using System;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Providers;
using EArm.Graph;

namespace EArm.Providers;

/// <summary>
/// Onboards a group in PIM for Groups for a <c>roleManagementPolicies</c> declaration that opts in (<c>properties.onboarding</c>): PIM onboards a group
/// only on its first eligibility or assignment ("You can't explicitly onboard a group"; Learn: privilegedidentitymanagement-for-groups-api-overview), and
/// Graph lists no policy for the group before that (real tenant, probe PP7). The steps, all in one run:
/// <list type="number">
/// <item>an <c>adminAssign</c> member eligibility for the named principal, expiring after <see cref="TemporaryDuration"/>, with the justification
/// "EARM onboarding of &lt;group&gt; for policy management";</item>
/// <item>the policy assignment of the scope is read every <see cref="ReplicationWait.Interval"/> until Graph lists the (post-onboarding) policy, for up to
/// <see cref="ListingWindow"/> (PP7: about one second). A policy id read before onboarding is never used: the caller applies the rules to the policy read here;</item>
/// <item>the eligibility is removed (<c>adminRemove</c>); an <c>ActiveDurationTooShort</c> answer is repeated every <see cref="RemovalInterval"/> for up to
/// <see cref="RemovalWindow"/> (PP7: accepted five seconds after the assignment). Onboarding persists once the eligibility is gone (PP7).</item>
/// </list>
/// An eligibility this class did not create (the adminAssign answered "already exists") is never removed. A removal that still fails is not thrown: it is
/// returned (<see cref="OnboardingResult.Leftover"/>) so the caller applies the policy and then fails loudly, naming the eligibility.
/// </summary>
internal sealed class GroupPolicyOnboarding
{
    /// <summary>How long the temporary eligibility lasts when its removal fails (it is removed seconds after it was created).</summary>
    public const string TemporaryDuration = "PT1H";

    /// <summary>The access the temporary eligibility grants (any eligibility onboards the group, member is the least privileged).</summary>
    public const string TemporaryAccess = "member";

    /// <summary>How long the policy lookup is repeated after the eligibility was accepted.</summary>
    public static readonly TimeSpan ListingWindow = TimeSpan.FromMinutes(3);

    /// <summary>The wait between two removals answered <c>ActiveDurationTooShort</c>.</summary>
    public static readonly TimeSpan RemovalInterval = TimeSpan.FromSeconds(30);

    /// <summary>How long a removal answered <c>ActiveDurationTooShort</c> is repeated (PIM's minimum is five minutes).</summary>
    public static readonly TimeSpan RemovalWindow = TimeSpan.FromMinutes(7);

    private readonly ReplicationWait _listing;
    private readonly ReplicationWait _removal;
    private readonly TimeProvider _clock;

    /// <param name="settle">The provider's settle schedule: its interval paces the policy lookup, its delay provider every wait.</param>
    /// <param name="clock">The clock the eligibility's start is taken from.</param>
    public GroupPolicyOnboarding(ReplicationWait settle, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(settle);
        ArgumentNullException.ThrowIfNull(clock);
        _listing = settle with { Window = ListingWindow };
        _removal = settle with { Interval = RemovalInterval, Window = RemovalWindow };
        _clock = clock;
    }

    /// <summary>The justification of the temporary eligibility.</summary>
    public static string Justification(string groupId)
    {
        return $"EARM onboarding of {groupId} for policy management";
    }

