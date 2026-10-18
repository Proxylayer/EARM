using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Engine.Execution;
using EArm.Engine.Planning;
using EArm.Graph;
using Xunit;

namespace EArm.Tests.Engine;

/// <summary>Shared helpers of the review-1A regression tests (adoption, case-only renames, one-time secrets).</summary>
internal static class Review1A
{
    public const string ApiVersion = "2026-10-01-preview";

    public static readonly DateTimeOffset Start = new(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static string AppId(string name)
    {
        return "/providers/Microsoft.Entra/applications/" + name;
    }

    public static string GroupId(string name)
    {
        return "/providers/Microsoft.Entra/groups/" + name;
    }

    public static string Gid(int n)
    {
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"7a000000-0000-4000-8000-{n:D12}");
    }

    public static TemplateDocument Apps(params string[] names)
    {
        return Templates.Resources(string.Join(", ", names.Select(n => "{ \"type\": \"Microsoft.Entra/applications\", \"apiVersion\": \"" + ApiVersion + "\", \"name\": \"" + n + "\", \"properties\": { \"displayName\": \"" + n + "\" } }")));
    }

    public static TemplateDocument Groups(params string[] names)
    {
        return Templates.Resources(string.Join(", ", names.Select(n => "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"" + ApiVersion + "\", \"name\": \"" + n + "\", \"properties\": { \"displayName\": \"" + n + "\", \"mailEnabled\": false, \"securityEnabled\": true } }")));
    }

    public static TemplateDocument Empty()
    {
        return Templates.Resources(string.Empty);
    }

    /// <summary>Plans (Create mode) and executes: what <c>deployment create -y</c> does.</summary>
    public static async Task<(DeploymentPlan Plan, DeploymentResult Result)> ExecuteAsync(
        EngineHost host,
        TemplateDocument document,
        DeploymentMode mode = DeploymentMode.Incremental,
        PlannerOptions? planner = null,
        ExecuteOptions? execute = null)
    {
        var plan = await host.ExpandAsync(document, mode, planner);
        var result = await host.Executor().ExecuteAsync(plan, execute ?? new ExecuteOptions(), Ct);
        return (plan, result);
    }

    public static string IdOf(JsonObject obj)
    {
        return obj["id"]!.GetValue<string>();
    }
}

/// <summary>Redirects GETs of one Graph path to another (two different keys that name one object).</summary>
internal sealed class AliasingGraphClient : GraphClientDecorator
{
    private readonly string _from;
    private readonly string _to;

    public AliasingGraphClient(IGraphClient inner, string from, string to)
        : base(inner)
    {
        _from = from;
        _to = to;
    }

    public override Task<JsonNode?> GetAsync(GraphRequest request, CancellationToken cancellationToken)
    {
        return base.GetAsync(request.Path == _from ? request with { Path = _to } : request, cancellationToken);
    }
}

/// <summary>
/// Simulates a write that Graph committed although the caller never got a usable answer: the inner call runs, then the configured failure is
/// thrown (once per matching write). Every matching attempt is counted.
/// </summary>
internal sealed class CommitThenFailGraphClient : GraphClientDecorator
{
    private readonly Func<GraphRequest, bool> _match;
    private readonly Func<GraphRequest, Exception> _failure;
    private int _remaining;
    private int _attempts;

    public CommitThenFailGraphClient(IGraphClient inner, Func<GraphRequest, bool> match, Func<GraphRequest, Exception> failure, int times = 1)
        : base(inner)
    {
        _match = match;
        _failure = failure;
        _remaining = times;
    }

    public int Attempts => Volatile.Read(ref _attempts);

    public override async Task<GraphResponse> PostAsync(GraphRequest request, JsonNode? body, CancellationToken cancellationToken)
    {
        var response = await base.PostAsync(request, body, cancellationToken);
        MaybeFail(request);
        return response;
    }

    public override async Task<GraphResponse> PatchAsync(GraphRequest request, JsonNode body, CancellationToken cancellationToken)
    {
        var response = await base.PatchAsync(request, body, cancellationToken);
        MaybeFail(request);
        return response;
    }

    private void MaybeFail(GraphRequest request)
    {
        if (!_match(request))
        {
            return;
        }

        Interlocked.Increment(ref _attempts);
        if (Interlocked.Decrement(ref _remaining) >= 0)
        {
            throw _failure(request);
        }
    }
}

/// <summary>On the first PATCH (the upsert of a uniqueName type) a concurrent creator wins: the object appears and Graph answers "already exists".</summary>
internal sealed class RacingApplicationClient : GraphClientDecorator
{
    private readonly Action _concurrentCreate;
    private int _raced;

    public RacingApplicationClient(IGraphClient inner, Action concurrentCreate)
        : base(inner)
    {
        _concurrentCreate = concurrentCreate;
    }

    public override Task<GraphResponse> PatchAsync(GraphRequest request, JsonNode body, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _raced, 1) == 0)
        {
            _concurrentCreate();
            throw new GraphException(System.Net.HttpStatusCode.BadRequest, "Request_BadRequest", "Another object with the same value for property uniqueName already exists.", request.Hint);
        }

        return base.PatchAsync(request, body, cancellationToken);
    }
}
