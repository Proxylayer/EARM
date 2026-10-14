# EntraARM

ARM-style JSON templates for **Microsoft Entra ID**, applied through the **Azure CLI**.

Azure has ARM templates; Entra ID has no true equivalent. EntraARM is a template format that mimics classic ARM templates as closely as it
can, but whose resources are Entra objects under a `Microsoft.Entra` provider namespace, plus a .NET tool (`entraarm`) whose verbs mirror
`az deployment group …`. It reads and writes Microsoft Graph with `az rest`, reusing your `az login` session: no Graph SDK, no app
registration of its own.

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

## A template at a glance

```json
{
  "$schema": "https://entraarm.dev/schemas/2026-10-01/deploymentTemplate.json#",
  "contentVersion": "1.0.0.0",
  "parameters": { "env": { "type": "string", "allowedValues": ["dev", "prod"] } },
  "variables":  { "appName": "[format('contoso-{0}-web', parameters('env'))]" },
  "resources": [
    { "type": "Microsoft.Entra/applications", "apiVersion": "2026-10-01", "name": "[variables('appName')]",
      "properties": { "displayName": "Contoso Web", "signInAudience": "AzureADMyOrg",
                      "web": { "redirectUris": ["https://web.contoso.com/signin-oidc"] },
                      "appRoles": [{ "value": "Reports.Admin", "displayName": "Reports admin", "description": "Manage reports", "allowedMemberTypes": ["User"] }] },
      "resources": [
        { "type": "passwordCredentials", "apiVersion": "2026-10-01", "name": "ci-secret", "properties": { "rotationTag": "2026Q4" } } ] },
    { "type": "Microsoft.Entra/servicePrincipals", "apiVersion": "2026-10-01",
      "name": "[reference(variables('appName')).appId]", "properties": { "appRoleAssignmentRequired": true } },
    { "type": "Microsoft.Entra/groups", "apiVersion": "2026-10-01", "name": "contoso-web-admins",
      "properties": { "displayName": "Contoso Web admins", "mailEnabled": false, "securityEnabled": true,
                      "appRoleAssignments": [{ "resourceAppId": "[reference(variables('appName')).appId]", "appRole": "Reports.Admin" }] } }
  ],
  "outputs": { "appId": { "type": "string", "value": "[reference(variables('appName')).appId]" } }
}
```

The application is addressed by its `name` (Graph `uniqueName`), so the second run finds and updates it. The service principal is named after the
application's `appId`, which only exists once the application does: EntraARM works that out and orders the two. The child `passwordCredentials` is
nested like an ARM child resource; changing `rotationTag` rotates the secret. More in [`samples/`](samples/README.md).

## Install

Prerequisites on the machine that runs `entraarm`:

