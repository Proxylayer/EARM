# What-if output

How `entraarm deployment what-if` (and `deployment create --what-if` / `-c`) report the changes a deployment would
make, in the human-readable text form and in the ARM-shaped JSON form (plan section 1.8). The renderers live in
`src/EntraArm.Engine/WhatIf/`; the CLI glue (`-o`, colours, `--no-pretty-print`, exit codes) in
`src/EntraArm.Cli/Output/WhatIfWriter.cs` and `src/EntraArm.Cli/Commands/DeploymentRunner.cs`.

Nothing in a what-if is written to the tenant or to the state. The what-if shown by `create -c` is the plan the
executor then applies (expansion, dependency graph and batches are computed once); the executor still re-reads each
resource right before writing it (docs/engine.md, pipeline step 6), so a confirmed what-if cannot go stale.

## Pipeline

```
DeploymentPlan (PlanMode.WhatIf)
   └─ WhatIfReport.From(plan)        rows in batch order (ties by resource id), then the mode-pass rows (by id);
        │                            ARM-shaped before/after payloads; secure values masked; edge counts; summary
        ├─ WhatIfText.Render(...)    -o table   (default)
        └─ WhatIfJson.Build(...)     -o json
```

`WhatIfReport` is the single model both renderers read, so the two outputs always agree on ordering, masking and
counts. Tests can build it from any plan and assert on `Rows`, `Summary`, `Edges` and `HasChanges`.

## Symbols

| Symbol | Meaning |
|---|---|
| `+` | Create: the object does not exist and will be created (`after` payload shown). |
| `~` | Modify: the object exists and differs (property deltas shown). Includes relationship-only changes (edge adds / removes under the declaring resource). |
| `-` | Delete: a state-managed object the template no longer declares, `--mode Complete` only (`before` payload shown). |
| `=` | NoChange: the object matches the template (id only; `--verbose` also prints the payload). |
| `*` | Ignore: nothing is done. Reasons: `condition evaluated to false`, `not in template; will be detached from state` (Incremental mode-pass row), `not in template and no longer in the tenant; will be detached from state`, or a condition that depends on a deploy-time value. |

A second character qualifies the row:

| Suffix | Meaning |
|---|---|
| `?` | A deploy-time value is involved: the resource id is still symbolic (its name depends on a resource that does not exist yet), a property is only known at deployment time, or an edge target is a placeholder. Such values render as `(deploy-time)`. |
| `!` | The change cannot be applied as planned (`unsupportedReason`): an immutable property differs on a type that cannot be recreated, a required property is missing, a read failed, a relationship could not be validated. The reason is printed on a `!` line under the row and counted in the footer. `!` wins over `?`. |

Secure values (`securestring` / `secureObject` parameters, tainted expression results, the type's `secure` overlay
paths such as `passwordProfile.password`, one-time secrets such as `secretText`) are always `***`, in payloads and on
both sides of a delta, in both output formats. The CLI never holds a what-if that contains a clear-text secret.

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
      ~ properties.mailNickname: "legacy" => "legacy-renamed"
      ! Immutable property properties.mailNickname of Microsoft.Entra/groups 'contoso-legacy' differs from the live object ...

  - /providers/Microsoft.Entra/groups/contoso-dev-retired [Microsoft.Entra/groups]
      name: "contoso-dev-retired"
      type: "Microsoft.Entra/groups"
      properties:
        displayName: "Retired"
      (not in template; Complete mode deletes it)

  = /providers/Microsoft.Entra/groups/contoso-dev-orders-ops [Microsoft.Entra/groups]

  * /providers/Microsoft.Entra/conditionalAccess/namedLocations/Contoso offices [Microsoft.Entra/conditionalAccess/namedLocations]
      (condition evaluated to false)

Resource changes: 1 to create, 3 to modify, 1 to delete, 1 unchanged, 1 ignored.
Edges: 2 to add, 0 to remove.
Required permissions: Application.ReadWrite.All, Group.ReadWrite.All, GroupMember.ReadWrite.All, User.ReadWrite.All.
1 change(s) cannot be applied as planned; see the rows marked '!'.

