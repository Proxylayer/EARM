using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Core.Secrets;
using EArm.Engine.Execution;
using EArm.Engine.Providers;
using EArm.Graph;
using EArm.Providers.Tests.Infrastructure;
using EArm.TestSupport;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// Review 1D / I-01 (hostile input): the <c>--allow-ca-enforce</c> guard and the lockout lint judge the REVEALED values. A
/// <c>securestring</c> parameter used to hide <c>state: enabled</c>, <c>includeUsers: All</c> or <c>block</c> from them (secret leaves never
/// matched a literal), so <c>create -y</c> wrote an enforced policy without the flag. Also blank entries are not exclusions, state matching is
/// case-insensitive, and the guard runs again on the final request body.
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
    public async Task SecretState_Enabled_IsRefusedWithoutTheFlag_OnTheWhatIfPath_AndTheExecutorPath()
    {
        var host = NewHost();
        var document = host.Resources(Policy("\"[parameters('s')]\"", "[\"" + Group + "\"]", string.Empty, "[\"mfa\"]"), parameters: Parameters("s"), values: [("s", "enabled")]);

        var whatIf = await host.WhatIfAsync(document);
        var row = Assert.Single(whatIf.Plan.Changes);
        Assert.True(row.HasError);
        Assert.Contains("--allow-ca-enforce", row.UnsupportedReason, StringComparison.Ordinal);

        var run = await host.CreateAsync(document);
        Assert.False(run.Succeeded);
        Check.NoWrites(run);
        Assert.Contains("--allow-ca-enforce", run.Errors, StringComparison.Ordinal);
        Assert.Empty(host.Directory.Objects("/identity/conditionalAccess/policies"));
    }

    [Theory]
    [InlineData("Enabled")]
    [InlineData("ENABLED")]
    [InlineData(" enabled ")]
    public async Task SecretState_InAnyCase_IsRefused(string value)
    {
        var host = NewHost();
        var run = await host.CreateAsync(host.Resources(Policy("\"[parameters('s')]\"", "[\"" + Group + "\"]", string.Empty, "[\"mfa\"]"), parameters: Parameters("s"), values: [("s", value)]));

        Assert.False(run.Succeeded);
        Check.NoWrites(run);
        Assert.Contains("--allow-ca-enforce", run.Errors, StringComparison.Ordinal);
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
    public async Task TheFinalRequestBody_IsGuardedAgainAtTheProvider()
    {
        var host = NewHost();
        var plan = await host.WhatIfAsync(host.Resources(Policy("\"disabled\"", "[\"" + Group + "\"]", string.Empty, "[\"mfa\"]")));
        var instance = plan.Plan.Instances.Single();
        instance.Properties!["state"] = SecretJson.CreateString("enabled");
        var provider = host.Provider<ConditionalAccessPoliciesProvider>(PolType);
        provider.PlanOptions = PlanOptions.Default;

        var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.CreateAsync(instance, host.Fake, ProviderHost.Ct));

        Assert.Equal(ConditionalAccessPoliciesProvider.PolicyRejected, ex.Code);
        Assert.Empty(host.Fake.Writes);
        Assert.DoesNotContain("enabled\"", ex.Message.Replace("--allow-ca-enforce", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void FinalBodyProblems_JudgeTheRevealedBody()
    {
        var body = (JsonObject)JsonNode.Parse("""{ "state": "Enabled", "conditions": { "users": { "includeUsers": ["All"] }, "applications": { "includeApplications": ["All"] }, "clientAppTypes": ["all"] }, "grantControls": { "operator": "OR", "builtInControls": ["block"] } }""")!;

        var create = ConditionalAccessPoliciesRules.FinalBodyProblems(body, GraphApi.V1, allowCaEnforce: false, isCreate: true);
        var patch = ConditionalAccessPoliciesRules.FinalBodyProblems(body, GraphApi.V1, allowCaEnforce: false, isCreate: false);
        var allowed = ConditionalAccessPoliciesRules.FinalBodyProblems(body, GraphApi.V1, allowCaEnforce: true, isCreate: false);

        Assert.Contains(create, p => p.Contains("--allow-ca-enforce", StringComparison.Ordinal));
        Assert.Contains(create, p => p.Contains("lockout risk", StringComparison.Ordinal));
        Assert.Single(patch);
        Assert.Empty(allowed);
    }

    [Fact]
    public void Problems_RevealSecretLeaves_ForTheCheckOnly()
    {
        var properties = (JsonObject)JsonNode.Parse("""{ "conditions": { "users": { "includeUsers": ["All"] }, "applications": { "includeApplications": ["All"] }, "clientAppTypes": ["all"] }, "grantControls": { "builtInControls": ["x"] } }""")!;
        properties["state"] = SecretJson.CreateString("enabled");
        properties["grantControls"]!["builtInControls"]![0] = SecretJson.CreateString("block");

        var problems = ConditionalAccessPoliciesRules.Problems(properties, GraphApi.V1, allowCaEnforce: false);

        Assert.Contains(problems, p => p.Contains("--allow-ca-enforce", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("lockout risk", StringComparison.Ordinal));
        Assert.True(SecretJson.IsSecret(properties["state"]), "the evaluated object is not modified");
        Assert.All(problems, p => Assert.DoesNotContain("\"enabled\"", p, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUpdateToAnEnforcedState_ThroughASecret_IsRefusedToo()
    {
        var host = NewHost();
        string create = Policy("\"disabled\"", "[\"" + Group + "\"]", string.Empty, "[\"mfa\"]");
        Check.Succeeded(await host.CreateAsync(host.Resources(create)));
        var document = host.Resources(Policy("\"[parameters('s')]\"", "[\"" + Group + "\"]", string.Empty, "[\"mfa\"]"), parameters: Parameters("s"), values: [("s", "enabled")]);

        var run = await host.CreateAsync(document);

        Assert.False(run.Succeeded);
        Check.NoWrites(run);
        Assert.Equal("disabled", host.Directory.Objects("/identity/conditionalAccess/policies").Single()["state"]!.GetValue<string>());
    }
}
