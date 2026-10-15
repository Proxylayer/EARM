#Requires -Version 7
<#
.SYNOPSIS
    Builds the EARM solution, or a single project, with dotnet build.
.DESCRIPTION
    Simple mode (default) prints only errors plus a one-line BUILD OK / BUILD FAIL summary.
    -Detailed switches to normal MSBuild verbosity for diagnosing a failure.
    -Target builds one project: a project name such as EArm.Core (looked up under src/ and tests/)
    or a path to a .csproj.
    The dotnet build exit code is propagated.
.EXAMPLE
    pwsh -File build.ps1
.EXAMPLE
    pwsh -File build.ps1 -Target EArm.Cli -Configuration Release
.EXAMPLE
    pwsh -File build.ps1 -Detailed
#>
[CmdletBinding()]
param(
    [switch]$Detailed,
    [string]$Target,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$env:DOTNET_NOLOGO = '1'

$repoRoot = $PSScriptRoot

function Resolve-ProjectPath {
    param([Parameter(Mandatory)][string]$Name)

    if (Test-Path -LiteralPath $Name -PathType Leaf) {
        return (Resolve-Path -LiteralPath $Name).Path
    }

    # Accept a bare project name (EArm.Core), a project directory, or a .csproj path.
    # Only a trailing ".csproj" is stripped; dotted name segments such as ".Tests" are part of the name.
    $baseName = Split-Path -Path $Name -Leaf
    if ($baseName -like '*.csproj') {
        $baseName = $baseName.Substring(0, $baseName.Length - '.csproj'.Length)
    }

    foreach ($dir in @('src', 'tests')) {
        $candidate = Join-Path $repoRoot $dir $baseName "$baseName.csproj"
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
    }

    return $null
}

if ($Target) {
    $buildTarget = Resolve-ProjectPath -Name $Target
    if (-not $buildTarget) {
        Write-Host "BUILD FAIL project '$Target' not found: expected src/<name>/<name>.csproj or tests/<name>/<name>.csproj"
        exit 2
    }
}
else {
    $buildTarget = Join-Path $repoRoot 'EArm.slnx'
}
$label = [System.IO.Path]::GetFileNameWithoutExtension($buildTarget)

$dotnetArgs = @('build', $buildTarget, '--configuration', $Configuration, '--nologo')
if ($Detailed) {
    $dotnetArgs += @('--verbosity', 'normal')
}
else {
    $dotnetArgs += @('--verbosity', 'quiet', '-tl:off', '-clp:ErrorsOnly')
}

# Simple mode captures the output: the dotnet CLI always appends its own 'Build succeeded. 0 Warning(s)' banner (it forces -clp:Summary after
# user arguments), so it is shown only when the build fails and the one-line result below is the only line on success.
if ($Detailed) {
    & dotnet @dotnetArgs
}
else {
    $buildOutput = @(& dotnet @dotnetArgs 2>&1 | ForEach-Object { "$_" })
}
$exitCode = $LASTEXITCODE
if (-not $Detailed -and $exitCode -ne 0) {
    $buildOutput | ForEach-Object { Write-Host $_ }
}

if ($exitCode -eq 0) {
    Write-Host "BUILD OK   $label ($Configuration)"
}
else {
    Write-Host "BUILD FAIL $label ($Configuration) - dotnet build exited with code $exitCode"
}

exit $exitCode
