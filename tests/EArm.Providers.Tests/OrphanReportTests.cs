using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Engine;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Providers.Tests.Infrastructure;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// The orphan report through the real providers (docs/whatif.md, "Orphan report"): Complete mode never deletes, it lists the undeclared live objects of the declared
/// types within the bounded scope of each type; both modes list the retained assignments of the declared principals.
/// </summary>
public sealed class OrphanReportTests
{
    private const string LocType = "Microsoft.Entra/conditionalAccess/namedLocations";
    private const string LocPath = "/identity/conditionalAccess/namedLocations";
    private const string PolicyPath = "/identity/conditionalAccess/policies";
    private const string StrengthType = "Microsoft.Entra/conditionalAccess/authenticationStrengths";
    private const string StrengthPath = "/policies/authenticationStrengthPolicies";
    private const string AppType = "Microsoft.Entra/applications";
    private const string FicType = "Microsoft.Entra/applications/federatedIdentityCredentials";
    private const string SpType = "Microsoft.Entra/servicePrincipals";
    private const string RoleAssignmentType = "Microsoft.Entra/roleAssignments";

    private static string Location(string name, string? extra = null)
    {
        return Templates.Resource(LocType, name, """{ "@odata.type": "#microsoft.graph.ipNamedLocation", "ipRanges": [ { "cidrAddress": "10.0.0.0/8" } ] }""", extra: extra);
    }

    private static void SeedLocation(ProviderHost host, string displayName)
    {
        host.Directory.Add(LocPath, new JsonObject
        {
            ["@odata.type"] = "#microsoft.graph.ipNamedLocation",
            ["displayName"] = displayName,
            ["isTrusted"] = false,
            ["ipRanges"] = new JsonArray(new JsonObject { ["@odata.type"] = "#microsoft.graph.iPv4CidrRange", ["cidrAddress"] = "192.168.0.0/16" }),
        });
    }

    private static ResourceChange Single(Run run, OrphanReason reason)
    {
        return Assert.Single(run.Plan.Orphans, o => o.Orphan == reason);
    }

    [Fact]
    public async Task Complete_ListsTheUndeclaredObjectsOfADeclaredCollectionType_AndNeverDeletes()
    {
        var host = new ProviderHost();
        SeedLocation(host, "Legacy offices");
        host.Directory.Add(PolicyPath, new JsonObject { ["displayName"] = "Somebody else's policy", ["state"] = "disabled" });
        var document = host.Resources(Location("Contoso offices"));

        var whatIf = await host.WhatIfAsync(document, DeploymentMode.Complete);
        var run = await host.CreateAsync(document, DeploymentMode.Complete);

        var orphan = Single(whatIf, OrphanReason.Undeclared);
        Assert.Equal("/providers/" + LocType + "/Legacy offices", orphan.Id.Id);
        Assert.Equal(host.IdOf(LocPath, "displayName", "Legacy offices"), orphan.GraphId);
        Assert.Equal(ChangeType.Ignore, orphan.Type);
        Assert.Single(whatIf.Plan.Orphans);
        Check.NoWrites(whatIf);

        // The policy type is not declared, so it is not looked at; nothing is deleted.
        Check.Succeeded(run);
        Assert.DoesNotContain(run.Writes, w => w.Method == "DELETE");
        Assert.Equal(1, host.Directory.Count(PolicyPath));
        Assert.Equal(2, host.Directory.Count(LocPath));
        var reported = Assert.Single(run.Executed.Orphans);
        Assert.Equal(orphan.Id.Id, reported.Id.Id);
        Assert.Equal(OrphanReason.Undeclared, reported.Orphan);
    }

    [Fact]
    public async Task Incremental_ListsNoUndeclaredCollectionObject()
    {
        var host = new ProviderHost();
        SeedLocation(host, "Legacy offices");

        var whatIf = await host.WhatIfAsync(host.Resources(Location("Contoso offices")));

        Assert.Empty(whatIf.Plan.Orphans);
    }

    [Fact]
    public async Task Complete_AResourceWhoseConditionIsFalse_IsReportedAsUndeclared_NotDeleted()
    {
        var host = new ProviderHost();
        Check.Succeeded(await host.CreateAsync(host.Resources(Location("Contoso offices"))));

        var run = await host.CreateAsync(host.Resources(Location("Contoso offices", extra: ", \"condition\": false")), DeploymentMode.Complete);

        Check.Succeeded(run);
        Check.NoWrites(run);
        Assert.Equal("/providers/" + LocType + "/Contoso offices", Assert.Single(run.Executed.Orphans).Id.Id);
    }

