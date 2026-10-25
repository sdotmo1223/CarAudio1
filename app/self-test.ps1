param([switch]$SkipBuild)

# Builds DreTunes and runs both halves of its self-test.
#
#   .\self-test.ps1              build, then the self-test (must exit 0)
#   .\self-test.ps1 -SkipBuild   reuse bin\DreTunes.exe
#
# The second run is the negative control: --mutant swaps in seven deliberately wrong
# implementations (a clean-forever diff, an always-allowed guard, a flat curve, a swapped
# passband readback, a Tune table that claims to be writable, a mute block that zeroes the
# channels it did not touch, and a mute result that claims device verification). Every
# assertion that depends on those must then FAIL. A self-test that cannot fail is not a test,
# so this script fails if the mutant run passes.
#
# DreTunes.exe is a winexe, so its console output has to be redirected to a file - PowerShell
# returns from a GUI-subsystem process instead of waiting for it.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $root 'bin\DreTunes.exe'
$var = Join-Path $root 'var'
New-Item -ItemType Directory -Force -Path $var | Out-Null

function Run-SelfTest([string]$name, [string[]]$extra) {
    $log = Join-Path $var ($name + '.txt')
    $args = @('--selftest') + $extra
    $p = Start-Process -FilePath $exe -ArgumentList $args -Wait -PassThru -NoNewWindow `
                       -RedirectStandardOutput $log
    return @{ Code = $p.ExitCode; Lines = @(Get-Content $log) }
}

if (-not $SkipBuild) {
    & (Join-Path $root 'build.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'build failed' }
}
if (-not (Test-Path $exe)) { throw "missing $exe - run build.ps1 first" }

$normal = Run-SelfTest 'selftest' @()
Write-Host '== self-test (expect: 0 failed, exit 0) =='
$normal.Lines | Where-Object { $_ -match '^  FAIL|^selftest:' } | Write-Host
$passed = ($normal.Lines | Where-Object { $_ -match '^selftest:' } | Select-Object -Last 1)
if ($normal.Code -ne 0) {
    Write-Host ''
    Write-Host "FAILED: the self-test exited $($normal.Code). Summary: $passed"
    Write-Host "        full output: $(Join-Path $var 'selftest.txt')"
    exit 1
}

$mutant = Run-SelfTest 'selftest-mutant' @('--mutant')
Write-Host ''
Write-Host '== negative control (expect: failures, exit 1) =='
$mutant.Lines | Where-Object { $_ -match '^  FAIL|^selftest:' } | Write-Host
$fails = ($mutant.Lines | Where-Object { $_ -match '^  FAIL' }).Count
if ($mutant.Code -eq 0 -or $fails -eq 0) {
    Write-Host ''
    Write-Host 'FAILED: the mutant run passed. The self-test would not have caught a broken diff, guard or curve.'
    exit 1
}

Write-Host ''
Write-Host "self-test.ps1: ok - $passed; the mutant run failed $fails assertion(s), as it must."
exit 0
