using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EntraArm.Az;
using EntraArm.TestSupport;

namespace EntraArm.Providers.Tests.Infrastructure;

/// <summary>
/// Forwards every call to the fake, but lets a test replace the answer of POSTs (<see cref="PostOverride"/>: return
/// <c>null</c> to forward) and observe each POST body: for malformed-response tests the fake cannot produce.
/// </summary>
internal sealed class InterceptingGraphClient(FakeGraphClient inner) : IGraphClient
{
    /// <summary>When set and returning a response, replaces the real POST (the request is not forwarded).</summary>
    public Func<GraphRequest, JsonNode?, GraphResponse?>? PostOverride { get; set; }

    public Task<JsonNode?> GetAsync(GraphRequest request, CancellationToken cancellationToken)
    {
        return inner.GetAsync(request, cancellationToken);
    }

    public Task<GraphResponse> PostAsync(GraphRequest request, JsonNode? body, CancellationToken cancellationToken)
    {
        if (PostOverride?.Invoke(request, body) is { } replaced)
        {
            return Task.FromResult(replaced);
        }

        return inner.PostAsync(request, body, cancellationToken);
    }

    /// <summary>When set and returning a response, replaces the real PATCH (the request is not forwarded).</summary>
    public Func<GraphRequest, JsonNode, GraphResponse?>? PatchOverride { get; set; }

    /// <summary>Forward PATCHes to the fake but answer 204 without the body the fake produced (an upsert that returns nothing).</summary>
    public bool DropPatchBodies { get; set; }

    public async Task<GraphResponse> PatchAsync(GraphRequest request, JsonNode body, CancellationToken cancellationToken)
    {
        if (PatchOverride?.Invoke(request, body) is { } replaced)
        {
            return replaced;
        }

        var response = await inner.PatchAsync(request, body, cancellationToken);
        return DropPatchBodies ? new GraphResponse(System.Net.HttpStatusCode.NoContent, null) : response;
    }

    public Task<GraphResponse> PutAsync(GraphRequest request, JsonNode body, CancellationToken cancellationToken)
    {
        return inner.PutAsync(request, body, cancellationToken);
    }

    public Task DeleteAsync(GraphRequest request, CancellationToken cancellationToken)
    {
        return inner.DeleteAsync(request, cancellationToken);
    }

    public IAsyncEnumerable<JsonNode> GetAllPagesAsync(GraphRequest request, CancellationToken cancellationToken)
    {
        return inner.GetAllPagesAsync(request, cancellationToken);
    }

    public Task<IReadOnlyList<BatchResponse>> BatchAsync(IReadOnlyList<BatchRequest> requests, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        return inner.BatchAsync(requests, api, hint, cancellationToken);
    }
}
