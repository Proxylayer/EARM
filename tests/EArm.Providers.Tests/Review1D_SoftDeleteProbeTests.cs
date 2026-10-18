using System;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Engine.Providers;
using EArm.Graph;
using EArm.Providers.Tests.Infrastructure;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// Review 1D / G-02, G-13, G-14: the <c>/directory/deletedItems</c> probe before a create needs the type's read permission (Learn:
/// directory-deleteditems-list; <c>Application.Read.All</c> for applications, which <c>Application.ReadWrite.OwnedBy</c> does not carry).
/// A 403 (or a 400 that the listing repeats) skips the probe with a <c>SoftDeleteProbeSkipped</c> warning and the create continues; the
/// service principal probe and restore exist like the applications' one.
/// </summary>
public sealed class Review1D_SoftDeleteProbeTests
{
    private const string AppId = "5b5b5b5b-1111-4222-8333-444444444444";

    private static async Task<AppliedResource> CreateDirectAsync(ProviderHost host, string type, string name, string properties)
    {
        var whatIf = await host.WhatIfAsync(host.Resources(Templates.Resource(type, name, properties)));
        var instance = whatIf.Plan.Instances.Single(i => ResourceIdentifier.TypeEquals(i.Type, type));
        var provider = host.Registry.Get(type);
        host.ClearRequests();
        return await provider.CreateAsync(instance, host.Fake, ProviderHost.Ct);
    }

    public static TheoryData<string, string, string, string> Types => new()
    {
        { ApplicationsProvider.ResourceType, "probe-app", """{ "displayName": "Probe" }""", "application" },
        { UsersProvider.ResourceType, "probe@contoso.com", """{ "displayName": "Probe" }""", "user" },
        { "Microsoft.Entra/groups", "probe-m365", """{ "displayName": "Probe", "mailEnabled": true, "securityEnabled": false, "groupTypes": ["Unified"], "mailNickname": "probem365" }""", "group" },
        { AdministrativeUnitsProvider.ResourceType, "Probe AU", """{ "description": "x" }""", "administrativeUnit" },
    };

    [Theory]
    [MemberData(nameof(Types))]
    public async Task Probe403_IsSkippedWithAWarning_AndTheCreateContinues(string type, string name, string properties, string what)
    {
        var host = new ProviderHost();
        host.Fake.Failures.Forbid("deletedItems", method: "GET");

        var applied = await CreateDirectAsync(host, type, name, properties);

        // (a Microsoft 365 group without owners also reports GroupHasNoOwnerAtCreate)
        var warning = Assert.Single(applied.Warnings, w => w.Code == ProviderWarnings.SoftDeleteProbeSkipped);
        Assert.Contains("microsoft.graph." + what, warning.Message, StringComparison.Ordinal);
        Assert.Contains(host.Fake.Requests, r => r.Method == "GET" && r.Path.Contains("deletedItems", StringComparison.Ordinal));
        Assert.Contains(host.Fake.Requests, r => r.IsWrite);
        Assert.False(string.IsNullOrEmpty(applied.GraphId));
    }

    [Theory]
    [MemberData(nameof(Types))]
    public async Task ProbeThatWorks_AddsNoWarning(string type, string name, string properties, string what)
    {
        _ = what;
        var host = new ProviderHost();

        var applied = await CreateDirectAsync(host, type, name, properties);

        Assert.DoesNotContain(applied.Warnings, w => w.Code == ProviderWarnings.SoftDeleteProbeSkipped);
    }

