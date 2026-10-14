using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EntraArm.Az;
using EntraArm.Core;
using EntraArm.Engine;
using EntraArm.Engine.Providers;
using EntraArm.Engine.State;
using EntraArm.Providers.Tests.Infrastructure;
using Xunit;

namespace EntraArm.Providers.Tests;

/// <summary>
/// Authentication context class references (<c>c1</c>..<c>c99</c>, the name is the Graph id): key and name errors, the invalid-shape
/// rows, the upsert that answers without a body, the update that has nothing to send, and the delete races.
/// </summary>
public sealed class AuthenticationContextGapTests
{
    private const string Type = AuthenticationContextClassReferencesProvider.ResourceType;
    private const string Path = "/identity/conditionalAccess/authenticationContextClassReferences";

    private static string Context(string name, string properties)
    {
        return Templates.Resource(Type, name, properties);
    }

    private static ResourceKey Key(params (string Name, string Value)[] values)
    {
        return new ResourceKey { Type = Type, Values = values.ToDictionary(v => v.Name, v => v.Value) };
    }

    private static StateEntry Entry(string id)
    {
        return new StateEntry { Id = ResourceIdentifier.Create(Type, id), Type = Type, ApiVersion = ProviderHost.ApiVersion, GraphId = id, Key = new Dictionary<string, string> { ["id"] = id } };
    }

    // ---- read ----

    [Fact]
    public async Task Read_WithoutAnIdOrWithABadId_IsAKeyError_AndAMissingContextIsNull()
    {
        var host = new ProviderHost();
        var provider = host.Provider<AuthenticationContextClassReferencesProvider>(Type);

        var noKey = await Assert.ThrowsAsync<ProviderException>(() => provider.ReadAsync(Key(), null, host.Fake, ProviderHost.Ct));
        var badKey = await Assert.ThrowsAsync<ProviderException>(() => provider.ReadAsync(Key(("id", "c100")), null, host.Fake, ProviderHost.Ct));
        var missing = await provider.ReadAsync(Key(("id", "c7")), null, host.Fake, ProviderHost.Ct);

        Assert.Equal(EngineDiagnosticCodes.ResourceKeyUnresolved, noKey.Code);
        Assert.Contains("carries no id", noKey.Message, StringComparison.Ordinal);
        Assert.Equal(EngineDiagnosticCodes.ResourceKeyUnresolved, badKey.Code);
        Assert.Contains("c100", badKey.Message, StringComparison.Ordinal);
        Assert.Null(missing);
    }

    // ---- plan ----

    [Theory]
    [InlineData("c0", "{ \"displayName\": \"x\" }", "not a valid authentication context class reference id")]
    [InlineData("c100", "{ \"displayName\": \"x\" }", "not a valid authentication context class reference id")]
    [InlineData("C5", "{ \"displayName\": \"x\" }", "not a valid authentication context class reference id")]
    [InlineData("c5", "{ \"description\": \"no display name\" }", "displayName is required")]
    [InlineData("c5", "{ \"displayName\": \"x\", \"isAvailable\": \"yes\" }", "isAvailable must be a boolean")]
    [InlineData("c5", "{ \"displayName\": \"x\", \"description\": 5 }", "description must be a string")]
    [InlineData("c5", "{ \"displayName\": 5 }", "displayName must be a string")]
    public async Task InvalidDeclarations_AreUnsupportedRows_AndNeverReachGraph(string name, string properties, string expected)
    {
        var host = new ProviderHost();
        var provider = host.Provider<AuthenticationContextClassReferencesProvider>(Type);
        var instance = await InstanceFactory.WithPropertiesAsync(host, Context("c1", "{ \"displayName\": \"valid\" }"), properties, name);

        var change = provider.Plan(instance, null, PlanOptions.Default);

        Assert.True(change.HasError);
        Assert.Contains(expected, change.UnsupportedReason, StringComparison.Ordinal);
        Assert.Empty(host.Fake.Writes);
    }

    [Fact]
    public async Task APlanWithAnExistingContext_ThatIsInvalid_IsAModifyRowWithTheError()
    {
        var host = new ProviderHost();
        host.Directory.Add(Path, new JsonObject { ["id"] = "c5", ["displayName"] = "old", ["isAvailable"] = true });
        var provider = host.Provider<AuthenticationContextClassReferencesProvider>(Type);
        var existing = await provider.ReadAsync(Key(("id", "c5")), null, host.Fake, ProviderHost.Ct);
        var instance = await InstanceFactory.WithPropertiesAsync(host, Context("c1", "{ \"displayName\": \"valid\" }"), "{ \"description\": \"missing the name\" }", "c5");

        var change = provider.Plan(instance, existing, PlanOptions.Default);

        Assert.True(change.HasError);
        Assert.Equal(ChangeType.Modify, change.Type);
        Assert.Equal("c5", change.GraphId);
    }

    // ---- create / update ----

    [Fact]
    public async Task Create_WithInvalidProperties_IsAProviderException_BeforeAnyWrite()
    {
        var host = new ProviderHost();
        var provider = host.Provider<AuthenticationContextClassReferencesProvider>(Type);
        var instance = await InstanceFactory.WithPropertiesAsync(host, Context("c1", "{ \"displayName\": \"valid\" }"), "{ \"displayName\": \"x\", \"isAvailable\": \"no\" }");

        var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.CreateAsync(instance, host.Fake, ProviderHost.Ct));

