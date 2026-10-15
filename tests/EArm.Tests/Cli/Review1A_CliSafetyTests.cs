using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Cli;
using EArm.Cli.Commands;
using EArm.Engine;
using EArm.Engine.Planning;
using EArm.Engine.State;
using EArm.Graph;
using EArm.Tests.Engine;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// The command-line side of the review-1A findings: the Complete-mode blast radius (I-06), the secure outputs file check (E-03a), lock before
/// state (E-04), clean state errors (S-06) and the adoption flags (E-02), driven in-process against the scripted Graph.
/// </summary>
[Collection(CliCollection.Name)]
public sealed class Review1A_CliSafetyTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string[] Args(CliSandbox sandbox, params string[] args)
    {
        return [.. args, .. sandbox.StateArgs];
    }

    private static string Id(char c, int n)
    {
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{c}0000000-0000-4000-8000-{n:D12}");
    }

    private static StateEntry GroupEntry(string name, string graphId, StateProvenance provenance = StateProvenance.Created)
    {
        return new StateEntry
        {
            Id = EArm.Core.ResourceIdentifier.Create(Fixture.GroupType, name),
            Type = Fixture.GroupType,
            ApiVersion = "2026-10-01-preview",
            Key = new Dictionary<string, string> { ["uniqueName"] = name },
            GraphId = graphId,
            Provenance = provenance,
            LastWriteOn = Start,
        };
    }

    private static StateEntry AppEntry(string name, string graphId, StateProvenance provenance = StateProvenance.Created)
    {
        return GroupEntry(name, graphId, provenance) with
        {
            Id = EArm.Core.ResourceIdentifier.Create(Fixture.AppType, name),
            Type = Fixture.AppType,
        };
    }

    private static async Task<StateFile> SeedAsync(CliSandbox sandbox, string deployment, params StateEntry[] entries)
    {
        var state = StateFile.CreateNew(deployment, CliSandbox.Tenant, Start);
        foreach (var entry in entries)
        {
            state.Upsert(entry);
        }

        state.CompleteRun(true, "Incremental", "sha256:previous", Start);
        return await sandbox.WriteStateAsync(state);
    }

    private static string Get(string collection, string uniqueName, string id)
    {
        return "{ \"match\": { \"method\": \"GET\", \"urlContains\": \"" + collection + "(uniqueName='" + uniqueName + "')\" }, \"status\": 200, \"body\": { \"id\": \"" + id + "\", \"uniqueName\": \"" + uniqueName + "\", \"displayName\": \"Live " + uniqueName + "\", \"mailEnabled\": false, \"securityEnabled\": true } }";
    }

    private static string Delete(string collection, string id)
    {
        return "{ \"match\": { \"method\": \"DELETE\", \"urlContains\": \"" + collection + "/" + id + "\" }, \"status\": 204 }";
    }

    private static string KeepTemplate(CliSandbox sandbox)
    {
        return sandbox.WriteTemplate("keep.entra.json", "\"resources\": [ { \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"keep\", \"condition\": false, \"properties\": { \"displayName\": \"Keep\", \"mailEnabled\": false, \"securityEnabled\": true } } ]");
    }

    private static int Deletes(CliSandbox sandbox)
    {
        return sandbox.RecordedCommands().Count(c => c.StartsWith("DELETE ", StringComparison.Ordinal));
    }

    // ---- Complete mode never deletes (it replaced the I-06 blast-radius limits) ----

    [Fact]
    public async Task Complete_WithAnEmptyTemplate_AndANonEmptyState_DeletesNothing_AndNeedsNoFlag()
    {
        using var sandbox = new CliSandbox(nameof(Complete_WithAnEmptyTemplate_AndANonEmptyState_DeletesNothing_AndNeedsNoFlag));
        sandbox.UsePreflightOk();
        string template = sandbox.WriteEmptyTemplate("wipe.entra.json");
        await SeedAsync(sandbox, "wipe", GroupEntry("g1", Id('a', 1)));

        var result = await CliRunner.RunAsync(Args(sandbox, "deployment", "create", "-f", template, "--mode", "Complete", "-y", "-o", "json"));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(0, Deletes(sandbox));
        Assert.Empty(result.Json["orphans"]!.AsArray());
        Assert.False(result.Json.ContainsKey("deleted"));
    }

    [Theory]
    [InlineData("--max-deletes", "3")]
    [InlineData("--allow-delete-all", null)]
    [InlineData("--delete-adopted", null)]
    public void TheRemovedDeleteFlags_AreUnknownOptions(string flag, string? value)
    {
        string[] args = value is null ? ["deployment", "create", "-f", "t.json", flag] : ["deployment", "create", "-f", "t.json", flag, value];

        var parse = Program.BuildRootCommand().Parse(args);

        Assert.NotEmpty(parse.Errors);
    }

    // ---- E-03a ----

    [Theory]
    [InlineData("directory")]
    [InlineData("missing-folder")]
    public async Task ABadSecureOutputsFile_StopsTheRunBeforeAnythingIsSentOrWritten(string kind)
    {
        using var sandbox = new CliSandbox(nameof(ABadSecureOutputsFile_StopsTheRunBeforeAnythingIsSentOrWritten) + kind);
        sandbox.UsePreflightOk();
        string template = sandbox.WriteEmptyTemplate();
        string target = kind == "directory" ? sandbox.TemplatesDir : Path.Combine(sandbox.Root, "no-such-folder", "secure.json");

        var result = await CliRunner.RunAsync(Args(sandbox, "deployment", "create", "-f", template, "-y", "--secure-outputs-file", target));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--secure-outputs-file", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("Deployment not started.", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(sandbox.RecordedCommands());
        Assert.False(Directory.Exists(sandbox.StateDir));
    }

    [Fact]
    public async Task WhatIf_DoesNotCheckTheSecureOutputsFile()
    {
        using var sandbox = new CliSandbox(nameof(WhatIf_DoesNotCheckTheSecureOutputsFile));
        sandbox.UsePreflightOk();
        string template = sandbox.WriteEmptyTemplate();

        var result = await CliRunner.RunAsync(Args(sandbox, "deployment", "what-if", "-f", template));

        Assert.Equal(0, result.ExitCode);
    }

    // ---- E-04: the lock comes before the state ----

    [Fact]
    public async Task Create_TakesTheLockBeforeReadingTheState()
    {
        using var sandbox = new CliSandbox(nameof(Create_TakesTheLockBeforeReadingTheState));
        sandbox.UsePreflightOk();
        string template = sandbox.WriteEmptyTemplate();
        sandbox.WriteRawState("empty", "this is not a state file");
        sandbox.WriteLock("empty", DateTimeOffset.UtcNow);

        var create = await CliRunner.RunAsync(Args(sandbox, "deployment", "create", "-f", template, "-y", "--lock-timeout", "0"));
        var whatIf = await CliRunner.RunAsync(Args(sandbox, "deployment", "what-if", "-f", template));

        // create stops at the lock (exit 4) without having touched the unreadable state; what-if takes no lock and reports the state.
        Assert.Equal(4, create.ExitCode);
        Assert.Contains("is locked by", create.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("not valid JSON", create.StandardError, StringComparison.Ordinal);
        Assert.Equal(1, whatIf.ExitCode);
        Assert.Contains("not valid JSON", whatIf.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhatIf_SaysItReadAnUnlockedSnapshot()
    {
        using var sandbox = new CliSandbox(nameof(WhatIf_SaysItReadAnUnlockedSnapshot));
        sandbox.UsePreflightOk();
        string template = sandbox.WriteEmptyTemplate();
        await SeedAsync(sandbox, "empty", GroupEntry("g1", Id('a', 1)));

        var result = await CliRunner.RunAsync(Args(sandbox, "deployment", "what-if", "-f", template));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("without taking its lock", result.StandardError, StringComparison.Ordinal);
        Assert.False(File.Exists(sandbox.LockPath("empty")));
    }

    [Fact]
    public async Task Forget_TakesTheLockBeforeReadingTheState()
    {
        using var sandbox = new CliSandbox(nameof(Forget_TakesTheLockBeforeReadingTheState));
        await SeedAsync(sandbox, "dep", GroupEntry("g1", Id('a', 1)));
        sandbox.WriteLock("dep", DateTimeOffset.UtcNow);

        var result = await CliRunner.RunAsync(Args(sandbox, "state", "forget", "-n", "dep", "--resource-id", "/providers/Microsoft.Entra/groups/unknown", "-y"));

        Assert.Equal(4, result.ExitCode);
        Assert.Contains("is locked by", result.StandardError, StringComparison.Ordinal);
    }

    // ---- S-06: clean errors ----

    [Fact]
    public async Task List_WithAStateDirThatIsAFile_IsAnError_NotNoDeployments()
    {
        using var sandbox = new CliSandbox(nameof(List_WithAStateDirThatIsAFile_IsAnError_NotNoDeployments));
        string file = Path.Combine(sandbox.Root, "state-is-a-file");
        await File.WriteAllTextAsync(file, "x", Ct);

        var result = await CliRunner.RunAsync("deployment", "list", "--state-dir", file);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("is a file", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("No deployments", result.StandardOutput + result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Show_OfAStateFileThatCannotBeOpened_IsACleanErrorWithoutAStackTrace()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "exclusive file locks are only enforced on Windows");
        using var sandbox = new CliSandbox(nameof(Show_OfAStateFileThatCannotBeOpened_IsACleanErrorWithoutAStackTrace));
        await SeedAsync(sandbox, "dep", GroupEntry("g1", Id('a', 1)));
        using var exclusive = new FileStream(sandbox.StatePath("dep"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var show = await CliRunner.RunAsync(Args(sandbox, "deployment", "show", "-n", "dep"));
        var operations = await CliRunner.RunAsync(Args(sandbox, "deployment", "operation", "list", "-n", "dep"));
        var forget = await CliRunner.RunAsync(Args(sandbox, "state", "forget", "-n", "dep", "--resource-id", "/providers/Microsoft.Entra/groups/g1", "-y"));

        foreach (var result in new[] { show, operations, forget })
        {
            Assert.Equal(1, result.ExitCode);
            Assert.Contains("could not be", result.StandardError, StringComparison.Ordinal);
            Assert.DoesNotContain("   at ", result.StandardError, StringComparison.Ordinal);
            Assert.DoesNotContain(".cs:line", result.StandardError, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("COM1")]
    [InlineData("name.")]
    public async Task AReservedDeploymentName_IsRefusedByTheStateCommands(string name)
    {
        using var sandbox = new CliSandbox(nameof(AReservedDeploymentName_IsRefusedByTheStateCommands));

        var show = await CliRunner.RunAsync(Args(sandbox, "deployment", "show", "-n", name));
        var unlock = await CliRunner.RunAsync(Args(sandbox, "state", "unlock", "-n", name));

        Assert.Equal(1, show.ExitCode);
        Assert.Contains("is not valid", show.StandardError, StringComparison.Ordinal);
        Assert.Equal(1, unlock.ExitCode);
        Assert.False(Directory.Exists(sandbox.StateDir));
    }

    // ---- E-02: adoption through the CLI ----

    [Fact]
    public async Task StateAdoptPin_RecordsTheEntryAsAdopted_AndAcceptsOpaqueIdsOfAssignments()
    {
        using var sandbox = new CliSandbox(nameof(StateAdoptPin_RecordsTheEntryAsAdopted_AndAcceptsOpaqueIdsOfAssignments));
        string assignment = "/providers/Microsoft.Entra/roleAssignments/" + Id('b', 1);

        var app = await CliRunner.RunAsync(Args(sandbox, "state", "adopt", "--deployment", "dep", "--resource-id", "/providers/Microsoft.Entra/applications/orders-api", "--object-id", Id('c', 1), "-y"));
        var role = await CliRunner.RunAsync(Args(sandbox, "state", "adopt", "--deployment", "dep", "--resource-id", assignment, "--object-id", "AzC8ELHhY0eVMkKAOQtymW1jTUZZ8qNOvDUlRZfSNxQ-1", "--key", "principalId=" + Id('d', 1), "--key", "roleDefinitionId=" + Id('e', 1), "--key", "directoryScopeId=/", "-y"));
        var bad = await CliRunner.RunAsync(Args(sandbox, "state", "adopt", "--deployment", "dep", "--resource-id", "/providers/Microsoft.Entra/groups/g", "--object-id", "me", "-y"));

        Assert.True(app.ExitCode == 0, app.StandardError);
        Assert.True(role.ExitCode == 0, role.StandardError);
        Assert.Equal(1, bad.ExitCode);
        Assert.Contains("is not a GUID", bad.StandardError, StringComparison.Ordinal);
        var state = (await sandbox.Store.LoadAsync("dep", Ct))!;
        Assert.Equal(StateProvenance.Adopted, state.Find("/providers/Microsoft.Entra/applications/orders-api")!.Provenance);
        Assert.Equal(StateProvenance.Adopted, state.Find(assignment)!.Provenance);
        Assert.Equal("AzC8ELHhY0eVMkKAOQtymW1jTUZZ8qNOvDUlRZfSNxQ-1", state.GraphIdOf(assignment));
    }

    [Fact]
    public async Task ADeploymentError_ThatEchoesASecureParameter_IsMaskedOnTheConsole()
    {
        using var sandbox = new CliSandbox(nameof(ADeploymentError_ThatEchoesASecureParameter_IsMaskedOnTheConsole));
        string template = sandbox.WriteTemplate("secret.entra.json", "\"parameters\": { \"pw\": { \"type\": \"securestring\" } }, \"resources\": [ { \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"g\", \"properties\": { \"displayName\": \"G\", \"mailEnabled\": false, \"securityEnabled\": true, \"description\": \"[parameters('pw')]\" } } ]");
        sandbox.UsePreflightOk(extraResponses:
            "{ \"match\": { \"method\": \"GET\", \"urlContains\": \"/groups\" }, \"status\": 404, \"body\": { \"error\": { \"code\": \"Request_ResourceNotFound\", \"message\": \"gone\" } } }," +
            "{ \"match\": { \"method\": \"PATCH\", \"urlContains\": \"/groups\" }, \"status\": 400, \"body\": { \"error\": { \"code\": \"Request_BadRequest\", \"message\": \"The value 'Hunter2-Secret-Value!' is not valid for description.\" } } }," +
            "{ \"match\": { \"method\": \"POST\", \"urlContains\": \"/groups\" }, \"status\": 400, \"body\": { \"error\": { \"code\": \"Request_BadRequest\", \"message\": \"The value 'Hunter2-Secret-Value!' is not valid for description.\" } } }");

        var result = await CliRunner.RunAsync(Args(sandbox, "deployment", "create", "-f", template, "-p", "pw=Hunter2-Secret-Value!", "-y", "-o", "json"));

        Assert.Equal(1, result.ExitCode);
        Assert.DoesNotContain("Hunter2-Secret-Value!", result.StandardOutput + result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("Hunter2-Secret-Value!", File.ReadAllText(sandbox.StatePath("secret")), StringComparison.Ordinal);
    }
}
