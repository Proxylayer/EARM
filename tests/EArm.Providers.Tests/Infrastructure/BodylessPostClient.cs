using System.Collections.Generic;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.TestSupport;

namespace EArm.Providers.Tests.Infrastructure;

/// <summary>A Graph client whose POST answers 201 with no body (the object is created, or not, behind the scenes) and forwards everything else to the fake.</summary>
internal sealed class BodylessPostClient(FakeGraphClient inner, bool createIt = true) : IGraphClient
{
    public Task<JsonNode?> GetAsync(GraphRequest request, CancellationToken cancellationToken)
    {
        return inner.GetAsync(request, cancellationToken);
    }

    public async Task<GraphResponse> PostAsync(GraphRequest request, JsonNode? body, CancellationToken cancellationToken)
    {
        if (createIt)
        {
            await inner.PostAsync(request, body, cancellationToken);
        }

        return new GraphResponse(HttpStatusCode.Created, null);
    }

    public Task<GraphResponse> PatchAsync(GraphRequest request, JsonNode body, CancellationToken cancellationToken)
    {
        return inner.PatchAsync(request, body, cancellationToken);
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
