using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using EArm.Core.Json;
using EArm.Core.Types;
using Json.Schema;

namespace EArm.Core.Validation;

/// <summary>One JSON Schema failure, located in the instance.</summary>
internal sealed record SchemaFinding(JsonPath Path, string Keyword, string Message, string EvaluationPath);

/// <summary>
/// The embedded JSON Schemas, built once. The per-type schemas are built first so the root template schema's
/// <c>$ref</c>s to them resolve through the global schema registry (JsonSchema.Net registers every schema by its
/// <c>$id</c> when it is built).
/// </summary>
internal sealed class TemplateSchemas
{
    private static readonly Lazy<TemplateSchemas> Embedded = new(Load);

    private static readonly HashSet<string> SpecificKeywords = new(StringComparer.Ordinal)
    {
        "type", "enum", "const", "pattern", "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum",
        "minLength", "maxLength", "minItems", "maxItems", "uniqueItems", "additionalProperties", "required",
        "format", "multipleOf", "minProperties", "maxProperties", "dependentRequired",
    };

    private TemplateSchemas(JsonSchema template, JsonSchema parameters, IReadOnlyList<JsonSchema> types)
    {
        Template = template;
        Parameters = parameters;
        Types = types;
    }

    public static TemplateSchemas Default => Embedded.Value;

    public JsonSchema Template { get; }

    public JsonSchema Parameters { get; }

    public IReadOnlyList<JsonSchema> Types { get; }

    private static TemplateSchemas Load()
    {
        var types = new List<JsonSchema>();
        foreach (var name in EmbeddedSchemas.NamesUnder(EmbeddedSchemas.TypeSchemaPrefix))
        {
            types.Add(JsonSchema.FromText(EmbeddedSchemas.ReadText(name)));
        }

        var template = JsonSchema.FromText(EmbeddedSchemas.ReadText(EmbeddedSchemas.TemplateSchemaResource));
        var parameters = JsonSchema.FromText(EmbeddedSchemas.ReadText(EmbeddedSchemas.ParametersSchemaResource));
        return new TemplateSchemas(template, parameters, types);
    }

    /// <summary>
    /// Evaluates <paramref name="instance"/> and returns at most one finding per instance location, preferring
    /// specific keyword failures (type, enum, pattern...) over applicator noise and the typed branch of
    /// <c>anyOf: [typed, expression]</c> over the expression branch.
    /// </summary>
    /// <remarks>
    /// The hierarchical output is walked top-down and only invalid nodes are entered: a failed <c>if</c> condition
    /// or the failing branch of an <c>anyOf</c> that passed overall sits under a valid node and is therefore never
    /// reported, which the flat list output would not allow. When every branch of an <c>anyOf</c>/<c>oneOf</c>
    /// failed, only the best-matching branch is entered (see <see cref="ChooseBranch"/>): the others describe what
    /// the value is not (the expression branch's <c>type: string</c> failure on an object with one bad property,
    /// the Key Vault branch's rejection of <c>value</c> on a literal parameter entry) rather than what is wrong.
    /// </remarks>
    public static IReadOnlyList<SchemaFinding> Evaluate(JsonSchema schema, JsonElement instance, JsonNode? instanceRoot)
    {
        ArgumentNullException.ThrowIfNull(schema);

        var options = new EvaluationOptions
        {
            OutputFormat = OutputFormat.Hierarchical,
            IncludeApplicatorErrors = false,
        };

        var results = schema.Evaluate(instance, options);
        if (results.IsValid)
        {
            return [];
        }

        var candidates = new List<SchemaFinding>();
        Walk(results, instanceRoot, candidates);

        // One finding per location: lowest rank wins, then the shortest evaluation path (closest to the root).
        var best = new Dictionary<JsonPath, SchemaFinding>();
        foreach (var candidate in candidates)
        {
            if (!best.TryGetValue(candidate.Path, out var current) || Rank(candidate) < Rank(current)
                || (Rank(candidate) == Rank(current) && candidate.EvaluationPath.Length < current.EvaluationPath.Length))
            {
                best[candidate.Path] = candidate;
            }
        }

        var list = new List<SchemaFinding>(best.Values);
        list.Sort(static (a, b) => string.CompareOrdinal(a.Path.ToJsonPointer(), b.Path.ToJsonPointer()));
        return list;
    }

