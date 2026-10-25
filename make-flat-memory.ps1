# Build a sweep reference memory inside a copy of a .idsp file.
#
# Why: a measurement must not be shaped by the tune. The cheap, safe way to get "no tune"
# is a whole memory that is the tune's levels with its EQ zeroed - same output gains and
# delays, nothing else carried over. Then the app only has to SELECT that memory (one
# verified cmd 12) instead of rewriting seventeen EQ blocks on the fly.
#
# Crossovers in the reference are PROTECTION ONLY, not the tune's split. Owner, 2026-10-19:
# "the tweeters are the only ones im concerned with, the 3.5 are good after 100hz", and the
# 8" and the 6.5" "can do full range". So:
#
#   out 0-3  tweeters  2500 Hz high-pass kept   (the only driver that can be hurt)
#   out 4-5  3.5"      100 Hz high-pass         (owner: good from 100 Hz)
#   out 6    8"        no high-pass, no low-pass
#   out 7    6.5"      no high-pass, no low-pass
#   out 8-16           untouched (master chain / unpopulated)
#
# Usage:
#   powershell -File make-flat-memory.ps1 [-File <src.idsp>] [-From 0] [-To 0] [-Out <dst.idsp>] [-Force]
#   -From / -To are 0-based memory indices. Since 2026-10-22 every library tune is ONE
#   tune per file, so the default (and the only valid pair for them) is 0/0: flatten the
#   file's single block in the copy. 4-block banks (evidence\*.idsp.4mem) still take the
#   historical -From 1 -To 3 ("memory 2 -> memory 4").
param(
    [string]$File = 'C:\Users\sdotm\OneDrive\Documents\Default Project\ds18-bridge\web\bor8f3.idsp',
    [int]$From = 0,
    [int]$To = 0,
    [string]$Out = '',
    [switch]$Force
)
$ErrorActionPreference = 'Stop'

if ($Out -eq '') {
    # NOT the served web folder: every .idsp in there is offered as a tune to import, and this
    # file is a reference memory, not something to apply over the tune.
    $dir = Join-Path $PSScriptRoot 'banks'
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
    $base = [IO.Path]::GetFileNameWithoutExtension($File)
    $Out = Join-Path $dir ("$base-flat" + ($To + 1) + ".idsp")
}
if (-not (Test-Path $File)) { throw "source not found: $File" }
if ((Resolve-Path $File).Path -eq $Out -and -not $Force) {
    throw 'refusing to overwrite the source file'
}
if ((Test-Path $Out) -and -not $Force) {
    throw "output exists: $Out  (pass -Force to replace it)"
}

# high-pass / low-pass for the reference, by output index. 0 = filter switched off.
$sweepPass = @{
    0 = @{ hp = 2500 }; 1 = @{ hp = 2500 }; 2 = @{ hp = 2500 }; 3 = @{ hp = 2500 }
    4 = @{ hp = 100  }; 5 = @{ hp = 100  }
    6 = @{ hp = 0    }; 7 = @{ hp = 0    }
    # 8..16: left exactly as the tune has them
}

[xml]$x = Get-Content $File -Raw
$mems = @($x.DP12_Param.SelectNodes('memory'))
if ($From -eq $To -and $mems.Count -gt 1) {
    throw '-From and -To must differ on a multi-memory bank (only single-tune files flatten in place)'
}
if ($From -lt 0 -or $From -ge $mems.Count) { throw "source memory $From out of range (file has $($mems.Count))" }
if ($To -lt 0 -or $To -ge $mems.Count) { throw "target memory $To out of range (file has $($mems.Count))" }

function MemStats($mem) {
    $nz = 0; $max = 0.0; $outs = 0
    foreach ($o in @($mem.output)) {
        $outs++
        foreach ($e in @($o.eq)) {
            $g = [math]::Abs([double]$e.Gain)
            if ($g -gt 0.05) { $nz++ }
            if ($g -gt $max) { $max = $g }
        }
    }
    [pscustomobject]@{ outputs = $outs; nonzeroBands = $nz; maxGain = [math]::Round($max, 1) }
}

$srcStats = MemStats $mems[$From]
$oldStats = MemStats $mems[$To]

# Copy the tune memory into the target slot.
$clone = $mems[$From].CloneNode($true)
if ($clone.Attributes -and $clone.Attributes['index']) { $clone.Attributes['index'].Value = [string]$To }

# Zero only the gain: the bands stay where they are so the curve is flat, not reshaped.
foreach ($o in @($clone.output)) {
    foreach ($e in @($o.eq)) { $e.Gain = '0' }
}

