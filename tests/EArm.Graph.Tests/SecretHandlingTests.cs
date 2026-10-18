using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph.Tests.Infrastructure;
using Xunit;

namespace EArm.Graph.Tests;

/// <summary>README item 11: <see cref="SecretRedactor"/> unit cases and the end-to-end "no secret in logs" check at Trace level.</summary>
public sealed class SecretHandlingTests
{
    private const string Jwt = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4ifQ.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("secretText", true)]
    [InlineData("password", true)]
    [InlineData("Authorization", true)]
    [InlineData("client_secret", true)]
    [InlineData("clientSecret", true)]
    [InlineData("accessToken", true)]
    [InlineData("passwordProfile", true)]
    [InlineData("keyCredentials", true)]
    [InlineData("x-ms-token", true)]
    [InlineData("myPasswordHint", true)]
    [InlineData("$skiptoken", false)]
    [InlineData("skiptoken", false)]
    [InlineData("$deltatoken", false)]
    [InlineData("deltaToken", false)]
    [InlineData("displayName", false)]
    [InlineData("$filter", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSecretName_FlagsSecretLikeNamesButNotPagingTokens(string? name, bool expected)
    {
        Assert.Equal(expected, SecretRedactor.IsSecretName(name));
    }

    [Theory]
    [InlineData("{\"secretText\":\"abc123\"}", "\"secretText\": \"***\"", "abc123")]
    [InlineData("{\"passwordProfile\":{\"password\":\"Hunter2!\"}}", "\"password\": \"***\"", "Hunter2!")]
    [InlineData("password=Hunter2!", "password=***", "Hunter2!")]
    [InlineData("client_secret: s3cr3t,next", "client_secret: ***", "s3cr3t")]
    [InlineData("clientSecret=s3cr3t", "clientSecret=***", "s3cr3t")]
    [InlineData("Bearer abc.def.ghi", "Bearer ***", "abc.def.ghi")]
    [InlineData("Authorization: Bearer " + Jwt, "Authorization: ***", "SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c")]
    [InlineData("token " + Jwt + " end", "token *** end", "SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c")]
    [InlineData("Authorization: Bearer abc.def.ghi", "Authorization: ***", "abc.def.ghi")]
    [InlineData("WARNING: secretText=S3cr3tValue end", "secretText=***", "S3cr3tValue")]
    [InlineData("credentials=abc", "credentials=***", "abc")]
    [InlineData("x-ms-token=abc", "x-ms-token=***", "abc")]
    public void RedactText_MasksSecretFragments(string text, string expectedFragment, string secret)
    {
        var redacted = SecretRedactor.RedactText(text);

        Assert.Contains(expectedFragment, redacted);
        Assert.DoesNotContain(secret, redacted);
    }

    [Theory]
    [InlineData("WARNING: secretText=S3cr3tValue end", "WARNING: secretText=*** end")]
    [InlineData("credentials=abc", "credentials=***")]
    [InlineData("x-ms-token=abc", "x-ms-token=***")]
    [InlineData("https://host/path?password=x", "https://host/path?password=***")]
    [InlineData("--password=abc", "--password=***")]
    [InlineData("note: Bearer abc", "note: Bearer ***")]
    [InlineData("Authorization: Bearer abc.def.ghi", "Authorization: ***")]
    public void RedactText_ProducesExactOutput(string text, string expected)
    {
        Assert.Equal(expected, SecretRedactor.RedactText(text));
    }

    [Theory]
    [InlineData("https://graph.microsoft.com/v1.0/users?$skiptoken=X1234abcd&$select=id")]
    [InlineData("$skiptoken=RFL+X'abc'")]
    [InlineData("displayName eq 'a b' & c")]
    [InlineData("ERROR: Not Found({\"error\":{\"code\":\"Request_ResourceNotFound\",\"message\":\"Resource 'x' does not exist.\"}})")]
    [InlineData("Time: 12:30:45")]
    [InlineData("")]
    public void RedactText_LeavesNonSecretTextAlone(string text)
    {
        Assert.Equal(text, SecretRedactor.RedactText(text));
        Assert.Equal(string.Empty, SecretRedactor.RedactText(null));
    }

    [Theory]
    [InlineData("Authorization=Bearer abc", "Authorization=***")]
    [InlineData("password=abc", "password=***")]
    [InlineData("client_secret=abc", "client_secret=***")]
    [InlineData("$skiptoken=abc", "$skiptoken=abc")]
    [InlineData("$filter=displayName eq 'x'", "$filter=displayName eq 'x'")]
    [InlineData("--only-show-errors", "--only-show-errors")]
    [InlineData("=weird", "=weird")]
    public void RedactArgument_MasksSecretNamedValuesOnly(string argument, string expected)
    {
        Assert.Equal(expected, SecretRedactor.RedactArgument(argument));
    }

    [Fact]
    public void RedactValue_UsesTheNameFirstThenTheText()
    {
        Assert.Equal("***", SecretRedactor.RedactValue("Authorization", "Bearer x"));
        Assert.Equal("x", SecretRedactor.RedactValue("$filter", "x"));
        Assert.Equal("password=***", SecretRedactor.RedactValue("$filter", "password=abc"));
        Assert.Equal(string.Empty, SecretRedactor.RedactValue("password", null));
    }

    [Fact]
    public void RedactJson_DeepCopiesWithSecretPropertiesMasked()
    {
        var original = new JsonObject
        {
            ["displayName"] = "keep",
            ["secretText"] = "mask-me",
            ["passwordProfile"] = new JsonObject { ["password"] = "mask-me-too", ["forceChangePasswordNextSignIn"] = true },
            ["$skiptoken"] = "keep-token",
            ["credentials"] = new JsonArray(new JsonObject { ["keyId"] = "k1", ["secretText"] = "mask-me-three" }),
            ["count"] = 2,
            ["nothing"] = null,
        };
        var snapshot = original.ToJsonString();

        var redacted = Assert.IsType<JsonObject>(SecretRedactor.RedactJson(original));

        Assert.Equal("keep", redacted["displayName"]?.GetValue<string>());
        Assert.Equal("***", redacted["secretText"]?.GetValue<string>());
        Assert.Equal("***", redacted["passwordProfile"]?.GetValue<string>());
        Assert.Equal("keep-token", redacted["$skiptoken"]?.GetValue<string>());
        Assert.Equal("***", redacted["credentials"]?.GetValue<string>());
        Assert.Equal(2, redacted["count"]?.GetValue<int>());
        Assert.Null(redacted["nothing"]);
        Assert.DoesNotContain("mask-me", redacted.ToJsonString());
        Assert.Equal(snapshot, original.ToJsonString());
        Assert.Null(SecretRedactor.RedactJson(null));

        var array = Assert.IsType<JsonArray>(SecretRedactor.RedactJson(new JsonArray(new JsonObject { ["password"] = "p" }, "plain")));
        Assert.Equal("***", array[0]?["password"]?.GetValue<string>());
        Assert.Equal("plain", array[1]?.GetValue<string>());
    }

    [Fact]
    public async Task NoSecretReachesTheLogAtTraceLevel()
    {
        const string password = "Hunter2!Secret-Value-77";
        const string secretText = "S3cr3tT3xt~FromGraph~88";
        var handler = new ScriptedHttpHandler(
            ScriptedResponse.Json("{\"id\":\"app-1\",\"secretText\":\"" + secretText + "\",\"displayName\":\"app\"}", System.Net.HttpStatusCode.Created)
                .WithHeader("request-id", "req-1"),
            ScriptedResponse.Error(System.Net.HttpStatusCode.BadRequest, "Request_BadRequest", "Invalid password=" + password + " and Authorization: Bearer " + Jwt));
        using var harness = new ClientHarness(handler);
        harness.Tokens.Failure = null;
        var request = new GraphRequest("/applications").WithParameter("$filter", "displayName eq 'app'").WithParameter("access_token", Jwt).WithParameter("$skiptoken", "page-2");
        var body = new JsonObject
        {
            ["displayName"] = "app",
            ["passwordProfile"] = new JsonObject { ["password"] = password },
        };

        var response = await harness.Client.PostAsync(request, body, Ct);
        var failure = await Assert.ThrowsAsync<GraphException>(() => harness.Client.PostAsync(new GraphRequest("/applications"), body, Ct));

        Assert.Equal(secretText, response.Body?["secretText"]?.GetValue<string>());

        var entries = harness.Log.Entries;
        Assert.NotEmpty(entries);
        Assert.Contains(entries, e => e.EventId.Id == 1000);

        var text = harness.Log.AllText + Environment.NewLine + failure.Message + Environment.NewLine + failure.GraphMessage + Environment.NewLine + failure;
        Assert.DoesNotContain(password, text);
        Assert.DoesNotContain(secretText, text);
        Assert.DoesNotContain(Jwt, text);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9", text);
        Assert.DoesNotContain("token-1", text);
        Assert.DoesNotContain("Bearer ", text);
        Assert.Contains("access_token=***", text);
        Assert.Contains("$skiptoken=page-2", text);
        Assert.Contains("$filter=displayName%20eq%20%27app%27", text);
        Assert.Contains("/v1.0/applications", text);
        Assert.Contains("req-1", text);
        Assert.DoesNotContain(entries.Select(static e => e.Message), m => m.Contains("displayName\":\"app", StringComparison.Ordinal));
    }
}