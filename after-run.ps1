# after-run.ps1 - grade the run the moment it finishes. No arguments needed.
#
# Reads /api/rta-meas (the page pushes every capture there automatically), takes the newest
# captures, plots them, and reports what each one actually proves.
#
# Two versions of this got the verdict wrong, and both are worth remembering:
#   1. headroom against the quietest 12 bins -> for a midrange that is the mic's digital silence
#      near 20 kHz, so a tweeter was reported as "50 dB of headroom, usable 20-160 Hz".
#   2. headroom against the median OUTSIDE the driver's rated band -> correct only while the
#      crossovers band-limit the driver. With the low-pass filters opened to 20 kHz the 6.5"
#      plays 60-400 Hz, so "outside its band" is its own output, and it was reported "buried in
#      the room" at -31 dBFS with a 4 dB margin. It was not buried; the yardstick was.
# So this now reports what the capture itself carries, which needs no band assumption:
#   peak      the loudest point of the curve
#   burst     sig.burstDb - the analyser's own dynamic range for that take (best signal to floor)
#   gate      sig.ok - whether the analyser accepted the take at all
#   band      sig.from-sig.to - the band where the analyser saw the signal rise and fall
# A take is trustworthy when the gate accepted it; it is *strong* when burst is large.
param(
    [int]$Last = 8,
    [int]$Plot = 0,
    [string]$PlotFile = '.\var\plots\run-curves.png'
)

$d = Invoke-RestMethod 'http://127.0.0.1:8585/api/rta-meas' -TimeoutSec 15
$items = @(@($d.meas) | Select-Object -Last $Last)
if ($items.Count -eq 0) { throw 'no measurements on the bridge yet' }

Write-Output ''
Write-Output ("newest {0} capture(s), from {1} stored on the bridge" -f $items.Count, $d.count)
Write-Output ''
Write-Output '  time      channel         peak   burst  gate   band the analyser saw'
Write-Output '  -------   -------------   -----  -----  -----  ------------------------'

$rows = @()
foreach ($m in $items) {
    $db = @($m.db | ForEach-Object { [double]$_ })
    if ($db.Count -lt 10) { continue }
    $peak = ($db | Measure-Object -Maximum).Maximum
    $burst = if ($null -ne $m.sig.burstDb) { [double]$m.sig.burstDb } else { [double]::NaN }
    $gate = if ($m.sig.ok) { 'ok' } else { 'REJECT' }
    $band = if ($null -ne $m.sig.from) { ('{0}-{1} Hz' -f [int]$m.sig.from, [int]$m.sig.to) } else { '(none)' }
    $tm = [DateTimeOffset]::FromUnixTimeMilliseconds([long]$m.t).LocalDateTime.ToString('HH:mm:ss')

    Write-Output ('  {0}   {1,-13}  {2,6:N1}  {3,5:N1}  {4,-6} {5}' -f $tm, $m.name, $peak, $burst, $gate, $band)
    $rows += [pscustomobject]@{ name = $m.name; peak = $peak; burst = $burst; ok = [bool]$m.sig.ok }
}

Write-Output ''
$rejected = @($rows | Where-Object { -not $_.ok })
$weak     = @($rows | Where-Object { $_.ok -and $_.burst -lt 40 })
$loudest  = ($rows | Sort-Object peak -Descending | Select-Object -First 1)
$quietest = ($rows | Sort-Object peak | Select-Object -First 1)

if ($rejected.Count) {
    Write-Output ('  rejected by the gate: ' + (($rejected | ForEach-Object { $_.name }) -join ', ') + ' - those are not measurements.')
}
if ($weak.Count) {
    Write-Output ('  accepted but thin (<40 dB of burst): ' + (($weak | ForEach-Object { $_.name }) -join ', '))
}
if (-not $rejected.Count -and -not $weak.Count) {
    Write-Output '  every channel was accepted by the gate with a large burst - this is a real run.'
}
Write-Output ('  spread: {0} at {1:N1} dBFS to {2} at {3:N1} dBFS = {4:N1} dB of level difference.' -f
    $loudest.name, $loudest.peak, $quietest.name, $quietest.peak, ($loudest.peak - $quietest.peak))

if ($Plot) {
    try {
        & powershell -NoProfile -ExecutionPolicy Bypass -File .\var\plot-rta-meas.ps1 -Last $Last -Out $PlotFile | Out-Null
        Write-Output ('  plot: ' + (Resolve-Path $PlotFile).Path)
    } catch {
        Write-Output ('  plot failed: ' + $_.Exception.Message)
    }
}
