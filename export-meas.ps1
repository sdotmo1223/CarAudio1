# export-meas.ps1 - write every capture out as its own self-describing file.
#
# /api/rta-meas is one big array, which is fine for a plot and useless for handing a single channel
# to a person or a model. This splits a run into one JSON + one CSV per capture, plus a README that
# states the conditions the numbers were taken under - because a curve with no context is a trap:
# the same driver reads 20 dB apart depending on stim level and crossover.
#
# Each JSON is deliberately verbose and self-contained:
#   channel / type / capturedAt / run
#   gate      the analyser's own verdict for that take (ok, burstDb, fromHz, toHz)
#   peak      loudest point and where it is
#   points    61 [hz, dbfs] pairs, 1/6-octave from 20 Hz
#
#   .\export-meas.ps1 -Last 8 -Label "first-real-run"
#   .\export-meas.ps1 -Last 8 -OutDir measurements\run-x -Notes "extra context"
param(
    [int]$Last = 8,
    [string]$OutDir = '',
    [string]$Label = 'run',
    [string]$Notes = '',
    [string]$Crossovers = 'path 0-3 HP1500 / 4-5 HP80 / 6 HP50 / 7 HP60, all LP open 20k, LR24',
    [string]$Conditions = 'engine off, sub amp disconnected, head unit 20/30, DSP master -4.8 dB, EQ flat, stim -6 dB'
)

$ErrorActionPreference = 'Stop'

$d = Invoke-RestMethod 'http://127.0.0.1:8585/api/rta-meas' -TimeoutSec 15
$items = @(@($d.meas) | Select-Object -Last $Last)
if ($items.Count -eq 0) { throw 'no measurements on the bridge' }

$stamp = (Get-Date).ToString('yyyyMMdd-HHmm')
$runId = '{0}-{1}' -f $Label, $stamp
if (-not $OutDir) { $OutDir = Join-Path 'measurements' ('run-' + $stamp) }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# The grid follows each take: current takes are 1/24-octave (241 points), legacy takes
# 1/6 (61). An old take re-exported must never have its frequencies relabelled.
$lastGstep = 6; $lastGcount = 61

function Slug([string]$s) {
    $t = ($s.ToLower() -replace '[^a-z0-9]+', '-').Trim('-')
    if (-not $t) { $t = 'channel' }
    return $t
}

$summary = @()
$seen = @{}
foreach ($m in $items) {
    $db = @($m.db | ForEach-Object { [double]$_ })
    if ($db.Count -lt 10) { continue }

    $gcount = if ($db.Count -gt 100) { 241 } else { 61 }
    $gstep = if ($db.Count -gt 100) { 24 } else { 6 }
    $lastGstep = $gstep; $lastGcount = $gcount
    $grid = 0..($gcount - 1) | ForEach-Object { [math]::Round(20 * [math]::Pow(2, $_ / $gstep), 2) }

    $name = if ($m.name) { [string]$m.name } else { 'unnamed' }
    $type = ($name -split '\s+')[0].ToLower()

    # duplicate names must not overwrite each other: tweeter-1, tweeter-1-2, ...
    $slug = Slug $name
    if ($seen.ContainsKey($slug)) { $seen[$slug]++; $slug = '{0}-{1}' -f $slug, $seen[$slug] }
    else { $seen[$slug] = 1 }

    $peakDb = ($db | Measure-Object -Maximum).Maximum
    $peakHz = 0
    for ($i = 0; $i -lt [Math]::Min($db.Count, $gcount); $i++) { if ($db[$i] -eq $peakDb) { $peakHz = $grid[$i]; break } }

    $when = [DateTimeOffset]::FromUnixTimeMilliseconds([long]$m.t).ToLocalTime()

    $points = @()
    for ($i = 0; $i -lt [Math]::Min($db.Count, $gcount); $i++) { $points += , @($grid[$i], [math]::Round($db[$i], 2)) }

    $rec = [ordered]@{
        channel    = $name
        type       = $type
        run        = $runId
        capturedAt = $when.ToString('yyyy-MM-ddTHH:mm:sszzz')
        epochMs    = [long]$m.t
        units      = 'dBFS'
        grid       = ('1/{0} octave, {1} points, 20 Hz to 20 kHz' -f $gstep, $gcount)
        kind       = [string]$m.kind
        gate       = [ordered]@{
            ok      = [bool]$m.sig.ok
            burstDb = if ($null -ne $m.sig.burstDb) { [math]::Round([double]$m.sig.burstDb, 2) } else { $null }
            fromHz  = if ($null -ne $m.sig.from) { [int]$m.sig.from } else { $null }
            toHz    = if ($null -ne $m.sig.to) { [int]$m.sig.to } else { $null }
        }
        peak       = [ordered]@{ dbfs = [math]::Round($peakDb, 2); hz = $peakHz }
        conditions = [ordered]@{
            crossovers = $Crossovers
            rig        = $Conditions
            note       = $Notes
        }
        points     = $points
    }

    $jsonPath = Join-Path $OutDir ($slug + '.json')
    $rec | ConvertTo-Json -Depth 6 | Set-Content -Path $jsonPath -Encoding UTF8

    $csv = @('hz,dbfs')
    for ($i = 0; $i -lt $db.Count; $i++) { $csv += ('{0},{1}' -f $grid[$i], [math]::Round($db[$i], 2)) }
    $csv | Set-Content -Path (Join-Path $OutDir ($slug + '.csv')) -Encoding ASCII

    # REW copy (File > Import frequency response, Ctrl+I): only lines beginning with a
    # number are imported; the '*' header is provenance and is ignored by REW.
    $rewDir = Join-Path $OutDir 'rew'
    if (-not (Test-Path $rewDir)) { New-Item -ItemType Directory -Force -Path $rewDir | Out-Null }
    $rew = @(
        ('* {0}  -  ds18-bridge measurement {1}' -f $name, $runId)
        ('* captured {0}, absolute dBFS at the mic, EQ flat, not normalised' -f $when.ToString('yyyy-MM-dd HH:mm'))
        '* import in REW: File > Import frequency response (Ctrl+I)'
        ('* grid: 1/{0} octave, {1} points, 20 Hz to 20 kHz' -f $gstep, $gcount)
    )
    for ($i = 0; $i -lt [Math]::Min($db.Count, $gcount); $i++) { $rew += ('{0} {1}' -f $grid[$i], $db[$i]) }
    $rew | Set-Content -Path (Join-Path $rewDir ($slug + '.txt')) -Encoding ASCII

    $summary += [pscustomobject]@{
        file = ($slug + '.json'); channel = $name; peak = $peakDb; at = $peakHz
        burst = [double]$m.sig.burstDb; ok = [bool]$m.sig.ok; from = [int]$m.sig.from; to = [int]$m.sig.to
    }
}

