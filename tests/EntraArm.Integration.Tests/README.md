# EntraArm.Integration.Tests

Real-tenant tests for EntraARM (work item WI-19). They drive the production stack (`pwsh` -> `az rest` -> Microsoft Graph) against a
**dev tenant** and are **skipped cleanly** unless `ENTRAARM_IT_TENANT` is set. `test.ps1` leaves this project out of a plain run
(`SKIP EntraArm.Integration.Tests (ENTRAARM_IT_TENANT is not set)`); naming it with `-Target` always runs it, and every test then skips
with the reason when the variable is unset.

Two kinds of tests live here:

| Kind | Classes | Purpose |
|---|---|---|
| Scenarios | `ApplicationScenarioTests`, `DirectoryScenarioTests`, `ConditionalAccessScenarioTests`, `RoleScenarioTests`, `AdoptScenarioTests`, `CleanupScenarioTests` | Create, what-if (NoChange), drift, Complete-mode delete, secure outputs, edges and adopt, run through the real CLI in-process (`Program.BuildRootCommand`). |
| Verification probes (`[Trait("Category","Verify")]`) | `GraphBehaviourProbes` | One test per open verification TODO. Each exercises one real Graph / Azure CLI behaviour the code assumes and asserts the assumption. |

## Prerequisites

1. **Azure CLI and PowerShell 7.4+**

   ```powershell
   winget install Microsoft.AzureCLI
   winget install Microsoft.PowerShell      # if pwsh is missing
   ```

2. **A dev tenant you can throw objects away in.** Never a production tenant. It needs a **verified domain** (the default
   `*.onmicrosoft.com` domain is fine) and Microsoft Entra ID P1 (or a trial) for the Conditional Access, authentication strength,
   custom role and role-assignable group scenarios. No Azure subscription is needed.

3. **A signed-in Azure CLI session for that tenant**

   ```powershell
   az login --allow-no-subscriptions --tenant <tenant-id>
   ```

   The preflight (`az version`, `az account show`, `GET /organization`) runs once per test run; when you are not logged in, or logged in
   to another tenant than `ENTRAARM_IT_TENANT`, every test skips with a message saying so.

4. **Permissions.** What the identity behind `az login` needs is the union of the groups you run (`entraarm permissions` computes the
   same lists from a template; see [docs/permissions.md](../../docs/permissions.md) for the manifest and the privilege-escalation warning).
   Two ways to get them:

   * a **user** who holds Global Administrator (simplest; the Azure CLI's own token is delegated, and it does not carry every granular
     Graph scope, Conditional Access in particular, so some scenarios may fail with a 403 that names the missing scope), or
   * a **service principal** with the application permissions below, admin consent granted, then
     `az login --service-principal -u <appId> -p <secret> --tenant <tenant-id> --allow-no-subscriptions`. This is the better choice for
     the Conditional Access, role and grant scenarios.

   | Test group | Graph application permissions |
   |---|---|
   | Every test (preflight, cleanup guard) | `Organization.Read.All`, `Application.ReadWrite.All`, `Group.ReadWrite.All`, `User.ReadWrite.All`, `AdministrativeUnit.ReadWrite.All`, `Policy.Read.All`, `RoleManagement.Read.Directory` (the sweep lists and deletes what a run created) |
   | `ApplicationScenarioTests`, adopt (application) | `Application.ReadWrite.All` |
   | `DirectoryScenarioTests` (groups, users, guest) | `User.ReadWrite.All`, `Group.ReadWrite.All`, `GroupMember.ReadWrite.All`, `User.Read.All` |
   | `ConditionalAccessScenarioTests` | `Policy.ReadWrite.ConditionalAccess`, `Policy.Read.All`, `Application.Read.All`, `Group.ReadWrite.All`; strengths additionally `Policy.ReadWrite.AuthenticationMethod` |
   | `RoleScenarioTests`, adopt (administrative unit) | `RoleManagement.ReadWrite.Directory`, `AdministrativeUnit.ReadWrite.All`, `Group.ReadWrite.All`, `AppRoleAssignment.ReadWrite.All`, `DelegatedPermissionGrant.ReadWrite.All`, `Application.Read.All`, `Application.ReadWrite.All` |
   | Probes | the union of the above; `B4` (user restore) additionally documents whether `User.DeleteRestore.All` is needed |
   | Purging soft-deleted objects (cleanup) | the type's `*.ReadWrite.All`; users may need `User.DeleteRestore.All` |

5. **Test users and guests.** Users are created and deleted by the tests; they need only the verified domain
   (`ENTRAARM_IT_DOMAIN`), get no licenses, and are never signed in. `It_Users_GuestWithExtSuffixIsReadByReference` reads an existing guest
   (a user whose UPN contains `#EXT#`): invite one external address by hand (B2B invitation) once; without a guest that test skips.

