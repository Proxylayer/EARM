using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;
using EArm.Graph.Auth;
using Xunit;

namespace EArm.Graph.Tests;

/// <summary>
/// Review finding Q-15, the default chain half: the chain holds exactly the environment, workload identity and managed identity credentials, its options
/// carry the cloud's authority and the tenant / client ids, and <c>AZURE_TOKEN_CREDENTIALS</c> cannot enable a credential that starts a process (split
/// from <see cref="Review1B_AuthFactoryTests"/>).
/// </summary>
[Collection(Review1BEnvironmentCollection.Name)]
public sealed class Review1B_DefaultChainTests
{
    private static readonly string[] AllowedChain = ["EnvironmentCredential", "WorkloadIdentityCredential", "ManagedIdentityCredential"];

    private static string NewRecordPath()
    {
        return Path.Combine(Path.GetTempPath(), "earm-r1b-auth-" + Guid.NewGuid().ToString("N"), "auth", "record.json");
    }

    // ---- the default chain ---------------------------------------------------------------------------------------

    private static string[] ChainSources(TokenCredential chain)
    {
        var field = typeof(DefaultAzureCredential).GetField("_sources", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return [.. ((TokenCredential[])field.GetValue(chain)!).Select(static c => c.GetType().Name)];
    }

    [Fact]
    public void TheDefaultChainContainsExactlyEnvironmentWorkloadIdentityAndManagedIdentity()
    {
        using var env = new EnvironmentScope().Set("AZURE_TOKEN_CREDENTIALS", null);
        var factory = new GraphAuthFactory(new AuthRecordStore(NewRecordPath()));

        var sources = ChainSources(factory.CreateDefaultChain(new GraphAuthOptions()));

        Assert.Equal(AllowedChain, sources);
        foreach (var forbidden in new[] { "AzureCliCredential", "AzureDeveloperCliCredential", "AzurePowerShellCredential", "VisualStudioCredential", "VisualStudioCodeCredential", "BrokerCredential", "SharedTokenCacheCredential", "InteractiveBrowserCredential" })
        {
            Assert.DoesNotContain(forbidden, sources);
        }
    }

    [Fact]
    public void EveryExcludeSwitchOfTheChainOptionsIsSetExceptForTheThreeAllowedCredentials()
    {
        var options = GraphAuthFactory.BuildDefaultChainOptions(new GraphAuthOptions());
        var switches = typeof(DefaultAzureCredentialOptions).GetProperties()
            .Where(static p => p.PropertyType == typeof(bool) && p.Name.StartsWith("Exclude", StringComparison.Ordinal) && p.Name.EndsWith("Credential", StringComparison.Ordinal))
            .ToList();

        Assert.True(switches.Count >= 10, "Azure.Identity offers the Exclude* switches this test is guarding");
        foreach (var property in switches)
        {
            var allowed = AllowedChain.Select(static n => "Exclude" + n).Contains(property.Name);
            Assert.Equal(!allowed, (bool)property.GetValue(options)!);
        }
    }

    [Theory]
    [InlineData("public", "https://login.microsoftonline.com/")]
    [InlineData("usgov", "https://login.microsoftonline.us/")]
    [InlineData("usgovdod", "https://login.microsoftonline.us/")]
    [InlineData("china", "https://login.chinacloudapi.cn/")]
    public void TheAuthorityHostComesFromTheCloudTableAndNotFromTheEnvironment(string cloudKey, string expectedAuthority)
    {
        using var env = new EnvironmentScope().Set("AZURE_AUTHORITY_HOST", "https://evil.example/");
        var cloud = GraphCloud.Parse(cloudKey);
        // china has no default sign-in app: a new sign-in there needs --client-id.
        var auth = new GraphAuthOptions { Cloud = cloud, ClientId = cloud == GraphCloud.China ? "44444444-4444-4444-4444-444444444444" : null };

        Assert.Equal(expectedAuthority, GraphAuthFactory.BuildDefaultChainOptions(auth).AuthorityHost.AbsoluteUri);
        Assert.Equal(expectedAuthority, GraphAuthFactory.BuildInteractiveOptions(auth, AuthTests.Record()).AuthorityHost.AbsoluteUri);
        Assert.Equal(expectedAuthority, GraphAuthFactory.BuildBrowserLoginOptions(auth).AuthorityHost.AbsoluteUri);
        Assert.Equal(expectedAuthority, GraphAuthFactory.BuildDeviceCodeLoginOptions(auth, null).AuthorityHost.AbsoluteUri);
    }

    [Fact]
    public void TenantAndClientIdArePlumbedIntoEveryCredentialOptionsObject()
    {
        var auth = new GraphAuthOptions { TenantId = "contoso.example", ClientId = "44444444-4444-4444-4444-444444444444" };
        var record = AuthTests.Record();

        var chain = GraphAuthFactory.BuildDefaultChainOptions(auth);
        var interactive = GraphAuthFactory.BuildInteractiveOptions(auth, record);
        var browser = GraphAuthFactory.BuildBrowserLoginOptions(auth);
        var device = GraphAuthFactory.BuildDeviceCodeLoginOptions(auth, null);

        Assert.Equal("contoso.example", chain.TenantId);
        Assert.Equal("contoso.example", interactive.TenantId);
        Assert.Equal("contoso.example", browser.TenantId);
        Assert.Equal("contoso.example", device.TenantId);
        Assert.Equal("44444444-4444-4444-4444-444444444444", interactive.ClientId);
        Assert.Equal("44444444-4444-4444-4444-444444444444", browser.ClientId);
        Assert.Equal("44444444-4444-4444-4444-444444444444", device.ClientId);
    }

    [Fact]
    public void WithoutTenantOrClientIdTheCredentialsKeepTheirOwnDefaults()
    {
        var auth = new GraphAuthOptions();
        var record = AuthTests.Record();

        Assert.Null(GraphAuthFactory.BuildDefaultChainOptions(auth).TenantId);
        // Review 3I (intentional change): the interactive credential defaults client and tenant to the ones the stored sign-in record names.
        Assert.Equal(record.TenantId, GraphAuthFactory.BuildInteractiveOptions(auth, record).TenantId);
        Assert.Equal(record.ClientId, GraphAuthFactory.BuildInteractiveOptions(auth, record).ClientId);
        Assert.Null(GraphAuthFactory.BuildBrowserLoginOptions(auth).TenantId);
        Assert.Null(GraphAuthFactory.BuildDeviceCodeLoginOptions(auth, null).TenantId);
    }

    [Fact]
    public void TheCachedSignInNeverOpensABrowserAndUsesTheEncryptedEarmCache()
    {
        var record = AuthTests.Record();
        var interactive = GraphAuthFactory.BuildInteractiveOptions(new GraphAuthOptions(), record);

        Assert.True(interactive.DisableAutomaticAuthentication);
        Assert.Same(record, interactive.AuthenticationRecord);
        Assert.Equal(GraphAuthFactory.CacheName, interactive.TokenCachePersistenceOptions!.Name);
        Assert.False(interactive.TokenCachePersistenceOptions.UnsafeAllowUnencryptedStorage);

        var browser = GraphAuthFactory.BuildBrowserLoginOptions(new GraphAuthOptions());
        Assert.Equal(GraphAuthFactory.CacheName, browser.TokenCachePersistenceOptions!.Name);
        Assert.False(browser.TokenCachePersistenceOptions.UnsafeAllowUnencryptedStorage);
        Assert.False(browser.DisableAutomaticAuthentication);

        var device = GraphAuthFactory.BuildDeviceCodeLoginOptions(new GraphAuthOptions(), null);
        Assert.Equal(GraphAuthFactory.CacheName, device.TokenCachePersistenceOptions!.Name);
        Assert.False(device.TokenCachePersistenceOptions.UnsafeAllowUnencryptedStorage);
    }

    [Fact]
    public void TheDeviceCodeCallbackIsPassedThroughOrDefaultsToTheConsole()
    {
        Func<DeviceCodeInfo, CancellationToken, Task> show = static (_, _) => Task.CompletedTask;

        Assert.Same(show, GraphAuthFactory.BuildDeviceCodeLoginOptions(new GraphAuthOptions(), show).DeviceCodeCallback);
        Assert.NotNull(GraphAuthFactory.BuildDeviceCodeLoginOptions(new GraphAuthOptions(), null).DeviceCodeCallback);
    }
    // ---- AZURE_TOKEN_CREDENTIALS allow-list ----------------------------------------------------------------------

    [Theory]
    [InlineData("prod")]
    [InlineData("PROD")]
    [InlineData(" prod ")]
    [InlineData("EnvironmentCredential")]
    [InlineData("workloadidentitycredential")]
    [InlineData("ManagedIdentityCredential")]
    [InlineData("")]
    public void ASafeTokenCredentialsValueIsAccepted(string value)
    {
        using var env = new EnvironmentScope().Set("AZURE_TOKEN_CREDENTIALS", value);
        var factory = new GraphAuthFactory(new AuthRecordStore(NewRecordPath()));

        var chain = factory.CreateDefaultChain(new GraphAuthOptions());

        Assert.IsType<DefaultAzureCredential>(chain);
    }

    [Fact]
    public void ABlankTokenCredentialsValueIsRefusedWithAClearMessage()
    {
        using var env = new EnvironmentScope().Set("AZURE_TOKEN_CREDENTIALS", "   ");
        var factory = new GraphAuthFactory(new AuthRecordStore(NewRecordPath()));

        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateDefaultChain(new GraphAuthOptions()));

        Assert.Contains("blank value", error.Message, StringComparison.Ordinal);
        Assert.Contains("Unset it", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dev")]
    [InlineData("DEV")]
    [InlineData("AzureCliCredential")]
    [InlineData("AzureDeveloperCliCredential")]
    [InlineData("AzurePowerShellCredential")]
    [InlineData("VisualStudioCredential")]
    [InlineData("VisualStudioCodeCredential")]
    [InlineData("BrokerCredential")]
    [InlineData("InteractiveBrowserCredential")]
    [InlineData("SharedTokenCacheCredential")]
    [InlineData("EnvironmentCredential,AzureCliCredential")]
    [InlineData("prod,dev")]
    [InlineData("0")]
    [InlineData("true")]
    public void AnythingOutsideTheAllowListIsRefusedWithTheOffendingValueInTheMessage(string value)
    {
        using var env = new EnvironmentScope().Set("AZURE_TOKEN_CREDENTIALS", value);
        var factory = new GraphAuthFactory(new AuthRecordStore(NewRecordPath()));

        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateDefaultChain(new GraphAuthOptions()));

        Assert.Contains("AZURE_TOKEN_CREDENTIALS", error.Message, StringComparison.Ordinal);
        Assert.Contains("'" + value + "'", error.Message, StringComparison.Ordinal);
        Assert.Contains("external process", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGuardAppliesToEveryModeThatBuildsTheDefaultChain()
    {
        using var env = new EnvironmentScope().Set("AZURE_TOKEN_CREDENTIALS", "dev");
        var factory = new GraphAuthFactory(new AuthRecordStore(NewRecordPath()));

        Assert.Throws<InvalidOperationException>(() => factory.Create(new GraphAuthOptions { Mode = GraphAuthMode.Default }));
        Assert.Throws<InvalidOperationException>(() => factory.Create(new GraphAuthOptions { Mode = GraphAuthMode.Auto }));
        // Interactive mode never builds the chain, so the variable is irrelevant there.
        Assert.NotNull(factory.Create(new GraphAuthOptions { Mode = GraphAuthMode.Interactive }));
    }
}
