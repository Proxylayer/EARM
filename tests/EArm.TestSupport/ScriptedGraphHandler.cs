using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace EArm.TestSupport;

/// <summary>One request that reached <see cref="ScriptedGraphHandler"/>: method, URL (with the query), headers and body text.</summary>
public sealed record RecordedHttpRequest(string Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string? Body)
{
    /// <summary><c>GET /v1.0/applications/x?$select=id</c>, percent-decoded: the form the CLI tests assert on.</summary>
    public string Command => Method + " " + Uri.UnescapeDataString(Uri.PathAndQuery);

    public override string ToString()
    {
        return Command;
    }
}

/// <summary>
/// An <see cref="HttpMessageHandler"/> for in-process CLI tests: it records every request and answers from a script of rules.
/// A rule has an optional match (method, a substring of the percent-decoded URL, a substring of the body), a status, an optional JSON
/// body and optional headers; the first not yet used rule that matches a request answers it (a <c>sticky</c> rule is never used up).
/// When no rule is left the handler throws, so a mis-scripted test fails loudly instead of retrying forever. The script is replaced with
/// <see cref="SetScript"/>; the recorded requests accumulate across scripts.
/// <code>
/// { "responses": [
///   { "match": { "method": "GET", "urlContains": "/organization" }, "status": 200, "body": { "value": [ { "id": "..." } ] }, "sticky": true },
///   { "match": { "method": "PATCH" }, "status": 204 },
///   { "match": { "urlContains": "/groups/" }, "status": 404, "body": { "error": { "code": "Request_ResourceNotFound", "message": "gone" } } }
/// ] }
/// </code>
/// A rule without <c>status</c> answers 200 with its body, or 204 without one.
/// <para>
/// Every run reads the tenant size right after its preflight (<c>GET /users/$count</c>). A request for it that no rule answers (and every one while
/// <see cref="Directory"/> is set) gets <see cref="UserCount"/> as Graph sends it, a plain-text number, so scripts need not mention it; a rule that
/// matches it (for example a 403) still wins over the default. Like Graph, the default answers <c>415 Request_BadRequest</c> when the request's
/// <c>Accept</c> does not allow <c>text/plain</c> (no <c>Accept</c>, <c>text/*</c> and <c>*/*</c> do).
/// </para>
/// </summary>
public sealed class ScriptedGraphHandler : HttpMessageHandler
{
    private sealed class Rule
    {
        public string? Method { get; init; }

        public string? UrlContains { get; init; }

        public string? BodyContains { get; init; }

        public int Status { get; init; }

        public string? Body { get; init; }

        public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public bool Sticky { get; init; }

        public bool Used { get; set; }

        public bool Matches(RecordedHttpRequest request)
        {
            return (Method is null || string.Equals(Method, request.Method, StringComparison.OrdinalIgnoreCase))
                && (UrlContains is null || Uri.UnescapeDataString(request.Uri.AbsoluteUri).Contains(UrlContains, StringComparison.OrdinalIgnoreCase))
                && (BodyContains is null || (request.Body ?? string.Empty).Contains(BodyContains, StringComparison.Ordinal));
        }
    }

    private readonly object _gate = new();
    private readonly List<RecordedHttpRequest> _requests = [];
    private List<Rule> _rules = [];

    /// <summary>The answer to <c>GET /users/$count</c> when no rule matches it; null removes the default (the request is then unscripted).</summary>
    public long? UserCount { get; set; } = DefaultUserCount;

    /// <summary>The default <see cref="UserCount"/>: a small tenant.</summary>
    public const long DefaultUserCount = 10;

    /// <summary>When set, every request is answered by this in-memory directory (see <see cref="FakeDirectoryHttp"/>) instead of the script; requests are still recorded.</summary>
    public FakeDirectory? Directory { get; set; }

    public IReadOnlyList<RecordedHttpRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>Every request as <c>METHOD /path?query</c> (decoded), in arrival order.</summary>
    public IReadOnlyList<string> Commands => [.. Requests.Select(static r => r.Command)];

