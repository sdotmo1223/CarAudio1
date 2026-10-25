# DreTunes launcher.
# Starts the bridge if it is not already serving, optionally wakes the RTA daemon for the
# RTA tab, then opens the control UI in its own app window (no tabs/address bar) using a
# dedicated browser profile so it behaves like a standalone program.
#
# Run directly (console visible) or via DreTunes.vbs (silent).
$ErrorActionPreference = 'Stop'

$root        = $PSScriptRoot
$bridgeExe   = Join-Path $root 'bin\ds18-bridge.exe'
$rtaExe      = Join-Path $root 'bin\rta.exe'
$port        = 8585
$rtaPort     = 8586
$appData     = Join-Path $env:LOCALAPPDATA 'DreTunes'
$profileDir  = Join-Path $appData 'browser-profile'
$logPath     = Join-Path $appData 'launch.log'

New-Item -ItemType Directory -Force -Path $appData | Out-Null
function Write-Log([string]$msg) {
    $line = "{0}  {1}" -f (Get-Date -Format 'HH:mm:ss'), $msg
    Add-Content -Path $logPath -Value $line -Encoding UTF8
    Write-Host $line
}
# Fast liveness probe. Get-NetTCPConnection takes seconds to enumerate; a bounded HTTP
# request answers in milliseconds, so the app window opens instantly on re-launch.
function Test-Http([string]$url, [int]$ms) {
    try {
        $req = [System.Net.WebRequest]::Create($url)
        $req.Method = 'GET'
        $req.Timeout = $ms
        $resp = $req.GetResponse()
        $resp.Close()
        return $true
    } catch { return $false }
}
function Wait-Http([string]$url, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        try { $null = Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 2; return $true } catch { Start-Sleep -Milliseconds 500 }
    }
    return $false
}
function Show-Fatal([string]$msg) {
    Write-Log "FATAL: $msg"
    try {
        $sh = New-Object -ComObject WScript.Shell
        $null = $sh.Popup("DreTunes could not start.`n`n$msg`n`nLog: $logPath", 0, 'DreTunes', 16)
    } catch { }
    exit 1
}

Write-Log "--- launch ---"

# 1. the bridge (required: it owns the USB link and serves the UI)
if (Test-Http "http://127.0.0.1:$port/api/info" 900) {
    Write-Log "bridge already running on $port"
} else {
    if (-not (Test-Path $bridgeExe)) { Show-Fatal "bridge not found at $bridgeExe" }
    Write-Log "starting bridge"
    Start-Process -FilePath $bridgeExe -ArgumentList '--serve', "--port", "$port" `
        -WorkingDirectory $root -WindowStyle Hidden
    if (-not (Wait-Http "http://127.0.0.1:$port/api/info" 25)) {
        Show-Fatal "the bridge did not answer on port $port within 25 s."
    }
    Write-Log "bridge ready"
}

# 2. the RTA daemon (optional: only feeds the RTA tab; the app works without it)
if (Test-Http "http://127.0.0.1:$rtaPort/rta.json" 900) {
    Write-Log "rta already running on $rtaPort"
} elseif (Test-Path $rtaExe) {
    try {
        Start-Process -FilePath $rtaExe -WorkingDirectory (Split-Path $rtaExe) -WindowStyle Hidden
        if (Wait-Http "http://127.0.0.1:$rtaPort/rta.json" 12) { Write-Log "rta capturing" }
        else { Write-Log "rta did not come up (RTA tab will report no meter)" }
    } catch { Write-Log "rta not started: $_" }
}

# 3. the app window - prefer a Chromium browser in app mode for a standalone window
$url = "http://localhost:$port/"
$browsers = @(
    "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
    "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
    "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe",
    "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
)
$browser = $browsers | Where-Object { Test-Path $_ } | Select-Object -First 1

# Window size: this is a laptop app, not a phone app. The old 440x900 opened the phone layout
# in a tall narrow window, so the whole UI read as small and lost next to a maximized screen.
# Fit a desktop-sized window to the display, and fall back to 1280x860 if the screen cannot be
# measured. The UI switches to its desktop layout above 820px wide, with the Tune tab going
# three-column past 1240px.
$winW = 1280; $winH = 860
try {
    Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
    $wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $winW = [Math]::Min(1280, [Math]::Max(900, $wa.Width - 40))
    $winH = [Math]::Min(900,  [Math]::Max(620, $wa.Height - 40))
} catch { Write-Log "screen size unavailable, using ${winW}x${winH}" }
Write-Log "app window ${winW}x${winH}"

if ($browser) {
    Write-Log ("opening app window via " + (Split-Path $browser -Leaf))
    Start-Process -FilePath $browser -ArgumentList `
        "--app=$url", "--user-data-dir=$profileDir", "--no-first-run", `
        "--no-default-browser-check", "--window-size=$winW,$winH"
} else {
    Write-Log "no Chromium browser found - opening in the default browser"
    Start-Process $url
}
Write-Log "done"
