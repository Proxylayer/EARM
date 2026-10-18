using System.Threading.Tasks;
using EArm.Engine.Execution;
using EArm.Engine.Providers;
using EArm.Providers.Tests.Infrastructure;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// <c>reference(id, apiVersion)</c> (an existing-resource lookup, the form a variable may use) of a resource the same run creates: the what-if shows
/// the reader as a "known at deployment" row (<c>?</c>) instead of an error, and create, with or without the what-if pass first, reads the object once
/// it exists.
/// </summary>
public sealed class ExistingLookupOfCreatedResourceTests
{
    private const string Source = "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"source\", \"properties\": { \"displayName\": \"source\" } }";

    private const string Variables = "\"variables\": { "
        + "\"sourceRef\": \"[reference(resourceId('Microsoft.Entra/groups', 'source'), '2026-10-01-preview')]\", "
        + "\"sourceId\": \"[reference(resourceId('Microsoft.Entra/groups', 'source'), '2026-10-01-preview').id]\", "
        + "\"reader\": { \"displayName\": \"reader\", \"description\": \"[variables('sourceRef').id]\" } }, ";

    public static TheoryData<string> Readers => new()
    {
        "{ \"displayName\": \"reader\", \"description\": \"[variables('sourceRef').id]\" }",
        "{ \"displayName\": \"reader\", \"description\": \"[variables('sourceId')]\" }",
        "\"[variables('reader')]\"",
        "{ \"displayName\": \"reader\", \"description\": \"[reference(resourceId('Microsoft.Entra/groups', 'source'), '2026-10-01-preview').id]\" }",
    };

    [Theory]
    [MemberData(nameof(Readers))]
    public async Task WhatIf_shows_a_known_at_deployment_row_instead_of_an_error(string properties)
    {
        var host = new ProviderHost();

        var whatIf = await host.WhatIfAsync(host.Parse(Template(properties)));

        var row = Assert.Single(whatIf.Plan.Changes, c => c.Id.Name == "reader");
        Assert.False(row.HasError, string.Join(" | ", row.Reasons));
        Assert.True(row.IsUnknown);
        Assert.Equal(ChangeType.Create, row.Type);
        Assert.Equal(ChangeType.Create, Assert.Single(whatIf.Plan.Changes, c => c.Id.Name == "source").Type);
    }

    [Theory]
    [MemberData(nameof(Readers))]
    public async Task Create_after_the_what_if_pass_reads_the_object_once_it_exists(string properties)
    {
        var host = new ProviderHost();

        // create without -y: the plan of the what-if pass (and its variable store) is the one executed.
        var whatIf = await host.WhatIfAsync(host.Parse(Template(properties)));
        var result = await host.Executor().ExecuteAsync(whatIf.Plan, new ExecuteOptions { Parallelism = 1 }, ProviderHost.Ct);

        Check.Succeeded(new Run(whatIf.Plan, result, []));
        AssertReaderDescribesSource(host);
    }

    [Theory]
    [MemberData(nameof(Readers))]
    public async Task Create_reads_the_object_once_it_exists(string properties)
    {
        var host = new ProviderHost();

        var run = await host.CreateAsync(host.Parse(Template(properties)));

        Check.Succeeded(run);
        AssertReaderDescribesSource(host);
    }

    [Fact]
    public async Task A_lookup_of_a_resource_that_is_not_in_the_template_and_does_not_exist_still_fails()
    {
        var host = new ProviderHost();
        string reader = "{ \"displayName\": \"reader\", \"description\": \"[reference(resourceId('Microsoft.Entra/groups', 'missing'), '2026-10-01-preview').id]\" }";

        var whatIf = await host.WhatIfAsync(host.Parse(Template(reader)));

        var row = Assert.Single(whatIf.Plan.Changes, c => c.Id.Name == "reader");
        Assert.True(row.HasError);
        Assert.Contains(row.Reasons, r => r.Contains("was not found", System.StringComparison.Ordinal));
    }

    private static string Template(string readerProperties)
    {
        return "{ \"$schema\": \"" + Templates.Schema + "\", \"contentVersion\": \"1.0.0.0\", " + Variables
            + "\"resources\": [ " + Source + ", { \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"reader\", \"properties\": "
            + readerProperties + ", \"dependsOn\": [ \"[resourceId('Microsoft.Entra/groups', 'source')]\" ] } ] }";
    }

    private static void AssertReaderDescribesSource(ProviderHost host)
    {
        var source = host.Directory.Find("groups", "displayName", "source");
        var reader = host.Directory.Find("groups", "displayName", "reader");
        Assert.NotNull(source);
        Assert.NotNull(reader);
        Assert.Equal(source!["id"]!.GetValue<string>(), reader!["description"]!.GetValue<string>());
    }
}
