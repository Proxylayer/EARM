using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EArm.Engine.State;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// One isolated environment per CLI test: a private folder under the test output directory with a state directory,
/// the runner's temp directory, the fake az steering files, and templates written on demand. The fake az is steered
/// through process-wide environment variables (the CLI has no other hook), which is why every test that uses a
/// sandbox lives in the sequential <c>Cli</c> collection; the variables are restored on dispose.
/// </summary>
internal sealed class CliSandbox : IDisposable
{
    public const string ArgvFileVariable = "EARM_FAKE_AZ_ARGV_FILE";
    public const string FixtureVariable = "EARM_FAKE_AZ_FIXTURE";
    public const string StateFileVariable = "EARM_FAKE_AZ_STATE_FILE";
    public const string Tenant = "11111111-1111-1111-1111-111111111111";
    public const string OtherTenant = "22222222-2222-2222-2222-222222222222";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly string[] ManagedVariables = [ArgvFileVariable, FixtureVariable, StateFileVariable, StateDirectory.EnvironmentVariable];

    private readonly Dictionary<string, string?> _previous = new(StringComparer.OrdinalIgnoreCase);

    public static string FakeAzPath { get; } = Path.Combine(AppContext.BaseDirectory, "fake-az", "fake-az.ps1");

    public CliSandbox(string testName)
    {
        if (!File.Exists(FakeAzPath))
        {
            throw new FileNotFoundException("fake-az.ps1 was not copied next to the test binaries; check EArm.Tests.csproj.", FakeAzPath);
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        string safe = new string(testName.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        Root = Path.Combine(AppContext.BaseDirectory, "cli-runs", (safe.Length > 40 ? safe[..40] : safe) + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Root);
        StateDir = Path.Combine(Root, "state");
        TempDir = Path.Combine(Root, "tmp");
        TemplatesDir = Path.Combine(Root, "templates");
        Directory.CreateDirectory(TemplatesDir);
        ArgvFile = Path.Combine(Root, "argv.jsonl");
        FakeStateFile = Path.Combine(Root, "argv.state");
        FixtureFile = Path.Combine(Root, "fixture.json");

        foreach (var name in ManagedVariables)
        {
            _previous[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }

        Environment.SetEnvironmentVariable(ArgvFileVariable, ArgvFile);
        Environment.SetEnvironmentVariable(StateFileVariable, FakeStateFile);
    }

    public string Root { get; }

    public string StateDir { get; }

    public string TempDir { get; }

    public string TemplatesDir { get; }

    public string ArgvFile { get; }

    public string FakeStateFile { get; }

    public string FixtureFile { get; }

    /// <summary>The global options every online invocation needs: the fake az, a private temp directory and the sandbox state directory.</summary>
    public string[] AzArgs => ["--az-command", FakeAzPath, "--temp-dir", TempDir, "--state-dir", StateDir];

    /// <summary>The global options without <c>--state-dir</c> (for directory-resolution tests).</summary>
    public string[] AzArgsWithoutStateDir => ["--az-command", FakeAzPath, "--temp-dir", TempDir];

    public FileStateStore Store => new(StateDir);

    public void UseFixture(string json)
    {
        File.WriteAllText(FixtureFile, json, Utf8NoBom);
        Environment.SetEnvironmentVariable(FixtureVariable, FixtureFile);
        if (File.Exists(FakeStateFile))
        {
            File.Delete(FakeStateFile);
        }
    }

    public void UsePreflightOk(string tenant = Tenant, string extraResponses = "")
    {
        UseFixture("{ \"responses\": [" +
            "{ \"match\": { \"argvContains\": \"version --only-show-errors\" }, \"exitCode\": 0, \"stdoutJson\": { \"azure-cli\": \"2.70.0\", \"extensions\": {} } }," +
            "{ \"match\": { \"argvContains\": \"account show\" }, \"exitCode\": 0, \"stdoutJson\": { \"environmentName\": \"AzureCloud\", \"id\": \"33333333-3333-3333-3333-333333333333\", \"name\": \"Sub\", \"tenantId\": \"" + tenant + "\", \"user\": { \"name\": \"alice@contoso.example\", \"type\": \"user\" } } }," +
            // Preflight reads /organization?$select=id; DeploymentEnvironment.ReadTenantAsync (tenant()) reads it again with displayName/verifiedDomains.
            "{ \"match\": { \"method\": \"GET\", \"urlContains\": \"/organization\" }, \"exitCode\": 0, \"stdoutJson\": { \"value\": [ { \"id\": \"" + tenant + "\", \"displayName\": \"Contoso\", \"verifiedDomains\": [ { \"name\": \"contoso.com\", \"isDefault\": true } ] } ] } }," +
            "{ \"match\": { \"method\": \"GET\", \"urlContains\": \"/organization\" }, \"exitCode\": 0, \"stdoutJson\": { \"value\": [ { \"id\": \"" + tenant + "\", \"displayName\": \"Contoso\", \"verifiedDomains\": [ { \"name\": \"contoso.com\", \"isDefault\": true } ] } ] } }" +
            (extraResponses.Length == 0 ? string.Empty : "," + extraResponses) +
            "] }");
    }

    public void UseNotLoggedIn()
    {
        UseFixture("{ \"responses\": [" +
            "{ \"match\": { \"argvContains\": \"version --only-show-errors\" }, \"exitCode\": 0, \"stdoutJson\": { \"azure-cli\": \"2.70.0\" } }," +
            "{ \"match\": { \"argvContains\": \"account show\" }, \"exitCode\": 1, \"stderr\": \"ERROR: Please run 'az login' to setup account.\" }" +
            "] }");
    }

    public void SetStateDirVariable(string? value)
    {
        Environment.SetEnvironmentVariable(StateDirectory.EnvironmentVariable, value);
    }

    /// <summary>Writes a template file into the sandbox and returns its path.</summary>
    public string WriteTemplate(string fileName, string body)
    {
        string path = Path.Combine(TemplatesDir, fileName);
        File.WriteAllText(path, "{ \"$schema\": \"https://schema.earm.proxylayer.net/schemas/2026-10-01/deploymentTemplate.json#\", \"contentVersion\": \"1.0.0.0\", " + body + " }", Utf8NoBom);
        return path;
    }

    /// <summary>A template that declares no resources (deployable without any provider).</summary>
    public string WriteEmptyTemplate(string fileName = "empty.entra.json", string outputs = "\"greeting\": { \"type\": \"string\", \"value\": \"hello\" }")
    {
        return WriteTemplate(fileName, "\"resources\": [], \"outputs\": { " + outputs + " }");
    }

    public async Task<StateFile> WriteStateAsync(StateFile state)
    {
        await Store.SaveAsync(state, TestContext.Current.CancellationToken);
        return state;
    }

    public void WriteRawState(string deploymentName, string json)
    {
        Directory.CreateDirectory(StateDir);
        File.WriteAllText(Path.Combine(StateDir, deploymentName + ".json"), json, Utf8NoBom);
    }

    public void WriteLock(string deploymentName, DateTimeOffset acquiredOn, int processId = 424242)
    {
        Directory.CreateDirectory(StateDir);
        File.WriteAllText(Path.Combine(StateDir, deploymentName + ".lock"), "{ \"pid\": " + processId + ", \"host\": \"other-host\", \"user\": \"someone\", \"acquiredOn\": \"" + acquiredOn.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + "\" }", Utf8NoBom);
    }

    public string StatePath(string deploymentName)
    {
        return Path.Combine(StateDir, deploymentName + ".json");
    }

    public string LockPath(string deploymentName)
    {
        return Path.Combine(StateDir, deploymentName + ".lock");
    }

    /// <summary>The argv of every az invocation the fake recorded (one line per call).</summary>
    public IReadOnlyList<string> RecordedCommands()
    {
        return ReadArgvLines()
            .Select(l => string.Join(' ', System.Text.Json.Nodes.JsonNode.Parse(l)!["argv"]!.AsArray().Select(a => a!.GetValue<string>())))
            .ToList();
    }

    /// <summary>
    /// The non-empty lines of the fake's argv file. Opened with FileShare.ReadWrite|Delete and re-read (50 x 20 ms) while the
    /// last line is still unterminated, so a reader never trips over a fake that is mid-append.
    /// </summary>
    public IReadOnlyList<string> ReadArgvLines()
    {
        for (var attempt = 1; ; attempt++)
        {
            string text;
            try
            {
                using var stream = new FileStream(ArgvFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Utf8NoBom);
                text = reader.ReadToEnd();
            }
            catch (FileNotFoundException)
            {
                return [];
            }
            catch (DirectoryNotFoundException)
            {
                return [];
            }
            catch (IOException) when (attempt < 50)
            {
                System.Threading.Thread.Sleep(20);
                continue;
            }

            if (text.Length > 0 && text[^1] != '\n' && attempt < 50)
            {
                System.Threading.Thread.Sleep(20);
                continue;
            }

            return text.Split('\n').Select(static l => l.TrimEnd('\r')).Where(static l => l.Length > 0).ToList();
        }
    }

    public void Dispose()
    {
        foreach (var pair in _previous)
        {
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }

        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
