using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core.Validation;
using EArm.Graph;

namespace EArm.Integration.Tests;

/// <summary>
/// The restore guarantee of the authentication methods scenarios and probes (<c>EARM_IT_ALLOW_AUTH_METHODS=1</c>): every test snapshots the live
/// configuration of a method BEFORE it changes anything (<see cref="SnapshotAsync"/>) and restores it in a <c>finally</c> (<see cref="RestoreAsync"/>);
/// the fixture restores whatever is still pending at teardown (<see cref="RestoreAllAsync"/>). The first snapshot of a method wins until it is restored,
/// so a second snapshot taken after a change never replaces the original. Each snapshot is also written to
/// <c>%TEMP%\earm-it\&lt;prefix&gt;\auth-methods\&lt;Method&gt;.json</c> (deleted once restored), for a manual restore after a crash.
/// <para>
/// A restore PATCHes (with the method's <c>@odata.type</c>) only the top-level properties whose live value differs from the snapshot, then re-reads the
/// configuration and compares every top-level property with the snapshot (OData annotations ignored, arrays compared as sets). Anything that still
/// differs is reported and keeps the snapshot pending.
/// </para>
/// </summary>
internal sealed class AuthMethodRestoreGuard
{
    internal const string CollectionPath = "/policies/authenticationMethodsPolicy/authenticationMethodConfigurations";

    private readonly IGraphClient _graph;
    private readonly string _directory;
    private readonly ConcurrentDictionary<string, JsonObject> _snapshots = new(StringComparer.OrdinalIgnoreCase);

