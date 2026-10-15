using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EArm.Cli.Hosting;
using EArm.Engine.State;
using EArm.Graph;
using EArm.Graph.Auth;
using EArm.TestSupport;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>A delay provider that never waits, so retries in CLI tests run at full speed.</summary>
internal sealed class NoDelay : IDelayProvider
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

/// <summary>
/// One isolated environment per CLI test: a private folder under the test output directory with a state directory and templates written on
/// demand, plus the HTTP seam of the CLI: a <see cref="ScriptedGraphHandler"/> (the fake Microsoft Graph), a <see cref="FakeJwtTokenProvider"/> (the fake
/// sign-in) and a <see cref="ServiceOverrides"/> installed for the current async flow, so every command the test runs in-process talks to the fakes.
/// Nothing is passed through environment variables except <c>EARM_STATE_DIR</c>, which is restored on dispose.
/// </summary>
internal sealed class CliSandbox : IDisposable
{
    public const string Tenant = "11111111-1111-1111-1111-111111111111";
    public const string OtherTenant = "22222222-2222-2222-2222-222222222222";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string? _previousStateDirVariable;
    private readonly IDisposable _ambient;

    public CliSandbox(string testName)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string safe = new string(testName.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        Root = Path.Combine(AppContext.BaseDirectory, "cli-runs", (safe.Length > 40 ? safe[..40] : safe) + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Root);
        StateDir = Path.Combine(Root, "state");
        TemplatesDir = Path.Combine(Root, "templates");
        RecordPath = Path.Combine(Root, "auth", "record.json");
        Directory.CreateDirectory(TemplatesDir);

        _previousStateDirVariable = Environment.GetEnvironmentVariable(StateDirectory.EnvironmentVariable);
        Environment.SetEnvironmentVariable(StateDirectory.EnvironmentVariable, null);

        Handler = new ScriptedGraphHandler();
        Tokens = new FakeJwtTokenProvider();
        Overrides = new ServiceOverrides
        {
            Handler = Handler,
            Tokens = Tokens,
            Delay = new NoDelay(),
            RecordStore = new AuthRecordStore(RecordPath),
        };
        _ambient = ServiceOverrides.UseAmbient(Overrides);
    }

    public string Root { get; }

    public string StateDir { get; }

    public string TemplatesDir { get; }

    /// <summary>Where the sandbox's sign-in record would live.</summary>
    public string RecordPath { get; }

    /// <summary>The fake Microsoft Graph every online command of this sandbox talks to.</summary>
    public ScriptedGraphHandler Handler { get; }

    /// <summary>The fake sign-in: claims of the "signed-in" identity, or a failure.</summary>
    public FakeJwtTokenProvider Tokens { get; }

    public ServiceOverrides Overrides { get; }

    /// <summary>Connection settings for tests that build an invocation record directly instead of going through the command line.</summary>
    public ConnectionSettings Connection => new() { Overrides = Overrides };

    /// <summary>The global option every online invocation needs: the sandbox state directory.</summary>
    public string[] StateArgs => ["--state-dir", StateDir];

    public FileStateStore Store => new(StateDir);

    /// <summary>Every request the fake Graph received, across scripts, as <c>METHOD /path?query</c> (percent-decoded).</summary>
    public IReadOnlyList<string> RecordedCommands()
    {
        return Handler.Commands;
    }

    public IReadOnlyList<RecordedHttpRequest> RecordedRequests()
    {
        return Handler.Requests;
    }

    public void UseScript(string json)
    {
        Handler.SetScript(json);
    }

    /// <summary>
    /// A signed-in identity of <paramref name="tenant"/> whose <c>GET /organization</c> succeeds (the preflight's single read), followed by
    /// <paramref name="extraResponses"/> (comma-separated <see cref="ScriptedGraphHandler"/> rules). Replaces the previous script.
    /// </summary>
    public void UsePreflightOk(string tenant = Tenant, string extraResponses = "")
    {
        Tokens.Failure = null;
        Tokens.TenantId = tenant;
        UseScript("{ \"responses\": [" +
            "{ \"match\": { \"method\": \"GET\", \"urlContains\": \"/organization\" }, \"sticky\": true, \"status\": 200, \"body\": { \"value\": [ { \"id\": \"" + tenant + "\", \"displayName\": \"Contoso\", \"verifiedDomains\": [ { \"name\": \"contoso.com\", \"isDefault\": true } ] } ] } }" +
            (extraResponses.Length == 0 ? string.Empty : "," + extraResponses) +
            "] }");
    }

    /// <summary>No credential signs in: every online command fails its preflight with the sign-in guidance (exit 3) before any request is sent.</summary>
    public void UseNotLoggedIn()
    {
        Tokens.Failure = FakeJwtTokenProvider.NotSignedIn().Failure;
        UseScript("{ \"responses\": [] }");
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

    public void Dispose()
    {
        _ambient.Dispose();
        Environment.SetEnvironmentVariable(StateDirectory.EnvironmentVariable, _previousStateDirVariable);

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
