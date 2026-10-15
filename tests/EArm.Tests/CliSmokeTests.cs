using System.CommandLine;
using System.IO;
using EArm.Cli;
using Xunit;

namespace EArm.Tests;

public sealed class CliSmokeTests
{
    [Fact]
    public void VersionOption_PrintsNonEmptyInformationalVersion()
    {
        using var output = new StringWriter();
        var configuration = new InvocationConfiguration { Output = output };

        int exitCode = Program.BuildRootCommand().Parse(["--version"]).Invoke(configuration);

        string printed = output.ToString().Trim();
        Assert.Equal(0, exitCode);
        Assert.False(string.IsNullOrWhiteSpace(printed), "earm --version printed nothing.");
        Assert.Equal(VersionInfo.InformationalVersion, printed);
    }
}
