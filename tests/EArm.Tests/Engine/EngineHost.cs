using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Az;
using EArm.Core;
using EArm.Core.Loading;
using EArm.Core.Types;
using EArm.Engine.Execution;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Engine.State;
using EArm.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using GraphApi = EArm.Az.GraphApi;

namespace EArm.Tests.Engine;

/// <summary>Records every delay the retry policy asks for and returns immediately (no real waiting).</summary>
internal sealed class RecordingDelayProvider : IDelayProvider
{
    private readonly List<TimeSpan> _delays = [];
    private readonly object _gate = new();

    public IReadOnlyList<TimeSpan> Delays
    {
        get
        {
            lock (_gate)
            {
                return [.. _delays];
            }
        }
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _delays.Add(delay);
        }

        return Task.CompletedTask;
    }
}

/// <summary>A forwarding <see cref="IGraphClient"/> for tests that need to observe or intercept calls around the fake.</summary>
internal class GraphClientDecorator : IGraphClient
{
    protected GraphClientDecorator(IGraphClient inner)
    {
        Inner = inner;
    }

    protected IGraphClient Inner { get; }

    public virtual Task<JsonNode?> GetAsync(GraphRequest request, CancellationToken cancellationToken)
    {
        return Inner.GetAsync(request, cancellationToken);
    }

    public virtual Task<GraphResponse> PostAsync(GraphRequest request, JsonNode? body, CancellationToken cancellationToken)
    {
        return Inner.PostAsync(request, body, cancellationToken);
    }

    public virtual Task<GraphResponse> PatchAsync(GraphRequest request, JsonNode body, CancellationToken cancellationToken)
    {
        return Inner.PatchAsync(request, body, cancellationToken);
    }

    public virtual Task<GraphResponse> PutAsync(GraphRequest request, JsonNode body, CancellationToken cancellationToken)
    {
        return Inner.PutAsync(request, body, cancellationToken);
    }

    public virtual Task DeleteAsync(GraphRequest request, CancellationToken cancellationToken)
    {
        return Inner.DeleteAsync(request, cancellationToken);
    }

    public virtual IAsyncEnumerable<JsonNode> GetAllPagesAsync(GraphRequest request, CancellationToken cancellationToken)
    {
        return Inner.GetAllPagesAsync(request, cancellationToken);
    }

    public virtual Task<IReadOnlyList<BatchResponse>> BatchAsync(IReadOnlyList<BatchRequest> requests, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        return Inner.BatchAsync(requests, api, hint, cancellationToken);
    }
}

/// <summary>Counts in-flight Graph calls (holding each one open for a moment) so the planner's parallelism bound can be asserted.</summary>
internal sealed class ConcurrencyGate : GraphClientDecorator
{
    private readonly TimeSpan _hold;
    private int _inFlight;
    private int _max;

    public ConcurrencyGate(IGraphClient inner, TimeSpan hold)
        : base(inner)
    {
        _hold = hold;
    }

    public int MaxObserved => Volatile.Read(ref _max);

