using System;
using System.Linq;
using System.Threading.Tasks;
using EntraArm.Core.Diagnostics;
using EntraArm.Engine;
using EntraArm.Engine.Planning;
using EntraArm.Expressions.Static;
using Xunit;

namespace EntraArm.Tests.Engine;

/// <summary>The object-pass dependency graph (plan section 2.4, step 4): edge kinds, edge-property exclusion, cycles, layering.</summary>
public sealed class DependencyGraphTests
{
    private const string GroupType = "Microsoft.Entra/groups";
    private const string UserType = "Microsoft.Entra/users";

    private static string Group(string name, string? properties = null, string? extra = null)
    {
        return "{ \"type\": \"" + GroupType + "\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"" + name + "\", \"properties\": { \"displayName\": \"G\", \"mailEnabled\": false, \"securityEnabled\": true" + (properties is null ? string.Empty : ", " + properties) + " }" + (extra is null ? string.Empty : ", " + extra) + " }";
    }

    private static string User(string upn, string? properties = null, string? extra = null)
    {
        return "{ \"type\": \"" + UserType + "\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"" + upn + "\", \"properties\": { \"displayName\": \"U\"" + (properties is null ? string.Empty : ", " + properties) + " }" + (extra is null ? string.Empty : ", " + extra) + " }";
    }

    private static async Task<DeploymentPlan> ExpandAsync(string resources)
    {
        return await new EngineHost().ExpandAsync(Templates.Resources(resources));
    }

    private static DependencyEdge Edge(DeploymentPlan plan, string dependent, string dependency)
    {
        var edge = plan.Graph.Edges.FirstOrDefault(e => e.Dependent.PlannedId.Id == dependent && e.Dependency.PlannedId.Id == dependency);
        Assert.NotNull(edge);
        return edge;
    }

    private static bool HasEdge(DeploymentPlan plan, string dependent, string dependency)
    {
        return plan.Graph.Edges.Any(e => e.Dependent.PlannedId.Id == dependent && e.Dependency.PlannedId.Id == dependency);
    }

    // ---- the fixture ----

    [Fact]
    public async Task Fixture_HasExplicitParentReferenceAndSymbolicNameEdges()
    {
        var plan = await new EngineHost().ExpandAsync(await Templates.FixtureAsync());
        string assignment = plan.Instances.Single(i => i.Type == Fixture.AppRoleAssignmentType).PlannedId.Id;
        string grant = plan.Instances.Single(i => i.Type == Fixture.Oauth2GrantType).PlannedId.Id;
        string roleAssignment = plan.Instances.Single(i => i.Type == Fixture.RoleAssignmentType).PlannedId.Id;

        Assert.Equal(DependencyKind.Explicit, Edge(plan, Fixture.App, Fixture.User).Kind);
        Assert.Equal(DependencyKind.Parent, Edge(plan, Fixture.Fic0, Fixture.App).Kind);
        Assert.Equal(DependencyKind.Parent, Edge(plan, Fixture.Fic1, Fixture.App).Kind);
        Assert.Equal(DependencyKind.Parent, Edge(plan, Fixture.Password, Fixture.App).Kind);
        // The service principal both names the application symbolically and lists it in dependsOn; the first edge recorded wins the dedupe.
        Assert.Contains(Edge(plan, Fixture.SpSymbolic, Fixture.App).Kind, new[] { DependencyKind.Explicit, DependencyKind.SymbolicName });
        Assert.Equal(DependencyKind.Explicit, Edge(plan, Fixture.Group, Fixture.SpSymbolic).Kind);
        Assert.Equal(DependencyKind.Explicit, Edge(plan, assignment, Fixture.Group).Kind);
        Assert.Equal(DependencyKind.Reference, Edge(plan, assignment, Fixture.SpSymbolic).Kind);
        Assert.Equal(DependencyKind.Explicit, Edge(plan, grant, Fixture.SpSymbolic).Kind);
        // The role assignment hashes the app's appId in its name and also references the app inside principalId; the first edge recorded wins the dedupe.
        Assert.Contains(Edge(plan, roleAssignment, Fixture.App).Kind, new[] { DependencyKind.Reference, DependencyKind.SymbolicName });
        Assert.Equal(DependencyKind.Reference, Edge(plan, Fixture.Policy, Fixture.AuthStrength).Kind);
        Assert.Equal(DependencyKind.Explicit, Edge(plan, Fixture.Policy, Fixture.NamedLocation).Kind);
        Assert.Equal(DependencyKind.Explicit, Edge(plan, Fixture.Au, Fixture.User).Kind);

        var roleAssignmentInstance = EngineHost.Instance(plan, roleAssignment);
        Assert.True(roleAssignmentInstance.IsSymbolic);
        Assert.Contains(roleAssignmentInstance.NameReferences, o => o.Target?.Id == Fixture.App);
    }

