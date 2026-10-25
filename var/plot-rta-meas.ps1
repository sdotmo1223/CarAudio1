# plot-rta-meas.ps1 - draw the saved 1/6-octave captures as curves so they can be judged, not guessed.
#
# /api/rta-meas hands back every capture the page has ever pushed (61 points, log-spaced from
# 20 Hz in 1/6-octave steps). A table of numbers hides the shape; one image shows whether a
# curve is a measurement or noise.
param(
    [int]$Last = 8,
    [string]$Out = '.\var\plots\run-curves.png',
    [double]$MinDb = -120,
    [double]$MaxDb = -10
)

$d = Invoke-RestMethod 'http://127.0.0.1:8585/api/rta-meas' -TimeoutSec 10
$items = @($d.meas | Select-Object -Last $Last)
if ($items.Count -eq 0) { throw 'no measurements returned' }

$grid = 0..60 | ForEach-Object { 20 * [math]::Pow(2, $_ / 6) }

Add-Type -AssemblyName System.Drawing
$w = 1200; $h = 620
$padL = 70; $padR = 190; $padT = 30; $padB = 55
$plotW = $w - $padL - $padR
$plotH = $h - $padT - $padB

$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::FromArgb(18, 20, 24))

$font  = New-Object System.Drawing.Font('Consolas', 10)
$fontS = New-Object System.Drawing.Font('Consolas', 8.5)
$white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(200, 220, 225))
$dim   = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(110, 120, 130))
$gridP = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(40, 50, 60), 1)

function XOf([int]$i) { $padL + ($i / 60.0) * $plotW }
function YOf([double]$db) {
    $t = ($db - $MinDb) / ($MaxDb - $MinDb)
    if ($t -lt 0) { $t = 0 }; if ($t -gt 1) { $t = 1 }
    $padT + (1 - $t) * $plotH
}

# grid: octave lines and dB lines
foreach ($freq in @(20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000)) {
    $i = 0; $bd = 1e9
    for ($k = 0; $k -lt 61; $k++) { $dd = [math]::Abs($grid[$k] - $freq); if ($dd -lt $bd) { $bd = $dd; $i = $k } }
    $x = XOf $i
    $g.DrawLine($gridP, $x, $padT, $x, $padT + $plotH)
    $label = if ($freq -ge 1000) { "{0}k" -f ($freq / 1000) } else { "$freq" }
    $g.DrawString($label, $fontS, $dim, ($x - 10), ($padT + $plotH + 6))
}
for ($db = $MinDb; $db -le $MaxDb; $db += 10) {
    $y = YOf $db
    $g.DrawLine($gridP, $padL, $y, ($padL + $plotW), $y)
    $g.DrawString(("{0}" -f [int]$db), $fontS, $dim, 20, ($y - 8))
}
$g.DrawString('dBFS', $fontS, $dim, 20, 10)
$g.DrawString('Hz', $fontS, $dim, ($padL + $plotW / 2), ($h - 22))

$palette = @(
    [System.Drawing.Color]::FromArgb(224, 91, 75),
    [System.Drawing.Color]::FromArgb(75, 208, 122),
    [System.Drawing.Color]::FromArgb(74, 163, 255),
    [System.Drawing.Color]::FromArgb(232, 163, 61),
    [System.Drawing.Color]::FromArgb(201, 209, 217),
    [System.Drawing.Color]::FromArgb(62, 193, 169),
    [System.Drawing.Color]::FromArgb(176, 122, 222),
    [System.Drawing.Color]::FromArgb(94, 200, 232)
)

$li = 0
foreach ($m in $items) {
    $col = $palette[$li % $palette.Count]
    $pen = New-Object System.Drawing.Pen($col, 1.8)
    $pts = New-Object System.Collections.Generic.List[System.Drawing.PointF]
    for ($k = 0; $k -lt 61 -and $k -lt $m.db.Count; $k++) {
        $pts.Add((New-Object System.Drawing.PointF((XOf $k), (YOf ([double]$m.db[$k])))))
    }
    if ($pts.Count -ge 2) { $g.DrawLines($pen, $pts.ToArray()) }

    $label = if ($m.name) { $m.name } else { '(unnamed)' }
    $y = $padT + 10 + $li * 20
    $g.FillRectangle((New-Object System.Drawing.SolidBrush($col)), ($padL + $plotW + 14), ($y - 8), 10, 10)
    $g.DrawString($label, $font, $white, ($padL + $plotW + 30), ($y - 10))
    $pen.Dispose()
    $li++
}

if (-not (Test-Path (Split-Path $Out))) { New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null }
$bmp.Save((Resolve-Path -LiteralPath (Split-Path $Out)).Path + '\' + (Split-Path $Out -Leaf), [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Output ("plotted {0} curves -> {1}" -f $items.Count, $Out)
