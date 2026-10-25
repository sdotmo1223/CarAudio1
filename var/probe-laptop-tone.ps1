# probe-laptop-tone.ps1 - is the UMIK-1 capsule actually hearing air-borne sound?
#
# Every take so far has been refused because the mic floor above 300 Hz sits near -82 dBFS
# while a cabin with audible music should be 40 dB louder than that. A tap on the housing
# registers (structure-borne), so the capsule is not dead, but that proves nothing about air.
#
# This plays a 1 kHz tone out of the laptop's own speakers - a source whose level is known -
# while recording the UMIK. A healthy capsule a hand's width away reads roughly -20 to -40
# dBFS. A capsule that only responds to vibration reads the room floor.
param(
    [int]$Seconds = 6,
    [string]$Dir = '.\var\probe',
    [string]$Device = 'audio=Microphone (UMIK-1)'
)

$ff    = 'C:\tools\ffmpeg\ffmpeg-master-latest-win64-gpl\bin\ffmpeg.exe'
$ffplay = Join-Path (Split-Path $ff) 'ffplay.exe'
if (-not (Test-Path $ff))     { throw "ffmpeg not found at $ff" }
if (-not (Test-Path $ffplay)) { throw "ffplay not found at $ffplay" }
if (-not (Test-Path $Dir)) { New-Item -ItemType Directory -Force -Path $Dir | Out-Null }
$Dir = (Resolve-Path $Dir).Path
$rec = Join-Path $Dir 'laptop-tone.wav'
if (Test-Path $rec) { Remove-Item $rec -Force }

$recArgs = @('-hide_banner', '-v', 'error', '-y', '-f', 'dshow', '-i', $Device,
             '-t', "$Seconds", '-ac', '1', '-ar', '48000', '-c:a', 'pcm_s16le', $rec)
$toneArgs = @('-hide_banner', '-nodisp', '-autoexit', '-volume', '100',
              '-f', 'lavfi', '-i', 'sine=frequency=1000:duration=4')

$recJob = Start-Job -ScriptBlock { param($e, $a) & $e @a; $LASTEXITCODE } -ArgumentList $ff, $recArgs
Start-Sleep -Milliseconds 1500
$toneJob = Start-Job -ScriptBlock { param($e, $a) & $e @a; $LASTEXITCODE } -ArgumentList $ffplay, $toneArgs

$recJob | Wait-Job -Timeout ($Seconds + 40) | Out-Null
$toneJob | Wait-Job -Timeout 40 | Out-Null
Remove-Job $recJob, $toneJob -Force -ErrorAction SilentlyContinue

if (-not (Test-Path $rec)) { throw "recording failed - $rec was not written" }
Write-Output ("recorded {0:N0} bytes -> {1}" -f (Get-Item $rec).Length, $rec)

$txt = (& $ff -hide_banner -i $rec -af 'volumedetect' -f null - 2>&1 | Out-String)
$mean = [regex]::Match($txt, 'mean_volume:\s*(-?[0-9.]+) dB').Groups[1].Value
$max  = [regex]::Match($txt, 'max_volume:\s*(-?[0-9.]+) dB').Groups[1].Value
Write-Output ("  full band : mean {0} dBFS   max {1} dBFS" -f $mean, $max)

# 1 kHz is the tone; measure it alone, and the rest of the spectrum for comparison.
foreach ($band in @(@{l='~1 kHz';f='bandpass=f=1000:width_type=h:w=200'},@{l='>300 Hz';f='highpass=f=300'})) {
    $t = (& $ff -hide_banner -i $rec -af ($band.f + ',volumedetect') -f null - 2>&1 | Out-String)
    $m = [regex]::Match($t, 'mean_volume:\s*(-?[0-9.]+) dB').Groups[1].Value
    Write-Output ("  {0,-9}: mean {1} dBFS" -f $band.l, $m)
}
