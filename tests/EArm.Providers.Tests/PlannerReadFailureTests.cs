using System;
using System.Linq;
using System.Threading.Tasks;
using EArm.Engine;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Providers.Tests.Infrastructure;
using Xunit;
using static EArm.Providers.Tests.Infrastructure.DirectoryTemplates;

namespace EArm.Providers.Tests;

/// <summary>
/// What-if when Graph refuses or fails the reads the plan depends on: the edge reads of an existing declarer, the Complete-mode
/// read of an orphan, and an existing-resource <c>reference()</c> lookup. Each failure becomes a row or diagnostic that names it;
/// none aborts the plan, none writes.
/// </summary>
public sealed class PlannerReadFailureTests
{
    [Fact]
    public async Task EdgeReadsThatFail_MarkTheDeclaringRowUnplannable_WithTheReason()
    {
        var host = new ProviderHost();
        string template = Join(Group(), Alice("\"memberOf\": [\"[reference('test-admins').id]\"]"));
        Check.Succeeded(await host.CreateAsync(host.Resources(template)));
        host.ClearRequests();
        host.Fake.Failures.Forbid("/memberOf", "GET");

        var whatIf = await host.WhatIfAsync(host.Resources(template));

        var row = whatIf.ChangeOfType("Microsoft.Entra/users");
        Assert.True(row.HasError);
        Assert.Contains("could not be planned", row.UnsupportedReason, StringComparison.Ordinal);
        Assert.Contains("403", row.UnsupportedReason, StringComparison.Ordinal);
        Assert.False(whatIf.ChangeOfType("Microsoft.Entra/groups").HasError);
        Check.NoWrites(whatIf);
    }

    [Fact]
    public async Task ACompleteModeOrphanThatCannotBeRead_IsAnUnsupportedDeleteRow_NotAnAbort()
    {
        var host = new ProviderHost();
        Check.Succeeded(await host.CreateAsync(host.Resources(Group())));
        host.Fake.Failures.Forbid("/groups", "GET");

        var whatIf = await host.WhatIfAsync(host.Resources(string.Empty), DeploymentMode.Complete);

        var row = Assert.Single(whatIf.Plan.Changes, c => c.Type == ChangeType.Delete);
        Assert.True(row.HasError);
        Assert.Contains("could not be read", row.UnsupportedReason, StringComparison.Ordinal);
        Check.NoWrites(whatIf);
    }

    [Fact]
    public async Task AnOrphanThatNoLongerExistsInTheTenant_IsDetachedNotDeleted_AndAnIncrementalOrphanIsKept()
    {
        var host = new ProviderHost();
        Check.Succeeded(await host.CreateAsync(host.Resources(Group())));
        await host.Fake.DeleteAsync(new EArm.Az.GraphRequest("/groups/" + host.IdOf("/groups", "uniqueName", "test-admins")), ProviderHost.Ct);

        var complete = await host.WhatIfAsync(host.Resources(string.Empty), DeploymentMode.Complete);
        var incremental = await host.WhatIfAsync(host.Resources(string.Empty), DeploymentMode.Incremental);

        var gone = Assert.Single(complete.Plan.Changes);
        Assert.Equal(ChangeType.Ignore, gone.Type);
        Assert.Contains("no longer in the tenant", string.Join(";", gone.Reasons), StringComparison.Ordinal);
        Assert.Contains(incremental.Plan.Diagnostics, d => d.Code == EngineDiagnosticCodes.ResourceDetached);
        Check.NoWrites(complete);
    }

    [Fact]
    public async Task AnExistingResourceLookup_ThatFails_LeavesTheReferenceUnresolved_WithAnExplanation()
    {
        var host = new ProviderHost();
        host.Directory.AddGroup("elsewhere", "Elsewhere");
        host.Fake.Failures.Forbid("/groups(uniqueName", "GET");
        string consumer = Group().Replace("\"displayName\": \"Test Admins\"", "\"displayName\": \"[reference(resourceId('Microsoft.Entra/groups', 'elsewhere'), '2026-10-01-preview').displayName]\"", StringComparison.Ordinal);

        var whatIf = await host.WhatIfAsync(host.Resources(consumer));

        Assert.NotEmpty(whatIf.Plan.UnresolvedReferences);
        Assert.Contains(whatIf.Plan.UnresolvedReferences, u => u.Reason.Contains("403", StringComparison.Ordinal) || u.Reason.Contains("denied", StringComparison.OrdinalIgnoreCase) || u.Reason.Contains("Insufficient", StringComparison.Ordinal));
        Check.NoWrites(whatIf);
    }

    [Fact]
    public async Task ASuccessfulExistingLookup_ResolvesTheReference_WithoutCreatingAnything()
    {
        var host = new ProviderHost();
        host.Directory.AddGroup("elsewhere", "Elsewhere");
        string consumer = Group().Replace("\"displayName\": \"Test Admins\"", "\"displayName\": \"[reference(resourceId('Microsoft.Entra/groups', 'elsewhere'), '2026-10-01-preview').displayName]\"", StringComparison.Ordinal);

        var run = await host.CreateAsync(host.Resources(consumer));

        Check.Succeeded(run);
        Assert.Equal("Elsewhere", host.Directory.Find("/groups", "uniqueName", "test-admins")!["displayName"]!.GetValue<string>());
        Assert.Single(host.Directory.Objects("/groups"), g => g["uniqueName"]!.GetValue<string>() == "elsewhere");
        Assert.Empty(run.Plan.UnresolvedReferences);
    }
}
