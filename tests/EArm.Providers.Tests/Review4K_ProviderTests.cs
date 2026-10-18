using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Providers;
using EArm.Providers.Tests.Infrastructure;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// Review round 4, K2: the fingerprint (<c>createdDateTime</c>) is selected on every read of the types that have it and never diffed or patched; every provider that
/// reads "the" key value checks the key's property name; the providers that write in several steps hand over what exists when a later step fails.
/// </summary>
public sealed class Review4K_ProviderTests
{
    private const string GroupType = "Microsoft.Entra/groups";
    private const string SkuId = "11111111-2222-3333-4444-555555555555";
    private const string SoftDeletedAppId = "5b5b5b5b-1111-4222-8333-444444444444";

    // ---- (a) createdDateTime ----

    private sealed record Subject(string Label, string Type, string Name, string Properties, string Collection, string KeyProperty, string? Dependencies = null);

    public static TheoryData<string> Subjects => new() { "users", "groups", "applications", "namedLocations", "policies", "authenticationStrengths" };

    private static Subject SubjectOf(string label)
    {
        return label switch
        {
            "users" => new Subject(label, UsersProvider.ResourceType, "u@contoso.com", """{ "displayName": "U", "accountEnabled": true, "passwordProfile": { "password": "Initial-P@ssw0rd-1" } }""", "/users", "userPrincipalName"),
            "groups" => new Subject(label, GroupType, "g1", """{ "displayName": "G", "mailEnabled": false, "securityEnabled": true }""", "/groups", "uniqueName"),
            "applications" => new Subject(label, ApplicationsProvider.ResourceType, "a1", """{ "displayName": "A" }""", "/applications", "uniqueName"),
            "namedLocations" => new Subject(label, NamedLocationsProvider.ResourceType, "Contoso offices", """{ "@odata.type": "#microsoft.graph.ipNamedLocation", "ipRanges": [ { "cidrAddress": "10.0.0.0/8" } ] }""", "/identity/conditionalAccess/namedLocations", "displayName"),
            "policies" => new Subject(label, ConditionalAccessPoliciesProvider.ResourceType, "CA-K4", """{ "conditions": { "users": { "includeGroups": ["aaaaaaaa-1111-2222-3333-444444444444"] }, "applications": { "includeApplications": ["All"] } }, "grantControls": { "builtInControls": ["mfa"], "operator": "OR" } }""", "/identity/conditionalAccess/policies", "displayName"),
            "authenticationStrengths" => new Subject(label, AuthenticationStrengthsProvider.ResourceType, "Orders MFA", """{ "allowedCombinations": ["fido2"], "description": "d" }""", "/policies/authenticationStrengthPolicies", "displayName"),
            _ => throw new ArgumentOutOfRangeException(nameof(label)),
        };
    }

