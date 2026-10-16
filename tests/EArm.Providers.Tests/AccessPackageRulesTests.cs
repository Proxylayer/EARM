using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Providers.Tests.Infrastructure;
using EArm.TestSupport;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>The pure assignment policy shape rules and the apply order the entitlement management providers declare (docs/access-packages.md, "Order").</summary>
public sealed class AccessPackageRulesTests
{
    private const string Root = FakeEntitlementManagement.Root;

    [Fact]
    public void ProjectLive_KeepsOnlyTheDeclaredShape_AndMatchesSubjectsByGroupId()
    {
        var desired = JsonNode.Parse("""
            { "requestApprovalSettings": { "isApprovalRequiredForAdd": true, "stages": [ { "durationBeforeAutomaticDenial": "P14D",
              "primaryApprovers": [ { "@odata.type": "#microsoft.graph.groupMembers", "groupId": "B" }, { "@odata.type": "#microsoft.graph.groupMembers", "groupId": "A" } ] } ] } }
            """);
        var live = JsonNode.Parse("""
            { "id": "p1", "requestApprovalSettings": { "isApprovalRequiredForAdd": true, "isApprovalRequiredForUpdate": false, "stages": [ { "durationBeforeAutomaticDenial": "P14D",
              "durationBeforeEscalation": "PT0S", "escalationApprovers": [],
              "primaryApprovers": [ { "@odata.type": "#microsoft.graph.groupMembers", "groupId": "a", "description": "Approvers A" },
                                    { "@odata.type": "#microsoft.graph.groupMembers", "groupId": "b", "description": "Approvers B" } ] } ] } }
            """);

        var projected = AssignmentPolicyShape.ProjectLive(desired, live)!;

        Assert.Equal(
            """{"requestApprovalSettings":{"isApprovalRequiredForAdd":true,"stages":[{"durationBeforeAutomaticDenial":"P14D","primaryApprovers":[{"@odata.type":"#microsoft.graph.groupMembers","groupId":"b"},{"@odata.type":"#microsoft.graph.groupMembers","groupId":"a"}]}]}}""",
            projected.ToJsonString());
    }

    [Fact]
    public void ProjectLive_KeepsAnUndeclaredLiveEntry_SoItIsDrift()
    {
        var desired = JsonNode.Parse("""{ "specificAllowedTargets": [ { "@odata.type": "#microsoft.graph.groupMembers", "groupId": "A" } ] }""");
        var live = JsonNode.Parse("""{ "specificAllowedTargets": [ { "@odata.type": "#microsoft.graph.groupMembers", "groupId": "A", "description": "x" }, { "@odata.type": "#microsoft.graph.singleUser", "userId": "U" } ] }""");

        var projected = AssignmentPolicyShape.ProjectLive(desired, live)!;

        Assert.Equal(2, projected["specificAllowedTargets"]!.AsArray().Count);
        Assert.False(projected["specificAllowedTargets"]![0]!.AsObject().ContainsKey("description"));
    }

