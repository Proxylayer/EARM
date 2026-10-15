using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph.Auth;
using EArm.Graph.Tests.Infrastructure;
using Xunit;

namespace EArm.Graph.Tests;

/// <summary>Review finding Q-15: the sign-in record store (round trip, permissions, corruption) and the display-only <see cref="TokenClaims"/> decoder, edge by edge.</summary>
[Collection(Review1BEnvironmentCollection.Name)]
public sealed class Review1B_RecordStoreAndClaimsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewRecordPath()
    {
        return Path.Combine(Path.GetTempPath(), "earm-r1b-store-" + Guid.NewGuid().ToString("N"), "auth", "record.json");
    }

    private static void Cleanup(string recordPath)
    {
        var root = Path.GetDirectoryName(Path.GetDirectoryName(recordPath));
        if (root is not null && Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---- record store ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryFieldOfTheRecordRoundTrips()
    {
        var path = NewRecordPath();
        try
        {
            var store = new AuthRecordStore(path);
            await store.SaveAsync(AuthTests.Record(), Ct);

            var loaded = store.TryLoad()!;

            Assert.Equal("alice@contoso.example", loaded.Username);
            Assert.Equal("login.microsoftonline.com", loaded.Authority);
            Assert.Equal("oid.tid", loaded.HomeAccountId);
            Assert.Equal("11111111-1111-1111-1111-111111111111", loaded.TenantId);
            Assert.Equal("04b07795-8ddb-461a-bbee-02f9e1bf7b46", loaded.ClientId);
            Assert.Equal(path, store.FilePath);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task SavingAgainReplacesTheRecordAndLeavesNoTemporaryFile()
    {
        var path = NewRecordPath();
        try
        {
            var store = new AuthRecordStore(path);
            await store.SaveAsync(AuthTests.Record("login.microsoftonline.com"), Ct);
            await store.SaveAsync(AuthTests.Record("login.microsoftonline.us"), Ct);

            Assert.Equal("login.microsoftonline.us", store.TryLoad()!.Authority);
            Assert.Equal(["record.json"], Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task TheRecordFileIsOwnerOnly()
    {
        var path = NewRecordPath();
        try
        {
            await new AuthRecordStore(path).SaveAsync(AuthTests.Record(), Ct);

            if (OperatingSystem.IsWindows())
            {
                var security = new FileInfo(path).GetAccessControl();
                Assert.True(security.AreAccessRulesProtected);
                var rule = Assert.Single(security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)).Cast<System.Security.AccessControl.FileSystemAccessRule>());
                Assert.Equal(System.Security.AccessControl.AccessControlType.Allow, rule.AccessControlType);
                Assert.Equal(System.Security.Principal.WindowsIdentity.GetCurrent().User, rule.IdentityReference);
            }
            else
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            }
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  ")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("\"just a string\"")]
    [InlineData("{ this is not a record")]
    [InlineData("{\"username\":\"a\",\"version\":\"99.0\"}")]
    [InlineData("{\"username\":42,\"authority\":[],\"homeAccountId\":{},\"tenantId\":1,\"clientId\":2,\"version\":\"1.0\"}")]
    public async Task ADamagedOrForeignRecordIsTreatedAsNotSignedIn(string content)
    {
        var path = NewRecordPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content, Ct);
            var store = new AuthRecordStore(path);

            Assert.True(store.Exists);
            Assert.Null(store.TryLoad());
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task BinaryGarbageAndATruncatedRecordAreTreatedAsNotSignedIn()
    {
        var path = NewRecordPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var store = new AuthRecordStore(path);
            await File.WriteAllBytesAsync(path, [0xFF, 0xFE, 0x00, 0x01, 0x80, 0x81, 0x00, 0x00], Ct);
            Assert.Null(store.TryLoad());

            await store.SaveAsync(AuthTests.Record(), Ct);
            var whole = await File.ReadAllBytesAsync(path, Ct);
            await File.WriteAllBytesAsync(path, whole[..(whole.Length / 2)], Ct);
            Assert.Null(store.TryLoad());
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task ADirectoryWhereTheRecordShouldBeIsNotARecord()
    {
        var path = NewRecordPath();
        try
        {
            Directory.CreateDirectory(path);
            var store = new AuthRecordStore(path);

            Assert.False(store.Exists);
            Assert.Null(store.TryLoad());
            Assert.False(store.Delete());
            await Task.CompletedTask;
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task ARecordLockedByAnotherProcessIsTreatedAsNotSignedIn()
    {
        var path = NewRecordPath();
        try
        {
            var store = new AuthRecordStore(path);
            await store.SaveAsync(AuthTests.Record(), Ct);

            if (OperatingSystem.IsWindows())
            {
                using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                Assert.Null(store.TryLoad());
            }
            else
            {
                Assert.NotNull(store.TryLoad());
            }
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task TheRecordHoldsNoTokenOrSecretMaterial()
    {
        var path = NewRecordPath();
        try
        {
            await new AuthRecordStore(path).SaveAsync(AuthTests.Record(), Ct);

            var text = await File.ReadAllTextAsync(path, Ct);

            foreach (var word in new[] { "token", "secret", "password", "refresh", "eyJ" })
            {
                Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void TheDefaultPathFollowsThePlatformConventionAndHonoursXdgDataHomeWhereItApplies()
    {
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
            Assert.Equal(Path.Combine(local, "EArm", "auth", "record.json"), AuthRecordStore.DefaultPath());
            return;
        }

        using var env = new EnvironmentScope().Set("XDG_DATA_HOME", "/custom/data");
        Assert.Equal(Path.Combine("/custom/data", "earm", "auth", "record.json"), AuthRecordStore.DefaultPath());
        env.Set("XDG_DATA_HOME", "relative/path");
        Assert.Contains(Path.Combine(".local", "share", "earm", "auth", "record.json"), AuthRecordStore.DefaultPath(), StringComparison.Ordinal);
        env.Set("XDG_DATA_HOME", null);
        Assert.Contains(Path.Combine(".local", "share", "earm", "auth", "record.json"), AuthRecordStore.DefaultPath(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheStoreRejectsANullRecord()
    {
        var store = new AuthRecordStore(NewRecordPath());

        await Assert.ThrowsAsync<ArgumentNullException>(() => store.SaveAsync(null!, Ct));
        Assert.False(store.Exists);
    }

    // ---- TokenClaims -----------------------------------------------------------------------------------------------

    private static string B64Url(string text)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string Jwt(string payloadJson)
    {
        return B64Url("{\"alg\":\"none\"}") + "." + B64Url(payloadJson) + ".sig";
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-jwt")]
    [InlineData("only.two")]
    [InlineData("a.b.c.d")]
    [InlineData("a..c")]
    [InlineData("a.!!!.c")]
    [InlineData("a.e30.c.")]
    public void ANonTokenIsNotDecoded(string? token)
    {
        Assert.False(TokenClaims.TryDecode(token, out var claims));
        Assert.Null(claims.TenantId);
        Assert.Empty(claims.Scopes);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("\"string payload\"")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("{\"tid\":")]
    public void APayloadThatIsNotAJsonObjectIsNotDecoded(string payload)
    {
        Assert.False(TokenClaims.TryDecode(Jwt(payload), out _));
    }

    [Fact]
    public void ABase64PayloadWithAnImpossibleLengthIsNotDecoded()
    {
        // A base64 string whose length is 1 mod 4 can never be valid.
        Assert.False(TokenClaims.TryDecode("a.QUJDR.c", out _));
    }

    [Fact]
    public void AHugeTokenIsRefusedBeforeItIsDecoded()
    {
        var huge = "a." + B64Url("{\"pad\":\"" + new string('x', TokenClaims.MaxTokenLength) + "\"}") + ".c";

        Assert.True(huge.Length > TokenClaims.MaxTokenLength);
        Assert.False(TokenClaims.TryDecode(huge, out _));
    }

    [Fact]
    public void ATokenJustUnderTheLimitStillDecodes()
    {
        var padding = TokenClaims.MaxTokenLength - 200;
        var token = Jwt("{\"tid\":\"t\",\"pad\":\"" + new string('x', padding * 3 / 4 - 100) + "\"}");

        Assert.True(token.Length <= TokenClaims.MaxTokenLength);
        Assert.True(TokenClaims.TryDecode(token, out var claims));
        Assert.Equal("t", claims.TenantId);
    }

    [Fact]
    public void AnEmptyObjectDecodesToAClaimsRecordWithNothingInIt()
    {
        Assert.True(TokenClaims.TryDecode(Jwt("{}"), out var claims));

        Assert.Null(claims.TenantId);
        Assert.Null(claims.ObjectId);
        Assert.Null(claims.UserPrincipalName);
        Assert.Null(claims.AppId);
        Assert.Null(claims.Audience);
        Assert.Null(claims.Name);
        Assert.Null(claims.ExpiresOn);
        Assert.Empty(claims.Scopes);
        Assert.Empty(claims.Roles);
        Assert.False(claims.IsApplication);
        Assert.Null(claims.PrincipalName);
        Assert.Equal("user", claims.PrincipalType);
    }

    [Fact]
    public void AUserTokenExposesNameTenantScopesAndExpiry()
    {
        var token = Jwt("{\"tid\":\"t1\",\"oid\":\"o1\",\"upn\":\"alice@contoso.example\",\"name\":\"Alice\",\"appid\":\"a1\",\"aud\":\"https://graph.microsoft.com\",\"scp\":\"User.Read  Directory.Read.All\",\"exp\":1790000000}");

        Assert.True(TokenClaims.TryDecode(token, out var claims));

        Assert.Equal("t1", claims.TenantId);
        Assert.Equal("o1", claims.ObjectId);
        Assert.Equal("alice@contoso.example", claims.PrincipalName);
        Assert.Equal("Alice", claims.Name);
        Assert.Equal("a1", claims.AppId);
        Assert.Equal("https://graph.microsoft.com", claims.Audience);
        Assert.Equal(["User.Read", "Directory.Read.All"], claims.Scopes);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790000000), claims.ExpiresOn);
        Assert.False(claims.IsApplication);
        Assert.Equal("user", claims.PrincipalType);
    }

    [Theory]
    [InlineData("{\"upn\":\"u1\",\"preferred_username\":\"u2\",\"unique_name\":\"u3\"}", "u1")]
    [InlineData("{\"preferred_username\":\"u2\",\"unique_name\":\"u3\"}", "u2")]
    [InlineData("{\"unique_name\":\"u3\"}", "u3")]
    public void TheUserNameComesFromUpnThenPreferredUsernameThenUniqueName(string payload, string expected)
    {
        Assert.True(TokenClaims.TryDecode(Jwt(payload), out var claims));
        Assert.Equal(expected, claims.UserPrincipalName);
    }

    [Fact]
    public void AnApplicationTokenIsRecognisedByIdtypOrByRolesWithoutAUser()
    {
        Assert.True(TokenClaims.TryDecode(Jwt("{\"idtyp\":\"app\",\"appid\":\"app1\",\"oid\":\"o\"}"), out var byType));
        Assert.True(byType.IsApplication);
        Assert.Equal("application", byType.PrincipalType);
        Assert.Equal("app1", byType.PrincipalName);

        Assert.True(TokenClaims.TryDecode(Jwt("{\"azp\":\"app2\",\"roles\":[\"Directory.ReadWrite.All\",\"Group.ReadWrite.All\"]}"), out var byRoles));
        Assert.True(byRoles.IsApplication);
        Assert.Equal("app2", byRoles.AppId);
        Assert.Equal(["Directory.ReadWrite.All", "Group.ReadWrite.All"], byRoles.Roles);

        Assert.True(TokenClaims.TryDecode(Jwt("{\"idtyp\":\"user\",\"roles\":[\"Some.Role\"],\"upn\":\"u\"}"), out var user));
        Assert.False(user.IsApplication);

        Assert.True(TokenClaims.TryDecode(Jwt("{\"roles\":[\"Some.Role\"],\"scp\":\"User.Read\"}"), out var delegated));
        Assert.False(delegated.IsApplication);
    }

    [Fact]
    public void AppIdWinsOverAzpAndAnApplicationFallsBackToTheObjectId()
    {
        Assert.True(TokenClaims.TryDecode(Jwt("{\"appid\":\"first\",\"azp\":\"second\"}"), out var both));
        Assert.Equal("first", both.AppId);

        Assert.True(TokenClaims.TryDecode(Jwt("{\"oid\":\"object-only\"}"), out var objectOnly));
        Assert.Equal("object-only", objectOnly.PrincipalName);
    }

    [Fact]
    public void UnicodeClaimsSurviveTheDecoding()
    {
        Assert.True(TokenClaims.TryDecode(Jwt("{\"name\":\"Zoë 日本語 🎉\",\"upn\":\"zoë@contoso.example\"}"), out var claims));

        Assert.Equal("Zoë 日本語 🎉", claims.Name);
        Assert.Equal("zoë@contoso.example", claims.PrincipalName);
    }

    [Fact]
    public void WrongTypedClaimsAreIgnoredRatherThanThrown()
    {
        Assert.True(TokenClaims.TryDecode(Jwt("{\"tid\":5,\"oid\":[],\"upn\":{},\"scp\":7,\"roles\":\"not-an-array\",\"exp\":\"soon\"}"), out var claims));

        Assert.Null(claims.TenantId);
        Assert.Null(claims.ObjectId);
        Assert.Null(claims.UserPrincipalName);
        Assert.Empty(claims.Scopes);
        Assert.Empty(claims.Roles);
        Assert.Null(claims.ExpiresOn);
    }

    [Fact]
    public void NonStringEntriesInTheRolesArrayAreSkipped()
    {
        Assert.True(TokenClaims.TryDecode(Jwt("{\"roles\":[\"A\",5,null,\"B\",{}]}"), out var claims));

        Assert.Equal(["A", "B"], claims.Roles);
    }

    [Fact]
    public void AnExpiryBeyondTheRepresentableRangeMakesTheTokenUndecodableInsteadOfThrowing()
    {
        Assert.False(TokenClaims.TryDecode(Jwt("{\"exp\":99999999999999999}"), out _));
    }

    [Theory]
    [InlineData("{\"tid\":\"t\"}")]
    [InlineData("{\"tid\":\"t\",\"x\":\"é\"}")]
    [InlineData("{\"tid\":\"t\",\"x\":\">>>???\"}")]
    public void Base64UrlPaddingVariantsAllDecode(string payload)
    {
        var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.True(TokenClaims.TryDecode("h." + raw + ".s", out var claims));
        Assert.Equal("t", claims.TenantId);
    }

    [Fact]
    public void AVeryDeeplyNestedPayloadIsNotDecodedAndDoesNotThrow()
    {
        var deep = new string('[', 5000) + new string(']', 5000);

        Assert.False(TokenClaims.TryDecode(Jwt("{\"a\":" + deep + "}"), out _));
    }

    [Fact]
    public async Task TheStatusDescribesTheSignedInIdentityWithoutKeepingTheToken()
    {
        var path = NewRecordPath();
        try
        {
            var store = new AuthRecordStore(path);
            await store.SaveAsync(AuthTests.Record(), Ct);
            var token = Jwt("{\"tid\":\"t1\",\"oid\":\"o1\",\"upn\":\"alice@contoso.example\",\"scp\":\"User.Read\",\"exp\":1790000000}");
            using var provider = new AzureIdentityTokenProvider(new FakeCredential().Returns(token, DateTimeOffset.FromUnixTimeSeconds(1790000000)), GraphAuthMode.Auto);

            var status = await GraphAuthStatus.ReadAsync(provider, new GraphAuthOptions(), store, Ct);

            Assert.Equal("auto", status.Mode);
            Assert.Equal("interactive sign-in, then the default chain", status.Credential);
            Assert.Equal("public", status.Cloud);
            Assert.Equal("t1", status.TenantId);
            Assert.Equal("alice@contoso.example", status.Principal);
            Assert.Equal("user", status.PrincipalType);
            Assert.Equal(["User.Read"], status.Scopes);
            Assert.Equal(path, status.SignInRecord);
            Assert.Equal("alice@contoso.example", status.SignInAccount);
            Assert.DoesNotContain(token, status.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public async Task TheStatusOfATokenWithoutClaimsStillReportsTheExpiryAndTheModeCredential()
    {
        var path = NewRecordPath();
        using var provider = new AzureIdentityTokenProvider(new FakeCredential().Returns("opaque", DateTimeOffset.FromUnixTimeSeconds(1790000000)), GraphAuthMode.Default);

        var defaultStatus = await GraphAuthStatus.ReadAsync(provider, new GraphAuthOptions { Mode = GraphAuthMode.Default }, new AuthRecordStore(path), Ct);

        Assert.Equal("default", defaultStatus.Mode);
        Assert.StartsWith("default credential chain", defaultStatus.Credential, StringComparison.Ordinal);
        Assert.Null(defaultStatus.TenantId);
        Assert.Null(defaultStatus.SignInRecord);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790000000), defaultStatus.ExpiresOn);

        using var interactiveProvider = new AzureIdentityTokenProvider(new FakeCredential().Returns("opaque", DateTimeOffset.UtcNow.AddHours(1)), GraphAuthMode.Interactive);
        var interactiveStatus = await GraphAuthStatus.ReadAsync(interactiveProvider, new GraphAuthOptions { Mode = GraphAuthMode.Interactive }, new AuthRecordStore(path), Ct);
        Assert.Equal("interactive sign-in", interactiveStatus.Credential);

        using var autoProvider = new AzureIdentityTokenProvider(new FakeCredential().Returns("opaque", DateTimeOffset.UtcNow.AddHours(1)), GraphAuthMode.Auto);
        var autoStatus = await GraphAuthStatus.ReadAsync(autoProvider, new GraphAuthOptions(), new AuthRecordStore(path), Ct);
        Assert.StartsWith("default credential chain", autoStatus.Credential, StringComparison.Ordinal);
    }
}
