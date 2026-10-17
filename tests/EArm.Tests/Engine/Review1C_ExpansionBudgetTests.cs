using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Loading;
using EArm.Core.Types;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Expressions;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Static;
using EArm.Tests.Cli;
using EArm.Tests.Engine;
using Xunit;

namespace EArm.Tests;

/// <summary>
/// Review 1C, findings I-04 and S-04: <c>copy.count</c> was not bounded (a count of 3,000,000 exhausted memory while the instances were built)
/// and nothing capped the instances of a template. A copy loop is limited to 800 iterations (the ARM limit) and the whole template to 5,000
/// instances; both are checked before a single iteration is built (<c>ExpansionLimitExceeded</c>).
/// </summary>
public sealed class Review1C_ExpansionBudgetTests
{
    private static string Loop(string tag, string count)
    {
        return "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"[format('" + tag + "-{0}', copyIndex())]\", "
            + "\"copy\": { \"name\": \"" + tag + "\", \"count\": " + count + " }, \"properties\": { \"displayName\": \"g\", \"mailEnabled\": false, \"securityEnabled\": true } }";
    }

    private static string Single(string name)
    {
        return Review1C.Group("\"" + name + "\"", "{ \"displayName\": \"g\", \"mailEnabled\": false, \"securityEnabled\": true }");
    }

    private static (IReadOnlyList<ResourceInstance> Instances, List<Diagnostic> Expansion, StaticContext Static) Expand(string resources, ParameterSet? parameters = null, string extraBody = "")
    {
        var catalog = ResourceTypeCatalog.Default;
        var document = new TemplateLoader(catalog).Parse(Review1C.Template(extraBody + "\"resources\": [ " + resources + " ]"), "expand.entra.json", parameters ?? ParameterSet.Empty);
        var staticContext = new StaticEvaluator().Evaluate(document);
        var diagnostics = new List<Diagnostic>();
        var instances = new ResourceExpander(ExpressionEvaluator.Default, catalog).Expand(staticContext, staticContext.Context, diagnostics);
        return (instances, diagnostics, staticContext);
    }

    // ---- per resource: 800 ----

    [Fact]
    public void ACopyCountOfThreeMillion_IsRefusedBeforeAnythingIsBuilt()
    {
        var (instances, expansion, staticContext) = Expand(Loop("g", "3000000"));

        Assert.Empty(instances);
        var error = Assert.Single(expansion, d => d.Code == DiagnosticCodes.ExpansionLimitExceeded);
        Assert.Equal("resources[0].copy.count", error.Path!.ToString());
        Assert.Contains("3000000", error.Message, StringComparison.Ordinal);
        Assert.Contains("800", error.Message, StringComparison.Ordinal);
        Assert.Contains(staticContext.Diagnostics, d => d.Code == DiagnosticCodes.ExpansionLimitExceeded && d.Path!.ToString() == "resources[0].copy.count");
    }

    [Theory]
    [InlineData(800, true)]
    [InlineData(801, false)]
    [InlineData(100_000_000, false)]
    public void TheCopyLimit_IsExactly800(long count, bool allowed)
    {
        var (instances, expansion, staticContext) = Expand(Loop("g", count.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        Assert.Equal(allowed, expansion.All(d => d.Code != DiagnosticCodes.ExpansionLimitExceeded));
        Assert.Equal(allowed, staticContext.Diagnostics.All(d => d.Code != DiagnosticCodes.ExpansionLimitExceeded));
        Assert.Equal(allowed ? (int)count : 0, instances.Count);
    }

    [Fact]
    public void TheCount_MayComeFromAParameterOrAnExpression_AndIsLimitedAllTheSame()
    {
        var byParameter = Expand(Loop("p", "\"[parameters('n')]\""), ParameterSet.Empty.WithInline("n", "5000"), "\"parameters\": { \"n\": { \"type\": \"int\" } }, ");
        var byExpression = Expand(Loop("e", "\"[mul(900, 2)]\""));

        Assert.Empty(byParameter.Instances);
        Assert.Contains(byParameter.Expansion, d => d.Code == DiagnosticCodes.ExpansionLimitExceeded);
        Assert.Empty(byExpression.Instances);
        Assert.Contains(byExpression.Expansion, d => d.Code == DiagnosticCodes.ExpansionLimitExceeded);
    }

    // ---- per template: 5,000 ----

    [Fact]
    public void ExactlyFiveThousandInstances_Pass_AndOneMoreDoesNot()
    {
        string sixLoops = string.Join(", ", Enumerable.Range(0, 6).Select(i => Loop("l" + i, "800")));

        var atLimit = Expand(sixLoops + ", " + Loop("tail", "200"));
        var over = Expand(sixLoops + ", " + Loop("tail", "201"));
        var overBySingle = Expand(sixLoops + ", " + Loop("tail", "200") + ", " + Single("one-more"));

        Assert.Equal(5_000, atLimit.Instances.Count);
        Assert.DoesNotContain(atLimit.Expansion, d => d.Code == DiagnosticCodes.ExpansionLimitExceeded);
        Assert.DoesNotContain(atLimit.Static.Diagnostics, d => d.Code == DiagnosticCodes.ExpansionLimitExceeded);

        Assert.Equal(4_800, over.Instances.Count);
        Assert.Contains(over.Expansion, d => d.Code == DiagnosticCodes.ExpansionLimitExceeded && d.Message.Contains("5000", StringComparison.Ordinal));
        Assert.Contains(over.Static.Diagnostics, d => d.Code == DiagnosticCodes.ExpansionLimitExceeded && d.Path!.ToString() == "resources[6]");

        Assert.Equal(5_000, overBySingle.Instances.Count);
        Assert.Contains(overBySingle.Expansion, d => d.Code == DiagnosticCodes.ExpansionLimitExceeded && d.Path!.ToString() == "resources[7]");
    }

    // ---- through the planner and the command line ----

    [Fact]
    public async Task ThePlanner_FailsValidation_WithoutExpanding()
    {
        var exception = await Assert.ThrowsAsync<ValidationFailedException>(() => new EngineHost().ExpandAsync(Templates.Resources(Loop("g", "3000000"))));

        Assert.Contains(exception.Diagnostics, d => d.Code == DiagnosticCodes.ExpansionLimitExceeded);
    }

    [Theory]
    [InlineData("800", 0)]
    [InlineData("801", 2)]
    [InlineData("3000000", 2)]
    public async Task OnTheCommandLine_Validate_AppliesTheCopyLimit(string count, int exitCode)
    {
        using var directory = new TestDirectory(nameof(OnTheCommandLine_Validate_AppliesTheCopyLimit) + count);
        string path = directory.File("copy.entra.json");
        await System.IO.File.WriteAllTextAsync(path, Review1C.Template("\"resources\": [ " + Loop("g", count) + " ]"), new UTF8Encoding(false), TestContext.Current.CancellationToken);

        var result = await CliRunner.ValidateJsonAsync(path);

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(exitCode != 0, result.Diagnostics.Any(d => d!["code"]!.GetValue<string>() == "ExpansionLimitExceeded"));
        Assert.Equal(string.Empty, result.StandardError);
    }
}
