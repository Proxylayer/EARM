using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Core.Types;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Expressions.Values;
using EArm.Graph;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Providers;

/// <summary>
/// What the tenant policy providers (group lifecycle, token lifetime, claims mapping, app management and cross-tenant access policies) share: the
/// unsupported plan row, the display-name lookup over the whole collection (Learn documents <c>$filter</c> on none of these collections for
/// <c>displayName</c>, so the match is client-side and case-insensitive; more than one match is <see cref="ProviderException.AmbiguousMatch"/>), the
/// entity path and the create / read-back helpers.
/// </summary>
public abstract class TenantPolicyProviderBase : ProviderBase
{
    protected TenantPolicyProviderBase(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog, string resourceType)
        : base(definition, catalog)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!ResourceIdentifier.TypeEquals(definition.Type, resourceType))
        {
            throw new ArgumentException($"{GetType().Name} implements '{resourceType}', not '{definition.Type}'.", nameof(definition));
        }
    }

    /// <summary>The noun of one object in messages (<c>token lifetime policy</c>).</summary>
    protected abstract string Noun { get; }

    /// <summary>The catalog definition of <paramref name="resourceType"/>; an error when the catalog lacks it.</summary>
    protected static ResourceTypeDefinition RequireDefinition(ResourceTypeCatalog catalog, string resourceType)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Find(resourceType)
            ?? throw new InvalidOperationException($"The catalog does not define '{resourceType}'.");
    }

    protected string EntityPath(string graphId)
    {
        return CollectionPath + "/" + GraphPath.Segment(graphId);
    }

    /// <summary>
    /// <c>GET {collection}</c> (every page), matched client-side on <paramref name="property"/>: none is <c>null</c>, one is the object, several are
    /// <see cref="ProviderException.AmbiguousMatch"/> listing the ids (Graph does not enforce unique display names; EARM does).
    /// </summary>
    protected async Task<JsonObject?> FindSingleAsync(IGraphClient graph, string property, string value, GraphApi api, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var matches = new List<JsonObject>();
        await foreach (var item in graph.GetAllPagesAsync(new GraphRequest(CollectionPath, api), cancellationToken).ConfigureAwait(false))
        {
            if (item is JsonObject policy && string.Equals(JsonObjects.GetString(policy, property), value, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(policy);
            }
        }

        if (matches.Count > 1)
        {
            string ids = string.Join(", ", matches.Select(m => JsonObjects.GetId(m) ?? "?"));
            throw new ProviderException(
                ProviderException.AmbiguousMatch,
                $"{matches.Count} {Noun} objects have {property} '{value}' (ids: {ids}); rename or delete the others so the name identifies one object.",
                ResourceIdentifier.Create(TypeName, value));
        }

        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary><c>POST {collection}</c> with <paramref name="body"/>; the response body is the object (Learn: 201, or 200 on the app management policy page).</summary>
    protected async Task<AppliedResource> PostCreateAsync(ResourceInstance desired, JsonObject body, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(graph);
        var response = await graph.PostAsync(new GraphRequest(CollectionPath, desired.Api, hint: desired.RetryHint), body, cancellationToken).ConfigureAwait(false);
        var live = response.Body as JsonObject
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: Graph returned no object for the created {Noun}.", desired.Id);
        string id = JsonObjects.GetId(live)
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{TypeName}: Graph returned an object without 'id' for '{desired.Name}'.", desired.Id);
        return new AppliedResource(desired.Id, id, live, ResolveKey(desired));
    }

    /// <summary><c>PATCH {path}</c> with the body (skipped when empty), then <c>GET {path}</c> for the object as Graph now serves it.</summary>
    protected async Task<AppliedResource> PatchAndReadAsync(ResourceInstance desired, ExistingResource existing, string path, JsonObject body, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(graph);
        if (body.Count == 0)
        {
            return new AppliedResource(desired.Id, existing.GraphId, (JsonObject)JsonValues.Clone(existing.Raw)!, ResolveKey(desired));
        }

        await graph.PatchAsync(new GraphRequest(path, desired.Api, hint: desired.RetryHint), body, cancellationToken).ConfigureAwait(false);
        var live = await graph.GetAsync(new GraphRequest(path, desired.Api, hint: desired.RetryHint), cancellationToken).ConfigureAwait(false) as JsonObject
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: the {Noun} ({existing.GraphId}) vanished after the update.", desired.Id);
        return new AppliedResource(desired.Id, existing.GraphId, live, ResolveKey(desired));
    }

    /// <summary><c>DELETE {path}</c>; a 404 is "already gone".</summary>
    protected static async Task DeleteIgnoringMissingAsync(string path, GraphApi api, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        try
        {
            await graph.DeleteAsync(new GraphRequest(path, api), cancellationToken).ConfigureAwait(false);
        }
        catch (GraphException ex) when (ex.IsNotFound)
        {
            // Already gone (deleted outside EARM): nothing to do.
        }
    }

    /// <summary>Removes the named properties (any casing) from a request body.</summary>
    protected static void RemoveProperties(JsonObject body, IEnumerable<string> properties)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(properties);
        foreach (string property in properties)
        {
            if (JsonObjects.FindKey(body, property) is { } key)
            {
                body.Remove(key);
            }
        }
    }

    /// <summary>A plan row that cannot be applied (<see cref="ResourceChange.UnsupportedReason"/> set), describing the declared object as it is.</summary>
    protected ResourceChange Unsupported(ResourceInstance desired, ExistingResource? existing, string message)
    {
        ArgumentNullException.ThrowIfNull(desired);
        return new ResourceChange
        {
            Id = desired.Id,
            ResourceType = TypeName,
            Type = existing is null ? ChangeType.Create : ChangeType.Modify,
            Mode = existing is null ? ChangeMode.Create : ChangeMode.Patch,
            GraphId = existing?.GraphId,
            Before = existing is null ? null : RenderBefore(existing.Properties, desired.Properties),
            After = JsonDiff.RenderObject(desired.Properties),
            IsUnknown = desired.HasUnknownProperties || desired.IsSymbolic,
            IsSymbolicId = desired.IsSymbolic,
            Reasons = [message],
            UnsupportedReason = message,
        };
    }

    /// <summary>The problems as a <see cref="ProviderException"/> (<see cref="EngineDiagnosticCodes.ResourceApplyFailed"/>) when any, for the apply path that skipped planning.</summary>
    protected static void ThrowIfProblems(ResourceInstance desired, IReadOnlyList<string> problems)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(problems);
        if (problems.Count > 0)
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: {string.Join("; ", problems)}.", desired.Id);
        }
    }

    /// <summary>The evaluated properties, or an error when the instance was not evaluated.</summary>
    protected static JsonObject PropertiesOf(ResourceInstance desired)
    {
        ArgumentNullException.ThrowIfNull(desired);
        return desired.Properties
            ?? throw new InvalidOperationException($"{desired.Describe()}: properties must be evaluated first.");
    }
}
