using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Core.Types;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Providers.Tests.Infrastructure;
using EArm.TestSupport;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// <c>Microsoft.Entra/azureRoleManagementPolicies</c> over <see cref="FakeAzureResourceManager"/> under a real <see cref="EArm.Graph.ArmClient"/>: the role
/// name resolved at the scope, the policy found through the assignment of that exact scope (parents ignored, scopes compared case-insensitively, a policy of
/// another scope refused), only the declared rules compared (filled from live), ONE PATCH of the changed rules (complete, without nulls), the PATCH answer as
/// the re-read, never created or deleted, and the offline registry that refuses any Resource Manager call.
/// </summary>
public sealed class AzureRoleManagementPoliciesProviderTests
{
    private const string Type = AzureRoleManagementPoliciesProvider.ResourceType;
    private const string Name = "3f6e1a2b-7c8d-4e9f-a0b1-c2d3e4f5a6b7";
    private const string Subscription = "6e1f4c2a-0000-4000-8000-000000000003";
    private const string SubscriptionScope = "/subscriptions/" + Subscription;
    private const string Rg = SubscriptionScope + "/resourceGroups/rg-earm";
    private const string Reader = FakeAzureResourceManager.Reader;
    private const string OtherReader = "0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d";
    private const string QualifiedReader = SubscriptionScope + "/providers/Microsoft.Authorization/roleDefinitions/" + Reader;
    private const string PolicyName = "b1e7c0de-0000-4000-8000-0000000000aa";
    private const string RgPolicyPath = Rg + "/providers/Microsoft.Authorization/roleManagementPolicies/" + PolicyName;

    private sealed record Env(ProviderHost Host, FakeAzureResourceManager Arm, AzureRoleManagementPoliciesProvider Provider);

    /// <summary>A host whose provider calls the fake ARM; Reader has a policy at the subscription (listed first, as a parent) and at the resource group.</summary>
    private static Env Create()
    {
        var host = new ProviderHost();
        var arm = new FakeAzureResourceManager();
        arm.AddPolicy(SubscriptionScope, Reader);
        arm.AddPolicy(Rg, Reader, PolicyName);
        var provider = new AzureRoleManagementPoliciesProvider(host.Catalog, arm.CreateClient());
        host.Registry.Register(provider, replace: true);
        return new Env(host, arm, provider);
    }

    private static string Policy(string rules, string role = "\"role\": \"Reader\"", string scope = Rg, string name = Name)
    {
        return Templates.Resource(Type, name, "{ \"scope\": \"" + scope + "\", " + role + ", \"rules\": [ " + rules + " ] }");
    }

