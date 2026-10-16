# Testing

Everything runs headless through `test.ps1`. The unit and in-process suites need no tenant, no sign-in and no network; the one suite that does
(`EArm.Integration.Tests`) is gated and is never part of a plain run.

```powershell
pwsh -File test.ps1                                    # every project except the gated integration one, then the Timing tests serially (see below); about 95 seconds warm (the build is included)
pwsh -File test.ps1 -SkipTiming                        # skip the serial Timing pass (45-60 seconds warm, 57 s measured): the quick loop while iterating
pwsh -File test.ps1 -Target EArm.Tests                 # one project (name or .csproj path)
pwsh -File test.ps1 -Filter "FullyQualifiedName~CliSmokeTests"   # a VSTest-syntax filter, passed to every selected project (one pass; see "Filtering")
pwsh -File test.ps1 -Detailed                          # full dotnet test output; only after a plain run shows a failure
pwsh -File test.ps1 -Coverage                          # + Cobertura XML per project in artifacts/test-results (slower: instrumented)
pwsh -File tools/Get-CoverageReport.ps1 -Top 15        # merge the XML: per-assembly table + weakest classes (see tests/COVERAGE.md)
```

Results are TRX files in `artifacts/test-results/`. The runner is Microsoft.Testing.Platform (opted in through `global.json`), xUnit v3. Simple mode prints one
line per project:

| Line | Meaning |
|---|---|
| `PASS` | The project ran and nothing failed (`n/total (k skipped)`). |
| `FAIL` | Failures, with the failing test names and the first assertion lines. Re-run with `-Detailed -Filter <name>` to diagnose. |
| `NO TESTS` | The project has no tests. |
| `GATED` | The project was **not run**: `EArm.Integration.Tests` without `EARM_IT_TENANT`. Not a pass. |
| `SKIPPED` | The project ran but every test skipped (for example the integration project named with `-Target` and no tenant). Not a pass. |
| `<project> [timing, serial]` | The second pass of the same project: only the `Category=Timing` tests, one at a time (`PASS` / `FAIL` as above; absent for a project without Timing tests). |
| `NO TESTS MATCHED` | Exit code **3**: a `-Filter` that selects no test in any selected project (a typo, or a namespace filter, see "Filtering"). A silent green run for a filter that ran nothing would hide the mistake. |

## Timing tests

A test whose assertion depends on the wall clock is tagged `[Trait("Category", "Timing")]` (on the method; `Review4L_RepositoryHygieneTests` fails when a test file asserts an
elapsed-time bound without one). `test.ps1` runs them in a **second, serial pass** (`--parallel none`) after the parallel pass that excludes them
(`--filter-not-trait Category=Timing`), so the rest of the suite is not competing for the CPU while a bound is measured. Their bounds are generous on purpose: they
separate "linear" from "quadratic", or "stopped by the timeout" from "ran to the default limit" (seconds to minutes), not 20 ms from 40 ms. Concurrency
tests that must observe overlapping requests hold each request open for 250-300 ms for the same reason.

- `-SkipTiming` drops the second pass (an overloaded machine, a quick edit loop). CI never uses it.
- `-Filter` runs one pass (the serial pass is only for an unfiltered run): `-Filter "Category=Timing"` runs only the timing tests, `-Filter "Category!=Timing"` everything else, and the two combine with other terms in the VSTest syntax (`"(FullyQualifiedName~Review1C)&Category!=Timing"`).
- A Timing test that fails only when the machine is busy is a bound that is too tight: widen it, or replace the clock by an operation count; do not retry.

## Filtering

`-Filter` is a VSTest-syntax expression: `FullyQualifiedName~Review1C_BudgetTests` (a class), `FullyQualifiedName~Review1C_BudgetTests.Union` (a method prefix),
`DisplayName~lock`, `Category=Timing` (a trait). **Test namespaces do not follow the folders**: a class under `tests/EArm.Tests/Core/` is usually in the namespace
`EArm.Tests` (some are in `EArm.Tests.Cli` or `EArm.Tests.Engine`), so `--filter-namespace EArm.Tests.Core` matches nothing, and `test.ps1` exits 3 for a filter that
matches nothing. Filter by class name, or run a test executable directly for the xunit v3 switches (`--filter-class EArm.Tests.Review1C_BudgetTests`,
`--filter-method`, `--filter-trait "Category=Timing"`, `--filter-not-trait`, `--parallel none`; `EArm.Tests.exe --help` lists them). To find which folder a class lives
in, search for the class name: `grep -rl "class Review1C_BudgetTests" tests`.

