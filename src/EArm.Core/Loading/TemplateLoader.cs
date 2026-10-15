using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Secrets;
using EArm.Core.Types;

namespace EArm.Core.Loading;

/// <summary>
/// Default <see cref="ITemplateLoader"/>. See the interface for the contract; the flattening rules are:
/// <list type="bullet">
/// <item>A nested child's full type is <c>&lt;parent type&gt;/&lt;short type&gt;</c>; a nested child may also spell
/// its full type, which must then start with the parent's type.</item>
/// <item>A nested child's name is <see cref="ExpressionSyntax.ComposeName"/> of the parent's name and its own.</item>
/// <item>The parent dependency is recorded in <see cref="ResourceDeclaration.InferredDependsOn"/> as a
/// <c>[resourceId(...)]</c> expression.</item>
/// <item>Nesting deeper than two levels is rejected with <see cref="DiagnosticCodes.NestingTooDeep"/>; the deeper
/// resources are ignored.</item>
/// </list>
/// </summary>
public sealed class TemplateLoader : ITemplateLoader
{
    private const int MaxNestingDepth = 2;

    private readonly ResourceTypeCatalog _catalog;

    public TemplateLoader()
        : this(ResourceTypeCatalog.Default)
    {
    }

    public TemplateLoader(ResourceTypeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
    }

    public async Task<TemplateDocument> LoadAsync(string path, ParameterSet parameters, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(parameters);

        byte[] bytes = await InputFiles.ReadAllBytesAsync(path, "Template", ct).ConfigureAwait(false);
        return Parse(bytes, path, parameters);
    }

    public TemplateDocument Parse(string json, string? sourcePath, ParameterSet parameters)
    {
        ArgumentNullException.ThrowIfNull(json);
        return Parse(Encoding.UTF8.GetBytes(json), sourcePath, parameters);
    }

    public TemplateDocument Parse(ReadOnlyMemory<byte> utf8, string? sourcePath, ParameterSet parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        // A lone surrogate escape ("\ud800") parses but throws on the first read of the string; it is rewritten to U+FFFD here
        // (same length, so positions hold) and reported as an error below, once the load context exists.
        utf8 = LoneSurrogates.Replace(utf8, out var loneSurrogates);

        JsonElement element;
        JsonNode? node;
        try
        {
            (element, node) = TemplateJson.Parse(utf8, DuplicateKeys.TemplateNameMaps);
        }
        catch (DuplicateJsonKeyException ex)
        {
            throw new TemplateLoadException(DiagnosticCodes.DuplicateJsonKey, ex.Message, sourcePath, ex.First.Location, ex.First.Path, inner: ex);
        }
        catch (InputTooLargeException ex)
        {
            throw new TemplateLoadException(DiagnosticCodes.InputTooLarge, $"Template is too large: {ex.Message}", sourcePath, inner: ex);
        }
        catch (JsonException ex)
        {
            throw new TemplateLoadException(DiagnosticCodes.InvalidJson, $"Template is not valid JSON: {ex.Message}", sourcePath, TemplateJson.LocationOf(ex), inner: ex);
        }

        if (node is not JsonObject root)
        {
            throw new TemplateLoadException(DiagnosticCodes.TemplateRootNotObject, "The template root must be a JSON object.", sourcePath, path: JsonPath.Root);
        }

        var context = new LoadContext(sourcePath, SourceMap.Build(utf8));
        foreach (var escape in loneSurrogates)
        {
            context.Diagnostics.Add(Diagnostic.Error(
                DiagnosticCodes.InvalidJson,
                $"The template contains an invalid Unicode escape (lone surrogate) at {escape.Path}; a surrogate must be written as a pair (high then low), or use the character itself.",
                escape.Path,
                escape.Location,
                sourcePath));
        }

        var parameterDefinitions = ReadParameters(root, context);
        var variables = ReadVariables(root, context);
        var functions = ReadFunctions(root);
        var resources = ReadResources(root, context);
        var outputs = ReadOutputs(root, context);
        var bound = BindParameters(parameterDefinitions, parameters, context);

        var topLevelKeys = new List<string>();
        foreach (var property in root)
        {
            topLevelKeys.Add(property.Key);
        }

        return new TemplateDocument
        {
            Root = root,
            RootElement = element,
            SourcePath = sourcePath,
            Schema = TemplateJson.AsString(root["$schema"]),
            ContentVersion = TemplateJson.AsString(root["contentVersion"]),
            Metadata = root["metadata"] as JsonObject,
            Parameters = parameterDefinitions,
            Variables = variables,
            Functions = functions,
            Resources = resources,
            Outputs = outputs,
            ParameterValues = bound,
            SuppliedParameters = parameters,
            LoadDiagnostics = context.Diagnostics,
            SourceMap = context.SourceMap,
            TopLevelKeys = topLevelKeys,
        };
    }

