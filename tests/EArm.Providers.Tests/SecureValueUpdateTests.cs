using System.Linq;
using System.Threading.Tasks;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Providers.Tests.Infrastructure;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// A secure value Graph reads back updates an existing object: a conditional access policy whose <c>state</c> comes from a
/// <c>securestring</c> parameter is switched to <c>enabled</c> with <c>--allow-ca-enforce</c> (it used to stay as it was, because every secure
/// value was create-only), the what-if shows a masked <c>Modify</c>, and a rerun is <c>NoChange</c> (no PATCH, so
/// <c>--exit-code-on-changes</c> does not fire).
/// </summary>
public sealed class SecureValueUpdateTests
{
    private const string PolType = ConditionalAccessPoliciesProvider.ResourceType;
    private const string PolPath = "/identity/conditionalAccess/policies";
    private const string GroupId = "aaaaaaaa-1111-2222-3333-444444444444";

    private static string Policy(string state)
    {
        return Templates.Resource(PolType, "CA-Secure", "{ \"state\": \"" + state + "\", \"conditions\": { \"users\": { \"includeGroups\": [\"" + GroupId + "\"] }, \"applications\": { \"includeApplications\": [\"All\"] } }, \"grantControls\": { \"builtInControls\": [\"mfa\"] } }");
    }

    private static string LiveState(ProviderHost host)
    {
        return host.Directory.Objects(PolPath).Single()["state"]!.GetValue<string>();
    }

    [Fact]
    public async Task A_secure_state_switches_an_existing_policy_and_a_rerun_changes_nothing()
    {
        var host = new ProviderHost { AllowCaEnforce = true };
        Check.Succeeded(await host.CreateAsync(host.Resources(Policy("disabled"))));
        var document = host.Resources(Policy("[parameters('state')]"), parameters: Templates.SecureParameter("state"), values: ("state", "enabled"));

        var whatIf = await host.WhatIfAsync(document);
        var row = Assert.Single(whatIf.Plan.Changes);
        Assert.Equal(ChangeType.Modify, row.Type);
        var delta = Assert.Single(row.Deltas, d => d.Path.ToString() == "properties.state");
        Assert.Equal(PropertyChangeType.Modify, delta.Kind);
        Assert.True(delta.IsSecret);
        Assert.Equal("\"***\"", delta.After!.ToJsonString());
        Assert.Equal("\"***\"", delta.Before!.ToJsonString());

        var run = await host.CreateAsync(document);
        Check.Succeeded(run);
        Assert.Equal("enabled", Check.Body(Assert.Single(run.Writes))["state"]!.GetValue<string>());
        Assert.Equal("enabled", LiveState(host));

        var again = await host.WhatIfAsync(document);
        Assert.Equal(ChangeType.NoChange, Assert.Single(again.Plan.Changes).Type);
        var rerun = await host.CreateAsync(document);
        Check.Succeeded(rerun);
        Assert.Empty(rerun.Writes);
        Assert.False(rerun.Result!.HasChanges);
    }
}
