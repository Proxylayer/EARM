#Requires -Version 7.4
<#
.SYNOPSIS
    EntraARM Azure CLI host script: reads a request file written by PwshAzRunner and invokes az.
.DESCRIPTION
    Embedded resource of EntraArm.Az. PwshAzRunner extracts it to the EntraARM temp directory and runs

        pwsh -NoProfile -NonInteractive -NoLogo -ExecutionPolicy Bypass -File Invoke-AzRest.ps1 -RequestFile <path> [-AzCommand <path>]

    The request file is UTF-8 JSON:

        {
          "command":       ["rest"],                 // or e.g. ["account", "show"], ["version"]
          "method":        "GET",                    // rest only
          "url":           "https://graph.microsoft.com/v1.0/users",   // rest only, never carries a query string
          "urlParameters": { "$select": "id" },      // rest only -> --url-parameters k=v ... (az URL-encodes them)
          "headers":       { "Prefer": "x" },        // rest only -> --headers k=v ...
          "bodyFile":      "<path>",                 // rest only -> --body @<path>
          "outputFile":    "<path>",                 // az stdout is written here (UTF-8, no BOM)
          "args":          []                        // extra arguments appended before --only-show-errors -o json
        }

    No argument ever passes through a shell: az receives $azArgs as an array through PowerShell's native command
    invocation (& $az @azArgs). $AzCommand may point at a .ps1 (the test double), which then runs in-process.

    Exit code: az's own exit code; 70 = request file unreadable / host failure; 127 = az not found.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $RequestFile,

    [string] $AzCommand
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$PSNativeCommandUseErrorActionPreference = $false
$PSStyle.OutputRendering = 'PlainText'

$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
try {
    [Console]::OutputEncoding = $utf8NoBom
}
catch {
    # No console attached (should not happen with CreateNoWindow); the output file is written with UTF-8 anyway.
}
$OutputEncoding = $utf8NoBom

function Write-EntraArmError {
    param([string] $Message)
    [Console]::Error.WriteLine("EntraArm: $Message")
}

# ---- Read the request file ------------------------------------------------------------------------------------------
$request = $null
try {
    $requestJson = [System.IO.File]::ReadAllText($RequestFile, $utf8NoBom)
    $request = ConvertFrom-Json -InputObject $requestJson -AsHashtable -Depth 64
}
catch {
    Write-EntraArmError "cannot read request file '$RequestFile': $($_.Exception.Message)"
    exit 70
}
if ($null -eq $request) {
    Write-EntraArmError "request file '$RequestFile' is empty"
    exit 70
}

# ---- Resolve the Azure CLI ------------------------------------------------------------------------------------------
# A file path (including a .ps1 test double) is used as given. Otherwise PowerShell's own command resolution finds az
# on PATH; on Windows that is the az.cmd launcher installed by the Azure CLI MSI, invoked with PowerShell's native
# argument passing exactly as a user typing "az rest ..." in pwsh would get.
$az = $null
if ($AzCommand) {
    if (Test-Path -LiteralPath $AzCommand -PathType Leaf) {
        $az = (Resolve-Path -LiteralPath $AzCommand).Path
    }
    else {
        $resolved = Get-Command -Name $AzCommand -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $resolved) {
            Write-EntraArmError "Azure CLI command '$AzCommand' (from --az-command / ENTRAARM_AZ_COMMAND) was not found."
            exit 127
        }
        $az = $resolved
    }
}
else {
    $resolved = Get-Command -Name 'az' -CommandType Application, ExternalScript -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $resolved) {
        Write-EntraArmError "Azure CLI ('az') was not found on PATH. Install it (winget install Microsoft.AzureCLI, https://aka.ms/installazurecli) or pass --az-command <path>."
        exit 127
    }
    $az = $resolved
}

# ---- Build $azArgs as an array (never a string) -----------------------------------------------------------------------
$azArgs = [System.Collections.Generic.List[string]]::new()

$command = @()
if ($null -ne $request['command']) {
    $command = @($request['command'])
}
if ($command.Count -eq 0) {
    $command = @('rest')
}
foreach ($part in $command) {
    $azArgs.Add([string]$part)
}

if ($command[0] -eq 'rest') {
    if ($request['method']) {
        $azArgs.Add('--method')
        $azArgs.Add([string]$request['method'])
    }
    if ($request['url']) {
        $azArgs.Add('--url')
        $azArgs.Add([string]$request['url'])
    }

    $urlParameters = $request['urlParameters']
    if ($null -ne $urlParameters -and $urlParameters.Count -gt 0) {
        $azArgs.Add('--url-parameters')
        foreach ($key in $urlParameters.Keys) {
            $azArgs.Add(('{0}={1}' -f $key, $urlParameters[$key]))
        }
    }

    $headers = $request['headers']
    if ($null -ne $headers -and $headers.Count -gt 0) {
        $azArgs.Add('--headers')
        foreach ($key in $headers.Keys) {
            $azArgs.Add(('{0}={1}' -f $key, $headers[$key]))
        }
    }

    if ($request['bodyFile']) {
        $azArgs.Add('--body')
        $azArgs.Add('@' + [string]$request['bodyFile'])
    }
}

$extraArgs = $request['args']
if ($null -ne $extraArgs) {
    foreach ($arg in @($extraArgs)) {
        $azArgs.Add([string]$arg)
    }
}

$azArgs.Add('--only-show-errors')
$azArgs.Add('-o')
$azArgs.Add('json')

# ---- Invoke az ------------------------------------------------------------------------------------------------------
# stdout is captured in-process and written to outputFile; stderr passes straight through to the host's stderr pipe.
$global:LASTEXITCODE = 0
$output = $null
try {
    $output = & $az @azArgs
}
catch {
    Write-EntraArmError "invoking az failed: $($_.Exception.Message)"
    exit 70
}
$exitCode = $LASTEXITCODE
if ($null -eq $exitCode) {
    $exitCode = 0
}

$text = ''
if ($null -ne $output) {
    $text = (@($output) | ForEach-Object { [string]$_ }) -join "`n"
}

$outputFile = $request['outputFile']
if ($outputFile) {
    try {
        [System.IO.File]::WriteAllText([string]$outputFile, $text, $utf8NoBom)
    }
    catch {
        Write-EntraArmError "cannot write output file '$outputFile': $($_.Exception.Message)"
        exit 70
    }
}
elseif ($text.Length -gt 0) {
    [Console]::Out.Write($text)
}

exit $exitCode
