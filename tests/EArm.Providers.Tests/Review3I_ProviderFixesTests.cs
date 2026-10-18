using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Providers.Tests.Infrastructure;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// Review 3I: federated identity credentials address their parent by object id (finding 10), conditional access reads ask for unknown enum members and
/// know <c>riskRemediation</c> (finding 6), sovereign-cloud references are written through the provider (finding 2).
/// </summary>
public sealed class Review3I_ProviderFixesTests
{
    private const string FicType = FederatedIdentityCredentialsProvider.ResourceType;
    private const string PolicyType = ConditionalAccessPoliciesProvider.ResourceType;

    private static string Fic()
    {
        return Templates.Resource(FicType, "ext-app/github-main", """{ "issuer": "https://token.actions.githubusercontent.com", "subject": "repo:contoso/orders:ref:refs/heads/main" }""");
    }

    // ---- finding 10 ----

    [Fact]
    public async Task NoCredentialCallIsAddressedThroughTheUniqueNameKeyOfItsParent()
    {
        var host = new ProviderHost();
        var app = host.Directory.AddApplication("ext-app", "External");

        var run = await host.CreateAsync(host.Resources(Fic()));

        Check.Succeeded(run);
        Assert.DoesNotContain(run.Requests, r => r.Path.Contains("(uniqueName='ext-app')/", StringComparison.Ordinal));
        Assert.All(
            run.Requests.Where(r => r.Path.Contains("federatedIdentityCredentials", StringComparison.Ordinal)),
            r => Assert.StartsWith("/applications/" + app["id"]!.GetValue<string>() + "/federatedIdentityCredentials", r.Path, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACredentialWhoseParentDoesNotExistFailsWithAClearMessage()
    {
        var host = new ProviderHost();

        var run = await host.CreateAsync(host.Resources(Fic()));

        Assert.False(run.Succeeded);
        Assert.Contains("ext-app", run.Errors, StringComparison.Ordinal);
        Check.NoWrites(run);
    }

    [Fact]
    public async Task TheParentIsLookedUpOncePerRunAndNotOncePerCall()
    {
        var host = new ProviderHost();
        host.Directory.AddApplication("ext-app", "External");

        var run = await host.CreateAsync(host.Resources(Fic()));

        Check.Succeeded(run);
        Assert.Single(run.Requests, r => r.Method == "GET" && r.Path == "/applications(uniqueName='ext-app')");
    }

    // ---- finding 6 ----

    private static string Policy(string controls)
    {
        return Templates.Resource(PolicyType, "CA-Risk", "{ \"conditions\": { \"users\": { \"includeGroups\": [\"aaaaaaaa-1111-2222-3333-444444444444\"] }, \"applications\": { \"includeApplications\": [\"All\"] } }, \"grantControls\": { \"builtInControls\": " + controls + " } }");
    }

    [Fact]
    public async Task EveryPolicyReadAsksForUnknownEnumMembers()
    {
        var host = new ProviderHost();
        Check.Succeeded(await host.CreateAsync(host.Resources(Policy("""["mfa"]"""))));
        host.ClearRequests();

        var rerun = await host.CreateAsync(host.Resources(Policy("""["mfa"]""")));

        Check.Succeeded(rerun);
        var gets = rerun.Requests.Where(r => r.Method == "GET" && r.Path.StartsWith("/identity/conditionalAccess/policies", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(gets);
        Assert.All(gets, r => Assert.True(r.HasHeader("Prefer", "include-unknown-enum-members"), r.Path));
    }

    [Fact]
    public async Task ThePolicyWritesAskForUnknownEnumMembersToo()
    {
        var host = new ProviderHost();

        var run = await host.CreateAsync(host.Resources(Policy("""["mfa"]""")));

        Check.Succeeded(run);
        Assert.True(Assert.Single(run.Writes).HasHeader("Prefer", "include-unknown-enum-members"));
    }

    [Fact]
    public async Task ARiskRemediationControlIsAcceptedAndAPasswordChangeCombinationIsNot()
    {
        var host = new ProviderHost();

        var ok = await host.WhatIfAsync(host.Resources(Policy("""["riskRemediation"]""")));
        var combined = await host.WhatIfAsync(host.Resources(Policy("""["passwordChange", "riskRemediation"]""")));

        Assert.False(ok.ChangeOfType(PolicyType).HasError);
        Assert.True(combined.ChangeOfType(PolicyType).HasError);
        Assert.Contains("separately", combined.ChangeOfType(PolicyType).UnsupportedReason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGeneratedSchemaListsRiskRemediation()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EArm.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        string schema = File.ReadAllText(Path.Combine(directory.FullName, "schemas", "2026-10-01", "types", "Microsoft.Entra", "conditionalAccess.policies.json"));
        var parsed = JsonNode.Parse(schema)!;
        Assert.Contains("\"riskRemediation\"", schema, StringComparison.Ordinal);
        Assert.NotNull(parsed);
    }
}
