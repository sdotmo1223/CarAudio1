# Full .idsp load for a file whose memory differs from the running cell.
# Same verified sequence as load-tune.ps1 (EQ cmd14 x8 -> crossovers cmd13 x8 ->
# output cmd11), but it FIRST jumps the device to the file's memory (cmd 12, the job
# the page's applyMemory does) so the load is audible, then verifies every block
# against the FILE and confirms master/groups were left untouched.
#
# Usage: powershell -File load-tune-jump.ps1 -File <path.idsp>
param(
    [Parameter(Mandatory = $true)][string]$File,
    # Which memory of the file to load, 0-based (the app shows 1-4, so "memory 2" = -Memory 1).
    # -1 means "whatever the file itself had live when it was saved" (RunStatus.mainMemory) -
    # which is NOT necessarily the memory the owner wants played.
    [int]$Memory = -1
)
$ErrorActionPreference = 'Stop'
$base = 'http://localhost:8585'
$root = 'C:\Users\sdotm\OneDrive\Documents\Default Project\ds18-bridge'
$file = $File
$stamp = (Get-Date).ToString('yyyy-MM-dd-HHmm')
$logPath = "$root\evidence\load-run-$stamp.log"
$say = { param($m) $m | Tee-Object -FilePath $logPath -Append }

