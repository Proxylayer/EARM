using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EArm.Tests.Engine;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// Golden-file tests over tests/fixtures/permissions: the table and JSON output of <c>earm permissions</c> for every sample in both modes,
/// byte for byte (line endings normalised). The goldens were captured before the offline pipeline moved into
/// <c>EArm.Engine.Permissions</c>, so they pin the command's output across that refactoring. Regenerate deliberately only when the output is meant to change:
/// <c>tools/Update-PermissionsGoldens.ps1</c> sets <c>EARM_UPDATE_GOLDENS=1</c>, and in that mode each case rewrites its golden file (UTF-8 without BOM, LF,
/// trailing blanks trimmed, as the comparison normalises) instead of comparing; review the diff before committing it. CI refuses the variable
/// (<see cref="WhatIfGoldenTests.IsUpdateRequested"/>).
/// </summary>
public sealed class PermissionsGoldenTests
{
    private static string GoldenDirectory => Path.Combine(TestPaths.RepoRoot, "tests", "fixtures", "permissions");

    public static TheoryData<string, string, string, string> Cases()
    {
        var data = new TheoryData<string, string, string, string>();
        (string Sample, string Parameters)[] samples =
        [
            ("orders-api", "orders-api"),
            ("orders-api.flat", "orders-api"),
            ("app-with-sp-and-secret", "app-with-sp-and-secret"),
            ("app-certificates-extensions", "app-certificates-extensions"),
            ("tenant-security", "tenant-security"),
            ("authentication-methods", "authentication-methods"),
            ("tenant-policies", "tenant-policies"),
            ("pim-eligibility", "pim-eligibility"),
            ("pim-active", "pim-active"),
            ("pim-policies", "pim-policies"),
            ("azure-pim-policies", "azure-pim-policies"),
            ("pim-group-onboarding", "pim-group-onboarding"),
            ("privileged-access", "privileged-access"),
            ("access-packages", "access-packages"),
            ("access-packages-apps-sites", "access-packages-apps-sites"),
            ("access-reviews", "access-reviews"),
        ];
        foreach (var (sample, parameters) in samples)
        {
            foreach (string mode in new[] { "app", "delegated" })
            {
                foreach (string format in new[] { "table", "json" })
                {
                    data.Add(sample, parameters, mode, format);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Output_MatchesTheGolden(string sample, string parameters, string mode, string format)
    {
        string template = TestPaths.Sample(sample + ".entra.json");
        var result = await CliRunner.RunAsync("permissions", "-f", template, "-p", "@" + TestPaths.Sample(parameters + ".parameters.json"), "--mode", mode, "-o", format);

        Assert.Equal(0, result.ExitCode);

        // The goldens were captured with the repository-relative template path.
        string relative = "samples/" + sample + ".entra.json";
        string actual = result.StandardOutput
            .Replace(template.Replace("\\", "\\\\", StringComparison.Ordinal), relative, StringComparison.Ordinal)
            .Replace(template, relative, StringComparison.Ordinal);
        string golden = Path.Combine(GoldenDirectory, $"{sample}.{mode}.{(format == "json" ? "json" : "txt")}");
        if (WhatIfGoldenTests.IsUpdateRequested(Environment.GetEnvironmentVariable(WhatIfGoldenTests.UpdateVariable), Environment.GetEnvironmentVariable("CI")))
        {
            File.WriteAllText(golden, Normalize(actual) + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return;
        }

        string expected = File.ReadAllText(golden);

        Assert.Equal(Normalize(expected), Normalize(actual));
    }

    private static string Normalize(string text)
    {
        return string.Join('\n', text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(l => l.TrimEnd())).TrimEnd();
    }
}
