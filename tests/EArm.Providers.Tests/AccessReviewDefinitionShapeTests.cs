using System;
using System.Text.Json.Nodes;
using EArm.Engine.Providers;
using EArm.Expressions.Values;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>The pure JSON rules of an access review definition: projection of the live object, the PUT document, and the refusals found before a request.</summary>
public sealed class AccessReviewDefinitionShapeTests
{
    private static JsonObject Obj(string json)
    {
        return (JsonObject)JsonNode.Parse(json)!;
    }

    private static JsonArray Arr(string json)
    {
        return (JsonArray)JsonNode.Parse(json)!;
    }

    private static JsonObject Reviewer(string query)
    {
        return new JsonObject { ["query"] = query, ["queryType"] = "MicrosoftGraph" };
    }

    private static JsonValue Unknown()
    {
        return UnknownJson.Create("reference('x').id", UnknownOrigins.Reference);
    }

    // ---- ProjectLive ----

    [Fact]
    public void ProjectLive_KeepsOnlyTheDeclaredMembers_WithTheLiveCasing_AndRecursesIntoObjects()
    {
        var desired = Obj("""{ "displayName": "x", "Settings": { "mailNotificationsEnabled": true }, "notLive": 1 }""");
        var live = Obj("""{ "id": "1", "displayName": "y", "status": "InProgress", "settings": { "mailNotificationsEnabled": false, "recommendationsEnabled": true } }""");

        var projected = AccessReviewDefinitionShape.ProjectLive(desired, live);

        Assert.True(JsonNode.DeepEquals(Obj("""{ "displayName": "y", "settings": { "mailNotificationsEnabled": false } }"""), projected), projected!.ToJsonString());
        Assert.Equal("InProgress", live["status"]!.GetValue<string>());
    }

    [Fact]
    public void ProjectLive_MatchesReviewersByQuery_InAnyOrderAndCase_AndKeepsExtraLiveEntries()
    {
        var desired = Arr("""[ { "query": "/users/B" }, { "query": "/users/a" } ]""");
        var live = Arr("""[ { "query": "/users/A", "queryRoot": null, "queryType": "MicrosoftGraph" }, { "query": "./owners", "queryRoot": null }, { "query": "/users/B", "queryRoot": null } ]""");

        var projected = AccessReviewDefinitionShape.ProjectLive(desired, live);

        Assert.True(JsonNode.DeepEquals(Arr("""[ { "query": "/users/B" }, { "query": "/users/A" }, { "query": "./owners", "queryRoot": null } ]"""), projected), projected!.ToJsonString());
    }

    [Fact]
    public void ProjectLive_AnEntryWithoutAMatchingQuery_FallsBackToTheSamePosition()
    {
        var desired = Arr("""[ { "query": "/users/new", "queryType": "MicrosoftGraph" } ]""");
        var live = Arr("""[ { "query": "/users/old", "queryType": "MicrosoftGraph", "queryRoot": null } ]""");

        var projected = AccessReviewDefinitionShape.ProjectLive(desired, live);

        Assert.True(JsonNode.DeepEquals(Arr("""[ { "query": "/users/old", "queryType": "MicrosoftGraph" } ]"""), projected), projected!.ToJsonString());

        // An unknown declared query matches by position too.
        var unknownDesired = new JsonArray(new JsonObject { ["query"] = Unknown() });
        var unknownProjected = AccessReviewDefinitionShape.ProjectLive(unknownDesired, live);
        Assert.Equal("/users/old", unknownProjected![0]!["query"]!.GetValue<string>());
    }

    [Fact]
    public void ProjectLive_MoreDeclaredEntriesThanLive_ProjectsOnlyTheLiveOnes()
    {
        var desired = Arr("""[ { "query": "/users/a" }, { "query": "/users/b" } ]""");
        var live = Arr("""[ { "query": "/users/a", "queryRoot": null } ]""");

        var projected = AccessReviewDefinitionShape.ProjectLive(desired, live);

        Assert.True(JsonNode.DeepEquals(Arr("""[ { "query": "/users/a" } ]"""), projected), projected!.ToJsonString());
    }

    [Fact]
    public void ProjectLive_ArraysOfScalars_AreTheLiveArray()
    {
        var projected = AccessReviewDefinitionShape.ProjectLive(Arr("""[ "monday" ]"""), Arr("""[ "monday", "friday" ]"""));

        Assert.True(JsonNode.DeepEquals(Arr("""[ "monday", "friday" ]"""), projected));
    }

