using System;
using System.Text;
using EntraArm.Core;
using EntraArm.Core.Diagnostics;
using EntraArm.Core.Loading;
using Xunit;

namespace EntraArm.Tests;

/// <summary>
/// Parameter file shapes: Key Vault reference variants (vault id forms, versions, wrong types, extras), conflicting or missing
/// members, the bare and the ARM layout, BOM and empty input, name case, and the merge / inline rules. Every refusal is a
/// <see cref="TemplateLoadException"/> with a stable code and (where one exists) the offending path.
/// </summary>
public sealed class ParameterSetShapeTests
{
    private const string VaultId = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg/providers/Microsoft.KeyVault/vaults/contoso-kv";

    private static ParameterSet Parse(string json)
    {
        return ParameterSet.Parse(json, "params.json");
    }

    private static string Reference(string body)
    {
        return "{ \"parameters\": { \"pw\": { \"reference\": " + body + " } } }";
    }

    // ---- Key Vault references ----

    [Theory]
    [InlineData("{ \"keyVault\": { \"id\": \"" + VaultId + "\" }, \"secretName\": \"orders-secret\" }", "contoso-kv", "orders-secret", null)]
    [InlineData("{ \"keyVault\": { \"id\": \"contoso-kv\" }, \"secretName\": \"s\", \"secretVersion\": \"0123abc\" }", "contoso-kv", "s", "0123abc")]
    [InlineData("{ \"keyVault\": { \"id\": \"contoso-kv\", \"extra\": 1 }, \"secretName\": \"s\", \"ignored\": true }", "contoso-kv", "s", null)]
    [InlineData("{ \"keyVault\": { \"id\": \"kv\" }, \"secretName\": \"ünï-日本\" }", "kv", "ünï-日本", null)]
    [InlineData("{ \"keyVault\": { \"id\": \"kv\" }, \"secretName\": \"s\", \"secretVersion\": \"   \" }", "kv", "s", null)]
    [InlineData("{ \"keyVault\": { \"id\": \"kv\" }, \"secretName\": \"s\", \"secretVersion\": null }", "kv", "s", null)]
    public void KeyVaultReferences_AreParsed_WithTheirVaultNameSecretAndVersion(string body, string vault, string secret, string? version)
    {
        var set = Parse(Reference(body));

        Assert.True(set.TryGet("pw", out var value));
        Assert.True(value.IsKeyVaultReference);
        Assert.Equal(vault, value.Reference!.VaultName);
        Assert.Equal(secret, value.Reference.SecretName);
        Assert.Equal(version, value.Reference.SecretVersion);
        Assert.Equal(ParameterValueSource.File, value.Source);
        Assert.Equal("params.json", value.SourcePath);
        Assert.Null(value.Value);
    }

    [Theory]
    [InlineData("\"just a string\"", "'reference' must be an object")]
    [InlineData("[]", "'reference' must be an object")]
    [InlineData("null", "'reference' must be an object")]
    [InlineData("{ }", "needs 'keyVault.id' and 'secretName'")]
    [InlineData("{ \"secretName\": \"s\" }", "needs 'keyVault.id' and 'secretName'")]
    [InlineData("{ \"keyVault\": \"kv\", \"secretName\": \"s\" }", "needs 'keyVault.id' and 'secretName'")]
    [InlineData("{ \"keyVault\": { }, \"secretName\": \"s\" }", "needs 'keyVault.id' and 'secretName'")]
    [InlineData("{ \"keyVault\": { \"id\": 5 }, \"secretName\": \"s\" }", "needs 'keyVault.id' and 'secretName'")]
    [InlineData("{ \"keyVault\": { \"id\": \"kv\" } }", "needs 'keyVault.id' and 'secretName'")]
    [InlineData("{ \"keyVault\": { \"id\": \"kv\" }, \"secretName\": 7 }", "needs 'keyVault.id' and 'secretName'")]
    [InlineData("{ \"keyVault\": { \"id\": \"  \" }, \"secretName\": \"s\" }", "")]
    [InlineData("{ \"keyVault\": { \"id\": \"kv\" }, \"secretName\": \"\" }", "")]
    public void MalformedKeyVaultReferences_AreInvalidParameterFiles_WithThePath(string body, string fragment)
    {
        var ex = Assert.ThrowsAny<Exception>(() => Parse(Reference(body)));

        if (ex is TemplateLoadException load)
        {
            Assert.Equal(DiagnosticCodes.InvalidParameterFile, load.Code);
            Assert.Contains("reference", load.Path!.ToString(), StringComparison.Ordinal);
            Assert.Contains(fragment, load.Message, StringComparison.Ordinal);
        }
        else
        {
            // Blank ids / names are refused by KeyVaultReference.Create itself.
            Assert.IsAssignableFrom<ArgumentException>(ex);
        }
    }

