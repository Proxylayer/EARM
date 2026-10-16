# Changelog

All notable changes to EARM are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

No version has been released yet. Everything below describes the changes since the switch from the Azure CLI / PowerShell
transport to the built-in Microsoft Graph HTTP client.

**Breaking changes**

- EARM keeps no local state. The `state` command group (`state unlock`, `state forget`, `state adopt`,
  `state set-provenance`), `deployment show`, `deployment list` and `deployment operation list` are removed, and
  `--state-dir`, `EARM_STATE_DIR`, `--force-unlock`, `--lock-timeout`, `--max-deletes`, `--allow-delete-all`,
  `--delete-adopted` and `deployment delete --delete-resources` are unknown options (usage error, exit 1).
- Exit code 4 (state locked) is retired and never returned; it will not be reused.
- `--mode Complete` no longer deletes anything; it reports undeclared objects as orphans.
- `deployment delete` takes the template (`-f`, `-p`) and deletes the objects it declares, found by their live key.
- A plain `create` that would replace objects asks for confirmation and fails on a non-interactive console without `-y`.
- Principal-side edges (`memberOf`, `appRoleAssignments`, `directoryRoles` declared on a principal) are ensure-only and
  never removed; an assignment whose principal, role or scope changed leaves the old assignment in place.
- A new `earm auth login` without `--client-id` signs in to Microsoft Graph Command Line Tools instead of the Azure CLI
  application, so consent is requested for that application; the China cloud requires `--client-id`.
- Defaults are applied only when EARM creates an object (see Changed): an existing object's undeclared properties are
  no longer reset.

### Migration

- Delete the old state directory (`.earm/`, wherever `--state-dir` / `EARM_STATE_DIR` pointed) and drop it from source
  control; keep secure-outputs files. Remove the retired options and verbs from scripts.
- Make sure every object can be found by its key: applications and groups by a `uniqueName` equal to the template
  `name` (a group without one is now adopted by its display name, see Added), display-name-keyed types by a unique
  `displayName`. Run `what-if` first and check every `+ Create`.
- Pipelines that relied on Complete mode to remove dropped objects must act on the orphan report themselves; pipelines
  that accept replacements must pass `-y`.
- Password credentials created before the rotation tag moved into the credential name are still matched by base name
  (warning `RotationTagNotStamped`), but a changed `rotationTag` does not rotate them until they have been rotated once;
  declare a different `endDateTime` to rotate one now.
- Run `earm auth login` once (optionally with `--scopes` or `-f <template>`) to sign in to the new default client and
  consent to the scopes your templates need. Sign-ins recorded for the Azure CLI application keep working, with a
  warning.
- Templates that relied on EARM resetting an undeclared property to its default (for example re-enabling a disabled
  user or service principal) must now declare that property.

### Added

- New resource types for Microsoft Entra entitlement management (access packages, see `docs/access-packages.md`):
  - `Microsoft.Entra/entitlementManagement/catalogs`, found by `displayName`; `state` and `isExternallyVisible`
    default to `published` and false only when EARM creates the catalog;
  - `Microsoft.Entra/entitlementManagement/catalogResources`, a group added to a catalog through an `adminAdd`
    resource request (found by catalog and group; the create waits until the group is listed; a role-assignable
    group without Global Administrator or Privileged Role Administrator is `CatalogResourceAccessDenied`);
  - `Microsoft.Entra/entitlementManagement/accessPackages`, found by `displayName` (duplicates in the tenant are
    `AmbiguousMatch`), bound to its catalog on create (`catalogId` is immutable);
  - `Microsoft.Entra/entitlementManagement/resourceRoleScopes`, the group role a package grants: `Member`, `Owner`,
    `Eligible Member` or `Eligible Owner` (PIM for Groups eligibility), picked by display name from the catalog's
    roles. An eligible grant is applied after a PIM for Groups schedule request of the same deployment that names the
    group (it onboards the group) and waits up to 90 s for the catalog to offer the role, so one run succeeds; it is
    deferred to the next run (a `PimGroupNotOnboarded` warning) only when that wait times out, and is the error
    `PimGroupNotOnboarded` when no request onboards the group. `deployment delete` and its preview warn before
    removing a role scope people hold (how many active assignments of which package lose which role);
  - `Microsoft.Entra/entitlementManagement/accessPackages/assignmentPolicies`, a package's policy found by
    `displayName` within the package: only the declared values are compared, and an update is a full `PUT` of the
    live policy with the declared values merged in.
  Catalog resources apply before the role scopes of their group and role scopes before the policies of their package.
  Deletes are refused by Graph while assignments exist (`AccessPackageAssignmentsExist`) or a catalog still holds
  packages (`CatalogNotEmpty`). Assignments to people are not managed. Sample: `samples/access-packages.entra.json`,
  where every package, the approver group's included, carries its assignment policy.

