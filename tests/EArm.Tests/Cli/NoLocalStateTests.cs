using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EArm.TestSupport;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// EARM keeps no local state: identity is resolved from the live tenant by key on every run. <c>what-if</c>, <c>create</c> and <c>delete</c> of a
/// real template, run with the working directory and the template in a private folder, leave nothing on disk but the secure outputs file that
/// <c>create</c> was asked to write: no <c>.earm/state</c> next to the template or under the working directory, no lock, no state file anywhere.
/// </summary>
[Collection(CliCollection.Name)]
public sealed class NoLocalStateTests
{
    private const string Sample = "app-with-sp-and-secret";

    [Fact]
    public async Task WhatIfCreateAndDelete_WriteNoFile_ButTheRequestedSecureOutputsFile()
    {
        using var sandbox = new CliSandbox(nameof(WhatIfCreateAndDelete_WriteNoFile_ButTheRequestedSecureOutputsFile));
        var directory = new FakeDirectory();
        sandbox.Handler.Directory = directory;
        string template = Path.Combine(sandbox.TemplatesDir, Sample + ".entra.json");
        string parameters = Path.Combine(sandbox.TemplatesDir, Sample + ".parameters.json");
        File.Copy(TestPaths.Sample(Sample + ".entra.json"), template);
        File.Copy(TestPaths.Sample(Sample + ".parameters.json"), parameters);
        string secrets = Path.Combine(sandbox.Root, "secure-outputs.json");
        string[] samplesBefore = Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(TestPaths.Sample(Sample + ".entra.json"))!, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        string previous = Environment.CurrentDirectory;

        CliResult whatIf;
        CliResult create;
        CliResult delete;
        int afterCreate;
        try
        {
            Environment.CurrentDirectory = sandbox.Root;
            whatIf = await CliRunner.RunAsync("deployment", "what-if", "-f", template, "-p", "@" + parameters, "-o", "json");
            create = await CliRunner.RunAsync("deployment", "create", "-f", template, "-p", "@" + parameters, "-y", "--secure-outputs-file", secrets, "-o", "json");
            afterCreate = directory.Count("/applications");
            delete = await CliRunner.RunAsync("deployment", "delete", "-f", template, "-p", "@" + parameters, "-y", "-o", "json");
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }

        Assert.True(whatIf.ExitCode == 0, whatIf.StandardOutput + whatIf.StandardError);
        Assert.True(create.ExitCode == 0, create.StandardOutput + create.StandardError);
        Assert.Equal(1, afterCreate);
        Assert.True(delete.ExitCode == 0, delete.StandardOutput + delete.StandardError);
        Assert.Equal(0, directory.Count("/applications"));

        // Only what the test wrote (the template, its parameter file) and the requested secure outputs file exist.
        Assert.True(File.Exists(secrets));
        Assert.Empty(sandbox.UnexpectedFiles(parameters, template, secrets));
        Assert.Empty(Directory.EnumerateDirectories(sandbox.Root, ".earm", SearchOption.AllDirectories));
        Assert.Equal(samplesBefore, Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(TestPaths.Sample(Sample + ".entra.json"))!, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray());
    }
}
