# Full-bank .idsp load: transfers ALL FOUR memories of the file to the device
# (memIndex 0-3), then leaves the device on a chosen memory.
#
# Why this exists: load-tune-jump.ps1 transfers only ONE memory (the file's own saved
# one), which leaves the other three device slots holding whatever was there before.
# Verified 2026-10-18: the DSP accepts writes addressed to a memory that is NOT the
# active cell, so the whole bank can be written without pre-jumping into each memory.
# BUT a write also SELECTS the memory it targets (verified - see protocol section 25),
# so the audio steps through the memories as the bank is written and the device ends on
# the last memory written. The trailing jump to -Play is what settles it.
#
# Usage:
#   powershell -File load-tune-bank.ps1 -File <path.idsp> [-Play <memIndex>]
#   -Play defaults to the file's own RunStatus.mainMemory.
#   Memory indices are 0-based; the app shows them as 1-4 ("memory 2" = -Play 1).
param(
    [Parameter(Mandatory = $true)][string]$File,
    [int]$Play = -1
)
$ErrorActionPreference = 'Stop'
$base = 'http://localhost:8585'
$root = 'C:\Users\sdotm\OneDrive\Documents\Default Project\ds18-bridge'
$stamp = (Get-Date).ToString('yyyy-MM-dd-HHmm')
$logPath = "$root\evidence\bank-load-$stamp.log"
$say = { param($m) $m | Tee-Object -FilePath $logPath -Append }