    [Fact]
    public void MergeForPut_KeepsUndeclaredLiveValues_DropsReadOnlyMembers_AndMergesStagesByPosition()
    {
        var live = JsonNode.Parse("""
            { "id": "p1", "createdDateTime": "2026-01-01T00:00:00Z", "displayName": "Approval", "questions": [ { "id": "q1" } ],
              "expiration": { "type": "afterDuration", "duration": "P180D", "endDateTime": null },
              "requestApprovalSettings": { "isApprovalRequiredForAdd": true, "stages": [ { "durationBeforeAutomaticDenial": "P14D", "durationBeforeEscalation": "PT0S",
                "primaryApprovers": [ { "@odata.type": "#microsoft.graph.groupMembers", "groupId": "A", "description": "Approvers" } ] } ] } }
            """)!.AsObject();
        var desired = JsonNode.Parse("""
            { "displayName": "Approval", "expiration": { "duration": "P90D" },
              "requestApprovalSettings": { "stages": [ { "durationBeforeAutomaticDenial": "P7D", "primaryApprovers": [ { "@odata.type": "#microsoft.graph.groupMembers", "groupId": "A" } ] } ] } }
            """)!.AsObject();

        var document = AssignmentPolicyShape.MergeForPut(live, desired);

        Assert.False(document.ContainsKey("id"));
        Assert.False(document.ContainsKey("createdDateTime"));
        Assert.Equal("q1", document["questions"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("afterDuration", document["expiration"]!["type"]!.GetValue<string>());
        Assert.Equal("P90D", document["expiration"]!["duration"]!.GetValue<string>());
        var stage = document["requestApprovalSettings"]!["stages"]![0]!.AsObject();
        Assert.Equal("P7D", stage["durationBeforeAutomaticDenial"]!.GetValue<string>());
        Assert.Equal("PT0S", stage["durationBeforeEscalation"]!.GetValue<string>());
        Assert.False(stage["primaryApprovers"]![0]!.AsObject().ContainsKey("description"));
        Assert.True(document["requestApprovalSettings"]!["isApprovalRequiredForAdd"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ACatalogResource_IsAppliedBeforeTheRoleScopesOfItsGroup_AndARoleScopeBeforeThePoliciesOfItsPackage()
    {
        var host = new ProviderHost();
        string group = host.Directory.AddGroup("pg-ops", "PG Ops")["id"]!.GetValue<string>();
        var catalog = (await host.Fake.PostAsync(new GraphRequest(Root + "/catalogs"), new JsonObject { ["displayName"] = "Ops" }, ProviderHost.Ct)).Body!;
        string catalogId = catalog["id"]!.GetValue<string>();
        var package = (await host.Fake.PostAsync(new GraphRequest(Root + "/accessPackages"), new JsonObject { ["displayName"] = "Ops Package", ["catalog"] = new JsonObject { ["id"] = catalogId } }, ProviderHost.Ct)).Body!;
        string packageId = package["id"]!.GetValue<string>();

        // The Owner role scope names the package and the catalog by literal id: no reference orders it after the catalog resource, only the provider does.
        // The Member role scope references the package, as the policy (its child) depends on it: only the provider orders the policy after it.
        string resources = string.Join(", ",
            Templates.Resource(ResourceRoleScopesProvider.ResourceType, "00000000-0000-4000-8000-000000000003", "{ \"accessPackageId\": \"" + packageId + "\", \"resourceOriginId\": \"" + group + "\", \"role\": \"Owner\" }"),
            Templates.Resource(AssignmentPoliciesProvider.ResourceType, "Ops Package/Approval", "{ \"allowedTargetScope\": \"allMemberUsers\" }"),
            Templates.Resource(ResourceRoleScopesProvider.ResourceType, "00000000-0000-4000-8000-000000000002", "{ \"accessPackageId\": \"[reference('Ops Package').id]\", \"resourceOriginId\": \"" + group + "\", \"role\": \"Member\" }"),
            Templates.Resource(CatalogResourcesProvider.ResourceType, "00000000-0000-4000-8000-000000000001", "{ \"catalogId\": \"" + catalogId + "\", \"originId\": \"" + group + "\" }"),
            Templates.Resource(AccessPackagesProvider.ResourceType, "Ops Package", "{ \"catalogId\": \"" + catalogId + "\" }"));

        var run = await host.CreateAsync(host.Resources(resources));

        Check.Succeeded(run);
        int resourceBatch = run.OpOfType(CatalogResourcesProvider.ResourceType).Batch;
        int literalScopeBatch = run.Op("00000000-0000-4000-8000-000000000003").Batch;
        int scopeBatch = run.Op("00000000-0000-4000-8000-000000000002").Batch;
        int policyBatch = run.OpOfType(AssignmentPoliciesProvider.ResourceType).Batch;
        Assert.True(resourceBatch < literalScopeBatch, $"catalog resource batch {resourceBatch}, role scope batch {literalScopeBatch}");
        Assert.True(scopeBatch < policyBatch, $"role scope batch {scopeBatch}, policy batch {policyBatch}");
        Assert.All(host.Directory.EntitlementManagement.RoleScopes, r => Assert.Equal(packageId, r["accessPackageId"]!.GetValue<string>()));
    }
}
