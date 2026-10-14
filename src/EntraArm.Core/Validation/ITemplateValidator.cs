using System.Collections.Generic;
using EntraArm.Core.Diagnostics;

namespace EntraArm.Core.Validation;

/// <summary>
/// Offline template validation: JSON Schema plus the semantic checks that need no expression evaluation.
/// The result includes the document's <see cref="TemplateDocument.LoadDiagnostics"/>, so callers get one list.
/// </summary>
public interface ITemplateValidator
{
    IReadOnlyList<Diagnostic> Validate(TemplateDocument document);
}
