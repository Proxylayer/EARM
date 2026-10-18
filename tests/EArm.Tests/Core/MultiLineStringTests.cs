using System.Linq;
using System.Text;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using Xunit;

namespace EArm.Tests;

/// <summary>
/// Multi-line strings, as Azure CLI and PowerShell accept them in ARM templates
/// (https://learn.microsoft.com/azure/azure-resource-manager/templates/syntax#multi-line-strings): a raw line break inside a string.
/// </summary>
public sealed class MultiLineStringTests
{
    private const string Template = "{\n  \"$schema\": \"" + Review1C.SchemaUri + "\",\n  \"contentVersion\": \"1.0.0.0\",\n"
        + "  \"parameters\": { \"env\": { \"type\": \"string\", \"defaultValue\": \"dev\" } },\n"
        + "  \"variables\": {\n    \"name\": \"[\n      format('g-{0}',\n        parameters('env'))\n    ]\",\n    \"text\": \"line one\nline two\"\n  },\n"
        + "  // a comment with \"quotes\n"
        + "  \"resources\": [],\n  \"outputs\": { \"o\": { \"type\": \"string\", \"value\": \"[variables('name')]\" }, \"bad\": { \"type\": \"strin\", \"value\": \"x\" } }\n}";

    [Fact]
    public void AnExpressionSpreadOverSeveralLines_IsEvaluated_AndALiteralKeepsItsLineBreak()
    {
        var context = new EArm.Expressions.Static.StaticEvaluator().Evaluate(Expr.Parse(Template));

        Assert.Equal("\"g-dev\"", Expr.Render(context.Outputs["o"].Value));
        Assert.Equal("line one\nline two", context.Variables.Get("text")!.GetValue<string>());
    }

    [Fact]
    public void Locations_AfterAMultiLineString_PointAtTheTextAsWritten()
    {
        var diagnostics = Review1C.Validate(Template);

        var bad = Assert.Single(diagnostics, d => d.Code == DiagnosticCodes.InvalidOutputType);
        int expectedLine = Template.Split('\n').ToList().FindIndex(l => l.Contains("\"bad\"", System.StringComparison.Ordinal)) + 1;
        Assert.Equal(expectedLine, bad.Location!.Value.Line);
        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.InvalidJson);
    }

    [Fact]
    public void Escape_LeavesStrictJsonUntouched_AndEscapesOnlyInsideStrings()
    {
        byte[] strict = Encoding.UTF8.GetBytes("{\n \"a\": \"b\\n\" // x\n}");
        var same = MultiLineStrings.Escape(strict, out var none);

        Assert.Empty(none);
        Assert.Equal(strict, same.ToArray());

        var escaped = MultiLineStrings.Escape(Encoding.UTF8.GetBytes("{ \"a\": \"x\r\n\ty\", \"b\": \"p\tq\" }"), out var insertions);
        Assert.Equal("{ \"a\": \"x\\r\\n\\ty\", \"b\": \"p\tq\" }", Encoding.UTF8.GetString(escaped.Span));
        Assert.Equal(3, insertions.Count);
    }

    [Fact]
    public void ARawTab_InASingleLineString_IsStillInvalidJson()
    {
        var ex = Assert.Throws<EArm.Core.Loading.TemplateLoadException>(() => Expr.Parse("{ \"$schema\": \"" + Review1C.SchemaUri + "\", \"contentVersion\": \"1.0.0.0\", \"resources\": [], \"metadata\": { \"a\": \"p\tq\" } }"));

        Assert.Equal(DiagnosticCodes.InvalidJson, ex.Code);
    }
}
