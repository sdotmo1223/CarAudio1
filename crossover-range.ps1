# crossover-range.ps1 - measure every driver full range, with the tweeters still protected.
#
# The tune's crossovers band-limit what a sweep can see: a 6.5" cut at 1 kHz can only ever
# show 140-1000 Hz, and a tweeter high-passed at 2.5 kHz can only show 2.5 k-20 k. To see what
# a driver can actually do, open its passband for the measurement and put it back afterwards.
#
# What "open" means on this hardware, proven against the device's own saved readback
# (evidence\passband-baseline-2026-10-16.json, all eight paths byte-identical):
#   low edge 20 Hz  -> the vendor's identity coefficients {0,0,1,0,0}: a true bypass.
#   high edge 20 kHz -> the vendor's own "off" (coefficients computed at 22 kHz). Not an
#                       identity, but it is exactly what the official app writes for "off".
# There is no third option: cmd 13 carries Fc/slope/type and 40 coefficients, no enable bit.
#
# Safety: the crossover change lives in the DSP memory until it is restored. The snapshot is
# written to disk before any write, is never overwritten while it exists, and -Action restore
# verifies every path against it, coefficients included.
#
#   .\crossover-range.ps1 plan                 # what would be written (no device needed)
#   .\crossover-range.ps1 show                 # current crossovers, paths 0-7
#   .\crossover-range.ps1 open   -TweeterFc 1500
#   .\crossover-range.ps1 restore
#
# Exit 0 = every path verified, 1 = something did not land.

