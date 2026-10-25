# Compiles the RTA capture daemon (src/Rta/RTAMeter.cs) with the same stock csc.exe the
# bridge uses - no SDK, no NuGet, C# 5 language level.
#
# There is nothing to link against: RTAMeter.cs is pure .NET Framework (winmm is reached
# through P/Invoke), so unlike build.ps1 there are no vendor DLLs to copy. The output goes
# to bin\rta.exe next to the bridge, which is where DreTunes.ps1 launches it from.
#
# A response file is used for the same reason as build.ps1: the project path contains a
# space, and PowerShell does not reliably re-quote constructed arguments for native tools.
#
# Kill the running rta.exe first - it holds an exclusive lock on bin\rta.exe.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = Join-Path $root 'src\Rta'
$bin  = Join-Path $root 'bin'
New-Item -ItemType Directory -Force -Path $bin | Out-Null

$csc = Join-Path $env:windir 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:windir 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'csc.exe not found (is .NET Framework 4.x installed?)' }

$out = Join-Path $bin 'rta.exe'
$rsp = Join-Path $bin 'rta.rsp'

$lines = @(
    '/nologo',
    '/warn:4',
    '/target:exe',
    '/platform:anycpu',
    '/unsafe',
    '/r:System.dll',
    '/r:System.Core.dll',
    "/out:`"$out`""
)
# Note: parenthesised. Inside an @() literal the bare `"x" + (Join-Path ...) + "y"` form is
# parsed as three separate array elements (the quotes land on their own lines in the file,
# which csc then rejects with CS2032). build.ps1 sidesteps this with +=.
$lines += ("`"" + (Join-Path $src 'RTAMeter.cs') + "`"")

# ASCII only: the compiler reads response files as ANSI/UTF-8 and paths here are ASCII.
[System.IO.File]::WriteAllLines($rsp, $lines, [System.Text.Encoding]::ASCII)

# Run from $bin so the response file can be referenced relatively - the only argument csc
# sees on the command line then contains no spaces at all.
Push-Location $bin
try {
    & $csc '@rta.rsp'
    $code = $LASTEXITCODE
}
finally {
    Pop-Location
}
if ($code -ne 0) { throw "compile failed (exit $code)" }
"compiled -> $out"
