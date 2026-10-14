using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EntraArm.Az;
using EntraArm.Engine.Execution;
using EntraArm.Engine.Planning;
using EntraArm.Engine.Providers;
using EntraArm.Providers.Tests.Infrastructure;
using Xunit;

namespace EntraArm.Providers.Tests;

/// <summary>The Conditional Access policies provider (WI-14) end to end, including the location dependency, the enforce guard and delete ordering.</summary>
public sealed class ConditionalAccessPoliciesProviderTests
{
    private const string LocType = NamedLocationsProvider.ResourceType;
    private const string PolType = ConditionalAccessPoliciesProvider.ResourceType;
    private const string LocPath = "/identity/conditionalAccess/namedLocations";
    private const string PolPath = "/identity/conditionalAccess/policies";
    private const string LocName = "Contoso offices";
    private const string PolName = "CA-Test-MFA";
    private const string GroupId = "aaaaaaaa-1111-2222-3333-444444444444";
    private const string StrengthId = "00000000-0000-0000-0000-000000000004";
    private const string DefaultConditions = "{ \"users\": { \"includeGroups\": [\"" + GroupId + "\"] }, \"applications\": { \"includeApplications\": [\"All\"] } }";
    private const string LocationConditions = "{ \"users\": { \"includeGroups\": [\"" + GroupId + "\"] }, \"applications\": { \"includeApplications\": [\"All\"] }, \"locations\": { \"includeLocations\": [\"All\"], \"excludeLocations\": [\"[reference('" + LocName + "').id]\"] } }";
    private const string DependsOnLocation = ", \"dependsOn\": [\"[resourceId('" + LocType + "', '" + LocName + "')]\"]";
    private const string SessionFrequency = "{ \"signInFrequency\": { \"value\": 1, \"type\": \"hours\", \"isEnabled\": true } }";

    private static string IpLocation()
    {
        return "{ \"type\": \"" + LocType + "\", \"apiVersion\": \"2026-10-01\", \"name\": \"" + LocName + "\", \"properties\": { \"@odata.type\": \"#microsoft.graph.ipNamedLocation\", \"ipRanges\": [ { \"cidrAddress\": \"10.0.0.0/8\" } ] } }";
    }

    private static string Policy(string conditions = DefaultConditions, string? grant = "{ \"builtInControls\": [\"mfa\"] }", string? session = null, string state = "", string apiVersion = "2026-10-01", string dependsOn = "")
    {
        string controls = string.Empty;
        if (grant is not null)
        {
            controls += ", \"grantControls\": " + grant;
        }

        if (session is not null)
        {
            controls += ", \"sessionControls\": " + session;
        }

        string stateJson = state.Length == 0 ? string.Empty : "\"state\": \"" + state + "\", ";
        return "{ \"type\": \"" + PolType + "\", \"apiVersion\": \"" + apiVersion + "\", \"name\": \"" + PolName + "\", \"properties\": { " + stateJson + "\"conditions\": " + conditions + controls + " }" + dependsOn + " }";
    }

    [Fact]
    public void Discovery_RegistersBothProviders_WithTheirPermissions()
    {
        var host = new ProviderHost();
        Assert.IsType<ConditionalAccessPoliciesProvider>(host.Provider(PolType));
        Assert.Equal(["Policy.ReadWrite.ConditionalAccess", "Policy.Read.All", "Application.Read.All"], host.Provider(PolType).RequiredPermissions(null));
    }

