using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace EArm.Tests.Engine;

/// <summary>Review round 2, finding S2-15: a copy loop whose resource names come from ANOTHER loop's iterations (<c>reference(format('capp-{0}', copyIndex())).appId</c>).</summary>
public sealed class Review3H_ExpansionTests
{
    private const string Api = "2026-10-01-preview";

    private static string AppLoop()
    {
        return "{ \"type\": \"Microsoft.Entra/applications\", \"apiVersion\": \"" + Api + "\", \"name\": \"[format('capp-{0}', copyIndex())]\", "
            + "\"copy\": { \"name\": \"apps\", \"count\": 2 }, \"properties\": { \"displayName\": \"[format('App {0}', copyIndex())]\" } }";
    }

    private static string SpLoop()
    {
        return "{ \"type\": \"Microsoft.Entra/servicePrincipals\", \"apiVersion\": \"" + Api + "\", \"name\": \"[reference(format('capp-{0}', copyIndex())).appId]\", "
            + "\"copy\": { \"name\": \"sps\", \"count\": 2 }, \"properties\": { } }";
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ACopyLoopOfServicePrincipals_NamedByTheAppsOfAnotherLoop_IsExpandedAndDeployed_InAnyDeclarationOrder(bool appsFirst)
    {
        var host = new EngineHost();
        var document = Templates.Resources(appsFirst ? AppLoop() + ", " + SpLoop() : SpLoop() + ", " + AppLoop());

        var (plan, result) = await host.CreateAsync(document);

        Assert.True(result.Succeeded, string.Join("\n", result.Operations.Where(o => o.Error is not null).Select(o => o.Error)) + string.Join("\n", result.Diagnostics));
        Assert.Equal(4, plan.Instances.Count);
        Assert.Equal(2, host.Directory.Count("/applications"));
        Assert.Equal(2, host.Directory.Count("/servicePrincipals"));
        foreach (var app in host.Directory.Objects("/applications"))
        {
            Assert.NotNull(host.Directory.Find("/servicePrincipals", "appId", app["appId"]!.GetValue<string>()));
        }
    }

    [Fact]
    public async Task TheWhatIf_OfThatTemplate_PlansBothLoops_WithDeployTimeNamesForTheServicePrincipals()
    {
        var host = new EngineHost();
        var document = Templates.Resources(AppLoop() + ", " + SpLoop());

        var plan = await host.WhatIfAsync(document);

        Assert.Equal(4, plan.Changes.Count);
        Assert.Equal(2, plan.Changes.Count(c => c.ResourceType == "Microsoft.Entra/servicePrincipals"));
        Assert.DoesNotContain(plan.Changes, c => c.HasError);
    }
}
