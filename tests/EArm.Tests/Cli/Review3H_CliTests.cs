using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.TestSupport;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// Review round 2 through the command line: one JSON document from every verb (S2-20), the options (S2-21) and no blind retry of a create (S2-07).
/// </summary>
public sealed class Review3H_CliTests
{
    private const string Api = "2026-10-01-preview";

    private static (CliSandbox Sandbox, FakeDirectory Directory) Setup(string test)
    {
        var sandbox = new CliSandbox(test);
        var directory = new FakeDirectory();
        sandbox.Handler.Directory = directory;
        sandbox.Tokens.TenantId = CliSandbox.Tenant;
        return (sandbox, directory);
    }

    private static string DeclaredGroupTemplate(CliSandbox sandbox)
    {
        return sandbox.WriteTemplate("t.entra.json", "\"resources\": [ { \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"" + Api + "\", \"name\": \"declared\", \"properties\": { \"displayName\": \"Declared\", \"mailEnabled\": false, \"securityEnabled\": true } } ]");
    }

    private static JsonObject OneDocument(CliResult result)
    {
        // Exactly one JSON document on stdout: JsonDocument.Parse refuses trailing content.
        using var document = JsonDocument.Parse(result.StandardOutput);
        return JsonNode.Parse(result.StandardOutput)!.AsObject();
    }

    [Fact]
    public async Task DeploymentDelete_Json_IsOneDocument_ForADelete_AndForNothingToDelete()
    {
        var (sandbox, directory) = Setup(nameof(DeploymentDelete_Json_IsOneDocument_ForADelete_AndForNothingToDelete));
        using var _ = sandbox;
        directory.AddGroup("declared", "Declared");
        string template = DeclaredGroupTemplate(sandbox);

        var objects = await CliRunner.RunAsync("deployment", "delete", "-f", template, "-y", "-o", "json");
        var nothing = await CliRunner.RunAsync("deployment", "delete", "-f", template, "-y", "-o", "json");

        Assert.Equal(0, objects.ExitCode);
        var doc = OneDocument(objects);
        Assert.Equal("Succeeded", doc["status"]!.GetValue<string>());
        Assert.Equal(1, doc["deleted"]!.GetValue<int>());
        Assert.Equal("Deleted", doc["outcomes"]![0]!["status"]!.GetValue<string>());
        Assert.Equal(0, directory.Count("/groups"));
        Assert.Equal(0, nothing.ExitCode);
        var none = OneDocument(nothing);
        Assert.Equal("Succeeded", none["status"]!.GetValue<string>());
        Assert.Contains("Nothing to delete", none["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("what-if")]
    [InlineData("create")]
    public async Task APreflightFailure_Json_IsOneFailedDocument_WithoutLiteralCarriageReturns(string verb)
    {
        using var sandbox = new CliSandbox("preflight-" + verb);
        sandbox.UseNotLoggedIn();
        string template = sandbox.WriteEmptyTemplate();

        string[] extra = verb == "create" ? ["-y"] : [];
        var result = await CliRunner.RunAsync(["deployment", verb, "-f", template, .. extra, "-o", "json"]);

        Assert.Equal(3, result.ExitCode);
        var doc = OneDocument(result);
        Assert.Equal("Failed", doc["status"]!.GetValue<string>());
        string message = doc["diagnostics"]![0]!["message"]!.GetValue<string>();
        Assert.StartsWith("Preflight failed", message, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', message);
        Assert.DoesNotContain("\\r", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void TheVerbOutputNormalisesLineBreaks_SoAMultiLineMessageReadsAsLines()
    {
        Assert.Equal("a\nb\nc", EArm.Cli.Commands.VerbOutput.Normalize("a\r\nb\rc"));
    }

    // ---- S2-21: options ----

    [Theory]
    [InlineData("--parallelism", "-1")]
    [InlineData("--parallelism", "0")]
    public async Task ANegativeBound_IsAUsageError_NothingRuns(string option, string value)
    {
        using var sandbox = new CliSandbox("bound" + option.Length + value);
        sandbox.UsePreflightOk();
        string template = sandbox.WriteEmptyTemplate();

        var result = await CliRunner.RunAsync("deployment", "create", "-f", template, "-y", option, value);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(option, result.StandardError + result.StandardOutput, StringComparison.Ordinal);
        Assert.Empty(sandbox.RecordedCommands());
        Assert.Empty(sandbox.UnexpectedFiles());
    }

    // ---- S2-07 through the real HTTP stack ----

    [Fact]
    public async Task AUserCreate_ThatAnswers503_IsSentExactlyOnce_AndGoesThroughTheKeyReRead()
    {
        using var sandbox = new CliSandbox(nameof(AUserCreate_ThatAnswers503_IsSentExactlyOnce_AndGoesThroughTheKeyReRead));
        string missing = "{ \"match\": { \"method\": \"GET\", \"urlContains\": \"/users/\" }, \"sticky\": true, \"status\": 404, \"body\": { \"error\": { \"code\": \"Request_ResourceNotFound\", \"message\": \"Resource does not exist.\" } } }";
        string deleted = "{ \"match\": { \"method\": \"GET\", \"urlContains\": \"/directory/deletedItems\" }, \"sticky\": true, \"status\": 200, \"body\": { \"value\": [] } }";
        string first = "{ \"match\": { \"method\": \"POST\", \"urlContains\": \"/users\" }, \"status\": 503, \"body\": { \"error\": { \"code\": \"ServiceUnavailable\", \"message\": \"try later\" } } }";
        string second = "{ \"match\": { \"method\": \"POST\", \"urlContains\": \"/users\" }, \"status\": 201, \"body\": { \"id\": \"11111111-aaaa-4aaa-8aaa-111111111111\", \"displayName\": \"U\", \"userPrincipalName\": \"u1@contoso.com\" } }";
        sandbox.UsePreflightOk(extraResponses: first + "," + second + "," + deleted + "," + missing);
        string template = sandbox.WriteTemplate("t.entra.json", "\"resources\": [ { \"type\": \"Microsoft.Entra/users\", \"apiVersion\": \"" + Api + "\", \"name\": \"u1@contoso.com\", \"properties\": { \"displayName\": \"U\", \"accountEnabled\": true, \"passwordProfile\": { \"password\": \"Initial-P@ssw0rd-1\" } } } ]");

        var result = await CliRunner.RunAsync("deployment", "create", "-f", template, "-n", "dep", "-y", "-o", "json");

        Assert.Equal(1, result.ExitCode);
        var commands = sandbox.RecordedCommands();
        Assert.Equal(1, commands.Count(c => c.StartsWith("POST /v1.0/users", StringComparison.Ordinal)));
        int post = commands.ToList().FindIndex(c => c.StartsWith("POST /v1.0/users", StringComparison.Ordinal));
        Assert.Contains(commands.Skip(post + 1), c => c.StartsWith("GET /v1.0/users/u1@contoso.com", StringComparison.Ordinal));
    }
}