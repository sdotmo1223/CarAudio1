# survey.ps1 - read-only channel survey of the DSP as it sits in the car.
#
# Answers one question with the device's own bytes: which DSP output is which speaker, and what
# is on it right now (crossover, EQ, gain, delay, mute). It exists because the Tune tab's
# speaker setup is a claim about the car, and the only thing that settles a claim about the car
# is the car - so this reads every output path and prints the device's answer next to the
# section-18 walk map, which is the only other record of what is physically wired.
#
# READ ONLY. It calls /api/info (a snapshot) and /api/eqread (cmd 9 sub 2 read). It never calls
# a write endpoint, and reads never select a memory (verified 2026-10-17).
#
# Usage:
#   .\survey.ps1                        # the memory the device is on
#   .\survey.ps1 -Mem 1 -Tag "car-am"   # a specific memory, tagged evidence file
#
# Output: the table on the console, full JSON in evidence\channel-survey-<stamp>.json

param(
    [int]$Port = 8585,
    [int]$Mem = -1,
    [string]$Tag = "",
    [switch]$Quiet            # table only, no per-path progress lines
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$base = "http://127.0.0.1:$Port"
$stamp = (Get-Date).ToString('yyyy-MM-dd-HHmmss')

# The section-18 walk map: which physical speaker hangs off which DSP output, established live on
# 2026-10-16 by muting/leveling single outputs and walking the car. Only the NAME travels with
# this tool - the crossover numbers in that map belong to the tune that was loaded that day
# (w6784b), not to whatever is loaded now, so they are printed as a reference and a difference
# is not an error. The tune file is the authority for crossovers; this map is the authority for
# which speaker is which output.
$walkMap = @(
    @{ path = 0; speaker = 'driver tweeter';        hp = 2000; lp = 20000 }
    @{ path = 1; speaker = 'passenger tweeter';     hp = 2000; lp = 20000 }
    @{ path = 2; speaker = 'rear driver tweeter';   hp = 2000; lp = 20000 }
    @{ path = 3; speaker = 'rear passenger tweeter';hp = 2000; lp = 20000 }
    @{ path = 4; speaker = 'front 3.5"';            hp = 250;  lp = 2500 }
    @{ path = 5; speaker = 'rear 3.5"';             hp = 250;  lp = 2500 }
    @{ path = 6; speaker = 'front 8"';              hp = 150;  lp = 2000 }
    @{ path = 7; speaker = 'rear 6.5"';             hp = 120;  lp = 1000 }
)

# The app's tune name (tunename.json) is what the UI claims is loaded; it is printed so the
# survey and the label can be compared in the same breath. ($root is script scope - inside a
# function $MyInvocation.MyCommand.Path is empty, which is why this reads it from there.)
function Get-TuneName {
    try {
        $p = Join-Path $root 'tunename.json'
        if (Test-Path $p) { return (Get-Content $p -Raw | ConvertFrom-Json).name }
    } catch { }
    return '(none)'
}

function Get-Json([string]$url) {
    (Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 10).Content | ConvertFrom-Json
}

function Post-Json([string]$url, $obj) {
    $body = $obj | ConvertTo-Json -Compress
    (Invoke-WebRequest -Uri $url -Method POST -UseBasicParsing -TimeoutSec 15 `
        -ContentType 'application/json' -Body $body).Content | ConvertFrom-Json
}

Write-Host "survey $base  (read only)" -ForegroundColor Cyan
$info = Get-Json "$base/api/info"

if (-not $info.open) {
    Write-Host "DSP not connected: $($info.error)" -ForegroundColor Yellow
    Write-Host "Nothing read. Power the amp / reseat the cable, then run this again." -ForegroundColor Yellow
    exit 2
}

$mem = if ($Mem -ge 0) { $Mem } else { [int]$info.mem }
$n = [int]$info.outPathCount
$src = [int]$info.source

Write-Host ("device open - mem {0} (memory {1} of 4) - source {2} - {3} outputs - master {4} dB - count source '{5}'" -f `
    $mem, ($mem + 1), $src, $n, $info.mainDb, $info.outPathCountSource)
Write-Host ""

$rows = @()
for ($p = 0; $p -lt $n; $p++) {
    if (-not $Quiet) { Write-Host ("  reading path {0} ..." -f $p) }
    $r = Post-Json "$base/api/eqread" @{ mem = $mem; path = $p }
    if (-not $r.ok) {
        Write-Host ("path {0}: read failed: {1}" -f $p, $r.error) -ForegroundColor Red
        continue
    }
    $pb = $r.passband
    $on = 0; $lo = 0.0; $hi = 0.0; $first = $true
    foreach ($b in $r.bands) {
        if ([Math]::Abs([double]$b.gain) -ge 0.05) {
            $on++
            if ($first) { $lo = [double]$b.gain; $hi = $lo; $first = $false }
            else {
                if ([double]$b.gain -lt $lo) { $lo = [double]$b.gain }
                if ([double]$b.gain -gt $hi) { $hi = [double]$b.gain }
            }
        }
    }
    $map = $walkMap | Where-Object { $_.path -eq $p } | Select-Object -First 1
    $rows += [pscustomobject]@{
        path      = $p
        walk      = if ($map) { $map.speaker } else { '(not in the walk map)' }
        hp        = if ($pb) { [int]$pb.lowFc } else { -1 }
        lp        = if ($pb) { [int]$pb.highFc } else { -1 }
        hpSlope   = if ($pb) { [int]$pb.lowSlope } else { -1 }
        lpSlope   = if ($pb) { [int]$pb.highSlope } else { -1 }
        hpType    = if ($pb) { [int]$pb.lowFilterType } else { -1 }
        lpType    = if ($pb) { [int]$pb.highFilterType } else { -1 }
        bands     = $on
        minGain   = if ($on) { [Math]::Round($lo, 1) } else { 0 }
        maxGain   = if ($on) { [Math]::Round($hi, 1) } else { 0 }
        gainDb    = if ($info.outputGainDb) { [double]$info.outputGainDb[$p] } else { $null }
        delayMs   = if ($info.outputDelayMs) { [double]$info.outputDelayMs[$p] } else { $null }
        mute      = if ($info.outputMute) { [bool]$info.outputMute[$p] } else { $null }
        revert    = if ($info.outputRevert) { [bool]$info.outputRevert[$p] } else { $null }
        byPass    = [bool]$r.byPass
        wireBytes = [int]$r.wireBytes
    }
}

Write-Host ""
$rows | Format-Table path, walk, hp, lp, hpSlope, lpSlope, bands, minGain, maxGain, gainDb, delayMs, mute, byPass -AutoSize

# Any path whose crossover disagrees with the walk map is the interesting line: either the map
# is stale or the device is holding a different tune.
$mismatch = @()
foreach ($r in $rows) {
    $m = $walkMap | Where-Object { $_.path -eq $r.path } | Select-Object -First 1
    if ($m -and ($r.hp -ne $m.hp -or $r.lp -ne $m.lp)) {
        $mismatch += ("path {0} ({1}): device {2}-{3} Hz, walk map said {4}-{5} Hz" -f `
            $r.path, $m.speaker, $r.hp, $r.lp, $m.hp, $m.lp)
    }
}
Write-Host ""
Write-Host ("app's tune name (tunename.json): {0}" -f (Get-TuneName))

# A path whose crossover differs from the 2026-10-16 reference is only interesting if the loaded
# tune is supposed to BE that reference. Keeping the numbers visible is what matters: the walk
# map pair is from w6784b, and bor8f3 (for example) crosses its tweeters at 2500, not 2000.
$diff = @()
foreach ($r in $rows) {
    $m = $walkMap | Where-Object { $_.path -eq $r.path } | Select-Object -First 1
    if ($m -and ($r.hp -ne $m.hp -or $r.lp -ne $m.lp)) {
        $diff += ("path {0} ({1}): device {2}-{3} Hz, 2026-10-16 reference was {4}-{5} Hz" -f `
            $r.path, $m.speaker, $r.hp, $r.lp, $m.hp, $m.lp)
    }
}
Write-Host ""
if ($diff.Count) {
    Write-Host "crossovers differ from the 2026-10-16 (w6784b) reference on $($diff.Count) path(s) - expected unless that tune is loaded:" -ForegroundColor Yellow
    $diff | ForEach-Object { Write-Host ("  " + $_) -ForegroundColor Yellow }
} else {
    Write-Host "crossovers match the 2026-10-16 (w6784b) reference on every path" -ForegroundColor Green
}

$evid = Join-Path $root 'evidence'
New-Item -ItemType Directory -Force -Path $evid | Out-Null
$name = if ($Tag) { "channel-survey-$stamp-$Tag.json" } else { "channel-survey-$stamp.json" }
$out = Join-Path $evid $name
[pscustomobject]@{
    when = (Get-Date).ToString('s')
    port = $Port
    mem  = $mem
    source = $src
    outPathCount = $n
    info = $info
    paths = $rows
    walkMapDiffs = $diff
    tuneName = (Get-TuneName)
    note = 'read-only survey: /api/info + /api/eqread per path. No write endpoint was called.'
} | ConvertTo-Json -Depth 8 | Set-Content -Path $out -Encoding UTF8
Write-Host ""
Write-Host "evidence -> evidence\$name"
