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
using EArm.Graph;
using EArm.TestSupport;
using Xunit;
using static EArm.Tests.Engine.Review1A;

namespace EArm.Tests.Engine;

/// <summary>
/// Review findings E-03 / E-08 (one-time secrets survive failure, cancellation and reruns), S-02 (secure values never reach logs or
/// output through an error text), E-05 (an ambiguous create) and E-06 (a changed tuple is a new object; the old one is left alone).
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
    public async Task Cancellation_AfterThePasswordCredentialExists_StillSavesTheSecret()
    {
        using var directory = new TestDirectory(nameof(Cancellation_AfterThePasswordCredentialExists_StillSavesTheSecret));
        string path = directory.File("secure.json");
        using var cts = new CancellationTokenSource();
        var host = new EngineHost(graph => new CancelAfterPostClient(graph, "/addPassword", cts));
        host.SeedExisting();
        var plan = await host.ExpandAsync(await Templates.FixtureAsync());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Executor().ExecuteAsync(plan, new ExecuteOptions { SecureOutputsFile = path }, cts.Token));

        var written = JsonNode.Parse(await File.ReadAllTextAsync(path, Ct))!.AsObject();
        var credential = written[SecureOutputsFile.OneTimeSecretsKey]![Fixture.Password]!.AsObject();
        Assert.StartsWith("fake-secret-", credential["secretText"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(credential["keyId"]?.GetValue<string>()));
    }

    // ---- E-08: reruns never destroy the only copy ----

    [Fact]
    public async Task ARerun_WithoutNewSecureValues_LeavesTheSecureOutputsFileUntouched()
    {
        using var directory = new TestDirectory(nameof(ARerun_WithoutNewSecureValues_LeavesTheSecureOutputsFileUntouched));
        string path = directory.File("secure.json");
        var host = new EngineHost();
        host.SeedExisting();
        var (_, first) = await ExecuteAsync(host, await Templates.FixtureAsync(), execute: new ExecuteOptions { SecureOutputsFile = path });
        Assert.True(first.Succeeded, string.Join("\n", first.Operations.Where(o => o.Status != OperationStatus.Succeeded).Select(o => o.Error)));
        string before = await File.ReadAllTextAsync(path, Ct);
        Assert.Contains("fake-secret-", before, StringComparison.Ordinal);

        // The rerun (a template that now produces no secure value, as a rerun of a one-time secret does) must not touch what the first run saved.
        var (_, second) = await ExecuteAsync(host, Groups("g1"), execute: new ExecuteOptions { SecureOutputsFile = path });

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

        var (_, result) = await ExecuteAsync(host, Groups("g1"), execute: new ExecuteOptions { SecureOutputsFile = path });

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

        var (_, result) = await ExecuteAsync(host, await Templates.FixtureAsync(), execute: new ExecuteOptions { SecureOutputsFile = path });

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

        var (_, result) = await ExecuteAsync(host, await Templates.FixtureAsync(), execute: new ExecuteOptions { SecureOutputsFile = path });

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
    public async Task AGraphErrorThatEchoesASecureValue_NeverReachesTheResultOrTheLog()
    {
        using var scope = SecretScrubber.Begin();
        var host = new EngineHost();
        host.Fake.Failures.Add("PATCH", "/groups", request => new GraphException(HttpStatusCode.BadRequest, "Request_BadRequest", $"The value '{Secret}' is not valid for property description.", request.Hint), times: 5);

        var (_, result) = await ExecuteAsync(host, SecretGroup());

        Assert.False(result.Succeeded);
        var op = Assert.Single(result.Operations);
        Assert.Contains("***", op.Error, StringComparison.Ordinal);
        AssertNoSecret(result);
    }

    [Fact]
    public async Task AnyOtherErrorTextThatEchoesASecureValue_IsMaskedByTheExecutorItself()
    {
        // No SecretScrubber.Begin() here and the exception is not a Graph one: the executor registers the secure parameters and masks every error it records.
        var host = new EngineHost(graph => new ThrowingPatchClient(graph, new InvalidOperationException($"serializer rejected '{Secret}'")));

        var (_, result) = await ExecuteAsync(host, SecretGroup());

        Assert.False(result.Succeeded);
        Assert.Contains("***", Assert.Single(result.Operations).Error, StringComparison.Ordinal);
        AssertNoSecret(result);
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

    private static void AssertNoSecret(DeploymentResult result)
    {
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

        var (_, result) = await ExecuteAsync(host, Groups("g1"));

        Assert.True(result.Succeeded, string.Join("\n", result.Operations.Select(o => o.Error)));
        Assert.Equal(1, host.Directory.Count("/groups"));
        var warning = Assert.Single(result.Diagnostics, d => d.Code == EngineDiagnosticCodes.CreateOutcomeUncertain);
        Assert.Contains(GroupId("g1"), warning.Message, StringComparison.Ordinal);
        var op = Assert.Single(result.Operations);
        Assert.Equal(ChangeType.Create, op.ChangeType);
        Assert.Equal(IdOf(host.Directory.Objects("/groups").Single()), op.GraphId);
    }

    [Fact]
    public async Task ACreateThatFailsWith503AfterGraphCommittedIt_IsCountedAsDone()
    {
        var host = new EngineHost(graph => new CommitThenFailGraphClient(
            graph,
            request => request.Path.StartsWith("/groups(", StringComparison.Ordinal),
            request => new GraphException(HttpStatusCode.ServiceUnavailable, "ServiceUnavailable", "The service is unavailable.", request.Hint),
            times: 10));

        var (_, result) = await ExecuteAsync(host, Groups("g1"));

        Assert.True(result.Succeeded, string.Join("\n", result.Operations.Select(o => o.Error)));
        Assert.Contains(result.Diagnostics, d => d.Code == EngineDiagnosticCodes.CreateOutcomeUncertain);
        Assert.Equal(1, host.Directory.Count("/groups"));
    }

    [Fact]
    public async Task ACreateThatFailsBeforeGraphCommittedIt_StillFails_WithTheOriginalError()
    {
        var host = new EngineHost();
        host.Fake.Failures.Add("PATCH", "/groups", _ => new GraphTransportException(GraphTransportFailure.Timeout, "PATCH: the request timed out."), times: 10);

        var (_, result) = await ExecuteAsync(host, Groups("g1"));

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

    private static (JsonObject Old, string OldId, string NewPrincipal) SeedReplacement(EngineHost host)
    {
        var oldUser = host.Directory.AddUser("old@contoso.com", "Old");
        var newUser = host.Directory.AddUser("new@contoso.com", "New");
        var assignment = host.Directory.Add("/roleManagement/directory/roleAssignments", new JsonObject { ["roleDefinitionId"] = Gid(900), ["principalId"] = IdOf(oldUser), ["directoryScopeId"] = "/" });
        return (assignment, IdOf(assignment), IdOf(newUser));
    }

    [Fact]
    public async Task AChangedTuple_WhenTheCreateFails_LeavesTheOldObjectAlone()
    {
        var host = new EngineHost();
        string name = Gid(902);
        var (_, oldId, newPrincipal) = SeedReplacement(host);
        host.Fake.Failures.Forbid("/roleManagement/directory/roleAssignments", "POST");

        var (_, result) = await ExecuteAsync(host, RoleAssignment(name, newPrincipal));

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(host.Fake.Writes, w => w.Method == "DELETE");
        var remaining = Assert.Single(host.Directory.Objects("/roleManagement/directory/roleAssignments"));
        Assert.Equal(oldId, IdOf(remaining));
    }

    [Fact]
    public async Task AChangedCompositeKey_IsANewObject_TheOldOneIsNeverReplaced()
    {
        var host = new EngineHost();
        string name = Gid(903);
        var (_, oldId, newPrincipal) = SeedReplacement(host);

        var (_, result) = await ExecuteAsync(host, RoleAssignment(name, newPrincipal));

        // Identity is the live tuple: a changed tuple is another assignment, created; the old one is left alone.
        Assert.DoesNotContain(host.Fake.Writes, w => w.Method == "DELETE");
        Assert.Equal(ChangeType.Create, Assert.Single(result.Operations).ChangeType);
        Assert.Equal(2, host.Directory.Count("/roleManagement/directory/roleAssignments"));
        Assert.Contains(host.Directory.Objects("/roleManagement/directory/roleAssignments"), a => IdOf(a) == oldId);
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
