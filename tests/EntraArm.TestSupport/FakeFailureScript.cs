using System;
using System.Collections.Generic;
using System.Net;
using EntraArm.Az;

namespace EntraArm.TestSupport;

/// <summary>
/// Scripted failures for <see cref="FakeGraphClient"/>: a rule matches a method (or <c>*</c>) and a path fragment
/// and fires a given number of times before the fake behaves normally again. Replication-lag rules throw the 404
/// Graph returns for a not-yet-replicated object, carrying the request's own retry hint so the exception is
/// classified exactly as the real client would classify it.
/// </summary>
public sealed class FakeFailureScript
{
    private readonly object _gate = new();
    private readonly List<Rule> _rules = [];

    private sealed class Rule(string method, string pathContains, Func<FakeGraphRequest, Exception> factory, int times)
    {
        public string Method { get; } = method;

        public string PathContains { get; } = pathContains;

        public Func<FakeGraphRequest, Exception> Factory { get; } = factory;

        public int Remaining { get; set; } = times;
    }

    /// <summary>Adds a rule; <paramref name="method"/> <c>*</c> matches every method; <paramref name="times"/> <c>-1</c> never expires.</summary>
    public void Add(string method, string pathContains, Func<FakeGraphRequest, Exception> factory, int times = 1)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(pathContains);
        ArgumentNullException.ThrowIfNull(factory);
        lock (_gate)
        {
            _rules.Add(new Rule(method, pathContains, factory, times));
        }
    }

    /// <summary>404 <c>Request_ResourceNotFound</c> N times (replication lag), then normal.</summary>
    public void ReplicationLag(string pathContains, int times = 1, string method = "*")
    {
        Add(method, pathContains, request => new GraphException(
            HttpStatusCode.NotFound,
            "Request_ResourceNotFound",
            $"Resource '{request.Path}' does not exist or one of its queried reference-property objects are not present.",
            request.Hint), times);
    }

    /// <summary>400 with the "does not reference a valid application object" replication message N times.</summary>
    public void ReplicationBadRequest(string pathContains, int times = 1, string method = "POST")
    {
        Add(method, pathContains, request => new GraphException(
            HttpStatusCode.BadRequest,
            "Request_BadRequest",
            "The appId '…' of the service principal does not reference a valid application object.",
            request.Hint), times);
    }

    /// <summary>429 N times, optionally with a Retry-After.</summary>
    public void Throttle(string pathContains, int times = 1, TimeSpan? retryAfter = null, string method = "*")
    {
        Add(method, pathContains, request => new GraphException(
            HttpStatusCode.TooManyRequests,
            "TooManyRequests",
            "Too many requests; retry after a while.",
            request.Hint,
            retryAfter: retryAfter), times);
    }

    /// <summary>403 <c>Authorization_RequestDenied</c> (never expires unless <paramref name="times"/> is given).</summary>
    public void Forbid(string pathContains, string method = "*", int times = -1)
    {
        Add(method, pathContains, request => new GraphException(
            HttpStatusCode.Forbidden,
            "Authorization_RequestDenied",
            "Insufficient privileges to complete the operation.",
            request.Hint), times);
    }

    /// <summary>503 N times.</summary>
    public void ServerError(string pathContains, int times = 1, string method = "*")
    {
        Add(method, pathContains, request => new GraphException(
            HttpStatusCode.ServiceUnavailable,
            "ServiceUnavailable",
            "The service is temporarily unavailable.",
            request.Hint), times);
    }

    /// <summary>400 "already exists" N times (idempotent-success class).</summary>
    public void AlreadyExists(string pathContains, string method = "POST", int times = 1)
    {
        Add(method, pathContains, request => new GraphException(
            HttpStatusCode.BadRequest,
            "Request_BadRequest",
            "Another object with the same value for property uniqueName already exists.",
            request.Hint), times);
    }

    /// <summary>A transient Azure CLI failure (no Graph body) N times.</summary>
    public void AzFailure(string pathContains, int times = 1, string method = "*")
    {
        Add(method, pathContains, _ => new AzCliException(AzCliFailure.Transient, "az rest: transient Azure CLI failure (exit 1). Connection reset by peer."), times);
    }

    /// <summary>The exception to throw for this request, or <c>null</c>; a matching rule's counter is consumed.</summary>
    internal Exception? Match(FakeGraphRequest request)
    {
        lock (_gate)
        {
            for (int i = 0; i < _rules.Count; i++)
            {
                var rule = _rules[i];
                if (rule.Method != "*" && !rule.Method.Equals(request.Method, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!request.Path.Contains(rule.PathContains, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (rule.Remaining > 0)
                {
                    rule.Remaining--;
                    if (rule.Remaining == 0)
                    {
                        _rules.RemoveAt(i);
                    }
                }

                return rule.Factory(request);
            }
        }

        return null;
    }

    public int PendingRules
    {
        get
        {
            lock (_gate)
            {
                return _rules.Count;
            }
        }
    }
}