    [Theory]
    [InlineData("2026-11-02T00:00:00Z", "2026-11-02", "2026-11-02T00:00:00Z")]
    [InlineData("2026-11-02T08:30:00.0000000+00:00", "2026-11-02", "2026-11-02T08:30:00.0000000+00:00")]
    [InlineData("2026-11-03T00:00:00Z", "2026-11-02", "2026-11-02")]
    [InlineData("2026-11-02", "2026-11-02", "2026-11-02")]
    [InlineData("2026-11-02 00:00:00", "2026-11-02", "2026-11-02")]
    [InlineData("2026-13-02T00:00:00Z", "2026-13-02", "2026-13-02")]
    [InlineData("2026-11-02T00:00:00Z", "2026-11-02T00:00:00Z", "2026-11-02T00:00:00Z")]
    [InlineData("abcdefghijTx", "abcdefghij", "abcdefghij")]
    public void ProjectLive_ADeclaredTimestamp_EqualsALiveDateOfTheSameDayOnly(string declared, string live, string expected)
    {
        var projected = AccessReviewDefinitionShape.ProjectLive(JsonValue.Create(declared), JsonValue.Create(live));

        Assert.Equal(expected, projected!.GetValue<string>());
    }

    [Fact]
    public void ProjectLive_AWholeRecurrence_IsNoDriftAgainstGraphsFilledInFields()
    {
        var desired = Obj("""{ "recurrence": { "pattern": { "type": "weekly", "interval": 1 }, "range": { "type": "noEnd", "startDate": "2026-11-02T00:00:00Z" } } }""");
        var live = Obj("""{ "recurrence": { "pattern": { "type": "weekly", "interval": 1, "month": 0, "dayOfMonth": 0, "daysOfWeek": [], "firstDayOfWeek": "sunday", "index": "first" }, "range": { "type": "noEnd", "numberOfOccurrences": 0, "recurrenceTimeZone": null, "startDate": "2026-11-02", "endDate": "9999-12-31" } } }""");

        var projected = AccessReviewDefinitionShape.ProjectLive(desired, live);

        Assert.True(JsonDiff.AreEquivalent(desired, projected), projected!.ToJsonString());
    }

    [Fact]
    public void ProjectLive_ADeclaredObjectAgainstALiveNull_IsTheLiveNull()
    {
        var projected = AccessReviewDefinitionShape.ProjectLive(Obj("""{ "instanceEnumerationScope": { "query": "/groups" } }"""), Obj("""{ "instanceEnumerationScope": null }"""));

        Assert.True(JsonNode.DeepEquals(Obj("""{ "instanceEnumerationScope": null }"""), projected), projected!.ToJsonString());
    }

    // ---- MergeForPut ----

