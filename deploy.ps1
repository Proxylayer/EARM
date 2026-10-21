#Requires -Version 7
<#
.SYNOPSIS
    Packs the earm CLI as a .NET tool package (package id Proxylayer.EARM, command 'earm') and (re)installs it as a global tool from that package.
.DESCRIPTION
    1. dotnet pack src/EArm.Cli -> artifacts/nupkg (previous Proxylayer.EARM.*.nupkg files are removed first). Packed with ContinuousIntegrationBuild and
       DeterministicSourcePaths so no absolute build path is embedded.
    2. dotnet tool uninstall -g Proxylayer.EARM, then dotnet tool uninstall -g earm (the package's former id, which installs the same
       'earm' command, so it has to go before the install; failures ignored: not installed).
    3. dotnet tool install -g Proxylayer.EARM --source artifacts/nupkg --ignore-failed-sources --version <packed version>. The local folder is
       the ONLY source (not --add-source), so a squatted package on nuget.org cannot be picked up (dependency confusion).
    4. Only after a successful install: dotnet tool uninstall -g entraarm (the tool's former name; failure ignored).
    A failed pack or install exits non-zero with the dotnet exit code.
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
$packageId = 'Proxylayer.EARM'
$formerPackageId = 'earm'
$legacyPackageId = 'entraarm'

New-Item -ItemType Directory -Force -Path $nupkgDir | Out-Null
Get-ChildItem -LiteralPath $nupkgDir -Filter "$packageId.*.nupkg" -ErrorAction SilentlyContinue | Remove-Item -Force
Get-ChildItem -LiteralPath $nupkgDir -Filter "$formerPackageId.*.nupkg" -ErrorAction SilentlyContinue | Remove-Item -Force  # former package id
Get-ChildItem -LiteralPath $nupkgDir -Filter 'entraarm.*.nupkg' -ErrorAction SilentlyContinue | Remove-Item -Force  # former package name

# ---- 1. Pack ------------------------------------------------------------------------------------------------
# ContinuousIntegrationBuild + DeterministicSourcePaths map the build directory to /_/ in the PDBs and the compiled paths, so the package embeds no absolute path
# of this machine (and a rebuilt package is byte-identical). They are passed here, not set in Directory.Build.props, so a normal dev build keeps real paths for debugging.
$packArgs = @('pack', $cliProject, '--configuration', $Configuration, '--output', $nupkgDir, '--nologo', '-p:ContinuousIntegrationBuild=true', '-p:DeterministicSourcePaths=true')
if ($Detailed) {
    $packArgs += @('--verbosity', 'normal')
}
else {
    $packArgs += @('--verbosity', 'quiet', '-tl:off', '-clp:ErrorsOnly')
}

Write-Host "PACK       $packageId ($Configuration) -> $nupkgDir"
if ($Detailed) {
    & dotnet @packArgs
}
else {
    # The dotnet CLI appends its own build-summary banner; it is shown only when the pack fails.
    $packOutput = @(& dotnet @packArgs 2>&1 | ForEach-Object { "$_" })
}
if ($LASTEXITCODE -ne 0) {
    if (-not $Detailed) {
        $packOutput | ForEach-Object { Write-Host $_ }
    }
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

# Ignores a failing uninstall (the tool is simply not installed).
function Uninstall-GlobalTool {
    param([Parameter(Mandatory)][string]$ToolId)

    & {
        $ErrorActionPreference = 'Continue'
        if ($Detailed) {
            & dotnet tool uninstall --global $ToolId
        }
        else {
            & dotnet tool uninstall --global $ToolId *> $null
        }
    }
}

# ---- 2. Uninstall the previous install (same package id, or the former id 'earm' with the same command) -----
# The former name 'entraarm' is NOT touched yet: it is only removed after the new tool installed (step 4), so a failed install
# never leaves the machine without any working tool.
Write-Host "UNINSTALL  $packageId (previous global install, if any)"
Uninstall-GlobalTool -ToolId $packageId
# 'earm' was this package's id before Proxylayer.EARM; it installs the same 'earm' command, so the install below would fail while it is present.
Write-Host "UNINSTALL  $formerPackageId (former package id, if any)"
Uninstall-GlobalTool -ToolId $formerPackageId

# ---- 3. Install from the local package ----------------------------------------------------------------------
# --source (NOT --add-source): the package is resolved from the local folder ONLY. With --add-source nuget.org stays a source too, so a
# squatted package of that id there (dependency confusion) could win over the freshly packed one. --ignore-failed-sources keeps an
# unreachable source from failing the install.
$installArgs = @('tool', 'install', '--global', $packageId, '--source', $nupkgDir, '--ignore-failed-sources', '--version', $version)
if (-not $Detailed) {
    $installArgs += @('--verbosity', 'quiet')
}

Write-Host "INSTALL    $packageId $version from $($package.Name)"
& dotnet @installArgs
if ($LASTEXITCODE -ne 0) {
    $installExit = $LASTEXITCODE
    Write-Host "DEPLOY FAIL dotnet tool install exited with code $installExit (the previous $packageId install was already removed; the former '$legacyPackageId' tool, if installed, was left in place)"
    exit $installExit
}

# ---- 4. Remove the legacy tool, only now that the new one is installed -------------------------------------
# 'entraarm' is the tool's former name (renamed to 'earm'); remove it so an upgraded dev machine keeps no stale tool.
Write-Host "UNINSTALL  $legacyPackageId (former tool name, if any)"
Uninstall-GlobalTool -ToolId $legacyPackageId

Write-Host "DEPLOY OK  $packageId $version installed globally; run 'earm --version' to confirm"
exit 0