    /// <summary>Replaces the script; the recorded requests are kept.</summary>
    public void SetScript(string fixtureJson)
    {
        ArgumentNullException.ThrowIfNull(fixtureJson);
        var rules = new List<Rule>();
        var root = JsonNode.Parse(fixtureJson) as JsonObject ?? throw new ArgumentException("The script is not a JSON object.", nameof(fixtureJson));
        foreach (var node in root["responses"]?.AsArray() ?? [])
        {
            var entry = node as JsonObject ?? throw new ArgumentException("A response is not a JSON object.", nameof(fixtureJson));
            var match = entry["match"] as JsonObject;
            var body = entry["body"];
            var status = entry["status"] is JsonValue statusValue && statusValue.TryGetValue<int>(out var code) ? code : (body is null ? 204 : 200);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (entry["headers"] is JsonObject headerObject)
            {
                foreach (var pair in headerObject)
                {
                    headers[pair.Key] = pair.Value?.GetValue<string>() ?? string.Empty;
                }
            }

            rules.Add(new Rule
            {
                Method = match?["method"]?.GetValue<string>(),
                UrlContains = match?["urlContains"]?.GetValue<string>(),
                BodyContains = match?["bodyContains"]?.GetValue<string>(),
                Status = status,
                Body = body?.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
                Headers = headers,
                Sticky = entry["sticky"] is JsonValue sticky && sticky.TryGetValue<bool>(out var isSticky) && isSticky,
            });
        }

        lock (_gate)
        {
            _rules = rules;
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers)
        {
            headers[header.Key] = string.Join(", ", header.Value);
        }

        string? body = null;
        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers)
            {
                headers[header.Key] = string.Join(", ", header.Value);
            }

            body = Encoding.UTF8.GetString(await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
        }

        var recorded = new RecordedHttpRequest(request.Method.Method, request.RequestUri!, headers, body);
        Rule? chosen = null;
        lock (_gate)
        {
            _requests.Add(recorded);
            if (Directory is not null && !IsUserCount(recorded))
            {
                var directoryResponse = FakeDirectoryHttp.Respond(Directory, recorded);
                directoryResponse.RequestMessage = request;
                return directoryResponse;
            }

            foreach (var rule in _rules)
            {
                if ((rule.Sticky || !rule.Used) && rule.Matches(recorded))
                {
                    rule.Used = true;
                    chosen = rule;
                    break;
                }
            }
        }

        if (chosen is null && IsUserCount(recorded) && UserCount is { } users)
        {
            if (!AcceptsPlainText(recorded))
            {
                return new HttpResponseMessage(HttpStatusCode.UnsupportedMediaType)
                {
                    RequestMessage = request,
                    Content = new StringContent(Error("Request_BadRequest", "The requested media type is not supported; $count is served only as text/plain."), new UTF8Encoding(false), "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(users.ToString(System.Globalization.CultureInfo.InvariantCulture), new UTF8Encoding(false), "text/plain"),
            };
        }

        if (chosen is null)
        {
            throw new InvalidOperationException("ScriptedGraphHandler: no scripted response left for " + recorded.Command);
        }

        var response = new HttpResponseMessage((HttpStatusCode)chosen.Status) { RequestMessage = request };
        if (chosen.Body is not null)
        {
            response.Content = new StringContent(chosen.Body, new UTF8Encoding(false), "application/json");
        }

        foreach (var header in chosen.Headers)
        {
            response.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return response;
    }

    private static bool IsUserCount(RecordedHttpRequest request)
    {
        return request.Method == "GET" && request.Uri.AbsolutePath.EndsWith("/users/$count", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether the request's <c>Accept</c> allows <c>text/plain</c>: absent, <c>text/plain</c>, <c>text/*</c> or <c>*/*</c> (with any parameters).</summary>
    private static bool AcceptsPlainText(RecordedHttpRequest request)
    {
        if (!request.Headers.TryGetValue("Accept", out var accept) || string.IsNullOrWhiteSpace(accept))
        {
            return true;
        }

        foreach (var range in accept.Split(','))
        {
            var mediaType = range.Split(';')[0].Trim();
            if (mediaType.Equals("text/plain", StringComparison.OrdinalIgnoreCase) || mediaType is "text/*" or "*/*")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A Graph error document, for scripts built in code: <c>{ "error": { "code": ..., "message": ... } }</c>.</summary>
    public static string Error(string code, string message)
    {
        return new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }.ToJsonString();
    }
}