- New resource type `Microsoft.Entra/authenticationMethodConfigurations`: manages the tenant authentication methods
  policy per method (fido2, temporaryAccessPass, x509Certificate, microsoftAuthenticator, sms, voice, email,
  softwareOath). Configurations are never created; only declared properties are patched; targets and AAGUIDs compare
  as sets; deprecated FIDO2 properties raise a warning; delete resets fido2, temporaryAccessPass and x509Certificate
  and refuses the others (`NotDeletable`); after a write the configuration is read again until the declared values
  show.
- New resource types for Privileged Identity Management, shaped like their ARM counterparts in
  `Microsoft.Authorization`, with the `guid()` name used as a label and the schedule found by its principal, role or
  group, and scope:
  - `Microsoft.Entra/roleEligibilityScheduleRequests` (directory role eligibility, with `role` / `administrativeUnit`
    sugar);
  - `Microsoft.Entra/roleAssignmentScheduleRequests` (time-bound active directory role assignments);
  - `Microsoft.Entra/privilegedAccess/groupEligibilityScheduleRequests` (PIM for Groups member or owner eligibility;
    the first request onboards the group in PIM for Groups permanently);
  - `Microsoft.Entra/privilegedAccess/groupAssignmentScheduleRequests` (time-bound active PIM for Groups
    assignments).

  Active assignments must be time-bound; a permanent assignment, or the same principal, role and scope also declared
  through `roleAssignments`, `directoryRoles`, group `members` / `owners`, `memberOf` or `ownerOf`, is refused
  (`PimAssignmentConflict`). Schedules shorter than 5 minutes are refused offline, and removing an active assignment
  within 5 minutes of its creation fails with `ActiveAssignmentTooNew`. Undeclared schedules of declared principals and
  target groups are reported as retained.
- New resource type `Microsoft.Entra/roleManagementPolicies`: manages the declared rules of the PIM policy of a
  directory role or of a PIM for Groups membership / ownership, shaped like ARM's
  `Microsoft.Authorization/roleManagementPolicies`. Policies are never created or deleted and are applied before the
  schedule requests of the same role or group. A group that PIM for Groups has not onboarded yet is reported as
  `PimPolicyNotAvailable`; when a schedule request in the same deployment onboards it, the policy is deferred to the
  next run (two runs). Approver `isBackup: true` is refused offline because Graph never returns it.
- A combined privileged-access sample (emergency access, tiered groups under PIM policies, PIM for Groups eligibility,
  a report-only Conditional Access policy on the PIM authentication context and authentication method
  configurations), plus a sample for each new type.
- Groups: a group without a `uniqueName` (created by a script or the portal) is adopted by its declared `displayName`
  when no group holds the declared `uniqueName`. Exactly one match is adopted and stamped with the `uniqueName` once
  (warning `GroupAdoptedByDisplayName`); several matches are `AmbiguousMatch`; a group holding another `uniqueName` is
  never adopted; a refusal to set the name is `UniqueNameNotSettable`. `deployment delete` never deletes a group that
  matches only by display name.
- Delegated sign-in with per-run scopes: `what-if`, `create` and `deployment delete` request exactly the delegated
  scopes the template needs for that verb instead of `.default` (service principal, workload and managed identity
  sign-ins keep `.default`).
- `auth login --scopes` and `auth login -f <template> [-p] [--verb]` consent to scopes up front; a template with Key
  Vault references also consents to the vault.
- Run-time consent prompts on interactive terminals, followed by a retry with a fresh credential; non-interactive runs
  stop before any write with `ConsentRequired` (exit 3) and the exact `auth login` command to run.
