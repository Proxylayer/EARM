using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Providers;
using EArm.Graph;
using EArm.Providers.Tests.Infrastructure;
using EArm.TestSupport;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>The standalone access review definitions provider end to end, over the fake directory's access review routes.</summary>
public sealed class AccessReviewDefinitionsProviderTests
{
    private const string ReviewType = AccessReviewDefinitionsProvider.ResourceType;
    private const string ReviewPath = FakeDirectory.AccessReviewDefinitionsPath;
    private const string ReviewName = "Quarterly review of Team Sales members";
    private const string GroupId = "6f1c3c48-1111-4a4a-9c9c-000000000001";
    private const string OtherGroupId = "6f1c3c48-1111-4a4a-9c9c-000000000002";
    private const string UserA = "aaaaaaaa-0000-0000-0000-00000000000a";
    private const string UserB = "bbbbbbbb-0000-0000-0000-00000000000b";

    private const string GroupScope = "{ \"query\": \"/groups/" + GroupId + "/transitiveMembers\", \"queryType\": \"MicrosoftGraph\" }";
    private const string UserAReviewers = "[ { \"query\": \"/users/" + UserA + "\", \"queryType\": \"MicrosoftGraph\" } ]";
    private const string UserBReviewers = "[ { \"query\": \"/users/" + UserB + "\", \"queryType\": \"MicrosoftGraph\" } ]";
    private const string OwnerReviewers = "[ { \"query\": \"./owners\", \"queryType\": \"MicrosoftGraph\" } ]";

    private const string Settings = "{ \"mailNotificationsEnabled\": true, \"instanceDurationInDays\": 14, \"recurrence\": { \"pattern\": { \"type\": \"absoluteMonthly\", \"interval\": 3 }, \"range\": { \"type\": \"noEnd\", \"startDate\": \"2026-11-02T00:00:00Z\" } } }";

    private static string Review(string reviewers = UserAReviewers, string extra = "", string name = ReviewName, string scope = GroupScope, string settings = Settings)
    {
        return "{ \"type\": \"" + ReviewType + "\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"" + name + "\", \"properties\": { "
            + "\"descriptionForAdmins\": \"Owners confirm the members every quarter.\", \"descriptionForReviewers\": \"Confirm who still needs to be a member.\", "
            + "\"scope\": " + scope + ", \"reviewers\": " + reviewers + ", \"settings\": " + settings + extra + " } }";
    }

    /// <summary>A definition as Graph serves it after a create of <see cref="Review"/>: every recurrence field, queryRoot nulls, the read-only members.</summary>
    private static JsonObject LiveDefinition(string name = ReviewName, string reviewersJson = UserAReviewers, string startDate = "2026-11-02")
    {
        var reviewers = (JsonArray)JsonNode.Parse(reviewersJson)!;
        foreach (var reviewer in reviewers.OfType<JsonObject>())
        {
            reviewer["queryRoot"] = null;
        }

        return new JsonObject
        {
            ["displayName"] = name,
            ["descriptionForAdmins"] = "Owners confirm the members every quarter.",
            ["descriptionForReviewers"] = "Confirm who still needs to be a member.",
            ["status"] = "InProgress",
            ["createdDateTime"] = "2026-10-01T08:00:00.000Z",
            ["lastModifiedDateTime"] = "2026-10-01T08:00:00.000Z",
            ["createdBy"] = new JsonObject { ["id"] = "99999999-0000-0000-0000-000000000001", ["displayName"] = "Admin", ["userPrincipalName"] = "admin@contoso.com" },
            ["scope"] = new JsonObject { ["@odata.type"] = "#microsoft.graph.accessReviewQueryScope", ["query"] = "/groups/" + GroupId + "/transitiveMembers", ["queryType"] = "MicrosoftGraph", ["queryRoot"] = null },
            ["instanceEnumerationScope"] = null,
            ["reviewers"] = reviewers,
            ["fallbackReviewers"] = new JsonArray(),
            ["backupReviewers"] = new JsonArray(),
            ["stageSettings"] = new JsonArray(),
            ["additionalNotificationRecipients"] = new JsonArray(),
            ["settings"] = new JsonObject
            {
                ["mailNotificationsEnabled"] = true,
                ["reminderNotificationsEnabled"] = true,
                ["justificationRequiredOnApproval"] = false,
                ["defaultDecisionEnabled"] = false,
                ["defaultDecision"] = "None",
                ["instanceDurationInDays"] = 14,
                ["autoApplyDecisionsEnabled"] = false,
                ["recommendationsEnabled"] = true,
                ["decisionHistoriesForReviewersEnabled"] = false,
                ["applyActions"] = new JsonArray(),
                ["recurrence"] = new JsonObject
                {
                    ["pattern"] = new JsonObject { ["type"] = "absoluteMonthly", ["interval"] = 3, ["month"] = 0, ["dayOfMonth"] = 0, ["daysOfWeek"] = new JsonArray(), ["firstDayOfWeek"] = "sunday", ["index"] = "first" },
                    ["range"] = new JsonObject { ["type"] = "noEnd", ["numberOfOccurrences"] = 0, ["recurrenceTimeZone"] = null, ["startDate"] = startDate, ["endDate"] = "9999-12-31" },
                },
            },
        };
    }

    private static ResourceKey Key(string name = ReviewName)
    {
        return new ResourceKey { Type = ReviewType, Values = new Dictionary<string, string> { ["displayName"] = name } };
    }

    private static DeleteTarget Target(string graphId, string name = ReviewName)
    {
        return new DeleteTarget { Id = ResourceIdentifier.Create(ReviewType, name), Type = ReviewType, ApiVersion = ProviderHost.ApiVersion, Key = new Dictionary<string, string> { ["displayName"] = name }, GraphId = graphId };
    }

    // ---- discovery ----

