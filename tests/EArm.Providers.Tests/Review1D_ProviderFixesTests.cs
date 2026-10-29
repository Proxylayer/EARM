using System;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Engine.Providers;
using EArm.Graph;
using EArm.Providers.Tests.Infrastructure;
using EArm.TestSupport;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// Review 1D provider fixes: G-07 (uniqueName is the upsert URL key, never a body property; reads fall back to a filter), G-08 (the
/// <c>$select</c> of a read includes the declared properties), G-09 (<c>ignoreLiveOnly</c>), G-11 (role-assignable groups),
/// G-15 (owners at create), G-16 (no navigation property in the role definition select), G-18 (grant filter fallback), G-19 (FIC parent form).
/// </summary>
public sealed class Review1D_ProviderFixesTests
{
    private const string AppType = ApplicationsProvider.ResourceType;
    private const string GroupType = "Microsoft.Entra/groups";
    private const string UserType = UsersProvider.ResourceType;
    private const string SpType = ServicePrincipalsProvider.ResourceType;
    private const string PolicyType = ConditionalAccessPoliciesProvider.ResourceType;

    private static GraphException BadRequest(FakeGraphRequest request)
    {
        return new GraphException(HttpStatusCode.BadRequest, "Request_BadRequest", "Resource not found for the segment 'uniqueName'.", request.Hint);
    }

    // ---- G-07: uniqueName ----

    [Fact]
    public async Task G07_ApplicationUpsertBody_DoesNotCarryUniqueName_ButTheUrlKeyAssignsIt()
    {
        var host = new ProviderHost();

        var run = await host.CreateAsync(host.Resources(Templates.Resource(AppType, "key-app", """{ "displayName": "Key" }""")));

        Check.Succeeded(run);
        var upsert = Assert.Single(run.Writes, w => w.Method == "PATCH");
        Assert.Equal("/applications(uniqueName='key-app')", upsert.Path);
        Assert.False(Check.Body(upsert).ContainsKey("uniqueName"));
        Assert.Equal("key-app", host.Directory.Find("/applications", "uniqueName", "key-app")!["uniqueName"]!.GetValue<string>());
        var rerun = await host.CreateAsync(host.Resources(Templates.Resource(AppType, "key-app", """{ "displayName": "Key" }""")));
        Check.NoWrites(rerun);
    }

    [Fact]
    public async Task G07_GroupUpsertBody_DoesNotCarryUniqueName()
    {
        var host = new ProviderHost();

        var run = await host.CreateAsync(host.Resources(Templates.Resource(GroupType, "key-group", """{ "displayName": "Key group" }""")));

        Check.Succeeded(run);
        var upsert = Assert.Single(run.Writes, w => w.Method == "PATCH");
        Assert.Equal("/groups(uniqueName='key-group')", upsert.Path);
        Assert.False(Check.Body(upsert).ContainsKey("uniqueName"));
        Assert.NotNull(host.Directory.Find("/groups", "uniqueName", "key-group"));
    }

