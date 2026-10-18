using System.Threading.Tasks;
using EArm.Engine.Providers;
using EArm.Providers.Tests.Infrastructure;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// A whole <c>properties</c> expression whose value depends on <c>reference()</c> of a resource the same run creates: its keys are not known
/// before that resource exists, so the what-if shows a "known at deployment" row (<c>Ignore</c>, <c>?</c>) instead of failing, and
/// <c>create</c> evaluates the expression once the referenced resource exists.
/// </summary>
public sealed class WholePropertiesDeployTimeTests
{
    private const string Reader = "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"reader\", "
        + "\"properties\": \"[union(createObject('displayName', 'reader'), createObject('description', reference(resourceId('Microsoft.Entra/groups', 'source')).id))]\", "
        + "\"dependsOn\": [ \"[resourceId('Microsoft.Entra/groups', 'source')]\" ] }";

    private const string Source = "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"source\", \"properties\": { \"displayName\": \"source\" } }";

    [Fact]
    public async Task WhatIf_shows_a_known_at_deployment_row_instead_of_an_error()
    {
        var host = new ProviderHost();

        var whatIf = await host.WhatIfAsync(host.Resources(Source + ", " + Reader));

        var row = Assert.Single(whatIf.Plan.Changes, c => c.Id.Name == "reader");
        Assert.Equal(ChangeType.Ignore, row.Type);
        Assert.True(row.IsUnknown);
        Assert.False(row.HasError);
        Assert.Contains(row.Reasons, r => r.Contains("only known at deployment", System.StringComparison.Ordinal));
        Assert.Equal(ChangeType.Create, Assert.Single(whatIf.Plan.Changes, c => c.Id.Name == "source").Type);
    }

    [Fact]
    public async Task Create_evaluates_the_expression_once_the_referenced_resource_exists()
    {
        var host = new ProviderHost();

        var run = await host.CreateAsync(host.Resources(Source + ", " + Reader));

        Check.Succeeded(run);
        var source = host.Directory.Find("groups", "displayName", "source");
        var reader = host.Directory.Find("groups", "displayName", "reader");
        Assert.NotNull(source);
        Assert.NotNull(reader);
        Assert.Equal(source!["id"]!.GetValue<string>(), reader!["description"]!.GetValue<string>());
    }
}
