# Relationships and assignments (edges)

Group membership, ownership, administrative unit membership, manager, app role assignments, directory role assignments and
licenses are arrays and scalars **inside `properties`**, declarable from either side of the relationship. Graph applies them
through `$ref` navigations and assignment objects, not through PATCH, so the executor applies them in a separate **edge pass**
after every object exists. How the pass works is in [engine.md](engine.md#edge-pass-entraarmengineedges); this page is the
catalog and the rules an author needs. The permissions each kind needs are in [permissions.md](permissions.md).

## Two ways to declare an edge

| Side | Properties | Semantics |
|---|---|---|
| **Target** (the object that has the members) | `members` on groups and administrative units, `owners` on applications, service principals and groups | The declared array is the **full desired set** for that target (ARM desired state): missing edges are added, **live edges that are not listed are removed**. An empty array removes everything. Absent = untouched. |
| **Principal** (the object that is a member or holds the assignment) | `memberOf`, `ownerOf`, `administrativeUnits`, `manager`, `appRoleAssignments`, `directoryRoles` | **Ensure** each declared edge exists; remove only edges **this deployment created earlier** (tracked in the state file) and that are no longer declared. Anything outside the template is never touched. Absent = untouched. |

Both sides may be mixed freely: the edge pass computes *desired = target-side sets ∪ principal-side declarations ∪ standalone
resources*, de-duplicates by edge identity, reads the live state, then adds before it removes. Declaring `user.memberOf` and
`group.members` for the same pair is one write. Removal happens when (a) the target declares the authoritative set and the edge is absent
from it, or (b) the edge is recorded in the state for a principal (or a standalone resource) and is no longer declared. `--mode Complete`
removes all state-recorded edges of a resource before it deletes the resource.

`reference()` calls inside edge arrays are evaluated in the edge pass and **do not** add object-pass dependencies, so mutual
declarations (`user.memberOf` naming a group whose `members` names the user) are never cycles.

## Catalog

| Edge kind | Target-side property | Principal-side property | Standalone resource | Graph apply |
|---|---|---|---|---|
| Group membership | `groups.members` | `memberOf` on users, service principals, groups | none | `POST /groups/{g}/members/$ref`, `DELETE /groups/{g}/members/{p}/$ref` |
| Ownership | `owners` on applications, service principals, groups | `ownerOf` on users and service principals (targets: applications, service principals, groups) | none | `POST /{applications\|servicePrincipals\|groups}/{t}/owners/$ref`, `DELETE …/owners/{p}/$ref` |
| Administrative unit membership | `administrativeUnits.members` | `administrativeUnits` on users and groups | none | `POST /directory/administrativeUnits/{au}/members/$ref`, `DELETE …/members/{p}/$ref` |
| Manager (single-valued) | none | `manager` on users: an object id; `null` removes, absent leaves untouched | none | `PUT /users/{u}/manager/$ref`, `DELETE /users/{u}/manager/$ref` |
| App role assignment | none | `appRoleAssignments: [{ resourceId \| resourceAppId, appRole \| appRoleId }]` on users, groups, service principals | `Microsoft.Entra/appRoleAssignments` | `POST /servicePrincipals/{r}/appRoleAssignedTo`, `DELETE …/appRoleAssignedTo/{id}` |
| Directory role assignment | none | `directoryRoles: [{ role \| roleDefinitionId, directoryScopeId \| administrativeUnit }]` on users, **role-assignable** groups, service principals | `Microsoft.Entra/roleAssignments` | `POST /roleManagement/directory/roleAssignments`, `DELETE …/{id}` |
| License (action-backed, not an edge) | none | `assignedLicenses: [{ skuId \| skuPartNumber, disabledPlans }]` on users (direct assignments only; `usageLocation` required) and groups (group-based licensing) | none | `POST /users\|groups/{id}/assignLicense { addLicenses, removeLicenses }` |

### Entry shapes

```json
"memberOf":            ["[reference('contoso-prod-admins').id]"],
"ownerOf":             ["[reference('contoso-prod-api').id]"],
"administrativeUnits": ["[reference('Orders AU').id]"],
"manager":             "[reference(resourceId('Microsoft.Entra/users', 'cio@contoso.com'), '2026-10-01-preview').id]",
"appRoleAssignments":  [{ "resourceAppId": "[wellKnownApp('MicrosoftGraph')]", "appRole": "User.Read.All" }],
"directoryRoles":      [{ "role": "Global Reader", "directoryScopeId": "/" },
                         { "role": "User Administrator", "administrativeUnit": "[reference('Orders AU').id]" }],
"assignedLicenses":    [{ "skuPartNumber": "ENTERPRISEPREMIUM", "disabledPlans": [] }]
```

- **Ids.** `members`, `owners`, `memberOf`, `ownerOf`, `administrativeUnits` and `manager` take Graph object ids. A target in the template
  is `reference('name').id`; a pre-existing object is `reference(resourceId('Microsoft.Entra/groups', 'name'), '2026-10-01-preview').id`.
  `memberOf` targets must be groups (not dynamic ones). `ownerOf` finds the target's kind from the template resource or
  `GET /directoryObjects/{id}`.
- **`appRoleAssignments` entries.** `resourceId` is the resource service principal's object id; `resourceAppId` is its application id,
  resolved to the principal (an error when the application has no principal in the tenant). `appRole` is the role's `value`
  (resolved against that principal's `appRoles`; an unknown value, a disabled role or a role whose `allowedMemberTypes` does not fit the
  principal is an error); `appRoleId` is the id (`00000000-0000-0000-0000-000000000000` = default access). Unknown members are rejected.
- **`directoryRoles` entries.** `role` accepts a role template id, a built-in role display name (`Global Reader`), a custom role's display name or
  its id (`reference('Orders Support').id` for a custom role of the template); `roleDefinitionId` takes a template id
  (`roleTemplate('Global Reader')`). The scope defaults to `/` (tenant); `directoryScopeId` is `/`, `/administrativeUnits/{id}` or `/{applicationObjectId}`;
  `administrativeUnit` is sugar for an administrative-unit scope (an object id, or `reference('Orders AU').id`). Assignments are permanent and
  active (PIM eligibility is out of scope). Only users, **role-assignable** groups (`isAssignableToRole: true`) and service principals can hold a role.
- **`assignedLicenses` entries.** `skuId` or `skuPartNumber` (resolved with `GET /subscribedSkus`); `disabledPlans` lists service plan ids to turn off, and a
  change re-adds the SKU with the new list. Only **direct** assignments are managed: a license a group hands down
  (`licenseAssignmentStates[].assignedByGroup`) is never removed from the user side. A user needs a `usageLocation`.

### Standalone resources are the same edges

`Microsoft.Entra/appRoleAssignments` and `Microsoft.Entra/roleAssignments` produce the same edge kinds as the principal-side
properties and are de-duplicated against them. The standalone resource is applied in the object pass, before the edge pass, so a
principal-side entry for the same assignment finds it in place and neither writes nor records it; the standalone resource *owns* the assignment:
removing only the principal-side entry changes nothing, removing the standalone resource deletes the assignment in Complete mode and detaches
it in Incremental mode. Use the standalone form for an assignment you want to name (it has a resource id, appears in `deployment show`) and
the principal-side form for the common "this user/group/principal has these roles" declaration.

## Rules and validation

The same edge declared with conflicting extras (for example two different `disabledPlans` for one license) is a validation error on every
declaring row. The offline validator (`deployment validate`) rejects:

| Code | When |
|---|---|
| `DynamicMembershipConflict` | `members` on a dynamic group (`groupTypes` contains `DynamicMembership`) or on a dynamic administrative unit (`membershipType: Dynamic`) |
| `DirectoryRolesRequireRoleAssignableGroup` | `directoryRoles` on a group without a literal `isAssignableToRole: true` |
| `AssignedLicensesRequireUsageLocation` | `assignedLicenses` on a user that declares no `usageLocation` |
| `SchemaViolation` | an entry of the wrong shape (a string where an object is expected, an `appRoleAssignments` entry with neither `resourceId` nor `resourceAppId`, an unknown member, a scope that does not start with `/`) |

What only the live edge model can decide is reported by the engine as `EdgeValidationFailed` (the objects of the row were applied, its edges
were not): a target that is a dynamic group or administrative unit (`memberOf` into a dynamic group), a target that does not exist or is of the
wrong kind, an entry that is not an object id, a role or role assignable constraint the live objects refute, or an entry that is still a
deploy-time placeholder when the pass runs. `EdgeApplyFailed` carries the Graph error of a failed `$ref`; `SignedInPrincipalRemoved` warns
when applying `owners` would remove the signed-in principal. See [diagnostics.md](diagnostics.md) for every code.

An edge that already existed before the template declared it is **not recorded** (the deployment did not create it) and therefore is never
removed by un-declaring it; only a target-side authoritative set removes foreign edges. Restricted administrative units: assign scoped roles
**before** adding members.

## What-if and state

- What-if renders edge changes under the declaring resource as `properties.<property>` array deltas (`+ [n]: "<objectId>"`,
  `- [n]: "<objectId>"`); `manager` is a scalar delta. A `NoChange` object with edge deltas is a `Modify` row. See [whatif.md](whatif.md).
- The state records the principal-side edges a deployment created, per property, so a later removal needs no lookup:

```json
"relationships": {
  "memberOf": ["<groupId>"],
  "ownerOf": [{ "id": "<appOrSpOrGroupId>", "collection": "applications" }],
  "administrativeUnits": ["<auId>"],
  "manager": "<userId>",
  "appRoleAssignments": [{ "principalId": "…", "resourceId": "…", "appRoleId": "…", "id": "<assignmentId>" }],
  "directoryRoles": [{ "roleDefinitionId": "…", "directoryScopeId": "/", "id": "<assignmentId>" }]
}
```

  Licenses are not recorded (the diff is live against the direct assignments). The state is safe to commit: ids only.
