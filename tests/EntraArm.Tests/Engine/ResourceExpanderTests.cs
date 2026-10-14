using System;
using System.Linq;
using System.Threading.Tasks;
using EntraArm.Core.Diagnostics;
using EntraArm.Engine;
using EntraArm.Engine.Planning;
using Xunit;

namespace EntraArm.Tests.Engine;

/// <summary>Expansion (plan section 2.4, step 3): copy loops, per-iteration names and conditions, duplicate ids, parent linkage.</summary>
public sealed class ResourceExpanderTests
{
    private const string GroupType = "Microsoft.Entra/groups";

    private static string Group(string name, string? extra = null)
    {
        return "{ \"type\": \"" + GroupType + "\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"" + name + "\", \"properties\": { \"displayName\": \"G\", \"mailEnabled\": false, \"securityEnabled\": true }" + (extra is null ? string.Empty : ", " + extra) + " }";
    }

    private static string GroupLoop(string copy, string? extra = null)
    {
        return Group("[format('g-{0}', copyIndex())]", "\"copy\": " + copy + (extra is null ? string.Empty : ", " + extra));
    }

    private static async Task<DeploymentPlan> ExpandAsync(string resources, params (string Name, string Value)[] parameters)
    {
        return await new EngineHost().ExpandAsync(Templates.Resources(resources, parameters));
    }

    private static async Task<ValidationFailedException> FailsAsync(string resources, params (string Name, string Value)[] parameters)
    {
        return await Assert.ThrowsAsync<ValidationFailedException>(() => ExpandAsync(resources, parameters));
    }

    [Fact]
    public async Task ParallelCopy_YieldsOneInstancePerIndex_WithConcreteNamesAndFrames()
    {
        var plan = await ExpandAsync(GroupLoop("{ \"name\": \"loop\", \"count\": 3, \"mode\": \"parallel\" }"));

        Assert.Equal(3, plan.Instances.Count);
        Assert.Equal(["g-0", "g-1", "g-2"], plan.Instances.Select(i => i.Name).ToList());
        Assert.All(plan.Instances, i =>
        {
            Assert.NotNull(i.Copy);
            Assert.Equal("loop", i.Copy.Name);
            Assert.Equal(3, i.Copy.Count);
            Assert.Equal(CopyMode.Parallel, i.Copy.Mode);
            Assert.Equal(3, i.Copy.BatchSize);
            Assert.False(i.IsSymbolic);
            Assert.True(i.Condition);
            Assert.Equal(0, i.DeclarationIndex);
        });
        Assert.Equal([0L, 1L, 2L], plan.Instances.Select(i => i.Copy!.Index).ToList());
        Assert.Single(plan.Batches);
    }

    [Fact]
    public async Task CopyCount_MayComeFromAParameter_AndZeroYieldsNoInstances()
    {
        var two = await new EngineHost().ExpandAsync(Templates.Parse("\"parameters\": { \"n\": { \"type\": \"int\" } }, \"resources\": [" + GroupLoop("{ \"name\": \"loop\", \"count\": \"[parameters('n')]\" }") + "]", ("n", "2")));
        var zero = await new EngineHost().ExpandAsync(Templates.Parse("\"parameters\": { \"n\": { \"type\": \"int\" } }, \"resources\": [" + GroupLoop("{ \"name\": \"loop\", \"count\": \"[parameters('n')]\" }") + "]", ("n", "0")));

        Assert.Equal(2, two.Instances.Count);
        Assert.Empty(zero.Instances);
        Assert.Empty(zero.Batches);
    }

