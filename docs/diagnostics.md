# Diagnostic codes

Codes emitted by `earm deployment validate` (offline). The constants live in
`src/EArm.Core/Diagnostics/DiagnosticCodes.cs`; keep this table in sync with it.

Exit codes: `0` no errors (warnings allowed), `2` at least one error, `1` the template or a parameter
file could not be loaded (the "fatal" rows below) or an argument was malformed.

Table output prints one line per diagnostic: `<severity> <Code> [<json path>] (<line>:<column>): <message>`,
prefixed with the file name when the finding is in the parameter file rather than the template.
JSON output (`-o json`) lists the same fields under `diagnostics[]`.

## Load failures (fatal, exit code 1)

| Code | Severity | Meaning |
|---|---|---|
| `FileNotFound` | Error | The template or parameter file does not exist. |
| `InvalidJson` | Error | The file is not valid JSON (comments and trailing commas are allowed; anything else is not). Line/column point at the parse error. JSON nested deeper than 64 levels is rejected the same way. A string with a lone surrogate escape (for example a backslash-u escape of D800 not followed by a low surrogate) is also reported here, with the path and the position of the escape: the template still loads (the escape is read as U+FFFD) but validation fails with exit code 2; in a parameter file it is a load failure (exit code 1). |
| `TemplateRootNotObject` | Error | The template root is not a JSON object. |
| `InvalidParameterFile` | Error | The parameter file is not in the `deploymentParameters` shape (an entry without exactly one of `value`/`reference`, a Key Vault reference without `keyVault.id`/`secretName`, a `secretVersion` that is present but not a string, duplicate names...). |
| `InvalidArgument` | Error | (CLI only) A `-p` token is neither `@file.json` nor `name=value`. |

## Load diagnostics (non-fatal)

| Code | Severity | Meaning |
|---|---|---|
| `NestingTooDeep` | Error | A nested child resource has its own non-empty `resources` array. v1 supports two levels (parent + child); deeper resources are ignored. Declare them at the top level with a `parent/child` name. |
| `DuplicateDefinition` | Error | Two parameters, variables or outputs have the same name ignoring case (ARM names are case-insensitive). |
| `UnknownParameter` | Error | A parameter file or `-p` value names a parameter the template does not declare. |

## JSON Schema

