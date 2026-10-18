using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Providers;
using EArm.Providers.Tests.Infrastructure;
using EArm.TestSupport;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>Custom app management policies, the tenant default app management policy singleton and the ensure-only <c>appManagementPolicies</c> edge.</summary>
public sealed class AppManagementPoliciesProviderTests
{
    private const string PolicyType = AppManagementPoliciesProvider.ResourceType;
    private const string DefaultType = DefaultAppManagementPolicyProvider.ResourceType;
    private const string PolicyPath = FakeDirectory.AppManagementPoliciesPath;
    private const string PasswordLifetime = "{ \"restrictionType\": \"passwordLifetime\", \"state\": \"enabled\", \"maxLifetime\": \"P90D\" }";
    private const string PasswordAddition = "{ \"restrictionType\": \"passwordAddition\", \"state\": \"enabled\", \"restrictForAppsCreatedAfterDateTime\": \"2024-01-01T00:00:00Z\" }";

    private static string Policy(string restrictions = "{ \"passwordCredentials\": [ " + PasswordLifetime + " ] }", string extra = "")
    {
        return "{ \"type\": \"" + PolicyType + "\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"Short secrets\", \"properties\": { \"description\": \"Secrets live 90 days\", \"isEnabled\": true, \"restrictions\": "
            + restrictions + extra + " } }";
    }

    private static string Default(string properties, string name = "default")
    {
        return "{ \"type\": \"" + DefaultType + "\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"" + name + "\", \"properties\": " + properties + " }";
    }

    private static string App(string policies = "\"[reference('Short secrets').id]\"")
    {
        return "{ \"type\": \"Microsoft.Entra/applications\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"legacy-app\", \"properties\": { \"displayName\": \"Legacy app\", \"appManagementPolicies\": [ " + policies + " ] } }";
    }

    private static IReadOnlyList<FakeGraphRequest> RefWrites(Run run)
    {
        return run.Writes.Where(w => w.Path.Contains("/appManagementPolicies/", StringComparison.Ordinal) && w.Path.EndsWith("$ref", StringComparison.Ordinal)).ToList();
    }

    // ---- custom policies ----

    [Fact]
    public async Task Create_ThenRerun_IsNoChange_AndReorderedRestrictionsAreNoChange()
    {
        var host = new ProviderHost();
        string both = "{ \"passwordCredentials\": [ " + PasswordLifetime + ", " + PasswordAddition + " ] }";

        var run = await host.CreateAsync(host.Resources(Policy(both)));

        Check.Succeeded(run);
        var post = Assert.Single(run.Writes);
        Assert.Equal(PolicyPath, post.Path);
        Assert.Equal("Short secrets", post.Body!["displayName"]!.GetValue<string>());

        var reordered = await host.CreateAsync(host.Resources(Policy("{ \"passwordCredentials\": [ " + PasswordAddition + ", " + PasswordLifetime + " ] }")));
        Assert.All(reordered.Executed.Operations, o => Assert.Equal(ChangeType.NoChange, o.ChangeType));
        Check.NoWrites(reordered);
    }

    [Fact]
    public async Task AChangedRestriction_IsOnePatch_ThatKeepsTheUndeclaredLiveMembers()
    {
        var host = new ProviderHost();
        await host.CreateAsync(host.Resources(Policy("{ \"passwordCredentials\": [ " + PasswordLifetime + " ], \"keyCredentials\": [ { \"restrictionType\": \"asymmetricKeyLifetime\", \"state\": \"enabled\", \"maxLifetime\": \"P365D\" } ] }")));
        string id = host.IdOf(PolicyPath, "displayName", "Short secrets");

        var run = await host.CreateAsync(host.Resources(Policy("{ \"passwordCredentials\": [ " + PasswordLifetime.Replace("P90D", "P30D", StringComparison.Ordinal) + " ] }")));

        Check.Succeeded(run);
        var patch = Assert.Single(run.Writes);
        Assert.Equal("PATCH", patch.Method);
        Assert.Equal(PolicyPath + "/" + id, patch.Path);
        Assert.Equal("P30D", patch.Body!["restrictions"]!["passwordCredentials"]![0]!["maxLifetime"]!.GetValue<string>());
        Assert.Equal("P365D", patch.Body!["restrictions"]!["keyCredentials"]![0]!["maxLifetime"]!.GetValue<string>());
        Assert.Single(host.Directory.FindById(id)!["restrictions"]!["keyCredentials"]!.AsArray());
    }

