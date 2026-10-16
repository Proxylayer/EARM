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
| [`authentication-methods.entra.json`](authentication-methods.entra.json) | The authentication methods policy (tenant singletons, updated and never created): passkeys limited to an AAGUID allow-list through the default passkey profile, one-time Temporary Access Passes for a helpdesk group, certificate-based authentication bound as multi-factor while the live binding rules are kept, and SMS, voice and email one-time passcodes switched off | [`authentication-methods.parameters.json`](authentication-methods.parameters.json) (the allowed AAGUIDs and the helpdesk group id) |
| [`pim-eligibility.entra.json`](pim-eligibility.entra.json) | PIM eligibility for directory roles in ARM's `roleEligibilityScheduleRequests` shape: a role-assignable group permanently eligible for Helpdesk Administrator (an ARM snippet as is), the same group eligible for User Administrator in an administrative unit until a fixed date (`role` / `administrativeUnit` sugar), and a user eligible for Security Reader for 180 days with a change ticket; plus PIM for Groups (`privilegedAccess/groupEligibilityScheduleRequests`): the same user eligible for membership of an operations group for 90 days, and the helpdesk group eligible for its ownership until the fixed date | [`pim-eligibility.parameters.json`](pim-eligibility.parameters.json) (the engineer's object id and the end date) |
| [`pim-active.entra.json`](pim-active.entra.json) | Time-bound active access through PIM: a role-assignable incident response group an active Security Administrator for 30 days with a change ticket (ARM's `roleAssignmentScheduleRequests` shape, pasted as is) next to its permanent Security Reader assignment (`roleAssignments`, a different role), a user an active Exchange Administrator until a fixed date (`role` sugar); plus PIM for Groups (`privilegedAccess/groupAssignmentScheduleRequests`): the same user an active member of an operations group for 7 days and the incident group its active owner until the fixed date | [`pim-active.parameters.json`](pim-active.parameters.json) (the engineer's object id and the end date) |

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
  rerun with the same names finds and updates the same objects in the tenant (EARM keeps no state file); renaming a resource creates a new object and
  leaves the old one. `-n` is only a label in the output (default: the template's file name). Remove what a sample created with
  `earm deployment delete -f <template> -p '@<parameters>'`.
- `orders-api.parameters.json` references a Key Vault secret (`contoso-kv` / `bg-initial`). Offline validation never reads it; for a real run replace
  it with `{ "value": "…" }` through `-p initialPassword=…` or point it at a vault you can read.
- Conditional Access policies in both samples are `enabledForReportingButNotEnforced`: report-only. Switching one to `enabled` needs `--allow-ca-enforce`.
  Keep an emergency-access exclusion (the `tenant-security` sample has one) before enforcing anything.
- `tenant-security.entra.json` needs Entra ID P1 and, for the role-assignable group and the directory roles, a Privileged Role Administrator
  (or an application with `RoleManagement.ReadWrite.Directory`).
- `authentication-methods.entra.json` changes tenant-wide sign-in methods: review the what-if first. Switching SMS, voice or email off removes them as
  sign-in and password-reset methods for everyone; the FIDO2 allow-list blocks every other passkey model. `earm deployment delete` resets fido2,
  temporaryAccessPass and x509Certificate to the Microsoft default and keeps the other methods (they cannot be reset). Needs an Authentication Policy
  Administrator (or an application with `Policy.ReadWrite.AuthenticationMethod`).
- `pim-eligibility.entra.json` needs Microsoft Entra ID P2 (or ID Governance) and a Privileged Role Administrator (or an application with
  `RoleEligibilitySchedule.ReadWrite.Directory`). A role management policy that forbids permanent eligibility refuses the Helpdesk Administrator
  request (`PimPolicyViolation`): give it an `AfterDateTime` or `AfterDuration` expiration. Changing a principal, role or scope creates a new eligibility
  and leaves the old one (reported as retained); `earm deployment delete` removes the declared eligibilities with `adminRemove`.
  The group eligibilities need `PrivilegedEligibilitySchedule.ReadWrite.AzureADGroup`; the first one onboards the operations group in PIM for Groups
  (permanently). A group's default PIM policy can forbid permanent eligibility (`PimPolicyViolation`): both group eligibilities here expire.
- `pim-active.entra.json` needs Microsoft Entra ID P2 (or ID Governance) and a Privileged Role Administrator (or an application with
  `RoleAssignmentSchedule.ReadWrite.Directory` and `PrivilegedAssignmentSchedule.ReadWrite.AzureADGroup`). Active assignments must be time-bound:
  a permanent one belongs in `roleAssignments` (or the group's `members` / `owners`), and the template may not declare both for the same
  principal, role and scope, nor a `members` / `owners` list on a group that gets PIM active members or owners (`PimAssignmentConflict`). A role's
  or group's PIM policy may cap the duration (`PimPolicyViolation`). `earm deployment delete` removes the declared assignments with `adminRemove`.
- `app-with-sp-and-secret.entra.json` returns the client secret exactly once, in the run that creates or rotates it; rerunning without changing `secretTag`
  prints that output as `null` with a warning. Change `secretTag` to rotate.
