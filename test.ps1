#Requires -Version 7
<#
.SYNOPSIS
    Runs the EARM xUnit test projects headless with dotnet test, writing TRX results to artifacts/test-results.
.DESCRIPTION
    dotnet test runs in Microsoft.Testing.Platform mode (opted in via global.json "test.runner"; xunit.v3 4.x no
    longer supports the VSTest path on the .NET 10 SDK). The script builds once (solution, or the single -Target
    project), then runs each selected test project with dotnet test --project ... --no-build.
    Simple mode (default) prints one PASS / FAIL / NO TESTS / SKIPPED / GATED line per project plus the names of failing tests;
    GATED means a project was not run (the integration project without EARM_IT_TENANT), SKIPPED that it ran but every test skipped
    (for example the integration project named explicitly without a tenant): neither is a pass;
    -Detailed streams the full dotnet test output (--output Detailed) instead.
    -Target runs one test project: a project name such as EArm.Tests, or a path to a .csproj.
    -Filter is passed through as the VSTest-syntax filter expression supported by xunit.v3
    (for example "FullyQualifiedName~CliSmokeTests"). Test namespaces do not follow the folders (EArm.Tests.Core is not the Core folder), so
    filter on class names (FullyQualifiedName~Review1C_BudgetTests) or traits (Category=Timing).
    EArm.Integration.Tests (real tenant) is skipped unless the EARM_IT_TENANT environment variable is
    set, or it is named explicitly via -Target.
    -Coverage adds Microsoft.Testing.Extensions.CodeCoverage collection and writes one Cobertura XML per project to
    artifacts/test-results/<project>.cobertura.xml (dev-time measurement; see tests/COVERAGE.md).
    A project with zero tests is reported as NO TESTS and does not fail the run (MTP exit code 8 is ignored); but a -Filter that matches no test in any
    selected project exits 3 with a message (a silent green run for a typo is worse than a failure).
    Tests that assert wall-clock bounds carry [Trait("Category", "Timing")]. Without -Filter, each project runs everything else in parallel first,
    then the Timing tests alone and serially (reported as "<project> [timing, serial]"), so the rest of the suite cannot distort them.
    A project with no [Trait("Category", "Timing")] in its sources gets no second pass (its process start would be wasted).
    -SkipTiming skips that second pass (useful on an overloaded machine or while iterating). With -Filter the expression is used as is, in one pass.
    -NoBuild skips the build step and runs the binaries already built (after build.ps1 or a previous test.ps1 run, same -Configuration).
    Iterating on one area: build once, then pwsh -File test.ps1 -NoBuild -SkipTiming -Target EArm.Tests -Filter "FullyQualifiedName~MyTests".
    Each result line ends with the wall-clock seconds of its pass; the last line is the total.
    Exit code is non-zero (3 for an empty -Filter match) when the build fails or any selected project has failing tests.
.EXAMPLE
    pwsh -File test.ps1
.EXAMPLE
    pwsh -File test.ps1 -Target EArm.Tests -Filter "FullyQualifiedName~CliSmokeTests"
.EXAMPLE
    pwsh -File test.ps1 -Detailed
.EXAMPLE
    pwsh -File test.ps1 -NoBuild -SkipTiming -Target EArm.Tests -Filter "FullyQualifiedName~Review4K_SecretsTests"
