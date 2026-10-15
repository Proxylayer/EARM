# Security

EARM changes a Microsoft Entra tenant: it can create applications and client secrets, assign directory roles, edit Conditional Access policies and delete
objects. Treat a run of `earm deployment create` with the care you give a privileged administrator session.

> EARM is a **preview** and has not been validated against a real tenant (see the [README](README.md#status)). Do not point it at production.

## Reporting a vulnerability

Please do not open a public issue for a security problem. Use the repository's private vulnerability reporting
(the **Report a vulnerability** button on the Security tab of <https://github.com/Proxylayer/EARM>) and include the version (`earm --version`), the
command line, a minimal template or state file that reproduces it, and what you expected. Do not include real tenant data or secrets. You will get an
acknowledgement; fixes land on the main branch first (there is no released version to backport to yet).

## Threat model

### What is trusted

| Trusted | Why it matters |
|---|---|
| The operator and the machine or pipeline that runs `earm` | It holds a credential that can write the tenant. A compromised runner is a compromised tenant. |
| The signed-in identity and the tenant it signs in to | EARM never widens its permissions; `earm permissions` lists what a template needs so the identity can be scoped. |
| The file system permissions of the state directory and of the secure-outputs file | Both are written owner-only (DACL on Windows, mode 0600 on Unix), but anything running as the same user can read them. |
| Reviewed templates | A template can legitimately grant a role, disable a Conditional Access policy or delete an object (Complete mode). **Review the template and the what-if before `create`**, the same way you review any infrastructure-as-code change. |

### The state file is trusted for which objects it names

The state file (`.earm/state/<name>.json`) is **trusted for WHICH objects a deployment manages**: it maps each resource id to a Graph object id, and a Complete-mode run or
`deployment delete --delete-resources` deletes the objects it names. Its integrity is **not cryptographic**: there is no signature, MAC or hash chain, so anyone who can
write the file can write a *consistent* entry (the true key and id of a real object, `provenance: created`) that EARM cannot tell from one of its own. EARM bounds the damage but
cannot prevent it (shape and key checks on load, a re-read by key before every delete, the signed-in principal and first-party service principals never deleted, adopted
objects never deleted without `--delete-adopted`, `--max-deletes` as a hard limit during the run, the confirmation, the tenant pin); the full table of tampering cases and what each
check stops is in [docs/engine.md](docs/engine.md#state-trust-model). What that means for operators:

- Keep `.earm/state` (or the `--state-dir` / `EARM_STATE_DIR` directory) in a **protected location**: writable only by the people and pipelines that may change the tenant, never by a
  contributor who can merely open a pull request.
- When the state is committed, protect the directory like the template: a **CODEOWNERS** entry for it (for example `/.earm/state/ @your-org/identity-admins`) and **branch protection**
  that requires an approved review and forbids direct pushes, so a state change cannot reach the branch a deployment runs from without a second pair of eyes. Review a state diff in a
  pull request as carefully as a template diff: it chooses which objects a later `--mode Complete` run may delete.
- A pipeline that runs `create` must read the state from the protected branch or location, not from a fork's checkout.

### What is defended against (hostile templates, parameter files and state)

Templates, parameter files and state files can arrive from a pull request, a shared directory or a tenant, so they are parsed as untrusted input:

- **No code execution and no side channels.** Expressions are a closed function set evaluated by an interpreter: no file, process, environment or arbitrary network
  access. EARM starts no external process (no Azure CLI, no PowerShell). The only network peers are the Microsoft Graph host, the Entra authority and the Key Vault
  suffix of the selected cloud (`EARM_CLOUD`, a fixed table: there is no setting that names another endpoint).
- **Bounded work.** JSON nesting, expression length and depth, value size, `copy` expansion, evaluation steps, produced text and wall-clock time are all limited
  ([docs/format.md](docs/format.md#evaluation-limits)); a violation is a diagnostic, never a hang or an out-of-memory crash. Duplicate JSON keys are rejected.
- **Graph requests fail closed.** Request paths, `@odata.nextLink` values and `$batch` sub-requests that are not relative Graph paths are refused (`UnsafeUrl`), query
  values are percent-encoded, `$filter` values are escaped, paging has hard limits, and HTTP redirects are never followed (the Authorization header cannot be forwarded to another host).
- **The state is a hint, not an authority** (and it is not tamper-proof: see [the trust model](#the-state-file-is-trusted-for-which-objects-it-names)). Graph ids in a state file are shape-checked, a pinned read is verified against the declared key, and every state-driven
  delete re-reads the object by key and refuses on a mismatch (`StateEntryMismatch`). The signed-in principal and first-party service principals are never deleted
  (`ProtectedObject`); adopted objects are never deleted unless `--delete-adopted`; Complete mode needs `--allow-delete-all` for an empty template or more than `--max-deletes`
  deletions. A state file is bound to its tenant (exit 3 on a mismatch). Symbolic links and junctions as state directory, state file, lock or secure-outputs file are refused.
- **Untrusted text is made safe before it is printed.** Control characters, terminal escape sequences, bidirectional overrides and CI log-command prefixes
  (`##vso[`, `##[`, `::`) in names and values are escaped or neutralised on both streams ([docs/cli.md](docs/cli.md#output-errors-and-prompts)).
- **Conditional Access guard.** A policy with `state: enabled` is refused without `--allow-ca-enforce`, so a template cannot silently lock administrators out.

### How secrets are handled

- `securestring` / `secureObject` parameters, `passwordProfile.password` and a credential's `secretText` are **tainted**: they may flow only into resource properties and
  `securestring` outputs, and are `***` in what-if, logs, diagnostics, error messages and JSON output. The state serializer refuses to write one, and error text from Graph is
  scrubbed of secret-looking values before it is stored or printed. Tokens, Authorization headers and request bodies are never logged.
- A secret Graph returns only once (a new client secret, a generated user password) is written to the file given by `--secure-outputs-file` **immediately**, under the key
  `oneTimeSecrets`, or printed with `--reveal-secure-outputs`. That file is in clear text, owner-only, and must be treated like a password store; it is git-ignored under the usual names, but you
  are responsible for where you put it.
- Key Vault parameter references are read over HTTPS with the signed-in identity's Key Vault token; the value is tainted from the moment it is read.
- Interactive sign-in tokens live in the platform's **encrypted** cache (DPAPI, Keychain, secret service); EARM never enables unencrypted storage. An `az login` session is not used.

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
2. Read `deployment what-if` (and `--mode Complete` what-if) before `create`; in CI use `--exit-code-on-changes` as a drift gate and require review of template changes.
3. Persist the state with the template or in a protected shared directory, and keep the secure-outputs file (and its `.previous` / `.lock` companions) out of source control and build logs. Protect the state directory with CODEOWNERS and branch protection (see above).
4. Do not pass `-y` and `--allow-delete-all` together in a pipeline unless a human reviewed the what-if for that exact run.
5. Set `EARM_KEYVAULT_ALLOWED` in pipelines that read Key Vault references, and do not give secrets to `what-if` / `validate` runs of untrusted templates (pull-request validation).
