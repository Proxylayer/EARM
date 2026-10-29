# Permissions

What the identity EARM signs in as needs for a template, and how to get the exact list:

```powershell
earm permissions -f samples/orders-api.entra.json -p '@samples/orders-api.parameters.json'              # app permissions (default)
earm permissions -f samples/orders-api.entra.json -p '@samples/orders-api.parameters.json' --mode delegated
earm permissions -f samples/orders-api.entra.json -p '@samples/orders-api.parameters.json' -o json
```

The verb is **offline** (no sign-in, no tenant access): it loads and validates the template, expands `copy` loops and
conditions with the offline context, and asks every resource provider what it needs for the resource as declared. The list is the union of

1. **the type's baseline and its conditional rules**: `IResourceProvider.RequiredPermissions(instance)` of every resource (for
   example `Application.ReadWrite.OwnedBy` instead of `.All` when the application declares `owners`, `RoleManagement.ReadWrite.Directory`
   on a group only when it declares `isAssignableToRole: true`); a type without a registered provider falls back to its schema overlay;
2. **the edge kinds** of the relationship properties the template declares (table below), including an empty array (which removes edges);
3. **the tenant lookups of expression functions**: `appRole()` / `permissionScope()` read service principals (`Application.Read.All`), `roleTemplate()` and `roleDefinitions()`
   read custom role definitions (`RoleManagement.Read.Directory`), `tenant()` reads `/organization`, and a `reference(resourceId('Microsoft.Entra/<type>', …), apiVersion)`
   existing-resource lookup needs the read variant of that type's permissions. A lookup that a write permission of the same family already covers
   (`Application.ReadWrite.All` ⊇ `Application.Read.All`) is folded away;
4. **the preflight**: every online verb reads `GET /organization?$select=id,displayName,verifiedDomains,countryLetterCode` (`Organization.Read.All` in app mode; a signed-in user reads `id`, `displayName` and `verifiedDomains` with `User.Read`, Learn: organization-get; the same read feeds `tenant()`, whose `countryCode` needs `Organization.Read.All`, which a template that calls `tenant()` lists);
5. **Key Vault parameter references** as an Azure requirement of the signed-in identity (not a Graph permission).

