using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EArm.Core.Validation;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Engine.WhatIf;
using EArm.Providers.Tests.Infrastructure;
using EArm.TestSupport;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// A group's <c>owners</c> list is the authoritative set of its owners: missing owners are added before live owners not listed are removed, an empty list
/// is refused (Graph does not remove the last user owner of a group), an absent list leaves the owners alone, and a set that still holds a deploy-time
/// placeholder is never pruned from its resolved part alone.
/// </summary>
public sealed class GroupOwnersAuthoritativeSetTests
{
    private const string GroupType = "Microsoft.Entra/groups";
    private const string GroupName = "ops";
    private const string LastOwnerHint = "last user owner";
    private const string PartialNote = "removed at deployment unless a deploy-time entry resolves to it";

    /// <summary>A group 'base' named "Base": four characters, so <see cref="FailsAtApply"/> divides by zero once it exists.</summary>
    private static readonly string Base = Templates.Resource(GroupType, "base", "{ \"displayName\": \"Base\" }");

    private const string FailsAtApply = "[string(div(1, sub(length(reference('base').displayName), 4)))]";

    private static string Ops(string? owners)
    {
        return Templates.Resource(GroupType, GroupName, "{ \"displayName\": \"Ops\"" + (owners is null ? string.Empty : ", \"owners\": " + owners) + " }");
    }

    private static string List(params string[] entries)
    {
        return "[" + string.Join(", ", entries.Select(e => "\"" + e + "\"")) + "]";
    }

    private static string Join(params string[] resources)
    {
        return string.Join(",", resources);
    }

    private static IReadOnlyList<FakeGraphRequest> EdgeWrites(Run run)
    {
        return run.Writes.Where(w => w.Path.Contains("$ref", StringComparison.Ordinal)).ToList();
    }

    private static PropertyDelta OwnersDelta(ResourceChange change)
    {
        return Assert.Single(change.Deltas, d => d.Path.ToString() == "properties.owners");
    }

    private sealed class Tenant
    {
        public Tenant()
        {
            AliceId = Host.Directory.AddUser("alice@contoso.com", "Alice")["id"]!.GetValue<string>();
            BobId = Host.Directory.AddUser("bob@contoso.com", "Bob")["id"]!.GetValue<string>();
        }

        public ProviderHost Host { get; } = new();

        public string AliceId { get; }

        public string BobId { get; }

        public string GroupId => Host.IdOf("/groups", "uniqueName", GroupName);

        public IReadOnlyList<string> Owners => Host.Directory.References($"/groups/{GroupId}/owners").ToList();

        /// <summary>Creates the group with the given live owners.</summary>
        public async Task<Tenant> WithOwnersAsync(params string[] owners)
        {
            Check.Succeeded(await Host.CreateAsync(Host.Resources(Ops(List(owners)))));
            return this;
        }

        public Task<Run> WhatIfAsync(string resources, string? parameters = null)
        {
            return Host.WhatIfAsync(Host.Resources(resources, parameters: parameters));
        }

        public Task<Run> CreateAsync(string resources, string? parameters = null, bool continueOnError = false)
        {
            return Host.CreateAsync(Host.Resources(resources, parameters: parameters), continueOnError: continueOnError);
        }
    }

    // ---- an empty owners list is refused ----

    [Fact]
    public void AnEmptyLiteralOwnersList_OnAGroup_IsASchemaViolation()
    {
        var host = new ProviderHost();

        var diagnostics = new TemplateValidator(host.Catalog).Validate(host.Resources(Ops("[]")));

        Assert.Contains(diagnostics, d => d.Code == "SchemaViolation" && d.Message.Contains("minItems", StringComparison.Ordinal) && d.ToString().Contains("properties.owners", StringComparison.Ordinal));
    }

    /// <summary>The literal never reaches the groups provider: the template is refused as a whole by the schema (minItems) in what-if and create alike.</summary>
    [Fact]
    public async Task AnEmptyLiteralOwnersList_OnAGroup_IsRefusedBeforeAnyGraphWrite_InWhatIfAndCreate()
    {
        var t = new Tenant();
        await t.WithOwnersAsync(t.AliceId);
        int before = t.Host.Fake.Requests.Count;

        var whatIf = await Assert.ThrowsAsync<ValidationFailedException>(() => t.WhatIfAsync(Ops("[]")));
        var create = await Assert.ThrowsAsync<ValidationFailedException>(() => t.CreateAsync(Ops("[]")));

        Assert.Contains(whatIf.Diagnostics, d => d.Code == "SchemaViolation");
        Assert.Contains(create.Diagnostics, d => d.Code == "SchemaViolation");
        Assert.DoesNotContain(t.Host.Fake.Requests.Skip(before), r => r.IsWrite);
        Assert.Equal([t.AliceId], t.Owners);
    }

