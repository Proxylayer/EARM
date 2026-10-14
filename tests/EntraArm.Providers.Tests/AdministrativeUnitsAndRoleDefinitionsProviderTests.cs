using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EntraArm.Az;
using EntraArm.Core.Types;
using EntraArm.Engine;
using EntraArm.Engine.Execution;
using EntraArm.Engine.Planning;
using EntraArm.Engine.Providers;
using EntraArm.Providers.Tests.Infrastructure;
using Xunit;
using static EntraArm.Providers.Tests.Infrastructure.DirectoryTemplates;

namespace EntraArm.Providers.Tests;

/// <summary>The administrative units provider (WI-15, U1–U8) end to end.</summary>
public sealed class AdministrativeUnitsProviderTests
{
    private const string AuType = "Microsoft.Entra/administrativeUnits";

    private const string MembersProps = "\"description\": \"Scoped admin for Orders\", \"visibility\": \"HiddenMembership\", \"members\": [\"[reference('alice@contoso.com').id]\"]";

    [Fact]
    public async Task U1_Create_PostsTheUnit_AndAddsMembersThroughTheEdgePass()
    {
        var host = new ProviderHost();

        var run = await host.CreateAsync(host.Resources(Join(Alice(), Au(props: MembersProps))));

        Check.Succeeded(run);
        string aliceId = host.IdOf("/users", "userPrincipalName", "alice@contoso.com");
        string auId = host.IdOf(BuiltInRoles.UnitsPath, "displayName", "Orders AU");
        var auRequests = run.Requests.Where(r => r.Path.Contains("administrativeUnit", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Contains(auRequests, r => r.Method == "GET" && r.Path == BuiltInRoles.UnitsPath && r.UrlParameters!["$filter"] == "displayName eq 'Orders AU'");
        Assert.Contains(auRequests, r => r.Method == "GET" && r.Path == "/directory/deletedItems/microsoft.graph.administrativeUnit");
        var post = Assert.Single(auRequests, r => r.Method == "POST" && r.Path == BuiltInRoles.UnitsPath);
        var body = Check.Body(post);
        Assert.Equal(["description", "displayName", "visibility"], body.Select(p => p.Key).Order(StringComparer.Ordinal).ToList());
        Assert.Contains(auRequests, r => r.Method == "POST" && r.Path == $"{BuiltInRoles.UnitsPath}/{auId}/members/$ref");
        Assert.Contains(aliceId, host.Directory.References($"{BuiltInRoles.UnitsPath}/{auId}/members"));
        var entry = host.StateEntryJson(AuId)!;
        Assert.Equal(auId, entry["id"]!.GetValue<string>());
        Assert.Equal("Orders AU", entry["key"]!["displayName"]!.GetValue<string>());
        Assert.Contains("AdministrativeUnit.ReadWrite.All", run.Plan.RequiredPermissions);
    }

    [Fact]
    public async Task U2_Rerun_IsNoChange_ReadByPinnedId()
    {
        var host = new ProviderHost();
        var document = host.Resources(Join(Alice(), Au(props: MembersProps)));
        Check.Succeeded(await host.CreateAsync(document));
        string auId = host.IdOf(BuiltInRoles.UnitsPath, "displayName", "Orders AU");

        var rerun = await host.CreateAsync(document);

        Check.Succeeded(rerun);
        Check.NoWrites(rerun);
        Assert.Equal(ChangeType.NoChange, rerun.Op("Orders AU").ChangeType);
        Assert.Contains(rerun.Requests, r => r.Method == "GET" && r.Path == $"{BuiltInRoles.UnitsPath}/{auId}");
    }

    [Fact]
    public async Task U3_DescriptionDrift_IsOnePatch_AndDeclaringAssignedIsNotDrift()
    {
        var host = new ProviderHost();
        Check.Succeeded(await host.CreateAsync(host.Resources(Join(Alice(), Au(props: MembersProps)))));
        string auId = host.IdOf(BuiltInRoles.UnitsPath, "displayName", "Orders AU");
        await host.Fake.PatchAsync(new GraphRequest($"{BuiltInRoles.UnitsPath}/{auId}"), new JsonObject { ["description"] = "edited in the portal" }, ProviderHost.Ct);
        host.ClearRequests();

        var run = await host.CreateAsync(host.Resources(Join(Alice(), Au(props: "\"description\": \"Scoped admin for Orders\", \"membershipType\": \"Assigned\", \"members\": [\"[reference('alice@contoso.com').id]\"]"))));

        Check.Succeeded(run);
        var write = Assert.Single(run.Writes);
        Assert.Equal("PATCH", write.Method);
        Assert.Equal($"{BuiltInRoles.UnitsPath}/{auId}", write.Path);
        Assert.Equal("""{"description":"Scoped admin for Orders"}""", write.Body!.ToJsonString());
    }

    [Fact]
    public async Task U4_DynamicUnit_InjectsProcessingOn_AndRefusesManualMembers()
    {
        var host = new ProviderHost();
        const string rule = "\"membershipType\": \"Dynamic\", \"membershipRule\": \"(user.department -eq \\\"Orders\\\")\"";

        var run = await host.CreateAsync(host.Resources(Join(Alice(), Au(name: "Dyn AU", props: rule))));
        Check.Succeeded(run);
        var body = Check.Body(Assert.Single(run.Writes, w => w.Method == "POST" && w.Path == BuiltInRoles.UnitsPath));
        Assert.Equal("Dynamic", body["membershipType"]!.GetValue<string>());
        Assert.Equal("On", body["membershipRuleProcessingState"]!.GetValue<string>());

        var offline = await Assert.ThrowsAsync<ValidationFailedException>(() => host.CreateAsync(host.Resources(Join(Alice(), Au(name: "Dyn AU", props: rule + ", \"members\": [\"[reference('alice@contoso.com').id]\"]")))));
        Assert.Contains(offline.Diagnostics, d => d.Code == "DynamicMembershipConflict");

        // The unit is dynamic in the tenant but the template is silent about membershipType: the edge pass refuses.
        var live = await host.CreateAsync(host.Resources(Join(Alice(), Au(name: "Dyn AU", props: "\"description\": \"live dynamic\", \"members\": [\"[reference('alice@contoso.com').id]\"]"))));
        var op = live.OpOfType(AuType);
        Assert.False(live.Succeeded);
        Assert.Equal(OperationStatus.Failed, op.Status);
        Assert.Equal(EngineDiagnosticCodes.EdgeValidationFailed, op.ErrorCode);
        Assert.Contains("dynamic administrative unit", op.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(live.Writes, w => w.Path.Contains("/members/", StringComparison.Ordinal));

        var noRule = await new ProviderHost().CreateAsync(host.Resources(Au(name: "Dyn2", props: "\"membershipType\": \"Dynamic\"")));
        Assert.False(noRule.Succeeded);
        Assert.Contains("requires properties.membershipRule", noRule.OpOfType(AuType).Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task U5_ChangingIsMemberManagementRestricted_IsAnImmutableChange()
    {
        var host = new ProviderHost();
        Check.Succeeded(await host.CreateAsync(host.Resources(Au())));

        var run = await host.CreateAsync(host.Resources(Au(props: "\"description\": \"Scoped admin for Orders\", \"isMemberManagementRestricted\": true")));

        var op = run.OpOfType(AuType);
        Assert.False(run.Succeeded);
        Assert.Equal(OperationStatus.Failed, op.Status);
        Assert.Contains("Immutable property", op.Error, StringComparison.Ordinal);
        Check.NoWrites(run);
    }

    [Fact]
    public async Task U6_SoftDeletedUnit_IsAConflict_AndRestoreIsRestoreThenPatch()
    {
        var host = new ProviderHost();
        Check.Succeeded(await host.CreateAsync(host.Resources(Au())));
        string auId = host.IdOf(BuiltInRoles.UnitsPath, "displayName", "Orders AU");
        host.Directory.SoftDelete(auId);
        host.ClearRequests();

        var conflict = await host.CreateAsync(host.Resources(Au()));

        var op = conflict.OpOfType(AuType);
        Assert.False(conflict.Succeeded);
        Assert.Equal(ProviderException.SoftDeletedConflict, op.ErrorCode);
        Assert.Contains("--on-soft-deleted-conflict restore", op.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(conflict.Writes, w => w.Path.StartsWith(BuiltInRoles.UnitsPath, StringComparison.Ordinal));

        host.OnSoftDeletedConflict = SoftDeletedConflict.Restore;
        var restored = await host.CreateAsync(host.Resources(Au(props: "\"description\": \"restored\"")));

        Check.Succeeded(restored);
        Assert.Contains(restored.Writes, w => w.Method == "POST" && w.Path == $"/directory/deletedItems/{auId}/restore");
        Assert.Contains(restored.Writes, w => w.Method == "PATCH" && w.Path == $"{BuiltInRoles.UnitsPath}/{auId}");
        Assert.Equal(1, host.Directory.Count(BuiltInRoles.UnitsPath));
        Assert.Equal(auId, restored.Entry(AuId).GraphId);
    }

    [Fact]
    public async Task U7_CompleteMode_DeletesTheUnitById()
    {
        var host = new ProviderHost();
        Check.Succeeded(await host.CreateAsync(host.Resources(Join(Alice(), Au()))));
        string auId = host.IdOf(BuiltInRoles.UnitsPath, "displayName", "Orders AU");

        var run = await host.CreateAsync(host.Resources(Alice()), DeploymentMode.Complete);

        Check.Succeeded(run);
        var write = Assert.Single(run.Writes);
        Assert.Equal("DELETE", write.Method);
        Assert.Equal($"{BuiltInRoles.UnitsPath}/{auId}", write.Path);
    }

    [Fact]
    public async Task U8_TwoUnitsWithTheSameName_AreAmbiguous()
    {
        var host = new ProviderHost();
        host.Directory.Add(BuiltInRoles.UnitsPath, new JsonObject { ["displayName"] = "Twins" });
        host.Directory.Add(BuiltInRoles.UnitsPath, new JsonObject { ["displayName"] = "Twins" });

        var run = await host.CreateAsync(host.Resources(Au(name: "Twins")));

        var op = Assert.Single(run.Executed.Operations);
        Assert.Equal(ProviderException.AmbiguousMatch, op.ErrorCode);
        Assert.Contains("entraarm state adopt", op.Error, StringComparison.Ordinal);
        Check.NoWrites(run);
    }

    [Fact]
    public async Task WhatIf_RestrictedUnitWithMembers_ExplainsTheOrdering_AndWritesNothing()
    {
        var host = new ProviderHost();

        var run = await host.WhatIfAsync(host.Resources(Join(Alice(), Au(props: "\"description\": \"x\", \"isMemberManagementRestricted\": true, \"members\": [\"[reference('alice@contoso.com').id]\"]"))));

        Check.NoWrites(run);
        Assert.Contains(run.Change("Orders AU").Reasons, r => r.Contains("restricts member management", StringComparison.Ordinal));
    }

    [Fact]
    public void Discovery_RegistersTheProvider()
    {
        Assert.IsType<AdministrativeUnitsProvider>(ProviderDiscovery.CreateRegistry(ResourceTypeCatalog.Default).Get(AuType));
    }
}

/// <summary>The custom role definitions provider (WI-15, R1–R6) end to end.</summary>
public sealed class RoleDefinitionsProviderTests
{
    private const string RoleType = "Microsoft.Entra/roleDefinitions";

    [Fact]
    public async Task R1_Create_PostsSortedActions_WithTheConstantRootScope()
    {
        var host = new ProviderHost();
        BuiltInRoles.Seed(host);

        var run = await host.CreateAsync(host.Resources(RoleDef()));

        Check.Succeeded(run);
        var requests = run.Requests.Where(r => r.Path.Contains("roleDefinitions", StringComparison.Ordinal)).ToList();
        Assert.Contains(requests, r => r.Method == "GET" && r.Path == BuiltInRoles.DefinitionsPath && r.UrlParameters!["$filter"] == "displayName eq 'Orders Support'");
        var body = Check.Body(Assert.Single(requests, r => r.Method == "POST"));
        Assert.Equal("Orders Support", body["displayName"]!.GetValue<string>());
        Assert.True(body["isEnabled"]!.GetValue<bool>());
        Assert.Equal("""["/"]""", body["resourceScopes"]!.ToJsonString());
        Assert.Equal("""[{"allowedResourceActions":["microsoft.directory/users/password/update","microsoft.directory/users/standard/read"]}]""", body["rolePermissions"]!.ToJsonString());
        string roleId = host.IdOf(BuiltInRoles.DefinitionsPath, "displayName", "Orders Support");
        Assert.Equal(roleId, host.StateEntryJson(RoleDefId)!["id"]!.GetValue<string>());
        Assert.Contains("RoleManagement.ReadWrite.Directory", run.Plan.RequiredPermissions);
    }

    [Fact]
    public async Task R2_ReorderedActions_AreNoChange_ReadByPinnedId()
    {
        var host = new ProviderHost();
        Check.Succeeded(await host.CreateAsync(host.Resources(RoleDef())));
        string roleId = host.IdOf(BuiltInRoles.DefinitionsPath, "displayName", "Orders Support");

        var rerun = await host.CreateAsync(host.Resources(RoleDef(actions: "\"microsoft.directory/users/standard/read\", \"microsoft.directory/users/password/update\"")));

        Check.Succeeded(rerun);
        Check.NoWrites(rerun);
        Assert.Equal(ChangeType.NoChange, rerun.Op("Orders Support").ChangeType);
        Assert.Contains(rerun.Requests, r => r.Method == "GET" && r.Path == $"{BuiltInRoles.DefinitionsPath}/{roleId}");
    }

    [Fact]
    public async Task R3_ChangedActions_PatchTheWholeRolePermissions_AndIsEnabled()
    {
        var host = new ProviderHost();
        Check.Succeeded(await host.CreateAsync(host.Resources(RoleDef())));
        string roleId = host.IdOf(BuiltInRoles.DefinitionsPath, "displayName", "Orders Support");

        var run = await host.CreateAsync(host.Resources(RoleDef(actions: "\"microsoft.directory/users/standard/read\"", extra: ", \"isEnabled\": false")));

        Check.Succeeded(run);
        var write = Assert.Single(run.Writes);
        Assert.Equal("PATCH", write.Method);
        Assert.Equal($"{BuiltInRoles.DefinitionsPath}/{roleId}", write.Path);
        var body = Check.Body(write);
        Assert.Equal(["isEnabled", "rolePermissions"], body.Select(p => p.Key).Order(StringComparer.Ordinal).ToList());
        Assert.Single(body["rolePermissions"]![0]!["allowedResourceActions"]!.AsArray());
        Assert.False(body["isEnabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task R4_ABuiltInName_IsRoleDefinitionBuiltIn_WithTheRoleTemplateHint()
    {
        var host = new ProviderHost();
        BuiltInRoles.Seed(host);

        var run = await host.CreateAsync(host.Resources(RoleDef(name: "Global Reader")));

        var op = Assert.Single(run.Executed.Operations);
        Assert.False(run.Succeeded);
        Assert.Equal(RoleDefinitionsProvider.BuiltInRole, op.ErrorCode);
        Assert.Contains("roleTemplate('Global Reader')", op.Error, StringComparison.Ordinal);
        Check.NoWrites(run);
    }

    [Fact]
    public async Task R5_DeclaringAServerManagedProperty_IsUnsupported()
    {
        var host = new ProviderHost();

        var run = await host.CreateAsync(host.Resources(RoleDef(extra: ", \"templateId\": \"11111111-1111-1111-1111-111111111111\"")));

        Assert.False(run.Succeeded);
        Assert.Contains("properties.templateId is assigned by Graph", run.OpOfType(RoleType).Error, StringComparison.Ordinal);
        Check.NoWrites(run);
    }

    [Fact]
    public async Task R6_DeletingAssignedRole_IsRoleDefinitionInUse_ThenSucceedsOnceUnassigned()
    {
        var host = new ProviderHost();
        Check.Succeeded(await host.CreateAsync(host.Resources(RoleDef())));
        string roleId = host.IdOf(BuiltInRoles.DefinitionsPath, "displayName", "Orders Support");
        string bobId = host.Directory.AddUser("bob@contoso.com", "Bob")["id"]!.GetValue<string>();
        var assignment = host.Directory.Add(BuiltInRoles.AssignmentsPath, new JsonObject { ["roleDefinitionId"] = roleId, ["principalId"] = bobId, ["directoryScopeId"] = "/" });

        var blocked = await host.CreateAsync(host.Resources(string.Empty), DeploymentMode.Complete);

        var op = blocked.Op("Orders Support");
        Assert.False(blocked.Succeeded);
        Assert.Equal(RoleDefinitionsProvider.RoleDefinitionInUse, op.ErrorCode);
        Assert.Contains("directoryRoles", op.Error, StringComparison.Ordinal);

        await host.Fake.DeleteAsync(new GraphRequest($"{BuiltInRoles.AssignmentsPath}/{assignment["id"]!.GetValue<string>()}"), ProviderHost.Ct);
        var run = await host.CreateAsync(host.Resources(string.Empty), DeploymentMode.Complete);

        Check.Succeeded(run);
        Assert.Contains(run.Writes, w => w.Method == "DELETE" && w.Path == $"{BuiltInRoles.DefinitionsPath}/{roleId}");
        Assert.Null(host.Directory.Find(BuiltInRoles.DefinitionsPath, "displayName", "Orders Support"));
    }

    [Fact]
    public async Task EmptyRolePermissions_AreNotCreated()
    {
        var host = new ProviderHost();
        const string empty = "{ \"type\": \"Microsoft.Entra/roleDefinitions\", \"apiVersion\": \"2026-10-01\", \"name\": \"Empty Role\", \"properties\": { \"description\": \"x\", \"rolePermissions\": [] } }";

        var whatIf = await host.WhatIfAsync(host.Resources(empty));
        var run = await host.CreateAsync(host.Resources(empty));

        Assert.True(whatIf.Plan.Changes.Single().HasError, "what-if row should be unsupported: " + string.Join(" | ", whatIf.Plan.Changes.Single().Reasons));
        Assert.False(run.Succeeded);
        Check.NoWrites(run);
    }

    [Fact]
    public async Task TwoCustomRolesWithTheSameName_AreAmbiguous()
    {
        var host = new ProviderHost();
        host.Directory.Add(BuiltInRoles.DefinitionsPath, new JsonObject { ["displayName"] = "Orders Support", ["isBuiltIn"] = false, ["rolePermissions"] = new JsonArray() });
        host.Directory.Add(BuiltInRoles.DefinitionsPath, new JsonObject { ["displayName"] = "Orders Support", ["isBuiltIn"] = false, ["rolePermissions"] = new JsonArray() });

        var run = await host.CreateAsync(host.Resources(RoleDef()));

        Assert.Equal(ProviderException.AmbiguousMatch, Assert.Single(run.Executed.Operations).ErrorCode);
        Check.NoWrites(run);
    }

    [Fact]
    public void Discovery_RegistersTheProvider()
    {
        Assert.IsType<RoleDefinitionsProvider>(ProviderDiscovery.CreateRegistry(ResourceTypeCatalog.Default).Get(RoleType));
    }
}
