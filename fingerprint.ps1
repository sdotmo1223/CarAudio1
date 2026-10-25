# fingerprint.ps1 - read-only record of the playing memory's EQ + crossovers.
#   fingerprint.ps1 -Out <path.json>
# Used to prove a sweep-mode flatten and its restore against the device itself, not against the
# page's own verification. Nothing here writes.
param(
  [Parameter(Mandatory = $true)][string]$Out,
  [string]$Base = 'http://127.0.0.1:8585',
  [string]$Label = ''
)
$ErrorActionPreference = 'Stop'

$st = Invoke-RestMethod -Uri "$Base/api/status" -Method Post -ContentType 'application/json' -Body '{}'
$mem = [int]$st.mem
$cells = @()
foreach ($p in 0..16) {
  $r = Invoke-RestMethod -Uri "$Base/api/eqread" -Method Post -ContentType 'application/json' `
        -Body (@{ mem = $mem; path = $p } | ConvertTo-Json)
  $bands = @()
  foreach ($bd in $r.bands) { $bands += [pscustomobject]@{ fc = [int]$bd.fc; gain = [double]$bd.gain; q = [double]$bd.q } }
  $pb = $r.passband
  $cells += [pscustomobject]@{
    path = $p; ok = [bool]$r.ok; byPass = [bool]$r.byPass
    worst = [double](($bands | ForEach-Object { [math]::Abs($_.gain) } | Measure-Object -Maximum).Maximum)
    bands = $bands
    passband = if ($pb) { [pscustomobject]@{ lowFc = [int]$pb.lowFc; highFc = [int]$pb.highFc
                                             lowSlope = [int]$pb.lowSlope; highSlope = [int]$pb.highSlope
                                             lowFilterType = [int]$pb.lowFilterType; highFilterType = [int]$pb.highFilterType } } else { $null }
  }
}
$doc = [pscustomobject]@{
  label = $Label; at = (Get-Date).ToString('o')
  status = [pscustomobject]@{ mem = $mem; mainDb = [double]$st.mainDb; mute = [bool]$st.mute
                              groupDb = @($st.groupDb); source = [int]$st.source }
  cells = $cells
}
$doc | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $Out -Encoding UTF8
Write-Output ("fingerprint -> " + $Out + "  mem=" + $mem + " mainDb=" + $st.mainDb + " mute=" + $st.mute)
$cells | ForEach-Object { "  path {0,2}  byPass={1,-5}  worst |gain| = {2,6:N2} dB" -f $_.path, $_.byPass, $_.worst }
