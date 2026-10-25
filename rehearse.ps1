# rehearse.ps1 - run a whole measurement cycle through the analysis with the answers known.
#
# The first real cycle should not also be the first time the pipeline runs. This builds a
# two-channel fixture with sweep-tool's own synth: the left channel is the loopback (eight
# sweeps at the page's cadence), the right channel is the mic (the same sweeps, delayed by a
# known number of milliseconds and scaled by a known gain). Then it runs the real
# measure-sweep.ps1 over the file and compares what came back.
#
#   .\rehearse.ps1                  # eight channels, 14 s cadence, noise at -70 dB
#   .\rehearse.ps1 -NoNegative      # skip the silent-channel control
#
# Exit 0 = every delay and level recovered within tolerance, and the silent control failed
# the way it must.

param(
    [string]$OutDir = "",
    [double]$Cadence = 14.0,          # 12 s sweep window + 2 s settle, as the page runs it
    [double]$FirstAt = 3.0,
    [double]$NoiseDb = -70,
    [switch]$NoNegative,
    [switch]$Keep                    # keep the 43 MB fixture WAVs (they are reproducible)
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$tool = Join-Path $root 'bin\sweep-tool.exe'
$sweep = Join-Path $root 'sweeps\sweep-20-20k-10s.wav'
if (-not $OutDir) { $OutDir = Join-Path $root 'evidence\rehearsal' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
if (-not (Test-Path $tool)) { throw "sweep-tool.exe missing - run build-sweep.ps1" }

# The car map: ch 1-4 tweeters, 5-6 the 3.5"s, 7 the 8", 8 the 6.5". Gains are the tune's.
$plan = @(
    @{ ch = 1; delay = 3.2;  gain = 0 },
    @{ ch = 2; delay = 3.8;  gain = -4 },
    @{ ch = 3; delay = 4.1;  gain = -7 },
    @{ ch = 4; delay = 4.6;  gain = -7 },
    @{ ch = 5; delay = 5.0;  gain = -3 },
    @{ ch = 6; delay = 5.4;  gain = -3 },
    @{ ch = 7; delay = 3.9;  gain = -14 },
    @{ ch = 8; delay = 4.4;  gain = -5 }
)
$i = 0
foreach ($b in $plan) { $b.at = $FirstAt + $i * $Cadence; $i++ }
$seconds = $plan[-1].at + 10 + 1.0

function New-Fixture([string]$wav, [string]$planFile, [double]$silentCh) {
    $lines = @([string]::Format([Globalization.CultureInfo]::InvariantCulture, "seconds={0:0.##}", $seconds), [string]::Format([Globalization.CultureInfo]::InvariantCulture, "noiseDb={0}", $NoiseDb))
    foreach ($b in $plan) {
        $g = if ($silentCh -and $b.ch -eq $silentCh) { -200 } else { $b.gain }
        $lines += [string]::Format([Globalization.CultureInfo]::InvariantCulture,
            "{0},{1},{2}", $b.at, $b.delay, $g)
    }
    $lines | Set-Content -Path $planFile -Encoding ASCII
    & $tool synth $sweep $wav $planFile | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "synth failed for $wav" }
}

function Analyse([string]$wav, [string]$tag) {
    $dir = Join-Path $OutDir $tag
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $out = & (Join-Path $root 'measure-sweep.ps1') -From $wav -Name $tag -OutDir $dir 2>&1
    $code = $LASTEXITCODE
    $res = Get-ChildItem $dir -Filter "sweep-*-$tag.json" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $sum = Get-ChildItem $dir -Filter "sweep-*-$tag-summary.json" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $res) { throw "no analysis JSON for $tag" }
    return @{
        code = $code
        path = $res.FullName
        json = (Get-Content -Raw $res.FullName | ConvertFrom-Json)
        summary = $(if ($sum) { Get-Content -Raw $sum.FullName | ConvertFrom-Json } else { $null })
        text = ($out -join "`n")
    }
}

# ---------------------------------------------------------------- the good cycle
$goodWav = Join-Path $OutDir 'rehearsal-cycle.wav'
$goodPlan = Join-Path $OutDir 'rehearsal-cycle.txt'
New-Fixture $goodWav $goodPlan 0
$a = Analyse $goodWav 'rehearsal'
$bursts = @($a.json.bursts)

$pass = 0; $fail = 0
function Check([bool]$cond, [string]$what) {
    if ($cond) { Write-Host "  PASS  $what" -ForegroundColor Green; $script:pass++ }
    else { Write-Host "  FAIL  $what" -ForegroundColor Red; $script:fail++ }
}

Write-Host ""
Write-Host ("fixture : {0} s, {1} bursts, noise {2} dB, cadence {3} s" -f
    $seconds.ToString('0.0'), $plan.Count, $NoiseDb, $Cadence)