    [Fact]
    public async Task Fixture_EdgePropertyReferences_DoNotCreateDependencies_ButArePopulated()
    {
        var plan = await new EngineHost().ExpandAsync(await Templates.FixtureAsync());
        var user = EngineHost.Instance(plan, Fixture.User);
        var group = EngineHost.Instance(plan, Fixture.Group);
        var app = EngineHost.Instance(plan, Fixture.App);
        var au = EngineHost.Instance(plan, Fixture.Au);

        // user.memberOf / ownerOf / administrativeUnits / appRoleAssignments reference the group, the app and the AU: edge pass only.
        Assert.False(HasEdge(plan, Fixture.User, Fixture.Group));
        Assert.False(HasEdge(plan, Fixture.User, Fixture.App));
        Assert.False(HasEdge(plan, Fixture.User, Fixture.Au));
        Assert.Contains(user.EdgeReferences, o => o.Target?.Id == Fixture.Group);
        Assert.Contains(user.EdgeReferences, o => o.Target?.Id == Fixture.App);
        Assert.Contains(user.EdgeReferences, o => o.Target?.Id == Fixture.Au);
        Assert.Empty(user.ObjectReferences);
        Assert.Single(user.ExistingReferences, o => o.Target?.Id == "/providers/Microsoft.Entra/users/cio@contoso.com");
        Assert.Single(user.ExistingReferences, o => o.Target?.Id == "/providers/Microsoft.Entra/groups/contoso-all-staff");

        // app.owners references the user: the explicit dependsOn is the only edge.
        Assert.Equal(DependencyKind.Explicit, Edge(plan, Fixture.App, Fixture.User).Kind);
        Assert.Contains(app.EdgeReferences, o => o.Target?.Id == Fixture.User);

        // group.members references the SP (edge); group.directoryRoles reference the role definition and the AU (edge, no dependency).
        Assert.Contains(group.EdgeReferences, o => o.Target?.Id == Fixture.SpSymbolic);
        Assert.False(HasEdge(plan, Fixture.Group, Fixture.RoleDefinition));
        Assert.False(HasEdge(plan, Fixture.Group, Fixture.Au));
        Assert.Contains(au.EdgeReferences, o => o.Target?.Id == Fixture.User);
    }

    [Fact]
    public async Task Fixture_IsLayeredIntoFiveBatches_OrderedByPhaseThenDeclaration()
    {
        var plan = await new EngineHost().ExpandAsync(await Templates.FixtureAsync());

        string[][] expected =
        [
            [Fixture.User, Fixture.RoleDefinition, Fixture.AuthStrength, Fixture.Accr, Fixture.NamedLocation],
            [Fixture.App, Fixture.Au],
            [Fixture.Fic0, Fixture.Fic1, Fixture.Password, Fixture.SpSymbolic],
            [Fixture.Group, plan.Instances.Single(i => i.Type == Fixture.Oauth2GrantType).PlannedId.Id, plan.Instances.Single(i => i.Type == Fixture.RoleAssignmentType).PlannedId.Id],
            [plan.Instances.Single(i => i.Type == Fixture.AppRoleAssignmentType).PlannedId.Id, Fixture.Policy],
        ];

        Assert.Equal(expected.Length, plan.Batches.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(i + 1, plan.Batches[i].Index);
            Assert.Equal(expected[i], plan.Batches[i].Instances.Select(x => x.PlannedId.Id).ToArray());
        }
    }

