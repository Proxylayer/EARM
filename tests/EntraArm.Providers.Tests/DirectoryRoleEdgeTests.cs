using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EntraArm.Az;
using EntraArm.Engine;
using EntraArm.Engine.Execution;
using EntraArm.Engine.Planning;
using EntraArm.Engine.Providers;
using EntraArm.Providers.Tests.Infrastructure;
using Xunit;
using static EntraArm.Providers.Tests.Infrastructure.DirectoryTemplates;

namespace EntraArm.Providers.Tests;

/// <summary>The <c>directoryRoles</c> edge kind (WI-15, E1–E8) and the delete order of the new types.</summary>
public sealed class DirectoryRoleEdgeTests
{
    private const string GlobalReaderRole = "{ \"role\": \"Global Reader\" }";

    private static ProviderHost NewHost()
    {
        var host = new ProviderHost();
        BuiltInRoles.Seed(host);
        return host;
    }

    private static JsonArray? Records(ProviderHost host, string resourceId)
    {
        return host.StateEntryJson(resourceId)?["relationships"]?["directoryRoles"] as JsonArray;
    }

    [Fact]
    public async Task DirectoryRoles_IsAPartBEdgeKind_NoLongerEdgeKindNotSupported()
    {
        var host = NewHost();
        Assert.True(host.EdgeKinds.TryGet(EntraArm.Engine.Edges.EdgeKind.DirectoryRole, out var handler));
        Assert.Equal(EntraArm.Engine.Edges.EdgePhase.B, handler.Phase);

        var document = host.Resources(Group(DirectoryRoles(GlobalReaderRole)));
        var whatIf = await host.WhatIfAsync(document);
        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        Assert.DoesNotContain(whatIf.Plan.Diagnostics.Concat(run.Executed.Diagnostics), d => d.Code == EngineDiagnosticCodes.EdgeKindNotSupported);
    }

    [Fact]
    public async Task E1_AGroup_GetsOneListAndOnePost_AndTheRecordCarriesTheAssignmentId()
    {
        var host = NewHost();

        var run = await host.CreateAsync(host.Resources(Group(DirectoryRoles(GlobalReaderRole))));

        Check.Succeeded(run);
        string groupId = host.IdOf("/groups", "uniqueName", "test-admins");
        Assert.Contains(run.Requests, r => r.Method == "GET" && r.Path == BuiltInRoles.AssignmentsPath && r.UrlParameters!["$filter"] == $"principalId eq '{groupId}'");
        var post = Assert.Single(run.Writes, w => w.Method == "POST" && w.Path == BuiltInRoles.AssignmentsPath);
        Assert.Equal(RetryHint.ExpectReplicationLag, post.Hint);
        Assert.Equal("{\"@odata.type\":\"#microsoft.graph.unifiedRoleAssignment\",\"roleDefinitionId\":\"" + BuiltInRoles.GlobalReader + "\",\"principalId\":\"" + groupId + "\",\"directoryScopeId\":\"/\"}", post.Body!.ToJsonString());
        Assert.Single(run.Writes, w => w.Path.Contains("roleAssignments", StringComparison.Ordinal));
        var record = Assert.IsType<JsonObject>(Assert.Single(Records(host, GroupId)!));
        Assert.Equal(BuiltInRoles.GlobalReader, record["roleDefinitionId"]!.GetValue<string>());
        Assert.Equal(groupId, record["principalId"]!.GetValue<string>());
        Assert.Equal("/", record["directoryScopeId"]!.GetValue<string>());
        Assert.Equal(host.Directory.Objects(BuiltInRoles.AssignmentsPath).Single()["id"]!.GetValue<string>(), record["id"]!.GetValue<string>());
        var delta = Assert.Single(run.Change("test-admins").Deltas, d => d.Path.ToString() == "properties.directoryRoles");
        Assert.Equal(PropertyChangeType.Array, delta.Kind);
        Assert.Single(delta.Children!);
    }

    [Fact]
    public async Task E2_Rerun_WritesNothing_AndKeepsTheRecord()
    {
        var host = NewHost();
        var document = host.Resources(Group(DirectoryRoles(GlobalReaderRole)));
        Check.Succeeded(await host.CreateAsync(document));

        var rerun = await host.CreateAsync(document);

        Check.Succeeded(rerun);
        Check.NoWrites(rerun);
        Assert.Single(Records(host, GroupId)!);
    }

