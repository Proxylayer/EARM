using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Core;
using EArm.Core.Loading;
using EArm.Core.Types;
using EArm.Engine.Edges;
using EArm.Engine.Execution;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Engine.State;
using EArm.Engine.WhatIf;
using EArm.TestSupport;
using Xunit;

namespace EArm.Providers.Tests.Infrastructure;

/// <summary>
/// One in-memory engine per test, wired to the real providers: <see cref="ProviderDiscovery.CreateRegistry"/> (catalog
/// types without a provider yet are backfilled with <see cref="EchoProvider"/>; <see cref="Echo"/> replaces one on
/// purpose), <see cref="FakeGraphClient"/> + <see cref="FakeDirectory"/>, <see cref="InMemoryStateStore"/> +
/// <see cref="FileStateSink"/>, <see cref="PlannerOptions"/> with <c>Parallelism = 1</c> and a <see cref="RetryPolicy"/>
/// over a no-delay provider, recording loggers, and helpers to load inline templates, run what-if / create and read
/// the state document back.
/// </summary>
internal sealed class ProviderHost
{
    public const string DeploymentName = "providers-test";

    public const string ApiVersion = "2026-10-01-preview";

    public const string PreviewApiVersion = "2026-10-01-preview.beta";

    public const string GraphAppId = "00000003-0000-0000-c000-000000000000";

    public ProviderHost(FakeGraphClient? fake = null)
    {
        Fake = fake ?? new FakeGraphClient();
        Registry = CreateRegistry(Catalog);
        Store = new InMemoryStateStore();
        Logs = new LogSink();
        Delays = new NoDelayProvider();
        ReplicationDelays = new NoDelayProvider();
        Retry = new RetryPolicy(new RetryOptions(), Delays, Logs.For<RetryPolicy>());
        EdgeKinds = ProviderDiscovery.CreateEdgeKinds(Catalog);
    }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ResourceTypeCatalog Catalog { get; } = ResourceTypeCatalog.Default;

    public FakeGraphClient Fake { get; }

    public FakeDirectory Directory => Fake.Directory;

    public ProviderRegistry Registry { get; }

    public InMemoryStateStore Store { get; }

    public LogSink Logs { get; }

    public NoDelayProvider Delays { get; }

    /// <summary>The executor's own replication wait after an uncertain create (<see cref="ReplicationWait"/>), kept apart from the retry policy's delays.</summary>
    public NoDelayProvider ReplicationDelays { get; }

    public RetryPolicy Retry { get; }

    public EdgeKindRegistry EdgeKinds { get; }

    /// <summary>Pass the no-delay retry policy to the planner (default); <c>false</c> runs without an outer policy.</summary>
    public bool UseRetry { get; set; } = true;

    /// <summary>Record every run in <see cref="Store"/> through a <see cref="FileStateSink"/> (default); <c>false</c> runs stateless like the harnesses without a store.</summary>
    public bool TrackState { get; set; } = true;

    public SoftDeletedConflict OnSoftDeletedConflict { get; set; } = SoftDeletedConflict.Error;

    public bool AllowCaEnforce { get; set; }

    public string? SignedInPrincipalId { get; set; }

    /// <summary>Plan as if signed in with an application (app-only) token: <c>PlannerOptions.AppOnlyToken</c>.</summary>
    public bool AppOnlyToken { get; set; }

    /// <summary>The discovered registry with every catalog type that has no provider yet served by <see cref="EchoProvider"/>.</summary>
    public static ProviderRegistry CreateRegistry(ResourceTypeCatalog catalog)
    {
        var registry = ProviderDiscovery.CreateRegistry(catalog);
        foreach (var definition in catalog.Types)
        {
            if (!registry.Contains(definition.Type))
            {
                registry.Register(new EchoProvider(definition, catalog));
            }
        }

        return registry;
    }

    /// <summary>Replaces the provider of one type with the catalog-driven echo provider.</summary>
    public void Echo(string type)
    {
        var definition = Catalog.Find(type) ?? throw new InvalidOperationException($"No catalog type '{type}'.");
        Registry.Register(new EchoProvider(definition, Catalog), replace: true);
    }

    public IResourceProvider Provider(string type)
    {
        return Registry.Get(type);
    }

    public T Provider<T>(string type)
        where T : class, IResourceProvider
    {
        return Assert.IsType<T>(Registry.Get(type), exactMatch: false);
    }

    // ---- templates ----

