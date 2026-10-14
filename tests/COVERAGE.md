# Test coverage

Measured with Microsoft.Testing.Extensions.CodeCoverage (Cobertura) through `test.ps1 -Coverage`, merged per source line by
`tools/Get-CoverageReport.ps1`. Only code under `src/` is counted. Branch figures are conditions x 2 as reported by the collector and
are approximate (the merge keeps the best result per condition across the three test projects, so a condition that one run saw as
50% and another run also saw as 50% stays 50%). The Integration project is gated and is not part of these numbers.

## Regenerate

```powershell
pwsh -File test.ps1 -Coverage                                    # all projects, writes artifacts/test-results/*.cobertura.xml
pwsh -File tools/Get-CoverageReport.ps1 -Top 30                  # per-assembly table + weakest classes
pwsh -File tools/Get-CoverageReport.ps1 -Format Json > cov.json  # every class with its uncovered line ranges
```

The coverage run is slower than a plain run (instrumentation); it is a development-time measurement, not part of the default suite.

## Summary per assembly (before / after this pass)

Before = commit 251d411 as is (2,827 tests). After = 3,837 tests (1,010 added; skipped results are the defect pins below plus 2 pre-existing skips; 0 failing).

| Assembly | Lines | Line % before | Line % after | Branch % before | Branch % after |
|---|---:|---:|---:|---:|---:|
| entraarm (CLI) | 2521 | 91.0 | 96.2 | 82.3 | 89.5 |
| EntraArm.Az | 1759 | 87.0 | 93.9 | 82.5 | 91.7 |
| EntraArm.Core | 2768 | 95.1 | 97.9 | 91.1 | 94.6 |
| EntraArm.Engine | 6558 | 90.8 | 93.1 | 83.8 | 87.1 |
| EntraArm.Expressions | 4146 | 95.8 | 96.9 | 93.0 | 94.3 |
| EntraArm.Providers | 6772 | 83.5 | 95.6 | 74.3 | 86.6 |
| **Total** | 24524 | 89.9 | 95.3 | 82.9 | 89.5 |

Tests per project: EntraArm.Tests 2106 -> 2449 (+343), EntraArm.Providers.Tests 459 -> 979 (+520), EntraArm.Az.Tests 262 -> 409 (+147).

## Top 30 weakest classes (baseline ranking by missed lines, classes with 8 or more lines)

"Untested now" lists the source line ranges still not executed by any test after this pass.

