# doctor.ps1 - one command that says whether the rig is ready to measure.
#
# The app can look fine while the things a measurement depends on are missing: the DSP is on a
# different memory, sweep mode was left on (EQ flat, master -8), the crossovers are still open
# from last night (the tune is band-limited by the tweeter protection only), the UMIK-1 is
# unplugged, rta.exe is not running, or the cal file is gone. Each of those has cost a run.
#
# This is read-only. The one exception is -Restore, which puts the crossovers back from the
# snapshot crossover-range.ps1 wrote, using the same verified write path.
#
#   .\doctor.ps1                 # the screen
#   .\doctor.ps1 -Restore        # close the crossovers if they are open
#   .\doctor.ps1 -Json           # machine-readable, also written to doctor.json
#
# Exit 0 = ready (warnings allowed), 1 = something blocks a measurement.

param(
    [switch]$Json,
    [switch]$Restore,
    [switch]$Deep,                    # read every path 0-16, not just the 8 outputs
    [switch]$SelfTest,                # compare the snapshot logic against the saved baseline
    [string]$Base = 'http://127.0.0.1:8585',
    [string]$RtaBase = 'http://127.0.0.1:8586',
    [int]$Mem = -1
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$rows = @()

function Add([string]$level, [string]$area, [string]$msg) {
    $script:rows += [pscustomobject]@{ level = $level; area = $area; msg = $msg }
}

function GetJson([string]$url, [int]$ms = 4000) {
    try { return Invoke-RestMethod -Uri $url -TimeoutSec ([Math]::Max(1, [int]($ms / 1000))) }
    catch { return $null }
}

function PostJson([string]$url, $body, [int]$ms = 8000) {
    try {
        return Invoke-RestMethod -Uri $url -Method Post -ContentType 'application/json' `
            -Body ($body | ConvertTo-Json -Depth 5) -TimeoutSec ([Math]::Max(1, [int]($ms / 1000)))
    } catch { return $null }
}

# Which snapshot paths no longer match the device? This is the check that decides whether the
# tune is still band-limited only by the tweeter protection, so it gets its own self-test.
function Get-OpenPaths($snap, $reads) {
    $open = @()
    foreach ($row in @($snap.paths)) {
        # The property must exist. A read with no path is not path 0 - [int]$null is 0, and
        # that coincidence is precisely how seven paths went unchecked.
        $now = $reads |
            Where-Object { $_.PSObject.Properties['path'] -and [int]$_.path -eq [int]$row.path } |
            Select-Object -First 1
        # An unread path is NOT "unchanged". This used to `continue`, which failed open: every
        # live read carried $null for path, [int]$null is 0, so every row except path 0 found no
        # match and was skipped - seven open crossovers would have reported as closed.
        if (-not $now) { $open += [int]$row.path; continue }
        $pb = $now.passband
        $same = ([int]$pb.lowFc -eq [int]$row.lowFc -and [int]$pb.highFc -eq [int]$row.highFc -and
                 [int]$pb.lowSlope -eq [int]$row.lowSlope -and [int]$pb.highSlope -eq [int]$row.highSlope -and
                 [int]$pb.lowFilterType -eq [int]$row.lowFilterType -and
                 [int]$pb.highFilterType -eq [int]$row.highFilterType)
        if (-not $same) { $open += [int]$row.path }
    }
    return @($open)
}

# ---------------------------------------------------------------- self-test
# Replays the device's saved readback as if it were a live read, then mutates one path. A
# comparator that has never been seen to fail has verified nothing.
if ($SelfTest) {
    $f = Join-Path $root 'evidence\passband-baseline-2026-10-16.json'
    if (-not (Test-Path $f)) { Write-Host "FAIL: baseline missing: $f"; exit 1 }
    $baseline = Get-Content -Raw -Encoding UTF8 $f | ConvertFrom-Json
    $reads = @(); $snapPaths = @()
    $i = 0
    foreach ($r in @($baseline.reads)) {
        $pb = $r.passband
        $reads += [pscustomobject]@{ path = $i; passband = $pb }
        $snapPaths += [pscustomobject]@{
            path = $i; lowFc = $pb.lowFc; highFc = $pb.highFc
            lowSlope = $pb.lowSlope; highSlope = $pb.highSlope
            lowFilterType = $pb.lowFilterType; highFilterType = $pb.highFilterType
        }
        $i++
    }
    $snap = [pscustomobject]@{ mem = 1; paths = $snapPaths }
    $pass = 0; $fail = 0
    $open = Get-OpenPaths $snap $reads
    if ($open.Count -eq 0) { Write-Host "  PASS  unchanged device reads as closed"; $pass++ }
    else { Write-Host ("  FAIL  unchanged device reported open: {0}" -f ($open -join ',')); $fail++ }

    $reads[3].passband.lowFc = 1000          # path 3 was 2500 Hz; this is the tweeter open
    $open = Get-OpenPaths $snap $reads
    if ($open.Count -eq 1 -and $open[0] -eq 3) { Write-Host "  PASS  one changed path is named, and only it"; $pass++ }
    else { Write-Host ("  FAIL  expected path 3 only, got: {0}" -f ($open -join ',')); $fail++ }

    $reads[3].passband.lowFc = 2500
    $reads[6].passband.highFc = 20000        # path 6 was 2500 Hz; this is the 8" opened
    $open = Get-OpenPaths $snap $reads
    if ($open.Count -eq 1 -and $open[0] -eq 6) { Write-Host "  PASS  a changed high edge is caught too"; $pass++ }
    else { Write-Host ("  FAIL  expected path 6 only, got: {0}" -f ($open -join ',')); $fail++ }

    # The shape the live loop actually had until 2026-10-21: reads with no path field at all.
    # /api/eqread echoes no path, so every one of these used to find no match and be skipped -
    # seven open crossovers reporting "closed". All of them must now count as open.
    $bare = @()
    foreach ($r in $reads) { $bare += [pscustomobject]@{ passband = $r.passband } }
    $open = Get-OpenPaths $snap $bare
    if ($open.Count -eq @($snap.paths).Count) {
        Write-Host "  PASS  reads without a path are unproven, not unchanged"; $pass++
    }
    else { Write-Host ("  FAIL  expected all {0} paths open, got: {1}" -f @($snap.paths).Count, ($open -join ',')); $fail++ }

    Write-Host ""
    Write-Host ("selftest: {0} passed, {1} failed" -f $pass, $fail)
    exit $(if ($fail -gt 0) { 1 } else { 0 })
}

# ---------------------------------------------------------------- bridge + DSP
$status = GetJson "$Base/api/status" 2500
$dsp = $false
$mem = $Mem
if (-not $status) {
    Add 'fail' 'bridge' "not answering on $Base - start it with .\DreTunes.ps1"
} elseif (-not $status.ok) {
    Add 'warn' 'dsp' "bridge is up, DSP not connected ($($status.error)). Reads below are skipped."
} else {
    $dsp = $true
    $mem = [int]$status.mem
    $src = if ($status.sourceName) { $status.sourceName } else { "source $($status.source)" }
    Add 'ok' 'dsp' ("memory {0}, master {1:N1} dB, {2}, {3}" -f ($mem + 1), [double]$status.mainDb,
        $(if ($status.mute) { 'muted' } else { 'unmuted' }), $src)
    if ($status.mute) { Add 'warn' 'dsp' 'the master is muted: a run will hear nothing' }
}

$paths = if ($Deep) { 0..16 } else { 0..7 }
$reads = @()
$flat = 0
$open = @()

if ($dsp) {
    foreach ($p in $paths) {
        $r = PostJson "$Base/api/eqread" @{ mem = $mem; path = $p } 4000
        if (-not $r -or -not $r.ok) {
            Add 'fail' 'read' ("path {0}: {1}" -f $p, $(if ($r) { $r.error } else { 'no reply' }))
            continue
        }
        # Stamp the path onto the read. /api/eqread does not echo it, and the crossover check
        # matches snapshot rows to reads by path - without this every read looked like path 0.
        $r | Add-Member -NotePropertyName path -NotePropertyValue $p -Force
        $reads += $r
        $allZero = $true
        foreach ($b in @($r.bands)) { if ([Math]::Abs([double]$b.gain) -gt 0.05) { $allZero = $false; break } }
        if ($allZero -and -not $r.byPass) { $flat++ }
    }
    if ($reads.Count -eq $paths.Count) {
        Add 'ok' 'read' ("{0} path(s) read: EQ and crossover blocks" -f $reads.Count)
    }
}

# ---------------------------------------------------------------- crossovers
$snapFile = Join-Path $root ("evidence\crossover-snapshot-mem{0}.json" -f ($mem + 1))
$snaps = @(Get-ChildItem (Join-Path $root 'evidence') -Filter 'crossover-snapshot-mem*.json' -ErrorAction SilentlyContinue)
if ($snaps.Count -eq 0) {
    Add 'info' 'crossover' 'no snapshot on disk: nothing is open from crossover-range.ps1'
} elseif (-not $dsp) {
    Add 'info' 'crossover' ("snapshot on disk ({0}) but the DSP is unplugged: connect it to check whether the crossovers are open" -f
        (($snaps | ForEach-Object { $_.BaseName -replace '^crossover-snapshot-mem', 'memory ' }) -join ', '))
} elseif (-not (Test-Path $snapFile)) {
    Add 'info' 'crossover' ("snapshots exist but none for memory {0}: {1}" -f ($mem + 1),
        (($snaps | ForEach-Object { $_.Name }) -join ', '))
} elseif ($reads.Count -gt 0) {
    $snap = Get-Content -Raw -Encoding UTF8 $snapFile | ConvertFrom-Json
    if ([int]$snap.mem -ne $mem) {
        Add 'info' 'crossover' ("snapshot is from memory {0}, device is on memory {1} - not comparable now" -f
            ([int]$snap.mem + 1), ($mem + 1))
    } else {
        $open = Get-OpenPaths $snap $reads
        if ($open.Count -gt 0) {
            Add 'fail' 'crossover' ("OPEN on path(s) {0}: the tune is not as it was. Run .\crossover-range.ps1 restore" -f ($open -join ','))
        } else {
            Add 'ok' 'crossover' ("closed: all {0} path(s) match the snapshot" -f @($snap.paths).Count)
        }
    }
}

# ---------------------------------------------------------------- sweep mode left on
if ($dsp -and $reads.Count -gt 0) {
    $main = [double]$status.mainDb
    if ($flat -eq $reads.Count -and $reads.Count -gt 0 -and [Math]::Abs($main + 8.0) -lt 0.05) {
        Add 'warn' 'sweep' 'EQ is flat on every path and the master is -8.0 dB: sweep mode looks active. Turn it off in the page (it restores the tune).'
    } elseif ($flat -eq $reads.Count -and $reads.Count -gt 0) {
        Add 'info' 'sweep' ("EQ is flat on every path read (master {0:N1} dB)" -f $main)
    }
}

# ---------------------------------------------------------------- RTA daemon + mic
$rta = GetJson "$RtaBase/rta.json" 2500
if (-not $rta) {
    Add 'warn' 'rta' "rta.exe is not answering on $RtaBase - the Tune tab will be empty"
} else {
    $devs = GetJson "$RtaBase/devices" 2500
    $umik = if ($devs -and $devs.devices) { @($devs.devices | Where-Object { $_.name -match 'UMIK' }) } else { @() }
    if ($umik.Count -eq 0) {
        Add 'warn' 'mic' 'no UMIK-1 among the capture devices - the cal file cannot apply'
    } elseif ($rta.device -notmatch 'UMIK') {
        # rta.exe remembers nothing: every start comes back on the system default, which on this
        # laptop is the Realtek array - the exact state that silently poisoned a night of
        # captures. Switch it here, then confirm on a fresh snapshot instead of assuming the
        # switch took, because a warning nobody acts on is how that night happened.
        $idx = [int]$umik[0].index
        [void](GetJson "$RtaBase/device?n=$idx" 4000)
        Start-Sleep -Milliseconds 900
        $sw = GetJson "$RtaBase/rta.json" 2500
        if ($sw -and $sw.device -match 'UMIK') {
            Add 'ok' 'mic' ("rta switched to the UMIK-1 (input {0}); it had been on '{1}'" -f $idx, $rta.device)
            $rta = $sw
        } else {
            Add 'warn' 'mic' ("rta is on '{0}', not the UMIK-1 - run GET {1}/device?n={2}" -f $rta.device, $RtaBase, $idx)
        }
    } else {
        Add 'ok' 'mic' ("rta is on the UMIK-1 (input {0}) - the cal file applies" -f $umik[0].index)
    }
    Add 'ok' 'rta' ("{0} @ {1} Hz, {2}" -f $rta.device, $rta.fs, $rta.status)
    if ($rta.levelDb -ne $null -and [double]$rta.levelDb -lt -80) {
        Add 'warn' 'mic' ("level {0:N1} dBFS is silence: check the Windows input level and that the UMIK-1 is selected" -f [double]$rta.levelDb)
    }
}

try {
    $pnp = @(Get-PnpDevice -FriendlyName '*UMIK*' -ErrorAction Stop)
    if ($pnp.Count -eq 0) { Add 'warn' 'usb' 'UMIK-1 is not in the device tree' }
    else {
        foreach ($g in ($pnp | Group-Object Status)) {
            $names = ($g.Group | ForEach-Object { $_.FriendlyName }) -join ' / '
            if ($g.Name -eq 'OK') { Add 'ok' 'usb' ("{0}: {1}" -f $names, $g.Name) }
            else { Add 'warn' 'usb' ("{0}: {1} (re-plug it; sharing a hub with the DSP can drop it)" -f $names, $g.Name) }
        }
    }
} catch { Add 'info' 'usb' 'Get-PnpDevice unavailable; skipped the device-tree check' }

# ---------------------------------------------------------------- files and tools
$cal = Join-Path $root 'cal\7170061-0deg.txt'
if (Test-Path $cal) {
    $lines = @(Get-Content $cal | Where-Object { $_ -match '^\s*[\d.]' })
    Add 'ok' 'cal' ("UMIK-1 cal file: {0} points, {1:yyyy-MM-dd}" -f $lines.Count, (Get-Item $cal).LastWriteTime)
} else {
    Add 'warn' 'cal' "cal\7170061-0deg.txt is missing: the RTA would plot an uncalibrated curve"
}

$ff = Join-Path $env:SystemDrive 'tools\ffmpeg\ffmpeg-master-latest-win64-gpl\bin\ffmpeg.exe'
$ffplay = Join-Path $env:SystemDrive 'tools\ffmpeg\ffmpeg-master-latest-win64-gpl\bin\ffplay.exe'
$csc = Join-Path $env:windir 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
foreach ($t in @(
    @{ n = 'ffmpeg'; p = $ff }, @{ n = 'ffplay'; p = $ffplay }, @{ n = 'csc'; p = $csc },
    @{ n = 'ds18-bridge.exe'; p = (Join-Path $root 'bin\ds18-bridge.exe') },
    @{ n = 'rta.exe'; p = (Join-Path $root 'bin\rta.exe') },
    @{ n = 'sweep-tool.exe'; p = (Join-Path $root 'bin\sweep-tool.exe') })) {
    if (Test-Path $t.p) { Add 'ok' 'tool' ("{0} {1:yyyy-MM-dd HH:mm}" -f $t.n, (Get-Item $t.p).LastWriteTime) }
    else { Add 'fail' 'tool' ("{0} missing: {1}" -f $t.n, $t.p) }
}

# ---------------------------------------------------------------- data + disk
$meas = @(Get-ChildItem (Join-Path $root 'measurements') -Filter '*.wav' -ErrorAction SilentlyContinue)
if ($meas.Count -gt 0) {
    $last = $meas | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    Add 'ok' 'data' ("{0} recording(s), newest {1:yyyy-MM-dd HH:mm} ({2:N0} KB)" -f $meas.Count, $last.LastWriteTime, ($last.Length / 1KB))
} else { Add 'info' 'data' 'no raw recordings yet' }

$backups = @(Get-ChildItem (Join-Path $root 'evidence\backups') -Filter '*.zip' -ErrorAction SilentlyContinue)
if ($backups.Count -gt 0) {
    $b = $backups | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    Add 'ok' 'backup' ("{0} revert point(s), newest {1:yyyy-MM-dd HH:mm}" -f $backups.Count, $b.LastWriteTime)
} else { Add 'warn' 'backup' 'no code backup: a bad edit has no undo' }

try {
    $free = (Get-PSDrive C).Free / 1GB
    if ($free -lt 5) { Add 'warn' 'disk' ("{0:N1} GB free on C:" -f $free) }
    else { Add 'ok' 'disk' ("{0:N1} GB free on C:" -f $free) }
} catch { }

# ---------------------------------------------------------------- restore if asked
if ($Restore) {
    if ($open.Count -eq 0) {
        Add 'info' 'restore' 'nothing to restore'
    } elseif (-not (Test-Path (Join-Path $root 'crossover-range.ps1'))) {
        Add 'fail' 'restore' 'crossover-range.ps1 is missing'
    } else {
        Add 'info' 'restore' ("closing path(s) {0} ..." -f ($open -join ','))
        & (Join-Path $root 'crossover-range.ps1') restore
        $code = $LASTEXITCODE
        if ($code -eq 0) { Add 'ok' 'restore' 'crossovers restored and verified' }
        else { Add 'fail' 'restore' "crossover-range.ps1 restore exited $code" }
        $open = @()
    }
}

# ---------------------------------------------------------------- verdict + output
$fails = @($rows | Where-Object { $_.level -eq 'fail' }).Count
$warns = @($rows | Where-Object { $_.level -eq 'warn' }).Count
$verdict = if ($fails -gt 0) { 'NOT READY' } elseif ($warns -gt 0) { 'READY WITH WARNINGS' } else { 'READY' }
$color = if ($fails -gt 0) { 'Red' } elseif ($warns -gt 0) { 'Yellow' } else { 'Green' }

$doc = [ordered]@{
    at = (Get-Date).ToString('s')
    verdict = $verdict
    fails = $fails
    warns = $warns
    dsp = $dsp
    mem = $(if ($dsp) { $mem } else { $null })
    crossoversOpen = @($open)
    rows = $rows
}
$doc | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $root 'doctor.json') -Encoding UTF8

if ($Json) { $doc | ConvertTo-Json -Depth 5; exit $(if ($fails -gt 0) { 1 } else { 0 }) }

Write-Host ""
foreach ($r in $rows) {
    $tag = switch ($r.level) {
        'ok' { '  ok  ' } 'warn' { ' warn ' } 'fail' { ' FAIL ' } default { ' info ' }
    }
    $c = switch ($r.level) { 'ok' { 'Green' } 'warn' { 'Yellow' } 'fail' { 'Red' } default { 'DarkGray' } }
    Write-Host ("{0} {1,-10} {2}" -f $tag, $r.area, $r.msg) -ForegroundColor $c
}
Write-Host ""
Write-Host ("  {0}   {1} fail, {2} warn" -f $verdict, $fails, $warns) -ForegroundColor $color
Write-Host "  doctor.json written" -ForegroundColor DarkGray
if ($open.Count -gt 0 -and -not $Restore) {
    Write-Host ""
    Write-Host ("  crossovers are open on path(s) {0}." -f ($open -join ',')) -ForegroundColor Yellow
    Write-Host "  close them:  .\doctor.ps1 -Restore   (or .\crossover-range.ps1 restore)" -ForegroundColor Yellow
}
exit $(if ($fails -gt 0) { 1 } else { 0 })
