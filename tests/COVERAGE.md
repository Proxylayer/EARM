# Test coverage

Measured with Microsoft.Testing.Extensions.CodeCoverage (Cobertura) through `test.ps1 -Coverage`, merged per source line by
`tools/Get-CoverageReport.ps1`. Only code under `src/` is counted. Branch figures are conditions x 2 as reported by the collector and are approximate: the merge keeps
the best result per condition across the three test projects. The gated `EArm.Integration.Tests` project is not part of these numbers. Measured on one host (Windows),
so the Unix-only branches of the owner-only file code count as uncovered here, and the Windows-only ones would on a Linux run.

Last measured: 2026-10-15, after the state subsystem was removed. A plain `test.ps1` runs 5,753 tests: 1,063 in `EArm.Graph.Tests` (1,037 in the parallel pass + 26 Timing), 1,292 in `EArm.Providers.Tests`, 3,398 in `EArm.Tests` (3,381 + 17 Timing); 3 skipped for the host (2 Unix-only file-mode tests and 1 that needs a symbolic link, which this Windows account cannot create), 0 failed. (In the instrumented run one deep-nesting test, `Review3J_VariableChainTests` with a 2,000-long chain, reported `ValueTooDeep` instead of `ExpressionTooDeep`; it passes in plain runs. The numbers below are from that run.)

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
| earm | 2576 | 2472 | 96 | 1016 | 884 | 87 |
| EArm.Core | 3910 | 3804 | 97.3 | 1878 | 1781 | 94.8 |
| EArm.Engine | 6503 | 5923 | 91.1 | 3118 | 2664 | 85.4 |
| EArm.Expressions | 5210 | 5053 | 97 | 2494 | 2365 | 94.8 |
| EArm.Graph | 3317 | 3123 | 94.2 | 1682 | 1527 | 90.8 |
| EArm.Providers | 7379 | 7014 | 95.1 | 3860 | 3309 | 85.7 |
| **Total** | 28895 | 27389 | 94.8 | 14048 | 12530 | 89.2 |

## The 15 weakest classes (8 or more lines, ranked by missed lines)

| # | Class | Assembly | Lines | Line % | Branch % | Uncovered lines |
|---:|---|---|---:|---:|---:|---|
| 1 | Engine.Execution.DeploymentExecutor.Run | Engine | 701 | 86.4 | 87 | 116-125, 269-275, 345-348, 384-397, 521-525, 536-538, 572-573, 615-619, 624, 629-632, 664-666, 705-707, 718, 732, 746-748, 768-771, 814-816, 831-833, 852-854, 862-865, 887-890, 896-899, 947-953, 1150-1156 |
| 2 | Engine.Edges.EdgePlanner | Engine | 502 | 86.9 | 82 | 138-139, 197-200, 204-207, 211-214, 407-415, 422-424, 481-482, 588-589, 625-627, 667-696, 709-711, 716-717, 736-737, 749-750, 781-783 |
| 3 | Engine.Planning.DependencyGraph.Builder | Engine | 330 | 84.5 | 77.8 | 191-193, 242-244, 262-263, 300-302, 335-336, 363-366, 370-377, 381-383, 390-392, 404-405, 409-411, 416-417, 429-430, 441-457 |
| 4 | Graph.OwnerOnlyFile | Graph | 102 | 69.6 | 64.7 | 59-61, 90-92, 101-102, 115-141, 175-178 |
| 5 | Providers.PasswordCredentialsProvider | Providers | 375 | 91.7 | 76.2 | 157-158, 163-164, 241-242, 430-433, 542-543, 564-566, 610-628 |
| 6 | Engine.Planning.ResourceExpander | Engine | 244 | 87.3 | 84.9 | 76-83, 107-109, 130-132, 177-180, 207-209, 217-220, 230-232, 253-259 |
| 7 | Engine.Planning.DeploymentPlanner | Engine | 346 | 91.9 | 87.1 | 281-283, 307-318, 356-361, 389-392, 503-505, 509-511 |
| 8 | Providers.GroupsLicensing | Providers | 205 | 87.3 | 80 | 100-106, 129-130, 164-167, 238-239, 243-244, 257-258, 262-263, 289-292, 324, 332 |
| 9 | Graph.HttpGraphClient | Graph | 629 | 96 | 87.6 | 89, 238-239, 247-248, 348-349, 487-489, 523-524, 645-652, 727-728, 732-734, 819, 911-913 |
| 10 | Providers.FederatedIdentityCredentialsProvider | Providers | 294 | 91.8 | 78.4 | 145-147, 164-165, 207-208, 213-214, 224-225, 309-310, 320-322, 357-360, 401-403, 457-459 |
| 11 | Engine.Execution.TemplateDeleter | Engine | 115 | 79.1 | 67.7 | 94-95, 111-113, 145-152, 176-177, 192-193, 197-198, 202-203, 215-216, 226-228 |
| 12 | Expressions.Functions.StringFunctions | Expressions | 653 | 96.3 | 92.5 | 233-241, 247-250, 338, 713, 820-822, 917-918, 953-959 |
| 13 | Engine.Execution.SecureOutputsFile | Engine | 186 | 87.1 | 81 | 41-43, 59-60, 67-68, 75, 86-88, 200-201, 246-254, 270-272 |
| 14 | Engine.Planning.InstanceEvaluator | Engine | 164 | 86 | 86 | 40-41, 55-56, 62-63, 70-71, 99-100, 108-111, 176-179, 227-229, 278-279 |
| 15 | Providers.ConditionalAccessPoliciesProvider | Providers | 164 | 86 | 74.1 | 78, 98-104, 119-120, 180-182, 205-208, 267-268, 302-306 |