    [Fact]
    public void Discovery_ImmutablesPermissionsAndCapabilities()
    {
        var host = new ProviderHost();
        var provider = host.Provider<AccessReviewDefinitionsProvider>(ReviewType);

        var immutable = provider.ImmutableProperties.Select(p => p.ToString()).ToList();
        Assert.Equal(2, immutable.Count);
        Assert.Contains(immutable, p => p.Contains("instanceEnumerationScope", StringComparison.Ordinal));
        Assert.Contains(immutable, p => p.Contains("scope", StringComparison.Ordinal) && !p.Contains("instanceEnumerationScope", StringComparison.Ordinal));
        Assert.Contains("AccessReview.ReadWrite.All", provider.RequiredPermissions(null));
        Assert.True(provider.KeyReadLagsCreate);
        Assert.IsAssignableFrom<IDeleteWarningSource>(provider);
        Assert.IsAssignableFrom<IOrphanCandidateSource>(provider);
        Assert.Equal(OrphanScope.Collection, provider.OrphanScope);
        Assert.Throws<ArgumentException>(() => new AccessReviewDefinitionsProvider(host.Catalog.Find(NamedLocationsProvider.ResourceType)!, host.Catalog));
    }

    // ---- create ----

    [Fact]
    public async Task Create_PostsTheDeclaredProperties_AfterReadingTheWholeCollection_AndTheRerunIsNoChange()
    {
        var host = new ProviderHost();

        var whatIf = await host.WhatIfAsync(host.Resources(Review()));
        var row = Assert.Single(whatIf.Plan.Changes);
        Assert.Equal(ChangeType.Create, row.Type);
        Assert.False(row.HasError);
        Check.NoWrites(whatIf);

        var run = await host.CreateAsync(host.Resources(Review()));

        Check.Succeeded(run);
        Assert.Equal("GET " + ReviewPath, run.Requests[0].ToString());
        Assert.DoesNotContain(run.Requests, r => r.UrlParameters is not null && r.UrlParameters.ContainsKey("$filter"));
        var post = Assert.Single(run.Writes);
        Assert.Equal("POST", post.Method);
        Assert.Equal(ReviewPath, post.Path);
        var body = post.Body!.AsObject();
        Assert.Equal(ReviewName, body["displayName"]!.GetValue<string>());
        Assert.Equal("Owners confirm the members every quarter.", body["descriptionForAdmins"]!.GetValue<string>());
        Assert.Equal("/groups/" + GroupId + "/transitiveMembers", body["scope"]!["query"]!.GetValue<string>());
        Assert.Equal("/users/" + UserA, body["reviewers"]![0]!["query"]!.GetValue<string>());
        Assert.Equal("2026-11-02T00:00:00Z", body["settings"]!["recurrence"]!["range"]!["startDate"]!.GetValue<string>());
        Assert.False(body.ContainsKey("id"));
        Assert.DoesNotContain(AccessReviewDefinitionShape.ReadOnlyProperties, p => body.ContainsKey(p));

        string id = host.IdOf(ReviewPath, "displayName", ReviewName);
        Assert.Equal(id, run.Executed.Operations.Single().GraphId);

        // Graph filled in status, createdBy, every recurrence field and the date-only startDate: none of it is drift.
        var stored = host.Directory.FindById(id)!;
        Assert.Equal("2026-11-02", stored["settings"]!["recurrence"]!["range"]!["startDate"]!.GetValue<string>());
        var rerun = await host.CreateAsync(host.Resources(Review()));
        Check.Succeeded(rerun);
        Assert.All(rerun.Executed.Operations, o => Assert.Equal(ChangeType.NoChange, o.ChangeType));
        Check.NoWrites(rerun);
    }

    [Fact]
    public async Task ACreateTheListOmitsForAFewReads_IsReadBackUntilVisible()
    {
        var host = new ProviderHost();
        host.Fake.HideCreatedFromLists(ReviewPath, 2);

        var run = await host.CreateAsync(host.Resources(Review()));

        Check.Succeeded(run);
        Check.NoDiagnostic(run, EngineDiagnosticCodes.CreatedObjectNotVisible);
        Assert.True(run.Requests.Count(r => r.Method == "GET" && r.Path == ReviewPath) >= 3, run.DescribeRequests());
        Assert.Single(run.Writes);
    }

    [Fact]
    public async Task CreateAsync_ReturnsTheCreatedObjectAndItsId()
    {
        var host = new ProviderHost();
        var provider = host.Provider<AccessReviewDefinitionsProvider>(ReviewType);
        var desired = (await host.WhatIfAsync(host.Resources(Review()))).Plan.Instances.Single();

        var applied = await provider.CreateAsync(desired, host.Fake, ProviderHost.Ct);

        Assert.Equal(host.IdOf(ReviewPath, "displayName", ReviewName), applied.GraphId);
        Assert.Equal("NotStarted", applied.Live!["status"]!.GetValue<string>());
    }

    // ---- read ----

    [Fact]
    public async Task Read_ListsEveryPage_AndMatchesTheDisplayNameCaseInsensitively()
    {
        var host = new ProviderHost();
        host.Fake.PageSize = 2;
        for (int i = 0; i < 5; i++)
        {
            host.Directory.Add(ReviewPath, LiveDefinition("Other review " + i));
        }

        var target = host.Directory.Add(ReviewPath, LiveDefinition(ReviewName.ToUpperInvariant()));
        var provider = host.Provider(ReviewType);
        host.ClearRequests();

        var found = await provider.ReadAsync(Key(), host.Fake, ProviderHost.Ct);

        Assert.NotNull(found);
        Assert.Equal(target["id"]!.GetValue<string>(), found.GraphId);
        Assert.Equal(3, host.Fake.Requests.Count);
        Assert.All(host.Fake.Requests, r => Assert.Equal("GET", r.Method));
        Assert.False(found.Properties.ContainsKey("createdBy"));
        Assert.False(found.Properties.ContainsKey("status"));

        Assert.Null(await provider.ReadAsync(Key("Not there"), host.Fake, ProviderHost.Ct));
        Assert.Null(await provider.ReadAsync(Key(ReviewName + " (old)"), host.Fake, ProviderHost.Ct));
    }