## Configuration

| Variable | Required | Meaning |
|---|---|---|
| `ENTRAARM_IT_TENANT` | yes | Tenant id (GUID) the tests may write to. Unset: everything skips. Must equal the tenant of the `az` session. |
| `ENTRAARM_IT_PREFIX` | no | Name prefix of every object the run creates, 10-40 chars of `a-z0-9-` starting with a letter. Default `entraarm-it-<6 hex>`, new for every run. The cleanup guard only ever touches objects whose name starts with it. |
| `ENTRAARM_IT_DOMAIN` | for user tests | A verified domain for user principal names, for example `contoso.onmicrosoft.com`. Tests that create users skip without it. |
| `ENTRAARM_IT_AZ` | no | `az` command override (path to `az`, `az.cmd` or a script), passed as `--az-command` and used by the fixture's own client. |
| `ENTRAARM_IT_ALLOW_CA` | `=1` | Required for every Conditional Access **policy**, authentication **strength** and **context** test, and for the orders variant. Named locations do not need it. |
| `ENTRAARM_IT_ALLOW_ROLES` | `=1` | Required for the tests that write directory roles, administrative units, role assignments, app role assignments and consent grants (all of `RoleScenarioTests`, the administrative-unit adopt test, the orders variant). |
| `ENTRAARM_IT_SWEEP` | `=1` | Enables `It_Cleanup_ManualSweepOfTheConfiguredPrefix` (see Manual cleanup). |

## Running

```powershell
$env:ENTRAARM_IT_TENANT = '<tenant-id>'
$env:ENTRAARM_IT_DOMAIN = 'contoso.onmicrosoft.com'
$env:ENTRAARM_IT_ALLOW_CA = '1'        # optional, see the table
$env:ENTRAARM_IT_ALLOW_ROLES = '1'     # optional, see the table

pwsh -File build.ps1 -Target EntraArm.Integration.Tests                                   # compile only
pwsh -File test.ps1 -Target EntraArm.Integration.Tests                                    # everything that is enabled
pwsh -File test.ps1 -Target EntraArm.Integration.Tests -Filter "Category=Verify"          # the probes only
pwsh -File test.ps1 -Target EntraArm.Integration.Tests -Filter "FullyQualifiedName~ApplicationScenarioTests"
pwsh -File test.ps1 -Target EntraArm.Integration.Tests -Filter "FullyQualifiedName~It_Probe_F1" -Detailed   # diagnose one probe
```

With `ENTRAARM_IT_TENANT` set, `pwsh -File test.ps1` (no `-Target`) also includes this project. All classes share one xUnit collection
and run sequentially: they share the `az` session and the cleanup guard, and the in-process CLI has static global options.

| Test | Needs besides the tenant |
|---|---|
| `It_Applications_RoundTripWithServicePrincipalFicAndSecret` | - |
| `It_Groups_MemberOfMembersOwnersEdgesOnBothSides` | `DOMAIN` |
| `It_Users_GeneratedPasswordUsageLocationAndSecureOutput` | `DOMAIN` |
| `It_Users_GuestWithExtSuffixIsReadByReference` | a guest user in the tenant |
| `It_NamedLocations_IpAndCountryRoundTrip` | - |
| `It_ConditionalAccess_ReportOnlyPolicyWithLocationAndGroup` | `ALLOW_CA` |
| `It_ConditionalAccess_StrengthAndContextLifecycleWithCompleteModeDeleteOrder` | `ALLOW_CA` |
| `It_Roles_CustomRoleAdministrativeUnitAssignmentAndDirectoryRolesEdge` | `ALLOW_ROLES` |
| `It_Roles_AppRoleAssignmentsAndOAuth2GrantsAgainstMicrosoftGraph` | `ALLOW_ROLES` |
| `It_Roles_OrdersSampleVariantCompleteModeDeleteRoundTrip` | `DOMAIN`, `ALLOW_CA`, `ALLOW_ROLES` |
| `It_Adopt_UniqueNameBackfillOnHandCreatedApplication` | - |
| `It_Adopt_ObjectIdPinsDisplayNameKeyedAdministrativeUnit` | `ALLOW_ROLES` |
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
  consent for `User.Read`) are behind `ENTRAARM_IT_ALLOW_ROLES=1`. Read [docs/permissions.md](../../docs/permissions.md) before giving a
  service principal `RoleManagement.ReadWrite.Directory` or `AppRoleAssignment.ReadWrite.All`.
