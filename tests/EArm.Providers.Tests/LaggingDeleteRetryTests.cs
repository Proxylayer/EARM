using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Providers.Tests.Infrastructure;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// Deletes Graph refuses until an earlier change of the same run has reached the Conditional Access service (<see cref="LaggingDeleteRetry"/>): a named
/// location still "trusted" right after it was unmarked (Graph 1177), and a strength / named location still "referenced" right after the policy that
/// referenced it was deleted. The fake directory models the lag in refused attempts (<c>ConditionalAccessLagAttempts</c>); nothing sleeps.
/// </summary>
public sealed class LaggingDeleteRetryTests
{
    private const string LocType = NamedLocationsProvider.ResourceType;
    private const string PolType = ConditionalAccessPoliciesProvider.ResourceType;
    private const string StrengthType = AuthenticationStrengthsProvider.ResourceType;
    private const string LocPath = "/identity/conditionalAccess/namedLocations";
    private const string PolPath = "/identity/conditionalAccess/policies";
    private const string StrengthPath = "/policies/authenticationStrengthPolicies";
    private const string LocName = "Contoso offices";
    private const string StrengthName = "Phish-resistant";
    private const string GroupId = "aaaaaaaa-1111-2222-3333-444444444444";

    private static string Location(bool trusted = false)
    {
        return Templates.Resource(LocType, LocName, "{ \"@odata.type\": \"#microsoft.graph.ipNamedLocation\", \"isTrusted\": " + (trusted ? "true" : "false") + ", \"ipRanges\": [ { \"cidrAddress\": \"10.0.0.0/8\" } ] }");
    }

    private static string Strength()
    {
        return Templates.Resource(StrengthType, StrengthName, "{ \"displayName\": \"" + StrengthName + "\", \"allowedCombinations\": [\"fido2\"] }");
    }

    private static string PolicyUsingLocationAndStrength()
    {
        const string conditions = "{ \"users\": { \"includeGroups\": [\"" + GroupId + "\"] }, \"applications\": { \"includeApplications\": [\"All\"] }, \"locations\": { \"includeLocations\": [\"All\"], \"excludeLocations\": [\"[reference('" + LocName + "').id]\"] } }";
        const string grant = "{ \"authenticationStrength\": { \"id\": \"[reference('" + StrengthName + "').id]\" } }";
        return Templates.Resource(PolType, "CA-Test-Lag", "{ \"conditions\": " + conditions + ", \"grantControls\": " + grant + " }",
            extra: ", \"dependsOn\": [\"" + "[resourceId('" + LocType + "', '" + LocName + "')]\", \"[resourceId('" + StrengthType + "', '" + StrengthName + "')]\"]");
    }

