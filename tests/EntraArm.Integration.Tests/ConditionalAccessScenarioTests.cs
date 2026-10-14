using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EntraArm.Az;
using Xunit;

namespace EntraArm.Integration.Tests;

/// <summary>
/// Named locations, Conditional Access policies (report-only), authentication strengths and authentication contexts.
/// Named locations need only the tenant; everything that writes a policy, strength or context needs
/// <c>ENTRAARM_IT_ALLOW_CA=1</c>. No test ever enforces a policy.
/// </summary>
[Collection(RealTenantCollection.Name)]
public sealed class ConditionalAccessScenarioTests : IntegrationTestBase
{
    private const string NamedLocations = "/identity/conditionalAccess/namedLocations";
    private const string Policies = "/identity/conditionalAccess/policies";
    private const string Strengths = "/policies/authenticationStrengthPolicies";
    private const string Contexts = "/identity/conditionalAccess/authenticationContextClassReferences";

    public ConditionalAccessScenarioTests(IntegrationFixture fixture)
        : base(fixture)
    {
    }

    [Fact]
    public async Task It_NamedLocations_IpAndCountryRoundTrip()
    {
        Fixture.Require();
        var run = Fixture.NewRun("locations", "named-locations.entra.json");
        string ipName = Fixture.Name("nl-ip");
        string countryName = Fixture.Name("nl-country");

        AssertCreated(await Fixture.Cli.CreateAsync(run, Ct), "create");

        var ip = await FindOneAsync(NamedLocations, "displayName", ipName);
        var country = await FindOneAsync(NamedLocations, "displayName", countryName);
        Assert.NotNull(ip);
        Assert.NotNull(country);
        Assert.Equal("#microsoft.graph.ipNamedLocation", ip.Text("@odata.type"));
        Assert.True((bool?)ip["isTrusted"] == true, "The IP location is not trusted.");
        var ranges = ip["ipRanges"]!.AsArray().OfType<JsonObject>().Select(r => r.Text("cidrAddress")).ToList();
        Assert.Contains("203.0.113.0/24", ranges);
        Assert.Contains("198.51.100.0/24", ranges);
        Assert.Equal("#microsoft.graph.countryNamedLocation", country.Text("@odata.type"));
        Assert.Equal(["GB", "IE"], country.Strings("countriesAndRegions").Order(StringComparer.Ordinal).ToArray());

        AssertNoChanges(await Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes"), "what-if after create");

        // Drift: the trust flag is flipped behind EntraARM's back.
        await Graph.PatchAsync(new GraphRequest($"{NamedLocations}/{ip.RequireId()}"), new JsonObject { ["@odata.type"] = "#microsoft.graph.ipNamedLocation", ["isTrusted"] = false }, Ct);
        AssertChange(await Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes"), "/namedLocations/" + ipName, "Modify", "what-if after drift");
        AssertCreated(await Fixture.Cli.CreateAsync(run, Ct), "create after drift");
        AssertNoChanges(await Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes"), "what-if after repair");

        // A template change: another range replaces the first one.
        var changed = run.WithParameter("cidr", "192.0.2.0/24");
        AssertChange(await Fixture.Cli.WhatIfAsync(changed, Ct, "--exit-code-on-changes"), "/namedLocations/" + ipName, "Modify", "what-if with another cidr");
        AssertCreated(await Fixture.Cli.CreateAsync(changed, Ct), "create with another cidr");
        var updated = await FindOneAsync(NamedLocations, "displayName", ipName);
        var updatedRanges = updated!["ipRanges"]!.AsArray().OfType<JsonObject>().Select(r => r.Text("cidrAddress")).ToList();
        Assert.Contains("192.0.2.0/24", updatedRanges);
        Assert.DoesNotContain("203.0.113.0/24", updatedRanges);

        AssertExit(0, await Fixture.Cli.DeleteAllAsync(run.Name, Ct), "delete --delete-resources");
        Assert.Null(await FindOneAsync(NamedLocations, "displayName", ipName));
        Assert.Null(await FindOneAsync(NamedLocations, "displayName", countryName));
    }

    [Fact]
    public async Task It_ConditionalAccess_ReportOnlyPolicyWithLocationAndGroup()
    {
        Fixture.Require(Needs.ConditionalAccess);
        var run = Fixture.NewRun("capolicy", "ca-policy.entra.json");
        string policyName = Fixture.Name("ca-policy");

        AssertCreated(await Fixture.Cli.CreateAsync(run, Ct), "create");

        var group = await GroupByNameAsync(Fixture.Name("ca-grp"), "id");
        var location = await FindOneAsync(NamedLocations, "displayName", Fixture.Name("ca-loc"));
        var policy = await FindOneAsync(Policies, "displayName", policyName);
        Assert.NotNull(group);
        Assert.NotNull(location);
        Assert.NotNull(policy);
        Assert.Equal("enabledForReportingButNotEnforced", policy.Text("state"));
        Assert.Contains(group.RequireId(), policy.Nested("conditions.users")!.Strings("includeGroups"));
        Assert.Contains(location.RequireId(), policy.Nested("conditions.locations")!.Strings("excludeLocations"));
        Assert.Contains("mfa", policy.Nested("grantControls")!.Strings("builtInControls"));

        AssertNoChanges(await Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes"), "what-if after create");

        // Drift: somebody disables the policy.
        await Graph.PatchAsync(new GraphRequest($"{Policies}/{policy.RequireId()}"), new JsonObject { ["state"] = "disabled" }, Ct);
        AssertChange(await Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes"), "/policies/" + policyName, "Modify", "what-if after drift");
        AssertCreated(await Fixture.Cli.CreateAsync(run, Ct), "create after drift");
        Assert.Equal("enabledForReportingButNotEnforced", (await FindOneAsync(Policies, "displayName", policyName))!.Text("state"));

        // The enforce guard: state 'enabled' is refused without --allow-ca-enforce and the live policy stays report-only.
        var enforced = run.WithParameter("policyState", "enabled");
        var guardedPreview = await Fixture.Cli.WhatIfAsync(enforced, Ct);
        var policyRow = guardedPreview.Changes.FirstOrDefault(c => ((string?)c["resourceId"])?.EndsWith("/policies/" + policyName, StringComparison.OrdinalIgnoreCase) == true);
        Assert.NotNull(policyRow);
        Assert.True(
            ((string?)policyRow["unsupportedReason"] ?? string.Empty).Contains("--allow-ca-enforce", StringComparison.Ordinal),
            "The what-if row of an 'enabled' policy does not name --allow-ca-enforce: " + policyRow.ToJsonString());

        var refused = await Fixture.Cli.CreateAsync(enforced, Ct);
        Assert.NotEqual(0, refused.ExitCode);
        Assert.Contains("--allow-ca-enforce", refused.Combined, StringComparison.Ordinal);
        Assert.Equal("enabledForReportingButNotEnforced", (await FindOneAsync(Policies, "displayName", policyName))!.Text("state"));

        AssertExit(0, await Fixture.Cli.DeleteAllAsync(run.Name, Ct), "delete --delete-resources");
        Assert.Null(await FindOneAsync(Policies, "displayName", policyName));
        Assert.Null(await FindOneAsync(NamedLocations, "displayName", Fixture.Name("ca-loc")));
    }

    [Fact]
    public async Task It_ConditionalAccess_StrengthAndContextLifecycleWithCompleteModeDeleteOrder()
    {
        Fixture.Require(Needs.ConditionalAccess);
        string contextId = await Fixture.AllocateContextIdAsync(Ct);
        var run = Fixture.NewRun("castrength", "ca-strength-context.entra.json").WithParameter("contextId", contextId);
        string strengthName = Fixture.Name("strength");
        string policyName = Fixture.Name("ctx-policy");

        var created = await Fixture.Cli.CreateAsync(run, Ct);
        AssertCreated(created, "create");

        var strength = await FindOneAsync(Strengths, "displayName", strengthName);
        Assert.NotNull(strength);
        Assert.Equal("custom", strength.Text("policyType"));
        Assert.Equal(["fido2", "windowsHelloForBusiness"], strength.Strings("allowedCombinations").Order(StringComparer.OrdinalIgnoreCase).ToArray());
        var context = await Graph.GetObjectAsync(new GraphRequest($"{Contexts}/{contextId}"), Ct);
        Assert.NotNull(context);
        Assert.Equal(Fixture.Name("ctx"), context.Text("displayName"));
        Assert.True((bool?)context["isAvailable"] == true, "The context is not available.");
        var policy = await FindOneAsync(Policies, "displayName", policyName);
        Assert.NotNull(policy);
        Assert.Equal(strength.RequireId(), policy.Nested("grantControls.authenticationStrength")!.Text("id"));
        Assert.Contains(contextId, policy.Nested("conditions.applications")!.Strings("includeAuthenticationContextClassReferences"));

        AssertNoChanges(await Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes"), "what-if after create");

        // Lifecycle: a changed context description and a changed set of combinations.
        var changed = run.WithParameter("contextDescription", "changed by the integration test");
        AssertChange(await Fixture.Cli.WhatIfAsync(changed, Ct, "--exit-code-on-changes"), "/authenticationContextClassReferences/" + contextId, "Modify", "what-if with another description");
        AssertCreated(await Fixture.Cli.CreateAsync(changed, Ct), "create with another description");
        Assert.Equal("changed by the integration test", (await Graph.GetObjectAsync(new GraphRequest($"{Contexts}/{contextId}"), Ct))!.Text("description"));

        var narrowed = changed.WithParameter("combinations", "[\"fido2\"]");
        AssertChange(await Fixture.Cli.WhatIfAsync(narrowed, Ct, "--exit-code-on-changes"), "/authenticationStrengths/" + strengthName, "Modify", "what-if with fewer combinations");
        AssertCreated(await Fixture.Cli.CreateAsync(narrowed, Ct), "create with fewer combinations");
        Assert.Equal(["fido2"], (await FindOneAsync(Strengths, "displayName", strengthName))!.Strings("allowedCombinations").ToArray());
        AssertNoChanges(await Fixture.Cli.WhatIfAsync(narrowed, Ct, "--exit-code-on-changes"), "what-if after the lifecycle");

        // Complete mode with nothing declared: the policy must go before the strength and the context it references.
        var emptyRun = narrowed.WithTemplate(TemplateEditor.Write(Path.Combine(Fixture.ScratchDirectory, "variants"), "castrength-empty.entra.json", TemplateEditor.Empty(TemplateEditor.Load(run.TemplateFile))));
        var complete = await Fixture.Cli.CreateAsync(emptyRun, Ct, "--mode", "Complete");
        AssertCreated(complete, "Complete with an empty template");

        var deleteOrder = complete.Operations
            .Where(o => (string?)o["changeType"] == "Delete")
            .Select(o => (string?)o["resourceId"] ?? string.Empty)
            .ToList();
        int policyIndex = deleteOrder.FindIndex(id => id.EndsWith("/policies/" + policyName, StringComparison.OrdinalIgnoreCase));
        int strengthIndex = deleteOrder.FindIndex(id => id.EndsWith("/authenticationStrengths/" + strengthName, StringComparison.OrdinalIgnoreCase));
        int contextIndex = deleteOrder.FindIndex(id => id.EndsWith("/authenticationContextClassReferences/" + contextId, StringComparison.OrdinalIgnoreCase));
        Assert.True(policyIndex >= 0 && strengthIndex >= 0 && contextIndex >= 0, "Expected three Delete operations; got: " + string.Join(", ", deleteOrder));
        Assert.True(policyIndex < strengthIndex && policyIndex < contextIndex, "The policy must be deleted before the strength and the context it references; the order was: " + string.Join(", ", deleteOrder));

        Assert.Null(await FindOneAsync(Policies, "displayName", policyName));
        Assert.Null(await FindOneAsync(Strengths, "displayName", strengthName));
        Assert.Null(await Graph.GetObjectAsync(new GraphRequest($"{Contexts}/{contextId}"), Ct));
    }
}
