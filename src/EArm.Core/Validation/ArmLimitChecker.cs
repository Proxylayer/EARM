using System.Collections.Generic;
using System.Globalization;
using EArm.Core.Diagnostics;
using EArm.Core.Json;

namespace EArm.Core.Validation;

/// <summary>
/// Warns (never errors) when a template exceeds a limit Azure Resource Manager enforces, so a template that is also meant for ARM tooling is
/// flagged early: 256 parameters, 512 variables, 64 outputs, 800 resources (copy counts included) and 4 MB of template text
/// (https://learn.microsoft.com/azure/azure-resource-manager/templates/best-practices#template-limits). EARM's own hard limits (input size,
/// instance count, evaluation budget) are enforced elsewhere and are unchanged.
/// </summary>
internal sealed class ArmLimitChecker
{
    public const int MaxParameters = 256;
    public const int MaxVariables = 512;
    public const int MaxOutputs = 64;
    public const int MaxResources = 800;
    public const long MaxTemplateBytes = 4L * 1024 * 1024;

    private readonly TemplateDocument _document;
    private readonly List<Diagnostic> _diagnostics;

    public ArmLimitChecker(TemplateDocument document, List<Diagnostic> diagnostics)
    {
        _document = document;
        _diagnostics = diagnostics;
    }

    public void Run()
    {
        Check(_document.Parameters.Count, MaxParameters, "parameters", JsonPath.Of("parameters"));
        Check(_document.Variables.Count, MaxVariables, "variables", JsonPath.Of("variables"));
        Check(_document.Outputs.Count, MaxOutputs, "outputs", JsonPath.Of("outputs"));
        Check(CountResources(), MaxResources, "resources (copy counts included)", JsonPath.Of("resources"));

        if (_document.SourceByteLength > MaxTemplateBytes)
        {
            Warn($"The template is {_document.SourceByteLength.ToString("N0", CultureInfo.InvariantCulture)} bytes; Azure Resource Manager limits a template to 4 MB ({MaxTemplateBytes.ToString("N0", CultureInfo.InvariantCulture)} bytes). EARM accepts it.", JsonPath.Root);
        }
    }

    /// <summary>Declarations, each counted once per iteration when its <c>copy.count</c> is a literal (an expression count is only known after expansion and counts once here).</summary>
    private long CountResources()
    {
        long total = 0;
        foreach (var resource in _document.Resources)
        {
            long? count = resource.Copy is { } copy && !TemplateJson.IsExpression(copy.Count) ? TemplateJson.AsInt64(copy.Count) : null;
            total += count is > 0 ? count.Value : 1;
        }

        return total;
    }

    private void Check(long count, int limit, string what, JsonPath path)
    {
        if (count > limit)
        {
            Warn($"The template declares {count.ToString(CultureInfo.InvariantCulture)} {what}; Azure Resource Manager allows at most {limit.ToString(CultureInfo.InvariantCulture)}. EARM accepts it, but the template would be refused by ARM tooling.", path);
        }
    }

    private void Warn(string message, JsonPath path)
    {
        _diagnostics.Add(Diagnostic.Warning(DiagnosticCodes.ArmLimitExceeded, message, path, _document.LocationOf(path), _document.SourcePath));
    }
}
