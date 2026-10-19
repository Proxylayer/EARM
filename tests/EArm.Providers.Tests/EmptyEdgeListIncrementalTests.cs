using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EArm.Core.Diagnostics;
using EArm.Engine;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Engine.WhatIf;
using EArm.Providers.Tests.Infrastructure;
using EArm.TestSupport;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// An empty target-side <c>members</c> / <c>owners</c> list (a literal <c>[]</c>, a parameter defaulting to one, or an expression evaluating to one)
/// declares nothing in Incremental mode: no entry is added or removed and one <c>EmptyEdgeListIgnored</c> info says so. Complete mode keeps emptying the
/// set, a non-empty list stays authoritative in both modes, and a principal-side list (<c>memberOf</c>) is never authoritative.
/// </summary>
public sealed class EmptyEdgeListIncrementalTests
{
    private const string GroupType = "Microsoft.Entra/groups";
    private const string GroupName = "ops";
    private const string ArrayParameter = "\"list\": { \"type\": \"array\", \"defaultValue\": [] }";

    public static TheoryData<string, string?> EmptyLists => new()
    {
        { "[]", null },
        { "\"[parameters('list')]\"", ArrayParameter },
        { "\"[json('[]')]\"", null },
    };

    private static string Ops(string? members = null, string? owners = null)
    {
        return Templates.Resource(GroupType, GroupName, "{ \"displayName\": \"Ops\""
            + (members is null ? string.Empty : ", \"members\": " + members)
            + (owners is null ? string.Empty : ", \"owners\": " + owners) + " }");
    }

    private static string App(string owners)
    {
        return Templates.Resource("Microsoft.Entra/applications", "owned-app", "{ \"displayName\": \"Owned App\", \"owners\": " + owners + " }");
    }

    private static string AppWithSp(string owners)
    {
        const string app = "{ \"type\": \"Microsoft.Entra/applications\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"sp-app\", \"properties\": { \"displayName\": \"SP App\" } }";
        return app + ", { \"type\": \"Microsoft.Entra/servicePrincipals\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"[reference('sp-app').appId]\", \"properties\": { \"owners\": " + owners + " }, \"dependsOn\": [\"[resourceId('Microsoft.Entra/applications', 'sp-app')]\"] }";
    }

    private static string Au(string members)
    {
        return DirectoryTemplates.Au(props: "\"description\": \"Scoped admin for Orders\", \"members\": " + members);
    }

    private static string List(params string[] ids)
    {
        return "[" + string.Join(", ", ids.Select(id => "\"" + id + "\"")) + "]";
    }

    private static IReadOnlyList<FakeGraphRequest> EdgeWrites(Run run)
    {
        return run.Writes.Where(w => w.Path.Contains("$ref", StringComparison.Ordinal)).ToList();
    }

    private static IReadOnlyList<Diagnostic> Ignored(IEnumerable<Diagnostic> diagnostics)
    {
        return diagnostics.Where(d => d.Code == EngineDiagnosticCodes.EmptyEdgeListIgnored).ToList();
    }

    private static PropertyDelta? Delta(ResourceChange change, string property)
    {
        return change.Deltas.SingleOrDefault(d => d.Path.ToString() == "properties." + property);
    }

