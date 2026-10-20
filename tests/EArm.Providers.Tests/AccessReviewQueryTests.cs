using System;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// When a declared access review query and the one Graph serves back are the same: Graph's <c>/v1.0</c> prefix and a members query's
/// <c>/microsoft.graph.user</c> cast are excused, nothing else is.
/// </summary>
public sealed class AccessReviewQueryTests
{
    private const string G = "6f1c3c48-1111-4a4a-9c9c-000000000001";

    private const string Assignments = "/identityGovernance/entitlementManagement/accessPackageAssignments?$filter=(accessPackageId eq 'a')";

    // ---- AreEquivalent ----

    [Theory]
    [InlineData("/groups/" + G + "/transitiveMembers", "/v1.0/groups/" + G + "/transitiveMembers/microsoft.graph.user")]
    [InlineData("/groups/" + G + "/owners", "/v1.0/groups/" + G + "/owners")]
    [InlineData("/groups/" + G + "/members", "/groups/" + G + "/members/microsoft.graph.user")]
    [InlineData("/groups/" + G + "/members/microsoft.graph.user", "/groups/" + G + "/members")]
    [InlineData("/v1.0/groups/" + G + "/owners", "/groups/" + G + "/owners")]
    [InlineData("/v1.0/groups/" + G + "/transitiveMembers", "/groups/" + G + "/transitiveMembers/microsoft.graph.user")]
    [InlineData("/groups/" + G + "/owners", "/GROUPS/" + G + "/OWNERS")]
    [InlineData("/groups/" + G + "/transitiveMembers", "/V1.0/Groups/" + G + "/TransitiveMembers/Microsoft.Graph.User")]
    [InlineData(Assignments, "/v1.0" + Assignments)]
    [InlineData("/v1.0" + Assignments, Assignments)]
    [InlineData("./owners", "./owners")]
    public void AreEquivalent_GraphsRewrites_AreTheSameQuery(string declared, string live)
    {
        Assert.True(AccessReviewQuery.AreEquivalent(declared, live), declared + " vs " + live);
    }

    [Theory]
    [InlineData("/groups/" + G + "/owners", "/beta/groups/" + G + "/owners")]
    [InlineData("/beta/groups/" + G + "/owners", "/v1.0/groups/" + G + "/owners")]
    [InlineData("/groups/" + G + "/members", "/groups/" + G + "/members/microsoft.graph.group")]
    [InlineData("/groups/" + G + "/members/microsoft.graph.group", "/groups/" + G + "/members/microsoft.graph.user")]
    [InlineData("/groups/" + G + "/transitiveMembers/microsoft.graph.group", "/v1.0/groups/" + G + "/transitiveMembers/microsoft.graph.user")]
    [InlineData("/groups/" + G + "/owners", "/groups/" + G + "/owners/microsoft.graph.user")]
    [InlineData("/groups/" + G + "/owners", "/v1.0/groups/" + G + "/owners/microsoft.graph.user")]
    [InlineData(Assignments, "/identityGovernance/entitlementManagement/accessPackageAssignments?$filter=(accessPackageId eq 'b')")]
    [InlineData(Assignments, "/v1.0/identityGovernance/entitlementManagement/accessPackageAssignments?$filter=(accessPackageId eq 'b')")]
    [InlineData("/v1.0groups/" + G + "/owners", "/groups/" + G + "/owners")]
    [InlineData("/groups/" + G + "/owners", "/v1.0groups/" + G + "/owners")]
    [InlineData("/groups/" + G + "/transitiveMembers", "/groups/" + G + "/members")]
    [InlineData("/groups/" + G + "/transitiveMembers", "/groups/" + "6f1c3c48-1111-4a4a-9c9c-000000000002" + "/transitiveMembers")]
    [InlineData("/users?$filter=department eq 'x'", "/users?$filter=department eq 'x'/microsoft.graph.user")]
    public void AreEquivalent_AnyOtherDifference_IsADifferentQuery(string declared, string live)
    {
        Assert.False(AccessReviewQuery.AreEquivalent(declared, live), declared + " vs " + live);
        Assert.False(AccessReviewQuery.AreEquivalent(live, declared), live + " vs " + declared);
    }

    [Fact]
    public void AreEquivalent_Null_EqualsOnlyNull()
    {
        Assert.True(AccessReviewQuery.AreEquivalent(null, null));
        Assert.False(AccessReviewQuery.AreEquivalent("/groups/g/owners", null));
        Assert.False(AccessReviewQuery.AreEquivalent(null, "/groups/g/owners"));
    }

    // ---- InDeclaredForm ----

    [Theory]
    [InlineData("/groups/" + G + "/transitiveMembers", "/v1.0/groups/" + G + "/transitiveMembers/microsoft.graph.user", "/groups/" + G + "/transitiveMembers")]
    [InlineData("/groups/" + G + "/owners", "/v1.0/groups/" + G + "/owners", "/groups/" + G + "/owners")]
    [InlineData("/v1.0/groups/" + G + "/members/microsoft.graph.user", "/groups/" + G + "/members", "/v1.0/groups/" + G + "/members/microsoft.graph.user")]
    [InlineData("/groups/" + G + "/members/microsoft.graph.user", "/v1.0/groups/" + G + "/members", "/groups/" + G + "/members/microsoft.graph.user")]
    [InlineData("/v1.0/groups/" + G + "/owners", "/groups/" + G + "/owners", "/v1.0/groups/" + G + "/owners")]
    [InlineData(Assignments, "/v1.0" + Assignments, Assignments)]
    [InlineData("/v1.0" + Assignments, Assignments, "/v1.0" + Assignments)]
    public void InDeclaredForm_UndoesThePrefixAndTheCast_InBothDirections(string declared, string live, string expected)
    {
        Assert.Equal(expected, AccessReviewQuery.InDeclaredForm(declared, live));
    }

    [Fact]
    public void InDeclaredForm_KeepsTheLiveLetterCase_SoACaseDifferenceStillShows()
    {
        Assert.Equal("/GROUPS/" + G + "/TransitiveMembers", AccessReviewQuery.InDeclaredForm("/groups/" + G + "/transitiveMembers", "/v1.0/GROUPS/" + G + "/TransitiveMembers/microsoft.graph.user"));
        Assert.Equal("/groups/" + G + "/OWNERS", AccessReviewQuery.InDeclaredForm("/groups/" + G + "/owners", "/V1.0/groups/" + G + "/OWNERS"));
    }

    [Theory]
    [InlineData("/groups/" + G + "/owners", "/beta/groups/" + G + "/owners")]
    [InlineData("/groups/" + G + "/members", "/v1.0/groups/" + G + "/members/microsoft.graph.group")]
    [InlineData("/groups/" + G + "/owners", "/v1.0/groups/" + G + "/owners/microsoft.graph.user")]
    [InlineData(Assignments, "/v1.0/identityGovernance/entitlementManagement/accessPackageAssignments?$filter=(accessPackageId eq 'b')")]
    public void InDeclaredForm_LeavesTheLiveTextUnchanged_WhenTheQueriesDiffer(string declared, string live)
    {
        Assert.Equal(live, AccessReviewQuery.InDeclaredForm(declared, live));
    }

    [Fact]
    public void InDeclaredForm_RefusesNull()
    {
        Assert.Throws<ArgumentNullException>(() => AccessReviewQuery.InDeclaredForm(null!, "/groups/g/owners"));
        Assert.Throws<ArgumentNullException>(() => AccessReviewQuery.InDeclaredForm("/groups/g/owners", null!));
    }
}
