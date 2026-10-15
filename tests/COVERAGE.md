# Test coverage

Measured with Microsoft.Testing.Extensions.CodeCoverage (Cobertura) through `test.ps1 -Coverage`, merged per source line by
`tools/Get-CoverageReport.ps1`. Only code under `src/` is counted. Branch figures are conditions x 2 as reported by the collector and are approximate: the merge keeps
the best result per condition across the three test projects. The gated `EArm.Integration.Tests` project is not part of these numbers. Measured on one host (Windows),
so the Unix-only branches of the owner-only file code count as uncovered here, and the Windows-only ones would on a Linux run.

Last measured: 2026-10-15, 6,113 tests: 1,063 in `EArm.Graph.Tests` (1,037 in the parallel pass + 26 Timing), 1,294 in `EArm.Providers.Tests`, 3,756 in `EArm.Tests` (3,739 + 17 Timing); 6 skipped for the host (2 Unix-only file-mode tests and 4 that need symbolic links, which this Windows account cannot create), 0 failed.

## Regenerate

```powershell
pwsh -File test.ps1 -Coverage                                    # all projects, writes artifacts/test-results/*.cobertura.xml
pwsh -File tools/Get-CoverageReport.ps1 -Top 15                  # per-assembly table + weakest classes
pwsh -File tools/Get-CoverageReport.ps1 -Format Json > cov.json  # every class with its uncovered line ranges
```

The coverage run is slower than a plain run (instrumentation); it is a development-time measurement, not part of the default suite. Update this file by pasting the
two tables below from the report and re-reading the notes under them; do not edit the numbers by hand. (The coverage run includes the serial Timing pass, written to
`<project>.timing.cobertura.xml`; the report merges every file.)

## Per assembly

| Assembly | Lines | Covered | Line % | Branches | Covered | Branch % |
|---|---:|---:|---:|---:|---:|---:|
| earm | 3770 | 3562 | 94.5 | 1506 | 1326 | 88 |
| EArm.Core | 3781 | 3675 | 97.2 | 1800 | 1704 | 94.7 |
| EArm.Engine | 8359 | 7784 | 93.1 | 3982 | 3484 | 87.5 |
| EArm.Expressions | 5085 | 4929 | 96.9 | 2414 | 2288 | 94.8 |
| EArm.Graph | 3304 | 3109 | 94.1 | 1674 | 1521 | 90.9 |
| EArm.Providers | 7358 | 7006 | 95.2 | 3844 | 3323 | 86.4 |
| **Total** | 31657 | 30065 | 95 | 15220 | 13646 | 89.7 |

## The 15 weakest classes (8 or more lines, ranked by missed lines)

| # | Class | Assembly | Lines | Line % | Branch % | Uncovered lines |
|---:|---|---|---:|---:|---:|---|
| 1 | Engine.Execution.DeploymentExecutor.Run | Engine | 893 | 90.6 | 89.3 | 184-187, 287-293, 364-367, 403-416, 544-547, 557-560, 660-661, 716-720, 731-733, 803-805, 850-854, 859, 865-868, 908-910, 950-952, 963, 977, 1021-1022, 1026-1028, 1036-1039, 1098-1100, 1151-1156, 1373-1378 |
| 2 | Engine.Edges.EdgePlanner | Engine | 548 | 90.3 | 86.3 | 143-144, 228-231, 235-238, 242-245, 438-446, 453-455, 512-513, 648-649, 685-687, 729-735, 739-745, 769-771, 796-797, 809-810, 841-843 |
| 3 | Engine.Planning.DependencyGraph.Builder | Engine | 330 | 84.5 | 77.8 | 191-193, 242-244, 262-263, 300-302, 335-336, 363-366, 370-377, 381-383, 390-392, 404-405, 409-411, 416-417, 429-430, 441-457 |
| 4 | Engine.State.FileStateStore | Engine | 388 | 90.7 | 93.1 | 127-128, 132-135, 366-367, 471-476, 520, 530-531, 542-544, 552-555, 575-577, 588-591, 607-610, 648-650 |
| 5 | Cli.Commands.StateCommand | earm | 196 | 82.7 | 83.3 | 115-121, 132-134, 163-164, 188-191, 210-212, 217-218, 227-228, 236-238, 245-246, 285-292 |
| 6 | Engine.Planning.ResourceExpander | Engine | 244 | 87.3 | 84.9 | 76-83, 107-109, 130-132, 177-180, 207-209, 217-220, 230-232, 253-259 |
| 7 | Graph.OwnerOnlyFile | Graph | 90 | 65.6 | 65.6 | 59-61, 90-92, 101-102, 115-141, 152-155 |
| 8 | Engine.Planning.DeploymentPlanner | Engine | 465 | 93.5 | 90 | 286-288, 312-323, 389-394, 587-593, 651-653, 657-659 |
| 9 | Providers.GroupsLicensing | Providers | 205 | 87.3 | 80 | 100-106, 129-130, 164-167, 238-239, 243-244, 257-258, 262-263, 289-292, 324, 332 |
| 10 | Providers.Edges.AppRoleAssignmentEdgeKind | Providers | 253 | 89.7 | 81 | 131-132, 144-145, 157-160, 347-371 |
| 11 | Graph.HttpGraphClient | Graph | 628 | 96 | 87.7 | 89, 238-239, 247-248, 348-349, 487-489, 523-524, 641-648, 723-724, 728-730, 815, 907-909 |
| 12 | Expressions.Functions.StringFunctions | Expressions | 653 | 96.3 | 92.4 | 232-240, 246-249, 337, 712, 819-821, 916-917, 952-958 |
| 13 | Providers.UsersProvider | Providers | 340 | 92.9 | 85.1 | 126, 139-141, 151-152, 272-273, 314-316, 400-402, 455-457, 543-544, 548-549, 566-568 |
| 14 | Providers.FederatedIdentityCredentialsProvider | Providers | 281 | 91.5 | 81.9 | 152-154, 171-172, 214-215, 220-221, 231-232, 316-317, 327-329, 361-364, 405-407, 466-468 |
| 15 | Cli.Commands.DeploymentRunner | earm | 263 | 90.9 | 82 | 222-228, 245-246, 291-296, 402-409, 418-419, 429-431 |

