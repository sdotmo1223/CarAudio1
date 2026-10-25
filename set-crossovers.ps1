# set-crossovers.ps1 - put an explicit high-pass on every path, with one command.
#
# crossover-range.ps1 can only say "tweeters here, everything else bypassed". The owner's tune
# wants a different low edge on each driver group, and the LP side is wide open everywhere:
#
#   path 0-3  BZRK RS-T150 1" tweeters      HP 1500   LP open (20 k)
#   path 4-5  NVX XQS3v2 3.5" (front/rear)  HP   80   LP open
#   path 6    Deaf Bonce AP-M81SE 8" pair   HP   50   LP open
#   path 7    Orion XTR XTX654 6.5" pair    HP   60   LP open
#
# Order of operations, deliberately:
#   1. read all eight paths and write a snapshot to disk  -> the way back exists before any write
#   2. dry-run each path through the bridge              -> the exact bytes cmd 13 would carry
#   3. write, then read back and compare scalars AND the 40 coefficients, per path
# Nothing is reported as done unless the device itself gives the value back.
#
#   .\set-crossovers.ps1 plan                       # offline: what would be sent
#   .\set-crossovers.ps1 show                       # what is on the device now
#   .\set-crossovers.ps1 apply                      # the owner's map above
#   .\set-crossovers.ps1 apply -Hp '4=80,5=80'
#   .\set-crossovers.ps1 restore                    # newest snapshot for this memory
#   .\set-crossovers.ps1 restore -Snapshot evidence\crossover-snapshot-mem2-....json
param(
    [ValidateSet('plan', 'show', 'apply', 'restore')]
    [string]$Action = 'show',
    [string]$Hp = '0=1500,1=1500,2=1500,3=1500,4=80,5=80,6=50,7=60',
    [int]$Lp = 20000,                 # 20 k = the vendor's own "off"
    [int]$Slope = 3,                  # 1/2/3 = 12/18/24 dB/oct
    [int]$FilterType = 1,             # 1 = Linkwitz-Riley
    [int]$Mem = -1,                   # -1 = the memory the device is playing
    [string]$Snapshot = '',           # explicit snapshot file for restore
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$base = 'http://127.0.0.1:8585'

function Fail([string]$msg) { Write-Host "error: $msg" -ForegroundColor Red; exit 1 }
function Say([string]$msg) { Write-Host $msg }

function Api([string]$path, $body) {
    if ($null -eq $body) { return Invoke-RestMethod -Uri ($base + $path) -Method Get -TimeoutSec 30 }
    return Invoke-RestMethod -Uri ($base + $path) -Method Post -ContentType 'application/json' `
        -Body ($body | ConvertTo-Json -Depth 6) -TimeoutSec 30
}

function DeviceMem() {
    $s = Api '/api/status' $null
    if (-not $s.ok) { Fail ("the DSP is not answering ({0}). Plug it in, run .\preflight.ps1, then run this again." -f $s.error) }
    if ($null -eq $s.mem) { Fail 'the device did not report a memory' }
    return [int]$s.mem
}

function Read-Pb([int]$mem, [int]$path) {
    $d = Api '/api/eqread' @{ mem = $mem; path = $path }
    if (-not $d.ok) { Fail ("path {0} read failed: {1}" -f $path, $d.error) }
    if (-not $d.passband) { Fail ("path {0} replied without a crossover block" -f $path) }
    return $d.passband
}

function DryRun([int]$path, $low, $high) {
    $d = Api '/api/passband' @{ dryRun = $true; path = $path; low = $low; high = $high }
    if (-not $d.ok) { Fail ("dry run for path {0} failed: {1}" -f $path, $d.error) }
    return $d
}

function Coffs($pbHex) {
    $low = @(); $high = @()
    for ($j = 0; $j -lt 20; $j++) {
        $low  += $pbHex.Substring(16 + $j * 8, 8)
        $high += $pbHex.Substring(176 + $j * 8, 8)
    }
    return @{ low = $low; high = $high }
}

# "0=1500,4=80" -> ordered map
function ParseHp([string]$s) {
    $m = @{}
    foreach ($pair in $s.Split(',')) {
        if ($pair.Trim() -eq '') { continue }
        $kv = $pair.Split('=')
        if ($kv.Count -ne 2) { Fail ("cannot read '{0}' - expected path=hz" -f $pair) }
        $p = [int]$kv[0]; $f = [int]$kv[1]
        if ($p -lt 0 -or $p -gt 7) { Fail ("path {0} is outside 0-7" -f $p) }
        if ($f -lt 20 -or $f -gt 20000) { Fail ("{0} Hz on path {1} is outside 20-20000" -f $f, $p) }
        $m[$p] = $f
    }
    return $m
}

function NewSnapshot([int]$mem, [string]$tag) {
    $stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
    $f = Join-Path $root ("evidence\crossover-snapshot-mem{0}-{1}.json" -f ($mem + 1), $stamp)
    $rows = @()
    foreach ($p in 0..7) {
        $pb = Read-Pb $mem $p
        $rows += [ordered]@{
            path = $p
            lowFc = [int]$pb.lowFc;  highFc = [int]$pb.highFc
            lowSlope = [int]$pb.lowSlope; highSlope = [int]$pb.highSlope
            lowFilterType = [int]$pb.lowFilterType; highFilterType = [int]$pb.highFilterType
            lowCoffHex = @($pb.lowCoffHex); highCoffHex = @($pb.highCoffHex)
        }
    }
    $snap = [ordered]@{ at = (Get-Date).ToString('s'); mem = $mem; tag = $tag; paths = $rows }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $f) | Out-Null
    $snap | ConvertTo-Json -Depth 5 | Set-Content -Path $f -Encoding UTF8
    return $f
}

$hpMap = ParseHp $Hp
$paths = 0..7

switch ($Action) {

    'plan' {
        Say ''
        Say ("target crossovers (memory {0})" -f $(if ($Mem -ge 0) { $Mem + 1 } else { 'the one playing' }))
        Say ("  {0,-5} {1,8} {2,8} {3,12} {4}" -f 'path', 'hp (Hz)', 'lp (Hz)', 'slope dB/oct', 'filter')
        foreach ($p in $paths) {
            $low  = @{ fc = [int]$hpMap[$p]; slope = $Slope; filterType = $FilterType; hiLow = 0; butt = 0; enable = $true }
            $high = @{ fc = $Lp;           slope = $Slope; filterType = $FilterType; hiLow = 1; butt = 0; enable = $true }
            $d = DryRun $p $low $high
            $lpTxt = if ([int]$d.highFc -ge 20000) { 'open' } else { [string]$d.highFc }
            $slopeDb = @{ 1 = 12; 2 = 18; 3 = 24 }
            Say ("  {0,-5} {1,8} {2,8} {3,12} {4,-10} {5} bytes" -f $p, $d.lowFc, $lpTxt,
                ('{0}' -f $slopeDb[$Slope]), 'LR', $d.pbBytes)
        }
        Say ''
        Say 'plan only - no cmd 13 sent, no device touched.'
        exit 0
    }

    'show' {
        if ($Mem -lt 0) { $Mem = DeviceMem }
        Say ''
        Say ("current crossovers (memory {0})" -f ($Mem + 1))
        Say ("  {0,-5} {1,8} {2,8} {3,10}" -f 'path', 'hp (Hz)', 'lp (Hz)', 'slopes')
        foreach ($p in $paths) {
            $pb = Read-Pb $Mem $p
            $hpTxt = if ([int]$pb.lowFc -le 20) { 'bypass' } else { [string]$pb.lowFc }
            $lpTxt = if ([int]$pb.highFc -ge 20000) { 'open' } else { [string]$pb.highFc }
            Say ("  {0,-5} {1,8} {2,8} {3,10}" -f $p, $hpTxt, $lpTxt, ("{0}/{1}" -f $pb.lowSlope, $pb.highSlope))
        }
        exit 0
    }

    'apply' {
        if ($Mem -lt 0) { $Mem = DeviceMem }

        # 1. the way back, before anything is written
        $snapFile = NewSnapshot $Mem 'before-target-crossovers'
        Say ("snapshot of all 8 paths -> {0}" -f (Split-Path -Leaf $snapFile))

        # 2. dry-run every path first: a bad value fails here, not halfway through the device
        $plan = @{}
        foreach ($p in $paths) {
            $low  = @{ fc = [int]$hpMap[$p]; slope = $Slope; filterType = $FilterType; hiLow = 0; butt = 0; enable = $true }
            $high = @{ fc = $Lp;           slope = $Slope; filterType = $FilterType; hiLow = 1; butt = 0; enable = $true }
            $plan[$p] = @{ low = $low; high = $high; expect = (DryRun $p $low $high) }
        }

        if ($WhatIf) { Say 'what-if: nothing written'; exit 0 }

        # 3. write, then believe only the readback
        Say ''
        Say ("  {0,-5} {1,8} {2,8} {3,14} {4}" -f 'path', 'hp (Hz)', 'lp (Hz)', 'result', '')
        $bad = @()
        foreach ($p in $paths) {
            $r = Api '/api/passband' @{ mem = $Mem; path = $p; low = $plan[$p].low; high = $plan[$p].high }
            if (-not $r.ok) { Fail ("path {0} write refused: {1}" -f $p, $r.error) }
            $back  = Read-Pb $Mem $p
            $expect = $plan[$p].expect
            $expC = Coffs $expect.pbHex

            $okScalar = ([int]$back.lowFc -eq [int]$expect.lowFc -and [int]$back.highFc -eq [int]$expect.highFc `
                -and [int]$back.lowSlope -eq [int]$expect.lowSlope -and [int]$back.highSlope -eq [int]$expect.highSlope `
                -and [int]$back.lowFilterType -eq [int]$expect.lowFilterType -and [int]$back.highFilterType -eq [int]$expect.highFilterType)
            $okCoff = (($back.lowCoffHex -join ',') -eq ($expC.low -join ',')) -and
                      (($back.highCoffHex -join ',') -eq ($expC.high -join ','))

            $lpTxt = if ([int]$expect.highFc -ge 20000) { 'open' } else { [string]$expect.highFc }
            if ($okScalar -and $okCoff) {
                Say ("  {0,-5} {1,8} {2,8} {3,14}" -f $p, $expect.lowFc, $lpTxt, 'verified')
            } else {
                $bad += $p
                Say ("  {0,-5} {1,8} {2,8} {3,14}" -f $p, $expect.lowFc, $lpTxt,
                    $(if (-not $okScalar) { 'SCALARS WRONG' } else { 'COEFFS WRONG' }))
            }
        }

        Say ''
        if ($bad.Count) {
            Fail ("paths {0} did not take the new crossover. Snapshot: {1}  ->  .\set-crossovers.ps1 restore -Snapshot '{1}'" -f ($bad -join ','), $snapFile)
        }
        Say ("all 8 paths verified against the device's own readback (scalars + 40 coefficients each).")
        Say ("way back: .\set-crossovers.ps1 restore -Snapshot '{0}'" -f $snapFile)
        exit 0
    }

    'restore' {
        if ($Mem -lt 0) { $Mem = DeviceMem }
        $f = $Snapshot
        if (-not $f) {
            $cand = Get-ChildItem (Join-Path $root 'evidence') -Filter ("crossover-snapshot-mem{0}-*.json" -f ($Mem + 1)) -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if (-not $cand) { Fail ("no snapshot for memory {0} - nothing to restore" -f ($Mem + 1)) }
            $f = $cand.FullName
        }
        if (-not (Test-Path $f)) { Fail ("no such snapshot: {0}" -f $f) }
        $snap = Get-Content -Raw -Encoding UTF8 $f | ConvertFrom-Json
        if ([int]$snap.mem -ne $Mem) { Fail ("the snapshot is from memory {0}, the device is on {1}" -f ([int]$snap.mem + 1), ($Mem + 1)) }
        Say ("restoring {0} path(s) from {1}" -f @($snap.paths).Count, (Split-Path -Leaf $f))

        if ($WhatIf) { exit 0 }

        $bad = @()
        foreach ($row in $snap.paths) {
            $low  = @{ fc = [int]$row.lowFc;  slope = [int]$row.lowSlope;  filterType = [int]$row.lowFilterType;  hiLow = 0; butt = 0; enable = $true }
            $high = @{ fc = [int]$row.highFc; slope = [int]$row.highSlope; filterType = [int]$row.highFilterType; hiLow = 1; butt = 0; enable = $true }
            $r = Api '/api/passband' @{ mem = $Mem; path = [int]$row.path; low = $low; high = $high }
            if (-not $r.ok) { Fail ("path {0} write refused: {1}" -f $row.path, $r.error) }
            $back = Read-Pb $Mem ([int]$row.path)
            $okScalar = ([int]$back.lowFc -eq [int]$row.lowFc -and [int]$back.highFc -eq [int]$row.highFc `
                -and [int]$back.lowSlope -eq [int]$row.lowSlope -and [int]$back.highSlope -eq [int]$row.highSlope `
                -and [int]$back.lowFilterType -eq [int]$row.lowFilterType -and [int]$back.highFilterType -eq [int]$row.highFilterType)
            $okCoff = (($back.lowCoffHex -join ',') -eq ((@($row.lowCoffHex)) -join ',')) -and
                      (($back.highCoffHex -join ',') -eq ((@($row.highCoffHex)) -join ','))
            if ($okScalar -and $okCoff) {
                Say ("  path {0}: {1} Hz / {2} Hz - byte-for-byte" -f $row.path, $row.lowFc, $row.highFc)
            } else {
                $bad += $row.path
                Say ("  path {0}: FAILED" -f $row.path)
            }
        }
        if ($bad.Count) { Fail ("paths {0} did not restore" -f ($bad -join ',')) }
        Say 'restored: every path matches the snapshot.'
        exit 0
    }
}
