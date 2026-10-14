#Requires -Version 7.4
<#
.SYNOPSIS
    Fake Azure CLI for the EntraArm.Az contract tests. The contract is tests/fake-az/README.md.
.DESCRIPTION
    Invoke-AzRest.ps1 (embedded in EntraArm.Az) runs this file in-process as "& fake-az.ps1 @azArgs", so there is
    deliberately no param() block: every az argv element arrives, in order, in $args.

    Per invocation the fake
      1. reads the body file named by "--body @<path>" immediately (the runner zeroes and deletes it right after),
      2. optionally hard-links the request/body/output files into ENTRAARM_FAKE_AZ_LINK_DIR (lets a test verify the
         runner zeroed them before deleting them),
      3. appends one compact JSON line to ENTRAARM_FAKE_AZ_ARGV_FILE: {"argv":[...],"bodyFile":...,"body":...,"env":{...},"cwd":"..."},
      4. replays the first unconsumed response of ENTRAARM_FAKE_AZ_FIXTURE whose "match" fits the call, remembering
         consumed response indexes in ENTRAARM_FAKE_AZ_STATE_FILE (default "<argv file>.state") so a sequence
         survives across process launches. Without a fixture it replies "{}" with exit 0.

    stdout goes through Write-Output, stderr through [Console]::Error.WriteLine, the exit code through "exit". The host
    runs with Set-StrictMode -Version Latest and $ErrorActionPreference = 'Stop'; every variable here carries a "fake"
    prefix so dynamic scoping never picks up a host variable by accident, and nothing global is touched.
#>

$fakeUtf8NoBom = [System.Text.UTF8Encoding]::new($false)
$fakeArgv = [string[]]@($args | ForEach-Object { [string]$_ })

function Get-FakeArgvValue {
    param([string[]] $Argv, [string] $Name)
    $index = [array]::IndexOf($Argv, $Name)
    if ($index -ge 0 -and $index + 1 -lt $Argv.Count) {
        return $Argv[$index + 1]
    }
    return $null
}

function Test-FakeMatch {
    param($Match, [string[]] $Argv, [string] $Body)
    if ($null -eq $Match -or $Match -isnot [System.Collections.IDictionary]) {
        return $true
    }
    if ($Match.Contains('method') -and $null -ne $Match['method']) {
        $method = Get-FakeArgvValue -Argv $Argv -Name '--method'
        if ($null -eq $method -or $method -ne [string]$Match['method']) {
            return $false
        }
    }
    if ($Match.Contains('urlContains') -and $null -ne $Match['urlContains']) {
        $url = Get-FakeArgvValue -Argv $Argv -Name '--url'
        if ($null -eq $url -or -not $url.Contains([string]$Match['urlContains'])) {
            return $false
        }
    }
    if ($Match.Contains('argvContains') -and $null -ne $Match['argvContains']) {
        if (-not ($Argv -join ' ').Contains([string]$Match['argvContains'])) {
            return $false
        }
    }
    if ($Match.Contains('bodyContains') -and $null -ne $Match['bodyContains']) {
        if (-not $Body.Contains([string]$Match['bodyContains'])) {
            return $false
        }
    }
    return $true
}

# ---- 1. Inspect the call ----------------------------------------------------------------------------------------------
$fakeBodyFile = $null
$fakeBody = $null
$fakeBodyArg = Get-FakeArgvValue -Argv $fakeArgv -Name '--body'
if ($null -ne $fakeBodyArg -and $fakeBodyArg.StartsWith('@')) {
    $fakeBodyFile = $fakeBodyArg.Substring(1)
    if ([System.IO.File]::Exists($fakeBodyFile)) {
        $fakeBody = [System.IO.File]::ReadAllText($fakeBodyFile, $fakeUtf8NoBom)
    }
}

# ---- 2. Optional hard links of the protocol files (req-/body-/out-<id>.json share one <id>) ----------------------------
$fakeLinkDir = $env:ENTRAARM_FAKE_AZ_LINK_DIR
if ($fakeLinkDir -and $fakeBodyFile) {
    [void][System.IO.Directory]::CreateDirectory($fakeLinkDir)
    $fakeFilesDir = [System.IO.Path]::GetDirectoryName($fakeBodyFile)
    $fakeFileName = [System.IO.Path]::GetFileName($fakeBodyFile)
    if ($fakeFileName -like 'body-*') {
        $fakeCallId = $fakeFileName.Substring(5)
        foreach ($fakePrefix in @('req-', 'body-', 'out-')) {
            $fakeSource = [System.IO.Path]::Combine($fakeFilesDir, $fakePrefix + $fakeCallId)
            if ($fakePrefix -eq 'out-' -and -not [System.IO.File]::Exists($fakeSource)) {
                # The host overwrites the output file in place after this script returns; the link follows it.
                [System.IO.File]::WriteAllText($fakeSource, '', $fakeUtf8NoBom)
            }
            if ([System.IO.File]::Exists($fakeSource)) {
                $fakeLink = [System.IO.Path]::Combine($fakeLinkDir, $fakePrefix + $fakeCallId)
                if (-not [System.IO.File]::Exists($fakeLink)) {
                    New-Item -ItemType HardLink -Path $fakeLink -Target $fakeSource | Out-Null
                }
            }
        }
    }
}

