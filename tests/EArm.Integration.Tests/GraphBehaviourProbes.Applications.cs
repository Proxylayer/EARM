using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Providers;
using Xunit;

namespace EArm.Integration.Tests;

/// <summary>Probes (d) and (f) of <see cref="GraphBehaviourProbes"/>: password credentials, application read-back and soft delete.</summary>
public sealed partial class GraphBehaviourProbes
{
    // ---- (d) password credentials ----

    [Fact]
    public async Task It_Probe_D_PasswordCredentialsAddRemoveAndSelectShape()
    {
        Fixture.Require();
        var report = new ProbeReport("d");
        var application = await TenantObjects.CreateApplicationAsync(Fixture, "probe-secret", null, Ct);
        string id = application.RequireId();
        string name = Fixture.Name("probe-secret");

        // addPassword response shape.
        var added = await Graph.PostAsync(
            new GraphRequest($"/applications/{id}/addPassword"),
            new JsonObject { ["passwordCredential"] = new JsonObject { ["displayName"] = "probe", ["endDateTime"] = DateTimeOffset.UtcNow.AddDays(30).ToString("O") } },
            Ct);
        var credential = added.Body as JsonObject;
        report.Observe($"addPassword answered {added.Status} with properties: {string.Join(", ", credential?.Select(p => p.Key) ?? [])}.");
        report.Expect(
            credential?.Text("keyId") is not null && !string.IsNullOrEmpty(credential.Text("secretText")) && credential.Text("displayName") == "probe" && credential.Text("endDateTime") is not null,
            "addPassword did not return keyId, secretText, displayName and endDateTime. Adjust PasswordCredentialsRules.SummaryOf and PasswordCredentialsProvider.AddPasswordAsync (src/EArm.Providers/PasswordCredentialsRules.cs, PasswordCredentialsProvider.cs).");
        string keyId = credential?.Text("keyId") ?? Guid.NewGuid().ToString();

        // The read the provider uses: $select=id,passwordCredentials on the alternate key; the secret is never returned again.
        var read = await Graph.GetObjectAsync(new GraphRequest(GraphPath.AlternateKey("/applications", "uniqueName", name)).WithSelect("id", "passwordCredentials"), Ct);
        var listed = (read?["passwordCredentials"] as JsonArray)?.OfType<JsonObject>().ToList();
        report.Expect(
            read?.Text("id") == id && listed is not null && listed.Any(c => c.Text("keyId") == keyId),
            "GET /applications(uniqueName='…')?$select=id,passwordCredentials did not return the id and the new credential. PasswordCredentialsProvider.ReadAsync (src/EArm.Providers/PasswordCredentialsProvider.cs) reads this way.");
        report.Expect(
            listed is null || listed.All(c => string.IsNullOrEmpty(c.Text("secretText"))),
            "A read of passwordCredentials returned secretText. LiveSecrets (src/EArm.Engine/Providers/LiveSecrets.cs) assumes Graph shows the secret only once; review SecretNotAvailableOnRerun handling.");

        // removePassword with an unknown keyId: the text and status IsUnknownKeyId has to recognise.
        try
        {
            await Graph.PostAsync(new GraphRequest($"/applications/{id}/removePassword"), new JsonObject { ["keyId"] = Guid.NewGuid().ToString() }, Ct);
            report.Observe("removePassword with an unknown keyId SUCCEEDED (204): PasswordCredentialsRules.IsUnknownKeyId is only a safety net.");
        }
        catch (GraphException ex)
        {
            report.Observe($"removePassword with an unknown keyId failed with {ProbeReport.Describe(ex)}.");
            report.Expect(
                PasswordCredentialsRules.IsUnknownKeyId(ex),
                $"removePassword for an unknown keyId failed with '{ProbeReport.Describe(ex)}', which PasswordCredentialsRules.IsUnknownKeyId does not treat as 'already gone'. Extend it (src/EArm.Providers/PasswordCredentialsRules.cs) and the unit tests in tests/EArm.Providers.Tests/PasswordCredentialsRulesTests.cs.");
        }

        // removePassword with the real keyId.
        await Graph.PostAsync(new GraphRequest($"/applications/{id}/removePassword"), new JsonObject { ["keyId"] = keyId }, Ct);
        var after = await Graph.GetObjectAsync(new GraphRequest("/applications/" + id).WithSelect("id", "passwordCredentials"), Ct);
        report.Expect(
            after!["passwordCredentials"]!.AsArray().OfType<JsonObject>().All(c => c.Text("keyId") != keyId),
            "removePassword did not remove the credential. Review PasswordCredentialsProvider.RemoveAsync (src/EArm.Providers/PasswordCredentialsProvider.cs).");

        report.Complete();
    }

    // ---- (f) application read-back and soft delete ----

