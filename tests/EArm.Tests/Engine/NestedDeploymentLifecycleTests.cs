using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Core.Types;
using EArm.Engine.Execution;
using EArm.Engine.Permissions;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Engine.WhatIf;
using EArm.Providers;
using Xunit;
using static EArm.Tests.Engine.NestedDeploymentTests;

namespace EArm.Tests.Engine;

/// <summary>Nested deployments in what-if (rows and outputs), delete, the orphan report and the permissions manifest.</summary>
public sealed class NestedDeploymentLifecycleTests
{
    private static readonly string NestedGroupTemplate = Templates.Parse($$"""
        "resources": [
          {{Deployment("nested", Inline($"\"resources\": [ {Group("inner-g")} ], \"outputs\": {{ \"id\": {{ \"type\": \"string\", \"value\": \"[reference('inner-g').id]\" }} }}", "inner"))}},
          {{Group("reader", "[reference('nested').outputs.id.value]")}}
        ]
        """).Root.ToJsonString();

    private static string Text(WhatIfReport report)
    {
        using var writer = new StringWriter();
        WhatIfText.Render(report, writer);
        return writer.ToString();
    }

    [Fact]
    public async Task WhatIf_lists_the_nested_resources_with_their_deployment_and_no_row_for_the_deployment()
    {
        var plan = await new EngineHost().WhatIfAsync(new Core.Loading.TemplateLoader().Parse(NestedGroupTemplate, "inline.entra.json", Core.ParameterSet.Empty));
        var report = WhatIfReport.From(plan);

        Assert.DoesNotContain(report.Rows, r => DeploymentResourceType.Is(r.ResourceType));
        var inner = Assert.Single(report.Rows, r => r.ResourceId == "/providers/Microsoft.Entra/groups/inner-g");
        Assert.Equal("nested", inner.DeploymentPath);
        Assert.Equal(ChangeType.Create, inner.Type);
        Assert.Contains("/providers/Microsoft.Entra/groups/inner-g [Microsoft.Entra/groups] (nested deployment: nested)", Text(report), StringComparison.Ordinal);

        var changes = WhatIfJson.Build(report)["changes"]!.AsArray().Select(c => c!.AsObject()).ToList();
        Assert.Equal("nested", changes.Single(c => c["resourceId"]!.GetValue<string>() == inner.ResourceId)["deployment"]!.GetValue<string>());
        Assert.Null(changes.Single(c => c["resourceId"]!.GetValue<string>() == "/providers/Microsoft.Entra/groups/reader")["deployment"]);
    }

    [Fact]
    public async Task WhatIf_an_output_that_depends_on_a_resource_still_to_create_is_deploy_time_in_the_reader()
    {
        var plan = await new EngineHost().WhatIfAsync(new Core.Loading.TemplateLoader().Parse(NestedGroupTemplate, "inline.entra.json", Core.ParameterSet.Empty));
        var report = WhatIfReport.From(plan);

        var reader = Assert.Single(report.Rows, r => r.ResourceId == "/providers/Microsoft.Entra/groups/reader");
        Assert.True(reader.IsUnknown);
        Assert.False(reader.IsUnsupported);
    }

    [Fact]
    public async Task WhatIf_an_output_of_an_existing_resource_flows_into_the_reader()
    {
        var host = new EngineHost();
        var existing = host.Directory.AddGroup("inner-g", "inner-g", g => { g["mailEnabled"] = false; g["securityEnabled"] = true; });

        var plan = await host.WhatIfAsync(new Core.Loading.TemplateLoader().Parse(NestedGroupTemplate, "inline.entra.json", Core.ParameterSet.Empty));
        var reader = EngineHost.Evaluated(plan, "/providers/Microsoft.Entra/groups/reader");

        Assert.Equal(existing["id"]!.GetValue<string>(), reader.Properties!["description"]!.GetValue<string>());
    }

    [Fact]
    public async Task Delete_lists_the_objects_of_nested_templates()
    {
        var host = new EngineHost();
        host.Directory.AddGroup("inner-g", "inner-g", g => { g["mailEnabled"] = false; g["securityEnabled"] = true; });

        var plan = await host.WhatIfAsync(new Core.Loading.TemplateLoader().Parse(NestedGroupTemplate, "inline.entra.json", Core.ParameterSet.Empty));
        var declared = TemplateDeleter.FindDeclaredObjects(plan);

        var inner = Assert.Single(declared);
        Assert.Equal("/providers/Microsoft.Entra/groups/inner-g", inner.Instance.Id.Id);
        Assert.Equal("nested", inner.Instance.DeploymentPath);
    }

    [Fact]
    public async Task The_complete_mode_orphan_report_counts_the_nested_declarations_as_declared()
    {
        var host = new EngineHost(registry: ProviderDiscovery.CreateRegistry(ResourceTypeCatalog.Default));
        foreach (var (id, name) in new[] { ("22222222-0000-0000-0000-000000000051", "Declared offices"), ("22222222-0000-0000-0000-000000000052", "Legacy offices") })
        {
            host.Directory.Add("/identity/conditionalAccess/namedLocations", new JsonObject
            {
                ["@odata.type"] = "#microsoft.graph.ipNamedLocation",
                ["id"] = id,
                ["displayName"] = name,
                ["isTrusted"] = false,
                ["ipRanges"] = new JsonArray(new JsonObject { ["@odata.type"] = "#microsoft.graph.iPv4CidrRange", ["cidrAddress"] = "10.0.0.0/8" }),
            });
        }

        string location = $$"""{ "type": "Microsoft.Entra/conditionalAccess/namedLocations", "apiVersion": "{{Api}}", "name": "Declared offices", "properties": { "@odata.type": "#microsoft.graph.ipNamedLocation", "isTrusted": false, "ipRanges": [ { "cidrAddress": "10.0.0.0/8" } ] } }""";
        var document = Templates.Parse($"\"resources\": [ {Deployment("nested", Inline($"\"resources\": [ {location} ]"))} ]");

        var plan = await host.WhatIfAsync(document, DeploymentMode.Complete);

        var orphan = Assert.Single(plan.Orphans);
        Assert.Equal("/providers/Microsoft.Entra/conditionalAccess/namedLocations/Legacy offices", orphan.Id.Id);
    }

    [Fact]
    public void Permissions_include_the_resources_of_nested_templates()
    {
        var document = Templates.Parse($"\"resources\": [ {Deployment("nested", Inline($"\"resources\": [ {Group("inner-g")} ]"))} ]");

        var expansion = OfflineTemplateExpansion.Run(document, ResourceTypeCatalog.Default);
        var report = PermissionManifest.Build("t.entra.json", PermissionMode.App, expansion, ProviderDiscovery.CreateRegistry(ResourceTypeCatalog.Default));

        Assert.True(expansion.Succeeded);
        Assert.Equal(1, report.ResourceCount);
        Assert.Contains(report.Permissions, p => p.Name.StartsWith("Group.", StringComparison.Ordinal));
    }
}
