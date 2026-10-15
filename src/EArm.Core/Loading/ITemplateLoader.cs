using System;
using System.Threading;
using System.Threading.Tasks;

namespace EArm.Core.Loading;

/// <summary>
/// Reads a <c>*.entra.json</c> template (comments and trailing commas allowed), flattens nested child resources
/// into top-level declarations with <c>parent/child</c> names and binds the supplied parameters to the template's
/// parameter definitions. Fatal problems raise <see cref="TemplateLoadException"/>; everything else is reported on
/// <see cref="TemplateDocument.LoadDiagnostics"/>.
/// </summary>
public interface ITemplateLoader
{
    Task<TemplateDocument> LoadAsync(string path, ParameterSet parameters, CancellationToken ct);

    TemplateDocument Parse(string json, string? sourcePath, ParameterSet parameters);

    TemplateDocument Parse(ReadOnlyMemory<byte> utf8, string? sourcePath, ParameterSet parameters);
}
