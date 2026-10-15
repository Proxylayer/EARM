using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Loading;
using EArm.Engine.State;
using EArm.Tests.Engine;
using Xunit;

namespace EArm.Tests;

/// <summary>
/// Review 5M: input that is not valid UTF-8 (the review 4L defect: <c>0xC3 0x28</c> inside a string) is refused at load as <c>InvalidJson</c> for templates, parameter files
/// and state files, through the loader and through the real command line, never as an unhandled exception.
/// </summary>
[Collection(CliCollection.Name)]
public sealed class Review5M_Utf8Tests
{
    private const string Header = "{ \"$schema\": \"https://schema.earm.proxylayer.net/schemas/2026-10-01/deploymentTemplate.json#\", \"contentVersion\": \"1.0.0.0\", ";

    private static byte[] Utf8(string text)
    {
        return new UTF8Encoding(false).GetBytes(text);
    }

    private static byte[] WithBadBytesInAString(string before, string after)
    {
        return [.. Utf8(before), 0xC3, 0x28, .. Utf8(after)];
    }

    [Fact]
    public async Task ATemplateFileWithInvalidUtf8InAResourceProperty_IsAnInvalidJsonLoadException_OnTheLineOfTheBadByte()
    {
        using var directory = new TestDirectory(nameof(ATemplateFileWithInvalidUtf8InAResourceProperty_IsAnInvalidJsonLoadException_OnTheLineOfTheBadByte));
        string path = directory.File("template.entra.json");
        string before = Header + "\n\"resources\": [ { \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"g\",\n  \"properties\": { \"displayName\": \"ab";
        await File.WriteAllBytesAsync(path, WithBadBytesInAString(before, "\" } } ] }"), TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<TemplateLoadException>(() => new TemplateLoader().LoadAsync(path, ParameterSet.Empty, TestContext.Current.CancellationToken));

        Assert.Equal(DiagnosticCodes.InvalidJson, ex.Code);
        Assert.Equal(path, ex.Source);
        Assert.Contains("0xC3 0x28", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(ex.Location);
        Assert.Equal(3, ex.Location!.Value.Line);
        Assert.Equal(Utf8("  \"properties\": { \"displayName\": \"ab").Length + 1, ex.Location.Value.Column);
    }

    [Fact]
    public async Task AParameterFileWithInvalidUtf8_IsAnInvalidJsonLoadException_WithTheFilePath()
    {
        using var directory = new TestDirectory(nameof(AParameterFileWithInvalidUtf8_IsAnInvalidJsonLoadException_WithTheFilePath));
        string path = directory.File("p.parameters.json");
        await File.WriteAllBytesAsync(path, WithBadBytesInAString("{ \"parameters\": { \"p\": { \"value\": \"x", "\" } } }"), TestContext.Current.CancellationToken);

        var ex = Assert.Throws<TemplateLoadException>(() => ParameterSet.FromFile(path));

        Assert.Equal(DiagnosticCodes.InvalidJson, ex.Code);
        Assert.Equal(path, ex.Source);
        Assert.Contains("Parameter file is not valid JSON", ex.Message, StringComparison.Ordinal);
        Assert.Contains("valid UTF-8", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(ex.Location);
    }

    [Fact]
    public void ValidMultiByteText_StillLoads()
    {
        var parameters = ParameterSet.Parse(Utf8("{ \"parameters\": { \"p\": { \"value\": \"café ü € \U0001F600\" } } }"), "p.json");

        Assert.Equal("café ü € \U0001F600", parameters.Values["p"].Value!.GetValue<string>());
    }

    [Fact]
    public async Task AnInlineValueWithALoneSurrogateCharacter_IsNotACrash()
    {
        // An inline -p value is a UTF-16 string from the command line, so it cannot hold malformed UTF-8; its analogue is a lone surrogate character.
        using var directory = new TestDirectory(nameof(AnInlineValueWithALoneSurrogateCharacter_IsNotACrash));
        string template = directory.File("template.entra.json");
        await File.WriteAllTextAsync(template, Header + "\"parameters\": { \"p\": { \"type\": \"object\" } }, \"resources\": [] }", TestContext.Current.CancellationToken);

        var result = await CliRunner.ValidateJsonAsync(template, "-p", "p={\"a\":\"x\ud800y\"}");

        Assert.DoesNotContain("Unexpected error", result.StandardError, StringComparison.Ordinal);
        Assert.InRange(result.ExitCode, 0, 2);
    }

    [Fact]
    public async Task TheCommandLine_ReportsInvalidUtf8InATemplate_AsAnInvalidJsonDiagnostic_NotAnUnexpectedError()
    {
        using var directory = new TestDirectory(nameof(TheCommandLine_ReportsInvalidUtf8InATemplate_AsAnInvalidJsonDiagnostic_NotAnUnexpectedError));
        string path = directory.File("template.entra.json");
        string before = Header + "\"resources\": [ { \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"g\", \"properties\": { \"displayName\": \"ab";
        await File.WriteAllBytesAsync(path, WithBadBytesInAString(before, "\" } } ] }"), TestContext.Current.CancellationToken);

        var result = await CliRunner.ValidateJsonAsync(path);

        Assert.Equal(1, result.ExitCode);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("InvalidJson", diagnostic!["code"]!.GetValue<string>());
        Assert.Contains("valid UTF-8", diagnostic["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(path, diagnostic["source"]!.GetValue<string>());
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task TheCommandLine_ReportsInvalidUtf8InAParameterFile_AsAnInvalidJsonDiagnostic_NotAnUnexpectedError()
    {
        using var directory = new TestDirectory(nameof(TheCommandLine_ReportsInvalidUtf8InAParameterFile_AsAnInvalidJsonDiagnostic_NotAnUnexpectedError));
        string template = directory.File("template.entra.json");
        string parameters = directory.File("template.parameters.json");
        await File.WriteAllTextAsync(template, Header + "\"parameters\": { \"p\": { \"type\": \"string\" } }, \"resources\": [] }", TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(parameters, WithBadBytesInAString("{ \"parameters\": { \"p\": { \"value\": \"a", "b\" } } }"), TestContext.Current.CancellationToken);

        var result = await CliRunner.ValidateJsonAsync(template, "-p", "@" + parameters);

        Assert.Equal(1, result.ExitCode);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("InvalidJson", diagnostic!["code"]!.GetValue<string>());
        Assert.Equal(parameters, diagnostic["source"]!.GetValue<string>());
        Assert.Equal(string.Empty, result.StandardError);
    }

    [Fact]
    public async Task AStateFileWithInvalidUtf8_IsACleanStateStoreException_NotSilentReplacementCharacters()
    {
        using var directory = new TestDirectory(nameof(AStateFileWithInvalidUtf8_IsACleanStateStoreException_NotSilentReplacementCharacters));
        var store = new FileStateStore(directory.Path);
        Directory.CreateDirectory(store.Directory);
        await File.WriteAllBytesAsync(store.PathFor("r5m"), WithBadBytesInAString("{ \"schemaVersion\": 1, \"deploymentName\": \"r5m\", \"note\": \"", "\" }"), TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<StateStoreException>(() => store.LoadAsync("r5m", TestContext.Current.CancellationToken));

        Assert.Equal("r5m", ex.DeploymentName);
        Assert.Contains("not valid UTF-8", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AValidMultiByteStateFile_StillLoadsPastTheEncodingCheck()
    {
        using var directory = new TestDirectory(nameof(AValidMultiByteStateFile_StillLoadsPastTheEncodingCheck));
        var store = new FileStateStore(directory.Path);
        Directory.CreateDirectory(store.Directory);
        await File.WriteAllBytesAsync(store.PathFor("r5m"), Utf8("{ \"note\": \"café\" "), TestContext.Current.CancellationToken);

        var ex = await Record.ExceptionAsync(() => store.LoadAsync("r5m", TestContext.Current.CancellationToken));

        // The truncated JSON is refused as JSON, which proves the bytes were decoded and parsed (not rejected as malformed UTF-8).
        Assert.NotNull(ex);
        Assert.IsNotType<StateStoreException>(ex);
        Assert.DoesNotContain("UTF-8", ex.Message, StringComparison.Ordinal);
    }
}