| # | Class | Assembly | Lines | Line % before | Line % after | Branch % before | Branch % after | Untested now (line ranges) |
|---:|---|---|---:|---:|---:|---:|---:|---|
| 1 | Providers.Edges.AppRoleAssignmentEdgeKind | Providers | 234 | 0.9 | 97 | 0 | 87.9 | 131-132, 144-145, 157-160 |
| 2 | Providers.OAuth2PermissionGrantsProvider | Providers | 293 | 53.2 | 97.3 | 38.2 | 85.4 | 59, 204-205, 285-286, 293-294, 302 |
| 3 | Providers.AppRoleAssignmentsProvider | Providers | 234 | 51.3 | 96.6 | 40.7 | 86.4 | 203-210, 244-245 |
| 4 | Providers.RoleAssignmentsProvider | Providers | 235 | 68.5 | 93.6 | 61.1 | 83.3 | 173-174, 199-207, 320-322, 362-364 |
| 5 | Engine.Edges.EdgePlanner | Engine | 509 | 88.4 | 89.6 | 84.4 | 85 | 136-137, 221-224, 228-231, 235-238, 431-439, 446-448, 568-570, 603-604, 634-636, 668-674, 678-684, 708-710, 735-736, 748-749, 780-782 |
| 6 | Providers.PasswordCredentialsProvider | Providers | 303 | 80.9 | 97 | 68.2 | 84.4 | 129-130, 135-136, 206-207, 468-470 |
| 7 | Az.PwshLocator | Az | 159 | 66.7 | 69.8 | 59.4 | 68.8 | 106-107, 120-122, 138-139, 146-189, 202-203 |
| 8 | Engine.Planning.DependencyGraph.Builder | Engine | 330 | 84.5 | 84.5 | 77.8 | 77.8 | 191-193, 242-244, 262-263, 300-302, 335-336, 363-366, 370-377, 381-383, 390-392, 404-405, 409-411, 416-417, 429-430, 441-457 |
| 9 | Engine.Execution.DeploymentExecutor.Run | Engine | 415 | 87.7 | 92 | 84.1 | 89.6 | 117-119, 215-222, 320-323, 376-379, 405-409, 420-422, 447-449, 475, 503-505 |
| 10 | Core.Validation.SemanticChecker | Core | 455 | 89 | 99.3 | 91.4 | 97.9 | 376-378 |
| 11 | Az.AzGraphClient | Az | 237 | 78.9 | 100 | 72 | 99.2 |  |
| 12 | Providers.AuthenticationContextClassReferencesProvider | Providers | 141 | 65.2 | 97.9 | 43.8 | 87.5 | 62, 94-95 |
| 13 | Providers.Edges.DirectoryRoleEdgeKind | Providers | 231 | 78.8 | 98.3 | 63.4 | 97 | 160-161, 171-172 |
| 14 | Engine.Planning.DeploymentPlanner | Engine | 346 | 87.6 | 93.1 | 85.5 | 90.9 | 262-264, 287-298, 439-445, 482-484, 488-490 |
| 15 | Providers.UsersProvider | Providers | 340 | 87.9 | 94.4 | 74.1 | 84.5 | 123, 136-138, 148-149, 264-265, 306-308, 417, 539-540, 544-545, 562-564 |
| 16 | Cli.Commands.ShowCommand | entraarm | 82 | 53.7 | 96.3 | 25 | 91.7 | 108-110 |
| 17 | Engine.Providers.JsonDiff | Engine | 443 | 91.6 | 97.7 | 89.1 | 95.7 | 117-118, 123-124, 316-318, 392-393, 723 |
| 18 | Engine.Planning.ResourceExpander | Engine | 198 | 81.8 | 86.4 | 71.3 | 80.9 | 53-55, 77-79, 111-114, 141-143, 151-154, 163-169, 187-193 |
| 19 | Providers.ApplicationsProvider | Providers | 313 | 88.8 | 92.7 | 79.6 | 83.1 | 88-89, 181-182, 224-225, 251-253, 368-373, 442-445, 457, 473-475 |
| 20 | Providers.GroupsProvider | Providers | 291 | 88 | 94.2 | 79.3 | 86.6 | 148-149, 226-228, 257-258, 288-289, 307-308, 317, 413-415, 432-433 |
| 21 | Expressions.Values.JsonValues | Expressions | 207 | 84.5 | 97.6 | 86.1 | 98.1 | 69-72, 317, 322 |
| 22 | Cli.Commands.DeleteCommand | entraarm | 111 | 71.2 | 91.9 | 50 | 96.7 | 63-66, 76-79, 150 |
| 23 | Engine.Planning.InstanceEvaluator | Engine | 161 | 80.7 | 83.9 | 82.1 | 85.7 | 40-41, 49-51, 55-56, 62-63, 70-71, 93-94, 102-105, 170-173, 221-223, 272-273 |
| 24 | Providers.AdministrativeUnitsProvider | Providers | 203 | 85.2 | 91.1 | 62.2 | 68.9 | 104, 116-117, 182-186, 231-233, 255-258, 316-317, 327-329 |
| 25 | Providers.FederatedIdentityCredentialsProvider | Providers | 232 | 88.4 | 94.4 | 78.6 | 82.5 | 123-124, 129-130, 225-226, 236-238, 349, 386-388 |
| 26 | Engine.Providers.JsonObjects | Engine | 161 | 83.9 | 100 | 75.5 | 100 |  |
| 27 | Providers.GroupsLicensing | Providers | 205 | 87.3 | 87.3 | 80 | 80 | 100-106, 129-130, 164-167, 238-239, 243-244, 257-258, 262-263, 289-292, 324, 332 |
| 28 | Cli.Commands.DeploymentRunner | entraarm | 157 | 83.4 | 94.3 | 76.8 | 83.9 | 147, 153, 199-206 |
| 29 | Providers.NamedLocationsProvider | Providers | 180 | 86.1 | 92.8 | 64.5 | 72.6 | 110-111, 162-164, 248-249, 259-261, 284-286 |
| 30 | Providers.ServicePrincipalsProvider | Providers | 197 | 87.3 | 94.4 | 67.1 | 73.7 | 88-96, 136-137, 164-165 |

## What is still not covered, and why

- `Az.PwshLocator` (about 70%): lines 146-189 probe the machine for a PowerShell executable (registry, well-known install folders,
  `PATH`). Exercising them needs a controlled `PATH` / `ProgramFiles`; mutating process environment variables races the other tests in
  the same host, so these remain manual. The pure parts (version parsing, candidate ordering) are covered.
- `Engine.Edges.EdgePlanner` 221-238 and 668-684: shapes that the offline validator and the edge-kind registry already reject before the
  planner runs; reachable only by hand-building instances. 431-448 is a defensive null branch.
- `Engine.Planning.DependencyGraph.Builder` 363-457, `ResourceExpander` 53-193, `InstanceEvaluator` 40-105: defensive branches for
  expression evaluation errors that are normally caught by `SemanticChecker` first (invalid `copy` shapes, unevaluable `condition`).
  Follow-up: a planner error-pass test class that builds the instances directly.
