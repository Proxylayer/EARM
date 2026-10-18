using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EArm.Cli.Output;
using EArm.Core.Json;
using EArm.Core.Types;

namespace EArm.Cli.Commands;

/// <summary>What <c>schema install</c> was asked to do.</summary>
internal sealed record SchemaInstallRequest
{
    public bool Global { get; init; }

    public string? Workspace { get; init; }

    public string? SettingsFile { get; init; }

    public string? SchemaDirectory { get; init; }

    public OutputFormat Output { get; init; } = OutputFormat.Table;

    /// <summary>Overrides the user settings location (tests); <c>null</c> uses the VS Code default of the platform.</summary>
    public string? UserSettingsFile { get; init; }

    /// <summary>The directory searched upwards for a <c>schemas/2026-10-01</c> checkout; <c>null</c> uses the workspace directory.</summary>
    public string? SearchFrom { get; init; }
}

/// <summary>
/// <c>earm schema install</c>: extracts the schemas to a stable per-user directory and merges a
/// <c>json.schemas</c> entry (<c>fileMatch: ["**/*.entra.json", "**/*.entra.jsonc"]</c>) into the VS Code settings.
/// </summary>
/// <remarks>
/// The extracted <c>deploymentTemplate.json</c> refers to the per-type schemas by absolute <c>file:///</c> URI instead of
/// the <c>https://schema.earm.proxylayer.net/...</c> ids (which are not published): VS Code resolves every <c>$ref</c> locally and
/// works offline. The <c>$id</c>s are untouched. When the working tree holds a <c>schemas/2026-10-01</c> directory
/// (running inside the EARM repository) it is the source of the copy, so schema edits are picked up by running
/// the verb again; otherwise the schemas embedded in this tool are extracted. The copy is idempotent: files whose
/// content is unchanged are not rewritten and an up-to-date settings entry leaves the settings file untouched.
/// </remarks>
internal static class SchemaInstaller
{
    public static readonly string[] FileMatch = ["**/*.entra.json", "**/*.entra.jsonc"];

    private const string SchemasKey = "json.schemas";
    private const string RefPrefix = "https://schema.earm.proxylayer.net/schemas/" + EmbeddedSchemas.SchemaVersion + "/";

    private static readonly Regex RefValue = new("(\"\\$ref\"\\s*:\\s*\")" + Regex.Escape(RefPrefix), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    public static int Run(SchemaInstallRequest request, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return RunCore(request, stdout, stderr);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
        {
            return FailureDocument.Write(request.Output, stdout, stderr, ValidateCommand.InvalidArgumentCode, "schema install failed: " + ex.Message, ExitCodes.Failure);
        }
    }

    private static int RunCore(SchemaInstallRequest request, TextWriter stdout, TextWriter stderr)
    {
        string workspace = Path.GetFullPath(request.Workspace ?? Directory.GetCurrentDirectory());
        string destination = Path.GetFullPath(request.SchemaDirectory ?? DefaultSchemaDirectory());

        // 1. Extract (or copy from the checkout) the schemas.
        string? checkout = FindCheckout(request.SearchFrom ?? workspace);
        var files = checkout is null ? EmbeddedFiles() : CheckoutFiles(checkout);
        string baseUri = DirectoryUri(destination);
        int written = 0;
        foreach (var (relative, content) in files)
        {
            string text = relative.Equals("deploymentTemplate.json", StringComparison.Ordinal) ? RewriteRefs(content, baseUri) : content;
            string target = Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(target) && File.ReadAllText(target, Encoding.UTF8) == text)
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, text, new UTF8Encoding(false));
            written++;
        }

        bool json = request.Output == OutputFormat.Json;
        bool table = request.Output == OutputFormat.Table;
        if (table)
        {
            stdout.WriteLine($"Schemas ({files.Count} file(s), {(written == 0 ? "up to date" : $"{written} written")}) at {destination}{(checkout is null ? " (from the schemas embedded in this tool)" : $" (from {checkout})")}.");
        }

