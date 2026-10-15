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
using EArm.Engine.State;
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
public abstract class ProviderBase : IResourceProvider, IExistingResourceLookup
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
    /// Whether a changed immutable property replaces the object (assignments, grants, secrets, FICs: every property
    /// immutable) or is a hard error (applications, groups, users). Defaults to the overlay's <c>immutable: ["*"]</c>.
    /// </summary>
    protected virtual bool RecreateOnImmutableChange => Definition.ImmutableAll;

    /// <summary>The upserts (<c>uniqueName</c>-keyed applications and groups, authentication contexts by id, federated identity credentials: PATCH with <c>Prefer: create-if-missing</c>) can be repeated after an unknown outcome; every POST create cannot.</summary>
    public virtual bool CreateIsRepeatable => Definition.NameKind is ResourceNameKind.UniqueName or ResourceNameKind.Id
        || Definition.Type.EndsWith("/federatedIdentityCredentials", StringComparison.OrdinalIgnoreCase);

    // ---- key ----

    public virtual ResourceKey ResolveKey(ResourceInstance desired)
    {
        ArgumentNullException.ThrowIfNull(desired);
        if (Definition.NameKind == ResourceNameKind.Guid)
        {
            return CompositeKey(desired);
        }

        var key = KeyForIdentifier(Definition, desired.Id, desired.IsSymbolic, desired.Parent?.IsSymbolic ?? false) with { Api = desired.Api };
        if (desired.ParentGraphId is not null && key.ParentKey is not null)
        {
            key = key with { ParentGraphId = desired.ParentGraphId };
        }

        return key;
    }

    /// <summary>
    /// <c>reference(resourceId(...), apiVersion)</c>: reads an object that is not declared in the template by the key
    /// its resource id implies. GUID-named (composite-key) types cannot be addressed this way.
    /// </summary>
    public virtual Task<ExistingResource?> ReadExistingAsync(ResourceIdentifier id, string apiVersion, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(apiVersion);
        ArgumentNullException.ThrowIfNull(graph);

        if (Definition.NameKind == ResourceNameKind.Guid)
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceKeyUnresolved, $"'{id.Id}' is a {TypeName}, whose name is a deterministic GUID; such objects cannot be looked up as existing resources by name. Declare the assignment in the template instead.", id);
        }

        if (!Definition.TryGetGraphApi(apiVersion, out var api))
        {
            throw new ProviderException(DiagnosticCodes.UnknownApiVersion, Definition.DescribeUnknownApiVersion(apiVersion), id);
        }

        var key = KeyForIdentifier(Definition, id) with { Api = GraphApiMapping.ToAz(api) };
        return ReadAsync(key, null, graph, cancellationToken);
    }

    /// <summary>
    /// The key an identifier implies for a name-keyed type: the last name segment under <c>nameMapsTo</c>, plus the
    /// parent's key for child types (resolved through the catalog).
    /// </summary>
    public ResourceKey KeyForIdentifier(ResourceTypeDefinition definition, ResourceIdentifier id, bool symbolic = false, bool parentSymbolic = false)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(id);

        string property = definition.NameMapsTo ?? throw new ProviderException(EngineDiagnosticCodes.ResourceKeyUnresolved, $"Type '{definition.Type}' has no name-mapped property; its key is composite.", id);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [property] = id.NameLeaf };
        var unresolved = symbolic ? new List<string> { property } : [];

        ResourceKey? parentKey = null;
        if (definition.IsChildType && id.Parent is { } parentId)
        {
            var parentDefinition = _catalog.Find(definition.ParentType)
                ?? throw new ProviderException(EngineDiagnosticCodes.ResourceKeyUnresolved, $"Parent type '{definition.ParentType}' of '{definition.Type}' is not in the catalog.", id);
            parentKey = KeyForIdentifier(parentDefinition, parentId, symbolic || parentSymbolic);
        }
        else if (definition.IsChildType)
        {
            // A symbolic child name is a single segment; the parent is unknown until the name resolves.
            unresolved.Add("parent");
        }

        return new ResourceKey { Type = definition.Type, Values = values, ParentKey = parentKey, UnresolvedProperties = unresolved };
    }

    private ResourceKey CompositeKey(ResourceInstance desired)
    {
        var properties = desired.Properties
            ?? throw new InvalidOperationException($"{desired.Describe()}: properties must be evaluated before its composite key can be resolved.");

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unresolved = new List<string>();
        foreach (var property in Definition.KeyProperties)
        {
            if (!JsonObjects.TryGetIgnoreCase(properties, property, out var node) || JsonObjects.IsAbsent(node))
            {
                // Absent composite member (oauth2PermissionGrants.principalId for AllPrincipals); a missing required
                // member is reported by Plan() through requiredOnCreate.
                continue;
            }

            if (JsonObjects.GetString(properties, property) is { } text)
            {
                values[property] = text;
                continue;
            }

            // A deploy-time-unknown or secret value: the key cannot be looked up yet.
            unresolved.Add(property);
        }

        return new ResourceKey { Type = TypeName, Api = desired.Api, Values = values, UnresolvedProperties = unresolved };
    }

    // ---- Graph calls (per provider) ----

    /// <summary>See <see cref="IResourceProvider.PrepareAsync"/>: sugar resolution and live-data validation before the key; nothing by default.</summary>
    public virtual Task PrepareAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public abstract Task<ExistingResource?> ReadAsync(ResourceKey key, string? stateId, IGraphClient graph, CancellationToken cancellationToken);

    public abstract Task<AppliedResource> CreateAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken);

    public abstract Task<AppliedResource> UpdateAsync(ResourceInstance desired, ExistingResource existing, ResourceChange change, IGraphClient graph, CancellationToken cancellationToken);

    public abstract Task DeleteAsync(StateEntry entry, IGraphClient graph, CancellationToken cancellationToken);

    // ---- plan ----

    public virtual ResourceChange Plan(ResourceInstance desired, ExistingResource? existing, PlanOptions options)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(options);

        if (desired.IsIgnored)
        {
            return ResourceChange.Ignore(desired.Id, TypeName, "condition evaluated to false");
        }

        var properties = desired.Properties
            ?? throw new InvalidOperationException($"{desired.Describe()}: properties must be evaluated before planning.");

        var reasons = new List<string>();
        if (desired.IsSymbolic)
        {
            reasons.Add($"the name is only known once '{SymbolSource(desired)}' exists");
        }

        if (existing is null)
        {
            var createOptions = DiffOptionsFor(existing: false);
            var createDeltas = JsonDiff.DescribeCreate(properties, createOptions);
            if (desired.HasUnknownProperties)
            {
                reasons.Add("some property values are only known at deployment time");
            }

            var missing = MissingRequiredOnCreate(properties);
            if (missing.Count > 0)
            {
                string message = $"{desired.Describe()} cannot be created: required propert{(missing.Count == 1 ? "y" : "ies")} {string.Join(", ", missing)} missing from 'properties'.";
                reasons.Add(message);
                return new ResourceChange
                {
                    Id = desired.Id,
                    ResourceType = TypeName,
                    Type = ChangeType.Create,
                    Mode = ChangeMode.Create,
                    Deltas = createDeltas,
                    After = JsonDiff.RenderObject(properties),
                    IsUnknown = desired.HasUnknownProperties || desired.IsSymbolic,
                    IsSymbolicId = desired.IsSymbolic,
                    Reasons = reasons,
                    UnsupportedReason = message,
                };
            }

            return new ResourceChange
            {
                Id = desired.Id,
                ResourceType = TypeName,
                Type = ChangeType.Create,
                Mode = ChangeMode.Create,
                Deltas = createDeltas,
                After = JsonDiff.RenderObject(properties),
                IsUnknown = desired.HasUnknownProperties || desired.IsSymbolic,
                IsSymbolicId = desired.IsSymbolic,
                Reasons = reasons,
            };
        }

        var diffOptions = DiffOptionsFor(existing: true);
        var deltas = JsonDiff.Diff(properties, existing.Properties, diffOptions);
        var effective = deltas.Where(d => d.HasEffect).ToList();
        var before = RenderBefore(existing.Properties, properties);
        var after = JsonDiff.RenderObject(properties);

        if (effective.Count == 0)
        {
            return new ResourceChange
            {
                Id = desired.Id,
                ResourceType = TypeName,
                Type = ChangeType.NoChange,
                Mode = ChangeMode.None,
                Deltas = deltas,
                Before = before,
                After = after,
                GraphId = existing.GraphId,
                IsUnknown = desired.HasUnknownProperties,
                Reasons = reasons,
            };
        }

        // immutable: ["*"] (ImmutableAll) strips the star from the overlay's list, so every effective delta is an
        // immutable change for such a type; otherwise only the listed paths are.
        var immutable = Definition.ImmutableAll
            ? effective
            : effective.Where(d => JsonDiff.IsImmutable(d, ImmutableProperties, diffOptions.BasePath)).ToList();
        if (immutable.Count > 0)
        {
            string names = string.Join(", ", immutable.Select(d => d.Path.ToString()));
            if (RecreateOnImmutableChange)
            {
                reasons.Add(Definition.ImmutableAll
                    ? $"immutable property {names} changed (every property of {Definition.TypeLeaf} is immutable); the object is deleted and created again"
                    : $"immutable property {names} changed; the object is deleted and created again");
                return new ResourceChange
                {
                    Id = desired.Id,
                    ResourceType = TypeName,
                    Type = ChangeType.Modify,
                    Mode = ChangeMode.Recreate,
                    Deltas = deltas,
                    Before = before,
                    After = after,
                    GraphId = existing.GraphId,
                    IsUnknown = desired.HasUnknownProperties,
                    Reasons = reasons,
                };
            }

            string message = $"Immutable property {names} of {desired.Describe()} differs from the live object and cannot be changed in place; restore the original value, or delete and recreate the resource.";
            reasons.Add(message);
            return new ResourceChange
            {
                Id = desired.Id,
                ResourceType = TypeName,
                Type = ChangeType.Modify,
                Mode = ChangeMode.Patch,
                Deltas = deltas,
                Before = before,
                After = after,
                GraphId = existing.GraphId,
                IsUnknown = desired.HasUnknownProperties,
                Reasons = reasons,
                UnsupportedReason = message,
            };
        }

        if (desired.HasUnknownProperties)
        {
            reasons.Add("some property values are only known at deployment time");
        }

        return new ResourceChange
        {
            Id = desired.Id,
            ResourceType = TypeName,
            Type = ChangeType.Modify,
            Mode = ChangeMode.Patch,
            Deltas = deltas,
            Before = before,
            After = after,
            GraphId = existing.GraphId,
            IsUnknown = desired.HasUnknownProperties,
            Reasons = reasons,
        };
    }

    /// <summary>The diff options for this type (member keys, nullability, create-only exclusions on update).</summary>
    protected JsonDiffOptions DiffOptionsFor(bool existing)
    {
        return JsonDiffOptions.ForDefinition(Definition, existing);
    }

    /// <summary>
    /// The rendering copy of the live properties for <see cref="ResourceChange.Before"/>: unknowns rendered, and every
    /// live value whose desired counterpart is secret, or whose path is in the type's overlay <c>secure</c> list,
    /// masked as <c>***</c> (a rerun's live value IS the secret, so rendering it verbatim would print the plaintext).
    /// Providers building their own <see cref="ResourceChange"/> use this instead of <see cref="JsonDiff.RenderObject"/>
    /// for the live side.
    /// </summary>
    protected JsonObject? RenderBefore(JsonObject? live, JsonObject? desired)
    {
        var rendered = JsonDiff.RenderLiveObject(live, desired);
        JsonDiff.MaskSecurePaths(rendered, Definition.Secure);
        return rendered;
    }

    /// <summary>
    /// The overlay's <c>requiredOnCreate</c> paths (dotted) that are absent from the evaluated properties and have no
    /// overlay default. A deploy-time-unknown value counts as present.
    /// </summary>
    protected IReadOnlyList<string> MissingRequiredOnCreate(JsonObject properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        var missing = new List<string>();
        foreach (var path in Definition.RequiredOnCreate)
        {
            if (Definition.Defaults is { } defaults && defaults.ContainsKey(path))
            {
                continue;
            }

            if (!JsonObjects.TryGetDotted(properties, path, out var node) || JsonObjects.IsAbsent(node))
            {
                missing.Add(path);
            }
        }

        return missing;
    }

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

    /// <summary>Replaces every <c>{name}</c> in a path pattern; throws <see cref="ProviderException"/> when a value is missing.</summary>
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

    private static string SymbolSource(ResourceInstance desired)
    {
        foreach (var occurrence in desired.NameReferences)
        {
            if (occurrence.Target is not null)
            {
                return occurrence.Target.Id;
            }
        }

        return desired.PlannedId.NameLeaf;
    }
}