    [Fact]
    public async Task E3_TheSameAssignmentDeclaredFromBothSides_IsOnePost_AndTheStandaloneResourceOwnsIt()
    {
        var host = NewHost();
        string standalone = Standalone("\"principalId\": \"[reference('test-admins').id]\", \"roleDefinitionId\": \"[roleTemplate('Global Reader')]\"", "[guid('group', 'gr')]");

        var run = await host.CreateAsync(host.Resources(Join(Group(DirectoryRoles("{ \"role\": \"Global Reader\", \"directoryScopeId\": \"/\" }")), standalone)));

        Check.Succeeded(run);
        Assert.Single(run.Writes, w => w.Path.Contains("roleAssignments", StringComparison.Ordinal));
        Assert.Single(host.Directory.Objects(BuiltInRoles.AssignmentsPath));
        Assert.Null(Records(host, GroupId));

        var removed = await host.CreateAsync(host.Resources(Join(Group(), standalone)));
        Check.Succeeded(removed);
        Check.NoWrites(removed);
        Assert.Single(host.Directory.Objects(BuiltInRoles.AssignmentsPath));
    }

    [Fact]
    public async Task E4_UndeclaringTheItem_DeletesTheTrackedAssignmentById()
    {
        var host = NewHost();
        Check.Succeeded(await host.CreateAsync(host.Resources(Group(DirectoryRoles(GlobalReaderRole)))));
        string recordedId = Records(host, GroupId)![0]!["id"]!.GetValue<string>();

        var run = await host.CreateAsync(host.Resources(Group()));

        Check.Succeeded(run);
        var write = Assert.Single(run.Writes);
        Assert.Equal("DELETE", write.Method);
        Assert.Equal($"{BuiltInRoles.AssignmentsPath}/{recordedId}", write.Path);
        Assert.Null(Records(host, GroupId));
        Assert.Empty(host.Directory.Objects(BuiltInRoles.AssignmentsPath));
    }

    [Fact]
    public async Task E5_CompleteMode_RemovesTheRecordedRoleEdgeBeforeDeletingTheGroup()
    {
        var host = NewHost();
        Check.Succeeded(await host.CreateAsync(host.Resources(Group(DirectoryRoles(GlobalReaderRole)))));
        string groupId = host.IdOf("/groups", "uniqueName", "test-admins");

        var run = await host.CreateAsync(host.Resources(string.Empty), DeploymentMode.Complete);

        Check.Succeeded(run);
        var writes = run.Writes.ToList();
        int edgeDelete = writes.FindIndex(w => w.Method == "DELETE" && w.Path.StartsWith(BuiltInRoles.AssignmentsPath + "/", StringComparison.Ordinal));
        int groupDelete = writes.FindIndex(w => w.Method == "DELETE" && w.Path == $"/groups/{groupId}");
        Assert.True(edgeDelete >= 0 && groupDelete >= 0, "both deletes were issued");
        Assert.True(edgeDelete < groupDelete, "the role edge goes before the group");
    }