    [Fact]
    public async Task WhatIf_OfANewPolicy_IsACreate_AndAMissingDescriptionIsAnErrorRow()
    {
        var host = new ProviderHost();

        var whatIf = await host.WhatIfAsync(host.Resources(Policy()));
        Assert.Equal(ChangeType.Create, Assert.Single(whatIf.Plan.Changes).Type);

        string noDescription = Policy().Replace("\"description\": \"Secrets live 90 days\", ", string.Empty, StringComparison.Ordinal);
        var missing = await host.WhatIfAsync(host.Resources(noDescription));
        var row = Assert.Single(missing.Plan.Changes);
        Assert.True(row.HasError);
        Assert.Contains("description", row.UnsupportedReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoPoliciesWithTheName_AreAnAmbiguousMatch_AndDeleteRemovesTheDeclaredOne()
    {
        var host = new ProviderHost();
        host.Directory.Add(PolicyPath, new JsonObject { ["displayName"] = "Short secrets", ["description"] = "a" });
        host.Directory.Add(PolicyPath, new JsonObject { ["displayName"] = "Short secrets", ["description"] = "b" });
        var key = new ResourceKey { Type = PolicyType, Values = new Dictionary<string, string> { ["displayName"] = "Short secrets" } };

        var ex = await Assert.ThrowsAsync<ProviderException>(() => host.Provider(PolicyType).ReadAsync(key, host.Fake, ProviderHost.Ct));
        Assert.Equal(ProviderException.AmbiguousMatch, ex.Code);

        var single = new ProviderHost();
        await single.CreateAsync(single.Resources(Policy()));
        var result = await single.DeleteDeclaredAsync(single.Resources(Policy()));
        Assert.True(result.Succeeded);
        Assert.Empty(single.Directory.Objects(PolicyPath));
    }

    // ---- the appManagementPolicies edge ----

    [Fact]
    public async Task AnApplicationDeclaringThePolicy_IsAssignedOnce()
    {
        var host = new ProviderHost();
        string template = Policy() + ", " + App();

        var run = await host.CreateAsync(host.Resources(template));

        Check.Succeeded(run);
        string appId = host.IdOf("/applications", "uniqueName", "legacy-app");
        var add = Assert.Single(RefWrites(run));
        Assert.Equal("/applications/" + appId + "/appManagementPolicies/$ref", add.Path);
        Assert.Equal([host.IdOf(PolicyPath, "displayName", "Short secrets")], host.Directory.AssignedPolicies(appId, "appManagementPolicies"));

        var rerun = await host.CreateAsync(host.Resources(template));
        Assert.Empty(RefWrites(rerun));
    }

    [Fact]
    public async Task AnApplicationHoldingAnotherPolicy_IsAnEdgeValidationError()
    {
        var host = new ProviderHost();
        string other = host.Directory.Add(PolicyPath, new JsonObject { ["displayName"] = "Other", ["description"] = "x" })["id"]!.GetValue<string>();
        var app = host.Directory.AddApplication("legacy-app", "Legacy app");
        host.Directory.AssignPolicy(app["id"]!.GetValue<string>(), "appManagementPolicies", other);

        var run = await host.CreateAsync(host.Resources(Policy() + ", " + App()));

        Assert.False(run.Succeeded);
        Assert.Equal(EngineDiagnosticCodes.EdgeValidationFailed, run.Op("legacy-app").ErrorCode);
        Assert.Empty(RefWrites(run));
    }

    // ---- the tenant default policy ----

    [Fact]
    public async Task Default_MatchingDeclaration_IsNoChange_AndAChangeIsAMergePatchOfTheTopLevelProperty()
    {
        var host = new ProviderHost();

        var same = await host.CreateAsync(host.Resources(Default("{ \"isEnabled\": false }")));
        Assert.All(same.Executed.Operations, o => Assert.Equal(ChangeType.NoChange, o.ChangeType));
        Check.NoWrites(same);

        var run = await host.CreateAsync(host.Resources(Default("{ \"isEnabled\": true, \"applicationRestrictions\": { \"passwordCredentials\": [ " + PasswordLifetime + " ] } }")));

        Check.Succeeded(run);
        var patch = Assert.Single(run.Writes);
        Assert.Equal("PATCH", patch.Method);
        Assert.Equal(FakeDirectory.DefaultAppManagementPolicyPath, patch.Path);
        Assert.True(patch.Body!["isEnabled"]!.GetValue<bool>());
        Assert.Empty(patch.Body!["applicationRestrictions"]!["keyCredentials"]!.AsArray());
        Assert.Null(patch.Body!["servicePrincipalRestrictions"]);
        Assert.True(host.Directory.DefaultAppManagementPolicy["isEnabled"]!.GetValue<bool>());

        var rerun = await host.CreateAsync(host.Resources(Default("{ \"isEnabled\": true, \"applicationRestrictions\": { \"passwordCredentials\": [ " + PasswordLifetime + " ] } }")));
        Check.NoWrites(rerun);
    }

    [Fact]
    public async Task Default_WhatIf_IsAModifyWithoutWrites()
    {
        var host = new ProviderHost();

        var whatIf = await host.WhatIfAsync(host.Resources(Default("{ \"isEnabled\": true }")));

        Assert.Equal(ChangeType.Modify, Assert.Single(whatIf.Plan.Changes).Type);
        Check.NoWrites(whatIf);
    }

    [Fact]
    public async Task Default_Delete_IsKeptAsNotDeletable_AndIsNotAFailure()
    {
        var host = new ProviderHost();

        var result = await host.DeleteDeclaredAsync(host.Resources(Default("{ \"isEnabled\": false }")));

        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal(DeletionStatus.Refused, outcome.Status);
        Assert.Equal(EngineDiagnosticCodes.NotDeletable, outcome.ErrorCode);
        Assert.True(result.Succeeded);
        Assert.Empty(host.Fake.Writes);
    }

    [Fact]
    public async Task Default_CreateIsAProviderException_AndOrphansAreNeverListed()
    {
        var host = new ProviderHost();
        var provider = host.Provider<DefaultAppManagementPolicyProvider>(DefaultType);
        var desired = await InstanceFactory.WithPropertiesAsync(host, Default("{ \"isEnabled\": true }"), "{ \"isEnabled\": true }");

        await Assert.ThrowsAsync<ProviderException>(() => provider.CreateAsync(desired, host.Fake, ProviderHost.Ct));
        Assert.Empty(await provider.ListOrphanCandidatesAsync(new OrphanQuery(), host.Fake, ProviderHost.Ct));
        Assert.True(provider.Plan(desired, null, PlanOptions.Default).HasError);
    }
}
