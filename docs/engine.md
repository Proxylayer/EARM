# The deployment engine (`EArm.Engine`)

How `earm deployment what-if` and `earm deployment create` turn a template into Graph calls, and
what a resource provider has to implement. How the planned changes are rendered (text and ARM-shaped JSON, symbols, exit code 5, golden
fixtures) is in [whatif.md](whatif.md); the template format is in [format.md](format.md), the relationship catalog
in [edges.md](edges.md), the required Graph permissions in [permissions.md](permissions.md) and every verb in
[cli.md](cli.md).

## Pipeline

```
load ──► validate ──► expand ──► graph ──► [per batch: resolve symbolic names ──► key ──► read ──► plan ──► apply] ──► edge pass ──► outputs ──► orphan report
 Core      Core +      Engine     Engine     Engine (planner: read + plan only; executor: + apply)                      Engine        Engine      Engine
           Expressions
```

1. **Load** (`TemplateLoader`): parse, flatten nested children to `parent/child` declarations, bind parameters.
2. **Validate** (`DeploymentPlanner`): the core `TemplateValidator` plus the `ExpressionValidator`. Two findings
   that are warnings offline become errors here: a parameter without a value (`MissingParameterValue`) and, for
   `create`, a Key Vault parameter reference (`KeyVaultReferenceUnresolved`; not resolvable in this version).
   Any error throws `ValidationFailedException` (CLI exit 2).
3. **Expand** (`ResourceExpander`): one `ResourceInstance` per declaration, or one per `copy` iteration
   (`copy.name/count/mode/batchSize`, concrete `copyIndex()`, name re-evaluated per index). `condition` is
   evaluated per instance; `false` keeps the instance as an `Ignore` row. Ids must be unique after expansion
   (`DuplicateResourceId`). A child whose parent is declared in the template is linked to the parent instance.
   The static index (`TemplateResourceIndex`) only holds ids computable without a copy frame; right after
   expansion the planner adds the concrete id of every copy iteration to the deployment's index (shared by every
   evaluation context), so `reference(format('g-{0}', copyIndex()))` resolves by name from then on.
4. **Graph** (`DependencyGraph`): edges are the union of
   - explicit `dependsOn` (resource id, `type/name`, bare name, or a copy-loop name → every iteration
     (`DependencyKind.CopyLoop`); entries that depend on `copyIndex()` are re-resolved per iteration). Every
     loop is registered in the static index by its literal `copy.name` (`TemplateResourceIndex.CopyLoops`) even
     when its iteration names depend on `copyIndex()` and therefore have no ids offline, so
     `dependsOn: ["loopName"]` validates offline and never reports `DependsOnTargetNotFound`,
   - the parent of a child resource (inferred; a superset of ARM's explicit rule),
   - implicit `reference()` / `resourceId()` occurrences in `properties` **outside** the relationship
     properties of the type (`ResourceTypeDefinition.IsRelationshipProperty`); occurrences inside edge
     properties (`members`, `owners`, `memberOf`, `manager`, ...) are kept on `ResourceInstance.EdgeReferences`
     for the edge pass and never create object-pass dependencies, so mutual declarations are not cycles.
     A `reference()` whose target depends on `copyIndex()` is `Deferred` offline (no diagnostic) and rescanned
     per iteration with the concrete copy frame against the expanded index, which yields the edge to that
     iteration (`u1` → `g-1`),
   - the inputs of a **symbolic name** (see below),
   - `mode: serial` copy chaining (iteration *n* waits for chunk *n-1*).

   A cycle is reported as `DependencyCycle` with the full path. The acyclic graph is layered Kahn-style:
   level = 1 + the highest level among the dependencies; one `DeploymentBatch` per level; inside a batch the
   instances are ordered by apply phase (named locations, auth contexts/strengths, role definitions,
   users, applications → service principals, FICs, password credentials → groups → administrative units →
   assignments/grants → CA policies) and then by declaration order. Phases are only a tie-breaker; dependencies
   decide the batches.
5. **Providers**: every instance type needs a provider in the `ProviderRegistry`; otherwise
   `UnknownProviderException` lists the missing types (CLI exit 1). The CLI composition root (`EngineServices`) registers the
   providers; a registry with none rejects every type.
6. **Per batch**, with `Parallel.ForEachAsync` bounded by `--parallelism`:
   - *what-if* (`PlanMode.WhatIf`; the rendering of its result is described in [whatif.md](whatif.md)): `ResolveKey` → `ReadAsync` → `Plan`. Nothing is written. A live object that
     exists is published to the resolver so later batches evaluate `reference()` against it; a resource that
     does not exist yet stays a placeholder and shows up as `(deploy-time)` in the rows that use it
     (`PropertyDelta.IsUnknown`, `ResourceChange.IsUnknown`).
   - *create* (`DeploymentExecutor`): evaluate properties → `ResolveKey` → `ReadAsync` → `Plan` →
     `CreateAsync` / `UpdateAsync` (or delete + create for `ChangeMode.Recreate`) → publish the live object →
     `IStateSink.ResourceAppliedAsync`. `PlanMode.Create` skips the planner's read pass: the executor plans each
     resource live right before applying it, so a what-if shown for confirmation never goes stale.
   - *evaluating properties* (`InstanceEvaluator.EvaluateProperties`, both modes) splits off the edge properties
     and **taints every leaf under the overlay's `secure` paths** (`InstanceEvaluator.TaintSecrets`:
     `passwordProfile.password`, `secretText`) exactly as a `securestring` parameter would, so a literal secret
     in a template is `***` in what-if rows, diagnostics, logs and the state hash for every type; only
     `ProviderBase.BuildCreateBody` / `BuildPatchBody` reveal it. The taint is idempotent (already secret, already
     tainted-unknown and `null` leaves are left alone), so a provider may taint again in its own `Plan`.