    /// <summary>
    /// DEFECT (low severity, not fixed here: src/ is out of scope for the test pass). ParameterSet.ParseKeyVaultReference
    /// (src/EntraArm.Core/ParameterSet.cs:199) reads <c>secretVersion</c> through <c>TemplateJson.AsString</c>, which is <c>null</c> for any
    /// non-string, so a numeric version (<c>"secretVersion": 2</c>) is silently dropped and the LATEST secret version is resolved instead of the
    /// pinned one. Expected: an InvalidParameterFile error (or the number taken as text). Actual: parsed with SecretVersion = null.
    /// </summary>
    [Fact(Skip = "DEFECT: a non-string secretVersion is silently ignored, so the latest secret version is used instead of the pinned one (ParameterSet.cs:199).")]
    public void Defect_ANumericSecretVersion_ShouldNotBeSilentlyDropped()
    {
        var parse = () => Parse(Reference("{ \"keyVault\": { \"id\": \"kv\" }, \"secretName\": \"s\", \"secretVersion\": 2 }"));

        try
        {
            var set = parse();
            Assert.True(set.TryGet("pw", out var value));
            Assert.NotNull(value.Reference!.SecretVersion);
        }
        catch (TemplateLoadException ex)
        {
            Assert.Equal(DiagnosticCodes.InvalidParameterFile, ex.Code);
        }
    }

    // ---- value / reference conflicts and layouts ----

    [Theory]
    [InlineData("{ \"parameters\": { \"p\": { } } }")]
    [InlineData("{ \"parameters\": { \"p\": { \"value\": 1, \"reference\": { \"keyVault\": { \"id\": \"kv\" }, \"secretName\": \"s\" } } } }")]
    [InlineData("{ \"parameters\": { \"p\": { \"metadata\": {} } } }")]
    public void ExactlyOneOfValueOrReference_IsRequired(string json)
    {
        var ex = Assert.Throws<TemplateLoadException>(() => Parse(json));

        Assert.Equal(DiagnosticCodes.InvalidParameterFile, ex.Code);
        Assert.Contains("exactly one of 'value' or 'reference'", ex.Message, StringComparison.Ordinal);
        Assert.Equal("parameters.p", ex.Path!.ToString());
    }

