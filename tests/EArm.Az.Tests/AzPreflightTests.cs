using System.Threading;
using System.Threading.Tasks;
using EArm.Az;
using EArm.Az.Tests.Infrastructure;
using Xunit;

namespace EArm.Az.Tests;

/// <summary>README item 13: <see cref="AzPreflight"/> over the fake az.</summary>
public sealed class AzPreflightTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task HappyPath_PopulatesTheResultFromVersionAccountAndOrganization()
    {
        using var context = new FakeAzContext(nameof(HappyPath_PopulatesTheResultFromVersionAccountAndOrganization));
        context.UseFixtureFile("preflight-ok.json");
        using var runner = context.CreateRunner();
        var preflight = new AzPreflight(runner, context.CreateGraphClient(runner, new RecordingDelayProvider()), context.Logger<AzPreflight>());

        var result = await preflight.RunAsync(Ct);

        Assert.Equal(Tenant, result.TenantId);
        Assert.Equal(Tenant, result.OrganizationId);
        Assert.Equal("alice@contoso.example", result.UserName);
        Assert.Equal("user", result.UserType);
        Assert.Equal("2.70.0", result.AzVersion);
        Assert.Equal("AzureCloud", result.CloudName);
        Assert.Equal("22222222-2222-2222-2222-222222222222", result.SubscriptionId);
        Assert.Equal("Contoso Subscription", result.SubscriptionName);

        var records = context.ReadRecords();
        Assert.Equal(3, records.Count);
        Assert.Equal(["version", "--only-show-errors", "-o", "json"], records[0].Argv);
        Assert.Equal(["account", "show", "--only-show-errors", "-o", "json"], records[1].Argv);
        Assert.Equal(["rest", "--method", "GET", "--url", "https://graph.microsoft.com/v1.0/organization", "--url-parameters", "$select=id", "--only-show-errors", "-o", "json"], records[2].Argv);

        Assert.Single(context.Log.Entries, e => e.EventId.Id == 1040);
        Assert.DoesNotContain(context.Log.Entries, e => e.EventId.Id == 1041);
    }

    [Fact]
    public async Task NotLoggedIn_FromAccountShow_CarriesTheLoginHint()
    {
        using var context = new FakeAzContext(nameof(NotLoggedIn_FromAccountShow_CarriesTheLoginHint));
        context.UseFixtureFile("preflight-not-logged-in.json");
        using var runner = context.CreateRunner();
        var preflight = new AzPreflight(runner, context.CreateGraphClient(runner, new RecordingDelayProvider()), context.Logger<AzPreflight>());

        var error = await Assert.ThrowsAsync<AzCliException>(() => preflight.RunAsync(Ct));

        Assert.Equal(AzCliFailure.NotLoggedIn, error.Failure);
        Assert.Contains("az login --allow-no-subscriptions", error.Message);
        Assert.Equal(1, error.ExitCode);
        Assert.Equal(2, context.ReadRecords().Count);
    }

    [Fact]
    public async Task AccountShowFailingForAnUnknownReason_IsReportedAsNotLoggedIn()
    {
        using var context = new FakeAzContext(nameof(AccountShowFailingForAnUnknownReason_IsReportedAsNotLoggedIn));
        context.UseScenario(new FakeScenario()
            .Add(FakeResponse.Json("{\"azure-cli\":\"2.70.0\"}").When(argvContains: "version"))
            .Add(FakeResponse.Error("ERROR: The command failed with an unexpected error.", exitCode: 1).When(argvContains: "account show")));
        using var runner = context.CreateRunner();
        var preflight = new AzPreflight(runner, context.CreateGraphClient(runner, new RecordingDelayProvider()), context.Logger<AzPreflight>());

        var error = await Assert.ThrowsAsync<AzCliException>(() => preflight.RunAsync(Ct));

        Assert.Equal(AzCliFailure.NotLoggedIn, error.Failure);
        Assert.Contains("az login --allow-no-subscriptions", error.Message);
    }

    [Fact]
    public async Task GraphRejectingTheToken_IsReportedAsNotLoggedInAfterOneRefresh()
    {
        using var context = new FakeAzContext(nameof(GraphRejectingTheToken_IsReportedAsNotLoggedInAfterOneRefresh));
        context.UseScenario(new FakeScenario()
            .Add(FakeResponse.Json("{\"azure-cli\":\"2.70.0\"}").When(argvContains: "version"))
            .Add(FakeResponse.Json("{\"tenantId\":\"" + Tenant + "\",\"environmentName\":\"AzureCloud\",\"user\":{\"name\":\"sp-1\",\"type\":\"servicePrincipal\"}}").When(argvContains: "account show"))
            .Add(FakeResponse.GraphError("unauthorized").When(method: "GET", urlContains: "/organization"))
            .Add(FakeResponse.Json("{\"accessToken\":\"t\",\"expiresOn\":\"2026-10-14 11:00:00.000000\"}").When(argvContains: "get-access-token"))
            .Add(FakeResponse.GraphError("unauthorized").When(method: "GET", urlContains: "/organization")));
        using var runner = context.CreateRunner();
        var preflight = new AzPreflight(runner, context.CreateGraphClient(runner, new RecordingDelayProvider()), context.Logger<AzPreflight>());

        var error = await Assert.ThrowsAsync<AzCliException>(() => preflight.RunAsync(Ct));

        Assert.Equal(AzCliFailure.NotLoggedIn, error.Failure);
        Assert.Contains("az login --allow-no-subscriptions", error.Message);
        Assert.Contains("InvalidAuthenticationToken", error.Message);
        var inner = Assert.IsType<GraphException>(error.InnerException);
        Assert.True(inner.IsUnauthorized);
        Assert.Equal(5, context.ReadRecords().Count);
    }

    [Fact]
    public async Task OrganizationWithoutId_IsAnUnknownAzFailure()
    {
        using var context = new FakeAzContext(nameof(OrganizationWithoutId_IsAnUnknownAzFailure));
        context.UseScenario(new FakeScenario()
            .Add(FakeResponse.Json("{\"azure-cli\":\"2.70.0\"}").When(argvContains: "version"))
            .Add(FakeResponse.Json("{\"tenantId\":\"" + Tenant + "\"}").When(argvContains: "account show"))
            .Add(FakeResponse.Json("{\"value\":[]}").When(method: "GET", urlContains: "/organization")));
        using var runner = context.CreateRunner();
        var preflight = new AzPreflight(runner, context.CreateGraphClient(runner, new RecordingDelayProvider()), context.Logger<AzPreflight>());

        var error = await Assert.ThrowsAsync<AzCliException>(() => preflight.RunAsync(Ct));

        Assert.Equal(AzCliFailure.Unknown, error.Failure);
        Assert.Contains("Organization.Read.All", error.Message);
    }
}
