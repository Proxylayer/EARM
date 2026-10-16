# EArm.Integration.Tests

Real-tenant tests for EARM. They drive the production stack (Azure.Identity -> `HttpClient` -> Microsoft Graph) against a
**dev tenant** and are **skipped cleanly** unless `EARM_IT_TENANT` is set. `test.ps1` leaves this project out of a plain run
(`GATED      EArm.Integration.Tests (not run: EARM_IT_TENANT is not set ...)`: not a pass); naming it with `-Target` always runs
it, and every test then skips with the reason when the variable is unset (reported as `SKIPPED ... gated by EARM_IT_TENANT`, again not a pass).

Two kinds of tests live here:

| Kind | Classes | Purpose |
|---|---|---|
| Scenarios | `ApplicationScenarioTests`, `DirectoryScenarioTests`, `ConditionalAccessScenarioTests`, `RoleScenarioTests`, `CleanupScenarioTests` | Create, what-if (NoChange), drift, Complete mode (report only, never deletes), `deployment delete -f` (reverse dependency order), secure outputs and edges, run through the real CLI in-process (`Program.BuildRootCommand`). EARM keeps no state, so every scenario finds its objects by key, as a real run does. |
| Verification probes (`[Trait("Category","Verify")]`) | `GraphBehaviourProbes` | One test per open verification TODO. Each exercises one real Graph behaviour the code assumes and asserts the assumption. |
| Spike probes (`[Trait("Category","Spike")]`) | `DelegatedAuthSpikeProbes` | Interactive, separately gated (`EARM_IT_AUTH_SPIKE=1`); record how Microsoft Entra answers delegated-auth questions. See [Delegated-auth spike (DES-0002)](#delegated-auth-spike-des-0002). |

## Prerequisites

1. **The .NET 10 SDK** (and PowerShell 7.4+ for `build.ps1` / `test.ps1`). No Azure CLI is needed or used.

2. **A dev tenant you can throw objects away in.** Never a production tenant. It needs a **verified domain** (the default
   `*.onmicrosoft.com` domain is fine) and Microsoft Entra ID P1 (or a trial) for the Conditional Access, authentication strength,
   custom role and role-assignable group scenarios. No Azure subscription is needed.

3. **A sign-in for that tenant.** The fixture builds the same stack as the CLI: `EARM_AUTH` / `EARM_TENANT_ID` / `EARM_CLIENT_ID` / `EARM_CLOUD` and the `AZURE_*`
   variables apply (see [docs/cli.md](../../docs/cli.md#signing-in)). Either set a service principal in the environment

   ```powershell
   $env:AZURE_TENANT_ID = '<tenant-id>'; $env:AZURE_CLIENT_ID = '<appId>'; $env:AZURE_CLIENT_SECRET = '<secret>'
   ```

   or sign in once with `earm auth login --tenant <tenant-id>`. The preflight (token claims, `GET /organization`) runs once per test run; when nothing signs in, or the
   token belongs to another tenant than `EARM_IT_TENANT`, every test skips with a message saying so.

4. **Permissions.** What the signed-in identity needs is the union of the groups you run (`earm permissions` computes the
   same lists from a template; see [docs/permissions.md](../../docs/permissions.md) for the manifest and the privilege-escalation warning).
   Two ways to get them:

   * a **user** who holds Global Administrator (simplest; the default public client of `earm auth login` is delegated and does not carry every granular
     Graph scope, Conditional Access in particular, so some scenarios may fail with a 403 that names the missing scope; register your own public-client
     app with the delegated permissions and pass `--client-id`), or
   * a **service principal** with the application permissions below, admin consent granted, signed in through the `AZURE_*` variables above. This is the
     better choice for the Conditional Access, role and grant scenarios.

   | Test group | Graph application permissions |
   |---|---|
   | Every test (preflight, cleanup guard) | `Organization.Read.All`, `Application.ReadWrite.All`, `Group.ReadWrite.All`, `User.ReadWrite.All`, `AdministrativeUnit.ReadWrite.All`, `Policy.Read.All`, `RoleManagement.Read.Directory` (the sweep lists and deletes what a run created) |
   | `ApplicationScenarioTests` | `Application.ReadWrite.All` |
   | `DirectoryScenarioTests` (groups, users, guest) | `User.ReadWrite.All`, `Group.ReadWrite.All`, `GroupMember.ReadWrite.All`, `User.Read.All` |
   | `ConditionalAccessScenarioTests` | `Policy.ReadWrite.ConditionalAccess`, `Policy.Read.All`, `Application.Read.All`, `Group.ReadWrite.All`; strengths additionally `Policy.ReadWrite.AuthenticationMethod` |
   | `RoleScenarioTests` | `RoleManagement.ReadWrite.Directory`, `AdministrativeUnit.ReadWrite.All`, `Group.ReadWrite.All`, `AppRoleAssignment.ReadWrite.All`, `DelegatedPermissionGrant.ReadWrite.All`, `Application.Read.All`, `Application.ReadWrite.All` |
   | Probes | the union of the above; `B4` (user restore) additionally documents whether `User.DeleteRestore.All` is needed |
   | Purging soft-deleted objects (cleanup) | the type's `*.ReadWrite.All`; users may need `User.DeleteRestore.All` |
5. **Test users and guests.** Users are created and deleted by the tests; they need only the verified domain
   (`EARM_IT_DOMAIN`), get no licenses, and are never signed in. `It_Users_GuestWithExtSuffixIsReadByReference` reads an existing guest
   (a user whose UPN contains `#EXT#`): invite one external address by hand (B2B invitation) once; without a guest that test skips.

## Configuration

| Variable | Required | Meaning |
|---|---|---|
| `EARM_IT_TENANT` | yes | Tenant id (GUID) the tests may write to. Unset: everything skips. Must equal the tenant of the sign-in. |
| `EARM_IT_PREFIX` | no | Name prefix of every object the run creates, 10-40 chars of `a-z0-9-` starting with a letter. Default `earm-it-<6 hex>`, new for every run. The cleanup guard only ever touches objects whose name starts with it. |
| `EARM_IT_DOMAIN` | for user tests | A verified domain for user principal names, for example `contoso.onmicrosoft.com`. Tests that create users skip without it. |
| `EARM_IT_ALLOW_CA` | `=1` | Required for every Conditional Access **policy**, authentication **strength** and **context** test, and for the orders variant. Named locations do not need it. |
| `EARM_IT_ALLOW_ROLES` | `=1` | Required for the tests that write directory roles, administrative units, role assignments, app role assignments and consent grants (all of `RoleScenarioTests`, including the orders variant). |
| `EARM_IT_SWEEP` | `=1` | Enables `It_Cleanup_ManualSweepOfTheConfiguredPrefix` (see Manual cleanup). |

## Running

```powershell
$env:EARM_IT_TENANT = '<tenant-id>'
$env:EARM_IT_DOMAIN = 'contoso.onmicrosoft.com'
$env:EARM_IT_ALLOW_CA = '1'        # optional, see the table
$env:EARM_IT_ALLOW_ROLES = '1'     # optional, see the table

pwsh -File build.ps1 -Target EArm.Integration.Tests                                   # compile only
pwsh -File test.ps1 -Target EArm.Integration.Tests                                    # everything that is enabled
pwsh -File test.ps1 -Target EArm.Integration.Tests -Filter "Category=Verify"          # the probes only
pwsh -File test.ps1 -Target EArm.Integration.Tests -Filter "FullyQualifiedName~ApplicationScenarioTests"
pwsh -File test.ps1 -Target EArm.Integration.Tests -Filter "FullyQualifiedName~It_Probe_F1" -Detailed   # diagnose one probe
```

With `EARM_IT_TENANT` set, `pwsh -File test.ps1` (no `-Target`) also includes this project. All classes share one xUnit collection
and run sequentially: they share the sign-in and the cleanup guard, and the in-process CLI has static global options.

| Test | Needs besides the tenant |
|---|---|
| `It_Applications_RoundTripWithServicePrincipalFicAndSecret` | - |
| `It_Groups_MemberOfMembersOwnersEdgesOnBothSides` | `DOMAIN` |
| `It_Users_GeneratedPasswordUsageLocationAndSecureOutput` | `DOMAIN` |
| `It_Users_GuestWithExtSuffixIsReadByReference` | a guest user in the tenant |
| `It_NamedLocations_IpAndCountryRoundTrip` | - |
| `It_ConditionalAccess_ReportOnlyPolicyWithLocationAndGroup` | `ALLOW_CA` |
| `It_ConditionalAccess_StrengthAndContextLifecycleWithDeleteOrder` | `ALLOW_CA` |
| `It_Roles_CustomRoleAdministrativeUnitAssignmentAndDirectoryRolesEdge` | `ALLOW_ROLES` |
| `It_Roles_AppRoleAssignmentsAndOAuth2GrantsAgainstMicrosoftGraph` | `ALLOW_ROLES` |
| `It_Roles_OrdersSampleVariantCompleteModeAndDeleteRoundTrip` | `DOMAIN`, `ALLOW_CA`, `ALLOW_ROLES` |
| `It_Cleanup_SweepRemovesTrackedGroupWithThePrefix` | - |
| `It_Cleanup_ManualSweepOfTheConfiguredPrefix` | `SWEEP=1` |
| `It_Probe_A` ... `It_Probe_G` (14 probes) | A, B1, B2, B3, D, F1-F4, G: -; B4: `DOMAIN`; C, E1, E2: `ALLOW_CA` |

## Cost and safety

* No Azure subscription resources are created and no licenses are assigned: Entra objects only, no cost. Users have no license and
  never sign in; their passwords are generated or random and are never printed (secure outputs go to an owner-only file in the run's
  scratch directory, which is deleted at the end).
* Conditional Access policies are always `enabledForReportingButNotEnforced`. **No test passes `--allow-ca-enforce`.** The enforce-guard
  test proves that `state: enabled` is refused. The guard never deletes an *enforced* policy it finds with the prefix; it reports it.
* Authentication contexts use ids `c99` downwards (never below `c50`); an id that already exists is skipped, so a foreign context is
  never overwritten.
* Privileged writes (role assignments, app role assignments such as `User.Read.All` on a throw-away service principal, tenant-wide
  consent for `User.Read`) are behind `EARM_IT_ALLOW_ROLES=1`. Read [docs/permissions.md](../../docs/permissions.md) before giving a
  service principal `RoleManagement.ReadWrite.Directory` or `AppRoleAssignment.ReadWrite.All`.
* Tenant limits to keep in mind: 500 role-assignable groups, soft-deleted objects count against some quotas for 30 days.
* Run it in a tenant that no one else is changing at the same time: what-if assertions expect NoChange right after a create.

## Cleanup guarantees

Every run has one prefix (`Fixture.Prefix`), and every object a test creates carries it (the templates take a `prefix` parameter). The scenarios
remove their own deployments with `earm deployment delete -f <template> -p prefix=<prefix> -y` (the production delete: the objects the template
declares, found by key, in reverse dependency order). There is no state directory to clean up. At teardown (`IntegrationFixture.DisposeAsync`), also
when tests failed, the fixture removes whatever is left:

1. **Prefix sweep** (`TenantCleanup`): objects created directly through Graph and tracked with `Fixture.Track(...)`, then everything whose
   `displayName`, `userPrincipalName` or `uniqueName` starts with the prefix, in dependency order: Conditional Access policies (not
   enforced ones), authentication strengths, contexts (disabled first), named locations, role assignments of owned principals and roles,
   custom role definitions, administrative units, groups, users, service principals (also by the `appId` of owned applications), applications.
   Every delete re-checks the prefix right before it is sent and refuses otherwise. A prefix must be at least 10 characters.
2. **Purge**: the soft-deleted copies (`/directory/deletedItems/microsoft.graph.{application,servicePrincipal,group,user,administrativeUnit}`)
   with the prefix are removed with `DELETE /directory/deletedItems/{id}`, so no name stays reserved for 30 days.
3. **Report**: `%TEMP%\earm-it\<prefix>\cleanup.log` and a diagnostic message list every action. Anything that could not be removed is
   a `LEFTOVER` line and makes the fixture teardown fail.

The sweep is what catches the objects `deployment delete` cannot: an object a test renamed (a new key is a new object, the old one stays), the old
assignment of a changed tuple (retained, never removed by EARM), and anything a failed run or a test left behind.

### Manual cleanup

After a crash (power loss, killed process) the teardown may not have run. Use the prefix printed in the test output or found under
`%TEMP%\earm-it\`:

```powershell
$env:EARM_IT_TENANT = '<tenant-id>'
$env:EARM_IT_PREFIX = 'earm-it-1a2b3c'      # the leaked run's prefix
$env:EARM_IT_SWEEP  = '1'
pwsh -File test.ps1 -Target EArm.Integration.Tests -Filter "FullyQualifiedName~It_Cleanup_ManualSweep"
```

or with plain Graph calls (any HTTP client with a Graph token; one object type shown, repeat for groups, users, servicePrincipals, `directory/administrativeUnits`):

```
GET    https://graph.microsoft.com/v1.0/applications?$filter=startswith(displayName,'earm-it-1a2b3c')&$select=id,displayName
DELETE https://graph.microsoft.com/v1.0/applications/<id>
DELETE https://graph.microsoft.com/v1.0/directory/deletedItems/<id>      # purge the soft-deleted copy
```
or with the tool itself, for the objects a scenario template declares: `earm deployment delete -f tests/EArm.Integration.Tests/Templates/<template> -p prefix=<prefix> -y`
(add the scenario's other `-p` values, such as the domain). That removes only what the template declares under that prefix; follow it with the sweep above for the rest.
Conditional Access policies, strengths and named locations are under *Entra ID > Security > Conditional Access* in the portal.

## How to read probe failures

A probe asserts an assumption the code makes and names the file to change; the observations go to the test output (diagnostic messages,
`-Detailed` shows them). Letters follow the verification TODOs:

| Probe | Asserts | A red test means |
|---|---|---|
| `A` transport | `%` and `&` and spaces in a `$filter` value survive the percent-encoding, the token is issued for the right tenant and audience, and a 3-page `@odata.nextLink` walk returns every item | Fix `src/EArm.Graph/GraphUrl.cs` (`BuildQuery`) or the paging loop in `HttpGraphClient.cs` |
| `B1` alternate key + action | `POST /applications/{id}/addPassword` returns `keyId` + `secretText`; the `applications(uniqueName=...)/addPassword` form is **recorded** (works or not), the provider does not depend on it | `PasswordCredentialsProvider.cs`; when the alternate-key form works, the object-id lookup is optional |
| `B2` role assignment filter | `?$filter=principalId eq '...'` works (what the provider uses); `... and roleDefinitionId eq ...` is **recorded** | `RoleAssignmentsLookup.cs` (only if the single filter fails) |
| `B3` scope ids | every `WellKnownIds.GraphScopes` / `GraphAppRoles` id equals the live Microsoft Graph service principal's; lists the `Verified: false` entries now confirmed | `src/EArm.Core/WellKnownIds.cs` |
| `B4` restore | a deleted user can be restored with the signed-in identity (records whether `User.DeleteRestore.All` is needed) | `docs/permissions.md`, users overlay `permissions` |
| `C` CA nulls | `[]` clears a collection inside `conditions`; `null` removes `sessionControls`; `null` for a collection is **recorded** | `ConditionalAccessPoliciesRules.FinishPatchBody` / `MarkRemovedControls` |
| `D` password credentials | `addPassword` response shape; `$select=id,passwordCredentials` on the alternate key; unknown-`keyId` `removePassword` failure is recognised by `IsUnknownKeyId` | `PasswordCredentialsRules.cs`, `PasswordCredentialsProvider.cs` |
| `E1` context | the context upsert answers 201 + object, 200 or 204; delete after `isAvailable: false` works | `AuthenticationContextClassReferencesProvider.cs` |
| `E2` strength | `allowedCombinations` accepts mixed casing and compares equal after normalising; `updateAllowedCombinations`; the collection filter is **recorded** | `AuthenticationStrengthsRules.cs` / `Provider.cs` |
| `F1` soft delete | `$filter=uniqueName eq` on `directory/deletedItems/microsoft.graph.application` is accepted and returns `uniqueName` | `ApplicationsProvider.FindSoftDeletedAsync` (the list fallback is primary then) |
| `F2` redirectUriSettings | an explicit `null` for the live-only `web.redirectUriSettings` is accepted and keeps the siblings | `nullable` in `schemas/2026-10-01/types/Microsoft.Entra/applications.json` |
| `F3` token version | fresh application reads `requestedAccessTokenVersion` null (or 1); 2 sticks; 1 reads back as null or 1 | `ApplicationsRules.cs` null-as-1 rule |
| `F4` app role value change | disable PATCH then re-enable-and-change-value PATCH works (a single-PATCH attempt is **recorded**) | `ApplicationsRules.cs` / `ApplicationsProvider.UpdateAsync` |
| `G` nested nulls | per sub-property of `web`, `api`, `info`, `spa`, `publicClient`: an explicit `null` clears scalars and collections, but not `web.implicitGrantSettings` / `api.requestedAccessTokenVersion`; prints the whole table | the `nullable` map of the applications overlay |

When all probes of a letter are green the matching verification item can be closed; paste the diagnostic output of
the **recorded** items into the TODO or into [docs/providers.md](../../docs/providers.md).

## Delegated-auth spike (DES-0002)

Phase 0 of DES-0002: EARM's user sign-in moves from the Azure CLI public client with `graph/.default` to the **Microsoft Graph Command
Line Tools** public client (`14d82eec-204b-4c2f-b7e8-296a70dab67e`) with explicit delegated Graph scopes. `DelegatedAuthSpikeProbes`
(`[Trait("Category","Spike")]`) answers the open questions against your dev tenant before any product code changes. The probes are
**interactive** (browser sign-in and consent prompts), so they run only on request: without `EARM_IT_AUTH_SPIKE=1` every one of them skips
with the reason, also in a full `test.ps1` run.

They do not use the real-tenant fixture and never touch EARM's own sign-in: no `earm auth login` record, no `earm` token cache. They use
Azure.Identity directly (`InteractiveBrowserCredential`, or `DeviceCodeCredential`) with their own persisted cache **`earm-spike`** and record
files under `%TEMP%\earm-it\auth-spike\`, and delete both when the class finishes. They run alone (no parallel collection), in the order
S1..S5, and share **one** interactive sign-in per client id: expect one browser prompt for the Graph CLI Tools client, one more for the
incremental consent of S2 (and possibly for Key Vault in S1), and up to two for the optional Azure CLI comparison.

The probes **record** what Microsoft Entra answers; they fail only when the harness cannot run (bad configuration, the shared sign-in was
cancelled or refused). Every probe prints its diagnostics (client id, requested scopes, outcome, the token claims `aud`, `scp`, `tid`,
`appid`, never the token, and the AADSTS code and message of a failure) and ends with a summary of `RESULT` lines. Everything is also
appended live to `%TEMP%\earm-it\auth-spike\spike-<timestamp>.log`, which is kept.

| Variable | Required | Meaning |
|---|---|---|
| `EARM_IT_AUTH_SPIKE` | `=1` | Enables the spike. |
| `EARM_IT_TENANT` | yes | The dev tenant the spike signs in to. |
| `EARM_CLOUD` | no | `public` (default), `usgov`, `usgovdod`, `china`: authority, Graph and Key Vault endpoints come from `GraphCloud` (S5 reports them). |
| `EARM_IT_AUTH_SPIKE_EXTRA_SCOPES` | no | Comma-separated Graph scope names S2 adds incrementally. Default `Policy.Read.All`. Pick one you have **not** consented to for the Graph CLI Tools client, or S2c cannot show the consent error. |
| `EARM_IT_KEYVAULT_URL` | no | `https://<vault>.vault.azure.net` (a vault of the selected cloud). With it, S1 lists secret metadata with the Key Vault token and reports the HTTP status only. |
| `EARM_IT_KEYVAULT_SECRET` | no | A secret name in that vault: S1 lists its versions (metadata, no value) instead of the vault's secrets. |
| `EARM_IT_AUTH_SPIKE_AZCLI` | `=1` | Runs S4, the Azure CLI client comparison. |
| `EARM_IT_AUTH_SPIKE_DEVICECODE` | `=1` | Device code instead of the browser (headless). The code is written to standard error and the live log; watch the log in a second terminal. |
| `EARM_IT_AUTH_SPIKE_KEEP` | `=1` | Keeps the `earm-spike` cache and the record files (to re-run probes without signing in again). Delete them later with a run without it. |

```powershell
$env:EARM_IT_TENANT = '<tenant-id>'
$env:EARM_IT_AUTH_SPIKE = '1'
$env:EARM_IT_KEYVAULT_URL = 'https://<vault>.vault.azure.net'   # optional (S1c)
$env:EARM_IT_AUTH_SPIKE_AZCLI = '1'                             # optional (S4)

pwsh -File test.ps1 -Target EArm.Integration.Tests -Filter "Category=Spike" -Detailed                                   # all probes, diagnostics streamed
pwsh -File test.ps1 -Target EArm.Integration.Tests -Filter "FullyQualifiedName~It_Spike_S2" -Detailed                   # one probe
Get-Content (Get-ChildItem $env:TEMP\earm-it\auth-spike\spike-*.log | Sort-Object LastWriteTime | Select-Object -Last 1) -Wait   # live log (device code)
Select-String -Path $env:TEMP\earm-it\auth-spike\spike-*.log -Pattern 'RESULT'                                          # the answers

# headless:
$env:EARM_IT_AUTH_SPIKE_DEVICECODE = '1'
```

Without `EARM_IT_AUTH_SPIKE` the same filter reports `SKIPPED ... ` (every probe skipped with the reason); that is the expected result of a
normal run.

| RESULT | Meaning | Feeds the decision |
|---|---|---|
| `S1a` / `S1b` | Can the Graph CLI Tools client get a Key Vault token (`https://vault.azure.net/user_impersonation`, `/.default`)? `GRANTED [silently ...]` means the Graph sign-in's refresh token is enough; `GRANTED [... then interactive]` means an extra prompt; `DENIED (AADSTS65002)` or `(AADSTS650057)` means the client is not pre-authorized for Key Vault. | **Key Vault approach**: one client for Graph and `reference()` secrets, or a second client / credential for Key Vault. |
| `S1c` | HTTP status of a metadata `GET` against the vault with that token: `200` usable, `401` token rejected, `403` token accepted but no data-plane permission (RBAC / access policy). | Confirms S1a/S1b in practice. |
| `S2a` | The base sign-in (`User.Read`, `Organization.Read.All`). | Baseline of the login flow. |
| `S2b` | Silent request with a new credential, the record and `DisableAutomaticAuthentication`: expected `GRANTED`. | The silent path of every later command works as today. |
| `S2c` | Silent request for base + extra scopes: the exception type and AADSTS code (expected `AUTH-REQUIRED [AuthenticationRequiredException]`, often `AADSTS65001`), with no prompt. `GRANTED` means the extra scopes were already consented (revoke and re-run). | **Silent consent error mapping**: what EARM must recognise and turn into "consent needed: run `earm auth login --scopes ...`". |
| `S2d` | The same set with prompting allowed: the incremental consent prompt. | **Incremental-consent flow** of `earm auth login` / the permission check. |
| `S2e` | A fresh credential with the same record, silent, extended set: expected `GRANTED`. | Consent is persisted and the record + cache serve it silently afterwards. |
| `S3` | Silent `graph/.default` after consent: which scopes `scp` carries; whether every scope consented in the run is present. | **Whether `.default` suffices** after consent, or EARM must always request explicit scopes. |
| `S4a` / `S4b` | Azure CLI client: `Policy.ReadWrite.ConditionalAccess` (expected `DENIED (AADSTS65002)`), Key Vault `.default` (expected `GRANTED`). | **Confirms the AADSTS65002 root cause**: the Azure CLI client is not pre-authorized for granular Graph scopes. |
| `S5` | The cloud, authority, token endpoint, Graph and Key Vault endpoints used (and the authority of each sign-in record). | USGov / other clouds work through `GraphCloud`. |

> **Consent side effect.** Consenting to scopes for the Microsoft Graph Command Line Tools client is **not** private to EARM or to this spike: the
> same enterprise application (and its oauth2PermissionGrant) is used by Microsoft Graph PowerShell and the Graph CLI, so whatever you consent to
> here they get too, and the spike's cleanup does not revoke it. On the consent prompt, do **not** tick "Consent on behalf of your organization"
> unless you intend a tenant-wide grant for every user. To revoke afterwards: *Entra ID > Enterprise applications > Microsoft Graph Command Line
> Tools > Permissions* (user consent under *User consent*, admin consent with *Review permissions* / revoke), or delete the grant with Graph:
> `GET /oauth2PermissionGrants?$filter=clientId eq '<servicePrincipal id of 14d82eec...>'` then `DELETE /oauth2PermissionGrants/<id>` (or `PATCH`
> its `scope` to remove just the extra scope).
