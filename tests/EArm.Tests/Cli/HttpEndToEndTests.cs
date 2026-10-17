using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.TestSupport;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// The whole product through the CLI seam: <c>earm deployment …</c> on a shipped sample runs the real argument parsing, preflight, planner, providers,
/// executor and <c>HttpGraphClient</c>; only the network (an in-memory directory behind an <see cref="System.Net.Http.HttpMessageHandler"/>) and the
/// sign-in (a fake token provider) are replaced. The recorded HTTP requests are asserted, which is what a real tenant would have seen.
/// </summary>
public sealed class HttpEndToEndTests
{
    private const string Sample = "app-with-sp-and-secret";

    private static string[] Deploy(CliSandbox sandbox, string verb, params string[] extra)
    {
        return ["deployment", verb, "-f", TestPaths.Sample(Sample + ".entra.json"), "-p", "@" + TestPaths.Sample(Sample + ".parameters.json"), "-o", "json", .. extra];
    }

    [Fact]
    public async Task WhatIfThenCreateThenRerun_SendsTheExpectedRequests_AndTheRerunIsNoChange()
    {
        using var sandbox = new CliSandbox(nameof(WhatIfThenCreateThenRerun_SendsTheExpectedRequests_AndTheRerunIsNoChange));
        var directory = new FakeDirectory();
        sandbox.Handler.Directory = directory;
        string secrets = Path.Combine(sandbox.Root, "secrets.json");

        var whatIf = await CliRunner.RunAsync(Deploy(sandbox, "what-if", "--exit-code-on-changes"));
        var whatIfRequests = sandbox.RecordedRequests().ToList();

        Assert.Equal(5, whatIf.ExitCode);
        Assert.Equal(4, whatIf.Json["summary"]!["create"]!.GetValue<int>());
        Assert.All(whatIfRequests, static r => Assert.Equal("GET", r.Method));
        Assert.Equal("/v1.0/organization", whatIfRequests[0].Uri.AbsolutePath);
        Assert.Empty(sandbox.UnexpectedFiles());

        var create = await CliRunner.RunAsync(Deploy(sandbox, "create", "-y", "--secure-outputs-file", secrets));

        Assert.Equal(0, create.ExitCode);
        Assert.Equal("Succeeded", create.Json["status"]!.GetValue<string>());
        var writes = sandbox.RecordedRequests().Skip(whatIfRequests.Count).Where(static r => r.Method != "GET").ToList();
        // An application and its federated credential are upserted by their uniqueName / name (PATCH + Prefer: create-if-missing).
        var application = Assert.Single(writes, static r => r.Method == "PATCH" && r.Uri.AbsolutePath == "/v1.0/applications(uniqueName='contoso-dev-web')");
        Assert.Contains("create-if-missing", application.Headers["Prefer"], StringComparison.Ordinal);
        Assert.Equal("Contoso Web (dev)", JsonNode.Parse(application.Body!)!["displayName"]!.GetValue<string>());
        Assert.Contains(writes, static r => r.Method == "POST" && r.Uri.AbsolutePath == "/v1.0/servicePrincipals");
        Assert.Contains(writes, static r => r.Method == "POST" && r.Uri.AbsolutePath.EndsWith("/addPassword", StringComparison.Ordinal));
        Assert.Contains(writes, static r => r.Method == "PATCH" && r.Uri.AbsolutePath.Contains("federatedIdentityCredentials(name='github-main')", StringComparison.Ordinal));
        Assert.All(writes, static r => Assert.StartsWith("application/json", r.Headers["Content-Type"], StringComparison.Ordinal));        Assert.All(sandbox.RecordedRequests(), static r =>
        {
            Assert.Equal("https", r.Uri.Scheme);
            Assert.Equal("graph.microsoft.com", r.Uri.Host);
            Assert.StartsWith("Bearer ", r.Headers["Authorization"], StringComparison.Ordinal);
            Assert.StartsWith("EARM/", r.Headers["User-Agent"], StringComparison.Ordinal);
        });
        Assert.Equal(1, directory.Count("/applications"));
        var secretOutputs = JsonNode.Parse(await File.ReadAllTextAsync(secrets, TestContext.Current.CancellationToken))!.AsObject();
        Assert.NotEmpty(secretOutputs);
        Assert.DoesNotContain(secretOutputs.First().Value!["value"]!.GetValue<string>(), create.StandardOutput + create.StandardError, StringComparison.Ordinal);

        int before = sandbox.RecordedRequests().Count;
        var rerunWhatIf = await CliRunner.RunAsync(Deploy(sandbox, "what-if", "--exit-code-on-changes"));
        var rerunCreate = await CliRunner.RunAsync(Deploy(sandbox, "create", "-y"));

        Assert.Equal(0, rerunWhatIf.ExitCode);
        Assert.Equal(0, rerunWhatIf.Json["summary"]!["create"]!.GetValue<int>());
        Assert.Equal(0, rerunWhatIf.Json["summary"]!["modify"]!.GetValue<int>());
        Assert.Equal(0, rerunCreate.ExitCode);
        var rerun = sandbox.RecordedRequests().Skip(before).ToList();
        Assert.DoesNotContain(rerun, static r => r.Method is "POST" or "PUT" or "DELETE");
        Assert.DoesNotContain(rerun, static r => r.Method == "PATCH");
        Assert.Empty(sandbox.UnexpectedFiles(secrets));
    }

    [Fact]
    public async Task APermissionFailureFromGraph_ReachesTheUserAsAResourceFailure_NotACrash()
    {
        using var sandbox = new CliSandbox(nameof(APermissionFailureFromGraph_ReachesTheUserAsAResourceFailure_NotACrash));
        sandbox.UsePreflightOk(extraResponses: "{ \"match\": { \"urlContains\": \"/applications\" }, \"status\": 403, \"body\": { \"error\": { \"code\": \"Authorization_RequestDenied\", \"message\": \"Insufficient privileges to complete the operation.\" } } }");

        var result = await CliRunner.RunAsync(Deploy(sandbox, "create", "-y"));

        Assert.NotEqual(0, result.ExitCode);
        Assert.NotEqual(3, result.ExitCode);
        Assert.Contains("Authorization_RequestDenied", result.StandardOutput + result.StandardError, StringComparison.Ordinal);
    }
}
