using System;
using System.Linq;
using System.Threading.Tasks;
using EArm.Core.Types;
using EArm.Engine.Permissions;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// The offline expansion behind <c>earm permissions</c> and the run scopes evaluates <c>deployment().name</c> as the run's deployment name (the template
/// file stem without <c>-n</c>), not <c>DeploymentInfo.Offline</c>'s <c>validate</c>, so resource ids built from it match what a deployment uses.
/// </summary>
[Collection(CliCollection.Name)]
public sealed class PermissionsDeploymentNameTests
{
    private const string Body = "\"resources\": [ { \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"[format('{0}-grp', deployment().name)]\", "
        + "\"properties\": { \"displayName\": \"G\", \"mailEnabled\": false, \"mailNickname\": \"g\", \"securityEnabled\": true } } ]";

    [Fact]
    public async Task EarmPermissions_NamesResourcesWithTheTemplateStem()
    {
        using var sandbox = new CliSandbox(nameof(EarmPermissions_NamesResourcesWithTheTemplateStem));
        string template = sandbox.WriteTemplate("orders.entra.json", Body);

        var result = await CliRunner.RunAsync("permissions", "-f", template, "-o", "json");

        Assert.Equal(0, result.ExitCode);
        var resources = result.Json["permissions"]!.AsArray().SelectMany(p => p!["resources"]!.AsArray().Select(r => r!.GetValue<string>())).ToList();
        Assert.Contains("/providers/Microsoft.Entra/groups/orders-grp", resources);
        Assert.DoesNotContain(resources, r => r.Contains("validate-grp", StringComparison.Ordinal));
    }

    [Fact]
    public void TheExpansion_UsesTheGivenDeploymentName_AndValidateWithoutOne()
    {
        var document = EArm.Tests.Expr.ParseBody(Body);

        var named = OfflineTemplateExpansion.Run(document, ResourceTypeCatalog.Default, deploymentName: "prod");
        var unnamed = OfflineTemplateExpansion.Run(document, ResourceTypeCatalog.Default);

        Assert.Equal("/providers/Microsoft.Entra/groups/prod-grp", Assert.Single(named.Instances).PlannedId.Id);
        Assert.Equal("/providers/Microsoft.Entra/groups/validate-grp", Assert.Single(unnamed.Instances).PlannedId.Id);
    }

    [Theory]
    [InlineData("orders-api.entra.json", "orders-api")]
    [InlineData("c:/t/orders-api.entra.jsonc", "orders-api")]
    [InlineData("plain.json", "plain")]
    public void DefaultName_IsTheFileStemWithoutEntra(string file, string expected)
    {
        Assert.Equal(expected, EArm.Cli.Commands.DeploymentInvocation.DefaultName(file));
    }
}