    [Fact]
    public async Task TwoReviewsWithTheName_AreAnAmbiguousMatch_ListingBothIds_AndNothingIsWritten()
    {
        var host = new ProviderHost();
        var first = host.Directory.Add(ReviewPath, LiveDefinition(ReviewName));
        var second = host.Directory.Add(ReviewPath, LiveDefinition(ReviewName.ToLowerInvariant()));

        var ex = await Assert.ThrowsAsync<ProviderException>(() => host.Provider(ReviewType).ReadAsync(Key(), host.Fake, ProviderHost.Ct));

        Assert.Equal(ProviderException.AmbiguousMatch, ex.Code);
        Assert.Contains("2 access reviews have displayName", ex.Message, StringComparison.Ordinal);
        Assert.Contains(first["id"]!.GetValue<string>(), ex.Message, StringComparison.Ordinal);
        Assert.Contains(second["id"]!.GetValue<string>(), ex.Message, StringComparison.Ordinal);
        Assert.Contains("rename or delete the others", ex.Message, StringComparison.Ordinal);

        var run = await host.CreateAsync(host.Resources(Review()));
        Assert.Equal(OperationStatus.Failed, run.OpOfType(ReviewType).Status);
        Check.NoWrites(run);
    }

    // ---- plan ----

    [Fact]
    public async Task Plan_GraphFilledValuesAndASameDayDate_AreNotDrift()
    {
        var host = new ProviderHost();
        host.Directory.Add(ReviewPath, LiveDefinition());

        var whatIf = await host.WhatIfAsync(host.Resources(Review()));
        var row = Assert.Single(whatIf.Plan.Changes);
        Assert.Equal(ChangeType.NoChange, row.Type);
        Assert.False(row.HasError);

        var dateOnly = await host.WhatIfAsync(host.Resources(Review(settings: Settings.Replace("2026-11-02T00:00:00Z", "2026-11-02", StringComparison.Ordinal))));
        Assert.Equal(ChangeType.NoChange, Assert.Single(dateOnly.Plan.Changes).Type);

        var run = await host.CreateAsync(host.Resources(Review()));
        Check.Succeeded(run);
        Check.NoWrites(run);
    }

    [Fact]
    public async Task Plan_ADifferentDayOrADeclaredSetting_IsAModify()
    {
        var host = new ProviderHost();
        host.Directory.Add(ReviewPath, LiveDefinition());

        var otherDay = await host.WhatIfAsync(host.Resources(Review(settings: Settings.Replace("2026-11-02T00:00:00Z", "2026-11-03T00:00:00Z", StringComparison.Ordinal))));
        var row = Assert.Single(otherDay.Plan.Changes);
        Assert.Equal(ChangeType.Modify, row.Type);
        Assert.Equal(ChangeMode.Patch, row.Mode);
        Assert.False(row.HasError);

        var setting = await host.WhatIfAsync(host.Resources(Review(settings: Settings.Replace("\"mailNotificationsEnabled\": true", "\"mailNotificationsEnabled\": false", StringComparison.Ordinal))));
        Assert.Equal(ChangeType.Modify, Assert.Single(setting.Plan.Changes).Type);

        var extraReviewer = new ProviderHost();
        extraReviewer.Directory.Add(ReviewPath, LiveDefinition(reviewersJson: "[ { \"query\": \"/users/" + UserA + "\", \"queryType\": \"MicrosoftGraph\" }, { \"query\": \"/users/" + UserB + "\", \"queryType\": \"MicrosoftGraph\" } ]"));
        var extra = await extraReviewer.WhatIfAsync(extraReviewer.Resources(Review()));
        Assert.Equal(ChangeType.Modify, Assert.Single(extra.Plan.Changes).Type);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("instanceEnumerationScope")]
    public async Task Plan_AnImmutableChange_IsRefusedInPlace_NeverDeletedAndRecreated(string property)
    {
        var host = new ProviderHost();
        var live = LiveDefinition();
        if (property == "instanceEnumerationScope")
        {
            live["instanceEnumerationScope"] = new JsonObject { ["query"] = "/groups", ["queryType"] = "MicrosoftGraph" };
        }

        host.Directory.Add(ReviewPath, live);
        string resource = property == "scope"
            ? Review(scope: GroupScope.Replace(GroupId, OtherGroupId, StringComparison.Ordinal))
            : Review(extra: ", \"instanceEnumerationScope\": { \"query\": \"/groups?$filter=(groupTypes/any(c:c eq 'Unified'))\", \"queryType\": \"MicrosoftGraph\" }");

        var whatIf = await host.WhatIfAsync(host.Resources(resource));

        var row = Assert.Single(whatIf.Plan.Changes);
        Assert.Equal(ChangeType.Modify, row.Type);
        Assert.Equal(ChangeMode.Patch, row.Mode);
        Assert.True(row.HasError);
        Assert.Contains("cannot be changed in place", row.UnsupportedReason, StringComparison.Ordinal);
        Assert.Contains(property, row.UnsupportedReason, StringComparison.Ordinal);

        var run = await host.CreateAsync(host.Resources(resource));
        Assert.DoesNotContain(run.Writes, w => w.Method is "DELETE" or "POST" or "PUT");
        Assert.Single(host.Directory.Objects(ReviewPath));
    }

    [Fact]
    public async Task Plan_AReviewerChangeBetweenIndividualUsers_IsAllowed_AndAppliedWithAPut()
    {
        var host = new ProviderHost();
        var live = host.Directory.Add(ReviewPath, LiveDefinition());
        string id = live["id"]!.GetValue<string>();

        var whatIf = await host.WhatIfAsync(host.Resources(Review(UserBReviewers)));
        var row = Assert.Single(whatIf.Plan.Changes);
        Assert.Equal(ChangeType.Modify, row.Type);
        Assert.False(row.HasError);

        var run = await host.CreateAsync(host.Resources(Review(UserBReviewers)));

        Check.Succeeded(run);
        var put = Assert.Single(run.Writes);
        Assert.Equal("PUT", put.Method);
        Assert.Equal(ReviewPath + "/" + id, put.Path);
        var reviewers = put.Body!["reviewers"]!.AsArray();
        Assert.Equal("/users/" + UserB, Assert.Single(reviewers)!["query"]!.GetValue<string>());
        Assert.Equal("/users/" + UserB, host.Directory.FindById(id)!["reviewers"]![0]!["query"]!.GetValue<string>());

        var rerun = await host.CreateAsync(host.Resources(Review(UserBReviewers)));
        Check.NoWrites(rerun);
    }

