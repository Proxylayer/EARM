using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Types;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Values;

namespace EArm.Expressions.Functions;

/// <summary>
/// <c>reference(nameOrId[, apiVersion[, 'Full']])</c> and the languageVersion 2.0 <c>references(loop[, 'Full' | 'Properties'])</c>
/// (https://learn.microsoft.com/azure/azure-resource-manager/templates/template-functions-resource#reference). Registered with the
/// deployment group (see <see cref="DeploymentFunctions"/>).
/// </summary>
internal static class ReferenceFunctions
{
    private const string FullOption = "Full";
    private const string PropertiesOption = "Properties";

    /// <summary><c>reference(nameOrId[, apiVersion[, 'Full']])</c>: see <see cref="IReferenceResolver"/>.</summary>
    public static JsonNode? Reference(FunctionCall call)
    {
        var context = call.Context;
        bool full = call.Has(2) && ReadOption(call, 2, allowProperties: false);
        string? apiVersion = null;
        if (call.Has(1))
        {
            if (UnknownJson.IsUnknown(call.Arg(1), out var unknownVersion))
            {
                return call.Unknown(unknownVersion.Origins | UnknownOrigins.Reference, unknownVersion.Tainted);
            }

            apiVersion = call.String(1);
        }

        RequireScope(call, "reference()");
        var target = call.Arg(0);

        // languageVersion 2.0: a symbolic name always names a resource of this template (an apiVersion and 'Full' only shape the result).
        if (JsonValues.TryGetString(target, out var targetText) && SymbolicReference.TryParse(targetText, out var symbolicName, out var copyIndex)
            && context.Resources.FindSymbolicDeclaration(symbolicName) is { } declaration)
        {
            if (context.Scope == EvaluationScope.Variable)
            {
                throw new ExpressionEvaluationException(DiagnosticCodes.ReferenceNotAllowedHere, $"A variable cannot reference a resource of this template ('{call.Redact(0, targetText)}'); use reference() in the resource properties or the outputs.", call.Offset);
            }

            return ReferenceSymbolic(call, declaration, symbolicName, copyIndex, apiVersion, full);
        }

        if (context.Scope == EvaluationScope.Variable && apiVersion is null)
        {
            throw new ExpressionEvaluationException(DiagnosticCodes.ReferenceNotAllowedHere, "A variable cannot reference a resource of this template; only existing resources may be looked up from variables: reference(resourceId(...), apiVersion).", call.Offset);
        }

        ResourceIdentifier id;
        bool tainted = false;
        UnknownValue? unknownTarget = null;
        if (UnknownJson.IsUnknown(target, out var unknown))
        {
            unknownTarget = unknown;
            tainted = unknown.Tainted;
            if (unknown.ResourceId is { } symbolicId)
            {
                id = symbolicId;
            }
            else if (apiVersion is null && TryFindSymbolicByName(context.Resources, unknown.Symbol, out var sameComputation))
            {
                // reference(expr) where a resource's name is the same deploy-time computation: matched symbolically,
                // exactly as resourceId(type, expr) would be.
                id = sameComputation;
            }
            else
            {
                return call.Unknown(unknown.Origins | UnknownOrigins.Reference, unknown.Tainted);
            }
        }
        else
        {
            string text = call.String(0);
            try
            {
                id = ResolveTarget(call, text, apiVersion);
            }
            catch (ExpressionEvaluationException ex) when (apiVersion is null && ex.Code == DiagnosticCodes.ReferenceTargetNotFound && context.Resources.PendingCopyLoops.Count > 0)
            {
                return PendingCopyLoopTarget(call, ex);
            }
        }

        if (apiVersion is null && context.Resources.Entries.Count > 0 && !context.Resources.Contains(id))
        {
            if (unknownTarget is not null)
            {
                // A symbolic id (deploy-time name segments) that matches no resource: the target cannot be computed
                // offline. The static scanner classifies the result (an offline artefact when only a parameter value
                // is missing, otherwise ReferenceNotStaticallyResolvable); the symbol text is never reported as an id.
                return call.Unknown(unknownTarget.Origins | UnknownOrigins.Reference, tainted);
            }

            var notFound = new ExpressionEvaluationException(DiagnosticCodes.ReferenceTargetNotFound, $"reference(): '{call.Redact(0, id.Id)}' is not a resource of this template; to look up an existing resource pass an apiVersion: reference(resourceId(...), '{ResourceTypeCatalog.SchemaVersion}').", call.Offset);
            if (context.Resources.PendingCopyLoops.Count > 0)
            {
                return PendingCopyLoopTarget(call, notFound);
            }

            throw notFound;
        }

        var properties = Resolve(call, id, apiVersion, apiVersion, tainted);
        if (!full)
        {
            return properties;
        }

        JsonNode resourceId = unknownTarget is not null ? UnknownJson.Create(unknownTarget) : JsonValue.Create(id.Id);
        return Envelope(properties, resourceId, apiVersion ?? DeclaredApiVersion(context.Resources, id), symbolicName: null);
    }

