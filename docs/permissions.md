# Permissions

What the identity behind `az login` needs for a template, and how to get the exact list:

```powershell
entraarm permissions -f samples/orders-api.entra.json -p @samples/orders-api.parameters.json              # app permissions (default)
entraarm permissions -f samples/orders-api.entra.json -p @samples/orders-api.parameters.json --mode delegated
entraarm permissions -f samples/orders-api.entra.json -p @samples/orders-api.parameters.json -o json
```

The verb is **offline** (no Azure CLI session, no tenant access): it loads and validates the template, expands `copy` loops and
conditions with the offline context, and asks every resource provider what it needs for the resource as declared. The list is the union of

1. **the type's baseline and its conditional rules**: `IResourceProvider.RequiredPermissions(instance)` of every resource (for
   example `Application.ReadWrite.OwnedBy` instead of `.All` when the application declares `owners`, `RoleManagement.ReadWrite.Directory`
   on a group only when it declares `isAssignableToRole: true`); a type without a registered provider falls back to its schema overlay;
2. **the edge kinds** of the relationship properties the template declares (table below), including an empty array (which removes edges);
3. **the tenant lookups of expression functions**: `appRole()` / `permissionScope()` read service principals (`Application.Read.All`), `roleTemplate()`
   reads custom role definitions (`RoleManagement.Read.Directory`), `tenant()` reads `/organization`, and a `reference(resourceId('Microsoft.Entra/<type>', …), apiVersion)`
   existing-resource lookup needs the read variant of that type's permissions. A lookup that a write permission of the same family already covers
   (`Application.ReadWrite.All` ⊇ `Application.Read.All`) is folded away;
4. **the preflight**: every online verb reads `GET /organization?$select=id` (`Organization.Read.All`, app mode);
5. **Key Vault parameter references** as an Azure requirement of the Azure CLI identity (not a Graph permission).

Resources whose name depends on another resource (a service principal named `[reference('app').appId]`) are computed from their declaration
and the references it contains. Resources whose `condition` evaluates to `false` offline are left out, and a count is reported in the notes.

## Modes

