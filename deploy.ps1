#Requires -Version 7
<#
.SYNOPSIS
    Packs the entraarm CLI as a .NET tool package and (re)installs it as a global tool from that package.
.DESCRIPTION
    1. dotnet pack src/EntraArm.Cli -> artifacts/nupkg (previous entraarm.*.nupkg files are removed first).
    2. dotnet tool uninstall -g entraarm (failure ignored: the tool may not be installed yet).
    3. dotnet tool install -g entraarm --add-source artifacts/nupkg --version <packed version>.
    -Detailed shows full dotnet output; simple mode prints only errors plus one line per step.
    After a successful run, "entraarm --version" works from any directory.
.EXAMPLE
    pwsh -File deploy.ps1
.EXAMPLE
    pwsh -File deploy.ps1 -Detailed
#>
[CmdletBinding()]
param(
    [switch]$Detailed,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$env:DOTNET_NOLOGO = '1'

$repoRoot = $PSScriptRoot
$cliProject = Join-Path $repoRoot 'src' 'EntraArm.Cli' 'EntraArm.Cli.csproj'
$nupkgDir = Join-Path $repoRoot 'artifacts' 'nupkg'
$packageId = 'entraarm'

New-Item -ItemType Directory -Force -Path $nupkgDir | Out-Null
Get-ChildItem -LiteralPath $nupkgDir -Filter "$packageId.*.nupkg" -ErrorAction SilentlyContinue | Remove-Item -Force

# ---- 1. Pack ------------------------------------------------------------------------------------------------
$packArgs = @('pack', $cliProject, '--configuration', $Configuration, '--output', $nupkgDir, '--nologo')
if ($Detailed) {
    $packArgs += @('--verbosity', 'normal')
}
else {
    $packArgs += @('--verbosity', 'quiet', '-clp:ErrorsOnly')
}

Write-Host "PACK       $packageId ($Configuration) -> $nupkgDir"
& dotnet @packArgs
if ($LASTEXITCODE -ne 0) {
    Write-Host "DEPLOY FAIL dotnet pack exited with code $LASTEXITCODE"
    exit $LASTEXITCODE
}

$package = Get-ChildItem -LiteralPath $nupkgDir -Filter "$packageId.*.nupkg" |
    Sort-Object -Property LastWriteTime -Descending |
    Select-Object -First 1
if (-not $package) {
    Write-Host "DEPLOY FAIL no $packageId.*.nupkg was produced in $nupkgDir"
    exit 1
}
$version = $package.BaseName.Substring($packageId.Length + 1)

# ---- 2. Uninstall the previous global install (ignored when not installed) ----------------------------------
Write-Host "UNINSTALL  $packageId (previous global install, if any)"
& {
    $ErrorActionPreference = 'Continue'
    if ($Detailed) {
        & dotnet tool uninstall --global $packageId
    }
    else {
        & dotnet tool uninstall --global $packageId *> $null
    }
}

# ---- 3. Install from the local package ----------------------------------------------------------------------
$installArgs = @('tool', 'install', '--global', $packageId, '--add-source', $nupkgDir, '--version', $version)
if (-not $Detailed) {
    $installArgs += @('--verbosity', 'quiet')
}

Write-Host "INSTALL    $packageId $version from $($package.Name)"
& dotnet @installArgs
if ($LASTEXITCODE -ne 0) {
    Write-Host "DEPLOY FAIL dotnet tool install exited with code $LASTEXITCODE"
    exit $LASTEXITCODE
}

Write-Host "DEPLOY OK  $packageId $version installed globally; run 'entraarm --version' to confirm"
exit 0