7. **Edge pass** after all batches and before the outputs: the relationship properties
   (`members`, `owners`, `memberOf`, `ownerOf`, `administrativeUnits`, `manager`) are reconciled against the
   live edges; what-if renders the same deltas read-only. See [Edge pass](#edge-pass-earmengineedges) below.
8. **Outputs** after the edge pass: secure outputs (`securestring`/`secureObject`, or any tainted value) are `***`
   unless `--reveal-secure-outputs`; `--secure-outputs-file` writes the revealed values with owner-only
   permissions (`OwnerOnlyFile`: the text goes to a new temp file in the same folder that is created owner-only
   (protected DACL with the current user alone on Windows, mode 0600 on Unix) and is then moved over the target, so
   an existing file's old permissions never apply; the result is verified, a read-only existing file or a directory
   path is an `IOException`, and the temp file is removed on failure). **One-time secrets** (`reference(...).secretText` of a password credential):
   Graph returns the secret only from `addPassword`, so it is available in the run that creates or rotates the
   credential and in no later run. On a rerun the provider publishes `secretText: null` plus the marker
   `secretTextAvailable: false` (`LiveSecrets`); the executor traces which published objects each output's
   `reference()` calls touched, and an output whose value is (or contains) that `null` is reported as `null` with
   the `SecretNotAvailableOnRerun` warning and left out of the secure outputs file. The run still succeeds. Change
   `rotationTag` to rotate the credential and obtain a new secret.
9. **Orphan report** (`OrphanReport`, after the outputs; read-only): the live objects of the declared types that no declared
   resource matches by key are listed in `DeploymentResult.Orphans` (`ResourceChange.Orphan`: `Undeclared` in Complete mode, `Retained` for
   assignments and grants of declared principals in both modes). **Nothing is ever deleted**: Complete mode only reports. See
   [Orphan report](#orphan-report-and-deployment-delete) below and [whatif.md](whatif.md#orphan-report).

### Failure handling

- Default: **fail fast at the batch boundary**. In-flight resources of the failing batch finish; every later
  batch is `Skipped`.
- `--continue-on-error`: only the transitive dependents of a failed resource (`DependencyGraph.TransitiveDependents`)
  are skipped; independent branches keep going. A dependent is recorded `Skipped` / `ResourceSkipped` with the
  reason "it depends on *type 'name'*, which failed" and reported through `IStateSink.ResourceSkippedAsync` at
  the start of its batch, before symbolic names are resolved: it is never attempted, its name is never
  re-evaluated (a symbolic dependent keeps its symbolic id in the result) and it never counts as a failure.
- A `create` that comes back "already exists" (`GraphException.IsAlreadyExists`) is idempotent success: the
  object is re-read by key and taken over (identity is the key: there is no created / adopted distinction).
- A `create` that comes back "already exists" also applies the declared properties **in the same run** (one extra PATCH where the provider supports it and the difference can be patched in place; otherwise an `ImmutablePropertyChanged` warning says the object differs and a rerun or the what-if shows how).
- A `create` with an **ambiguous outcome** (a timeout, a transport error, any 5xx: the write may have been committed) is not repeated and not
  simply failed: the object is looked up by key, and because Graph replicates a new object with some delay the **executor itself waits out
  replication** (`ReplicationWait`, a constructor argument of `DeploymentExecutor`: the read is repeated every 5 s until the object is found or
  90 s of waiting were scheduled, the defaults of `RetryOptions.ReplicationInterval` / `ReplicationWindow`). "Not found yet" covers a `null` from
  the provider - an empty `$filter` result answers 200, never 404 - as well as a 404 or a replication-lag 400. The executor owns this wait whether
  or not the plan carries a retry policy (the CLI's never does: `HttpGraphClient` retries each call itself); with a policy each read goes through
  it with `RetryHint.Default`, so throttling and transient failures are still waited out but the replication window is never waited twice. The
  reads are not cancellable (an object found is recorded, not orphaned); a Ctrl+C cuts the wait short to one last read. The same wait applies to
  a create answered "already exists" before the object is taken over. A create whose ambiguous failure hid no commit therefore reports its error
  only after the window. When the object exists the create counts as done (warning
  `CreateOutcomeUncertain`; a value Graph only returns at creation, such as a secret, is not available for it), otherwise the original error is reported.
  The executor never repeats a create blindly **at any level**: its own retry policy (`RetryPolicy.ExecuteAsync(..., idempotent: false)`) only waits out a 429, replication lag and a token refresh for a provider write
  (create, update), never a 5xx or a lost connection; reads and deletes stay repeatable. The edge pass sends an add (`POST`) the same way, and **reads the edge back** when its answer was lost: an edge that turns out to exist
  (and was not there when the pass planned it) counts as added. A `$batch` is sent once (the client retries failed sub-requests itself); every failure is read per item from
  `BatchResponse.Error`.
- **A failure after the primary write.** A provider that writes in several steps (create the object, then owners, licenses, a restore...) and fails after the first hands the partial result over
  (`ProviderException.Partial`: `UsersProvider` does it when `assignLicense` fails after `POST /users`); for any other provider the executor looks the object up by key after a failed create. Either way the
  object, its provenance and its one-time secret (a generated password) are **recorded in the state and the secure outputs file** and the resource is reported failed (`ResourceApplyFailed`,
  "recorded in the state"); a rerun converges instead of conflicting with an object nobody recorded.
- **Unexpected exceptions.** Anything not handled above (a null reference, a format error, an HTTP timeout that is not the caller's cancellation) fails **that one resource** (`ResourceApplyFailed`, `unexpected error (<type>)`):
  parallel siblings are never cancelled by it, and the end phase (edge pass, outputs, secure outputs file, orphan report, completion record) still runs. An exception of the engine itself outside any resource aborts the run the same
  way: the unstarted resources are recorded as skipped, the orphan report judges nothing, the run ends `Failed` with an error diagnostic, and the state is **never left `Running`**.
- **Replacing** an object whose immutable properties changed (`ChangeMode.Recreate`: credentials, federated identity credentials, named location kinds,
  every-property-immutable types) deletes the old object, then creates it again. The old object was found by the key, so the replacement has the same key and
  identity; there is no create-first variant. (A changed **tuple** of an assignment or grant is not a replacement: the tuple is the identity, so it is a new
  object, created, and the old one is retained and reported, see the orphan report.) A replacement is guarded like every delete (`DeleteGuards`): the signed-in principal
  and a service principal owned by another organization are never replaced (`ProtectedObject`; nothing is deleted or created, the what-if row is an error row). It
  also needs **confirmation**: `ExecuteOptions.ApprovedRecreates` lists the resource ids the user confirmed (`null` = all, the CLI's `-y`); a replacement of any other
  resource fails with `RecreateNotConfirmed` before anything is deleted. The CLI plans with the read-only what-if pass first when `-y` is not given, lists the
  replacements and asks (non-interactive without `-y`: exit 1 before any write). The what-if row of a recreate says that the existing object is DELETED and created
  again (warning `DestructiveDelete`); a provider's `IDeleteWarningSource` risk is warned before the delete.
- **The end phase records what happened regardless of the caller's cancellation, and stops writing when asked to.** After the last batch the executor runs the edge pass, evaluates the outputs, merges the secure
  outputs file, runs the orphan report and records the completion; **recording** (state saves, one-time secrets, the completion) uses `CancellationToken.None`, so a Ctrl+C or a failed state write cannot skip the steps
  after it. The step that **writes to the tenant again** (the edge pass) checks the caller's token **between operations**: the one in flight finishes and is recorded, nothing new is
  started (the edge operations not started are recorded as failed "cancelled"), and the run ends `Cancelled`. A cancelled run ends with `lastStatus: Cancelled` (never stuck at `Running`), the unstarted resources recorded as skipped, and then throws
  `OperationCanceledException`. An `IOException` / `UnauthorizedAccessException` from a state save (retried 5 times with a growing pause first) fails
  that resource (`StateSaveFailed`: the object may exist without an entry) or adds a `StateSaveFailed` error diagnostic; it never crashes the run.
- **One-time secrets**: when `--secure-outputs-file` is set, a password credential's `secretText` (and a generated user password) is merged into the file
  (`oneTimeSecrets` → resource id, written atomically through `OwnerOnlyFile`) right after the apply that produced it, before the state records the resource.
  `SecureOutputsFile` validates the path before the run, merges the secure outputs afterwards and never shrinks or replaces an existing file; a file that
  is not valid JSON is kept as `<file>.previous` (owner-only like the file itself). The read-merge-write runs under an exclusive handle on `<file>.lock` (released by the operating system even when a run dies; a second run waits up to 30 s), so two runs sharing one file never lose each other's one-time secret; the generated password of a user is registered with the scrubber before the request that carries it is sent.
- **Error texts never carry a secure value**: the planner and the executor register every secure parameter value (and the tainted leaves of evaluated
  properties and provider results) with `SecretScrubber`, and every error that reaches the state (`operations[].error`), the result, the what-if rows and the
  log is passed through `SecretScrubber.Scrub` first.
- Replication lag: an instance whose dependency was **created in this run** carries
  `RetryHint.ExpectReplicationLag`; providers put it on their write requests (`GraphRequest.Hint`) so a 404 /
  "does not reference a valid application object" is retried for up to 90 s. Reads use the default hint (a
  404 on read means "create"). `HttpGraphClient` retries every call itself; `PlannerOptions.RetryPolicy` adds an
  outer policy only for tests that drive a fake client.
- **Idempotency of writes** (`GraphRequest.Idempotent`, `BatchRequest.Idempotent`): GET, HEAD, PUT, DELETE and PATCH may be repeated
  blindly; **POST may not** unless the caller marks it (`WithIdempotent(true)`, e.g. an action such as `/restore`). A non-idempotent request is
  still retried after a 429 (Graph rejected it before processing), a replication-lag 404/400, a 401 token refresh and a failure before anything was
  sent (DNS, refused connection, TLS). A timeout, a dropped connection, an oversized/unreadable response or any 5xx is **not** retried: it surfaces as
  `GraphTransportException.Ambiguous == true` / `GraphException.IsAmbiguous == true` ("the outcome is unknown"), and the executor re-reads the object by key
  instead of repeating the write. A `$batch` envelope is idempotent only when every sub-request is; a failed non-idempotent sub-request is re-sent
  individually only after a 429 or a replication-lag answer, and a missing sub-response is an (ambiguous, for a write) error, never a success.
  When the envelope of a **later** chunk (after the first 20 sub-requests) fails after its retries, the earlier chunks' results are kept: the failed chunk's items carry the
  failure as `BatchResponse.Error` (`IsAmbiguous` for a write that may have followed a commit, never for a read or a 4xx) and the chunks after it are not sent
  (`BatchNotSent`, never ambiguous); only a failure of the **first** chunk throws.
- **A 401** is retried once with a new token. A `WWW-Authenticate: Bearer ... claims="..."` challenge (continuous access evaluation) is decoded and the new token is
  requested with those claims (`TokenRequestContext.Claims`, which makes MSAL skip its cache). When the credential hands back the very token that was just rejected,
  repeating the call cannot succeed: the call ends with a `NotAuthenticated` error (exit code 3) naming the Graph error code and request id, without a second request.
- **Timeouts**: a per-call timeout that expires while the TCP connection of the request is still being made, with no other call of the client having finished in
  the meantime, proves nothing was sent: it is not ambiguous and a write is retried. Every other timeout of a write is ambiguous (the request may have been sent over a pooled
  connection), and so is a timeout on a handler that does not report connection progress (tests).
- **Sovereign clouds**: the providers write public-cloud references (`@odata.id`, `owners@odata.bind`, `members@odata.bind`, any key ending in `@odata.id` or `@odata.bind`, string or
  array of strings, at any depth, also inside a `$batch` envelope); `HttpGraphClient` rewrites them to the Graph host of the active cloud (`graph.microsoft.us`, `dod-graph.microsoft.us`,
  `microsoftgraph.chinacloudapi.cn`) when it serialises the body.
- **Transport limits** (`GraphOptions`, `RetryOptions`): response bodies are read with a limit of 16 MiB counted after decompression
  (`MaxResponseBytes`, error kind `ResponseTooLarge`; Key Vault 1 MiB), request bodies are limited to 4 MiB (`MaxRequestBodyBytes`, `RequestTooLarge`),
  `GetAllPagesAsync` stops after 2,000 pages or 500,000 items (`MaxPages`, `MaxItems`, `PagingLimitExceeded`; a repeated `@odata.nextLink` is still an
  `InvalidResponse`), a server `Retry-After` is honoured up to 120 s per attempt (longer values are clamped and logged), and one logical call never
  spends more than 10 minutes retrying (`RetryOptions.MaxRetryAfter`, `MaxTotalTime`).
- **Paths**: `GraphPath.Segment` refuses `.`, `..` and control characters (`System.Uri` collapses dot segments even when percent-encoded, so there is no
  safe encoding); `HttpGraphClient` additionally fails closed (`UnsafeUrl`, nothing sent) when a request path, `@odata.nextLink` or `$batch` sub-request URL
  contains a dot segment or is rewritten by URL normalisation.
- **Secret values** (`SecretScrubber`): whoever learns a secret value at run time (a Key Vault secret, a generated password) calls
  `SecretScrubber.Register(value)` (ignored below 3 characters; its URL-encoded (upper and lower case hex), JSON-escaped (also lower case `\u003c`), HTML-encoded and base64 forms are
  registered too). Every Graph and transport exception, the error parser, `SecretRedactor.RedactText` (so all logging) and Key Vault failures then replace it with `***`. The scan is a single
  pass over a sorted pattern table (cost independent of the number of values); overlapping matches are masked as one run. `Register(value, secureDeclared: false)` marks a value that was only
  derived from a secret (taint tracking): below 8 characters it is matched on word boundaries only, so a short derived value such as `pass` does not mask `passwordCredentials`. The registry is
  `AsyncLocal`: call `SecretScrubber.Begin()` (or `Register`) at the top of the run, before the work that may format an error is started. A nested `Begin()` joins the outer registry instead of
  copying it, so what an inner scope (the deployment runner) registers is still masked by the program-level catch-all after the inner scope ends.

### Graph batch item codes

`HttpGraphClient.BatchAsync` (`$batch`, 20 sub-requests per envelope) returns one `BatchResponse` per sub-request; a failed one carries a `GraphException` in
`BatchResponse.Error`, whose `Code` is the Graph error code of that sub-request (`Request_ResourceNotFound`, `Request_BadRequest`, ...). Two codes are EARM's own, set
when an **envelope** of a later chunk fails after the earlier chunks were already reported. They are string literals on the item, not entries of
`DiagnosticCodes` / `EngineDiagnosticCodes`, so they have no row in [diagnostics.md](diagnostics.md); scripts and CI logs can match on them:

| Code | On which items | Meaning |
|---|---|---|
| `BatchEnvelopeFailed` | The items of the envelope that failed at the **transport** level after its retries (a timeout, a dropped connection, a response that was too large or unreadable, no sign-in) | There is no Graph error code to copy, so the item gets this one; its status is mapped from the failure (401, 504, 400 or 502) and the message starts with "The $batch envelope failed". It is **ambiguous** (`IsAmbiguous`) for a write that may have followed a commit; never for a read. When the envelope failed with a Graph error instead (for example a 4xx), the items keep that error's own code. |
| `BatchNotSent` | The items of the chunks **after** the failed envelope | The chunk was never sent ("nothing was written for this sub-request"). Never ambiguous: safe to run again. |

Only a failure of the **first** envelope throws; later failures are reported per item so the earlier results are kept. A sub-request that was re-sent on its own
after a 429 or a replication-lag answer and failed again reports the code of that failure (empty when it was a transport failure of the single request).

### Symbolic names

A resource whose `name` depends on another resource (the sample's service principal,
`"name": "[reference(variables('apiName')).appId]"`) gets a **symbolic id** during static evaluation: the name
segment is the unknown's symbol (`ref(/providers/Microsoft.Entra/applications/x).appId`). Everything that refers
to it, by `dependsOn` or by `resourceId('…/servicePrincipals', reference('x').appId)`, computes the same symbol
and therefore the same id, so the graph is complete before anything exists.

Right before its batch runs (sequentially, so the resource index is never mutated while expressions evaluate in
parallel) `SymbolicNames.Resolve` re-evaluates the name with the live resolver. Once the input is live the name
becomes concrete: `ResourceInstance.Id` changes (`PlannedId` keeps the symbol), the concrete id is added to the
resource index so `reference(resourceId(...))` of the concrete id succeeds, and the live object is published
under **both** ids. In what-if, when the input does not exist yet, the row keeps the symbolic id and is marked
`IsSymbolicId`.

### Existing resources

`reference(resourceId(...), apiVersion)` is the ARM "existing resource" idiom. `LiveReferenceResolver` forwards
such lookups to the provider (`IExistingResourceLookup.ReadExistingAsync`, implemented by `ProviderBase` for
every name-keyed type) and caches the result. Lookups that cannot be performed (no provider, GUID-named type,
unresolvable key) stay placeholders and are listed in `DeploymentPlan.UnresolvedReferences` / the what-if
diagnostics.

### Entra lookups and `tenant()` / `environment()`

`appRole(ref, value)`, `permissionScope(ref, value)` and `roleTemplate(nameOrId)` resolve ids in three steps, in
the expression engine (`EntraFunctions`): the live object of a template resource named by `ref` (an application's
`appRoles` / `api.oauth2PermissionScopes`, a service principal's `appRoles` / `oauth2PermissionScopes`: no request,
no replication lag), the embedded tables of `EArm.Core.WellKnownIds` (Microsoft Graph permissions, built-in
directory roles: no request), and finally the tenant through `IEntraLookup`. The engine's implementation is
`Planning/GraphEntraLookup.cs`: `GET /servicePrincipals(appId='…')?$select=id,appId,appRoles,oauth2PermissionScopes`,
falling back to `GET /applications(appId='…')?$select=id,appId,appRoles,api` when no principal exists yet, and
`GET /roleManagement/directory/roleDefinitions?$filter=displayName eq '…'` for custom role names. Answers are cached
per run (failed reads are not); `DeploymentPlanner.PlanAsync` puts one instance into the deployment-wide
`EvaluationContext.EntraLookup`, so both the what-if read phase and execution resolve live. Offline (validation, the
static analysis the dependency graph is built from) the context holds `PlaceholderEntraLookup` and the functions
yield deploy-time unknowns (`appRole('<appId>', 'Value')`).

A `ref` that names a template application or service principal is an implicit dependency, exactly like
`reference(name)`: `ReferenceScanner` records it as a `Reference` occurrence, so the graph orders the lookup after the
target's apply. A `ref` that is an appId GUID carries no dependency and is never evaluated by the scanner (a live
scan therefore never triggers a tenant request); `reference('app').appId` as `ref` is covered by the nested
`reference()`.

`tenant()` returns `PlannerOptions.Tenant` and `environment()` `PlannerOptions.Environment`. The CLI fills them
after preflight with `DeploymentEnvironment.TenantFromOrganization` (the preflight's single
`GET /organization?$select=id,displayName,verifiedDomains` → `{ tenantId, defaultDomain, displayName }`; when the identity may not read the organization the
read is logged and `tenant()` stays unknown) and `DeploymentEnvironment.Environment(cloud)` (`{ name, graph, authentication.loginEndpoint }` of the
selected `EARM_CLOUD`, from the fixed cloud table in `GraphCloud`). The preflight (`GraphPreflight`) also reads the token's `tid` and `oid` claims, so
`PlannerOptions.SignedInPrincipalId` is known without `GET /me` (an app-only token has no `/me`).
Planner tests that construct `PlannerOptions` directly leave both `null`, which keeps the functions unknown.

## Edge pass (`EArm.Engine.Edges`)

Relationships and assignments are arrays inside `properties` that
Graph applies through `$ref` navigations (or assignment objects), not through PATCH. The loader keeps them in
`properties`; `InstanceEvaluator` splits them off into `ResourceInstance.EdgeProperties` (evaluated leniently at
object time: an expression that cannot be evaluated yet becomes a deploy-time placeholder instead of failing the
object), `DependencyGraph` never turns a `reference()` inside them into an object-pass dependency, and the edge
pass re-evaluates them against the live resolver once every object exists, so `reference()` yields real ids and
mutual declarations (`user.memberOf` ↔ `group.members`) are never cycles.

### Model

- **`Edge`** = `(kind, targetId, principalId[, targetCollection][, extra])`, ids are Graph object ids.
  `EdgeKind`: `GroupMembership`, `Ownership`, `AdministrativeUnitMembership`, `Manager` (part A, shipped),
  `AppRoleAssignment`, `DirectoryRole` (part B, registered by `EArm.Providers`). `EdgeKinds.TryFromRelationship` maps the overlay's
  `relationships[].kind`; the action-backed `license` kind is not an edge (providers apply `assignedLicenses` as a
  post-write action).
- **`IEdgeKindHandler`** (one per kind, in `EdgeKindRegistry`; `PlannerOptions.EdgeKinds`, default = part A):
  `ResolveItemAsync` (a declared item → the other side's id, collection, extras; placeholders and invalid items),
  `ReadTargetAsync` (existence, dynamic flag, collection, and the principals attached when asked),
  `ReadPrincipalAsync` (the targets a principal is attached to, read from the principal's side),
  `AddRequest` / `RemoveRequest` (`BatchRequest`s), `RecordEntry` / `FromRecordAsync` (no longer called: edges are not
  recorded; they go with the state subsystem), `IdentityOf` (de-duplication key), `GraphIdsOf` (the
  object ids on both sides, for the replication-lag hint; defaults to target + principal), and the flags
  `IsSingleValued`, `RejectsDynamicTargets`, `WarnOnSignedInPrincipalRemoval`, `Phase`.
- **`EdgeDeclaration`**: one entry of one relationship property of one instance (declarer, property, index,
  `SelfId` = the declarer's Graph id or `null` while it does not exist, `OtherId`, placeholder flag).
- **`EdgeGraphContext`**: the Graph client inside the plan's retry policy, the live resolver (template objects by
  Graph id, so a just-created target needs no read), per-pass caches (`memberOf` reads shared by the membership
  kinds, `directoryObjects` type lookups) and the signed-in principal (`PlannerOptions.SignedInPrincipalId`, else
  `GET /me?$select=id` once, when an ownership removal needs it).

### Algorithm (`EdgePlanner` → `EdgePlan` → `EdgeExecutor`)

1. **Collect**: for every applied (or, in what-if, planned) instance that declares an edge property: re-evaluate `properties`, keep the relationship subset, normalise each
   entry through the kind's handler. Duplicates inside one array collapse; an unknown handler kind yields the
   `EdgeKindNotSupported` warning.
2. **Authoritative sets** (target side: `groups.members`, `owners`, `administrativeUnits.members`): desired set =
   the declared ids ∪ the ids of template principals whose principal-side property names this target (those
   declarations are *governed* by the set). Live set = `GET /{collection}/{t}/{members|owners}?$select=id`.
   Adds = desired − live; removes = live − desired (replace semantics). A declared empty array removes
   everything. A dynamic group / AU target is a validation error for the target row and every governed row.
   **A set is never pruned when a declared resource that could contribute to it did not apply** (it failed, or
   was skipped under `--continue-on-error`, or has an error row in the what-if) and its declaration names a principal-side property of the set's kind (`memberOf`, `ownerOf`, `administrativeUnits`):
   that resource's declarations are unknown, so removing "everything not declared" would remove its live memberships and ownerships. The adds still happen; a warning (`EdgePassSkipped`,
   "was not pruned") names the resource; the next run, with the resource applied, prunes.
3. **Principal side, not governed** (`memberOf`, `ownerOf`, `administrativeUnits` naming a target the template
   does not declare authoritatively, for example a pre-existing group via `reference(resourceId(...), apiVersion)`):
   existence is read from the principal (`GET /{users|servicePrincipals|groups}/{p}/memberOf`, filtered by
   `@odata.type`; `GET /{users|servicePrincipals}/{p}/ownedObjects`), the target is read once (existence, dynamic
   flag, `ownerOf` target collection from the template or `GET /directoryObjects/{id}?$select=id`), missing edges
   are added. Nothing outside the template is removed.
4. **Principal side is ensure-only**: a principal-side edge that is no longer declared is **never removed**, whoever created it. Only
   an authoritative target-side set (step 2) prunes. (Earlier versions removed the edges a deployment had recorded in its state; there are
   no records any more.)
5. **`manager`** (single-valued): a declared id replaces the live manager (`PUT /users/{u}/manager/$ref`), an
   explicit `null` removes it (`DELETE`), an absent property leaves it untouched.
6. **De-duplication**: operations are keyed by `IdentityOf(edge)`; the same edge declared from both sides (or by
   a standalone resource, part B) is one write attributed to every declaring row. The same identity with
   non-equivalent extras is a validation error on every declaring row.
7. **Apply** (`EdgeExecutor`, create only): part A then part B, adds before removes, grouped by (Graph API, retry
   hint); `RetryHint.ExpectReplicationLag` when either side was created in this run; one request per edge up to
   20 per group, `$batch` beyond that (sent once, failures read per item). "Already exists" on an add and 404 on a remove are idempotent successes.
   Operations attributed only to rows with validation errors are skipped. An add whose answer was lost (5xx, timeout) is read back and counted as created when the edge exists. The caller's cancellation is
   checked between operations (see Failure handling).
8. **Report**: the row's `properties.<property>` deltas (`PropertyChangeType.Array` with `[n]` `Create` /
   `Delete` children; `manager` as a scalar `Create` / `Modify` / `Delete`) are merged into the declaring
   resource's change (`EdgeChanges.Merge`: a `NoChange` object row with edge changes becomes `Modify`). In
   `create` a row whose edges fail validation or application turns its operation into `Failed`
   (`EdgeValidationFailed` / `EdgeApplyFailed`); in what-if the row carries `unsupportedReason`. Removing the
   signed-in principal from `owners` raises `SignedInPrincipalRemoved`. When an earlier batch failed (fail-fast)
   the pass does not run and `EdgePassSkipped` is reported.

9. **Reads Graph does not allow** (warnings, never failures; the property is left alone: no operations, no deltas):
   - **`ownerOf` on a user with an application (app-only) token.** `GET /users/{id}/ownedObjects` has no application
     permission (Learn: user-list-ownedobjects). When preflight reports the principal type `application` (the token has no
     `scp` claim; `PlannerOptions.AppOnlyToken`), the planner does not attempt the read: the row carries an
     `EdgeUnsupportedInAppMode` warning (also a warning diagnostic) that names the alternative, declaring the user in
     `owners` on the application, service principal or group (`ownerOf` on a service principal reads fine). Writes on the target side
     work with app-only tokens, so `owners` is unaffected.
   - **`manager` read answers 403.** `GET /users/{id}/manager` can be refused for an identity without `User.Read.All`:
     `ManagerReadForbidden` warning, the manager relationship is neither read nor written for that user, the run goes on.

Templates without relationship properties never enter the pass: no reads, no diagnostics. Edges are not recorded in the
state (a state file written by an earlier version may still carry `relationships`; nothing reads them).

### Adding a kind (app role assignments, directory roles)

Implement `IEdgeKindHandler` with `Phase = EdgePhase.B` in `EArm.Providers`, attribute it
`[EntraEdgeKind(EdgeKind.<Kind>)]`, and `ProviderDiscovery.CreateEdgeKinds(catalog)` (wired by `EngineServices` into
`PlannerOptions.EdgeKinds`) registers it next to the part A kinds. Return your assignment id from the add response
through `RecordEntry` / `FromRecordAsync` so removals can `DELETE` by id. `ResolveItemAsync` is asynchronous so name
resolution (`resourceAppId` → service principal, `appRole` value → id, role names → template ids) happens there;
`IdentityOf` must include the extras that distinguish assignments. Two conventions the planner relies on: the
principal-side existence read and the tracked removals are keyed by `Edge.TargetId`, so a kind whose assignments are
not unique per target encodes the assignment in it (`{resourceId}/{appRoleId}`) and overrides `GraphIdsOf` to return
the real object ids for the replication-lag hint; and previous state records are matched to declarations by
`IdentityOf`, so a record may carry extras (the assignment id) a declaration cannot know. The standalone resource
types (`Microsoft.Entra/appRoleAssignments`, `roleAssignments`) are applied in the object pass, before the edge pass,
so a principal-side item for the same assignment finds it in place and the pass neither writes nor records it; the
executor, the state recording and the what-if rendering need no change. Walk-through in
[providers.md, "Adding an edge kind"](providers.md#adding-an-edge-kind).

## State (`EArm.Engine.State`)

One `StateFile` per deployment name, keyed by ARM-style resource id, records which Graph objects
the deployment manages (`id` = Graph id, `key`, `parentId`, `contentHash`, `dependsOn`, `lastWriteOn`, provider
`extra`, `relationships` for the edge pass) plus the run metadata (`tenantId`, `createdOn`, `lastDeployedOn`,
`lastStatus` = `Running | Succeeded | Failed`, `lastMode`, `lastTemplateHash`) and the per-resource `operations` of
the last run (`deployment operation list`). It is safe to commit: ids and hashes only. `StateSerializer` refuses to
write an entry that carries a secret (`SecretJson`) or a deploy-time placeholder (`UnknownJson`), and
`contentHash` (`StateHashing`) is the SHA-256 of the canonical evaluated properties with those leaves removed
(informational: the diff is always live-vs-desired).

```json
{ "schemaVersion": 1, "deploymentName": "orders-dev", "tenantId": "1111…", "createdOn": "…", "lastDeployedOn": "…",
  "lastStatus": "Succeeded", "lastMode": "Incremental", "lastTemplateHash": "sha256:…",
  "resources": {
    "/providers/Microsoft.Entra/applications/contoso-dev-api":
      { "type": "Microsoft.Entra/applications", "apiVersion": "2026-10-01-preview", "key": { "uniqueName": "contoso-dev-api" },
        "id": "568e…", "contentHash": "sha256:…", "dependsOn": ["/providers/Microsoft.Entra/users/bg-orders@contoso.com"], "lastWriteOn": "…" },
    "/providers/Microsoft.Entra/applications/contoso-dev-api/passwordCredentials/ci-secret":
      { "type": "Microsoft.Entra/applications/passwordCredentials", "apiVersion": "2026-10-01-preview", "key": { "displayName": "ci-secret" },
        "id": "<keyId>", "parentId": "568e…", "contentHash": "sha256:…", "dependsOn": ["…/applications/contoso-dev-api"],
        "lastWriteOn": "…", "extra": { "keyId": "…", "endDateTime": "2027-10-01T00:00:00Z" } } },
  "operations": [ { "resourceId": "…", "type": "…", "status": "Succeeded", "changeType": "Create", "batch": 2,
                    "startedOn": "…", "finishedOn": "…", "graphId": "…" } ] }
```

- **The state is a hint, not an authority** (it is committed and can be edited; the full model is [below](#state-trust-model)). (1) **Graph ids have a shape**: `StateIdShapes` accepts a GUID for
  directory objects, an opaque token (`[A-Za-z0-9_=-]`) for role assignments, app role assignments and OAuth2 permission grants, `c1`..`c99` for
  authentication contexts; anything else (`me`, `../x`) fails the load with a clear error. **An entry's `key` is the key its resource id implies** (`StateKeyRules`): a name-keyed type carries exactly its `nameMapsTo` property
  with the last name segment as value (an application may add its `appId`), a GUID-named type exactly its catalog key properties; an entry that says otherwise (`key: { "securityEnabled": "true" }` on a group with
  a victim's id) fails the load with `StateEntryInvalid` naming the entry,. An entry without any key gets the derived key. The ids recorded under `relationships`
  (target ids, assignment ids, principal/resource/role ids, scopes) are shape-checked at load too. (2) **A pinned read is verified** (`ProviderReads.ReadVerifiedAsync`,
  executor and planner): the returned object's key properties (`uniqueName`, `appId`, `userPrincipalName`, `displayName`, the composite members) must match the
  desired key, case-insensitively; a mismatch discards the pinned result, warns `StateEntryMismatch` and looks the object up by key instead, so an object
  with another key is never PATCHed. (3) **No delete reads the state** (Complete mode only reports; `deployment delete` and the old object of a recreate are
  found by key, and the signed-in principal and service principals with a foreign `appOwnerOrganizationId` are never deleted: `DeleteGuards`, `ProtectedObject`).
  (4) Entries still carry `provenance` (`created` | `adopted`) and an optional `createdDateTime`; nothing reads them any more. `schemaVersion` stays 1. (5) **One object, one resource**: resource ids compare case-insensitively (Graph matches its keys that way; GUID-shaped names are lower-cased), so
  `MyApp` → `myapp` is a rename of one resource, not a delete and a create; two declarations whose ids differ only by case are a `DuplicateResourceId`
  error; two declarations that resolve to the same Graph object fail the second at apply time (`DuplicateGraphObject`).
- **A rename outside the template** (a user's UPN, a group, named location or policy `displayName` changed in the portal): the pinned object no longer has the key and nothing else has it. The resource **fails** with
  `StateEntryMismatch` ("object <id> was renamed outside the template ... restore the name, or run `earm state forget`") instead of planning a create that would duplicate the object and orphan the original; the what-if shows an
  error row. (Composite-keyed types have no rename: a key that differs is a new object, as before.)
- **Keys are always concrete.** A resource whose name is symbolic (`[reference('app').appId]`) is recorded under
  its resolved id; copy iterations under their expanded ids. `FileStateSink` throws if asked to record a symbolic
  instance. `StateSerializer.ParseResourceId(id, type)` rebuilds the identifier from a key and the entry's type
  (no catalog needed, so unknown types still round-trip).
- **Store** (`IStateStore` / `FileStateStore`): `<dir>/<deploymentName>.json`, written to a temp file and moved into
  place; `LoadAsync` goes through `StateMigrator` (`schemaVersion` 1; a newer version → `StateSchemaUnsupportedException`).
  The directory is `--state-dir`, else `EARM_STATE_DIR`, else `.earm/state` next to the template
  (`StateDirectory.Resolve`). Deployment names are file names: `StateNames` allows `[A-Za-z0-9][A-Za-z0-9._-]*`.
  `tests/EArm.TestSupport/InMemoryStateStore` is the in-memory counterpart (same serializer, records every save).
  Names are matched with `\A…\z` (a trailing newline is not a name), may not end with `.` and may not be a Windows reserved device name (`CON`, `PRN`, `AUX`,
  `NUL`, `COM1`-`COM9`, `LPT1`-`LPT9`, with or without an extension). The state directory and every state, lock and temp file must be ordinary: a symbolic link or
  junction, or a state directory that is a file, throws `StateStoreException` (exit 1; `deployment list --state-dir <file>` is an error, not "No deployments."). A link **anywhere above** the state directory (an ancestor
  folder: `PathSafety.FindLinkedComponent`; the entries directly under the root on Unix-like systems are skipped) is refused too, and so is a link above the folder of the secure outputs file. A state file over 16 MiB and a lock file over 1 MiB
  are refused / not read (`StateStoreException`). Reads of the state allow a concurrent writer (`FileShare.ReadWrite | Delete`, three retries) and the atomic replace retries a refusal, so a reader and a saver never collide on Windows.
  The resolved directory is logged at Debug; a directory outside the user profile, the current directory and the template folder is warned about. Every state command
  reports `IOException` / `UnauthorizedAccessException` as a one-line message with exit 1.
- **Lock**: `<deploymentName>.lock` created with `FileMode.CreateNew` holding pid/host/user/UTC time and a random lock id; `LockAsync`
  waits up to the given time (`--lock-timeout`, 10 s) then throws `StateLockedException` (CLI exit 4). The holder rewrites a **heartbeat** timestamp every
  30 s (real time) while it runs, so a long run is never stale. A lock whose newest timestamp (heartbeat, else acquisition) is older than one hour is taken over
  with a warning, **unless** another process of that pid still runs on this host (checked with the process start time, so a reused pid does not count; only for
  up to 24 h). A lock whose holder is a **process of this host that demonstrably no longer runs** is taken over **at once**, whatever its age. The takeover is serialised: the lock file is first renamed to a unique `.stale`
  name (only one taker can win the rename), judged there (still the content that was judged abandoned?) and deleted, or put back untouched when it changed (a holder that heartbeated, or another taker's new lock), so two takers can never
  both end up holding. The holder's **heartbeat** rewrites the file in place through one open handle after checking its `lockId`, so a lock that was deleted (`state unlock`) stays deleted and a lock another holder owns is never rewritten; a
  **release** deletes the file only while it still carries the lease's own `lockId` (after `state unlock` the old run never deletes the next holder's lock).
  `state unlock -n <name>` / `--force-unlock` break it. **The lock is taken before the state is read** (`create`, `delete`, `state forget`, `state adopt`:
  lock → load state → preflight → tenant pin → plan), so two runs never plan against the same stale state; `what-if` never locks (it never writes) and says
  it read an unlocked snapshot.
- **Tenant pin**: the CLI calls `StateFile.EnsureTenant(preflight tenant)` after preflight; a state written for
  another tenant throws `TenantMismatchException` (exit 3). A state without a tenant adopts the current one.
- **Sink** (`FileStateSink : IStateSink`): `BeginAsync` marks the run `Running` (operations cleared); every
  `ResourceAppliedAsync` upserts the entry and **saves after every resource**; `ResourceFailedAsync` records the
  (redacted) error and keeps the previous entry; skipped/ignored resources are recorded too;
  `ResourceEdgesFailedAsync` records an edge failure (edges themselves are no longer recorded: the executor never calls `ResourceEdgesAppliedAsync`,
  `ResourceDetachedAsync` or `ResourceDeletedAsync` any more); `CompletedAsync` sets `lastStatus` /
  `lastDeployedOn` / `lastMode` / `lastTemplateHash`. A crash leaves `lastStatus: Running` and the entries written so far.
  **Recording is not cancellable**: every record after `BeginAsync` describes work that already happened, so the sink
  saves it under its own 30-second bound (`FileStateSink.PersistTimeout`) and the executor passes `CancellationToken.None`;
  a Ctrl+C right after a Graph write therefore still records that object (a generated secret or key id is never
  orphaned), while cancellation keeps stopping new work between resources and batches. **A write in flight is not
  aborted either**: `HttpGraphClient` honours the caller's token for a `POST` / `PATCH` / `PUT` / `DELETE` only until
  the request is sent (token, connection slot, retry waits); once it is on the wire it runs to completion, bounded by
  the request timeout (`GraphOptions.Timeout`, 120 s; a timeout is reported as an ambiguous failure, not as a
  cancellation), and its answer - a created object, a one-time secret - is returned and recorded (secure outputs file,
  then state) before the cancellation takes effect. Reads stay cancellable at once. When the cancellation reaches a
  provider **between** two of its requests (after its first write committed), the executor looks the object up by key
  once more, uncancellably, and records it (the resource is reported failed: "cancelled while the object was being
  created"); when nothing is found the create never happened and the resource is skipped. The CLI gives this end
  phase time to finish: `Program.ProcessTerminationTimeout` is 5 minutes instead of System.CommandLine's 2-second
  default (one request timeout for the write, one for the follow-up lookup, the 30 s state-save bound, with margin;
  `null` is not an option, it would disable the cancellation token and Ctrl+C would kill the process at once).
  `UsersProvider` treats a cancellation of its `assignLicense` step like any failure after `POST /users`: the user and
  its generated password go out as `ProviderException.Partial` and are recorded. Not covered: a multi-chunk `$batch`
  that is cancelled between chunks throws away the per-item results of the chunks already sent (the edges are read
  back on the next run). The executor also passes the pinned Graph id to
  `ReadAsync` (`ExecuteOptions.State`); a 404 on the pinned id is drift: the provider falls back to the key, the
  resource is planned as `Create` if nothing is found, and the sink re-pins the entry.
- **No delete is driven by the state.** Complete mode only reports undeclared objects, and `deployment delete` finds the declared objects by
  key from the template (see [Orphan report and `deployment delete`](#orphan-report-and-deployment-delete)). The state is write-only for the
  engine apart from the extra data of unchanged resources; it goes away with the state subsystem.

### State trust model

The state file is **not cryptographically protected** and EARM does not pretend otherwise: it is a plain JSON file in a directory the user chose, committed to a repository by design. Anyone who can write it can make EARM believe that an
object belongs to a deployment. What EARM does about that, and what it cannot do:

| What | Defence |
|---|---|
| Re-pointing an entry (`id`) at somebody else's object | The entry's `key` must be the key its resource id implies (load error `StateEntryInvalid`). Identity is resolved live by key, and nothing is deleted on the strength of an entry: Complete mode only reports, `deployment delete` works from the template. |
| Recorded edge ids (`relationships`) | Shape-checked at load; never read (principal-side edges are ensure-only, nothing is removed because it was recorded). |
| A pinned id that does not match the key | Ignored (the object is looked up by key); never PATCHed; a renamed object fails the resource (`StateEntryMismatch`). |
| **An attacker who writes a *consistent* entry** (the true key and id of a real object, `provenance: created`, a copied `createdDateTime`) | Harmless for deletes: no delete reads the state. |

Therefore: keep the state in a **trusted location** (a private, access-controlled storage or a repository branch with review), do not take state files from pull requests of untrusted authors, and protect `.earm/state` the way you protect the pipeline that deploys: for a Git repository add `.earm/state/` to **CODEOWNERS** so a change of a state file needs the review of the deployment owners,
and protect the branch that the deployment runs from. A tampered file that fails the integrity rules is refused loudly; a consistent forgery is only visible in review.

## Orphan report and `deployment delete`

**Complete mode never deletes.** `OrphanReport` (`EArm.Engine.Planning`, run by the planner in what-if and by the executor after the outputs) lists, for every type the
template declares whose provider implements `IOrphanCandidateSource`, the live objects in a bounded scope (`OrphanScope`):
`Collection` (Conditional Access policies, named locations, custom authentication strengths, custom role definitions, administrative units, authentication contexts),
`ChildrenOfDeclaredParents` (federated identity credentials and password credentials of the declared applications, the service principals of the declared applications)
and `DeclaredPrincipals` (role assignments, app role assignments, OAuth2 permission grants of the principals / clients the declared resources of the type name). A
candidate whose Graph id no declared resource of the type holds is an orphan: an `Ignore` row marked `ResourceChange.Orphan` (`Undeclared`; `Retained` for the
`DeclaredPrincipals` scope, with an `AssignmentRetained` warning). Incremental mode runs only the `DeclaredPrincipals` scope (the old tuple of a changed assignment is
retained and still grants access, so the what-if says so). A type is skipped with `OrphanReportIncomplete` when one of its declared resources is not settled in the
run (what-if: an error row or an unresolved key; executor: not succeeded) or its listing fails; applications, groups and users are tenant-wide and never listed (an
`Info` diagnostic). Orphans are in `DeploymentPlan.Orphans` (what-if) and `DeploymentResult.Orphans` (create); they never count as changes.

**`deployment delete`** (`TemplateDeleter`, `EArm.Engine.Execution`): the CLI plans the template in what-if mode (identity by live key, read-only), then
`TemplateDeleter.FindDeclaredObjects(plan)` lists the declared objects that exist, in **reverse dependency order** (the batches reversed, ties by
`ApplyPhases.DeleteOrderOf` and resource id), each with its `displayName`, the guard's refusal (`DeleteGuards`: the signed-in principal and service principals
owned by another organization are kept) and the provider's `IDeleteWarningSource` risk (groups: distribution lists and mail-enabled security groups, which Learn
does not list as restorable). After the confirmation `DeleteAsync` re-reads each object by key (it must be the listed one), deletes it through the provider inside
the retry policy (a 404 counts as gone) and stops at the first failure unless `continueOnError`; a delete in flight is never cancelled, a Ctrl+C skips the rest.
No state is read or written.

## Key Vault parameter references (`EArm.Engine.KeyVault`)

A parameter file entry `{ "reference": { "keyVault": { "id": "…/vaults/contoso-kv" }, "secretName": "bg-initial" } }`
is bound as a pending reference by the loader. For `create` and `what-if` the CLI resolves it after preflight with
`KeyVaultSecretResolver` (`KeyVaultClient`: `GET https://<last id segment or bare name>.vault.azure.net/secrets/<secret>[/<version>]?api-version=7.4`
with a bearer token for the Key Vault scope; the vault suffix comes from the cloud table; names are validated against the Key Vault character rules before the URL is built) and binds the
value **secure-tainted** whatever the declared type. A failure (`KeyVaultResolutionException`) is reported as
`KeyVaultReferenceUnresolved` with the (redacted) Key Vault answer (exit 3). `deployment validate` stays offline. A
pending reference that reaches `PlanAsync` is still an error for `create` and a warning plus deploy-time placeholder
for `what-if`. Tests script the vault with `tests/EArm.TestSupport/ScriptedGraphHandler`.

## Provider contract (`EArm.Engine.Providers`)

```csharp
public interface IResourceProvider
{
    string TypeName { get; }                                    // "Microsoft.Entra/applications"
    IReadOnlyDictionary<string, GraphApi> ApiVersions { get; }  // "2026-10-01-preview" -> V1, "2026-10-01-preview.beta" -> Beta
    IReadOnlyList<string> RequiredPermissions(ResourceInstance? desired);
    IReadOnlySet<JsonPath> ImmutableProperties { get; }
    IReadOnlyList<RelationshipSpec> Relationships { get; }      // edge properties (applied by the edge pass)
    Task PrepareAsync(ResourceInstance desired, IGraphClient g, CancellationToken ct);  // default no-op: sugar + live validation before the key
    ResourceKey ResolveKey(ResourceInstance desired);           // from name / parent / composite properties
    Task<ExistingResource?> ReadAsync(ResourceKey key, string? stateId, IGraphClient g, CancellationToken ct);
    ResourceChange Plan(ResourceInstance desired, ExistingResource? existing, PlanOptions o);
    bool CreateIsRepeatable { get; }                            // default false: only an upsert (PATCH + create-if-missing) may be repeated after an unknown outcome
    Task<AppliedResource> CreateAsync(ResourceInstance desired, IGraphClient g, CancellationToken ct);
    Task<AppliedResource> UpdateAsync(ResourceInstance desired, ExistingResource existing, ResourceChange change, IGraphClient g, CancellationToken ct);
    Task DeleteAsync(StateEntry entry, IGraphClient g, CancellationToken ct);
}
```

- `ResourceInstance` is the evaluated resource: `Id` / `PlannedId`, `Api`, `Properties` (object properties,
  edge properties already split into `EdgeProperties`; the edge pass re-evaluates those, so a provider reads
  `EdgeProperties` only for action-backed properties such as `assignedLicenses`), `Parent` / `ParentGraphId`,
  `Copy`, `RetryHint`.
  Properties may contain **secret leaves** (`SecretJson`: `securestring` parameter values, and every leaf under
  the overlay's `secure` paths, which the engine taints when it evaluates the instance) and, in what-if,
  **unknown leaves** (`UnknownJson`); never use `JsonNode.DeepClone` on them (`JsonValues.Clone` /
  `SecretJson.Clone`).
- **A create or update that writes in several steps** (create the object, then owners, licenses, a restore, a federated credential...) must not lose what it already did when a later step fails: throw
  `new ProviderException(code, message, id) { Partial = appliedSoFar }` (the `AppliedResource` of the object as far as it got, including its one-time secrets). The executor records the object, its provenance and its
  one-time secret and reports the resource failed; a provider that does not do this is covered by a re-read by key after a failed create (the object is recorded, but a value Graph only returned in the lost response,
  such as a generated password, is gone). `UsersProvider` does it for `assignLicense`; the other providers' post-create steps (group owners and licenses, restore, ...) rely on the re-read until they adopt `Partial`.
  `CreateIsRepeatable` says whether the create is an upsert (`ProviderBase`: `uniqueName`- and id-keyed types and federated credentials); any other create is never repeated by the executor's retry policy after a 5xx or a lost connection.
- `PrepareAsync` runs on the evaluated instance right before `ResolveKey`, in the planner (what-if) and the
  executor, inside the retry policy with the default hint. It is where a provider resolves name sugar against Graph
  (`resourceAppId` → `resourceId`, `appRole` → `appRoleId`, `scopes` → `scope`) by rewriting `Properties` in place,
  and where live-data validation throws `ProviderException` (an unsupported row in what-if, a failed operation in
  create). Not called for a still-symbolic instance (its `Plan` must tolerate the raw sugar).
- `ResourceKey`: the Graph key (`uniqueName`, `userPrincipalName`, `appId`, `displayName`, composite
  properties), `ParentKey` / `ParentGraphId` for child types, `Api`, `UnresolvedProperties` (deploy-time
  values; `IsResolved == false` means "cannot look up yet").
- `ExistingResource`: `GraphId`, `Properties` (after `stripOnRead`, the diff view) and `Raw` (what
  `reference()` returns).
- `AppliedResource`: `GraphId`, the `Live` object to publish (may carry secret leaves such as a new
  `secretText`), the `Key`, and `Extra` data for the state file (never a secret or a placeholder; the sink refuses
  both). `Warnings` are non-fatal observations of the apply (`ProviderWarning(Code, Message)`); the executor reports each as a warning diagnostic against the resource
  (see [providers.md](providers.md#review-1d-behaviours)). On a `NoChange` the executor republishes `ExistingResource.Raw` and carries the entry's recorded `extra`
  forward unchanged, so provider data written at create time (derived ids) survives reruns.
- `PlanOptions`: deployment-wide knobs for `Plan` (`Mode`, `PlanMode`, `AllowCaEnforce`, `OnSoftDeletedConflict`)
  plus `State`, the deployment's state before the run, for providers whose live object cannot carry everything the
  diff needs (no provider reads it any more: password credentials carry their `rotationTag` in the live `displayName`).
  Read-only for providers.
- `LiveSecrets`: the marker contract for one-time secrets (`secretText` / `secretTextAvailable`) between a provider's
  published live object and the output evaluation (pipeline step 8).
- `ResourceChange` / `PropertyDelta`: ARM what-if vocabulary (`Create | Modify | Delete | NoChange | Ignore`,
  modes `Create | Patch | Recreate | Delete | None`, property kinds `Create | Delete | Modify | Array | NoEffect`),
  `Before` / `After` rendering copies (secrets `***`, unknowns `(deploy-time)`), `UnsupportedReason` when the
  change cannot be applied (the executor fails the resource).

### `ProviderBase`

Derive from `ProviderBase(ResourceTypeDefinition)` and implement only the four Graph calls. It supplies, from
the catalog overlay of the type:

- `ResolveKey`: `nameMapsTo` ← last name segment (`uniqueName`, `userPrincipalName`, `appId`, `displayName`,
  `id`), `ParentKey` for `parent/child` names, composite keys from `keyProperties` for GUID-named types;
  `ReadExistingAsync` for `reference(id, apiVersion)`.
- `Plan`: the shared `JsonDiff`: scalars replace; scalar arrays are order-insensitive sets;
  object arrays by `memberKey` (else canonical JSON, order-insensitive); nested objects compared whole with an
  explicit `null` (`Delete`) for every live-only sub-property unless `nullable: false` (`NoEffect`); GUIDs
  case-insensitive; `null` / `[]` ≡ absent; `createOnly` paths skipped on update; secrets never compared
  (`IsSecret`: on an existing object a secret-bearing value is `NoEffect`, so a rerun neither PATCHes it nor counts it as a change; a create still
  sends it, and a new secret reaches an existing object only through the type's rotation, such as a password credential's `rotationTag`, or a
  replacement; the non-secret members of a secret-bearing object are still compared); unknowns flagged (`IsUnknown`). Only declared top-level properties take part (Graph PATCH is a
  merge). An immutable change (a delta on an `immutable` path; for an `immutable: ["*"]` type —
  `ResourceTypeDefinition.ImmutableAll`: password credentials, app role assignments, role assignments — **every**
  effective delta) is `Modify`/`Recreate` when `RecreateOnImmutableChange` is true (it defaults to
  `ImmutableAll`; the executor then deletes and creates), otherwise `UnsupportedReason`. `ImmutableProperties`
  stays the explicitly listed paths (empty for `["*"]`). A provider that overrides `Plan` keeps its own decision:
  password credentials rotate as `Modify`/`Patch` so `UpdateAsync` can add the new secret before removing the
  old one. A create missing `requiredOnCreate` properties (without overlay defaults) is unsupported as well.
- `BuildCreateBody`: object properties + the name injected into `nameMapsTo` (a different declared value is
  `NameMappedPropertyConflict`) + overlay `defaults` + **secrets revealed** (the only place besides
  `--reveal-secure-outputs`; bodies are never logged by the Graph transport).
- `BuildPatchBody`: `JsonDiff.BuildPatchBody` (changed top-level properties in full, nested objects with
  explicit nulls, `null` / `[]` for cleared properties), secrets revealed.
- `StripServerManaged` / `ToExisting`: apply `stripOnRead`.
- `GraphPathFor(key)` / `SubstitutePath`: the overlay path with `{parentId}` / `{resourceId}` substituted.

Put `desired.RetryHint` on write requests. Throw `ProviderException` (with an `EngineDiagnosticCodes` or
provider code such as `AmbiguousMatch`) for non-Graph failures; let `GraphException` propagate.

## Test support (`tests/EArm.TestSupport`)

- `FakeGraphClient` / `FakeDirectory`: an in-memory tenant with alternate-key routing, `$filter … eq …`,
  `Prefer: create-if-missing`, pagination (`PageSize`), `$ref` navigations, actions (`addPassword`,
  `removePassword`, `assignLicense`, `updateAllowedCombinations`), `$batch`, a request log (`Requests`,
  `Writes`) and `FakeFailureScript` (replication lag, 429, 403, 5xx, "already exists", transport failures). It does
  not retry; pass a `RetryPolicy` with a no-op `IDelayProvider` through `PlannerOptions.RetryPolicy`.
- `EchoProvider`: a generic catalog-driven provider (`EchoProvider.RegistryForCatalog(catalog)` registers one
  per type) so planner and executor tests run end to end without the real providers. It reads the pinned Graph id
  first (any name kind) and falls back to the key.
- `InMemoryStateStore`: an `IStateStore` over memory that round-trips every save through `StateSerializer` and
  keeps the saved documents (`Saves`) for "saved after every resource" assertions.
- `ScriptedGraphHandler` and `FakeJwtTokenProvider`: an `HttpMessageHandler` that records requests and replays scripted
  HTTP responses (match on method, URL and body), and a token provider with chosen claims or a sign-in failure; the CLI tests
  inject both through `ServiceOverrides` (no environment variables).
