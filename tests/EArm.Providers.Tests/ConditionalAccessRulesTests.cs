using System;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Providers.Tests;

/// <summary>Pure rules of the named locations and Conditional Access policies providers.</summary>
public sealed class ConditionalAccessRulesTests
{
    private const string StrengthId = "00000000-0000-0000-0000-000000000004";
    private const string GroupId = "aaaaaaaa-1111-2222-3333-444444444444";

    // ---- named locations ----

    [Theory]
    [InlineData("10.0.0.0/8", "10.0.0.0/8", false)]
    [InlineData(" 10.0.0.0/8 ", "10.0.0.0/8", false)]
    [InlineData("2001:DB8:0000::/32", "2001:db8::/32", true)]
    [InlineData("192.168.1.1", "192.168.1.1/32", false)]
    [InlineData("::1", "::1/128", true)]
    [InlineData("0.0.0.0/0", "0.0.0.0/0", false)]
    public void CidrProblem_CanonicalisesValidRanges(string text, string expectedCanonical, bool expectedIPv6)
    {
        Assert.Null(NamedLocationsRules.CidrProblem(text, out var canonical, out var isIPv6));
        Assert.Equal(expectedCanonical, canonical);
        Assert.Equal(expectedIPv6, isIPv6);
    }

    [Theory]
    [InlineData("10.0.0.1/8", "host bits")]
    [InlineData("10.0.0.0/33", "invalid prefix")]
    [InlineData("2001:db8::/129", "invalid prefix")]
    [InlineData("10.0.0/8", "four octets")]
    [InlineData("fe80::1%eth0/64", "not a valid CIDR")]
    [InlineData("nope", "not a valid CIDR")]
    [InlineData("10.0.0.0/x", "invalid prefix")]
    [InlineData("", "not a valid CIDR")]
    public void CidrProblem_RefusesInvalidRanges(string text, string expectedText)
    {
        string? problem = NamedLocationsRules.CidrProblem(text, out _, out _);
        Assert.NotNull(problem);
        Assert.Contains(expectedText, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void CidrProblem_HostBits_SuggestsTheNetworkAddress()
    {
        string? problem = NamedLocationsRules.CidrProblem("10.0.0.1/8", out _, out _);
        Assert.Contains("10.0.0.0/8", problem, StringComparison.Ordinal);
        Assert.Equal(NamedLocationsRules.IPv6RangeType, NamedLocationsRules.RangeTypeFor(true));
        Assert.Equal(NamedLocationsRules.IPv4RangeType, NamedLocationsRules.RangeTypeFor(false));
    }

    [Fact]
    public void NormalizeDesired_IpLocation_InfersRangeTypes_CanonicalisesCidrs_AndDefaultsIsTrusted()
    {
        var properties = (JsonObject)JsonNode.Parse("""{ "@odata.type": "#MICROSOFT.graph.ipNamedLocation", "ipRanges": [ { "cidrAddress": "10.0.0.0/8" }, { "cidrAddress": "2001:DB8::/32", "@odata.type": "#microsoft.graph.ipv6cidrrange" } ] }""")!;

        var problems = NamedLocationsRules.NormalizeDesired(properties);

        Assert.Empty(problems);
        Assert.Equal(NamedLocationsRules.IpKind, properties["@odata.type"]!.GetValue<string>());
        Assert.False(properties["isTrusted"]!.GetValue<bool>());
        Assert.Equal("""{"cidrAddress":"10.0.0.0/8","@odata.type":"#microsoft.graph.iPv4CidrRange"}""", properties["ipRanges"]![0]!.ToJsonString());
        Assert.Equal("""{"cidrAddress":"2001:db8::/32","@odata.type":"#microsoft.graph.iPv6CidrRange"}""", properties["ipRanges"]![1]!.ToJsonString());
        Assert.Equal(NamedLocationsRules.IpKind, NamedLocationsRules.KindOf(properties));
    }

    [Fact]
    public void NormalizeDesired_CountryLocation_UpperCasesCodes_AndAppliesDefaults()
    {
        var properties = (JsonObject)JsonNode.Parse("""{ "@odata.type": "#microsoft.graph.countryNamedLocation", "countriesAndRegions": [ "gb", " ie " ] }""")!;

        var problems = NamedLocationsRules.NormalizeDesired(properties);

        Assert.Empty(problems);
        Assert.Equal("""["GB","IE"]""", properties["countriesAndRegions"]!.ToJsonString());
        Assert.False(properties["includeUnknownCountriesAndRegions"]!.GetValue<bool>());
        Assert.Equal("clientIpAddress", properties["countryLookupMethod"]!.GetValue<string>());
        Assert.False(properties.ContainsKey("isTrusted"));
    }

    [Fact]
    public void NormalizeDesired_ReportsForeignProperties_BadRanges_AndUnknownKinds()
    {
        var foreignOnIp = (JsonObject)JsonNode.Parse("""{ "@odata.type": "#microsoft.graph.ipNamedLocation", "countriesAndRegions": ["GB"], "ipRanges": [ { "cidrAddress": "10.0.0.1/8" }, "text", { }, { "cidrAddress": "10.0.0.0/8", "@odata.type": "#microsoft.graph.iPv6CidrRange" } ] }""")!;
        var problems = NamedLocationsRules.NormalizeDesired(foreignOnIp);
        Assert.Contains(problems, p => p.Contains("countriesAndRegions applies to country", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("ipRanges[0]", StringComparison.Ordinal) && p.Contains("host bits", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("ipRanges[1] must be an object", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("ipRanges[2] has no cidrAddress", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("ipRanges[3]", StringComparison.Ordinal) && p.Contains("IPv4 range but declares", StringComparison.Ordinal));

        var foreignOnCountry = (JsonObject)JsonNode.Parse("""{ "@odata.type": "#microsoft.graph.countryNamedLocation", "isTrusted": true, "ipRanges": [ { "cidrAddress": "10.0.0.0/8" } ] }""")!;
        problems = NamedLocationsRules.NormalizeDesired(foreignOnCountry);
        Assert.Contains(problems, p => p.Contains("isTrusted applies to IP", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("ipRanges applies to IP", StringComparison.Ordinal));

        var unknownKind = (JsonObject)JsonNode.Parse("""{ "@odata.type": "#microsoft.graph.somethingElse" }""")!;
        Assert.Contains(NamedLocationsRules.NormalizeDesired(unknownKind), p => p.Contains("must be", StringComparison.Ordinal));
        Assert.Null(NamedLocationsRules.KindOf(unknownKind));

        Assert.Empty(NamedLocationsRules.NormalizeDesired((JsonObject)JsonNode.Parse("""{ "isTrusted": true }""")!));
    }

    [Fact]
    public void NormalizeLive_MatchesTheDesiredCanonicalForm_WithoutMutatingTheInput()
    {
        var live = (JsonObject)JsonNode.Parse("""{ "@odata.type": "#microsoft.graph.IPNamedLocation", "ipRanges": [ { "@odata.type": "#microsoft.graph.IPV4CidrRange", "cidrAddress": "10.0.0.0/8" }, { "cidrAddress": "2001:DB8:0::/32" } ], "countriesAndRegions": ["gb"] }""")!;

        var normalized = NamedLocationsRules.NormalizeLive(live);

        Assert.Equal(NamedLocationsRules.IpKind, normalized["@odata.type"]!.GetValue<string>());
        Assert.Equal(NamedLocationsRules.IPv4RangeType, normalized["ipRanges"]![0]!["@odata.type"]!.GetValue<string>());
        Assert.Equal("2001:db8::/32", normalized["ipRanges"]![1]!["cidrAddress"]!.GetValue<string>());
        Assert.Equal(NamedLocationsRules.IPv6RangeType, normalized["ipRanges"]![1]!["@odata.type"]!.GetValue<string>());
        Assert.Equal("GB", normalized["countriesAndRegions"]![0]!.GetValue<string>());
        Assert.Equal("#microsoft.graph.IPNamedLocation", live["@odata.type"]!.GetValue<string>());
        Assert.Equal(["id", "createdDateTime", "modifiedDateTime"], NamedLocationsRules.ServerAssignedProperties);
    }

    // ---- conditional access policies ----

    private static JsonObject Policy(string conditions, string? grant = "{ \"builtInControls\": [\"mfa\"] }", string? session = null, string? state = null)
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

        string stateJson = state is null ? string.Empty : "\"state\": \"" + state + "\", ";
        return (JsonObject)JsonNode.Parse("{ " + stateJson + "\"conditions\": " + conditions + controls + " }")!;
    }

    private const string DefaultConditions = "{ \"users\": { \"includeGroups\": [\"" + GroupId + "\"] }, \"applications\": { \"includeApplications\": [\"All\"] } }";

    private static JsonObject Normalized(JsonObject properties)
    {
        ConditionalAccessPoliciesRules.NormalizeDesired(properties);
        return properties;
    }

    [Fact]
    public void NormalizeDesired_AppliesStateClientAppTypesOperator_AndReducesTheStrength()
    {
        var properties = Policy(DefaultConditions, "{ \"authenticationStrength\": { \"id\": \"" + StrengthId + "\", \"displayName\": \"ignored\" } }");

        ConditionalAccessPoliciesRules.NormalizeDesired(properties);

        Assert.Equal(ConditionalAccessPoliciesRules.DefaultState, properties["state"]!.GetValue<string>());
        Assert.Equal("""["all"]""", properties["conditions"]!["clientAppTypes"]!.ToJsonString());
        Assert.Equal("OR", properties["grantControls"]!["operator"]!.GetValue<string>());
        Assert.Equal("{\"id\":\"" + StrengthId + "\"}", properties["grantControls"]!["authenticationStrength"]!.ToJsonString());

        var sessionOnly = Policy(DefaultConditions, grant: null, session: "{ \"signInFrequency\": { \"value\": 1 } }", state: "disabled");
        ConditionalAccessPoliciesRules.NormalizeDesired(sessionOnly);
        Assert.Equal("disabled", sessionOnly["state"]!.GetValue<string>());
        Assert.False(sessionOnly.ContainsKey("grantControls"));
    }

    [Fact]
    public void NormalizeLive_PrunesNullsAndEmptyArrays_AndReducesTheStrength()
    {
        var live = (JsonObject)JsonNode.Parse("""
            { "displayName": "p", "templateId": null,
              "conditions": { "userRiskLevels": [], "platforms": null, "clientAppTypes": ["all"], "users": { "includeUsers": [], "includeGroups": ["G"], "includeGuestsOrExternalUsers": null }, "applications": { "includeApplications": ["All"], "excludeApplications": [], "applicationFilter": null } },
              "grantControls": { "operator": "OR", "builtInControls": [], "termsOfUse": [], "authenticationStrength": { "id": "S", "displayName": "x", "allowedCombinations": ["fido2"] } },
              "sessionControls": null }
            """)!;

        var normalized = ConditionalAccessPoliciesRules.NormalizeLive(live);

        Assert.Equal("""{"displayName":"p","conditions":{"clientAppTypes":["all"],"users":{"includeGroups":["G"]},"applications":{"includeApplications":["All"]}},"grantControls":{"operator":"OR","authenticationStrength":{"id":"S"}}}""", normalized.ToJsonString());
        Assert.True(live.ContainsKey("templateId"));
    }

    [Fact]
    public void Problems_IsEmptyForAWellFormedPolicy()
    {
        Assert.Empty(ConditionalAccessPoliciesRules.Problems(Normalized(Policy(DefaultConditions)), GraphApi.V1, allowCaEnforce: false));
        Assert.Empty(ConditionalAccessPoliciesRules.Problems(Normalized(Policy(DefaultConditions, grant: null, session: "{ \"signInFrequency\": { \"value\": 1, \"type\": \"hours\", \"isEnabled\": true } }")), GraphApi.V1, false));
        Assert.Empty(ConditionalAccessPoliciesRules.Problems(Normalized(Policy(DefaultConditions, "{ \"authenticationStrength\": { \"id\": \"" + StrengthId + "\" } }", "{ \"signInFrequency\": { \"value\": 1 } }")), GraphApi.V1, false));
        Assert.Empty(ConditionalAccessPoliciesRules.Problems(Normalized(Policy(DefaultConditions, state: "enabled")), GraphApi.V1, allowCaEnforce: true));
    }

    [Theory]
    [InlineData("enabled", "--allow-ca-enforce")]
    [InlineData("disabled", null)]
    [InlineData("enabledForReportingButNotEnforced", null)]
    public void Problems_EnforceGuard(string state, string? expectedText)
    {
        var problems = ConditionalAccessPoliciesRules.Problems(Normalized(Policy(DefaultConditions, state: state)), GraphApi.V1, allowCaEnforce: false);

        if (expectedText is null)
        {
            Assert.Empty(problems);
        }
        else
        {
            Assert.Contains(expectedText, Assert.Single(problems), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Problems_ReportEveryShapeLint()
    {
        Assert.Contains(ConditionalAccessPoliciesRules.Problems((JsonObject)JsonNode.Parse("""{ "grantControls": { "builtInControls": ["mfa"] } }""")!, GraphApi.V1, false), p => p.Contains("conditions is required", StringComparison.Ordinal));
        Assert.Contains(ConditionalAccessPoliciesRules.Problems((JsonObject)JsonNode.Parse("""{ "conditions": "x", "grantControls": { "builtInControls": ["mfa"] } }""")!, GraphApi.V1, false), p => p.Contains("conditions must be an object", StringComparison.Ordinal));
        Assert.Contains(ConditionalAccessPoliciesRules.Problems(Normalized(Policy("{ \"users\": { \"includeGroups\": [\"" + GroupId + "\"] }, \"applications\": { \"excludeApplications\": [\"All\"] } }")), GraphApi.V1, false), p => p.Contains("conditions.applications must include", StringComparison.Ordinal));
        Assert.Contains(ConditionalAccessPoliciesRules.Problems(Normalized(Policy("{ \"users\": { \"excludeUsers\": [\"" + GroupId + "\"] }, \"applications\": { \"includeApplications\": [\"All\"] } }")), GraphApi.V1, false), p => p.Contains("conditions.users must include", StringComparison.Ordinal));
        Assert.Contains(ConditionalAccessPoliciesRules.Problems(Policy(DefaultConditions), GraphApi.V1, false), p => p.Contains("conditions.clientAppTypes is required", StringComparison.Ordinal));
        Assert.Contains(ConditionalAccessPoliciesRules.Problems(Normalized(Policy(DefaultConditions, grant: null)), GraphApi.V1, false), p => p.Contains("at least one of grantControls or sessionControls", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("\"includeApplications\": [\"All\"], \"includeAuthenticationContextClassReferences\": [\"c10\"]", true)]
    [InlineData("\"includeApplications\": [\"All\"], \"includeUserActions\": [\"urn:user:registersecurityinfo\"]", true)]
    [InlineData("\"includeAuthenticationContextClassReferences\": [\"c10\"]", false)]
    [InlineData("\"includeUserActions\": [\"urn:user:registersecurityinfo\"]", false)]
    [InlineData("\"includeApplications\": [], \"includeAuthenticationContextClassReferences\": [\"c10\"]", false)]
    [InlineData("\"includeApplications\": [\"All\"], \"includeUserActions\": []", false)]
    public void Problems_RefuseIncludeApplications_WithUserActionsOrAuthenticationContexts(string applications, bool refused)
    {
        // Graph 400 1012: 'applications' condition cannot include both 'includeApplications' and 'includeUserActions'/'includeAuthenticationContextClassReferences'.
        var problems = ConditionalAccessPoliciesRules.Problems(Normalized(Policy("{ \"users\": { \"includeGroups\": [\"" + GroupId + "\"] }, \"applications\": { " + applications + " } }")), GraphApi.V1, false);

        if (refused)
        {
            Assert.Contains("1012", Assert.Single(problems), StringComparison.Ordinal);
        }
        else
        {
            Assert.Empty(problems);
        }
    }

    [Fact]
    public void Problems_ReportGrantControlConflicts_AndTheLockoutPattern()
    {
        Assert.Contains(ConditionalAccessPoliciesRules.Problems(Normalized(Policy(DefaultConditions, "{ \"builtInControls\": [\"block\", \"mfa\"] }")), GraphApi.V1, false), p => p.Contains("'block' cannot be combined", StringComparison.Ordinal));
        Assert.Contains(ConditionalAccessPoliciesRules.Problems(Normalized(Policy(DefaultConditions, "{ \"builtInControls\": [\"block\"], \"authenticationStrength\": { \"id\": \"" + StrengthId + "\" } }")), GraphApi.V1, false), p => p.Contains("'block' cannot be combined", StringComparison.Ordinal));
        Assert.Contains(ConditionalAccessPoliciesRules.Problems(Normalized(Policy(DefaultConditions, "{ \"builtInControls\": [\"block\"], \"termsOfUse\": [\"t\"] }")), GraphApi.V1, false), p => p.Contains("'block' cannot be combined", StringComparison.Ordinal));
        Assert.Contains(ConditionalAccessPoliciesRules.Problems(Normalized(Policy(DefaultConditions, "{ \"builtInControls\": [\"mfa\"], \"authenticationStrength\": { \"id\": \"" + StrengthId + "\" } }")), GraphApi.V1, false), p => p.Contains("mutually exclusive", StringComparison.Ordinal));
        Assert.Empty(ConditionalAccessPoliciesRules.Problems(Normalized(Policy(DefaultConditions, "{ \"builtInControls\": [\"block\"] }")), GraphApi.V1, false));

        const string allUsersAllApps = "{ \"users\": { \"includeUsers\": [\"All\"] }, \"applications\": { \"includeApplications\": [\"All\"] } }";
        Assert.Contains(ConditionalAccessPoliciesRules.Problems(Normalized(Policy(allUsersAllApps, "{ \"builtInControls\": [\"block\"] }")), GraphApi.V1, false), p => p.Contains("lockout risk", StringComparison.Ordinal));
        Assert.Empty(ConditionalAccessPoliciesRules.Problems(Normalized(Policy("{ \"users\": { \"includeUsers\": [\"All\"], \"excludeGroups\": [\"" + GroupId + "\"] }, \"applications\": { \"includeApplications\": [\"All\"] } }", "{ \"builtInControls\": [\"block\"] }")), GraphApi.V1, false));
        Assert.Empty(ConditionalAccessPoliciesRules.Problems(Normalized(Policy("{ \"users\": { \"includeUsers\": [\"All\"], \"excludeUsers\": [\"" + GroupId + "\"] }, \"applications\": { \"includeApplications\": [\"All\"] } }", "{ \"builtInControls\": [\"block\"] }")), GraphApi.V1, false));
        Assert.Empty(ConditionalAccessPoliciesRules.Problems(Normalized(Policy(allUsersAllApps, "{ \"builtInControls\": [\"mfa\"] }")), GraphApi.V1, false));
    }

    [Fact]
    public void Problems_PreviewOnlyProperties_NeedTheBetaEndpoint()
    {
        var cae = Normalized(Policy(DefaultConditions, session: "{ \"continuousAccessEvaluation\": { \"mode\": \"disabled\" } }"));
        Assert.Contains(ConditionalAccessPoliciesRules.Problems(cae, GraphApi.V1, false), p => p.Contains("beta endpoint", StringComparison.Ordinal) && p.Contains("sessionControls.continuousAccessEvaluation", StringComparison.Ordinal));
        Assert.Empty(ConditionalAccessPoliciesRules.Problems(cae, GraphApi.Beta, false));

        var templated = Normalized(Policy(DefaultConditions));
        templated["templateId"] = "t";
        Assert.Contains(ConditionalAccessPoliciesRules.Problems(templated, GraphApi.V1, false), p => p.Contains("properties.templateId", StringComparison.Ordinal));
        Assert.Equal(["templateId", "partialEnablementStrategy", "sessionControls.continuousAccessEvaluation"], ConditionalAccessPoliciesRules.PreviewOnlyProperties);
    }

    [Fact]
    public void MarkRemovedControls_WritesNullOnlyForControlsTheLiveObjectCarries()
    {
        var live = (JsonObject)JsonNode.Parse("""{ "grantControls": { "operator": "OR" }, "sessionControls": null }""")!;
        var desired = new JsonObject();

        ConditionalAccessPoliciesRules.MarkRemovedControls(desired, live);

        Assert.True(desired.ContainsKey("grantControls"));
        Assert.Null(desired["grantControls"]);
        Assert.False(desired.ContainsKey("sessionControls"));

        var declared = (JsonObject)JsonNode.Parse("""{ "grantControls": { "builtInControls": ["mfa"] } }""")!;
        ConditionalAccessPoliciesRules.MarkRemovedControls(declared, live);
        Assert.NotNull(declared["grantControls"]);
        ConditionalAccessPoliciesRules.MarkRemovedControls(new JsonObject(), null);
    }

    [Fact]
    public void FinishCreateBody_DropsServerAssigned_Nulls_AndTheConjuredGrantControls()
    {
        var body = (JsonObject)JsonNode.Parse("""{ "id": "x", "createdDateTime": "d", "displayName": "p", "sessionControls": { "signInFrequency": { "value": 1 } }, "grantControls": { "operator": "OR" }, "templateId": null }""")!;
        var declared = (JsonObject)JsonNode.Parse("""{ "sessionControls": { "signInFrequency": { "value": 1 } } }""")!;

        ConditionalAccessPoliciesRules.FinishCreateBody(body, declared);

        Assert.Equal("""{"displayName":"p","sessionControls":{"signInFrequency":{"value":1}}}""", body.ToJsonString());

        var declaredGrant = (JsonObject)JsonNode.Parse("""{ "grantControls": { "operator": "OR", "builtInControls": ["mfa"] } }""")!;
        var withGrant = (JsonObject)JsonNode.Parse("""{ "grantControls": { "operator": "OR", "builtInControls": ["mfa"] } }""")!;
        ConditionalAccessPoliciesRules.FinishCreateBody(withGrant, declaredGrant);
        Assert.True(withGrant.ContainsKey("grantControls"));
    }

    [Fact]
    public void FinishPatchBody_TurnsNullsOverLiveCollectionsIntoEmptyArrays_Recursively()
    {
        var body = (JsonObject)JsonNode.Parse("""{ "modifiedDateTime": "d", "grantControls": { "authenticationStrength": { "id": "S" }, "builtInControls": null, "termsOfUse": null }, "conditions": { "users": { "includeUsers": ["All"], "includeGroups": null }, "platforms": null }, "sessionControls": null }""")!;
        var live = (JsonObject)JsonNode.Parse("""{ "grantControls": { "builtInControls": ["mfa"], "termsOfUse": null }, "conditions": { "users": { "includeGroups": ["G"] }, "platforms": { "includePlatforms": [] } }, "sessionControls": { } }""")!;

        ConditionalAccessPoliciesRules.FinishPatchBody(body, live);

        Assert.Equal("""{"grantControls":{"authenticationStrength":{"id":"S"},"builtInControls":[],"termsOfUse":null},"conditions":{"users":{"includeUsers":["All"],"includeGroups":[]},"platforms":null},"sessionControls":null}""", body.ToJsonString());

        var noLive = (JsonObject)JsonNode.Parse("""{ "id": "x", "grantControls": { "builtInControls": null } }""")!;
        ConditionalAccessPoliciesRules.FinishPatchBody(noLive, null);
        Assert.Equal("""{"grantControls":{"builtInControls":null}}""", noLive.ToJsonString());
    }

    [Fact]
    public void ContainsLiteral_IsCaseInsensitive_AndIgnoresNonArrays()
    {
        Assert.True(ConditionalAccessPoliciesRules.ContainsLiteral(new JsonArray("all"), "All"));
        Assert.False(ConditionalAccessPoliciesRules.ContainsLiteral(new JsonArray("x"), "All"));
        Assert.False(ConditionalAccessPoliciesRules.ContainsLiteral(JsonValue.Create("All"), "All"));
        Assert.False(ConditionalAccessPoliciesRules.ContainsLiteral(null, "All"));
        Assert.Equal(["conditions", "grantControls", "sessionControls"], ConditionalAccessPoliciesRules.ComplexProperties);
    }
}
