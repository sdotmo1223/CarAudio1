# Compiles DreTunes with the .NET Framework C# compiler that ships with Windows.
#
# Why not MSBuild / dotnet / WebView2:
#   * dotnet.exe and MSBuild are not installed on this machine and cannot be installed
#     offline (the disk budget is ~5 GB and the brief forbids internet installs).
#   * The WebView2 runtime is present but the managed SDK DLL (Microsoft.Web.WebView2.Core)
#     is not, so a WebView2-hosted UI cannot be compiled offline.
#   * csc.exe in %windir%\Microsoft.NET\Framework64\v4.0.30319 is part of .NET Framework 4.8,
#     which is whitelisted here and is the same toolchain ds18-bridge\build.ps1 uses.
#   Readme.md and APP-NOTES.md repeat this justification; it is not an accident.
#
# Language level is C# 5: that compiler predates interpolation, ?., nameof and
# expression-bodied members. The sources are written to that level deliberately.
#
# A response file is used because the project path contains a space and PowerShell does not
# reliably re-quote constructed arguments when invoking a native command.
# -Out allows building beside a running copy (the exe is locked while the window is open).
# It is only for verification; the shipped binary is always bin\DreTunes.exe.
param([string]$Out = 'DreTunes.exe')

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = Join-Path $root 'src'
$bin  = Join-Path $root 'bin'
New-Item -ItemType Directory -Force -Path $bin | Out-Null

$csc = Join-Path $env:windir 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:windir 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'csc.exe not found (is .NET Framework 4.x installed?)' }

$out = Join-Path $bin $Out
$name = [System.IO.Path]::GetFileNameWithoutExtension($Out)
$rsp = Join-Path $bin 'build.rsp'
if ($name -ne 'DreTunes') { $rsp = Join-Path $bin ('build-' + $name + '.rsp') }
$ico = Join-Path $root '..\DreTunes.ico'
$man = Join-Path $root 'app.manifest'

# No external assemblies: the app is self-contained against the framework. The strict JSON
# and .idsp parsers are in-repo (Json.cs, TuneFile.cs) rather than borrowed, so there is no
# JavaScriptSerializer dependency and no XML schema guesswork.
$lines = @(
    '/nologo',
    '/warn:4',
    '/target:winexe',
    '/platform:anycpu',
    '/optimize+',
    '/r:System.dll',
    '/r:System.Core.dll',
    '/r:System.Drawing.dll',
    '/r:System.Windows.Forms.dll',
    '/r:System.Xml.dll',
    "/win32manifest:`"$man`"",
    "/out:`"$out`""
)
if (Test-Path $ico) { $lines += "/win32icon:`"$ico`"" }

$files = Get-ChildItem -Path $src -Filter *.cs | Sort-Object Name
if ($files.Count -eq 0) { throw "no sources in $src" }
foreach ($f in $files) { $lines += "`"" + $f.FullName + "`"" }

# ASCII only: the compiler reads response files as ANSI/UTF-8 and every path here is ASCII.
[System.IO.File]::WriteAllLines($rsp, $lines, [System.Text.Encoding]::ASCII)

# Run from $bin so the response file is referenced relatively - the only argument csc sees
# on the command line then contains no spaces at all.
Push-Location $bin
try {
    & $csc ('@' + [System.IO.Path]::GetFileName($rsp))
    $code = $LASTEXITCODE
}
finally {
    Pop-Location
}
if ($code -ne 0) { throw "compile failed (exit $code)" }

$size = [math]::Round((Get-Item $out).Length / 1KB, 1)
"compiled -> $out  ($size KB, $($files.Count) sources)"
