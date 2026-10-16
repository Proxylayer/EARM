# Security

EARM changes a Microsoft Entra tenant: it can create applications and client secrets, assign directory roles, edit Conditional Access policies and delete
objects. Treat a run of `earm deployment create` with the care you give a privileged administrator session.

> EARM is a **preview**: its gated integration suite has been run against a development tenant, but it is not used in production (see the [README](README.md#status)). Do not point it at production.

## Reporting a vulnerability

Please do not open a public issue for a security problem. Use the repository's private vulnerability reporting
(the **Report a vulnerability** button on the Security tab of <https://github.com/Proxylayer/EARM>) and include the version (`earm --version`), the
command line, a minimal template and parameter file that reproduce it, and what you expected. Do not include real tenant data or secrets. You will get an
acknowledgement; fixes land on the main branch first (there is no released version to backport to yet).

## Threat model

### What is trusted

| Trusted | Why it matters |
|---|---|
| The operator and the machine or pipeline that runs `earm` | It holds a credential that can write the tenant. A compromised runner is a compromised tenant. |
| The signed-in identity and the tenant it signs in to | EARM never widens its permissions; `earm permissions` lists what a template needs so the identity can be scoped. |
| The file system permissions of the secure-outputs file | It is written owner-only (DACL on Windows, mode 0600 on Unix), but anything running as the same user can read it. It is the only file EARM writes. |
| Reviewed templates | A template can legitimately grant a role, disable a Conditional Access policy, replace an object (a recreate, after confirmation) or, with `deployment delete -f`, delete the objects it declares. **Review the template and the what-if before `create`**, the same way you review any infrastructure-as-code change. |
| The tenant's display names | Some types are found by `displayName` ([docs/engine.md](docs/engine.md#identity-without-state)). Whoever can rename or create such an object in the tenant (a Conditional Access policy, a named location, an administrative unit, a custom role) decides which object a later run updates, or makes it fail with `AmbiguousMatch`. Restrict who can manage those objects outside the pipeline. |

### No local state, and what EARM deletes

EARM keeps no state file: every run finds the declared objects in the tenant by their keys ([docs/engine.md](docs/engine.md#identity-without-state)). There is
therefore no file whose tampering could make EARM act on an object, and **nothing is deleted except what a template declares**:

- `create`, in either mode, deletes nothing the template does not declare. Complete mode only **reports** undeclared objects of the declared types.
- A **replacement** (an immutable property changed: delete, then create) is listed and needs confirmation (`-y`, or the prompt; a non-interactive run without `-y` fails before any
  write with `RecreateNotConfirmed`).
- `deployment delete -f` deletes the objects the template declares, found by key, after a confirmation that lists them; each is re-read by key right before its delete.
- The signed-in principal and service principals owned by another organization (Microsoft first-party and multi-tenant apps) are never deleted or replaced (`ProtectedObject`).

The flip side is security-relevant: **an assignment whose tuple changed keeps its old assignment.** EARM creates the new role assignment, app role assignment or grant and
reports the old one (`orphan: retained`, warning `AssignmentRetained`), but never removes it, so the old principal or scope keeps its access until someone removes it. A
principal-side relationship (`memberOf`, a principal's `appRoleAssignments` / `directoryRoles`) dropped from the template is not removed either; only target-side lists
(`members`, `owners`) are authoritative ([docs/edges.md](docs/edges.md)). Review the retained rows of every what-if that changes an assignment.

### What is defended against (hostile templates and parameter files)

Templates and parameter files can arrive from a pull request, a shared directory or a tenant, so they are parsed as untrusted input:

- **No code execution and no side channels.** Expressions are a closed function set evaluated by an interpreter: no file, process, environment or arbitrary network
  access. EARM starts no external process (no Azure CLI, no PowerShell). The only network peers are the Microsoft Graph host, the Entra authority and the Key Vault
  suffix of the selected cloud (`EARM_CLOUD`, a fixed table: there is no setting that names another endpoint).
- **Bounded work.** JSON nesting, expression length and depth, value size, `copy` expansion, evaluation steps, produced text and wall-clock time are all limited
  ([docs/format.md](docs/format.md#evaluation-limits)); a violation is a diagnostic, never a hang or an out-of-memory crash. Duplicate JSON keys are rejected.
- **Graph requests fail closed.** Request paths, `@odata.nextLink` values and `$batch` sub-requests that are not relative Graph paths are refused (`UnsafeUrl`), query
  values are percent-encoded, `$filter` values are escaped, paging has hard limits, and HTTP redirects are never followed (the Authorization header cannot be forwarded to another host).
- **No delete beyond the template.** See [No local state, and what EARM deletes](#no-local-state-and-what-earm-deletes). A symbolic link or junction as the secure-outputs file, or
  anywhere above its folder, is refused.
- **Untrusted text is made safe before it is printed.** Control characters, terminal escape sequences, bidirectional overrides and CI log-command prefixes
  (`##vso[`, `##[`, `::`) in names and values are escaped or neutralised on both streams ([docs/cli.md](docs/cli.md#output-errors-and-prompts)).
- **Conditional Access guard.** A policy with `state: enabled` is refused without `--allow-ca-enforce`, so a template cannot silently lock administrators out.

### How secrets are handled

- `securestring` / `secureObject` parameters, `passwordProfile.password` and a credential's `secretText` are **tainted**: they may flow only into resource properties and
  `securestring` outputs, and are `***` in what-if, logs, diagnostics, error messages and JSON output. Error text from Graph is
  scrubbed of secret-looking values before it is printed. Tokens, Authorization headers and request bodies are never logged.
- A secret on an existing object is **write-once**: EARM never compares or re-sends it (it cannot read it back), so changing a `secretText` or `passwordProfile.password` value in the
  template does not change the live secret. Rotate a client secret with `rotationTag`; a user's password is set only at creation.
- A secret Graph returns only once (a new client secret, a generated user password) is written to the file given by `--secure-outputs-file` **immediately**, under the key
  `oneTimeSecrets`, or printed with `--reveal-secure-outputs`. That file is in clear text, owner-only, and must be treated like a password store; it is git-ignored under the usual names, but you
  are responsible for where you put it.
- Key Vault parameter references are read over HTTPS with the signed-in identity's Key Vault token; the value is tainted from the moment it is read.
- Interactive sign-in tokens live in the platform's **encrypted** cache (DPAPI, Keychain, secret service); EARM never enables unencrypted storage. An `az login` session is not used.

### Delegated sign-in and shared consent

`earm auth login` signs you in to the Microsoft first-party app **Microsoft Graph Command Line Tools** (`14d82eec-204b-4c2f-b7e8-296a70dab67e`); nobody registers an
app. A run acts with **your** delegated permissions and requests only the scopes of its verb (read scopes for `what-if`), so EARM can never do more than you could
yourself, and usually much less ([docs/permissions.md](docs/permissions.md#delegated-scopes-per-run)). What to know about the consent this needs:

- **Consent is shared with other tools.** A delegated permission is granted to the sign-in app, not to EARM. Microsoft Graph PowerShell and the Microsoft Graph CLI sign in
  with the same app, so a scope you consent for EARM is also usable by them for your account (and the other way round). Consent only what your templates need
  (`earm auth login -f <template>` consents exactly that set).
- **Never tick "Consent on behalf of your organization"** on the consent prompt unless you intend it: it grants the scopes to the app for **every user** of the tenant
  (an `AllPrincipals` grant), not just for you. Without the tick an administrator's consent applies to the administrator only.
- **Review or revoke** at any time:
  - as the user: My Apps (`https://myapps.microsoft.com`) > the app's permissions;
  - as an administrator: Microsoft Entra admin center > *Enterprise applications* > *Microsoft Graph Command Line Tools* > *Permissions* (the *User consent* tab lists
    per-user grants, *Admin consent* the tenant-wide ones; revoke from there, or *Review permissions*);
  - with Microsoft Graph: find the grants with `GET /oauth2PermissionGrants?$filter=clientId eq '<object id of the app's service principal in your tenant>'`, then
    `DELETE /oauth2PermissionGrants/<id>` to remove a grant, or `PATCH` its `scope` to drop single scopes. Revoking takes effect for new tokens; an access token
    already issued stays valid until it expires (about an hour).
- **Tenants that do not allow user consent** (or that disabled the Microsoft app) need an administrator to grant the scopes, or an app registration of their own passed
  with `--client-id`; unattended runs should use a service principal or managed identity instead of a user sign-in.
- An older sign-in record that uses the Microsoft Azure CLI app (`04b07795-...`) keeps working with a warning; `earm auth login` switches to the default app.

### Residual risks (known, not fully closed)

- **A hostile template can learn one bit per output from a secret it may read.** A secret-dependent expression can make a container empty or not
  (`filter(createArray(1), lambda(...))` over the first character of a password), and an empty container has no element to carry the secret mark, so its emptiness is not
  tracked as secret and a plain output shows it ([docs/format.md](docs/format.md#security-considerations)). This stays until the planned taint-carrier change lands (the empty
  container itself would carry the mark). **Do not supply secrets (secure parameters, Key Vault references) to `what-if` or `validate` runs of templates you do not trust**, for
  example the pull-request validation of a repository that accepts outside contributions: run those without secrets or with throw-away values.
- **Key Vault references use the caller's identity.** A parameter file names a vault and a secret, and EARM reads it with the signed-in identity's Key Vault token, so a template or
  parameter file can reference any vault that identity can read, and the value (tainted) may then flow into a resource property the template defines, where whoever can read that
  object sees it. Set **`EARM_KEYVAULT_ALLOWED`** (a comma-separated list of vault names) in every pipeline that deploys: a reference to any other vault is refused before a token is
  requested. Give the identity access only to the vaults a deployment needs.
- **CI log-command neutralisation is best-effort.** Text that a CI log parser would run as a command is broken on both streams (`##vso[`, `##[` and a leading `::`; control
  characters and bidirectional overrides are escaped), which covers Azure Pipelines and GitHub Actions. Other systems have other command syntaxes, and a runner may change how it
  parses a log, so treat this as defence in depth: do not give a job that processes untrusted templates a token that matters, and prefer `-o json` output for machine consumption.

### Out of scope

- A malicious or compromised operator, runner or signed-in identity.
- Anything another process running as the same user can do to files EARM wrote.
- The content of a reviewed template: EARM faithfully applies a template that deletes your groups, and Graph decides what a permission allows.
- Vulnerabilities in Microsoft Graph, Azure.Identity or the .NET runtime (report those to Microsoft); EARM tracks their security updates through its package versions.

## Hardening checklist for operators

1. Run unattended deployments as a service principal or managed identity with only the permissions `earm permissions` lists, in a dedicated dev tenant first.
   Interactive runs act as the signed-in user: consent only the scopes your templates need, never on behalf of the organization by accident, and review the grants of
   *Microsoft Graph Command Line Tools* from time to time ([Delegated sign-in and shared consent](#delegated-sign-in-and-shared-consent)).
2. Read `deployment what-if` (and `--mode Complete` what-if) before `create`; in CI use `--exit-code-on-changes` as a drift gate and require review of template changes.
3. Keep the secure-outputs file (and its `.previous` / `.lock` companions) out of source control and build logs.
4. Do not pass `-y` in a pipeline unless a human reviewed the what-if (its replacements) for that exact run, and review the `orphan: retained` rows: old assignments keep granting access until you remove them.
5. Serialise runs of one template (no lock protects concurrent runs; display-name-keyed types can be duplicated).
6. Set `EARM_KEYVAULT_ALLOWED` in pipelines that read Key Vault references, and do not give secrets to `what-if` / `validate` runs of untrusted templates (pull-request validation).
