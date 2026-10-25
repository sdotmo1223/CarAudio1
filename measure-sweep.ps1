# measure-sweep.ps1 - raw sweep measurement: record the mic while the sweep plays, then
# cross-correlate to get (a) the arrival delay and (b) the transfer function.
#
# Why not the RTA meter? The meter keeps one magnitude number per band with no time in it,
# so it cannot produce an arrival time. This records the raw audio instead:
#
#   channel 0 (left)  = loopback (Stereo Mix) - the sound leaving the PC
#   channel 1 (right) = microphone            - the sound arriving at the mic
#
# Correlating the two gives the delay from "left the PC" to "reached the mic" without
# depending on when the player happened to start, which is what time alignment needs.
#
# The response is computed twice when a loopback is available:
#   <name>.json        mic / sweep   = the whole chain (DAC, amp, driver, cabin, mic)
#   <name>-timing.json mic / loopback = the same but with the source cancelled; its delay
#                                       is the one to trust for alignment
#
# Usage:
#   .\measure-sweep.ps1 -Name "driver tweeter"          # plays the sweep here
#   .\measure-sweep.ps1 -NoPlay -Seconds 14             # page plays it, script only records
#   .\measure-sweep.ps1 -Mic "Microphone (UMIK-1)"      # force a mic
#   .\measure-sweep.ps1 -NoLoopback                     # mic only (single-channel recording)
#
# The UMIK-1 is used when it is present; otherwise the default input. The loopback is
# Stereo Mix when it exists - note that Stereo Mix only sees the Realtek output, so with a
# Bluetooth output there is no loopback and only the sweep-referenced delay is available.

