# where.ps1 - the address to put in the phone, right now.
#
# The laptop's LAN address changes with the network (measured 2026-10-20: it was 10.66.161.189
# in the car and 192.168.7.253 on the work Wi-Fi an hour later), so a saved URL goes stale.
# The machine name does not: http://<name>:8585 answered on both networks.
#
#   powershell -File where.ps1
#
param([int]$Port = 8585, [string]$Page = 'play.html')

$name = $env:COMPUTERNAME
$ips = @(Get-NetIPAddress -AddressFamily IPv4 |
    Where-Object { $_.IPAddress -notmatch '^(127\.|169\.254\.)' } |
    Sort-Object -Property SkipAsSource, InterfaceMetric |
    ForEach-Object { $_.IPAddress })

$candidates = @()
$candidates += @{ url = ("http://{0}:{1}/{2}" -f $name, $Port, $Page); label = 'machine name (stable across networks)' }
$candidates += @{ url = ("http://{0}.local:{1}/{2}" -f $name, $Port, $Page); label = 'mDNS name' }
foreach ($ip in $ips) { $candidates += @{ url = ("http://{0}:{1}/{2}" -f $ip, $Port, $Page); label = 'current LAN address (changes with the network)' } }

foreach ($c in $candidates) {
    $state = 'no answer'
    try {
        $r = Invoke-WebRequest $c.url -TimeoutSec 4 -UseBasicParsing
        $state = "ok $($r.StatusCode)"
    } catch {
        $state = 'failed'
    }
    Write-Host ("  {0,-58} {1,-16} {2}" -f $c.url, $state, $c.label)
}

Write-Host ''
Write-Host "DSP / tune page : http://$name`:$Port/"
Write-Host "Stimulus player : http://$name`:$Port/$Page"
Write-Host ''
Write-Host "The phone must be on the same Wi-Fi as this laptop."
