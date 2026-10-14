using System;
using System.Threading;
using System.Threading.Tasks;
using EntraArm.Az;
using EntraArm.Core;
using EntraArm.Core.Secrets;
using EntraArm.Engine;
using EntraArm.Engine.KeyVault;
using EntraArm.TestSupport;
using Xunit;

namespace EntraArm.Tests.Engine;

/// <summary>Key Vault parameter references (plan section 1.6) resolved through <see cref="FakeAzRunner"/>.</summary>
public sealed class KeyVaultSecretResolverTests
{
    private const string VaultId = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg/providers/Microsoft.KeyVault/vaults/contoso-kv";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ParameterSet Reference(string parameter, string vaultId, string secretName, string? version = null)
    {
        string versionJson = version is null ? string.Empty : ", \"secretVersion\": \"" + version + "\"";
        return ParameterSet.Parse("{ \"parameters\": { \"" + parameter + "\": { \"reference\": { \"keyVault\": { \"id\": \"" + vaultId + "\" }, \"secretName\": \"" + secretName + "\"" + versionJson + " } } } }");
    }

    private sealed class ThrowingRunner : IAzRunner
    {
        public Task<AzResult> RunAsync(AzRequest request, CancellationToken cancellationToken)
        {
            throw new ToolchainException("pwsh", "pwsh was not found on PATH.");
        }
    }

