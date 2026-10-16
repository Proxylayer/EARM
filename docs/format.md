# Template format (schema version 2026-10-01)

An EARM template is a JSON document that looks and behaves like a classic Azure Resource Manager template, but
its resources are Microsoft Entra objects under the `Microsoft.Entra` provider namespace. This page is the
reference for the format; [edges.md](edges.md) covers relationship properties, [permissions.md](permissions.md) the
Graph permissions a template needs, [cli.md](cli.md) the verbs, and [diagnostics.md](diagnostics.md) every
validation code. Worked templates are in [`samples/`](../samples/README.md).

- File convention: `*.entra.json` (or `*.entra.jsonc`). The parser accepts `//` and `/* */` comments, trailing commas and
  a UTF-8 byte-order mark.
- Editor support: `earm schema install` maps the JSON Schemas to `**/*.entra.json` in VS Code
  (see [JSON Schemas](#json-schemas)).
- Validate offline at any time: `earm deployment validate -f template.entra.json -p '@params.json'`.

## Envelope

```json
{
  "$schema": "https://schema.earm.proxylayer.net/schemas/2026-10-01/deploymentTemplate.json#",
  "contentVersion": "1.0.0.0",
  "metadata": { "description": "free-form" },
  "parameters": { },
  "variables": { },
  "functions": [ ],
  "resources": [ ],
  "outputs": { }
}
```

| Key | Required | Notes |
|---|---|---|
| `$schema` | yes | Exactly `https://schema.earm.proxylayer.net/schemas/2026-10-01/deploymentTemplate.json#` (the trailing `#` is optional); `UnrecognizedSchema` otherwise. |
| `contentVersion` | yes | A literal `major.minor.build.revision`, for example `1.0.0.0`. |
| `metadata` | no | Free-form. |
| `parameters` | no | Inputs: `type`, `defaultValue`, `allowedValues`, `minValue`/`maxValue`, `minLength`/`maxLength`, `metadata.description`. See [Parameters](#parameters-and-parameter-files). |
| `variables` | no | Named values computed lazily from parameters and other variables; memoised. |
| `functions` | no | User-defined functions: `[{ "namespace": "c", "members": { "slug": { "parameters": [...], "output": { "type": "string", "value": "[...]" } } } }]`, called as `c.slug(...)`. They may not call `reference()` or other user functions. |
| `resources` | yes | An **array** of resources. |
| `outputs` | no | `{ "name": { "type", "value", "condition" } }`, evaluated after everything is applied. |

There is no `languageVersion`, no symbolic names, no `existing` keyword and no deployment-settings block: deployment
knobs are command-line flags. Unknown top-level keys are a warning (`UnknownTopLevelProperty`) and are ignored.

## Resources

```json
{
  "type": "Microsoft.Entra/applications",
  "apiVersion": "2026-10-01-preview",
  "name": "[variables('apiName')]",
  "condition": "[equals(parameters('env'), 'prod')]",
  "copy": { "name": "apps", "count": 2, "mode": "parallel", "batchSize": 1 },
  "dependsOn": ["[resourceId('Microsoft.Entra/users', 'bg-orders@contoso.com')]"],
  "comments": "free text",
  "metadata": { },
  "properties": { },
  "resources": [ ]
}
```

| Key | Required | Rule |
|---|---|---|
| `type` | yes | A type of the [catalog](#resource-type-catalog); matched case-insensitively. Unknown type: `UnknownResourceType`. |
| `apiVersion` | yes | A **literal**. `2026-10-01-preview` is the only normal version and maps to Microsoft Graph `v1.0`; `2026-10-01-preview.beta` maps to `beta` (passthrough, no normalisation guarantees). The format is in preview. The plain `2026-10-01` label was renamed `2026-10-01-preview` and is an `UnknownApiVersion` error with a rename hint; anything else is an `UnknownApiVersion` error too. |
| `name` | yes | The ARM-style key of the resource (see below). May be an expression, but must be computable before deployment from literals, parameters and variables (or be the same deploy-time computation as another resource: a symbolic name). |
| `properties` | no | The Graph properties to apply, plus the relationship properties of [edges.md](edges.md). An expression string for the whole object is accepted. |
| `dependsOn` | no | Resource ids, `type/name`, unique bare names or copy-loop names. Implicit dependencies come from `reference()` and `resourceId()` of template resources (outside relationship properties) and from nesting. |
| `condition` | no | Boolean or expression; `false` makes the instance an `Ignore` row. |
| `copy` | no | `name` (literal), `count`, `mode` (`serial`/`parallel`), `batchSize`; `copyIndex([name][, offset])` inside the resource. Property-level `copy` is not supported. |
| `resources` | no | Nested child resources (short `type`, short `name`, same `apiVersion` rule); two levels (parent and child) only. |
| `comments`, `metadata` | no | Ignored. |

### Names and resource ids

`name` is the ARM-style key of the resource. The provider injects it into the Graph property that identifies the object;
declaring that Graph property in `properties` too is an error unless the values are equal
(`NameMappedPropertyConflict`). EARM keeps no state: every run finds the object again **by this key in the live tenant**, as ARM finds a
resource by type and name, so changing the `name` names a different object (a rename is a new object; the old one stays). The full key
table and its consequences are in [engine.md](engine.md#identity-without-state).

| Name kind | Types | Meaning |
|---|---|---|
| `uniqueName` | applications, groups | Graph `uniqueName` alternate key; upsert is `PATCH /x(uniqueName='…')` with `Prefer: create-if-missing`. Immutable. An existing object without a `uniqueName` is not found by name (an application declared with its `appId` is found by that and gets the name set, `UniqueNameNotSettable` when Graph refuses). |
| `userPrincipalName` | users | `local@domain`. |
| `appId` | servicePrincipals | The application (client) id GUID, or `[reference('app').appId]`. |
| child name | federatedIdentityCredentials, passwordCredentials | `parent/child`; the child is the credential's own name. Nested children get the parent's name prepended by the loader. |
| `displayName` | named locations, CA policies, authentication strengths, administrative units, role definitions | Looked up by `displayName` on every run (0 hits: create, 1: that object, more than one: `AmbiguousMatch`; the display name must be unique in the tenant). A display name changed outside the template is no longer found: the next run creates a second object. |
| `id` | authenticationContextClassReferences, authenticationMethodConfigurations | The name is the Graph id: `c1` … `c99` for an authentication context; the method id for an authentication method configuration (`fido2`, `temporaryAccessPass`, `x509Certificate`, `microsoftAuthenticator`, `sms`, `voice`, `email`, `softwareOath`; case-insensitive, Graph returns `Fido2`). |
| GUID | appRoleAssignments, oauth2PermissionGrants, roleAssignments, roleEligibilityScheduleRequests, privilegedAccess/groupEligibilityScheduleRequests | A deterministic GUID chosen by the author with `guid(...)`, as with `Microsoft.Authorization/roleAssignments`. The `name` is a **label** (it gives the resource its id in the what-if and the result); the Graph object is found by its composite properties on every run. A changed composite is a **different assignment**: it is created, and the old one is retained and reported (`orphan: retained`), never removed (see [engine.md](engine.md#retained-assignments)). Only a change of a mutable property (a grant's `scope`) updates in place. |

Every name segment, of every type, must be printable single-line text without leading or trailing whitespace and must not consist of dots only
(`.`, `..`); `namePattern`, `nameMaxLength` and `nameKind` are checked for literal names offline and for names an expression computes (offline once the value
is known, and again for each `copy` iteration). `namePattern` is matched against the whole segment: `^` and `$` mean the start and the very end of
the text, so a name with a trailing line feed does not match. `nameMaxLength` caps a name Graph limits more tightly: an authentication strength's
name (its displayName) is at most 30 characters (Graph: "Policy DisplayName length cannot be longer than 30 characters"; Learn documents no
limit for named locations or Conditional Access policies, so none is enforced for them). A property whose expression evaluates, offline, to a value of the wrong JSON type for
the type's schema is `PropertyTypeMismatch`.

The resource id is `/providers/Microsoft.Entra/<type>/<name>[/<childType>/<childName>]` (tenant scope, ARM style):
deterministic, and what the what-if output and the deployment result print. `resourceId('Microsoft.Entra/applications/passwordCredentials', 'app', 'ci-secret')` builds it.

### Differences from ARM

| ARM | EARM |
|---|---|
| `location`, `sku`, `kind`, top-level `tags`, `identity`, `scope`, resource group and subscription | Not present: Entra objects have none of them (Graph `tags` live inside `properties`). `resourceGroup()`, `subscription()`, `managementGroup()`, `managementGroupResourceId()`, `extensionResourceId()`, `subscriptionResourceId()`, `tenantResourceId()`, `listKeys()`, `listSecrets()`, `providers()` and `pickZones()` are errors (`UnsupportedArmFunction`) with a pointer to the alternative. |
| Deployment history (`az deployment group show / list`, `operation list`); a resource is identified by type and name | **No state and no history**: nothing is stored, locally or in the tenant. Identity is the resource's type and `name`, resolved by a live read on every run, which is the ARM model; `name` maps to a Graph key ([key table](engine.md#identity-without-state)). For display-name-keyed types the key is not unique in Graph: duplicates are `AmbiguousMatch`. A rename is a new object, as in ARM. There is no lock, so concurrent runs of one template are not serialised. |
| PUT replaces the resource | **PATCH asymmetry.** Graph is merge-PATCH at the top level: only the **declared** top-level properties are applied, omitted ones are untouched. A declared nested object is replaced whole (live-only sub-properties are sent as explicit `null`, unless Graph refuses that), and a declared array is written whole. |
| Complete mode deletes resources of the resource group the template does not declare | Complete mode **only reports** the undeclared live objects of the declared types (bounded per type; applications, groups and users are never listed) and **never deletes**. Remove objects with `deployment delete -f` (the objects a template declares) or by hand. |
| A changed immutable property fails the deployment (most resource providers) | Applications, groups and users fail (`ImmutablePropertyChanged`); the cheap types (assignments, credentials, named location kinds) are **replaced** (delete, then create), which needs confirmation (`-y` or the prompt; a non-interactive run without `-y` fails before any write). |
| Changing a role assignment's properties under the same name is an error | The assignment `name` is a label; the tuple is the identity. A changed tuple creates a new assignment and **keeps the old one**, which still grants access: it is reported (`orphan: retained`), never removed. |
| Every PUT sends the secrets of the resource | Secrets are **write-once** on an existing object: a `secretText` / `passwordProfile.password` is sent at creation and never compared or re-sent (Graph cannot return it, so a rerun would otherwise always change). Rotate client secrets with `rotationTag`. |
| Child relationships are separate resources with their own lifecycle | Relationship arrays are declarable from either side ([edges.md](edges.md)): target-side lists (`members`, `owners`) are authoritative and prune; principal-side lists (`memberOf`, `appRoleAssignments`, `directoryRoles`, ...) only add, never remove. |
| Resources start as soon as their own dependencies finish; a failure lets independent resources finish | **Level-batched**: the dependency graph is cut into levels and each level runs as a batch (`--parallelism` inside it). By default a failure **stops at the batch boundary** (fail-fast; later batches are skipped); `--continue-on-error` keeps independent branches going. |
| `--rollback-on-error` | Not available (planned). A failed run leaves what it applied; a rerun converges. |
| Type names such as `Microsoft.Network/virtualNetworks/subnets` are parent/child | `conditionalAccess/policies`, `conditionalAccess/namedLocations`, `conditionalAccess/authenticationStrengths` and `conditionalAccess/authenticationContextClassReferences` are **pseudo-child** type names: top-level resources with a one-segment `name`, not children of a `conditionalAccess` resource. |
| `apiVersion` is the resource provider's REST version | `apiVersion` is the format version: `2026-10-01-preview` (Graph v1.0) or `2026-10-01-preview.beta` (Graph beta, passthrough, no normalisation guarantees). |
| `reference()` and `list*()` may not be used in `name` or `dependsOn` | Allowed: a name such as `[reference('app').appId]` is a **symbolic name** resolved before its batch ([engine.md](engine.md#symbolic-names)). |
| Secure outputs are never returned | `securestring` / `secureObject` outputs are `***` unless `--reveal-secure-outputs`, or written to an owner-only file with `--secure-outputs-file`. |
| `az deployment` exit codes (0 success, 1 error, 2 command-line parse error, 3 resource not found) | 0 success, 1 failure (usage errors included), 2 validation, 3 authentication / preflight, 5 `--exit-code-on-changes`; 4 is retired ([cli.md](cli.md#exit-codes)). |
| Implicit parent dependency of a child must be explicit | Inferred for nested children (a superset of the ARM rule). |
| `existing` resources; `reference(id, apiVersion)` reads any resource | `reference(resourceId('Microsoft.Entra/…', 'name'), '2026-10-01-preview')` (the form **with** an `apiVersion`) always means an object that is **not** in the template: no dependency, an error when not found. Reference a template resource without the `apiVersion`. |
| Relationships live in separate resources | Arrays inside `properties`, declarable from either side; see [edges.md](edges.md). |
| Nested and linked deployments (`Microsoft.Resources/deployments`, `templateLink`) | Not supported: a template is deployed on its own. Split the work into several templates and pass values between them through parameter files. |
| `copy` in `variables` (variable iteration) and property-level `copy` | Not supported. A `copy` block in `variables` is an `InvalidCopy` error (it is never read as a variable named `copy`); build arrays with `map(range(0, n), lambda('i', ...))` instead. |
| `uniqueString()` and `guid()` | Deterministic and stable in EARM, but **not** the same values ARM computes for the same inputs (different hash). Do not rely on them to reproduce names or ids created by an ARM or Bicep deployment. |
| `reference(resourceIdOrName, apiVersion, 'Full')` | `reference()` takes at most two arguments; there is no `'Full'` form (the result is always the Graph object itself). |
| Other ARM functions | Not available (an unknown-function error): `references`, `deployer`, `dataUri`, `dataUriToString`, `parseCidr`, `cidrSubnet`, `cidrHost`, `listAccountSas` and the other `list*` functions. |
| Bicep-only operators | Expressions are ARM JSON syntax: the Bicep safe-dereference `.?` and non-null assertion `!` operators do not exist. Use `tryGet(obj, 'key')`, `tryIndexFromEnd(array, n)` or `coalesce()`. |
| Parameter constraints | As in ARM, only `defaultValue` may be an expression; an expression in `minValue`, `maxValue`, `minLength` or `maxLength` is an `InvalidParameterConstraint` error. An expression default is checked against the type and the constraints once it is evaluated. For an `array` parameter, `allowedValues` lists the values its **elements** may take (every element must be one of them). |

## Resource type catalog

`Microsoft.Entra/…` types of schema version 2026-10-01. The table follows the `x-earm` overlay at the end of each
schema file (`earm schema print <type>`), which the executor loads at runtime. A provider can adjust its overlay (groups require only
`displayName` and filter `groupTypes` / `visibility` from the immutable list), so the table also notes those adjustments.

| Type | `name` is | Graph path | Immutable (error, or recreate for cheap types) | Edge properties | Required on create |
|---|---|---|---|---|---|
| `applications` | `uniqueName` | `/applications` | `uniqueName`, `appId`, `id`, `publisherDomain` | `owners` | `displayName` (`signInAudience` defaults to `AzureADMyOrg`) |
| `applications/federatedIdentityCredentials` | credential `name` (parent: application) | `/applications/{id}/federatedIdentityCredentials` | `name` | none | `issuer`, `subject` (`audiences` defaults to `["api://AzureADTokenExchange"]`); at most 20 per application, and `issuer` + `subject` must be unique within the application |
| `applications/passwordCredentials` | credential `displayName` (parent: application) | `addPassword` / `removePassword` | everything (replaced, never edited) | none | nothing (`endDateTime` defaults to +180 days) |
| `servicePrincipals` | `appId` | `/servicePrincipals` | `appId`, `servicePrincipalType` | `owners`, `memberOf`, `ownerOf`, `appRoleAssignments`, `directoryRoles` | nothing (`accountEnabled` defaults to true, `appRoleAssignmentRequired` to false) |
| `groups` | `uniqueName` | `/groups` | `uniqueName`, `mailEnabled`, `isAssignableToRole`, `groupTypes` (Unified presence), `visibility` (HiddenMembership; `mailNickname`, Public / Private visibility and DynamicMembership are updated in place) | `members`, `owners`, `memberOf`, `administrativeUnits`, `appRoleAssignments`, `directoryRoles`, `assignedLicenses` | `displayName` (`mailEnabled` and `securityEnabled` default per kind, `mailNickname` is derived from the name) |
| `users` | `userPrincipalName` | `/users` | `userPrincipalName` | `manager`, `memberOf`, `ownerOf`, `administrativeUnits`, `appRoleAssignments`, `directoryRoles`, `assignedLicenses` | `displayName` (`mailNickname`, `accountEnabled`, `passwordProfile` defaulted) |
| `roleDefinitions` | `displayName` (custom roles only) | `/roleManagement/directory/roleDefinitions` | `templateId` | none | `rolePermissions[].allowedResourceActions` (`isEnabled` defaults to true) |
| `conditionalAccess/authenticationContextClassReferences` | the id, `c1`…`c99` | `/identity/conditionalAccess/authenticationContextClassReferences` | `id` | none | `displayName` (`isAvailable` defaults to true) |
| `conditionalAccess/authenticationStrengths` | `displayName` (custom only) | `/policies/authenticationStrengthPolicies` | `policyType` | none | `allowedCombinations` |
| `appRoleAssignments` | deterministic GUID | `/servicePrincipals/{resourceId}/appRoleAssignedTo` | everything; composite key (`principalId`, `resourceId`, `appRoleId`) | none | the three key properties |
| `oauth2PermissionGrants` | deterministic GUID | `/oauth2PermissionGrants` | key fields (`clientId`, `resourceId`, `consentType`, `principalId`); only `scope` is mutable | none | key + `scope` |
| `conditionalAccess/namedLocations` | `displayName` | `/identity/conditionalAccess/namedLocations` | `@odata.type` | none | `@odata.type` (Graph also requires `ipRanges` or `countriesAndRegions`, per kind) |
| `conditionalAccess/policies` | `displayName` | `/identity/conditionalAccess/policies` | none | none | `conditions.users`, `conditions.applications`, `grantControls` or `sessionControls` (`state` defaults to `enabledForReportingButNotEnforced`, `conditions.clientAppTypes` to `["all"]`, `grantControls.operator` to `OR`) |
| `administrativeUnits` | `displayName` | `/directory/administrativeUnits` | `isMemberManagementRestricted` | `members` | nothing |
| `roleAssignments` | deterministic GUID | `/roleManagement/directory/roleAssignments` | everything; composite key (`roleDefinitionId`, `principalId`, `directoryScopeId`) | none | the three key properties (`directoryScopeId` defaults to `/`) |
| `roleEligibilityScheduleRequests` | deterministic GUID (a label) | `POST /roleManagement/directory/roleEligibilityScheduleRequests` (adminAssign / adminUpdate / adminRemove); read from `/roleManagement/directory/roleEligibilitySchedules` | the key (`principalId`, `roleDefinitionId`, `directoryScopeId` or `appScopeId`): a changed key is a new eligibility and the old one is retained; `scheduleInfo` changes in place | none | `principalId` and `roleDefinitionId` or `role`. ARM names (`requestType` optional, only `AdminAssign`; `condition`, `conditionVersion` and `target*ScheduleId` refused); sugar `role`, `administrativeUnit`; the scope defaults to `/`, `scheduleInfo` to NoExpiration, `justification` to `Deployed by earm`. See [providers.md](providers.md#worked-example-pim-eligibility-for-directory-roles). |
| `privilegedAccess/groupEligibilityScheduleRequests` | deterministic GUID (a label) | `POST /identityGovernance/privilegedAccess/group/eligibilityScheduleRequests` (adminAssign / adminUpdate / adminRemove); read from `/identityGovernance/privilegedAccess/group/eligibilitySchedules` | the key (`groupId`, `principalId`, `accessId`): a changed key is a new eligibility and the old one is retained; `scheduleInfo` changes in place | none | `groupId` or `group`, `principalId` and `accessId` (`member` or `owner`). Sugar `group` (the group's uniqueName or id); `requestType` optional, only `AdminAssign`; `scheduleInfo` defaults to NoExpiration (a group's PIM policy may forbid it), `justification` to `Deployed by earm`. The first request onboards the group in PIM for Groups permanently; dynamic and on-premises synced groups are refused. See [providers.md](providers.md#worked-example-pim-for-groups-eligibility). |
| `authenticationMethodConfigurations` | the method id (`fido2`, `temporaryAccessPass`, `x509Certificate`, `microsoftAuthenticator`, `sms`, `voice`, `email`, `softwareOath`; case-insensitive) | `/policies/authenticationMethodsPolicy/authenticationMethodConfigurations` | none (`@odata.type` is derived from the name) | none | never created: a tenant singleton that must exist (a missing one is an error); delete resets fido2 / temporaryAccessPass / x509Certificate to the Microsoft default and keeps the others (`NotDeletable`). See [providers.md](providers.md#worked-example-authentication-method-configurations). |

An immutable change is a hard error (`ImmutablePropertyChanged`) for every type that is not recreated: applications, service principals, groups,
users, custom roles, authentication strengths, administrative units...; for the cheap types (assignments, secrets, named location kinds) it is
`Modify` with `mode: recreate`. Federated credentials and grants are never recreated: their immutable properties are their key, so a change is a
different object (a renamed credential is a new one; a grant with a changed key is created and the old one is retained). Soft-deleted objects (applications,
Microsoft 365 groups, users, service principals, administrative units) keep their keys for 30 days: a create that finds one
fails with a hint unless `--on-soft-deleted-conflict restore`. The probe that finds them needs the type's read permission; an identity without it (for example an
`Application.ReadWrite.OwnedBy`-only service principal creating applications) gets the warning `SoftDeleteProbeSkipped` and the create proceeds without the check.

### Property vocabulary

`properties` is the Graph representation of the object (camelCase, as Graph returns it) plus a few EARM extensions.
`earm schema print <type>` shows the vocabulary of one type; the JSON Schemas are the reference for names, types and
enumerations. In short:

- **Nested objects** (`web`, `api`, `spa`, `publicClient`, `conditions`, `grantControls`, `sessionControls`, `passwordProfile`) are
  compared whole and written whole; a sub-property that exists live but is absent from the template is sent as explicit
  `null`, or `[]` when it is a collection (Graph refuses `null` for collections such as `redirectUris`), except where the
  overlay says Graph refuses clearing it at all (`nullable`: applications' `web.implicitGrantSettings` and
  `web.redirectUriSettings`).
- **Arrays of scalars** (`identifierUris`, `tags`, `redirectUris`, `builtInControls`, `members`) are compared as sets and written whole.
- **Arrays of objects** are compared by a member key (`id` for `appRoles` and `oauth2PermissionScopes`, `cidrAddress` for `ipRanges`)
  and written whole. An omitted app role or scope `id` is derived deterministically (UUIDv5 of `uniqueName` and `value`), so every run computes the same id.
- **Passthrough**: every object keeps `additionalProperties: true`. A property the schema does not list is still sent to
  Graph unchanged; it is simply not validated or documented.
- **EARM-only properties** (never sent to Graph): `rotationTag` and `rotateBeforeDays` on `passwordCredentials` (see below),
  and the sugar properties that are rewritten before the key is computed: `scopes` on `oauth2PermissionGrants` (→ `scope`),
  `resourceAppId` / `appRole` on `appRoleAssignments` and in `appRoleAssignments` edge entries (→ `resourceId` / `appRoleId`),
  `role` / `administrativeUnit` on `roleAssignments` and in `directoryRoles` edge entries (→ `roleDefinitionId` / `directoryScopeId`), and
  `skuPartNumber` in `assignedLicenses` (→ `skuId`).
- **Secrets**: `passwordProfile.password` (users) and `secretText` (password credentials) are secure leaves: tainted like a
  `securestring` parameter, `***` in every rendering.

#### Password credential rotation

Graph never edits a client secret, and returns its text once. `applications/passwordCredentials` therefore:

- names the credential `<name>#<rotationTag>` in Graph (`displayName`) when `rotationTag` is declared, else `<name>`; the name and the
  tag may not contain `#`, and the whole `displayName` is capped at 256 characters (rejected offline);
- matches the credential among the application's secrets by that base name (with or without a tag), preferring the one whose tag
  equals the declared one;
- is `NoChange` unless `rotationTag` differs from the tag in the live `displayName` (no local state is involved), a declared `endDateTime` / `startDateTime` differs, the
  secret expired, or fewer than `rotateBeforeDays` (default 30) remain; then it **adds the new secret first, then removes the old one**;
- publishes `secretText` on the creating or rotating run only (`reference('…/passwordCredentials/ci-secret').secretText` flowing into a
  `securestring` output); a rerun reports that output as `null` with the `SecretNotAvailableOnRerun` warning and does not fail.
  Change `rotationTag` to rotate and obtain a new secret.
- treats a credential created by an older EARM (named `<name>`, no tag) as matching a declared tag, with the `RotationTagNotStamped`
  warning, so upgrading never rotates by surprise; its next rotation writes the tag into the name. Until then a changed `rotationTag`
  is not detected: declare a different `endDateTime` to rotate it.

## Expressions

A string of the form `[ ... ]` is an expression; `[[` escapes a literal bracket and `''` escapes a quote. Expressions are at
most 24,576 characters, nest at most 100 levels deep (calls, indexers, `lambda()` bodies and chained `.property` / `[index]`
accesses counted together; `ExpressionTooDeep` beyond that) and function names are case-insensitive. They may appear in any string value (property values, names,
conditions, dependsOn entries, outputs).

| Group | Functions |
|---|---|
| String | `concat format toLower toUpper substring replace split join trim startsWith endsWith contains length empty indexOf lastIndexOf padLeft string base64 base64ToString uri uriComponent uriComponentToString guid uniqueString newGuid utcNow dateTimeAdd dateTimeFromEpoch dateTimeToEpoch` |
| Logic | `if and or not equals less lessOrEquals greater greaterOrEquals coalesce bool true false` |
| Numeric | `add sub mul div mod min max int float` |
| Array and object | `array createArray createObject first last take skip union intersection range items json null objectKeys flatten distinct shallowMerge tryGet indexFromEnd tryIndexFromEnd` and the lambda functions `map mapValues filter reduce sort toObject groupBy lambda lambdaVariables` |
| Resource and deployment | `resourceId reference copyIndex deployment tenant environment parameters variables` and user functions `namespace.fn(...)` |
| **Entra** | `wellKnownApp(name)`, `appRole(app, value)`, `permissionScope(app, value)`, `roleTemplate(name)` |

ARM comparison rules apply: `contains()` on a string is case-sensitive (on an object it matches keys case-insensitively), while
`startsWith`, `endsWith`, `indexOf` and `lastIndexOf` on strings ignore case. `union()` of objects merges nested objects recursively
(arrays are replaced, the last one wins); `shallowMerge()` replaces nested objects whole. `tryGet()` and `tryIndexFromEnd()` return
`null` for a missing key or an out-of-range index; `indexFromEnd(array, 1)` is the last element.

The Entra functions:

- `wellKnownApp('MicrosoftGraph')` returns the application id of a resource application: `MicrosoftGraph`, `ExchangeOnline`,
  `SharePointOnline`, `AzureResourceManager` (alias `AzureServiceManagement`), `AzureKeyVault`, `AzureStorage`, `AzureDevOps`. Pure; usable anywhere.
- `appRole(appIdOrName, 'Value')` and `permissionScope(appIdOrName, 'Value')` return the id of an application permission (`Role`) or a
  delegated scope (`Scope`) of a resource: first from the live object of a template application or service principal, then from the
  embedded Microsoft Graph tables (no request), then from the tenant. The first argument is an application id GUID or the name of a
  template resource (an implicit dependency).
- `roleTemplate('Global Reader')` returns the role template id of a built-in directory role (GUIDs pass through; custom roles are
  resolved by display name from the tenant, or referenced as `reference('Custom role name').id`).
- `tenant()` returns `{ tenantId, defaultDomain, displayName }`, `environment()` returns `{ name, graph, authentication.loginEndpoint }`,
  `deployment()` returns `{ name, templateHash, tenantId, mode }`.

`appRole`, `permissionScope`, `roleTemplate` and `tenant` need the tenant, so they are legal in resource properties, names and
outputs but not in parameter defaults, variables or user functions (`FunctionNotAllowedHere`). `utcNow()` and `newGuid()` are legal
only as parameter defaults.

### `reference()` and `resourceId()`

- `reference('name')` or `reference(resourceId('Microsoft.Entra/type', 'name'))` returns the **live Graph object** of a template
  resource (`id`, `appId`, `uniqueName`, …) and adds an implicit dependency, except inside relationship properties, where it is
  evaluated in the edge pass after all objects exist and never creates a dependency (so mutual declarations are never cycles).
  In what-if, the object of a resource that does not exist yet is a `(deploy-time)` placeholder.
- `reference(resourceId(...), '2026-10-01-preview')` (with an `apiVersion`) looks up an object that is **not** in the template: no
  dependency, an error if it does not exist. Variables may do this; they may not reference template resources.
- A bare `reference('name')` needs the name to be unique in the template, else `AmbiguousReference`.
- A resource whose `name` is `[reference('app').appId]` gets a *symbolic id* during static evaluation, so everything that refers
  to it computes the same id before anything exists ([engine.md](engine.md#symbolic-names)).

### Evaluation phases and secure values

1. Parse. 2. Parameters: the `-p` values (parameter files and `name=value`) in command-line order, a later one wins; then `defaultValue` for a parameter no `-p` sets. 3. Variables and user functions (lazily, memoised).
4. Resource names and `dependsOn`, then the dependency graph. 5. Resources in topological batches, `condition` and `properties` just in time.
6. Outputs.

`securestring` / `secureObject` parameters, `secretText`, `passwordProfile.password` and anything computed from them are *tainted*:
they may flow into resource properties and `securestring` / `secureObject` outputs only (not into names, `condition`, `copy.count`,
`dependsOn` or plain outputs, which are printed in what-if, results and logs). They are `***` in what-if, logs and diagnostics; the plain text
exists only in Graph request bodies and in `--reveal-secure-outputs` / `--secure-outputs-file`.

### Evaluation limits

Templates are untrusted input, so what an expression may produce is bounded. The size limits are part of the format (the same template
passes or fails the same way everywhere); the work limits bound one run of a command. A violation is a diagnostic
([diagnostics.md](diagnostics.md#expressions-limits)), never a crash or an out-of-memory failure.

| Limit | Value | Diagnostic |
|---|---|---|
| JSON nesting of a template or parameter file | 64 levels (the reader's limit) | `InvalidJson` |
| Nesting of a **value** an expression builds (`createArray`, `createObject`, `array`, `union`, `items`, `map`, user function outputs, a wrapped parameter) | 64 levels. The JSON output of a command allows 256, so a 64-level value fits inside the structure around it. | `ValueTooDeep` |
| Nesting of one expression (calls, indexers, lambda bodies) | 100 levels | `ExpressionTooDeep` |
| Length of one expression | 24,576 characters | `ExpressionTooLong` |
| Length of one string a function produces | 1 MiB (1,048,576 characters) | `EvaluationBudgetExceeded` |
| Elements of one array or object | 100,000 | `EvaluationBudgetExceeded` |
| Nodes (containers and leaves) of one value | 1,000,000 | `EvaluationBudgetExceeded` |
| `copy.count` of one resource | 800 (the ARM limit) | `ExpansionLimitExceeded` |
| Resource instances of a template, all `copy` loops together | 5,000 | `ExpansionLimitExceeded` |
| Steps of a run (function calls plus lambda iterations) | 50,000,000 | `EvaluationBudgetExceeded` |
| Value nodes produced by a run / characters of text produced by a run | 5,000,000 / 134,217,728 (128 Mi) | `EvaluationBudgetExceeded` |
| Memory held by evaluated variables (string characters at 2 bytes each plus a fixed cost per node) | 256 MiB per run | `EvaluationBudgetExceeded` |
| Variables reading variables (a chain `v1` uses `v0`, `v2` uses `v1`, ...) | 128 levels | `VariableChainTooDeep` |
| `format()` | The format string is parsed by EARM: a specifier is at most 100 characters; a standard numeric specifier (`D N F X G P E`, either case) has a precision of at most 100; a string argument ignores its specifier (as in .NET). Padded items and arguments are added up against the 1 MiB string limit as they are written. | `InvalidFunctionArguments`, `EvaluationBudgetExceeded` |
| `indexOf`, `lastIndexOf`, `contains` on strings | Linear time (case-insensitive search of the folded text); the length of the text and the needle is charged to the step budget (1 step per 1,024 characters), as is every other string operation | `EvaluationBudgetExceeded` |
| Time one evaluation (a variable, the properties of a resource, an output) may take | 60 seconds; `--evaluation-timeout <seconds>` or `EARM_EVALUATION_TIMEOUT`, `0` = no clock. The clock runs from the start of the evaluation, so the time a deployment spends on the network between evaluations never counts; the step and node limits above bound the total work. | `EvaluationBudgetExceeded` |

A per-value limit fails that value (and what depends on it) and the run goes on, so one oversized variable does not hide the other findings.
A work limit ends the run: it stays tripped, and every later expression fails fast with the same message. `union` and `intersection` are linear
in the number of elements, so they stay within the limits for any array that is within them. An unknown (deploy-time) value's symbol, built
from the symbols of its arguments, is cut to 1,024 characters (the rest replaced by a hash) so `concat(x, x)` chains over an unknown cannot grow it without bound.

Repeated property names in an object (`"state": "disabled", "state": "enabled"`) are rejected when the file is loaded (`DuplicateJsonKey`):
JSON readers disagree about which value wins. **Names that differ only in letter case count as repeated** (`"state"` and `"State"`): EARM looks
properties up ignoring case and takes the first match, so a guard (the `--allow-ca-enforce` check on a conditional access policy, for one) would
judge one key and the request would carry both. The same holds for an object an expression builds (`union`, `createObject`, `items`, `toObject`):
it fails with `DuplicateJsonKey` naming both spellings when its value is used. The names under `parameters`, `variables` and `outputs` are the one
exception (case variants there are `DuplicateDefinition`).

### Input limits

A template file and a parameter file are each read only up to **8 MiB (8,388,608 bytes)**; a larger file is refused with `InputTooLarge`
(exit code 1) from its size, before it is read, and the read itself stops at the limit for a file that reports no size. A document may also hold
at most **2,000,000 JSON values** (objects, arrays and leaves); they are counted in one cheap pass over the text before anything is built, so a
file of millions of tiny nodes is refused without allocating its tree. Files must be UTF-8 (a byte-order mark is accepted); UTF-16 and UTF-32
are named as such instead of failing with a byte-level syntax error. A directory passed as the template file is reported as a directory.

## Parameters and parameter files

Parameter types: `string`, `securestring`, `int`, `bool`, `object`, `secureObject`, `array`. An `int` accepts any JSON number whose exact
value is a whole number in the 64-bit range, however it is spelled (`3`, `3.0`, `1.5e3`, `-0.0`); a fraction (`3.5`) or a number outside
the range is a `ParameterValueTypeMismatch`, and `copy.count` / `copy.batchSize` follow the same rule. A value is supplied by
`-p name=value` (inline; converted to the parameter type) or by a parameter file (`-p '@file.json'`, quoted in PowerShell); the `-p` values are applied in
command-line order and a later one wins, whichever form it has (as with the Azure CLI: put the file first, then override with `name=value`). A parameter
no `-p` sets falls back to its `defaultValue`. An unknown parameter name is an error (`UnknownParameter`); a value of the wrong type or outside `allowedValues` is
reported (`ParameterValueTypeMismatch`, `ParameterValueNotAllowed`). A parameter without a value is a warning in `deployment validate`
and an error for `create` and `what-if`.

```json
{
  "$schema": "https://schema.earm.proxylayer.net/schemas/2026-10-01/deploymentParameters.json#",
  "contentVersion": "1.0.0.0",
  "parameters": {
    "env": { "value": "prod" },
    "initialPassword": {
      "reference": {
        "keyVault": { "id": "/subscriptions/…/resourceGroups/rg/providers/Microsoft.KeyVault/vaults/contoso-kv" },
        "secretName": "bg-initial"
      }
    }
  }
}
```

### Key Vault references

An entry with `reference` instead of `value` is a Key Vault secret. For `create` and `what-if` the CLI resolves it after the
preflight with an HTTPS read of the vault (`GET https://<vault>.vault.azure.net/secrets/<secretName>[/<version>]?api-version=7.4`, using a Key Vault token of the
signed-in identity; the host suffix follows `EARM_CLOUD`); the vault name is the last segment of `keyVault.id` (a bare name is accepted) and must follow the Key Vault
naming rules (3-24 letters, digits and hyphens), as must the secret name (1-127 letters, digits and hyphens) and a version (32 hex characters), or the reference fails before any request. `secretVersion` is optional and must be a string
(absent, `null` or blank means the latest version); a number or any other non-string is an `InvalidParameterFile` error with the
path and position, never silently replaced by the latest version. The value is **always secure
(tainted)**, whatever the declared parameter type, and it must satisfy the constraints of the parameter (`allowedValues`, `minValue` / `maxValue`, `minLength` / `maxLength`) like a supplied value; the error names the parameter and the constraint, never the value. `EARM_KEYVAULT_ALLOWED` restricts the vaults a parameter file may read ([cli.md](cli.md)). The signed-in identity needs read access to the secret (`Key Vault
Secrets User` or the `get` secret permission of an access policy); that is an Azure permission, not a Graph one
([permissions.md](permissions.md)). `deployment validate` stays offline and does not resolve references; a failed resolution is
`KeyVaultReferenceUnresolved` (exit 3).

## Outputs

```json
"outputs": {
  "appId":     { "type": "string",       "value": "[reference(variables('appName')).appId]" },
  "ciSecret":  { "type": "securestring", "value": "[reference(resourceId('Microsoft.Entra/applications/passwordCredentials', variables('appName'), 'ci-secret')).secretText]" }
}
```

Outputs are evaluated after the edge pass. A `securestring` / `secureObject` output (or any tainted value) prints as `***` unless
`--reveal-secure-outputs`; `--secure-outputs-file <path>` writes the revealed values to a file with owner-only permissions.

## Security considerations

A template is code that runs with the credentials of the person or pipeline that runs `earm`: treat an untrusted template like an untrusted script.

- **Secrets and untrusted templates.** Secure values are masked everywhere EARM prints them, and a secure value cannot reach a name, a `condition`, a
  `copy.count`, `dependsOn` or a plain output. That includes values that depend on a secret only through a decision: an expression such as
  `filter(createArray(1), lambda('x', equals(substring(parameters('pw'), 0, 1), 'S')))` is empty or not depending on the first character. The
  elements a secret-dependent `filter`, `sort` or `if` lets through are secure, and a container or `null` that comes out *empty* because of a
  secret (a `filter` verdict, a `take` / `skip` / `range` count, an `if` condition) carries the mark itself (a tainted empty): it cannot reach a
  plain output, is written as `***` in what-if and logs and is a plain `[]` / `{}` / `null` only in a Graph request body. Other channels of an
  expression language (timing, the tenant objects a template creates or names, error messages of a template you control) cannot all be closed. **Do not supply secrets (secure parameters, Key Vault references) to `validate`, `what-if`
  or `create` runs of templates you do not trust**, for example the pull-request validation of a repository that accepts outside contributions;
  run those with no secrets, or only with throw-away values.
- **Log commands.** Everything EARM prints goes through a sanitizer: control characters are written as visible escapes (`\u001b`), and text a CI
  log parser would run as a command is broken (`##vso[` and `##[` anywhere in a line become `#\u0023vso[` and `#\u0023[`, a leading `::` gets an
  apostrophe in front). In `-o json` output the `\u0023` is an ordinary JSON escape that decodes back to `#`.
- **Resource use.** Evaluation, file size, JSON node count and variable depth are bounded (see [Evaluation limits](#evaluation-limits) and
  [Input limits](#input-limits)); a template that exceeds them fails with a diagnostic instead of using the machine.

## JSON Schemas

The schemas live in [`schemas/2026-10-01/`](../schemas/2026-10-01) and are embedded in the CLI:

| Schema | Command |
|---|---|
| Template envelope (`deploymentTemplate.json`, draft 2020-12): resource keys, `if`/`then` per `type` selecting the per-type schema | `earm schema print template` |
| Parameter file (`deploymentParameters.json`) | `earm schema print parameters` |
| One per resource type, `types/Microsoft.Entra/<type>.json` (path segments joined with `.`): property schemas plus the `x-earm` overlay | `earm schema print Microsoft.Entra/conditionalAccess/policies` |

Conventions of the per-type schemas:

- Every leaf, array item and nested object is `anyOf: [ <typed schema>, { "$ref": "#/$defs/expression" } ]`, so an expression
  string validates wherever a value of any type is expected (the ARM convention). The offline validator reports the *typed* branch
  when a value is neither (`SchemaViolation` at the exact path, for example `resources[10].properties.state`).
- Enumerations only where Graph defines a closed set: `signInAudience`, group `groupTypes` / `visibility` /
  `membershipRuleProcessingState`, `consentType`, named location `@odata.type` and `countryLookupMethod`, CA `state` /
  `clientAppTypes` / `builtInControls` / `operator` / risk levels / platforms / session control types, `userType`, and so on.
  Open-ended sets (authentication method combinations, directory role actions) are documented in the description, not enforced.
- `required` appears only where an object is meaningless without a member: `requiredResourceAccess[].resourceAppId` and `resourceAccess`, `ipRanges[].cidrAddress`,
  `rolePermissions[].allowedResourceActions`, the edge entries (an `appRoleAssignments` entry needs `resourceId` or `resourceAppId` and `appRole` or `appRoleId`, a
  `directoryRoles` entry `role` or `roleDefinitionId`, an `assignedLicenses` entry `skuId` or `skuPartNumber`). Properties that are required **on create**
  (`requiredOnCreate` in the overlay: `displayName`, `issuer`, …) are deliberately not schema-required, because a template that only updates an object legitimately omits
  them; the providers report them in the what-if row. `deployment validate` reports nested `required` findings (the envelope's own required keys have specific
  diagnostic codes instead). Note that an `anyOf` of `required` alternatives is reported against its first alternative.
- `additionalProperties` stays `true` (Graph passthrough) except for the edge entry objects of `appRoleAssignments`, `directoryRoles`
  and `assignedLicenses`, whose handlers reject unknown members.
- `x-earm` (`type`, `graphPath`, `apiVersions`, `nameMapsTo`, `nameKind`, `namePattern`, `nameMaxLength`, `keyProperties`, `requiredOnCreate`,
  `defaults`, `immutable`, `createOnly`, `secure`, `relationships`, `memberKey`, `stripOnRead`, `nullable`, `ignoreLiveOnly`, `permissions`) drives the executor.
  `ignoreLiveOnly` lists dotted paths (`web`, `api`, `sessionControls.signInFrequency`) under which sub-properties that exist only on the live object (server-defaulted
  children) are not drift and are not nulled in a PATCH.
  The property schemas are generated from one table by `tools/New-TypeSchemas.ps1`, which keeps the overlay text untouched.

`earm schema install` writes copies to `%LOCALAPPDATA%\EArm\schemas\2026-10-01\` (with the cross-file references rewritten to
local `file:///` URIs, because `https://schema.earm.proxylayer.net/schemas/…` is not published) and adds
`{ "fileMatch": ["**/*.entra.json", "**/*.entra.jsonc"], "url": "file:///…/deploymentTemplate.json" }` to `json.schemas` in the
workspace's `.vscode/settings.json` (or the user settings with `--global`). See [cli.md](cli.md#schema).
