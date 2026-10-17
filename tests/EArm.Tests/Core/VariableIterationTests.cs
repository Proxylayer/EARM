using System.Linq;
using EArm.Core.Diagnostics;
using EArm.Expressions.Static;
using Xunit;

namespace EArm.Tests;

/// <summary>
/// ARM variable iteration (https://learn.microsoft.com/azure/azure-resource-manager/templates/copy-variables): a top-level <c>copy</c> array in
/// <c>variables</c> declares one array variable per entry, and a <c>copy</c> array inside a variable's value builds an array property.
/// </summary>
public sealed class VariableIterationTests
{
    private static string Template(string variables, string outputs)
    {
        return Review1C.Template("\"parameters\": { \"itemCount\": { \"type\": \"int\", \"defaultValue\": 3 } }, \"variables\": { " + variables + " }, \"resources\": [], \"outputs\": { " + outputs + " }");
    }

    private static string Output(string template, string name)
    {
        var context = new StaticEvaluator().Evaluate(Expr.Parse(template));
        Assert.DoesNotContain(context.Diagnostics, d => d.IsError);
        return Expr.Render(context.Outputs[name].Value);
    }

    [Fact]
    public void ATopLevelCopyEntry_DeclaresAnArrayVariable()
    {
        string template = Template(
            "\"copy\": [ { \"name\": \"stringArray\", \"count\": \"[parameters('itemCount')]\", \"input\": \"[concat('item', copyIndex('stringArray', 1))]\" } ]",
            "\"o\": { \"type\": \"array\", \"value\": \"[variables('stringArray')]\" }");

        Assert.Equal("[\"item1\",\"item2\",\"item3\"]", Output(template, "o"));
        Assert.DoesNotContain(Review1C.Validate(template), d => d.IsError);
    }

    [Fact]
    public void SeveralEntries_ObjectInputs_AndAZeroCount_AreSupported()
    {
        string template = Template(
            "\"copy\": [ { \"name\": \"disks\", \"count\": 2, \"input\": { \"name\": \"[concat('d', copyIndex('disks', 1))]\", \"diskIndex\": \"[copyIndex('disks')]\" } }, "
            + "{ \"name\": \"none\", \"count\": 0, \"input\": \"x\" } ], \"other\": \"[length(variables('disks'))]\"",
            "\"disks\": { \"type\": \"array\", \"value\": \"[variables('disks')]\" }, \"none\": { \"type\": \"array\", \"value\": \"[variables('none')]\" }, \"other\": { \"type\": \"int\", \"value\": \"[variables('other')]\" }");

        Assert.Equal("[{\"name\":\"d1\",\"diskIndex\":0},{\"name\":\"d2\",\"diskIndex\":1}]", Output(template, "disks"));
        Assert.Equal("[]", Output(template, "none"));
        Assert.Equal("2", Output(template, "other"));
    }

    [Fact]
    public void ACopyArrayInsideAVariable_BuildsAnArrayProperty()
    {
        string template = Template(
            "\"topLevelObject\": { \"sampleProperty\": \"sampleValue\", \"copy\": [ { \"name\": \"disks\", \"count\": \"[parameters('itemCount')]\", \"input\": { \"diskIndex\": \"[copyIndex('disks')]\" } } ] }",
            "\"o\": { \"type\": \"object\", \"value\": \"[variables('topLevelObject')]\" }");

        Assert.Equal("{\"sampleProperty\":\"sampleValue\",\"disks\":[{\"diskIndex\":0},{\"diskIndex\":1},{\"diskIndex\":2}]}", Output(template, "o"));
        Assert.DoesNotContain(Review1C.Validate(template), d => d.IsError);
    }

    [Fact]
    public void AnIterationVariable_ClashingWithAPlainVariable_IsADuplicateDefinition()
    {
        string template = Template(
            "\"items\": 1, \"copy\": [ { \"name\": \"Items\", \"count\": 1, \"input\": 1 } ]",
            "\"o\": { \"type\": \"int\", \"value\": 1 }");

        var diagnostic = Assert.Single(Review1C.Validate(template), d => d.Code == DiagnosticCodes.DuplicateDefinition);
        Assert.Equal("variables.copy[0].name", diagnostic.Path!.ToString());
    }

    [Theory]
    [InlineData("{ \"count\": 1, \"input\": 1 }", "variables.copy[0].name")]
    [InlineData("{ \"name\": \"a\", \"input\": 1 }", "variables.copy[0].count")]
    [InlineData("{ \"name\": \"a\", \"count\": 1 }", "variables.copy[0].input")]
    [InlineData("{ \"name\": \"a\", \"count\": -1, \"input\": 1 }", "variables.copy[0].count")]
    [InlineData("{ \"name\": \"a\", \"count\": 801, \"input\": 1 }", "variables")]
    public void AnInvalidEntry_IsReported(string entry, string path)
    {
        var diagnostics = Review1C.Validate(Template("\"copy\": [ " + entry + " ]", "\"o\": { \"type\": \"int\", \"value\": 1 }"));

        Assert.Contains(diagnostics, d => d.IsError && d.Path!.ToString().StartsWith(path, System.StringComparison.Ordinal));
    }

    [Fact]
    public void CopyIndexWithoutTheLoopName_OrOutsideTheInput_IsRefused()
    {
        var unnamed = Review1C.Validate(Template("\"copy\": [ { \"name\": \"a\", \"count\": 2, \"input\": \"[copyIndex('other')]\" } ]", "\"o\": { \"type\": \"int\", \"value\": 1 }"));
        var inCount = Review1C.Validate(Template("\"copy\": [ { \"name\": \"a\", \"count\": \"[copyIndex('a')]\", \"input\": 1 } ]", "\"o\": { \"type\": \"int\", \"value\": 1 }"));

        Assert.Contains(unnamed, d => d.Code == DiagnosticCodes.CopyIndexOutsideCopy);
        Assert.Contains(inCount, d => d.Code == DiagnosticCodes.CopyIndexOutsideCopy);
        Assert.True(unnamed.Concat(inCount).All(d => !d.IsError || d.Code == DiagnosticCodes.CopyIndexOutsideCopy));
    }
}