warning DestructiveDelete: '/providers/Microsoft.Entra/groups/contoso-dev-retired': this group is not a Microsoft 365 group ...
```

Rules:

- **Header**: `Deployment '<name>' (<mode>) on tenant <id>`; `(unknown)` when the tenant id is not known (offline).
- **Rows** are two-space indented: `<marker> <resourceId> [<resourceType>]`. Order: batch order of the dependency
  graph, ties broken by resource id (ordinal), then the mode-pass rows (Delete / Ignore of state entries not in the
  template) by resource id.
- **Create / Delete** print the `after` / `before` payload (six-space body indent): `name`, `type`, `apiVersion`
  (omitted for mode-pass rows, which have no template instance) and `properties`. Nested objects indent by two,
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
  block. `NoEffect` deltas (live-only sub-properties Graph refuses to null) are hidden unless `--verbose`.
- **Edge deltas** (plan section 1.4) appear under the declaring resource as `properties.<property>` array deltas with
  `+ [n]: "<objectId>"` / `- [n]: "<objectId>"` children (`manager` is a scalar delta). A `NoChange` object with edge
  deltas is a `Modify` row.
- **Reasons** are printed in parentheses under the row (symbolic name: `the name is only known once '<id>' exists`;
  immutable recreate; deploy-time properties; delete risks); the unsupported reason on a `!` line.
- **Footer**: `Resource changes: n to create, n to modify, n to delete, n unchanged, n ignored.`,
  `Edges: n to add, n to remove.` (edge entries created / removed, counted over the relationship-property deltas),
  `Required permissions: …` (sorted union of the providers' application permissions; `none.` when empty), then the
  unsupported count when non-zero, then a blank line and the diagnostics (warnings; `warning <Code>: <message>`)
  and the unresolved existing-resource lookups.
- **`--result-format ResourceIdOnly`** prints the rows (marker, id, type), the reasons and the footer only.
- **Colours**: ANSI only when standard output is a terminal (not redirected, not captured in-process) and neither
  `NO_COLOR` (any non-empty value) nor `AZURE_CORE_NO_COLOR` (`true` / `1` / `yes` / `on`) is set. Green `+`,
  magenta `~`, red `-`, grey `=` / `*`, cyan `?`, bold red `!`. No column padding, so lines wrap the same at any
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
    { "resourceId": "/providers/Microsoft.Entra/groups/contoso-dev-retired", "changeType": "Delete", "before": { "...": "..." },
      "unsupportedReason": null, "reasons": [ "not in template; Complete mode deletes it" ] },
    { "resourceId": "/providers/Microsoft.Entra/groups/contoso-dev-orders-ops", "changeType": "NoChange", "before": { "...": "..." }, "after": { "...": "..." }, "unsupportedReason": null },
    { "resourceId": "/providers/Microsoft.Entra/conditionalAccess/namedLocations/Contoso offices", "changeType": "Ignore", "unsupportedReason": null, "reasons": [ "condition evaluated to false" ] }
  ],
  "diagnostics": [ { "level": "Warning", "code": "DestructiveDelete", "target": null, "message": "..." } ],
  "requiredPermissions": [ "Application.ReadWrite.All", "Group.ReadWrite.All" ],
  "unresolvedReferences": [ { "resourceId": "/providers/Microsoft.Entra/users/cio@contoso.com", "apiVersion": "2026-10-01-preview", "reason": "..." } ],
  "summary": { "create": 1, "modify": 2, "delete": 1, "noChange": 1, "ignore": 1, "unsupported": 0, "edges": { "add": 1, "remove": 1 } }
}
```

| Member | Notes |
|---|---|
| `status` | Always `Succeeded` (a failed plan exits before any what-if is rendered, see exit codes). |
| `deploymentName`, `tenantId`, `mode` | `-n` (or the template stem), the signed-in tenant (`null` offline), `Incremental` / `Complete`. |
| `changes[]` | Same order as the text. `resourceId` is the ARM-style id; `changeType` one of `Create`, `Modify`, `Delete`, `NoChange`, `Ignore`. |
| `before` / `after` | `{ name, type, apiVersion?, properties }`. ARM semantics: Create has `after`, Delete has `before`, Modify and NoChange have both, Ignore has neither. `apiVersion` is absent on mode-pass rows (no template instance). `after.properties` includes the relationship properties; `before.properties` is the live object as the provider reads it (server-managed properties stripped). Absent with `--result-format ResourceIdOnly`. |
| `delta[]` | Modify rows only (ARM: Create / Delete carry only the payload). Each entry: `path`, `propertyChangeType` (`Create`, `Delete`, `Modify`, `Array`, `NoEffect`), `before` / `after` (omitted when null), `children[]`. Top-level paths are rooted at the resource (`properties.web.redirectUris`); child paths are relative to their parent (`[0]`, `['<memberKey>']`, `isEnabled`), as in the plan's example. Absent with `ResourceIdOnly`. |
| `unsupportedReason` | Always present on every change; `null` unless the change cannot be applied as planned. |
| `reasons[]` | **Extension.** Present when non-empty: the human-readable reasons printed in parentheses in the text (ignore reason, symbolic name, deploy-time values, recreate, delete risks) including the unsupported reason. |
| `diagnostics[]` | `level` (`Info` / `Warning` / `Error`), `code`, `target` (template path or `null`), `message`. Planning warnings (detached resources, destructive deletes, edge warnings) plus one `UnresolvedReference` warning per unresolved existing-resource lookup. |
| `requiredPermissions[]` | **Extension.** Sorted union of the Graph application permissions the plan needs. |
| `unresolvedReferences[]` | **Extension.** `reference(resourceId(...), apiVersion)` lookups that could not be performed: `resourceId`, `apiVersion`, `reason`. |
| `summary` | **Extension.** Counts per change type plus `unsupported` (rows carrying an `unsupportedReason`, already counted in their change type) and `edges` (`add` / `remove`). |