## The suites

| Project | What it tests | Seam |
|---|---|---|
| `EArm.Tests` | Core (loader, validator, schemas, parameters), the expression language, the engine (planner, executor, edge pass, orphan report, what-if), the CLI in-process (including `NoLocalStateTests`: what-if, create and delete write no file but the requested secure outputs file) | `CliRunner` drives `Program.InvokeAsync` with captured output; `CliSandbox` adds a private working directory, a fake sign-in and a fake Graph behind the real `HttpClient` stack |
| `EArm.Providers.Tests` | One provider per type family: rules (pure), lifecycle against the fake directory, failure matrix, edge kinds, secret leaks | `ProviderHost` (real providers + `FakeGraphClient` over `FakeDirectory`) |
| `EArm.Graph.Tests` | The transport: `HttpGraphClient` (paging, `$batch`, retry, limits), auth, preflight, Key Vault, secret redaction, owner-only files | `ScriptedHttpHandler` and a loopback server; no real network |
| `EArm.TestSupport` | Not a test project: `FakeDirectory` (an in-memory Microsoft Graph with the behaviours that matter: alternate keys, `$filter`, `Prefer: create-if-missing`, soft delete, paging, `$batch`), `FakeGraphClient`, `ScriptedGraphHandler`, `EchoProvider`, fake token providers | built as a reference, never run |
| `EArm.Integration.Tests` | The real thing against a dev tenant: scenarios through the real CLI, plus probes that verify Graph behaviours the fakes assume | gated, see below |

