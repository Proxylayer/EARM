using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph.Tests.Infrastructure;
using Xunit;

namespace EArm.Graph.Tests;

/// <summary>
/// Review 2E: an alternate key built by <see cref="GraphPath.AlternateKey"/> from a value with <c>?</c>, <c>#</c>, <c>%</c>, <c>/</c> or <c>'</c> reaches the wire as one
/// path (no query string, no fragment, one escape level) and the handler sees the same escaped path the key was built with.
/// </summary>
public sealed class Review2E_AlternateKeyWireTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, string> Values => new()
    {
        { "a?b#c", "/v1.0/applications(uniqueName='a%3Fb%23c')" },
        { "it's", "/v1.0/applications(uniqueName='it''s')" },
        { "50% off", "/v1.0/applications(uniqueName='50%25%20off')" },
        { "a%3Fb", "/v1.0/applications(uniqueName='a%253Fb')" },
        { "a/b", "/v1.0/applications(uniqueName='a%2Fb')" },
    };

    [Theory]
    [MemberData(nameof(Values))]
    public async Task TheKeyReachesTheWireAsOnePathWithNoQueryOrFragment(string value, string expectedPath)
    {
        using var harness = new ClientHarness(new ScriptedHttpHandler(ScriptedResponse.Json("{\"id\":\"1\"}")));

        await harness.Client.GetAsync(new GraphRequest(GraphPath.AlternateKey("/applications", "uniqueName", value)), Ct);

        var uri = Assert.Single(harness.Handler.Requests).Uri;
        Assert.Equal(expectedPath, uri.AbsolutePath);
        Assert.Equal(string.Empty, uri.Query);
        Assert.Equal(string.Empty, uri.Fragment);
        Assert.DoesNotContain('?', uri.AbsolutePath);
        Assert.DoesNotContain('#', uri.AbsolutePath);
    }

    [Theory]
    [MemberData(nameof(Values))]
    public void TheKeyBuiltFromTheValue_HasNoRawReservedCharacter(string value, string expectedPath)
    {
        string key = GraphPath.AlternateKey("/applications", "uniqueName", value);

        Assert.Equal(expectedPath["/v1.0".Length..], key);
        Assert.DoesNotContain(key, c => c is '?' or '#' or ' ');
    }
}