| Needs | Why |
|---|---|
| **PowerShell 7.4+** (`pwsh` on `PATH`) | EntraARM starts `pwsh` and calls the Azure CLI from it (the Windows `az` launcher needs PowerShell's quoting). It never launches `cmd.exe`. |
| **Azure CLI** (`winget install Microsoft.AzureCLI`) | The transport. |
| `az login --allow-no-subscriptions` | A session for the tenant (no Azure subscription is needed). Use `az login --service-principal …` for unattended runs. |
| **.NET 10** | To run (or build) the tool. |

From a checkout (builds, packs and installs the global tool `entraarm`):

```powershell
pwsh -File deploy.ps1                 # dotnet pack + dotnet tool install -g entraarm --add-source artifacts/nupkg
entraarm --version
```

or from any feed that hosts the package: `dotnet tool install -g entraarm`. Offline verbs (`validate`, `permissions`, `schema`) need no
Azure CLI. `pwsh -File build.ps1` builds, `pwsh -File test.ps1` runs the tests.

## Cheat sheet

```powershell
entraarm deployment validate -f app.entra.json -p @app.parameters.json          # offline: schema, catalog, expressions
entraarm permissions         -f app.entra.json -p @app.parameters.json          # what the identity needs (offline)
entraarm deployment what-if  -f app.entra.json -p @app.parameters.json          # preview (read only); --exit-code-on-changes
entraarm deployment create   -f app.entra.json -p @app.parameters.json -c       # what-if, confirm, apply
entraarm deployment create   -f app.entra.json -p @app.parameters.json --mode Complete   # also delete what the template dropped
entraarm deployment show -n app --refresh                                       # state + drift
entraarm deployment delete -n app --delete-resources -y                         # tear down what the deployment manages
entraarm state adopt -n app --resource-id /providers/Microsoft.Entra/groups/x --object-id <guid>   # bind an existing object
entraarm schema install                                                         # VS Code validation of *.entra.json
```

| Verb | Purpose |
|---|---|
| `deployment validate` | Offline validation: JSON Schema, type catalog, semantic checks, expression syntax and static evaluation. Exit 2 on errors. |
| `deployment what-if` | The changes `create` would make, in ARM's vocabulary (`+ ~ - = *`) or ARM-shaped JSON (`-o json`). Never writes. |
| `deployment create` | Apply in dependency order; state saved after every resource; `-c` confirms with a what-if first. |
| `deployment show` / `list` / `operation list` / `delete` | Inspect and remove deployments (state-backed). |
| `permissions` | Graph permissions and Entra roles a template needs (`--mode app\|delegated`, `-o json`). |
| `schema print` / `install` | Emit the JSON Schemas, or install the VS Code mapping. |
| `state unlock` / `forget` / `adopt` | Maintain state: break a lock, drop a resource, adopt an existing object. |

Every verb and flag, and the exit codes (0 ok, 1 failure, 2 validation, 3 auth/preflight/tenant, 4 state lock, 5 what-if changes): [docs/cli.md](docs/cli.md).

## Modes

- **Incremental** (default): creates and updates the declared resources. Only the properties you declare are applied (Graph is merge-PATCH; omitted
  properties are untouched). A resource that disappeared from the template is *detached* from the state with a warning; the object stays.
- **Complete**: additionally deletes the objects the state manages that the template no longer declares, in reverse dependency order (edges first,
  then groups, applications, users, policies…). Preview it with `what-if --mode Complete`; security groups are deleted permanently and say so.

## State

Per deployment, `./.entraarm/state/<name>.json` next to the template (`--state-dir` or `ENTRAARM_STATE_DIR` to move it). It maps each resource id
(`/providers/Microsoft.Entra/applications/contoso-dev-web`) to the Graph object id, so displayName-named objects stay bound, and records the edges the deployment
created. It holds ids and hashes only (the serializer refuses to write a secret), is written atomically after **every** resource (a failed run leaves an accurate
partial record), is locked while a run is writing, and is pinned to the tenant (a different tenant is refused with exit 3). Safe to commit.

## Secrets

`securestring` and `secureObject` parameters, `passwordProfile.password` and `secretText` are tainted: they may flow into resource properties and `securestring`
outputs only, are `***` in every rendering, and never reach state, logs, what-if or diagnostics. A parameter file can pull a secret from **Azure Key Vault**
(`"reference": { "keyVault": { "id": … }, "secretName": … }`, resolved with `az keyvault secret show`). A client secret's text is returned by Graph exactly once:
read it from a `securestring` output with `--secure-outputs-file secrets.json` (owner-only file) or `--reveal-secure-outputs` in the run that creates or rotates it.

## What-if

`deployment what-if` (and `create -c`) shows `+ Create`, `~ Modify` with property deltas, `- Delete` (Complete mode), `= NoChange` and `* Ignore`, a `?` for values only
known at deployment time and a `!` for a change that cannot be applied as planned; `-o json` mirrors ARM's what-if shape. `--exit-code-on-changes` makes it a CI
drift check (exit 5). Details and golden fixtures: [docs/whatif.md](docs/whatif.md).

## Permissions

`entraarm permissions -f template -p …` lists, offline, the Microsoft Graph application permissions (or delegated scopes and the Entra roles, `--mode delegated`) a
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
  reset, administrative-unit scoped role members, a published `$schema` URL (use `entraarm schema install` meanwhile).
- Graph is eventually consistent: a freshly created object can be invisible for seconds, so dependent calls are retried for up to 90 s. Mail-enabled security groups cannot be
  created through Graph (adopt only). Tenant policies (an application management policy blocking secrets, licensing) can refuse a call; the Graph error is reported per resource.
- `az` is a hard dependency: there is no Azure.Identity or Graph SDK path. The delegated permission mode and a few Graph behaviours are verified by the gated integration
  tests (`ENTRAARM_IT_TENANT`), not by the unit tests.

## Documentation

| Page | Content |
|---|---|
| [docs/format.md](docs/format.md) | The template format: envelope, resource rules, type catalog, expression functions, parameters, Key Vault, JSON Schemas |
| [docs/edges.md](docs/edges.md) | Relationships and assignments declared from either side |
| [docs/permissions.md](docs/permissions.md) | The permission manifest and `entraarm permissions` |
| [docs/cli.md](docs/cli.md) | Every verb and flag, exit codes |
| [docs/whatif.md](docs/whatif.md) | What-if output, symbols, JSON shape, golden fixtures |
| [docs/engine.md](docs/engine.md) | Pipeline, dependency graph, edge pass, state, the provider contract |
| [docs/providers.md](docs/providers.md) | Writing and testing a resource provider, with a worked example per type family |
| [docs/diagnostics.md](docs/diagnostics.md) | Every diagnostic code |
| [samples/README.md](samples/README.md) | The sample templates |
| [schemas/2026-10-01](schemas/2026-10-01) | The JSON Schemas (generated per-type properties: `tools/New-TypeSchemas.ps1`) |
