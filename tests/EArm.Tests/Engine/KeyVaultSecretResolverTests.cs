using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Core.Secrets;
using EArm.Engine;
using EArm.Engine.KeyVault;
using EArm.Graph;
using EArm.Tests.Cli;
using EArm.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EArm.Tests.Engine;

/// <summary>Key Vault parameter references resolved over HTTP through <see cref="KeyVaultClient"/> against a scripted handler.</summary>
public sealed class KeyVaultSecretResolverTests
{
    private const string VaultId = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg/providers/Microsoft.KeyVault/vaults/contoso-kv";
    private const string Version = "abc123def4567890abc123def4567890";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Harness : IDisposable
    {
        private readonly HttpClient _http;

        public Harness(GraphCloud? cloud = null)
        {
            Handler = new ScriptedGraphHandler();
            Tokens = new FakeJwtTokenProvider();
            _http = new HttpClient(Handler, disposeHandler: false);
            var client = new KeyVaultClient(_http, Tokens, new RetryPolicy(new RetryOptions(), new NoDelay(), NullLogger<RetryPolicy>.Instance), cloud ?? GraphCloud.Public);
            Resolver = new KeyVaultSecretResolver(client);
        }

        public ScriptedGraphHandler Handler { get; }

        public FakeJwtTokenProvider Tokens { get; }

        public KeyVaultSecretResolver Resolver { get; }

        public Harness WithSecret(string vault, string secret, string value, string? version = null)
        {
            string path = "/secrets/" + secret + (version is null ? string.Empty : "/" + version);
            Handler.SetScript("{ \"responses\": [ { \"match\": { \"method\": \"GET\", \"urlContains\": \"https://" + vault + ".vault.azure.net" + path + "?\" }, \"status\": 200, \"body\": { \"value\": " + System.Text.Json.JsonSerializer.Serialize(value) + " } } ] }");
            return this;
        }

        public void Dispose()
        {
            _http.Dispose();
        }
    }

    private static ParameterSet Reference(string parameter, string vaultId, string secretName, string? version = null)
    {
        string versionJson = version is null ? string.Empty : ", \"secretVersion\": \"" + version + "\"";
        return ParameterSet.Parse("{ \"parameters\": { \"" + parameter + "\": { \"reference\": { \"keyVault\": { \"id\": \"" + vaultId + "\" }, \"secretName\": \"" + secretName + "\"" + versionJson + " } } } }");
    }

    [Fact]
    public async Task ResolveAsync_BindsTheSecretTainted_AndLeavesTheOriginalDocumentAlone()
    {
        using var harness = new Harness().WithSecret("contoso-kv", "bg-initial", "s3cret-value");
        var document = await Templates.FixtureAsync(env: "dev", password: null, parameters: Reference("initialPassword", VaultId, "bg-initial"));
        Assert.True(KeyVaultSecretResolver.HasPendingReferences(document));

        var resolved = await harness.Resolver.ResolveAsync(document, Ct);

        Assert.False(KeyVaultSecretResolver.HasPendingReferences(resolved));
        var bound = resolved.ParameterValues["initialPassword"];
        Assert.Null(bound.PendingReference);
        Assert.True(SecretJson.IsSecret(bound.Value, out var secret));
        Assert.Equal("s3cret-value", secret.Value);
        Assert.Equal("\"***\"", bound.Value!.ToJsonString());
        Assert.NotNull(document.ParameterValues["initialPassword"].PendingReference);
        Assert.Equal(document.ParameterValues["env"].Value?.ToJsonString(), resolved.ParameterValues["env"].Value?.ToJsonString());
        Assert.Same(document.Root, resolved.Root);
    }

