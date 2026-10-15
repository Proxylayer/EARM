# EARM

ARM-style JSON templates for **Microsoft Entra ID**, applied through the **Microsoft Graph** REST API.

EARM is short for "Entra ARM" (ARM-style templates for Entra), pronounced as the letters E-A-R-M or as "earm"; the command line tool is `earm`.

> **Preview.** The template format and `apiVersion` `2026-10-01-preview` are in preview: breaking changes are possible and nothing is released
> yet. EARM has **not been validated against a real tenant**: the integration suite that would do so is gated (see
> [Contributing and testing](#contributing-and-testing)) and has not been run against one. Microsoft Graph **v1.0** is used by default
> (`apiVersion` `2026-10-01-preview`); the beta endpoint is opt-in per resource with `2026-10-01-preview.beta` (passthrough, no normalisation
> guarantees). The plain `2026-10-01` label was renamed `2026-10-01-preview` and is rejected with an `UnknownApiVersion` error that says so.

Repository: <https://github.com/Proxylayer/EARM>

Azure has ARM templates; Entra ID has no true equivalent. EARM is a template format that mimics classic ARM templates as closely as it
can, but whose resources are Entra objects under a `Microsoft.Entra` provider namespace, plus a .NET tool (`earm`) whose verbs mirror
`az deployment group …`. It reads and writes Microsoft Graph over plain HTTPS (`HttpClient`) with a token from Azure.Identity: no Graph SDK, no Azure CLI, no PowerShell
at run time, and no app registration of its own unless you want interactive sign-in with admin scopes.

- **Declarative and idempotent**: applications, service principals, federated credentials and client secrets, groups, users, app role
  assignments and consent grants, directory roles and custom roles, administrative units, Conditional Access policies, named locations,
  authentication strengths and contexts. A rerun updates; it never duplicates.
- **ARM vocabulary**: `resources[]` with `type` / `apiVersion` / `name` / `properties` / `dependsOn` / `condition` / `copy`, parameters,
  variables, user functions, outputs, `resourceId()` / `reference()` / `copyIndex()`, parameter files with Key Vault references,
  Incremental and Complete modes, **what-if** with ARM's change types.
- **Relationships from either side**: `members`, `owners`, `memberOf`, `manager`, `appRoleAssignments`, `directoryRoles`, `assignedLicenses` are
  plain arrays in `properties`, applied after all objects exist, without cycles.
- **Safe by construction**: a state file per deployment (ids only, safe to commit), secrets tainted and never written to state, logs or what-if,
  state locking, tenant pinning, soft-delete awareness, a Conditional Access enforcement guard.
- **Editor support**: JSON Schemas for the envelope and every resource type, installable into VS Code with one command.

## Status

Implemented (a working pre-release, see [Limitations](#limitations-v1) for what is deferred):

- **15 resource types**: `applications`, `applications/federatedIdentityCredentials`, `applications/passwordCredentials`, `servicePrincipals`,
  `groups`, `users`, `appRoleAssignments`, `oauth2PermissionGrants`, `roleAssignments`, `roleDefinitions`, `administrativeUnits`,
  `conditionalAccess/policies`, `conditionalAccess/namedLocations`, `conditionalAccess/authenticationStrengths` and
  `conditionalAccess/authenticationContextClassReferences` (all under `Microsoft.Entra/`).
- The verbs `deployment validate | what-if | create | show | list | delete`, `deployment operation list`, `permissions`, `schema print | install` and
  `state unlock | forget | adopt | set-provenance`, `auth login | logout | status`.
- Tests: about 6,100 unit and in-process tests (EArm.Tests, EArm.Providers.Tests, EArm.Graph.Tests; a warm `test.ps1` takes about 1-1.5 minutes), against fakes of Graph (an HTTP handler over an in-memory directory)
  and of the sign-in, with about 95 % line and 90 % branch coverage ([tests/COVERAGE.md](tests/COVERAGE.md); how they are organised and reviewed: [docs/testing.md](docs/testing.md)).
  The tenant-backed integration suite is gated behind `EARM_IT_TENANT` and has **not been run**: it is not part of those numbers, and a plain `test.ps1` reports it as `GATED`, not as a pass.

## Quick start

From a clean clone (prerequisite: the [.NET 10 SDK](global.json), 10.0.301 or a later 10.0.x SDK; PowerShell 7.4+ runs the repository scripts `build.ps1`, `test.ps1`
and `deploy.ps1`; see [Install](#install)):

```powershell
git clone https://github.com/Proxylayer/EARM.git
cd EARM

pwsh -File build.ps1        # build the solution: prints BUILD OK, zero warnings
pwsh -File test.ps1         # run the whole suite headless (well under a minute)
pwsh -File deploy.ps1       # pack the CLI and install it as the global tool `earm`

# offline: no sign-in or tenant needed
earm deployment validate -f samples/orders-api.entra.json -p '@samples/orders-api.parameters.json'

# against a DEV tenant you may write to
earm auth login                                 # interactive; or set AZURE_TENANT_ID / AZURE_CLIENT_ID / AZURE_CLIENT_SECRET (see Signing in)
earm deployment what-if -f samples/app-with-sp-and-secret.entra.json -p '@samples/app-with-sp-and-secret.parameters.json'
earm deployment create  -f samples/app-with-sp-and-secret.entra.json -p '@samples/app-with-sp-and-secret.parameters.json' -c --secure-outputs-file ./secure-outputs.json
```

`orders-api` is the full tour but its parameter file references a Key Vault secret (replace that entry before a real run); the
`app-with-sp-and-secret` sample deploys without one. Every sample is described in [samples/README.md](samples/README.md). `create` writes real objects:
read the `what-if` first, and use a throwaway tenant while the format is in preview. The secure-outputs file holds the client secret in clear text:
`.gitignore` of this repository covers `secure-outputs*.json` and `*.secrets.json`; give it such a name (or ignore it) in your own repository too.

## A template at a glance

```json
{
  "$schema": "https://schema.earm.proxylayer.net/schemas/2026-10-01/deploymentTemplate.json#",
  "contentVersion": "1.0.0.0",
  "parameters": { "env": { "type": "string", "allowedValues": ["dev", "prod"] } },
  "variables":  { "appName": "[format('contoso-{0}-web', parameters('env'))]" },
  "resources": [
    { "type": "Microsoft.Entra/applications", "apiVersion": "2026-10-01-preview", "name": "[variables('appName')]",
      "properties": { "displayName": "Contoso Web", "signInAudience": "AzureADMyOrg",
                      "web": { "redirectUris": ["https://web.contoso.com/signin-oidc"] },
                      "appRoles": [{ "value": "Reports.Admin", "displayName": "Reports admin", "description": "Manage reports", "allowedMemberTypes": ["User"] }] },
      "resources": [
        { "type": "passwordCredentials", "apiVersion": "2026-10-01-preview", "name": "ci-secret", "properties": { "rotationTag": "2026Q4" } } ] },
    { "type": "Microsoft.Entra/servicePrincipals", "apiVersion": "2026-10-01-preview",
      "name": "[reference(variables('appName')).appId]", "properties": { "appRoleAssignmentRequired": true } },
    { "type": "Microsoft.Entra/groups", "apiVersion": "2026-10-01-preview", "name": "contoso-web-admins",
      "properties": { "displayName": "Contoso Web admins", "mailEnabled": false, "securityEnabled": true,
                      "appRoleAssignments": [{ "resourceAppId": "[reference(variables('appName')).appId]", "appRole": "Reports.Admin" }] } }
  ],
  "outputs": { "appId": { "type": "string", "value": "[reference(variables('appName')).appId]" } }
}
```

The application is addressed by its `name` (Graph `uniqueName`), so the second run finds and updates it. The service principal is named after the
application's `appId`, which only exists once the application does: EARM works that out and orders the two. The child `passwordCredentials` is
nested like an ARM child resource; changing `rotationTag` rotates the secret. More in [`samples/`](samples/README.md).

## Install

> **Renamed from EntraARM.** The project and tool used to be called EntraARM / `entraarm` and are now EARM / `earm`. If you used a pre-rename
> checkout or tool: the global tool `entraarm` is replaced by `earm` (`deploy.ps1` uninstalls the old one); the environment variables
> `ENTRAARM_*` became `EARM_*`; the state directory `.entraarm/` became `.earm/` (delete old state files) and `%LOCALAPPDATA%\EntraArm` became
> `%LOCALAPPDATA%\EArm`; and the template `$schema` host `entraarm.dev` became `schema.earm.proxylayer.net`. Details:
> [docs/cli.md](docs/cli.md#migrating-from-the-old-name).

Prerequisite on the machine that builds or runs `earm`:

| Needs | Why |
|---|---|
| **.NET 10 SDK** (`global.json`: 10.0.301, later 10.0.x SDKs allowed) | To build, test and install the tool from a checkout (the installed tool runs on the .NET 10 runtime). |

Nothing else: EARM talks to Microsoft Graph itself and starts no external process (no Azure CLI, no PowerShell, no `cmd`). PowerShell 7.4+ is only used by the
repository scripts (`build.ps1`, `test.ps1`, `deploy.ps1`, `tools/*.ps1`).

### Signing in

Online verbs use Azure.Identity. Pick whichever fits (`earm auth status` shows what is in effect; details in [docs/cli.md](docs/cli.md#signing-in)):

| For | Configure |
|---|---|
| Unattended runs, CI | A **service principal**: `AZURE_TENANT_ID`, `AZURE_CLIENT_ID` and `AZURE_CLIENT_SECRET` (or `AZURE_CLIENT_CERTIFICATE_PATH`). Grant it Microsoft Graph **application permissions** (`earm permissions` lists them). |
| Azure-hosted automation | A **managed identity** (set `AZURE_CLIENT_ID` for a user-assigned one) or **workload identity** (`AZURE_FEDERATED_TOKEN_FILE` with `AZURE_TENANT_ID` / `AZURE_CLIENT_ID`). |
| A person at a keyboard | `earm auth login` (browser) or `earm auth login --device-code`; tokens are kept in an encrypted cache (`earm auth logout` forgets them). Use `--tenant`, and `--client-id` for your own public-client app registration with the needed delegated permissions. |

An `az login` session is **not** used, and the Azure CLI, Azure PowerShell and Visual Studio credentials are deliberately excluded from the chain. `EARM_CLOUD` selects a national cloud
(`usgov`, `usgovdod`, `china`).
From a checkout (builds, packs and installs the global tool `earm`):

```powershell
pwsh -File deploy.ps1                 # dotnet pack + dotnet tool install -g earm --source artifacts/nupkg (the local folder is the only source)
earm --version
```

or from any feed that hosts the package: `dotnet tool install -g earm`. Offline verbs (`validate`, `permissions`, `schema`) need no
sign-in. `pwsh -File build.ps1` builds, `pwsh -File test.ps1` runs the tests.

## Cheat sheet

```powershell
earm deployment validate -f app.entra.json -p '@app.parameters.json'          # offline: schema, catalog, expressions
earm permissions         -f app.entra.json -p '@app.parameters.json'          # what the identity needs (offline)
earm deployment what-if  -f app.entra.json -p '@app.parameters.json'          # preview (read only); --exit-code-on-changes
earm deployment create   -f app.entra.json -p '@app.parameters.json' -c       # what-if, confirm, apply
earm deployment create   -f app.entra.json -p '@app.parameters.json' --mode Complete   # also delete what the template dropped
earm deployment show -n app --refresh                                       # state + drift
earm deployment delete -n app --delete-resources -y                         # tear down what the deployment created (-y: no prompt)
earm state adopt -n app --resource-id /providers/Microsoft.Entra/groups/x --object-id <guid>   # bind an existing object
earm state adopt --type Microsoft.Entra/groups --id <objectId> --unique-name contoso-x             # online: set the uniqueName an existing group or application lacks
earm state set-provenance -n app --resource-id /providers/Microsoft.Entra/groups/x --provenance created   # mark an entry as created (or adopted)
earm schema install                                                         # VS Code validation of *.entra.json
```

| Verb | Purpose |
|---|---|
| `deployment validate` | Offline validation: JSON Schema, type catalog, semantic checks, expression syntax and static evaluation. Exit 2 on errors. |
| `deployment what-if` | The changes `create` would make, in ARM's vocabulary (`+ ~ - = *`) or ARM-shaped JSON (`-o json`). Never writes. |
| `deployment create` | Apply in dependency order; state saved after every resource; `-c` shows the what-if and asks before applying, `-y` skips that question (and the Complete-mode delete list) but never waives `--allow-delete-all`; plain `create` does not prompt. |
| `deployment show` / `list` / `operation list` / `delete` | Inspect and remove deployments (state-backed). |
| `permissions` | Graph permissions and Entra roles a template needs (`--mode app\|delegated`, `-o json`). |
| `schema print` / `install` | Emit the JSON Schemas, or install the VS Code mapping. |
| `state unlock` / `forget` / `adopt` / `set-provenance` | Maintain state: break a lock, drop a resource (also an invalid one), adopt an existing object (or set an object's `uniqueName` with `--type/--id/--unique-name`; the older `--name` spelling is deprecated and hidden), mark an entry `created` or `adopted`. |

Every verb and flag, and the exit codes (0 ok, 1 failure, 2 validation, 3 auth/preflight/tenant, 4 state lock, 5 what-if changes): [docs/cli.md](docs/cli.md).

## Modes

- **Incremental** (default): creates and updates the declared resources. Only the properties you declare are applied (Graph is merge-PATCH; omitted
  properties are untouched). A resource that disappeared from the template is *detached* from the state with a warning; the object stays.
- **Complete**: additionally deletes the objects **the state file records** that the template no longer declares, in reverse dependency order (edges first,
  then groups, applications, users, policies…). It is **not** a "delete everything in the tenant that is not in the template" mode: an object that is not in the
  state file is never deleted, however it got there, and with no state file (a fresh checkout, another `--state-dir`) Complete mode deletes nothing.
  Preview it with `what-if --mode Complete`; security groups are deleted permanently and say so.

Complete mode is guarded, because a mistyped template path or an emptied file must not wipe a deployment:

| Guard | Behaviour |
|---|---|
| Confirmation | A Complete-mode `create` lists what it would delete (resource id, type, live `displayName`, `[adopted]`) and asks `Delete them as part of this deployment? (y/n)`; `-y` skips the question, and without `-y` a non-interactive standard input stops the run (exit 1, nothing started). |
| `--allow-delete-all` | Required, even with `-y`, when the template declares **no resources** while the state manages some, or when the run would delete more than `--max-deletes` objects (default 25). |
| `--max-deletes <n>` | An estimate before the run **and a hard limit during it**: the executor counts every state-driven delete (orphans and the old objects of recreates) and stops with `DeleteLimitExceeded` at the limit, so a run can never delete more than the number that was confirmed, even when the estimate was low (for example resource names only known at deployment time). `0` allows none; `--allow-delete-all` lifts it. |
| Adopted objects | An object that existed before the deployment managed it (found by key without a state entry, or pinned with `state adopt`) is recorded as `adopted` and **never deleted** (warning `AdoptedObjectNotDeleted`, the entry stays) unless `--delete-adopted`. `AdoptedExistingObject` warns when an object is taken over. A state file written **before provenance was recorded** has no `provenance` on its entries and **all of them read as adopted**, so a Complete-mode run keeps everything (`AdoptedObjectNotDeleted`, "no provenance record"): mark the ones EARM created with `earm state set-provenance -n <name> --resource-id <id> --provenance created`. |
| Re-verification | Every state-driven delete re-reads the object by its key and refuses (`StateEntryMismatch`) when the live id is not the pinned one; the signed-in principal and first-party service principals are never deleted (`ProtectedObject`). |

The same rules apply to `deployment delete --delete-resources`. Details: [docs/cli.md](docs/cli.md#deployment-what-if-and-deployment-create),
[docs/engine.md](docs/engine.md#state-earmenginestate), [docs/diagnostics.md](docs/diagnostics.md#engine-codes-what-if-and-create).

## State

Per deployment, `./.earm/state/<name>.json` next to the template (`--state-dir` or `EARM_STATE_DIR` to move it). It maps each resource id
(`/providers/Microsoft.Entra/applications/contoso-dev-web`) to the Graph object id, so displayName-named objects stay bound, and records the edges the deployment
created. It holds ids and hashes only (the serializer refuses to write a secret), is written atomically after **every** resource (a failed run leaves an accurate
partial record), and is pinned to the tenant (a different tenant is refused with exit 3).

**The state must be persisted and shared.** It is the only memory of what the deployment manages: commit it with the template, or keep it in a directory every
operator and pipeline run can reach (`--state-dir` / `EARM_STATE_DIR`). A run that starts without it finds existing objects by key again (and records them as
`adopted`, so Complete mode will not delete them), but it cannot delete what it no longer knows about, and two runs that do not see each other's state will race. A state is
read as a hint, not an authority: ids are shape-checked, every pinned read is verified against the key, and a state edited to point at another object is refused
(`StateEntryMismatch`). There is no state backend other than the file (a blob backend is planned).

**A damaged state is repairable.** Loading a state checks every entry (a Graph id that is a GUID, a `key` that is the key its resource id implies, well-formed relationship ids); an entry that fails is refused by every verb that would act on the tenant (`StateEntryInvalid`, naming the entry). The two verbs that exist to get out of that situation read the state **leniently**: `state forget` removes the bad entry, and `deployment delete` (without `--delete-resources`) removes the state file; neither ever uses a leniently loaded state to touch the tenant (`delete --delete-resources` loads strictly). Trust model of the file itself (what a hand-edited state can and cannot do): [SECURITY.md](SECURITY.md#the-state-file-is-trusted-for-which-objects-it-names).

**Locking.** A mutating run (`deployment create`, `deployment delete`, `state forget`, `state adopt`) takes `<name>.lock` in the state directory first, **then** loads the state, so a concurrent run cannot
act on a stale read. `create` waits `--lock-timeout` seconds (default 10) for another run to finish, the other verbs fail at once; either way a lock that stays held is exit 4. `what-if` never locks: it reads an unlocked snapshot and says so. A lock left by a crashed run
is taken over after an hour or broken with `state unlock` / `--force-unlock`.

## Secrets

`securestring` and `secureObject` parameters, `passwordProfile.password` and `secretText` are tainted: they may flow into resource properties and `securestring`
outputs only, are `***` in every rendering, and never reach state, logs, what-if or diagnostics. A parameter file can pull a secret from **Azure Key Vault**
(`"reference": { "keyVault": { "id": … }, "secretName": … }`, read over HTTPS from the vault with the signed-in identity's Key Vault token). A client secret's text is returned by Graph exactly once:
read it from a `securestring` output with `--secure-outputs-file ./secure-outputs.json` (an owner-only file, checked before anything is deployed) or `--reveal-secure-outputs`
in the run that creates or rotates it.

One-time secrets (a password credential's `secretText`, a generated user password) are written to the secure-outputs file **immediately** after the call that produced them,
under the reserved top-level key `oneTimeSecrets` (by resource id), so a crash or Ctrl+C cannot lose them; a rerun merges into the file and never replaces it with a smaller one.
Treat that file like a password store: it is the only copy, it is in clear text, and it must not be committed ([.gitignore](.gitignore) covers `secure-outputs*.json`,
`*.secrets.json` and `secrets.json`). See [SECURITY.md](SECURITY.md) for the threat model.

## What-if

`deployment what-if` (and `create -c`) shows `+ Create`, `~ Modify` with property deltas, `- Delete` (Complete mode), `= NoChange` and `* Ignore`, a `?` for values only
known at deployment time and a `!` for a change that cannot be applied as planned; `-o json` mirrors ARM's what-if shape. `--exit-code-on-changes` makes it a CI
drift check (exit 5). Details and golden fixtures: [docs/whatif.md](docs/whatif.md).

## Permissions

`earm permissions -f template -p …` lists, offline, the Microsoft Graph application permissions (or delegated scopes and the Entra roles, `--mode delegated`) a
template needs, grouped by permission with the resources that need them, including the conditional ones (`Application.ReadWrite.OwnedBy` instead of `.All`,
`RoleManagement.ReadWrite.Directory` for role-assignable groups and directory roles, `AppRoleAssignment.ReadWrite.All` for assignments…) and the Key Vault note.
Manifest tables and warnings: [docs/permissions.md](docs/permissions.md).

## Limitations (v1)

- Types: applications (+ federated credentials and password credentials), service principals, groups, users (member users), app role assignments,
  OAuth2 permission grants, directory role assignments and custom role definitions, administrative units, Conditional Access policies, named locations,
  authentication strengths and authentication contexts. The provider registry makes more types cheap to add ([docs/providers.md](docs/providers.md)).
- **Deferred**: gallery / template application instantiation, application logo, SAML token-signing certificates, group settings templates and lifecycle policies,
  B2B invitations (guests), devices, PIM eligibility, the authentication methods policy, cross-tenant access, custom security attributes, entitlement management, terms of use,
  nested deployments.
- **Later (v1.1+)**: property-level `copy`, `--rollback-on-error`, `export` (template from a tenant), `deployment validate --online`, a blob state backend, user password
  reset, administrative-unit scoped role members, a published `$schema` URL (use `earm schema install` meanwhile).
- Graph is eventually consistent: a freshly created object can be invisible for seconds, so dependent calls are retried for up to 90 s. Mail-enabled security groups cannot be
  created through Graph (adopt only). Tenant policies (an application management policy blocking secrets, licensing) can refuse a call; the Graph error is reported per resource.
- **Input limits** (a violation is a diagnostic, never a hang or a crash): a template file and a parameter file are each read only up to **8 MiB** (`InputTooLarge`, exit 1; also more than 2,000,000 JSON values), a variable may read at most **128** levels of other variables (`VariableChainTooDeep`), and expression length and depth, value sizes, `copy` expansion (800 per resource), evaluation steps and wall-clock time (`--evaluation-timeout`, default 60 s per evaluation) are bounded too: [docs/format.md](docs/format.md#evaluation-limits).
- Sign-in is Azure.Identity only (no Azure CLI session, no Graph SDK). The delegated permission mode and a few Graph behaviours are verified by the gated integration
  tests (`EARM_IT_TENANT`), not by the unit tests.

## Documentation

| Page | Content |
|---|---|
| [docs/format.md](docs/format.md) | The template format: envelope, resource rules, type catalog, expression functions, parameters, Key Vault, JSON Schemas |
| [docs/edges.md](docs/edges.md) | Relationships and assignments declared from either side |
| [docs/permissions.md](docs/permissions.md) | The permission manifest and `earm permissions` |
| [docs/cli.md](docs/cli.md) | Every verb and flag, exit codes |
| [docs/whatif.md](docs/whatif.md) | What-if output, symbols, JSON shape, golden fixtures |
| [docs/engine.md](docs/engine.md) | Pipeline, dependency graph, edge pass, state, the provider contract |
| [docs/providers.md](docs/providers.md) | Writing and testing a resource provider, with a worked example per type family |
| [docs/diagnostics.md](docs/diagnostics.md) | Every diagnostic code |
| [docs/testing.md](docs/testing.md) | The test suites, `test.ps1` modes, coverage, mutation-style review notes, the gated integration tests |
| [SECURITY.md](SECURITY.md) | Threat model, what is defended, how secrets are handled, how to report a vulnerability |
| [samples/README.md](samples/README.md) | The sample templates |
| [schemas/2026-10-01](schemas/2026-10-01) | The JSON Schemas (generated per-type properties: `tools/New-TypeSchemas.ps1`) |

## Repository layout

| Path | Content |
|---|---|
| `src/EArm.Cli` | The `earm` command-line tool (System.CommandLine verbs, output rendering, schema installer) |
| `src/EArm.Core` | Template loading, validation, diagnostics, the resource type catalog and the embedded JSON Schemas |
| `src/EArm.Expressions` | The template expression language: parser, evaluator, deployment, string, array, object, lambda and Entra functions |
| `src/EArm.Engine` | Planner, dependency graph, executor, what-if, state, the provider contract |
| `src/EArm.Providers` | One provider per resource type family (applications, groups, users, directory roles, Conditional Access, ...) |
| `src/EArm.Graph` | The Graph transport: `HttpClient`-based Graph client, Azure.Identity token provider and `earm auth` plumbing, Key Vault reader, retry and error mapping |
| `schemas/2026-10-01` | The JSON Schemas of the template format (envelope, parameters and one per resource type); the folder names the template-schema version, not the resource `apiVersion` |
| `samples` | Sample templates and parameter files ([samples/README.md](samples/README.md)) |
| `docs` | Format, CLI, engine, providers, what-if, permissions and diagnostics documentation (see [Documentation](#documentation)) |
| `tests` | xUnit v3 projects (`EArm.Tests`, `EArm.Providers.Tests`, `EArm.Graph.Tests`, gated `EArm.Integration.Tests`), the shared fakes in `EArm.TestSupport` and the golden `fixtures` |
| `tools` | `New-TypeSchemas.ps1` (regenerates the per-type property schemas), `Get-CoverageReport.ps1` and `Update-WhatIfGoldens.ps1` |
| `.github` | The CI workflow (`workflows/ci.yml`) |
| `build.ps1`, `test.ps1`, `deploy.ps1` | Build, test and install-as-global-tool scripts |

## Contributing and testing

Use the repository scripts rather than ad-hoc `dotnet` commands:

```powershell
pwsh -File build.ps1                         # simple: errors and a one-line BUILD OK / BUILD FAIL (-Detailed for full output, -Target <project>, -Configuration Release)
pwsh -File test.ps1                          # whole suite, headless (-Detailed, -Target <test project>, -Filter <expression>, -Coverage, -SkipTiming)
pwsh -File deploy.ps1                        # pack and install the global tool (only when you mean to)
pwsh -File tools/New-TypeSchemas.ps1         # regenerate schemas/2026-10-01/types after changing a type's properties (a test checks the output is current)
```

The build must stay at zero warnings and `test.ps1` all green. Per-type schemas are generated: edit `tools/New-TypeSchemas.ps1` (or the
`x-earm` overlay at the end of a type schema for behaviour) and regenerate; do not hand-edit the property parts.

`EArm.Integration.Tests` writes to a real **dev** tenant and is not part of a plain `test.ps1` run: the script prints `GATED` for it, which is **not** a pass, unless
`EARM_IT_TENANT` is set (naming it with `test.ps1 -Target EArm.Integration.Tests` runs it, and every test then skips with the reason). It is driven by the environment
variables `EARM_IT_TENANT` (required), `EARM_IT_PREFIX`, `EARM_IT_DOMAIN`, `EARM_IT_ALLOW_CA`, `EARM_IT_ALLOW_ROLES` and `EARM_IT_SWEEP`; read
[tests/EArm.Integration.Tests/README.md](tests/EArm.Integration.Tests/README.md) first (what it creates, how it cleans up, which permissions it needs).
The suites, coverage (`test.ps1 -Coverage`, [tests/COVERAGE.md](tests/COVERAGE.md)) and how the tests are reviewed: [docs/testing.md](docs/testing.md).
A `-Filter` that matches no test exits 3 (a typo is not a green run); tests that assert a wall-clock bound run in a second, serial pass (`-SkipTiming` skips it while iterating).
CI (`.github/workflows/ci.yml`) runs `build.ps1` and `test.ps1` on Windows and Linux, then builds Release and packs the tool as an artifact. To report a vulnerability, see [SECURITY.md](SECURITY.md).

## Licence

No licence file yet: all rights reserved until one is added.