    [Fact]
    public async Task It_Probe_F1_DeletedApplicationFilterOnUniqueName()
    {
        Fixture.Require();
        var report = new ProbeReport("f1");
        var application = await TenantObjects.CreateApplicationAsync(Fixture, "probe-softdel", null, Ct);
        string name = Fixture.Name("probe-softdel");
        string id = application.RequireId();
        await Graph.DeleteAsync(new GraphRequest("/applications/" + id), Ct);

        // Exactly the request ApplicationsProvider.FindSoftDeletedAsync sends first.
        var request = new GraphRequest(ApplicationsProvider.DeletedApplicationsPath)
            .WithFilter($"uniqueName eq '{name}'")
            .WithCount()
            .WithConsistencyLevelEventual()
            .WithSelect("id", "uniqueName", "displayName", "deletedDateTime");
        try
        {
            var hits = await EventuallyAsync(() => Graph.ListAsync(request, Ct), found => found is { Count: > 0 });
            report.Observe($"$filter=uniqueName eq on {ApplicationsProvider.DeletedApplicationsPath} is ACCEPTED and returned {hits?.Count ?? 0} object(s).");
            report.Expect(
                hits is { Count: 1 } && hits[0].RequireId() == id,
                "The filtered soft-delete probe did not find the deleted application. ApplicationsProvider.FindSoftDeletedAsync (src/EArm.Providers/ApplicationsProvider.cs) would miss a soft-deleted holder of the key and the create would fail with a duplicate; make the list scan the primary path.");
            report.Expect(
                hits is { Count: 1 } && string.Equals(hits[0].Text("uniqueName"), name, StringComparison.OrdinalIgnoreCase),
                "A deleted application is returned without 'uniqueName'; FirstDeletedMatchAsync in ApplicationsProvider.cs matches on it client-side and would never find the holder. Match on displayName or request the property with $select.");
        }
        catch (GraphException ex)
        {
            report.Expect(false, $"Graph rejects the filter on uniqueName for {ApplicationsProvider.DeletedApplicationsPath} ({ProbeReport.Describe(ex)}); the list fallback runs on every create. Make the list scan the primary path in ApplicationsProvider.FindSoftDeletedAsync (src/EArm.Providers/ApplicationsProvider.cs) and correct docs/providers.md ('Create' row of the worked example).");
        }

        await Graph.DeleteAsync(new GraphRequest("/directory/deletedItems/" + id), Ct);
        report.Complete();
    }

