using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace EntraArm.Az;

/// <summary>
/// <see cref="IGraphClient"/> over <c>az rest</c> (through <see cref="IAzRunner"/>): status inference, error
/// mapping, retries per <see cref="RetryPolicy"/>, absolute <c>@odata.nextLink</c> pagination and <c>$batch</c>
/// in chunks of 20 with individual retry of failed sub-requests.
/// </summary>
public sealed class AzGraphClient : IGraphClient
{
    public const int MaxBatchSize = 20;

    private readonly IAzRunner _runner;
    private readonly RetryPolicy _retryPolicy;
    private readonly ILogger<AzGraphClient> _logger;

    public AzGraphClient(IAzRunner runner, RetryPolicy retryPolicy, ILogger<AzGraphClient> logger)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _retryPolicy = retryPolicy ?? throw new ArgumentNullException(nameof(retryPolicy));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<JsonNode?> GetAsync(GraphRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var response = await SendAsync("GET", request, null, cancellationToken).ConfigureAwait(false);
            return response.Body;
        }
        catch (GraphException ex) when (ex.IsNotFound && !ex.IsReplication)
        {
            return null;
        }
    }

    public Task<GraphResponse> PostAsync(GraphRequest request, JsonNode? body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync("POST", request, body, cancellationToken);
    }

    public Task<GraphResponse> PatchAsync(GraphRequest request, JsonNode body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(body);
        return SendAsync("PATCH", request, body, cancellationToken);
    }

    public Task<GraphResponse> PutAsync(GraphRequest request, JsonNode body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(body);
        return SendAsync("PUT", request, body, cancellationToken);
    }

    public async Task DeleteAsync(GraphRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await SendAsync("DELETE", request, null, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<JsonNode> GetAllPagesAsync(GraphRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var current = request;
        while (true)
        {
            var response = await SendAsync("GET", current, null, cancellationToken).ConfigureAwait(false);
            if (response.Body is not JsonObject page)
            {
                yield break;
            }

            if (page["value"] is JsonArray items)
            {
                foreach (var item in items)
                {
                    if (item is not null)
                    {
                        yield return item;
                    }
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

            current = current.ForNextLink(nextLink);
        }
    }

    public async Task<IReadOnlyList<BatchResponse>> BatchAsync(IReadOnlyList<BatchRequest> requests, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0)
        {
            return [];
        }

        for (var i = 0; i < requests.Count; i++)
        {
            if (requests[i].Path.Contains('?', StringComparison.Ordinal))
            {
                throw new ArgumentException($"Batch request {i} embeds a query string in its path; use UrlParameters.", nameof(requests));
            }
        }

        var results = new BatchResponse?[requests.Count];
        var chunks = 0;
        for (var offset = 0; offset < requests.Count; offset += MaxBatchSize)
        {
            chunks++;
            var count = Math.Min(MaxBatchSize, requests.Count - offset);
            var body = new JsonObject();
            var array = new JsonArray();
            for (var i = 0; i < count; i++)
            {
                array.Add(BuildBatchItem(requests[offset + i], WireId(offset + i)));
            }

            body["requests"] = array;

            var batchRequest = new GraphRequest("/$batch", api, hint: hint);
            var response = await SendAsync("POST", batchRequest, body, cancellationToken).ConfigureAwait(false);
            if (response.Body is not JsonObject envelope || envelope["responses"] is not JsonArray responses)
            {
                throw new AzCliException(AzCliFailure.Unknown, "$batch returned no 'responses' array.");
            }

            foreach (var item in responses)
            {
                if (item is not JsonObject sub)
                {
                    continue;
                }

                var id = sub["id"] is JsonValue idValue && idValue.TryGetValue<string>(out var idText) ? idText : null;
                if (id is null || !TryIndexFromWireId(id, requests.Count, out var index) || index < offset || index >= offset + count)
                {
                    continue;
                }

                var status = sub["status"] is JsonValue statusValue && statusValue.TryGetValue<int>(out var statusInt) ? statusInt : 0;
                var subBody = sub["body"]?.DeepClone();
                var request = requests[index];
                var reportedId = request.Id ?? WireId(index);
                results[index] = status is >= 200 and < 400
                    ? new BatchResponse(reportedId, request, (HttpStatusCode)status, subBody, null)
                    : new BatchResponse(reportedId, request, (HttpStatusCode)status, subBody, GraphErrorParser.FromBatchResponse(status, subBody, hint));
            }
        }

        var failed = 0;
        for (var i = 0; i < results.Length; i++)
        {
            if (results[i] is null)
            {
                var request = requests[i];
                results[i] = new BatchResponse(
                    request.Id ?? WireId(i),
                    request,
                    HttpStatusCode.InternalServerError,
                    null,
                    new GraphException(HttpStatusCode.InternalServerError, string.Empty, "$batch returned no response for this sub-request.", hint));
            }

            if (results[i]!.Error is { IsAlreadyExists: false })
            {
                failed++;
            }
        }

        AzLog.BatchSummary(_logger, requests.Count, chunks, failed);

        for (var i = 0; i < results.Length; i++)
        {
            var current = results[i]!;
            if (current.Error is null || current.Error.IsAlreadyExists)
            {
                continue;
            }

            var request = current.Request;
            var single = new GraphRequest(request.Path, api, request.Headers, advanced: false, hint) { UrlParameters = request.UrlParameters };
            try
            {
                var response = await SendAsync(request.Method, single, request.Body, cancellationToken).ConfigureAwait(false);
                results[i] = new BatchResponse(current.Id, request, response.Status, response.Body, null);
            }
            catch (GraphException ex)
            {
                results[i] = new BatchResponse(current.Id, request, ex.Status, current.Body, ex);
            }
        }

        var final = new BatchResponse[results.Length];
        for (var i = 0; i < results.Length; i++)
        {
            final[i] = results[i]!;
        }

        return final;
    }

    /// <summary>Runs one <c>az rest</c> call under the retry policy and maps the result.</summary>
    internal async Task<GraphResponse> SendAsync(string method, GraphRequest request, JsonNode? body, CancellationToken cancellationToken)
    {
        method = method.ToUpperInvariant();
        var headers = new Dictionary<string, string>(request.EffectiveHeaders(), StringComparer.OrdinalIgnoreCase);
        if (body is not null && !headers.ContainsKey("Content-Type"))
        {
            headers["Content-Type"] = "application/json";
        }

        var azRequest = AzRequest.Rest(method, request.Url, request.UrlParameters, headers, body);
        var response = await _retryPolicy.ExecuteAsync(
            async token =>
            {
                var result = await _runner.RunAsync(azRequest, token).ConfigureAwait(false);
                return GraphResultMapper.ToGraphResponse(result, method, request);
            },
            request.Hint,
            RefreshAccessTokenAsync,
            cancellationToken).ConfigureAwait(false);

        AzLog.GraphResponse(_logger, method, request.Url, (int)response.Status);
        return response;
    }

    private async Task RefreshAccessTokenAsync(CancellationToken cancellationToken)
    {
        var refresh = AzRequest.ForCommand("account", "get-access-token").WithArguments("--resource-type", "ms-graph");
        var result = await _runner.RunAsync(refresh, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw AzCliException.FromResult(result, "az account get-access-token");
        }
    }

    private static JsonObject BuildBatchItem(BatchRequest request, string wireId)
    {
        var item = new JsonObject
        {
            ["id"] = wireId,
            ["method"] = request.Method.ToUpperInvariant(),
            ["url"] = BuildBatchUrl(request),
        };

        // JsonObject keys are case-sensitive but HTTP header names are not: track a caller-supplied Content-Type of any case so
        // the default is added only when none exists, and the caller's value and spelling win.
        var headers = new JsonObject();
        var hasContentType = false;
        if (request.Headers is not null)
        {
            foreach (var pair in request.Headers)
            {
                headers[pair.Key] = pair.Value;
                hasContentType |= string.Equals(pair.Key, "Content-Type", StringComparison.OrdinalIgnoreCase);
            }
        }

        if (request.Body is not null && !hasContentType)
        {
            headers["Content-Type"] = "application/json";
        }

        if (headers.Count > 0)
        {
            item["headers"] = headers;
        }

        if (request.Body is not null)
        {
            item["body"] = request.Body.DeepClone();
        }

        return item;
    }

    private static string BuildBatchUrl(BatchRequest request)
    {
        var path = request.Path.StartsWith('/') ? request.Path : "/" + request.Path;
        if (request.UrlParameters is not { Count: > 0 })
        {
            return path;
        }

        var builder = new StringBuilder(path).Append('?');
        var first = true;
        foreach (var pair in request.UrlParameters)
        {
            if (!first)
            {
                builder.Append('&');
            }

            first = false;
            builder.Append(pair.Key).Append('=').Append(Uri.EscapeDataString(pair.Value));
        }

        return builder.ToString();
    }

    private static string WireId(int index)
    {
        return (index + 1).ToString(CultureInfo.InvariantCulture);
    }

    private static bool TryIndexFromWireId(string id, int count, out int index)
    {
        index = -1;
        if (!int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1 || number > count)
        {
            return false;
        }

        index = number - 1;
        return true;
    }
}

/// <summary>Maps an <see cref="AzResult"/> of an <c>az rest</c> call to a <see cref="GraphResponse"/> or throws the mapped error.</summary>
internal static class GraphResultMapper
{
    public static GraphResponse ToGraphResponse(AzResult result, string method, GraphRequest request)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(request);

        if (result.Succeeded)
        {
            var text = result.StandardOutput.Trim();
            if (text.Length == 0)
            {
                return new GraphResponse(HttpStatusCode.NoContent, null);
            }

            JsonNode? body;
            try
            {
                body = JsonNode.Parse(JsonSurrogateSanitizer.Replace(text));
            }
            catch (JsonException ex)
            {
                throw new AzCliException(AzCliFailure.Unknown, $"{method} {request.Url}: az returned output that is not JSON ({ex.Message}).", result.ExitCode, result.StandardError, innerException: ex);
            }

            if (body is JsonObject obj && obj.Count == 1 && obj["error"] is JsonObject errorObject)
            {
                throw GraphErrorParser.FromErrorObject(null, errorObject, request.Hint, result.StandardError);
            }

            return new GraphResponse(InferStatus(method, request, body), body);
        }

        if (GraphErrorParser.TryParse(result.StandardError, result.StandardOutput, request.Hint, out var graphError))
        {
            throw graphError!;
        }

        throw AzCliException.FromResult(result, $"{method} {request.Url}");
    }

    /// <summary>No body ⇒ 204; a body with <c>id</c> from POST or a create-if-missing PATCH ⇒ 201; otherwise 200.</summary>
    public static HttpStatusCode InferStatus(string method, GraphRequest request, JsonNode? body)
    {
        if (body is null)
        {
            return HttpStatusCode.NoContent;
        }

        var hasId = body is JsonObject obj && obj.ContainsKey("id");
        switch (method.ToUpperInvariant())
        {
            case "POST":
                return hasId ? HttpStatusCode.Created : HttpStatusCode.OK;
            case "PATCH":
            {
                var prefer = GraphRequest.TryGetHeader(request.Headers, "Prefer", out var value) ? value : string.Empty;
                return hasId && prefer.Contains("create-if-missing", StringComparison.OrdinalIgnoreCase) ? HttpStatusCode.Created : HttpStatusCode.OK;
            }

            default:
                return HttpStatusCode.OK;
        }
    }
}
