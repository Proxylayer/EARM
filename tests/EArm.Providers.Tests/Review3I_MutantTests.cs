using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core.Secrets;
using EArm.Core.Types;
using EArm.Engine.Providers;
using EArm.Graph;
using EArm.TestSupport;
using Xunit;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Providers.Tests;

/// <summary>
/// Review 3I, finding 12: tests for the behaviour the mutation run showed to be unguarded in the providers (CA01, CA07, CA13, DP02, DP04, PS02, PS04, PS05,
/// PW07, PW08, PW10, PW11, PW14, PW16).
/// </summary>
public sealed class Review3I_MutantTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonObject J(string json)
    {
        return (JsonObject)JsonNode.Parse(json)!;
    }

    // ---- conditional access ----

    private const string AllUsersAllApps = """{ "users": { "includeUsers": ["All"] }, "applications": { "includeApplications": ["All"] }, "clientAppTypes": ["all"] }""";

    private static JsonObject Policy(string state, string conditions, string controls = """["mfa"]""")
    {
        return J("{ \"state\": " + JsonValue.Create(state)!.ToJsonString() + ", \"conditions\": " + conditions + ", \"grantControls\": { \"operator\": \"OR\", \"builtInControls\": " + controls + " } }");
    }

    // CA01
    [Theory]
    [InlineData("enabled")]
    [InlineData(" enabled ")]
    [InlineData("ENABLED")]
    [InlineData("\tEnabled\n")]
    public void AFinalBodyWithAnEnabledStateIsBlockedWithoutTheEnforceFlag(string state)
    {
        var body = Policy(state, AllUsersAllApps);

        Assert.Contains(ConditionalAccessPoliciesRules.FinalBodyProblems(body, GraphApi.V1, allowCaEnforce: false, isCreate: false), p => p.Contains("--allow-ca-enforce", StringComparison.Ordinal));
        Assert.Empty(ConditionalAccessPoliciesRules.FinalBodyProblems(body, GraphApi.V1, allowCaEnforce: true, isCreate: false));
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("enabledForReportingButNotEnforced")]
    public void AFinalBodyThatIsNotEnforcedPassesTheGuard(string state)
    {
        Assert.Empty(ConditionalAccessPoliciesRules.FinalBodyProblems(Policy(state, AllUsersAllApps), GraphApi.V1, allowCaEnforce: false, isCreate: false));
    }

    // CA07
    [Fact]
    public void ACreateBodyReportsEachProblemOnce()
    {
        var body = Policy("enabled", AllUsersAllApps);

        var problems = ConditionalAccessPoliciesRules.FinalBodyProblems(body, GraphApi.V1, allowCaEnforce: false, isCreate: true);

        Assert.Single(problems, p => p.Contains("--allow-ca-enforce", StringComparison.Ordinal));
        Assert.Equal(problems.Count, problems.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ACreateBodyAddsTheStructuralProblemsAfterTheStateGuard()
    {
        var body = J("""{ "state": "enabled" }""");

        var problems = ConditionalAccessPoliciesRules.FinalBodyProblems(body, GraphApi.V1, allowCaEnforce: false, isCreate: true);

        Assert.Contains(problems, p => p.Contains("conditions is required", StringComparison.Ordinal));
        Assert.Equal(problems.Count, problems.Distinct(StringComparer.Ordinal).Count());
    }

    // CA13
    [Fact]
    public void TheLockoutLintNeedsAllApplicationsNotJustAllUsers()
    {
        string specificApp = """{ "users": { "includeUsers": ["All"] }, "applications": { "includeApplications": ["11111111-2222-3333-4444-555555555555"] }, "clientAppTypes": ["all"] }""";
        string allApps = """{ "users": { "includeUsers": ["All"] }, "applications": { "includeApplications": ["All"] }, "clientAppTypes": ["all"] }""";
        string specificUsers = """{ "users": { "includeUsers": ["11111111-2222-3333-4444-555555555555"] }, "applications": { "includeApplications": ["All"] }, "clientAppTypes": ["all"] }""";

        Assert.DoesNotContain(ConditionalAccessPoliciesRules.Problems(Policy("disabled", specificApp, """["block"]"""), GraphApi.V1, false), p => p.Contains("lockout", StringComparison.Ordinal));
        Assert.DoesNotContain(ConditionalAccessPoliciesRules.Problems(Policy("disabled", specificUsers, """["block"]"""), GraphApi.V1, false), p => p.Contains("lockout", StringComparison.Ordinal));
        Assert.Contains(ConditionalAccessPoliciesRules.Problems(Policy("disabled", allApps, """["block"]"""), GraphApi.V1, false), p => p.Contains("lockout", StringComparison.Ordinal));
    }

    // ---- deleted-items probe ----

    private const string DeletedGroups = "/directory/deletedItems/microsoft.graph.group";

    private static readonly string[] Select = ["id", "uniqueName", "displayName"];

    private static void RejectOnce(FakeGraphClient client, HttpStatusCode status, int times = 1)
    {
        client.Failures.Add("GET", "/directory/deletedItems", _ => new GraphException(status, "Request_BadRequest", "no"), times);
    }

    // DP02
    [Fact]
    public async Task TheFallbackListingAsksForTheLargestPageAndMatchesOnTheClientSide()
    {
        var client = new FakeGraphClient();
        client.Directory.AddDeleted("/groups", J("""{ "uniqueName": "held", "displayName": "Held", "groupTypes": [] }"""));
        RejectOnce(client, HttpStatusCode.BadRequest);
        var warnings = new List<ProviderWarning>();

        var found = await DeletedItemsProbe.FindAsync(client, DeletedGroups, "group", "uniqueName", "held", Select, GraphApi.V1, advanced: false, warnings, Ct);

        Assert.NotNull(found);
        Assert.Empty(warnings);
        var requests = client.Requests.Where(r => r.Path == DeletedGroups).ToList();
        Assert.Equal(2, requests.Count);
        Assert.True(requests[0].UrlParameters!.ContainsKey("$filter"));
        Assert.Equal("999", requests[1].UrlParameters!["$top"]);
        Assert.False(requests[1].UrlParameters!.ContainsKey("$filter"));
    }

    // DP04
    [Theory]
    [InlineData("HELD", "held")]
    [InlineData("held", "HELD")]
    [InlineData("Held-Name", "hELD-nAME")]
    public async Task TheProbeMatchesTheKeyIgnoringCase(string stored, string wanted)
    {
        var client = new FakeGraphClient();
        client.Directory.AddDeleted("/groups", J("{ \"uniqueName\": \"" + stored + "\", \"displayName\": \"Held\", \"groupTypes\": [] }"));

        var found = await DeletedItemsProbe.FindAsync(client, DeletedGroups, "group", "uniqueName", stored, Select, GraphApi.V1, advanced: false, [], Ct);
        var other = await DeletedItemsProbe.FindAsync(client, DeletedGroups, "group", "uniqueName", wanted, Select, GraphApi.V1, advanced: false, [], Ct);

        Assert.NotNull(found);
        Assert.NotNull(other);
    }

    [Fact]
    public async Task AnotherKeyIsNoMatch()
    {
        var client = new FakeGraphClient();
        client.Directory.AddDeleted("/groups", J("""{ "uniqueName": "held", "displayName": "Held", "groupTypes": [] }"""));

        Assert.Null(await DeletedItemsProbe.FindAsync(client, DeletedGroups, "group", "uniqueName", "helds", Select, GraphApi.V1, advanced: false, [], Ct));
    }

    // PS02
    [Fact]
    public async Task AProbeThatIsRejectedTwiceIsSkippedWithAWarningAndNull()
    {
        var client = new FakeGraphClient();
        RejectOnce(client, HttpStatusCode.BadRequest, times: -1);
        var warnings = new List<ProviderWarning>();

        var found = await DeletedItemsProbe.FindAsync(client, DeletedGroups, "group", "uniqueName", "held", Select, GraphApi.V1, advanced: false, warnings, Ct);

        Assert.Null(found);
        var warning = Assert.Single(warnings);
        Assert.Equal(ProviderWarnings.SoftDeleteProbeSkipped, warning.Code);
        Assert.Contains("400", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AForbiddenProbeIsSkippedWithAWarningAndNull()
    {
        var client = new FakeGraphClient();
        RejectOnce(client, HttpStatusCode.Forbidden, times: -1);
        var warnings = new List<ProviderWarning>();

        Assert.Null(await DeletedItemsProbe.FindAsync(client, DeletedGroups, "group", "uniqueName", "held", Select, GraphApi.V1, advanced: false, warnings, Ct));

        Assert.Equal(ProviderWarnings.SoftDeleteProbeSkipped, Assert.Single(warnings).Code);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, true)]
    [InlineData(HttpStatusCode.Forbidden, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    public void OnlyA400OrA403MeansTheProbeIsUnavailable(HttpStatusCode status, bool unavailable)
    {
        Assert.Equal(unavailable, ProviderSupport.IsProbeUnavailable(new GraphException(status, "x", "y")));
    }

    [Fact]
    public async Task AServerErrorOfTheProbeIsNotSwallowed()
    {
        var client = new FakeGraphClient();
        RejectOnce(client, HttpStatusCode.InternalServerError, times: -1);

        await Assert.ThrowsAsync<GraphException>(() => DeletedItemsProbe.FindAsync(client, DeletedGroups, "group", "uniqueName", "held", Select, GraphApi.V1, advanced: false, [], Ct));
    }

    // ---- declared $select names ----

    private static ResourceTypeDefinition GroupDefinition()
    {
        return ResourceTypeCatalog.Default.Find("Microsoft.Entra/groups")!;
    }

    // PS04
    [Fact]
    public void ADeclaredIdIsNeverSelectedTwiceWhateverItsCase()
    {
        var declared = J("""{ "Id": "x", "displayName": "d", "description": "e" }""");

        var names = ProviderSupport.DeclaredSelectNames(GroupDefinition(), declared, exclude: ["id"]);

        Assert.DoesNotContain(names, n => n.Equals("id", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("displayName", names);
    }

    // PS05
    [Fact]
    public void OnlyPlainGraphIdentifiersAreSelected()
    {
        var declared = J("""{ "_hidden": 1, "1abc": 2, "with space": 3, "with-dash": 4, "x.y": 5, "ok_1": 6, "Okay": 7, "": 8 }""");

        var names = ProviderSupport.DeclaredSelectNames(GroupDefinition(), declared);

        Assert.Equal(["ok_1", "Okay"], names.ToArray());
    }

    [Fact]
    public void ADeclaredNameIsSelectedOnceEvenWhenItsCaseDiffers()
    {
        var declared = J("""{ "visibility": "Private", "VISIBILITY": "Public" }""");

        var names = ProviderSupport.DeclaredSelectNames(GroupDefinition(), declared);

        Assert.Single(names);
    }

    [Fact]
    public void TheUnionKeepsTheManagedOrderAndAddsOnlyNewNames()
    {
        var union = ProviderSupport.Union(["id", "displayName"], ["DisplayName", "description"]);

        Assert.Equal(["id", "displayName", "description"], union);
    }

    // ---- password credentials ----

    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    // PW07
    [Fact]
    public void TwoInstantsWithinOneSecondAreTheSame()
    {
        Assert.True(PasswordCredentialsRules.SameInstant(Now, Now));
        Assert.True(PasswordCredentialsRules.SameInstant(Now, Now.AddSeconds(1)));
        Assert.True(PasswordCredentialsRules.SameInstant(Now.AddSeconds(1), Now));
        Assert.True(PasswordCredentialsRules.SameInstant(Now, Now.AddMilliseconds(-999)));
        Assert.False(PasswordCredentialsRules.SameInstant(Now, Now.AddMilliseconds(1001)));
        Assert.False(PasswordCredentialsRules.SameInstant(Now.AddMilliseconds(1001), Now));
        Assert.True(PasswordCredentialsRules.SameInstant(null, null));
        Assert.False(PasswordCredentialsRules.SameInstant(Now, null));
        Assert.False(PasswordCredentialsRules.SameInstant(null, Now));
    }

    [Fact]
    public void TheSameInstantInAnotherOffsetIsTheSame()
    {
        Assert.True(PasswordCredentialsRules.SameInstant(Now, Now.ToOffset(TimeSpan.FromHours(5))));
    }

    // PW08
    [Fact]
    public void AnEndDateNoLaterThanTheStartDateIsRefused()
    {
        string Describe(string start, string end) => PasswordCredentialsRules.DescribeDesired(J("{ \"startDateTime\": \"" + start + "\", \"endDateTime\": \"" + end + "\" }"), out _) ?? "ok";

        Assert.NotEqual("ok", Describe("2027-01-01T00:00:00Z", "2027-01-01T00:00:00Z"));
        Assert.NotEqual("ok", Describe("2027-01-01T00:00:01Z", "2027-01-01T00:00:00Z"));
        Assert.Equal("ok", Describe("2027-01-01T00:00:00Z", "2027-01-01T00:00:01Z"));
    }

    // PW10
    [Theory]
    [InlineData("0", true)]
    [InlineData("30", true)]
    [InlineData("2147483647", true)]
    [InlineData("2147483648", false)]
    [InlineData("-1", false)]
    public void RotateBeforeDaysIsANonNegativeInt32(string value, bool accepted)
    {
        var error = PasswordCredentialsRules.DescribeDesired(J("{ \"rotateBeforeDays\": " + value + " }"), out var desired);

        Assert.Equal(accepted, error is null);
        if (accepted)
        {
            Assert.Equal(int.Parse(value, CultureInfo.InvariantCulture), desired!.RotateBeforeDays);
        }
    }

    // PW14
    [Fact]
    public void ATaintedRotateBeforeDaysIsRefused()
    {
        var properties = J("""{ "rotateBeforeDays": 30 }""");
        properties["rotateBeforeDays"] = SecretJson.CreateString("30");

        var error = PasswordCredentialsRules.DescribeDesired(properties, out var desired);

        Assert.NotNull(error);
        Assert.Null(desired);
        Assert.Contains("rotateBeforeDays", error, StringComparison.Ordinal);
    }

    // PW11
    private static PasswordCredentialSummary Live(DateTimeOffset end)
    {
        return new PasswordCredentialSummary(Guid.NewGuid().ToString("D"), "ci", end.AddDays(-170), end, "abc");
    }

    [Theory]
    [InlineData("Blue", "blue", true)]
    [InlineData("blue", "blue", false)]
    [InlineData("Blue", "Blue", false)]
    [InlineData(null, "blue", true)]
    [InlineData("blue", null, true)]
    [InlineData(null, null, false)]
    public void TheRotationTagIsComparedCaseSensitively(string? desiredTag, string? recordedTag, bool rotates)
    {
        var desired = new DesiredPasswordCredential(null, null, desiredTag, 30);
        var recorded = new RecordedPasswordCredential("k", null, recordedTag);

        var triggers = PasswordCredentialsRules.RotationTriggers(desired, Live(Now.AddDays(100)), recorded, Now);

        Assert.Equal(rotates, triggers.Any(t => t.Property == PasswordCredentialsRules.RotationTagProperty));
    }

    // PW16
    [Fact]
    public void ADateWithoutAnOffsetIsReadAsUtcWhateverTheHostTimeZone()
    {
        // Discriminating on a host whose time zone is not UTC (the development machine is Pacific time); on a UTC host the assertion holds trivially.
        Assert.True(PasswordCredentialsRules.TryParseDateTime(JsonValue.Create("2027-03-01T10:00:00"), out var value));

        Assert.Equal(TimeSpan.Zero, value.Offset);
        Assert.Equal(new DateTimeOffset(2027, 3, 1, 10, 0, 0, TimeSpan.Zero), value);
        Assert.Equal("2027-03-01T10:00:00Z", PasswordCredentialsRules.Format(value));
    }

    [Fact]
    public void ADateWithAnOffsetIsConvertedToUtc()
    {
        Assert.True(PasswordCredentialsRules.TryParseDateTime(JsonValue.Create("2027-03-01T10:00:00+02:00"), out var value));

        Assert.Equal(new DateTimeOffset(2027, 3, 1, 8, 0, 0, TimeSpan.Zero), value);
        Assert.Equal(TimeSpan.Zero, value.Offset);
    }
}
