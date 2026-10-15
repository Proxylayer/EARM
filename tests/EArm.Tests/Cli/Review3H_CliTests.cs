using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Engine;
using EArm.Engine.State;
using EArm.TestSupport;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// Review round 2 through the command line: a tampered state file (S2-01), recorded edge ids (S2-02), the delete estimate (S2-12), <c>set-provenance</c> (S2-16),
/// one JSON document from every verb (S2-20) and the options (S2-21).
/// </summary>
[Collection(CliCollection.Name)]
public sealed class Review3H_CliTests
{
    private const string Api = "2026-10-01-preview";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);

    private static (CliSandbox Sandbox, FakeDirectory Directory) Setup(string test)
    {
        var sandbox = new CliSandbox(test);
        var directory = new FakeDirectory();
        sandbox.Handler.Directory = directory;
        sandbox.Tokens.TenantId = CliSandbox.Tenant;
        return (sandbox, directory);
    }

    private static string StateJson(string deployment, string entries)
    {
        return "{ \"schemaVersion\": 1, \"deploymentName\": \"" + deployment + "\", \"tenantId\": \"" + CliSandbox.Tenant + "\", \"createdOn\": \"2026-10-01T12:00:00+00:00\", \"lastDeployedOn\": \"2026-10-01T12:00:00+00:00\", \"lastStatus\": \"Succeeded\", \"lastMode\": \"Incremental\", \"lastTemplateHash\": \"sha256:x\", \"resources\": {" + entries + "}, \"operations\": [] }";
    }

    private static string GroupEntry(string name, string keyJson, string id, string provenance = "\"provenance\": \"created\",", string extra = "")
    {
        return "\"/providers/Microsoft.Entra/groups/" + name + "\": { \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"" + Api + "\", \"key\": " + keyJson + ", \"id\": \"" + id + "\", " + provenance + " \"dependsOn\": [], \"lastWriteOn\": \"2026-10-01T12:00:00+00:00\"" + extra + " }";
    }

    private static string DeclaredGroupTemplate(CliSandbox sandbox)
    {
        return sandbox.WriteTemplate("t.entra.json", "\"resources\": [ { \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"" + Api + "\", \"name\": \"declared\", \"properties\": { \"displayName\": \"Declared\", \"mailEnabled\": false, \"securityEnabled\": true } } ]");
    }

    private static int Count(CliSandbox sandbox, string prefix)
    {
        return sandbox.RecordedCommands().Count(c => c.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static JsonObject OneDocument(CliResult result)
    {
        // Exactly one JSON document on stdout: JsonDocument.Parse refuses trailing content.
        using var document = JsonDocument.Parse(result.StandardOutput);
        return JsonNode.Parse(result.StandardOutput)!.AsObject();
    }

    // ---- S2-01 through the command line ----

    [Theory]
    [InlineData("{ \"securityEnabled\": \"true\" }")]
    [InlineData("{ \"displayName\": \"Victim Group\" }")]
    public async Task ATamperedStateEntry_IsRefusedAtLoad_ByEveryVerb_AndNothingIsDeleted(string key)
    {
        var (sandbox, directory) = Setup("tamper-" + key.Length);
        using var _ = sandbox;
        string victimId = directory.AddGroup("victim", "Victim Group")["id"]!.GetValue<string>();
        sandbox.WriteRawState("dep", StateJson("dep", GroupEntry("orphan", key, victimId)));
        string template = DeclaredGroupTemplate(sandbox);

        var create = await CliRunner.RunAsync("deployment", "create", "-f", template, "-n", "dep", "--mode", "Complete", "-y", "--state-dir", sandbox.StateDir, "-o", "json");
        var show = await CliRunner.RunAsync("deployment", "show", "-n", "dep", "--state-dir", sandbox.StateDir, "-o", "json");

        Assert.Equal(1, create.ExitCode);
        var doc = OneDocument(create);
        Assert.Equal("Failed", doc["status"]!.GetValue<string>());
        Assert.Equal("StateEntryInvalid", doc["diagnostics"]![0]!["code"]!.GetValue<string>());
        Assert.Contains("/providers/Microsoft.Entra/groups/orphan", doc["diagnostics"]![0]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(1, show.ExitCode);
        Assert.Equal("StateEntryInvalid", OneDocument(show)["diagnostics"]![0]!["code"]!.GetValue<string>());
        Assert.Equal(0, Count(sandbox, "DELETE"));
        Assert.NotNull(directory.FindById(victimId));
    }

    [Fact]
    public async Task StateForget_RepairsATamperedEntry_AndTheNextRunProceedsWithoutTouchingTheObject()
    {
        var (sandbox, directory) = Setup(nameof(StateForget_RepairsATamperedEntry_AndTheNextRunProceedsWithoutTouchingTheObject));
        using var _ = sandbox;
        string victimId = directory.AddGroup("victim", "Victim Group")["id"]!.GetValue<string>();
        sandbox.WriteRawState("dep", StateJson("dep", GroupEntry("orphan", "{ \"securityEnabled\": \"true\" }", victimId)));
        string template = DeclaredGroupTemplate(sandbox);

        var forget = await CliRunner.RunAsync("state", "forget", "-n", "dep", "--resource-id", "/providers/Microsoft.Entra/groups/orphan", "-y", "--state-dir", sandbox.StateDir, "-o", "json");
        var create = await CliRunner.RunAsync("deployment", "create", "-f", template, "-n", "dep", "--mode", "Complete", "-y", "--state-dir", sandbox.StateDir, "-o", "json");

        Assert.Equal(0, forget.ExitCode);
        Assert.Equal("Succeeded", OneDocument(forget)["status"]!.GetValue<string>());
        Assert.Equal(0, create.ExitCode);
        Assert.Equal(0, Count(sandbox, "DELETE"));
        Assert.NotNull(directory.FindById(victimId));
    }

    // ---- S2-02: a recorded assignment id is verified before it is removed ----

    [Fact]
    public async Task ARecordedDirectoryRoleAssignmentOfAnotherPrincipal_IsNeverRemoved_OneOfTheOrphanIs()
    {
        var (sandbox, directory) = Setup(nameof(ARecordedDirectoryRoleAssignmentOfAnotherPrincipal_IsNeverRemoved_OneOfTheOrphanIs));
        using var _ = sandbox;
        string orphanId = directory.AddGroup("orphan", "Orphan group")["id"]!.GetValue<string>();
        string otherPrincipal = directory.AddUser("someone@contoso.com", "Someone")["id"]!.GetValue<string>();
        const string RoleId = "62e90394-69f5-4237-9190-012177145e10";
        string victimAssignment = directory.Add("/roleManagement/directory/roleAssignments", new JsonObject { ["roleDefinitionId"] = RoleId, ["principalId"] = otherPrincipal, ["directoryScopeId"] = "/" })["id"]!.GetValue<string>();
        string ownAssignment = directory.Add("/roleManagement/directory/roleAssignments", new JsonObject { ["roleDefinitionId"] = "fe930be7-5e62-47db-91af-98c3a49a38b1", ["principalId"] = orphanId, ["directoryScopeId"] = "/" })["id"]!.GetValue<string>();
        string relationships = ", \"relationships\": { \"directoryRoles\": [ "
            + "{ \"roleDefinitionId\": \"" + RoleId + "\", \"principalId\": \"" + orphanId + "\", \"directoryScopeId\": \"/\", \"id\": \"" + victimAssignment + "\" }, "
            + "{ \"roleDefinitionId\": \"fe930be7-5e62-47db-91af-98c3a49a38b1\", \"principalId\": \"" + orphanId + "\", \"directoryScopeId\": \"/\", \"id\": \"" + ownAssignment + "\" } ] }";
        sandbox.WriteRawState("dep", StateJson("dep", GroupEntry("orphan", "{ \"uniqueName\": \"orphan\" }", orphanId, extra: relationships)));
        string template = DeclaredGroupTemplate(sandbox);

        var result = await CliRunner.RunAsync("deployment", "create", "-f", template, "-n", "dep", "--mode", "Complete", "-y", "--state-dir", sandbox.StateDir, "-o", "json");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(sandbox.RecordedCommands(), c => c.StartsWith("GET /v1.0/roleManagement/directory/roleAssignments/" + victimAssignment, StringComparison.Ordinal));
        Assert.DoesNotContain(sandbox.RecordedCommands(), c => c.StartsWith("DELETE", StringComparison.Ordinal) && c.Contains(victimAssignment, StringComparison.Ordinal));
        Assert.NotNull(directory.FindById(victimAssignment));
        Assert.Contains(sandbox.RecordedCommands(), c => c.StartsWith("DELETE", StringComparison.Ordinal) && c.Contains(ownAssignment, StringComparison.Ordinal));
        Assert.Equal(1, directory.Count("/roleManagement/directory/roleAssignments"));
        Assert.Null(directory.Find("/groups", "uniqueName", "orphan"));
    }

    // ---- S2-12: the delete estimate counts entries of types it cannot judge yet ----

    private static StateFile SpOrphans(int count)
    {
        var state = StateFile.CreateNew("dep", CliSandbox.Tenant, Start);
        for (int i = 0; i < count; i++)
        {
            string appId = new Guid(0xa0 + i, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1]).ToString("D");
            state.Upsert(new StateEntry
            {
                Id = StateSerializer.ParseResourceId("/providers/Microsoft.Entra/servicePrincipals/" + appId, "Microsoft.Entra/servicePrincipals")!,
                Type = "Microsoft.Entra/servicePrincipals",
                ApiVersion = Api,
                Key = new Dictionary<string, string> { ["appId"] = appId },
                GraphId = new Guid(0xb0 + i, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1]).ToString("D"),
                Provenance = StateProvenance.Created,
                LastWriteOn = Start,
            });
        }

        state.CompleteRun(true, "Incremental", "sha256:previous", Start);
        return state;
    }

    private static string SymbolicSpTemplate(CliSandbox sandbox)
    {
        return sandbox.WriteTemplate("t.entra.json", "\"resources\": [ "
            + "{ \"type\": \"Microsoft.Entra/applications\", \"apiVersion\": \"" + Api + "\", \"name\": \"app1\", \"properties\": { \"displayName\": \"App 1\" } }, "
            + "{ \"type\": \"Microsoft.Entra/servicePrincipals\", \"apiVersion\": \"" + Api + "\", \"name\": \"[reference(resourceId('Microsoft.Entra/applications', 'app1')).appId]\", \"properties\": { } } ]");
    }

    [Fact]
    public async Task EntriesOfATypeWithADeployTimeName_AreCountedAsPossibleDeletions_SoMaxDeletesCannotBeExceededByTheModePass()
    {
        var (sandbox, _) = Setup(nameof(EntriesOfATypeWithADeployTimeName_AreCountedAsPossibleDeletions_SoMaxDeletesCannotBeExceededByTheModePass));
        using var _ = sandbox;
        await sandbox.WriteStateAsync(SpOrphans(3));
        string template = SymbolicSpTemplate(sandbox);

        var result = await CliRunner.RunAsync("deployment", "create", "-f", template, "-n", "dep", "--mode", "Complete", "-y", "--max-deletes", "1", "--state-dir", sandbox.StateDir);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("may include 3", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("--max-deletes 1", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("only known at deployment time", result.StandardError, StringComparison.Ordinal);
        Assert.Equal(0, Count(sandbox, "POST"));
        Assert.Equal(0, Count(sandbox, "DELETE"));
    }

    [Fact]
    public async Task TheRefusal_WithJsonOutput_IsOneDocument_AndAllowDeleteAllLiftsIt()
    {
        var (sandbox, _) = Setup(nameof(TheRefusal_WithJsonOutput_IsOneDocument_AndAllowDeleteAllLiftsIt));
        using var disposeSandbox = sandbox;
        await sandbox.WriteStateAsync(SpOrphans(3));
        string template = SymbolicSpTemplate(sandbox);

        var refused = await CliRunner.RunAsync("deployment", "create", "-f", template, "-n", "dep", "--mode", "Complete", "-y", "--max-deletes", "1", "--state-dir", sandbox.StateDir, "-o", "json");
        var allowed = await CliRunner.RunAsync("deployment", "create", "-f", template, "-n", "dep", "--mode", "Complete", "-y", "--max-deletes", "1", "--allow-delete-all", "--state-dir", sandbox.StateDir, "-o", "json");

        Assert.Equal(1, refused.ExitCode);
        Assert.Equal("DeleteLimitExceeded", OneDocument(refused)["diagnostics"]![0]!["code"]!.GetValue<string>());
        Assert.DoesNotContain("Refusing the Complete-mode deployment", allowed.StandardOutput + allowed.StandardError, StringComparison.Ordinal);
    }

    // ---- S2-16: set-provenance ----

    [Fact]
    public async Task ALegacyEntry_IsExplainedAsSuch_AndSetProvenanceMakesItDeletable()
    {
        var (sandbox, directory) = Setup(nameof(ALegacyEntry_IsExplainedAsSuch_AndSetProvenanceMakesItDeletable));
        using var _ = sandbox;
        string id = directory.AddGroup("legacy", "Legacy")["id"]!.GetValue<string>();
        sandbox.WriteRawState("dep", StateJson("dep", GroupEntry("legacy", "{ \"uniqueName\": \"legacy\" }", id, provenance: string.Empty)));
        string template = DeclaredGroupTemplate(sandbox);

        var kept = await CliRunner.RunAsync("deployment", "create", "-f", template, "-n", "dep", "--mode", "Complete", "-y", "--state-dir", sandbox.StateDir, "-o", "json");
        Assert.Equal(0, kept.ExitCode);
        Assert.Contains("no provenance record", kept.StandardOutput, StringComparison.Ordinal);
        Assert.NotNull(directory.FindById(id));

        var mark = await CliRunner.RunAsync("state", "set-provenance", "-n", "dep", "--resource-id", "/providers/Microsoft.Entra/groups/legacy", "--provenance", "created", "-y", "--state-dir", sandbox.StateDir, "-o", "json");
        Assert.Equal(0, mark.ExitCode);
        var doc = OneDocument(mark);
        Assert.Equal("Succeeded", doc["status"]!.GetValue<string>());
        Assert.Equal("created", doc["provenance"]!.GetValue<string>());
        Assert.True(doc["changed"]!.GetValue<bool>());

        var deleted = await CliRunner.RunAsync("deployment", "create", "-f", template, "-n", "dep", "--mode", "Complete", "-y", "--state-dir", sandbox.StateDir, "-o", "json");
        Assert.Equal(0, deleted.ExitCode);
        Assert.Null(directory.Find("/groups", "uniqueName", "legacy"));
    }

    [Fact]
    public async Task SetProvenance_RefusesAnUnknownId_AndAnUnknownValue_AndNeedsConfirmation()
    {
        var (sandbox, directory) = Setup(nameof(SetProvenance_RefusesAnUnknownId_AndAnUnknownValue_AndNeedsConfirmation));
        using var _ = sandbox;
        string id = directory.AddGroup("g", "G")["id"]!.GetValue<string>();
        sandbox.WriteRawState("dep", StateJson("dep", GroupEntry("g", "{ \"uniqueName\": \"g\" }", id, provenance: "\"provenance\": \"adopted\",")));
        string stateBefore = await File.ReadAllTextAsync(sandbox.StatePath("dep"), Ct);

        var unknown = await CliRunner.RunAsync("state", "set-provenance", "-n", "dep", "--resource-id", "/providers/Microsoft.Entra/groups/nope", "--provenance", "created", "-y", "--state-dir", sandbox.StateDir, "-o", "json");
        var badValue = await CliRunner.RunAsync("state", "set-provenance", "-n", "dep", "--resource-id", "/providers/Microsoft.Entra/groups/g", "--provenance", "maybe", "-y", "--state-dir", sandbox.StateDir);
        var unconfirmed = await CliRunner.RunAsync("state", "set-provenance", "-n", "dep", "--resource-id", "/providers/Microsoft.Entra/groups/g", "--provenance", "created", "--state-dir", sandbox.StateDir, "-o", "json");
        var same = await CliRunner.RunAsync("state", "set-provenance", "-n", "dep", "--resource-id", "/providers/Microsoft.Entra/groups/g", "--provenance", "adopted", "-y", "--state-dir", sandbox.StateDir, "-o", "json");

        Assert.Equal(1, unknown.ExitCode);
        Assert.Equal("ResourceNotInState", OneDocument(unknown)["diagnostics"]![0]!["code"]!.GetValue<string>());
        Assert.NotEqual(0, badValue.ExitCode);
        Assert.NotEqual(0, unconfirmed.ExitCode);
        Assert.Equal(0, same.ExitCode);
        Assert.False(OneDocument(same)["changed"]!.GetValue<bool>());
        Assert.Contains("\"provenance\": \"adopted\"", await File.ReadAllTextAsync(sandbox.StatePath("dep"), Ct), StringComparison.Ordinal);
        Assert.NotEmpty(stateBefore);
    }

    // ---- S2-20: one JSON document from every verb, on success and on failure ----

    [Fact]
    public async Task StateUnlock_Json_IsOneDocument_WhetherOrNotTheDeploymentWasLocked()
    {
        var (sandbox, _) = Setup(nameof(StateUnlock_Json_IsOneDocument_WhetherOrNotTheDeploymentWasLocked));
        using var _ = sandbox;

        var notLocked = await CliRunner.RunAsync("state", "unlock", "-n", "dep", "--state-dir", sandbox.StateDir, "-o", "json");
        sandbox.WriteLock("dep", DateTimeOffset.UtcNow);
        var locked = await CliRunner.RunAsync("state", "unlock", "-n", "dep", "--state-dir", sandbox.StateDir, "-o", "json");

        Assert.Equal(0, notLocked.ExitCode);
        Assert.False(OneDocument(notLocked)["unlocked"]!.GetValue<bool>());
        Assert.Equal(0, locked.ExitCode);
        var doc = OneDocument(locked);
        Assert.Equal("Succeeded", doc["status"]!.GetValue<string>());
        Assert.True(doc["unlocked"]!.GetValue<bool>());
        Assert.False(File.Exists(sandbox.LockPath("dep")));
    }

    [Fact]
    public async Task StateForget_Json_Success_UnknownDeployment_UnknownResource_AreEachOneDocument()
    {
        var (sandbox, directory) = Setup(nameof(StateForget_Json_Success_UnknownDeployment_UnknownResource_AreEachOneDocument));
        using var _ = sandbox;
        string id = directory.AddGroup("g", "G")["id"]!.GetValue<string>();
        sandbox.WriteRawState("dep", StateJson("dep", GroupEntry("g", "{ \"uniqueName\": \"g\" }", id)));

        var noDeployment = await CliRunner.RunAsync("state", "forget", "-n", "ghost", "--resource-id", "/providers/Microsoft.Entra/groups/g", "-y", "--state-dir", sandbox.StateDir, "-o", "json");
        var noResource = await CliRunner.RunAsync("state", "forget", "-n", "dep", "--resource-id", "/providers/Microsoft.Entra/groups/zzz", "-y", "--state-dir", sandbox.StateDir, "-o", "json");
        var done = await CliRunner.RunAsync("state", "forget", "-n", "dep", "--resource-id", "/providers/Microsoft.Entra/groups/g", "-y", "--state-dir", sandbox.StateDir, "-o", "json");

        Assert.Equal("DeploymentNotFound", OneDocument(noDeployment)["diagnostics"]![0]!["code"]!.GetValue<string>());
        Assert.Equal("ResourceNotInState", OneDocument(noResource)["diagnostics"]![0]!["code"]!.GetValue<string>());
        var doc = OneDocument(done);
        Assert.Equal("Succeeded", doc["status"]!.GetValue<string>());
        Assert.Equal(0, doc["remaining"]!.GetValue<int>());
        Assert.All(new[] { noDeployment, noResource }, r => Assert.Equal(1, r.ExitCode));
    }

    [Fact]
    public async Task StateAdopt_Json_AFailureIsOneDocument_AndASuccessCarriesAStatus()
    {
        var (sandbox, _) = Setup(nameof(StateAdopt_Json_AFailureIsOneDocument_AndASuccessCarriesAStatus));
        using var _ = sandbox;

        var bad = await CliRunner.RunAsync("state", "adopt", "-n", "dep", "--resource-id", "/providers/Microsoft.Entra/applications/orders", "--object-id", "not-a-guid", "--state-dir", sandbox.StateDir, "-o", "json");
        var good = await CliRunner.RunAsync("state", "adopt", "-n", "dep", "--resource-id", "/providers/Microsoft.Entra/applications/orders", "--object-id", "6f1b0000-0000-4000-8000-0000000000aa", "--state-dir", sandbox.StateDir, "-o", "json");
        var again = await CliRunner.RunAsync("state", "adopt", "-n", "dep", "--resource-id", "/providers/Microsoft.Entra/applications/orders", "--object-id", "6f1b0000-0000-4000-8000-0000000000aa", "--state-dir", sandbox.StateDir, "-o", "json");
        var neither = await CliRunner.RunAsync("state", "adopt", "--state-dir", sandbox.StateDir, "-o", "json");

        Assert.Equal(1, bad.ExitCode);
        Assert.Equal("Failed", OneDocument(bad)["status"]!.GetValue<string>());
        Assert.Equal("Succeeded", OneDocument(good)["status"]!.GetValue<string>());
        Assert.False(OneDocument(again)["changed"]!.GetValue<bool>());
        Assert.Equal(1, neither.ExitCode);
        Assert.Equal("Failed", OneDocument(neither)["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task DeploymentDelete_Json_IsOneDocument_ForTheStateOnlyDelete_ThePerObjectDelete_AndAnUnknownName()
    {
        var (sandbox, directory) = Setup(nameof(DeploymentDelete_Json_IsOneDocument_ForTheStateOnlyDelete_ThePerObjectDelete_AndAnUnknownName));
        using var _ = sandbox;
        string id = directory.AddGroup("g", "G")["id"]!.GetValue<string>();
        sandbox.WriteRawState("dep", StateJson("dep", GroupEntry("g", "{ \"uniqueName\": \"g\" }", id)));
        sandbox.WriteRawState("dep2", StateJson("dep2", GroupEntry("g", "{ \"uniqueName\": \"g\" }", id)));

        var unknown = await CliRunner.RunAsync("deployment", "delete", "-n", "ghost", "-y", "--state-dir", sandbox.StateDir, "-o", "json");
        var stateOnly = await CliRunner.RunAsync("deployment", "delete", "-n", "dep", "-y", "--state-dir", sandbox.StateDir, "-o", "json");
        var objects = await CliRunner.RunAsync("deployment", "delete", "-n", "dep2", "--delete-resources", "-y", "--state-dir", sandbox.StateDir, "-o", "json");

        Assert.Equal("DeploymentNotFound", OneDocument(unknown)["diagnostics"]![0]!["code"]!.GetValue<string>());
        var stateOnlyDoc = OneDocument(stateOnly);
        Assert.Equal("Succeeded", stateOnlyDoc["status"]!.GetValue<string>());
        Assert.True(stateOnlyDoc["stateRemoved"]!.GetValue<bool>());
        Assert.Equal(0, objects.ExitCode);
        var doc = OneDocument(objects);
        Assert.Equal("Succeeded", doc["status"]!.GetValue<string>());
        Assert.Equal(1, doc["deleted"]!.GetValue<int>());
        Assert.Equal("Deleted", doc["outcomes"]![0]!["status"]!.GetValue<string>());
        Assert.Equal(0, directory.Count("/groups"));
    }

    [Fact]
    public async Task Show_List_AndOperationList_Json_UnknownNames_AreOneDocument()
    {
        var (sandbox, directory) = Setup(nameof(Show_List_AndOperationList_Json_UnknownNames_AreOneDocument));
        using var _ = sandbox;
        sandbox.WriteRawState("dep", StateJson("dep", GroupEntry("g", "{ \"uniqueName\": \"g\" }", directory.AddGroup("g", "G")["id"]!.GetValue<string>())));

        var show = await CliRunner.RunAsync("deployment", "show", "-n", "ghost", "--state-dir", sandbox.StateDir, "-o", "json");
        var operations = await CliRunner.RunAsync("deployment", "operation", "list", "-n", "ghost", "--state-dir", sandbox.StateDir, "-o", "json");
        var list = await CliRunner.RunAsync("deployment", "list", "-n", "ghost", "--state-dir", sandbox.StateDir, "-o", "json");
        var listAll = await CliRunner.RunAsync("deployment", "list", "--state-dir", sandbox.StateDir, "-o", "json");

        Assert.Equal(1, show.ExitCode);
        Assert.Equal("Failed", OneDocument(show)["status"]!.GetValue<string>());
        Assert.Equal(1, operations.ExitCode);
        Assert.Equal("Failed", OneDocument(operations)["status"]!.GetValue<string>());
        Assert.Equal(0, list.ExitCode);
        Assert.Empty(OneDocument(list)["deployments"]!.AsArray());
        Assert.Single(OneDocument(listAll)["deployments"]!.AsArray());
    }

    [Theory]
    [InlineData("what-if")]
    [InlineData("create")]
    public async Task APreflightFailure_Json_IsOneFailedDocument_WithoutLiteralCarriageReturns(string verb)
    {
        using var sandbox = new CliSandbox("preflight-" + verb);
        sandbox.UseNotLoggedIn();
        string template = sandbox.WriteEmptyTemplate();

        string[] extra = verb == "create" ? ["-y"] : [];
        var result = await CliRunner.RunAsync(["deployment", verb, "-f", template, .. extra, "--state-dir", sandbox.StateDir, "-o", "json"]);

        Assert.Equal(3, result.ExitCode);
        var doc = OneDocument(result);
        Assert.Equal("Failed", doc["status"]!.GetValue<string>());
        string message = doc["diagnostics"]![0]!["message"]!.GetValue<string>();
        Assert.StartsWith("Preflight failed", message, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', message);
        Assert.DoesNotContain("\\r", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void TheVerbOutputNormalisesLineBreaks_SoAMultiLineMessageReadsAsLines()
    {
        Assert.Equal("a\nb\nc", EArm.Cli.Commands.VerbOutput.Normalize("a\r\nb\rc"));
    }

    // ---- S2-21: options ----

    [Theory]
    [InlineData("--parallelism", "-1")]
    [InlineData("--parallelism", "0")]
    [InlineData("--max-deletes", "-1")]
    [InlineData("--lock-timeout", "-1")]
    public async Task ANegativeBound_IsAUsageError_NothingRuns(string option, string value)
    {
        using var sandbox = new CliSandbox("bound" + option.Length + value);
        sandbox.UsePreflightOk();
        string template = sandbox.WriteEmptyTemplate();

        var result = await CliRunner.RunAsync("deployment", "create", "-f", template, "-y", option, value, "--state-dir", sandbox.StateDir);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(option, result.StandardError + result.StandardOutput, StringComparison.Ordinal);
        Assert.Empty(sandbox.RecordedCommands());
        Assert.False(File.Exists(sandbox.StatePath("empty")));
    }

    [Fact]
    public async Task ZeroIsStillAValidMaxDeletesAndLockTimeout()
    {
        using var sandbox = new CliSandbox(nameof(ZeroIsStillAValidMaxDeletesAndLockTimeout));
        sandbox.UsePreflightOk();
        string template = sandbox.WriteEmptyTemplate();

        var result = await CliRunner.RunAsync("deployment", "create", "-f", template, "-y", "--max-deletes", "0", "--lock-timeout", "0", "--state-dir", sandbox.StateDir, "-o", "json");

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task StateAdopt_UniqueName_IsTheDocumentedOption_AndTheOldNameStillWorksWithAHint()
    {
        var (sandbox, directory) = Setup(nameof(StateAdopt_UniqueName_IsTheDocumentedOption_AndTheOldNameStillWorksWithAHint));
        using var _ = sandbox;
        string first = directory.AddGroup("x1", "G1", g => g.Remove("uniqueName"))["id"]!.GetValue<string>();
        string second = directory.AddGroup("x2", "G2", g => g.Remove("uniqueName"))["id"]!.GetValue<string>();

        var viaNew = await CliRunner.RunAsync("state", "adopt", "--type", "Microsoft.Entra/groups", "--id", first, "--unique-name", "orders-admins", "-y", "--state-dir", sandbox.StateDir);
        var viaOld = await CliRunner.RunAsync("state", "adopt", "--type", "Microsoft.Entra/groups", "--id", second, "--name", "orders-ops", "-y", "--state-dir", sandbox.StateDir);
        var help = await CliRunner.RunAsync("state", "adopt", "--help");

        Assert.Equal(0, viaNew.ExitCode);
        Assert.DoesNotContain("deprecated", viaNew.StandardError, StringComparison.Ordinal);
        Assert.Equal(0, viaOld.ExitCode);
        Assert.Contains("--unique-name", viaOld.StandardError, StringComparison.Ordinal);
        Assert.Contains("deprecated", viaOld.StandardError, StringComparison.Ordinal);
        Assert.Equal("orders-admins", directory.FindById(first)!["uniqueName"]!.GetValue<string>());
        Assert.Equal("orders-ops", directory.FindById(second)!["uniqueName"]!.GetValue<string>());
        Assert.Contains("--unique-name", help.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("--name ", help.StandardOutput, StringComparison.Ordinal);
    }

    // ---- S2-07 through the real HTTP stack ----

    [Fact]
    public async Task AUserCreate_ThatAnswers503_IsSentExactlyOnce_AndGoesThroughTheKeyReRead()
    {
        using var sandbox = new CliSandbox(nameof(AUserCreate_ThatAnswers503_IsSentExactlyOnce_AndGoesThroughTheKeyReRead));
        string missing = "{ \"match\": { \"method\": \"GET\", \"urlContains\": \"/users/\" }, \"sticky\": true, \"status\": 404, \"body\": { \"error\": { \"code\": \"Request_ResourceNotFound\", \"message\": \"Resource does not exist.\" } } }";
        string deleted = "{ \"match\": { \"method\": \"GET\", \"urlContains\": \"/directory/deletedItems\" }, \"sticky\": true, \"status\": 200, \"body\": { \"value\": [] } }";
        string first = "{ \"match\": { \"method\": \"POST\", \"urlContains\": \"/users\" }, \"status\": 503, \"body\": { \"error\": { \"code\": \"ServiceUnavailable\", \"message\": \"try later\" } } }";
        string second = "{ \"match\": { \"method\": \"POST\", \"urlContains\": \"/users\" }, \"status\": 201, \"body\": { \"id\": \"11111111-aaaa-4aaa-8aaa-111111111111\", \"displayName\": \"U\", \"userPrincipalName\": \"u1@contoso.com\" } }";
        sandbox.UsePreflightOk(extraResponses: first + "," + second + "," + deleted + "," + missing);
        string template = sandbox.WriteTemplate("t.entra.json", "\"resources\": [ { \"type\": \"Microsoft.Entra/users\", \"apiVersion\": \"" + Api + "\", \"name\": \"u1@contoso.com\", \"properties\": { \"displayName\": \"U\", \"accountEnabled\": true, \"passwordProfile\": { \"password\": \"Initial-P@ssw0rd-1\" } } } ]");

        var result = await CliRunner.RunAsync("deployment", "create", "-f", template, "-n", "dep", "-y", "--state-dir", sandbox.StateDir, "-o", "json");

        Assert.Equal(1, result.ExitCode);
        var commands = sandbox.RecordedCommands();
        Assert.Equal(1, commands.Count(c => c.StartsWith("POST /v1.0/users", StringComparison.Ordinal)));
        int post = commands.ToList().FindIndex(c => c.StartsWith("POST /v1.0/users", StringComparison.Ordinal));
        Assert.Contains(commands.Skip(post + 1), c => c.StartsWith("GET /v1.0/users/u1@contoso.com", StringComparison.Ordinal));
    }
}