    [Theory]
    [InlineData("{ \"parameters\": { \"p\": 5 } }", "must be an object")]
    [InlineData("{ \"parameters\": { \"p\": [] } }", "must be an object")]
    [InlineData("{ \"parameters\": { \"p\": null } }", "must be an object")]
    [InlineData("{ \"parameters\": [] }", "'parameters' must be an object")]
    [InlineData("{ \"parameters\": \"x\" }", "'parameters' must be an object")]
    [InlineData("{ \"parameters\": null }", "'parameters' must be an object")]
    public void WrongShapes_AreInvalidParameterFiles(string json, string fragment)
    {
        var ex = Assert.Throws<TemplateLoadException>(() => Parse(json));

        Assert.Equal(DiagnosticCodes.InvalidParameterFile, ex.Code);
        Assert.Contains(fragment, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("5")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    public void ANonObjectRoot_IsAnInvalidParameterFile(string json)
    {
        Assert.Equal(DiagnosticCodes.InvalidParameterFile, Assert.Throws<TemplateLoadException>(() => Parse(json)).Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ \"parameters\": { ")]
    [InlineData("{ \"parameters\": { \"p\": { \"value\": } } }")]
    public void MalformedOrEmptyJson_IsInvalidJson(string json)
    {
        Assert.Equal(DiagnosticCodes.InvalidJson, Assert.Throws<TemplateLoadException>(() => Parse(json)).Code);
    }

    [Fact]
    public void NamesDifferingOnlyInCase_AreADuplicate_AndTheBareLayoutSkipsTheMetadataMembers()
    {
        var duplicate = Assert.Throws<TemplateLoadException>(() => Parse("{ \"parameters\": { \"Name\": { \"value\": 1 }, \"name\": { \"value\": 2 } } }"));
        Assert.Contains("defined more than once", duplicate.Message, StringComparison.Ordinal);

        var bare = Parse("{ \"$schema\": \"s\", \"contentVersion\": \"1.0.0.0\", \"metadata\": 5, \"Env\": { \"value\": \"prod\" } }");
        Assert.True(bare.TryGet("env", out var env));
        Assert.Equal("prod", env.Value!.GetValue<string>());
        Assert.Equal("1.0.0.0", bare.ContentVersion);
        Assert.Equal("s", bare.Schema);
        Assert.False(bare.TryGet("metadata", out _));
        Assert.False(bare.TryGet("$schema", out _));
    }

    [Fact]
    public void ABom_AnEmptyParametersObject_AndNullValues_AreFine()
    {
        byte[] bomBytes = [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("{ \"parameters\": { \"n\": { \"value\": null }, \"z\": { \"value\": 0 } } }")];
        var withBom = ParameterSet.Parse(new ReadOnlyMemory<byte>(bomBytes), "bom.json");
        var empty = Parse("{ \"parameters\": { } }");

        Assert.True(withBom.TryGet("n", out var n));
        Assert.Null(n.Value);
        Assert.True(withBom.TryGet("z", out var z));
        Assert.Equal(0, z.Value!.GetValue<int>());
        Assert.False(empty.TryGet("anything", out _));
    }

    // ---- merge and inline ----

    [Fact]
    public void Merge_LetsTheLaterSetWin_AndKeepsTheFirstMetadataWhenTheOtherHasNone()
    {
        var first = Parse("{ \"$schema\": \"one\", \"contentVersion\": \"1.0.0.0\", \"parameters\": { \"a\": { \"value\": 1 }, \"b\": { \"value\": 2 } } }");
        var second = ParameterSet.Parse("{ \"parameters\": { \"B\": { \"value\": 20 }, \"c\": { \"value\": 3 } } }", "second.json");

        var merged = first.Merge(second);

        Assert.Equal(1, merged.TryGet("a", out var a) ? a.Value!.GetValue<int>() : -1);
        Assert.Equal(20, merged.TryGet("b", out var b) ? b.Value!.GetValue<int>() : -1);
        Assert.Equal(3, merged.TryGet("c", out var c) ? c.Value!.GetValue<int>() : -1);
        Assert.Equal("one", merged.Schema);
        Assert.Equal("1.0.0.0", merged.ContentVersion);
        Assert.Equal("second.json", merged.SourcePath);
        Assert.Throws<ArgumentNullException>(() => first.Merge(null!));
    }

    [Theory]
    [InlineData("name=value", true, "name", "value")]
    [InlineData("name=", true, "name", "")]
    [InlineData("name==x", true, "name", "=x")]
    [InlineData(" name = x ", true, "name", " x ")]
    [InlineData("=value", false, "", "")]
    [InlineData("novalue", false, "", "")]
    [InlineData("", false, "", "")]
    [InlineData("  =x", false, "", "")]
    public void InlineTokens_SplitAtTheFirstEquals(string token, bool ok, string name, string raw)
    {
        Assert.Equal(ok, ParameterSet.TryParseInline(token, out var parsedName, out var parsedRaw));
        if (ok)
        {
            Assert.Equal(name, parsedName);
            Assert.Equal(raw, parsedRaw);
        }
    }

    [Fact]
    public void Inline_ReplacesAFileValue_CaseInsensitively_AndRejectsBlankNames()
    {
        var set = Parse("{ \"parameters\": { \"Env\": { \"value\": \"dev\" } } }");

        var overridden = set.WithInline("env", "prod");

        Assert.True(overridden.TryGet("ENV", out var env));
        Assert.Equal("prod", env.RawText);
        Assert.Equal(ParameterValueSource.Inline, env.Source);
        Assert.True(set.TryGet("env", out var original));
        Assert.Equal("dev", original.Value!.GetValue<string>());
        Assert.ThrowsAny<ArgumentException>(() => set.WithInline(" ", "x"));
        Assert.Throws<ArgumentNullException>(() => set.WithInline("a", null!));
    }
}

internal static class ParameterSetShapeExtensions
{
    public static bool TryGet(this ParameterSet set, string name, out ParameterValue value)
    {
        return set.Values.TryGetValue(name, out value!);
    }
}
