# Template format (schema version 2026-10-01)

An EntraARM template is a JSON document that looks and behaves like a classic Azure Resource Manager template, but
its resources are Microsoft Entra objects under the `Microsoft.Entra` provider namespace. This page is the
reference for the format; [edges.md](edges.md) covers relationship properties, [permissions.md](permissions.md) the
Graph permissions a template needs, [cli.md](cli.md) the verbs, and [diagnostics.md](diagnostics.md) every
validation code. Worked templates are in [`samples/`](../samples/README.md).

- File convention: `*.entra.json` (or `*.entra.jsonc`). The parser accepts `//` and `/* */` comments, trailing commas and
  a UTF-8 byte-order mark.
- Editor support: `entraarm schema install` maps the JSON Schemas to `**/*.entra.json` in VS Code
  (see [JSON Schemas](#json-schemas)).
- Validate offline at any time: `entraarm deployment validate -f template.entra.json -p @params.json`.

## Envelope

```json
{
  "$schema": "https://entraarm.dev/schemas/2026-10-01/deploymentTemplate.json#",
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
| `$schema` | yes | Exactly `https://entraarm.dev/schemas/2026-10-01/deploymentTemplate.json#` (the trailing `#` is optional); `UnrecognizedSchema` otherwise. |
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
(`NameMappedPropertyConflict`).

| Name kind | Types | Meaning |
|---|---|---|
| `uniqueName` | applications, groups | Graph `uniqueName` alternate key; upsert is `PATCH /x(uniqueName='…')` with `Prefer: create-if-missing`. Immutable. A brownfield object gets one with `entraarm state adopt --type … --id … --name …`. |
| `userPrincipalName` | users | `local@domain`. |
| `appId` | servicePrincipals | The application (client) id GUID, or `[reference('app').appId]`. |
| child name | federatedIdentityCredentials, passwordCredentials | `parent/child`; the child is the credential's own name. Nested children get the parent's name prepended by the loader. |
| `displayName` | named locations, CA policies, authentication strengths, administrative units, role definitions | Looked up by `displayName` (0 hits: create, 1: pin the id in state, more than one: error; resolve with `state adopt --resource-id … --object-id …`). |
| `id` | authenticationContextClassReferences | `c1` … `c99`; the name is the Graph id. |
| GUID | appRoleAssignments, oauth2PermissionGrants, roleAssignments | A deterministic GUID chosen by the author with `guid(...)`, as with `Microsoft.Authorization/roleAssignments`. The Graph object is found by its composite properties and its id pinned in state; a changed composite under the same name is `Modify`/`Recreate`. |

The resource id is `/providers/Microsoft.Entra/<type>/<name>[/<childType>/<childName>]` (tenant scope, ARM style):
deterministic, the key of the state file and what the what-if output prints. `resourceId('Microsoft.Entra/applications/passwordCredentials', 'app', 'ci-secret')` builds it.

### Differences from ARM

| ARM | EntraARM |
|---|---|
| `location`, `sku`, `kind`, top-level `tags`, `identity`, `scope`, resource group and subscription | Not present: Entra objects have none of them (Graph `tags` live inside `properties`). `resourceGroup()`, `subscription()`, `managementGroup()`, `extensionResourceId()`, `subscriptionResourceId()`, `tenantResourceId()`, `listKeys()`, `listSecrets()`, `providers()` and `pickZones()` are errors (`UnsupportedArmFunction`) with a pointer to the alternative. |
| PUT replaces the resource | Graph is merge-PATCH: only the **declared** properties are applied; omitted ones are untouched. Arrays and nested objects are replaced whole when declared. |
| Implicit parent dependency of a child must be explicit | Inferred for nested children (a superset of the ARM rule). |
| `existing` resources | `reference(resourceId('Microsoft.Entra/…', 'name'), '2026-10-01-preview')` looks up an object that is not in the template (no dependency; an error when not found). |
| Relationships live in separate resources | Arrays inside `properties`, declarable from either side; see [edges.md](edges.md). |

## Resource type catalog

`Microsoft.Entra/…` types of schema version 2026-10-01. The source of truth is the `x-entraarm` overlay at the end of each
schema file (`entraarm schema print <type>`); the executor loads the same file at runtime, so this table cannot drift from
behaviour.

| Type | `name` is | Graph path | Immutable (error, or recreate for cheap types) | Edge properties | Required on create |
|---|---|---|---|---|---|
| `applications` | `uniqueName` | `/applications` | `uniqueName`, `appId`, `id`, `publisherDomain` | `owners` | `displayName` |
| `applications/federatedIdentityCredentials` | credential `name` (parent: application) | `/applications/{id}/federatedIdentityCredentials` | `name` | none | `issuer`, `subject` (`audiences` defaults to `["api://AzureADTokenExchange"]`) |
| `applications/passwordCredentials` | credential `displayName` (parent: application) | `addPassword` / `removePassword` | everything (replaced, never edited) | none | nothing (`endDateTime` defaults to +180 days) |
| `servicePrincipals` | `appId` | `/servicePrincipals` | `appId`, `servicePrincipalType` | `owners`, `memberOf`, `ownerOf`, `appRoleAssignments`, `directoryRoles` | nothing |
| `groups` | `uniqueName` | `/groups` | `uniqueName`, `mailNickname`, `mailEnabled`, `isAssignableToRole`, `groupTypes` (Unified presence), `visibility` (HiddenMembership) | `members`, `owners`, `memberOf`, `administrativeUnits`, `appRoleAssignments`, `directoryRoles`, `assignedLicenses` | `displayName`, `mailEnabled`, `securityEnabled` (`mailNickname` derived from the name) |
| `users` | `userPrincipalName` | `/users` | `userPrincipalName` | `manager`, `memberOf`, `ownerOf`, `administrativeUnits`, `appRoleAssignments`, `directoryRoles`, `assignedLicenses` | `displayName` (`mailNickname`, `accountEnabled`, `passwordProfile` defaulted) |
| `roleDefinitions` | `displayName` (custom roles only) | `/roleManagement/directory/roleDefinitions` | `templateId` | none | `rolePermissions[].allowedResourceActions` (`isEnabled` defaults to true) |
| `conditionalAccess/authenticationContextClassReferences` | the id, `c1`…`c99` | `/identity/conditionalAccess/authenticationContextClassReferences` | `id` | none | `displayName` (`isAvailable` defaults to true) |
| `conditionalAccess/authenticationStrengths` | `displayName` (custom only) | `/policies/authenticationStrengthPolicies` | `policyType` | none | `allowedCombinations` |
| `appRoleAssignments` | deterministic GUID | `/servicePrincipals/{resourceId}/appRoleAssignedTo` | everything; composite key (`principalId`, `resourceId`, `appRoleId`) | none | the three key properties |
| `oauth2PermissionGrants` | deterministic GUID | `/oauth2PermissionGrants` | key fields (`clientId`, `resourceId`, `consentType`, `principalId`); only `scope` is mutable | none | key + `scope` |
| `conditionalAccess/namedLocations` | `displayName` | `/identity/conditionalAccess/namedLocations` | `@odata.type` | none | `@odata.type` and `ipRanges` or `countriesAndRegions` |
| `conditionalAccess/policies` | `displayName` | `/identity/conditionalAccess/policies` | none | none | `state`, `conditions.users`, `conditions.applications`, `grantControls` or `sessionControls` |
| `administrativeUnits` | `displayName` | `/directory/administrativeUnits` | `isMemberManagementRestricted` | `members` | nothing |
| `roleAssignments` | deterministic GUID | `/roleManagement/directory/roleAssignments` | everything; composite key (`roleDefinitionId`, `principalId`, `directoryScopeId`) | none | the three key properties (`directoryScopeId` defaults to `/`) |

An immutable change is a hard error for applications, groups and users; for the cheap types (assignments, grants, secrets,
federated credentials, named location kinds) it is `Modify` with `mode: recreate`. Soft-deleted objects (applications,
Microsoft 365 groups, users, service principals, administrative units) keep their keys for 30 days: a create that finds one
fails with a hint unless `--on-soft-deleted-conflict restore`.

### Property vocabulary

`properties` is the Graph representation of the object (camelCase, as Graph returns it) plus a few EntraARM extensions.
`entraarm schema print <type>` shows the vocabulary of one type; the JSON Schemas are the reference for names, types and
enumerations. In short:

- **Nested objects** (`web`, `api`, `spa`, `publicClient`, `conditions`, `grantControls`, `sessionControls`, `passwordProfile`) are
  compared whole and written whole; a sub-property that exists live but is absent from the template is sent as explicit
  `null` (except where the overlay says Graph refuses that, `nullable`).
- **Arrays of scalars** (`identifierUris`, `tags`, `redirectUris`, `builtInControls`, `members`) are compared as sets and written whole.
- **Arrays of objects** are compared by a member key (`id` for `appRoles` and `oauth2PermissionScopes`, `cidrAddress` for `ipRanges`)
  and written whole. An omitted app role or scope `id` is derived deterministically (UUIDv5 of `uniqueName` and `value`) and recorded in state.
- **Passthrough**: every object keeps `additionalProperties: true`. A property the schema does not list is still sent to
  Graph unchanged; it is simply not validated or documented.
- **EntraARM-only properties** (never sent to Graph): `rotationTag` and `rotateBeforeDays` on `passwordCredentials` (see below),
  and the sugar properties that are rewritten before the key is computed: `scopes` on `oauth2PermissionGrants` (→ `scope`),
  `resourceAppId` / `appRole` on `appRoleAssignments` and in `appRoleAssignments` edge entries (→ `resourceId` / `appRoleId`),
  `role` / `administrativeUnit` on `roleAssignments` and in `directoryRoles` edge entries (→ `roleDefinitionId` / `directoryScopeId`), and
  `skuPartNumber` in `assignedLicenses` (→ `skuId`).
- **Secrets**: `passwordProfile.password` (users) and `secretText` (password credentials) are secure leaves: tainted like a
  `securestring` parameter, `***` in every rendering.

#### Password credential rotation

Graph never edits a client secret, and returns its text once. `applications/passwordCredentials` therefore:

- matches the credential among the application's secrets by `displayName` (the child name);
- is `NoChange` unless `rotationTag` differs from the one recorded in state, a declared `endDateTime` / `startDateTime` differs, the
  secret expired, or fewer than `rotateBeforeDays` (default 30) remain; then it **adds the new secret first, then removes the old one**;
- publishes `secretText` on the creating or rotating run only (`reference('…/passwordCredentials/ci-secret').secretText` flowing into a
  `securestring` output); a rerun reports that output as `null` with the `SecretNotAvailableOnRerun` warning and does not fail.
  Change `rotationTag` to rotate and obtain a new secret.

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
| Array and object | `array createArray createObject first last take skip union intersection range items json null objectKeys flatten` and the lambda functions `map filter reduce sort toObject groupBy lambda lambdaVariables` |
| Resource and deployment | `resourceId reference copyIndex deployment tenant environment parameters variables` and user functions `namespace.fn(...)` |
| **Entra** | `wellKnownApp(name)`, `appRole(app, value)`, `permissionScope(app, value)`, `roleTemplate(name)` |

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

1. Parse. 2. Parameters: parameter file, then `-p name=value`, then `defaultValue`. 3. Variables and user functions (lazily, memoised).
4. Resource names and `dependsOn`, then the dependency graph. 5. Resources in topological batches, `condition` and `properties` just in time.
6. Outputs.

`securestring` / `secureObject` parameters, `secretText`, `passwordProfile.password` and anything computed from them are *tainted*:
they may flow into resource properties and `securestring` / `secureObject` outputs only (not into names, `condition`, `copy.count`,
`dependsOn` or plain outputs, which are written to state and logs). They are `***` in what-if, logs and diagnostics; the plain text
exists only in Graph request bodies and in `--reveal-secure-outputs` / `--secure-outputs-file`.

## Parameters and parameter files

Parameter types: `string`, `securestring`, `int`, `bool`, `object`, `secureObject`, `array`. An `int` accepts any JSON number whose exact
value is a whole number in the 64-bit range, however it is spelled (`3`, `3.0`, `1.5e3`, `-0.0`); a fraction (`3.5`) or a number outside
the range is a `ParameterValueTypeMismatch`, and `copy.count` / `copy.batchSize` follow the same rule. A value is supplied, in precedence order, by
`-p name=value` (inline; converted to the parameter type), by a parameter file (`-p @file.json`, later files and values win), then by
`defaultValue`. An unknown parameter name is an error (`UnknownParameter`); a value of the wrong type or outside `allowedValues` is
reported (`ParameterValueTypeMismatch`, `ParameterValueNotAllowed`). A parameter without a value is a warning in `deployment validate`
and an error for `create` and `what-if`.

```json
{
  "$schema": "https://entraarm.dev/schemas/2026-10-01/deploymentParameters.json#",
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
preflight with `az keyvault secret show --vault-name <vault> --name <secretName> [--version <v>] --query value`, through the same
Azure CLI session; the vault name is the last segment of `keyVault.id` (a bare name is accepted). `secretVersion` is optional and must be a string
(absent, `null` or blank means the latest version); a number or any other non-string is an `InvalidParameterFile` error with the
path and position, never silently replaced by the latest version. The value is **always secure
(tainted)**, whatever the declared parameter type. The identity behind the Azure CLI session needs read access to the secret (`Key Vault
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

## JSON Schemas

The schemas live in [`schemas/2026-10-01/`](../schemas/2026-10-01) and are embedded in the CLI:

| Schema | Command |
|---|---|
| Template envelope (`deploymentTemplate.json`, draft 2020-12): resource keys, `if`/`then` per `type` selecting the per-type schema | `entraarm schema print template` |
| Parameter file (`deploymentParameters.json`) | `entraarm schema print parameters` |
| One per resource type, `types/Microsoft.Entra/<type>.json` (path segments joined with `.`): property schemas plus the `x-entraarm` overlay | `entraarm schema print Microsoft.Entra/conditionalAccess/policies` |

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
- `x-entraarm` (`type`, `graphPath`, `apiVersions`, `nameMapsTo`, `nameKind`, `namePattern`, `keyProperties`, `requiredOnCreate`,
  `defaults`, `immutable`, `createOnly`, `secure`, `relationships`, `memberKey`, `stripOnRead`, `nullable`, `permissions`) drives the executor.
  The property schemas are generated from one table by `tools/New-TypeSchemas.ps1`, which keeps the overlay text untouched.

`entraarm schema install` writes copies to `%LOCALAPPDATA%\EntraArm\schemas\2026-10-01\` (with the cross-file references rewritten to
local `file:///` URIs, because `https://entraarm.dev/schemas/…` is not published) and adds
`{ "fileMatch": ["**/*.entra.json", "**/*.entra.jsonc"], "url": "file:///…/deploymentTemplate.json" }` to `json.schemas` in the
workspace's `.vscode/settings.json` (or the user settings with `--global`). See [cli.md](cli.md#schema).
