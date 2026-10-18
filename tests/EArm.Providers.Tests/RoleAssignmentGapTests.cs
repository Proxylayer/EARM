using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Core;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Expressions.Values;
using EArm.Providers.Tests.Infrastructure;
using EArm.TestSupport;
using Xunit;
using static EArm.Providers.Tests.Infrastructure.DirectoryTemplates;

namespace EArm.Providers.Tests;

/// <summary>
/// <c>Microsoft.Entra/roleAssignments</c> branches the S1–S10 scenarios do not reach: prepare conflicts and
/// placeholders, the pinned-id read, a body-less create, delete tolerance, and the never-edited-in-place update.
/// </summary>
public sealed class RoleAssignmentGapTests
{
    private const string AssignmentType = RoleAssignmentsProvider.ResourceType;
    private const string AssignmentName = "bbbbbbbb-0000-0000-0000-0000000000b1";

    private sealed record Tenant(ProviderHost Host, string UserId, string AuId, RoleAssignmentsProvider Provider);

    private static Tenant NewTenant()
    {
        var host = new ProviderHost();
        BuiltInRoles.Seed(host);
        string userId = host.Directory.AddUser("alice@contoso.com", "Alice")["id"]!.GetValue<string>();
        string auId = host.Directory.Add(BuiltInRoles.UnitsPath, new JsonObject { ["displayName"] = "Orders AU" })["id"]!.GetValue<string>();
        return new Tenant(host, userId, auId, host.Provider<RoleAssignmentsProvider>(AssignmentType));
    }

    private static Task<ResourceInstance> Instance(Tenant t, string propertiesJson, string? name = null)
    {
        string valid = Standalone("\"principalId\": \"" + t.UserId + "\", \"roleDefinitionId\": \"" + BuiltInRoles.GlobalReader + "\"", AssignmentName);
        return InstanceFactory.WithPropertiesAsync(t.Host, valid, propertiesJson, name);
    }

    private static ResourceKey Key(Tenant t, string scope = "/")
    {
        return new ResourceKey
        {
            Type = AssignmentType,
            Values = new Dictionary<string, string> { ["roleDefinitionId"] = BuiltInRoles.GlobalReader, ["principalId"] = t.UserId, ["directoryScopeId"] = scope },
        };
    }

    // ---- prepare ----

