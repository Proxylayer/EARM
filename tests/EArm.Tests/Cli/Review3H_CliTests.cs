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

    // ---- S2-16: set-provenance ----

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
    public async Task DeploymentDelete_Json_IsOneDocument_ForADelete_AndForNothingToDelete()
    {
        var (sandbox, directory) = Setup(nameof(DeploymentDelete_Json_IsOneDocument_ForADelete_AndForNothingToDelete));
        using var _ = sandbox;
        directory.AddGroup("declared", "Declared");
        string template = DeclaredGroupTemplate(sandbox);

        var objects = await CliRunner.RunAsync("deployment", "delete", "-f", template, "-y", "-o", "json");
        var nothing = await CliRunner.RunAsync("deployment", "delete", "-f", template, "-y", "-o", "json");

        Assert.Equal(0, objects.ExitCode);
        var doc = OneDocument(objects);
        Assert.Equal("Succeeded", doc["status"]!.GetValue<string>());
        Assert.Equal(1, doc["deleted"]!.GetValue<int>());
        Assert.Equal("Deleted", doc["outcomes"]![0]!["status"]!.GetValue<string>());
        Assert.Equal(0, directory.Count("/groups"));
        Assert.Equal(0, nothing.ExitCode);
        var none = OneDocument(nothing);
        Assert.Equal("Succeeded", none["status"]!.GetValue<string>());
        Assert.Contains("Nothing to delete", none["message"]!.GetValue<string>(), StringComparison.Ordinal);
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
    public async Task ZeroIsStillAValidLockTimeout()
    {
        using var sandbox = new CliSandbox(nameof(ZeroIsStillAValidLockTimeout));
        sandbox.UsePreflightOk();
        string template = sandbox.WriteEmptyTemplate();

        var result = await CliRunner.RunAsync("deployment", "create", "-f", template, "-y", "--lock-timeout", "0", "--state-dir", sandbox.StateDir, "-o", "json");

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