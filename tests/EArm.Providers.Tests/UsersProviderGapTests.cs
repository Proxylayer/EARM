using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Core;
using EArm.Core.Secrets;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Expressions.Values;
using EArm.Providers.Tests.Infrastructure;
using EArm.TestSupport;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>Users provider branches beyond the lifecycle tests: permissions, key and name errors, licence shape errors, a refused soft-deleted probe, the password profile shapes and the delete race.</summary>
public sealed class UsersProviderGapTests
{
    private const string Type = UsersProvider.ResourceType;
    private const string SomeId = "6d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0001";

    private static string User(string name = "alice@contoso.com", string properties = "{ \"displayName\": \"Alice\" }")
    {
        return Templates.Resource(Type, name, properties);
    }

    private static Task<ResourceInstance> Instance(ProviderHost host, string properties, string? name = null)
    {
        return InstanceFactory.WithPropertiesAsync(host, User(), properties, name);
    }

    // ---- permissions ----

    [Theory]
    [InlineData("{ \"displayName\": \"A\" }", new[] { "User.ReadWrite.All" })]
    [InlineData("{ \"displayName\": \"A\", \"memberOf\": [\"" + SomeId + "\"] }", new[] { "User.ReadWrite.All", "GroupMember.ReadWrite.All" })]
    [InlineData("{ \"displayName\": \"A\", \"administrativeUnits\": [\"" + SomeId + "\"] }", new[] { "User.ReadWrite.All", "AdministrativeUnit.ReadWrite.All" })]
    public async Task RequiredPermissions_AddTheConditionalEntriesOfTheDeclaredEdgeProperties(string properties, string[] expected)
    {
        var host = new ProviderHost();
        var provider = host.Provider(Type);
        var instance = await Instance(host, properties);
        instance.EdgeProperties = (JsonObject)JsonNode.Parse(properties)!;

        var permissions = provider.RequiredPermissions(instance);

        foreach (string permission in expected)
        {
            Assert.Contains(permission, permissions);
        }

        Assert.Contains("User.ReadWrite.All", provider.RequiredPermissions(null));
    }

    // ---- read ----

    [Fact]
    public async Task Read_WithoutAUserPrincipalName_IsAKeyError_AndAnUnknownUserIsNull()
    {
        var host = new ProviderHost();
        var provider = host.Provider(Type);

        var noKey = await Assert.ThrowsAsync<ProviderException>(() => provider.ReadAsync(new ResourceKey { Type = Type, Values = new Dictionary<string, string>() }, host.Fake, ProviderHost.Ct));
        var missing = await provider.ReadAsync(new ResourceKey { Type = Type, Values = new Dictionary<string, string> { ["userPrincipalName"] = "ghost@contoso.com" } }, host.Fake, ProviderHost.Ct);

        Assert.Equal(EngineDiagnosticCodes.ResourceKeyUnresolved, noKey.Code);
        Assert.Contains("carries no userPrincipalName", noKey.Message, StringComparison.Ordinal);
        Assert.Null(missing);
    }

    // ---- plan ----