    // ---- parameters ----

    private static Dictionary<string, ParameterDefinition> ReadParameters(JsonObject root, LoadContext context)
    {
        var definitions = new Dictionary<string, ParameterDefinition>(StringComparer.OrdinalIgnoreCase);
        if (root["parameters"] is not JsonObject parameters)
        {
            return definitions;
        }

        var basePath = JsonPath.Of("parameters");
        foreach (var entry in parameters)
        {
            var path = basePath.Append(entry.Key);
            if (entry.Value is not JsonObject definition)
            {
                // Shape errors are reported by the JSON Schema.
                continue;
            }

            if (definitions.ContainsKey(entry.Key))
            {
                context.Error(DiagnosticCodes.DuplicateDefinition, $"Parameter '{entry.Key}' is defined more than once (parameter names are case-insensitive).", path);
                continue;
            }

            definitions[entry.Key] = new ParameterDefinition
            {
                Name = entry.Key,
                Type = TemplateJson.AsString(definition["type"]),
                DefaultValue = definition["defaultValue"],
                HasDefaultValue = definition.ContainsKey("defaultValue"),
                AllowedValues = definition["allowedValues"] as JsonArray,
                MinValue = definition["minValue"],
                MaxValue = definition["maxValue"],
                MinLength = definition["minLength"],
                MaxLength = definition["maxLength"],
                Metadata = definition["metadata"] as JsonObject,
                Source = definition,
                Path = path,
                Location = context.SourceMap.FindNearest(path),
            };
        }

        return definitions;
    }

    private static Dictionary<string, JsonNode?> ReadVariables(JsonObject root, LoadContext context)
    {
        var variables = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        if (root["variables"] is not JsonObject source)
        {
            return variables;
        }

        var basePath = JsonPath.Of("variables");
        foreach (var entry in source)
        {
            if (!variables.TryAdd(entry.Key, entry.Value))
            {
                context.Error(DiagnosticCodes.DuplicateDefinition, $"Variable '{entry.Key}' is defined more than once (variable names are case-insensitive).", basePath.Append(entry.Key));
            }
        }

        return variables;
    }

    private static List<UserFunctionNamespace> ReadFunctions(JsonObject root)
    {
        var namespaces = new List<UserFunctionNamespace>();
        if (root["functions"] is not JsonArray functions)
        {
            return namespaces;
        }

        var basePath = JsonPath.Of("functions");
        for (int i = 0; i < functions.Count; i++)
        {
            if (functions[i] is not JsonObject ns)
            {
                continue;
            }

            var nsPath = basePath.Append(i);
            var members = new Dictionary<string, UserFunctionDefinition>(StringComparer.OrdinalIgnoreCase);
            if (ns["members"] is JsonObject memberObjects)
            {
                foreach (var member in memberObjects)
                {
                    if (member.Value is not JsonObject definition)
                    {
                        continue;
                    }

                    var memberPath = nsPath.Append("members").Append(member.Key);
                    var parameterList = new List<UserFunctionParameter>();
                    if (definition["parameters"] is JsonArray parameters)
                    {
                        foreach (var parameter in parameters)
                        {
                            if (parameter is JsonObject p)
                            {
                                parameterList.Add(new UserFunctionParameter(TemplateJson.AsString(p["name"]), TemplateJson.AsString(p["type"])));
                            }
                        }
                    }

                    var output = definition["output"] as JsonObject;
                    members[member.Key] = new UserFunctionDefinition
                    {
                        Name = member.Key,
                        Parameters = parameterList,
                        OutputType = output is null ? null : TemplateJson.AsString(output["type"]),
                        OutputValue = output?["value"],
                        Source = definition,
                        Path = memberPath,
                    };
                }
            }

            namespaces.Add(new UserFunctionNamespace
            {
                Namespace = TemplateJson.AsString(ns["namespace"]),
                Members = members,
                Source = ns,
                Path = nsPath,
            });
        }

        return namespaces;
    }

