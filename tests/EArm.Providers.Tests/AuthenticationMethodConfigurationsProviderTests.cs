using System;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Graph;
using EArm.Providers.Tests.Infrastructure;
using EArm.TestSupport;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// <c>Microsoft.Entra/authenticationMethodConfigurations</c>: tenant singletons named by the method id. Never created, updated with a merge PATCH that carries the
/// derived <c>@odata.type</c>, AAGUIDs and keyed target / profile arrays compared as sets, undeclared sub-properties (the certificate binding rules) kept from the
/// live object, the FIDO2 legacy-property warning, delete as a reset for fido2 / temporaryAccessPass / x509Certificate and kept (NotDeletable) for the rest.
/// </summary>
public sealed class AuthenticationMethodConfigurationsProviderTests
{
    private const string Type = AuthenticationMethodConfigurationsProvider.ResourceType;
    private const string Path = FakeAuthenticationMethodsPolicy.CollectionPath;
    private const string YubiKeyA = "cb69481e-8ff7-4039-93ec-0a2729a154a8";
    private const string YubiKeyB = "ee882879-721c-4913-9775-3dfcce97072a";
    private const string YubiKeyC = "fa2b99dc-9e39-4257-8f92-4a30d23c4118";

    private static string Method(string name, string properties)
    {
        return Templates.Resource(Type, name, properties);
    }

    private static JsonObject Live(ProviderHost host, string graphId)
    {
        return host.Directory.Find(Path, "id", graphId) ?? throw new InvalidOperationException("no " + graphId);
    }

    private static void Seed(ProviderHost host, string graphId, Action<JsonObject> configure)
    {
        var configuration = FakeAuthenticationMethodsPolicy.Default(graphId)!;
        configure(configuration);
        host.Directory.Add(Path, configuration);
    }

    private static FakeGraphRequest SinglePatch(Run run)
    {
        var write = Assert.Single(run.Writes);
        Assert.Equal("PATCH", write.Method);
        return write;
    }

    // ---- no change / modify ----

    [Fact]
    public async Task AMatchingDeclaration_IsNoChange_AndReadsTheGraphIdPath()
    {
        var host = new ProviderHost();

        var run = await host.WhatIfAsync(host.Resources(Method("sms", "{ \"state\": \"disabled\" }")));

        var change = run.Change("/sms");
        Assert.Equal(ChangeType.NoChange, change.Type);
        Assert.Equal("Sms", change.GraphId);
        Assert.Contains(run.Requests, r => r.Method == "GET" && r.Path.EndsWith("/authenticationMethodConfigurations/Sms", StringComparison.Ordinal));
        Check.NoWrites(run);
    }

    [Fact]
    public async Task TheName_IsCaseInsensitive_AndAGraphIdNameWorksToo()
    {
        var host = new ProviderHost();

        var run = await host.WhatIfAsync(host.Resources(Method("FIDO2", "{ \"state\": \"disabled\" }") + ", " + Method("TemporaryAccessPass", "{ \"isUsableOnce\": false }")));

        Assert.Equal(ChangeType.NoChange, run.Change("/FIDO2").Type);
        Assert.Equal("Fido2", run.Change("/FIDO2").GraphId);
        Assert.Equal(ChangeType.NoChange, run.Change("/TemporaryAccessPass").Type);
    }

    [Fact]
    public async Task AChangedState_IsAModify_WhosePatchCarriesTheDerivedODataType_AndOnlyTheChangedProperty()
    {
        var host = new ProviderHost();
        var document = host.Resources(Method("sms", "{ \"state\": \"enabled\" }"));

        var whatIf = await host.WhatIfAsync(document);
        var change = whatIf.Change("/sms");
        Assert.Equal(ChangeType.Modify, change.Type);
        Assert.Equal(ChangeMode.Patch, change.Mode);
        Assert.Equal("properties.state", Assert.Single(change.Deltas, d => d.HasEffect).Path.ToString());

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        var patch = SinglePatch(run);
        Assert.EndsWith("/authenticationMethodConfigurations/Sms", patch.Path, StringComparison.Ordinal);
        var body = Check.Body(patch);
        Assert.Equal(["@odata.type", "state"], body.Select(p => p.Key).ToArray());
        Assert.Equal("#microsoft.graph.smsAuthenticationMethodConfiguration", body["@odata.type"]!.GetValue<string>());
        Assert.Equal("enabled", Live(host, "Sms")["state"]!.GetValue<string>());

        var rerun = await host.WhatIfAsync(document);
        Assert.Equal(ChangeType.NoChange, rerun.Change("/sms").Type);
    }

