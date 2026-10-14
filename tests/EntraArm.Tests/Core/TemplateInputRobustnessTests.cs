using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EntraArm.Core;
using EntraArm.Core.Diagnostics;
using EntraArm.Core.Json;
using EntraArm.Core.Loading;
using EntraArm.Core.Validation;
using Xunit;

namespace EntraArm.Tests;

/// <summary>
/// Unusual template inputs: a byte-order mark, an empty or whitespace file, comments, trailing commas, duplicate keys,
/// huge or odd numbers, very deep nesting, lone surrogates and control characters, through the loader and the
/// validator. The contract: either a document, or a <see cref="TemplateLoadException"/> with a diagnostic code and
/// (when the position is known) a location, never another exception type.
/// </summary>
public sealed class TemplateInputRobustnessTests
{
    private const string Envelope = "{ \"$schema\": \"https://entraarm.dev/schemas/2026-10-01/deploymentTemplate.json#\", \"contentVersion\": \"1.0.0.0\", \"resources\": [] ";

    private static readonly TemplateLoader Loader = new();

    private static TemplateDocument Parse(byte[] utf8)
    {
        return Loader.Parse(utf8, "input.entra.json", ParameterSet.Empty);
    }

    private static byte[] Bytes(string text)
    {
        return Encoding.UTF8.GetBytes(text);
    }

    // ---- loader: files ----

    [Fact]
    public void ABom_IsStripped_ForUtf8Documents()
    {
        var withBom = Parse([.. Encoding.UTF8.GetPreamble(), .. Bytes(Envelope + "}")]);
        var without = Parse(Bytes(Envelope + "}"));

        Assert.Equal(without.ContentVersion, withBom.ContentVersion);
        Assert.Empty(withBom.Resources);
        Assert.Equal(without.TopLevelKeys, withBom.TopLevelKeys);
    }

