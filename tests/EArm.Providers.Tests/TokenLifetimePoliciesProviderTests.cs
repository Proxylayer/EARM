using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Providers.Tests.Infrastructure;
using EArm.TestSupport;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>Token lifetime policies (display-name keyed STS policies) and the ensure-only <c>tokenLifetimePolicies</c> edge of applications and service principals.</summary>
public sealed class TokenLifetimePoliciesProviderTests
{
    private const string PolicyType = TokenLifetimePoliciesProvider.ResourceType;
    private const string PolicyPath = FakeDirectory.TokenLifetimePoliciesPath;
    private const string PolicyName = "Web sessions";

    private static string Policy(string definition = "{ \"TokenLifetimePolicy\": { \"Version\": 1, \"AccessTokenLifetime\": \"02:00:00\" } }", string extra = "", string name = PolicyName)
    {
        return "{ \"type\": \"" + PolicyType + "\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"" + name + "\", \"properties\": { \"definition\": [ " + definition + " ]" + extra + " } }";
    }

    private static string App(string policies = "\"[reference('Web sessions').id]\"")
    {
        return "{ \"type\": \"Microsoft.Entra/applications\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"web-app\", \"properties\": { \"displayName\": \"Web app\""
            + (policies.Length == 0 ? string.Empty : ", \"tokenLifetimePolicies\": [ " + policies + " ]") + " } }";
    }

    private static IReadOnlyList<FakeGraphRequest> RefWrites(Run run)
    {
        return run.Writes.Where(w => w.Path.Contains("/tokenLifetimePolicies/", StringComparison.Ordinal) && w.Path.EndsWith("$ref", StringComparison.Ordinal)).ToList();
    }

    private static JsonObject SeedPolicy(ProviderHost host, string name = PolicyName, string lifetime = "02:00:00")
    {
        return host.Directory.Add(PolicyPath, new JsonObject
        {
            ["displayName"] = name,
            ["definition"] = new JsonArray("{\"TokenLifetimePolicy\":{\"Version\":1,\"AccessTokenLifetime\":\"" + lifetime + "\"}}"),
            ["isOrganizationDefault"] = false,
        });
    }

    [Fact]
    public void Discovery_ReadsWithPolicyReadAll()
    {
        var provider = Assert.IsType<TokenLifetimePoliciesProvider>(new ProviderHost().Provider(PolicyType));
        Assert.Equal(["Policy.ReadWrite.ApplicationConfiguration", "Policy.Read.All"], provider.RequiredPermissions(null));
        Assert.Equal("Policy.Read.All", PermissionManifestReadVariant("Policy.ReadWrite.ApplicationConfiguration"));
    }

    [Fact]
    public async Task Create_SendsTheObjectSugarAsACompactString_AndRerunsInEitherSpellingAreNoChange()
    {
        var host = new ProviderHost();

        var run = await host.CreateAsync(host.Resources(Policy()));

        Check.Succeeded(run);
        var post = Assert.Single(run.Writes);
        Assert.Equal("POST", post.Method);
        Assert.Equal(PolicyPath, post.Path);
        Assert.Equal("{\"TokenLifetimePolicy\":{\"Version\":1,\"AccessTokenLifetime\":\"02:00:00\"}}", post.Body!["definition"]![0]!.GetValue<string>());
        Assert.Equal(PolicyName, post.Body!["displayName"]!.GetValue<string>());
        Assert.Equal(2, post.Body!.AsObject().Count);
        Assert.Equal("GET " + PolicyPath, run.Requests[0].ToString());

        var rerun = await host.CreateAsync(host.Resources(Policy()));
        Assert.All(rerun.Executed.Operations, o => Assert.Equal(ChangeType.NoChange, o.ChangeType));
        Check.NoWrites(rerun);

        var asString = await host.CreateAsync(host.Resources(Policy("\"{ \\\"TokenLifetimePolicy\\\": { \\\"AccessTokenLifetime\\\": \\\"02:00:00\\\", \\\"Version\\\": 1 } }\"")));
        Assert.All(asString.Executed.Operations, o => Assert.Equal(ChangeType.NoChange, o.ChangeType));
        Check.NoWrites(asString);
    }

