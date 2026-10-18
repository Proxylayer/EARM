using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core.Json;

namespace EArm.Core.Types;

/// <summary>
/// The registry of resource types EARM understands, read from the <c>x-earm</c> overlay of the embedded
/// per-type JSON Schemas. Shared by the validator (type / apiVersion / name-shape checks), the engine and the
/// providers, so the schema files are the single source of truth for per-type behaviour.
/// </summary>
public sealed class ResourceTypeCatalog
{
    public const string SchemaVersion = EmbeddedSchemas.SchemaVersion;
    public const string SchemaBaseUri = "https://schema.earm.proxylayer.net/schemas/" + SchemaVersion + "/";
    public const string TemplateSchemaUri = SchemaBaseUri + "deploymentTemplate.json";
    public const string ParametersSchemaUri = SchemaBaseUri + "deploymentParameters.json";

    private static readonly Lazy<ResourceTypeCatalog> DefaultInstance = new(LoadEmbedded);

    private readonly Dictionary<string, ResourceTypeDefinition> _byType;
    private readonly List<ResourceTypeDefinition> _ordered;

    private ResourceTypeCatalog(IEnumerable<ResourceTypeDefinition> definitions)
    {
        _byType = new Dictionary<string, ResourceTypeDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions)
        {
            if (!_byType.TryAdd(definition.Type, definition))
            {
                throw new InvalidOperationException($"Resource type '{definition.Type}' is defined twice.");
            }
        }

        // Depth = number of ancestors + 1, resolved against the catalog (parents must be known).
        foreach (var key in _byType.Keys.ToArray())
        {
            _byType[key] = _byType[key] with { Depth = ComputeDepth(_byType[key], _byType) };
        }