    [Fact]
    public async Task Plan_AReviewerChange_IsRefused_WhileTheLiveOrTheDeclaredReviewersAreNotAllIndividualUsers()
    {
        // Live ./owners, declared a user.
        var owners = new ProviderHost();
        owners.Directory.Add(ReviewPath, LiveDefinition(reviewersJson: OwnerReviewers));
        var fromOwners = await owners.WhatIfAsync(owners.Resources(Review(UserAReviewers)));
        var ownersRow = Assert.Single(fromOwners.Plan.Changes);
        Assert.True(ownersRow.HasError);
        Assert.Contains("./owners", ownersRow.UnsupportedReason, StringComparison.Ordinal);
        Assert.Contains("individual user", ownersRow.UnsupportedReason, StringComparison.Ordinal);
        var ownersRun = await owners.CreateAsync(owners.Resources(Review(UserAReviewers)));
        Check.NoWrites(ownersRun);

        // Live a user, declared a group's members.
        string groupReviewers = "[ { \"query\": \"/groups/" + OtherGroupId + "/transitiveMembers\", \"queryType\": \"MicrosoftGraph\" } ]";
        var users = new ProviderHost();
        users.Directory.Add(ReviewPath, LiveDefinition());
        var toGroup = await users.WhatIfAsync(users.Resources(Review(groupReviewers)));
        var groupRow = Assert.Single(toGroup.Plan.Changes);
        Assert.True(groupRow.HasError);
        Assert.Contains("/groups/" + OtherGroupId + "/transitiveMembers", groupRow.UnsupportedReason, StringComparison.Ordinal);
        Assert.Equal(ChangeMode.Patch, groupRow.Mode);

        // Unchanged non-user reviewers with another change: no refusal.
        var unchanged = await owners.WhatIfAsync(owners.Resources(Review(OwnerReviewers, settings: Settings.Replace("\"instanceDurationInDays\": 14", "\"instanceDurationInDays\": 7", StringComparison.Ordinal))));
        var unchangedRow = Assert.Single(unchanged.Plan.Changes);
        Assert.Equal(ChangeType.Modify, unchangedRow.Type);
        Assert.False(unchangedRow.HasError);
    }

    [Fact]
    public async Task Plan_ADeployTimeUnknownReviewerQuery_IsNotJudged()
    {
        var host = new ProviderHost();
        host.Directory.Add(ReviewPath, LiveDefinition());
        string group = "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"team-reviewers\", \"properties\": { \"displayName\": \"Team reviewers\", \"mailEnabled\": false, \"mailNickname\": \"team-reviewers\", \"securityEnabled\": true } }";
        string unknownReviewers = "[ { \"query\": \"[format('/groups/{0}/transitiveMembers', reference(resourceId('Microsoft.Entra/groups', 'team-reviewers')).id)]\", \"queryType\": \"MicrosoftGraph\" } ]";

        var whatIf = await host.WhatIfAsync(host.Resources(group + ", " + Review(unknownReviewers)));

        var row = whatIf.ChangeOfType(ReviewType);
        Assert.False(row.HasError, row.UnsupportedReason);
        Assert.Equal(ChangeType.Modify, row.Type);
    }

    [Fact]
    public async Task Plan_BackupAndFallbackReviewersThatDiffer_AreAnErrorRow_AndTheCreateRefusesThem()
    {
        var host = new ProviderHost();
        string differ = ", \"fallbackReviewers\": " + UserAReviewers + ", \"backupReviewers\": " + UserBReviewers;

        var whatIf = await host.WhatIfAsync(host.Resources(Review(extra: differ)));
        var row = Assert.Single(whatIf.Plan.Changes);
        Assert.Equal(ChangeType.Create, row.Type);
        Assert.True(row.HasError);
        Assert.Contains("backupReviewers and fallbackReviewers differ", row.UnsupportedReason, StringComparison.Ordinal);

        var run = await host.CreateAsync(host.Resources(Review(extra: differ)));
        Assert.NotEqual(DeploymentStatus.Succeeded, run.Executed.Status);
        Check.NoWrites(run);

        var desired = await InstanceFactory.WithPropertiesAsync(host, Review(), "{ \"descriptionForAdmins\": \"a\", \"descriptionForReviewers\": \"b\", \"scope\": " + GroupScope + differ + " }");
        var ex = await Assert.ThrowsAsync<ProviderException>(() => host.Provider(ReviewType).CreateAsync(desired, host.Fake, ProviderHost.Ct));
        Assert.Equal(EngineDiagnosticCodes.ResourceApplyFailed, ex.Code);
        Check.NoWrites(new Run(whatIf.Plan, null, host.Fake.Requests));

        var same = await host.WhatIfAsync(host.Resources(Review(extra: ", \"fallbackReviewers\": " + UserAReviewers + ", \"backupReviewers\": " + UserAReviewers)));
        Assert.False(Assert.Single(same.Plan.Changes).HasError);
    }

    [Fact]
    public async Task Plan_AMissingRequiredProperty_IsAnErrorRow()
    {
        var host = new ProviderHost();
        string noScope = Review().Replace("\"scope\": " + GroupScope + ", ", string.Empty, StringComparison.Ordinal);

        var whatIf = await host.WhatIfAsync(host.Resources(noScope));

        var row = Assert.Single(whatIf.Plan.Changes);
        Assert.True(row.HasError);
        Assert.Contains("scope", row.UnsupportedReason, StringComparison.Ordinal);
    }

    // ---- queries Graph stores rewritten (a /v1.0 prefix, a members query's /microsoft.graph.user cast) ----

    private const string Assignments = "/identityGovernance/entitlementManagement/accessPackageAssignments?$filter=(accessPackageId eq 'a')";

    private static string Scope(string query)
    {
        return "{ \"query\": \"" + query + "\", \"queryType\": \"MicrosoftGraph\" }";
    }

    private static string Reviewers(params string[] queries)
    {
        return "[ " + string.Join(", ", queries.Select(q => "{ \"query\": \"" + q + "\", \"queryType\": \"MicrosoftGraph\" }")) + " ]";
    }

