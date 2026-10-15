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

    // ---- I-06: Complete-mode blast radius ----

    [Fact]
    public async Task Complete_ATemplateWithoutResources_AndANonEmptyState_NeedsAllowDeleteAll_EvenWithYes()
    {
        using var sandbox = new CliSandbox(nameof(Complete_ATemplateWithoutResources_AndANonEmptyState_NeedsAllowDeleteAll_EvenWithYes));
        sandbox.UsePreflightOk(extraResponses: Get("/groups", "g1", Id('a', 1)) + "," + Delete("/groups", Id('a', 1)));
        string template = sandbox.WriteEmptyTemplate("wipe.entra.json");
        await SeedAsync(sandbox, "wipe", GroupEntry("g1", Id('a', 1)));

        var result = await CliRunner.RunAsync(Args(sandbox, "deployment", "create", "-f", template, "--mode", "Complete", "-y"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--allow-delete-all", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("declares no resources", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("Deployment not started.", result.StandardError, StringComparison.Ordinal);
        Assert.Equal(0, Deletes(sandbox));
        var state = (await sandbox.Store.LoadAsync("wipe", Ct))!;
        Assert.Single(state.Resources);
        Assert.Equal(StateFile.StatusSucceeded, state.LastStatus);
        Assert.False(File.Exists(sandbox.LockPath("wipe")));
    }

    [Fact]
    public async Task Complete_WithAllowDeleteAll_AndYes_DeletesTheOrphans()
    {
        using var sandbox = new CliSandbox(nameof(Complete_WithAllowDeleteAll_AndYes_DeletesTheOrphans));
        sandbox.UsePreflightOk(extraResponses: Get("/groups", "g1", Id('a', 1)) + "," + Delete("/groups", Id('a', 1)));
        string template = sandbox.WriteEmptyTemplate("wipe.entra.json");
        await SeedAsync(sandbox, "wipe", GroupEntry("g1", Id('a', 1)));

        var result = await CliRunner.RunAsync(Args(sandbox, "deployment", "create", "-f", template, "--mode", "Complete", "-y", "--allow-delete-all", "-o", "json"));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, Deletes(sandbox));
        Assert.Equal(1, result.Json["deleted"]!.GetValue<int>());
        Assert.Empty((await sandbox.Store.LoadAsync("wipe", Ct))!.Resources);
    }

    [Fact]
    public async Task Complete_MoreDeletesThanMaxDeletes_NeedsAllowDeleteAll_AndTheLimitIsInclusive()
    {
        using var sandbox = new CliSandbox(nameof(Complete_MoreDeletesThanMaxDeletes_NeedsAllowDeleteAll_AndTheLimitIsInclusive));
        string template = KeepTemplate(sandbox);
        await SeedAsync(sandbox, "keep", GroupEntry("g1", Id('a', 1)), GroupEntry("g2", Id('a', 2)), GroupEntry("g3", Id('a', 3)));
        sandbox.UsePreflightOk();

        var refused = await CliRunner.RunAsync(Args(sandbox, "deployment", "create", "-f", template, "--mode", "Complete", "-y", "--max-deletes", "2"));

        Assert.Equal(1, refused.ExitCode);
        Assert.Contains("3 object(s), more than --max-deletes 2", refused.StandardError, StringComparison.Ordinal);
        Assert.Equal(0, Deletes(sandbox));

        sandbox.UsePreflightOk(extraResponses: string.Join(",", new[] { 1, 2, 3 }.Select(n => Get("/groups", "g" + n, Id('a', n)) + "," + Delete("/groups", Id('a', n)))));
        var atTheLimit = await CliRunner.RunAsync(Args(sandbox, "deployment", "create", "-f", template, "--mode", "Complete", "-y", "--max-deletes", "3", "-o", "json"));

        Assert.Equal(0, atTheLimit.ExitCode);
        Assert.Equal(3, atTheLimit.Json["deleted"]!.GetValue<int>());
    }

    [Fact]
    public async Task Complete_AdoptedEntries_DoNotCountTowardsMaxDeletes_AndAreNeverDeletedByDefault()
    {
        using var sandbox = new CliSandbox(nameof(Complete_AdoptedEntries_DoNotCountTowardsMaxDeletes_AndAreNeverDeletedByDefault));
        string template = KeepTemplate(sandbox);
        await SeedAsync(sandbox, "keep", GroupEntry("g1", Id('a', 1), StateProvenance.Adopted), GroupEntry("g2", Id('a', 2), StateProvenance.Adopted), GroupEntry("g3", Id('a', 3)));
        sandbox.UsePreflightOk(extraResponses: Get("/groups", "g3", Id('a', 3)) + "," + Delete("/groups", Id('a', 3)));

        var result = await CliRunner.RunAsync(Args(sandbox, "deployment", "create", "-f", template, "--mode", "Complete", "-y", "--max-deletes", "1", "-o", "json"));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, Deletes(sandbox));
        Assert.Equal(2, result.Diagnostics.Count(d => d!["code"]!.GetValue<string>() == EngineDiagnosticCodes.AdoptedObjectNotDeleted));
        var state = (await sandbox.Store.LoadAsync("keep", Ct))!;
        Assert.Equal(2, state.Resources.Count);
        Assert.All(state.Resources.Values, e => Assert.Equal(StateProvenance.Adopted, e.Provenance));
    }

    [Fact]
    public async Task Complete_DeleteAdopted_DeletesTheAdoptedEntriesToo()
    {
        using var sandbox = new CliSandbox(nameof(Complete_DeleteAdopted_DeletesTheAdoptedEntriesToo));
        string template = KeepTemplate(sandbox);
        await SeedAsync(sandbox, "keep", GroupEntry("g1", Id('a', 1), StateProvenance.Adopted));
        sandbox.UsePreflightOk(extraResponses: Get("/groups", "g1", Id('a', 1)) + "," + Delete("/groups", Id('a', 1)));

        var result = await CliRunner.RunAsync(Args(sandbox, "deployment", "create", "-f", template, "--mode", "Complete", "-y", "--delete-adopted", "-o", "json"));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, Deletes(sandbox));
        Assert.Empty((await sandbox.Store.LoadAsync("keep", Ct))!.Resources);
    }

    [Fact]
    public async Task Complete_WithoutYes_ListsWhatWillBeDeleted_AndAsks()
    {
        using var sandbox = new CliSandbox(nameof(Complete_WithoutYes_ListsWhatWillBeDeleted_AndAsks));
        string template = KeepTemplate(sandbox);
        await SeedAsync(sandbox, "keep", GroupEntry("g1", Id('a', 1)), GroupEntry("g2", Id('a', 2), StateProvenance.Adopted));
        var invocation = new DeploymentInvocation
        {
            TemplateFile = template,
            Mode = DeploymentMode.Complete,
            DeleteAdopted = true,
            Output = OutputFormat.Json,
            StateDir = sandbox.StateDir,
            Connection = sandbox.Connection,
        };
        sandbox.UsePreflightOk(extraResponses: Get("/groups", "g1", Id('a', 1)) + "," + Get("/groups", "g2", Id('a', 2)));
        using var declinedOut = new StringWriter();
        using var declinedErr = new StringWriter();

        int declined = await DeploymentRunner.CreateAsync(invocation, declinedOut, declinedErr, () => "n", Ct);

        Assert.Equal(1, declined);
        string listing = declinedOut.ToString();
        Assert.Contains("Complete mode will DELETE 2 object(s)", listing, StringComparison.Ordinal);
        Assert.Contains("/providers/Microsoft.Entra/groups/g1 (Microsoft.Entra/groups)", listing, StringComparison.Ordinal);
        Assert.Contains("\"Live g1\"", listing, StringComparison.Ordinal);
        Assert.Contains("/providers/Microsoft.Entra/groups/g2 (Microsoft.Entra/groups) \"Live g2\" [adopted]", listing, StringComparison.Ordinal);
        Assert.Contains("Deployment not started.", declinedErr.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, Deletes(sandbox));
        Assert.Equal(2, (await sandbox.Store.LoadAsync("keep", Ct))!.Resources.Count);

        sandbox.UsePreflightOk(extraResponses: Get("/groups", "g1", Id('a', 1)) + "," + Get("/groups", "g2", Id('a', 2)) + "," + Get("/groups", "g1", Id('a', 1)) + "," + Delete("/groups", Id('a', 1)) + "," + Get("/groups", "g2", Id('a', 2)) + "," + Delete("/groups", Id('a', 2)));
        using var acceptedOut = new StringWriter();
        using var acceptedErr = new StringWriter();

        int accepted = await DeploymentRunner.CreateAsync(invocation, acceptedOut, acceptedErr, () => "y", Ct);

        Assert.Equal(0, accepted);
        Assert.Equal(2, Deletes(sandbox));
    }

    [Fact]
    public async Task Complete_WithoutYes_OnRedirectedStdin_RefusesWithTheYesHint_AndDeletesNothing()
    {
        Assert.SkipUnless(Console.IsInputRedirected, "standard input is interactive in this test host; the prompt would block.");
        using var sandbox = new CliSandbox(nameof(Complete_WithoutYes_OnRedirectedStdin_RefusesWithTheYesHint_AndDeletesNothing));
        string template = KeepTemplate(sandbox);
        await SeedAsync(sandbox, "keep", GroupEntry("g1", Id('a', 1)));
        sandbox.UsePreflightOk(extraResponses: Get("/groups", "g1", Id('a', 1)));

        var result = await CliRunner.RunAsync(Args(sandbox, "deployment", "create", "-f", template, "--mode", "Complete"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("pass --yes to confirm", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("Deployment not started.", result.StandardError, StringComparison.Ordinal);
        Assert.Equal(0, Deletes(sandbox));
    }

    [Fact]
    public async Task Incremental_NeverNeedsTheDeleteFlags()
    {
        using var sandbox = new CliSandbox(nameof(Incremental_NeverNeedsTheDeleteFlags));
        string template = sandbox.WriteEmptyTemplate("inc.entra.json");
        await SeedAsync(sandbox, "inc", GroupEntry("g1", Id('a', 1)));
        sandbox.UsePreflightOk();

        var result = await CliRunner.RunAsync(Args(sandbox, "deployment", "create", "-f", template, "-y", "-o", "json"));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, result.Json["detached"]!.GetValue<int>());
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
    public async Task DeleteResources_KeepsAdoptedObjects_UnlessDeleteAdoptedIsPassed()
    {
        using var sandbox = new CliSandbox(nameof(DeleteResources_KeepsAdoptedObjects_UnlessDeleteAdoptedIsPassed));
        await SeedAsync(sandbox, "dep", AppEntry("alpha", Id('a', 1)), AppEntry("bravo", Id('a', 2), StateProvenance.Adopted));
        sandbox.UsePreflightOk(extraResponses: Get("/applications", "alpha", Id('a', 1)) + "," + Delete("/applications", Id('a', 1)));

        var first = await CliRunner.RunAsync(Args(sandbox, "deployment", "delete", "-n", "dep", "-y", "--delete-resources"));

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(1, Deletes(sandbox));
        Assert.Contains("(kept:", first.StandardOutput + first.StandardError, StringComparison.Ordinal);
        Assert.Contains("--delete-adopted", first.StandardOutput, StringComparison.Ordinal);
        var left = (await sandbox.Store.LoadAsync("dep", Ct))!;
        Assert.Equal(["/providers/Microsoft.Entra/applications/bravo"], left.Resources.Keys.ToList());

        sandbox.UsePreflightOk(extraResponses: Get("/applications", "bravo", Id('a', 2)) + "," + Delete("/applications", Id('a', 2)));
        var second = await CliRunner.RunAsync(Args(sandbox, "deployment", "delete", "-n", "dep", "-y", "--delete-resources", "--delete-adopted"));

        Assert.Equal(0, second.ExitCode);
        Assert.False(File.Exists(sandbox.StatePath("dep")));
    }

    [Fact]
    public async Task DeleteResources_NeverDeletesAnObjectTheStateEntryDoesNotDescribe()
    {
        using var sandbox = new CliSandbox(nameof(DeleteResources_NeverDeletesAnObjectTheStateEntryDoesNotDescribe));
        await SeedAsync(sandbox, "dep", AppEntry("alpha", Id('a', 1)));

        // The key "alpha" now belongs to another object than the one the state pins, and the pinned object is a different application altogether.
        sandbox.UsePreflightOk(extraResponses: Get("/applications", "alpha", Id('f', 9)) + "," + Get("/applications/" + Id('a', 1), "something-else", Id('a', 1)).Replace("(uniqueName='something-else')", string.Empty, StringComparison.Ordinal));
        var result = await CliRunner.RunAsync(Args(sandbox, "deployment", "delete", "-n", "dep", "-y", "--delete-resources"));

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(0, Deletes(sandbox));
        Assert.Contains(Id('f', 9), result.StandardError, StringComparison.Ordinal);
        Assert.True(File.Exists(sandbox.StatePath("dep")));
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
