using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Az;
using EArm.Core.Json;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Expressions.Values;

namespace EArm.Providers;

/// <summary>
/// The action-backed <c>assignedLicenses</c> property (plan sections 1.4 and 3.2): <c>[{ skuId | skuPartNumber,
/// disabledPlans }]</c> is reconciled against the live direct assignments with
/// <c>POST /{groups|users}/{id}/assignLicense { addLicenses, removeLicenses }</c>; <c>skuPartNumber</c> is resolved
/// through <c>GET /subscribedSkus</c>, read once per run (per provider instance). Written for groups, shaped so the
/// users provider (WI-13) can reuse the diff and the SKU table.
/// </summary>
internal sealed class GroupsLicensing
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SkuTable? _skus;

    /// <summary><c>skuPartNumber</c> ↔ <c>skuId</c> from <c>/subscribedSkus</c>.</summary>
    public sealed class SkuTable
    {
        private readonly Dictionary<string, string> _idByPartNumber = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _partNumberById = new(StringComparer.OrdinalIgnoreCase);

        public static SkuTable Empty { get; } = new();

        public int Count => _idByPartNumber.Count;

        public void Add(string skuId, string skuPartNumber)
        {
            ArgumentNullException.ThrowIfNull(skuId);
            ArgumentNullException.ThrowIfNull(skuPartNumber);
            _idByPartNumber[skuPartNumber] = skuId;
            _partNumberById[skuId] = skuPartNumber;
        }

        public string? IdOf(string? skuPartNumber)
        {
            return skuPartNumber is not null && _idByPartNumber.TryGetValue(skuPartNumber, out var id) ? id : null;
        }

        public string? PartNumberOf(string? skuId)
        {
            return skuId is not null && _partNumberById.TryGetValue(skuId, out var part) ? part : null;
        }
    }

    /// <summary>One license entry (desired or live) in canonical form.</summary>
    public sealed record LicenseEntry(string? SkuId, string? SkuPartNumber, IReadOnlySet<string> DisabledPlans, JsonObject Source)
    {
        public string Key => SkuId is not null ? "id:" + SkuId.ToLowerInvariant() : "part:" + (SkuPartNumber ?? string.Empty).ToLowerInvariant();

        public string Describe()
        {
            return SkuPartNumber ?? SkuId ?? "?";
        }
    }

    /// <summary>The reconciliation: entries to add (or re-add with new disabled plans), SKU ids to remove, the what-if children and validation errors.</summary>
    public sealed record LicenseDiff(IReadOnlyList<LicenseEntry> Adds, IReadOnlyList<string> Removes, IReadOnlyList<PropertyDelta> Children, IReadOnlyList<string> Errors, bool HasUnknown)
    {
        public bool HasChanges => Adds.Count > 0 || Removes.Count > 0;
    }

    /// <summary>The SKU table when a read already loaded it this run; <c>null</c> otherwise (no Graph call).</summary>
    public SkuTable? Cached => _skus;

    /// <summary>The SKU table, read once; <c>null</c> when the read failed (callers decide whether that is fatal).</summary>
    public async Task<SkuTable?> TryGetSkusAsync(IGraphClient graph, GraphApi api, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_skus is not null)
            {
                return _skus;
            }

            var table = new SkuTable();
            try
            {
                var request = new GraphRequest("/subscribedSkus", api).WithSelect("skuId", "skuPartNumber", "prepaidUnits", "consumedUnits");
                await foreach (var item in graph.GetAllPagesAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    if (item is JsonObject sku && JsonObjects.GetString(sku, "skuId") is { } id && JsonObjects.GetString(sku, "skuPartNumber") is { } part)
                    {
                        table.Add(id, part);
                    }
                }
            }
            catch (GraphException)
            {
                return null;
            }
            catch (AzCliException)
            {
                return null;
            }

            _skus = table;
            return table;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Adds <c>skuPartNumber</c> to the live <c>assignedLicenses</c> entries so the diff can match part numbers; best effort.</summary>
    public async Task EnrichAsync(JsonObject live, IGraphClient graph, GraphApi api, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (!JsonObjects.TryGetIgnoreCase(live, "assignedLicenses", out var node) || node is not JsonArray licenses || licenses.Count == 0)
        {
            return;
        }

        var skus = await TryGetSkusAsync(graph, api, cancellationToken).ConfigureAwait(false);
        if (skus is null)
        {
            return;
        }

        foreach (var item in licenses)
        {
            if (item is JsonObject license && JsonObjects.GetString(license, "skuPartNumber") is null && skus.PartNumberOf(JsonObjects.GetString(license, "skuId")) is { } part)
            {
                license["skuPartNumber"] = part;
            }
        }
    }

    /// <summary>
    /// Compares the declared entries with the live ones (matched by <c>skuId</c>, else by <c>skuPartNumber</c>):
    /// missing entries are added, entries with a different <c>disabledPlans</c> set are re-added with the new set,
    /// live entries not declared are removed. A declared entry without <c>skuId</c> / <c>skuPartNumber</c>, or a
    /// <c>skuPartNumber</c> the tenant has no subscription for, is an error.
    /// </summary>
    public static LicenseDiff Diff(JsonArray? desired, JsonArray? live, SkuTable? skus, JsonPath? basePath = null)
    {
        var errors = new List<string>();
        var children = new List<PropertyDelta>();
        var adds = new List<LicenseEntry>();
        var removes = new List<string>();
        bool unknown = false;
        var path = basePath ?? JsonPath.Of("properties", "assignedLicenses");

        var liveEntries = Parse(live, skus, errors, isDesired: false).ToDictionary(e => e.Key, e => e, StringComparer.Ordinal);
        var desiredEntries = new List<LicenseEntry>();
        if (desired is not null)
        {
            for (int i = 0; i < desired.Count; i++)
            {
                if (UnknownJson.ContainsUnknown(desired[i]))
                {
                    unknown = true;
                    children.Add(new PropertyDelta(path.Append(i), PropertyChangeType.Create, null, JsonDiff.Render(desired[i]), true));
                    continue;
                }

                if (desired[i] is not JsonObject obj)
                {
                    errors.Add($"assignedLicenses[{i}] must be an object with skuId or skuPartNumber.");
                    continue;
                }

                var entry = ToEntry(obj, skus);
                if (entry.SkuId is null && entry.SkuPartNumber is null)
                {
                    errors.Add($"assignedLicenses[{i}] needs skuId or skuPartNumber.");
                    continue;
                }

                if (entry.SkuId is null && skus is { Count: > 0 } && skus.IdOf(entry.SkuPartNumber) is null)
                {
                    errors.Add($"assignedLicenses[{i}]: the tenant has no subscription with skuPartNumber '{entry.SkuPartNumber}'.");
                    continue;
                }

                desiredEntries.Add(entry);
            }
        }

        var matched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in desiredEntries)
        {
            var match = liveEntries.Values.FirstOrDefault(l => SameSku(l, entry));
            if (match is null)
            {
                adds.Add(entry);
                children.Add(new PropertyDelta(path, PropertyChangeType.Create, null, Render(entry)));
                continue;
            }

            matched.Add(match.Key);
            if (!match.DisabledPlans.SetEquals(entry.DisabledPlans))
            {
                adds.Add(entry with { SkuId = entry.SkuId ?? match.SkuId });
                children.Add(new PropertyDelta(path, PropertyChangeType.Modify, Render(match), Render(entry)));
            }
        }

        foreach (var (key, entry) in liveEntries)
        {
            if (!matched.Contains(key) && entry.SkuId is not null)
            {
                removes.Add(entry.SkuId);
                children.Add(new PropertyDelta(path, PropertyChangeType.Delete, Render(entry), null));
            }
        }

        var indexed = new List<PropertyDelta>(children.Count);
        for (int i = 0; i < children.Count; i++)
        {
            indexed.Add(children[i] with { Path = JsonPath.Root.Append(i) });
        }

        return new LicenseDiff(adds, removes, indexed, errors, unknown);
    }

    /// <summary>Applies a diff with <c>POST {entityPath}/assignLicense</c>; a no-op when nothing changes.</summary>
    public async Task ApplyAsync(IGraphClient graph, GraphApi api, string entityPath, JsonArray? desired, JsonArray? live, RetryHint hint, string describe, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(entityPath);
        ArgumentNullException.ThrowIfNull(describe);

        if (desired is null && (live is null || live.Count == 0))
        {
            return;
        }

        if (UnknownJson.ContainsUnknown(desired))
        {
            throw new ProviderException(EngineDiagnosticCodes.UnresolvedDeployTimeValue, $"{describe}: assignedLicenses depends on values that are not available at this point of the deployment.");
        }

        SkuTable? skus = null;
        bool needsTable = desired is not null && desired.Any(d => d is JsonObject o && JsonObjects.GetString(o, "skuId") is null);
        if (needsTable)
        {
            skus = await TryGetSkusAsync(graph, api, cancellationToken).ConfigureAwait(false)
                ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{describe}: skuPartNumber values cannot be resolved because GET /subscribedSkus failed (Organization.Read.All is required).");
        }

        var diff = Diff(desired, live, skus);
        if (diff.Errors.Count > 0)
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{describe}: {string.Join(" ", diff.Errors)}");
        }

        if (!diff.HasChanges)
        {
            return;
        }

        var body = new JsonObject
        {
            ["addLicenses"] = new JsonArray([.. diff.Adds.Select(a => (JsonNode)AddBody(a))]),
            ["removeLicenses"] = new JsonArray([.. diff.Removes.Select(r => (JsonNode)JsonValue.Create(r))]),
        };
        await graph.PostAsync(new GraphRequest(entityPath.TrimEnd('/') + "/assignLicense", api, hint: hint), body, cancellationToken).ConfigureAwait(false);
    }

    // ---- helpers ----

    private static IEnumerable<LicenseEntry> Parse(JsonArray? array, SkuTable? skus, List<string> errors, bool isDesired)
    {
        if (array is null)
        {
            yield break;
        }

        for (int i = 0; i < array.Count; i++)
        {
            if (array[i] is JsonObject obj)
            {
                yield return ToEntry(obj, skus);
            }
            else if (isDesired)
            {
                errors.Add($"assignedLicenses[{i}] must be an object.");
            }
        }
    }

    private static LicenseEntry ToEntry(JsonObject obj, SkuTable? skus)
    {
        string? skuId = JsonObjects.GetString(obj, "skuId");
        string? part = JsonObjects.GetString(obj, "skuPartNumber");
        skuId ??= skus?.IdOf(part);
        part ??= skus?.PartNumberOf(skuId);
        var plans = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (JsonObjects.TryGetIgnoreCase(obj, "disabledPlans", out var node) && node is JsonArray disabled)
        {
            foreach (var plan in disabled)
            {
                if (JsonValues.TryGetString(plan, out var text))
                {
                    plans.Add(text);
                }
            }
        }

        return new LicenseEntry(skuId, part, plans, obj);
    }

    private static bool SameSku(LicenseEntry live, LicenseEntry desired)
    {
        if (desired.SkuId is not null && live.SkuId is not null)
        {
            return JsonObjects.KeyEquals(desired.SkuId, live.SkuId);
        }

        return desired.SkuPartNumber is not null && live.SkuPartNumber is not null && desired.SkuPartNumber.Equals(live.SkuPartNumber, StringComparison.OrdinalIgnoreCase);
    }

    private static JsonObject AddBody(LicenseEntry entry)
    {
        return new JsonObject
        {
            ["skuId"] = entry.SkuId ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"assignedLicenses: skuPartNumber '{entry.SkuPartNumber}' could not be resolved to a skuId."),
            ["disabledPlans"] = new JsonArray([.. entry.DisabledPlans.Order(StringComparer.OrdinalIgnoreCase).Select(p => (JsonNode)JsonValue.Create(p))]),
        };
    }

    private static JsonObject Render(LicenseEntry entry)
    {
        var rendered = new JsonObject();
        if (entry.SkuPartNumber is not null)
        {
            rendered["skuPartNumber"] = entry.SkuPartNumber;
        }

        if (entry.SkuId is not null)
        {
            rendered["skuId"] = entry.SkuId;
        }

        rendered["disabledPlans"] = new JsonArray([.. entry.DisabledPlans.Order(StringComparer.OrdinalIgnoreCase).Select(p => (JsonNode)JsonValue.Create(p))]);
        return rendered;
    }
}
