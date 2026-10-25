# measure.ps1 - scripted RTA capture for evidence.
#
# Polls the rta daemon's HTTP endpoint (NOT the file - the capture thread never
# writes files; see RTAMeter.cs), averages the 1/24-octave bands over N seconds,
# prints a tilt readout and saves the JSON next to this script.
#
# Usage:
#   .\measure.ps1                    # 10 s against localhost:8586
#   .\measure.ps1 -Seconds 30 -Target 192.168.1.84 -Tag "driver-door-open"
#
# Requires: rta.exe running on the target host (any machine on the LAN).
param(
    [int]$Seconds = 10,
    [string]$Target = "localhost",
    [int]$Port = 8586,
    [string]$Tag = ""
)

$url = "http://${Target}:${Port}/rta.json"
$sw = [Diagnostics.Stopwatch]::StartNew()
$samples = New-Object System.Collections.Generic.List[object]
$fail = 0

Write-Host "measuring $Seconds s from $url ..."
while ($sw.Elapsed.TotalSeconds -lt $Seconds) {
    try {
        $j = (Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 3).Content | ConvertFrom-Json
        if ($j.ok -and $j.bands) { $samples.Add($j) } else { $fail++ }
    } catch { $fail++ }
    Start-Sleep -Milliseconds 250
}

if ($samples.Count -eq 0) {
    Write-Host "no samples collected ($fail failures) - is rta.exe running on ${Target}:$Port?"
    exit 1
}

# average each band across samples (bands are identical arrays: 241 entries)
$n = $samples.Count
$avg = for ($k = 0; $k -lt $samples[0].bands.Count; $k++) {
    $sum = 0.0
    foreach ($s in $samples) { $sum += [double]$s.bands[$k].db }
    [pscustomobject]@{ f = [double]$samples[0].bands[$k].f; db = $sum / $n }
}

# crude tilt: least-squares slope in dB/octave over the averaged curve
$x = @(); $y = @()
foreach ($b in $avg) {
    if ($b.f -ge 40 -and $b.f -le 16000 -and $b.db -gt -110) {
        $x += [Math]::Log($b.f, 2); $y += $b.db
    }
}
$slope = 0.0
if ($x.Count -gt 2) {
    $mx = ($x | Measure-Object -Average).Average
    $my = ($y | Measure-Object -Average).Average
    $num = 0.0; $den = 0.0
    for ($i = 0; $i -lt $x.Count; $i++) { $num += ($x[$i] - $mx) * ($y[$i] - $my); $den += ($x[$i] - $mx) * ($x[$i] - $mx) }
    if ($den -gt 0) { $slope = $num / $den }
}

$meta = $samples[$n - 1]
Write-Host ""
Write-Host "samples=$n  fails=$fail  device=$($meta.device)  fs=$($meta.fs)"
Write-Host ("tilt: {0:+0.00;-0.00} dB/oct  (negative = bass-heavy, positive = treble-heavy)" -f $slope)
Write-Host ""
Write-Host "  Hz      dB    "
foreach ($b in $avg) {
    if ($b.f -ge 20 -and $b.f -le 20000) {
        $bar = "#" * [Math]::Max(0, [Math]::Min(60, [int](($b.db + 120) / 2)))
        Write-Host ("{0,6}  {1,7:N1}  {2}" -f $b.f, $b.db, $bar)
    }
}

# save evidence: averaged bands + raw metadata
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$tagPart = if ($Tag) { "-$($Tag -replace '[^\w-]','_')" } else { "" }
$outFile = Join-Path $PSScriptRoot "rta-measure-$stamp$tagPart.json"
[pscustomobject]@{
    ok = $true; ts = $meta.ts; seconds = $Seconds; samples = $n; fails = $fail
    device = $meta.device; fs = $meta.fs; fft = $meta.fft
    tiltDbPerOct = [Math]::Round($slope, 3)
    bands = $avg
} | ConvertTo-Json -Depth 5 | Set-Content -Path $outFile -Encoding UTF8
Write-Host ""
Write-Host "saved: $outFile"
