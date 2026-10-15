using System;
using System.Linq;
using System.Threading.Tasks;
using EArm.Core.Diagnostics;
using EArm.Engine.Planning;
using EArm.Providers.Tests.Infrastructure;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// <c>copy</c> loops with counts, batch sizes, names and per-iteration names that evaluate to nonsense: each is a located
/// diagnostic (never an exception, never a silent zero or an endless expansion), and the legitimate edge values expand as documented.
/// </summary>
public sealed class CopyLoopDiagnosticsTests
{
    private static string Loop(string copy, string name = "[concat('g', copyIndex('loop'))]")
    {
        return "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"" + name + "\", \"copy\": " + copy + ", \"properties\": { \"displayName\": \"G\" } }";
    }

    private static string Copy(string count, string? batchSize = null, string? mode = null, string name = "loop")
    {
        return "{ \"name\": \"" + name + "\", \"count\": " + count + (mode is null ? string.Empty : ", \"mode\": \"" + mode + "\"") + (batchSize is null ? string.Empty : ", \"batchSize\": " + batchSize) + " }";
    }

    private static async Task<ValidationFailedException> Fail(string resource)
    {
        var host = new ProviderHost();
        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => host.ExpandAsync(host.Resources(resource)));
        Assert.Empty(host.Fake.Requests);
        return ex;
    }

    [Theory]
    [InlineData("\"[sub(0, 1)]\"", "non-negative integer")]
    [InlineData("\"[concat('a', 'b')]\"", "non-negative integer")]
    [InlineData("\"[json('1.5')]\"", "non-negative integer")]
    [InlineData("\"[createArray(1, 2)]\"", "non-negative integer")]
    [InlineData("\"[null()]\"", "non-negative integer")]
    public async Task ACountThatIsNotANonNegativeInteger_IsInvalidCopy(string count, string fragment)
    {
        var ex = await Fail(Loop(Copy(count)));

        var diagnostic = ex.Diagnostics.First(d => d.Code == DiagnosticCodes.InvalidCopy);
        Assert.Contains(fragment, diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("copy", diagnostic.Path!.ToString(), StringComparison.Ordinal);
        Assert.Contains("count", diagnostic.Path.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACountExpression_ThatFailsToEvaluate_IsReportedWithItsOwnCodeAndPath()
    {
        var ex = await Fail(Loop(Copy("\"[div(1, 0)]\"")));

        var diagnostic = ex.Diagnostics.First(d => d.Path is not null && d.Path.ToString().Contains("count", StringComparison.Ordinal));
        Assert.Contains("division by zero", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("\"[sub(0, 1)]\"")]
    [InlineData("0")]
    [InlineData("\"[concat('x', 'y')]\"")]
    public async Task ABatchSizeThatIsNotPositive_IsInvalidCopy(string batchSize)
    {
        var host = new ProviderHost();

        var ex = await Record.ExceptionAsync(() => host.ExpandAsync(host.Resources(Loop(Copy("2", batchSize, "serial")))));

        var validation = Assert.IsType<ValidationFailedException>(ex);
        Assert.Contains(validation.Diagnostics, d => d.Code is DiagnosticCodes.InvalidCopy or "SchemaViolation");
    }

    [Fact]
    public async Task ACopyNameThatIsNotALiteralString_IsInvalidCopy()
    {
        var ex = await Fail(Loop(Copy("2", name: "x").Replace("\"x\"", "\"[add(1, 1)]\"", StringComparison.Ordinal)));

        Assert.Contains(ex.Diagnostics, d => d.Code == DiagnosticCodes.InvalidCopy && d.Message.Contains("literal string", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnIterationWhoseNameIsEmpty_IsReportedPerIteration()
    {
        var ex = await Fail(Loop(Copy("3"), "[if(equals(copyIndex('loop'), 1), '', concat('g', copyIndex('loop')))]"));

        Assert.Contains(ex.Diagnostics, d => d.Code == DiagnosticCodes.InvalidResourceName);
    }

    [Fact]
    public async Task ACountOfZero_ExpandsToNothing_AndTheOthersStillExpand()
    {
        var host = new ProviderHost();
        string none = Loop(Copy("0"));
        string two = Loop(Copy("2", name: "two"), "[concat('two', copyIndex('two'))]");

        var plan = await host.ExpandAsync(host.Resources(none + "," + two));

        Assert.Equal(["two0", "two1"], plan.Instances.Select(i => i.Id.NameLeaf).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task ACountComputedFromAParameter_AndASerialBatchSize_ExpandInOrder()
    {
        var host = new ProviderHost();
        string loop = Loop(Copy("\"[parameters('n')]\"", "2", "serial"), "[concat('s', copyIndex('loop', 1))]");

        var plan = await host.ExpandAsync(host.Resources(loop, parameters: "\"n\": { \"type\": \"int\", \"defaultValue\": 5 }"));

        Assert.Equal(["s1", "s2", "s3", "s4", "s5"], plan.Instances.Select(i => i.Id.NameLeaf).Order(StringComparer.Ordinal).ToArray());
        var batches = plan.Batches.Select(b => b.Instances.Count).ToList();
        Assert.True(batches.Count >= 3, "serial copy with batchSize 2 is split into at least three batches: " + string.Join(",", batches));
    }
}
