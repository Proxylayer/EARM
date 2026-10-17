using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Values;

namespace EArm.Expressions.Static;

/// <summary>The tree walk of <see cref="ReferenceScanner"/>, including the loops that build arrays (property and output iteration).</summary>
public static partial class ReferenceScanner
{
    /// <summary>
    /// Walks a JSON tree. <c>path</c> is where a value ends up once evaluated (occurrences carry it: a property iteration entry
    /// <c>memberOf</c> yields <c>properties.memberOf[i]</c>, so edge classification sees the property it builds); <c>sourcePath</c> is
    /// where the expression is written, for diagnostics.
    /// </summary>
    private sealed class TreeScan(ExpressionEvaluator evaluator, StaticContext context, List<ReferenceOccurrence> occurrences, List<Diagnostic> diagnostics)
    {
        public void Walk(JsonNode? node, EvaluationContext evaluationContext, JsonPath path, JsonPath sourcePath)
        {
            switch (node)
            {
                case JsonObject obj:
                    bool expandCopy = evaluationContext.Scope == EvaluationScope.Resource;
                    foreach (var property in obj)
                    {
                        if (expandCopy && PropertyCopyBlock.IsBlock(property.Key, property.Value))
                        {
                            continue;
                        }

                        Walk(property.Value, evaluationContext, path.Append(property.Key), sourcePath.Append(property.Key));
                    }

                    if (expandCopy)
                    {
                        foreach (var entry in PropertyCopyBlock.Entries(obj))
                        {
                            if (entry.Name is not { } name)
                            {
                                continue; // Reported by the template validator.
                            }

                            var entryPath = sourcePath.Append(PropertyCopyBlock.Key).Append(entry.Index);
                            var target = path.Append(name);
                            WalkLoop(name, entry.Count, entry.Input, requiresName: true, evaluationContext, entryPath.Append("count"), i => target.Append((int)i), entryPath.Append("input"), $"property copy '{name}'");
                        }
                    }

                    break;
                case JsonArray array:
                    for (int i = 0; i < array.Count; i++)
                    {
                        Walk(array[i], evaluationContext, path.Append(i), sourcePath.Append(i));
                    }

                    break;
                default:
                    {
                        string? text = TemplateJson.AsString(node);
                        if (text is null || !ExpressionSyntax.IsExpression(text))
                        {
                            return;
                        }

                        ScanExpression(evaluator, text, context, evaluationContext, path, sourcePath, occurrences, diagnostics);
                        break;
                    }
            }
        }

        /// <summary>
        /// One loop that builds an array: the input is walked once per element with a concrete frame when the count is known (each
        /// reference resolves to its own target, so a dependency is exact), otherwise once with the frame unresolved (deferred).
        /// </summary>
        public void WalkLoop(string name, JsonNode? count, JsonNode? input, bool requiresName, EvaluationContext evaluationContext, JsonPath countPath, Func<long, JsonPath> elementPath, JsonPath inputPath, string what)
        {
            long? n;
            UnknownValue? unknown;
            try
            {
                n = CopyExpansion.EvaluateCount(evaluator, count, evaluationContext, what, out unknown);
            }
            catch (ExpressionException ex)
            {
                diagnostics.Add(Diagnostic.Error(ex.Code, ex.MessageWithOffset, countPath, context.Document.LocationOf(countPath), context.Document.SourcePath));
                return;
            }

            if (n is { } known)
            {
                for (long i = 0; i < known; i++)
                {
                    Walk(input, evaluationContext.PushCopy(new CopyFrame(name, i, known, requiresName)), elementPath(i), inputPath);
                }

                return;
            }

            if (unknown is not null && (unknown.Origins & UnknownOrigins.Copy) == 0)
            {
                if (unknown.Origins == UnknownOrigins.Parameter)
                {
                    diagnostics.Add(Diagnostic.Info(DiagnosticCodes.ReferenceDependsOnMissingParameter, $"The count of {what} depends on a parameter that has no value offline; its elements are checked once the value is supplied. Deploy-time value: {unknown.Symbol}.", countPath, context.Document.LocationOf(countPath), context.Document.SourcePath));
                }
                else
                {
                    diagnostics.Add(Diagnostic.Error(DiagnosticCodes.InvalidCopy, $"The count of {what} cannot be determined before deployment: it must be computable from literals, parameters and variables (ARM evaluates loop counts before anything is deployed). Offending value: {unknown.Symbol}.", countPath, context.Document.LocationOf(countPath), context.Document.SourcePath));
                }
            }

            Walk(input, evaluationContext.PushCopy(CopyFrame.Unresolved(name, null, requiresName)), elementPath(0), inputPath);
        }
    }
}
