using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core.Validation;
using EArm.Graph;
using Xunit;

namespace EArm.Integration.Tests;

/// <summary>
/// Verification probes for the open authentication methods questions of phase 1 (<c>EARM_IT_ALLOW_AUTH_METHODS=1</c>). AM1 only reads. AM2 sends PATCHes
/// that write the live values back (no effective change) and AM3 resets Temporary Access Pass only when it is disabled in the tenant; both run under the
/// restore guard (<see cref="AuthMethodRestoreGuard"/>: snapshot first, restore and verify in a <c>finally</c>, again at teardown). Each probe records what
/// Graph answers and asserts only the assumption the code relies on, naming the file to change.
/// </summary>
[Collection(RealTenantCollection.Name)]
[Trait("Category", "Verify")]
public sealed class AuthMethodProbes : IntegrationTestBase
{
    public AuthMethodProbes(IntegrationFixture fixture)
        : base(fixture)
    {
    }

    [Fact]
    public async Task It_Probe_AM1_UrlSegmentsODataTypesAndNestedAnnotations()
    {
        Fixture.Require(Needs.AuthMethods);
        var report = new ProbeReport("am1");

        try
        {
            var policy = await Graph.GetObjectAsync(new GraphRequest("/policies/authenticationMethodsPolicy").WithExpand("authenticationMethodConfigurations"), Ct);
            var ids = policy?["authenticationMethodConfigurations"]?.AsArray().Select(c => c.Text("id")).ToList() ?? [];
            report.Observe("the policy lists the configurations: " + string.Join(", ", ids));
        }
        catch (GraphException ex)
        {
            report.Observe("GET /policies/authenticationMethodsPolicy?$expand=authenticationMethodConfigurations: " + ProbeReport.Describe(ex));
        }

        foreach (var method in AuthenticationMethodConfigurationRules.Methods)
        {
            JsonObject? live = null;
            try
            {
                live = await Graph.GetObjectAsync(new GraphRequest(AuthMethodRestoreGuard.EntityPath(method)), Ct);
            }
            catch (GraphException ex)
            {
                report.Observe($"{method.Name}: GET …/{method.GraphId} failed: {ProbeReport.Describe(ex)}");
            }

            report.Expect(
                live is not null && string.Equals(live.Text("id"), method.GraphId, StringComparison.OrdinalIgnoreCase) && string.Equals(live.Text("@odata.type"), method.ODataType, StringComparison.OrdinalIgnoreCase),
                $"{method.Name}: GET …/authenticationMethodConfigurations/{method.GraphId} (the PascalCase segment the provider uses) answered id '{live.Text("id")}' and @odata.type '{live.Text("@odata.type")}', expected '{method.GraphId}' and '{method.ODataType}'. Fix the method table (AuthenticationMethodConfigurationRules.Methods) in src/EArm.Core/Validation/AuthenticationMethodConfigurationRules.cs.");
            if (live is null)
            {
                continue;
            }

            report.Observe($"{method.Name}: id='{live.Text("id")}' state='{live.Text("state")}' properties: {string.Join(", ", live.Select(p => p.Key))}");
            var nested = new List<string>();
            CollectODataTypes(live, string.Empty, nested, topLevel: true);
            report.Observe($"{method.Name}: nested @odata.type at {(nested.Count == 0 ? "(none)" : string.Join("; ", nested))}");

            try
            {
                var camel = await Graph.GetObjectAsync(new GraphRequest(AuthMethodRestoreGuard.CollectionPath + "/" + GraphPath.Segment(method.Name)), Ct);
                report.Observe($"{method.Name}: the camelCase segment …/{method.Name} {(camel is null ? "answers 404" : "works too")}");
            }
            catch (GraphException ex)
            {
                report.Observe($"{method.Name}: the camelCase segment …/{method.Name} is refused: {ProbeReport.Describe(ex)}");
            }

            if (method.Name == "fido2")
            {
                report.Observe($"fido2: keyRestrictions={live["keyRestrictions"]?.ToJsonString() ?? "(absent)"} isAttestationEnforced={live["isAttestationEnforced"]?.ToJsonString() ?? "(absent)"} passkeyProfiles={live["passkeyProfiles"]?.ToJsonString() ?? "(absent)"} defaultPasskeyProfile={live["defaultPasskeyProfile"]?.ToJsonString() ?? "(absent)"}");
            }
        }

        report.Complete();
    }