    /// <summary>
    /// <c>references(loop[, 'Full' | 'Properties'])</c>: the runtime states of every iteration of a copy loop named by its symbolic name, in
    /// copy-index order (languageVersion 2.0). Not allowed inside the loop itself, nor where <c>reference()</c> is not.
    /// </summary>
    public static JsonNode? References(FunctionCall call)
    {
        var context = call.Context;
        bool full = call.Has(1) && ReadOption(call, 1, allowProperties: true);
        RequireScope(call, "references()");
        if (context.Scope == EvaluationScope.Variable)
        {
            throw new ExpressionEvaluationException(DiagnosticCodes.ReferenceNotAllowedHere, "references() cannot be used in a variable; use it in the resource properties or the outputs.", call.Offset);
        }

        string text = call.String(0);
        if (!SymbolicReference.TryParse(text, out var symbolicName, out var index) || index is not null || context.Resources.FindSymbolicDeclaration(symbolicName) is not { } declaration)
        {
            throw new ExpressionEvaluationException(DiagnosticCodes.ReferenceTargetNotFound, $"references(): '{call.Redact(0, text)}' is not the symbolic name of a resource collection (a copy loop) of this template; references() needs languageVersion 2.0 and a symbolic name.", call.Offset);
        }

        if (declaration.Copy is null)
        {
            throw new ExpressionEvaluationException(DiagnosticCodes.ReferenceTargetNotFound, $"references(): '{symbolicName}' is not a copy loop; use reference('{symbolicName}') for a single resource.", call.Offset);
        }

        string? loopName = Core.Json.TemplateJson.LiteralString(declaration.Copy.Name);
        if (loopName is not null && context.CopyStack.Any(frame => string.Equals(frame.Name, loopName, StringComparison.OrdinalIgnoreCase)) && context.Scope == EvaluationScope.Resource)
        {
            throw new ExpressionEvaluationException(DiagnosticCodes.ReferenceNotAllowedHere, $"references('{symbolicName}') cannot be used inside the copy loop it names.", call.Offset);
        }

        var entries = context.Resources.FindByDeclaration(declaration);
        if (entries.Count == 0 && context.Resources.PendingCopyLoops.Any(p => ReferenceEquals(p.Declaration, declaration)))
        {
            var pending = context.Resources.PendingCopyLoops.First(p => ReferenceEquals(p.Declaration, declaration));
            return UnknownJson.Create($"references('{symbolicName}')", pending.Origins == UnknownOrigins.Parameter ? UnknownOrigins.Parameter : pending.Origins | UnknownOrigins.Reference);
        }

        var result = new JsonArray();
        foreach (var entry in entries)
        {
            if (context.References.IsConditionFalse(entry.Id))
            {
                // ARM: an iteration that is not deployed because of a false condition is left out of the result.
                continue;
            }

            var properties = Resolve(call, entry.Id, null, null, tainted: false);
            result.Add(full ? Envelope(properties, IdValue(entry), declaration.ApiVersion ?? ResourceTypeCatalog.SchemaVersion, $"{symbolicName}[{entry.CopyIndex ?? 0}]") : properties);
        }

        return result;
    }

    /// <summary>True when <paramref name="target"/> is a symbolic name (or <c>loop[i]</c>) of a resource of this template (languageVersion 2.0).</summary>
    internal static bool NamesSymbolicResource(TemplateResourceIndex index, JsonNode? target)
    {
        ArgumentNullException.ThrowIfNull(index);
        return JsonValues.TryGetString(target, out var text) && SymbolicReference.TryParse(text, out var name, out _) && index.FindSymbolicDeclaration(name) is not null;
    }

