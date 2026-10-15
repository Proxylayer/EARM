using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Engine.State;
using EArm.Graph;
using EArm.TestSupport;
using Xunit;
using static EArm.Tests.Engine.Review1A;

namespace EArm.Tests.Engine;

/// <summary>
/// Review findings E-03 / E-08 (one-time secrets survive failure, cancellation and reruns), S-02 (secure values never reach state, logs or
/// output through an error text), E-05 (an ambiguous create), E-06 (replace: create first, delete second) and E-11 (partial edges).
/// </summary>
public sealed class Review1A_RunSafetyTests
{
    private const string Secret = "Hunter2-Secret-Value!";

    // ---- E-03a: the secure outputs file is validated before anything is written ----

    [Fact]
    public void Validate_AcceptsAWritableNewFile_AndLeavesNothingBehind()
    {
        using var directory = new TestDirectory(nameof(Validate_AcceptsAWritableNewFile_AndLeavesNothingBehind));

        Assert.Null(SecureOutputsFile.Validate(directory.File("secure.json")));
        Assert.Empty(Directory.GetFileSystemEntries(directory.Path));
    }

    [Fact]
    public void Validate_RefusesADirectory_AMissingFolder_AndAReadOnlyFile()
    {
        using var directory = new TestDirectory(nameof(Validate_RefusesADirectory_AMissingFolder_AndAReadOnlyFile));
        string readOnly = directory.File("read-only.json");
        File.WriteAllText(readOnly, "{}");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        try
        {
            Assert.Contains("is a directory", SecureOutputsFile.Validate(directory.Path), StringComparison.Ordinal);
            Assert.Contains("does not exist", SecureOutputsFile.Validate(Path.Combine(directory.Path, "missing", "secure.json")), StringComparison.Ordinal);
            Assert.Contains("read-only", SecureOutputsFile.Validate(readOnly), StringComparison.Ordinal);
            Assert.NotNull(SecureOutputsFile.Validate(string.Empty));
        }
        finally
        {
            File.SetAttributes(readOnly, FileAttributes.Normal);
        }
    }

    [Fact]
    public void Validate_RefusesASymbolicLink()
    {
        using var directory = new TestDirectory(nameof(Validate_RefusesASymbolicLink));
        string target = directory.File("target.json");
        File.WriteAllText(target, "{}");
        string link = directory.File("link.json");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Skip("symbolic links cannot be created here: " + ex.Message);
        }

