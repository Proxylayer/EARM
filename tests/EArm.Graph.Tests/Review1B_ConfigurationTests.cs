using System;
using EArm.Graph.Auth;
using EArm.Graph.Tests.Infrastructure;
using Xunit;

namespace EArm.Graph.Tests;

/// <summary>
/// Review finding Q-20 (Graph part): <c>EARM_CLOUD=bogus</c> leaked ".NET argument-exception text" ("(Parameter 'value')"). Configuration errors are
/// <see cref="GraphConfigurationException"/>s with a clean message that names the setting and lists the valid values, uniformly for the cloud, the
/// authentication mode, the tenant and the client id.
/// </summary>
[Collection(Review1BEnvironmentCollection.Name)]
public sealed class Review1B_ConfigurationTests
{
    private static void AssertClean(GraphConfigurationException error)
    {
        Assert.DoesNotContain("Parameter", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("(Parameter", error.ToString().Split('\n')[0], StringComparison.Ordinal);
        Assert.Null(error.ParamName);
        Assert.DoesNotContain('\n', error.Message);
    }

    [Fact]
    public void ABogusCloudInTheEnvironmentIsACleanConfigurationError()
    {
        using var env = new EnvironmentScope().Set(GraphCloud.EnvironmentVariable, "bogus");

        var error = Assert.Throws<GraphConfigurationException>(GraphCloud.FromEnvironment);

        AssertClean(error);
        Assert.Equal("Unknown cloud 'bogus' (EARM_CLOUD). Valid values: public, usgov, usgovdod, china.", error.Message);
        Assert.IsAssignableFrom<ArgumentException>(error);
    }

    [Theory]
    [InlineData(null, "public")]
    [InlineData("", "public")]
    [InlineData("   ", "public")]
    [InlineData("public", "public")]
    [InlineData("USGOV", "usgov")]
    [InlineData(" China ", "china")]
    [InlineData("UsGovDod", "usgovdod")]
    public void ValidCloudsAreCaseAndWhitespaceInsensitive(string? value, string expected)
    {
        using var env = new EnvironmentScope().Set(GraphCloud.EnvironmentVariable, value);

        Assert.Equal(expected, GraphCloud.FromEnvironment().Key);
        Assert.Equal(expected, GraphCloud.Parse(value).Key);
    }

    [Fact]
    public void AnUnsetCloudVariableMeansThePublicCloud()
    {
        using var env = new EnvironmentScope().Set(GraphCloud.EnvironmentVariable, null);

        Assert.Same(GraphCloud.Public, GraphCloud.FromEnvironment());
    }

    [Theory]
    [InlineData("pub lic")]
    [InlineData("usgov\nx")]
    [InlineData("cn")]
    public void EveryInvalidCloudNamesTheSettingAndListsTheValidValues(string value)
    {
        var error = Assert.Throws<GraphConfigurationException>(() => GraphCloud.Parse(value));

        AssertClean(error);
        Assert.Contains("EARM_CLOUD", error.Message, StringComparison.Ordinal);
        Assert.EndsWith("Valid values: public, usgov, usgovdod, china.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ControlCharactersAndLongValuesAreNeutralisedInTheMessage()
    {
        var error = Assert.Throws<GraphConfigurationException>(() => GraphCloud.Parse("bad\u001b[31m\r\nvalue" + new string('x', 200)));

        Assert.DoesNotContain('\u001b', error.Message);
        Assert.DoesNotContain('\r', error.Message);
        Assert.DoesNotContain('\n', error.Message);
        Assert.Contains("...", error.Message, StringComparison.Ordinal);
        Assert.True(error.Message.Length < 200);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("short", "short")]
    [InlineData("a\tb", "a?b")]
    public void DescribeKeepsShortCleanValuesAndMasksControlCharacters(string value, string expected)
    {
        Assert.Equal(expected, GraphCloud.Describe(value));
    }

    [Fact]
    public void DescribeCutsAtFortyCharacters()
    {
        Assert.Equal(new string('a', 40), GraphCloud.Describe(new string('a', 40)));
        Assert.Equal(new string('a', 40) + "...", GraphCloud.Describe(new string('a', 41)));
    }

    [Fact]
    public void TheConfigurationExceptionKeepsItsInnerException()
    {
        var inner = new InvalidOperationException("inner");

        var error = new GraphConfigurationException("outer", inner);

        Assert.Same(inner, error.InnerException);
        Assert.Equal("outer", error.Message);
    }

    // ---- the other settings are uniform ---------------------------------------------------------------------------

    [Theory]
    [InlineData("cli")]
    [InlineData("AUTO2")]
    [InlineData("device-code")]
    public void AnUnknownAuthModeIsACleanConfigurationError(string value)
    {
        var error = Assert.Throws<GraphConfigurationException>(() => GraphAuthOptions.ParseMode(value));

        AssertClean(error);
        Assert.Contains("EARM_AUTH", error.Message, StringComparison.Ordinal);
        Assert.Contains("--auth", error.Message, StringComparison.Ordinal);
        Assert.EndsWith("Valid values: auto, default, interactive.", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, GraphAuthMode.Auto)]
    [InlineData("", GraphAuthMode.Auto)]
    [InlineData(" AUTO ", GraphAuthMode.Auto)]
    [InlineData("Default", GraphAuthMode.Default)]
    [InlineData("INTERACTIVE", GraphAuthMode.Interactive)]
    public void ValidAuthModesParse(string? value, GraphAuthMode expected)
    {
        Assert.Equal(expected, GraphAuthOptions.ParseMode(value));
    }

    [Theory]
    [InlineData("bad tenant")]
    [InlineData("contoso.com/../x")]
    [InlineData("tenant;drop")]
    [InlineData("ten\u0007ant")]
    public void AnInvalidTenantIsACleanConfigurationError(string tenant)
    {
        var error = Assert.Throws<GraphConfigurationException>(() => GraphAuthOptions.Resolve(null, tenant, null, GraphCloud.Public));

        AssertClean(error);
        Assert.Contains("--tenant", error.Message, StringComparison.Ordinal);
        Assert.Contains("EARM_TENANT_ID", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\u0007', error.Message);
    }

    [Theory]
    [InlineData("11111111-1111-1111-1111-111111111111")]
    [InlineData("contoso.onmicrosoft.com")]
    [InlineData("organizations")]
    [InlineData("common")]
    [InlineData("a-b.c")]
    public void ValidTenantsAreAccepted(string tenant)
    {
        Assert.Equal(tenant, GraphAuthOptions.Resolve(null, tenant, null, GraphCloud.Public).TenantId);
    }

    [Fact]
    public void ATenantOfExactlyTwoHundredFiftyThreeCharactersIsTheLongestAccepted()
    {
        Assert.NotNull(GraphAuthOptions.Resolve(null, new string('a', 253), null, GraphCloud.Public).TenantId);
        Assert.Throws<GraphConfigurationException>(() => GraphAuthOptions.Resolve(null, new string('a', 254), null, GraphCloud.Public));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("1234")]
    [InlineData("11111111-1111-1111-1111-11111111111Z")]
    public void AnInvalidClientIdIsACleanConfigurationError(string clientId)
    {
        var error = Assert.Throws<GraphConfigurationException>(() => GraphAuthOptions.Resolve(null, null, clientId, GraphCloud.Public));

        AssertClean(error);
        Assert.Contains("--client-id", error.Message, StringComparison.Ordinal);
        Assert.Contains("EARM_CLIENT_ID", error.Message, StringComparison.Ordinal);
        Assert.Contains("GUID", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveTakesTheEnvironmentWhenNoArgumentIsGivenAndEmptyArgumentsFallThrough()
    {
        using var env = new EnvironmentScope()
            .Set(GraphAuthOptions.AuthVariable, "default")
            .Set(GraphAuthOptions.TenantVariable, "from-env.example")
            .Set(GraphAuthOptions.ClientIdVariable, "55555555-5555-5555-5555-555555555555");

        var fromEnvironment = GraphAuthOptions.Resolve("", "  ", null, GraphCloud.China);
        var overridden = GraphAuthOptions.Resolve("interactive", "arg.example", "66666666-6666-6666-6666-666666666666", GraphCloud.UsGov);

        Assert.Equal(GraphAuthMode.Default, fromEnvironment.Mode);
        Assert.Equal("from-env.example", fromEnvironment.TenantId);
        Assert.Equal("55555555-5555-5555-5555-555555555555", fromEnvironment.ClientId);
        Assert.Same(GraphCloud.China, fromEnvironment.Cloud);
        Assert.Equal(GraphAuthMode.Interactive, overridden.Mode);
        Assert.Equal("arg.example", overridden.TenantId);
        Assert.Equal("66666666-6666-6666-6666-666666666666", overridden.ClientId);
    }

    [Fact]
    public void ABadEnvironmentValueIsReportedLikeABadArgument()
    {
        using var env = new EnvironmentScope().Set(GraphAuthOptions.AuthVariable, "nonsense");

        var error = Assert.Throws<GraphConfigurationException>(() => GraphAuthOptions.Resolve(null, null, null, GraphCloud.Public));

        AssertClean(error);
        Assert.Contains("nonsense", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(GraphAuthMode.Auto, "auto")]
    [InlineData(GraphAuthMode.Default, "default")]
    [InlineData(GraphAuthMode.Interactive, "interactive")]
    public void EveryModeHasAName(GraphAuthMode mode, string name)
    {
        Assert.Equal(name, GraphAuthOptions.ModeName(mode));
    }

    [Fact]
    public void AnUndefinedModeHasNoName()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GraphAuthOptions.ModeName((GraphAuthMode)99));
    }
}