    private static ResourceKey Key(string scope, string roleDefinitionId = QualifiedReader)
    {
        return new ResourceKey { Type = Type, Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["scope"] = scope, ["roleDefinitionId"] = roleDefinitionId } };
    }

    private static List<RecordedHttpRequest> Requests(FakeAzureResourceManager arm, string method, string pathEndsWith)
    {
        return arm.Requests.Where(r => r.Method == method && r.Uri.AbsolutePath.EndsWith(pathEndsWith, StringComparison.Ordinal)).ToList();
    }

    private static string Query(RecordedHttpRequest request)
    {
        return Uri.UnescapeDataString(request.Uri.Query);
    }

    private static string SubscriptionPolicyPath(Env env)
    {
        return env.Arm.Policy(SubscriptionScope, Reader)["id"]!.GetValue<string>();
    }

    private static IEnumerable<PropertyDelta> Flatten(PropertyDelta delta)
    {
        yield return delta;
        foreach (var child in delta.Children ?? [])
        {
            foreach (var nested in Flatten(child))
            {
                yield return nested;
            }
        }
    }

    // ---- prepare: the role sugar ----

    [Fact]
    public async Task ARoleName_IsResolvedAtTheScope_AndAMatchingRule_IsNoChange_WithoutAnyGraphCall()
    {
        var env = Create();

        var run = await env.Host.WhatIfAsync(env.Host.Resources(Policy("""{ "id": "Expiration_EndUser_Assignment", "maximumDuration": "PT8H" }""")));

        var change = run.Change(Name);
        Assert.Equal(ChangeType.NoChange, change.Type);
        Assert.Equal(RgPolicyPath, change.GraphId);
        var lookup = Assert.Single(Requests(env.Arm, "GET", Rg + "/providers/Microsoft.Authorization/roleDefinitions"));
        Assert.Contains("api-version=2022-04-01", Query(lookup), StringComparison.Ordinal);
        Assert.Contains("$filter=roleName eq 'Reader'", Query(lookup), StringComparison.Ordinal);
        Assert.Empty(run.Requests);
    }

    [Fact]
    public async Task ARoleNameNoRoleHas_IsRoleManagementPolicyInvalid()
    {
        var env = Create();

        var run = await env.Host.CreateAsync(env.Host.Resources(Policy("""{ "id": "Expiration_EndUser_Assignment" }""", role: "\"role\": \"No Such Role\"")));

        Assert.False(run.Succeeded);
        Assert.Equal(EngineDiagnosticCodes.RoleManagementPolicyInvalid, run.Op(Name).ErrorCode);
        Assert.Contains("no Azure role named 'No Such Role'", run.Op(Name).Error, StringComparison.Ordinal);
        Assert.Empty(env.Arm.Patches);
    }

    [Fact]
    public async Task TwoRolesOfTheName_AreRoleManagementPolicyInvalid_ListingBoth()
    {
        var env = Create();
        env.Arm.RoleDefinitions.Add((OtherReader, "reader"));

        var run = await env.Host.CreateAsync(env.Host.Resources(Policy("""{ "id": "Expiration_EndUser_Assignment" }""")));

        Assert.False(run.Succeeded);
        var op = run.Op(Name);
        Assert.Equal(EngineDiagnosticCodes.RoleManagementPolicyInvalid, op.ErrorCode);
        Assert.Contains("2 Azure roles named 'Reader'", op.Error, StringComparison.Ordinal);
        Assert.Contains(Reader, op.Error, StringComparison.Ordinal);
        Assert.Contains(OtherReader, op.Error, StringComparison.Ordinal);
        Assert.Empty(env.Arm.Patches);
    }

    [Fact]
    public async Task TheListedRoleNames_AreMatchedAgainIgnoringCase_SoAnotherRoleArmReturns_IsNotTaken()
    {
        var env = Create();
        env.Arm.RoleDefinitionAnswer =
        [
            FakeAzureResourceManager.RoleDefinition(Subscription, OtherReader, "Reader and Data Access"),
            FakeAzureResourceManager.RoleDefinition(Subscription, Reader, "READER"),
        ];

        var run = await env.Host.WhatIfAsync(env.Host.Resources(Policy("""{ "id": "Expiration_EndUser_Assignment", "maximumDuration": "PT8H" }""", role: "\"role\": \"reader\"")));

        var change = run.Change(Name);
        Assert.Equal(ChangeType.NoChange, change.Type);
        Assert.Equal(RgPolicyPath, change.GraphId);
    }

    [Fact]
    public async Task AGuidInRole_SkipsTheRoleLookup()
    {
        var env = Create();

        var run = await env.Host.WhatIfAsync(env.Host.Resources(Policy("""{ "id": "Expiration_EndUser_Assignment", "maximumDuration": "PT8H" }""", role: "\"role\": \"" + Reader.ToUpperInvariant() + "\"")));

        Assert.Equal(ChangeType.NoChange, run.Change(Name).Type);
        Assert.DoesNotContain(env.Arm.Requests, r => r.Uri.AbsolutePath.Contains("roleDefinitions", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AScopeKnownOnlyAtDeployment_MakesTheRoleDefinitionIdUnknown_AndThePolicyIsLookedUpThen()
    {
        var env = Create();
        string group = Templates.Resource("Microsoft.Entra/groups", "new-group", "{ \"displayName\": \"New Group\", \"mailEnabled\": false, \"mailNickname\": \"new-group\", \"securityEnabled\": true }");
        string scope = "[format('" + SubscriptionScope + "/resourceGroups/{0}', reference(resourceId('Microsoft.Entra/groups', 'new-group')).id)]";

        var run = await env.Host.WhatIfAsync(env.Host.Resources(group + ", " + Policy("""{ "id": "Expiration_EndUser_Assignment", "maximumDuration": "PT4H" }""", scope: scope)));

        var change = run.Change(Name);
        Assert.True(change.IsUnknown);
        Assert.Null(change.UnsupportedReason);
        Assert.Contains(change.Reasons, r => r.Contains("looked up when the deployment runs", StringComparison.Ordinal));
        Assert.DoesNotContain(env.Arm.Requests, r => r.Uri.AbsolutePath.Contains("roleDefinitions", StringComparison.Ordinal));
        var instance = run.Plan.Instances.Single(i => ResourceIdentifier.TypeEquals(i.Type, Type));
        Assert.False(instance.Properties!.ContainsKey("role"));
        Assert.True(EArm.Expressions.Values.UnknownJson.IsUnknown(instance.Properties["roleDefinitionId"]));
    }

    // ---- read ----

    [Theory]
    [InlineData(Reader)]
    [InlineData("/providers/Microsoft.Authorization/roleDefinitions/" + Reader)]
    [InlineData(SubscriptionScope + "/resourceGroups/other/providers/Microsoft.Authorization/roleDefinitions/" + Reader)]
    public async Task TheAssignmentFilter_CarriesTheSubscriptionQualifiedRoleDefinitionId(string roleDefinitionId)
    {
        var env = Create();

        var run = await env.Host.WhatIfAsync(env.Host.Resources(Policy("""{ "id": "Expiration_EndUser_Assignment", "maximumDuration": "PT8H" }""", role: "\"roleDefinitionId\": \"" + roleDefinitionId + "\"")));

        Assert.Equal(ChangeType.NoChange, run.Change(Name).Type);
        var list = Assert.Single(Requests(env.Arm, "GET", Rg + "/providers/Microsoft.Authorization/roleManagementPolicyAssignments"));
        Assert.Contains("api-version=2020-10-01", Query(list), StringComparison.Ordinal);
        Assert.Contains("$filter=roleDefinitionId eq '" + QualifiedReader + "'", Query(list), StringComparison.Ordinal);
        Assert.Single(Requests(env.Arm, "GET", RgPolicyPath));
    }

    [Fact]
    public async Task TheParentScopesAssignment_IsIgnored_OnlyThePolicyOfTheExactScopeIsRead()
    {
        var env = Create();
        var subscriptionRule = env.Arm.Rule(SubscriptionScope, Reader, "Expiration_EndUser_Assignment");
        subscriptionRule["maximumDuration"] = "PT4H";
        string subscriptionPolicy = SubscriptionPolicyPath(env);
        await env.Arm.CreateClient().PatchAsync(subscriptionPolicy, "2020-10-01", new JsonObject { ["properties"] = new JsonObject { ["rules"] = new JsonArray(subscriptionRule) } }, ProviderHost.Ct);
        Assert.Equal("PT4H", env.Arm.Rule(SubscriptionScope, Reader, "Expiration_EndUser_Assignment")["maximumDuration"]!.GetValue<string>());

        var live = await env.Provider.ReadAsync(Key(Rg), env.Host.Fake, ProviderHost.Ct);

        Assert.NotNull(live);
        Assert.Equal(RgPolicyPath, live.GraphId);
        var rule = live.Properties!["rules"]!.AsArray().OfType<JsonObject>().Single(r => r["id"]!.GetValue<string>() == "Expiration_EndUser_Assignment");
        Assert.Equal("PT8H", rule["maximumDuration"]!.GetValue<string>());
    }

    [Fact]
    public async Task AScopeWithOnlyAnInheritedPolicy_ReadsAsMissing()
    {
        var env = Create();
        const string Other = SubscriptionScope + "/resourceGroups/rg-without-own-policy";
        env.Arm.Scopes.Add(Other);

        Assert.Null(await env.Provider.ReadAsync(Key(Other), env.Host.Fake, ProviderHost.Ct));
        Assert.DoesNotContain(env.Arm.Requests, r => r.Uri.AbsolutePath.Contains("/roleManagementPolicies/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheScope_IsComparedIgnoringCase_AndNormalisedWithoutTheTrailingSlash()
    {
        var env = Create();
        string declared = "/SUBSCRIPTIONS/" + Subscription.ToUpperInvariant() + "/resourcegroups/RG-EARM/";

        var run = await env.Host.WhatIfAsync(env.Host.Resources(Policy("""{ "id": "Expiration_EndUser_Assignment", "maximumDuration": "PT8H" }""", scope: declared)));

        var change = run.Change(Name);
        Assert.Equal(ChangeType.NoChange, change.Type);
        Assert.Equal(SubscriptionScope + "/resourceGroups/RG-EARM/providers/Microsoft.Authorization/roleManagementPolicies/" + PolicyName, change.GraphId);
        Assert.All(env.Arm.Requests, r => Assert.StartsWith(SubscriptionScope + "/resourceGroups/RG-EARM/", r.Uri.AbsolutePath, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(SubscriptionScope + "/providers/Microsoft.Authorization/roleManagementPolicies/" + PolicyName)]
    [InlineData("{other}/providers/Microsoft.Authorization/roleManagementPolicies/" + PolicyName + "/extra")]
    [InlineData("{other}/providers/Microsoft.Authorization/roleManagementPolicies/")]
    [InlineData("{other}/providers/Microsoft.Authorization/roleManagementPolicyAssignments/" + PolicyName)]
    public async Task AnAssignmentWhosePolicyIsNotInTheScopesCollection_IsReadFailed(string policyId)
    {
        var env = Create();
        const string Other = SubscriptionScope + "/resourceGroups/rg-foreign";
        env.Arm.Scopes.Add(Other);
        env.Arm.ExtraAssignments.Add(new JsonObject
        {
            ["properties"] = new JsonObject { ["scope"] = Other, ["roleDefinitionId"] = QualifiedReader, ["policyId"] = policyId.Replace("{other}", Other, StringComparison.Ordinal) },
        });

        var error = await Assert.ThrowsAsync<ProviderException>(() => env.Provider.ReadAsync(Key(Other), env.Host.Fake, ProviderHost.Ct));

        Assert.Equal(EngineDiagnosticCodes.ReadFailed, error.Code);
        Assert.Contains("never changes a policy of another scope", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(env.Arm.Requests, r => r.Uri.AbsolutePath.Contains("/roleManagementPolicies/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APolicyThatReportsAnotherScope_IsReadFailed()
    {
        var env = Create();
        env.Arm.SetReportedScope(Rg, Reader, SubscriptionScope);

        var error = await Assert.ThrowsAsync<ProviderException>(() => env.Provider.ReadAsync(Key(Rg), env.Host.Fake, ProviderHost.Ct));

        Assert.Equal(EngineDiagnosticCodes.ReadFailed, error.Code);
        Assert.Contains("belongs to scope '" + SubscriptionScope + "'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A404OnTheAssignmentList_ReadsAsMissing()
    {
        var env = Create();

        Assert.Null(await env.Provider.ReadAsync(Key(SubscriptionScope + "/resourceGroups/rg-not-created-yet"), env.Host.Fake, ProviderHost.Ct));
    }

    // ---- plan ----

    [Fact]
    public async Task AMissingPolicy_IsPimPolicyNotAvailable_AndNothingIsWritten()
    {
        var env = Create();
        var document = env.Host.Resources(Policy("""{ "id": "Expiration_EndUser_Assignment", "maximumDuration": "PT4H" }""", role: "\"role\": \"Contributor\""));

        var whatIf = await env.Host.WhatIfAsync(document);
        var change = whatIf.Change(Name);
        Assert.Equal(EngineDiagnosticCodes.PimPolicyNotAvailable, change.UnsupportedCode);
        Assert.Contains("lists no PIM policy for role " + SubscriptionScope + "/providers/Microsoft.Authorization/roleDefinitions/" + FakeAzureResourceManager.Contributor, change.UnsupportedReason, StringComparison.Ordinal);

        var run = await env.Host.CreateAsync(document);
        Assert.False(run.Succeeded);
        Assert.Equal(EngineDiagnosticCodes.PimPolicyNotAvailable, run.Op(Name).ErrorCode);
        Assert.Empty(env.Arm.Patches);
    }

    [Fact]
    public async Task AScopeThatDoesNotExistYet_IsPimPolicyNotAvailable()
    {
        var env = Create();

        var whatIf = await env.Host.WhatIfAsync(env.Host.Resources(Policy("""{ "id": "Expiration_EndUser_Assignment" }""", role: "\"roleDefinitionId\": \"" + Reader + "\"", scope: SubscriptionScope + "/resourceGroups/rg-later")));

        Assert.Equal(EngineDiagnosticCodes.PimPolicyNotAvailable, whatIf.Change(Name).UnsupportedCode);
    }

    [Fact]
    public async Task ARuleIdThePolicyDoesNotHave_IsAnError_ThatListsThePolicysRuleIds()
    {
        var env = Create();
        var document = env.Host.Resources(Policy("""{ "id": "AuthenticationContext_Admin_Assignment", "isEnabled": false }"""));

        var change = (await env.Host.WhatIfAsync(document)).Change(Name);

        Assert.Equal(EngineDiagnosticCodes.RoleManagementPolicyInvalid, change.UnsupportedCode);
        Assert.Contains("has no rule AuthenticationContext_Admin_Assignment", change.UnsupportedReason, StringComparison.Ordinal);
        Assert.Contains("Approval_EndUser_Assignment", change.UnsupportedReason, StringComparison.Ordinal);
        Assert.Contains("Expiration_EndUser_Assignment", change.UnsupportedReason, StringComparison.Ordinal);
        var run = await env.Host.CreateAsync(document);
        Assert.False(run.Succeeded);
        Assert.Empty(env.Arm.Patches);
    }

    [Fact]
    public async Task ANameThatIsNotAGuid_IsRefusedByValidation_BeforeAnyResourceManagerCall()
    {
        var env = Create();

        var error = await Assert.ThrowsAsync<ValidationFailedException>(() => env.Host.WhatIfAsync(env.Host.Resources(Policy("""{ "id": "Expiration_EndUser_Assignment" }""", name: "reader-policy"))));

        Assert.Contains("must be a GUID", error.Message, StringComparison.Ordinal);
        Assert.Empty(env.Arm.Requests);
    }

    [Fact]
    public async Task AnArmPastedRule_WithItsNullsAndRuleType_IsFilledFromLive_AndIsNoChange()
    {
        var env = Create();
        string pasted = """
            { "isExpirationRequired": true, "maximumDuration": "PT480M", "id": "Expiration_EndUser_Assignment", "ruleType": "RoleManagementPolicyExpirationRule",
              "target": { "caller": "EndUser", "operations": ["All"], "level": "Assignment", "targetObjects": null, "inheritableSettings": null, "enforcedSettings": null } },
            { "id": "Enablement_EndUser_Assignment", "enabledRules": ["multifactorauthentication"] }
            """;

        var run = await env.Host.WhatIfAsync(env.Host.Resources(Policy(pasted)));

        Assert.Equal(ChangeType.NoChange, run.Change(Name).Type);
    }

    [Fact]
    public async Task ADurationInAnotherSpelling_IsNoChange()
    {
        var env = Create();
        Check.Succeeded(await env.Host.CreateAsync(env.Host.Resources(Policy("""{ "id": "Expiration_EndUser_Assignment", "maximumDuration": "PT4H" }"""))));

        var rerun = await env.Host.WhatIfAsync(env.Host.Resources(Policy("""{ "id": "Expiration_EndUser_Assignment", "maximumDuration": "PT240M" }""")));

        Assert.Equal(ChangeType.NoChange, rerun.Change(Name).Type);
    }

    [Fact]
    public async Task OnlyTheDeclaredRules_AreShown_LiveOnlyRulesAreNot()
    {
        var env = Create();

        var change = (await env.Host.WhatIfAsync(env.Host.Resources(Policy("""{ "id": "Expiration_EndUser_Assignment", "maximumDuration": "PT4H" }""")))).Change(Name);

        Assert.Equal(ChangeType.Modify, change.Type);
        var paths = change.Deltas.SelectMany(Flatten).Where(d => d.HasEffect).Select(d => d.Path.ToString()).ToList();
        Assert.Contains("properties.rules.Expiration_EndUser_Assignment.maximumDuration", paths);
        Assert.All(paths.Where(p => p != "properties.rules"), p => Assert.StartsWith("properties.rules.Expiration_EndUser_Assignment", p, StringComparison.Ordinal));
        Assert.Single(change.Before!["rules"]!.AsArray());
        Assert.Contains(change.Reasons, r => r.Contains("one PATCH of the policy", StringComparison.Ordinal));
    }

    // ---- update ----

    [Fact]
    public async Task TheChangedRules_AreOnePatchOfTheRebuiltPolicyPath_EachRuleComplete_WithoutNulls_AndTheRerunIsNoChange()
    {
        var env = Create();
        var approvalBefore = env.Arm.Rule(Rg, Reader, "Approval_EndUser_Assignment");
        var document = env.Host.Resources(Policy("""
            { "id": "Expiration_EndUser_Assignment", "maximumDuration": "PT4H" },
            { "id": "Enablement_EndUser_Assignment", "enabledRules": ["MultiFactorAuthentication"] },
            { "id": "Expiration_Admin_Assignment", "maximumDuration": "P90D" }
            """));

        var run = await env.Host.CreateAsync(document);

        Check.Succeeded(run);
        Check.NoWrites(run);
        var (path, body) = Assert.Single(env.Arm.Patches);
        Assert.Equal(RgPolicyPath, path);
        var patch = Assert.Single(Requests(env.Arm, "PATCH", RgPolicyPath));
        Assert.Equal("?api-version=2020-10-01", patch.Uri.Query);
        Assert.Equal(["properties"], body.Select(p => p.Key).ToArray());
        Assert.Equal(["rules"], body["properties"]!.AsObject().Select(p => p.Key).ToArray());
        var rules = body["properties"]!["rules"]!.AsArray().OfType<JsonObject>().ToList();
        Assert.Equal(["Expiration_EndUser_Assignment", "Expiration_Admin_Assignment"], rules.Select(r => r["id"]!.GetValue<string>()).ToArray());
        var endUser = rules[0];
        Assert.Equal("PT4H", endUser["maximumDuration"]!.GetValue<string>());
        Assert.True(endUser["isExpirationRequired"]!.GetValue<bool>());
        Assert.Equal("RoleManagementPolicyExpirationRule", endUser["ruleType"]!.GetValue<string>());
        Assert.Equal("EndUser", endUser["target"]!["caller"]!.GetValue<string>());
        Assert.Equal("Assignment", endUser["target"]!["level"]!.GetValue<string>());
        Assert.False(endUser["target"]!.AsObject().ContainsKey("targetObjects"), "a null member must not be sent");
        Assert.DoesNotContain("null", body.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal("P90D", rules[1]["maximumDuration"]!.GetValue<string>());
        Assert.Equal("Admin", rules[1]["target"]!["caller"]!.GetValue<string>());

        Assert.Equal("PT4H", env.Arm.Rule(Rg, Reader, "Expiration_EndUser_Assignment")["maximumDuration"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(approvalBefore, env.Arm.Rule(Rg, Reader, "Approval_EndUser_Assignment")), "an undeclared rule must not change");
        Assert.Equal("P365D", env.Arm.Rule(SubscriptionScope, Reader, "Expiration_Admin_Eligibility")["maximumDuration"]!.GetValue<string>());
        Assert.Equal("PT8H", env.Arm.Rule(SubscriptionScope, Reader, "Expiration_EndUser_Assignment")["maximumDuration"]!.GetValue<string>());

        Assert.Equal(ChangeType.NoChange, (await env.Host.WhatIfAsync(document)).Change(Name).Type);
    }

    [Fact]
    public async Task UpdateAsync_SendsNothing_WhenEveryDeclaredRuleAlreadyHasItsValue()
    {
        var env = Create();
        var whatIf = await env.Host.WhatIfAsync(env.Host.Resources(Policy("""{ "id": "Expiration_EndUser_Assignment", "maximumDuration": "PT4H" }""")));
        var change = whatIf.Change(Name);
        Assert.Equal(ChangeType.Modify, change.Type);

        // Another change landed between the plan and the apply.
        var landed = env.Arm.Rule(Rg, Reader, "Expiration_EndUser_Assignment");
        landed["maximumDuration"] = "PT4H";
        await env.Arm.CreateClient().PatchAsync(RgPolicyPath, "2020-10-01", new JsonObject { ["properties"] = new JsonObject { ["rules"] = new JsonArray(landed) } }, ProviderHost.Ct);
        env.Arm.Patches.Clear();
        var existing = await env.Provider.ReadAsync(Key(Rg), env.Host.Fake, ProviderHost.Ct);

        var applied = await env.Provider.UpdateAsync(whatIf.Plan.Instances.Single(), existing!, change, env.Host.Fake, ProviderHost.Ct);

        Assert.Empty(env.Arm.Patches);
        Assert.Equal(RgPolicyPath, applied.GraphId);
    }

    [Fact]
    public async Task ThePatchAnswer_IsTheLiveResult_WithoutAReRead()
    {
        var env = Create();
        env.Arm.PatchAnswer = answer =>
        {
            answer["properties"]!["displayName"] = "answered by the PATCH";
            return answer;
        };
        var whatIf = await env.Host.WhatIfAsync(env.Host.Resources(Policy("""{ "id": "Expiration_EndUser_Assignment", "maximumDuration": "PT4H" }""")));
        var existing = await env.Provider.ReadAsync(Key(Rg), env.Host.Fake, ProviderHost.Ct);
        int before = env.Arm.Requests.Count;

        var applied = await env.Provider.UpdateAsync(whatIf.Plan.Instances.Single(), existing!, whatIf.Change(Name), env.Host.Fake, ProviderHost.Ct);

        Assert.Equal("answered by the PATCH", applied.Live["properties"]!["displayName"]!.GetValue<string>());
        Assert.Equal(["PATCH"], env.Arm.Requests.Skip(before).Select(r => r.Method).ToArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnAnswerWithoutProperties_TriggersAReRead(bool emptyBody)
    {
        var env = Create();
        env.Arm.PatchAnswer = answer => emptyBody ? null : new JsonObject { ["id"] = answer["id"]!.GetValue<string>() };
        var whatIf = await env.Host.WhatIfAsync(env.Host.Resources(Policy("""{ "id": "Expiration_EndUser_Assignment", "maximumDuration": "PT4H" }""")));
        var existing = await env.Provider.ReadAsync(Key(Rg), env.Host.Fake, ProviderHost.Ct);
        int before = env.Arm.Requests.Count;

        var applied = await env.Provider.UpdateAsync(whatIf.Plan.Instances.Single(), existing!, whatIf.Change(Name), env.Host.Fake, ProviderHost.Ct);

        var after = env.Arm.Requests.Skip(before).ToList();
        Assert.Equal("PATCH", after[0].Method);
        Assert.Contains(after.Skip(1), r => r.Method == "GET" && r.Uri.AbsolutePath == RgPolicyPath);
        var rule = applied.Live["properties"]!["rules"]!.AsArray().OfType<JsonObject>().Single(r => r["id"]!.GetValue<string>() == "Expiration_EndUser_Assignment");
        Assert.Equal("PT4H", rule["maximumDuration"]!.GetValue<string>());
    }

    // ---- create / delete / orphans / permissions ----

    [Fact]
    public async Task CreateAsync_Throws_PimPolicyNotAvailable()
    {
        var env = Create();
        var whatIf = await env.Host.WhatIfAsync(env.Host.Resources(Policy("""{ "id": "Expiration_EndUser_Assignment" }""")));

        var error = await Assert.ThrowsAsync<ProviderException>(() => env.Provider.CreateAsync(whatIf.Plan.Instances.Single(), env.Host.Fake, ProviderHost.Ct));

        Assert.Equal(EngineDiagnosticCodes.PimPolicyNotAvailable, error.Code);
        Assert.Contains("never created", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteAsync_Throws_NotDeletable_WithTheRefusalText_AndNoOrphansAreListed()
    {
        var env = Create();
        var live = (await env.Provider.ReadAsync(Key(Rg), env.Host.Fake, ProviderHost.Ct))!;
        int before = env.Arm.Requests.Count;

        var refused = await Assert.ThrowsAsync<ProviderException>(() => env.Provider.DeleteAsync(Key(Rg), live, env.Host.Fake, ProviderHost.Ct));

        Assert.Equal(EngineDiagnosticCodes.NotDeletable, refused.Code);
        string? refusal = env.Provider.DescribeDeleteRefusal(Key(Rg), live);
        Assert.NotNull(refusal);
        Assert.Contains("cannot be deleted", refusal, StringComparison.Ordinal);
        Assert.Contains(refusal, refused.Message, StringComparison.Ordinal);
        Assert.Equal(OrphanScope.Collection, env.Provider.OrphanScope);
        Assert.Empty(await env.Provider.ListOrphanCandidatesAsync(new OrphanQuery(), env.Host.Fake, ProviderHost.Ct));
        Assert.Equal(before, env.Arm.Requests.Count);
    }

    [Fact]
    public async Task DeploymentDelete_KeepsThePolicy_AsNotDeletable_AndCompleteModeListsNoOrphans()
    {
        var env = Create();
        var document = env.Host.Resources(Policy("""{ "id": "Expiration_EndUser_Assignment" }"""));

        var result = await env.Host.DeleteDeclaredAsync(document);

        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal(DeletionStatus.Refused, outcome.Status);
        Assert.Equal(EngineDiagnosticCodes.NotDeletable, outcome.ErrorCode);
        Assert.True(result.Succeeded);
        Assert.Empty(env.Arm.Patches);
        var complete = await env.Host.WhatIfAsync(document, DeploymentMode.Complete);
        Assert.DoesNotContain(complete.Changes, c => c.IsOrphan && ResourceIdentifier.TypeEquals(c.ResourceType, Type));
    }

    [Fact]
    public async Task RequiredPermissions_AreEmpty_TheGrantIsAzureRbac()
    {
        var env = Create();
        var whatIf = await env.Host.WhatIfAsync(env.Host.Resources(Policy("""{ "id": "Expiration_EndUser_Assignment" }""")));

        Assert.Empty(env.Provider.RequiredPermissions(null));
        Assert.Empty(env.Provider.RequiredPermissions(whatIf.Plan.Instances.Single()));
    }

    // ---- required Azure roles (the what-if footer) ----

    private const string RoleList = "Owner, User Access Administrator or Role Based Access Control Administrator at ";

    [Fact]
    public async Task RequiredAzureRoles_NameTheRolesThatMayWriteThePolicy_AtTheDeclaredScope()
    {
        var env = Create();
        var whatIf = await env.Host.WhatIfAsync(env.Host.Resources(Policy("""{ "id": "Expiration_EndUser_Assignment" }""")));

        var role = Assert.Single(env.Provider.RequiredAzureRoles(whatIf.Plan.Instances.Single()));

        Assert.Equal(RoleList + "'" + Rg + "'", role);
    }

    [Fact]
    public async Task RequiredAzureRoles_NormaliseTheScope_LowerCaseSubscription_WithoutTheTrailingSlash()
    {
        var env = Create();
        var instance = await InstanceFactory.WithPropertiesAsync(
            env.Host,
            Policy("""{ "id": "Expiration_EndUser_Assignment" }"""),
            "{ \"scope\": \" /subscriptions/" + Subscription.ToUpperInvariant() + "/resourceGroups/rg-earm/ \", \"roleDefinitionId\": \"" + Reader + "\" }");

        Assert.Equal([RoleList + "'" + Rg + "'"], env.Provider.RequiredAzureRoles(instance));
    }

    [Fact]
    public async Task RequiredAzureRoles_KeepAScopeThatIsNotAnAzureScope_AsDeclared()
    {
        var env = Create();
        var instance = await InstanceFactory.WithPropertiesAsync(env.Host, Policy("""{ "id": "Expiration_EndUser_Assignment" }"""), "{ \"scope\": \"/not/an/azure/scope\" }");

        Assert.Equal([RoleList + "'/not/an/azure/scope'"], env.Provider.RequiredAzureRoles(instance));
    }

    [Fact]
    public async Task RequiredAzureRoles_WithoutAnInstanceOrAScope_SayTheDeclaredScope()
    {
        var env = Create();
        var withoutScope = await InstanceFactory.WithPropertiesAsync(env.Host, Policy("""{ "id": "Expiration_EndUser_Assignment" }"""), "{ \"roleDefinitionId\": \"" + Reader + "\" }");

        Assert.Equal([RoleList + "'the declared scope'"], env.Provider.RequiredAzureRoles(null));
        Assert.Equal([RoleList + "'the declared scope'"], env.Provider.RequiredAzureRoles(withoutScope));
        Assert.Empty(((IResourceManagerProvider)new DefaultResourceManagerProvider()).RequiredAzureRoles(null));
    }

    /// <summary>An <see cref="IResourceManagerProvider"/> that keeps the interface's default (no Azure role).</summary>
    private sealed class DefaultResourceManagerProvider : IResourceManagerProvider;

    // ---- registries ----

    [Fact]
    public async Task TheOfflineRegistry_ServesTheType_ButAnyResourceManagerCallFails_NamingTheFix()
    {
        var provider = Assert.IsType<AzureRoleManagementPoliciesProvider>(ProviderDiscovery.CreateRegistry(ResourceTypeCatalog.Default).Get(Type));
        var fake = new FakeGraphClient();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ReadAsync(Key(Rg), fake, ProviderHost.Ct));

        Assert.Contains("without a Resource Manager client", error.Message, StringComparison.Ordinal);
        Assert.Contains("ProviderDiscovery.CreateRegistry(catalog, settle, resourceManager)", error.Message, StringComparison.Ordinal);
        Assert.Empty(fake.Requests);
        Assert.IsAssignableFrom<IResourceManagerProvider>(provider);
    }

    [Fact]
    public async Task TheOfflineProvider_FailsTheRoleLookup_InPrepare()
    {
        var host = new ProviderHost();

        var run = await host.CreateAsync(host.Resources(Policy("""{ "id": "Expiration_EndUser_Assignment" }""")));

        Assert.False(run.Succeeded, "an offline registry cannot resolve a role name");
        Check.NoWrites(run);
    }

    [Fact]
    public async Task CreateRegistry_WithAResourceManagerClient_InjectsIt()
    {
        var arm = new FakeAzureResourceManager();
        arm.AddPolicy(Rg, Reader, PolicyName);

        var registry = ProviderDiscovery.CreateRegistry(ResourceTypeCatalog.Default, ReplicationWait.Default, arm.CreateClient());
        var provider = Assert.IsType<AzureRoleManagementPoliciesProvider>(registry.Get(Type));
        var live = await provider.ReadAsync(Key(Rg), new FakeGraphClient(), ProviderHost.Ct);

        Assert.Equal(RgPolicyPath, live!.GraphId);
        Assert.NotEmpty(arm.Requests);
        Assert.Throws<ArgumentNullException>(() => ProviderDiscovery.CreateRegistry(ResourceTypeCatalog.Default, ReplicationWait.Default, null!));
    }

    // ---- management group scopes, and the eligibilities a policy governs ----

    private const string Mg = "/providers/Microsoft.Management/managementGroups/mg-platform";
    private const string EligibilityName = "9c8d7e6f-0000-4000-8000-0000000000e1";
    private const string Principal = "7d3c2b1a-0000-4000-8000-0000000000c1";

    /// <summary>The host of <see cref="Create"/>, with the eligibility provider on the same fake ARM.</summary>
    private static Env CreateWithEligibilities()
    {
        var env = Create();
        env.Host.Registry.Register(new AzureRoleEligibilityScheduleRequestsProvider(env.Host.Catalog, env.Arm.CreateClient()), replace: true);
        return env;
    }

    private static string Eligibility(string scope, string schedule = ", \"scheduleInfo\": { \"expiration\": { \"type\": \"AfterDuration\", \"duration\": \"P180D\" } }")
    {
        return Templates.Resource(AzureRoleEligibilityScheduleRequestsProvider.ResourceType, EligibilityName, "{ \"scope\": \"" + scope + "\", \"role\": \"Reader\", \"principalId\": \"" + Principal + "\"" + schedule + " }");
    }

    [Fact]
    public async Task APolicyAtAManagementGroup_IsFoundThroughTheTenantLevelRoleId()
    {
        var env = Create();
        string path = env.Arm.AddPolicy(Mg, Reader);

        var run = await env.Host.WhatIfAsync(env.Host.Resources(Policy("""{ "id": "Expiration_EndUser_Assignment", "maximumDuration": "PT8H" }""", scope: Mg + "/")));

        var change = run.Change(Name);
        Assert.Equal(ChangeType.NoChange, change.Type);
        Assert.Equal(path, change.GraphId);
        var list = Assert.Single(Requests(env.Arm, "GET", Mg + "/providers/Microsoft.Authorization/roleManagementPolicyAssignments"));
        Assert.Contains("$filter=roleDefinitionId eq '/providers/Microsoft.Authorization/roleDefinitions/" + Reader + "'", Query(list), StringComparison.Ordinal);
        Assert.Single(Requests(env.Arm, "GET", Mg + "/providers/Microsoft.Authorization/roleDefinitions"));
    }

    [Fact]
    public async Task AManagementGroupPolicyArmDoesNotListYet_WithoutAnEligibility_IsPimPolicyNotAvailable_NamingTheCauseAndTheFix()
    {
        var env = Create();
        env.Arm.AddPolicyOnFirstRequest(Mg, Reader);

        var change = (await env.Host.WhatIfAsync(env.Host.Resources(Policy("""{ "id": "Expiration_EndUser_Assignment", "maximumDuration": "PT4H" }""", scope: Mg)))).Change(Name);

        Assert.Equal(EngineDiagnosticCodes.PimPolicyNotAvailable, change.UnsupportedCode);
        Assert.Contains("at a management group, ARM creates a role's PIM policy only with the first eligibility or assignment request", change.UnsupportedReason, StringComparison.Ordinal);
        Assert.Contains("no azureRoleEligibilityScheduleRequests of this deployment", change.UnsupportedReason, StringComparison.Ordinal);
        Assert.False(change.RetryAtEndOfRun);
    }

    [Fact]
    public async Task AManagementGroupPolicyArmDoesNotListYet_WaitsForTheEligibilityOfTheDeployment_AndIsAppliedAtTheEndOfTheRun()
    {
        var env = CreateWithEligibilities();
        env.Arm.AddPolicyOnFirstRequest(Mg, Reader);
        var document = env.Host.Resources(Policy("""{ "id": "Expiration_EndUser_Assignment", "maximumDuration": "PT4H" }""", scope: Mg) + ", " + Eligibility(Mg));

        var whatIf = await env.Host.WhatIfAsync(document);
        var deferred = whatIf.Change(Name);
        Assert.Equal(ChangeType.Ignore, deferred.Type);
        Assert.True(deferred.RetryAtEndOfRun);
        Assert.Contains(deferred.Reasons, r => r.Contains("the policy is applied at the end of this run", StringComparison.Ordinal));
        Assert.Equal(ChangeType.Create, whatIf.Change(EligibilityName).Type);

        var run = await env.Host.CreateAsync(document);

        Assert.True(run.Succeeded, run.Result?.Diagnostics.FirstOrDefault()?.Message);
        Assert.True(env.Arm.HasPolicy(Mg, Reader));
        Assert.Single(env.Arm.EligibilityRequests);
        var patch = Assert.Single(env.Arm.Patches);
        Assert.StartsWith(Mg + "/providers/Microsoft.Authorization/roleManagementPolicies/", patch.Path, StringComparison.Ordinal);
        Assert.Equal("PT4H", env.Arm.Rule(Mg, Reader, "Expiration_EndUser_Assignment")["maximumDuration"]!.GetValue<string>());
    }

    [Fact]
    public async Task APolicy_IsAppliedBeforeTheEligibilityOfTheSameScopeAndRole_WithoutADependsOn()
    {
        var env = CreateWithEligibilities();
        var document = env.Host.Resources(Eligibility(Rg) + ", " + Policy("""{ "id": "Expiration_EndUser_Assignment", "maximumDuration": "PT4H" }"""));

        var run = await env.Host.CreateAsync(document);

        Assert.True(run.Succeeded, run.Result?.Diagnostics.FirstOrDefault()?.Message);
        var writes = env.Arm.Requests.Where(r => r.Method is "PATCH" or "PUT").Select(r => r.Method).ToList();
        Assert.Equal(["PATCH", "PUT"], writes);
    }

    [Fact]
    public async Task AnEligibilityThatAlreadyDependsOnThePolicy_IsAppliedAfterIt_Once()
    {
        var env = CreateWithEligibilities();
        string eligibility = Templates.Resource(
            AzureRoleEligibilityScheduleRequestsProvider.ResourceType,
            EligibilityName,
            "{ \"scope\": \"" + Rg + "\", \"role\": \"Reader\", \"principalId\": \"" + Principal + "\" }",
            extra: Templates.DependsOn(Type, Name));
        var document = env.Host.Resources(eligibility + ", " + Policy("""{ "id": "Expiration_Admin_Eligibility", "isExpirationRequired": false }"""));

        var run = await env.Host.CreateAsync(document);

        Assert.True(run.Succeeded, run.Result?.Diagnostics.FirstOrDefault()?.Message);
        Assert.Equal(["PATCH", "PUT"], env.Arm.Requests.Where(r => r.Method is "PATCH" or "PUT").Select(r => r.Method).ToList());
    }

    [Fact]
    public async Task AnEligibilityOfAnotherRoleOrScope_IsNotGoverned()
    {
        var env = CreateWithEligibilities();
        var provider = env.Provider;
        var plan = await env.Host.ExpandAsync(env.Host.Resources(Policy("""{ "id": "Expiration_EndUser_Assignment" }""") + ", " + Eligibility(SubscriptionScope)));
        var policy = plan.Instances.Single(i => ResourceIdentifier.TypeEquals(i.Type, Type));

        Assert.Empty(provider.AppliesBefore(policy, plan.Instances));
    }

    [Fact]
    public void TheProvider_IsAppliedInPhase4_AndDeletedInOrder2()
    {
        Assert.Equal(4, ApplyPhases.Of(Type));
        Assert.Equal(2, ApplyPhases.DeleteOrderOf(Type));
    }
}
