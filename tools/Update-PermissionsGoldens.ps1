#Requires -Version 7
<#
.SYNOPSIS
    Regenerates the permissions golden files in tests/fixtures/permissions from the current 'earm permissions' output.
.DESCRIPTION
    Sets EARM_UPDATE_GOLDENS=1 and runs the golden tests (EArm.Tests, PermissionsGoldenTests): in that mode every case (sample x app/delegated x
    table/json) rewrites <sample>.<mode>.txt / <sample>.<mode>.json (UTF-8 without BOM, LF, trailing blanks trimmed) instead of comparing it.
    Run it only after an intentional change to the permission manifest or its rendering, read the resulting diff, then run test.ps1 normally
    to confirm the goldens now match. The samples and their parameter files are never touched. A new sample needs its row in
    PermissionsGoldenTests.Cases() first; this script then creates its four files.
.EXAMPLE
    pwsh -File tools/Update-PermissionsGoldens.ps1
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$env:EARM_UPDATE_GOLDENS = '1'
try {
    & pwsh -NoProfile -File (Join-Path $repoRoot 'test.ps1') -Target EArm.Tests -Filter 'FullyQualifiedName~PermissionsGoldenTests'
    $exitCode = $LASTEXITCODE
}
finally {
    Remove-Item Env:\EARM_UPDATE_GOLDENS -ErrorAction SilentlyContinue
}

if ($exitCode -eq 0) {
    Write-Host 'Golden files rewritten under tests/fixtures/permissions. Review the diff, then run: pwsh -File test.ps1'
}
exit $exitCode
