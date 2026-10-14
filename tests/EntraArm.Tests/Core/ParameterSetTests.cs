using System;
using System.Text.Json.Nodes;
using EntraArm.Core;
using EntraArm.Core.Diagnostics;
using EntraArm.Core.Json;
using EntraArm.Core.Loading;
using Xunit;

namespace EntraArm.Tests;

public sealed class ParameterSetTests
{
    [Fact]
    public void FromFile_ReadsTheSampleParameterFile()
    {
        string path = TestPaths.Sample("orders-api.parameters.json");

        var set = ParameterSet.FromFile(path);

        Assert.Equal(path, set.SourcePath);
        Assert.Equal("https://entraarm.dev/schemas/2026-10-01/deploymentParameters.json#", set.Schema);
        Assert.Equal("1.0.0.0", set.ContentVersion);
        Assert.NotNull(set.RootElement);
        Assert.Equal(2, set.Values.Count);

        var env = set.Values["ENV"];
        Assert.Equal("prod", TemplateJson.AsString(env.Value));
        Assert.Equal(ParameterValueSource.File, env.Source);
        Assert.Equal(path, env.SourcePath);
        Assert.False(env.IsInline);
        Assert.False(env.IsKeyVaultReference);
        Assert.Null(env.RawText);

        var password = set.Values["initialPassword"];
        Assert.True(password.IsKeyVaultReference);
        Assert.Null(password.Value);
        Assert.NotNull(password.Reference);
        Assert.Equal("contoso-kv", password.Reference.VaultName);
        Assert.Equal("bg-initial", password.Reference.SecretName);
        Assert.Null(password.Reference.SecretVersion);
        Assert.StartsWith("/subscriptions/", password.Reference.KeyVaultId);
    }

    [Fact]
    public void Parse_AcceptsTheBareShapeWithoutAParametersKey()
    {
        var set = ParameterSet.Parse("""{ "$schema": "ignored", "contentVersion": "1.0.0.0", "metadata": {}, "env": { "value": "dev" } }""");

        var env = Assert.Single(set.Values).Value;
        Assert.Equal("dev", TemplateJson.AsString(env.Value));
        Assert.Null(set.SourcePath);
        Assert.Equal("ignored", set.Schema);
    }

    [Fact]
    public void Parse_AcceptsCommentsAndTrailingCommas()
    {
        var set = ParameterSet.Parse("{ // comment\n \"parameters\": { \"a\": { \"value\": [1, 2,], }, }, }");

        Assert.Equal(2, Assert.IsType<JsonArray>(set.Values["a"].Value).Count);
    }

    [Fact]
    public void Parse_ReadsSecretVersionFromAKeyVaultReference()
    {
        var set = ParameterSet.Parse("""{ "parameters": { "pw": { "reference": { "keyVault": { "id": "kv" }, "secretName": "s", "secretVersion": "v2" } } } }""");

        var reference = set.Values["pw"].Reference;
        Assert.NotNull(reference);
        Assert.Equal(new KeyVaultReference("kv", "s", "v2"), reference);
        Assert.Equal("keyVault 'kv' secret 's' version 'v2'", reference.ToString());
    }

    [Theory]
    [InlineData("[]", "")]
    [InlineData("{ \"parameters\": 1 }", "parameters")]
    [InlineData("{ \"parameters\": { \"env\": \"dev\" } }", "parameters.env")]
    [InlineData("{ \"parameters\": { \"env\": {} } }", "parameters.env")]
    [InlineData("{ \"parameters\": { \"env\": { \"value\": 1, \"reference\": {} } } }", "parameters.env")]
    [InlineData("{ \"parameters\": { \"env\": { \"reference\": { \"secretName\": \"s\" } } } }", "parameters.env.reference")]
    [InlineData("{ \"parameters\": { \"env\": { \"reference\": { \"keyVault\": { \"id\": \"kv\" } } } } }", "parameters.env.reference")]
    [InlineData("{ \"parameters\": { \"env\": { \"reference\": \"text\" } } }", "parameters.env.reference")]
    [InlineData("{ \"parameters\": { \"env\": { \"value\": 1 }, \"ENV\": { \"value\": 2 } } }", "parameters.ENV")]
    [InlineData("{ \"env\": { \"value\": 1 }, \"Env\": { \"value\": 2 } }", "Env")]
    public void Parse_RejectsInvalidShapesWithInvalidParameterFile(string json, string expectedPath)
    {
        var exception = Assert.Throws<TemplateLoadException>(() => ParameterSet.Parse(json, "p.json"));

        Assert.Equal(DiagnosticCodes.InvalidParameterFile, exception.Code);
        Assert.Equal("p.json", exception.Source);
        Assert.Equal(expectedPath, exception.Path?.ToString());
    }

    [Fact]
    public void Parse_InvalidJson_ThrowsInvalidJsonWithALocation()
    {
        var exception = Assert.Throws<TemplateLoadException>(() => ParameterSet.Parse("{ \"parameters\": { \"a\": }", "p.json"));

        Assert.Equal(DiagnosticCodes.InvalidJson, exception.Code);
        Assert.NotNull(exception.Location);
        Assert.Equal(1, exception.Location.Value.Line);
    }