    public TemplateDocument Parse(string json, params (string Name, string Value)[] parameters)
    {
        var set = ParameterSet.Empty;
        foreach (var (name, value) in parameters)
        {
            set = set.WithInline(name, value);
        }

        return new TemplateLoader(Catalog).Parse(json, "inline.entra.json", set);
    }

    /// <summary>A template with the given resources (and optional outputs / parameters objects), parsed with inline parameter values.</summary>
    public TemplateDocument Resources(string resourcesJson, string? outputs = null, string? parameters = null, params (string Name, string Value)[] values)
    {
        return Parse(Templates.Envelope(resourcesJson, outputs, parameters), values);
    }

    // ---- engine ----

    public PlannerOptions Options()
    {
        return new PlannerOptions
        {
            DeploymentName = DeploymentName,
            TenantId = Directory.TenantId,
            Catalog = Catalog,
            Parallelism = 1,
            RetryPolicy = UseRetry ? Retry : null,
            OnSoftDeletedConflict = OnSoftDeletedConflict,
            AllowCaEnforce = AllowCaEnforce,
            EdgeKinds = EdgeKinds,
            SignedInPrincipalId = SignedInPrincipalId,
            AppOnlyToken = AppOnlyToken,
        };
    }

    public DeploymentPlanner Planner()
    {
        return new DeploymentPlanner(Registry, Fake, Options(), Logs.For<DeploymentPlanner>());
    }

    public DeploymentExecutor Executor()
    {
        return new DeploymentExecutor(Registry, Fake, Logs.For<DeploymentExecutor>(), new ReplicationWait { Delay = ReplicationDelays });
    }

    public Task<StateFile?> LoadStateAsync()
    {
        return Store.LoadAsync(DeploymentName, Ct);
    }

    /// <summary>Expansion and ordering only (no Graph reads): the instances the providers would see.</summary>
    public async Task<DeploymentPlan> ExpandAsync(TemplateDocument document, DeploymentMode mode = DeploymentMode.Incremental)
    {
        var state = TrackState ? await LoadStateAsync() : null;
        return await Planner().PlanAsync(document, state, mode, PlanMode.Create, Ct);
    }

    public async Task<Run> WhatIfAsync(TemplateDocument document, DeploymentMode mode = DeploymentMode.Incremental)
    {
        var state = TrackState ? await LoadStateAsync() : null;
        int before = Fake.Requests.Count;
        var plan = await Planner().PlanAsync(document, state, mode, PlanMode.WhatIf, Ct);
        return new Run(plan, null, state, RequestsSince(before));
    }

    public async Task<Run> CreateAsync(TemplateDocument document, DeploymentMode mode = DeploymentMode.Incremental, bool continueOnError = false, string? secureOutputsFile = null, bool revealSecureOutputs = false)
    {
        var state = TrackState ? await LoadStateAsync() : null;
        int before = Fake.Requests.Count;
        var plan = await Planner().PlanAsync(document, state, mode, PlanMode.Create, Ct);
        var options = new ExecuteOptions
        {
            Parallelism = 1,
            ContinueOnError = continueOnError,
            SecureOutputsFile = secureOutputsFile,
            RevealSecureOutputs = revealSecureOutputs,
        };

        FileStateSink? sink = null;
        if (TrackState)
        {
            state ??= StateFile.CreateNew(DeploymentName, Directory.TenantId, DateTimeOffset.UtcNow);
            sink = new FileStateSink(Store, state, plan, Logs.For<FileStateSink>());
            await sink.BeginAsync(Ct);
            options = options with { StateSink = sink, State = state };
        }

        var result = await Executor().ExecuteAsync(plan, options, Ct);
        if (sink is not null)
        {
            await sink.CompletedAsync(result.Status, Ct);
        }

        return new Run(plan, result, state, RequestsSince(before));
    }

    /// <summary><c>deployment delete</c>: the objects <paramref name="document"/> declares, found by key, deleted in reverse dependency order.</summary>
    public async Task<TemplateDeleteResult> DeleteDeclaredAsync(TemplateDocument document, bool continueOnError = false)
    {
        var plan = await Planner().PlanAsync(document, null, DeploymentMode.Incremental, PlanMode.WhatIf, Ct);
        var targets = TemplateDeleter.FindDeclaredObjects(plan);
        return await new TemplateDeleter(Registry, Fake, Logs.For<TemplateDeleter>()).DeleteAsync(plan, targets, continueOnError, Ct);
    }

