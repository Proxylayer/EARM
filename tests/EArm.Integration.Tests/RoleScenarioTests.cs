using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Core;
using Xunit;

namespace EArm.Integration.Tests;

/// <summary>
/// Directory roles, administrative units, role assignments (tenant and AU scope), application role assignments, delegated
/// consent grants and the whole orders sample variant. These write privileged objects, so every test needs
/// <c>EARM_IT_ALLOW_ROLES=1</c> (the orders variant also needs <c>EARM_IT_ALLOW_CA=1</c> and <c>EARM_IT_DOMAIN</c>).
/// </summary>
[Collection(RealTenantCollection.Name)]
public sealed class RoleScenarioTests : IntegrationTestBase
{
    private const string RoleDefinitions = "/roleManagement/directory/roleDefinitions";
    private const string RoleAssignments = "/roleManagement/directory/roleAssignments";
    private const string AdministrativeUnits = "/directory/administrativeUnits";

    public RoleScenarioTests(IntegrationFixture fixture)
        : base(fixture)
    {
    }

    [Fact]
    public async Task It_Roles_CustomRoleAdministrativeUnitAssignmentAndDirectoryRolesEdge()
    {
        Fixture.Require(Needs.Roles);
        var run = Fixture.NewRun("roles", "roles.entra.json");
        Assert.True(WellKnownIds.TryGetRoleTemplateId("Directory Readers", out string directoryReaders));
        Assert.True(WellKnownIds.TryGetRoleTemplateId("User Administrator", out string userAdministrator));

        AssertCreated(await Fixture.Cli.CreateAsync(run, Ct), "create");

        var role = await FindOneAsync(RoleDefinitions, "displayName", Fixture.Name("role"));
        var unit = await FindOneAsync(AdministrativeUnits, "displayName", Fixture.Name("au"));
        var group = await GroupByNameAsync(Fixture.Name("rgrp"), "id", "isAssignableToRole");
        Assert.NotNull(role);
        Assert.NotNull(unit);
        Assert.NotNull(group);
        Assert.True((bool?)group["isAssignableToRole"] == true, "The group is not role-assignable.");
        var actions = role["rolePermissions"]!.AsArray().OfType<JsonObject>().SelectMany(p => p.Strings("allowedResourceActions")).ToList();
        Assert.Contains("microsoft.directory/users/password/update", actions);

        string unitScope = "/administrativeUnits/" + unit.RequireId();
        var assignments = await ListAssignmentsAsync(group.RequireId());
        Assert.Contains(("/", directoryReaders), assignments.Select(a => (a.Scope, a.Role)));
        Assert.Contains((unitScope, userAdministrator), assignments.Select(a => (a.Scope, a.Role)));
        Assert.Contains((unitScope, role.RequireId()), assignments.Select(a => (a.Scope, a.Role)));
        Assert.Equal(3, assignments.Count);

        AssertNoChanges(await Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes"), "what-if after create");

        // Drift: the tenant-scope assignment is removed behind EARM's back and comes back.
        var tenantScoped = assignments.Single(a => a.Scope == "/" && a.Role == directoryReaders);
        await Graph.DeleteAsync(new GraphRequest($"{RoleAssignments}/{tenantScoped.Id}"), Ct);
        var drift = await Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes");
        AssertExit(5, drift, "what-if after dropping an assignment");
        Assert.True(EdgeCount(drift, "add") >= 1, "Expected the missing Directory Readers assignment to be an edge to add: " + drift.DescribeChanges());
        AssertCreated(await Fixture.Cli.CreateAsync(run, Ct), "create after drift");
        Assert.Equal(3, (await ListAssignmentsAsync(group.RequireId())).Count);

        // deployment delete: assignments go before the role, the unit and the group.
        AssertExit(0, await Fixture.Cli.DeleteAllAsync(run, Ct), "deployment delete");
        Assert.Null(await FindOneAsync(RoleDefinitions, "displayName", Fixture.Name("role")));
        Assert.Null(await FindOneAsync(AdministrativeUnits, "displayName", Fixture.Name("au")));
        Assert.Null(await GroupByNameAsync(Fixture.Name("rgrp"), "id"));
    }

    [Fact]
    public async Task It_Roles_AppRoleAssignmentsAndOAuth2GrantsAgainstMicrosoftGraph()
    {
        Fixture.Require(Needs.Roles);
        var run = Fixture.NewRun("grants", "assignments.entra.json");
        Assert.True(WellKnownIds.TryGetGraphAppRoleId("User.Read.All", out string userReadAll));

        var created = await Fixture.Cli.CreateAsync(run, Ct);
        AssertCreated(created, "create");
        string servicePrincipalId = (string?)created.Json["outputs"]?["servicePrincipalId"]?["value"] ?? throw new InvalidOperationException("No servicePrincipalId output.");
        var graphSp = await FindOneAsync("/servicePrincipals", "appId", WellKnownIds.MicrosoftGraphAppId, "id");
        Assert.NotNull(graphSp);

        var roleAssignments = await Graph.ListAsync(new GraphRequest($"/servicePrincipals/{servicePrincipalId}/appRoleAssignments"), Ct);
        Assert.Contains(roleAssignments, a => a.Text("resourceId") == graphSp.RequireId() && string.Equals(a.Text("appRoleId"), userReadAll, StringComparison.OrdinalIgnoreCase));

        var grants = await Graph.ListAsync(new GraphRequest("/oauth2PermissionGrants").WithFilter($"clientId eq '{servicePrincipalId}'"), Ct);
        var grant = Assert.Single(grants);
        Assert.Equal("AllPrincipals", grant.Text("consentType"));
        Assert.Equal(graphSp.RequireId(), grant.Text("resourceId"));
        Assert.Contains("User.Read", (grant.Text("scope") ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries));

        AssertNoChanges(await Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes"), "what-if after create");

        // Drift: the grant's scope changes; the template wins.
        await Graph.PatchAsync(new GraphRequest("/oauth2PermissionGrants/" + grant.RequireId()), new JsonObject { ["scope"] = "User.Read profile" }, Ct);
        var drift = await Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes");
        AssertExit(5, drift, "what-if after drift");
        Assert.Contains(drift.Changes, c => ((string?)c["resourceId"])?.Contains("/oauth2PermissionGrants/", StringComparison.OrdinalIgnoreCase) == true && (string?)c["changeType"] == "Modify");
        AssertCreated(await Fixture.Cli.CreateAsync(run, Ct), "create after drift");
        var repaired = Assert.Single(await Graph.ListAsync(new GraphRequest("/oauth2PermissionGrants").WithFilter($"clientId eq '{servicePrincipalId}'"), Ct));
        Assert.Equal(["User.Read"], (repaired.Text("scope") ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries));

        AssertExit(0, await Fixture.Cli.DeleteAllAsync(run, Ct), "deployment delete");
        Assert.Empty(await Graph.ListAsync(new GraphRequest("/oauth2PermissionGrants").WithFilter($"clientId eq '{servicePrincipalId}'"), Ct));
        Assert.Null(await Graph.GetObjectAsync(new GraphRequest("/servicePrincipals/" + servicePrincipalId), Ct));
    }

    [Fact]
    public async Task It_Roles_OrdersSampleVariantCompleteModeAndDeleteRoundTrip()
    {
        Fixture.Require(Needs.Domain | Needs.ConditionalAccess | Needs.Roles);
        string contextId = await Fixture.AllocateContextIdAsync(Ct);
        var run = Fixture.NewRun("orders", "orders-it.entra.json")
            .WithParameter("domain", Fixture.Domain)
            .WithParameter("contextId", contextId)
            .WithParameter("initialPassword", NewPassword());

        // Nothing exists yet: every row is a Create and none is unsupported.
        var preview = await Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes");
        AssertExit(5, preview, "what-if before the first create");
        Assert.All(preview.Changes, c => Assert.Null(c["unsupportedReason"]));
        Assert.DoesNotContain(preview.Changes, c => (string?)c["changeType"] is "Modify" or "Delete");

        string secretFile = Path.Combine(Fixture.ScratchDirectory, "orders.secrets.json");
        var created = await Fixture.Cli.CreateAsync(run, Ct, "--secure-outputs-file", secretFile);
        AssertCreated(created, "create");
        Assert.False(string.IsNullOrWhiteSpace((string?)SecureOutputs(secretFile)["ciSecret"]?["value"]), "The ci-secret output is empty.");

        AssertNoChanges(await Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes"), "what-if after create");

        int managed = created.Json["operations"]!.AsArray().Count(o => (string?)o?["status"] == "Succeeded");
        Assert.True(managed >= 15, $"Expected at least 15 applied resources, found {managed}.");

        // Complete mode never deletes: with the full template it changes nothing and lists no orphan of its own objects.
        var completePreview = await Fixture.Cli.WhatIfAsync(run, Ct, "--mode", "Complete", "--exit-code-on-changes");
        AssertExit(0, completePreview, "Complete what-if");
        Assert.DoesNotContain(completePreview.Changes, c => (string?)c["changeType"] == "Delete");

        // deployment delete removes everything the template declares, in reverse dependency order.
        var deleted = await Fixture.Cli.DeleteAllAsync(run, Ct);
        AssertExit(0, deleted, "deployment delete");
        Assert.True((int?)deleted.Json["deleted"] >= 15, "Expected at least 15 deleted objects: " + deleted.StandardOutput);

        Assert.Null(await ApplicationByNameAsync(Fixture.Name("api"), "id"));
        Assert.Null(await UserAsync(Fixture.Upn("bg"), "id"));
        Assert.Null(await GroupByNameAsync(Fixture.Name("admins"), "id"));
        Assert.Null(await FindOneAsync(AdministrativeUnits, "displayName", Fixture.Name("orders-au")));
        Assert.Null(await FindOneAsync(RoleDefinitions, "displayName", Fixture.Name("support")));
        Assert.Null(await FindOneAsync("/identity/conditionalAccess/policies", "displayName", Fixture.Name("ca-admins-mfa")));
    }

    private async Task<System.Collections.Generic.List<(string Id, string Role, string Scope)>> ListAssignmentsAsync(string principalId)
    {
        var items = await Graph.ListAsync(new GraphRequest(RoleAssignments).WithFilter($"principalId eq '{principalId}'"), Ct);
        return items.Select(i => (i.RequireId(), i.Text("roleDefinitionId") ?? string.Empty, i.Text("directoryScopeId") ?? string.Empty)).ToList();
    }
}
