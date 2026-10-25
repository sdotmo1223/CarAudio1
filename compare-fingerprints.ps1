# compare-fingerprints.ps1 - prove a flatten/restore cycled the playing memory back exactly.
#   compare-fingerprints.ps1 -Before <a.json> -After <b.json>
# Compares every band (fc, gain, q), every byPass bit, every crossover scalar, and the master state.
# Read-only: two JSON files on disk, nothing talks to the device.
param(
  [Parameter(Mandatory = $true)][string]$Before,
  [Parameter(Mandatory = $true)][string]$After,
  [double]$GainTol = 0.001,
  [double]$QTol = 0.001
)
$ErrorActionPreference = 'Stop'
$a = Get-Content -LiteralPath $Before -Raw | ConvertFrom-Json
$b = Get-Content -LiteralPath $After  -Raw | ConvertFrom-Json

$fail = @()
$cellCount = 0
for ($i = 0; $i -lt $a.cells.Count; $i++) {
  $ca = $a.cells[$i]; $cb = $b.cells[$i]
  if ($ca.path -ne $cb.path) { $fail += ("cell " + $i + ": path " + $ca.path + " vs " + $cb.path); continue }
  if ($ca.byPass -ne $cb.byPass) { $fail += ("path " + $ca.path + ": byPass " + $ca.byPass + " -> " + $cb.byPass) }
  if ($ca.bands.Count -ne $cb.bands.Count) { $fail += ("path " + $ca.path + ": band count " + $ca.bands.Count + " vs " + $cb.bands.Count); continue }
  for ($k = 0; $k -lt $ca.bands.Count; $k++) {
    $ba = $ca.bands[$k]; $bb = $cb.bands[$k]
    if ([int]$ba.fc -ne [int]$bb.fc) { $fail += ("path " + $ca.path + " band " + $k + ": fc " + $ba.fc + " -> " + $bb.fc) }
    if ([math]::Abs([double]$ba.gain - [double]$bb.gain) -gt $GainTol) {
      $fail += ("path " + $ca.path + " band " + $k + ": gain " + $ba.gain + " -> " + $bb.gain)
    }
    if ([math]::Abs([double]$ba.q - [double]$bb.q) -gt $QTol) {
      $fail += ("path " + $ca.path + " band " + $k + ": q " + $ba.q + " -> " + $bb.q)
    }
    $cellCount++
  }
  if ($ca.passband -or $cb.passband) {
    if (-not $ca.passband -or -not $cb.passband) { $fail += ("path " + $ca.path + ": passband present on one side only") }
    else {
      foreach ($f in 'lowFc','highFc','lowSlope','highSlope','lowFilterType','highFilterType') {
        if ($ca.passband.$f -ne $cb.passband.$f) {
          $fail += ("path " + $ca.path + " passband." + $f + ": " + $ca.passband.$f + " -> " + $cb.passband.$f)
        }
      }
    }
  }
}
if ($a.status.mem -ne $b.status.mem) { $fail += ("mem " + $a.status.mem + " -> " + $b.status.mem) }
if ([math]::Abs([double]$a.status.mainDb - [double]$b.status.mainDb) -gt 0.01) {
  $fail += ("mainDb " + $a.status.mainDb + " -> " + $b.status.mainDb)
}
if ($a.status.mute -ne $b.status.mute) { $fail += ("mute " + $a.status.mute + " -> " + $b.status.mute) }
$ga = ($a.status.groupDb | ForEach-Object { [double]$_ }) -join '/'
$gb = ($b.status.groupDb | ForEach-Object { [double]$_ }) -join '/'
if ($ga -ne $gb) { $fail += ("groups " + $ga + " -> " + $gb) }
if ($a.status.source -ne $b.status.source) { $fail += ("source " + $a.status.source + " -> " + $b.status.source) }

Write-Output ("before : " + $Before + "  (" + $a.label + ")")
Write-Output ("after  : " + $After  + "  (" + $b.label + ")")
Write-Output ("bands compared: " + $cellCount + " across " + $a.cells.Count + " paths; crossovers compared per path")
if ($fail.Count) {
  Write-Output ("VERDICT: FAIL - " + $fail.Count + " difference(s)")
  $fail | Select-Object -First 20 | ForEach-Object { Write-Output ("  " + $_) }
  exit 1
}
Write-Output "VERDICT: PASS - every band, byPass bit, crossover scalar and the master state are identical"
exit 0