param(
    [ValidateSet('plan', 'show', 'snapshot', 'open', 'restore')]
    [string]$Action = 'show',
    [int]$Mem = -1,                       # -1 = the memory the device is playing
    [int]$TweeterFc = 1500,               # tweeter high-pass while measuring = its rated floor
    [int]$LowFc = 20,                     # low edge for the non-tweeter drivers; 20 = true bypass
    [string]$TweeterPaths = '0,1,2,3',    # ch 1-4 on the documented car map
    [string]$OpenPaths = '0,1,2,3,4,5,6,7',
    [string]$Snapshot = '',
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$base = 'http://127.0.0.1:8585'

function Fail([string]$msg) { Write-Host "error: $msg" -ForegroundColor Red; exit 1 }
function Say([string]$msg) { Write-Host $msg }

function Api([string]$path, $body) {
    if ($null -eq $body) {
        return Invoke-RestMethod -Uri ($base + $path) -Method Get -TimeoutSec 30
    }
    return Invoke-RestMethod -Uri ($base + $path) -Method Post -ContentType 'application/json' `
        -Body ($body | ConvertTo-Json -Depth 6) -TimeoutSec 30
}

function DeviceMem() {
    $s = Api '/api/status' $null
    if (-not $s.ok) { Fail "the device did not answer: $($s.error)" }
    if ($null -eq $s.mem) { Fail "the device did not report a memory" }
    return [int]$s.mem
}

function Read-Pb([int]$mem, [int]$path) {
    $d = Api '/api/eqread' @{ mem = $mem; path = $path }
    if (-not $d.ok) { Fail ("path {0} read failed: {1}" -f $path, $d.error) }
    if (-not $d.passband) { Fail ("path {0} replied without a crossover block" -f $path) }
    return $d.passband
}

# The bytes the bridge would send for a setting: used both as the pre-flight check and as the
# exact expectation for the readback.
function DryRun([int]$path, $low, $high) {
    $d = Api '/api/passband' @{ dryRun = $true; path = $path; low = $low; high = $high }
    if (-not $d.ok) { Fail ("dry run for path {0} failed: {1}" -f $path, $d.error) }
    return $d
}

function Coffs($pbHex) {
    $low = @(); $high = @()
    for ($j = 0; $j -lt 20; $j++) {
        $low += $pbHex.Substring(16 + $j * 8, 8)
        $high += $pbHex.Substring(176 + $j * 8, 8)
    }
    return @{ low = $low; high = $high }
}

function PbTable([int]$mem, [int[]]$paths, [string]$title) {
    Say ""
    Say ("{0}  (memory {1})" -f $title, ($mem + 1))
    Say ("  {0,-4} {1,8} {2,8} {3,8} {4,10} {5,10}" -f 'path', 'hp (Hz)', 'lp (Hz)', 'slopes', 'hp type', 'lp type')
    foreach ($p in $paths) {
        $pb = Read-Pb $mem $p
        $hp = if ([int]$pb.lowFc -le 20) { 'bypass' } else { [string]$pb.lowFc }
        $lp = if ([int]$pb.highFc -ge 20000) { 'open' } else { [string]$pb.highFc }
        Say ("  {0,-4} {1,8} {2,8} {3,8} {4,10} {5,10}" -f $p, $hp, $lp,
            ("{0}/{1}" -f $pb.lowSlope, $pb.highSlope), $pb.lowFilterType, $pb.highFilterType)
    }
}

# 1/2/3 = 12/18/24 dB/oct; 1 = Linkwitz-Riley (the tune's own filter type).
$lowOpen  = @{ fc = $LowFc;   slope = 3; filterType = 1; hiLow = 0; butt = 0; enable = $true }
$highOpen = @{ fc = 20000;    slope = 3; filterType = 1; hiLow = 1; butt = 0; enable = $true }
$tweetHp  = @{ fc = $TweeterFc; slope = 3; filterType = 1; hiLow = 0; butt = 0; enable = $true }

$openList   = @($OpenPaths.Split(',')  | Where-Object { $_ -ne '' } | ForEach-Object { [int]$_ })
$tweetList  = @($TweeterPaths.Split(',') | Where-Object { $_ -ne '' } | ForEach-Object { [int]$_ })

# The snapshot is per memory and lives on disk: the only copy of the crossovers while they are
# open. A second "open" adopts an existing one instead of overwriting it with the open values.
function SnapshotPath([int]$mem) {
    if ($Snapshot) { return $Snapshot }
    return (Join-Path $root ("evidence\crossover-snapshot-mem{0}.json" -f ($mem + 1)))
}

function LoadSnapshot([int]$mem) {
    $f = SnapshotPath $mem
    if (-not (Test-Path $f)) { return $null }
    return (Get-Content -Raw -Encoding UTF8 $f | ConvertFrom-Json)
}

switch ($Action) {

    'plan' {
        Say ""
        Say ("Measurement crossovers: tweeters (paths {0}) high-pass {1} Hz LR24; every other path full range." -f ($tweetList -join ','), $TweeterFc)
        Say "No device needed for this listing; the hex is what cmd 13 would carry."
        foreach ($p in $openList) {
            $low = if ($tweetList -contains $p) { $tweetHp } else { $lowOpen }
            $d = DryRun $p $low $highOpen
            Say ("  path {0}: low {1,6} Hz  high {2,6} Hz  -> {3} bytes  {4}" -f
                $p, $d.lowFc, $d.highFc, $d.pbBytes, $d.pbHex.Substring(0, 32))
        }
        exit 0
    }

    'show' {
        if ($Mem -lt 0) { $Mem = DeviceMem }
        PbTable $Mem $openList 'current crossovers'
        Say ""
        Say ("snapshot on disk: {0}" -f $(if (Test-Path (SnapshotPath $Mem)) { SnapshotPath $Mem } else { 'none' }))
        exit 0
    }

    'snapshot' {
        if ($Mem -lt 0) { $Mem = DeviceMem }
        $f = SnapshotPath $Mem
        if ((Test-Path $f) -and -not $WhatIf) {
            Fail ("a snapshot already exists at {0} - it is never overwritten. Restore it first, or pass -Snapshot with another name." -f $f)
        }
        $rows = @()
        foreach ($p in $openList) {
            $pb = Read-Pb $Mem $p
            $rows += [ordered]@{
                path = $p
                lowFc = [int]$pb.lowFc;  highFc = [int]$pb.highFc
                lowSlope = [int]$pb.lowSlope; highSlope = [int]$pb.highSlope
                lowFilterType = [int]$pb.lowFilterType; highFilterType = [int]$pb.highFilterType
                lowCoffHex = @($pb.lowCoffHex); highCoffHex = @($pb.highCoffHex)
            }
        }
        $snap = [ordered]@{ at = (Get-Date).ToString('s'); mem = $Mem; paths = $rows }
        if ($WhatIf) { Say "would write $f"; $snap | ConvertTo-Json -Depth 5; exit 0 }
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $f) | Out-Null
        $snap | ConvertTo-Json -Depth 5 | Set-Content -Path $f -Encoding UTF8
        Say ("saved {0} path(s) from memory {1} -> {2}" -f $rows.Count, ($Mem + 1), $f)
        exit 0
    }

    'open' {
        if ($Mem -lt 0) { $Mem = DeviceMem }
        $f = SnapshotPath $Mem
        $snap = LoadSnapshot $Mem
        if ($snap) {
            # Adopt what is on disk - never overwrite it - but only after proving it still describes
            # the device we are about to change. A stale snapshot is not a way back: restoring it
            # would write crossovers that were never the state this run opened from. This used to
            # exit here, which meant taking the snapshot first (the safe order) disabled 'open'
            # while still reporting success.
            Say ("a snapshot is already on disk ({0}) - adopting it, not taking a new one." -f $f)
            if ([int]$snap.mem -ne $Mem) {
                Fail ("the snapshot came from memory {0}, but the device is on memory {1}. Switch memory first - a cmd 13 write is addressed to the memory it names." -f ([int]$snap.mem + 1), ($Mem + 1))
            }
            $drift = @()
            foreach ($row in @($snap.paths)) {
                $cur = Read-Pb $Mem ([int]$row.path)
                $curSig = "{0}/{1}/{2}/{3}/{4}/{5}/{6}/{7}" -f [int]$cur.lowFc, [int]$cur.highFc, [int]$cur.lowSlope, [int]$cur.highSlope, [int]$cur.lowFilterType, [int]$cur.highFilterType, ((@($cur.lowCoffHex)) -join ','), ((@($cur.highCoffHex)) -join ',')
                $snapSig = "{0}/{1}/{2}/{3}/{4}/{5}/{6}/{7}" -f [int]$row.lowFc, [int]$row.highFc, [int]$row.lowSlope, [int]$row.highSlope, [int]$row.lowFilterType, [int]$row.highFilterType, ((@($row.lowCoffHex)) -join ','), ((@($row.highCoffHex)) -join ',')
                if ($curSig -ne $snapSig) { $drift += [int]$row.path }
            }
            if ($drift.Count) {
                Fail ("the snapshot does not match the device (paths {0} differ), so it is not a safe way back from here. Move {1} aside and re-run -Action open to take a fresh one." -f ($drift -join ','), (Split-Path -Leaf $f))
            }
            Say ("adopted snapshot matches the device on all {0} path(s) - opening." -f @($snap.paths).Count)
        } else {
        # Snapshot every path first: nothing is written until the way back exists on disk.
        $rows = @()
        foreach ($p in $openList) {
            $pb = Read-Pb $Mem $p
            $rows += [ordered]@{
                path = $p
                lowFc = [int]$pb.lowFc;  highFc = [int]$pb.highFc
                lowSlope = [int]$pb.lowSlope; highSlope = [int]$pb.highSlope
                lowFilterType = [int]$pb.lowFilterType; highFilterType = [int]$pb.highFilterType
                lowCoffHex = @($pb.lowCoffHex); highCoffHex = @($pb.highCoffHex)
            }
        }
        $snap = [ordered]@{ at = (Get-Date).ToString('s'); mem = $Mem; paths = $rows }
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $f) | Out-Null
        $snap | ConvertTo-Json -Depth 5 | Set-Content -Path $f -Encoding UTF8
        Say ("snapshot: {0} path(s) from memory {1} -> {2}" -f $rows.Count, ($Mem + 1), $f)
        }

        if ($WhatIf) { Say "what-if: no cmd 13 sent"; exit 0 }

        $bad = @()
        foreach ($p in $openList) {
            $low = if ($tweetList -contains $p) { $tweetHp } else { $lowOpen }
            $expect = DryRun $p $low $highOpen
            $r = Api '/api/passband' @{ mem = $Mem; path = $p; low = $low; high = $highOpen }
            if (-not $r.ok) { Fail ("path {0} write refused: {1}" -f $p, $r.error) }
            $back = Read-Pb $Mem $p
            $expC = Coffs $expect.pbHex
            $okScalar = ([int]$back.lowFc -eq [int]$expect.lowFc -and [int]$back.highFc -eq [int]$expect.highFc `
                -and [int]$back.lowSlope -eq [int]$expect.lowSlope -and [int]$back.highSlope -eq [int]$expect.highSlope `
                -and [int]$back.lowFilterType -eq [int]$expect.lowFilterType -and [int]$back.highFilterType -eq [int]$expect.highFilterType)
            $okCoff = (($back.lowCoffHex -join ',') -eq ($expC.low -join ',')) -and
                      (($back.highCoffHex -join ',') -eq ($expC.high -join ','))
            $label = if ($tweetList -contains $p) { ("{0} Hz high-pass" -f $TweeterFc) } else { 'full range' }
            if ($okScalar -and $okCoff) {
                Say ("  path {0}: {1,-18} verified (scalars + 40 coefficients)" -f $p, $label)
            } else {
                $bad += $p
                Say ("  path {0}: FAILED - {1}" -f $p, $(if (-not $okScalar) { 'scalars did not read back' } else { 'coefficients did not match' }))
            }
        }
        if ($bad.Count) {
            Say ""
            Fail ("paths {0} did not take the measurement crossover. The snapshot is still on disk; run -Action restore." -f ($bad -join ','))
        }
        Say ""
        Say "CROSSOVERS OPEN - the tune is band-limited only by the tweeter protection."
        Say "Measure now, then run: .\crossover-range.ps1 restore"
        exit 0
    }

    'restore' {
        if ($Mem -lt 0) { $Mem = DeviceMem }
        $snap = LoadSnapshot $Mem
        if (-not $snap) { Fail ("no snapshot at {0} - nothing to restore" -f (SnapshotPath $Mem)) }
        if ([int]$snap.mem -ne $Mem) {
            Fail ("the snapshot came from memory {0}, but the device is on memory {1}. Switch memory first - a cmd 13 write is addressed to the memory it names." -f ([int]$snap.mem + 1), ($Mem + 1))
        }
        if ($WhatIf) { Say ("would restore {0} path(s) from {1}" -f @($snap.paths).Count, (SnapshotPath $Mem)); exit 0 }

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
                Say ("  path {0}: restored to {1} Hz / {2} Hz - verified byte-for-byte" -f $row.path, $row.lowFc, $row.highFc)
            } else {
                $bad += $row.path
                Say ("  path {0}: FAILED - {1}" -f $row.path, $(if (-not $okScalar) { 'scalars did not read back' } else { 'coefficients differ from the snapshot' }))
            }
        }
        if ($bad.Count) {
            Fail ("paths {0} did not restore. The snapshot is untouched; run restore again, or reload the bank: load-tune-bank.ps1 -File web\bor8f3.idsp -Play 1" -f ($bad -join ','))
        }
        Say ""
        Say "restored: every path matches the snapshot. The snapshot file is kept as evidence."
        exit 0
    }
}