    [Fact]
    public async Task Fixture_TransitiveDependents_OfTheUser_IsEverythingButTheStandaloneResources()
    {
        var plan = await new EngineHost().ExpandAsync(await Templates.FixtureAsync());
        var user = EngineHost.Instance(plan, Fixture.User);

        var dependents = plan.Graph.TransitiveDependents(user).Select(i => i.PlannedId.Id).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(Fixture.InstanceCount - 5, dependents.Count);
        Assert.Contains(Fixture.App, dependents);
        Assert.Contains(Fixture.Fic1, dependents);
        Assert.Contains(Fixture.Policy, dependents);
        Assert.Contains(Fixture.Au, dependents);
        Assert.DoesNotContain(Fixture.User, dependents);
        Assert.DoesNotContain(Fixture.RoleDefinition, dependents);
        Assert.DoesNotContain(Fixture.NamedLocation, dependents);
        Assert.Empty(plan.Graph.TransitiveDependents(EngineHost.Instance(plan, Fixture.Policy)));
        Assert.Equal([Fixture.User], plan.Graph.DependenciesOf(EngineHost.Instance(plan, Fixture.App)).Select(i => i.PlannedId.Id).ToList());
        Assert.Contains(EngineHost.Instance(plan, Fixture.App), plan.Graph.DependentsOf(user));
    }

    // ---- inline shapes ----

    [Fact]
    public async Task MutualMembersAndMemberOf_IsNotACycle()
    {
        var plan = await ExpandAsync(
            Group("a", "\"members\": [\"[reference('b').id]\"]") + ", " +
            Group("b", "\"memberOf\": [\"[reference('a').id]\"]"));

        Assert.Empty(plan.Graph.Edges);
        Assert.Single(plan.Batches);
        Assert.All(plan.Instances, i => Assert.Single(i.EdgeReferences));
    }

    [Fact]
    public async Task ExplicitCycle_IsReportedWithItsPath()
    {
        var error = await Assert.ThrowsAsync<ValidationFailedException>(() => ExpandAsync(
            Group("a", null, "\"dependsOn\": [\"b\"]") + ", " +
            Group("b", null, "\"dependsOn\": [\"[resourceId('Microsoft.Entra/groups', 'c')]\"]") + ", " +
            Group("c", null, "\"dependsOn\": [\"Microsoft.Entra/groups/a\"]")));

        var cycle = Assert.Single(error.Errors);
        Assert.Equal(EngineDiagnosticCodes.DependencyCycle, cycle.Code);
        Assert.Contains("/providers/Microsoft.Entra/groups/a -> /providers/Microsoft.Entra/groups/b -> /providers/Microsoft.Entra/groups/c -> /providers/Microsoft.Entra/groups/a", cycle.Message);
        Assert.Contains("relationship property", cycle.Message);
        Assert.Equal("resources[0]", cycle.Path?.ToString());
    }

    [Fact]
    public async Task ReferenceCycleThroughObjectProperties_IsReported()
    {
        var error = await Assert.ThrowsAsync<ValidationFailedException>(() => ExpandAsync(
            Group("a", "\"description\": \"[reference('b').id]\"") + ", " +
            Group("b", "\"description\": \"[reference('a').id]\"")));

        Assert.Contains(error.Errors, d => d.Code == EngineDiagnosticCodes.DependencyCycle);
    }

