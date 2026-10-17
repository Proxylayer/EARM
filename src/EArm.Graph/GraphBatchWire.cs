using System;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;

namespace EArm.Graph;

/// <summary>
/// The wire format of Graph <c>$batch</c> for <see cref="HttpGraphClient"/>: sub-request items and URLs, the numeric wire ids that tie answers
/// back to requests, and the per-item outcome reported when an envelope fails after earlier chunks were already reported.
/// </summary>
internal static class GraphBatchWire
{
    /// <summary>
    /// The per-item outcome of a <c>$batch</c> envelope that failed after earlier chunks had already been reported. <paramref name="sent"/> is true for the
    /// items of the failed envelope (a write's outcome is then ambiguous when the failure was) and false for items of later chunks that were never sent
    /// (never ambiguous: safe to run again).
    /// </summary>
    internal static BatchResponse EnvelopeFailureResponse(BatchRequest request, string wireId, Exception failure, bool sent, RetryHint hint)
    {
        HttpStatusCode status;
        string code;
        string message;
        bool envelopeAmbiguous;
        JsonObject? error = null;
        TimeSpan? retryAfter = null;
        string? requestId = null;
        string? clientRequestId = null;
        if (failure is GraphException graph)
        {
            status = graph.Status;
            code = graph.Code;
            message = graph.GraphMessage;
            envelopeAmbiguous = graph.IsAmbiguous;
            error = graph.Error;
            retryAfter = graph.RetryAfter;
            requestId = graph.RequestId;
            clientRequestId = graph.ClientRequestId;
        }
        else
        {
            var transport = (GraphTransportException)failure;
            status = transport.Failure switch
            {
                GraphTransportFailure.NotAuthenticated => HttpStatusCode.Unauthorized,
                GraphTransportFailure.Timeout => HttpStatusCode.GatewayTimeout,
                GraphTransportFailure.RequestTooLarge or GraphTransportFailure.UnsafeUrl => HttpStatusCode.BadRequest,
                _ => HttpStatusCode.BadGateway,
            };
            code = "BatchEnvelopeFailed";
            message = transport.Message;
            envelopeAmbiguous = transport.Ambiguous;
        }

        var reportedId = request.Id ?? wireId;
        if (!sent)
        {
            var notSent = new GraphException(status, "BatchNotSent", "Not sent: an earlier $batch envelope failed (" + message + "); nothing was written for this sub-request.", hint);
            return new BatchResponse(reportedId, request, status, null, notSent);
        }

        var text = "The $batch envelope failed: " + message;
        var item = new GraphException(status, code, text, hint, error, retryAfter, requestId, clientRequestId)
        {
            IsAmbiguous = envelopeAmbiguous && HttpGraphClient.IsWrite(request.Method),
        };
        return new BatchResponse(reportedId, request, status, null, item);
    }

    internal static JsonObject BuildBatchItem(BatchRequest request, string url, string wireId)
    {
        var item = new JsonObject
        {
            ["id"] = wireId,
            ["method"] = request.Method.ToUpperInvariant(),
            ["url"] = url,
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

    internal static string BuildBatchUrl(BatchRequest request)
    {
        var path = request.Path.StartsWith('/') ? request.Path : "/" + request.Path;
        var query = GraphUrl.BuildQuery(request.UrlParameters);
        return query.Length == 0 ? path : path + "?" + query;
    }

    internal static string WireId(int index)
    {
        return (index + 1).ToString(CultureInfo.InvariantCulture);
    }

    internal static bool TryIndexFromWireId(string id, int count, out int index)
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
