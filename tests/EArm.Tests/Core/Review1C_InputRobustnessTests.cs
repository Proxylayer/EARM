using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Cli;
using EArm.Cli.Output;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Loading;
using EArm.Engine.Execution;
using EArm.Engine.WhatIf;
using EArm.Tests.Cli;
using EArm.Tests.Engine;
using Xunit;

namespace EArm.Tests;

/// <summary>
/// Review 1C, finding I-05: four ways to crash a command with a stack trace. Repeated JSON keys (<c>"resources": [], "resources": []</c>) made the
/// parsed object throw <see cref="ArgumentException"/> on first use; a number beyond the decimal range on an enum-typed property made the schema
/// library throw <see cref="FormatException"/>; a syntax error in a variable reached through another variable made the reference scanner throw
/// <c>ExpressionParseException</c>; and a value nested 64 levels broke the JSON writer. Each is now a diagnostic.
/// </summary>
public sealed class Review1C_InputRobustnessTests
{
    // ---- repeated JSON keys ----

    private static TemplateLoadException LoadFails(string text, ParameterSet? parameters = null)
    {
        return Assert.Throws<TemplateLoadException>(() => new TemplateLoader().Parse(text, "dup.entra.json", parameters ?? ParameterSet.Empty));
    }

    [Fact]
    public void ARepeatedTopLevelKey_IsDuplicateJsonKey_WithThePathAndThePositionOfTheRepeat()
    {
        string text = "{ \"$schema\": \"" + Review1C.SchemaUri + "\", \"contentVersion\": \"1.0.0.0\",\n  \"resources\": [],\n  \"resources\": [] }";

        var error = LoadFails(text);

        Assert.Equal(DiagnosticCodes.DuplicateJsonKey, error.Code);
        Assert.Equal("resources", error.Path!.ToString());
        Assert.Equal(new SourceLocation(3, 3), error.Location);
        Assert.Contains("\"resources\"", error.Message, StringComparison.Ordinal);
        Assert.Equal("dup.entra.json", error.Source);
    }

    [Fact]
    public void ARepeatedKeyInsideAResource_IsFoundAtItsOwnPath_AndAllRepeatsAreCounted()
    {
        // The case that matters: a conditional access policy with two "state" keys and two "builtInControls" keys.
        string text = Review1C.Template("\"resources\": [ { \"type\": \"Microsoft.Entra/conditionalAccess/policies\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"p\", "
            + "\"properties\": { \"state\": \"disabled\", \"state\": \"enabled\", \"grantControls\": { \"operator\": \"OR\", \"builtInControls\": [\"block\"], \"builtInControls\": [\"mfa\"] } } } ]");

        var error = LoadFails(text);

        Assert.Equal(DiagnosticCodes.DuplicateJsonKey, error.Code);
        Assert.Equal("resources[0].properties.state", error.Path!.ToString());
        Assert.Contains("1 more", error.Message, StringComparison.Ordinal);
        var exception = Assert.IsType<DuplicateJsonKeyException>(error.InnerException);
        Assert.Equal(["resources[0].properties.state", "resources[0].properties.grantControls.builtInControls"], exception.Duplicates.Select(d => d.Path.ToString()).ToArray());
    }

    [Fact]
    public void KeysThatRepeatInDifferentObjects_AreNotDuplicates()
    {
        string text = Review1C.Template("\"resources\": [], \"metadata\": { \"a\": 1, \"b\": 2, \"nested\": { \"a\": 3 }, \"list\": [ { \"a\": 1 }, { \"a\": 2 } ] }");

        var document = new TemplateLoader().Parse(text, "ok.entra.json", ParameterSet.Empty);

        Assert.NotNull(document.Metadata);
    }

    [Fact]
    public void ARepeatedKeyInAParameterFile_IsReported_WithItsPath()
    {
        var error = Assert.Throws<TemplateLoadException>(() => ParameterSet.Parse("{ \"parameters\": { \"a\": { \"value\": \"x\" }, \"a\": { \"value\": \"y\" } } }", "p.parameters.json"));

        Assert.Equal(DiagnosticCodes.DuplicateJsonKey, error.Code);
        Assert.Equal("parameters.a", error.Path!.ToString());
        Assert.Equal("p.parameters.json", error.Source);
    }

    [Fact]
    public void ARepeatedKeyInAnInlineJsonParameter_IsADuplicateJsonKeyDiagnostic()
    {
        string text = Review1C.Template("\"parameters\": { \"o\": { \"type\": \"object\" } }, \"resources\": []");
        var parameters = ParameterSet.Empty.WithInline("o", "{\"a\":1,\"a\":2}");

        var document = new TemplateLoader().Parse(text, "inline.entra.json", parameters);

        Assert.Contains(document.LoadDiagnostics, d => d.Code == DiagnosticCodes.DuplicateJsonKey && d.IsError);
    }

