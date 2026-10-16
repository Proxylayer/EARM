# Resource providers (`EArm.Providers`)

How a `Microsoft.Entra/<type>` provider is written, registered and tested. The engine side of the contract
(`IResourceProvider`, `ProviderBase`, `JsonDiff`, `ResourceInstance`, `FakeGraphClient`) is described in
[engine.md](engine.md); this page is the checklist for adding a type without touching any shared file.

## Registration is by discovery

`ProviderDiscovery.CreateRegistry(catalog)` (called once per command by the CLI composition root,
`EngineServices`) scans the `EArm.Providers` assembly for every non-abstract class that implements
`IResourceProvider`, reads its `[EntraProvider("Microsoft.Entra/<type>")]` attribute, looks the type up in the
`ResourceTypeCatalog` (the `x-earm` overlay of `schemas/2026-10-01/types/Microsoft.Entra/<type>.json`) and
constructs the provider with the first constructor it finds among

```
(ResourceTypeDefinition definition, ResourceTypeCatalog catalog)   // preferred
(ResourceTypeDefinition definition)
(ResourceTypeCatalog catalog)
()
```

Nothing else is needed: no registration list, no edit to `EngineServices`. The following mistakes fail at startup
with a message naming the class:

- a concrete `IResourceProvider` without the attribute (a forgotten attribute would otherwise be a silent gap);
- an attribute naming a type the catalog does not define (add the type schema first);
- two classes claiming the same type;
- a class without one of the constructors above, or whose `TypeName` differs from the attribute.

A class may carry the attribute more than once when one implementation serves several catalog types.

## Writing a provider

```csharp
[EntraProvider(ResourceType)]
public sealed class GroupsProvider : ProviderBase
{
    public const string ResourceType = "Microsoft.Entra/groups";

    public GroupsProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog = null)
        : base(definition, catalog)
    {
    }

    public override Task<ExistingResource?> ReadAsync(ResourceKey key, IGraphClient graph, CancellationToken ct) { ... }
    public override Task<AppliedResource> CreateAsync(ResourceInstance desired, IGraphClient graph, CancellationToken ct) { ... }
    public override Task<AppliedResource> UpdateAsync(ResourceInstance desired, ExistingResource existing, ResourceChange change, IGraphClient graph, CancellationToken ct) { ... }
    public override Task DeleteAsync(ResourceKey key, ExistingResource live, IGraphClient graph, CancellationToken ct) { ... }
}
```

Derive from `ProviderBase` and implement the four Graph calls; override `Plan`, `ResolveKey` or
`RequiredPermissions` only for per-type behaviour. The base class gives you, from the overlay:

| Need | Use |
|---|---|
| The Graph key from the ARM-style `name` (`uniqueName`, `userPrincipalName`, `appId`, `displayName`, `parent/child`, composite) | `ResolveKey(desired)` (base), `KeyForIdentifier` |
| The collection and entity paths | `CollectionPath` (`/applications`), `GraphPathFor(key)` / `SubstitutePath` for `{parentId}` / `{resourceId}` placeholders, `GraphPath.AlternateKey(collection, "uniqueName", value)` (doubles `'`), `GraphPath.Segment(id)` |
| The live object for the diff and for `reference()` | `ToExisting(live)` (applies `stripOnRead`), `StripServerManaged` |
| The create body | `BuildCreateBody(desired)`: object properties + the name injected into `nameMapsTo` (a different declared value throws `NameMappedPropertyConflict`) + overlay `defaults` + **secrets revealed**. This and `BuildPatchBody` are the only places a `SecretValue` becomes plain text; never log or store the result. |
| The update body | `BuildPatchBody(desired, existing, change)`: the effective deltas as a merge-PATCH (arrays and nested objects whole, explicit `null` for live-only sub-properties unless `nullable: false`; a live-only **collection** is cleared with `[]` instead, since Graph refuses `null` for collections such as `web.redirectUris` / `spa.redirectUris` / `publicClient.redirectUris`) |
| The diff | `Plan` (base): `JsonDiff` with the overlay's `memberKey`, `nullable`, `createOnly`, `immutable` (error, or `Recreate` when `immutable: ["*"]`), `requiredOnCreate` |
| Existing-resource lookups (`reference(resourceId(...), apiVersion)`) | `ReadExistingAsync` (base) calls your `ReadAsync` with the key the id implies; nothing to do for name-keyed types |

Rules every provider follows:

1. **`ResourceInstance.RetryHint` goes on every write request** (`new GraphRequest(path, desired.Api, hint: desired.RetryHint)`):
   when a dependency was created in this run the executor sets `ExpectReplicationLag` and the Graph transport retries
   404 / "does not reference a valid …" / the Conditional Access API's "`NNNN: … with id … does not exist in the directory`" for up to 90 s. Reads use the default hint (a 404 on read means "create").
   A write that addresses an object *you* created a moment earlier (a second PATCH after a create) uses
   `RetryHint.ExpectReplicationLag` explicitly.