    [Theory]
    [InlineData("{ \"role\": \"Global Reader\", \"roleDefinitionId\": \"" + BuiltInRoles.SecurityReader + "\", \"principalId\": \"USER\" }", "differs from the declared roleDefinitionId")]
    [InlineData("{ \"role\": \"No Such Role\", \"principalId\": \"USER\" }", "No Such Role")]
    [InlineData("{ \"role\": \"Global Reader\", \"administrativeUnit\": \"No Such AU\", \"principalId\": \"USER\" }", "No Such AU")]
    [InlineData("{ \"role\": \"Global Reader\", \"administrativeUnit\": \"Orders AU\", \"directoryScopeId\": \"/administrativeUnits/00000000-0000-0000-0000-000000000001\", \"principalId\": \"USER\" }", "differs from the declared directoryScopeId")]
    [InlineData("{ \"role\": \"Global Reader\", \"principalId\": \"00000000-0000-0000-0000-0000000000ff\" }", "00000000-0000-0000-0000-0000000000ff")]
    [InlineData("{ \"principalId\": \"USER\" }", "needs the role")]
    [InlineData("{ \"role\": \"Global Reader\", \"principalId\": \"USER\", \"extra\": 1 }", "is not a property of a directory role assignment")]
    [InlineData("{ \"role\": \"Global Reader\", \"principalId\": 7 }", "must be a non-empty string")]
    public async Task Prepare_RejectsInvalidDeclarations_WithARoleAssignmentInvalidProviderException(string properties, string expected)
    {
        var t = NewTenant();
        var instance = await Instance(t, properties.Replace("USER", t.UserId, StringComparison.Ordinal));

        var ex = await Assert.ThrowsAsync<ProviderException>(() => t.Provider.PrepareAsync(instance, t.Host.Fake, ProviderHost.Ct));

        Assert.Equal(RoleAssignmentsProvider.InvalidAssignment, ex.Code);
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prepare_ResolvesSugar_NormalisesIds_AndDefaultsTheScopeToTheRoot()
    {
        var t = NewTenant();
        var sugar = await Instance(t, "{ \"role\": \"Global Reader\", \"administrativeUnit\": \"Orders AU\", \"principalId\": \"" + t.UserId.ToUpperInvariant() + "\" }");
        await t.Provider.PrepareAsync(sugar, t.Host.Fake, ProviderHost.Ct);

        Assert.Equal(BuiltInRoles.GlobalReader, sugar.Properties!["roleDefinitionId"]!.GetValue<string>());
        Assert.Equal("/administrativeUnits/" + t.AuId, sugar.Properties["directoryScopeId"]!.GetValue<string>());
        Assert.Equal(t.UserId, sugar.Properties["principalId"]!.GetValue<string>());
        Assert.False(sugar.Properties.ContainsKey("role"));
        Assert.False(sugar.Properties.ContainsKey("administrativeUnit"));

        var canonical = await Instance(t, "{ \"roleDefinitionId\": \"" + BuiltInRoles.GlobalReader.ToUpperInvariant() + "\", \"principalId\": \"" + t.UserId + "\" }");
        await t.Provider.PrepareAsync(canonical, t.Host.Fake, ProviderHost.Ct);
        Assert.Equal(BuiltInRoles.GlobalReader, canonical.Properties!["roleDefinitionId"]!.GetValue<string>());
        Assert.Equal("/", canonical.Properties["directoryScopeId"]!.GetValue<string>());

        var scoped = await Instance(t, "{ \"roleDefinitionId\": \"" + BuiltInRoles.GlobalReader + "\", \"principalId\": \"" + t.UserId + "\", \"directoryScopeId\": \"/administrativeUnits/" + t.AuId.ToUpperInvariant() + "\" }");
        await t.Provider.PrepareAsync(scoped, t.Host.Fake, ProviderHost.Ct);
        Assert.Equal("/administrativeUnits/" + t.AuId, scoped.Properties!["directoryScopeId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Prepare_DeployTimePlaceholders_MoveToTheCanonicalProperty()
    {
        var t = NewTenant();
        var unknownRole = await Instance(t, "{ \"principalId\": \"" + t.UserId + "\" }");
        unknownRole.Properties!["role"] = UnknownJson.Create("reference('custom').id", UnknownOrigins.Reference);
        await t.Provider.PrepareAsync(unknownRole, t.Host.Fake, ProviderHost.Ct);
        Assert.True(UnknownJson.IsUnknown(unknownRole.Properties["roleDefinitionId"]));
        Assert.False(unknownRole.Properties.ContainsKey("role"));

        var unknownUnit = await Instance(t, "{ \"roleDefinitionId\": \"" + BuiltInRoles.GlobalReader + "\", \"principalId\": \"" + t.UserId + "\" }");
        unknownUnit.Properties!["administrativeUnit"] = UnknownJson.Create("reference('au').id", UnknownOrigins.Reference);
        await t.Provider.PrepareAsync(unknownUnit, t.Host.Fake, ProviderHost.Ct);
        Assert.True(UnknownJson.IsUnknown(unknownUnit.Properties["directoryScopeId"]));
        Assert.False(unknownUnit.Properties.ContainsKey("administrativeUnit"));
    }

    [Fact]
    public async Task Prepare_NullArguments_AndUnevaluatedInstances_AreGuarded()
    {
        var t = NewTenant();
        var instance = await Instance(t, "{ }");
        instance.Properties = null;

        await Assert.ThrowsAsync<ArgumentNullException>(() => t.Provider.PrepareAsync(null!, t.Host.Fake, ProviderHost.Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => t.Provider.PrepareAsync(instance, null!, ProviderHost.Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => t.Provider.PrepareAsync(instance, t.Host.Fake, ProviderHost.Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => t.Provider.CreateAsync(instance, t.Host.Fake, ProviderHost.Ct));
    }

    // ---- plan ----

    [Fact]
    public async Task Plan_GlobalAdministratorOnAnUpdate_AndTheUnsupportedShapes_AreReported()
    {
        var t = NewTenant();
        var instance = await Instance(t, "{ \"roleDefinitionId\": \"" + BuiltInRoles.GlobalAdministrator + "\", \"principalId\": \"" + t.UserId + "\", \"directoryScopeId\": \"/\" }");

        var change = t.Provider.Plan(instance, null, PlanOptions.Default);
        Assert.Contains(RoleAssignmentsRules.GlobalAdministratorReason, change.Reasons);

        var badName = await Instance(t, "{ \"roleDefinitionId\": \"" + BuiltInRoles.GlobalReader + "\", \"principalId\": \"" + t.UserId + "\" }", name: "not-a-guid");
        Assert.Contains("is not a GUID", t.Provider.Plan(badName, null, PlanOptions.Default).UnsupportedReason, StringComparison.Ordinal);

        var badProperty = await Instance(t, "{ \"roleDefinitionId\": \"" + BuiltInRoles.GlobalReader + "\", \"principalId\": \"" + t.UserId + "\", \"colour\": \"red\" }");
        Assert.Contains("is not a property of a directory role assignment", t.Provider.Plan(badProperty, null, PlanOptions.Default).UnsupportedReason, StringComparison.Ordinal);
    }

    // ---- read ----

    [Fact]
    public async Task Read_FindsTheAssignmentByComposite_AndTheViewHasACanonicalScope()
    {
        var t = NewTenant();
        var live = t.Host.Directory.Add(BuiltInRoles.AssignmentsPath, new JsonObject { ["roleDefinitionId"] = BuiltInRoles.GlobalReader, ["principalId"] = t.UserId, ["directoryScopeId"] = "/" });
        string id = live["id"]!.GetValue<string>();

        var found = await t.Provider.ReadAsync(Key(t), t.Host.Fake, ProviderHost.Ct);
        var missing = await t.Provider.ReadAsync(Key(t, "/administrativeUnits/" + t.AuId), t.Host.Fake, ProviderHost.Ct);

        Assert.Equal(id, found!.GraphId);
        Assert.Equal("/", found.Properties["directoryScopeId"]!.GetValue<string>());
        Assert.False(found.Properties.ContainsKey("@odata.type"));
        Assert.Null(missing);
    }

    [Fact]
    public async Task Read_AKeyWithoutRoleOrPrincipal_Throws()
    {
        var t = NewTenant();
        var key = new ResourceKey { Type = AssignmentType, Values = new Dictionary<string, string> { ["principalId"] = t.UserId } };

        var ex = await Assert.ThrowsAsync<ProviderException>(() => t.Provider.ReadAsync(key, t.Host.Fake, ProviderHost.Ct));

        Assert.Equal(EngineDiagnosticCodes.ResourceKeyUnresolved, ex.Code);
    }

    // ---- create ----

    [Fact]
    public async Task Create_AlreadyExistsButAbsent_Throws_AndABodylessCreateIsReadBack()
    {
        var t = NewTenant();
        var instance = await Instance(t, "{ \"roleDefinitionId\": \"" + BuiltInRoles.GlobalReader + "\", \"principalId\": \"" + t.UserId + "\", \"directoryScopeId\": \"/\" }");
        t.Host.Fake.Failures.Add("POST", BuiltInRoles.AssignmentsPath, request => new GraphException(HttpStatusCode.BadRequest, "Request_BadRequest", "A conflicting object with one or more of the specified property values is present in the directory.", request.Hint));

        var ex = await Assert.ThrowsAsync<ProviderException>(() => t.Provider.CreateAsync(instance, t.Host.Fake, ProviderHost.Ct));
        Assert.Contains("could not be read back", ex.Message, StringComparison.Ordinal);

        var bodyless = new BodylessPostClient(t.Host.Fake);
        var applied = await t.Provider.CreateAsync(instance, bodyless, ProviderHost.Ct);
        Assert.Equal(t.Host.Directory.Objects(BuiltInRoles.AssignmentsPath).Single()["id"]!.GetValue<string>(), applied.GraphId);
        Assert.Equal(BuiltInRoles.GlobalReader, applied.Extra!["roleDefinitionId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Create_ABodylessCreate_WhoseAssignmentCannotBeFound_Throws()
    {
        var t = NewTenant();
        var instance = await Instance(t, "{ \"roleDefinitionId\": \"" + BuiltInRoles.GlobalReader + "\", \"principalId\": \"" + t.UserId + "\", \"directoryScopeId\": \"/\" }");
        var lost = new BodylessPostClient(t.Host.Fake, createIt: false);

        var ex = await Assert.ThrowsAsync<ProviderException>(() => t.Provider.CreateAsync(instance, lost, ProviderHost.Ct));

        Assert.Contains("returned no object", ex.Message, StringComparison.Ordinal);
    }

    // ---- update / delete ----

    [Fact]
    public async Task Update_IsNeverSupported()
    {
        var t = NewTenant();
        var instance = await Instance(t, "{ \"roleDefinitionId\": \"" + BuiltInRoles.GlobalReader + "\", \"principalId\": \"" + t.UserId + "\" }");

        var ex = await Assert.ThrowsAsync<ProviderException>(() => t.Provider.UpdateAsync(instance, null!, null!, t.Host.Fake, ProviderHost.Ct));

        Assert.Contains("never edited in place", ex.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentNullException>(() => t.Provider.UpdateAsync(null!, null!, null!, t.Host.Fake, ProviderHost.Ct));
    }

    [Fact]
    public async Task Delete_ToleratesAMissingAssignment_AndPropagatesOtherErrors()
    {
        var t = NewTenant();
        var entry = new DeleteTarget
        {
            Id = ResourceIdentifier.Create(AssignmentType, AssignmentName),
            Type = AssignmentType,
            ApiVersion = ProviderHost.ApiVersion,
            GraphId = Guid.NewGuid().ToString("D"),
        };

        await t.Provider.DeleteAsync(entry.ResourceKey, entry.Live, t.Host.Fake, ProviderHost.Ct);

        t.Host.Fake.Failures.Forbid(BuiltInRoles.AssignmentsPath, "DELETE");
        var ex = await Assert.ThrowsAsync<GraphException>(() => t.Provider.DeleteAsync(entry.ResourceKey, entry.Live, t.Host.Fake, ProviderHost.Ct));
        Assert.True(ex.IsForbidden);
        await Assert.ThrowsAsync<ArgumentNullException>(() => t.Provider.DeleteAsync(null!, null!, t.Host.Fake, ProviderHost.Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => t.Provider.DeleteAsync(entry.ResourceKey, entry.Live, null!, ProviderHost.Ct));
    }
}
