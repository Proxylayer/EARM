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

    Several fakes run at the same time (PwshAzRunner.MaxParallel > 1), so every read-modify-write of the shared files
    (argv file, state file, concurrency gauge) happens under ONE named System.Threading.Mutex derived from the argv
    file path (the state file path when there is no argv file). Files are opened with FileShare.ReadWrite|Delete and
    the open is retried on IOException (50 x 20 ms), so a concurrent reader in the test host never blocks a writer
    and vice versa. Each argv record is written as ONE line with a single Write call. See tests/fake-az/README.md.
#>

$fakeUtf8NoBom = [System.Text.UTF8Encoding]::new($false)
$fakeArgv = [string[]]@($args | ForEach-Object { [string]$_ })

$fakeShare = [System.IO.FileShare]([int][System.IO.FileShare]::ReadWrite -bor [int][System.IO.FileShare]::Delete)

function Open-FakeFile {
    # Opens a shared file; retries sharing violations (IOException other than missing file/dir) 50 x 20 ms.
    param([string] $Path, [System.IO.FileMode] $Mode, [System.IO.FileAccess] $Access)
    for ($attempt = 1; ; $attempt++) {
        try {
            return [System.IO.FileStream]::new($Path, $Mode, $Access, $fakeShare)
        }
        catch [System.IO.FileNotFoundException] { throw }
        catch [System.IO.DirectoryNotFoundException] { throw }
        catch [System.IO.IOException] {
            if ($attempt -ge 50) { throw }
            Start-Sleep -Milliseconds 20
        }
    }
}

function Add-FakeLine {
    # Appends one line with a single Write call (no partial line is ever visible to a reader).
    param([string] $Path, [string] $Line)
    $bytes = $fakeUtf8NoBom.GetBytes($Line + "`n")
    $stream = Open-FakeFile -Path $Path -Mode ([System.IO.FileMode]::Append) -Access ([System.IO.FileAccess]::Write)
    try {
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush()
    }
    finally {
        $stream.Dispose()
    }
}

function Read-FakeText {
    # Whole-file text, or $null when the file does not exist.
    param([string] $Path)
    if (-not [System.IO.File]::Exists($Path)) { return $null }
    try {
        $stream = Open-FakeFile -Path $Path -Mode ([System.IO.FileMode]::Open) -Access ([System.IO.FileAccess]::Read)
    }
    catch [System.IO.FileNotFoundException] { return $null }
    try {
        $reader = [System.IO.StreamReader]::new($stream, $fakeUtf8NoBom)
        return $reader.ReadToEnd()
    }
    finally {
        $stream.Dispose()
    }
}

function Write-FakeText {
    # Replaces the file content (used for the gauge, always under the mutex).
    param([string] $Path, [string] $Text)
    $bytes = $fakeUtf8NoBom.GetBytes($Text)
    $stream = Open-FakeFile -Path $Path -Mode ([System.IO.FileMode]::Create) -Access ([System.IO.FileAccess]::Write)
    try {
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush()
    }
    finally {
        $stream.Dispose()
    }
}

function New-FakeMutex {
    param([string] $KeyPath)
    $full = [System.IO.Path]::GetFullPath($KeyPath)
    if ([OperatingSystem]::IsWindows()) { $full = $full.ToLowerInvariant() }
    $hash = [System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($full))
    return [System.Threading.Mutex]::new($false, 'EntraArmFakeAz-' + [System.Convert]::ToHexString($hash, 0, 16))
}

function Invoke-FakeLocked {
    # Runs $Action holding the cross-process mutex. An abandoned mutex (a holder killed by a runner timeout) is ours.
    param([System.Threading.Mutex] $Mutex, [scriptblock] $Action)
    $acquired = $false
    try {
        try { $acquired = $Mutex.WaitOne([TimeSpan]::FromSeconds(60)) }
        catch [System.Threading.AbandonedMutexException] { $acquired = $true }
        if (-not $acquired) { throw 'fake-az: could not acquire the cross-process mutex within 60 s' }
        return (& $Action)
    }
    finally {
        if ($acquired) { $Mutex.ReleaseMutex() }
    }
}

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
$fakeStateFile = $env:ENTRAARM_FAKE_AZ_STATE_FILE
if (-not $fakeStateFile) {
    $fakeStateFile = if ($fakeArgvFile) { "$fakeArgvFile.state" } elseif ($env:ENTRAARM_FAKE_AZ_FIXTURE) { "$($env:ENTRAARM_FAKE_AZ_FIXTURE).state" } else { $null }
}
$fakeMutex = $null
$fakeLockKey = if ($fakeArgvFile) { $fakeArgvFile } else { $fakeStateFile }
if ($fakeLockKey) {
    $fakeMutex = New-FakeMutex -KeyPath $fakeLockKey
}
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
    [void](Invoke-FakeLocked -Mutex $fakeMutex -Action { Add-FakeLine -Path $fakeArgvFile -Line $fakeLine })
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

