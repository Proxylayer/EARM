using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using EArm.Graph.Auth;

namespace EArm.Graph;

/// <summary>
/// Builds the HTTP request <see cref="HttpGraphClient"/> sends: the serialized body (with public-cloud references rewritten for a sovereign cloud),
/// the validated caller headers and the message with the bearer token and the client's own headers.
/// </summary>
internal static class GraphRequestBuilder
{
    private static readonly JsonSerializerOptions BodyJsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly HashSet<string> ForbiddenRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Proxy-Authorization",
        "Cookie",
        "Cookie2",
    };

    private static readonly HashSet<string> IgnoredRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host",
        "Content-Length",
        "Transfer-Encoding",
        "User-Agent",
        "Content-Type",
        "Connection",
        "Expect",
        "client-request-id",
    };

    internal static string SerializeBody(JsonNode body, GraphCloud cloud)
    {
        if (cloud != GraphCloud.Public)
        {
            body = body.DeepClone();
            RewriteODataIds(body, cloud);
        }

        return body.ToJsonString(BodyJsonOptions);
    }

    /// <summary>
    /// The Edge pass and the providers write public-cloud references (<c>@odata.id</c>, <c>owners@odata.bind</c>, <c>members@odata.bind</c>, ...); in a
    /// sovereign cloud they must name that cloud's Graph host. Every string value, and every string element of an array value, of a key ending in
    /// <c>@odata.id</c> or <c>@odata.bind</c> that starts with the public Graph origin is rewritten.
    /// </summary>
    private static void RewriteODataIds(JsonNode? node, GraphCloud cloud)
    {
        var publicOrigin = GraphCloud.Public.GraphOrigin + "/";
        switch (node)
        {
            case JsonObject obj:
                foreach (var pair in obj.ToArray())
                {
                    if (IsReferenceKey(pair.Key))
                    {
                        if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text))
                        {
                            obj[pair.Key] = RewriteOrigin(text, publicOrigin, cloud);
                            continue;
                        }

                        if (pair.Value is JsonArray references)
                        {
                            for (var i = 0; i < references.Count; i++)
                            {
                                if (references[i] is JsonValue element && element.TryGetValue<string>(out var elementText))
                                {
                                    references[i] = RewriteOrigin(elementText, publicOrigin, cloud);
                                }
                            }

                            continue;
                        }
                    }

                    RewriteODataIds(pair.Value, cloud);
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    RewriteODataIds(item, cloud);
                }

                break;
        }
    }

    private static bool IsReferenceKey(string key)
    {
        return key.EndsWith("@odata.id", StringComparison.OrdinalIgnoreCase) || key.EndsWith("@odata.bind", StringComparison.OrdinalIgnoreCase);
    }

    private static string RewriteOrigin(string text, string publicOrigin, GraphCloud cloud)
    {
        return text.StartsWith(publicOrigin, StringComparison.OrdinalIgnoreCase) ? cloud.GraphOrigin + "/" + text[publicOrigin.Length..] : text;
    }

    internal static void ValidateHeaders(IReadOnlyDictionary<string, string> headers)
    {
        foreach (var pair in headers)
        {
            if (ForbiddenRequestHeaders.Contains(pair.Key))
            {
                throw new ArgumentException($"The '{pair.Key}' header is managed by the client and cannot be supplied by a request.", nameof(headers));
            }

            if (pair.Key.Length == 0 || pair.Key.AsSpan().IndexOfAny("\r\n:") >= 0 || pair.Value.AsSpan().IndexOfAny('\r', '\n') >= 0)
            {
                throw new ArgumentException($"The header '{pair.Key}' is not a valid HTTP header.", nameof(headers));
            }
        }
    }

    internal static HttpRequestMessage BuildMessage(string method, Uri uri, IReadOnlyDictionary<string, string> headers, string? payload, GraphToken token, string clientRequestId, string userAgent)
    {
        var message = new HttpRequestMessage(new HttpMethod(method), uri);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        message.Headers.TryAddWithoutValidation("Accept", "application/json");
        message.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        message.Headers.TryAddWithoutValidation("client-request-id", clientRequestId);
        message.Headers.TryAddWithoutValidation("return-client-request-id", "true");

        string? contentType = null;
        foreach (var pair in headers)
        {
            if (string.Equals(pair.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                contentType = pair.Value;
                continue;
            }

            if (IgnoredRequestHeaders.Contains(pair.Key))
            {
                continue;
            }

            if (string.Equals(pair.Key, "Accept", StringComparison.OrdinalIgnoreCase))
            {
                message.Headers.Remove("Accept");
            }

            message.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
        }

        if (payload is not null)
        {
            message.Content = new StringContent(payload, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), "application/json");
            if (contentType is not null && MediaTypeHeaderValue.TryParse(contentType, out var parsed))
            {
                message.Content.Headers.ContentType = parsed;
            }
        }

        return message;
    }
}
