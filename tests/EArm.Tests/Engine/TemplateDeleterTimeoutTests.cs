using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EArm.Engine.Execution;
using EArm.Graph;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static EArm.Tests.Engine.Review1A;

namespace EArm.Tests.Engine;

/// <summary>
/// <c>deployment delete</c>: a delete that ends with a cancellation the caller did not ask for (an HTTP timeout) fails that object only, with the
/// timeout text; the run is not reported cancelled and, under <c>--continue-on-error</c>, the next object is still deleted.
/// </summary>
public sealed class TemplateDeleterTimeoutTests
{
    [Fact]
    public async Task ADeleteThatTimesOut_FailsThatObjectOnly_AndTheRunGoesOn()
    {
        var (host, timingOut) = Seed();
        var deleter = new TemplateDeleter(host.Registry, new TimingOutDeleteClient(host.Graph, timingOut), NullLogger<TemplateDeleter>.Instance);
        var plan = await host.WhatIfAsync(Groups("g1", "g2"));
        var targets = await deleter.FindDeclaredObjectsAsync(plan, Ct);

        var result = await deleter.DeleteAsync(plan, targets, continueOnError: true, Ct);

        Assert.False(result.Cancelled);
        Assert.False(result.Succeeded);
        var failed = Assert.Single(result.Outcomes, o => o.Status == DeletionStatus.Failed);
        Assert.Equal(timingOut, failed.Target.GraphId);
        Assert.Contains("timed out or was cancelled", failed.Error, StringComparison.Ordinal);
        Assert.Equal(EArm.Engine.EngineDiagnosticCodes.ResourceDeleted, failed.ErrorCode);
        Assert.Single(result.Outcomes, o => o.Status == DeletionStatus.Deleted);
        Assert.Equal(1, host.Directory.Count("/groups"));
    }

    [Fact]
    public async Task ADeleteThatTimesOut_WithoutContinueOnError_SkipsTheRest_ButIsNotACancellation()
    {
        var (host, timingOut) = Seed();
        var deleter = new TemplateDeleter(host.Registry, new TimingOutDeleteClient(host.Graph, timingOut), NullLogger<TemplateDeleter>.Instance);
        var plan = await host.WhatIfAsync(Groups("g1", "g2"));
        var targets = (await deleter.FindDeclaredObjectsAsync(plan, Ct)).OrderBy(t => t.GraphId == timingOut ? 0 : 1).ToList();

        var result = await deleter.DeleteAsync(plan, targets, continueOnError: false, Ct);

        Assert.False(result.Cancelled);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(0, result.DeletedCount);
        Assert.Equal(2, host.Directory.Count("/groups"));
    }

    private static (EngineHost Host, string TimingOutId) Seed()
    {
        var host = new EngineHost();
        var first = host.Directory.AddGroup("g1", "g1", g => { g["mailEnabled"] = false; g["securityEnabled"] = true; });
        host.Directory.AddGroup("g2", "g2", g => { g["mailEnabled"] = false; g["securityEnabled"] = true; });
        return (host, IdOf(first));
    }

    /// <summary>Answers the DELETE of one object with the <see cref="TaskCanceledException"/> an <c>HttpClient</c> timeout throws.</summary>
    private sealed class TimingOutDeleteClient : GraphClientDecorator
    {
        private readonly string _graphId;

        public TimingOutDeleteClient(IGraphClient inner, string graphId)
            : base(inner)
        {
            _graphId = graphId;
        }

        public override Task DeleteAsync(GraphRequest request, CancellationToken cancellationToken)
        {
            return request.Path.Contains(_graphId, StringComparison.OrdinalIgnoreCase)
                ? throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.", new TimeoutException())
                : base.DeleteAsync(request, cancellationToken);
        }
    }
}
