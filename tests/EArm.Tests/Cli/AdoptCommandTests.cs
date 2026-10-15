using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Cli;
using EArm.Cli.Commands;
using EArm.Core;
using EArm.Core.Types;
using EArm.Engine.State;
using EArm.TestSupport;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary><c>earm state adopt</c>: the offline pin form and the online uniqueName form (against a fake Microsoft Graph).</summary>
[Collection(CliCollection.Name)]
public sealed class AdoptCommandTests
{
    private const string Dep = "orders-prod";
    private const string Provider = "/providers/Microsoft.Entra/";
    private const string Obj1 = "6f1b0000-0000-4000-8000-000000000001";
    private const string Obj2 = "6f1b0000-0000-4000-8000-000000000002";
    private const string Principal = "aaaaaaaa-0000-4000-8000-000000000001";
    private const string ResourceGuid = "bbbbbbbb-0000-4000-8000-000000000002";
    private const string AppRole = "cccccccc-0000-4000-8000-000000000003";
    private const string NameGuid = "dddddddd-0000-4000-8000-000000000004";
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<CliResult> Pin(CliSandbox sandbox, string resourceId, string objectId, params string[] extra)
    {
        string[] args = ["state", "adopt", "-n", Dep, "--resource-id", resourceId, "--object-id", objectId, "-y", "--state-dir", sandbox.StateDir, .. extra];
        return CliRunner.RunAsync(args);
    }

    private static StateInvocation Invocation(CliSandbox sandbox, bool yes = false)
    {
        return new StateInvocation
        {
            Name = Dep,
            StateDir = sandbox.StateDir,
            Output = OutputFormat.Table,
            Connection = sandbox.Connection,
            Yes = yes,
        };
    }

    private static ResourceIdentifier ParseId(string resourceId)
    {
        Assert.True(ResourceTypeCatalog.Default.TryParseResourceId(resourceId, out var identifier), resourceId);
        return identifier!;
    }

    private static StateEntry Entry(string resourceId, string graphId, string type, string keyName, string keyValue, string? parent = null)
    {
        return new StateEntry
        {
            Id = ParseId(resourceId),
            Type = type,
            ApiVersion = "2026-10-01-preview",
            Key = new Dictionary<string, string> { [keyName] = keyValue },
            GraphId = graphId,
            ParentGraphId = parent,
            LastWriteOn = Start,
        };
    }

    // ---- pin: one case per name kind ----