#>
[CmdletBinding()]
param(
    [switch]$Detailed,
    [string]$Target,
    [string]$Filter,
    [switch]$Coverage,
    [switch]$SkipTiming,
    [switch]$NoBuild,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$env:DOTNET_NOLOGO = '1'

$repoRoot = $PSScriptRoot
$testsDir = Join-Path $repoRoot 'tests'
$resultsDir = Join-Path $repoRoot 'artifacts' 'test-results'
$integrationProjectName = 'EArm.Integration.Tests'
$integrationGateVariable = 'EARM_IT_TENANT'
$zeroTestsExitCode = 8
$runClock = [System.Diagnostics.Stopwatch]::StartNew()

function Resolve-TestProjectPath {
    param([Parameter(Mandatory)][string]$Name)

    if (Test-Path -LiteralPath $Name -PathType Leaf) {
        return (Resolve-Path -LiteralPath $Name).Path
    }

    # Accept a bare project name (EArm.Tests), a project directory, or a .csproj path.
    # Only a trailing ".csproj" is stripped; dotted name segments such as ".Tests" are part of the name.
    $baseName = Split-Path -Path $Name -Leaf
    if ($baseName -like '*.csproj') {
        $baseName = $baseName.Substring(0, $baseName.Length - '.csproj'.Length)
    }

    $candidate = Join-Path $testsDir $baseName "$baseName.csproj"
    if (Test-Path -LiteralPath $candidate -PathType Leaf) {
        return $candidate
    }

    return $null
}

function Test-IsSupportProject {
    param([Parameter(Mandatory)][string]$ProjectPath)

    return (Get-Content -LiteralPath $ProjectPath -Raw) -match '<IsTestProject>\s*false\s*</IsTestProject>'
}
# True when the project's sources tag at least one test [Trait("Category", "Timing")]: only those projects get the serial Timing pass.
function Test-HasTimingTests {
    param([Parameter(Mandatory)][string]$ProjectPath)

    $projectDir = Split-Path -Parent $ProjectPath
    $sources = Get-ChildItem -LiteralPath $projectDir -Recurse -File -Filter '*.cs' |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
    foreach ($source in $sources) {
        if (Select-String -LiteralPath $source.FullName -SimpleMatch -Pattern '[Trait("Category", "Timing")]' -Quiet) {
            return $true
        }
    }
    return $false
}

function Get-SummaryCount {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Label
    )

    $match = [regex]::Match($Text, "(?m)^\s*$Label\s*:\s*(\d+)\s*$")
    if ($match.Success) {
        return [int]$match.Groups[1].Value
    }
    return -1
}

# ---- Select the test projects -------------------------------------------------------------------------------
if ($Target) {
    $resolved = Resolve-TestProjectPath -Name $Target
    if (-not $resolved) {
        Write-Host "TEST FAIL  test project '$Target' not found: expected tests/<name>/<name>.csproj"
        exit 2
    }
    $projects = @($resolved)
    $buildTarget = $resolved
}
else {
    $candidates = @(
        Get-ChildItem -LiteralPath $testsDir -Directory |
            ForEach-Object { Join-Path $_.FullName "$($_.Name).csproj" } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
            Sort-Object
    )

    # A project that declares <IsTestProject>false</IsTestProject> (a support library such as EArm.TestSupport) is
    # built as a reference, never run.
    $projects = @()
    foreach ($candidate in $candidates) {
        if (Test-IsSupportProject -ProjectPath $candidate) {
            Write-Host "SKIP       $([System.IO.Path]::GetFileNameWithoutExtension($candidate)) (support library)"
        }
        else {
            $projects += $candidate
        }
    }

    $gateValue = [System.Environment]::GetEnvironmentVariable($integrationGateVariable)
    if ([string]::IsNullOrWhiteSpace($gateValue)) {
        $projects = @($projects | Where-Object { [System.IO.Path]::GetFileNameWithoutExtension($_) -ne $integrationProjectName })
        Write-Host "GATED      $integrationProjectName (not run: $integrationGateVariable is not set; it writes to a real dev tenant, see tests/EArm.Integration.Tests/README.md)"
    }

    $buildTarget = Join-Path $repoRoot 'EArm.slnx'
}

if ($projects.Count -eq 0) {
    Write-Host 'TEST FAIL  no test projects selected'
    exit 1
}

New-Item -ItemType Directory -Force -Path $resultsDir | Out-Null

# ---- Build once ---------------------------------------------------------------------------------------------
$buildArgs = @('build', $buildTarget, '--configuration', $Configuration, '--nologo')
if ($Detailed) {
    $buildArgs += @('--verbosity', 'normal')
}
else {
    $buildArgs += @('--verbosity', 'quiet', '-tl:off', '-clp:ErrorsOnly')
}

