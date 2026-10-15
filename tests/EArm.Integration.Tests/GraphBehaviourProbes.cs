using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Core;
using EArm.Providers;
using Xunit;

namespace EArm.Integration.Tests;

/// <summary>
/// Verification probes: each test exercises one real Graph
/// behaviour the code assumes, records what it observed (<see cref="TestContext.SendDiagnosticMessage"/>) and asserts the
/// assumption. A red test names the file to change in its message; a green test means the assumption is confirmed and the
/// matching TODO can be closed. The letters group the probes: (a) launcher and paging, (b) alternate keys, filters, scope
/// ids and restore permission, (c) Conditional Access nulls, (d) password credentials, (e) contexts and strengths, (f)
/// application read-back and soft delete, (g) nested-object null handling. Run only these with
/// <c>-Target EArm.Integration.Tests -Filter "Category=Verify"</c>.
/// </summary>
[Collection(RealTenantCollection.Name)]
[Trait("Category", "Verify")]
public sealed class GraphBehaviourProbes : IntegrationTestBase
{
    private const string GraphAppId = WellKnownIds.MicrosoftGraphAppId;

    public GraphBehaviourProbes(IntegrationFixture fixture)
        : base(fixture)
    {
    }

    // ---- (a) HTTP transport: query encoding, token audience, paging ----

