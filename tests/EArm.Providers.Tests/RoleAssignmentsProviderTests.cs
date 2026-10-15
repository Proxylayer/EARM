using System;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Az;
using EArm.Core.Types;
using EArm.Engine.Execution;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Providers.Tests.Infrastructure;
using Xunit;
using static EArm.Providers.Tests.Infrastructure.DirectoryTemplates;

namespace EArm.Providers.Tests;

/// <summary>The standalone <c>Microsoft.Entra/roleAssignments</c> provider (WI-15, S1–S10) and its what-if rows.</summary>
public sealed class RoleAssignmentsProviderTests
{
    private const string AssignmentType = "Microsoft.Entra/roleAssignments";

    private static ProviderHost NewHost()
    {
        var host = new ProviderHost();
        BuiltInRoles.Seed(host);
        return host;
    }

    private static string AssignmentId(Run run)
    {
        return run.Plan.Instances.First(i => i.Type == AssignmentType).Id.Id;
    }

    [Fact]
    public void Discovery_RegistersTheProvider()
    {
        Assert.IsType<RoleAssignmentsProvider>(ProviderDiscovery.CreateRegistry(ResourceTypeCatalog.Default).Get(AssignmentType));
    }

    [Fact]
    public async Task S1_GlobalReader_ByName_ListsThenPosts_UnderReplicationLag()
    {
        var host = NewHost();

        var run = await host.CreateAsync(host.Resources(Join(Alice(), Standalone(AliceRef + ", \"role\": \"Global Reader\""))));

        Check.Succeeded(run);
        string aliceId = host.IdOf("/users", "userPrincipalName", "alice@contoso.com");
        var requests = run.Requests.Where(r => r.Path.Contains("roleAssignments", StringComparison.Ordinal) || r.Path.Contains("directoryObjects", StringComparison.Ordinal) || r.Path.Contains("roleDefinitions", StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain(requests, r => r.Path.Contains("roleDefinitions", StringComparison.Ordinal));
        Assert.Contains(requests, r => r.Method == "GET" && r.Path == $"/directoryObjects/{aliceId}");
        Assert.Contains(requests, r => r.Method == "GET" && r.Path == BuiltInRoles.AssignmentsPath && r.UrlParameters!["$filter"] == $"principalId eq '{aliceId}'");
        var post = Assert.Single(requests, r => r.Method == "POST");
        Assert.Equal(BuiltInRoles.AssignmentsPath, post.Path);
        Assert.Equal(RetryHint.ExpectReplicationLag, post.Hint);
        Assert.Equal("{\"@odata.type\":\"#microsoft.graph.unifiedRoleAssignment\",\"roleDefinitionId\":\"" + BuiltInRoles.GlobalReader + "\",\"principalId\":\"" + aliceId + "\",\"directoryScopeId\":\"/\"}", post.Body!.ToJsonString());
        var entry = host.StateEntryJson(AssignmentId(run))!;
        var assignment = Assert.Single(host.Directory.Objects(BuiltInRoles.AssignmentsPath));
        Assert.Equal(assignment["id"]!.GetValue<string>(), entry["id"]!.GetValue<string>());
        Assert.Equal(BuiltInRoles.GlobalReader, entry["key"]!["roleDefinitionId"]!.GetValue<string>());
        Assert.Equal(aliceId, entry["key"]!["principalId"]!.GetValue<string>());
        Assert.Equal("/", entry["key"]!["directoryScopeId"]!.GetValue<string>());
        Assert.Equal(BuiltInRoles.GlobalReader, entry["extra"]!["roleDefinitionId"]!.GetValue<string>());
        Assert.Contains("RoleManagement.ReadWrite.Directory", run.Plan.RequiredPermissions);
    }

    [Fact]
    public async Task S2_Rerun_IsNoChange_ReadByPinnedId_AndTheCanonicalFormAdoptsTheLiveAssignment()
    {
        var host = NewHost();
        var template = Join(Alice(), Standalone(AliceRef + ", \"role\": \"Global Reader\""));
        var first = await host.CreateAsync(host.Resources(template));
        Check.Succeeded(first);
        string id = host.Directory.Objects(BuiltInRoles.AssignmentsPath).Single()["id"]!.GetValue<string>();
        string standaloneId = AssignmentId(first);

        var rerun = await host.CreateAsync(host.Resources(template));
        Check.Succeeded(rerun);
        Check.NoWrites(rerun);
        Assert.Equal(ChangeType.NoChange, rerun.Op(standaloneId).ChangeType);
        Assert.Contains(rerun.Requests, r => r.Method == "GET" && r.Path == $"{BuiltInRoles.AssignmentsPath}/{id}");

        var canonical = await host.CreateAsync(host.Resources(Join(Alice(), Standalone(AliceRef + ", \"roleDefinitionId\": \"[roleTemplate('Global Reader')]\", \"directoryScopeId\": \"/\"", name: "[guid('alice', 'canonical')]"))));
        Check.Succeeded(canonical);
        Check.NoWrites(canonical);
        Assert.Equal(ChangeType.NoChange, canonical.Op(AssignmentId(canonical)).ChangeType);
        Assert.Single(host.Directory.Objects(BuiltInRoles.AssignmentsPath));
    }

    [Fact]
    public async Task S3_AdministrativeUnitSugar_ResolvesTheUnitScope_AndTheCanonicalScopeIsNoChange()
    {
        var host = NewHost();
        string name = "[guid('alice', 'ua', 'orders-au')]";
        var sugar = Join(Alice(), Au(), Standalone(AliceRef + ", \"role\": \"User Administrator\", \"administrativeUnit\": \"Orders AU\"", name, DependsOnUnit()));

        var run = await host.CreateAsync(host.Resources(sugar));

        Check.Succeeded(run);
        string auId = host.IdOf(BuiltInRoles.UnitsPath, "displayName", "Orders AU");
        Assert.Contains(run.Requests, r => r.Method == "GET" && r.Path == BuiltInRoles.UnitsPath && r.UrlParameters!["$filter"] == "displayName eq 'Orders AU'");
        var post = Assert.Single(run.Writes, w => w.Method == "POST" && w.Path == BuiltInRoles.AssignmentsPath);
        Assert.Equal(BuiltInRoles.UserAdministrator, Check.Body(post)["roleDefinitionId"]!.GetValue<string>());
        Assert.Equal($"/administrativeUnits/{auId}", Check.Body(post)["directoryScopeId"]!.GetValue<string>());
        Assert.Equal($"/administrativeUnits/{auId}", host.StateEntryJson(AssignmentId(run))!["key"]!["directoryScopeId"]!.GetValue<string>());

        var canonical = await host.CreateAsync(host.Resources(Join(Alice(), Au(), Standalone(AliceRef + ", \"roleDefinitionId\": \"[roleTemplate('User Administrator')]\", \"directoryScopeId\": \"[format('/administrativeUnits/{0}', reference('Orders AU').id)]\"", name))));
        Check.Succeeded(canonical);
        Check.NoWrites(canonical);
        Assert.Equal(ChangeType.NoChange, canonical.Op(AssignmentId(canonical)).ChangeType);
    }

    [Fact]
    public async Task S4_CustomRole_ByName_ByReference_AndByExistingLookup_ResolveToTheSameAssignment()
    {
        var host = NewHost();
        string name = "[guid('alice', 'orders-support')]";

        var run = await host.CreateAsync(host.Resources(Join(Alice(), RoleDef(), Standalone(AliceRef + ", \"role\": \"Orders Support\"", name, DependsOnRole()))));

        Check.Succeeded(run);
        string customRoleId = host.IdOf(BuiltInRoles.DefinitionsPath, "displayName", "Orders Support");
        Assert.Contains(run.Requests, r => r.Method == "GET" && r.Path == BuiltInRoles.DefinitionsPath && r.UrlParameters!["$filter"] == "displayName eq 'Orders Support'");
        var post = Assert.Single(run.Writes, w => w.Method == "POST" && w.Path == BuiltInRoles.AssignmentsPath);
        Assert.Equal(customRoleId, Check.Body(post)["roleDefinitionId"]!.GetValue<string>());
        Assert.Equal(RetryHint.ExpectReplicationLag, post.Hint);

        var byReference = await host.CreateAsync(host.Resources(Join(Alice(), RoleDef(), Standalone(AliceRef + ", \"roleDefinitionId\": \"[reference('Orders Support').id]\"", name))));
        Check.Succeeded(byReference);
        Check.NoWrites(byReference);

        var byLookup = await host.CreateAsync(host.Resources(Join(Alice(), RoleDef(), Standalone(AliceRef + ", \"roleDefinitionId\": \"[reference(resourceId('Microsoft.Entra/roleDefinitions', 'Orders Support'), '2026-10-01-preview').id]\"", name))));
        Check.Succeeded(byLookup);
        Check.NoWrites(byLookup);
        Assert.Equal(ChangeType.NoChange, byLookup.Op(AssignmentId(byLookup)).ChangeType);
    }

    [Fact]
    public async Task S5_ACompositeChangeUnderTheSameName_IsDeleteThenPost()
    {
        var host = NewHost();
        var first = await host.CreateAsync(host.Resources(Join(Alice(), Standalone(AliceRef + ", \"role\": \"Global Reader\""))));
        Check.Succeeded(first);
        string oldId = host.Directory.Objects(BuiltInRoles.AssignmentsPath).Single()["id"]!.GetValue<string>();
        string standaloneId = AssignmentId(first);

        var run = await host.CreateAsync(host.Resources(Join(Alice(), Standalone(AliceRef + ", \"role\": \"Security Reader\""))));

        Check.Succeeded(run);
        var op = run.Op(standaloneId);
        Assert.Equal(ChangeType.Modify, op.ChangeType);
        Assert.Equal(ChangeMode.Recreate, op.Mode);
        var writes = run.Writes;
        Assert.Equal(2, writes.Count);
        Assert.Equal("DELETE", writes[0].Method);
        Assert.Equal($"{BuiltInRoles.AssignmentsPath}/{oldId}", writes[0].Path);
        Assert.Equal("POST", writes[1].Method);
        Assert.Equal(BuiltInRoles.SecurityReader, Check.Body(writes[1])["roleDefinitionId"]!.GetValue<string>());
        Assert.NotEqual(oldId, run.Entry(standaloneId).GraphId);
    }

    [Fact]
    public async Task S6_ADuplicate400OnCreate_AdoptsTheExistingAssignment()
    {
        var host = NewHost();
        host.Fake.Failures.Add("POST", BuiltInRoles.AssignmentsPath, request =>
        {
            var existing = (JsonObject)request.Body!.DeepClone();
            existing.Remove("@odata.type");
            host.Directory.Add(BuiltInRoles.AssignmentsPath, existing);
            return new GraphException(HttpStatusCode.BadRequest, "Request_BadRequest", "A conflicting object with one or more of the specified property values is present in the directory.", request.Hint);
        });

        var run = await host.CreateAsync(host.Resources(Join(Alice(), Standalone(AliceRef + ", \"role\": \"Global Reader\""))));

        Check.Succeeded(run);
        var assignment = Assert.Single(host.Directory.Objects(BuiltInRoles.AssignmentsPath));
        Assert.Equal(assignment["id"]!.GetValue<string>(), run.Entry(AssignmentId(run)).GraphId);
    }

    [Fact]
    public async Task S7_GlobalAdministrator_CarriesThePrivilegedRoleAdministratorReason()
    {
        var host = NewHost();
        var document = host.Resources(Join(Alice(), Standalone(AliceRef + ", \"role\": \"Global Administrator\"", "[guid('alice', 'ga')]")));

        var whatIf = await host.WhatIfAsync(document);
        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        Assert.Contains(whatIf.Changes.First(c => c.ResourceType == AssignmentType).Reasons, r => r.Contains("Privileged Role Administrator", StringComparison.Ordinal));
        Assert.Contains(run.Executed.Changes.First(c => c.ResourceType == AssignmentType).Reasons, r => r.Contains("Privileged Role Administrator", StringComparison.Ordinal));
    }

    [Fact]
    public async Task S8_ANonRoleAssignableGroup_IsRoleAssignmentInvalid_WithZeroAssignmentWrites()
    {
        var host = NewHost();

        var run = await host.CreateAsync(host.Resources(Join(Group(assignable: false), Standalone("\"principalId\": \"[reference('test-admins').id]\", \"role\": \"Global Reader\"", "[guid('group', 'gr')]"))));

        var op = run.OpOfType(AssignmentType);
        Assert.False(run.Succeeded);
        Assert.Equal(OperationStatus.Failed, op.Status);
        Assert.Equal(RoleAssignmentsProvider.InvalidAssignment, op.ErrorCode);
        Assert.Contains("not role-assignable", op.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(run.Writes, w => w.Path.Contains("roleAssignments", StringComparison.Ordinal));
        Assert.Contains(run.Requests, r => r.Method == "GET" && r.Path.StartsWith("/groups/", StringComparison.Ordinal) && r.UrlParameters!["$select"].Contains("isAssignableToRole", StringComparison.Ordinal));
    }

    [Fact]
    public async Task S9_UnknownRoleOrUnitNames_FailBeforeAnyWrite()
    {
        var host = NewHost();

        var role = await host.CreateAsync(host.Resources(Join(Alice(), Standalone(AliceRef + ", \"role\": \"Nope Role\"", "[guid('alice', 'nope')]"))));
        var roleOp = role.OpOfType(AssignmentType);
        Assert.False(role.Succeeded);
        Assert.Equal(RoleAssignmentsProvider.InvalidAssignment, roleOp.ErrorCode);
        Assert.Contains("no directory role named 'Nope Role'", roleOp.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(role.Writes, w => w.Path.Contains("roleAssignments", StringComparison.Ordinal));

        var unit = await host.CreateAsync(host.Resources(Join(Alice(), Standalone(AliceRef + ", \"role\": \"Global Reader\", \"administrativeUnit\": \"Nope AU\"", "[guid('alice', 'nope-au')]"))));
        Assert.False(unit.Succeeded);
        Assert.Contains("no administrative unit named 'Nope AU'", unit.OpOfType(AssignmentType).Error, StringComparison.Ordinal);
        Assert.DoesNotContain(unit.Writes, w => w.Path.Contains("roleAssignments", StringComparison.Ordinal));
    }

    [Fact]
    public async Task S10_CompleteMode_DeletesTheAssignmentById()
    {
        var host = NewHost();
        Check.Succeeded(await host.CreateAsync(host.Resources(Join(Alice(), Standalone(AliceRef + ", \"role\": \"Global Reader\"")))));

        var run = await host.CreateAsync(host.Resources(Alice()), DeploymentMode.Complete);

        Check.Succeeded(run);
        var write = Assert.Single(run.Writes);
        Assert.Equal("DELETE", write.Method);
        Assert.StartsWith(BuiltInRoles.AssignmentsPath + "/", write.Path, StringComparison.Ordinal);
        Assert.Empty(host.Directory.Objects(BuiltInRoles.AssignmentsPath));
    }

    [Fact]
    public async Task TheNameMustBeAGuid_AndOnlyKnownPropertiesAreAccepted()
    {
        var host = NewHost();

        var badName = await Assert.ThrowsAsync<ValidationFailedException>(() => host.WhatIfAsync(host.Resources(Join(Alice(), Standalone(AliceRef + ", \"role\": \"Global Reader\"", "not-a-guid")))));
        Assert.Contains(badName.Diagnostics, d => d.Code == "InvalidResourceName");

        var unknown = await host.WhatIfAsync(host.Resources(Join(Alice(), Standalone(AliceRef + ", \"role\": \"Global Reader\", \"bogus\": 1"))));
        var row = unknown.Changes.First(c => c.ResourceType == AssignmentType);
        Assert.True(row.HasError);
        Assert.Contains("is not a property of a directory role assignment", row.UnsupportedReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task W1_WhatIf_OnAFreshTenant_WritesNothing_AndFlagsTheProblemRows()
    {
        var host = NewHost();
        var template = Join(
            Alice(DirectoryRoles("{ \"role\": \"Global Reader\" }")),
            Au(props: "\"description\": \"x\", \"isMemberManagementRestricted\": true, \"members\": [\"[reference('alice@contoso.com').id]\"]"),
            RoleDef(),
            Group(DirectoryRoles("{ \"role\": \"Orders Support\", \"administrativeUnit\": \"Orders AU\" }"), assignable: false),
            Standalone("\"principalId\": \"[reference('alice@contoso.com').id]\", \"role\": \"Global Administrator\"", "[guid('alice', 'ga')]"));

        var run = await host.WhatIfAsync(host.Resources(template));

        Check.NoWrites(run);
        Assert.Equal(ChangeType.Create, run.Change("Orders AU").Type);
        Assert.Equal(ChangeType.Create, run.Change("Orders Support").Type);
        var standalone = run.Changes.First(c => c.ResourceType == AssignmentType);
        Assert.Equal(ChangeType.Create, standalone.Type);
        Assert.Contains(standalone.Reasons, r => r.Contains("Privileged Role Administrator", StringComparison.Ordinal));
        Assert.Contains(run.Change("Orders AU").Reasons, r => r.Contains("restricts member management", StringComparison.Ordinal));
        Assert.Contains("role-assignable", run.Change("test-admins").UnsupportedReason, StringComparison.Ordinal);
        var alice = run.Change("alice@contoso.com");
        var delta = Assert.Single(alice.Deltas, d => d.Path.ToString() == "properties.directoryRoles");
        Assert.Equal(PropertyChangeType.Array, delta.Kind);
        Assert.Single(delta.Children!);
    }

    [Fact]
    public async Task W2_WhatIf_AgainstADeployedTenant_ShowsTheRecreateAndAdoptionRows()
    {
        var host = NewHost();
        string aliceRole(string role)
        {
            return Standalone(AliceRef + ", \"role\": \"" + role + "\"");
        }

        string groupAssignment = Standalone("\"principalId\": \"[reference('test-admins').id]\", \"role\": \"Global Reader\"", "[guid('group', 'gr')]");
        var deployed = await host.CreateAsync(host.Resources(Join(Alice(), Group(DirectoryRoles("{ \"role\": \"Global Reader\" }")), aliceRole("Global Reader"))));
        Check.Succeeded(deployed);

        var run = await host.WhatIfAsync(host.Resources(Join(Alice(), Group(DirectoryRoles("{ \"role\": \"Global Reader\" }")), aliceRole("Security Reader"), groupAssignment)));

        Check.NoWrites(run);
        var recreate = run.Changes.First(c => c.ResourceType == AssignmentType && c.Type == ChangeType.Modify);
        Assert.Contains(recreate.Deltas, d => d.Path.ToString() == "properties.roleDefinitionId");
        Assert.Equal(ChangeType.NoChange, run.Change("test-admins").Type);
        Assert.Equal(ChangeType.NoChange, run.Changes.First(c => c.ResourceType == AssignmentType && c.Type != ChangeType.Modify).Type);
    }
}