Golden files: `tests/fixtures/whatif/*` pin the what-if text and JSON byte for byte; regenerate them with `tools/Update-WhatIfGoldens.ps1` after an intentional
rendering change and review the diff ([whatif.md](whatif.md#golden-fixtures)). `tests/fixtures/templates` holds valid and invalid templates for the loader and validator.

## Conventions

- No implicit usings and no PowerShell or `az` at run time: tests that shell out (`TypeSchemaGeneratorTests` runs `tools/New-TypeSchemas.ps1`) **skip** when `pwsh`
  is not on the `PATH` rather than fail.
- A test may skip only for an environmental reason it states (`Assert.SkipUnless(...)`): Windows-only DACL checks, Unix-only file modes, symbolic links that the host
  will not create. A test that always skips is dead and is deleted, not kept.
- Prefer proofs that do not read a clock: file stamps are back-dated (`SchemaInstallerTests.SecondRun_RewritesNothing`), clocks are injected (`TimeProvider`,
  `FakeTimeProvider`, a fixed clock for password rotation), delays are replaced by a no-delay provider, and "linear, not quadratic" is asserted through the evaluation
  budget's step counter where the work is charged (`Review1C_BudgetTests` union / intersection, `Review3J_VariableChainTests`). Some behaviour is genuinely about
  wall-clock time (the evaluation timeout, a regex timeout, the wait for a held secure-outputs file, requests that must overlap to prove parallelism), and
  there a test does read the clock or sleep. Those tests carry `[Trait("Category", "Timing")]` (see "Timing tests"); a test that waits for something to happen
  polls or awaits it with a generous limit instead of sleeping a guessed time, and an upper bound is a hang guard, not a benchmark. The remaining fixed
  `Task.Delay` calls only hold something open and assert a negative that a loaded machine cannot turn into a failure (a waiter is still waiting, a lock is still held).
- Secrets in fixtures are obvious strings (`hunter2...`) so a leak assertion (`DoesNotContain`) is meaningful.
- Logic that is hard to reach through the CLI is extracted into a pure helper and tested directly; the CLI tests then only prove the wiring.

## Mutation-style review

The suite is reviewed by asking, for each behaviour that matters, "which one-line change to the product would still pass every test?" (flip a `<` to `<=`,
drop a flag between the parser and the engine, send PUT instead of PATCH, skip a redaction). Where the answer was "this one", a test was added whose summary names the
mutant it exists to kill. Those classes carry the review round in their name, so they are easy to find and to read as a list of guarded edges:

| Prefix | Round | Examples |
|---|---|---|
| `Review1A_`..`Review1D_` | The first review wave: run safety, identity by live key, transport limits, console safety, provider fidelity | `Review1A_IdentityTests`, `Review1D_CaFlagTests` (the Conditional Access guard reaches both what-if and create) |
| `Review2E_` | Alternate-key wire format, adoption, diagnostic caps, app-only token behaviour | `Review2E_AlternateKeyWireTests` |
| `Review2F_` | Flag wiring through the real command line (`--parallelism`, `--continue-on-error`, `--on-soft-deleted-conflict`, `--result-format`, `--no-pretty-print`, `-n`, `--exit-code-on-changes`), `tenant()` / `environment()` / signed-in principal reaching the engine, the edge executor's verb, `$batch` threshold (20 / 21) and sub-response handling, the rotation boundary (`<` versus `<=` at exactly `rotateBeforeDays`), docs/cli.md versus the command tree in both directions | `Review2F_CliWiringTests`, `Review2F_EdgeExecutorTests`, `Review2F_RotationBoundaryTests`, `Review2F_DocsCliParityTests` |
| `Review4L_` | Repository hygiene and pinned surface: `.gitignore` patterns (textually and through `git check-ignore`), the CI workflow's safety settings, wall-clock assertions only inside `Category=Timing`, the new command-line flags and diagnostic codes pinned by name in the tree and in the documents, the `$batch` item error codes | `Review4L_RepositoryHygieneTests`, `Review4L_NewSurfaceDocsTests`, `Review4L_BatchErrorCodeTests` |
| `Review2G_` | Quoted values of secret names in `SecretRedactor` (`password="x"`, escapes, unterminated quotes, linear time) | `Review2G_SecretRedactorQuotedValueTests` |

A test that exposes a real defect is left failing (with the defect described in its summary) until the product is fixed; it is not skipped to hide the red.
Known limitation of the mutation approach here: it is a manual review, not an automated mutation run (no Stryker configuration is checked in).

## Coverage

`test.ps1 -Coverage` collects Cobertura XML with Microsoft.Testing.Extensions.CodeCoverage; `tools/Get-CoverageReport.ps1` merges the projects per source line and
reports per assembly and per class. Only `src/` counts. The numbers are measured on one host, so platform-specific branches (Unix file modes on a Windows run, DACL code
on a Linux run) show as uncovered; see [tests/COVERAGE.md](../tests/COVERAGE.md) for the current table and why the weakest classes are weak.

## The gated integration tests

`EArm.Integration.Tests` writes to a real **dev** tenant. A plain `test.ps1` prints `GATED` for it and moves on. To run it set `EARM_IT_TENANT` (the tenant id the
sign-in must belong to) and, depending on what you want to exercise, `EARM_IT_DOMAIN` (a verified domain for user principal names), `EARM_IT_ALLOW_CA=1`
(Conditional Access objects), `EARM_IT_ALLOW_ROLES=1` (directory roles, administrative units, role and consent assignments), `EARM_IT_PREFIX` (the name prefix of
every object it creates) and `EARM_IT_SWEEP=1` (a manual cleanup of a leaked prefix). Every object carries the run's prefix and is cleaned up by a guard that
only touches that prefix. The main sign-in is your own user (`earm auth login`): the fixture requests the delegated scopes of the enabled scenarios, so consent
them once with the `earm auth login --tenant <id> --scopes ...` command a skipped run prints (or set `EARM_IT_INTERACTIVE=1` to be prompted once at the start);
a service principal in the `AZURE_*` variables is the CI option. `EARM_IT_CONSENT_PROBE=1` runs the interactive consent probes (`Category=Consent`).
What it creates, what it needs and how to clean up after a crashed run: [tests/EArm.Integration.Tests/README.md](../tests/EArm.Integration.Tests/README.md).
Until it has been run once against a tenant, the product is **unvalidated against a real Microsoft Graph**.

## CI

`.github/workflows/ci.yml` runs `build.ps1` and `test.ps1` (both passes) on `windows-latest` and `ubuntu-latest` with the SDK from `global.json`, then builds
the **Release** configuration and runs `dotnet pack` (the same deterministic switches as `deploy.ps1`; the package is an artifact, never published), and uploads the TRX files.
The **Linux job is what runs the Unix-only tests** (owner-only file modes, symbolic-link refusal where the host allows them); the Windows job runs the DACL tests; each side
skips the other with a stated reason. Actions are pinned by major tag (the SHA-pinning trade-off is written in the workflow). The workflow sets `EARM_UPDATE_GOLDENS` to empty and a
step fails the job when it is set on a CI run (`WhatIfGoldenTests.CiRun_MustNotRequestAGoldenUpdate` is the same guard inside the suite). It never sets `EARM_IT_TENANT`.