# Simple mode captures the build output (the dotnet CLI always appends a 'Build succeeded. 0 Warning(s)' banner) and shows it only on failure.
# -NoBuild skips this step: the test binaries of the last build are run as they are (dotnet test fails if they are missing).
if ($NoBuild) {
    Write-Host "NO BUILD   running the binaries already built ($Configuration)"
}
else {
    $buildClock = [System.Diagnostics.Stopwatch]::StartNew()
    if ($Detailed) {
        & dotnet @buildArgs
    }
    else {
        $buildOutput = @(& dotnet @buildArgs 2>&1 | ForEach-Object { "$_" })
    }
    if ($LASTEXITCODE -ne 0) {
        if (-not $Detailed) {
            $buildOutput | ForEach-Object { Write-Host $_ }
        }
        Write-Host "TEST FAIL  build of $([System.IO.Path]::GetFileName($buildTarget)) exited with code $LASTEXITCODE"
        exit $LASTEXITCODE
    }
    Write-Host ("BUILD OK   {0:N1} s" -f $buildClock.Elapsed.TotalSeconds)
}

# ---- Run each project ---------------------------------------------------------------------------------------
# One "pass" is one dotnet test invocation of one project. Without -Filter every project runs two passes: everything except the
# tests tagged [Trait("Category", "Timing")] in parallel, then the Timing tests alone and serially (--parallel none), so a wall-clock
# bound is not distorted by the rest of the suite. -SkipTiming drops the second pass. With -Filter the expression is passed through
# as is, in one pass (a VSTest filter cannot be combined with the trait filters).
$timingTrait = 'Category=Timing'

function Invoke-TestPass {
    param(
        [Parameter(Mandatory)][string]$Project,
        [Parameter(Mandatory)][string]$Label,
        [Parameter(Mandatory)][string[]]$Selection,
        [Parameter(Mandatory)][string]$ArtifactName,
        [switch]$QuietWhenEmpty
    )

    $projectName = [System.IO.Path]::GetFileNameWithoutExtension($Project)

    # dotnet test options first, then "--", then options forwarded to the test application (MTP + xunit.v3).
    $testArgs = @(
        'test',
        '--project', $Project,
        '--configuration', $Configuration,
        '--no-build',
        '--results-directory', $resultsDir
    )
    if ($Detailed) {
        $testArgs += @('--verbosity', 'normal', '--output', 'Detailed')
    }
    else {
        $testArgs += @('--verbosity', 'quiet', '--no-progress', '--no-ansi')
    }

    $testArgs += @(
        '--',
        '--report-xunit-trx',
        '--report-xunit-trx-filename', "$ArtifactName.trx",
        '--ignore-exit-code', "$zeroTestsExitCode"
    )
    $testArgs += $Selection
    if ($Coverage) {
        $testArgs += @('--coverage', '--coverage-output-format', 'cobertura', '--coverage-output', "$ArtifactName.cobertura.xml")
    }

    $passClock = [System.Diagnostics.Stopwatch]::StartNew()
    if ($Detailed) {
        Write-Host "---- $Label ----"
        $lines = @(& dotnet @testArgs 2>&1 | ForEach-Object { Write-Host "$_"; "$_" })
    }
    else {
        $lines = @(& dotnet @testArgs 2>&1 | ForEach-Object { "$_" })
    }
    $exitCode = $LASTEXITCODE
    $seconds = '({0:N1} s)' -f $passClock.Elapsed.TotalSeconds
    $text = $lines -join "`n"

    $total = Get-SummaryCount -Text $text -Label 'total'
    $failed = Get-SummaryCount -Text $text -Label 'failed'
    $succeeded = Get-SummaryCount -Text $text -Label 'succeeded'
    $skipped = Get-SummaryCount -Text $text -Label 'skipped'

    if ($total -lt 0) {
        if (-not $Detailed) {
            Write-Host "FAIL       $Label produced no test summary (exit $exitCode)"
            $lines | Select-Object -Last 20 | ForEach-Object { Write-Host "           $_" }
        }
        if ($exitCode -eq 0) {
            $exitCode = 1
        }
    }
    elseif ($total -eq 0) {
        if (-not $QuietWhenEmpty -and -not $Detailed) {
            Write-Host "NO TESTS   $Label $seconds"
        }
    }
    elseif ($Detailed) {
        # The full output was streamed above; only the exit code and the counts matter here.
        if ($failed -gt 0 -and $exitCode -eq 0) {
            $exitCode = 1
        }
    }
    elseif ($failed -eq 0 -and $exitCode -eq 0 -and $skipped -ge $total) {
        # Every test skipped: nothing was verified, so this must not look like a pass.
        $reason = if ($projectName -eq $integrationProjectName -and [string]::IsNullOrWhiteSpace([System.Environment]::GetEnvironmentVariable($integrationGateVariable))) {
            "gated by $integrationGateVariable (not set): $skipped test(s) skipped, nothing was verified against a tenant"
        }
        else {
            "all $skipped test(s) skipped, nothing was verified"
        }
        Write-Host "SKIPPED    $Label $reason $seconds"
    }
    elseif ($failed -eq 0 -and $exitCode -eq 0) {
        Write-Host "PASS       $Label $succeeded/$total ($skipped skipped) $seconds"
    }
    else {
        Write-Host "FAIL       $Label $failed failed, $succeeded passed, $skipped skipped of $total (exit $exitCode) $seconds"
        $lines | Select-String -Pattern '^\s*failed\s+\S+' | ForEach-Object { Write-Host "           $($_.Line.Trim())" }
        $lines | Select-String -Pattern 'Assert\.|Expected:|Actual:|Exception|error' | Select-Object -First 20 |
            ForEach-Object { Write-Host "           $($_.Line.Trim())" }
        if ($exitCode -eq 0) {
            $exitCode = 1
        }
    }

    return [pscustomobject]@{ ExitCode = $exitCode; Total = [Math]::Max($total, 0) }
}

