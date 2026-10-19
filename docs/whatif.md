# What-if output

How `earm deployment what-if` (and `deployment create --what-if` / `-c`) report the changes a deployment would
make, in the human-readable text form and in the ARM-shaped JSON form. The renderers live in
`src/EArm.Engine/WhatIf/`; the CLI glue (`-o`, colours, `--no-pretty-print`, exit codes) in
`src/EArm.Cli/Output/WhatIfWriter.cs` and `src/EArm.Cli/Commands/DeploymentRunner.cs`.

Nothing in a what-if is written to the tenant or to the local disk (EARM keeps no state: every resource is found by its live key). The what-if shown by `create -c` is the plan the
executor then applies (expansion, dependency graph and batches are computed once); the executor still re-reads each
resource right before writing it (docs/engine.md, pipeline step 6), so a confirmed what-if cannot go stale.

## Pipeline

```
DeploymentPlan (PlanMode.WhatIf)
   └─ WhatIfReport.From(plan)        rows in batch order (ties by resource id), then the orphan report rows (by type, then id);
        │                            ARM-shaped before/after payloads; secure values masked; edge counts; summary;
        │                            the resources whose condition is false apart (Skipped)
        └─ WhatIfView.Of(report, --result-format, --exclude-change-types)
             ├─ WhatIfText.Render(...)    -o table   (default)
             └─ WhatIfJson.Build(...)     -o json (and jsonc / yaml / yamlc / tsv, rendered from it)
```

`WhatIfReport` is the single model both renderers read, so the two outputs always agree on ordering, masking and
counts. Tests can build it from any plan and assert on `Rows`, `Skipped`, `Summary`, `Edges` and `HasChanges`. `WhatIfView` is what one run shows: the
report in a result format, minus the excluded change types, with the summary and edge counts of the rows shown.

## Change types

The `changeType` values, with ARM's meaning (Learn, "Template deployment what-if", change types) and how EARM uses each:

| Change type | ARM | EARM |
|---|---|---|
| `Create` | The resource doesn't exist but is defined in the template. | Same. |
| `Delete` | Complete mode: the resource exists but isn't defined in the template, and is deleted. | Never emitted: Complete mode reports such objects as orphans and never deletes them (an `Ignore` row, see below). |
| `Modify` | The resource exists, is defined, and its properties change. | Same (`FullResourcePayloads`). |
| `NoChange` | The resource exists, is defined, and its properties don't change. | Same (`FullResourcePayloads`). |
| `Deploy` | The resource exists and is defined; returned with `ResourceIdOnly`, when what-if does not determine whether properties change. | Same: with `--result-format ResourceIdOnly` every existing declared resource (`Modify` or `NoChange`) is `Deploy`, symbol `!`. A languageVersion 2.0 `"existing": true` resource is read, never deployed, and stays `NoChange`. |
| `Ignore` | The resource exists but isn't defined in the template (or what-if hit its limits); it is not deployed or modified. | The orphan report rows (live objects not declared, or retained assignments; never written), a resource whose condition is only known at deployment time (`?`), a resource whose whole `properties` is one expression only known at deployment time (`?`: it reads a resource the run creates), and a resource a provider defers because PIM for Groups has not onboarded its group yet (`create` applies a group policy at the end of the same run, once a request of the run has onboarded the group). |
| `Unsupported` | The change cannot be determined or applied. | A change that cannot be applied as planned (`unsupportedReason`; the text keeps the planned symbol and adds `!`). |

**A resource whose condition is false** is not part of the deployment: ARM's what-if does not list it, and neither does EARM (it used to be an `Ignore`
row). It is kept apart as **skipped**: the text lists it only with `--verbose`, under `Skipped (condition false; not part of the deployment, nothing is read
or written):`, and the JSON carries it in the EARM extension `skipped[]` (`{ "resourceId", "reason" }`) and in `summary.skipped`, never in `changes[]`.

**`--exclude-change-types` / `-x`** (`--what-if-exclude-change-types` on `create`) hides the rows of the named change types from both formats, as the
Azure CLI does. The filter applies to the reported change type, so `Unsupported` hides the rows that cannot be applied as planned and `Modify` does not,
and `Deploy` matters only with `ResourceIdOnly`. The summary and the edge counts describe the rows shown; the text adds `Not shown: n row(s) of the
excluded change types (...); they are still deployed.` Nothing about the deployment changes: `--exit-code-on-changes` and the replacement confirmation
of `create` still see every row.

## Symbols