    [Fact]
    public async Task Create_PostsThePolicyAfterTheLocation_WithDefaultsAndReplicationHint()
    {
        var host = new ProviderHost();
        var document = host.Resources(IpLocation() + ", " + Policy(LocationConditions, dependsOn: DependsOnLocation));

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        var writes = run.Writes;
        Assert.Equal(2, writes.Count);
        Assert.All(writes, w => Assert.Equal("POST", w.Method));
        Assert.Equal(LocPath, writes[0].Path);
        Assert.Equal(PolPath, writes[1].Path);
        string locId = host.IdOf(LocPath, "displayName", LocName);
        string body = writes[1].Body!.ToJsonString();
        Assert.Contains("\"excludeLocations\":[\"" + locId + "\"]", body, StringComparison.Ordinal);
        Assert.Contains("\"includeGroups\":[\"" + GroupId + "\"]", body, StringComparison.Ordinal);
        Assert.Contains("\"state\":\"enabledForReportingButNotEnforced\"", body, StringComparison.Ordinal);
        Assert.Contains("\"clientAppTypes\":[\"all\"]", body, StringComparison.Ordinal);
        Assert.Contains("\"grantControls\":{\"builtInControls\":[\"mfa\"],\"operator\":\"OR\"}", body, StringComparison.Ordinal);
        Assert.Contains("\"displayName\":\"CA-Test-MFA\"", body, StringComparison.Ordinal);
        Assert.Equal(RetryHint.ExpectReplicationLag, writes[1].Hint);
        Assert.Contains(run.Requests, r => r.ToString() == "GET " + PolPath + "?$filter=displayName eq 'CA-Test-MFA'");

        var whatIf = await new ProviderHost().WhatIfAsync(host.Resources(IpLocation() + ", " + Policy(LocationConditions, dependsOn: DependsOnLocation)));
        var row = whatIf.ChangeOfType(PolType);
        Assert.Equal(ChangeType.Create, row.Type);
        Assert.False(row.HasError);
        Assert.True(row.IsUnknown);
        Check.NoWrites(whatIf);

        var rerun = await host.CreateAsync(document);
        Assert.All(rerun.Executed.Operations, o => Assert.Equal(ChangeType.NoChange, o.ChangeType));
        Check.NoWrites(rerun);
    }

