using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using EArm.Graph.Auth;

namespace EArm.Graph.Tests.Infrastructure;

/// <summary>A token provider that hands out <c>token-1</c>, <c>token-2</c>, ... (a new one after <see cref="Invalidate"/>) and counts the calls.</summary>
internal sealed class FakeTokenProvider : IGraphTokenProvider
{
    private readonly object _gate = new();
    private int _generation = 1;

    public List<string> Scopes { get; } = [];

    public int Invalidations { get; private set; }

    public Exception? Failure { get; set; }

    /// <summary>The token value of the current generation, e.g. <c>token-1</c>.</summary>
    public string Current
    {
        get
        {
            lock (_gate)
            {
                return "token-" + _generation;
            }
        }
    }

    public ValueTask<GraphToken> GetTokenAsync(TokenScopes scope, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Scopes.Add(scope.ToString());
            if (Failure is not null)
            {
                throw Failure;
            }

            return ValueTask.FromResult(new GraphToken("token-" + _generation, DateTimeOffset.UtcNow.AddHours(1)));
        }
    }

    public void Invalidate(TokenScopes scope)
    {
        lock (_gate)
        {
            Invalidations++;
            _generation++;
        }
    }
}

/// <summary>A hand-wound clock.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset start)
    {
        _now = start;
    }

    public override DateTimeOffset GetUtcNow()
    {
        return _now;
    }

    public void Advance(TimeSpan by)
    {
        _now += by;
    }
}

/// <summary>An Azure.Identity credential that returns scripted tokens (or throws) and counts the calls.</summary>
internal sealed class FakeCredential : TokenCredential
{
    private readonly Queue<Func<TokenRequestContext, AccessToken>> _script = new();
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public List<string> Scopes { get; } = [];

    public FakeCredential Returns(string token, DateTimeOffset expiresOn)
    {
        _script.Enqueue(_ => new AccessToken(token, expiresOn));
        return this;
    }

    public FakeCredential Throws(Exception exception)
    {
        _script.Enqueue(_ => throw exception);
        return this;
    }

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        lock (Scopes)
        {
            Scopes.AddRange(requestContext.Scopes);
        }

        return _script.Dequeue()(requestContext);
    }

    public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        await Task.Yield();
        return GetToken(requestContext, cancellationToken);
    }
}
