using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EntraArm.Az;

/// <summary>
/// A Microsoft Graph request: a path relative to the API base (never carrying a query string), the query
/// parameters (sent as <c>--url-parameters</c> so az URL-encodes them), headers, the advanced-query flag
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
            throw new ArgumentException("Graph paths never carry a query string; use WithSelect/WithFilter/WithParameter so the query goes through --url-parameters.", nameof(path));
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

    /// <summary>Query parameters in insertion order (keys keep their <c>$</c>).</summary>
    public IReadOnlyDictionary<string, string>? UrlParameters { get; init; }

    /// <summary>Set for pagination follow-ups whose base differs from <see cref="Api"/>; otherwise null.</summary>
    internal string? AbsoluteUrl { get; init; }

    /// <summary>The absolute URL without query string.</summary>
    public string Url => AbsoluteUrl ?? Api.BaseUrl() + Path;

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

    /// <summary>
    /// The request for an <c>@odata.nextLink</c>: the link's query string is split back into url parameters
    /// (decoded, so az re-encodes them exactly once) and headers/hint are re-sent.
    /// </summary>
    internal GraphRequest ForNextLink(string nextLink)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nextLink);
        var query = nextLink.IndexOf('?', StringComparison.Ordinal);
        var urlPart = query < 0 ? nextLink : nextLink[..query];
        var queryPart = query < 0 ? string.Empty : nextLink[(query + 1)..];

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in queryPart.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            var key = DecodeQueryComponent(separator < 0 ? pair : pair[..separator]);
            var value = separator < 0 ? string.Empty : DecodeQueryComponent(pair[(separator + 1)..]);
            parameters[key] = value;
        }

        var basePrefix = Api.BaseUrl() + "/";
        if (urlPart.StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return this with { Path = "/" + urlPart[basePrefix.Length..], AbsoluteUrl = null, UrlParameters = parameters.Count == 0 ? null : parameters };
        }

        return this with { AbsoluteUrl = urlPart, UrlParameters = parameters.Count == 0 ? null : parameters };
    }

    private static string DecodeQueryComponent(string component)
    {
        return Uri.UnescapeDataString(component.Replace('+', ' '));
    }

    public override string ToString()
    {
        var parameters = UrlParameters is { Count: > 0 }
            ? "?" + string.Join('&', UrlParameters.Select(static p => p.Key + "=" + p.Value))
            : string.Empty;
        return Url + parameters;
    }
}
