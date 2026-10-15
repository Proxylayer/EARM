using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EArm.Graph;

/// <summary>
/// A Microsoft Graph request: a path relative to the API base (never carrying a query string), the query
/// parameters (percent-encoded by the transport per RFC 3986), headers, the advanced-query flag
/// (adds <c>ConsistencyLevel: eventual</c>) and the retry hint.
/// </summary>
public sealed record GraphRequest
{
    public GraphRequest(
        string path,
        GraphApi api = GraphApi.V1,
        IReadOnlyDictionary<string, string>? headers = null,
        bool advanced = false,
        RetryHint hint = RetryHint.Default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Contains('?', StringComparison.Ordinal))
        {
            throw new ArgumentException("Graph paths never carry a query string; use WithSelect/WithFilter/WithParameter so the transport encodes the query.", nameof(path));
        }

        Path = path.StartsWith('/') ? path : "/" + path;
        Api = api;
        Headers = headers;
        Advanced = advanced;
        Hint = hint;
    }

    /// <summary>Path relative to the API base, always starting with <c>/</c>, e.g. <c>/applications(uniqueName='x')</c>.</summary>
    public string Path { get; init; }

    public GraphApi Api { get; init; }

    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>Advanced query: adds <c>ConsistencyLevel: eventual</c>. Callers add <c>$count=true</c> via <see cref="WithCount"/> when needed.</summary>
    public bool Advanced { get; init; }

    public RetryHint Hint { get; init; }

    /// <summary>
    /// Whether the request may be repeated blindly after a failure that could have followed a commit (timeout, dropped connection, 5xx).
    /// Null (the default) decides by method: GET, HEAD, PUT, DELETE and PATCH are idempotent by HTTP semantics; POST is not. Set <c>true</c> on a POST
    /// that is safe to repeat (an action such as <c>/restore</c>, a POST whose body carries an idempotency key), or <c>false</c> on a PUT/PATCH
    /// that is not (an append). A non-idempotent request is still retried after a 429 (rejected before processing), a replication-lag 404/400 and
    /// a 401 token refresh, but a timeout, a dropped connection or a 5xx surfaces as an <see cref="GraphTransportException.Ambiguous"/> /
    /// <see cref="GraphException.IsAmbiguous"/> failure so the caller can re-read by key.
    /// </summary>
    public bool? Idempotent { get; init; }

    /// <summary>The method-based default of <see cref="Idempotent"/>: true for GET, HEAD, PUT, DELETE and PATCH; false for POST and anything unknown.</summary>
    public static bool IsIdempotentMethod(string method)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        return method.ToUpperInvariant() is "GET" or "HEAD" or "PUT" or "DELETE" or "PATCH" or "OPTIONS";
    }

    /// <summary>Whether this request, sent with <paramref name="method"/>, may be repeated blindly: <see cref="Idempotent"/> when set, else the method's default.</summary>
    public bool IsIdempotentFor(string method)
    {
        return Idempotent ?? IsIdempotentMethod(method);
    }

    /// <summary>Marks the request as safe (true) or unsafe (false) to repeat after an ambiguous failure; see <see cref="Idempotent"/>.</summary>
    public GraphRequest WithIdempotent(bool idempotent)
    {
        return this with { Idempotent = idempotent };
    }

    /// <summary>Query parameters in insertion order (keys keep their <c>$</c>).</summary>
    public IReadOnlyDictionary<string, string>? UrlParameters { get; init; }

    public GraphRequest WithSelect(params string[] properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        return WithParameter("$select", string.Join(',', properties));
    }

    public GraphRequest WithFilter(string filter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filter);
        return WithParameter("$filter", filter);
    }

    public GraphRequest WithTop(int top)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(top, 1);
        return WithParameter("$top", top.ToString(CultureInfo.InvariantCulture));
    }

    public GraphRequest WithExpand(string expand)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expand);
        return WithParameter("$expand", expand);
    }

    public GraphRequest WithOrderBy(string orderBy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderBy);
        return WithParameter("$orderby", orderBy);
    }

    public GraphRequest WithSearch(string search)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(search);
        return WithParameter("$search", search);
    }

    /// <summary><c>$count=true</c>; usually paired with <see cref="WithConsistencyLevelEventual"/>.</summary>
    public GraphRequest WithCount()
    {
        return WithParameter("$count", "true");
    }

    public GraphRequest WithParameter(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (UrlParameters is not null)
        {
            foreach (var pair in UrlParameters)
            {
                parameters[pair.Key] = pair.Value;
            }
        }

        parameters[name] = value;
        return this with { UrlParameters = parameters };
    }

    public GraphRequest WithHeader(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Headers is not null)
        {
            foreach (var pair in Headers)
            {
                headers[pair.Key] = pair.Value;
            }
        }

        headers[name] = value;
        return this with { Headers = headers };
    }

    /// <summary>Adds a <c>Prefer</c> directive (e.g. <c>create-if-missing</c>, <c>return=representation</c>), merging with an existing one.</summary>
    public GraphRequest WithPrefer(string directive)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directive);
        var existing = TryGetHeader(Headers, "Prefer", out var current) ? current : null;
        var merged = string.IsNullOrEmpty(existing) ? directive : existing + ", " + directive;
        return WithHeader("Prefer", merged);
    }

    /// <summary>
    /// Looks a header up by name ignoring case (HTTP header names are case-insensitive, but a caller-supplied dictionary may
    /// not be), so a <c>prefer</c> or <c>consistencylevel</c> spelling is honoured exactly like <c>Prefer</c>.
    /// </summary>
    internal static bool TryGetHeader(IReadOnlyDictionary<string, string>? headers, string name, out string value)
    {
        if (headers is not null)
        {
            foreach (var pair in headers)
            {
                if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = pair.Value;
                    return true;
                }
            }
        }

        value = string.Empty;
        return false;
    }

    public GraphRequest WithConsistencyLevelEventual()
    {
        return this with { Advanced = true };
    }

    public GraphRequest WithHint(RetryHint hint)
    {
        return this with { Hint = hint };
    }

    public GraphRequest WithApi(GraphApi api)
    {
        return this with { Api = api };
    }

    /// <summary>Headers actually sent: <see cref="Headers"/> plus <c>ConsistencyLevel: eventual</c> when <see cref="Advanced"/>.</summary>
    public IReadOnlyDictionary<string, string> EffectiveHeaders()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Headers is not null)
        {
            foreach (var pair in Headers)
            {
                headers[pair.Key] = pair.Value;
            }
        }

        if (Advanced && !headers.ContainsKey("ConsistencyLevel"))
        {
            headers["ConsistencyLevel"] = "eventual";
        }

        return headers;
    }

    public override string ToString()
    {
        var parameters = UrlParameters is { Count: > 0 }
            ? "?" + string.Join('&', UrlParameters.Select(static p => p.Key + "=" + SecretRedactor.RedactValue(p.Key, p.Value)))
            : string.Empty;
        return Api.BaseUrl() + Path + parameters;
    }
}
