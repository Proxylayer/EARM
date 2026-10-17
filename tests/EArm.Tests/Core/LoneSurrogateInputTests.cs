using System;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Loading;
using EArm.Core.Validation;
using Xunit;

namespace EArm.Tests;

/// <summary>
/// Lone surrogate escapes in template and parameter text: the loader replaces them with U+FFFD and reports each with its path and position, and
/// nothing later in the pipeline throws on one (split from <see cref="TemplateInputRobustnessTests"/>).
/// </summary>
public sealed class LoneSurrogateInputTests
{
    private const string Envelope = "{ \"$schema\": \"https://schema.earm.proxylayer.net/schemas/2026-10-01/deploymentTemplate.json#\", \"contentVersion\": \"1.0.0.0\", \"resources\": [] ";

    private static readonly TemplateLoader Loader = new();

    [Fact]
    public void LoneSurrogatesAndControlCharacterEscapes_AreLoaded_WithTheSurrogateReplacedAndTheControlCharactersKept()
    {
        // A lone surrogate escape is legal JSON text; the loader must not crash on it. It rewrites the surrogate to U+FFFD (the validator then reports it, see
        // ALoneSurrogateEscape_ShouldBeADiagnostic_NotAnUnhandledException); the escaped control characters survive as the characters they name.
        var document = Loader.Parse(Envelope + ", \"metadata\": { \"note\": \"\\ud800 and \\u0001 and \\u0000\" } }", "x.json", ParameterSet.Empty);

        Assert.NotNull(document.Metadata);
        Assert.Equal(["$schema", "contentVersion", "resources", "metadata"], document.TopLevelKeys);
        Assert.Equal("\uFFFD and \u0001 and \u0000", TemplateJson.AsString(document.Root["metadata"]!["note"]));
    }

    /// <summary>
    /// A string with a lone surrogate escape (<c>"\ud800"</c>) used to load and then crash the offline JSON Schema pass (JsonSchema.Net reads
    /// the string and System.Text.Json throws <see cref="InvalidOperationException"/>), ending the command with an unhandled exception. The
    /// loader now rewrites the escape to U+FFFD and reports an InvalidJson error with the path and position, and the schema pass is guarded.
    /// </summary>
    [Fact]
    public void ALoneSurrogateEscape_ShouldBeADiagnostic_NotAnUnhandledException()
    {
        var document = Loader.Parse("{ \"$schema\": \"https://schema.earm.proxylayer.net/schemas/2026-10-01/deploymentTemplate.json#\", \"contentVersion\": \"1.0.0.0\", \"resources\": [ { \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"g\", \"properties\": { \"displayName\": \"bad \\ud800 surrogate\" } } ] }", "x.json", ParameterSet.Empty);

        var diagnostics = new TemplateValidator(EArm.Core.Types.ResourceTypeCatalog.Default).Validate(document);

        Assert.NotNull(diagnostics);
        var reported = Assert.Single(diagnostics, d => d.Code == DiagnosticCodes.InvalidJson);
        Assert.Equal(DiagnosticSeverity.Error, reported.Severity);
        Assert.Contains("lone surrogate", reported.Message, StringComparison.Ordinal);
        Assert.Equal("resources[0].properties.displayName", reported.Path!.ToString());
        Assert.NotNull(reported.Location);
        Assert.Equal("bad \uFFFD surrogate", TemplateJson.AsString(document.Root["resources"]![0]!["properties"]!["displayName"]));
    }

