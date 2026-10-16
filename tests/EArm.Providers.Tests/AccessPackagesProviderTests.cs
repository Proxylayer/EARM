using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Providers.Tests.Infrastructure;
using EArm.TestSupport;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// The entitlement management providers end to end over the fake tenant (docs/access-packages.md): a catalog, a group added to it, an access package,
/// the role the package grants and its approval policy; create, NoChange, modify, what-if, ambiguous names, the asynchronous resource request, the
/// create-only defaults, the delete refusals and the PIM for Groups onboarding prerequisite of the eligible roles.
/// </summary>
public sealed class AccessPackagesProviderTests
{
    private const string Root = FakeEntitlementManagement.Root;
    private const string CatalogName = "PG Development Access";
    private const string PackageName = "PG Development Privileged Access";
    private const string PolicyName = "Approval by PG Development Approvers";

    private sealed record Tenant(ProviderHost Host, string GroupId, string ApproversId);

    private static Tenant NewTenant(bool roleAssignable = false)
    {
        var host = new ProviderHost();
        string group = host.Directory.AddGroup("pg-dev-privileged-access", "PG Development Privileged Access", g => g["isAssignableToRole"] = roleAssignable)["id"]!.GetValue<string>();
        string approvers = host.Directory.AddGroup("pg-dev-approvers", "PG Development Approvers")["id"]!.GetValue<string>();
        return new Tenant(host, group, approvers);
    }

    private static string Catalog(string extra = "")
    {
        return Templates.Resource(AccessPackageCatalogsProvider.ResourceType, CatalogName, "{ \"description\": \"Access to Development\"" + extra + " }");
    }

    private static string CatalogResource(string groupId)
    {
        return Templates.Resource(CatalogResourcesProvider.ResourceType, Guid(1), "{ \"catalogId\": \"[reference('" + CatalogName + "').id]\", \"originId\": \"" + groupId + "\" }");
    }

    private static string Package(string description = "Eligible membership")
    {
        return Templates.Resource(AccessPackagesProvider.ResourceType, PackageName, "{ \"catalogId\": \"[reference('" + CatalogName + "').id]\", \"description\": \"" + description + "\" }");
    }

    private static string RoleScope(string groupId, string role = "Member", int label = 2)
    {
        return Templates.Resource(ResourceRoleScopesProvider.ResourceType, Guid(label), "{ \"accessPackageId\": \"[reference('" + PackageName + "').id]\", \"resourceOriginId\": \"" + groupId + "\", \"role\": \"" + role + "\" }");
    }

    private static string Policy(string approversId, string duration = "P180D")
    {
        return Templates.Resource(AssignmentPoliciesProvider.ResourceType, PackageName + "/" + PolicyName, """
            {
              "description": "Member users request it; the approvers approve",
              "allowedTargetScope": "allMemberUsers",
              "expiration": { "type": "afterDuration", "duration": "DURATION" },
              "requestorSettings": { "enableTargetsToSelfAddAccess": true, "enableTargetsToSelfRemoveAccess": true },
              "requestApprovalSettings": {
                "isApprovalRequiredForAdd": true,
                "isRequestorJustificationRequired": true,
                "stages": [ { "durationBeforeAutomaticDenial": "P14D", "isApproverJustificationRequired": true,
                              "primaryApprovers": [ { "@odata.type": "#microsoft.graph.groupMembers", "groupId": "APPROVERS" } ] } ]
              }
            }
            """.Replace("DURATION", duration, StringComparison.Ordinal).Replace("APPROVERS", approversId, StringComparison.Ordinal));
    }

    private static string Guid(int n)
    {
        return $"00000000-0000-4000-8000-{n:D12}";
    }

    private static string All(Tenant t, string role = "Member", string duration = "P180D", string packageDescription = "Eligible membership")
    {
        return string.Join(", ", Catalog(), CatalogResource(t.GroupId), Package(packageDescription), RoleScope(t.GroupId, role), Policy(t.ApproversId, duration));
    }