| Symbol | Meaning |
|---|---|
| `+` | Create: the object does not exist and will be created (`after` payload shown). |
| `~` | Modify: the object exists and differs (property deltas shown). Includes relationship-only changes (edge adds / removes under the declaring resource). |
| `-` | Delete: reserved by the ARM shape. A deployment never deletes an undeclared object (Complete mode only reports it), so no row carries it; the old object of a recreate is part of its `~` row (reason "the existing object (...) is DELETED and created again"). |
| `=` | NoChange: the object matches the template (id only; `--verbose` also prints the payload). |
| `*` | Ignore: nothing is done. Reasons: a condition that depends on a deploy-time value, a resource a provider defers (PIM not onboarded yet), or a row of the **orphan report**: `(orphan: undeclared)` / `(orphan: retained)` after the type, see [Orphan report](#orphan-report). A resource whose condition is false is not a row at all (see [Change types](#change-types)). |
| `!` | Deploy (`--result-format ResourceIdOnly` only): an existing declared resource that is redeployed; property changes are not computed for the output. |

A second character qualifies the row:

| Suffix | Meaning |
|---|---|
| `?` | A deploy-time value is involved: the resource id is still symbolic (its name depends on a resource that does not exist yet), a property is only known at deployment time, or an edge target is a placeholder. Such values render as `(deploy-time)`. |
| `!` (after the symbol) | The change cannot be applied as planned (`unsupportedReason`): an immutable property differs on a type that cannot be recreated, a required property is missing, a read failed, a relationship could not be validated. The reason is printed on a `!` line under the row and counted in the footer. `!` wins over `?`. |

Secure values (`securestring` / `secureObject` parameters, tainted expression results, the type's `secure` overlay
paths such as `passwordProfile.password`, one-time secrets such as `secretText`) are always `***`, in payloads and on
both sides of a delta, in both output formats. The CLI never holds a what-if that contains a clear-text secret. On an existing object, a secure
value Graph reads back (a scalar such as a policy `state` or a name passed as a `securestring` parameter) is compared like any value: a `Modify`
delta shown as `*** => ***` when it differs (and then sent), nothing when it is equal. A write-only secret (a path the type declares `secure`, a
value Graph does not return, a secure array) cannot be compared: it is a `NoEffect` delta (shown with `--verbose`), so a rerun of an unchanged
template is `NoChange` and `--exit-code-on-changes` does not fire for it; a `Create` still sends the secret. Unlike an ARM PUT, which carries
every secure value on every deployment, EARM sends a write-only secret only when it creates the object.

## Text layout (`-o table`)

```
Deployment 'orders-dev' (Incremental) on tenant 11111111-1111-1111-1111-111111111111

Resource and property changes are indicated with these symbols:
  + Create
  ~ Modify
  - Delete
  = NoChange
  * Ignore
  ? Value only known at deployment time
  ! Change cannot be applied as planned

The deployment would make the following changes:

  + /providers/Microsoft.Entra/applications/contoso-dev-api [Microsoft.Entra/applications]
      name: "contoso-dev-api"
      type: "Microsoft.Entra/applications"
      apiVersion: "2026-10-01-preview"
      properties:
        displayName: "Orders API"
        identifierUris: [
          0: "api://contoso-dev-api"
        ]
        appRoles: [
          0:
            id: "6d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0002"
            value: "Orders.Admin"
        ]

  ~ /providers/Microsoft.Entra/applications/contoso-dev-api [Microsoft.Entra/applications]
      ~ properties.displayName: "Orders API (old)" => "Orders API"
      ~ properties.web:
          ~ redirectUris: [
              + [1]: "https://orders.contoso.com/signin-oidc"
              - [0]: "https://old.contoso.com/signin"
            ]
      ~ properties.appRoles: [
          ~ ['6d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0002']:
              ~ isEnabled: true => false
          + ['6d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0003']:
              id: "6d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0003"
              value: "Orders.Reader"
        ]

  ~? /providers/Microsoft.Entra/users/bg-orders@contoso.com [Microsoft.Entra/users]
      ~ properties.manager: "22222222-0000-0000-0000-000000000004" => "22222222-0000-0000-0000-000000000005"
      ~ properties.memberOf: [
          + [0]: "(deploy-time)"
        ]

  ~! /providers/Microsoft.Entra/groups/contoso-legacy [Microsoft.Entra/groups]
      ~ properties.mailEnabled: false => true
      ~ properties.mailNickname: "legacy" => "legacy-renamed"
      ! Immutable property properties.mailEnabled of Microsoft.Entra/groups 'contoso-legacy' differs from the live object ...

  = /providers/Microsoft.Entra/groups/contoso-dev-orders-ops [Microsoft.Entra/groups]

  * /providers/Microsoft.Entra/roleAssignments/22222222-0000-0000-0000-000000000060 [Microsoft.Entra/roleAssignments] (orphan: retained)
      (retained: no longer declared; still grants access (principalId '...', roleDefinitionId '...', directoryScopeId '/'). Assignments are never removed: delete it yourself if the access should end.)

Resource changes: 1 to create, 3 to modify, 0 to delete, 1 unchanged, 0 ignored.
Edges: 2 to add, 0 to remove.
Orphans: 1 live object(s) not declared in the template (reported only; never deleted).
Required permissions: Application.ReadWrite.All, Group.ReadWrite.All, GroupMember.ReadWrite.All, User.ReadWrite.All.
1 change(s) cannot be applied as planned; see the rows marked '!'.

warning AssignmentRetained: '/providers/Microsoft.Entra/roleAssignments/22222222-0000-0000-0000-000000000060' (Graph id ...) is not declared by the template but still grants access ...
```

Rules:

- **Header**: `Deployment '<name>' (<mode>) on tenant <id>`; `(unknown)` when the tenant id is not known (offline).
- **Rows** are two-space indented: `<marker> <resourceId> [<resourceType>]`. Order: batch order of the dependency
  graph, ties broken by resource id (ordinal), then the orphan report rows by type, then resource id; an orphan row
  carries `(orphan: undeclared)` or `(orphan: retained)` after the type and its reason under it, never a payload.
- **Create / Delete** print the `after` / `before` payload (six-space body indent): `name`, `type`, `apiVersion`
  and `properties`. Nested objects indent by two,
  arrays print as `key: [` … `n: value` … `]`; empty values print as `{}` / `[]`; strings are JSON-quoted. For
  Create rows the payload is the full declaration: object properties plus the relationship properties
  (`members`, `owners`, `memberOf`, `ownerOf`, `administrativeUnits`, `manager`, …) with deploy-time targets as
  `(deploy-time)`.
- **Modify** prints one line per delta: `<marker> <path>: <before> => <after>` for scalars (`+ path: value` /
  `- path: value` for one-sided deltas), `~ path:` followed by indented children for nested objects, and
  `~ path: [` … `]` for arrays. Child paths are relative to the parent: `[n]` for index-addressed entries (scalar
  arrays, edge entries), `['<key>']` for array-of-object members addressed by the type's `memberKey` (`id` for
  `appRoles` / `oauth2PermissionScopes`, `cidrAddress` for `ipRanges`, `value` for roles / scopes, …; index when
  the member has no key), bare property names inside objects. A one-sided composite value prints as an indented
  block. `NoEffect` deltas (live-only sub-properties Graph refuses to null) are hidden unless `--verbose`, except a `NoEffect`
  delta that carries a provider note, which is always shown with the note in parentheses, also under a `NoChange` row: a
  Conditional Access policy's declared `state: enabled` held back without `--allow-ca-enforce` prints
  `= properties.state: "disabled" => "enabled" (declared enabled, live value kept; pass --allow-ca-enforce to enforce)`.
