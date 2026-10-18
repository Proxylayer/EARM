using System;
using System.Threading.Tasks;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Providers.Tests.Infrastructure;
using Xunit;
using static EArm.Providers.Tests.Infrastructure.DirectoryTemplates;

namespace EArm.Providers.Tests;

/// <summary>
/// Edge-pass planning errors and edge cases beyond the T1–T9 lifecycle: declared targets that do not exist, a whole array that is only
/// known at deployment time, an object that cannot be read back, and rows that fail without issuing a single edge write.
/// </summary>
public sealed class EdgePlannerErrorTests
{
    private static string AliceWith(string edges)
    {
        return Alice(edges);
    }

    private static async Task<Run> Create(ProviderHost host, string resources)
    {
        return await host.CreateAsync(host.Resources(resources));
    }

    [Fact]
    public async Task AManagerThatDoesNotExist_FailsTheDeclarer_WithoutAnEdgeWrite()
    {
        var host = new ProviderHost();
        string ghost = Guid.NewGuid().ToString("D");

        var run = await Create(host, AliceWith("\"manager\": \"" + ghost + "\""));

        var op = run.OpOfType("Microsoft.Entra/users");
        Assert.False(run.Succeeded);
        Assert.Equal(EngineDiagnosticCodes.EdgeValidationFailed, op.ErrorCode);
        Assert.Contains("does not exist or is not a user", op.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(run.Writes, w => w.Path.Contains("manager", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AGroupThatDoesNotExist_InMemberOf_FailsTheDeclarer_AndNamesTheId()
    {
        var host = new ProviderHost();
        string ghost = Guid.NewGuid().ToString("D");

        var run = await Create(host, AliceWith("\"memberOf\": [\"" + ghost + "\"]"));

        var op = run.OpOfType("Microsoft.Entra/users");
        Assert.False(run.Succeeded);
        Assert.Equal(EngineDiagnosticCodes.EdgeValidationFailed, op.ErrorCode);
        Assert.Contains(ghost, op.Error, StringComparison.Ordinal);
        Assert.Contains("does not exist or is not a group", op.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(run.Writes, w => w.Path.Contains("$ref", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AUserIdWhereAGroupIsExpected_IsNotAGroup()
    {
        var host = new ProviderHost();
        string bobId = host.Directory.AddUser("bob@contoso.com", "Bob")["id"]!.GetValue<string>();

        var run = await Create(host, AliceWith("\"memberOf\": [\"" + bobId + "\"]"));

        Assert.False(run.Succeeded);
        Assert.Contains("does not exist or is not a group", run.OpOfType("Microsoft.Entra/users").Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhatIf_OfAnEdgeToAnObjectThatDoesNotExist_FlagsTheRowOfAnExistingDeclarer_AndWritesNothing()
    {
        var host = new ProviderHost();
        string ghost = Guid.NewGuid().ToString("D");
        Check.Succeeded(await Create(host, Alice()));
        host.ClearRequests();

        var whatIf = await host.WhatIfAsync(host.Resources(AliceWith("\"memberOf\": [\"" + ghost + "\"]")));

        var row = whatIf.ChangeOfType("Microsoft.Entra/users");
        Assert.True(row.HasError, "an existing declarer's edge targets are validated in what-if");
        Check.NoWrites(whatIf);
    }

    /// <summary>
    /// Documents a design limit rather than a defect: for a declarer that does not exist yet the planner has no principal to read, so every
    /// declared edge is planned as an add without checking its target (EdgePlanner.PlanPrincipalSideAsync: "the principal does not exist yet").
    /// What-if therefore shows a clean create that the real run then rejects with EdgeValidationFailed.
    /// </summary>
    [Fact]
    public async Task WhatIf_OfANewDeclarer_DoesNotValidateItsEdgeTargets_TheCreateDoes()
    {
        var host = new ProviderHost();
        string ghost = Guid.NewGuid().ToString("D");
        var document = host.Resources(AliceWith("\"memberOf\": [\"" + ghost + "\"]"));

        var whatIf = await host.WhatIfAsync(document);
        var run = await host.CreateAsync(document);

        Assert.False(whatIf.ChangeOfType("Microsoft.Entra/users").HasError);
        Assert.False(run.Succeeded);
        Assert.Equal(EngineDiagnosticCodes.EdgeValidationFailed, run.OpOfType("Microsoft.Entra/users").ErrorCode);
    }

    [Fact]
    public async Task AWholeEdgeArray_OnlyKnownAtDeployment_IsAnUnknownRowInWhatIf_AndResolvesOnCreate()
    {
        var host = new ProviderHost();
        host.Directory.AddGroup("existing", "Existing");
        string template = Join(
            "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"base\", \"properties\": { \"displayName\": \"Base\" } }",
            Alice("\"memberOf\": \"[createArray(reference('base').id)]\""));

        var whatIf = await host.WhatIfAsync(host.Resources(template));
        var run = await host.CreateAsync(host.Resources(template));

        Assert.All(whatIf.Plan.Changes, c => Assert.False(c.HasError, c.UnsupportedReason));
        Check.NoWrites(whatIf);
        Check.Succeeded(run);
        string baseId = host.IdOf("/groups", "uniqueName", "base");
        Assert.Contains(run.Writes, w => w.Path == "/groups/" + baseId + "/members/$ref");
    }

    [Fact]
    public async Task ARerunWithNothingToDo_PerformsNoEdgeWrites_EvenWithSeveralDeclarationsOfTheSameEdge()
    {
        var host = new ProviderHost();
        string template = Join(
            Group(),
            Alice("\"memberOf\": [\"[reference('test-admins').id]\", \"[reference('test-admins').id]\"]"));

        var first = await Create(host, template);
        Check.Succeeded(first);
        Assert.Single(first.Writes, w => w.Path.EndsWith("/members/$ref", StringComparison.Ordinal));

        var rerun = await Create(host, template);

        Check.Succeeded(rerun);
        Check.NoWrites(rerun);
    }

    [Fact]
    public async Task ADeclarationThatNamesTheSameEdgeFromBothSides_IsOneWrite()
    {
        var host = new ProviderHost();
        string template = Join(
            Group("\"members\": [\"[reference('alice@contoso.com').id]\"]"),
            Alice("\"memberOf\": [\"[reference('test-admins').id]\"]"));

        var run = await Create(host, template);

        Check.Succeeded(run);
        Assert.Single(run.Writes, w => w.Path.EndsWith("/members/$ref", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnEdgeThatFailsToWrite_FailsOnlyItsDeclarer_AndLaterRunsRetryIt()
    {
        var host = new ProviderHost();
        string template = Join(Group(), Alice("\"memberOf\": [\"[reference('test-admins').id]\"]"));
        host.Fake.Failures.Forbid("/members/$ref", "POST", 1);

        var failed = await Create(host, template);

        Assert.False(failed.Succeeded);
        Assert.Equal(OperationStatus.Failed, failed.OpOfType("Microsoft.Entra/users").Status);
        Assert.Equal(OperationStatus.Succeeded, failed.OpOfType("Microsoft.Entra/groups").Status);
        Assert.Empty(host.Directory.References("/groups/" + host.IdOf("/groups", "uniqueName", "test-admins") + "/members"));

        var retried = await Create(host, template);

        Check.Succeeded(retried);
        Assert.Single(host.Directory.References("/groups/" + host.IdOf("/groups", "uniqueName", "test-admins") + "/members"));
    }
}
