# Compiles the sweep measurement tool (src/SweepTool/SweepTool.cs) with the same stock
# csc.exe the bridge and rta use - no SDK, no NuGet, C# 5 language level.
#
# The tool is pure .NET Framework: WAV read/write, a radix-2 FFT and cross-correlation.
# It writes bin\sweep-tool.exe, which measure-sweep.ps1 drives.
#
# A response file is used for the same reason as the other builds: the project path contains
# a space, and PowerShell does not reliably re-quote constructed arguments for native tools.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = Join-Path $root 'src\SweepTool'
$bin  = Join-Path $root 'bin'
New-Item -ItemType Directory -Force -Path $bin | Out-Null

$csc = Join-Path $env:windir 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:windir 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'csc.exe not found (is .NET Framework 4.x installed?)' }

$out = Join-Path $bin 'sweep-tool.exe'
$rsp = Join-Path $bin 'sweep-tool.rsp'

$lines = @(
    '/nologo',
    '/warn:4',
    '/target:exe',
    '/platform:anycpu',
    '/r:System.dll',
    '/r:System.Core.dll',
    "/out:`"$out`""
)
$lines += ("`"" + (Join-Path $src 'SweepTool.cs') + "`"")

[System.IO.File]::WriteAllLines($rsp, $lines, [System.Text.Encoding]::ASCII)

Push-Location $bin
try {
    & $csc '@sweep-tool.rsp'
    $code = $LASTEXITCODE
}
finally {
    Pop-Location
}
if ($code -ne 0) { throw "compile failed (exit $code)" }
"compiled -> $out"
