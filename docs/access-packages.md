# Access packages (entitlement management)

EARM manages Microsoft Entra **entitlement management** with five resource types: who may **request** membership of a group, who **approves** the
request, and for how long the membership lasts. People are given the membership by the access package's assignment process, never by the template:
an assignment is a person's request (or an administrator's direct assignment), not desired state, so EARM never lists, creates or removes one.

| Type (`Microsoft.Entra/...`) | What it declares | Graph object |
|---|---|---|
| `entitlementManagement/catalogs` | A catalog: the container of the resources and access packages one team manages | [`accessPackageCatalog`](https://learn.microsoft.com/en-us/graph/api/resources/accesspackagecatalog?view=graph-rest-1.0) |
| `entitlementManagement/catalogResources` | A group added to a catalog (so packages of the catalog may grant its roles) | [`accessPackageResource`](https://learn.microsoft.com/en-us/graph/api/accesspackagecatalog-list-resources?view=graph-rest-1.0), written through [`accessPackageResourceRequest`](https://learn.microsoft.com/en-us/graph/api/resources/accesspackageresourcerequest?view=graph-rest-1.0) |
| `entitlementManagement/accessPackages` | An access package in a catalog | [`accessPackage`](https://learn.microsoft.com/en-us/graph/api/resources/accesspackage?view=graph-rest-1.0) |
| `entitlementManagement/resourceRoleScopes` | A group role (Member, Owner, Eligible Member, Eligible Owner) that a package grants | [`accessPackageResourceRoleScope`](https://learn.microsoft.com/en-us/graph/api/resources/accesspackageresourcerolescope?view=graph-rest-1.0) |
| `entitlementManagement/accessPackages/assignmentPolicies` | A policy of a package: who may request, approval stages, expiration, requestor settings, optional access reviews | [`accessPackageAssignmentPolicy`](https://learn.microsoft.com/en-us/graph/api/resources/accesspackageassignmentpolicy?view=graph-rest-1.0) |

Every type uses the apiVersions of the other types: `2026-10-01-preview` (Graph v1.0, under `/identityGovernance/entitlementManagement`) and
`2026-10-01-preview.beta` (Graph beta, passthrough). Property-level detail is in [format.md](format.md#resource-type-catalog) and the provider view
in [providers.md](providers.md#worked-example-access-packages). The sample [`samples/access-packages.entra.json`](../samples/access-packages.entra.json)
implements the scenario below.

## The scenario

Each subscription has three access groups (`PG <Subscription> Privileged Access`, `Operational Access`, `Read-Only Access`) and an approver group
(`PG <Subscription> Approvers`). People get **eligible** membership of the privileged and operational groups (PIM for Groups, activated when needed)
and **standing** membership of the read-only group, through one access package per group, approved by the subscription's approver group. The
membership granted by a package must not outlive the group's eligibility expiry, so the policy's `expiration.duration` is no longer than the group's
PIM policy `Expiration_Admin_Eligibility` maximum. The approver group's own package is approved by a person chosen outside EARM. Directory role
groups get the same shape, approved by `PG Tenant Approvers`.

```text
catalogs ──► catalogResources (one per group) ──► resourceRoleScopes ◄── accessPackages ◄── accessPackages/assignmentPolicies
```

## Identity: live reads, no state

Like every EARM type (stateless identity: an object is found on every run from its template name or key tuple, never from a local record):

| Type | Key | Lookup |
|---|---|---|
| `catalogs` | `displayName` (the resource name) | `GET /identityGovernance/entitlementManagement/catalogs?$filter=displayName eq '…'`, matched case-insensitively; more than one hit is `AmbiguousMatch` |
| `accessPackages` | `displayName` (the resource name) | `GET …/accessPackages?$filter=displayName eq '…'&$expand=catalog`; more than one hit (in any catalog) is `AmbiguousMatch`. EARM keeps package names unique in the tenant, which Graph does not |
| `accessPackages/assignmentPolicies` | the package (parent) + `displayName` (the child name) | `GET …/assignmentPolicies?$filter=accessPackage/id eq '…'`, matched on displayName client-side; two policies of the package with the name are `AmbiguousMatch` |
| `catalogResources` | the tuple (`catalogId`, `originId`); the GUID `name` is a label | `GET …/catalogs/{catalogId}/resources?$filter=originId eq '…'` |
| `resourceRoleScopes` | the tuple (`accessPackageId`, `resourceOriginId`, `role`); the GUID `name` is a label | `GET …/accessPackages/{id}?$expand=resourceRoleScopes($expand=role,scope)`, matched on `scope.originId` and `role.displayName` |

`$filter` on `displayName` is documented for catalogs ([list catalogs](https://learn.microsoft.com/en-us/graph/api/entitlementmanagement-list-catalogs?view=graph-rest-1.0))
and access packages ([list access packages](https://learn.microsoft.com/en-us/graph/api/entitlementmanagement-list-accesspackages?view=graph-rest-1.0)),
`accessPackage/id` for policies ([list assignment policies](https://learn.microsoft.com/en-us/graph/api/entitlementmanagement-list-assignmentpolicies?view=graph-rest-1.0))
and `originId` for catalog resources ([list resources](https://learn.microsoft.com/en-us/graph/api/accesspackagecatalog-list-resources?view=graph-rest-1.0)).
All five lists can miss an object for a moment after its write (Microsoft's own sample waits before reading the resources back:
[create a catalog](https://learn.microsoft.com/en-us/entra/id-governance/entitlement-management-catalog-create)), so every type confirms an empty
read with the **key absence check** before it plans a create (four reads, four seconds apart, see [engine.md](engine.md)).

A renamed catalog, package or policy is a new object (ARM rule). A changed tuple of a catalog resource or role scope is a new object too; the old one
stays (catalog resources and role scopes are not reported as retained: they grant nothing by themselves and are removed by `deployment delete`).

## Properties

### Catalogs

`displayName` comes from the name; `description`, `state` (`published` / `unpublished`) and `isExternallyVisible` are written as declared
([create](https://learn.microsoft.com/en-us/graph/api/entitlementmanagement-post-catalogs?view=graph-rest-1.0),
[update](https://learn.microsoft.com/en-us/graph/api/accesspackagecatalog-update?view=graph-rest-1.0): `PATCH`). **Create-only defaults:** a catalog EARM
creates gets `state: published` and `isExternallyVisible: false` unless declared; an existing catalog is changed only where the template declares a
property. `catalogType` is service-controlled and not compared.

### Catalog resources

```json
{ "type": "Microsoft.Entra/entitlementManagement/catalogResources", "apiVersion": "2026-10-01-preview",
  "name": "[guid('pg-catalog', 'pg-dev-privileged-access')]",
  "properties": { "catalogId": "[reference('catalog').id]", "originId": "[reference('devPrivileged').id]" } }
```

`originSystem` defaults to `AadGroup`, the only origin EARM manages (applications and SharePoint sites are not supported yet). Every property is part
of the key: nothing is updated in place.

- **Add**: `POST /identityGovernance/entitlementManagement/resourceRequests` with `requestType: adminAdd`, `resource { originId, originSystem }` and
  `catalog { id }` ([create resource request](https://learn.microsoft.com/en-us/graph/api/entitlementmanagement-post-resourcerequests?view=graph-rest-1.0)).
  The request has a `state` (`delivered` or `deliveryFailed`,
  [resource request](https://learn.microsoft.com/en-us/graph/api/resources/accesspackageresourcerequest?view=graph-rest-1.0)): `deliveryFailed` is an
  apply error (`CatalogResourceRequestFailed`); any state other than `delivered` and every delivered request is followed by reading the catalog's
  resources until the group is listed (every 5 s, up to 90 s), because the resource only shows in the catalog once the request is processed. A group
  still not listed then is `CatalogResourceRequestFailed` (run again: the next read finds it).
- **Remove** (`deployment delete`): `POST …/resourceRequests` with `requestType: adminRemove`, `resource { id }` (the catalog resource's own id, not the
  group's) and `catalog { id }`. Graph refuses while a package of the catalog still grants a role of the group
  ([create a catalog](https://learn.microsoft.com/en-us/entra/id-governance/entitlement-management-catalog-create)): `CatalogResourceInUse`; the role
  scopes are deleted first by the delete order.
- **Who may add a group**: a catalog owner or Identity Governance Administrator **and** an owner of the group or a role that may change its members
  ([delegate](https://learn.microsoft.com/en-us/entra/id-governance/entitlement-management-delegate)). A **role-assignable** group
  (`isAssignableToRole: true`) can only be added by a **Global Administrator** or **Privileged Role Administrator** (or an owner of the group):
  Identity Governance Administrator and User Administrator are not enough
  ([role-assignable groups](https://learn.microsoft.com/en-us/entra/identity/role-based-access-control/groups-concept)). Graph answers 403; EARM
  reports `CatalogResourceAccessDenied` and says which roles are needed.

### Access packages

`displayName` comes from the name; `catalogId` (the catalog's object id, usually `[reference('catalog').id]`) is required on create and **immutable**
(Graph's `catalog` relationship is "required when creating, read-only",
[access package](https://learn.microsoft.com/en-us/graph/api/resources/accesspackage?view=graph-rest-1.0)): a changed catalog is an error, never a
move or recreate. `description` and `isHidden` are written with `PATCH`
([update](https://learn.microsoft.com/en-us/graph/api/accesspackage-update?view=graph-rest-1.0)). **Create-only default:** `isHidden: false`.
The create body carries `catalog { id }` ([create](https://learn.microsoft.com/en-us/graph/api/entitlementmanagement-post-accesspackages?view=graph-rest-1.0)).

`deployment delete` deletes a package; Graph refuses while it has assignments
([delete](https://learn.microsoft.com/en-us/graph/api/accesspackage-delete?view=graph-rest-1.0)): `AccessPackageAssignmentsExist`, and the package
stays. Remove the assignments first (they are people's access, so EARM never does that for you).

### Resource role scopes

```json
{ "type": "Microsoft.Entra/entitlementManagement/resourceRoleScopes", "apiVersion": "2026-10-01-preview",
  "name": "[guid('pg-dev-privileged', 'eligible-member')]",
  "properties": { "accessPackageId": "[reference('devPrivilegedPackage').id]",
                  "resourceOriginId": "[reference('devPrivileged').id]", "role": "Eligible Member" } }
```

`role` is the group role's display name: `Member`, `Owner`, `Eligible Member` or `Eligible Owner`. The role is not built from a pattern: EARM reads
the catalog's roles of the group (`GET …/catalogs/{catalogId}/resourceRoles?$filter=(originSystem eq 'AadGroup' and resource/id eq '…')`,
[list resource roles](https://learn.microsoft.com/en-us/graph/api/accesspackagecatalog-list-resourceroles?view=graph-rest-1.0)) and posts the one with
that display name, with its `originId` (`Member_<groupId>`, `Owner_<groupId>` for the active roles, as documented in
[list resource role scopes](https://learn.microsoft.com/en-us/graph/api/accesspackage-list-resourcerolescopes?view=graph-rest-1.0)), to
`POST …/accessPackages/{id}/resourceRoleScopes` with `scope { originId: <groupId>, originSystem: AadGroup, isRootScope: true }`
([create](https://learn.microsoft.com/en-us/graph/api/accesspackage-post-resourcerolescopes?view=graph-rest-1.0)). A group that is not a resource of
the package's catalog, or a role the catalog does not offer, is `AccessPackageRoleNotAvailable`.

Role scopes have no update ([resource role scope](https://learn.microsoft.com/en-us/graph/api/resources/accesspackageresourcerolescope?view=graph-rest-1.0)):
every property is part of the key, and `deployment delete` uses `DELETE …/accessPackages/{id}/resourceRoleScopes/{id}`
([delete](https://learn.microsoft.com/en-us/graph/api/accesspackage-delete-resourcerolescopes?view=graph-rest-1.0)). Graph does **not** refuse that
delete while the package has assignments: the people assigned lose the role
([change resource roles](https://learn.microsoft.com/en-us/entra/id-governance/entitlement-management-access-package-resources)). Review a
`deployment delete` that removes a role scope with that in mind.

#### Can a package grant eligible PIM for Groups membership?

**Yes, with caveats.** For a group managed by PIM for Groups, the roles a catalog offers include **Eligible Member** and **Eligible Owner** besides
Member and Owner ([change resource roles](https://learn.microsoft.com/en-us/entra/id-governance/entitlement-management-access-package-resources),
[assign eligibility through access packages](https://learn.microsoft.com/en-us/entra/id-governance/entitlement-management-access-package-eligible)).
The caveats:

- The PIM integration is labelled **Preview** on the reference page
  ([PIM and access packages](https://learn.microsoft.com/en-us/entra/id-governance/entitlement-management-access-package-pim-reference)) and needs
  Microsoft Entra ID Governance (or the Entra Suite).
- Graph v1.0 has no flag for it: `accessPackageResourceRole.type` (`active` / `eligible`) only applies to Azure resources
  ([resource role](https://learn.microsoft.com/en-us/graph/api/resources/accesspackageresourcerole?view=graph-rest-1.0)). The eligible group roles are
  separate roles in the catalog's role list, and their `originId` format is **not documented**. That is why EARM discovers the role by its display
  name instead of building the id. This is verified against Learn only; the integration test `ResourceRoleScope_EligibleMember` confirms it on a
  real tenant.
- The group must be **onboarded** in PIM for Groups first (its first PIM schedule request or policy update, see [pim.md](pim.md#group-onboarding)).
  Before that, the catalog offers only Member and Owner, and `Eligible Member` is `AccessPackageRoleNotAvailable`.
- The group's PIM policy caps the eligibility (`Expiration_Admin_Eligibility`): if the package's assignment outlives it, PIM removes the eligibility
  while entitlement management still shows the person as assigned. Keep the policy's `expiration` within the group's eligibility maximum (EARM does
  not compare the two yet).

### Assignment policies

A child of its package: `"name": "<package>/<policy>"` (or nested under the package), the policy's `displayName` is the child name. The properties are
Graph's, passed through as declared ([assignment policy](https://learn.microsoft.com/en-us/graph/api/resources/accesspackageassignmentpolicy?view=graph-rest-1.0)):
`description`, `allowedTargetScope` (`allMemberUsers`, `specificDirectoryUsers`, …), `specificAllowedTargets`, `expiration`
(`{ "type": "afterDuration", "duration": "P180D" }`, [expiration pattern](https://learn.microsoft.com/en-us/graph/api/resources/expirationpattern?view=graph-rest-1.0)),
`requestorSettings`, `requestApprovalSettings` (`isApprovalRequiredForAdd`, `isRequestorJustificationRequired`, `stages` of
[approval stages](https://learn.microsoft.com/en-us/graph/api/resources/accesspackageapprovalstage?view=graph-rest-1.0) with `primaryApprovers` /
`fallbackPrimaryApprovers` / `escalationApprovers`), `reviewSettings` (optional access reviews), `automaticRequestSettings`, `questions`,
`notificationSettings`. Approvers and targets are subject sets; a group is
`{ "@odata.type": "#microsoft.graph.groupMembers", "groupId": "[reference('approvers').id]" }`
([groupMembers](https://learn.microsoft.com/en-us/graph/api/resources/groupmembers?view=graph-rest-1.0)).

- **Compare**: only what the template declares. Live sub-properties the template does not declare (Graph fills in every stage field, a subject's
  read-only `description`, …) are not drift; subject sets compare order-insensitively.
- **Create**: `POST …/assignmentPolicies` with `accessPackage { id }`
  ([create](https://learn.microsoft.com/en-us/graph/api/entitlementmanagement-post-assignmentpolicies?view=graph-rest-1.0)).
- **Update**: Graph only offers `PUT`, a full replace that requires every property
  ([update](https://learn.microsoft.com/en-us/graph/api/accesspackageassignmentpolicy-update?view=graph-rest-1.0)). EARM builds the PUT from the live
  policy (re-read with `$expand=questions` right before the PUT) with the declared values merged in: nested objects merge, a declared array replaces
  the live one (its objects merged with the live entry at the same position, or with the same `groupId` / `userId` for subject sets). Undeclared
  settings therefore keep their live values, as in a PATCH.
- **Delete**: `DELETE …/assignmentPolicies/{id}`; Graph refuses while the policy has assignments (also those of users deleted in the last 30 days,
  [troubleshoot](https://learn.microsoft.com/en-us/entra/id-governance/entitlement-management-troubleshoot)): `AccessPackageAssignmentsExist`.

## Lifecycle

| Step | What EARM does |
|---|---|
| Create | Catalog, catalog resource (resource request, then the wait), package, role scope, policy, in that order. |
| Update | `PATCH` for catalogs and packages, the merged `PUT` for policies; catalog resources and role scopes have nothing to update. |
| Incremental run | Nothing the template does not declare is touched: other catalogs, packages, resources, role scopes and policies stay, and so do every assignment and request. |
| Complete run | Report-only, as for every type: undeclared catalogs and packages are listed (`orphan: undeclared`); nothing is deleted. |
| `deployment delete` | Reverse order: policies and role scopes, then packages, then catalog resources (adminRemove), then catalogs. Graph refuses a policy or package with assignments (`AccessPackageAssignmentsExist`) and a catalog that still holds packages (`CatalogNotEmpty`, [create a catalog](https://learn.microsoft.com/en-us/entra/id-governance/entitlement-management-catalog-create)); those objects stay and the delete reports the error. |

Assignments to people (`assignments`, `assignmentRequests`) are **out of scope**: they are requests made by people (or direct assignments made by an
administrator as a one-off act), not desired state.

### Order

The template's references give most of the order (a package references its catalog, a role scope its package and group, a policy is a child of its
package). Two dependencies are not visible in the references, and the providers declare them through `IApplyOrderSource`
([engine.md](engine.md#pipeline)), as the PIM policies do:

- a **catalog resource** applies before every role scope that grants a role of the same group (`resourceRoleScopes.resourceOriginId` and
  `catalogResources.originId` reference the same declared group, or are the same literal id): the group must be in the catalog first;
- a **role scope** applies before every policy of the same package: the first assignment a policy allows then already grants the role.

Apply phases (a tie-breaker inside a dependency batch): catalogs with the administrative units (4); catalog resources and packages with the
assignments (5); role scopes and policies with the Conditional Access policies (6).

## Permissions

| Type | Delegated scope | Application permission | Entra roles |
|---|---|---|---|
| all five | `EntitlementManagement.ReadWrite.All` (what-if: `EntitlementManagement.Read.All`) | `EntitlementManagement.ReadWrite.All` | Identity Governance Administrator, or the catalog roles (catalog creator for a new catalog, catalog owner, access package manager) |
| `catalogResources` | also `Group.Read.All` | also `Group.ReadWrite.All` (and `RoleManagement.ReadWrite.Directory` for a role-assignable group) | also an owner of the group or a role that changes its members; **Global Administrator or Privileged Role Administrator for a role-assignable group** |

Learn: every entitlement management API page lists `EntitlementManagement.ReadWrite.All` as the least privileged permission for writes and
`EntitlementManagement.Read.All` for reads; the role matrix is in [delegate](https://learn.microsoft.com/en-us/entra/id-governance/entitlement-management-delegate).
The scopes come from the overlays, so `earm permissions`, `earm auth login` and the per-run scope set (`RunScopes`) pick them up with no special
case. A signed-in user needs the Entra role **active** (PIM: activate it and sign in again). Entitlement management needs Microsoft Entra ID P2 or
ID Governance; eligible group roles need ID Governance.

## Not covered

Assignments and assignment requests, connected organizations, custom extensions, catalog role assignments (catalog owners and readers), applications
and SharePoint sites as catalog resources, and an offline check that a policy's expiration stays within the granted group's eligibility maximum.