    // ---- state and directory helpers ----

    public string? StateDocument => Store.DocumentOf(DeploymentName);

    /// <summary>The serialised state entry of a resource id, read back from the stored document.</summary>
    public JsonObject? StateEntryJson(string resourceId)
    {
        return StateDocument is null ? null : (JsonNode.Parse(StateDocument) as JsonObject)?["resources"]?[resourceId] as JsonObject;
    }

    public string IdOf(string collection, string property, string value)
    {
        return Directory.Find(collection, property, value)?["id"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"No object in {collection} with {property} = '{value}'.");
    }

    public IReadOnlyList<FakeGraphRequest> RequestsSince(int index)
    {
        return Fake.Requests.Skip(index).ToList();
    }

    public void ClearRequests()
    {
        Fake.ClearRequests();
    }
}

/// <summary>The outcome of one planner / executor run: the plan, the result (<c>null</c> for what-if), the state (when tracked) and the requests issued during the run.</summary>
internal sealed record Run(DeploymentPlan Plan, DeploymentResult? Result, StateFile? State, IReadOnlyList<FakeGraphRequest> Requests)
{
    public IReadOnlyList<FakeGraphRequest> Writes => Requests.Where(r => r.IsWrite).ToList();

    public DeploymentResult Executed => Result ?? throw new InvalidOperationException("A what-if run has no result.");

    public bool Succeeded => Executed.Succeeded;

    public string Errors => Result is null ? string.Empty : string.Join("; ", Result.Operations.Where(o => o.Error is not null).Select(o => o.Id.Id + ": " + o.ErrorCode + " " + o.Error));

    public ResourceOperation Op(string idContains)
    {
        return Executed.Operations.First(o => o.Id.Id.Contains(idContains, StringComparison.OrdinalIgnoreCase));
    }

    public ResourceOperation OpOfType(string type)
    {
        return Executed.Operations.Single(o => ResourceIdentifier.TypeEquals(o.Type, type));
    }

    /// <summary>A what-if row (or, after a create, a row computed at apply time) whose id contains the text.</summary>
    public ResourceChange Change(string idContains)
    {
        var rows = Result is null ? Plan.Changes : Executed.Changes;
        return rows.First(c => c.Id.Id.Contains(idContains, StringComparison.OrdinalIgnoreCase));
    }

    public ResourceChange ChangeOfType(string type)
    {
        var rows = Result is null ? Plan.Changes : Executed.Changes;
        return rows.Single(c => ResourceIdentifier.TypeEquals(c.ResourceType, type));
    }

    public IReadOnlyList<ResourceChange> Changes => Result is null ? Plan.Changes : Executed.Changes;

    public JsonNode? Output(string name)
    {
        return (Executed.Outputs[name] as JsonObject)?["value"];
    }

    public JsonNode? SecureOutput(string name)
    {
        return (Executed.SecureOutputs[name] as JsonObject)?["value"];
    }

    public StateEntry Entry(string resourceId)
    {
        return (State ?? throw new InvalidOperationException("The run was not tracked.")).Resources[resourceId];
    }

    public string WhatIfJsonText => WhatIfJson.Build(Plan).ToJsonString();

    public string WhatIfTextOutput
    {
        get
        {
            using var writer = new StringWriter();
            WhatIfText.Render(Plan, writer);
            return writer.ToString();
        }
    }

    public string DescribeRequests()
    {
        return string.Join("\n", Requests.Select(Describe));
    }

    public static string Describe(FakeGraphRequest request)
    {
        string prefer = request.Headers is not null && request.Headers.TryGetValue("Prefer", out var p) ? " [Prefer: " + p + "]" : string.Empty;
        string hint = request.Hint == RetryHint.Default ? string.Empty : " [" + request.Hint + "]";
        string body = request.Body is null ? string.Empty : " " + request.Body.ToJsonString();
        return request + prefer + hint + body;
    }
}

/// <summary>A private directory under the test output folder for secure-outputs files, deleted on dispose.</summary>
internal sealed class TestDirectory : IDisposable
{
    public TestDirectory()
    {
        Path = System.IO.Path.Combine(AppContext.BaseDirectory, "provider-runs", Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name)
    {
        return System.IO.Path.Combine(Path, name);
    }

    public void Dispose()
    {
        try
        {
            if (System.IO.Directory.Exists(Path))
            {
                System.IO.Directory.Delete(Path, recursive: true);
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
