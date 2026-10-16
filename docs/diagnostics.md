# Diagnostic codes

Codes emitted by `earm deployment validate` (offline). The constants live in
`src/EArm.Core/Diagnostics/DiagnosticCodes.cs`; keep this table in sync with it.

Exit codes: `0` no errors (warnings allowed), `2` at least one error, `1` the template or a parameter
file could not be loaded (the "fatal" rows below) or an argument was malformed.

Table output prints one line per diagnostic: `<severity> <Code> [<json path>] (<line>:<column>): <message>`,
prefixed with the file name when the finding is in the parameter file rather than the template.
JSON output (`-o json`) lists the same fields under `diagnostics[]`.

A finding that repeats is capped: at most 50 diagnostics of one code and severity are listed (the first ones, in order), then one line
`... and N more error(s) of the same code <Code> (not shown).` (a template with 12,000 variables that each nest too deep would otherwise print 12,000
identical lines). The error and warning counts and the exit code count every finding. In JSON the capped list is `diagnostics[]`, `suppressedCount` is the total number
left out (always present, `0` when nothing was) and `suppressed[]` lists `{ severity, code, count }` per capped code (omitted when empty).

## Load failures (fatal, exit code 1)

| Code | Severity | Meaning |
|---|---|---|
| `FileNotFound` | Error | The template or parameter file does not exist, or the path is a directory (the message says which). |
| `InputTooLarge` | Error | A template or parameter file is larger than 8 MiB (8,388,608 bytes; checked from the file size before anything is read, and while reading for a file that reports no size), or holds more than 2,000,000 JSON values (counted in one cheap pass before the document is built). Exit code 1. See [format.md](format.md#input-limits). |
| `InvalidJson` | Error | The file is not valid JSON (comments and trailing commas are allowed; anything else is not). Line/column point at the parse error. A UTF-16 or UTF-32 file (byte-order mark, or NUL bytes) is reported as such: the file must be UTF-8. JSON nested deeper than 64 levels is rejected the same way. A string with a lone surrogate escape (for example a backslash-u escape of D800 not followed by a low surrogate) is also reported here, with the path and the position of the escape: the template still loads (the escape is read as U+FFFD) but validation fails with exit code 2; in a parameter file it is a load failure (exit code 1). |
| `DuplicateJsonKey` | Error | An object repeats a property name (`"resources": [], "resources": []`, or two `state` keys in one conditional access policy). JSON readers disagree about which value wins, so a repeated key is rejected before anything is interpreted. **Property names are compared ignoring letter case** (EARM looks properties up that way and takes the first match, so `"state": "enabledForReportingButNotEnforced", "State": "enabled"` would be judged on one key and sent with both): the message names both spellings. Path, line and column are those of the **repeated** key; every repeated key (up to 50) is listed in the message, the first one is the primary. Also reported for a parameter file, for an inline JSON `-p` value (a load diagnostic) and by the `json()` function (`ExpressionEvaluationFailed`); and for an object an expression builds with two keys that differ only in case (`union`, `createObject`, `items`, `toObject`...) when the value is used (an evaluation error with this code). The names a template declares under `parameters`, `variables` and `outputs` (and the names in a parameter file) are exempt: case variants there are `DuplicateDefinition` / `InvalidParameterFile`. Keys that repeat in different objects are fine. |
| `TemplateRootNotObject` | Error | The template root is not a JSON object. |
| `InvalidParameterFile` | Error | The parameter file is not in the `deploymentParameters` shape (an entry without exactly one of `value`/`reference`, a Key Vault reference without `keyVault.id`/`secretName`, a `secretVersion` that is present but not a string, duplicate names...). |
| `InvalidArgument` | Error | (CLI only) A `-p` token is neither `@file.json` nor `name=value`. |

## Load diagnostics (non-fatal)

| Code | Severity | Meaning |
|---|---|---|
| `NestingTooDeep` | Error | A nested child resource has its own non-empty `resources` array. v1 supports two levels (parent + child); deeper resources are ignored. Declare them at the top level with a `parent/child` name. |
| `DuplicateDefinition` | Error | Two parameters, variables or outputs have the same name ignoring case (ARM names are case-insensitive). |
| `UnknownParameter` | Error | A parameter file or `-p` value names a parameter the template does not declare. |

## CLI-level codes

Emitted by the command line itself, not by validation or the engine; there is no constant in the diagnostic code classes.

| Code | Severity | Meaning |
|---|---|---|
| `AuthenticationFailed` | Error | (CLI only) `earm auth status` could not read the sign-in: the credential could not be built or the token request failed. Exit code 3; with `-o json` the same code is printed in the document's single diagnostic, next to the message on standard error. |

Two more codes appear only on the items of a failed Graph `$batch` envelope (`BatchEnvelopeFailed`, `BatchNotSent`). They are strings on the item's error, not
diagnostic codes, so they have no table row here; see "Graph batch item codes" in [engine.md](engine.md).

## JSON Schema

| Code | Severity | Meaning |
|---|---|---|
| `SchemaViolation` | Error | The template (or parameter file) violates the embedded JSON Schema: unknown resource key, wrong value type, bad `apiVersion` format, etc. The message names the schema keyword. Findings that a semantic code already covers at the same path are not repeated. The per-type schemas (`schemas/2026-10-01/types`, see [format.md](format.md#json-schemas)) add enumerations, patterns, nested object shapes and missing `required` members inside a resource's `properties` (for example `resources[3].properties.directoryRoles[0]`); a missing required key of the envelope itself is reported with its own code (`MissingRequiredProperty`, `MissingResourceType`, ...), never as a schema violation. A number outside the decimal range (such as `1e300`) on a property the schema compares (an enum or const) cannot be evaluated by the schema library; the template is then reported once, at its root, as a `SchemaViolation` saying so, instead of failing the command. |

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
| `InvalidResourceName` | Error | A literal name has the wrong shape for its type: wrong number of `/` segments (child types need `parent/child`), empty segment, non-GUID name for `appRoleAssignments`/`oauth2PermissionGrants`/`roleAssignments`/`servicePrincipals`, UPN without `@`, `authenticationContextClassReferences` name not `c1`..`c99`, federated credential name not matching `^[A-Za-z0-9][A-Za-z0-9_-]{2,119}$`. For **every** type: a segment with a control character (a trailing line feed passed the old `$`-anchored patterns), leading or trailing whitespace, or made of dots only (`.`, `..`). Name patterns are matched against the whole segment (`^` and `$` mean the start and the very end of the text). Computed names (`[concat(...)]`, `[parameters('n')]`) face the same rules: offline once their value is known, and again for every `copy` iteration when the resources are expanded (the message then starts with `copy '<name>' iteration <n>:`). Text from the name shown in the message is escaped (`\n`, `\u001b`). |
| `ParentChildTypeMismatch` | Error | A child type is nested under (or spelled with) the wrong parent type, e.g. `federatedIdentityCredentials` under a group. |
| `DuplicateResourceId` | Error | Two declarations with literal names produce the same resource id (`/providers/Microsoft.Entra/<type>/<name>`). Names compare case-sensitively, types case-insensitively. |
| `NameMappedPropertyConflict` | Error | `properties` declares the Graph property the name is injected into (`uniqueName`, `userPrincipalName`, `appId`, `displayName`, `id`, child `name`) with a different literal value. |
| `DynamicMembershipConflict` | Error | `members` declared on a dynamic group (`groupTypes` contains `DynamicMembership`) or a dynamic administrative unit (`membershipType: Dynamic`). |
| `DirectoryRolesRequireRoleAssignableGroup` | Error | `directoryRoles` on a group without a literal `isAssignableToRole: true`. |
| `AssignedLicensesRequireUsageLocation` | Error | `assignedLicenses` on a user that declares no `usageLocation`. |
| `InvalidCopy` | Error | `copy` is missing `name`/`count`, `name` is not a literal string, `count` is negative or not an integer, `mode` is not `serial`/`parallel`, or `batchSize` is not positive. Also a `copy` block in `variables` (variable iteration is not supported). |

## Parameters

| Code | Severity | Meaning |
|---|---|---|
| `InvalidParameterType` | Error | A parameter (or user-function parameter) has no `type` or an unknown one; allowed: `string`, `securestring`, `int`, `bool`, `object`, `secureObject`, `array`. |
| `AllowedValueTypeMismatch` | Error | An `allowedValues` entry does not match the parameter type. |
| `InvalidParameterConstraint` | Error | `minValue`/`maxValue` on a non-`int` parameter, `minLength`/`maxLength` on a type other than `string`/`securestring`/`array`, a negative length, a non-integer bound, an expression bound (ARM allows expressions only in `defaultValue`), or min greater than max. |
| `DefaultValueTypeMismatch` | Error | The literal `defaultValue` does not match the parameter type. At deployment (and wherever the default is evaluated), also an expression `defaultValue` whose result does not match the type. |
| `DefaultValueNotAllowed` | Error | The literal `defaultValue` (or, once evaluated, an expression `defaultValue`) violates a constraint of the parameter: it is not in `allowedValues`, is outside `minValue`/`maxValue`, or is shorter or longer than `minLength`/`maxLength`. Secure parameters are checked too; the message names the constraint, never the value. |
| `SecureParameterHasDefault` | Warning | A `securestring`/`secureObject` parameter has a default value. |
| `MissingParameterValue` | Warning | No value was supplied and the parameter has no default. A warning offline; deployment treats it as an error. |
| `ParameterValueTypeMismatch` | Error | A supplied value (file or `-p`) does not match, or cannot be converted to, the declared type. |
| `ParameterValueNotAllowed` | Error | A supplied value violates a constraint of the parameter: not in `allowedValues`, outside `minValue`/`maxValue`, or shorter or longer than `minLength`/`maxLength` (arrays: the element count). Secure parameters are checked too, on the revealed value; the message names the constraint (and the parameter), never the value. |

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
| `UnsupportedArmFunction` | Error | An ARM function with no Entra meaning: `resourceGroup subscription managementGroup managementGroupResourceId extensionResourceId subscriptionResourceId tenantResourceId listKeys listSecrets providers pickZones`. The message names the alternative. |
| `NotImplementedYet` | Warning | A function registered as `FunctionKind.NotImplementedYet` evaluates to a deploy-time placeholder. **Not emitted by the default registry any more**: lambdas (`lambda map filter reduce sort toObject groupBy lambdaVariables`), the Entra functions (`wellKnownApp appRole permissionScope roleTemplate tenant environment`) and user-defined functions are implemented. Kept for registries that stage a function before implementing it. |
| `NonDeterministicFunctionNotAllowed` | Error | `utcNow()` or `newGuid()` outside a parameter `defaultValue` (ARM rule). |
| `FunctionNotAllowedHere` | Error | A function in a place the phase rules forbid: `variables()` inside a parameter default value or a user-defined function; `appRole()`, `permissionScope()`, `roleTemplate()` or `tenant()` inside a parameter default, a variable or a user-defined function (they need the tenant, which is only consulted while resources are applied; `wellKnownApp()` is pure and allowed anywhere); `lambdaVariables()` outside the body of a lambda; a user-defined function called from inside another user-defined function (ARM rule). |
| `InvalidFunctionArguments` | Error | Wrong argument count or type for a function (`concat` of strings and arrays, `substring` on a number, `createObject` with an odd count...). Lambdas: a `lambda()` anywhere but a lambda slot of `map filter reduce sort toObject groupBy` (including as a user-function argument), a lambda slot holding something else, a lambda declaring the wrong number of parameters for its slot (`map`/`filter`: `element[, index]`; `reduce`: `current, next[, index]`; `sort`: `left, right`; `toObject`/`groupBy`: `element`) or the same name twice, `lambdaVariables('x')` naming no parameter of an enclosing lambda, a `filter`/`sort` lambda not returning a boolean, a `toObject`/`groupBy` key lambda not returning a string. Entra: `wellKnownApp()` with a name outside the embedded table (the message lists the names), `appRole()`/`permissionScope()` whose first argument is neither an appId (GUID) nor the name/resource id of a `Microsoft.Entra/applications` or `Microsoft.Entra/servicePrincipals` resource of the template. User functions: an argument whose value does not match the declared parameter type. |
| `ExpressionEvaluationFailed` | Error | The arguments had the right types but the operation failed: `substring` out of range, integer overflow, division by zero, invalid base64/JSON/date/duration, property or index not found, `int('abc')`, `toObject` producing a duplicate key (a duplicate secret-derived key is reported as `***`, never its text). Object keys are not taint-tracked, so a key computed from a secure value (`createObject`, `toObject`, `groupBy`) is masked in the result rather than written: the first distinct secret key becomes `***`, the next `***#2`, and so on (a clear key already named like that is skipped); equal secret keys still group or collide, the values stay secure, and a lookup by the real key text finds nothing. Entra lookups with no match in the tenant: `appRole()`/`permissionScope()` for an appId without a service principal or application, or a `value` the principal does not declare (the message lists the available values); `roleTemplate()` naming no role definition (a custom role declared in the same template is referenced as `reference('<name>').id`). User functions: a body that fails (reported at the call site; the message carries the body's offset and path) or an output that does not match the declared `output.type`. |

## Expressions: limits

Every function result is checked against the limits of [format.md](format.md#evaluation-limits) before it is used; a tenant-independent, per-run budget bounds the total work. Limits are properties of the format, not of a machine: the same template fails the same way everywhere (the wall-clock limit aside).

| Code | Severity | Meaning |
|---|---|---|
| `ValueTooDeep` | Error | A value built by expressions is nested more than 64 levels deep (arrays or objects inside arrays or objects: `createArray(createArray(...))`, `createObject('k', ...)`, `array()`, `items()`, a lambda that wraps its element, a user function output, a parameter value wrapped by the template). 64 is the nesting the JSON reader and writer accept, so deeper values could not be stored or printed. Also raised when a walker over a value runs out of stack. Reported at the expression (`variables.v65` for a chain of 5,000 `createArray(variables('v{i-1}'))` variables) instead of crashing the process. |
| `EvaluationBudgetExceeded` | Error | An evaluation exceeded a size or work limit: a string over 1 MiB (1,048,576 characters; also a `format` whose padded items and arguments would add up past it, and a `replace`, `concat`, `base64`, `string()` of a large value or `flatten` that would grow past it), an array or object over 100,000 elements, a value over 1,000,000 nodes, more than 50,000,000 steps (function calls plus lambda iterations), more than 5,000,000 value nodes or 128 Mi characters (134,217,728) of text produced in total, more than 256 MiB held by evaluated variables, or one evaluation (a variable, the properties of a resource, an output) taking more than `--evaluation-timeout` seconds (default 60, `EARM_EVALUATION_TIMEOUT`). Per-value limits fail that value only; the work limits end the run: every later expression fails with the same message. The message names the limit. |
| `ExpansionLimitExceeded` | Error | `copy.count` is more than 800 (the ARM limit), or the template would expand to more than 5,000 resource instances in total (all `copy` loops together). Reported before a single iteration is built (offline by `validate`, and again by the planner), so a count of 3,000,000 costs nothing. |

## Expressions: parameters, variables, copy

| Code | Severity | Meaning |
|---|---|---|
| `UndefinedParameter` | Error | `parameters('x')` names a parameter the template does not declare; inside a user-defined function body, `parameters('x')` names only the function's own parameters, so a template parameter name is reported here too. Also reported for an expression inside a resource's `properties` (at the path of that value): `validate` evaluates each property expression offline and reports this, `UndefinedVariable` and `UnknownFunction`; anything that is only known at deployment (`reference()` of a deployed resource, live lookups) is skipped. |
| `UndefinedVariable` | Error | `variables('x')` names a variable the template does not declare (also inside a resource's `properties`, see `UndefinedParameter`). |
| `VariableCycle` | Error | A variable refers to itself, directly or through other variables. |
| `VariableChainTooDeep` | Error | Variables read other variables more than 128 levels deep (a chain `v1` = `variables('v0')`, `v2` = `variables('v1')` ... of 128 or more). ARM has no such limit; EARM bounds the evaluation depth so a chain of hundreds cannot exhaust the stack. A variable that is within the limit when read from the top evaluates normally; shorten the chain (or compute the value in one expression). |
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
| `PropertyTypeMismatch` | Error | A property given as an expression evaluates, offline, to a value of the wrong JSON type for the resource type's schema (an array for a string property: `"description": "[createArray('a')]"` on a group). The template schema can only judge the text of the file, so such a value used to pass `validate` and fail at deployment. Only the `type` keyword is checked, only for fully known values: a deploy-time unknown (`reference()`, a parameter without a value, `copyIndex()`) or a secure value is skipped, and a wrong value written literally is reported once, as `SchemaViolation`. A wrong value inside an array or object an expression produced is reported at that expression and names the evaluated path. |
| `ConditionNotBoolean` | Error | A resource or output `condition` evaluates to something other than a boolean. |
| `SecureValueInResourceKey` | Error | A secure (tainted) value flows into `name`, `condition`, `copy.count` or `dependsOn`; these are written to the output, what-if and logs. |
| `SecureValueInPlainOutput` | Error | A secure value flows into an output that is not `securestring`/`secureObject`. |
| `DuplicateResourceId` | Error | (Also here) Two declarations produce the same id after evaluating expression names (literal pairs are reported by the core validator). |
| `InvalidCopy` | Error | (Also here) `copy.count` evaluates to a negative or non-integer value, or depends on `copyIndex()`. |

## Still not checked offline

Duplicate ids between `copy` iterations need copy expansion; `memberOf` targeting a dynamic group,
non-existent edge targets and conflicting edge declarations need the live edge model and are reported by the
engine's edge pass as `EdgeValidationFailed`.

## Engine codes (`what-if` and `create`)

Raised by `EArm.Engine` (`EngineDiagnosticCodes`) while planning and executing; they never appear in
`deployment validate`. Errors in the planning phase exit 2 (`ValidationFailedException`); failures of individual
resources are reported per operation (exit 1).

| Code | Severity | Meaning |
|---|---|---|
| `DependencyCycle` | Error | The dependency graph (explicit `dependsOn`, parent, implicit `reference()`, symbolic-name inputs, serial copy chaining) has a cycle; the message carries the path. Move a `reference()` into a relationship property (applied after all objects exist) or drop a `dependsOn`. |
| `KeyVaultReferenceUnresolved` | Error / Warning | A parameter supplied as a Key Vault reference could not be resolved: the Key Vault read failed (invalid vault or secret name, vault or secret missing, no access, no sign-in; the message is redacted; exit 3), or the reference reached the planner unresolved (error for `create`, warning plus deploy-time placeholder for `what-if`). |
| `UnresolvedReference` | Warning | A `reference(resourceId(...), apiVersion)` lookup of an existing resource could not be performed (no provider for the type, GUID-named type, unresolvable key); the value stays a deploy-time placeholder. |
| `ConditionUnresolved` | Error (resource) | A resource `condition` depends on a value that is not available when the resource is applied, or is not a boolean. |
| `UnresolvedDeployTimeValue` | Error (resource) | The evaluated properties still contain deploy-time-unknown leaves when the resource is applied (a referenced resource was not deployed, or a function is a stub). |
| `SymbolicNameUnresolved` | Error (resource) | A name that depends on another resource (`[reference('app').appId]`) could not be computed before its batch ran. |
| `ResourceKeyUnresolved` | Error (resource) | The provider could not derive the Graph key (unknown composite properties, unknown parent, missing path placeholder). |
| `ImmutablePropertyChanged` | Error (resource) | An immutable property differs from the live object and the type cannot be recreated (applications, groups, users). |
| `ReadFailed` | Warning | A Graph read of an existing object failed during planning; the row is reported as unsupported. |
| `ResourceApplyFailed` | Error (resource) | Creating, updating or recreating the resource failed; the message carries the Graph error. Also: **an unexpected exception** inside one resource (a null reference, a format error, an HTTP timeout that is not the caller's cancellation) fails only that resource (`unexpected error (<type>)`): the siblings, the edge pass, the secure outputs file, and the orphan report still run; and **a failure after the object was written** (a license, an owner or a restore step of the provider failed after the object was created or updated): its one-time secret (a generated user password) is written to the secure outputs file, the resource is reported failed with the object's Graph id ("the object was created (Graph id ...), but a later step failed ... Run the deployment again to finish it."), and a rerun finds the object by key and converges. |
| `ResourceSkipped` | Error (resource) | The resource was not attempted because a dependency (or, without `--continue-on-error`, an earlier batch) failed. |
| `OutputEvaluationFailed` | Error | An output could not be evaluated after the resources were applied (a placeholder remained, an expression failed). |
| `SecretNotAvailableOnRerun` | Warning | An output reads the one-time secret of a resource that was not created or rotated in this run: `reference(...).secretText` of a password credential (Graph returns it once, from `addPassword`), or `reference(...).passwordProfile.password` of a user (Graph never returns a password, so the initial password is only known in the run that creates the user). The output is reported as `null` and left out of the secure outputs file; the run succeeds. Change the credential's `rotationTag` to rotate it and obtain a new secret; a user's initial password is in the secure outputs file of the run that created the user. |
| `CopyExpansionFailed` | Error | A `copy` block could not be expanded (`count`, `mode` or `batchSize` not resolvable at deployment). |
| `ResourceDeleted` | Error (resource) | `deployment delete` failed to delete a declared object (the message carries the Graph error), or the object's key now names another object than the one that was listed (nothing is deleted for it). The rest stops unless `--continue-on-error`. |
| `EdgeValidationFailed` | Error (resource) | A relationship declaration (`members`, `owners`, `memberOf`, `ownerOf`, `administrativeUnits`, `manager`) is invalid: the target is a dynamic group or administrative unit, does not exist or is not of the right kind, an entry is not an object id, the same edge is declared with conflicting values, or an entry is still a deploy-time placeholder when the edge pass runs. The resource's objects were applied; its relationships were not. |
| `EdgeApplyFailed` | Error (resource) | Adding or removing a relationship edge (`$ref`) failed; the message carries the Graph error. The edges already applied stay; a rerun reads the live relationships again and applies what is still missing. |
| `EdgeKindNotSupported` | Warning | A declared relationship kind has no handler in this version (for example `appRoleAssignments` or `directoryRoles` when only the engine's default edge kinds are registered); the property was ignored. |
| `EdgePassSkipped` | Warning | The edge pass did not run because an earlier batch failed (fail-fast); run the deployment again once the failure is fixed. |
| `EdgeUnsupportedInAppMode` | Warning | A relationship cannot be read with an application (app-only) token, so the property was left alone (no read, no write): `ownerOf` on a user needs `GET /users/{id}/ownedObjects`, which Microsoft Graph does not support without a signed-in user (Learn: user-list-ownedobjects). Declare the user in `owners` of the application, service principal or group instead, or sign in as a user. |
| `ManagerReadForbidden` | Warning | Reading `/users/{id}/manager` was refused (403: the identity lacks `User.Read.All`/`Directory.Read.All`); the `manager` relationship of that user is skipped (not read, not written) and the run goes on. |
| `SignedInPrincipalRemoved` | Warning | Applying the template removes the signed-in principal from an object's `owners` (replace semantics); you may lose the right to manage it. |
| `DestructiveDelete` | Warning | A delete removes an object for good: the old object of a **recreate** (an immutable property changed on a type that is deleted and created again; what-if says so on the row and a deployment asks for confirmation, `-y`), or an object that cannot be restored from the recycle bin (a distribution list or mail-enabled security group), named in the `deployment delete` listing and the recreate warning. |
| `ProtectedObject` | Error (resource) / kept | A delete would remove the signed-in principal, or a service principal owned by another organization (a Microsoft first-party or multi-tenant app); never done. A **recreate** of such an object fails (nothing is deleted or created; what-if shows an error row); `deployment delete` keeps it (listed as kept, not a failure). |
| `RecreateNotConfirmed` | Error | A deployment would replace objects (an immutable property changed: delete, then create again) and that was not confirmed: the prompt was declined, or standard input is not interactive and `-y` was not passed. Nothing is written (exit 1, "Deployment not started."). A replacement the run only discovers while it runs (its input resolved during the run) is refused the same way when it was not part of the confirmed plan (the resource fails, nothing is deleted). |
| `OrphanReportIncomplete` | Warning / Info | The orphan report (Complete mode; the retained assignments in both modes) did not list a declared type: one of its declared resources is not known in this run (a deploy-time name or condition, a read or apply that failed), or the listing itself failed. Info: Complete mode never lists the tenant-wide types (applications, groups, users). Nothing is ever deleted either way. |
| `AssignmentRetained` | Warning | A live role assignment, app role assignment or OAuth2 permission grant of a principal (client) the template declares is not declared: typically the old tuple of an assignment whose principal, role or scope changed (a changed tuple is a new assignment; the old one is **retained**). It still grants access; EARM never removes it. The what-if (both modes) and the deployment result list it with `orphan: retained`. Delete it yourself if the access should end. |
| `DuplicateGraphObject` | Error (resource) | Two declared resources resolve to the same Graph object; the second one fails (remove one declaration or give them different keys). |
| `CreatedObjectNotVisible` | Warning | A create of a `displayName`-keyed type (Conditional Access policy, named location, authentication strength, administrative unit, custom role definition) succeeded, but reading the key back (Graph's `$filter=displayName eq` list) did not return the new object within the replication window (90 s), or the read failed. The create counts as done. A run started before Graph catches up may not find the object and plan to create it again, which would make a duplicate (and later `AmbiguousMatch`); wait a minute, and check with `deployment what-if` that the next run shows no create. See [engine.md](engine.md), "Read-your-write after a create". |
| `CreateOutcomeUncertain` | Warning | A create ended with an unknown outcome (timeout, transport error or 5xx) but the object exists when looked up by key, so the create counts as done and was not repeated (neither by the engine nor by an outer retry: a create is never repeated blindly). Values only returned at creation (a secret) are not available for it. |
| `SecureOutputsWriteFailed` | Error | The `--secure-outputs-file` could not be written (secure outputs, or a one-time secret right after it was produced: that secret cannot be read from Graph again, rotate the credential). |

## Provider codes

Raised by the resource providers (`EArm.Providers`) against one resource; the constants are in `EngineDiagnosticCodes`. The ones of severity Warning never fail a
run: a provider reports them in `AppliedResource.Warnings`, or (`SecretExpiresSoon`, `RotationTagNotStamped`) as a plan row reason `Code: ...` that what-if and create both turn into a
warning diagnostic. The others are the `Code` of a provider failure and appear as the error of the resource's operation.

| Code | Severity | Meaning |
|---|---|---|
| `SoftDeleteProbeSkipped` | Warning | The `GET /directory/deletedItems/microsoft.graph.<type>` probe made before a create was refused (403) or rejected (400), usually because the identity has `Application.ReadWrite.OwnedBy` but not `Application.Read.All`; the create goes on without knowing whether a soft-deleted object holds the key. |
| `GroupHasNoOwnerAtCreate` | Warning | A Microsoft 365 group was created without `properties.owners`: with an application token Graph creates it ownerless ("anonymous") and nobody but an administrator can manage it. Declare `owners`. |
| `SecretExpiresSoon` | Warning | A password credential has expired or expires within the rotation window and a replacement would not live longer (a declared fixed `endDateTime`, or a default that is not later); nothing is rotated. Declare a later `endDateTime` or remove it. |
| `RotationTagNotStamped` | Warning | A password credential's live `displayName` carries no `#<rotationTag>` suffix (it was created by an earlier EARM preview that kept the tag in a local state file) while the template declares a `rotationTag`. The credential is treated as matching, so upgrading never rotates a secret by surprise; the next rotation writes the tag into the name. Until then a changed `rotationTag` is not detected: declare a different `endDateTime` to rotate it now. |
| `PasswordCredentialOrphaned` | Error (resource) | An `addPassword` call failed without a usable answer and the application now holds credentials with the same name that EARM did not record, so their secrets are lost. The message lists their key ids; remove them with `POST /applications/{id}/removePassword` (EARM never does), then deploy again. |
| `UniqueNameNotSettable` | Error (resource) | Graph refused to set the `uniqueName` of an existing application (the property is immutable; it can only be assigned when the application is created by the upsert). Raised when an application declared with an `appId` is found by that `appId` without a `uniqueName` and EARM tries to give it the template `name`. Recreate the application through EARM, or remove the declared `appId` and let EARM create a new application. |
| `AmbiguousMatch` | Error (resource) | A lookup by `displayName` (or another non-unique key) matched more than one object. EARM keeps no state that could say which one is meant, so the display names of those types (Conditional Access policies, named locations, administrative units, custom role definitions, authentication strengths) must be unique: rename or delete the extra objects (the message lists their ids). Two concurrent runs of one template can cause this (see [cli.md](cli.md#deployment-what-if-and-deployment-create), "No lock"). |
| `SoftDeletedConflict` | Error (resource) | The key of a resource to create is held by a soft-deleted object. Pass `--on-soft-deleted-conflict restore` to restore it, or purge it from the recycle bin. |
| `AppRoleAssignmentInvalid` | Error (resource) | An `appRoleAssignments` declaration cannot be applied: the principal, resource or app role does not exist or does not fit together. |
| `PermissionGrantInvalid` | Error (resource) | An `oauth2PermissionGrants` declaration cannot be applied: an unknown property, `consentType` and `principalId` that do not fit together, a `scope` value the resource service principal does not expose (or has disabled), or a resource that does not exist. |
| `RoleAssignmentInvalid` | Error (resource) | A directory `roleAssignments` declaration cannot be applied: unknown role, principal or scope. |
| `RoleDefinitionBuiltIn` | Error (resource) | A `roleDefinitions` declaration names a built-in role; built-in roles cannot be created, changed or deleted. |
| `RoleDefinitionInUse` | Error (resource) | A custom role definition cannot be deleted while role assignments still use it. |
| `AuthenticationContextReferenced` | Error (resource) | An authentication context class reference cannot be deleted while a Conditional Access policy references it. When a policy was deleted earlier in the same run, the delete is first repeated every 5 s for up to 90 s (the Conditional Access service sees the policy delete late). |
| `AuthenticationStrengthReferenced` | Error (resource) | An authentication strength cannot be deleted while a Conditional Access policy references it. When a policy was deleted earlier in the same run, the delete is first repeated every 5 s for up to 90 s (the Conditional Access service sees the policy delete late). |
| `AuthenticationStrengthBuiltIn` | Error (resource) | A built-in authentication strength cannot be created, changed or deleted. |
| `ConditionalAccessPolicyRejected` | Error (resource) | A Conditional Access policy was refused before the write (for example `state: enabled` without `--allow-ca-enforce`, or an invalid shape); the message lists the problems. |
| `NamedLocationReferenced` | Error (resource) | A named location cannot be deleted while a Conditional Access policy references it. When a policy was deleted earlier in the same run, the delete is first repeated every 5 s for up to 90 s (the Conditional Access service sees the policy delete late). |
| `NamedLocationTrusted` | Error (resource) | Graph kept refusing to delete a named location because it is marked as a trusted location (Graph error `1177`), even after EARM unmarked it (`isTrusted: false`) and kept retrying every 5 s for 90 s (Graph applies the unmark with a delay). EARM unmarks a trusted IP location before deleting it, so this means the unmark did not take effect; set `isTrusted: false` on the location and delete it again. |
