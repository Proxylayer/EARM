# Command line reference

`earm` mirrors `az deployment group …`. Online verbs talk to Microsoft Graph over HTTPS with a token of the identity EARM is signed in as (see [Signing in](#signing-in)); offline verbs need neither a sign-in nor a tenant. EARM starts no external process: no Azure CLI, no PowerShell.

```
earm
├── deployment
│   ├── validate        offline schema + expression validation
│   ├── what-if         preview the changes (read only)
│   ├── create          apply a template
│   └── delete          delete the objects a template declares (by live key)
├── auth
│   ├── login           sign in interactively (browser or device code)
│   ├── logout          forget the interactive sign-in
│   └── status          who EARM is signed in as
├── permissions         Graph permissions and Entra roles a template needs (offline)
└── schema
    ├── print           write a JSON Schema to stdout
    └── install         map the schemas to *.entra.json in VS Code
```

`earm --version` prints the version; a bare `earm` prints help; every verb accepts `-?`, `-h` and `--help`.

## Global options

Valid on every verb (recursive).

| Option | Meaning |
|---|---|
| `-o`, `--output table\|json\|none` | Output format. Default `table`. `none` prints nothing (useful with `--exit-code-on-changes`). |
| `--auth auto\|default\|interactive` | How tokens are obtained. Default: `EARM_AUTH`, then `auto` (a cached `earm auth login` sign-in when there is one, else the default credential chain). |
| `--tenant <id\|domain>` | Tenant to sign in to. Default: `EARM_TENANT_ID`. |
| `--client-id <guid>` | Public-client app the interactive sign-in uses. Default: `EARM_CLIENT_ID`, then the client of the stored sign-in, then Microsoft Graph Command Line Tools (`14d82eec-204b-4c2f-b7e8-296a70dab67e`; none in the `china` cloud, where `earm auth login` requires `--client-id`). |
| `--verbose` | Debug logging on stderr: every Graph call (method, path, status, timing, request id; secret-looking query values masked); tokens, Authorization headers and bodies are never logged. |
| `--debug` | Trace logging on stderr: as `--verbose` plus token acquisition and retry details (never the tokens). Also prints the full exception (with stack trace) of an unexpected error; `EARM_DEBUG=1` does the same. |
| `--evaluation-timeout <seconds>` | Wall-clock budget of one evaluation (a variable, the properties of a resource, an output; the time between evaluations, such as network calls, never counts). Default: `EARM_EVALUATION_TIMEOUT`, then 60. `0` disables the time limit (the step limit of [format.md](format.md#evaluation-limits) still applies); the largest value, for the flag and the variable alike, is 86,400 (24 hours). A value that is not a number of seconds in that range (`abc`, `NaN`, `Infinity`, a negative or larger number) is a one-line usage error (exit 1), never an "Unexpected error"; an invalid `EARM_EVALUATION_TIMEOUT` is ignored. An exceeded budget is the diagnostic `EvaluationBudgetExceeded`. |

Environment: `EARM_AUTH`, `EARM_TENANT_ID`, `EARM_CLIENT_ID`, `EARM_EVALUATION_TIMEOUT`, `EARM_DEBUG`, `EARM_KEYVAULT_ALLOWED` (comma-separated vault names, case-insensitive: when set, a Key Vault parameter reference to any other vault is refused before a token is requested), `EARM_CLOUD` (`public` (default), `usgov`, `usgovdod` or `china`: selects the Graph host, the Entra authority and the Key Vault suffix from a fixed table; there is no way to name another endpoint), plus the `AZURE_*` variables of Azure.Identity (see [Signing in](#signing-in)); colours (what-if) are off when
`NO_COLOR` (any non-empty value) or `AZURE_CORE_NO_COLOR` (`true`, `1`, `yes`, `on`) is set or the output is redirected.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success (with `--exit-code-on-changes`: `what-if` planned no change, `create` succeeded and wrote nothing). |
| 1 | Deployment or operation failure; a template or parameter file that cannot be loaded; a malformed argument or a usage error; a type without a provider; a refusal (confirmation declined, non-interactive without `--yes`, a replacement that was not confirmed: `RecreateNotConfirmed`); an **unexpected error**, printed as one line `Unexpected error (<type>): <message>` (the full exception only with `--debug` / `EARM_DEBUG=1`), a file access problem (`Access denied: ...`, `A file operation failed ...`) or running out of memory. |
| 2 | Validation failure: the template has errors (offline validation, expansion or the dependency graph). |
| 3 | Authentication or preflight failure: no credential signs in, an expired interactive sign-in, a sign-in that lacks consent for a requested scope (`Consent needed`), a Graph token that is rejected, an unreadable Key Vault reference. |
| 5 | `--exit-code-on-changes`: `what-if` (and `create --what-if`) planned a Create, Modify or Delete (or an edge add or remove); `create` **succeeded** and applied at least one (a failed `create` exits 1). Orphan report rows never count: they are reported, never written. A secret-bearing property of an existing object is never compared, so it never counts either. |

Code four is retired: it meant a locked local state, and EARM keeps no local state. It is not reused, so the other codes keep their meaning.

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
| `auto` (default) | The cached sign-in of `earm auth login` when one exists, otherwise `default`. There is **no fallback**: when a cached sign-in exists but cannot produce a token silently (expired or revoked refresh token), the run fails with "your interactive sign-in expired: run `earm auth login`, or pass `--auth default`" instead of quietly acting as a different identity. A sign-in that is valid but lacks consent for a requested scope fails with "Consent needed" instead (see [Consent](#consent)). A record that belongs to another cloud is ignored (logged). |
| `default` | `DefaultAzureCredential` restricted to credentials that start no process: **environment** (`AZURE_TENANT_ID` + `AZURE_CLIENT_ID` + `AZURE_CLIENT_SECRET`, or `AZURE_CLIENT_CERTIFICATE_PATH`, or `AZURE_USERNAME`/`AZURE_PASSWORD`), **workload identity** (`AZURE_FEDERATED_TOKEN_FILE`) and **managed identity** (`AZURE_CLIENT_ID` selects a user-assigned one). The Azure CLI, Azure Developer CLI, Azure PowerShell, Visual Studio and Visual Studio Code credentials are excluded. `AZURE_TOKEN_CREDENTIALS` is refused unless it is `prod` or one of the three credentials above. |
| `interactive` | Only the cached sign-in; fails with "run `earm auth login`" when there is none. |

The identity of the first token (tenant id, object id, app id, user principal name or app id) is logged once at Information level (shown by `--verbose`); the token itself never is.
An unknown `--auth`, `--tenant`, `--client-id` or `EARM_CLOUD` value is a plain failure (exit 1) whose message names the setting and lists the valid values.

### `earm auth login [--device-code] [--scopes <list>] [-f <template> [-p ...] [--verb create|whatif|delete]] [--tenant <id>] [--client-id <guid>]`

Signs in as a user: a browser window by default, `--device-code` on a machine without one. The tokens go to a persistent **encrypted** token cache named
`earm` (DPAPI on Windows, Keychain on macOS, the secret service on Linux); unencrypted storage is never enabled, and when the platform cannot protect the cache the login
fails with a message that says so. The non-secret account record is stored owner-only at `%LOCALAPPDATA%\EArm\auth\record.json` (`$XDG_DATA_HOME/earm/auth/record.json`
elsewhere); every login writes a fresh record naming the client it used.

**Sign-in app.** Nobody registers an application: the default client is the Microsoft first-party app **Microsoft Graph Command Line Tools**
(`14d82eec-204b-4c2f-b7e8-296a70dab67e`, the app Microsoft Graph PowerShell uses) in the `public`, `usgov` and `usgovdod` clouds. It requests delegated scopes
dynamically, so EARM acts with **your** delegated permissions, limited to the scopes consented. The app does not exist in Azure operated by 21Vianet: with
`EARM_CLOUD=china`, `login` without `--client-id` fails (exit 1) and asks for the id of a public-client app registration of your own (delegated Graph permissions, a
`http://localhost` redirect URI, and "allow public client flows" for `--device-code`). `--client-id` / `EARM_CLIENT_ID` selects such an app in any cloud.

**Scopes.** A plain `login` requests the base set `User.Read` and `Organization.Read.All`. To consent ahead of time what later runs will request:

| Option | Meaning |
|---|---|
| `--scopes <list>` | Extra delegated scopes, space- or comma-separated: Microsoft Graph names (`Group.ReadWrite.All`), Graph scopes qualified with the cloud's Graph origin, or the Key Vault scope (`https://vault.azure.net/user_impersonation`). |
| `-f`, `--template-file <template>` | Consent the scopes one run of the template requests (the set `earm permissions -f <template> --verb <verb>` lists), computed offline. When the template has Key Vault parameter references, a second interactive request consents `https://<vault suffix>/user_impersonation` as the same account. |
| `-p`, `--parameters <value>` | Parameter values of `-f`, as for the deployment verbs (`@file.json` or `name=value`; they decide copy counts and conditions). |
| `--verb create\|whatif\|delete` | Which run of `-f` to consent for. Default `create`. |

`--scopes` and `-f` combine (union, plus the base set). An unloadable template exits 1, a template with validation errors 2, as `earm permissions` does.
With `--auth default` they do not apply (a service principal or managed identity uses the application permissions an administrator granted it) and `login` exits 1.
Exit 0; 3 when the sign-in fails.
`login` never waits for a browser that nobody can use: with **standard input and output both redirected** (a script, a pipeline) and no `--device-code` it fails at once
(exit 1) and says to add `--device-code`; with `--auth default` (or `EARM_AUTH=default`) it fails at once too, because the default credential chain (environment variables,
workload identity, managed identity) signs nobody in: `login` is only for interactive sign-in.

### `earm auth logout`

Deletes the sign-in record and the files of the `earm` token cache. Service principal, workload identity and managed identity sign-ins come from the environment and are not affected.

### `earm auth status [-f <template> [-p ...] [--verb create|whatif|delete]]`

Acquires a token the way an online verb would and prints the mode, cloud, tenant, principal (user principal name, or app id for an app-only token), object id, the client
(application) id with its name when it is Microsoft Graph Command Line Tools or Microsoft Azure CLI, the delegated scopes (`scp`) or application roles and the expiry, read from
the token's claims **without validating** them (display only; the token is never printed). Exit 3 with the sign-in guidance (the credentials that were tried and how to
configure one) when nothing signs in.

With `-f <template>` (and `-p`, `--verb` as for `login`, default `create`), it also lists the delegated scopes that run requires and those missing from the token's `scp`
(`requiredScopes`, `missingScopes`, `keyVaultNeeded` with `-o json`), with the `earm auth login` command that consents them. Missing scopes do not change the exit code (0).
`-f` with an app-only identity (`--auth default`, or a service principal token) exits 1: delegated scopes do not apply; see `earm permissions --mode app`.

### Consent

Runs get their tokens silently. When the sign-in is valid but has not been granted a requested scope (the identity platform answers AADSTS65001), the run fails with exit
code 3 and a `Consent needed` message instead of "sign-in expired": it names the missing scopes and the command that grants them once, interactively
(`earm auth login --scopes <missing>`, or `earm auth login -f <template>` for everything a template needs). Signing in again without those scopes does not help.
If your organization requires admin consent for a scope, an administrator grants it to the sign-in app instead.

Consent is recorded for the **sign-in app**, not for EARM: a grant to Microsoft Graph Command Line Tools is shared by every tool that signs in with that app (Microsoft
Graph PowerShell, the Microsoft Graph CLI). Review or revoke it under My Apps (`https://myapps.microsoft.com`) or, as an administrator, under Enterprise applications.

A sign-in record from an earlier version uses the Microsoft Azure CLI app (`04b07795-8ddb-461a-bbee-02f9e1bf7b46`). It keeps working for what that app is pre-authorized
for, with one warning per run (and a note in `auth status`) recommending `earm auth login`, which switches to the default app.

### Migrating from the Azure CLI transport

Earlier previews ran `az rest` through PowerShell. That is gone: `--az-command`, `--pwsh-path`, `--temp-dir` and the environment variables `EARM_AZ_COMMAND`,
`EARM_PWSH_PATH` and `EARM_TEMP_DIR` were removed, and an existing `az login` session is **no longer used**. Sign in with `earm auth login` or the `AZURE_*` variables instead; Azure CLI and
PowerShell are no longer prerequisites (PowerShell remains only for the repository's own `build.ps1`, `test.ps1`, `deploy.ps1` and `tools/*.ps1`).
## `deployment`

### Shared inputs of `validate`, `what-if`, `create`

| Option | Meaning |
|---|---|
| `-f`, `--template-file <path>` | The template (`*.entra.json`). Required. |
| `-p`, `--parameters <…>` | Repeatable, **one value per `-p`** (on every command that takes it: a stray token such as `-p @f.json -n x` is a usage error naming the unknown option instead of being swallowed as another value). `@file.json` loads a parameter file ([format.md](format.md#parameters-and-parameter-files)); `name=value` sets one value. In PowerShell write the file form in single quotes, `-p '@file.json'`: a bare `@` there is the splatting operator and a parse error. Values are applied **in command-line order** and a later value wins, whichever form it has (as `az deployment group create --parameters`): in `-p '@f.json' -p env=dev` the inline value overrides the file, in `-p env=dev -p '@f.json'` the file's `env` overrides it. Put the parameter file first and override selectively with `name=value`. A parameter no `-p` sets falls back to its `defaultValue`. |
| `-o`, `--output table\|json\|none` | Output format (a global option, valid on every verb). Default `table`. `none` prints nothing. |

### `deployment validate`

`earm deployment validate -f <template> [-p …] [-o table|json]`. There is no `-n`: validate is offline and has no deployment name. Offline: JSON Schema (envelope and per-type property schemas), the type
catalog, semantic checks, expression syntax and static evaluation (names, `dependsOn`, references, outputs, and every expression inside `properties`, which is evaluated far enough to report an undefined variable or parameter and an unknown function). No tenant access. Exit 0 with
no errors (warnings allowed), 2 with errors, 1 when a file cannot be loaded. Every code is in [diagnostics.md](diagnostics.md). At most 50 diagnostics of one code and severity are listed; the rest are summarised in one line (`suppressedCount` and `suppressed[]` in JSON, the counts stay exact).

### `deployment what-if` and `deployment create`

`what-if` reads the tenant and prints what `create` would do; it never writes. `create` applies the template in dependency order. Neither keeps
anything on the local disk: every resource is found again on every run by its **live key** (see [Identity without state](engine.md#identity-without-state)),
and the only file a run writes is the `--secure-outputs-file` you name.

| Option | Verb | Meaning |
|---|---|---|
| `-n`, `--name <name>` | both | Deployment name, a label shown in the output (nothing is stored under it). Default: the template file name without `.entra.json`. `validate` is offline, has no deployment name and does not accept `-n`. |
| `--mode Incremental\|Complete` | both | Incremental (default): create or update the declared resources; nothing else is touched. Complete: the same writes, plus a read-only **orphan report** of the live objects of the declared types that the template does not declare (bounded per type: see [whatif.md](whatif.md#orphan-report)). Complete mode **never deletes**; to remove declared objects use `deployment delete`. Both modes report the retained role / app role assignments and grants of the principals the template declares (`orphan: retained`). |
| `--parallelism <n>` | both | Resources read or applied concurrently inside a dependency batch. Default 4. At least 1: a smaller value is a usage error (exit 1). |
| `--allow-ca-enforce` | both | Allow Conditional Access policies with `state: enabled` (default: report-only or disabled only). |
| `--on-soft-deleted-conflict error\|restore` | both | What to do when a key to create is held by a soft-deleted object: fail with a hint (default), or restore it and continue. Nothing is ever purged. |
| `--result-format FullResourcePayloads\|ResourceIdOnly` | both | What-if detail. |
| `--exit-code-on-changes` | both | `what-if`: exit 5 when the what-if contains a Create, Modify or Delete, or an edge change. `create`: exit 5 when the deployment succeeded and applied at least one of them (a resource created, updated, replaced or deleted, or a relationship entry added or removed); with `-c` too it is the applied run that counts, not the what-if shown before it. A failure exits 1 first; orphan report rows never count. |
| `--no-pretty-print` | both | Compact JSON for `-o json`. |
| `--what-if` | `create` | Alias of `deployment what-if`. |
| `-c`, `--confirm-with-what-if` | `create` | Print the what-if, ask `Are you sure you want to execute the deployment? (y/n):`, then apply the same plan. With `-o json` standard output stays **one** document: the what-if is not printed on its own but embedded in the deployment result as `whatIf` (the what-if document), the prompt shows the what-if as text on standard error, and a declined prompt prints `{ "status": "Failed", "diagnostics": [ { "code": "NotConfirmed", "message": "Deployment not started." } ], "whatIf": {...} }` with `Deployment not started.` on standard error (exit 1). |
| `-y`, `--yes` | `create` | Skip the confirmations: the one `--confirm-with-what-if` asks (the deployment is applied right after the what-if), and the one a plain `create` asks before it **replaces** objects (an immutable property changed: delete, then create again). Without `-y`, standard input must be interactive for either question; a non-interactive `create` that would replace objects fails before any write (`RecreateNotConfirmed`, exit 1). A plain `create` without replacements never prompts. |
| `--continue-on-error` | `create` | Keep deploying resources that do not depend on a failed one (default: stop at the batch boundary). |
| `--reveal-secure-outputs` | `create` | Print `securestring` / `secureObject` outputs in clear text instead of `***`. |
| `--secure-outputs-file <path>` | `create` | Write the secure outputs in clear text to a file with owner-only permissions. The file is checked **before** anything is deployed (a directory, a missing folder, a read-only file or a symbolic link stops the run: exit 1, "Deployment not started."). One-time secrets (a password credential's `secretText`, a generated user password) are written to it **immediately** after the apply that produced them, under the reserved top-level key `oneTimeSecrets` (by resource id), so a crash or Ctrl+C cannot lose them. A rerun **merges** into an existing file (keys that are not superseded stay) and never replaces it with a smaller one; a run with no secure values does not touch the file at all. |

Before anything is planned, `create` checks `--secure-outputs-file` (exit 1); then both verbs load the parameters and the template (exit 1), run offline
validation (exit 2, before any sign-in), run the preflight (acquires a token, reads its tenant and principal, one `GET /organization`; exit 3) and resolve
Key Vault parameter references (exit 3). There is no lock and no tenant assertion: the tenant is the one the credential signs in to (`earm auth status` shows it).
Rendering is described in [whatif.md](whatif.md), the pipeline in [engine.md](engine.md).

**No lock.** Nothing serialises two runs. Two concurrent `create` runs of one template are safe for the types found by a unique key (applications and groups
by `uniqueName`, users by UPN, service principals by `appId`, assignments by their tuple: the second write fails or finds the first run's object), but for the
types found by `displayName` (Conditional Access policies, named locations, administrative units, custom role definitions, authentication strengths) both runs
can see "not found" and **both create**, leaving two objects with one display name; every later run then fails with `AmbiguousMatch` until one is deleted.
Serialise runs of one template in your pipeline (a concurrency group, a single agent).

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

With `-o json` the standard output of a verb that fails **before** it has anything else to report (a preflight failure, exit 3; a replacement refusal of a plain `create`) is **one JSON document** `{ "status": "Failed", "diagnostics": [ { "severity": "Error", "code": "...", "message": "..." } ] }` instead of prose on stderr; a multi-line message uses `\n` line breaks. A declined `-c` prompt with `-o json` is the same kind of document with code `NotConfirmed` and the what-if under `whatIf`; with `-o table` the what-if text is already on stdout and the decline stays on stderr. The read-only planning pass of a plain `create` without `-y` is never printed: its stdout carries only the result (or the refusal) document.

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

`delete` removes only what the template **declares** and finds by key. It does not find objects a template no longer declares, objects whose key changed (a
renamed `uniqueName` is a different object), or the old assignment of a changed tuple: list those with `what-if --mode Complete` (the orphan report) and
remove them yourself, or put them back in a template (a scratch template that declares just them is enough) and run `delete -f` on that.

### Migrating from the state-file versions

Earlier previews kept a state file per deployment (`.earm/state/<name>.json` with a `<name>.lock`, or under `--state-dir` / `EARM_STATE_DIR`). That subsystem
is gone; identity is resolved from the live tenant on every run ([engine.md](engine.md#identity-without-state)). What to do:

- **Delete the state directory** (`.earm/`, wherever `--state-dir` / `EARM_STATE_DIR` pointed, and an old `.entraarm/`) and drop it from source control.
  Nothing reads it. Keep your secure-outputs files: they are not state.
- **Remove the retired options** from scripts and pipelines: `--state-dir`, `EARM_STATE_DIR`, `--force-unlock`, `--lock-timeout`, `--max-deletes`,
  `--allow-delete-all`, `--delete-adopted` and `delete --delete-resources` are unknown options now (a usage error, exit 1). The verbs
  `state unlock | forget | adopt | set-provenance`, `deployment show`, `deployment list` and `deployment operation list` no longer exist; `-n` is only a label.
  `deployment delete` now takes the template (`-f`, `-p`) and deletes what it declares.
- **Exit code 4** (state locked) is never returned; no other code changed.
- **Complete mode no longer deletes.** A pipeline that relied on it to remove what the template dropped now gets a report (`orphans[]`, "Not declared in the
  template") and must remove those objects itself.
- **Objects that were bound only through the state must be findable by key.** Applications and groups need a `uniqueName` equal to the template `name`
  (EARM sets it on the objects it creates; an object adopted from outside without one is not found and would be created again: set its `uniqueName` in Graph
  first, or, for an application, declare its `appId` so it is found by that and given the name). Display-name-keyed types (Conditional Access policies, named locations, administrative units, custom role definitions, authentication strengths) are
  found by `displayName`: one renamed in the portal is no longer found, and the next run creates a second one. Run `what-if` first and check every `+ Create`.
- **Legacy password credentials.** A credential is now found by its `displayName` `<name>#<rotationTag>`; one created by a state-file version is named
  `<name>` with no tag. It is still matched by its base name and reported with `RotationTagNotStamped`, and it is **not rotated by a changed `rotationTag`**
  until it has been rotated once (the change cannot be detected). To rotate such a credential now, declare a different `endDateTime`; the replacement is then
  named with the tag.
- **Changed assignments keep the old one.** An assignment whose principal, role or scope changed is a new assignment; the old one stays and is reported
  (`orphan: retained`), never removed. The state-file versions deleted it. Review [Retained assignments](engine.md#retained-assignments).
- **Principal-side edges no longer remove.** A `memberOf` / `appRoleAssignments` / `directoryRoles` entry removed from a principal is no longer removed from
  the tenant ([edges.md](edges.md)).
- **Recreates ask.** A plain `create` that would replace objects prompts, and fails without `-y` on a non-interactive console (`RecreateNotConfirmed`): add
  `-y` to unattended pipelines that accept replacements.
- **Concurrent runs are not serialised.** The lock is gone; see "No lock" above.

### Migrating from the old name

The `apiVersion` relabel (`2026-10-01` became `2026-10-01-preview`, and the old `2026-10-01-preview` beta became `2026-10-01-preview.beta`) has no compatibility shim because the format is unreleased: update the `apiVersion` of your templates (the old label is rejected with `UnknownApiVersion`). The product rename (`entraarm` to `earm`) likewise has no shim: `%LOCALAPPDATA%\EntraArm` became `%LOCALAPPDATA%\EArm` and `ENTRAARM_*` became `EARM_*`; delete an old `.entraarm/` state directory (nothing reads it) and remove the old `entraarm` global tool (`dotnet tool uninstall -g entraarm`).

## `permissions`

`earm permissions -f <template> [-p …] [--mode app|delegated] [--verb whatif|create|delete] [-o table|json]`. Offline. Lists the Graph permissions (and, for
`--mode delegated`, the Entra roles of which the user needs one per group) the template needs, grouped by permission with the resources that
need it and why. Exit 0; 2 when the template has validation errors; 1 when it cannot be loaded. Details and the manifest tables:
[permissions.md](permissions.md).

The list covers both deployment modes: Complete mode never deletes, it only reads the declared types for its orphan report, which the permissions
above already allow. `deployment delete` needs the same permissions as managing the declared types.

`--verb whatif|create|delete` prints instead the delegated scopes one run of that verb requests for a signed-in user (implies `--mode delegated`;
`--mode app` with `--verb` exits 1): what-if gets only read scopes, create the write scopes, delete write for the declared types and reads for edges
and lookups; every set includes `Organization.Read.All` (preflight) and `User.Read` (sign-in), and reports whether the run needs a Key Vault token.
See [permissions.md](permissions.md#delegated-scopes-per-run).

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