        Assert.Equal(EngineDiagnosticCodes.ResourceApplyFailed, ex.Code);
        Assert.Contains("isAvailable must be a boolean", ex.Message, StringComparison.Ordinal);
        Assert.Empty(host.Fake.Writes);
    }

    [Fact]
    public async Task Create_WhenTheUpsertAnswersWithoutABody_ReadsTheContextBack_OrSurfacesTheReplicationLag404()
    {
        var host = new ProviderHost();
        var provider = host.Provider<AuthenticationContextClassReferencesProvider>(Type);
        var instance = await InstanceFactory.WithPropertiesAsync(host, Context("c1", "{ \"displayName\": \"valid\" }"), "{ \"displayName\": \"Sensitive\" }", "c9");

        var applied = await provider.CreateAsync(instance, new InterceptingGraphClient(host.Fake) { DropPatchBodies = true }, ProviderHost.Ct);

        Assert.Equal("c9", applied.GraphId);
        Assert.Equal("Sensitive", host.Directory.Find(Path, "id", "c9")!["displayName"]!.GetValue<string>());

        var other = await InstanceFactory.WithPropertiesAsync(host, Context("c1", "{ \"displayName\": \"valid\" }"), "{ \"displayName\": \"Never stored\" }", "c10");
        var vanished = await Assert.ThrowsAsync<GraphException>(() => provider.CreateAsync(other, new InterceptingGraphClient(host.Fake) { PatchOverride = (_, _) => new GraphResponse(HttpStatusCode.NoContent, null) }, ProviderHost.Ct));
        Assert.True(vanished.IsNotFound && vanished.IsReplication, "the read-back runs under the replication-lag hint: the policy retries it, then the 404 surfaces");
    }

    [Fact]
    public async Task Update_WithNothingToSend_ReturnsTheLiveObject_AndAVanishedContextIsAnError()
    {
        var host = new ProviderHost();
        host.Directory.Add(Path, new JsonObject { ["id"] = "c4", ["displayName"] = "Same", ["isAvailable"] = true });
        var provider = host.Provider<AuthenticationContextClassReferencesProvider>(Type);
        var existing = (await provider.ReadAsync(Key(("id", "c4")), null, host.Fake, ProviderHost.Ct))!;
        var same = await InstanceFactory.WithPropertiesAsync(host, Context("c1", "{ \"displayName\": \"valid\" }"), "{ \"displayName\": \"Same\", \"isAvailable\": true }", "c4");
        var change = provider.Plan(same, existing, PlanOptions.Default);

        var unchanged = await provider.UpdateAsync(same, existing, change, host.Fake, ProviderHost.Ct);

        Assert.Equal("c4", unchanged.GraphId);
        Assert.Empty(host.Fake.Writes);

        var renamed = await InstanceFactory.WithPropertiesAsync(host, Context("c1", "{ \"displayName\": \"valid\" }"), "{ \"displayName\": \"Renamed\", \"isAvailable\": true }", "c4");
        var renameChange = provider.Plan(renamed, existing, PlanOptions.Default);
        host.Fake.Failures.Add("GET", Path + "/c4", request => new GraphException(HttpStatusCode.NotFound, "Request_ResourceNotFound", "gone", request.Hint));
        var gone = await Assert.ThrowsAsync<ProviderException>(() => provider.UpdateAsync(renamed, existing, renameChange, host.Fake, ProviderHost.Ct));
        Assert.Contains("vanished after the update", gone.Message, StringComparison.Ordinal);
    }

    // ---- delete ----

    [Fact]
    public async Task Delete_ToleratesAContextThatDisappearsBetweenTheReadAndTheDelete_AndPropagatesOtherErrors()
    {
        var host = new ProviderHost();
        host.Directory.Add(Path, new JsonObject { ["id"] = "c6", ["displayName"] = "x", ["isAvailable"] = false });
        var provider = host.Provider<AuthenticationContextClassReferencesProvider>(Type);
        host.Fake.Failures.Add("DELETE", Path + "/c6", request => new GraphException(HttpStatusCode.NotFound, "Request_ResourceNotFound", "gone", request.Hint));

        await provider.DeleteAsync(Entry("c6"), host.Fake, ProviderHost.Ct);

        host.Directory.Add(Path, new JsonObject { ["id"] = "c8", ["displayName"] = "y", ["isAvailable"] = false });
        host.Fake.Failures.Forbid(Path + "/c8", "DELETE");
        var denied = await Assert.ThrowsAsync<GraphException>(() => provider.DeleteAsync(Entry("c8"), host.Fake, ProviderHost.Ct));
        Assert.True(denied.IsForbidden);
    }

    [Fact]
    public async Task Delete_OfAnAvailableContext_SwitchesItOffFirst_AndAnObjectWithoutTheFlagIsTreatedAsAvailable()
    {
        var host = new ProviderHost();
        host.Directory.Add(Path, new JsonObject { ["id"] = "c2", ["displayName"] = "no flag" });
        var provider = host.Provider<AuthenticationContextClassReferencesProvider>(Type);

        await provider.DeleteAsync(Entry("c2"), host.Fake, ProviderHost.Ct);

        var writes = host.Fake.Writes;
        Assert.Equal(["PATCH", "DELETE"], writes.Select(w => w.Method).ToArray());
        Assert.Null(host.Directory.Find(Path, "id", "c2"));
    }
}
