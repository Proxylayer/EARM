# Privileged access (PIM)

EARM manages Microsoft Entra **Privileged Identity Management** (PIM) for directory roles and for groups with five resource types, shaped after ARM's
PIM for Azure resources where ARM has one, and Azure RBAC access through Azure Resource Manager with three more (the PIM role settings of Azure roles,
eligibility for Azure roles, and permanent Azure role assignments; [Azure roles](#azure-roles)):

| Type (`Microsoft.Entra/...`) | What it declares | ARM analogue |
|---|---|---|
| `roleEligibilityScheduleRequests` | A principal **eligible** for a directory role (at the tenant, an administrative unit or an application scope) | `Microsoft.Authorization/roleEligibilityScheduleRequests` |
| `roleAssignmentScheduleRequests` | A **time-bound active** directory role assignment | `Microsoft.Authorization/roleAssignmentScheduleRequests` |
| `privilegedAccess/groupEligibilityScheduleRequests` | A user or group **eligible** for the membership or ownership of a group (PIM for Groups) | none |
| `privilegedAccess/groupAssignmentScheduleRequests` | A **time-bound active** membership or ownership of a group through PIM for Groups | none |
| `roleManagementPolicies` | The PIM policy **rules** (role settings) of a directory role or of a group's member / owner access: activation duration, MFA, justification, approval, authentication context, assignment expiry, notifications | `Microsoft.Authorization/roleManagementPolicies` |
| `azureRoleManagementPolicies` | The same PIM policy **rules** for an **Azure RBAC role at an Azure scope** (a management group, a subscription, a resource group or a resource), written through Azure Resource Manager, not Graph: named by `scope` and `roleDefinitionId` (or the role's name in `role`); never created or deleted, only the declared rule ids are managed, and the changed ones are sent in one PATCH ([providers.md](providers.md#worked-example-azure-pim-role-management-policies), sample [`azure-pim-policies.entra.json`](../samples/azure-pim-policies.entra.json)) | `Microsoft.Authorization/roleManagementPolicies` (the same resource) |
| `azureRoleEligibilityScheduleRequests` | A principal **eligible** (PIM) for an **Azure RBAC role at an Azure scope**, through Azure Resource Manager: named by `scope`, `roleDefinitionId` (or `role`) and `principalId`; an existing eligibility is adopted, a missing one is requested once (`AdminAssign`) | `Microsoft.Authorization/roleEligibilityScheduleRequests` (the same resource) |
| `azureRoleAssignments` | A **permanent active** Azure RBAC role assignment at an Azure scope, through Azure Resource Manager: named by `scope`, `roleDefinitionId` (or `role`) and `principalId`; an existing assignment (under any name) is adopted, a missing one is created | `Microsoft.Authorization/roleAssignments` (the same resource) |

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
failed policy skips the requests it governs. An `azureRoleManagementPolicies` resource is ordered the same way before the
`azureRoleEligibilityScheduleRequests` of the same `scope` and role (both `role`, or both `roleDefinitionId`: a role name is not matched against a GUID
offline).

## Group onboarding

A group joins PIM for Groups (**onboarding**) on its first schedule request or policy update, and **cannot be offboarded**. Graph only lists a group's
policies once it is onboarded, and their ids change at onboarding, so EARM never caches a policy id (a rule `PATCH` answered 404 re-reads it once).

When a template declares both a group's policy and a schedule request for that group and access, and the group is not onboarded yet, the first run
**defers** the policy to the end of the run (the what-if shows an `Ignore` row with a `PimPolicyNotAvailable` warning), lets the request onboard the
group under the default policy, and then applies the policy **in the same run**: after the last batch the policy is read and planned again, the read
repeated on the replication schedule (1, 2, 4 s, then every 5 s, up to 90 s) until Graph lists the onboarded group's policy. Only when Graph still
lists none by then does the row stay `Ignored` with the `PimPolicyNotAvailable` warning, and the next run applies the policy. Without a same-run request the policy row fails with `PimPolicyNotAvailable` (onboard the group first), unless the
policy opts in to onboarding (below). The default group policy often forbids permanent eligibility, so give the first group request an expiration.

### Onboarding opt-in

PIM has no "onboard this group" call ("You can't explicitly onboard a group"; Learn: privilegedidentitymanagement-for-groups-api-overview), and in a
real tenant Graph lists no policy for the group before its first eligibility or assignment (probe PP7). A group policy can therefore **opt in** to
onboarding its group itself, by naming the principal of a short-lived eligibility:

```json
{
  "type": "Microsoft.Entra/roleManagementPolicies",
  "apiVersion": "2026-10-01-preview",
  "name": "[guid('pim-policy', 'pim-ops', 'member')]",
  "properties": {
    "group": "[reference('pim-ops').id]",
    "accessId": "member",
    "onboarding": {
      "temporaryEligiblePrincipalId": "[reference('pim-onboarding@contoso.com').id]"
    },
    "rules": [
      { "id": "Expiration_EndUser_Assignment", "maximumDuration": "PT4H" }
    ]
  }
}
```

When Graph lists no policy for the group, `onboarding` is present, and no schedule request of the same deployment onboards the group, the policy row is
a `Create` whose reasons read `group not onboarded: a temporary eligibility for <principal> will onboard it, then be removed`. What-if writes nothing.
The create then, in the same run:

1. requests an `adminAssign` **member** eligibility for the principal, expiring after **PT1H**, with the justification
   `EARM onboarding of <group id> for policy management`;
2. reads the group's policy assignment until Graph lists the post-onboarding policy (every 5 s, up to 3 minutes; about one second in PP7). A policy id
   read before onboarding is never used: the member-role lookup can show a **pre-onboarding id** that differs from the policy's id afterwards, and only
   the policy read after onboarding is patched;
3. removes the eligibility (`adminRemove`), repeating an `ActiveDurationTooShort` answer every 30 s for up to 7 minutes (PP7: accepted five seconds
   after the assignment; onboarding persists once the only eligibility is gone);
4. applies the declared rules to the onboarded policy (the usual one `PATCH` per changed rule).

A removal that still fails does not go unnoticed: the policy is applied, then the resource **fails** with `PimOnboardingEligibilityNotRemoved`, naming the
principal, the group, the request, when the eligibility expires on its own, and how to remove it. A refused eligibility (the principal does not exist, or
the group's default policy does not allow it) is `PimGroupOnboardingFailed` with Graph's answer, and nothing is left behind. An eligibility the principal
already held is never removed (it is not EARM's).

`onboarding` is ignored when the group is onboarded already (the row is a plain `Modify` or `NoChange`) and when a same-run schedule request onboards the
group (the policy is deferred as above). It is never sent to Graph and never compared. It is valid only on a group policy, needs exactly
`temporaryEligiblePrincipalId`, a GUID or an expression (checked offline), and the principal must be a **user or service principal** that the group's
default policy allows to be made eligible (a disabled user created for this purpose works well). It needs
`PrivilegedEligibilitySchedule.ReadWrite.AzureADGroup` and a role that may write the group's eligibilities ([permissions.md](permissions.md)); the
manifest adds them only for policies that opt in.

An access package role scope that grants **Eligible Member** or **Eligible Owner** of the same group counts such a policy as a same-run onboarding,
like a schedule request: it is applied after the policy and waits for the catalog to offer the eligible role, so a fresh group needs one run
([access-packages.md](access-packages.md#can-a-package-grant-eligible-pim-for-groups-membership)).

Why a property of the policy resource rather than a deployment setting: ARM keeps a resource's behaviour in its own `properties` (the template says
what happens to that resource, and nothing outside the template changes it), the onboarding concerns exactly one group, and naming the principal
belongs next to the group it onboards. A deployment-wide switch would hide a privileged write (an eligibility) behind a command-line flag and apply it
to every group at once. EARM's other sugar (`role`, `group`, `accessId`) lives in `properties` the same way.

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
  `owner`), even an empty `members` list in Complete mode: the authoritative list would remove the PIM member (in Incremental mode a literal `[]`
  declares nothing and is no conflict; an empty `owners` list on a group is refused in Complete mode anyway);
- a group active assignment whose principal lists the group in `memberOf` / `ownerOf` (re-added permanently when the schedule ends).

A permanent assignment already live under a time-bound type's tuple is refused too, never converted or removed (`deployment delete` keeps it,
`NotDeletable`). **Eligibility** never changes members, owners or assignments, so it combines freely with `members`, `owners` and `roleAssignments`.

## Authentication context and MFA

A policy's `AuthenticationContext_EndUser_Assignment` rule makes an activation request a Conditional Access authentication context (`claimValue`
`c1`..`c99`, a `conditionalAccess/authenticationContextClassReferences` resource of the template); a Conditional Access policy targeting that context
can then demand an authentication strength (for example phishing-resistant MFA). PIM offers the authentication context **instead of** the MFA
enablement rule (`MultiFactorAuthentication` in `Enablement_EndUser_Assignment`), not together with it: declare one or the other per policy. EARM does
not check that combination offline.

## Azure roles

Three types manage Azure RBAC access through **Azure Resource Manager** instead of Graph (`IResourceManagerProvider`: a separate Resource Manager token,
and a delegated sign-in's one-time consent, `earm auth login -f <template>`). Each is named by an Azure `scope` and a role, and read live on every run:

- **`scope`**: a management group (`/providers/Microsoft.Management/managementGroups/{id}`), a subscription (`/subscriptions/{id}`), a resource group, or a
  resource id under a resource group. It is normalised (no trailing `/`; ARM's casing of the keywords; the subscription id lower case). Only an object
  whose `properties.scope` **is** that scope counts: an eligibility or assignment inherited from a parent scope, one of a child scope, and (for
  eligibilities) one held through a group (`memberType` other than `Direct`) are never adopted, changed or deleted.
- **`roleDefinitionId`** (a GUID or any role definition id) or **`role`** (a role's name such as `Contributor`, or its GUID; resolved at plan time with
  `GET {scope}/providers/Microsoft.Authorization/roleDefinitions?$filter=roleName eq '…'`; exactly one role of that name must be assignable there). The id is
  normalised to the form ARM uses at the scope: `/subscriptions/{subscription}/providers/Microsoft.Authorization/roleDefinitions/{guid}` under a
  subscription, and the tenant-level `/providers/Microsoft.Authorization/roleDefinitions/{guid}` at a management group (roles are compared by GUID).
- **`principalId`** (eligibilities and assignments): the object id of a user, group or service principal, usually `[reference(resourceId('Microsoft.Entra/groups', …)).id]`
  of a group of the same template. It is not looked up in Graph (no Graph permission is needed); ARM refuses an unknown principal.

| Type | Read | Missing | Exists | `deployment delete` |
|---|---|---|---|---|
| `azureRoleManagementPolicies` | `roleManagementPolicyAssignments?$filter=roleDefinitionId eq '…'` at the scope, then the policy | an error (`PimPolicyNotAvailable`), except at a management group (below) | the declared rules are compared; the changed ones are one PATCH | kept (`NotDeletable`) |
| `azureRoleEligibilityScheduleRequests` | `roleEligibilitySchedules` (api-version 2020-10-01) at the scope, every page, matched client-side | one `PUT roleEligibilityScheduleRequests/{new GUID}` (2022-04-01-preview), `requestType: AdminAssign` | adopted (`NoChange`); a declared end the live eligibility does not have is reported in the row, never rewritten | one `AdminRemove` request |
| `azureRoleAssignments` | `roleAssignments` (api-version 2022-04-01) at the scope, every page (`nextLink`), matched client-side | `PUT roleAssignments/{guid(scope, roleDefinitionId, principalId)}` | adopted (`NoChange`) under any name: one made in the portal, by PIM (an active PIM assignment is a role assignment too) or by another tool | `DELETE` of the matched assignment |

A deployment (incremental or complete) never deletes an Azure eligibility or role assignment, and the orphan report does not list them. An eligibility
request's `justification` (default "Deployed by earm") and `scheduleInfo` (default `{ "expiration": { "type": "NoExpiration" } }`; `AfterDuration` with
`duration`, `AfterDateTime` with `endDateTime`; `startDateTime` is now when absent or past) are sent with the request only; an assignment's
`principalType` (`User`, `Group` or `ServicePrincipal`, which lets ARM accept a principal created moments earlier) and `description` are sent on create only.
An eligibility or assignment that appears between the plan and the write (ARM answers `RoleAssignmentExists`) is read again and adopted. A request the
role's PIM policy refuses fails with `PimPolicyViolation`: the **default Azure policy forbids permanent eligibility** (`Expiration_Admin_Eligibility`, at
most 365 days), so either declare an expiration or declare the role's `azureRoleManagementPolicies` with
`{ "id": "Expiration_Admin_Eligibility", "isExpirationRequired": false }` (applied first; see the sample
[`azure-pim-access.entra.json`](../samples/azure-pim-access.entra.json)).

The signed-in identity needs an Azure RBAC role at each scope: **Owner** or **User Access Administrator** for eligibility requests
(`Microsoft.Authorization/roleEligibilityScheduleRequests/write`), **Owner**, **User Access Administrator** or **Role Based Access Control Administrator** for
role assignments (`Microsoft.Authorization/roleAssignments/write`, and `/delete` for `deployment delete`) and policies; **Reader** is enough for a what-if.
`earm permissions` lists each grant per declared scope, and the what-if footer names them (`Required Azure roles`).

### Management group policies

At a subscription, resource group or resource, ARM has a PIM policy for every role. At a **management group** it does not: a role's policy (and its
policy assignment) appears only once the first eligibility or assignment request of that role exists at that management group, so the
`roleManagementPolicyAssignments` lookup lists nothing before that. EARM handles it this way:

- When the template also declares an `azureRoleEligibilityScheduleRequests` of the **same scope and role** (spelled the same way), the policy is ordered
  before it as usual, finds no policy, and is **deferred to the end of the run** (an `Ignore` row with a `PimPolicyNotAvailable` warning in what-if and in
  the run). The eligibility request runs, which creates the policy; after the last batch the executor reads the policy again (on the replication
  schedule) and applies the declared rules. If ARM still lists none by then, the run reports the warning and the next deployment applies the rules.
  Until then the role's **default** policy judges that first request, so give it a `scheduleInfo` the default rules accept (an `AfterDuration` of at most
  `P365D`); once the declared policy is in force, later requests follow it.
- Otherwise the policy fails with `PimPolicyNotAvailable`, naming the cause and the fix: declare an eligibility of the same scope and role in the template,
  or make a principal eligible for the role at the management group in the portal, then deploy again.

## Licensing and permissions

PIM needs **Microsoft Entra ID P2** or **Microsoft Entra ID Governance** in the tenant; without it Graph refuses the requests. The Graph permissions per type
(`RoleEligibilitySchedule.ReadWrite.Directory`, `RoleAssignmentSchedule.ReadWrite.Directory`, `PrivilegedEligibilitySchedule.ReadWrite.AzureADGroup`,
`PrivilegedAssignmentSchedule.ReadWrite.AzureADGroup`, `RoleManagementPolicy.ReadWrite.Directory` / `.AzureADGroup`) and Entra roles are in
[permissions.md](permissions.md#manifest-by-type). They are escalation-capable: read [SECURITY.md](../SECURITY.md#privileged-access-and-authentication-methods). `azureRoleManagementPolicies`
needs no Graph permission and no Entra licence check of its own: an Azure RBAC role at the policy's scope that may write role management policies (Owner,
User Access Administrator or Role Based Access Control Administrator; Reader for a what-if), and a delegated sign-in's one-time Azure Resource Manager consent
(`earm auth login -f <template>`); `earm permissions` lists the grant per policy and scope. The same holds for `azureRoleEligibilityScheduleRequests` and
`azureRoleAssignments` ([Azure roles](#azure-roles)); an Azure eligibility needs PIM for Azure resources, which Microsoft Entra ID P2 or ID Governance in the
tenant provides.

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

Time-bound active assignment requests for Azure roles (`Microsoft.Authorization/roleAssignmentScheduleRequests`; use ARM's own type), changing or
renewing an existing Azure eligibility in place (remove it and deploy again), conditions on Azure role assignments (ABAC `condition`), PIM alerts and their
settings, access reviews, and activating an eligibility (a user's action, not a deployment).
