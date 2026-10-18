# EArm.Integration.Tests

Real-tenant tests for EARM. They drive the production stack (Azure.Identity -> `HttpClient` -> Microsoft Graph) against a
**dev tenant** and are **skipped cleanly** unless `EARM_IT_TENANT` is set. `test.ps1` leaves this project out of a plain run
(`GATED      EArm.Integration.Tests (not run: EARM_IT_TENANT is not set ...)`: not a pass); naming it with `-Target` always runs
it, and every test then skips with the reason when the variable is unset (reported as `SKIPPED ... gated by EARM_IT_TENANT`, again not a pass).

Four kinds of tests live here:

| Kind | Classes | Purpose |
|---|---|---|
| Scenarios | `ApplicationScenarioTests`, `DirectoryScenarioTests`, `ConditionalAccessScenarioTests`, `RoleScenarioTests`, `PimScenarioTests`, `AuthMethodScenarioTests`, `CleanupScenarioTests` | Create, what-if (NoChange), drift, Complete mode (report only, never deletes), `deployment delete -f` (reverse dependency order), secure outputs and edges, run through the real CLI in-process (`Program.BuildRootCommand`). EARM keeps no state, so every scenario finds its objects by key, as a real run does. |
| Verification probes (`[Trait("Category","Verify")]`) | `GraphBehaviourProbes`, `GroupMembershipProbes`, `PimProbes`, `AuthMethodProbes` | One test per open verification TODO. Each exercises one real Graph behaviour the code assumes and asserts the assumption. |
| Consent probes (`[Trait("Category","Consent")]`) | `ConsentProbes` | Interactive, separately gated (`EARM_IT_CONSENT_PROBE=1`); check the delegated-consent flow on real MSAL output. See [Consent probes](#consent-probes). |
| Spike probes (`[Trait("Category","Spike")]`) | `DelegatedAuthSpikeProbes` | Interactive, separately gated (`EARM_IT_AUTH_SPIKE=1`); record how Microsoft Entra answers delegated-auth questions. See [Delegated-auth spike (DES-0002)](#delegated-auth-spike-des-0002). |

## Prerequisites

1. **The .NET 10 SDK** (and PowerShell 7.4+ for `build.ps1` / `test.ps1`). No Azure CLI is needed or used.

2. **A dev tenant you can throw objects away in.** Never a production tenant. It needs a **verified domain** (the default
   `*.onmicrosoft.com` domain is fine) and Microsoft Entra ID P1 (or a trial) for the Conditional Access, authentication strength,
   custom role and role-assignable group scenarios. **The PIM scenarios and probes (`EARM_IT_ALLOW_PIM=1`) need Microsoft Entra ID P2 or
   Microsoft Entra ID Governance** (a P2 trial works); without it every PIM request is refused and those tests fail, so leave the gate off. A
   signed-in user's roles must be **active**, not just eligible: Privileged Role Administrator for directory roles and role-assignable groups, and
   Groups Administrator, Identity Governance Administrator, User Administrator or Directory Writers for PIM for Groups on groups that are not
   role-assignable (Privileged Role Administrator alone gets 403 `PimGroupAccessDenied` there). No Azure subscription is needed.

3. **A sign-in for that tenant: your own user (the main path).** The fixture builds the same stack as the CLI: `EARM_AUTH` / `EARM_TENANT_ID` / `EARM_CLIENT_ID` /
   `EARM_CLOUD` and the `AZURE_*` variables apply (see [docs/cli.md](../../docs/cli.md#signing-in)). Sign in once:

   ```powershell
   earm auth login --tenant <tenant-id>
   ```

   No app registration is needed: the sign-in app is Microsoft Graph Command Line Tools, and the tests act with **your** delegated permissions. The fixture then
   requests, explicitly, the delegated scopes of everything the run will do: the union of the what-if, create and delete scope sets
   (`earm permissions --verb ...`) of every scenario template whose gates are on (`EARM_IT_DOMAIN`, `EARM_IT_ALLOW_CA`, `EARM_IT_ALLOW_ROLES`), plus the scopes of
   the cleanup guard (table below). One consent covers the whole run:

   * **Consent once, ahead of time** (recommended). A run that lacks consent skips every test with a message that contains the exact command for your gates
     (`pwsh -File test.ps1 -Target EArm.Integration.Tests -Detailed` shows it). With every gate on it is:

     ```powershell
     earm auth login --tenant <tenant-id> --scopes AdministrativeUnit.Read.All AdministrativeUnit.ReadWrite.All Application.Read.All Application.ReadWrite.All AppRoleAssignment.ReadWrite.All AuthenticationContext.Read.All AuthenticationContext.ReadWrite.All DelegatedPermissionGrant.Read.All DelegatedPermissionGrant.ReadWrite.All Directory.Read.All Group.Read.All Group.ReadWrite.All GroupMember.Read.All GroupMember.ReadWrite.All Organization.Read.All Policy.Read.All Policy.Read.ConditionalAccess Policy.ReadWrite.ConditionalAccess RoleManagement.Read.Directory RoleManagement.ReadWrite.Directory User.DeleteRestore.All User.Read User.Read.All User.ReadWrite.All
     ```

     (with no gate on, the list drops `AdministrativeUnit.Read.All`, `AppRoleAssignment.ReadWrite.All`, `AuthenticationContext.*`, `DelegatedPermissionGrant.*` and
     `RoleManagement.ReadWrite.Directory`). `EARM_IT_ALLOW_PIM=1` adds `RoleEligibilitySchedule.ReadWrite.Directory`, `RoleAssignmentSchedule.ReadWrite.Directory`,
     `PrivilegedEligibilitySchedule.ReadWrite.AzureADGroup` and `PrivilegedAssignmentSchedule.ReadWrite.AzureADGroup` (plus their read variants for what-if);
     `EARM_IT_ALLOW_AUTH_METHODS=1` adds `Policy.ReadWrite.AuthenticationMethod`; `EARM_IT_ALLOW_PIM_POLICIES=1` (with `ALLOW_PIM`) adds
     `RoleManagementPolicy.ReadWrite.Directory`, `RoleManagementPolicy.ReadWrite.AzureADGroup` (plus their read variants) and `AuthenticationContext.Read.All`;
     `EARM_IT_ALLOW_ACCESS_PACKAGES=1` adds `EntitlementManagement.ReadWrite.All` (plus its read variant). The run's set is computed from the templates (`IntegrationScopes`), so trust the
     printed command over this copy.
   * **Or be prompted at the start of the run:** `$env:EARM_IT_INTERACTIVE = '1'` lets the fixture open ONE browser consent prompt for the whole set before the first
     test (only when a person is at the machine; a test host cannot detect that itself). Without it a run never waits for a browser.

   Nearly all of these scopes need **admin consent** ([docs/permissions.md](../../docs/permissions.md#admin-consent)): sign in as an administrator of the dev tenant
   (Global Administrator is simplest) and consent for yourself. Do **not** tick "Consent on behalf of your organization", and remember that the grant is shared with
   Microsoft Graph PowerShell and the Graph CLI ([SECURITY.md](../../SECURITY.md#delegated-sign-in-and-shared-consent), also for how to revoke it). The CLI runs
   inside the tests never prompt: each requests a subset of the consented set, which the refresh token serves silently, and a missing scope fails that test with
   `ConsentRequired` (exit 3).

   **CI option: a service principal.** Set it in the environment (it requests `.default` and uses application permissions, unchanged):

   ```powershell
   $env:AZURE_TENANT_ID = '<tenant-id>'; $env:AZURE_CLIENT_ID = '<appId>'; $env:AZURE_CLIENT_SECRET = '<secret>'
   $env:EARM_AUTH = 'default'     # only needed when an 'earm auth login' record also exists on the machine (a stored sign-in wins otherwise)
   ```

   The preflight (token claims, the `scp` check, `GET /organization`) runs once per test run; when nothing signs in, consent is missing, or the token belongs to another
   tenant than `EARM_IT_TENANT`, every test skips with a message saying so.

4. **Permissions.** What the signed-in identity needs is the union of the groups you run (`earm permissions` computes the same lists from a template; see
   [docs/permissions.md](../../docs/permissions.md) for the manifest and the privilege-escalation warning). A **user** additionally needs Entra roles that allow the
   writes (Global Administrator covers every scenario); the delegated scopes come from the consent above. A **service principal** needs the application permissions
   below, with admin consent granted. The delegated scope names match, except that the delegated runs also request the read variants for what-if.

   | Test group | Graph permissions |
   |---|---|
   | Every test (preflight, cleanup guard) | `Organization.Read.All` (the preflight's `GET /organization`: needed as an application permission by a service principal; a user reads it with `User.Read`, but the fixture still requests the delegated scope, so it must be consented), `User.Read` (delegated only), `Application.ReadWrite.All`, `Group.ReadWrite.All`, `User.ReadWrite.All`, `User.DeleteRestore.All` (purging deleted users), `AdministrativeUnit.ReadWrite.All`, `Policy.Read.All`, `RoleManagement.Read.Directory` (the sweep lists and deletes what a run created; `IntegrationScopes.CleanupScopes`) |
   | `ApplicationScenarioTests` | `Application.ReadWrite.All` |
   | `DirectoryScenarioTests` (groups, users, guest) | `User.ReadWrite.All`, `Group.ReadWrite.All`, `GroupMember.ReadWrite.All`, `User.Read.All` |
   | `ConditionalAccessScenarioTests` | `Policy.ReadWrite.ConditionalAccess`, `Policy.Read.All`, `Application.Read.All`, `Group.ReadWrite.All`; contexts `AuthenticationContext.ReadWrite.All` |
   | `RoleScenarioTests` | `RoleManagement.ReadWrite.Directory`, `AdministrativeUnit.ReadWrite.All`, `Group.ReadWrite.All`, `AppRoleAssignment.ReadWrite.All`, `DelegatedPermissionGrant.ReadWrite.All`, `Application.Read.All`, `Application.ReadWrite.All` |
   | `PimScenarioTests`, `PimProbes` | `RoleEligibilitySchedule.ReadWrite.Directory`, `RoleAssignmentSchedule.ReadWrite.Directory`, `PrivilegedEligibilitySchedule.ReadWrite.AzureADGroup`, `PrivilegedAssignmentSchedule.ReadWrite.AzureADGroup`, `RoleManagement.ReadWrite.Directory` (P5 writes a plain role assignment), `User.ReadWrite.All`, `Group.ReadWrite.All`, `Directory.Read.All`; a user also needs **Privileged Role Administrator** (or Global Administrator) |
   | `AuthMethodScenarioTests`, `AuthMethodProbes` | `Policy.ReadWrite.AuthenticationMethod`; a user also needs **Authentication Policy Administrator** (or Global Administrator) |
   | `PimPolicyScenarioTests`, `PimPolicyProbes` | `RoleManagementPolicy.ReadWrite.Directory`, `RoleManagementPolicy.ReadWrite.AzureADGroup`, `PrivilegedEligibilitySchedule.ReadWrite.AzureADGroup`, `AuthenticationContext.Read.All` (PP6 reads the tenant's contexts), `User.ReadWrite.All`, `Group.ReadWrite.All`, `Directory.Read.All` (`IntegrationScopes.PimPolicyScopes` for the restore guard); a user also needs **Privileged Role Administrator** (or Global Administrator) |
   | PIM cleanup (with `ALLOW_PIM`) | the four `*Schedule.ReadWrite.*` scopes above (`IntegrationScopes.PimCleanupScopes`) |
   | Probes | the union of the above; `B4` (user restore) additionally documents whether `User.DeleteRestore.All` is needed |
   | Purging soft-deleted objects (cleanup) | the type's `*.ReadWrite.All`; users `User.DeleteRestore.All` |

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
| `EARM_IT_ALLOW_PIM` | `=1` | Required for every PIM test (`PimScenarioTests`, `PimProbes`): directory role eligibilities, PIM for Groups eligibilities and time-bound active role and group assignments of disabled, prefixed test users. Needs Entra ID P2 / ID Governance and `EARM_IT_DOMAIN`. Also turns on the PIM part of the cleanup guard. |
| `EARM_IT_ALLOW_AUTH_METHODS` | `=1` | Required for `AuthMethodScenarioTests` and `AuthMethodProbes`, which change the **tenant-wide** authentication methods policy. Only safe toggles, always under the snapshot / restore guard (see [Authentication methods: the restore guarantee](#authentication-methods-the-restore-guarantee)). Leave it off in a tenant people sign in to. |
| `EARM_IT_ALLOW_PIM_POLICIES` | `=1` | Required (together with `EARM_IT_ALLOW_PIM`) for `PimPolicyScenarioTests` and `PimPolicyProbes`, which change **tenant-wide** PIM role management policies that can never be deleted: only the Message Center Reader policy and the PIM for Groups policies of prefixed test groups, always under the snapshot / restore guard (see [PIM policies: the restore guarantee](#pim-policies-the-restore-guarantee)). A separate gate because `ALLOW_PIM` alone only ever writes schedules of prefixed principals. Leave it off in a tenant people activate roles in. |
| `EARM_IT_ALLOW_ACCESS_PACKAGES` | `=1` | Required for `AccessPackageScenarioTests`, which create prefixed entitlement management catalogs, catalog resources, access packages, role scopes and assignment policies (never assignments). The tenant needs Microsoft Entra ID P2 or ID Governance and the signed-in identity an **active** Identity Governance Administrator plus Groups Administrator (the test groups are not role-assignable). The eligible-role test also needs `EARM_IT_ALLOW_PIM` and `EARM_IT_DOMAIN`: a same-template PIM for Groups eligibility of a disabled test user onboards its group (permanently; the group is deleted at the end). The prefix sweep removes prefixed catalogs with their contents before the groups. `AccessPackageResourceKindScenarioTests` adds a prefixed app registration's service principal to a catalog and grants an app role (by name, then by id). |
| `EARM_IT_SP_SITE_URL`, `EARM_IT_SP_SITE_ROLE` | a site URL; one of its group names (`Team Members`) | Required (with `EARM_IT_ALLOW_ACCESS_PACKAGES`) for the SharePoint Online site test of `AccessPackageResourceKindScenarioTests`: the existing site is added to a prefixed catalog, its group granted by a package, then removed; the site is never changed. Run it signed in as a user who is a SharePoint Administrator or an administrator of the site (Graph lists sites in a catalog only to delegated callers). |
| `EARM_IT_SWEEP` | `=1` | Enables `It_Cleanup_ManualSweepOfTheConfiguredPrefix` (see Manual cleanup). |
| `EARM_IT_INTERACTIVE` | `=1` | A person is at the machine: a user sign-in that lacks consent for the run's scopes may open ONE browser consent prompt at the start. Off: the run skips with the `earm auth login --scopes ...` command. Never set it in CI. |
| `EARM_AUTH` | no | `default` forces the service principal / managed identity of the environment when an `earm auth login` record also exists. |

## Running

```powershell
$env:EARM_IT_TENANT = '<tenant-id>'
$env:EARM_IT_DOMAIN = 'contoso.onmicrosoft.com'
$env:EARM_IT_ALLOW_CA = '1'        # optional, see the table
$env:EARM_IT_ALLOW_ROLES = '1'     # optional, see the table
$env:EARM_IT_ALLOW_PIM = '1'       # optional: needs Entra ID P2 / ID Governance
$env:EARM_IT_ALLOW_AUTH_METHODS = '1'   # optional: tenant-wide policy, snapshot + restore
$env:EARM_IT_ALLOW_PIM_POLICIES = '1'   # optional, with ALLOW_PIM: tenant-wide PIM policies, snapshot + restore
$env:EARM_IT_ALLOW_ACCESS_PACKAGES = '1'  # optional: prefixed catalogs, access packages and policies (ID Governance)

pwsh -File build.ps1 -Target EArm.Integration.Tests                                   # compile only
pwsh -File test.ps1 -Target EArm.Integration.Tests                                    # everything that is enabled
pwsh -File test.ps1 -Target EArm.Integration.Tests -Filter "Category=Verify"          # the probes only
pwsh -File test.ps1 -Target EArm.Integration.Tests -Detailed                         # shows skip reasons, such as the consent command
pwsh -File test.ps1 -Target EArm.Integration.Tests -Filter "FullyQualifiedName~ApplicationScenarioTests"
pwsh -File test.ps1 -Target EArm.Integration.Tests -Filter "FullyQualifiedName~It_Probe_F1" -Detailed   # diagnose one probe
pwsh -File test.ps1 -Target EArm.Integration.Tests -Filter "FullyQualifiedName~PimScenarioTests|FullyQualifiedName~PimProbes" -Detailed   # PIM only
pwsh -File test.ps1 -Target EArm.Integration.Tests -Filter "FullyQualifiedName~AuthMethod" -Detailed     # authentication methods only
pwsh -File test.ps1 -Target EArm.Integration.Tests -Filter "FullyQualifiedName~PimPolicy" -Detailed      # PIM policies only
```

With `EARM_IT_TENANT` set, `pwsh -File test.ps1` (no `-Target`) also includes this project. All classes share one xUnit collection
and run sequentially: they share the sign-in and the cleanup guard, and the in-process CLI has static global options.

| Test | Needs besides the tenant |
|---|---|
| `It_Applications_RoundTripWithServicePrincipalFicAndSecret` | - |
| `It_Groups_MemberOfMembersOwnersEdgesOnBothSides` | `DOMAIN` |
| `It_Groups_AdoptsExistingGroupByDisplayName` | - |
| `It_Users_GeneratedPasswordUsageLocationAndSecureOutput` | `DOMAIN` |
| `It_Users_GuestWithExtSuffixIsReadByReference` | a guest user in the tenant |
| `It_NamedLocations_IpAndCountryRoundTrip` | - |
| `It_ConditionalAccess_ReportOnlyPolicyWithLocationAndGroup` | `ALLOW_CA` |
| `It_ConditionalAccess_StrengthAndContextLifecycleWithDeleteOrder` | `ALLOW_CA` |
| `It_Roles_CustomRoleAdministrativeUnitAssignmentAndDirectoryRolesEdge` | `ALLOW_ROLES` |
| `It_Roles_AppRoleAssignmentsAndOAuth2GrantsAgainstMicrosoftGraph` | `ALLOW_ROLES` |
| `It_Roles_OrdersSampleVariantCompleteModeAndDeleteRoundTrip` | `DOMAIN`, `ALLOW_CA`, `ALLOW_ROLES` |
| `It_Pim_RoleEligibilityLifecycleWithAdminUpdateTupleChangeAndDelete` | `DOMAIN`, `ALLOW_PIM` |
| `It_Pim_GroupEligibilityMemberAndOwnerWithDynamicGroupRefusal` | `DOMAIN`, `ALLOW_PIM` |
| `It_Pim_TimeBoundActiveRoleAndGroupAssignments` | `DOMAIN`, `ALLOW_PIM` |
| `It_Pim_ActiveAssignmentConflictsAreRefusedBeforeAnyWrite` | `DOMAIN`, `ALLOW_PIM` (what-if only, nothing is written) |
| `It_Cleanup_SweepRemovesPimSchedulesOfPrefixedPrincipals` | `DOMAIN`, `ALLOW_PIM` |
| `It_AuthMethods_SafeToggleRoundTripUnderTheRestoreGuard` | `ALLOW_AUTH_METHODS` |
| `It_PimPolicy_DirectoryRolePolicyModifyRerunNoChangeKeptOnDeleteAndRestored` | `ALLOW_PIM`, `ALLOW_PIM_POLICIES` |
| `It_PimPolicy_GroupPolicyWithSameRunEligibilityConvergesInTwoRunsAndIsKeptOnDelete` | `DOMAIN`, `ALLOW_PIM`, `ALLOW_PIM_POLICIES` |
| `It_Cleanup_SweepRemovesTrackedGroupWithThePrefix` | - |
| `It_Cleanup_ManualSweepOfTheConfiguredPrefix` | `SWEEP=1` |
| `It_Probe_A` ... `It_Probe_H` (15 probes) | A, B1, B2, B3, D, F1-F4, G: -; B4: `DOMAIN`; C, E1, E2: `ALLOW_CA`; H: `DOMAIN`, `ALLOW_ROLES` |
| `It_Probe_GA1` (group adoption) | - |
| `It_Probe_P1` ... `It_Probe_P5` (PIM) | `DOMAIN`, `ALLOW_PIM` |
| `It_Probe_AM1` ... `It_Probe_AM4` (authentication methods) | `ALLOW_AUTH_METHODS`; AM3 also skips unless Temporary Access Pass is disabled in the tenant |
| `It_Probe_PP1` ... `It_Probe_PP7` (PIM policies) | `ALLOW_PIM`, `ALLOW_PIM_POLICIES`; PP1, PP4 and PP7 also `DOMAIN`; PP6 only records when the tenant has an available authentication context; PP7 takes about 20-25 minutes (it polls 15 minutes before onboarding: shorten `PreOnboardingPollMinutes` in `PimPolicyProbes.cs` to iterate) |

## Cost and safety

* No Azure subscription resources are created and no licenses are assigned: Entra objects only, no cost. Users have no license and
  never sign in; their passwords are generated or random and are never printed (secure outputs go to an owner-only file in the run's
  scratch directory, which is deleted at the end).
* Conditional Access policies are always `enabledForReportingButNotEnforced`. **No test passes `--allow-ca-enforce`.** The enforce-rule
  test proves that `state: enabled` is held back (the existing report-only policy keeps its live state, with a warning). The guard never deletes an *enforced* policy it finds with the prefix; it reports it.
* Authentication contexts use ids `c99` downwards (never below `c50`); an id that already exists is skipped, so a foreign context is
  never overwritten.
* Privileged writes (role assignments, app role assignments such as `User.Read.All` on a throw-away service principal, tenant-wide
  consent for `User.Read`) are behind `EARM_IT_ALLOW_ROLES=1`. Read [docs/permissions.md](../../docs/permissions.md) before giving a
  service principal `RoleManagement.ReadWrite.Directory` or `AppRoleAssignment.ReadWrite.All`.
* PIM (`EARM_IT_ALLOW_PIM=1`): the principals are **disabled** prefixed test users that never sign in, the roles are low-privilege (Message Center
  Reader, Directory Readers, Reports Reader), schedules are time-bound (2 to 50 days) and removed again by the scenarios, `deployment delete` and the
  cleanup guard. The first PIM for Groups request onboards the prefixed test group in PIM permanently; the group is deleted at the end. P1 tries
  requests a default policy may refuse (a permanent group eligibility, a 10-year and a permanent active assignment of Message Center Reader to the
  disabled user) and removes any that is accepted at once. The conflict scenario only runs what-if: nothing is written.
* Authentication methods (`EARM_IT_ALLOW_AUTH_METHODS=1`) are **tenant-wide**: see [the restore guarantee](#authentication-methods-the-restore-guarantee).
* PIM role management policies (`EARM_IT_ALLOW_PIM_POLICIES=1`) are **tenant-wide and cannot be deleted**: only the Message Center Reader policy
  (activation duration, enabled rules, approval by a prefixed approvers group, and PP2 / PP3 / PP6 rewrites) and the policies of prefixed test groups
  (PP7 writes one rule of its own group's policy back to its current value) are changed, always under [the restore guarantee](#pim-policies-the-restore-guarantee). The Global Administrator and Privileged Role Administrator
  policies are never touched (the guard refuses them, and every other directory role, before any request).
* Tenant limits to keep in mind: 500 role-assignable groups, soft-deleted objects count against some quotas for 30 days.
* Run it in a tenant that no one else is changing at the same time: what-if assertions expect NoChange right after a create.

## Cleanup guarantees

Every run has one prefix (`Fixture.Prefix`), and every object a test creates carries it (the templates take a `prefix` parameter). The scenarios
remove their own deployments with `earm deployment delete -f <template> -p prefix=<prefix> -y` (the production delete: the objects the template
declares, found by key, in reverse dependency order). There is no state directory to clean up. At teardown (`IntegrationFixture.DisposeAsync`), also
when tests failed, the fixture removes whatever is left:

0. **Authentication method and PIM policy restore** (`AuthMethodRestoreGuard`, `PimPolicyRestoreGuard`, only when a snapshot is still pending):
   every configuration or policy a test snapshotted but could not restore itself is PATCHed back to its snapshot and verified (see below), before the
   sweep deletes the prefixed approver groups a changed rule may name; one that still differs is a `LEFTOVER`.
1. **Prefix sweep** (`TenantCleanup`): objects created directly through Graph and tracked with `Fixture.Track(...)`, then everything whose
   `displayName`, `userPrincipalName` or `uniqueName` starts with the prefix, in dependency order: Conditional Access policies (not
   enforced ones), custom authentication strengths, authentication contexts (only ids `c50`..`c99`, the range the fixture allocates;
   marked unavailable first), named locations (trusted ones are unmarked first; Graph applies the unmark with a delay, so a delete answered
   with 1177 is retried every 5 s for up to 90 s), with `EARM_IT_ALLOW_PIM=1` the PIM schedules of owned principals (`PimCleanup`: directory role
   eligibilities, time-bound active role assignments, PIM for Groups eligibilities and time-bound active assignments, each removed with an
   `adminRemove` request and re-listed for up to 60 s; a schedule on a prefixed group whose principal is NOT prefixed is only logged, never removed;
   permanent assignments go with the role assignment and membership cleanup), role assignments of owned principals and roles, custom role definitions, administrative
   units, groups, users, service principals (also by the `appId` of owned applications), applications. Every delete re-checks the prefix right
   before it is sent and refuses otherwise. A prefix must be at least 10 characters; the Conditional Access scenarios also need it to be at
   most 20 characters, because Graph limits an authentication strength's name to 30 characters.
2. **Purge**: the soft-deleted copies (`/directory/deletedItems/microsoft.graph.{application,servicePrincipal,group,user,administrativeUnit}`)
   with the prefix are removed with `DELETE /directory/deletedItems/{id}`, so no name stays reserved for 30 days.
3. **Verify**: every collection above is listed again. An object Graph accepted a DELETE for but that is still listed is read by id until it
   answers 404 (up to 90 s; listings lag behind deletes) and logged as `confirmed ... is gone`.
4. **Report**: `%TEMP%\earm-it\<prefix>\cleanup.log` and a diagnostic message list every action. Each sweep (a manual sweep, the teardown)
   appends its own section, headed `==== EARM integration cleanup (<trigger>) ...`; the file is never overwritten. A `deleted ...` line means
   Graph accepted the DELETE. Anything that could not be removed is a `LEFTOVER` line and makes the fixture teardown fail.
   `It_Cleanup_SweepRemovesTrackedGroupWithThePrefix` rides on this sweep instead of running one of its own: it creates and tracks a prefixed group,
   and after the teardown sweep the fixture reads the group again; a group that is still there is a leftover, so the teardown fails.

The sweep is what catches the objects `deployment delete` cannot: an object a test renamed (a new key is a new object, the old one stays), the old
assignment of a changed tuple (retained, never removed by EARM), and anything a failed run or a test left behind. Its PIM lines start with `pim:`
(`pim: adminRemove accepted for ...`, `pim: ... was already gone`, `pim: NOT removing ...`), the restore lines with `auth methods:` and `pim policies:`.

### Authentication methods: the restore guarantee

The authentication methods policy is a tenant singleton, so these tests can never "create and delete their own copy". Instead:

1. **Snapshot first.** Before anything is changed, `Fixture.AuthMethods.SnapshotAsync(method)` reads the live configuration and keeps it (the first
   snapshot of a method wins until it is restored). It is also written to `%TEMP%\earm-it\<prefix>\auth-methods\<Method>.json`.
2. **Safe toggles only.** The scenario changes the Temporary Access Pass lifetimes only while the method is **enabled** (never its state, length or
   targets; Graph accepts a lifetime PATCH of a disabled one but keeps serving the old values, see AM4); the AAGUID list of a FIDO2
   key restriction only when that restriction is **not enforced** and the tenant has **no passkey profiles** (otherwise FIDO2 is left out); and the
   Software OATH state only when it is **disabled** (it is enabled for the test and disabled again). No method the tenant relies on is ever disabled,
   and no `includeTargets` / `excludeTargets` are declared. AM2 only writes live values back; AM3 resets (DELETE) Temporary Access Pass only while it
   is disabled, and skips otherwise; AM4 changes its lifetimes and restores them. With no safe toggle in the tenant the scenario skips.
3. **Restore exactly.** The scenario first deploys the snapshot values back with EARM; then, in a `finally`, the guard PATCHes (with the method's
   `@odata.type`) every top-level property whose live value differs from the snapshot and re-reads until every property equals the snapshot
   (OData annotations ignored, arrays compared as sets; up to 60 s). The fixture teardown repeats this for every snapshot still pending, also after a
   failed or aborted test. A configuration that still differs is a `LEFTOVER` (the teardown fails) and its snapshot file is kept.

After a crash, restore by hand from the snapshot file: `PATCH https://graph.microsoft.com/v1.0/policies/authenticationMethodsPolicy/authenticationMethodConfigurations/<Method>`
with the file's properties (drop `id` and the `@odata.context` annotations, keep `@odata.type`), for example in Graph Explorer.

### PIM policies: the restore guarantee

A role management policy exists for every directory role and onboarded group and cannot be deleted, so these tests change live policies and put them
back:

1. **Low-risk scopes only.** `PimPolicyRestoreGuard` accepts the directory role policy of **Message Center Reader** (template id
   `790c1fb9-7f7d-4f88-86a1-ef1f95c05c1b`, scope `/`) and the PIM for Groups policies of groups whose displayName starts with the run prefix; it
   refuses every other scope (Global Administrator and Privileged Role Administrator by name) before a request is sent.
2. **Snapshot first.** `Fixture.PimPolicies.SnapshotAsync(scope)` reads the policy (`$expand=policy($expand=rules)`) and keeps every rule as Graph
   returned it (the first snapshot of a policy wins until it is restored). It is also written to
   `%TEMP%\earm-it\<prefix>\pim-policies\<scopeType>-<scopeId>-<roleDefinitionId>.json` (`directoryrole-root-790c1fb9-...json` for Message Center
   Reader). A group PIM has not onboarded has no policy yet, so its snapshot is taken right after the onboarding run.
3. **Values that always differ, never MFA.** The scenarios derive the maximum activation duration (`PT3H` or `PT2H`) and the enabled rules
   (`Justification, Ticketing` or `Justification`) from the snapshot so they differ from the live values; multifactor authentication is never enabled by a
   scenario (it can conflict with an authentication context rule). PP6 enables it on purpose to record Graph's answer, and is restored like the rest.
4. **Restore exactly.** In a `finally`, the guard PATCHes (`PATCH /policies/roleManagementPolicies/{id}/rules/{ruleId}`) every snapshotted rule whose
   live value differs, with the snapshot's own body (OData annotations other than `@odata.type`, the read-only approver `description` and null members
   dropped; on a 400, the provider's body for the same values), and re-reads until every rule equals the snapshot in the provider's comparable shape
   (up to 60 s). The policy is looked up by scope every time, so a group policy re-id on onboarding does not matter. The fixture teardown repeats this
   for every snapshot still pending, before the prefix sweep. A policy that still differs is a `LEFTOVER` (the teardown fails) and its snapshot file is
   kept. A group policy that is no longer listed (its prefixed group was deleted) has nothing left to restore.

After a crash, restore by hand from the snapshot file: for each rule in `rules`, `PATCH https://graph.microsoft.com/v1.0/policies/roleManagementPolicies/<policyId>/rules/<rule id>`
with the rule (keep `@odata.type`, drop the `@odata.context` annotations and the approvers' `description`), for example in Graph Explorer, or set
Message Center Reader back under *Identity Governance > Privileged Identity Management > Microsoft Entra roles > Settings*.

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
Conditional Access policies, strengths and named locations are under *Entra ID > Security > Conditional Access* in the portal. Set `EARM_IT_ALLOW_PIM=1` for
the manual sweep too when the leaked run used PIM, so the PIM schedules of its principals are removed with `adminRemove` (deleting and purging the
principal ends them as well); PIM assignments are also visible under *Entra ID > Roles & admins* and *Identity Governance > Privileged Identity Management*.

## How to read probe failures

A probe asserts an assumption the code makes and names the file to change; the observations go to the test output (diagnostic messages,
`-Detailed` shows them). The scenarios' own observations (`Observe`, for example how many what-if attempts a convergence took) go to the test's
output too, and so are kept in the TRX file for passing tests. Letters follow the verification TODOs:

| Probe | Asserts | A red test means |
|---|---|---|
| `A` transport | `%` and `&` and spaces in a `$filter` value survive the percent-encoding, the token is issued for the right tenant and audience, and a 3-page `@odata.nextLink` walk returns every item | Fix `GraphUrl.BuildQuery` or the paging loop in `HttpGraphClient.GetAllPagesAsync` |
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
| `H` group members (`GroupMembershipProbes`) | a service principal and a user added to a group in one `$batch` (as the edge pass sends them) are both read back, for a plain and a role-assignable security group: the user through v1.0 `/members`, the service principal through the type cast `/members/microsoft.graph.servicePrincipal`; whether v1.0 `/members` lists the service principal is **recorded** | `src/EArm.Engine/Edges/GroupMembershipEdgeKind.cs` (the reads of group members) |
| `GA1` uniqueName on an existing group (`GroupAdoptionProbes`) | `PATCH /groups/{id}` sets the `uniqueName` of a group created without one, and it reads back; whether a second PATCH to another value is accepted is **recorded** | `GroupsProvider.AdoptUniqueNameAsync` (adoption by display name) and the group adoption notes in `docs/providers.md` |

| `P1` PIM errors (`PimProbes`) | a second adminAssign of an existing eligibility fails with an error `PimScheduleRequests.IsAlreadyExists` recognises (or is accepted); an adminRemove of a missing eligibility fails with an error `IsAlreadyGone` recognises (or is accepted); any 400 the default policy answers to a permanent group eligibility, a 10-year or a permanent active role assignment is classified by `IsPolicyViolation`. The exact codes are **recorded**; when no attempt is refused the policy mapping is reported as unverified | `src/EArm.Providers/PimScheduleRequests.cs` |
| `P2` AfterDuration and ids | an AfterDuration eligibility reads back as `afterDuration` or `afterDateTime` (the read-back shape is **recorded**); the adminAssign response's `targetScheduleId` is the schedule id; an adminUpdate without `startDateTime` (what EARM sends for a declared past start) is accepted, `RoleAssignmentExists` repeated with the provider's backoff (the variant that worked, omitted or live start, and the number of `RoleAssignmentExists` answers are **recorded**); after the adminUpdate the tuple lists exactly one Direct schedule (id stability **recorded**) and a fractional-second end reads back within `TimeTolerance` | `PimScheduleComparison.cs`, `PimScheduleRequestsProvider.cs` (`StandInFor`, `TimeTolerance`), `RoleSchedulesLookup.cs` |
| `P3` group eligibility | a fresh group eligibility passes `IsDirectAndActive` and `GroupSchedulesLookup.Matches` (`memberType` / `accessId` casing **recorded**); the combined `groupId eq … and principalId eq …` filter returns it; whether adminRemove needs `scheduleInfo` is **recorded** (the provider always sends it; asserted to work) | `PimScheduleRequests.cs`, `GroupSchedulesLookup.cs`, `GroupScheduleRequestsProvider.cs` (`RemoveSchedule`) |
| `P4` PIM-active member | a PIM active group member is listed by `GET /groups/{id}/members` (the delay, when the active schedule is listed and the adminRemove answer, `RoleAssignmentDoesNotExist` included, are **recorded**), which the members-list conflict rests on | `src/EArm.Providers/DeclarationConflicts.cs` |
| `P5` plain assignment | a plain `roleAssignments` entry reads back as one Direct, Assigned, permanent `roleAssignmentSchedule` (`createdUsing` **recorded**), so the active type refuses to adopt it | `RoleAssignmentScheduleRequestsProvider.cs` (`LiveConflict`), `PimScheduleComparison.IsPermanent` |
| `AM1` URL segments (`AuthMethodProbes`, read-only) | `GET …/authenticationMethodConfigurations/{PascalCase id}` answers each method with the id and `@odata.type` of the method table; the camelCase segment, the property names, the nested `@odata.type` paths and the FIDO2 legacy / passkey profile properties are **recorded** | `src/EArm.Core/Validation/AuthenticationMethodConfigurationRules.cs` |
| `AM2` PATCH shape | a PATCH with the derived `@odata.type` works and resending the live `includeTargets` (nested `@odata.type` included) is accepted; the answer to a PATCH **without** `@odata.type` is **recorded** | `AuthenticationMethodConfigurationsProvider.cs` (`UpdateAsync`), `AuthenticationMethodConfigurationsShape.cs` |
| `AM3` reset | `DELETE` of a disabled Temporary Access Pass is accepted and the configuration still exists afterwards; what the reset changed is **recorded** | `AuthenticationMethodConfigurationRules.cs` (`CanReset`), `AuthenticationMethodConfigurationsProvider.DeleteAsync` |
| `AM4` lifetime PATCH sticks | **observation only**: a Temporary Access Pass PATCH of `minimumLifetimeInMinutes` alone, then of the three lifetimes, each re-read for 60 s; whether the values stick, and the method's state, are **recorded** | `AuthMethodScenarioTests.IsTemporaryAccessPassToggleSafe`, docs/providers.md |
| `PP1` group policy onboarding (`PimPolicyProbes`) | the assignment lookup of a new, not yet onboarded group is not refused (whether the policy is listed before onboarding, the scopeId+scopeType filter and whether the policy id changes on onboarding are **recorded**), and the member policy is listed within 180 s of the first eligibility request | `RoleManagementPolicyLookup.FindAsync`, `RoleManagementPoliciesProvider` (`PlanMissing`), docs/pim.md |
| `PP2` rule PATCH shape | the provider's rule body (`@odata.type` first, `ruleType` dropped, nulls left out) for the live Message Center Reader expiration rule is accepted; the answer to the same PATCH **without** `@odata.type` is **recorded** | `RoleManagementPolicyShape.ToGraph` |
| `PP3` approvers | after an approval rule PATCH with a prefixed approvers group, the rule read back compares equal to the declaration the way Plan compares it; whether Graph accepts a subject set with `description` and echoes `isBackup` / `description` is **recorded** | `RoleManagementPolicyShape.cs` (`Comparable`, `ApproverToArm`) |
| `PP4` target casing | every rule target of the Message Center Reader policy and of a prefixed group's member policy normalises to ARM's casing (`caller`, `level`, `operations`); the live spellings are **recorded** | `RoleManagementPolicyRules.NormalizeRules`, `RoleManagementPolicyShape.ToArm` |
| `PP5` `$expand` | `GET /policies/roleManagementPolicyAssignments?$filter=…&$expand=policy($expand=rules)` returns the rules; `$expand=policy` alone and `GET /policies/roleManagementPolicies/{id}?$expand=rules` are **recorded** | `RoleManagementPolicyLookup.FindAsync` |
| `PP6` MFA + authentication context | **recorded only**: whether the Message Center Reader policy accepts `MultiFactorAuthentication` in the enablement rule together with an enabled authentication context rule (an existing, available context of the tenant; never created), the error code when refused, and whether EARM's offline checks accept such a template | `RoleManagementPolicyRuleChecks.cs` (add an offline check when Graph refuses the combination) |
| `PP7` when a group policy appears (TODO-0093) | **recorded only** (about 20-25 minutes): for a new prefixed group, every 30 s for 15 minutes, the status, item count and policy ids of the member assignment lookup, the scopeId+scopeType assignment lookup and the policies collection (`GET /policies/roleManagementPolicies?$filter=scopeId eq '…' and scopeType eq 'Group'`), stopping early when one returns a policy; if the policies collection returns one, whether writing `Expiration_EndUser_Assignment` back to its own value onboards the group within 180 s; how long after the first eligibility request (P7D) the policy is listed; and whether the member policy is still listed 60 s after that only eligibility is removed | how EARM onboards empty groups: `RoleManagementPoliciesProvider` (`PlanMissing`), `RoleManagementPolicyLookup`, docs/pim.md |

When all probes of a letter are green the matching verification item can be closed; paste the diagnostic output of
the **recorded** items into the TODO or into [docs/providers.md](../../docs/providers.md).

## Consent probes

`ConsentProbes` (`[Trait("Category","Consent")]`) checks the delegated-consent flow of DES-0002 against your tenant, on the real MSAL output that the unit tests can
only simulate. Three probes, run in order and building on each other:

| Probe | Asserts |
|---|---|
| `It_Consent_1_...` | A silent request for a scope the sign-in has not consented is classified by `AzureIdentityTokenProvider` as `ConsentRequiredException` (not "sign-in expired"), `ConsentRequiredException.IsConsentFailure` accepts the real exception chain, and the message carries the `earm auth login --scopes` command. |
| `It_Consent_2_...` | The provider's consent prompt (browser, once, for exactly that set) followed by the silent retry on a **new** credential succeeds. |
| `It_Consent_3_...` | The token's `scp` contains every explicitly requested scope. |

They are interactive (one browser sign-in, one consent prompt) and run only with `EARM_IT_CONSENT_PROBE=1` and `EARM_IT_TENANT`; otherwise each skips with the reason.
They do **not** touch EARM's own sign-in: the sign-in record is kept in memory, the tokens in a separate persisted cache **`earm-consent-probe`**, deleted when the class
finishes. What cannot be isolated is the consent itself: probe 2 grants the probe scope to Microsoft Graph Command Line Tools for your account, and that grant stays.

| Variable | Required | Meaning |
|---|---|---|
| `EARM_IT_CONSENT_PROBE` | `=1` | Enables the probes. |
| `EARM_IT_TENANT` | yes | The tenant they sign in to. |
| `EARM_IT_CONSENT_PROBE_SCOPE` | no | The Graph scope requested incrementally, one you have **not** consented to the Graph CLI Tools app. Default `Presence.Read` (user-consentable, reads only your own presence). |
| `EARM_CLOUD` | no | The cloud (`public` default, `usgov`, `usgovdod`; `china` has no Graph CLI Tools app). |

```powershell
$env:EARM_IT_TENANT = '<tenant-id>'
$env:EARM_IT_CONSENT_PROBE = '1'
pwsh -File test.ps1 -Target EArm.Integration.Tests -Filter "Category=Consent" -Detailed
```

When the probe scope is already consented, probe 1 skips ("already consented"). To run the probes again, revoke it (Microsoft Entra admin center > *Enterprise
applications* > *Microsoft Graph Command Line Tools* > *Permissions* > *User consent*, or remove the scope from your `oauth2PermissionGrant`, see
[SECURITY.md](../../SECURITY.md#delegated-sign-in-and-shared-consent)) or pick another scope with `EARM_IT_CONSENT_PROBE_SCOPE`.

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
| `S2a` | The base sign-in (the spike's historical set `User.Read`, `Organization.Read.All`; `earm auth login` now asks for `User.Read` only). | Baseline of the login flow. |
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
