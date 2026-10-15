using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Engine.Providers;
using EArm.Graph;

namespace EArm.Providers;

/// <summary>The shared soft-delete probe: <c>GET /directory/deletedItems/microsoft.graph.&lt;type&gt;?$filter=&lt;key&gt; eq '…'</c>.</summary>
public static class DeletedItemsProbe
{
    /// <summary>
    /// The soft-deleted object whose <paramref name="keyProperty"/> equals <paramref name="keyValue"/>, or <c>null</c>. The
    /// filtered query runs first (advanced-query headers when <paramref name="advanced"/>); a 400 falls back to listing the
    /// deleted objects (<c>$top=999</c>) and matching client-side; a 403, or a 400 that the listing repeats, means this
    /// identity cannot probe (the listing needs the type's read permission, Learn directory-deleteditems-list): the probe is
    /// skipped with <see cref="ProviderWarnings.SoftDeleteProbeSkipped"/> in <paramref name="warnings"/> and <c>null</c> is returned.
    /// </summary>
    public static async Task<JsonObject?> FindAsync(
        IGraphClient graph,
        string deletedPath,
        string what,
        string keyProperty,
        string keyValue,
        string[] select,
        GraphApi api,
        bool advanced,
        List<ProviderWarning> warnings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(warnings);
        var filtered = new GraphRequest(deletedPath, api)
            .WithFilter(keyProperty + " eq '" + ProviderSupport.EscapeLiteral(keyValue) + "'")
            .WithSelect(select);
        if (advanced)
        {
            filtered = filtered.WithCount().WithConsistencyLevelEventual();
        }

        try
        {
            return await FirstMatchAsync(graph, filtered, keyProperty, keyValue, cancellationToken).ConfigureAwait(false);
        }
        catch (GraphException ex) when (ex.Status == HttpStatusCode.BadRequest)
        {
            try
            {
                var scan = new GraphRequest(deletedPath, api).WithSelect(select).WithTop(999);
                return await FirstMatchAsync(graph, scan, keyProperty, keyValue, cancellationToken).ConfigureAwait(false);
            }
            catch (GraphException again) when (ProviderSupport.IsProbeUnavailable(again))
            {
                warnings.Add(ProviderSupport.ProbeSkipped(what, keyValue, again));
                return null;
            }
        }
        catch (GraphException ex) when (ProviderSupport.IsProbeUnavailable(ex))
        {
            warnings.Add(ProviderSupport.ProbeSkipped(what, keyValue, ex));
            return null;
        }
    }

    private static async Task<JsonObject?> FirstMatchAsync(IGraphClient graph, GraphRequest request, string keyProperty, string keyValue, CancellationToken cancellationToken)
    {
        await foreach (var item in graph.GetAllPagesAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (item is JsonObject deleted && string.Equals(JsonObjects.GetString(deleted, keyProperty), keyValue, StringComparison.OrdinalIgnoreCase))
            {
                return deleted;
            }
        }

        return null;
    }
}
