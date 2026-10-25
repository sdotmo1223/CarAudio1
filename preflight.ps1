# preflight.ps1 - get the rig ready after plugging in, with one command.
#
# Two things break on every USB re-plug and neither announces itself:
#   1. the bridge keeps its handle to the DSP, which is now a dead one - /api/status says ok=false
#   2. rta.exe keeps a device *index*, and the list reorders, so it silently captures the
#      laptop's Realtek array (which reads -93 dBFS and still claims "capturing")
# This waits for the hardware, reconnects the DSP, re-points the RTA at the UMIK-1, and then
# proves it with doctor.ps1. Exit 0 means genuinely ready to measure.
param(
    [int]$WaitSeconds = 90
)

$ErrorActionPreference = 'Stop'
$Bridge = 'http://127.0.0.1:8585'
$Rta    = 'http://127.0.0.1:8586'

function Say([string]$m) { Write-Output ('  ' + $m) }

# ---- 1. bridge and rta alive -------------------------------------------------
foreach ($p in @(@{ n = 'bridge'; u = "$Bridge/api/status" }, @{ n = 'rta'; u = "$Rta/rta.json" })) {
    $ok = $false
    try { Invoke-RestMethod $p.u -TimeoutSec 4 | Out-Null; $ok = $true } catch { }
    if (-not $ok) {
        Say ("$($p.n) is not answering - start it with .\DreTunes.ps1")
        exit 2
    }
    Say ("$($p.n) alive")
}

# ---- 2. DSP ------------------------------------------------------------------
$deadline = (Get-Date).AddSeconds($WaitSeconds)
$connected = $false
$reconnected = $false
while ((Get-Date) -lt $deadline) {
    $st = $null
    try { $st = Invoke-RestMethod "$Bridge/api/status" -TimeoutSec 5 } catch { }
    if ($st -and $st.ok) { $connected = $true; break }

    if (-not $reconnected) {
        try {
            Invoke-RestMethod "$Bridge/api/connect" -Method Post -Body '{}' -ContentType 'application/json' -TimeoutSec 25 | Out-Null
            $reconnected = $true
            Say 'asked the bridge to reopen the DSP'
        } catch {
            Say 'DSP not answering yet - waiting for it to be plugged in'
        }
    }
    Start-Sleep -Seconds 3
}

if (-not $connected) {
    Say ("DSP did not come up within $WaitSeconds s - plug it in and run this again")
    exit 3
}
$st = Invoke-RestMethod "$Bridge/api/status" -TimeoutSec 5
Say ("DSP connected: memory {0}, master {1} dB, mute {2}, source {3}" -f ($st.mem + 1), $st.mainDb, $st.mute, $st.sourceName)

# ---- 3. UMIK present and selected in the RTA ---------------------------------
$ff = 'C:\tools\ffmpeg\ffmpeg-master-latest-win64-gpl\bin\ffmpeg.exe'
$umikSeen = $false
while ((Get-Date) -lt $deadline) {
    $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    $listing = (& $ff -hide_banner -list_devices true -f dshow -i dummy 2>&1 | Out-String)
    $ErrorActionPreference = $old
    if ($listing -match '"([^"]*UMIK[^"]*)"\s+\(audio\)') { $umikSeen = $true; break }
    Start-Sleep -Seconds 2
}
if (-not $umikSeen) {
    Say 'UMIK-1 is not on USB - re-plug it (a hub shared with the DSP is what drops it)'
    exit 4
}
Say 'UMIK-1 is on USB'

# re-point the RTA even when the name looks right: the index is what it uses, not the name
$devices = Invoke-RestMethod "$Rta/devices" -TimeoutSec 10
$idx = -1
for ($i = 0; $i -lt $devices.devices.Count; $i++) {
    if ($devices.devices[$i].name -match 'UMIK') { $idx = $i; break }
}
if ($idx -lt 0) { Say 'the RTA does not list the UMIK-1 - restart rta.exe'; exit 5 }
Invoke-RestMethod ("$Rta/device?n=" + $idx) -TimeoutSec 20 | Out-Null
Start-Sleep -Seconds 2

$j = Invoke-RestMethod "$Rta/rta.json" -TimeoutSec 5
if (-not ($j.device -match 'UMIK')) {
    Say ("the RTA still reports '" + $j.device + "' - it must say UMIK-1 before any run")
    exit 6
}
Say ("RTA on " + $j.device + " (input " + $idx + "), level " + [math]::Round([double]$j.levelDb,1) + " dBFS")

# ---- 4. prove it ------------------------------------------------------------
Write-Output ''
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'doctor.ps1') 2>&1 |
    Select-String 'dsp |read |crossover|sweep |mic |rta |usb |disk |READY|NOT READY|FAIL|fail' |
    ForEach-Object { '  ' + $_.Line.Trim() }
