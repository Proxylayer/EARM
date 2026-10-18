using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Core;
using EArm.Engine.Providers;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Providers;

/// <summary>The Graph lookups of <see cref="GroupsProvider"/>: by uniqueName, by display name for adoption, and soft-deleted Microsoft 365 groups by mailNickname.</summary>
public sealed partial class GroupsProvider
{
    /// <summary>
    /// <c>GET /groups(uniqueName='…')</c>. <c>uniqueName</c> is documented as an alternate key for the upsert only, so a tenant that
    /// rejects the key form (400) is asked with <c>$filter=uniqueName eq '…'</c> (advanced query). A 404 stays "does not exist".
    /// </summary>
    private static async Task<JsonObject?> GetByUniqueNameAsync(IGraphClient graph, ResourceKey key, string[] select, CancellationToken cancellationToken)
    {
        string uniqueName = ProviderSupport.SingleKey(key, "uniqueName");
        try
        {
            return await graph.GetAsync(new GraphRequest(AlternateKeyPath(key), key.Api).WithSelect(select), cancellationToken).ConfigureAwait(false) as JsonObject;
        }
        catch (GraphException ex) when (ex.Status == HttpStatusCode.BadRequest)
        {
            var request = new GraphRequest(Collection, key.Api)
                .WithFilter("uniqueName eq '" + ProviderSupport.EscapeLiteral(uniqueName) + "'")
                .WithCount()
                .WithConsistencyLevelEventual()
                .WithSelect(select);
            await foreach (var item in graph.GetAllPagesAsync(request, cancellationToken).ConfigureAwait(false))
            {
                if (item is JsonObject match && string.Equals(JsonObjects.GetString(match, "uniqueName"), uniqueName, StringComparison.OrdinalIgnoreCase))
                {
                    return match;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// <c>GET /groups?$filter=displayName eq '…'</c> (a basic query: displayName supports <c>eq</c>), re-checked client-side (case-insensitive).
    /// See <see cref="ReadAsync"/> for which match is adopted.
    /// </summary>
    private async Task<JsonObject?> FindAdoptableByDisplayNameAsync(IGraphClient graph, ResourceKey key, string displayName, string[] select, CancellationToken cancellationToken)
    {
        string uniqueName = ProviderSupport.SingleKey(key, "uniqueName");
        var request = new GraphRequest(Collection, key.Api)
            .WithFilter("displayName eq '" + ProviderSupport.EscapeLiteral(displayName) + "'")
            .WithSelect(select);
        var candidates = new List<JsonObject>();
        await foreach (var item in graph.GetAllPagesAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (item is not JsonObject match || !string.Equals(JsonObjects.GetString(match, "displayName"), displayName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? liveUniqueName = JsonObjects.GetString(match, "uniqueName");
            if (string.Equals(liveUniqueName, uniqueName, StringComparison.OrdinalIgnoreCase))
            {
                return match;
            }

            if (string.IsNullOrEmpty(liveUniqueName))
            {
                candidates.Add(match);
            }
        }

        if (candidates.Count > 1)
        {
            string ids = string.Join(", ", candidates.Select(m => JsonObjects.GetId(m) ?? "?"));
            throw new ProviderException(
                ProviderException.AmbiguousMatch,
                $"No group has uniqueName '{uniqueName}', and {candidates.Count} groups without a uniqueName have displayName '{displayName}' (ids: {ids}); EARM cannot tell which one to adopt. Rename or delete the others so the display name identifies one group (nothing was created).",
                ResourceIdentifier.Create(TypeName, uniqueName));
        }

        return candidates.Count == 1 ? candidates[0] : null;
    }

    private async Task<JsonObject?> FindSoftDeletedAsync(string uniqueName, string? mailNickname, GraphApi api, IGraphClient graph, List<ProviderWarning> warnings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(mailNickname))
        {
            return null;
        }

        JsonNode? page;
        try
        {
            var request = new GraphRequest(DeletedGroups, api)
                .WithFilter($"mailNickname eq '{ProviderSupport.EscapeLiteral(mailNickname)}'")
                .WithSelect("id", "displayName", "uniqueName", "mailNickname", "deletedDateTime");
            page = await graph.GetAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (GraphException ex)
        {
            // The probe is best effort: a tenant (or an identity without Group.Read.All) that refuses the query must not block creates.
            warnings.Add(ProviderSupport.ProbeSkipped("group", mailNickname, ex));
            return null;
        }

        if ((page as JsonObject)?["value"] is not JsonArray items)
        {
            return null;
        }

        foreach (var item in items)
        {
            if (item is JsonObject deleted && (JsonObjects.KeyEquals(JsonObjects.GetString(deleted, "uniqueName"), uniqueName) || JsonObjects.KeyEquals(JsonObjects.GetString(deleted, "mailNickname"), mailNickname)))
            {
                return deleted;
            }
        }

        return null;
    }
}
