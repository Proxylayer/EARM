using System;
using System.Linq;
using System.Threading.Tasks;
using EArm.Engine.State;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary><c>earm deployment list -n/--name</c> keeps only the deployment of that name (case-insensitive exact match); an unknown name is an empty list, exit 0 and a message.</summary>
[Collection(CliCollection.Name)]
public sealed class Review2G_DeploymentListNameFilterTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);

    private static StateFile Sample(string name)
    {
        var state = StateFile.CreateNew(name, CliSandbox.Tenant, Start);
        state.CompleteRun(true, "Incremental", "sha256:template", Start.AddMinutes(1));
        return state;
    }

    private static async Task<CliSandbox> SandboxAsync(string test)
    {
        var sandbox = new CliSandbox(test);
        await sandbox.WriteStateAsync(Sample("orders-dev"));
        await sandbox.WriteStateAsync(Sample("orders-dev-2"));
        await sandbox.WriteStateAsync(Sample("billing"));
        return sandbox;
    }

    [Theory]
    [InlineData("-n")]
    [InlineData("--name")]
    public async Task ListWithAName_ShowsOnlyThatDeployment(string option)
    {
        using var sandbox = await SandboxAsync(nameof(ListWithAName_ShowsOnlyThatDeployment) + option.Length);

        var json = await CliRunner.RunAsync("deployment", "list", option, "orders-dev", "--state-dir", sandbox.StateDir, "-o", "json");

        Assert.Equal(0, json.ExitCode);
        var deployments = json.Json["deployments"]!.AsArray();
        Assert.Equal(["orders-dev"], deployments.Select(d => d!["name"]!.GetValue<string>()).ToArray());
        Assert.Equal(string.Empty, json.StandardError);
    }

    [Fact]
    public async Task TheNameMatch_IsCaseInsensitive_AndExact()
    {
        using var sandbox = await SandboxAsync(nameof(TheNameMatch_IsCaseInsensitive_AndExact));

        var upper = await CliRunner.RunAsync("deployment", "list", "-n", "ORDERS-DEV", "--state-dir", sandbox.StateDir, "-o", "json");
        var prefix = await CliRunner.RunAsync("deployment", "list", "-n", "orders", "--state-dir", sandbox.StateDir, "-o", "json");

        Assert.Equal(["orders-dev"], upper.Json["deployments"]!.AsArray().Select(d => d!["name"]!.GetValue<string>()).ToArray());
        Assert.Empty(prefix.Json["deployments"]!.AsArray());
    }

    [Fact]
    public async Task AnUnknownName_ListsNothing_PrintsAMessage_AndExitsZero()
    {
        using var sandbox = await SandboxAsync(nameof(AnUnknownName_ListsNothing_PrintsAMessage_AndExitsZero));

        var table = await CliRunner.RunAsync("deployment", "list", "-n", "nope", "--state-dir", sandbox.StateDir);
        var json = await CliRunner.RunAsync("deployment", "list", "-n", "nope", "--state-dir", sandbox.StateDir, "-o", "json");

        Assert.Equal(0, table.ExitCode);
        Assert.Contains("No deployments.", table.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("No deployment named 'nope'", table.StandardError, StringComparison.Ordinal);
        Assert.Equal(0, json.ExitCode);
        Assert.Empty(json.Json["deployments"]!.AsArray());
        Assert.Contains("No deployment named 'nope'", json.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutAName_EveryDeploymentIsListed()
    {
        using var sandbox = await SandboxAsync(nameof(WithoutAName_EveryDeploymentIsListed));

        var json = await CliRunner.RunAsync("deployment", "list", "--state-dir", sandbox.StateDir, "-o", "json");

        Assert.Equal(3, json.Json["deployments"]!.AsArray().Count);
        Assert.Equal(string.Empty, json.StandardError);
    }
}