- `--device-code` on `create`, `what-if` and `deployment delete`.
- `auth status` shows the client application, and with `-f <template>` the scopes still missing.
- `earm permissions --verb whatif|create|delete` prints the delegated scope set for one verb.
- Expression functions `tryGet`, `distinct`, `shallowMerge`, `mapValues`, `indexFromEnd` and `tryIndexFromEnd`.
- `--exit-code-on-changes` also applies to `create`: exit 5 when the run applied a create, modify, delete or edge
  change (orphans never count).
- Offline validation: authentication strength names longer than 30 characters; duplicate federated credential
  audiences and duplicate issuer + subject pairs on one application; Conditional Access policies that combine
  `includeApplications` with user actions or authentication contexts (`ConflictingApplicationIncludes`).
- Graph errors carry the request-id, client-request-id and date that Graph returned, for support tickets.

### Changed

- Identity is resolved from the live tenant on every run: an object is its declared type plus name, found by its key
  (`uniqueName`, `displayName`, user principal name, or the composite key of an assignment). A rename is a new object;
  ambiguous display names are `AmbiguousMatch`. There is no lock, and `-n` is only a label in the output.
- Complete mode runs a read-only orphan report over the declared types; orphans appear in the what-if text and JSON and
  in the deployment result (`orphans`) and never count as changes. The deployment result JSON drops `detached` and
  `deleted`.
- `deployment delete -f <template>` deletes the template's declared objects found by their live key, in reverse
  dependency order, behind a confirmation. The signed-in principal and service principals of other organizations are
  never deleted.
- Replacing an object because of an immutable property change needs `-y` or an interactive confirmation
  (`RecreateNotConfirmed` otherwise).
- Principal-side edges only add; target-side authoritative sets still remove undeclared members. Assignments that are
  no longer declared are reported as retained (`AssignmentRetained`) and never removed.
- Secrets are write-once: secret-bearing values on an existing object diff as no effect and are not rewritten on
  reruns; non-secret members of a secret-bearing object are still compared.
- A password credential's `rotationTag` is stored in the credential's display name (`<name>#<rotationTag>`); `#` in the
  name or tag, or a display name over 256 characters, is refused.
- Display-name-keyed types (Conditional Access policies, named locations, authentication strengths, administrative
  units, custom role definitions): before a create, absence is confirmed with repeated reads and an object found by any
  read is adopted; after a create, the key is read back until it is visible twice in a row (warning
  `CreatedObjectNotVisible` if it never shows).
- Eventual consistency: after an ambiguous or "already exists" create, the key read is polled until the object appears
  or the replication window elapses; mutating requests are no longer cancelled once sent (Ctrl+C is honoured between
  operations).
- The default interactive client is Microsoft Graph Command Line Tools; sign-ins recorded for the Azure CLI application
  keep working with a warning recommending `earm auth login`. A silent consent failure reports "consent needed" rather
  than "sign-in expired".
- Expressions follow ARM more closely: `contains()` on strings is case-sensitive; `union()` merges nested objects
  recursively; supplied values and user-function values that look like expressions no longer bypass type checks;
  expression default values are checked against type, allowed values and limits; expression-valued `minValue` /
  `maxValue` / `minLength` / `maxLength` are rejected; `allowedValues` on array parameters constrains each element.
- `create -c -o json` prints a single JSON document with the what-if embedded (a declined prompt prints one failed
  document); the confirmation prompt shows the what-if as text on stderr.
- What-if JSON rows that cannot be planned have changeType `Unsupported`.
- Choice option values (`--mode`, `-o`, `--result-format`, `--auth`, ...) are case-insensitive.
- Groups: `mailNickname` and Public/Private `visibility` are updated in place instead of being treated as immutable.
- Applications: the live `uniqueName` compares case-insensitively.
- Users: the defaults (`accountEnabled: true`, the derived `mailNickname`,
  `passwordProfile.forceChangePasswordNextSignIn: true`) now apply only when EARM creates the user. An existing user
  that EARM finds by its userPrincipalName is no longer given them, so its undeclared properties are never written.
  Previously a template that declared a user with only edge properties (such as `memberOf` or
  `administrativeUnits`) re-enabled that user's account if it was disabled. Effect on existing templates: an existing
  disabled user is no longer re-enabled, and what-if no longer shows an `accountEnabled` change for it; templates that
  rely on EARM keeping existing accounts enabled must now declare `"accountEnabled": true`.