- `Providers.GroupsLicensing` (lines 100-332): licence-assignment error mapping; needs a fake SKU catalogue with partially failing
  `assignLicense` calls (`FakeGraphClient` has no per-call batch failure script yet).
- `Cli.Commands.ShowCommand` 108-110, `DeleteCommand` 63-79: interactive confirmation paths that need a real console redirect; the
  non-interactive paths are covered.
- `PwshProcess`: timeouts and kill-on-cancel are covered through the fake-az script; OS-specific `ProcessStartInfo` fallbacks are not
  reachable on a single OS.
- Deep recursion: the parser caps nesting at 100 levels (defect 1, fixed), so a 1,200-deep or 5,000-deep call chain is an `ExpressionTooDeep` diagnostic; the tests drive 1,200, 5,000 and hand-built 200,000-deep trees.

## Defects found. Pinned by a test named `Defect_*` while open; the fixed ones are renamed and run as normal tests

Open (still skipped): 3, 6, 7, 8, 9. Fixed: 1, 2, 4, 5, 10 (see "Fixed defects" below the table). Remove the `Skip` of an open one to reproduce.


| # | Severity | Where | Expected | Actual | Test |
|---|---|---|---|---|---|
| 1 (fixed) | high (robustness) | `src/EntraArm.Expressions/Syntax/ExpressionParser.cs` (recursive descent) | a depth diagnostic past a few hundred nested calls | about 1,200 nested function calls overflow the stack and abort the process | `ExpressionParserFuzzTests.Defect_DeeplyNestedCalls_ShouldBeADiagnostic_NotAStackOverflow` |
| 2 (fixed) | medium | `FileStateSink` (state entry save uses the caller token) | the entry of the object written just before a cancellation is saved | state has no resources; the object exists in the tenant but is unrecorded (a generated secret or key id would be orphaned) | `CancellationTests.Defect_ARecordIsKept_ForTheObjectWrittenJustBeforeTheCancellation` |
| 3 | medium-low | `src/EntraArm.Az/SecretRedactor.cs` `RedactText` | linear time | quadratic on long unbroken word-character runs: 16,000 chars take 5+ s | `SecretRedactorPropertyTests.Defect_RedactText_IsQuadraticOnALongUnbrokenToken` |
| 4 (fixed) | low-medium | `TemplateSchemas` schema pass | a diagnostic (InvalidJson / SchemaViolation), exit code 2 | a lone surrogate escape in a template string throws an unhandled InvalidOperationException | `TemplateInputRobustnessTests.Defect_ALoneSurrogateEscape_ShouldBeADiagnostic_NotAnUnhandledException` |
| 5 (fixed) | low-medium | `src/EntraArm.Engine/Providers/JsonDiff.cs:179` `AreEquivalent` (used near `EdgePlanner.cs:677`) | documented order-insensitive arrays compare equal when reordered | reordered arrays differ, so a role list returned by Graph in another order is flagged as modified | `JsonDiffPropertyTests.Defect_AreEquivalent_TreatsAReorderedArrayAsDifferent` |
| 6 | low | `src/EntraArm.Core/ParameterSet.cs:199` `ParseKeyVaultReference` | InvalidParameterFile error (or number taken as text) for a non-string `secretVersion` | silently parsed with `SecretVersion = null`, so the latest secret version is used instead of the pinned one | `ParameterSetShapeTests.Defect_ANumericSecretVersion_ShouldNotBeSilentlyDropped` |
| 7 | low | `src/EntraArm.Core/Json/TemplateJson.cs:104` `AsInt64` | same answer for the same number however it was created | parsed `3.0` / `1.5e3` are non-integral, a CLR double `3.0` is 3 | `TemplateInputRobustnessTests.Defect_AsInt64_ParsedIntegralFloatsAreNotIntegral` |
| 8 | low | `src/EntraArm.Az/AzGraphClient.cs:280` `BuildBatchItem` | one Content-Type header | a caller-supplied lower-case `content-type` is duplicated (`content-type` and `Content-Type`) | `AzGraphClientEdgeTests.Defect_Batch_ACallerContentTypeInAnotherCase_IsDuplicated` |
| 9 | low | `src/EntraArm.Az/SecretRedactor.cs` `IsSecretName` | any name containing `authorization` masks its value | `Proxy-Authorization`, `--authorization=`, `x-ms-authorization` values are left as is | `SecretRedactorPropertyTests.Defect_PrefixedAuthorizationNames_AreNotMasked` |
| 10 (fixed) | medium (Windows) | `src/EntraArm.Engine/Execution/OwnerOnlyFile.cs:53` `WriteWindows` | protected, current-user-only DACL even when replacing an existing file | an existing file keeps its inherited rules, so a secret is written readable by everyone the folder grants | `OwnerOnlyFileTests.Defect_WriteAllText_OnWindows_ReplacingAnExistingFile_KeepsItsOldDacl` |

