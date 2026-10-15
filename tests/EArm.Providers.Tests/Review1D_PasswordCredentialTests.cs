using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Graph;
using EArm.Providers.Tests.Infrastructure;
using EArm.TestSupport;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// Review 1D / E-07 and G-12: a secret is rotated only when a replacement would live longer (a declared fixed <c>endDateTime</c> inside the
/// rotation window used to rotate it on EVERY run); otherwise <c>SecretExpiresSoon</c> is reported and nothing is rotated. An
/// <c>addPassword</c> that fails without a usable answer is never repeated; an unrecorded same-name credential is reported by keyId.
/// </summary>
public sealed class Review1D_PasswordCredentialTests
{
    private const string SecretType = PasswordCredentialsProvider.ResourceType;
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static PasswordCredentialSummary Live(DateTimeOffset end)
    {
        return new PasswordCredentialSummary(Guid.NewGuid().ToString("D"), "ci", Now.AddDays(-100), end, "abc");
    }

    private static DesiredPasswordCredential Desired(DateTimeOffset? end = null, int rotateBeforeDays = 30, string? tag = null)
    {
        return new DesiredPasswordCredential(end, null, tag, rotateBeforeDays);
    }

    [Fact]
    public void E07_AFixedEndDateInsideTheWindow_IsNotRotated_ItWarnsWithTheDate()
    {
        var end = new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero);

        var assessment = PasswordCredentialsRules.AssessRotation(Desired(end), Live(end), Now);

