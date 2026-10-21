# Changelog

All notable changes to EARM are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added

- `Microsoft.Entra/accessReviews/definitions`: standalone access reviews (Graph `accessReviewScheduleDefinition`) of group members, access
  package assignments, application or role access. Keyed by `displayName` (the whole collection is matched client-side; duplicates are
  `AmbiguousMatch`); only declared values are compared, and an update is a full `PUT` of the live definition with the declared values merged
  in. `scope` and `instanceEnumerationScope` are immutable (a change is refused, never a replace), and a reviewer change is refused while the
  reviewers are not all individual users, as Graph refuses it. Needs `AccessReview.ReadWrite.All` (`AccessReview.Read.All` for `what-if`).
  New sample `samples/access-reviews.entra.json`; see docs/providers.md.
- Graph request scheduler: every Graph request, each `$batch` sub-request included, is paced per Microsoft service within Microsoft's published
  throttling limits, so EARM waits before sending instead of collecting 429s. Directory requests are charged their published resource-unit cost
  (reconciled with `x-ms-resource-unit`) against 3,500 / 5,000 / 8,000 units per 10 s by tenant size (read once with `GET /users/$count` when the
  smallest budget would be reached; the smallest tier stays when it cannot be read) and 3,000 writes per 150 s; Conditional Access policies and named
  locations are sent one at a time, at most one per second. A 429 from any area pauses that whole area until `Retry-After` has elapsed (the throttle
  backoff without one), then it resumes at the same rate; a `$batch` sub-response 429 is waited out before the sub-request is re-sent. An envelope
  carries at most one Conditional Access sub-request. Areas without a published rate rely on `Retry-After`. The budgets are fixed. See
  docs/engine.md#graph-request-scheduler.

### Changed

- **Breaking:** an empty authoritative list (`members: []` on groups and administrative units, `owners: []` on groups, applications and service
  principals; a literal or an expression evaluating to `[]`) **declares nothing in Incremental mode** (the default): no member or owner is added or
  removed, and an `EmptyEdgeListIgnored` info says the list was ignored. It used to remove every live entry. In **Complete** mode an empty list is
  still authoritative and removes every entry. A non-empty list is authoritative in both modes. A literal `members: []` beside a PIM for Groups
  active assignment is no `PimAssignmentConflict` in Incremental mode any more. See docs/edges.md#empty-lists.
- **Breaking:** `owners: []` on a `Microsoft.Entra/groups` resource is refused in Complete mode: the row is unsupported in `what-if` and `create`
  before anything is written. There the declared `owners` list is authoritative, so an empty one asks for every owner to be removed, which Graph
  refuses for the last user owner of a group (group-delete-owners) and which therefore failed half-way. In Incremental mode it declares nothing
  (above), so the schema accepts `[]`. Leave `owners` out to keep the live owners untouched. Applications and service principals accept an empty
  list in both modes.
- An authoritative `members` or `owners` list is no longer pruned from a partial desired set. While an entry is still a deploy-time value,
  `what-if` shows each live entry it does not otherwise list as a removal flagged `?` ("removed at deployment unless a deploy-time entry
  resolves to it"), counted as an edge removal, instead of a certain one; `create` decides once every entry has resolved. At deployment time
  a placeholder still unresolved in a principal-side `memberOf` / `ownerOf` / `administrativeUnits` of the same kind (its referenced resource did
  not deploy) now also leaves the set unpruned, with an `EdgePassSkipped` warning, where the set used to remove the live entries that
  declaration might have named. See docs/edges.md#authoritative-owners.

### Fixed

- A `Microsoft.Entra/accessReviews/definitions` is no longer refused on its second run with "Immutable property properties.scope … differs from
  the live object", and its reviewers no longer show as changed. Graph stores scope and reviewer queries rewritten (`/groups/{id}/transitiveMembers`
  comes back as `/v1.0/groups/{id}/transitiveMembers/microsoft.graph.user`, `/groups/{id}/owners` as `/v1.0/groups/{id}/owners`); access review
  queries are now compared by strict equivalence: a leading `/v1.0` is ignored, and a `/microsoft.graph.user` cast directly after `/members` or
  `/transitiveMembers` equals its absence. `/beta`, a different cast and a different `$filter` are still differences, and the declared text is
  still what is sent to Graph. See docs/providers.md (access reviews).
- `Microsoft.Entra/entitlementManagement/resourceRoleScopes` declared in different access packages of one catalog that grant the same role
  of the same group (or app, or site) are no longer reported as `DuplicateGraphObject` by `what-if` and `create`. Graph gives a role scope
  the composite id `<roleId>_<scopeId>` of the catalog's role and scope, so the same grant has the same id in every package of the catalog;
  the "two declarations, one object" check now scopes a role scope by its access package. Two declarations of the same grant in one package
  are still a duplicate. The check also tells resource types apart, so objects of different types never count as one.