| `--mode` | Credentials | Output |
|---|---|---|
| `app` (default) | `az login --service-principal`: Microsoft Graph **application permissions** (app roles), granted with admin consent to the service principal | One list of application permissions, each with the resources that need it and why. `Application.ReadWrite.All` subsumes `Application.ReadWrite.OwnedBy` and is listed alone, naming the resources that would manage with `OwnedBy`. |
| `delegated` | `az login` as a user: **delegated scopes** on the token plus the user's **Entra roles** | The same list as delegated scopes (`OwnedBy` exists only as an application permission, so it becomes `Application.ReadWrite.All`), plus *Entra roles* groups: for each group the user needs **at least one** of the listed roles (they are alternatives), for the listed resources. |

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
      "resources": ["parameter 'initialPassword'"], "reasons": ["az keyvault secret show --vault-name contoso-kv --name bg-initial"] }
  ],
  "entraRoles": [ { "anyOf": ["Application Administrator", "Cloud Application Administrator"], "resources": ["…"], "reasons": ["provider"] } ],
  "notes": ["…"]
}
```

`kind` is `graphApplication`, `graphDelegated` or `azureRbac`; `entraRoles` is filled in delegated mode only. `reasons` are `provider`,
`edge: <property>`, `condition: <property>`, `lookup: …` and `preflight: …`. A `what-if` also prints the sorted union of the
providers' application permissions (`Required permissions:` in the footer, `requiredPermissions` in the JSON); `permissions` is the same
computation plus the edge, lookup and role detail, without needing a session.

## Manifest by type

Application permissions are the `permissions.application` of the type's overlay; roles are `permissions.roles`.

| Type | Application permission | Delegated | Entra role (delegated, any of) |
|---|---|---|---|
| `applications`, `…/federatedIdentityCredentials`, `…/passwordCredentials` | `Application.ReadWrite.OwnedBy` (applications the principal owns: `owners` declared, or a child of an application in the template) → otherwise `Application.ReadWrite.All` | `Application.ReadWrite.All` | Application Developer (own applications), Cloud Application Administrator, Application Administrator |
| `servicePrincipals` | same; first-party (Microsoft) application ids always need `.All` | same | Cloud Application Administrator, Application Administrator |
| `groups` | `Group.ReadWrite.All`; `GroupMember.ReadWrite.All` when `members`, `owners` or `memberOf` is declared; `AdministrativeUnit.ReadWrite.All` with `administrativeUnits`; `RoleManagement.ReadWrite.Directory` when `isAssignableToRole: true`; `Organization.Read.All` with `assignedLicenses` | same | Groups Administrator; role-assignable: Privileged Role Administrator; licenses: License Administrator |
| `users` | `User.ReadWrite.All` plus the conditional entries below for each edge or action property declared | same | User Administrator (+ Groups Administrator, License Administrator, Privileged Role Administrator as used) |
| `roleDefinitions` | `RoleManagement.ReadWrite.Directory` | same | Privileged Role Administrator |
| `roleAssignments` | `RoleManagement.ReadWrite.Directory` | same | Privileged Role Administrator |
| `conditionalAccess/authenticationContextClassReferences` | `Policy.ReadWrite.ConditionalAccess` | same | Conditional Access Administrator |
| `conditionalAccess/authenticationStrengths` | `Policy.ReadWrite.AuthenticationMethod`, `Policy.Read.All` | same | Authentication Policy Administrator |
| `conditionalAccess/namedLocations`, `conditionalAccess/policies` | `Policy.ReadWrite.ConditionalAccess`, `Policy.Read.All` (+ `Application.Read.All` for policies) | same | Conditional Access Administrator, Security Administrator |
| `appRoleAssignments` | `AppRoleAssignment.ReadWrite.All`, `Application.Read.All` | same | Cloud Application Administrator, Application Administrator; granting Microsoft Graph application permissions: Privileged Role Administrator |
| `oauth2PermissionGrants` | `DelegatedPermissionGrant.ReadWrite.All`, `Application.Read.All` | same | Cloud Application Administrator, Application Administrator |
| `administrativeUnits` | `AdministrativeUnit.ReadWrite.All` | same | Privileged Role Administrator |
| deleted-item restore and purge (`--on-soft-deleted-conflict restore`) | the type's `*.ReadWrite.All` | same | per type |

Conditional entries of `users` (overlay `permissions.conditional`): `memberOf` → `GroupMember.ReadWrite.All` (Groups Administrator);
`administrativeUnits` → `AdministrativeUnit.ReadWrite.All` (Privileged Role Administrator); `assignedLicenses` → `Organization.Read.All`
(License Administrator); `appRoleAssignments` → `AppRoleAssignment.ReadWrite.All`, `Application.Read.All`; `directoryRoles` →
`RoleManagement.ReadWrite.Directory` (Privileged Role Administrator).

## Edge kinds

| Edge kind (property) | Application permissions | Entra role (any of) |
|---|---|---|
| Group membership (`members`, `memberOf`) | `GroupMember.ReadWrite.All` | Groups Administrator |
| Ownership (`owners`) | the target type's permission: applications and service principals `Application.ReadWrite.OwnedBy` / `.All`, groups `GroupMember.ReadWrite.All` | the target type's roles |
| Ownership (`ownerOf`) | `Application.ReadWrite.All` when a target is an application or service principal, `Group.ReadWrite.All` when it is a group (the target's kind is known only at deployment, so both are listed) | the target type's roles |
| Administrative unit membership (`members` of an AU, `administrativeUnits`) | `AdministrativeUnit.ReadWrite.All` | Privileged Role Administrator |
| Manager (`manager`) | `User.ReadWrite.All` | User Administrator |
| App role assignment (`appRoleAssignments`) | `AppRoleAssignment.ReadWrite.All`, `Application.Read.All` | Cloud Application Administrator, Application Administrator |
| Directory role assignment (`directoryRoles`) | `RoleManagement.ReadWrite.Directory` | Privileged Role Administrator |
| License (`assignedLicenses`) | `Organization.Read.All` (+ the type's own `User.ReadWrite.All` / `Group.ReadWrite.All`) | License Administrator |

## Things to know

- **Application and delegated permissions are different grants.** The Azure CLI's own first-party token does not carry every
  granular Graph scope (Conditional Access policies in particular); for the delegated mode, use an `az login` whose token includes the listed
  scopes. The delegated column is derived from the application permissions and is verified against a tenant by the integration tests
  (work item WI-19), not by this verb.
- **`RoleManagement.ReadWrite.Directory` and `AppRoleAssignment.ReadWrite.All` are privilege-escalation permissions.** An application-only
  caller holding either can grant itself more (any directory role, any Graph application permission). Treat the service principal that deploys
  `roleAssignments`, `directoryRoles` or `appRoleAssignments` as highly privileged; prefer a delegated, role-holding user for those.
- **Role-assignable groups** need `RoleManagement.ReadWrite.Directory` and a Privileged Role Administrator, are immutable in that respect, and
  are limited to 500 per tenant.
- **`Application.ReadWrite.OwnedBy`** only manages applications the caller owns; the template makes the caller the owner by declaring `owners`
  (the signed-in principal is warned about when a declared set would remove it, `SignedInPrincipalRemoved`).
- **Soft-deleted objects** (applications, Microsoft 365 groups, users, service principals, administrative units) are restored with the type's
  `ReadWrite` permission when `--on-soft-deleted-conflict restore` is used.
- **Key Vault.** A parameter bound to a Key Vault secret needs the Azure CLI identity to read that secret: the Azure RBAC role `Key Vault Secrets
  User` (data plane) on the vault, or an access policy with `get` on secrets. It is listed as `azureRbac` and is independent of Graph.
- A `403 Authorization_RequestDenied` on a resource fails that resource (independent branches continue only with `--continue-on-error`); the
  manifest row of its type above is what to grant.
