using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using EArm.Az;
using Microsoft.Extensions.Logging;

namespace EArm.Az.Tests.Infrastructure;

/// <summary>
/// One isolated environment per test: a private folder under the test output directory holding the fake's steering
/// files (argv recording, consumed-response state, fixture, hard-link capture) and the runner's temp directory, plus an
/// <see cref="AzOptions"/> that points <c>AzCommand</c> at <c>fake-az.ps1</c> and steers the fake through
/// <see cref="AzOptions.AdditionalEnvironment"/>.
/// </summary>
internal sealed class FakeAzContext : IDisposable
{
    public const string ArgvFileVariable = "EARM_FAKE_AZ_ARGV_FILE";
    public const string FixtureVariable = "EARM_FAKE_AZ_FIXTURE";
    public const string StateFileVariable = "EARM_FAKE_AZ_STATE_FILE";
    public const string LinkDirectoryVariable = "EARM_FAKE_AZ_LINK_DIR";

    public static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static string FakeAzPath { get; } = Path.Combine(AppContext.BaseDirectory, "fake-az", "fake-az.ps1");

    public static string FixturesRoot { get; } = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public FakeAzContext(string testName)
    {
        if (!File.Exists(FakeAzPath))
        {
            throw new FileNotFoundException("fake-az.ps1 was not copied next to the test binaries; check EArm.Az.Tests.csproj.", FakeAzPath);
        }

        Root = Path.Combine(AppContext.BaseDirectory, "fake-az-runs", Sanitize(testName) + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Root);

        TempDirectory = Path.Combine(Root, "tmp");
        LinkDirectory = Path.Combine(Root, "links");
        ArgvFile = Path.Combine(Root, "argv.jsonl");
        StateFile = Path.Combine(Root, "argv.state");
        FixtureFile = Path.Combine(Root, "fixture.json");

        Options = new AzOptions
        {
            AzCommand = FakeAzPath,
            TempDirectory = TempDirectory,
        };
        Options.AdditionalEnvironment[ArgvFileVariable] = ArgvFile;
        Options.AdditionalEnvironment[StateFileVariable] = StateFile;
    }

    public string Root { get; }

    /// <summary>The runner's temp directory (<see cref="AzOptions.TempDirectory"/>).</summary>
    public string TempDirectory { get; }

    /// <summary>Where the fake hard-links the protocol files when <see cref="EnableLinkCapture"/> was called.</summary>
    public string LinkDirectory { get; }

    public string ArgvFile { get; }

    public string StateFile { get; }

    public string FixtureFile { get; }

    public AzOptions Options { get; }

    /// <summary>Every log line written by components created through <see cref="Logger{T}"/>.</summary>
    public LogSink Log { get; } = new();

    public TestLogger<T> Logger<T>()
    {
        return new TestLogger<T>(Log);
    }

    /// <summary>Writes the scenario as the fixture file and points the fake at it.</summary>
    public void UseScenario(FakeScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        File.WriteAllText(FixtureFile, scenario.ToJson(), Utf8NoBom);
        Options.AdditionalEnvironment[FixtureVariable] = FixtureFile;
        ResetFakeState();
    }

    /// <summary>Points the fake at a static fixture under <c>Fixtures/</c>.</summary>
    public void UseFixtureFile(string fileName)
    {
        var path = Path.Combine(FixturesRoot, fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Fixture not found in the test output directory.", path);
        }

        Options.AdditionalEnvironment[FixtureVariable] = path;
        ResetFakeState();
    }

    /// <summary>Asks the fake to hard-link request/body/output files into <see cref="LinkDirectory"/>.</summary>
    public void EnableLinkCapture()
    {
        Options.AdditionalEnvironment[LinkDirectoryVariable] = LinkDirectory;
    }

    /// <summary>Forgets which fixture responses were consumed (the fake tracks them in the state file).</summary>
    public void ResetFakeState()
    {
        if (File.Exists(StateFile))
        {
            File.Delete(StateFile);
        }
    }

    public IReadOnlyList<FakeAzRecord> ReadRecords()
    {
        return FakeAzRecord.ReadAll(ArgvFile);
    }

    /// <summary>
    /// The fake's concurrency gauge (<c>&lt;argv file&gt;.gauge</c>): how many fakes were inside their replay window at
    /// once (<see cref="FakeAzGauge.MaxConcurrent"/>) and how many arrived in total. Zeroes when no fake wrote one.
    /// </summary>
    public FakeAzGauge ReadGauge()
    {
        var text = FakeAzFiles.ReadSettledText(ArgvFile + ".gauge");
        var parts = (text ?? string.Empty).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return new FakeAzGauge(0, 0, 0);
        }

        var culture = System.Globalization.CultureInfo.InvariantCulture;
        return new FakeAzGauge(int.Parse(parts[0], culture), int.Parse(parts[1], culture), int.Parse(parts[2], culture));
    }

    /// <summary>Files left in the temp directory other than the extracted host script.</summary>
    public IReadOnlyList<string> LeftoverProtocolFiles()
    {
        if (!Directory.Exists(TempDirectory))
        {
            return [];
        }

        return Directory.GetFiles(TempDirectory)
            .Where(static f => !Path.GetFileName(f).StartsWith("Invoke-AzRest-", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Where(static f => f is not null)
            .Select(static f => f!)
            .ToList();
    }

    public PwshAzRunner CreateRunner()
    {
        return new PwshAzRunner(Options, SharedPwsh.Locator, Logger<PwshAzRunner>());
    }

    public RetryPolicy CreateRetryPolicy(IDelayProvider delayProvider, RetryOptions? retryOptions = null)
    {
        return new RetryPolicy(retryOptions ?? new RetryOptions(), delayProvider, Logger<RetryPolicy>());
    }

    public AzGraphClient CreateGraphClient(IAzRunner runner, IDelayProvider delayProvider, RetryOptions? retryOptions = null)
    {
        return new AzGraphClient(runner, CreateRetryPolicy(delayProvider, retryOptions), Logger<AzGraphClient>());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A killed pwsh may still hold a handle for a moment; the folder lives under bin/ and is harmless.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        var text = builder.ToString();
        return text.Length <= 48 ? text : text[..48];
    }
}