    public AuthMethodRestoreGuard(IGraphClient graph, string runDirectory)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(runDirectory);
        _graph = graph;
        _directory = Path.Combine(runDirectory, "auth-methods");
    }

    /// <summary>The methods (Graph ids) whose snapshot has not been restored yet.</summary>
    public IReadOnlyList<string> Pending => [.. _snapshots.Keys.Order(StringComparer.Ordinal)];

    /// <summary>The method's table entry (name, Graph id, <c>@odata.type</c>).</summary>
    public static AuthenticationMethod MethodOf(string name)
    {
        return AuthenticationMethodConfigurationRules.Find(name) ?? throw new ArgumentException($"'{name}' is not an authentication method EARM manages.", nameof(name));
    }

    /// <summary>The entity path of a method (<c>…/authenticationMethodConfigurations/{GraphId}</c>).</summary>
    public static string EntityPath(AuthenticationMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        return CollectionPath + "/" + GraphPath.Segment(method.GraphId);
    }

    /// <summary>Reads the live configuration and keeps it as the method's snapshot (unless one is already pending); returns the pending snapshot (a copy).</summary>
    public async Task<JsonObject> SnapshotAsync(string name, CancellationToken cancellationToken)
    {
        var method = MethodOf(name);
        if (_snapshots.TryGetValue(method.GraphId, out var existing))
        {
            return (JsonObject)existing.DeepClone();
        }

        var live = await _graph.GetObjectAsync(new GraphRequest(EntityPath(method)), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"The {method.Name} authentication method configuration does not exist; nothing can be snapshotted.");
        var snapshot = _snapshots.GetOrAdd(method.GraphId, live);
        Persist(method, snapshot);
        return (JsonObject)snapshot.DeepClone();
    }

    /// <summary>
    /// Restores one method's pending snapshot (a no-op when none is pending). Returns true when the live configuration equals the snapshot afterwards; the
    /// lines of <paramref name="log"/> say what was sent and what still differs.
    /// </summary>
    public async Task<bool> RestoreAsync(string name, IList<string> log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(log);
        var method = MethodOf(name);
        if (!_snapshots.TryGetValue(method.GraphId, out var snapshot))
        {
            return true;
        }

        string path = EntityPath(method);
        try
        {
            var live = await _graph.GetObjectAsync(new GraphRequest(path), cancellationToken).ConfigureAwait(false);
            var differing = live is null ? Comparable(snapshot).Select(p => p.Key).ToList() : Differences(snapshot, live);
            if (differing.Count > 0)
            {
                var body = new JsonObject { ["@odata.type"] = method.ODataType };
                foreach (string property in differing)
                {
                    body[property] = StripAnnotations(snapshot[property]);
                }

                await _graph.PatchAsync(new GraphRequest(path).WithHint(RetryHint.ExpectReplicationLag), body, cancellationToken).ConfigureAwait(false);
                log.Add($"auth methods: restored {method.Name} from the snapshot (PATCH {string.Join(", ", differing)})");
            }

            // The policy is eventually consistent: re-read until the snapshot is visible or the time is up.
            var deadline = DateTime.UtcNow.AddSeconds(60);
            List<string> remaining;
            while (true)
            {
                var after = await _graph.GetObjectAsync(new GraphRequest(path), cancellationToken).ConfigureAwait(false);
                remaining = after is null ? ["(configuration not readable)"] : Differences(snapshot, after);
                if (remaining.Count == 0 || DateTime.UtcNow >= deadline)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }

            if (remaining.Count > 0)
            {
                log.Add($"auth methods: {method.Name} still differs from the snapshot after the restore ({string.Join(", ", remaining)}); snapshot kept at {FileOf(method)}");
                return false;
            }

            _snapshots.TryRemove(method.GraphId, out _);
            TryDelete(method);
            log.Add(differing.Count == 0
                ? $"auth methods: {method.Name} already equals the snapshot; nothing to restore"
                : $"auth methods: verified {method.Name} equals the snapshot");
            return true;
        }
        catch (GraphException ex)
        {
            log.Add($"auth methods: restoring {method.Name} failed: {ProbeReport.Describe(ex)}; snapshot kept at {FileOf(method)}");
            return false;
        }
    }

    /// <summary>Restores every pending snapshot; returns one leftover line per method that could not be restored.</summary>
    public async Task<IReadOnlyList<string>> RestoreAllAsync(IList<string> log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(log);
        var leftovers = new List<string>();
        foreach (string graphId in Pending)
        {
            if (!await RestoreAsync(graphId, log, cancellationToken).ConfigureAwait(false))
            {
                leftovers.Add($"authentication method configuration {graphId} could not be restored to its snapshot; PATCH it back from {FileOf(MethodOf(graphId))} (keep @odata.type)");
            }
        }

        return leftovers;
    }

    /// <summary>
    /// The top-level properties of the snapshot (OData annotations and the id left out) whose live values differ. A property only the live configuration
    /// carries is not in the snapshot, so it cannot be restored and is not compared.
    /// </summary>
    public static List<string> Differences(JsonObject snapshot, JsonObject live)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(live);
        var actual = Comparable(live).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        return Comparable(snapshot)
            .Where(p => !string.Equals(Canonical(p.Value), Canonical(actual.GetValueOrDefault(p.Key)), StringComparison.Ordinal))
            .Select(p => p.Key)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>A canonical text of a JSON value: object members sorted, arrays sorted (compared as sets), OData annotations (except <c>@odata.type</c>) dropped.</summary>
    public static string Canonical(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return "null";
            case JsonObject obj:
                return "{" + string.Join(",", obj
                    .Where(p => !IsAnnotation(p.Key))
                    .OrderBy(p => p.Key, StringComparer.Ordinal)
                    .Select(p => JsonSerializer.Serialize(p.Key) + ":" + Canonical(p.Value))) + "}";
            case JsonArray array:
                return "[" + string.Join(",", array.Select(Canonical).Order(StringComparer.Ordinal)) + "]";
            default:
                return node.ToJsonString();
        }
    }

    /// <summary>A copy of a value without the OData annotations Graph adds on read (<c>@odata.context</c>, <c>x@odata.context</c>); <c>@odata.type</c> is kept.</summary>
    public static JsonNode? StripAnnotations(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var copy = new JsonObject();
                foreach (var pair in obj.Where(p => !IsAnnotation(p.Key)))
                {
                    copy[pair.Key] = StripAnnotations(pair.Value);
                }

                return copy;
            case JsonArray array:
                return new JsonArray(array.Select(StripAnnotations).ToArray());
            default:
                return node?.DeepClone();
        }
    }

    private static IEnumerable<KeyValuePair<string, JsonNode?>> Comparable(JsonObject configuration)
    {
        return configuration.Where(p => !IsAnnotation(p.Key) && !p.Key.Equals("@odata.type", StringComparison.Ordinal) && !p.Key.Equals("id", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsAnnotation(string key)
    {
        return key.Contains("@odata.", StringComparison.Ordinal) && !key.Equals("@odata.type", StringComparison.Ordinal);
    }

    private string FileOf(AuthenticationMethod method)
    {
        return Path.Combine(_directory, method.GraphId + ".json");
    }

    private void Persist(AuthenticationMethod method, JsonObject snapshot)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(FileOf(method), snapshot.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        }
        catch (IOException)
        {
            // The in-memory snapshot still drives the restore; the file only helps after a crash.
        }
    }

    private void TryDelete(AuthenticationMethod method)
    {
        try
        {
            File.Delete(FileOf(method));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
