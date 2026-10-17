using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Core;
using EArm.Core.Types;
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
public sealed partial class GraphBehaviourProbes : IntegrationTestBase
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
            $"'%' or '&' did not survive the query encoding: a $filter on '{names[0]}' returned {exact?.Count ?? 0} object(s). See GraphUrl.BuildQuery.");

        // Spaces in a filter value ('startswith(displayName,'<stem> 100% & ')') are accepted by Graph once they are percent-encoded.
        var all = await EventuallyAsync(
            () => Graph.ListAsync(new GraphRequest("/groups").WithFilter($"startswith(displayName,'{stem} 100% & ')").WithSelect("id", "displayName"), Ct),
            found => found is { Count: >= 3 });
        report.Expect(
            all is { Count: 3 },
            $"A $filter containing spaces, '%' and '&' returned {all?.Count ?? 0} of 3 groups: the space encoding is wrong or Graph rejects it. See GraphUrl.BuildQuery.");

        // The token is requested for the Graph scope: GET /organization must name the signed-in tenant.
        var organization = await Graph.GetObjectAsync(new GraphRequest("/organization").WithSelect("id"), Ct);
        string? organizationId = (organization?["value"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault()?.Text("id");
        report.Expect(
            string.Equals(organizationId, Fixture.Session?.TenantId, StringComparison.OrdinalIgnoreCase),
            $"GET /organization returned '{organizationId}' instead of tenant {Fixture.Session?.TenantId}: the token was issued for another tenant or audience. See GraphCloud.GraphScope.");

        // @odata.nextLink: three pages of one item each; the next link is followed as returned (validated to be on the Graph origin).
        var paged = await Graph.ListAsync(new GraphRequest("/groups").WithFilter($"startswith(displayName,'{stem} 100% & ')").WithSelect("id", "displayName").WithTop(1), Ct);
        report.Expect(
            paged.Count == 3 && paged.Select(p => p.RequireId()).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 3,
            $"Following @odata.nextLink returned {paged.Count} item(s) ({paged.Select(p => p.Text("displayName")).Distinct().Count()} distinct) instead of 3. See the paging loop in src/EArm.Graph/HttpGraphClient.cs (GetAllPagesAsync).");

        report.Complete();
    }

    // ---- helpers ----

    private const string RoleAssignmentsPath = "/roleManagement/directory/roleAssignments";
    private const string PoliciesPath = "/identity/conditionalAccess/policies";
    private const string StrengthsPath = "/policies/authenticationStrengthPolicies";
    private const string ContextsPath = "/identity/conditionalAccess/authenticationContextClassReferences";

    private const string OverlayFile = "schemas/2026-10-01/types/Microsoft.Entra/applications.json";

    /// <summary>The applications overlay's x-earm.nullable verdict for a dotted sub-property: nullable unless listed as false.</summary>
    private static bool OverlayNullable(string dotted)
    {
        var definition = ResourceTypeCatalog.Default.Find(ApplicationsProvider.ResourceType)
            ?? throw new InvalidOperationException($"The catalog has no {ApplicationsProvider.ResourceType} type.");
        return !(definition.Nullable.TryGetValue(dotted, out bool flag) && !flag);
    }

    /// <summary>PATCHes {parent:{property:value}} and classifies the read-back against <paramref name="before"/>, or "Rejected (code)".</summary>
    private async Task<string> PatchAndClassifyAsync(string path, string parent, string property, JsonNode? value, JsonObject? before)
    {
        try
        {
            await Graph.PatchAsync(new GraphRequest(path), new JsonObject { [parent] = new JsonObject { [property] = value } }, Ct);
        }
        catch (GraphException ex)
        {
            return "Rejected (" + ex.Code + ")";
        }

        var after = (await Graph.GetObjectAsync(new GraphRequest(path).WithSelect("id", parent), Ct)).Nested(parent);
        return Classify(before, after, property);
    }

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