    [Fact]
    public async Task TheScenario_IsCreatedInDependencyOrder_AndARerunIsNoChange()
    {
        var t = NewTenant();
        var document = t.Host.Resources(All(t));

        var whatIf = await t.Host.WhatIfAsync(document);
        Assert.All(whatIf.Plan.Changes, c => Assert.Equal(ChangeType.Create, c.Type));
        Assert.DoesNotContain(whatIf.Plan.Changes, c => c.HasError);
        Check.NoWrites(whatIf);

        var run = await t.Host.CreateAsync(document);

        Check.Succeeded(run);
        Assert.Equal(
            [Root + "/catalogs", Root + "/resourceRequests", Root + "/accessPackages", "resourceRoleScopes", Root + "/assignmentPolicies"],
            run.Writes.Select(w => w.Path.EndsWith("/resourceRoleScopes", StringComparison.Ordinal) ? "resourceRoleScopes" : w.Path).ToArray());

        var catalogBody = Check.Body(run.Writes[0]);
        Assert.Equal("published", catalogBody["state"]!.GetValue<string>());
        Assert.False(catalogBody["isExternallyVisible"]!.GetValue<bool>());
        Assert.Equal(CatalogName, catalogBody["displayName"]!.GetValue<string>());

        var request = Check.Body(run.Writes[1]);
        Assert.Equal("adminAdd", request["requestType"]!.GetValue<string>());
        Assert.Equal(t.GroupId, request["resource"]!["originId"]!.GetValue<string>());
        Assert.Equal("AadGroup", request["resource"]!["originSystem"]!.GetValue<string>());

        var packageBody = Check.Body(run.Writes[2]);
        Assert.False(packageBody.ContainsKey("catalogId"));
        Assert.NotNull(packageBody["catalog"]!["id"]);
        Assert.False(packageBody["isHidden"]!.GetValue<bool>());

        var roleScope = Check.Body(run.Writes[3]);
        Assert.Equal("Member_" + t.GroupId, roleScope["role"]!["originId"]!.GetValue<string>());
        Assert.True(roleScope["scope"]!["isRootScope"]!.GetValue<bool>());

        var policy = Check.Body(run.Writes[4]);
        Assert.Equal(PolicyName, policy["displayName"]!.GetValue<string>());
        Assert.NotNull(policy["accessPackage"]!["id"]);

        var rerun = await t.Host.CreateAsync(document);
        Assert.All(rerun.Executed.Operations, o => Assert.Equal(ChangeType.NoChange, o.ChangeType));
        Check.NoWrites(rerun);
    }

    [Fact]
    public async Task ChangedDescriptionsAndExpiration_ArePatchedAndPut_TheLivePolicyIsTheBaseOfThePut()
    {
        var t = NewTenant();
        Check.Succeeded(await t.Host.CreateAsync(t.Host.Resources(All(t))));
        string policyId = t.Host.Directory.EntitlementManagement.Policies.Single()["id"]!.GetValue<string>();
        t.Host.ClearRequests();

        var document = t.Host.Resources(All(t, duration: "P90D", packageDescription: "Eligible membership, approved"));
        var whatIf = await t.Host.WhatIfAsync(document);
        Assert.Equal(ChangeType.Modify, whatIf.Change(PackageName + "/assignmentPolicies").Type);
        Assert.Equal(ChangeType.Modify, whatIf.Change("accessPackages/" + PackageName).Type);
        Assert.Equal(ChangeType.NoChange, whatIf.Change("catalogs/").Type);

        var run = await t.Host.CreateAsync(document);

        Check.Succeeded(run);
        var patch = Assert.Single(run.Writes, w => w.Method == "PATCH");
        Assert.Equal("{\"description\":\"Eligible membership, approved\"}", Check.Json(patch.Body));
        var put = Assert.Single(run.Writes, w => w.Method == "PUT");
        Assert.Equal(Root + "/assignmentPolicies/" + policyId, put.Path);
        var body = Check.Body(put);
        Assert.Equal("P90D", body["expiration"]!["duration"]!.GetValue<string>());
        Assert.False(body.ContainsKey("id"));
        Assert.False(body.ContainsKey("createdDateTime"));
        Assert.NotNull(body["accessPackage"]!["id"]);
        var stage = body["requestApprovalSettings"]!["stages"]![0]!.AsObject();
        Assert.Equal("PT0S", stage["durationBeforeEscalation"]!.GetValue<string>());
        Assert.False(stage["primaryApprovers"]![0]!.AsObject().ContainsKey("description"));
        Assert.False(body["requestApprovalSettings"]!["isApprovalRequiredForUpdate"]!.GetValue<bool>());

        Assert.All((await t.Host.WhatIfAsync(document)).Plan.Changes, c => Assert.Equal(ChangeType.NoChange, c.Type));
    }