    [Fact]
    public void ABomOnlyOrEmptyFile_IsAnInvalidJsonLoadException()
    {
        foreach (byte[] bytes in new[] { Array.Empty<byte>(), Encoding.UTF8.GetPreamble().ToArray(), Bytes("   \r\n  "), Bytes("\t") })
        {
            var ex = Assert.Throws<TemplateLoadException>(() => Parse(bytes));

            Assert.Equal(DiagnosticCodes.InvalidJson, ex.Code);
            Assert.Equal("input.entra.json", ex.Source);
        }
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    [InlineData("true")]
    public void ARootThatIsNotAnObject_IsTemplateRootNotObject(string json)
    {
        var ex = Assert.Throws<TemplateLoadException>(() => Loader.Parse(json, "x.json", ParameterSet.Empty));

        Assert.Equal(DiagnosticCodes.TemplateRootNotObject, ex.Code);
    }

    [Theory]
    [InlineData("{ \"a\": ")]
    [InlineData("{ \"a\": 1 \"b\": 2 }")]
    [InlineData("{ a: 1 }")]
    [InlineData("{ \"a\": 'single' }")]
    [InlineData("{ \"a\": NaN }")]
    [InlineData("{ \"a\": Infinity }")]
    [InlineData("{ \"a\": 01 }")]
    [InlineData("{ \"a\": .5 }")]
    [InlineData("{ \"a\": 1.e3 }")]
    [InlineData("{ \"a\": \"unterminated }")]
    [InlineData("{ \"a\": \"bad \\q escape\" }")]
    [InlineData("{ \"a\": \"bad \\u12 escape\" }")]
    [InlineData("{ \"a\": \"raw\ttab\" }")]
    [InlineData("{ \"a\": 1 } trailing")]
    [InlineData("{ \"a\": 1 }{ \"b\": 2 }")]
    [InlineData("{ /* unterminated comment ")]
    [InlineData("\u0000{ }")]
    public void MalformedJson_IsAnInvalidJsonLoadException_WithAPosition(string json)
    {
        var ex = Assert.Throws<TemplateLoadException>(() => Loader.Parse(json, "x.json", ParameterSet.Empty));

        Assert.Equal(DiagnosticCodes.InvalidJson, ex.Code);
        Assert.IsAssignableFrom<JsonException>(ex.InnerException);
        Assert.Contains("not valid JSON", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CommentsAndTrailingCommas_AreAccepted()
    {
        var document = Loader.Parse("// leading comment\n{ /* inline */ \"$schema\": \"https://entraarm.dev/schemas/2026-10-01/deploymentTemplate.json#\", \"contentVersion\": \"1.0.0.0\", \"resources\": [ ], }", "x.json", ParameterSet.Empty);

        Assert.Equal("1.0.0.0", document.ContentVersion);
    }

    [Fact]
    public void InvalidUtf8Bytes_AndUtf16Files_AreInvalidJson_NotACrash()
    {
        Assert.Throws<TemplateLoadException>(() => Parse([0xFF, 0xFE, 0x7B, 0x00, 0x7D, 0x00]));
        Assert.Throws<TemplateLoadException>(() => Parse([0xC3, 0x28, 0x7B, 0x7D]));
    }

    [Fact]
    public void InvalidUtf8InsideAString_IsEitherRejectedAtLoad_OrLoadsAsAStringTheLaterStagesCanRead()
    {
        byte[] bytes = [.. Bytes(Envelope + ", \"metadata\": { \"note\": \""), 0xC3, 0x28, .. Bytes("\" } }")];

        try
        {
            var document = Parse(bytes);
            Assert.NotNull(document.Metadata);
        }
        catch (TemplateLoadException ex)
        {
            Assert.Equal(DiagnosticCodes.InvalidJson, ex.Code);
        }
    }

    [Fact]
    public void LoneSurrogatesAndControlCharacterEscapes_RoundTripOrFailCleanly()
    {
        // A lone surrogate escape is legal JSON text; the reader must not crash on it.
        var document = Loader.Parse(Envelope + ", \"metadata\": { \"note\": \"\\ud800 and \\u0001 and \\u0000\" } }", "x.json", ParameterSet.Empty);

        Assert.NotNull(document.Metadata);
        Assert.Equal(["$schema", "contentVersion", "resources", "metadata"], document.TopLevelKeys);
    }

    /// <summary>
    /// A string with a lone surrogate escape (<c>"\ud800"</c>) used to load and then crash the offline JSON Schema pass (JsonSchema.Net reads
    /// the string and System.Text.Json throws <see cref="InvalidOperationException"/>), ending the command with an unhandled exception. The
    /// loader now rewrites the escape to U+FFFD and reports an InvalidJson error with the path and position, and the schema pass is guarded.
    /// </summary>
    [Fact]
    public void ALoneSurrogateEscape_ShouldBeADiagnostic_NotAnUnhandledException()
    {
        var document = Loader.Parse("{ \"$schema\": \"https://entraarm.dev/schemas/2026-10-01/deploymentTemplate.json#\", \"contentVersion\": \"1.0.0.0\", \"resources\": [ { \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01\", \"name\": \"g\", \"properties\": { \"displayName\": \"bad \\ud800 surrogate\" } } ] }", "x.json", ParameterSet.Empty);

        var diagnostics = new TemplateValidator(EntraArm.Core.Types.ResourceTypeCatalog.Default).Validate(document);

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

        var diagnostics = new TemplateValidator(EntraArm.Core.Types.ResourceTypeCatalog.Default).Validate(document);

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

    // ---- numbers, keys, depth ----

    [Fact]
    public void HugeAndOddNumbers_LoadWithoutLosingTheDocument()
    {
        string json = Envelope + ", \"metadata\": { \"big\": 123456789012345678901234567890, \"neg\": -0, \"tiny\": 1E-400, \"huge\": 1E+400, \"exp\": 1.5e3, \"long\": 9223372036854775807, \"over\": 9223372036854775808 } }";

        var document = Loader.Parse(json, "x.json", ParameterSet.Empty);

        var metadata = Assert.IsType<JsonObject>(document.Metadata);
        Assert.Equal(7, metadata.Count);
        Assert.Equal(9223372036854775807L, TemplateJson.AsInt64(metadata["long"]));
        Assert.Null(TemplateJson.AsInt64(metadata["over"]));
        Assert.Null(TemplateJson.AsInt64(metadata["big"]));
        Assert.Equal(0L, TemplateJson.AsInt64(metadata["neg"]));
        Assert.Null(TemplateJson.AsInt64(metadata["huge"]));
        Assert.Null(TemplateJson.AsInt64(metadata["tiny"]));
    }

    /// <summary>
    /// DEFECT (low severity, not fixed here: src/ is out of scope for the test pass). TemplateJson.AsInt64 (src/EntraArm.Core/Json/TemplateJson.cs:104)
    /// is documented as "the integral value of a leaf node", but for a parsed (JsonElement-backed) number it only accepts integer syntax:
    /// <c>3.0</c> and <c>1.5e3</c> give <c>null</c>, while the same values held as CLR doubles (<c>JsonValue.Create(3.0)</c>) give 3 and 1500.
    /// Expected: the same answer for the same number however it was created. Actual: parsed 3.0 / 1.5e3 are "not integral".
    /// </summary>
    [Fact(Skip = "DEFECT: AsInt64 treats parsed 3.0 / 1.5e3 as non-integral but CLR double 3.0 as 3 (TemplateJson.cs:104).")]
    public void Defect_AsInt64_ParsedIntegralFloatsAreNotIntegral()
    {
        var document = Loader.Parse(Envelope + ", \"metadata\": { \"three\": 3.0, \"exp\": 1.5e3 } }", "x.json", ParameterSet.Empty);

        Assert.Equal(TemplateJson.AsInt64(JsonValue.Create(3.0)), TemplateJson.AsInt64(document.Metadata!["three"]));
        Assert.Equal(1500L, TemplateJson.AsInt64(document.Metadata["exp"]));
    }

    [Fact]
    public void DuplicateObjectKeys_DoNotCrashTheLoader()
    {
        string json = Envelope + ", \"metadata\": { \"k\": 1, \"k\": 2, \"K\": 3 } }";

        try
        {
            var document = Loader.Parse(json, "x.json", ParameterSet.Empty);
            Assert.NotNull(document.Metadata);
        }
        catch (TemplateLoadException ex)
        {
            Assert.False(string.IsNullOrEmpty(ex.Code));
        }
    }

    [Fact]
    public void DeeplyNestedJson_IsALoadException_AtTheReaderDepthLimit_AndModerateDepthLoads()
    {
        string nested(int depth)
        {
            return string.Concat(Enumerable.Repeat("{ \"n\": ", depth)) + "1" + string.Concat(Enumerable.Repeat(" }", depth));
        }

        var moderate = Loader.Parse(Envelope + ", \"metadata\": " + nested(30) + " }", "x.json", ParameterSet.Empty);
        Assert.NotNull(moderate.Metadata);

        var ex = Assert.Throws<TemplateLoadException>(() => Loader.Parse(Envelope + ", \"metadata\": " + nested(500) + " }", "x.json", ParameterSet.Empty));
        Assert.Equal(DiagnosticCodes.InvalidJson, ex.Code);
        Assert.Contains("depth", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AVeryLongString_AndManyResources_Load()
    {
        string longText = new('x', 2_000_000);
        var document = Loader.Parse(Envelope + ", \"metadata\": { \"long\": \"" + longText + "\" } }", "x.json", ParameterSet.Empty);
        Assert.Equal(2_000_000, ((string?)document.Metadata!["long"]!.GetValue<string>())!.Length);

        string resources = string.Join(",", Enumerable.Range(0, 400).Select(i => "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01\", \"name\": \"g" + i + "\", \"properties\": { \"displayName\": \"G" + i + "\" } }"));
        var many = Loader.Parse("{ \"$schema\": \"https://entraarm.dev/schemas/2026-10-01/deploymentTemplate.json#\", \"contentVersion\": \"1.0.0.0\", \"resources\": [ " + resources + " ] }", "x.json", ParameterSet.Empty);
        Assert.Equal(400, many.Resources.Count);
    }

    // ---- loader: file API ----

    [Fact]
    public async Task LoadAsync_ReportsAMissingFile_AndRejectsBlankPaths()
    {
        string missing = Path.Combine(Path.GetTempPath(), "entraarm-missing-" + Guid.NewGuid().ToString("N") + ".entra.json");

        var ex = await Assert.ThrowsAsync<TemplateLoadException>(() => Loader.LoadAsync(missing, ParameterSet.Empty, TestContext.Current.CancellationToken));

        Assert.Equal(DiagnosticCodes.FileNotFound, ex.Code);
        Assert.Contains(missing, ex.Message, StringComparison.Ordinal);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Loader.LoadAsync(" ", ParameterSet.Empty, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Loader.LoadAsync(missing, null!, TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentNullException>(() => Loader.Parse((string)null!, null, ParameterSet.Empty));
        Assert.Throws<ArgumentNullException>(() => Loader.Parse("{}", null, null!));
        Assert.Throws<ArgumentNullException>(() => new TemplateLoader(null!));
    }

    [Fact]
    public async Task LoadAsync_ADirectoryInsteadOfAFile_IsNotAFileNotFoundCrash()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "template-robustness-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        try
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => Loader.LoadAsync(directory, ParameterSet.Empty, TestContext.Current.CancellationToken));

            Assert.True(ex is TemplateLoadException or UnauthorizedAccessException or IOException, ex.GetType().Name);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---- structure: duplicates and shapes ----

    [Fact]
    public void DuplicateParametersAndOutputs_AreReportedCaseInsensitively_AndNonObjectDefinitionsAreSkipped()
    {
        string json = Envelope.Replace("\"resources\": [] ", string.Empty, StringComparison.Ordinal)
            + "\"parameters\": { \"Name\": { \"type\": \"string\" }, \"name\": { \"type\": \"string\" }, \"broken\": 5 }, "
            + "\"outputs\": { \"Out\": { \"type\": \"string\", \"value\": \"a\" }, \"out\": { \"type\": \"string\", \"value\": \"b\" }, \"weird\": [] }, "
            + "\"resources\": [], \"functions\": [ 7, { \"namespace\": \"f\", \"members\": { \"m\": 1 } } ] }";

        var document = Loader.Parse(json, "x.json", ParameterSet.Empty);

        Assert.Equal(2, document.LoadDiagnostics.Count(d => d.Code == DiagnosticCodes.DuplicateDefinition));
        Assert.Contains(document.LoadDiagnostics, d => d.Message.Contains("Parameter 'name'", StringComparison.Ordinal));
        Assert.Contains(document.LoadDiagnostics, d => d.Message.Contains("Output 'out'", StringComparison.Ordinal));
        Assert.Single(document.Parameters, p => p.Key.Equals("broken", StringComparison.OrdinalIgnoreCase) is false);
    }

    [Fact]
    public void NestedChildren_WithAMismatchedFullType_AreReported()
    {
        string json = "{ \"$schema\": \"https://entraarm.dev/schemas/2026-10-01/deploymentTemplate.json#\", \"contentVersion\": \"1.0.0.0\", \"resources\": [ "
            + "{ \"type\": \"Microsoft.Entra/applications\", \"apiVersion\": \"2026-10-01\", \"name\": \"app\", \"properties\": { \"displayName\": \"App\" }, \"resources\": [ "
            + "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01\", \"name\": \"child\", \"properties\": { } } ] } ] }";

        var document = Loader.Parse(json, "x.json", ParameterSet.Empty);

        Assert.Contains(document.LoadDiagnostics, d => d.Code == DiagnosticCodes.ParentChildTypeMismatch);
    }

    // ---- TemplateJson helpers ----

    [Fact]
    public void TemplateJsonHelpers_AreStrictAboutKinds()
    {
        Assert.Null(TemplateJson.AsString(null));
        Assert.Null(TemplateJson.AsString(JsonValue.Create(5)));
        Assert.Equal("x", TemplateJson.AsString(JsonValue.Create("x")));
        Assert.Null(TemplateJson.AsBool(JsonValue.Create("true")));
        Assert.Null(TemplateJson.AsBool(null));
        Assert.True(TemplateJson.AsBool(JsonValue.Create(true)));
        Assert.False(TemplateJson.AsBool(JsonValue.Create(false)));
        Assert.Null(TemplateJson.AsInt64(JsonValue.Create("5")));
        Assert.Null(TemplateJson.AsInt64(null));
        Assert.Equal(5L, TemplateJson.AsInt64(JsonValue.Create(5)));
        Assert.Equal(5L, TemplateJson.AsInt64(JsonValue.Create(5L)));
        Assert.Equal(3L, TemplateJson.AsInt64(JsonValue.Create(3.0)));
        Assert.Null(TemplateJson.AsInt64(JsonValue.Create(3.5)));
        Assert.Null(TemplateJson.AsInt64(JsonValue.Create(1e20)));
        Assert.Null(TemplateJson.AsInt64(JsonValue.Create(9007199254740992d)));
        Assert.Equal(9007199254740991L, TemplateJson.AsInt64(JsonValue.Create(9007199254740991d)));
        Assert.Null(TemplateJson.LiteralString(null));
        Assert.Equal("plain", TemplateJson.LiteralString(JsonValue.Create("plain")));
        Assert.Null(TemplateJson.LiteralString(JsonValue.Create("[concat('a','b')]")));
        Assert.Equal("[literal", TemplateJson.LiteralString(JsonValue.Create("[[literal")));
        Assert.True(TemplateJson.IsExpression(JsonValue.Create("[parameters('a')]")));
        Assert.False(TemplateJson.IsExpression(JsonValue.Create("[[escaped]")));
        Assert.False(TemplateJson.IsExpression(JsonValue.Create(5)));
    }

    [Fact]
    public void TemplateJson_ToNode_MapsEveryKind_AndLocationOfNeedsAPosition()
    {
        using var document = JsonDocument.Parse("[null, true, 1, \"s\", [], {}]");
        var nodes = document.RootElement.EnumerateArray().Select(TemplateJson.ToNode).ToList();

        Assert.Null(nodes[0]);
        Assert.IsAssignableFrom<JsonValue>(nodes[1]);
        Assert.IsAssignableFrom<JsonValue>(nodes[2]);
        Assert.IsAssignableFrom<JsonValue>(nodes[3]);
        Assert.IsType<JsonArray>(nodes[4]);
        Assert.IsType<JsonObject>(nodes[5]);

        var positioned = Assert.Throws<TemplateLoadException>(() => Loader.Parse("{\n  \"a\": ,\n}", null, ParameterSet.Empty)).Location;
        Assert.NotNull(positioned);
        Assert.Equal(2, positioned.Value.Line);
        Assert.True(positioned.Value.Column >= 1);
        var jsonException = Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse("{").Dispose());
        Assert.NotNull(TemplateJson.LocationOf(jsonException));
        Assert.Null(TemplateJson.LocationOf(new JsonException("no position")));
    }

    // ---- validator ----

    [Fact]
    public void Validator_OnAnEmptyResourcesArrayAndMinimalDocument_ProducesNoErrors()
    {
        var document = Loader.Parse(Envelope + "}", "x.json", ParameterSet.Empty);

        var diagnostics = new TemplateValidator(Loader is null ? throw new InvalidOperationException() : EntraArm.Core.Types.ResourceTypeCatalog.Default).Validate(document);

        Assert.DoesNotContain(diagnostics, d => d.IsError);
    }
}