Line numbers are of the source at the time of the run; they drift with every edit, so re-run the report instead of trusting them after a change.

## What is still not covered, and why

- **Platform branches.** `Graph.OwnerOnlyFile` lines 115-141 are the Unix path (mode 0600), and the Windows DACL checks are the part a Linux run misses; the tests for each side
  skip on the other platform (`OwnerOnlyFileTests`; CI runs both a Windows and a Linux job).
- **Defensive branches the validator makes unreachable.** `EdgePlanner` 197-214 and 667-696, `DependencyGraph.Builder` 363-457, `ResourceExpander` 76-259 and
  `InstanceEvaluator` 40-111 handle shapes (invalid `copy` forms, unevaluable `condition`, an edge declared on an unsupported type) that `SemanticChecker` and the edge-kind
  registry reject before the planner runs; they can only be reached by hand-building instances.
- **Late failure handling in the executor.** `DeploymentExecutor.Run` 116-125 (the end phase after an unexpected exception) and the other listed ranges are mostly guards for
  cancellation between provider steps, secure-outputs file failures and providers that throw something other than the documented types. The documented failure kinds are covered by the provider failure matrix and
  `Review1A_RunSafetyTests`.
- **`Engine.Execution.TemplateDeleter`**: the refusal and failure branches of `deployment delete` that need a provider failing between the re-read and the delete, or a
  cancellation in the middle of the list; the listing, the guards and the reverse order are covered (`RecreateAndDeleteCommandTests`).
- **`Providers.GroupsLicensing`** 100-332: licence-assignment error mapping; needs a fake SKU catalogue with partially failing `assignLicense` calls (`FakeGraphClient` has no per-call
  failure script for that action yet).
- **Not measurable here:** everything that needs a real Microsoft Graph or a real sign-in is covered only by the gated integration project, which has not been run against a tenant.
- The planner and executor `--parallelism` limit is asserted through the number of concurrent HTTP requests, which `HttpGraphClient`'s own `MaxParallel` also enforces, so a
  mutant that mis-wires only one of the layers is masked by the other (noted in `Review2F_CliWiringTests`).

## Defect history of the first coverage pass

Ten defects were found by the first coverage pass and are all fixed; each has a test that is now a normal test (names without a `Defect_` prefix). In short: unbounded
expression nesting (stack overflow), a state save tied to the caller's cancellation token (the state file is gone since), quadratic and then stack-overflowing secret redaction (now four linear scans),
lone-surrogate escapes crashing the schema pass, order-sensitive array diffing, a non-string `secretVersion` silently dropped, value-dependent `AsInt64`, a duplicated
`Content-Type` header, prefixed `Authorization` names not masked, and the owner-only file keeping its old DACL when it replaced an existing file. A later one, a quoted value assigned to a secret name (`password="x"`) that `SecretRedactor` did not mask, is fixed too and is covered by `Review2G_SecretRedactorQuotedValueTests`. The last one, a string whose bytes are not valid UTF-8 that the loader accepted and a later stage choked on (found by review 4L), is fixed too (strict UTF-8 in `TemplateJson.Parse`): `TemplateInputRobustnessTests.InvalidUtf8InsideAString_IsAnInvalidJsonLoadException_WithTheByteOffset` and `Review5M_Utf8Tests` cover it.

## Known flakiness

None known in plain runs. Wall-clock-dependent tests are the usual source, so they are fenced: a test that asserts an elapsed-time bound carries `[Trait("Category", "Timing")]`
and `test.ps1` runs that group in a second, serial pass (`-SkipTiming` skips it) with bounds that separate "linear" from "quadratic" or "stopped by the timeout" from "ran to the
default limit", not milliseconds; tests that must observe overlapping requests hold each one open for 250-300 ms; the other time proofs use injected clocks, back-dated file stamps
or the evaluation budget's step counter. The suite was run with 28 busy loops competing for the 28 cores of the measuring machine and the previously fragile tests
(`Review1C_BudgetTests` union / intersection, `Review1C_FuzzTests`, the concurrency probes of `Review2F_CliWiringTests`) passed. A coverage
(instrumented) run is slower than a plain one; if a Timing test fails only under `-Coverage`, re-run it plain before suspecting the product. Fixed `Task.Delay` calls that remain
only hold something open for a negative check (a waiter is still waiting, a released lock does not reappear) and cannot fail because the machine is slow.

## Runtime

On the measuring machine (28 cores, warm build) the parallel pass of `pwsh -File test.ps1 -SkipTiming` takes **45-60 seconds** (57 s measured), the serial Timing pass adds about 35-40 s (a plain `test.ps1` is about 95 s), and `-Coverage` (both passes) takes about 110 s. A cold build adds the compile time; fewer cores stretch the parallel pass.