    [Fact]
    public void MergeForPut_DropsTheReadOnlyMembersAndAnnotations_MergesObjects_AndReplacesArrays()
    {
        var live = Obj("""
        {
          "@odata.context": "https://graph.microsoft.com/v1.0/$metadata#definitions/$entity",
          "id": "1", "displayName": "Review", "Status": "InProgress", "createdBy": { "id": "u" }, "createdDateTime": "t", "lastModifiedDateTime": "t", "instances": [],
          "instances@odata.context": "x",
          "scope": { "@odata.type": "#microsoft.graph.accessReviewQueryScope", "query": "/groups/g/transitiveMembers" },
          "reviewers": [ { "query": "/users/a", "queryRoot": null }, { "query": "/users/b", "queryRoot": null } ],
          "settings": { "mailNotificationsEnabled": false, "recommendationsEnabled": true, "recurrence": { "range": { "type": "noEnd", "startDate": "2026-11-02" } } }
        }
        """);
        var desired = Obj("""{ "reviewers": [ { "query": "/users/c" } ], "settings": { "mailNotificationsEnabled": true, "recurrence": { "range": { "startDate": "2026-12-01" } } } }""");

        var document = AccessReviewDefinitionShape.MergeForPut(live, desired);

        foreach (string readOnly in new[] { "Status", "createdBy", "createdDateTime", "lastModifiedDateTime", "instances", "@odata.context", "instances@odata.context" })
        {
            Assert.False(document.ContainsKey(readOnly), readOnly);
        }

        Assert.Equal("1", document["id"]!.GetValue<string>());
        Assert.Equal("#microsoft.graph.accessReviewQueryScope", document["scope"]!["@odata.type"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(Arr("""[ { "query": "/users/c" } ]"""), document["reviewers"]));
        Assert.True(document["settings"]!["mailNotificationsEnabled"]!.GetValue<bool>());
        Assert.True(document["settings"]!["recommendationsEnabled"]!.GetValue<bool>());
        Assert.Equal("noEnd", document["settings"]!["recurrence"]!["range"]!["type"]!.GetValue<string>());
        Assert.Equal("2026-12-01", document["settings"]!["recurrence"]!["range"]!["startDate"]!.GetValue<string>());

        // The live object is not changed.
        Assert.Equal("InProgress", live["Status"]!.GetValue<string>());
        Assert.Equal(2, live["reviewers"]!.AsArray().Count);
    }

    [Fact]
    public void MergeForPut_MatchesDeclaredMembersToLiveOnesCaseInsensitively()
    {
        var document = AccessReviewDefinitionShape.MergeForPut(Obj("""{ "descriptionForAdmins": "old" }"""), Obj("""{ "DescriptionForAdmins": "new" }"""));

        Assert.Single(document);
        Assert.Equal("new", document["descriptionForAdmins"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("""{ "fallbackReviewers": [ { "query": "/users/n" } ] }""", true, false)]
    [InlineData("""{ "backupReviewers": [ { "query": "/users/n" } ] }""", false, true)]
    [InlineData("""{ "settings": {} }""", true, false)]
    [InlineData("""{ "fallbackReviewers": [ { "query": "/users/n" } ], "backupReviewers": [ { "query": "/users/n" } ] }""", true, true)]
    public void MergeForPut_SendsOnlyTheDeclaredReviewerAlias_ElseFallbackReviewers(string desired, bool fallback, bool backup)
    {
        var live = Obj("""{ "fallbackReviewers": [ { "query": "/users/old" } ], "backupReviewers": [ { "query": "/users/old" } ], "settings": {} }""");

        var document = AccessReviewDefinitionShape.MergeForPut(live, Obj(desired));

        Assert.Equal(fallback, document.ContainsKey("fallbackReviewers"));
        Assert.Equal(backup, document.ContainsKey("backupReviewers"));
    }

    [Fact]
    public void MergeForPut_DeclaredNull_ReplacesTheLiveValue()
    {
        var document = AccessReviewDefinitionShape.MergeForPut(Obj("""{ "settings": { "defaultDecision": "Deny" } }"""), Obj("""{ "settings": null }"""));

        Assert.True(document.ContainsKey("settings"));
        Assert.Null(document["settings"]);
    }

    // ---- Problems ----

    [Fact]
    public void Problems_BackupAndFallbackReviewersThatDiffer_AreOneProblem()
    {
        var differ = new JsonObject { ["fallbackReviewers"] = new JsonArray(Reviewer("/users/a")), ["BackupReviewers"] = new JsonArray(Reviewer("/users/b")) };
        var problem = Assert.Single(AccessReviewDefinitionShape.Problems(differ));
        Assert.Contains("declare fallbackReviewers only", problem, StringComparison.Ordinal);

        Assert.Empty(AccessReviewDefinitionShape.Problems(new JsonObject { ["fallbackReviewers"] = new JsonArray(Reviewer("/users/a")), ["backupReviewers"] = new JsonArray(Reviewer("/users/a")) }));
        Assert.Empty(AccessReviewDefinitionShape.Problems(new JsonObject { ["backupReviewers"] = new JsonArray(Reviewer("/users/a")) }));
        Assert.Empty(AccessReviewDefinitionShape.Problems(new JsonObject { ["fallbackReviewers"] = new JsonArray(Reviewer("/users/a")), ["backupReviewers"] = Unknown() }));
        Assert.Empty(AccessReviewDefinitionShape.Problems(new JsonObject()));
    }

    // ---- ReviewerChangeRefusal ----

    [Fact]
    public void ReviewerChangeRefusal_NothingWhenTheReviewersAreUndeclaredUnknownOrUnchanged()
    {
        var live = new JsonObject { ["reviewers"] = Arr("""[ { "query": "./owners", "queryType": "MicrosoftGraph", "queryRoot": null } ]""") };

        Assert.Null(AccessReviewDefinitionShape.ReviewerChangeRefusal(new JsonObject(), live));
        Assert.Null(AccessReviewDefinitionShape.ReviewerChangeRefusal(new JsonObject { ["reviewers"] = Unknown() }, live));
        Assert.Null(AccessReviewDefinitionShape.ReviewerChangeRefusal(new JsonObject { ["reviewers"] = new JsonArray(Reviewer("./owners")) }, live));
    }

    [Theory]
    [InlineData("/users/a")]
    [InlineData("/v1.0/users/a")]
    [InlineData("/beta/users/a")]
    [InlineData("/USERS/a")]
    public void ReviewerChangeRefusal_NothingBetweenIndividualUsers(string query)
    {
        var live = new JsonObject { ["reviewers"] = new JsonArray(Reviewer(query)) };
        var desired = new JsonObject { ["reviewers"] = new JsonArray(Reviewer("/users/b"), Reviewer("/users/c")) };

        Assert.Null(AccessReviewDefinitionShape.ReviewerChangeRefusal(desired, live));
    }

    [Theory]
    [InlineData("./owners")]
    [InlineData("./manager")]
    [InlineData("/groups/g/transitiveMembers")]
    [InlineData("/users/a/manager")]
    [InlineData("/users?$filter=department eq 'x'")]
    [InlineData("/users/")]
    public void ReviewerChangeRefusal_NamesTheReviewersThatAreNotIndividualUsers(string query)
    {
        var fromLive = AccessReviewDefinitionShape.ReviewerChangeRefusal(
            new JsonObject { ["reviewers"] = new JsonArray(Reviewer("/users/b")) },
            new JsonObject { ["reviewers"] = new JsonArray(Reviewer(query)) });
        Assert.NotNull(fromLive);
        Assert.Contains(query, fromLive, StringComparison.Ordinal);
        Assert.Contains("(/users/{id})", fromLive, StringComparison.Ordinal);

        var fromDeclared = AccessReviewDefinitionShape.ReviewerChangeRefusal(
            new JsonObject { ["reviewers"] = new JsonArray(Reviewer(query)) },
            new JsonObject { ["reviewers"] = new JsonArray(Reviewer("/users/a")) });
        Assert.NotNull(fromDeclared);
        Assert.Contains(query, fromDeclared, StringComparison.Ordinal);
    }

    [Fact]
    public void ReviewerChangeRefusal_ListsEachOffenderOnce_AndSkipsUnknownQueries()
    {
        var refusal = AccessReviewDefinitionShape.ReviewerChangeRefusal(
            new JsonObject { ["reviewers"] = new JsonArray(Reviewer("./OWNERS"), Reviewer("/users/b")) },
            new JsonObject { ["reviewers"] = new JsonArray(Reviewer("./owners"), Reviewer("/groups/g/members")) });
        Assert.NotNull(refusal);
        Assert.Contains("./owners, /groups/g/members", refusal, StringComparison.Ordinal);
        Assert.DoesNotContain("OWNERS", refusal, StringComparison.Ordinal);

        // A deploy-time-unknown query is not judged; the remaining reviewers are users.
        var unknown = AccessReviewDefinitionShape.ReviewerChangeRefusal(
            new JsonObject { ["reviewers"] = new JsonArray(new JsonObject { ["query"] = Unknown() }, Reviewer("/users/b")) },
            new JsonObject { ["reviewers"] = new JsonArray(Reviewer("/users/a")) });
        Assert.Null(unknown);
    }

    [Fact]
    public void ReviewerChangeRefusal_ASelfReviewThatGetsReviewers_IsJudgedOnTheDeclaredOnes()
    {
        var self = new JsonObject { ["reviewers"] = new JsonArray() };

        Assert.Null(AccessReviewDefinitionShape.ReviewerChangeRefusal(new JsonObject { ["reviewers"] = new JsonArray(Reviewer("/users/a")) }, self));
        Assert.NotNull(AccessReviewDefinitionShape.ReviewerChangeRefusal(new JsonObject { ["reviewers"] = new JsonArray(Reviewer("./owners")) }, self));
        Assert.NotNull(AccessReviewDefinitionShape.ReviewerChangeRefusal(new JsonObject { ["reviewers"] = new JsonArray() }, new JsonObject { ["reviewers"] = new JsonArray(Reviewer("./owners")) }));
    }
}