### Fixed defects (the pinned tests were un-skipped and renamed without the `Defect_` prefix)

| # | Fix | Test now |
|---|---|---|
| 1 | `ExpressionParser.MaxNestingDepth = 100` (calls, indexers, lambda bodies and chained `.p` / `[i]` counted together; error `ExpressionTooDeep` with the offset). `Expression.Render`, `ExpressionEvaluator.Evaluate` / `EvaluateTree`, `ExpressionValidator.Visit` and `ReferenceScanner.Visit` also check `RuntimeHelpers.TryEnsureSufficientExecutionStack` and raise the same code for hand-built trees. | `ExpressionParserFuzzTests.DeeplyNestedCalls_ShouldBeADiagnostic_NotAStackOverflow` |
| 2 | `FileStateSink` saves under its own 30 s bound instead of the caller token; `DeploymentExecutor` passes `CancellationToken.None` for every record of completed work. | `CancellationTests.ARecordIsKept_ForTheObjectWrittenJustBeforeTheCancellation` |
| 4 | `LoneSurrogates` rewrites lone surrogate escapes to U+FFFD at every JSON parse boundary; the template loader reports an `InvalidJson` error with path and position; the schema pass is guarded. | `TemplateInputRobustnessTests.ALoneSurrogateEscape_ShouldBeADiagnostic_NotAnUnhandledException` |
| 5 | `JsonDiff` compares order-insensitive arrays as multisets (already fixed in the working tree before this pass). | `JsonDiffPropertyTests.AreEquivalent_TreatsAReorderedArrayAsEquivalent` |
| 10 | `OwnerOnlyFile` writes a new owner-only temp file next to the target and moves it over the target (the old DACL / mode never applies), verifies the result, and removes the temp file on failure. | `OwnerOnlyFileTests.WriteAllText_OnWindows_ReplacingAnExistingFile_DoesNotKeepItsOldDacl` |

## Known flakiness

- `RunnerHonoursMaxParallelAndServesConcurrentCalls` (pre-existing, Az.Tests) failed once during a coverage run (timing sensitive under
  instrumentation); not seen in plain runs and not caused by the added tests. Every added class was run three times in a row per project
  with identical results.

## Tests added (by project and class)

EntraArm.Az.Tests (+147): AzGraphClientEdgeTests 25, AzInfrastructureEdgeTests 16, AzPreflightAndRequestTests 19,
GraphErrorParserEdgeTests 49, GraphPathPropertyTests 6, SecretRedactorPropertyTests 32.

EntraArm.Providers.Tests (+520): ProviderContractTests 136, ProviderLifecycleContractTests 55, ProviderFailureMatrixTests 44,
RulePropertyTests 36, AppRoleAssignmentScenarioTests 36, PermissionGrantScenarioTests 33, DirectoryRoleEdgeKindUnitTests 20,
ProviderModifyTests 18, RoleAssignmentGapTests 18, AuthenticationContextGapTests 14, CopyLoopDiagnosticsTests 13,
GroupsProviderGapTests 13, UsersProviderGapTests 13, DependsOnDiagnosticsTests 12, PasswordCredentialFailureTests 11,
EdgePlannerErrorTests 9, ProviderDiscoveryErrorTests 9, AppRoleAssignmentPrepareTests 7, ApplyTimeFailureTests 7,
RecordedEdgesTests 7, PlannerReadFailureTests 5, CancellationTests 4. Helpers: Infrastructure/InstanceFactory, BodylessPostClient,
InterceptingGraphClient.

EntraArm.Tests (+343): ParameterSetShapeTests 48, SemanticCheckerShapeTests 46, TemplateInputRobustnessTests 41, StateRobustnessTests 35,
JsonValuesTests 34, JsonObjectsTests 28, CanonicalJsonPropertyTests 26, ExpressionParserFuzzTests 17, OutputWriterTests 13,
DeploymentExitCodeTests 12, OwnerOnlyFileTests 11, JsonDiffPropertyTests 10, CliArgumentErrorTests 9, StateRefreshAndDeleteCommandTests 7,
FunctionArgumentFuzzTests 6. Shared: tests/EntraArm.TestSupport/JsonGenerator.cs (seeded generated data, no new package).

## Runtime

Full plain run (`pwsh -File test.ps1`, warm build): about 2m20s now against about 2m02s before (+18 s, under the 60 s budget).
