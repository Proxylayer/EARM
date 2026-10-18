using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Engine.Providers;
using EArm.Graph;
using EArm.Providers.Tests.Infrastructure;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// The kinds of object a group's v1.0 <c>owners</c> and <c>members</c> lists leave out. Learn, group-list-owners: "service principals are not listed as
/// group owners" in v1.0, so the ownership edge kind reads them through the type cast, as the membership kind does for members (known issue: members omit
/// service principals). Devices and organizational contacts are documented member types with no such note: v1.0 lists them, so they need nothing extra.
/// The fake models both.
/// </summary>
public sealed class GroupOwnerAndMemberTypesTests
{
    private const string GroupName = "typed-group";

    private static string Group(string property, params string[] ids)
    {
        string list = string.Join(", ", ids.Select(id => "\"" + id + "\""));
        return Templates.Resource("Microsoft.Entra/groups", GroupName, "{ \"displayName\": \"Typed Group\", \"" + property + "\": [" + list + "] }");
    }

    private static async Task AddReferenceAsync(ProviderHost host, string navigationPath, string id)
    {
        await host.Fake.PostAsync(new GraphRequest(navigationPath + "/$ref"), new JsonObject { ["@odata.id"] = "https://graph.microsoft.com/v1.0/directoryObjects/" + id }, ProviderHost.Ct);
    }

    private static string Id(JsonObject entity)
    {
        return entity["id"]!.GetValue<string>();
    }

    private static async Task<string[]> V1IdsAsync(ProviderHost host, string path)
    {
        var page = await host.Fake.GetAsync(new GraphRequest(path).WithSelect("id"), ProviderHost.Ct);
        return page!["value"]!.AsArray().Select(o => o!["id"]!.GetValue<string>()).ToArray();
    }

    [Fact]
    public async Task AServicePrincipalOwnerOfAGroup_OmittedByV1Owners_IsReadThroughTheTypeCast_AndTheNextWhatIfIsNoChange()
    {
        var host = new ProviderHost();
        string alice = Id(host.Directory.AddUser("alice@contoso.com", "Alice"));
        string appId = host.Directory.AddApplication("owner-app", "Owner App")["appId"]!.GetValue<string>();
        string sp = Id(host.Directory.AddServicePrincipal(appId, "Owner SP"));
        var template = host.Resources(Group("owners", alice, sp));

        var created = await host.CreateAsync(template);

        Check.Succeeded(created);
        string groupId = host.IdOf("/groups", "uniqueName", GroupName);
        Assert.Contains(sp, host.Directory.References($"/groups/{groupId}/owners"));
        Assert.DoesNotContain(sp, await V1IdsAsync(host, $"/groups/{groupId}/owners"));

        var whatIf = await host.WhatIfAsync(template);

        Check.NoWrites(whatIf);
        var row = whatIf.Change(GroupName);
        Assert.Equal(ChangeType.NoChange, row.Type);
        Assert.DoesNotContain(row.Deltas, d => d.Path.ToString() == "properties.owners");
        Assert.Contains(whatIf.Requests, r => r.Method == "GET" && r.Path == $"/groups/{groupId}/owners/microsoft.graph.servicePrincipal");
    }

    [Fact]
    public async Task AnUndeclaredServicePrincipalOwnerOfAGroup_IsSeen_AndPrunedByTheAuthoritativeOwnersSet()
    {
        var host = new ProviderHost();
        string alice = Id(host.Directory.AddUser("alice@contoso.com", "Alice"));
        string appId = host.Directory.AddApplication("owner-app", "Owner App")["appId"]!.GetValue<string>();
        string sp = Id(host.Directory.AddServicePrincipal(appId, "Owner SP"));
        Check.Succeeded(await host.CreateAsync(host.Resources(Group("owners", alice, sp))));
        string groupId = host.IdOf("/groups", "uniqueName", GroupName);

        var pruned = await host.CreateAsync(host.Resources(Group("owners", alice)));

        Check.Succeeded(pruned);
        Assert.Contains(pruned.Writes, w => w.Method == "DELETE" && w.Path == $"/groups/{groupId}/owners/{sp}/$ref"
            || (w.Path == "/$batch" && w.Body!.ToJsonString().Contains($"/groups/{groupId}/owners/{sp}/$ref", StringComparison.Ordinal)));
        Assert.DoesNotContain(sp, host.Directory.References($"/groups/{groupId}/owners"));
        Assert.Contains(alice, host.Directory.References($"/groups/{groupId}/owners"));
    }

    [Fact]
    public async Task ApplicationOwners_AreListedWhole_ServicePrincipalsIncluded()
    {
        var host = new ProviderHost();
        var app = host.Directory.AddApplication("owned-app", "Owned App");
        string sp = Id(host.Directory.AddServicePrincipal(host.Directory.AddApplication("owner-app", "Owner App")["appId"]!.GetValue<string>(), "Owner SP"));
        await AddReferenceAsync(host, $"/applications/{Id(app)}/owners", sp);

        Assert.Contains(sp, await V1IdsAsync(host, $"/applications/{Id(app)}/owners"));
    }

    [Fact]
    public async Task DeviceAndOrgContactMembers_AreListedByV1Members_SoADeclaredSetSettles_AndAnUndeclaredOneIsPruned()
    {
        var host = new ProviderHost();
        string device = Id(host.Directory.Add("/devices", new JsonObject { ["displayName"] = "Kiosk 1", ["deviceId"] = Guid.NewGuid().ToString("D") }));
        string contact = Id(host.Directory.Add("/contacts", new JsonObject { ["displayName"] = "Partner Contact", ["mail"] = "partner@fabrikam.com" }));
        var both = host.Resources(Group("members", device, contact));

        Check.Succeeded(await host.CreateAsync(both));
        string groupId = host.IdOf("/groups", "uniqueName", GroupName);
        var listed = await host.Fake.GetAsync(new GraphRequest($"/groups/{groupId}/members"), ProviderHost.Ct);
        Assert.Equal(
            ["#microsoft.graph.device", "#microsoft.graph.orgContact"],
            listed!["value"]!.AsArray().Select(m => m!["@odata.type"]!.GetValue<string>()).Order(StringComparer.Ordinal).ToArray());

        var settled = await host.WhatIfAsync(both);
        Check.NoWrites(settled);
        Assert.Equal(ChangeType.NoChange, settled.Change(GroupName).Type);

        var pruned = await host.CreateAsync(host.Resources(Group("members", device)));
        Check.Succeeded(pruned);
        Assert.Equal([device], host.Directory.References($"/groups/{groupId}/members").ToArray());
    }
}