    private static void Walk(EvaluationResults node, JsonNode? instanceRoot, List<SchemaFinding> findings)
    {
        if (node.IsValid)
        {
            return;
        }

        bool hasOwnErrors = false;
        if (node.Errors is { Count: > 0 } errors)
        {
            hasOwnErrors = true;
            var path = JsonPath.FromJsonPointer(node.InstanceLocation.ToString(), instanceRoot);
            string evaluationPath = node.EvaluationPath.ToString();
            foreach (var error in errors)
            {
                findings.Add(new SchemaFinding(path, error.Key, error.Value, evaluationPath));
            }
        }

        if (node.Details is not { Count: > 0 } details)
        {
            return;
        }

        // JsonSchema.Net emits no node for the anyOf/oneOf keyword itself: the branches are details of the schema
        // node, at <this evaluation path>/anyOf/<n>, next to ordinary keyword nodes (properties/x, items, allOf/n)
        // which are walked as they are. A failed branch is listed even when the group passed, so a group is only
        // considered when every branch failed.
        string parentPath = node.EvaluationPath.ToString();
        List<EvaluationResults>? anyOfBranches = null;
        List<EvaluationResults>? oneOfBranches = null;
        foreach (var detail in details)
        {
            switch (BranchKeyword(parentPath, detail))
            {
                case "anyOf":
                    (anyOfBranches ??= []).Add(detail);
                    break;
                case "oneOf":
                    (oneOfBranches ??= []).Add(detail);
                    break;
                default:
                    Walk(detail, instanceRoot, findings);
                    break;
            }
        }

        if (anyOfBranches is not null && ChooseBranch(anyOfBranches, instanceRoot) is { } bestAnyOf)
        {
            findings.AddRange(bestAnyOf);
        }

        // A oneOf that matched more than once is reported on this node (keyword 'oneOf'); its branches are not the finding.
        if (oneOfBranches is not null && !(hasOwnErrors && node.Errors!.ContainsKey("oneOf")) && ChooseBranch(oneOfBranches, instanceRoot) is { } bestOneOf)
        {
            findings.AddRange(bestOneOf);
        }
    }

    /// <summary>
    /// <c>anyOf</c>/<c>oneOf</c> when <paramref name="detail"/> is a branch of that keyword of the node evaluated at
    /// <paramref name="parentPath"/> (its evaluation path is <c>&lt;parentPath&gt;/anyOf/&lt;n&gt;</c>), otherwise <c>null</c>.
    /// </summary>
    private static string? BranchKeyword(string parentPath, EvaluationResults detail)
    {
        string path = detail.EvaluationPath.ToString();
        if (path.Length <= parentPath.Length || !path.StartsWith(parentPath, StringComparison.Ordinal) || path[parentPath.Length] != '/')
        {
            return null;
        }

        string relative = path[(parentPath.Length + 1)..];
        int slash = relative.IndexOf('/');
        if (slash < 0)
        {
            return null;
        }

        string keyword = relative[..slash];
        string index = relative[(slash + 1)..];
        if (keyword is not ("anyOf" or "oneOf") || index.Length == 0)
        {
            return null;
        }

        foreach (char c in index)
        {
            if (!char.IsAsciiDigit(c))
            {
                return null;
            }
        }

        return keyword;
    }

    /// <summary>
    /// For a group in which every branch failed, walks each branch separately and keeps the findings of the branch
    /// that matched the value best: the one that failed deepest in the instance, then the one whose shallowest
    /// failure is deepest, then the first declared (the schemas list the typed branch before the expression
    /// branch). A branch whose only failure is the node itself (the expression branch's <c>type: string</c> on an
    /// object, the Key Vault branch's missing <c>reference</c> on a literal entry) therefore loses to a branch that
    /// got into the value. <c>null</c> when a branch passed (the group is not a failure) or none produced a finding.
    /// </summary>
    private static List<SchemaFinding>? ChooseBranch(List<EvaluationResults> branches, JsonNode? instanceRoot)
    {
        foreach (var branch in branches)
        {
            if (branch.IsValid)
            {
                return null;
            }
        }

        List<SchemaFinding>? best = null;
        int bestDeepest = -1;
        int bestShallowest = -1;
        foreach (var branch in branches)
        {
            var candidate = new List<SchemaFinding>();
            Walk(branch, instanceRoot, candidate);
            if (candidate.Count == 0)
            {
                continue;
            }

            int deepest = int.MinValue;
            int shallowest = int.MaxValue;
            foreach (var finding in candidate)
            {
                deepest = Math.Max(deepest, finding.Path.Depth);
                shallowest = Math.Min(shallowest, finding.Path.Depth);
            }

            if (best is null || deepest > bestDeepest || (deepest == bestDeepest && shallowest > bestShallowest))
            {
                best = candidate;
                bestDeepest = deepest;
                bestShallowest = shallowest;
            }
        }

        return best;
    }

    private static int Rank(SchemaFinding finding)
    {
        int rank = SpecificKeywords.Contains(finding.Keyword) ? 0 : 2;
        if (finding.EvaluationPath.Contains("/anyOf/1", StringComparison.Ordinal))
        {
            // The expression branch of anyOf [typed, expression]: the typed branch's message is the useful one.
            rank += 1;
        }

        return rank;
    }
}