- A `Microsoft.Entra/entitlementManagement/resourceRoleScopes` that grants `Eligible Member` or `Eligible Owner` of a group onboarded to PIM
  for Groups in the same run by a group `Microsoft.Entra/roleManagementPolicies` with `properties.onboarding.temporaryEligiblePrincipalId` is
  no longer the error `PimGroupNotOnboarded` on the first run. Such a policy now counts as a same-run onboarding, like a group schedule
  request: the role scope is applied after it, the what-if shows a Create that waits for the onboarding, and the create waits (every 5 s, up
  to 90 s) for the catalog to offer the eligible role. See docs/access-packages.md#can-a-package-grant-eligible-pim-for-groups-membership.

## [0.3.0] - 2026-10-17

### Added

- `deployment validate --fetch-links` (off by default): the offline validation downloads the https linked templates and parameter files of
  nested deployments, with the rules and the per-run cache of `what-if` and `create`, and validates them like local files. Without it they
  are still not fetched (`LinkedTemplateNotFetched`, whose message now names the option).
- Key Vault references in the parameters of nested deployments, as ARM allows: a `reference` entry of `properties.parameters` (its vault id,
  secret name and version may be expressions of the parent, ARM's dynamic id) and a `reference` in the linked parameter file of
  `parametersLink`. `what-if` and `create` resolve them after the preflight with the signed-in identity (secure, checked against the
  parameter's constraints, `EARM_KEYVAULT_ALLOWED` applies; a failure is `KeyVaultReferenceUnresolved`, exit 3); offline they stay
  unknown, and `earm permissions` and the run's Key Vault token take them into account. They were `NestedDeploymentFeatureNotSupported`.
- Outer-scope nested templates are evaluated in the parent's context, as in ARM: `copyIndex()` in their resources and outputs is the iteration
  of the deployment's own copy loop, and `reference('name')` also finds the parent template's resources by name (with the implicit
  dependency).
- `Microsoft.Entra/applications/keyCredentials`: certificate credentials of an application. The key is the credential's `displayName`; the
  public certificate (`key`, base64 DER or PEM, also from a `securestring` parameter) is compared by thumbprint. Graph has no
  single-certificate add without proof of possession, so EARM PATCHes the whole `keyCredentials` collection and sends every credential it
  does not manage back unchanged (ensure-only); a replacement swaps the entry in one PATCH. Private keys and PFX files are refused, as are
  `usage: Sign`, two live credentials with the same name (`AmbiguousMatch`) and an application that also declares `keyCredentials`.
- `Microsoft.Entra/applications/extensionProperties`: directory extension definitions (`dataType`, `targetObjects`, `isMultiValued`). The
  name is the short name; Graph's `extension_<appId>_<name>` is what `reference(...).name` returns. Every property is immutable, so a change
  is a confirmed recreate; every delete warns that the stored values are lost (`DestructiveDelete`), and an extension synced from
  on-premises is kept by `deployment delete`.
- Sample `app-certificates-extensions` with both types.
- Tenant policy resource types (docs/providers.md, "Worked example: tenant policies"):
  `Microsoft.Entra/groupLifecyclePolicies` (the group expiration policy, a singleton named `default`),
  `Microsoft.Entra/policies/tokenLifetimePolicies`, `Microsoft.Entra/policies/claimsMappingPolicies` and
  `Microsoft.Entra/policies/appManagementPolicies` (keyed by `displayName`; duplicates are `AmbiguousMatch`),
  `Microsoft.Entra/policies/defaultAppManagementPolicy` (the tenant default, never created; delete keeps it, `NotDeletable`),
  `Microsoft.Entra/policies/crossTenantAccessPolicy` (the default cross-tenant access configuration, `default`; delete resets it to the service
  defaults) and `Microsoft.Entra/policies/crossTenantAccessPolicy/partners` (keyed by the partner's `tenantId`). Policy documents
  (`definition`) may be declared as JSON objects and compare by content; undeclared members of declared nested setting objects keep their live values.
- Principal-side, ensure-only policy edges: `groupLifecyclePolicies` on Microsoft 365 groups (`addGroup`, for a `Selected` policy),
  `tokenLifetimePolicies` and `appManagementPolicies` on applications and service principals (one per object), `claimsMappingPolicies` on service
  principals. The `tenant-policies` sample, permission goldens and the `tenant-policies-incremental` what-if scenario cover them; the gated
  integration scenarios (`EARM_IT_ALLOW_APP_MANAGEMENT`, `EARM_IT_ALLOW_CROSS_TENANT`, `EARM_IT_ALLOW_GROUP_LIFECYCLE`) are written but not yet run.
- What-if scopes: `Policy.ReadWrite.ApplicationConfiguration` and `Policy.ReadWrite.CrossTenantAccess` read with `Policy.Read.All`.
- Access reviews of access package assignments: an assignment policy's `reviewSettings` now has a typed schema (`isEnabled`,
  `expirationBehavior`, `isRecommendationEnabled`, `isReviewerJustificationRequired`, `isSelfReview`, `schedule` with `startDateTime`,
  `expiration` and `recurrence`, `primaryReviewers`, `fallbackReviewers`). Graph's filled-in review members and a `startDateTime` written back
  with milliseconds are not drift; reviewers compare as sets, and a reviewer without an id (`requestorManager`, the sponsors) matches by its
  type. `InvalidAccessPackageReviewSettings` (offline, and at plan time for computed values) checks reviewers when a review is enabled and not
  a self-review, the schedule, the ISO 8601 duration of each review and the recurrence shape. A licence refusal of an enabled review is
  `GovernanceLicenseRequired`. The access-packages sample reviews every access group's package quarterly.
- Access packages over applications and SharePoint Online sites: `entitlementManagement/catalogResources` accepts `originSystem`
  `AadApplication` (`originId` = the service principal's object id) and `SharePointOnline` (`originId` = the site URL, matched
  case-insensitively and without a trailing slash), and `entitlementManagement/resourceRoleScopes` grants an app role (by display name or
  app role id) or a site group (by full display name or its number in the site); `role` is no longer limited to the four group roles,
  and a role scope's `originSystem` defaults to the catalog resource's. A template service principal is created before its catalog
  resource. Permissions follow the kind: `Application.ReadWrite.All` or `Sites.FullControl.All` in place of `Group.ReadWrite.All`, and
  the overlay key `rolesAllOfByValue` gives the Entra roles (Identity Governance Administrator for an app; with SharePoint
  Administrator for a site). New sample `samples/access-packages-apps-sites.entra.json`. Graph lists sites in a catalog only to
  delegated callers, so sites are deployed signed in.
- `earm permissions` expresses Entra roles needed together: a `together: A + (B | C)` line in the table, an entry with an empty
  `anyOf` and an `allOf` array of parts in the JSON (the overlay key `rolesAllOf`). A catalog resource now needs Identity Governance
  Administrator together with Privileged Role Administrator (role-assignable template group) or with Groups Administrator, Intune
  Administrator or User Administrator (any other template group); an application adding a role-assignable group also needs
  `RoleManagement.ReadWrite.Directory`.
- `GovernanceLicenseRequired`: Graph's 400 `NoGovernanceSku` on an eligible access package role (Eligible Member / Eligible Owner) is
  reported with the licence it needs and what to do instead. Apply-time only: the what-if does not read `subscribedSkus`.
- `AccessPackageExpirationExceedsEligibility` (warning, before planning): an access package assignment policy whose assignments outlive
  (or never end, `noExpiration`) the `Expiration_Admin_Eligibility` maximum the template's `roleManagementPolicies` requires for a group
  the package grants as Eligible Member / Eligible Owner. `DeclarationConflict` gains a `Severity` so a conflict source can warn.
- Adopt-only resources: `"x-earm": { "adoptOnly": true, "objectId": "…" }` on any resource (groups and users in particular) adopts and
  manages an object that must already exist and never creates it (`AdoptOnlyNotFound` instead of a create; a user needs none of the
  create-only properties); `objectId` pins the Graph object id the key must lead to (`AdoptedObjectMismatch`), which makes an adoption
  by `displayName` a hard check. `deployment delete` leaves adopt-only objects alone. Malformed declarations are `InvalidAdoptOnly`.
- applications/passwordCredentials: `stampRotationTag` (EARM-only boolean, default `false`). When `true` and the live credential's
  `displayName` carries no `rotationTag` (created before EARM kept the tag there), the secret is rotated once so the declared tag is
  stamped into the new credential's name, instead of keeping it with the `RotationTagNotStamped` warning. It needs a `rotationTag` and
  has no effect once the tag is stamped, so it can stay declared.
- End-of-run retry: a provider can defer a resource until later work of the same run has happened (`ResourceChange.RetryAtEndOfRun`). After
  the last batch the executor reads and plans it again (log event 2088), repeating the read on the replication schedule (1, 2, 4 s, then
  every 5 s, up to 90 s) while it is still deferred, and applies what that plan says.

### Changed

- `earm permissions` narrows the roles of a group PIM policy's `onboarding` by the group, as for the PIM for Groups requests: Privileged
  Role Administrator for a role-assignable template group that `group` or `scopeId` references, Identity Governance Administrator,
  Groups Administrator, User Administrator or Directory Writers for any other template group (all five when the group is not declared in
  the template). The policy itself still needs Privileged Role Administrator.
- The what-if of an access package assignment policy shows a changed approval stage as the changed properties inside that stage
  (`requestApprovalSettings.stages[0].durationBeforeAutomaticDenial: "P14D" => "P7D"`) instead of the whole stage removed and added:
  stages are matched by position (overlay `memberKey` `$index`), as for the approval stages of a role management policy.
- `reference()` / `references()` in the outputs of a nested template evaluated in the outer scope is now `ReferenceNotAllowedHere` (ARM
  requires the inner scope there); EARM used to evaluate it. Set `"expressionEvaluationOptions": { "scope": "inner" }` on the deployment.
- A group PIM policy (`roleManagementPolicies`) deferred because a schedule request of the same run onboards its group is now applied at
  the end of that run, once Graph lists the onboarded group's policy, instead of by the next run: such a template converges in one run.
  `create` reports `PimPolicyNotAvailable` only when Graph still lists no policy at the end of the retry; the what-if still shows the
  deferred `Ignore` row with the warning.

- `earm permissions`: a languageVersion 2.0 `"existing": true` resource needs only the read variant of its type's permissions and no
  Entra role.
- `earm permissions`: PIM for Groups requests (and a group policy that onboards its group) list User Administrator and Directory
  Writers as alternatives besides Privileged Role Administrator, Identity Governance Administrator and Groups Administrator.
- `earm permissions`: a PIM for Groups request whose `groupId` references a template group narrows those alternatives by the group's
  kind, as catalog resources do: only Privileged Role Administrator for a role-assignable group, the other four for any other group.
  An existing group (or `group`) keeps all five.
- The type schema overlays no longer carry the per-type `permissions.delegated` lists. They were never read (delegated scopes come
  from the providers' conditional rules), so they could only drift from what a run requests.
- A plain `earm auth login` requests only `User.Read` (the base set no longer includes `Organization.Read.All`, which needs admin consent),
  so a user without an admin role can sign in. Signed-in runs no longer request `Organization.Read.All` for the preflight either: `User.Read`
  reads `GET /organization` (id, display name, verified domains). A template that calls `tenant()` still requests it, for `countryCode`.
- `what-if`, `create` and `deployment delete` download an https linked template (or linked parameter file) once per run; the offline
  expansion before sign-in, the validation and the plan used to download it again each.
- Access package resource role scopes no longer count the package's delivered assignments on every read (one call per role scope in
  every what-if and create): the count is read only when a delete is planned (`deployment delete` and its preview, the delete half of a
  replacement), through the new `IDeleteWarningSource.DescribeDeleteRiskAsync` and `TemplateDeleter.FindDeclaredObjectsAsync`.
- The executor's replication waits are adaptive: the read after a create with an uncertain outcome, the read-your-write after a create of a
  display-name-keyed type, and the repeat of a delete refused while an earlier change propagates now wait 1, 2 and 4 s, then every 5 s (was a fixed
  5 s), still up to 90 s. Most objects show up within a second or two, so a run spends less time waiting; the window is unchanged.
- `create` without `-y` (or with `-c`) confirms the absence of the display-name-keyed objects it will create in later dependency batches while the
  earlier batches deploy, instead of one batch after the other. The protection against duplicates is the same: the executor still reads the key once
  more right before the `POST`, and an object any read finds is adopted. New log events 2086 (absence confirmed by the early reads) and 2087 (early
  reads started, Debug).
- The warning for an object found on an extra read (event 2083) names the attempt out of the total (`extra read 1 of 3`).
- `create -y` now overlaps the absence confirmation of later batches with the earlier ones, as `create` without `-y` already
  did: a new Conditional Access policy, named location, authentication strength, administrative unit or custom role definition
  in a later batch no longer adds its own 12 s wait when its key (its name) is known before the run. The keys come from a dry
  run that makes no Graph call and fills none of the run's caches; a key built from an object an earlier batch creates is
  still confirmed right before its `POST`. The run still reads the key once more before every `POST`, so nothing is created
  twice.

### Fixed

- What-if: a resource that reads `reference(id, apiVersion)` (directly or through a variable) of an object the same run creates is a
  "known at deployment" (`?`) row instead of an error row (`reference(): existing resource … was not found`). A variable whose value
  depends on such a reference is not remembered, so `create` without `-y` reads the object once it exists.
- `earm permissions` no longer lists the nonexistent `AppRoleAssignment.Read.All` for a `reference(id, apiVersion)` lookup of an app
  role assignment: it lists `Application.Read.All`, the permission that reads app role assignments.
- applications: the create upsert (`PATCH /applications(uniqueName='…')` with `Prefer: create-if-missing`) now carries only the declared
  properties, so an application that appeared after the plan's read no longer gets the default `signInAudience: AzureADMyOrg` merged
  into it. A real create still ends with `AzureADMyOrg` (Graph's documented default; EARM PATCHes it right after the create should
  Graph have applied another value).
- Group `owners`: a service principal owner is now read through `GET /groups/{id}/owners/microsoft.graph.servicePrincipal`, because v1.0
  `GET /groups/{id}/owners` does not list service principal owners (Learn). A declared service principal owner was planned as an add on
  every run, and an undeclared one was never pruned. Device and organizational contact members need no such read: v1.0 lists them.
- applications/passwordCredentials: the `PasswordCredentialOrphaned` message no longer says EARM "did not record" the extra credentials
  (EARM keeps no state); it names the credentials that were not there when the run read the application.
- A request that times out (or is otherwise cancelled without Ctrl+C) no longer aborts the whole `what-if`: the resource whose read
  timed out is an error row and the rest of the what-if completes. The same holds for an existing-resource lookup (`reference(id,
  apiVersion)` stays a placeholder and is reported as unresolved), the orphan report (that type is not listed, with a warning), the
  edge pass (the declaring resources fail their relationships) and `deployment delete` (that object fails). Ctrl+C still stops the run.
- An adopt-only resource whose object is missing at apply time fails with `AdoptOnlyNotFound` after one key read. It no longer waits
  for the extra reads that confirm an absence before a create (12 s for a Conditional Access policy, named location, authentication
  strength, administrative unit or custom role definition): nothing is created for it either way.
- docs/engine.md no longer says a resource whose condition is false is kept as an `Ignore` row: it is left out of the
  deployment and the what-if lists it only as skipped (`skipped[]`).
- `union()` and `intersection()` with a single argument are rejected (`InvalidFunctionArguments`), as ARM requires at
  least two.
- docs/format.md now states the order of `items()`: sorted alphabetically by key, ignoring case, as ARM returns it (the
  behaviour was already this; it is now pinned by tests).
- Reading part of a parameter (`parameters('x')[i]`) or of a lambda binding (`lambdaVariables('x').y`) no longer copies the
  whole value and counts it against the run's 5,000,000 value-node budget on every read; only the selected part is copied,
  as for `variables('x')[i]`. A large array parameter read once per copy iteration could otherwise fail with
  `EvaluationBudgetExceeded`.
- A resource whose name is built from `deployment().name` got a wrong planned id in `what-if`, `create` and `delete`
  (`g-validate` instead of `g-<deployment name>`): the static evaluation of names, ids and `dependsOn` used the offline name `validate`. It now uses the deployment
  name (`-n` or its default); an outer-scope nested template sees the parent's deployment. `earm permissions`, the run scopes and the offline
  expansion of nested templates evaluate `deployment().name` the same way.
- A secure value now updates an existing object when Graph reads the property back: a conditional access policy whose
  `state` comes from a `securestring` parameter is switched to `enabled` with `--allow-ca-enforce` (it kept its live state,
  because every secure value was create-only). Such a value is compared like any other and shown as `*** => ***` when it
  differs; an equal value is `NoChange`. Write-only secrets (a path the type declares `secure`, such as
  `passwordProfile.password`, or a value Graph does not return) are still sent only on create, so a rerun neither PATCHes them
  nor trips `--exit-code-on-changes`.
- The what-if no longer fails a resource whose whole `properties` is one expression that reads `reference()` of a resource the
  same run creates ("properties is a single expression whose value is only known at deployment"): it shows the resource as
  known at deployment (`Ignore`, `?`), and `create` evaluates the expression once the referenced resource exists.
- A resource whose whole `properties` is one expression is now ordered like the same resource written as an object literal:
  the known, non-secure values of the expression are declared as literals, so the apply order that matches declared values
  (a group PIM policy before the eligibility and assignment requests of its group, access package resource ordering) sees
  them. Secure values and values known only at deployment stay expressions, as in an object literal.
- A create whose request ends with a cancellation that is not Ctrl+C (a raw HTTP timeout that a client does not report as a transport
  timeout) is treated like a timed-out create: the key is read again and an object that exists is counted as created
  (`CreateOutcomeUncertain`) instead of failing the resource and leaving the object unrecorded.
- `HttpGraphClient.BatchAsync` cancelled by the caller after its first 20-request chunk no longer throws away the per-item results of the
  chunks already sent: they are returned, and the items of the chunks not sent carry the new item code `BatchCancelled` (status 499, never
  ambiguous). A failed item whose individual re-send was cancelled keeps its original failure.
- An update of an existing object cut short by Ctrl+C (between two requests of the provider) or by a raw HTTP timeout is no longer reported
  as skipped or as an unexpected error: the object is read back by key and planned again, and the resource is reported applied when the
  object now matches the template, or failed with its Graph id ("the update may have been applied in part or not at all") when it does not.
- `create` without `-y` (or with `-c`): a variable that looks up with `reference(id, apiVersion)` an object the same run creates no longer
  keeps the "was not found" of the what-if pass, which failed every resource reading it. A not-found lookup is not remembered by the
  variable store, so the run evaluates the variable again once the object exists.
- A create answered "already exists" whose existing object the provider refuses to manage (a permanent PIM assignment where a
  time-bound one is declared, `PimAssignmentConflict`) is no longer adopted with only an `ImmutablePropertyChanged` warning when the
  object shows up late: the resource fails with the provider's code, reason and the object's Graph id. When the provider itself finds
  and refuses the object, its refusal is reported as is, instead of "the object was created but the create did not complete".
- The what-if of a role management policy shows a changed approval stage as the changed properties inside that stage
  (`setting.approvalStages[0].approvalStageTimeOutInDays: 1 => 2`) instead of the whole stage removed and added: stages are matched by
  position (overlay `memberKey` `$index`, new).
- README.md, docs/cli.md, docs/diagnostics.md, docs/engine.md, docs/format.md and docs/providers.md no longer list assignments and
  password credentials as replaced (delete, then create) on an immutable change. Every property of a role or app role assignment is part
  of its key (a changed value is a new assignment, created; the old one is retained), a grant's only non-key property is updated in place,
  and a password credential rotates (add, then remove). In practice only a named location's kind change replaces an object.

## [0.2.0] - 2026-10-17

This is the first release. Everything below describes the changes since the switch from the Azure CLI / PowerShell
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
- `guid()` and `uniqueString()` now compute the values Azure Resource Manager computes, so every value they produce
  changes once (see Migration).
- `less`, `lessOrEquals`, `greater` and `greaterOrEquals` order strings as ARM does (culture-aware, lowercase before
  uppercase of the same letter: `greater('A', 'a')` is now true); `and()` and `or()` need at least two arguments;
  `array()` of an array returns it unchanged instead of wrapping it; `union()` and `intersection()` match object keys
  case-insensitively; `range()` refuses a start outside 32 bits or a `start + count` above 2,147,483,647.

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
- `guid()` names: what-if shows renamed labels; no objects move. The `name` of the GUID-named types (role assignments,
  PIM requests and policies, app role assignments, grants, catalog resources, resource role scopes) is only a label,
  and the objects are found by their composite key, so the first run after the upgrade shows each such resource under
  its new id with the same change it had before. Anything else built from `guid()` or `uniqueString()` (a display
  name, a `mailNickname`, an app role or scope `id`, a `uniqueName`) gets a new value: review the what-if, and pin
  such values with a parameter or a literal where they must not change (an enabled app role or scope cannot be
  removed by Graph). The new values match what an ARM or Bicep deployment computes for the same inputs.
- Scripts that read a condition-false resource from the what-if JSON (`changeType: Ignore`, reason
  `condition evaluated to false`) read it from `skipped[]` now. Scripts that used `-p a=1 b=2` to get a usage error get both
  values applied instead.

### Added

- Nested and linked deployments (`Microsoft.Resources/deployments`, see `docs/format.md#nested-and-linked-deployments`): an inline
  `template` or a `templateLink` (`relativePath` next to the template or relative to its URL, or an https `uri` downloaded with the rules
  of https parameter files) is expanded into the parent's plan: one graph, one what-if, one apply. Outer and inner expression scope as in
  ARM (inner by default and outer refused with languageVersion 2.0), `parameters` or `parametersLink`, `contentVersion` checks,
  `condition` and `copy` on the deployment, `dependsOn` to and from it (a deployment waits for everything inside it), outputs read back with
  `reference('<deployment>').outputs.<name>.value` (secure outputs stay masked), nesting up to 10 levels and 500 deployments, cycle detection
  for linked templates, ids unique across all templates. What-if rows name their nested deployment (`deployment` in the JSON);
  `deployment delete`, the orphan report and `earm permissions` include the nested resources; `deployment validate` checks inline and local
  linked templates offline. Mode Complete on a nested deployment, template specs and other Azure scopes are refused. New sample
  `samples/nested-deployments.entra.json`.
- languageVersion 2.0 (`"languageVersion": "2.0"`), so Bicep-compiled JSON templates of EARM types are accepted unchanged
  (see `docs/format.md#languageversion-20`):
  - `resources` as an object keyed by symbolic name; `dependsOn`, `reference()` and `references()` by symbolic name,
    including a loop's name (every iteration) and `loop[i]` (one iteration);
  - `"existing": true` resources: read by their key and published for `reference()`, shown as `NoChange` rows, never
    created, updated or deleted, and left out of `deployment delete` and the orphan report; a missing one is
    `ExistingResourceNotFound`;
  - `definitions` (user-defined types) with `$ref`, and the type keywords `nullable`, `properties`,
    `additionalProperties` (and Bicep's `sealed`), `discriminator`, `items` and `prefixItems`, checked against supplied
    values and defaults; a nullable parameter may be omitted;
  - Bicep metadata markers such as `__bicep_export!` are accepted and ignored; new diagnostics `InvalidLanguageVersion`,
    `InvalidSymbolicName`, `InvalidExistingResource` and `InvalidTypeDefinition`.
- `references(loop[, 'Full' | 'Properties'])`: the runtime states of a resource loop's iterations, leaving out those whose
  condition is false.
- `reference(nameOrId, apiVersion, 'Full')`: `{ apiVersion, properties, resourceId, isTemplateResource }` (plus
  `symbolicName` for a symbolic reference).
- Variable iteration: a top-level `copy` array in `variables` declares array variables, and a `copy` array inside a
  variable's value builds an array property, as in ARM.
- Multi-line strings (a raw line break inside a template string), as Azure CLI and PowerShell accept them.
- `ArmLimitExceeded` warnings for templates over ARM's limits (256 parameters, 512 variables, 64 outputs, 800 resources,
  4 MB); EARM's own limits are unchanged.
- Template functions `like` (case-insensitive `*` wildcard), `base64ToJson`, `dataUri`, `dataUriToString`,
  `deployer()` (`{ objectId, tenantId, userPrincipalName }` of the signed-in principal; empty `userPrincipalName` for
  an app-only sign-in) and `roleDefinitions(name)`, ARM's name-based role helper applied to Microsoft Entra directory
  roles (`{ id, roleDefinitionId, name, templateId }`; custom roles need `RoleManagement.Read.Directory`).
- `az deployment` options for the CLI (see `docs/cli.md`, "Compared with `az deployment`"):
  - `-x` / `--exclude-change-types` on `what-if` and `--what-if-exclude-change-types` (also `-x`) on `create`: hide change types from the what-if
    (ARM's names, case-insensitive); the deployment, `--exit-code-on-changes` and the replacement confirmation still see every row.
  - `--proceed-if-no-change` on `create -c`: deploy without the prompt when the what-if shows only `NoChange` / `Ignore` rows.
  - `-o jsonc`, `yaml`, `yamlc` and `tsv`, rendered from the verb's JSON document, and `--query` (JMESPath, through the JmesPath.Net package,
    Apache-2.0) on every verb; `--query` without `-o` selects `json`. The default output stays `table`.
  - `-p` in every form the Azure CLI takes: several values per `-p`, a parameter file without `@`, an inline JSON object, `name=@file`, and an
    `https://` URL (no credentials, no redirects, 1 MiB, 120 s; new diagnostic `ParameterDownloadFailed`). A `.bicepparam` file is refused
    with a message.
  - Interactive `what-if` / `create` ask for parameters that have no value (`Please provide string value for 'x' (? for help):`, secure ones
    without echo); `--no-prompt` (also on `validate`) turns this off.
  - `validate --validation-level Template|Provider|ProviderNoRbac`: `Template` (default) is the offline validation; the others sign in and plan
    read-only against the tenant with the write or read scopes.
  - `-w` for `create --what-if`, `-r` for `--result-format`, and `--what-if-result-format` on `create`.
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
- Copy loops reference like ARM: `reference()` / `resourceId()` of one instance of a resource `copy` loop by its evaluated
  name works from anywhere in the template (a single resource, another loop, a property or output loop) and depends on that
  instance only; offline validation indexes every instance whose loop count is computable from parameters and variables.
  A loop whose count is only known at deployment is named in the `ReferenceTargetNotFound` message.
- Property iteration (`"copy": [ { "name", "count", "input" } ]` inside resource properties, `copyIndex('name')`) and output
  iteration (`"copy": { "count", "input" }` on an output), with schema support and `InvalidCopy` shape checks. A property copy
  can build an edge list such as `memberOf` from references to loop instances.
- docs/format.md: a Copy loops section, including the flattened-cartesian idiom for nested loops (scopes x tiers), and the
  sample `samples/scoped-access-groups.entra.json`.
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
- `roleManagementPolicies`: a group policy can opt in to onboarding its group in PIM for Groups with
  `onboarding: { temporaryEligiblePrincipalId }`. When Graph lists no policy for the group and no schedule request of the
  deployment onboards it, the policy row is a Create noted `group not onboarded: a temporary eligibility for
  <principal> will onboard it, then be removed` (what-if writes nothing); the create requests a one-hour member
  eligibility for the principal, waits for the group's policy, removes the eligibility (repeating
  `ActiveDurationTooShort` for up to 7 minutes) and applies the rules, all in one run. A refused eligibility is
  `PimGroupOnboardingFailed`; an eligibility that cannot be removed fails the resource with
  `PimOnboardingEligibilityNotRemoved`, naming it. The opt-in adds `PrivilegedEligibilitySchedule.ReadWrite.AzureADGroup`
  to the permission manifest of that policy only. New sample `pim-group-onboarding`.
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

- `contentVersion` accepts any string, as in ARM (only a non-string is `InvalidContentVersion`).
- Parameter, output and function type names are accepted in any case (`secureString`, `SecureObject`), in the template
  schema too.
- Template functions follow ARM more closely:
  - `deployment()` returns ARM's shape, `{ name, properties: { templateHash, parameters, mode } }`; `parameters`
    lists the values supplied by parameter files and `-p` (never secure ones). The flat `templateHash`, `tenantId` and
    `mode` keys remain as aliases.
  - `tenant()` returns `{ countryCode, displayName, id, tenantId, defaultDomain }` (`id` is `/tenants/<tenantId>`;
    the preflight now also reads the organization's `countryLetterCode`) and, like `environment()`, is allowed in
    variables and parameter defaults.
  - `environment()` adds `portal`, `resourceManager`, `authentication.audiences`, `authentication.tenant`,
    `authentication.identityProvider` and `suffixes.keyvaultDns`; `graph` stays the Microsoft Graph endpoint.
  - `guid()` uses ARM's namespace and `uniqueString()` ARM's hash, checked against values Azure Resource Manager
    produced (see Breaking changes and Migration).
  - String comparisons, `and`/`or`, `array`, `union`/`intersection` and `range` as listed under Breaking changes.
  - `padLeft()` accepts any total length up to the 1 MiB string limit (it was capped at 16).
- What-if change types follow ARM: a resource whose condition is false is no longer an `Ignore` row (ARM's `Ignore` means "exists but not
  managed by this template"); it is left out of the changes, listed in the JSON's new `skipped[]` / `summary.skipped` and, in the text, only
  with `--verbose`. With `--result-format ResourceIdOnly` an existing declared resource is reported as `Deploy` (`!`) instead of
  `Modify` / `NoChange`, as ARM does (a languageVersion 2.0 `"existing": true` resource stays `NoChange`: it is never deployed). The JSON
  summary gains `deploy` and `skipped`.
- `-p` takes several values per occurrence, as the Azure CLI does (it took one value per `-p`).
- `--rollback-on-error` is not offered, by decision: EARM has no deployment history to roll back to, and an automatic undo of a failed run cannot
  restore secrets, replaced object ids or Graph writes such as PIM requests and consent grants. Deploy the last known-good template again instead
  (see `docs/cli.md`).

- A `reference()` whose arguments use a lambda variable is refused with a message that points to property and output copy
  loops (ARM does not support lambda variables inside `reference()`); a `reference()` inside a lambda that uses no lambda
  variable still resolves.
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
- `what-if` and the planning pass of `create` no longer re-read a display-name-keyed object (or a PIM schedule request)
  that the first key read did not find: a new object of these types no longer costs 3 extra reads and 12 s of waiting
  per batch in `what-if`. The executor of `create` still confirms the absence with every extra read right before each
  `POST`, so an object a flapping list omitted is adopted, never created twice; `deployment delete` keeps the
  confirmation in its planner, because it deletes what that plan finds.
- The extra reads that confirm an absence log at Debug instead of Information (one line per object per read). One
  Information line summarizes them instead: per planner batch (`Batch 0: confirmed 4 object(s) absent after 3 extra
  read(s) (12 s)`, event 2084) and per executor check (`Confirmed ... absent after 3 extra read(s) (12 s)`, event
  2085), only when extra reads ran. An object found on an extra read is still logged as a warning (event 2083).
- `--allow-ca-enforce` decides whether a Conditional Access policy's declared `state: enabled` is applied, instead of
  gating the template. Before, a policy declaring `state: enabled` without the flag was refused: an unsupported what-if
  row and a failed `create` (`ConditionalAccessPolicyRejected`, nothing written). Now the enforcement is held back at plan
  time with a `ConditionalAccessEnforcementNotAllowed` warning: a policy that does not exist yet is created report-only
  (`enabledForReportingButNotEnforced`), and an existing policy keeps its live `state` (`state` is left out of the PATCH;
  the other declared properties still update). What-if shows the held-back `state` delta with a note (a `NoEffect` delta
  with a `note` in the JSON, also on a `NoChange` row), and a policy whose only difference is the held-back state is
  `NoChange`, so `--exit-code-on-changes` does not fire for it. The executor plans again against a fresh read, so the
  rule holds on the live state at apply time. With the flag the declared state is applied as written; declared
  `disabled` / report-only and an undeclared `state` (report-only on create, untouched afterwards) are unchanged.

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

- A resource whose whole `properties` is one expression (`"properties": "[variables('groups')[copyIndex()].properties]"`)
  was planned and deployed with `properties: {}` ("required property displayName missing from 'properties'"): only an
  object literal was evaluated. The expression is now evaluated for each instance at expansion, and the instance declares
  the keys of its value, relationship properties included.
- Adopting an existing group by display name no longer fails with "Immutable property properties.isAssignableToRole ...
  differs" when the template declares `isAssignableToRole: false`: Graph reads `null` back for a group created without it,
  which is now read as `false`.
- The what-if of an Incremental deployment no longer warns `OrphanReportIncomplete` for the eligibilities and assignments
  of a principal the same run creates: nothing live can match them yet, and the other declared principals are listed.
- A large template no longer fails `what-if`, `create`, `delete` or `validate --validation-level Provider` with
  `EvaluationBudgetExceeded` ("more than 5,000,000 value elements in total") while plain `validate` passes. Reading part of
  a variable (`variables('x')[copyIndex()].name`) copied and counted the whole variable on every read, so copy loops over
  a large variable used up the run's budget once the online path evaluated the template a few times. Such reads now walk
  the evaluated variable and copy only what they select, and a variable read by resources planned in parallel is evaluated
  once instead of once per thread. The budget itself is unchanged.
- A `copy` block in `variables` is read as ARM variable iteration (see Added) instead of a variable named `copy`;
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
