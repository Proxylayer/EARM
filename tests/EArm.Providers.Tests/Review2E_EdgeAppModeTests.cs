using System;
using System.Linq;
using System.Threading.Tasks;
using EArm.Core.Diagnostics;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Providers.Tests.Infrastructure;
using EArm.TestSupport;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// Review 2E, edge pass: reading <c>/users/{id}/ownedObjects</c> is not supported with an application (app-only) token (Learn: user-list-ownedobjects),
/// and <c>/users/{id}/manager</c> can answer 403. Both used to fail the whole edge pass; they are warnings now and the property is left alone.
/// </summary>
public sealed class Review2E_EdgeAppModeTests
{
    private const string App = """
        { "type": "Microsoft.Entra/applications", "apiVersion": "2026-10-01-preview", "name": "test-app", "properties": { "displayName": "Test App" } }
        """;

    private const string AliceOwnsApp = """
        { "type": "Microsoft.Entra/users", "apiVersion": "2026-10-01-preview", "name": "alice@contoso.com",
          "properties": { "displayName": "Alice", "ownerOf": ["[reference('test-app').id]"] } }
        """;

    private const string AliceWithManager = """
        { "type": "Microsoft.Entra/users", "apiVersion": "2026-10-01-preview", "name": "alice@contoso.com",
          "properties": { "displayName": "Alice", "manager": "[reference(resourceId('Microsoft.Entra/users', 'boss@contoso.com'), '2026-10-01-preview').id]" } }
        """;

    private static ProviderHost Host(bool appOnly)
    {
        return new ProviderHost { AppOnlyToken = appOnly };
    }

    private static string Join(params string[] resources)
    {
        return string.Join(",", resources);
    }

    private static bool ReadsOwnedObjects(FakeGraphRequest request)
    {
        return request.Path.Contains("/ownedObjects", StringComparison.Ordinal);
    }

    [Fact]
    public async Task OwnerOfOnAUser_WithAnApplicationToken_IsAWarningRow_AndNothingIsReadOrWritten()
    {
        var host = Host(appOnly: true);

        var run = await host.CreateAsync(host.Resources(Join(App, AliceOwnsApp)));

        Check.Succeeded(run);
        var warning = Assert.Single(run.Executed.Diagnostics, d => d.Code == EngineDiagnosticCodes.EdgeUnsupportedInAppMode);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("ownerOf", warning.Message, StringComparison.Ordinal);
        Assert.Contains("application (app-only) token", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(run.Requests, ReadsOwnedObjects);
        Assert.DoesNotContain(run.Writes, w => w.Path.Contains("/owners/$ref", StringComparison.Ordinal));
        Assert.Contains(run.Change("alice@contoso.com").Reasons, r => r.Contains("EdgeUnsupportedInAppMode", StringComparison.Ordinal) || r.Contains("app-only", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OwnerOfOnAUser_WithAnApplicationToken_IsAlsoReportedByWhatIf_WithoutReading()
    {
        var host = Host(appOnly: true);

        var run = await host.WhatIfAsync(host.Resources(Join(App, AliceOwnsApp)));

        var warning = Assert.Single(run.Plan.Diagnostics, d => d.Code == EngineDiagnosticCodes.EdgeUnsupportedInAppMode);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.DoesNotContain(run.Requests, ReadsOwnedObjects);
        Assert.DoesNotContain(run.Change("alice@contoso.com").Deltas, d => d.Path.ToString() == "properties.ownerOf");
    }

    [Fact]
    public async Task OwnerOfOnAUser_WithADelegatedToken_StillAddsTheOwner()
    {
        var host = Host(appOnly: false);

        var run = await host.CreateAsync(host.Resources(Join(App, AliceOwnsApp)));

        Check.Succeeded(run);
        Assert.DoesNotContain(run.Executed.Diagnostics, d => d.Code == EngineDiagnosticCodes.EdgeUnsupportedInAppMode);
        Assert.Contains(run.Writes, w => w.Path.Contains("/owners/$ref", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheRecordedOwnerOf_IsKept_WhileAnApplicationTokenSkipsTheProperty()
    {
        var host = Host(appOnly: false);
        Check.Succeeded(await host.CreateAsync(host.Resources(Join(App, AliceOwnsApp))));
        var before = host.StateEntryJson("/providers/Microsoft.Entra/users/alice@contoso.com")?["relationships"]?["ownerOf"]?.ToJsonString();
        Assert.NotNull(before);

        host.AppOnlyToken = true;
        var rerun = await host.CreateAsync(host.Resources(Join(App, AliceOwnsApp)));

        Check.Succeeded(rerun);
        Assert.Contains(rerun.Executed.Diagnostics, d => d.Code == EngineDiagnosticCodes.EdgeUnsupportedInAppMode);
        Assert.Equal(before, host.StateEntryJson("/providers/Microsoft.Entra/users/alice@contoso.com")?["relationships"]?["ownerOf"]?.ToJsonString());
        Assert.DoesNotContain(rerun.Writes, w => w.Path.Contains("$ref", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ManagerRead403_IsAWarning_TheManagerIsNotWritten_AndTheRunSucceeds()
    {
        var host = Host(appOnly: false);
        host.Directory.AddUser("boss@contoso.com", "Boss");
        host.Fake.Failures.Forbid("/manager", method: "GET");

        var run = await host.CreateAsync(host.Resources(AliceWithManager));

        Check.Succeeded(run);
        var warning = Assert.Single(run.Executed.Diagnostics, d => d.Code == EngineDiagnosticCodes.ManagerReadForbidden);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("manager", warning.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(run.Writes, w => w.Path.EndsWith("/manager/$ref", StringComparison.Ordinal));
        Assert.DoesNotContain(run.Executed.Operations, o => o.Status == OperationStatus.Failed);
    }

    [Fact]
    public async Task ManagerRead403_OnWhatIfOfAnExistingUser_IsAWarningNotAnUnsupportedRow()
    {
        var host = Host(appOnly: false);
        host.Directory.AddUser("boss@contoso.com", "Boss");
        host.Directory.AddUser("alice@contoso.com", "Alice");
        host.Fake.Failures.Forbid("/manager", method: "GET");

        var run = await host.WhatIfAsync(host.Resources(AliceWithManager));

        Assert.Contains(run.Plan.Diagnostics, d => d.Code == EngineDiagnosticCodes.ManagerReadForbidden && d.Severity == DiagnosticSeverity.Warning);
        Assert.All(run.Changes, c => Assert.False(c.HasError, c.UnsupportedReason));
    }

    [Fact]
    public void TheNewCodes_AreWarningConstants()
    {
        Assert.Equal("EdgeUnsupportedInAppMode", EngineDiagnosticCodes.EdgeUnsupportedInAppMode);
        Assert.Equal("ManagerReadForbidden", EngineDiagnosticCodes.ManagerReadForbidden);
    }
}