    private static Dictionary<string, OutputDefinition> ReadOutputs(JsonObject root, LoadContext context)
    {
        var outputs = new Dictionary<string, OutputDefinition>(StringComparer.OrdinalIgnoreCase);
        if (root["outputs"] is not JsonObject source)
        {
            return outputs;
        }

        var basePath = JsonPath.Of("outputs");
        foreach (var entry in source)
        {
            var path = basePath.Append(entry.Key);
            if (entry.Value is not JsonObject definition)
            {
                continue;
            }

            if (outputs.ContainsKey(entry.Key))
            {
                context.Error(DiagnosticCodes.DuplicateDefinition, $"Output '{entry.Key}' is defined more than once (output names are case-insensitive).", path);
                continue;
            }

            outputs[entry.Key] = new OutputDefinition
            {
                Name = entry.Key,
                Type = TemplateJson.AsString(definition["type"]),
                Value = definition["value"],
                Condition = definition["condition"],
                Metadata = definition["metadata"] as JsonObject,
                Source = definition,
                Path = path,
                Location = context.SourceMap.FindNearest(path),
            };
        }

        return outputs;
    }

    // ---- resources ----

    private List<ResourceDeclaration> ReadResources(JsonObject root, LoadContext context)
    {
        var declarations = new List<ResourceDeclaration>();
        if (root["resources"] is not JsonArray resources)
        {
            return declarations;
        }

        var basePath = JsonPath.Of("resources");
        for (int i = 0; i < resources.Count; i++)
        {
            if (resources[i] is JsonObject resource)
            {
                Visit(resource, basePath.Append(i), parent: null, depth: 1, declarations, context);
            }
        }

        return declarations;
    }