    [Fact]
    public async Task SelfDependency_IsReportedAsACycle()
    {
        var error = await Assert.ThrowsAsync<ValidationFailedException>(() => ExpandAsync(Group("a", null, "\"dependsOn\": [\"a\"]")));

        var cycle = Assert.Single(error.Errors, d => d.Code == EngineDiagnosticCodes.DependencyCycle);
        Assert.Contains("depends on itself", cycle.Message);
    }

    [Fact]
    public async Task DependsOnACopyLoopName_WaitsForEveryIteration()
    {
        var plan = await ExpandAsync(
            Group("[format('g-{0}', copyIndex())]", null, "\"copy\": { \"name\": \"loop\", \"count\": 3 }") + ", " +
            User("u@contoso.com", null, "\"dependsOn\": [\"loop\"]"));

        const string user = "/providers/Microsoft.Entra/users/u@contoso.com";
        var edges = plan.Graph.Edges.Where(e => e.Dependent.PlannedId.Id == user).ToList();
        Assert.Equal(3, edges.Count);
        Assert.All(edges, e => Assert.Equal(DependencyKind.CopyLoop, e.Kind));
        Assert.Equal(["g-0", "g-1", "g-2"], edges.Select(e => e.Dependency.Name).Order(StringComparer.Ordinal).ToList());
        Assert.Equal(2, plan.Batches.Count);
        Assert.Equal([user], plan.Batches[1].Instances.Select(i => i.PlannedId.Id).ToList());
    }

    [Fact]
    public async Task DeferredCopyIndexDependsOn_IsResolvedPerIteration()
    {
        var plan = await ExpandAsync(
            Group("[format('g-{0}', copyIndex())]", null, "\"copy\": { \"name\": \"groups\", \"count\": 2 }") + ", " +
            User("[format('u{0}@contoso.com', copyIndex())]", null, "\"copy\": { \"name\": \"users\", \"count\": 2 }, \"dependsOn\": [\"[resourceId('Microsoft.Entra/groups', format('g-{0}', copyIndex()))]\"]"));

        var u0 = plan.Instances.Single(i => i.Name == "u0@contoso.com");
        var u1 = plan.Instances.Single(i => i.Name == "u1@contoso.com");
        Assert.Equal(["g-0"], plan.Graph.DependenciesOf(u0).Select(i => i.Name).ToList());
        Assert.Equal(["g-1"], plan.Graph.DependenciesOf(u1).Select(i => i.Name).ToList());
        Assert.All(plan.Graph.Edges, e => Assert.Equal(DependencyKind.Explicit, e.Kind));
        Assert.Equal(2, plan.Batches.Count);
    }

    [Fact]
    public async Task DeferredDependsOn_ThatNamesNothing_IsReported()
    {
        var error = await Assert.ThrowsAsync<ValidationFailedException>(() => ExpandAsync(
            Group("[format('g-{0}', copyIndex())]", null, "\"copy\": { \"name\": \"groups\", \"count\": 2 }") + ", " +
            User("[format('u{0}@contoso.com', copyIndex())]", null, "\"copy\": { \"name\": \"users\", \"count\": 2 }, \"dependsOn\": [\"[format('g-{0}', add(copyIndex(), 5))]\"]")));

        Assert.Contains(error.Errors, d => d.Code == DiagnosticCodes.DependsOnTargetNotFound);
    }

    [Fact]
    public async Task ReferencesInsideCopyIterations_AreRescannedWithTheConcreteFrame()
    {
        var plan = await ExpandAsync(
            Group("[format('g-{0}', copyIndex())]", null, "\"copy\": { \"name\": \"groups\", \"count\": 2 }") + ", " +
            User("[format('u{0}@contoso.com', copyIndex())]", "\"description\": \"[reference(format('g-{0}', copyIndex())).id]\"", "\"copy\": { \"name\": \"users\", \"count\": 2 }"));

        var u1 = plan.Instances.Single(i => i.Name == "u1@contoso.com");
        var edge = Assert.Single(plan.Graph.Edges, e => ReferenceEquals(e.Dependent, u1));
        Assert.Equal(DependencyKind.Reference, edge.Kind);
        Assert.Equal("g-1", edge.Dependency.Name);
        Assert.Single(u1.ObjectReferences);
    }

