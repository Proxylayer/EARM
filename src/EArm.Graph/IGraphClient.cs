using System.Collections.Generic;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace EArm.Graph;

/// <summary>One request inside a <c>POST /$batch</c>.</summary>
/// <param name="Method">HTTP method.</param>
/// <param name="Path">Path relative to the API base (e.g. <c>/groups/{id}/members/$ref</c>), without a query string.</param>
/// <param name="Body">Optional JSON body.</param>
/// <param name="Headers">Optional headers; <c>Content-Type: application/json</c> is added automatically with a body.</param>
/// <param name="UrlParameters">Optional query parameters, percent-encoded into the sub-request URL.</param>
/// <param name="Id">Optional caller id echoed in the response; defaults to the 1-based position.</param>
/// <param name="Idempotent">Whether the sub-request may be repeated blindly (see <see cref="GraphRequest.Idempotent"/>); null decides by method (POST is not).</param>
public sealed record BatchRequest(
    string Method,
    string Path,
    JsonNode? Body = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    IReadOnlyDictionary<string, string>? UrlParameters = null,
    string? Id = null,
    bool? Idempotent = null)
{
    /// <summary>Whether this sub-request may be repeated blindly: <see cref="Idempotent"/> when set, else the method's default.</summary>
    public bool IsIdempotent => Idempotent ?? GraphRequest.IsIdempotentMethod(Method);
}

/// <summary>Outcome of one <see cref="BatchRequest"/>, after any individual retry of a failed sub-response.</summary>
public sealed record BatchResponse(string Id, BatchRequest Request, HttpStatusCode Status, JsonNode? Body, GraphException? Error)
{
    public bool Succeeded => Error is null && (int)Status < 400;
}

/// <summary>Microsoft Graph client. The production implementation is <see cref="HttpGraphClient"/> (over <see cref="System.Net.Http.HttpClient"/>).</summary>
public interface IGraphClient
{
    /// <summary>GET. Returns null for a 404 (unless the request expects replication lag, in which case 404 is retried then thrown).</summary>
    Task<JsonNode?> GetAsync(GraphRequest request, CancellationToken cancellationToken);

    Task<GraphResponse> PostAsync(GraphRequest request, JsonNode? body, CancellationToken cancellationToken);

    Task<GraphResponse> PatchAsync(GraphRequest request, JsonNode body, CancellationToken cancellationToken);

    /// <summary>PUT (used for single-valued <c>$ref</c> navigation properties such as <c>/users/{id}/manager/$ref</c>).</summary>
    Task<GraphResponse> PutAsync(GraphRequest request, JsonNode body, CancellationToken cancellationToken);

    Task DeleteAsync(GraphRequest request, CancellationToken cancellationToken);

    /// <summary>GET a collection and follow every <c>@odata.nextLink</c> (validated to be on the Graph origin), re-sending the headers.</summary>
    IAsyncEnumerable<JsonNode> GetAllPagesAsync(GraphRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Sends the requests through <c>POST /$batch</c> in chunks of 20, inspects each sub-response status and retries
    /// failed sub-requests individually (through the normal retry policy). Never throws for a sub-request failure;
    /// inspect <see cref="BatchResponse.Error"/>. Results are in request order.
    /// <para>
    /// When the envelope of a LATER chunk fails (after its retries), the earlier chunks already ran and are reported as they are: the failed chunk's
    /// items carry that failure as their <see cref="BatchResponse.Error"/> (<see cref="GraphException.IsAmbiguous"/> for a write when the failure may
    /// follow a commit) and the items of the chunks after it were never sent (<c>BatchNotSent</c>, never ambiguous). A failure of the FIRST chunk still
    /// throws, because nothing has been reported yet.
    /// </para>
    /// <para>
    /// The same holds for the caller's cancellation: after the first chunk, it ends the batch with the results already known and the items not sent
    /// marked <c>BatchCancelled</c> (status 499, never ambiguous); a failed sub-request whose individual re-send was cancelled keeps its original failure.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<BatchResponse>> BatchAsync(IReadOnlyList<BatchRequest> requests, GraphApi api, RetryHint hint, CancellationToken cancellationToken);
}