# ---- 5. Select the first unconsumed matching response (atomic: read state, pick, append index under the mutex) -----------
$fakeSelectAction = {
    $fakeConsumed = @{}
    $fakeStateText = Read-FakeText -Path $fakeStateFile
    if ($null -ne $fakeStateText) {
        foreach ($fakeStateLine in $fakeStateText.Split("`n")) {
            $fakeStateLine = $fakeStateLine.Trim()
            if ($fakeStateLine -match '^\d+$') {
                $fakeConsumed[[int]$fakeStateLine] = $true
            }
        }
    }

    $fakePicked = -1
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
            $fakePicked = $fakeIndex
            break
        }
    }

    if ($fakePicked -ge 0) {
        Add-FakeLine -Path $fakeStateFile -Line ([string]$fakePicked)
    }
    return $fakePicked
}
$fakeSelected = [int](Invoke-FakeLocked -Mutex $fakeMutex -Action $fakeSelectAction)

if ($fakeSelected -lt 0) {
    [Console]::Error.WriteLine('fake-az: no fixture response left for ' + ($fakeArgv -join ' '))
    exit 99
}
# ---- 6. Replay it -------------------------------------------------------------------------------------------------------
$fakeResponse = $fakeResponses[$fakeSelected]
if ($fakeResponse -isnot [System.Collections.IDictionary]) {
    [Console]::Error.WriteLine("fake-az: fixture response $fakeSelected is not an object")
    exit 98
}

# Concurrency gauge "<current> <max> <arrivals>" in "<argv file>.gauge": how many fakes are inside the replay window
# (between selection and the end of the sleep/barrier) at once. Updated under the mutex.
$fakeGaugeFile = if ($fakeArgvFile) { "$fakeArgvFile.gauge" } else { $null }
$fakeUpdateGauge = {
    param([int] $DeltaCurrent, [int] $DeltaArrivals)
    $text = Read-FakeText -Path $fakeGaugeFile
    $current = 0; $max = 0; $arrivals = 0
    if ($text) {
        $parts = $text.Trim().Split(' ')
        if ($parts.Count -ge 3) { $current = [int]$parts[0]; $max = [int]$parts[1]; $arrivals = [int]$parts[2] }
    }
    $current += $DeltaCurrent
    $arrivals += $DeltaArrivals
    if ($current -gt $max) { $max = $current }
    Write-FakeText -Path $fakeGaugeFile -Text "$current $max $arrivals`n"
    return $arrivals
}
$fakeArrival = 0
$fakeBarrierFailed = $false
if ($fakeGaugeFile) {
    $fakeArrival = [int](Invoke-FakeLocked -Mutex $fakeMutex -Action { & $fakeUpdateGauge -DeltaCurrent 1 -DeltaArrivals 1 })
}
try {
    # Optional barrier: the first <parties> arrivals wait until <parties> fakes are inside at the same time, which proves
    # (deterministically, without sleeping a guessed duration) that the runner really runs calls concurrently. Fakes
    # arriving later pass straight through. A barrier that is not met within timeoutSeconds (default 30) exits 97.
    if ($fakeGaugeFile -and $fakeResponse.Contains('barrier') -and $fakeResponse['barrier'] -is [System.Collections.IDictionary]) {
        $fakeBarrier = $fakeResponse['barrier']
        $fakeParties = [int]$fakeBarrier['parties']
        $fakeBarrierTimeout = if ($fakeBarrier.Contains('timeoutSeconds') -and $null -ne $fakeBarrier['timeoutSeconds']) { [double]$fakeBarrier['timeoutSeconds'] } else { 30.0 }
        if ($fakeArrival -le $fakeParties) {
            $fakeDeadline = [DateTime]::UtcNow.AddSeconds($fakeBarrierTimeout)
            while ($true) {
                $fakeArrivals = [int](Invoke-FakeLocked -Mutex $fakeMutex -Action { & $fakeUpdateGauge -DeltaCurrent 0 -DeltaArrivals 0 })
                if ($fakeArrivals -ge $fakeParties) { break }
                if ([DateTime]::UtcNow -ge $fakeDeadline) {
                    [Console]::Error.WriteLine("fake-az: barrier of $fakeParties parties not met within $fakeBarrierTimeout s (only $fakeArrivals arrived)")
                    $fakeBarrierFailed = $true
                    break
                }
                Start-Sleep -Milliseconds 20
            }
        }
    }

    if ($fakeResponse.Contains('sleepSeconds') -and $null -ne $fakeResponse['sleepSeconds']) {
        $fakeSleep = [double]$fakeResponse['sleepSeconds']
        if ($fakeSleep -gt 0) {
            Start-Sleep -Milliseconds ([int]($fakeSleep * 1000))
        }
    }
}
finally {
    if ($fakeGaugeFile) {
        [void](Invoke-FakeLocked -Mutex $fakeMutex -Action { & $fakeUpdateGauge -DeltaCurrent -1 -DeltaArrivals 0 })
    }
}
if ($fakeBarrierFailed) {
    exit 97
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