        Assert.Contains("symbolic link", SecureOutputsFile.Validate(link), StringComparison.Ordinal);
    }

    // ---- E-03b / E-03d: cancellation and one-time secrets ----

    [Fact]
    public async Task Cancellation_AfterThePasswordCredentialExists_StillSavesTheSecret_AndTheStateIsCancelledNotRunning()
    {
        using var directory = new TestDirectory(nameof(Cancellation_AfterThePasswordCredentialExists_StillSavesTheSecret_AndTheStateIsCancelledNotRunning));
        string path = directory.File("secure.json");
        using var cts = new CancellationTokenSource();
        var host = new EngineHost(graph => new CancelAfterPostClient(graph, "/addPassword", cts));
        host.SeedExisting();
        var state = NewState();
        var store = new InMemoryStateStore();
        var plan = await host.ExpandAsync(await Templates.FixtureAsync(), state);
        var sink = new FileStateSink(store, state, plan);
        await sink.BeginAsync(Ct);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Executor().ExecuteAsync(plan, new ExecuteOptions { StateSink = sink, State = state, SecureOutputsFile = path }, cts.Token));

        var written = JsonNode.Parse(await File.ReadAllTextAsync(path, Ct))!.AsObject();
        var credential = written[SecureOutputsFile.OneTimeSecretsKey]![Fixture.Password]!.AsObject();
        Assert.StartsWith("fake-secret-", credential["secretText"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(credential["keyId"]?.GetValue<string>()));
        Assert.Equal(StateFile.StatusCancelled, state.LastStatus);
        var saved = await store.LoadAsync(EngineHost.DeploymentName, Ct);
        Assert.Equal(StateFile.StatusCancelled, saved!.LastStatus);
        Assert.NotNull(saved.Find(Fixture.Password));
        Assert.All(saved.Operations.Where(o => o.Status == OperationStatus.Skipped), o => Assert.Equal("ResourceSkipped", o.ErrorCode));
        Assert.DoesNotContain(saved.Operations, o => o.Status == OperationStatus.Failed);
    }

    [Fact]
    public async Task TheOneTimeSecret_IsOnDisk_BeforeTheStateRecordsTheResource()
    {
        using var directory = new TestDirectory(nameof(TheOneTimeSecret_IsOnDisk_BeforeTheStateRecordsTheResource));
        string path = directory.File("secure.json");
        var host = new EngineHost();
        host.SeedExisting();
        var state = NewState();
        var plan = await host.ExpandAsync(await Templates.FixtureAsync(), state);
        var real = new FileStateSink(new InMemoryStateStore(), state, plan);
        await real.BeginAsync(Ct);
        var probe = new FileProbeSink(real, path, Fixture.Password);

        var result = await host.Executor().ExecuteAsync(plan, new ExecuteOptions { StateSink = probe, State = state, SecureOutputsFile = path }, Ct);

        Assert.True(result.Succeeded);
        Assert.NotNull(probe.FileAtApply);
        Assert.Contains("fake-secret-", probe.FileAtApply, StringComparison.Ordinal);
        Assert.Contains(SecureOutputsFile.OneTimeSecretsKey, probe.FileAtApply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheOneTimeSecret_IsSavedEvenWhenTheStateCannotBeWritten()
    {
        using var directory = new TestDirectory(nameof(TheOneTimeSecret_IsSavedEvenWhenTheStateCannotBeWritten));
        string path = directory.File("secure.json");
        var host = new EngineHost();
        host.SeedExisting();
        var state = NewState();

        // Every save that would record the password credential fails: the run still ends normally and the secret Graph returned once is in the file.
        var store = new FlakyStateStore(new InMemoryStateStore(), failAfterSaves: 0, failWhen: saved => saved.Find(Fixture.Password) is not null);

        var (_, result, _) = await ExecuteAsync(host, state, await Templates.FixtureAsync(), execute: new ExecuteOptions { SecureOutputsFile = path }, store: store);

        Assert.False(result.Succeeded);
        var credential = Assert.Single(result.Operations, o => o.Id.Id == Fixture.Password);
        Assert.Equal(EngineDiagnosticCodes.StateSaveFailed, credential.ErrorCode);
        Assert.True(store.FailedSaves >= FileStateSink.SaveAttempts);
        var written = JsonNode.Parse(await File.ReadAllTextAsync(path, Ct))!.AsObject();
        Assert.StartsWith("fake-secret-", written[SecureOutputsFile.OneTimeSecretsKey]![Fixture.Password]!["secretText"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    // ---- E-03c: state save failures ----

    [Fact]
    public async Task AStateSaveThatFailsForGood_IsAFailedResource_NotACrash()
    {
        var host = new EngineHost();
        var state = NewState();
        var store = new FlakyStateStore(new InMemoryStateStore(), failAfterSaves: 1);

        var (_, result, _) = await ExecuteAsync(host, state, Groups("g1"), store: store);

        Assert.False(result.Succeeded);
        var op = Assert.Single(result.Operations);
        Assert.Equal(OperationStatus.Failed, op.Status);
        Assert.Equal(EngineDiagnosticCodes.StateSaveFailed, op.ErrorCode);
        Assert.Contains("may exist in the tenant", op.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", op.Error, StringComparison.Ordinal);
        Assert.Contains(result.Diagnostics, d => d.Code == EngineDiagnosticCodes.StateSaveFailed && d.IsError);
        Assert.True(store.FailedSaves >= FileStateSink.SaveAttempts, $"retried {store.FailedSaves} time(s)");
    }

    [Fact]
    public async Task AStateSaveThatFailsTwice_IsRetried_AndTheResourceSucceeds()
    {
        var host = new EngineHost();
        var state = NewState();
        var memory = new InMemoryStateStore();
        var store = new FlakyStateStore(memory, failAfterSaves: 1, failures: 2);

        var (_, result, _) = await ExecuteAsync(host, state, Groups("g1"), store: store);

        Assert.True(result.Succeeded, string.Join("\n", result.Operations.Select(o => o.Error)));
        Assert.Equal(2, store.FailedSaves);
        Assert.Contains(GroupId("g1"), memory.DocumentOf(EngineHost.DeploymentName) ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(StateFile.StatusSucceeded, state.LastStatus);
    }

    // ---- E-08: reruns never destroy the only copy ----

    [Fact]
    public async Task ARerun_WithoutNewSecureValues_LeavesTheSecureOutputsFileUntouched()
    {
        using var directory = new TestDirectory(nameof(ARerun_WithoutNewSecureValues_LeavesTheSecureOutputsFileUntouched));
        string path = directory.File("secure.json");
        var host = new EngineHost();
        host.SeedExisting();
        var state = NewState();
        var (_, first, _) = await ExecuteAsync(host, state, await Templates.FixtureAsync(), execute: new ExecuteOptions { SecureOutputsFile = path });
        Assert.True(first.Succeeded, string.Join("\n", first.Operations.Where(o => o.Status != OperationStatus.Succeeded).Select(o => o.Error)));
        string before = await File.ReadAllTextAsync(path, Ct);
        Assert.Contains("fake-secret-", before, StringComparison.Ordinal);

        // The rerun (a template that now produces no secure value, as a rerun of a one-time secret does) must not touch what the first run saved.
        var (_, second, _) = await ExecuteAsync(host, state, Groups("g1"), execute: new ExecuteOptions { SecureOutputsFile = path });

        Assert.True(second.Succeeded, Describe(second));
        Assert.Empty(second.SecureOutputs);
        Assert.Null(second.SecureOutputsFile);
        Assert.Equal(before, await File.ReadAllTextAsync(path, Ct));
    }

    [Fact]
    public async Task ARunWithoutSecureValues_NeverCreatesTheFile()
    {
        using var directory = new TestDirectory(nameof(ARunWithoutSecureValues_NeverCreatesTheFile));
        string path = directory.File("secure.json");
        var host = new EngineHost();

        var (_, result, _) = await ExecuteAsync(host, NewState(), Groups("g1"), execute: new ExecuteOptions { SecureOutputsFile = path });

        Assert.True(result.Succeeded);
        Assert.False(File.Exists(path));
        Assert.Null(result.SecureOutputsFile);
    }

    [Fact]
    public async Task SecureOutputs_AreMergedIntoAnExistingFile_NeverReplacingIt()
    {
        using var directory = new TestDirectory(nameof(SecureOutputs_AreMergedIntoAnExistingFile_NeverReplacingIt));
        string path = directory.File("secure.json");
        await File.WriteAllTextAsync(path, "{ \"keepMe\": { \"type\": \"securestring\", \"value\": \"from-an-earlier-run\" }, \"ciSecret\": { \"type\": \"securestring\", \"value\": \"superseded\" } }", Ct);
        var host = new EngineHost();
        host.SeedExisting();

        var (_, result, _) = await ExecuteAsync(host, NewState(), await Templates.FixtureAsync(), execute: new ExecuteOptions { SecureOutputsFile = path });

        Assert.True(result.Succeeded);
        var written = JsonNode.Parse(await File.ReadAllTextAsync(path, Ct))!.AsObject();
        Assert.Equal("from-an-earlier-run", written["keepMe"]!["value"]!.GetValue<string>());
        Assert.StartsWith("fake-secret-", written["ciSecret"]!["value"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.NotNull(written[SecureOutputsFile.OneTimeSecretsKey]![Fixture.Password]);
        Assert.Equal(SecureOutputsFile.OneTimeSecretsKey, written.Last().Key);
    }

    [Fact]
    public async Task AnUnreadableExistingFile_IsKeptNextToTheNewOne()
    {
        using var directory = new TestDirectory(nameof(AnUnreadableExistingFile_IsKeptNextToTheNewOne));
        string path = directory.File("secure.json");
        await File.WriteAllTextAsync(path, "this is not json but may be the only copy of something", Ct);
        var host = new EngineHost();
        host.SeedExisting();

        var (_, result, _) = await ExecuteAsync(host, NewState(), await Templates.FixtureAsync(), execute: new ExecuteOptions { SecureOutputsFile = path });

        Assert.True(result.Succeeded);
        Assert.Equal("this is not json but may be the only copy of something", await File.ReadAllTextAsync(path + ".previous", Ct));
        Assert.NotNull(JsonNode.Parse(await File.ReadAllTextAsync(path, Ct)));
    }

    // ---- S-02: secure values in error texts ----

    private static EArm.Core.TemplateDocument SecretGroup()
    {
        return Templates.Parse(
            "\"parameters\": { \"secret\": { \"type\": \"securestring\" } }, \"resources\": [ { \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"" + ApiVersion + "\", \"name\": \"g\", \"properties\": { \"displayName\": \"G\", \"mailEnabled\": false, \"securityEnabled\": true, \"description\": \"[parameters('secret')]\" } } ]",
            ("secret", Secret));
    }

    [Fact]
    public async Task AGraphErrorThatEchoesASecureValue_NeverReachesTheStateTheResultOrTheLog()
    {
        using var scope = SecretScrubber.Begin();
        var host = new EngineHost();
        host.Fake.Failures.Add("PATCH", "/groups", request => new GraphException(HttpStatusCode.BadRequest, "Request_BadRequest", $"The value '{Secret}' is not valid for property description.", request.Hint), times: 5);
        var state = NewState();

        var (_, result, store) = await ExecuteAsync(host, state, SecretGroup());

        Assert.False(result.Succeeded);
        var op = Assert.Single(result.Operations);
        Assert.Contains("***", op.Error, StringComparison.Ordinal);
        AssertNoSecret(result, store);
    }

    [Fact]
    public async Task AnyOtherErrorTextThatEchoesASecureValue_IsMaskedByTheExecutorItself()
    {
        // No SecretScrubber.Begin() here and the exception is not a Graph one: the executor registers the secure parameters and masks every error it records.
        var host = new EngineHost(graph => new ThrowingPatchClient(graph, new InvalidOperationException($"serializer rejected '{Secret}'")));
        var state = NewState();

        var (_, result, store) = await ExecuteAsync(host, state, SecretGroup());

        Assert.False(result.Succeeded);
        Assert.Contains("***", Assert.Single(result.Operations).Error, StringComparison.Ordinal);
        AssertNoSecret(result, store);
    }

    [Fact]
    public async Task WhatIf_AReadErrorThatEchoesASecureValue_IsMasked()
    {
        var host = new EngineHost();
        host.Fake.Failures.Add("GET", "/groups", request => new GraphException(HttpStatusCode.BadRequest, "Request_BadRequest", $"Bad filter near '{Secret}'", request.Hint), times: 5);

        var plan = await host.WhatIfAsync(SecretGroup());

        var change = Assert.Single(plan.Changes);
        Assert.True(change.HasError);
        Assert.DoesNotContain(Secret, change.UnsupportedReason, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, string.Join("\n", plan.Diagnostics.Select(d => d.Message)), StringComparison.Ordinal);
    }

    private static string Describe(DeploymentResult result)
    {
        return string.Join("; ", result.Operations.Where(o => o.Status is OperationStatus.Failed or OperationStatus.Skipped).Select(o => o.Id.Id + ": " + o.Status + " " + o.Error))
            + " | " + string.Join("; ", result.Diagnostics.Where(d => d.IsError).Select(d => d.ToString()));
    }

    private static void AssertNoSecret(DeploymentResult result, InMemoryStateStore store)
    {
        Assert.DoesNotContain(Secret, store.DocumentOf(EngineHost.DeploymentName) ?? string.Empty, StringComparison.Ordinal);
        foreach (string save in store.Saves)
        {
            Assert.DoesNotContain(Secret, save, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(Secret, string.Join("\n", result.Operations.Select(o => o.Error)), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, string.Join("\n", result.Diagnostics.Select(d => d.Message)), StringComparison.Ordinal);
    }

    // ---- E-05: a create with an unknown outcome ----

    [Fact]
    public async Task ACreateThatTimesOutAfterGraphCommittedIt_IsCountedAsDone_WithAWarning()
    {
        var host = new EngineHost(graph => new CommitThenFailGraphClient(
            graph,
            request => request.Path.StartsWith("/groups(", StringComparison.Ordinal),
            _ => new GraphTransportException(GraphTransportFailure.Timeout, "PATCH: the request timed out.", ambiguous: true),
            times: 10));
        var state = NewState();

        var (_, result, _) = await ExecuteAsync(host, state, Groups("g1"));

        Assert.True(result.Succeeded, string.Join("\n", result.Operations.Select(o => o.Error)));
        Assert.Equal(1, host.Directory.Count("/groups"));
        var warning = Assert.Single(result.Diagnostics, d => d.Code == EngineDiagnosticCodes.CreateOutcomeUncertain);
        Assert.Contains(GroupId("g1"), warning.Message, StringComparison.Ordinal);
        var op = Assert.Single(result.Operations);
        Assert.Equal(ChangeType.Create, op.ChangeType);
        Assert.Equal(StateProvenance.Created, state.Find(GroupId("g1"))!.Provenance);
        Assert.Equal(IdOf(host.Directory.Objects("/groups").Single()), state.GraphIdOf(GroupId("g1")));
    }

    [Fact]
    public async Task ACreateThatFailsWith503AfterGraphCommittedIt_IsCountedAsDone()
    {
        var host = new EngineHost(graph => new CommitThenFailGraphClient(
            graph,
            request => request.Path.StartsWith("/groups(", StringComparison.Ordinal),
            request => new GraphException(HttpStatusCode.ServiceUnavailable, "ServiceUnavailable", "The service is unavailable.", request.Hint),
            times: 10));

        var (_, result, _) = await ExecuteAsync(host, NewState(), Groups("g1"));

        Assert.True(result.Succeeded, string.Join("\n", result.Operations.Select(o => o.Error)));
        Assert.Contains(result.Diagnostics, d => d.Code == EngineDiagnosticCodes.CreateOutcomeUncertain);
        Assert.Equal(1, host.Directory.Count("/groups"));
    }

    [Fact]
    public async Task ACreateThatFailsBeforeGraphCommittedIt_StillFails_WithTheOriginalError()
    {
        var host = new EngineHost();
        host.Fake.Failures.Add("PATCH", "/groups", _ => new GraphTransportException(GraphTransportFailure.Timeout, "PATCH: the request timed out."), times: 10);

        var (_, result, _) = await ExecuteAsync(host, NewState(), Groups("g1"));

        Assert.False(result.Succeeded);
        Assert.Contains("timed out", Assert.Single(result.Operations).Error, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == EngineDiagnosticCodes.CreateOutcomeUncertain);
        Assert.Equal(0, host.Directory.Count("/groups"));
    }

    // ---- E-06: replace = create first, delete second ----

    private static EArm.Core.TemplateDocument RoleAssignment(string name, string principalId)
    {
        return Templates.Resources("{ \"type\": \"Microsoft.Entra/roleAssignments\", \"apiVersion\": \"" + ApiVersion + "\", \"name\": \"" + name + "\", \"properties\": { \"roleDefinitionId\": \"" + Gid(900) + "\", \"principalId\": \"" + principalId + "\", \"directoryScopeId\": \"/\" } }");
    }

    private static (JsonObject Old, string OldId, string NewPrincipal, StateFile State) SeedReplacement(EngineHost host, string name)
    {
        var oldUser = host.Directory.AddUser("old@contoso.com", "Old");
        var newUser = host.Directory.AddUser("new@contoso.com", "New");
        var assignment = host.Directory.Add("/roleManagement/directory/roleAssignments", new JsonObject { ["roleDefinitionId"] = Gid(900), ["principalId"] = IdOf(oldUser), ["directoryScopeId"] = "/" });
        var state = NewState();
        state.Upsert(new StateEntry
        {
            Id = StateSerializer.ParseResourceId("/providers/Microsoft.Entra/roleAssignments/" + name, Fixture.RoleAssignmentType)!,
            Type = Fixture.RoleAssignmentType,
            ApiVersion = ApiVersion,
            Key = new Dictionary<string, string> { ["principalId"] = IdOf(oldUser), ["roleDefinitionId"] = Gid(900), ["directoryScopeId"] = "/" },
            GraphId = IdOf(assignment),
            Provenance = StateProvenance.Created,
            LastWriteOn = Start,
        });
        return (assignment, IdOf(assignment), IdOf(newUser), state);
    }

    [Fact]
    public async Task ReplacingAnObject_WhenTheCreateFails_KeepsTheOldObjectAndItsStateEntry()
    {
        var host = new EngineHost();
        string name = Gid(902);
        var (_, oldId, newPrincipal, state) = SeedReplacement(host, name);
        host.Fake.Failures.Forbid("/roleManagement/directory/roleAssignments", "POST");

        var (_, result, _) = await ExecuteAsync(host, state, RoleAssignment(name, newPrincipal));

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(host.Fake.Writes, w => w.Method == "DELETE");
        var remaining = Assert.Single(host.Directory.Objects("/roleManagement/directory/roleAssignments"));
        Assert.Equal(oldId, IdOf(remaining));
        Assert.Equal(oldId, state.GraphIdOf("/providers/Microsoft.Entra/roleAssignments/" + name));
    }

    [Fact]
    public async Task AChangedCompositeKey_IsANewObject_TheOldOneIsNeverReplaced()
    {
        var host = new EngineHost();
        string name = Gid(903);
        var (_, oldId, newPrincipal, state) = SeedReplacement(host, name);
        var entry = state.Find("/providers/Microsoft.Entra/roleAssignments/" + name)!;
        state.Upsert(entry with { Provenance = StateProvenance.Adopted });

        var (_, result, _) = await ExecuteAsync(host, state, RoleAssignment(name, newPrincipal));

        // Identity is the live tuple: a changed tuple is another assignment, created; the old one is left alone whatever the state recorded.
        Assert.DoesNotContain(host.Fake.Writes, w => w.Method == "DELETE");
        Assert.Equal(ChangeType.Create, Assert.Single(result.Operations).ChangeType);
        Assert.Equal(2, host.Directory.Count("/roleManagement/directory/roleAssignments"));
        Assert.Contains(host.Directory.Objects("/roleManagement/directory/roleAssignments"), a => IdOf(a) == oldId);
    }

    // ---- E-11: edges added before another edge of the row failed are still recorded ----

    [Fact]
    public async Task WhenOneEdgeOfARowFails_TheEdgesThatWereAdded_AreStillRecordedInTheState()
    {
        var host = new EngineHost();
        var allStaff = host.SeedExisting()[1];
        string allStaffId = IdOf(allStaff);
        host.Fake.Failures.Forbid("/groups/" + allStaffId + "/members", "POST");
        var state = NewState();

        var (_, result, _) = await ExecuteAsync(host, state, await Templates.FixtureAsync());

        Assert.False(result.Succeeded);
        var user = Assert.Single(result.Operations, o => o.Id.Id == Fixture.User);
        Assert.Equal(OperationStatus.Failed, user.Status);
        Assert.Equal(EngineDiagnosticCodes.EdgeApplyFailed, user.ErrorCode);
        var recorded = state.Find(Fixture.User)!.Relationships;
        Assert.NotNull(recorded);
        var memberOf = Assert.IsType<JsonArray>(recorded["memberOf"]);
        string admins = IdOf(host.Directory.Find("/groups", "uniqueName", "contoso-dev-orders-admins")!);
        Assert.Contains(memberOf, node => node!.ToJsonString().Contains(admins, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(memberOf, node => node!.ToJsonString().Contains(allStaffId, StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed class CancelAfterPostClient : GraphClientDecorator
{
    private readonly string _pathSuffix;
    private readonly CancellationTokenSource _source;

    public CancelAfterPostClient(IGraphClient inner, string pathSuffix, CancellationTokenSource source)
        : base(inner)
    {
        _pathSuffix = pathSuffix;
        _source = source;
    }

    public override async Task<GraphResponse> PostAsync(GraphRequest request, JsonNode? body, CancellationToken cancellationToken)
    {
        var response = await base.PostAsync(request, body, cancellationToken);
        if (request.Path.EndsWith(_pathSuffix, StringComparison.Ordinal))
        {
            await _source.CancelAsync();
        }

        return response;
    }
}

internal sealed class ThrowingPatchClient : GraphClientDecorator
{
    private readonly Exception _exception;

    public ThrowingPatchClient(IGraphClient inner, Exception exception)
        : base(inner)
    {
        _exception = exception;
    }

    public override Task<GraphResponse> PatchAsync(GraphRequest request, JsonNode body, CancellationToken cancellationToken)
    {
        throw _exception;
    }
}

/// <summary>Records the content of the secure outputs file at the moment the state records one resource.</summary>
internal sealed class FileProbeSink : IStateSink
{
    private readonly IStateSink _inner;
    private readonly string _path;
    private readonly string _resourceId;

    public FileProbeSink(IStateSink inner, string path, string resourceId)
    {
        _inner = inner;
        _path = path;
        _resourceId = resourceId;
    }

    public string? FileAtApply { get; private set; }

    public Task ResourceAppliedAsync(ResourceInstance instance, ResourceChange change, AppliedResource applied, CancellationToken cancellationToken)
    {
        if (instance.Id.Id == _resourceId)
        {
            FileAtApply = File.Exists(_path) ? File.ReadAllText(_path) : null;
        }

        return _inner.ResourceAppliedAsync(instance, change, applied, cancellationToken);
    }

    public Task ResourceFailedAsync(ResourceInstance instance, string error, CancellationToken cancellationToken)
    {
        return _inner.ResourceFailedAsync(instance, error, cancellationToken);
    }

    public Task CompletedAsync(DeploymentStatus status, CancellationToken cancellationToken)
    {
        return _inner.CompletedAsync(status, cancellationToken);
    }

    public Task ResourceStartingAsync(ResourceInstance instance, CancellationToken cancellationToken)
    {
        return _inner.ResourceStartingAsync(instance, cancellationToken);
    }

    public Task ResourceSkippedAsync(ResourceInstance instance, string reason, CancellationToken cancellationToken)
    {
        return _inner.ResourceSkippedAsync(instance, reason, cancellationToken);
    }

    public Task ResourceIgnoredAsync(ResourceInstance instance, string reason, CancellationToken cancellationToken)
    {
        return _inner.ResourceIgnoredAsync(instance, reason, cancellationToken);
    }

    public Task ResourceDetachedAsync(StateEntry entry, CancellationToken cancellationToken)
    {
        return _inner.ResourceDetachedAsync(entry, cancellationToken);
    }

    public Task ResourceDeletedAsync(StateEntry entry, CancellationToken cancellationToken)
    {
        return _inner.ResourceDeletedAsync(entry, cancellationToken);
    }

    public Task ResourceDeleteFailedAsync(StateEntry entry, string error, CancellationToken cancellationToken)
    {
        return _inner.ResourceDeleteFailedAsync(entry, error, cancellationToken);
    }

    public Task ResourceEdgesAppliedAsync(ResourceInstance instance, JsonObject? relationships, CancellationToken cancellationToken)
    {
        return _inner.ResourceEdgesAppliedAsync(instance, relationships, cancellationToken);
    }

    public Task ResourceEdgesFailedAsync(ResourceInstance instance, string error, CancellationToken cancellationToken)
    {
        return _inner.ResourceEdgesFailedAsync(instance, error, cancellationToken);
    }
}

/// <summary>An <see cref="IStateStore"/> whose saves fail with an <see cref="IOException"/> after the first N succeeded (a limited number of times, or for good).</summary>
internal sealed class FlakyStateStore : IStateStore
{
    private readonly InMemoryStateStore _inner;
    private readonly int _failAfterSaves;
    private readonly int _failures;
    private int _saves;
    private int _failed;

    private readonly Func<StateFile, bool>? _failWhen;

    public FlakyStateStore(InMemoryStateStore inner, int failAfterSaves, int failures = int.MaxValue, Func<StateFile, bool>? failWhen = null)
    {
        _inner = inner;
        _failAfterSaves = failAfterSaves;
        _failures = failures;
        _failWhen = failWhen;
    }

    public int FailedSaves => Volatile.Read(ref _failed);

    public Task<StateFile?> LoadAsync(string deploymentName, CancellationToken cancellationToken)
    {
        return _inner.LoadAsync(deploymentName, cancellationToken);
    }

    public Task SaveAsync(StateFile state, CancellationToken cancellationToken)
    {
        int saves = Interlocked.Increment(ref _saves);
        if (saves > _failAfterSaves && Volatile.Read(ref _failed) < _failures && (_failWhen is null || _failWhen(state)))
        {
            Interlocked.Increment(ref _failed);
            throw new IOException("The process cannot access the file because it is being used by another process.");
        }

        return _inner.SaveAsync(state, cancellationToken);
    }

    public Task<bool> DeleteAsync(string deploymentName, CancellationToken cancellationToken)
    {
        return _inner.DeleteAsync(deploymentName, cancellationToken);
    }

    public Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken)
    {
        return _inner.ListAsync(cancellationToken);
    }

    public Task<IAsyncDisposable> LockAsync(string deploymentName, TimeSpan wait, CancellationToken cancellationToken)
    {
        return _inner.LockAsync(deploymentName, wait, cancellationToken);
    }

    public Task<StateLockInfo?> GetLockAsync(string deploymentName, CancellationToken cancellationToken)
    {
        return _inner.GetLockAsync(deploymentName, cancellationToken);
    }

    public Task<StateLockInfo?> ForceUnlockAsync(string deploymentName, CancellationToken cancellationToken)
    {
        return _inner.ForceUnlockAsync(deploymentName, cancellationToken);
    }
}