    /// <summary>An expression the schema cannot see through reaches the groups provider, which makes the row unsupported at plan time.</summary>
    [Theory]
    [InlineData("\"[parameters('owners')]\"", "\"owners\": { \"type\": \"array\", \"defaultValue\": [] }")]
    [InlineData("\"[json('[]')]\"", null)]
    public async Task AnOwnersExpressionThatEvaluatesToEmpty_OnAnExistingGroup_IsUnsupported_AndPlansNoEdgeWrite(string owners, string? parameters)
    {
        var t = new Tenant();
        await t.WithOwnersAsync(t.AliceId);

        var whatIf = await t.WhatIfAsync(Ops(owners), parameters);
        var create = await t.CreateAsync(Ops(owners), parameters);

        var row = whatIf.Change(GroupName);
        Assert.True(row.HasError);
        Assert.Contains(LastOwnerHint, row.UnsupportedReason, StringComparison.Ordinal);
        Assert.DoesNotContain(row.Deltas, d => d.Path.ToString() == "properties.owners");
        Check.NoWrites(whatIf);

        Assert.False(create.Succeeded);
        var op = create.Op(GroupName);
        Assert.Equal(OperationStatus.Failed, op.Status);
        Assert.Contains(LastOwnerHint, op.Error, StringComparison.Ordinal);
        Assert.Empty(EdgeWrites(create));
        Assert.Equal([t.AliceId], t.Owners);
    }

    [Fact]
    public async Task AnOwnersParameterDefaultingToEmpty_OnANewGroup_IsUnsupported_AndCreatesNothing()
    {
        var t = new Tenant();
        const string parameters = "\"owners\": { \"type\": \"array\", \"defaultValue\": [] }";

        var whatIf = await t.WhatIfAsync(Ops("\"[parameters('owners')]\""), parameters);
        var create = await t.CreateAsync(Ops("\"[parameters('owners')]\""), parameters);

        Assert.Contains(LastOwnerHint, whatIf.Change(GroupName).UnsupportedReason, StringComparison.Ordinal);
        Assert.False(create.Succeeded);
        Assert.Contains(LastOwnerHint, create.Op(GroupName).Error, StringComparison.Ordinal);
        Check.NoWrites(create);
        Assert.Null(t.Host.Directory.Find("/groups", "uniqueName", GroupName));
    }

    [Fact]
    public async Task AnEmptyOwnersList_OnAnApplication_IsStillAllowed_AndRemovesEveryOwner()
    {
        var t = new Tenant();
        string Template(string owners) => Templates.Resource("Microsoft.Entra/applications", "owned-app", "{ \"displayName\": \"Owned App\", \"owners\": " + owners + " }");
        Check.Succeeded(await t.CreateAsync(Template(List(t.AliceId, t.BobId))));
        string appId = t.Host.IdOf("/applications", "uniqueName", "owned-app");

        var whatIf = await t.WhatIfAsync(Template("[]"));
        var run = await t.CreateAsync(Template("[]"));

        Assert.False(whatIf.Change("owned-app").HasError);
        Assert.Equal(2, OwnersDelta(whatIf.Change("owned-app")).Children!.Count(c => c.Kind == PropertyChangeType.Delete));
        Check.Succeeded(run);
        Assert.Equal(2, EdgeWrites(run).Count(w => w.Method == "DELETE" && w.Path.StartsWith($"/applications/{appId}/owners/", StringComparison.Ordinal)));
        Assert.Empty(t.Host.Directory.References($"/applications/{appId}/owners"));
    }

    [Fact]
    public async Task AnEmptyOwnersList_OnAServicePrincipal_IsStillAllowed_AndRemovesEveryOwner()
    {
        var t = new Tenant();
        const string app = "{ \"type\": \"Microsoft.Entra/applications\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"sp-app\", \"properties\": { \"displayName\": \"SP App\" } }";
        string Template(string owners) => Join(app, "{ \"type\": \"Microsoft.Entra/servicePrincipals\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"[reference('sp-app').appId]\", \"properties\": { \"owners\": " + owners + " }, \"dependsOn\": [\"[resourceId('Microsoft.Entra/applications', 'sp-app')]\"] }");
        Check.Succeeded(await t.CreateAsync(Template(List(t.AliceId))));
        string spId = t.Host.Directory.Objects("/servicePrincipals").Single()["id"]!.GetValue<string>();
        Assert.Equal([t.AliceId], t.Host.Directory.References($"/servicePrincipals/{spId}/owners"));

        var run = await t.CreateAsync(Template("[]"));

        Check.Succeeded(run);
        Assert.Single(EdgeWrites(run), w => w.Method == "DELETE" && w.Path == $"/servicePrincipals/{spId}/owners/{t.AliceId}/$ref");
        Assert.Empty(t.Host.Directory.References($"/servicePrincipals/{spId}/owners"));
    }

