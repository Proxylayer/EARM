using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Engine.State;
using EArm.Tests.Engine;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// Review 2E: <c>deployment show</c> says for every entry whether the deployment created the object or adopted it, because Complete mode only deletes
/// created objects and the user must be able to see which is which, in the table and in the JSON.
/// </summary>
[Collection(CliCollection.Name)]
public sealed class Review2E_ShowProvenanceTests
{
    private const string Created = "/providers/Microsoft.Entra/applications/created-app";
    private const string Adopted = "/providers/Microsoft.Entra/applications/adopted-app";
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);

    private static StateEntry Entry(string name, StateProvenance provenance, string graphId)
    {
        return new StateEntry
        {
            Id = ResourceIdentifier.Create(Fixture.AppType, name),
            Type = Fixture.AppType,
            ApiVersion = "2026-10-01-preview",
            Key = new Dictionary<string, string> { ["uniqueName"] = name },
            GraphId = graphId,
            LastWriteOn = Start,
            Provenance = provenance,
        };
    }

    private static StateFile Sample()
    {
        var state = StateFile.CreateNew("prov-dep", CliSandbox.Tenant, Start);
        state.Upsert(Entry("created-app", StateProvenance.Created, "a1000000-0000-4000-8000-000000000001"));
        state.Upsert(Entry("adopted-app", StateProvenance.Adopted, "a2000000-0000-4000-8000-000000000002"));
        state.CompleteRun(false, "Incremental", "sha256:template", Start.AddMinutes(1));
        return state;
    }

    [Fact]
    public async Task Table_ShowsAProvenanceColumn_WithCreatedAndAdoptedPerEntry()
    {
        using var sandbox = new CliSandbox(nameof(Table_ShowsAProvenanceColumn_WithCreatedAndAdoptedPerEntry));
        await sandbox.WriteStateAsync(Sample());

        var table = await CliRunner.RunAsync("deployment", "show", "-n", "prov-dep", "--state-dir", sandbox.StateDir);

        Assert.Equal(0, table.ExitCode);
        var lines = table.StandardOutput.Split('\n').Select(l => l.TrimEnd()).ToList();
        Assert.Contains(lines, l => l.StartsWith("Resource", StringComparison.Ordinal) && l.Contains("Provenance", StringComparison.Ordinal));
        var created = Assert.Single(lines, l => l.StartsWith(Created, StringComparison.Ordinal));
        var adopted = Assert.Single(lines, l => l.StartsWith(Adopted, StringComparison.Ordinal));
        Assert.Matches(@"a1000000-0000-4000-8000-000000000001\s+created\s", created);
        Assert.Matches(@"a2000000-0000-4000-8000-000000000002\s+adopted\s", adopted);
    }

    [Fact]
    public async Task Json_CarriesTheProvenanceOfEveryEntry()
    {
        using var sandbox = new CliSandbox(nameof(Json_CarriesTheProvenanceOfEveryEntry));
        await sandbox.WriteStateAsync(Sample());

        var json = await CliRunner.RunAsync("deployment", "show", "-n", "prov-dep", "--state-dir", sandbox.StateDir, "-o", "json");

        Assert.Equal(0, json.ExitCode);
        Assert.Equal("created", json.Json["resources"]![Created]!["provenance"]!.GetValue<string>());
        Assert.Equal("adopted", json.Json["resources"]![Adopted]!["provenance"]!.GetValue<string>());
    }
}