    /// <summary><see cref="LiveDefinition"/> with the scope and reviewer queries as the real tenant stores them.</summary>
    private static JsonObject RewrittenLive(string scopeQuery, params string[] reviewerQueries)
    {
        var live = LiveDefinition(reviewersJson: Reviewers(reviewerQueries.Select(FakeDirectory.RewriteAccessReviewQuery).ToArray()));
        live["scope"]!["query"] = FakeDirectory.RewriteAccessReviewQuery(scopeQuery);
        return live;
    }

    [Fact]
    public async Task Rewritten_ACreatedReview_RerunsAsNoChange_WithoutAnImmutableScopeError()
    {
        var host = new ProviderHost();
        host.Directory.AccessReviewQueriesRewritten = true;
        string owners = "/groups/" + OtherGroupId + "/owners";
        string resource = Review(Reviewers(owners, "/groups/" + OtherGroupId + "/members") + ", \"fallbackReviewers\": " + Reviewers("/groups/" + GroupId + "/owners"));

        var run = await host.CreateAsync(host.Resources(resource));

        Check.Succeeded(run);
        var post = Assert.Single(run.Writes).Body!.AsObject();
        Assert.Equal("/groups/" + GroupId + "/transitiveMembers", post["scope"]!["query"]!.GetValue<string>());
        Assert.Equal(owners, post["reviewers"]![0]!["query"]!.GetValue<string>());
        var stored = host.Directory.FindById(host.IdOf(ReviewPath, "displayName", ReviewName))!;
        Assert.Equal("/v1.0/groups/" + GroupId + "/transitiveMembers/microsoft.graph.user", stored["scope"]!["query"]!.GetValue<string>());
        Assert.Equal("/v1.0" + owners, stored["reviewers"]![0]!["query"]!.GetValue<string>());
        Assert.Equal("/v1.0/groups/" + OtherGroupId + "/members/microsoft.graph.user", stored["reviewers"]![1]!["query"]!.GetValue<string>());
        Assert.Equal("/v1.0/groups/" + GroupId + "/owners", stored["fallbackReviewers"]![0]!["query"]!.GetValue<string>());

        var whatIf = await host.WhatIfAsync(host.Resources(resource));
        var row = Assert.Single(whatIf.Plan.Changes);
        Assert.Equal(ChangeType.NoChange, row.Type);
        Assert.False(row.HasError, row.UnsupportedReason);
        Assert.Null(row.UnsupportedReason);

        var rerun = await host.CreateAsync(host.Resources(resource));
        Check.Succeeded(rerun);
        Assert.All(rerun.Executed.Operations, o => Assert.Equal(ChangeType.NoChange, o.ChangeType));
        Check.NoWrites(rerun);
    }

    [Theory]
    [InlineData("/groups/" + GroupId + "/transitiveMembers", "/groups/" + OtherGroupId + "/owners")]
    [InlineData("/groups/" + GroupId + "/members", "/groups/" + OtherGroupId + "/transitiveMembers")]
    [InlineData("/v1.0/groups/" + GroupId + "/transitiveMembers", "/v1.0/groups/" + OtherGroupId + "/owners")]
    [InlineData("/groups/" + GroupId + "/owners", "./owners")]
    [InlineData(Assignments, "/groups/" + OtherGroupId + "/owners")]
    public async Task Rewritten_ConfirmedScopeAndReviewers_AreNoChange(string scopeQuery, string reviewerQuery)
    {
        var host = new ProviderHost();
        host.Directory.Add(ReviewPath, RewrittenLive(scopeQuery, reviewerQuery));
        string resource = Review(Reviewers(reviewerQuery), scope: Scope(scopeQuery));

        var whatIf = await host.WhatIfAsync(host.Resources(resource));

        var row = Assert.Single(whatIf.Plan.Changes);
        Assert.Equal(ChangeType.NoChange, row.Type);
        Assert.False(row.HasError, row.UnsupportedReason);

        var run = await host.CreateAsync(host.Resources(resource));
        Check.Succeeded(run);
        Check.NoWrites(run);
    }

    [Fact]
    public async Task Rewritten_AConfirmedInstanceEnumerationScope_IsNoChange()
    {
        var host = new ProviderHost();
        var live = RewrittenLive("/groups/" + GroupId + "/transitiveMembers", "/users/" + UserA);
        live["instanceEnumerationScope"] = new JsonObject { ["query"] = "/v1.0/groups/" + OtherGroupId + "/members/microsoft.graph.user", ["queryType"] = "MicrosoftGraph" };
        host.Directory.Add(ReviewPath, live);
        string resource = Review(extra: ", \"instanceEnumerationScope\": " + Scope("/groups/" + OtherGroupId + "/members"));

        var whatIf = await host.WhatIfAsync(host.Resources(resource));

        var row = Assert.Single(whatIf.Plan.Changes);
        Assert.Equal(ChangeType.NoChange, row.Type);
        Assert.False(row.HasError, row.UnsupportedReason);
    }

    [Theory]
    [InlineData("/groups/" + GroupId + "/transitiveMembers", "/groups/" + OtherGroupId + "/transitiveMembers")]
    [InlineData("/groups/" + GroupId + "/transitiveMembers", "/beta/groups/" + GroupId + "/transitiveMembers")]
    [InlineData("/groups/" + GroupId + "/transitiveMembers", "/groups/" + GroupId + "/transitiveMembers/microsoft.graph.group")]
    [InlineData("/groups/" + GroupId + "/owners", "/groups/" + GroupId + "/owners/microsoft.graph.user")]
    [InlineData(Assignments, "/identityGovernance/entitlementManagement/accessPackageAssignments?$filter=(accessPackageId eq 'b')")]
    public async Task Rewritten_ARealScopeChange_IsStillRefusedAsImmutable(string liveScope, string declaredScope)
    {
        var host = new ProviderHost();
        host.Directory.Add(ReviewPath, RewrittenLive(liveScope, "/users/" + UserA));
        string resource = Review(scope: Scope(declaredScope));

        var whatIf = await host.WhatIfAsync(host.Resources(resource));

        var row = Assert.Single(whatIf.Plan.Changes);
        Assert.Equal(ChangeType.Modify, row.Type);
        Assert.True(row.HasError);
        Assert.Contains("cannot be changed in place", row.UnsupportedReason, StringComparison.Ordinal);
        Assert.Contains("scope", row.UnsupportedReason, StringComparison.Ordinal);

        var run = await host.CreateAsync(host.Resources(resource));
        Assert.DoesNotContain(run.Writes, w => w.Method is "DELETE" or "POST" or "PUT");
        Assert.Single(host.Directory.Objects(ReviewPath));
    }

