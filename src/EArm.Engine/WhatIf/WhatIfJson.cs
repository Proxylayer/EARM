using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Engine.Planning;
using EArm.Engine.Providers;

namespace EArm.Engine.WhatIf;

/// <summary>
/// The ARM-shaped what-if JSON: <c>status</c>, <c>deploymentName</c>, <c>tenantId</c>,
/// <c>mode</c>, <c>changes[]</c> (<c>resourceId</c>, <c>changeType</c> (<c>Unsupported</c> when the row has an <c>unsupportedReason</c>; <c>Deploy</c> for an existing
/// declared resource with <c>ResourceIdOnly</c>), <c>before</c>, <c>after</c>, <c>delta[]</c>
/// with <c>path</c> / <c>propertyChangeType</c> / <c>before</c> / <c>after</c> / <c>children</c> / <c>note</c> (EARM: why a declared value is held back;
/// a <c>NoChange</c> row carries a <c>delta</c> only for such noted deltas),
/// <c>unsupportedReason</c>) and <c>diagnostics[]</c>, plus EARM's extensions: <c>reasons[]</c> per change,
/// <c>orphan</c> (<c>undeclared</c> / <c>retained</c>) and <c>graphId</c> on the <c>Ignore</c> rows of the orphan report,
/// <c>summary</c> (with <c>orphans</c> and <c>skipped</c>), <c>requiredPermissions[]</c>, <c>unresolvedReferences[]</c> and <c>skipped[]</c> (the resources whose
/// condition is false, which ARM does not list among the changes). <c>changes[]</c> and <c>summary</c> describe the rows a <see cref="WhatIfView"/> shows
/// (<c>--exclude-change-types</c> applied). Secure values are <c>***</c> and
/// deploy-time values <c>(deploy-time)</c>; <c>ResourceIdOnly</c> drops <c>before</c>, <c>after</c> and
/// <c>delta</c>. Member order is fixed so the document diffs cleanly.
/// </summary>
public static class WhatIfJson
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, MaxDepth = 256, NewLine = "\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false, MaxDepth = 256, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// The document as text: indented or on one line (<c>--no-pretty-print</c>), with quotes and non-ASCII written
    /// raw (the output is for terminals and pipes, as with the Azure CLI), so the same call produces the CLI output and
    /// the golden fixtures. Pretty output uses LF line endings on every OS (not <see cref="Environment.NewLine"/>), as
    /// the fixtures and docs/whatif.md specify.
    /// </summary>
    public static string Serialize(JsonObject document, bool prettyPrint = true)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.ToJsonString(prettyPrint ? Pretty : Compact);
    }

    public static JsonObject Build(DeploymentPlan plan, ResultFormat format = ResultFormat.FullResourcePayloads)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Build(WhatIfReport.From(plan), format);
    }

    public static JsonObject Build(WhatIfReport report, ResultFormat format = ResultFormat.FullResourcePayloads)
    {
        ArgumentNullException.ThrowIfNull(report);
        return Build(WhatIfView.Of(report, format));
    }

    public static JsonObject Build(WhatIfView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var report = view.Report;
        var format = view.Format;

        var changes = new JsonArray();
        foreach (var row in view.Rows)
        {
            changes.Add(BuildChange(row, format));
        }

        var skipped = new JsonArray();
        foreach (var row in report.Skipped)
        {
            skipped.Add(new JsonObject
            {
                ["resourceId"] = row.ResourceId,
                ["reason"] = ResourceChange.ConditionFalseReason,
            });
        }

        var diagnostics = new JsonArray();
        foreach (var diagnostic in report.Diagnostics)
        {
            diagnostics.Add(BuildDiagnostic(diagnostic));
        }

        var unresolvedReferences = new JsonArray();
        foreach (var unresolved in report.UnresolvedReferences)
        {
            diagnostics.Add(new JsonObject
            {
                ["level"] = DiagnosticSeverity.Warning.ToString(),
                ["code"] = EngineDiagnosticCodes.UnresolvedReference,
                ["target"] = unresolved.Id.Id,
                ["message"] = $"reference(resourceId('{unresolved.Id.Type}', ...), '{unresolved.ApiVersion}') could not be resolved: {unresolved.Reason}",
            });
            unresolvedReferences.Add(new JsonObject
            {
                ["resourceId"] = unresolved.Id.Id,
                ["apiVersion"] = unresolved.ApiVersion,
                ["reason"] = unresolved.Reason,
            });
        }

        var requiredPermissions = new JsonArray();
        foreach (var permission in report.RequiredPermissions)
        {
            requiredPermissions.Add(JsonValue.Create(permission));
        }

        var summary = view.Summary;
        var document = new JsonObject
        {
            ["status"] = "Succeeded",
            ["deploymentName"] = report.DeploymentName,
            ["tenantId"] = report.TenantId,
            ["mode"] = report.Mode.ToString(),
            ["changes"] = changes,
            ["diagnostics"] = diagnostics,
            ["requiredPermissions"] = requiredPermissions,
            ["unresolvedReferences"] = unresolvedReferences,
            ["skipped"] = skipped,
            ["summary"] = new JsonObject
            {
                ["create"] = summary.Create,
                ["modify"] = summary.Modify,
                ["delete"] = summary.Delete,
                ["noChange"] = summary.NoChange,
                ["deploy"] = summary.Deploy,
                ["ignore"] = summary.Ignore,
                ["unsupported"] = summary.Unsupported,
                ["orphans"] = summary.Orphans,
                ["skipped"] = summary.Skipped,
                ["edges"] = new JsonObject
                {
                    ["add"] = view.Edges.Add,
                    ["remove"] = view.Edges.Remove,
                },
            },
        };

        // Only a template with Azure Resource Manager types carries the key (next to requiredPermissions), so Graph-only documents keep their shape.
        if (report.RequiredAzureRoles.Count > 0)
        {
            document.Insert(document.IndexOf("requiredPermissions") + 1, "requiredAzureRoles", new JsonArray([.. report.RequiredAzureRoles.Select(r => (JsonNode?)JsonValue.Create(r))]));
        }

        return document;
    }

    public static JsonObject BuildChange(WhatIfRow row, ResultFormat format)
    {
        ArgumentNullException.ThrowIfNull(row);
        var json = new JsonObject
        {
            ["resourceId"] = row.ResourceId,
            // ARM: a change that cannot be applied as planned is "Unsupported" (with its unsupportedReason); the payloads below still follow the planned type.
            // ResourceIdOnly reports an existing declared resource as "Deploy".
            ["changeType"] = row.DisplayType(format).ToString(),
        };

        if (row.DeploymentPath is { } deployment)
        {
            // EARM: the nested deployments that deploy the resource (ARM lists such resources as ordinary changes).
            json["deployment"] = deployment;
        }

        if (format == ResultFormat.FullResourcePayloads)
        {
            // ARM: Create carries after, Delete carries before, Modify and NoChange carry both; only Modify has a delta.
            if (row.Before is not null && row.Type is ChangeType.Modify or ChangeType.Delete or ChangeType.NoChange)
            {
                json["before"] = row.Before.DeepClone();
            }

            if (row.After is not null && row.Type is ChangeType.Create or ChangeType.Modify or ChangeType.NoChange)
            {
                json["after"] = row.After.DeepClone();
            }

            if (row.Type == ChangeType.Modify)
            {
                json["delta"] = BuildDeltas(row.Deltas, JsonPath.Root);
            }
            else if (row.Type == ChangeType.NoChange && row.Deltas.Any(d => d.Note is not null))
            {
                // A NoChange row carries only the deltas a provider held back with a note (why a declared value is not applied).
                json["delta"] = BuildDeltas(row.Deltas.Where(d => d.Note is not null).ToList(), JsonPath.Root);
            }
        }

        if (row.Change.Orphan is { } orphan)
        {
            // The orphan report (never deleted): why it is listed, and the live object's id.
            json["orphan"] = orphan == OrphanReason.Retained ? "retained" : "undeclared";
            json["graphId"] = row.Change.GraphId;
        }

        json["unsupportedReason"] = row.UnsupportedReason;
        var reasons = row.Change.Reasons;
        if (reasons.Count > 0)
        {
            var array = new JsonArray();
            foreach (var reason in reasons)
            {
                array.Add(JsonValue.Create(reason));
            }

            json["reasons"] = array;
        }

        return json;
    }

    /// <summary>Top-level paths are rooted at the resource (<c>properties.web.redirectUris</c>); child paths are relative to their parent (<c>[0]</c>, <c>['id']</c>, <c>isEnabled</c>).</summary>
    private static JsonArray BuildDeltas(IReadOnlyList<PropertyDelta> deltas, JsonPath parent)
    {
        var array = new JsonArray();
        foreach (var delta in deltas)
        {
            var item = new JsonObject
            {
                ["path"] = WhatIfPaths.Relative(delta.Path, parent),
                ["propertyChangeType"] = delta.Kind.ToString(),
            };

            if (delta.Before is not null)
            {
                item["before"] = delta.Before.DeepClone();
            }

            if (delta.After is not null)
            {
                item["after"] = delta.After.DeepClone();
            }

            if (delta.Children is { Count: > 0 } children)
            {
                item["children"] = BuildDeltas(children, delta.Path);
            }

            if (delta.Note is { } note)
            {
                item["note"] = note;
            }

            array.Add(item);
        }

        return array;
    }

    private static JsonObject BuildDiagnostic(Diagnostic diagnostic)
    {
        return new JsonObject
        {
            ["level"] = diagnostic.Severity.ToString(),
            ["code"] = diagnostic.Code,
            ["target"] = diagnostic.Path?.ToString(),
            ["message"] = diagnostic.Message,
        };
    }
}
