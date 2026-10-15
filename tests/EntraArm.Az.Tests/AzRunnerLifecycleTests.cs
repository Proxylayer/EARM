using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using EntraArm.Az;
using EntraArm.Az.Tests.Infrastructure;
using Xunit;

namespace EntraArm.Az.Tests;

/// <summary>
/// README items 4, 5, 10 and the exit-127 part of 6: protocol file lifecycle (zeroed and deleted after success, timeout
/// and cancellation), the owner-only temp directory, the timeout kill, and the guarantee that only pwsh is launched.
/// </summary>
public sealed class AzRunnerLifecycleTests
{
    private const string V1 = "https://graph.microsoft.com/v1.0";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ProtocolFilesAreZeroedAndDeletedAfterACall()
    {
        using var context = new FakeAzContext(nameof(ProtocolFilesAreZeroedAndDeletedAfterACall));
        context.EnableLinkCapture();
        context.UseScenario(new FakeScenario().Add(FakeResponse.Json("{\"id\":\"app-1\",\"secretText\":\"zero-me-out-please\"}")));
        using var runner = context.CreateRunner();
        var request = AzRequest.Rest(
            "POST",
            V1 + "/applications",
            null,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Authorization"] = "Bearer zero-me-too" },
            new JsonObject { ["displayName"] = "zero me", ["passwordProfile"] = new JsonObject { ["password"] = "Hunter2!" } });

        var result = await runner.RunAsync(request, Ct);

        Assert.True(result.Succeeded, "stderr: " + result.StandardError);
        Assert.Contains("zero-me-out-please", result.StandardOutput);
        Assert.Empty(context.LeftoverProtocolFiles());

        // The fake hard-linked req-/body-/out-<id>.json; the links outlive the deletion and show the zeroed content.
        var links = Directory.GetFiles(context.LinkDirectory);
        Assert.Equal(3, links.Length);
        foreach (var prefix in new[] { "req-", "body-", "out-" })
        {
            var link = Assert.Single(links, l => Path.GetFileName(l).StartsWith(prefix, StringComparison.Ordinal));
            var bytes = File.ReadAllBytes(link);
            Assert.NotEmpty(bytes);
            Assert.All(bytes, b => Assert.Equal((byte)0, b));
        }
    }

    [Fact]
    public async Task TimeoutKillsThePwshTreeAndLeavesNoFiles()
    {
        using var context = new FakeAzContext(nameof(TimeoutKillsThePwshTreeAndLeavesNoFiles));
        context.UseScenario(new FakeScenario().Add(FakeResponse.Json("{\"id\":\"late\"}").Sleeping(20)));
        using var runner = context.CreateRunner();
        var request = AzRequest.Rest("POST", V1 + "/applications", body: new JsonObject { ["displayName"] = "slow" }) with { Timeout = TimeSpan.FromSeconds(2) };

        var result = await runner.RunAsync(request, Ct);

        Assert.True(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
        Assert.False(result.Succeeded);
        Assert.InRange(result.Elapsed, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(6));
        Assert.Empty(context.LeftoverProtocolFiles());
        Assert.Single(context.ReadRecords());

        var failure = AzCliException.FromResult(result, "POST /applications");
        Assert.Equal(AzCliFailure.Transient, failure.Failure);
        Assert.True(failure.TimedOut);
        Assert.True(failure.IsRetryable);
        Assert.Contains("timeout", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(context.Log.Entries, e => e.EventId.Id == 1004);
    }

    [Fact]
    public async Task CancellationKillsThePwshTreeAndLeavesNoFiles()
    {
        using var context = new FakeAzContext(nameof(CancellationKillsThePwshTreeAndLeavesNoFiles));
        context.UseScenario(new FakeScenario().Add(FakeResponse.Json("{\"id\":\"late\"}").Sleeping(20)));
        using var runner = context.CreateRunner();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(700));
        var request = AzRequest.Rest("POST", V1 + "/applications", body: new JsonObject { ["displayName"] = "slow" });

        var started = DateTime.UtcNow;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(request, cancellation.Token));

        Assert.InRange(DateTime.UtcNow - started, TimeSpan.Zero, TimeSpan.FromSeconds(8));
        Assert.Empty(context.LeftoverProtocolFiles());
    }