    [Theory]
    [InlineData("applications", "orders-api", "uniqueName", "orders-api")]
    [InlineData("groups", "contoso-admins", "uniqueName", "contoso-admins")]
    [InlineData("users", "bg-orders@contoso.com", "userPrincipalName", "bg-orders@contoso.com")]
    [InlineData("servicePrincipals", NameGuid, "appId", NameGuid)]
    [InlineData("conditionalAccess/policies", "CA-Orders-Admins-MFA", "displayName", "CA-Orders-Admins-MFA")]
    [InlineData("conditionalAccess/namedLocations", "Contoso offices", "displayName", "Contoso offices")]
    [InlineData("conditionalAccess/authenticationStrengths", "Orders phishing-resistant", "displayName", "Orders phishing-resistant")]
    [InlineData("administrativeUnits", "Orders AU", "displayName", "Orders AU")]
    [InlineData("roleDefinitions", "Orders Support", "displayName", "Orders Support")]
    public async Task Pin_RecordsTheObjectId_UnderTheKeyOfTheTypesNameKind(string type, string name, string keyName, string keyValue)
    {
        using var sandbox = new CliSandbox(nameof(Pin_RecordsTheObjectId_UnderTheKeyOfTheTypesNameKind));
        string resourceId = Provider + type + "/" + name;

        var result = await Pin(sandbox, resourceId, Obj1);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        Assert.Contains($"Pinned {resourceId} to {Obj1} in deployment '{Dep}'", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("the state file was created", result.StandardOutput, StringComparison.Ordinal);
        var state = await sandbox.Store.LoadAsync(Dep, Ct);
        Assert.NotNull(state);
        var entry = Assert.Single(state.Resources).Value;
        Assert.Equal(resourceId, entry.Id.Id);
        Assert.Equal("Microsoft.Entra/" + type, entry.Type);
        Assert.Equal(Obj1, entry.GraphId);
        Assert.Equal("2026-10-01-preview", entry.ApiVersion);
        Assert.Equal(new Dictionary<string, string> { [keyName] = keyValue }, entry.Key.ToDictionary(p => p.Key, p => p.Value));
        Assert.Null(entry.ParentGraphId);
        Assert.Null(state.TenantId);
        Assert.False(File.Exists(sandbox.LockPath(Dep)));
        Assert.Empty(sandbox.RecordedCommands());
    }

    [Fact]
    public async Task Pin_AddsToAnExistingState_AndKeepsTheOtherEntries()
    {
        using var sandbox = new CliSandbox(nameof(Pin_AddsToAnExistingState_AndKeepsTheOtherEntries));
        var existing = StateFile.CreateNew(Dep, CliSandbox.Tenant, Start);
        existing.Upsert(Entry(Provider + "groups/other", "61000000-0000-4000-8000-000000000001", "Microsoft.Entra/groups", "uniqueName", "other"));
        await sandbox.WriteStateAsync(existing);

        var result = await Pin(sandbox, Provider + "applications/orders-api", Obj1);

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("the state file was created", result.StandardOutput, StringComparison.Ordinal);
        var state = (await sandbox.Store.LoadAsync(Dep, Ct))!;
        Assert.Equal(2, state.Resources.Count);
        Assert.Equal("61000000-0000-4000-8000-000000000001", state.GraphIdOf(Provider + "groups/other"));
        Assert.Equal(Obj1, state.GraphIdOf(Provider + "applications/orders-api"));
        Assert.Equal(CliSandbox.Tenant, state.TenantId);
    }

    [Fact]
    public async Task Pin_JsonOutput_HasTheDocumentedFields()
    {
        using var sandbox = new CliSandbox(nameof(Pin_JsonOutput_HasTheDocumentedFields));

        var result = await Pin(sandbox, Provider + "applications/orders-api", Obj1, "-o", "json");

        Assert.Equal(0, result.ExitCode);
        var json = result.Json;
        Assert.Equal(["status", "deployment", "stateFile", "resourceId", "type", "objectId", "previousObjectId", "stateCreated"], json.Select(p => p.Key).ToArray());
        Assert.Equal(Dep, json["deployment"]!.GetValue<string>());
        Assert.Equal(sandbox.StatePath(Dep), json["stateFile"]!.GetValue<string>());
        Assert.Equal(Obj1, json["objectId"]!.GetValue<string>());
        Assert.Null(json["previousObjectId"]);
        Assert.True(json["stateCreated"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("6F1B0000-0000-4000-8000-0000000000AA", "6f1b0000-0000-4000-8000-0000000000aa")]
    [InlineData("{6f1b0000-0000-4000-8000-0000000000aa}", "6f1b0000-0000-4000-8000-0000000000aa")]
    [InlineData("6f1b0000000040008000" + "0000000000aa", "6f1b0000-0000-4000-8000-0000000000aa")]
    public async Task Pin_NormalisesTheObjectIdToLowerCaseHyphenatedForm(string given, string expected)
    {
        using var sandbox = new CliSandbox(nameof(Pin_NormalisesTheObjectIdToLowerCaseHyphenatedForm));

        var result = await Pin(sandbox, Provider + "applications/orders-api", given);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, (await sandbox.Store.LoadAsync(Dep, Ct))!.GraphIdOf(Provider + "applications/orders-api"));
    }

    // ---- pin: children ----

    [Fact]
    public async Task Pin_ChildWithoutAParentInTheState_IsRefused()
    {
        using var sandbox = new CliSandbox(nameof(Pin_ChildWithoutAParentInTheState_IsRefused));

        var result = await Pin(sandbox, Provider + "applications/orders-api/federatedIdentityCredentials/github", Obj1);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("is a child of " + Provider + "applications/orders-api, which the state does not know yet", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("--parent-object-id", result.StandardError, StringComparison.Ordinal);
        Assert.False(File.Exists(sandbox.StatePath(Dep)));
    }

    [Fact]
    public async Task Pin_ChildWithTheParentInTheState_TakesItsParentGraphId()
    {
        using var sandbox = new CliSandbox(nameof(Pin_ChildWithTheParentInTheState_TakesItsParentGraphId));
        var existing = StateFile.CreateNew(Dep, CliSandbox.Tenant, Start);
        existing.Upsert(Entry(Provider + "applications/orders-api", "a9000000-0000-4000-8000-000000000001", "Microsoft.Entra/applications", "uniqueName", "orders-api"));
        await sandbox.WriteStateAsync(existing);

        var fic = await Pin(sandbox, Provider + "applications/orders-api/federatedIdentityCredentials/github", Obj1);
        var secret = await Pin(sandbox, Provider + "applications/orders-api/passwordCredentials/ci-secret", Obj2);

        Assert.Equal(0, fic.ExitCode);
        Assert.Equal(0, secret.ExitCode);
        var state = (await sandbox.Store.LoadAsync(Dep, Ct))!;
        var ficEntry = state.Find(Provider + "applications/orders-api/federatedIdentityCredentials/github")!;
        Assert.Equal("a9000000-0000-4000-8000-000000000001", ficEntry.ParentGraphId);
        Assert.Equal("name", ficEntry.Key.Single().Key);
        Assert.Equal("github", ficEntry.Key.Single().Value);
        var secretEntry = state.Find(Provider + "applications/orders-api/passwordCredentials/ci-secret")!;
        Assert.Equal("a9000000-0000-4000-8000-000000000001", secretEntry.ParentGraphId);
        Assert.Equal("displayName", secretEntry.Key.Single().Key);
        Assert.Equal(3, state.Resources.Count);
    }

    [Fact]
    public async Task Pin_ChildWithAParentObjectId_NeedsNoParentInTheState()
    {
        using var sandbox = new CliSandbox(nameof(Pin_ChildWithAParentObjectId_NeedsNoParentInTheState));

        var result = await Pin(sandbox, Provider + "applications/orders-api/federatedIdentityCredentials/github", Obj1, "--parent-object-id", Obj2);

        Assert.Equal(0, result.ExitCode);
        var entry = (await sandbox.Store.LoadAsync(Dep, Ct))!.Find(Provider + "applications/orders-api/federatedIdentityCredentials/github")!;
        Assert.Equal(Obj2, entry.ParentGraphId);
        Assert.Equal(Obj1, entry.GraphId);
    }

    [Fact]
    public async Task Pin_ParentObjectIdOnATopLevelType_IsRefused()
    {
        using var sandbox = new CliSandbox(nameof(Pin_ParentObjectIdOnATopLevelType_IsRefused));

        var result = await Pin(sandbox, Provider + "applications/orders-api", Obj1, "--parent-object-id", Obj2);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--parent-object-id applies to child types", result.StandardError, StringComparison.Ordinal);
        Assert.False(File.Exists(sandbox.StatePath(Dep)));
    }

    // ---- pin: GUID-named types ----

    [Fact]
    public async Task Pin_GuidNamedTypeWithoutKeys_IsRefused_NamingTheMissingKeys()
    {
        using var sandbox = new CliSandbox(nameof(Pin_GuidNamedTypeWithoutKeys_IsRefused_NamingTheMissingKeys));

        var assignment = await Pin(sandbox, Provider + "appRoleAssignments/" + NameGuid, Obj1);
        var grant = await Pin(sandbox, Provider + "oauth2PermissionGrants/" + NameGuid, Obj1);
        var role = await Pin(sandbox, Provider + "roleAssignments/" + NameGuid, Obj1);

        Assert.Equal(1, assignment.ExitCode);
        Assert.Contains("is matched by its properties, not its name: pass --key principalId=<value> --key resourceId=<value> --key appRoleId=<value>", assignment.StandardError, StringComparison.Ordinal);
        Assert.Equal(1, grant.ExitCode);
        Assert.Contains("--key clientId=<value> --key resourceId=<value> --key consentType=<value>", grant.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("principalId=<value>", grant.StandardError, StringComparison.Ordinal);
        Assert.Equal(1, role.ExitCode);
        Assert.Contains("--key roleDefinitionId=<value> --key principalId=<value> --key directoryScopeId=<value>", role.StandardError, StringComparison.Ordinal);
        Assert.False(File.Exists(sandbox.StatePath(Dep)));
    }

    [Fact]
    public async Task Pin_GuidNamedTypeWithItsKeys_RecordsTheCompositeKey()
    {
        using var sandbox = new CliSandbox(nameof(Pin_GuidNamedTypeWithItsKeys_RecordsTheCompositeKey));

        var assignment = await Pin(sandbox, Provider + "appRoleAssignments/" + NameGuid, Obj1,
            "--key", "principalId=" + Principal, "--key", "resourceId=" + ResourceGuid, "--key", "appRoleId=" + AppRole);
        var grant = await Pin(sandbox, Provider + "oauth2PermissionGrants/" + ResourceGuid, Obj2,
            "--key", "clientId=" + Principal, "--key", "resourceId=" + ResourceGuid, "--key", "consentType=AllPrincipals");

        Assert.Equal(0, assignment.ExitCode);
        Assert.Equal(0, grant.ExitCode);
        var state = (await sandbox.Store.LoadAsync(Dep, Ct))!;
        var assignmentKey = state.Find(Provider + "appRoleAssignments/" + NameGuid)!.Key;
        Assert.Equal(Principal, assignmentKey["principalId"]);
        Assert.Equal(ResourceGuid, assignmentKey["resourceId"]);
        Assert.Equal(AppRole, assignmentKey["appRoleId"]);
        var grantKey = state.Find(Provider + "oauth2PermissionGrants/" + ResourceGuid)!.Key;
        Assert.Equal(3, grantKey.Count);
        Assert.Equal("AllPrincipals", grantKey["consentType"]);
        Assert.False(grantKey.ContainsKey("principalId"));
    }

    [Fact]
    public async Task Pin_ConsentTypePrincipal_NeedsThePrincipalIdKey()
    {
        using var sandbox = new CliSandbox(nameof(Pin_ConsentTypePrincipal_NeedsThePrincipalIdKey));

        var result = await Pin(sandbox, Provider + "oauth2PermissionGrants/" + NameGuid, Obj1,
            "--key", "clientId=" + Principal, "--key", "resourceId=" + ResourceGuid, "--key", "consentType=Principal");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--key principalId=<value>", result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("appRoleAssignments", "not-a-guid", "must be a GUID")]
    [InlineData("servicePrincipals", "orders", "must be a GUID")]
    [InlineData("users", "no-at-sign", "must be a user principal name")]
    [InlineData("conditionalAccess/authenticationContextClassReferences", "c10", "addressed by its name")]
    public async Task Pin_NamesThatCannotBePinned_AreRefused(string type, string name, string message)
    {
        using var sandbox = new CliSandbox(nameof(Pin_NamesThatCannotBePinned_AreRefused));

        var result = await Pin(sandbox, Provider + type + "/" + name, Obj1);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(message, result.StandardError, StringComparison.Ordinal);
        Assert.False(File.Exists(sandbox.StatePath(Dep)));
    }

    [Fact]
    public async Task Pin_BadKeyTokens_AreRefused()
    {
        using var sandbox = new CliSandbox(nameof(Pin_BadKeyTokens_AreRefused));
        string assignment = Provider + "appRoleAssignments/" + NameGuid;

        var malformed = await Pin(sandbox, assignment, Obj1, "--key", "principalId");
        var unknownProperty = await Pin(sandbox, assignment, Obj1, "--key", "colour=red");
        var onNameKeyedType = await Pin(sandbox, Provider + "applications/orders-api", Obj1, "--key", "uniqueName=orders-api");

        Assert.Equal(1, malformed.ExitCode);
        Assert.Contains("--key 'principalId' is not name=value", malformed.StandardError, StringComparison.Ordinal);
        Assert.Equal(1, unknownProperty.ExitCode);
        Assert.Contains("'colour' is not a key property of Microsoft.Entra/appRoleAssignments", unknownProperty.StandardError, StringComparison.Ordinal);
        Assert.Equal(1, onNameKeyedType.ExitCode);
        Assert.Contains("--key applies to GUID-named types only", onNameKeyedType.StandardError, StringComparison.Ordinal);
    }

    // ---- pin: re-pin ----

    [Fact]
    public async Task Pin_SameIdAgain_IsANoOp_WithoutAPrompt()
    {
        using var sandbox = new CliSandbox(nameof(Pin_SameIdAgain_IsANoOp_WithoutAPrompt));
        string resourceId = Provider + "applications/orders-api";
        Assert.Equal(0, (await Pin(sandbox, resourceId, Obj1)).ExitCode);
        var before = File.ReadAllText(sandbox.StatePath(Dep));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        string[] prompts = [];

        int exit = await AdoptCommand.PinAsync(Invocation(sandbox), new AdoptRequest { ResourceId = resourceId, ObjectId = Obj1.ToUpperInvariant() }, stdout, stderr, () => throw new InvalidOperationException("no prompt expected"), Ct);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr.ToString());
        Assert.Contains($"is already pinned to {Obj1} in deployment '{Dep}'", stdout.ToString(), StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(sandbox.StatePath(Dep)));
        Assert.Empty(prompts);
    }

    [Fact]
    public async Task Pin_AnotherIdWithYes_ReplacesIt_AndReportsThePreviousOne()
    {
        using var sandbox = new CliSandbox(nameof(Pin_AnotherIdWithYes_ReplacesIt_AndReportsThePreviousOne));
        string resourceId = Provider + "applications/orders-api";
        Assert.Equal(0, (await Pin(sandbox, resourceId, Obj1)).ExitCode);

        var result = await Pin(sandbox, resourceId, Obj2, "-o", "json");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Obj1, result.Json["previousObjectId"]!.GetValue<string>());
        Assert.Equal(Obj2, result.Json["objectId"]!.GetValue<string>());
        Assert.False(result.Json["stateCreated"]!.GetValue<bool>());
        Assert.Equal(Obj2, (await sandbox.Store.LoadAsync(Dep, Ct))!.GraphIdOf(resourceId));
    }

    [Fact]
    public async Task Pin_AnotherIdWithoutYes_AsksFirst_AndDeclineChangesNothing()
    {
        using var sandbox = new CliSandbox(nameof(Pin_AnotherIdWithoutYes_AsksFirst_AndDeclineChangesNothing));
        string resourceId = Provider + "applications/orders-api";
        Assert.Equal(0, (await Pin(sandbox, resourceId, Obj1)).ExitCode);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exit = await AdoptCommand.PinAsync(Invocation(sandbox), new AdoptRequest { ResourceId = resourceId, ObjectId = Obj2 }, stdout, stderr, () => "n", Ct);

        Assert.Equal(1, exit);
        Assert.Contains($"{resourceId} is pinned to {Obj1} in deployment '{Dep}'. Re-pin it to {Obj2}? (y/n): ", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("Nothing changed.", stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(Obj1, (await sandbox.Store.LoadAsync(Dep, Ct))!.GraphIdOf(resourceId));
        Assert.False(File.Exists(sandbox.LockPath(Dep)));
    }

    [Fact]
    public async Task Pin_AnotherIdWithoutYes_AnsweredYes_ReplacesIt()
    {
        using var sandbox = new CliSandbox(nameof(Pin_AnotherIdWithoutYes_AnsweredYes_ReplacesIt));
        string resourceId = Provider + "applications/orders-api";
        Assert.Equal(0, (await Pin(sandbox, resourceId, Obj1)).ExitCode);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exit = await AdoptCommand.PinAsync(Invocation(sandbox), new AdoptRequest { ResourceId = resourceId, ObjectId = Obj2 }, stdout, stderr, () => "y", Ct);

        Assert.Equal(0, exit);
        Assert.Equal(Obj2, (await sandbox.Store.LoadAsync(Dep, Ct))!.GraphIdOf(resourceId));
    }

    [Fact]
    public async Task Pin_Repin_KeepsTheExistingEntrysDependenciesAndApiVersion()
    {
        using var sandbox = new CliSandbox(nameof(Pin_Repin_KeepsTheExistingEntrysDependenciesAndApiVersion));
        var existing = StateFile.CreateNew(Dep, CliSandbox.Tenant, Start);
        var entry = Entry(Provider + "groups/contoso-admins", "c0de0000-0000-4000-8000-0000000000d1", "Microsoft.Entra/groups", "uniqueName", "contoso-admins");
        existing.Upsert(new StateEntry
        {
            Id = entry.Id,
            Type = entry.Type,
            ApiVersion = "2026-10-01-preview.beta",
            Key = entry.Key,
            GraphId = entry.GraphId,
            DependsOn = [Provider + "applications/orders-api"],
            LastWriteOn = Start,
        });
        await sandbox.WriteStateAsync(existing);

        var result = await Pin(sandbox, Provider + "groups/contoso-admins", Obj1);

        Assert.Equal(0, result.ExitCode);
        var updated = (await sandbox.Store.LoadAsync(Dep, Ct))!.Find(Provider + "groups/contoso-admins")!;
        Assert.Equal(Obj1, updated.GraphId);
        Assert.Equal("2026-10-01-preview.beta", updated.ApiVersion);
        Assert.Equal([Provider + "applications/orders-api"], updated.DependsOn);
    }

    // ---- pin: locked, bad input ----

    [Fact]
    public async Task Pin_LockedState_ExitsFour_AndChangesNothing()
    {
        using var sandbox = new CliSandbox(nameof(Pin_LockedState_ExitsFour_AndChangesNothing));
        Assert.Equal(0, (await Pin(sandbox, Provider + "applications/orders-api", Obj1)).ExitCode);
        sandbox.WriteLock(Dep, DateTimeOffset.UtcNow);
        string before = File.ReadAllText(sandbox.StatePath(Dep));

        var result = await Pin(sandbox, Provider + "groups/contoso-admins", Obj2);

        Assert.Equal(4, result.ExitCode);
        Assert.Contains("is locked by", result.StandardError, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(sandbox.StatePath(Dep)));
        Assert.True(File.Exists(sandbox.LockPath(Dep)), "the other run's lock must survive");
    }

    [Fact]
    public async Task Pin_UnreadableState_ExitsOne()
    {
        using var sandbox = new CliSandbox(nameof(Pin_UnreadableState_ExitsOne));
        sandbox.WriteRawState(Dep, "{ \"schemaVersion\": 9 }");

        var result = await Pin(sandbox, Provider + "applications/orders-api", Obj1);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("schemaVersion 9", result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("6f1b0000-0000-4000-8000")]
    [InlineData("{}")]
    [InlineData("zzzzzzzz-0000-4000-8000-000000000001")]
    public async Task Pin_BadObjectId_ExitsOne(string objectId)
    {
        using var sandbox = new CliSandbox(nameof(Pin_BadObjectId_ExitsOne));

        var result = await Pin(sandbox, Provider + "applications/orders-api", objectId);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"--object-id '{objectId}' is not a GUID", result.StandardError, StringComparison.Ordinal);
        Assert.False(File.Exists(sandbox.StatePath(Dep)));
    }

    [Theory]
    [InlineData("/providers/Microsoft.Entra/spaceships/x")]
    [InlineData("orders-api")]
    [InlineData("/providers/Microsoft.Entra/applications")]
    [InlineData("/subscriptions/x/providers/Microsoft.Entra/applications/orders-api")]
    public async Task Pin_BadResourceId_ExitsOne(string resourceId)
    {
        using var sandbox = new CliSandbox(nameof(Pin_BadResourceId_ExitsOne));

        var result = await Pin(sandbox, resourceId, Obj1);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("is not a resource id of a known type", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pin_WithoutADeploymentNameOrHalfTheForm_ExitsOne()
    {
        using var sandbox = new CliSandbox(nameof(Pin_WithoutADeploymentNameOrHalfTheForm_ExitsOne));

        var noName = await CliRunner.RunAsync("state", "adopt", "--resource-id", Provider + "applications/a", "--object-id", Obj1, "--state-dir", sandbox.StateDir);
        var badName = await CliRunner.RunAsync("state", "adopt", "-n", "bad name", "--resource-id", Provider + "applications/a", "--object-id", Obj1, "--state-dir", sandbox.StateDir);
        var noObject = await CliRunner.RunAsync("state", "adopt", "-n", Dep, "--resource-id", Provider + "applications/a", "--state-dir", sandbox.StateDir);
        var noResource = await CliRunner.RunAsync("state", "adopt", "-n", Dep, "--object-id", Obj1, "--state-dir", sandbox.StateDir);

        Assert.Equal(1, noName.ExitCode);
        Assert.Contains("A deployment name is required", noName.StandardError, StringComparison.Ordinal);
        Assert.Equal(1, badName.ExitCode);
        Assert.Equal(1, noObject.ExitCode);
        Assert.Contains("Pinning needs both --resource-id and --object-id.", noObject.StandardError, StringComparison.Ordinal);
        Assert.Equal(1, noResource.ExitCode);
        Assert.False(Directory.Exists(sandbox.StateDir));
    }

    // ---- form selection ----

    [Fact]
    public async Task BothFormsOrNoForm_ExitOne()
    {
        using var sandbox = new CliSandbox(nameof(BothFormsOrNoForm_ExitOne));

        var both = await CliRunner.RunAsync("state", "adopt", "-n", Dep, "--resource-id", Provider + "applications/a", "--object-id", Obj1,
            "--type", "Microsoft.Entra/applications", "--id", Obj2, "--unique-name", "a", "-y", "--state-dir", sandbox.StateDir);
        var none = await CliRunner.RunAsync("state", "adopt", "-n", Dep, "--state-dir", sandbox.StateDir);
        var bare = await CliRunner.RunAsync("state", "adopt");

        Assert.Equal(1, both.ExitCode);
        Assert.Contains("Choose one form", both.StandardError, StringComparison.Ordinal);
        Assert.Equal(1, none.ExitCode);
        Assert.Contains("Nothing to adopt", none.StandardError, StringComparison.Ordinal);
        Assert.Equal(1, bare.ExitCode);
        Assert.Contains("Nothing to adopt", bare.StandardError, StringComparison.Ordinal);
        Assert.False(Directory.Exists(sandbox.StateDir));
    }

    [Fact]
    public async Task UniqueNameForm_MissingPieces_ExitOne_BeforeAnyRequest()
    {
        using var sandbox = new CliSandbox(nameof(UniqueNameForm_MissingPieces_ExitOne_BeforeAnyRequest));
        sandbox.UsePreflightOk();

        var noId = await CliRunner.RunAsync(["state", "adopt", "--type", "Microsoft.Entra/applications", "--unique-name", "orders-api", "-y", .. sandbox.StateArgs]);
        var noName = await CliRunner.RunAsync(["state", "adopt", "--type", "Microsoft.Entra/applications", "--id", Obj1, "-y", .. sandbox.StateArgs]);
        var noType = await CliRunner.RunAsync(["state", "adopt", "--id", Obj1, "--unique-name", "orders-api", "-y", .. sandbox.StateArgs]);

        Assert.All(new[] { noId, noName, noType }, r => Assert.Equal(1, r.ExitCode));
        Assert.All(new[] { noId, noName, noType }, r => Assert.Contains("Setting a uniqueName needs --type, --id and --unique-name.", r.StandardError, StringComparison.Ordinal));
        Assert.Empty(sandbox.RecordedCommands());
    }

    [Theory]
    [InlineData("Microsoft.Entra/users", "6f1b0000-0000-4000-8000-000000000001", "x", "has no uniqueName")]
    [InlineData("Microsoft.Entra/spaceships", "6f1b0000-0000-4000-8000-000000000001", "x", "has no uniqueName")]
    [InlineData("conditionalAccess/policies", "6f1b0000-0000-4000-8000-000000000001", "x", "has no uniqueName")]
    [InlineData("applications", "not-a-guid", "orders-api", "is not a GUID")]
    [InlineData("groups", "not-a-guid", "contoso-admins", "is not a GUID")]
    public async Task UniqueNameForm_BadTypeOrId_ExitsOne_BeforeAnyRequest(string type, string id, string name, string message)
    {
        using var sandbox = new CliSandbox(nameof(UniqueNameForm_BadTypeOrId_ExitsOne_BeforeAnyRequest));
        sandbox.UsePreflightOk();

        var result = await CliRunner.RunAsync(["state", "adopt", "--type", type, "--id", id, "--unique-name", name, "-y", .. sandbox.StateArgs]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(message, result.StandardError, StringComparison.Ordinal);
        Assert.Empty(sandbox.RecordedCommands());
    }

    // ---- uniqueName form ----

    private const string AppObjectId = "11110000-0000-4000-8000-000000000001";
    private const string AppClientId = "22220000-0000-4000-8000-000000000002";
    private const string GroupObjectId = "33330000-0000-4000-8000-000000000003";

    private static string GetApp(string urlPart, string? uniqueName)
    {
        string unique = uniqueName is null ? string.Empty : ", \"uniqueName\": \"" + uniqueName + "\"";
        return "{ \"match\": { \"method\": \"GET\", \"urlContains\": \"" + urlPart + "\" }, \"status\": 200, \"body\": { \"id\": \"" + AppObjectId + "\", \"appId\": \"" + AppClientId + "\", \"displayName\": \"Orders API\"" + unique + " } }";
    }

    private const string PatchOk = "{ \"match\": { \"method\": \"PATCH\" }, \"status\": 204 }";

    private static string GraphError(string method, string urlPart, int status, string code, string message)
    {
        return "{ \"match\": { \"method\": \"" + method + "\", \"urlContains\": \"" + urlPart + "\" }, \"status\": " + status + ", \"body\": { \"error\": { \"code\": \"" + code + "\", \"message\": \"" + message + "\" } } }";
    }
    private static Task<CliResult> SetUniqueName(CliSandbox sandbox, string type, string id, string name, params string[] extra)
    {
        string[] args = ["state", "adopt", "--type", type, "--id", id, "--unique-name", name, .. sandbox.StateArgs, .. extra];
        return CliRunner.RunAsync(args);
    }

    private static List<RecordedHttpRequest> Patches(CliSandbox sandbox)
    {
        return sandbox.RecordedRequests().Where(r => r.Method == "PATCH").ToList();
    }
    [Fact]
    public async Task UniqueName_WithoutASignIn_ExitsThree_AndReadsNothing()
    {
        using var sandbox = new CliSandbox(nameof(UniqueName_WithoutASignIn_ExitsThree_AndReadsNothing));
        sandbox.UseNotLoggedIn();

        var result = await SetUniqueName(sandbox, "Microsoft.Entra/applications", AppObjectId, "orders-api", "-y");

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("Preflight failed:", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(sandbox.RecordedCommands(), c => c.Contains("/applications", StringComparison.Ordinal));
        Assert.Empty(Patches(sandbox));
        Assert.False(Directory.Exists(sandbox.StateDir), "the uniqueName form never writes a state file");
    }

    [Fact]
    public async Task UniqueName_OnAnApplicationWithoutOne_PatchesTheBody()
    {
        using var sandbox = new CliSandbox(nameof(UniqueName_OnAnApplicationWithoutOne_PatchesTheBody));
        sandbox.UsePreflightOk(extraResponses: GetApp("/applications/" + AppObjectId, null) + "," + PatchOk);

        var result = await SetUniqueName(sandbox, "Microsoft.Entra/applications", AppObjectId, "orders-api", "-y");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        Assert.Contains("Set uniqueName 'orders-api' on application 'Orders API' (" + AppObjectId + ").", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("/providers/Microsoft.Entra/applications/orders-api", result.StandardOutput, StringComparison.Ordinal);
        var patch = Assert.Single(Patches(sandbox));
        Assert.Contains("/applications/" + AppObjectId, patch.Command, StringComparison.Ordinal);
        Assert.Equal("{\"uniqueName\":\"orders-api\"}", patch.Body!.Trim());
        Assert.Equal("orders-api", JsonNode.Parse(patch.Body!)!["uniqueName"]!.GetValue<string>());
        Assert.Single(JsonNode.Parse(patch.Body!)!.AsObject());
    }

    [Fact]
    public async Task UniqueName_ByApplicationClientId_FallsBackToTheAppIdAlternateKey()
    {
        using var sandbox = new CliSandbox(nameof(UniqueName_ByApplicationClientId_FallsBackToTheAppIdAlternateKey));
        string notFound = GraphError("GET", "/applications/" + AppClientId, 404, "Request_ResourceNotFound", "not found");
        sandbox.UsePreflightOk(extraResponses: notFound + "," + GetApp("appId='" + AppClientId + "'", null) + "," + PatchOk);

        var result = await SetUniqueName(sandbox, "applications", AppClientId, "orders-api", "-y", "-o", "json");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(AppObjectId, result.Json["objectId"]!.GetValue<string>());
        Assert.Equal("orders-api", result.Json["uniqueName"]!.GetValue<string>());
        Assert.Equal("Microsoft.Entra/applications", result.Json["type"]!.GetValue<string>());
        Assert.Equal("/providers/Microsoft.Entra/applications/orders-api", result.Json["resourceId"]!.GetValue<string>());
        Assert.Equal(CliSandbox.Tenant, result.Json["tenantId"]!.GetValue<string>());
        var patch = Assert.Single(Patches(sandbox));
        Assert.Contains("/applications/" + AppObjectId, patch.Command, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UniqueName_AlreadyTheSame_ExitsZero_WithoutAPatch()
    {
        using var sandbox = new CliSandbox(nameof(UniqueName_AlreadyTheSame_ExitsZero_WithoutAPatch));
        sandbox.UsePreflightOk(extraResponses: GetApp("/applications/" + AppObjectId, "orders-api"));

        var result = await SetUniqueName(sandbox, "Microsoft.Entra/applications", AppObjectId, "orders-api", "-y");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("already has uniqueName 'orders-api'; nothing to do.", result.StandardOutput, StringComparison.Ordinal);
        Assert.Empty(Patches(sandbox));
    }

    [Fact]
    public async Task UniqueName_DifferentExistingName_IsRefused_BecauseItIsImmutable()
    {
        using var sandbox = new CliSandbox(nameof(UniqueName_DifferentExistingName_IsRefused_BecauseItIsImmutable));
        sandbox.UsePreflightOk(extraResponses: GetApp("/applications/" + AppObjectId, "legacy-name"));

        var result = await SetUniqueName(sandbox, "Microsoft.Entra/applications", AppObjectId, "orders-api", "-y");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("already has uniqueName 'legacy-name', which is immutable; 'orders-api' cannot be set.", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("Use 'legacy-name' as the resource name in the template.", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(Patches(sandbox));
    }

    [Fact]
    public async Task UniqueName_ObjectNotFound_ExitsOne()
    {
        using var sandbox = new CliSandbox(nameof(UniqueName_ObjectNotFound_ExitsOne));
        string notFound = GraphError("GET", "/groups/", 404, "Request_ResourceNotFound", "not found");
        sandbox.UsePreflightOk(extraResponses: notFound);

        var result = await SetUniqueName(sandbox, "Microsoft.Entra/groups", GroupObjectId, "contoso-admins", "-y");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"No group with object id {GroupObjectId} in tenant {CliSandbox.Tenant}.", result.StandardError, StringComparison.Ordinal);
        Assert.Empty(Patches(sandbox));
    }

    [Fact]
    public async Task UniqueName_OnAGroup_PatchesTheGroupsCollection()
    {
        using var sandbox = new CliSandbox(nameof(UniqueName_OnAGroup_PatchesTheGroupsCollection));
        string group = "{ \"match\": { \"method\": \"GET\", \"urlContains\": \"/groups/" + GroupObjectId + "\" }, \"status\": 200, \"body\": { \"id\": \"" + GroupObjectId + "\", \"displayName\": \"Admins\" } }";
        sandbox.UsePreflightOk(extraResponses: group + "," + PatchOk);

        var result = await SetUniqueName(sandbox, "groups", GroupObjectId, "contoso-admins", "-y");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Set uniqueName 'contoso-admins' on group 'Admins' (" + GroupObjectId + ").", result.StandardOutput, StringComparison.Ordinal);
        var patch = Assert.Single(Patches(sandbox));
        Assert.Contains("/groups/" + GroupObjectId, patch.Command, StringComparison.Ordinal);
        Assert.Equal("contoso-admins", JsonNode.Parse(patch.Body!)!["uniqueName"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("Microsoft.Entra/applications", "Application.ReadWrite.All (Application.ReadWrite.OwnedBy for applications it owns)")]
    [InlineData("Microsoft.Entra/groups", "Group.ReadWrite.All")]
    public async Task UniqueName_Forbidden_NamesThePermissionToGrant(string type, string permissionHint)
    {
        using var sandbox = new CliSandbox(nameof(UniqueName_Forbidden_NamesThePermissionToGrant));
        bool isApp = type.EndsWith("applications", StringComparison.Ordinal);
        string objectId = isApp ? AppObjectId : GroupObjectId;
        string live = isApp ? GetApp("/applications/" + AppObjectId, null) : "{ \"match\": { \"method\": \"GET\", \"urlContains\": \"/groups/" + GroupObjectId + "\" }, \"status\": 200, \"body\": { \"id\": \"" + GroupObjectId + "\", \"displayName\": \"Admins\" } }";
        string forbidden = GraphError("PATCH", "", 403, "Authorization_RequestDenied", "Insufficient privileges to complete the operation.");
        sandbox.UsePreflightOk(extraResponses: live + "," + forbidden);

        var result = await SetUniqueName(sandbox, type, objectId, "orders-api", "-y");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Microsoft Graph refused the request:", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("Authorization_RequestDenied", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("The signed-in identity needs " + permissionHint, result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UniqueName_WithoutYes_AsksFirst_AndDeclineChangesNothing()
    {
        using var sandbox = new CliSandbox(nameof(UniqueName_WithoutYes_AsksFirst_AndDeclineChangesNothing));
        sandbox.UsePreflightOk(extraResponses: GetApp("/applications/" + AppObjectId, null) + "," + PatchOk);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var invocation = new StateInvocation
        {
            StateDir = sandbox.StateDir,
            Output = OutputFormat.Table,
            Connection = sandbox.Connection,
        };

        int exit = await AdoptCommand.SetUniqueNameAsync(invocation, new AdoptRequest { Type = "Microsoft.Entra/applications", Id = AppObjectId, UniqueName = "orders-api" }, stdout, stderr, () => "n", Ct);

        Assert.Equal(1, exit);
        Assert.Contains("uniqueName is immutable once set. (y/n): ", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("Nothing changed.", stderr.ToString(), StringComparison.Ordinal);
        Assert.Empty(Patches(sandbox));
    }
}