    public override async Task<JsonNode?> GetAsync(GraphRequest request, CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken);
        try
        {
            return await Inner.GetAsync(request, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    public override async IAsyncEnumerable<JsonNode> GetAllPagesAsync(GraphRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken);
        try
        {
            await foreach (var item in Inner.GetAllPagesAsync(request, cancellationToken))
            {
                yield return item;
            }
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private async Task EnterAsync(CancellationToken cancellationToken)
    {
        int now = Interlocked.Increment(ref _inFlight);
        int seen;
        do
        {
            seen = Volatile.Read(ref _max);
        }
        while (now > seen && Interlocked.CompareExchange(ref _max, now, seen) != seen);

        await Task.Delay(_hold, cancellationToken);
    }
}

/// <summary>Runs a hook before every POST (for example to simulate a concurrent creator racing the executor).</summary>
internal sealed class InterceptingGraphClient : GraphClientDecorator
{
    public InterceptingGraphClient(IGraphClient inner)
        : base(inner)
    {
    }

    public Func<GraphRequest, JsonNode?, Task>? BeforePost { get; set; }

    public override async Task<GraphResponse> PostAsync(GraphRequest request, JsonNode? body, CancellationToken cancellationToken)
    {
        if (BeforePost is { } hook)
        {
            await hook(request, body);
        }

        return await Inner.PostAsync(request, body, cancellationToken);
    }
}

/// <summary>Records every <see cref="IStateSink"/> call in order: <c>Method:resourceId</c>.</summary>
internal sealed class RecordingStateSink : IStateSink
{
    private readonly List<string> _calls = [];
    private readonly object _gate = new();

    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    public int IndexOf(string call)
    {
        return Calls.ToList().IndexOf(call);
    }

    public Task ResourceStartingAsync(ResourceInstance instance, CancellationToken cancellationToken)
    {
        return Record("Starting:" + instance.Id.Id);
    }

    public Task ResourceAppliedAsync(ResourceInstance instance, ResourceChange change, AppliedResource applied, CancellationToken cancellationToken)
    {
        return Record("Applied:" + instance.Id.Id);
    }

    public Task ResourceFailedAsync(ResourceInstance instance, string error, CancellationToken cancellationToken)
    {
        return Record("Failed:" + instance.Id.Id);
    }

    public Task ResourceSkippedAsync(ResourceInstance instance, string reason, CancellationToken cancellationToken)
    {
        return Record("Skipped:" + instance.Id.Id);
    }

    public Task ResourceIgnoredAsync(ResourceInstance instance, string reason, CancellationToken cancellationToken)
    {
        return Record("Ignored:" + instance.Id.Id);
    }

    public Task ResourceDetachedAsync(StateEntry entry, CancellationToken cancellationToken)
    {
        return Record("Detached:" + entry.Id.Id);
    }

    public Task ResourceDeletedAsync(StateEntry entry, CancellationToken cancellationToken)
    {
        return Record("Deleted:" + entry.Id.Id);
    }

    public Task ResourceDeleteFailedAsync(StateEntry entry, string error, CancellationToken cancellationToken)
    {
        return Record("DeleteFailed:" + entry.Id.Id);
    }

    public Task CompletedAsync(DeploymentStatus status, CancellationToken cancellationToken)
    {
        return Record("Completed:" + status);
    }

    private Task Record(string call)
    {
        lock (_gate)
        {
            _calls.Add(call);
        }

        return Task.CompletedTask;
    }
}

/// <summary>A private directory under the test output folder, deleted on dispose.</summary>
internal sealed class TestDirectory : IDisposable
{
    public TestDirectory(string testName)
    {
        char[] invalid = System.IO.Path.GetInvalidFileNameChars();
        string safe = new string(testName.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        Path = System.IO.Path.Combine(AppContext.BaseDirectory, "engine-runs", (safe.Length > 40 ? safe[..40] : safe) + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name)
    {
        return System.IO.Path.Combine(Path, name);
    }

    public string Sub(string name)
    {
        string sub = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(sub);
        return sub;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Template helpers: the engine fixture with inline parameters, and inline templates.</summary>
internal static class Templates
{
    public const string SchemaUri = "https://schema.earm.proxylayer.net/schemas/2026-10-01/deploymentTemplate.json#";

    public const string FixtureFile = "orders-api.engine.entra.json";

    public const string DefaultPassword = "Initial-P@ssw0rd-1";

    public static string FixturePath => TestPaths.ValidFixture(FixtureFile);

    public static ParameterSet Inline(params (string Name, string Value)[] parameters)
    {
        var set = ParameterSet.Empty;
        foreach (var (name, value) in parameters)
        {
            set = set.WithInline(name, value);
        }

        return set;
    }

    /// <summary>Loads the engine fixture with <c>env</c> and <c>initialPassword</c> supplied inline (pass <c>null</c> to leave one out).</summary>
    public static async Task<TemplateDocument> FixtureAsync(string? env = "dev", string? password = DefaultPassword, ParameterSet? parameters = null)
    {
        var set = parameters ?? ParameterSet.Empty;
        if (env is not null)
        {
            set = set.WithInline("env", env);
        }

        if (password is not null)
        {
            set = set.WithInline("initialPassword", password);
        }

        return await new TemplateLoader().LoadAsync(FixturePath, set, TestContext.Current.CancellationToken);
    }

    /// <summary>Parses an inline template from its body (everything after <c>contentVersion</c>).</summary>
    public static TemplateDocument Parse(string body, params (string Name, string Value)[] parameters)
    {
        string json = "{ \"$schema\": \"" + SchemaUri + "\", \"contentVersion\": \"1.0.0.0\", " + body + " }";
        return new TemplateLoader().Parse(json, "inline.entra.json", Inline(parameters));
    }

    public static TemplateDocument Resources(string resourcesJson, params (string Name, string Value)[] parameters)
    {
        return Parse("\"resources\": [" + resourcesJson + "]", parameters);
    }
}

/// <summary>
/// One in-memory engine per test: a <see cref="FakeGraphClient"/>, the catalog-driven <see cref="EchoProvider"/>
/// registry, a retry policy with a recording (no-op) delay provider, and the planner / executor wired to them.
/// </summary>
internal sealed class EngineHost
{
    public const string Tenant = "11111111-1111-1111-1111-111111111111";

    public const string GraphAppId = "00000003-0000-0000-c000-000000000000";

    public const string DeploymentName = "orders-dev";

    public EngineHost(Func<IGraphClient, IGraphClient>? decorate = null, ProviderRegistry? registry = null)
    {
        Fake = new FakeGraphClient();
        Graph = decorate is null ? Fake : decorate(Fake);
        Registry = registry ?? EchoProvider.RegistryForCatalog(Catalog);
        Delays = new RecordingDelayProvider();
        Retry = new RetryPolicy(new RetryOptions(), Delays, NullLogger<RetryPolicy>.Instance);
    }

    public ResourceTypeCatalog Catalog { get; } = ResourceTypeCatalog.Default;

    public FakeGraphClient Fake { get; }

    public IGraphClient Graph { get; }

    public FakeDirectory Directory => Fake.Directory;

    public ProviderRegistry Registry { get; }

    public RecordingDelayProvider Delays { get; }

    public RetryPolicy Retry { get; }

    /// <summary>The objects the engine fixture looks up as existing resources (<c>reference(..., apiVersion)</c>).</summary>
    public JsonObject[] SeedExisting()
    {
        var cio = Directory.AddUser("cio@contoso.com", "Chief Information Officer");
        var allStaff = Directory.AddGroup("contoso-all-staff", "All staff");
        var graph = Directory.AddServicePrincipal(GraphAppId, "Microsoft Graph");
        return [cio, allStaff, graph];
    }

    public PlannerOptions Options(string? tenantId = Tenant, int parallelism = 4, string deploymentName = DeploymentName)
    {
        return new PlannerOptions
        {
            DeploymentName = deploymentName,
            TenantId = tenantId,
            Parallelism = parallelism,
            Catalog = Catalog,
            RetryPolicy = Retry,
        };
    }

    public DeploymentPlanner Planner(PlannerOptions? options = null)
    {
        return new DeploymentPlanner(Registry, Graph, options ?? Options(), NullLogger<DeploymentPlanner>.Instance);
    }

    public DeploymentExecutor Executor()
    {
        return new DeploymentExecutor(Registry, Graph, NullLogger<DeploymentExecutor>.Instance);
    }

    public Task<DeploymentPlan> WhatIfAsync(TemplateDocument document, StateFile? state = null, DeploymentMode mode = DeploymentMode.Incremental, PlannerOptions? options = null)
    {
        return Planner(options).PlanAsync(document, state, mode, PlanMode.WhatIf, TestContext.Current.CancellationToken);
    }

    /// <summary>Expansion and ordering only (<see cref="PlanMode.Create"/>): no Graph reads.</summary>
    public Task<DeploymentPlan> ExpandAsync(TemplateDocument document, StateFile? state = null, DeploymentMode mode = DeploymentMode.Incremental, PlannerOptions? options = null)
    {
        return Planner(options).PlanAsync(document, state, mode, PlanMode.Create, TestContext.Current.CancellationToken);
    }

    public async Task<(DeploymentPlan Plan, DeploymentResult Result)> CreateAsync(TemplateDocument document, ExecuteOptions? execute = null, StateFile? state = null, DeploymentMode mode = DeploymentMode.Incremental, PlannerOptions? options = null)
    {
        var plan = await ExpandAsync(document, state, mode, options);
        var result = await Executor().ExecuteAsync(plan, execute ?? ExecuteOptions.Default, TestContext.Current.CancellationToken);
        return (plan, result);
    }

    /// <summary>Every evaluated instance of a plan (what the providers see), by resource id.</summary>
    public static ResourceInstance Instance(DeploymentPlan plan, string resourceId)
    {
        var instance = plan.Instances.FirstOrDefault(i => i.PlannedId.Id == resourceId || i.Id.Id == resourceId);
        Assert.NotNull(instance);
        return instance;
    }

    public static ResourceInstance Evaluated(DeploymentPlan plan, string resourceId)
    {
        var instance = Instance(plan, resourceId);
        if (!instance.IsEvaluated)
        {
            InstanceEvaluator.EvaluateProperties(plan.Evaluator, instance);
        }

        return instance;
    }

    public IResourceProvider Provider(string type)
    {
        return Registry.Get(type);
    }

    public EchoProvider Echo(string type)
    {
        return Assert.IsType<EchoProvider>(Registry.Get(type));
    }
}

/// <summary>Resource ids of the engine fixture (<c>env=dev</c>).</summary>
internal static class Fixture
{
    public const string AppType = "Microsoft.Entra/applications";
    public const string SpType = "Microsoft.Entra/servicePrincipals";
    public const string UserType = "Microsoft.Entra/users";
    public const string GroupType = "Microsoft.Entra/groups";
    public const string FicType = "Microsoft.Entra/applications/federatedIdentityCredentials";
    public const string PasswordType = "Microsoft.Entra/applications/passwordCredentials";
    public const string RoleDefinitionType = "Microsoft.Entra/roleDefinitions";
    public const string AuthStrengthType = "Microsoft.Entra/conditionalAccess/authenticationStrengths";
    public const string AccrType = "Microsoft.Entra/conditionalAccess/authenticationContextClassReferences";
    public const string AppRoleAssignmentType = "Microsoft.Entra/appRoleAssignments";
    public const string Oauth2GrantType = "Microsoft.Entra/oauth2PermissionGrants";
    public const string NamedLocationType = "Microsoft.Entra/conditionalAccess/namedLocations";
    public const string PolicyType = "Microsoft.Entra/conditionalAccess/policies";
    public const string AuType = "Microsoft.Entra/administrativeUnits";
    public const string RoleAssignmentType = "Microsoft.Entra/roleAssignments";

    public const string App = "/providers/Microsoft.Entra/applications/contoso-dev-api";
    public const string User = "/providers/Microsoft.Entra/users/bg-orders@contoso.com";
    public const string Group = "/providers/Microsoft.Entra/groups/contoso-dev-orders-admins";
    public const string Fic0 = App + "/federatedIdentityCredentials/github-0";
    public const string Fic1 = App + "/federatedIdentityCredentials/github-1";
    public const string Password = App + "/passwordCredentials/ci-secret";
    public const string RoleDefinition = "/providers/Microsoft.Entra/roleDefinitions/Orders Support";
    public const string AuthStrength = "/providers/Microsoft.Entra/conditionalAccess/authenticationStrengths/Orders phishing-resistant";
    public const string Accr = "/providers/Microsoft.Entra/conditionalAccess/authenticationContextClassReferences/c10";
    public const string NamedLocation = "/providers/Microsoft.Entra/conditionalAccess/namedLocations/Contoso offices";
    public const string Policy = "/providers/Microsoft.Entra/conditionalAccess/policies/CA-Orders-Admins-MFA";
    public const string Au = "/providers/Microsoft.Entra/administrativeUnits/Orders AU";
    public const string SpSymbol = "ref(" + App + ").appId";
    public const string SpSymbolic = "/providers/Microsoft.Entra/servicePrincipals/" + SpSymbol;

    /// <summary>The number of instances after expansion (16: 13 top-level declarations, 2 nested children, the FIC copy expands to 2).</summary>
    public const int InstanceCount = 16;

    public static string Sp(string appId)
    {
        return "/providers/Microsoft.Entra/servicePrincipals/" + appId;
    }
}
