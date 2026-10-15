using System;
using System.Text.Json.Nodes;
using EArm.Engine.Planning;
using EArm.Graph;
using Xunit;

namespace EArm.Tests.Engine;

/// <summary>What <c>tenant()</c> and <c>environment()</c> are built from after the preflight: the one organization read and the cloud table.</summary>
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
}
