using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
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
/// languageVersion 2.0 templates (<see cref="TemplateDocument.UsesSymbolicNames"/>) declare <c>resources</c> as an object keyed by symbolic
/// name and may declare user-defined types in <c>definitions</c>, referenced with <c>$ref</c> (see <see cref="TypeReferences"/>).
/// </summary>
public sealed class TemplateLoader : ITemplateLoader
{
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

        // Multi-line strings (a raw line break inside a string), which Azure CLI and PowerShell accept in ARM templates, are escaped before parsing;
        // locations are mapped back to the text as written.
        var original = TemplateJson.StripBom(utf8);
        var parsed = MultiLineStrings.Escape(original, out var insertions);

        JsonElement element;
        JsonNode? node;
        try
        {
            (element, node) = TemplateJson.Parse(parsed, DuplicateKeys.TemplateNameMaps);
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

        var context = new LoadContext(sourcePath, insertions.Count == 0 ? SourceMap.Build(utf8) : SourceMap.Build(parsed, original, insertions));
        foreach (var escape in loneSurrogates)
        {
            context.Diagnostics.Add(Diagnostic.Error(
                DiagnosticCodes.InvalidJson,
                $"The template contains an invalid Unicode escape (lone surrogate) at {escape.Path}; a surrogate must be written as a pair (high then low), or use the character itself.",
                escape.Path,
                escape.Location,
                sourcePath));
        }

        return Build(root, element, context, utf8.Length, parameters);
    }

    /// <summary>
    /// Loads the inline <c>template</c> object of a nested deployment as a document of its own (<see cref="TemplateDocument.IsNested"/>). Its paths are
    /// relative to the object; <paramref name="sourceMap"/> (the file's map rebased on the object, <see cref="SourceMap.Rebase"/>) keeps the
    /// locations those of <paramref name="sourcePath"/>. The node tree is shared with the parent template and must not be mutated.
    /// </summary>
    public TemplateDocument LoadNested(JsonObject root, string? sourcePath, SourceMap sourceMap, ParameterSet parameters)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(sourceMap);
        ArgumentNullException.ThrowIfNull(parameters);

        string text = root.ToJsonString();
        using var parsed = JsonDocument.Parse(text);
        var element = parsed.RootElement.Clone();
        long length = Encoding.UTF8.GetByteCount(text);
        return Build(root, element, new LoadContext(sourcePath, sourceMap), length, parameters) with { IsNested = true };
    }

    private TemplateDocument Build(JsonObject root, JsonElement element, LoadContext context, long byteLength, ParameterSet parameters)
    {
        string? sourcePath = context.SourcePath;
        bool symbolic = LanguageVersions.IsVersion2(root, context);
        var definitions = ReadDefinitions(root);
        var parameterDefinitions = ReadParameters(root, definitions, context);
        var (variables, iterations) = VariableReader.Read(root, context);
        var functions = ReadFunctions(root, definitions);
        var resources = new ResourceReader(_catalog, context).Read(root, symbolic);
        var outputs = ReadOutputs(root, definitions, context);
        var bound = ParameterBinder.BindParameters(parameterDefinitions, parameters, context);

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
            SourceByteLength = byteLength,
            Schema = TemplateJson.AsString(root["$schema"]),
            ContentVersion = TemplateJson.AsString(root["contentVersion"]),
            Metadata = root["metadata"] as JsonObject,
            LanguageVersion = TemplateJson.AsString(root["languageVersion"]),
            UsesSymbolicNames = symbolic,
            Definitions = definitions,
            Parameters = parameterDefinitions,
            Variables = variables,
            VariableIterations = iterations,
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

    // ---- definitions and parameters ----

    private static Dictionary<string, JsonObject> ReadDefinitions(JsonObject root)
    {
        var definitions = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (root["definitions"] is JsonObject source)
        {
            foreach (var (name, value) in source)
            {
                if (value is JsonObject definition)
                {
                    definitions[name] = definition;
                }
            }
        }

        return definitions;
    }

    private static Dictionary<string, ParameterDefinition> ReadParameters(JsonObject root, IReadOnlyDictionary<string, JsonObject> types, LoadContext context)
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

            var schema = TypeReferences.Resolve(definition, types, out var problem);
            var effective = schema ?? definition;
            definitions[entry.Key] = new ParameterDefinition
            {
                Name = entry.Key,
                Type = TemplateJson.AsString(effective["type"]),
                TypeSchema = schema,
                TypeProblem = problem,
                TypeDefinitions = types,
                DefaultValue = definition["defaultValue"],
                HasDefaultValue = definition.ContainsKey("defaultValue"),
                AllowedValues = effective["allowedValues"] as JsonArray,
                MinValue = effective["minValue"],
                MaxValue = effective["maxValue"],
                MinLength = effective["minLength"],
                MaxLength = effective["maxLength"],
                Metadata = definition["metadata"] as JsonObject,
                Source = definition,
                Path = path,
                Location = context.SourceMap.FindNearest(path),
            };
        }

        return definitions;
    }

    private static List<UserFunctionNamespace> ReadFunctions(JsonObject root, IReadOnlyDictionary<string, JsonObject> types)
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
                                parameterList.Add(new UserFunctionParameter(TemplateJson.AsString(p["name"]), TypeName(p, types)));
                            }
                        }
                    }

                    var output = definition["output"] as JsonObject;
                    members[member.Key] = new UserFunctionDefinition
                    {
                        Name = member.Key,
                        Parameters = parameterList,
                        OutputType = output is null ? null : TypeName(output, types),
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

    /// <summary>The type text of a node, following its <c>$ref</c>; <c>null</c> when it has none or the reference is broken (reported by the semantic checks).</summary>
    private static string? TypeName(JsonObject node, IReadOnlyDictionary<string, JsonObject> types)
    {
        return TypeReferences.Resolve(node, types, out _) is { } schema ? TemplateJson.AsString(schema["type"]) : null;
    }

    private static Dictionary<string, OutputDefinition> ReadOutputs(JsonObject root, IReadOnlyDictionary<string, JsonObject> types, LoadContext context)
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

            var schema = TypeReferences.Resolve(definition, types, out var problem);
            outputs[entry.Key] = new OutputDefinition
            {
                Name = entry.Key,
                Type = TemplateJson.AsString((schema ?? definition)["type"]),
                TypeSchema = schema,
                TypeProblem = problem,
                TypeDefinitions = types,
                Value = definition["value"],
                Condition = definition["condition"],
                Copy = definition["copy"] is JsonObject copy ? new OutputCopy { Count = copy["count"], Input = copy["input"], Source = copy, Path = path.Append("copy") } : null,
                Metadata = definition["metadata"] as JsonObject,
                Source = definition,
                Path = path,
                Location = context.SourceMap.FindNearest(path),
            };
        }

        return outputs;
    }
}