Line numbers are of the source at the time of the run; they drift with every edit, so re-run the report instead of trusting them after a change.

## What is still not covered, and why

- **Platform branches.** `Graph.OwnerOnlyFile` lines 115-141 are the Unix path (mode 0600), and the Windows DACL checks are the part a Linux run misses; the tests for each side
  skip on the other platform (`OwnerOnlyFileTests`; CI runs both a Windows and a Linux job).
- **`Engine.State.FileStateStore`**: the lock's I/O race handlers (a lock file vanishing or being held between a read and a delete) and the liveness probe of another process's pid (`Process.GetProcessById`), which a single-process test run cannot stage; the stale-takeover logic around them is covered with an injected `IsHolderAlive`.
- **Defensive branches the validator makes unreachable.** `EdgePlanner` 221-238 and 691-707, `DependencyGraph.Builder` 363-457, `ResourceExpander` 53-210 and
  `InstanceEvaluator` 40-111 handle shapes (invalid `copy` forms, unevaluable `condition`, an edge declared on an unsupported type) that `SemanticChecker` and the edge-kind
  registry reject before the planner runs; they can only be reached by hand-building instances.
- **Late failure handling in the executor.** `DeploymentExecutor.Run` 159-162 (the end phase after an unexpected exception) and the other listed ranges are mostly guards for
  state saves that keep failing and for providers that throw something other than the documented types. The documented failure kinds are covered by the provider failure matrix and
  `Review1A_RunSafetyTests`.
- **`Cli.Commands.StateCommand`**: the catch blocks of the verbs (a `StateException` or an I/O failure while reading or writing the state, an empty `-n`), which need a store that
  fails on demand; the verbs' normal paths, `set-provenance` and the lenient `forget` are covered. (`Cli.Output.ConsoleSanitizer`, weak in the previous measurement, is no longer
  among the weakest: its `TextWriter` overloads now have tests.)
- **`Providers.GroupsLicensing`** 100-332: licence-assignment error mapping; needs a fake SKU catalogue with partially failing `assignLicense` calls (`FakeGraphClient` has no per-call
  failure script for that action yet).
- **Not measurable here:** everything that needs a real Microsoft Graph or a real sign-in is covered only by the gated integration project, which has not been run against a tenant.
- The planner and executor `--parallelism` limit is asserted through the number of concurrent HTTP requests, which `HttpGraphClient`'s own `MaxParallel` also enforces, so a
  mutant that mis-wires only one of the layers is masked by the other (noted in `Review2F_CliWiringTests`).

## Defect history of the first coverage pass

Ten defects were found by the first coverage pass and are all fixed; each has a test that is now a normal test (names without a `Defect_` prefix). In short: unbounded
expression nesting (stack overflow), a state save tied to the caller's cancellation token, quadratic and then stack-overflowing secret redaction (now four linear scans),
lone-surrogate escapes crashing the schema pass, order-sensitive array diffing, a non-string `secretVersion` silently dropped, value-dependent `AsInt64`, a duplicated
`Content-Type` header, prefixed `Authorization` names not masked, and the owner-only file keeping its old DACL when it replaced an existing file. A later one, a quoted value assigned to a secret name (`password="x"`) that `SecretRedactor` did not mask, is fixed too and is covered by `Review2F_StateSinkTests` and `Review2G_SecretRedactorQuotedValueTests`. The last one, a string whose bytes are not valid UTF-8 that the loader accepted and a later stage choked on (found by review 4L), is fixed too (strict UTF-8 in `TemplateJson.Parse` and in the state file reader): `TemplateInputRobustnessTests.InvalidUtf8InsideAString_IsAnInvalidJsonLoadException_WithTheByteOffset` and `Review5M_Utf8Tests` cover it.

## Known flakiness

None known in plain runs. Wall-clock-dependent tests are the usual source, so they are fenced: a test that asserts an elapsed-time bound carries `[Trait("Category", "Timing")]`
and `test.ps1` runs that group in a second, serial pass (`-SkipTiming` skips it) with bounds that separate "linear" from "quadratic" or "stopped by the timeout" from "ran to the
default limit", not milliseconds; tests that must observe overlapping requests hold each one open for 250-300 ms; the other time proofs use injected clocks, back-dated file stamps
or the evaluation budget's step counter. The suite was run with 28 busy loops competing for the 28 cores of the measuring machine and the previously fragile tests
(`Review1C_BudgetTests` union / intersection, `FileStateStoreStressTests`, `Review1C_FuzzTests`, the concurrency probes of `Review2F_CliWiringTests`) passed. A coverage
(instrumented) run is slower than a plain one; if a Timing test fails only under `-Coverage`, re-run it plain before suspecting the product. Fixed `Task.Delay` calls that remain
only hold something open for a negative check (a waiter is still waiting, a released lock does not reappear) and cannot fail because the machine is slow.

## Runtime

On the measuring machine (28 cores, warm build) the parallel pass of `pwsh -File test.ps1 -SkipTiming` takes **45-60 seconds** (57 s measured), the serial Timing pass adds about 35-40 s (a plain `test.ps1` is about 95 s), and `-Coverage` (both passes) takes about 110 s. A cold build adds the compile time; fewer cores stretch the parallel pass.
