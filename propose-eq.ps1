# propose-eq.ps1 - turn a measured curve into a 31-band EQ proposal. Writes nothing to the DSP.
#
# The DSP's EQ is 31 fixed bands at Q=1, so "designing" is choosing a gain per band. This reads
# a curve (a raw sweep take, or one of the app's saved RTA measurements), works out the gains
# that would flatten it to a target, and prints the table plus the curve those gains predict.
#
#   .\propose-eq.ps1 -Bursts evidence\rehearsal\rehearsal\sweep-....json -Burst 0
#   .\propose-eq.ps1 -Name "driver tweeter"              # newest saved RTA measurement
#   .\propose-eq.ps1 -Name "rear 6.5" -TiltDbPerOct -0.5 # a house tilt
#   .\propose-eq.ps1 -SelfTest                           # round-trip with a known answer
#
# Rules that keep it honest:
#   - the curve is normalised to the median of its usable band, so a proposal never tries to
#     raise the whole channel by the microphone's absolute level
#   - a frequency more than -FloorBelow dB under the curve's top is excluded: boosting a null
#     or a driver's rolloff buys excursion, not sound
#   - boosts are capped at +BoostCap, cuts at -CutCap
#   - the predicted curve is a model (RBJ peaking, Q=1, 48 kHz), not a device readback: it is a
#     plan to review, and the RTA is what confirms it
#
# Exit 0 = proposal written; 1 = no usable curve.