**Optional: the tenant size.** Right after the preflight every online run reads `GET /users/$count` once to size the pacing of its Graph requests
(engine.md, [Graph request scheduler](engine.md#graph-request-scheduler)). It needs `User.ReadBasic.All` (or a broader user or directory read such as
`User.Read.All` or `Directory.Read.All`), which is **not** a required permission: it is not in the list above, a run signed in as a user does not request
it, and the report mentions it in a note only. Without it the run keeps the smallest tenant's budget (3,500 resource units per 10 s), which can only make a
large deployment slower.

A languageVersion 2.0 `"existing": true` resource is only read: it needs the read variant of its type's permissions (reason `lookup: existing resource ("existing": true, read only)`, folded away when a write permission of the same family is listed), no edge permission and no Entra role.
and the references it contains. Resources whose `condition` evaluates to `false` offline are left out, and a count is reported in the notes.

The list covers both deployment modes and `deployment delete`: Complete mode never deletes, it only reads the declared types for its orphan report
(which the read side of the permissions above allows), and deleting the declared objects needs the same permissions as managing them. EARM keeps no
state, so there is no extra permission for reading back what an earlier run created: every run reads the tenant by key.

## Modes

| `--mode` | Credentials | Output |
|---|---|---|
| `app` (default) | a service principal, workload identity or managed identity (`AZURE_*` environment, see [cli.md](cli.md#signing-in)): Microsoft Graph **application permissions** (app roles), granted with admin consent to the service principal | One list of application permissions, each with the resources that need it and why. `Application.ReadWrite.All` subsumes `Application.ReadWrite.OwnedBy` and is listed alone, naming the resources that would manage with `OwnedBy`. |
| `delegated` | `earm auth login` as a user: **delegated scopes** on the token plus the user's **Entra roles** | The same list as delegated scopes (`OwnedBy` exists only as an application permission, so it becomes `Application.ReadWrite.All`), plus *Entra roles* groups: for each group the user needs **at least one** of the listed roles (they are alternatives), for the listed resources. A group written `together: A + (B | C)` needs its roles **together**: one role of every part at the same time (a part in parentheses lists alternatives). |

`-o json`:

```json
{
  "template": "samples/orders-api.entra.json",
  "mode": "app",
  "resourceCount": 16,
  "permissions": [
    { "name": "Application.ReadWrite.OwnedBy", "kind": "graphApplication",
      "resources": ["/providers/Microsoft.Entra/applications/contoso-prod-api"], "reasons": ["provider"] },
    { "name": "Key Vault secret read ('Key Vault Secrets User' or the get-secret access policy) on vault 'contoso-kv'", "kind": "azureRbac",
      "resources": ["parameter 'initialPassword'"], "reasons": ["GET https://contoso-kv.vault.azure.net/secrets/bg-initial"] }
  ],
  "entraRoles": [ { "anyOf": ["Application Administrator", "Cloud Application Administrator"], "resources": ["…"], "reasons": ["provider"] } ],
  "notes": ["…"]
}
```

`kind` is `graphApplication`, `graphDelegated` or `azureRbac`; `entraRoles` is filled in delegated mode only. An entry of roles needed together has an empty `anyOf` and an `allOf` array of parts, each part an array of alternatives (`{ "anyOf": [], "allOf": [["Identity Governance Administrator"], ["Privileged Role Administrator"]], … }`); entries of alternatives carry no `allOf`. `reasons` are `provider`,
`edge: <property>`, `condition: <property>`, `lookup: …` and `preflight: …`. A `what-if` also prints the sorted union of the
providers' application permissions (`Required permissions:` in the footer, `requiredPermissions` in the JSON); `permissions` is the same
computation plus the edge, lookup and role detail, without needing a session.

## Delegated scopes per run

`earm permissions … --verb whatif|create|delete` prints the delegated scopes one run of that verb requests for a signed-in user
(`RunScopes` in `EArm.Engine.Permissions`, computed offline from the template and its parameters, before any sign-in). The source is the
delegated list above, then:

| Verb | Scopes |
|---|---|
| `whatif` | every scope folded to its read variant (`X.ReadWrite.Y` → `X.Read.Y`; `AppRoleAssignment.ReadWrite.All` and `Application.ReadWrite.OwnedBy` have no such read scope and fold to `Application.Read.All`) |
| `create` | the delegated list as is |
| `delete` | write for the permissions a declared type's provider asks for; edge and lookup permissions folded to reads (deleting an object removes its edges) |
| `validate` | none (offline) |

Every set except `validate` adds `User.Read` (sign-in; the edge pass can read `GET /me`, and the preflight reads `GET /organization` with it).
`Organization.Read.All` needs admin consent and is in the set only when the template calls `tenant()`. A read scope next to its write scope (`Group.Read.All` with `Group.ReadWrite.All`) is folded into the write scope. `needsKeyVault` is true when
the template has Key Vault parameter references: the run then also needs a `https://vault.azure.net` token. `needsResourceManager` is true when the
template declares a type whose provider calls Azure Resource Manager: the run then also needs a `https://management.azure.com` token. Entra roles are not part of the scope set; they
are a property of the user, not of the token. The schema overlays carry no per-type `delegated` list: the providers' conditional rules are more
precise, and `Application.ReadWrite.OwnedBy` is the only application permission without a same-named scope.

```json
{ "template": "…", "verb": "whatif", "graphScopes": ["Application.Read.All", "…", "User.Read"], "needsKeyVault": true, "needsResourceManager": false,
  "scopes": [ { "name": "Application.Read.All", "resources": ["…"], "reasons": ["lookup: appRole()", "provider (read of Application.ReadWrite.All)"] } ] }
```

These are the scopes the online verbs **request**: a run signed in as a user (`earm auth login`, the Microsoft Graph Command Line Tools app by default) asks the
identity platform for exactly this set, never for `.default`, and the preflight checks that the token's `scp` carries all of it before anything is read or written.
A scope that has not been consented stops the run with "Consent needed" (exit 3, JSON code `ConsentRequired`), or prompts once in an interactive terminal; see
[cli.md](cli.md#consent). Consent the set ahead of time with `earm auth login -f <template> [-p …] --verb <verb>` (the same computation), and check it with
`earm auth status -f <template>`. A service principal, workload identity or managed identity keeps requesting `.default` and needs the application permissions of
`--mode app` instead.

### Admin consent

Per the Microsoft Graph permissions reference, nearly every scope EARM requests needs **admin consent**: everything except `User.Read`, including
`Organization.Read.All` (only for `tenant()`), `Policy.Read.All` and every `Policy.ReadWrite.*`, `Application.Read.All` / `Application.ReadWrite.All`,
`RoleManagement.Read.Directory` / `RoleManagement.ReadWrite.Directory`, `AppRoleAssignment.ReadWrite.All`, `DelegatedPermissionGrant.ReadWrite.All` and the
directory-wide `Group.*`, `User.*.All`, `Directory.Read.All` and `AdministrativeUnit.*` scopes. In practice:

- An administrator who may grant consent (for example a Global Administrator or Privileged Role Administrator) answers the prompt of `earm auth login` for
  themselves. Leave **Consent on behalf of your
  organization** unticked unless every user should get the grant (see [SECURITY.md](../SECURITY.md#delegated-sign-in-and-shared-consent)).
- A plain `earm auth login` requests only `User.Read`, which any user may consent to; the admin-consent scopes come with `--scopes`, `-f <template>`
  or the first run that needs them.
- Any other user gets "Need admin approval" for those scopes: an administrator grants the listed delegated permissions to the sign-in app (Enterprise applications > Microsoft Graph
  Command Line Tools > Permissions), or the tenant's admin consent workflow is used.
- A consented scope is not a role: the user still needs the Entra roles of `--mode delegated` for the objects the template manages.

## Manifest by type

Application permissions are the `permissions.application` of the type's overlay; roles are `permissions.roles`.

| Type | Application permission | Delegated | Entra role (delegated, any of) |
|---|---|---|---|
| `applications`, `…/federatedIdentityCredentials`, `…/passwordCredentials`, `…/keyCredentials` | `Application.ReadWrite.OwnedBy` (applications the principal owns: `owners` declared, or a child of an application in the template) → otherwise `Application.ReadWrite.All` | `Application.ReadWrite.All` | Application Developer (own applications), Cloud Application Administrator, Application Administrator |
| `applications/extensionProperties` | `Application.ReadWrite.OwnedBy` (a child of an application in the template) → otherwise `Application.ReadWrite.All` ([application-post-extensionproperty](https://learn.microsoft.com/en-us/graph/api/application-post-extensionproperty?view=graph-rest-1.0), [extensionproperty-delete](https://learn.microsoft.com/en-us/graph/api/extensionproperty-delete?view=graph-rest-1.0)) | `Application.ReadWrite.All` | Directory Writers, Cloud Application Administrator, Application Administrator |
| `servicePrincipals` | same; first-party (Microsoft) application ids always need `.All` | same | Cloud Application Administrator, Application Administrator |
| `groups` | `Group.ReadWrite.All` (the upsert `PATCH /groups(uniqueName=…)` lists it as the least privileged permission; `Group.Create` is only the least privileged one of the plain `POST /groups`, which EARM does not use); `GroupMember.ReadWrite.All` when `members`, `owners` or `memberOf` is declared; `AdministrativeUnit.ReadWrite.All` with `administrativeUnits`; `RoleManagement.ReadWrite.Directory` when `isAssignableToRole: true`; `LicenseAssignment.ReadWrite.All` + `LicenseAssignment.Read.All` with `assignedLicenses` | same | Groups Administrator; role-assignable: Privileged Role Administrator; licenses: License Administrator |
| `users` | `User.ReadWrite.All` plus the conditional entries below for each edge or action property declared; restoring a soft-deleted user (`--on-soft-deleted-conflict restore`) needs `User.DeleteRestore.All` (noted by `earm permissions`) | same | User Administrator (+ Groups Administrator, License Administrator, Privileged Role Administrator as used) |
| `roleDefinitions` | `RoleManagement.ReadWrite.Directory` | same | Privileged Role Administrator |
| `roleAssignments` | `RoleManagement.ReadWrite.Directory`, `Directory.Read.All` (`GET /directoryObjects/{id}` resolves the principal kind), `Group.Read.All` (the role-assignable check `GET /groups/{id}?$select=isAssignableToRole`; folded away when `Group.ReadWrite.All` is listed) | same | Privileged Role Administrator |
| `conditionalAccess/authenticationContextClassReferences` | `AuthenticationContext.ReadWrite.All` (Learn lists it as the least privileged permission for create/update/delete; `Policy.ReadWrite.ConditionalAccess` is the higher privileged alternative; the pages mention a known issue that may require consenting to both) | same | Conditional Access Administrator |
| `conditionalAccess/authenticationStrengths` | `Policy.ReadWrite.ConditionalAccess`, `Policy.Read.All` (Learn: `Policy.ReadWrite.ConditionalAccess` is the least privileged permission for create, update and delete, `Policy.ReadWrite.AuthenticationMethod` the higher privileged one) | same | Authentication Policy Administrator |
| `conditionalAccess/namedLocations`, `conditionalAccess/policies` | `Policy.ReadWrite.ConditionalAccess`, `Policy.Read.All` (+ `Application.Read.All` for policies) | same | Conditional Access Administrator, Security Administrator |
| `appRoleAssignments` | `AppRoleAssignment.ReadWrite.All`, `Application.Read.All`, `Directory.Read.All` (principal kind lookup) | same | Cloud Application Administrator, Application Administrator; granting Microsoft Graph application permissions: Privileged Role Administrator |
| `oauth2PermissionGrants` | `DelegatedPermissionGrant.ReadWrite.All`, `Application.Read.All` | same | Cloud Application Administrator, Application Administrator |
| `administrativeUnits` | `AdministrativeUnit.ReadWrite.All` | same | Privileged Role Administrator |
| `roleEligibilityScheduleRequests` | `RoleEligibilitySchedule.ReadWrite.Directory` (Learn, rbacapplication-post-roleeligibilityschedulerequests: the least privileged permission, delegated and application; the schedule reads need `RoleEligibilitySchedule.Read.Directory`, the `whatif` scope), `Directory.Read.All` (principal kind; role and administrative unit names), `Group.Read.All` (the role-assignable check; folded away when `Group.ReadWrite.All` is listed) | same | Privileged Role Administrator (Global Reader suffices for a delegated what-if). The tenant needs Microsoft Entra ID P2 or ID Governance |
| `roleAssignmentScheduleRequests` | `RoleAssignmentSchedule.ReadWrite.Directory` (Learn, rbacapplication-post-roleassignmentschedulerequests: the least privileged permission, delegated and application; the schedule reads need `RoleAssignmentSchedule.Read.Directory` (rbacapplication-list-roleassignmentschedules), the `whatif` scope), `Directory.Read.All` (principal kind; role and administrative unit names), `Group.Read.All` (the role-assignable check; folded away when `Group.ReadWrite.All` is listed) | same | Privileged Role Administrator (Global Reader, Security Reader or Security Operator suffice for a delegated what-if). The tenant needs Microsoft Entra ID P2 or ID Governance |
| `privilegedAccess/groupEligibilityScheduleRequests` | `PrivilegedEligibilitySchedule.ReadWrite.AzureADGroup` (Learn, privilegedaccessgroup-post-eligibilityschedulerequests: the least privileged permission, delegated and application; the schedule reads need `PrivilegedEligibilitySchedule.Read.AzureADGroup`, the `whatif` scope), `Directory.Read.All` (principal kind; the `group` uniqueName lookup), `Group.Read.All` (the target group kind check: dynamic, on-premises synced; folded away when `Group.ReadWrite.All` is listed) | same | The role must be **active**, not just eligible. A target group that is not role-assignable: Groups Administrator, Identity Governance Administrator, User Administrator or Directory Writers (directory scope), or an owner of the group; Privileged Role Administrator alone does **not** cover it. A role-assignable target group: Privileged Role Administrator (or an owner). Without one of them Graph answers 403 `UnauthorizedAccessException` (`PimGroupAccessDenied`). The manifest narrows the roles by the target group: Privileged Role Administrator for a role-assignable template group that `groupId` references, Identity Governance Administrator, Groups Administrator, User Administrator or Directory Writers for any other template group, and all five as alternatives when the group is not declared in the template (an existing group id, or `group`). The first request onboards the group in PIM for Groups permanently. The tenant needs Microsoft Entra ID P2 or ID Governance |
| `privilegedAccess/groupAssignmentScheduleRequests` | `PrivilegedAssignmentSchedule.ReadWrite.AzureADGroup` (Learn, privilegedaccessgroup-post-assignmentschedulerequests: the least privileged permission, delegated and application; the schedule reads need `PrivilegedAssignmentSchedule.Read.AzureADGroup` (privilegedaccessgroup-list-assignmentschedules), the `whatif` scope), `Directory.Read.All` (principal kind; the `group` uniqueName lookup), `Group.Read.All` (the target group kind check; folded away when `Group.ReadWrite.All` is listed) | same | The role must be **active**, not just eligible. A target group that is not role-assignable: Groups Administrator, Identity Governance Administrator, User Administrator or Directory Writers (directory scope), or an owner of the group; Privileged Role Administrator alone does **not** cover it. A role-assignable target group: Privileged Role Administrator (or an owner). Without one of them Graph answers 403 `UnauthorizedAccessException` (`PimGroupAccessDenied`). The manifest narrows the roles by the target group: Privileged Role Administrator for a role-assignable template group that `groupId` references, Identity Governance Administrator, Groups Administrator, User Administrator or Directory Writers for any other template group, and all five as alternatives when the group is not declared in the template (an existing group id, or `group`). The first request onboards the group in PIM for Groups permanently. The tenant needs Microsoft Entra ID P2 or ID Governance |
| `roleManagementPolicies` | `RoleManagementPolicy.ReadWrite.Directory` for a directory role policy, `RoleManagementPolicy.ReadWrite.AzureADGroup` for a PIM for Groups policy (Learn, unifiedrolemanagementpolicyrule-update: the least privileged permissions, delegated and application; the assignment and rule reads need `RoleManagementPolicy.Read.Directory` / `RoleManagementPolicy.Read.AzureADGroup` (policyroot-list-rolemanagementpolicyassignments), the `whatif` scopes; only the variant of the declared scope is asked for, both when the scope is computed), `Directory.Read.All` (role name and group lookups) | same | Privileged Role Administrator (directory role policies; Learn lists no role for group policies, where Privileged Role Administrator or the group's owner is what the portal accepts). A group policy update onboards the group in PIM for Groups permanently; needs Microsoft Entra ID P2 or ID Governance. A group policy that declares `onboarding` also needs `PrivilegedEligibilitySchedule.ReadWrite.AzureADGroup` (the temporary eligibility that onboards the group, [pim.md](pim.md#onboarding-opt-in)) and a role that writes the group's eligibilities (Privileged Role Administrator, Identity Governance Administrator or Groups Administrator; Privileged Role Administrator alone does not cover a group that is not role-assignable); the manifest adds both only for such a policy (`RequiredPermissions` and the overlay's `permissions.conditional.onboarding`) and narrows the onboarding roles by the group, as for the PIM for Groups requests: Privileged Role Administrator for a role-assignable template group that `group` or `scopeId` references, Identity Governance Administrator, Groups Administrator, User Administrator or Directory Writers for any other template group, and all five as alternatives when the group is not declared in the template |
| `azureRoleManagementPolicies` | none: Azure Resource Manager, not Graph. Listed under **Azure** as `Microsoft.Authorization/roleManagementPolicies/write` at the declared scope (a what-if reads with `.../read`); the run requests a separate Resource Manager token (`needsResourceManager`) | none | none (Entra); an Azure RBAC role at the scope: Owner, User Access Administrator or Role Based Access Control Administrator (Reader for a what-if) |
| `azureRoleEligibilityScheduleRequests` | none: Azure Resource Manager, not Graph. Listed under **Azure** as `Microsoft.Authorization/roleEligibilityScheduleRequests/write` at the declared scope (a what-if reads `roleEligibilitySchedules` with `.../read`); the run requests a separate Resource Manager token (`needsResourceManager`) | none | none (Entra); an Azure RBAC role at the scope: Owner or User Access Administrator (Reader for a what-if); PIM for Azure resources needs Microsoft Entra ID P2 or ID Governance |
| `azureRoleAssignments` | none: Azure Resource Manager, not Graph. Listed under **Azure** as `Microsoft.Authorization/roleAssignments/write` (and `.../delete` for `deployment delete`) at the declared scope (a what-if reads with `.../read`); the run requests a separate Resource Manager token (`needsResourceManager`) | none | none (Entra); an Azure RBAC role at the scope: Owner, User Access Administrator or Role Based Access Control Administrator (Reader for a what-if) |
| `authenticationMethodConfigurations` | `Policy.ReadWrite.AuthenticationMethod` (Learn, the method pages' update and delete: the least privileged permission, delegated and application; reads need `Policy.Read.AuthenticationMethod`, the `whatif` scope) | same | Authentication Policy Administrator (Global Reader suffices for a delegated what-if) |
| `groupLifecyclePolicies` | `Directory.ReadWrite.All` (Learn, grouplifecyclepolicy-post-grouplifecyclepolicies, -update, -delete: the only and least privileged write permission, delegated and application; reads need `Directory.Read.All`, the `whatif` scope) | same | Groups Administrator or User Administrator (Learn, Entra groups-lifecycle). Expiration needs Microsoft Entra ID P1 (or higher) licences for the members of the governed groups |
| `policies/tokenLifetimePolicies`, `policies/claimsMappingPolicies`, `policies/appManagementPolicies`, `policies/defaultAppManagementPolicy` | `Policy.ReadWrite.ApplicationConfiguration` (Learn: the create, update and delete pages of each policy and tenantappmanagementpolicy-update list it as the least privileged write permission, delegated and application), `Policy.Read.All` (the list and get pages: the least privileged read permission, which is also the `whatif` scope) | same | Cloud Application Administrator or Application Administrator (Learn names them for app management policies; the token lifetime and claims mapping pages name no role). The Entra app management how-to also asks for Security Administrator |
| `accessReviews/definitions` | `AccessReview.ReadWrite.All` (Learn: [accessreviewset-post-definitions](https://learn.microsoft.com/en-us/graph/api/accessreviewset-post-definitions?view=graph-rest-1.0), [accessreviewscheduledefinition-update](https://learn.microsoft.com/en-us/graph/api/accessreviewscheduledefinition-update?view=graph-rest-1.0) and [-delete](https://learn.microsoft.com/en-us/graph/api/accessreviewscheduledefinition-delete?view=graph-rest-1.0): the least privileged and only write permission), `AccessReview.Read.All` for reads and the `whatif` scope ([accessreviewset-list-definitions](https://learn.microsoft.com/en-us/graph/api/accessreviewset-list-definitions?view=graph-rest-1.0)) | same | User Administrator or Identity Governance Administrator (reviews of groups, applications and access packages); Identity Governance Administrator or Privileged Role Administrator for reviews of Microsoft Entra roles. Reviews need Microsoft Entra ID Governance (or P2) licensing |
| `policies/crossTenantAccessPolicy`, `policies/crossTenantAccessPolicy/partners` | `Policy.ReadWrite.CrossTenantAccess` (Learn: the default update and reset and the partner create, update and delete pages), `Policy.Read.All` (reads and the `whatif` scope) | same | Security Administrator (Teams Administrator also, except identity synchronization settings); Security Reader or Global Reader for a delegated what-if |
| `entitlementManagement/catalogs`, `entitlementManagement/accessPackages`, `entitlementManagement/resourceRoleScopes`, `entitlementManagement/accessPackages/assignmentPolicies` | `EntitlementManagement.ReadWrite.All` (Learn: every entitlement management API page lists it as the least privileged permission for writes, delegated and application; reads need `EntitlementManagement.Read.All`, the `whatif` scope) | same | Identity Governance Administrator, or the catalog roles: catalog creator (a new catalog), catalog owner, access package manager (packages, role scopes, policies). The tenant needs Microsoft Entra ID P2 or ID Governance; eligible group roles need ID Governance ([access-packages.md](access-packages.md#permissions)). A policy's `reviewSettings` (access reviews) need no other scope or role; they need ID Governance for the member users who review or are reviewed (P2 still covers the review capabilities it had; Learn, licensing-fundamentals), and a licence refusal is `GovernanceLicenseRequired` ([access reviews](access-packages.md#access-reviews)) |
| `entitlementManagement/catalogResources` | `EntitlementManagement.ReadWrite.All` and `Group.ReadWrite.All` (Learn, entitlementmanagement-post-resourcerequests: application callers need both; a role-assignable group needs `RoleManagement.ReadWrite.Directory`: listed for a role-assignable template group, and with the reason "when the group is role-assignable" for a group the template does not declare) | `EntitlementManagement.ReadWrite.All`, `Group.Read.All` | Identity Governance Administrator or catalog owner **and** an owner of the group or a role that changes its members; a **role-assignable** group: Global Administrator or Privileged Role Administrator (or an owner of the group). The manifest lists them as roles needed **together** (the overlay's `rolesAllOf`): Identity Governance Administrator + Privileged Role Administrator for a role-assignable template group, Identity Governance Administrator + (Groups Administrator \| Intune Administrator \| User Administrator) for any other template group (Learn, entitlement-management-delegate), and every alternative of the second part when the group is not declared in the template. **Application** (`originSystem: AadApplication`): `Application.ReadWrite.All` instead of `Group.ReadWrite.All`; Identity Governance Administrator alone suffices (a catalog owner must also own the app or be an Application Administrator / Cloud Application Administrator). **SharePoint Online site** (`SharePointOnline`): `Sites.FullControl.All` instead of `Group.ReadWrite.All`; Identity Governance Administrator + SharePoint Administrator (or an administrator of the site); Graph lists sites in a catalog only to delegated callers. These come from the overlay's `rolesAllOfByValue` (keyed by the declared or offline-evaluated `originSystem`, replacing `rolesAllOf`); a still-computed `originSystem` keeps the group roles and asks for all three permissions (Learn, entitlementmanagement-post-resourcerequests, entitlement-management-delegate). Graph answers 403 otherwise (`CatalogResourceAccessDenied`) |
| deleted-item restore (`--on-soft-deleted-conflict restore`) | users `User.DeleteRestore.All`; groups `Group.ReadWrite.All`; administrative units `AdministrativeUnit.ReadWrite.All`; applications and service principals `Application.ReadWrite.OwnedBy` (Learn: directory-deleteditems-restore) | same | per type |
| the soft-delete probe before a create (`GET /directory/deletedItems/microsoft.graph.<type>`) | the type's read permission: applications and service principals `Application.Read.All`, groups `Group.Read.All`, users `User.Read.All`, administrative units `AdministrativeUnit.Read.All` (Learn: directory-deleteditems-list). An identity that lacks it (an `Application.ReadWrite.OwnedBy`-only service principal) gets a 403: the probe is **skipped** with the warning `SoftDeleteProbeSkipped` and the create goes on | same | per type |

Conditional entries of `users` (overlay `permissions.conditional`): `memberOf` → `GroupMember.ReadWrite.All`, `Directory.Read.All` (Groups Administrator);
`administrativeUnits` → `AdministrativeUnit.ReadWrite.All` (Privileged Role Administrator); `assignedLicenses` → `LicenseAssignment.ReadWrite.All`,
`LicenseAssignment.Read.All` (License Administrator); `appRoleAssignments` → `AppRoleAssignment.ReadWrite.All`, `Application.Read.All`,
`Directory.Read.All`; `directoryRoles` → `RoleManagement.ReadWrite.Directory`, `Directory.Read.All` (Privileged Role Administrator).

Conditional entry of `roleManagementPolicies`: `onboarding` → `PrivilegedEligibilitySchedule.ReadWrite.AzureADGroup` (from the provider's
`RequiredPermissions`) and one of Privileged Role Administrator, Identity Governance Administrator or Groups Administrator.

## Edge kinds

| Edge kind (property) | Application permissions | Entra role (any of) |
|---|---|---|
| Group membership (`members`, `memberOf`) | `GroupMember.ReadWrite.All`, `Directory.Read.All` (principal kinds are resolved with `GET /directoryObjects/{id}`; `memberOf` of a user is read with `GET /users/{id}/memberOf`, which needs `Directory.Read.All` with an application token); `members` also lists `Application.ReadWrite.All` because a service principal member needs it in addition (Learn: group-post-members) | Groups Administrator |
| Ownership (`owners`) | `Directory.Read.All`, plus the target type's permission: applications and service principals `Application.ReadWrite.OwnedBy` / `.All`, groups `Group.ReadWrite.All` / `GroupMember.ReadWrite.All` (Learn: application-post-owners lists `Application.ReadWrite.OwnedBy` AND `Directory.Read.All`, and notes that `OwnedBy` is not sufficient to add another owner: grant `Application.ReadWrite.All` when the owners include anyone besides the owning principal; `earm permissions` prints that note) | the target type's roles |
| Ownership (`ownerOf`) | `Application.ReadWrite.All` when a target is an application or service principal, `Group.ReadWrite.All` when it is a group (the target's kind is known only at deployment, so both are listed). **`ownerOf` on a user needs a signed-in (delegated) token**: reading `GET /users/{id}/ownedObjects` is not supported with application permissions (Learn: user-list-ownedobjects), and `earm permissions` says so | the target type's roles |
| Administrative unit membership (`members` of an AU, `administrativeUnits`) | `AdministrativeUnit.ReadWrite.All` | Privileged Role Administrator |
| Manager (`manager`) | `User.ReadWrite.All` (the write, `PUT /users/{id}/manager/$ref`); the read `GET /users/{id}/manager` has no application permission in Learn (user-list-manager) | User Administrator |
| App role assignment (`appRoleAssignments`) | `AppRoleAssignment.ReadWrite.All`, `Application.Read.All`, `Directory.Read.All` | Cloud Application Administrator, Application Administrator |
| Directory role assignment (`directoryRoles`) | `RoleManagement.ReadWrite.Directory`, `Directory.Read.All` (principal kind; a group principal additionally `Group.Read.All` for `isAssignableToRole`) | Privileged Role Administrator |
| License (`assignedLicenses`) | `LicenseAssignment.ReadWrite.All` (assignLicense) and `LicenseAssignment.Read.All` (`GET /subscribedSkus` for `skuPartNumber`); `Organization.Read.All` is **not** needed (Learn: user-assignlicense, group-assignlicense, subscribedsku-list) | License Administrator |
| Group lifecycle policy (`groupLifecyclePolicies`) | `Directory.ReadWrite.All` (Learn: grouplifecyclepolicy-addgroup, group-list-grouplifecyclepolicies) | Groups Administrator, User Administrator |
| Token lifetime policy (`tokenLifetimePolicies`), claims mapping policy (`claimsMappingPolicies`) | `Application.ReadWrite.All` (`Application.ReadWrite.OwnedBy` for owned objects, app-only) and `Policy.Read.All`; `Policy.ReadWrite.ApplicationConfiguration` is **not** needed (Learn: application-post-tokenlifetimepolicies, serviceprincipal-post-claimsmappingpolicies and their list / delete pages; even the list pages ask for a write permission) | Cloud Application Administrator, Application Administrator (Learn names none) |
| App management policy (`appManagementPolicies`) | `Policy.ReadWrite.ApplicationConfiguration` and `Application.Read.All` (Learn: appmanagementpolicy-post-appliesto, -delete-appliesto) | Cloud Application Administrator, Application Administrator |

## Things to know

- **Application and delegated permissions are different grants.** A user sign-in (`earm auth login`) uses delegated scopes, consented to the sign-in app
  (Microsoft Graph Command Line Tools by default, no app registration), and requests per run only the scopes of its verb ([above](#delegated-scopes-per-run)).
  A service principal or managed identity uses application permissions granted with admin consent. Your own public-client app (`--client-id`, stored in the
  sign-in record) is only an override for Azure operated by 21Vianet and for tenants that block the Microsoft app; see [cli.md](cli.md#signing-in). The delegated
  column is derived from the application permissions and is verified against a tenant by the integration tests
  (see [tests/EArm.Integration.Tests/README.md](../tests/EArm.Integration.Tests/README.md)), not by this verb.
- **`RoleManagement.ReadWrite.Directory` and `AppRoleAssignment.ReadWrite.All` are privilege-escalation permissions.** An application-only
  caller holding either can grant itself more (any directory role, any Graph application permission). Treat the service principal that deploys
  `roleAssignments`, `directoryRoles` or `appRoleAssignments` as highly privileged; prefer a delegated, role-holding user for those.
- **The PIM and authentication-method permissions are privilege-control surfaces.** `RoleEligibilitySchedule.ReadWrite.Directory`,
  `RoleAssignmentSchedule.ReadWrite.Directory` and `PrivilegedEligibilitySchedule` / `PrivilegedAssignmentSchedule.ReadWrite.AzureADGroup` let the caller make
  any principal eligible for (or active in) any role or PIM-governed group, itself included; `RoleManagementPolicy.ReadWrite.Directory` / `.AzureADGroup`
  can remove the approval, MFA and duration limits that make eligibility safe; `Policy.ReadWrite.AuthenticationMethod` changes how every user signs in.
  Treat an identity holding them like one holding `RoleManagement.ReadWrite.Directory` ([SECURITY.md](../SECURITY.md#privileged-access-and-authentication-methods)).
- **Role-assignable groups** need `RoleManagement.ReadWrite.Directory` and a Privileged Role Administrator, are immutable in that respect, and
  are limited to 500 per tenant.
- **`Application.ReadWrite.OwnedBy`** only manages applications the caller owns; the template makes the caller the owner by declaring `owners`
  (the signed-in principal is warned about when a declared set would remove it, `SignedInPrincipalRemoved`).
- **Soft-deleted objects** (applications, Microsoft 365 and security groups, users, service principals, administrative units; only distribution lists are deleted immediately, [group-delete](https://learn.microsoft.com/en-us/graph/api/group-delete)) are restored with the permission of
  the "deleted-item restore" row above when `--on-soft-deleted-conflict restore` is used; the probe that finds them needs the type's read permission and is skipped
  with a `SoftDeleteProbeSkipped` warning when the identity lacks it.
- **Key Vault.** A parameter bound to a Key Vault secret needs the signed-in identity to read that secret: the Azure RBAC role `Key Vault Secrets
  User` (data plane) on the vault, or an access policy with `get` on secrets. It is listed as `azureRbac` and is independent of Graph. A parameter of
  a nested deployment is listed as `<deployment>/<parameter>`.
- A `403 Authorization_RequestDenied` on a resource fails that resource (independent branches continue only with `--continue-on-error`); the
  manifest row of its type above is what to grant.

## Review 1D corrections (what changed and why)

Checked against Microsoft Learn on 2026-10-14 (Microsoft Graph v1.0 pages):

- `Application.ReadWrite.All` is `1bfefb4e-e0b5-418b-a88f-73c46d2cc8e9` (the embedded table carried a wrong GUID; every embedded Graph permission id is now pinned by a test
  against the [permissions reference](https://learn.microsoft.com/en-us/graph/permissions-reference)). `appRole()` / `permissionScope()` prefer the tenant's answer and use the table only offline.
- Adding an owner: [application-post-owners](https://learn.microsoft.com/en-us/graph/api/application-post-owners) and
  [serviceprincipal-post-owners](https://learn.microsoft.com/en-us/graph/api/serviceprincipal-post-owners) list `Application.ReadWrite.OwnedBy` **and** `Directory.Read.All`.
- `GET /directoryObjects/{id}`: [directoryobject-get](https://learn.microsoft.com/en-us/graph/api/directoryobject-get) lists `Directory.Read.All` only.
  `GET /groups/{id}` ([group-get](https://learn.microsoft.com/en-us/graph/api/group-get)) accepts `Group.Read.All`.
- `GET /users/{id}/memberOf` of another user, application: `Directory.Read.All` ([user-list-memberof](https://learn.microsoft.com/en-us/graph/api/user-list-memberof)).
  `ownedObjects`: application permissions are "Not supported" ([user-list-ownedobjects](https://learn.microsoft.com/en-us/graph/api/user-list-ownedobjects)).
- Restore: `User.DeleteRestore.All` for users, `Application.ReadWrite.OwnedBy` for applications and service principals
  ([directory-deleteditems-restore](https://learn.microsoft.com/en-us/graph/api/directory-deleteditems-restore)); the list
  ([directory-deleteditems-list](https://learn.microsoft.com/en-us/graph/api/directory-deleteditems-list)) needs the type's read permission.
- A service principal as group member: `GroupMember.ReadWrite.All` and `Application.ReadWrite.All` ([group-post-members](https://learn.microsoft.com/en-us/graph/api/group-post-members)).
- Group create/upsert: `Group.ReadWrite.All` for the upsert (`PATCH /groups(uniqueName=…)`, [group-upsert](https://learn.microsoft.com/en-us/graph/api/group-upsert));
  `Group.Create` is the least privileged permission of `POST /groups` only ([group-post-groups](https://learn.microsoft.com/en-us/graph/api/group-post-groups)), which needs read permissions for the owner and member kinds as well.
  Both pages say a group created with an application token and no owners is created anonymously: EARM binds declared `owners` into the create body (`owners@odata.bind`) and warns (`GroupHasNoOwnerAtCreate`) for a Microsoft 365 group without owners.
- Authentication strengths: `Policy.ReadWrite.ConditionalAccess` is the least privileged write permission (authenticationstrengthroot-post-policies, authenticationstrengthpolicy-update);
  `Policy.ReadWrite.AuthenticationMethod` is the higher privileged one. The `updateAllowedCombinations` action has no page that was checked.
- Authentication contexts: `AuthenticationContext.ReadWrite.All` (authenticationcontextclassreference-update / -delete).
- Licenses: `LicenseAssignment.ReadWrite.All` (user-assignlicense, group-assignlicense) and `LicenseAssignment.Read.All` (subscribedsku-list).