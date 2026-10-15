using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using EArm.Graph;

namespace EArm.TestSupport;

/// <summary>
/// Answers real HTTP requests from a <see cref="FakeDirectory"/>: the URL is split back into the path (still percent-encoded, as
/// <see cref="FakeGraphClient"/> receives it) and the decoded query, the body is parsed, and the directory's answer (or the
/// <see cref="GraphException"/> it throws) becomes a status, a JSON body and an error document. <c>POST /$batch</c> is served by running each sub-request.
/// Used by <see cref="ScriptedGraphHandler.Directory"/> so a CLI test can run a whole deployment through the real HTTP client.
/// </summary>
public static class FakeDirectoryHttp
{
    private const int PageSize = 100;

    public static HttpResponseMessage Respond(FakeDirectory directory, RecordedHttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(request);

        var path = request.Uri.AbsolutePath;
        var api = path.StartsWith("/beta/", StringComparison.OrdinalIgnoreCase) ? "/beta" : "/v1.0";
        var relative = path[api.Length..];
        var body = string.IsNullOrWhiteSpace(request.Body) ? null : JsonNode.Parse(request.Body);

        if (request.Method == "POST" && relative.Equals("/$batch", StringComparison.Ordinal))
        {
            return Json(HttpStatusCode.OK, Batch(directory, body as JsonObject));
        }

        var (status, answer) = Invoke(directory, request.Method, relative, request.Uri.Query, request.Headers, body);
        return Json(status, answer);
    }

    private static JsonNode Batch(FakeDirectory directory, JsonObject? envelope)
    {
        var responses = new JsonArray();
        foreach (var item in envelope?["requests"]?.AsArray() ?? [])
        {
            var sub = item as JsonObject;
            var url = sub?["url"]?.GetValue<string>() ?? "/";
            var split = url.IndexOf('?', StringComparison.Ordinal);
            var subPath = split < 0 ? url : url[..split];
            var subQuery = split < 0 ? string.Empty : url[split..];
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in sub?["headers"] as JsonObject ?? [])
            {
                headers[pair.Key] = pair.Value?.GetValue<string>() ?? string.Empty;
            }

            var (status, answer) = Invoke(directory, sub?["method"]?.GetValue<string>() ?? "GET", subPath, subQuery, headers, sub?["body"]);
            var entry = new JsonObject { ["id"] = sub?["id"]?.DeepClone(), ["status"] = (int)status };
            if (answer is not null)
            {
                entry["body"] = answer;
            }

            responses.Add(entry);
        }

        return new JsonObject { ["responses"] = responses };
    }

    private static (HttpStatusCode Status, JsonNode? Body) Invoke(FakeDirectory directory, string method, string path, string query, IReadOnlyDictionary<string, string> headers, JsonNode? body)
    {
        try
        {
            return directory.Handle(method.ToUpperInvariant(), path, ParseQuery(query), headers, body, RetryHint.Default, PageSize);
        }
        catch (GraphException ex)
        {
            return (ex.Status, new JsonObject { ["error"] = new JsonObject { ["code"] = ex.Code, ["message"] = ex.GraphMessage } });
        }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (query.Length <= 1)
        {
            return parameters;
        }

        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=', StringComparison.Ordinal);
            var key = Uri.UnescapeDataString(separator < 0 ? part : part[..separator]);
            parameters[key] = separator < 0 ? string.Empty : Uri.UnescapeDataString(part[(separator + 1)..]);
        }

        return parameters;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, JsonNode? body)
    {
        var response = new HttpResponseMessage(status);
        if (body is not null && status != HttpStatusCode.NoContent)
        {
            response.Content = new StringContent(body.ToJsonString(), new UTF8Encoding(false), "application/json");
        }

        return response;
    }
}