    [Fact]
    public async Task ApplicationProbe_400ThenAListingThatIsForbidden_IsSkippedToo()
    {
        var host = new ProviderHost();
        host.Fake.Failures.Add("GET", "deletedItems", request => new GraphException(HttpStatusCode.BadRequest, "Request_UnsupportedQuery", "unsupported filter", request.Hint), times: 1);
        host.Fake.Failures.Forbid("deletedItems", method: "GET");

        var applied = await CreateDirectAsync(host, ApplicationsProvider.ResourceType, "probe-app", """{ "displayName": "Probe" }""");

        Assert.Equal(ProviderWarnings.SoftDeleteProbeSkipped, Assert.Single(applied.Warnings).Code);
        Assert.Equal(2, host.Fake.Requests.Count(r => r.Path.Contains("deletedItems", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ApplicationProbe_400ThenAWorkingListing_FindsTheSoftDeletedHolder()
    {
        var host = new ProviderHost();
        host.Directory.AddDeleted("/applications", new JsonObject { ["uniqueName"] = "held-app", ["displayName"] = "Old" });
        host.Fake.Failures.Add("GET", "deletedItems", request => new GraphException(HttpStatusCode.BadRequest, "Request_UnsupportedQuery", "unsupported filter", request.Hint), times: 1);

        var run = await host.CreateAsync(host.Resources(Templates.Resource(ApplicationsProvider.ResourceType, "held-app", """{ "displayName": "New" }""")));

        Assert.False(run.Succeeded);
        Assert.Contains("soft-deleted application", run.Errors, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecutorRun_WithAForbiddenProbe_StillCreatesTheApplication()
    {
        var host = new ProviderHost();
        host.Fake.Failures.Forbid("deletedItems", method: "GET");

        var run = await host.CreateAsync(host.Resources(Templates.Resource(ApplicationsProvider.ResourceType, "owned-only-app", """{ "displayName": "Owned only" }""")));

        Check.Succeeded(run);
        Assert.NotNull(host.Directory.Find("/applications", "uniqueName", "owned-only-app"));
    }

    // ---- service principals: probe + restore (G-14) ----

    private static string ServicePrincipal(string properties = "{ }")
    {
        return Templates.Resource(ServicePrincipalsProvider.ResourceType, AppId, properties);
    }

    [Fact]
    public async Task ServicePrincipal_ASoftDeletedHolder_IsReported_UnlessRestoreIsRequested()
    {
        var host = new ProviderHost();
        host.Directory.AddApplication("sp-app", "SP app", AppId);
        var deleted = host.Directory.AddDeleted("/servicePrincipals", new JsonObject { ["appId"] = AppId, ["displayName"] = "SP app", ["accountEnabled"] = true, ["servicePrincipalType"] = "Application" });

        var refused = await host.CreateAsync(host.Resources(ServicePrincipal()));

        Assert.False(refused.Succeeded);
        Assert.Contains("soft-deleted service principal", refused.Errors, StringComparison.Ordinal);
        Assert.Contains(deleted["id"]!.GetValue<string>(), refused.Errors, StringComparison.Ordinal);
        Assert.Empty(host.Directory.Objects("/servicePrincipals"));

        var restoring = new ProviderHost { OnSoftDeletedConflict = SoftDeletedConflict.Restore };
        restoring.Directory.AddApplication("sp-app", "SP app", AppId);
        var deletedForRestore = restoring.Directory.AddDeleted("/servicePrincipals", new JsonObject { ["appId"] = AppId, ["displayName"] = "SP app", ["accountEnabled"] = true, ["servicePrincipalType"] = "Application" });

        var restored = await restoring.CreateAsync(restoring.Resources(ServicePrincipal()));

        Check.Succeeded(restored);
        Assert.Equal(deletedForRestore["id"]!.GetValue<string>(), restored.OpOfType(ServicePrincipalsProvider.ResourceType).GraphId);
        Assert.Contains(restored.Writes, w => w.Method == "POST" && w.Path.EndsWith("/restore", StringComparison.Ordinal));
        Assert.DoesNotContain(restored.Writes, w => w.Method == "POST" && w.Path == "/servicePrincipals");
        Assert.Single(restoring.Directory.Objects("/servicePrincipals"));
    }

    [Fact]
    public async Task ServicePrincipal_AForbiddenProbe_IsSkippedWithAWarning()
    {
        var host = new ProviderHost();
        host.Directory.AddApplication("sp-app", "SP app", AppId);
        host.Fake.Failures.Forbid("deletedItems", method: "GET");

        var applied = await CreateDirectAsync(host, ServicePrincipalsProvider.ResourceType, AppId, "{ }");

        Assert.Equal(ProviderWarnings.SoftDeleteProbeSkipped, Assert.Single(applied.Warnings).Code);
        Assert.Single(host.Directory.Objects("/servicePrincipals"));
    }
}
