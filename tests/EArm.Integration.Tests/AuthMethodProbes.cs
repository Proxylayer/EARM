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
/// that write the live values back (no effective change) and AM3 resets Temporary Access Pass only when it is disabled in the tenant; AM4 changes its lifetimes and watches whether they stick; all run under the
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
                $"{method.Name}: GET …/authenticationMethodConfigurations/{method.GraphId} (the PascalCase segment the provider uses) answered id '{live.Text("id")}' and @odata.type '{live.Text("@odata.type")}', expected '{method.GraphId}' and '{method.ODataType}'. Fix the method table AuthenticationMethodConfigurationRules.Methods.");
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
                report.Expect(false, $"PATCH with the derived @odata.type ({method.ODataType}) is refused: {ProbeReport.Describe(ex)}. Fix AuthenticationMethodConfigurationsProvider.UpdateAsync.");
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
                    report.Expect(false, $"resending the live includeTargets is refused: {ProbeReport.Describe(ex)}. The provider resends undeclared live sub-properties; strip the server-filled ones (or nested @odata.type) in AuthenticationMethodConfigurationsShape.FillUndeclared.");
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
                report.Expect(false, $"DELETE …/TemporaryAccessPass (documented as a reset to the default) is refused: {ProbeReport.Describe(ex)}. Set CanReset false for temporaryAccessPass in AuthenticationMethodConfigurationRules.Methods (and revisit AuthenticationMethodConfigurationsProvider.DeleteAsync).");
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

    /// <summary>
    /// The real tenant accepted (204) a Temporary Access Pass PATCH of <c>minimumLifetimeInMinutes</c> 60 -> 15 (and earlier <c>defaultLength</c> 8 -> 9)
    /// while the method was disabled, but never served the new value. AM4 sends the provider's exact body shape (<c>@odata.type</c> + the changed lifetime)
    /// twice, first <c>minimumLifetimeInMinutes</c> alone, then the three lifetimes together, and re-reads each for 60 s, recording whether the values stick
    /// and the method's state. Observation only (the scenario chooses its toggles from what sticks); restore-guarded.
    /// </summary>
    [Fact]
    public async Task It_Probe_AM4_TemporaryAccessPassLifetimePatchSticks()
    {
        Fixture.Require(Needs.AuthMethods);
        var report = new ProbeReport("am4");
        var method = AuthMethodRestoreGuard.MethodOf("temporaryAccessPass");
        string path = AuthMethodRestoreGuard.EntityPath(method);
        var log = new List<string>();
        var snapshot = await Fixture.AuthMethods.SnapshotAsync(method.Name, Ct);
        string state = snapshot.Text("state") ?? "?";
        int min = (int?)snapshot["minimumLifetimeInMinutes"] ?? 60;
        int def = (int?)snapshot["defaultLifetimeInMinutes"] ?? 60;
        int max = (int?)snapshot["maximumLifetimeInMinutes"] ?? 480;
        report.Observe($"temporaryAccessPass state '{state}', lifetimes min {min} / default {def} / max {max}");
        try
        {
            // 1: minimumLifetimeInMinutes alone, to a value inside the documented bounds (10 .. default).
            int newMin = min == 15 ? 10 : 15;
            if (newMin <= def)
            {
                await PatchAndWatchAsync(report, path, method.ODataType, state, new JsonObject { ["minimumLifetimeInMinutes"] = newMin });
            }
            else
            {
                report.Observe($"minimumLifetimeInMinutes alone: skipped ({newMin} would exceed defaultLifetimeInMinutes {def})");
            }

            // 2: the three lifetimes together (what the scenario sent), to the other documented-valid set.
            var lifetimes = new[] { (Min: 60, Default: 60, Max: 480), (Min: 15, Default: 30, Max: 120) }.First(c => c != (min, def, max));
            await PatchAndWatchAsync(
                report,
                path,
                method.ODataType,
                state,
                new JsonObject { ["minimumLifetimeInMinutes"] = lifetimes.Min, ["defaultLifetimeInMinutes"] = lifetimes.Default, ["maximumLifetimeInMinutes"] = lifetimes.Max });
        }
        finally
        {
            bool restored = await Fixture.AuthMethods.RestoreAsync(method.Name, log, CancellationToken.None);
            log.ForEach(report.Observe);
            report.Expect(restored, "the Temporary Access Pass configuration could not be restored to its snapshot (the fixture retries at teardown); see the observations.");
        }

        report.Complete();
    }

    /// <summary>PATCHes <paramref name="changes"/> (with the derived <c>@odata.type</c> first) and re-reads every 5 s for 60 s, recording which values stuck.</summary>
    private async Task PatchAndWatchAsync(ProbeReport report, string path, string odataType, string state, JsonObject changes)
    {
        string what = string.Join(", ", changes.Select(p => $"{p.Key}={p.Value?.ToJsonString()}"));
        var body = new JsonObject { ["@odata.type"] = odataType };
        foreach (var (name, value) in changes)
        {
            body[name] = value?.DeepClone();
        }

        try
        {
            await Graph.PatchAsync(new GraphRequest(path), body, Ct);
        }
        catch (GraphException ex)
        {
            report.Observe($"PATCH {what} (state '{state}'): refused: {ProbeReport.Describe(ex)}");
            return;
        }

        var started = DateTimeOffset.UtcNow;
        JsonObject? live = null;
        while (true)
        {
            live = await Graph.GetObjectAsync(new GraphRequest(path), Ct);
            bool stuck = live is not null && changes.All(p => AuthMethodRestoreGuard.Canonical(live[p.Key]) == AuthMethodRestoreGuard.Canonical(p.Value));
            double seconds = (DateTimeOffset.UtcNow - started).TotalSeconds;
            if (stuck)
            {
                report.Observe($"PATCH {what} (state '{state}'): accepted, and read back after {seconds:F0} s: the change STICKS");
                return;
            }

            if (seconds >= 60)
            {
                string read = live is null ? "(unreadable)" : string.Join(", ", changes.Select(p => $"{p.Key}={live[p.Key]?.ToJsonString()}"));
                report.Observe($"PATCH {what} (state '{state}'): accepted (204), but after {seconds:F0} s Graph still serves {read}: the change does NOT stick");
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(5), Ct);
        }
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
