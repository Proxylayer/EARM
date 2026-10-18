# Template format (schema version 2026-10-01)

An EARM template is a JSON document that looks and behaves like a classic Azure Resource Manager template, but
its resources are Microsoft Entra objects under the `Microsoft.Entra` provider namespace. This page is the
reference for the format; [edges.md](edges.md) covers relationship properties, [permissions.md](permissions.md) the
Graph permissions a template needs, [cli.md](cli.md) the verbs, and [diagnostics.md](diagnostics.md) every
validation code. Worked templates are in [`samples/`](../samples/README.md).

- File convention: `*.entra.json` (or `*.entra.jsonc`). The parser accepts `//` and `/* */` comments, trailing commas,
  a UTF-8 byte-order mark and multi-line strings (a raw line break inside a string, as Azure CLI and PowerShell accept it:
  [syntax](https://learn.microsoft.com/azure/azure-resource-manager/templates/syntax#multi-line-strings)), so a long expression can span several lines.
- Editor support: `earm schema install` maps the JSON Schemas to `**/*.entra.json` in VS Code
  (see [JSON Schemas](#json-schemas)).
- Validate offline at any time: `earm deployment validate -f template.entra.json -p '@params.json'`.

## Envelope

```json
{
  "$schema": "https://schema.earm.proxylayer.net/schemas/2026-10-01/deploymentTemplate.json#",
  "languageVersion": "2.0",
  "contentVersion": "1.0.0.0",
  "metadata": { "description": "free-form" },
  "definitions": { },
  "parameters": { },
  "variables": { },
  "functions": [ ],
  "resources": [ ], /* or { } keyed by symbolic name with languageVersion 2.0 */
  "outputs": { }
}
```

| Key | Required | Notes |
|---|---|---|
| `$schema` | yes | Exactly `https://schema.earm.proxylayer.net/schemas/2026-10-01/deploymentTemplate.json#` (the trailing `#` is optional); `UnrecognizedSchema` otherwise. |
| `contentVersion` | yes | Any string, as in ARM ("You can provide any value for this element", [syntax](https://learn.microsoft.com/azure/azure-resource-manager/templates/syntax#template-format)); `1.0.0.0` by convention. A non-string is `InvalidContentVersion`. |
| `languageVersion` | no | `"2.0"` enables symbolic names, `existing` resources, `definitions` and the aggregate type constraints; omitted (or `"1.0"`) is the original language. See [languageVersion 2.0](#languageversion-20). Any other value is `InvalidLanguageVersion` (an `-experimental` version is a warning and is read as 2.0). |
| `metadata` | no | Free-form. |
| `definitions` | no | languageVersion 2.0: user-defined types, referenced with `"$ref": "#/definitions/<name>"`. |
| `parameters` | no | Inputs: `type`, `defaultValue`, `allowedValues`, `minValue`/`maxValue`, `minLength`/`maxLength`, `metadata.description`; with languageVersion 2.0 also `$ref`, `nullable`, `properties`, `additionalProperties`, `discriminator`, `items`, `prefixItems`. See [Parameters](#parameters-and-parameter-files). |
| `variables` | no | Named values computed lazily from parameters and other variables; memoised. A top-level `copy` array declares array variables ([variable iteration](#copy-loops)). |
| `functions` | no | User-defined functions: `[{ "namespace": "c", "members": { "slug": { "parameters": [...], "output": { "type": "string", "value": "[...]" } } } }]`, called as `c.slug(...)`. They may not call `reference()` or other user functions. |
| `resources` | yes | An **array** of resources; with languageVersion 2.0 an **object** keyed by symbolic name. |
| `outputs` | no | `{ "name": { "type", "value", "condition" } }` (or `copy` instead of `value`; `$ref` instead of `type` with languageVersion 2.0), evaluated after everything is applied. |

There is no `apiProfile` and no deployment-settings block: deployment knobs are command-line flags. Unknown top-level keys are a warning
(`UnknownTopLevelProperty`) and are ignored.

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
| `properties` | no | The Graph properties to apply, plus the relationship properties of [edges.md](edges.md). An expression string for the whole object is accepted (`"properties": "[variables('groups')[copyIndex()].properties]"`): it is evaluated for each instance when the template is expanded, and the instance declares the keys of the object it yields as if they were written as an object literal: a known, non-secure value is declared as that literal value (so relationship properties inside it are edges, and the apply order between resources, which matches declared values, sees it), a secure value or one known only at deployment as an expression (secure values stay secure). When its value depends on a resource the same run creates (`reference()` of it), its keys are not known before that resource exists: the what-if shows the resource as known at deployment (`Ignore`, `?`) and `create` evaluates the expression once the referenced resource exists. Declare an object whose values are expressions to see its properties in the what-if. |
| `dependsOn` | no | Resource ids, `type/name`, unique bare names or copy-loop names; with languageVersion 2.0 symbolic names, a loop's symbolic name (every iteration) or `loop[i]` (one iteration). Implicit dependencies come from `reference()`, `references()` and `resourceId()` of template resources (outside relationship properties) and from nesting. |
| `condition` | no | Boolean or expression; `false` leaves the instance out of the deployment (what-if lists it only as skipped, see [whatif.md](whatif.md#change-types)). |
| `copy` | no | `name` (literal), `count`, `mode` (`serial`/`parallel`), `batchSize`; `copyIndex([name][, offset])` inside the resource. See [Copy loops](#copy-loops), which also covers property and output iteration. |
| `resources` | no | Nested child resources (short `type`, short `name`, same `apiVersion` rule); two levels (parent and child) only. |
| `existing` | no | languageVersion 2.0: `true` reads the resource instead of deploying it (see [languageVersion 2.0](#languageversion-20)). |
| `x-earm` | no | EARM's resource-level extension (not ARM's): `{ "adoptOnly": true, "objectId": "<object id>" }` adopts an object that must already exist and never creates it. See [Adopt-only resources](#adopt-only-resources). |
| `comments`, `metadata` | no | Ignored. |

### Names and resource ids

`name` is the ARM-style key of the resource. The provider injects it into the Graph property that identifies the object;
declaring that Graph property in `properties` too is an error unless the values are equal
(`NameMappedPropertyConflict`). EARM keeps no state: every run finds the object again **by this key in the live tenant**, as ARM finds a
resource by type and name, so changing the `name` names a different object (a rename is a new object; the old one stays). The full key
table and its consequences are in [engine.md](engine.md#identity-without-state).

| Name kind | Types | Meaning |
|---|---|---|
| `uniqueName` | applications, groups | Graph `uniqueName` alternate key; upsert is `PATCH /x(uniqueName='…')` with `Prefer: create-if-missing`. Immutable. An existing object without a `uniqueName` is not found by name, with two adoption fallbacks: an application declared with its `appId` is found by that and gets the name set; a group is found by its `displayName` when no group holds the `uniqueName` and exactly one group WITHOUT a `uniqueName` has that display name (case-insensitive; `GroupAdoptedByDisplayName` warning, the update sets the name; several such groups are `AmbiguousMatch`, a same-name group holding another `uniqueName` is never adopted). `UniqueNameNotSettable` when Graph refuses to set the name. |
| `userPrincipalName` | users | `local@domain`. |
| `appId` | servicePrincipals | The application (client) id GUID, or `[reference('app').appId]`. |
| child name | federatedIdentityCredentials, passwordCredentials, access package assignmentPolicies | `parent/child`; the child is the credential's own name. Nested children get the parent's name prepended by the loader. |
| `displayName` | named locations, CA policies, authentication strengths, administrative units, role definitions, entitlement management catalogs and access packages | Looked up by `displayName` on every run (0 hits: create, 1: that object, more than one: `AmbiguousMatch`; the display name must be unique in the tenant). A display name changed outside the template is no longer found: the next run creates a second object. |
| `id` | authenticationContextClassReferences, authenticationMethodConfigurations | The name is the Graph id: `c1` … `c99` for an authentication context (the Conditional Access documentation allows 99; the Graph v1.0 resource page still says `c1` to `c25`, see [providers.md](providers.md)); the method id for an authentication method configuration (`fido2`, `temporaryAccessPass`, `x509Certificate`, `microsoftAuthenticator`, `sms`, `voice`, `email`, `softwareOath`; case-insensitive, Graph returns `Fido2`). |
| GUID | entitlementManagement/catalogResources, entitlementManagement/resourceRoleScopes, appRoleAssignments, oauth2PermissionGrants, roleAssignments, roleEligibilityScheduleRequests, roleAssignmentScheduleRequests, privilegedAccess/groupEligibilityScheduleRequests, privilegedAccess/groupAssignmentScheduleRequests, roleManagementPolicies | A deterministic GUID chosen by the author with `guid(...)`, as with `Microsoft.Authorization/roleAssignments`. The `name` is a **label** (it gives the resource its id in the what-if and the result); the Graph object is found by its composite properties on every run. A changed composite is a **different assignment**: it is created, and the old one is retained and reported (`orphan: retained`), never removed (see [engine.md](engine.md#retained-assignments)). Only a change of a mutable property (a grant's `scope`) updates in place. |

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
| A changed immutable property fails the deployment (most resource providers) | Applications, groups and users fail (`ImmutablePropertyChanged`); a named location whose kind changes is **replaced** (delete, then create), which needs confirmation (`-y` or the prompt; a non-interactive run without `-y` fails before any write). |
| Changing a role assignment's properties under the same name is an error | The assignment `name` is a label; the tuple is the identity. A changed tuple creates a new assignment and **keeps the old one**, which still grants access: it is reported (`orphan: retained`), never removed. |
| PIM for Azure resources: `Microsoft.Authorization/roleEligibilityScheduleRequests`, `roleAssignmentScheduleRequests`, `roleManagementPolicies` | **Mirrored for directory roles** under `Microsoft.Entra/` with ARM's type and property names, so an ARM snippet pastes unchanged, and mapped onto Graph's PIM request APIs (`/roleManagement/directory/role{Eligibility,Assignment}ScheduleRequests`, `/policies/roleManagementPolicies`). Differences: an ARM request is an immutable record (a new request per change, named by its own GUID); in EARM the GUID `name` is a label, the **schedule** is found by its tuple on every run and EARM chooses `adminAssign` / `adminUpdate` / `adminRemove` itself (`requestType` is optional and only `AdminAssign` is accepted; `condition`, `conditionVersion` and `target*ScheduleId` are refused). The scope is the directory scope (`directoryScopeId`, `/` or an administrative unit; `appScopeId`), never an ARM scope. A policy is named by its assignment (`role`, or `scopeType` / `scopeId` / `roleDefinitionId`) instead of its policy id, only the declared rule ids are managed, approvers keep ARM's `{ id, userType }` (mapped to Graph's `singleUser` / `groupMembers`), and a policy is never created or deleted. Time-bound active assignments only: a permanent one stays `roleAssignments`. See [pim.md](pim.md). |
| PIM for Groups and the authentication methods policy | No ARM analogue. `privilegedAccess/groupEligibilityScheduleRequests` and `privilegedAccess/groupAssignmentScheduleRequests` take Graph's property names with the same schedule shape as the directory role types; `authenticationMethodConfigurations` are **tenant singletons** named by the method id: updated, never created; `deployment delete` resets fido2, temporaryAccessPass and x509Certificate to the Microsoft default and keeps the others (`NotDeletable`). |
| Microsoft Graph Bicep extension (`Microsoft.Graph/*` types) | Not related to EARM's format (EARM is ARM JSON, not Bicep), and has no counterpart for PIM eligibility, PIM schedules, PIM policies or authentication method configurations: there is nothing to translate from or to for those types. |
| Every PUT sends the secrets of the resource | Write-only secrets are **write-once** on an existing object: a `secretText` / `passwordProfile.password` (a path the type declares `secure`, or any secure value Graph does not return) is sent at creation and never compared or re-sent (Graph cannot return it, so a rerun would otherwise always change). Rotate client secrets with `rotationTag`. A secure value Graph does read back (a `state` from a `securestring` parameter) is compared and updated like any other value, masked in the what-if. |
| Child relationships are separate resources with their own lifecycle | Relationship arrays are declarable from either side ([edges.md](edges.md)): target-side lists (`members`, `owners`) are authoritative and prune; principal-side lists (`memberOf`, `appRoleAssignments`, `directoryRoles`, ...) only add, never remove. |
| Resources start as soon as their own dependencies finish; a failure lets independent resources finish | **Level-batched**: the dependency graph is cut into levels and each level runs as a batch (`--parallelism` inside it). By default a failure **stops at the batch boundary** (fail-fast; later batches are skipped); `--continue-on-error` keeps independent branches going. |
| `--rollback-on-error` | Not available (planned). A failed run leaves what it applied; a rerun converges. |
| Type names such as `Microsoft.Network/virtualNetworks/subnets` are parent/child | `conditionalAccess/policies`, `conditionalAccess/namedLocations`, `conditionalAccess/authenticationStrengths`, `conditionalAccess/authenticationContextClassReferences`, `privilegedAccess/groupEligibilityScheduleRequests` and `privilegedAccess/groupAssignmentScheduleRequests` are **pseudo-child** type names: top-level resources with a one-segment `name`, not children of a `conditionalAccess` or `privilegedAccess` resource. |
| `apiVersion` is the resource provider's REST version | `apiVersion` is the format version: `2026-10-01-preview` (Graph v1.0) or `2026-10-01-preview.beta` (Graph beta, passthrough, no normalisation guarantees). |
| `reference()` and `list*()` may not be used in `name` or `dependsOn` | Allowed: a name such as `[reference('app').appId]` is a **symbolic name** resolved before its batch ([engine.md](engine.md#symbolic-names)). |
| Secure outputs are never returned | `securestring` / `secureObject` outputs are `***` unless `--reveal-secure-outputs`, or written to an owner-only file with `--secure-outputs-file`. |
| `az deployment` exit codes (0 success, 1 error, 2 command-line parse error, 3 resource not found) | 0 success, 1 failure (usage errors included), 2 validation, 3 authentication / preflight, 5 `--exit-code-on-changes`; 4 is retired ([cli.md](cli.md#exit-codes)). |
| Implicit parent dependency of a child must be explicit | Inferred for nested children (a superset of the ARM rule). |
| `existing` resources; `reference(id, apiVersion)` reads any resource | `existing` resources are supported with languageVersion 2.0 ([languageVersion 2.0](#languageversion-20)): read, never written, a `NoChange` row in what-if. Without symbolic names, `reference(resourceId('Microsoft.Entra/…', 'name'), '2026-10-01-preview')` (the form **with** an `apiVersion`) always means an object that is **not** in the template: no dependency, an error when not found. Reference a template resource without the `apiVersion`. |
| `reference()` is not allowed in `variables` | **Intentional superset**: the existing-object form `reference(resourceId(...), apiVersion)` is allowed in a variable (a fixed lookup, evaluated once; when it names a resource the same run creates, the value is known at deployment: the what-if shows `?` and the variable is evaluated again once the resource exists). A reference to a template resource (by name, id or symbolic name) and `references()` are refused in variables, as ARM refuses them. |
| `reference()` accepts a resource name only without symbolic names | With languageVersion 2.0, EARM still accepts a bare resource name or an id besides the symbolic name (a superset). |
| Relationships live in separate resources | Arrays inside `properties`, declarable from either side; see [edges.md](edges.md). |
| Nested and linked deployments (`Microsoft.Resources/deployments`, `templateLink`) are tracked deployments in the deployment history | **Template composition**: the nested and linked templates are expanded into the parent's plan (one graph, one what-if, one apply); no deployment record exists ([Nested and linked deployments](#nested-and-linked-deployments)). Incremental mode only (ARM's rule too). Not supported: template specs (`templateLink.id`), other Azure scopes (`subscriptionId`, `resourceGroup`, `scope`), Key Vault references in nested parameters, a template given as a JSON string, `onErrorDeployment`. A `relativePath` may name a local file; `parametersLink.uri` may be a relative path (EARM extensions). `copyIndex()` of the deployment's loop is not visible inside an outer-scope template (pass it to an inner-scope one). What-if names the deployment of each nested row and shows no row for the deployment itself. |
| Property iteration only in top-level resources | Also accepted in nested child resources (the loader flattens them to top-level declarations). |
| `reference(resourceIdOrName, apiVersion, 'Full')` returns the full resource object (`location`, `sku`, `subscriptionId`, ...) | Returns `{ apiVersion, properties, resourceId, isTemplateResource[, symbolicName] }`: the keys that apply to an Entra object, `resourceId` being the EARM id. |
| Multi-line strings are refused by the REST API, the portal and pipelines, and accepted by Azure CLI and PowerShell | Accepted (EARM is a command-line tool like them). |
| Template limits (256 parameters, 256 or 512 variables, 64 outputs, 800 resources, 4 MB) are errors | Warnings (`ArmLimitExceeded`); EARM's own, larger hard limits apply ([Input limits](#input-limits)). |
| `contentVersion` and the parameter type names | Same: any `contentVersion` string; `string`, `secureString`, `SecureObject`, ... in any case. |
| `deployment()` returns `{ name, properties: { template, templateHash, parameters, mode, provisioningState, templateLink } }` | `{ name, properties: { templateHash, parameters, mode } }`, plus the flat aliases `templateHash`, `tenantId` and `mode` of earlier EARM versions. There is no `template` (the template is in the file), no `provisioningState` (nothing is stored) and no `templateLink` (EARM deploys local files only). `templateHash` is EARM's `sha256:` hash of the template, not ARM's numeric hash. `properties.parameters` lists the values supplied by parameter files and `-p` as `{ name: { value } }`, without secure parameters, Key Vault references or defaults; offline (`deployment validate`) it is a deploy-time unknown. |
| `tenant()` returns `{ countryCode, displayName, id, tenantId }` | The same fields (`countryCode` is the organization's `countryLetterCode`, `id` is `/tenants/<tenantId>`), plus `defaultDomain` (the default verified domain). When the identity may not read `/organization` (or offline) `tenant()` is a deploy-time unknown. |
| `deployer()` returns the principal that started the deployment | The principal EARM signed in as: `{ objectId, tenantId, userPrincipalName }`, with an empty `userPrincipalName` for an app-only sign-in (a service principal or managed identity), as in ARM. A deploy-time unknown offline. |
| `environment()` describes the Azure environment, `graph` being Azure AD Graph (`https://graph.windows.net/`) | Only the fields that mean something for Entra: `name`, `portal`, `resourceManager`, `authentication` (`loginEndpoint`, `audiences`, `tenant`, `identityProvider`) and `suffixes.keyvaultDns`. **`graph` is the Microsoft Graph endpoint** EARM talks to (`https://graph.microsoft.com` in the public cloud). The storage, SQL, batch, gallery and other Azure suffixes and endpoints are absent. The US Government DoD cloud reports the US Government Resource Manager endpoints. A deploy-time unknown offline. |
| `roleDefinitions(roleName)` resolves an **Azure RBAC** role by name to `{ id, roleDefinitionId }` | Resolves a **Microsoft Entra directory role** by display name: `{ id, roleDefinitionId, name, templateId }`, where `id`, `roleDefinitionId` and `templateId` all hold the role definition GUID (a directory role is addressed by its GUID, which is what `roleAssignments` and the PIM types take as `roleDefinitionId`). Built-in roles come from the embedded table, custom roles from the tenant; a GUID passes through. Legal where `roleTemplate()` is. |
| `padLeft()` | ARM documents no upper bound on `totalLength`; EARM allows any length up to its string limit (1 MiB, `EvaluationBudgetExceeded` beyond). |
| Other ARM functions | Not available (an unknown-function error): `parseCidr`, `cidrSubnet`, `cidrHost`, `listAccountSas` and the other `list*` functions. |
| Bicep-only operators | Expressions are ARM JSON syntax: the Bicep safe-dereference `.?` and non-null assertion `!` operators do not exist. Use `tryGet(obj, 'key')`, `tryIndexFromEnd(array, n)` or `coalesce()`. |
| Parameter constraints | As in ARM, only `defaultValue` may be an expression; an expression in `minValue`, `maxValue`, `minLength` or `maxLength` is an `InvalidParameterConstraint` error. An expression default is checked against the type and the constraints once it is evaluated. For an `array` parameter, `allowedValues` lists the values its **elements** may take (every element must be one of them). |

### Adopt-only resources

A resource that must already exist, and that the template adopts and manages but never creates, declares EARM's resource-level extension
`x-earm`:

```json
{
  "type": "Microsoft.Entra/groups", "apiVersion": "2026-10-01-preview", "name": "pg-delete-protection",
  "properties": { "displayName": "PointGuard Delete Protection", "description": "Managed by EARM" },
  "x-earm": { "adoptOnly": true, "objectId": "[parameters('deleteProtectionGroupId')]" }
}
```

- `adoptOnly: true`: the object is found by the resource's key as always (for a group also by the `displayName` fallback) and updated like any
  other resource, but a missing object is the error `AdoptOnlyNotFound` (an unsupported Create row in what-if; the deployment fails the resource
  and creates nothing) instead of a create. `deployment delete` leaves it alone: the template never created it.
- `objectId` (optional; a GUID, or an expression known before deployment: a parameter or variable) pins the Graph object id the key must lead to
  and implies `adoptOnly`. Another object under the key is `AdoptedObjectMismatch`, and nothing is written to it. This turns an adoption by name
  (a group adopted by `displayName`, a user by `userPrincipalName`) into a hard check instead of a what-if review.
- A user that is adopt-only needs none of the create-only properties (`displayName`, `passwordProfile`…): edges-only adoption of an existing
  account (`memberOf`, `directoryRoles`) is `{ "x-earm": { "adoptOnly": true } }` with only the properties to manage.

It applies to every type (groups and users are the common cases). It differs from languageVersion 2.0 `"existing": true`, which only **reads** an
object (no property may be declared, nothing is written); the two cannot be combined (`InvalidAdoptOnly`).

**Why `x-earm` and not a plain resource key.** ARM's resource declaration has no adopt-only notion, and ARM refuses unknown resource keys, so any
shape is an EARM extension; the `x-` prefix (the convention of vendor extensions in JSON Schema and OpenAPI, and of EARM's own type overlays)
says so and cannot collide with a key ARM adds later. `"existing"` keeps ARM's boolean meaning (read only); `metadata` is free-form and never
evaluated by ARM, so a pinned id from a parameter would not fit; and a key inside `properties` would be mistaken for a Graph property.

## languageVersion 2.0

`"languageVersion": "2.0"` turns on the ARM features Bicep compiles to ([syntax](https://learn.microsoft.com/azure/azure-resource-manager/templates/syntax#languageversion-20)), so a
Bicep-compiled JSON template of EARM types is accepted unchanged. The original language (no `languageVersion`) behaves as
before. [`samples/symbolic-names.entra.json`](../samples/symbolic-names.entra.json) shows all of it.

**Symbolic names** ([resource declaration](https://learn.microsoft.com/azure/azure-resource-manager/templates/resource-declaration#use-symbolic-name)). `resources` is an object whose keys are
the symbolic names: letters, digits and `_`, case-sensitive, unique, and free to overlap with parameter, variable and output names
(`InvalidSymbolicName` otherwise). A symbolic name is what `dependsOn`, `reference()` and `references()` take:

| Form | Means |
|---|---|
| `"dependsOn": [ "app" ]` | the resource `app`; for a copy loop, every iteration ([resource dependency](https://learn.microsoft.com/azure/azure-resource-manager/templates/resource-dependency#dependson)) |
| `"dependsOn": [ "[format('apps[{0}]', copyIndex())]" ]`, `"apps[1]"` | one iteration of the loop `apps` (the form Bicep emits for `apps[i]`) |
| `reference('app')`, `reference('apps[1]')` | the live object of the resource or iteration (an implicit dependency, also with an `apiVersion` or `'Full'`); `reference('apps')` without an index is `ReferenceTargetNotFound` |
| `references('apps'[, 'Full' \| 'Properties'])` | the array of the iterations' objects, in copy-index order, leaving out an iteration whose `condition` is false; not allowed inside the loop it names, nor in a variable ([references](https://learn.microsoft.com/azure/azure-resource-manager/templates/template-functions-resource#references)) |

Resource ids, `type/name` and bare names keep working in `dependsOn` and `reference()`; ARM refuses a bare resource name in `reference()` once
symbolic names are on, EARM still accepts it. Nested `resources` arrays (children with short types) still work inside a symbolic declaration.

**Existing resources** ([declare existing resources](https://learn.microsoft.com/azure/azure-resource-manager/templates/resource-declaration#declare-existing-resources)). A symbolic declaration
with `"existing": true` needs only `type`, `apiVersion` and `name` (a `properties` object is `InvalidExistingResource`: it would be ignored).
The engine reads the object by its key, as it reads any declared resource, and publishes it for `reference()`; it never creates, updates or
deletes it. In what-if it is a `NoChange` row with the reason "existing resource (\"existing\": true): read only, never written" (ARM never
deploys an existing resource, so it never shows a change for one); a missing object is an unsupported row in what-if and a failed resource
(`ExistingResourceNotFound`, its dependents skipped) in a deployment, as ARM fails a deployment that reads a resource that does not exist.
`deployment delete` leaves it alone, and the orphan report neither lists it nor lets it widen the listing (an existing application does not put its credentials in scope). `existing` resources may have a
`condition` and a `copy` loop.

**User-defined types** ([type definitions](https://learn.microsoft.com/azure/azure-resource-manager/templates/definitions), [parameters](https://learn.microsoft.com/azure/azure-resource-manager/templates/parameters)). `definitions` holds named types;
a parameter, an output, a user-defined function parameter or output, and any nested type may use `"$ref": "#/definitions/<name>"` instead of
`type` (keys written next to the `$ref`, such as `nullable` or `metadata`, take precedence). A type may declare, besides `type`,
`allowedValues`, `minValue`/`maxValue` and `minLength`/`maxLength`:

| Keyword | Applies to | Rule |
|---|---|---|
| `nullable` | any | the value may be `null` or omitted; a nullable parameter without a value or default is `null` (no `MissingParameterValue`) |
| `properties` | object | a type per property; a property is required unless its type is `nullable` |
| `additionalProperties` | object | a type for the undeclared properties, or `false` to refuse them (Bicep's `sealed: true` is read as `false`); allowed when absent |
| `discriminator` | object | `{ "propertyName", "mapping": { "<value>": <object type> } }`: the property's value selects the type the rest is checked against |
| `prefixItems` | array | a type per leading element |
| `items` | array | a type for the elements after `prefixItems`, or `false` for none |

Supplied values and defaults are checked against these constraints like the scalar ones (`ParameterValueNotAllowed`,
`DefaultValueNotAllowed`; the message names the location in the value, never the value). A malformed type (an unknown `type`, an
unresolvable or cyclic `$ref`, `properties` on a string) is `InvalidTypeDefinition`; a languageVersion 2.0 keyword in a template without
`"languageVersion": "2.0"` is `InvalidLanguageVersion`. As in the original language, output values are not checked against their declared
type. `metadata` may be anywhere, and Bicep's markers (`__bicep_export!`, `__bicep_imported_from!`, `_generator`) are accepted and ignored.

A nested deployment in a languageVersion 2.0 template is evaluated in its own scope by default, and `outer` is refused, as in ARM (see
[Nested and linked deployments](#nested-and-linked-deployments)). Not applicable to EARM: `apiProfile`.

## Nested and linked deployments

A `Microsoft.Resources/deployments` resource deploys another template together with this one: an inline `template` (a **nested** template) or a
file named by `templateLink` (a **linked** template), as in ARM ([linked and nested templates](https://learn.microsoft.com/azure/azure-resource-manager/templates/linked-templates),
[resource reference](https://learn.microsoft.com/azure/templates/microsoft.resources/deployments)). EARM keeps no deployment record (it is stateless),
so a nested deployment is template composition: its template is loaded, validated like a main template, and its resources are expanded into the
same plan as the parent's: the same graph and batches, what-if rows, apply, `deployment delete`, the orphan report and `earm permissions`.
The type name and ARM's apiVersions (`2015-01-01` to `2026-06-01`; the value changes nothing) are kept, so an ARM snippet pastes in.
[`samples/nested-deployments.entra.json`](../samples/nested-deployments.entra.json) shows every form.

```json
{
  "type": "Microsoft.Resources/deployments",
  "apiVersion": "2025-04-01",
  "name": "teams",
  "properties": {
    "mode": "Incremental",
    "expressionEvaluationOptions": { "scope": "inner" },
    "parameters": { "env": { "value": "[parameters('env')]" } },
    "template": {
      "$schema": "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#",
      "contentVersion": "1.0.0.0",
      "parameters": { "env": { "type": "string" } },
      "resources": [ { "type": "Microsoft.Entra/groups", "apiVersion": "2026-10-01-preview", "name": "[format('contoso-{0}-team', parameters('env'))]", "properties": { "displayName": "Team", "mailEnabled": false, "securityEnabled": true } } ],
      "outputs": { "groupId": { "type": "string", "value": "[reference(format('contoso-{0}-team', parameters('env'))).id]" } }
    }
  }
}
```

| Property | EARM |
|---|---|
| `name`, `condition`, `copy`, `dependsOn`, `comments`, `metadata` | As for any resource. The name follows ARM's rule (1-64 characters: letters, digits, `-`, `_`, `.`, `(`, `)`) and must be computable before deployment, as must the `condition` (`NestedDeploymentNotStatic`); a deployment whose condition is false is not expanded. A `copy` loop expands the template once per iteration. |
| `properties.mode` | `Incremental` only. ARM allows only Incremental for nested and linked deployments; deploy the parent with `--mode Complete` instead: the resources of the nested templates count as declared in the parent's Complete-mode comparison (the orphan report). `Complete` is `InvalidNestedDeployment`. |
| `properties.template` | The inline template: any template EARM accepts (EARM types only; its `$schema` may be EARM's or ARM's, and may be omitted). A template given as a JSON string is not supported. |
| `properties.templateLink.relativePath` | A linked file relative to the template that declares the deployment: the file's directory, or, for a template read from a URL, that URL (ARM combines the parent URL and the relative path; the parent's query string is kept). `..` is allowed; an absolute path is refused. |
| `properties.templateLink.uri` | An https URL, downloaded with the rules of `-p https://...` parameter files: https only, no user name or password in the URL, no credentials sent, redirects never followed, at most 1 MiB and 120 s; the query string (a SAS token) is sent but never shown. `queryString` is appended to the URL. |
| `properties.templateLink.contentVersion`, `properties.parametersLink.contentVersion` | When given, must equal the linked file's `contentVersion` (`LinkedTemplateContentVersionMismatch`). |
| `properties.templateLink.id` | Template specs are Azure resources: `NestedDeploymentFeatureNotSupported`. |
| `properties.parameters` | Values for the template's parameters, `{ "name": { "value": ... } }`, evaluated in the parent (with the deployment's copy iteration). A value may depend on a resource the parent deploys (`reference()`, another nested deployment's outputs): it is re-evaluated when the nested resources are planned and applied, and the nested resources wait for that resource. A Key Vault `reference` entry is not supported: pass the secret from a secure parameter of the parent. A value for a parameter the template does not declare is `UnknownParameter`; a required parameter that is not passed is `MissingParameterValue` (an error, even offline). |
| `properties.parametersLink` | A parameter file (the `deploymentParameters` shape): an https `uri`, or, as an EARM extension, a path relative to the declaring template. Not together with `parameters` (ARM rule). |
| `properties.expressionEvaluationOptions.scope` | `outer` (the default for an inline template) or `inner`. A linked template is always inner (an explicit `outer` is ignored with a warning). With languageVersion 2.0 the default is `inner` and `outer` is refused, as in ARM. The value must be literal. |
| `properties.debugSetting` | Accepted and ignored. Other properties (`onErrorDeployment`, `validationLevel`, ...) and `location`, `tags`, `identity` are ignored with `NestedDeploymentPropertyIgnored`; `subscriptionId`, `resourceGroup` and `scope` (another Azure scope) are refused. |

**Scope.** In the **outer** scope the template's expressions see the parent's parameters, variables and user-defined functions; the template's own
`parameters`, `variables` and `functions` are ignored (a warning), `parameters` / `parametersLink` on the deployment are refused, and a `copy` loop
inside the template is an error (ARM rule: put the copy on the deployment, or use the inner scope). In the **inner** scope the template is
isolated: it sees only the parameters passed in, and its own variables and functions. `deployment()` is the nested deployment in the inner
scope (its `name`) and the parent's in the outer scope. In both scopes `copyIndex()` refers to the template's own loops: the deployment's
iteration is passed as a parameter (`"index": { "value": "[copyIndex()]" }`).

**Names and dependencies.** Resource names, symbolic names, copy-loop names and `dependsOn` resolve within the template that declares them, as in
ARM: `reference('name')` names a resource of the same template. A deployment is a resource of its parent: `dependsOn` on it (its name, its
symbolic name, `type/name` or `resourceId('Microsoft.Resources/deployments', 'name')`) waits for **every** resource of its template, nested
deployments included, and every resource of the template waits for what the deployment depends on (its `dependsOn`, and the resources its
`parameters` reference).

**Outputs.** `reference('<deployment>').outputs.<output>.value` (or `reference('<symbolic name>')`, and `references('<loop>')` for a deployment loop)
returns the template's outputs once its resources are planned (what-if) or applied (create), with an implicit dependency on the deployment.
In what-if an output that depends on a resource still to be created is a deploy-time value. A `secureString` / `secureObject` output stays
secure: usable in the parent, masked wherever it is shown. An output that cannot be evaluated fails the deployment resource in a deployment
(its dependents are skipped) and is a warning in what-if. Unlike ARM, `reference()` of the template's own resources is also evaluated in the
outputs of an outer-scope template.

**Identity across templates.** Ids must be unique across the main template and every nested template: two templates that declare the same
object are a `DuplicateResourceId` error naming both deployments, as two declarations of one template are. Two nested deployments with the
same name (in any template) are a duplicate as well.

**Limits.** Nested deployments are expanded at most 10 levels deep and at most 500 in one run (ARM's what-if limit of nested templates;
`NestedDeploymentLimitExceeded`); the instance limit applies to the whole expansion. A linked template that is deployed again by a template it
encloses is `LinkedTemplateCycle`.

**Offline validation.** `deployment validate` expands nested and linked templates offline: inline templates and local linked files are validated
(their findings point at the place in the file that holds them), https links are not downloaded (`LinkedTemplateNotFetched`, a warning), and a
deployment whose condition, name or link depends on a value that is not known offline is not expanded (a warning). `what-if`, `create` and
`delete` download https links and validate everything before the first sign-in.

## Resource type catalog

`Microsoft.Entra/…` types of schema version 2026-10-01. The table follows the `x-earm` overlay at the end of each
schema file (`earm schema print <type>`), which the executor loads at runtime. A provider can adjust its overlay (groups require only
`displayName` and filter `groupTypes` / `visibility` from the immutable list), so the table also notes those adjustments.

Defaults (the parentheses in the last column, and those named in the type's rules) apply only to an object EARM creates,
including the restore of a soft-deleted object that replaces a create. An existing object that EARM finds or adopts is
changed only where the template declares a property: an undeclared property is neither diffed nor written, so declare
it to manage it. Two kinds of default still apply on every run: one inside a declared nested object that Graph replaces
whole (a Conditional Access policy's `conditions.clientAppTypes` and `grantControls.operator`, written with the declared
`conditions` / `grantControls` so the PATCH does not clear them), and one that defines what the declaration means (the
`directoryScopeId` of a role assignment, and the scope, `scheduleInfo` and `justification` of a PIM schedule request).

| Type | `name` is | Graph path | Immutable (error, or recreate for a named location kind) | Edge properties | Required on create |
|---|---|---|---|---|---|
| `applications` | `uniqueName` | `/applications` | `uniqueName`, `appId`, `id`, `publisherDomain` | `owners` | `displayName` (`signInAudience` defaults to `AzureADMyOrg`: the default describes the application EARM creates and is never sent in the upsert, which would merge it into an application that already exists; a created application that lacks it gets it by a follow-up PATCH) |
| `applications/federatedIdentityCredentials` | credential `name` (parent: application) | `/applications/{id}/federatedIdentityCredentials` | `name` | none | `issuer`, `subject` (`audiences` defaults to `["api://AzureADTokenExchange"]`); at most 20 per application, and `issuer` + `subject` must be unique within the application |
| `applications/passwordCredentials` | credential `displayName` (parent: application) | `addPassword` / `removePassword` | everything (never edited: a rotation adds the new secret, then removes the old one) | none | nothing (`endDateTime` defaults to +180 days) |
| `servicePrincipals` | `appId` | `/servicePrincipals` | `appId`, `servicePrincipalType` | `owners`, `memberOf`, `ownerOf`, `appRoleAssignments`, `directoryRoles` | nothing (on create, `accountEnabled` defaults to true and `appRoleAssignmentRequired` to false) |
| `groups` | `uniqueName` (else adopted by `displayName` when it has no `uniqueName`, see above) | `/groups` | `uniqueName`, `mailEnabled`, `isAssignableToRole`, `groupTypes` (Unified presence), `visibility` (HiddenMembership; `mailNickname`, Public / Private visibility and DynamicMembership are updated in place) | `members`, `owners`, `memberOf`, `administrativeUnits`, `appRoleAssignments`, `directoryRoles`, `assignedLicenses` | `displayName` (`mailEnabled` and `securityEnabled` default per kind, `mailNickname` is derived from the name) |
| `users` | `userPrincipalName` | `/users` | `userPrincipalName` | `manager`, `memberOf`, `ownerOf`, `administrativeUnits`, `appRoleAssignments`, `directoryRoles`, `assignedLicenses` | `displayName` (`mailNickname`, `accountEnabled`, `passwordProfile` defaulted on create only; an existing user's undeclared properties are never written) |
| `roleDefinitions` | `displayName` (custom roles only) | `/roleManagement/directory/roleDefinitions` | `templateId` | none | `rolePermissions[].allowedResourceActions` (`isEnabled` defaults to true) |
| `conditionalAccess/authenticationContextClassReferences` | the id, `c1`…`c99` | `/identity/conditionalAccess/authenticationContextClassReferences` | `id` | none | `displayName` (`isAvailable` defaults to true on create) |
| `conditionalAccess/authenticationStrengths` | `displayName` (custom only) | `/policies/authenticationStrengthPolicies` | `policyType` | none | `allowedCombinations` |
| `appRoleAssignments` | deterministic GUID | `/servicePrincipals/{resourceId}/appRoleAssignedTo` | everything; composite key (`principalId`, `resourceId`, `appRoleId`) | none | the three key properties |
| `oauth2PermissionGrants` | deterministic GUID | `/oauth2PermissionGrants` | key fields (`clientId`, `resourceId`, `consentType`, `principalId`); only `scope` is mutable | none | key + `scope` |
| `conditionalAccess/namedLocations` | `displayName` | `/identity/conditionalAccess/namedLocations` | `@odata.type` | none | `@odata.type` (Graph also requires `ipRanges` or `countriesAndRegions`, per kind; on create, `isTrusted` defaults to false, `includeUnknownCountriesAndRegions` to false and `countryLookupMethod` to `clientIpAddress`) |
| `conditionalAccess/policies` | `displayName` | `/identity/conditionalAccess/policies` | none | none | `conditions.users`, `conditions.applications`, `grantControls` or `sessionControls` (`state` defaults to `enabledForReportingButNotEnforced` on create and is left as it is live afterwards when undeclared; a declared `state: enabled` is applied only with `--allow-ca-enforce`: without it a new policy is created report-only and an existing policy keeps its live state, with a `ConditionalAccessEnforcementNotAllowed` warning; inside a declared `conditions` / `grantControls`, `clientAppTypes` defaults to `["all"]` and `operator` to `OR` on every run) |
| `administrativeUnits` | `displayName` | `/directory/administrativeUnits` | `isMemberManagementRestricted` | `members` | nothing (a unit that becomes dynamic, on create or by switching from assigned, gets `membershipRuleProcessingState: On` unless declared) |
| `roleAssignments` | deterministic GUID | `/roleManagement/directory/roleAssignments` | everything; composite key (`roleDefinitionId`, `principalId`, `directoryScopeId`) | none | the three key properties (`directoryScopeId` defaults to `/`) |
| `roleEligibilityScheduleRequests` | deterministic GUID (a label) | `POST /roleManagement/directory/roleEligibilityScheduleRequests` (adminAssign / adminUpdate / adminRemove); read from `/roleManagement/directory/roleEligibilitySchedules` | the key (`principalId`, `roleDefinitionId`, `directoryScopeId` or `appScopeId`): a changed key is a new eligibility and the old one is retained; `scheduleInfo` changes in place | none | `principalId` and `roleDefinitionId` or `role`. ARM names (`requestType` optional, only `AdminAssign`; `condition`, `conditionVersion` and `target*ScheduleId` refused); sugar `role`, `administrativeUnit`; the scope defaults to `/`, `scheduleInfo` to NoExpiration, `justification` to `Deployed by earm`. See [providers.md](providers.md#worked-example-pim-eligibility-for-directory-roles). |
| `privilegedAccess/groupEligibilityScheduleRequests` | deterministic GUID (a label) | `POST /identityGovernance/privilegedAccess/group/eligibilityScheduleRequests` (adminAssign / adminUpdate / adminRemove); read from `/identityGovernance/privilegedAccess/group/eligibilitySchedules` | the key (`groupId`, `principalId`, `accessId`): a changed key is a new eligibility and the old one is retained; `scheduleInfo` changes in place | none | `groupId` or `group`, `principalId` and `accessId` (`member` or `owner`). Sugar `group` (the group's uniqueName or id); `requestType` optional, only `AdminAssign`; `scheduleInfo` defaults to NoExpiration (a group's PIM policy may forbid it), `justification` to `Deployed by earm`. The first request onboards the group in PIM for Groups permanently; dynamic and on-premises synced groups are refused. See [providers.md](providers.md#worked-example-pim-for-groups-eligibility). |
| `roleAssignmentScheduleRequests` | deterministic GUID (a label) | `POST /roleManagement/directory/roleAssignmentScheduleRequests` (adminAssign / adminUpdate / adminRemove); read from `/roleManagement/directory/roleAssignmentSchedules` (`assignmentType` Assigned) | the key (`principalId`, `roleDefinitionId`, `directoryScopeId` or `appScopeId`): a changed key is a new assignment and the old one is retained; `scheduleInfo` changes in place | none | as `roleEligibilityScheduleRequests`, plus a **time-bound** `scheduleInfo` (AfterDateTime or AfterDuration; absent or NoExpiration is refused: a permanent active assignment is `roleAssignments`). The same principal, role and scope may not also be declared by a `roleAssignments` resource or a principal's `directoryRoles` (`PimAssignmentConflict`), and a live permanent assignment under the tuple is refused, never converted or removed. See [providers.md](providers.md#worked-example-pim-active-assignments). |
| `privilegedAccess/groupAssignmentScheduleRequests` | deterministic GUID (a label) | `POST /identityGovernance/privilegedAccess/group/assignmentScheduleRequests` (adminAssign / adminUpdate / adminRemove); read from `/identityGovernance/privilegedAccess/group/assignmentSchedules` (`assignmentType` assigned) | the key (`groupId`, `principalId`, `accessId`): a changed key is a new assignment and the old one is retained; `scheduleInfo` changes in place | none | as `privilegedAccess/groupEligibilityScheduleRequests`, plus a **time-bound** `scheduleInfo` (a permanent membership is the group's `members` / `owners`). The target group may not declare `members` (for `accessId: member`) or `owners` (for `owner`) and the principal may not list the group in `memberOf` / `ownerOf` (`PimAssignmentConflict`): an active assignment is a real membership, which the authoritative list would remove and the ensure-only list would re-add permanently. See [providers.md](providers.md#worked-example-pim-active-assignments). |
| `roleManagementPolicies` | deterministic GUID (a label) | found through `GET /policies/roleManagementPolicyAssignments?$filter=scopeId eq … and scopeType eq … and roleDefinitionId eq …&$expand=policy($expand=rules)`; written with `PATCH /policies/roleManagementPolicies/{policyId}/rules/{ruleId}` per changed rule (never created, never deleted) | the scope (`scopeType`, `scopeId`, `roleDefinitionId`, or the sugar `role` / `group` + `accessId`); every rule property can change | none | `rules` (at least one; only the declared rule ids are managed). A group policy may add `onboarding: { temporaryEligiblePrincipalId }` to let the deployment onboard its group in PIM for Groups with a temporary eligibility ([pim.md](pim.md#onboarding-opt-in)) |
| `authenticationMethodConfigurations` | the method id (`fido2`, `temporaryAccessPass`, `x509Certificate`, `microsoftAuthenticator`, `sms`, `voice`, `email`, `softwareOath`; case-insensitive) | `/policies/authenticationMethodsPolicy/authenticationMethodConfigurations` | none (`@odata.type` is derived from the name) | none | never created: a tenant singleton that must exist (a missing one is an error); delete resets fido2 / temporaryAccessPass / x509Certificate to the Microsoft default and keeps the others (`NotDeletable`). See [providers.md](providers.md#worked-example-authentication-method-configurations). |
| `entitlementManagement/catalogs` | `displayName` | `/identityGovernance/entitlementManagement/catalogs` | none (`catalogType` is service-controlled and not compared) | none | nothing (`state` defaults to `published` and `isExternallyVisible` to false on create). Delete is refused while the catalog holds packages (`CatalogNotEmpty`). See [access-packages.md](access-packages.md). |
| `entitlementManagement/catalogResources` | deterministic GUID (a label) | `POST /identityGovernance/entitlementManagement/resourceRequests` (`adminAdd` / `adminRemove`); read from `/catalogs/{catalogId}/resources` | everything; composite key (`catalogId`, `originId`) | none | `catalogId`, `originId` (a group; `originSystem` defaults to `AadGroup`, the only origin). The create waits until the group is listed in the catalog (`CatalogResourceRequestFailed` after 90 s). A role-assignable group needs Global Administrator or Privileged Role Administrator (`CatalogResourceAccessDenied`). |
| `entitlementManagement/accessPackages` | `displayName` (unique in the tenant; duplicates are `AmbiguousMatch`) | `/identityGovernance/entitlementManagement/accessPackages` | `catalogId` (error, never a move) | none | `catalogId` (`isHidden` defaults to false on create). Delete is refused while the package has assignments (`AccessPackageAssignmentsExist`). |
| `entitlementManagement/resourceRoleScopes` | deterministic GUID (a label) | `/identityGovernance/entitlementManagement/accessPackages/{id}/resourceRoleScopes` (create and delete only) | everything; composite key (`accessPackageId`, `resourceOriginId`, `role`) | none | the three key properties; `role` is `Member`, `Owner`, `Eligible Member` or `Eligible Owner`, picked by display name from the catalog's roles of the group. The group must be a resource of the package's catalog (`AccessPackageRoleNotAvailable`); the eligible roles need a group onboarded in PIM for Groups: a same-run group schedule request is applied first and the role scope waits for the catalog to offer the role (`PimGroupNotOnboarded` without such a request, or as a next-run warning when the wait times out). Deleting a role scope people hold carries a warning. |
| `entitlementManagement/accessPackages/assignmentPolicies` | `displayName` (parent: access package) | `/identityGovernance/entitlementManagement/assignmentPolicies` (`$filter=accessPackage/id eq`) | none | none | Graph's properties as declared (`allowedTargetScope`, `expiration`, `requestorSettings`, `requestApprovalSettings` ...). Only declared values are compared (subject sets order-insensitively); an update is a full `PUT` of the live policy with the declared values merged in. Delete is refused while the policy has assignments. |

An immutable change is a hard error (`ImmutablePropertyChanged`) for every type that is not recreated: applications, service principals, groups,
users, custom roles, authentication strengths, administrative units...; for a named location's kind (`@odata.type`) it is
`Modify` with `mode: recreate`. Assignments, federated credentials and grants are never recreated: their immutable properties are their key, so a change is a
different object (a renamed credential is a new one; an assignment or grant with a changed key is created and the old one is retained). Password
credentials are never edited and never recreated either: a rotation is a `Modify` that adds the new secret, then removes the old one. Soft-deleted objects (applications,
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
- **EARM-only properties** (never sent to Graph): `rotationTag`, `rotateBeforeDays` and `stampRotationTag` on `passwordCredentials` (see below),
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
  is not detected: declare a different `endDateTime` to rotate it, or set `stampRotationTag: true` (default `false`), which rotates such an
  untagged credential once so the declared tag is stamped into the new name. It needs a `rotationTag` and does nothing once the live name
  carries a tag, so it can stay declared.

## Expressions

A string of the form `[ ... ]` is an expression; `[[` escapes a literal bracket and `''` escapes a quote. Expressions are at
most 24,576 characters, nest at most 100 levels deep (calls, indexers, `lambda()` bodies and chained `.property` / `[index]`
accesses counted together; `ExpressionTooDeep` beyond that) and function names are case-insensitive. They may appear in any string value (property values, names,
conditions, dependsOn entries, outputs).

| Group | Functions |
|---|---|
| String | `concat format toLower toUpper substring replace split join trim startsWith endsWith contains like length empty indexOf lastIndexOf padLeft string base64 base64ToString base64ToJson dataUri dataUriToString uri uriComponent uriComponentToString guid uniqueString newGuid utcNow dateTimeAdd dateTimeFromEpoch dateTimeToEpoch` |
| Logic | `if and or not equals less lessOrEquals greater greaterOrEquals coalesce bool true false` |
| Numeric | `add sub mul div mod min max int float` |
| Array and object | `array createArray createObject first last take skip union intersection range items json null objectKeys flatten distinct shallowMerge tryGet indexFromEnd tryIndexFromEnd` and the lambda functions `map mapValues filter reduce sort toObject groupBy lambda lambdaVariables` |
| Resource and deployment | `resourceId reference references copyIndex deployment tenant deployer environment parameters variables` and user functions `namespace.fn(...)` |
| **Entra** | `wellKnownApp(name)`, `appRole(app, value)`, `permissionScope(app, value)`, `roleTemplate(name)`, `roleDefinitions(name)` |

ARM comparison rules apply: `contains()` on a string is case-sensitive (on an object it matches keys case-insensitively), while
`startsWith`, `endsWith`, `indexOf` and `lastIndexOf` on strings ignore case. `less`, `lessOrEquals`, `greater` and `greaterOrEquals` order
strings as ARM does, by culture-aware comparison rather than character code: a lowercase letter sorts before the same uppercase letter, so
`greater('A', 'a')` is true. `like(text, pattern)` ignores case and knows one wildcard, `*` (any run of characters, possibly empty); every
other character, `?` included, matches only itself. `and`, `or`, `union` and `intersection` take at least two arguments. `union()` of objects merges nested objects
recursively (arrays are replaced, the last one wins) and, like `intersection()`, matches property names case-insensitively (the first
spelling is kept); `shallowMerge()` replaces nested objects whole. `items()` returns the entries sorted alphabetically by key, as ARM does,
whatever order the object declares them in (case is ignored; keys that differ only in case keep a fixed ordinal order). `array()` returns an array argument unchanged and wraps anything else.
`range(start, count)` needs `count` between 0 and 10,000 and `start + count` at most 2,147,483,647, as in ARM. `guid()` and
`uniqueString()` compute the same values as ARM: `guid()` is the RFC 4122 version 5 UUID in ARM's namespace
`11fb06fb-712d-4ddd-98c7-e71bbd588830` of the arguments joined with `-` (UTF-8), `uniqueString()` ARM's 13-character hash of the same
text. `dataUri()` writes `data:text/plain;charset=utf8;base64,...`; `dataUriToString()` reads any `data:` URI, base64 or percent-encoded. `tryGet()` and `tryIndexFromEnd()` return
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
- `roleDefinitions('Global Reader')` is ARM's name-based role helper for directory roles: `{ id, roleDefinitionId, name, templateId }`
  of the role (see [Differences from ARM](#differences-from-arm)).

The deployment functions:

- `deployment()` returns `{ name, properties: { templateHash, parameters, mode } }`, ARM's shape, and keeps the flat `templateHash`,
  `tenantId` and `mode` of earlier versions as aliases: `deployment().properties.mode` and `deployment().mode` are the same value.
  `deployment().name` is the deployment name (`-n`, or its default) in `what-if`, `create` and `delete`, also where names, ids and
  `dependsOn` are worked out before anything is read; only `validate`, which has no deployment name, sees `validate`.
- `tenant()` returns `{ countryCode, displayName, id, tenantId, defaultDomain }` (`id` is `/tenants/<tenantId>`).
- `deployer()` returns `{ objectId, tenantId, userPrincipalName }` of the signed-in principal (`userPrincipalName` is empty for an
  app-only sign-in).
- `environment()` returns `{ name, graph, portal, resourceManager, authentication: { loginEndpoint, audiences, tenant, identityProvider },
  suffixes: { keyvaultDns } }` for the selected cloud; `graph` is Microsoft Graph.

`deployment()`, `tenant()`, `deployer()` and `environment()` come from the sign-in check that runs before anything is evaluated, so they
are legal everywhere, parameter defaults and variables included; offline (`deployment validate`) they, or the parts that need the tenant,
are deploy-time unknowns. `appRole`, `permissionScope`, `roleTemplate` and `roleDefinitions` may ask the tenant while resources are
applied, so they are legal in resource properties, names and outputs but not in parameter defaults, variables or user functions
(`FunctionNotAllowedHere`). `utcNow()` and `newGuid()` are legal only as parameter defaults.

### `reference()` and `resourceId()`

- `reference('name')` or `reference(resourceId('Microsoft.Entra/type', 'name'))` returns the **live Graph object** of a template
  resource (`id`, `appId`, `uniqueName`, …) and adds an implicit dependency, except inside relationship properties, where it is
  evaluated in the edge pass after all objects exist and never creates a dependency (so mutual declarations are never cycles).
  In what-if, the object of a resource that does not exist yet is a `(deploy-time)` placeholder.
- `reference(resourceId(...), '2026-10-01-preview')` (with an `apiVersion`) looks up an object that is **not** in the template: no
  dependency, an error if it does not exist. Variables may do this; they may not reference template resources. ARM refuses `reference()`
  in variables altogether ([best practices](https://learn.microsoft.com/azure/azure-resource-manager/templates/best-practices#variables)); EARM allows this existing-object form there on purpose
  (templates use it to look up a fixed object once), and refuses the other forms in variables as ARM does.
- `reference(nameOrId, apiVersion, 'Full')` ([reference](https://learn.microsoft.com/azure/azure-resource-manager/templates/template-functions-resource#reference)) returns ARM's full resource object,
  reduced to what applies to Entra: `{ "apiVersion", "properties", "resourceId", "isTemplateResource" }` (plus `symbolicName` for a
  languageVersion 2.0 symbolic reference), where `properties` is what the short form returns and `resourceId` is the EARM id
  (`/providers/Microsoft.Entra/...`, as `resourceId()` renders it). ARM's `location`, `sku`, `subscriptionId`, `resourceGroupName` and
  the provisioning fields do not exist for Entra objects. `'Full'` is compared case-insensitively; any other third argument is an error.
- With languageVersion 2.0, `reference()` and `references()` take symbolic names ([languageVersion 2.0](#languageversion-20)).
- A bare `reference('name')` needs the name to be unique in the template, else `AmbiguousReference`.
- A resource whose `name` is `[reference('app').appId]` gets a *symbolic id* during static evaluation, so everything that refers
  to it computes the same id before anything exists ([engine.md](engine.md#symbolic-names)).
- An instance of a `copy` loop is referenced by its **evaluated name**, from anywhere in the template (a single resource, another loop,
  a property copy, an output): `reference(format('pg-{0}-approvers', parameters('scopes')[1]))`. The name only has to be computable
  before deployment from literals, parameters, variables and `copyIndex()`; the dependency is on that one instance, not on the loop
  ([Copy loops](#copy-loops)).
- The target must be computable before deployment: a target built from another resource's live values is
  `ReferenceNotStaticallyResolvable`, and so is one built from a lambda variable (ARM does not support lambda variables inside
  `reference()`); build such lists with a property or output copy loop instead.

### Copy loops

ARM's four iterations, all supported ([Learn: resource iteration](https://learn.microsoft.com/azure/azure-resource-manager/templates/copy-resources),
[property iteration](https://learn.microsoft.com/azure/azure-resource-manager/templates/copy-properties),
[output iteration](https://learn.microsoft.com/azure/azure-resource-manager/templates/copy-outputs),
[variable iteration](https://learn.microsoft.com/azure/azure-resource-manager/templates/copy-variables)). Every count is at most 800, may be 0,
and must be computable before deployment from literals, parameters and variables (an error otherwise).

| Iteration | Where | Shape | `copyIndex()` |
|---|---|---|---|
| Resource | a resource | `"copy": { "name", "count", "mode", "batchSize" }` | `copyIndex()` or `copyIndex('name')`, optional offset |
| Property | any object inside a resource's `properties` | `"copy": [ { "name": "<property>", "count", "input" } ]`: one array property per entry, `input` evaluated once per element | `copyIndex('<property>')` only (the name is required: `copyIndex()` without one still means the resource loop) |
| Output | an output, instead of `value` | `"copy": { "count", "input" }`: the output is the array of `input` evaluated once per element | `copyIndex()` |
| Variable | the top of `variables` | `"copy": [ { "name": "<variable>", "count", "input" } ]`: one array variable per entry (a clash with another variable is `DuplicateDefinition`) | `copyIndex('<variable>')`, optional offset |
| Variable property | any object inside a variable's value | the property iteration shape: one array property per entry | `copyIndex('<property>')` |

**References.** During static evaluation every instance of a resource loop whose count is computable is indexed under its evaluated name,
so `reference()` and `resourceId()` find one instance exactly as they find a single resource. A reference to instance *k* creates a
dependency on instance *k* only; `dependsOn: ["<loop name>"]` waits for every instance. Inside a property or output loop each element
is evaluated with its own `copyIndex()`, so a list of references built by property iteration depends on exactly the instances it names.
When a loop's count is only known at deployment (it uses a `reference()`), its instances cannot be referenced by name
(`ReferenceTargetNotFound`, naming the loop); when it waits only for a parameter that has no value offline, `deployment validate`
defers the reference with `ReferenceDependsOnMissingParameter`.

```json
"properties": {
  "copy": [ {
    "name": "memberOf",
    "count": "[length(parameters('scopes'))]",
    "input": "[reference(format('pg-{0}-approvers', parameters('scopes')[copyIndex('memberOf')].key)).id]"
  } ]
}
```

```json
"outputs": {
  "approverGroups": {
    "type": "array",
    "copy": {
      "count": "[length(parameters('scopes'))]",
      "input": { "scope": "[parameters('scopes')[copyIndex()].key]", "id": "[reference(format('pg-{0}-approvers', parameters('scopes')[copyIndex()].key)).id]" }
    }
  }
}
```

**Nested loops.** ARM has no nested resource loops; a loop over two dimensions (scopes x tiers) is **one** loop over the flattened
cartesian product. Either build the product as a variable and index it:

```json
"variables": {
  "scopeTiers": "[flatten(map(parameters('scopes'), lambda('s', map(variables('tiers'), lambda('t', createObject('scope', lambdaVariables('s').key, 'tier', lambdaVariables('t'), 'uniqueName', format('pg-{0}-{1}-access', lambdaVariables('s').key, lambdaVariables('t'))))))))]"
},
"resources": [ {
  "copy": { "name": "accessGroups", "count": "[length(variables('scopeTiers'))]" },
  "type": "Microsoft.Entra/groups", "apiVersion": "2026-10-01-preview",
  "name": "[variables('scopeTiers')[copyIndex()].uniqueName]",
  "properties": { "displayName": "[variables('scopeTiers')[copyIndex()].uniqueName]" }
} ]
```

or compute both indexes from `copyIndex()` with `div` and `mod`:
`count` = `[mul(length(parameters('scopes')), length(variables('tiers')))]`, scope = `parameters('scopes')[div(copyIndex(), length(variables('tiers')))]`,
tier = `variables('tiers')[mod(copyIndex(), length(variables('tiers')))]`. The instances, their references, their dependencies and output
loops over the same array then work like any other loop. A property copy inside a resource loop is the other legitimate nesting:
each resource instance builds its own array (`copyIndex()` for the resource, `copyIndex('<property>')` for the element).
[`samples/scoped-access-groups.entra.json`](../samples/scoped-access-groups.entra.json) puts all of it together.

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
| Nesting of a **value** an expression builds (`createArray`, `createObject`, `array` of a non-array, `union`, `items`, `map`, user function outputs, a wrapped parameter) | 64 levels. The JSON output of a command allows 256, so a 64-level value fits inside the structure around it. | `ValueTooDeep` |
| Nesting of one expression (calls, indexers, lambda bodies) | 100 levels | `ExpressionTooDeep` |
| Length of one expression | 24,576 characters | `ExpressionTooLong` |
| Length of one string a function produces | 1 MiB (1,048,576 characters) | `EvaluationBudgetExceeded` |
| Elements of one array or object | 100,000 | `EvaluationBudgetExceeded` |
| Nodes (containers and leaves) of one value | 1,000,000 | `EvaluationBudgetExceeded` |
| `copy.count` of one resource | 800 (the ARM limit) | `ExpansionLimitExceeded` |
| Resource instances of a template, all `copy` loops together | 5,000 | `ExpansionLimitExceeded` |
| Steps of a run (function calls plus lambda iterations) | 50,000,000 | `EvaluationBudgetExceeded` |
| Value nodes produced by a run / characters of text produced by a run | 5,000,000 / 134,217,728 (128 Mi). Counts values that are produced, not values that are read: a variable is evaluated once per evaluation and counted once, and `variables('x')[copyIndex()].name` counts only the element it selects (so do `parameters('x')[i]` and `lambdaVariables('x').y`). Passing a whole variable to a function (`length(variables('x'))`) copies it and counts its nodes. | `EvaluationBudgetExceeded` |
| Memory held by evaluated variables (string characters at 2 bytes each plus a fixed cost per node) | 256 MiB per run | `EvaluationBudgetExceeded` |
| Variables reading variables (a chain `v1` uses `v0`, `v2` uses `v1`, ...) | 128 levels | `VariableChainTooDeep` |
| `format()` | The format string is parsed by EARM: a specifier is at most 100 characters; a standard numeric specifier (`D N F X G P E`, either case) has a precision of at most 100; a string argument ignores its specifier (as in .NET). Padded items and arguments are added up against the 1 MiB string limit as they are written. | `InvalidFunctionArguments`, `EvaluationBudgetExceeded` |
| `indexOf`, `lastIndexOf`, `contains` on strings | Linear time (case-insensitive search of the folded text); the length of the text and the needle is charged to the step budget (1 step per 1,024 characters), as is every other string operation | `EvaluationBudgetExceeded` |
| Time one evaluation (a variable, the properties of a resource, an output) may take | 60 seconds; `--evaluation-timeout <seconds>` or `EARM_EVALUATION_TIMEOUT`, `0` = no clock. The clock runs from the start of the evaluation, so the time a deployment spends on the network between evaluations never counts; the step and node limits above bound the total work. | `EvaluationBudgetExceeded` |

ARM documents no work budget of this kind (its limits are on the template itself, such as its 4 MB size and 800 instances per copy loop).
EARM keeps the step and node budgets for the whole run, across every evaluation pass of one command (`what-if` expands, validates and plans the
template, so it evaluates each variable about four times, once per pass), because they are what stops a template from consuming unbounded time and
memory when the expression engine runs locally. They are sized far above real templates: a template of about fifty data-heavy variables and
150 resource instances needs roughly 5% of the node budget for a complete `what-if`.

A per-value limit fails that value (and what depends on it) and the run goes on, so one oversized variable does not hide the other findings.
A work limit ends the run: it stays tripped, and every later expression fails fast with the same message. `union` and `intersection` are linear
in the number of elements, so they stay within the limits for any array that is within them. An unknown (deploy-time) value's symbol, built
from the symbols of its arguments, is cut to 1,024 characters (the rest replaced by a hash) so `concat(x, x)` chains over an unknown cannot grow it without bound.

Repeated property names in an object (`"state": "disabled", "state": "enabled"`) are rejected when the file is loaded (`DuplicateJsonKey`):
JSON readers disagree about which value wins. **Names that differ only in letter case count as repeated** (`"state"` and `"State"`): EARM looks
properties up ignoring case and takes the first match, so a guard (the `--allow-ca-enforce` rule on a conditional access policy, for one) would
judge one key and the request would carry both. The same holds for an object an expression builds (`union`, `createObject`, `items`, `toObject`):
it fails with `DuplicateJsonKey` naming both spellings when its value is used. The names under `parameters`, `variables` and `outputs` are the one
exception (case variants there are `DuplicateDefinition`).

### Input limits

A template file and a parameter file are each read only up to **8 MiB (8,388,608 bytes)**; a larger file is refused with `InputTooLarge`
(exit code 1) from its size, before it is read, and the read itself stops at the limit for a file that reports no size. A document may also hold
at most **2,000,000 JSON values** (objects, arrays and leaves); they are counted in one cheap pass over the text before anything is built, so a
file of millions of tiny nodes is refused without allocating its tree. Files must be UTF-8 (a byte-order mark is accepted); UTF-16 and UTF-32
are named as such instead of failing with a byte-level syntax error. A directory passed as the template file is reported as a directory.

**ARM limits are warnings.** Azure Resource Manager limits a template to 256 parameters, 512 variables, 64 outputs, 800 resources (copy counts
included) and 4 MB ([template limits](https://learn.microsoft.com/azure/azure-resource-manager/templates/best-practices#template-limits); the
[syntax](https://learn.microsoft.com/azure/azure-resource-manager/templates/syntax#variables) page still says 256 variables, EARM warns from the
higher, more recent figure). EARM reports each as an `ArmLimitExceeded` warning and accepts the template; its own hard limits are the ones in
this section and in [Evaluation limits](#evaluation-limits) (8 MiB of input, 5,000 resource instances, the evaluation budget). The resource count
uses the literal `copy.count` of each loop; a count computed by an expression counts once.

## Parameters and parameter files

Parameter types: `string`, `securestring`, `int`, `bool`, `object`, `secureObject`, `array`, compared case-insensitively as in ARM (`secureString` and `SecureObject` are accepted, in the schema too). An `int` accepts any JSON number whose exact
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

An output may declare `"copy": { "count", "input" }` instead of `value`: its value is then the array of `input` evaluated once per
element, with `copyIndex()` bound to the position ([Copy loops](#copy-loops)).

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
  `permissions` holds `application` (there is no `delegated` list: the delegated manifest uses the providers' rules, or these names for a type without a provider), `roles` (Entra roles that are alternatives), `rolesAllOf` (Entra roles needed together: an array of
  parts, each an array of alternatives, for example a catalog resource's `[["Identity Governance Administrator"], ["Groups Administrator", …]]`),
  `conditional` (per declared property) and `notes`; [permissions.md](permissions.md) describes how `earm permissions` reads them.
  The property schemas are generated from one table by `tools/New-TypeSchemas.ps1`, which keeps the overlay text untouched.

`earm schema install` writes copies to `%LOCALAPPDATA%\EArm\schemas\2026-10-01\` (with the cross-file references rewritten to
local `file:///` URIs, because `https://schema.earm.proxylayer.net/schemas/…` is not published) and adds
`{ "fileMatch": ["**/*.entra.json", "**/*.entra.jsonc"], "url": "file:///…/deploymentTemplate.json" }` to `json.schemas` in the
workspace's `.vscode/settings.json` (or the user settings with `--global`). See [cli.md](cli.md#schema).
