param()
# Competition-entry build sweep, 2026-10-22 evening (owner at the gym).
# 1) regression: the default (no --fill) path must still reproduce the verified
#    house plan byte for byte - proves the --fill-rounds arg changed nothing by default.
# 2) five --fill candidates from different stage-1 basins (+ one deeper-rounds run).
# 3) system-predict residual for every candidate, appended to summary.txt.
$ErrorActionPreference = 'Continue'
New-Item -ItemType Directory -Force var\build-sweep | Out-Null
Remove-Item var\build-sweep\summary.txt -ErrorAction SilentlyContinue

python tune/fit-dense.py --pin-gains --max-bands 19 `
  --out var/build-sweep/regress-house.json --idsp var/build-sweep/regress-house.idsp `
  *> var/build-sweep/log-regress.txt
$rp = 'regression default path byte-identical: '
if (Test-Path tune\tune-plan-house.json) {
  $a = (Get-FileHash tune\tune-plan-house.json).Hash
  $b = (Get-FileHash var\build-sweep\regress-house.json).Hash
  $rp += ($a -eq $b)
} else { $rp += 'tune-plan-house.json missing' }
$rp | Add-Content var\build-sweep\summary.txt

$cfgs = @(
  @{ n = 'c0-mb19';      mb = '19' },
  @{ n = 'c1-mb15';      mb = '15' },
  @{ n = 'c2-mb23';      mb = '23' },
  @{ n = 'c3-mb27';      mb = '27' },
  @{ n = 'c4-mb19-r8';   mb = '19'; r = '8' }
)
foreach ($c in $cfgs) {
  $sw = [Diagnostics.Stopwatch]::StartNew()
  $argList = @('tune/fit-dense.py', '--pin-gains', '--fill', '--max-bands', $c.mb,
               '--out', "var/build-sweep/plan-$($c.n).json",
               '--idsp', "var/build-sweep/cand-$($c.n).idsp")
  if ($c.r) { $argList += @('--fill-rounds', $c.r) }
  python @argList *> "var/build-sweep/log-$($c.n).txt"
  $code = $LASTEXITCODE
  $sw.Stop()
  ("{0}: exit={1} time={2}s" -f $c.n, $code, [int]$sw.Elapsed.TotalSeconds) |
    Add-Content var\build-sweep\summary.txt
}

foreach ($c in $cfgs) {
  if (-not (Test-Path "var\build-sweep\cand-$($c.n).idsp")) { continue }
  $res = python tune/system-predict.py "var\build-sweep\cand-$($c.n).idsp" 2>&1 |
    Select-String -Pattern 'residual rms|bands'
  ("{0}: {1}" -f $c.n, (($res | ForEach-Object { $_.Line.Trim() }) -join '  ')) |
    Add-Content var\build-sweep\summary.txt
}
'DONE' | Add-Content var\build-sweep\summary.txt
