using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Providers;

namespace EArm.Integration.Tests;

/// <summary>
/// The PIM part of the prefix sweep (<see cref="TenantCleanup"/>, only with <c>EARM_IT_ALLOW_PIM=1</c>): before the prefixed principals and groups are
/// deleted, it removes (PIM <c>adminRemove</c> requests) the directory role eligibilities, the time-bound active role assignments and the PIM for Groups
/// eligibilities and time-bound active assignments whose PRINCIPAL is one of the prefixed principals. A schedule on a prefixed group whose principal is
/// not prefixed is never removed (it is only logged; the group's deletion ends it). Permanent active assignments are left to the role assignment and
/// membership cleanup, and activations of an eligibility (<c>assignmentType</c> Activated) are not this guard's (the tests never activate). Failures never
/// throw: they are logged and returned as leftovers.
/// </summary>
internal sealed class PimCleanup
{
    private const string Justification = "EARM integration test cleanup";

    private static readonly TimeSpan VerifyWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly IGraphClient _graph;
    private readonly List<string> _log = [];
    private readonly List<string> _leftovers = [];
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(Kind Kind, string Filter, string ScheduleId, string Label)> _removed = [];

    public PimCleanup(IGraphClient graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        _graph = graph;
    }

    /// <summary>One schedule collection the guard covers.</summary>
    private sealed record Kind(string Label, string SchedulesPath, string RequestsPath, bool IsGroup, Func<JsonObject, bool> Keep);

    private static readonly Kind[] Kinds =
    [
        new("role eligibility", RoleSchedulesLookup.EligibilitySchedulesPath, RoleSchedulesLookup.EligibilityRequestsPath, IsGroup: false, static _ => true),
        new("time-bound active role assignment", RoleSchedulesLookup.AssignmentSchedulesPath, RoleSchedulesLookup.AssignmentRequestsPath, IsGroup: false, IsTimeBoundAssigned),
        new("group eligibility", GroupSchedulesLookup.EligibilitySchedulesPath, GroupSchedulesLookup.EligibilityRequestsPath, IsGroup: true, static _ => true),
        new("time-bound active group assignment", GroupSchedulesLookup.AssignmentSchedulesPath, GroupSchedulesLookup.AssignmentRequestsPath, IsGroup: true, IsTimeBoundAssigned),
    ];

    /// <summary>What the guard did, one line per action.</summary>
    public IReadOnlyList<string> Entries => _log;

    /// <summary>Schedules of prefixed principals that could not be removed.</summary>
    public IReadOnlyList<string> Leftovers => _leftovers;

    /// <summary>
    /// Removes the PIM schedules of the prefixed principals (<paramref name="principals"/>: id → name) and logs the schedules of the prefixed groups
    /// (<paramref name="groups"/>: id → name) that belong to other principals.
    /// </summary>
    public async Task RemoveAsync(IReadOnlyDictionary<string, string> principals, IReadOnlyDictionary<string, string> groups, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principals);
        ArgumentNullException.ThrowIfNull(groups);

