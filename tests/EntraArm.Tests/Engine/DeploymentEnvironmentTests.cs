using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EntraArm.Az;
using EntraArm.Engine.Planning;
using EntraArm.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EntraArm.Tests.Engine;

/// <summary>What <c>tenant()</c> and <c>environment()</c> are built from after preflight (WI-16).</summary>
public sealed class DeploymentEnvironmentTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Answers every GET with the same body (or throws).</summary>
    private sealed class StubGraphClient : GraphClientDecorator
    {
        private readonly JsonNode? _body;

        public StubGraphClient(JsonNode? body)
            : base(new FakeGraphClient())
        {
            _body = body;
        }

        public override Task<JsonNode?> GetAsync(GraphRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_body?.DeepClone());
        }
    }

    private static JsonObject Organization(string? displayName, params (string Name, bool? IsDefault)[] domains)
    {
        var verified = new JsonArray();
        foreach (var (name, isDefault) in domains)
        {
            var domain = new JsonObject { ["name"] = name };
            if (isDefault is { } flag)
            {
                domain["isDefault"] = flag;
            }

            verified.Add(domain);
        }

        var organization = new JsonObject { ["id"] = Tenant, ["verifiedDomains"] = verified };
        if (displayName is not null)
        {
            organization["displayName"] = displayName;
        }

        return new JsonObject { ["value"] = new JsonArray(organization) };
    }

    [Fact]
    public async Task ReadTenant_UsesTheFakeOrganization_AndSelectsTheRightFields()
    {
        var fake = new FakeGraphClient();

        var tenant = await DeploymentEnvironment.ReadTenantAsync(fake, Tenant, NullLogger.Instance, Ct);

        Assert.NotNull(tenant);
        Assert.Equal(Tenant, tenant["tenantId"]!.GetValue<string>());
        Assert.Equal("contoso.com", tenant["defaultDomain"]!.GetValue<string>());
        Assert.Equal("Contoso (fake)", tenant["displayName"]!.GetValue<string>());
        var request = Assert.Single(fake.Requests);
        Assert.Equal("/organization", request.Path);
        Assert.Equal("id,displayName,verifiedDomains", request.UrlParameters!["$select"]);
        Assert.Equal(RetryHint.Default, request.Hint);
    }

    [Fact]
    public async Task ReadTenant_PrefersTheDefaultDomain_ElseTheFirst()
    {
        var preferred = await DeploymentEnvironment.ReadTenantAsync(new StubGraphClient(Organization("Contoso", ("other.onmicrosoft.com", false), ("contoso.com", true), ("third.com", false))), Tenant, NullLogger.Instance, Ct);
        var first = await DeploymentEnvironment.ReadTenantAsync(new StubGraphClient(Organization("Contoso", ("a.com", null), ("b.com", false))), Tenant, NullLogger.Instance, Ct);
        var none = await DeploymentEnvironment.ReadTenantAsync(new StubGraphClient(Organization(null)), Tenant, NullLogger.Instance, Ct);

        Assert.Equal("contoso.com", preferred!["defaultDomain"]!.GetValue<string>());
        Assert.Equal("a.com", first!["defaultDomain"]!.GetValue<string>());
        Assert.Null(none!["defaultDomain"]);
        Assert.Null(none["displayName"]);
        Assert.Equal(Tenant, none["tenantId"]!.GetValue<string>());
    }

    [Fact]
    public async Task ReadTenant_FallsBackToThePreflightTenantId_WhenTheObjectHasNone()
    {
        var body = new JsonObject { ["value"] = new JsonArray(new JsonObject { ["displayName"] = "X" }) };

        var tenant = await DeploymentEnvironment.ReadTenantAsync(new StubGraphClient(body), Tenant, NullLogger.Instance, Ct);

        Assert.Equal(Tenant, tenant!["tenantId"]!.GetValue<string>());
        Assert.Equal("X", tenant["displayName"]!.GetValue<string>());
    }

    [Fact]
    public async Task ReadTenant_ReturnsNull_WhenTheReadFailsOrYieldsNoOrganization()
    {
        var fake = new FakeGraphClient();
        fake.Failures.Forbid("/organization");

        Assert.Null(await DeploymentEnvironment.ReadTenantAsync(fake, Tenant, NullLogger.Instance, Ct));
        Assert.Null(await DeploymentEnvironment.ReadTenantAsync(new StubGraphClient(new JsonObject { ["value"] = new JsonArray() }), Tenant, NullLogger.Instance, Ct));
        Assert.Null(await DeploymentEnvironment.ReadTenantAsync(new StubGraphClient(null), Tenant, NullLogger.Instance, Ct));
        Assert.Null(await DeploymentEnvironment.ReadTenantAsync(new StubGraphClient(new JsonArray()), Tenant, NullLogger.Instance, Ct));
    }

    [Theory]
    [InlineData(null, "AzureCloud")]
    [InlineData("", "AzureCloud")]
    [InlineData("AzureCloud", "AzureCloud")]
    [InlineData("AzureUSGovernment", "AzureUSGovernment")]
    public void Environment_IsThePublicCloudConstants_NamedAfterTheSession(string? cloudName, string expectedName)
    {
        var environment = DeploymentEnvironment.Environment(cloudName);

        Assert.Equal(expectedName, environment["name"]!.GetValue<string>());
        Assert.Equal("https://graph.microsoft.com", environment["graph"]!.GetValue<string>());
        Assert.Equal("https://login.microsoftonline.com/", environment["authentication"]!["loginEndpoint"]!.GetValue<string>());
        Assert.Equal(DeploymentEnvironment.PublicLoginEndpoint, environment["authentication"]!["loginEndpoint"]!.GetValue<string>());
    }
}
