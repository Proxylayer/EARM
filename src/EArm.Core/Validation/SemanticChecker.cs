using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Types;

namespace EArm.Core.Validation;

/// <summary>
/// The semantic checks that need only the parsed document and the type catalog (no expression evaluation).
/// Appends to the diagnostics list it is given.
/// </summary>
internal sealed partial class SemanticChecker
{
    private static readonly string[] KnownTopLevelKeys = ["$schema", "languageVersion", "contentVersion", "metadata", "definitions", "parameters", "variables", "functions", "resources", "outputs"];

    private readonly ResourceTypeCatalog _catalog;
    private readonly TemplateDocument _document;
    private readonly List<Diagnostic> _diagnostics;

    public SemanticChecker(ResourceTypeCatalog catalog, TemplateDocument document, List<Diagnostic> diagnostics)
    {
        _catalog = catalog;
        _document = document;
        _diagnostics = diagnostics;
    }

    public void Run()
    {
        CheckEnvelope();
        new ParameterDeclarationChecker(_document, _diagnostics).Run();
        CheckFunctions();
        new ResourceDeclarationChecker(_catalog, _document, _diagnostics).Run();
        CheckOutputs();
    }

    // ---- envelope ----

    private void CheckEnvelope()
    {
        var root = _document.Root;

        if (_document.Schema is null && !_document.IsNested)
        {
            Error(DiagnosticCodes.UnrecognizedSchema, $"The template is missing '$schema'; expected '{ResourceTypeCatalog.TemplateSchemaUri}#'.", JsonPath.Of("$schema"));
        }
        else if (_document.Schema is not null && !ResourceTypeCatalog.IsTemplateSchemaRecognized(_document.Schema) && !(_document.IsNested && ResourceTypeCatalog.IsArmTemplateSchema(_document.Schema)))
        {
            Error(DiagnosticCodes.UnrecognizedSchema, $"'$schema' value '{_document.Schema}' is not recognised; expected '{ResourceTypeCatalog.TemplateSchemaUri}#'.", JsonPath.Of("$schema"));
        }

        if (!root.ContainsKey("contentVersion"))
        {
            Error(DiagnosticCodes.MissingRequiredProperty, "The template is missing the required 'contentVersion' property.", JsonPath.Of("contentVersion"));
        }
        else if (_document.ContentVersion is null)
        {
            // ARM accepts any value ("You can provide any value for this element"); EARM only requires it to be a string.
            Error(DiagnosticCodes.InvalidContentVersion, "'contentVersion' must be a string, for example '1.0.0.0'.", JsonPath.Of("contentVersion"));
        }

        if (!root.ContainsKey("resources"))
        {
            Error(DiagnosticCodes.MissingRequiredProperty, "The template is missing the required 'resources' (an array, or with languageVersion 2.0 an object keyed by symbolic name).", JsonPath.Of("resources"));
        }

        foreach (var key in _document.TopLevelKeys)
        {
            if (Array.IndexOf(KnownTopLevelKeys, key) < 0)
            {
                Warning(DiagnosticCodes.UnknownTopLevelProperty, $"Top-level property '{key}' is not part of the template format and will be ignored.", JsonPath.Of(key));
            }
        }
    }

    // ---- functions ----

    private void CheckFunctions()
    {
        foreach (var ns in _document.Functions)
        {
            if (ns.Namespace is null)
            {
                Error(DiagnosticCodes.MissingRequiredProperty, "A function namespace entry is missing the required 'namespace' property.", ns.Path.Append("namespace"));
            }

            if (ns.Source["members"] is not JsonObject)
            {
                Error(DiagnosticCodes.MissingRequiredProperty, $"Function namespace '{ns.Namespace}' is missing the required 'members' object.", ns.Path.Append("members"));
            }

            foreach (var member in ns.Members.Values)
            {
                if (member.Source["output"] is not JsonObject)
                {
                    Error(DiagnosticCodes.MissingRequiredProperty, $"Function '{ns.Namespace}.{member.Name}' is missing the required 'output' object.", member.Path.Append("output"));
                    continue;
                }

                if (member.OutputType is null)
                {
                    Error(DiagnosticCodes.MissingRequiredProperty, $"Function '{ns.Namespace}.{member.Name}' output is missing the required 'type' property.", member.Path.Append("output").Append("type"));
                }
                else if (!TemplateParameterTypes.TryParse(member.OutputType, out _))
                {
                    Error(DiagnosticCodes.InvalidOutputType, $"Function '{ns.Namespace}.{member.Name}' output has unknown type '{member.OutputType}'.", member.Path.Append("output").Append("type"));
                }

                if (!((JsonObject)member.Source["output"]!).ContainsKey("value"))
                {
                    Error(DiagnosticCodes.MissingRequiredProperty, $"Function '{ns.Namespace}.{member.Name}' output is missing the required 'value' property.", member.Path.Append("output").Append("value"));
                }

                for (int i = 0; i < member.Parameters.Count; i++)
                {
                    var parameter = member.Parameters[i];
                    if (parameter.Name is null)
                    {
                        Error(DiagnosticCodes.MissingRequiredProperty, $"Function '{ns.Namespace}.{member.Name}' parameter {i} is missing 'name'.", member.Path.Append("parameters").Append(i));
                    }

                    if (parameter.Type is null)
                    {
                        Error(DiagnosticCodes.MissingRequiredProperty, $"Function '{ns.Namespace}.{member.Name}' parameter '{parameter.Name}' is missing 'type'.", member.Path.Append("parameters").Append(i));
                    }
                    else if (!TemplateParameterTypes.TryParse(parameter.Type, out _))
                    {
                        Error(DiagnosticCodes.InvalidParameterType, $"Function '{ns.Namespace}.{member.Name}' parameter '{parameter.Name}' has unknown type '{parameter.Type}'.", member.Path.Append("parameters").Append(i).Append("type"));
                    }
                }
            }
        }
    }

    // ---- outputs ----

    private void CheckOutputs()
    {
        foreach (var output in _document.Outputs.Values)
        {
            if (output.TypeProblem is not null)
            {
                // An unresolvable '$ref' is reported by the type definition checker.
            }
            else if (output.Type is null)
            {
                Error(DiagnosticCodes.MissingRequiredProperty, $"Output '{output.Name}' is missing the required 'type' property.", output.Path.Append("type"));
            }
            else if (output.ParsedType is null)
            {
                Error(DiagnosticCodes.InvalidOutputType, $"Output '{output.Name}' has unknown type '{output.Type}'; expected one of: {string.Join(", ", TemplateParameterTypes.CanonicalNames)}.", output.Path.Append("type"));
            }

            if (!output.Source.ContainsKey("value") && !output.Source.ContainsKey("copy"))
            {
                Error(DiagnosticCodes.MissingRequiredProperty, $"Output '{output.Name}' is missing the required 'value' property (or a 'copy' loop).", output.Path.Append("value"));
            }
        }
    }

    // ---- helpers ----

    private void Error(string code, string message, JsonPath path)
    {
        _diagnostics.Add(Diagnostic.Error(code, message, path, _document.LocationOf(path), _document.SourcePath));
    }

    private void Warning(string code, string message, JsonPath path)
    {
        _diagnostics.Add(Diagnostic.Warning(code, message, path, _document.LocationOf(path), _document.SourcePath));
    }
}
