# probe-two-mics.ps1 - record the UMIK-1 and the laptop array at the same moment.
# A tap on the housing is structure-borne and proves nothing about the capsule. A clap in the
# air either reaches the capsule or it does not, and the laptop array answers the same question
# independently, so the two recordings settle where the sound stopped.
#
# Devices are opened through ffmpeg's dshow input. Arguments go through Start-Job with an array
# so the spaces and parentheses in a dshow device name survive; Start-Process joins an array
# into one string and splits "audio=Microphone (UMIK-1)" at the space, which fails silently.
param(
    [int]$Seconds = 10,
    [string]$Dir = '.\var\probe'
)

$ff = 'C:\tools\ffmpeg\ffmpeg-master-latest-win64-gpl\bin\ffmpeg.exe'
if (-not (Test-Path $ff)) { throw "ffmpeg not found at $ff" }
if (-not (Test-Path $Dir)) { New-Item -ItemType Directory -Force -Path $Dir | Out-Null }
$Dir = (Resolve-Path $Dir).Path

$devices = @(
    @{ name = 'umik';    dshow = 'audio=Microphone (UMIK-1)' },
    @{ name = 'builtin'; dshow = 'audio=Microphone Array (Realtek(R) Audio)' }
)

$jobs = @()
foreach ($d in $devices) {
    $out = Join-Path $Dir ($d.name + '.wav')
    $log = Join-Path $Dir ($d.name + '.log')
    if (Test-Path $out) { Remove-Item $out -Force }
    if (Test-Path $log) { Remove-Item $log -Force }
    $argList = @(
        '-hide_banner', '-v', 'info', '-y',
        '-f', 'dshow', '-i', $d.dshow,
        '-t', "$Seconds", '-ac', '1', '-ar', '48000',
        '-c:a', 'pcm_s16le', $out
    )
    $jobs += [pscustomobject]@{
        name = $d.name
        path = $out
        log  = $log
        job  = Start-Job -ScriptBlock {
            param($exe, $argList, $logPath)
            & $exe @argList *>&1 | Out-File -FilePath $logPath -Encoding utf8
            $LASTEXITCODE
        } -ArgumentList $ff, $argList, $log
    }
}

foreach ($x in $jobs) { $x.job | Wait-Job -Timeout ($Seconds + 40) | Out-Null }
Write-Output ("recorded {0} s in parallel" -f $Seconds)
foreach ($x in $jobs) {
    $state = if (Test-Path $x.path) { "{0:N0} bytes" -f (Get-Item $x.path).Length } else { 'FAILED' }
    Write-Output ("  {0,-8} {1}" -f $x.name, $state)
}
foreach ($x in $jobs) {
    $err = if (Test-Path $x.log) { (Get-Content $x.log | Select-String 'Error|error|Invalid|Could not' | Select-Object -First 2 | ForEach-Object { $_.Line.Trim() }) -join ' | ' } else { '' }
    if ($err) { Write-Output ("  {0,-8} ffmpeg: {1}" -f $x.name, $err) }
}
foreach ($x in $jobs) { Remove-Job $x.job -Force -ErrorAction SilentlyContinue }