    [Fact]
    public async Task WhatIf_OfANewPolicy_IsACreate()
    {
        var host = new ProviderHost();

        var whatIf = await host.WhatIfAsync(host.Resources(Policy()));

        var row = Assert.Single(whatIf.Plan.Changes);
        Assert.Equal(ChangeType.Create, row.Type);
        Assert.False(row.HasError);
        Check.NoWrites(whatIf);
    }

    [Fact]
    public async Task ALifetimeChange_PatchesTheDefinitionOnly()
    {
        var host = new ProviderHost();
        string id = SeedPolicy(host)["id"]!.GetValue<string>();
        string changed = "{ \"TokenLifetimePolicy\": { \"Version\": 1, \"AccessTokenLifetime\": \"04:00:00\" } }";

        var whatIf = await host.WhatIfAsync(host.Resources(Policy(changed)));
        Assert.Equal(ChangeType.Modify, Assert.Single(whatIf.Plan.Changes).Type);

        var run = await host.CreateAsync(host.Resources(Policy(changed, ", \"isOrganizationDefault\": false")));

        Check.Succeeded(run);
        var patch = Assert.Single(run.Writes);
        Assert.Equal("PATCH", patch.Method);
        Assert.Equal(PolicyPath + "/" + id, patch.Path);
        Assert.Single(patch.Body!.AsObject());
        Assert.Equal("{\"TokenLifetimePolicy\":{\"Version\":1,\"AccessTokenLifetime\":\"04:00:00\"}}", patch.Body!["definition"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task TwoPoliciesWithTheName_AreAnAmbiguousMatch()
    {
        var host = new ProviderHost();
        SeedPolicy(host);
        SeedPolicy(host, PolicyName.ToUpperInvariant());
        var key = new ResourceKey { Type = PolicyType, Values = new Dictionary<string, string> { ["displayName"] = PolicyName } };

        var ex = await Assert.ThrowsAsync<ProviderException>(() => host.Provider(PolicyType).ReadAsync(key, host.Fake, ProviderHost.Ct));

        Assert.Equal(ProviderException.AmbiguousMatch, ex.Code);
        Assert.All(host.Directory.Objects(PolicyPath).Select(o => o["id"]!.GetValue<string>()), id => Assert.Contains(id, ex.Message, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("{ \"definition\": [ { \"TokenLifetimePolicy\": { \"Version\": 1, \"AccessTokenLifetime\": \"00:05:00\" } } ] }", "outside the allowed range")]
    [InlineData("{ \"definition\": [ { \"TokenLifetimePolicy\": { \"Version\": 1, \"MaxInactiveTime\": \"90.00:00:00\" } } ] }", "MaxInactiveTime")]
    [InlineData("{ \"definition\": [ { \"TokenLifetimePolicy\": { \"AccessTokenLifetime\": \"02:00:00\" } } ] }", "\"Version\": 1")]
    [InlineData("{ \"definition\": [ { \"ClaimsMappingPolicy\": { \"Version\": 1 } } ] }", "TokenLifetimePolicy")]
    [InlineData("{ \"definition\": [ \"not json\" ] }", "not a JSON object")]
    [InlineData("{ \"definition\": [ \"{}\", \"{}\" ] }", "exactly one policy document")]
    public async Task InvalidDefinitions_AreUnsupportedRows(string properties, string expected)
    {
        var host = new ProviderHost();
        var desired = await InstanceFactory.WithPropertiesAsync(host, Policy(), properties);

        var change = host.Provider(PolicyType).Plan(desired, null, PlanOptions.Default);

        Assert.True(change.HasError);
        Assert.Contains(expected, change.UnsupportedReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_RemovesThePolicy_WithTheRiskListed_AndCompleteModeListsOrphans()
    {
        var host = new ProviderHost();
        SeedPolicy(host);
        SeedPolicy(host, "Undeclared");
        var document = host.Resources(Policy());
        var plan = await host.Planner().PlanAsync(document, DeploymentMode.Incremental, PlanMode.WhatIf, ProviderHost.Ct);
        var listed = Assert.Single(TemplateDeleter.FindDeclaredObjects(plan));
        Assert.Contains("fall back to the default", listed.Risk, StringComparison.Ordinal);

        var complete = await host.WhatIfAsync(document, DeploymentMode.Complete);
        Assert.Contains("Undeclared", complete.WhatIfTextOutput, StringComparison.Ordinal);

        var result = await host.DeleteDeclaredAsync(document);

        Assert.True(result.Succeeded);
        Assert.Equal(["Undeclared"], host.Directory.Objects(PolicyPath).Select(o => o["displayName"]!.GetValue<string>()));
    }

    // ---- the tokenLifetimePolicies edge ----

    [Fact]
    public async Task AnApplicationDeclaringThePolicy_IsAssignedOnce_AndTheRerunWritesNothing()
    {
        var host = new ProviderHost();
        string template = Policy() + ", " + App();

        var whatIf = await host.WhatIfAsync(host.Resources(template));
        Assert.DoesNotContain(whatIf.Plan.Changes, c => c.HasError);

        var run = await host.CreateAsync(host.Resources(template));

        Check.Succeeded(run);
        string policyId = host.IdOf(PolicyPath, "displayName", PolicyName);
        string appId = host.IdOf("/applications", "uniqueName", "web-app");
        var add = Assert.Single(RefWrites(run));
        Assert.Equal("POST", add.Method);
        Assert.Equal("/applications/" + appId + "/tokenLifetimePolicies/$ref", add.Path);
        Assert.EndsWith("/policies/tokenLifetimePolicies/" + policyId + "\"}", add.Body!.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal([policyId], host.Directory.AssignedPolicies(appId, "tokenLifetimePolicies"));

        var rerun = await host.CreateAsync(host.Resources(template));
        Check.Succeeded(rerun);
        Assert.Empty(RefWrites(rerun));

        var undeclared = await host.CreateAsync(host.Resources(Policy() + ", " + App(string.Empty)));
        Check.Succeeded(undeclared);
        Assert.Empty(RefWrites(undeclared));
        Assert.Equal([policyId], host.Directory.AssignedPolicies(appId, "tokenLifetimePolicies"));
    }

    [Fact]
    public async Task AServicePrincipal_CanHoldThePolicy()
    {
        var host = new ProviderHost();
        var app = host.Directory.AddApplication("api-app", "API");
        string sp = "{ \"type\": \"Microsoft.Entra/servicePrincipals\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"" + app["appId"]!.GetValue<string>()
            + "\", \"properties\": { \"tokenLifetimePolicies\": [ \"[reference('Web sessions').id]\" ] } }";

        var run = await host.CreateAsync(host.Resources(Policy() + ", " + sp));

        Check.Succeeded(run);
        var add = Assert.Single(RefWrites(run));
        Assert.StartsWith("/servicePrincipals/", add.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnApplicationHoldingAnotherPolicy_IsAnEdgeValidationError()
    {
        var host = new ProviderHost();
        string other = SeedPolicy(host, "Other")["id"]!.GetValue<string>();
        var app = host.Directory.AddApplication("web-app", "Web app");
        host.Directory.AssignPolicy(app["id"]!.GetValue<string>(), "tokenLifetimePolicies", other);

        var run = await host.CreateAsync(host.Resources(Policy() + ", " + App()));

        Assert.False(run.Succeeded);
        var row = run.Op("web-app");
        Assert.Equal(EngineDiagnosticCodes.EdgeValidationFailed, row.ErrorCode);
        Assert.Contains("already has token lifetime policy " + other, row.Error, StringComparison.Ordinal);
        Assert.Empty(RefWrites(run));
    }

    [Fact]
    public async Task TwoDeclaredPolicies_AreAnEdgeValidationError()
    {
        var host = new ProviderHost();
        string template = Policy() + ", " + Policy(name: "Second") + ", " + App("\"[reference('Web sessions').id]\", \"[reference('Second').id]\"");

        var run = await host.CreateAsync(host.Resources(template));

        Assert.False(run.Succeeded);
        Assert.Contains("allows one token lifetime policy per application", run.Op("web-app").Error, StringComparison.Ordinal);
    }

    private static string PermissionManifestReadVariant(string permission)
    {
        return EArm.Engine.Permissions.PermissionManifest.ReadVariant(permission);
    }
}