    [Fact]
    public async Task E6_ANonAssignableGroup_IsRejectedOffline_ForALiteral_AndEdgeValidationFailed_ForAnExpression()
    {
        var host = NewHost();
        var literal = Group(DirectoryRoles(GlobalReaderRole)).Replace("\"isAssignableToRole\": true", "\"isAssignableToRole\": false", StringComparison.Ordinal);

        var offline = await Assert.ThrowsAsync<ValidationFailedException>(() => host.CreateAsync(host.Resources(literal)));
        Assert.Contains(offline.Diagnostics, d => d.Code == "DirectoryRolesRequireRoleAssignableGroup");

        var run = await host.CreateAsync(host.Resources(Group(DirectoryRoles(GlobalReaderRole), assignable: false)));

        var op = run.OpOfType("Microsoft.Entra/groups");
        Assert.False(run.Succeeded);
        Assert.Equal(OperationStatus.Failed, op.Status);
        Assert.Equal(EngineDiagnosticCodes.EdgeValidationFailed, op.ErrorCode);
        Assert.Contains("role-assignable", op.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(run.Writes, w => w.Path.Contains("roleAssignments", StringComparison.Ordinal));
    }

    [Fact]
    public async Task E7_TwoUnitScopedRolesOnAUser_AreRecordedWithIds_AndUndeclaringOneRemovesExactlyThat()
    {
        var host = NewHost();
        string both = Join(Au(), RoleDef(), Alice(DirectoryRoles(
            "{ \"role\": \"Orders Support\", \"administrativeUnit\": \"Orders AU\" }",
            "{ \"role\": \"[roleTemplate('User Administrator')]\", \"directoryScopeId\": \"[format('/administrativeUnits/{0}', reference('Orders AU').id)]\" }")));

        var run = await host.CreateAsync(host.Resources(both));

        Check.Succeeded(run);
        string aliceId = host.IdOf("/users", "userPrincipalName", "alice@contoso.com");
        string auId = host.IdOf(BuiltInRoles.UnitsPath, "displayName", "Orders AU");
        string customRoleId = host.IdOf(BuiltInRoles.DefinitionsPath, "displayName", "Orders Support");
        var posts = run.Writes.Where(w => w.Method == "POST" && w.Path == BuiltInRoles.AssignmentsPath).ToList();
        Assert.Equal(2, posts.Count);
        Assert.Contains(posts, p => Check.Body(p)["roleDefinitionId"]!.GetValue<string>() == customRoleId && Check.Body(p)["directoryScopeId"]!.GetValue<string>() == $"/administrativeUnits/{auId}" && p.Hint == RetryHint.ExpectReplicationLag);
        Assert.Contains(posts, p => Check.Body(p)["roleDefinitionId"]!.GetValue<string>() == BuiltInRoles.UserAdministrator && Check.Body(p)["directoryScopeId"]!.GetValue<string>() == $"/administrativeUnits/{auId}");
        var records = Records(host, AliceId)!;
        Assert.Equal(2, records.Count);
        Assert.All(records, r => Assert.NotNull(r!["id"]));
        Assert.All(records, r => Assert.Equal($"/administrativeUnits/{auId}", r!["directoryScopeId"]!.GetValue<string>()));

        var rerun = await host.CreateAsync(host.Resources(both));
        Check.Succeeded(rerun);
        Check.NoWrites(rerun);

        var onlyCustom = await host.CreateAsync(host.Resources(Join(Au(), RoleDef(), Alice(DirectoryRoles("{ \"role\": \"Orders Support\", \"administrativeUnit\": \"Orders AU\" }")))));
        Check.Succeeded(onlyCustom);
        var delete = Assert.Single(onlyCustom.Writes);
        Assert.Equal("DELETE", delete.Method);
        var remaining = Assert.Single(host.Directory.Objects(BuiltInRoles.AssignmentsPath), a => string.Equals(a["principalId"]!.GetValue<string>(), aliceId, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(customRoleId, remaining["roleDefinitionId"]!.GetValue<string>());
    }

    [Fact]
    public async Task E8_TheSameRoleByNameAndById_OnAServicePrincipal_IsOnePost()
    {
        var host = NewHost();
        const string app = """{ "type": "Microsoft.Entra/applications", "apiVersion": "2026-10-01", "name": "test-api", "properties": { "displayName": "Test API" } }""";
        string sp = "{ \"type\": \"Microsoft.Entra/servicePrincipals\", \"apiVersion\": \"2026-10-01\", \"name\": \"[reference('test-api').appId]\", \"properties\": { "
            + DirectoryRoles("{ \"role\": \"Directory Readers\" }", "{ \"role\": \"" + BuiltInRoles.DirectoryReaders + "\", \"directoryScopeId\": \"/\" }")
            + " }, \"dependsOn\": [\"[resourceId('Microsoft.Entra/applications', 'test-api')]\"] }";

        var run = await host.CreateAsync(host.Resources(Join(app, sp)));

        Check.Succeeded(run);
        Assert.Single(run.Writes, w => w.Path.Contains("roleAssignments", StringComparison.Ordinal));
        Assert.Single(host.Directory.Objects(BuiltInRoles.AssignmentsPath));
    }

    [Fact]
    public async Task DeleteAll_RemovesAssignmentsThenUnitsThenRoleDefinitions()
    {
        var host = NewHost();
        var deployed = await host.CreateAsync(host.Resources(Join(Alice(), Au(), RoleDef(), Standalone(AliceRef + ", \"role\": \"Orders Support\", \"administrativeUnit\": \"Orders AU\"", "[guid('alice', 'scoped')]", DependsOnRole() + ", " + DependsOnUnit()))));
        Check.Succeeded(deployed);
        Assert.Single(host.Directory.Objects(BuiltInRoles.AssignmentsPath));
        host.ClearRequests();

        var deletion = await host.DeleteAllAsync();

        Assert.Equal(0, deletion.FailedCount);
        Assert.Equal(4, deletion.DeletedCount);
        var deletes = host.Fake.Writes.Where(w => w.Method == "DELETE").Select(w => w.Path).ToList();
        int assignment = deletes.FindIndex(p => p.StartsWith(BuiltInRoles.AssignmentsPath + "/", StringComparison.Ordinal));
        int unit = deletes.FindIndex(p => p.StartsWith(BuiltInRoles.UnitsPath + "/", StringComparison.Ordinal));
        int role = deletes.FindIndex(p => p.StartsWith(BuiltInRoles.DefinitionsPath + "/", StringComparison.Ordinal));
        Assert.True(assignment >= 0 && unit > assignment && role > unit, "order: roleAssignments, administrativeUnits, roleDefinitions; got " + string.Join(" | ", deletes));
        Assert.Equal(5, host.Directory.Count(BuiltInRoles.DefinitionsPath));
        Assert.Equal(0, host.Directory.Count(BuiltInRoles.UnitsPath));
        Assert.Empty(host.Directory.Objects(BuiltInRoles.AssignmentsPath));
    }
}