param(
    [string]$Name = "",
    [double]$Seconds = 14,
    [string]$Sweep = "",
    [string]$Mic = "",
    [string]$Loopback = "",
    [double]$Lead = 1.2,
    [switch]$NoPlay,
    [switch]$NoLoopback,
    [string]$From = "",
    [string]$OutDir = ""
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$ffmpeg = Join-Path $env:SystemDrive 'tools\ffmpeg\ffmpeg-master-latest-win64-gpl\bin\ffmpeg.exe'
$ffplay = Join-Path $env:SystemDrive 'tools\ffmpeg\ffmpeg-master-latest-win64-gpl\bin\ffplay.exe'
$tool = Join-Path $root 'bin\sweep-tool.exe'
if (-not (Test-Path $ffmpeg)) { throw "ffmpeg not found at $ffmpeg" }
if (-not (Test-Path $tool)) { throw "sweep-tool.exe not found - run build-sweep.ps1 first" }

if (-not $OutDir) { $OutDir = Join-Path $root 'measurements' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$tag = if ($Name) { '-' + ($Name -replace '[^\w-]+', '_') } else { '' }
$base = "sweep-$stamp$tag"
$recPath = Join-Path $OutDir "$base.wav"
$jsonTf = Join-Path $OutDir "$base.json"

$ffLog = Join-Path $OutDir "$base-ffmpeg.log"

# --- the sweep file ---------------------------------------------------------
if (-not $Sweep) {
    $Sweep = Join-Path $root 'sweeps\sweep-20-20k-10s.wav'
    if (-not (Test-Path $Sweep)) {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Sweep) | Out-Null
        & $tool make $Sweep 10 20 20000 48000 | Out-Null
    }
}
if (-not (Test-Path $Sweep)) { throw "sweep file not found: $Sweep" }

# Re-analysing a take that is already on disk: skip the capture entirely so the same
# numbers can be reproduced later without playing anything.
if ($From) {
    if (-not (Test-Path $From)) { throw "recording not found: $From" }
    $recPath = (Resolve-Path $From).Path
    $useLoopback = $true
    $Mic = '(from file)'
    $Loopback = '(from file)'
} else {
$useLoopback = -not $NoLoopback
}

# --- pick devices -----------------------------------------------------------
if (-not $From) {
# ffmpeg prints one line per device as:  "Friendly Name" (audio)
# Parse those and match by substring; the caller can override with -Mic/-Loopback.
# ffmpeg writes the device list to stderr and exits non-zero; with $ErrorActionPreference
# at Stop that becomes a terminating NativeCommandError, so relax it just for this probe.
$oldEap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$listing = (& $ffmpeg -hide_banner -list_devices true -f dshow -i dummy 2>&1 | Out-String)
$ErrorActionPreference = $oldEap
$devices = @()
foreach ($line in ($listing -split "`n")) {
    $m = [regex]::Match($line, '"([^"]+)"\s+\(audio\)')
    if ($m.Success) { $devices += $m.Groups[1].Value }
}
if ($devices.Count -eq 0) { throw "no audio capture devices found (ffmpeg -list_devices)" }

if (-not $Mic) {
    $cand = $devices | Where-Object { $_ -match 'UMIK' } | Select-Object -First 1
    if (-not $cand) { $cand = $devices | Where-Object { $_ -match 'Microphone Array' } | Select-Object -First 1 }
    if (-not $cand) { $cand = $devices | Where-Object { $_ -match 'Microphone' } | Select-Object -First 1 }
    if (-not $cand) { $cand = $devices[0] }
    $Mic = $cand
} else {
    $hit = $devices | Where-Object { $_ -like "*$Mic*" } | Select-Object -First 1
    if ($hit) { $Mic = $hit } else { throw "mic '$Mic' not found. Devices: $($devices -join '; ')" }
}

$useLoopback = -not $NoLoopback
if ($useLoopback) {
    if (-not $Loopback) {
        $Loopback = $devices | Where-Object { $_ -match 'Stereo Mix' } | Select-Object -First 1
    } else {
        $hit = $devices | Where-Object { $_ -like "*$Loopback*" } | Select-Object -First 1
        if (-not $hit) { throw "loopback '$Loopback' not found. Devices: $($devices -join '; ')" }
        $Loopback = $hit
    }
    if (-not $Loopback) { $useLoopback = $false }
}

Write-Host "mic      : $Mic"
if ($useLoopback) { Write-Host "loopback : $Loopback" } else { Write-Host "loopback : (none)" }
Write-Host "sweep    : $(Split-Path -Leaf $Sweep)"
Write-Host "record   : $([math]::Round($Seconds,1)) s -> $(Split-Path -Leaf $recPath)"

# --- build the ffmpeg command ----------------------------------------------
# Both devices are forced to 48 kHz / mono before merging, because dshow hands back each
# device's native format (Stereo Mix is 44.1 kHz, the UMIK is 48 kHz) and amerge requires
# matching streams.
function Q([string]$s) {
    if ($s -notmatch '[\s"]') { return $s }
    return '"' + ($s -replace '"', '\"') + '"'
}

$mono = 'aresample=48000,pan=mono|c0=c0'
$args = @('-y', '-hide_banner', '-loglevel', 'warning')
if ($useLoopback) {
    $args += @('-f', 'dshow', '-i', "audio=$Loopback")
    $args += @('-f', 'dshow', '-i', "audio=$Mic")
    $args += @('-filter_complex', "[0:a]$mono[r];[1:a]$mono[m];[r][m]amerge=inputs=2[a]")
    $args += @('-map', '[a]', '-ac', '2')
} else {
    $args += @('-f', 'dshow', '-i', "audio=$Mic")
    $args += @('-filter_complex', "[0:a]$mono[a]")
    $args += @('-map', '[a]', '-ac', '1')
}
$args += @('-t', [string]$Seconds, '-ar', '48000', '-c:a', 'pcm_s16le', $recPath)

$cmdline = ($args | ForEach-Object { Q $_ }) -join ' '
Write-Host ""
Write-Host "recording ..."

# ProcessStartInfo rather than Start-Process: it gives a reliable exit code and lets the
# stderr text be collected without a temp file race.
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $ffmpeg
$psi.Arguments = $cmdline
$psi.UseShellExecute = $false
$psi.RedirectStandardError = $true
$psi.CreateNoWindow = $true
$p = [System.Diagnostics.Process]::Start($psi)
$errTask = $p.StandardError.ReadToEndAsync()

Start-Sleep -Milliseconds ([int]($Lead * 1000))

if (-not $NoPlay) {
    if (-not (Test-Path $ffplay)) { throw "ffplay not found at $ffplay" }
    Write-Host "playing  : $(Split-Path -Leaf $Sweep)"
    & $ffplay -hide_banner -loglevel error -nodisp -autoexit -volume 100 $Sweep
}

$p.WaitForExit()
$err = $errTask.Result
if ($err) { Set-Content -Path $ffLog -Value $err -Encoding UTF8 }
if ($p.ExitCode -ne 0) {
    throw "ffmpeg failed (exit $($p.ExitCode)): $err"
}
if (-not (Test-Path $recPath)) { throw "no recording written: $recPath" }
Write-Host "recorded : $(Split-Path -Leaf $recPath)"
}   # end of the live-capture branch

# --- analyse ---------------------------------------------------------------
# With a loopback the tool can find every burst in the cycle and measure each one against
# the sound that produced it; that is the mode that gives comparable per-channel delays.
# Without a loopback the only reference is the sweep file, which still gives the shape but
# not a delay that can be compared between runs.
$bursts = 0
$delays = @()
$analyseExit = 0
$jsonTiming = $null

if ($useLoopback) {
    Write-Host ""
    & $tool bursts $recPath $Sweep $jsonTf
    $analyseExit = $LASTEXITCODE
    if (Test-Path $jsonTf) {
        $j = Get-Content $jsonTf -Raw | ConvertFrom-Json
        $list = @($j.bursts)
        $bursts = $list.Count
        $delays = @($list | Where-Object { $_.ok } | ForEach-Object { [double]$_.delayMs })
    }
    if ($analyseExit -eq 3) {
        Write-Host "warning: the mic correlated with no burst - check the mic and its level."
    }
} else {
    & $tool analyse $Sweep $recPath $jsonTf
    $analyseExit = $LASTEXITCODE
    if ($analyseExit -eq 3) {
        Write-Host ""
        Write-Host "warning: the mic did not carry the whole sweep, so there is no delay and no curve above."
        Write-Host "         This take cannot be used for alignment - re-record with the whole sweep played."
    } elseif ($analyseExit -eq 0) {
        Write-Host ""
        Write-Host "note     : recorded without a loopback, so the number above is where the sweep sits in the"
        Write-Host "           file (counted from the record button), NOT a per-channel arrival delay. Only the"
        Write-Host "           response curve is usable from this mode."
    }
}

$summary = [ordered]@{
    at = (Get-Date).ToString('s')
    name = $Name
    mic = $Mic
    loopback = $(if ($useLoopback) { $Loopback } else { $null })
    seconds = $Seconds
    sweep = (Split-Path -Leaf $Sweep)
    recording = (Split-Path -Leaf $recPath)
    result = (Split-Path -Leaf $jsonTf)
    bursts = $bursts
    delaysMs = $delays
    measured = ($analyseExit -eq 0)
}
$summary | ConvertTo-Json -Depth 4 | Set-Content -Path (Join-Path $OutDir "$base-summary.json") -Encoding UTF8

Write-Host ""
if ($delays.Count -gt 0) {
    # Relative delays are what alignment uses; show them next to the raw numbers.
    $min = ($delays | Measure-Object -Minimum).Minimum
    $rel = ($delays | ForEach-Object { "{0:+0.00;-0.00;0.00}" -f ($_ - $min) }) -join "  "
    Write-Host "bursts   : $bursts ($($delays.Count) measured)"
    Write-Host "delays   : $(($delays | ForEach-Object { $_.ToString('0.00') }) -join '  ') ms"
    Write-Host "relative : $rel ms   (against the earliest, which is not necessarily channel 1)"
} elseif ($bursts -gt 0) {
    Write-Host "bursts   : $bursts found, none measured - the mic did not hear them."
}
Write-Host "result   : $jsonTf"
exit $analyseExit
