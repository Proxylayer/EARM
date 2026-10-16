# EARM

ARM-style JSON templates for **Microsoft Entra ID**, applied through the **Microsoft Graph** REST API.

EARM is short for "Entra ARM" (ARM-style templates for Entra), pronounced as the letters E-A-R-M or as "earm"; the command line tool is `earm`.

> **Preview.** The template format and `apiVersion` `2026-10-01-preview` are in preview: breaking changes are possible and nothing is released
> yet. The gated integration suite (see [Contributing and testing](#contributing-and-testing)) has been run against a development
> tenant, but EARM is still a preview and is not used in production. Microsoft Graph **v1.0** is used by default
> (`apiVersion` `2026-10-01-preview`); the beta endpoint is opt-in per resource with `2026-10-01-preview.beta` (passthrough, no normalisation
> guarantees). The plain `2026-10-01` label was renamed `2026-10-01-preview` and is rejected with an `UnknownApiVersion` error that says so.

Repository: <https://github.com/Proxylayer/EARM>

Azure has ARM templates; Entra ID has no true equivalent. EARM is a template format that mimics classic ARM templates as closely as it
can, but whose resources are Entra objects under a `Microsoft.Entra` provider namespace, plus a .NET tool (`earm`) whose verbs mirror
`az deployment group …`. It reads and writes Microsoft Graph over plain HTTPS (`HttpClient`) with a token from Azure.Identity: no Graph SDK, no Azure CLI, no PowerShell
at run time, and no app registration of its own: interactive sign-in uses Microsoft's Microsoft Graph Command Line Tools app (your own public-client app is only
needed in Azure operated by 21Vianet or in tenants that block the Microsoft app).

- **Declarative and idempotent**: applications, service principals, federated credentials and client secrets, groups, users, app role
  assignments and consent grants, directory roles and custom roles, administrative units, Conditional Access policies, named locations,
  authentication strengths and contexts, PIM eligibility, time-bound assignments and policies, and the authentication methods policy. A rerun finds each object again by its key in the live tenant and updates it.
- **ARM vocabulary**: `resources[]` with `type` / `apiVersion` / `name` / `properties` / `dependsOn` / `condition` / `copy`, parameters,
  variables, user functions, outputs, `resourceId()` / `reference()` / `copyIndex()`, parameter files with Key Vault references,
  Incremental and Complete modes, **what-if** with ARM's change types.
- **Relationships from either side**: `members`, `owners`, `memberOf`, `manager`, `appRoleAssignments`, `directoryRoles`, `assignedLicenses` are
  plain arrays in `properties`, applied after all objects exist, without cycles.
- **No local state**: identity is the resource's type and `name`, resolved by a live read on every run, as in ARM. Nothing to commit, share or lock;
  the only output file a deployment run writes is the secure-outputs file you ask for (plus its `.lock` / `.previous` companions).
- **Safe by construction**: nothing the template does not declare is ever deleted (Complete mode only reports), replacements need confirmation,
  secrets tainted and never written to logs or what-if, soft-delete awareness, a Conditional Access enforcement guard.
- **Editor support**: JSON Schemas for the envelope and every resource type, installable into VS Code with one command.

## Status

Implemented (a working pre-release, see [Limitations](#limitations-v1) for what is deferred):

- **21 resource types**: `applications`, `applications/federatedIdentityCredentials`, `applications/passwordCredentials`, `servicePrincipals`,
  `groups`, `users`, `appRoleAssignments`, `oauth2PermissionGrants`, `roleAssignments`, `roleDefinitions`, `administrativeUnits`,
  `conditionalAccess/policies`, `conditionalAccess/namedLocations`, `conditionalAccess/authenticationStrengths`,
  `conditionalAccess/authenticationContextClassReferences`, `authenticationMethodConfigurations` (the authentication methods policy's tenant
  singletons: updated, never created), `roleEligibilityScheduleRequests` (PIM eligibility for directory roles, shaped like ARM's
  `Microsoft.Authorization/roleEligibilityScheduleRequests`), `roleAssignmentScheduleRequests` (time-bound active directory role assignments
  through PIM, shaped like ARM's `Microsoft.Authorization/roleAssignmentScheduleRequests`), `privilegedAccess/groupEligibilityScheduleRequests` (PIM
  for Groups eligibility: eligible membership or ownership of a group), `privilegedAccess/groupAssignmentScheduleRequests` (time-bound active
  membership or ownership of a group through PIM for Groups) and `roleManagementPolicies` (the PIM policy rules of a directory role or a group:
  approval, MFA, justification, maximum durations, notifications; shaped like ARM's `Microsoft.Authorization/roleManagementPolicies`, only the
  declared rules managed, never created or deleted) (all under `Microsoft.Entra/`).
- The verbs `deployment validate | what-if | create | delete`, `permissions`, `schema print | install` and `auth login | logout | status`.
- Tests: about 6,500 unit and in-process tests (EArm.Tests, EArm.Providers.Tests, EArm.Graph.Tests; a full `test.ps1` run, build included, takes about 2-3 minutes), against fakes of Graph (an HTTP handler over an in-memory directory)
  and of the sign-in, with about 95 % line and 89 % branch coverage ([tests/COVERAGE.md](tests/COVERAGE.md); how they are organised and reviewed: [docs/testing.md](docs/testing.md)).
  The tenant-backed integration suite is gated behind `EARM_IT_TENANT` and has been run against a development tenant: it is not part of those numbers, and a plain `test.ps1` reports it as `GATED`, not as a pass.

## Quick start

From a clean clone (prerequisite: the [.NET 10 SDK](global.json), 10.0.301 or a later 10.0.x SDK; PowerShell 7.4+ runs the repository scripts `build.ps1`, `test.ps1`
and `deploy.ps1`; see [Install](#install)):

```powershell
git clone https://github.com/Proxylayer/EARM.git
cd EARM

pwsh -File build.ps1        # build the solution: prints BUILD OK, zero warnings
pwsh -File test.ps1         # run the whole suite headless (about 2-3 minutes)
pwsh -File deploy.ps1       # pack the CLI and install it as the global tool `earm`

# offline: no sign-in or tenant needed
earm deployment validate -f samples/orders-api.entra.json -p '@samples/orders-api.parameters.json'

# against a DEV tenant you may write to
earm auth login -f samples/app-with-sp-and-secret.entra.json -p '@samples/app-with-sp-and-secret.parameters.json'   # sign in as yourself, consent what the sample needs (or set AZURE_*, see Signing in)
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
nested like an ARM child resource; changing `rotationTag` rotates the secret. Nothing is stored between runs: the next run reads the tenant again.
More in [`samples/`](samples/README.md).

## Install

> **Renamed from EntraARM.** The project and tool used to be called EntraARM / `entraarm` and are now EARM / `earm`. If you used a pre-rename
> checkout or tool: the global tool `entraarm` is replaced by `earm` (`deploy.ps1` uninstalls the old one); the environment variables
> `ENTRAARM_*` became `EARM_*`; `%LOCALAPPDATA%\EntraArm` became `%LOCALAPPDATA%\EArm`; and the template `$schema` host `entraarm.dev` became
> `schema.earm.proxylayer.net`. An old `.entraarm/` or `.earm/` state directory is no longer read and can be deleted. Details:
> [docs/cli.md](docs/cli.md#migrating-from-the-old-name).

> **Coming from a version with a state file?** EARM no longer keeps one. Delete `.earm/` (or your `--state-dir`), drop the retired options from
> your pipelines (`--state-dir`, `--max-deletes`, `--allow-delete-all`, `--delete-adopted`, `--force-unlock`, `--lock-timeout`, the `state` verbs,
> `deployment show | list | operation list`), and read the behaviour changes (Complete mode no longer deletes, changed assignments keep the old
> one, legacy password credentials, recreates ask) in [docs/cli.md](docs/cli.md#migrating-from-the-state-file-versions).

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
| A person at a keyboard | `earm auth login` (browser) or `earm auth login --device-code`, with `--tenant` for a specific tenant. No app registration: the sign-in app is Microsoft's **Microsoft Graph Command Line Tools**, and EARM acts with **your** delegated permissions, requesting per run only the scopes of the verb (read scopes for `what-if`). Consent them once with `earm auth login -f <template>` (or `--scopes ...`); an interactive run asks for a missing consent on the spot, a non-interactive one stops with exit 3 (`ConsentRequired`) before writing anything. Tokens are kept in an encrypted cache (`earm auth logout` forgets them). `--client-id` (your own public-client app) is only needed in Azure operated by 21Vianet or in tenants that block the Microsoft app. |

Consent to the Microsoft Graph Command Line Tools app is **shared** with Microsoft Graph PowerShell and the Graph CLI, and nearly every scope EARM needs requires
admin consent: read [SECURITY.md](SECURITY.md#delegated-sign-in-and-shared-consent) (never tick "Consent on behalf of your organization" by accident; how to review and
revoke) and [docs/permissions.md](docs/permissions.md#admin-consent). An `az login` session is **not** used, and the Azure CLI, Azure PowerShell and Visual Studio
credentials are deliberately excluded from the chain. `EARM_CLOUD` selects a national cloud (`usgov`, `usgovdod`, `china`).

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
earm deployment what-if  -f app.entra.json -p '@app.parameters.json' --mode Complete   # also report (never delete) what the template no longer declares
earm deployment create   -f app.entra.json -p '@app.parameters.json' -y       # apply; -y confirms replacements in unattended runs
earm deployment delete   -f app.entra.json -p '@app.parameters.json'          # delete what the template declares (asks; -y: no prompt)
earm schema install                                                         # VS Code validation of *.entra.json
```

| Verb | Purpose |
|---|---|
| `deployment validate` | Offline validation: JSON Schema, type catalog, semantic checks, expression syntax and static evaluation. Exit 2 on errors. |
| `deployment what-if` | The changes `create` would make, in ARM's vocabulary (`+ ~ - = *`) or ARM-shaped JSON (`-o json`). Never writes. |
| `deployment create` | Apply in dependency order; `-c` shows the what-if and asks before applying; a plain `create` asks only before it **replaces** objects (an immutable property changed), and `-y` skips both questions. A non-interactive run that would replace objects fails before any write without `-y`. |
| `deployment delete` | Delete the objects the template declares, found by their live keys, in reverse dependency order, after a confirmation that lists them (`-y` skips it). |
| `permissions` | Graph permissions and Entra roles a template needs (`--mode app\|delegated`, `--verb whatif\|create\|delete`, `-o json`). |
| `schema print` / `install` | Emit the JSON Schemas, or install the VS Code mapping. |

Every verb and flag, and the exit codes (0 ok, 1 failure, 2 validation, 3 auth/preflight, 5 changes with `--exit-code-on-changes`; 4 is retired): [docs/cli.md](docs/cli.md).

## Identity without state

EARM keeps **no state**: no state file, no lock, no deployment history. Like ARM, which finds an Azure resource by its type and name, EARM finds each
declared resource **in the live tenant by a key derived from its type and `name`**, on every run. Found: update it (or leave it). Not found: create it.

| Type (`Microsoft.Entra/...`) | Found by |
|---|---|
| `applications`, `groups` | `uniqueName` = the template `name` |
| `users` | `userPrincipalName` = the `name` |
| `servicePrincipals` | the `appId` of its application = the `name` |
| `applications/federatedIdentityCredentials` | its `name` under the parent application |
| `applications/passwordCredentials` | `displayName` `<name>#<rotationTag>` under the parent application (matched by `<name>`) |
| `conditionalAccess/authenticationContextClassReferences` | its id (`c1`..`c99`) = the `name` |
| `authenticationMethodConfigurations` | the method id (`fido2`, `temporaryAccessPass`, `sms`...) = the `name`; a tenant singleton that always exists (never created; delete resets or keeps it) |
| `roleAssignments`, `roleEligibilityScheduleRequests`, `roleAssignmentScheduleRequests`, `privilegedAccess/groupEligibilityScheduleRequests`, `privilegedAccess/groupAssignmentScheduleRequests`, `appRoleAssignments`, `oauth2PermissionGrants` | their tuple (principal, role or resource or group, scope / access / consent type); the `guid(...)` `name` is only a label |
| `roleManagementPolicies` | the policy assignment of (`scopeType`, `scopeId`, `roleDefinitionId`): a directory role, or a group's member / owner access; the `guid(...)` `name` is only a label; never created (a missing policy is `PimPolicyNotAvailable`) |
| `conditionalAccess/policies`, `conditionalAccess/namedLocations`, `administrativeUnits`, custom `roleDefinitions`, custom `conditionalAccess/authenticationStrengths` | `displayName` = the `name` |

What follows from it:

- **A rename is a new object** (the ARM rule). Change a resource's `name` and the next run creates a new object; the old one stays in the tenant.
  The same happens when someone renames a display-name-keyed object (a Conditional Access policy, a named location...) in the portal: it is no longer
  found, and the next run creates a second one.
- **Display names must be unique** for the display-name-keyed types: two live objects with the declared `displayName` fail the resource with
  `AmbiguousMatch` (EARM never guesses which one you meant).
- **No lock.** Nothing stops two runs at once. For the uniquely keyed types that is harmless, but two concurrent runs of one template can both create a
  display-name-keyed object, leaving duplicates (and `AmbiguousMatch` afterwards). Serialise runs of one template in your pipeline.
- **No tenant assertion**: the tenant is the one the credential signs in to (`earm auth status`).
- **Cancelled runs** leave only the secure-outputs file and the logs. A Ctrl+C stops new work, but a write already sent to Graph completes and is
  reported (and its one-time secret saved); the next run finds everything by key and converges.

Full key table and details: [docs/engine.md](docs/engine.md#identity-without-state).

## Modes

- **Incremental** (default): creates and updates the declared resources; nothing else is touched. A resource removed from the template is simply no
  longer managed: the object stays in the tenant.
- **Complete**: the same writes, plus a read-only **orphan report**: the live objects of the types the template declares that no declared resource
  matches are listed ("Not declared in the template (reported only; never deleted)", `orphans[]` in JSON). Complete mode **never deletes**, which is
  where EARM deliberately differs from ARM. The listing is bounded: only the types the template declares; the whole (small) collection for Conditional
  Access policies, named locations, custom authentication strengths, custom role definitions, administrative units and authentication contexts; the
  credentials and service principals of the declared applications; the assignments, PIM eligibilities and time-bound PIM assignments and the grants of the
  declared principals (PIM for Groups: of the declared target groups). Applications, groups and users are tenant-wide and never listed; authentication
  method configurations and PIM policies always exist, so they have no orphans.

To remove objects, run **`earm deployment delete -f <template>`**: it deletes the objects the template declares (found by key, reverse dependency order,
after a confirmation that lists them). To remove an object a template no longer declares, put it back in a template (a scratch template that declares
just it is enough) and delete with that, or remove it in the portal / Graph. The signed-in principal and service principals owned by another organization
are never deleted or replaced (`ProtectedObject`). Details: [docs/cli.md](docs/cli.md#deployment-delete), [docs/whatif.md](docs/whatif.md#orphan-report).

> **Retained assignments (security-relevant).** A role assignment, PIM eligibility or time-bound PIM assignment, app role assignment or consent grant is
> identified by its tuple. When the template changes the principal, the role, the scope or the group, EARM creates the new assignment and **keeps the old
> one, which still grants access** (an old eligibility can still be activated). It is reported in
> every what-if and result as `orphan: retained` (warning `AssignmentRetained`), in both modes, and **never removed** by EARM. Review those rows whenever
> you change an assignment, and remove the old access yourself. Details: [docs/engine.md](docs/engine.md#retained-assignments).

**Relationships**: target-side lists (`members`, `owners`) are authoritative and remove what they do not list; principal-side lists (`memberOf`,
`appRoleAssignments`, `directoryRoles`, `ownerOf`, `administrativeUnits`) are **ensure-only**: they add, and never remove an entry you drop
([docs/edges.md](docs/edges.md)).

**Replacements ask first.** An immutable change on a cheap type (an assignment, a credential, a named location's kind) is applied as delete,
then create. `create` lists those objects and asks `Replace them? (y/n)`; `-y` confirms, and without `-y` a non-interactive run fails before anything is
written (`RecreateNotConfirmed`, exit 1). On every other type (applications, service principals, groups, users, custom roles, authentication strengths,
administrative units...) an immutable change is an error instead (`ImmutablePropertyChanged`).

## Privileged access (PIM)

Directory roles and group access can be **just in time** through Privileged Identity Management, written in ARM's PIM vocabulary:

```json
{ "type": "Microsoft.Entra/roleManagementPolicies", "apiVersion": "2026-10-01-preview", "name": "[guid('policy', 'Security Administrator')]",
  "properties": { "role": "Security Administrator", "rules": [
    { "id": "Expiration_EndUser_Assignment", "isExpirationRequired": true, "maximumDuration": "PT8H" },
    { "id": "Enablement_EndUser_Assignment", "enabledRules": ["MultiFactorAuthentication", "Justification"] } ] } },
{ "type": "Microsoft.Entra/roleEligibilityScheduleRequests", "apiVersion": "2026-10-01-preview", "name": "[guid('sec-admins', 'Security Administrator')]",
  "properties": { "principalId": "[reference('sec-admins').id]", "role": "Security Administrator",
                  "scheduleInfo": { "expiration": { "type": "AfterDuration", "duration": "P365D" } } } }
```

- **Eligible** (`roleEligibilityScheduleRequests`, `privilegedAccess/groupEligibilityScheduleRequests`) or **time-bound active**
  (`roleAssignmentScheduleRequests`, `privilegedAccess/groupAssignmentScheduleRequests`); permanent access stays `roleAssignments` / `directoryRoles` and
  the group's `members` / `owners`, and the template may not declare both for the same tuple (`PimAssignmentConflict`).
- Graph's PIM APIs are request-based; EARM finds the **schedule** by its tuple and sends `adminAssign`, `adminUpdate` or `adminRemove` itself, so a rerun
  converges like any other type. `roleManagementPolicies` changes only the declared rules, is never created or deleted, and is applied **before** the
  requests of its role or group.
- A group's first request onboards it to PIM for Groups permanently; a group policy may only appear after that, in which case the policy applies on
  the second run. A changed tuple keeps the old eligibility (`orphan: retained`).
- Needs **Microsoft Entra ID P2** or **ID Governance**; the permissions are escalation-capable ([SECURITY.md](SECURITY.md#privileged-access-and-authentication-methods)).

Concepts: [docs/pim.md](docs/pim.md); a combined example with emergency access, a Conditional Access policy on the PIM authentication context and the
authentication methods policy: [`samples/privileged-access.entra.json`](samples/privileged-access.entra.json).

## Secrets

`securestring` and `secureObject` parameters, `passwordProfile.password` and `secretText` are tainted: they may flow into resource properties and `securestring`
outputs only, are `***` in every rendering, and never reach logs, what-if or diagnostics.

**Secrets are write-once.** Graph never returns a secret, so EARM cannot compare one: a secret-bearing value is sent when the object is created and never
compared or re-sent on an existing object (a rerun would otherwise always change it). Changing a user's `passwordProfile.password` in the template does not
reset the password; rotate a client secret with `rotationTag`.

A parameter file can pull a secret from **Azure Key Vault**
(`"reference": { "keyVault": { "id": … }, "secretName": … }`, read over HTTPS from the vault with the signed-in identity's Key Vault token). A client secret's text is returned by Graph exactly once:
read it from a `securestring` output with `--secure-outputs-file ./secure-outputs.json` (an owner-only file, checked before anything is deployed) or `--reveal-secure-outputs`
in the run that creates or rotates it.

One-time secrets (a password credential's `secretText`, a generated user password) are written to the secure-outputs file **immediately** after the call that produced them,
under the reserved top-level key `oneTimeSecrets` (by resource id), so a crash or Ctrl+C cannot lose them; a rerun merges into the file and never replaces it with a smaller one.
Treat that file like a password store: it is the only copy, it is in clear text, and it must not be committed ([.gitignore](.gitignore) covers `secure-outputs*.json`,
`*.secrets.json` and `secrets.json`). See [SECURITY.md](SECURITY.md) for the threat model.

## What-if

`deployment what-if` (and `create -c`) shows `+ Create`, `~ Modify` with property deltas (a replacement says the object is deleted and created again), `= NoChange` and
`* Ignore` (the orphan report rows are `Ignore` rows marked `orphan: undeclared` / `orphan: retained`), a `?` for values only
known at deployment time and a `!` for a change that cannot be applied as planned; `-o json` mirrors ARM's what-if shape. `--exit-code-on-changes` makes it a CI
drift check (exit 5). Details and golden fixtures: [docs/whatif.md](docs/whatif.md).

## Permissions

`earm permissions -f template -p …` lists, offline, the Microsoft Graph application permissions (or delegated scopes and the Entra roles, `--mode delegated`) a
template needs, grouped by permission with the resources that need them, including the conditional ones (`Application.ReadWrite.OwnedBy` instead of `.All`,
`RoleManagement.ReadWrite.Directory` for role-assignable groups and directory roles, `AppRoleAssignment.ReadWrite.All` for assignments, the PIM and
authentication-method permissions…) and the Key Vault note.
Manifest tables and warnings: [docs/permissions.md](docs/permissions.md).

## Limitations (v1)

- Types: applications (+ federated credentials and password credentials), service principals, groups, users (member users), app role assignments,
  OAuth2 permission grants, directory role assignments and custom role definitions, administrative units, Conditional Access policies, named locations,
  authentication strengths and authentication contexts, PIM eligibility, time-bound PIM assignments and PIM policies for directory roles and groups, and the
  authentication method configurations. The provider registry makes more types cheap to add ([docs/providers.md](docs/providers.md)).
- **Deferred**: gallery / template application instantiation, application logo, SAML token-signing certificates, group settings templates and lifecycle policies,
  B2B invitations (guests), devices, cross-tenant access, custom security attributes, entitlement management, access reviews, lifecycle workflows, terms of use,
  PIM for Azure resources (ARM's own `Microsoft.Authorization` types cover it) and PIM alerts, the authentication methods policy's tenant-wide settings
  (registration campaign and the other root properties) and the beta-only methods (hardware OATH, QR code), nested deployments.
- **Later (v1.1+)**: property-level `copy`, `--rollback-on-error`, `export` (template from a tenant), `deployment validate --online`, user password
  reset, name-prefix scoping for the orphan report of applications, groups and users, administrative-unit scoped role members, a published `$schema` URL (use `earm schema install` meanwhile).
- Graph is eventually consistent: a freshly created object can be invisible for seconds, so dependent calls are retried for up to 90 s. Mail-enabled security groups cannot be
  created through Graph (an existing one is managed when it is found by its `uniqueName`). Tenant policies (an application management policy blocking secrets, licensing) can refuse a call; the Graph error is reported per resource.
- **Input limits** (a violation is a diagnostic, never a hang or a crash): a template file and a parameter file are each read only up to **8 MiB** (`InputTooLarge`, exit 1; also more than 2,000,000 JSON values), a variable may read at most **128** levels of other variables (`VariableChainTooDeep`), and expression length and depth, value sizes, `copy` expansion (800 per resource), evaluation steps and wall-clock time (`--evaluation-timeout`, default 60 s per evaluation) are bounded too: [docs/format.md](docs/format.md#evaluation-limits).
- Sign-in is Azure.Identity only (no Azure CLI session, no Graph SDK). The delegated sign-in (per-run scopes, consent classification and the consent prompt) and a few
  Graph behaviours are verified by the gated integration tests (`EARM_IT_TENANT`, `EARM_IT_CONSENT_PROBE`), not by the unit tests.

## Documentation

| Page | Content |
|---|---|
| [docs/format.md](docs/format.md) | The template format: envelope, resource rules, type catalog, expression functions, parameters, Key Vault, JSON Schemas |
| [docs/edges.md](docs/edges.md) | Relationships and assignments declared from either side |
| [docs/permissions.md](docs/permissions.md) | The permission manifest and `earm permissions` |
| [docs/cli.md](docs/cli.md) | Every verb and flag, exit codes |
| [docs/whatif.md](docs/whatif.md) | What-if output, symbols, JSON shape, golden fixtures |
| [docs/engine.md](docs/engine.md) | Pipeline, dependency graph, edge pass, identity without state, the orphan report, the provider contract |
| [docs/providers.md](docs/providers.md) | Writing and testing a resource provider, with a worked example per type family |
| [docs/pim.md](docs/pim.md) | Privileged Identity Management: eligible vs active, request-based APIs, policy ordering, group onboarding, conflicts, licensing |
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
| `src/EArm.Engine` | Planner, dependency graph, executor, what-if, orphan report, the provider contract |
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
variables `EARM_IT_TENANT` (required), `EARM_IT_PREFIX`, `EARM_IT_DOMAIN`, `EARM_IT_ALLOW_CA`, `EARM_IT_ALLOW_ROLES`, `EARM_IT_SWEEP` and `EARM_IT_INTERACTIVE`; read
[tests/EArm.Integration.Tests/README.md](tests/EArm.Integration.Tests/README.md) first (what it creates, how it cleans up, which permissions it needs).
The suites, coverage (`test.ps1 -Coverage`, [tests/COVERAGE.md](tests/COVERAGE.md)) and how the tests are reviewed: [docs/testing.md](docs/testing.md).
A `-Filter` that matches no test exits 3 (a typo is not a green run); tests that assert a wall-clock bound run in a second, serial pass (`-SkipTiming` skips it while iterating).
CI (`.github/workflows/ci.yml`) runs `build.ps1` and `test.ps1` on Windows and Linux, then builds Release and packs the tool as an artifact. To report a vulnerability, see [SECURITY.md](SECURITY.md).

## Licence

No licence file yet: all rights reserved until one is added.