    [Fact]
    public async Task ATemporaryAccessPassLifetimeChange_PatchesExactlyTheODataTypeAndTheChangedLifetime()
    {
        // The real-tenant scenario: minimumLifetimeInMinutes 60 -> 15 was accepted (204) but never read back changed while the method was disabled.
        // The body EARM sends must be the documented merge PATCH (Learn: temporaryaccesspassauthenticationmethodconfiguration-update): the derived
        // @odata.type first, then only the changed property at the top level, as an integer.
        var host = new ProviderHost();
        var document = host.Resources(Method("temporaryAccessPass", "{ \"state\": \"disabled\", \"minimumLifetimeInMinutes\": 15 }"));

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        var patch = SinglePatch(run);
        Assert.EndsWith("/policies/authenticationMethodsPolicy/authenticationMethodConfigurations/TemporaryAccessPass", patch.Path, StringComparison.Ordinal);
        Assert.Equal(
            "{\"@odata.type\":\"#microsoft.graph.temporaryAccessPassAuthenticationMethodConfiguration\",\"minimumLifetimeInMinutes\":15}",
            Check.Body(patch).ToJsonString());
        Assert.Equal(15, Live(host, "TemporaryAccessPass")["minimumLifetimeInMinutes"]!.GetValue<int>());
    }

    [Fact]
    public async Task ADeclaredIdOrODataType_ThatNamesTheMethod_IsAccepted_AndNeverDiffed()
    {
        var host = new ProviderHost();

        var run = await host.WhatIfAsync(host.Resources(Method("email", "{ \"id\": \"email\", \"@odata.type\": \"#microsoft.graph.emailAuthenticationMethodConfiguration\", \"state\": \"disabled\" }")));

        Assert.Equal(ChangeType.NoChange, run.Change("/email").Type);
    }

    // ---- sets ----