param(
    [string]$Bursts = "",             # sweep-tool bursts/analyse JSON
    [int]$Burst = -1,                 # which burst; -1 = first ok one
    [string]$Rta = "",                # rta-measurements.json (default: the one in this folder)
    [string]$Name = "",               # saved measurement name to match (newest)
    [string]$TargetFile = "",         # 31-point target (array, or {db:[...]})
    [double]$TiltDbPerOct = 0,        # house tilt, dB per octave around 1 kHz
    [double]$BoostCap = 6,
    [double]$CutCap = 12,
    [double]$FloorBelow = 15,
    [double]$Smooth = 1,              # neighbouring grid points averaged either side (0 = none)
    [string]$Out = "",
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$FS = 48000.0
$BAND_FC = @(20, 25, 31, 40, 50, 63, 80, 100, 125, 160, 200, 250, 315, 400, 500, 630, 800,
             1000, 1250, 1600, 2000, 2500, 3150, 4000, 5000, 6300, 8000, 10000, 12500, 16000, 20000)

# Magnitude of one peaking band at f, in dB. RBJ cookbook, Q=1: the same six numbers the DSP
# carries per band (fc, gain, q) interpreted the standard way.
function PeakDb([double]$f, [double]$fc, [double]$gainDb, [double]$q = 1.0) {
    $A = [Math]::Pow(10.0, $gainDb / 40.0)
    $w0 = 2.0 * [Math]::PI * $fc / $FS
    $alpha = [Math]::Sin($w0) / (2.0 * $q)
    $b0 = 1.0 + $alpha * $A; $b1 = -2.0 * [Math]::Cos($w0); $b2 = 1.0 - $alpha * $A
    $a0 = 1.0 + $alpha / $A; $a1 = -2.0 * [Math]::Cos($w0); $a2 = 1.0 - $alpha / $A
    $w = 2.0 * [Math]::PI * $f / $FS
    $z1 = [System.Numerics.Complex]::FromPolarCoordinates(1.0, -$w)
    $z2 = $z1 * $z1
    $num = $b0 + $b1 * $z1 + $b2 * $z2
    $den = $a0 + $a1 * $z1 + $a2 * $z2
    $h = $num / $den
    return 20.0 * [Math]::Log10($h.Magnitude)
}

# Gaussian elimination with partial pivoting. n is 31, so this is plenty.
function Solve([double[,]]$A, [double[]]$b) {
    $n = $b.Length
    for ($col = 0; $col -lt $n; $col++) {
        $piv = $col
        for ($r = $col + 1; $r -lt $n; $r++) {
            $vr = $A[$r, $col]
            $vp = $A[$piv, $col]
            $ar = if ($vr -lt 0) { -$vr } else { $vr }
            $ap = if ($vp -lt 0) { -$vp } else { $vp }
            if ($ar -gt $ap) { $piv = $r }
        }
        $v = $A[$piv, $col]
        $av = if ($v -lt 0) { -$v } else { $v }
        if ($av -lt 1e-12) { continue }
        if ($piv -ne $col) {
            for ($c = $col; $c -lt $n; $c++) { $t = $A[$col, $c]; $A[$col, $c] = $A[$piv, $c]; $A[$piv, $c] = $t }
            $t = $b[$col]; $b[$col] = $b[$piv]; $b[$piv] = $t
        }
        for ($r = $col + 1; $r -lt $n; $r++) {
            $f = $A[$r, $col] / $A[$col, $col]
            if ($f -eq 0) { continue }
            for ($c = $col; $c -lt $n; $c++) { $A[$r, $c] -= $f * $A[$col, $c] }
            $b[$r] -= $f * $b[$col]
        }
    }
    $x = New-Object double[] $n
    for ($r = $n - 1; $r -ge 0; $r--) {
        $s = $b[$r]
        for ($c = $r + 1; $c -lt $n; $c++) { $s -= $A[$r, $c] * $x[$c] }
        $d = $A[$r, $r]
        $ad = if ($d -lt 0) { -$d } else { $d }
        $x[$r] = if ($ad -lt 1e-12) { 0 } else { $s / $d }
    }
    return ,$x
}

# Median without LINQ-fu: copy, sort, middle.
function Median([double[]]$v) {
    if ($v.Length -eq 0) { return 0.0 }
    $s = @($v | Sort-Object)
    $m = [int][Math]::Floor($s.Count / 2)
    if ($s.Count % 2 -eq 1) { return [double]$s[$m] }
    return ([double]$s[$m - 1] + [double]$s[$m]) / 2.0
}

# The core: measurement (f[], db[]) -> 31 gains, predicted curve, usable mask.
function Fit([double[]]$freqs, [double[]]$meas, [double[]]$target) {
    $n = $freqs.Length
    $k = $BAND_FC.Length

    $usable = New-Object bool[] $n
    $top = ($meas | Measure-Object -Maximum).Maximum
    for ($i = 0; $i -lt $n; $i++) { $usable[$i] = ($meas[$i] -ge ($top - $FloorBelow)) }

    # normalise to the median of the usable band: the absolute level is the mic's problem
    $keep = @()
    for ($i = 0; $i -lt $n; $i++) { if ($usable[$i]) { $keep += $meas[$i] } }
    if ($keep.Count -lt 8) { throw "only $($keep.Count) usable point(s): nothing to fit" }
    $base = Median ([double[]]$keep)

    # basis: response at each measurement frequency for +1 dB on each band
    $basis = New-Object 'double[][]' $n
    for ($i = 0; $i -lt $n; $i++) {
        $row = New-Object double[] $k
        for ($j = 0; $j -lt $k; $j++) { $row[$j] = PeakDb $freqs[$i] $BAND_FC[$j] 1.0 1.0 }
        $basis[$i] = $row
    }

    # normal equations with a small ridge, weighted by the usable mask
    $A = New-Object 'double[,]' $k, $k
    $b = New-Object double[] $k
    for ($i = 0; $i -lt $n; $i++) {
        if (-not $usable[$i]) { continue }
        $c = $target[$i] - ($meas[$i] - $base)
        $row = $basis[$i]
        for ($j = 0; $j -lt $k; $j++) {
            $b[$j] += $row[$j] * $c
            for ($m = 0; $m -lt $k; $m++) { $A[$j, $m] += $row[$j] * $row[$m] }
        }
    }
    for ($j = 0; $j -lt $k; $j++) { $A[$j, $j] += 0.10 }

    $g = Solve $A $b
    for ($j = 0; $j -lt $k; $j++) {
        # A band whose own centre is under the floor is a rolled-off driver, not a room mode:
        # cutting there is fine, boosting there is excursion bought with a number.
        $near = 0; $bd = 1e9
        for ($q = 0; $q -lt $n; $q++) {
            $d = [Math]::Abs([Math]::Log($freqs[$q] / $BAND_FC[$j]))
            if ($d -lt $bd) { $bd = $d; $near = $q }
        }
        if ($meas[$near] -lt ($top - $FloorBelow) -and $g[$j] -gt 0) { $g[$j] = 0.0 }
        if ($g[$j] -gt $BoostCap) { $g[$j] = $BoostCap }
        if ($g[$j] -lt (-1.0 * $CutCap)) { $g[$j] = (-1.0 * $CutCap) }
        if ([Math]::Abs($g[$j]) -lt 0.15) { $g[$j] = 0.0 }   # no dots at a tenth of a dB
    }

    $pred = New-Object double[] $n
    for ($i = 0; $i -lt $n; $i++) {
        $s = $meas[$i] - $base
        for ($j = 0; $j -lt $k; $j++) { $s += $basis[$i][$j] * $g[$j] }
        $pred[$i] = $s
    }
    return @{ gains = $g; pred = $pred; usable = $usable; base = $base }
}

# ---------------------------------------------------------------- self-test
# Round trip: build a curve from known gains, fit it, and check both the residual and the two
# guardrails. A solver that has never been seen to fail has verified nothing.
if ($SelfTest) {
    $freqs = New-Object double[] 61
    for ($i = 0; $i -lt 61; $i++) { $freqs[$i] = 20.0 * [Math]::Pow(2.0, $i / 6.0) }
    $truth = New-Object double[] 31
    foreach ($p in @(@(10, 4), @(13, -3), @(15, 2), @(17, -5), @(18, 3), @(20, -4), @(21, 5), @(23, -2), @(25, 4))) {
        $truth[$p[0]] = $p[1]
    }
    $meas = New-Object double[] 61
    for ($i = 0; $i -lt 61; $i++) {
        $s = 0.0
        for ($j = 0; $j -lt 31; $j++) { $s += (PeakDb $freqs[$i] $BAND_FC[$j] 1.0 1.0) * $truth[$j] }
        $meas[$i] = $s
    }
    # a null and a rolloff that must not be boosted
    for ($i = 0; $i -lt 61; $i++) {
        if ($freqs[$i] -lt 60) { $meas[$i] -= 30.0 }
        if ([Math]::Abs($freqs[$i] - 125) -lt 20) { $meas[$i] -= 25.0 }
    }
    $target = New-Object double[] 61
    $fit = Fit $freqs $meas $target

    $pass = 0; $fail = 0
    function Check([bool]$cond, [string]$what) {
        if ($cond) { Write-Host "  PASS  $what" -ForegroundColor Green; $script:pass++ }
        else { Write-Host "  FAIL  $what" -ForegroundColor Red; $script:fail++ }
    }

    $maxResid = 0.0
    for ($i = 0; $i -lt 61; $i++) {
        if (-not $fit.usable[$i]) { continue }
        $r = [Math]::Abs($fit.pred[$i] - $target[$i])
        if ($r -gt $maxResid) { $maxResid = $r }
    }
    $peakBoost = ($fit.gains | Measure-Object -Maximum).Maximum
    $idx125 = [Array]::IndexOf($BAND_FC, 125)
    Check ($maxResid -le 1.5) ("usable band flattened: max residual {0:N2} dB <= 1.5" -f $maxResid)
    Check ($peakBoost -le $BoostCap + 0.001) ("no gain above the +{0} cap (max {1:N2})" -f $BoostCap, $peakBoost)
    Check ([Math]::Abs($fit.gains[$idx125]) -le 2.0) ("the 125 Hz null is not chased (gain {0:N2} dB)" -f $fit.gains[$idx125])
    Check ($fit.gains[2] -eq 0 -and $fit.gains[3] -eq 0) "the sub-60 Hz rolloff is left alone"

    Write-Host ""
    Write-Host ("selftest: {0} passed, {1} failed" -f $pass, $fail)
    exit $(if ($fail) { 1 } else { 0 })
}

# ---------------------------------------------------------------- input
$curves = $null; $source = ""; $title = ""
if ($Bursts) {
    if (-not (Test-Path $Bursts)) { throw "no such file: $Bursts" }
    $j = Get-Content -Raw -Encoding UTF8 $Bursts | ConvertFrom-Json
    if ($j.bursts) {
        $list = @($j.bursts)
        if ($list.Count -eq 0) { throw "no bursts in $Bursts" }
        $pick = if ($Burst -ge 0) { $list[$Burst] } else { $list | Where-Object { $_.ok } | Select-Object -First 1 }
        if (-not $pick) { throw "no ok burst in $Bursts" }
        $bands = @($pick.bands)
        $tag = "burst $($pick.i)"
    } elseif ($j.bands) {
        # a single-curve `analyse` result
        $bands = @($j.bands)
        $tag = Split-Path -Leaf $Bursts
    } else {
        throw "no bands or bursts in $Bursts"
    }
    if ($bands.Count -lt 8) { throw "$tag has no band data (truncated?)" }
    $freqs = New-Object double[] $bands.Count
    $meas = New-Object double[] $bands.Count
    for ($i = 0; $i -lt $bands.Count; $i++) { $freqs[$i] = [double]$bands[$i].f; $meas[$i] = [double]$bands[$i].db }
    $source = "$Bursts $tag"; $title = $tag
} else {
    if (-not $Rta) { $Rta = Join-Path $root 'rta-measurements.json' }
    if (-not (Test-Path $Rta)) { throw "no measurement file: $Rta (pass -Bursts or -Rta)" }
    # @() around the pipeline would wrap the whole array as one element: ConvertFrom-Json emits
    # the list as a single object, so take it first and then enumerate it.
    $parsed = Get-Content -Raw -Encoding UTF8 $Rta | ConvertFrom-Json
    $all = @($parsed)
    $hit = if ($Name) { $all | Where-Object { $_.name -eq $Name } | Select-Object -Last 1 }
           else { $all | Select-Object -Last 1 }
    if (-not $hit) { throw "no measurement named '$Name' in $Rta" }
    $db = @($hit.db)
    if ($db.Count -lt 8) { throw "measurement '$($hit.name)' has only $($db.Count) points" }
    $freqs = New-Object double[] $db.Count
    $meas = New-Object double[] $db.Count
    $gstep = if ($db.Count -gt 100) { 24.0 } else { 6.0 }   # 1/24-oct takes (241) or legacy 1/6 (61)
    for ($i = 0; $i -lt $db.Count; $i++) { $freqs[$i] = 20.0 * [Math]::Pow(2.0, $i / $gstep); $meas[$i] = [double]$db[$i] }
    $source = "$Rta '$($hit.name)'"; $title = $hit.name
}

# optional smoothing over the 1/6-octave points
if ($Smooth -gt 0) {
    $sm = New-Object double[] $meas.Length
    for ($i = 0; $i -lt $meas.Length; $i++) {
        $s = 0.0; $c = 0
        for ($d = -1 * [int]$Smooth; $d -le [int]$Smooth; $d++) {
            $k = $i + $d
            if ($k -ge 0 -and $k -lt $meas.Length) { $s += $meas[$k]; $c++ }
        }
        $sm[$i] = $s / $c
    }
    $meas = $sm
}

# ---------------------------------------------------------------- target
$target = New-Object double[] $freqs.Length
if ($TargetFile) {
    $tj = Get-Content -Raw -Encoding UTF8 $TargetFile | ConvertFrom-Json
    $pts = if ($tj -is [array]) { @($tj) } elseif ($tj.db) { @($tj.db) } else { throw "target file must be a 31-point array or {db:[...]}" }
    if (@($pts).Count -ne 31) { throw "target needs 31 points, got $(@($pts).Count)" }
    # the target is defined at the 31 band centres; interpolate in log frequency
    for ($i = 0; $i -lt $freqs.Length; $i++) {
        $lf = [Math]::Log($freqs[$i]) / [Math]::Log(2.0)
        $pos = 0; $best = 1e9
        for ($j = 0; $j -lt 31; $j++) {
            $d = [Math]::Abs($lf - [Math]::Log($BAND_FC[$j]) / [Math]::Log(2.0))
            if ($d -lt $best) { $best = $d; $pos = $j }
        }
        $target[$i] = [double]$pts[$pos]
    }
} else {
    for ($i = 0; $i -lt $freqs.Length; $i++) {
        $target[$i] = $TiltDbPerOct * ([Math]::Log($freqs[$i] / 1000.0) / [Math]::Log(2.0))
    }
}

# ---------------------------------------------------------------- fit + report
$fit = Fit $freqs $meas $target
$g = $fit.gains

Write-Host ""
Write-Host ("source : {0}" -f $source)
Write-Host ("target : {0}" -f $(if ($TargetFile) { Split-Path -Leaf $TargetFile } elseif ($TiltDbPerOct -ne 0) { ("{0:+0.0;-0.0} dB/oct" -f $TiltDbPerOct) } else { "flat" }))
Write-Host ("model  : RBJ peaking, Q=1, 48 kHz; boosts <= +{0}, cuts >= -{1}; floor -{2} dB" -f $BoostCap, $CutCap, $FloorBelow)
Write-Host ""
Write-Host ("  {0,6} {1,8} {2,8} {3,8} {4,8} {5,8}" -f 'Hz', 'meas', 'target', 'gain', 'pred', 'resid')

$maxResid = 0.0; $cuts = 0; $boosts = 0; $peakBoost = 0.0; $peakAt = 0
for ($j = 0; $j -lt 31; $j++) {
    # the measurement point nearest this band centre
    $i = 0; $best = 1e9
    for ($q = 0; $q -lt $freqs.Length; $q++) {
        $d = [Math]::Abs([Math]::Log($freqs[$q] / $BAND_FC[$j]))
        if ($d -lt $best) { $best = $d; $i = $q }
    }
    $m = $meas[$i] - $fit.base
    $p = $fit.pred[$i]
    $r = $p - $target[$i]
    $mark = if ($fit.usable[$i]) { "" } else { " ~" }
    if ($fit.usable[$i] -and [Math]::Abs($r) -gt $maxResid) { $maxResid = [Math]::Abs($r) }
    if ($g[$j] -lt -0.001) { $cuts++ }
    if ($g[$j] -gt 0.001) { $boosts++; if ($g[$j] -gt $peakBoost) { $peakBoost = $g[$j]; $peakAt = $BAND_FC[$j] } }
    Write-Host ("  {0,6} {1,8:N1} {2,8:N1} {3,8:+0.0;-0.0;0.0} {4,8:N1} {5,8:+0.0;-0.0;0.0}{6}" -f
        $BAND_FC[$j], $m, $target[$i], $g[$j], $p, $r, $mark)
}

Write-Host ""
Write-Host ("  usable : -{0} dB under the top; max residual {1:N2} dB" -f $FloorBelow, $maxResid)
Write-Host ("  gains  : {0} cut, {1} boost; peak boost {2:+0.0;-0.0} dB at {3} Hz" -f $cuts, $boosts, $peakBoost, $peakAt)
Write-Host "  ~ = below the floor, excluded from the fit (cut or leave, never boost)"

$proposal = [ordered]@{
    at = (Get-Date).ToString('s')
    source = $source
    target = $(if ($TargetFile) { Split-Path -Leaf $TargetFile } elseif ($TiltDbPerOct -ne 0) { "$TiltDbPerOct dB/oct" } else { "flat" })
    model = "RBJ peaking Q=1 fs=48000, ridge 0.10"
    boostCap = $BoostCap; cutCap = $CutCap; floorBelow = $FloorBelow
    bassDb = $fit.base
    bands = @(for ($j = 0; $j -lt 31; $j++) { [ordered]@{ fc = $BAND_FC[$j]; gain = [Math]::Round($g[$j], 1) } })
    predicted = @(for ($i = 0; $i -lt $freqs.Length; $i++) { [ordered]@{ f = [Math]::Round($freqs[$i], 1); db = [Math]::Round($fit.pred[$i], 2); usable = $fit.usable[$i] } })
    maxResidualDb = [Math]::Round($maxResid, 2)
}
if (-not $Out) {
    $dir = Join-Path $root 'evidence\proposals'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $safe = ($title -replace '[^\w.-]+', '_')
    $Out = Join-Path $dir ("proposal-{0}-{1}.json" -f $safe, (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$proposal | ConvertTo-Json -Depth 5 | Set-Content -Path $Out -Encoding UTF8
Write-Host ""
Write-Host "proposal: $Out" -ForegroundColor Cyan
exit 0