        foreach (var (principalId, name) in principals)
        {
            foreach (var kind in Kinds)
            {
                string filter = "principalId eq '" + PimScheduleRequests.Quote(principalId) + "'";
                foreach (var schedule in await ListAsync(kind, filter, $"of '{name}'", cancellationToken).ConfigureAwait(false))
                {
                    await RemoveOneAsync(kind, filter, schedule, name, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        foreach (var (groupId, name) in groups)
        {
            foreach (var kind in Kinds.Where(k => k.IsGroup))
            {
                string filter = "groupId eq '" + PimScheduleRequests.Quote(groupId) + "'";
                foreach (var schedule in await ListAsync(kind, filter, $"on group '{name}'", cancellationToken).ConfigureAwait(false))
                {
                    string? principalId = schedule.Text("principalId");
                    if (principalId is not null && principals.TryGetValue(principalId, out string? principalName))
                    {
                        await RemoveOneAsync(kind, filter, schedule, principalName, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        _log.Add($"pim: NOT removing {kind.Label} {schedule.Text("id")} on group '{name}': its principal {principalId} does not carry the prefix (the group's deletion ends it)");
                    }
                }
            }
        }

        await VerifyAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<JsonObject>> ListAsync(Kind kind, string filter, string what, CancellationToken cancellationToken)
    {
        try
        {
            return await PimScheduleRequests.ListSchedulesAsync(_graph, kind.SchedulesPath, filter, kind.Keep, GraphApi.V1, RetryHint.Default, cancellationToken).ConfigureAwait(false);
        }
        catch (GraphException ex)
        {
            // An unlicensed tenant, or a group PIM never onboarded, can refuse the read; the principal or group deletion still ends any schedule.
            _log.Add($"pim: {kind.Label}s {what} could not be listed ({ProbeReport.Describe(ex)}); they end with the deletion of the principal or group");
            return [];
        }
    }

    private async Task RemoveOneAsync(Kind kind, string filter, JsonObject schedule, string principalName, CancellationToken cancellationToken)
    {
        string scheduleId = schedule.Text("id") ?? "(no id)";
        if (!_seen.Add(kind.Label + "|" + scheduleId))
        {
            return;
        }

        JsonObject body;
        string label;
        if (kind.IsGroup)
        {
            var tuple = new GroupScheduleTuple(schedule.Text("groupId") ?? string.Empty, schedule.Text("principalId") ?? string.Empty, schedule.Text("accessId") ?? string.Empty);
            body = GroupSchedulesLookup.RequestBody(PimScheduleRequests.AdminRemove, tuple, PimScheduleComparison.ScheduleView(schedule), JsonValue.Create(Justification), null);
            label = $"{kind.Label} {scheduleId} ({tuple.AccessId} of group {tuple.GroupId}) of '{principalName}'";
        }
        else if (RoleSchedulesLookup.TupleOf(schedule) is { } tuple)
        {
            body = RoleSchedulesLookup.RequestBody(PimScheduleRequests.AdminRemove, tuple, null, JsonValue.Create(Justification), null);
            label = $"{kind.Label} {scheduleId} (role {tuple.RoleDefinitionId} at {tuple.Scope}) of '{principalName}'";
        }
        else
        {
            Leftover($"{kind.Label} {scheduleId} of '{principalName}' has no principalId / roleDefinitionId / scope, so it cannot be removed");
            return;
        }

        try
        {
            await _graph.PostAsync(new GraphRequest(kind.RequestsPath), body, cancellationToken).ConfigureAwait(false);
            _log.Add($"pim: adminRemove accepted for {label}");
            _removed.Add((kind, filter, scheduleId, label));
        }
        catch (GraphException ex) when (PimScheduleRequests.IsAlreadyGone(ex))
        {
            _log.Add($"pim: {label} was already gone ({ProbeReport.Describe(ex)})");
        }
        catch (GraphException ex)
        {
            Leftover($"{label} could not be removed: {ProbeReport.Describe(ex)}");
        }
    }

    /// <summary>Re-lists until every schedule whose adminRemove was accepted is gone (PIM applies requests asynchronously) or the window ends.</summary>
    private async Task VerifyAsync(CancellationToken cancellationToken)
    {
        var pending = _removed.ToList();
        var started = DateTime.UtcNow;
        while (pending.Count > 0)
        {
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var (kind, filter, scheduleId, _) = pending[i];
                try
                {
                    var listed = await PimScheduleRequests.ListSchedulesAsync(_graph, kind.SchedulesPath, filter, _ => true, GraphApi.V1, RetryHint.Default, cancellationToken).ConfigureAwait(false);
                    if (!listed.Any(s => string.Equals(s.Text("id"), scheduleId, StringComparison.OrdinalIgnoreCase)))
                    {
                        pending.RemoveAt(i);
                    }
                }
                catch (GraphException)
                {
                    // Not confirmable right now; retried until the window ends.
                }
            }

            if (pending.Count == 0 || DateTime.UtcNow - started >= VerifyWindow)
            {
                break;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        foreach (var (_, _, _, label) in pending)
        {
            // Not a leftover on its own: the principal is deleted (and purged) next, which ends the schedule; a principal that cannot be deleted is one.
            _log.Add($"pim: {label} is still listed {VerifyWindow.TotalSeconds:F0} s after its adminRemove was accepted; it ends with the principal's deletion");
        }

        if (_removed.Count > 0 && pending.Count < _removed.Count)
        {
            _log.Add($"pim: confirmed {_removed.Count - pending.Count} of {_removed.Count} removed schedule(s) gone");
        }
    }

    private static bool IsTimeBoundAssigned(JsonObject schedule)
    {
        return PimScheduleRequests.IsAssigned(schedule) && !PimScheduleComparison.IsPermanent(schedule);
    }

    private void Leftover(string line)
    {
        _log.Add("LEFTOVER pim: " + line);
        _leftovers.Add(line);
    }
}
