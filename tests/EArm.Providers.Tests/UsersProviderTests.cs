using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Core;
using EArm.Core.Types;
using EArm.Engine.Execution;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Engine.WhatIf;
using EArm.Providers.Tests.Infrastructure;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>The users provider end to end: passwords sent once and masked everywhere else, licensing, UPN rules, guests, soft delete, deletes and permissions.</summary>
public sealed class UsersProviderTests
{
    private const string Type = UsersProvider.ResourceType;
    private const string Sku = "6fd2c87f-b296-42f0-b197-1e91e994b900";
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private static string User(string upn, string properties)
    {
        return Templates.Resource(Type, upn, "{" + properties + "}");
    }

    private static string Id(string upn)
    {
        return "/providers/Microsoft.Entra/users/" + upn;
    }

    private static void SeedSkus(ProviderHost host)
    {
        host.Directory.Add("/subscribedSkus", new JsonObject { ["skuId"] = Sku, ["skuPartNumber"] = "ENTERPRISEPACK", ["prepaidUnits"] = new JsonObject { ["enabled"] = 10 }, ["consumedUnits"] = 1 });
    }

    [Fact]
    public void S14_Discovery_RegistersUsersProvider_WithTheManagedSelect()
    {
        var registry = ProviderDiscovery.CreateRegistry(ResourceTypeCatalog.Default);
        Assert.IsType<UsersProvider>(registry.Get(Type));
        Assert.Equal(Type, new UsersProvider(ResourceTypeCatalog.Default).TypeName);
        Assert.Contains("assignedLicenses", UsersProvider.ManagedProperties);
        Assert.Contains("licenseAssignmentStates", UsersProvider.ManagedProperties);
        Assert.Contains("streetAddress", UsersProvider.ManagedProperties);
    }

    [Fact]
    public async Task S1_ExplicitPassword_IsSentExactlyOnce_AndMaskedEverywhereElse()
    {
        const string password = "Explicit-P@ss-1234-xyz";
        var host = new ProviderHost();
        var document = host.Resources(User("alice@contoso.com", "\"displayName\": \"Alice\", \"jobTitle\": \"Dev\", \"passwordProfile\": { \"password\": \"" + password + "\" }"));

        var whatIf = await host.WhatIfAsync(document);
        string whatIfJson = WhatIfJson.Build(whatIf.Plan).ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var change = whatIf.Change("alice@contoso.com");
        Assert.Equal(ChangeType.Create, change.Type);
        Assert.False(change.HasError);
        Assert.DoesNotContain(password, whatIfJson, StringComparison.Ordinal);
        Assert.Contains("\"***\"", whatIfJson, StringComparison.Ordinal);
        Assert.DoesNotContain(password, whatIf.WhatIfTextOutput, StringComparison.Ordinal);
        string after = change.After!.ToJsonString();
        Assert.Contains("\"mailNickname\":\"alice\"", after, StringComparison.Ordinal);
        Assert.Contains("\"accountEnabled\":true", after, StringComparison.Ordinal);
        Assert.Contains("\"forceChangePasswordNextSignIn\":true", after, StringComparison.Ordinal);
        Check.NoWrites(whatIf);

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        var post = Assert.Single(run.Writes, w => w.Method == "POST" && w.Path == "/users");
        var body = Check.Body(post);
        Assert.Equal(password, body["passwordProfile"]!["password"]!.GetValue<string>());
        Assert.True(body["passwordProfile"]!["forceChangePasswordNextSignIn"]!.GetValue<bool>());
        Assert.Equal("alice@contoso.com", body["userPrincipalName"]!.GetValue<string>());
        Assert.Equal("alice", body["mailNickname"]!.GetValue<string>());
        Assert.True(body["accountEnabled"]!.GetValue<bool>());
        Assert.False(body.ContainsKey("id"));
        Assert.Equal(1, host.Fake.Requests.Count(r => r.Body is not null && r.Body.ToJsonString().Contains(password, StringComparison.Ordinal)));
        Assert.Equal(RetryHint.Default, post.Hint);
        Assert.False(host.Logs.Contains(password));
        Assert.Equal("Dev", host.Directory.Find("/users", "userPrincipalName", "alice@contoso.com")!["jobTitle"]!.GetValue<string>());
        Assert.Single(whatIf.Plan.Changes, c => c.ResourceType == Type);
    }