    private static IReadOnlyList<string> Sorted(IEnumerable<string> ids)
    {
        return ids.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private sealed class Tenant
    {
        public Tenant()
        {
            AliceId = Host.Directory.AddUser("alice@contoso.com", "Alice")["id"]!.GetValue<string>();
            BobId = Host.Directory.AddUser("bob@contoso.com", "Bob")["id"]!.GetValue<string>();
            CarolId = Host.Directory.AddUser("carol@contoso.com", "Carol")["id"]!.GetValue<string>();
        }

        public ProviderHost Host { get; } = new();

        public string AliceId { get; }

        public string BobId { get; }

        public string CarolId { get; }

        public string GroupId => Host.IdOf("/groups", "uniqueName", GroupName);

        public IReadOnlyList<string> Members => Host.Directory.References($"/groups/{GroupId}/members").ToList();

        public IReadOnlyList<string> Owners => Host.Directory.References($"/groups/{GroupId}/owners").ToList();

        public async Task SeedAsync(string resources)
        {
            Check.Succeeded(await CreateAsync(resources));
        }

        public Task<Run> WhatIfAsync(string resources, string? parameters = null, DeploymentMode mode = DeploymentMode.Incremental)
        {
            return Host.WhatIfAsync(Host.Resources(resources, parameters: parameters), mode);
        }

        public Task<Run> CreateAsync(string resources, string? parameters = null, DeploymentMode mode = DeploymentMode.Incremental)
        {
            return Host.CreateAsync(Host.Resources(resources, parameters: parameters), mode);
        }
    }

    // ---- group members: [] in Incremental mode ----

    [Theory]
    [MemberData(nameof(EmptyLists))]
    public async Task EmptyMembers_OnAnExistingGroup_InIncrementalMode_IsNoChange_KeepsTheMembers_AndReportsOneInfo(string members, string? parameters)
    {
        var t = new Tenant();
        await t.SeedAsync(Ops(members: List(t.AliceId, t.BobId)));

        var whatIf = await t.WhatIfAsync(Ops(members: members), parameters);
        var create = await t.CreateAsync(Ops(members: members), parameters);

        Check.NoWrites(whatIf);
        var row = whatIf.Change(GroupName);
        Assert.False(row.HasError, row.UnsupportedReason);
        Assert.Equal(ChangeType.NoChange, row.Type);
        Assert.Null(Delta(row, "members"));
        Assert.False(WhatIfReport.From(whatIf.Plan).HasChanges);
        var info = Assert.Single(Ignored(whatIf.Plan.Diagnostics));
        Assert.Equal(DiagnosticSeverity.Info, info.Severity);
        Assert.Contains("properties.members", info.Message, StringComparison.Ordinal);

        Check.Succeeded(create);
        Assert.Empty(EdgeWrites(create));
        Assert.Single(Ignored(create.Executed.Diagnostics));
        Assert.Equal(Sorted([t.AliceId, t.BobId]), Sorted(t.Members));
    }

    [Fact]
    public async Task EmptyMembers_OnANewGroup_InIncrementalMode_CreatesTheGroup_WithNoMemberWrite()
    {
        var t = new Tenant();

        var create = await t.CreateAsync(Ops(members: "[]"));

        Check.Succeeded(create);
        Assert.Empty(EdgeWrites(create));
        Assert.Empty(t.Members);
        Check.Diagnostic(create, EngineDiagnosticCodes.EmptyEdgeListIgnored);
    }

    // ---- group owners: [] in Incremental mode ----

    [Theory]
    [MemberData(nameof(EmptyLists))]
    public async Task EmptyOwners_OnAnExistingGroup_InIncrementalMode_IsNoError_AndRemovesNoOwner(string owners, string? parameters)
    {
        var t = new Tenant();
        await t.SeedAsync(Ops(owners: List(t.AliceId, t.BobId)));

        var whatIf = await t.WhatIfAsync(Ops(owners: owners), parameters);
        var create = await t.CreateAsync(Ops(owners: owners), parameters);

        var row = whatIf.Change(GroupName);
        Assert.False(row.HasError, row.UnsupportedReason);
        Assert.Null(row.UnsupportedReason);
        Assert.Equal(ChangeType.NoChange, row.Type);
        Assert.Null(Delta(row, "owners"));
        Assert.Single(Ignored(whatIf.Plan.Diagnostics));

        Check.Succeeded(create);
        Assert.Empty(EdgeWrites(create));
        Assert.Equal(Sorted([t.AliceId, t.BobId]), Sorted(t.Owners));
    }

    [Theory]
    [MemberData(nameof(EmptyLists))]
    public async Task EmptyOwners_OnANewGroup_InIncrementalMode_CreatesTheGroupNormally(string owners, string? parameters)
    {
        var t = new Tenant();

        var whatIf = await t.WhatIfAsync(Ops(owners: owners), parameters);
        var create = await t.CreateAsync(Ops(owners: owners), parameters);

        var row = whatIf.Change(GroupName);
        Assert.False(row.HasError, row.UnsupportedReason);
        Assert.Equal(ChangeType.Create, row.Type);

        Check.Succeeded(create);
        Assert.NotNull(t.Host.Directory.Find("/groups", "uniqueName", GroupName));
        Assert.Empty(EdgeWrites(create));
        Assert.Empty(t.Owners);
    }

    // ---- application / service principal owners and AU members: [] in Incremental mode ----

    [Fact]
    public async Task EmptyOwners_OnAnApplication_InIncrementalMode_RemovesNoOwner()
    {
        var t = new Tenant();
        await t.SeedAsync(App(List(t.AliceId, t.BobId)));
        string appId = t.Host.IdOf("/applications", "uniqueName", "owned-app");

        var whatIf = await t.WhatIfAsync(App("[]"));
        var create = await t.CreateAsync(App("[]"));

        Assert.Equal(ChangeType.NoChange, whatIf.Change("owned-app").Type);
        Assert.Null(Delta(whatIf.Change("owned-app"), "owners"));
        Assert.Single(Ignored(whatIf.Plan.Diagnostics));
        Check.Succeeded(create);
        Assert.Empty(EdgeWrites(create));
        Assert.Equal(Sorted([t.AliceId, t.BobId]), Sorted(t.Host.Directory.References($"/applications/{appId}/owners")));
    }

    [Fact]
    public async Task EmptyOwners_OnAServicePrincipal_InIncrementalMode_RemovesNoOwner()
    {
        var t = new Tenant();
        await t.SeedAsync(AppWithSp(List(t.AliceId)));
        string spId = t.Host.Directory.Objects("/servicePrincipals").Single()["id"]!.GetValue<string>();

        var whatIf = await t.WhatIfAsync(AppWithSp("[]"));
        var create = await t.CreateAsync(AppWithSp("[]"));

        Assert.Null(Delta(whatIf.ChangeOfType(ServicePrincipalsProvider.ResourceType), "owners"));
        Assert.Single(Ignored(whatIf.Plan.Diagnostics));
        Check.Succeeded(create);
        Assert.Empty(EdgeWrites(create));
        Assert.Equal([t.AliceId], t.Host.Directory.References($"/servicePrincipals/{spId}/owners"));
    }

    [Fact]
    public async Task EmptyMembers_OnAnAdministrativeUnit_InIncrementalMode_RemovesNoMember()
    {
        var t = new Tenant();
        await t.SeedAsync(Au(List(t.AliceId, t.BobId)));
        string auId = t.Host.Directory.Objects(BuiltInRoles.UnitsPath).Single()["id"]!.GetValue<string>();

        var whatIf = await t.WhatIfAsync(Au("[]"));
        var create = await t.CreateAsync(Au("[]"));

        Assert.Null(Delta(whatIf.Change("Orders AU"), "members"));
        Assert.Single(Ignored(whatIf.Plan.Diagnostics));
        Check.Succeeded(create);
        Assert.Empty(EdgeWrites(create));
        Assert.Equal(Sorted([t.AliceId, t.BobId]), Sorted(t.Host.Directory.References($"{BuiltInRoles.UnitsPath}/{auId}/members")));
    }

    // ---- a non-empty list stays authoritative in Incremental mode ----

    [Fact]
    public async Task ANonEmptyMembersList_InIncrementalMode_StillAddsTheMissing_AndRemovesTheUnlisted()
    {
        var t = new Tenant();
        await t.SeedAsync(Ops(members: List(t.AliceId, t.BobId)));

        var whatIf = await t.WhatIfAsync(Ops(members: List(t.AliceId, t.CarolId)));
        var create = await t.CreateAsync(Ops(members: List(t.AliceId, t.CarolId)));

        var children = Delta(whatIf.Change(GroupName), "members")!.Children!;
        Assert.Equal(t.BobId, Assert.Single(children, c => c.Kind == PropertyChangeType.Delete).Before!.GetValue<string>());
        Assert.Single(children, c => c.Kind == PropertyChangeType.Create);
        Assert.Empty(Ignored(whatIf.Plan.Diagnostics));

        Check.Succeeded(create);
        Assert.Single(EdgeWrites(create), w => w.Method == "POST" && w.Path == $"/groups/{t.GroupId}/members/$ref");
        Assert.Single(EdgeWrites(create), w => w.Method == "DELETE" && w.Path == $"/groups/{t.GroupId}/members/{t.BobId}/$ref");
        Assert.Equal(Sorted([t.AliceId, t.CarolId]), Sorted(t.Members));
        Check.NoDiagnostic(create, EngineDiagnosticCodes.EmptyEdgeListIgnored);
    }

    // ---- Complete mode empties the set ----

    [Theory]
    [MemberData(nameof(EmptyLists))]
    public async Task EmptyMembers_OnAnExistingGroup_InCompleteMode_RemovesEveryMember_WithDeleteChildrenInTheWhatIf(string members, string? parameters)
    {
        var t = new Tenant();
        await t.SeedAsync(Ops(members: List(t.AliceId, t.BobId)));

        var whatIf = await t.WhatIfAsync(Ops(members: members), parameters, DeploymentMode.Complete);
        var create = await t.CreateAsync(Ops(members: members), parameters, DeploymentMode.Complete);

        Check.NoWrites(whatIf);
        var children = Delta(whatIf.Change(GroupName), "members")!.Children!;
        Assert.Equal(2, children.Count);
        Assert.All(children, c => Assert.Equal(PropertyChangeType.Delete, c.Kind));
        Assert.Empty(Ignored(whatIf.Plan.Diagnostics));

        Check.Succeeded(create);
        Assert.Equal(2, EdgeWrites(create).Count(w => w.Method == "DELETE" && w.Path.StartsWith($"/groups/{t.GroupId}/members/", StringComparison.Ordinal)));
        Assert.Empty(t.Members);
    }

    [Fact]
    public async Task EmptyOwners_OnAServicePrincipal_InCompleteMode_ShowsADeleteChildInTheWhatIf()
    {
        var t = new Tenant();
        await t.SeedAsync(AppWithSp(List(t.AliceId)));

        var whatIf = await t.WhatIfAsync(AppWithSp("[]"), mode: DeploymentMode.Complete);

        var child = Assert.Single(Delta(whatIf.ChangeOfType(ServicePrincipalsProvider.ResourceType), "owners")!.Children!);
        Assert.Equal(PropertyChangeType.Delete, child.Kind);
        Assert.Equal(t.AliceId, child.Before!.GetValue<string>());
    }

    [Fact]
    public async Task EmptyMembers_OnAnAdministrativeUnit_InCompleteMode_RemovesEveryMember()
    {
        var t = new Tenant();
        await t.SeedAsync(Au(List(t.AliceId, t.BobId)));
        string auId = t.Host.Directory.Objects(BuiltInRoles.UnitsPath).Single()["id"]!.GetValue<string>();

        var whatIf = await t.WhatIfAsync(Au("[]"), mode: DeploymentMode.Complete);
        var create = await t.CreateAsync(Au("[]"), mode: DeploymentMode.Complete);

        Assert.Equal(2, Delta(whatIf.Change("Orders AU"), "members")!.Children!.Count(c => c.Kind == PropertyChangeType.Delete));
        Check.Succeeded(create);
        Assert.Empty(t.Host.Directory.References($"{BuiltInRoles.UnitsPath}/{auId}/members"));
    }

    // ---- principal side ----

    [Fact]
    public async Task EmptyMembers_BesideAPrincipalWhoseMemberOfNamesTheGroup_InIncrementalMode_AddsThatMembership_AndPrunesNoOne()
    {
        var t = new Tenant();
        await t.SeedAsync(Ops(members: List(t.AliceId, t.BobId)));
        string carol = Templates.Resource("Microsoft.Entra/users", "carol@contoso.com", "{ \"displayName\": \"Carol\", \"memberOf\": [\"[reference('ops').id]\"] }");
        string template = Ops(members: "[]") + ", " + carol;

        var whatIf = await t.WhatIfAsync(template);
        var create = await t.CreateAsync(template);

        Assert.DoesNotContain(whatIf.Changes.SelectMany(c => c.Deltas).SelectMany(d => d.Children ?? []), c => c.Kind == PropertyChangeType.Delete);
        Check.Succeeded(create);
        Assert.Single(EdgeWrites(create), w => w.Method == "POST" && w.Path == $"/groups/{t.GroupId}/members/$ref");
        Assert.DoesNotContain(EdgeWrites(create), w => w.Method == "DELETE");
        Assert.Equal(Sorted([t.AliceId, t.BobId, t.CarolId]), Sorted(t.Members));
    }

    [Fact]
    public async Task AnEmptyMemberOf_OnAPrincipal_RaisesNoDiagnostic_AndRemovesNothing()
    {
        var t = new Tenant();
        await t.SeedAsync(Ops(members: List(t.AliceId)));
        string alice = Templates.Resource("Microsoft.Entra/users", "alice@contoso.com", "{ \"displayName\": \"Alice\", \"memberOf\": [] }");

        var whatIf = await t.WhatIfAsync(alice);
        var create = await t.CreateAsync(alice);

        Assert.Empty(Ignored(whatIf.Plan.Diagnostics));
        Assert.DoesNotContain(whatIf.Plan.Diagnostics, d => d.ToString().Contains("memberOf", StringComparison.Ordinal));
        Assert.Null(Delta(whatIf.Change("alice@contoso.com"), "memberOf"));
        Check.Succeeded(create);
        Assert.Empty(EdgeWrites(create));
        Check.NoDiagnostic(create, EngineDiagnosticCodes.EmptyEdgeListIgnored);
        Assert.Equal([t.AliceId], t.Members);
    }
}