    [Theory]
    [InlineData("\\ud800")]
    [InlineData("\\udbff")]
    [InlineData("\\udc00")]
    [InlineData("\\udfff")]
    [InlineData("a\\ud800b")]
    [InlineData("\\ud800\\ud800")]
    [InlineData("\\udc00\\ud800")]
    [InlineData("\\ud800\\u0041")]
    public void EveryShapeOfLoneSurrogate_IsReportedOnce_AndTheRestOfThePipelineStaysUp(string escaped)
    {
        var document = Loader.Parse(Envelope + ", \"metadata\": { \"note\": \"" + escaped + "\" } }", "x.json", ParameterSet.Empty);

        var diagnostics = new TemplateValidator(EArm.Core.Types.ResourceTypeCatalog.Default).Validate(document);

        Assert.Contains(diagnostics, d => d.Code == DiagnosticCodes.InvalidJson && d.Path!.ToString() == "metadata.note");
        Assert.DoesNotContain("\ud800", TemplateJson.AsString(document.Metadata!["note"])!, StringComparison.Ordinal);
        Assert.Contains("\uFFFD", TemplateJson.AsString(document.Metadata["note"])!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\\ud83d\\ude00")]
    [InlineData("\\ud83d\\ude00\\ud83d\\ude00")]
    [InlineData("\\\\ud800")]
    [InlineData("\\\\\\\\ud800")]
    public void ValidPairsAndEscapedBackslashes_AreNotLoneSurrogates(string escaped)
    {
        var document = Loader.Parse(Envelope + ", \"metadata\": { \"note\": \"" + escaped + "\" } }", "x.json", ParameterSet.Empty);

        Assert.DoesNotContain(document.LoadDiagnostics, d => d.Code == DiagnosticCodes.InvalidJson);
        Assert.NotNull(TemplateJson.AsString(document.Metadata!["note"]));
    }

    [Fact]
    public void ALoneSurrogateFollowedByALetter_IsLone()
    {
        var document = Loader.Parse(Envelope + ", \"metadata\": { \"note\": \"\\ud83dx\" } }", "x.json", ParameterSet.Empty);

        Assert.Contains(document.LoadDiagnostics, d => d.Code == DiagnosticCodes.InvalidJson);
    }

    [Fact]
    public void LoneSurrogatesInSeveralPlaces_AreEachReported_WithTheirOwnPaths()
    {
        string json = Envelope + ", \"metadata\": { \"a\": \"\\ud800\", \"list\": [ \"ok\", \"x\\udc00\" ], \"b\\ud800\": 1 } }";

        var document = Loader.Parse(json, "x.json", ParameterSet.Empty);

        var paths = document.LoadDiagnostics.Where(d => d.Code == DiagnosticCodes.InvalidJson).Select(d => d.Path!.ToString()).ToList();
        Assert.Equal(["metadata.a", "metadata.list[1]", "metadata['b\uFFFD']"], paths);
    }

    [Fact]
    public void ALoneSurrogateEscapeInAComment_IsIgnored()
    {
        var document = Loader.Parse("// \\ud800 in a comment\n" + Envelope + ", \"metadata\": { \"note\": \"fine\" /* \\udc00 */ } }", "x.json", ParameterSet.Empty);

        Assert.DoesNotContain(document.LoadDiagnostics, d => d.Code == DiagnosticCodes.InvalidJson);
    }

    [Fact]
    public void ALoneSurrogateEscape_AfterABom_ReportsTheColumnOfTheEscape()
    {
        byte[] bom = [0xEF, 0xBB, 0xBF];
        string text = Envelope + ", \"metadata\": { \"note\": \"ü\\ud800\" } }";

        var document = Loader.Parse(bom.Concat(Encoding.UTF8.GetBytes(text)).ToArray(), "x.json", ParameterSet.Empty);

        var diagnostic = Assert.Single(document.LoadDiagnostics, d => d.Code == DiagnosticCodes.InvalidJson);
        Assert.Equal(1, diagnostic.Location!.Value.Line);
        Assert.Equal(text.IndexOf("\\ud800", StringComparison.Ordinal) + 1, diagnostic.Location.Value.Column);
    }

    [Fact]
    public void ALoneSurrogateInAParameterFile_IsALoadExceptionWithPositionAndPath()
    {
        var ex = Assert.Throws<TemplateLoadException>(() => ParameterSet.Parse("{ \"parameters\": { \"p\": { \"value\": \"a\\ud800b\" } } }", "p.json"));

        Assert.Equal(DiagnosticCodes.InvalidJson, ex.Code);
        Assert.Contains("lone surrogate", ex.Message, StringComparison.Ordinal);
        Assert.Equal("parameters.p.value", ex.Path!.ToString());
        Assert.NotNull(ex.Location);
        Assert.Equal("p.json", ex.Source);
    }

    [Fact]
    public void LoneSurrogates_Replace_RewritesInPlaceKeepingTheLength_AndLeavesCleanTextAlone()
    {
        string clean = "{\"a\":\"\\ud83d\\ude00 \\u00e9\"}";
        Assert.Same(clean, LoneSurrogates.Replace(clean));

        string dirty = "{\"a\":\"x\\ud800y\",\"b\":[\"\\udc00\"]}";
        string replaced = LoneSurrogates.Replace(dirty);

        Assert.Equal(dirty.Length, replaced.Length);
        Assert.Equal("{\"a\":\"x\\ufffdy\",\"b\":[\"\\ufffd\"]}", replaced);
        Assert.Equal("x\uFFFDy", JsonNode.Parse(replaced)!["a"]!.GetValue<string>());
    }

    [Fact]
    public void LoneSurrogates_Replace_OnTextThatIsNotJson_DoesNotThrow()
    {
        Assert.Equal("not json \\ud800", LoneSurrogates.Replace("not json \\ud800"));
        Assert.Equal("{\"a\": \"\\ufffd\" nope", LoneSurrogates.Replace("{\"a\": \"\\ud800\" nope"));
    }

    [Fact]
    public void CanonicalJson_NeverThrowsOnALoneSurrogate_FromParsedOrBuiltNodes()
    {
        // A node parsed straight from text with a lone surrogate escape (not through the loader) used to throw on the first read.
        var parsed = JsonNode.Parse("{\"k\":\"x\\ud800y\",\"list\":[\"\\udc00\"]}")!;
        var built = new JsonObject { ["k"] = JsonValue.Create("x" + (char)0xD800 + "y"), ["list"] = new JsonArray(JsonValue.Create(((char)0xDC00).ToString())) };

        string fromParsed = CanonicalJson.Serialize(parsed);
        string fromBuilt = CanonicalJson.Serialize(built);

        Assert.Equal("{\"k\":\"x�y\",\"list\":[\"�\"]}", fromParsed);
        Assert.Equal(fromParsed, fromBuilt);
        Assert.Equal(CanonicalJson.Hash(parsed), CanonicalJson.Hash(built));
    }
}
