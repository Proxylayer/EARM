using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EArm.Az;
using EArm.Az.Tests.Infrastructure;
using Xunit;

namespace EArm.Az.Tests;

/// <summary>README item 12: version parsing, the 7.4 floor, and locator failures.</summary>
public sealed class PwshLocatorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("7.6.6", true, "7.6.6")]
    [InlineData("7.5.0-preview.3", true, "7.5.0")]
    [InlineData("7.4.0\r\n", true, "7.4.0")]
    [InlineData("  7.6.0+build.1  \nextra", true, "7.6.0")]
    [InlineData("7.6", true, "7.6")]
    [InlineData("5.1.22621.4391", true, "5.1.22621.4391")]
    [InlineData("", false, "0.0")]
    [InlineData("   ", false, "0.0")]
    [InlineData("pwsh: not found", false, "0.0")]
    [InlineData("seven", false, "0.0")]
    public void TryParseVersion_ReadsTheFirstLineUpToAPrereleaseOrBuildSuffix(string text, bool expectedResult, string expectedVersion)
    {
        var parsed = PwshLocator.TryParseVersion(text, out var version);

        Assert.Equal(expectedResult, parsed);
        Assert.Equal(new Version(expectedVersion), version);
    }

    [Theory]
    [InlineData("7.4.0", true)]
    [InlineData("7.4", true)]
    [InlineData("7.6.6", true)]
    [InlineData("8.0.0-preview.1", true)]
    [InlineData("7.3.12", false)]
    [InlineData("7.2.0", false)]
    [InlineData("5.1.22621.4391", false)]
    public void MinimumVersionIsSevenPointFour(string text, bool acceptable)
    {
        Assert.Equal(new Version(7, 4), PwshLocator.MinimumVersion);
        Assert.True(PwshLocator.TryParseVersion(text, out var version));
        Assert.Equal(acceptable, version >= PwshLocator.MinimumVersion);
    }

    [Fact]
    public async Task LocatesARealPwshOfAtLeastSevenPointFour()
    {
        var pwsh = await SharedPwsh.Locator.LocateAsync(Ct);

        Assert.True(File.Exists(pwsh.Path), pwsh.Path);
        Assert.Equal(OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh", Path.GetFileName(pwsh.Path));
        Assert.True(pwsh.Version >= PwshLocator.MinimumVersion, pwsh.Version.ToString());
        Assert.Same(pwsh, await SharedPwsh.Locator.LocateAsync(Ct));
    }

    [Fact]
    public async Task BogusPwshPathThrowsToolchainException()
    {
        var bogus = Path.Combine(Path.GetTempPath(), "earm-no-such-dir-" + Guid.NewGuid().ToString("N"), OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh");
        var sink = new LogSink();
        var locator = new PwshLocator(new AzOptions { PwshPath = bogus }, new TestLogger<PwshLocator>(sink));

        var exception = await Assert.ThrowsAsync<ToolchainException>(() => locator.LocateAsync(Ct));

        Assert.Equal("pwsh", exception.Tool);
        Assert.Contains(bogus, exception.Message);
        Assert.Contains("--pwsh-path", exception.Message);
        Assert.Contains("https://aka.ms/powershell", exception.Message);
        Assert.Empty(sink.Entries);
    }

    [Fact]
    public async Task BogusPwshNameNotOnPathThrowsToolchainException()
    {
        var locator = new PwshLocator(new AzOptions { PwshPath = "earm-no-such-pwsh" }, new TestLogger<PwshLocator>(new LogSink()));

        var exception = await Assert.ThrowsAsync<ToolchainException>(() => locator.LocateAsync(Ct));

        Assert.Equal("pwsh", exception.Tool);
        Assert.Contains("earm-no-such-pwsh", exception.Message);
    }

    [Fact]
    public async Task WindowsPowerShellFiveIsRejectedAsTooOld()
    {
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        var legacy = string.IsNullOrEmpty(systemRoot) ? null : Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        Assert.SkipUnless(OperatingSystem.IsWindows() && legacy is not null && File.Exists(legacy), "Windows PowerShell 5.1 is not available on this machine.");

        var locator = new PwshLocator(new AzOptions { PwshPath = legacy }, new TestLogger<PwshLocator>(new LogSink()));

        var exception = await Assert.ThrowsAsync<ToolchainException>(() => locator.LocateAsync(Ct));

        Assert.Equal("pwsh", exception.Tool);
        Assert.Contains("too old", exception.Message);
        Assert.Contains("7.4", exception.Message);
    }

    [Fact]
    public void FindOnPath_OnlyAcceptsExecutables()
    {
        var found = PwshLocator.FindOnPath("pwsh");
        Assert.NotNull(found);
        Assert.Equal(OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh", Path.GetFileName(found));
        Assert.Null(PwshLocator.FindOnPath("earm-no-such-pwsh"));
    }

    [Fact]
    public void AzOptions_ExplicitSettingsWinAndDefaultsAreSane()
    {
        var options = new AzOptions();
        Assert.Equal(TimeSpan.FromSeconds(120), options.Timeout);
        Assert.Equal(4, options.MaxParallel);
        Assert.Empty(options.AdditionalEnvironment);
        Assert.EndsWith(Path.Combine("EArm", "tmp"), AzOptions.DefaultTempDirectory(), StringComparison.Ordinal);
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(AzOptions.TempDirectoryVariable)))
        {
            Assert.Equal(AzOptions.DefaultTempDirectory(), options.ResolveTempDirectory());
        }

        var explicitOptions = new AzOptions { PwshPath = "C:\\p\\pwsh.exe", AzCommand = "C:\\x\\az.ps1", TempDirectory = "C:\\t" };
        Assert.Equal("C:\\p\\pwsh.exe", explicitOptions.ResolvePwshPathSetting());
        Assert.Equal("C:\\x\\az.ps1", explicitOptions.ResolveAzCommandSetting());
        Assert.Equal("C:\\t", explicitOptions.ResolveTempDirectory());

        var blank = new AzOptions { PwshPath = " ", AzCommand = "", TempDirectory = "\t" };
        Assert.Equal(new AzOptions().ResolvePwshPathSetting(), blank.ResolvePwshPathSetting());
        Assert.Equal(new AzOptions().ResolveAzCommandSetting(), blank.ResolveAzCommandSetting());
        Assert.Equal(new AzOptions().ResolveTempDirectory(), blank.ResolveTempDirectory());
    }
}
