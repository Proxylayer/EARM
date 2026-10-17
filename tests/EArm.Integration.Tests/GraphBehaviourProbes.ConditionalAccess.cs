using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Core;
using EArm.Providers;
using Xunit;

namespace EArm.Integration.Tests;

/// <summary>Probes (c) and (e) of <see cref="GraphBehaviourProbes"/>: Conditional Access PATCH of empty arrays and nulls, authentication contexts and strengths.</summary>
public sealed partial class GraphBehaviourProbes
{
    // ---- (c) Conditional Access PATCH: [] versus null ----

    [Fact]
    public async Task It_Probe_C_ConditionalAccessPatchClearsCollectionsWithEmptyArrayAndObjectsWithNull()
    {
        Fixture.Require(Needs.ConditionalAccess);
        var report = new ProbeReport("c");
        var included = await TenantObjects.CreateGroupAsync(Fixture, "probe-ca-in", Ct);
        var excluded = await TenantObjects.CreateGroupAsync(Fixture, "probe-ca-out", Ct);
        var policy = await Graph.CreateObjectAsync(
            PoliciesPath,
            new JsonObject
            {
                ["displayName"] = Fixture.Name("probe-ca"),
                ["state"] = "enabledForReportingButNotEnforced",
                ["conditions"] = Conditions(included.RequireId(), excluded.RequireId()),
                ["grantControls"] = new JsonObject { ["operator"] = "OR", ["builtInControls"] = new JsonArray("mfa") },
                ["sessionControls"] = new JsonObject
                {
                    ["signInFrequency"] = new JsonObject { ["isEnabled"] = true, ["type"] = "hours", ["value"] = 8, ["authenticationType"] = "primaryAndSecondaryAuthentication", ["frequencyInterval"] = "timeBased" },
                },
            },
            Ct);
        string path = $"{PoliciesPath}/{policy.RequireId()}";
        Fixture.Track(path);

        // A PATCH right after the POST can answer 404 until the new policy replicates: wait until a GET sees it, and let
        // the requests themselves retry replication lag too.
        var replicated = await EventuallyAsync(() => Graph.GetObjectAsync(new GraphRequest(path), Ct), live => live is not null, 90);
        Assert.True(replicated is not null, $"The CA policy {path} was not readable 90 s after its POST.");

        // 1. [] clears a collection inside conditions (the provider coerces its explicit nulls to []).
        await Graph.PatchAsync(new GraphRequest(path).WithHint(RetryHint.ExpectReplicationLag), new JsonObject { ["conditions"] = Conditions(included.RequireId(), null) }, Ct);
        var afterEmpty = await Graph.GetObjectAsync(new GraphRequest(path), Ct);
        report.Expect(
            afterEmpty.Nested("conditions.users").IsEmptyOrAbsent("excludeGroups"),
            "PATCH conditions.users.excludeGroups = [] did not clear the collection. ConditionalAccessPoliciesRules.CoerceNullCollections / FinishPatchBody must send something else.");

        // 2. What does an explicit null do for a collection? Recorded only: the provider never sends it.
        await Graph.PatchAsync(new GraphRequest(path), new JsonObject { ["conditions"] = Conditions(included.RequireId(), excluded.RequireId()) }, Ct);
        try
        {
            var withNull = Conditions(included.RequireId(), null);
            withNull["users"]!["excludeGroups"] = null;
            await Graph.PatchAsync(new GraphRequest(path), new JsonObject { ["conditions"] = withNull }, Ct);
            var afterNull = await Graph.GetObjectAsync(new GraphRequest(path), Ct);
            report.Observe($"PATCH conditions.users.excludeGroups = null is ACCEPTED; the collection afterwards: {(afterNull.Nested("conditions.users").IsEmptyOrAbsent("excludeGroups") ? "cleared" : "unchanged")}.");
        }
        catch (GraphException ex)
        {
            report.Observe($"PATCH conditions.users.excludeGroups = null is REJECTED: {ProbeReport.Describe(ex)}. The provider's null -> [] coercion is required.");
        }

        // 3. null removes a control object.
        await Graph.PatchAsync(new GraphRequest(path), new JsonObject { ["sessionControls"] = null }, Ct);
        var afterObjectNull = await Graph.GetObjectAsync(new GraphRequest(path), Ct);
        report.Expect(
            afterObjectNull.IsNullOrAbsent("sessionControls"),
            "PATCH sessionControls = null did not remove the session controls. ConditionalAccessPoliciesRules.MarkRemovedControls assumes null removes a control object.");

        report.Complete();
    }

    // ---- (e) authentication contexts and strengths ----