        // 2. Merge the mapping into the settings file.
        string settingsPath = ResolveSettingsPath(request, workspace);
        string url = new Uri(Path.Combine(destination, "deploymentTemplate.json")).AbsoluteUri;
        var outcome = MergeSettings(settingsPath, url);
        if (table)
        {
            switch (outcome.Status)
            {
                case SettingsStatus.Created:
                    stdout.WriteLine($"Created {settingsPath} with the json.schemas mapping for {string.Join(", ", FileMatch)}.");
                    break;
                case SettingsStatus.Updated:
                    stdout.WriteLine($"Updated {settingsPath}: json.schemas maps {string.Join(", ", FileMatch)} to {url}.");
                    break;
                default:
                    stdout.WriteLine($"{settingsPath} already maps {string.Join(", ", FileMatch)} to {url}; nothing to change.");
                    break;
            }
        }

        if (outcome.BackupPath is not null)
        {
            stderr.WriteLine($"warning: {settingsPath} contained comments or trailing commas, which are not preserved when the file is rewritten; the original is saved as {outcome.BackupPath}.");
        }

        if (json)
        {
            var report = new JsonObject
            {
                ["status"] = "Succeeded",
                ["schemaDirectory"] = destination,
                ["files"] = files.Count,
                ["written"] = written,
                ["source"] = checkout is null ? "embedded" : "checkout",
                ["checkout"] = checkout,
                ["settingsFile"] = settingsPath,
                ["settings"] = outcome.Status.ToString().ToLowerInvariant(),
                ["settingsBackup"] = outcome.BackupPath,
                ["url"] = url,
                ["fileMatch"] = new JsonArray(FileMatch.Select(m => (JsonNode?)JsonValue.Create(m)).ToArray()),
            };
            stdout.WriteLine(report.ToJsonString(FailureDocument.Options));
        }

