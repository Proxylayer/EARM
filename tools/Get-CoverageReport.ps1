#Requires -Version 7
<#
.SYNOPSIS
    Merges the Cobertura files written by 'test.ps1 -Coverage' and reports line / branch coverage per assembly and class.
.DESCRIPTION
    Reads artifacts/test-results/*.cobertura.xml, keeps only classes whose source file lives under src/ (excluding obj/
    generated code), merges the runs by taking the maximum hit count per source line and the best coverage per branch
    condition (a union approximation: a 50% condition seen in two runs counts as 50%), and attributes lines to the
    top-level type (nested types, lambdas and async state machines are folded into their declaring type).
    Branch counts are conditions * 2 as reported by Microsoft.Testing.Extensions.CodeCoverage.
.PARAMETER Format
    Summary (default): per-assembly table plus the weakest classes. Json: full per-class data including uncovered
    line ranges (used for gap analysis).
.PARAMETER Top
    Number of weakest classes listed in Summary mode.
.PARAMETER MinLines
    Classes with fewer executable lines than this are ignored in the ranking (trivial DTOs).
#>
[CmdletBinding()]
param(
    [string]$ResultsDir = (Join-Path $PSScriptRoot '..' 'artifacts' 'test-results'),
    [ValidateSet('Summary', 'Json')]
    [string]$Format = 'Summary',
    [int]$Top = 30,
    [int]$MinLines = 8,
    [string]$OnlyAssembly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$files = @(Get-ChildItem -LiteralPath $ResultsDir -Filter '*.cobertura.xml')
if ($files.Count -eq 0) { throw "No *.cobertura.xml in $ResultsDir" }

# key "file|line" -> @{ Hits; Class; Assembly; Cond = @{ idx -> pct } }
$lines = @{}
foreach ($f in $files) {
    $xml = [xml](Get-Content -LiteralPath $f.FullName -Raw)
    foreach ($pkg in $xml.coverage.packages.package) {
        $asm = $pkg.name
        foreach ($cls in $pkg.classes.class) {
            $file = $cls.filename
            if ($file -notmatch '\\src\\' -or $file -match '\\obj\\') { continue }
            $topType = (($cls.name -split '/')[0]) -replace '\.<.*$', ''
            foreach ($m in $cls.methods.method) {
                foreach ($ln in $m.lines.line) {
                    $key = "$file|$($ln.number)"
                    if (-not $lines.ContainsKey($key)) {
                        $lines[$key] = @{ Hits = 0; Class = $topType; Assembly = $asm; File = $file; Number = [int]$ln.number; Cond = @{} }
                    }
                    $e = $lines[$key]
                    $e.Hits = [Math]::Max($e.Hits, [int]$ln.hits)
                    if ($ln.branch -eq 'True' -and $ln.conditions) {
                        foreach ($c in $ln.conditions.condition) {
                            $pct = [int](($c.coverage -replace '%', ''))
                            $idx = [int]$c.number
                            if (-not $e.Cond.ContainsKey($idx) -or $e.Cond[$idx] -lt $pct) { $e.Cond[$idx] = $pct }
                        }
                    }
                }
            }
        }
    }
}

$byClass = @{}
foreach ($e in $lines.Values) {
    if ($OnlyAssembly -and $e.Assembly -ne $OnlyAssembly) { continue }
    $k = "$($e.Assembly)|$($e.Class)"
    if (-not $byClass.ContainsKey($k)) {
        $byClass[$k] = @{ Assembly = $e.Assembly; Class = $e.Class; File = $e.File; Valid = 0; Covered = 0; BrValid = 0; BrCovered = 0.0; Uncovered = [System.Collections.Generic.List[int]]::new() }
    }
    $c = $byClass[$k]
    $c.Valid++
    if ($e.Hits -gt 0) { $c.Covered++ } else { $c.Uncovered.Add($e.Number) }
    foreach ($pct in $e.Cond.Values) { $c.BrValid += 2; $c.BrCovered += 2.0 * $pct / 100.0 }
}

function Get-Ranges([int[]]$nums) {
    $sorted = $nums | Sort-Object
    $out = @(); $start = $null; $prev = $null
    foreach ($n in $sorted) {
        if ($null -eq $start) { $start = $n; $prev = $n; continue }
        if ($n -le $prev + 3) { $prev = $n; continue }
        $out += $(if ($start -eq $prev) { "$start" } else { "$start-$prev" })
        $start = $n; $prev = $n
    }
    if ($null -ne $start) { $out += $(if ($start -eq $prev) { "$start" } else { "$start-$prev" }) }
    return $out
}

$rows = foreach ($c in $byClass.Values) {
    [pscustomobject]@{
        Assembly  = $c.Assembly
        Class     = $c.Class
        File      = ($c.File -replace '^.*\\src\\', 'src\')
        Lines     = $c.Valid
        Covered   = $c.Covered
        LinePct   = [Math]::Round(100.0 * $c.Covered / [Math]::Max(1, $c.Valid), 1)
        Branches  = $c.BrValid
        BrCovered = [Math]::Round($c.BrCovered, 0)
        BranchPct = $(if ($c.BrValid -eq 0) { $null } else { [Math]::Round(100.0 * $c.BrCovered / $c.BrValid, 1) })
        Missed    = $c.Valid - $c.Covered
        Uncovered = (Get-Ranges $c.Uncovered.ToArray()) -join ', '
    }
}

if ($Format -eq 'Json') {
    $rows | Sort-Object Missed -Descending | ConvertTo-Json -Depth 4
    return
}

$asmRows = $rows | Group-Object Assembly | ForEach-Object {
    $l = ($_.Group | Measure-Object Lines -Sum).Sum
    $cv = ($_.Group | Measure-Object Covered -Sum).Sum
    $b = ($_.Group | Measure-Object Branches -Sum).Sum
    $bc = ($_.Group | Measure-Object BrCovered -Sum).Sum
    [pscustomobject]@{ Assembly = $_.Name; Lines = $l; Covered = $cv; LinePct = [Math]::Round(100.0 * $cv / [Math]::Max(1, $l), 1); Branches = $b; BrCovered = $bc; BranchPct = [Math]::Round(100.0 * $bc / [Math]::Max(1, $b), 1) }
} | Sort-Object Assembly

$tl = ($asmRows | Measure-Object Lines -Sum).Sum; $tc = ($asmRows | Measure-Object Covered -Sum).Sum
$tb = ($asmRows | Measure-Object Branches -Sum).Sum; $tbc = ($asmRows | Measure-Object BrCovered -Sum).Sum

'| Assembly | Lines | Covered | Line % | Branches | Covered | Branch % |'
'|---|---:|---:|---:|---:|---:|---:|'
foreach ($a in $asmRows) { "| $($a.Assembly) | $($a.Lines) | $($a.Covered) | $($a.LinePct) | $($a.Branches) | $($a.BrCovered) | $($a.BranchPct) |" }
"| **Total** | $tl | $tc | $([Math]::Round(100.0 * $tc / [Math]::Max(1, $tl), 1)) | $tb | $tbc | $([Math]::Round(100.0 * $tbc / [Math]::Max(1, $tb), 1)) |"
''
"Top $Top weakest classes (>= $MinLines lines, ranked by missed lines)"
'| # | Class | Assembly | Lines | Line % | Branch % | Uncovered lines |'
'|---:|---|---|---:|---:|---:|---|'
$i = 0
foreach ($r in ($rows | Where-Object { $_.Lines -ge $MinLines } | Sort-Object Missed -Descending | Select-Object -First $Top)) {
    $i++
    $bp = if ($null -eq $r.BranchPct) { 'n/a' } else { $r.BranchPct }
    "| $i | $($r.Class -replace '^EntraArm\.', '') | $($r.Assembly -replace '^EntraArm\.', '') | $($r.Lines) | $($r.LinePct) | $bp | $($r.Uncovered) |"
}
