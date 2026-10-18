using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Core.Loading;
using EArm.Core.Types;
using EArm.Engine.Planning;
using EArm.Engine.WhatIf;
using EArm.Providers;
using EArm.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EArm.Tests.Engine;

/// <summary>One manifest entry of <c>tests/fixtures/whatif/scenarios.json</c>.</summary>
internal sealed record WhatIfScenario(
    string Name,
    string DeploymentName,
    DeploymentMode Mode,
    ResultFormat ResultFormat,
    string TenantId,
    string Template,
    string Seed,
    IReadOnlyDictionary<string, string> Parameters,
    string ExpectedText,
    string ExpectedJson)
{
    public static string Directory => Path.Combine(TestPaths.RepoRoot, "tests", "fixtures", "whatif");

    public static IReadOnlyList<WhatIfScenario> All()
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(Directory, "scenarios.json")))!.AsArray();
        return manifest.Select(n => FromJson(n!.AsObject())).ToList();
    }

    public static WhatIfScenario Find(string name)
    {
        return All().Single(s => s.Name == name);
    }

    private static WhatIfScenario FromJson(JsonObject json)
    {
        string Text(string key) => json[key]!.GetValue<string>();
        var parameters = (json["parameters"] as JsonObject ?? []).ToDictionary(p => p.Key, p => p.Value!.GetValue<string>(), StringComparer.Ordinal);
        return new WhatIfScenario(
            Text("name"),
            Text("deploymentName"),
            Enum.Parse<DeploymentMode>(Text("mode")),
            Enum.Parse<ResultFormat>(Text("resultFormat")),
            Text("tenantId"),
            Text("template"),
            Text("seed"),
            parameters,
            Text("expectedText"),
            Text("expectedJson"));
    }

    public string PathOf(string file)
    {
        return Path.Combine(Directory, file);
    }

    /// <summary>The golden text, line endings normalised to LF.</summary>
    public string ReadExpectedText()
    {
        return File.ReadAllText(PathOf(ExpectedText)).Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    public string ReadExpectedJson()
    {
        return File.ReadAllText(PathOf(ExpectedJson)).Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>Plans the scenario exactly as tests/fixtures/whatif/README.md describes (real providers plus EchoProvider for the rest).</summary>
    public async Task<WhatIfRun> PlanAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var catalog = ResourceTypeCatalog.Default;
        var registry = ProviderDiscovery.CreateRegistry(catalog);
        foreach (var definition in catalog.Types.Where(d => !registry.Contains(d.Type)))
        {
            registry.Register(new EchoProvider(definition, catalog));
        }

        var client = new FakeGraphClient();
        foreach (var entry in JsonNode.Parse(File.ReadAllText(PathOf(Seed)))!.AsArray().Select(n => n!.AsObject()))
        {
            if (entry["collection"] is { } collection)
            {
                client.Directory.Add(collection.GetValue<string>(), entry["entity"]!.AsObject());
                continue;
            }

            string reference = entry["ref"]!.GetValue<string>();
            var body = new JsonObject { ["@odata.id"] = "https://graph.microsoft.com/v1.0/directoryObjects/" + entry["id"]!.GetValue<string>() };
            var request = new EArm.Graph.GraphRequest(reference);
            if (string.Equals(entry["method"]?.GetValue<string>(), "PUT", StringComparison.OrdinalIgnoreCase))
            {
                await client.PutAsync(request, body, ct);
            }
            else
            {
                await client.PostAsync(request, body, ct);
            }
        }

        client.ClearRequests();

        var options = new PlannerOptions
        {
            DeploymentName = DeploymentName,
            TenantId = TenantId,
            Catalog = catalog,
            EdgeKinds = ProviderDiscovery.CreateEdgeKinds(catalog),
            KeyAbsenceCheck = new KeyAbsenceCheck { Delay = new RecordingDelayProvider() },
        };
        var parameters = Parameters.Aggregate(ParameterSet.Empty, (set, p) => set.WithInline(p.Key, p.Value));
        var document = await new TemplateLoader(catalog).LoadAsync(PathOf(Template), parameters, ct);
        var planner = new DeploymentPlanner(registry, client, options, NullLogger<DeploymentPlanner>.Instance);
        var plan = await planner.PlanAsync(document, Mode, PlanMode.WhatIf, ct);
        return new WhatIfRun(this, client, plan, WhatIfReport.From(plan));
    }
}

internal sealed record WhatIfRun(WhatIfScenario Scenario, FakeGraphClient Client, DeploymentPlan Plan, WhatIfReport Report)
{
    /// <summary>The text as the golden files were written: LF, no colours.</summary>
    public string RenderText()
    {
        using var writer = new StringWriter { NewLine = "\n" };
        WhatIfText.Render(Report, writer, new WhatIfTextOptions(Scenario.ResultFormat));
        return writer.ToString();
    }

    public string RenderJson()
    {
        // System.Text.Json indents with Environment.NewLine; the golden files are LF (see tests/fixtures/whatif/README.md).
        return WhatIfJson.Serialize(WhatIfJson.Build(Report, Scenario.ResultFormat), prettyPrint: true).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    public WhatIfRow Row(string resourceId)
    {
        return Assert.Single(Report.Rows, r => r.ResourceId == resourceId);
    }
}