        return ExitCodes.Ok;
    }

    // ---- locations ----

    /// <summary>%LOCALAPPDATA%\EArm\schemas\2026-10-01 (the platform's local application data directory elsewhere).</summary>
    internal static string DefaultSchemaDirectory()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(root))
        {
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }

        return Path.Combine(root, "EArm", "schemas", EmbeddedSchemas.SchemaVersion);
    }

    /// <summary>The VS Code user settings file of the platform.</summary>
    internal static string UserSettingsPath()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Code", "User", "settings.json");
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(home, "Library", "Application Support", "Code", "User", "settings.json");
        }

        string config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg ? xdg : Path.Combine(home, ".config");
        return Path.Combine(config, "Code", "User", "settings.json");
    }

    private static string ResolveSettingsPath(SchemaInstallRequest request, string workspace)
    {
        if (!string.IsNullOrWhiteSpace(request.SettingsFile))
        {
            return Path.GetFullPath(request.SettingsFile);
        }

        if (request.Global)
        {
            string path = request.UserSettingsFile ?? UserSettingsPath();
            if (!Directory.Exists(Path.GetDirectoryName(path)))
            {
                throw new InvalidOperationException($"The VS Code user settings directory '{Path.GetDirectoryName(path)}' does not exist (is VS Code installed?). Pass --settings-file <path> to choose the file.");
            }

            return path;
        }

        return Path.Combine(workspace, ".vscode", "settings.json");
    }

    /// <summary>The nearest ancestor of <paramref name="start"/> that holds <c>schemas/2026-10-01/deploymentTemplate.json</c>, as a directory path of that schema folder.</summary>
    private static string? FindCheckout(string start)
    {
        var directory = new DirectoryInfo(start);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "schemas", EmbeddedSchemas.SchemaVersion);
            if (File.Exists(Path.Combine(candidate, "deploymentTemplate.json")) && Directory.Exists(Path.Combine(candidate, "types")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static List<(string Relative, string Content)> EmbeddedFiles()
    {
        string prefix = "schemas/" + EmbeddedSchemas.SchemaVersion + "/";
        return EmbeddedSchemas.Names
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
            .Select(n => (n[prefix.Length..], EmbeddedSchemas.ReadText(n)))
            .ToList();
    }

    private static List<(string Relative, string Content)> CheckoutFiles(string schemaDirectory)
    {
        return Directory.EnumerateFiles(schemaDirectory, "*.json", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => (Path.GetRelativePath(schemaDirectory, p).Replace('\\', '/'), File.ReadAllText(p, Encoding.UTF8)))
            .ToList();
    }

    /// <summary><c>file:///C:/dir/</c> with a trailing slash.</summary>
    private static string DirectoryUri(string directory)
    {
        string uri = new Uri(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar).AbsoluteUri;
        return uri.EndsWith('/') ? uri : uri + "/";
    }

    /// <summary>Points every <c>$ref</c> at the published schema base to the local copy, leaving the <c>$id</c>s alone.</summary>
    internal static string RewriteRefs(string schemaText, string baseUri)
    {
        return RefValue.Replace(schemaText, m => m.Groups[1].Value + baseUri);
    }

    // ---- settings merge ----

    private enum SettingsStatus
    {
        Created,
        Updated,
        Unchanged,
    }

    private sealed record SettingsOutcome(SettingsStatus Status, string? BackupPath);

    private static SettingsOutcome MergeSettings(string path, string url)
    {
        JsonObject root;
        bool existed = File.Exists(path);
        bool lenientOnly = false;
        string? original = null;
        if (existed)
        {
            original = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(original))
            {
                root = [];
            }
            else
            {
                JsonNode? parsed;
                try
                {
                    parsed = JsonNode.Parse(original, documentOptions: TemplateJson.DocumentOptions);
                }
                catch (JsonException ex)
                {
                    throw new InvalidOperationException($"{path} is not valid JSON (comments and trailing commas are accepted): {ex.Message}");
                }

                root = parsed as JsonObject ?? throw new InvalidOperationException($"{path} does not contain a JSON object.");
                lenientOnly = !IsStrictJson(original);
            }
        }
        else
        {
            root = [];
        }

        var schemas = root[SchemasKey] as JsonArray;
        if (root[SchemasKey] is not null && schemas is null)
        {
            throw new InvalidOperationException($"'{SchemasKey}' in {path} is not an array.");
        }

        schemas ??= [];
        var desired = new JsonObject
        {
            ["fileMatch"] = new JsonArray(FileMatch.Select(m => (JsonNode?)JsonValue.Create(m)).ToArray()),
            ["url"] = url,
        };

        // Existing EARM entries: the same fileMatch pattern or a url that points at our template schema.
        var mine = schemas.Select((node, index) => (node, index)).Where(p => IsEArmEntry(p.node)).ToList();
        bool unchanged = mine.Count == 1 && JsonNode.DeepEquals(mine[0].node, desired);
        if (unchanged)
        {
            return new SettingsOutcome(SettingsStatus.Unchanged, null);
        }

        if (mine.Count == 0)
        {
            schemas.Add(desired);
        }
        else
        {
            int first = mine[0].index;
            foreach (var (node, _) in mine.Skip(1))
            {
                schemas.Remove(node);
            }

            schemas[first] = desired;
        }

        root[SchemasKey] = schemas;

        string? backup = null;
        if (existed && lenientOnly)
        {
            backup = path + ".earm.bak";
            File.Copy(path, backup, overwrite: true);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Serialize(root, original) + "\n", new UTF8Encoding(false));
        return new SettingsOutcome(existed ? SettingsStatus.Updated : SettingsStatus.Created, backup);
    }

    private static bool IsEArmEntry(JsonNode? node)
    {
        if (node is not JsonObject entry)
        {
            return false;
        }

        if (entry["fileMatch"] is JsonArray matches && matches.Any(m => m is JsonValue v && v.TryGetValue<string>(out string? text) && text.Equals(FileMatch[0], StringComparison.Ordinal)))
        {
            return true;
        }

        return entry["url"] is JsonValue url && url.TryGetValue<string>(out string? target)
            && target.Contains("deploymentTemplate.json", StringComparison.OrdinalIgnoreCase)
            && target.Contains("EArm", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStrictJson(string text)
    {
        try
        {
            using var _ = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Indented JSON using the indentation of the existing file (tabs, or its first indent width; default four spaces, as VS Code writes).</summary>
    private static string Serialize(JsonObject root, string? original)
    {
        char indentChar = ' ';
        int indentSize = 4;
        if (original is not null)
        {
            var match = Regex.Match(original, "\\r?\\n([ \\t]+)\\S", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (match.Success)
            {
                string indent = match.Groups[1].Value;
                indentChar = indent[0];
                indentSize = indentChar == '\t' ? 1 : indent.Length;
            }
        }

        return root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            IndentCharacter = indentChar,
            IndentSize = indentSize,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }
}