    [Fact]
    public async Task It_Probe_AM2_PatchWithoutODataTypeAndResendingLiveTargets()
    {
        Fixture.Require(Needs.AuthMethods);
        var report = new ProbeReport("am2");
        var method = AuthMethodRestoreGuard.MethodOf("temporaryAccessPass");
        string path = AuthMethodRestoreGuard.EntityPath(method);
        var log = new List<string>();
        var snapshot = await Fixture.AuthMethods.SnapshotAsync(method.Name, Ct);
        try
        {
            // The live value written back: no effective change, only the request shape is probed.
            var length = snapshot["defaultLength"]?.DeepClone();
            try
            {
                await Graph.PatchAsync(new GraphRequest(path), new JsonObject { ["defaultLength"] = length }, Ct);
                report.Observe("PATCH WITHOUT @odata.type was ACCEPTED: the provider's derived @odata.type is not required (harmless).");
            }
            catch (GraphException ex)
            {
                report.Observe("PATCH without @odata.type: " + ProbeReport.Describe(ex));
            }

            try
            {
                await Graph.PatchAsync(new GraphRequest(path), new JsonObject { ["@odata.type"] = method.ODataType, ["defaultLength"] = length?.DeepClone() }, Ct);
                report.Observe("PATCH with @odata.type: accepted");
            }
            catch (GraphException ex)
            {
                report.Expect(false, $"PATCH with the derived @odata.type ({method.ODataType}) is refused: {ProbeReport.Describe(ex)}. Fix UpdateAsync in src/EArm.Providers/AuthenticationMethodConfigurationsProvider.cs.");
            }

            // The provider fills undeclared sub-properties of a declared member from the live object (AuthenticationMethodConfigurationsShape), so the live
            // includeTargets go back as Graph returned them, nested @odata.type included.
            var targets = AuthMethodRestoreGuard.StripAnnotations(snapshot["includeTargets"]);
            report.Observe("live includeTargets: " + (targets?.ToJsonString() ?? "(absent)"));
            if (targets is JsonArray)
            {
                try
                {
                    await Graph.PatchAsync(new GraphRequest(path), new JsonObject { ["@odata.type"] = method.ODataType, ["includeTargets"] = targets }, Ct);
                    report.Observe("resending the live includeTargets (server-filled members and nested @odata.type included): accepted");
                }
                catch (GraphException ex)
                {
                    report.Expect(false, $"resending the live includeTargets is refused: {ProbeReport.Describe(ex)}. The provider resends undeclared live sub-properties; strip the server-filled ones (or nested @odata.type) in src/EArm.Providers/AuthenticationMethodConfigurationsShape.cs (FillUndeclared).");
                }
            }
        }
        finally
        {
            bool restored = await Fixture.AuthMethods.RestoreAsync(method.Name, log, CancellationToken.None);
            log.ForEach(report.Observe);
            report.Expect(restored, "the Temporary Access Pass configuration could not be restored to its snapshot (the fixture retries at teardown); see the observations.");
        }

        report.Complete();
    }

    [Fact]
    public async Task It_Probe_AM3_DeleteResetsADisabledTemporaryAccessPass()
    {
        Fixture.Require(Needs.AuthMethods);
        var report = new ProbeReport("am3");
        var method = AuthMethodRestoreGuard.MethodOf("temporaryAccessPass");
        string path = AuthMethodRestoreGuard.EntityPath(method);
        var current = await Graph.GetObjectAsync(new GraphRequest(path), Ct);
        Assert.SkipUnless(
            string.Equals(current.Text("state"), "disabled", StringComparison.OrdinalIgnoreCase),
            $"Temporary Access Pass is '{current.Text("state")}' in this tenant; the reset (DELETE) probe runs only while it is disabled, so it can never interrupt a configuration in use.");

        var log = new List<string>();
        var snapshot = await Fixture.AuthMethods.SnapshotAsync(method.Name, Ct);
        try
        {
            try
            {
                await Graph.DeleteAsync(new GraphRequest(path), Ct);
                report.Observe("DELETE …/TemporaryAccessPass: accepted");
            }
            catch (GraphException ex)
            {
                report.Expect(false, $"DELETE …/TemporaryAccessPass (documented as a reset to the default) is refused: {ProbeReport.Describe(ex)}. Set CanReset false for temporaryAccessPass in src/EArm.Core/Validation/AuthenticationMethodConfigurationRules.cs (and revisit AuthenticationMethodConfigurationsProvider.DeleteAsync).");
            }

            var after = await EventuallyAsync(() => Graph.GetObjectAsync(new GraphRequest(path), Ct), found => found is not null);
            report.Expect(after is not null, "the configuration cannot be read after the DELETE; the provider assumes a reset never removes it (ReadAsync 404 = missing). See AuthenticationMethodConfigurationsProvider.DeleteAsync.");
            if (after is not null)
            {
                var changed = AuthMethodRestoreGuard.Differences(snapshot, after);
                report.Observe(changed.Count == 0
                    ? "the reset changed nothing visible (the configuration already had the default values)"
                    : "the reset changed: " + string.Join("; ", changed.Select(p => $"{p}: {AuthMethodRestoreGuard.StripAnnotations(snapshot[p])?.ToJsonString()} -> {AuthMethodRestoreGuard.StripAnnotations(after[p])?.ToJsonString()}")));
            }
        }
        finally
        {
            bool restored = await Fixture.AuthMethods.RestoreAsync(method.Name, log, CancellationToken.None);
            log.ForEach(report.Observe);
            report.Expect(restored, "the Temporary Access Pass configuration could not be restored to its snapshot after the reset (the fixture retries at teardown); see the observations.");
        }

        report.Complete();
    }

    /// <summary>The paths below the top level that carry an <c>@odata.type</c> (and its value).</summary>
    private static void CollectODataTypes(JsonNode? node, string path, List<string> found, bool topLevel)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (key == "@odata.type" && !topLevel)
                    {
                        found.Add($"{(path.Length == 0 ? "." : path)}={value}");
                    }
                    else if (value is JsonObject or JsonArray)
                    {
                        CollectODataTypes(value, path + "." + key, found, topLevel: false);
                    }
                }

                break;
            case JsonArray array:
                for (int i = 0; i < array.Count; i++)
                {
                    CollectODataTypes(array[i], $"{path}[{i}]", found, topLevel: false);
                }

                break;
        }
    }
}
