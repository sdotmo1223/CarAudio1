# flatten-tune.ps1 - take the EQ out of the playing memory, keep the crossovers.
#
# The measurement gate in the page refuses a run while any path in the playing memory still has a
# non-zero EQ gain, because a response measured through the tune is a measurement of the tune.
# This is the tool that clears that gate: it reads all seventeen EQ cells, writes the snapshot to
# disk, then writes zero gain to every cell that carried a band. The passband is never touched -
# crossovers, slopes and the enable bit are read and reinstated untouched by /api/eq, which only
# carries the EQ group.
#
# Zero gain on all 31 bands is an exact identity at every frequency, which is what makes this safe
# and reversible: -Restore writes the snapshot back band for band.
#
#   powershell -File flatten-tune.ps1                 # snapshot + flatten the playing memory
#   powershell -File flatten-tune.ps1 -WhatIf          # show what it would write, change nothing
#   powershell -File flatten-tune.ps1 -Restore <json>  # put a snapshot back
#
[CmdletBinding()]
param(
    [int]$Mem = -1,
    [string]$Restore = '',
    [switch]$WhatIf,
    [string]$Bridge = 'http://127.0.0.1:8585',
    [string]$Out = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$cells = 17          # eight outputs, nine further EQ cells; all of them are read

function Post([string]$path, $body) {
    $json = $body | ConvertTo-Json -Depth 8 -Compress
    return Invoke-RestMethod ($Bridge + $path) -Method Post -Body $json -ContentType 'application/json' -TimeoutSec 30
}

function Read-Cell([int]$mem, [int]$p) {
    return (Post '/api/eqread' @{ mem = $mem; path = $p })
}

function Cell-Is-Tuned($r) {
    if (-not $r.ok) { return $false }
    if ($r.byPass) { return $false }
    foreach ($b in $r.bands) { if ([Math]::Abs([double]$b.gain) -gt 0.05) { return $true } }
    return $false
}

# ---------------------------------------------------------------- state
$st = Invoke-RestMethod ($Bridge + '/api/status') -TimeoutSec 15
if (-not $st.ok) { Write-Host 'the bridge reports the DSP is not connected'; exit 1 }
if ($Mem -lt 0) { $Mem = [int]$st.mem }
Write-Host ("memory   : {0} (display {1})  main {2:N1} dB" -f $Mem, ($Mem + 1), [double]$st.mainDb)

# ---------------------------------------------------------------- restore
if ($Restore) {
    if (-not (Test-Path $Restore)) { Write-Host ("no such snapshot: " + $Restore); exit 1 }
    $snap = Get-Content -Raw $Restore | ConvertFrom-Json
    $snapMem = [int]$snap.mem
    if ($snapMem -ne $Mem) {
        Write-Host ("snapshot is memory {0} but the device is on memory {1} - refusing" -f ($snapMem + 1), ($Mem + 1))
        exit 1
    }
    $done = 0; $bad = 0
    foreach ($entry in @($snap.paths)) {
        $p = [int]$entry.path
        if ($entry.state -eq 'flat') { continue }
        if ($WhatIf) { Write-Host ("  would restore path {0} ({1} bands)" -f $p, @($entry.bands).Count); continue }
        $bands = @($entry.bands | ForEach-Object {
            @{ fc = [int]$_.fc; gain = [double]$_.gain; q = [double]$_.q; type = 0; enable = $true }
        })
        $r = Post '/api/eq' @{ mem = $Mem; path = $p; byPass = [bool]$entry.byPass; bands = $bands }
        if (-not $r.ok) { Write-Host ("  path {0} write failed: {1}" -f $p, $r.error); $bad++; continue }
        $chk = Read-Cell $Mem $p
        $worst = 0.0
        if ($chk.ok) {
            for ($i = 0; $i -lt 31; $i++) {
                $d = [Math]::Abs([double]$chk.bands[$i].gain - [double]$entry.bands[$i].gain)
                if ($d -gt $worst) { $worst = $d }
            }
        }
        if ($worst -gt 0.05) { Write-Host ("  path {0} read back off by {1:N2} dB" -f $p, $worst); $bad++; continue }
        Write-Host ("  path {0} restored, read back within {1:N2} dB" -f $p, $worst)
        $done++
    }
    if (-not $WhatIf) { Write-Host ("restored : {0} path(s), {1} failed" -f $done, $bad) }
    if ($bad) { exit 1 }
    exit 0
}

# ---------------------------------------------------------------- read all cells
$state = @()
for ($p = 0; $p -lt $cells; $p++) {
    $r = Read-Cell $Mem $p
    if (-not $r.ok) { Write-Host ("read failed on path {0}: {1} - nothing written" -f $p, $r.error); exit 1 }
    $bands = @()
    foreach ($b in $r.bands) { $bands += @{ fc = [int]$b.fc; gain = [double]$b.gain; q = [double]$b.q } }
    $state += [pscustomobject]@{
        path = $p; byPass = [bool]$r.byPass
        state = $(if ($r.byPass) { 'bypassed' } elseif (Cell-Is-Tuned $r) { 'tuned' } else { 'flat' })
        bands = $bands
    }
}

$tuned = @($state | Where-Object { $_.state -eq 'tuned' })
Write-Host ("cells    : {0} read, {1} tuned, {2} flat, {3} bypassed" -f $state.Count, $tuned.Count,
    @($state | Where-Object { $_.state -eq 'flat' }).Count, @($state | Where-Object { $_.state -eq 'bypassed' }).Count)

if (-not $tuned.Count) {
    Write-Host 'nothing to do: every EQ cell is already neutral in this memory'
    exit 0
}

# ---------------------------------------------------------------- snapshot first, always
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
if (-not $Out) {
    $dir = Join-Path $root 'evidence'
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
    $Out = Join-Path $dir ("eq-snapshot-mem{0}-{1}.json" -f ($Mem + 1), $stamp)
}
$snapshot = [pscustomobject]@{
    kind = 'eq-snapshot'; created = (Get-Date).ToString('o'); mem = $Mem
    mainDb = [double]$st.mainDb; paths = $state
}
if (-not $WhatIf) {
    $snapshot | ConvertTo-Json -Depth 8 | Set-Content -Path $Out -Encoding UTF8
    Write-Host ("snapshot : {0}" -f $Out)
}

# ---------------------------------------------------------------- write zero gain
$done = 0; $bad = 0
foreach ($e in $tuned) {
    if ($WhatIf) { Write-Host ("  would flatten path {0}" -f $e.path); continue }
    $bands = @($e.bands | ForEach-Object {
        @{ fc = [int]$_.fc; gain = 0.0; q = [double]$_.q; type = 0; enable = $true }
    })
    $r = Post '/api/eq' @{ mem = $Mem; path = $e.path; byPass = $false; bands = $bands }
    if (-not $r.ok) { Write-Host ("  path {0} write failed: {1}" -f $e.path, $r.error); $bad++; continue }
    # The write frame is fire-and-forget on this unit: acked comes back false even when the device
    # applied it (measured 2026-10-20 - the very next read showed zero gain on every path). The
    # read-back below is the verdict, exactly like cmd 13. An ACK is reported, never required.
    $note = $(if ($r.acked) { '' } else { ' (no ACK, verifying by read-back)' })

    $chk = Read-Cell $Mem $e.path
    if (-not $chk.ok) { Write-Host ("  path {0} read back failed: {1}" -f $e.path, $chk.error); $bad++; continue }
    $worst = 0.0
    foreach ($b in $chk.bands) { $d = [Math]::Abs([double]$b.gain); if ($d -gt $worst) { $worst = $d } }
    if ($worst -gt 0.05) { Write-Host ("  path {0} still carries {1:N2} dB" -f $e.path, $worst); $bad++; continue }
    Write-Host ("  path {0} flat, read back {1:N2} dB{2}" -f $e.path, $worst, $note)
    $done++
}

if ($WhatIf) { Write-Host 'what-if  : nothing written'; exit 0 }

# ---------------------------------------------------------------- prove it across every cell
$left = 0
for ($p = 0; $p -lt $cells; $p++) {
    $r = Read-Cell $Mem $p
    if (-not $r.ok) { $left++; continue }
    if (Cell-Is-Tuned $r) { Write-Host ("  path {0} STILL TUNED" -f $p); $left++ }
}
Write-Host ("flattened: {0} path(s), {1} failed, {2} still tuned" -f $done, $bad, $left)
if ($bad -or $left) { exit 1 }
Write-Host 'done     : memory is crossovers only, no EQ in circuit'
