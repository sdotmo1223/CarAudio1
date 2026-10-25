# Compiles the ds18-bridge sources with the .NET Framework C# compiler (no SDK required).
# Language level is C# 5 because csc.exe ships with .NET Framework 4.x.
#
# A response file (.rsp) is used because the project path contains a space and PowerShell
# does not reliably re-quote constructed arguments when invoking native commands.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = Join-Path $root 'src'
$bin  = Join-Path $root 'bin'
New-Item -ItemType Directory -Force -Path $bin | Out-Null

$csc = Join-Path $env:windir 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:windir 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'csc.exe not found (is .NET Framework 4.x installed?)' }

# Vendor DLLs extracted from the official app (manifest resources, verified byte-length by
# REA inspect_managed_artifact). LibHoneyWg computes the biquad coefficients (reused, not
# re-implemented); LibUsbDotNet is the USB transport.
#
# Preferred location is ds18-bridge/vendor next to the sources so the build does not depend
# on %TEMP%. The temp path is kept only as a fallback for a fresh extraction.
$dllNames = @('LibHoneyWg.dll', 'LibUsbDotNet.dll')
$vendorDirs = @((Join-Path $root 'vendor'), (Join-Path $env:TEMP 'opencode\ds18'))
foreach ($dll in $dllNames) {
    $dest = Join-Path $bin $dll
    $from = $null
    foreach ($dir in $vendorDirs) {
        $cand = Join-Path $dir $dll
        if (Test-Path $cand) { $from = $cand; break }
    }
    if (-not $from) {
        throw ("missing vendor DLL {0}; expected in one of: {1}" -f $dll, ($vendorDirs -join ', '))
    }
    Copy-Item -Force $from $dest
}

$out  = Join-Path $bin 'ds18-bridge.exe'
$rsp  = Join-Path $bin 'build.rsp'
$honey = Join-Path $bin 'LibHoneyWg.dll'
$usb   = Join-Path $bin 'LibUsbDotNet.dll'

# Response-file arguments: quote any path that contains a space.
$lines = @(
    '/nologo',
    '/warn:4',
    '/target:exe',
    '/platform:anycpu',
    '/r:System.dll',
    '/r:System.Core.dll',
    '/r:System.Web.Extensions.dll',
    "/r:`"$honey`"",
    "/r:`"$usb`"",
    "/out:`"$out`""
)
foreach ($f in @('Protocol.cs', 'DspMath.cs', 'DspClient.cs', 'BridgeServer.cs', 'Program.cs')) {
    $lines += "`"" + (Join-Path $src $f) + "`""
}

# ASCII only: the compiler reads response files as ANSI/UTF-8 and paths here are ASCII.
[System.IO.File]::WriteAllLines($rsp, $lines, [System.Text.Encoding]::ASCII)

# Run from $bin so the response file can be referenced relatively - the only argument
# csc sees on the command line then contains no spaces at all.
Push-Location $bin
try {
    & $csc '@build.rsp'
    $code = $LASTEXITCODE
}
finally {
    Pop-Location
}
if ($code -ne 0) { throw "compile failed (exit $code)" }
"compiled -> $out"
