# Privileged access (PIM)

EARM manages Microsoft Entra **Privileged Identity Management** (PIM) for directory roles and for groups with five resource types, shaped after ARM's
PIM for Azure resources where ARM has one:

| Type (`Microsoft.Entra/...`) | What it declares | ARM analogue |
|---|---|---|
| `roleEligibilityScheduleRequests` | A principal **eligible** for a directory role (at the tenant, an administrative unit or an application scope) | `Microsoft.Authorization/roleEligibilityScheduleRequests` |
| `roleAssignmentScheduleRequests` | A **time-bound active** directory role assignment | `Microsoft.Authorization/roleAssignmentScheduleRequests` |
| `privilegedAccess/groupEligibilityScheduleRequests` | A user or group **eligible** for the membership or ownership of a group (PIM for Groups) | none |
| `privilegedAccess/groupAssignmentScheduleRequests` | A **time-bound active** membership or ownership of a group through PIM for Groups | none |
| `roleManagementPolicies` | The PIM policy **rules** (role settings) of a directory role or of a group's member / owner access: activation duration, MFA, justification, approval, authentication context, assignment expiry, notifications | `Microsoft.Authorization/roleManagementPolicies` |

Property-level detail is in [format.md](format.md#resource-type-catalog) and the provider view in [providers.md](providers.md#worked-example-pim-eligibility-for-directory-roles);
this page explains how the pieces fit together. The sample [`samples/privileged-access.entra.json`](../samples/privileged-access.entra.json) combines them
with an emergency-access group, a Conditional Access policy on the PIM authentication context and the authentication methods policy.

## Eligible, active, permanent

| Access | Declare it with | Notes |
|---|---|---|
| Permanent active directory role | `roleAssignments`, or a principal's `directoryRoles` | Not PIM: assigned through `/roleManagement/directory/roleAssignments`. |
| Time-bound active directory role | `roleAssignmentScheduleRequests` | `scheduleInfo` must expire (AfterDateTime or AfterDuration); absent or NoExpiration is refused. |
| Eligible directory role (permanent or time-bound) | `roleEligibilityScheduleRequests` | The principal **activates** it when needed, under the role's PIM policy. |
| Permanent group membership / ownership | the group's `members` / `owners` (or the principal's `memberOf` / `ownerOf`) | Not PIM. |
| Time-bound active membership / ownership | `privilegedAccess/groupAssignmentScheduleRequests` | Must expire. |
| Eligible membership / ownership | `privilegedAccess/groupEligibilityScheduleRequests` | Activated under the group's PIM policy for that access. |

An **activation** of an eligibility is the principal's own act, never the template's: EARM never lists, compares or removes activations. A principal can be
a user, a **role-assignable** group (`isAssignableToRole: true`) or a service principal for directory roles, and a user or group for PIM for Groups.

## Request-based Graph APIs

Graph has no "PUT the eligibility" call: every change is a **schedule request** (`POST …ScheduleRequests` with an `action`), which PIM processes
asynchronously into a **schedule**. EARM hides that:

- the GUID `name` is only a label; the schedule is found on every run by its tuple, (`principalId`, `roleDefinitionId`, `directoryScopeId` or `appScopeId`)
  for directory roles and (`groupId`, `principalId`, `accessId`) for groups, keeping only Direct (not group-inherited) schedules;
- not found: `adminAssign`; found with a different schedule: `adminUpdate` (in place, never a recreate); `deployment delete`: `adminRemove`;
- PIM refuses a schedule shorter than five minutes and the removal of an assignment within five minutes of its creation (Learn:
  pim-how-to-add-role-to-user): an AfterDuration `duration` below `PT5M`, or an AfterDateTime `endDateTime` less than 5 minutes after a declared
  `startDateTime`, is refused offline; a `deployment delete` sooner than five minutes after the create fails with `ActiveAssignmentTooNew` (Graph's
  400 `ActiveDurationTooShort`), exit code 1, and leaves the assignment in place: run the delete again once it is five minutes old (EARM does not wait);