    private static JsonNode? ReferenceSymbolic(FunctionCall call, ResourceDeclaration declaration, string symbolicName, long? copyIndex, string? apiVersion, bool full)
    {
        var context = call.Context;
        if (declaration.Copy is not null && copyIndex is null)
        {
            throw new ExpressionEvaluationException(DiagnosticCodes.ReferenceTargetNotFound, $"reference(): '{symbolicName}' is a copy loop; name one iteration with '{symbolicName}[i]' (Bicep: format('{symbolicName}[{{0}}]', i)) or use references('{symbolicName}').", call.Offset);
        }

        if (declaration.Copy is null && copyIndex is not null)
        {
            throw new ExpressionEvaluationException(DiagnosticCodes.InvalidSymbolicName, $"reference(): '{symbolicName}[{copyIndex}]' indexes a resource that is not a copy loop.", call.Offset);
        }

        var entries = context.Resources.FindByDeclaration(declaration, copyIndex);
        if (entries.Count != 1)
        {
            var notFound = new ExpressionEvaluationException(DiagnosticCodes.ReferenceTargetNotFound, $"reference(): the name of {declaration.Describe()} ('{symbolicName}{(copyIndex is null ? string.Empty : $"[{copyIndex}]")}') could not be computed.", call.Offset);
            if (copyIndex is not null && context.Resources.PendingCopyLoops.Count > 0)
            {
                return PendingCopyLoopTarget(call, notFound);
            }

            throw notFound;
        }

        var entry = entries[0];
        string? shownVersion = apiVersion ?? declaration.ApiVersion;
        var properties = Resolve(call, entry.Id, lookupVersion: null, shownVersion: null, tainted: false);
        string shownName = copyIndex is null ? symbolicName : $"{symbolicName}[{copyIndex}]";
        return full ? Envelope(properties, IdValue(entry), shownVersion ?? ResourceTypeCatalog.SchemaVersion, shownName) : properties;
    }

    /// <summary>Resolves the runtime state of <paramref name="id"/>: the live object, or a placeholder whose symbol names the target.</summary>
    private static JsonNode? Resolve(FunctionCall call, ResourceIdentifier id, string? lookupVersion, string? shownVersion, bool tainted)
    {
        var result = call.Context.References.Resolve(id, lookupVersion);
        switch (result)
        {
            case ReferenceResult.Live live:
                return tainted ? TaintHelper.Taint(JsonValues.Clone(live.Object)) : JsonValues.Clone(live.Object);
            case ReferenceResult.Placeholder placeholder:
                // The symbol is display text (it reaches messages and other symbols); the identity itself stays in Source.
                return UnknownJson.Create(DeploymentFunctions.Symbol(call.Redact(0, placeholder.Id.Id), shownVersion is null ? null : call.Redact(1, shownVersion)), UnknownOrigins.Reference, tainted, source: placeholder.Id);
            case ReferenceResult.NotFound notFound:
                throw new ExpressionEvaluationException(DiagnosticCodes.ReferenceTargetNotFound, $"reference(): existing resource '{call.Redact(0, notFound.Id.Id)}' (apiVersion {call.Redact(1, notFound.ApiVersion)}) was not found.", call.Offset);
            default:
                throw call.Failure($"the reference resolver returned an unexpected result ({result.GetType().Name}).");
        }
    }

    /// <summary>
    /// The <c>'Full'</c> form: ARM's full resource object reduced to what applies to Entra (no location, sku, subscription or resource group):
    /// <c>apiVersion</c>, <c>properties</c> (what the short form returns), <c>resourceId</c> (the EARM id, as <c>resourceId()</c> renders it),
    /// <c>isTemplateResource</c>, and for a symbolic reference <c>symbolicName</c>.
    /// </summary>
    private static JsonObject Envelope(JsonNode? properties, JsonNode resourceId, string apiVersion, string? symbolicName)
    {
        var envelope = new JsonObject
        {
            ["apiVersion"] = apiVersion,
            ["properties"] = properties?.Parent is null ? properties : JsonValues.Clone(properties),
            ["resourceId"] = resourceId,
            ["isTemplateResource"] = symbolicName is not null || UnknownJson.IsUnknown(properties, out var unknown) && unknown.Source is not null,
        };
        if (symbolicName is not null)
        {
            envelope["symbolicName"] = symbolicName;
        }

        return envelope;
    }

    private static JsonNode IdValue(TemplateResourceIndex.Entry entry)
    {
        return entry.IsSymbolic ? UnknownJson.Create($"resourceId({entry.Id.Type}, {entry.Id.Name})", UnknownOrigins.Reference, resourceId: entry.Id) : JsonValue.Create(entry.Id.Id);
    }

    private static string DeclaredApiVersion(TemplateResourceIndex index, ResourceIdentifier id)
    {
        return index.TryGet(id, out var entry) && entry.Declaration?.ApiVersion is { } version ? version : ResourceTypeCatalog.SchemaVersion;
    }

