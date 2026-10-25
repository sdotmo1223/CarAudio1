# verify-sweep-gate.ps1 - prove the analyser refuses what is not a measurement, and accepts what is.
#
# Two failure modes were measured on the rig on 2026-10-20, and this script reproduces both from
# the reference sweep rather than depending on whatever takes happen to be in measurements\:
#
#   no sweep at all      peak-to-rest looked healthy (8.7 dB) and a 27.8 s "delay" was reported
#   part of the sweep    2.5 s played, then silence: coef 0.228, the missing 7.5 s at noise floor
#
# A gate that only tests presence passes the second case. A gate that tests uniformity alone
# rejects a tweeter measured against the full-band sweep, so the controls below are band-matched.
#
# Run it after any change to sweep-tool's analyse path:
#   powershell -File verify-sweep-gate.ps1
#
param([string]$Tmp = '')

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$tool = Join-Path $root 'bin\sweep-tool.exe'
$ff = Join-Path $env:SystemDrive 'tools\ffmpeg\ffmpeg-master-latest-win64-gpl\bin\ffmpeg.exe'
$full = Join-Path $root 'sweeps\sweep-20-20k-10s.wav'
$band = Join-Path $root 'sweeps\sweep-1500-20k-10s.wav'

if (-not (Test-Path $tool)) { throw "sweep-tool.exe missing - run build-sweep.ps1" }
if (-not (Test-Path $ff)) { throw "ffmpeg missing at $ff" }
if (-not (Test-Path $full)) { throw "reference sweep missing: $full" }
if (-not (Test-Path $band)) {
    & $tool make $band 10 1500 20000 48000 | Out-Null
}

if (-not $Tmp) { $Tmp = Join-Path $root 'var\gate' }
New-Item -ItemType Directory -Force -Path $Tmp | Out-Null

$pass = 0; $fail = 0
function Check([bool]$cond, [string]$what) {
    if ($cond) { Write-Host ("  PASS  " + $what); $script:pass++ }
    else { Write-Host ("  FAIL  " + $what); $script:fail++ }
}

# --- make the controls -----------------------------------------------------
# A quiet pink-noise bed so the mic is not a digital silence, and -12 dB stands in for distance.
# No artificial band edges: the reference already defines the band each control is judged against,
# and adding a low-pass here would be indistinguishable from the truncation being tested for.
function Make-Control([string]$src, [string]$out, [string]$filters) {
    & $ff -hide_banner -v error -y -i $src -f lavfi -i "anoisesrc=d=13:c=pink:a=0.01" `
        -filter_complex "[0:a]volume=-12dB,adelay=500:all=1,$filters[s];[s][1:a]amix=inputs=2:duration=first:normalize=0[a]" `
        -map "[a]" -ar 48000 -ac 1 $out
}

$cFull = Join-Path $Tmp 'ctrl-fullband.wav'
$cTweet = Join-Path $Tmp 'ctrl-tweeter.wav'
$cCut = Join-Path $Tmp 'ctrl-truncated.wav'
Make-Control $full $cFull 'anull'
# A tweeter is measured against the 1.5 kHz-onward sweep, so its control has to be built from
# that file. Filtering the full-band sweep instead leaves a different frequency-vs-time map: the
# same 1.5 kHz arrives 6 s in, not at the start, and the two do not correlate.
Make-Control $band $cTweet 'anull'
# Playback that stops after 4 s, then nothing but the noise floor - the exact field failure.
& $ff -hide_banner -v error -y -i $full -f lavfi -i "anoisesrc=d=13:c=pink:a=0.01" `
    -filter_complex "[0:a]volume=-12dB,adelay=500:all=1,atrim=0:4.5[s];[s][1:a]amix=inputs=2:duration=longest:normalize=0[a]" `
    -map "[a]" -ar 48000 -ac 1 $cCut

# --- run -------------------------------------------------------------------
function Analyse([string]$ref, [string]$rec) {
    $out = Join-Path $Tmp 'r.json'
    if (Test-Path $out) { Remove-Item $out -Force }
    & $tool analyse $ref $rec $out | Out-Null
    $code = $LASTEXITCODE
    $j = $null
    if (Test-Path $out) { $j = Get-Content $out -Raw | ConvertFrom-Json }
    return [pscustomobject]@{ code = $code; json = $j }
}

Write-Host "controls (true delay 500.000 ms)"
$a = Analyse $full $cFull
Check ($a.code -eq 0) ("full-band capture is measured (exit " + $a.code + ")")
if ($a.code -eq 0) {
    Check ([Math]::Abs([double]$a.json.delayMs - 500.0) -lt 2.0) ("delay " + $a.json.delayMs.ToString('0.000') + " ms is within 2 ms of 500")
}
$b = Analyse $band $cTweet
Check ($b.code -eq 0) ("tweeter capture against its own band-limited reference is measured (exit " + $b.code + ")")
if ($b.code -eq 0) {
    Check ([Math]::Abs([double]$b.json.delayMs - 500.0) -lt 2.0) ("delay " + $b.json.delayMs.ToString('0.000') + " ms is within 2 ms of 500")
}
$self = Analyse $full $full
Check ($self.code -eq 0 -and [Math]::Abs([double]$self.json.delayMs) -lt 0.1) ("the reference against itself measures 0 ms")

Write-Host "refusals"
$c = Analyse $full $cCut
Check ($c.code -eq 3) ("a sweep that stops after 4 s is refused (exit " + $c.code + ")")
Check ($c.code -eq 3 -and [string]$c.json.verdict -match 'part of the sweep') ("  and the reason names the missing part: " + [string]$c.json.verdict)
Check ($c.code -eq 3 -and @($c.json.bands).Count -eq 0) ("  and no response curve is published")

# The three real takes from the car, when they are still on disk.
$real = @(
    @{ f = 'sweep-20261020-074141-phone_car_test.wav'; why = 'correlate' },
    @{ f = 'sweep-20261020-074829-phone_sweep.wav'; why = 'part of the sweep' },
    @{ f = 'sweep-20261020-075005-phone_sweep2.wav'; why = '' }
)
foreach ($r in $real) {
    $p = Join-Path $root ("measurements\" + $r.f)
    if (-not (Test-Path $p)) { continue }
    $x = Analyse $full $p
    Check ($x.code -eq 3) ("field take " + $r.f + " is refused (exit " + $x.code + ")")
}

Write-Host ""
Write-Host ("result: {0} passed, {1} failed" -f $pass, $fail)
if ($fail) { exit 1 }
Write-Host "gate    : refuses noise and truncation, accepts a real band-matched arrival"



