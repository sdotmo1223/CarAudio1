# verify-all.ps1 - one verdict across every gate in this project.
#
# Why this exists (2026-10-25): there were eleven verification entry points and no rollup.
# `verify-web.ps1`, `verify-sweep-gate.ps1`, `lint-refs.ps1`, `verify-swap-tunes.py`,
# `doctor.ps1`, `preflight.ps1`, `rehearse.ps1`, `fingerprint.ps1`, `compare-fingerprints.ps1`,
# `app\self-test.ps1` and `after-run.ps1` each answer "did MY thing pass" and nothing answers
# "is the repo healthy". A green always meant one script, never the project.
#
# Every gate here is READ-ONLY and needs no hardware. Nothing writes to the DSP; nothing
# needs the car. Gates that need the amplifier are deliberately not in this list - they are
# on the rig, and doctor.ps1 says so in its own output.
#
#   powershell -File verify-all.ps1           # run everything, print a table, write DSB\VERIFY.md
#   powershell -File verify-all.ps1 -Only web,refs
#   powershell -File verify-all.ps1 -All      # + the desktop app's self-test (builds it)
#   powershell -File verify-all.ps1 -Json
#
# SESSION START (2026-10-25): this is the first command of a session, before reading anything.
#   powershell -File dsb-status.ps1        # what is true right now
#   powershell -File verify-all.ps1        # is the repo green
# Two commands, about 35 seconds, and the 5,330-line knowledge base no longer has to be
# re-read to answer five questions it never answered in prose anyway.
#
# Exit 0 = every gate passed. Exit 1 = at least one failed.

param([string[]]$Only, [switch]$Json, [switch]$All)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$py   = 'python'

# name, how to run it, what it proves
$gates = @(
    @{ n = 'lint-refs'; cmd = 'powershell'; a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $root 'lint-refs.ps1'));
       proves = 'every reference in DSB\ resolves to a real file' },
    @{ n = 'verify-web'; cmd = 'powershell'; a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $root 'verify-web.ps1'));
       proves = 'the inline script parses, every id the script reaches for exists, the measurement-baseline classifier still classifies' },
    @{ n = 'verify-sweep'; cmd = 'powershell'; a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $root 'verify-sweep-gate.ps1'));
       proves = 'the analyser rejects a partial or missing sweep and accepts a real one' },
    @{ n = 'verify-swap'; cmd = $py; a = @((Join-Path $root 'tune\verify-swap-tunes.py'));
       proves = 'the A/B swap tunes are byte-consistent with what they claim to be' }
)

if ($All) {
    # Behind -All on purpose: unlike the four above this one BUILDS, and it needs the
    # running DreTunes.exe stopped first because the exe holds bin\DreTunes.dll. A session
    # that has the app open would otherwise fail a gate for a reason that has nothing to do
    # with whether the code is correct. self-test.ps1 also runs its own negative control and
    # fails if the mutant run passes - a self-test that cannot fail has verified nothing.
    $gates += @{ n = 'app-selftest'; cmd = 'powershell'
                 a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $root 'app\self-test.ps1'))
                 proves = 'DreTunes builds, 126 assertions pass, and its 7 mutants are all caught' }
}

if ($Only -and $Only.Count -gt 0) { $gates = $gates | Where-Object { $Only -contains $_.n } }

$results = @()
foreach ($g in $gates) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $out = ''
    $code = -1
    try {
        $out = (& $g.cmd @($g.a) 2>&1 | Out-String)
        $code = $LASTEXITCODE
        if ($null -eq $code) { $code = 0 }
    } catch {
        $out = $_.Exception.Message
        $code = -1
    }
    $sw.Stop()
    $results += [pscustomobject]@{
        gate    = $g.n
        passed  = ($code -eq 0)
        exit    = $code
        ms      = [int]$sw.ElapsedMilliseconds
        proves  = $g.proves
        tail    = (($out -split "`r?`n" | Where-Object { $_.Trim() -ne '' } | Select-Object -Last 4) -join ' | ')
    }
}

$failed = @($results | Where-Object { -not $_.passed })

if ($Json) {
    [pscustomobject]@{ at = (Get-Date -Format o); clean = ($failed.Count -eq 0); gates = $results } |
        ConvertTo-Json -Depth 5
    if ($failed.Count -gt 0) { exit 1 } else { exit 0 }
}

# ---------------------------------------------------------------- human report
$lines = New-Object System.Collections.ArrayList
[void]$lines.Add('# VERIFY - one verdict across every gate')
[void]$lines.Add('')
[void]$lines.Add(("Run {0}. Every gate below is read-only and needs no hardware." -f (Get-Date -Format 'yyyy-MM-dd HH:mm')))
[void]$lines.Add('')
[void]$lines.Add('| gate | result | time | what it proves |')
[void]$lines.Add('|---|---|---|---|')
foreach ($r in $results) {
    $mark = if ($r.passed) { 'PASS' } else { '**FAIL**' }
    # PowerShell's -f uses {n}, not %s. This file originally wrote %s because C-style
    # placeholders are a habit; the whole table rendered as literal "%s". See the review
    # note in DSB\START-HERE.md - it happened twice, so it is a pattern, not a typo.
    [void]$lines.Add(('| `{0}` | {1} | {2} ms | {3} |' -f $r.gate, $mark, $r.ms, $r.proves))
}
[void]$lines.Add('')
if ($failed.Count -eq 0) {
    [void]$lines.Add('**All gates green.**')
    'verify-all: ALL GREEN'
} else {
    [void]$lines.Add(('**{0} gate(s) failed:** {1}' -f $failed.Count, (($failed | ForEach-Object { $_.gate }) -join ', ')))
    ''
    foreach ($r in $failed) {
        [void]$lines.Add(('FAILED {0} (exit {1})' -f $r.gate, $r.exit))
        [void]$lines.Add(('      {0}' -f $r.tail))
    }
    ''
    'verify-all: FAIL'
}
[void]$lines.Add('')
[void]$lines.Add('Not covered here, and never can be from a desk: the DSP writes, the flatten and its')
[void]$lines.Add('restore, the measurement run, the freeze and the microphone. Those are proved on the')
[void]$lines.Add('rig with `doctor.ps1` and `rehearse.ps1`. A green page here says nothing about them.')
[void]$lines.Add('')
[void]$lines.Add('Not run by default: `app\self-test.ps1` (`-All`, builds the app and needs it closed),')
[void]$lines.Add('`doctor.ps1` (needs the car), `compare-fingerprints.ps1` and `rehearse.ps1` (need args')
[void]$lines.Add('or a run). See `START-HERE.md` for the session-start sequence.')

$report = ($lines -join "`r`n")
Set-Content -LiteralPath (Join-Path $root 'DSB\VERIFY.md') -Value $report -Encoding UTF8
Write-Output $report

if ($failed.Count -gt 0) { exit 1 } else { exit 0 }
