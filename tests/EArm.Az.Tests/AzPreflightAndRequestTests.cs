using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Az;
using EArm.Az.Tests.Infrastructure;
using Xunit;

namespace EArm.Az.Tests;

/// <summary>
/// <see cref="AzPreflight"/> over scripted az answers and a stub Graph (no pwsh), the <see cref="GraphRequest"/> builder and its
/// next-link handling, and the owner-only temp directory.
/// </summary>
public sealed class AzPreflightAndRequestTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Account(string tenant = Tenant, string extra = "")
    {
        return "{ \"environmentName\": \"AzureCloud\", \"id\": \"sub-1\", \"name\": \"Sub\", \"tenantId\": \"" + tenant + "\", \"user\": { \"name\": \"alice@contoso.example\", \"type\": \"user\" }" + extra + " }";
    }

    private static ScriptedAzRunner Az(string versionOut, AzResult account)
    {
        return new ScriptedAzRunner((request, index) => request.CommandText == "version" ? ScriptedAzRunner.Ok(versionOut) : account);
    }

    private static AzPreflight Preflight(IAzRunner runner, Func<GraphRequest, JsonNode?> graph, LogSink? sink = null)
    {
        return new AzPreflight(runner, new StubGraph(graph), new TestLogger<AzPreflight>(sink ?? new LogSink()));
    }

    private static JsonNode Organization(string id)
    {
        return new JsonObject { ["value"] = new JsonArray(new JsonObject { ["id"] = id }) };
    }

    // ---- preflight ----

    [Fact]
    public async Task Preflight_ReadsTheSessionAndTheOrganization_AndReportsEveryField()
    {
        var runner = Az("{ \"azure-cli\": \"2.70.0\" }", ScriptedAzRunner.Ok(Account()));

        var result = await Preflight(runner, _ => Organization(Tenant)).RunAsync(Ct);

        Assert.Equal(Tenant, result.TenantId);
        Assert.Equal(Tenant, result.OrganizationId);
        Assert.Equal("alice@contoso.example", result.UserName);
        Assert.Equal("user", result.UserType);
        Assert.Equal("2.70.0", result.AzVersion);
        Assert.Equal("AzureCloud", result.CloudName);
        Assert.Equal("sub-1", result.SubscriptionId);
        Assert.Equal("Sub", result.SubscriptionName);
    }

    [Fact]
    public async Task Preflight_ToleratesAnUnparsableVersion_AMissingUser_AndAHomeTenantFallback()
    {
        var runner = Az("not json at all", ScriptedAzRunner.Ok("{ \"homeTenantId\": \"" + Tenant + "\" }"));

        var result = await Preflight(runner, _ => Organization(Tenant)).RunAsync(Ct);

        Assert.Null(result.AzVersion);
        Assert.Null(result.UserName);
        Assert.Null(result.UserType);
        Assert.Equal(Tenant, result.TenantId);
    }

    [Fact]
    public async Task Preflight_ATenantMismatchBetweenAccountAndOrganization_IsLogged_NotFatal()
    {
        var sink = new LogSink();
        var runner = Az("{ }", ScriptedAzRunner.Ok(Account()));

        var result = await Preflight(runner, _ => Organization("22222222-2222-2222-2222-222222222222"), sink).RunAsync(Ct);

        Assert.Equal(Tenant, result.TenantId);
        Assert.Equal("22222222-2222-2222-2222-222222222222", result.OrganizationId);
        Assert.Contains(sink.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
        var sameCase = await Preflight(Az("{ }", ScriptedAzRunner.Ok(Account())), _ => Organization(Tenant.ToUpperInvariant()), new LogSink()).RunAsync(Ct);
        Assert.Equal(Tenant, sameCase.TenantId);
    }

    [Fact]
    public async Task Preflight_AFailingVersion_IsTheAzFailure_AndAnUnclassifiedAccountFailureMeansNotLoggedIn()
    {
        var versionFails = new ScriptedAzRunner((request, _) => ScriptedAzRunner.Fail("'az' is not recognized as an internal or external command", 1));
        var unclassified = Az("{ }", ScriptedAzRunner.Fail("something odd happened", 7));
        var loggedOut = Az("{ }", ScriptedAzRunner.Fail("ERROR: Please run 'az login' to setup account.", 1));

        var first = await Assert.ThrowsAsync<AzCliException>(() => Preflight(versionFails, _ => Organization(Tenant)).RunAsync(Ct));
        var second = await Assert.ThrowsAsync<AzCliException>(() => Preflight(unclassified, _ => Organization(Tenant)).RunAsync(Ct));
        var third = await Assert.ThrowsAsync<AzCliException>(() => Preflight(loggedOut, _ => Organization(Tenant)).RunAsync(Ct));

        Assert.Contains("az version", first.Message, StringComparison.Ordinal);
        Assert.Equal(AzCliFailure.NotLoggedIn, second.Failure);
        Assert.Contains(AzCliException.LoginHint, second.Message, StringComparison.Ordinal);
        Assert.Equal(AzCliFailure.NotLoggedIn, third.Failure);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[1, 2]")]
    [InlineData("\"text\"")]
    [InlineData("{ \"user\": { \"name\": \"x\" } }")]
    [InlineData("{ \"tenantId\": \"\" }")]
    public async Task Preflight_AnAccountWithoutAUsableTenant_Fails(string accountOutput)
    {
        var runner = Az("{ }", ScriptedAzRunner.Ok(accountOutput));

        var ex = await Assert.ThrowsAsync<AzCliException>(() => Preflight(runner, _ => Organization(Tenant)).RunAsync(Ct));

        Assert.True(ex.Failure is AzCliFailure.NotLoggedIn or AzCliFailure.Unknown, ex.Failure.ToString());
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public async Task Preflight_ARejectedGraphToken_IsNotLoggedIn_AndAnEmptyOrganizationListIsAnError()
    {
        var runner = Az("{ }", ScriptedAzRunner.Ok(Account()));

        var rejected = await Assert.ThrowsAsync<AzCliException>(() => Preflight(runner, _ => throw new GraphException(HttpStatusCode.Unauthorized, "InvalidAuthenticationToken", "expired", RetryHint.Default)).RunAsync(Ct));
        Assert.Equal(AzCliFailure.NotLoggedIn, rejected.Failure);
        Assert.IsType<GraphException>(rejected.InnerException);

        foreach (JsonNode? body in new JsonNode?[] { null, new JsonObject(), new JsonObject { ["value"] = new JsonArray() }, new JsonObject { ["value"] = new JsonArray(new JsonObject { ["id"] = string.Empty }) }, new JsonObject { ["value"] = new JsonArray(5) } })
        {
            var ex = await Assert.ThrowsAsync<AzCliException>(() => Preflight(runner, _ => body).RunAsync(Ct));
            Assert.Contains("returned no organization id", ex.Message, StringComparison.Ordinal);
        }

        var other = await Assert.ThrowsAsync<GraphException>(() => Preflight(runner, _ => throw new GraphException(HttpStatusCode.Forbidden, "Authorization_RequestDenied", "no", RetryHint.Default)).RunAsync(Ct));
        Assert.True(other.IsForbidden);
    }

    [Fact]
    public void Preflight_RejectsNullDependencies()
    {
        var log = new TestLogger<AzPreflight>(new LogSink());
        var runner = ScriptedAzRunner.Sequence(ScriptedAzRunner.Ok("{}"));
        var graph = new StubGraph(_ => null);

        Assert.Throws<ArgumentNullException>(() => new AzPreflight(null!, graph, log));
        Assert.Throws<ArgumentNullException>(() => new AzPreflight(runner, null!, log));
        Assert.Throws<ArgumentNullException>(() => new AzPreflight(runner, graph, null!));
    }

    // ---- GraphRequest ----

    [Fact]
    public void GraphRequest_BuildsQueriesHeadersAndHints_WithoutMutatingTheOriginal()
    {
        var original = new GraphRequest("users");

        var built = original.WithSelect("id", "displayName").WithFilter("startswith(displayName,'a')").WithTop(5).WithExpand("manager").WithOrderBy("displayName").WithSearch("\"alice\"").WithCount()
            .WithHeader("X-One", "1").WithPrefer("return=representation").WithPrefer("create-if-missing").WithConsistencyLevelEventual().WithHint(RetryHint.ExpectReplicationLag).WithApi(GraphApi.Beta);

        Assert.Null(original.UrlParameters);
        Assert.Equal("/users", original.Path);
        Assert.Equal(["$select", "$filter", "$top", "$expand", "$orderby", "$search", "$count"], built.UrlParameters!.Keys.ToArray());
        Assert.Equal("id,displayName", built.UrlParameters["$select"]);
        Assert.Equal("5", built.UrlParameters["$top"]);
        Assert.Equal("true", built.UrlParameters["$count"]);
        var headers = built.EffectiveHeaders();
        Assert.Equal("return=representation, create-if-missing", headers["Prefer"]);
        Assert.Equal("eventual", headers["ConsistencyLevel"]);
        Assert.Equal("1", headers["x-one"]);
        Assert.Equal(RetryHint.ExpectReplicationLag, built.Hint);
        Assert.StartsWith("https://graph.microsoft.com/beta/users", built.Url, StringComparison.Ordinal);
        Assert.Contains("$top=5", built.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void GraphRequest_ValidatesItsArguments()
    {
        Assert.Throws<ArgumentException>(() => new GraphRequest("/users?$select=id"));
        Assert.ThrowsAny<ArgumentException>(() => new GraphRequest(" "));
        var request = new GraphRequest("/x");
        Assert.Throws<ArgumentOutOfRangeException>(() => request.WithTop(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => request.WithTop(-5));
        Assert.ThrowsAny<ArgumentException>(() => request.WithFilter(" "));
        Assert.ThrowsAny<ArgumentException>(() => request.WithExpand(string.Empty));
        Assert.ThrowsAny<ArgumentException>(() => request.WithOrderBy(" "));
        Assert.ThrowsAny<ArgumentException>(() => request.WithSearch(string.Empty));
        Assert.ThrowsAny<ArgumentException>(() => request.WithParameter(" ", "v"));
        Assert.Throws<ArgumentNullException>(() => request.WithParameter("p", null!));
        Assert.ThrowsAny<ArgumentException>(() => request.WithHeader(string.Empty, "v"));
        Assert.Throws<ArgumentNullException>(() => request.WithHeader("h", null!));
        Assert.ThrowsAny<ArgumentException>(() => request.WithPrefer(" "));
        Assert.Throws<ArgumentNullException>(() => request.WithSelect(null!));
    }

    [Theory]
    [InlineData("https://graph.microsoft.com/v1.0/users?$skiptoken=a%2Bb+c&$top=2", "/users", "a+b c")]
    [InlineData("https://graph.microsoft.com/v1.0/users", "/users", null)]
    [InlineData("https://graph.microsoft.com/v1.0/groups/g/members?$skiptoken=x&flag", "/groups/g/members", "x")]
    public void NextLinks_AreSplitBackIntoPathAndDecodedParameters(string link, string path, string? skipToken)
    {
        var next = new GraphRequest("/users").WithHeader("ConsistencyLevel", "eventual").WithHint(RetryHint.ExpectReplicationLag).ForNextLink(link);

        Assert.Equal(path, next.Path);
        Assert.Equal(skipToken, next.UrlParameters is not null && next.UrlParameters.TryGetValue("$skiptoken", out var token) ? token : null);
        Assert.Equal("eventual", next.EffectiveHeaders()["ConsistencyLevel"]);
        Assert.Equal(RetryHint.ExpectReplicationLag, next.Hint);
    }

    [Fact]
    public void ANextLinkOnAnotherBase_KeepsItsAbsoluteUrl()
    {
        var next = new GraphRequest("/users").ForNextLink("https://graph.microsoft.com/beta/users?$skiptoken=t");

        Assert.Equal("https://graph.microsoft.com/beta/users", next.Url);
        Assert.Equal("t", next.UrlParameters!["$skiptoken"]);
        Assert.ThrowsAny<ArgumentException>(() => new GraphRequest("/users").ForNextLink(" "));
    }

    // ---- temp directory ----

    [Fact]
    public void SecureTempDirectory_IsCreatedOnDemand_WithOwnerOnlyAccess_AndIdempotently()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "secure-temp-" + Guid.NewGuid().ToString("N")[..8], "nested");
        var directory = new SecureTempDirectory(root);
        try
        {
            Assert.False(Directory.Exists(root));

            directory.EnsureCreated();
            directory.EnsureCreated();

            Assert.True(Directory.Exists(root));
            Assert.Equal(Path.Combine(Path.GetFullPath(root), "x.json"), directory.FilePath("x.json"));
            Assert.ThrowsAny<ArgumentException>(() => directory.FilePath(" "));
            Assert.ThrowsAny<ArgumentException>(() => new SecureTempDirectory(string.Empty));
            AssertOwnerOnly(root);
        }
        finally
        {
            try
            {
                Directory.Delete(Path.GetDirectoryName(root)!, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static void AssertOwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            AssertWindowsOwnerOnly(path);
        }
        else
        {
            AssertUnixOwnerOnly(path);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AssertWindowsOwnerOnly(string path)
    {
        var security = new DirectoryInfo(path).GetAccessControl();
        using var identity = WindowsIdentity.GetCurrent();
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        Assert.True(security.AreAccessRulesProtected);
        var rule = Assert.Single(rules);
        Assert.Equal(identity.User, rule.IdentityReference);
        Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights & FileSystemRights.FullControl);
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void AssertUnixOwnerOnly(string path)
    {
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(path));
    }

    /// <summary>A Graph client that answers every GET with a function result (or throws what the function throws).</summary>
    private sealed class StubGraph(Func<GraphRequest, JsonNode?> get) : IGraphClient
    {
        public Task<JsonNode?> GetAsync(GraphRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult(get(request));
        }

        public Task<GraphResponse> PostAsync(GraphRequest request, JsonNode? body, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<GraphResponse> PatchAsync(GraphRequest request, JsonNode body, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<GraphResponse> PutAsync(GraphRequest request, JsonNode body, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task DeleteAsync(GraphRequest request, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public IAsyncEnumerable<JsonNode> GetAllPagesAsync(GraphRequest request, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<BatchResponse>> BatchAsync(IReadOnlyList<BatchRequest> requests, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }
}
