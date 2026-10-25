# probe-mic-response.ps1 - measure the UMIK's frequency response with white noise.
#
# Every in-cabin take is refused with no energy above 300 Hz, yet the same mic reads a 1 kHz
# laptop tone and registers hand taps. Before blaming the car any further, play white noise
# out of the laptop and measure what the capsule actually reports band by band. A healthy
# capsule is flat within a few dB; a covered or damaged one rolls off hard above a few
# hundred Hz, which would look exactly like a silent cabin.
param(
    [int]$Seconds = 6,
    [string]$Dir = '.\var\probe',
    [string]$Device = 'audio=Microphone (UMIK-1)'
)

$ff     = 'C:\tools\ffmpeg\ffmpeg-master-latest-win64-gpl\bin\ffmpeg.exe'
$ffplay = Join-Path (Split-Path $ff) 'ffplay.exe'
if (-not (Test-Path $Dir)) { New-Item -ItemType Directory -Force -Path $Dir | Out-Null }
$Dir = (Resolve-Path $Dir).Path
$rec = Join-Path $Dir 'white-noise.wav'
if (Test-Path $rec) { Remove-Item $rec -Force }

$recArgs = @('-hide_banner', '-v', 'error', '-y', '-f', 'dshow', '-i', $Device,
             '-t', "$Seconds", '-ac', '1', '-ar', '48000', '-c:a', 'pcm_s16le', $rec)
$noiseArgs = @('-hide_banner', '-nodisp', '-autoexit', '-volume', '100',
               '-f', 'lavfi', '-i', 'anoisesrc=color=white:amplitude=0.9:duration=5')

$recJob = Start-Job -ScriptBlock { param($e, $a) & $e @a; $LASTEXITCODE } -ArgumentList $ff, $recArgs
Start-Sleep -Milliseconds 1000
$noiseJob = Start-Job -ScriptBlock { param($e, $a) & $e @a; $LASTEXITCODE } -ArgumentList $ffplay, $noiseArgs
$recJob | Wait-Job -Timeout ($Seconds + 40) | Out-Null
$noiseJob | Wait-Job -Timeout 40 | Out-Null
Remove-Job $recJob, $noiseJob -Force -ErrorAction SilentlyContinue
if (-not (Test-Path $rec)) { throw "recording failed" }

Write-Output ("recorded {0:N0} bytes" -f (Get-Item $rec).Length)
Write-Output 'band centre   level dBFS'
foreach ($f in @(100, 200, 500, 1000, 2000, 4000, 8000, 12000)) {
    $band = "bandpass=f=$f`:width_type=h:w=$($f/2)"
    $t = (& $ff -hide_banner -i $rec -af ($band + ',volumedetect') -f null - 2>&1 | Out-String)
    $m = [regex]::Match($t, 'mean_volume:\s*(-?[0-9.]+) dB').Groups[1].Value
    Write-Output ("  {0,6} Hz   {1,8}" -f $f, $m)
}
$png = Join-Path $Dir 'white-noise.png'
& $ff -hide_banner -v error -y -i $rec -lavfi "showspectrumpic=s=900x400:mode=combined:scale=log:fscale=log:legend=1" $png
Write-Output ("plot: $png")
