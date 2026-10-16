using System;
using System.Threading.Tasks;
using EArm.Core.Diagnostics;
using EArm.Engine;
using EArm.Providers.Tests.Infrastructure;
using EArm.TestSupport;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// Review 2E: the warnings a provider makes (<c>AppliedResource.Warnings</c>, and the <c>SecretExpiresSoon:</c> reasons of a plan row) reach the run's
/// diagnostics, in what-if and in create, as warnings against the resource; their codes are the documented <see cref="EngineDiagnosticCodes"/>.
/// </summary>
public sealed class Review2E_ProviderWarningDiagnosticTests
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return now;
        }
    }

    private const string ExpiringSecret = """
        { "type": "Microsoft.Entra/applications", "apiVersion": "2026-10-01-preview", "name": "pw-app", "properties": { "displayName": "PW" } },
        { "type": "Microsoft.Entra/applications/passwordCredentials", "apiVersion": "2026-10-01-preview", "name": "pw-app/ci", "properties": { "endDateTime": "2026-10-30T00:00:00Z" } }
        """;

    [Fact]
    public async Task AnAppliedResourcesWarnings_BecomeWarningDiagnostics_OfTheCreate()
    {
        var host = new ProviderHost();
        host.Fake.Failures.Forbid("deletedItems", method: "GET");

        var run = await host.CreateAsync(host.Resources(Templates.Resource("Microsoft.Entra/applications", "probe-app", """{ "displayName": "Probe" }""")));

        Check.Succeeded(run);
        var warning = Assert.Single(run.Executed.Diagnostics, d => d.Code == EngineDiagnosticCodes.SoftDeleteProbeSkipped);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("probe-app", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOwnerlessMicrosoft365Group_ReportsGroupHasNoOwnerAtCreate_AsADiagnostic()
    {
        var host = new ProviderHost();

        var run = await host.CreateAsync(host.Resources(Templates.Resource("Microsoft.Entra/groups", "ownerless", """{ "displayName": "Ownerless", "groupTypes": ["Unified"], "mailEnabled": true, "mailNickname": "ownerless" }""")));

        Check.Succeeded(run);
        Assert.Contains(run.Executed.Diagnostics, d => d.Code == EngineDiagnosticCodes.GroupHasNoOwnerAtCreate && d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public async Task SecretExpiresSoon_IsAWarningDiagnostic_InWhatIfAndInCreate()
    {
        var host = new ProviderHost();
        host.Provider<PasswordCredentialsProvider>(PasswordCredentialsProvider.ResourceType).Clock = new FixedClock(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
        Check.Succeeded(await host.CreateAsync(host.Resources(ExpiringSecret)));

        var whatIf = await host.WhatIfAsync(host.Resources(ExpiringSecret));
        var rerun = await host.CreateAsync(host.Resources(ExpiringSecret));

        var planned = Assert.Single(whatIf.Plan.Diagnostics, d => d.Code == EngineDiagnosticCodes.SecretExpiresSoon);
        Assert.Equal(DiagnosticSeverity.Warning, planned.Severity);
        Assert.Contains("2026-10-30", planned.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SecretExpiresSoon:", planned.Message, StringComparison.Ordinal);
        var applied = Assert.Single(rerun.Executed.Diagnostics, d => d.Code == EngineDiagnosticCodes.SecretExpiresSoon);
        Assert.Equal(DiagnosticSeverity.Warning, applied.Severity);
        Assert.Contains("pw-app/ci", applied.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASecretThatIsNotExpiring_ReportsNoWarning()
    {
        var host = new ProviderHost();
        const string template = """
            { "type": "Microsoft.Entra/applications", "apiVersion": "2026-10-01-preview", "name": "pw-app", "properties": { "displayName": "PW" } },
            { "type": "Microsoft.Entra/applications/passwordCredentials", "apiVersion": "2026-10-01-preview", "name": "pw-app/ci", "properties": { } }
            """;
        Check.Succeeded(await host.CreateAsync(host.Resources(template)));

        var whatIf = await host.WhatIfAsync(host.Resources(template));

        Assert.DoesNotContain(whatIf.Plan.Diagnostics, d => d.Code == EngineDiagnosticCodes.SecretExpiresSoon);
    }

    [Theory]
    [InlineData(nameof(ProviderWarnings.SoftDeleteProbeSkipped))]
    [InlineData(nameof(ProviderWarnings.GroupHasNoOwnerAtCreate))]
    [InlineData(nameof(ProviderWarnings.GroupAdoptedByDisplayName))]
    [InlineData(nameof(ProviderWarnings.SecretExpiresSoon))]
    [InlineData(nameof(ProviderWarnings.RotationTagNotStamped))]
    [InlineData(nameof(ProviderWarnings.PasswordCredentialOrphaned))]
    public void TheProviderWarningCodes_AreTheDocumentedEngineCodes(string name)
    {
        string fromProviders = (string)typeof(ProviderWarnings).GetField(name)!.GetRawConstantValue()!;
        string fromEngine = (string)typeof(EngineDiagnosticCodes).GetField(name)!.GetRawConstantValue()!;

        Assert.Equal(name, fromProviders);
        Assert.Equal(fromEngine, fromProviders);
    }
}