2. **Read by the key, and only by the key.** EARM keeps no state, so there is no stored Graph id to try first: `ReadAsync` gets the
   `ResourceKey` and finds the live object with it ([engine.md](engine.md#identity-without-state)). Return `null` when nothing exists; throw
   `ProviderException(ProviderException.AmbiguousMatch, …)` when a displayName lookup matches several objects (never pick one).
   `DeleteAsync` receives the key **and the live object just read by it** (`ExistingResource`), so it deletes by that object's Graph id.
3. **Edge properties never reach the body or the diff.** The overlay's `relationships` (`owners`, `members`,
   `memberOf`, `manager`, …) are split off by the engine into `ResourceInstance.EdgeProperties` before your
   provider sees `Properties`; the edge pass applies them. Declare them in the overlay, not in code, and do not
   read them back into the body. `RequiredPermissions` may consult `EdgeProperties` (the applications provider
   returns `Application.ReadWrite.OwnedBy` only when `owners` is declared).
4. **Select the managed property set on reads** (`.WithSelect(...)`): users return a tiny default set, and a
   property the template declares but the read does not return would show as drift on every run. The `$select` of a
   read is the managed set **plus the top-level properties the template declares for that resource** (`DeclaredSelects`,
   filled by the provider's `ResolveKey`; edge, create-only, secure and server-managed names are left out), so a
   passthrough property outside the managed set (`description` of an application, `employeeHireDate` of a user) is
   compared and not PATCHed on every run. The managed set of users, groups and applications also selects `createdDateTime` (the overlay's
   `stripOnRead` keeps it out of the diff, so it is never compared or PATCHed);
   the other types either return it by default (named locations, Conditional Access policies, authentication strengths) or do not have it
   (service principals, administrative units, role definitions, federated identity credentials, authentication contexts: Learn).
   A provider that reads "the" single key value uses `ProviderSupport.SingleKey(key, nameMapsTo)`, which refuses a key of another property.
   A multi-step create or restore that fails after its first write throws `ProviderException` with `Partial` set (groups: licenses after a
   create or restore; applications: the `{appId}` identifierUris PATCH and any step after a restore; service principals: after a restore), so the
   executor reports the object's Graph id (and writes its one-time secret to the secure outputs file) and fails the resource.
5. **Let `GraphException` propagate**; the executor classifies it (retry, "already exists" → re-read by key and take over,
   terminal). Throw `ProviderException` (with an `EngineDiagnosticCodes` or provider constant such as
   `SoftDeletedConflict`) for failures that are not Graph errors. Treat a 404 on delete as "already gone".
6. **Soft delete.** For types Graph keeps in `/directory/deletedItems` (applications, Microsoft 365 **and security** groups ([group-delete](https://learn.microsoft.com/en-us/graph/api/group-delete):
   only distribution groups are deleted immediately), users, service principals, administrative units) probe before a create whose key lookup was a 404; a hit is
   `ProviderException(SoftDeletedConflict)` unless `PlanOptions.OnSoftDeletedConflict == Restore`, in which case
   `POST /directory/deletedItems/{id}/restore` and continue with the upsert. `PlanOptions` only reaches `Plan`;
   capture what `CreateAsync` needs there (the executor plans each instance right before applying it). The probe needs
   the type's **read** permission (Learn: directory-deleteditems-list; `Application.Read.All` for applications, which an
   `Application.ReadWrite.OwnedBy`-only identity lacks): use `DeletedItemsProbe.FindAsync`, which treats a 403 (or a 400
   that its fallback listing repeats) as "probe unavailable" and returns a `SoftDeleteProbeSkipped`
   `ProviderWarning` for `AppliedResource.Warnings` instead of failing the create.
7. **Never `JsonNode.DeepClone` evaluated properties** (secret and unknown leaves are lost); use `JsonValues.Clone`.
8. **Put per-type pure logic in a separate static class** (`ApplicationsRules`) so it can be unit-tested without
   Graph, and keep provider state out of instance fields: one provider instance serves every resource of its type,
   in parallel. Deployment-wide snapshots (the `PlanOptions` of rule 6) are the only exception.
9. **Sugar and live-data validation go in `PrepareAsync`** (override of `ProviderBase.PrepareAsync`, called by the
   planner and the executor on the evaluated instance right before `ResolveKey`). It is the only asynchronous hook
   that sees `desired` before the key and the plan: rewrite `desired.Properties` in place (`resourceAppId` →
   `resourceId`, `appRole` → `appRoleId`, `scopes` → `scope`) so the composite key, the diff and the create body
   all see canonical ids, and throw `ProviderException` for anything Graph would accept but the template
   author got wrong (a role value the resource does not define, a scope it does not expose): that is an unsupported
   what-if row and a failed create. Leave deploy-time-unknown leaves alone (or move them under the canonical name).
   `PrepareAsync` must be idempotent; the CLI may evaluate an instance twice (what-if, then create).

### Worked example: `ApplicationsProvider`

Requests per operation (every read selects `ApplicationsProvider.ManagedProperties`):

| Operation | Requests |
|---|---|
| Read | `GET /applications(uniqueName='…')` (a 400 — Learn documents `uniqueName` as an alternate key of the upsert only — falls back to `GET /applications?$filter=uniqueName eq '…'&$count=true` with `ConsistencyLevel: eventual`) → `GET /applications(appId='…')` (only when the template declares `appId`, to adopt a brownfield app whose `uniqueName` is still unset). The `$select` is `ManagedProperties` plus the declared top-level properties |
| Create | `GET /directory/deletedItems/microsoft.graph.application?$filter=uniqueName eq '…'&$count=true` (`ConsistencyLevel: eventual`; falls back to listing the deleted applications if Graph rejects the filter; a 403 skips the probe with the `SoftDeleteProbeSkipped` warning) → `[POST /directory/deletedItems/{id}/restore]` → `PATCH /applications(uniqueName='…')` + `Prefer: create-if-missing` (**the body does not carry `uniqueName`**: it is the URL key and Learn documents it as read-only; 201: created; 204: existed → `GET /applications(uniqueName='…')` and adopt) → `[PATCH /applications/{id} { identifierUris }]` when any URI carries `{appId}` |
| Update | `[PATCH /applications/{id}` disabling removed/changed roles and scopes `]` → `PATCH /applications/{id}` (merge body) → `GET /applications/{id}` |
| Delete | `DELETE /applications/{id}` (soft delete; a 404 is tolerated) |

Type-specific rules (all in `ApplicationsRules`, pure):

- `appRoles` and `api.oauth2PermissionScopes` are matched by `id`. A member declared without `id` gets
  `v5(v5(ApplicationsRules.DerivedIdNamespace, uniqueName), value)` — stable across runs, so every run derives the same
  ids and nothing needs to be stored (the create result carries them as `AppliedResource.Extra.derivedIds`). `isEnabled` defaults to `true`.
- Removing a member, or changing its `value` / `allowedMemberTypes` (roles) / `type` (scopes), takes two PATCHes:
  the live array with those members `isEnabled: false`, then the final array.
- `value` must be unique across roles and scopes (a plan error otherwise).
- `identifierUris` entries containing `{appId}` (`api://{appId}`) are written by a second PATCH after the create;
  on later runs the placeholder is substituted with the live appId before the diff.
- Live `api.requestedAccessTokenVersion: null` compares as `1` (an explicit `null` clears it, so it is nullable); live
  `web.implicitGrantSettings` always has both booleans; Graph refuses `null` for `web.implicitGrantSettings` and
  `web.redirectUriSettings` (`nullable: false`: never sent as `null`) and for the `redirectUris` collections (a live-only
  collection is cleared with `[]`); the server-managed `origin` of roles/scopes is ignored; `keyCredentials` takes part in the diff only
  when declared (passthrough).
- `name` vs a declared `properties.uniqueName` that differs is an error; a declared `appId` is only a lookup key
  (never written) and is an error when no application exists for it.

### Worked example: `PasswordCredentialsProvider` (one-time secrets)

`Microsoft.Entra/applications/passwordCredentials`: the name is `<application uniqueName>/<credential name>`. The live
`displayName` is `<name>#<rotationTag>` when the template declares a `rotationTag`, else the plain `<name>`
(`PasswordCredentialName`; the tag is the text after the **last** `#`). The credential is matched among the application's
`passwordCredentials` by base name (exactly `<name>`, or `<name>#<any tag>`); among several, the newest whose live tag equals
the declared one, else the newest. The declared tag reaches the read as a `ResourceKey.Hints` entry (it steers the choice, it is
not identity and never persisted). Graph never edits a credential and returns `secretText` exactly once, from
`addPassword`, so every change is a replacement and the secret is handled as below. Action URLs always address
the application by object id (the parent applied or read in this run, or an id lookup); only reads use
`/applications(uniqueName='…')`.

| Operation | Requests |
|---|---|
| Read | `GET /applications/{parentId}?$select=id,passwordCredentials` (or `GET /applications(uniqueName='…')?$select=…`); a 404 is "does not exist" |
| Create | `[GET /applications(uniqueName='…')?$select=id]` when the parent's id is not known → `POST /applications/{id}/addPassword { "passwordCredential": { "displayName": "<name>[#<rotationTag>]", "endDateTime"[, "startDateTime"] } }` |
| Rotate (`Modify`, mode `Patch`) | the same `addPassword` **first** (named with the new tag), then `POST /applications/{id}/removePassword { "keyId" }` for every live same-base-name credential except the new one (a consumer never sees a moment without a valid secret; the executor's `Recreate` path would delete first, so the row is `Patch` and `UpdateAsync` does the replacement) |
| Delete | `GET /applications/{parentId}?$select=id,passwordCredentials` (404 = the application and its credentials are gone) → `removePassword` for the live credential's `keyId` and every same-base-name leftover; an unknown `keyId` (404, or Graph's 400 "No password credential found with keyId …") counts as removed |

Type-specific rules (all in `PasswordCredentialsRules`, pure):

- `Plan`: `Create` when no same-name credential exists; otherwise `NoChange` unless the declared `rotationTag` differs
  from the tag in the live `displayName` (absent ≡ absent; no local state is consulted), a declared `endDateTime` / `startDateTime` differs from the live value (instants
  within one second are equal, so an offset in the template does not drift), the credential expired, or it expires
  within `rotateBeforeDays` (template property, default 30). The what-if row carries `properties.rotationTag` /
  `properties.endDateTime` / `properties.startDateTime` deltas and a reason that names the trigger; both sides of the
  row show the live `displayName` (`<name>#<tag>`) and `rotationTag` (the live side's parsed from the name), so a tag
  change reads `rotationTag: '2026Q3' → '2026Q4'`.
- **Upgrade from a state-kept tag**: a live credential named plain `<name>` (created by an earlier EARM preview that kept the
  tag in a local state file) carries no tag. When the template declares one, it is treated as **matching** (an upgrade
  never rotates a secret by surprise) and the row carries the `RotationTagNotStamped` warning; the next rotation (expiry,
  a changed `endDateTime` / `startDateTime`) names the replacement `<name>#<tag>`. Until then a changed `rotationTag` is
  not detected; declare a different `endDateTime` to force the rotation. A template that drops its `rotationTag` while
  the live name carries one rotates to the plain name.
- `endDateTime` defaults to "now + 180 days" **once**, at create; reruns compare against the live expiry, never against
  a fresh default.
- The published live object is `{ id, keyId, displayName, startDateTime, endDateTime, hint, secretText,
  secretTextAvailable }`: in the creating / rotating run `secretText` is a `SecretJson` leaf (so
  `reference('…').secretText` is a secure-tainted value that flows to a `securestring` output and nowhere else);
  on reruns it is `null` with `secretTextAvailable: false`, and the executor reports an output that reads it as
  `null` with the `SecretNotAvailableOnRerun` warning instead of failing the run (`LiveSecrets`, engine.md step 8).
- The secret is never persisted: only the secure outputs file receives it. The Graph id is the `keyId`.
- Permissions follow the applications provider: `Application.ReadWrite.OwnedBy` when the parent application is
  declared in the template, otherwise `Application.ReadWrite.All`.
- A declared `properties.displayName` that differs from the name, a date that does not parse, a non-scalar
  `rotationTag` or a negative `rotateBeforeDays` make the row unsupported; a name without the parent segment is
  an offline validation error. A `#` in the name or in the `rotationTag`, or a live `displayName` longer than 256
  characters (a conservative cap: Graph documents none), is `InvalidResourceName`: offline for literals (the semantic
  checker), an unsupported row for values computed by expressions. An empty `rotationTag` counts as none.

### Worked example: GUID-named types (`AppRoleAssignmentsProvider`, `OAuth2PermissionGrantsProvider`)

The name is a deterministic GUID the author picks with `guid(...)` (the core validator rejects anything else). It is a
**label**: the Graph object is found by the overlay's composite `keyProperties` on every run, so a composite change under the
same name finds nothing and is a **Create**; the old assignment is not looked at by the resource and stays (the orphan report lists
it as `orphan: retained`, see [engine.md](engine.md#retained-assignments)). The overlay says `immutable: ["*"]`, which the catalog
folds into `ImmutableAll` with an empty per-property list; `ProviderBase.Plan` treats every effective difference of an existing
object of such a type (one found by its full key, so only non-key properties can differ) as an immutable change and, since
`RecreateOnImmutableChange` defaults to `ImmutableAll`, plans `Recreate` (the executor deletes, then creates, after confirmation).
A provider only needs its own `Plan` for type-specific validation or reasons. For the orphan report a GUID-named provider
implements `IOrphanCandidateSource` with the `DeclaredPrincipals` scope (the assignments of the principals the template declares).

`Microsoft.Entra/appRoleAssignments` — key (`principalId`, `resourceId`, `appRoleId`); sugar `resourceAppId` (the
resource application's id) and `appRole` (the role's `value`); `00000000-0000-0000-0000-000000000000` is default access.

| Operation | Requests |
|---|---|
| Prepare | `GET /servicePrincipals(appId='…')` (sugar) or `GET /servicePrincipals/{resourceId}`, `$select=id,appId,displayName,appRoles,oauth2PermissionScopes`, under the instance hint → `[GET /directoryObjects/{principalId}?$select=id]` when the role restricts `allowedMemberTypes`. Unknown role value or id, disabled role, or a role the principal's kind may not hold (users and groups are `User`, service principals `Application`) ⇒ `ProviderException(AppRoleAssignmentInvalid)` |
| Read | from the principal side (bounded: the resource side of a principal such as Microsoft Graph's is tenant-sized): `GET /directoryObjects/{principalId}?$select=id` for its collection → `GET /{users\|groups\|servicePrincipals}/{principalId}/appRoleAssignments?$top=999` (every page) matched on `resourceId` + `appRoleId`; a 404 is "does not exist" |
| Create | `POST /servicePrincipals/{resourceId}/appRoleAssignedTo { principalId, resourceId, appRoleId }` under the instance hint; 400 "Permission being assigned already exists" ⇒ re-read by composite (`ExpectReplicationLag`) and adopt. `AppliedResource.Extra` = the resolved ids |
| Update | never (`Recreate` instead) |
| Delete | `DELETE /servicePrincipals/{resourceId}/appRoleAssignedTo/{id}` (404 tolerated); the `resourceId` comes from the key, the id from the live object |

`Microsoft.Entra/oauth2PermissionGrants` — key (`clientId`, `resourceId`, `consentType`, `principalId`; the last one
only for `Principal`, written as explicit `null` for `AllPrincipals`); sugar `scopes: [...]` → `scope`; `scope` is a
set of values (order-insensitive, duplicates collapsed; the PATCH keeps the template's order).

| Operation | Requests |
|---|---|
| Prepare | `GET /servicePrincipals/{resourceId}` (summary select) under the instance hint; a value absent from the resource's `oauth2PermissionScopes`, or disabled there, ⇒ `ProviderException(PermissionGrantInvalid)` (Graph itself accepts any text) |
| Read | `GET /oauth2PermissionGrants?$filter=clientId eq '…' and resourceId eq '…' and consentType eq '…'[ and principalId eq '…']` |
| Create | `POST /oauth2PermissionGrants { clientId, resourceId, consentType, principalId, scope }`; 400 "Permission entry already exists" ⇒ lookup by key, `PATCH { scope }` when the set differs, re-read and adopt |
| Update | `PATCH /oauth2PermissionGrants/{id} { scope }` → `GET /oauth2PermissionGrants/{id}` |
| Delete | `DELETE /oauth2PermissionGrants/{id}` (404 tolerated) |

Pure rules live in `AppRoleAssignmentsRules` (declared shape, `allowedMemberTypes`, the `{resourceId}/{appRoleId}`
target key, the edge identity) and `OAuth2PermissionGrantsRules` (consent types, scope tokens and set equality,
unknown / disabled scope values); the Graph reads shared with the edge kind are in `AppRoleAssignmentsLookup`.

### Worked example: `ServicePrincipalsProvider` (a name that depends on another resource)

`Microsoft.Entra/servicePrincipals`: the name is the application (client) id, a literal GUID or an expression such as
`[reference('contoso-dev-web').appId]` that the engine resolves once the application exists (a *symbolic id*, see
[engine.md](engine.md#symbolic-names)). Every read selects `ServicePrincipalsProvider.ManagedProperties`.

| Operation | Requests |
|---|---|
| Read | `GET /servicePrincipals(appId='…')`; a 404 is "does not exist" |
| Create | soft-delete probe `GET /directory/deletedItems/microsoft.graph.servicePrincipal?$filter=appId eq '…'` (advanced query; a 403 skips it with a warning) → a hit is `ProviderException(SoftDeletedConflict)`, or with `--on-soft-deleted-conflict restore` `POST /directory/deletedItems/{id}/restore` and the restored principal is adopted (declared properties PATCHed on) instead of `POST`; otherwise `POST /servicePrincipals { appId, accountEnabled, appRoleAssignmentRequired, …declared properties }` under the instance's retry hint (`ExpectReplicationLag` when the application was created in this run: the 400 "does not reference a valid application object" and the 404 are retried for up to 90 s). A 400 "same value for property appId already exists" means the principal exists (auto-provisioned, or not yet visible to the read): it is re-read by `appId` under the lag hint and adopted, with one `PATCH /servicePrincipals/{id}` when the declared properties differ, then re-read |
| Update | `PATCH /servicePrincipals/{id}` (merge body: changed scalars; `tags`, `notificationEmailAddresses`, `replyUrls`, `alternativeNames` and `samlSingleSignOnSettings` whole) → `GET /servicePrincipals/{id}` |
| Delete | `DELETE /servicePrincipals/{id}` (soft delete; cascades the principal's assignments and grants in both directions); a 404 is "already gone" |

Type-specific rules (`ServicePrincipalsRules`, pure):

- The overlay defaults `accountEnabled: true` and `appRoleAssignmentRequired: false` apply only to a principal EARM
  creates (including the restore of a soft-deleted one that replaces the create). An existing principal never receives
  them: one found by the read is diffed on its declared properties only, and one adopted after a 400 "already exists"
  is PATCHed with the declared properties without the defaults `Plan` filled in for the create. A principal disabled or
  switched to assignment-required outside EARM therefore stays so unless the template declares `accountEnabled` /
  `appRoleAssignmentRequired`.
- `appRoles` and `oauth2PermissionScopes` of the principal mirror the application's and lag an application PATCH by
  seconds. They are selected (for `reference()`) but stripped from the diff; resolving role or scope ids goes through
  `ServicePrincipalLookup.FindByAppIdAsync`, which always reads fresh.
- `owners`, `memberOf`, `ownerOf`, `appRoleAssignments` and `directoryRoles` are edge properties (applied by the edge
  pass, never in the body).
- Permissions: `Application.ReadWrite.OwnedBy` when the principal's application is a resource of the template (the
  name references it, or `dependsOn` names it), otherwise `Application.ReadWrite.All` (first-party application ids
  always need `.All`). Reference first-party principals with `reference(resourceId(...), apiVersion)`; never manage them.

### Worked example: `FederatedIdentityCredentialsProvider` (an alternate key under a parent)

`Microsoft.Entra/applications/federatedIdentityCredentials`: the name is `<application uniqueName>/<credential name>`
(nested children are flattened to that form by the loader). Every path starts with the parent segment:
`/applications/{parentId}`. When the parent's Graph id is not known (an application outside the template, found by `uniqueName`), the provider
resolves it first (`GET /applications(uniqueName='…')?$select=id`, a `$filter=uniqueName eq '…'` listing when a tenant rejects the key form; once per run): Learn documents
the credential endpoints under `/applications/{id}` and `/applications(appId='…')` ([federatedidentitycredential-upsert](https://learn.microsoft.com/en-us/graph/api/federatedidentitycredential-upsert)),
and the `applications(uniqueName='…')/federatedIdentityCredentials` form appears only in one of its examples. A parent that does not exist makes a create fail with a clear message, a
read find nothing and a delete a no-op; the fake directory answers 400 to any navigation below a `(uniqueName=…)` key.

| Operation | Requests |
|---|---|
| Read | `GET {parent}/federatedIdentityCredentials(name='…')` → `GET {parent}/federatedIdentityCredentials` (the siblings, for the uniqueness and limit checks); a 404 is "does not exist" |
| Create / Update | the upsert `PATCH {parent}/federatedIdentityCredentials(name='…')` + `Prefer: create-if-missing` (201: created, the body is the object; 204: merged into the existing one, re-read); when the parent's id was not known, the parent lookup runs first |
| Delete | `DELETE {parent}/federatedIdentityCredentials(name='…')`; a 404 is "already gone" |

`Plan` refuses (an unsupported what-if row, a failed create) a name outside the overlay pattern
(`^[A-Za-z0-9][A-Za-z0-9_-]{2,119}$`: 3-120 letters, digits, `-` or `_`, starting with a letter or digit), a non-https `issuer`, an empty `subject`, `claimsMatchingExpression` on `2026-10-01-preview`
(Graph v1.0; it needs `2026-10-01-preview.beta`), an `issuer` + `subject` pair already used by another credential of the application (live, or declared
elsewhere in the same deployment) and a 21st credential. Written literally, a duplicate pair (two declarations, or a `copy` loop with a literal
subject) and a duplicate `audiences` value are already refused offline by `deployment validate` (`FederatedCredentialChecker`: `DuplicateFederatedCredentialSubject`,
`DuplicateFederatedCredentialAudience`). A rename is a new resource: the old credential stays (Complete mode
lists it as undeclared; nothing deletes it), so a rename that keeps issuer and subject is refused until you delete the old
credential. Permissions follow the parent rule of the password credentials above.

The writes (upsert, delete) to the credentials of **one** application go one at a time (a gate per parent path in the provider), even when the
executor applies them in parallel (`--parallelism`, a `copy` loop in `parallel` mode): Graph does not accept concurrent writes to one application's
credential collection. A real-tenant run that upserted `github-0` and `github-1` of a copy loop at the same moment got
`400 Request_BadRequest: Request contains a property with duplicate values` for the second, although the two subjects differed; Learn's
workload identity federation considerations likewise say to create the credentials of one parent sequentially. Credentials of different
applications are still written in parallel.

### Worked example: `UsersProvider` (create-only secrets, action-backed licenses)

`Microsoft.Entra/users`: the name is the `userPrincipalName` (a guest's `#EXT#` is percent-encoded on the path). Every read
selects `UsersProvider.ManagedProperties` (a user read returns a tiny default set otherwise).

| Operation | Requests |
|---|---|
| Read | `GET /users/{upn}`; a 404 is "does not exist". When the user holds licenses `GET /subscribedSkus` runs once per run to resolve `skuPartNumber` |
| Create | soft-delete probe `GET /directory/deletedItems/microsoft.graph.user?$filter=userPrincipalName eq '…'` (advanced query; a 400 falls back to listing) → on a hit `ProviderException(SoftDeletedConflict)`, or with `--on-soft-deleted-conflict restore` `POST /directory/deletedItems/{id}/restore` + `PATCH /users/{id}` + `GET /users/{id}`; otherwise `POST /users`; then `POST /users/{id}/assignLicense` when `assignedLicenses` is declared |
| Update | `PATCH /users/{id}` (merge body; never `passwordProfile`, `userPrincipalName` or `id`) → `GET /users/{id}` → `POST /users/{id}/assignLicense` for the add/remove delta of the **direct** assignments |
| Delete | `DELETE /users/{id}` (soft delete, 30-day recycle bin: `GET /directory/deletedItems/microsoft.graph.user` lists it, `…/restore` brings it back; Microsoft Learn does not say whether the UPN stays reserved, so EARM never assumes it either way — it probes before a create and reports a hit as a conflict); a 404 is "already gone" |

Type-specific rules (`UsersRules`, `UsersLicensing`, pure):

- Defaults apply only when EARM creates the user (including the restore of a soft-deleted holder of the UPN that
  replaces the create): `accountEnabled: true`, `mailNickname` derived from the UPN local part,
  `passwordProfile.forceChangePasswordNextSignIn: true`. An existing user found by UPN never receives a default: an
  undeclared property is neither diffed nor PATCHed, so a declaration with only edge properties (`memberOf`,
  `administrativeUnits`, …) leaves a disabled account disabled. Declare `accountEnabled` to manage it on existing users.
  (The same create-only rule holds for every type's defaults; see [format.md](format.md#resource-type-catalog).)
- `passwordProfile` is create-only and write-only: never diffed (a declared one on an existing user is ignored), never in
  a PATCH, and its `password` is tainted at plan time, so what-if, logs, diagnostics and the result only see `***`;
  the plain text exists in the `POST /users` body alone. Without a declared password a random 24-character one is
  generated at create time. Either way it is published on the live object as a secret, so
  `[reference('alice@contoso.com').passwordProfile.password]` can feed a `securestring` output in the creating run
  (convention: `<localPart>InitialPassword`); later runs cannot read it: on every run that does not create the user the
  live object carries `passwordProfile.password: null` with `initialPasswordAvailable: false` (`LiveSecrets`), so that
  output is `null` with the `SecretNotAvailableOnRerun` warning and the run still succeeds. A generated password is also
  written to the secure outputs file (`oneTimeSecrets`) right after the create; nothing else keeps it.
- `userPrincipalName` is immutable: a live user whose UPN differs from the name is an unsupported change.
- `assignedLicenses` reconciles the declared list against the **direct** assignments only
  (`licenseAssignmentStates[].assignedByGroup == null`), so group-inherited licenses are never removed from the user side;
  a non-empty declaration needs `usageLocation` (declared or live; the offline validator reports
  `AssignedLicensesRequireUsageLocation`).
- `manager`, `memberOf`, `ownerOf`, `administrativeUnits`, `appRoleAssignments` and `directoryRoles` are edge properties
  (the engine keeps them out of `Properties`). Permissions: `User.ReadWrite.All` plus the overlay's
  `permissions.conditional` entry of every edge or action property the template declares; `ownerOf` also needs the
  target type's permission, which that provider reports.

### Worked example: Conditional Access (`NamedLocationsProvider`, `ConditionalAccessPoliciesProvider`)

Policy reads and writes carry `Prefer: include-unknown-enum-members`: [conditionalaccessgrantcontrols](https://learn.microsoft.com/en-us/graph/api/resources/conditionalaccessgrantcontrols)
lists `riskRemediation` as a member of the evolvable `builtInControls` enum that Graph returns only to such a request (otherwise it reads back as `unknownFutureValue`). The schema enum
knows `riskRemediation`, and `passwordChange` together with `riskRemediation` is an unsupported row (Learn: "must be used separately").

Both are named by `displayName` and found by it on every run (there is no stored id). A lookup that finds no object
is a create, one hit is the object, several hits are `ProviderException(AmbiguousMatch)` listing the ids (rename or delete
the extra objects: the display name must be unique). A policy renamed in the portal is no longer found; the next run creates
a second one.

`Microsoft.Entra/conditionalAccess/namedLocations`:

| Operation | Requests |
|---|---|
| Read | `GET /identity/conditionalAccess/namedLocations?$filter=displayName eq '…'` |
| Create | `POST /identity/conditionalAccess/namedLocations` (the response body is the object) |
| Update | `PATCH …/{id}`, the body **always** carrying `@odata.type` (Graph requires it on this collection) → `GET …/{id}` |
| Delete | a trusted IP location (live `isTrusted: true`) is first unmarked with `PATCH …/{id}` `{"@odata.type": "#microsoft.graph.ipNamedLocation", "isTrusted": false}` (Graph refuses to delete a trusted location: 400, error `1177`), then `DELETE …/{id}`; a 1177 on the DELETE unmarks and retries once, and a second 1177 is surfaced as `NamedLocationTrusted`; any other 400 (a policy still references the location) is surfaced as `NamedLocationReferenced`. Graph applies the unmark with a delay, so the engine repeats a delete refused as `NamedLocationTrusted` every 5 s for up to 90 s (`LaggingDeleteRetry`, `deployment delete` and the old object of a recreate), and one refused as `NamedLocationReferenced` likewise when a policy was deleted earlier in the same run |

`@odata.type` selects the kind (`#microsoft.graph.ipNamedLocation`: `ipRanges`, `isTrusted`;
`#microsoft.graph.countryNamedLocation`: `countriesAndRegions`, `includeUnknownCountriesAndRegions`,
`countryLookupMethod`) and is immutable: a kind change is `Modify`/`Recreate` (delete, then create; Graph refuses the
delete while a policy references the location). `NamedLocationsRules.NormalizeDesired` runs inside `Plan` (so what-if,
bodies and the result describe the same object): the IPv4/IPv6 range `@odata.type` is inferred when omitted, a CIDR with
host bits set is rejected, ranges and country codes are canonicalised, and the live object is normalised the same way,
so reordered or differently spelt ranges are not drift. The per-kind defaults (`isTrusted: false`;
`includeUnknownCountriesAndRegions: false`, `countryLookupMethod: clientIpAddress`) apply only to a location EARM creates,
including the replacement of a kind change: an existing location's undeclared properties are never written, so a
location marked trusted outside EARM stays trusted unless the template declares `isTrusted`.

`Microsoft.Entra/conditionalAccess/policies`:

| Operation | Requests |
|---|---|
| Read | `GET /identity/conditionalAccess/policies?$filter=displayName eq '…'` |
| Create | `POST /identity/conditionalAccess/policies` |
| Update | `PATCH …/{id}` carrying every changed top-level property **whole** (`conditions`, `grantControls`, `sessionControls` are replaced, not merged, by Graph; a removed control is an explicit `null`) → `GET …/{id}` |
| Delete | `DELETE …/{id}`; a 404 is "already gone" |

`ConditionalAccessPoliciesRules.NormalizeDesired` fills the defaults and reduces `authenticationStrength` to `{ id }`.
`state: enabledForReportingButNotEnforced` is a create default: an existing policy whose template does not declare
`state` keeps its live state (an enabled or disabled policy is not switched to report-only). `conditions.clientAppTypes:
["all"]` and `grantControls.operator: OR` are filled on every run, but only inside a declared `conditions` /
`grantControls`: Graph replaces those objects whole, so leaving the default out of the PATCH would clear the value.
`NormalizeLive` removes read-back noise (`customAuthenticationFactors: []`, `termsOfUse: []`, `platforms: null`). `Plan` refuses
what Graph would reject or the plan guards against: `state: enabled` without `--allow-ca-enforce`, an empty users or
applications include, no control at all, `block` combined with another control, `mfa` together with
`authenticationStrength`, a `conditions.applications` that combines a non-empty `includeApplications` with a non-empty
`includeUserActions` or `includeAuthenticationContextClassReferences` (Graph refuses it with error `1012`; written literally it is
already refused offline by `deployment validate` as `ConflictingApplicationIncludes`, `ConditionalAccessPolicyChecker`, and arrays
computed by expressions are caught here at plan time), the lockout pattern (all users + all applications + block with no exclusion) and the
preview-only properties (`templateId`, `partialEnablementStrategy`, `sessionControls.continuousAccessEvaluation`) on
`2026-10-01-preview` (Graph v1.0; use `2026-10-01-preview.beta` for them). References are by id: users and groups by object id, `includeApplications` by application (client) id,
locations by named location id (`reference('Contoso offices').id` makes the dependency), roles by role template id. The
last `PlanOptions` seen is kept so a direct `CreateAsync`/`UpdateAsync` applies the same guard.

### Worked example: authentication context and authentication strength

`Microsoft.Entra/conditionalAccess/authenticationContextClassReferences`: the name **is** the Graph id (`c1`..`c99`, pattern
`^c[1-9][0-9]?$`); there is no displayName lookup: the name is the Graph id. Graph upserts on this collection. The two Learn pages disagree on the range:
[authenticationcontextclassreference](https://learn.microsoft.com/en-us/graph/api/resources/authenticationcontextclassreference) says `c1` to `c25`, while the Conditional Access documentation
([cloud apps, actions and authentication context](https://learn.microsoft.com/en-us/entra/identity/conditional-access/concept-conditional-access-cloud-apps): "up to 99 authentication context definitions (c1-c99)") allows `c1` to `c99`; EARM accepts
`c1`..`c99` and leaves the limit to Graph, which answers a 400 for an id the tenant does not allow.

| Operation | Requests |
|---|---|
| Read | `GET /identity/conditionalAccess/authenticationContextClassReferences/{id}`; a 404 is "create" |
| Create | `PATCH …/{id} { displayName, description, isAvailable }` (the response object when Graph returns one, otherwise `GET …/{id}`) |
| Update | `PATCH …/{id}` (merge body of the changed scalars) → `GET …/{id}` |
| Delete | `GET …/{id}` (404 = already gone) → `PATCH …/{id} { isAvailable: false }` while the context is still available (Graph refuses to delete an available one) → `DELETE …/{id}`; a 400 (still available, or referenced by a policy's `includeAuthenticationContextClassReferences`) is surfaced as `Referenced` (repeated every 5 s for up to 90 s when a policy was deleted earlier in the same run, like the strength) |

`Plan` requires `displayName` and, for a context EARM creates, defaults `isAvailable: true` into the evaluated
properties (an explicit `isAvailable: null` also means `true`). An existing context's undeclared `isAvailable` is never
written: one switched off outside EARM shows as drift only when the template declares `isAvailable: true`. The id is immutable by definition: another name is another resource, never a recreate.
Policies reference a context by id (`"includeAuthenticationContextClassReferences": ["c10"]`, or
`[reference(resourceId('Microsoft.Entra/conditionalAccess/authenticationContextClassReferences', 'c10')).id]` to get the
ordering dependency).

`Microsoft.Entra/conditionalAccess/authenticationStrengths`: the name is the `displayName`; only custom strengths
(`policyType: custom`) are managed. The built-in ones (Multifactor authentication, Passwordless MFA, Phishing-resistant
MFA) are read-only: reference them with
`reference(resourceId('Microsoft.Entra/conditionalAccess/authenticationStrengths', '<display name>'), '2026-10-01-preview').id`
(the existing-resource lookup matches built-ins too) or the well-known ids
(`00000000-0000-0000-0000-000000000002` MFA, `…0003` passwordless, `…0004` phishing-resistant).

| Operation | Requests |
|---|---|
| Read | `GET /policies/authenticationStrengthPolicies` (every page; the displayName match is client-side because `$filter` on this collection is unverified): one custom hit is the object, several are `AmbiguousMatch`, only a built-in hit is the built-in (which `Plan` refuses to manage), nothing is "create" |
| Create | `POST /policies/authenticationStrengthPolicies { displayName, description, allowedCombinations }`. Graph refuses a displayName over 30 characters ("Policy DisplayName length cannot be longer than 30 characters"), so the name is limited to 30 characters offline (`nameMaxLength`; `deployment validate` reports `InvalidResourceName`) |
| Update | `PATCH …/{id}` only when `displayName` / `description` changed (never carrying `allowedCombinations`) → `POST …/{id}/updateAllowedCombinations { allowedCombinations }` only when the combination set differs → `GET …/{id}` |
| Delete | `DELETE …/{id}`; a 400 while a policy references the strength is surfaced as `Referenced` (`deployment delete` deletes policies first: reverse dependency order; a `Referenced` refusal right after a policy was deleted in the same run is repeated every 5 s for up to 90 s, because the Conditional Access service sees the policy delete late) |

`allowedCombinations` is compared as a set of normalised strings (`AuthenticationStrengthsRules.NormalizeCombination`): when
the sets are equal the desired array adopts the live spelling before the shared diff runs, so reordered or differently
cased combinations are not drift; the values Graph receives are the template's own (trimmed). `policyType` is immutable
and stripped on read; a declared `custom` is dropped as implied, anything else is an unsupported row.

### Worked example: authentication method configurations

`Microsoft.Entra/authenticationMethodConfigurations`: the per-method configurations of the tenant authentication methods policy. Each one is a **tenant
singleton** that always exists, so the type is never created and never listed as an orphan. The name is the method id, compared case-insensitively
(Graph returns `Fido2`), and the provider derives the `@odata.type` Graph requires on every PATCH from it
(`AuthenticationMethodConfigurationRules.Methods`, shared with the offline validator):

| Name | Graph id (URL segment) | `@odata.type` | Delete |
|---|---|---|---|
| `fido2` | `Fido2` | `#microsoft.graph.fido2AuthenticationMethodConfiguration` | reset to the Microsoft default |
| `temporaryAccessPass` | `TemporaryAccessPass` | `#microsoft.graph.temporaryAccessPassAuthenticationMethodConfiguration` | reset to the Microsoft default |
| `x509Certificate` | `X509Certificate` | `#microsoft.graph.x509CertificateAuthenticationMethodConfiguration` | reset to the Microsoft default |
| `microsoftAuthenticator` | `MicrosoftAuthenticator` | `#microsoft.graph.microsoftAuthenticatorAuthenticationMethodConfiguration` | kept (`NotDeletable`) |
| `sms` | `Sms` | `#microsoft.graph.smsAuthenticationMethodConfiguration` | kept (`NotDeletable`) |
| `voice` | `Voice` | `#microsoft.graph.voiceAuthenticationMethodConfiguration` | kept (`NotDeletable`) |
| `email` | `Email` | `#microsoft.graph.emailAuthenticationMethodConfiguration` | kept (`NotDeletable`) |
| `softwareOath` | `SoftwareOath` | `#microsoft.graph.softwareOathAuthenticationMethodConfiguration` | kept (`NotDeletable`) |

The eight are the derived types Learn lists for `authenticationMethodConfiguration` in Graph v1.0; other methods (hardware OATH, QR code) are beta-only
and not supported yet.

| Operation | Requests |
|---|---|
| Read | `GET /policies/authenticationMethodsPolicy/authenticationMethodConfigurations/{GraphId}`; a 404 means the tenant does not offer the method |
| Create | never: a missing configuration is an unsupported plan row ("does not exist in the tenant ... EARM never creates") and a failed operation |
| Update | `PATCH …/{GraphId} { "@odata.type": …, <changed top-level properties> }` (a merge PATCH) → `GET …/{GraphId}` |
| Delete | fido2, temporaryAccessPass, x509Certificate: `DELETE …/{GraphId}`, which Graph documents as "revert to the default configuration" (a 404 is nothing to reset); the delete-risk text (`IDeleteWarningSource`) says so in the `deployment delete` listing. Every other method: no request; the object is **kept** (`IDeleteRefusalSource`, `NotDeletable`) |

Comparison and bodies:

- Only declared top-level properties are compared (Graph PATCH merges them). **Below** them Graph replaces a complex property whole, so before the diff
  every undeclared sub-property of a declared object, and of a declared `includeTargets` / `excludeTargets` / `passkeyProfiles` member (matched by
  `id`), is copied from the live object (`AuthenticationMethodConfigurationsShape.FillUndeclared`) and resent. The costly case is certificate-based
  authentication: a declared `authenticationModeConfiguration` without `rules` keeps (and resends) the live binding rules; an explicit `"rules": []`
  clears them. A declared value always wins, so `null` / `[]` clear.
- `includeTargets`, `excludeTargets` and `passkeyProfiles` compare as sets keyed by `id` (overlay `memberKey`); FIDO2 `aaGuids` compare as a set and are
  normalised to Graph's lower-case form, as are `state` and `enforcementType`. A declared `id` / `@odata.type` that names the same method is accepted and
  dropped (never diffed); one that names another method is an error.
- FIDO2 `isAttestationEnforced` and `keyRestrictions` are deprecated by Microsoft (removed in October 2027) in favour of `passkeyProfiles` /
  `defaultPasskeyProfile`. Both are still managed; declaring the legacy ones adds a `DeprecatedAuthenticationMethodProperty` plan reason and warning
  (what-if and create).
- The plan-time checks repeat the offline ones on evaluated values (`InvalidAuthenticationMethodConfiguration` offline, an unsupported row at plan time):
  the method name, `state` (`enabled` / `disabled`), AAGUID format, an enforced allow-list (`isEnforced: true`, `enforcementType: allow`) with no
  `aaGuids` (it would block every passkey), Temporary Access Pass `defaultLength` 8–48 and lifetimes 10–43200 minutes with minimum ≤ default ≤ maximum,
  the certificate default and rule modes (`x509CertificateSingleFactor` / `x509CertificateMultiFactor`), and an `id` on every target and passkey profile.
- Graph answers the PATCH with 204 and serves the previous values for a while; the update reads again every 5 s for up to 90 s until the declared values
  show, then takes the last read as it is (the update never fails on it; a rerun shows any remaining drift). Observed in a test tenant: a PATCH of
  Temporary Access Pass `defaultLength` 8 → 9 (inside the documented 8–48) was accepted, and later `minimumLifetimeInMinutes` 60 → 15 (with default 30
  and maximum 120), but a GET still answered the old values more than 60 s later. In both cases the method's `state` was `disabled`: Graph appears to
  accept, but not apply, property changes of a disabled Temporary Access Pass (Learn documents no such rule; probe AM4 re-checks it, PATCHing a lifetime
  alone and the three together and re-reading each for 60 s). The PATCH body is the documented one (`@odata.type` plus the changed top-level properties),
  so EARM sends the change as declared; a rerun of a disabled method then still shows the drift (whether a PATCH that enables the method and changes the
  values together applies them is not verified yet). The integration scenario toggles the lifetimes only while the method is enabled.

`deployment delete` treats a kept method like a protected object: it is listed as `[kept: not deletable]`, its outcome is `Refused` with
`errorCode: NotDeletable`, and it does **not** fail the command. A template that also manages `sms` (say) must stay deletable: a refusal that failed the
run could never succeed, and the method can be switched off with `state: disabled` instead.

### Worked example: PIM eligibility for directory roles

The concepts the four PIM request types and `roleManagementPolicies` share (eligible vs active, the request APIs, ordering, onboarding,
retained schedules, conflicts, licensing) are in [pim.md](pim.md); this section and the next three are the provider view.

`Microsoft.Entra/roleEligibilityScheduleRequests` (`RoleEligibilityScheduleRequestsProvider`): eligible directory role assignments through Privileged
Identity Management, written exactly like ARM's `Microsoft.Authorization/roleEligibilityScheduleRequests`, so an ARM snippet pastes unchanged. The
`name` is a `guid(...)` label; the eligibility is found by its tuple (`principalId`, `roleDefinitionId`, `directoryScopeId` or `appScopeId`) and read
live on every run. The pure rules (property set, exclusive pairs, schedule rules) are `RoleEligibilityScheduleRules`, shared with the offline checker.

| Property | Meaning |
|---|---|
| `principalId` | Required: a user, a **role-assignable** group (checked before any request: `RoleEligibilityInvalid` otherwise) or a service principal |
| `roleDefinitionId` / `role` | One of them: the role template id, or the EARM sugar `role` (template id, built-in name, custom role name or id, resolved like `roleAssignments`) |
| `directoryScopeId` / `administrativeUnit` / `appScopeId` | At most one; `administrativeUnit` (id or displayName) is sugar for `/administrativeUnits/{id}`; default `/` |
| `requestType` | Optional, only `AdminAssign` (any casing): EARM chooses the action itself |
| `scheduleInfo` | `startDateTime`, `expiration { type, endDateTime, duration }`; absent = NoExpiration (permanent). Types any casing, sent in Graph's camelCase |
| `justification`, `ticketInfo` | Request metadata: sent with every request, never compared. `justification` defaults to `Deployed by earm` (policies may require one) |
| `condition`, `conditionVersion`, `target*ScheduleId` | ARM-only: refused (`InvalidRoleEligibilityScheduleRequest` offline, an unsupported row at plan time) |

| Operation | Requests |
|---|---|
| Prepare | the `role` / `administrativeUnit` lookups (as for `roleAssignments`), `GET /directoryObjects/{principalId}` and, for a group, `GET /groups/{id}?$select=id,isAssignableToRole` |
| Read | `GET /roleManagement/directory/roleEligibilitySchedules?$filter=principalId eq '…' and roleDefinitionId eq '…' and directoryScopeId eq '…'` (or `appScopeId`), every page, re-matched client-side and kept to `memberType` Direct (an eligibility inherited through a group is not the declared one) and to schedules that are not revoked. Schedules include future-dated eligibilities (schedule *instances* list only the current ones). |
| Create | `POST /roleManagement/directory/roleEligibilityScheduleRequests { action: adminAssign, principalId, roleDefinitionId, directoryScopeId, scheduleInfo, justification, ticketInfo }`, then a read-back by tuple. PIM processes requests asynchronously: until the schedule is listed, the request's `targetScheduleId` (the schedule id) stands in, and the engine confirms the key read like other lagging types (`KeyReadLagsCreate`, `KeyAbsenceCheck`). Graph's `RoleAssignmentExists` is adopted: re-read by tuple, or (not listed yet) left to the engine's replication wait on "already exists". |
| Update | `POST` with `action: adminUpdate` and the full request (in place; never a recreate), then a read-back |
| Delete | `POST` with `action: adminRemove` (tuple + the default justification); an eligibility that is already gone (`RoleAssignmentDoesNotExist`, 404) is not an error |

Comparison: the live schedule is reduced to the tuple and `scheduleInfo` (`RoleSchedulesLookup.LiveView`, `PimScheduleComparison`), and equivalent values are aligned before
the shared diff, so only real drift is a `Modify`:

- the expiration `type` must match; AfterDateTime compares `endDateTime` within **1 s**;
- AfterDuration is applied when the eligibility is created and **never compared afterwards** (Graph may report the schedule as AfterDateTime with the
  computed end): a declared AfterDuration matches a live AfterDuration or AfterDateTime;
- a declared `startDateTime` in the past is never drift (Graph starts such a schedule now); a future one compares within 1 s; an undeclared start is not
  compared;
- `justification` and `ticketInfo` are never compared.

A changed tuple is a **new** eligibility: the template's declaration is created and the old schedule stays, reported as `orphan: retained` with an
`AssignmentRetained` warning (orphan scope: the Direct eligibilities of the declared principals, `OrphanScope.DeclaredPrincipals`), never removed by a
deploy. A request refused by the role's PIM policy (`RoleAssignmentRequestPolicyValidationFailed`: an `ExpirationRule` that forbids permanent
eligibility, a `JustificationRule`, ...) fails the resource with `PimPolicyViolation` and Graph's text. Needs Microsoft Entra ID P2 (or ID Governance).

The lifecycle (plan, adminAssign / adminUpdate / adminRemove, the read-back, `RoleAssignmentExists` adoption, `PimPolicyViolation`, the live-conflict
hook) is shared by every PIM type through the base class `PimScheduleRequestsProvider<TTuple>`; the schedule rules are `PimScheduleRules` (Core), the
comparison `PimScheduleComparison` and the Graph helpers `PimScheduleRequests`. Under it, `RoleScheduleRequestsProvider` (the directory role tuple,
sugar, Prepare and orphan listing, over `RoleSchedulesLookup`) serves the eligibility and the active assignment types alike, and
`GroupScheduleRequestsProvider` (over `GroupSchedulesLookup`) the two PIM for Groups types; a concrete type supplies its endpoints, rules, messages and
the schedules it keeps.

### Worked example: PIM for Groups eligibility

`Microsoft.Entra/privilegedAccess/groupEligibilityScheduleRequests` (`GroupEligibilityScheduleRequestsProvider`): a user or a group made eligible
for the **membership** (`accessId: member`) or the **ownership** (`accessId: owner`) of a group through PIM for Groups. There is no ARM analogue: the
type is namespaced like `conditionalAccess/*` (a top-level type, `parentType` null), the properties follow Graph's
`privilegedAccessGroupEligibilityScheduleRequest`, and the schedule / metadata rules are those of `roleEligibilityScheduleRequests`. The `name` is a
`guid(...)` label; the eligibility is found by (`groupId`, `principalId`, `accessId`). The pure rules are `GroupEligibilityScheduleRules`.

| Property | Meaning |
|---|---|
| `groupId` / `group` | Exactly one: the target group's object id (for a group of the same template, `[reference(resourceId('Microsoft.Entra/groups', 'ops')).id]`), or the EARM sugar `group` (its uniqueName or object id, looked up in Prepare: the group must already exist) |
| `principalId` | Required: a user or a group (a group can be an eligible member of another group); service principals and other objects are refused (`GroupEligibilityInvalid`) |
| `accessId` | Required: `member` or `owner` (any casing, sent in Graph's lowercase) |
| `requestType`, `scheduleInfo`, `justification`, `ticketInfo` | As for `roleEligibilityScheduleRequests`; `scheduleInfo` absent = NoExpiration |

| Operation | Requests |
|---|---|
| Prepare | `GET /groups/{id}` (or `GET /groups(uniqueName='…')` for `group`) `?$select=id,uniqueName,displayName,groupTypes,onPremisesSyncEnabled`: a group with `groupTypes` containing `DynamicMembership` or with `onPremisesSyncEnabled: true` is refused (PIM for Groups governs neither); then `GET /directoryObjects/{principalId}` (users and groups only, never the target group itself) |
| Read | `GET /identityGovernance/privilegedAccess/group/eligibilitySchedules?$filter=groupId eq '…' and principalId eq '…'` (Graph requires a `groupId` or `principalId` filter), every page, matched client-side on `accessId` and kept to `memberType` Direct and to schedules that are not revoked or failed |
| Create | `POST /identityGovernance/privilegedAccess/group/eligibilityScheduleRequests { action: adminAssign, accessId, groupId, principalId, scheduleInfo, justification, ticketInfo }` (`scheduleInfo` is required by Graph and always sent), then the shared read-back. The **first request for a group onboards it in PIM for Groups, permanently** (it cannot be offboarded; its PIM policies then exist) |
| Update | `POST` with `action: adminUpdate` |
| Delete | `POST` with `action: adminRemove`, carrying the live schedule as `scheduleInfo`; an eligibility that is already gone is not an error; a schedule younger than five minutes (400 `ActiveDurationTooShort`) fails with `ActiveAssignmentTooNew` without waiting |

Comparison and retained eligibilities follow the directory-role type (`PimScheduleComparison`). The orphan scope is new: the Direct eligibilities of
the **declared target groups** (`OrphanScope.DeclaredTargetGroups`; `GET …/eligibilitySchedules?$filter=groupId eq '…'` per declared group), so an
undeclared eligibility of a group the template manages (including the old tuple after a principal or `accessId` change) is reported as
`orphan: retained` in both modes, never removed. Eligibility never touches the group's members or owners, so it does not conflict with a pruning
`members` / `owners` list on the same group.

A group's default PIM policy often forbids permanent eligibility (the `Expiration_Admin_Eligibility` rule). Graph then refuses a NoExpiration request
and the resource fails with `PimPolicyViolation`, whose message points at an AfterDateTime / AfterDuration expiration or at changing the group's PIM
policy (`roleManagementPolicies`, [below](#worked-example-pim-role-management-policies)). Needs Microsoft Entra ID P2 (or ID Governance).

### Worked example: PIM active assignments

`Microsoft.Entra/roleAssignmentScheduleRequests` (`RoleAssignmentScheduleRequestsProvider`, ARM analogue `Microsoft.Authorization/roleAssignmentScheduleRequests`)
and `Microsoft.Entra/privilegedAccess/groupAssignmentScheduleRequests` (`GroupAssignmentScheduleRequestsProvider`) are the **active**, **time-bound**
counterparts of the two eligibility types: same properties, sugar, identity tuple, lifecycle and comparison, against the assignment endpoints. The
pure rules (`RoleAssignmentScheduleRules`, `GroupAssignmentScheduleRules`) are the eligibility rules plus one: the schedule must be time-bound
(`PimScheduleRules.CheckTimeBound`). An absent `scheduleInfo` or `expiration`, or NoExpiration, is refused offline (`InvalidRoleAssignmentScheduleRequest`,
`InvalidGroupAssignmentScheduleRequest`) and at plan time for computed values: a permanent active directory role assignment is `Microsoft.Entra/roleAssignments`,
a permanent membership or ownership is the group's `members` / `owners`.

| Operation | Directory roles | PIM for Groups |
|---|---|---|
| Read | `GET /roleManagement/directory/roleAssignmentSchedules?$filter=principalId eq … and roleDefinitionId eq … and directoryScopeId eq …` (Learn: `$filter` supported on these), kept to `memberType` Direct and `assignmentType` **Assigned** | `GET /identityGovernance/privilegedAccess/group/assignmentSchedules?$filter=groupId eq … and principalId eq …` (a groupId or principalId filter is required), matched on `accessId`, kept to Direct and `assignmentType` **assigned** |
| Create / Update / Delete | `POST /roleManagement/directory/roleAssignmentScheduleRequests` with adminAssign / adminUpdate / adminRemove | `POST /identityGovernance/privilegedAccess/group/assignmentScheduleRequests` (`scheduleInfo` always sent; the remove carries the live schedule); the first request onboards the group |

An `Activated` schedule is a principal's activation of an eligibility, never the template's, so it is left out. The assignment schedule list also
carries the **permanent** assignments made outside PIM (Learn, unifiedRoleAssignmentSchedule: "directly through the role assignments API"; for an
onboarded group, the members added through the groups API); Graph marks their origin unreliably (`createdUsing` may be null or the schedule's own
id), so EARM tells them apart by the expiration: a permanent schedule under the declared tuple is **not** the type's. `LiveConflict` refuses it at
plan time (an unsupported row with `PimAssignmentConflict` wording), when a create adopts it after `RoleAssignmentExists` (the resource fails), and on
`deployment delete` (`IDeleteRefusalSource`: kept, `NotDeletable`); EARM never converts it into a time-bound assignment or removes it. The orphan
listing (`DeclaredPrincipals` for roles, `DeclaredTargetGroups` for groups) reports only time-bound schedules (`active:`, `active member:`,
`active owner:`); permanent ones are reported by `roleAssignments` or belong to the group's lists.

**Template conflicts** (`IDeclarationConflictSource`, `DeclarationConflicts`): the planner asks these providers once the instances are expanded and the
dependency graph built, before any Graph call, in what-if and deployment alike. An error `PimAssignmentConflict` is raised when

- a `roleAssignmentScheduleRequests` and a `roleAssignments` resource, or an entry of the principal's `directoryRoles`, name the same principal, role and
  scope: Graph holds one assignment for both, so one type makes it time-bound and the other permanent (and re-creates it when it expires);
- a `privilegedAccess/groupAssignmentScheduleRequests` targets a group of the template that declares `members` (for `accessId: member`) or `owners` (for
  `owner`), even an empty list. A PIM active member is a real member (it appears in `/groups/{id}/members`), so the authoritative list would remove it
  in the edge pass. Listing the principal in `members` instead would make the membership permanent, so the conflict is refused outright rather than
  allowed when the principal is listed; drop the list or use `groupEligibilityScheduleRequests` (eligibility never touches the members);
- the principal of a `groupAssignmentScheduleRequests` lists the target group in `memberOf` / `ownerOf` (ensure-only: re-added permanently when the
  schedule ends).

Values are compared as written, per expanded instance: two values match when they reference the same declared resource (`reference()` / `resourceId()`,
resolved per copy iteration), are the same literal, or the same expression text outside copy loops; a role also matches between `role` names and
`roleTemplate('…')`, a scope defaults to `/`, and a computed `accessId` conflicts with either list. Differently spelled values (a literal object id
against a reference) are not matched; the live check above covers what is already deployed.

### Worked example: PIM role management policies

`Microsoft.Entra/roleManagementPolicies` (`RoleManagementPoliciesProvider`, ARM analogue `Microsoft.Authorization/roleManagementPolicies`) manages the
**rules** of the PIM policy (role settings) of a directory role or of a PIM for Groups membership / ownership. The declaration is ARM's:
`properties.rules[]` with `id`, `ruleType` (optional: implied by the id) and `target`, plus the per-type fields, which have the same names in Graph
(Learn: unifiedrolemanagementpolicyrule-update, approvalsettings, unifiedapprovalstage). Two things are mapped (`RoleManagementPolicyShape`):

| ARM (template) | Graph |
|---|---|
| `ruleType: RoleManagementPolicyApprovalRule` (Expiration, Enablement, AuthenticationContext, Notification) | `@odata.type: #microsoft.graph.unifiedRoleManagementPolicyApprovalRule` (required on every rule PATCH) |
| approver `{ id, userType: User }` | `{ @odata.type: #microsoft.graph.singleUser, userId }` |
| approver `{ id, userType: Group }` | `{ @odata.type: #microsoft.graph.groupMembers, groupId }` |

An approver's `description` is read-only in Graph (never compared or sent); `isBackup: false` and absent are the same. `isBackup: true` is refused offline (`InvalidRoleManagementPolicy`): Graph accepts it on a PATCH but never returns it when the policy is read (real-tenant probe PP3), so EARM could neither verify it nor detect drift and every plan would show a change.

The policy is named by its **assignment**: `scopeType` (`DirectoryRole`, or Graph's `Directory`; `Group`), `scopeId` (`/`, or the group's object id) and
`roleDefinitionId` (the role template id, or `member` / `owner`), or by the sugar `role` (resolved like `roleAssignments`' `role`; DirectoryRole, `/`) or
`group` (uniqueName or object id; dynamic and on-premises synced groups refused) + `accessId`. The `guid()` name is a label.

| Operation | Graph |
|---|---|
| Read | `GET /policies/roleManagementPolicyAssignments?$filter=scopeId eq '…' and scopeType eq '…' and roleDefinitionId eq '…'&$expand=policy($expand=rules)` (the scope filter is required; Learn: policyroot-list-rolemanagementpolicyassignments), re-matched client-side; the policy id is the Graph id |
| Create | never: a missing policy is `PimPolicyNotAvailable` (see below) |
| Update | `PATCH /policies/roleManagementPolicies/{policyId}/rules/{ruleId}` once per **changed** declared rule, `@odata.type` first |
| Delete | never: kept as `NotDeletable` (`IDeleteRefusalSource`); revert a setting by declaring the value to restore |

**Only the declared rule ids are managed.** The live view holds every rule in ARM's shape; the plan reduces it to the declared ids (a declared id the
live policy does not have is `RoleManagementPolicyInvalid`, listing the policy's ids), fills every member a declared rule leaves out from the live rule
(`FillFromLive`: nested objects, approval stages by position, approvers by id; Graph replaces a rule's complex members whole), and diffs with the
`rules` member key `id`. Enums compare case-insensitively (`RoleManagementPolicyRules.NormalizeRules`), approvers as a set, `maximumDuration` by value
(`PT480M` = `PT8H`), an empty `claimValue` as absent; `enabledRules: []` and `notificationRecipients: []` clear. The pure rules (offline
`InvalidRoleManagementPolicy`, plan time for computed values) are in `RoleManagementPolicyRules` / `RoleManagementPolicyRuleChecks`.

**Order and onboarding.** A policy is applied before the schedule requests it governs (`IApplyOrderSource`, `RoleManagementPolicyApplyOrder`: the
role eligibility / active assignment requests of the same role, the group eligibility / active assignment requests of the same group and access),
so they are judged by the declared rules (for example a group policy that allows permanent eligibility before a permanent group eligibility). Values
match when they reference the same declared resource or are the same literal (a role also by name vs template id); differently spelled values (a
group's uniqueName in one, its object id in the other) are not matched: add a `dependsOn`. A failed policy skips the requests it governs.
PIM for Groups onboards a group on its first schedule request **or policy update**, and the group's policy and assignment ids change then (Learn:
privilegedidentitymanagement-for-groups-api-overview), so the provider never caches a policy id, and a rule PATCH answered 404 re-reads the policy id
and is sent once more (the previous PATCH onboarded the group). When Graph lists no policy for a group (not onboarded yet):

- a `privilegedAccess/groupEligibilityScheduleRequests` or `groupAssignmentScheduleRequests` of the same deployment names the group and access: the
  policy row is deferred (an `Ignore` row with a `PimPolicyNotAvailable` warning), the requests go ahead under the group's default policy and onboard
  the group, and the next run applies the policy;
- otherwise the row fails with `PimPolicyNotAvailable` (declare a group request or onboard the group in the portal).

A directory role without a policy (an unknown role id) is `PimPolicyNotAvailable` too. Complete mode lists no orphans (policies always exist).

### Worked example: access packages

Five providers cover entitlement management ([access-packages.md](access-packages.md) has the user view and the Learn citations). They share
`EntitlementManagementGraph` (the displayName lookups with their `AmbiguousMatch` guard, the catalog of a package, a catalog's resources and roles) and
`AccessPackageApplyOrder` (the orders the references do not show).

| Provider | Key | Read | Write |
|---|---|---|---|
| `AccessPackageCatalogsProvider` (`entitlementManagement/catalogs`) | `displayName` | `GET …/catalogs?$filter=displayName eq '…'`, re-matched client-side | `POST`, `PATCH` of the declared deltas, `DELETE` (400 → `CatalogNotEmpty`); the overlay defaults (`state`, `isExternallyVisible`) reach only the create body |
| `CatalogResourcesProvider` (`entitlementManagement/catalogResources`) | (`catalogId`, `originId`) | `GET …/catalogs/{id}/resources?$filter=originId eq '…'`; the diff view is the key tuple and `originSystem` | `POST …/resourceRequests` `adminAdd`, then the catalog is read every `ReplicationWait.Interval` until the group shows (403 → `CatalogResourceAccessDenied`, `deliveryFailed` or never listed → `CatalogResourceRequestFailed`); `adminRemove` on delete (400 → `CatalogResourceInUse`). `IApplyOrderSource`: before the role scopes of the same group |
| `AccessPackagesProvider` (`entitlementManagement/accessPackages`) | `displayName` | `GET …/accessPackages?$filter=displayName eq '…'&$expand=catalog`; `catalogId` is copied from `catalog.id` so the immutable binding is diffed | `POST` with `catalog { id }`, `PATCH` without `catalogId`, `DELETE` (assignments → `AccessPackageAssignmentsExist`) |
| `ResourceRoleScopesProvider` (`entitlementManagement/resourceRoleScopes`) | (`accessPackageId`, `resourceOriginId`, `role`) | `GET …/accessPackages/{id}?$expand=resourceRoleScopes($expand=role,scope)`, matched on `scope.originId` and `role.displayName` | `POST …/resourceRoleScopes` with the role picked by display name from the catalog's roles of the group (never built from a pattern: the eligible roles' `originId` is undocumented), `DELETE`. `IApplyOrderSource`: before the policies of the same package |
| `AssignmentPoliciesProvider` (`entitlementManagement/accessPackages/assignmentPolicies`) | parent package + `displayName` | the package (engine parent id, else the package lookup by name) → `GET …/assignmentPolicies?$filter=accessPackage/id eq '…'` | `POST` with `accessPackage { id }`; update is the full `PUT` of `AssignmentPolicyShape.MergeForPut` (live policy re-read with `$expand=questions`, declared values merged in); `DELETE` (assignments → `AccessPackageAssignmentsExist`) |

`Plan` of the policies diffs the live policy cut down to the declared shape (`AssignmentPolicyShape.ProjectLive`): Graph fills in every stage field and a
subject's read-only `description`, which are not drift, while an extra live approver is. All five confirm an empty read with the key absence check
(`KeyReadLagsCreate`).

**Eligible roles and PIM for Groups onboarding.** `GroupScheduleRequestsProvider` (both PIM for Groups request types) implements `IApplyOrderSource`:
a request is applied before every role scope that grants an eligible (or computed) role of its group (`AccessPackageApplyOrder.EligibleRoleScopesOnboardedBy`).
`ResourceRoleScopesProvider.PrepareAsync` checks, for an `Eligible Member` / `Eligible Owner` role whose package and catalog resource already exist,
that the catalog offers the role. When it does not and a same-run request onboards the group, the provider waits while applying (the role scope then
has a dependency created in this run, so its `RetryHint` is `ExpectReplicationLag`; a what-if never has one): it reads the catalog's roles every
`ReplicationWait.Interval` up to `ReplicationWait.Window` (`Settle`), and `CreateAsync` does the same when the catalog resource was not listed yet at
prepare time. `Plan` then returns: a Create row with the wait in its reasons (what-if); an Ignore row with a `PimGroupNotOnboarded` warning when the
wait timed out (the fallback, the next run creates it); an error row `PimGroupNotOnboarded` when no request onboards the group. EARM never onboards a
group by itself.

**Delete warning.** `ResourceRoleScopesProvider` implements `IDeleteWarningSource`: `ReadAsync` adds the package's display name and its delivered
assignment count (`EntitlementManagementGraph.CountDeliveredAssignmentsAsync`, `null` on 400 / 403 / 404) to the live object, and
`DescribeDeleteRisk` turns them into the warning `deployment delete` shows in its preview and logs before the delete (none when nobody holds the role).
Tests: `AccessPackagesProviderTests`, `AccessPackageOnboardingTests` and `AccessPackageRulesTests` over `FakeEntitlementManagement` (routed from `FakeDirectory`), and
`AccessPackageScenarioTests` against a real tenant (`EARM_IT_ALLOW_ACCESS_PACKAGES=1`).
## Adding an edge kind

Part B edge kinds (app role assignments, directory roles) live next to their standalone provider and
are discovered like providers: implement `IEdgeKindHandler` (`EArm.Engine.Edges`) with `Phase = EdgePhase.B`,
attribute the class `[EntraEdgeKind(EdgeKind.<Kind>)]`, give it a `(ResourceTypeCatalog)` or parameterless
constructor, and `ProviderDiscovery.CreateEdgeKinds(catalog)` returns `EdgeKindRegistry.Default` (part A) plus every
attributed handler. `EngineServices` passes that registry to the planner through `PlannerOptions.EdgeKinds` unless
the options already carry one, so nothing else is wired. Failures at startup name the class: a concrete handler
without the attribute, two classes claiming one kind, a class claiming a part A kind, no usable constructor, a
`Kind` that differs from the attribute.

What the engine's edge pass expects from a part B handler (worked example: `Edges/AppRoleAssignmentEdgeKind.cs`):

- **`ResolveItemAsync`** receives the declared object (`{ resourceId | resourceAppId, appRole | appRoleId }`) and
  resolves names there: template objects first (`context.Resolver.TryGetLive` by the service principal's resource id,
  whose name is the appId; `context.FindInstanceByGraphId` for an object id), then Graph. A deploy-time placeholder
  is `EdgeItem.Unknown`, anything the live data refutes is `EdgeItem.Invalid` (the row fails validation and nothing
  of it is written).
- **Encode the assignment in `TargetId`.** The planner keys principal-side existence (`ReadPrincipalAsync`) by `Edge.TargetId` alone, and a principal may hold several roles on one resource, so the target key is
  `{resourceId}/{appRoleId}` (`AppRoleAssignmentsRules.TargetKey`) and the real ids travel in `Edge.Extra`
  (`resourceId`, `appRoleId`, plus the assignment `id` on live edges). Override `GraphIdsOf` to return the real object ids so an
  edge touching an object created in this run is written under `RetryHint.ExpectReplicationLag`. What-if renders the
  target key as the entry's `after` value.
- **`IdentityOf`** includes the extras that distinguish assignments (`AppRoleAssignment|resource|principal|role`);
  the planner de-duplicates operations and matches live edges to declarations by this identity.
- **`RemoveRequest`** takes the assignment id from the live read's extras. Part B kinds are principal-side only, and principal-side
  edges are ensure-only, so the pass never removes them today; the method exists for the contract.
- **Standalone resource vs principal-side item.** The standalone type (`Microsoft.Entra/appRoleAssignments`) is applied
  in the object pass, before the edge pass; a principal-side item for the same composite then finds the assignment in
  place, so the pass writes nothing for the principal. The standalone resource therefore owns the
  assignment: removing the principal-side item alone changes nothing, and removing the standalone resource leaves the
  assignment in place in both modes (reported as `orphan: retained`; `deployment delete -f` with a template that still
  declares it removes it). A principal-side item that is no longer declared is never removed. (Known wart: a what-if on a tenant where neither exists yet shows the
  add under both rows, since the pass cannot see the standalone resource's planned create.)

## Review 1D behaviours

- **Warnings.** A provider reports a non-fatal observation of an apply (`SoftDeleteProbeSkipped`, `GroupHasNoOwnerAtCreate`) in `AppliedResource.Warnings`
  (`ProviderWarning(Code, Message)`, never persisted); a what-if observation of a plan (`SecretExpiresSoon`, `RotationTagNotStamped`) is a row reason of the form `Code: message`
  (`ProviderWarnings.Format`). The executor reports every `AppliedResource.Warnings` entry as a warning diagnostic against the resource, and the planner and
  executor turn a plan row reason that starts with `SecretExpiresSoon:` or `RotationTagNotStamped:` into a warning diagnostic too (`ProviderWarningReasons`), so what-if and create both list them.
  The codes are `EngineDiagnosticCodes` constants documented in [diagnostics.md](diagnostics.md#provider-codes).
- **`x-earm.ignoreLiveOnly`** (overlay): dotted paths (`web`, `api`, `sessionControls.signInFrequency`). Under each path, sub-properties that exist only on the live
  object are neither drift nor nulled in the PATCH (Graph fills server-defaulted children such as `signInFrequency.authenticationType` /
  `frequencyInterval` or `web.redirectUriSettings`). Applied by `ProviderSupport.PruneLiveOnly` to the live diff view of applications and
  Conditional Access policies; to clear such a child, declare it (`null` / `[]`). Live `@odata.*` annotations inside a policy are dropped when it is read.
- **Role-assignable groups** (`isAssignableToRole: true`, Learn group-post-groups example 3): `securityEnabled` defaults to `true`, a Microsoft 365 group defaults to
  `visibility: Private`; a dynamic group, `securityEnabled: false` or another visibility is an unsupported row. (The page also says such a group "cannot have an owner",
  yet its own example creates one with `owners@odata.bind`; EARM does not forbid owners.)
- **Owners at create**: declared `owners` (object ids, at most 20) are bound in the group upsert body as `owners@odata.bind`; the edge pass remains authoritative.
- **Password credentials**: the secret is rotated when `liveEnd < now + rotateBeforeDays` **and** a replacement would live longer than the live credential (the
  declared `endDateTime`, else now + 180 days); otherwise the what-if row is `NoChange` with a `SecretExpiresSoon` reason naming the date. An `addPassword` whose
  answer is lost (timeout, dropped connection, 5xx on the write) is never repeated: the provider lists the application's credentials and the failure message names
  every same-name credential other than the one it was creating (`PasswordCredentialOrphaned`, with the `keyId`); nothing is deleted automatically.
- **Fallbacks** for forms Learn does not document on a read: `GET /groups|applications(uniqueName='…')` → `$filter=uniqueName eq '…'` on a 400;
  the parent application of a credential is always resolved to its object id first (the `uniqueName` key form, then that filter on a 400); the `oauth2PermissionGrants`
  multi-clause `$filter … and …` → `clientId eq '…'` alone with a client-side match on a 400.
- **`ApplicationsProvider.AdoptUniqueNameAsync`** sets the `uniqueName` of an application that has none (adoption by `appId`) with its own PATCH and turns a Graph
  refusal into `UniqueNameNotSettable` (uniqueName is documented as immutable and read-only; an application created without it may not be adoptable by name).
- **Groups adopted by display name** (`GroupsProvider`): `ResolveKey` puts the declared `displayName` (a known string) in `ResourceKey.Hints["displayName"]`. When `GET /groups(uniqueName='…')`
  finds nothing, `ReadAsync` lists `GET /groups?$filter=displayName eq '…'` (basic query) and re-checks client-side (case-insensitive): a match that already holds the
  declared `uniqueName` is the object (the key read lagged); exactly one match WITHOUT a `uniqueName` is adopted; several are `AmbiguousMatch`; a match holding another
  `uniqueName` is ignored (the group is created). `Plan` diffs the adopted group as if it held the key, adds a `properties.uniqueName` delta (a `Modify` even when
  nothing else differs) and a `GroupAdoptedByDisplayName: adopting existing group '<displayName>' (Graph id …) by display name; uniqueName will be set to '<name>'`
  reason (a warning diagnostic in what-if and create). `UpdateAsync` sets `uniqueName` with its own `PATCH /groups/{id}` before the other changes; a 400 is
  `UniqueNameNotSettable` and nothing else is written. `KeyReadLagsCreate` stays `false`: the key read is the alternate key, the fallback targets groups that existed
  before the run, and the read after the adoption (or a create) goes by `uniqueName`, so no absence confirmation or read-your-write wait is needed. Deletes go by
  `uniqueName`: `deployment delete` refuses a group without one (`IDeleteRefusalSource`, `NotDeletable`), so it never deletes a group no deployment adopted.

## Test fixtures

- Drive the provider through `FakeGraphClient` / `FakeDirectory` (`tests/EArm.TestSupport`). Seed objects
  with `AddApplication`, `AddGroup`, `AddUser`, `AddServicePrincipal`, `Add(path, entity)`; stage soft-deleted
  objects with `AddDeleted("/applications", entity)` or `SoftDelete(id)` (they are listed by
  `GET /directory/deletedItems/microsoft.graph.<type>`, restored by `POST …/{id}/restore`, purged by `DELETE`). Like Graph, `DELETE` of an
  application, service principal, user, administrative unit, Microsoft 365 group or **security group** (only distribution lists are deleted immediately) **moves the object there**
  (30 days, `FakeDirectory.Clock`); a create with the same `uniqueName` / `userPrincipalName` / `appId` / Microsoft 365 `mailNickname` conflicts until it is restored or purged.
  The fake is as strict as the Learn pages it was written from: a create without the required properties (group `displayName` / `mailEnabled` / `mailNickname` / `securityEnabled`; user
  `accountEnabled` / `displayName` / `mailNickname` / `passwordProfile.password`; application, administrative unit, role definition and conditional access policy `displayName`; policy `state` one of
  `enabled` / `disabled` / `enabledForReportingButNotEnforced`; named location `@odata.type` plus `ipRanges` or `countriesAndRegions`) is a 400, a `$top` over 999 on a directory collection is a 400, and
  typed objects (`FakeDirectorySchema`: user, group, application, service principal, administrative unit, role definition, permission grant, federated credential, app role assignment) come back
  with only the default property set of their Learn resource page unless `$select` names more; `$select` of a property the type does not have is a 400 (`$select=*` returns the whole object; a selected property the object holds no value for is left out, where Graph answers `null`). Duplicate named-location display names are not rejected (Learn does not document it). Advanced queries
  (`$search`, `$count`, `ne` / `endswith` / `not` in `$filter`) on directory objects need `ConsistencyLevel: eventual` (and `$count=true` for a filter), a `$filter` operator the
  fake does not implement is a 400, an undocumented operation (`PUT` on an entity, `PATCH` of an app role assignment, an untyped `GET /directory/deletedItems`) is a 405 / 400, and
  `owners@odata.bind` / `members@odata.bind` in a create or update body become references.
  Assert on `client.Requests` / `client.Writes` (method, path, headers, body) and on `Directory.Objects(path)`.
  App role assignments live in `/servicePrincipals/{id}/appRoleAssignedTo` (`Objects(...)` to inspect; `POST` fills
  `principalType` and the display names, rejects a duplicate with the Graph 400, and the principal-side
  `/{users|groups|servicePrincipals}/{id}/appRoleAssignments` navigation is derived from them); `/oauth2PermissionGrants`
  rejects a duplicate key with "Permission entry already exists"; deleting a principal cascades both.
- Script failures with `client.Failures` (`ReplicationLag`, `Throttle`, `AlreadyExists`, `Forbid`, …). The fake does
  not retry: pass a `RetryPolicy` with a no-op `IDelayProvider` through `PlannerOptions.RetryPolicy` when the
  scenario needs retries.
- End-to-end: `TemplateLoader.Parse(json, …)` → `DeploymentPlanner.PlanAsync(document, mode, PlanMode.Create, ct)`
  → `DeploymentExecutor.ExecuteAsync(plan, ExecuteOptions.Default, ct)` with a registry from
  `ProviderDiscovery.CreateRegistry(ResourceTypeCatalog.Default)`; `PlanMode.WhatIf` must leave `client.Writes` empty.
- Unit-test the pure rules directly (`ApplicationsRules.DeriveMemberId`, `NormalizeDesired`, `NormalizeLive`,
  `DuplicateValues`, `DisableStep`, `SubstituteAppId`).