    [Fact]
    public async Task AnExtraLiveApprover_IsDrift()
    {
        var t = NewTenant();
        Check.Succeeded(await t.Host.CreateAsync(t.Host.Resources(All(t))));
        var stage = t.Host.Directory.EntitlementManagement.Policies.Single()["requestApprovalSettings"]!["stages"]![0]!;
        ((JsonArray)stage["primaryApprovers"]!).Add(new JsonObject { ["@odata.type"] = "#microsoft.graph.singleUser", ["userId"] = Guid(9), ["description"] = "Someone" });

        var whatIf = await t.Host.WhatIfAsync(t.Host.Resources(All(t)));

        Assert.Equal(ChangeType.Modify, whatIf.Change(PackageName + "/assignmentPolicies").Type);
    }

    [Fact]
    public async Task AnExistingCatalog_GetsNoCreateOnlyDefaults()
    {
        var t = NewTenant();
        var live = t.Host.Directory.EntitlementManagement.AddCatalog(CatalogName);
        live["state"] = "unpublished";

        var run = await t.Host.CreateAsync(t.Host.Resources(Catalog()));

        Check.Succeeded(run);
        var patch = Assert.Single(run.Writes);
        Assert.Equal("{\"description\":\"Access to Development\"}", Check.Json(patch.Body));
        Assert.True(live["isExternallyVisible"]!.GetValue<bool>());
        Assert.Equal("unpublished", live["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task TwoCatalogsWithTheName_AreAmbiguous_AndNothingIsWritten()
    {
        var t = NewTenant();
        t.Host.Directory.EntitlementManagement.AddCatalog(CatalogName);
        t.Host.Directory.EntitlementManagement.AddCatalog(CatalogName);

        var run = await t.Host.CreateAsync(t.Host.Resources(Catalog()));

        var op = Assert.Single(run.Executed.Operations);
        Assert.Equal(ProviderException.AmbiguousMatch, op.ErrorCode);
        Assert.Contains("rename or delete the others", op.Error, StringComparison.Ordinal);
        Check.NoWrites(run);
    }

    [Fact]
    public async Task ARequestedResource_ListedLate_IsWaitedFor()
    {
        var t = NewTenant();
        t.Host.Directory.EntitlementManagement.ResourceListLag = 2;

        var run = await t.Host.CreateAsync(t.Host.Resources(Catalog() + ", " + CatalogResource(t.GroupId)));

        Check.Succeeded(run);
        Assert.True(t.Host.SettleDelays.Count >= 1, "expected the create to wait for the resource");
        Assert.Single(run.Writes, w => w.Path == Root + "/resourceRequests");
    }

    [Fact]
    public async Task AFailedResourceRequest_IsCatalogResourceRequestFailed()
    {
        var t = NewTenant();
        t.Host.Directory.EntitlementManagement.NextRequestState = "deliveryFailed";

        var run = await t.Host.CreateAsync(t.Host.Resources(Catalog() + ", " + CatalogResource(t.GroupId)));

        Assert.Equal(EngineDiagnosticCodes.CatalogResourceRequestFailed, run.Op("catalogResources").ErrorCode);
    }

    [Fact]
    public async Task ARoleAssignableGroup_WithoutPrivilegedRoleAdministrator_IsCatalogResourceAccessDenied()
    {
        var t = NewTenant(roleAssignable: true);
        t.Host.Directory.EntitlementManagement.CallerMayAddRoleAssignableGroups = false;

        var run = await t.Host.CreateAsync(t.Host.Resources(Catalog() + ", " + CatalogResource(t.GroupId)));

        var op = run.Op("catalogResources");
        Assert.Equal(EngineDiagnosticCodes.CatalogResourceAccessDenied, op.ErrorCode);
        Assert.Contains("Privileged Role Administrator", op.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PackagesAndPoliciesWithAssignments_AreNotDeleted()
    {
        var t = NewTenant();
        var document = t.Host.Resources(All(t));
        Check.Succeeded(await t.Host.CreateAsync(document));
        var em = t.Host.Directory.EntitlementManagement;
        em.AddAssignment(em.Packages.Single()["id"]!.GetValue<string>(), em.Policies.Single()["id"]!.GetValue<string>());

        var deletion = await t.Host.DeleteDeclaredAsync(document, continueOnError: true);

        Assert.False(deletion.Succeeded);
        var refused = deletion.Outcomes.Where(o => o.ErrorCode == EngineDiagnosticCodes.AccessPackageAssignmentsExist).Select(o => o.Target.Instance.Type).ToList();
        Assert.Contains(AssignmentPoliciesProvider.ResourceType, refused);
        Assert.Single(em.Packages);
        Assert.Single(em.Policies);
        Assert.Empty(em.RoleScopes);
    }

    [Fact]
    public async Task WithoutAssignments_DeploymentDelete_RemovesEverythingInReverseOrder()
    {
        var t = NewTenant();
        var document = t.Host.Resources(All(t));
        Check.Succeeded(await t.Host.CreateAsync(document));
        t.Host.ClearRequests();

        var deletion = await t.Host.DeleteDeclaredAsync(document);

        Assert.True(deletion.Succeeded, string.Join("; ", deletion.Outcomes.Select(o => o.Error)));
        var em = t.Host.Directory.EntitlementManagement;
        Assert.Empty(em.Policies);
        Assert.Empty(em.RoleScopes);
        Assert.Empty(em.Packages);
        Assert.Empty(em.Resources);
        Assert.Empty(em.Catalogs);
        var remove = Assert.Single(t.Host.Fake.Writes, w => w.Path == Root + "/resourceRequests");
        Assert.Equal("adminRemove", Check.Body(remove)["requestType"]!.GetValue<string>());
    }

    // ---- eligible roles (PIM for Groups) ----

    [Fact]
    public async Task AnEligibleRole_OfAGroupNotOnboarded_IsPimGroupNotOnboarded()
    {
        var t = NewTenant();
        Check.Succeeded(await t.Host.CreateAsync(t.Host.Resources(All(t))));
        var document = t.Host.Resources(All(t) + ", " + RoleScope(t.GroupId, "Eligible Member", label: 3));

        var whatIf = await t.Host.WhatIfAsync(document);
        var row = whatIf.Change(Guid(3));
        Assert.Equal(EngineDiagnosticCodes.PimGroupNotOnboarded, row.UnsupportedCode);
        Assert.Contains("no schedule request of this deployment onboards it", row.UnsupportedReason, StringComparison.Ordinal);

        var run = await t.Host.CreateAsync(document);
        Assert.Equal(EngineDiagnosticCodes.PimGroupNotOnboarded, run.Op(Guid(3)).ErrorCode);
        Check.NoWrites(run);
    }

    [Fact]
    public async Task AnEligibleRole_OfAGroupOnboardedBySameRunRequest_IsDeferred_AndCreatedOnceOnboarded()
    {
        var t = NewTenant();
        Check.Succeeded(await t.Host.CreateAsync(t.Host.Resources(All(t))));
        string alice = t.Host.Directory.AddUser("alice@contoso.com", "Alice")["id"]!.GetValue<string>();
        string eligibility = Templates.Resource(GroupEligibilityScheduleRequestsProvider.ResourceType, Guid(4), "{ \"groupId\": \"" + t.GroupId + "\", \"principalId\": \"" + alice + "\", \"accessId\": \"member\", \"scheduleInfo\": { \"expiration\": { \"type\": \"AfterDuration\", \"duration\": \"P30D\" } } }");
        var document = t.Host.Resources(All(t) + ", " + RoleScope(t.GroupId, "Eligible Member", label: 3) + ", " + eligibility);

        var whatIf = await t.Host.WhatIfAsync(document);
        Assert.Equal(ChangeType.Ignore, whatIf.Change(Guid(3)).Type);
        Assert.Contains(whatIf.Plan.Diagnostics, d => d.Code == EngineDiagnosticCodes.PimGroupNotOnboarded);

        // The executor plans each batch again before applying it: when the request has onboarded the group by then, the role scope is created in this
        // run; otherwise it stays deferred and the next run creates it.
        var run = await t.Host.CreateAsync(document);
        Check.Succeeded(run);
        var next = await t.Host.CreateAsync(document);
        Check.Succeeded(next);

        var post = Assert.Single(run.Writes.Concat(next.Writes), w => w.Path.EndsWith("/resourceRoleScopes", StringComparison.Ordinal));
        Assert.Equal("Eligible Member", Check.Body(post)["role"]!["displayName"]!.GetValue<string>());
        Assert.Equal("EligibleMember_" + t.GroupId, Check.Body(post)["role"]!["originId"]!.GetValue<string>());
        Assert.Equal(ChangeType.NoChange, (await t.Host.WhatIfAsync(document)).Change(Guid(3)).Type);
    }

    [Fact]
    public async Task AGroupNotInTheCatalog_IsAccessPackageRoleNotAvailable()
    {
        var t = NewTenant();
        var document = t.Host.Resources(string.Join(", ", Catalog(), Package(), RoleScope(t.GroupId)));

        var run = await t.Host.CreateAsync(document);

        var op = run.Op(Guid(2));
        Assert.Equal(EngineDiagnosticCodes.AccessPackageRoleNotAvailable, op.ErrorCode);
        Assert.Contains("catalogResources", op.Error, StringComparison.Ordinal);
    }
}
