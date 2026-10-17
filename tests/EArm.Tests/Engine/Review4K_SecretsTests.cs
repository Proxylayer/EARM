using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using EArm.Core;
using EArm.Core.Secrets;
using EArm.Engine.Execution;
using EArm.Graph;
using Xunit;

namespace EArm.Tests.Engine;

/// <summary>
/// <see cref="SecretRegistration"/> (what is masked from every message, and how strictly) and <see cref="SecureOutputsFile"/> (the one file that may hold the only copy of a
/// one-time secret): the decisions a refactor must not change silently.
/// </summary>
public sealed class Review4K_SecretsTests
{
    private static TemplateDocument Document(string parametersJson, string valuesJson)
    {
        string body = "\"parameters\": " + parametersJson + ", \"resources\": []";
        return Expr.Parse(Expr.Envelope(body), ParameterSet.Parse("{ \"parameters\": " + valuesJson + " }"));
    }

    // ---- RegisterDocument ----

    [Fact]
    public void ANonSecureParameter_IsNotMasked_ASecureOneIs()
    {
        using var scope = SecretScrubber.Begin();
        var document = Document(
            "{ \"plain\": { \"type\": \"string\" }, \"pw\": { \"type\": \"securestring\" } }",
            "{ \"plain\": { \"value\": \"visible-text-123\" }, \"pw\": { \"value\": \"hidden-text-456\" } }");

        SecretRegistration.RegisterDocument(document);

        Assert.Equal("visible-text-123 and ***", SecretRegistration.Scrub("visible-text-123 and hidden-text-456"));
    }

    [Fact]
    public void NumbersAndBooleansOfASecureObject_AreNeverRegistered_TheirStringsAre()
    {
        using var scope = SecretScrubber.Begin();
        var document = Document(
            "{ \"obj\": { \"type\": \"secureObject\" } }",
            "{ \"obj\": { \"value\": { \"count\": 12345, \"flag\": true, \"word\": \"hidden-word-1\" } } }");

        SecretRegistration.RegisterDocument(document);

        Assert.Equal("12345 true and ***", SecretRegistration.Scrub("12345 true and hidden-word-1"));
    }

    [Fact]
    public void EveryStringLeaf_NestedInObjectsAndArrays_OfASecureParameter_IsRegistered()
    {
        using var scope = SecretScrubber.Begin();
        var document = Document(
            "{ \"obj\": { \"type\": \"secureObject\" } }",
            "{ \"obj\": { \"value\": { \"a\": { \"b\": [ \"deep-leaf-one\", { \"c\": \"deep-leaf-two\", \"d\": [ \"deep-leaf-three\" ] } ] }, \"top\": \"top-leaf-four\" } } }");

        SecretRegistration.RegisterDocument(document);

        Assert.Equal("*** *** *** ***", SecretRegistration.Scrub("deep-leaf-one deep-leaf-two deep-leaf-three top-leaf-four"));
    }

    [Fact]
    public void ASecureParameterWithoutAValue_RegistersNothing_AndDoesNotThrow()
    {
        using var scope = SecretScrubber.Begin();
        var document = Document("{ \"pw\": { \"type\": \"securestring\" } }", "{ }");

        SecretRegistration.RegisterDocument(document);

        Assert.Equal("text", SecretRegistration.Scrub("text"));
    }

    // ---- RegisterTainted / RegisterAllLeaves ----

    [Fact]
    public void ATaintedLeaf_OfAString_IsRegistered_ButNotANumberOrABoolean()
    {
        using var scope = SecretScrubber.Begin();
        var tree = new JsonObject
        {
            ["s"] = SecretJson.CreateString("tainted-string-9"),
            ["n"] = SecretJson.Create(new SecretValue("98765", System.Text.Json.JsonValueKind.Number)),
            ["b"] = SecretJson.Create(new SecretValue("true", System.Text.Json.JsonValueKind.True)),
            ["nested"] = new JsonArray(SecretJson.CreateString("tainted-nested-7")),
            ["plain"] = "plain-leaf-text",
        };

        SecretRegistration.RegisterTainted(tree);

        Assert.Equal("*** 98765 true *** plain-leaf-text", SecretRegistration.Scrub("tainted-string-9 98765 true tainted-nested-7 plain-leaf-text"));
    }