    [Fact]
    public async Task Rewritten_ARealChangeOfNonIndividualReviewers_IsStillRefused()
    {
        string ownersA = "/groups/" + GroupId + "/owners";
        foreach (string declared in new[] { Reviewers("/groups/" + OtherGroupId + "/owners"), Reviewers(ownersA, "./owners") })
        {
            var host = new ProviderHost();
            host.Directory.Add(ReviewPath, RewrittenLive("/groups/" + GroupId + "/transitiveMembers", ownersA));

            var whatIf = await host.WhatIfAsync(host.Resources(Review(declared)));

            var row = Assert.Single(whatIf.Plan.Changes);
            Assert.Equal(ChangeType.Modify, row.Type);
            Assert.True(row.HasError, declared);
            Assert.Contains("individual user", row.UnsupportedReason, StringComparison.Ordinal);

            var run = await host.CreateAsync(host.Resources(Review(declared)));
            Check.NoWrites(run);
        }
    }

    [Fact]
    public async Task Rewritten_AnUpdate_PutsTheTemplatesQueryText_AndTheRerunIsNoChange()
    {
        var host = new ProviderHost();
        host.Directory.AccessReviewQueriesRewritten = true;
        string owners = "/groups/" + OtherGroupId + "/owners";
        var live = host.Directory.Add(ReviewPath, RewrittenLive("/groups/" + GroupId + "/transitiveMembers", owners));
        string id = live["id"]!.GetValue<string>();
        string changed = Settings.Replace("\"instanceDurationInDays\": 14", "\"instanceDurationInDays\": 7", StringComparison.Ordinal);
        string resource = Review(Reviewers(owners), settings: changed);

        var whatIf = await host.WhatIfAsync(host.Resources(resource));
        var row = Assert.Single(whatIf.Plan.Changes);
        Assert.Equal(ChangeType.Modify, row.Type);
        Assert.False(row.HasError, row.UnsupportedReason);

        var run = await host.CreateAsync(host.Resources(resource));

        Check.Succeeded(run);
        var put = Assert.Single(run.Writes);
        Assert.Equal("PUT", put.Method);
        Assert.Equal("/groups/" + GroupId + "/transitiveMembers", put.Body!["scope"]!["query"]!.GetValue<string>());
        Assert.Equal(owners, Assert.Single(put.Body!["reviewers"]!.AsArray())!["query"]!.GetValue<string>());
        var stored = host.Directory.FindById(id)!;
        Assert.Equal(7, stored["settings"]!["instanceDurationInDays"]!.GetValue<int>());
        Assert.Equal("/v1.0/groups/" + GroupId + "/transitiveMembers/microsoft.graph.user", stored["scope"]!["query"]!.GetValue<string>());

        var rerun = await host.CreateAsync(host.Resources(resource));
        Check.Succeeded(rerun);
        Check.NoWrites(rerun);
    }

    // ---- update ----

