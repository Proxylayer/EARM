using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Providers.Tests.Infrastructure;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// Copy-loop references through the engine: a reference to one instance creates a dependency on that instance only, <c>dependsOn</c>
/// on a loop name waits for every instance, a property copy builds an edge list of instance ids that the edge pass applies, an output
/// copy returns one element per instance, and the flattened scopes x tiers loop expands and orders like any other loop.
/// </summary>
public sealed class CopyLoopReferenceEngineTests
{
    private const string Parameters = "\"scopes\": { \"type\": \"array\", \"defaultValue\": [ \"dev\", \"uat\", \"prd\" ] }";

    private const string Approvers = "{ \"copy\": { \"name\": \"approvers\", \"count\": \"[length(parameters('scopes'))]\" }, \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", "
        + "\"name\": \"[format('pg-{0}-approvers', parameters('scopes')[copyIndex()])]\", \"properties\": { \"displayName\": \"[format('PG {0} Approvers', parameters('scopes')[copyIndex()])]\", \"mailEnabled\": false, \"securityEnabled\": true } }";

    private static ResourceInstance Instance(DeploymentPlan plan, string name)
    {
        return plan.Instances.Single(i => string.Equals(i.Id.NameLeaf, name, StringComparison.Ordinal));
    }

    private static string[] DependencyNames(DeploymentPlan plan, string name)
    {
        return plan.Graph.DependenciesOf(Instance(plan, name)).Select(d => d.Id.NameLeaf).Order(StringComparer.Ordinal).ToArray();
    }

    [Fact]
    public async Task AReferenceToOneInstance_DependsOnThatInstanceOnly()
    {
        var host = new ProviderHost();
        string mirrors = "{ \"copy\": { \"name\": \"mirrors\", \"count\": 3 }, \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"[format('mirror-{0}', copyIndex())]\", "
            + "\"properties\": { \"displayName\": \"M\", \"mailEnabled\": false, \"securityEnabled\": true, \"description\": \"[reference(format('pg-{0}-approvers', parameters('scopes')[copyIndex()])).id]\" } }";
        string single = "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"single\", "
            + "\"properties\": { \"displayName\": \"S\", \"mailEnabled\": false, \"securityEnabled\": true, \"description\": \"[reference('pg-uat-approvers').id]\" } }";
        string waiter = "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"waiter\", \"dependsOn\": [ \"approvers\" ], "
            + "\"properties\": { \"displayName\": \"W\", \"mailEnabled\": false, \"securityEnabled\": true } }";

        var plan = await host.ExpandAsync(host.Resources(Approvers + ", " + mirrors + ", " + single + ", " + waiter, parameters: Parameters));

        Assert.Equal(["pg-dev-approvers"], DependencyNames(plan, "mirror-0"));
        Assert.Equal(["pg-prd-approvers"], DependencyNames(plan, "mirror-2"));
        Assert.Equal(["pg-uat-approvers"], DependencyNames(plan, "single"));
        Assert.Equal(["pg-dev-approvers", "pg-prd-approvers", "pg-uat-approvers"], DependencyNames(plan, "waiter"));
    }

    [Fact]
    public async Task APropertyCopyMemberOf_AndAnOutputCopy_ApplyAndReturnEveryInstance()
    {
        var host = new ProviderHost();
        string alice = "{ \"type\": \"Microsoft.Entra/users\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"alice@contoso.com\", \"properties\": { \"displayName\": \"Alice\", "
            + "\"copy\": [ { \"name\": \"memberOf\", \"count\": \"[length(parameters('scopes'))]\", \"input\": \"[reference(format('pg-{0}-approvers', parameters('scopes')[copyIndex('memberOf')])).id]\" } ] } }";
        string outputs = "\"approverIds\": { \"type\": \"array\", \"copy\": { \"count\": \"[length(parameters('scopes'))]\", \"input\": { \"scope\": \"[parameters('scopes')[copyIndex()]]\", \"id\": \"[reference(format('pg-{0}-approvers', parameters('scopes')[copyIndex()])).id]\" } } }";

        var run = await host.CreateAsync(host.Resources(Approvers + ", " + alice, outputs, Parameters));

        Check.Succeeded(run);
        string aliceId = host.IdOf("/users", "userPrincipalName", "alice@contoso.com");
        var output = Assert.IsType<JsonArray>(run.Output("approverIds"));
        Assert.Equal(3, output.Count);
        foreach (var (scope, i) in new[] { "dev", "uat", "prd" }.Select((s, i) => (s, i)))
        {
            string groupId = host.IdOf("/groups", "uniqueName", $"pg-{scope}-approvers");
            Assert.Contains(aliceId, host.Directory.References("/groups/" + groupId + "/members"));
            Assert.Equal(scope, output[i]!["scope"]!.GetValue<string>());
            Assert.Equal(groupId, output[i]!["id"]!.GetValue<string>());
        }

        var rerun = await host.CreateAsync(host.Resources(Approvers + ", " + alice, outputs, Parameters));
        Check.Succeeded(rerun);
        Check.NoWrites(rerun);
    }

    [Fact]
    public async Task AFlattenedScopesTimesTiersLoop_ExpandsEveryPair_AndItsReferencesStayExact()
    {
        var host = new ProviderHost();
        string variables = "\"tiers\": [ \"privileged\", \"reader\" ]";
        string access = "{ \"copy\": { \"name\": \"access\", \"count\": \"[mul(length(parameters('scopes')), length(variables('tiers')))]\" }, \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", "
            + "\"name\": \"[format('pg-{0}-{1}-access', parameters('scopes')[div(copyIndex(), length(variables('tiers')))], variables('tiers')[mod(copyIndex(), length(variables('tiers')))])]\", "
            + "\"properties\": { \"displayName\": \"A\", \"mailEnabled\": false, \"securityEnabled\": true, \"description\": \"[reference(format('pg-{0}-approvers', parameters('scopes')[div(copyIndex(), length(variables('tiers')))])).id]\" } }";
        string json = Templates.Envelope(Approvers + ", " + access, null, Parameters).Replace("\"resources\":", "\"variables\": { " + variables + " }, \"resources\":", StringComparison.Ordinal);

        var plan = await host.ExpandAsync(host.Parse(json));

        var names = plan.Instances.Where(i => i.Copy?.Name == "access").Select(i => i.Id.NameLeaf).ToArray();
        Assert.Equal(["pg-dev-privileged-access", "pg-dev-reader-access", "pg-uat-privileged-access", "pg-uat-reader-access", "pg-prd-privileged-access", "pg-prd-reader-access"], names);
        Assert.Equal(["pg-uat-approvers"], DependencyNames(plan, "pg-uat-reader-access"));
    }
}
