using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EArm.Cli;
using EArm.Cli.Commands;
using EArm.Engine.Planning;
using EArm.TestSupport;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// How many times each verb reads the key of a <c>displayName</c>-keyed object that is not in the tenant (<see cref="KeyAbsenceCheck"/>):
/// <c>what-if</c> and the planning pass of <c>create</c> plan on the first read (<see cref="PlannerOptions.ConfirmKeyAbsence"/> off); the executor of
/// <c>create</c> still confirms the absence with every extra read right before its POST; <c>delete</c> keeps the confirmation in its planner, because it
/// deletes what that plan finds.
/// </summary>
public sealed class KeyAbsenceRereadCommandTests
{
    private const string LocType = "Microsoft.Entra/conditionalAccess/namedLocations";
    private const string LocPath = "/identity/conditionalAccess/namedLocations";
    private static readonly int Extra = KeyAbsenceCheck.Default.ExtraReads;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (CliSandbox Sandbox, string Template) Setup(string test)
    {
        var sandbox = new CliSandbox(test);
        sandbox.Handler.Directory = new FakeDirectory();
        sandbox.Tokens.TenantId = CliSandbox.Tenant;
        string template = sandbox.WriteTemplate("loc.entra.json", "\"resources\": [ { \"type\": \"" + LocType + "\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"Contoso offices\", \"properties\": { \"@odata.type\": \"#microsoft.graph.ipNamedLocation\", \"ipRanges\": [ { \"cidrAddress\": \"10.0.0.0/8\" } ] } } ]");
        return (sandbox, template);
    }

    private static DeploymentInvocation Invocation(CliSandbox sandbox, string template)
    {
        return new DeploymentInvocation { TemplateFile = template, Output = OutputFormat.Table, Connection = sandbox.Connection };
    }

    /// <summary>The key reads (list reads) of the named locations, up to the first write.</summary>
    private static int KeyReadsBeforeFirstWrite(CliSandbox sandbox)
    {
        return sandbox.RecordedCommands()
            .TakeWhile(c => c.StartsWith("GET ", StringComparison.Ordinal))
            .Count(c => c.Contains(LocPath + "?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhatIf_ANewObject_IsReadOnce()
    {
        var (sandbox, template) = Setup(nameof(WhatIf_ANewObject_IsReadOnce));
        using var _ = sandbox;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exit = await DeploymentRunner.WhatIfAsync(Invocation(sandbox, template), stdout, stderr, Ct);

        Assert.True(exit == ExitCodes.Ok, stdout + stderr.ToString());
        Assert.Equal(1, KeyReadsBeforeFirstWrite(sandbox));
    }

    [Fact]
    public async Task Create_WithoutYes_PlansOnOneRead_ThenTheExecutorConfirmsTheAbsenceBeforeThePost()
    {
        var (sandbox, template) = Setup(nameof(Create_WithoutYes_PlansOnOneRead_ThenTheExecutorConfirmsTheAbsenceBeforeThePost));
        using var _ = sandbox;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exit = await DeploymentRunner.CreateAsync(Invocation(sandbox, template), stdout, stderr, () => throw new InvalidOperationException("no prompt expected"), Ct);

        Assert.True(exit == ExitCodes.Ok, stdout + stderr.ToString());
        Assert.Single(sandbox.RecordedCommands(), c => c.StartsWith("POST ", StringComparison.Ordinal) && c.Contains(LocPath, StringComparison.Ordinal));
        Assert.Equal(1 + 1 + Extra, KeyReadsBeforeFirstWrite(sandbox));
    }

    [Fact]
    public async Task Delete_AnObjectNotInTheTenant_IsConfirmedAbsentWithEveryExtraRead()
    {
        var (sandbox, template) = Setup(nameof(Delete_AnObjectNotInTheTenant_IsConfirmedAbsentWithEveryExtraRead));
        using var _ = sandbox;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exit = await DeleteCommand.RunAsync(Invocation(sandbox, template), stdout, stderr, () => throw new InvalidOperationException("no prompt expected"), Ct);

        Assert.True(exit == ExitCodes.Ok, stdout + stderr.ToString());
        Assert.Contains("Nothing to delete", stdout.ToString(), StringComparison.Ordinal);
        Assert.Equal(1 + Extra, KeyReadsBeforeFirstWrite(sandbox));
    }
}