    [Fact]
    public async Task ResolveAsync_BindsTheSecretTainted_AndLeavesTheOriginalDocumentAlone()
    {
        var az = new FakeAzRunner().WithSecret("contoso-kv", "bg-initial", "s3cret-value");
        var document = await Templates.FixtureAsync(env: "dev", password: null, parameters: Reference("initialPassword", VaultId, "bg-initial"));
        Assert.True(KeyVaultSecretResolver.HasPendingReferences(document));

        var resolved = await new KeyVaultSecretResolver(az).ResolveAsync(document, Ct);

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
    public async Task ResolveAsync_IssuesAzKeyvaultSecretShow_WithTheVaultNameFromTheId()
    {
        var az = new FakeAzRunner().WithSecret("contoso-kv", "bg-initial", "x");
        var document = await Templates.FixtureAsync(env: "dev", password: null, parameters: Reference("initialPassword", VaultId, "bg-initial"));

        await new KeyVaultSecretResolver(az).ResolveAsync(document, Ct);

        var request = Assert.Single(az.Requests);
        Assert.Equal("keyvault secret show", request.CommandText);
        Assert.Equal(["keyvault", "secret", "show"], request.Command);
        Assert.False(request.IsRest);
        Assert.Null(request.Body);
        Assert.Equal(["--vault-name", "contoso-kv", "--name", "bg-initial", "--query", "value"], request.Arguments);
        Assert.Equal(["keyvault", "secret", "show", "--vault-name", "contoso-kv", "--name", "bg-initial", "--query", "value", "--only-show-errors", "-o", "json"], request.BuildAzArguments(null));
    }

    [Theory]
    [InlineData("contoso-kv", "contoso-kv")]
    [InlineData("/subscriptions/s/resourceGroups/rg/providers/Microsoft.KeyVault/vaults/Other-KV/", "Other-KV")]
    public async Task VaultName_IsTheLastSegmentOrTheBareName(string vaultId, string expected)
    {
        var az = new FakeAzRunner().WithSecret(expected, "bg-initial", "x");
        var document = await Templates.FixtureAsync(env: "dev", password: null, parameters: Reference("initialPassword", vaultId, "bg-initial"));

        await new KeyVaultSecretResolver(az).ResolveAsync(document, Ct);

        Assert.Equal(expected, Assert.Single(az.Requests).Arguments![1]);
    }

    [Fact]
    public async Task SecretVersion_IsPassedAsVersion()
    {
        var az = new FakeAzRunner().WithSecret("contoso-kv", "bg-initial", "pinned", "abc123");
        var reference = KeyVaultReference.Create(VaultId, "bg-initial", "abc123");

        string value = await new KeyVaultSecretResolver(az).GetSecretAsync("initialPassword", reference, Ct);

        Assert.Equal("pinned", value);
        Assert.Equal(["--vault-name", "contoso-kv", "--name", "bg-initial", "--version", "abc123", "--query", "value"], Assert.Single(az.Requests).Arguments);
        Assert.Equal("keyVault 'contoso-kv' secret 'bg-initial' version 'abc123'", reference.ToString());
    }

    [Fact]
    public async Task MissingSecret_IsKeyVaultResolutionException_WithARedactedDetail()
    {
        var az = new FakeAzRunner();
        var document = await Templates.FixtureAsync(env: "dev", password: null, parameters: Reference("initialPassword", VaultId, "bg-initial"));

        var error = await Assert.ThrowsAsync<KeyVaultResolutionException>(() => new KeyVaultSecretResolver(az).ResolveAsync(document, Ct));

        Assert.Equal(EngineDiagnosticCodes.KeyVaultReferenceUnresolved, error.Code);
        Assert.Equal("initialPassword", error.ParameterName);
        Assert.Equal("bg-initial", error.Reference.SecretName);
        Assert.Equal("contoso-kv", error.Reference.VaultName);
        Assert.Contains("SecretNotFound", error.Detail);
        Assert.Contains("Parameter 'initialPassword' could not be resolved from keyVault 'contoso-kv' secret 'bg-initial'", error.Message);
        Assert.IsType<AzCliException>(error.InnerException);
    }

    [Fact]
    public async Task AzErrorOutput_IsRedactedBeforeItReachesTheMessage()
    {
        var az = new FakeAzRunner().WithResponse("keyvault secret show", new AzResult(1, string.Empty, "ERROR: forbidden; client_secret=hunter2 Bearer eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiIxIn0.c2lnbmF0dXJlLXBhcnQ", TimeSpan.Zero, false));

        var error = await Assert.ThrowsAsync<KeyVaultResolutionException>(() => new KeyVaultSecretResolver(az).GetSecretAsync("p", KeyVaultReference.Create("kv", "s", null), Ct));

        Assert.Contains("forbidden", error.Detail);
        Assert.DoesNotContain("hunter2", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJhbGciOiJSUzI1NiJ9", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolchainFailure_IsWrapped()
    {
        var error = await Assert.ThrowsAsync<KeyVaultResolutionException>(() => new KeyVaultSecretResolver(new ThrowingRunner()).GetSecretAsync("p", KeyVaultReference.Create("kv", "s", null), Ct));

        Assert.IsType<ToolchainException>(error.InnerException);
        Assert.Contains("pwsh was not found", error.Detail);
    }

    [Theory]
    [InlineData("\"from-json\"\n", "from-json")]
    [InlineData("\"with \\\"quotes\\\" and \\u00fc\"", "with \"quotes\" and ü")]
    [InlineData("raw-tsv-value\r\n", "raw-tsv-value")]
    [InlineData("  spaced  ", "spaced")]
    public async Task Output_IsParsedAsJsonString_OrTakenRaw(string stdout, string expected)
    {
        var az = new FakeAzRunner().WithResponse("keyvault secret show", new AzResult(0, stdout, string.Empty, TimeSpan.Zero, false));

        string value = await new KeyVaultSecretResolver(az).GetSecretAsync("p", KeyVaultReference.Create("kv", "s", null), Ct);

        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("{ \"value\": \"x\" }", "JSON object")]
    [InlineData("[ \"x\" ]", "JSON object")]
    [InlineData("", "no value")]
    [InlineData("   \n", "no value")]
    public async Task UnexpectedOutput_IsRejected(string stdout, string expectedFragment)
    {
        var az = new FakeAzRunner().WithResponse("keyvault secret show", new AzResult(0, stdout, string.Empty, TimeSpan.Zero, false));

        var error = await Assert.ThrowsAsync<KeyVaultResolutionException>(() => new KeyVaultSecretResolver(az).GetSecretAsync("p", KeyVaultReference.Create("kv", "s", null), Ct));

        Assert.Contains(expectedFragment, error.Detail);
    }

    [Fact]
    public async Task NonSecureParameters_AreStillTainted()
    {
        var az = new FakeAzRunner().WithSecret("kv", "env-name", "dev");
        var document = new EntraArm.Core.Loading.TemplateLoader().Parse(
            "{ \"$schema\": \"" + Templates.SchemaUri + "\", \"contentVersion\": \"1.0.0.0\", \"parameters\": { \"env\": { \"type\": \"string\" } }, \"resources\": [] }",
            "inline.entra.json",
            Reference("env", "kv", "env-name"));

        var resolved = await new KeyVaultSecretResolver(az).ResolveAsync(document, Ct);

        Assert.True(SecretJson.IsSecret(resolved.ParameterValues["env"].Value, out var secret));
        Assert.Equal("dev", secret.Value);
        Assert.False(resolved.ParameterValues["env"].IsSecure, "the declared type stays string; the value is tainted regardless");
    }

    [Fact]
    public async Task DocumentsWithoutReferences_AreReturnedAsIs_WithoutCallingAz()
    {
        var az = new FakeAzRunner();
        var document = await Templates.FixtureAsync();

        var resolved = await new KeyVaultSecretResolver(az).ResolveAsync(document, Ct);

        Assert.Same(document, resolved);
        Assert.Empty(az.Requests);
    }
}