- `requestType` is optional and only `AdminAssign` is accepted (so ARM snippets paste unchanged): EARM picks the action;
- `justification` and `ticketInfo` are request metadata, sent with every request and never compared (`justification` defaults to `Deployed by earm`
  because policies often require one);
- AfterDuration is applied when the schedule is created and not compared afterwards; an end time compares within 1 s; a past start is not drift;
- a freshly accepted request may not be listed yet: the request's `targetScheduleId` stands in and the engine waits for the read like other lagging types.

Policies are different: they always exist, so `roleManagementPolicies` only **reads** a policy (through `/policies/roleManagementPolicyAssignments`) and
`PATCH`es each declared rule that differs. Only the declared rule ids are managed; every other rule keeps its live value. A policy is never created and
`deployment delete` keeps it (`NotDeletable`): to undo a setting, declare the value to restore.

## Policies before requests

A request is judged by the policy in force when PIM receives it (a policy that forbids permanent eligibility refuses a NoExpiration request with
`PimPolicyViolation`). So a `roleManagementPolicies` resource is applied **before** the schedule requests it governs: the provider declares the order
through `IApplyOrderSource` ([engine.md](engine.md#pipeline)), which adds an ordering edge from the policy to each role request of the same role and each
group request of the same group and access in the template, on top of the apply phase (policies are phase 4, requests phase 5). Values match when they
reference the same declared resource or are the same literal (a role also by name against template id); spell them alike, or add a `dependsOn`. A
failed policy skips the requests it governs.

## Group onboarding

A group joins PIM for Groups (**onboarding**) on its first schedule request or policy update, and **cannot be offboarded**. Graph only lists a group's
policies once it is onboarded, and their ids change at onboarding, so EARM never caches a policy id (a rule `PATCH` answered 404 re-reads it once).

When a template declares both a group's policy and a schedule request for that group and access, and the group is not onboarded yet, the first run
**defers** the policy (an `Ignore` row with a `PimPolicyNotAvailable` warning), lets the request onboard the group under the default policy, and the
**second run** applies the policy. Without a same-run request the policy row fails with `PimPolicyNotAvailable` (onboard the group first). The default
group policy often forbids permanent eligibility, so give the first group request an expiration.

Dynamic-membership and on-premises synced groups are refused (PIM for Groups governs neither), and objects in a **restricted management administrative
unit** cannot be governed by PIM at all: keep PIM-governed groups out of such units.

## Retained eligibilities and assignments

A changed tuple (another principal, role, scope, group or access) is a **new** schedule: EARM creates it and **keeps the old one**, which can still be
activated (eligible) or still grants access (active). Both modes report it as `orphan: retained` with an `AssignmentRetained` warning (descriptions
`eligible:`, `eligible member:`, `active:`, `active owner:` …), from the schedules of the declared principals (directory roles) or of the declared target
groups (PIM for Groups). EARM never removes it: remove it yourself, or delete it with a scratch template ([engine.md](engine.md#retained-assignments)).

## Conflicts with permanent access

Graph holds one assignment per tuple, and a PIM active member is a real member of the group. The planner therefore refuses, before any Graph call,
with `PimAssignmentConflict`:

- a `roleAssignmentScheduleRequests` whose principal, role and scope are also declared by a `roleAssignments` resource or a `directoryRoles` entry;
- a `privilegedAccess/groupAssignmentScheduleRequests` on a group of the template that declares `members` (for `accessId: member`) or `owners` (for
  `owner`), even an empty list: the authoritative list would remove the PIM member;
- a group active assignment whose principal lists the group in `memberOf` / `ownerOf` (re-added permanently when the schedule ends).

A permanent assignment already live under a time-bound type's tuple is refused too, never converted or removed (`deployment delete` keeps it,
`NotDeletable`). **Eligibility** never changes members, owners or assignments, so it combines freely with `members`, `owners` and `roleAssignments`.

## Authentication context and MFA

A policy's `AuthenticationContext_EndUser_Assignment` rule makes an activation request a Conditional Access authentication context (`claimValue`
`c1`..`c99`, a `conditionalAccess/authenticationContextClassReferences` resource of the template); a Conditional Access policy targeting that context
can then demand an authentication strength (for example phishing-resistant MFA). PIM offers the authentication context **instead of** the MFA
enablement rule (`MultiFactorAuthentication` in `Enablement_EndUser_Assignment`), not together with it: declare one or the other per policy. EARM does
not check that combination offline.

## Licensing and permissions

PIM needs **Microsoft Entra ID P2** or **Microsoft Entra ID Governance** in the tenant; without it Graph refuses the requests. The Graph permissions per type
(`RoleEligibilitySchedule.ReadWrite.Directory`, `RoleAssignmentSchedule.ReadWrite.Directory`, `PrivilegedEligibilitySchedule.ReadWrite.AzureADGroup`,
`PrivilegedAssignmentSchedule.ReadWrite.AzureADGroup`, `RoleManagementPolicy.ReadWrite.Directory` / `.AzureADGroup`) and Entra roles are in
[permissions.md](permissions.md#manifest-by-type). They are escalation-capable: read [SECURITY.md](../SECURITY.md#privileged-access-and-authentication-methods).

A signed-in user needs the Entra role **active**, not just eligible: PIM checks the roles the token carries. For PIM for Groups
(`privilegedAccess/groupEligibilityScheduleRequests`, `groupAssignmentScheduleRequests`) that is a directory-scope **Groups Administrator**, **Identity
Governance Administrator**, **User Administrator** or **Directory Writers** for a group that is not role-assignable, **Privileged Role Administrator** for a
role-assignable group, or ownership of the group (Learn: privilegedaccessgroup-post-eligibilityschedulerequests,
privilegedaccessgroup-list-eligibilityschedulerequests). **Privileged Role Administrator alone does not cover groups that are not role-assignable.** Without
one of them every read and request under `/identityGovernance/privilegedAccess/group/` answers 403 `UnauthorizedAccessException`. Graph gives the same
answer for a group PIM does not know yet, so EARM reads a 403 on a schedule list as "no schedule" and reports only a write that keeps answering 403 as
`PimGroupAccessDenied` ([diagnostics.md](diagnostics.md)): activate the role (or assign it), then run `earm auth login` again so the token carries it.

Graph behaviours EARM absorbs: the schedule list of a principal or group PIM has not seen yet (created moments earlier) answers 404 instead of an empty list
(read as "no schedule"; PIM for Groups answers 403 `UnauthorizedAccessException` there too, also read as "no schedule", logged at debug); the policy
assignment list of a group PIM for Groups has not onboarded answers 404 `ResourceNotFound` or 403 `UnauthorizedAccessException` (both read as "not listed",
see `PimPolicyNotAvailable`); an adminUpdate right after a schedule was replaced can answer `RoleAssignmentExists` while it settles (repeated with a bounded
backoff, about 75 s, then an apply error); a declared `startDateTime` that is already past is left out of an adminUpdate (Graph starts the updated schedule
now; a past start is never drift). PIM replaces a schedule by removing it, so it refuses the adminUpdate or adminRemove of a group's **only owner
assignment** with 400 `CannotDeleteLastAdminAssignment`: EARM reports `PimLastOwnerAssignment` and the schedule stays as it was; give the group another
owner first (a permanent owner, or a second owner assignment). The first PIM for Groups requests about a group (or principal) created seconds earlier answer 403
`UnauthorizedAccessException` although the caller's role is active (accepted about 15 s later): an adminAssign request (always), and any request when a
dependency was created in the same run, repeats that 403 on `/identityGovernance/privilegedAccess/group/*` on the replication schedule (every 5 s, up to
90 s; each repeat is logged at information) before it reports `PimGroupAccessDenied`, whose message says whether the request was repeated.

## Not covered

PIM for Azure resources (use ARM's own `Microsoft.Authorization` types), PIM alerts and their settings, access reviews, and activating an eligibility
(a user's action, not a deployment).