    /// <summary>A whole number counts as an integer however it is written: <c>3.0</c> and <c>1.5e1</c> loop like <c>3</c> and <c>15</c>.</summary>
    [Theory]
    [InlineData("3.0", 3)]
    [InlineData("1.5e1", 15)]
    [InlineData("-0.0", 0)]
    public async Task CopyCount_AndBatchSize_AcceptAWholeNumberSpelledWithAFractionOrExponent(string number, int expected)
    {
        var literal = await ExpandAsync(GroupLoop("{ \"name\": \"loop\", \"count\": " + number + ", \"mode\": \"parallel\" }"));
        var fromParameter = await new EngineHost().ExpandAsync(Templates.Parse("\"parameters\": { \"n\": { \"type\": \"int\", \"defaultValue\": " + number + " } }, \"resources\": [" + GroupLoop("{ \"name\": \"loop\", \"count\": \"[parameters('n')]\" }") + "]"));

        Assert.Equal(expected, literal.Instances.Count);
        Assert.Equal(expected, fromParameter.Instances.Count);

        var batched = await ExpandAsync(GroupLoop("{ \"name\": \"loop\", \"count\": 4, \"mode\": \"serial\", \"batchSize\": 2.0 }"));
        Assert.Equal(4, batched.Instances.Count);
    }

    [Fact]
    public async Task CopyCount_WithAFraction_IsStillRejected()
    {
        var failure = await FailsAsync(GroupLoop("{ \"name\": \"loop\", \"count\": 3.5 }"));

        Assert.Contains(failure.Diagnostics, d => d.Code == DiagnosticCodes.InvalidCopy);
    }

    [Fact]
    public async Task SerialCopy_DefaultsToBatchSizeOne_AndChainsIterations()
    {
        var plan = await ExpandAsync(GroupLoop("{ \"name\": \"loop\", \"count\": 3, \"mode\": \"serial\" }"));

        Assert.All(plan.Instances, i => Assert.Equal(CopyMode.Serial, i.Copy!.Mode));
        Assert.All(plan.Instances, i => Assert.Equal(1, i.Copy!.BatchSize));
        Assert.Equal([0L, 1L, 2L], plan.Instances.Select(i => i.Copy!.Chunk).ToList());
        Assert.Equal(3, plan.Batches.Count);
        Assert.Equal(2, plan.Graph.Edges.Count);
        Assert.All(plan.Graph.Edges, e => Assert.Equal(DependencyKind.CopySerial, e.Kind));
    }

    [Fact]
    public async Task SerialCopy_WithBatchSize_ChunksIterations()
    {
        var plan = await ExpandAsync(GroupLoop("{ \"name\": \"loop\", \"count\": 5, \"mode\": \"serial\", \"batchSize\": 2 }"));

        Assert.Equal([0L, 0L, 1L, 1L, 2L], plan.Instances.Select(i => i.Copy!.Chunk).ToList());
        Assert.Equal(3, plan.Batches.Count);
        Assert.Equal(["g-0", "g-1"], plan.Batches[0].Instances.Select(i => i.Name).ToList());
        Assert.Equal(["g-2", "g-3"], plan.Batches[1].Instances.Select(i => i.Name).ToList());
        Assert.Equal(["g-4"], plan.Batches[2].Instances.Select(i => i.Name).ToList());

        // Chunk n waits for every iteration of chunk n-1 (and only those).
        var g2 = plan.Instances.Single(i => i.Name == "g-2");
        Assert.Equal(["g-0", "g-1"], plan.Graph.DependenciesOf(g2).Select(i => i.Name).Order(StringComparer.Ordinal).ToList());
        var g4 = plan.Instances.Single(i => i.Name == "g-4");
        Assert.Equal(["g-2", "g-3"], plan.Graph.DependenciesOf(g4).Select(i => i.Name).Order(StringComparer.Ordinal).ToList());
    }

    [Fact]
    public async Task ParallelCopy_WithBatchSize_DoesNotChain()
    {
        var plan = await ExpandAsync(GroupLoop("{ \"name\": \"loop\", \"count\": 4, \"mode\": \"parallel\", \"batchSize\": 2 }"));

        Assert.All(plan.Instances, i => Assert.Equal(2, i.Copy!.BatchSize));
        Assert.Empty(plan.Graph.Edges);
        Assert.Single(plan.Batches);
    }