    // ---- the authoritative set ----

    [Fact]
    public async Task AnUnlistedLiveOwner_IsAWhatIfRemoval_ThatCountsAsAChange_AndIsDeletedByTheDeployment()
    {
        var t = new Tenant();
        await t.WithOwnersAsync(t.AliceId, t.BobId);

        var whatIf = await t.WhatIfAsync(Ops(List(t.AliceId)));
        var run = await t.CreateAsync(Ops(List(t.AliceId)));

        Check.NoWrites(whatIf);
        var child = Assert.Single(OwnersDelta(whatIf.Change(GroupName)).Children!);
        Assert.Equal(PropertyChangeType.Delete, child.Kind);
        Assert.Equal(t.BobId, child.Before!.GetValue<string>());
        Assert.False(child.IsUnknown);
        var report = WhatIfReport.From(whatIf.Plan);
        var row = Assert.Single(report.Rows, r => r.ResourceId.EndsWith("/" + GroupName, StringComparison.Ordinal));
        Assert.Equal(new EdgeSummary(0, 1), row.Edges);
        Assert.True(row.IsWrite);
        Assert.True(report.HasChanges);

        Check.Succeeded(run);
        var write = Assert.Single(EdgeWrites(run));
        Assert.Equal("DELETE", write.Method);
        Assert.Equal($"/groups/{t.GroupId}/owners/{t.BobId}/$ref", write.Path);
        Assert.Equal([t.AliceId], t.Owners);
    }

    [Fact]
    public async Task AnAbsentOwnersList_LeavesTheLiveOwnersUntouched()
    {
        var t = new Tenant();
        await t.WithOwnersAsync(t.AliceId, t.BobId);

        var whatIf = await t.WhatIfAsync(Ops(null));
        var run = await t.CreateAsync(Ops(null));

        Assert.Equal(ChangeType.NoChange, whatIf.Change(GroupName).Type);
        Assert.DoesNotContain(whatIf.Change(GroupName).Deltas, d => d.Path.ToString() == "properties.owners");
        Assert.False(WhatIfReport.From(whatIf.Plan).HasChanges);
        Check.Succeeded(run);
        Assert.Empty(EdgeWrites(run));
        Assert.Equal(2, t.Owners.Count);
    }

    [Fact]
    public async Task ReplacingTheOnlyOwner_AddsTheNewOwnerBeforeRemovingTheOldOne()
    {
        var t = new Tenant();
        await t.WithOwnersAsync(t.AliceId);

        var run = await t.CreateAsync(Ops(List(t.BobId)));

        Check.Succeeded(run);
        var writes = EdgeWrites(run).ToList();
        int add = writes.FindIndex(w => w.Method == "POST" && w.Path == $"/groups/{t.GroupId}/owners/$ref");
        int remove = writes.FindIndex(w => w.Method == "DELETE" && w.Path == $"/groups/{t.GroupId}/owners/{t.AliceId}/$ref");
        Assert.True(add >= 0, "the new owner is added");
        Assert.True(remove >= 0, "the old owner is removed");
        Assert.True(add < remove, "the add precedes the removal:\n" + string.Join("\n", writes.Select(Run.Describe)));
        Assert.Equal([t.BobId], t.Owners);
    }

    [Fact]
    public async Task RemovingTheSignedInUserAsGroupOwner_Warns_AndStillRemovesIt()
    {
        var t = new Tenant();
        await t.WithOwnersAsync(t.AliceId, t.BobId);
        t.Host.Directory.SignedInUserId = t.AliceId;

        var run = await t.CreateAsync(Ops(List(t.BobId)));

        Check.Succeeded(run);
        Check.Diagnostic(run, EngineDiagnosticCodes.SignedInPrincipalRemoved);
        Assert.Single(EdgeWrites(run), w => w.Method == "DELETE" && w.Path == $"/groups/{t.GroupId}/owners/{t.AliceId}/$ref");
        Assert.Equal([t.BobId], t.Owners);
    }

    // ---- a set with a deploy-time placeholder ----

