using System;
using System.IO;
using System.Text.Json.Nodes;
using EArm.Graph.Auth;
using Xunit;

namespace EArm.Tests;

/// <summary>
/// <see cref="Review1C.RunChild"/> starts the CLI with a runtimeconfig that carries the test-only switch which keeps logout away from the real token cache,
/// so a child process is as guarded as the test host (tests/Directory.Build.props).
/// </summary>
public sealed class Review1C_ChildProcessGuardTests
{
    [Fact]
    public void TheGuardedRuntimeConfig_TurnsTheTokenCacheSwitchOn_AndKeepsTheCliFramework()
    {
        var guarded = JsonNode.Parse(File.ReadAllText(Review1C.GuardedRuntimeConfig.Value))!["runtimeOptions"]!;
        var original = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "earm.runtimeconfig.json")))!["runtimeOptions"]!;

        Assert.True(guarded["configProperties"]![GraphAuthFactory.ForbidPlatformCacheSwitch]!.GetValue<bool>());
        Assert.True(JsonNode.DeepEquals(original["framework"], guarded["framework"]));
        Assert.Equal(original["tfm"]!.GetValue<string>(), guarded["tfm"]!.GetValue<string>());
    }

    [Fact]
    public void AChildStartedWithTheGuardedRuntimeConfig_Runs()
    {
        var result = Review1C.RunChild(["--version"], TimeSpan.FromSeconds(60));

        Assert.True(result.ExitCode == 0, result.StandardError);
        Assert.False(string.IsNullOrWhiteSpace(result.StandardOutput));
    }
}