    [Fact]
    public async Task DuplicateIdsAfterExpansion_AreReported()
    {
        var error = await FailsAsync(Group("[format('g-{0}', mod(copyIndex(), 2))]", "\"copy\": { \"name\": \"loop\", \"count\": 3 }"));

        var duplicate = Assert.Single(error.Errors);
        Assert.Equal(DiagnosticCodes.DuplicateResourceId, duplicate.Code);
        Assert.Contains("'/providers/Microsoft.Entra/groups/g-0'", duplicate.Message);
        Assert.Contains("iteration 0", duplicate.Message);
        Assert.Equal("resources[0].name", duplicate.Path?.ToString());
    }

    [Fact]
    public async Task DuplicateIdBetweenALoopAndALiteral_IsReported()
    {
        var error = await FailsAsync(GroupLoop("{ \"name\": \"loop\", \"count\": 2 }") + ", " + Group("g-1"));

        var duplicate = Assert.Single(error.Errors);
        Assert.Equal(DiagnosticCodes.DuplicateResourceId, duplicate.Code);
        Assert.Equal("resources[1].name", duplicate.Path?.ToString());
    }

    [Fact]
    public async Task Condition_IsEvaluatedPerIteration()
    {
        var plan = await ExpandAsync(GroupLoop("{ \"name\": \"loop\", \"count\": 3 }", "\"condition\": \"[less(copyIndex(), 2)]\""));

        Assert.Equal(3, plan.Instances.Count);
        Assert.Equal([true, true, false], plan.Instances.Select(i => i.Condition).ToList());
        Assert.True(plan.Instances[2].IsIgnored);
        Assert.Contains(plan.Instances[2], plan.Batches.SelectMany(b => b.Instances));
    }

    [Fact]
    public async Task Condition_FromAParameter_KeepsTheInstanceAsIgnored()
    {
        var plan = await new EngineHost().ExpandAsync(Templates.Parse("\"parameters\": { \"env\": { \"type\": \"string\" } }, \"resources\": [" + Group("g", "\"condition\": \"[equals(parameters('env'), 'prod')]\"") + "]", ("env", "dev")));

        var instance = Assert.Single(plan.Instances);
        Assert.True(instance.IsIgnored);
        Assert.False(instance.Condition);
    }

