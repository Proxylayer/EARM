#Requires -Version 7
<#
.SYNOPSIS
    Packs the earm CLI as a .NET tool package and (re)installs it as a global tool from that package.
.DESCRIPTION
    1. dotnet pack src/EArm.Cli -> artifacts/nupkg (previous earm.*.nupkg files are removed first).
    2. dotnet tool uninstall -g entraarm (the tool's former name) and earm (failures ignored: not installed yet).
    3. dotnet tool install -g earm --add-source artifacts/nupkg --version <packed version>.
    -Detailed shows full dotnet output; simple mode prints only errors plus one line per step.
    After a successful run, "earm --version" works from any directory.
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
$cliProject = Join-Path $repoRoot 'src' 'EArm.Cli' 'EArm.Cli.csproj'
$nupkgDir = Join-Path $repoRoot 'artifacts' 'nupkg'
$packageId = 'earm'

New-Item -ItemType Directory -Force -Path $nupkgDir | Out-Null
Get-ChildItem -LiteralPath $nupkgDir -Filter "$packageId.*.nupkg" -ErrorAction SilentlyContinue | Remove-Item -Force
Get-ChildItem -LiteralPath $nupkgDir -Filter 'entraarm.*.nupkg' -ErrorAction SilentlyContinue | Remove-Item -Force  # former package name

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

# ---- 2. Uninstall the previous global installs (ignored when not installed) --------------------------------
# 'entraarm' is the tool's former name (renamed to 'earm'); remove it so an upgraded dev machine keeps no stale tool.
$legacyPackageId = 'entraarm'
foreach ($toolId in @($legacyPackageId, $packageId)) {
    Write-Host "UNINSTALL  $toolId (previous global install, if any)"
    & {
        $ErrorActionPreference = 'Continue'
        if ($Detailed) {
            & dotnet tool uninstall --global $toolId
        }
        else {
            & dotnet tool uninstall --global $toolId *> $null
        }
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

Write-Host "DEPLOY OK  $packageId $version installed globally; run 'earm --version' to confirm"
exit 0
