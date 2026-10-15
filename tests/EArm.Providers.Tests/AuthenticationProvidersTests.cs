using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Az;
using EArm.Core;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Engine.State;
using EArm.Providers.Tests.Infrastructure;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>Authentication context class references and authentication strengths (WI-20): pure rules and end-to-end scenarios.</summary>
public sealed class AuthenticationProvidersTests
{
    private const string AccrType = AuthenticationContextClassReferencesProvider.ResourceType;
    private const string StrengthType = AuthenticationStrengthsProvider.ResourceType;
    private const string AccrPath = "/identity/conditionalAccess/authenticationContextClassReferences";
    private const string StrengthPath = "/policies/authenticationStrengthPolicies";

    private static string Accr(string name, string properties)
    {
        return Templates.Resource(AccrType, name, properties);
    }

    private static string Strength(string name, string properties)
    {
        return Templates.Resource(StrengthType, name, properties);
    }

    // ---- rules: authentication context ----

    [Theory]
    [InlineData("c1", true)]
    [InlineData("c9", true)]
    [InlineData("c10", true)]
    [InlineData("c99", true)]
    [InlineData("c0", false)]
    [InlineData("c01", false)]
    [InlineData("c100", false)]
    [InlineData("C5", false)]
    [InlineData("x5", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ContextIdValidation(string? name, bool valid)
    {
        Assert.Equal(valid, AuthenticationContextClassReferencesRules.IsValidId(name));
        Assert.Equal(valid, AuthenticationContextClassReferencesRules.NameProblem(name) is null);
    }

    [Fact]
    public void ContextNormalizeDesired_DefaultsIsAvailable_AndReportsShapeProblems()
    {
        var ok = (JsonObject)JsonNode.Parse("""{ "displayName": "Sensitive", "isAvailable": null }""")!;
        Assert.Empty(AuthenticationContextClassReferencesRules.NormalizeDesired(ok));
        Assert.True(ok["isAvailable"]!.GetValue<bool>());

        var missing = new JsonObject();
        Assert.Contains(AuthenticationContextClassReferencesRules.NormalizeDesired(missing), p => p.Contains("displayName is required", StringComparison.Ordinal));
        Assert.True(missing["isAvailable"]!.GetValue<bool>());

        var wrong = (JsonObject)JsonNode.Parse("""{ "displayName": 5, "description": 6, "isAvailable": "yes" }""")!;
        var problems = AuthenticationContextClassReferencesRules.NormalizeDesired(wrong);
        Assert.Contains(problems, p => p.Contains("displayName must be a string", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("description must be a string", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("isAvailable must be a boolean", StringComparison.Ordinal));
    }

    [Fact]
    public void ContextHelpers_ReadAvailability_AndBuildBodies()
    {
        Assert.True(AuthenticationContextClassReferencesRules.IsAvailable((JsonObject)JsonNode.Parse("""{ "isAvailable": true }""")!));
        Assert.False(AuthenticationContextClassReferencesRules.IsAvailable((JsonObject)JsonNode.Parse("""{ "isAvailable": false }""")!));
        Assert.Null(AuthenticationContextClassReferencesRules.IsAvailable((JsonObject)JsonNode.Parse("""{ "isAvailable": "x" }""")!));
        Assert.Null(AuthenticationContextClassReferencesRules.IsAvailable(null));
        Assert.Equal("""{"isAvailable":false}""", AuthenticationContextClassReferencesRules.UnavailableBody().ToJsonString());
        var body = (JsonObject)JsonNode.Parse("""{ "ID": "c1", "displayName": "x" }""")!;
        AuthenticationContextClassReferencesRules.RemoveServerAssigned(body);
        Assert.Equal("""{"displayName":"x"}""", body.ToJsonString());
    }

    // ---- rules: authentication strengths ----

    [Theory]
    [InlineData("fido2", "fido2")]
    [InlineData("Password, MicrosoftAuthenticatorPush", "microsoftauthenticatorpush,password")]
    [InlineData(" a , b ", "a,b")]
    public void NormalizeCombination_SortsAndLowerCases(string input, string expected)
    {
        Assert.Equal(expected, AuthenticationStrengthsRules.NormalizeCombination(input));
    }

    [Fact]
    public void CanonicalCombinations_AreSortedDistinct_AndNullWhenMalformed()
    {
        Assert.Equal(["fido2", "password,x"], AuthenticationStrengthsRules.CanonicalCombinations(new JsonArray("x,password", "fido2", "FIDO2")));
        Assert.Null(AuthenticationStrengthsRules.CanonicalCombinations(new JsonArray(1)));
        Assert.Null(AuthenticationStrengthsRules.CanonicalCombinations(JsonValue.Create("fido2")));
        Assert.Null(AuthenticationStrengthsRules.CanonicalCombinations(null));

        var left = (JsonObject)JsonNode.Parse("""{ "allowedCombinations": ["password,fido2", "x509CertificateMultiFactor"] }""")!;
        var right = (JsonObject)JsonNode.Parse("""{ "allowedCombinations": ["x509certificatemultifactor", "FIDO2,password"] }""")!;
        Assert.True(AuthenticationStrengthsRules.CombinationsEqual(left, right));
        Assert.False(AuthenticationStrengthsRules.CombinationsEqual(left, (JsonObject)JsonNode.Parse("""{ "allowedCombinations": ["fido2"] }""")!));
        Assert.False(AuthenticationStrengthsRules.CombinationsEqual(left, new JsonObject()));
        Assert.True(AuthenticationStrengthsRules.DeclaresCombinations(left));
        Assert.False(AuthenticationStrengthsRules.DeclaresCombinations(new JsonObject { ["allowedCombinations"] = new JsonArray() }));
    }

    [Fact]
    public void StrengthNormalizeDesired_DropsCustomPolicyType_AndReportsProblems()
    {
        var custom = (JsonObject)JsonNode.Parse("""{ "policyType": "custom", "allowedCombinations": [" fido2 "] }""")!;
        Assert.Empty(AuthenticationStrengthsRules.NormalizeDesired(custom));
        Assert.False(custom.ContainsKey("policyType"));
        Assert.Equal("fido2", custom["allowedCombinations"]![0]!.GetValue<string>());

        var builtIn = (JsonObject)JsonNode.Parse("""{ "policyType": "builtIn", "allowedCombinations": ["fido2"] }""")!;
        Assert.Contains(AuthenticationStrengthsRules.NormalizeDesired(builtIn), p => p.Contains("built-in authentication strengths cannot be managed", StringComparison.Ordinal));

        Assert.Contains(AuthenticationStrengthsRules.NormalizeDesired((JsonObject)JsonNode.Parse("""{ "allowedCombinations": [] }""")!), p => p.Contains("at least one", StringComparison.Ordinal));
        Assert.Contains(AuthenticationStrengthsRules.NormalizeDesired((JsonObject)JsonNode.Parse("""{ "allowedCombinations": null }""")!), p => p.Contains("at least one", StringComparison.Ordinal));
        Assert.Contains(AuthenticationStrengthsRules.NormalizeDesired((JsonObject)JsonNode.Parse("""{ "allowedCombinations": "fido2" }""")!), p => p.Contains("must be an array of strings", StringComparison.Ordinal));
        var items = AuthenticationStrengthsRules.NormalizeDesired((JsonObject)JsonNode.Parse("""{ "allowedCombinations": [1, "  "] }""")!);
        Assert.Contains(items, p => p.Contains("[0] must be a string", StringComparison.Ordinal));
        Assert.Contains(items, p => p.Contains("[1] is empty", StringComparison.Ordinal));
        Assert.Empty(AuthenticationStrengthsRules.NormalizeDesired(new JsonObject()));
    }

    [Fact]
    public void StrengthHelpers_AdoptLiveSpelling_BuildBodies_AndStripServerProperties()
    {
        var desired = (JsonObject)JsonNode.Parse("""{ "allowedCombinations": ["fido2", "Password,x"] }""")!;
        var live = (JsonObject)JsonNode.Parse("""{ "allowedCombinations": ["x,password", "FIDO2"] }""")!;
        Assert.True(AuthenticationStrengthsRules.AdoptLiveCombinations(desired, live));
        Assert.Equal("""["x,password","FIDO2"]""", desired["allowedCombinations"]!.ToJsonString());
        Assert.False(AuthenticationStrengthsRules.AdoptLiveCombinations(desired, (JsonObject)JsonNode.Parse("""{ "allowedCombinations": ["other"] }""")!));

        Assert.Equal("""{"allowedCombinations":["fido2"]}""", AuthenticationStrengthsRules.UpdateCombinationsBody((JsonObject)JsonNode.Parse("""{ "displayName": "d", "allowedCombinations": ["fido2"] }""")!).ToJsonString());
        var body = (JsonObject)JsonNode.Parse("""{ "displayName": "d", "allowedCombinations": ["fido2"], "policyType": "custom", "id": "i", "requirementsSatisfied": "mfa" }""")!;
        Assert.True(AuthenticationStrengthsRules.RemoveCombinations(body));
        Assert.False(AuthenticationStrengthsRules.RemoveCombinations(body));
        AuthenticationStrengthsRules.RemoveServerAssigned(body);
        Assert.Equal("""{"displayName":"d"}""", body.ToJsonString());
        Assert.True(AuthenticationStrengthsRules.IsBuiltIn((JsonObject)JsonNode.Parse("""{ "policyType": "BuiltIn" }""")!));
        Assert.False(AuthenticationStrengthsRules.IsBuiltIn((JsonObject)JsonNode.Parse("""{ "policyType": "custom" }""")!));
    }

    // ---- end to end: authentication context ----

    [Fact]
    public async Task Context_IsUpsertedById_AndRerunIsNoChange()
    {
        var host = new ProviderHost();
        var document = host.Resources(Accr("c10", """{ "displayName": "Sensitive actions", "description": "Step-up" }"""));

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        var write = Assert.Single(run.Writes);
        Assert.Equal("PATCH", write.Method);
        Assert.Equal(AccrPath + "/c10", write.Path);
        Assert.Equal(1, run.Executed.SucceededCount);
        var body = Check.Body(write);
        Assert.False(body.ContainsKey("id"));
        Assert.True(body["isAvailable"]!.GetValue<bool>());
        Assert.Equal("Sensitive actions", body["displayName"]!.GetValue<string>());
        Assert.Equal("c10", host.Directory.Find(AccrPath, "id", "c10")!["id"]!.GetValue<string>());

        var rerun = await host.CreateAsync(document);
        Assert.Equal(ChangeType.NoChange, Assert.Single(rerun.Executed.Operations).ChangeType);
        Check.NoWrites(rerun);
    }

    [Fact]
    public async Task Context_Drift_IsPatchedAlone()
    {
        var host = new ProviderHost();
        host.Directory.Add(AccrPath, new JsonObject { ["id"] = "c10", ["displayName"] = "Old", ["description"] = "d", ["isAvailable"] = true });

        var run = await host.CreateAsync(host.Resources(Accr("c10", """{ "displayName": "New", "description": "d" }""")));

        Check.Succeeded(run);
        var write = Assert.Single(run.Writes);
        Assert.Equal("PATCH", write.Method);
        Assert.Equal(AccrPath + "/c10", write.Path);
        Assert.Equal("""{"displayName":"New"}""", write.Body!.ToJsonString());
    }

    [Theory]
    [InlineData("c0")]
    [InlineData("c100")]
    [InlineData("C5")]
    public async Task Context_InvalidIds_AreRejected(string name)
    {
        var host = new ProviderHost();

        object? outcome = null;
        try
        {
            outcome = await host.WhatIfAsync(host.Resources(Accr(name, """{ "displayName": "x" }""")));
        }
        catch (ValidationFailedException ex)
        {
            outcome = ex;
        }

        if (outcome is Run run)
        {
            Assert.True(run.Plan.Changes.Single().HasError);
        }
        else
        {
            Assert.IsType<ValidationFailedException>(outcome);
        }
    }

    [Fact]
    public async Task Context_Delete_MakesItUnavailableFirst_ThenDeletes_AndToleratesNotFound()
    {
        var host = new ProviderHost();
        await host.CreateAsync(host.Resources(Accr("c10", """{ "displayName": "Sensitive" }""")));
        host.ClearRequests();

        var complete = await host.CreateAsync(host.Resources(string.Empty), DeploymentMode.Complete);

        Check.Succeeded(complete);
        var writes = complete.Writes;
        Assert.Equal(2, writes.Count);
        Assert.Equal("PATCH", writes[0].Method);
        Assert.Equal(AccrPath + "/c10", writes[0].Path);
        Assert.Equal("""{"isAvailable":false}""", writes[0].Body!.ToJsonString());
        Assert.Equal("DELETE", writes[1].Method);
        Assert.Equal(0, host.Directory.Count(AccrPath));

        var provider = host.Provider(AccrType);
        var entry = new StateEntry { Id = ResourceIdentifier.Create(AccrType, "c11"), Type = AccrType, ApiVersion = ProviderHost.ApiVersion, Key = new Dictionary<string, string> { ["id"] = "c11" }, GraphId = "c11" };
        host.ClearRequests();
        await provider.DeleteAsync(entry, host.Fake, ProviderHost.Ct);
        Assert.Empty(host.Fake.Writes);
    }

    [Fact]
    public async Task Context_DeleteRefusedByGraph_IsAProviderException()
    {
        var host = new ProviderHost();
        host.Directory.Add(AccrPath, new JsonObject { ["id"] = "c12", ["displayName"] = "In use", ["isAvailable"] = false });
        host.Fake.Failures.Add("DELETE", AccrPath + "/c12", r => new GraphException(HttpStatusCode.BadRequest, "Request_BadRequest", "The context is referenced by a Conditional Access policy.", r.Hint));
        var provider = host.Provider(AccrType);
        var entry = new StateEntry { Id = ResourceIdentifier.Create(AccrType, "c12"), Type = AccrType, ApiVersion = ProviderHost.ApiVersion, Key = new Dictionary<string, string> { ["id"] = "c12" }, GraphId = "c12" };

        var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.DeleteAsync(entry, host.Fake, ProviderHost.Ct));

        Assert.Equal(AuthenticationContextClassReferencesProvider.Referenced, ex.Code);
        Assert.Contains("c12", ex.Message, StringComparison.Ordinal);
    }

    // ---- end to end: authentication strengths ----

    private const string StrengthProps = """{ "displayName": "Phishing-resistant", "description": "FIDO2 only", "allowedCombinations": ["fido2", "windowsHelloForBusiness"] }""";

    [Fact]
    public async Task Strength_IsPosted_AndRerunWithReorderedCasingIsNoChange()
    {
        var host = new ProviderHost();

        var run = await host.CreateAsync(host.Resources(Strength("Phishing-resistant", StrengthProps)));

        Check.Succeeded(run);
        var post = Assert.Single(run.Writes);
        Assert.Equal("POST", post.Method);
        Assert.Equal(StrengthPath, post.Path);
        Assert.Equal("Phishing-resistant", Check.Body(post)["displayName"]!.GetValue<string>());
        Assert.Equal(2, Check.Body(post)["allowedCombinations"]!.AsArray().Count);

        var reordered = await host.CreateAsync(host.Resources(Strength("Phishing-resistant", """{ "displayName": "Phishing-resistant", "description": "FIDO2 only", "allowedCombinations": ["WindowsHelloForBusiness", "FIDO2"] }""")));
        Assert.Equal(ChangeType.NoChange, Assert.Single(reordered.Executed.Operations).ChangeType);
        Check.NoWrites(reordered);
    }

    [Fact]
    public async Task Strength_CombinationChange_UsesTheAction_AndOtherPropertiesPatchSeparately()
    {
        var host = new ProviderHost();
        await host.CreateAsync(host.Resources(Strength("Phishing-resistant", StrengthProps)));
        string id = host.IdOf(StrengthPath, "displayName", "Phishing-resistant");

        var combos = await host.CreateAsync(host.Resources(Strength("Phishing-resistant", """{ "displayName": "Phishing-resistant", "description": "FIDO2 only", "allowedCombinations": ["fido2"] }""")));
        Check.Succeeded(combos);
        var action = Assert.Single(combos.Writes);
        Assert.Equal("POST", action.Method);
        Assert.Equal(StrengthPath + "/" + id + "/updateAllowedCombinations", action.Path);
        Assert.Equal("""{"allowedCombinations":["fido2"]}""", action.Body!.ToJsonString());

        var both = await host.CreateAsync(host.Resources(Strength("Phishing-resistant", """{ "displayName": "Phishing-resistant", "description": "Changed", "allowedCombinations": ["fido2", "temporaryAccessPassOneTime"] }""")));
        Check.Succeeded(both);
        Assert.Equal(2, both.Writes.Count);
        Assert.Equal("PATCH", both.Writes[0].Method);
        Assert.Equal("""{"description":"Changed"}""", both.Writes[0].Body!.ToJsonString());
        Assert.EndsWith("/updateAllowedCombinations", both.Writes[1].Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Strength_BuiltIn_IsReadableButNeverManaged()
    {
        var host = new ProviderHost();
        host.Directory.Add(StrengthPath, new JsonObject { ["displayName"] = "Multifactor authentication", ["policyType"] = "builtIn", ["allowedCombinations"] = new JsonArray("password,microsoftAuthenticatorPush") });

        var whatIf = await host.WhatIfAsync(host.Resources(Strength("Multifactor authentication", """{ "displayName": "Multifactor authentication", "allowedCombinations": ["fido2"] }""")));
        var row = Assert.Single(whatIf.Plan.Changes);
        Assert.True(row.HasError);
        Assert.Contains("built-in authentication strength", row.UnsupportedReason, StringComparison.Ordinal);

        var run = await host.CreateAsync(host.Resources(Strength("Multifactor authentication", """{ "displayName": "Multifactor authentication", "allowedCombinations": ["fido2"] }""")));
        Assert.NotEqual(EArm.Engine.Execution.DeploymentStatus.Succeeded, run.Executed.Status);
        Check.NoWrites(run);

        var policyType = await host.WhatIfAsync(host.Resources(Strength("Mine", """{ "displayName": "Mine", "policyType": "builtIn", "allowedCombinations": ["fido2"] }""")));
        Assert.Contains("custom", Assert.Single(policyType.Plan.Changes).UnsupportedReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Strength_TwoCustomWithTheSameName_AreAmbiguous()
    {
        var host = new ProviderHost();
        host.Directory.Add(StrengthPath, new JsonObject { ["displayName"] = "Dup", ["policyType"] = "custom", ["allowedCombinations"] = new JsonArray("fido2") });
        host.Directory.Add(StrengthPath, new JsonObject { ["displayName"] = "Dup", ["policyType"] = "custom", ["allowedCombinations"] = new JsonArray("fido2") });

        var run = await host.CreateAsync(host.Resources(Strength("Dup", """{ "displayName": "Dup", "allowedCombinations": ["fido2"] }""")));

        var op = Assert.Single(run.Executed.Operations);
        Assert.Equal(ProviderException.AmbiguousMatch, op.ErrorCode);
        Assert.Contains("earm state adopt", op.Error, StringComparison.Ordinal);
        Check.NoWrites(run);
    }

    [Fact]
    public async Task Strength_Delete_ByIdToleratesNotFound_AndReportsReferenced()
    {
        var host = new ProviderHost();
        await host.CreateAsync(host.Resources(Strength("Phishing-resistant", StrengthProps)));
        string id = host.IdOf(StrengthPath, "displayName", "Phishing-resistant");
        var provider = host.Provider(StrengthType);
        var entry = new StateEntry { Id = ResourceIdentifier.Create(StrengthType, "Phishing-resistant"), Type = StrengthType, ApiVersion = ProviderHost.ApiVersion, Key = new Dictionary<string, string> { ["displayName"] = "Phishing-resistant" }, GraphId = id };

        host.Fake.Failures.Add("DELETE", StrengthPath + "/" + id, r => new GraphException(HttpStatusCode.BadRequest, "Request_BadRequest", "referenced by a policy", r.Hint), times: 1);
        var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.DeleteAsync(entry, host.Fake, ProviderHost.Ct));
        Assert.Equal(AuthenticationStrengthsProvider.Referenced, ex.Code);

        host.ClearRequests();
        await provider.DeleteAsync(entry, host.Fake, ProviderHost.Ct);
        Assert.Equal(StrengthPath + "/" + id, Assert.Single(host.Fake.Writes).Path);
        Assert.Equal(0, host.Directory.Count(StrengthPath));
        await provider.DeleteAsync(entry, host.Fake, ProviderHost.Ct);
    }

    [Fact]
    public async Task Strength_AndPolicy_ShareTheStrengthId_AndDeleteInOrder()
    {
        var host = new ProviderHost();
        const string policy = """
            { "type": "Microsoft.Entra/conditionalAccess/policies", "apiVersion": "2026-10-01-preview", "name": "CA-Strength",
              "properties": { "conditions": { "users": { "includeGroups": ["aaaaaaaa-1111-2222-3333-444444444444"] }, "applications": { "includeApplications": ["All"] } },
                "grantControls": { "authenticationStrength": { "id": "[reference('Phishing-resistant').id]" } } },
              "dependsOn": ["[resourceId('Microsoft.Entra/conditionalAccess/authenticationStrengths', 'Phishing-resistant')]"] }
            """;

        var run = await host.CreateAsync(host.Resources(Strength("Phishing-resistant", StrengthProps) + "," + policy));

        Check.Succeeded(run);
        string strengthId = host.IdOf(StrengthPath, "displayName", "Phishing-resistant");
        var policyPost = Assert.Single(run.Writes, w => w.Path == "/identity/conditionalAccess/policies");
        Assert.Contains("\"authenticationStrength\":{\"id\":\"" + strengthId + "\"}", policyPost.Body!.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(RetryHint.ExpectReplicationLag, policyPost.Hint);

        host.ClearRequests();
        var deletion = await host.DeleteAllAsync();
        Assert.True(deletion.Succeeded);
        Assert.StartsWith("/identity/conditionalAccess/policies/", host.Fake.Writes[0].Path, StringComparison.Ordinal);
        Assert.StartsWith(StrengthPath + "/", host.Fake.Writes[1].Path, StringComparison.Ordinal);
    }
}
