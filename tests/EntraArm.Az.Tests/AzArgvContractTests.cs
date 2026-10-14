using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EntraArm.Az;
using EntraArm.Az.Tests.Infrastructure;
using Xunit;

namespace EntraArm.Az.Tests;

/// <summary>
/// README items 1-4: the argv the fake records through the real pwsh host equals <see cref="AzRequest.BuildAzArguments"/>,
/// tricky values arrive unchanged, UTF-8 round-trips both ways and the body file carries the request body.
/// </summary>
public sealed class AzArgvContractTests
{
    private const string V1 = "https://graph.microsoft.com/v1.0";

    private static readonly IReadOnlyDictionary<string, Func<AzRequest>> NamedRequests = new Dictionary<string, Func<AzRequest>>(StringComparer.Ordinal)
    {
        ["rest-get-select-filter-count"] = static () => AzRequest.Rest(
            "GET",
            V1 + "/users",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["$select"] = "id,displayName",
                ["$filter"] = "displayName eq 'a b' & c",
                ["$count"] = "true",
            },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["ConsistencyLevel"] = "eventual" }),

        ["rest-get-percent-quote-backtick"] = static () => AzRequest.Rest(
            "GET",
            V1 + "/users",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["$search"] = "\"displayName:100%\"",
                ["$filter"] = "mail eq 'tick`n$dollar\\back;semi|pipe<lt>gt'",
            }),

        ["rest-get-non-ascii"] = static () => AzRequest.Rest(
            "GET",
            V1 + "/users",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["$filter"] = "displayName eq 'Ünïcødé 日本語 🎉'" }),

        ["rest-get-alternate-key-apostrophes"] = static () => AzRequest.Rest(
            "GET",
            V1 + "/" + GraphPath.AlternateKey("applications", "uniqueName", "it's a 'test'")),

        ["rest-post-body-and-headers"] = static () => AzRequest.Rest(
            "POST",
            V1 + "/applications",
            null,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Prefer"] = "return=representation",
                ["Content-Type"] = "application/json",
            },
            new JsonObject
            {
                ["displayName"] = "Contract App",
                ["tags"] = new JsonArray("a", "b"),
                ["web"] = new JsonObject { ["redirectUris"] = new JsonArray("https://localhost/signin-oidc") },
            }),

        ["rest-patch-create-if-missing"] = static () => AzRequest.Rest(
            "PATCH",
            V1 + "/" + GraphPath.AlternateKey("applications", "uniqueName", "app-1"),
            null,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Prefer"] = "create-if-missing", ["Content-Type"] = "application/json" },
            new JsonObject { ["displayName"] = "App 1" }),

        ["rest-delete-ref"] = static () => AzRequest.Rest("DELETE", V1 + "/groups/g1/members/u1/$ref"),

        ["command-account-show"] = static () => AzRequest.ForCommand("account", "show"),

        ["command-get-access-token"] = static () => AzRequest.ForCommand("account", "get-access-token").WithArguments("--resource-type", "ms-graph"),

        ["command-version"] = static () => AzRequest.ForCommand("version"),
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("rest-get-select-filter-count")]
    [InlineData("rest-get-percent-quote-backtick")]
    [InlineData("rest-get-non-ascii")]
    [InlineData("rest-get-alternate-key-apostrophes")]
    [InlineData("rest-post-body-and-headers")]
    [InlineData("rest-patch-create-if-missing")]
    [InlineData("rest-delete-ref")]
    [InlineData("command-account-show")]
    [InlineData("command-get-access-token")]
    [InlineData("command-version")]
    public async Task RecordedArgvEqualsBuildAzArguments(string requestName)
    {
        var request = NamedRequests[requestName]();
        using var context = new FakeAzContext(requestName);
        using var runner = context.CreateRunner();

        var result = await runner.RunAsync(request, Ct);

        Assert.True(result.Succeeded, "stderr: " + result.StandardError);
        var record = Assert.Single(context.ReadRecords());
        Assert.Equal(request.BuildAzArguments(record.BodyFile), record.Argv);
        Assert.Equal("--only-show-errors", record.Argv[^3]);
        Assert.Equal("-o", record.Argv[^2]);
        Assert.Equal("json", record.Argv[^1]);

        if (request.IsRest)
        {
            var url = record.ValueAfter("--url");
            Assert.NotNull(url);
            Assert.DoesNotContain('?', url);
        }
        else
        {
            Assert.DoesNotContain("--url", record.Argv);
            Assert.DoesNotContain("--method", record.Argv);
        }

        if (request.Body is null)
        {
            Assert.Null(record.BodyFile);
            Assert.Null(record.Body);
            Assert.DoesNotContain("--body", record.Argv);
        }
        else
        {
            Assert.NotNull(record.BodyFile);
            Assert.Equal("@" + record.BodyFile, record.ValueAfter("--body"));
            Assert.NotNull(record.Body);
            Assert.True(JsonNode.DeepEquals(request.Body, JsonNode.Parse(record.Body)), "body file content differs from request.Body");
        }
    }

    [Theory]
    [InlineData("displayName eq 'a b' & c")]
    [InlineData("100% & more %20 %%")]
    [InlineData("\"quoted\" value")]
    [InlineData("back`tick `n `$x `\"y")]
    [InlineData("Ünïcødé 日本語 🎉")]
    [InlineData("it''s")]
    [InlineData("semi;colon|pipe<lt>gt(paren)[bracket]{brace}#hash")]
    [InlineData("trailing backslash \\")]
    public async Task UrlParameterValueArrivesUnchanged(string value)
    {
        var request = AzRequest.Rest("GET", V1 + "/users", new Dictionary<string, string>(StringComparer.Ordinal) { ["$filter"] = value });
        using var context = new FakeAzContext("url-parameter-value");
        using var runner = context.CreateRunner();

        var result = await runner.RunAsync(request, Ct);

        Assert.True(result.Succeeded, "stderr: " + result.StandardError);
        var record = Assert.Single(context.ReadRecords());
        var parameter = Assert.Single(record.Section("--url-parameters"));
        Assert.Equal("$filter=" + value, parameter);
    }

    [Fact]
    public async Task HeaderValueWithEqualsSignArrivesUnchanged()
    {
        var request = AzRequest.Rest(
            "GET",
            V1 + "/users",
            null,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Prefer"] = "odata.maxpagesize=5, return=representation", ["ConsistencyLevel"] = "eventual" });
        using var context = new FakeAzContext(nameof(HeaderValueWithEqualsSignArrivesUnchanged));
        using var runner = context.CreateRunner();

        var result = await runner.RunAsync(request, Ct);

        Assert.True(result.Succeeded, "stderr: " + result.StandardError);
        var record = Assert.Single(context.ReadRecords());
        Assert.Equal(["Prefer=odata.maxpagesize=5, return=representation", "ConsistencyLevel=eventual"], record.Section("--headers"));
    }

    [Fact]
    public async Task RunnerSetsTheAzureCliAndPowerShellEnvironment()
    {
        using var context = new FakeAzContext(nameof(RunnerSetsTheAzureCliAndPowerShellEnvironment));
        using var runner = context.CreateRunner();

        var result = await runner.RunAsync(AzRequest.ForCommand("version"), Ct);

        Assert.True(result.Succeeded, "stderr: " + result.StandardError);
        var record = Assert.Single(context.ReadRecords());
        Assert.Equal("json", record.Env["AZURE_CORE_OUTPUT"]);
        Assert.Equal("1", record.Env["AZURE_CORE_NO_COLOR"]);
        Assert.Equal("1", record.Env["POWERSHELL_TELEMETRY_OPTOUT"]);
        Assert.Equal("Off", record.Env["POWERSHELL_UPDATECHECK"]);
        Assert.False(string.IsNullOrEmpty(record.Cwd));
    }

    [Fact]
    public async Task NonAsciiStdoutRoundTripsIntoStandardOutput()
    {
        using var context = new FakeAzContext(nameof(NonAsciiStdoutRoundTripsIntoStandardOutput));
        context.UseFixtureFile("non-ascii-user.json");
        using var runner = context.CreateRunner();

        var result = await runner.RunAsync(AzRequest.Rest("GET", V1 + "/users/11111111-2222-3333-4444-555555555555"), Ct);

        Assert.True(result.Succeeded, "stderr: " + result.StandardError);
        Assert.Contains("Ünïcødé 日本語 🎉", result.StandardOutput);
        var body = Assert.IsType<JsonObject>(JsonNode.Parse(result.StandardOutput));
        Assert.Equal("Ünïcødé 日本語 🎉", body["displayName"]?.GetValue<string>());
        Assert.Equal("ünï@contoso.example", body["mail"]?.GetValue<string>());
        Assert.Equal("quotes ' \" and backslash \\ and tab \t and <angle> & ampersand", body["note"]?.GetValue<string>());
    }

    [Fact]
    public async Task NonAsciiStderrRoundTripsIntoStandardError()
    {
        using var context = new FakeAzContext(nameof(NonAsciiStderrRoundTripsIntoStandardError));
        context.UseScenario(new FakeScenario().Add(FakeResponse.Error("ERROR: Ünïcødé 日本語 🎉 failed", exitCode: 3)));
        using var runner = context.CreateRunner();

        var result = await runner.RunAsync(AzRequest.Rest("GET", V1 + "/users"), Ct);

        Assert.False(result.Succeeded);
        Assert.Equal(3, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Contains("ERROR: Ünïcødé 日本語 🎉 failed", result.StandardError);
    }

    [Fact]
    public async Task AsciiBodyFileEqualsToJsonStringExactly()
    {
        var body = new JsonObject
        {
            ["displayName"] = "plain",
            ["signInAudience"] = "AzureADMyOrg",
            ["requiredResourceAccess"] = new JsonArray(new JsonObject { ["resourceAppId"] = "00000003-0000-0000-c000-000000000000" }),
            ["count"] = 3,
            ["enabled"] = true,
            ["nothing"] = null,
        };
        using var context = new FakeAzContext(nameof(AsciiBodyFileEqualsToJsonStringExactly));
        using var runner = context.CreateRunner();

        var result = await runner.RunAsync(AzRequest.Rest("POST", V1 + "/applications", body: body), Ct);

        Assert.True(result.Succeeded, "stderr: " + result.StandardError);
        var record = Assert.Single(context.ReadRecords());
        Assert.Equal(body.ToJsonString(), record.Body);
    }

    [Fact]
    public async Task NonAsciiBodyIsWrittenAsRawUtf8()
    {
        // BMP characters are written raw (relaxed escaping); astral characters such as emoji are surrogate-escaped by
        // System.Text.Json regardless of the encoder, which az decodes identically. Semantics must match exactly.
        var body = new JsonObject
        {
            ["displayName"] = "Ünïcødé 日本語 🎉",
            ["description"] = "quotes ' \" <tag> & amp + plus",
        };
        using var context = new FakeAzContext(nameof(NonAsciiBodyIsWrittenAsRawUtf8));
        using var runner = context.CreateRunner();

        var result = await runner.RunAsync(AzRequest.Rest("POST", V1 + "/applications", body: body), Ct);

        Assert.True(result.Succeeded, "stderr: " + result.StandardError);
        var record = Assert.Single(context.ReadRecords());
        Assert.NotNull(record.Body);
        Assert.Contains("Ünïcødé 日本語", record.Body);
        Assert.Contains("<tag> & amp + plus", record.Body);
        Assert.DoesNotContain("\\u00", record.Body);
        Assert.DoesNotContain("\\u65E5", record.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\\u003C", record.Body, StringComparison.OrdinalIgnoreCase);
        Assert.True(JsonNode.DeepEquals(body, JsonNode.Parse(record.Body)));
    }

    [Fact]
    public void RestRequestRejectsQueryStringInUrl()
    {
        var exception = Assert.Throws<ArgumentException>(static () => AzRequest.Rest("GET", V1 + "/users?$select=id"));
        Assert.Equal("url", exception.ParamName);
    }

    [Fact]
    public void BuildAzArgumentsShapesRestAndCommandRequests()
    {
        var rest = AzRequest.Rest(
            "get",
            V1 + "/users",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["$select"] = "id" },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Prefer"] = "x" });
        Assert.Equal("GET", rest.Method);
        Assert.Equal(
            ["rest", "--method", "GET", "--url", V1 + "/users", "--url-parameters", "$select=id", "--headers", "Prefer=x", "--body", "@C:\\b.json", "--only-show-errors", "-o", "json"],
            rest.BuildAzArguments("C:\\b.json"));
        Assert.Equal(
            ["rest", "--method", "GET", "--url", V1 + "/users", "--url-parameters", "$select=id", "--headers", "Prefer=x", "--only-show-errors", "-o", "json"],
            rest.BuildAzArguments(null));

        var command = AzRequest.ForCommand("account", "get-access-token").WithArguments("--resource-type", "ms-graph");
        Assert.False(command.IsRest);
        Assert.Equal("account get-access-token", command.CommandText);
        Assert.Equal(["account", "get-access-token", "--resource-type", "ms-graph", "--only-show-errors", "-o", "json"], command.BuildAzArguments(null));

        Assert.Throws<ArgumentException>(static () => AzRequest.ForCommand());
        Assert.Throws<ArgumentException>(static () => AzRequest.ForCommand("account", " "));
    }
}
