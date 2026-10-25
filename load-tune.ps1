# Full .idsp tune load with per-step readback verification.
# Writes EQ (cmd 14) + crossovers (cmd 13) + output gains/delays (cmd 11) for the
# file's memory index onto the live device, then verifies every block against the
# FILE (not the old baseline) and confirms master/groups were left untouched.
# Snapshots: pre-load info+dspcfg first; restore references are
#   evidence\tune-baseline-2026-10-16.json + evidence\passband-baseline-2026-10-16.json
$ErrorActionPreference = 'Stop'
$base = 'http://localhost:8585'
$root = 'C:\Users\sdotm\OneDrive\Documents\Default Project\ds18-bridge'
$file = 'C:\Users\sdotm\Downloads\w6784b.idsp'
$stamp = (Get-Date).ToString('yyyy-MM-dd')

function Post($path, $obj) {
    (Invoke-WebRequest "$base$path" -Method POST -Body ($obj | ConvertTo-Json -Depth 8) `
        -ContentType 'application/json' -UseBasicParsing -TimeoutSec 15).Content | ConvertFrom-Json
}
function HexToBytes($h) {
    $b = New-Object byte[] ($h.Length / 2)
    for ($i = 0; $i -lt $b.Length; $i++) { $b[$i] = [Convert]::ToByte($h.Substring($i * 2, 2), 16) }
    $b
}
function PbCanon($pb) {  # canonical 336-hex-char slot strings from a JSON passband block
    $lf = [BitConverter]::ToString([BitConverter]::GetBytes([uint16]$pb.lowFc)).Replace('-', '')
    $hf = [BitConverter]::ToString([BitConverter]::GetBytes([uint16]$pb.highFc)).Replace('-', '')
    $low = $lf + ('{0:X2}' -f $pb.lowSlope) + ('{0:X2}' -f $pb.lowFilterType) + ($pb.lowCoffHex -join '')
    $high = $hf + ('{0:X2}' -f $pb.highSlope) + ('{0:X2}' -f $pb.highFilterType) + ($pb.highCoffHex -join '')
    @($low, $high)
}

[xml]$x = Get-Content $file -Raw
$fm = [int]$x.DP12_Param.RunStatus.mainMemory
$fo = $x.DP12_Param.SelectNodes('memory')[$fm].output
"file: master=$($x.DP12_Param.RunStatus.mainVolume) dB, memory index $fm"

# ---- 0. pre-load snapshot -------------------------------------------------
$info = (Invoke-WebRequest "$base/api/info" -UseBasicParsing -TimeoutSec 10).Content | ConvertFrom-Json
$cfg = (Invoke-WebRequest "$base/api/dspcfg" -UseBasicParsing -TimeoutSec 15).Content | ConvertFrom-Json
@{ captured = (Get-Date).ToString('o'); info = $info; cfgBodyHex = $cfg.bodyHex } |
    ConvertTo-Json -Depth 6 | Set-Content "$root\evidence\pre-load-snapshot-$stamp.json" -Encoding UTF8
"snapshot: master=$($info.mainDb), groups=[$($info.groupDb -join ',')], open=$($info.open)"
if (-not $info.open) { throw 'link not open - aborting before any write' }

# ---- 1. EQ (cmd 14) x8 ----------------------------------------------------
$eqLog = @()
for ($p = 0; $p -lt 8; $p++) {
    $bands = @()
    for ($i = 0; $i -lt 31; $i++) {
        $e = $fo[$p].eq[$i]
        $bands += , @{ fc = [int]$e.Fc; gain = [double]$e.Gain; q = [double]$e.Q; enable = $true }
    }
    $r = Post '/api/eq' @{ mem = $fm; path = $p; byPass = $false; bands = $bands }
    $eqLog += "p$p tx=$($r.txBytes)B acked=$($r.acked)"
    Start-Sleep -Milliseconds 150
}
"eq writes: $($eqLog -join ' | ')"

# ---- 2. crossovers (cmd 13) x8 -------------------------------------------
$pbLog = @()
for ($p = 0; $p -lt 8; $p++) {
    $f0 = $fo[$p].passBand[0]; $f1 = $fo[$p].passBand[1]
    $r = Post '/api/passband' @{
        mem = $fm; path = $p
        low  = @{ fc = [int]$f0.Fc; slope = [int]$f0.Slope; filterType = [int]$f0.filterType; hiLow = [int]$f0.hiLowType; butt = [int]$f0.butt_Type; enable = [bool]([int]$f0.Enable) }
        high = @{ fc = [int]$f1.Fc; slope = [int]$f1.Slope; filterType = [int]$f1.filterType; hiLow = [int]$f1.hiLowType; butt = [int]$f1.butt_Type; enable = [bool]([int]$f1.Enable) }
    }
    $pbLog += "p$p L$($r.lowFc)/sl$($r.lowSlope) H$($r.highFc)/sl$($r.highSlope) acked=$($r.acked)"
}
"passband writes: $($pbLog -join ' | ')"

# ---- 3. output gains + delays (cmd 11), complete 16-entry block -----------
# Plain @() arrays on purpose: PS 5.1 ConvertTo-Json wraps New-Object primitive
# arrays in {"value":[...]}, which deserializes as an object and fails validation.
$gain = @(); $delay = @(); $mute = @(); $revert = @()
for ($i = 0; $i -lt 16; $i++) { $gain += 0.0; $delay += 0.0; $mute += $false; $revert += $false }
for ($p = 0; $p -lt 8; $p++) { $gain[$p] = [double]$fo[$p].gain; $delay[$p] = [double]$fo[$p].delay }
# outputs 8-15: vendor rule encodes only i < outPathCount (8); the frame zeroes the rest.
$rOut = Post '/api/output' @{ mem = $fm; mute = $mute; revert = $revert; gainDb = $gain; delayMs = $delay }
"output write: enableMask=0x$('{0:X4}' -f $rOut.enableMask) transmitted=$($rOut.transmitted) acked=$($rOut.acked) groups echoed=[$($rOut.groupDb -join ',')]"

# ---- 4. VERIFY -------------------------------------------------------------
$fail = @()

# 4a. EQ vs file
for ($p = 0; $p -lt 8; $p++) {
    $r = Post '/api/eqread' @{ mem = $fm; path = $p }
    if ($r.byPass) { $fail += "p$p byPass=true (file has none)" }
    for ($i = 0; $i -lt 31; $i++) {
        $e = $fo[$p].eq[$i]; $b = $r.bands[$i]
        if ([int]$b.fc -ne [int]$e.Fc) { $fail += "p$p band$i fc $($b.fc) != file $($e.Fc)" }
        $dg = [math]::Abs([double]$b.gain - [double]$e.Gain)
        if ($dg -gt 0.061) { $fail += "p$p band$i gain $($b.gain) != file $($e.Gain)" }
        $dq = [math]::Abs([double]$b.q - [double]$e.Q)
        if ($dq -gt 0.051) { $fail += "p$p band$i q $($b.q) != file $($e.Q)" }
    }
    $rPass = $r.passband
    # 4b. passband scalars vs file, coefficients vs a fresh dry-run of the same values
    $f0 = $fo[$p].passBand[0]; $f1 = $fo[$p].passBand[1]
    if ([int]$rPass.lowFc -ne [int]$f0.Fc -or [int]$rPass.highFc -ne [int]$f1.Fc -or
        [int]$rPass.lowSlope -ne [int]$f0.Slope -or [int]$rPass.highSlope -ne [int]$f1.Slope -or
        [int]$rPass.lowFilterType -ne [int]$f0.filterType -or [int]$rPass.highFilterType -ne [int]$f1.filterType) {
        $fail += "p$p passband scalars != file (dev L$($rPass.lowFc)/sl$($rPass.lowSlope) H$($rPass.highFc)/sl$($rPass.highSlope))"
    }
    $dry = Post '/api/passband' @{
        mem = $fm; path = $p; dryRun = $true
        low  = @{ fc = [int]$f0.Fc; slope = [int]$f0.Slope; filterType = [int]$f0.filterType; hiLow = [int]$f0.hiLowType; butt = [int]$f0.butt_Type; enable = [bool]([int]$f0.Enable) }
        high = @{ fc = [int]$f1.Fc; slope = [int]$f1.Slope; filterType = [int]$f1.filterType; hiLow = [int]$f1.hiLowType; butt = [int]$f1.butt_Type; enable = [bool]([int]$f1.Enable) }
    }
    $rb = PbCanon $rPass
    $dc = $dry.pbHex
    $dLow = $dc.Substring(0, 4) + $dc.Substring(8, 2) + $dc.Substring(12, 2) + $dc.Substring(16, 160)
    $dHigh = $dc.Substring(4, 4) + $dc.Substring(10, 2) + $dc.Substring(14, 2) + $dc.Substring(176, 160)
    if ($rb[0] -ne $dLow) { $fail += "p$p low coeffs != HlpGetCoff output" }
    if ($rb[1] -ne $dHigh) { $fail += "p$p high coeffs != HlpGetCoff output" }
}

# 4c. output block vs file (decode dspcfg memOut[1])
$cfg2 = (Invoke-WebRequest "$base/api/dspcfg" -UseBasicParsing -TimeoutSec 15).Content | ConvertFrom-Json
$body = HexToBytes $cfg2.bodyHex
$enable = [BitConverter]::ToUInt16($body, 1760)
$revertM = [BitConverter]::ToUInt16($body, 1762)
if ($enable -ne 0x00FF) { $fail += "enable mask 0x$('{0:X4}' -f $enable) != 0x00FF" }
if ($revertM -ne 0) { $fail += "revert mask $revertM != 0" }
$outCheck = @()
for ($i = 0; $i -lt 8; $i++) {
    $gu = [BitConverter]::ToUInt16($body, 1764 + 2 * $i)
    $du = [BitConverter]::ToUInt16($body, 1796 + 2 * $i)
    $gdb = -1.0 * $gu / 10.0
    $dms = $du / 100.0
    $fg = [double]$fo[$i].gain
    $fd = [double]$fo[$i].delay
    if ([math]::Abs($gdb - $fg) -gt 0.051) { $fail += "out$i gain $gdb != file $fg" }
    if ([math]::Abs($dms - $fd) -gt 0.0051) { $fail += "out$i delay $dms != file $fd" }
    $outCheck += "g$i=$($gdb)dB d$i=$($dms)ms"
}

# 4d. master / groups / mute untouched (option 1 guarantee)
$info2 = (Invoke-WebRequest "$base/api/info" -UseBasicParsing -TimeoutSec 10).Content | ConvertFrom-Json
if ([math]::Abs([double]$info2.mainDb - [double]$info.mainDb) -gt 0.01) { $fail += "master moved: $($info.mainDb) -> $($info2.mainDb)" }
$g0 = $info.groupDb -join ','; $g1 = $info2.groupDb -join ','
if ($g1 -ne $g0) { $fail += "groups moved: $g0 -> $g1" }
if ([int]$info2.mem -ne $fm) { $fail += "memory moved: $($info2.mem)" }
if ($info2.mute) { $fail += "device is muted" }

# ---- 5. evidence -----------------------------------------------------------
@{
    captured      = (Get-Date).ToString('o')
    file          = $file
    fileModified  = (Get-Item $file).LastWriteTime.ToString('o')
    memoryIndex   = $fm
    eqWrites      = $eqLog
    passbandWrites = $pbLog
    outputWrite   = @{ enableMask = $enable; gains = $outCheck }
    postInfo      = $info2
    verdict       = $(if ($fail.Count -eq 0) { 'ALL VERIFIED' } else { "FAILED: $($fail.Count)" })
    failures      = $fail
} | ConvertTo-Json -Depth 6 | Set-Content "$root\evidence\tune-load-$stamp.json" -Encoding UTF8

""
if ($fail.Count -eq 0) {
    "LOAD COMPLETE - ALL VERIFIED against file: EQ 248x3, passband 8x(2 scalars + 40 coeffs), output 8x(gain+delay), master $($info2.mainDb), groups [$($info2.groupDb -join ',')] untouched"
} else {
    "LOAD FINISHED WITH $($fail.Count) FAILURE(S):"
    $fail | ForEach-Object { "  $_" }
}