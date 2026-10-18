using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Core.Secrets;
using EArm.Engine.Providers;
using EArm.Graph;
using EArm.Providers.Tests.Infrastructure;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// Review 1D / I-01 (hostile input): the <c>--allow-ca-enforce</c> rule and the lockout lint judge the REVEALED values. A
/// <c>securestring</c> parameter used to hide <c>state: enabled</c>, <c>includeUsers: All</c> or <c>block</c> from them (secret leaves never
/// matched a literal), so <c>create -y</c> wrote an enforced policy without the flag. Also blank entries are not exclusions, state matching is
/// case-insensitive, and the rule runs again on the final request body. Without the flag an enforce request is held back (report-only on
/// create, live state kept on update), never written.
/// </summary>
public sealed class Review1D_ConditionalAccessGuardTests
{
    private const string PolType = ConditionalAccessPoliciesProvider.ResourceType;
    private const string Group = "aaaaaaaa-1111-2222-3333-444444444444";

    private static string Parameters(params string[] names)
    {
        return string.Join(", ", names.Select(Templates.SecureParameter));
    }

    private static string Policy(string state, string includeUsers, string excludeUsers, string controls, string includeApps = "[\"All\"]")
    {
        string stateJson = state.Length == 0 ? string.Empty : "\"state\": " + state + ", ";
        string exclude = excludeUsers.Length == 0 ? string.Empty : ", \"excludeUsers\": " + excludeUsers;
        return Templates.Resource(PolType, "CA-Guard", "{ " + stateJson + "\"conditions\": { \"users\": { \"includeUsers\": " + includeUsers + exclude + " }, \"applications\": { \"includeApplications\": " + includeApps + " } }, \"grantControls\": { \"builtInControls\": " + controls + " } }");
    }

    private static ProviderHost NewHost(bool allow = false)
    {
        return new ProviderHost { AllowCaEnforce = allow };
    }

    // ---- the bypasses ----