    [Fact]
    public async Task PhasesOnlyBreakTiesInsideABatch()
    {
        // The group is declared first but users (phase 1) are applied before groups (phase 3) inside the same batch;
        // a dependency moves the user after the group regardless of phases.
        var tie = await ExpandAsync(Group("g") + ", " + User("u@contoso.com"));
        var dependency = await ExpandAsync(Group("g") + ", " + User("u@contoso.com", null, "\"dependsOn\": [\"g\"]"));

        var batch = Assert.Single(tie.Batches);
        Assert.Equal(["/providers/Microsoft.Entra/users/u@contoso.com", "/providers/Microsoft.Entra/groups/g"], batch.Instances.Select(i => i.PlannedId.Id).ToList());

        Assert.Equal(2, dependency.Batches.Count);
        Assert.Equal("/providers/Microsoft.Entra/groups/g", Assert.Single(dependency.Batches[0].Instances).PlannedId.Id);
        Assert.Equal("/providers/Microsoft.Entra/users/u@contoso.com", Assert.Single(dependency.Batches[1].Instances).PlannedId.Id);
    }

    [Fact]
    public async Task Level_IsOnePlusTheHighestDependencyLevel()
    {
        var plan = await ExpandAsync(
            Group("a") + ", " +
            Group("b", null, "\"dependsOn\": [\"a\"]") + ", " +
            Group("c", null, "\"dependsOn\": [\"a\", \"b\"]") + ", " +
            Group("d", null, "\"dependsOn\": [\"a\"]"));

        Assert.Equal(3, plan.Batches.Count);
        Assert.Equal(["a"], plan.Batches[0].Instances.Select(i => i.Name).ToList());
        Assert.Equal(["b", "d"], plan.Batches[1].Instances.Select(i => i.Name).ToList());
        Assert.Equal(["c"], plan.Batches[2].Instances.Select(i => i.Name).ToList());
        Assert.Equal(4, plan.Graph.Edges.Count);
        Assert.Equal(1, plan.Graph.Edges.Count(e => e.Dependent.Name == "c" && e.Dependency.Name == "a"));
    }

    [Fact]
    public async Task DuplicateEdges_AreRecordedOnce()
    {
        var plan = await ExpandAsync(
            Group("a") + ", " +
            Group("b", "\"description\": \"[reference('a').id]\"", "\"dependsOn\": [\"a\", \"[resourceId('Microsoft.Entra/groups', 'a')]\"]"));

        var edge = Assert.Single(plan.Graph.Edges);
        Assert.Equal("b", edge.Dependent.Name);
        Assert.Equal(DependencyKind.Explicit, edge.Kind);
        Assert.Single(EngineHost.Instance(plan, "/providers/Microsoft.Entra/groups/b").Dependencies);
    }

    [Fact]
    public async Task NameReferences_AreScanned_AndTheSymbolicInstanceDependsOnItsInput()
    {
        var plan = await new EngineHost().ExpandAsync(await Templates.FixtureAsync());
        var sp = EngineHost.Instance(plan, Fixture.SpSymbolic);

        Assert.True(sp.IsSymbolic);
        Assert.Equal(Fixture.SpSymbol, sp.PlannedId.NameLeaf);
        var occurrence = Assert.Single(sp.NameReferences);
        Assert.Equal(ReferenceOccurrenceKind.Reference, occurrence.Kind);
        Assert.Equal(Fixture.App, occurrence.Target?.Id);
        Assert.Equal([Fixture.App], sp.Dependencies.Select(d => d.Id).ToList());
    }
}