    [Fact]
    public async Task DeploymentDelete_WaitsOutTheReferencesOfThePolicyItJustDeleted()
    {
        var host = new ProviderHost();
        var document = host.Resources(Location() + ", " + Strength() + ", " + PolicyUsingLocationAndStrength());
        Check.Succeeded(await host.CreateAsync(document));
        int delaysBefore = host.ReplicationDelays.Count;
        host.Directory.ConditionalAccessLagAttempts = 2;
        host.ClearRequests();

        var deletion = await host.DeleteDeclaredAsync(document);

        Assert.True(deletion.Succeeded, string.Join("; ", deletion.Outcomes.Select(o => o.Error)));
        Assert.All(deletion.Outcomes, o => Assert.Equal(DeletionStatus.Deleted, o.Status));
        Assert.Equal(0, host.Directory.Count(PolPath));
        Assert.Equal(0, host.Directory.Count(StrengthPath));
        Assert.Equal(0, host.Directory.Count(LocPath));

        // The strength and the location were each refused twice ("referenced") and waited out on the replication schedule.
        Assert.Equal(4, host.ReplicationDelays.Count - delaysBefore);
        Assert.Equal(3, host.Fake.Writes.Count(w => w.Method == "DELETE" && w.Path.StartsWith(StrengthPath + "/", StringComparison.Ordinal)));
        Assert.Equal(3, host.Fake.Writes.Count(w => w.Method == "DELETE" && w.Path.StartsWith(LocPath + "/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task DeploymentDelete_OfATrustedLocation_RepeatsTheDeleteUntilTheUnmarkHasPropagated()
    {
        var host = new ProviderHost();
        var document = host.Resources(Location(trusted: true));
        Check.Succeeded(await host.CreateAsync(document));
        int delaysBefore = host.ReplicationDelays.Count;
        host.Directory.ConditionalAccessLagAttempts = 4;
        host.ClearRequests();

        var deletion = await host.DeleteDeclaredAsync(document);

        Assert.True(deletion.Succeeded, string.Join("; ", deletion.Outcomes.Select(o => o.Error)));
        Assert.Equal(0, host.Directory.Count(LocPath));
        // Each attempt unmarks, deletes, meets 1177, unmarks again and deletes once more: attempts 1 and 2 are refused, attempt 3 deletes.
        Assert.Equal(2, host.ReplicationDelays.Count - delaysBefore);
        Assert.Equal(5, host.Fake.Writes.Count(w => w.Method == "DELETE"));
        Assert.All(host.Fake.Writes.Where(w => w.Method == "PATCH"), p => Assert.Equal("{\"@odata.type\":\"#microsoft.graph.ipNamedLocation\",\"isTrusted\":false}", p.Body!.ToJsonString()));
    }

    [Fact]
    public async Task DeploymentDelete_OfATrustedLocation_GivesUpAfterTheReplicationWindow()
    {
        var host = new ProviderHost();
        var document = host.Resources(Location(trusted: true));
        Check.Succeeded(await host.CreateAsync(document));
        int delaysBefore = host.ReplicationDelays.Count;
        host.Directory.ConditionalAccessLagAttempts = 1000;

        var deletion = await host.DeleteDeclaredAsync(document);

        var outcome = Assert.Single(deletion.Outcomes);
        Assert.Equal(DeletionStatus.Failed, outcome.Status);
        Assert.Equal(EngineDiagnosticCodes.NamedLocationTrusted, outcome.ErrorCode);
        // The adaptive schedule: 1 + 2 + 4 s, then sixteen 5 s waits (87 s; one more would pass the 90 s window).
        Assert.Equal(19, host.ReplicationDelays.Count - delaysBefore);
        Assert.Equal(1, host.Directory.Count(LocPath));
    }

    [Fact]
    public async Task DeploymentDelete_OfAStrengthAPolicyOutsideTheRunStillReferences_FailsAtOnce()
    {
        var host = new ProviderHost();
        var document = host.Resources(Strength());
        Check.Succeeded(await host.CreateAsync(document));
        string strengthId = host.IdOf(StrengthPath, "displayName", StrengthName);
        host.Directory.Add(PolPath, new JsonObject { ["displayName"] = "Not in the template", ["state"] = "disabled", ["grantControls"] = new JsonObject { ["authenticationStrength"] = new JsonObject { ["id"] = strengthId } } });

        int delaysBefore = host.ReplicationDelays.Count;
        var deletion = await host.DeleteDeclaredAsync(document);

        var outcome = Assert.Single(deletion.Outcomes);
        Assert.Equal(DeletionStatus.Failed, outcome.Status);
        Assert.Equal(EngineDiagnosticCodes.AuthenticationStrengthReferenced, outcome.ErrorCode);
        Assert.Equal(0, host.ReplicationDelays.Count - delaysBefore);
    }

    [Fact]
    public async Task KindChange_OfATrustedLocation_DeletesTheOldObjectOnceTheUnmarkHasPropagated()
    {
        var host = new ProviderHost();
        Check.Succeeded(await host.CreateAsync(host.Resources(Location(trusted: true))));
        int delaysBefore = host.ReplicationDelays.Count;
        host.Directory.ConditionalAccessLagAttempts = 3;

        var country = Templates.Resource(LocType, LocName, "{ \"@odata.type\": \"#microsoft.graph.countryNamedLocation\", \"countriesAndRegions\": [\"US\"] }");
        var run = await host.CreateAsync(host.Resources(country));

        Check.Succeeded(run);
        // One wait for the refused delete, one between the two reads read-your-write needs to see the replacement in a row.
        Assert.Equal(2, host.ReplicationDelays.Count - delaysBefore);
        var only = Assert.Single(host.Directory.Objects(LocPath));
        Assert.Equal("#microsoft.graph.countryNamedLocation", only["@odata.type"]!.GetValue<string>());
    }
}