    [Fact]
    public void ADerivedShortLeaf_DoesNotShredUnrelatedText_ButASecureDeclaredOneStillMasks()
    {
        // "a1b" is 3 characters: below the 8-character boundary rule a derived (tainted, not declared) value is matched as a whole word only.
        using (SecretScrubber.Begin())
        {
            SecretRegistration.RegisterTainted(SecretJson.CreateString("a1b"));
            Assert.Equal("data1bc a1bc ba1b *** (***)", SecretRegistration.Scrub("data1bc a1bc ba1b a1b (a1b)"));
        }

        using (SecretScrubber.Begin())
        {
            SecretRegistration.RegisterAllLeaves(SecretJson.CreateString("a1b"));
            Assert.Equal("dat***c ***c b*** *** (***)", SecretRegistration.Scrub("data1bc a1bc ba1b a1b (a1b)"));
        }

        using (SecretScrubber.Begin())
        {
            // A plain string leaf of a secure parameter value is secure-declared too.
            SecretRegistration.RegisterAllLeaves(new JsonArray("a1b"));
            Assert.Equal("dat***c", SecretRegistration.Scrub("data1bc"));
        }
    }

    [Fact]
    public void ASecureDeclaredParameter_WithAShortValue_IsMaskedAsASubstring()
    {
        using var scope = SecretScrubber.Begin();
        var document = Document("{ \"pw\": { \"type\": \"securestring\" } }", "{ \"pw\": { \"value\": \"abcd\" } }");

        SecretRegistration.RegisterDocument(document);

        Assert.Equal("x***y", SecretRegistration.Scrub("xabcdy"));
    }

    // ---- SecureOutputsFile ----

    private static string NewFile(out string folder, string name = "secure.json")
    {
        folder = Path.Combine(AppContext.BaseDirectory, "engine-runs", "r4k-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, name);
    }

    /// <summary>
    /// Removes a test folder without failing the test. The merge's <c>&lt;file&gt;.lock</c> is deleted on close; when a scanner or indexer still has it open at that
    /// moment the delete stays pending, and Windows refuses to delete the folder ("access denied" on the lock) until the scanner lets go. A plain
    /// <c>Directory.Delete</c> at the end of a test turned that moment into a flaky failure in a busy full run, so this waits it out briefly and then leaves the
    /// folder to the next clean build.
    /// </summary>
    private static void DeleteFolder(string folder)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 40)
                {
                    return;
                }