    [Fact]
    public async Task It_Probe_A_QueryEncodingTokenAudienceAndNextLink()
    {
        Fixture.Require();
        var report = new ProbeReport("a");
        string stem = Fixture.Name("pg");
        string[] names = [$"{stem} 100% & alpha", $"{stem} 100% & beta", $"{stem} 100% & gamma"];
        var ids = new List<string>();
        for (int i = 0; i < names.Length; i++)
        {
            ids.Add((await TenantObjects.CreateGroupAsync(Fixture, "pg" + i, Ct, names[i])).RequireId());
        }

        // '%' and '&' inside a $filter value must reach Graph unchanged (RFC 3986 percent-encoding by HttpGraphClient).
        var exact = await EventuallyAsync(
            () => Graph.ListAsync(new GraphRequest("/groups").WithFilter($"displayName eq '{names[0]}'").WithSelect("id", "displayName"), Ct),
            found => found is { Count: > 0 });
        report.Expect(
            exact is { Count: 1 } && exact[0].RequireId() == ids[0],
            $"'%' or '&' did not survive the query encoding: a $filter on '{names[0]}' returned {exact?.Count ?? 0} object(s). See GraphUrl.BuildQuery in src/EArm.Graph/GraphUrl.cs.");

        // Spaces in a filter value ('startswith(displayName,'<stem> 100% & ')') are accepted by Graph once they are percent-encoded.
        var all = await EventuallyAsync(
            () => Graph.ListAsync(new GraphRequest("/groups").WithFilter($"startswith(displayName,'{stem} 100% & ')").WithSelect("id", "displayName"), Ct),
            found => found is { Count: >= 3 });
        report.Expect(
            all is { Count: 3 },
            $"A $filter containing spaces, '%' and '&' returned {all?.Count ?? 0} of 3 groups: the space encoding is wrong or Graph rejects it. See GraphUrl.BuildQuery in src/EArm.Graph/GraphUrl.cs.");

        // The token is requested for the Graph scope: GET /organization must name the signed-in tenant.
        var organization = await Graph.GetObjectAsync(new GraphRequest("/organization").WithSelect("id"), Ct);
        string? organizationId = (organization?["value"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault()?.Text("id");
        report.Expect(
            string.Equals(organizationId, Fixture.Session?.TenantId, StringComparison.OrdinalIgnoreCase),
            $"GET /organization returned '{organizationId}' instead of tenant {Fixture.Session?.TenantId}: the token was issued for another tenant or audience. See GraphCloud.GraphScope in src/EArm.Graph/GraphCloud.cs.");

        // @odata.nextLink: three pages of one item each; the next link is followed as returned (validated to be on the Graph origin).
        var paged = await Graph.ListAsync(new GraphRequest("/groups").WithFilter($"startswith(displayName,'{stem} 100% & ')").WithSelect("id", "displayName").WithTop(1), Ct);
        report.Expect(
            paged.Count == 3 && paged.Select(p => p.RequireId()).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 3,
            $"Following @odata.nextLink returned {paged.Count} item(s) ({paged.Select(p => p.Text("displayName")).Distinct().Count()} distinct) instead of 3. See the paging loop in src/EArm.Graph/HttpGraphClient.cs (GetAllPagesAsync).");

        report.Complete();
    }

    // ---- (b) alternate key + action, filter and, delegated scope ids, restore permission ----

    [Fact]
    public async Task It_Probe_B1_AlternateKeyUrlWithAddPasswordAction()
    {
        Fixture.Require();
        var report = new ProbeReport("b1");
        var application = await TenantObjects.CreateApplicationAsync(Fixture, "probe-altkey", null, Ct);
        string name = Fixture.Name("probe-altkey");

        // What the provider does: actions always address the object id.
        string idUrl = $"/applications/{application.RequireId()}/addPassword";
        var byId = await Graph.PostAsync(new GraphRequest(idUrl), new JsonObject { ["passwordCredential"] = new JsonObject { ["displayName"] = "probe-by-id" } }, Ct);
        report.Expect(
            byId.Body is JsonObject credential && credential.Text("keyId") is not null && credential.Text("secretText") is not null,
            $"POST {idUrl} did not return keyId and secretText: the object-id addPassword path of src/EArm.Providers/PasswordCredentialsProvider.cs (AddPasswordAsync) no longer matches Graph.");

        // The open question: does the alternate-key form accept the action? Recorded only; the provider does not depend on it.
        string altUrl = GraphPath.AlternateKey("/applications", "uniqueName", name) + "/addPassword";
        try
        {
            var byKey = await Graph.PostAsync(new GraphRequest(altUrl), new JsonObject { ["passwordCredential"] = new JsonObject { ["displayName"] = "probe-by-key" } }, Ct);
            report.Observe($"POST {altUrl} WORKS (status {byKey.Status}, keys: {string.Join(',', (byKey.Body as JsonObject)?.Select(p => p.Key) ?? [])}). The object-id lookup before addPassword in PasswordCredentialsProvider.cs could be dropped; docs/providers.md ('Action URLs always address the application by object id') can say the alternate-key form is also accepted.");
        }
        catch (GraphException ex)
        {
            report.Observe($"POST {altUrl} is REJECTED: {ProbeReport.Describe(ex)}. The provider's object-id addressing is required; keep it and record that in docs/providers.md.");
        }

        report.Complete();
    }

    [Fact]
    public async Task It_Probe_B2_RoleAssignmentFilterWithAnd()
    {
        Fixture.Require();
        var report = new ProbeReport("b2");
        var sample = (await Graph.ListAsync(new GraphRequest(RoleAssignmentsPath).WithTop(1), Ct)).FirstOrDefault();
        Assert.SkipWhen(sample is null, "The tenant has no directory role assignment to build a filter from.");

        string principal = sample!.Text("principalId")!;
        string definition = sample.Text("roleDefinitionId")!;
        string scope = sample.Text("directoryScopeId") ?? "/";

        // The provider's shape (RoleAssignmentsLookup): one condition, matched client-side.
        var single = await Graph.ListAsync(new GraphRequest(RoleAssignmentsPath).WithFilter($"principalId eq '{principal}'"), Ct);
        report.Expect(
            single.Any(a => a.Text("id") == sample.Text("id")),
            $"GET {RoleAssignmentsPath}?$filter=principalId eq '…' did not return the assignment: RoleAssignmentsLookup (src/EArm.Providers/RoleAssignmentsLookup.cs) relies on it.");

        // The open question: may conditions be combined? Recorded only.
        foreach (string filter in new[]
        {
            $"principalId eq '{principal}' and roleDefinitionId eq '{definition}'",
            $"principalId eq '{principal}' and roleDefinitionId eq '{definition}' and directoryScopeId eq '{scope}'",
        })
        {
            try
            {
                var combined = await Graph.ListAsync(new GraphRequest(RoleAssignmentsPath).WithFilter(filter), Ct);
                report.Observe($"$filter={filter} is ACCEPTED and returned {combined.Count} assignment(s) (expected at least 1: {combined.Any(a => a.Text("id") == sample.Text("id"))}). RoleAssignmentsLookup could narrow its query; keeping the client-side match is also correct.");
            }
            catch (GraphException ex)
            {
                report.Observe($"$filter={filter} is REJECTED: {ProbeReport.Describe(ex)}. Keep the single-condition filter with client-side matching in RoleAssignmentsLookup.cs.");
            }
        }

        report.Complete();
    }

    [Fact]
    public async Task It_Probe_B3_GraphDelegatedScopeAndAppRoleIdsMatchTheTable()
    {
        Fixture.Require();
        var report = new ProbeReport("b3");
        var graphSp = await Graph.GetObjectAsync(
            new GraphRequest(GraphPath.AlternateKey("/servicePrincipals", "appId", GraphAppId)).WithSelect("id", "oauth2PermissionScopes", "appRoles"),
            Ct);
        Assert.NotNull(graphSp);

        var liveScopes = ToValueIdMap(graphSp["oauth2PermissionScopes"]);
        var liveRoles = ToValueIdMap(graphSp["appRoles"]);

        var confirmed = new List<string>();
        foreach (var scope in WellKnownIds.GraphScopes)
        {
            if (!liveScopes.TryGetValue(scope.Value, out string? liveId))
            {
                report.Expect(false, $"The delegated scope '{scope.Value}' does not exist on Microsoft Graph. Remove or rename it in WellKnownIds.GraphScopes (src/EArm.Core/WellKnownIds.cs).");
            }
            else if (!string.Equals(liveId, scope.Id, StringComparison.OrdinalIgnoreCase))
            {
                report.Expect(false, $"The delegated scope '{scope.Value}' has id {liveId} on Microsoft Graph, not {scope.Id}. Correct it in WellKnownIds.GraphScopes (src/EArm.Core/WellKnownIds.cs).");
            }
            else if (!scope.Verified)
            {
                confirmed.Add(scope.Value);
            }
        }

        foreach (var role in WellKnownIds.GraphAppRoles)
        {
            if (!liveRoles.TryGetValue(role.Value, out string? liveId) || !string.Equals(liveId, role.Id, StringComparison.OrdinalIgnoreCase))
            {
                report.Expect(false, $"The application permission '{role.Value}' is {(liveId is null ? "missing on" : "id " + liveId + " on")} Microsoft Graph, the table says {role.Id}. Correct WellKnownIds.GraphAppRoles (src/EArm.Core/WellKnownIds.cs).");
            }
        }

        report.Observe($"{confirmed.Count} of {WellKnownIds.GraphScopes.Count(s => !s.Verified)} unverified delegated scope ids confirmed; set Verified (drop ', Verified: false') in src/EArm.Core/WellKnownIds.cs for: {string.Join(", ", confirmed)}.");
        report.Complete();
    }

    [Fact]
    public async Task It_Probe_B4_UserSoftDeleteRestoreNeedsOnlyTheDocumentedPermission()
    {
        Fixture.Require(Needs.Domain);
        var report = new ProbeReport("b4");
        string name = Fixture.Name("probe-restore");
        var user = await Graph.CreateObjectAsync(
            "/users",
            new JsonObject
            {
                ["accountEnabled"] = true,
                ["displayName"] = name,
                ["mailNickname"] = name,
                ["userPrincipalName"] = Fixture.Upn("probe-restore"),
                ["passwordProfile"] = new JsonObject { ["password"] = NewPassword(), ["forceChangePasswordNextSignIn"] = true },
            },
            Ct);
        string id = user.RequireId();
        Fixture.Track("/users/" + id);

        await Graph.DeleteAsync(new GraphRequest("/users/" + id), Ct);
        var deleted = await EventuallyAsync(
            () => Graph.GetObjectAsync(new GraphRequest("/directory/deletedItems/" + id), Ct),
            found => found is not null);
        Assert.NotNull(deleted);

        report.Observe($"Signed in as {Fixture.Session?.PrincipalName} ({Fixture.Session?.PrincipalType}).");
        try
        {
            var restored = await Graph.PostAsync(new GraphRequest($"/directory/deletedItems/{id}/restore"), null, Ct);
            report.Expect(
                restored.Body is JsonObject restoredUser && restoredUser.Text("id") == id,
                $"POST /directory/deletedItems/{id}/restore returned no user object (status {restored.Status}); see UsersProvider.RestoreOrFailAsync in src/EArm.Providers/UsersProvider.cs.");
        }
        catch (GraphException ex) when (ex.IsForbidden)
        {
            report.Expect(false, $"Restoring a deleted user is forbidden ({ProbeReport.Describe(ex)}). Add 'User.DeleteRestore.All' to the restore row of docs/permissions.md and to the users overlay permissions (schemas/2026-10-01/types/Microsoft.Entra/users.json, x-earm.permissions) and README.md of this project.");
        }

        report.Complete();
    }

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

        // 1. [] clears a collection inside conditions (the provider coerces its explicit nulls to []).
        await Graph.PatchAsync(new GraphRequest(path), new JsonObject { ["conditions"] = Conditions(included.RequireId(), null) }, Ct);
        var afterEmpty = await Graph.GetObjectAsync(new GraphRequest(path), Ct);
        report.Expect(
            afterEmpty.Nested("conditions.users").IsEmptyOrAbsent("excludeGroups"),
            "PATCH conditions.users.excludeGroups = [] did not clear the collection. ConditionalAccessPoliciesRules.CoerceNullCollections / FinishPatchBody (src/EArm.Providers/ConditionalAccessPoliciesRules.cs) must send something else.");

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
            "PATCH sessionControls = null did not remove the session controls. ConditionalAccessPoliciesRules.MarkRemovedControls (src/EArm.Providers/ConditionalAccessPoliciesRules.cs) assumes null removes a control object.");

        report.Complete();
    }

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
            "The context upsert answered something other than 201 + object, 200 or 204. Adjust AuthenticationContextClassReferencesProvider.CreateAsync (src/EArm.Providers/AuthenticationContextClassReferencesProvider.cs): it expects an object with the id or no body.");

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
            "The context still exists after isAvailable=false + DELETE. See AuthenticationContextClassReferencesProvider.DeleteAsync (src/EArm.Providers/AuthenticationContextClassReferencesProvider.cs).");
        report.Complete();
    }

    [Fact]
    public async Task It_Probe_E2_AuthenticationStrengthCombinationCasingAndFilter()
    {
        Fixture.Require(Needs.ConditionalAccess);
        var report = new ProbeReport("e2");
        string name = Fixture.Name("probe-strength");

        JsonObject? strength = null;
        try
        {
            // Mixed casing on purpose: the provider compares case-insensitively and sends the template's own spelling.
            strength = await Graph.CreateObjectAsync(StrengthsPath, new JsonObject { ["displayName"] = name, ["description"] = "probe", ["allowedCombinations"] = new JsonArray("Fido2", "windowsHelloForBusiness") }, Ct);
            Fixture.Track($"{StrengthsPath}/{strength.RequireId()}");
        }
        catch (GraphException ex)
        {
            report.Expect(false, $"allowedCombinations with mixed casing ('Fido2') is rejected ({ProbeReport.Describe(ex)}). AuthenticationStrengthsProvider should send a normalised spelling: change AuthenticationStrengthsRules.NormalizeCombination usage in src/EArm.Providers/AuthenticationStrengthsProvider.cs (CreateAsync / UpdateAsync) to send the canonical camelCase value and update docs/providers.md.");
        }

        if (strength is not null)
        {
            var live = await Graph.GetObjectAsync(new GraphRequest($"{StrengthsPath}/{strength.RequireId()}"), Ct);
            report.Observe($"Stored allowedCombinations: {string.Join(", ", live.Strings("allowedCombinations"))}.");
            var desired = new JsonObject { ["allowedCombinations"] = new JsonArray("fido2", "windowsHelloForBusiness") };
            report.Expect(
                live is not null && AuthenticationStrengthsRules.CombinationsEqual(desired, live),
                "The stored allowedCombinations differ from the template's by more than casing/order, so the provider would report drift on every run. Fix AuthenticationStrengthsRules.NormalizeCombination / CombinationsEqual (src/EArm.Providers/AuthenticationStrengthsRules.cs).");

            // The replace action with another casing.
            await Graph.PostAsync(
                new GraphRequest($"{StrengthsPath}/{strength.RequireId()}/updateAllowedCombinations"),
                new JsonObject { ["allowedCombinations"] = new JsonArray("FIDO2") },
                Ct);
            var updated = await Graph.GetObjectAsync(new GraphRequest($"{StrengthsPath}/{strength.RequireId()}"), Ct);
            report.Expect(
                updated is not null && AuthenticationStrengthsRules.CombinationsEqual(new JsonObject { ["allowedCombinations"] = new JsonArray("fido2") }, updated),
                "updateAllowedCombinations with 'FIDO2' did not leave a set equal to ['fido2'] (case-insensitively). Review AuthenticationStrengthsProvider.UpdateAsync (src/EArm.Providers/AuthenticationStrengthsProvider.cs).");

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
    public async Task It_Probe_F2_WebRedirectUriSettingsIsLiveOnlyAndNullable()
    {
        Fixture.Require();
        var report = new ProbeReport("f2");
        var application = await TenantObjects.CreateApplicationAsync(Fixture, "probe-redirect", new JsonObject { ["web"] = new JsonObject { ["redirectUris"] = new JsonArray("https://earm-it.example.test/cb") } }, Ct);
        string path = "/applications/" + application.RequireId();

        var read = await Graph.GetObjectAsync(new GraphRequest(path).WithSelect("id", "web"), Ct);
        var web = read.Nested("web");
        report.Observe($"web as read back: {web?.ToJsonString()}. redirectUriSettings is {(web is not null && web.ContainsKey("redirectUriSettings") ? "present" : "absent")} on a fresh application.");

        // The engine sends an explicit null for a live-only sub-property unless the overlay marks it nullable: false.
        foreach (var body in new[]
        {
            new JsonObject { ["web"] = new JsonObject { ["redirectUriSettings"] = null } },
            new JsonObject { ["web"] = new JsonObject { ["redirectUris"] = new JsonArray("https://earm-it.example.test/cb"), ["redirectUriSettings"] = null } },
        })
        {
            try
            {
                await Graph.PatchAsync(new GraphRequest(path), body, Ct);
            }
            catch (GraphException ex)
            {
                report.Expect(false, $"PATCH {body.ToJsonString()} is rejected ({ProbeReport.Describe(ex)}). Add \"web.redirectUriSettings\": false to x-earm.nullable in schemas/2026-10-01/types/Microsoft.Entra/applications.json so the engine never sends the null (see docs/providers.md, 'BuildPatchBody').");
            }
        }

        var after = (await Graph.GetObjectAsync(new GraphRequest(path).WithSelect("id", "web"), Ct)).Nested("web");
        report.Expect(
            after?.Strings("redirectUris").Contains("https://earm-it.example.test/cb") == true,
            "A PATCH that nulls web.redirectUriSettings dropped the sibling web.redirectUris: nested objects are replaced, not merged. ProviderBase.BuildPatchBody (src/EArm.Engine/Providers/ProviderBase.cs) must send the whole web object.");
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

    // ---- (g) nested-object PATCH null handling per sub-property ----

    [Fact]
    public async Task It_Probe_G_NestedObjectNullHandlingPerSubProperty()
    {
        Fixture.Require();
        var report = new ProbeReport("g");
        var application = await TenantObjects.CreateApplicationAsync(
            Fixture,
            "probe-nested",
            new JsonObject
            {
                ["web"] = new JsonObject
                {
                    ["homePageUrl"] = "https://earm-it.example.test/home",
                    ["logoutUrl"] = "https://earm-it.example.test/logout",
                    ["redirectUris"] = new JsonArray("https://earm-it.example.test/cb"),
                    ["implicitGrantSettings"] = new JsonObject { ["enableAccessTokenIssuance"] = true, ["enableIdTokenIssuance"] = true },
                },
                ["api"] = new JsonObject { ["requestedAccessTokenVersion"] = 2 },
                ["info"] = new JsonObject
                {
                    ["marketingUrl"] = "https://earm-it.example.test/m",
                    ["privacyStatementUrl"] = "https://earm-it.example.test/p",
                    ["supportUrl"] = "https://earm-it.example.test/s",
                    ["termsOfServiceUrl"] = "https://earm-it.example.test/t",
                },
                ["spa"] = new JsonObject { ["redirectUris"] = new JsonArray("https://earm-it.example.test/spa") },
                ["publicClient"] = new JsonObject { ["redirectUris"] = new JsonArray("https://earm-it.example.test/pc") },
            },
            Ct);
        string path = "/applications/" + application.RequireId();

        // (parent, property, nullable according to the overlay: true = an explicit null clears it, false = Graph keeps it or refuses).
        (string Parent, string Property, bool Nullable)[] cases =
        [
            ("web", "homePageUrl", true),
            ("web", "logoutUrl", true),
            ("web", "redirectUris", true),
            ("web", "implicitGrantSettings", false),
            ("api", "requestedAccessTokenVersion", false),
            ("info", "marketingUrl", true),
            ("info", "privacyStatementUrl", true),
            ("info", "supportUrl", true),
            ("info", "termsOfServiceUrl", true),
            ("spa", "redirectUris", true),
            ("publicClient", "redirectUris", true),
        ];

        var table = new List<string>();
        foreach (var (parent, property, nullable) in cases)
        {
            var before = (await Graph.GetObjectAsync(new GraphRequest(path).WithSelect("id", parent), Ct)).Nested(parent);
            string outcome;
            try
            {
                await Graph.PatchAsync(new GraphRequest(path), new JsonObject { [parent] = new JsonObject { [property] = null } }, Ct);
                var after = (await Graph.GetObjectAsync(new GraphRequest(path).WithSelect("id", parent), Ct)).Nested(parent);
                outcome = Classify(before, after, property);
            }
            catch (GraphException ex)
            {
                outcome = "Rejected (" + ex.Code + ")";
            }

            table.Add($"{parent}.{property}: {outcome}");
            string overlay = $"x-earm.nullable in schemas/2026-10-01/types/Microsoft.Entra/applications.json (currently {(nullable ? "not listed, i.e. nullable" : "\"" + parent + "." + property + "\": false")})";
            if (nullable)
            {
                report.Expect(
                    outcome == "Cleared",
                    $"An explicit null for {parent}.{property} is {outcome}, not Cleared: set \"{parent}.{property}\": false in {overlay} so the engine stops sending it (or send [] for collections).");
            }
            else
            {
                report.Expect(
                    outcome != "Cleared",
                    $"An explicit null for {parent}.{property} CLEARS it, but the overlay says it is not nullable: remove the \"{parent}.{property}\": false entry from {overlay}.");
            }
        }

        report.Observe("Per sub-property result of {parent:{property:null}}:" + Environment.NewLine + string.Join(Environment.NewLine, table));
        report.Complete();
    }

    // ---- helpers ----

    private const string RoleAssignmentsPath = "/roleManagement/directory/roleAssignments";
    private const string PoliciesPath = "/identity/conditionalAccess/policies";
    private const string StrengthsPath = "/policies/authenticationStrengthPolicies";
    private const string ContextsPath = "/identity/conditionalAccess/authenticationContextClassReferences";

    private static JsonObject Conditions(string includeGroup, string? excludeGroup)
    {
        var users = new JsonObject { ["includeGroups"] = new JsonArray(includeGroup) };
        users["excludeGroups"] = excludeGroup is null ? new JsonArray() : new JsonArray(excludeGroup);
        return new JsonObject
        {
            ["clientAppTypes"] = new JsonArray("all"),
            ["applications"] = new JsonObject { ["includeApplications"] = new JsonArray("All") },
            ["users"] = users,
        };
    }

    private static JsonObject Role(string id, string value, bool enabled)
    {
        return new JsonObject
        {
            ["id"] = id,
            ["value"] = value,
            ["displayName"] = value,
            ["description"] = "probe role",
            ["allowedMemberTypes"] = new JsonArray("User"),
            ["isEnabled"] = enabled,
        };
    }

    private static Dictionary<string, string> ToValueIdMap(JsonNode? array)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (array is JsonArray items)
        {
            foreach (var item in items.OfType<JsonObject>())
            {
                if (item.Text("value") is { } value && item.Text("id") is { } id)
                {
                    map[value] = id;
                }
            }
        }

        return map;
    }

    private static int? ReadVersion(JsonObject? api)
    {
        return api?["requestedAccessTokenVersion"] is JsonValue value && value.TryGetValue<int>(out int version) ? version : null;
    }

    /// <summary>Cleared (null, absent, empty array or an all-false/empty object), Unchanged, or Changed.</summary>
    private static string Classify(JsonObject? before, JsonObject? after, string property)
    {
        JsonNode? previous = before?[property];
        JsonNode? current = after?[property];
        if (current is null || (current is JsonArray array && array.Count == 0))
        {
            return "Cleared";
        }

        if (JsonNode.DeepEquals(previous, current))
        {
            return "Unchanged";
        }

        return "Changed (" + current.ToJsonString() + ")";
    }
}
