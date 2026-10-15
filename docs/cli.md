# Command line reference

`earm` mirrors `az deployment group …`. Online verbs talk to Microsoft Graph over HTTPS with a token of the identity EARM is signed in as (see [Signing in](#signing-in)); offline verbs need neither a sign-in nor a tenant. EARM starts no external process: no Azure CLI, no PowerShell.

```
earm
├── deployment
│   ├── validate        offline schema + expression validation
│   ├── what-if         preview the changes (read only)
│   ├── create          apply a template
│   ├── show            a deployment's state (and --refresh: drift)
│   ├── list            deployments recorded in the state directory
│   ├── delete          delete the objects a template declares (by live key)
│   └── operation list  per-resource operations of the last run
├── auth
│   ├── login           sign in interactively (browser or device code)
│   ├── logout          forget the interactive sign-in
│   └── status          who EARM is signed in as
├── permissions         Graph permissions and Entra roles a template needs (offline)
├── schema
│   ├── print           write a JSON Schema to stdout
│   └── install         map the schemas to *.entra.json in VS Code
└── state
    ├── unlock          break a stale state lock
    ├── forget          drop one resource from a state
    ├── set-provenance  mark one state entry created or adopted
    └── adopt           pin an existing object's id, or set uniqueName on it
```

`earm --version` prints the version; a bare `earm` prints help; every verb accepts `-?`, `-h` and `--help`.

## Global options

Valid on every verb (recursive).

| Option | Meaning |
|---|---|
| `-o`, `--output table\|json\|none` | Output format. Default `table`. `none` prints nothing (useful with `--exit-code-on-changes`). |
| `--auth auto\|default\|interactive` | How tokens are obtained. Default: `EARM_AUTH`, then `auto` (a cached `earm auth login` sign-in when there is one, else the default credential chain). |
| `--tenant <id\|domain>` | Tenant to sign in to. Default: `EARM_TENANT_ID`. |
| `--client-id <guid>` | App registration (public client) used by `earm auth login`. Default: `EARM_CLIENT_ID`, then the Azure.Identity developer client. |
| `--state-dir <dir>` | Directory of the state files. Default: `EARM_STATE_DIR`, then `.earm/state` next to the template (or under the current directory). |
| `--verbose` | Debug logging on stderr: every Graph call (method, path, status, timing, request id; secret-looking query values masked); tokens, Authorization headers and bodies are never logged. |
| `--debug` | Trace logging on stderr: as `--verbose` plus token acquisition and retry details (never the tokens). Also prints the full exception (with stack trace) of an unexpected error; `EARM_DEBUG=1` does the same. |
| `--evaluation-timeout <seconds>` | Wall-clock budget of one evaluation (a variable, the properties of a resource, an output; the time between evaluations, such as network calls, never counts). Default: `EARM_EVALUATION_TIMEOUT`, then 60. `0` disables the time limit (the step limit of [format.md](format.md#evaluation-limits) still applies); the largest value, for the flag and the variable alike, is 86,400 (24 hours). A value that is not a number of seconds in that range (`abc`, `NaN`, `Infinity`, a negative or larger number) is a one-line usage error (exit 1), never an "Unexpected error"; an invalid `EARM_EVALUATION_TIMEOUT` is ignored. An exceeded budget is the diagnostic `EvaluationBudgetExceeded`. |

Environment: `EARM_STATE_DIR`, `EARM_AUTH`, `EARM_TENANT_ID`, `EARM_CLIENT_ID`, `EARM_EVALUATION_TIMEOUT`, `EARM_DEBUG`, `EARM_KEYVAULT_ALLOWED` (comma-separated vault names, case-insensitive: when set, a Key Vault parameter reference to any other vault is refused before a token is requested), `EARM_CLOUD` (`public` (default), `usgov`, `usgovdod` or `china`: selects the Graph host, the Entra authority and the Key Vault suffix from a fixed table; there is no way to name another endpoint), plus the `AZURE_*` variables of Azure.Identity (see [Signing in](#signing-in)); colours (what-if) are off when
`NO_COLOR` (any non-empty value) or `AZURE_CORE_NO_COLOR` (`true`, `1`, `yes`, `on`) is set or the output is redirected.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success (for `what-if`: no changes, or changes without `--exit-code-on-changes`). |
| 1 | Deployment or operation failure; a template, parameter file or state file that cannot be loaded; a malformed argument or a usage error; a type without a provider; a refusal (confirmation declined, non-interactive without `--yes`, a replacement that was not confirmed: `RecreateNotConfirmed`); an **unexpected error**, printed as one line `Unexpected error (<type>): <message>` (the full exception only with `--debug` / `EARM_DEBUG=1`), a file access problem (`Access denied: ...`, `A file operation failed ...`) or running out of memory. |
| 2 | Validation failure: the template has errors (offline validation, expansion or the dependency graph). |
| 3 | Authentication, preflight or tenant failure: no credential signs in, a Graph token that is rejected, a state for another tenant, an unreadable Key Vault reference. |
| 4 | The deployment's state is locked by another run (`--lock-timeout` elapsed). |
| 5 | `what-if --exit-code-on-changes` found a Create, Modify or Delete (or an edge add or remove). Orphan report rows never count: they are reported, never written. |

## Output, errors and prompts

- **Standard output** carries the result of the verb (`-o json` is one JSON document); **standard error** carries messages, logs, usage errors
  **and the help that follows a usage error**, and the confirmation prompts (`Are you sure ...? (y/n):`), so `-o json` output can always be parsed.
  `--help` and a bare `earm` print help on standard output with exit 0.
- With `-o json` **every verb prints exactly one JSON document on standard output, on success and on failure**: the report of the verb, or for a failure that has
  no report of its own (`auth login`, `logout`, `status`, `schema print`, `schema install`, and a command line that does not parse) `{ "status": "Failed",
  "diagnostics": [ { "severity": "Error", "code": ..., "message": ... } ] }` next to the message on standard error (codes `AuthenticationFailed`, `InvalidArgument`).
  A message of several lines (the credentials an unsuccessful `auth status` tried) keeps them: `message` joins them with a line feed and `lines` lists them.
- **Untrusted text is made safe.** Names, property keys and values come from templates and tenants and are echoed into messages. Everything written
  to either stream passes one filter: control characters (line breaks inside a value, ESC and the terminal sequences built on it, BEL, DEL, the C1
  controls), the line and paragraph separators and the bidirectional overrides are written as visible escapes (a backslash and `n`, `u001b`,
  `u202e`); text a CI log parser would run as a command is broken so it cannot: the second `#` of `##vso[` and `##[` is written as `\u0023` wherever the
  token appears in a line (`#\u0023vso[`; an Azure Pipelines agent finds it mid-line, and in `-o json` it is an ordinary JSON escape that decodes back to `#`), and a `::` that is
  the first thing on a line, after any whitespace (a GitHub Actions runner trims it), gets an apostrophe in front. Only the colour codes of the what-if text pass through (and only on a terminal). JSON output is unaffected (JSON escapes control
  characters itself), but messages inside it are escaped the same way.
- **Unexpected errors** never print a stack trace: the line is `Unexpected error (<exception type>): <message>`, with secrets scrubbed, control
  characters escaped and the message cut to 500 characters, exit code 1. `--debug` (or `EARM_DEBUG=1`) adds the full exception. File and permission
  problems get a plain sentence (`Access denied: ...`, `A file operation failed (...): ...`), and running out of memory is a one-line message; both exit 1.

## Signing in

EARM obtains tokens with Azure.Identity; nothing is delegated to a command-line tool. `--auth` (or `EARM_AUTH`) chooses:

| Mode | Behaviour |
|---|---|
| `auto` (default) | The cached sign-in of `earm auth login` when one exists, otherwise `default`. There is **no fallback**: when a cached sign-in exists but cannot produce a token silently (expired or revoked refresh token), the run fails with "your interactive sign-in expired: run `earm auth login`, or pass `--auth default`" instead of quietly acting as a different identity. A record that belongs to another cloud is ignored (logged). |
| `default` | `DefaultAzureCredential` restricted to credentials that start no process: **environment** (`AZURE_TENANT_ID` + `AZURE_CLIENT_ID` + `AZURE_CLIENT_SECRET`, or `AZURE_CLIENT_CERTIFICATE_PATH`, or `AZURE_USERNAME`/`AZURE_PASSWORD`), **workload identity** (`AZURE_FEDERATED_TOKEN_FILE`) and **managed identity** (`AZURE_CLIENT_ID` selects a user-assigned one). The Azure CLI, Azure Developer CLI, Azure PowerShell, Visual Studio and Visual Studio Code credentials are excluded. `AZURE_TOKEN_CREDENTIALS` is refused unless it is `prod` or one of the three credentials above. |
| `interactive` | Only the cached sign-in; fails with "run `earm auth login`" when there is none. |

The identity of the first token (tenant id, object id, app id, user principal name or app id) is logged once at Information level (shown by `--verbose`); the token itself never is.
An unknown `--auth`, `--tenant`, `--client-id` or `EARM_CLOUD` value is a plain failure (exit 1) whose message names the setting and lists the valid values.

### `earm auth login [--device-code] [--tenant <id>] [--client-id <guid>]`

Signs in as a user: a browser window by default, `--device-code` on a machine without one. The tokens go to a persistent **encrypted** token cache named
`earm` (DPAPI on Windows, Keychain on macOS, the secret service on Linux); unencrypted storage is never enabled, and when the platform cannot protect the cache the login
fails with a message that says so. The non-secret account record is stored owner-only at `%LOCALAPPDATA%\EArm\auth\record.json` (`$XDG_DATA_HOME/earm/auth/record.json`
elsewhere). The default app registration is the Azure.Identity developer client, whose tokens only carry the Graph permissions it is pre-authorized for; to use the
administrative scopes EARM needs (see [permissions.md](permissions.md)), register your own public-client application with the needed **delegated** permissions, a
`http://localhost` redirect URI (and "allow public client flows" for `--device-code`), and pass its id with `--client-id` / `EARM_CLIENT_ID`. Exit 0; 3 when the sign-in fails.
`login` never waits for a browser that nobody can use: with **standard input and output both redirected** (a script, a pipeline) and no `--device-code` it fails at once
(exit 1) and says to add `--device-code`; with `--auth default` (or `EARM_AUTH=default`) it fails at once too, because the default credential chain (environment variables,
workload identity, managed identity) signs nobody in: `login` is only for interactive sign-in.

### `earm auth logout`

Deletes the sign-in record and the files of the `earm` token cache. Service principal, workload identity and managed identity sign-ins come from the environment and are not affected.

### `earm auth status`

Acquires a token the way an online verb would and prints the mode, cloud, tenant, principal (user principal name, or app id for an app-only token), object id, delegated scopes or
application roles and the expiry, read from the token's claims **without validating** them (display only; the token is never printed). Exit 3 with the sign-in guidance
(the credentials that were tried and how to configure one) when nothing signs in.

### Migrating from the Azure CLI transport

Earlier previews ran `az rest` through PowerShell. That is gone: `--az-command`, `--pwsh-path`, `--temp-dir` and the environment variables `EARM_AZ_COMMAND`,
`EARM_PWSH_PATH` and `EARM_TEMP_DIR` were removed, and an existing `az login` session is **no longer used**. Sign in with `earm auth login` or the `AZURE_*` variables instead; Azure CLI and
PowerShell are no longer prerequisites (PowerShell remains only for the repository's own `build.ps1`, `test.ps1`, `deploy.ps1` and `tools/*.ps1`).
## `deployment`

### Shared inputs of `validate`, `what-if`, `create`

| Option | Meaning |
|---|---|
| `-f`, `--template-file <path>` | The template (`*.entra.json`). Required. |
| `-p`, `--parameters <…>` | Repeatable, **one value per `-p`** (on every command that takes it, and `--key` of `state adopt` likewise: a stray token such as `-p @f.json -n x` is a usage error naming the unknown option instead of being swallowed as another value). `@file.json` loads a parameter file ([format.md](format.md#parameters-and-parameter-files)); `name=value` sets one value. In PowerShell write the file form in single quotes, `-p '@file.json'`: a bare `@` there is the splatting operator and a parse error. Later values win. Precedence: `-p name=value` > parameter file > `defaultValue`. |
| `-o`, `--output table\|json\|none` | Output format (a global option, valid on every verb). Default `table`. `none` prints nothing. |

### `deployment validate`

`earm deployment validate -f <template> [-p …] [-o table|json]`. There is no `-n`: validate is offline and has no deployment name. Offline: JSON Schema (envelope and per-type property schemas), the type
catalog, semantic checks, expression syntax and static evaluation (names, `dependsOn`, references, outputs, and every expression inside `properties`, which is evaluated far enough to report an undefined variable or parameter and an unknown function). No tenant access. Exit 0 with
no errors (warnings allowed), 2 with errors, 1 when a file cannot be loaded. Every code is in [diagnostics.md](diagnostics.md). At most 50 diagnostics of one code and severity are listed; the rest are summarised in one line (`suppressedCount` and `suppressed[]` in JSON, the counts stay exact).

### `deployment what-if` and `deployment create`

`what-if` reads the tenant and prints what `create` would do; it never writes and never locks the state. `create` applies the template in
dependency order, recording every resource in the state after each operation.

| Option | Verb | Meaning |
|---|---|---|
| `-n`, `--name <name>` | both | Deployment name (the state file name). Default: the template file name without `.entra.json`. Letters, digits, `.`, `_`, `-`; up to 128 characters. `validate` is offline, has no deployment name and does not accept `-n`. |
| `--mode Incremental\|Complete` | both | Incremental (default): create or update the declared resources; nothing else is touched. Complete: the same writes, plus a read-only **orphan report** of the live objects of the declared types that the template does not declare (bounded per type: see [whatif.md](whatif.md#orphan-report)). Complete mode **never deletes**; to remove declared objects use `deployment delete`. Both modes report the retained role / app role assignments and grants of the principals the template declares (`orphan: retained`). |
| `--parallelism <n>` | both | Resources read or applied concurrently inside a dependency batch. Default 4. At least 1: a smaller value is a usage error (exit 1). |
| `--allow-ca-enforce` | both | Allow Conditional Access policies with `state: enabled` (default: report-only or disabled only). |
| `--on-soft-deleted-conflict error\|restore` | both | What to do when a key to create is held by a soft-deleted object: fail with a hint (default), or restore it and continue. Nothing is ever purged. |
| `--result-format FullResourcePayloads\|ResourceIdOnly` | both | What-if detail. |
| `--exit-code-on-changes` | both | Exit 5 when the what-if contains a Create, Modify or Delete, or an edge change. |
| `--no-pretty-print` | both | Compact JSON for `-o json`. |
| `--what-if` | `create` | Alias of `deployment what-if`. |
| `-c`, `--confirm-with-what-if` | `create` | Print the what-if, ask `Are you sure you want to execute the deployment? (y/n):`, then apply the same plan. |
| `-y`, `--yes` | `create` | Skip the confirmations: the one `--confirm-with-what-if` asks (the deployment is applied right after the what-if), and the one a plain `create` asks before it **replaces** objects (an immutable property changed: delete, then create again). Without `-y`, standard input must be interactive for either question; a non-interactive `create` that would replace objects fails before any write (`RecreateNotConfirmed`, exit 1). A plain `create` without replacements never prompts. |
| `--continue-on-error` | `create` | Keep deploying resources that do not depend on a failed one (default: stop at the batch boundary). |
| `--reveal-secure-outputs` | `create` | Print `securestring` / `secureObject` outputs in clear text instead of `***`. |
| `--secure-outputs-file <path>` | `create` | Write the secure outputs in clear text to a file with owner-only permissions. The file is checked **before** anything is deployed (a directory, a missing folder, a read-only file or a symbolic link stops the run: exit 1, "Deployment not started."). One-time secrets (a password credential's `secretText`, a generated user password) are written to it **immediately** after the apply that produced them, under the reserved top-level key `oneTimeSecrets` (by resource id), so a crash, Ctrl+C or failed state save cannot lose them. A rerun **merges** into an existing file (keys that are not superseded stay) and never replaces it with a smaller one; a run with no secure values does not touch the file at all. |
| `--force-unlock` | `create` | Break the deployment's state lock first. |
| `--lock-timeout <seconds>` | `create` | Wait for another run's lock this long (default 10; zero or more, a negative value is a usage error), then exit 4. |

Before anything is planned, the CLI runs offline validation (exit 2) and, for `create`, checks `--secure-outputs-file` (exit 1), then takes the state
lock (exit 4), **then** loads the state (exit 1 when unreadable: a symbolic link or junction as state directory, as **any folder above it** or as state file, a state file that is not valid or over 16 MiB, a
Graph id that is not a GUID, an entry whose `key` is not the key its resource id implies or whose recorded relationship ids are malformed: `StateEntryInvalid`, repair with `state forget`), runs the preflight (acquires a token, reads its tenant and principal, one `GET /organization`; exit 3), checks the state's tenant
(exit 3 on a mismatch) and resolves Key Vault parameter references (exit 3). `what-if` never locks: it reads the state as an unlocked snapshot and says so on
stderr. Rendering is described in [whatif.md](whatif.md), the pipeline in [engine.md](engine.md).

**Replacements need confirmation.** A `create` that would replace objects (an immutable property changed on a type that is deleted and created again:
assignments, grants, credentials, named location kinds...) lists them on stderr (resource id, type, Graph id) and asks `Replace them? (y/n):` unless `-y`.
To know them before anything is written, a `create` without `-y` reads every resource first (the same read-only pass as `what-if`; not printed unless
`-c`, where the what-if rows are the list and its prompt is the confirmation). On a non-interactive standard input without `-y` it stops with exit 1,
`RecreateNotConfirmed` and "Deployment not started.". A replacement the run only discovers while it runs (its input resolved during the run) and that was not
confirmed fails that resource without deleting anything. The signed-in principal and service principals owned by another organization are never replaced
(`ProtectedObject`). **Nothing the template does not declare is ever deleted** by `create`, in either mode.

The deployment result (`-o json`) carries `orphans[]`: `{ "resourceId", "type", "graphId", "orphan": "undeclared" | "retained", "reason" }` for the live objects of
the orphan report (the table output lists them under "Not declared in the template (reported only; never deleted)"). Orphans do not affect the status or the
exit code. The former `detached` and `deleted` counts are gone.

With `-o json` the standard output of a verb that fails **before** it has anything else to report (a preflight failure, exit 3; an unreadable state; a state lock, exit 4; a replacement refusal of a plain `create`) is **one JSON document** `{ "status": "Failed", "diagnostics": [ { "severity": "Error", "code": "...", "message": "..." } ] }` instead of prose on stderr; a multi-line message uses `\n` line breaks. A decline of a confirmation prompt, and a refusal after the what-if document of `-c` was written, stay on stderr (the document already on stdout is the one document).

### `deployment delete`

`earm deployment delete -f <template> [-p …] [-n <name>] [-y] [--continue-on-error] [--parallelism <n>]` deletes the objects the template declares. They are
found exactly as `what-if` finds them, **by their live keys** (no state is involved; `-n` is only a label), and deleted in **reverse dependency order**
(the reverse of the dependency batches: children and assignments before their parents). Resources whose condition is false, whose name is only known at
deployment time or that do not exist are skipped. The list (resource id, type, `displayName`, Graph id, `[kept: protected]`, a delete-risk note such as "deleted
permanently") goes to stderr with `Delete these <n> object(s)? (y/n):`; `-y` skips the question, and a non-interactive standard input without `-y` deletes nothing
(exit 1, "Nothing deleted."). Every object is re-read by its key right before it is deleted and must be the one that was listed. The signed-in principal and service
principals owned by another organization (Microsoft first-party or multi-tenant apps) are **never deleted**: they are reported as kept, which is not a failure.
A failure stops the rest (reported as skipped) unless `--continue-on-error`. Exit 0 when every listed object was deleted, already gone or kept; 1 when a delete
failed, the confirmation was declined or the template cannot be loaded; 2 on validation errors; 3 when no credential signs in. A declared object that could not be
read is a failure of that object.

With `-o json`: `{ "status", "deployment", "message", "deleted", "alreadyGone", "kept", "failed", "skipped", "outcomes": [ { "resourceId", "type", "graphId", "status", "error", "errorCode" } ] }`
(`status` of an outcome: `Deleted`, `AlreadyGone`, `Refused`, `Failed`, `Skipped`); nothing to delete is `{ "status": "Succeeded", "message": "Nothing to delete: ...", "deployment", "deleted": 0 }`.
The deployment's state file is not read or changed.

### `deployment show`, `list`, `operation list`

| Verb | Syntax | Notes |
|---|---|---|
| `show` | `-n <name> [-f <template>] [--refresh]` | The state summary: last run, tenant, resources with their Graph ids and their provenance (`created` or `adopted`; JSON: `resources.<id>.provenance`). `--refresh` re-reads every object through its provider and reports drift (`InSync`, `Missing`, `Replaced`) without changing the state; needs a session (exit 3 on a tenant mismatch). |
| `list` | `[-n <name>] [-f <template>]` | Every deployment with a state file in the state directory. `-n` keeps only the deployment of that name (case-insensitive exact match); an unknown name lists nothing (an empty `deployments` array with `-o json`), prints `No deployment named '<name>' ...` on stderr and still exits 0. A state file that cannot be read shows its error on its own row. |
| `operation list` | `-n <name> [-f <template>]` | The per-resource operations of the last run, from the state. |

With `-o json` every one of these verbs (and the `state` verbs below) writes **exactly one JSON document**, on success and on failure: a failure (unknown deployment `DeploymentNotFound`, an unreadable state, a tenant mismatch, a failed preflight) is `{ "status": "Failed", "diagnostics": [ { "severity", "code", "message" } ] }` with the same exit code as the table output.

`-f` only **locates** the state directory (`.earm/state` next to the template); `--state-dir` overrides it.

### Migrating from the old name

State files record each resource's `apiVersion`. The `apiVersion` relabel (`2026-10-01` became `2026-10-01-preview`, and the old `2026-10-01-preview` beta became `2026-10-01-preview.beta`) has no compatibility shim because the format is unreleased: **state files written before the rename must be deleted** before the next run (the objects stay in the tenant; `state adopt` re-pins them). The product rename (`entraarm` to `earm`) likewise has no shim: the state directory moved from `.entraarm/` to `.earm/` (and `%LOCALAPPDATA%\EntraArm` to `%LOCALAPPDATA%\EArm`, `ENTRAARM_*` to `EARM_*`), so delete or move an old `.entraarm/` directory (the objects stay in the tenant; `state adopt` re-pins them) and remove the old `entraarm` global tool (`dotnet tool uninstall -g entraarm`).

## `permissions`

`earm permissions -f <template> [-p …] [--mode app|delegated] [-o table|json]`. Offline. Lists the Graph permissions (and, for
`--mode delegated`, the Entra roles of which the user needs one per group) the template needs, grouped by permission with the resources that
need it and why. Exit 0; 2 when the template has validation errors; 1 when it cannot be loaded. Details and the manifest tables:
[permissions.md](permissions.md).

The list covers both deployment modes: Complete mode never deletes, it only reads the declared types for its orphan report, which the permissions
above already allow. `deployment delete` needs the same permissions as managing the declared types.

## `schema`

### `schema print`

`earm schema print [template|parameters|<type>]` writes an embedded JSON Schema to standard output (default `template`). `<type>` is
`Microsoft.Entra/applications`, the short form `applications`, a child type `applications/passwordCredentials`, or the file spelling
`conditionalAccess.policies`; case-insensitive. An unknown name lists the known types and exits 1 (with `-o json`: a failure document on standard output).

```powershell
earm schema print template > deploymentTemplate.json
earm schema print Microsoft.Entra/conditionalAccess/policies
```

### `schema install`

`earm schema install [--global] [--workspace <dir>] [--settings-file <path>] [--schema-dir <dir>]`

With `-o json` the text is replaced by one document: `{ "status": "Succeeded", "schemaDirectory", "files", "written", "source": "embedded"|"checkout", "checkout", "settingsFile", "settings": "created"|"updated"|"unchanged", "settingsBackup", "url", "fileMatch" }` (on failure the failure document described under [Output, errors and prompts](#output-errors-and-prompts)).

1. **Extracts the schemas** to `--schema-dir` (default `%LOCALAPPDATA%\EArm\schemas\2026-10-01`; `~/.local/share/EArm/schemas/2026-10-01` on
   Linux and macOS). The source is the `schemas/2026-10-01` directory of the checkout when the workspace is inside the EARM repository
   (so schema edits are picked up by running the verb again), otherwise the schemas embedded in the tool. In the extracted
   `deploymentTemplate.json` the `$ref`s to the per-type schemas are rewritten to absolute `file:///` URIs, because the
   `https://schema.earm.proxylayer.net/schemas/…` ids are not published and VS Code must resolve everything locally. Files whose content is unchanged are not rewritten.
2. **Merges the mapping** into a settings file: `.vscode/settings.json` of `--workspace` (default: the current directory), the VS Code user settings
   with `--global` (`%APPDATA%\Code\User\settings.json`, `~/Library/Application Support/Code/User/settings.json`,
   `~/.config/Code/User/settings.json`), or `--settings-file`:

   ```json
   "json.schemas": [
     { "fileMatch": ["**/*.entra.json", "**/*.entra.jsonc"], "url": "file:///C:/Users/me/AppData/Local/EArm/schemas/2026-10-01/deploymentTemplate.json" }
   ]
   ```

   Other settings and other `json.schemas` entries are preserved; an existing EARM entry is updated in place (never duplicated); an
   up-to-date file is not rewritten, so the verb is idempotent. The file may contain comments and trailing commas (they are read, but cannot be
   preserved when the file is rewritten: the original is then saved as `settings.json.earm.bak` and a warning says so). The indentation of the
   existing file is kept.

Exit 0, or 1 when a file cannot be read or written, the settings are not valid JSON, or `--global` finds no VS Code user directory.

## `state`

| Verb | Syntax | Notes |
|---|---|---|
| `unlock` | `-n <name> [-f <template>]` | Breaks a stale lock (`<state-dir>/<name>.lock`) left by a run that died. A lock whose holder is a process of this host that no longer runs is taken over automatically **at once** (no waiting); any other lock older than one hour without a heartbeat is also taken over automatically. A lock is only ever released or refreshed by the run that took it: after `unlock` the old run does not delete or rewrite the lock of whoever took the lock next. |
| `forget` | `-n <name> --resource-id <id> [-f <template>] [-y]` | Drops one resource from the state; the object stays in the tenant and is adopted again by the next deployment. Exit 4 when locked. It reads the state **leniently**, so it also removes an entry that fails the integrity rules (`StateEntryInvalid`), which every other verb refuses to load. |
| `set-provenance` | `-n <name> --resource-id <id> --provenance created\|adopted [-f <template>] [-y]` | Marks one entry of the state as `created` or `adopted`. Nothing reads the provenance any more (Complete mode never deletes and `deployment delete` works from the template); the verb remains until the state subsystem is removed. The resource id must already be in the state (an unknown id is refused, exit 1; the verb never invents an entry). Asks for confirmation unless `-y`. Exit 4 when locked. Example: `earm state set-provenance -n orders-prod --resource-id /providers/Microsoft.Entra/groups/orders-admins --provenance created`. |
| `adopt` | see below | Brings an existing object under a template. |

### `state adopt`

Two forms (exactly one per call). In this verb `-n` / `--deployment` is the deployment, and `--unique-name` is the uniqueName (the older spelling `--name` still works, is hidden from `--help` and prints a deprecation note: every other state verb uses `--name` for the deployment).

**Pin an id (offline)**: `earm state adopt -n <deployment> --resource-id <id> --object-id <guid> [-f <template>] [--parent-object-id <guid>] [--key name=value …] [-y]`

Records the Graph object id under a resource id in the deployment's state (creating the state file when there is none), so a type that is looked up by
`displayName` (several matches: `AmbiguousMatch`) or found by composite properties (GUID-named types) binds to the intended object; the next
deployment reads it by that id and falls back to the key only if the id is gone. The state key follows the type's name kind; for a child type the parent must already be
in the state or be given with `--parent-object-id`; a GUID-named type (`appRoleAssignments`, `oauth2PermissionGrants`, `roleAssignments`) needs its
key properties as `--key principalId=<guid> --key …` so deletes and lookups can find the object. An id that is already pinned to another object asks for
confirmation (`-y` skips it). Exit 0; 1 on bad input or a refusal; 4 when the state is locked. Needs no sign-in.

```powershell
earm state adopt -n orders-prod --resource-id /providers/Microsoft.Entra/conditionalAccess/policies/CA-Orders-Admins-MFA --object-id 6f1b…
```

**Set a uniqueName (online)**: `earm state adopt --type Microsoft.Entra/applications|groups --id <objectId|appId> --unique-name <uniqueName> [-y]`

`PATCH /applications/{id}` or `/groups/{id}` with `{ "uniqueName": … }` on a brownfield object, so a template can address it by that name
(`Prefer: create-if-missing` upserts then update it instead of creating a duplicate). The order is: argument checks, **preflight** (exit 3 without a
working sign-in), read the object (an application by object id, then by application id), **refuse** when a `uniqueName` is already
set and differs (it is immutable; an identical one is "nothing to do", exit 0), confirm (`-y` skips), PATCH. Exit 0; 1 on a refusal, a missing object or a Graph error
(a 403 names the permission); 3 on the preflight. For an application the PATCH goes through the applications provider: when Graph rejects it (uniqueName is documented as immutable and read-only) the
error is one line starting `UniqueNameNotSettable:` that explains the alternatives, exit 1. Needs `Application.ReadWrite.All` (`.OwnedBy` for an owned application) or `Group.ReadWrite.All`, or a role that manages them.
