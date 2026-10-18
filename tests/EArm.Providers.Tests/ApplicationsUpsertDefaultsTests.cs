using System;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Providers.Tests.Infrastructure;
using EArm.Engine.Providers;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// The applications create is an upsert (<c>PATCH /applications(uniqueName='…')</c> + <c>Prefer: create-if-missing</c>) that merges its body into an
/// application that already exists, such as one created after the plan's read. The overlay default (<c>signInAudience: AzureADMyOrg</c>) must reach
/// only an application EARM really creates, never one the upsert merges into.
/// </summary>
public sealed class ApplicationsUpsertDefaultsTests
{
    private const string Type = ApplicationsProvider.ResourceType;
    private const string Name = "race-app";
    private const string Collection = "/applications";

    private static async Task<(ProviderHost Host, ApplicationsProvider Provider, ResourceInstance Instance)> ArrangeAsync(string properties)
    {
        var host = new ProviderHost();
        var provider = host.Provider<ApplicationsProvider>(Type);
        var instance = await InstanceFactory.WithPropertiesAsync(host, Templates.Resource(Type, Name, "{ \"displayName\": \"Race\" }"), properties);
        host.ClearRequests();
        return (host, provider, instance);
    }

    private static JsonObject UpsertBody(ProviderHost host)
    {
        return (JsonObject)host.Fake.Writes.Single(w => w.Method == "PATCH" && w.Path.Contains("uniqueName", StringComparison.Ordinal)).Body!;
    }

    [Fact]
    public async Task AnApplicationThatAppearedAfterTheRead_KeepsItsSignInAudience_OnlyTheDeclaredPropertiesAreMerged()
    {
        var (host, provider, instance) = await ArrangeAsync("{ \"displayName\": \"Race renamed\" }");
        host.Directory.AddApplication(Name, "Race", configure: app => app["signInAudience"] = "AzureADMultipleOrgs");

        var applied = await provider.CreateAsync(instance, host.Fake, ProviderHost.Ct);

        var live = host.Directory.Find(Collection, "uniqueName", Name)!;
        Assert.Equal("AzureADMultipleOrgs", live["signInAudience"]!.GetValue<string>());
        Assert.Equal("Race renamed", live["displayName"]!.GetValue<string>());
        Assert.Null(JsonObjects.FindKey(UpsertBody(host), "signInAudience"));
        Assert.Single(host.Fake.Writes);
        Assert.Equal(live["id"]!.GetValue<string>(), applied.GraphId);
    }

    [Fact]
    public async Task ARealCreate_HasTheDefaultSignInAudience_WithoutASecondWrite_WhenGraphAppliesItItself()
    {
        var (host, provider, instance) = await ArrangeAsync("{ \"displayName\": \"Race\" }");

        await provider.CreateAsync(instance, host.Fake, ProviderHost.Ct);

        Assert.Equal("AzureADMyOrg", host.Directory.Find(Collection, "uniqueName", Name)!["signInAudience"]!.GetValue<string>());
        Assert.Null(JsonObjects.FindKey(UpsertBody(host), "signInAudience"));
        Assert.Single(host.Fake.Writes);
    }

    [Fact]
    public async Task ARealCreate_WhereGraphAppliedAnotherAudience_IsPatchedToTheDefault_RightAfterTheCreate()
    {
        var (host, provider, instance) = await ArrangeAsync("{ \"displayName\": \"Race\" }");
        var graph = new InterceptingGraphClient(host.Fake)
        {
            PatchOverride = (request, body) =>
            {
                if (!request.Path.Contains("uniqueName", StringComparison.Ordinal))
                {
                    return null;
                }

                var response = host.Fake.PatchAsync(request, body, ProviderHost.Ct).GetAwaiter().GetResult();
                host.Directory.Find(Collection, "uniqueName", Name)!["signInAudience"] = "AzureADandPersonalMicrosoftAccount";
                ((JsonObject)response.Body!)["signInAudience"] = "AzureADandPersonalMicrosoftAccount";
                return response;
            },
        };

        var applied = await provider.CreateAsync(instance, graph, ProviderHost.Ct);

        var live = host.Directory.Find(Collection, "uniqueName", Name)!;
        Assert.Equal("AzureADMyOrg", live["signInAudience"]!.GetValue<string>());
        var second = host.Fake.Writes.Single(w => w.Method == "PATCH" && w.Path == "/applications/" + applied.GraphId);
        Assert.Equal("AzureADMyOrg", second.Body!["signInAudience"]!.GetValue<string>());
        Assert.Equal(RetryHint.ExpectReplicationLag, second.Hint);
        Assert.Equal("AzureADMyOrg", applied.Live!["signInAudience"]!.GetValue<string>());
    }

    [Fact]
    public async Task ADeclaredSignInAudience_IsPartOfTheUpsert_AndIsMergedIntoAnApplicationThatAppearedAfterTheRead()
    {
        var (host, provider, instance) = await ArrangeAsync("{ \"displayName\": \"Race\", \"signInAudience\": \"AzureADMyOrg\" }");
        host.Directory.AddApplication(Name, "Race", configure: app => app["signInAudience"] = "AzureADMultipleOrgs");

        await provider.CreateAsync(instance, host.Fake, ProviderHost.Ct);

        Assert.Equal("AzureADMyOrg", UpsertBody(host)["signInAudience"]!.GetValue<string>());
        Assert.Equal("AzureADMyOrg", host.Directory.Find(Collection, "uniqueName", Name)!["signInAudience"]!.GetValue<string>());
    }

    [Fact]
    public async Task AFailedDefaultWrite_AfterARealCreate_HandsTheCreatedApplicationOver()
    {
        var (host, provider, instance) = await ArrangeAsync("{ \"displayName\": \"Race\" }");
        var graph = new InterceptingGraphClient(host.Fake)
        {
            PatchOverride = (request, body) =>
            {
                if (!request.Path.Contains("uniqueName", StringComparison.Ordinal))
                {
                    throw new GraphException(HttpStatusCode.Forbidden, "Authorization_RequestDenied", "Insufficient privileges", request.Hint);
                }

                var response = host.Fake.PatchAsync(request, body, ProviderHost.Ct).GetAwaiter().GetResult();
                ((JsonObject)response.Body!).Remove("signInAudience");
                return response;
            },
        };

        var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.CreateAsync(instance, graph, ProviderHost.Ct));

        Assert.NotNull(ex.Partial);
        Assert.Contains("signInAudience", ex.Message, StringComparison.Ordinal);
    }
}