`--no-pretty-print` writes the same document on one line. Member order is fixed (as listed) so the document diffs
cleanly between runs. The text is produced by `WhatIfJson.Serialize(document, prettyPrint)`: quotes and non-ASCII
are written raw (no `'` escapes), as `az` does for terminal and pipe output. `-o none` prints nothing (useful
with `--exit-code-on-changes`).

## CLI flags and exit codes

| Flag | Verb | Effect |
|---|---|---|
| `--exit-code-on-changes` | `what-if`, `create --what-if` | Exit `5` when the what-if contains any `Create`, `Modify` or `Delete` row, or any edge add / remove (`WhatIfReport.HasChanges`). `NoChange` and `Ignore` rows (including "will be detached" rows in Incremental mode) do not count. |
| `--result-format FullResourcePayloads\|ResourceIdOnly` | `what-if`, `create` | Default `FullResourcePayloads`. `ResourceIdOnly` drops payloads and deltas from both formats. |
| `--no-pretty-print` | `what-if`, `create` | Compact JSON (`-o json`). |
| `--what-if` | `create` | Alias of `deployment what-if`: same output and exit codes, never locks or writes the state. |
| `-c` / `--confirm-with-what-if` | `create` | Prints the what-if in the selected `-o` format, asks `Are you sure you want to execute the deployment? (y/n):` and applies the same plan on `y` / `yes`. With `-o json` the what-if JSON precedes the prompt on standard output. |
| `-y` / `--yes` | `create` | Skips the prompt. Required when standard input is not interactive: without it the CLI prints `Standard input is not interactive; pass --yes to confirm.` and `Deployment not started.` to standard error and exits `1` without writing anything. Answering anything but `y` / `yes` exits `1` the same way. |
| `--verbose` | global | Also prints the payload of `NoChange` rows and the `NoEffect` deltas (and the usual debug logging on standard error). |

Exit codes of the what-if path (plan section 1.9): `0` no changes or changes without `--exit-code-on-changes`,
`5` changes with it, `2` validation failure (before anything is rendered), `3` auth / preflight / tenant mismatch,
`1` unreadable state or a type without a provider. `create -c` continues with the `create` exit codes (`0` / `1`)
after the prompt.

## Golden fixtures

`tests/fixtures/whatif/` holds golden outputs generated from the engine against `FakeGraphClient` with the real
providers (`ProviderDiscovery.CreateRegistry`) plus `EchoProvider` for catalog types without a provider. Each
scenario is a template (`<scenario>.entra.json`), a directory seed (`<scenario>.seed.json`: entities to add per
collection and `$ref` navigation links to post), an optional state file (`<scenario>.state.json`, the on-disk state
shape) and the two expected renderings (`<scenario>.expected.txt`, `<scenario>.expected.json`, LF line endings,
UTF-8 without BOM). `scenarios.json` lists them with the deployment name, mode, tenant id, inline parameters and
result format; `tests/fixtures/whatif/README.md` has the seed format.

| Scenario | Covers |
|---|---|
| `create-incremental` | Create rows (application with nested object, arrays, keyed array), symbolic service principal id (`+?` + reason), securestring password (`***`), deploy-time edge targets, `manager` to an existing user, condition-false `Ignore` row, edge adds. |
| `modify-incremental` | Scalar, keyed-member (`['id']`) modify and create, nested object + array (`web.redirectUris`), group `members` add / remove, `manager` replaced, deploy-time `memberOf` (`~?`), immutable `mailNickname` change (`~!`), a `NoChange` group, a Create group. |
| `modify-resource-id-only` | The same inputs rendered with `--result-format ResourceIdOnly`. |
| `delete-complete` | `--mode Complete` with a state entry the template dropped: `-` row with the `before` payload, delete-risk reason and `DestructiveDelete` warning; a `NoChange` row. |
| `detach-incremental` | The same state in Incremental mode: `*` "will be detached" row, `ResourceDetached` warning after the footer, `HasChanges == false`. |

Regenerate them (after an intentional rendering change) with the WI-17 harness:

```powershell
# from the harness directory (scratchpad/harness-wi17): builds against src/EntraArm.Providers and tests/EntraArm.TestSupport
dotnet build Harness.csproj
dotnet bin/Debug/net10.0/harness-wi17.dll C:\Users\mrjon\source\EntraARM
```

The harness rewrites every fixture under `tests/fixtures/whatif/`, prints each rendering and checks the decisive
lines (symbols, masking, counts, exit codes through the built CLI with `tests/fake-az/fake-az.ps1`). Golden tests
should load the template with `TemplateLoader`, seed a `FakeGraphClient` from the seed file, deserialize the state
with `StateSerializer.Deserialize`, plan with `PlanMode.WhatIf` (tenant id and deployment name from the manifest,
`EdgeKinds = ProviderDiscovery.CreateEdgeKinds(catalog)`), build `WhatIfReport.From(plan)` and compare
`WhatIfText.Render` / `WhatIfJson.Serialize(WhatIfJson.Build(report, format), prettyPrint: true)` against the
expected files after normalising line endings.
