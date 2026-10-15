using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Cli;
using EArm.Cli.Commands;
using EArm.Core.Types;
using EArm.Engine;
using EArm.Engine.State;
using EArm.Providers;
using EArm.Tests.Engine;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// <c>earm deployment what-if / create</c> driven in-process against a fake Microsoft Graph over HTTP and a fake sign-in (the exit
/// codes of the CLI). Templates without resources go through the whole pipeline (preflight, state, lock, plan, execute) without
/// needing a resource provider; the engine fixture covers the offline paths that stop before any request.
/// </summary>
[Collection(CliCollection.Name)]
public sealed class DeploymentCommandTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string[] Args(CliSandbox sandbox, params string[] args)
    {
        return [.. args, .. sandbox.StateArgs];
    }

    private static StateFile OrphanState(string name, string tenant = CliSandbox.Tenant)
    {
        var state = StateFile.CreateNew(name, tenant, Start);
        state.Upsert(new StateEntry
        {
            Id = EArm.Core.ResourceIdentifier.Create(Fixture.GroupType, "old-group"),
            Type = Fixture.GroupType,
            ApiVersion = "2026-10-01-preview",
            Key = new System.Collections.Generic.Dictionary<string, string> { ["uniqueName"] = "old-group" },
            GraphId = "c0de0000-0000-4000-8000-0000000000d1",
            LastWriteOn = Start,
        });
        state.CompleteRun(true, "Incremental", "sha256:previous", Start);
        return state;
    }

    /// <summary>Generic answers for a groups provider, should one be registered: the pinned read and the delete of the orphan.</summary>
    private const string GroupResponses =
        "{ \"match\": { \"method\": \"GET\", \"urlContains\": \"/groups\" }, \"status\": 200, \"body\": { \"id\": \"c0de0000-0000-4000-8000-0000000000d1\", \"uniqueName\": \"old-group\", \"displayName\": \"Old\", \"mailEnabled\": false, \"securityEnabled\": true } }," +
        "{ \"match\": { \"method\": \"GET\", \"urlContains\": \"/groups\" }, \"status\": 200, \"body\": { \"id\": \"c0de0000-0000-4000-8000-0000000000d1\", \"uniqueName\": \"old-group\", \"displayName\": \"Old\", \"mailEnabled\": false, \"securityEnabled\": true } }," +
        "{ \"match\": { \"method\": \"DELETE\", \"urlContains\": \"/groups\" }, \"status\": 204 }";

    // ---- offline failures (never send a request) ----

    [Fact]
    public async Task WhatIf_WithoutParameterValues_FailsValidationBeforeAnyRequest()
    {
        using var sandbox = new CliSandbox(nameof(WhatIf_WithoutParameterValues_FailsValidationBeforeAnyRequest));

        var result = await CliRunner.RunAsync("deployment", "what-if", "-f", Templates.FixturePath, "-o", "json", "--state-dir", sandbox.StateDir);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("Failed", result.Json["status"]!.GetValue<string>());
        var missing = result.Diagnostics.Where(d => d!["code"]!.GetValue<string>() == "MissingParameterValue").ToList();
        Assert.Equal(2, missing.Count);
        Assert.All(missing, d => Assert.Equal("Error", d!["severity"]!.GetValue<string>()));
        Assert.Contains(missing, d => d!["message"]!.GetValue<string>().Contains("Supply it with -p name=value", StringComparison.Ordinal));
        Assert.Empty(sandbox.RecordedCommands());
        Assert.False(Directory.Exists(sandbox.StateDir));
    }

    [Fact]
    public async Task WhatIf_WithAMalformedParameterToken_ExitsOneBeforeAnyRequest()
    {
        using var sandbox = new CliSandbox(nameof(WhatIf_WithAMalformedParameterToken_ExitsOneBeforeAnyRequest));

        var result = await CliRunner.RunAsync("deployment", "what-if", "-f", Templates.FixturePath, "-p", "notatoken", "-o", "json", "--state-dir", sandbox.StateDir);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(result.Diagnostics, d => d!["code"]!.GetValue<string>() == "InvalidArgument");
        Assert.Empty(sandbox.RecordedCommands());
    }

    [Theory]
    [InlineData("bad name")]
    [InlineData("-leading-dash")]
    [InlineData("a/b")]
    public async Task InvalidDeploymentName_ExitsOneBeforeAnyRequest(string name)
    {
        using var sandbox = new CliSandbox(nameof(InvalidDeploymentName_ExitsOneBeforeAnyRequest));
        string template = sandbox.WriteEmptyTemplate();

        var result = await CliRunner.RunAsync("deployment", "what-if", "-f", template, "-n", name, "-o", "json", "--state-dir", sandbox.StateDir);

        Assert.Equal(1, result.ExitCode);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("InvalidArgument", diagnostic!["code"]!.GetValue<string>());
        Assert.Contains("is not valid", diagnostic["message"]!.GetValue<string>());
        Assert.Empty(sandbox.RecordedCommands());
    }

    [Fact]
    public async Task UnsupportedStateSchema_ExitsOneBeforeAnyRequest()
    {
        using var sandbox = new CliSandbox(nameof(UnsupportedStateSchema_ExitsOneBeforeAnyRequest));
        string template = sandbox.WriteEmptyTemplate();
        sandbox.WriteRawState("empty", "{ \"schemaVersion\": 2, \"deploymentName\": \"empty\", \"resources\": {} }");

        var whatIf = await CliRunner.RunAsync("deployment", "what-if", "-f", template, "--state-dir", sandbox.StateDir);
        var create = await CliRunner.RunAsync("deployment", "create", "-f", template, "-y", "--state-dir", sandbox.StateDir);

        Assert.Equal(1, whatIf.ExitCode);
        Assert.Contains("schemaVersion 2", whatIf.StandardError);
        Assert.Contains("Upgrade earm", whatIf.StandardError);
        Assert.Equal(1, create.ExitCode);
        Assert.Empty(sandbox.RecordedCommands());
    }

    // ---- preflight ----

    [Fact]
    public async Task WhatIf_NotSignedIn_ExitsThreeWithTheSignInGuidance()
    {
        using var sandbox = new CliSandbox(nameof(WhatIf_NotSignedIn_ExitsThreeWithTheSignInGuidance));
        sandbox.UseNotLoggedIn();

        var result = await CliRunner.RunAsync(Args(sandbox, "deployment", "what-if", "-f", Templates.FixturePath, "-p", "env=dev", "-p", "initialPassword=P@ss-w0rd1"));

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("Preflight failed: Not signed in", result.StandardError);
        Assert.Contains("earm auth login", result.StandardError);
        Assert.Contains("AZURE_CLIENT_SECRET", result.StandardError);
        Assert.DoesNotContain("az login", result.StandardError);
        Assert.Empty(sandbox.RecordedCommands());
        Assert.False(File.Exists(sandbox.LockPath("orders-api.engine")));
    }
    [Fact]
    public async Task TenantMismatch_ExitsThree()
    {
        using var sandbox = new CliSandbox(nameof(TenantMismatch_ExitsThree));
        sandbox.UsePreflightOk();
        string template = sandbox.WriteEmptyTemplate();
        await sandbox.WriteStateAsync(OrphanState("empty", CliSandbox.OtherTenant));

        var result = await CliRunner.RunAsync(Args(sandbox, "deployment", "what-if", "-f", template));

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("was deployed to tenant " + CliSandbox.OtherTenant, result.StandardError);
        Assert.Contains("earm auth login --tenant " + CliSandbox.OtherTenant, result.StandardError);
        Assert.Equal(["GET /v1.0/organization?$select=id,displayName,verifiedDomains"], sandbox.RecordedCommands());
    }

    // ---- what-if ----

    [Fact]
    public async Task WhatIf_EmptyTemplate_ReportsNoChanges_AndNeverWrites()
    {
        using var sandbox = new CliSandbox(nameof(WhatIf_EmptyTemplate_ReportsNoChanges_AndNeverWrites));
        sandbox.UsePreflightOk();
        string template = sandbox.WriteEmptyTemplate();

        var json = await CliRunner.RunAsync(Args(sandbox, "deployment", "what-if", "-f", template, "-o", "json", "--exit-code-on-changes"));
        sandbox.UsePreflightOk();
        var table = await CliRunner.RunAsync(Args(sandbox, "deployment", "what-if", "-f", template, "-n", "named"));

        Assert.Equal(0, json.ExitCode);
        Assert.Equal("Succeeded", json.Json["status"]!.GetValue<string>());
        Assert.Equal("empty", json.Json["deploymentName"]!.GetValue<string>());
        Assert.Equal(CliSandbox.Tenant, json.Json["tenantId"]!.GetValue<string>());
        Assert.Equal("Incremental", json.Json["mode"]!.GetValue<string>());
        Assert.Empty(json.Json["changes"]!.AsArray());
        Assert.Equal(0, json.Json["summary"]!["create"]!.GetValue<int>());
        Assert.Equal(string.Empty, json.StandardError);

        Assert.Equal(0, table.ExitCode);
        Assert.Contains("Deployment 'named' (Incremental) on tenant " + CliSandbox.Tenant, table.StandardOutput);
        Assert.Contains("Resource changes: 0 to create, 0 to modify, 0 to delete, 0 unchanged, 0 ignored.", table.StandardOutput);
        Assert.Contains("The template declares no resources and the state records none.", table.StandardOutput);

        Assert.False(File.Exists(sandbox.StatePath("empty")), "what-if never writes a state");
        Assert.False(File.Exists(sandbox.LockPath("empty")), "what-if never locks");
        // Per run: one GET /organization (the preflight; it also feeds tenant()); what-if never writes.
        Assert.Equal(2, sandbox.RecordedCommands().Count);
        Assert.All(sandbox.RecordedCommands(), c => Assert.StartsWith("GET ", c, StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhatIf_OrphanedStateEntry_IsIgnoreRowInIncremental_AndDeleteRowInComplete()
    {
        using var sandbox = new CliSandbox(nameof(WhatIf_OrphanedStateEntry_IsIgnoreRowInIncremental_AndDeleteRowInComplete));
        sandbox.UsePreflightOk(extraResponses: GroupResponses);
        string template = sandbox.WriteEmptyTemplate();
        await sandbox.WriteStateAsync(OrphanState("empty"));

        var incremental = await CliRunner.RunAsync(Args(sandbox, "deployment", "what-if", "-f", template, "-o", "json", "--exit-code-on-changes"));
        sandbox.UsePreflightOk(extraResponses: GroupResponses);
        var complete = await CliRunner.RunAsync(Args(sandbox, "deployment", "what-if", "-f", template, "-o", "json", "--mode", "Complete", "--exit-code-on-changes"));
        sandbox.UsePreflightOk(extraResponses: GroupResponses);
        var completeText = await CliRunner.RunAsync(Args(sandbox, "deployment", "what-if", "-f", template, "--mode", "Complete"));

        Assert.Equal(0, incremental.ExitCode);
        var ignore = Assert.Single(incremental.Json["changes"]!.AsArray());
        Assert.Equal("/providers/Microsoft.Entra/groups/old-group", ignore!["resourceId"]!.GetValue<string>());
        Assert.Equal("Ignore", ignore["changeType"]!.GetValue<string>());
        Assert.Contains(ModePass.DetachReasonWhatIf, ignore["reasons"]!.AsArray().Select(r => r!.GetValue<string>()));
        Assert.Contains(incremental.Diagnostics, d => d!["code"]!.GetValue<string>() == EngineDiagnosticCodes.ResourceDetached && d["level"]!.GetValue<string>() == "Warning");
        Assert.Equal(1, incremental.Json["summary"]!["ignore"]!.GetValue<int>());

        Assert.Equal(5, complete.ExitCode);
        var delete = Assert.Single(complete.Json["changes"]!.AsArray());
        Assert.Equal("Delete", delete!["changeType"]!.GetValue<string>());
        Assert.Equal(1, complete.Json["summary"]!["delete"]!.GetValue<int>());
        Assert.Equal("Complete", complete.Json["mode"]!.GetValue<string>());

        Assert.Equal(0, completeText.ExitCode);
        Assert.Contains("  - /providers/Microsoft.Entra/groups/old-group", completeText.StandardOutput);
        Assert.True(File.Exists(sandbox.StatePath("empty")), "what-if leaves the state alone");
        Assert.Single(JsonNode.Parse(await File.ReadAllTextAsync(sandbox.StatePath("empty"), Ct))!["resources"]!.AsObject());
    }

    // ---- create ----

    [Fact]
    public async Task Create_EmptyTemplate_WritesTheStateAndOutputs_AndReleasesTheLock()
    {
        using var sandbox = new CliSandbox(nameof(Create_EmptyTemplate_WritesTheStateAndOutputs_AndReleasesTheLock));
        sandbox.UsePreflightOk();
        string template = sandbox.WriteEmptyTemplate();

        var json = await CliRunner.RunAsync(Args(sandbox, "deployment", "create", "-f", template, "-y", "-o", "json"));
        sandbox.UsePreflightOk();
        var table = await CliRunner.RunAsync(Args(sandbox, "deployment", "create", "-f", template, "-y", "-n", "custom-name"));

        Assert.Equal(0, json.ExitCode);
        Assert.Equal("Succeeded", json.Json["status"]!.GetValue<string>());
        Assert.Equal("empty", json.Json["deploymentName"]!.GetValue<string>());
        Assert.Equal("hello", json.Json["outputs"]!["greeting"]!["value"]!.GetValue<string>());
        Assert.Equal(0, json.Json["succeeded"]!.GetValue<int>());
        Assert.Empty(json.Json["operations"]!.AsArray());
        Assert.Null(json.Json["secureOutputsFile"]);

        var state = JsonNode.Parse(await File.ReadAllTextAsync(sandbox.StatePath("empty"), Ct))!.AsObject();
        Assert.Equal(1, state["schemaVersion"]!.GetValue<int>());
        Assert.Equal("empty", state["deploymentName"]!.GetValue<string>());
        Assert.Equal(CliSandbox.Tenant, state["tenantId"]!.GetValue<string>());
        Assert.Equal("Succeeded", state["lastStatus"]!.GetValue<string>());
        Assert.Equal("Incremental", state["lastMode"]!.GetValue<string>());
        Assert.StartsWith("sha256:", state["lastTemplateHash"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Empty(state["resources"]!.AsObject());
        Assert.False(File.Exists(sandbox.LockPath("empty")), "the lock is released after the run");

        Assert.Equal(0, table.ExitCode);
        Assert.Contains("Deployment 'custom-name' Succeeded", table.StandardOutput);
        Assert.Contains("greeting: \"hello\"", table.StandardOutput);
        Assert.True(File.Exists(sandbox.StatePath("custom-name")));
    }

    [Fact]
    public async Task Create_SecureOutputs_AreRedactedUnlessRevealed_AndWrittenToTheFile()
    {
        using var sandbox = new CliSandbox(nameof(Create_SecureOutputs_AreRedactedUnlessRevealed_AndWrittenToTheFile));
        sandbox.UsePreflightOk();
        string template = sandbox.WriteTemplate("secret.entra.json", "\"parameters\": { \"pw\": { \"type\": \"securestring\" } }, \"resources\": [], \"outputs\": { \"echo\": { \"type\": \"securestring\", \"value\": \"[parameters('pw')]\" } }");
        string secureFile = Path.Combine(sandbox.Root, "secure-outputs.json");

        var redacted = await CliRunner.RunAsync(Args(sandbox, "deployment", "create", "-f", template, "-p", "pw=hunter2", "-y", "-o", "json", "--secure-outputs-file", secureFile));
        sandbox.UsePreflightOk();
        var revealed = await CliRunner.RunAsync(Args(sandbox, "deployment", "create", "-f", template, "-p", "pw=hunter2", "-y", "-o", "json", "--reveal-secure-outputs"));

        Assert.Equal(0, redacted.ExitCode);
        Assert.Equal("***", redacted.Json["outputs"]!["echo"]!["value"]!.GetValue<string>());
        Assert.Equal(secureFile, redacted.Json["secureOutputsFile"]!.GetValue<string>());
        Assert.DoesNotContain("hunter2", redacted.StandardOutput, StringComparison.Ordinal);
        Assert.Equal("hunter2", JsonNode.Parse(await File.ReadAllTextAsync(secureFile, Ct))!["echo"]!["value"]!.GetValue<string>());
        Assert.Equal(0, revealed.ExitCode);
        Assert.Equal("hunter2", revealed.Json["outputs"]!["echo"]!["value"]!.GetValue<string>());
        Assert.DoesNotContain("hunter2", await File.ReadAllTextAsync(sandbox.StatePath("secret"), Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_ConfirmWithWhatIf_DeclinedAtThePrompt_ExitsOneWithoutDeploying()
    {
        using var sandbox = new CliSandbox(nameof(Create_ConfirmWithWhatIf_DeclinedAtThePrompt_ExitsOneWithoutDeploying));
        sandbox.UsePreflightOk();
        string template = sandbox.WriteEmptyTemplate();
        var invocation = new DeploymentInvocation
        {
            TemplateFile = template,
            ConfirmWithWhatIf = true,
            Output = OutputFormat.Json,
            StateDir = sandbox.StateDir,
            Connection = sandbox.Connection,
        };
        using var declinedOut = new StringWriter();
        using var declinedErr = new StringWriter();

        int declined = await DeploymentRunner.CreateAsync(invocation, declinedOut, declinedErr, () => "n", Ct);

        Assert.Equal(1, declined);
        Assert.Contains("Deployment not started.", declinedErr.ToString());
        Assert.Contains("Are you sure you want to execute the deployment?", declinedErr.ToString());
        Assert.Contains("\"changes\"", declinedOut.ToString());
        Assert.False(File.Exists(sandbox.StatePath("empty")));
        Assert.False(File.Exists(sandbox.LockPath("empty")));

        sandbox.UsePreflightOk();
        using var acceptedOut = new StringWriter();
        using var acceptedErr = new StringWriter();
        int accepted = await DeploymentRunner.CreateAsync(invocation, acceptedOut, acceptedErr, () => "yes", Ct);

        Assert.Equal(0, accepted);
        Assert.True(File.Exists(sandbox.StatePath("empty")));
        Assert.Contains("\"status\": \"Succeeded\"", acceptedOut.ToString());
    }

    [Fact]
    public async Task Create_ConfirmWithWhatIf_OnRedirectedStdin_RefusesWithTheYesHint()
    {
        Assert.SkipUnless(Console.IsInputRedirected, "standard input is interactive in this test host; the prompt would block.");
        using var sandbox = new CliSandbox(nameof(Create_ConfirmWithWhatIf_OnRedirectedStdin_RefusesWithTheYesHint));
        sandbox.UsePreflightOk();
        string template = sandbox.WriteEmptyTemplate();

        var result = await CliRunner.RunAsync(Args(sandbox, "deployment", "create", "-f", template, "-c"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("pass --yes to confirm", result.StandardError);
        Assert.Contains("Deployment not started.", result.StandardError);
        Assert.False(File.Exists(sandbox.StatePath("empty")));
    }

    [Fact]
    public async Task Create_Locked_ExitsFour_AndForceUnlockBreaksTheLock()
    {
        using var sandbox = new CliSandbox(nameof(Create_Locked_ExitsFour_AndForceUnlockBreaksTheLock));
        sandbox.UsePreflightOk();
        string template = sandbox.WriteEmptyTemplate();
        sandbox.WriteLock("empty", DateTimeOffset.UtcNow);

        var locked = await CliRunner.RunAsync(Args(sandbox, "deployment", "create", "-f", template, "-y", "--lock-timeout", "0"));
        bool stateAfterLocked = File.Exists(sandbox.StatePath("empty"));
        bool lockAfterLocked = File.Exists(sandbox.LockPath("empty"));
        sandbox.UsePreflightOk();
        var forced = await CliRunner.RunAsync(Args(sandbox, "deployment", "create", "-f", template, "-y", "--force-unlock"));

        Assert.Equal(4, locked.ExitCode);
        Assert.Contains("is locked by process 424242 on other-host (someone)", locked.StandardError);
        Assert.Contains("earm state unlock -n empty", locked.StandardError);
        Assert.False(stateAfterLocked, "a locked run never writes the state");
        Assert.True(lockAfterLocked, "a locked run leaves the foreign lock alone");

        Assert.Equal(0, forced.ExitCode);
        Assert.Contains("Broke the state lock of deployment 'empty' held by process 424242", forced.StandardError);
        Assert.True(File.Exists(sandbox.StatePath("empty")));
        Assert.False(File.Exists(sandbox.LockPath("empty")));
    }

    [Fact]
    public async Task Create_StaleLock_IsTakenOver()
    {
        using var sandbox = new CliSandbox(nameof(Create_StaleLock_IsTakenOver));
        sandbox.UsePreflightOk();
        string template = sandbox.WriteEmptyTemplate();
        sandbox.WriteLock("empty", DateTimeOffset.UtcNow.AddHours(-2));

        var result = await CliRunner.RunAsync(Args(sandbox, "deployment", "create", "-f", template, "-y", "--lock-timeout", "0"));

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(sandbox.StatePath("empty")));
        Assert.False(File.Exists(sandbox.LockPath("empty")));
    }

    [Fact]
    public async Task Create_Incremental_DetachesOrphanedEntries()
    {
        using var sandbox = new CliSandbox(nameof(Create_Incremental_DetachesOrphanedEntries));
        sandbox.UsePreflightOk();
        string template = sandbox.WriteEmptyTemplate();
        await sandbox.WriteStateAsync(OrphanState("empty"));

        var result = await CliRunner.RunAsync(Args(sandbox, "deployment", "create", "-f", template, "-y", "-o", "json"));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("Succeeded", result.Json["status"]!.GetValue<string>());
        Assert.Equal(1, result.Json["detached"]!.GetValue<int>());
        var operation = Assert.Single(result.Json["operations"]!.AsArray());
        Assert.Equal("Detached", operation!["status"]!.GetValue<string>());
        Assert.Contains(result.Json["diagnostics"]!.AsArray(), d => d!["code"]!.GetValue<string>() == EngineDiagnosticCodes.ResourceDetached);
        var state = JsonNode.Parse(await File.ReadAllTextAsync(sandbox.StatePath("empty"), Ct))!.AsObject();
        Assert.Empty(state["resources"]!.AsObject());
        Assert.Equal("Succeeded", state["lastStatus"]!.GetValue<string>());
        Assert.Single(state["operations"]!.AsArray());
    }

    // ---- state directory resolution ----

    [Fact]
    public async Task StateDirectory_FlagBeatsEnvironment_WhichBeatsTheTemplateDirectory()
    {
        using var sandbox = new CliSandbox(nameof(StateDirectory_FlagBeatsEnvironment_WhichBeatsTheTemplateDirectory));
        sandbox.UsePreflightOk();
        string template = sandbox.WriteEmptyTemplate();
        string fromEnvironment = Path.Combine(sandbox.Root, "env-state");

        var flag = await CliRunner.RunAsync("deployment", "list", "-o", "json", "--state-dir", sandbox.StateDir);
        sandbox.SetStateDirVariable(fromEnvironment);
        var environment = await CliRunner.RunAsync("deployment", "list", "-o", "json");
        var flagOverEnvironment = await CliRunner.RunAsync("deployment", "list", "-o", "json", "--state-dir", sandbox.StateDir);
        var created = await CliRunner.RunAsync(["deployment", "create", "-f", template, "-y", "-o", "none"]);
        sandbox.SetStateDirVariable(null);
        var templateRelative = await CliRunner.RunAsync("deployment", "list", "-o", "json", "-f", template);
        var workingDirectory = await CliRunner.RunAsync("deployment", "list", "-o", "json");

        Assert.Equal(sandbox.StateDir, flag.Json["stateDirectory"]!.GetValue<string>());
        Assert.Equal(fromEnvironment, environment.Json["stateDirectory"]!.GetValue<string>());
        Assert.Equal(sandbox.StateDir, flagOverEnvironment.Json["stateDirectory"]!.GetValue<string>());
        Assert.Equal(0, created.ExitCode);
        Assert.True(File.Exists(Path.Combine(fromEnvironment, "empty.json")), "create honours EARM_STATE_DIR");
        Assert.Equal(Path.Combine(sandbox.TemplatesDir, ".earm", "state"), templateRelative.Json["stateDirectory"]!.GetValue<string>());
        Assert.Equal(Path.Combine(Directory.GetCurrentDirectory(), ".earm", "state"), workingDirectory.Json["stateDirectory"]!.GetValue<string>());
    }

    // ---- help ----

    public static TheoryData<string[], string[]> HelpCases()
    {
        return new TheoryData<string[], string[]>
        {
            {
                new[] { "deployment", "create", "--help" },
                new[] { "--template-file", "-f", "--parameters", "-p", "--name", "-n", "--mode", "--parallelism", "--allow-ca-enforce", "--on-soft-deleted-conflict", "--what-if", "--confirm-with-what-if", "-c", "--yes", "-y", "--continue-on-error", "--reveal-secure-outputs", "--secure-outputs-file", "--force-unlock", "--lock-timeout", "--result-format", "--no-pretty-print", "--state-dir", "--auth", "--tenant", "--client-id", "--output", "-o", "--verbose", "--debug" }
            },
            {
                new[] { "deployment", "what-if", "--help" },
                new[] { "--template-file", "--parameters", "--name", "--mode", "--parallelism", "--allow-ca-enforce", "--on-soft-deleted-conflict", "--result-format", "--exit-code-on-changes", "--no-pretty-print", "--state-dir", "--auth" }
            },
            {
                new[] { "deployment", "delete", "--help" },
                new[] { "--name", "--template-file", "--yes", "--delete-resources", "--force-unlock", "--continue-on-error", "--state-dir" }
            },
            {
                new[] { "deployment", "show", "--help" },
                new[] { "--name", "--template-file", "--refresh", "--state-dir" }
            },
            {
                new[] { "deployment", "operation", "list", "--help" },
                new[] { "--name", "--template-file", "--state-dir" }
            },
            {
                new[] { "state", "forget", "--help" },
                new[] { "--name", "--resource-id", "--yes", "--state-dir" }
            },
            {
                new[] { "state", "unlock", "--help" },
                new[] { "--name", "--template-file", "--state-dir" }
            },
            {
                new[] { "deployment", "--help" },
                new[] { "validate", "what-if", "create", "show", "list", "delete", "operation" }
            },
            {
                new[] { "state", "--help" },
                new[] { "unlock", "forget", "adopt" }
            },
        };
    }

    [Theory]
    [MemberData(nameof(HelpCases))]
    public async Task Help_ListsEveryFlag(string[] args, string[] expected)
    {
        var result = await CliRunner.RunAsync(args);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        foreach (var flag in expected)
        {
            Assert.Contains(flag, result.StandardOutput);
        }
    }

    [Fact]
    public async Task Help_DocumentsTheExitCodes()
    {
        var result = await CliRunner.RunAsync("deployment", "what-if", "--help");

        Assert.Contains("Exit with code 5", result.StandardOutput);
        var create = await CliRunner.RunAsync("deployment", "create", "--help");
        Assert.Contains("exit code 4", create.StandardOutput);
    }
}