# ---- 3. Record the invocation ------------------------------------------------------------------------------------------
$fakeArgvFile = $env:ENTRAARM_FAKE_AZ_ARGV_FILE
if ($fakeArgvFile) {
    $fakeRecord = [ordered]@{
        argv     = $fakeArgv
        bodyFile = $fakeBodyFile
        body     = $fakeBody
        env      = [ordered]@{
            AZURE_CORE_OUTPUT           = $env:AZURE_CORE_OUTPUT
            AZURE_CORE_NO_COLOR         = $env:AZURE_CORE_NO_COLOR
            POWERSHELL_TELEMETRY_OPTOUT = $env:POWERSHELL_TELEMETRY_OPTOUT
            POWERSHELL_UPDATECHECK      = $env:POWERSHELL_UPDATECHECK
        }
        cwd      = (Get-Location).Path
    }
    $fakeLine = ConvertTo-Json -InputObject $fakeRecord -Compress -Depth 8
    [System.IO.File]::AppendAllText($fakeArgvFile, $fakeLine + "`n", $fakeUtf8NoBom)
}

# ---- 4. Load the fixture -----------------------------------------------------------------------------------------------
$fakeFixture = $env:ENTRAARM_FAKE_AZ_FIXTURE
if (-not $fakeFixture) {
    Write-Output '{}'
    exit 0
}

$fakeResponses = [System.Collections.Generic.List[object]]::new()
$fakeFixturePaths = @()
if ([System.IO.Directory]::Exists($fakeFixture)) {
    $fakeFixturePaths = @([System.IO.Directory]::GetFiles($fakeFixture, '*.json') | Sort-Object)
}
else {
    $fakeFixturePaths = @($fakeFixture)
}
foreach ($fakePath in $fakeFixturePaths) {
    $fakeDocument = ConvertFrom-Json -InputObject ([System.IO.File]::ReadAllText($fakePath, $fakeUtf8NoBom)) -AsHashtable -Depth 64
    if ($fakeDocument -is [System.Collections.IDictionary] -and $fakeDocument.Contains('responses')) {
        foreach ($fakeItem in @($fakeDocument['responses'])) {
            $fakeResponses.Add($fakeItem)
        }
    }
    elseif ($fakeDocument -is [System.Collections.IDictionary]) {
        $fakeResponses.Add($fakeDocument)
    }
    else {
        foreach ($fakeItem in @($fakeDocument)) {
            $fakeResponses.Add($fakeItem)
        }
    }
}

# ---- 5. Select the first unconsumed matching response ------------------------------------------------------------------
$fakeStateFile = $env:ENTRAARM_FAKE_AZ_STATE_FILE
if (-not $fakeStateFile) {
    $fakeStateFile = if ($fakeArgvFile) { "$fakeArgvFile.state" } else { "$fakeFixture.state" }
}
$fakeConsumed = @{}
if ([System.IO.File]::Exists($fakeStateFile)) {
    foreach ($fakeStateLine in [System.IO.File]::ReadAllLines($fakeStateFile)) {
        if ($fakeStateLine -match '^\d+$') {
            $fakeConsumed[[int]$fakeStateLine] = $true
        }
    }
}

$fakeSelected = -1
for ($fakeIndex = 0; $fakeIndex -lt $fakeResponses.Count; $fakeIndex++) {
    if ($fakeConsumed.ContainsKey($fakeIndex)) {
        continue
    }
    $fakeCandidate = $fakeResponses[$fakeIndex]
    $fakeMatch = $null
    if ($fakeCandidate -is [System.Collections.IDictionary] -and $fakeCandidate.Contains('match')) {
        $fakeMatch = $fakeCandidate['match']
    }
    if (Test-FakeMatch -Match $fakeMatch -Argv $fakeArgv -Body ([string]$fakeBody)) {
        $fakeSelected = $fakeIndex
        break
    }
}

if ($fakeSelected -lt 0) {
    [Console]::Error.WriteLine('fake-az: no fixture response left for ' + ($fakeArgv -join ' '))
    exit 99
}
[System.IO.File]::AppendAllText($fakeStateFile, "$fakeSelected`n", $fakeUtf8NoBom)

# ---- 6. Replay it -------------------------------------------------------------------------------------------------------
$fakeResponse = $fakeResponses[$fakeSelected]
if ($fakeResponse -isnot [System.Collections.IDictionary]) {
    [Console]::Error.WriteLine("fake-az: fixture response $fakeSelected is not an object")
    exit 98
}

if ($fakeResponse.Contains('sleepSeconds') -and $null -ne $fakeResponse['sleepSeconds']) {
    $fakeSleep = [double]$fakeResponse['sleepSeconds']
    if ($fakeSleep -gt 0) {
        Start-Sleep -Milliseconds ([int]($fakeSleep * 1000))
    }
}

if ($fakeResponse.Contains('stderr') -and $null -ne $fakeResponse['stderr'] -and ([string]$fakeResponse['stderr']).Length -gt 0) {
    [Console]::Error.WriteLine([string]$fakeResponse['stderr'])
}

if ($fakeResponse.Contains('stdoutJson') -and $null -ne $fakeResponse['stdoutJson']) {
    Write-Output (ConvertTo-Json -InputObject $fakeResponse['stdoutJson'] -Compress -Depth 64)
}
elseif ($fakeResponse.Contains('stdout') -and $null -ne $fakeResponse['stdout'] -and ([string]$fakeResponse['stdout']).Length -gt 0) {
    Write-Output ([string]$fakeResponse['stdout'])
}

$fakeExitCode = 0
if ($fakeResponse.Contains('exitCode') -and $null -ne $fakeResponse['exitCode']) {
    $fakeExitCode = [int]$fakeResponse['exitCode']
}
exit $fakeExitCode