        _ordered = _byType.Values.OrderBy(d => d.Type, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The catalog built from the schemas embedded in this assembly.</summary>
    public static ResourceTypeCatalog Default => DefaultInstance.Value;

    /// <summary>The <c>$schema</c> values a template may declare (with or without the trailing <c>#</c>).</summary>
    public static IReadOnlyList<string> KnownTemplateSchemas { get; } = [TemplateSchemaUri, TemplateSchemaUri + "#"];

    public static IReadOnlyList<string> KnownParameterSchemas { get; } = [ParametersSchemaUri, ParametersSchemaUri + "#"];

    /// <summary>All types, sorted by name.</summary>
    public IReadOnlyList<ResourceTypeDefinition> Types => _ordered;

    public static ResourceTypeCatalog LoadEmbedded()
    {
        var definitions = new List<ResourceTypeDefinition>();
        foreach (var name in EmbeddedSchemas.NamesUnder(EmbeddedSchemas.TypeSchemaPrefix))
        {
            definitions.Add(ParseOverlay(EmbeddedSchemas.ReadText(name), name));
        }

        return new ResourceTypeCatalog(definitions);
    }

    /// <summary>Builds a catalog from explicit definitions (tests, future provider packs).</summary>
    public static ResourceTypeCatalog FromDefinitions(IEnumerable<ResourceTypeDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        return new ResourceTypeCatalog(definitions);
    }

    public static bool IsTemplateSchemaRecognized(string? schema)
    {
        return schema is not null && KnownTemplateSchemas.Any(s => s.Equals(schema.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// True for ARM's own template schema (<c>https://schema.management.azure.com/schemas/&lt;date&gt;/deploymentTemplate.json#</c>), which the template of a
    /// nested or linked deployment may declare (ARM snippets paste in); the top-level template must declare EARM's.
    /// </summary>
    public static bool IsArmTemplateSchema(string? schema)
    {
        if (schema is null)
        {
            return false;
        }

        string text = schema.Trim().TrimEnd('#');
        return text.StartsWith("https://schema.management.azure.com/schemas/", StringComparison.OrdinalIgnoreCase)
            && text.EndsWith("/deploymentTemplate.json", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The definition of <paramref name="type"/>; template-level types (<see cref="DeploymentResourceType"/>) resolve too.</summary>
    public bool TryGet(string? type, out ResourceTypeDefinition definition)
    {
        definition = null!;
        if (type is null)
        {
            return false;
        }

        if (_byType.TryGetValue(ResourceIdentifier.NormalizeType(type), out definition!))
        {
            return true;
        }

        if (DeploymentResourceType.Is(type))
        {
            definition = DeploymentResourceType.Definition;
            return true;
        }

        return false;
    }

    public ResourceTypeDefinition? Find(string? type)
    {
        return TryGet(type, out var definition) ? definition : null;
    }

    public bool IsKnownType(string? type)
    {
        return TryGet(type, out _);
    }

    /// <summary>The canonical casing of a known type; unknown types are returned normalised but otherwise as written.</summary>
    public string CanonicalType(string type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return TryGet(type, out var definition) ? definition.Type : ResourceIdentifier.NormalizeType(type);
    }

    public bool TryGetGraphApi(string? type, string? apiVersion, out GraphApi api)
    {
        api = GraphApi.V1;
        return TryGet(type, out var definition) && definition.TryGetGraphApi(apiVersion, out api);
    }

    /// <summary>Child types whose parent is <paramref name="parentType"/>.</summary>
    public IReadOnlyList<ResourceTypeDefinition> ChildrenOf(string parentType)
    {
        ArgumentNullException.ThrowIfNull(parentType);
        return _ordered.Where(d => d.ParentType is not null && ResourceIdentifier.TypeEquals(d.ParentType, parentType)).ToList();
    }

    /// <summary>Types whose last segment is <paramref name="leaf"/> (used to explain parent/child mismatches).</summary>
    public IReadOnlyList<ResourceTypeDefinition> FindByLeaf(string leaf)
    {
        ArgumentNullException.ThrowIfNull(leaf);
        return _ordered.Where(d => d.TypeLeaf.Equals(leaf, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// Parses an ARM-style resource id (<c>/providers/Microsoft.Entra/applications/app/federatedIdentityCredentials/fic</c>)
    /// using the catalog to tell type segments from name segments.
    /// </summary>
    public bool TryParseResourceId(string? id, out ResourceIdentifier? identifier)
    {
        identifier = null;
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        string text = id.Trim();
        if (!text.StartsWith(ResourceIdentifier.ProvidersPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // A nested deployment: /providers/Microsoft.Resources/deployments/<name>.
        string deploymentPrefix = ResourceIdentifier.ProvidersPrefix + DeploymentResourceType.Type + "/";
        if (text.StartsWith(deploymentPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string deploymentName = text[deploymentPrefix.Length..];
            if (deploymentName.Length == 0 || deploymentName.Contains('/'))
            {
                return false;
            }

            identifier = new ResourceIdentifier(DeploymentResourceType.Type, [deploymentName]);
            return true;
        }

        var segments = text[ResourceIdentifier.ProvidersPrefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3 || !segments[0].Equals(ResourceIdentifier.ProviderNamespace, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Walk: at each level consume the longest known type prefix, then one name segment.
        var typeSegments = new List<string>();
        var names = new List<string>();
        int i = 1;
        while (i < segments.Length)
        {
            bool matched = false;
            for (int take = segments.Length - i - 1; take >= 1; take--)
            {
                var candidate = ResourceIdentifier.ProviderNamespace + "/" + string.Join('/', typeSegments.Concat(segments.Skip(i).Take(take)));
                if (_byType.TryGetValue(candidate, out var definition) && definition.Depth == names.Count + 1)
                {
                    typeSegments.AddRange(segments.Skip(i).Take(take));
                    names.Add(segments[i + take]);
                    i += take + 1;
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                return false;
            }
        }

        var type = ResourceIdentifier.ProviderNamespace + "/" + string.Join('/', typeSegments);
        if (!_byType.TryGetValue(type, out var final) || final.Depth != names.Count)
        {
            return false;
        }

        identifier = new ResourceIdentifier(final.Type, names);
        return true;
    }

    private static int ComputeDepth(ResourceTypeDefinition definition, Dictionary<string, ResourceTypeDefinition> all)
    {
        int depth = 1;
        var current = definition;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { definition.Type };
        while (current.ParentType is not null)
        {
            if (!all.TryGetValue(ResourceIdentifier.NormalizeType(current.ParentType), out var parent))
            {
                throw new InvalidOperationException($"Resource type '{definition.Type}' declares unknown parent type '{current.ParentType}'.");
            }

            if (!seen.Add(parent.Type))
            {
                throw new InvalidOperationException($"Resource type '{definition.Type}' has a cyclic parent chain.");
            }

            depth++;
            current = parent;
        }

        return depth;
    }

    /// <summary>Parses one per-type schema file and its <c>x-earm</c> overlay.</summary>
    public static ResourceTypeDefinition ParseOverlay(string schemaJson, string resourceName)
    {
        ArgumentNullException.ThrowIfNull(schemaJson);
        ArgumentNullException.ThrowIfNull(resourceName);

        var root = JsonNode.Parse(schemaJson, documentOptions: TemplateJson.DocumentOptions) as JsonObject
            ?? throw new InvalidOperationException($"Type schema '{resourceName}' is not a JSON object.");
        var overlay = root["x-earm"] as JsonObject
            ?? throw new InvalidOperationException($"Type schema '{resourceName}' has no 'x-earm' overlay.");

        string type = RequiredString(overlay, "type", resourceName);
        string graphPath = RequiredString(overlay, "graphPath", resourceName);

        var apiVersions = new Dictionary<string, GraphApi>(StringComparer.OrdinalIgnoreCase);
        if (overlay["apiVersions"] is JsonObject versions)
        {
            foreach (var pair in versions)
            {
                if (!GraphApis.TryParse(TemplateJson.AsString(pair.Value), out var api))
                {
                    throw new InvalidOperationException($"Type schema '{resourceName}': apiVersion '{pair.Key}' maps to unknown Graph API '{pair.Value}'.");
                }

                apiVersions[pair.Key] = api;
            }
        }

        if (apiVersions.Count == 0)
        {
            throw new InvalidOperationException($"Type schema '{resourceName}' declares no apiVersions.");
        }

        if (!ResourceNameKinds.TryParse(TemplateJson.AsString(overlay["nameKind"]), out var nameKind))
        {
            throw new InvalidOperationException($"Type schema '{resourceName}' has a missing or unknown 'nameKind'.");
        }

        var immutable = StringList(overlay["immutable"]);
        bool immutableAll = immutable.Contains("*");

        return new ResourceTypeDefinition
        {
            Type = ResourceIdentifier.NormalizeType(type),
            ParentType = TemplateJson.AsString(overlay["parentType"]) is { } parent ? ResourceIdentifier.NormalizeType(parent) : null,
            Description = TemplateJson.AsString(root["description"]),
            GraphPath = graphPath,
            ApiVersions = apiVersions,
            NameMapsTo = TemplateJson.AsString(overlay["nameMapsTo"]),
            NameKind = nameKind,
            NamePattern = TemplateJson.AsString(overlay["namePattern"]),
            NameMaxLength = TemplateJson.AsInt64(overlay["nameMaxLength"]) is { } maxLength and > 0 and <= int.MaxValue ? (int)maxLength : null,
            KeyProperties = StringList(overlay["keyProperties"]),
            RequiredOnCreate = StringList(overlay["requiredOnCreate"]),
            Defaults = overlay["defaults"] as JsonObject,
            Immutable = immutable.Where(p => p != "*").Select(JsonPath.Parse).ToList(),
            ImmutableAll = immutableAll,
            CreateOnly = StringList(overlay["createOnly"]).Select(JsonPath.Parse).ToList(),
            Secure = StringList(overlay["secure"]).Select(JsonPath.Parse).ToList(),
            Relationships = ParseRelationships(overlay["relationships"], resourceName),
            MemberKey = StringMap(overlay["memberKey"]),
            StripOnRead = StringList(overlay["stripOnRead"]).Select(JsonPath.Parse).ToList(),
            Nullable = BoolMap(overlay["nullable"]),
            Permissions = ParsePermissions(overlay["permissions"] as JsonObject),
            Schema = root,
            SchemaResourceName = resourceName,
        };
    }

    private static string RequiredString(JsonObject overlay, string key, string resourceName)
    {
        return TemplateJson.AsString(overlay[key])
            ?? throw new InvalidOperationException($"Type schema '{resourceName}' overlay is missing '{key}'.");
    }

    private static List<string> StringList(JsonNode? node)
    {
        var list = new List<string>();
        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (TemplateJson.AsString(item) is { } text)
                {
                    list.Add(text);
                }
            }
        }

        return list;
    }

    private static Dictionary<string, string> StringMap(JsonNode? node)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (node is JsonObject obj)
        {
            foreach (var pair in obj)
            {
                if (TemplateJson.AsString(pair.Value) is { } text)
                {
                    map[pair.Key] = text;
                }
            }
        }

        return map;
    }

    private static Dictionary<string, bool> BoolMap(JsonNode? node)
    {
        var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (node is JsonObject obj)
        {
            foreach (var pair in obj)
            {
                if (TemplateJson.AsBool(pair.Value) is { } flag)
                {
                    map[pair.Key] = flag;
                }
            }
        }

        return map;
    }

    private static List<RelationshipSpec> ParseRelationships(JsonNode? node, string resourceName)
    {
        var list = new List<RelationshipSpec>();
        if (node is not JsonArray array)
        {
            return list;
        }

        foreach (var item in array)
        {
            if (item is not JsonObject spec)
            {
                continue;
            }

            string property = TemplateJson.AsString(spec["property"])
                ?? throw new InvalidOperationException($"Type schema '{resourceName}': relationship entry without 'property'.");
            string kindText = TemplateJson.AsString(spec["kind"]) ?? string.Empty;
            string sideText = TemplateJson.AsString(spec["side"]) ?? string.Empty;
            if (!Enum.TryParse<RelationshipKind>(kindText, ignoreCase: true, out var kind))
            {
                throw new InvalidOperationException($"Type schema '{resourceName}': relationship '{property}' has unknown kind '{kindText}'.");
            }

            if (!Enum.TryParse<RelationshipSide>(sideText, ignoreCase: true, out var side))
            {
                throw new InvalidOperationException($"Type schema '{resourceName}': relationship '{property}' has unknown side '{sideText}'.");
            }

            list.Add(new RelationshipSpec(property, kind, side));
        }

        return list;
    }

    private static PermissionSet ParsePermissions(JsonObject? node)
    {
        if (node is null)
        {
            return PermissionSet.Empty;
        }

        return new PermissionSet(StringList(node["application"]), StringList(node["delegated"]), StringList(node["roles"]), node);
    }
}