    [Fact]
    public async Task AaGuids_CompareAsANormalisedSet_AndAnAddedOneIsSentLowerCase()
    {
        var host = new ProviderHost();
        Seed(host, "Fido2", c => c["keyRestrictions"] = new JsonObject { ["isEnforced"] = true, ["enforcementType"] = "allow", ["aaGuids"] = new JsonArray(YubiKeyA, YubiKeyB) });
        string same = "{ \"keyRestrictions\": { \"isEnforced\": true, \"enforcementType\": \"allow\", \"aaGuids\": [\"" + YubiKeyB.ToUpperInvariant() + "\", \"" + YubiKeyA.ToUpperInvariant() + "\"] } }";

        var unchanged = await host.WhatIfAsync(host.Resources(Method("fido2", same)));

        Assert.Equal(ChangeType.NoChange, unchanged.Change("/fido2").Type);

        string more = "{ \"keyRestrictions\": { \"isEnforced\": true, \"enforcementType\": \"allow\", \"aaGuids\": [\"" + YubiKeyA + "\", \"" + YubiKeyB + "\", \"" + YubiKeyC.ToUpperInvariant() + "\"] } }";
        var run = await host.CreateAsync(host.Resources(Method("fido2", more)));

        Check.Succeeded(run);
        var body = Check.Body(SinglePatch(run));
        Assert.Equal("#microsoft.graph.fido2AuthenticationMethodConfiguration", body["@odata.type"]!.GetValue<string>());
        Assert.Equal([YubiKeyA, YubiKeyB, YubiKeyC], body["keyRestrictions"]!["aaGuids"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
    }

    [Fact]
    public async Task Targets_CompareAsASetById_WithServerFilledMemberPropertiesKept()
    {
        var host = new ProviderHost();
        const string group = "9d3a2f10-0000-4000-8000-000000000001";
        Seed(host, "TemporaryAccessPass", c => c["includeTargets"] = new JsonArray(
            new JsonObject { ["targetType"] = "group", ["id"] = "all_users", ["isRegistrationRequired"] = false },
            new JsonObject { ["targetType"] = "group", ["id"] = group, ["isRegistrationRequired"] = true }));

        var reordered = await host.WhatIfAsync(host.Resources(Method("temporaryAccessPass", "{ \"includeTargets\": [ { \"id\": \"" + group.ToUpperInvariant() + "\", \"targetType\": \"group\" }, { \"id\": \"all_users\", \"targetType\": \"group\" } ] }")));

        Assert.Equal(ChangeType.NoChange, reordered.Change("/temporaryAccessPass").Type);

        var run = await host.CreateAsync(host.Resources(Method("temporaryAccessPass", "{ \"includeTargets\": [ { \"id\": \"" + group + "\", \"targetType\": \"group\" } ] }")));

        Check.Succeeded(run);
        var targets = Check.Body(SinglePatch(run))["includeTargets"]!.AsArray();
        var only = Assert.IsType<JsonObject>(Assert.Single(targets));
        Assert.Equal(group, only["id"]!.GetValue<string>());
        Assert.True(only["isRegistrationRequired"]!.GetValue<bool>(), "the live member's undeclared isRegistrationRequired is kept, not reset");
        Assert.Single(Live(host, "TemporaryAccessPass")["includeTargets"]!.AsArray());
    }

    [Fact]
    public async Task PasskeyProfiles_CompareById_AndTheProfileKeyRestrictionsAreNormalised()
    {
        var host = new ProviderHost();
        string profile = "{ \"passkeyProfiles\": [ { \"id\": \"" + FakeAuthenticationMethodsPolicy.DefaultPasskeyProfileId + "\", \"keyRestrictions\": { \"isEnforced\": true, \"enforcementType\": \"allow\", \"aaGuids\": [\"" + YubiKeyA.ToUpperInvariant() + "\"] } } ] }";

        var run = await host.CreateAsync(host.Resources(Method("fido2", profile)));

        Check.Succeeded(run);
        var sent = Check.Body(SinglePatch(run))["passkeyProfiles"]!.AsArray().Single()!.AsObject();
        Assert.Equal("Default passkey profile", sent["name"]!.GetValue<string>());
        Assert.Equal(YubiKeyA, sent["keyRestrictions"]!["aaGuids"]![0]!.GetValue<string>());
        Assert.DoesNotContain(run.Executed.Diagnostics, d => d.Code == EngineDiagnosticCodes.DeprecatedAuthenticationMethodProperty);

        var rerun = await host.WhatIfAsync(host.Resources(Method("fido2", profile)));
        Assert.Equal(ChangeType.NoChange, rerun.Change("/fido2").Type);
    }

    // ---- x509 rules ----

    [Fact]
    public async Task X509_UndeclaredRules_AreResentFromTheLiveObject_AndAnExplicitEmptyListClearsThem()
    {
        var host = new ProviderHost();
        var rule = new JsonObject { ["x509CertificateRuleType"] = "policyOID", ["identifier"] = "1.2.3.4", ["x509CertificateAuthenticationMode"] = "x509CertificateMultiFactor" };
        Seed(host, "X509Certificate", c => c["authenticationModeConfiguration"] = new JsonObject { ["x509CertificateAuthenticationDefaultMode"] = "x509CertificateSingleFactor", ["rules"] = new JsonArray(rule.DeepClone()) });

        var run = await host.CreateAsync(host.Resources(Method("x509Certificate", "{ \"authenticationModeConfiguration\": { \"x509CertificateAuthenticationDefaultMode\": \"x509CertificateMultiFactor\" } }")));

        Check.Succeeded(run);
        var sent = Check.Body(SinglePatch(run))["authenticationModeConfiguration"]!.AsObject();
        Assert.Equal("x509CertificateMultiFactor", sent["x509CertificateAuthenticationDefaultMode"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(new JsonArray(rule.DeepClone()), sent["rules"]), "the live rules travel with the PATCH: " + sent.ToJsonString());
        Assert.Single(Live(host, "X509Certificate")["authenticationModeConfiguration"]!["rules"]!.AsArray());

        var cleared = await host.CreateAsync(host.Resources(Method("x509Certificate", "{ \"authenticationModeConfiguration\": { \"x509CertificateAuthenticationDefaultMode\": \"x509CertificateMultiFactor\", \"rules\": [] } }")));

        Check.Succeeded(cleared);
        Assert.Empty(Check.Body(SinglePatch(cleared))["authenticationModeConfiguration"]!["rules"]!.AsArray());
        Assert.Empty(Live(host, "X509Certificate")["authenticationModeConfiguration"]!["rules"]!.AsArray());
    }

    // ---- deprecation ----

    [Fact]
    public async Task TheLegacyFido2Properties_RaiseTheDeprecationWarning_InWhatIfAndCreate()
    {
        var host = new ProviderHost();
        var document = host.Resources(Method("fido2", "{ \"isAttestationEnforced\": true, \"keyRestrictions\": { \"isEnforced\": false, \"enforcementType\": \"block\", \"aaGuids\": [] } }"));

        var whatIf = await host.WhatIfAsync(document);
        var run = await host.CreateAsync(document);

        var planned = Assert.Single(whatIf.Plan.Diagnostics, d => d.Code == EngineDiagnosticCodes.DeprecatedAuthenticationMethodProperty);
        Assert.Contains("isAttestationEnforced and keyRestrictions are deprecated", planned.Message, StringComparison.Ordinal);
        Assert.Contains("passkeyProfiles", planned.Message, StringComparison.Ordinal);
        Check.Succeeded(run);
        Check.Diagnostic(run, EngineDiagnosticCodes.DeprecatedAuthenticationMethodProperty);
    }

    // ---- never create ----

    [Fact]
    public async Task AMissingConfiguration_IsAnErrorRow_AndTheCreateFailsWithoutWriting()
    {
        var host = new ProviderHost();
        host.Fake.Failures.Add("GET", Path + "/Voice", request => new GraphException(HttpStatusCode.NotFound, "Request_ResourceNotFound", "gone", request.Hint), times: 20);
        var document = host.Resources(Method("voice", "{ \"state\": \"disabled\" }"));

        var whatIf = await host.WhatIfAsync(document);
        var run = await host.CreateAsync(document);

        var change = whatIf.Change("/voice");
        Assert.True(change.HasError);
        Assert.Contains("does not exist in the tenant", change.UnsupportedReason, StringComparison.Ordinal);
        Assert.Contains("never creates", change.UnsupportedReason, StringComparison.Ordinal);
        Assert.False(run.Succeeded);
        Check.NoWrites(run);
    }

    [Fact]
    public async Task CreateAsync_IsAlwaysAProviderException()
    {
        var host = new ProviderHost();
        var provider = host.Provider<AuthenticationMethodConfigurationsProvider>(Type);
        var instance = await InstanceFactory.WithPropertiesAsync(host, Method("sms", "{ \"state\": \"disabled\" }"), "{ \"state\": \"enabled\" }");

        var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.CreateAsync(instance, host.Fake, ProviderHost.Ct));

        Assert.Equal(EngineDiagnosticCodes.ResourceApplyFailed, ex.Code);
        Assert.Contains("never creates", ex.Message, StringComparison.Ordinal);
        Assert.Empty(host.Fake.Writes);
    }

    // ---- invalid declarations (evaluated values the offline validator would stop first) ----

    [Theory]
    [InlineData("hardwareOath", "{ \"state\": \"enabled\" }", "is not a supported authentication method configuration")]
    [InlineData("sms", "{ \"@odata.type\": \"#microsoft.graph.voiceAuthenticationMethodConfiguration\" }", "must be '#microsoft.graph.smsAuthenticationMethodConfiguration'")]
    [InlineData("sms", "{ \"id\": \"Voice\" }", "conflicts with the resource name 'sms'")]
    [InlineData("sms", "{ \"state\": \"on\" }", "properties.state must be one of enabled, disabled")]
    [InlineData("fido2", "{ \"keyRestrictions\": { \"isEnforced\": true, \"enforcementType\": \"allow\", \"aaGuids\": [] } }", "enforces an allow-list")]
    [InlineData("fido2", "{ \"keyRestrictions\": { \"aaGuids\": [\"yubikey\"] } }", "is not a valid AAGUID")]
    [InlineData("temporaryAccessPass", "{ \"minimumLifetimeInMinutes\": 120, \"defaultLifetimeInMinutes\": 60 }", "must not be greater than properties.defaultLifetimeInMinutes")]
    [InlineData("temporaryAccessPass", "{ \"defaultLength\": 49 }", "must be between 8 and 48")]
    [InlineData("x509Certificate", "{ \"authenticationModeConfiguration\": { \"x509CertificateAuthenticationDefaultMode\": \"strong\" } }", "must be one of x509CertificateSingleFactor, x509CertificateMultiFactor")]
    [InlineData("sms", "{ \"includeTargets\": [ { \"targetType\": \"group\" } ] }", "must declare 'id'")]
    [InlineData("sms", "{ \"excludeTargets\": [ { \"id\": \"g1\" }, { \"id\": \"G1\" } ] }", "is listed more than once")]
    public async Task InvalidDeclarations_AreUnsupportedRows_AndNeverReachGraph(string name, string properties, string expected)
    {
        var host = new ProviderHost();
        var provider = host.Provider<AuthenticationMethodConfigurationsProvider>(Type);
        var instance = await InstanceFactory.WithPropertiesAsync(host, Method("sms", "{ \"state\": \"disabled\" }"), properties, name);
        var existing = new ExistingResource("Sms", FakeAuthenticationMethodsPolicy.Default("Sms")!, FakeAuthenticationMethodsPolicy.Default("Sms")!);

        var change = provider.Plan(instance, existing, PlanOptions.Default);

        Assert.True(change.HasError);
        Assert.Contains(expected, change.UnsupportedReason, StringComparison.Ordinal);
        Assert.Empty(host.Fake.Writes);
    }

    // ---- delete ----

    [Fact]
    public async Task Delete_OfAResettableMethod_ResetsItToTheDefault_WithTheRiskListed()
    {
        var host = new ProviderHost();
        Seed(host, "Fido2", c => c["state"] = "enabled");
        var document = host.Resources(Method("fido2", "{ \"state\": \"enabled\" }"));
        var plan = await host.Planner().PlanAsync(document, DeploymentMode.Incremental, PlanMode.WhatIf, ProviderHost.Ct);
        var listed = Assert.Single(TemplateDeleter.FindDeclaredObjects(plan));

        var result = await host.DeleteDeclaredAsync(document);

        Assert.Null(listed.Refusal);
        Assert.Contains("resets the fido2 authentication method policy to the Microsoft default", listed.Risk, StringComparison.Ordinal);
        Assert.True(result.Succeeded);
        Assert.Equal(DeletionStatus.Deleted, Assert.Single(result.Outcomes).Status);
        Assert.Contains(host.Fake.Writes, w => w.Method == "DELETE" && w.Path.EndsWith("/authenticationMethodConfigurations/Fido2", StringComparison.Ordinal));
        Assert.Equal("disabled", Live(host, "Fido2")["state"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("sms")]
    [InlineData("voice")]
    [InlineData("email")]
    [InlineData("softwareOath")]
    [InlineData("microsoftAuthenticator")]
    public async Task Delete_OfAMethodWithoutAReset_IsKeptWithTheReason_AndIsNotAFailure(string name)
    {
        var host = new ProviderHost();
        var document = host.Resources(Method(name, "{ }"));

        var result = await host.DeleteDeclaredAsync(document);

        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal(DeletionStatus.Refused, outcome.Status);
        Assert.Equal(EngineDiagnosticCodes.NotDeletable, outcome.ErrorCode);
        Assert.Contains("can neither delete nor reset", outcome.Error, StringComparison.Ordinal);
        Assert.True(result.Succeeded, "a kept singleton is not a failure");
        Assert.Equal(1, result.RefusedCount);
        Assert.DoesNotContain(host.Fake.Writes, w => w.Method == "DELETE");
    }

    [Fact]
    public async Task DeleteAsync_OfAMethodWithoutAReset_IsNotDeletable_AndAVanishedResettableOneIsNoError()
    {
        var host = new ProviderHost();
        var provider = host.Provider<AuthenticationMethodConfigurationsProvider>(Type);
        var smsKey = new ResourceKey { Type = Type, Values = new System.Collections.Generic.Dictionary<string, string> { ["id"] = "sms" } };
        var tapKey = new ResourceKey { Type = Type, Values = new System.Collections.Generic.Dictionary<string, string> { ["id"] = "temporaryAccessPass" } };
        var sms = new ExistingResource("Sms", Live(host, "Sms"), Live(host, "Sms"));
        var tap = new ExistingResource("TemporaryAccessPass", Live(host, "TemporaryAccessPass"), Live(host, "TemporaryAccessPass"));
        host.Fake.Failures.Add("DELETE", Path + "/TemporaryAccessPass", request => new GraphException(HttpStatusCode.NotFound, "Request_ResourceNotFound", "gone", request.Hint));

        var refused = await Assert.ThrowsAsync<ProviderException>(() => provider.DeleteAsync(smsKey, sms, host.Fake, ProviderHost.Ct));
        await provider.DeleteAsync(tapKey, tap, host.Fake, ProviderHost.Ct);

        Assert.Equal(EngineDiagnosticCodes.NotDeletable, refused.Code);
        Assert.Null(provider.DescribeDeleteRisk(smsKey, sms));
        Assert.NotNull(provider.DescribeDeleteRefusal(smsKey, sms));
        Assert.Null(provider.DescribeDeleteRefusal(tapKey, tap));
    }

    // ---- complete mode ----

    [Fact]
    public async Task CompleteMode_ListsNoOrphans_AndDoesNotCallTheTypeIncomplete()
    {
        var host = new ProviderHost();

        var run = await host.WhatIfAsync(host.Resources(Method("sms", "{ \"state\": \"disabled\" }")), DeploymentMode.Complete);

        Assert.DoesNotContain(run.Plan.Changes, c => c.IsOrphan);
        Assert.DoesNotContain(run.Plan.Diagnostics, d => d.Code == EngineDiagnosticCodes.OrphanReportIncomplete && d.Message.Contains("authenticationMethodConfigurations", StringComparison.Ordinal));
    }

    // ---- the fake's Graph rules ----

    [Fact]
    public async Task TheFake_RefusesAPatchWithoutTheODataType_AndAnyCreate()
    {
        var host = new ProviderHost();

        var untyped = await Assert.ThrowsAsync<GraphException>(() => host.Fake.PatchAsync(new GraphRequest(Path + "/Sms"), new JsonObject { ["state"] = "enabled" }, ProviderHost.Ct));
        var created = await Assert.ThrowsAsync<GraphException>(() => host.Fake.PostAsync(new GraphRequest(Path), new JsonObject { ["id"] = "Other" }, ProviderHost.Ct));
        var missing = await Assert.ThrowsAsync<GraphException>(() => host.Fake.PatchAsync(new GraphRequest(Path + "/HardwareOath"), new JsonObject { ["@odata.type"] = "#microsoft.graph.hardwareOathAuthenticationMethodConfiguration" }, ProviderHost.Ct));

        Assert.Equal(HttpStatusCode.BadRequest, untyped.Status);
        Assert.Contains("@odata.type", untyped.GraphMessage, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, created.Status);
        Assert.True(missing.IsNotFound);
        Assert.Equal("disabled", Live(host, "Sms")["state"]!.GetValue<string>());
    }
}
