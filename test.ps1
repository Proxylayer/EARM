#Requires -Version 7
<#
.SYNOPSIS
    Runs the EntraARM xUnit test projects headless with dotnet test, writing TRX results to artifacts/test-results.
.DESCRIPTION
    dotnet test runs in Microsoft.Testing.Platform mode (opted in via global.json "test.runner"; xunit.v3 4.x no
    longer supports the VSTest path on the .NET 10 SDK). The script builds once (solution, or the single -Target
    project), then runs each selected test project with dotnet test --project ... --no-build.
    Simple mode (default) prints one PASS / FAIL / NO TESTS line per project plus the names of failing tests;
    -Detailed streams the full dotnet test output (--output Detailed) instead.
    -Target runs one test project: a project name such as EntraArm.Tests, or a path to a .csproj.
    -Filter is passed through as the VSTest-syntax filter expression supported by xunit.v3
    (for example "FullyQualifiedName~CliSmokeTests").
    EntraArm.Integration.Tests (real tenant) is skipped unless the ENTRAARM_IT_TENANT environment variable is
    set, or it is named explicitly via -Target.
    -Coverage adds Microsoft.Testing.Extensions.CodeCoverage collection and writes one Cobertura XML per project to
    artifacts/test-results/<project>.cobertura.xml (dev-time measurement; see tests/COVERAGE.md).
    A project with zero tests is reported as NO TESTS and does not fail the run (MTP exit code 8 is ignored).
    Exit code is non-zero when the build fails or any selected project has failing tests.
.EXAMPLE
    pwsh -File test.ps1
.EXAMPLE
    pwsh -File test.ps1 -Target EntraArm.Tests -Filter "FullyQualifiedName~CliSmokeTests"
.EXAMPLE
    pwsh -File test.ps1 -Detailed
#>
[CmdletBinding()]
param(
    [switch]$Detailed,
    [string]$Target,
    [string]$Filter,
    [switch]$Coverage,
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
$integrationProjectName = 'EntraArm.Integration.Tests'
$integrationGateVariable = 'ENTRAARM_IT_TENANT'
$zeroTestsExitCode = 8

function Resolve-TestProjectPath {
    param([Parameter(Mandatory)][string]$Name)

    if (Test-Path -LiteralPath $Name -PathType Leaf) {
        return (Resolve-Path -LiteralPath $Name).Path
    }

    # Accept a bare project name (EntraArm.Tests), a project directory, or a .csproj path.
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

    # A project that declares <IsTestProject>false</IsTestProject> (a support library such as EntraArm.TestSupport) is
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
        Write-Host "SKIP       $integrationProjectName ($integrationGateVariable is not set)"
    }

    $buildTarget = Join-Path $repoRoot 'EntraArm.slnx'
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
    $buildArgs += @('--verbosity', 'quiet', '-clp:ErrorsOnly')
}

& dotnet @buildArgs
if ($LASTEXITCODE -ne 0) {
    Write-Host "TEST FAIL  build of $([System.IO.Path]::GetFileName($buildTarget)) exited with code $LASTEXITCODE"
    exit $LASTEXITCODE
}

# ---- Run each project ---------------------------------------------------------------------------------------
$overallExitCode = 0

foreach ($project in $projects) {
    $projectName = [System.IO.Path]::GetFileNameWithoutExtension($project)

    # dotnet test options first, then "--", then options forwarded to the test application (MTP + xunit.v3).
    $testArgs = @(
        'test',
        '--project', $project,
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
        '--report-xunit-trx-filename', "$projectName.trx",
        '--ignore-exit-code', "$zeroTestsExitCode"
    )
    if ($Filter) {
        $testArgs += @('--filter', $Filter)
    }
    if ($Coverage) {
        $testArgs += @('--coverage', '--coverage-output-format', 'cobertura', '--coverage-output', "$projectName.cobertura.xml")
    }

    if ($Detailed) {
        Write-Host "---- $projectName ----"
        & dotnet @testArgs
        $exitCode = $LASTEXITCODE
        if ($exitCode -ne 0) {
            $overallExitCode = $exitCode
        }
        continue
    }

    $lines = @(& dotnet @testArgs 2>&1 | ForEach-Object { "$_" })
    $exitCode = $LASTEXITCODE
    $text = $lines -join "`n"

    $total = Get-SummaryCount -Text $text -Label 'total'
    $failed = Get-SummaryCount -Text $text -Label 'failed'
    $succeeded = Get-SummaryCount -Text $text -Label 'succeeded'
    $skipped = Get-SummaryCount -Text $text -Label 'skipped'

    if ($total -lt 0) {
        Write-Host "FAIL       $projectName produced no test summary (exit $exitCode)"
        $lines | Select-Object -Last 20 | ForEach-Object { Write-Host "           $_" }
        if ($exitCode -eq 0) {
            $exitCode = 1
        }
    }
    elseif ($total -eq 0) {
        Write-Host "NO TESTS   $projectName"
    }
    elseif ($failed -eq 0 -and $exitCode -eq 0) {
        Write-Host "PASS       $projectName $succeeded/$total ($skipped skipped)"
    }
    else {
        Write-Host "FAIL       $projectName $failed failed, $succeeded passed, $skipped skipped of $total (exit $exitCode)"
        $lines | Select-String -Pattern '^\s*failed\s+\S+' | ForEach-Object { Write-Host "           $($_.Line.Trim())" }
        $lines | Select-String -Pattern 'Assert\.|Expected:|Actual:|Exception|error' | Select-Object -First 20 |
            ForEach-Object { Write-Host "           $($_.Line.Trim())" }
        if ($exitCode -eq 0) {
            $exitCode = 1
        }
    }

    if ($exitCode -ne 0) {
        $overallExitCode = $exitCode
    }
}

Write-Host "TRX        $resultsDir"
exit $overallExitCode
