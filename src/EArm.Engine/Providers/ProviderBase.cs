using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Secrets;
using EArm.Core.Types;
using EArm.Expressions.Values;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Engine.Providers;

/// <summary>
/// The behaviour every provider shares, driven by the catalog overlay of its type: key resolution from the
/// ARM-style <c>name</c> (<c>nameMapsTo</c> / <c>nameKind</c>), injection of the name into the Graph body,
/// defaults, server-managed path stripping, the shared <see cref="JsonDiff"/>, and immutable-change detection
/// (error, or <see cref="ChangeMode.Recreate"/> when the overlay marks every property immutable with
/// <c>immutable: ["*"]</c>, in which case any effective delta replaces the object). A provider that overrides
/// <see cref="Plan"/> decides for itself (password credentials rotate through <see cref="ChangeMode.Patch"/> so
/// the new secret exists before the old one is removed). Providers implement the Graph calls.
/// </summary>
public abstract partial class ProviderBase : IResourceProvider, IExistingResourceLookup
{
    private readonly ResourceTypeCatalog _catalog;

    protected ProviderBase(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Definition = definition;
        _catalog = catalog ?? ResourceTypeCatalog.Default;

        var apiVersions = new Dictionary<string, GraphApi>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in definition.ApiVersions)
        {
            apiVersions[pair.Key] = GraphApiMapping.ToAz(pair.Value);
        }