    [Fact]
    public async Task It_Probe_E1_AuthenticationContextUpsertResponseAndDelete()
    {
        Fixture.Require(Needs.ConditionalAccess);
        var report = new ProbeReport("e1");
        string contextId = await Fixture.AllocateContextIdAsync(Ct);
        string path = $"{ContextsPath}/{contextId}";
        var body = new JsonObject { ["displayName"] = Fixture.Name("probe-ctx"), ["description"] = "probe", ["isAvailable"] = true };

        var upsert = await Graph.PatchAsync(new GraphRequest(path), body, Ct);
        Fixture.Track(path);
        report.Observe($"PATCH {path} (upsert of a new context) answered {upsert.Status}; body: {(upsert.Body is null ? "none" : "present with id " + upsert.Body.Text("id"))}.");
        report.Expect(
            upsert.Status is System.Net.HttpStatusCode.Created or System.Net.HttpStatusCode.OK or System.Net.HttpStatusCode.NoContent && (upsert.Body is null || upsert.Body.Text("id") == contextId),
            "The context upsert answered something other than 201 + object, 200 or 204. Adjust AuthenticationContextClassReferencesProvider.CreateAsync: it expects an object with the id or no body.");

        var read = await Graph.GetObjectAsync(new GraphRequest(path), Ct);
        report.Expect(
            read?.Text("displayName") == body.Text("displayName") && (bool?)read?["isAvailable"] == true,
            "The context was not readable with its displayName and isAvailable = true after the upsert.");

        // Delete: still available -> refused (the provider disables first).
        try
        {
            await Graph.DeleteAsync(new GraphRequest(path), Ct);
            report.Observe("DELETE of an AVAILABLE context succeeded: the 'isAvailable: false' step in AuthenticationContextClassReferencesProvider.DeleteAsync is not needed (kept as a harmless safety).");
        }
        catch (GraphException ex)
        {
            report.Observe($"DELETE of an available context is refused: {ProbeReport.Describe(ex)}; the provider disables the context first.");
            await Graph.PatchAsync(new GraphRequest(path), new JsonObject { ["isAvailable"] = false }, Ct);
            await Graph.DeleteAsync(new GraphRequest(path), Ct);
        }

        report.Expect(
            await Graph.GetObjectAsync(new GraphRequest(path), Ct) is null,
            "The context still exists after isAvailable=false + DELETE. See AuthenticationContextClassReferencesProvider.DeleteAsync.");
        report.Complete();
    }

    [Fact]
    public async Task It_Probe_E2_AuthenticationStrengthCombinationCasingAndFilter()
    {
        Fixture.Require(Needs.ConditionalAccess);
        var report = new ProbeReport("e2");
        string name = Fixture.Name("probe-str");

        JsonObject? strength = null;
        try
        {
            // Mixed casing on purpose: the provider compares case-insensitively and sends the template's own spelling.
            strength = await Graph.CreateObjectAsync(StrengthsPath, new JsonObject { ["displayName"] = name, ["description"] = "probe", ["allowedCombinations"] = new JsonArray("Fido2", "windowsHelloForBusiness") }, Ct);
            Fixture.Track($"{StrengthsPath}/{strength.RequireId()}");
        }
        catch (GraphException ex)
        {
            report.Expect(false, $"allowedCombinations with mixed casing ('Fido2') is rejected ({ProbeReport.Describe(ex)}). AuthenticationStrengthsProvider should send a normalised spelling: change the AuthenticationStrengthsRules.NormalizeCombination usage in AuthenticationStrengthsProvider.CreateAsync / UpdateAsync to send the canonical camelCase value and update docs/providers.md.");
        }

        if (strength is not null)
        {
            var live = await Graph.GetObjectAsync(new GraphRequest($"{StrengthsPath}/{strength.RequireId()}"), Ct);
            report.Observe($"Stored allowedCombinations: {string.Join(", ", live.Strings("allowedCombinations"))}.");
            var desired = new JsonObject { ["allowedCombinations"] = new JsonArray("fido2", "windowsHelloForBusiness") };
            report.Expect(
                live is not null && AuthenticationStrengthsRules.CombinationsEqual(desired, live),
                "The stored allowedCombinations differ from the template's by more than casing/order, so the provider would report drift on every run. Fix AuthenticationStrengthsRules.NormalizeCombination / CombinationsEqual.");

            // The replace action with another casing.
            await Graph.PostAsync(
                new GraphRequest($"{StrengthsPath}/{strength.RequireId()}/updateAllowedCombinations"),
                new JsonObject { ["allowedCombinations"] = new JsonArray("FIDO2") },
                Ct);
            var updated = await Graph.GetObjectAsync(new GraphRequest($"{StrengthsPath}/{strength.RequireId()}"), Ct);
            report.Expect(
                updated is not null && AuthenticationStrengthsRules.CombinationsEqual(new JsonObject { ["allowedCombinations"] = new JsonArray("fido2") }, updated),
                "updateAllowedCombinations with 'FIDO2' did not leave a set equal to ['fido2'] (case-insensitively). Review AuthenticationStrengthsProvider.UpdateAsync.");

            // The collection filter the provider avoids (it lists and matches client-side).
            try
            {
                var filtered = await Graph.ListAsync(new GraphRequest(StrengthsPath).WithFilter($"displayName eq '{name}'"), Ct);
                report.Observe($"$filter=displayName eq on {StrengthsPath} is ACCEPTED and returned {filtered.Count} object(s); AuthenticationStrengthsProvider could use it instead of listing every strength.");
            }
            catch (GraphException ex)
            {
                report.Observe($"$filter=displayName eq on {StrengthsPath} is REJECTED ({ProbeReport.Describe(ex)}); keep the client-side match in AuthenticationStrengthsProvider.");
            }
        }

        report.Complete();
    }
}
