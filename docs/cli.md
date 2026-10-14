# Command line reference

`entraarm` mirrors `az deployment group …`. Online verbs talk to Microsoft Graph through the **Azure CLI session** (`az rest`, launched
through PowerShell 7); offline verbs need neither a session nor a tenant.

```
entraarm
├── deployment
│   ├── validate        offline schema + expression validation
│   ├── what-if         preview the changes (read only)
│   ├── create          apply a template
│   ├── show            a deployment's state (and --refresh: drift)
│   ├── list            deployments recorded in the state directory
│   ├── delete          remove a deployment's state (and --delete-resources: its objects)
│   └── operation list  per-resource operations of the last run
├── permissions         Graph permissions and Entra roles a template needs (offline)
├── schema
│   ├── print           write a JSON Schema to stdout
│   └── install         map the schemas to *.entra.json in VS Code
└── state
    ├── unlock          break a stale state lock
    ├── forget          drop one resource from a state
    └── adopt           pin an existing object's id, or set uniqueName on it
```

`entraarm --version` prints the version; a bare `entraarm` prints help; every verb accepts `-?`, `-h` and `--help`.

## Global options

Valid on every verb (recursive).

| Option | Meaning |
|---|---|
| `-o`, `--output table\|json\|none` | Output format. Default `table`. `none` prints nothing (useful with `--exit-code-on-changes`). |
| `--pwsh-path <path>` | PowerShell 7.4+ executable. Default: `ENTRAARM_PWSH_PATH`, then `pwsh` on `PATH`. EntraARM launches `pwsh` and nothing else (never `cmd.exe`). |
| `--az-command <path>` | Azure CLI command or script to run instead of `az` (a test double). Default: `ENTRAARM_AZ_COMMAND`, then `az` on `PATH`. |
| `--temp-dir <dir>` | Directory for the short-lived request and body files (owner-only, zeroed and deleted after each call). Default: `ENTRAARM_TEMP_DIR`, then `%LOCALAPPDATA%\EntraArm\tmp`. |
| `--state-dir <dir>` | Directory of the state files. Default: `ENTRAARM_STATE_DIR`, then `.entraarm/state` next to the template (or under the current directory). |
| `--verbose` | Debug logging on stderr: every `az` call with URL parameters and headers redacted; bodies are never logged. |
| `--debug` | Trace logging on stderr: as `--verbose` plus the `az` argument vector and redacted stderr. |

Environment: `ENTRAARM_STATE_DIR`, `ENTRAARM_PWSH_PATH`, `ENTRAARM_AZ_COMMAND`, `ENTRAARM_TEMP_DIR`; colours (what-if) are off when
`NO_COLOR` (any non-empty value) or `AZURE_CORE_NO_COLOR` (`true`, `1`, `yes`, `on`) is set or the output is redirected.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success (for `what-if`: no changes, or changes without `--exit-code-on-changes`). |
| 1 | Deployment or operation failure; a template, parameter file or state file that cannot be loaded; a malformed argument; a type without a provider; a refusal (confirmation declined, non-interactive without `--yes`). |
| 2 | Validation failure: the template has errors (offline validation, expansion or the dependency graph). |
| 3 | Azure CLI, authentication, preflight or tenant failure: no usable `az` session, a Graph token that is rejected, a state or Key Vault reference for another tenant. |
| 4 | The deployment's state is locked by another run (`--lock-timeout` elapsed). |
| 5 | `what-if --exit-code-on-changes` found a Create, Modify or Delete (or an edge add or remove). |

## `deployment`

### Shared inputs of `validate`, `what-if`, `create`

