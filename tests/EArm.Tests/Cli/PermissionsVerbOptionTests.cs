using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary><c>earm permissions --verb whatif|create|delete</c>: the delegated scopes one run of that verb requests (offline).</summary>
public sealed class PermissionsVerbOptionTests
{
    private static Task<CliResult> OrdersAsync(params string[] extra)
    {
        string[] args = ["permissions", "-f", TestPaths.Sample("orders-api.entra.json"), "-p", "@" + TestPaths.Sample("orders-api.parameters.json"), .. extra];
        return CliRunner.RunAsync(args);
    }

    [Theory]
    [InlineData("whatif")]
    [InlineData("create")]
    [InlineData("delete")]
    [InlineData("WhatIf")]
    public async Task Json_HasTheDocumentedShape(string verb)
    {
        var result = await OrdersAsync("--verb", verb, "-o", "json");

        Assert.Equal(0, result.ExitCode);
        var json = result.Json;
        Assert.Equal(["template", "verb", "graphScopes", "needsKeyVault", "scopes"], json.Select(p => p.Key).ToList());
        Assert.Equal(verb.ToLowerInvariant(), json["verb"]!.GetValue<string>());
        Assert.True(json["needsKeyVault"]!.GetValue<bool>());
        var names = json["graphScopes"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Contains("Organization.Read.All", names);
        Assert.Contains("User.Read", names);
        Assert.Equal(names, json["scopes"]!.AsArray().Select(s => s!["name"]!.GetValue<string>()).ToList());
    }

    [Fact]
    public async Task WhatIf_ListsOnlyReadScopes()
    {
        var result = await OrdersAsync("--verb", "whatif", "-o", "json");

        var names = result.Json["graphScopes"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.DoesNotContain(names, n => n.Contains("ReadWrite", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Table_ListsTheScopesAndTheKeyVaultNeed()
    {
        var result = await OrdersAsync("--verb", "create");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Delegated scopes a create run of", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("  Policy.ReadWrite.ConditionalAccess", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("https://vault.azure.net/user_impersonation", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutKeyVaultReferences_SaysKeyVaultIsNotNeeded()
    {
        var result = await CliRunner.RunAsync("permissions", "-f", TestPaths.Sample("tenant-security.entra.json"), "--verb", "whatif");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Azure Key Vault: not needed", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplicitAppMode_IsRejected_ButDelegatedModeIsAccepted()
    {
        var app = await OrdersAsync("--verb", "create", "--mode", "app", "-o", "json");
        var delegated = await OrdersAsync("--verb", "create", "--mode", "delegated", "-o", "json");

        Assert.Equal(1, app.ExitCode);
        Assert.Contains("--mode app", app.StandardOutput + app.StandardError, StringComparison.Ordinal);
        Assert.Equal(0, delegated.ExitCode);
    }

    [Fact]
    public async Task UnknownVerb_IsAParseError()
    {
        var result = await OrdersAsync("--verb", "validate");

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task NoneOutput_PrintsNothing()
    {
        var result = await OrdersAsync("--verb", "whatif", "-o", "none");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
    }
}