        ApiVersions = apiVersions;
        ImmutableProperties = definition.Immutable.ToHashSet();
    }

    public ResourceTypeDefinition Definition { get; }

    public string TypeName => Definition.Type;

    public IReadOnlyDictionary<string, GraphApi> ApiVersions { get; }

    /// <summary>
    /// The overlay's explicitly listed immutable paths. Empty for an <c>immutable: ["*"]</c> type
    /// (<see cref="ResourceTypeDefinition.ImmutableAll"/>), where <see cref="Plan"/> treats every effective delta
    /// as an immutable change.
    /// </summary>
    public IReadOnlySet<JsonPath> ImmutableProperties { get; }

    public IReadOnlyList<RelationshipSpec> Relationships => Definition.Relationships;

    /// <summary>The catalog used to resolve parent types for child keys.</summary>
    protected ResourceTypeCatalog Catalog => _catalog;

    /// <summary>The overlay's application permissions; providers override for conditional permissions.</summary>
    public virtual IReadOnlyList<string> RequiredPermissions(ResourceInstance? desired)
    {
        return Definition.Permissions.Application;
    }

    /// <summary>
    /// Whether a changed immutable property replaces the object or is a hard error (applications, groups, users). Defaults to the overlay's
    /// <c>immutable: ["*"]</c>. In practice only a named location's kind (<c>@odata.type</c>) is replaced: the assignments and grants that opt in have no immutable
    /// property outside their key (a changed key is a new object, created), and password credentials plan their own rotation (a <c>Modify</c> that adds, then removes).
    /// </summary>
    protected virtual bool RecreateOnImmutableChange => Definition.ImmutableAll;

    /// <summary>The upserts (<c>uniqueName</c>-keyed applications and groups, authentication contexts by id, federated identity credentials: PATCH with <c>Prefer: create-if-missing</c>) can be repeated after an unknown outcome; every POST create cannot.</summary>
    public virtual bool CreateIsRepeatable => Definition.NameKind is ResourceNameKind.UniqueName or ResourceNameKind.Id
        || Definition.Type.EndsWith("/federatedIdentityCredentials", StringComparison.OrdinalIgnoreCase);

    /// <summary>The <c>displayName</c>-keyed types are found with a <c>$filter</c> list, which can miss a new object for a few seconds after its create.</summary>
    public virtual bool KeyReadLagsCreate => Definition.NameKind is ResourceNameKind.DisplayName;

    /// <summary>See <see cref="IResourceProvider.ClaimIdentity"/>: <c>{type}/{graphId}</c> unless the type's Graph id is only unique within a scope.</summary>
    public virtual string ClaimIdentity(ResourceKey key, string graphId)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(graphId);
        return TypeName + "/" + graphId;
    }

    // ---- Graph calls (per provider) ----

    /// <summary>See <see cref="IResourceProvider.PrepareAsync"/>: sugar resolution and live-data validation before the key; nothing by default.</summary>
    public virtual Task PrepareAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public abstract Task<ExistingResource?> ReadAsync(ResourceKey key, IGraphClient graph, CancellationToken cancellationToken);

    public abstract Task<AppliedResource> CreateAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken);

    public abstract Task<AppliedResource> UpdateAsync(ResourceInstance desired, ExistingResource existing, ResourceChange change, IGraphClient graph, CancellationToken cancellationToken);

    public abstract Task DeleteAsync(ResourceKey key, ExistingResource live, IGraphClient graph, CancellationToken cancellationToken);

    // ---- shared helpers for providers ----

    /// <summary>Removes the overlay's <c>stripOnRead</c> paths from a copy of the live object.</summary>
    public JsonObject StripServerManaged(JsonObject live)
    {
        ArgumentNullException.ThrowIfNull(live);
        var copy = (JsonObject)JsonValues.Clone(live)!;
        foreach (var path in Definition.StripOnRead)
        {
            JsonObjects.Remove(copy, path);
        }

        return copy;
    }

    /// <summary>Wraps a live object: its Graph id, the stripped diff view and the raw object for <c>reference()</c>.</summary>
    public ExistingResource ToExisting(JsonObject live)
    {
        ArgumentNullException.ThrowIfNull(live);
        string id = JsonObjects.GetId(live)
            ?? throw new ProviderException(EngineDiagnosticCodes.ReadFailed, $"The live {TypeName} object has no 'id'.");
        return new ExistingResource(id, StripServerManaged(live), (JsonObject)JsonValues.Clone(live)!);
    }

    /// <summary>
    /// The body of a create: the evaluated object properties with the ARM-style name injected into
    /// <c>nameMapsTo</c> (an equal declared value is tolerated, a different one is an error), the overlay defaults
    /// filled in, and secret leaves revealed (this is the only place, besides <c>--reveal-secure-outputs</c>, where
    /// a secret becomes plain text; the Graph transport never logs bodies).
    /// </summary>
    public JsonObject BuildCreateBody(ResourceInstance desired)
    {
        ArgumentNullException.ThrowIfNull(desired);
        var body = CloneProperties(desired);
        InjectName(body, desired);
        ApplyDefaults(body);
        return Reveal(body);
    }

    /// <summary>The body of an update: the effective deltas as a merge-PATCH (see <see cref="JsonDiff.BuildPatchBody"/>), secrets revealed.</summary>
    public JsonObject BuildPatchBody(ResourceInstance desired, ExistingResource existing, ResourceChange change)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(change);
        var properties = desired.Properties
            ?? throw new InvalidOperationException($"{desired.Describe()}: properties must be evaluated before building a PATCH body.");
        var body = JsonDiff.BuildPatchBody(properties, existing.Properties, change.Deltas, DiffOptionsFor(existing: true));
        return Reveal(body);
    }

    /// <summary>A deep copy of the evaluated object properties (edge properties excluded), secrets and unknowns preserved.</summary>
    protected static JsonObject CloneProperties(ResourceInstance desired)
    {
        ArgumentNullException.ThrowIfNull(desired);
        var properties = desired.Properties
            ?? throw new InvalidOperationException($"{desired.Describe()}: properties must be evaluated first.");
        return (JsonObject)JsonValues.Clone(properties)!;
    }

    /// <summary>Writes the resource name into the overlay's <c>nameMapsTo</c> property; a conflicting declared value is an error.</summary>
    protected void InjectName(JsonObject body, ResourceInstance desired)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(desired);
        if (Definition.NameMapsTo is not { } property)
        {
            return;
        }

        string name = desired.Id.NameLeaf;
        if (JsonObjects.TryGetIgnoreCase(body, property, out var declared) && !JsonObjects.IsAbsent(declared))
        {
            if (!JsonValues.TryGetString(declared, out var text) || !string.Equals(text, name, StringComparison.Ordinal))
            {
                throw new ProviderException(
                    DiagnosticCodes.NameMappedPropertyConflict,
                    $"{desired.Describe()}: properties.{property} ('{JsonValues.Render(declared)}') conflicts with the resource name '{name}'; the name is written to {property}, so either omit the property or make it equal.",
                    desired.Id);
            }
        }

        string key = JsonObjects.FindKey(body, property) ?? property;
        body[key] = name;
    }

    /// <summary>Fills in the overlay defaults (dotted paths) for keys the template does not set.</summary>
    protected void ApplyDefaults(JsonObject body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (Definition.Defaults is null)
        {
            return;
        }

        foreach (var pair in Definition.Defaults)
        {
            JsonObjects.SetDefault(body, pair.Key, pair.Value);
        }
    }

    /// <summary>Reveals secret leaves for a Graph request body.</summary>
    protected static JsonObject Reveal(JsonObject body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return (JsonObject)SecretJson.Reveal(body)!;
    }

    /// <summary>
    /// The overlay's Graph path with <c>{placeholder}</c> segments substituted: <c>{parentId}</c> from the key's parent
    /// Graph id, anything else from the key's values (<c>{resourceId}</c> of an app role assignment).
    /// </summary>
    protected string GraphPathFor(ResourceKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return SubstitutePath(Definition.GraphPath, name =>
        {
            if (name.Equals("parentId", StringComparison.OrdinalIgnoreCase))
            {
                return key.ParentGraphId;
            }

            return key.TryGet(name, out var value) ? value : null;
        });
    }

    /// <summary>The <see cref="ProviderException"/> (<see cref="EngineDiagnosticCodes.ResourceKeyUnresolved"/>) for a <c>{name}</c> of the Graph path that has no value yet; the caller throws it.</summary>
    protected ProviderException MissingPathValue(string name, ResourceIdentifier? id)
    {
        return new ProviderException(EngineDiagnosticCodes.ResourceKeyUnresolved, $"{TypeName}: the Graph path '{Definition.GraphPath}' needs a value for '{{{name}}}' that is not known yet.", id);
    }

    /// <summary>Replaces every <c>{name}</c> in a path pattern using <paramref name="resolve"/>; a <c>null</c> result throws.</summary>
    public string SubstitutePath(string pattern, Func<string, string?> resolve)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(resolve);
        var sb = new System.Text.StringBuilder();
        int i = 0;
        while (i < pattern.Length)
        {
            int open = pattern.IndexOf('{', i);
            if (open < 0)
            {
                sb.Append(pattern, i, pattern.Length - i);
                break;
            }

            int close = pattern.IndexOf('}', open);
            if (close < 0)
            {
                sb.Append(pattern, i, pattern.Length - i);
                break;
            }

            sb.Append(pattern, i, open - i);
            string name = pattern[(open + 1)..close];
            string value = resolve(name) ?? throw MissingPathValue(name, null);
            sb.Append(GraphPath.Segment(value));
            i = close + 1;
        }

        return sb.ToString();
    }

    /// <summary>The overlay's Graph path up to (excluding) the first placeholder segment: the top-level collection.</summary>
    protected string CollectionPath
    {
        get
        {
            int placeholder = Definition.GraphPath.IndexOf('{', StringComparison.Ordinal);
            if (placeholder < 0)
            {
                return Definition.GraphPath;
            }

            return Definition.GraphPath[..placeholder].TrimEnd('/');
        }
    }
}
