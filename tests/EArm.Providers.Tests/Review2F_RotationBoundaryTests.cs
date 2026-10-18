using System;
using System.Linq;
using System.Threading.Tasks;
using EArm.Engine.Providers;
using EArm.Providers.Tests.Infrastructure;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// Review 2F / mutation review: the exact edges of the password credential rotation rules, with a fixed clock. A mutant that turns
/// <c>&lt;</c> into <c>&lt;=</c> (or the expiry <c>&lt;=</c> into <c>&lt;</c>) in <see cref="PasswordCredentialsRules.AssessRotation"/> must fail here:
/// <c>liveEnd - now &lt; rotateBeforeDays</c> is the window (exactly rotateBeforeDays left is NOT in it), <c>liveEnd &lt;= now</c> is expired,
/// and a replacement is only worth it when it ends strictly later than the live credential.
/// </summary>
public sealed class Review2F_RotationBoundaryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static PasswordCredentialSummary Live(DateTimeOffset end)
    {
        return new PasswordCredentialSummary(Guid.NewGuid().ToString("D"), "ci", end.AddDays(-170), end, "abc");
    }

    private static DesiredPasswordCredential Desired(DateTimeOffset? end = null, int rotateBeforeDays = 30)
    {
        return new DesiredPasswordCredential(end, null, null, rotateBeforeDays);
    }

    private static RotationAssessment Assess(DesiredPasswordCredential desired, DateTimeOffset liveEnd)
    {
        return PasswordCredentialsRules.AssessRotation(desired, Live(liveEnd), Now);
    }

    // ---- the window: liveEnd - now < rotateBeforeDays ----

    [Fact]
    public void ExactlyRotateBeforeDaysLeft_IsOutsideTheWindow_NothingIsRotatedAndNothingWarns()
    {
        var assessment = Assess(Desired(rotateBeforeDays: 30), Now.AddDays(30));

        Assert.Empty(assessment.Triggers);
        Assert.Empty(assessment.Warnings);
    }

    [Fact]
    public void OneTickInsideTheWindow_IsRotated()
    {
        var assessment = Assess(Desired(rotateBeforeDays: 30), Now.AddDays(30).AddTicks(-1));

        var trigger = Assert.Single(assessment.Triggers);
        Assert.Equal(PasswordCredentialsRules.EndDateTimeProperty, trigger.Property);
        Assert.Contains("expires on", trigger.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void OneTickOutsideTheWindow_IsNotRotated()
    {
        Assert.Empty(Assess(Desired(rotateBeforeDays: 30), Now.AddDays(30).AddTicks(1)).Triggers);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(90)]
    public void TheEdgeMovesWithRotateBeforeDays(int days)
    {
        Assert.Empty(Assess(Desired(rotateBeforeDays: days), Now.AddDays(days)).Triggers);
        Assert.Single(Assess(Desired(rotateBeforeDays: days), Now.AddDays(days).AddSeconds(-1)).Triggers);
    }

    // ---- expiry: liveEnd <= now ----

    [Fact]
    public void ACredentialExpiringExactlyNow_IsExpired_AndRotatedEvenWithAZeroDayWindow()
    {
        var assessment = Assess(Desired(rotateBeforeDays: 0), Now);

        var trigger = Assert.Single(assessment.Triggers);
        Assert.Contains("expired on", trigger.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ACredentialExpiringOneSecondFromNow_WithAZeroDayWindow_IsNotYetRotated()
    {
        var assessment = Assess(Desired(rotateBeforeDays: 0), Now.AddSeconds(1));

        Assert.Empty(assessment.Triggers);
        Assert.Empty(assessment.Warnings);
    }

    [Fact]
    public void AZeroDayWindow_NeverCountsALiveCredentialAsInTheWindow()
    {
        Assert.Empty(Assess(Desired(rotateBeforeDays: 0), Now.AddTicks(1)).Triggers);
        Assert.Single(Assess(Desired(rotateBeforeDays: 0), Now.AddTicks(-1)).Triggers);
    }

    // ---- the replacement must end strictly later than the live credential ----

    [Fact]
    public void AReplacementEndingExactlyWhenTheLiveCredentialEnds_IsNotWorthRotating_ItWarns()
    {
        var liveEnd = PasswordCredentialsRules.DefaultEndDateTime(Now);

        var assessment = Assess(Desired(rotateBeforeDays: 365), liveEnd);

        Assert.Empty(assessment.Triggers);
        var warning = Assert.Single(assessment.Warnings);
        Assert.Contains("NOT rotated", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AReplacementEndingOneSecondLaterThanTheLiveCredential_IsRotated()
    {
        var liveEnd = PasswordCredentialsRules.DefaultEndDateTime(Now).AddSeconds(-1);

        var assessment = Assess(Desired(rotateBeforeDays: 365), liveEnd);

        Assert.Single(assessment.Triggers);
        Assert.Empty(assessment.Warnings);
    }

    [Fact]
    public void AReplacementEndingOneSecondEarlierThanTheLiveCredential_WarnsInsteadOfRotating()
    {
        var liveEnd = PasswordCredentialsRules.DefaultEndDateTime(Now).AddSeconds(1);

        var assessment = Assess(Desired(rotateBeforeDays: 365), liveEnd);

        Assert.Empty(assessment.Triggers);
        Assert.Single(assessment.Warnings);
    }

    // ---- through the provider, with a fixed clock ----

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow()
        {
            return Now;
        }
    }

    private const string Template = """
        { "type": "Microsoft.Entra/applications", "apiVersion": "2026-10-01-preview", "name": "rot-app", "properties": { "displayName": "Rot" } },
        { "type": "Microsoft.Entra/applications/passwordCredentials", "apiVersion": "2026-10-01-preview", "name": "rot-app/ci", "properties": { "rotateBeforeDays": 30 } }
        """;

    [Fact]
    public async Task TheProvider_UsesItsClock_AtTheExactBoundary()
    {
        var clock = new FixedClock(Now);
        var host = new ProviderHost();
        host.Provider<PasswordCredentialsProvider>(PasswordCredentialsProvider.ResourceType).Clock = clock;
        Check.Succeeded(await host.CreateAsync(host.Resources(Template)));
        var createdEnd = PasswordCredentialsRules.DefaultEndDateTime(Now);

        // Exactly 30 days before the end: still outside the window, a rerun changes nothing.
        clock.Now = createdEnd.AddDays(-30);
        host.ClearRequests();
        var atEdge = await host.CreateAsync(host.Resources(Template));
        Check.Succeeded(atEdge);
        Check.NoWrites(atEdge);
        Assert.Equal(ChangeType.NoChange, atEdge.ChangeOfType(PasswordCredentialsProvider.ResourceType).Type);

        // One second later the credential is inside the window and a replacement lives longer: rotated (a second credential, same name).
        clock.Now = createdEnd.AddDays(-30).AddSeconds(1);
        host.ClearRequests();
        var inside = await host.CreateAsync(host.Resources(Template));
        Check.Succeeded(inside);
        Assert.Equal(ChangeType.Modify, inside.ChangeOfType(PasswordCredentialsProvider.ResourceType).Type);
        Assert.Contains(inside.Writes, w => w.Path.EndsWith("/addPassword", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheProvider_RotatesAnExpiredCredential_ThatExpiresExactlyNow()
    {
        var clock = new FixedClock(Now);
        var host = new ProviderHost();
        host.Provider<PasswordCredentialsProvider>(PasswordCredentialsProvider.ResourceType).Clock = clock;
        const string zeroWindow = """
            { "type": "Microsoft.Entra/applications", "apiVersion": "2026-10-01-preview", "name": "rot-app", "properties": { "displayName": "Rot" } },
            { "type": "Microsoft.Entra/applications/passwordCredentials", "apiVersion": "2026-10-01-preview", "name": "rot-app/ci", "properties": { "rotateBeforeDays": 0 } }
            """;
        Check.Succeeded(await host.CreateAsync(host.Resources(zeroWindow)));
        var createdEnd = PasswordCredentialsRules.DefaultEndDateTime(Now);
        string firstKeyId = host.Directory.Objects("/applications").Single()["passwordCredentials"]![0]!["keyId"]!.GetValue<string>();

        clock.Now = createdEnd.AddSeconds(-1);
        host.ClearRequests();
        var before = await host.CreateAsync(host.Resources(zeroWindow));
        Check.NoWrites(before);

        clock.Now = createdEnd;
        host.ClearRequests();
        var atExpiry = await host.CreateAsync(host.Resources(zeroWindow));
        Check.Succeeded(atExpiry);
        Assert.Contains(atExpiry.Writes, w => w.Path.EndsWith("/addPassword", StringComparison.Ordinal));
        string rotatedKeyId = host.Directory.Objects("/applications").Single()["passwordCredentials"]![0]!["keyId"]!.GetValue<string>();
        Assert.NotEqual(firstKeyId, rotatedKeyId);
    }
}