function Post($path, $obj) {
    (Invoke-WebRequest "$base$path" -Method POST -Body ($obj | ConvertTo-Json -Depth 8) `
        -ContentType 'application/json' -UseBasicParsing -TimeoutSec 25).Content | ConvertFrom-Json
}
function GetJson($path) {
    (Invoke-WebRequest "$base$path" -UseBasicParsing -TimeoutSec 20).Content | ConvertFrom-Json
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
function Bands($output, $path) {
    $a = @()
    for ($i = 0; $i -lt 31; $i++) {
        $e = $output[$path].eq[$i]
        $a += , @{ fc = [int]$e.Fc; gain = [double]$e.Gain; q = [double]$e.Q; enable = $true }
    }
    $a
}
function PassBody($output, $path, $dry) {
    $f0 = $output[$path].passBand[0]; $f1 = $output[$path].passBand[1]
    @{ mem = $null; path = $path; dryRun = $dry
       low  = @{ fc = [int]$f0.Fc; slope = [int]$f0.Slope; filterType = [int]$f0.filterType; hiLow = [int]$f0.hiLowType; butt = [int]$f0.butt_Type; enable = [bool]([int]$f0.Enable) }
       high = @{ fc = [int]$f1.Fc; slope = [int]$f1.Slope; filterType = [int]$f1.filterType; hiLow = [int]$f1.hiLowType; butt = [int]$f1.butt_Type; enable = [bool]([int]$f1.Enable) } }
}

"=== bank load start $(Get-Date -Format o) ===" | Set-Content $logPath -Encoding UTF8

# ---- read the file --------------------------------------------------------
[xml]$x = Get-Content $File -Raw
$mems = @($x.DP12_Param.SelectNodes('memory'))
$fileMem = [int]$x.DP12_Param.RunStatus.mainMemory
if ($mems.Count -lt 1) { throw "no memories in $File" }
if ($Play -lt 0) { $Play = $fileMem }
if ($Play -ge $mems.Count) { throw "play memory $Play out of range (file has $($mems.Count))" }

& $say "file: $File"
& $say ("memories in file: " + $mems.Count + " ; file's own live memory: " + $fileMem + " (shown as " + ($fileMem + 1) + ")")
& $say ("will transfer memories 0-" + ($mems.Count - 1) + " ; will leave the device on memory " + ($Play + 1))

# ---- pre-flight ----------------------------------------------------------
$info = GetJson '/api/info'
if (-not $info.open) { throw 'link not open - aborting before any write' }
& $say ("pre: open=" + $info.open + " activeMem=" + $info.mem + " (memory " + ([int]$info.mem + 1) + ") mainDb=" + $info.mainDb + " groups=[" + ($info.groupDb -join ',') + "] src=" + $info.source)

# ---- snapshot every slot we are about to overwrite -----------------------
# EQ + crossovers are readable and therefore recoverable; cmd 11 levels are read back
# later for the play memory only. The tune files on disk are the real restore path.
$pre = @()
for ($m = 0; $m -lt $mems.Count; $m++) {
    $slot = @()
    for ($p = 0; $p -lt 8; $p++) {
        $r = Post '/api/eqread' @{ mem = $m; path = $p }
        if ($r.ok -eq $false) { throw ("snapshot read failed mem=$m path=$p : " + $r.error) }
        $pb = $r.passband
        $slot += , @{ p = $p; byPass = $r.byPass
            hpFc = $pb.lowFc; hpSlope = $pb.lowSlope; hpFilter = $pb.lowFilterType
            lpFc = $pb.highFc; lpSlope = $pb.highSlope; lpFilter = $pb.highFilterType
            bands = @($r.bands | ForEach-Object { @{ fc = $_.fc; gain = $_.gain; q = $_.q } }) }
    }
    $pre += , @{ memory = $m; outputs = $slot }
    & $say ("snapshot: memory " + ($m + 1) + " pre-state captured")
}
@{ captured = (Get-Date).ToString('o'); file = $File; slots = $pre } |
    ConvertTo-Json -Depth 9 | Set-Content "$root\evidence\bank-before-$stamp.json" -Encoding UTF8
& $say "pre-state -> evidence\bank-before-$stamp.json"

# ---- write pass: every memory, no jumps ----------------------------------
$writeLog = @()
for ($m = 0; $m -lt $mems.Count; $m++) {
    $fo = $mems[$m].output
    $eqAcks = 0; $pbAcks = 0
    for ($p = 0; $p -lt 8; $p++) {
        $r = Post '/api/eq' @{ mem = $m; path = $p; byPass = $false; bands = (Bands $fo $p) }
        if ($r.acked) { $eqAcks++ }
        Start-Sleep -Milliseconds 120
    }
    for ($p = 0; $p -lt 8; $p++) {
        $b = PassBody $fo $p $false
        $b.mem = $m
        $r = Post '/api/passband' $b
        if ($r.acked) { $pbAcks++ }
    }
    $gain = @(); $delay = @(); $mute = @(); $revert = @()
    for ($i = 0; $i -lt 16; $i++) { $gain += 0.0; $delay += 0.0; $mute += $false; $revert += $false }
    for ($p = 0; $p -lt 8; $p++) { $gain[$p] = [double]$fo[$p].gain; $delay[$p] = [double]$fo[$p].delay }
    $ro = Post '/api/output' @{ mem = $m; mute = $mute; revert = $revert; gainDb = $gain; delayMs = $delay }
    $writeLog += ("mem" + ($m + 1) + " eq8:" + $eqAcks + " pb8:" + $pbAcks + " out:acked=" + $ro.acked + " mask=0x" + ('{0:X4}' -f $ro.enableMask) + " tx=" + $ro.transmitted)
    & $say ("written memory " + ($m + 1) + "  (eq acked " + $eqAcks + "/8, crossover acked " + $pbAcks + "/8, output acked=" + $ro.acked + ", enableMask=0x" + ('{0:X4}' -f $ro.enableMask) + ")")
}

# ---- jump to the play memory (the only audible step) ---------------------
$jumpHex = ''
$infoNow = GetJson '/api/info'
if ([int]$infoNow.mem -ne $Play) {
    $groups = @(); foreach ($g in $infoNow.groupDb) { $groups += [double]$g }
    $jr = Post '/api/master' @{ mainDb = [double]$infoNow.mainDb; mute = [bool]$infoNow.mute; groups = $groups; mem = $Play; source = [int]$infoNow.source }
    if (-not $jr.ok) { throw ("memory jump failed: " + $jr.error) }
    $jumpHex = $jr.txHex
    Start-Sleep -Milliseconds 500
}
$infoAfter = GetJson '/api/info'
& $say ("device now on memory " + ([int]$infoAfter.mem + 1) + " (memIndex " + $infoAfter.mem + ") jumpTx=" + $jumpHex)

# ---- verify every memory by readback -------------------------------------
$fail = @()
$vd = @()
for ($m = 0; $m -lt $mems.Count; $m++) {
    $fo = $mems[$m].output
    $bad = 0; $bandsChecked = 0; $bandsOk = 0; $coefOk = 0
    for ($p = 0; $p -lt 8; $p++) {
        $r = Post '/api/eqread' @{ mem = $m; path = $p }
        if ($r.byPass) { $bad++; $fail += ("mem" + ($m + 1) + " p" + $p + " byPass=true (file has none)") }
        for ($i = 0; $i -lt 31; $i++) {
            $e = $fo[$p].eq[$i]; $b = $r.bands[$i]
            $bandsChecked++
            $okBand = $true
            if ([int]$b.fc -ne [int]$e.Fc) { $bad++; $okBand = $false; $fail += ("mem" + ($m + 1) + " p" + $p + " band" + $i + " fc " + $b.fc + " != file " + $e.Fc) }
            if ([math]::Abs([double]$b.gain - [double]$e.Gain) -gt 0.061) { $bad++; $okBand = $false; $fail += ("mem" + ($m + 1) + " p" + $p + " band" + $i + " gain " + $b.gain + " != file " + $e.Gain) }
            if ([math]::Abs([double]$b.q - [double]$e.Q) -gt 0.051) { $bad++; $okBand = $false; $fail += ("mem" + ($m + 1) + " p" + $p + " band" + $i + " q " + $b.q + " != file " + $e.Q) }
            if ($okBand) { $bandsOk++ }
        }
        $rPass = $r.passband
        $f0 = $fo[$p].passBand[0]; $f1 = $fo[$p].passBand[1]
        if ([int]$rPass.lowFc -ne [int]$f0.Fc -or [int]$rPass.highFc -ne [int]$f1.Fc -or
            [int]$rPass.lowSlope -ne [int]$f0.Slope -or [int]$rPass.highSlope -ne [int]$f1.Slope -or
            [int]$rPass.lowFilterType -ne [int]$f0.filterType -or [int]$rPass.highFilterType -ne [int]$f1.filterType) {
            $bad++
            $fail += ("mem" + ($m + 1) + " p" + $p + " passband scalars != file (dev L" + $rPass.lowFc + "/sl" + $rPass.lowSlope + " H" + $rPass.highFc + "/sl" + $rPass.highSlope + ")")
        }
        $b2 = PassBody $fo $p $true; $b2.mem = $m
        $dry = Post '/api/passband' $b2
        $dc = $dry.pbHex
        $dLow = $dc.Substring(0, 4) + $dc.Substring(8, 2) + $dc.Substring(12, 2) + $dc.Substring(16, 160)
        $dHigh = $dc.Substring(4, 4) + $dc.Substring(10, 2) + $dc.Substring(14, 2) + $dc.Substring(176, 160)
        $rb = PbCanon $rPass
        if ($rb[0] -eq $dLow -and $rb[1] -eq $dHigh) { $coefOk++ }
        else {
            $bad++
            $fail += ("mem" + ($m + 1) + " p" + $p + " crossover coeffs != HlpGetCoff output")
        }
    }
    $vd += , @{ memory = $m; eqBandsOk = $bandsOk; eqBandsChecked = $bandsChecked; crossoverCoeffsOk = $coefOk; problems = $bad }
    & $say ("verify memory " + ($m + 1) + ": eq " + $bandsOk + "/" + $bandsChecked + " bands, crossover coeffs " + $coefOk + "/8 paths, problems=" + $bad)
}

# ---- verify the output block for the memory that is playing --------------
$cfg = GetJson '/api/dspcfg'
$body = HexToBytes $cfg.bodyHex
$enable = [BitConverter]::ToUInt16($body, 1760)
$revertM = [BitConverter]::ToUInt16($body, 1762)
$foPlay = $mems[$Play].output
$outCheck = @()
if ($enable -ne 0x00FF) { $fail += ("play memory enable mask 0x" + ('{0:X4}' -f $enable) + " != 0x00FF") }
if ($revertM -ne 0) { $fail += ("play memory revert mask " + $revertM + " != 0") }
for ($i = 0; $i -lt 8; $i++) {
    $gu = [BitConverter]::ToUInt16($body, 1764 + 2 * $i)
    $du = [BitConverter]::ToUInt16($body, 1796 + 2 * $i)
    $gdb = -1.0 * $gu / 10.0
    $dms = $du / 100.0
    if ([math]::Abs($gdb - [double]$foPlay[$i].gain) -gt 0.051) { $fail += ("play out" + $i + " gain " + $gdb + " != file " + $foPlay[$i].gain) }
    # delays travel in 0.01 ms units, so a file value like 4.1 truncates to 4.09
    if ([math]::Abs($dms - [double]$foPlay[$i].delay) -gt 0.011) { $fail += ("play out" + $i + " delay " + $dms + " != file " + $foPlay[$i].delay) }
    $outCheck += ("g" + $i + "=" + $gdb + "dB d" + $i + "=" + $dms + "ms")
}
& $say ("verify play output (memory " + ($Play + 1) + "): enable=0x" + ('{0:X4}' -f $enable) + " " + ($outCheck -join ' '))

# ---- master / groups must not have moved ---------------------------------
if ([math]::Abs([double]$infoAfter.mainDb - [double]$info.mainDb) -gt 0.01) { $fail += ("master moved: " + $info.mainDb + " -> " + $infoAfter.mainDb) }
if (($infoAfter.groupDb -join ',') -ne ($info.groupDb -join ',')) { $fail += ("groups moved: " + ($info.groupDb -join ',') + " -> " + ($infoAfter.groupDb -join ',')) }
if ([int]$infoAfter.mem -ne $Play) { $fail += ("device not on the requested memory: mem=" + $infoAfter.mem + " expected " + $Play) }
if ($infoAfter.mute) { $fail += "device is muted" }

@{ captured = (Get-Date).ToString('o'); file = $File; play = $Play; jumpTx = $jumpHex
   writes = $writeLog; verify = $vd; playOutput = $outCheck; enableMask = $enable
   preInfo = @{ mem = $info.mem; mainDb = $info.mainDb; groups = $info.groupDb }
   postInfo = @{ mem = $infoAfter.mem; mainDb = $infoAfter.mainDb; groups = $infoAfter.groupDb }
   verdict = $(if ($fail.Count -eq 0) { 'PASS' } else { 'FAILED: ' + $fail.Count })
   failures = $fail } |
    ConvertTo-Json -Depth 9 | Set-Content "$root\evidence\bank-load-$stamp.json" -Encoding UTF8

& $say ("evidence -> evidence\bank-load-$stamp.json")
if ($fail.Count -eq 0) {
    & $say "VERDICT: PASS - all $($mems.Count) memories match the file; device on memory $($Play + 1)"
} else {
    & $say ("VERDICT: FAILED: " + $fail.Count + " - see failures in the evidence json")
    & $say ("  " + ($fail -join "`n  "))
}
