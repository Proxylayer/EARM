# The deployment engine (`EntraArm.Engine`)

How `entraarm deployment what-if` and `entraarm deployment create` turn a template into Graph calls, and
what a resource provider has to implement. Plan references are to the project plan, sections 1.3–1.5, 2.2,
2.4 and 3.3. How the planned changes are rendered (text and ARM-shaped JSON, symbols, exit code 5, golden
fixtures) is in [whatif.md](whatif.md); the template format is in [format.md](format.md), the relationship catalog
in [edges.md](edges.md), the required Graph permissions in [permissions.md](permissions.md) and every verb in
[cli.md](cli.md).

## Pipeline

```
load ──► validate ──► expand ──► graph ──► [per batch: resolve symbolic names ──► key ──► read ──► plan ──► apply] ──► edge pass ──► outputs ──► mode pass
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
     for the edge pass (WI-09) and never create object-pass dependencies, so mutual declarations are not cycles.
     A `reference()` whose target depends on `copyIndex()` is `Deferred` offline (no diagnostic) and rescanned
     per iteration with the concrete copy frame against the expanded index, which yields the edge to that
     iteration (`u1` → `g-1`),
   - the inputs of a **symbolic name** (see below),
   - `mode: serial` copy chaining (iteration *n* waits for chunk *n-1*).

   A cycle is reported as `DependencyCycle` with the full path. The acyclic graph is layered Kahn-style:
   level = 1 + the highest level among the dependencies; one `DeploymentBatch` per level; inside a batch the
   instances are ordered by apply phase (plan 3.3: named locations, auth contexts/strengths, role definitions,
   users, applications → service principals, FICs, password credentials → groups → administrative units →
   assignments/grants → CA policies) and then by declaration order. Phases are only a tie-breaker; dependencies
   decide the batches.
5. **Providers**: every instance type needs a provider in the `ProviderRegistry`; otherwise
   `UnknownProviderException` lists the missing types (CLI exit 1). The registry is empty until the provider
   work items register their types in `EngineServices` (CLI composition root).
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
7. **Edge pass** after all batches and before the outputs: the relationship properties of plan section 1.4
   (`members`, `owners`, `memberOf`, `ownerOf`, `administrativeUnits`, `manager`) are reconciled against the
   live edges; what-if renders the same deltas read-only. See [Edge pass](#edge-pass-entraarmengineedges) below.
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
9. **Mode pass** (`ModePass`, after the outputs): state entries whose resource id is not among the expanded
   instances are detached (Incremental) or deleted (Complete). See [State](#state-entraarmenginestate) below.

### Failure handling

- Default: **fail fast at the batch boundary**. In-flight resources of the failing batch finish; every later
  batch is `Skipped`.
- `--continue-on-error`: only the transitive dependents of a failed resource (`DependencyGraph.TransitiveDependents`)
  are skipped; independent branches keep going. A dependent is recorded `Skipped` / `ResourceSkipped` with the
  reason "it depends on *type 'name'*, which failed" and reported through `IStateSink.ResourceSkippedAsync` at
  the start of its batch, before symbolic names are resolved: it is never attempted, its name is never
  re-evaluated (a symbolic dependent keeps its symbolic id in the result) and it never counts as a failure.
- A `create` that comes back "already exists" (`GraphException.IsAlreadyExists`) is idempotent success: the
  object is re-read by key and adopted.
- Replication lag: an instance whose dependency was **created in this run** carries
  `RetryHint.ExpectReplicationLag`; providers put it on their write requests (`GraphRequest.Hint`) so a 404 /
  "does not reference a valid application object" is retried for up to 90 s. Reads use the default hint (a
  404 on read means "create"). `AzGraphClient` retries every call itself; `PlannerOptions.RetryPolicy` adds an
  outer policy only for tests that drive a fake client.

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

### Entra lookups and `tenant()` / `environment()` (WI-16)

`appRole(ref, value)`, `permissionScope(ref, value)` and `roleTemplate(nameOrId)` resolve ids in three steps, in
the expression engine (`EntraFunctions`): the live object of a template resource named by `ref` (an application's
`appRoles` / `api.oauth2PermissionScopes`, a service principal's `appRoles` / `oauth2PermissionScopes`: no request,
no replication lag), the embedded tables of `EntraArm.Core.WellKnownIds` (Microsoft Graph permissions, built-in
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
after preflight with `DeploymentEnvironment.ReadTenantAsync` (`GET /organization?$select=id,displayName,verifiedDomains`
→ `{ tenantId, defaultDomain, displayName }`; a failed read is logged and leaves `tenant()` unknown) and
`DeploymentEnvironment.Environment(cloudName)` (`{ name, graph, authentication.loginEndpoint }`: constants of the
public cloud named after the session's Azure CLI cloud, since the Graph client only targets `graph.microsoft.com`).
Planner tests that construct `PlannerOptions` directly leave both `null`, which keeps the functions unknown.

## Edge pass (`EntraArm.Engine.Edges`)

Plan sections 1.4, 1.7, 1.8 and 2.4 step 4. Relationships and assignments are arrays inside `properties` that
Graph applies through `$ref` navigations (or assignment objects), not through PATCH. The loader keeps them in
`properties`; `InstanceEvaluator` splits them off into `ResourceInstance.EdgeProperties` (evaluated leniently at
object time: an expression that cannot be evaluated yet becomes a deploy-time placeholder instead of failing the
object), `DependencyGraph` never turns a `reference()` inside them into an object-pass dependency, and the edge
pass re-evaluates them against the live resolver once every object exists, so `reference()` yields real ids and
mutual declarations (`user.memberOf` ↔ `group.members`) are never cycles.

### Model

- **`Edge`** = `(kind, targetId, principalId[, targetCollection][, extra])`, ids are Graph object ids.
  `EdgeKind`: `GroupMembership`, `Ownership`, `AdministrativeUnitMembership`, `Manager` (part A, shipped),
  `AppRoleAssignment`, `DirectoryRole` (part B, WI-12 / WI-15). `EdgeKinds.TryFromRelationship` maps the overlay's
  `relationships[].kind`; the action-backed `license` kind is not an edge (providers apply `assignedLicenses` as a
  post-write action).
- **`IEdgeKindHandler`** (one per kind, in `EdgeKindRegistry`; `PlannerOptions.EdgeKinds`, default = part A):
  `ResolveItemAsync` (a declared item → the other side's id, collection, extras; placeholders and invalid items),
  `ReadTargetAsync` (existence, dynamic flag, collection, and the principals attached when asked),
  `ReadPrincipalAsync` (the targets a principal is attached to, read from the principal's side),
  `AddRequest` / `RemoveRequest` (`BatchRequest`s), `RecordEntry` / `FromRecordAsync` (state format),
  `IdentityOf` (de-duplication key; previous state records are matched to declarations by it), `GraphIdsOf` (the
  object ids on both sides, for the replication-lag hint; defaults to target + principal), and the flags
  `IsSingleValued`, `RejectsDynamicTargets`, `WarnOnSignedInPrincipalRemoval`, `Phase`.
- **`EdgeDeclaration`**: one entry of one relationship property of one instance (declarer, property, index,
  `SelfId` = the declarer's Graph id or `null` while it does not exist, `OtherId`, placeholder flag).
- **`EdgeGraphContext`**: the Graph client inside the plan's retry policy, the live resolver (template objects by
  Graph id, so a just-created target needs no read), per-pass caches (`memberOf` reads shared by the membership
  kinds, `directoryObjects` type lookups) and the signed-in principal (`PlannerOptions.SignedInPrincipalId`, else
  `GET /me?$select=id` once, when an ownership removal needs it).

### Algorithm (`EdgePlanner` → `EdgePlan` → `EdgeExecutor`)

1. **Collect**: for every applied (or, in what-if, planned) instance that declares an edge property or has
   `relationships` recorded in the state: re-evaluate `properties`, keep the relationship subset, normalise each
   entry through the kind's handler. Duplicates inside one array collapse; an unknown handler kind yields the
   `EdgeKindNotSupported` warning.
2. **Authoritative sets** (target side: `groups.members`, `owners`, `administrativeUnits.members`): desired set =
   the declared ids ∪ the ids of template principals whose principal-side property names this target (those
   declarations are *governed* by the set). Live set = `GET /{collection}/{t}/{members|owners}?$select=id`.
   Adds = desired − live; removes = live − desired (replace semantics). A declared empty array removes
   everything. A dynamic group / AU target is a validation error for the target row and every governed row.
3. **Principal side, not governed** (`memberOf`, `ownerOf`, `administrativeUnits` naming a target the template
   does not declare authoritatively, for example a pre-existing group via `reference(resourceId(...), apiVersion)`):
   existence is read from the principal (`GET /{users|servicePrincipals|groups}/{p}/memberOf`, filtered by
   `@odata.type`; `GET /{users|servicePrincipals}/{p}/ownedObjects`), the target is read once (existence, dynamic
   flag, `ownerOf` target collection from the template or `GET /directoryObjects/{id}?$select=id`), missing edges
   are added. Nothing outside the template is removed.
4. **Tracked removals**: every edge recorded in the principal's state entry that is no longer declared is removed,
   unless an authoritative target-side set of this template still desires it. Only edges that still exist are
   removed (the principal read decides); a removal that 404s counts as done.
5. **`manager`** (single-valued): a declared id replaces the live manager (`PUT /users/{u}/manager/$ref`), an
   explicit `null` removes it (`DELETE`), an absent property leaves it untouched (no tracked removal).
6. **De-duplication**: operations are keyed by `IdentityOf(edge)`; the same edge declared from both sides (or by
   a standalone resource, part B) is one write attributed to every declaring row. The same identity with
   non-equivalent extras is a validation error on every declaring row.
7. **Apply** (`EdgeExecutor`, create only): part A then part B, adds before removes, grouped by (Graph API, retry
   hint); `RetryHint.ExpectReplicationLag` when either side was created in this run; one request per edge up to
   20 per group, `$batch` beyond that. "Already exists" on an add and 404 on a remove are idempotent successes.
   Operations attributed only to rows with validation errors are skipped.
8. **Report**: the row's `properties.<property>` deltas (`PropertyChangeType.Array` with `[n]` `Create` /
   `Delete` children; `manager` as a scalar `Create` / `Modify` / `Delete`) are merged into the declaring
   resource's change (`EdgeChanges.Merge`: a `NoChange` object row with edge changes becomes `Modify`). In
   `create` a row whose edges fail validation or application turns its operation into `Failed`
   (`EdgeValidationFailed` / `EdgeApplyFailed`); in what-if the row carries `unsupportedReason`. Removing the
   signed-in principal from `owners` raises `SignedInPrincipalRemoved`. When an earlier batch failed (fail-fast)
   the pass does not run and `EdgePassSkipped` is reported.

Templates without relationship properties (and without recorded relationships in the state) never enter the pass:
no reads, no diagnostics.

### State format (`relationships`)

Only principal-side edges this deployment created are recorded, per property, after the pass succeeds
(`IStateSink.ResourceEdgesAppliedAsync`); target-side authoritative sets are a live diff every run and record
nothing. An edge that already existed before the template declared it is not recorded (it was not created by the
deployment) and is therefore never removed by undeclaring it.

```json
"relationships": {
  "memberOf": ["<groupId>", "<groupId>"],
  "ownerOf": [{ "id": "<appOrSpOrGroupId>", "collection": "applications" }],
  "administrativeUnits": ["<auId>"],
  "manager": "<userId>"
}
```

(`ownerOf` keeps the Graph collection so a later removal needs no type lookup; a bare id is still accepted and
resolved through `directoryObjects`.) Part B kinds record objects carrying the Graph assignment id. In Complete
mode the executor removes a deleted resource's recorded edges (`RecordedEdges.RemoveAsync`) before the object
itself; a 404 or failure there is logged and never blocks the delete.

### Adding a kind (WI-12 app role assignments, WI-15 directory roles)

Implement `IEdgeKindHandler` with `Phase = EdgePhase.B` in `EntraArm.Providers`, attribute it
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

## State (`EntraArm.Engine.State`)

Plan section 1.7. One `StateFile` per deployment name, keyed by ARM-style resource id, records which Graph objects
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

- **Keys are always concrete.** A resource whose name is symbolic (`[reference('app').appId]`) is recorded under
  its resolved id; copy iterations under their expanded ids. `FileStateSink` throws if asked to record a symbolic
  instance. `StateSerializer.ParseResourceId(id, type)` rebuilds the identifier from a key and the entry's type
  (no catalog needed, so unknown types still round-trip).
- **Store** (`IStateStore` / `FileStateStore`): `<dir>/<deploymentName>.json`, written to a temp file and moved into
  place; `LoadAsync` goes through `StateMigrator` (`schemaVersion` 1; a newer version → `StateSchemaUnsupportedException`).
  The directory is `--state-dir`, else `ENTRAARM_STATE_DIR`, else `.entraarm/state` next to the template
  (`StateDirectory.Resolve`). Deployment names are file names: `StateNames` allows `[A-Za-z0-9][A-Za-z0-9._-]*`.
  `tests/EntraArm.TestSupport/InMemoryStateStore` is the in-memory counterpart (same serializer, records every save).
- **Lock**: `<deploymentName>.lock` created with `FileMode.CreateNew` holding pid/host/user/UTC time; `LockAsync`
  waits up to the given time (`--lock-timeout`, 10 s) then throws `StateLockedException` (CLI exit 4); a lock older
  than one hour is taken over with a warning; `state unlock -n <name>` / `--force-unlock` break it. `what-if` never
  locks (it never writes).
- **Tenant pin**: the CLI calls `StateFile.EnsureTenant(preflight tenant)` after preflight; a state written for
  another tenant throws `TenantMismatchException` (exit 3). A state without a tenant adopts the current one.
- **Sink** (`FileStateSink : IStateSink`): `BeginAsync` marks the run `Running` (operations cleared); every
  `ResourceAppliedAsync` upserts the entry and **saves after every resource**; `ResourceFailedAsync` records the
  (redacted) error and keeps the previous entry; skipped/ignored resources and the mode pass are recorded too;
  `ResourceEdgesAppliedAsync` replaces the entry's `relationships` after the edge pass and `ResourceEdgesFailedAsync`
  records an edge failure (the entry keeps its previous records); `CompletedAsync` sets `lastStatus` /
  `lastDeployedOn` / `lastMode` / `lastTemplateHash`. A crash leaves `lastStatus: Running` and the entries written so far.
  **Recording is not cancellable**: every record after `BeginAsync` describes work that already happened, so the sink
  saves it under its own 30-second bound (`FileStateSink.PersistTimeout`) and the executor passes `CancellationToken.None`;
  a Ctrl+C right after a Graph write therefore still records that object (a generated secret or key id is never
  orphaned), while cancellation keeps stopping new work between resources and batches. The executor also passes the pinned Graph id to
  `ReadAsync` (`ExecuteOptions.State`); a 404 on the pinned id is drift: the provider falls back to the key, the
  resource is planned as `Create` if nothing is found, and the sink re-pins the entry.
- **Mode pass** (`ModePass`, executor and planner): orphans = entries whose id is not among the expanded instances
  (`condition: false` instances still count as declared; types with a still-symbolic instance are left alone with a
  warning). Incremental: `Ignore` row "not in template; will be detached from state" (what-if) / detached with a
  `ResourceDetached` warning (create). Complete: `Delete` rows with the live object as `before` (an object already
  gone is an `Ignore` row), applied in **reverse dependency order** from the entries' `dependsOn` with
  `ApplyPhases.DeleteOrderOf` as tie-breaker, each through the provider's `DeleteAsync` inside the retry policy
  (`StateDeletion`) after its recorded edges were removed; a 404 counts as deleted; the first failure stops the
  pass unless `--continue-on-error`. A provider implementing `IDeleteWarningSource` (groups: security groups are
  deleted permanently) adds a `DestructiveDelete` warning to the what-if row and the run.
- **`deployment delete --delete-resources`** (`DeploymentDeleter`): deletes every entry in the same order, saving
  after each, then removes the state file; without the flag only the file goes.

## Key Vault parameter references (`EntraArm.Engine.KeyVault`)

Plan section 1.6. A parameter file entry `{ "reference": { "keyVault": { "id": "…/vaults/contoso-kv" }, "secretName": "bg-initial" } }`
is bound as a pending reference by the loader. For `create` and `what-if` the CLI resolves it after preflight with
`KeyVaultSecretResolver` (`az keyvault secret show --vault-name <last id segment or bare name> --name <secret>
[--version <v>] --query value`, through the same `IAzRunner`, which always asks for JSON output) and binds the
value **secure-tainted** whatever the declared type. A failure (`KeyVaultResolutionException`) is reported as
`KeyVaultReferenceUnresolved` with the az message redacted (exit 3). `deployment validate` stays offline. A
pending reference that reaches `PlanAsync` is still an error for `create` and a warning plus deploy-time placeholder
for `what-if`. `tests/EntraArm.TestSupport/FakeAzRunner` scripts `keyvault secret show` for tests.

## Provider contract (`EntraArm.Engine.Providers`)

```csharp
public interface IResourceProvider
{
    string TypeName { get; }                                    // "Microsoft.Entra/applications"
    IReadOnlyDictionary<string, GraphApi> ApiVersions { get; }  // "2026-10-01-preview" -> V1, "2026-10-01-preview.beta" -> Beta
    IReadOnlyList<string> RequiredPermissions(ResourceInstance? desired);
    IReadOnlySet<JsonPath> ImmutableProperties { get; }
    IReadOnlyList<RelationshipSpec> Relationships { get; }      // edge properties (applied by WI-09)
    Task PrepareAsync(ResourceInstance desired, IGraphClient g, CancellationToken ct);  // default no-op: sugar + live validation before the key
    ResourceKey ResolveKey(ResourceInstance desired);           // from name / parent / composite properties
    Task<ExistingResource?> ReadAsync(ResourceKey key, string? stateId, IGraphClient g, CancellationToken ct);
    ResourceChange Plan(ResourceInstance desired, ExistingResource? existing, PlanOptions o);
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
  both). On a `NoChange` the executor republishes `ExistingResource.Raw` and carries the entry's recorded `extra`
  forward unchanged, so provider data written at create time (`keyId`, `endDateTime`, derived ids) survives reruns.
