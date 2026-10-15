# Samples

Every sample validates offline with no diagnostics when its parameter file is supplied (`earm deployment validate`), and `earm permissions` lists
what each needs. Deploying one needs a sign-in (`earm auth login`, or the `AZURE_*` variables of a service principal; see [docs/cli.md](../docs/cli.md#signing-in)) with those permissions; `orders-api` additionally needs read access
to a Key Vault secret unless you replace its parameter file's `reference` entry.

| Sample | Shows | Parameters |
|---|---|---|
| [`orders-api.entra.json`](orders-api.entra.json) | The full tour: every v1 resource type in one template with nested children (`copy`ed federated credentials, a client secret), edges from both sides (`members`, `memberOf`, `ownerOf`, `manager`, `administrativeUnits`, `appRoleAssignments`, `directoryRoles`, `assignedLicenses`), a custom role, authentication strength and context, a named location built with a lambda, a conditional Conditional Access policy, standalone assignments and grants, Entra expression functions, user functions and secure outputs | [`orders-api.parameters.json`](orders-api.parameters.json) (a Key Vault reference for the initial password) |
| [`orders-api.flat.entra.json`](orders-api.flat.entra.json) | The same template with the children written as top-level resources with `parent/child` names instead of nested; it loads to the identical model | the same file |
| [`app-with-sp-and-secret.entra.json`](app-with-sp-and-secret.entra.json) | An application with `web`, `api` scopes, `appRoles` and `requiredResourceAccess`, its service principal (named after the application's `appId`), a GitHub Actions federated credential and a rotating client secret; outputs `appId`, the service principal's object id, the tenant id and the secret as a `securestring` | [`app-with-sp-and-secret.parameters.json`](app-with-sp-and-secret.parameters.json) |
| [`tenant-security.entra.json`](tenant-security.entra.json) | A tenant security baseline: IP and country named locations, a phishing-resistant authentication strength, an authentication context, two report-only Conditional Access policies (with a break-glass exclusion), a role-assignable admin group holding built-in, custom and administrative-unit-scoped directory roles, a custom role definition and an administrative unit | [`tenant-security.parameters.json`](tenant-security.parameters.json) |

## Try them

```powershell
# offline
earm deployment validate -f samples/app-with-sp-and-secret.entra.json -p '@samples/app-with-sp-and-secret.parameters.json'
earm permissions         -f samples/tenant-security.entra.json        -p '@samples/tenant-security.parameters.json' --mode delegated

# against a dev tenant (sign in first: earm auth login)
earm deployment what-if -f samples/app-with-sp-and-secret.entra.json -p '@samples/app-with-sp-and-secret.parameters.json'
earm deployment create  -f samples/app-with-sp-and-secret.entra.json -p '@samples/app-with-sp-and-secret.parameters.json' -c --secure-outputs-file ./secure-outputs.json
```

Notes:

- The names and domains (`contoso`, `bg-orders@contoso.com`, `203.0.113.0/24`) are placeholders: change the prefixes and domains before you deploy. A
  rerun with the same names updates the same objects; `-n` chooses the state file name (default: the template's file name).
- `orders-api.parameters.json` references a Key Vault secret (`contoso-kv` / `bg-initial`). Offline validation never reads it; for a real run replace
  it with `{ "value": "…" }` through `-p initialPassword=…` or point it at a vault you can read.
- Conditional Access policies in both samples are `enabledForReportingButNotEnforced`: report-only. Switching one to `enabled` needs `--allow-ca-enforce`.
  Keep an emergency-access exclusion (the `tenant-security` sample has one) before enforcing anything.
- `tenant-security.entra.json` needs Entra ID P1 and, for the role-assignable group and the directory roles, a Privileged Role Administrator
  (or an application with `RoleManagement.ReadWrite.Directory`).
- `app-with-sp-and-secret.entra.json` returns the client secret exactly once, in the run that creates or rotates it; rerunning without changing `secretTag`
  prints that output as `null` with a warning. Change `secretTag` to rotate.
