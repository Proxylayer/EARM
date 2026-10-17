using System;
using EArm.Core;
using EArm.Engine.Providers;
using EArm.Expressions;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Values;

namespace EArm.Engine.Planning;

/// <summary>
/// The adopt-only rule of a resource that declares <c>"x-earm": { "adoptOnly": true, "objectId": "…" }</c> (<see cref="AdoptOnlyDeclaration"/>), applied to the
/// change the provider planned from the key read, in the what-if and right before applying alike: no object under the key is
/// <see cref="EngineDiagnosticCodes.AdoptOnlyNotFound"/> instead of a create, and a found object whose Graph id is not the pinned one is
/// <see cref="EngineDiagnosticCodes.AdoptedObjectMismatch"/> instead of an update. Every other change passes through.
/// </summary>
internal static class AdoptOnlyCheck
{
    public static ResourceChange Apply(ExpressionEvaluator evaluator, ResourceInstance instance, ExistingResource? existing, ResourceChange change)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(change);
        if (instance.Declaration.AdoptOnly is not { } adoptOnly || instance.IsIgnored)
        {
            return change;
        }

        // A planned create (even one the provider could not build, such as a user without displayName) is replaced: nothing is created.
        if (existing is null && change.Type == ChangeType.Create)
        {
            string message = $"{instance.Describe()} is adopt-only (\"{AdoptOnlyDeclaration.Key}\": {{ \"{AdoptOnlyDeclaration.AdoptOnlyProperty}\": true }}): the object must already exist and EARM never creates it, but none has its key. Create it first (or correct its name), or remove {AdoptOnlyDeclaration.AdoptOnlyProperty} to let the template create it.";
            return ResourceChange.Unsupported(instance.Id, instance.Type, ChangeType.Create, message) with { UnsupportedCode = EngineDiagnosticCodes.AdoptOnlyNotFound };
        }

        if (existing is null || adoptOnly.ObjectId is null || change.HasError)
        {
            return change;
        }

        string? pinned = Pinned(evaluator, instance, adoptOnly, out string? problem);
        if (pinned is null)
        {
            string message = $"{instance.Describe()}: {AdoptOnlyDeclaration.Key}.{AdoptOnlyDeclaration.ObjectIdProperty} {problem}; the pinned object id must be known before deployment (a literal, a parameter or a variable).";
            return ResourceChange.Unsupported(instance.Id, instance.Type, change.Type, message) with { UnsupportedCode = EngineDiagnosticCodes.AdoptedObjectMismatch };
        }

        if (!string.Equals(pinned.Trim(), existing.GraphId, StringComparison.OrdinalIgnoreCase))
        {
            string message = $"{instance.Describe()}: its key leads to object {existing.GraphId}, but {AdoptOnlyDeclaration.Key}.{AdoptOnlyDeclaration.ObjectIdProperty} pins {pinned.Trim()}. Nothing was changed: a different object holds the name (or the pinned object was renamed). Correct the name or the pinned id.";
            return ResourceChange.Unsupported(instance.Id, instance.Type, change.Type, message) with { UnsupportedCode = EngineDiagnosticCodes.AdoptedObjectMismatch };
        }

        return change with { Reasons = [.. change.Reasons, $"adopt-only: the object is the pinned {existing.GraphId}"] };
    }

    private static string? Pinned(ExpressionEvaluator evaluator, ResourceInstance instance, AdoptOnlyDeclaration adoptOnly, out string? problem)
    {
        problem = null;
        try
        {
            var value = evaluator.EvaluateTree(adoptOnly.ObjectId, instance.Context);
            if (UnknownJson.ContainsUnknown(value))
            {
                problem = "depends on a value that is only known at deployment";
                return null;
            }

            if (JsonValues.TryGetString(value, out string text) && Guid.TryParse(text, out _))
            {
                return text;
            }

            problem = $"is {JsonValues.Describe(value)}, not an object id (a GUID)";
            return null;
        }
        catch (ExpressionException ex)
        {
            problem = "cannot be evaluated: " + ex.MessageWithOffset;
            return null;
        }
    }
}
