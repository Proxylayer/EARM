using System;
using System.Linq;
using System.Threading.Tasks;
using EArm.Engine.Providers;
using EArm.Providers.Tests.Infrastructure;
using EArm.TestSupport;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// Review 2E: a uniqueName is a valid name for an application or group when it is any text without leading or trailing space or a slash, so
/// <c>a?b#c</c> and <c>50% off</c> are legal names. They go into the alternate-key path <c>applications(uniqueName='...')</c>: a raw <c>?</c>
/// would start the query string, a raw <c>#</c> a fragment, a raw <c>%</c> would be read as an escape, and <c>'</c> must be doubled.
/// </summary>
public sealed class Review2E_KeyEscapingTests
{
    public static TheoryData<string, string> Names => new()
    {
        { "a?b#c", "a%3Fb%23c" },
        { "it's", "it''s" },
        { "50% off", "50%25%20off" },
        { "a%3Fb", "a%253Fb" },
        { "q?x='1'&y=2#frag", "q%3Fx=''1''%26y=2%23frag" },
    };

    private static string KeyPath(string collection, string encoded)
    {
        return "/" + collection + "(uniqueName='" + encoded + "')";
    }

    private static void AssertNoRawQueryOrFragment(ProviderHost host, string collection)
    {
        var paths = host.Fake.Requests.Where(r => r.Path.StartsWith("/" + collection + "(", StringComparison.Ordinal)).Select(r => r.Path).ToList();
        Assert.NotEmpty(paths);
        Assert.All(paths, p =>
        {
            Assert.DoesNotContain('?', p);
            Assert.DoesNotContain('#', p);
            Assert.DoesNotContain(' ', p);
        });
    }

    [Theory]
    [MemberData(nameof(Names))]
    public async Task Applications_PutTheEscapedNameInTheKeyPath_AndFindItAgainOnTheRerun(string name, string encoded)
    {
        var host = new ProviderHost();
        var template = host.Resources(Templates.Resource("Microsoft.Entra/applications", name, """{ "displayName": "Escaped" }"""));

        Check.Succeeded(await host.CreateAsync(template));
        host.TrackState = false;
        var rerun = await host.CreateAsync(host.Resources(Templates.Resource("Microsoft.Entra/applications", name, """{ "displayName": "Escaped" }""")));

        Assert.Contains(host.Fake.Requests, r => r.Path == KeyPath("applications", encoded));
        AssertNoRawQueryOrFragment(host, "applications");
        Assert.Equal(ChangeType.NoChange, rerun.OpOfType("Microsoft.Entra/applications").ChangeType);
        Assert.Single(host.Directory.Objects("/applications"));
        Assert.Equal(name, host.Directory.Objects("/applications").Single()["uniqueName"]!.GetValue<string>());
    }

    [Theory]
    [MemberData(nameof(Names))]
    public async Task Groups_PutTheEscapedNameInTheKeyPath_AndFindItAgainOnTheRerun(string name, string encoded)
    {
        var host = new ProviderHost();
        var template = host.Resources(Templates.Resource("Microsoft.Entra/groups", name, """{ "displayName": "Escaped" }"""));

        Check.Succeeded(await host.CreateAsync(template));
        host.TrackState = false;
        var rerun = await host.CreateAsync(host.Resources(Templates.Resource("Microsoft.Entra/groups", name, """{ "displayName": "Escaped" }""")));

        Assert.Contains(host.Fake.Requests, r => r.Path == KeyPath("groups", encoded));
        AssertNoRawQueryOrFragment(host, "groups");
        Assert.Equal(ChangeType.NoChange, rerun.OpOfType("Microsoft.Entra/groups").ChangeType);
        Assert.Single(host.Directory.Objects("/groups"));
        Assert.Equal(name, host.Directory.Objects("/groups").Single()["uniqueName"]!.GetValue<string>());
    }

    [Fact]
    public async Task AChildOfAnApplicationWithAnAwkwardName_AddressesTheParentByTheEscapedKey()
    {
        var host = new ProviderHost();
        const string name = "a?b#c";
        string template = Templates.Resource("Microsoft.Entra/applications", name, """{ "displayName": "Parent" }""")
            + ","
            + Templates.Resource("Microsoft.Entra/applications/passwordCredentials", name + "/ci", "{ }");

        var run = await host.CreateAsync(host.Resources(template));

        Check.Succeeded(run);
        Assert.Contains(host.Fake.Requests, r => r.Path.StartsWith(KeyPath("applications", "a%3Fb%23c"), StringComparison.Ordinal));
        AssertNoRawQueryOrFragment(host, "applications");
    }
}
