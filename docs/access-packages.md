# Access packages (entitlement management)

EARM manages Microsoft Entra **entitlement management** with five resource types: who may **request** membership of a group, who **approves** the
request, and for how long the membership lasts. People are given the membership by the access package's assignment process, never by the template:
an assignment is a person's request (or an administrator's direct assignment), not desired state, so EARM never lists, creates or removes one.

| Type (`Microsoft.Entra/...`) | What it declares | Graph object |
|---|---|---|
| `entitlementManagement/catalogs` | A catalog: the container of the resources and access packages one team manages | [`accessPackageCatalog`](https://learn.microsoft.com/en-us/graph/api/resources/accesspackagecatalog?view=graph-rest-1.0) |
| `entitlementManagement/catalogResources` | A group, an application or a SharePoint Online site added to a catalog (so packages of the catalog may grant its roles) | [`accessPackageResource`](https://learn.microsoft.com/en-us/graph/api/accesspackagecatalog-list-resources?view=graph-rest-1.0), written through [`accessPackageResourceRequest`](https://learn.microsoft.com/en-us/graph/api/resources/accesspackageresourcerequest?view=graph-rest-1.0) |
| `entitlementManagement/accessPackages` | An access package in a catalog | [`accessPackage`](https://learn.microsoft.com/en-us/graph/api/resources/accesspackage?view=graph-rest-1.0) |
| `entitlementManagement/resourceRoleScopes` | A role that a package grants: a group role (Member, Owner, Eligible Member, Eligible Owner), an app role, or a site group | [`accessPackageResourceRoleScope`](https://learn.microsoft.com/en-us/graph/api/resources/accesspackageresourcerolescope?view=graph-rest-1.0) |
| `entitlementManagement/accessPackages/assignmentPolicies` | A policy of a package: who may request, approval stages, expiration, requestor settings, optional access reviews | [`accessPackageAssignmentPolicy`](https://learn.microsoft.com/en-us/graph/api/resources/accesspackageassignmentpolicy?view=graph-rest-1.0) |

Every type uses the apiVersions of the other types: `2026-10-01-preview` (Graph v1.0, under `/identityGovernance/entitlementManagement`) and
`2026-10-01-preview.beta` (Graph beta, passthrough). Property-level detail is in [format.md](format.md#resource-type-catalog) and the provider view
in [providers.md](providers.md#worked-example-access-packages). The sample [`samples/access-packages.entra.json`](../samples/access-packages.entra.json)
implements the scenario below; [`samples/access-packages-apps-sites.entra.json`](../samples/access-packages-apps-sites.entra.json) grants an app role
and a SharePoint Online site group ([Resource kinds](#resource-kinds)).

## The scenario

Each subscription has three access groups (`PG <Subscription> Privileged Access`, `Operational Access`, `Read-Only Access`) and an approver group
(`PG <Subscription> Approvers`). People get **eligible** membership of the privileged and operational groups (PIM for Groups, activated when needed)
and **standing** membership of the read-only group, through one access package per group, approved by the subscription's approver group. The
membership granted by a package must not outlive the group's eligibility expiry, so the policy's `expiration.duration` is no longer than the group's
PIM policy `Expiration_Admin_Eligibility` maximum. The approver group gets its own package, whose policy (approval by `PG Tenant Approvers`, a short
expiration) is written in the template too. Every access group's package is reviewed quarterly by the approver group (`PG Tenant Approvers` as the
fallback reviewers). Directory role groups get the same shape, approved by `PG Tenant Approvers`.

**Every package carries its assignment policy in the template.** People decide the approval settings (who approves, how many stages, how long access
lasts), but the decision is declared next to the package, so it is reviewed in the what-if and repaired when it drifts; a package without a policy
cannot be requested at all.

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
| `catalogResources` | the tuple (`catalogId`, `originId`); the GUID `name` is a label; a site URL is compared case-insensitively and without its trailing slash | `GET …/catalogs/{catalogId}/resources?$filter=originId eq '…'` (a site URL without its trailing slash; when that finds nothing, the catalog's whole list, matched client-side) |
| `resourceRoleScopes` | the tuple (`accessPackageId`, `resourceOriginId`, `role`); the GUID `name` is a label | `GET …/accessPackages/{id}?$expand=resourceRoleScopes($expand=role,scope)`, matched on `scope.originId` (a site URL as above) and `role.displayName` or `role.originId` |

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

`originSystem` defaults to `AadGroup`; `AadApplication` adds an application and `SharePointOnline` a site ([Resource kinds](#resource-kinds)). Every
property is part of the key: nothing is updated in place.

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

#### Resource kinds

The resource request has the same shape for every kind; only `originSystem` and the meaning of `originId` change
([create resource request](https://learn.microsoft.com/en-us/graph/api/entitlementmanagement-post-resourcerequests?view=graph-rest-1.0), examples 1 to 3):

| `originSystem` | `originId` | Roles a package can grant (`resourceRoleScopes.role`) | Who may add it |
|---|---|---|---|
| `AadGroup` (default) | the group's object id | `Member`, `Owner`; `Eligible Member`, `Eligible Owner` for a group PIM for Groups manages | catalog owner or Identity Governance Administrator **and** group owner or a role that changes its members (above) |
| `AadApplication` | the **service principal's** object id (the enterprise application), for example `[reference(resourceId('Microsoft.Entra/servicePrincipals', reference('app').appId)).id]` | the app roles, by display name or app role id; `Default Access` for an app without app roles | Identity Governance Administrator, or a catalog owner who owns the application or is an Application Administrator / Cloud Application Administrator (the action checked is `microsoft.directory/servicePrincipals/appRoleAssignedTo/update`) |
| `SharePointOnline` | the site URL, for example `https://contoso.sharepoint.com/sites/Orders` (casing and a trailing slash do not matter) | the site's groups, by full display name (`Orders Owners`, `Orders Members`, `Orders Visitors`) or by number in the site (`3`, `4`, `5` for the default groups) | catalog owner or Identity Governance Administrator **and** SharePoint Administrator or an administrator of the site |

```json
{ "type": "Microsoft.Entra/entitlementManagement/catalogResources", "apiVersion": "2026-10-01-preview",
  "name": "[guid('orders-catalog', 'orders-site')]",
  "properties": { "catalogId": "[reference('catalog').id]", "originId": "https://contoso.sharepoint.com/sites/Orders", "originSystem": "SharePointOnline" } }
```

- **Order**: a service principal declared in the template is referenced by `originId`, so it is created first (and the catalog resource after it, then
  the role scopes). A site is not an EARM resource: it must exist, and nothing orders it.
- **Sites need a delegated caller**: Graph lists SharePoint Online resources of a catalog only to delegated callers, not to applications
  ([list resources](https://learn.microsoft.com/en-us/graph/api/accesspackagecatalog-list-resources?view=graph-rest-1.0)). An app-only deployment can
  request the site but never reads it back, so the create ends in `CatalogResourceRequestFailed` and every later run plans a create; deploy a site as a
  signed-in user.
- **Permissions**: an application caller needs `Application.ReadWrite.All` for an application and `Sites.FullControl.All` for a site, in place of the
  group's `Group.ReadWrite.All` (same Learn page). `earm permissions` asks for the one of the declared `originSystem`, all three when it is computed.

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

For a group, `role` is the group role's display name: `Member`, `Owner`, `Eligible Member` or `Eligible Owner`. The role is not built from a pattern: EARM reads
the catalog's roles of the group (`GET …/catalogs/{catalogId}/resourceRoles?$filter=(originSystem eq 'AadGroup' and resource/id eq '…')`,
[list resource roles](https://learn.microsoft.com/en-us/graph/api/accesspackagecatalog-list-resourceroles?view=graph-rest-1.0)) and posts the one with
that display name, with its `originId` (`Member_<groupId>`, `Owner_<groupId>` for the active roles, as documented in
[list resource role scopes](https://learn.microsoft.com/en-us/graph/api/accesspackage-list-resourcerolescopes?view=graph-rest-1.0)), to
`POST …/accessPackages/{id}/resourceRoleScopes` with `scope { originId: <groupId>, originSystem: AadGroup, isRootScope: true }`
([create](https://learn.microsoft.com/en-us/graph/api/accesspackage-post-resourcerolescopes?view=graph-rest-1.0)). A group that is not a resource of
the package's catalog, or an active role the catalog does not offer, is `AccessPackageRoleNotAvailable`; an eligible role of a group PIM for Groups has
not onboarded is `PimGroupNotOnboarded` (below).

**Roles of an application or a site.** Set `resourceOriginId` to the catalog resource's `originId`; `originSystem` may be declared on the role scope
too (`AadApplication` or `SharePointOnline`) and is otherwise taken from the catalog resource. EARM lists the catalog's roles of the resource the same way and picks the one
whose **display name** is `role`, else the one whose role **`originId`** is `role`:

- an application: the app role's display name (`Approver`), or its id (the catalog role's `originId` is the app role id; Learn:
  [create role scope](https://learn.microsoft.com/en-us/graph/api/accesspackage-post-resourcerolescopes?view=graph-rest-1.0), example 2). The app role's
  `value` (`Orders.Approve`) is not matched: the catalog does not list it. An app without app roles offers `Default Access`;
- a site: the site group's full display name (`Orders Members`), or its number in the site (`4`; the catalog role's `originId` is "the sequence number of
  the role in the site", same page, example 1, and [list resource roles](https://learn.microsoft.com/en-us/graph/api/accesspackagecatalog-list-resourceroles?view=graph-rest-1.0),
  example 2). A short name (`Members`) is not matched: the group names start with the site title.

A role named by its id or number keeps that spelling in the diff, so a rerun is NoChange. A role the catalog does not offer is
`AccessPackageRoleNotAvailable`, listing the roles it offers. The eligible-role handling below applies to groups only: an app role whose name starts with
`Eligible` is an ordinary role.

Role scopes have no update ([resource role scope](https://learn.microsoft.com/en-us/graph/api/resources/accesspackageresourcerolescope?view=graph-rest-1.0)):
every property is part of the key, and `deployment delete` uses `DELETE …/accessPackages/{id}/resourceRoleScopes/{id}`
([delete](https://learn.microsoft.com/en-us/graph/api/accesspackage-delete-resourcerolescopes?view=graph-rest-1.0)). Graph does **not** refuse that
delete while the package has assignments: the people assigned lose the role
([change resource roles](https://learn.microsoft.com/en-us/entra/id-governance/entitlement-management-access-package-resources)). So when a delete is
planned (`deployment delete` and its preview, or the delete half of a replacement) EARM counts the package's delivered assignments, once, instead of on every
read of the role scope (`GET …/assignments?$filter=accessPackage/id eq '…' and state eq 'delivered'`,
[list assignments](https://learn.microsoft.com/en-us/graph/api/entitlementmanagement-list-assignments?view=graph-rest-1.0)), and `deployment delete`
(its preview included) **warns** before it removes a role scope people hold, like the other delete warnings: "2 active assignments of access package
'PG Development Privileged Access' (…) hold the 'Eligible Member' role of group …". When the assignments cannot be read (403), the warning says so
instead; nobody holding it means no warning.

#### Can a package grant eligible PIM for Groups membership?

**Yes, with caveats.** For a group managed by PIM for Groups, the roles a catalog offers include **Eligible Member** and **Eligible Owner** besides
Member and Owner ([change resource roles](https://learn.microsoft.com/en-us/entra/id-governance/entitlement-management-access-package-resources),
[assign eligibility through access packages](https://learn.microsoft.com/en-us/entra/id-governance/entitlement-management-access-package-eligible)).
The caveats:

- The PIM integration is labelled **Preview** on the reference page
  ([PIM and access packages](https://learn.microsoft.com/en-us/entra/id-governance/entitlement-management-access-package-pim-reference)) and needs
  Microsoft Entra ID Governance (or the Entra Suite). Without the licence Graph refuses the role scope's create with 400 `NoGovernanceSku`, which EARM
  reports as **`GovernanceLicenseRequired`** (assign the licence, or grant Member / Owner instead). The what-if does not predict it: telling a
  Governance SKU apart in `GET /subscribedSkus` needs a list of service plan ids Learn does not publish for this feature, and the read would add
  `LicenseAssignment.Read.All` (or `Organization.Read.All`) to every template that grants an eligible role, so the check is apply-time only.
- Graph v1.0 has no flag for it: `accessPackageResourceRole.type` (`active` / `eligible`) only applies to Azure resources
  ([resource role](https://learn.microsoft.com/en-us/graph/api/resources/accesspackageresourcerole?view=graph-rest-1.0)). The eligible group roles are
  separate roles in the catalog's role list, and their `originId` format is **not documented**. That is why EARM discovers the role by its display
  name instead of building the id. This is verified against Learn only; the integration test `It_AccessPackages_ResourceRoleScope_EligibleMember` confirms it on
  a real tenant.
- **Prerequisite: the group must be onboarded in PIM for Groups first.** "Once the resource is managed by PIM, eligible roles can be added as its role
  assignment in an access package" ([assign eligibility through access packages](https://learn.microsoft.com/en-us/entra/id-governance/entitlement-management-access-package-eligible)),
  and a group cannot be onboarded explicitly: PIM onboards it on its first eligibility or assignment schedule request, or in the portal
  ([PIM for Groups API overview](https://learn.microsoft.com/en-us/graph/api/resources/privilegedidentitymanagement-for-groups-api-overview); see
  [pim.md](pim.md#group-onboarding)). EARM onboards a group only when a declaration of the template does. Before onboarding the catalog offers only
  Member and Owner:
  - **A same-run declaration onboards the group: one run.** Two declarations of the **same deployment** onboard the group when they name it (the same
    declared group referenced, or the same literal id; the group a `roleManagementPolicies` names by `group` or `scopeId`):
    - a `privilegedAccess/groupEligibilityScheduleRequests` or `groupAssignmentScheduleRequests` for the group;
    - a group `Microsoft.Entra/roleManagementPolicies` (`scopeType` `Group`, or `group` / `accessId`) that opts in to onboarding with
      `properties.onboarding.temporaryEligiblePrincipalId` ([pim.md](pim.md#group-onboarding)); its create onboards the group with a temporary
      eligibility. Either access (`member` or `owner`) onboards the whole group, so it covers Eligible Member and Eligible Owner alike.

    The role scope is **ordered after that declaration** (the group schedule request and role management policy providers declare it through
    `IApplyOrderSource`; the policies are phase 4 and the role scopes phase 6 anyway), and when it is applied it **waits** for the catalog to offer the
    eligible role, reading the catalog's roles every 5 s for up to 90 s (the replication wait; real tenants list a group's PIM policy 1-2 s after its
    first eligibility). The what-if shows the role scope as a Create that waits for the onboarding; a group only known at deployment (`?` in the
    what-if) is matched by the declarations, the same way.
  - **Fallback:** when the role is still not offered after the wait, the row is deferred (an Ignore row with a `PimGroupNotOnboarded` warning) and the
    **next run** creates the role scope.
  - **Nothing onboards the group:** the row is the error **`PimGroupNotOnboarded`** (in the style of the PIM policies' `PimPolicyNotAvailable`).
    The check runs while planning when the package and the catalog resource already exist; otherwise the create reports it.
  The beta `privilegedAccess` register API is not used (it stops returning data on 28 October 2026).
- The group's PIM policy caps the eligibility (`Expiration_Admin_Eligibility`): if the package's assignment outlives it, PIM removes the eligibility
  while entitlement management still shows the person as assigned. Keep the policy's `expiration` within the group's eligibility maximum. When the template declares
  the group's PIM policy (a `roleManagementPolicies` with `group` / `scopeId` and the same `accessId`) and its `Expiration_Admin_Eligibility` rule
  requires an expiration, EARM compares the two before planning: a policy whose `expiration.duration` is longer than the rule's `maximumDuration`, or
  that never expires (`noExpiration`), is the **warning** `AccessPackageExpirationExceedsEligibility` (what-if and create alike). A warning, not an
  error: the deployment itself succeeds and the loss happens later, to the people assigned. A group policy the template does not declare is not read,
  an `afterDateTime` end is not compared, and values only known at deployment are skipped.

### Assignment policies

A child of its package: `"name": "<package>/<policy>"` (or nested under the package), the policy's `displayName` is the child name. The properties are
Graph's, passed through as declared ([assignment policy](https://learn.microsoft.com/en-us/graph/api/resources/accesspackageassignmentpolicy?view=graph-rest-1.0)):
`description`, `allowedTargetScope` (`allMemberUsers`, `specificDirectoryUsers`, …), `specificAllowedTargets`, `expiration`
(`{ "type": "afterDuration", "duration": "P180D" }`, [expiration pattern](https://learn.microsoft.com/en-us/graph/api/resources/expirationpattern?view=graph-rest-1.0)),
`requestorSettings`, `requestApprovalSettings` (`isApprovalRequiredForAdd`, `isRequestorJustificationRequired`, `stages` of
[approval stages](https://learn.microsoft.com/en-us/graph/api/resources/accesspackageapprovalstage?view=graph-rest-1.0) with `primaryApprovers` /
`fallbackPrimaryApprovers` / `escalationApprovers`), `reviewSettings` ([Access reviews](#access-reviews)), `automaticRequestSettings`, `questions`,
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
  the live one (its objects merged with the live entry at the same position, or with the same `groupId` / `userId` for subject sets, or the same
  `@odata.type` and `managerLevel` for a subject set without an id such as `requestorManager`). Undeclared
  settings therefore keep their live values, as in a PATCH.
- **Delete**: `DELETE …/assignmentPolicies/{id}`; Graph refuses while the policy has assignments (also those of users deleted in the last 30 days,
  [troubleshoot](https://learn.microsoft.com/en-us/entra/id-governance/entitlement-management-troubleshoot)): `AccessPackageAssignmentsExist`.

#### Access reviews

`reviewSettings` ([accessPackageAssignmentReviewSettings](https://learn.microsoft.com/en-us/graph/api/resources/accesspackageassignmentreviewsettings?view=graph-rest-1.0))
makes the assignments of the policy reviewed on a schedule. A quarterly review by the approver group, with a fallback group, open 14 days, removing
access nobody reviewed:

```json
"reviewSettings": {
  "isEnabled": true,
  "expirationBehavior": "removeAccess",
  "isRecommendationEnabled": true,
  "isReviewerJustificationRequired": true,
  "isSelfReview": false,
  "schedule": {
    "startDateTime": "2026-11-01T00:00:00Z",
    "expiration": { "type": "afterDuration", "duration": "P14D" },
    "recurrence": { "pattern": { "type": "absoluteMonthly", "interval": 3 }, "range": { "type": "noEnd" } }
  },
  "primaryReviewers": [ { "@odata.type": "#microsoft.graph.groupMembers", "groupId": "[reference('approvers').id]" } ],
  "fallbackReviewers": [ { "@odata.type": "#microsoft.graph.groupMembers", "groupId": "[reference('tenantApprovers').id]" } ]
}
```

- **Shape**: `expirationBehavior` is `keepAccess`, `removeAccess` or `acceptAccessRecommendation`; `schedule` is an
  [entitlementManagementSchedule](https://learn.microsoft.com/en-us/graph/api/resources/entitlementmanagementschedule?view=graph-rest-1.0) whose
  `recurrence` is a [patternedRecurrence](https://learn.microsoft.com/en-us/graph/api/resources/patternedrecurrence?view=graph-rest-1.0) (quarterly:
  `absoluteMonthly` with `interval: 3`, as in Learn's [create policy](https://learn.microsoft.com/en-us/graph/api/entitlementmanagement-post-assignmentpolicies?view=graph-rest-1.0)
  example 2). Reviewers are subject sets: `groupMembers`, `singleUser`, `requestorManager` (`managerLevel`), `targetUserSponsors`, `internalSponsors`,
  `externalSponsors`.
- **Validation** (`InvalidAccessPackageReviewSettings`, offline for literal values and at plan time for computed ones): an enabled review that is not a
  self-review needs `primaryReviewers` and a `schedule` with `startDateTime`; a `groupMembers` reviewer needs `groupId`, a `singleUser` reviewer
  `userId`; `expiration.duration` must be a positive ISO 8601 duration (`P14D`) and is required for `afterDuration` (`endDateTime` for
  `afterDateTime`); the recurrence needs a pattern type, `daysOfWeek` for weekly and relative patterns, `month` 1-12 for yearly patterns, `dayOfMonth`
  for an absolute yearly pattern, an `endDate` for an `endDate` range and `numberOfOccurrences` of at least 1 for a `numbered` range. The schema checks
  the enumerations and the integer ranges.
- **Compare**: only the declared members. Graph answers with every member filled in (the booleans, empty reviewer lists, `month: 0`,
  `dayOfMonth: 0`, `daysOfWeek: []`, `firstDayOfWeek`, `index`, `numberOfOccurrences: 0`, a reviewer's `description`) and may write `startDateTime`
  back with milliseconds: none of that is drift (the same instant is the same value). Reviewers compare as sets: order does not matter, a reviewer
  without an id (`requestorManager`) matches by its type and `managerLevel`, and an extra live reviewer is drift.
- **Update**: the merged PUT above; undeclared review members keep their live values, a declared reviewer list replaces the live one.
- **Licence**: access reviews need **Microsoft Entra ID Governance** for the member users who review or are reviewed; Microsoft Entra ID P2 still covers
  the review capabilities it already had, and sponsors as reviewers need ID Governance
  ([licensing fundamentals](https://learn.microsoft.com/en-us/entra/id-governance/licensing-fundamentals), "Access reviews"). No permission beyond
  `EntitlementManagement.ReadWrite.All` and no role beyond the policy's (access package manager, catalog owner or Identity Governance Administrator) is
  documented for `reviewSettings` (Learn: create policy, permissions). A create or update with an enabled review that Graph refuses (400 / 403) with
  `NoGovernanceSku` or a message about a licence or SKU is reported as **`GovernanceLicenseRequired`**; the error code is undocumented, so the match is
  on the code or message, and the what-if does not predict it.
- **Review instances and decisions** (the access reviews Microsoft Entra runs from the policy, and the decisions reviewers make) are not exposed: they are the reviews'
  runtime results, created by Microsoft Entra on the schedule, not desired state; read them in the portal or with Graph.
## Lifecycle

| Step | What EARM does |
|---|---|
| Create | Catalog, catalog resource (resource request, then the wait), package, role scope, policy, in that order. |
| Update | `PATCH` for catalogs and packages, the merged `PUT` for policies; catalog resources and role scopes have nothing to update. |
| Incremental run | Nothing the template does not declare is touched: other catalogs, packages, resources, role scopes and policies stay, and so do every assignment and request. |
| Complete run | Report-only, as for every type: undeclared catalogs and packages are listed (`orphan: undeclared`); nothing is deleted. |
| `deployment delete` | Reverse order: policies and role scopes, then packages, then catalog resources (adminRemove), then catalogs. A role scope that people hold carries a warning (count, package and role) in the preview and before the delete. Graph refuses a policy or package with assignments (`AccessPackageAssignmentsExist`) and a catalog that still holds packages (`CatalogNotEmpty`, [create a catalog](https://learn.microsoft.com/en-us/entra/id-governance/entitlement-management-catalog-create)); those objects stay and the delete reports the error. |

Assignments to people (`assignments`, `assignmentRequests`) are **out of scope**: they are requests made by people (or direct assignments made by an
administrator as a one-off act), not desired state.

### Order

The template's references give most of the order (a package references its catalog, a role scope its package and group, a policy is a child of its
package). Three dependencies are not visible in the references, and the providers declare them through `IApplyOrderSource`
([engine.md](engine.md#pipeline)), as the PIM policies do:

- a **catalog resource** applies before every role scope that grants a role of the same resource (`resourceRoleScopes.resourceOriginId` and
  `catalogResources.originId` reference the same declared group or service principal, or are the same literal id or site URL, regardless of case and
  trailing slash): the resource must be in the catalog first;
- a **role scope** applies before every policy of the same package: the first assignment a policy allows then already grants the role;
- a **PIM for Groups schedule request** applies before every role scope that grants an eligible role (`Eligible Member`, `Eligible Owner`, or a
  computed role) of the group it names: the request onboards the group, and only then does the catalog offer the eligible roles (see above).

Apply phases (a tie-breaker inside a dependency batch): catalogs with the administrative units (4); catalog resources and packages with the
assignments (5); role scopes and policies with the Conditional Access policies (6).

## Permissions

| Type | Delegated scope | Application permission | Entra roles |
|---|---|---|---|
| all five | `EntitlementManagement.ReadWrite.All` (what-if: `EntitlementManagement.Read.All`) | `EntitlementManagement.ReadWrite.All` | Identity Governance Administrator, or the catalog roles (catalog creator for a new catalog, catalog owner, access package manager) |
| `catalogResources` (group) | also `Group.Read.All` | also `Group.ReadWrite.All` (and `RoleManagement.ReadWrite.Directory` for a role-assignable group) | also an owner of the group or a role that changes its members; **Global Administrator or Privileged Role Administrator for a role-assignable group** |
| `catalogResources` (`AadApplication`) | | also `Application.ReadWrite.All` | Identity Governance Administrator alone suffices; a catalog owner must also own the application or be an Application Administrator / Cloud Application Administrator |
| `catalogResources` (`SharePointOnline`) | | also `Sites.FullControl.All` (but Graph lists sites only to delegated callers: deploy sites signed in) | also **SharePoint Administrator** or an administrator of the site |

Learn: every entitlement management API page lists `EntitlementManagement.ReadWrite.All` as the least privileged permission for writes and
`EntitlementManagement.Read.All` for reads; the role matrix is in [delegate](https://learn.microsoft.com/en-us/entra/id-governance/entitlement-management-delegate).
The scopes come from the overlays, so `earm permissions`, `earm auth login` and the per-run scope set (`RunScopes`) pick them up with no special
case. A signed-in user needs the Entra role **active** (PIM: activate it and sign in again). Entitlement management needs Microsoft Entra ID P2 or
ID Governance; eligible group roles need ID Governance, and so do access reviews of the assignments (P2 still covers the review capabilities it had, see [Access reviews](#access-reviews)).

## Not covered

Assignments and assignment requests, connected organizations, custom extensions, catalog role assignments (catalog owners and readers), Microsoft
Entra roles and Azure resources as catalog resources, resource attributes (`adminUpdate` requests), site scopes other than the root, and an offline
check that a policy's expiration stays within the granted group's eligibility maximum.
