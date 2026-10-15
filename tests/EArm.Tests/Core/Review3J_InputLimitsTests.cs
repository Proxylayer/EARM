using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Loading;
using EArm.Tests.Engine;
using Xunit;

namespace EArm.Tests;

/// <summary>
/// Review 3J, finding 6: a 38 MB template took 35 s and 5 GB, a directory passed as the template was reported as "not found", a UTF-16 file as
/// <c>'0xFF' is an invalid start of a value</c>, and only the first repeated key was listed. Template and parameter files are now capped at 8 MiB
/// (<c>InputTooLarge</c>, from the file size, before they are read), documents at 2,000,000 JSON values (counted in one cheap pass), UTF-16/UTF-32 is
/// named as such, and every repeated key (up to 50) is in the message.
/// </summary>
public sealed class Review3J_InputLimitsTests
{
    private static string Padded(int bytes)
    {
        // A valid template whose size is exactly <bytes> (the padding is a comment).
        string head = Review1C.Template("\"resources\": []");
        string comment = "/*";
        int padding = bytes - Encoding.UTF8.GetByteCount(head) - comment.Length - 2;
        return head + comment + new string('x', padding) + "*/";
    }

    // ---- file size ----

    [Fact]
    public async Task ATemplateOfExactlyEightMebibytes_Loads_AndOneByteMoreIsRefusedBeforeItIsRead()
    {
        using var directory = new TestDirectory(nameof(ATemplateOfExactlyEightMebibytes_Loads_AndOneByteMoreIsRefusedBeforeItIsRead));
        string ok = directory.File("ok.entra.json");
        string big = directory.File("big.entra.json");
        File.WriteAllText(ok, Padded(TemplateJson.MaxInputBytes), new UTF8Encoding(false));
        File.WriteAllText(big, Padded(TemplateJson.MaxInputBytes + 1), new UTF8Encoding(false));
        Assert.Equal(8L * 1024 * 1024, new FileInfo(ok).Length);

        var document = await new TemplateLoader().LoadAsync(ok, ParameterSet.Empty, TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<TemplateLoadException>(() => new TemplateLoader().LoadAsync(big, ParameterSet.Empty, TestContext.Current.CancellationToken));

        Assert.NotNull(document.Root);
        Assert.Equal(DiagnosticCodes.InputTooLarge, error.Code);
        Assert.Contains("8,388,608 bytes", error.Message, StringComparison.Ordinal);
        Assert.Contains("8,388,609 bytes", error.Message, StringComparison.Ordinal);
        Assert.Equal(big, error.Source);
    }

    [Fact]
    public void AParameterFileOverTheLimit_IsRefused()
    {
        using var directory = new TestDirectory(nameof(AParameterFileOverTheLimit_IsRefused));
        string path = directory.File("big.parameters.json");
        File.WriteAllText(path, "{ \"parameters\": {} }" + new string(' ', TemplateJson.MaxInputBytes), new UTF8Encoding(false));

        var error = Assert.Throws<TemplateLoadException>(() => ParameterSet.FromFile(path));

        Assert.Equal(DiagnosticCodes.InputTooLarge, error.Code);
        Assert.StartsWith("Parameter file ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InMemoryText_OverTheLimit_IsRefusedToo()
    {
        string text = Padded(TemplateJson.MaxInputBytes + 10);

        var error = Assert.Throws<TemplateLoadException>(() => new TemplateLoader().Parse(text, "inline.entra.json", ParameterSet.Empty));

        Assert.Equal(DiagnosticCodes.InputTooLarge, error.Code);
    }

    [Fact]
    public async Task ADirectoryPassedAsTheFile_IsReportedAsADirectory()
    {
        using var directory = new TestDirectory(nameof(ADirectoryPassedAsTheFile_IsReportedAsADirectory));

        var template = await Assert.ThrowsAsync<TemplateLoadException>(() => new TemplateLoader().LoadAsync(directory.Path, ParameterSet.Empty, TestContext.Current.CancellationToken));
        var parameters = Assert.Throws<TemplateLoadException>(() => ParameterSet.FromFile(directory.Path));

        Assert.Contains("is a directory", template.Message, StringComparison.Ordinal);
        Assert.Contains("is a directory", parameters.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("was not found", template.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingFile_IsStillNotFound()
    {
        using var directory = new TestDirectory(nameof(AMissingFile_IsStillNotFound));

        var error = Assert.Throws<TemplateLoadException>(() => ParameterSet.FromFile(directory.File("nope.json")));

        Assert.Equal(DiagnosticCodes.FileNotFound, error.Code);
        Assert.Contains("was not found", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadAllBytes_StopsAtTheLimit_ForAFileThatGrowsAfterTheSizeCheck()
    {
        using var directory = new TestDirectory(nameof(ReadAllBytes_StopsAtTheLimit_ForAFileThatGrowsAfterTheSizeCheck));
        string path = directory.File("x.json");
        File.WriteAllBytes(path, new byte[TemplateJson.MaxInputBytes]);

        Assert.Equal(TemplateJson.MaxInputBytes, InputFiles.ReadAllBytes(path, "Template").Length);
        File.AppendAllText(path, "x");
        Assert.Equal(DiagnosticCodes.InputTooLarge, Assert.Throws<TemplateLoadException>(() => InputFiles.ReadAllBytes(path, "Template")).Code);
    }

    // ---- node count ----

    [Fact]
    public void ADocumentWithMoreThanTwoMillionValues_IsRefused_BeforeItIsBuilt()
    {
        // 2,000,001 numbers in one array, written in about 4 MB.
        string text = Review1C.Template("\"resources\": [], \"metadata\": { \"values\": [" + string.Join(',', Enumerable.Repeat("0", 2_000_001)) + "] }");
        Assert.True(text.Length < TemplateJson.MaxInputBytes);
        var started = DateTime.UtcNow;

        var error = Assert.Throws<TemplateLoadException>(() => new TemplateLoader().Parse(text, "many.entra.json", ParameterSet.Empty));

        Assert.Equal(DiagnosticCodes.InputTooLarge, error.Code);
        Assert.Contains("2,000,000 JSON values", error.Message, StringComparison.Ordinal);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(15));
    }

    [Theory]
    [InlineData(5, false)]
    [InlineData(4, true)]
    public void TheNodeCount_IsToTheValue_ForLeavesObjectsAndArrays(long limit, bool tooMany)
    {
        // { "a": [1, 2], "b": 3 } holds five values: the object, the array, 1, 2 and 3.
        var scan = DuplicateKeys.Scan(Encoding.UTF8.GetBytes("{ \"a\": [1, 2], \"b\": 3 }"), 50, null, limit);

        Assert.Equal(tooMany, scan.TooManyNodes);
        if (!tooMany)
        {
            Assert.Equal(5, scan.Nodes);
        }
    }

    // ---- encodings ----

    [Theory]
    [InlineData("utf-16le-bom")]
    [InlineData("utf-16be-bom")]
    [InlineData("utf-16le-nobom")]
    [InlineData("utf-32le-bom")]
    public void AUtf16OrUtf32File_IsNamedAsSuch(string kind)
    {
        string json = Review1C.Template("\"resources\": []");
        byte[] bytes = kind switch
        {
            "utf-16le-bom" => [.. new UnicodeEncoding(false, true).GetPreamble(), .. new UnicodeEncoding(false, false).GetBytes(json)],
            "utf-16be-bom" => [.. new UnicodeEncoding(true, true).GetPreamble(), .. new UnicodeEncoding(true, false).GetBytes(json)],
            "utf-16le-nobom" => new UnicodeEncoding(false, false).GetBytes(json),
            _ => [.. new UTF32Encoding(false, true).GetPreamble(), .. new UTF32Encoding(false, false).GetBytes(json)],
        };

        var error = Assert.Throws<TemplateLoadException>(() => new TemplateLoader().Parse(bytes, "wide.entra.json", ParameterSet.Empty));
        var parameterError = Assert.Throws<TemplateLoadException>(() => ParameterSet.Parse(bytes, "wide.parameters.json"));

        Assert.Equal(DiagnosticCodes.InvalidJson, error.Code);
        Assert.Contains("must be UTF-8", error.Message, StringComparison.Ordinal);
        Assert.Contains(kind.StartsWith("utf-32", StringComparison.Ordinal) ? "UTF-32 detected" : "UTF-16 detected", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("0xFF", error.Message, StringComparison.Ordinal);
        Assert.Contains("must be UTF-8", parameterError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AUtf8ByteOrderMark_IsAccepted()
    {
        byte[] bytes = [.. new UTF8Encoding(true).GetPreamble(), .. Encoding.UTF8.GetBytes(Review1C.Template("\"resources\": []"))];

        Assert.NotNull(new TemplateLoader().Parse(bytes, "bom.entra.json", ParameterSet.Empty).Root);
    }

    // ---- every repeated key ----

    [Fact]
    public void EveryRepeatedKey_IsListed_NotOnlyTheFirst()
    {
        string text = Review1C.Template("\"parameters\": { \"p\": { \"type\": \"string\", \"type\": \"string\" } }, \"resources\": [], \"resources\": [], \"variables\": { \"v\": { \"a\": 1, \"a\": 2 } }");

        var error = Assert.Throws<TemplateLoadException>(() => new TemplateLoader().Parse(text, "dup.entra.json", ParameterSet.Empty));

        Assert.Equal(DiagnosticCodes.DuplicateJsonKey, error.Code);
        var exception = Assert.IsType<DuplicateJsonKeyException>(error.InnerException);
        Assert.Equal(["parameters.p.type", "resources", "variables.v.a"], exception.Duplicates.Select(d => d.Path.ToString()).ToArray());
        Assert.Contains("2 more repeated key(s): resources (line 1", error.Message, StringComparison.Ordinal);
        Assert.Contains("variables.v.a (line 1", error.Message, StringComparison.Ordinal);
    }
}