$overallExitCode = 0
$selectedTests = 0

foreach ($project in $projects) {
    $projectName = [System.IO.Path]::GetFileNameWithoutExtension($project)

    if ($Filter) {
        $passes = @(
            @{ Label = $projectName; Selection = @('--filter', $Filter); Artifact = $projectName; Quiet = $false }
        )
    }
    else {
        $passes = @(
            @{ Label = $projectName; Selection = @('--filter-not-trait', $timingTrait); Artifact = $projectName; Quiet = $false }
        )
        # A project without a single Timing test gets no second pass: it would only pay for another process start.
        if (-not $SkipTiming -and (Test-HasTimingTests -ProjectPath $project)) {
            $passes += @{ Label = "$projectName [timing, serial]"; Selection = @('--filter-trait', $timingTrait, '--parallel', 'none'); Artifact = "$projectName.timing"; Quiet = $true }
        }
    }

    foreach ($pass in $passes) {
        $outcome = Invoke-TestPass -Project $project -Label $pass.Label -Selection $pass.Selection -ArtifactName $pass.Artifact -QuietWhenEmpty:$pass.Quiet
        $selectedTests += $outcome.Total
        if ($outcome.ExitCode -ne 0) {
            $overallExitCode = $outcome.ExitCode
        }
    }
}

Write-Host "TRX        $resultsDir"
Write-Host ("TIME       {0:N1} s" -f $runClock.Elapsed.TotalSeconds)

# A -Filter that matches no test in any selected project is a mistake (a typo, or a namespace filter: test namespaces do not follow the folders, see
# docs/testing.md), not a green run.
if ($Filter -and $selectedTests -eq 0 -and $overallExitCode -eq 0) {
    Write-Host "NO TESTS MATCHED the filter '$Filter' in any selected project: nothing was run. Check the expression (FullyQualifiedName~Part, DisplayName~Part, Category=Timing) and see docs/testing.md."
    exit 3
}

exit $overallExitCode