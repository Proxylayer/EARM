using System;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Engine.Planning;
using EArm.Graph;
using EArm.Graph.Auth;
using Xunit;

namespace EArm.Tests.Engine;

/// <summary>What <c>tenant()</c>, <c>deployer()</c> and <c>environment()</c> are built from after the preflight: the one organization read, the signed-in principal and the cloud table.</summary>
public sealed class DeploymentEnvironmentTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";

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

        return organization;
    }

    [Fact]
    public void Tenant_IsBuiltFromTheOrganizationTheDefaultDomainAndTheDisplayName()
    {
        var tenant = DeploymentEnvironment.TenantFromOrganization(Organization("Contoso", ("contoso.onmicrosoft.com", false), ("contoso.com", true)), Tenant);

        Assert.NotNull(tenant);
        Assert.Equal(Tenant, tenant["tenantId"]!.GetValue<string>());
        Assert.Equal("contoso.com", tenant["defaultDomain"]!.GetValue<string>());
        Assert.Equal("Contoso", tenant["displayName"]!.GetValue<string>());
    }

    [Fact]
    public void Tenant_PrefersTheDefaultDomain_ElseTheFirst()
    {
        var preferred = DeploymentEnvironment.TenantFromOrganization(Organization("Contoso", ("other.onmicrosoft.com", false), ("contoso.com", true), ("third.com", false)), Tenant);
        var first = DeploymentEnvironment.TenantFromOrganization(Organization("Contoso", ("a.com", null), ("b.com", false)), Tenant);
        var none = DeploymentEnvironment.TenantFromOrganization(Organization(null), Tenant);

        Assert.Equal("contoso.com", preferred!["defaultDomain"]!.GetValue<string>());
        Assert.Equal("a.com", first!["defaultDomain"]!.GetValue<string>());
        Assert.Null(none!["defaultDomain"]);
        Assert.Null(none["displayName"]);
        Assert.Equal(Tenant, none["tenantId"]!.GetValue<string>());
    }

    [Fact]
    public void Tenant_FallsBackToThePreflightTenantId_WhenTheObjectHasNone()
    {
        var tenant = DeploymentEnvironment.TenantFromOrganization(new JsonObject { ["displayName"] = "X" }, Tenant);

        Assert.Equal(Tenant, tenant!["tenantId"]!.GetValue<string>());
        Assert.Equal("X", tenant["displayName"]!.GetValue<string>());
    }

    [Fact]
    public void Tenant_IsNull_WhenTheOrganizationCouldNotBeRead()
    {
        Assert.Null(DeploymentEnvironment.TenantFromOrganization(null, Tenant));
        Assert.Throws<ArgumentException>(() => DeploymentEnvironment.TenantFromOrganization(null, " "));
    }

    [Theory]
    [InlineData("public", "AzureCloud", "https://graph.microsoft.com", "https://login.microsoftonline.com/")]
    [InlineData("usgov", "AzureUSGovernment", "https://graph.microsoft.us", "https://login.microsoftonline.us/")]
    [InlineData("usgovdod", "AzureUSGovernmentDoD", "https://dod-graph.microsoft.us", "https://login.microsoftonline.us/")]
    [InlineData("china", "AzureChinaCloud", "https://microsoftgraph.chinacloudapi.cn", "https://login.chinacloudapi.cn/")]
    public void Environment_ComesFromTheCloudTable(string cloud, string expectedName, string graph, string loginEndpoint)
    {
        var environment = DeploymentEnvironment.Environment(GraphCloud.Parse(cloud));

        Assert.Equal(expectedName, environment["name"]!.GetValue<string>());
        Assert.Equal(graph, environment["graph"]!.GetValue<string>());
        Assert.Equal(loginEndpoint, environment["authentication"]!["loginEndpoint"]!.GetValue<string>());
    }

    [Fact]
    public void Environment_OfThePublicCloud_MatchesTheDocumentedConstants()
    {
        var environment = DeploymentEnvironment.Environment(GraphCloud.Public);

        Assert.Equal(DeploymentEnvironment.DefaultCloudName, environment["name"]!.GetValue<string>());
        Assert.Equal(DeploymentEnvironment.PublicLoginEndpoint, environment["authentication"]!["loginEndpoint"]!.GetValue<string>());
        Assert.Equal(GraphApiExtensions.Host, environment["graph"]!.GetValue<string>());
    }

    [Fact]
    public void Tenant_HasArmsShape_PlusTheDefaultDomain()
    {
        var organization = Organization("Contoso", ("contoso.com", true));
        organization["countryLetterCode"] = "US";

        var tenant = DeploymentEnvironment.TenantFromOrganization(organization, Tenant)!;

        Assert.Equal("US", tenant["countryCode"]!.GetValue<string>());
        Assert.Equal("Contoso", tenant["displayName"]!.GetValue<string>());
        Assert.Equal("/tenants/" + Tenant, tenant["id"]!.GetValue<string>());
        Assert.Equal(Tenant, tenant["tenantId"]!.GetValue<string>());
        Assert.Equal("contoso.com", tenant["defaultDomain"]!.GetValue<string>());
        Assert.Null(DeploymentEnvironment.TenantFromOrganization(Organization("Contoso"), Tenant)!["countryCode"]);
    }

    [Theory]
    [InlineData("public", "https://management.azure.com/", "https://portal.azure.com", "https://management.core.windows.net/", ".vault.azure.net")]
    [InlineData("usgov", "https://management.usgovcloudapi.net/", "https://portal.azure.us", "https://management.core.usgovcloudapi.net/", ".vault.usgovcloudapi.net")]
    [InlineData("usgovdod", "https://management.usgovcloudapi.net/", "https://portal.azure.us", "https://management.core.usgovcloudapi.net/", ".vault.usgovcloudapi.net")]
    [InlineData("china", "https://management.chinacloudapi.cn/", "https://portal.azure.cn", "https://management.core.chinacloudapi.cn/", ".vault.azure.cn")]
    public void Environment_CarriesTheArmFieldsOfTheCloud(string cloud, string resourceManager, string portal, string firstAudience, string keyVaultDns)
    {
        var environment = DeploymentEnvironment.Environment(GraphCloud.Parse(cloud));

        Assert.Equal(resourceManager, environment["resourceManager"]!.GetValue<string>());
        Assert.Equal(portal, environment["portal"]!.GetValue<string>());
        var audiences = Assert.IsType<JsonArray>(environment["authentication"]!["audiences"]);
        Assert.Equal(firstAudience, audiences[0]!.GetValue<string>());
        Assert.Equal(resourceManager, audiences[1]!.GetValue<string>());
        Assert.Equal("common", environment["authentication"]!["tenant"]!.GetValue<string>());
        Assert.Equal("AAD", environment["authentication"]!["identityProvider"]!.GetValue<string>());
        Assert.Equal(keyVaultDns, environment["suffixes"]!["keyvaultDns"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("public", "https://management.azure.com/", "https://management.core.windows.net/")]
    [InlineData("usgov", "https://management.usgovcloudapi.net/", "https://management.core.usgovcloudapi.net/")]
    [InlineData("usgovdod", "https://management.usgovcloudapi.net/", "https://management.core.usgovcloudapi.net/")]
    [InlineData("china", "https://management.chinacloudapi.cn/", "https://management.core.chinacloudapi.cn/")]
    public void Environment_ResourceManagerAndAudiences_AreUnchanged_AndFollowTheCloudsResourceManagerOrigin(string cloud, string resourceManager, string classicAudience)
    {
        var graphCloud = GraphCloud.Parse(cloud);

        var environment = DeploymentEnvironment.Environment(graphCloud);

        Assert.Equal(resourceManager, environment["resourceManager"]!.GetValue<string>());
        Assert.Equal(graphCloud.ResourceManagerOrigin + "/", environment["resourceManager"]!.GetValue<string>());
        var audiences = Assert.IsType<JsonArray>(environment["authentication"]!["audiences"]);
        Assert.Equal([classicAudience, resourceManager], audiences.Select(static a => a!.GetValue<string>()));
    }

    [Fact]
    public void Deployer_OfAUser_HasTheUserPrincipalName()
    {
        var preflight = new GraphPreflightResult(Tenant, null, "aaaaaaaa-0000-1111-2222-bbbbbbbbbbbb", "john.doe@contoso.com", "user", null, GraphCloud.Public, GraphAuthMode.Auto);

        var deployer = DeploymentEnvironment.Deployer(preflight)!;

        Assert.Equal("aaaaaaaa-0000-1111-2222-bbbbbbbbbbbb", deployer["objectId"]!.GetValue<string>());
        Assert.Equal(Tenant, deployer["tenantId"]!.GetValue<string>());
        Assert.Equal("john.doe@contoso.com", deployer["userPrincipalName"]!.GetValue<string>());
    }

    [Fact]
    public void Deployer_OfAServicePrincipal_HasAnEmptyUserPrincipalName_AndNoObjectIdMeansUnknown()
    {
        var app = new GraphPreflightResult(Tenant, null, "cccccccc-0000-1111-2222-dddddddddddd", "00000000-1111-2222-3333-444444444444", "application", "00000000-1111-2222-3333-444444444444", GraphCloud.Public, GraphAuthMode.Auto);
        var noClaims = new GraphPreflightResult(Tenant, null, null, null, "user", null, GraphCloud.Public, GraphAuthMode.Auto);

        var deployer = DeploymentEnvironment.Deployer(app)!;

        Assert.Equal("cccccccc-0000-1111-2222-dddddddddddd", deployer["objectId"]!.GetValue<string>());
        Assert.Equal(string.Empty, deployer["userPrincipalName"]!.GetValue<string>());
        Assert.Null(DeploymentEnvironment.Deployer(noClaims));
    }
}
