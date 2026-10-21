# Proxylayer.EARM

ARM-style JSON templates for **Microsoft Entra ID**, deployed through the **Microsoft Graph** API. This package installs the `earm` command.

> **Preview.** The template format and `apiVersion` `2026-10-01-preview` are in preview, and breaking changes are possible. Try it in a
> development tenant first.

## Install

```shell
dotnet tool install --global Proxylayer.EARM
earm --version
```

Requires the .NET 10 runtime. `earm` starts no other process: no Azure CLI, no PowerShell, no Graph SDK.

## What it does

Azure has ARM templates; Entra ID has nothing like them. EARM uses the ARM template format for Entra objects, under a `Microsoft.Entra` provider
namespace, and its verbs mirror `az deployment`.

- **Declarative and idempotent.** Applications, service principals, credentials, groups, users, licenses, role assignments, custom roles,
  administrative units, Conditional Access, authentication methods, PIM for directory roles and groups, access packages and access reviews.
  A rerun finds each object in the tenant and updates it.
- **ARM vocabulary.** `resources[]` with `type`, `apiVersion`, `name`, `properties`, `dependsOn`, `condition` and `copy`; parameters, variables,
  user functions and outputs; `resourceId()`, `reference()`, `copyIndex()`; parameter files with Key Vault references; Incremental and Complete
  modes; what-if with ARM's change types.
- **No local state.** A resource is found by its type and `name` in the live tenant on every run, as in ARM. There is nothing to commit, share or lock.
- **Safe by default.** Nothing the template doesn't declare is deleted (Complete mode only reports), replacements ask first, secrets never reach
  logs or what-if, and a new Conditional Access policy stays report-only unless you pass `--allow-ca-enforce`.

## A template

```json
{
  "$schema": "https://schema.earm.proxylayer.net/schemas/2026-10-01/deploymentTemplate.json#",
  "contentVersion": "1.0.0.0",
  "parameters": { "env": { "type": "string", "allowedValues": ["dev", "prod"] } },
  "variables":  { "appName": "[format('contoso-{0}-web', parameters('env'))]" },
  "resources": [
    { "type": "Microsoft.Entra/applications", "apiVersion": "2026-10-01-preview", "name": "[variables('appName')]",
      "properties": { "displayName": "Contoso Web", "signInAudience": "AzureADMyOrg",
                      "appRoles": [{ "value": "Reports.Admin", "displayName": "Reports admin", "description": "Manage reports", "allowedMemberTypes": ["User"] }] } },
    { "type": "Microsoft.Entra/servicePrincipals", "apiVersion": "2026-10-01-preview",
      "name": "[reference(variables('appName')).appId]", "properties": { "appRoleAssignmentRequired": true } },
    { "type": "Microsoft.Entra/groups", "apiVersion": "2026-10-01-preview", "name": "contoso-web-admins",
      "properties": { "displayName": "Contoso Web admins", "mailEnabled": false, "securityEnabled": true,
                      "appRoleAssignments": [{ "resourceAppId": "[reference(variables('appName')).appId]", "appRole": "Reports.Admin" }] } }
  ],
  "outputs": { "appId": { "type": "string", "value": "[reference(variables('appName')).appId]" } }
}
```

## Use

```shell
earm deployment validate -f app.entra.json -p @app.parameters.json        # offline: schema, types, expressions
earm permissions         -f app.entra.json -p @app.parameters.json        # Graph permissions and Entra roles it needs (offline)
earm auth login          -f app.entra.json -p @app.parameters.json        # sign in and consent what the template needs
earm deployment what-if  -f app.entra.json -p @app.parameters.json        # preview, read only
earm deployment create   -f app.entra.json -p @app.parameters.json -c     # show the what-if, confirm, apply
earm deployment delete   -f app.entra.json -p @app.parameters.json        # delete what the template declares (asks first)
earm schema install                                                       # editor validation for *.entra.json in VS Code
```

Exit codes: 0 success, 1 failure, 2 validation errors, 3 sign-in or preflight failure, 5 changes found (with `--exit-code-on-changes`).

## Signing in

| For | Use |
|---|---|
| A person | `earm auth login` (browser) or `earm auth login --device-code`. It signs in with Microsoft's Microsoft Graph Command Line Tools app and your delegated permissions, so no app registration is needed. |
| CI | A service principal: `AZURE_TENANT_ID`, `AZURE_CLIENT_ID` and `AZURE_CLIENT_SECRET` (or `AZURE_CLIENT_CERTIFICATE_PATH`), with the Graph application permissions `earm permissions` lists. |
| Azure-hosted automation | A managed identity or workload identity. |

Consent given to the Microsoft Graph Command Line Tools app is shared with Microsoft Graph PowerShell. Read
[SECURITY.md](https://github.com/Proxylayer/EARM/blob/main/SECURITY.md) before granting admin consent.

## Documentation

- [Repository and full README](https://github.com/Proxylayer/EARM)
- [Template format and functions](https://github.com/Proxylayer/EARM/blob/main/docs/format.md)
- [Every verb and flag](https://github.com/Proxylayer/EARM/blob/main/docs/cli.md)
- [Relationships: members, owners, roles, licenses](https://github.com/Proxylayer/EARM/blob/main/docs/edges.md)
- [Privileged Identity Management](https://github.com/Proxylayer/EARM/blob/main/docs/pim.md)
- [Samples](https://github.com/Proxylayer/EARM/blob/main/samples/README.md)
- [Changelog](https://github.com/Proxylayer/EARM/blob/main/CHANGELOG.md)