    [Fact]
    public async Task SecretState_Enabled_IsHeldBackWithoutTheFlag_OnTheWhatIfPath_AndTheExecutorPath()
    {
        var host = NewHost();
        var document = host.Resources(Policy("\"[parameters('s')]\"", "[\"" + Group + "\"]", string.Empty, "[\"mfa\"]"), parameters: Parameters("s"), values: [("s", "enabled")]);

        var whatIf = await host.WhatIfAsync(document);
        var row = Assert.Single(whatIf.Plan.Changes);
        Assert.False(row.HasError);
        string reason = Assert.Single(row.Reasons, r => r.StartsWith(ProviderWarnings.ConditionalAccessEnforcementNotAllowed + ":", StringComparison.Ordinal));
        Assert.Contains("secure value", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("declared enabled", reason, StringComparison.Ordinal);

        var run = await host.CreateAsync(document);
        Check.Succeeded(run);
        Assert.Equal(ConditionalAccessPoliciesRules.DefaultState, Check.Body(Assert.Single(run.Writes))["state"]!.GetValue<string>());
        Assert.Equal(ConditionalAccessPoliciesRules.DefaultState, host.Directory.Objects("/identity/conditionalAccess/policies").Single()["state"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("Enabled")]
    [InlineData("ENABLED")]
    [InlineData(" enabled ")]
    public async Task SecretState_InAnyCase_IsHeldBack(string value)
    {
        var host = NewHost();
        var run = await host.CreateAsync(host.Resources(Policy("\"[parameters('s')]\"", "[\"" + Group + "\"]", string.Empty, "[\"mfa\"]"), parameters: Parameters("s"), values: [("s", value)]));

        Check.Succeeded(run);
        Assert.Equal(ConditionalAccessPoliciesRules.DefaultState, Check.Body(Assert.Single(run.Writes))["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task SecretState_Enabled_IsAcceptedWithTheFlag_AndWrittenRevealed()
    {
        var host = NewHost(allow: true);

        var run = await host.CreateAsync(host.Resources(Policy("\"[parameters('s')]\"", "[\"" + Group + "\"]", string.Empty, "[\"mfa\"]"), parameters: Parameters("s"), values: [("s", "enabled")]));

        Check.Succeeded(run);
        Assert.Equal("enabled", Check.Body(Assert.Single(run.Writes))["state"]!.GetValue<string>());
        Assert.Equal("enabled", host.Directory.Objects("/identity/conditionalAccess/policies").Single()["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task LockoutLint_SeesSecretIncludeAndControlValues()
    {
        var host = NewHost();
        var document = host.Resources(
            Policy("\"disabled\"", "[\"[parameters('a')]\"]", string.Empty, "[\"[parameters('b')]\"]"),
            parameters: Parameters("a", "b"),
            values: [("a", "All"), ("b", "block")]);

        var whatIf = await host.WhatIfAsync(document);

        var row = Assert.Single(whatIf.Plan.Changes);
        Assert.True(row.HasError);
        Assert.Contains("lockout risk", row.UnsupportedReason, StringComparison.Ordinal);
        var run = await host.CreateAsync(document);
        Assert.False(run.Succeeded);
        Check.NoWrites(run);
    }

    [Theory]
    [InlineData("[\"\"]")]
    [InlineData("[\"  \"]")]
    [InlineData("[\"\", \" \"]")]
    public async Task BlankExcludeUsers_IsNotABreakGlassExclusion(string exclude)
    {
        var host = NewHost();

        var whatIf = await host.WhatIfAsync(host.Resources(Policy("\"disabled\"", "[\"All\"]", exclude, "[\"block\"]")));

        var row = Assert.Single(whatIf.Plan.Changes);
        Assert.True(row.HasError);
        Assert.Contains("lockout risk", row.UnsupportedReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARealExclusion_StillSatisfiesTheLockoutLint()
    {
        var host = NewHost();

        var run = await host.CreateAsync(host.Resources(Policy("\"disabled\"", "[\"All\"]", "[\"" + Group + "\"]", "[\"block\"]")));

        Check.Succeeded(run);
    }

    [Fact]
    public async Task ABlankIncludeIsNotAnInclude()
    {
        var host = NewHost();

        var whatIf = await host.WhatIfAsync(host.Resources(Policy("\"disabled\"", "[\"\"]", string.Empty, "[\"mfa\"]")));

        var row = Assert.Single(whatIf.Plan.Changes);
        Assert.True(row.HasError);
        Assert.Contains("conditions.users must include at least one user", row.UnsupportedReason, StringComparison.Ordinal);
    }

    // ---- defence in depth ----

    [Fact]
    public async Task TheFinalRequestBody_IsHeldBackAgainAtTheProvider()
    {
        var host = NewHost();
        var plan = await host.WhatIfAsync(host.Resources(Policy("\"disabled\"", "[\"" + Group + "\"]", string.Empty, "[\"mfa\"]")));
        var instance = plan.Plan.Instances.Single();
        instance.Properties!["state"] = SecretJson.CreateString("enabled");
        var provider = host.Provider<ConditionalAccessPoliciesProvider>(PolType);
        provider.PlanOptions = PlanOptions.Default;

        var applied = await provider.CreateAsync(instance, host.Fake, ProviderHost.Ct);

        Assert.Equal(ConditionalAccessPoliciesRules.DefaultState, Check.Body(Assert.Single(host.Fake.Writes))["state"]!.GetValue<string>());
        var warning = Assert.Single(applied.Warnings);
        Assert.Equal(ProviderWarnings.ConditionalAccessEnforcementNotAllowed, warning.Code);
        Assert.DoesNotContain("declared enabled", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FinalBodyProblems_JudgeTheRevealedCreateBody_AndNeverTheState()
    {
        var body = (JsonObject)JsonNode.Parse("""{ "state": "Enabled", "conditions": { "users": { "includeUsers": ["All"] }, "applications": { "includeApplications": ["All"] }, "clientAppTypes": ["all"] }, "grantControls": { "operator": "OR", "builtInControls": ["block"] } }""")!;

        var create = ConditionalAccessPoliciesRules.FinalBodyProblems(body, GraphApi.V1, isCreate: true);
        var patch = ConditionalAccessPoliciesRules.FinalBodyProblems(body, GraphApi.V1, isCreate: false);

        Assert.DoesNotContain(create, p => p.Contains("--allow-ca-enforce", StringComparison.Ordinal));
        Assert.Contains(create, p => p.Contains("lockout risk", StringComparison.Ordinal));
        Assert.Empty(patch);
    }

    [Fact]
    public void Problems_RevealSecretLeaves_ForTheCheckOnly()
    {
        var properties = (JsonObject)JsonNode.Parse("""{ "conditions": { "users": { "includeUsers": ["All"] }, "applications": { "includeApplications": ["All"] }, "clientAppTypes": ["all"] }, "grantControls": { "builtInControls": ["x"] } }""")!;
        properties["state"] = SecretJson.CreateString("enabled");
        properties["grantControls"]!["builtInControls"]![0] = SecretJson.CreateString("block");

        var problems = ConditionalAccessPoliciesRules.Problems(properties, GraphApi.V1);

        Assert.DoesNotContain(problems, p => p.Contains("--allow-ca-enforce", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("lockout risk", StringComparison.Ordinal));
        Assert.True(SecretJson.IsSecret(properties["state"]), "the evaluated object is not modified");
        Assert.All(problems, p => Assert.DoesNotContain("\"enabled\"", p, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUpdateToAnEnforcedState_ThroughASecret_KeepsTheLiveState()
    {
        var host = NewHost();
        string create = Policy("\"disabled\"", "[\"" + Group + "\"]", string.Empty, "[\"mfa\"]");
        Check.Succeeded(await host.CreateAsync(host.Resources(create)));
        var document = host.Resources(Policy("\"[parameters('s')]\"", "[\"" + Group + "\"]", string.Empty, "[\"mfa\"]"), parameters: Parameters("s"), values: [("s", "enabled")]);

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        Check.NoWrites(run);
        Assert.Equal("disabled", host.Directory.Objects("/identity/conditionalAccess/policies").Single()["state"]!.GetValue<string>());
    }
}
