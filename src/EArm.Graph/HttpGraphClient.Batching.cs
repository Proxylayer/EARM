using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace EArm.Graph;

public sealed partial class HttpGraphClient
{
    public async Task<IReadOnlyList<BatchResponse>> BatchAsync(IReadOnlyList<BatchRequest> requests, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0)
        {
            return [];
        }

        var urls = new string[requests.Count];
        for (var i = 0; i < requests.Count; i++)
        {
            if (requests[i].Path.Contains('?', StringComparison.Ordinal))
            {
                throw new ArgumentException($"Batch request {i} embeds a query string in its path; use UrlParameters.", nameof(requests));
            }

            urls[i] = GraphBatchWire.BuildBatchUrl(requests[i]);
            GraphUrl.ValidateBatchUrl(urls[i]);
        }

        var results = new BatchResponse?[requests.Count];
        var chunks = 0;
        var failedAt = requests.Count;
        Exception? envelopeFailure = null;
        for (var offset = 0; offset < requests.Count; offset += MaxBatchSize)
        {
            chunks++;
            var count = Math.Min(MaxBatchSize, requests.Count - offset);
            var body = new JsonObject();
            var array = new JsonArray();
            var chunkIdempotent = true;
            for (var i = 0; i < count; i++)
            {
                array.Add(GraphBatchWire.BuildBatchItem(requests[offset + i], urls[offset + i], GraphBatchWire.WireId(offset + i)));
                chunkIdempotent &= requests[offset + i].IsIdempotent;
            }

            body["requests"] = array;

            // The envelope is a POST, but it only repeats what its sub-requests are allowed to repeat: one non-idempotent sub-request makes the whole POST non-idempotent.
            var batchRequest = new GraphRequest("/$batch", api, hint: hint) { Idempotent = chunkIdempotent };
            JsonArray responses;
            try
            {
                var response = await SendAsync("POST", batchRequest, body, cancellationToken).ConfigureAwait(false);
                if (response.Body is not JsonObject envelope || envelope["responses"] is not JsonArray parsed)
                {
                    throw new GraphTransportException(GraphTransportFailure.InvalidResponse, "$batch returned no 'responses' array.");
                }

                responses = parsed;
            }
            catch (Exception ex) when (offset > 0 && ex is GraphException or GraphTransportException)
            {
                // An earlier chunk already ran: its sub-requests may have committed, so its results must not be thrown away. Report this
                // chunk (and, because the later chunks may depend on it, every chunk after it) per item instead of throwing. A failure
                // of the very first chunk still throws: nothing has been reported yet, and the exception carries the precise outcome.
                failedAt = offset;
                envelopeFailure = ex;
                break;
            }
            catch (OperationCanceledException ex) when (offset > 0 && cancellationToken.IsCancellationRequested)
            {
                // The caller cancelled between chunks (a write is never abandoned on the wire, so this chunk was not sent): the earlier chunks' results
                // are kept and this chunk and every later one is reported as cancelled, instead of throwing their results away.
                failedAt = offset;
                envelopeFailure = ex;
                break;
            }

            foreach (var item in responses)
            {
                if (item is not JsonObject sub)
                {
                    continue;
                }

                var id = sub["id"] is JsonValue idValue && idValue.TryGetValue<string>(out var idText) ? idText : null;
                if (id is null || !GraphBatchWire.TryIndexFromWireId(id, requests.Count, out var index) || index < offset || index >= offset + count)
                {
                    continue;
                }

                var status = sub["status"] is JsonValue statusValue && statusValue.TryGetValue<int>(out var statusInt) ? statusInt : 0;
                var subBody = sub["body"]?.DeepClone();
                var request = requests[index];
                var reportedId = request.Id ?? GraphBatchWire.WireId(index);
                if (status is >= 200 and < 400)
                {
                    results[index] = new BatchResponse(reportedId, request, (HttpStatusCode)status, subBody, null);
                    continue;
                }

                var error = GraphErrorParser.FromBatchResponse(status, subBody, sub["headers"] as JsonObject, hint, _time);
                error.IsAmbiguous = status >= 500 && IsWrite(request.Method);
                results[index] = new BatchResponse(reportedId, request, (HttpStatusCode)status, subBody, error);
            }
        }