    private void Visit(JsonObject resource, JsonPath path, ResourceDeclaration? parent, int depth, List<ResourceDeclaration> declarations, LoadContext context)
    {
        string? declaredType = TemplateJson.AsString(resource["type"]);
        string? declaredName = TemplateJson.AsString(resource["name"]);

        string? fullType = declaredType is null ? null : ResourceIdentifier.NormalizeType(declaredType);
        string? name = declaredName;
        var inferred = new List<string>();

        if (parent is not null)
        {
            if (fullType is not null && parent.Type is not null)
            {
                if (fullType.StartsWith(ResourceIdentifier.ProviderNamespace + "/", StringComparison.OrdinalIgnoreCase))
                {
                    // Full type spelled inside a nested declaration: it must sit under the parent's type.
                    if (!fullType.StartsWith(parent.Type + "/", StringComparison.OrdinalIgnoreCase))
                    {
                        context.Error(
                            DiagnosticCodes.ParentChildTypeMismatch,
                            $"Nested resource type '{declaredType}' is not a child type of its parent '{parent.Type}'.",
                            path.Append("type"));
                    }
                }
                else
                {
                    fullType = parent.Type + "/" + fullType.Trim('/');
                }
            }

            if (parent.Name is not null && declaredName is not null)
            {
                name = ExpressionSyntax.ComposeName(parent.Name, declaredName);
            }
            else
            {
                name = null;
            }

            if (parent.Type is not null && parent.Name is not null)
            {
                inferred.Add(ExpressionSyntax.ResourceIdExpression(parent.Type, parent.Name));
            }
        }

        if (fullType is not null)
        {
            fullType = _catalog.CanonicalType(fullType);
        }

        var dependsOn = new List<string>();
        if (resource["dependsOn"] is JsonArray dependsOnArray)
        {
            foreach (var item in dependsOnArray)
            {
                if (TemplateJson.AsString(item) is { } text)
                {
                    dependsOn.Add(text);
                }
            }
        }

        CopyDeclaration? copy = null;
        if (resource["copy"] is JsonObject copyObject)
        {
            copy = new CopyDeclaration
            {
                Name = copyObject["name"],
                Count = copyObject["count"],
                Mode = copyObject["mode"],
                BatchSize = copyObject["batchSize"],
                Source = copyObject,
                Path = path.Append("copy"),
            };
        }

        var declaration = new ResourceDeclaration
        {
            Type = fullType,
            DeclaredType = declaredType,
            ApiVersion = TemplateJson.AsString(resource["apiVersion"]),
            Name = name,
            DeclaredName = declaredName,
            StaticId = ComputeStaticId(fullType, name),
            Properties = resource["properties"],
            DependsOn = dependsOn,
            InferredDependsOn = inferred,
            Condition = resource["condition"],
            Copy = copy,
            Comments = TemplateJson.AsString(resource["comments"]),
            Metadata = resource["metadata"] as JsonObject,
            Source = resource,
            Path = path,
            Location = context.SourceMap.FindNearest(path),
            NestingDepth = depth,
            NestedParentType = parent?.Type,
            NestedParentPath = parent?.Path,
        };
        declarations.Add(declaration);

        if (resource["resources"] is not JsonArray children || children.Count == 0)
        {
            return;
        }

        var childrenPath = path.Append("resources");
        if (depth >= MaxNestingDepth)
        {
            context.Error(
                DiagnosticCodes.NestingTooDeep,
                $"Resources nested more than {MaxNestingDepth} levels deep are not supported; declare '{declaration.Describe()}' children at the top level with a 'parent/child' name.",
                childrenPath);
            return;
        }

        for (int i = 0; i < children.Count; i++)
        {
            if (children[i] is JsonObject child)
            {
                Visit(child, childrenPath.Append(i), declaration, depth + 1, declarations, context);
            }
        }
    }

    private ResourceIdentifier? ComputeStaticId(string? type, string? name)
    {
        if (type is null || name is null || ExpressionSyntax.IsExpression(name))
        {
            return null;
        }

        if (!_catalog.TryGet(type, out var definition))
        {
            return null;
        }

        var segments = ExpressionSyntax.LiteralValue(name).Split('/');
        if (segments.Length != definition.Depth)
        {
            return null;
        }

        return ResourceIdentifier.TryCreate(definition.Type, string.Join('/', segments), out var identifier) ? identifier : null;
    }

    // ---- parameter binding ----

    private static Dictionary<string, BoundParameter> BindParameters(Dictionary<string, ParameterDefinition> definitions, ParameterSet supplied, LoadContext context)
    {
        var bound = new Dictionary<string, BoundParameter>(StringComparer.OrdinalIgnoreCase);

        foreach (var definition in definitions.Values)
        {
            if (supplied.Values.TryGetValue(definition.Name, out var value))
            {
                bound[definition.Name] = Bind(definition, value, context);
                continue;
            }

            if (definition.HasDefaultValue)
            {
                JsonNode? defaultValue = definition.DefaultValue;
                if (definition.IsSecure && !TemplateJson.IsExpression(defaultValue))
                {
                    defaultValue = SecretJson.Taint(defaultValue);
                }

                bound[definition.Name] = new BoundParameter { Name = definition.Name, Definition = definition, Value = defaultValue, Source = ParameterValueSource.Default };
                continue;
            }

            bound[definition.Name] = new BoundParameter { Name = definition.Name, Definition = definition, Source = ParameterValueSource.Default, IsMissing = true };
        }

        foreach (var pair in supplied.Values)
        {
            if (definitions.ContainsKey(pair.Key))
            {
                continue;
            }

            string origin = pair.Value.IsInline ? "inline parameter (-p)" : "parameter file";
            context.Diagnostics.Add(Diagnostic.Error(
                DiagnosticCodes.UnknownParameter,
                $"The {origin} supplies a value for '{pair.Key}', but the template declares no such parameter.",
                pair.Value.IsInline ? null : JsonPath.Of("parameters", pair.Key),
                null,
                pair.Value.SourcePath));
        }

        return bound;
    }