        Assert.Empty(assessment.Triggers);
        var warning = Assert.Single(assessment.Warnings);
        Assert.Equal(ProviderWarnings.SecretExpiresSoon, warning.Code);
        Assert.Contains("2026-10-30T00:00:00Z", warning.Message, StringComparison.Ordinal);
        Assert.Contains("NOT rotated", warning.Message, StringComparison.Ordinal);
        Assert.Empty(PasswordCredentialsRules.RotationTriggers(Desired(end), Live(end), Now));
    }

    [Fact]
    public void E07_ADefaultEndThatIsNotLaterThanTheLiveOne_IsNotRotatedEveryRun()
    {
        // rotateBeforeDays 365 > the 180-day default lifetime: the credential is "in the window" for its whole life, and a replacement ends at now + 180 days.
        var live = Live(Now.AddDays(200));

        var assessment = PasswordCredentialsRules.AssessRotation(Desired(rotateBeforeDays: 365), live, Now);

        Assert.Empty(assessment.Triggers);
        Assert.Single(assessment.Warnings);
    }

    [Fact]
    public void E07_ADefaultEndThatIsLaterThanTheLiveOne_IsRotatedAsBefore()
    {
        var live = Live(Now.AddDays(10));

        var trigger = Assert.Single(PasswordCredentialsRules.RotationTriggers(Desired(), live, Now));

        Assert.Equal(PasswordCredentialsRules.EndDateTimeProperty, trigger.Property);
        Assert.Contains("expires on", trigger.Reason, StringComparison.Ordinal);
        Assert.Empty(PasswordCredentialsRules.AssessRotation(Desired(), live, Now).Warnings);
    }

    [Fact]
    public void E07_ADeclaredEndLaterThanTheLiveOne_IsRotated_NoWarning()
    {
        var live = Live(new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero));
        var wanted = new DateTimeOffset(2027, 6, 1, 0, 0, 0, TimeSpan.Zero);

        var assessment = PasswordCredentialsRules.AssessRotation(Desired(wanted), live, Now);

        Assert.Equal([PasswordCredentialsRules.EndDateTimeProperty], assessment.Triggers.Select(t => t.Property).ToArray());
        Assert.Empty(assessment.Warnings);
    }

    [Fact]
    public void E07_AnExpiredCredentialWhoseDeclaredEndIsInThePast_WarnsInsteadOfRotatingIntoAnExpiredSecret()
    {
        var past = Now.AddDays(-3);

        var assessment = PasswordCredentialsRules.AssessRotation(Desired(past), Live(past), Now);

        Assert.Empty(assessment.Triggers);
        Assert.Contains("expired on", Assert.Single(assessment.Warnings).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void E07_AChangedRotationTag_StillRotates_EvenWhenTheEndCannotBeExtended()
    {
        var end = new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero);

        var assessment = PasswordCredentialsRules.AssessRotation(Desired(end, tag: "q4"), Live(end) with { DisplayName = "ci#q3" }, Now);

        Assert.Equal([PasswordCredentialsRules.RotationTagProperty], assessment.Triggers.Select(t => t.Property).ToArray());
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return now;
        }
    }

    [Fact]
    public async Task E07_AFixedEndInsideTheWindow_IsNoChangeOnEveryRun_WithTheWarningAsAReason()
    {
        var host = new ProviderHost();
        host.Provider<PasswordCredentialsProvider>(SecretType).Clock = new FixedClock(Now);
        const string template = """
            { "type": "Microsoft.Entra/applications", "apiVersion": "2026-10-01-preview", "name": "pw-app", "properties": { "displayName": "PW" } },
            { "type": "Microsoft.Entra/applications/passwordCredentials", "apiVersion": "2026-10-01-preview", "name": "pw-app/ci", "properties": { "endDateTime": "2026-10-30T00:00:00Z" } }
            """;
        Check.Succeeded(await host.CreateAsync(host.Resources(template)));

        for (int i = 0; i < 3; i++)
        {
            host.ClearRequests();
            var rerun = await host.CreateAsync(host.Resources(template));

            Check.Succeeded(rerun);
            Check.NoWrites(rerun);
            var change = rerun.ChangeOfType(SecretType);
            Assert.Equal(ChangeType.NoChange, change.Type);
            Assert.Contains(change.Reasons, r => r.StartsWith(ProviderWarnings.SecretExpiresSoon + ":", StringComparison.Ordinal) && r.Contains("2026-10-30", StringComparison.Ordinal));
        }

        Assert.Single(host.Directory.Objects("/applications").Single()["passwordCredentials"]!.AsArray());
    }

    // ---- G-12: ambiguous addPassword ----

    private static async Task<(ProviderHost Host, ResourceInstance Instance, PasswordCredentialsProvider Provider)> ArrangeAsync()
    {
        var host = new ProviderHost();
        Check.Succeeded(await host.CreateAsync(host.Resources(Templates.Resource(ApplicationsProvider.ResourceType, "pw-app", """{ "displayName": "PW" }"""))));
        var whatIf = await host.WhatIfAsync(host.Resources(Templates.Resource(SecretType, "pw-app/ci", "{ }")));
        return (host, whatIf.Plan.Instances.Single(), host.Provider<PasswordCredentialsProvider>(SecretType));
    }

    [Fact]
    public async Task G12_AnAmbiguousAddPassword_IsNotRepeated_AndTheOrphanIsReportedByKeyId()
    {
        var (host, instance, provider) = await ArrangeAsync();
        int attempts = 0;
        var graph = new InterceptingGraphClient(host.Fake)
        {
            PostOverride = (request, body) =>
            {
                if (!request.Path.EndsWith("/addPassword", StringComparison.Ordinal))
                {
                    return null;
                }

                attempts++;
                host.Fake.PostAsync(request, body, ProviderHost.Ct).GetAwaiter().GetResult(); // Graph committed the credential ...
                throw new GraphTransportException(GraphTransportFailure.Timeout, "POST: the request to graph.microsoft.com timed out.", ambiguous: true); // ... but the answer was lost.
            },
        };

        var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.CreateAsync(instance, graph, ProviderHost.Ct));

        Assert.Equal(1, attempts);
        Assert.Equal(EngineDiagnosticCodes.ResourceApplyFailed, ex.Code);
        string keyId = host.Directory.Objects("/applications").Single()["passwordCredentials"]![0]!["keyId"]!.GetValue<string>();
        Assert.Contains(ProviderWarnings.PasswordCredentialOrphaned, ex.Message, StringComparison.Ordinal);
        Assert.Contains(keyId, ex.Message, StringComparison.Ordinal);
        Assert.Contains("never deletes them automatically", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(host.Fake.Writes, w => w.Path.EndsWith("/removePassword", StringComparison.Ordinal));
        Assert.IsType<GraphTransportException>(ex.InnerException);
    }

    [Fact]
    public async Task G12_AnAmbiguousAddPassword_ThatNeverReachedGraph_SaysItCanSimplyBeRunAgain()
    {
        var (host, instance, provider) = await ArrangeAsync();
        var graph = new InterceptingGraphClient(host.Fake)
        {
            PostOverride = (request, _) => request.Path.EndsWith("/addPassword", StringComparison.Ordinal)
                ? throw new GraphTransportException(GraphTransportFailure.Timeout, "POST: timed out.", ambiguous: true)
                : null,
        };

        var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.CreateAsync(instance, graph, ProviderHost.Ct));

        Assert.DoesNotContain(ProviderWarnings.PasswordCredentialOrphaned, ex.Message, StringComparison.Ordinal);
        Assert.Contains("can simply be run again", ex.Message, StringComparison.Ordinal);
        Assert.Empty(host.Directory.Objects("/applications").Single()["passwordCredentials"]?.AsArray() ?? []);
    }

    [Fact]
    public async Task G12_ANonAmbiguousFailure_KeepsItsOriginalType()
    {
        var (host, instance, provider) = await ArrangeAsync();
        host.Fake.Failures.Forbid("/addPassword", method: "POST");

        var ex = await Assert.ThrowsAsync<GraphException>(() => provider.CreateAsync(instance, host.Fake, ProviderHost.Ct));

        Assert.True(ex.IsForbidden);
    }
}
