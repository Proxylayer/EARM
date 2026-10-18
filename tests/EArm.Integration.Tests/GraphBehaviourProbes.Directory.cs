using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Core;
using Xunit;

namespace EArm.Integration.Tests;

/// <summary>Probes (b) of <see cref="GraphBehaviourProbes"/>: alternate key with an action, role assignment filters, delegated scope and app role ids, and the user restore permission.</summary>
public sealed partial class GraphBehaviourProbes
{
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
            $"POST {idUrl} did not return keyId and secretText: the object-id addPassword path of PasswordCredentialsProvider.AddAsync no longer matches Graph.");

        // The open question: does the alternate-key form accept the action? Recorded only; the provider does not depend on it.
        string altUrl = GraphPath.AlternateKey("/applications", "uniqueName", name) + "/addPassword";
        try
        {
            var byKey = await Graph.PostAsync(new GraphRequest(altUrl), new JsonObject { ["passwordCredential"] = new JsonObject { ["displayName"] = "probe-by-key" } }, Ct);
            report.Observe($"POST {altUrl} WORKS (status {byKey.Status}, keys: {string.Join(',', (byKey.Body as JsonObject)?.Select(p => p.Key) ?? [])}). The object-id lookup before addPassword (PasswordCredentialsProvider.ResolveParentIdAsync) could be dropped; docs/providers.md ('Action URLs always address the application by object id') can say the alternate-key form is also accepted.");
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
            $"GET {RoleAssignmentsPath}?$filter=principalId eq '…' did not return the assignment: RoleAssignmentsLookup relies on it.");

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
                report.Observe($"$filter={filter} is REJECTED: {ProbeReport.Describe(ex)}. Keep the single-condition filter with client-side matching in RoleAssignmentsLookup.");
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
                report.Expect(false, $"The delegated scope '{scope.Value}' does not exist on Microsoft Graph. Remove or rename it in WellKnownIds.GraphScopes.");
            }
            else if (!string.Equals(liveId, scope.Id, StringComparison.OrdinalIgnoreCase))
            {
                report.Expect(false, $"The delegated scope '{scope.Value}' has id {liveId} on Microsoft Graph, not {scope.Id}. Correct it in WellKnownIds.GraphScopes.");
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
                report.Expect(false, $"The application permission '{role.Value}' is {(liveId is null ? "missing on" : "id " + liveId + " on")} Microsoft Graph, the table says {role.Id}. Correct WellKnownIds.GraphAppRoles.");
            }
        }

        report.Observe($"{confirmed.Count} of {WellKnownIds.GraphScopes.Count(s => !s.Verified)} unverified delegated scope ids confirmed; set Verified (drop ', Verified: false') in WellKnownIds.GraphScopes for: {string.Join(", ", confirmed)}.");
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
                $"POST /directory/deletedItems/{id}/restore returned no user object (status {restored.Status}); see UsersProvider.RestoreOrFailAsync.");
        }
        catch (GraphException ex) when (ex.IsForbidden)
        {
            report.Expect(false, $"Restoring a deleted user is forbidden ({ProbeReport.Describe(ex)}). Add 'User.DeleteRestore.All' to the restore row of docs/permissions.md and to the users overlay permissions (schemas/2026-10-01/types/Microsoft.Entra/users.json, x-earm.permissions) and README.md of this project.");
        }

        report.Complete();
    }
}