    [Fact]
    public void FromFile_MissingFile_ThrowsFileNotFound()
    {
        string path = TestPaths.Sample("missing.parameters.json");

        var exception = Assert.Throws<TemplateLoadException>(() => ParameterSet.FromFile(path));

        Assert.Equal(DiagnosticCodes.FileNotFound, exception.Code);
        Assert.Equal(path, exception.Source);
    }

    [Theory]
    [InlineData("env=prod", true, "env", "prod")]
    [InlineData("a=b=c", true, "a", "b=c")]
    [InlineData("a=", true, "a", "")]
    [InlineData(" a =x", true, "a", "x")]
    [InlineData("list=[1,2]", true, "list", "[1,2]")]
    [InlineData("=x", false, "", "")]
    [InlineData("noequals", false, "", "")]
    [InlineData("", false, "", "")]
    [InlineData(" =x", false, "", "")]
    public void TryParseInline_SplitsAtTheFirstEquals(string token, bool expected, string name, string rawText)
    {
        bool parsed = ParameterSet.TryParseInline(token, out string actualName, out string actualRaw);

        Assert.Equal(expected, parsed);
        if (expected)
        {
            Assert.Equal(name, actualName);
            Assert.Equal(rawText, actualRaw);
        }
    }

    [Fact]
    public void WithInline_OverridesCaseInsensitivelyAndKeepsTheRawText()
    {
        var file = ParameterSet.Parse("""{ "parameters": { "env": { "value": "dev" } } }""", "p.json");

        var set = file.WithInline("ENV", "prod");

        var env = Assert.Single(set.Values).Value;
        Assert.True(env.IsInline);
        Assert.Equal("prod", env.RawText);
        Assert.Equal("prod", TemplateJson.AsString(env.Value));
        Assert.Null(env.SourcePath);
        Assert.Equal("p.json", set.SourcePath);
        Assert.NotNull(set.RootElement);

        // The original set is unchanged.
        Assert.Equal("dev", TemplateJson.AsString(file.Values["env"].Value));
        Assert.Throws<ArgumentException>(() => set.WithInline(" ", "x"));
        Assert.Throws<ArgumentNullException>(() => set.WithInline("x", null!));
    }

    [Fact]
    public void Merge_LaterValuesWinAndFileMetadataIsTakenFromTheOtherSet()
    {
        var first = ParameterSet.Empty.WithInline("a", "1").WithInline("b", "1");
        var second = ParameterSet.Parse("""{ "contentVersion": "2.0.0.0", "parameters": { "B": { "value": 2 }, "c": { "value": 3 } } }""", "second.json");

        var merged = first.Merge(second);

        Assert.Equal(3, merged.Values.Count);
        Assert.Equal("1", merged.Values["a"].RawText);
        Assert.Equal(2, TemplateJson.AsInt64(merged.Values["b"].Value));
        Assert.Equal(3, TemplateJson.AsInt64(merged.Values["c"].Value));
        Assert.Equal("second.json", merged.SourcePath);
        Assert.Equal("2.0.0.0", merged.ContentVersion);

        var reversed = second.Merge(first);
        Assert.Equal("1", reversed.Values["b"].RawText);
        Assert.Equal("second.json", reversed.SourcePath);
        Assert.Throws<ArgumentNullException>(() => first.Merge(null!));
    }

    [Fact]
    public void Empty_HasNoValuesOrMetadata()
    {
        Assert.Empty(ParameterSet.Empty.Values);
        Assert.Null(ParameterSet.Empty.SourcePath);
        Assert.Null(ParameterSet.Empty.RootElement);
        Assert.Null(ParameterSet.Empty.Schema);
    }

    [Theory]
    [InlineData("contoso-kv", "contoso-kv")]
    [InlineData("/subscriptions/0/resourceGroups/rg/providers/Microsoft.KeyVault/vaults/contoso-kv", "contoso-kv")]
    [InlineData("/subscriptions/0/resourceGroups/rg/providers/Microsoft.KeyVault/vaults/contoso-kv/", "contoso-kv")]
    [InlineData("  contoso-kv  ", "contoso-kv")]
    public void KeyVaultReference_VaultName_IsTheLastSegment(string id, string expected)
    {
        var reference = KeyVaultReference.Create(id, "secret", null);

        Assert.Equal(expected, reference.VaultName);
        Assert.Null(reference.SecretVersion);
        Assert.Equal($"keyVault '{expected}' secret 'secret'", reference.ToString());
    }

    [Fact]
    public void KeyVaultReference_Create_NormalisesBlankVersionAndRejectsBlankIds()
    {
        Assert.Null(KeyVaultReference.Create("kv", "s", "  ").SecretVersion);
        Assert.Equal("v1", KeyVaultReference.Create("kv", "s", "v1").SecretVersion);
        Assert.Throws<ArgumentException>(() => KeyVaultReference.Create(" ", "s", null));
        Assert.Throws<ArgumentException>(() => KeyVaultReference.Create("kv", "", null));
    }
}