* Tenant limits to keep in mind: 500 role-assignable groups, soft-deleted objects count against some quotas for 30 days.
* Run it in a tenant that no one else is changing at the same time: what-if assertions expect NoChange right after a create.

## Cleanup guarantees

Every run has one prefix (`Fixture.Prefix`). At teardown (`IntegrationFixture.DisposeAsync`), also when tests failed:

1. **Deployments.** Every state file in the run's private state directory (`%TEMP%\entraarm-it\<prefix>\state`) is loaded and its objects
   are deleted in reverse dependency order with the production `DeploymentDeleter.DeleteAllAsync` (continue on error).
2. **Prefix sweep** (`TenantCleanup`): objects created directly through Graph and tracked with `Fixture.Track(...)`, then everything whose
   `displayName`, `userPrincipalName` or `uniqueName` starts with the prefix, in dependency order: Conditional Access policies (not
   enforced ones), authentication strengths, contexts (disabled first), named locations, role assignments of owned principals and roles,
   custom role definitions, administrative units, groups, users, service principals (also by the `appId` of owned applications), applications.
   Every delete re-checks the prefix right before it is sent and refuses otherwise. A prefix must be at least 10 characters.
3. **Purge**: the soft-deleted copies (`/directory/deletedItems/microsoft.graph.{application,servicePrincipal,group,user,administrativeUnit}`)
   with the prefix are removed with `DELETE /directory/deletedItems/{id}`, so no name stays reserved for 30 days.
4. **Report**: `%TEMP%\entraarm-it\<prefix>\cleanup.log` and a diagnostic message list every action. Anything that could not be removed is
   a `LEFTOVER` line and makes the fixture teardown fail; the state directory is kept then.

### Manual cleanup

After a crash (power loss, killed process) the teardown may not have run. Use the prefix printed in the test output or found under
`%TEMP%\entraarm-it\`:

```powershell
$env:ENTRAARM_IT_TENANT = '<tenant-id>'
$env:ENTRAARM_IT_PREFIX = 'entraarm-it-1a2b3c'      # the leaked run's prefix
$env:ENTRAARM_IT_SWEEP  = '1'
pwsh -File test.ps1 -Target EntraArm.Integration.Tests -Filter "FullyQualifiedName~It_Cleanup_ManualSweep"
```

or with `az` alone (one object type shown; repeat for groups, users, servicePrincipals, `directory/administrativeUnits`):

```powershell
$p = 'entraarm-it-1a2b3c'
az rest --method GET --url 'https://graph.microsoft.com/v1.0/applications' --url-parameters "`$filter=startswith(displayName,'$p')" --query 'value[].{id:id,name:displayName}' -o table
az rest --method DELETE --url 'https://graph.microsoft.com/v1.0/applications/<id>'
az rest --method DELETE --url 'https://graph.microsoft.com/v1.0/directory/deletedItems/<id>'     # purge the soft-deleted copy
```

or with the tool itself, from the kept state directory: `entraarm deployment delete -n <deployment> --state-dir "%TEMP%\entraarm-it\<prefix>\state" --delete-resources -y`.
Conditional Access policies, strengths and named locations are under *Entra ID > Security > Conditional Access* in the portal.

## How to read probe failures

A probe asserts an assumption the code makes and names the file to change; the observations go to the test output (diagnostic messages,
`-Detailed` shows them). Letters follow the verification TODOs:

| Probe | Asserts | A red test means |
|---|---|---|
| `A` launcher | `%` and `&` in a `--url-parameters` value, spaces, and the Graph token without `--resource` work through the real `az` under `pwsh`; a 3-page `@odata.nextLink` walk returns every item | Fix `src/EntraArm.Az/Scripts/Invoke-AzRest.ps1`, `AzRequest.cs`, `GraphRequest.ForNextLink` |
| `B1` alternate key + action | `POST /applications/{id}/addPassword` returns `keyId` + `secretText`; the `applications(uniqueName=...)/addPassword` form is **recorded** (works or not), the provider does not depend on it | `PasswordCredentialsProvider.cs`; when the alternate-key form works, the object-id lookup is optional |
| `B2` role assignment filter | `?$filter=principalId eq '...'` works (what the provider uses); `... and roleDefinitionId eq ...` is **recorded** | `RoleAssignmentsLookup.cs` (only if the single filter fails) |
| `B3` scope ids | every `WellKnownIds.GraphScopes` / `GraphAppRoles` id equals the live Microsoft Graph service principal's; lists the `Verified: false` entries now confirmed | `src/EntraArm.Core/WellKnownIds.cs` |
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

When all probes of a letter are green the matching scribe TODO (WI-01 verification items) can be closed; paste the diagnostic output of
the **recorded** items into the TODO or into [docs/providers.md](../../docs/providers.md).