    [Fact]
    public async Task ResolveAsync_IssuesOneGet_WithTheVaultNameFromTheId_AndAKeyVaultToken()
    {
        using var harness = new Harness().WithSecret("contoso-kv", "bg-initial", "x");
        var document = await Templates.FixtureAsync(env: "dev", password: null, parameters: Reference("initialPassword", VaultId, "bg-initial"));

        await harness.Resolver.ResolveAsync(document, Ct);

        var request = Assert.Single(harness.Handler.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal("https://contoso-kv.vault.azure.net/secrets/bg-initial?api-version=7.4", request.Uri.AbsoluteUri);
        Assert.Null(request.Body);
        Assert.StartsWith("Bearer ", request.Headers["Authorization"], StringComparison.Ordinal);
        Assert.Equal(["https://vault.azure.net/.default"], harness.Tokens.RequestedScopes);
    }

    [Theory]
    [InlineData("contoso-kv", "contoso-kv")]
    [InlineData("/subscriptions/s/resourceGroups/rg/providers/Microsoft.KeyVault/vaults/Other-KV/", "other-kv")]
    public async Task VaultName_IsTheLastSegmentOrTheBareName_AsALowerCaseHost(string vaultId, string expectedHost)
    {
        using var harness = new Harness().WithSecret(expectedHost, "bg-initial", "x");
        var document = await Templates.FixtureAsync(env: "dev", password: null, parameters: Reference("initialPassword", vaultId, "bg-initial"));

        await harness.Resolver.ResolveAsync(document, Ct);

        Assert.Equal(expectedHost + ".vault.azure.net", Assert.Single(harness.Handler.Requests).Uri.Host);
    }

    [Fact]
    public async Task SecretVersion_IsPartOfThePath()
    {
        using var harness = new Harness().WithSecret("contoso-kv", "bg-initial", "pinned", Version);
        var reference = KeyVaultReference.Create(VaultId, "bg-initial", Version);

        string value = await harness.Resolver.GetSecretAsync("initialPassword", reference, Ct);

        Assert.Equal("pinned", value);
        Assert.Equal("/secrets/bg-initial/" + Version, Assert.Single(harness.Handler.Requests).Uri.AbsolutePath);
        Assert.Equal("keyVault 'contoso-kv' secret 'bg-initial' version '" + Version + "'", reference.ToString());
    }

    [Fact]
    public async Task SovereignCloudsUseTheirVaultSuffixAndScope()
    {
        using var harness = new Harness(GraphCloud.UsGov);
        harness.Handler.SetScript("{ \"responses\": [ { \"status\": 200, \"body\": { \"value\": \"gov\" } } ] }");

        string value = await harness.Resolver.GetSecretAsync("p", KeyVaultReference.Create("contoso-kv", "s", null), Ct);

        Assert.Equal("gov", value);
        Assert.Equal("contoso-kv.vault.usgovcloudapi.net", Assert.Single(harness.Handler.Requests).Uri.Host);
        Assert.Equal(["https://vault.usgovcloudapi.net/.default"], harness.Tokens.RequestedScopes);
    }

    [Theory]
    [InlineData(404, "SecretNotFound", "the vault has no such secret")]
    [InlineData(403, "Forbidden", "access denied")]
    [InlineData(400, "BadParameter", "Key Vault answered 400")]
    public async Task ErrorStatuses_AreKeyVaultResolutionExceptions_WithAnActionableDetail(int status, string code, string expectedDetail)
    {
        using var harness = new Harness();
        harness.Handler.SetScript("{ \"responses\": [ { \"status\": " + status + ", \"body\": { \"error\": { \"code\": \"" + code + "\", \"message\": \"A secret with (name/id) bg-initial was not found.\" } } } ] }");
        var document = await Templates.FixtureAsync(env: "dev", password: null, parameters: Reference("initialPassword", VaultId, "bg-initial"));

        var error = await Assert.ThrowsAsync<KeyVaultResolutionException>(() => harness.Resolver.ResolveAsync(document, Ct));

        Assert.Equal(EngineDiagnosticCodes.KeyVaultReferenceUnresolved, error.Code);
        Assert.Equal("initialPassword", error.ParameterName);
        Assert.Equal("bg-initial", error.Reference.SecretName);
        Assert.Equal("contoso-kv", error.Reference.VaultName);
        Assert.Contains(expectedDetail, error.Detail, StringComparison.Ordinal);
        Assert.Contains(code, error.Detail, StringComparison.Ordinal);
        Assert.Contains("Parameter 'initialPassword' could not be resolved from keyVault 'contoso-kv' secret 'bg-initial'", error.Message, StringComparison.Ordinal);
        Assert.IsType<GraphException>(error.InnerException);
    }

    [Fact]
    public async Task ErrorMessages_AreRedactedBeforeTheyReachTheDetail()
    {
        using var harness = new Harness();
        harness.Handler.SetScript("{ \"responses\": [ { \"status\": 403, \"body\": { \"error\": { \"code\": \"Forbidden\", \"message\": \"forbidden; client_secret=hunter2 Bearer eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiIxIn0.c2lnbmF0dXJlLXBhcnQ\" } } } ] }");

        var error = await Assert.ThrowsAsync<KeyVaultResolutionException>(() => harness.Resolver.GetSecretAsync("p", KeyVaultReference.Create("kv-vault", "s", null), Ct));

        Assert.Contains("forbidden", error.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJhbGciOiJSUzI1NiJ9", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASignInFailure_IsWrapped_WithTheGuidance()
    {
        using var harness = new Harness();
        harness.Tokens.Failure = FakeJwtTokenProvider.NotSignedIn().Failure;

        var error = await Assert.ThrowsAsync<KeyVaultResolutionException>(() => harness.Resolver.GetSecretAsync("p", KeyVaultReference.Create("kv-vault", "s", null), Ct));

        Assert.IsType<GraphTransportException>(error.InnerException);
        Assert.Contains("Not signed in", error.Detail, StringComparison.Ordinal);
        Assert.Empty(harness.Handler.Requests);
    }

    [Theory]
    [InlineData("evil.example/x#", "orders-secret", "not a valid Key Vault name")]
    [InlineData("kv@evil.example", "orders-secret", "not a valid Key Vault name")]
    [InlineData("ab", "orders-secret", "not a valid Key Vault name")]
    [InlineData("contoso-kv", "../../x", "not a valid Key Vault secret name")]
    [InlineData("contoso-kv", "a.b", "not a valid Key Vault secret name")]
    [InlineData("contoso-kv", "a/b", "not a valid Key Vault secret name")]
    public async Task NamesThatBreakTheKeyVaultRules_NeverBuildAUrlOrReachTheNetwork(string vaultId, string secretName, string expectedFragment)
    {
        using var harness = new Harness();

        var error = await Assert.ThrowsAsync<KeyVaultResolutionException>(() => harness.Resolver.GetSecretAsync("p", KeyVaultReference.Create(vaultId, secretName, null), Ct));

        Assert.Contains(expectedFragment, error.Detail, StringComparison.Ordinal);
        Assert.Empty(harness.Handler.Requests);
        Assert.Empty(harness.Tokens.RequestedScopes);
    }

    [Fact]
    public async Task ABadVersion_NeverReachesTheNetwork()
    {
        using var harness = new Harness();

        var error = await Assert.ThrowsAsync<KeyVaultResolutionException>(() => harness.Resolver.GetSecretAsync("p", KeyVaultReference.Create("contoso-kv", "s", "v2/../x"), Ct));

        Assert.Contains("not a valid Key Vault secret version", error.Detail, StringComparison.Ordinal);
        Assert.Empty(harness.Handler.Requests);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{ \"value\": null }")]
    [InlineData("{ \"value\": 5 }")]
    [InlineData("[ \"x\" ]")]
    public async Task AnAnswerWithoutAStringValue_IsRejected(string body)
    {
        using var harness = new Harness();
        harness.Handler.SetScript("{ \"responses\": [ { \"status\": 200, \"body\": " + body + " } ] }");

        var error = await Assert.ThrowsAsync<KeyVaultResolutionException>(() => harness.Resolver.GetSecretAsync("p", KeyVaultReference.Create("kv-vault", "s", null), Ct));

        Assert.Contains("without a secret value", error.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("with \\\"quotes\\\" and \\u00fc", "with \"quotes\" and ü")]
    [InlineData("  spaced  ", "  spaced  ")]
    [InlineData("multi\\nline", "multi\nline")]
    public async Task TheValueIsTakenExactly_WithoutTrimming(string jsonEscaped, string expected)
    {
        using var harness = new Harness();
        harness.Handler.SetScript("{ \"responses\": [ { \"status\": 200, \"body\": { \"value\": \"" + jsonEscaped + "\" } } ] }");

        string value = await harness.Resolver.GetSecretAsync("p", KeyVaultReference.Create("kv-vault", "s", null), Ct);

        Assert.Equal(expected, value);
    }

    [Fact]
    public async Task NonSecureParameters_AreStillTainted()
    {
        using var harness = new Harness().WithSecret("kv-vault", "env-name", "dev");
        var document = new EArm.Core.Loading.TemplateLoader().Parse(
            "{ \"$schema\": \"" + Templates.SchemaUri + "\", \"contentVersion\": \"1.0.0.0\", \"parameters\": { \"env\": { \"type\": \"string\" } }, \"resources\": [] }",
            "inline.entra.json",
            Reference("env", "kv-vault", "env-name"));

        var resolved = await harness.Resolver.ResolveAsync(document, Ct);

        Assert.True(SecretJson.IsSecret(resolved.ParameterValues["env"].Value, out var secret));
        Assert.Equal("dev", secret.Value);
        Assert.False(resolved.ParameterValues["env"].IsSecure, "the declared type stays string; the value is tainted regardless");
    }

    [Fact]
    public async Task DocumentsWithoutReferences_AreReturnedAsIs_WithoutAnyRequest()
    {
        using var harness = new Harness();
        var document = await Templates.FixtureAsync();

        var resolved = await harness.Resolver.ResolveAsync(document, Ct);

        Assert.Same(document, resolved);
        Assert.Empty(harness.Handler.Requests);
        Assert.Empty(harness.Tokens.RequestedScopes);
    }

    [Fact]
    public async Task EveryPendingReference_IsResolved_InParameterNameOrder()
    {
        using var harness = new Harness();
        harness.Handler.SetScript("{ \"responses\": [ { \"match\": { \"urlContains\": \"/secrets/s-a?\" }, \"status\": 200, \"body\": { \"value\": \"A\" } }, { \"match\": { \"urlContains\": \"/secrets/s-b?\" }, \"status\": 200, \"body\": { \"value\": \"B\" } } ] }");
        var parameters = ParameterSet.Parse("{ \"parameters\": {"
            + " \"zeta\": { \"reference\": { \"keyVault\": { \"id\": \"kv-vault\" }, \"secretName\": \"s-b\" } },"
            + " \"alpha\": { \"reference\": { \"keyVault\": { \"id\": \"kv-vault\" }, \"secretName\": \"s-a\" } } } }");
        var document = new EArm.Core.Loading.TemplateLoader().Parse(
            "{ \"$schema\": \"" + Templates.SchemaUri + "\", \"contentVersion\": \"1.0.0.0\", \"parameters\": { \"alpha\": { \"type\": \"securestring\" }, \"zeta\": { \"type\": \"securestring\" } }, \"resources\": [] }",
            "inline.entra.json",
            parameters);

        var resolved = await harness.Resolver.ResolveAsync(document, Ct);

        Assert.Equal(["/secrets/s-a", "/secrets/s-b"], harness.Handler.Requests.Select(static r => r.Uri.AbsolutePath));
        Assert.True(SecretJson.IsSecret(resolved.ParameterValues["alpha"].Value, out var alpha));
        Assert.Equal("A", alpha.Value);
        Assert.True(SecretJson.IsSecret(resolved.ParameterValues["zeta"].Value, out var zeta));
        Assert.Equal("B", zeta.Value);
    }
}