    [Fact]
    public async Task Complete_BuiltInAuthenticationStrengths_AreNeverReported()
    {
        var host = new ProviderHost();
        host.Directory.Add(StrengthPath, new JsonObject { ["id"] = "00000000-0000-0000-0000-000000000002", ["displayName"] = "Multifactor authentication", ["policyType"] = "builtIn", ["allowedCombinations"] = new JsonArray("password,sms") });
        host.Directory.Add(StrengthPath, new JsonObject { ["displayName"] = "Old custom strength", ["policyType"] = "custom", ["allowedCombinations"] = new JsonArray("fido2") });
        var document = host.Resources(Templates.Resource(StrengthType, "Phishing-resistant", """{ "allowedCombinations": ["fido2", "windowsHelloForBusiness"] }"""));

        var whatIf = await host.WhatIfAsync(document, DeploymentMode.Complete);

        var orphan = Single(whatIf, OrphanReason.Undeclared);
        Assert.Equal("/providers/" + StrengthType + "/Old custom strength", orphan.Id.Id);
        Assert.Single(whatIf.Plan.Orphans);
    }

    [Fact]
    public async Task Complete_ListsTheChildrenOfTheDeclaredApplicationsOnly()
    {
        var host = new ProviderHost();
        string declaredApp = host.Directory.AddApplication("orders-api", "Orders API")["id"]!.GetValue<string>();
        string otherApp = host.Directory.AddApplication("someone-else", "Someone else")["id"]!.GetValue<string>();
        host.Directory.Add("/applications/" + declaredApp + "/federatedIdentityCredentials", new JsonObject { ["name"] = "github-old", ["issuer"] = "https://token.actions.githubusercontent.com", ["subject"] = "repo:contoso/orders:ref:refs/heads/old", ["audiences"] = new JsonArray("api://AzureADTokenExchange") });
        host.Directory.Add("/applications/" + otherApp + "/federatedIdentityCredentials", new JsonObject { ["name"] = "github-x", ["issuer"] = "https://token.actions.githubusercontent.com", ["subject"] = "repo:contoso/x:ref:refs/heads/main", ["audiences"] = new JsonArray("api://AzureADTokenExchange") });
        string app = Templates.Resource(AppType, "orders-api", """{ "displayName": "Orders API" }""");
        string fic = Templates.Resource(FicType, "orders-api/github-main", """{ "issuer": "https://token.actions.githubusercontent.com", "subject": "repo:contoso/orders:ref:refs/heads/main" }""", extra: Templates.DependsOn(AppType, "orders-api"));

        var whatIf = await host.WhatIfAsync(host.Resources(app + ", " + fic), DeploymentMode.Complete);

        var orphan = Assert.Single(whatIf.Plan.Orphans, o => o.ResourceType == FicType);
        Assert.Equal("/providers/" + AppType + "/orders-api/federatedIdentityCredentials/github-old", orphan.Id.Id);
        Assert.Equal(OrphanReason.Undeclared, orphan.Orphan);
        Assert.DoesNotContain(whatIf.Plan.Orphans, o => o.Id.Id.Contains("github-x", StringComparison.Ordinal));
        Assert.Contains(whatIf.Plan.Diagnostics, d => d.Code == EngineDiagnosticCodes.OrphanReportIncomplete && d.Message.Contains("'" + AppType + "'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Complete_TheServicePrincipalOfADeclaredApplication_ThatTheTemplateDoesNotDeclare_IsReported()
    {
        var host = new ProviderHost();
        string appIdA = Guid.NewGuid().ToString("D");
        string appIdB = Guid.NewGuid().ToString("D");
        host.Directory.AddApplication("app-a", "App A", appIdA);
        host.Directory.AddApplication("app-b", "App B", appIdB);
        host.Directory.AddServicePrincipal(appIdA, "App A");
        string spB = host.Directory.AddServicePrincipal(appIdB, "App B")["id"]!.GetValue<string>();
        string template = Templates.Resource(AppType, "app-a", """{ "displayName": "App A" }""") + ", "
            + Templates.Resource(AppType, "app-b", """{ "displayName": "App B" }""") + ", "
            + Templates.Resource(SpType, appIdA, "{ }", extra: Templates.DependsOn(AppType, "app-a"));

        var whatIf = await host.WhatIfAsync(host.Resources(template), DeploymentMode.Complete);

        var orphan = Assert.Single(whatIf.Plan.Orphans);
        Assert.Equal(SpType, orphan.ResourceType);
        Assert.Equal(spB, orphan.GraphId);
        Assert.Equal("/providers/" + SpType + "/" + appIdB, orphan.Id.Id);
    }

    [Theory]
    [InlineData(DeploymentMode.Incremental)]
    [InlineData(DeploymentMode.Complete)]
    public async Task TheOldTupleOfAChangedRoleAssignment_IsReportedAsRetained_InBothModes_AndOtherPrincipalsAreNotLookedAt(DeploymentMode mode)
    {
        var host = new ProviderHost();
        BuiltInRoles.Seed(host);
        string alice = host.Directory.AddUser("alice@contoso.com", "Alice")["id"]!.GetValue<string>();
        string bob = host.Directory.AddUser("bob@contoso.com", "Bob")["id"]!.GetValue<string>();
        string Assignment(string role)
        {
            return Templates.Resource(RoleAssignmentType, "[guid('alice', 'reader')]", "{ \"principalId\": \"" + alice + "\", \"role\": \"" + role + "\" }");
        }

        Check.Succeeded(await host.CreateAsync(host.Resources(Assignment("Directory Readers"))));
        host.Directory.Add(BuiltInRoles.AssignmentsPath, new JsonObject { ["roleDefinitionId"] = BuiltInRoles.SecurityReader, ["principalId"] = bob, ["directoryScopeId"] = "/" });
        string oldId = host.Directory.Objects(BuiltInRoles.AssignmentsPath).Single(a => a["principalId"]!.GetValue<string>() == alice)["id"]!.GetValue<string>();

        // The template now gives alice another role: the tuple changed, so it is a new assignment and the old one is retained.
        var document = host.Resources(Assignment("Global Reader"));
        var whatIf = await host.WhatIfAsync(document, mode);
        var run = await host.CreateAsync(document, mode);

        var retained = Single(whatIf, OrphanReason.Retained);
        Assert.Equal(oldId, retained.GraphId);
        Assert.Contains(retained.Reasons, r => r.StartsWith("retained: no longer declared; still grants access", StringComparison.Ordinal));
        Assert.Single(whatIf.Plan.Orphans);
        Assert.Contains(whatIf.Plan.Diagnostics, d => d.Code == EngineDiagnosticCodes.AssignmentRetained && d.Message.Contains(oldId, StringComparison.Ordinal));

        Check.Succeeded(run);
        Assert.DoesNotContain(run.Writes, w => w.Method == "DELETE");
        Assert.Equal(oldId, Assert.Single(run.Executed.Orphans).GraphId);
        Assert.Equal(2, host.Directory.Objects(BuiltInRoles.AssignmentsPath).Count(a => a["principalId"]!.GetValue<string>() == alice));

        // A rerun has nothing to write: the retained assignment is still reported, and orphans never count as changes.
        var rerun = await host.WhatIfAsync(document, mode);
        Assert.False(rerun.Plan.HasChanges);
        Assert.False(EArm.Engine.WhatIf.WhatIfReport.From(rerun.Plan).HasChanges);
        Assert.Single(rerun.Plan.Orphans);
    }

    [Fact]
    public async Task ADeclaredResourceThatCouldNotBeRead_KeepsItsTypeOutOfTheReport_WithAWarning()
    {
        var host = new ProviderHost();
        SeedLocation(host, "Legacy offices");
        host.Fake.Failures.Forbid("namedLocations", "GET");

        var whatIf = await host.WhatIfAsync(host.Resources(Location("Contoso offices")), DeploymentMode.Complete);

        Assert.Empty(whatIf.Plan.Orphans);
        Assert.Contains(whatIf.Plan.Diagnostics, d => d.Code == EngineDiagnosticCodes.OrphanReportIncomplete && d.Message.Contains(LocType, StringComparison.Ordinal));
    }
}
