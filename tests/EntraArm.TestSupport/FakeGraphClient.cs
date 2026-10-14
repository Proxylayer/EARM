using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EntraArm.Az;

namespace EntraArm.TestSupport;

/// <summary>
/// An <see cref="IGraphClient"/> over an in-memory <see cref="FakeDirectory"/>: alternate-key routing
/// (<c>applications(uniqueName='x')</c>, <c>servicePrincipals(appId='x')</c>, <c>users/{upn}</c>), <c>$filter</c>,
/// <c>Prefer: create-if-missing</c>, pagination, <c>$batch</c>, scripted failures and a request log. It does not
/// retry: wrap engine calls in a <see cref="RetryPolicy"/> (with a fake delay provider) to exercise retries.
/// Like <see cref="AzGraphClient"/>, <see cref="GetAsync"/> returns <c>null</c> for a 404 unless the request expects
/// replication lag, in which case the 404 is thrown (and would be retried by the policy).
/// </summary>
public sealed class FakeGraphClient : IGraphClient
{
    private readonly object _gate = new();
    private readonly List<FakeGraphRequest> _requests = [];

    public FakeGraphClient()
        : this(new FakeDirectory())
    {
    }

    public FakeGraphClient(FakeDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        Directory = directory;
    }

    public FakeDirectory Directory { get; }

    public FakeFailureScript Failures { get; } = new();

    /// <summary>Items per page for collection GETs (a small value exercises pagination).</summary>
    public int PageSize { get; set; } = 100;

    /// <summary>Every call received, in order.</summary>
    public IReadOnlyList<FakeGraphRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToList();
            }
        }
    }

    public IReadOnlyList<FakeGraphRequest> Writes => Requests.Where(r => r.IsWrite).ToList();

    public void ClearRequests()
    {
        lock (_gate)
        {
            _requests.Clear();
        }
    }

    // ---- IGraphClient ----

    public Task<JsonNode?> GetAsync(GraphRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var (_, body) = Send("GET", request, null, cancellationToken);
            return Task.FromResult(body);
        }
        catch (GraphException ex) when (ex.IsNotFound && !ex.IsReplication)
        {
            return Task.FromResult<JsonNode?>(null);
        }
    }

    public Task<GraphResponse> PostAsync(GraphRequest request, JsonNode? body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (status, responseBody) = Send("POST", request, body, cancellationToken);
        return Task.FromResult(new GraphResponse(status, responseBody));
    }

    public Task<GraphResponse> PatchAsync(GraphRequest request, JsonNode body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(body);
        var (status, responseBody) = Send("PATCH", request, body, cancellationToken);
        return Task.FromResult(new GraphResponse(status, responseBody));
    }

    public Task<GraphResponse> PutAsync(GraphRequest request, JsonNode body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(body);
        var (status, responseBody) = Send("PUT", request, body, cancellationToken);
        return Task.FromResult(new GraphResponse(status, responseBody));
    }

    public Task DeleteAsync(GraphRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Send("DELETE", request, null, cancellationToken);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<JsonNode> GetAllPagesAsync(GraphRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var current = request;
        while (true)
        {
            var (_, body) = Send("GET", current, null, cancellationToken);
            if (body is not JsonObject page)
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
                yield return page;
                yield break;
            }

            string? nextLink = page["@odata.nextLink"] is JsonValue link && link.TryGetValue<string>(out var text) ? text : null;
            if (string.IsNullOrEmpty(nextLink))
            {
                yield break;
            }

            current = FollowNextLink(current, nextLink);
            await Task.Yield();
        }
    }

    public Task<IReadOnlyList<BatchResponse>> BatchAsync(IReadOnlyList<BatchRequest> requests, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var results = new List<BatchResponse>(requests.Count);
        for (int i = 0; i < requests.Count; i++)
        {
            var item = requests[i];
            string id = item.Id ?? (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var request = new GraphRequest(item.Path, api, item.Headers, advanced: false, hint) { UrlParameters = item.UrlParameters };
            try
            {
                var (status, body) = Send(item.Method.ToUpperInvariant(), request, item.Body, cancellationToken);
                results.Add(new BatchResponse(id, item, status, body, null));
            }
            catch (GraphException ex)
            {
                results.Add(new BatchResponse(id, item, ex.Status, null, ex));
            }
        }

        Log(new FakeGraphRequest("POST", "/$batch", api, null, null, null, hint));
        return Task.FromResult<IReadOnlyList<BatchResponse>>(results);
    }

    // ---- dispatch ----

    private (HttpStatusCode Status, JsonNode? Body) Send(string method, GraphRequest request, JsonNode? body, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var logged = new FakeGraphRequest(method, request.Path, request.Api, request.UrlParameters, request.EffectiveHeaders(), body?.DeepClone(), request.Hint);
        Log(logged);

        if (Failures.Match(logged) is { } scripted)
        {
            throw scripted;
        }

        return Directory.Handle(method, request.Path, request.UrlParameters, request.EffectiveHeaders(), body, request.Hint, PageSize);
    }

    private void Log(FakeGraphRequest request)
    {
        lock (_gate)
        {
            _requests.Add(request);
        }
    }

    /// <summary>The same split the real client does for a next link: path back to relative form, query into url parameters.</summary>
    private static GraphRequest FollowNextLink(GraphRequest current, string nextLink)
    {
        int query = nextLink.IndexOf('?', StringComparison.Ordinal);
        string urlPart = query < 0 ? nextLink : nextLink[..query];
        string queryPart = query < 0 ? string.Empty : nextLink[(query + 1)..];

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in queryPart.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = pair.IndexOf('=', StringComparison.Ordinal);
            string key = Uri.UnescapeDataString(separator < 0 ? pair : pair[..separator]);
            string value = separator < 0 ? string.Empty : Uri.UnescapeDataString(pair[(separator + 1)..]);
            parameters[key] = value;
        }

        string basePrefix = current.Api.BaseUrl() + "/";
        string path = urlPart.StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase) ? "/" + urlPart[basePrefix.Length..] : urlPart;
        return new GraphRequest(path, current.Api, current.Headers, current.Advanced, current.Hint) { UrlParameters = parameters.Count == 0 ? null : parameters };
    }
}