    [Fact]
    public void TheJsonFunction_RefusesRepeatedKeys_WithoutEchoingASecureText()
    {
        var plain = Assert.Throws<EArm.Expressions.ExpressionEvaluationException>(() => Expr.Eval("[json('{\"a\":1,\"a\":2}')]"));
        Assert.Equal(DiagnosticCodes.ExpressionEvaluationFailed, plain.Code);
        Assert.Contains("\"a\"", plain.Message, StringComparison.Ordinal);

        var secure = Expr.Context(parameters: Expr.Parameters(("s", EArm.Core.Secrets.SecretJson.CreateString("{\"hidden\":1,\"hidden\":2}"))));
        var hidden = Assert.Throws<EArm.Expressions.ExpressionEvaluationException>(() => Expr.Eval("[json(parameters('s'))]", secure));
        Assert.DoesNotContain("hidden", hidden.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnTheCommandLine_ARepeatedKey_ExitsOne_WithTheCodeThePathAndLineAndColumn()
    {
        using var directory = new TestDirectory(nameof(OnTheCommandLine_ARepeatedKey_ExitsOne_WithTheCodeThePathAndLineAndColumn));
        string path = directory.File("dup.entra.json");
        await File.WriteAllTextAsync(path, "{ \"$schema\": \"" + Review1C.SchemaUri + "\", \"contentVersion\": \"1.0.0.0\",\n \"resources\": [],\n \"resources\": [] }", TestContext.Current.CancellationToken);

        var result = await CliRunner.ValidateJsonAsync(path);

        Assert.Equal(1, result.ExitCode);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("DuplicateJsonKey", diagnostic!["code"]!.GetValue<string>());
        Assert.Equal("resources", diagnostic["path"]!.GetValue<string>());
        Assert.Equal(3, diagnostic["line"]!.GetValue<int>());
        Assert.Equal(2, diagnostic["column"]!.GetValue<int>());
        Assert.Equal(string.Empty, result.StandardError);
    }

    // ---- a number beyond the decimal range ----

    [Theory]
    [InlineData("1e300")]
    [InlineData("-1e300")]
    [InlineData("1e400")]
    [InlineData("123456789012345678901234567890123456789")]
    public void AHugeNumber_OnAnEnumTypedProperty_IsADiagnostic_NotAFormatException(string number)
    {
        string text = Review1C.Template("\"resources\": [ { \"type\": \"Microsoft.Entra/applications\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"app\", \"properties\": { \"displayName\": \"x\", \"api\": { \"requestedAccessTokenVersion\": " + number + " } } } ]");

        var diagnostics = Review1C.Validate(text);

        Assert.Contains(diagnostics, d => d.Code == DiagnosticCodes.SchemaViolation && d.IsError);
    }

    [Fact]
    public async Task OnTheCommandLine_AHugeNumber_ExitsTwo_NotWithAStackTrace()
    {
        using var directory = new TestDirectory(nameof(OnTheCommandLine_AHugeNumber_ExitsTwo_NotWithAStackTrace));
        string path = directory.File("huge.entra.json");
        await File.WriteAllTextAsync(path, Review1C.Template("\"resources\": [ { \"type\": \"Microsoft.Entra/applications\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"app\", \"properties\": { \"displayName\": \"x\", \"api\": { \"requestedAccessTokenVersion\": 1e300 } } } ]"), TestContext.Current.CancellationToken);

        var result = await CliRunner.ValidateJsonAsync(path);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains(result.Diagnostics, d => d!["code"]!.GetValue<string>() == "SchemaViolation");
        Assert.Equal(string.Empty, result.StandardError);
    }

    // ---- a syntax error in a variable reached through another variable ----

    [Theory]
    [InlineData("\"bad\": \"[concat(c)]\", \"n\": \"[variables('bad')]\"")]
    [InlineData("\"bad\": \"[a.b(c)]\", \"n\": \"[variables('bad')]\"")]
    [InlineData("\"bad\": \"[concat('x'))]\", \"n\": \"[variables('bad')]\"")]
    public void ASyntaxErrorBehindAVariable_UsedInAReference_IsADiagnostic(string variables)
    {
        string text = Review1C.Template("\"variables\": { " + variables + " }, \"resources\": [ "
            + Review1C.Group("\"g\"", "{ \"displayName\": \"g\", \"mailEnabled\": false, \"securityEnabled\": true, \"members\": [ \"[reference(variables('n')).id]\" ] }") + " ]");

        var diagnostics = Review1C.Validate(text);

        Assert.Contains(diagnostics, d => d.Code == DiagnosticCodes.ExpressionSyntaxError && d.Path!.ToString() == "variables.bad");
        Assert.Contains(diagnostics, d => d.IsError && d.Path!.ToString().StartsWith("resources[0].properties.members", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OnTheCommandLine_TheReferenceScannerCase_ExitsTwo()
    {
        using var directory = new TestDirectory(nameof(OnTheCommandLine_TheReferenceScannerCase_ExitsTwo));
        string path = directory.File("scan.entra.json");
        await File.WriteAllTextAsync(path, Review1C.Template("\"variables\": { \"bad\": \"[concat(c)]\", \"n\": \"[variables('bad')]\" }, \"resources\": [ "
            + Review1C.Group("\"g\"", "{ \"displayName\": \"g\", \"mailEnabled\": false, \"securityEnabled\": true, \"members\": [ \"[reference(variables('n')).id]\" ] }") + " ]"), TestContext.Current.CancellationToken);

        var result = await CliRunner.ValidateJsonAsync(path);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
    }

    // ---- a value nested 64 levels in the JSON output ----

    [Fact]
    public void TheDeploymentResultJson_WritesAValueNested64Levels()
    {
        var outputs = new JsonObject { ["o"] = new JsonObject { ["type"] = "array", ["value"] = Review1C.DeepTree(64) } };
        var result = new DeploymentResult("deep", DeploymentStatus.Succeeded, [], [], outputs, new JsonObject(), [], null, TimeSpan.Zero);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        DeploymentResultWriter.Write(result, OutputFormat.Json, prettyPrint: true, stdout, stderr);
        DeploymentResultWriter.Write(result, OutputFormat.Json, prettyPrint: false, stdout, stderr);

        string text = stdout.ToString();
        Assert.StartsWith("{", text.TrimStart(), StringComparison.Ordinal);
        using var document = JsonDocument.Parse(text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Last(), new JsonDocumentOptions { MaxDepth = 256 });
        Assert.Equal("deep", document.RootElement.GetProperty("deploymentName").GetString());
    }

    [Fact]
    public void TheWhatIfJson_WritesAValueNested70Levels()
    {
        var document = new JsonObject { ["status"] = "Succeeded", ["changes"] = new JsonArray(new JsonObject { ["after"] = Review1C.DeepTree(70) }) };

        string pretty = WhatIfJson.Serialize(document, prettyPrint: true);
        string compact = WhatIfJson.Serialize(document, prettyPrint: false);

        using var parsed = JsonDocument.Parse(compact, new JsonDocumentOptions { MaxDepth = 256 });
        Assert.Equal("Succeeded", parsed.RootElement.GetProperty("status").GetString());
        Assert.Contains("leaf", pretty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateWithJson_OfAValueNested64Levels_Succeeds_AndTheOutputIsValidJson()
    {
        using var sandbox = new CliSandbox(nameof(CreateWithJson_OfAValueNested64Levels_Succeeds_AndTheOutputIsValidJson));
        sandbox.UsePreflightOk();
        string expression = "[" + string.Concat(Enumerable.Repeat("createArray(", 64)) + "'leaf'" + new string(')', 64) + "]";
        string template = sandbox.WriteTemplate("deep.entra.json", "\"resources\": [], \"outputs\": { \"o\": { \"type\": \"array\", \"value\": \"" + expression + "\" } }");

        var result = await CliRunner.RunAsync(["deployment", "create", "-f", template, "-o", "json"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        using var parsed = JsonDocument.Parse(result.StandardOutput, new JsonDocumentOptions { MaxDepth = 256 });
        Assert.Equal("Succeeded", parsed.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task AValueNested65Levels_IsAValueTooDeepDiagnostic_AtDeploymentTime()
    {
        using var sandbox = new CliSandbox(nameof(AValueNested65Levels_IsAValueTooDeepDiagnostic_AtDeploymentTime));
        sandbox.UsePreflightOk();
        string expression = "[" + string.Concat(Enumerable.Repeat("createArray(", 65)) + "'leaf'" + new string(')', 65) + "]";
        string template = sandbox.WriteTemplate("deeper.entra.json", "\"resources\": [], \"outputs\": { \"o\": { \"type\": \"array\", \"value\": \"" + expression + "\" } }");

        var result = await CliRunner.RunAsync(["deployment", "create", "-f", template, "-o", "json"]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("ValueTooDeep", result.StandardOutput + result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", result.StandardOutput + result.StandardError, StringComparison.Ordinal);
    }
}
