using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EArm.Graph;

/// <summary>
/// Reads a Graph HTTP response for <see cref="HttpGraphClient"/>: a 2xx body parsed as JSON, a 3xx refused (redirects are never followed) and
/// anything else mapped to a <see cref="GraphException"/>.
/// </summary>
internal static class GraphResponseReader
{
    internal static GraphResponse Map(HttpResponseMessage response, string text, RetryHint hint, string clientRequestId, string? requestId, TimeProvider time)
    {
        var status = response.StatusCode;
        var code = (int)status;
        if (code is >= 200 and < 300)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return new GraphResponse(status, null);
            }

            try
            {
                return new GraphResponse(status, JsonNode.Parse(JsonSurrogateSanitizer.Replace(text.TrimStart('﻿'))));
            }
            catch (JsonException ex)
            {
                throw new GraphTransportException(GraphTransportFailure.InvalidResponse, $"Graph answered {code} with a body that is not JSON ({ex.Message}). request-id {requestId ?? "n/a"}.", ex);
            }
        }

        if (code is >= 300 and < 400)
        {
            throw new GraphException(status, "UnexpectedRedirect", $"Graph answered with a redirect ({code}); redirects are never followed because the credentials must not leave the Graph host.", hint, null, null, requestId, clientRequestId);
        }

        throw GraphErrorParser.FromResponse(status, text, response.Headers, clientRequestId, hint, time);
    }

    internal static string? FirstHeader(HttpResponseHeaders headers, string name)
    {
        if (!headers.TryGetValues(name, out var values))
        {
            return null;
        }

        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