| Option | Meaning |
|---|---|
| `-f`, `--template-file <path>` | The template (`*.entra.json`). Required. |
| `-p`, `--parameters <…>` | Repeatable. `@file.json` loads a parameter file ([format.md](format.md#parameters-and-parameter-files)); `name=value` sets one value. Later values win. Precedence: `-p name=value` > parameter file > `defaultValue`. |
| `-o`, `--output table\|json\|none` | Output format (a global option, valid on every verb). Default `table`. `none` prints nothing. |

### `deployment validate`

`entraarm deployment validate -f <template> [-p …] [-o table|json]`. There is no `-n`: validate is offline and has no deployment name. Offline: JSON Schema (envelope and per-type property schemas), the type
catalog, semantic checks, expression syntax and static evaluation (names, `dependsOn`, references, outputs). No tenant access. Exit 0 with
no errors (warnings allowed), 2 with errors, 1 when a file cannot be loaded. Every code is in [diagnostics.md](diagnostics.md).

### `deployment what-if` and `deployment create`

`what-if` reads the tenant and prints what `create` would do; it never writes and never locks the state. `create` applies the template in
dependency order, recording every resource in the state after each operation.

| Option | Verb | Meaning |
|---|---|---|
| `-n`, `--name <name>` | both | Deployment name (the state file name). Default: the template file name without `.entra.json`. Letters, digits, `.`, `_`, `-`; up to 128 characters. `validate` is offline, has no deployment name and does not accept `-n`. |
| `--mode Incremental\|Complete` | both | Incremental (default): create or update the declared resources; resources that vanished from the template are detached from the state with a warning. Complete: additionally **delete** the state-managed objects the template no longer declares, in reverse dependency order (security groups are deleted permanently, with a warning). |
| `--parallelism <n>` | both | Resources read or applied concurrently inside a dependency batch. Default 4. |
| `--allow-ca-enforce` | both | Allow Conditional Access policies with `state: enabled` (default: report-only or disabled only). |
| `--on-soft-deleted-conflict error\|restore` | both | What to do when a key to create is held by a soft-deleted object: fail with a hint (default), or restore it and continue. Nothing is ever purged. |
| `--result-format FullResourcePayloads\|ResourceIdOnly` | both | What-if detail. |
| `--exit-code-on-changes` | both | Exit 5 when the what-if contains a Create, Modify or Delete, or an edge change. |
| `--no-pretty-print` | both | Compact JSON for `-o json`. |
| `--what-if` | `create` | Alias of `deployment what-if`. |
| `-c`, `--confirm-with-what-if` | `create` | Print the what-if, ask `Are you sure you want to execute the deployment? (y/n):`, then apply the same plan. |
| `-y`, `--yes` | `create` | Do not prompt. **Required when standard input is not interactive.** |
| `--continue-on-error` | `create` | Keep deploying resources that do not depend on a failed one (default: stop at the batch boundary). |
| `--reveal-secure-outputs` | `create` | Print `securestring` / `secureObject` outputs in clear text instead of `***`. |
| `--secure-outputs-file <path>` | `create` | Write the secure outputs in clear text to a file with owner-only permissions. |
| `--force-unlock` | `create` | Break the deployment's state lock first. |
| `--lock-timeout <seconds>` | `create` | Wait for another run's lock this long (default 10), then exit 4. |

Before anything is planned, the CLI runs offline validation (exit 2), loads the state (exit 1 when unreadable), runs the preflight
(`az version`, `az account show`, `GET /organization`; exit 3), checks the state's tenant (exit 3 on a mismatch), resolves Key Vault parameter
references (exit 3) and, for `create`, takes the state lock (exit 4). Rendering is described in [whatif.md](whatif.md), the pipeline in
[engine.md](engine.md).

### `deployment show`, `list`, `delete`, `operation list`

| Verb | Syntax | Notes |
|---|---|---|
| `show` | `-n <name> [-f <template>] [--refresh]` | The state summary: last run, tenant, resources with their Graph ids. `--refresh` re-reads every object through its provider and reports drift (`InSync`, `Missing`, `Replaced`) without changing the state; needs a session (exit 3 on a tenant mismatch). |
| `list` | `[-f <template>]` | Every deployment with a state file in the state directory. |
| `delete` | `-n <name> [-f <template>] [-y] [--delete-resources] [--force-unlock] [--continue-on-error]` | Removes the state file; the objects stay in the tenant. `--delete-resources` first deletes every state-managed object in reverse dependency order (needs a session; a failure keeps the entry and exits 1). Asks for confirmation unless `-y`. |
| `operation list` | `-n <name> [-f <template>]` | The per-resource operations of the last run, from the state. |

`-f` only **locates** the state directory (`.entraarm/state` next to the template); `--state-dir` overrides it.

## `permissions`

`entraarm permissions -f <template> [-p …] [--mode app|delegated] [-o table|json]`. Offline. Lists the Graph permissions (and, for
`--mode delegated`, the Entra roles of which the user needs one per group) the template needs, grouped by permission with the resources that
need it and why. Exit 0; 2 when the template has validation errors; 1 when it cannot be loaded. Details and the manifest tables:
[permissions.md](permissions.md).

## `schema`

### `schema print`

`entraarm schema print [template|parameters|<type>]` writes an embedded JSON Schema to standard output (default `template`). `<type>` is
`Microsoft.Entra/applications`, the short form `applications`, a child type `applications/passwordCredentials`, or the file spelling
`conditionalAccess.policies`; case-insensitive. An unknown name lists the known types and exits 1.

```powershell
entraarm schema print template > deploymentTemplate.json
entraarm schema print Microsoft.Entra/conditionalAccess/policies
```

### `schema install`

`entraarm schema install [--global] [--workspace <dir>] [--settings-file <path>] [--schema-dir <dir>]`

1. **Extracts the schemas** to `--schema-dir` (default `%LOCALAPPDATA%\EntraArm\schemas\2026-10-01`; `~/.local/share/EntraArm/schemas/2026-10-01` on
   Linux and macOS). The source is the `schemas/2026-10-01` directory of the checkout when the workspace is inside the EntraARM repository
   (so schema edits are picked up by running the verb again), otherwise the schemas embedded in the tool. In the extracted
   `deploymentTemplate.json` the `$ref`s to the per-type schemas are rewritten to absolute `file:///` URIs, because the
   `https://entraarm.dev/schemas/…` ids are not published and VS Code must resolve everything locally. Files whose content is unchanged are not rewritten.
2. **Merges the mapping** into a settings file: `.vscode/settings.json` of `--workspace` (default: the current directory), the VS Code user settings
   with `--global` (`%APPDATA%\Code\User\settings.json`, `~/Library/Application Support/Code/User/settings.json`,
   `~/.config/Code/User/settings.json`), or `--settings-file`:

   ```json
   "json.schemas": [
     { "fileMatch": ["**/*.entra.json", "**/*.entra.jsonc"], "url": "file:///C:/Users/me/AppData/Local/EntraArm/schemas/2026-10-01/deploymentTemplate.json" }
   ]
   ```

   Other settings and other `json.schemas` entries are preserved; an existing EntraARM entry is updated in place (never duplicated); an
   up-to-date file is not rewritten, so the verb is idempotent. The file may contain comments and trailing commas (they are read, but cannot be
   preserved when the file is rewritten: the original is then saved as `settings.json.entraarm.bak` and a warning says so). The indentation of the
   existing file is kept.

Exit 0, or 1 when a file cannot be read or written, the settings are not valid JSON, or `--global` finds no VS Code user directory.

## `state`

| Verb | Syntax | Notes |
|---|---|---|
| `unlock` | `-n <name> [-f <template>]` | Breaks a stale lock (`<state-dir>/<name>.lock`) left by a run that died. Locks older than one hour are also taken over automatically. |
| `forget` | `-n <name> --resource-id <id> [-f <template>] [-y]` | Drops one resource from the state; the object stays in the tenant and is adopted again by the next deployment. Exit 4 when locked. |
| `adopt` | see below | Brings an existing object under a template. |

### `state adopt`

Two forms (exactly one per call). In this verb `-n` / `--deployment` is the deployment, and `--name` is the uniqueName.

**Pin an id (offline)**: `entraarm state adopt -n <deployment> --resource-id <id> --object-id <guid> [-f <template>] [--parent-object-id <guid>] [--key name=value …] [-y]`

Records the Graph object id under a resource id in the deployment's state (creating the state file when there is none), so a type that is looked up by
`displayName` (several matches: `AmbiguousMatch`) or found by composite properties (GUID-named types) binds to the intended object; the next
deployment reads it by that id and falls back to the key only if the id is gone. The state key follows the type's name kind; for a child type the parent must already be
in the state or be given with `--parent-object-id`; a GUID-named type (`appRoleAssignments`, `oauth2PermissionGrants`, `roleAssignments`) needs its
key properties as `--key principalId=<guid> --key …` so deletes and lookups can find the object. An id that is already pinned to another object asks for
confirmation (`-y` skips it). Exit 0; 1 on bad input or a refusal; 4 when the state is locked. Needs no Azure CLI.

```powershell
entraarm state adopt -n orders-prod --resource-id /providers/Microsoft.Entra/conditionalAccess/policies/CA-Orders-Admins-MFA --object-id 6f1b…
```

**Set a uniqueName (online)**: `entraarm state adopt --type Microsoft.Entra/applications|groups --id <objectId|appId> --name <uniqueName> [-y]`

`PATCH /applications/{id}` or `/groups/{id}` with `{ "uniqueName": … }` on a brownfield object, so a template can address it by that name
(`Prefer: create-if-missing` upserts then update it instead of creating a duplicate). The order is: argument checks, **preflight** (exit 3 without a
usable Azure CLI session), read the object (an application by object id, then by application id), **refuse** when a `uniqueName` is already
set and differs (it is immutable; an identical one is "nothing to do", exit 0), confirm (`-y` skips), PATCH. Exit 0; 1 on a refusal, a missing object or a Graph error
(a 403 names the permission); 3 on the preflight. Needs `Application.ReadWrite.All` (`.OwnedBy` for an owned application) or `Group.ReadWrite.All`, or a role that manages them.