    [Fact]
    public async Task WhatIf_OfASetWithADeployTimeEntry_ShowsUnlistedOwnersAsUnknownRemovals_AndTheDeploymentDecidesOnceResolved()
    {
        var t = new Tenant();
        await t.WithOwnersAsync(t.AliceId, t.BobId);
        string dave = Templates.Resource("Microsoft.Entra/users", "dave@contoso.com", "{ \"displayName\": \"Dave\" }");
        string template = Join(dave, Ops("[\"" + t.AliceId + "\", \"[reference('dave@contoso.com').id]\"]"));

        var whatIf = await t.WhatIfAsync(template);

        Check.NoWrites(whatIf);
        var group = whatIf.Change(GroupName);
        Assert.False(group.HasError, group.UnsupportedReason);
        Assert.Equal(ChangeType.Modify, group.Type);
        Assert.True(group.IsUnknown);
        var children = OwnersDelta(group).Children!;
        var removal = Assert.Single(children, c => c.Kind == PropertyChangeType.Delete);
        Assert.Equal(t.BobId, removal.Before!.GetValue<string>());
        Assert.True(removal.IsUnknown);
        Assert.Equal(PartialNote, removal.Note);
        Assert.Contains(children, c => c.Kind == PropertyChangeType.Create && c.IsUnknown);
        var row = Assert.Single(WhatIfReport.From(whatIf.Plan).Rows, r => r.ResourceId.EndsWith("/" + GroupName, StringComparison.Ordinal));
        Assert.Equal("~?", row.Marker);

        var run = await t.CreateAsync(template);

        Check.Succeeded(run);
        string daveId = t.Host.IdOf("/users", "userPrincipalName", "dave@contoso.com");
        Assert.Contains(EdgeWrites(run), w => w.Method == "DELETE" && w.Path == $"/groups/{t.GroupId}/owners/{t.BobId}/$ref");
        Assert.Equal(new[] { t.AliceId, daveId }.Order(StringComparer.Ordinal), t.Owners.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Create_WithAnUnresolvedEntryInTheSet_RemovesNothing_AndWarnsThatTheSetWasNotPruned()
    {
        var t = new Tenant();
        await t.WithOwnersAsync(t.AliceId, t.BobId);
        string ghost = Templates.Resource("Microsoft.Entra/users", "ghost@contoso.com", "{ \"displayName\": \"" + FailsAtApply + "\" }", extra: Templates.DependsOn(GroupType, "base"));
        string template = Join(Base, ghost, Ops("[\"" + t.AliceId + "\", \"[reference('ghost@contoso.com').id]\"]"));

        var run = await t.CreateAsync(template, continueOnError: true);

        Assert.False(run.Succeeded);
        Assert.Equal(OperationStatus.Failed, run.Op("ghost").Status);
        Assert.DoesNotContain(EdgeWrites(run), w => w.Method == "DELETE");
        var warning = Assert.Single(run.Executed.Diagnostics, d => d.Code == EngineDiagnosticCodes.EdgePassSkipped);
        Assert.Contains("properties.owners was not pruned", warning.Message, StringComparison.Ordinal);
        Assert.Contains("is not resolved", warning.Message, StringComparison.Ordinal);
        var group = run.Op(GroupName);
        Assert.Equal(OperationStatus.Failed, group.Status);
        Assert.Contains("depends on values that are not available", group.Error, StringComparison.Ordinal);
        Assert.Equal(new[] { t.AliceId, t.BobId }.Order(StringComparer.Ordinal), t.Owners.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AnUnresolvedOwnerOfElsewhere_KeepsTheSetUnpruned_OnCreate_ButTheWhatIfShowsCertainRemovals()
    {
        var t = new Tenant();
        await t.WithOwnersAsync(t.AliceId, t.BobId);
        string ghostGroup = Templates.Resource(GroupType, "ghost-group", "{ \"displayName\": \"" + FailsAtApply + "\" }", extra: Templates.DependsOn(GroupType, "base"));
        string carol = Templates.Resource("Microsoft.Entra/users", "carol@contoso.com", "{ \"displayName\": \"Carol\", \"ownerOf\": [\"[reference('ghost-group').id]\"] }");
        string template = Join(Base, ghostGroup, carol, Ops(List(t.AliceId)));

        var whatIf = await t.WhatIfAsync(template);
        var run = await t.CreateAsync(template, continueOnError: true);

        var removal = Assert.Single(OwnersDelta(whatIf.Change(GroupName)).Children!, c => c.Kind == PropertyChangeType.Delete);
        Assert.Equal(t.BobId, removal.Before!.GetValue<string>());
        Assert.False(removal.IsUnknown);
        Assert.Null(removal.Note);

        Assert.False(run.Succeeded);
        Assert.Equal(OperationStatus.Failed, run.Op("ghost-group").Status);
        Assert.DoesNotContain(EdgeWrites(run), w => w.Method == "DELETE");
        var warning = Assert.Single(run.Executed.Diagnostics, d => d.Code == EngineDiagnosticCodes.EdgePassSkipped && d.Message.Contains("properties.owners", StringComparison.Ordinal));
        Assert.Contains("was not pruned", warning.Message, StringComparison.Ordinal);
        Assert.Contains("is not resolved", warning.Message, StringComparison.Ordinal);
        Assert.Contains("ownerOf", warning.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { t.AliceId, t.BobId }.Order(StringComparer.Ordinal), t.Owners.Order(StringComparer.Ordinal));
    }
}
