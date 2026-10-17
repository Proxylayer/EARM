using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading;

namespace EArm.Graph;

public sealed partial class HttpGraphClient
{
    public async IAsyncEnumerable<JsonNode> GetAllPagesAsync(GraphRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var uri = GraphUrl.Build(_options.Cloud, request);
        var visited = new HashSet<string>(StringComparer.Ordinal) { uri.AbsoluteUri };
        var maxPages = Math.Max(1, _options.MaxPages);
        var maxItems = Math.Max(1, _options.MaxItems);
        var pages = 0;
        var yielded = 0;
        while (true)
        {
            if (pages >= maxPages)
            {
                throw new GraphTransportException(
                    GraphTransportFailure.PagingLimitExceeded,
                    $"GET {GraphUrl.RedactForLog(uri)}: the collection has more than {maxPages.ToString(CultureInfo.InvariantCulture)} pages ({yielded.ToString(CultureInfo.InvariantCulture)} items so far); stopping instead of paging without end. Narrow the query with $filter or $top.");
            }

            pages++;
            var response = await SendAsync("GET", uri, request, null, cancellationToken).ConfigureAwait(false);
            if (response.Body is not JsonObject page)
            {
                yield break;
            }

            if (page["value"] is JsonArray items)
            {
                foreach (var item in items)
                {
                    if (item is null)
                    {
                        continue;
                    }

                    if (yielded >= maxItems)
                    {
                        throw new GraphTransportException(
                            GraphTransportFailure.PagingLimitExceeded,
                            $"GET {GraphUrl.RedactForLog(uri)}: the collection has more than {maxItems.ToString(CultureInfo.InvariantCulture)} items; stopping instead of reading without end. Narrow the query with $filter.");
                    }

                    yielded++;
                    yield return item;
                }
            }
            else
            {
                // Not a collection: a single entity was addressed.
                yield return page;
                yield break;
            }

            var nextLink = page["@odata.nextLink"] is JsonValue link && link.TryGetValue<string>(out var text) ? text : null;
            if (string.IsNullOrEmpty(nextLink))
            {
                yield break;
            }

            uri = GraphUrl.ParseNextLink(_options.Cloud, nextLink);
            if (!visited.Add(uri.AbsoluteUri))
            {
                throw new GraphTransportException(GraphTransportFailure.InvalidResponse, $"Graph returned an @odata.nextLink that was already followed ({GraphUrl.RedactForLog(uri)}); stopping instead of looping.");
            }
        }
    }
}