        if (envelopeFailure is not null)
        {
            for (var i = failedAt; i < results.Length; i++)
            {
                results[i] = envelopeFailure is OperationCanceledException
                    ? GraphBatchWire.CancelledResponse(requests[i], GraphBatchWire.WireId(i), hint)
                    : GraphBatchWire.EnvelopeFailureResponse(requests[i], GraphBatchWire.WireId(i), envelopeFailure, i < Math.Min(failedAt + MaxBatchSize, requests.Count), hint);
            }
        }

        var failed = 0;
        for (var i = 0; i < results.Length; i++)
        {
            if (results[i] is null)
            {
                // Never "OK by default": a sub-request without an answer is a failure, and for a write its outcome is unknown.
                var request = requests[i];
                results[i] = new BatchResponse(
                    request.Id ?? GraphBatchWire.WireId(i),
                    request,
                    HttpStatusCode.InternalServerError,
                    null,
                    new GraphException(HttpStatusCode.InternalServerError, string.Empty, "$batch returned no response for this sub-request.", hint) { IsAmbiguous = IsWrite(request.Method) });
            }

            if (results[i]!.Error is { IsAlreadyExists: false })
            {
                failed++;
            }
        }

        GraphLog.BatchSummary(_logger, requests.Count, chunks, failed);

        for (var i = 0; i < results.Length; i++)
        {
            var current = results[i]!;
            if (current.Error is null || current.Error.IsAlreadyExists)
            {
                continue;
            }

            if (i >= failedAt)
            {
                // The envelope itself failed under the retry policy (or was never sent): nothing to re-send one by one.
                continue;
            }

            var request = current.Request;

            // A non-idempotent sub-request is only repeated when the failure proves it was not processed: a 429, or a replication-lag
            // answer (the referenced object was not there yet). A 5xx or a missing answer may hide a commit; those stay failed (IsAmbiguous).
            if (!request.IsIdempotent && !(current.Error.Status == HttpStatusCode.TooManyRequests || current.Error.IsReplication))
            {
                continue;
            }

            var single = new GraphRequest(request.Path, api, request.Headers, advanced: false, hint) { UrlParameters = request.UrlParameters, Idempotent = request.Idempotent };
            try
            {
                var response = await SendAsync(request.Method, single, request.Body, cancellationToken).ConfigureAwait(false);
                results[i] = new BatchResponse(current.Id, request, response.Status, response.Body, null);
            }
            catch (GraphException ex)
            {
                results[i] = new BatchResponse(current.Id, request, ex.Status, current.Body, ex);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Cancelled before the single re-send went out: this item and the ones after it keep the failure their envelope reported, and every
                // result already known is returned instead of being thrown away.
                break;
            }
            catch (GraphTransportException ex) when (ex.Failure is GraphTransportFailure.Network or GraphTransportFailure.Timeout or GraphTransportFailure.ResponseTooLarge)
            {
                // One sub-request that cannot be re-sent must not discard the results of the others: report it as that item's failure.
                var status = ex.Failure == GraphTransportFailure.Timeout ? HttpStatusCode.GatewayTimeout : HttpStatusCode.BadGateway;
                var error = new GraphException(status, string.Empty, ex.Message, hint) { IsAmbiguous = ex.Ambiguous };
                results[i] = new BatchResponse(current.Id, request, status, current.Body, error);
            }
        }

        var final = new BatchResponse[results.Length];
        for (var i = 0; i < results.Length; i++)
        {
            final[i] = results[i]!;
        }

        return final;
    }
}