$readme = @(
    ('# Run {0}' -f $runId)
    ''
    ('Captured from the DSP/UMIK-1 rig on {0}. One file per channel: `<channel>.json` (full record)' -f (Get-Date).ToString('yyyy-MM-dd HH:mm'))
    'and `<channel>.csv` (two columns: hz, dbfs) for anything that prefers a table.'
    ''
    '## Conditions these numbers were taken under'
    ''
    ('- crossovers: {0}' -f $Crossovers)
    ('- rig: {0}' -f $Conditions)
    '- microphone: UMIK-1 (cal file applied), RTA at 48 kHz'
    ('- grid: 1/{0} octave, {1} points, 20 Hz - 20 kHz, values in dBFS (absolute, not normalised)' -f $lastGstep, $lastGcount)
)
if ($Notes) { $readme += ('- notes: {0}' -f $Notes) }
$readme += @(
    ''
    '## What is in each file'
    ''
    '| field | meaning |'
    '|---|---|'
    '| `gate.ok` | the analyser accepted the take (presence, completeness, correlation) |'
    '| `gate.burstDb` | dynamic range of that take: peak above the take''s own floor |'
    '| `gate.fromHz` / `toHz` | where the analyser saw the signal rise and fall |'
    '| `peak` | loudest point of the curve and its frequency |'
    ('| `points` | {0} `[hz, dbfs]` pairs (1/{1} octave) |' -f $lastGcount, $lastGstep)
    ''
    'A curve is only comparable to another curve taken with the same crossovers, level and mic'
    'position. `gate.burstDb` under ~40 means the take is thin; `gate.ok` false means it is not a'
    'measurement at all.'
    ''
    '## This run'
    ''
    '| file | channel | peak dBFS | at | burst dB | gate | band seen |'
    '|---|---|---|---|---|---|---|'
)
foreach ($s in $summary) {
    $readme += ('| `{0}` | {1} | {2:N1} | {3} Hz | {4:N1} | {5} | {6}-{7} Hz |' -f
        $s.file, $s.channel, $s.peak, $s.at, $s.burst, $(if ($s.ok) { 'ok' } else { 'REJECT' }), $s.from, $s.to)
}
$readme | Set-Content -Path (Join-Path $OutDir 'README.md') -Encoding UTF8

Write-Output ''
Write-Output ('  {0} channel file(s) written to {1}' -f $summary.Count, $OutDir)
foreach ($s in $summary) { Write-Output ('    {0,-22} {1,6:N1} dBFS at {2,6} Hz' -f $s.file, $s.peak, $s.at) }
Write-Output ('  index: ' + (Join-Path $OutDir 'README.md'))
