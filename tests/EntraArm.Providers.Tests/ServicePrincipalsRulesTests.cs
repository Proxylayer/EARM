using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EntraArm.Core;
using EntraArm.Engine.Providers;
using EntraArm.Providers.Tests.Infrastructure;
using EntraArm.TestSupport;
using Xunit;

namespace EntraArm.Providers.Tests;

/// <summary>Pure rules and the lookup helper of the service principals provider (WI-08).</summary>
public sealed class ServicePrincipalsRulesTests
{
    private const string AppId = "6d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0002";

    [Theory]
    [InlineData(AppId, true)]
    [InlineData("{6D2F1C0E-2B7A-4C1E-9A1B-4F3F1F6A0002}", true)]
    [InlineData("not-a-guid", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAppId_AcceptsGuidsOnly(string? name, bool expected)
    {
        Assert.Equal(expected, ServicePrincipalsRules.IsAppId(name));
    }

    [Fact]
    public void ReconcileDeclaredAppId_RewritesAnEqualSpelling_AndRejectsADifferentValue()
    {
        var equal = new JsonObject { ["appId"] = AppId.ToUpperInvariant() };
        Assert.Null(ServicePrincipalsRules.ReconcileDeclaredAppId(equal, AppId, "sp"));
        Assert.Equal(AppId, equal["appId"]!.GetValue<string>());

        var different = new JsonObject { ["appId"] = Guid.NewGuid().ToString("D") };
        string? problem = ServicePrincipalsRules.ReconcileDeclaredAppId(different, AppId, "sp");
        Assert.NotNull(problem);
        Assert.Contains("conflicts with the resource name", problem, StringComparison.Ordinal);
        Assert.Contains("NameMappedPropertyConflict", problem, StringComparison.Ordinal);

        Assert.Null(ServicePrincipalsRules.ReconcileDeclaredAppId(new JsonObject(), AppId, "sp"));
        Assert.Null(ServicePrincipalsRules.ReconcileDeclaredAppId(new JsonObject { ["appId"] = null }, AppId, "sp"));
    }

    [Fact]
    public void RemoveServerAssigned_DropsIdAndType_AndAppIdUnlessKept()
    {
        var create = (JsonObject)JsonNode.Parse("""{ "id": "x", "servicePrincipalType": "Application", "appId": "a", "tags": [] }""")!;
        ServicePrincipalsRules.RemoveServerAssigned(create, keepAppId: true);
        Assert.Equal("""{"appId":"a","tags":[]}""", create.ToJsonString());

        var patch = (JsonObject)JsonNode.Parse("""{ "id": "x", "ServicePrincipalType": "Application", "AppId": "a", "tags": [] }""")!;
        ServicePrincipalsRules.RemoveServerAssigned(patch, keepAppId: false);
        Assert.Equal("""{"tags":[]}""", patch.ToJsonString());
    }

    [Fact]
    public void OrderCreateBody_PutsLeadingPropertiesFirst_ThenTheRest()
    {
        var body = (JsonObject)JsonNode.Parse("""{ "tags": ["t"], "notes": "n", "appId": "a", "accountEnabled": true }""")!;

        var ordered = ServicePrincipalsRules.OrderCreateBody(body, ["appId", "accountEnabled", "missing"]);

        Assert.Equal("""{"appId":"a","accountEnabled":true,"tags":["t"],"notes":"n"}""", ordered.ToJsonString());
        Assert.Equal("""{"tags":["t"],"notes":"n","appId":"a","accountEnabled":true}""", body.ToJsonString());
    }

    [Fact]
    public void NormalizeLive_CopiesDeclaredPassthroughs_FromTheRawObject()
    {
        var stripped = (JsonObject)JsonNode.Parse("""{ "accountEnabled": true, "tags": [] }""")!;
        var raw = (JsonObject)JsonNode.Parse("""{ "id": "o", "appId": "a", "servicePrincipalType": "Application", "accountEnabled": true, "tags": [] }""")!;
        var existing = new ExistingResource("o", stripped, raw);

        var both = ServicePrincipalsRules.NormalizeLive(existing, (JsonObject)JsonNode.Parse("""{ "appId": "a", "servicePrincipalType": "ManagedIdentity" }""")!);
        Assert.Equal("a", both["appId"]!.GetValue<string>());
        Assert.Equal("Application", both["servicePrincipalType"]!.GetValue<string>());

        var none = ServicePrincipalsRules.NormalizeLive(existing, new JsonObject());
        Assert.Null(none["appId"]);
        Assert.Null(none["servicePrincipalType"]);
        Assert.Null(stripped["appId"]);
    }

    [Fact]
    public void ReferencesTemplateApplication_IsTrueOnlyForAnApplicationTarget()
    {
        Assert.True(ServicePrincipalsRules.ReferencesTemplateApplication([ResourceIdentifier.Create("Microsoft.Entra/users", "a@b.c"), ResourceIdentifier.Create("Microsoft.Entra/applications", "x")]));
        Assert.False(ServicePrincipalsRules.ReferencesTemplateApplication([ResourceIdentifier.Create("Microsoft.Entra/groups", "g")]));
        Assert.False(ServicePrincipalsRules.ReferencesTemplateApplication([]));
    }

    [Fact]
    public async Task Lookup_FindsByAppId_WithTheLookupSelect_AndResolvesRoleAndScopeIds()
    {
        var client = new FakeGraphClient();
        var seeded = client.Directory.AddServicePrincipal(AppId, "Test API", sp =>
        {
            sp["appRoles"] = new JsonArray(new JsonObject { ["id"] = "6d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0002", ["value"] = "Orders.Admin", ["isEnabled"] = true });
            sp["oauth2PermissionScopes"] = new JsonArray(new JsonObject { ["id"] = "7d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0003", ["value"] = "access_as_user", ["isEnabled"] = true });
        });

        var summary = await ServicePrincipalLookup.FindByAppIdAsync(client, AppId, cancellationToken: ProviderHost.Ct);

        Assert.NotNull(summary);
        Assert.Equal(seeded["id"]!.GetValue<string>(), summary.Id);
        Assert.Equal(AppId, summary.AppId);
        Assert.Equal("6d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0002", summary.FindAppRoleId("Orders.Admin"));
        Assert.Equal("7d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0003", summary.FindPermissionScopeId("access_as_user"));
        Assert.Null(summary.FindAppRoleId("nope"));
        var request = Assert.Single(client.Requests);
        Assert.Equal("/servicePrincipals(appId='" + AppId + "')", request.Path);
        Assert.Equal(string.Join(",", ServicePrincipalLookup.SelectedProperties), request.UrlParameters!["$select"]);

        Assert.Null(await ServicePrincipalLookup.FindByAppIdAsync(client, Guid.NewGuid().ToString("D"), cancellationToken: ProviderHost.Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => ServicePrincipalLookup.FindByAppIdAsync(client, "not-a-guid", cancellationToken: ProviderHost.Ct));
    }

    [Fact]
    public void Summarize_ReadsIdsAndValues_FromALiveObject()
    {
        var live = (JsonObject)JsonNode.Parse("""{ "id": "o", "appId": "a", "displayName": "d", "appRoles": [ { "id": "r1", "value": "R" } ], "oauth2PermissionScopes": [ { "id": "s1", "value": "S" } ] }""")!;

        var summary = ServicePrincipalLookup.Summarize(live);

        Assert.Equal("o", summary.Id);
        Assert.Equal("a", summary.AppId);
        Assert.Equal("r1", summary.FindAppRoleId("R"));
        Assert.Equal("s1", summary.FindPermissionScopeId("S"));
        Assert.Null(summary.FindPermissionScopeId("R"));
    }
}