    [Fact]
    public async Task S2_GeneratedPassword_IsASecureOutput_AndTheRerunReadsByUpnWithNoWrites()
    {
        var host = new ProviderHost();
        const string output = "\"bobInitialPassword\": { \"type\": \"securestring\", \"value\": \"[reference('bob@contoso.com').passwordProfile.password]\" }";
        var document = host.Resources(User("bob@contoso.com", "\"displayName\": \"Bob\""), output);

        var whatIf = await host.WhatIfAsync(document);
        var change = whatIf.Change("bob@contoso.com");
        Assert.Contains(change.Reasons, r => r.Contains("generated", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("\"password\"", change.After!.ToJsonString(), StringComparison.Ordinal);

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        var post = Assert.Single(run.Writes, w => w.Method == "POST" && w.Path == "/users");
        string? generated = Check.Body(post)["passwordProfile"]?["password"]?.GetValue<string>();
        Assert.NotNull(generated);
        Assert.Equal(24, generated.Length);
        Assert.Contains(generated, char.IsUpper);
        Assert.Contains(generated, char.IsLower);
        Assert.Contains(generated, char.IsDigit);
        Assert.Contains(generated, c => !char.IsLetterOrDigit(c));
        Assert.Equal(generated, run.SecureOutput("bobInitialPassword")!.GetValue<string>());
        Assert.Equal("***", run.Output("bobInitialPassword")!.GetValue<string>());
        Assert.False(host.Logs.Contains(generated));
        Assert.DoesNotContain(generated, WhatIfJson.Build(whatIf.Plan).ToJsonString(), StringComparison.Ordinal);

        var rerunDocument = host.Resources(User("bob@contoso.com", "\"displayName\": \"Bob\""));
        var rerunWhatIf = await host.WhatIfAsync(rerunDocument);
        var rerunRow = rerunWhatIf.Change("bob@contoso.com");
        Assert.Equal(ChangeType.NoChange, rerunRow.Type);
        Assert.False(rerunRow.HasError);

        var rerun = await host.CreateAsync(rerunDocument);
        Check.Succeeded(rerun);
        Check.NoWrites(rerun);
        Assert.DoesNotContain(rerun.Requests, r => r.Body is not null && r.Body.ToJsonString().Contains("passwordProfile", StringComparison.Ordinal));
        var byUpn = rerun.Requests.First(r => r.Method == "GET" && r.Path.StartsWith("/users/", StringComparison.Ordinal));
        Assert.Equal("/users/bob@contoso.com", byUpn.Path);
        Assert.Contains("licenseAssignmentStates", byUpn.UrlParameters!["$select"], StringComparison.Ordinal);
        Assert.Contains("jobTitle", byUpn.UrlParameters["$select"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task S3_JobTitleDrift_IsPatchedAlone_ReadByUpnWithoutState()
    {
        var host = new ProviderHost();
        host.Directory.AddUser("carol@contoso.com", "Carol", u =>
        {
            u["jobTitle"] = "Old title";
            u["department"] = "R&D";
        });

        var run = await host.CreateAsync(host.Resources(User("carol@contoso.com", "\"displayName\": \"Carol\", \"jobTitle\": \"New title\", \"department\": \"R&D\"")));

        Check.Succeeded(run);
        var patch = Assert.Single(run.Writes);
        Assert.Equal("PATCH", patch.Method);
        Assert.StartsWith("/users/", patch.Path, StringComparison.Ordinal);
        Assert.Equal("""{"jobTitle":"New title"}""", patch.Body!.ToJsonString());
        Assert.Equal("GET", run.Requests[0].Method);
        Assert.Equal("/users/carol@contoso.com", run.Requests[0].Path);
    }

    /// <summary>
    /// A Ctrl+C that arrives after <c>POST /users</c> committed but before <c>assignLicense</c> used to escape as an OperationCanceledException: the
    /// executor recorded the user as skipped, so the user was orphaned and its generated password (returned by nobody, ever again) lost. The
    /// cancellation is now handled like any failure after the create: the user and its password are handed over as the partial result.
    /// </summary>
    [Fact]
    public async Task ACancellationBeforeAssignLicense_HandsOverTheCreatedUser_WithItsGeneratedPassword()
    {
        var host = new ProviderHost();
        SeedSkus(host);
        using var cts = new System.Threading.CancellationTokenSource();
        var graph = new InterceptingGraphClient(host.Fake)
        {
            PostOverride = (request, body) =>
            {
                if (request.Path == "/users")
                {
                    var response = host.Fake.PostAsync(request, body, ProviderHost.Ct).GetAwaiter().GetResult();
                    cts.Cancel();
                    return response;
                }

                cts.Token.ThrowIfCancellationRequested();
                return null;
            },
        };
        var whatIf = await host.WhatIfAsync(host.Resources(User("erin@contoso.com", "\"displayName\": \"Erin\", \"usageLocation\": \"GB\", \"assignedLicenses\": [ { \"skuPartNumber\": \"ENTERPRISEPACK\", \"disabledPlans\": [] } ]")));
        var instance = whatIf.Plan.Instances.Single(i => ResourceIdentifier.TypeEquals(i.Type, Type));

        var error = await Assert.ThrowsAsync<ProviderException>(() => host.Registry.Get(Type).CreateAsync(instance, graph, cts.Token));

        var partial = Assert.IsType<AppliedResource>(error.Partial);
        Assert.Equal(host.IdOf("/users", "userPrincipalName", "erin@contoso.com"), partial.GraphId);
        Assert.True(partial.Extra!["generatedPassword"]!.GetValue<bool>());
        Assert.IsType<OperationCanceledException>(error.InnerException, exactMatch: false);
    }

    [Fact]
    public async Task S4_AssignedLicenses_AreAddedAfterCreate_WithReplicationHint_AndNotRepeated()
    {
        var host = new ProviderHost();
        SeedSkus(host);
        var document = host.Resources(User("dave@contoso.com", "\"displayName\": \"Dave\", \"usageLocation\": \"GB\", \"assignedLicenses\": [ { \"skuPartNumber\": \"ENTERPRISEPACK\", \"disabledPlans\": [] } ]"));

        var whatIf = await host.WhatIfAsync(document);
        var change = whatIf.Change("dave@contoso.com");
        Assert.Equal(ChangeType.Create, change.Type);
        Assert.False(change.HasError, change.UnsupportedReason);
        Assert.Contains(change.Deltas, d => d.Path.ToString() == "properties.assignedLicenses");

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        var assign = Assert.Single(run.Writes, w => w.Path.EndsWith("/assignLicense", StringComparison.Ordinal));
        Assert.Equal("POST", assign.Method);
        Assert.StartsWith("/users/", assign.Path, StringComparison.Ordinal);
        Assert.Equal("{\"addLicenses\":[{\"skuId\":\"" + Sku + "\",\"disabledPlans\":[]}],\"removeLicenses\":[]}", assign.Body!.ToJsonString());
        Assert.Equal(RetryHint.ExpectReplicationLag, assign.Hint);
        Assert.Contains(run.Requests, r => r.Path == "/subscribedSkus");

        var rerun = await host.CreateAsync(document);
        Check.Succeeded(rerun);
        Check.NoWrites(rerun);
        Assert.True(rerun.Requests.Count(r => r.Path == "/subscribedSkus") <= 1);
    }

    [Fact]
    public async Task S5_InheritedLicenses_AreNeverRemoved_AndCanBeDeclaredDirectly()
    {
        var host = new ProviderHost();
        SeedSkus(host);
        host.Directory.AddUser("erin@contoso.com", "Erin", u =>
        {
            u["usageLocation"] = "GB";
            u["assignedLicenses"] = new JsonArray(new JsonObject { ["skuId"] = Sku, ["disabledPlans"] = new JsonArray() });
            u["licenseAssignmentStates"] = new JsonArray(new JsonObject { ["skuId"] = Sku, ["disabledPlans"] = new JsonArray(), ["assignedByGroup"] = "22222222-2222-2222-2222-222222222222", ["state"] = "Active", ["error"] = "None" });
        });

        var empty = await host.CreateAsync(host.Resources(User("erin@contoso.com", "\"displayName\": \"Erin\", \"usageLocation\": \"GB\", \"assignedLicenses\": []")));
        Check.Succeeded(empty);
        Check.NoWrites(empty);
        Assert.Single(host.Directory.Find("/users", "userPrincipalName", "erin@contoso.com")!["assignedLicenses"]!.AsArray());

        var same = await host.CreateAsync(host.Resources(User("erin@contoso.com", "\"displayName\": \"Erin\", \"usageLocation\": \"GB\", \"assignedLicenses\": [ { \"skuPartNumber\": \"ENTERPRISEPACK\" } ]")));
        Check.Succeeded(same);
        var assign = Assert.Single(same.Writes, w => w.Path.EndsWith("/assignLicense", StringComparison.Ordinal));
        Assert.Contains("\"addLicenses\":[{\"skuId\":\"" + Sku + "\"", assign.Body!.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("\"removeLicenses\":[]", assign.Body!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task S6_DirectLicense_IsRemovedWhenTheTemplateDeclaresNone()
    {
        var host = new ProviderHost();
        SeedSkus(host);
        host.Directory.AddUser("frank@contoso.com", "Frank", u =>
        {
            u["usageLocation"] = "GB";
            u["assignedLicenses"] = new JsonArray(new JsonObject { ["skuId"] = Sku, ["disabledPlans"] = new JsonArray() });
            u["licenseAssignmentStates"] = new JsonArray(
                new JsonObject { ["skuId"] = Sku, ["disabledPlans"] = new JsonArray(), ["assignedByGroup"] = null, ["state"] = "Active", ["error"] = "None" },
                new JsonObject { ["skuId"] = Sku, ["disabledPlans"] = new JsonArray(), ["assignedByGroup"] = "22222222-2222-2222-2222-222222222222", ["state"] = "Active", ["error"] = "None" });
        });
        var document = host.Resources(User("frank@contoso.com", "\"displayName\": \"Frank\", \"usageLocation\": \"GB\", \"assignedLicenses\": []"));

        var whatIf = await host.WhatIfAsync(document);
        Assert.Equal(ChangeType.Modify, whatIf.Change("frank@contoso.com").Type);

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        var assign = Assert.Single(run.Writes);
        Assert.EndsWith("/assignLicense", assign.Path, StringComparison.Ordinal);
        Assert.Equal("{\"addLicenses\":[],\"removeLicenses\":[\"" + Sku + "\"]}", assign.Body!.ToJsonString());
        var live = host.Directory.Find("/users", "userPrincipalName", "frank@contoso.com")!;
        var state = Assert.Single(live["licenseAssignmentStates"]!.AsArray());
        Assert.NotEmpty(state!["assignedByGroup"]!.GetValue<string>());
    }

    [Fact]
    public async Task S7_AssignedLicenses_RequireAUsageLocation()
    {
        var host = new ProviderHost();
        SeedSkus(host);

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => host.WhatIfAsync(host.Resources(User("gina@contoso.com", "\"displayName\": \"Gina\", \"assignedLicenses\": [ { \"skuId\": \"" + Sku + "\" } ]"))));
        Assert.Contains("AssignedLicensesRequireUsageLocation", ex.Message, StringComparison.Ordinal);

        var declaredNull = host.Resources(User("gina@contoso.com", "\"displayName\": \"Gina\", \"usageLocation\": null, \"assignedLicenses\": [ { \"skuId\": \"" + Sku + "\" } ]"));
        var whatIf = await host.WhatIfAsync(declaredNull);
        var change = whatIf.Change("gina@contoso.com");
        Assert.True(change.HasError);
        Assert.Contains("usageLocation", change.UnsupportedReason, StringComparison.Ordinal);
        var run = await host.CreateAsync(declaredNull);
        Assert.Equal(OperationStatus.Failed, run.Op("gina@contoso.com").Status);
        Check.NoWrites(run);

        var liveHost = new ProviderHost();
        SeedSkus(liveHost);
        liveHost.Directory.AddUser("gina@contoso.com", "Gina", u => u["usageLocation"] = "GB");
        Assert.True((await liveHost.WhatIfAsync(declaredNull)).Change("gina@contoso.com").HasError);

        var empty = await host.WhatIfAsync(host.Resources(User("gina@contoso.com", "\"displayName\": \"Gina\", \"usageLocation\": null, \"assignedLicenses\": []")));
        Assert.False(empty.Change("gina@contoso.com").HasError);
    }

    private const string UserOldId = "0a0a0a0a-0000-4000-8000-0000000000a1";
    private const string UserIvanId = "0a0a0a0a-0000-4000-8000-0000000000a2";
    private const string UserGoneId = "0a0a0a0a-0000-4000-8000-0000000000a3";

    [Fact]
    public async Task S8_UpnChange_IsANewUser_TheRenamedOneIsNeverTouched()
    {
        var host = new ProviderHost();
        var old = host.Directory.AddUser("old@contoso.com", "Renamed", u => u["id"] = UserOldId);

        var whatIf = await host.WhatIfAsync(host.Resources(User("new@contoso.com", "\"displayName\": \"Renamed\"")));

        // Identity is the live key (ARM rule): a user renamed outside the template is not the declared one.
        // The declared UPN is looked up live and, absent, is a create; the renamed user is never read by its id or planned against.
        var change = whatIf.Change("new@contoso.com");
        Assert.False(change.HasError);
        Assert.Equal(ChangeType.Create, change.Type);
        Assert.Equal("/users/new@contoso.com", whatIf.Requests[0].Path);
        Assert.DoesNotContain(whatIf.Requests, r => r.Path == "/users/" + UserOldId);
    }

    [Fact]
    public async Task S9_GuestUpn_IsPercentEncoded_AndGetsATrimmedMailNickname()
    {
        const string guest = "alice_gmail.com#EXT#@contoso.onmicrosoft.com";
        var host = new ProviderHost();
        host.Directory.AddUser(guest, "Alice (Guest)", u => u["userType"] = "Guest");
        var document = host.Resources(User(guest, "\"displayName\": \"Alice (Guest)\", \"userType\": \"Guest\""));

        var whatIf = await host.WhatIfAsync(document);
        Assert.Equal("/users/alice_gmail.com%23EXT%23@contoso.onmicrosoft.com", whatIf.Requests[0].Path);
        var change = whatIf.Change(guest);
        Assert.Equal(ChangeType.NoChange, change.Type);
        Assert.False(change.HasError);

        var fresh = new ProviderHost();
        var created = await fresh.CreateAsync(document);
        Check.Succeeded(created);
        var post = Assert.Single(created.Writes, w => w.Method == "POST" && w.Path == "/users");
        Assert.Equal("alice_gmail-com-ext", Check.Body(post)["mailNickname"]!.GetValue<string>());
    }

    [Fact]
    public async Task S10_SoftDeletedUser_IsAConflict_AndRestoreIsRestoreThenPatch_WithoutAPassword()
    {
        var host = new ProviderHost();
        host.Directory.AddDeleted("/users", new JsonObject { ["id"] = "U-DEL", ["userPrincipalName"] = "henry@contoso.com", ["displayName"] = "Henry (old)", ["mailNickname"] = "henry", ["accountEnabled"] = false, ["jobTitle"] = "Former" });
        var document = host.Resources(User("henry@contoso.com", "\"displayName\": \"Henry\", \"jobTitle\": \"Returning\""));

        var run = await host.CreateAsync(document);

        var op = run.Op("henry@contoso.com");
        Assert.Equal(OperationStatus.Failed, op.Status);
        Assert.Equal(ProviderException.SoftDeletedConflict, op.ErrorCode);
        var probe = Assert.Single(run.Requests, r => r.Path == UsersProvider.DeletedUsersPath);
        Assert.Equal("userPrincipalName eq 'henry@contoso.com'", probe.UrlParameters!["$filter"]);
        Assert.True(probe.HasHeader("ConsistencyLevel", "eventual"));
        Check.NoWrites(run);

        host.OnSoftDeletedConflict = SoftDeletedConflict.Restore;
        var restored = await host.CreateAsync(document);

        Check.Succeeded(restored);
        var writes = restored.Writes;
        Assert.Equal(2, writes.Count);
        Assert.Equal("POST", writes[0].Method);
        Assert.Equal("/directory/deletedItems/U-DEL/restore", writes[0].Path);
        Assert.Equal("PATCH", writes[1].Method);
        Assert.Equal("/users/U-DEL", writes[1].Path);
        Assert.Equal("""{"displayName":"Henry","jobTitle":"Returning","accountEnabled":true,"mailNickname":"henry"}""", writes[1].Body!.ToJsonString());
        Assert.DoesNotContain(restored.Requests, r => r.Body is not null && r.Body.ToJsonString().Contains("passwordProfile", StringComparison.Ordinal));
        Assert.Equal("Returning", host.Directory.Find("/users", "userPrincipalName", "henry@contoso.com")!["jobTitle"]!.GetValue<string>());
    }

    [Fact]
    public async Task S11_DeploymentDelete_DeletesUsersById_AndSkipsOnesThatDoNotExist()
    {
        var host = new ProviderHost();
        host.Directory.AddUser("ivan@contoso.com", "Ivan", u => u["id"] = UserIvanId);

        var deletion = await host.DeleteDeclaredAsync(host.Resources(User("ivan@contoso.com", "\"displayName\": \"Ivan\"") + ", " + User("zed@contoso.com", "\"displayName\": \"Zed\"")));

        Assert.True(deletion.Succeeded);
        Assert.Contains(host.Fake.Writes, w => w.Method == "DELETE" && w.Path == "/users/" + UserIvanId);

        // A declared user that does not exist is not listed at all: nothing is sent for it.
        Assert.DoesNotContain(host.Fake.Writes, w => w.Method == "DELETE" && w.Path == "/users/" + UserGoneId);
        Assert.Null(host.Directory.Find("/users", "userPrincipalName", "ivan@contoso.com"));
        var outcome = Assert.Single(deletion.Outcomes);
        Assert.Equal(DeletionStatus.Deleted, outcome.Status);
    }

    public static TheoryData<string, string[]> PermissionCases => new()
    {
        { "\"displayName\": \"P\"", new[] { "User.ReadWrite.All" } },
        { "\"displayName\": \"P\", \"memberOf\": [\"11111111-1111-1111-1111-111111111111\"]", new[] { "User.ReadWrite.All", "GroupMember.ReadWrite.All", "Directory.Read.All" } },
        { "\"displayName\": \"P\", \"administrativeUnits\": [\"11111111-1111-1111-1111-111111111111\"]", new[] { "User.ReadWrite.All", "AdministrativeUnit.ReadWrite.All" } },
        { "\"displayName\": \"P\", \"usageLocation\": \"GB\", \"assignedLicenses\": [ { \"skuId\": \"6fd2c87f-b296-42f0-b197-1e91e994b900\" } ]", new[] { "User.ReadWrite.All", "LicenseAssignment.ReadWrite.All", "LicenseAssignment.Read.All" } },
        { "\"displayName\": \"P\", \"appRoleAssignments\": [ { \"resourceAppId\": \"00000003-0000-0000-c000-000000000000\", \"appRole\": \"User.Read.All\" } ]", new[] { "User.ReadWrite.All", "AppRoleAssignment.ReadWrite.All", "Application.Read.All", "Directory.Read.All" } },
        { "\"displayName\": \"P\", \"directoryRoles\": [ { \"role\": \"Directory Readers\" } ]", new[] { "User.ReadWrite.All", "RoleManagement.ReadWrite.Directory", "Directory.Read.All" } },
        { "\"displayName\": \"P\", \"ownerOf\": [\"11111111-1111-1111-1111-111111111111\"], \"manager\": null", new[] { "User.ReadWrite.All" } },
        { "\"displayName\": \"P\", \"memberOf\": [], \"administrativeUnits\": []", new[] { "User.ReadWrite.All" } },
    };

    [Theory]
    [MemberData(nameof(PermissionCases))]
    public async Task S12_RequiredPermissions_FollowTheDeclaredRelationships(string properties, string[] expected)
    {
        var host = new ProviderHost();
        var plan = await host.ExpandAsync(host.Resources(User("perm@contoso.com", properties)));
        var instance = Assert.Single(plan.Instances);

        var actual = host.Provider<UsersProvider>(Type).RequiredPermissions(instance);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void S12_RequiredPermissions_WithoutAnInstance_IsTheBasePermission()
    {
        Assert.Equal([UsersProvider.ReadWriteAllPermission], new ProviderHost().Provider<UsersProvider>(Type).RequiredPermissions(null));
    }

    [Fact]
    public async Task S13_UserTypeChange_IsAllowedWithAReason_DeclaredUpnMustMatch_DisplayNameIsRequired()
    {
        var host = new ProviderHost();
        host.Directory.AddUser("kim@contoso.com", "Kim", u => u["userType"] = "Member");

        var whatIf = await host.WhatIfAsync(host.Resources(User("kim@contoso.com", "\"displayName\": \"Kim\", \"userType\": \"Guest\"")));
        var change = whatIf.Change("kim@contoso.com");
        Assert.Equal(ChangeType.Modify, change.Type);
        Assert.False(change.HasError);
        Assert.Contains(change.Reasons, r => r.StartsWith("userType changes", StringComparison.Ordinal));

        var literal = await Assert.ThrowsAsync<ValidationFailedException>(() => host.WhatIfAsync(host.Resources(User("kim@contoso.com", "\"displayName\": \"Kim\", \"userPrincipalName\": \"other@contoso.com\""))));
        Assert.Contains("NameMappedPropertyConflict", literal.Message, StringComparison.Ordinal);

        var conflict = await host.WhatIfAsync(host.Resources(User("kim@contoso.com", "\"displayName\": \"Kim\", \"userPrincipalName\": \"[concat('other@', 'contoso.com')]\"")));
        var conflictChange = conflict.Change("kim@contoso.com");
        Assert.True(conflictChange.HasError);
        Assert.Contains("NameMappedPropertyConflict", conflictChange.UnsupportedReason, StringComparison.Ordinal);

        var equal = await host.WhatIfAsync(host.Resources(User("kim@contoso.com", "\"displayName\": \"Kim\", \"userPrincipalName\": \"kim@contoso.com\"")));
        Assert.Equal(ChangeType.NoChange, equal.Change("kim@contoso.com").Type);

        var badName = await Assert.ThrowsAsync<ValidationFailedException>(() => host.WhatIfAsync(host.Resources(User("not-a-upn", "\"displayName\": \"X\""))));
        Assert.Contains("InvalidResourceName", badName.Message, StringComparison.Ordinal);

        var missing = await host.WhatIfAsync(host.Resources(User("noname@contoso.com", "\"jobTitle\": \"X\"")));
        var missingChange = missing.Change("noname@contoso.com");
        Assert.True(missingChange.HasError);
        Assert.Contains("displayName", missingChange.UnsupportedReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisabledExistingUser_DeclaredWithEdgesOnly_StaysDisabled_AndGetsItsEdges()
    {
        var host = new ProviderHost();
        string userId = host.Directory.AddUser("dana@contoso.com", "Dana", u => u["accountEnabled"] = false)["id"]!.GetValue<string>();
        string groupId = host.Directory.AddGroup("existing", "Existing")["id"]!.GetValue<string>();
        var document = host.Resources(User("dana@contoso.com", "\"memberOf\": [\"" + groupId + "\"]"));

        var whatIf = await host.WhatIfAsync(document);
        var change = whatIf.Change("dana@contoso.com");
        Assert.False(change.HasError);
        Assert.NotEmpty(change.Deltas);
        Assert.All(change.Deltas, d => Assert.Contains("memberOf", d.Path.ToString(), StringComparison.Ordinal));
        var row = WhatIfJson.Build(whatIf.Plan)["changes"]!.AsArray().Single(c => c!["resourceId"]?.GetValue<string>() == Id("dana@contoso.com"));
        Assert.DoesNotContain("accountEnabled", row!["delta"]?.ToJsonString() ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("\"accountEnabled\":true", row["after"]?.ToJsonString() ?? string.Empty, StringComparison.Ordinal);
        Check.NoWrites(whatIf);

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        Assert.DoesNotContain(run.Writes, w => w.Path.StartsWith("/users", StringComparison.Ordinal));
        Assert.Single(run.Writes, w => w.Method == "POST" && w.Path == $"/groups/{groupId}/members/$ref");
        Assert.Contains(userId, host.Directory.References($"/groups/{groupId}/members"));
        Assert.False(host.Directory.Find("/users", "userPrincipalName", "dana@contoso.com")!["accountEnabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task DisabledExistingUser_WithOtherDeclaredChanges_PatchesOnlyTheDeclaredProperties()
    {
        var host = new ProviderHost();
        host.Directory.AddUser("erin@contoso.com", "Erin", u => u["accountEnabled"] = false);
        var document = host.Resources(User("erin@contoso.com", "\"displayName\": \"Erin\", \"jobTitle\": \"Ops\""));

        var whatIf = await host.WhatIfAsync(document);
        var change = whatIf.Change("erin@contoso.com");
        Assert.Equal(ChangeType.Modify, change.Type);
        Assert.DoesNotContain(change.Deltas, d => d.Path.ToString().Contains("accountEnabled", StringComparison.Ordinal));

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        var patch = Assert.Single(run.Writes, w => w.Method == "PATCH");
        Assert.Equal("""{"jobTitle":"Ops"}""", Check.Body(patch).ToJsonString());
        Assert.False(host.Directory.Find("/users", "userPrincipalName", "erin@contoso.com")!["accountEnabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ExistingUser_DeclaringAccountEnabled_IsStillCorrected()
    {
        var host = new ProviderHost();
        host.Directory.AddUser("finn@contoso.com", "Finn", u => u["accountEnabled"] = false);
        var document = host.Resources(User("finn@contoso.com", "\"displayName\": \"Finn\", \"accountEnabled\": true"));

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        var patch = Assert.Single(run.Writes, w => w.Method == "PATCH");
        Assert.Equal("""{"accountEnabled":true}""", Check.Body(patch).ToJsonString());
    }
}