- `PlanOptions`: deployment-wide knobs for `Plan` (`Mode`, `PlanMode`, `AllowCaEnforce`, `OnSoftDeletedConflict`)
  plus `State`, the deployment's state before the run, for providers whose live object cannot carry everything the
  diff needs (password credentials read the recorded `rotationTag` and `endDateTime` from the entry's `extra`).
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
- `Plan`: the shared `JsonDiff` (plan 1.3): scalars replace; scalar arrays are order-insensitive sets;
  object arrays by `memberKey` (else canonical JSON, order-insensitive); nested objects compared whole with an
  explicit `null` (`Delete`) for every live-only sub-property unless `nullable: false` (`NoEffect`); GUIDs
  case-insensitive; `null` / `[]` ≡ absent; `createOnly` paths skipped on update; secrets never compared
  (`IsSecret`); unknowns flagged (`IsUnknown`). Only declared top-level properties take part (Graph PATCH is a
  merge). An immutable change (a delta on an `immutable` path; for an `immutable: ["*"]` type —
  `ResourceTypeDefinition.ImmutableAll`: password credentials, app role assignments, role assignments — **every**
  effective delta) is `Modify`/`Recreate` when `RecreateOnImmutableChange` is true (it defaults to
  `ImmutableAll`; the executor then deletes and creates), otherwise `UnsupportedReason`. `ImmutableProperties`
  stays the explicitly listed paths (empty for `["*"]`). A provider that overrides `Plan` keeps its own decision:
  password credentials rotate as `Modify`/`Patch` so `UpdateAsync` can add the new secret before removing the
  old one. A create missing `requiredOnCreate` properties (without overlay defaults) is unsupported as well.
- `BuildCreateBody`: object properties + the name injected into `nameMapsTo` (a different declared value is
  `NameMappedPropertyConflict`) + overlay `defaults` + **secrets revealed** (the only place besides
  `--reveal-secure-outputs`; bodies are never logged by the Az layer).
- `BuildPatchBody`: `JsonDiff.BuildPatchBody` (changed top-level properties in full, nested objects with
  explicit nulls, `null` / `[]` for cleared properties), secrets revealed.
- `StripServerManaged` / `ToExisting`: apply `stripOnRead`.
- `GraphPathFor(key)` / `SubstitutePath`: the overlay path with `{parentId}` / `{resourceId}` substituted.

Put `desired.RetryHint` on write requests. Throw `ProviderException` (with an `EngineDiagnosticCodes` or
provider code such as `AmbiguousMatch`) for non-Graph failures; let `GraphException` propagate.

## Test support (`tests/EntraArm.TestSupport`)

- `FakeGraphClient` / `FakeDirectory`: an in-memory tenant with alternate-key routing, `$filter … eq …`,
  `Prefer: create-if-missing`, pagination (`PageSize`), `$ref` navigations, actions (`addPassword`,
  `removePassword`, `assignLicense`, `updateAllowedCombinations`), `$batch`, a request log (`Requests`,
  `Writes`) and `FakeFailureScript` (replication lag, 429, 403, 5xx, "already exists", az failures). It does
  not retry; pass a `RetryPolicy` with a no-op `IDelayProvider` through `PlannerOptions.RetryPolicy`.
- `EchoProvider`: a generic catalog-driven provider (`EchoProvider.RegistryForCatalog(catalog)` registers one
  per type) so planner and executor tests run end to end without the real providers. It reads the pinned Graph id
  first (any name kind) and falls back to the key.
- `InMemoryStateStore`: an `IStateStore` over memory that round-trips every save through `StateSerializer` and
  keeps the saved documents (`Saves`) for "saved after every resource" assertions.
- `FakeAzRunner`: an `IAzRunner` with a secret table for `az keyvault secret show` and scripted responses for other
  commands; unknown commands fail like az.
