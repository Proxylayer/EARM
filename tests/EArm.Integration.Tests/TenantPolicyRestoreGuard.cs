using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;

namespace EArm.Integration.Tests;

/// <summary>
/// The restore guarantee of the tenant-wide policy scenarios (the tenant default app management policy and the default cross-tenant access
/// configuration): a test snapshots the singleton BEFORE it changes anything (<see cref="SnapshotAsync"/>) and restores it in a <c>finally</c>
/// (<see cref="RestoreAsync"/>). The snapshot is also written to <c>%TEMP%\earm-it\&lt;prefix&gt;\tenant-policies\&lt;name&gt;.json</c> (deleted once
/// restored) for a manual restore after a crash. A restore PATCHes the top-level properties whose live value differs from the snapshot (read-only ones
/// excluded), then re-reads for up to 60 s until every compared property equals the snapshot (arrays as sets, OData annotations ignored, through
/// <see cref="AuthMethodRestoreGuard.Differences"/>).
/// </summary>
internal sealed class TenantPolicyRestoreGuard
{
    private readonly IGraphClient _graph;
    private readonly string _directory;
    private readonly string _path;
    private readonly string _name;
    private readonly IReadOnlySet<string> _readOnly;
    private JsonObject? _snapshot;

    /// <param name="graph">The fixture's Graph client.</param>
    /// <param name="runDirectory">The run's scratch root.</param>
    /// <param name="path">The singleton's Graph path (<c>/policies/defaultAppManagementPolicy</c>).</param>
    /// <param name="name">A file-safe name for the snapshot.</param>
    /// <param name="readOnly">Top-level properties never sent back (read-only on the singleton).</param>
    public TenantPolicyRestoreGuard(IGraphClient graph, string runDirectory, string path, string name, params string[] readOnly)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(runDirectory);
        _graph = graph;
        _directory = Path.Combine(runDirectory, "tenant-policies");
        _path = path;
        _name = name;
        _readOnly = new HashSet<string>(["id", .. readOnly], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Reads the live singleton and keeps it as the snapshot (the first snapshot wins); returns a copy.</summary>
    public async Task<JsonObject> SnapshotAsync(CancellationToken cancellationToken)
    {
        if (_snapshot is null)
        {
            _snapshot = await _graph.GetObjectAsync(new GraphRequest(_path), cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"{_path} could not be read; nothing can be snapshotted.");
            Persist();
        }

        return (JsonObject)_snapshot.DeepClone();
    }

    /// <summary>Restores the snapshot (a no-op without one); true when the live object equals it afterwards. <paramref name="log"/> says what happened.</summary>
    public async Task<bool> RestoreAsync(IList<string> log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (_snapshot is null)
        {
            return true;
        }

        try
        {
            var live = await _graph.GetObjectAsync(new GraphRequest(_path), cancellationToken).ConfigureAwait(false);
            var differing = Compared(live is null ? [.. _snapshot.Select(p => p.Key)] : AuthMethodRestoreGuard.Differences(_snapshot, live));
            if (differing.Count > 0)
            {
                var body = new JsonObject();
                foreach (string property in differing)
                {
                    body[property] = AuthMethodRestoreGuard.StripAnnotations(_snapshot[property]);
                }

                await _graph.PatchAsync(new GraphRequest(_path).WithHint(RetryHint.ExpectReplicationLag), body, cancellationToken).ConfigureAwait(false);
                log.Add($"{_name}: restored from the snapshot (PATCH {string.Join(", ", differing)})");
            }

            var deadline = DateTime.UtcNow.AddSeconds(60);
            List<string> remaining;
            while (true)
            {
                var after = await _graph.GetObjectAsync(new GraphRequest(_path), cancellationToken).ConfigureAwait(false);
                remaining = after is null ? ["(not readable)"] : Compared(AuthMethodRestoreGuard.Differences(_snapshot, after));
                if (remaining.Count == 0 || DateTime.UtcNow >= deadline)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }

            if (remaining.Count > 0)
            {
                log.Add($"{_name}: still differs from the snapshot after the restore ({string.Join(", ", remaining)}); snapshot kept at {FileName}");
                return false;
            }

            _snapshot = null;
            TryDelete();
            log.Add(differing.Count == 0 ? $"{_name}: already equals the snapshot" : $"{_name}: verified equal to the snapshot");
            return true;
        }
        catch (GraphException ex)
        {
            log.Add($"{_name}: restoring failed: {ProbeReport.Describe(ex)}; snapshot kept at {FileName}");
            return false;
        }
    }

    private string FileName => Path.Combine(_directory, _name + ".json");

    private List<string> Compared(IEnumerable<string> properties)
    {
        return properties.Where(p => !_readOnly.Contains(p) && !p.Contains("@odata.", StringComparison.Ordinal)).ToList();
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(FileName, _snapshot!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        }
        catch (IOException)
        {
            // The in-memory snapshot still drives the restore; the file only helps after a crash.
        }
    }

    private void TryDelete()
    {
        try
        {
            File.Delete(FileName);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