    /// <summary>
    /// Onboards the group of <paramref name="scope"/> with a temporary eligibility for <paramref name="principalId"/> and returns the policy assignment Graph
    /// lists afterwards. Throws <see cref="EngineDiagnosticCodes.PimGroupOnboardingFailed"/> when the eligibility is refused or the policy is not listed in
    /// time (after removing the eligibility; a removal that failed is named in the message, with
    /// <see cref="EngineDiagnosticCodes.PimOnboardingEligibilityNotRemoved"/>), and <see cref="EngineDiagnosticCodes.PimGroupAccessDenied"/> on a 403.
    /// </summary>
    public async Task<OnboardingResult> OnboardAsync(ResourceInstance desired, PolicyScope scope, string principalId, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        ArgumentNullException.ThrowIfNull(graph);
        var tuple = new GroupScheduleTuple(scope.ScopeId, principalId, TemporaryAccess);
        var created = await AssignAsync(desired, tuple, graph, cancellationToken).ConfigureAwait(false);

        JsonObject? assignment = null;
        Exception? waitFailure = null;
        try
        {
            assignment = await WaitForPolicyAsync(scope, desired, graph, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (created is not null && ex is GraphException or GraphTransportException or OperationCanceledException)
        {
            // The eligibility exists: it is removed before anything is reported.
            waitFailure = ex;
        }

        string? leftover = created is null ? null : await RemoveAsync(desired, tuple, created, graph, cancellationToken).ConfigureAwait(false);
        if (waitFailure is OperationCanceledException && leftover is null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(waitFailure);
        }

        if (waitFailure is not null)
        {
            throw Failed(desired, leftover, $"reading the group's policy after the onboarding eligibility for principal {principalId} was accepted failed ({waitFailure.Message})", waitFailure);
        }

        if (assignment is null)
        {
            throw Failed(desired, leftover, $"Graph still lists no PIM policy for {scope.Describe()} {ListingWindow.TotalSeconds:0} s after the onboarding eligibility for principal {principalId} was accepted (GET {RoleManagementPolicyLookup.AssignmentsPath}?$filter={RoleManagementPolicyLookup.Filter(scope)}); run the deployment again in a few minutes", null);
        }

        return new OnboardingResult(assignment, leftover);
    }

    /// <summary>The adminAssign; the eligibility created, or <c>null</c> when the principal already held it (not this run's: never removed).</summary>
    private async Task<CreatedEligibility?> AssignAsync(ResourceInstance desired, GroupScheduleTuple tuple, IGraphClient graph, CancellationToken cancellationToken)
    {
        var start = _clock.GetUtcNow().AddMinutes(-1);
        var scheduleInfo = new JsonObject
        {
            ["startDateTime"] = start.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            ["expiration"] = new JsonObject { ["type"] = "afterDuration", ["duration"] = TemporaryDuration },
        };
        var body = GroupSchedulesLookup.RequestBody(PimScheduleRequests.AdminAssign, tuple, scheduleInfo, JsonValue.Create(Justification(tuple.GroupId)), null);
        string subject = $"{desired.Describe()}: the temporary eligibility that onboards group {tuple.GroupId} in PIM for Groups (principal {tuple.PrincipalId}, {TemporaryAccess})";
        GraphResponse response;
        try
        {
            response = await graph.PostAsync(new GraphRequest(GroupSchedulesLookup.EligibilityRequestsPath, desired.Api, hint: RetryHint.ExpectReplicationLag), body, cancellationToken).ConfigureAwait(false);
        }
        catch (GraphException ex) when (PimScheduleRequests.IsAlreadyExists(ex))
        {
            // The principal already is an eligible member: that eligibility is not EARM's, so it is kept.
            return null;
        }
        catch (GraphException ex) when (ex.IsForbidden)
        {
            throw GroupSchedulesLookup.AccessDenied(ex, subject, desired.Id);
        }
        catch (Exception ex) when (ProviderReads.IsAmbiguousWriteFailure(ex))
        {
            // The request may have been committed although no answer came back: an eligibility that exists now is this run's and is removed again.
            var found = await GroupSchedulesLookup.FindAsync(graph, GroupSchedulesLookup.EligibilitySchedulesPath, static _ => true, tuple, desired.Api, RetryHint.ExpectReplicationLag, CancellationToken.None).ConfigureAwait(false);
            if (found is null)
            {
                throw new ProviderException(EngineDiagnosticCodes.PimGroupOnboardingFailed, $"{subject} could not be requested ({ex.Message}); no eligibility was created. Run the deployment again.", desired.Id, ex);
            }

            return new CreatedEligibility(null, PimScheduleComparison.ScheduleView(found) ?? scheduleInfo, ExpiresAt(start));
        }
        catch (GraphException ex)
        {
            throw new ProviderException(
                EngineDiagnosticCodes.PimGroupOnboardingFailed,
                $"{subject} was refused by Graph ({(int)ex.Status} {ex.Code}: {ex.GraphMessage}). The principal must be an existing user or service principal that the group's default PIM policy allows to be made eligible for the membership; name another principal in properties.onboarding.temporaryEligiblePrincipalId, or onboard the group in the portal. Nothing was created.",
                desired.Id,
                ex);
        }

        var request = response.Body as JsonObject;
        if (PimScheduleRequests.HasFailedStatus(request, out var status))
        {
            throw new ProviderException(EngineDiagnosticCodes.PimGroupOnboardingFailed, $"{subject} ended with status {status}; the group was not onboarded.", desired.Id);
        }

        return new CreatedEligibility(JsonObjects.GetId(request), scheduleInfo, ExpiresAt(start));
    }

    /// <summary>The policy assignment once Graph lists it, or <c>null</c> after <see cref="ListingWindow"/>.</summary>
    private async Task<JsonObject?> WaitForPolicyAsync(PolicyScope scope, ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        var waited = TimeSpan.Zero;
        while (true)
        {
            var assignment = await RoleManagementPolicyLookup.FindAsync(graph, scope, desired.Api, RetryHint.Default, cancellationToken).ConfigureAwait(false);
            if (assignment is not null && RoleManagementPolicyLookup.PolicyId(assignment) is not null)
            {
                return assignment;
            }

            if (waited + _listing.Interval > _listing.Window)
            {
                return null;
            }

            await _listing.Delay.DelayAsync(_listing.Interval, cancellationToken).ConfigureAwait(false);
            waited += _listing.Interval;
        }
    }

    /// <summary>The adminRemove of the eligibility this run created; <c>null</c> when it is gone, else why it is still there (never throws a Graph failure).</summary>
    private async Task<string?> RemoveAsync(ResourceInstance desired, GroupScheduleTuple tuple, CreatedEligibility created, IGraphClient graph, CancellationToken cancellationToken)
    {
        var body = GroupSchedulesLookup.RequestBody(PimScheduleRequests.AdminRemove, tuple, created.ScheduleInfo, JsonValue.Create(Justification(tuple.GroupId)), null);
        var waited = TimeSpan.Zero;
        int tooNew = 0;
        while (true)
        {
            try
            {
                // Not cancellable: the eligibility exists, and a cancelled run must not leave it behind silently.
                await graph.PostAsync(new GraphRequest(GroupSchedulesLookup.EligibilityRequestsPath, desired.Api, hint: RetryHint.Default), body, CancellationToken.None).ConfigureAwait(false);
                return null;
            }
            catch (GraphException ex) when (PimScheduleRequests.IsAlreadyGone(ex))
            {
                return null;
            }
            catch (GraphException ex) when (PimScheduleRequests.IsTooNewToRemove(ex))
            {
                tooNew++;
                if (waited + _removal.Interval > _removal.Window)
                {
                    return Leftover(tuple, created, $"Graph answered every removal with ActiveDurationTooShort ({tooNew} attempts over {waited.TotalSeconds:0} s)");
                }

                try
                {
                    await _removal.Delay.DelayAsync(_removal.Interval, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return Leftover(tuple, created, "the deployment was cancelled while EARM waited to remove it (Graph had answered ActiveDurationTooShort)");
                }

                waited += _removal.Interval;
            }
            catch (Exception ex) when (ex is GraphException or GraphTransportException)
            {
                return Leftover(tuple, created, $"Graph refused the removal ({ex.Message})");
            }
        }
    }

    private static string Leftover(GroupScheduleTuple tuple, CreatedEligibility created, string why)
    {
        string request = created.RequestId is null ? string.Empty : $", request {created.RequestId}";
        return $"the temporary onboarding eligibility of principal {tuple.PrincipalId} as {TemporaryAccess} of group {tuple.GroupId}{request} could not be removed: {why}. "
            + $"It is still there and expires on its own at {created.ExpiresAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)}; remove it now in the Entra admin center "
            + $"(Identity Governance > Privileged Identity Management > Groups > {tuple.GroupId} > Assignments > Eligible assignments) or with "
            + $"POST {GroupSchedulesLookup.EligibilityRequestsPath} {{ \"action\": \"adminRemove\", \"groupId\": \"{tuple.GroupId}\", \"principalId\": \"{tuple.PrincipalId}\", \"accessId\": \"{TemporaryAccess}\" }}";
    }

    private static ProviderException Failed(ResourceInstance desired, string? leftover, string message, Exception? inner)
    {
        return leftover is null
            ? new ProviderException(EngineDiagnosticCodes.PimGroupOnboardingFailed, $"{desired.Describe()}: {message}. The temporary eligibility was removed again.", desired.Id, inner)
            : new ProviderException(EngineDiagnosticCodes.PimOnboardingEligibilityNotRemoved, $"{desired.Describe()}: {message}; and {leftover}.", desired.Id, inner);
    }

    private static DateTimeOffset ExpiresAt(DateTimeOffset start)
    {
        return start + System.Xml.XmlConvert.ToTimeSpan(TemporaryDuration);
    }

    /// <summary>The eligibility this run created: the request id (when Graph answered), the schedule it was requested with, and when it expires.</summary>
    private sealed record CreatedEligibility(string? RequestId, JsonNode ScheduleInfo, DateTimeOffset ExpiresAt);
}

/// <summary>What <see cref="GroupPolicyOnboarding.OnboardAsync"/> leaves: the group's policy assignment, and why the temporary eligibility is still there (or <c>null</c>).</summary>
internal sealed record OnboardingResult(JsonObject Assignment, string? Leftover);