    [Theory]
    [MemberData(nameof(Subjects))]
    public async Task TheFingerprint_IsReadFromTheLiveObject_AndNeverDiffedOrPatched(string label)
    {
        var subject = SubjectOf(label);
        var host = new ProviderHost();
        var document = host.Resources(Templates.Resource(subject.Type, subject.Name, subject.Properties));
        Check.Succeeded(await host.CreateAsync(document));
        string resourceId = "/providers/" + subject.Type + "/" + subject.Name;
        var live = host.Directory.Find(subject.Collection, subject.KeyProperty, subject.Name)!;
        live["createdDateTime"] ??= "2026-03-04T05:06:07Z";
        var expected = ProviderReads.CreatedDateTimeOf(live);
        Assert.NotNull(expected);
        host.ClearRequests();

        var whatIf = await host.WhatIfAsync(document);

        // Whatever the read returned, a live object that carries createdDateTime (and nothing else the template does not say) is NoChange.
        var provider = (ProviderBase)host.Registry.Get(subject.Type);
        var instance = whatIf.Plan.Instances.Single(i => ResourceIdentifier.TypeEquals(i.Type, subject.Type));
        var withFingerprint = (JsonObject)live.DeepClone();
        withFingerprint["createdDateTime"] = "2026-03-04T05:06:07Z";
        var planned = provider.Plan(instance, provider.ToExisting(withFingerprint), new PlanOptions());
        Assert.Equal(EArm.Engine.Providers.ChangeType.NoChange, planned.Type);
        Assert.DoesNotContain(planned.Deltas, d => d.HasEffect);

        var row = whatIf.ChangeOfType(subject.Type);
        Assert.Equal(EArm.Engine.Providers.ChangeType.NoChange, row.Type);
        Assert.DoesNotContain(row.Deltas, d => d.HasEffect);
        Assert.DoesNotContain(row.Deltas, d => d.Path.ToString().Contains("createdDateTime", StringComparison.OrdinalIgnoreCase));
        Check.NoWrites(whatIf);

        var rerun = await host.CreateAsync(document);

        Check.Succeeded(rerun);
        Check.NoWrites(rerun);
        if (label is "users" or "groups" or "applications")
        {
            Assert.Contains(rerun.Requests, r => r.Method == "GET" && r.UrlParameters is not null && r.UrlParameters.TryGetValue("$select", out var select) && select.Contains("createdDateTime", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("groups")]
    [InlineData("applications")]
    [InlineData("users")]
    public async Task AnUpdate_NeverSendsTheFingerprint(string label)
    {
        var subject = SubjectOf(label);
        var host = new ProviderHost();
        Check.Succeeded(await host.CreateAsync(host.Resources(Templates.Resource(subject.Type, subject.Name, subject.Properties))));
        string changed = subject.Properties.Replace("\"displayName\": \"" + (label == "users" ? "U" : label == "groups" ? "G" : "A") + "\"", "\"displayName\": \"Changed\"", StringComparison.Ordinal);

        var run = await host.CreateAsync(host.Resources(Templates.Resource(subject.Type, subject.Name, changed)));

        Check.Succeeded(run);
        var patch = Assert.Single(run.Writes, w => w.Method == "PATCH");
        Assert.DoesNotContain("createdDateTime", patch.Body!.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Changed", patch.Body!["displayName"]!.GetValue<string>());
    }

    [Fact]
    public void TheTypesWithoutTheProperty_AreNotSelectedForIt()
    {
        // Learn: servicePrincipal, administrativeUnit, unifiedRoleDefinition, federatedIdentityCredential and authenticationContextClassReference have no createdDateTime.
        Assert.DoesNotContain("createdDateTime", ServicePrincipalsProvider.ManagedProperties);
        Assert.DoesNotContain("createdDateTime", AdministrativeUnitsRules.ManagedProperties);
        Assert.DoesNotContain("createdDateTime", RoleDefinitionsRules.ManagedProperties);
        Assert.DoesNotContain("createdDateTime", FederatedIdentityCredentialsRules.ManagedProperties);
        Assert.DoesNotContain("createdDateTime", FederatedIdentityCredentialsRules.PreviewManagedProperties);
        Assert.Contains("createdDateTime", UsersProvider.ManagedProperties);
        Assert.Contains("createdDateTime", ApplicationsProvider.ManagedProperties);
    }

    // ---- (b) the key audit ----

    private static ResourceKey KeyOf(string type, params (string Name, string Value)[] values)
    {
        var dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in values)
        {
            dictionary[name] = value;
        }

        return new ResourceKey { Type = type, Values = dictionary };
    }

    [Fact]
    public void SingleKey_ReturnsTheValueOfTheExpectedProperty_IgnoringCaseOfTheName()
    {
        var key = KeyOf(GroupType, ("UniqueName", "g1"));

        Assert.Equal("g1", ProviderSupport.SingleKey(key, "uniqueName"));
        Assert.Equal("g1", ProviderSupport.SingleKey(key, "UNIQUENAME"));
    }

    [Theory]
    [InlineData("displayName")]
    [InlineData("appId")]
    [InlineData("userPrincipalName")]
    public void SingleKey_RefusesAKeyOfAnotherProperty(string wrongName)
    {
        var key = KeyOf(GroupType, (wrongName, "g1"));

        var error = Assert.Throws<ProviderException>(() => ProviderSupport.SingleKey(key, "uniqueName"));

        Assert.Equal(EngineDiagnosticCodes.ResourceKeyUnresolved, error.Code);
        Assert.Contains("carries no uniqueName", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SingleKey_RefusesAnEmptyKey_AndAKeyWithMoreThanTheOneProperty()
    {
        Assert.Throws<ProviderException>(() => ProviderSupport.SingleKey(KeyOf(GroupType), "uniqueName"));

        var two = KeyOf(ApplicationsProvider.ResourceType, ("uniqueName", "a"), ("appId", "5b5b5b5b-1111-4222-8333-444444444444"));
        var error = Assert.Throws<ProviderException>(() => ProviderSupport.SingleKey(two, "uniqueName"));
        Assert.Contains("single property", error.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string, string> NameKeyedTypes => new()
    {
        { GroupType, "uniqueName" },
        { UsersProvider.ResourceType, "userPrincipalName" },
        { AdministrativeUnitsProvider.ResourceType, "displayName" },
        { ConditionalAccessPoliciesProvider.ResourceType, "displayName" },
        { NamedLocationsProvider.ResourceType, "displayName" },
        { AuthenticationStrengthsProvider.ResourceType, "displayName" },
        { RoleDefinitionsProvider.ResourceType, "displayName" },
        { AuthenticationContextClassReferencesProvider.ResourceType, "id" },
    };

    [Theory]
    [MemberData(nameof(NameKeyedTypes))]
    public async Task EveryProviderThatReadsTheSingleKey_ChecksThePropertyName_BeforeAnyRequest(string type, string expectedName)
    {
        var host = new ProviderHost();
        var provider = host.Registry.Get(type);
        Assert.Equal(expectedName, host.Catalog.Find(type)!.NameMapsTo);

        // The key of another type: a property the catalog does not name for this one.
        var wrong = KeyOf(type, expectedName == "id" ? ("displayName", "c1") : ("appId", "5b5b5b5b-1111-4222-8333-444444444444"));
        var error = await Assert.ThrowsAsync<ProviderException>(() => provider.ReadAsync(wrong, host.Fake, ProviderHost.Ct));
        Assert.Equal(EngineDiagnosticCodes.ResourceKeyUnresolved, error.Code);
        Assert.Empty(host.Fake.Requests);

        // And a key that has the right property plus a stray one is no single key either.
        var stray = KeyOf(type, (expectedName, "x"), ("stray", "y"));
        await Assert.ThrowsAsync<ProviderException>(() => provider.ReadAsync(stray, host.Fake, ProviderHost.Ct));
        Assert.Empty(host.Fake.Requests);
    }

    // ---- (c) ProviderException.Partial ----

    private static async Task<AppliedResource> CreateDirectAsync(ProviderHost host, string type, string name, string properties)
    {
        var whatIf = await host.WhatIfAsync(host.Resources(Templates.Resource(type, name, properties)));
        var instance = whatIf.Plan.Instances.Single(i => ResourceIdentifier.TypeEquals(i.Type, type));
        var provider = host.Registry.Get(type);
        host.ClearRequests();
        return await provider.CreateAsync(instance, host.Fake, ProviderHost.Ct);
    }

    private static void SeedSkus(ProviderHost host)
    {
        host.Directory.Add("/subscribedSkus", new JsonObject { ["skuId"] = SkuId, ["skuPartNumber"] = "ENTERPRISEPREMIUM", ["prepaidUnits"] = new JsonObject { ["enabled"] = 10 }, ["consumedUnits"] = 1 });
    }

    private const string LicensedGroup = """{ "displayName": "Licensed", "mailEnabled": false, "securityEnabled": true, "assignedLicenses": [{ "skuPartNumber": "ENTERPRISEPREMIUM", "disabledPlans": [] }] }""";

    [Fact]
    public async Task Groups_ALicenseFailureAfterTheCreate_HandsOverTheGroup()
    {
        var host = new ProviderHost();
        SeedSkus(host);
        host.Fake.Failures.Forbid("assignLicense", "POST");

        var error = await Assert.ThrowsAsync<ProviderException>(() => CreateDirectAsync(host, GroupType, "licensed", LicensedGroup));

        var partial = Assert.IsType<AppliedResource>(error.Partial);
        Assert.Equal(host.IdOf("/groups", "uniqueName", "licensed"), partial.GraphId);
        Assert.Equal("licensed", partial.Key.SingleName == "uniqueName" ? partial.Key.Single : null);
        Assert.Contains("licenses failed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Groups_ALicenseFailureAfterTheCreate_IsRecordedByTheExecutor_AsAFailedButRecordedResource()
    {
        var host = new ProviderHost();
        SeedSkus(host);
        host.Fake.Failures.Forbid("assignLicense", "POST");

        var run = await host.CreateAsync(host.Resources(Templates.Resource(GroupType, "licensed", LicensedGroup)));

        Assert.False(run.Succeeded);
        var operation = run.OpOfType(GroupType);
        Assert.Equal(OperationStatus.Failed, operation.Status);
        Assert.Contains("a later step failed", operation.Error, StringComparison.Ordinal);
        Assert.Equal(host.IdOf("/groups", "uniqueName", "licensed"), run.GraphIdOf("/providers/Microsoft.Entra/groups/licensed"));
    }

    [Fact]
    public async Task Groups_ALicenseFailureAfterARestore_HandsOverTheRestoredGroup()
    {
        var host = new ProviderHost { OnSoftDeletedConflict = SoftDeletedConflict.Restore };
        SeedSkus(host);
        var deleted = host.Directory.AddDeleted("/groups", new JsonObject
        {
            ["uniqueName"] = "licensed",
            ["displayName"] = "Old licensed",
            ["mailNickname"] = "licensed",
            ["groupTypes"] = new JsonArray(),
            ["mailEnabled"] = false,
            ["securityEnabled"] = true,
        });
        host.Fake.Failures.Forbid("assignLicense", "POST");

        var error = await Assert.ThrowsAsync<ProviderException>(() => CreateDirectAsync(host, GroupType, "licensed", LicensedGroup));

        var partial = Assert.IsType<AppliedResource>(error.Partial);
        Assert.Equal(deleted["id"]!.GetValue<string>(), partial.GraphId);
        Assert.Contains("restored", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Applications_AFailureOfTheIdentifierUrisPatch_HandsOverTheApplication()
    {
        var host = new ProviderHost();
        host.Fake.Failures.Forbid("/applications/", "PATCH");

        var error = await Assert.ThrowsAsync<ProviderException>(() => CreateDirectAsync(host, ApplicationsProvider.ResourceType, "uris-app", """{ "displayName": "Uris", "identifierUris": ["api://{appId}"] }"""));

        var partial = Assert.IsType<AppliedResource>(error.Partial);
        Assert.Equal(host.IdOf("/applications", "uniqueName", "uris-app"), partial.GraphId);
        Assert.Contains("identifierUris", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Applications_AFailureAfterTheRestore_HandsOverTheRestoredApplication()
    {
        var host = new ProviderHost { OnSoftDeletedConflict = SoftDeletedConflict.Restore };
        var deleted = host.Directory.AddDeleted("/applications", new JsonObject { ["uniqueName"] = "restored-app", ["displayName"] = "Old", ["appId"] = Guid.NewGuid().ToString("D") });
        host.Fake.Failures.Forbid("uniqueName", "PATCH");

        var error = await Assert.ThrowsAsync<ProviderException>(() => CreateDirectAsync(host, ApplicationsProvider.ResourceType, "restored-app", """{ "displayName": "New" }"""));

        var partial = Assert.IsType<AppliedResource>(error.Partial);
        Assert.Equal(deleted["id"]!.GetValue<string>(), partial.GraphId);
        Assert.Contains("restored", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Applications_ThePartialResult_IsRecordedByTheExecutor()
    {
        var host = new ProviderHost();
        host.Fake.Failures.Forbid("/applications/", "PATCH");

        var run = await host.CreateAsync(host.Resources(Templates.Resource(ApplicationsProvider.ResourceType, "uris-app", """{ "displayName": "Uris", "identifierUris": ["api://{appId}"] }""")));

        Assert.False(run.Succeeded);
        Assert.Contains("a later step failed", run.OpOfType(ApplicationsProvider.ResourceType).Error, StringComparison.Ordinal);
        Assert.Equal(host.IdOf("/applications", "uniqueName", "uris-app"), run.GraphIdOf("/providers/Microsoft.Entra/applications/uris-app"));
    }

    [Fact]
    public async Task ServicePrincipals_AFailureAfterTheRestore_HandsOverTheRestoredPrincipal()
    {
        var host = new ProviderHost { OnSoftDeletedConflict = SoftDeletedConflict.Restore };
        host.Directory.AddApplication("sp-app", "SP app", SoftDeletedAppId);
        var deleted = host.Directory.AddDeleted("/servicePrincipals", new JsonObject { ["appId"] = SoftDeletedAppId, ["displayName"] = "SP app", ["accountEnabled"] = true, ["servicePrincipalType"] = "Application" });
        host.Fake.Failures.Forbid("/servicePrincipals/", "PATCH");

        var error = await Assert.ThrowsAsync<ProviderException>(() => CreateDirectAsync(host, ServicePrincipalsProvider.ResourceType, SoftDeletedAppId, """{ "notes": "differs from the restored object" }"""));

        var partial = Assert.IsType<AppliedResource>(error.Partial);
        Assert.Equal(deleted["id"]!.GetValue<string>(), partial.GraphId);
        Assert.Contains("restored", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnrestoredFailure_IsNotAPartialResult()
    {
        // No restore happened: a failing upsert leaves nothing behind, so there is nothing to hand over.
        var host = new ProviderHost();
        host.Fake.Failures.Forbid("uniqueName", "PATCH");

        var error = await Assert.ThrowsAnyAsync<Exception>(() => CreateDirectAsync(host, ApplicationsProvider.ResourceType, "plain-app", """{ "displayName": "Plain" }"""));

        Assert.IsNotType<ProviderException>(error);
    }
}
