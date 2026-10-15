using System.Collections.Generic;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace EArm.Az;

/// <summary>One request inside a <c>POST /$batch</c>.</summary>
/// <param name="Method">HTTP method.</param>
/// <param name="Path">Path relative to the API base (e.g. <c>/groups/{id}/members/$ref</c>), without a query string.</param>
/// <param name="Body">Optional JSON body.</param>
/// <param name="Headers">Optional headers; <c>Content-Type: application/json</c> is added automatically with a body.</param>
/// <param name="UrlParameters">Optional query parameters, encoded into the sub-request URL.</param>
/// <param name="Id">Optional caller id echoed in the response; defaults to the 1-based position.</param>
public sealed record BatchRequest(
    string Method,
    string Path,
    JsonNode? Body = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    IReadOnlyDictionary<string, string>? UrlParameters = null,
    string? Id = null);

/// <summary>Outcome of one <see cref="BatchRequest"/>, after any individual retry of a failed sub-response.</summary>
public sealed record BatchResponse(string Id, BatchRequest Request, HttpStatusCode Status, JsonNode? Body, GraphException? Error)
{
    public bool Succeeded => Error is null && (int)Status < 400;
}

/// <summary>Microsoft Graph client. The production implementation is <see cref="AzGraphClient"/> (over <c>az rest</c>).</summary>
public interface IGraphClient
{
    /// <summary>GET. Returns null for a 404 (unless the request expects replication lag, in which case 404 is retried then thrown).</summary>
    Task<JsonNode?> GetAsync(GraphRequest request, CancellationToken cancellationToken);

    Task<GraphResponse> PostAsync(GraphRequest request, JsonNode? body, CancellationToken cancellationToken);

    Task<GraphResponse> PatchAsync(GraphRequest request, JsonNode body, CancellationToken cancellationToken);

    /// <summary>PUT (used for single-valued <c>$ref</c> navigation properties such as <c>/users/{id}/manager/$ref</c>).</summary>
    Task<GraphResponse> PutAsync(GraphRequest request, JsonNode body, CancellationToken cancellationToken);

    Task DeleteAsync(GraphRequest request, CancellationToken cancellationToken);

    /// <summary>GET a collection and follow every absolute <c>@odata.nextLink</c>, re-sending the headers.</summary>
    IAsyncEnumerable<JsonNode> GetAllPagesAsync(GraphRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Sends the requests through <c>POST /$batch</c> in chunks of 20, inspects each sub-response status and retries
    /// failed sub-requests individually (through the normal retry policy). Never throws for a sub-request failure;
    /// inspect <see cref="BatchResponse.Error"/>. Results are in request order.
    /// </summary>
    Task<IReadOnlyList<BatchResponse>> BatchAsync(IReadOnlyList<BatchRequest> requests, GraphApi api, RetryHint hint, CancellationToken cancellationToken);
}