- **Removals that depend on deploy-time values**: when an authoritative list (`members`, `owners`) still holds a placeholder, the live
  entries it does not otherwise list are shown as `- [n]: "<objectId>" (removed at deployment unless a deploy-time entry resolves to it)` and
  the row is marked `?`; they count as edge removals. See [edges.md](edges.md#authoritative-owners).
- **Empty authoritative lists**: in Incremental mode (the default) `members: []` / `owners: []` declares nothing, so the row has no
  delta for it and nothing is removed; an `EmptyEdgeListIgnored` info diagnostic says the list was ignored. In Complete mode each live
  entry is a `- [n]` removal (a group's `owners: []` is an unsupported row instead). See [edges.md](edges.md#empty-lists).
- **Edge deltas** appear under the declaring resource as `properties.<property>` array deltas with
  `+ [n]: "<objectId>"` / `- [n]: "<objectId>"` children (`manager` is a scalar delta). A `NoChange` object with edge
  deltas is a `Modify` row.
- **Reasons** are printed in parentheses under the row (symbolic name: `the name is only known once '<id>' exists`;
  immutable recreate; deploy-time properties; delete risks); the unsupported reason on a `!` line.
- **Footer**: `Resource changes: n to create, n to modify, n to delete, n unchanged, n ignored.` (orphan rows are
  not counted as ignored), `Edges: n to add, n to remove.` (edge entries created / removed, counted over the
  relationship-property deltas), `Orphans: n live object(s) not declared in the template (reported only; never
  deleted).` (only when there are orphan rows), `Required permissions: …` (sorted union of the providers' application permissions; `none.` when empty), then the
  unsupported count when non-zero, then a blank line and the diagnostics (warnings; `warning <Code>: <message>`)
  and the unresolved existing-resource lookups.
- **`--result-format ResourceIdOnly`** prints the rows (marker, id, type), the reasons and the footer only. As ARM, an existing declared resource is
  `Deploy` (`!`, blue) instead of `Modify` / `NoChange`; the legend lists `! Deploy` and marks `?` and `!` as qualifiers written after the symbol
  (`!!` is a Deploy row that cannot be applied as planned, `!?` one with deploy-time values), and the footer reads
  `Resource changes: n to create, n to deploy, n to delete, n ignored.`
- **`--exclude-change-types`** leaves out the rows of the excluded change types and adds the `Not shown: ...` line after the edge counts.
- **`--verbose`** adds the `Skipped (condition false; ...)` section after the rows and `Skipped: n resource(s) whose condition is false.` after the footer.
- **Colours**: ANSI only when standard output is a terminal (not redirected, not captured in-process) and neither
  `NO_COLOR` (any non-empty value) nor `AZURE_CORE_NO_COLOR` (`true` / `1` / `yes` / `on`) is set. Green `+`,
  magenta `~`, red `-`, grey `=` / `*`, cyan `?`, bold red `!` (a change that cannot be applied), blue `!` (Deploy). No column padding, so lines wrap the same at any
  width.

## JSON shape (`-o json`)

```json
{
  "status": "Succeeded",
  "deploymentName": "orders-dev",
  "tenantId": "11111111-1111-1111-1111-111111111111",
  "mode": "Incremental",
  "changes": [
    {
      "resourceId": "/providers/Microsoft.Entra/applications/contoso-dev-api",
      "changeType": "Modify",
      "before": { "name": "contoso-dev-api", "type": "Microsoft.Entra/applications", "apiVersion": "2026-10-01-preview", "properties": { "displayName": "Orders API (old)" } },
      "after":  { "name": "contoso-dev-api", "type": "Microsoft.Entra/applications", "apiVersion": "2026-10-01-preview", "properties": { "displayName": "Orders API" } },
      "delta": [
        { "path": "properties.displayName", "propertyChangeType": "Modify", "before": "Orders API (old)", "after": "Orders API" },
        { "path": "properties.appRoles", "propertyChangeType": "Array", "before": [ "..." ], "after": [ "..." ],
          "children": [
            { "path": "['6d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0002']", "propertyChangeType": "Modify", "before": { "...": "..." }, "after": { "...": "..." },
              "children": [ { "path": "isEnabled", "propertyChangeType": "Modify", "before": true, "after": false } ] },
            { "path": "['6d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0003']", "propertyChangeType": "Create", "after": { "...": "..." } }
          ] }
      ],
      "unsupportedReason": null
    },
    {
      "resourceId": "/providers/Microsoft.Entra/users/bg-orders@contoso.com",
      "changeType": "Create",
      "after": { "name": "bg-orders@contoso.com", "type": "Microsoft.Entra/users", "apiVersion": "2026-10-01-preview",
                 "properties": { "passwordProfile": { "password": "***" }, "manager": "U-CIO", "memberOf": [ "(deploy-time)" ] } },
      "unsupportedReason": null,
      "reasons": [ "some property values are only known at deployment time" ]
    },
    {
      "resourceId": "/providers/Microsoft.Entra/groups/contoso-dev-orders-admins",
      "changeType": "Modify",
      "before": { "...": "..." }, "after": { "...": "..." },
      "delta": [ { "path": "properties.members", "propertyChangeType": "Array",
                   "children": [ { "path": "[0]", "propertyChangeType": "Create", "after": "22222222-0000-0000-0000-000000000003" },
                                 { "path": "[1]", "propertyChangeType": "Delete", "before": "22222222-0000-0000-0000-000000000001" } ] } ],
      "unsupportedReason": null
    },
    { "resourceId": "/providers/Microsoft.Entra/groups/contoso-dev-orders-ops", "changeType": "NoChange", "before": { "...": "..." }, "after": { "...": "..." }, "unsupportedReason": null },
    { "resourceId": "/providers/Microsoft.Entra/roleAssignments/22222222-0000-0000-0000-000000000060", "changeType": "Ignore",
      "orphan": "retained", "graphId": "22222222-0000-0000-0000-000000000060", "unsupportedReason": null,
      "reasons": [ "retained: no longer declared; still grants access (...). Assignments are never removed: delete it yourself if the access should end." ] }
  ],
  "diagnostics": [ { "level": "Warning", "code": "AssignmentRetained", "target": null, "message": "..." } ],
  "requiredPermissions": [ "Application.ReadWrite.All", "Group.ReadWrite.All" ],
  "unresolvedReferences": [ { "resourceId": "/providers/Microsoft.Entra/users/cio@contoso.com", "apiVersion": "2026-10-01-preview", "reason": "..." } ],
  "skipped": [ { "resourceId": "/providers/Microsoft.Entra/conditionalAccess/namedLocations/Contoso offices", "reason": "condition evaluated to false" } ],
  "summary": { "create": 1, "modify": 2, "delete": 0, "noChange": 1, "deploy": 0, "ignore": 0, "unsupported": 0, "orphans": 1, "skipped": 1, "edges": { "add": 1, "remove": 1 } }
}
```

| Member | Notes |
|---|---|
| `status` | Always `Succeeded` (a failed plan exits before any what-if is rendered, see exit codes). |
| `deploymentName`, `tenantId`, `mode` | `-n` (or the template stem), the signed-in tenant (`null` offline), `Incremental` / `Complete`. |
| `changes[]` | Same order as the text. `resourceId` is the ARM-style id; `changeType` one of `Create`, `Modify`, `Delete`, `NoChange`, `Ignore`, `Deploy` (`ResourceIdOnly` only, for an existing declared resource), or ARM's `Unsupported` for a row that cannot be applied as planned (it carries an `unsupportedReason`; the text marks it `!`). An `Unsupported` row keeps the `before` / `after` / `delta` of its planned change type (the text symbol shows that type) and is counted in `summary` under that type and under `unsupported`. |
| `before` / `after` | `{ name, type, apiVersion, properties }`. ARM semantics: Create has `after`, Delete has `before`, Modify and NoChange have both, Ignore (orphan rows included) has neither. `after.properties` includes the relationship properties; `before.properties` is the live object as the provider reads it (server-managed properties stripped). Absent with `--result-format ResourceIdOnly`. |
| `delta[]` | Modify rows only (ARM: Create / Delete carry only the payload). Each entry: `path`, `propertyChangeType` (`Create`, `Delete`, `Modify`, `Array`, `NoEffect`), `before` / `after` (omitted when null), `children[]`, `note` (EARM: why a declared value is held back, for example a Conditional Access `state: enabled` without `--allow-ca-enforce`; a `NoChange` row carries a `delta[]` holding only such noted deltas). Top-level paths are rooted at the resource (`properties.web.redirectUris`); child paths are relative to their parent (`[0]`, `['<memberKey>']`, `isEnabled`), as in ARM's what-if output. Absent with `ResourceIdOnly`. |
| `deployment` | **Extension.** Present on the row of a resource deployed by a nested or linked deployment: the deployment names from the main template down, joined with `/` (`teams/payments`). See [Nested deployments](#nested-deployments). |
| `orphan`, `graphId` | **Extension.** Present on the rows of the [orphan report](#orphan-report) only (always `changeType: Ignore`): `orphan` is `undeclared` (a live object of a declared type no declared resource matches, Complete mode) or `retained` (a live assignment / grant of a declared principal that is not declared, both modes); `graphId` is the live object's id. Present with `ResourceIdOnly` too. |
| `unsupportedReason` | Always present on every change; `null` unless the change cannot be applied as planned (then `changeType` is `Unsupported`). |
| `reasons[]` | **Extension.** Present when non-empty: the human-readable reasons printed in parentheses in the text (ignore reason, symbolic name, deploy-time values, recreate, delete risks) including the unsupported reason. |
| `diagnostics[]` | `level` (`Info` / `Warning` / `Error`), `code`, `target` (template path or `null`), `message`. Planning warnings (recreates, retained assignments, an incomplete orphan report, edge warnings) plus one `UnresolvedReference` warning per unresolved existing-resource lookup. |
| `requiredPermissions[]` | **Extension.** Sorted union of the Graph application permissions the plan needs. |
| `unresolvedReferences[]` | **Extension.** `reference(resourceId(...), apiVersion)` lookups that could not be performed: `resourceId`, `apiVersion`, `reason`. |
| `skipped[]` | **Extension.** The resources whose condition evaluated to false, in row order: `resourceId`, `reason`. ARM's what-if does not list them; they are not in `changes[]`. |
| `summary` | **Extension.** Counts per change type of the rows shown (after `--exclude-change-types`; with `ResourceIdOnly` the existing declared resources count under `deploy`, not `modify` / `noChange`) plus `unsupported` (rows carrying an `unsupportedReason`, `changeType: Unsupported`, already counted in their planned change type), `orphans` (orphan report rows; not counted in `ignore`), `skipped` (condition false; never a row) and `edges` (`add` / `remove`). |

## Nested deployments

ARM's what-if expands nested and linked templates and lists their resources as ordinary changes
([what-if limits](https://learn.microsoft.com/azure/azure-resource-manager/templates/deploy-what-if#what-if-limits)). EARM does the same: the resources of
a `Microsoft.Resources/deployments` template ([format.md](format.md#nested-and-linked-deployments)) are planned in the same batches as the parent's and
shown as rows, each annotated with the deployment it comes from:

```
  + /providers/Microsoft.Entra/groups/contoso-dev-team-payments [Microsoft.Entra/groups] (nested deployment: team-payments)
```

(`"deployment": "team-payments"` in the JSON). The deployment itself is not a row: it writes nothing. Its outputs are evaluated once its resources are
planned, so a parent resource that reads `reference('team-payments').outputs.groupId.value` shows the value when the group exists, and a deploy-time value
(`?`) when the group is still to be created. A deployment whose condition is false is not expanded and contributes no row. ARM gives up expanding after 500
nested templates and reports the rest as `Ignore`; EARM's limit is the same number but an error (`NestedDeploymentLimitExceeded`), since a partial
plan cannot be applied. Linked https templates are downloaded for the what-if.

## Orphan report

Complete mode **never deletes**. Instead the what-if (and the deployment result, `orphans[]`) lists the live objects of the types the template declares that no
declared resource matches by key: an `Ignore` row with `orphan: undeclared`. The listing is bounded per type:

| Types | What is listed |
|---|---|
| Conditional Access policies, named locations, custom authentication strengths, custom role definitions (`isBuiltIn eq false`), administrative units, authentication contexts | The whole (small) collection. |
| Service principals, federated identity credentials, password credentials | The children of the applications the template declares (the service principal of each declared application; its federated identity credentials and password credentials). |
| Role assignments, PIM role eligibilities, app role assignments, OAuth2 permission grants | The assignments (eligibilities: the Direct eligibility schedules) of the principals (grants: the clients) the declared resources of that type name (`orphan: retained`, see below). |
| PIM for Groups eligibilities | The Direct eligibility schedules of the target groups the declared resources name (`orphan: retained`, see below). |
| PIM active assignments (directory roles; PIM for Groups) | The time-bound Direct assignment schedules (`assignmentType` Assigned) of the declared principals (directory roles) or of the declared target groups (PIM for Groups), `orphan: retained`. Permanent schedules and activations of eligibilities are not listed: the permanent ones are `roleAssignments`' (which lists them itself) or the group's members / owners. |
| Applications, groups, users | Not listed: tenant-wide collections. An `Info` diagnostic `OrphanReportIncomplete` says so. |
| Authentication method configurations, PIM role management policies | Nothing: every method configuration and every role's (or onboarded group's) PIM policy always exists and is never created, so none can be an orphan (no listing, no diagnostic). |

Only types the template declares are looked at. A type is not listed (warning `OrphanReportIncomplete`) when one of its declared resources is not known in the run (a
deploy-time name or condition, a failed read or apply): its live object could otherwise be mistaken for an orphan. A resource the what-if plans as a Create whose key
waits only on objects the same run creates (an eligibility or assignment of a group the template creates: its principal id is deploy-time) is known: no live object
can carry that key yet, so it does not stop the listing for the other declared principals and no warning is shown. A resource whose condition is false declares
nothing, so its live object is listed (ARM's Complete mode deletes it; EARM only reports it).

**Retained assignments.** The identity of an assignment or grant is its tuple (principal, role or app role, scope / resource, consent type; for PIM for
Groups, group, principal and access). When the tuple of a declared assignment, eligibility or grant changes, the new one is created and the **old one is retained**:
it still grants access (an eligibility can still be activated). Both modes therefore list the live assignments, eligibilities and grants of the declared principals
(PIM for Groups: of the declared target groups) that no declared resource matches, as `orphan: retained` rows with the reason `retained: no longer declared; still grants access`
and an `AssignmentRetained` warning. Delete such an assignment yourself when the access should end.

Orphan rows are never written to and never count as changes: `--exit-code-on-changes` ignores them, and so does `HasChanges`.

`--no-pretty-print` writes the same document on one line. Member order is fixed (as listed) so the document diffs
cleanly between runs. The text is produced by `WhatIfJson.Serialize(document, prettyPrint)`: quotes and non-ASCII
are written raw (no `'` escapes), as the Azure CLI does for terminal and pipe output. `-o none` prints nothing (useful
with `--exit-code-on-changes`).

## CLI flags and exit codes

| Flag | Verb | Effect |
|---|---|---|
| `--exit-code-on-changes` | `what-if`, `create` | `what-if` / `create --what-if`: exit `5` when the what-if contains any `Create`, `Modify` or `Delete` row, or any edge add / remove (`WhatIfReport.HasChanges`). `NoChange` and `Ignore` rows (orphan report rows included) do not count. `create` (with or without `-c`): exit `5` when the deployment succeeded and applied such a change (`DeploymentResult.HasChanges`); a failure exits `1`. |
| `-r`, `--result-format FullResourcePayloads\|ResourceIdOnly` | `what-if`, `create` | Default `FullResourcePayloads`. `ResourceIdOnly` drops payloads and deltas from both formats and reports existing declared resources as `Deploy`. `create` also takes the Azure CLI's `--what-if-result-format`. |
| `-x`, `--exclude-change-types <type> ...` | `what-if`, `create` | Hides the named change types (`Create`, `Delete`, `Deploy`, `Ignore`, `Modify`, `NoChange`, `Unsupported`; case-insensitive) from the output; never changes what is deployed or the exit code. `create` also takes the Azure CLI's `--what-if-exclude-change-types`. |
| `--proceed-if-no-change` | `create` | With `-c`: no prompt when the rows shown are all `NoChange` / `Ignore` and no edge changes (the Azure CLI's test, on the filtered what-if); a replacement hidden by `-x` is still confirmed. |
| `--no-pretty-print` | `what-if`, `create` | Compact JSON (`-o json`). |
| `-w`, `--what-if` | `create` | Alias of `deployment what-if`: same output and exit codes, never writes. |
| `-c` / `--confirm-with-what-if` | `create` | Prints the what-if in the selected `-o` format, asks `Are you sure you want to execute the deployment? (y/n):` and applies the same plan on `y` / `yes`. With `-o json` standard output carries one document: the what-if is embedded in the deployment result as `whatIf` (or, when the prompt is declined, in a `Failed` document with code `NotConfirmed`), and the prompt shows the what-if as text on standard error. |
| `-y` / `--yes` | `create` | Skips the confirmation prompts: the one `-c` asks (the deployment is applied right after the what-if) and the one a plain `create` asks before it replaces objects (`Replace them? (y/n):`, after listing them on standard error). A plain `create` without replacements never prompts. Without `-y`, standard input must be interactive: without it the CLI prints `Standard input is not interactive; pass --yes to confirm.` and `Deployment not started.` to standard error and exits `1` without writing anything (`RecreateNotConfirmed` for a replacement). Answering anything but `y` / `yes` exits `1` the same way. |
| `--verbose` | global | Also prints the payload of `NoChange` rows, the `NoEffect` deltas and the skipped (condition-false) resources (and the usual debug logging on standard error). |

Exit codes of the what-if path: `0` no changes or changes without `--exit-code-on-changes`,
`5` changes with it, `2` validation failure (before anything is rendered), `3` auth / preflight / Key Vault reference,
`1` an unreadable template or parameter file, or a type without a provider. `create -c` continues with the `create` exit codes (`0` / `1`, or `5` with
`--exit-code-on-changes` when the applied run changed something) after the prompt.

## Golden fixtures

`tests/fixtures/whatif/` holds golden outputs generated from the engine against `FakeGraphClient` with the real
providers (`ProviderDiscovery.CreateRegistry`) plus `EchoProvider` for catalog types without a provider. Each
scenario is a template (`<scenario>.entra.json`), a directory seed (`<scenario>.seed.json`: entities to add per
collection and `$ref` navigation links to post) and the two expected renderings (`<scenario>.expected.txt`, `<scenario>.expected.json`, LF line endings,
UTF-8 without BOM). `scenarios.json` lists them with the deployment name, mode, tenant id, inline parameters and
result format; `tests/fixtures/whatif/README.md` has the seed format.

| Scenario | Covers |
|---|---|
| `create-incremental` | Create rows (application with nested object, arrays, keyed array), symbolic service principal id (`+?` + reason), securestring password (`***`), deploy-time edge targets, `manager` to an existing user, a condition-false resource (not a row: `skipped[]`), edge adds. |
| `modify-incremental` | Scalar, keyed-member (`['id']`) modify and create, nested object + array (`web.redirectUris`), group `members` add / remove, `manager` replaced, deploy-time `memberOf` (`~?`), an in-place `mailNickname` change next to an immutable `mailEnabled` change (`~!`, JSON `changeType: Unsupported`), a `NoChange` group, a Create group. |
| `modify-resource-id-only` | The same inputs rendered with `--result-format ResourceIdOnly`: the existing resources as `Deploy` (`!`, `!!`, `!?`). |
| `orphans-complete` | `--mode Complete` orphan report: a named location the template does not declare (`orphan: undeclared`), a role assignment of the declared principal whose tuple the template no longer declares (`orphan: retained`, `AssignmentRetained` warning), the `Orphans:` footer line; two Create rows; no Delete row. |
| `retained-incremental` | The same inputs in Incremental mode: only the retained assignment is listed. |
| `pim-eligibility-incremental` | PIM eligibility (`roleEligibilityScheduleRequests`): a `NoChange` row whose live end differs by 400 ms (1 s tolerance) and whose justification is metadata, an endDateTime `Modify` (adminUpdate, in place), an AfterDuration `Create` with the `role` sugar resolved offline and the default justification, and an undeclared eligibility of a declared principal reported as `orphan: retained` with its `AssignmentRetained` warning. |
| `pim-groups-incremental` | PIM for Groups eligibility (`privilegedAccess/groupEligibilityScheduleRequests`): a `NoChange` member row (400 ms end difference, `accessId` casing and justification ignored), an owner endDateTime `Modify` (adminUpdate), an AfterDuration owner `Create` through the `group` sugar (uniqueName resolved to `groupId`), and an undeclared member eligibility of the declared target group reported as `orphan: retained`. |
| `pim-active-incremental` | PIM active assignments (`roleAssignmentScheduleRequests`, `privilegedAccess/groupAssignmentScheduleRequests`): a directory role `NoChange` row (400 ms end difference, justification ignored), an AfterDuration role `Create`, a group membership endDateTime `Modify` (adminUpdate, in place), and the undeclared time-bound assignments of the declared principal and target group reported as `orphan: retained` (`active:` / `active member:`), while the seeded permanent assignments of the same principal and group are not listed. |
| `pim-policies-incremental` | PIM policy rules (`roleManagementPolicies`): a directory role policy (`role` sugar) whose activation duration and approval rule change (the approval stage, matched by position, shows only its added group approver as `~ [0]:`; undeclared rules and members are not shown), a policy named by `scopeType` / `scopeId` / `roleDefinitionId` that matches the live rule (`NoChange`), and a PIM for Groups member policy (`group` sugar) for a group PIM has not onboarded (the fake lists no policy before onboarding, as a real tenant does) that opts in to onboarding (`onboarding.temporaryEligiblePrincipalId`): a `Create` row with the declared rules, the `group not onboarded` note, the onboarding steps, and `PrivilegedEligibilitySchedule.ReadWrite.AzureADGroup` in the required permissions. |
| `auth-methods-incremental` | Authentication method configurations (tenant singletons): a FIDO2 AAGUID added to a set compared case-insensitively, a state and Temporary Access Pass changes, a certificate-based authentication default mode changed while the undeclared live binding rules are kept (no delta for them), a `NoChange` sms row, and the `DeprecatedAuthenticationMethodProperty` reason and warning for the legacy `keyRestrictions`. |

Regenerate them after an intentional rendering change with:

```powershell
pwsh -File tools/Update-WhatIfGoldens.ps1     # sets EARM_UPDATE_GOLDENS=1 and runs WhatIfGoldenTests in rewrite mode
pwsh -File test.ps1                           # confirm: the goldens now match
```

`WhatIfGoldenTests` (`tests/EArm.Tests/Engine`) loads each scenario from `scenarios.json`, loads the template with `TemplateLoader`,
seeds a `FakeGraphClient` from the seed file, plans with `PlanMode.WhatIf`
(tenant id and deployment name from the manifest, `EdgeKinds = ProviderDiscovery.CreateEdgeKinds(catalog)`), builds
`WhatIfReport.From(plan)` and compares `WhatIfText.Render` / `WhatIfJson.Serialize(WhatIfJson.Build(report, format), prettyPrint: true)`
against the expected files after normalising line endings. With `EARM_UPDATE_GOLDENS=1` it writes the files instead of comparing. Review the
diff of `tests/fixtures/whatif/*.expected.*` before committing it: a changed golden is a changed user-visible output.