| Code | Severity | Meaning |
|---|---|---|
| `SchemaViolation` | Error | The template (or parameter file) violates the embedded JSON Schema: unknown resource key, wrong value type, bad `apiVersion` format, etc. The message names the schema keyword. Findings that a semantic code already covers at the same path are not repeated. The per-type schemas (`schemas/2026-10-01/types`, see [format.md](format.md#json-schemas)) add enumerations, patterns, nested object shapes and missing `required` members inside a resource's `properties` (for example `resources[3].properties.directoryRoles[0]`); a missing required key of the envelope itself is reported with its own code (`MissingRequiredProperty`, `MissingResourceType`, ...), never as a schema violation. |

## Envelope

| Code | Severity | Meaning |
|---|---|---|
| `UnrecognizedSchema` | Error | `$schema` is missing or is not `https://schema.earm.proxylayer.net/schemas/2026-10-01/deploymentTemplate.json#`. |
| `InvalidContentVersion` | Error | `contentVersion` is not a literal of the form `major.minor.build.revision`. |
| `MissingRequiredProperty` | Error | A required key is missing: root `contentVersion`/`resources`, output `type`/`value`, function `namespace`/`members`/`output`, function parameter `name`/`type`. |
| `UnknownTopLevelProperty` | Warning | A top-level key is not part of the format (`$schema`, `contentVersion`, `metadata`, `parameters`, `variables`, `functions`, `resources`, `outputs`) and is ignored. |

## Resources

| Code | Severity | Meaning |
|---|---|---|
| `MissingResourceType` | Error | A resource has no `type`. |
| `MissingApiVersion` | Error | A resource has no `apiVersion`. |
| `MissingResourceName` | Error | A resource has no `name`. |
| `UnknownResourceType` | Error | The type is not in the catalog (`Microsoft.Entra/...`; see the message for the known list). |
| `UnknownApiVersion` | Error | The type exists but does not support this `apiVersion`. `2026-10-01-preview` (Graph v1.0) and `2026-10-01-preview.beta` (Graph beta) are the only valid values; the retired plain `2026-10-01` gets a rename hint (`apiVersion '2026-10-01' was renamed '2026-10-01-preview' ...`). |
| `InvalidResourceName` | Error | A literal name has the wrong shape for its type: wrong number of `/` segments (child types need `parent/child`), empty segment, non-GUID name for `appRoleAssignments`/`oauth2PermissionGrants`/`roleAssignments`/`servicePrincipals`, UPN without `@`, `authenticationContextClassReferences` name not `c1`..`c99`, federated credential name not matching `^[A-Za-z][A-Za-z0-9-]{2,119}$`. |
| `ParentChildTypeMismatch` | Error | A child type is nested under (or spelled with) the wrong parent type, e.g. `federatedIdentityCredentials` under a group. |
| `DuplicateResourceId` | Error | Two declarations with literal names produce the same resource id (`/providers/Microsoft.Entra/<type>/<name>`). Names compare case-sensitively, types case-insensitively. |
| `NameMappedPropertyConflict` | Error | `properties` declares the Graph property the name is injected into (`uniqueName`, `userPrincipalName`, `appId`, `displayName`, `id`, child `name`) with a different literal value. |
| `DynamicMembershipConflict` | Error | `members` declared on a dynamic group (`groupTypes` contains `DynamicMembership`) or a dynamic administrative unit (`membershipType: Dynamic`). |
| `DirectoryRolesRequireRoleAssignableGroup` | Error | `directoryRoles` on a group without a literal `isAssignableToRole: true`. |
| `AssignedLicensesRequireUsageLocation` | Error | `assignedLicenses` on a user that declares no `usageLocation`. |
| `InvalidCopy` | Error | `copy` is missing `name`/`count`, `name` is not a literal string, `count` is negative or not an integer, `mode` is not `serial`/`parallel`, or `batchSize` is not positive. |

## Parameters

| Code | Severity | Meaning |
|---|---|---|
| `InvalidParameterType` | Error | A parameter (or user-function parameter) has no `type` or an unknown one; allowed: `string`, `securestring`, `int`, `bool`, `object`, `secureObject`, `array`. |
| `AllowedValueTypeMismatch` | Error | An `allowedValues` entry does not match the parameter type. |
| `InvalidParameterConstraint` | Error | `minValue`/`maxValue` on a non-`int` parameter, `minLength`/`maxLength` on a type other than `string`/`securestring`/`array`, a negative length, a non-integer bound, or min greater than max. |
| `DefaultValueTypeMismatch` | Error | The literal `defaultValue` does not match the parameter type. |
| `DefaultValueNotAllowed` | Error | The literal `defaultValue` is not in `allowedValues`. |
| `SecureParameterHasDefault` | Warning | A `securestring`/`secureObject` parameter has a default value. |
| `MissingParameterValue` | Warning | No value was supplied and the parameter has no default. A warning offline; deployment treats it as an error. |
| `ParameterValueTypeMismatch` | Error | A supplied value (file or `-p`) does not match, or cannot be converted to, the declared type. |
| `ParameterValueNotAllowed` | Error | A supplied value is not in `allowedValues`. |

## Outputs

| Code | Severity | Meaning |
|---|---|---|
| `InvalidOutputType` | Error | An output (or user-function output) has an unknown `type`. |

## Expressions: syntax and functions

Emitted by `EArm.Expressions` (`ExpressionValidator`), after the checks above. Messages end with
`(at offset N)`: the 0-based character offset inside the expression string, counting the opening `[`.

| Code | Severity | Meaning |
|---|---|---|
| `ExpressionSyntaxError` | Error | The `[...]` string does not match the expression grammar: unterminated string (`'` is escaped by doubling), bare identifier (`true`/`false`/`null` are functions: `true()`), floating-point literal, missing `)`/`]`, trailing text, bad lambda parameter list. |
| `ExpressionTooLong` | Error | The expression string exceeds 24,576 characters (ARM limit). |
| `ExpressionTooDeep` | Error | The expression nests more than 100 levels deep: calls, indexers, `lambda()` bodies and chained `.property` / `[index]` accesses are counted together. The message carries the offset where the limit was hit. Also reported when a hand-built expression tree is too deep for the stack to be walked, so a hostile template yields this diagnostic (exit code 2) instead of crashing the process. |
| `UnknownFunction` | Error | The function is not an ARM function EARM supports, or `ns.fn` names a namespace/member the template's `functions` section does not declare. |
| `UnsupportedArmFunction` | Error | An ARM function with no Entra meaning: `resourceGroup subscription managementGroup extensionResourceId subscriptionResourceId tenantResourceId listKeys listSecrets providers pickZones`. The message names the alternative. |
| `NotImplementedYet` | Warning | A function registered as `FunctionKind.NotImplementedYet` evaluates to a deploy-time placeholder. **Not emitted by the default registry any more**: lambdas (`lambda map filter reduce sort toObject groupBy lambdaVariables`), the Entra functions (`wellKnownApp appRole permissionScope roleTemplate tenant environment`) and user-defined functions are implemented (WI-16). Kept for registries that stage a function before implementing it. |
| `NonDeterministicFunctionNotAllowed` | Error | `utcNow()` or `newGuid()` outside a parameter `defaultValue` (ARM rule). |
| `FunctionNotAllowedHere` | Error | A function in a place the phase rules of plan section 1.5 forbid: `variables()` inside a parameter default value or a user-defined function; `appRole()`, `permissionScope()`, `roleTemplate()` or `tenant()` inside a parameter default, a variable or a user-defined function (they need the tenant, which is only consulted while resources are applied; `wellKnownApp()` is pure and allowed anywhere); `lambdaVariables()` outside the body of a lambda; a user-defined function called from inside another user-defined function (ARM rule). |
| `InvalidFunctionArguments` | Error | Wrong argument count or type for a function (`concat` of strings and arrays, `substring` on a number, `createObject` with an odd count...). Lambdas: a `lambda()` anywhere but a lambda slot of `map filter reduce sort toObject groupBy` (including as a user-function argument), a lambda slot holding something else, a lambda declaring the wrong number of parameters for its slot (`map`/`filter`: `element[, index]`; `reduce`: `current, next[, index]`; `sort`: `left, right`; `toObject`/`groupBy`: `element`) or the same name twice, `lambdaVariables('x')` naming no parameter of an enclosing lambda, a `filter`/`sort` lambda not returning a boolean, a `toObject`/`groupBy` key lambda not returning a string. Entra: `wellKnownApp()` with a name outside the embedded table (the message lists the names), `appRole()`/`permissionScope()` whose first argument is neither an appId (GUID) nor the name/resource id of a `Microsoft.Entra/applications` or `Microsoft.Entra/servicePrincipals` resource of the template. User functions: an argument whose value does not match the declared parameter type. |
| `ExpressionEvaluationFailed` | Error | The arguments had the right types but the operation failed: `substring` out of range, integer overflow, division by zero, invalid base64/JSON/date/duration, property or index not found, `int('abc')`, `toObject` producing a duplicate key (a duplicate secret-derived key is reported as `***`, never its text). Object keys are not taint-tracked, so a key computed from a secure value (`createObject`, `toObject`, `groupBy`) is masked in the result rather than written: the first distinct secret key becomes `***`, the next `***#2`, and so on (a clear key already named like that is skipped); equal secret keys still group or collide, the values stay secure, and a lookup by the real key text finds nothing. Entra lookups with no match in the tenant: `appRole()`/`permissionScope()` for an appId without a service principal or application, or a `value` the principal does not declare (the message lists the available values); `roleTemplate()` naming no role definition (a custom role declared in the same template is referenced as `reference('<name>').id`). User functions: a body that fails (reported at the call site; the message carries the body's offset and path) or an output that does not match the declared `output.type`. |

## Expressions: parameters, variables, copy

| Code | Severity | Meaning |
|---|---|---|
| `UndefinedParameter` | Error | `parameters('x')` names a parameter the template does not declare; inside a user-defined function body, `parameters('x')` names only the function's own parameters, so a template parameter name is reported here too. |
| `UndefinedVariable` | Error | `variables('x')` names a variable the template does not declare. |
| `VariableCycle` | Error | A variable refers to itself, directly or through other variables. |
| `ParameterDefaultCycle` | Error | A parameter default value refers to itself through `parameters()`. |
| `CopyIndexOutsideCopy` | Error | `copyIndex()` outside a resource with a `copy` loop, inside the `copy` block itself, or naming a loop that is not in scope. |

## Expressions: references and dependencies

| Code | Severity | Meaning |
|---|---|---|
| `ReferenceNotAllowedHere` | Error | `reference()` in a parameter default or a user-defined function, or a same-template `reference()` (no apiVersion) in a variable. Variables may only look up existing resources: `reference(resourceId(...), apiVersion)`. |
| `ReferenceNotStaticallyResolvable` | Error | The target of a `reference()` cannot be computed before deployment. It must follow from literals, parameters and variables, or name a resource whose own name is the same deploy-time computation (a name such as `[reference('app').appId]` is matched symbolically, whether written as `reference(reference('app').appId)` or `resourceId('...', reference('app').appId)`). Targets that depend on `copyIndex()` are resolved per iteration instead; targets that depend only on a parameter without an offline value are `ReferenceDependsOnMissingParameter`. |
| `ReferenceDependsOnMissingParameter` | Info | The target of a `reference()`/`resourceId()` or a `dependsOn` entry depends only on a parameter that has no value offline (not supplied and no default, or a Key Vault reference). Not an authoring error: validating without values cannot compute the target, so it is treated as a deferred (symbolic) target and resolved once the value is supplied (`-p` or a parameter file). |
| `ReferenceTargetNotFound` | Error | `reference(name)` / `reference(resourceId(...))` with a literal target names no resource of the template (pass an apiVersion to look up an existing resource), or an existing-resource lookup found nothing. A target whose evaluated name is only known at deployment is never reported here (see the two codes above). |
| `AmbiguousReference` | Error | `reference(name)` with a bare name that several resources of different types share; use `resourceId()`. |
| `InvalidResourceId` | Error | A `/providers/...` string (in `reference()` or `dependsOn`) is not a valid EARM resource id. |
| `DependsOnTargetNotFound` | Error | A `dependsOn` entry (resource id, `type/name`, bare name or copy loop name) does not name a resource of the template, or its value is only known at deployment (unless only a parameter value is missing offline: `ReferenceDependsOnMissingParameter`). |
| `AmbiguousDependsOn` | Error | A bare-name `dependsOn` entry matches several resources. |

## Expressions: evaluated resource keys and outputs

| Code | Severity | Meaning |
|---|---|---|
| `ApiVersionMustBeLiteral` | Error | `apiVersion` is an expression. |
| `ResourceNameNotString` | Error | The evaluated `name` is not a string. (`InvalidResourceName` is also raised when the evaluated name has the wrong segment count for its type.) |
| `ConditionNotBoolean` | Error | A resource or output `condition` evaluates to something other than a boolean. |
| `SecureValueInResourceKey` | Error | A secure (tainted) value flows into `name`, `condition`, `copy.count` or `dependsOn`; these are written to state and logs. |
| `SecureValueInPlainOutput` | Error | A secure value flows into an output that is not `securestring`/`secureObject`. |
| `DuplicateResourceId` | Error | (Also here) Two declarations produce the same id after evaluating expression names (literal pairs are reported by the core validator). |
| `InvalidCopy` | Error | (Also here) `copy.count` evaluates to a negative or non-integer value, or depends on `copyIndex()`. |

## Still not checked offline

Duplicate ids between `copy` iterations need copy expansion (WI-05); `memberOf` targeting a dynamic group,
non-existent edge targets and conflicting edge declarations need the live edge model and are reported by the
engine's edge pass as `EdgeValidationFailed` (WI-09).

## Engine codes (`what-if` and `create`)

Raised by `EArm.Engine` (`EngineDiagnosticCodes`) while planning and executing; they never appear in
`deployment validate`. Errors in the planning phase exit 2 (`ValidationFailedException`); failures of individual
resources are reported per operation (exit 1); the state codes map to their own exit codes.

| Code | Severity | Meaning |
|---|---|---|
| `DependencyCycle` | Error | The dependency graph (explicit `dependsOn`, parent, implicit `reference()`, symbolic-name inputs, serial copy chaining) has a cycle; the message carries the path. Move a `reference()` into a relationship property (applied after all objects exist) or drop a `dependsOn`. |
| `KeyVaultReferenceUnresolved` | Error / Warning | A parameter supplied as a Key Vault reference could not be resolved: `az keyvault secret show` failed (vault or secret missing, no access; the az message is redacted; exit 3), or the reference reached the planner unresolved (error for `create`, warning plus deploy-time placeholder for `what-if`). |
| `UnresolvedReference` | Warning | A `reference(resourceId(...), apiVersion)` lookup of an existing resource could not be performed (no provider for the type, GUID-named type, unresolvable key); the value stays a deploy-time placeholder. |
| `ConditionUnresolved` | Error (resource) | A resource `condition` depends on a value that is not available when the resource is applied, or is not a boolean. |
| `UnresolvedDeployTimeValue` | Error (resource) | The evaluated properties still contain deploy-time-unknown leaves when the resource is applied (a referenced resource was not deployed, or a function is a stub). |
| `SymbolicNameUnresolved` | Error (resource) | A name that depends on another resource (`[reference('app').appId]`) could not be computed before its batch ran. |
| `ResourceKeyUnresolved` | Error (resource) | The provider could not derive the Graph key (unknown composite properties, unknown parent, missing path placeholder). |
| `ImmutablePropertyChanged` | Error (resource) | An immutable property differs from the live object and the type cannot be recreated (applications, groups, users). |
| `ReadFailed` | Warning | A Graph read of an existing object failed during planning; the row is reported as unsupported. |
| `ResourceApplyFailed` | Error (resource) | Creating, updating or recreating the resource failed; the message carries the Graph / az error. |
| `ResourceSkipped` | Error (resource) | The resource was not attempted because a dependency (or, without `--continue-on-error`, an earlier batch) failed. |
| `OutputEvaluationFailed` | Error | An output could not be evaluated after the resources were applied (a placeholder remained, an expression failed). |
| `SecretNotAvailableOnRerun` | Warning | An output reads the one-time secret of a resource that was not created or rotated in this run (`reference(...).secretText` of a password credential: Graph returns it once, from `addPassword`). The output is reported as `null` and left out of the secure outputs file; the run succeeds. Change the credential's `rotationTag` to rotate it and obtain a new secret. |
| `CopyExpansionFailed` | Error | A `copy` block could not be expanded (`count`, `mode` or `batchSize` not resolvable at deployment). |
| `StateLocked` | Error | Another `earm` process holds the deployment's state lock (`<state-dir>/<name>.lock`); exit 4. Wait, `state unlock -n <name>`, or `--force-unlock`. Locks older than one hour are taken over automatically. |
| `TenantMismatch` | Error | The state file was written for another tenant than the Azure CLI session; exit 3. |
| `StateSchemaUnsupported` | Error | The state file has a newer `schemaVersion` than this executor understands, or is not a state file; exit 1. |
| `ResourceDetached` | Warning | A state-managed resource is no longer declared in the template; Incremental mode drops it from the state and leaves the object (`what-if`: `*` Ignore row). Also raised when entries of a type could not be judged because a resource of that type still has a deploy-time name. |
| `ResourceDeleted` | Error (resource) | Complete mode (or `deployment delete --delete-resources`) failed to delete a resource the template no longer declares; the entry stays in the state. |
| `EdgeValidationFailed` | Error (resource) | A relationship declaration (`members`, `owners`, `memberOf`, `ownerOf`, `administrativeUnits`, `manager`) is invalid: the target is a dynamic group or administrative unit, does not exist or is not of the right kind, an entry is not an object id, the same edge is declared with conflicting values, or an entry is still a deploy-time placeholder when the edge pass runs. The resource's objects were applied; its relationships were not. |
| `EdgeApplyFailed` | Error (resource) | Adding or removing a relationship edge (`$ref`) failed; the message carries the Graph error. The state keeps the previously recorded edges. |
| `EdgeKindNotSupported` | Warning | A declared relationship kind has no handler in this version (`appRoleAssignments`, `directoryRoles` until their work items land); the property was ignored. |
| `EdgePassSkipped` | Warning | The edge pass did not run because an earlier batch failed (fail-fast); run the deployment again once the failure is fixed. |
| `SignedInPrincipalRemoved` | Warning | Applying the template removes the signed-in principal from an object's `owners` (replace semantics); you may lose the right to manage it. |
| `DestructiveDelete` | Warning | Complete mode is about to delete an object that cannot be restored from the recycle bin (a group that is not a Microsoft 365 group). |
