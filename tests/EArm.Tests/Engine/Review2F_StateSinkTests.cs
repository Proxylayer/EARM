using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core.Secrets;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Engine.State;
using EArm.Graph;
using EArm.TestSupport;
using Xunit;

namespace EArm.Tests.Engine;

/// <summary>
/// Review 2F / mutation review at the <see cref="FileStateSink"/> seam (not through the executor): the state file is committed to source control, so
/// no operation error may carry a secret into it, whatever the shape of the secret; the bounded persistence token; the run status mapping.
/// </summary>
public sealed class Review2F_StateSinkTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string JwtSecret = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3OCJ9.c2lnbmF0dXJlLXBhcnQtMTIzNDU2";

    /// <summary>One error text carrying a secret in every shape the redactor knows, plus a harmless marker that must survive.</summary>
    private static readonly string HostileError =
        "Graph said Authorization_RequestDenied: {\"password\": \"hunter2-json\"} client_secret=csecret-pair Authorization: Bearer opaque-bearer-token-123 " + JwtSecret;

    private static readonly string[] Secrets = ["hunter2-json", "csecret-pair", "opaque-bearer-token-123", JwtSecret, "eyJhbGciOiJSUzI1NiJ9"];

    private sealed record Rig(FileStateSink Sink, InMemoryStateStore Store, StateFile State, DeploymentPlan Plan, EngineHost Host);

    private static async Task<Rig> NewRigAsync()
    {
        var host = new EngineHost();
        var plan = await host.ExpandAsync(await Templates.FixtureAsync());
        var store = new InMemoryStateStore();
        var state = StateFile.CreateNew(EngineHost.DeploymentName, null, Start);
        var sink = new FileStateSink(store, state, plan, timeProvider: new FakeTimeProvider(Start));
        await sink.BeginAsync(Ct);
        return new Rig(sink, store, state, plan, host);
    }

    private static async Task<StateEntry> AppliedUserAsync(Rig rig)
    {
        var user = EngineHost.Evaluated(rig.Plan, Fixture.User);
        var key = rig.Host.Provider(Fixture.UserType).ResolveKey(user);
        var change = new ResourceChange { Id = user.Id, ResourceType = Fixture.UserType, Type = ChangeType.Create, Mode = ChangeMode.Create };
        await rig.Sink.ResourceAppliedAsync(user, change, new AppliedResource(user.Id, "U1", new System.Text.Json.Nodes.JsonObject(), key), Ct);
        return rig.State.Find(Fixture.User)!;
    }

    private static void AssertNoSecretAnywhere(Rig rig)
    {
        Assert.NotEmpty(rig.Store.Saves);
        foreach (string document in rig.Store.Saves.Append(rig.Store.DocumentOf(EngineHost.DeploymentName) ?? string.Empty))
        {
            foreach (string secret in Secrets)
            {
                Assert.DoesNotContain(secret, document, StringComparison.Ordinal);
            }
        }

        foreach (var operation in rig.State.Operations)
        {
            foreach (string secret in Secrets)
            {
                Assert.DoesNotContain(secret, operation.Error ?? string.Empty, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task ResourceFailed_RedactsEverySecretShape_ButKeepsTheDiagnosis()
    {
        var rig = await NewRigAsync();
        var user = EngineHost.Evaluated(rig.Plan, Fixture.User);

        await rig.Sink.ResourceFailedAsync(user, HostileError, Ct);

        var operation = Assert.Single(rig.State.Operations, o => o.Status == OperationStatus.Failed);
        Assert.Contains("Authorization_RequestDenied", operation.Error, StringComparison.Ordinal);
        Assert.Contains(SecretRedactor.Mask, operation.Error, StringComparison.Ordinal);
        Assert.Equal(EngineDiagnosticCodes.ResourceApplyFailed, operation.ErrorCode);
        AssertNoSecretAnywhere(rig);
    }

    /// <summary>
    /// <see cref="SecretRedactor"/> masks <c>name=value</c>, JSON <c>"name": "value"</c> and a QUOTED value assigned with <c>=</c> or <c>:</c> without JSON quoting
    /// of the name (<c>password="x"</c>, <c>secretText='x'</c>): an error text of that shape must not be written to the committed state file.
    /// </summary>
    [Theory]
    [InlineData("password=\"quoted-secret-1\"")]
    [InlineData("secretText='quoted-secret-2'")]
    [InlineData("client_secret: \"quoted-secret-3\"")]
    public async Task ASecretNameAssignedAQuotedValue_IsMaskedInTheStateFile(string fragment)
    {
        var rig = await NewRigAsync();
        var user = EngineHost.Evaluated(rig.Plan, Fixture.User);

        await rig.Sink.ResourceFailedAsync(user, "Graph refused " + fragment + " (Request_BadRequest)", Ct);

        string document = rig.Store.DocumentOf(EngineHost.DeploymentName)!;
        Assert.DoesNotContain("quoted-secret", document, StringComparison.Ordinal);
        Assert.Contains("Request_BadRequest", document, StringComparison.Ordinal);
    }
    [Fact]
    public async Task ResourceDeleteFailed_RedactsEverySecretShape()
    {
        var rig = await NewRigAsync();
        var entry = await AppliedUserAsync(rig);

        await rig.Sink.ResourceDeleteFailedAsync(entry, HostileError, Ct);

        var operation = Assert.Single(rig.State.Operations, o => o.Status == OperationStatus.Failed);
        Assert.Contains("Authorization_RequestDenied", operation.Error, StringComparison.Ordinal);
        Assert.Equal(ChangeType.Delete, operation.ChangeType);
        AssertNoSecretAnywhere(rig);
    }

    [Fact]
    public async Task ResourceEdgesFailed_RedactsEverySecretShape()
    {
        var rig = await NewRigAsync();
        var user = EngineHost.Evaluated(rig.Plan, Fixture.User);

        await rig.Sink.ResourceEdgesFailedAsync(user, HostileError, Ct);

        var operation = Assert.Single(rig.State.Operations, o => o.Status == OperationStatus.Failed);
        Assert.Contains("Authorization_RequestDenied", operation.Error, StringComparison.Ordinal);
        Assert.Equal(EngineDiagnosticCodes.EdgeApplyFailed, operation.ErrorCode);
        AssertNoSecretAnywhere(rig);
    }

    [Fact]
    public async Task ARegisteredSecretValue_ThatHasNoRecognisableShape_NeverReachesTheStateFile()
    {
        const string opaque = "Zq9-plain-looking-value-7731";
        using var scope = SecretScrubber.Begin();
        SecretScrubber.Register(opaque);
        var rig = await NewRigAsync();
        var user = EngineHost.Evaluated(rig.Plan, Fixture.User);
        var entry = await AppliedUserAsync(rig);

        await rig.Sink.ResourceFailedAsync(user, $"The value {opaque} is not allowed here.", Ct);
        await rig.Sink.ResourceDeleteFailedAsync(entry, $"cannot delete with {opaque}", Ct);
        await rig.Sink.ResourceEdgesFailedAsync(user, $"edge: {opaque}", Ct);

        Assert.DoesNotContain(opaque, rig.Store.DocumentOf(EngineHost.DeploymentName)!, StringComparison.Ordinal);
        Assert.All(rig.Store.Saves, document => Assert.DoesNotContain(opaque, document, StringComparison.Ordinal));
        Assert.Equal(3, rig.State.Operations.Count(o => o.Status == OperationStatus.Failed));
    }

    [Fact]
    public async Task APasswordCarriedInAnErrorOfTheExecutor_DoesNotReachTheSavedDocument()
    {
        // The same property through the full executor: the sink is what the CLI wires, and the executor passes its error text straight in.
        var host = new EngineHost();
        host.SeedExisting();
        host.Fake.Failures.Add("POST", "/users", request => new GraphException(System.Net.HttpStatusCode.BadRequest, "Request_BadRequest", "bad body {\"password\":\"hunter2-exec\"} client_secret=exec-secret-value", request.Hint));
        var store = new InMemoryStateStore();
        var plan = await host.ExpandAsync(await Templates.FixtureAsync());
        var state = StateFile.CreateNew(EngineHost.DeploymentName, null, Start);
        var sink = new FileStateSink(store, state, plan, timeProvider: new FakeTimeProvider(Start));
        await sink.BeginAsync(Ct);

        var result = await host.Executor().ExecuteAsync(plan, new ExecuteOptions { StateSink = sink, State = state }, Ct);

        Assert.False(result.Succeeded);
        Assert.All(store.Saves, document =>
        {
            Assert.DoesNotContain("hunter2-exec", document, StringComparison.Ordinal);
            Assert.DoesNotContain("exec-secret-value", document, StringComparison.Ordinal);
        });
        Assert.Contains("Request_BadRequest", Assert.Single(state.Operations, o => o.Status == OperationStatus.Failed).Error, StringComparison.Ordinal);
    }

    // ---- bounded persistence ----

    private sealed class CapturingStore : IStateStore
    {
        public List<CancellationToken> Tokens { get; } = [];

        public Task SaveAsync(StateFile state, CancellationToken cancellationToken)
        {
            Tokens.Add(cancellationToken);
            return Task.CompletedTask;
        }

        public Task<StateFile?> LoadAsync(string deploymentName, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(string deploymentName, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IAsyncDisposable> LockAsync(string deploymentName, TimeSpan wait, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<StateLockInfo?> GetLockAsync(string deploymentName, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<StateLockInfo?> ForceUnlockAsync(string deploymentName, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Fact]
    public async Task PersistTimeout_IsAFiniteBound_AndTheSaveGetsABoundedTokenThatIsNotTheCallers()
    {
        // A mutant that sets the bound to Infinite, or hands the store the caller's token or CancellationToken.None, must fail.
        Assert.InRange(FileStateSink.PersistTimeout, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5));

        var host = new EngineHost();
        var plan = await host.ExpandAsync(await Templates.FixtureAsync());
        var store = new CapturingStore();
        var sink = new FileStateSink(store, StateFile.CreateNew(EngineHost.DeploymentName, null, Start), plan, timeProvider: new FakeTimeProvider(Start));
        await sink.BeginAsync(Ct);
        var user = EngineHost.Evaluated(plan, Fixture.User);
        using var callers = new CancellationTokenSource();

        await sink.ResourceFailedAsync(user, "boom", callers.Token);
        await callers.CancelAsync();

        var persisted = store.Tokens[^1];
        Assert.True(persisted.CanBeCanceled);
        Assert.False(persisted.IsCancellationRequested, "cancelling the caller's token must not cancel the persistence in flight");
        Assert.NotEqual(callers.Token, persisted);
        Assert.NotEqual(CancellationToken.None, persisted);
    }

    // ---- the run status written by CompletedAsync ----

    [Theory]
    [InlineData(DeploymentStatus.Succeeded, StateFile.StatusSucceeded)]
    [InlineData(DeploymentStatus.Failed, StateFile.StatusFailed)]
    [InlineData(DeploymentStatus.Cancelled, StateFile.StatusCancelled)]
    public async Task CompletedAsync_WritesTheStatusOfTheRun_AndNeverLeavesItRunning(DeploymentStatus status, string expected)
    {
        var rig = await NewRigAsync();
        Assert.Equal(StateFile.StatusRunning, rig.State.LastStatus);

        await rig.Sink.CompletedAsync(status, Ct);

        Assert.Equal(expected, rig.State.LastStatus);
        Assert.Contains("\"lastStatus\": \"" + expected + "\"", rig.Store.DocumentOf(EngineHost.DeploymentName), StringComparison.Ordinal);
    }
}