    [Fact]
    public async Task Update_RereadsAndPutsTheLiveDefinitionWithTheDeclaredValuesMerged_ThenReadsItBack()
    {
        var host = new ProviderHost();
        var seeded = LiveDefinition();
        seeded["fallbackReviewers"] = JsonNode.Parse(UserBReviewers);
        seeded["backupReviewers"] = JsonNode.Parse(UserBReviewers);
        seeded["@odata.context"] = "https://graph.microsoft.com/v1.0/$metadata#identityGovernance/accessReviews/definitions/$entity";
        var live = host.Directory.Add(ReviewPath, seeded);
        string id = live["id"]!.GetValue<string>();
        string changed = Settings.Replace("\"instanceDurationInDays\": 14", "\"instanceDurationInDays\": 7", StringComparison.Ordinal);

        var run = await host.CreateAsync(host.Resources(Review(UserAReviewers + ", \"fallbackReviewers\": " + UserAReviewers, settings: changed)));

        Check.Succeeded(run);
        string entity = ReviewPath + "/" + id;
        int put = run.Requests.ToList().FindIndex(r => r.Method == "PUT");
        Assert.True(put > 0, run.DescribeRequests());
        Assert.Equal("GET " + entity, run.Requests[put - 1].ToString());
        Assert.Equal("GET " + entity, run.Requests[put + 1].ToString());
        Assert.Equal(entity, run.Requests[put].Path);
        Assert.Single(run.Writes);

        var body = run.Requests[put].Body!.AsObject();
        Assert.Equal(id, body["id"]!.GetValue<string>());
        foreach (string readOnly in AccessReviewDefinitionShape.ReadOnlyProperties)
        {
            Assert.False(body.ContainsKey(readOnly), readOnly + " was sent");
        }

        Assert.DoesNotContain(body, p => p.Key.StartsWith("@odata.", StringComparison.Ordinal));

        // Objects merge: the declared setting changes, the undeclared live ones stay.
        Assert.Equal(7, body["settings"]!["instanceDurationInDays"]!.GetValue<int>());
        Assert.True(body["settings"]!["recommendationsEnabled"]!.GetValue<bool>());
        Assert.True(body["settings"]!["reminderNotificationsEnabled"]!.GetValue<bool>());
        Assert.Equal("9999-12-31", body["settings"]!["recurrence"]!["range"]!["endDate"]!.GetValue<string>());

        // Declared arrays replace; only the declared reviewer alias is sent.
        Assert.Equal("/users/" + UserA, Assert.Single(body["fallbackReviewers"]!.AsArray())!["query"]!.GetValue<string>());
        Assert.False(body.ContainsKey("backupReviewers"));
        Assert.Equal("/groups/" + GroupId + "/transitiveMembers", body["scope"]!["query"]!.GetValue<string>());
        Assert.Equal("Owners confirm the members every quarter.", body["descriptionForAdmins"]!.GetValue<string>());

        var stored = host.Directory.FindById(id)!;
        Assert.Equal(7, stored["settings"]!["instanceDurationInDays"]!.GetValue<int>());
        Assert.Equal("InProgress", stored["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Update_DeclaringOnlyBackupReviewers_SendsBackupReviewersAlone()
    {
        var host = new ProviderHost();
        var seeded = LiveDefinition();
        seeded["fallbackReviewers"] = JsonNode.Parse(UserBReviewers);
        seeded["backupReviewers"] = JsonNode.Parse(UserBReviewers);
        host.Directory.Add(ReviewPath, seeded);

        var run = await host.CreateAsync(host.Resources(Review(extra: ", \"backupReviewers\": " + UserAReviewers)));

        Check.Succeeded(run);
        var body = Assert.Single(run.Writes).Body!.AsObject();
        Assert.Equal("/users/" + UserA, Assert.Single(body["backupReviewers"]!.AsArray())!["query"]!.GetValue<string>());
        Assert.False(body.ContainsKey("fallbackReviewers"));
    }

    [Fact]
    public async Task UpdateAsync_OfADefinitionThatVanished_FailsBeforeWriting()
    {
        var host = new ProviderHost();
        var provider = host.Provider<AccessReviewDefinitionsProvider>(ReviewType);
        var desired = (await host.WhatIfAsync(host.Resources(Review()))).Plan.Instances.Single();
        var existing = provider.ToExisting(new JsonObject { ["id"] = "missing", ["displayName"] = ReviewName });
        var change = provider.Plan(desired, existing, PlanOptions.Default);
        host.ClearRequests();

        var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.UpdateAsync(desired, existing, change, host.Fake, ProviderHost.Ct));

        Assert.Equal(EngineDiagnosticCodes.ResourceApplyFailed, ex.Code);
        Assert.Contains("vanished before the update", ex.Message, StringComparison.Ordinal);
        Assert.Empty(host.Fake.Writes);
    }

    // ---- delete ----

    [Fact]
    public async Task Delete_IsADelete_AndNotFoundIsTolerated()
    {
        var host = new ProviderHost();
        var live = host.Directory.Add(ReviewPath, LiveDefinition());
        string id = live["id"]!.GetValue<string>();
        var provider = host.Provider(ReviewType);
        var entry = Target(id);
        host.ClearRequests();

        await provider.DeleteAsync(entry.ResourceKey, entry.Live, host.Fake, ProviderHost.Ct);

        var delete = Assert.Single(host.Fake.Writes);
        Assert.Equal("DELETE", delete.Method);
        Assert.Equal(ReviewPath + "/" + id, delete.Path);
        Assert.Equal(0, host.Directory.Count(ReviewPath));

        // Already gone (deleted outside EARM): a 404 is nothing to do.
        await provider.DeleteAsync(entry.ResourceKey, entry.Live, host.Fake, ProviderHost.Ct);

        // Any other failure is not swallowed.
        var other = host.Directory.Add(ReviewPath, LiveDefinition());
        string otherId = other["id"]!.GetValue<string>();
        host.Fake.Failures.Add("DELETE", ReviewPath + "/" + otherId, r => new GraphException(HttpStatusCode.Forbidden, "Authorization_RequestDenied", "Insufficient privileges.", r.Hint));
        await Assert.ThrowsAnyAsync<Exception>(() => provider.DeleteAsync(Target(otherId).ResourceKey, Target(otherId).Live, host.Fake, ProviderHost.Ct));
    }

    [Fact]
    public async Task DeploymentDelete_RemovesTheDeclaredReview_AndWarnsThatItsDecisionsGoWithIt()
    {
        var host = new ProviderHost();
        await host.CreateAsync(host.Resources(Review()));
        string id = host.IdOf(ReviewPath, "displayName", ReviewName);
        var provider = host.Provider<AccessReviewDefinitionsProvider>(ReviewType);

        string? risk = provider.DescribeDeleteRisk(Target(id).ResourceKey, Target(id).Live);
        Assert.Equal("the access review's instances and the decisions recorded in them are deleted with it", risk);

        var result = await host.DeleteDeclaredAsync(host.Resources(Review()));

        Assert.True(result.Succeeded);
        Assert.Empty(host.Directory.Objects(ReviewPath));
    }

    // ---- orphans ----

    [Fact]
    public async Task Orphans_ListTheWholeCollection_ByIdAndDisplayName()
    {
        var host = new ProviderHost();
        var first = host.Directory.Add(ReviewPath, LiveDefinition("Review one"));
        var second = host.Directory.Add(ReviewPath, LiveDefinition("Review two"));
        var provider = host.Provider<AccessReviewDefinitionsProvider>(ReviewType);
        host.ClearRequests();

        var candidates = await provider.ListOrphanCandidatesAsync(new OrphanQuery(), host.Fake, ProviderHost.Ct);

        Assert.Equal(2, candidates.Count);
        Assert.Contains(candidates, c => c.GraphId == first["id"]!.GetValue<string>() && c.Name == "Review one");
        Assert.Contains(candidates, c => c.GraphId == second["id"]!.GetValue<string>() && c.Name == "Review two");
        var request = Assert.Single(host.Fake.Requests);
        Assert.Equal(ReviewPath, request.Path);
        Assert.Contains("id", request.UrlParameters!["$select"], StringComparison.Ordinal);
        Assert.Contains("displayName", request.UrlParameters!["$select"], StringComparison.Ordinal);
    }

    // ---- the sample ----

    [Fact]
    public async Task TheSample_DeploysAfterItsGroupsAndAccessPackage_WithTheirIdsInTheQueries_AndTheRerunIsNoChange()
    {
        var host = new ProviderHost();
        string template = System.IO.File.ReadAllText(SamplePath("access-reviews.entra.json"));

        var run = await host.CreateAsync(host.Parse(template, ("fallbackReviewerId", UserA)));

        Assert.True(run.Succeeded, run.Errors);
        string teamGroupId = host.IdOf("/groups", "uniqueName", "team-sales-emea");
        string reviewersGroupId = host.IdOf("/groups", "uniqueName", "team-access-reviewers");
        var members = host.Directory.Find(ReviewPath, "displayName", "Quarterly review of Team Sales EMEA members")!;
        var assignments = host.Directory.Find(ReviewPath, "displayName", "Quarterly review of Sales EMEA Team Access assignments")!;
        Assert.Equal("/groups/" + teamGroupId + "/transitiveMembers", members["scope"]!["query"]!.GetValue<string>());
        Assert.Equal("./owners", members["reviewers"]![0]!["query"]!.GetValue<string>());
        Assert.Equal("/users/" + UserA, members["fallbackReviewers"]![0]!["query"]!.GetValue<string>());
        Assert.StartsWith("/identityGovernance/entitlementManagement/accessPackageAssignments?$filter=(accessPackageId eq '", assignments["scope"]!["query"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.DoesNotContain("[", assignments["scope"]!["query"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("/groups/" + reviewersGroupId + "/transitiveMembers", assignments["reviewers"]![0]!["query"]!.GetValue<string>());
        Assert.Equal(members["id"]!.GetValue<string>(), run.Output("memberReviewId")!.GetValue<string>());
        Assert.Equal(assignments["id"]!.GetValue<string>(), run.Output("assignmentReviewId")!.GetValue<string>());

        // Each review is created after the objects its queries reference.
        var posts = run.Writes.Where(w => w.Method == "POST").Select(w => w.Path).ToList();
        Assert.True(posts.LastIndexOf("/groups") < posts.IndexOf(ReviewPath), string.Join(", ", posts));

        var rerun = await host.CreateAsync(host.Parse(template, ("fallbackReviewerId", UserA)));
        Assert.True(rerun.Succeeded, rerun.Errors);
        Assert.DoesNotContain(rerun.Writes, w => w.Path.StartsWith(ReviewPath, StringComparison.OrdinalIgnoreCase));
        Assert.All(rerun.Executed.Operations.Where(o => ResourceIdentifier.TypeEquals(o.Type, ReviewType)), o => Assert.Equal(ChangeType.NoChange, o.ChangeType));
    }

    [Fact]
    public async Task TheSample_OnATenantThatStoresQueriesRewritten_RerunsAsNoChange()
    {
        var host = new ProviderHost();
        host.Directory.AccessReviewQueriesRewritten = true;
        string template = System.IO.File.ReadAllText(SamplePath("access-reviews.entra.json"));

        var run = await host.CreateAsync(host.Parse(template, ("fallbackReviewerId", UserA)));

        Assert.True(run.Succeeded, run.Errors);
        string teamGroupId = host.IdOf("/groups", "uniqueName", "team-sales-emea");
        var members = host.Directory.Find(ReviewPath, "displayName", "Quarterly review of Team Sales EMEA members")!;
        var assignments = host.Directory.Find(ReviewPath, "displayName", "Quarterly review of Sales EMEA Team Access assignments")!;
        Assert.Equal("/v1.0/groups/" + teamGroupId + "/transitiveMembers/microsoft.graph.user", members["scope"]!["query"]!.GetValue<string>());
        Assert.StartsWith("/v1.0/identityGovernance/entitlementManagement/accessPackageAssignments?$filter=", assignments["scope"]!["query"]!.GetValue<string>(), StringComparison.Ordinal);

        // What was sent is the template's text.
        var posts = run.Writes.Where(w => w.Method == "POST" && w.Path == ReviewPath).ToList();
        Assert.Equal(2, posts.Count);
        Assert.Contains(posts, p => p.Body!["scope"]!["query"]!.GetValue<string>() == "/groups/" + teamGroupId + "/transitiveMembers");

        var whatIf = await host.WhatIfAsync(host.Parse(template, ("fallbackReviewerId", UserA)));
        Assert.All(whatIf.Plan.Changes.Where(c => ResourceIdentifier.TypeEquals(c.ResourceType, ReviewType)), c =>
        {
            Assert.Equal(ChangeType.NoChange, c.Type);
            Assert.False(c.HasError, c.UnsupportedReason);
        });

        var rerun = await host.CreateAsync(host.Parse(template, ("fallbackReviewerId", UserA)));
        Assert.True(rerun.Succeeded, rerun.Errors);
        Assert.DoesNotContain(rerun.Writes, w => w.Path.StartsWith(ReviewPath, StringComparison.OrdinalIgnoreCase));
        Assert.All(rerun.Executed.Operations.Where(o => ResourceIdentifier.TypeEquals(o.Type, ReviewType)), o => Assert.Equal(ChangeType.NoChange, o.ChangeType));
    }

    private static string SamplePath(string fileName)
    {
        var directory = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !System.IO.File.Exists(System.IO.Path.Combine(directory.FullName, "EArm.slnx")))
        {
            directory = directory.Parent;
        }

        return System.IO.Path.Combine(directory?.FullName ?? throw new InvalidOperationException("EArm.slnx not found above the test output."), "samples", fileName);
    }

    // ---- the fake's access review routes ----

    [Fact]
    public async Task Fake_RefusesADisplayNameFilter_PatchAndAScopeChangeByPut()
    {
        var host = new ProviderHost();
        var live = host.Directory.Add(ReviewPath, LiveDefinition());
        string id = live["id"]!.GetValue<string>();

        var filter = await Assert.ThrowsAsync<GraphException>(async () =>
        {
            await foreach (var _ in host.Fake.GetAllPagesAsync(new GraphRequest(ReviewPath, GraphApi.V1).WithFilter("displayName eq 'x'"), ProviderHost.Ct))
            {
            }
        });
        Assert.Equal(HttpStatusCode.BadRequest, filter.Status);

        var patch = await Assert.ThrowsAsync<GraphException>(() => host.Fake.PatchAsync(new GraphRequest(ReviewPath + "/" + id, GraphApi.V1), new JsonObject { ["descriptionForAdmins"] = "x" }, ProviderHost.Ct));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, patch.Status);

        var document = host.Directory.FindById(id)!;
        foreach (string readOnly in AccessReviewDefinitionShape.ReadOnlyProperties)
        {
            document.Remove(readOnly);
        }

        document["scope"]!["query"] = "/groups/" + OtherGroupId + "/transitiveMembers";
        var scope = await Assert.ThrowsAsync<GraphException>(() => host.Fake.PutAsync(new GraphRequest(ReviewPath + "/" + id, GraphApi.V1), document, ProviderHost.Ct));
        Assert.Equal(HttpStatusCode.BadRequest, scope.Status);
        Assert.Contains("scope", scope.Message, StringComparison.Ordinal);
    }
}