function Post($path, $obj) {
    (Invoke-WebRequest "$base$path" -Method POST -Body ($obj | ConvertTo-Json -Depth 8) `
        -ContentType 'application/json' -UseBasicParsing -TimeoutSec 25).Content | ConvertFrom-Json
}
function HexToBytes($h) {
    $b = New-Object byte[] ($h.Length / 2)
    for ($i = 0; $i -lt $b.Length; $i++) { $b[$i] = [Convert]::ToByte($h.Substring($i * 2, 2), 16) }
    $b
}
function PbCanon($pb) {
    $lf = [BitConverter]::ToString([BitConverter]::GetBytes([uint16]$pb.lowFc)).Replace('-', '')
    $hf = [BitConverter]::ToString([BitConverter]::GetBytes([uint16]$pb.highFc)).Replace('-', '')
    $low = $lf + ('{0:X2}' -f $pb.lowSlope) + ('{0:X2}' -f $pb.lowFilterType) + ($pb.lowCoffHex -join '')
    $high = $hf + ('{0:X2}' -f $pb.highSlope) + ('{0:X2}' -f $pb.highFilterType) + ($pb.highCoffHex -join '')
    @($low, $high)
}

"=== load start $(Get-Date -Format o) ===" | Set-Content $logPath -Encoding UTF8

[xml]$x = Get-Content $file -Raw
$fmFile = [int]$x.DP12_Param.RunStatus.mainMemory
$memCount = @($x.DP12_Param.SelectNodes('memory')).Count
$fm = if ($Memory -ge 0) { $Memory } else { $fmFile }
if ($fm -lt 0 -or $fm -ge $memCount) {
    throw ("memory index $fm is out of range - file has $memCount memories (indices 0-" + ($memCount - 1) + ")")
}
$fo = $x.DP12_Param.SelectNodes('memory')[$fm].output
& $say "file: $file"
& $say ("file has " + $memCount + " memories; file's own live memory = " + $fmFile + " (shown as memory " + ($fmFile + 1) + ")")
& $say ("loading memory index " + $fm + " (shown as memory " + ($fm + 1) + ")")

# ---- 0. pre-load snapshot + link check ------------------------------------
$info = (Invoke-WebRequest "$base/api/info" -UseBasicParsing -TimeoutSec 10).Content | ConvertFrom-Json
$preOut = @()
for ($p = 0; $p -lt 8; $p++) {
    $preOut += , @{ p = $p; gain = [double]$fo[$p].gain; delay = [double]$fo[$p].delay
        hp = [int]$fo[$p].passBand[0].Fc; hpSlope = [int]$fo[$p].passBand[0].Slope
        lp = [int]$fo[$p].passBand[1].Fc; lpSlope = [int]$fo[$p].passBand[1].Slope }
}
@{ captured = (Get-Date).ToString('o'); file = $file; fileMem = $fm; preInfo = $info; fileOutputs = $preOut } |
    ConvertTo-Json -Depth 6 | Set-Content "$root\evidence\load-prelaunch-$stamp.json" -Encoding UTF8
& $say ("pre: open=" + $info.open + " mem=" + $info.mem + " mainDb=" + $info.mainDb + " groups=[" + ($info.groupDb -join ',') + "] src=" + $info.source)
if (-not $info.open) { throw 'link not open - aborting before any write' }

# ---- 0b. snapshot the TARGET memory's current EQ + crossovers -------------
# (revert reference: cmd 11 levels cannot be read back, but cmd 14 curves and cmd 13
#  crossovers can, so at least those are recoverable if this load must be undone)
$tgtSnap = @()
$snapOk = $true
for ($p = 0; $p -lt 8; $p++) {
    try {
        $tr = Post '/api/eqread' @{ mem = $fm; path = $p }
        if ($tr.ok -eq $false) { $snapOk = $false; break }
        $pb = $tr.passband
        $tgtSnap += , @{ p = $p; byPass = $tr.byPass
            hpFc = $pb.lowFc; hpSlope = $pb.lowSlope; hpFilter = $pb.lowFilterType
            lpFc = $pb.highFc; lpSlope = $pb.highSlope; lpFilter = $pb.highFilterType
            bands = @($tr.bands | ForEach-Object { @{ fc = $_.fc; gain = $_.gain; q = $_.q } }) }
    } catch { $snapOk = $false; break }
}
if ($snapOk) {
    @{ captured = (Get-Date).ToString('o'); memory = $fm; outputs = $tgtSnap } |
        ConvertTo-Json -Depth 8 | Set-Content "$root\evidence\mem$fm-before-$stamp.json" -Encoding UTF8
    & $say ("target memory " + ($fm + 1) + " pre-state captured (EQ + crossovers) -> evidence\mem" + $fm + "-before-" + $stamp + ".json")
} else {
    & $say ("WARNING: could not snapshot target memory " + ($fm + 1) + " pre-state")
}

# ---- 1. jump to the file's memory (cmd 12) --------------------------------
$jumpHex = ''
if ([int]$info.mem -ne $fm) {
    $groups = @(); foreach ($g in $info.groupDb) { $groups += [double]$g }
    $jr = Post '/api/master' @{ mainDb = [double]$info.mainDb; mute = [bool]$info.mute; groups = $groups; mem = $fm; source = [int]$info.source }
    if (-not $jr.ok) { throw ("memory jump failed: " + $jr.error) }
    $jumpHex = $jr.txHex
    & $say ("jumped to memory " + ($fm + 1) + " (memIndex " + $fm + ") tx=" + $jumpHex)
    Start-Sleep -Milliseconds 400
} else {
    & $say ("already on memory " + ($fm + 1))
}

# ---- 2. EQ (cmd 14) x8 ----------------------------------------------------
$eqLog = @()
for ($p = 0; $p -lt 8; $p++) {
    $bands = @()
    for ($i = 0; $i -lt 31; $i++) {
        $e = $fo[$p].eq[$i]
        $bands += , @{ fc = [int]$e.Fc; gain = [double]$e.Gain; q = [double]$e.Q; enable = $true }
    }
    $r = Post '/api/eq' @{ mem = $fm; path = $p; byPass = $false; bands = $bands }
    $eqLog += ("p" + $p + " tx=" + $r.txBytes + "B acked=" + $r.acked)
    Start-Sleep -Milliseconds 150
}
& $say ("eq writes: " + ($eqLog -join ' | '))

# ---- 3. crossovers (cmd 13) x8 -------------------------------------------
$pbLog = @()
for ($p = 0; $p -lt 8; $p++) {
    $f0 = $fo[$p].passBand[0]; $f1 = $fo[$p].passBand[1]
    $r = Post '/api/passband' @{
        mem = $fm; path = $p
        low  = @{ fc = [int]$f0.Fc; slope = [int]$f0.Slope; filterType = [int]$f0.filterType; hiLow = [int]$f0.hiLowType; butt = [int]$f0.butt_Type; enable = [bool]([int]$f0.Enable) }
        high = @{ fc = [int]$f1.Fc; slope = [int]$f1.Slope; filterType = [int]$f1.filterType; hiLow = [int]$f1.hiLowType; butt = [int]$f1.butt_Type; enable = [bool]([int]$f1.Enable) }
    }
    $pbLog += ("p" + $p + " L" + $r.lowFc + "/sl" + $r.lowSlope + " H" + $r.highFc + "/sl" + $r.highSlope + " acked=" + $r.acked)
}
& $say ("passband writes: " + ($pbLog -join ' | '))

# ---- 4. output gains + delays (cmd 11) -----------------------------------
$gain = @(); $delay = @(); $mute = @(); $revert = @()
for ($i = 0; $i -lt 16; $i++) { $gain += 0.0; $delay += 0.0; $mute += $false; $revert += $false }
for ($p = 0; $p -lt 8; $p++) { $gain[$p] = [double]$fo[$p].gain; $delay[$p] = [double]$fo[$p].delay }
$rOut = Post '/api/output' @{ mem = $fm; mute = $mute; revert = $revert; gainDb = $gain; delayMs = $delay }
& $say ("output write: enableMask=0x" + ('{0:X4}' -f $rOut.enableMask) + " transmitted=" + $rOut.transmitted + " acked=" + $rOut.acked + " groups echoed=[" + ($rOut.groupDb -join ',') + "]")

# ---- 5. VERIFY ------------------------------------------------------------
$fail = @()

for ($p = 0; $p -lt 8; $p++) {
    $r = Post '/api/eqread' @{ mem = $fm; path = $p }
    if ($r.byPass) { $fail += ("p" + $p + " byPass=true (file has none)") }
    for ($i = 0; $i -lt 31; $i++) {
        $e = $fo[$p].eq[$i]; $b = $r.bands[$i]
        if ([int]$b.fc -ne [int]$e.Fc) { $fail += ("p" + $p + " band" + $i + " fc " + $b.fc + " != file " + $e.Fc) }
        $dg = [math]::Abs([double]$b.gain - [double]$e.Gain)
        if ($dg -gt 0.061) { $fail += ("p" + $p + " band" + $i + " gain " + $b.gain + " != file " + $e.Gain) }
        $dq = [math]::Abs([double]$b.q - [double]$e.Q)
        if ($dq -gt 0.051) { $fail += ("p" + $p + " band" + $i + " q " + $b.q + " != file " + $e.Q) }
    }
    $rPass = $r.passband
    $f0 = $fo[$p].passBand[0]; $f1 = $fo[$p].passBand[1]
    if ([int]$rPass.lowFc -ne [int]$f0.Fc -or [int]$rPass.highFc -ne [int]$f1.Fc -or
        [int]$rPass.lowSlope -ne [int]$f0.Slope -or [int]$rPass.highSlope -ne [int]$f1.Slope -or
        [int]$rPass.lowFilterType -ne [int]$f0.filterType -or [int]$rPass.highFilterType -ne [int]$f1.filterType) {
        $fail += ("p" + $p + " passband scalars != file (dev L" + $rPass.lowFc + "/sl" + $rPass.lowSlope + " H" + $rPass.highFc + "/sl" + $rPass.highSlope + ")")
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
    if ($rb[0] -ne $dLow) { $fail += ("p" + $p + " low coeffs != HlpGetCoff output") }
    if ($rb[1] -ne $dHigh) { $fail += ("p" + $p + " high coeffs != HlpGetCoff output") }
}

$cfg2 = (Invoke-WebRequest "$base/api/dspcfg" -UseBasicParsing -TimeoutSec 20).Content | ConvertFrom-Json
$body = HexToBytes $cfg2.bodyHex
$enable = [BitConverter]::ToUInt16($body, 1760)
$revertM = [BitConverter]::ToUInt16($body, 1762)
if ($enable -ne 0x00FF) { $fail += ("enable mask 0x" + ('{0:X4}' -f $enable) + " != 0x00FF") }
if ($revertM -ne 0) { $fail += ("revert mask " + $revertM + " != 0") }
$outCheck = @()
for ($i = 0; $i -lt 8; $i++) {
    $gu = [BitConverter]::ToUInt16($body, 1764 + 2 * $i)
    $du = [BitConverter]::ToUInt16($body, 1796 + 2 * $i)
    $gdb = -1.0 * $gu / 10.0
    $dms = $du / 100.0
    $fg = [double]$fo[$i].gain
    $fd = [double]$fo[$i].delay
    if ([math]::Abs($gdb - $fg) -gt 0.051) { $fail += ("out" + $i + " gain " + $gdb + " != file " + $fg) }
    if ([math]::Abs($dms - $fd) -gt 0.0051) { $fail += ("out" + $i + " delay " + $dms + " != file " + $fd) }
    $outCheck += ("g" + $i + "=" + $gdb + "dB d" + $i + "=" + $dms + "ms")
}

$info2 = (Invoke-WebRequest "$base/api/info" -UseBasicParsing -TimeoutSec 10).Content | ConvertFrom-Json
if ([math]::Abs([double]$info2.mainDb - [double]$info.mainDb) -gt 0.01) { $fail += ("master moved: " + $info.mainDb + " -> " + $info2.mainDb) }
$g0 = $info.groupDb -join ','; $g1 = $info2.groupDb -join ','
if ($g1 -ne $g0) { $fail += ("groups moved: " + $g0 + " -> " + $g1) }
if ([int]$info2.mem -ne $fm) { $fail += ("device not on the file's memory: mem=" + $info2.mem + " expected " + $fm) }
if ($info2.mute) { $fail += "device is muted" }

@{ captured = (Get-Date).ToString('o'); file = $file; memoryIndex = $fm; jumpTx = $jumpHex
   eqWrites = $eqLog; passbandWrites = $pbLog
   outputWrite = @{ enableMask = $enable; gains = $outCheck }
   preInfo = $info; postInfo = $info2
   verdict = $(if ($fail.Count -eq 0) { 'ALL VERIFIED' } else { "FAILED: " + $fail.Count })
   failures = $fail } |
    ConvertTo-Json -Depth 6 | Set-Content "$root\evidence\load-$stamp.json" -Encoding UTF8

"" | Tee-Object -FilePath $logPath -Append | Out-Null
if ($fail.Count -eq 0) {
    & $say ("LOAD COMPLETE - ALL VERIFIED against file: EQ 248x3, crossovers 8x(2 scalars + 40 coeffs), output 8x(gain+delay); memory " + ($fm + 1) + "; master " + $info2.mainDb + "; groups [" + ($info2.groupDb -join ',') + "] untouched")
} else {
    & $say ("LOAD FINISHED WITH " + $fail.Count + " FAILURE(S):")
    $fail | ForEach-Object { & $say ("  " + $_) }
}
"log: $logPath"
