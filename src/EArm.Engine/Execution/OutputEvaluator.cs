using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Secrets;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Expressions;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Values;

namespace EArm.Engine.Execution;

/// <summary>
/// Evaluates the template outputs after the batches of a deployment run: the plain outputs, and the secure ones for the secure outputs file
/// (revealed in the result only on request). A null that stands for a secret Graph returned in an earlier run is reported as a warning.
/// </summary>
internal sealed class OutputEvaluator
{
    private readonly DeploymentPlan _plan;
    private readonly bool _revealSecureOutputs;
    private readonly RunDiagnostics _diagnostics;

    public OutputEvaluator(DeploymentPlan plan, bool revealSecureOutputs, RunDiagnostics diagnostics)
    {
        _plan = plan;
        _revealSecureOutputs = revealSecureOutputs;
        _diagnostics = diagnostics;
    }

    public (JsonObject Outputs, JsonObject SecureOutputs) Evaluate(CancellationToken cancellationToken)
    {
        var outputs = new JsonObject();
        var secure = new JsonObject();

        // Outputs are evaluated one at a time after every batch; the trace notes which published objects each
        // output's reference() calls touched, so a null that stands for a secret Graph only returned in an earlier
        // run (LiveSecrets) is reported as a warning instead of silently or fatally.
        var trace = new SecretAvailabilityTrace(_plan.Resolver);
        var context = _plan.Context.ForScope(EvaluationScope.Output) with { References = trace };
        var document = _plan.Document;

        foreach (var output in document.Outputs.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var valuePath = output.Copy?.Path ?? output.Path.Append("value");
            trace.Reset();
            try
            {
                if (output.Condition is not null)
                {
                    var condition = _plan.Evaluator.EvaluateTree(output.Condition, context);
                    if (!JsonValues.TryGetBool(condition, out bool include))
                    {
                        OutputError(document, output.Name, output.Path.Append("condition"), "the condition does not evaluate to a boolean.");
                        continue;
                    }

                    if (!include)
                    {
                        continue;
                    }
                }

                var value = CopyExpansion.EvaluateOutput(_plan.Evaluator, output, context);
                if (UnknownJson.ContainsUnknown(value))
                {
                    var leaves = JsonObjects.UnknownLeaves(value, JsonPath.Root);
                    OutputError(document, output.Name, valuePath, $"the value depends on something that was not resolved during this run: {string.Join("; ", leaves.Select(l => l.Symbol))}.");
                    continue;
                }

                string type = output.Type ?? "string";
                bool isSecure = output.IsSecure || SecretJson.ContainsSecret(value);
                if (trace.UnavailableSecrets.Count > 0 && LiveSecrets.ContainsNull(value))
                {
                    // The value is (or holds) the null a provider published in place of a one-time secret
                    // (reference(...).secretText of a password credential that was not created in this run).
                    string sources = string.Join(", ", trace.UnavailableSecrets.Select(id => $"'{id.Id}'"));
                    _diagnostics.Add(Diagnostic.Warning(
                        EngineDiagnosticCodes.SecretNotAvailableOnRerun,
                        $"Output '{output.Name}' is null: the secret of {sources} is only available in the run that creates it ({UnavailableSecretDetail(trace)}). The output is reported as null and left out of the secure outputs file.",
                        valuePath,
                        document.LocationOf(valuePath),
                        document.SourcePath));
                    outputs[output.Name] = new JsonObject { ["type"] = type, ["value"] = isSecure ? SecretJson.Redact(value) : JsonValues.Clone(value) };
                    continue;
                }

                if (isSecure)
                {
                    secure[output.Name] = new JsonObject { ["type"] = type, ["value"] = SecretJson.Reveal(value) };
                    outputs[output.Name] = new JsonObject { ["type"] = type, ["value"] = _revealSecureOutputs ? SecretJson.Reveal(value) : SecretJson.Redact(value) };
                }
                else
                {
                    outputs[output.Name] = new JsonObject { ["type"] = type, ["value"] = JsonValues.Clone(value) };
                }
            }
            catch (ExpressionException ex)
            {
                OutputError(document, output.Name, valuePath, ex.MessageWithOffset);
            }
        }

        return (outputs, secure);
    }

    /// <summary>Why the secrets an output read are not available, and what to do about it, per kind (password credential, user).</summary>
    private static string UnavailableSecretDetail(SecretAvailabilityTrace trace)
    {
        var parts = new List<string>(2);
        if (trace.UnavailableSecretTexts)
        {
            parts.Add("Graph returns a password credential's secretText once and it was not rotated in this run; change rotationTag to rotate the credential and obtain a new secret");
        }

        if (trace.UnavailableInitialPasswords)
        {
            parts.Add("Graph never returns a user's password, so the initial password is only known in the run that creates the user; it is in the secure outputs file of that run");
        }

        return string.Join("; ", parts);
    }

    private void OutputError(TemplateDocument document, string name, JsonPath path, string detail)
    {
        _diagnostics.Add(Diagnostic.Error(EngineDiagnosticCodes.OutputEvaluationFailed, $"Output '{name}' could not be evaluated: {detail}", path, document.LocationOf(path), document.SourcePath));
    }
}
