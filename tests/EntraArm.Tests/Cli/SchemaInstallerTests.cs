using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using EntraArm.Cli.Commands;
using EntraArm.Core.Types;
using Xunit;

namespace EntraArm.Tests.Cli;

/// <summary>
/// <c>entraarm schema install</c> (<see cref="SchemaInstaller"/>) in isolated temp directories. The user settings file and the
/// default schema directory are never used: <see cref="SchemaInstallRequest.UserSettingsFile"/> and an explicit schema directory
/// stand in for them, and <see cref="SchemaInstallRequest.SearchFrom"/> decides whether the checkout or the embedded schemas are the source.
/// </summary>
[Collection(CliCollection.Name)]
public sealed class SchemaInstallerTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly string[] FileMatch = ["**/*.entra.json", "**/*.entra.jsonc"];

    private sealed class Layout : IDisposable
    {
        private readonly IsolatedDirectory _root;

        public Layout(string label)
        {
            _root = new IsolatedDirectory(label);
            Workspace = _root.Combine("workspace");
            Directory.CreateDirectory(Workspace);
            SchemaDir = _root.Combine("schemas-out");
            Settings = _root.Combine("workspace", ".vscode", "settings.json");
        }

        public string Root => _root.Path;

        public string Workspace { get; }

        public string SchemaDir { get; }

        public string Settings { get; }

        public string TemplatePath => Path.Combine(SchemaDir, "deploymentTemplate.json");

        public string ExpectedUrl => new Uri(TemplatePath).AbsoluteUri;

        /// <summary>A request that uses the embedded schemas (no checkout above the isolated root) and the workspace settings file.</summary>
        public SchemaInstallRequest Request(Func<SchemaInstallRequest, SchemaInstallRequest>? adjust = null)
        {
            var request = new SchemaInstallRequest
            {
                Workspace = Workspace,
                SchemaDirectory = SchemaDir,
                SearchFrom = _root.Path,
            };
            return adjust is null ? request : adjust(request);
        }

        public void WriteSettings(string text, string? path = null)
        {
            string target = path ?? Settings;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, text, Utf8NoBom);
        }

        public void Dispose()
        {
            _root.Dispose();
        }
    }

    private static (int ExitCode, string Out, string Err) Run(SchemaInstallRequest request)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        int exit = SchemaInstaller.Run(request, stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private static JsonObject ReadSettings(string path)
    {
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }

    private static Dictionary<string, string> Snapshot(string directory)
    {
        return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(directory, f).Replace('\\', '/'), f => File.ReadAllText(f), StringComparer.Ordinal);
    }

    private static Dictionary<string, DateTime> Stamps(string directory)
    {
        return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => File.GetLastWriteTimeUtc(f), StringComparer.Ordinal);
    }

    private static JsonObject EntraArmEntry(JsonObject settings)
    {
        return settings["json.schemas"]!.AsArray().Select(n => n!.AsObject()).Single(e => e["fileMatch"] is JsonArray m && m.Any(x => x!.GetValue<string>() == FileMatch[0]));
    }

    // ---- extraction ----

    [Fact]
    public void Embedded_ExtractsEveryEmbeddedFile_AndRewritesOnlyTheTemplateRefs()
    {
        using var layout = new Layout(nameof(Embedded_ExtractsEveryEmbeddedFile_AndRewritesOnlyTheTemplateRefs));

        var (exit, stdout, stderr) = Run(layout.Request());

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("(from the schemas embedded in this tool)", stdout, StringComparison.Ordinal);
        string prefix = "schemas/" + EmbeddedSchemas.SchemaVersion + "/";
        var embedded = EmbeddedSchemas.Names.Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        var extracted = Snapshot(layout.SchemaDir);
        Assert.Equal(embedded.Select(n => n[prefix.Length..]).Order(StringComparer.Ordinal).ToList(), extracted.Keys.Order(StringComparer.Ordinal).ToList());
        Assert.Contains($"Schemas ({embedded.Count} file(s), {embedded.Count} written)", stdout, StringComparison.Ordinal);

        foreach (var name in embedded.Where(n => !n.EndsWith("/deploymentTemplate.json", StringComparison.Ordinal)))
        {
            Assert.Equal(EmbeddedSchemas.ReadText(name), extracted[name[prefix.Length..]]);
        }

        string template = extracted["deploymentTemplate.json"];
        string baseUri = new Uri(layout.SchemaDir + Path.DirectorySeparatorChar).AbsoluteUri;
        Assert.StartsWith("file:///", baseUri, StringComparison.Ordinal);
        Assert.Contains($"\"$ref\": \"{baseUri}types/Microsoft.Entra/applications.json\"", template, StringComparison.Ordinal);
        Assert.DoesNotContain("\"$ref\": \"https://entraarm.dev", template, StringComparison.Ordinal);
        Assert.Contains("\"$id\": \"https://entraarm.dev/schemas/2026-10-01/deploymentTemplate.json\"", template, StringComparison.Ordinal);
        Assert.Contains("\"$ref\": \"#/$defs/resource\"", template, StringComparison.Ordinal);
    }

    [Fact]
    public void Embedded_EveryRewrittenRef_PointsAtAnExtractedFile()
    {
        using var layout = new Layout(nameof(Embedded_EveryRewrittenRef_PointsAtAnExtractedFile));

        Assert.Equal(0, Run(layout.Request()).ExitCode);

        var refs = Regex.Matches(File.ReadAllText(layout.TemplatePath), "\"\\$ref\"\\s*:\\s*\"(file:///[^\"]+)\"", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2))
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();
        Assert.True(refs.Count >= 4, "the template refers to at least the four nested-capable types");
        foreach (string uri in refs)
        {
            Assert.True(File.Exists(new Uri(uri).LocalPath), uri);
        }
    }

    [Fact]
    public void Checkout_IsTheSourceInsideTheRepository_AndExtractsTheSameContentAsTheEmbeddedSchemas()
    {
        using var embeddedLayout = new Layout(nameof(Checkout_IsTheSourceInsideTheRepository_AndExtractsTheSameContentAsTheEmbeddedSchemas) + "-e");
        using var checkoutLayout = new Layout(nameof(Checkout_IsTheSourceInsideTheRepository_AndExtractsTheSameContentAsTheEmbeddedSchemas) + "-c");

        var embedded = Run(embeddedLayout.Request());
        var checkout = Run(checkoutLayout.Request(r => r with { SearchFrom = TestPaths.RepoRoot }));

        Assert.Equal(0, embedded.ExitCode);
        Assert.Equal(0, checkout.ExitCode);
        Assert.Contains("(from " + Path.Combine(TestPaths.RepoRoot, "schemas", "2026-10-01") + ")", checkout.Out, StringComparison.Ordinal);
        var fromEmbedded = Snapshot(embeddedLayout.SchemaDir);
        var fromCheckout = Snapshot(checkoutLayout.SchemaDir);
        Assert.Equal(fromEmbedded.Keys.Order(StringComparer.Ordinal).ToList(), fromCheckout.Keys.Order(StringComparer.Ordinal).ToList());

        // The template differs only by the base URI of the rewritten refs, which names each layout's own directory.
        foreach (var pair in fromEmbedded)
        {
            string left = pair.Key == "deploymentTemplate.json" ? pair.Value.Replace(new Uri(embeddedLayout.SchemaDir + Path.DirectorySeparatorChar).AbsoluteUri, "BASE/", StringComparison.Ordinal) : pair.Value;
            string right = pair.Key == "deploymentTemplate.json" ? fromCheckout[pair.Key].Replace(new Uri(checkoutLayout.SchemaDir + Path.DirectorySeparatorChar).AbsoluteUri, "BASE/", StringComparison.Ordinal) : fromCheckout[pair.Key];
            Assert.Equal(left.Replace("\r\n", "\n", StringComparison.Ordinal), right.Replace("\r\n", "\n", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task SecondRun_RewritesNothing()
    {
        using var layout = new Layout(nameof(SecondRun_RewritesNothing));
        Assert.Equal(0, Run(layout.Request()).ExitCode);
        var schemaStamps = Stamps(layout.SchemaDir);
        var settingsStamp = File.GetLastWriteTimeUtc(layout.Settings);
        string settingsText = File.ReadAllText(layout.Settings);
        await Task.Delay(80, TestContext.Current.CancellationToken);

        var (exit, stdout, stderr) = Run(layout.Request());

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("up to date", stdout, StringComparison.Ordinal);
        Assert.Contains("nothing to change", stdout, StringComparison.Ordinal);
        Assert.Equal(schemaStamps, Stamps(layout.SchemaDir));
        Assert.Equal(settingsStamp, File.GetLastWriteTimeUtc(layout.Settings));
        Assert.Equal(settingsText, File.ReadAllText(layout.Settings));
    }

    [Fact]
    public void ARunAfterOneSchemaFileChanged_RewritesExactlyThatFile()
    {
        using var layout = new Layout(nameof(ARunAfterOneSchemaFileChanged_RewritesExactlyThatFile));
        Assert.Equal(0, Run(layout.Request()).ExitCode);
        string edited = Path.Combine(layout.SchemaDir, "types", "Microsoft.Entra", "groups.json");
        File.WriteAllText(edited, "{ \"stale\": true }", Utf8NoBom);

        var (exit, stdout, _) = Run(layout.Request());

        Assert.Equal(0, exit);
        Assert.Contains("1 written", stdout, StringComparison.Ordinal);
        Assert.Equal(EmbeddedSchemas.ReadText(EmbeddedSchemas.TypeSchemaPrefix + "groups.json"), File.ReadAllText(edited));
    }

    // ---- settings merge ----

    [Fact]
    public void NewWorkspace_CreatesVscodeSettings_WithTheDocumentedMapping()
    {
        using var layout = new Layout(nameof(NewWorkspace_CreatesVscodeSettings_WithTheDocumentedMapping));
        Assert.False(File.Exists(layout.Settings));

        var (exit, stdout, _) = Run(layout.Request());

        Assert.Equal(0, exit);
        Assert.Contains($"Created {layout.Settings}", stdout, StringComparison.Ordinal);
        var entry = Assert.Single(ReadSettings(layout.Settings)["json.schemas"]!.AsArray())!.AsObject();
        Assert.Equal(FileMatch, entry["fileMatch"]!.AsArray().Select(m => m!.GetValue<string>()).ToArray());
        Assert.Equal(layout.ExpectedUrl, entry["url"]!.GetValue<string>());
        Assert.StartsWith("file:///", entry["url"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.EndsWith("/deploymentTemplate.json", entry["url"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingSettings_KeepOtherKeysAndOtherSchemaEntries_AndTheirIndentation()
    {
        using var layout = new Layout(nameof(ExistingSettings_KeepOtherKeysAndOtherSchemaEntries_AndTheirIndentation));
        layout.WriteSettings("{\n  \"editor.tabSize\": 2,\n  \"json.schemas\": [\n    { \"fileMatch\": [\"/package.json\"], \"url\": \"https://example.test/package.json\" }\n  ],\n  \"files.exclude\": { \"**/.git\": true }\n}\n");

        var (exit, stdout, stderr) = Run(layout.Request());

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains($"Updated {layout.Settings}", stdout, StringComparison.Ordinal);
        var settings = ReadSettings(layout.Settings);
        Assert.Equal(2, settings["editor.tabSize"]!.GetValue<int>());
        Assert.True(settings["files.exclude"]!["**/.git"]!.GetValue<bool>());
        var schemas = settings["json.schemas"]!.AsArray();
        Assert.Equal(2, schemas.Count);
        Assert.Equal("https://example.test/package.json", schemas[0]!["url"]!.GetValue<string>());
        Assert.Equal(layout.ExpectedUrl, schemas[1]!["url"]!.GetValue<string>());
        Assert.Equal(["editor.tabSize", "json.schemas", "files.exclude"], settings.Select(p => p.Key).ToArray());
        string text = File.ReadAllText(layout.Settings);
        Assert.Contains("\n  \"editor.tabSize\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n    \"editor.tabSize\"", text, StringComparison.Ordinal);
        Assert.False(File.Exists(layout.Settings + ".entraarm.bak"));
    }

    [Fact]
    public void ExistingTabIndentation_IsKept()
    {
        using var layout = new Layout(nameof(ExistingTabIndentation_IsKept));
        layout.WriteSettings("{\n\t\"editor.tabSize\": 2\n}\n");

        Assert.Equal(0, Run(layout.Request()).ExitCode);

        string text = File.ReadAllText(layout.Settings);
        Assert.Contains("\n\t\"editor.tabSize\"", text, StringComparison.Ordinal);
        Assert.Contains("\n\t\"json.schemas\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEntraArmEntryWithAnOldUrl_IsUpdatedInPlace_AndNeverDuplicated()
    {
        using var layout = new Layout(nameof(AnEntraArmEntryWithAnOldUrl_IsUpdatedInPlace_AndNeverDuplicated));
        layout.WriteSettings(
            "{ \"json.schemas\": [" +
            "{ \"fileMatch\": [\"/a.json\"], \"url\": \"https://example.test/a.json\" }," +
            "{ \"fileMatch\": [\"**/*.entra.json\"], \"url\": \"file:///C:/old/EntraArm/schemas/2026-10-01/deploymentTemplate.json\" }," +
            "{ \"fileMatch\": [\"/b.json\"], \"url\": \"https://example.test/b.json\" }," +
            "{ \"fileMatch\": [\"**/*.other.json\"], \"url\": \"file:///D:/elsewhere/EntraArm/schemas/2026-10-01/deploymentTemplate.json\" }" +
            "] }");

        var (exit, _, _) = Run(layout.Request());

        Assert.Equal(0, exit);
        var schemas = ReadSettings(layout.Settings)["json.schemas"]!.AsArray();
        Assert.Equal(["https://example.test/a.json", layout.ExpectedUrl, "https://example.test/b.json"], schemas.Select(s => s!["url"]!.GetValue<string>()).ToArray());
        Assert.Equal(FileMatch, schemas[1]!["fileMatch"]!.AsArray().Select(m => m!.GetValue<string>()).ToArray());

        // Idempotent from here.
        string settled = File.ReadAllText(layout.Settings);
        Assert.Contains("nothing to change", Run(layout.Request()).Out, StringComparison.Ordinal);
        Assert.Equal(settled, File.ReadAllText(layout.Settings));
    }

    [Fact]
    public void ADifferentSchemaDirectory_UpdatesTheUrlOfTheExistingEntry()
    {
        using var layout = new Layout(nameof(ADifferentSchemaDirectory_UpdatesTheUrlOfTheExistingEntry));
        Assert.Equal(0, Run(layout.Request()).ExitCode);
        string moved = Path.Combine(layout.Root, "moved-schemas");

        var (exit, stdout, _) = Run(layout.Request(r => r with { SchemaDirectory = moved }));

        Assert.Equal(0, exit);
        Assert.Contains($"Updated {layout.Settings}", stdout, StringComparison.Ordinal);
        var entry = EntraArmEntry(ReadSettings(layout.Settings));
        Assert.Equal(new Uri(Path.Combine(moved, "deploymentTemplate.json")).AbsoluteUri, entry["url"]!.GetValue<string>());
        Assert.Single(ReadSettings(layout.Settings)["json.schemas"]!.AsArray());
    }

    [Fact]
    public void CommentsAndTrailingCommas_AreReadButNotPreserved_TheOriginalIsSavedAsBak()
    {
        using var layout = new Layout(nameof(CommentsAndTrailingCommas_AreReadButNotPreserved_TheOriginalIsSavedAsBak));
        string original = "{\n  // keep the tabs narrow\n  \"editor.tabSize\": 2,\n  \"files.autoSave\": \"off\", /* trailing comma follows */\n}\n";
        layout.WriteSettings(original);

        var (exit, _, stderr) = Run(layout.Request());

        Assert.Equal(0, exit);
        string backup = layout.Settings + ".entraarm.bak";
        Assert.True(File.Exists(backup));
        Assert.Equal(original, File.ReadAllText(backup));
        Assert.Contains("warning:", stderr, StringComparison.Ordinal);
        Assert.Contains("comments or trailing commas", stderr, StringComparison.Ordinal);
        Assert.Contains("settings.json.entraarm.bak", stderr, StringComparison.Ordinal);
        var settings = ReadSettings(layout.Settings);
        Assert.Equal(2, settings["editor.tabSize"]!.GetValue<int>());
        Assert.Equal("off", settings["files.autoSave"]!.GetValue<string>());
        Assert.DoesNotContain("keep the tabs narrow", File.ReadAllText(layout.Settings), StringComparison.Ordinal);

        // The rewritten file is plain JSON now: the next run changes nothing and does not touch the backup.
        var again = Run(layout.Request());
        Assert.Equal(0, again.ExitCode);
        Assert.Equal(string.Empty, again.Err);
        Assert.Equal(original, File.ReadAllText(backup));
    }

    [Fact]
    public void CommentsInAnUpToDateSettingsFile_AreLeftAlone_WithoutABackup()
    {
        using var layout = new Layout(nameof(CommentsInAnUpToDateSettingsFile_AreLeftAlone_WithoutABackup));
        Assert.Equal(0, Run(layout.Request()).ExitCode);
        var settings = ReadSettings(layout.Settings);
        string commented = "// my settings\n" + settings.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n";
        layout.WriteSettings(commented);

        var (exit, stdout, stderr) = Run(layout.Request());

        Assert.Equal(0, exit);
        Assert.Contains("nothing to change", stdout, StringComparison.Ordinal);
        Assert.Equal(string.Empty, stderr);
        Assert.Equal(commented, File.ReadAllText(layout.Settings));
        Assert.False(File.Exists(layout.Settings + ".entraarm.bak"));
    }

    [Theory]
    [InlineData("{ \"json.schemas\": [ ")]
    [InlineData("[ 1, 2 ]")]
    [InlineData("{ \"json.schemas\": { \"fileMatch\": [] } }")]
    public void UnusableSettings_ExitOne_AndAreLeftUntouched(string content)
    {
        using var layout = new Layout(nameof(UnusableSettings_ExitOne_AndAreLeftUntouched));
        layout.WriteSettings(content);

        var (exit, _, stderr) = Run(layout.Request());

        Assert.Equal(1, exit);
        Assert.StartsWith("schema install failed: ", stderr, StringComparison.Ordinal);
        Assert.Equal(content, File.ReadAllText(layout.Settings));
        Assert.False(File.Exists(layout.Settings + ".entraarm.bak"));
    }

    [Fact]
    public void AnEmptySettingsFile_IsTreatedAsNew()
    {
        using var layout = new Layout(nameof(AnEmptySettingsFile_IsTreatedAsNew));
        layout.WriteSettings("  \n");

        var (exit, _, _) = Run(layout.Request());

        Assert.Equal(0, exit);
        Assert.Equal(layout.ExpectedUrl, EntraArmEntry(ReadSettings(layout.Settings))["url"]!.GetValue<string>());
        Assert.False(File.Exists(layout.Settings + ".entraarm.bak"));
    }

    // ---- where the settings go ----

    [Fact]
    public void SettingsFile_OverridesTheWorkspaceAndGlobal()
    {
        using var layout = new Layout(nameof(SettingsFile_OverridesTheWorkspaceAndGlobal));
        string explicitFile = Path.Combine(layout.Root, "elsewhere", "my-settings.json");

        var (exit, _, _) = Run(layout.Request(r => r with
        {
            SettingsFile = explicitFile,
            Global = true,
            UserSettingsFile = Path.Combine(layout.Root, "no-such-dir", "settings.json"),
        }));

        Assert.Equal(0, exit);
        Assert.True(File.Exists(explicitFile));
        Assert.False(File.Exists(layout.Settings));
        Assert.Equal(layout.ExpectedUrl, EntraArmEntry(ReadSettings(explicitFile))["url"]!.GetValue<string>());
    }

    [Fact]
    public void Global_WithNoVsCodeUserDirectory_ExitsOne_AndCreatesNothingThere()
    {
        using var layout = new Layout(nameof(Global_WithNoVsCodeUserDirectory_ExitsOne_AndCreatesNothingThere));
        string userDirectory = Path.Combine(layout.Root, "AppData", "Code", "User");
        string userSettings = Path.Combine(userDirectory, "settings.json");

        var (exit, _, stderr) = Run(layout.Request(r => r with { Global = true, UserSettingsFile = userSettings }));

        Assert.Equal(1, exit);
        Assert.Contains("schema install failed:", stderr, StringComparison.Ordinal);
        Assert.Contains("does not exist", stderr, StringComparison.Ordinal);
        Assert.Contains("--settings-file", stderr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(userDirectory));
        Assert.False(File.Exists(layout.Settings));
    }

    [Fact]
    public void Global_WithAVsCodeUserDirectory_WritesTheUserSettings_NotTheWorkspace()
    {
        using var layout = new Layout(nameof(Global_WithAVsCodeUserDirectory_WritesTheUserSettings_NotTheWorkspace));
        string userDirectory = Path.Combine(layout.Root, "AppData", "Code", "User");
        Directory.CreateDirectory(userDirectory);
        string userSettings = Path.Combine(userDirectory, "settings.json");
        File.WriteAllText(userSettings, "{ \"window.zoomLevel\": 1 }", Utf8NoBom);

        var (exit, _, _) = Run(layout.Request(r => r with { Global = true, UserSettingsFile = userSettings }));

        Assert.Equal(0, exit);
        var settings = ReadSettings(userSettings);
        Assert.Equal(1, settings["window.zoomLevel"]!.GetValue<int>());
        Assert.Equal(layout.ExpectedUrl, EntraArmEntry(settings)["url"]!.GetValue<string>());
        Assert.False(File.Exists(layout.Settings));
    }

    // ---- command line ----

    [Fact]
    public async Task Cli_SettingsFileAndSchemaDir_AreHonoured()
    {
        using var layout = new Layout(nameof(Cli_SettingsFileAndSchemaDir_AreHonoured));
        string settingsFile = Path.Combine(layout.Root, "custom", "settings.json");

        var result = await CliRunner.RunAsync("schema", "install", "--settings-file", settingsFile, "--schema-dir", layout.SchemaDir);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        Assert.True(File.Exists(layout.TemplatePath));
        Assert.True(File.Exists(Path.Combine(layout.SchemaDir, "types", "Microsoft.Entra", "applications.json")));
        Assert.Equal(layout.ExpectedUrl, EntraArmEntry(ReadSettings(settingsFile))["url"]!.GetValue<string>());
        Assert.Contains(layout.SchemaDir, result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cli_Workspace_GetsItsOwnVscodeSettings()
    {
        using var layout = new Layout(nameof(Cli_Workspace_GetsItsOwnVscodeSettings));

        var first = await CliRunner.RunAsync("schema", "install", "--workspace", layout.Workspace, "--schema-dir", layout.SchemaDir);
        var second = await CliRunner.RunAsync("schema", "install", "--workspace", layout.Workspace, "--schema-dir", layout.SchemaDir);

        Assert.Equal(0, first.ExitCode);
        Assert.Contains("Created", first.StandardOutput, StringComparison.Ordinal);
        Assert.True(File.Exists(layout.Settings));
        Assert.Equal(0, second.ExitCode);
        Assert.Contains("up to date", second.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("nothing to change", second.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cli_InvalidSettingsFile_ExitsOne()
    {
        using var layout = new Layout(nameof(Cli_InvalidSettingsFile_ExitsOne));
        string settingsFile = Path.Combine(layout.Root, "bad.json");
        layout.WriteSettings("{ nope", settingsFile);

        var result = await CliRunner.RunAsync("schema", "install", "--settings-file", settingsFile, "--schema-dir", layout.SchemaDir);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("is not valid JSON", result.StandardError, StringComparison.Ordinal);
    }

    // ---- RewriteRefs and locations ----

    [Fact]
    public void RewriteRefs_ChangesRefsUnderThePublishedBase_AndLeavesIdsAndLocalRefsAlone()
    {
        const string text =
            "{ \"$id\": \"https://entraarm.dev/schemas/2026-10-01/deploymentTemplate.json\", " +
            "\"a\": { \"$ref\": \"https://entraarm.dev/schemas/2026-10-01/types/Microsoft.Entra/groups.json\" }, " +
            "\"b\": { \"$ref\" :  \"https://entraarm.dev/schemas/2026-10-01/types/Microsoft.Entra/users.json\" }, " +
            "\"c\": { \"$ref\": \"#/$defs/resource\" }, " +
            "\"d\": { \"$ref\": \"https://elsewhere.test/schemas/2026-10-01/x.json\" }, " +
            "\"note\": \"see https://entraarm.dev/schemas/2026-10-01/types for the published copy\" }";

        string rewritten = SchemaInstaller.RewriteRefs(text, "file:///C:/out/");

        Assert.Contains("\"$id\": \"https://entraarm.dev/schemas/2026-10-01/deploymentTemplate.json\"", rewritten, StringComparison.Ordinal);
        Assert.Contains("\"$ref\": \"file:///C:/out/types/Microsoft.Entra/groups.json\"", rewritten, StringComparison.Ordinal);
        Assert.Contains("\"$ref\" :  \"file:///C:/out/types/Microsoft.Entra/users.json\"", rewritten, StringComparison.Ordinal);
        Assert.Contains("\"$ref\": \"#/$defs/resource\"", rewritten, StringComparison.Ordinal);
        Assert.Contains("\"$ref\": \"https://elsewhere.test/schemas/2026-10-01/x.json\"", rewritten, StringComparison.Ordinal);
        Assert.Contains("see https://entraarm.dev/schemas/2026-10-01/types for the published copy", rewritten, StringComparison.Ordinal);
        Assert.Equal(text, SchemaInstaller.RewriteRefs(text, "https://entraarm.dev/schemas/2026-10-01/"));
    }

    [Fact]
    public void RewriteRefs_OnTheRealTemplate_TouchesNoIdAndNoLocalRef()
    {
        string template = EmbeddedSchemas.ReadText(EmbeddedSchemas.TemplateSchemaResource);

        string rewritten = SchemaInstaller.RewriteRefs(template, "file:///C:/out/");

        Assert.Equal(CountMatches(template, "\"$id\""), CountMatches(rewritten, "\"$id\""));
        Assert.Contains("\"$id\": \"https://entraarm.dev/schemas/2026-10-01/deploymentTemplate.json\"", rewritten, StringComparison.Ordinal);
        Assert.Equal(CountMatches(template, "\"$ref\": \"#/"), CountMatches(rewritten, "\"$ref\": \"#/"));
        Assert.DoesNotContain("\"$ref\": \"https://entraarm.dev", rewritten, StringComparison.Ordinal);
    }

    private static int CountMatches(string text, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    [Fact]
    public void DefaultLocations_FollowTheDocumentedLayout()
    {
        string schemaDirectory = SchemaInstaller.DefaultSchemaDirectory();
        string userSettings = SchemaInstaller.UserSettingsPath();

        Assert.EndsWith(Path.Combine("EntraArm", "schemas", "2026-10-01"), schemaDirectory, StringComparison.Ordinal);
        Assert.EndsWith(Path.Combine("Code", "User", "settings.json"), userSettings, StringComparison.Ordinal);
        if (OperatingSystem.IsWindows())
        {
            Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), schemaDirectory, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), userSettings, StringComparison.OrdinalIgnoreCase);
        }
    }
}