    [Fact]
    public async Task ReadBackNoise_IsNoChange()
    {
        var host = new ProviderHost();
        host.Directory.Add(PolPath, new JsonObject
        {
            ["displayName"] = PolName,
            ["state"] = "enabledForReportingButNotEnforced",
            ["createdDateTime"] = "2026-01-01T00:00:00Z",
            ["modifiedDateTime"] = null,
            ["templateId"] = null,
            ["deletedDateTime"] = null,
            ["conditions"] = new JsonObject
            {
                ["userRiskLevels"] = new JsonArray(),
                ["signInRiskLevels"] = new JsonArray(),
                ["clientAppTypes"] = new JsonArray("all"),
                ["servicePrincipalRiskLevels"] = new JsonArray(),
                ["insiderRiskLevels"] = null,
                ["platforms"] = null,
                ["locations"] = null,
                ["devices"] = null,
                ["clientApplications"] = null,
                ["applications"] = new JsonObject { ["includeApplications"] = new JsonArray("All"), ["excludeApplications"] = new JsonArray(), ["includeUserActions"] = new JsonArray(), ["includeAuthenticationContextClassReferences"] = new JsonArray(), ["applicationFilter"] = null },
                ["users"] = new JsonObject { ["includeUsers"] = new JsonArray(), ["excludeUsers"] = new JsonArray(), ["includeGroups"] = new JsonArray(GroupId.ToUpperInvariant()), ["excludeGroups"] = new JsonArray(), ["includeRoles"] = new JsonArray(), ["excludeRoles"] = new JsonArray(), ["includeGuestsOrExternalUsers"] = null, ["excludeGuestsOrExternalUsers"] = null },
            },
            ["grantControls"] = new JsonObject
            {
                ["operator"] = "OR",
                ["builtInControls"] = new JsonArray(),
                ["customAuthenticationFactors"] = new JsonArray(),
                ["termsOfUse"] = new JsonArray(),
                ["authenticationStrength"] = new JsonObject { ["id"] = StrengthId, ["displayName"] = "Phishing-resistant MFA", ["policyType"] = "builtIn", ["requirementsSatisfied"] = "mfa", ["allowedCombinations"] = new JsonArray("fido2", "x509CertificateMultiFactor"), ["createdDateTime"] = "2021-12-01T00:00:00Z" },
            },
            ["sessionControls"] = null,
        });

        var run = await host.CreateAsync(host.Resources(Policy(grant: "{ \"authenticationStrength\": { \"id\": \"" + StrengthId + "\" } }")));

        Check.Succeeded(run);
        Assert.Equal(ChangeType.NoChange, Assert.Single(run.Executed.Operations).ChangeType);
        Check.NoWrites(run);
        var before = run.Executed.Changes.Single().Before!.ToJsonString();
        Assert.Contains("\"authenticationStrength\":{\"id\":\"" + StrengthId + "\"}", before, StringComparison.Ordinal);
        Assert.DoesNotContain("platforms", before, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GrantControlsChange_PatchesTheFullObject_AndStrengthSwitchClearsBuiltIns()
    {
        var host = new ProviderHost();
        await host.CreateAsync(host.Resources(Policy()));
        string id = host.IdOf(PolPath, "displayName", PolName);

        var changed = await host.CreateAsync(host.Resources(Policy(grant: "{ \"operator\": \"AND\", \"builtInControls\": [\"mfa\", \"compliantDevice\"] }")));
        Check.Succeeded(changed);
        var patch = Assert.Single(changed.Writes);
        Assert.Equal("PATCH", patch.Method);
        Assert.Equal(PolPath + "/" + id, patch.Path);
        Assert.Equal("{\"grantControls\":{\"operator\":\"AND\",\"builtInControls\":[\"mfa\",\"compliantDevice\"]}}", patch.Body!.ToJsonString());

        var strength = await host.CreateAsync(host.Resources(Policy(grant: "{ \"authenticationStrength\": { \"id\": \"" + StrengthId + "\", \"displayName\": \"ignored\" } }")));
        Check.Succeeded(strength);
        Assert.Equal("{\"grantControls\":{\"authenticationStrength\":{\"id\":\"" + StrengthId + "\"},\"operator\":\"OR\",\"builtInControls\":[]}}", Assert.Single(strength.Writes).Body!.ToJsonString());

        var users = await host.CreateAsync(host.Resources(Policy("{ \"users\": { \"includeUsers\": [\"All\"], \"excludeGroups\": [\"" + GroupId + "\"] }, \"applications\": { \"includeApplications\": [\"All\"] } }", "{ \"authenticationStrength\": { \"id\": \"" + StrengthId + "\" } }")));
        Check.Succeeded(users);
        Assert.Equal("{\"conditions\":{\"users\":{\"includeUsers\":[\"All\"],\"excludeGroups\":[\"" + GroupId + "\"],\"includeGroups\":[]},\"applications\":{\"includeApplications\":[\"All\"]},\"clientAppTypes\":[\"all\"]}}", Assert.Single(users.Writes).Body!.ToJsonString());
    }

    [Fact]
    public async Task RemovingSessionControls_PatchesNull_AndRemovingOneControlSendsAnExplicitNullInsideTheObject()
    {
        var host = new ProviderHost();
        var created = await host.CreateAsync(host.Resources(Policy(session: SessionFrequency)));
        Check.Succeeded(created);
        string id = host.IdOf(PolPath, "displayName", PolName);

        var removed = await host.CreateAsync(host.Resources(Policy()));
        Check.Succeeded(removed);
        var write = Assert.Single(removed.Writes);
        Assert.Equal(PolPath + "/" + id, write.Path);
        Assert.Equal("{\"sessionControls\":null}", write.Body!.ToJsonString());
        Assert.False(host.Directory.FindById(id)!.ContainsKey("sessionControls"));

        await host.CreateAsync(host.Resources(Policy(session: "{ \"signInFrequency\": { \"value\": 1, \"type\": \"hours\", \"isEnabled\": true }, \"persistentBrowser\": { \"mode\": \"never\", \"isEnabled\": true } }")));
        var partial = await host.CreateAsync(host.Resources(Policy(session: SessionFrequency)));
        Check.Succeeded(partial);
        Assert.Equal("{\"sessionControls\":{\"signInFrequency\":{\"value\":1,\"type\":\"hours\",\"isEnabled\":true},\"persistentBrowser\":null}}", Assert.Single(partial.Writes).Body!.ToJsonString());
    }

    [Fact]
    public async Task EnforceGuard_AppliesInWhatIf_Create_AndDirectCreate_ButNotWithTheFlag()
    {
        var host = new ProviderHost();
        var whatIf = await host.WhatIfAsync(host.Resources(Policy(state: "enabled")));
        var row = Assert.Single(whatIf.Plan.Changes);
        Assert.True(row.HasError);
        Assert.Contains("--allow-ca-enforce", row.UnsupportedReason, StringComparison.Ordinal);

        var create = await host.CreateAsync(host.Resources(Policy(state: "enabled")));
        Assert.NotEqual(DeploymentStatus.Succeeded, create.Executed.Status);
        Check.NoWrites(create);
        Assert.Contains("--allow-ca-enforce", create.Errors, StringComparison.Ordinal);

        var allowedHost = new ProviderHost { AllowCaEnforce = true };
        var allowed = await allowedHost.CreateAsync(allowedHost.Resources(Policy(state: "enabled")));
        Check.Succeeded(allowed);
        Assert.Contains("\"state\":\"enabled\"", Assert.Single(allowed.Writes).Body!.ToJsonString(), StringComparison.Ordinal);

        var provider = new ConditionalAccessPoliciesProvider(EntraArm.Core.Types.ResourceTypeCatalog.Default) { PlanOptions = PlanOptions.Default };
        var direct = new FakeClientOnly();
        var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.CreateAsync(allowed.Plan.Instances.Single(), direct.Client, ProviderHost.Ct));
        Assert.Equal(ConditionalAccessPoliciesProvider.PolicyRejected, ex.Code);
        Assert.Empty(direct.Client.Writes);

        var disabledHost = new ProviderHost();
        Check.Succeeded(await disabledHost.CreateAsync(disabledHost.Resources(Policy(state: "disabled"))));
    }

    private sealed class FakeClientOnly
    {
        public EntraArm.TestSupport.FakeGraphClient Client { get; } = new();
    }

    [Theory]
    [InlineData("lockout", "lockout risk")]
    [InlineData("blockMfa", "'block' cannot be combined")]
    [InlineData("blockStrength", "'block' cannot be combined")]
    [InlineData("mfaStrength", "mutually exclusive")]
    [InlineData("noAppInclude", "conditions.applications must include")]
    [InlineData("noUserInclude", "conditions.users must include")]
    [InlineData("noControls", "at least one of grantControls or sessionControls")]
    [InlineData("noConditions", "conditions is required")]
    [InlineData("caeV1", "beta endpoint")]
    public async Task Lints_AreUnsupportedWhatIfRows(string scenario, string expectedText)
    {
        const string allUsersAllApps = "{ \"users\": { \"includeUsers\": [\"All\"] }, \"applications\": { \"includeApplications\": [\"All\"] } }";
        string template = scenario switch
        {
            "lockout" => Policy(allUsersAllApps, "{ \"builtInControls\": [\"block\"] }"),
            "blockMfa" => Policy(DefaultConditions, "{ \"builtInControls\": [\"block\", \"mfa\"] }"),
            "blockStrength" => Policy(DefaultConditions, "{ \"builtInControls\": [\"block\"], \"authenticationStrength\": { \"id\": \"" + StrengthId + "\" } }"),
            "mfaStrength" => Policy(DefaultConditions, "{ \"builtInControls\": [\"mfa\"], \"authenticationStrength\": { \"id\": \"" + StrengthId + "\" } }"),
            "noAppInclude" => Policy("{ \"users\": { \"includeGroups\": [\"" + GroupId + "\"] }, \"applications\": { \"excludeApplications\": [\"All\"] } }"),
            "noUserInclude" => Policy("{ \"users\": { \"excludeUsers\": [\"" + GroupId + "\"] }, \"applications\": { \"includeApplications\": [\"All\"] } }"),
            "noControls" => Policy(DefaultConditions, grant: null),
            "noConditions" => "{ \"type\": \"" + PolType + "\", \"apiVersion\": \"2026-10-01\", \"name\": \"x\", \"properties\": { \"grantControls\": { \"builtInControls\": [\"mfa\"] } } }",
            "caeV1" => Policy(DefaultConditions, session: "{ \"continuousAccessEvaluation\": { \"mode\": \"disabled\" } }"),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        var host = new ProviderHost();

        var run = await host.WhatIfAsync(host.Resources(template));

        var row = Assert.Single(run.Plan.Changes);
        Assert.True(row.HasError);
        Assert.Contains(expectedText, row.UnsupportedReason, StringComparison.Ordinal);
        Check.NoWrites(run);
    }

    [Fact]
    public async Task AcceptedShapes_ArePlannedWithoutError()
    {
        var host = new ProviderHost();
        const string allUsersAllApps = "{ \"users\": { \"includeUsers\": [\"All\"], \"excludeGroups\": [\"" + GroupId + "\"] }, \"applications\": { \"includeApplications\": [\"All\"] } }";

        Assert.False((await host.WhatIfAsync(host.Resources(Policy(allUsersAllApps, "{ \"builtInControls\": [\"block\"] }")))).Plan.Changes.Single().HasError);
        Assert.False((await host.WhatIfAsync(host.Resources(Policy(DefaultConditions, "{ \"builtInControls\": [\"block\"] }")))).Plan.Changes.Single().HasError);
        Assert.False((await host.WhatIfAsync(host.Resources(Policy(DefaultConditions, session: "{ \"continuousAccessEvaluation\": { \"mode\": \"disabled\" } }", apiVersion: "2026-10-01-preview")))).Plan.Changes.Single().HasError);
        Assert.False((await host.WhatIfAsync(host.Resources(Policy(DefaultConditions, "{ \"authenticationStrength\": { \"id\": \"" + StrengthId + "\" } }", session: SessionFrequency)))).Plan.Changes.Single().HasError);
    }

    [Fact]
    public async Task CompleteMode_DeletesThePolicyBeforeTheLocation_AndSoDoesDeleteAll()
    {
        var host = new ProviderHost();
        var first = await host.CreateAsync(host.Resources(IpLocation() + ", " + Policy(LocationConditions, dependsOn: DependsOnLocation)));
        Check.Succeeded(first);
        string locId = host.IdOf(LocPath, "displayName", LocName);
        string polId = host.IdOf(PolPath, "displayName", PolName);

        var complete = await host.CreateAsync(host.Resources(string.Empty), DeploymentMode.Complete);

        Check.Succeeded(complete);
        var writes = complete.Writes;
        Assert.Equal(2, writes.Count);
        Assert.All(writes, w => Assert.Equal("DELETE", w.Method));
        Assert.Equal(PolPath + "/" + polId, writes[0].Path);
        Assert.Equal(LocPath + "/" + locId, writes[1].Path);
        Assert.Equal(0, host.Directory.Count(PolPath));
        Assert.Equal(0, host.Directory.Count(LocPath));

        var other = new ProviderHost();
        await other.CreateAsync(other.Resources(IpLocation() + ", " + Policy(LocationConditions, dependsOn: DependsOnLocation)));
        other.ClearRequests();
        var deletion = await other.DeleteAllAsync();
        Assert.True(deletion.Succeeded);
        Assert.Equal(2, other.Fake.Writes.Count);
        Assert.StartsWith(PolPath + "/", other.Fake.Writes[0].Path, StringComparison.Ordinal);
        Assert.StartsWith(LocPath + "/", other.Fake.Writes[1].Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionOnlyPolicy_ConjuresNoGrantControls()
    {
        var host = new ProviderHost();

        var run = await host.CreateAsync(host.Resources(Policy(grant: null, session: SessionFrequency)));

        Check.Succeeded(run);
        string body = Assert.Single(run.Writes).Body!.ToJsonString();
        Assert.DoesNotContain("grantControls", body, StringComparison.Ordinal);
        Assert.Contains("\"sessionControls\":{\"signInFrequency\"", body, StringComparison.Ordinal);

        var rerun = await host.CreateAsync(host.Resources(Policy(grant: null, session: SessionFrequency)));
        Assert.Equal(ChangeType.NoChange, Assert.Single(rerun.Executed.Operations).ChangeType);
        Check.NoWrites(rerun);
    }
}