- Defaults are now create-only for every resource type: an existing object that EARM finds or adopts is changed only
  where the template declares a property. Types changed: `servicePrincipals` (`accountEnabled: true`,
  `appRoleAssignmentRequired: false`; also not written when a create turns into the adoption of a principal that
  appeared after the read), `conditionalAccess/policies` (`state: enabledForReportingButNotEnforced`),
  `conditionalAccess/namedLocations` (`isTrusted: false`; `includeUnknownCountriesAndRegions: false`,
  `countryLookupMethod: clientIpAddress`), `conditionalAccess/authenticationContextClassReferences`
  (`isAvailable: true`) and `administrativeUnits` (`membershipRuleProcessingState: On`, still applied when a unit becomes
  dynamic). Previously these defaults were part of the desired state on every run, so a property changed outside EARM
  but not declared in the template was silently reset (a disabled service principal re-enabled, an enabled CA policy
  switched to report-only, a trusted location untrusted, a paused dynamic unit resumed). Defaults still apply on create,
  on the soft-deleted restore that replaces a create, and on the replacement of a named location's kind change. Kept on
  every run, because they belong to a declared value: `conditions.clientAppTypes` and `grantControls.operator` inside a
  declared CA `conditions` / `grantControls` (Graph replaces those objects whole), and the scope, `scheduleInfo` and
  `justification` of PIM schedule requests. Effect on existing templates: what-if no longer shows changes for these
  undeclared properties; templates that relied on EARM resetting an undeclared property to its default must now declare
  it.

### Removed

- The local state subsystem: the state file and lock, provenance and operation records, the `state` command group,
  `deployment show`, `deployment list`, `deployment operation list`, and the options `--state-dir`, `EARM_STATE_DIR`,
  `--force-unlock`, `--lock-timeout`, `--max-deletes`, `--allow-delete-all`, `--delete-adopted` and
  `deployment delete --delete-resources`. `permissions` no longer takes `-n`.
- Exit code 4 (state locked).
- The state diagnostic codes (`StateLocked`, `TenantMismatch`, `StateSchemaUnsupported`, `StateEntryInvalid`,
  `StateEntryMismatch`, `StateStoreUnsafe`, `StateSaveFailed`) and the codes `ResourceDetached`, `OrphanSharesObject`,
  `AdoptedExistingObject`, `AdoptedObjectNotDeleted` and `DeleteLimitExceeded`.

### Fixed

- A `copy` block in `variables` is an `InvalidCopy` error instead of a variable named `copy`;
  `managementGroupResourceId` is rejected with a pointer to `resourceId`.
- Trusted IP named locations are unmarked before they are deleted (Graph refuses to delete a trusted location); a
  persistent refusal is reported as `NamedLocationTrusted`.
- A Conditional Access policy created right after the named location it references is retried while the location
  replicates.
- Conditional Access deletes refused because an earlier unmark or policy delete has not propagated yet are repeated
  within the replication window.
- Federated identity credentials of one application are written one at a time (Graph refuses concurrent writes to the
  same collection); credentials of different applications still go in parallel.
- Service principal members of groups are read through the type-cast endpoint, so an added service principal member
  no longer shows as a change on every run.
- A user's initial password output is null (with `SecretNotAvailableOnRerun`) on runs that did not create the user,
  instead of failing the run.
- Applications: live-only collections are cleared with `[]`, and `web.redirectUriSettings` is no longer set to null
  (Graph refuses both nulls).
- PIM: schedules that PIM has not listed yet, and PIM for Groups' 403 before a group is onboarded, are read as "no
  schedule" instead of failing; a 403 right after a group was created is retried as replication lag
  (`PimGroupAccessDenied` if it persists); a just-replaced schedule that is still settling is retried; changing or
  removing a group's only owner assignment is reported as `PimLastOwnerAssignment`.
- Samples use `microsoft.directory/users/basic/update` in custom roles (Graph rejects `users/password/update`).
- Help text: the `--client-id` default and the macOS schema directory are described correctly.
- Unit tests no longer delete the developer's real EARM token cache.