    [Fact]
    public async Task TempDirectoryIsOwnerOnly()
    {
        using var context = new FakeAzContext(nameof(TempDirectoryIsOwnerOnly));
        using var runner = context.CreateRunner();

        var result = await runner.RunAsync(AzRequest.ForCommand("version"), Ct);

        Assert.True(result.Succeeded, "stderr: " + result.StandardError);
        Assert.Equal(Path.GetFullPath(context.TempDirectory), runner.TempDirectory);
        Assert.True(Directory.Exists(runner.TempDirectory));

        if (OperatingSystem.IsWindows())
        {
            var security = new DirectoryInfo(runner.TempDirectory).GetAccessControl(AccessControlSections.Access);
            Assert.True(security.AreAccessRulesProtected, "the temp directory DACL inherits from its parent");

            using var identity = WindowsIdentity.GetCurrent();
            var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
            var rule = Assert.Single(rules);
            Assert.Equal(identity.User, rule.IdentityReference);
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            Assert.True(rule.FileSystemRights.HasFlag(FileSystemRights.FullControl));
            Assert.True(rule.InheritanceFlags.HasFlag(InheritanceFlags.ObjectInherit));
        }
        else
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(runner.TempDirectory));
        }
    }

    [Fact]
    public async Task OnlyPwshIsEverLaunched()
    {
        var pwsh = await SharedPwsh.Locator.LocateAsync(Ct);
        Assert.Contains(Path.GetFileName(pwsh.Path), new[] { "pwsh.exe", "pwsh" }, StringComparer.OrdinalIgnoreCase);
        Assert.True(pwsh.Version >= PwshLocator.MinimumVersion, $"pwsh {pwsh.Version} is older than {PwshLocator.MinimumVersion}");

        using var context = new FakeAzContext(nameof(OnlyPwshIsEverLaunched));
        using var runner = context.CreateRunner();
        var scriptPath = Path.Combine(context.TempDirectory, "Invoke-AzRest-0123456789abcdef.ps1");
        var requestFile = Path.Combine(context.TempDirectory, "req-1.json");

        var startInfo = runner.BuildStartInfo(pwsh.Path, scriptPath, requestFile, FakeAzContext.FakeAzPath);

        Assert.Equal(pwsh.Path, startInfo.FileName);
        Assert.EndsWith(OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh", startInfo.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.True(startInfo.RedirectStandardInput);
        Assert.Equal(string.Empty, startInfo.Arguments);
        Assert.Equal(
            ["-NoProfile", "-NonInteractive", "-NoLogo", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "-RequestFile", requestFile, "-AzCommand", FakeAzContext.FakeAzPath],
            startInfo.ArgumentList);
        Assert.All(startInfo.ArgumentList, a => Assert.DoesNotContain("cmd.exe", a, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("json", startInfo.Environment["AZURE_CORE_OUTPUT"]);
        Assert.Equal("1", startInfo.Environment["AZURE_CORE_NO_COLOR"]);
        Assert.Equal("1", startInfo.Environment["POWERSHELL_TELEMETRY_OPTOUT"]);
        Assert.Equal("Off", startInfo.Environment["POWERSHELL_UPDATECHECK"]);
        Assert.Equal(context.ArgvFile, startInfo.Environment[FakeAzContext.ArgvFileVariable]);

        // The host script itself never shells out either.
        var script = EmbeddedScript.Text;
        Assert.DoesNotContain("cmd.exe", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(new Regex(@"\bcmd(\.exe)?\s+/[ck]\b", RegexOptions.IgnoreCase), script);
        Assert.DoesNotContain("Invoke-Expression", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("& $az @azArgs", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AzCommandThatCannotBeResolvedIsNotInstalledExit127()
    {
        using var context = new FakeAzContext(nameof(AzCommandThatCannotBeResolvedIsNotInstalledExit127));
        context.Options.AzCommand = "entraarm-no-such-az-command";
        using var runner = context.CreateRunner();

        var result = await runner.RunAsync(AzRequest.ForCommand("version"), Ct);

        Assert.False(result.Succeeded);
        Assert.Equal(127, result.ExitCode);
        Assert.Contains("entraarm-no-such-az-command", result.StandardError);
        Assert.Equal(AzCliFailure.NotInstalled, AzCliException.Classify(result));
        var failure = AzCliException.FromResult(result, "az version");
        Assert.Equal(AzCliFailure.NotInstalled, failure.Failure);
        Assert.False(failure.IsRetryable);
        Assert.Contains("--az-command", failure.Message);
        Assert.Empty(context.ReadRecords());
    }

    [Fact]
    public async Task ExhaustedFixtureFailsLoudlyInsteadOfHanging()
    {
        using var context = new FakeAzContext(nameof(ExhaustedFixtureFailsLoudlyInsteadOfHanging));
        context.UseScenario(new FakeScenario().Add(FakeResponse.Empty()));
        using var runner = context.CreateRunner();

        var first = await runner.RunAsync(AzRequest.ForCommand("version"), Ct);
        var second = await runner.RunAsync(AzRequest.ForCommand("version"), Ct);

        Assert.True(first.Succeeded, "stderr: " + first.StandardError);
        Assert.Equal(99, second.ExitCode);
        Assert.Contains("fake-az: no fixture response left for version", second.StandardError);
        Assert.Equal(2, context.ReadRecords().Count);
    }

    [Fact]
    public async Task RunnerHonoursMaxParallelAndServesConcurrentCalls()
    {
        using var context = new FakeAzContext(nameof(RunnerHonoursMaxParallelAndServesConcurrentCalls));
        context.Options.MaxParallel = 2;
        using var runner = context.CreateRunner();

        // Deterministic concurrency proof instead of timing: the first two fake processes rendezvous at a cross-process
        // barrier (exit 97 if the runner never gets two of them running at once), the fake's gauge records how many were
        // inside at the same time, and the runner's semaphore caps that at MaxParallel.
        var scenario = new FakeScenario();
        for (var i = 0; i < 4; i++)
        {
            scenario.Add(FakeResponse.Json("{\"id\":\"u" + i + "\"}").WithBarrier(parties: 2));
        }

        context.UseScenario(scenario);

        var tasks = Enumerable.Range(0, 4)
            .Select(i => runner.RunAsync(AzRequest.Rest("GET", V1 + "/users/u" + i), Ct))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.True(r.Succeeded, "stderr: " + r.StandardError));
        var gauge = context.ReadGauge();
        Assert.Equal(4, gauge.Arrivals);
        Assert.Equal(0, gauge.Current);
        Assert.Equal(2, gauge.MaxConcurrent);
        var records = context.ReadRecords();
        Assert.Equal(4, records.Count);
        Assert.Equal(
            Enumerable.Range(0, 4).Select(i => V1 + "/users/u" + i).OrderBy(static u => u, StringComparer.Ordinal),
            records.Select(static r => r.ValueAfter("--url")).OrderBy(static u => u, StringComparer.Ordinal));
        Assert.Empty(context.LeftoverProtocolFiles());
    }
}