    /// <summary>Reads the <c>'Full'</c> (or, for <c>references()</c>, <c>'Properties'</c>) option, compared case-insensitively as ARM does.</summary>
    private static bool ReadOption(FunctionCall call, int index, bool allowProperties)
    {
        string option = call.String(index);
        if (string.Equals(option, FullOption, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (allowProperties && string.Equals(option, PropertiesOption, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        throw call.Error(allowProperties ? $"the last argument must be 'Full' or 'Properties', got {call.Render(index)}." : $"the third argument must be 'Full', got {call.Render(index)}.");
    }

    private static void RequireScope(FunctionCall call, string function)
    {
        switch (call.Context.Scope)
        {
            case EvaluationScope.ParameterDefault:
                throw new ExpressionEvaluationException(DiagnosticCodes.ReferenceNotAllowedHere, $"{function} cannot be used in a parameter default value.", call.Offset);
            case EvaluationScope.UserFunction:
                throw new ExpressionEvaluationException(DiagnosticCodes.ReferenceNotAllowedHere, $"{function} cannot be used inside a user-defined function.", call.Offset);
        }
    }

    /// <summary>
    /// A same-template <c>reference()</c> that names no indexed resource while some copy loop could not be expanded statically
    /// (its count is not known yet): the target may be one of that loop's instances. When every such loop waits only for a
    /// parameter value that is missing offline the result is an unknown value of origin <see cref="UnknownOrigins.Parameter"/>
    /// alone (the static scanner defers it); otherwise the loop's count is only known at deployment, which ARM does not allow
    /// either, and the reference fails with that explanation.
    /// </summary>
    private static JsonNode? PendingCopyLoopTarget(FunctionCall call, ExpressionEvaluationException notFound)
    {
        var pending = call.Context.Resources.PendingCopyLoops;
        if (pending.All(p => p.Origins == UnknownOrigins.Parameter))
        {
            return UnknownJson.Create($"ref({call.Render(0)})", UnknownOrigins.Parameter);
        }

        var loops = string.Join(", ", pending.Where(p => p.Origins != UnknownOrigins.Parameter).Select(p => $"'{p.Name}' (count {p.Symbol})"));
        throw new ExpressionEvaluationException(notFound.Code, $"{notFound.Message} It may name an instance of copy loop {loops}, but that loop's count is only known at deployment, so its instances cannot be referenced by name; make the count computable from parameters and variables.", notFound.Offset);
    }

    /// <summary>
    /// Finds the one resource whose name is the deploy-time computation <paramref name="symbol"/>: a symbolic index
    /// entry whose single name segment is that symbol (see <see cref="UnknownValue.Symbol"/>). False when none or
    /// several match (several is a duplicate-id situation reported by the static evaluator).
    /// </summary>
    private static bool TryFindSymbolicByName(TemplateResourceIndex index, string symbol, [NotNullWhen(true)] out ResourceIdentifier? id)
    {
        id = null;
        foreach (var entry in index.Entries)
        {
            if (!entry.IsSymbolic || entry.Id.NameSegments.Count != 1 || !string.Equals(entry.Id.NameSegments[0], symbol, StringComparison.Ordinal))
            {
                continue;
            }

            if (id is not null)
            {
                id = null;
                return false;
            }

            id = entry.Id;
        }

        return id is not null;
    }

    /// <summary>
    /// Turns the string argument of <c>reference()</c> into an identity: a resource id, a bare name (unique in the
    /// template), or a <c>type/name</c> pair.
    /// </summary>
    internal static ResourceIdentifier ResolveTarget(FunctionCall call, string text, string? apiVersion)
    {
        var context = call.Context;
        if (text.StartsWith(ResourceIdentifier.ProvidersPrefix, StringComparison.OrdinalIgnoreCase))
        {
            if (!context.Catalog.TryParseResourceId(text, out var parsed) || parsed is null)
            {
                throw new ExpressionEvaluationException(DiagnosticCodes.InvalidResourceId, $"reference(): '{call.Redact(0, text)}' is not a valid resource id (expected /providers/{ResourceIdentifier.ProviderNamespace}/<type>/<name>[/<childType>/<childName>] with a known type).", call.Offset);
            }

            return parsed;
        }

        var byName = context.Resources.FindByName(text);
        if (byName.Count == 1)
        {
            return byName[0].Id;
        }

        if (byName.Count > 1)
        {
            throw new ExpressionEvaluationException(DiagnosticCodes.AmbiguousReference, $"reference(): '{call.Redact(0, text)}' names {byName.Count} resources ({string.Join(", ", byName.Select(e => e.Id.Type))}); use resourceId() to pick one.", call.Offset);
        }

        if (DeploymentFunctions.TryParseTypeAndName(context.Catalog, text, out var typed))
        {
            return typed;
        }

        if (apiVersion is not null)
        {
            throw new ExpressionEvaluationException(DiagnosticCodes.ReferenceTargetNotFound, $"reference(): '{call.Redact(0, text)}' is not a resource of this template; an existing resource must be identified by resourceId() or 'type/name' together with the apiVersion.", call.Offset);
        }

        throw new ExpressionEvaluationException(DiagnosticCodes.ReferenceTargetNotFound, $"reference(): no resource named '{call.Redact(0, text)}' is declared in this template.", call.Offset);
    }
}
