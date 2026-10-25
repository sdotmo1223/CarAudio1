# apply-tune.ps1 - write tune-plan.json to the DSP, then prove it took.
#
# Order, and why:
#   1. snapshot the entire live state to disk (gains, delays, mutes, polarity, EQ per path)
#      BEFORE a single byte is sent. That snapshot is the only way back.
#   2. EQ per path  (/api/eq), then the output block (/api/output).
#      The output block is written once, complete: cmd 11 carries all sixteen outputs in one
#      frame, and a zero in an omitted field is 0 dB full level, not "leave alone".
#   3. read every path back and compare. A write that is not verified is not a write.
#
#   .\apply-tune.ps1 -Plan tune\tune-plan.json            # write + verify
#   .\apply-tune.ps1 -Plan tune\tune-plan.json -WhatIf    # snapshot and report, change nothing
#   .\apply-tune.ps1 -Restore evidence\tune-before-....json
param(
    [string]$Plan = 'tune\tune-plan.json',
    [string]$Restore = '',
    [double]$Tolerance = 0.11,
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'
$base = 'http://127.0.0.1:8585'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

function Fail([string]$m) { Write-Host "error: $m" -ForegroundColor Red; exit 1 }
function Say([string]$m)  { Write-Host $m }

function Api([string]$path, $body) {
    if ($null -eq $body) { return Invoke-RestMethod -Uri ($base + $path) -Method Get -TimeoutSec 30 }
    return Invoke-RestMethod -Uri ($base + $path) -Method Post -ContentType 'application/json' `
        -Body ($body | ConvertTo-Json -Depth 8) -TimeoutSec 40
}

function LiveState() {
    # /api/info, NOT /api/status: only info carries the cmd-11 output block
    # (outputGainDb / outputDelayMs / outputMute / outputRevert). Status is the light
    # endpoint and returns none of them, so reading it here made the first real write throw.
    $s = Api '/api/info' $null
    if (-not $s.open) { Fail ("the DSP is not answering ({0}). Plug it in and run .\preflight.ps1 first." -f $s.error) }
    return $s
}

function Snapshot([int]$mem, [string]$tag) {
    $st = LiveState
    $paths = @()
    foreach ($p in 0..7) {
        $d = Api '/api/eqread' @{ mem = $mem; path = $p }
        if (-not $d.ok) { Fail ("path $p read failed: " + $d.error) }
        $paths += [ordered]@{ path = $p; byPass = $d.byPass; bands = $d.bands }
    }
    $snap = [ordered]@{
        at      = (Get-Date).ToString('s')
        tag     = $tag
        mem     = $mem
        mute    = @($st.outputMute)
        gainDb  = @($st.outputGainDb)
        delayMs = @($st.outputDelayMs)
        revert  = @($st.outputRevert)
        paths   = $paths
    }
    $f = Join-Path $root ("evidence\tune-before-{0}.json" -f (Get-Date).ToString('yyyyMMdd-HHmmss'))
    $snap | ConvertTo-Json -Depth 8 | Set-Content -Path $f -Encoding UTF8
    return $f
}

# ---------------------------------------------------------------- restore
if ($Restore) {
    if (-not (Test-Path $Restore)) { Fail "no such snapshot: $Restore" }
    $snap = Get-Content -Raw -Encoding UTF8 $Restore | ConvertFrom-Json
    $mem = [int]$snap.mem
    Say ("restoring {0} (memory {1})" -f (Split-Path -Leaf $Restore), ($mem + 1))

    if ($WhatIf) { Say 'what-if: nothing written'; exit 0 }

    foreach ($p in @($snap.paths)) {
        $r = Api '/api/eq' @{ mem = $mem; path = [int]$p.path; byPass = [bool]$p.byPass; bands = @($p.bands) }
        if (-not $r.ok) { Fail ("path {0} EQ restore refused: {1}" -f $p.path, $r.error) }
    }
    $r = Api '/api/output' @{ mem = $mem; mute = @($snap.mute); gainDb = @($snap.gainDb);
                              delayMs = @($snap.delayMs); revert = @($snap.revert) }
    if (-not $r.ok) { Fail ("output block restore refused: " + $r.error) }
    Say 'restored: EQ and output block written back from the snapshot.'
    exit 0
}

# ---------------------------------------------------------------- apply
if (-not (Test-Path $Plan)) { Fail "no plan at $Plan - run tune\fit-tune.py first" }

# The parsed plan goes into $tune, never $plan. PowerShell variable names are
# case-insensitive, so "$plan = ... | ConvertFrom-Json" assigns to the [string]
# parameter above and the object is coerced straight back into a string. That is
# exactly how the first real run reported success while writing nothing.
$tune = Get-Content -Raw -Encoding UTF8 $Plan | ConvertFrom-Json
$pathProps = @($tune.paths.PSObject.Properties)
if ($pathProps.Count -eq 0) {
    Fail "the plan has no paths in it ($Plan) - refusing to report success for nothing"
}

$st = LiveState
$mem = [int]$st.mem
Say ("device: memory {0}, master {1} dB, mute {2}" -f ($mem + 1), $st.mainDb, $st.mute)

$snapFile = Snapshot $mem 'before-tune'
Say ("snapshot -> {0}" -f (Split-Path -Leaf $snapFile))

$gain  = @($st.outputGainDb  | ForEach-Object { [double]$_ })
$delay = @($st.outputDelayMs | ForEach-Object { [double]$_ })
$mute  = @($st.outputMute)
$rev   = @($st.outputRevert)

Say ''
Say '  path  channel        old gain   delta   new gain'
$newGain = @{}
foreach ($prop in $tune.paths.PSObject.Properties) {
    $path = [int]$prop.Name
    $p = $prop.Value
    $old = $gain[$path]
    $delta = [double]$p.gainDeltaDb
    $new = [math]::Max(-40.0, [math]::Min(0.0, [math]::Round($old + $delta, 1)))
    $newGain[$path] = $new
    Say ('  {0,4}  {1,-13} {2,7:N1}  {3,6:+0.0;-0.0;0.0}  {4,8:N1}' -f $path, $p.channel, $old, $delta, $new)
}

if ($WhatIf) { Say ''; Say 'what-if: nothing written'; exit 0 }

# 1. EQ first: the gains only mean something once the shapes are in place
Say ''
$bad = @()
$verifiedPaths = 0
foreach ($prop in $tune.paths.PSObject.Properties) {
    $path = [int]$prop.Name
    $bands = @($prop.Value.bands)
    $r = Api '/api/eq' @{ mem = $mem; path = $path; byPass = $false; bands = $bands }
    if (-not $r.ok) { Fail ("path {0} EQ write refused: {1}" -f $path, $r.error) }

    $back = Api '/api/eqread' @{ mem = $mem; path = $path }
    if (-not $back.ok) { Fail ("path {0} EQ read-back failed: {1}" -f $path, $back.error) }
    $got = @($back.bands)

    # compare only the bands we set; the rest are deliberately zero
    $mismatch = 0
    for ($i = 0; $i -lt $bands.Count; $i++) {
        $fcOk = ([math]::Abs([double]$got[$i].fc - [double]$bands[$i].fc) -le 1.0)
        $gOk  = ([math]::Abs([double]$got[$i].gain - [double]$bands[$i].gain) -le $Tolerance)
        $qOk  = ([math]::Abs([double]$got[$i].q - [double]$bands[$i].q) -le 0.15)
        if (-not ($fcOk -and $gOk -and $qOk)) { $mismatch++ }
    }
    if ($mismatch -eq 0) {
        $verifiedPaths++
        Say ('  path {0}  {1,2} band(s) verified' -f $path, $bands.Count)
    } else {
        $bad += "path $path ($mismatch band(s) differ)"
        Say ('  path {0}  {1} band(s) DID NOT read back' -f $path, $mismatch) -ForegroundColor Red
    }
}

# 2. the output block, complete
foreach ($k in $newGain.Keys) { $gain[$k] = $newGain[$k] }
$r = Api '/api/output' @{ mem = $mem; mute = $mute; gainDb = $gain; delayMs = $delay; revert = $rev }
if (-not $r.ok) { Fail ("output block refused: " + $r.error) }

$after = LiveState
$gBad = @()
foreach ($k in $newGain.Keys) {
    $got = [double]$after.outputGainDb[$k]
    if ([math]::Abs($got - $newGain[$k]) -gt $Tolerance) { $gBad += ("path {0}: asked {1:N1}, read {2:N1}" -f $k, $newGain[$k], $got) }
}
$dBad = @()
for ($i = 0; $i -lt 16; $i++) {
    if ([math]::Abs([double]$after.outputDelayMs[$i] - $delay[$i]) -gt 0.01) { $dBad += $i }
}

Say ''
if ($gBad.Count) { $gBad | ForEach-Object { Say ('  GAIN MISMATCH ' + $_) -ForegroundColor Red }; $bad += 'gains' }
else { Say '  output gains verified on the device' }
if ($dBad.Count) { Say ('  DELAY CHANGED on path(s) ' + ($dBad -join ',') + ' - this tune never touches delays') -ForegroundColor Red; $bad += 'delays' }
else { Say '  delays unchanged, as intended' }

Say ''
# A verifier that checks nothing must never report success: that is exactly what
# happened when the plan object was silently a string and both loops ran zero times.
if ($verifiedPaths -ne $pathProps.Count) {
    $bad += ("only {0} of {1} paths were verified" -f $verifiedPaths, $pathProps.Count)
}
if ($bad.Count) {
    Fail ("not everything landed: {0}. Snapshot: {1} -> .\apply-tune.ps1 -Restore '{1}'" -f ($bad -join '; '), $snapFile)
}
Say ("TUNE APPLIED - {0} of {1} paths verified band by band, every output level read back." -f $verifiedPaths, $pathProps.Count)
Say ("way back: .\apply-tune.ps1 -Restore '{0}'" -f $snapFile)
