#Requires -Version 7
<#
.SYNOPSIS
    Regenerates the what-if golden files in tests/fixtures/whatif from the current engine output.
.DESCRIPTION
    Sets EARM_UPDATE_GOLDENS=1 and runs the golden tests (EArm.Tests, WhatIfGoldenTests): in that mode the Text_ and Json_
    tests rewrite <scenario>.expected.txt / <scenario>.expected.json (UTF-8 without BOM, LF) instead of comparing them.
    Run it only after an intentional change to the what-if rendering, read the resulting diff, then run test.ps1 normally
    to confirm the goldens now match. The templates, seeds, states and scenarios.json are hand-written and never touched.
.EXAMPLE
    pwsh -File tools/Update-WhatIfGoldens.ps1
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$env:EARM_UPDATE_GOLDENS = '1'
try {
    & pwsh -NoProfile -File (Join-Path $repoRoot 'test.ps1') -Target EArm.Tests -Filter 'FullyQualifiedName~WhatIfGoldenTests'
    $exitCode = $LASTEXITCODE
}
finally {
    Remove-Item Env:\EARM_UPDATE_GOLDENS -ErrorAction SilentlyContinue
}

if ($exitCode -eq 0) {
    Write-Host 'Golden files rewritten under tests/fixtures/whatif. Review the diff, then run: pwsh -File test.ps1'
}
exit $exitCode
