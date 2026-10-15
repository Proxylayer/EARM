using System;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using EArm.Az;
using EArm.Core.Secrets;
using EArm.Engine.Providers;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>Pure rules of the password credentials provider (WI-11): desired parsing, selection, rotation triggers with a fixed clock, Graph answers, views and state extra.</summary>
public sealed class PasswordCredentialsRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static PasswordCredentialSummary Credential(string keyId, DateTimeOffset? end, string? name = "ci-secret", string? hint = "fak")
    {
        return new PasswordCredentialSummary(keyId, name, end?.AddDays(-170), end, hint);
    }

    [Fact]
    public void DescribeDesired_ParsesDatesTagsAndThreshold_WithDefaults()
    {
        Assert.Null(PasswordCredentialsRules.DescribeDesired(new JsonObject(), out var defaults));
        Assert.NotNull(defaults);
        Assert.Null(defaults.EndDateTime);
        Assert.Null(defaults.StartDateTime);
        Assert.Null(defaults.RotationTag);
        Assert.Equal(PasswordCredentialsRules.DefaultRotateBeforeDays, defaults.RotateBeforeDays);

        var properties = (JsonObject)JsonNode.Parse("""{ "endDateTime": "2028-01-01T00:00:00Z", "startDateTime": "2027-01-01T02:00:00+02:00", "rotationTag": 42, "rotateBeforeDays": 5 }""")!;
        Assert.Null(PasswordCredentialsRules.DescribeDesired(properties, out var desired));
        Assert.NotNull(desired);
        Assert.Equal(new DateTimeOffset(2028, 1, 1, 0, 0, 0, TimeSpan.Zero), desired.EndDateTime);
        Assert.Equal(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), desired.StartDateTime);
        Assert.Equal("42", desired.RotationTag);
        Assert.Equal(5, desired.RotateBeforeDays);
    }

    [Theory]
    [InlineData("""{ "endDateTime": "not-a-date" }""", "endDateTime")]
    [InlineData("""{ "startDateTime": "nope" }""", "startDateTime")]
    [InlineData("""{ "endDateTime": "2027-01-01T00:00:00Z", "startDateTime": "2028-01-01T00:00:00Z" }""", "must be later than")]
    [InlineData("""{ "rotationTag": { "x": 1 } }""", "rotationTag")]
    [InlineData("""{ "rotateBeforeDays": -1 }""", "rotateBeforeDays")]
    [InlineData("""{ "rotateBeforeDays": "ten" }""", "non-negative integer")]
    public void DescribeDesired_RejectsInvalidDeclarations(string json, string expectedText)
    {
        string? problem = PasswordCredentialsRules.DescribeDesired((JsonObject)JsonNode.Parse(json)!, out var desired);

        Assert.NotNull(problem);
        Assert.Contains(expectedText, problem, StringComparison.Ordinal);
        Assert.Null(desired);
    }

    [Fact]
    public void DescribeDesired_RejectsASecretRotationTag()
    {
        var properties = new JsonObject { ["rotationTag"] = SecretJson.CreateString("tag") };
        string? problem = PasswordCredentialsRules.DescribeDesired(properties, out _);
        Assert.NotNull(problem);
        Assert.Contains("rotationTag", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Select_PrefersTheRecordedKeyId_OtherwiseTheNewest()
    {
        var older = Credential("aaaaaaaa-0000-0000-0000-000000000001", Now.AddDays(10));
        var newer = Credential("aaaaaaaa-0000-0000-0000-000000000002", Now.AddDays(100));
        var undated = new PasswordCredentialSummary("aaaaaaaa-0000-0000-0000-000000000003", "ci-secret", null, null, null);

        Assert.Same(newer, PasswordCredentialsRules.Select([older, newer, undated], null));
        Assert.Same(older, PasswordCredentialsRules.Select([older, newer], "AAAAAAAA-0000-0000-0000-000000000001"));
        Assert.Same(newer, PasswordCredentialsRules.Select([older, newer], "aaaaaaaa-0000-0000-0000-00000000dead"));
        Assert.Same(undated, PasswordCredentialsRules.Select([undated], null));
        Assert.Null(PasswordCredentialsRules.Select([], null));
    }

    [Fact]
    public void Summarize_AndSameName_MatchDisplayNamesCaseSensitively_AndSkipEntriesWithoutKeyId()
    {
        var credentials = PasswordCredentialsRules.Summarize((JsonArray)JsonNode.Parse("""
            [ { "keyId": "k1", "displayName": "ci-secret", "endDateTime": "2027-01-01T00:00:00Z", "hint": "abc" },
              { "keyId": "k2", "displayName": "CI-SECRET" },
              { "displayName": "ci-secret" },
              { "id": "k3", "displayName": "ci-secret" } ]
            """)!);

        Assert.Equal(3, credentials.Count);
        Assert.Equal(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), credentials[0].EndDateTime);
        Assert.Equal("abc", credentials[0].Hint);
        var sameName = PasswordCredentialsRules.SameName(credentials, "ci-secret");
        Assert.Equal(["k1", "k3"], [.. sameName.Select(c => c.KeyId)]);
        Assert.Empty(PasswordCredentialsRules.Summarize(null));
    }

    [Fact]
    public void RotationTriggers_AreEmptyWhenNothingDiffers()
    {
        var desired = new DesiredPasswordCredential(null, null, null, 30);
        var live = Credential("k", Now.AddDays(100));

        Assert.Empty(PasswordCredentialsRules.RotationTriggers(desired, live, RecordedPasswordCredential.Empty, Now));
        Assert.Empty(PasswordCredentialsRules.RotationTriggers(desired with { RotationTag = "q3" }, live, new RecordedPasswordCredential("k", live.EndDateTime, "q3"), Now));
        Assert.Empty(PasswordCredentialsRules.RotationTriggers(desired with { EndDateTime = live.EndDateTime!.Value.AddMilliseconds(500) }, live, RecordedPasswordCredential.Empty, Now));
    }

    [Fact]
    public void RotationTriggers_NameTheTagChange_WithBothSides()
    {
        var live = Credential("k", Now.AddDays(100));

        var trigger = Assert.Single(PasswordCredentialsRules.RotationTriggers(new DesiredPasswordCredential(null, null, "2026Q4", 30), live, new RecordedPasswordCredential("k", null, "2026Q3"), Now));
        Assert.Equal("rotationTag", trigger.Property);
        Assert.Equal("2026Q3", trigger.Before!.GetValue<string>());
        Assert.Equal("2026Q4", trigger.After!.GetValue<string>());
        Assert.Contains("'2026Q3' → '2026Q4'", trigger.Reason, StringComparison.Ordinal);

        var removed = Assert.Single(PasswordCredentialsRules.RotationTriggers(new DesiredPasswordCredential(null, null, null, 30), live, new RecordedPasswordCredential("k", null, "2026Q3"), Now));
        Assert.Null(removed.After);
        Assert.Contains("→ none", removed.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void RotationTriggers_DetectDeclaredDateChanges()
    {
        var live = Credential("k", Now.AddDays(100));
        var wantedEnd = Now.AddDays(200);
        var wantedStart = Now.AddDays(-1);

        var triggers = PasswordCredentialsRules.RotationTriggers(new DesiredPasswordCredential(wantedEnd, wantedStart, null, 30), live, RecordedPasswordCredential.Empty, Now);

        Assert.Equal(2, triggers.Count);
        Assert.Equal("endDateTime", triggers[0].Property);
        Assert.Equal(PasswordCredentialsRules.Format(wantedEnd), triggers[0].After!.GetValue<string>());
        Assert.Equal("startDateTime", triggers[1].Property);
        Assert.Equal(PasswordCredentialsRules.Format(wantedStart), triggers[1].After!.GetValue<string>());
    }

    [Fact]
    public void RotationTriggers_RotateOnExpiry_AndWithinTheThreshold_UsingTheClock()
    {
        var desired = new DesiredPasswordCredential(null, null, null, 30);

        var soon = Assert.Single(PasswordCredentialsRules.RotationTriggers(desired, Credential("k", Now.AddDays(10)), RecordedPasswordCredential.Empty, Now));
        Assert.Equal("endDateTime", soon.Property);
        Assert.Contains("expires on", soon.Reason, StringComparison.Ordinal);
        Assert.Contains("in 10 days", soon.Reason, StringComparison.Ordinal);
        Assert.Contains("fewer than rotateBeforeDays (30)", soon.Reason, StringComparison.Ordinal);
        Assert.Equal(PasswordCredentialsRules.Format(PasswordCredentialsRules.DefaultEndDateTime(Now)), soon.After!.GetValue<string>());

        var expired = Assert.Single(PasswordCredentialsRules.RotationTriggers(desired, Credential("k", Now.AddDays(-1)), RecordedPasswordCredential.Empty, Now));
        Assert.Contains("expired on", expired.Reason, StringComparison.Ordinal);

        Assert.Empty(PasswordCredentialsRules.RotationTriggers(desired with { RotateBeforeDays = 5 }, Credential("k", Now.AddDays(10)), RecordedPasswordCredential.Empty, Now));
        Assert.Empty(PasswordCredentialsRules.RotationTriggers(desired, Credential("k", Now.AddDays(30).AddMinutes(1)), RecordedPasswordCredential.Empty, Now));
        Assert.Single(PasswordCredentialsRules.RotationTriggers(desired, Credential("k", Now.AddDays(30).AddMinutes(-1)), RecordedPasswordCredential.Empty, Now));
        Assert.Empty(PasswordCredentialsRules.RotationTriggers(desired, Credential("k", null), RecordedPasswordCredential.Empty, Now));
    }

    [Fact]
    public void IsUnknownKeyId_Accepts404_AndGraphsKeyIdMessages_Only()
    {
        Assert.True(PasswordCredentialsRules.IsUnknownKeyId(new GraphException(HttpStatusCode.NotFound, "Request_ResourceNotFound", "Resource not found.", RetryHint.Default)));
        Assert.True(PasswordCredentialsRules.IsUnknownKeyId(new GraphException(HttpStatusCode.BadRequest, "Request_BadRequest", "No password credential found with keyId 00000000-0000-0000-0000-00000000dead.", RetryHint.Default)));
        Assert.True(PasswordCredentialsRules.IsUnknownKeyId(new GraphException(HttpStatusCode.BadRequest, "Request_BadRequest", "The credential was not found.", RetryHint.Default)));
        Assert.False(PasswordCredentialsRules.IsUnknownKeyId(new GraphException(HttpStatusCode.BadRequest, "Request_BadRequest", "Tenant policy forbids this.", RetryHint.Default)));
        Assert.False(PasswordCredentialsRules.IsUnknownKeyId(new GraphException(HttpStatusCode.Forbidden, "Authorization_RequestDenied", "No password credential found with keyId x.", RetryHint.Default)));
    }

    [Fact]
    public void LiveView_CarriesASecretLeafAndTheAvailabilityMarker_NeverPlainText()
    {
        var credential = Credential("k", Now.AddDays(100));

        var creating = PasswordCredentialsRules.LiveView(credential, new SecretValue("plain-secret"));
        Assert.True(SecretJson.IsSecret(creating[LiveSecrets.SecretTextProperty]));
        Assert.True(creating[LiveSecrets.AvailabilityMarker]!.GetValue<bool>());
        Assert.False(LiveSecrets.IsSecretTextUnavailable(creating));
        Assert.DoesNotContain("plain-secret", creating.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("\"secretText\":\"***\"", creating.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal("k", creating["id"]!.GetValue<string>());
        Assert.Equal("k", creating["keyId"]!.GetValue<string>());
        Assert.Equal(PasswordCredentialsRules.Format(credential.EndDateTime!.Value), creating["endDateTime"]!.GetValue<string>());

        var rerun = PasswordCredentialsRules.LiveView(credential, null);
        Assert.Null(rerun[LiveSecrets.SecretTextProperty]);
        Assert.False(rerun[LiveSecrets.AvailabilityMarker]!.GetValue<bool>());
        Assert.True(LiveSecrets.IsSecretTextUnavailable(rerun));
    }

    [Fact]
    public void ExtraFor_AndDiffView_HaveNoSecret()
    {
        var credential = Credential("k", Now.AddDays(100));

        var extra = PasswordCredentialsRules.ExtraFor(credential, "q3");
        Assert.Equal("{\"keyId\":\"k\",\"endDateTime\":\"" + PasswordCredentialsRules.Format(credential.EndDateTime!.Value) + "\",\"hint\":\"fak\",\"rotationTag\":\"q3\"}", extra.ToJsonString());
        Assert.False(PasswordCredentialsRules.ExtraFor(credential, null).ContainsKey("rotationTag"));
        Assert.False(SecretJson.ContainsSecret(extra));
        Assert.DoesNotContain("secretText", extra.ToJsonString(), StringComparison.Ordinal);

        var view = PasswordCredentialsRules.DiffView(credential, "q3");
        Assert.Equal("q3", view["rotationTag"]!.GetValue<string>());
        Assert.False(PasswordCredentialsRules.DiffView(credential).ContainsKey("rotationTag"));
        Assert.False(view.ContainsKey("secretText"));

        var recorded = PasswordCredentialsRules.Recorded(extra);
        Assert.Equal("k", recorded.KeyId);
        Assert.Equal("q3", recorded.RotationTag);
        Assert.True(PasswordCredentialsRules.SameInstant(credential.EndDateTime, recorded.EndDateTime));
        Assert.Same(RecordedPasswordCredential.Empty, PasswordCredentialsRules.Recorded(null));
    }

    [Fact]
    public void Bodies_AndDateHelpers_AreCanonical()
    {
        var end = new DateTimeOffset(2028, 1, 1, 2, 0, 0, TimeSpan.FromHours(2));
        Assert.Equal("""{"passwordCredential":{"displayName":"ci-secret","endDateTime":"2028-01-01T00:00:00Z"}}""", PasswordCredentialsRules.AddPasswordBody("ci-secret", end, null).ToJsonString());
        Assert.Equal("""{"passwordCredential":{"displayName":"ci-secret","endDateTime":"2028-01-01T00:00:00Z","startDateTime":"2027-01-01T00:00:00Z"}}""", PasswordCredentialsRules.AddPasswordBody("ci-secret", end, new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)).ToJsonString());
        Assert.Equal("""{"keyId":"k"}""", PasswordCredentialsRules.RemovePasswordBody("k").ToJsonString());
        Assert.Equal(Now.AddDays(180), PasswordCredentialsRules.DefaultEndDateTime(Now));
        Assert.True(PasswordCredentialsRules.SameInstant(null, null));
        Assert.False(PasswordCredentialsRules.SameInstant(Now, null));
        Assert.False(PasswordCredentialsRules.SameInstant(Now, Now.AddSeconds(2)));
        Assert.True(PasswordCredentialsRules.TryParseDateTime(JsonValue.Create("2028-01-01"), out var date));
        Assert.Equal(TimeSpan.Zero, date.Offset);
        Assert.False(PasswordCredentialsRules.TryParseDateTime(SecretJson.CreateString("2028-01-01"), out _));
        Assert.False(PasswordCredentialsRules.TryParseDateTime(null, out _));
        Assert.True(PasswordCredentialsRules.KeyIdEquals("ABC", "abc"));
    }
}
