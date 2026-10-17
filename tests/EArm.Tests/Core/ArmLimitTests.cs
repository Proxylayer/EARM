using System.Linq;
using System.Text;
using EArm.Core.Diagnostics;
using Xunit;

namespace EArm.Tests;

/// <summary>
/// The ARM template limits (256 parameters, 512 variables, 64 outputs, 800 resources with copy counts, 4 MB) are warnings, never errors:
/// EARM keeps its own, larger hard limits.
/// </summary>
public sealed class ArmLimitTests
{
    private static string Members(string section, int count, string valueFormat)
    {
        var sb = new StringBuilder("\"" + section + "\": { ");
        for (int i = 0; i < count; i++)
        {
            sb.Append(i == 0 ? string.Empty : ", ").Append("\"m").Append(i).Append("\": ").Append(valueFormat.Replace("{i}", i.ToString(System.Globalization.CultureInfo.InvariantCulture), System.StringComparison.Ordinal));
        }

        return sb.Append(" }, ").ToString();
    }

    [Theory]
    [InlineData("parameters", 256, false)]
    [InlineData("parameters", 257, true)]
    [InlineData("variables", 512, false)]
    [InlineData("variables", 513, true)]
    [InlineData("outputs", 64, false)]
    [InlineData("outputs", 65, true)]
    public void ASectionOverTheArmLimit_IsAWarning(string section, int count, bool warned)
    {
        string value = section switch
        {
            "parameters" => "{ \"type\": \"int\", \"defaultValue\": 1 }",
            "variables" => "1",
            _ => "{ \"type\": \"int\", \"value\": 1 }",
        };

        var diagnostics = Review1C.Validate(Review1C.Template(Members(section, count, value) + "\"resources\": []"));

        Assert.DoesNotContain(diagnostics, d => d.IsError);
        Assert.Equal(warned, diagnostics.Any(d => d.Code == DiagnosticCodes.ArmLimitExceeded && d.Severity == DiagnosticSeverity.Warning && d.Path!.ToString() == section));
    }

    [Theory]
    [InlineData(800, false)]
    [InlineData(801, true)]
    public void ResourcesOverTheArmLimit_CountCopyIterations(int count, bool warned)
    {
        static string Loop(string prefix, int n) =>
            "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"[concat('" + prefix + "', copyIndex())]\", \"copy\": { \"name\": \"" + prefix + "\", \"count\": " + n + " }, "
            + "\"properties\": { \"displayName\": \"[concat('" + prefix + "', copyIndex())]\", \"mailEnabled\": false, \"mailNickname\": \"[concat('" + prefix + "', copyIndex())]\", \"securityEnabled\": true } }";

        var diagnostics = Review1C.Validate(Review1C.Template("\"resources\": [ " + Loop("a", 400) + ", " + Loop("b", count - 400) + " ]"));

        Assert.Equal(warned, diagnostics.Any(d => d.Code == DiagnosticCodes.ArmLimitExceeded && d.Path!.ToString() == "resources"));
        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.ArmLimitExceeded && d.IsError);
    }

    [Fact]
    public void ATemplateOver4MB_IsAWarning()
    {
        string padding = new('x', (4 * 1024 * 1024) + 16);
        var diagnostics = Review1C.Validate(Review1C.Template("\"metadata\": { \"pad\": \"" + padding + "\" }, \"resources\": []"));

        Assert.Contains(diagnostics, d => d.Code == DiagnosticCodes.ArmLimitExceeded && d.Severity == DiagnosticSeverity.Warning && d.Path!.IsRoot);
        Assert.DoesNotContain(diagnostics, d => d.IsError);
    }
}