# Protection only: keep the driver-safe high-passes, switch every tuning filter off.
foreach ($o in @($clone.output)) {
    $pi = [int]$o.index
    if (-not $sweepPass.ContainsKey($pi)) { continue }
    $hp = $o.passBand[0]; $lp = $o.passBand[1]
    $want = [int]$sweepPass[$pi].hp
    if ($want -gt 0) {
        $hp.Fc = [string]$want
        $hp.Enable = '1'
    } else {
        $hp.Enable = '0'
    }
    $lp.Enable = '0'
}

[void]$x.DP12_Param.ReplaceChild($clone, $mems[$To])
$x.Save($Out)

# ---- re-read what was written and prove it ------------------------------------------------
[xml]$y = Get-Content $Out -Raw
$ym = @($y.DP12_Param.SelectNodes('memory'))
if ($ym.Count -ne $mems.Count) { throw "memory count changed: $($ym.Count)" }
$newStats = MemStats $ym[$To]
if ($newStats.nonzeroBands -ne 0) { throw "target still has $($newStats.nonzeroBands) non-zero bands" }

$bad = 0
foreach ($b in @($ym[$To].output)) {
    $p = [int]$b.index
    $a = $ym[$From].output | Where-Object { [int]$_.index -eq $p } | Select-Object -First 1
    if (-not $a) { $bad++; "path $p is missing from the tune memory"; continue }

    # levels always come from the tune
    if ([double]$a.gain -ne [double]$b.gain) { $bad++; "path $p gain differs: $($a.gain) -> $($b.gain)" }
    if ([double]$a.delay -ne [double]$b.delay) { $bad++; "path $p delay differs: $($a.delay) -> $($b.delay)" }

    $hp = $b.passBand[0]; $lp = $b.passBand[1]
    if ($sweepPass.ContainsKey($p)) {
        $want = [int]$sweepPass[$p].hp
        $wantEn = if ($want -gt 0) { 1 } else { 0 }
        if ([int]$hp.Enable -ne $wantEn) { $bad++; "path $p high-pass enable=$($hp.Enable), wanted $wantEn" }
        if ($want -gt 0 -and [int]$hp.Fc -ne $want) { $bad++; "path $p high-pass $($hp.Fc) Hz, wanted $want" }
        if ([int]$lp.Enable -ne 0) { $bad++; "path $p low-pass is still enabled" }
    } elseif ($a.passBand[0].OuterXml -ne $b.passBand[0].OuterXml -or
              $a.passBand[1].OuterXml -ne $b.passBand[1].OuterXml) {
        $bad++; "path $p passband differs from the tune (only 0-7 are profiled)"
    }
}
if ($bad -ne 0) { throw "reference memory is not what was asked for: $bad problem(s)" }

# The tune itself must be untouched. Multi-memory mode: the output keeps [From] intact
# (a different slot was replaced). In-place mode (single-tune file): the output's only
# block IS the flat copy, so the guarantee that matters is the SOURCE FILE on disk -
# re-read it and compare against the stats taken before the write.
$srcAfter = $null
if ($From -eq $To) {
    [xml]$z = Get-Content $File -Raw
    $zm = @($z.DP12_Param.SelectNodes('memory'))
    $srcAfter = MemStats $zm[$From]
} else {
    $srcAfter = MemStats $ym[$From]
}
if ($srcAfter.nonzeroBands -ne $srcStats.nonzeroBands -or $srcAfter.maxGain -ne $srcStats.maxGain) {
    throw "source memory changed: $($srcStats.nonzeroBands)/$($srcStats.maxGain) -> $($srcAfter.nonzeroBands)/$($srcAfter.maxGain)"
}

function PassLine($mem, $idx) {
    $o = $mem.output | Where-Object { [int]$_.index -eq $idx } | Select-Object -First 1
    $hp = $o.passBand[0]; $lp = $o.passBand[1]
    $h = if ([int]$hp.Enable -ne 0) { "$($hp.Fc) Hz high-pass" } else { 'no high-pass' }
    $l = if ([int]$lp.Enable -ne 0) { "$($lp.Fc) Hz low-pass" } else { 'no low-pass' }
    "$h, $l"
}

"source : $File"
"out    : $Out"
"copied : memory $($From + 1) -> memory $($To + 1)   (shown 1-4)"
"tune   : $($srcStats.nonzeroBands) non-zero bands, max $($srcStats.maxGain) dB  (kept)"
"old    : memory $($To + 1) had $($oldStats.nonzeroBands) non-zero bands, max $($oldStats.maxGain) dB  (replaced)"
"flat   : memory $($To + 1) has $($newStats.nonzeroBands) non-zero bands; gains and delays from the tune"
"pass   : tweeters   " + (PassLine $ym[$To] 0)
"pass   : 3.5 in     " + (PassLine $ym[$To] 4)
"pass   : 8 in       " + (PassLine $ym[$To] 6)
"pass   : 6.5 in     " + (PassLine $ym[$To] 7)
"VERDICT: PASS"