    [Fact]
    public async Task G07_ReadByUniqueName_400_FallsBackToAFilteredAdvancedQuery_ForApplicationsAndGroups()
    {
        var host = new ProviderHost();
        Check.Succeeded(await host.CreateAsync(host.Resources(Templates.Resource(AppType, "fb-app", """{ "displayName": "Fallback" }""") + "," + Templates.Resource(GroupType, "fb-group", """{ "displayName": "Fallback group" }"""))));
        host.Fake.Failures.Add("GET", "(uniqueName=", BadRequest, times: -1);
        host.ClearRequests();

        var rerun = await host.CreateAsync(host.Resources(Templates.Resource(AppType, "fb-app", """{ "displayName": "Fallback" }""") + "," + Templates.Resource(GroupType, "fb-group", """{ "displayName": "Fallback group" }""")));

        Check.Succeeded(rerun);
        Check.NoWrites(rerun);
        Assert.All(rerun.Executed.Operations, o => Assert.Equal(EArm.Engine.Providers.ChangeType.NoChange, o.ChangeType));
        var filtered = rerun.Requests.Where(r => r.Method == "GET" && r.UrlParameters is not null && r.UrlParameters.TryGetValue("$filter", out var f) && f.StartsWith("uniqueName eq", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, filtered.Count);
        Assert.All(filtered, r => Assert.True(r.HasHeader("ConsistencyLevel", "eventual")));
        Assert.All(filtered, r => Assert.Equal("true", r.UrlParameters!["$count"]));
    }

    private static string TwoAppsAndTwoGroups()
    {
        return string.Join(",",
            Templates.Resource(AppType, "fb-app-1", """{ "displayName": "Fallback 1" }"""),
            Templates.Resource(AppType, "fb-app-2", """{ "displayName": "Fallback 2" }"""),
            Templates.Resource(GroupType, "fb-group-1", """{ "displayName": "Fallback group 1" }"""),
            Templates.Resource(GroupType, "fb-group-2", """{ "displayName": "Fallback group 2" }"""));
    }

    private static int KeyFormReads(Run run, string collection)
    {
        return run.Requests.Count(r => r.Method == "GET" && r.Path.StartsWith(collection + "(uniqueName=", StringComparison.Ordinal));
    }

    private static int FilterReads(Run run, string collection)
    {
        return run.Requests.Count(r => r.Method == "GET" && r.Path == collection && r.UrlParameters is not null && r.UrlParameters.TryGetValue("$filter", out var f) && f.StartsWith("uniqueName eq", StringComparison.Ordinal));
    }

    [Fact]
    public async Task G07_AfterTheFirst400_TheLaterLookupsOfTheProviderGoStraightToTheFilter_ForApplicationsAndGroups()
    {
        var host = new ProviderHost();
        Check.Succeeded(await host.CreateAsync(host.Resources(TwoAppsAndTwoGroups())));
        host.Fake.Failures.Add("GET", "(uniqueName=", BadRequest, times: -1);

        var whatIf = await host.WhatIfAsync(host.Resources(TwoAppsAndTwoGroups()));

        // One refused key-form read per type (the first lookup), then the filter for every lookup, the refused one included.
        Assert.Equal(1, KeyFormReads(whatIf, "/applications"));
        Assert.Equal(1, KeyFormReads(whatIf, "/groups"));
        Assert.Equal(2, FilterReads(whatIf, "/applications"));
        Assert.Equal(2, FilterReads(whatIf, "/groups"));
        Assert.All(whatIf.Plan.Changes, c => Assert.Equal(EArm.Engine.Providers.ChangeType.NoChange, c.Type));
        Assert.Equal(4, whatIf.Plan.Changes.Count);
    }

    [Fact]
    public async Task G07_TheRefusalIsRememberedPerProviderInstance_AFreshInstanceAsksWithTheKeyFormAgain()
    {
        var host = new ProviderHost();
        Check.Succeeded(await host.CreateAsync(host.Resources(TwoAppsAndTwoGroups())));
        host.Fake.Failures.Add("GET", "(uniqueName=", BadRequest, times: -1);
        await host.WhatIfAsync(host.Resources(TwoAppsAndTwoGroups()));

        // The same provider instances (ProviderHost keeps one registry): they remember the refusal and send no key-form read at all.
        var sameInstances = await host.WhatIfAsync(host.Resources(TwoAppsAndTwoGroups()));

        Assert.Equal(0, KeyFormReads(sameInstances, "/applications"));
        Assert.Equal(0, KeyFormReads(sameInstances, "/groups"));
        Assert.Equal(2, FilterReads(sameInstances, "/applications"));
        Assert.Equal(2, FilterReads(sameInstances, "/groups"));

        // A new registry (a new run in the CLI builds one) starts over: its first lookup per type tries the key form again.
        var freshRun = new ProviderHost(host.Fake);
        var fresh = await freshRun.WhatIfAsync(freshRun.Resources(TwoAppsAndTwoGroups()));

        Assert.Equal(1, KeyFormReads(fresh, "/applications"));
        Assert.Equal(1, KeyFormReads(fresh, "/groups"));
        Assert.All(fresh.Plan.Changes, c => Assert.Equal(EArm.Engine.Providers.ChangeType.NoChange, c.Type));
    }

    [Fact]
    public async Task G07_AKeyFormThatGraphAccepts_KeepsBeingUsed_AndA404IsNotARefusal()
    {
        // fb-app-1 / fb-group-1 exist (key-form 200); fb-app-2 / fb-group-2 do not (key-form 404): neither answer switches the provider to the filter.
        var host = new ProviderHost();
        Check.Succeeded(await host.CreateAsync(host.Resources(string.Join(",",
            Templates.Resource(AppType, "fb-app-1", """{ "displayName": "Fallback 1" }"""),
            Templates.Resource(GroupType, "fb-group-1", """{ "displayName": "Fallback group 1" }""")))));
        host.ClearRequests();

        var whatIf = await host.WhatIfAsync(host.Resources(TwoAppsAndTwoGroups()));

        Assert.Equal(2, KeyFormReads(whatIf, "/applications"));
        Assert.Equal(2, KeyFormReads(whatIf, "/groups"));
        Assert.Equal(0, FilterReads(whatIf, "/applications"));
        Assert.Equal(0, FilterReads(whatIf, "/groups"));
        Assert.Equal(2, whatIf.Plan.Changes.Count(c => c.Type == EArm.Engine.Providers.ChangeType.Create));
    }

    [Fact]
    public async Task G07_TheRefusalOfOneType_DoesNotSwitchTheOtherType()
    {
        var host = new ProviderHost();
        Check.Succeeded(await host.CreateAsync(host.Resources(TwoAppsAndTwoGroups())));
        host.Fake.Failures.Add("GET", "/groups(uniqueName=", BadRequest, times: -1);

        var whatIf = await host.WhatIfAsync(host.Resources(TwoAppsAndTwoGroups()));

        Assert.Equal(1, KeyFormReads(whatIf, "/groups"));
        Assert.Equal(2, FilterReads(whatIf, "/groups"));
        Assert.Equal(2, KeyFormReads(whatIf, "/applications"));
        Assert.Equal(0, FilterReads(whatIf, "/applications"));
    }

    [Fact]
    public async Task G07_AdoptingByAppId_SetsTheUniqueNameWithItsOwnPatch_AndAGraphRefusalIsExplained()
    {
        var host = new ProviderHost();
        string appId = Guid.NewGuid().ToString("D");
        host.Directory.Add("/applications", new JsonObject { ["displayName"] = "Brownfield", ["appId"] = appId });
        host.Fake.Failures.Add("PATCH", "/applications/", request => new GraphException(HttpStatusCode.BadRequest, "Request_BadRequest", "Property uniqueName is read-only.", request.Hint), times: -1);

        var run = await host.CreateAsync(host.Resources(Templates.Resource(AppType, "adopt-me", "{ \"displayName\": \"Brownfield\", \"appId\": \"" + appId + "\" }")));

        Assert.False(run.Succeeded);
        var op = run.OpOfType(AppType);
        Assert.Equal(ApplicationsProvider.UniqueNameNotSettable, op.ErrorCode);
        Assert.Contains("documented as immutable and read-only", op.Error, StringComparison.Ordinal);
        Assert.Contains("adopt-me", op.Error, StringComparison.Ordinal);
    }

    // ---- G-08: declared properties are selected ----

    [Fact]
    public async Task G08_ApplicationDescription_IsSelectedAndComparedNotPatchedEveryRun()
    {
        var host = new ProviderHost();
        string template = Templates.Resource(AppType, "desc-app", """{ "displayName": "Desc", "description": "Orders API" }""");
        Check.Succeeded(await host.CreateAsync(host.Resources(template)));
        host.ClearRequests();

        var rerun = await host.CreateAsync(host.Resources(template));

        Check.Succeeded(rerun);
        Check.NoWrites(rerun);
        Assert.Equal(EArm.Engine.Providers.ChangeType.NoChange, rerun.OpOfType(AppType).ChangeType);
        var read = rerun.Requests.First(r => r.Method == "GET" && r.Path.StartsWith("/applications", StringComparison.Ordinal));
        Assert.Contains("description", read.UrlParameters!["$select"].Split(','));
        Assert.Contains("displayName", read.UrlParameters["$select"].Split(','));
    }

    [Fact]
    public async Task G08_ADeclaredPassthroughProperty_OfGroupsUsersAndServicePrincipals_IsSelectedToo()
    {
        var host = new ProviderHost();
        string appId = Guid.NewGuid().ToString("D");
        host.Directory.AddApplication("sp-app", "SP app", appId);
        string template = Templates.Resource(GroupType, "sel-group", """{ "displayName": "G", "classification": "Confidential" }""") + ","
            + Templates.Resource(UserType, "hire@contoso.com", """{ "displayName": "H", "employeeHireDate": "2026-01-02T00:00:00Z" }""") + ","
            + Templates.Resource(SpType, appId, """{ "preferredTokenSigningKeyThumbprint": "ABCDEF0123" }""");
        Check.Succeeded(await host.CreateAsync(host.Resources(template)));
        host.ClearRequests();

        var rerun = await host.CreateAsync(host.Resources(template));

        Check.Succeeded(rerun);
        Check.NoWrites(rerun);
        string Select(string pathStart) => rerun.Requests.First(r => r.Method == "GET" && r.Path.StartsWith(pathStart, StringComparison.Ordinal) && r.UrlParameters is not null && r.UrlParameters.ContainsKey("$select")).UrlParameters!["$select"];
        Assert.Contains("classification", Select("/groups").Split(','));
        Assert.Contains("employeeHireDate", Select("/users").Split(','));
        Assert.Contains("preferredTokenSigningKeyThumbprint", Select("/servicePrincipals").Split(','));
    }

    [Fact]
    public async Task G08_EdgeAndCreateOnlyProperties_AreNotSelected()
    {
        var host = new ProviderHost();
        string template = Templates.Resource(UserType, "edge@contoso.com", """{ "displayName": "E", "passwordProfile": { "password": "Passw0rd!Passw0rd!" }, "memberOf": [] }""");
        Check.Succeeded(await host.CreateAsync(host.Resources(template)));
        host.ClearRequests();

        await host.CreateAsync(host.Resources(template));

        string select = host.Fake.Requests.First(r => r.Method == "GET" && r.Path.StartsWith("/users", StringComparison.Ordinal) && r.UrlParameters is not null && r.UrlParameters.ContainsKey("$select")).UrlParameters!["$select"];
        Assert.DoesNotContain("passwordProfile", select, StringComparison.Ordinal);
        Assert.DoesNotContain("memberOf", select, StringComparison.Ordinal);
    }

    // ---- G-09: ignoreLiveOnly ----

    private static string App(string web)
    {
        return Templates.Resource(AppType, "web-app", "{ \"displayName\": \"Web\", \"web\": " + web + " }");
    }

    [Fact]
    public async Task G09_ServerDefaultedLiveOnlyChildrenOfWebAndApi_AreNotDrift_AndNotNulled()
    {
        var host = new ProviderHost();
        Check.Succeeded(await host.CreateAsync(host.Resources(App("""{ "redirectUris": ["https://app.contoso.com/signin"] }"""))));
        string id = host.IdOf("/applications", "uniqueName", "web-app");
        await host.Fake.PatchAsync(new GraphRequest("/applications/" + id), JsonNode.Parse("""
            { "web": { "redirectUris": ["https://app.contoso.com/signin"], "homePageUrl": "https://home.contoso.com", "redirectUriSettings": [ { "uri": "https://app.contoso.com/signin", "index": 0 } ] },
              "api": { "requestedAccessTokenVersion": 2, "preAuthorizedApplications": [ { "appId": "11111111-1111-1111-1111-111111111111", "delegatedPermissionIds": [] } ] } }
            """)!, ProviderHost.Ct);

        var same = await host.WhatIfAsync(host.Resources(App("""{ "redirectUris": ["https://app.contoso.com/signin"] }""")));
        Assert.Equal(EArm.Engine.Providers.ChangeType.NoChange, same.ChangeOfType(AppType).Type);

        var changed = await host.CreateAsync(host.Resources(App("""{ "redirectUris": ["https://app.contoso.com/signin", "https://app.contoso.com/other"] }""")));
        Check.Succeeded(changed);
        var patch = Assert.Single(changed.Writes, w => w.Method == "PATCH");
        var web = Check.Body(patch)["web"]!.AsObject();
        Assert.Equal(["redirectUris"], web.Select(p => p.Key).ToArray());
    }

    [Fact]
    public async Task G09_ConditionalAccessSignInFrequency_ServerDefaults_AreNotDrift_AndNotNulled()
    {
        var host = new ProviderHost();
        const string group = "aaaaaaaa-1111-2222-3333-444444444444";
        string Policy(int value) => Templates.Resource(PolicyType, "CA-Frequency", "{ \"conditions\": { \"users\": { \"includeGroups\": [\"" + group + "\"] }, \"applications\": { \"includeApplications\": [\"All\"] } }, \"grantControls\": { \"builtInControls\": [\"mfa\"] }, \"sessionControls\": { \"signInFrequency\": { \"value\": " + value + ", \"type\": \"hours\", \"isEnabled\": true } } }");
        Check.Succeeded(await host.CreateAsync(host.Resources(Policy(1))));
        string id = host.IdOf("/identity/conditionalAccess/policies", "displayName", "CA-Frequency");
        await host.Fake.PatchAsync(new GraphRequest("/identity/conditionalAccess/policies/" + id), JsonNode.Parse("""
            { "sessionControls": { "signInFrequency": { "value": 1, "type": "hours", "isEnabled": true, "authenticationType": "primaryAndSecondaryAuthentication", "frequencyInterval": "timeBased" } } }
            """)!, ProviderHost.Ct);

        var same = await host.WhatIfAsync(host.Resources(Policy(1)));
        Assert.Equal(EArm.Engine.Providers.ChangeType.NoChange, same.ChangeOfType(PolicyType).Type);

        var changed = await host.CreateAsync(host.Resources(Policy(2)));
        Check.Succeeded(changed);
        var frequency = Check.Body(Assert.Single(changed.Writes, w => w.Method == "PATCH"))["sessionControls"]!["signInFrequency"]!.AsObject();
        Assert.Equal(["isEnabled", "type", "value"], frequency.Select(p => p.Key).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void G09_TheOverlaysDeclareTheIgnoreLiveOnlyPaths()
    {
        var catalog = EArm.Core.Types.ResourceTypeCatalog.Default;
        Assert.Equal(["web", "api"], ProviderSupport.IgnoreLiveOnlyPaths(catalog.Find(AppType)!));
        Assert.Equal(["sessionControls.signInFrequency"], ProviderSupport.IgnoreLiveOnlyPaths(catalog.Find(PolicyType)!));
        Assert.Empty(ProviderSupport.IgnoreLiveOnlyPaths(catalog.Find(GroupType)!));
    }

    [Fact]
    public void G09_LiveOnlyChildren_AreRemovedOnlyUnderADeclaredPath()
    {
        var definition = EArm.Core.Types.ResourceTypeCatalog.Default.Find(PolicyType)!;
        var desired = (JsonObject)JsonNode.Parse("""{ "sessionControls": { "signInFrequency": { "value": 1 } }, "state": "enabled" }""")!;
        var live = (JsonObject)JsonNode.Parse("""{ "sessionControls": { "signInFrequency": { "value": 1, "frequencyInterval": "timeBased" }, "persistentBrowser": { "mode": "never" } }, "state": "enabled", "extra": 1 }""")!;

        var pruned = ProviderSupport.PruneLiveOnly(definition, desired, new ExistingResource("id", live, live));

        Assert.Equal("""{"sessionControls":{"signInFrequency":{"value":1},"persistentBrowser":{"mode":"never"}},"state":"enabled","extra":1}""", pruned.Properties.ToJsonString());
        Assert.Same(live, ProviderSupport.PruneLiveOnly(definition, new JsonObject(), new ExistingResource("id", live, live)).Properties);
    }

    [Fact]
    public void G09_ODataAnnotationsInsideALivePolicy_AreNotPartOfIt()
    {
        var live = (JsonObject)JsonNode.Parse("""
            { "state": "disabled", "grantControls": { "operator": "OR", "builtInControls": [], "authenticationStrength@odata.context": "https://graph.microsoft.com/v1.0/$metadata#identity/conditionalAccess/policies('x')/grantControls/authenticationStrength/$entity",
              "authenticationStrength": { "id": "00000000-0000-0000-0000-000000000004", "displayName": "Phishing-resistant MFA", "policyType": "builtIn" } } }
            """)!;

        var normalized = ConditionalAccessPoliciesRules.NormalizeLive(live);

        Assert.Equal("""{"state":"disabled","grantControls":{"operator":"OR","authenticationStrength":{"id":"00000000-0000-0000-0000-000000000004"}}}""", normalized.ToJsonString());
    }

    // ---- G-10: federated credential names ----

    [Theory]
    [InlineData("github-main", true)]
    [InlineData("1st-credential", true)]
    [InlineData("under_score", true)]
    [InlineData("ab", false)]
    [InlineData("_leading", false)]
    [InlineData("-leading", false)]
    [InlineData("has space", false)]
    [InlineData("dot.ted", false)]
    public void G10_TheNamePattern_IsTheSameInTheOverlayAndTheRules(string name, bool valid)
    {
        string overlayPattern = EArm.Core.Types.ResourceTypeCatalog.Default.Find(FederatedIdentityCredentialsProvider.ResourceType)!.NamePattern!;

        Assert.Equal(FederatedIdentityCredentialsRules.DefaultNamePattern, overlayPattern);
        Assert.Equal("^[A-Za-z0-9][A-Za-z0-9_-]{2,119}$", overlayPattern);
        Assert.Equal(valid, FederatedIdentityCredentialsRules.NameProblem(name, overlayPattern) is null);
        Assert.Equal(valid, System.Text.RegularExpressions.Regex.IsMatch(name, overlayPattern));
    }

    [Fact]
    public async Task G10_ACredentialNamedWithADigitAndAnUnderscore_IsCreated()
    {
        var host = new ProviderHost();
        const string template = """
            { "type": "Microsoft.Entra/applications", "apiVersion": "2026-10-01-preview", "name": "fic-name-app", "properties": { "displayName": "A" } },
            { "type": "Microsoft.Entra/applications/federatedIdentityCredentials", "apiVersion": "2026-10-01-preview", "name": "fic-name-app/1st_credential",
              "properties": { "issuer": "https://token.actions.githubusercontent.com", "subject": "repo:contoso/orders:ref:refs/heads/main" } }
            """;

        var run = await host.CreateAsync(host.Resources(template));

        Check.Succeeded(run);
        Assert.Contains(run.Writes, w => w.Method == "PATCH" && w.Path.Contains("federatedIdentityCredentials(name='1st_credential')", StringComparison.Ordinal));
    }

    // ---- G-11 / G-15: groups ----

    private const string RoleAssignableM365 = """{ "displayName": "Role admins", "groupTypes": ["Unified"], "mailEnabled": true, "mailNickname": "roleadmins", "isAssignableToRole": true }""";

    [Fact]
    public async Task G11_RoleAssignableMicrosoft365Group_IsCreatedSecurityEnabledAndPrivate()
    {
        var host = new ProviderHost();

        var run = await host.CreateAsync(host.Resources(Templates.Resource(GroupType, "role-admins", RoleAssignableM365)));

        Check.Succeeded(run);
        var body = Check.Body(Assert.Single(run.Writes, w => w.Method == "PATCH"));
        Assert.True(body["securityEnabled"]!.GetValue<bool>());
        Assert.Equal("Private", body["visibility"]!.GetValue<string>());
        Assert.True(body["isAssignableToRole"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("""{ "displayName": "G", "isAssignableToRole": true, "securityEnabled": false, "mailEnabled": true, "groupTypes": ["Unified"] }""", "securityEnabled: true")]
    [InlineData("""{ "displayName": "G", "isAssignableToRole": true, "visibility": "Public", "groupTypes": ["Unified"], "mailEnabled": true }""", "visibility ''Private''")]
    [InlineData("""{ "displayName": "G", "isAssignableToRole": true, "groupTypes": ["DynamicMembership"], "membershipRule": "(user.department -eq \"x\")" }""", "cannot be dynamic")]
    public async Task G11_RoleAssignableGroupsThatGraphWouldRefuse_AreUnsupportedRows(string properties, string fragment)
    {
        var host = new ProviderHost();

        var whatIf = await host.WhatIfAsync(host.Resources(Templates.Resource(GroupType, "bad-role-group", properties)));

        var row = Assert.Single(whatIf.Plan.Changes);
        Assert.True(row.HasError);
        Assert.Contains(fragment.Replace("''", "'", StringComparison.Ordinal), row.UnsupportedReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task G15_DeclaredOwners_AreBoundInTheUpsertBody_AndAnOwnerlessMicrosoft365GroupWarns()
    {
        var host = new ProviderHost();
        string ownerId = host.Directory.AddUser("owner@contoso.com", "Owner")["id"]!.GetValue<string>();
        var owned = await host.WhatIfAsync(host.Resources(Templates.Resource(GroupType, "owned-group", "{ \"displayName\": \"Owned\", \"groupTypes\": [\"Unified\"], \"mailEnabled\": true, \"owners\": [\"" + ownerId + "\"] }")));
        var provider = host.Registry.Get(GroupType);

        var applied = await provider.CreateAsync(owned.Plan.Instances.Single(), host.Fake, ProviderHost.Ct);

        var body = Check.Body(Assert.Single(host.Fake.Writes, w => w.Method == "PATCH"));
        Assert.Equal(["https://graph.microsoft.com/v1.0/directoryObjects/" + ownerId], body["owners@odata.bind"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        Assert.Empty(applied.Warnings);
        Assert.Equal([ownerId], host.Directory.References("/groups/" + applied.GraphId + "/owners").ToArray());

        var ownerless = new ProviderHost();
        var plan = await ownerless.WhatIfAsync(ownerless.Resources(Templates.Resource(GroupType, "ownerless", "{ \"displayName\": \"Ownerless\", \"groupTypes\": [\"Unified\"], \"mailEnabled\": true }")));
        var result = await ownerless.Registry.Get(GroupType).CreateAsync(plan.Plan.Instances.Single(), ownerless.Fake, ProviderHost.Ct);

        var warning = Assert.Single(result.Warnings, w => w.Code == ProviderWarnings.GroupHasNoOwnerAtCreate);
        Assert.Contains("anonymously", warning.Message, StringComparison.Ordinal);
        Assert.False(Check.Body(Assert.Single(ownerless.Fake.Writes, w => w.Method == "PATCH")).ContainsKey("owners@odata.bind"));
    }

    [Fact]
    public async Task G15_ASecurityGroupWithoutOwners_DoesNotWarn()
    {
        var host = new ProviderHost();
        var plan = await host.WhatIfAsync(host.Resources(Templates.Resource(GroupType, "sec-group", """{ "displayName": "Sec" }""")));

        var result = await host.Registry.Get(GroupType).CreateAsync(plan.Plan.Instances.Single(), host.Fake, ProviderHost.Ct);

        Assert.Empty(result.Warnings);
    }

    // ---- G-16: role definitions ----

    [Fact]
    public async Task G16_RoleDefinitionReads_DoNotSelectTheInheritsPermissionsFromNavigationProperty()
    {
        var host = new ProviderHost();
        const string role = """{ "type": "Microsoft.Entra/roleDefinitions", "apiVersion": "2026-10-01-preview", "name": "Orders Support", "properties": { "description": "x", "rolePermissions": [ { "allowedResourceActions": ["microsoft.directory/users/standard/read"] } ] } }""";
        Check.Succeeded(await host.CreateAsync(host.Resources(role)));

        await host.CreateAsync(host.Resources(role));

        var selects = host.Fake.Requests.Where(r => r.Path.Contains("roleDefinitions", StringComparison.Ordinal) && r.UrlParameters is not null && r.UrlParameters.ContainsKey("$select")).Select(r => r.UrlParameters!["$select"]).ToList();
        Assert.NotEmpty(selects);
        Assert.All(selects, s => Assert.DoesNotContain("inheritsPermissionsFrom", s, StringComparison.Ordinal));
        Assert.DoesNotContain("inheritsPermissionsFrom", string.Join(',', RoleDefinitionsRules.ManagedProperties), StringComparison.Ordinal);
        Assert.Contains("inheritsPermissionsFrom", RoleDefinitionsRules.ServerManagedProperties);
    }

    // ---- G-18: oauth2PermissionGrants filter ----

    [Fact]
    public async Task G18_AGrantFilterThatGraphRejects_FallsBackToClientIdOnly_AndMatchesClientSide()
    {
        var host = new ProviderHost();
        string resourceId = host.Directory.AddServicePrincipal(Guid.NewGuid().ToString("D"), "Orders API")["id"]!.GetValue<string>();
        string clientId = host.Directory.AddServicePrincipal(Guid.NewGuid().ToString("D"), "Client")["id"]!.GetValue<string>();
        string otherResource = host.Directory.AddServicePrincipal(Guid.NewGuid().ToString("D"), "Other API")["id"]!.GetValue<string>();
        host.Directory.Add("/oauth2PermissionGrants", new JsonObject { ["clientId"] = clientId, ["resourceId"] = otherResource, ["consentType"] = "AllPrincipals", ["principalId"] = null, ["scope"] = "other.read" });
        var wanted = host.Directory.Add("/oauth2PermissionGrants", new JsonObject { ["clientId"] = clientId, ["resourceId"] = resourceId, ["consentType"] = "AllPrincipals", ["principalId"] = null, ["scope"] = "orders.read" });
        host.Fake.Failures.Add("GET", "/oauth2PermissionGrants", request => new GraphException(HttpStatusCode.BadRequest, "Request_UnsupportedQuery", "Unsupported or invalid query filter clause.", request.Hint), times: 1);
        var provider = host.Provider<OAuth2PermissionGrantsProvider>(OAuth2PermissionGrantsProvider.ResourceType);
        var key = new ResourceKey
        {
            Type = OAuth2PermissionGrantsProvider.ResourceType,
            Values = new System.Collections.Generic.Dictionary<string, string> { ["clientId"] = clientId, ["resourceId"] = resourceId, ["consentType"] = "AllPrincipals" },
        };

        var found = await provider.ReadAsync(key, host.Fake, ProviderHost.Ct);

        Assert.NotNull(found);
        Assert.Equal(wanted["id"]!.GetValue<string>(), found!.GraphId);
        var filters = host.Fake.Requests.Where(r => r.Path == "/oauth2PermissionGrants").Select(r => r.UrlParameters!["$filter"]).ToList();
        Assert.Equal(2, filters.Count);
        Assert.Contains(" and ", filters[0], StringComparison.Ordinal);
        Assert.Equal("clientId eq '" + clientId + "'", filters[1]);
    }

    // ---- G-19: federated identity credentials ----

    [Fact]
    public async Task G19_AParentUniqueNameForm_ThatGraphRejects_FallsBackToTheParentObjectId()
    {
        var host = new ProviderHost();
        const string template = """
            { "type": "Microsoft.Entra/applications", "apiVersion": "2026-10-01-preview", "name": "fic-app", "properties": { "displayName": "Fic app" } },
            { "type": "Microsoft.Entra/applications/federatedIdentityCredentials", "apiVersion": "2026-10-01-preview", "name": "fic-app/github-main",
              "properties": { "issuer": "https://token.actions.githubusercontent.com", "subject": "repo:contoso/orders:ref:refs/heads/main" } }
            """;
        Check.Succeeded(await host.CreateAsync(host.Resources(template)));
        // Review 3I (intentional change): the credential endpoints are no longer addressed through the uniqueName key, so the tenant that rejects the key form
        // rejects the parent lookup itself.
        host.Fake.Failures.Add("GET", "applications(uniqueName='fic-app')", BadRequest, times: -1);
        host.ClearRequests();

        // The application is not part of this template: the credential's parent is addressed by its uniqueName.
        const string onlyTheCredential = """
            { "type": "Microsoft.Entra/applications/federatedIdentityCredentials", "apiVersion": "2026-10-01-preview", "name": "fic-app/github-main",
              "properties": { "issuer": "https://token.actions.githubusercontent.com", "subject": "repo:contoso/orders:ref:refs/heads/main" } }
            """;
        var rerun = await host.CreateAsync(host.Resources(onlyTheCredential));

        Check.Succeeded(rerun);
        Check.NoWrites(rerun);
        Assert.Equal(EArm.Engine.Providers.ChangeType.NoChange, rerun.OpOfType(FederatedIdentityCredentialsProvider.ResourceType).ChangeType);
        Assert.Contains(rerun.Requests, r => r.Method == "GET" && r.Path == "/applications" && r.UrlParameters!["$filter"] == "uniqueName eq 'fic-app'");
    }
}