    [Fact]
    public async Task APlanForAnIgnoredInstance_IsAnIgnoreRow_AndABadUserPrincipalNameIsAnUnsupportedRow()
    {
        var host = new ProviderHost();
        var provider = host.Provider(Type);
        var ignored = await Instance(host, "{ \"displayName\": \"A\" }");
        ignored.Condition = false;
        var badName = await Instance(host, "{ \"displayName\": \"A\" }", name: "no-at-sign");
        var spaced = await Instance(host, "{ \"displayName\": \"A\" }", name: "a b@contoso.com");

        Assert.Equal(ChangeType.Ignore, provider.Plan(ignored, null, PlanOptions.Default).Type);
        var shape = provider.Plan(badName, null, PlanOptions.Default);
        Assert.True(shape.HasError);
        Assert.Contains("userPrincipalName of the form local@domain", shape.UnsupportedReason, StringComparison.Ordinal);
        Assert.Contains("whitespace", provider.Plan(spaced, null, PlanOptions.Default).UnsupportedReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARenamedLiveUser_IsAnImmutableChange_AndALiveUserWithADifferentCaseIsTheSameUser()
    {
        var host = new ProviderHost();
        host.Directory.AddUser("alice@contoso.com", "Alice");
        var provider = host.Provider<UsersProvider>(Type);
        var key = new ResourceKey { Type = Type, Values = new Dictionary<string, string> { ["userPrincipalName"] = "alice@contoso.com" } };
        var existing = (await provider.ReadAsync(key, host.Fake, ProviderHost.Ct))!;
        var renamedRaw = (JsonObject)existing.Raw.DeepClone();
        renamedRaw["userPrincipalName"] = "alicia@contoso.com";
        var renamed = existing with { Raw = renamedRaw };
        var instance = await Instance(host, "{ \"displayName\": \"Alice\" }", name: "alice@contoso.com");
        var upper = await Instance(host, "{ \"displayName\": \"Alice\" }", name: "ALICE@CONTOSO.COM");

        var refused = provider.Plan(instance, renamed, PlanOptions.Default);
        var sameUser = provider.Plan(upper, existing, PlanOptions.Default);

        Assert.True(refused.HasError);
        Assert.Contains("userPrincipalName is immutable", refused.UnsupportedReason, StringComparison.Ordinal);
        Assert.False(sameUser.HasError, sameUser.UnsupportedReason);
    }

    [Fact]
    public async Task AssignedLicenses_OfTheWrongShape_AreRowErrors_AndAPlaceholderIsAnUnknownRow()
    {
        var host = new ProviderHost();
        var provider = host.Provider(Type);
        var notArray = await Instance(host, "{ \"displayName\": \"A\" }");
        notArray.EdgeProperties = new JsonObject { ["assignedLicenses"] = "E5" };
        var unknown = await Instance(host, "{ \"displayName\": \"A\" }");
        unknown.EdgeProperties = new JsonObject { ["assignedLicenses"] = UnknownJson.Create("reference('x').skus", UnknownOrigins.Reference) };
        var noLocation = await Instance(host, "{ \"displayName\": \"A\" }");
        noLocation.EdgeProperties = new JsonObject { ["assignedLicenses"] = new JsonArray(new JsonObject { ["skuId"] = SomeId }) };

        var shape = provider.Plan(notArray, null, PlanOptions.Default);
        var pending = provider.Plan(unknown, null, PlanOptions.Default);
        var location = provider.Plan(noLocation, null, PlanOptions.Default);

        Assert.Contains("assignedLicenses must be an array", shape.UnsupportedReason, StringComparison.Ordinal);
        Assert.True(pending.IsUnknown);
        Assert.Contains("only known at deployment time", string.Join(";", pending.Reasons), StringComparison.Ordinal);
        Assert.Contains("requires usageLocation", location.UnsupportedReason, StringComparison.Ordinal);
    }

    // ---- create ----

    [Fact]
    public async Task Create_WithABadNameOrAnUnresolvedPasswordProfile_IsAProviderException_BeforeAnyWrite()
    {
        var host = new ProviderHost();
        var provider = host.Provider(Type);
        var badName = await Instance(host, "{ \"displayName\": \"A\" }", name: "no-at-sign");
        var unresolved = await Instance(host, "{ \"displayName\": \"A\", \"passwordProfile\": \"x\" }");
        unresolved.Properties!["passwordProfile"] = UnknownJson.Create("reference('x').pw", UnknownOrigins.Reference);

        var name = await Assert.ThrowsAsync<ProviderException>(() => provider.CreateAsync(badName, host.Fake, ProviderHost.Ct));
        var profile = await Assert.ThrowsAsync<ProviderException>(() => provider.CreateAsync(unresolved, host.Fake, ProviderHost.Ct));

        Assert.Contains("userPrincipalName of the form local@domain", name.Message, StringComparison.Ordinal);
        Assert.Contains("passwordProfile depends on values that are not available", profile.Message, StringComparison.Ordinal);
        Assert.Empty(host.Fake.Writes);
    }

    [Fact]
    public async Task Create_ARefusedDeletedItemsFilter_FallsBackToListingAndMatchingClientSide()
    {
        var host = new ProviderHost();
        host.Fake.Failures.Add("GET", "/directory/deletedItems", request => request.UrlParameters is { } p && p.ContainsKey("$filter")
            ? new GraphException(HttpStatusCode.BadRequest, "Request_UnsupportedQuery", "filter not supported", request.Hint)
            : null!, times: 1);

        var run = await host.CreateAsync(host.Resources(User()));

        Check.Succeeded(run);
        Assert.NotNull(host.Directory.Find("/users", "userPrincipalName", "alice@contoso.com"));
        Assert.Contains(run.Requests, r => r.Path.StartsWith("/directory/deletedItems", StringComparison.Ordinal) && (r.UrlParameters is null || !r.UrlParameters.ContainsKey("$filter")));
    }

    [Fact]
    public async Task Create_WithoutAPasswordGeneratesOne_AndNeverPublishesItAsPlainText()
    {
        var host = new ProviderHost();

        var run = await host.CreateAsync(host.Resources(User()));

        Check.Succeeded(run);
        var post = Assert.Single(run.Writes, w => w.Method == "POST" && w.Path == "/users");
        string generated = Check.Body(post)["passwordProfile"]!["password"]!.GetValue<string>();
        Assert.True(generated.Length >= UsersRules.GeneratedPasswordLength);
        Assert.DoesNotContain(generated, host.StateDocument!, StringComparison.Ordinal);
        Assert.DoesNotContain(host.Logs.Lines, l => l.Contains(generated, StringComparison.Ordinal));
        Assert.DoesNotContain(generated, run.WhatIfJsonText, StringComparison.Ordinal);
        Assert.DoesNotContain(generated, run.WhatIfTextOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_ADeclaredPassword_IsSentOnce_AndLeavesNoTraceAnywhereElse()
    {
        var host = new ProviderHost();
        const string secret = "Decl@red-Pass-7731";

        var run = await host.CreateAsync(host.Resources(User(properties: "{ \"displayName\": \"Alice\", \"passwordProfile\": { \"password\": \"" + secret + "\", \"forceChangePasswordNextSignIn\": false } }")));

        Check.Succeeded(run);
        Assert.Single(run.Writes, w => w.Body is not null && w.Body.ToJsonString().Contains(secret, StringComparison.Ordinal));
        Assert.DoesNotContain(secret, host.StateDocument!, StringComparison.Ordinal);
        Assert.DoesNotContain(host.Logs.Lines, l => l.Contains(secret, StringComparison.Ordinal));
        Assert.DoesNotContain(secret, run.WhatIfJsonText + run.WhatIfTextOutput + run.Errors, StringComparison.Ordinal);
    }

    // ---- delete ----

    [Fact]
    public async Task Delete_ToleratesAUserThatIsAlreadyGone_AndPropagatesOtherErrors()
    {
        var host = new ProviderHost();
        Check.Succeeded(await host.CreateAsync(host.Resources(User())));
        var state = (await host.LoadStateAsync())!;
        var entry = state.Resources.Values.Single();
        var provider = host.Provider(Type);
        host.Fake.Failures.Add("DELETE", "/users/", request => new GraphException(HttpStatusCode.NotFound, "Request_ResourceNotFound", "gone", request.Hint), times: 1);

        await provider.DeleteAsync(DeleteTarget.KeyOf(entry), DeleteTarget.LiveOf(entry), host.Fake, ProviderHost.Ct);

        host.Fake.Failures.Forbid("/users/", "DELETE");
        var denied = await Assert.ThrowsAsync<GraphException>(() => provider.DeleteAsync(DeleteTarget.KeyOf(entry), DeleteTarget.LiveOf(entry), host.Fake, ProviderHost.Ct));
        Assert.True(denied.IsForbidden);
    }

    [Fact]
    public void PlanOptions_CapturedByPlan_AreWhatCreateSees()
    {
        var host = new ProviderHost();
        var provider = host.Provider<UsersProvider>(Type);
        var options = PlanOptions.Default with { AllowCaEnforce = true };

        provider.PlanOptions = options;

        Assert.Same(options, provider.PlanOptions);
        Assert.NotEmpty(provider.RequiredPermissions(null));
    }
}
