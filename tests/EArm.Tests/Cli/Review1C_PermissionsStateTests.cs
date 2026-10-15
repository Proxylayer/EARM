using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Engine.State;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// Review 1C, finding I-08: <c>earm permissions</c> listed what a deployment needs to create and update, but a <c>--mode Complete</c> run also
/// deletes the objects the state records and the template no longer declares, which needs their delete permissions. With <c>-n &lt;deployment&gt;</c>
/// (and <c>--state-dir</c>) the command reads the state and adds them; without it a note says the list is incomplete for Complete mode.
/// </summary>
[Collection(CliCollection.Name)]
public sealed class Review1C_PermissionsStateTests
{
    private const string GroupType = "Microsoft.Entra/groups";
    private const string ApplicationType = "Microsoft.Entra/applications";

    private static StateEntry Entry(string type, string name, string graphId)
    {
        return new StateEntry
        {
            Id = ResourceIdentifier.Create(type, name),
            Type = type,
            ApiVersion = "2026-10-01-preview",
            GraphId = graphId,
        };
    }

    private static async Task<(CliSandbox Sandbox, string Template)> ArrangeAsync(string testName, params StateEntry[] entries)
    {
        var sandbox = new CliSandbox(testName);
        string template = sandbox.WriteTemplate("keep.entra.json", "\"resources\": [ { \"type\": \"" + GroupType + "\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"keep\", \"properties\": { \"displayName\": \"keep\", \"mailEnabled\": false, \"securityEnabled\": true } } ]");
        var state = StateFile.CreateNew("d", CliSandbox.Tenant, DateTimeOffset.UtcNow);
        foreach (var entry in entries)
        {
            state.Upsert(entry);
        }

        await sandbox.WriteStateAsync(state);
        return (sandbox, template);
    }

    private static JsonObject PermissionNamed(JsonNode report, string name)
    {
        return report["permissions"]!.AsArray().Select(p => p!.AsObject()).Single(p => p["name"]!.GetValue<string>() == name);
    }