    private static BoundParameter Bind(ParameterDefinition definition, ParameterValue supplied, LoadContext context)
    {
        if (supplied.Reference is not null)
        {
            return new BoundParameter
            {
                Name = definition.Name,
                Definition = definition,
                PendingReference = supplied.Reference,
                Source = supplied.Source,
            };
        }

        JsonNode? value;
        bool conversionFailed = false;
        if (supplied.IsInline && supplied.RawText is not null)
        {
            conversionFailed = !TryConvertInline(definition, supplied.RawText, context, out value);
        }
        else
        {
            value = supplied.Value?.DeepClone();
        }

        if (definition.IsSecure)
        {
            value = SecretJson.Taint(value);
        }

        return new BoundParameter
        {
            Name = definition.Name,
            Definition = definition,
            Value = value,
            Source = supplied.Source,
            ConversionFailed = conversionFailed,
        };
    }

    /// <summary>
    /// Converts the raw text of <c>-p name=value</c> to the declared type: strings are taken verbatim, <c>int</c> and
    /// <c>bool</c> are parsed, <c>object</c>/<c>array</c> are parsed as JSON. Unknown types keep the text.
    /// Returns <c>false</c> (with the raw text as <paramref name="value"/>) when the conversion fails; the
    /// <c>ParameterValueTypeMismatch</c> error is reported here, once, with the definition's location.
    /// </summary>
    private static bool TryConvertInline(ParameterDefinition definition, string rawText, LoadContext context, out JsonNode? value)
    {
        switch (definition.ParsedType)
        {
            case TemplateParameterType.Int:
                if (long.TryParse(rawText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
                {
                    value = JsonValue.Create(l);
                    return true;
                }

                break;

            case TemplateParameterType.Bool:
                if (bool.TryParse(rawText.Trim(), out bool b))
                {
                    value = JsonValue.Create(b);
                    return true;
                }

                break;

            case TemplateParameterType.Object:
            case TemplateParameterType.SecureObject:
            case TemplateParameterType.Array:
                try
                {
                    string jsonText = LoneSurrogates.Replace(rawText);
                    var duplicates = DuplicateKeys.Find(Encoding.UTF8.GetBytes(jsonText), limit: 1);
                    if (duplicates.Count > 0)
                    {
                        context.Diagnostics.Add(Diagnostic.Error(
                            DiagnosticCodes.DuplicateJsonKey,
                            $"The inline value for parameter '{definition.Name}' is not accepted: {duplicates[0].Describe()}",
                            definition.Path,
                            definition.Location,
                            context.SourcePath));
                        value = JsonValue.Create(rawText);
                        return false;
                    }

                    var parsed = JsonNode.Parse(jsonText, documentOptions: TemplateJson.DocumentOptions);
                    if (TemplateParameterTypes.Matches(parsed, definition.ParsedType.Value))
                    {
                        value = parsed;
                        return true;
                    }
                }
                catch (JsonException)
                {
                    // Reported below.
                }

                break;

            default:
                value = JsonValue.Create(rawText);
                return true;
        }

        context.Diagnostics.Add(Diagnostic.Error(
            DiagnosticCodes.ParameterValueTypeMismatch,
            $"The inline value for parameter '{definition.Name}' cannot be converted to type '{definition.Type}'.",
            definition.Path,
            definition.Location,
            context.SourcePath));
        value = JsonValue.Create(rawText);
        return false;
    }

    private sealed class LoadContext
    {
        public LoadContext(string? sourcePath, SourceMap sourceMap)
        {
            SourcePath = sourcePath;
            SourceMap = sourceMap;
        }

        public string? SourcePath { get; }

        public SourceMap SourceMap { get; }

        public List<Diagnostic> Diagnostics { get; } = [];

        public void Error(string code, string message, JsonPath path)
        {
            Diagnostics.Add(Diagnostic.Error(code, message, path, SourceMap.FindNearest(path), SourcePath));
        }
    }
}
