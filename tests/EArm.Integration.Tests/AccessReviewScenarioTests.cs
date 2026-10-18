using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Graph;
using Xunit;

namespace EArm.Integration.Tests;

/// <summary>
/// Standalone access reviews against a real tenant (docs/providers.md, accessReviews/definitions): a prefixed definition reviewing a prefixed group's
/// members, created, compared (Graph's filled-in recurrence fields, queryRoot nulls and the date-only start date must not be drift), changed with the
/// merged full-replace PUT, refused on a scope change, and deleted. Needs <c>EARM_IT_ALLOW_ACCESS_REVIEWS=1</c> (Microsoft Entra ID Governance or P2).
/// The prefix sweep removes prefixed definitions before the groups (<see cref="TenantCleanup"/>).
/// </summary>
[Collection(RealTenantCollection.Name)]
public sealed class AccessReviewScenarioTests : IntegrationTestBase
{
    private const string Definitions = "/identityGovernance/accessReviews/definitions";

    public AccessReviewScenarioTests(IntegrationFixture fixture)
        : base(fixture)
    {
    }

    [Fact]
    public async Task It_AccessReviews_DefinitionRoundTrip_MergedPut_AndImmutableScope()
    {
        Fixture.Require(Needs.AccessReviews);
        var run = Fixture.NewRun("accessreview", "access-reviews.entra.json");
        string reviewName = Fixture.Name("arv-members");
        try
        {
            AssertCreated(await Fixture.Cli.CreateAsync(run, Ct), "create");

            var review = await EventuallyAsync(() => FindListedAsync(reviewName), found => found is not null);
            Assert.True(review is not null, $"No access review named '{reviewName}' became visible.");
            string id = review.RequireId();
            var team = await GroupByNameAsync(Fixture.Name("arv-team"), "id");
            Assert.NotNull(team);
            Assert.Equal("/groups/" + team.RequireId() + "/transitiveMembers", review.Nested("scope")!.Text("query"), ignoreCase: true);
            Observe($"recurrence as served: {review.Nested("settings")?["recurrence"]?.ToJsonString()}");
            Observe($"reviewers as served: {review["reviewers"]?.ToJsonString()}");

            await AssertNoChangesEventuallyAsync(() => Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes"), "what-if after create");

            // A settings change: the merged PUT keeps the undeclared live members and the reviewers.
            var shorter = run.WithParameter("instanceDurationInDays", "7");
            AssertChange(await Fixture.Cli.WhatIfAsync(shorter, Ct, "--exit-code-on-changes"), "/definitions/" + reviewName, "Modify", "what-if with a shorter instance duration");
            AssertCreated(await Fixture.Cli.CreateAsync(shorter, Ct), "create with a shorter instance duration");
            var updated = await EventuallyAsync(
                () => Graph.GetObjectAsync(new GraphRequest($"{Definitions}/{id}"), Ct),
                found => (int?)found?.Nested("settings")?["instanceDurationInDays"] == 7);
            Assert.Equal(7, (int?)updated!.Nested("settings")!["instanceDurationInDays"]);
            Assert.NotEmpty(updated!["reviewers"]!.AsArray());
            Assert.Equal(review.Nested("scope")!.Text("query"), updated.Nested("scope")!.Text("query"));
            await AssertNoChangesEventuallyAsync(() => Fixture.Cli.WhatIfAsync(shorter, Ct, "--exit-code-on-changes"), "what-if after the duration change");

            // A scope change is refused in place, never a delete and recreate.
            var otherScope = shorter.WithParameter("scopeGroup", "reviewers");
            var refused = await Fixture.Cli.WhatIfAsync(otherScope, Ct, "--exit-code-on-changes");
            var row = refused.Changes.Single(c => ((string?)c["resourceId"])?.EndsWith("/definitions/" + reviewName, StringComparison.Ordinal) == true);
            Assert.Contains("cannot be changed in place", (string?)row["unsupportedReason"] ?? string.Empty, StringComparison.Ordinal);
            Assert.NotNull(await Graph.GetObjectAsync(new GraphRequest($"{Definitions}/{id}"), Ct));
        }
        finally
        {
            AssertCreated(await Fixture.Cli.DeleteAllAsync(run, Ct), "delete");
        }

        Assert.Null(await EventuallyAsync(() => FindListedAsync(reviewName), found => found is null));
    }

    /// <summary>The definition with the display name, from the whole list (Graph filters this list only on the scope query).</summary>
    private async Task<JsonObject?> FindListedAsync(string displayName)
    {
        var all = await Graph.ListAsync(new GraphRequest(Definitions), Ct);
        return all.SingleOrDefault(d => string.Equals(d.Text("displayName"), displayName, StringComparison.OrdinalIgnoreCase));
    }
}