    [Fact]
    public async Task WithTheDeploymentName_TheDeletePermissionsOfUndeclaredTypes_AreAdded()
    {
        var (sandbox, template) = await ArrangeAsync(
            nameof(WithTheDeploymentName_TheDeletePermissionsOfUndeclaredTypes_AreAdded),
            Entry(GroupType, "keep", "a1000000-0000-4000-8000-000000000001"),
            Entry(GroupType, "gone", "a1000000-0000-4000-8000-000000000002"),
            Entry(ApplicationType, "old-app", "a1000000-0000-4000-8000-000000000003"));
        using (sandbox)
        {
            var without = await CliRunner.RunAsync([.. sandbox.StateArgs, "permissions", "-f", template, "-o", "json"]);
            var with = await CliRunner.RunAsync([.. sandbox.StateArgs, "permissions", "-f", template, "-n", "d", "-o", "json"]);

            Assert.Equal(0, without.ExitCode);
            Assert.DoesNotContain(without.Json["permissions"]!.AsArray(), p => p!["name"]!.GetValue<string>() == "Application.ReadWrite.All");

            Assert.Equal(0, with.ExitCode);
            var application = PermissionNamed(with.Json, "Application.ReadWrite.All");
            Assert.Equal(["/providers/Microsoft.Entra/applications/old-app"], application["resources"]!.AsArray().Select(r => r!.GetValue<string>()).ToArray());
            Assert.Contains("Complete mode", application["reasons"]!.AsArray()[0]!.GetValue<string>(), StringComparison.Ordinal);

            var group = PermissionNamed(with.Json, "Group.ReadWrite.All");
            var groupResources = group["resources"]!.AsArray().Select(r => r!.GetValue<string>()).ToArray();
            Assert.Contains("/providers/Microsoft.Entra/groups/keep", groupResources);
            Assert.Contains("/providers/Microsoft.Entra/groups/gone", groupResources);
            Assert.DoesNotContain("/providers/Microsoft.Entra/groups/keep", with.Json["permissions"]!.AsArray().Where(p => p!["name"]!.GetValue<string>() == "Application.ReadWrite.All").SelectMany(p => p!["resources"]!.AsArray()).Select(r => r!.GetValue<string>()));
            Assert.Contains(with.Json["notes"]!.AsArray(), n => n!.GetValue<string>().Contains("2 object(s) recorded in the state of deployment 'd'", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task WithoutTheDeploymentName_ANoteSaysCompleteModeNeedsMore()
    {
        var (sandbox, template) = await ArrangeAsync(nameof(WithoutTheDeploymentName_ANoteSaysCompleteModeNeedsMore), Entry(GroupType, "keep", "a1000000-0000-4000-8000-000000000001"));
        using (sandbox)
        {
            var json = await CliRunner.RunAsync([.. sandbox.StateArgs, "permissions", "-f", template, "-o", "json"]);
            var table = await CliRunner.RunAsync([.. sandbox.StateArgs, "permissions", "-f", template]);

            Assert.Contains(json.Json["notes"]!.AsArray(), n => n!.GetValue<string>().Contains("--mode Complete", StringComparison.Ordinal) && n.GetValue<string>().Contains("-n <deployment>", StringComparison.Ordinal));
            Assert.Contains("delete permissions of every type the state records", table.StandardOutput, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ADeploymentWithoutState_OrWithNothingUndeclared_AddsNothing_AndSaysSo()
    {
        var (sandbox, template) = await ArrangeAsync(nameof(ADeploymentWithoutState_OrWithNothingUndeclared_AddsNothing_AndSaysSo), Entry(GroupType, "keep", "a1000000-0000-4000-8000-000000000001"));
        using (sandbox)
        {
            var missing = await CliRunner.RunAsync([.. sandbox.StateArgs, "permissions", "-f", template, "-n", "nope", "-o", "json"]);
            var nothing = await CliRunner.RunAsync([.. sandbox.StateArgs, "permissions", "-f", template, "-n", "d", "-o", "json"]);

            Assert.Equal(0, missing.ExitCode);
            Assert.Contains(missing.Json["notes"]!.AsArray(), n => n!.GetValue<string>().Contains("There is no state for deployment 'nope'", StringComparison.Ordinal));
            Assert.Equal(0, nothing.ExitCode);
            Assert.Contains(nothing.Json["notes"]!.AsArray(), n => n!.GetValue<string>().Contains("records no object that the template does not declare", StringComparison.Ordinal));
            Assert.Equal(
                missing.Json["permissions"]!.AsArray().Select(p => p!["name"]!.GetValue<string>()).ToArray(),
                nothing.Json["permissions"]!.AsArray().Select(p => p!["name"]!.GetValue<string>()).ToArray());
        }
    }

    [Fact]
    public async Task InDelegatedMode_TheSameDeletePermissionsAreListedAsScopes()
    {
        var (sandbox, template) = await ArrangeAsync(
            nameof(InDelegatedMode_TheSameDeletePermissionsAreListedAsScopes),
            Entry(GroupType, "keep", "a1000000-0000-4000-8000-000000000001"),
            Entry(ApplicationType, "old-app", "a1000000-0000-4000-8000-000000000003"));
        using (sandbox)
        {
            var result = await CliRunner.RunAsync([.. sandbox.StateArgs, "permissions", "-f", template, "-n", "d", "--mode", "delegated", "-o", "json"]);

            Assert.Equal(0, result.ExitCode);
            var application = PermissionNamed(result.Json, "Application.ReadWrite.All");
            Assert.Equal("graphDelegated", application["kind"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task AnUnreadableState_IsANote_NotACrash()
    {
        var (sandbox, template) = await ArrangeAsync(nameof(AnUnreadableState_IsANote_NotACrash), Entry(GroupType, "keep", "a1000000-0000-4000-8000-000000000001"));
        using (sandbox)
        {
            sandbox.WriteRawState("broken", "{ this is not json");

            var result = await CliRunner.RunAsync([.. sandbox.StateArgs, "permissions", "-f", template, "-n", "broken", "-o", "json"]);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains(result.Json["notes"]!.AsArray(), n => n!.GetValue<string>().Contains("could not be read", StringComparison.Ordinal));
        }
    }
}