    [Fact]
    public async Task NonBooleanCondition_InAnIteration_IsAnError()
    {
        var error = await FailsAsync(GroupLoop("{ \"name\": \"loop\", \"count\": 2 }", "\"condition\": \"[copyIndex()]\""));

        Assert.Contains(error.Errors, d => d.Code == DiagnosticCodes.ConditionNotBoolean && d.Message.Contains("iteration 0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvalidCopyValues_AreReported()
    {
        var badMode = await new EngineHost().ExpandAsync(Templates.Parse("\"parameters\": { \"m\": { \"type\": \"string\" } }, \"resources\": [" + GroupLoop("{ \"name\": \"loop\", \"count\": 1, \"mode\": \"[parameters('m')]\" }") + "]", ("m", "parallel")));
        Assert.Single(badMode.Instances);

        var error = await Assert.ThrowsAsync<ValidationFailedException>(() => new EngineHost().ExpandAsync(Templates.Parse("\"parameters\": { \"m\": { \"type\": \"string\" } }, \"resources\": [" + GroupLoop("{ \"name\": \"loop\", \"count\": 1, \"mode\": \"[parameters('m')]\" }") + "]", ("m", "sideways"))));
        var invalid = Assert.Single(error.Errors);
        Assert.Equal(DiagnosticCodes.InvalidCopy, invalid.Code);
        Assert.Contains("sideways", invalid.Message);

        var negative = await Assert.ThrowsAsync<ValidationFailedException>(() => new EngineHost().ExpandAsync(Templates.Parse("\"parameters\": { \"n\": { \"type\": \"int\" } }, \"resources\": [" + GroupLoop("{ \"name\": \"loop\", \"count\": \"[parameters('n')]\" }") + "]", ("n", "-1"))));
        Assert.Contains(negative.Errors, d => d.Code == DiagnosticCodes.InvalidCopy);
    }

    [Fact]
    public async Task CopyCount_DependingOnADeployTimeValue_IsCopyExpansionFailed()
    {
        var error = await FailsAsync(Group("other") + ", " + GroupLoop("{ \"name\": \"loop\", \"count\": \"[length(reference('other').displayName)]\" }"));

        var failed = Assert.Single(error.Errors, d => d.Code == EngineDiagnosticCodes.CopyExpansionFailed);
        Assert.Contains("copy.count depends on a value that is not known before deployment", failed.Message);
    }

    [Fact]
    public async Task NestedChild_IsLinkedToItsParentInstance()
    {
        var plan = await new EngineHost().ExpandAsync(await Templates.FixtureAsync());

        var app = EngineHost.Instance(plan, Fixture.App);
        var fic0 = EngineHost.Instance(plan, Fixture.Fic0);
        var fic1 = EngineHost.Instance(plan, Fixture.Fic1);
        var password = EngineHost.Instance(plan, Fixture.Password);

        Assert.Equal(Fixture.InstanceCount, plan.Instances.Count);
        Assert.Same(app, fic0.Parent);
        Assert.Same(app, fic1.Parent);
        Assert.Same(app, password.Parent);
        Assert.Equal(Fixture.App, fic0.ParentId?.Id);
        Assert.Null(app.Parent);
        Assert.NotNull(fic0.Copy);
        Assert.Equal("fics", fic0.Copy.Name);
        Assert.Equal(0, fic0.Copy.Index);
        Assert.Equal(1, fic1.Copy!.Index);
    }

    [Fact]
    public async Task TopLevelParentChildName_IsLinkedToItsParentInstance_AndAnExistingParentIsNot()
    {
        const string fic = "{ \"type\": \"Microsoft.Entra/applications/federatedIdentityCredentials\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"app/github\", \"properties\": { \"issuer\": \"i\", \"subject\": \"s\" }, \"dependsOn\": [\"/providers/Microsoft.Entra/applications/app\"] }";
        const string app = "{ \"type\": \"Microsoft.Entra/applications\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"app\", \"properties\": { \"displayName\": \"A\" } }";
        const string orphanFic = "{ \"type\": \"Microsoft.Entra/applications/federatedIdentityCredentials\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"existing-app/github\", \"properties\": { \"issuer\": \"i\", \"subject\": \"s\" } }";

        var linked = await ExpandAsync(fic + ", " + app);
        var standalone = await ExpandAsync(orphanFic);

        var child = EngineHost.Instance(linked, "/providers/Microsoft.Entra/applications/app/federatedIdentityCredentials/github");
        Assert.Same(EngineHost.Instance(linked, "/providers/Microsoft.Entra/applications/app"), child.Parent);
        Assert.Contains(linked.Graph.Edges, e => e.Kind is DependencyKind.Parent or DependencyKind.Explicit && ReferenceEquals(e.Dependent, child) && ReferenceEquals(e.Dependency, child.Parent));
        Assert.Equal(2, linked.Batches.Count);

        var orphan = Assert.Single(standalone.Instances);
        Assert.Null(orphan.Parent);
        Assert.Equal("/providers/Microsoft.Entra/applications/existing-app", orphan.ParentId?.Id);
    }

    [Fact]
    public async Task ExpandedInstances_KeepDeclarationOrderAndCarryPhases()
    {
        var plan = await new EngineHost().ExpandAsync(await Templates.FixtureAsync());

        Assert.Equal(1, EngineHost.Instance(plan, Fixture.User).Phase);
        Assert.Equal(1, EngineHost.Instance(plan, Fixture.App).Phase);
        Assert.Equal(2, EngineHost.Instance(plan, Fixture.SpSymbolic).Phase);
        Assert.Equal(2, EngineHost.Instance(plan, Fixture.Fic0).Phase);
        Assert.Equal(3, EngineHost.Instance(plan, Fixture.Group).Phase);
        Assert.Equal(4, EngineHost.Instance(plan, Fixture.Au).Phase);
        Assert.Equal(5, plan.Instances.Single(i => i.Type == Fixture.AppRoleAssignmentType).Phase);
        Assert.Equal(6, EngineHost.Instance(plan, Fixture.Policy).Phase);
        Assert.True(plan.Instances.Select(i => i.DeclarationIndex).SequenceEqual(plan.Instances.Select(i => i.DeclarationIndex).Order()));
    }
}