Write-Host ("analyse : exit {0}, {1} burst(s) found" -f $a.code, $bursts.Count)
Write-Host ""
Write-Host ("  {0,-4} {1,10} {2,10} {3,9} {4,9} {5,8}" -f 'ch', 'delay in', 'delay out', 'err ms', 'gain in', 'level out')

$worstDelay = 0.0
$worstLevel = 0.0
for ($k = 0; $k -lt $plan.Count; $k++) {
    $want = $plan[$k]
    $got = if ($k -lt $bursts.Count) { $bursts[$k] } else { $null }
    if (-not $got) {
        Write-Host ("  {0,-4} {1,10:N2} {2,10} {3,9} {4,9} {5,8}" -f $want.ch, $want.delay, '-', 'MISSING', $want.gain, '-')
        $fail++
        continue
    }
    $err = [double]$got.delayMs - $want.delay
    $band = $null
    foreach ($b in @($got.bands)) { if ($b.f -ge 900 -and $b.f -le 1100) { $band = $b; break } }
    $lvl = if ($band) { [double]$band.db } else { $null }
    $lvlErr = if ($null -ne $lvl) { $lvl - $want.gain } else { $null }
    if ($null -ne $err) { $worstDelay = [Math]::Max($worstDelay, [Math]::Abs($err)) }
    if ($null -ne $lvlErr) { $worstLevel = [Math]::Max($worstLevel, [Math]::Abs($lvlErr)) }
    Write-Host ("  {0,-4} {1,10:N2} {2,10:N2} {3,9:+0.00;-0.00;0.00} {4,9} {5,8}" -f
        $want.ch, $want.delay, [double]$got.delayMs, $err, $want.gain,
        $(if ($null -ne $lvl) { "{0:N1}" -f $lvl } else { '-' }))
    if (-not $got.ok) { $fail++ }
    if ([Math]::Abs($err) -gt 0.05) { $fail++ }
    if ($null -ne $lvlErr -and [Math]::Abs($lvlErr) -gt 1.0) { $fail++ }
}

Write-Host ""
Check ($bursts.Count -eq $plan.Count) ("all {0} bursts found (got {1})" -f $plan.Count, $bursts.Count)
Check ($a.code -eq 0) "analysis exits 0"
Check ($worstDelay -le 0.05) ("worst delay error {0:N3} ms <= 0.05" -f $worstDelay)
Check ($worstLevel -le 1.0) ("worst level error {0:N2} dB <= 1.0" -f $worstLevel)

# Relative delays are what alignment actually uses; print them so a regression is obvious.
$delays = @($bursts | Where-Object { $_.ok } | ForEach-Object { [double]$_.delayMs })
if ($delays.Count -gt 1) {
    $min = ($delays | Measure-Object -Minimum).Minimum
    Write-Host ("relative: " + (($delays | ForEach-Object { "{0:+0.00;-0.00;0.00}" -f ($_ - $min) }) -join '  '))
}

# ---------------------------------------------------------------- the silent control
# A driver that is not wired must come back not-ok. If the tool ever fabricated a delay for it,
# the run would align a dead channel to a number that was never measured.
if (-not $NoNegative) {
    Write-Host ""
    $badWav = Join-Path $OutDir 'rehearsal-silent.wav'
    $badPlan = Join-Path $OutDir 'rehearsal-silent.txt'
    New-Fixture $badWav $badPlan 3
    $n = Analyse $badWav 'rehearsal-silent'
    $nb = @($n.json.bursts)
    $okCount = @($nb | Where-Object { $_.ok }).Count
    $silent = if ($nb.Count -ge 3) { $nb[2] } else { $null }
    $text = $n.text
    Write-Host ("negative: {0} burst(s) found, {1} measured, exit {2}" -f $nb.Count, $okCount, $n.code)
    Check ($okCount -eq ($plan.Count - 1)) ("exactly one channel unmeasured (got {0} measured)" -f $okCount)
    Check ($silent -and -not $silent.ok) "the silent channel is marked not-ok"
    Check ($nb.Count -eq $plan.Count) "all loopback bursts were still found with one silent channel"
    Check ($n.code -eq 0) "a cycle with one dead driver still exits 0 (the other seven are usable)"
}

if (-not $Keep) {
    # 112 s of stereo 48 kHz is ~43 MB each; the plan files and JSONs are the evidence.
    Remove-Item $goodWav -ErrorAction SilentlyContinue
    if (-not $NoNegative) { Remove-Item $badWav -ErrorAction SilentlyContinue }
}

Write-Host ""
Write-Host ("rehearsal: {0} passed, {1} failed" -f $pass, $fail) -ForegroundColor $(if ($fail) { 'Red' } else { 'Green' })
Write-Host "plan     : $goodPlan  (the WAV fixtures are rebuilt each run)"
Write-Host "result   : $($a.path)"
exit $(if ($fail -gt 0) { 1 } else { 0 })
