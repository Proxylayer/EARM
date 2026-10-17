using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Loading;
using EArm.Core.Validation;
using Xunit;

namespace EArm.Tests;

/// <summary>
/// Unusual template inputs: a byte-order mark, an empty or whitespace file, comments, trailing commas, duplicate keys,
/// huge or odd numbers, very deep nesting, lone surrogates and control characters, through the loader and the
/// validator. The contract: either a document, or a <see cref="TemplateLoadException"/> with a diagnostic code and
/// (when the position is known) a location, never another exception type.
/// </summary>
public sealed class TemplateInputRobustnessTests
{
    private const string Envelope = "{ \"$schema\": \"https://schema.earm.proxylayer.net/schemas/2026-10-01/deploymentTemplate.json#\", \"contentVersion\": \"1.0.0.0\", \"resources\": [] ";

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
        var document = Loader.Parse("// leading comment\n{ /* inline */ \"$schema\": \"https://schema.earm.proxylayer.net/schemas/2026-10-01/deploymentTemplate.json#\", \"contentVersion\": \"1.0.0.0\", \"resources\": [ ], }", "x.json", ParameterSet.Empty);

        Assert.Equal("1.0.0.0", document.ContentVersion);
    }

    [Fact]
    public void InvalidUtf8Bytes_AndUtf16Files_AreInvalidJson_NotACrash()
    {
        Assert.Throws<TemplateLoadException>(() => Parse([0xFF, 0xFE, 0x7B, 0x00, 0x7D, 0x00]));
        Assert.Throws<TemplateLoadException>(() => Parse([0xC3, 0x28, 0x7B, 0x7D]));
    }

    [Fact]
    public void InvalidUtf8InsideAString_IsAnInvalidJsonLoadException_WithTheByteOffset()
    {
        // Review 4L defect, fixed in review 5M: the loader used to accept a string whose bytes are not valid UTF-8 (0xC3 followed by 0x28) and the first stage that read the
        // string as text threw InvalidOperationException ("Unexpected error (InvalidOperationException)", exit 1). Strict UTF-8 decoding now refuses the document up front, as
        // it already did for invalid bytes outside a string (InvalidUtf8Bytes_AndUtf16Files_AreInvalidJson_NotACrash), with the offset of the malformed sequence.
        byte[] prefix = Bytes(Envelope + ", \"metadata\": { \"note\": \"");
        byte[] bytes = [.. prefix, 0xC3, 0x28, .. Bytes("\" } }")];

        var ex = Assert.Throws<TemplateLoadException>(() => Parse(bytes));

        Assert.Equal(DiagnosticCodes.InvalidJson, ex.Code);
        Assert.Equal("input.entra.json", ex.Source);
        Assert.Contains("not valid JSON", ex.Message, StringComparison.Ordinal);
        Assert.Contains("valid UTF-8", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"byte offset {prefix.Length}", ex.Message, StringComparison.Ordinal);
        Assert.Contains("0xC3 0x28", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(ex.Location);
        Assert.Equal(1, ex.Location!.Value.Line);
        Assert.Equal(prefix.Length + 1, ex.Location.Value.Column);
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
    /// <c>TemplateJson.AsInt64</c> depends only on a number's exact value, never on how the node was created: any JSON number that is a
    /// whole number inside the <see cref="long"/> range yields it (<c>3.0</c>, <c>1.5e3</c>, <c>-0.0</c>, <c>1e2</c>), and fractions,
    /// out-of-range values and non-numbers yield <c>null</c>. The check reads the decimal digits, so large integers do not lose precision.
    /// </summary>
    [Theory]
    [InlineData("3", 3L)]
    [InlineData("3.0", 3L)]
    [InlineData("-0.0", 0L)]
    [InlineData("-0", 0L)]
    [InlineData("0.000", 0L)]
    [InlineData("0e999999999999", 0L)]
    [InlineData("1.5e3", 1500L)]
    [InlineData("1E+2", 100L)]
    [InlineData("1e2", 100L)]
    [InlineData("30e-1", 3L)]
    [InlineData("0.1e1", 1L)]
    [InlineData("100000e-5", 1L)]
    [InlineData("1500.000", 1500L)]
    [InlineData("-2.5e1", -25L)]
    [InlineData("1e18", 1_000_000_000_000_000_000L)]
    [InlineData("9007199254740993", 9007199254740993L)]
    [InlineData("9007199254740993.0", 9007199254740993L)]
    [InlineData("90071992547409930e-1", 9007199254740993L)]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("9223372036854775807.0", long.MaxValue)]
    [InlineData("-9223372036854775808", long.MinValue)]
    [InlineData("-9223372036854775808.0", long.MinValue)]
    [InlineData("-9.223372036854775808e18", long.MinValue)]
    [InlineData("9223372036854775808", null)]
    [InlineData("9223372036854775808.0", null)]
    [InlineData("-9223372036854775809", null)]
    [InlineData("9.223372036854775808e18", null)]
    [InlineData("1e19", null)]
    [InlineData("1E+400", null)]
    [InlineData("1e-400", null)]
    [InlineData("123456789012345678901234567890", null)]
    [InlineData("1.5", null)]
    [InlineData("3.5", null)]
    [InlineData("-0.5", null)]
    [InlineData("1e-1", null)]
    [InlineData("15e-1", null)]
    [InlineData("3.0000000000000000000000000000001", null)]
    [InlineData("9007199254740993.5", null)]
    [InlineData("\"3\"", null)]
    [InlineData("true", null)]
    [InlineData("null", null)]
    [InlineData("[3]", null)]
    [InlineData("{}", null)]
    public void AsInt64_IsValueBased_ForParsedNumbers(string json, long? expected)
    {
        Assert.Equal(expected, TemplateJson.AsInt64(JsonNode.Parse(json)));
    }

    [Fact]
    public void AsInt64_ParsedIntegralFloatsAreIntegral_ThroughTheLoader()
    {
        var document = Loader.Parse(Envelope + ", \"metadata\": { \"three\": 3.0, \"exp\": 1.5e3, \"negZero\": -0.0, \"hundred\": 1e2, \"half\": 1.5, \"over\": 1e19 } }", "x.json", ParameterSet.Empty);

        var metadata = document.Metadata!;
        Assert.Equal(TemplateJson.AsInt64(JsonValue.Create(3.0)), TemplateJson.AsInt64(metadata["three"]));
        Assert.Equal(3L, TemplateJson.AsInt64(metadata["three"]));
        Assert.Equal(1500L, TemplateJson.AsInt64(metadata["exp"]));
        Assert.Equal(0L, TemplateJson.AsInt64(metadata["negZero"]));
        Assert.Equal(100L, TemplateJson.AsInt64(metadata["hundred"]));
        Assert.Null(TemplateJson.AsInt64(metadata["half"]));
        Assert.Null(TemplateJson.AsInt64(metadata["over"]));
    }

    /// <summary>The CLR-backed numbers (what code builds with <c>JsonValue.Create</c>) agree with the parsed ones.</summary>
    [Fact]
    public void AsInt64_AgreesBetweenClrBackedAndParsedNumbers()
    {
        string[] texts = ["3", "3.0", "-0.0", "1500", "1.5e3", "1e2", "1e18", "9007199254740992", "1.5", "-0.25", "1e19", "9223372036854775808", "-9223372036854775808", "1e300"];
        foreach (var text in texts)
        {
            long? parsed = TemplateJson.AsInt64(JsonNode.Parse(text));
            double asDouble = double.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

            Assert.True(parsed == TemplateJson.AsInt64(JsonValue.Create(asDouble)), $"double {text}");
            if (decimal.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var asDecimal))
            {
                Assert.True(parsed == TemplateJson.AsInt64(JsonValue.Create(asDecimal)), $"decimal {text}");
            }
        }

        Assert.Equal(9007199254740992L, TemplateJson.AsInt64(JsonValue.Create(9007199254740992d)));
        Assert.Null(TemplateJson.AsInt64(JsonValue.Create(double.NaN)));
        Assert.Null(TemplateJson.AsInt64(JsonValue.Create(double.PositiveInfinity)));
        Assert.Null(TemplateJson.AsInt64(JsonValue.Create(double.NegativeInfinity)));
        Assert.Equal(3L, TemplateJson.AsInt64(JsonValue.Create(3f)));
        Assert.Null(TemplateJson.AsInt64(JsonValue.Create(3.5f)));
        Assert.Equal(7L, TemplateJson.AsInt64(JsonValue.Create((byte)7)));
        Assert.Equal(-7L, TemplateJson.AsInt64(JsonValue.Create((sbyte)-7)));
        Assert.Equal(7L, TemplateJson.AsInt64(JsonValue.Create((short)7)));
        Assert.Equal(7L, TemplateJson.AsInt64(JsonValue.Create((ushort)7)));
        Assert.Equal(7L, TemplateJson.AsInt64(JsonValue.Create(7u)));
        Assert.Equal(7L, TemplateJson.AsInt64(JsonValue.Create(7UL)));
        Assert.Null(TemplateJson.AsInt64(JsonValue.Create(ulong.MaxValue)));
        Assert.Equal(3L, TemplateJson.AsInt64(JsonValue.Create(3.0m)));
        Assert.Null(TemplateJson.AsInt64(JsonValue.Create(3.5m)));
        Assert.Null(TemplateJson.AsInt64(JsonValue.Create(79228162514264337593543950335m)));
    }

    [Theory]
    [InlineData("3", true)]
    [InlineData("3.0", true)]
    [InlineData("1.5e3", true)]
    [InlineData("-0.0", true)]
    [InlineData("3.5", false)]
    [InlineData("1e19", false)]
    [InlineData("9223372036854775808", false)]
    [InlineData("\"3\"", false)]
    public void ACopyCountAndBatchSize_AcceptAnyWholeNumberSpelling(string number, bool accepted)
    {
        string resource = "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"g\", \"copy\": { \"name\": \"c\", \"count\": " + number + ", \"batchSize\": " + number + " } }";
        var document = Loader.Parse(Envelope.Replace("\"resources\": []", "\"resources\": [ " + resource + " ]", StringComparison.Ordinal) + "}", "x.json", ParameterSet.Empty);

        var diagnostics = new TemplateValidator(EArm.Core.Types.ResourceTypeCatalog.Default).Validate(document);

        var count = diagnostics.Where(d => d.Code == DiagnosticCodes.InvalidCopy && d.Path?.ToString() == "resources[0].copy.count").ToList();
        var batch = diagnostics.Where(d => d.Code == DiagnosticCodes.InvalidCopy && d.Path?.ToString() == "resources[0].copy.batchSize").ToList();
        if (accepted)
        {
            Assert.Empty(count);
            // A zero-valued number is a valid count but not a valid batch size (must be positive).
            Assert.Equal(number == "-0.0" ? 1 : 0, batch.Count);
        }
        else
        {
            Assert.Single(count);
            Assert.Single(batch);
        }
    }

    [Fact]
    public void DuplicateObjectKeys_AreRejectedByTheLoader_WithTheKeyPathAndPosition()
    {
        string json = Envelope + ", \"metadata\": { \"k\": 1, \"k\": 2, \"K\": 3 } }";

        var ex = Assert.Throws<TemplateLoadException>(() => Loader.Parse(json, "x.json", ParameterSet.Empty));

        Assert.Equal(DiagnosticCodes.DuplicateJsonKey, ex.Code);
        Assert.Equal("metadata.k", ex.Path!.ToString());
        Assert.NotNull(ex.Location);
        Assert.Equal("x.json", ex.Source);
        Assert.IsType<DuplicateJsonKeyException>(ex.InnerException);
    }

    [Fact]
    public void KeysThatDifferOnlyInCase_AreRejectedToo_BecauseReadersDisagreeAboutWhichOneWins()
    {
        string json = Envelope + ", \"metadata\": { \"k\": 1, \"K\": 3 } }";

        var ex = Assert.Throws<TemplateLoadException>(() => Loader.Parse(json, "x.json", ParameterSet.Empty));

        Assert.Equal(DiagnosticCodes.DuplicateJsonKey, ex.Code);
        Assert.Contains("letter case", ex.Message, StringComparison.Ordinal);
        Assert.Equal("metadata.K", ex.Path!.ToString());
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

        string resources = string.Join(",", Enumerable.Range(0, 400).Select(i => "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"g" + i + "\", \"properties\": { \"displayName\": \"G" + i + "\" } }"));
        var many = Loader.Parse("{ \"$schema\": \"https://schema.earm.proxylayer.net/schemas/2026-10-01/deploymentTemplate.json#\", \"contentVersion\": \"1.0.0.0\", \"resources\": [ " + resources + " ] }", "x.json", ParameterSet.Empty);
        Assert.Equal(400, many.Resources.Count);
    }

    // ---- loader: file API ----

    [Fact]
    public async Task LoadAsync_ReportsAMissingFile_AndRejectsBlankPaths()
    {
        string missing = Path.Combine(Path.GetTempPath(), "earm-missing-" + Guid.NewGuid().ToString("N") + ".entra.json");

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
        string json = "{ \"$schema\": \"https://schema.earm.proxylayer.net/schemas/2026-10-01/deploymentTemplate.json#\", \"contentVersion\": \"1.0.0.0\", \"resources\": [ "
            + "{ \"type\": \"Microsoft.Entra/applications\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"app\", \"properties\": { \"displayName\": \"App\" }, \"resources\": [ "
            + "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"child\", \"properties\": { } } ] } ] }";

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
        Assert.Equal(9007199254740992L, TemplateJson.AsInt64(JsonValue.Create(9007199254740992d)));
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

        var diagnostics = new TemplateValidator(Loader is null ? throw new InvalidOperationException() : EArm.Core.Types.ResourceTypeCatalog.Default).Validate(document);

        Assert.DoesNotContain(diagnostics, d => d.IsError);
    }
}