                Thread.Sleep(50);
            }
        }
    }

    [Fact]
    public void APathWithATrailingSeparator_IsADirectoryError_EvenWhenNoSuchFolderExists()
    {
        string path = NewFile(out string folder, "does-not-exist-yet") + Path.DirectorySeparatorChar;

        string? problem = SecureOutputsFile.Validate(path);

        Assert.NotNull(problem);
        Assert.Contains("is a directory", problem, StringComparison.Ordinal);
        DeleteFolder(folder);
    }

    [Fact]
    public void AnExistingDirectory_AndAMissingFolder_AreDistinctErrors()
    {
        _ = NewFile(out string folder);

        Assert.Contains("is a directory", SecureOutputsFile.Validate(folder), StringComparison.Ordinal);
        Assert.Contains("does not exist", SecureOutputsFile.Validate(Path.Combine(folder, "missing", "file.json")), StringComparison.Ordinal);
        Assert.Null(SecureOutputsFile.Validate(Path.Combine(folder, "ok.json")));
        DeleteFolder(folder);
    }

    [Fact]
    public void AnOutputNamedLikeTheOneTimeSecretsBucket_CannotReplaceTheBucket_AndNeverReachesTheFile()
    {
        string path = NewFile(out string folder);
        SecureOutputsFile.MergeOneTimeSecret(path, "/providers/Microsoft.Entra/applications/app/passwordCredentials/ci", new JsonObject { ["secretText"] = "only-copy" });

        bool written = SecureOutputsFile.MergeOutputs(path, new JsonObject
        {
            [SecureOutputsFile.OneTimeSecretsKey] = new JsonObject { ["type"] = "securestring", ["value"] = "attacker-controlled" },
            ["other"] = new JsonObject { ["type"] = "securestring", ["value"] = "kept" },
        });

        Assert.True(written);
        var document = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal("only-copy", document[SecureOutputsFile.OneTimeSecretsKey]!["/providers/Microsoft.Entra/applications/app/passwordCredentials/ci"]!["secretText"]!.GetValue<string>());
        Assert.DoesNotContain("attacker-controlled", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Equal("kept", document["other"]!["value"]!.GetValue<string>());
        Assert.Equal(SecureOutputsFile.OneTimeSecretsKey, document.Select(p => p.Key).Last());
        DeleteFolder(folder);
    }

    [Fact]
    public void AnOutputNamedLikeTheBucket_WithoutABucket_IsSkipped_AndAFileHoldingOnlyThatIsNotWritten()
    {
        string path = NewFile(out string folder);

        bool written = SecureOutputsFile.MergeOutputs(path, new JsonObject { [SecureOutputsFile.OneTimeSecretsKey] = new JsonObject { ["type"] = "securestring", ["value"] = "x" } });

        Assert.True(written);
        var document = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
        Assert.False(document.ContainsKey(SecureOutputsFile.OneTimeSecretsKey));
        DeleteFolder(folder);
    }

    [Fact]
    public void ANonObjectJsonDocument_IsKeptNextToTheNewFile_AsPrevious()
    {
        string path = NewFile(out string folder);
        File.WriteAllText(path, "[ \"precious\" ]");

        SecureOutputsFile.MergeOneTimeSecret(path, "/providers/Microsoft.Entra/users/u", new JsonObject { ["generatedPassword"] = "pw-1" });

        Assert.Equal("[ \"precious\" ]", File.ReadAllText(path + ".previous"));
        var document = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal("pw-1", document[SecureOutputsFile.OneTimeSecretsKey]!["/providers/Microsoft.Entra/users/u"]!["generatedPassword"]!.GetValue<string>());
        DeleteFolder(folder);
    }

    [Fact]
    public void UnparseableText_IsKeptAsPrevious_Too()
    {
        string path = NewFile(out string folder);
        File.WriteAllText(path, "this is {{ not json");

        SecureOutputsFile.MergeOneTimeSecret(path, "/providers/Microsoft.Entra/users/u", new JsonObject { ["generatedPassword"] = "pw-1" });

        Assert.Equal("this is {{ not json", File.ReadAllText(path + ".previous"));
        DeleteFolder(folder);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n\t ")]
    public void ABlankExistingFile_IsNotKeptAsPrevious(string existing)
    {
        string path = NewFile(out string folder);
        File.WriteAllText(path, existing);

        SecureOutputsFile.MergeOneTimeSecret(path, "/providers/Microsoft.Entra/users/u", new JsonObject { ["generatedPassword"] = "pw-1" });

        Assert.False(File.Exists(path + ".previous"));
        Assert.True(File.Exists(path));
        DeleteFolder(folder);
    }

    [Fact]
    public void AnObjectDocument_IsMerged_NotReplaced_AndNeverKeptAsPrevious()
    {
        string path = NewFile(out string folder);
        File.WriteAllText(path, "{ \"earlier\": { \"type\": \"securestring\", \"value\": \"old\" } }");

        SecureOutputsFile.MergeOneTimeSecret(path, "/providers/Microsoft.Entra/users/u", new JsonObject { ["generatedPassword"] = "pw-1" });

        Assert.False(File.Exists(path + ".previous"));
        var document = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal("old", document["earlier"]!["value"]!.GetValue<string>());
        Assert.NotNull(document[SecureOutputsFile.OneTimeSecretsKey]);
        DeleteFolder(folder);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void AnEmptyResourceId_IsRefused_AndNothingIsWritten(string resourceId)
    {
        string path = NewFile(out string folder);

        Assert.ThrowsAny<ArgumentException>(() => SecureOutputsFile.MergeOneTimeSecret(path, resourceId, new JsonObject { ["secretText"] = "x" }));
        Assert.Throws<ArgumentNullException>(() => SecureOutputsFile.MergeOneTimeSecret(path, null!, new JsonObject()));
        Assert.Throws<ArgumentNullException>(() => SecureOutputsFile.MergeOneTimeSecret(path, "/providers/Microsoft.Entra/users/u", null!));

        Assert.False(File.Exists(path));
        DeleteFolder(folder);
    }

    [Fact]
    public void NoSecureOutputs_WritesNothing_NotEvenAnEmptyObject()
    {
        string path = NewFile(out string folder);

        Assert.False(SecureOutputsFile.MergeOutputs(path, new JsonObject()));

        Assert.False(File.Exists(path));
        DeleteFolder(folder);
    }
}
