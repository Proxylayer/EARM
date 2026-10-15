using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Engine;
using EArm.Engine.KeyVault;
using EArm.Graph;
using EArm.Tests.Cli;
using EArm.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EArm.Tests.Engine;

/// <summary>
/// Key Vault references, review round 4: a resolved secret is checked against the constraints of the parameter it binds (never echoed), and
/// <c>EARM_KEYVAULT_ALLOWED</c> restricts which vaults a parameter file may read, before any token is requested.
/// </summary>
public sealed class Review4K_KeyVaultTests
{
    private const string VaultId = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg/providers/Microsoft.KeyVault/vaults/contoso-kv";
    private const string OtherVaultId = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg/providers/Microsoft.KeyVault/vaults/other-kv";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Harness : IDisposable
    {
        private readonly HttpClient _http;

        public Harness(string[]? allowed = null)
        {
            Handler = new ScriptedGraphHandler();
            Tokens = new FakeJwtTokenProvider();
            _http = new HttpClient(Handler, disposeHandler: false);
            var client = new KeyVaultClient(_http, Tokens, new RetryPolicy(new RetryOptions(), new NoDelay(), NullLogger<RetryPolicy>.Instance), GraphCloud.Public) { AllowedVaults = allowed };
            Resolver = new KeyVaultSecretResolver(client);
        }

        public ScriptedGraphHandler Handler { get; }

        public FakeJwtTokenProvider Tokens { get; }

        public KeyVaultSecretResolver Resolver { get; }

        public Harness WithSecret(string vault, string secret, string value)
        {
            Handler.SetScript("{ \"responses\": [ { \"match\": { \"method\": \"GET\", \"urlContains\": \"https://" + vault + ".vault.azure.net/secrets/" + secret + "?\" }, \"status\": 200, \"body\": { \"value\": " + System.Text.Json.JsonSerializer.Serialize(value) + " } } ] }");
            return this;
        }

        public void Dispose()
        {
            _http.Dispose();
        }
    }

    private static TemplateDocument Document(string parameterDefinition, string vaultId = VaultId, string secretName = "the-secret")
    {
        var parameters = ParameterSet.Parse("{ \"parameters\": { \"pw\": { \"reference\": { \"keyVault\": { \"id\": \"" + vaultId + "\" }, \"secretName\": \"" + secretName + "\" } } } }");
        return Expr.Parse(Expr.Envelope("\"parameters\": { \"pw\": " + parameterDefinition + " }, \"resources\": []"), parameters);
    }

    // ---- constraints of the parameter apply to the resolved secret ----

    [Fact]
    public async Task AResolvedSecret_ThatSatisfiesTheParametersConstraints_IsBound()
    {
        using var harness = new Harness().WithSecret("contoso-kv", "the-secret", "alpha-value-1");
        var document = Document("{ \"type\": \"securestring\", \"minLength\": 8, \"maxLength\": 16 }");

        var resolved = await harness.Resolver.ResolveAsync(document, Ct);

        Assert.False(KeyVaultSecretResolver.HasPendingReferences(resolved));
    }

    [Theory]
    [InlineData("{ \"type\": \"securestring\", \"minLength\": 20 }", "minLength")]
    [InlineData("{ \"type\": \"securestring\", \"maxLength\": 4 }", "maxLength")]
    [InlineData("{ \"type\": \"securestring\", \"allowedValues\": [\"beta-value-1\", \"gamma-value-1\"] }", "allowedValues")]
    public async Task AResolvedSecret_ThatBreaksAConstraint_IsRefused_WithoutEchoingTheValueOrTheAllowedOnes(string definition, string constraint)
    {
        using var harness = new Harness().WithSecret("contoso-kv", "the-secret", "alpha-value-1");
        var document = Document(definition);

        var error = await Assert.ThrowsAsync<KeyVaultResolutionException>(() => harness.Resolver.ResolveAsync(document, Ct));

        Assert.Equal(EngineDiagnosticCodes.KeyVaultReferenceUnresolved, error.Code);
        Assert.Equal("pw", error.ParameterName);
        Assert.Contains(constraint, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("alpha-value-1", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("alpha-value-1", error.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("beta-value-1", error.Message, StringComparison.Ordinal);
    }

    // ---- EARM_KEYVAULT_ALLOWED ----

    [Fact]
    public async Task AVaultThatIsNotAllowed_IsRefusedBeforeAnyTokenOrRequest()
    {
        using var harness = new Harness(["contoso-kv"]).WithSecret("other-kv", "the-secret", "alpha-value-1");
        var document = Document("{ \"type\": \"securestring\" }", OtherVaultId);

        var error = await Assert.ThrowsAsync<KeyVaultResolutionException>(() => harness.Resolver.ResolveAsync(document, Ct));

        Assert.Contains("EARM_KEYVAULT_ALLOWED", error.Message, StringComparison.Ordinal);
        Assert.Contains("contoso-kv", error.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Tokens.RequestedScopes);
        Assert.Empty(harness.Handler.Commands);
    }

    [Theory]
    [InlineData("contoso-kv")]
    [InlineData("CONTOSO-KV")]
    [InlineData("other-kv,contoso-kv")]
    public async Task AnAllowedVault_IsReadWhateverTheCaseOfTheList(string allowedList)
    {
        using var harness = new Harness([.. KeyVaultClient.ParseAllowedVaults(allowedList)!]).WithSecret("contoso-kv", "the-secret", "alpha-value-1");
        var document = Document("{ \"type\": \"securestring\" }");

        var resolved = await harness.Resolver.ResolveAsync(document, Ct);

        Assert.False(KeyVaultSecretResolver.HasPendingReferences(resolved));
        Assert.Single(harness.Handler.Commands);
    }

    [Fact]
    public async Task AnEmptyAllowList_AllowsNoVault()
    {
        using var harness = new Harness([.. KeyVaultClient.ParseAllowedVaults(",")!]).WithSecret("contoso-kv", "the-secret", "alpha-value-1");

        var error = await Assert.ThrowsAsync<KeyVaultResolutionException>(() => harness.Resolver.ResolveAsync(Document("{ \"type\": \"securestring\" }"), Ct));

        Assert.Contains("no vault", error.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Handler.Commands);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankSetting_MeansNoRestriction(string? setting)
    {
        Assert.Null(KeyVaultClient.ParseAllowedVaults(setting));
    }

    [Fact]
    public void TheList_IsSplitOnCommas_TrimmedAndStripped_OfEmptyNames()
    {
        Assert.Equal(["a-vault", "B-Vault", "c-vault"], KeyVaultClient.ParseAllowedVaults(" a-vault, B-Vault ,, c-vault,"));
        Assert.Empty(KeyVaultClient.ParseAllowedVaults(",")!);
        Assert.Equal("EARM_KEYVAULT_ALLOWED", KeyVaultClient.AllowedVaultsVariable);
    }
}