    [Fact]
    public async Task It_Probe_F2_WebRedirectUriSettingsNullHandlingMatchesTheOverlay()
    {
        Fixture.Require();
        var report = new ProbeReport("f2");
        var application = await TenantObjects.CreateApplicationAsync(Fixture, "probe-redirect", new JsonObject { ["web"] = new JsonObject { ["redirectUris"] = new JsonArray("https://earm-it.example.test/cb") } }, Ct);
        string path = "/applications/" + application.RequireId();

        var read = await Graph.GetObjectAsync(new GraphRequest(path).WithSelect("id", "web"), Ct);
        var web = read.Nested("web");
        report.Observe($"web as read back: {web?.ToJsonString()}. redirectUriSettings is {(web is not null && web.ContainsKey("redirectUriSettings") ? "present" : "absent")} on a fresh application.");

        // The engine sends an explicit null for a live-only sub-property unless the overlay marks it nullable: false. Graph rejects
        // that null for web.redirectUriSettings (Collection(RedirectUriSettings) is Nullable=False), so the overlay must say false.
        bool overlayNullable = OverlayNullable("web.redirectUriSettings");
        bool anyAccepted = false;
        foreach (var body in new[]
        {
            new JsonObject { ["web"] = new JsonObject { ["redirectUriSettings"] = null } },
            new JsonObject { ["web"] = new JsonObject { ["redirectUris"] = new JsonArray("https://earm-it.example.test/cb"), ["redirectUriSettings"] = null } },
        })
        {
            try
            {
                await Graph.PatchAsync(new GraphRequest(path), body, Ct);
                anyAccepted = true;
                report.Observe($"PATCH {body.ToJsonString()} is ACCEPTED.{(overlayNullable ? string.Empty : $" The overlay's \"web.redirectUriSettings\": false is conservative, not required ({OverlayFile}).")}");
            }
            catch (GraphException ex)
            {
                report.Observe($"PATCH {body.ToJsonString()} is rejected ({ProbeReport.Describe(ex)}).");
                report.Expect(
                    !overlayNullable,
                    $"Graph rejects an explicit null for web.redirectUriSettings, but the overlay lets the engine send it: add \"web.redirectUriSettings\": false to x-earm.nullable in {OverlayFile} (see docs/providers.md, 'BuildPatchBody').");
            }
        }

        if (anyAccepted)
        {
            var after = (await Graph.GetObjectAsync(new GraphRequest(path).WithSelect("id", "web"), Ct)).Nested("web");
            report.Expect(
                after?.Strings("redirectUris").Contains("https://earm-it.example.test/cb") == true,
                "A PATCH that nulls web.redirectUriSettings dropped the sibling web.redirectUris: nested objects are replaced, not merged. ProviderBase.BuildPatchBody (src/EArm.Engine/Providers/ProviderBase.cs) must send the whole web object.");
        }

        report.Complete();
    }

    [Fact]
    public async Task It_Probe_F3_RequestedAccessTokenVersionNullVersusSet()
    {
        Fixture.Require();
        var report = new ProbeReport("f3");
        var application = await TenantObjects.CreateApplicationAsync(Fixture, "probe-tokenver", null, Ct);
        string path = "/applications/" + application.RequireId();

        var fresh = (await Graph.GetObjectAsync(new GraphRequest(path).WithSelect("id", "api"), Ct)).Nested("api");
        int? initial = ReadVersion(fresh);
        report.Observe($"requestedAccessTokenVersion on a fresh application: {(initial is null ? "null" : initial.ToString())}.");
        report.Expect(
            initial is null or 1,
            $"A fresh application reports requestedAccessTokenVersion {initial}; ApplicationsRules treats null as 1 (src/EArm.Providers/ApplicationsRules.cs, NormalizeLive) and would report drift for a template that omits api.");

        await Graph.PatchAsync(new GraphRequest(path), new JsonObject { ["api"] = new JsonObject { ["requestedAccessTokenVersion"] = 2 } }, Ct);
        report.Expect(
            ReadVersion((await Graph.GetObjectAsync(new GraphRequest(path).WithSelect("id", "api"), Ct)).Nested("api")) == 2,
            "requestedAccessTokenVersion = 2 was not stored.");

        await Graph.PatchAsync(new GraphRequest(path), new JsonObject { ["api"] = new JsonObject { ["requestedAccessTokenVersion"] = 1 } }, Ct);
        int? reset = ReadVersion((await Graph.GetObjectAsync(new GraphRequest(path).WithSelect("id", "api"), Ct)).Nested("api"));
        report.Observe($"After PATCH requestedAccessTokenVersion = 1 the live value is {(reset is null ? "null" : reset.ToString())}.");
        report.Expect(
            reset is null or 1,
            $"Setting requestedAccessTokenVersion = 1 reads back as {reset}; the 'null compares as 1' rule in ApplicationsRules.NormalizeDesired / NormalizeLive (src/EArm.Providers/ApplicationsRules.cs) does not hold.");
        report.Complete();
    }

    [Fact]
    public async Task It_Probe_F4_AppRoleValueChangeNeedsDisableThenReEnableInOneRequest()
    {
        Fixture.Require();
        var report = new ProbeReport("f4");
        const string RoleId = "6d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a4004";
        var application = await TenantObjects.CreateApplicationAsync(Fixture, "probe-approle", new JsonObject { ["appRoles"] = new JsonArray(Role(RoleId, "Probe.One", true)) }, Ct);
        string path = "/applications/" + application.RequireId();

        // What ApplicationsProvider does: PATCH 1 disables the role, PATCH 2 re-enables it in the same request that changes 'value'.
        await Graph.PatchAsync(new GraphRequest(path), new JsonObject { ["appRoles"] = new JsonArray(Role(RoleId, "Probe.One", false)) }, Ct);
        try
        {
            await Graph.PatchAsync(new GraphRequest(path), new JsonObject { ["appRoles"] = new JsonArray(Role(RoleId, "Probe.Two", true)) }, Ct);
        }
        catch (GraphException ex)
        {
            report.Expect(false, $"The second PATCH (re-enable AND change 'value') is rejected ({ProbeReport.Describe(ex)}). ApplicationsRules / ApplicationsProvider.UpdateAsync (src/EArm.Providers/ApplicationsRules.cs, ApplicationsProvider.cs) must use three PATCHes: disable, change the value while disabled, then enable.");
        }

        var role = ((await Graph.GetObjectAsync(new GraphRequest(path).WithSelect("id", "appRoles"), Ct))?["appRoles"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(r => r.Text("id") == RoleId);
        report.Expect(
            role?.Text("value") == "Probe.Two" && (bool?)role["isEnabled"] == true,
            $"After the two PATCHes the role reads back as {role?.ToJsonString() ?? "missing"}, expected value 'Probe.Two' and isEnabled true.");

        // For the record: is the disable step needed at all?
        try
        {
            await Graph.PatchAsync(new GraphRequest(path), new JsonObject { ["appRoles"] = new JsonArray(Role(RoleId, "Probe.Three", true)) }, Ct);
            report.Observe("Changing 'value' of an ENABLED role in one PATCH is ACCEPTED: the disable step in ApplicationsProvider.UpdateAsync is unnecessary (harmless).");
        }
        catch (GraphException ex)
        {
            report.Observe($"Changing 'value' of an enabled role in one PATCH is REJECTED ({ProbeReport.Describe(ex)}): the disable step is required.");
        }

        report.Complete();
    }
}
