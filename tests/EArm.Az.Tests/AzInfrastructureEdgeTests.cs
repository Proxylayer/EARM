using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EArm.Az;
using EArm.Az.Tests.Infrastructure;
using Xunit;

namespace EArm.Az.Tests;

/// <summary>
/// Edge cases of the process and file plumbing under the Az transport: the pwsh child process (start failure,
/// timeout, cancellation, captured streams), the owner-only protocol files (create-new, zero-and-delete, hostile
/// states), and locator failures with a real executable that is not PowerShell.
/// </summary>
public sealed class AzInfrastructureEdgeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Scratch(string label)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "az-edge-runs", label + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Cleanup(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static async Task<System.Diagnostics.ProcessStartInfo> PwshCommandAsync(string command)
    {
        var pwsh = await SharedPwsh.Locator.LocateAsync(Ct);
        var info = PwshProcess.CreateStartInfo(pwsh.Path);
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-NoLogo");
        info.ArgumentList.Add("-Command");
        info.ArgumentList.Add(command);
        return info;
    }

    // ---- PwshProcess ----

    [Fact]
    public async Task Process_CapturesBothStreams_AndTheExitCode()
    {
        var info = await PwshCommandAsync("[Console]::Error.WriteLine('to-stderr'); Write-Output 'to-stdout'; exit 7");

        var outcome = await PwshProcess.RunAsync(info, TimeSpan.FromSeconds(60), Ct);

        Assert.Equal(7, outcome.ExitCode);
        Assert.False(outcome.TimedOut);
        Assert.Contains("to-stdout", outcome.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("to-stderr", outcome.StandardError, StringComparison.Ordinal);
        Assert.True(outcome.Elapsed > TimeSpan.Zero);
    }

    [Fact]
    public async Task Process_ThatOutlastsTheTimeout_IsKilled_AndReportedAsTimedOut()
    {
        var info = await PwshCommandAsync("Start-Sleep -Seconds 60");

        var outcome = await PwshProcess.RunAsync(info, TimeSpan.FromMilliseconds(1500), Ct);

        Assert.True(outcome.TimedOut);
        Assert.Equal(-1, outcome.ExitCode);
        Assert.True(outcome.Elapsed < TimeSpan.FromSeconds(45), "the tree was killed rather than waited for: " + outcome.Elapsed);
    }

    [Fact]
    public async Task Process_CancelledByTheCaller_IsKilled_AndThrowsOperationCanceled()
    {
        var info = await PwshCommandAsync("Start-Sleep -Seconds 60");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(1500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PwshProcess.RunAsync(info, TimeSpan.FromSeconds(120), cts.Token));
    }

    [Fact]
    public async Task Process_WithAnAlreadyCancelledToken_NeverStarts()
    {
        var info = await PwshCommandAsync("Write-Output 'never'");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PwshProcess.RunAsync(info, TimeSpan.FromSeconds(60), cts.Token));
    }

    [Fact]
    public async Task Process_ThatCannotStart_IsAToolchainException_AndNullStartInfoIsRejected()
    {
        string missing = Path.Combine(Path.GetTempPath(), "earm-missing-" + Guid.NewGuid().ToString("N"), OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh");
        var info = PwshProcess.CreateStartInfo(missing);

        var ex = await Assert.ThrowsAsync<ToolchainException>(() => PwshProcess.RunAsync(info, TimeSpan.FromSeconds(5), Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => PwshProcess.RunAsync(null!, TimeSpan.FromSeconds(5), Ct));

        Assert.Equal("pwsh", ex.Tool);
        Assert.Contains(missing, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateStartInfo_NeverUsesTheShell_AndTurnsTelemetryAndUpdateChecksOff()
    {
        var info = PwshProcess.CreateStartInfo("pwsh");

        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
        Assert.True(info.RedirectStandardOutput && info.RedirectStandardError && info.RedirectStandardInput);
        Assert.Equal("1", info.Environment["POWERSHELL_TELEMETRY_OPTOUT"]);
        Assert.Equal("Off", info.Environment["POWERSHELL_UPDATECHECK"]);
        Assert.Equal(new UTF8Encoding(false).WebName, info.StandardOutputEncoding!.WebName);
    }

    // ---- PwshLocator ----

    [Fact]
    public async Task Locator_AConfiguredExecutableThatIsNotPowerShell_IsRejectedWithItsOutput()
    {
        string? dotnet = PwshLocator.FindOnPath("dotnet");
        Assert.SkipWhen(dotnet is null, "dotnet is not on PATH.");
        var locator = new PwshLocator(new AzOptions { PwshPath = dotnet }, new TestLogger<PwshLocator>(new LogSink()));

        var ex = await Assert.ThrowsAsync<ToolchainException>(() => locator.LocateAsync(Ct));

        Assert.Equal("pwsh", ex.Tool);
        Assert.Contains("did not report a usable version", ex.Message, StringComparison.Ordinal);
        Assert.Contains("--pwsh-path", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Locator_ABareNameOnPath_IsResolvedToTheFullPath()
    {
        string? dotnet = PwshLocator.FindOnPath("dotnet");
        Assert.SkipWhen(dotnet is null, "dotnet is not on PATH.");
        var locator = new PwshLocator(new AzOptions { PwshPath = "dotnet" }, new TestLogger<PwshLocator>(new LogSink()));

        // The name resolves (the failure is the version probe, not "not found").
        var ex = await Assert.ThrowsAsync<ToolchainException>(() => locator.LocateAsync(Ct));

        Assert.DoesNotContain("was not found at the configured path", ex.Message, StringComparison.Ordinal);
        Assert.Contains(dotnet!, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Locator_ConcurrentCallers_ShareOneProbe()
    {
        var locator = new PwshLocator(new AzOptions(), new TestLogger<PwshLocator>(new LogSink()));

        var all = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => locator.LocateAsync(Ct)));

        Assert.All(all, info => Assert.Same(all[0], info));
    }

    [Fact]
    public void Locator_RejectsNullDependencies()
    {
        Assert.Throws<ArgumentNullException>(() => new PwshLocator(null!, new TestLogger<PwshLocator>(new LogSink())));
        Assert.Throws<ArgumentNullException>(() => new PwshLocator(new AzOptions(), null!));
    }

    // ---- SecureFile ----

    [Fact]
    public void SecureFile_WriteAllText_CreatesANewFileWithoutBom_AndRefusesToOverwrite()
    {
        string directory = Scratch(nameof(SecureFile_WriteAllText_CreatesANewFileWithoutBom_AndRefusesToOverwrite));
        try
        {
            string path = Path.Combine(directory, "body.json");

            SecureFile.WriteAllText(path, "{\"name\":\"ünï ✓\"}");

            byte[] bytes = File.ReadAllBytes(path);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
            Assert.Equal("{\"name\":\"ünï ✓\"}", SecureFile.TryReadAllText(path));
            Assert.Throws<IOException>(() => SecureFile.WriteAllText(path, "second"));
            Assert.Equal("{\"name\":\"ünï ✓\"}", SecureFile.TryReadAllText(path));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void SecureFile_TryReadAllText_ReturnsNullForAMissingFile_AndStripsABom()
    {
        string directory = Scratch(nameof(SecureFile_TryReadAllText_ReturnsNullForAMissingFile_AndStripsABom));
        try
        {
            Assert.Null(SecureFile.TryReadAllText(Path.Combine(directory, "missing.json")));
            string withBom = Path.Combine(directory, "bom.json");
            File.WriteAllBytes(withBom, [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("{}")]);

            Assert.Equal("{}", SecureFile.TryReadAllText(withBom));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void SecureFile_ZeroAndDelete_RemovesSmallEmptyAndMultiChunkFiles()
    {
        string directory = Scratch(nameof(SecureFile_ZeroAndDelete_RemovesSmallEmptyAndMultiChunkFiles));
        try
        {
            string small = Path.Combine(directory, "small.json");
            string empty = Path.Combine(directory, "empty.json");
            string large = Path.Combine(directory, "large.json");
            File.WriteAllText(small, "secret");
            File.WriteAllBytes(empty, []);
            File.WriteAllBytes(large, new byte[(64 * 1024 * 2) + 123]);

            SecureFile.ZeroAndDelete(small);
            SecureFile.ZeroAndDelete(empty);
            SecureFile.ZeroAndDelete(large);

            Assert.False(File.Exists(small));
            Assert.False(File.Exists(empty));
            Assert.False(File.Exists(large));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void SecureFile_ZeroAndDelete_NeverThrows_ForNullBlankMissingOrDirectoryPaths()
    {
        string directory = Scratch(nameof(SecureFile_ZeroAndDelete_NeverThrows_ForNullBlankMissingOrDirectoryPaths));
        try
        {
            SecureFile.ZeroAndDelete(null);
            SecureFile.ZeroAndDelete(string.Empty);
            SecureFile.ZeroAndDelete(Path.Combine(directory, "missing.json"));
            SecureFile.ZeroAndDelete(directory);

            Assert.True(Directory.Exists(directory));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void SecureFile_ZeroAndDelete_NeverThrows_WhenTheFileIsHeldOpenElsewhere()
    {
        string directory = Scratch(nameof(SecureFile_ZeroAndDelete_NeverThrows_WhenTheFileIsHeldOpenElsewhere));
        try
        {
            string path = Path.Combine(directory, "held.json");
            File.WriteAllText(path, "secret");

            using (var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                SecureFile.ZeroAndDelete(path);
                Assert.True(holder.CanRead);
            }

            // Windows: delete-pending until the holder closed; Unix: already unlinked. Either way the path is unusable for a reader afterwards.
            Assert.False(File.Exists(path));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void SecureFile_ZeroAndDelete_NeverThrows_ForAReadOnlyFile()
    {
        string directory = Scratch(nameof(SecureFile_ZeroAndDelete_NeverThrows_ForAReadOnlyFile));
        string path = Path.Combine(directory, "readonly.json");
        try
        {
            File.WriteAllText(path, "secret");
            File.SetAttributes(path, FileAttributes.ReadOnly);

            SecureFile.ZeroAndDelete(path);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }

            Cleanup(directory);
        }
    }
}
