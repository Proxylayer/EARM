using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Engine.Providers;
using EArm.Providers.Tests.Infrastructure;
using EArm.TestSupport;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>Claims mapping policies and the ensure-only <c>claimsMappingPolicies</c> edge of service principals (the STS base is covered in depth by the token lifetime tests).</summary>
public sealed class ClaimsMappingPoliciesProviderTests
{
    private const string PolicyType = ClaimsMappingPoliciesProvider.ResourceType;
    private const string PolicyPath = FakeDirectory.ClaimsMappingPoliciesPath;
    private const string Document = "{ \"ClaimsMappingPolicy\": { \"Version\": 1, \"IncludeBasicClaimSet\": \"true\", \"ClaimsSchema\": [ { \"Source\": \"user\", \"ID\": \"employeeid\", \"JwtClaimType\": \"employeeid\" } ] } }";

    private static string Policy(string name = "Employee id", string document = Document, string extra = "")
    {
        return "{ \"type\": \"" + PolicyType + "\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"" + name + "\", \"properties\": { \"definition\": [ " + document + " ]" + extra + " } }";
    }

    private static string ServicePrincipal(string appId, string policies)
    {
        return "{ \"type\": \"Microsoft.Entra/servicePrincipals\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"" + appId + "\", \"properties\": { \"claimsMappingPolicies\": [ " + policies + " ] } }";
    }

    private static IReadOnlyList<FakeGraphRequest> RefWrites(Run run)
    {
        return run.Writes.Where(w => w.Path.Contains("/claimsMappingPolicies/", StringComparison.Ordinal) && w.Path.EndsWith("$ref", StringComparison.Ordinal)).ToList();
    }

    [Fact]
    public async Task Create_ThenRerun_IsNoChange_AndAModifiedDocumentIsOnePatch()
    {
        var host = new ProviderHost();

        var run = await host.CreateAsync(host.Resources(Policy()));

        Check.Succeeded(run);
        Assert.Equal("POST", Assert.Single(run.Writes).Method);
        var rerun = await host.CreateAsync(host.Resources(Policy()));
        Assert.All(rerun.Executed.Operations, o => Assert.Equal(ChangeType.NoChange, o.ChangeType));
        Check.NoWrites(rerun);

        var changed = await host.CreateAsync(host.Resources(Policy(document: Document.Replace("\"true\"", "\"false\"", StringComparison.Ordinal))));
        Check.Succeeded(changed);
        var patch = Assert.Single(changed.Writes);
        Assert.Equal("PATCH", patch.Method);
        Assert.Equal(PolicyPath + "/" + host.IdOf(PolicyPath, "displayName", "Employee id"), patch.Path);
        Assert.Contains("\"IncludeBasicClaimSet\":\"false\"", patch.Body!["definition"]![0]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task IsOrganizationDefaultTrue_IsAnUnsupportedRow()
    {
        var host = new ProviderHost();

        var whatIf = await host.WhatIfAsync(host.Resources(Policy(extra: ", \"isOrganizationDefault\": true")));

        var row = Assert.Single(whatIf.Plan.Changes);
        Assert.True(row.HasError);
        Assert.Contains("service principals only", row.UnsupportedReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoPoliciesWithTheName_AreAnAmbiguousMatch()
    {
        var host = new ProviderHost();
        foreach (int _ in new[] { 1, 2 })
        {
            host.Directory.Add(PolicyPath, new JsonObject { ["displayName"] = "Employee id", ["definition"] = new JsonArray("{\"ClaimsMappingPolicy\":{\"Version\":1}}") });
        }

        var key = new ResourceKey { Type = PolicyType, Values = new Dictionary<string, string> { ["displayName"] = "Employee id" } };
        var ex = await Assert.ThrowsAsync<ProviderException>(() => host.Provider(PolicyType).ReadAsync(key, host.Fake, ProviderHost.Ct));

        Assert.Equal(ProviderException.AmbiguousMatch, ex.Code);
    }

    [Fact]
    public async Task Delete_RemovesThePolicy()
    {
        var host = new ProviderHost();
        await host.CreateAsync(host.Resources(Policy()));

        var result = await host.DeleteDeclaredAsync(host.Resources(Policy()));

        Assert.True(result.Succeeded);
        Assert.Empty(host.Directory.Objects(PolicyPath));
    }

    [Fact]
    public async Task AServicePrincipal_TakesBothDeclaredPolicies_Once()
    {
        var host = new ProviderHost();
        var app = host.Directory.AddApplication("api-app", "API");
        string appId = app["appId"]!.GetValue<string>();
        string template = Policy() + ", " + Policy("Department") + ", " + ServicePrincipal(appId, "\"[reference('Employee id').id]\", \"[reference('Department').id]\"");

        var whatIf = await host.WhatIfAsync(host.Resources(template));
        Assert.DoesNotContain(whatIf.Plan.Changes, c => c.HasError);

        var run = await host.CreateAsync(host.Resources(template));

        Check.Succeeded(run);
        string spId = host.IdOf("/servicePrincipals", "appId", appId);
        Assert.Equal(2, RefWrites(run).Count);
        Assert.All(RefWrites(run), w => Assert.Equal("/servicePrincipals/" + spId + "/claimsMappingPolicies/$ref", w.Path));
        Assert.Equal(2, host.Directory.AssignedPolicies(spId, "claimsMappingPolicies").Count);

        var rerun = await host.CreateAsync(host.Resources(template));
        Check.Succeeded(rerun);
        Assert.Empty(RefWrites(rerun));
    }
}
