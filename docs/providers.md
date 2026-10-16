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
   404 / "does not reference a valid …" for up to 90 s. Reads use the default hint (a 404 on read means "create").
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

- The overlay defaults `accountEnabled: true` and `appRoleAssignmentRequired: false` are part of the desired state: `Plan`
  fills them in, so a principal that drifted from a default is corrected on the next run.
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
elsewhere in the same deployment) and a 21st credential. A rename is a new resource: the old credential stays (Complete mode
lists it as undeclared; nothing deletes it), so a rename that keeps issuer and subject is refused until you delete the old
credential. Permissions follow the parent rule of the password credentials above.

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

- Defaults: `accountEnabled: true` (part of the desired state), `mailNickname` derived from the UPN local part (create only),
  `passwordProfile.forceChangePasswordNextSignIn: true`.
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
| Delete | a trusted IP location (live `isTrusted: true`) is first unmarked with `PATCH …/{id}` `{"@odata.type": "#microsoft.graph.ipNamedLocation", "isTrusted": false}` (Graph refuses to delete a trusted location: 400, error `1177`), then `DELETE …/{id}`; a 1177 on the DELETE unmarks and retries once, and a second 1177 is surfaced as `NamedLocationTrusted`; any other 400 (a policy still references the location) is surfaced as `NamedLocationReferenced` |

`@odata.type` selects the kind (`#microsoft.graph.ipNamedLocation`: `ipRanges`, `isTrusted`;
`#microsoft.graph.countryNamedLocation`: `countriesAndRegions`, `includeUnknownCountriesAndRegions`,
`countryLookupMethod`) and is immutable: a kind change is `Modify`/`Recreate` (delete, then create; Graph refuses the
delete while a policy references the location). `NamedLocationsRules.NormalizeDesired` runs inside `Plan` (so what-if,
bodies and the result describe the same object): the IPv4/IPv6 range `@odata.type` is inferred when omitted, a CIDR with
host bits set is rejected, ranges and country codes are canonicalised, and the live object is normalised the same way,
so reordered or differently spelt ranges are not drift.

`Microsoft.Entra/conditionalAccess/policies`:

| Operation | Requests |
|---|---|
| Read | `GET /identity/conditionalAccess/policies?$filter=displayName eq '…'` |
| Create | `POST /identity/conditionalAccess/policies` |
| Update | `PATCH …/{id}` carrying every changed top-level property **whole** (`conditions`, `grantControls`, `sessionControls` are replaced, not merged, by Graph; a removed control is an explicit `null`) → `GET …/{id}` |
| Delete | `DELETE …/{id}`; a 404 is "already gone" |

`ConditionalAccessPoliciesRules.NormalizeDesired` fills the defaults (`state: enabledForReportingButNotEnforced`,
`conditions.clientAppTypes: ["all"]`, `grantControls.operator: OR`) and reduces `authenticationStrength` to `{ id }`;
`NormalizeLive` removes read-back noise (`customAuthenticationFactors: []`, `termsOfUse: []`, `platforms: null`). `Plan` refuses
what Graph would reject or the plan guards against: `state: enabled` without `--allow-ca-enforce`, an empty users or
applications include, no control at all, `block` combined with another control, `mfa` together with
`authenticationStrength`, the lockout pattern (all users + all applications + block with no exclusion) and the
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
| Delete | `GET …/{id}` (404 = already gone) → `PATCH …/{id} { isAvailable: false }` while the context is still available (Graph refuses to delete an available one) → `DELETE …/{id}`; a 400 (still available, or referenced by a policy's `includeAuthenticationContextClassReferences`) is surfaced as `Referenced` |

`Plan` requires `displayName` and defaults `isAvailable: true` into the evaluated properties, so a context switched off
outside EARM shows as drift. The id is immutable by definition: another name is another resource, never a recreate.
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
| Create | `POST /policies/authenticationStrengthPolicies { displayName, description, allowedCombinations }` |
| Update | `PATCH …/{id}` only when `displayName` / `description` changed (never carrying `allowedCombinations`) → `POST …/{id}/updateAllowedCombinations { allowedCombinations }` only when the combination set differs → `GET …/{id}` |
| Delete | `DELETE …/{id}`; a 400 while a policy references the strength is surfaced as `Referenced` (`deployment delete` deletes policies first: reverse dependency order) |

`allowedCombinations` is compared as a set of normalised strings (`AuthenticationStrengthsRules.NormalizeCombination`): when
the sets are equal the desired array adopts the live spelling before the shared diff runs, so reordered or differently
cased combinations are not drift; the values Graph receives are the template's own (trimmed). `policyType` is immutable
and stripped on read; a declared `custom` is dropped as implied, anything else is an unsupported row.

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
