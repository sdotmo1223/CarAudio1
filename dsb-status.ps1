# dsb-status.ps1 - generate DSB\STATUS.md: the state of the world, in one read.
#
# Why this exists (2026-10-25): the knowledge base was 5,330 lines across fourteen documents,
# and every session started by re-reading a large slice of it to answer five questions:
#   which tune is playing, is the rig healthy, are the gates green, what changed last,
#   which documents are stale.
# None of those questions is prose - they are all already answered by files on disk. This
# reads those files and writes one page.
#
# It is generated. Do not edit DSB\STATUS.md by hand; it will be overwritten. Anything worth
# saying permanently belongs in TUNE-PLAYBOOK.md or OPEN-QUESTIONS.md instead.
#
#   powershell -File dsb-status.ps1           # writes DSB\STATUS.md and prints it
#   powershell -File dsb-status.ps1 -Print    # print only, write nothing
#
# Exit 0 always: a status page that fails to build is still better than no status page, so
# every individual read is defensive and reports what it could not see.

param([switch]$Print)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dsb  = Join-Path $root 'DSB'
$now  = Get-Date -Format 'yyyy-MM-dd HH:mm'

function Read-Json([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    try { return (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json) } catch { return $null }
}
function Age([string]$iso) {
    if (-not $iso) { return 'unknown' }
    try {
        $d = [datetime]::Parse($iso)
        $span = (Get-Date) - $d
        if ($span.TotalDays -ge 1) { return ('{0:N0} d ago' -f $span.TotalDays) }
        if ($span.TotalHours -ge 1) { return ('{0:N0} h ago' -f $span.TotalHours) }
        return ('{0:N0} min ago' -f $span.TotalMinutes)
    } catch { return 'unknown' }
}

$lines = New-Object System.Collections.ArrayList
function W([string]$s) { [void]$lines.Add($s) }

# ---------------------------------------------------------------- playing tune
$tune = Read-Json (Join-Path $root 'tunename.json')
W '# STATUS - generated, do not edit'
W ''
W ("Generated {0} by `dsb-status.ps1`. Read `START-HERE.md` first, then this page." -f $now)
W ''
W '## Playing now'
if ($tune -and $tune.name) { W ('- **DSP memory carries:** `{0}`' -f $tune.name) }
else { W '- **DSP memory:** no `tunename.json` on disk - the bridge has not remembered a name yet' }
$plan = Read-Json (Join-Path $root 'tune\tune-plan.json')
if ($plan) {
    # ConvertFrom-Json gives a LIST for an array and an OBJECT for a dictionary, and
    # @(\$obj).Count is 1 for both. Count the properties instead, and stringify a target
    # that is an array rather than printing System.Object[].
    $n = 0
    if ($plan.paths -is [System.Array]) { $n = $plan.paths.Count }
    elseif ($plan.paths) { $n = @($plan.paths.PSObject.Properties).Count }
    $tgt = $plan.target
    if ($tgt -is [System.Array]) {
        # the house curve is 15 anchors; show the shape, not the whole ladder
        $flat = @($tgt | ForEach-Object { $_ })
        $shown = 0; $parts = @()
        for ($i = 0; $i -lt $flat.Count; $i += 2) {
            $shown++
            if ($shown -le 5) { $parts += ('{0} Hz @ {1}' -f $flat[$i], $flat[$i+1]) }
        }
        $tgt = (($parts -join ', ') + (' ... (%d anchors total)' -f $shown))
    }
    elseif ($tgt -is [System.Management.Automation.PSCustomObject]) {
        $tgt = (($tgt.PSObject.Properties | ForEach-Object { '{0}={1}' -f $_.Name, $_.Value }) -join ', ')
    }
    W ('- **Last built plan:** `tune\tune-plan.json` ({0} paths, target {1})' -f $n, $tgt)
}
$mirror = Read-Json (Join-Path $root 'output-mirror.json')
if ($mirror) { W ('- **Last cmd-11 mirror:** memory {0}, {1} outputs, delays {2} ms' -f $mirror.mem, $mirror.outPathCount, (($mirror.delayMs | Select-Object -First 8) -join '/')) }
W ''

# ---------------------------------------------------------------- rig
$doctor = Read-Json (Join-Path $root 'doctor.json')
W '## Rig'
if ($doctor) {
    $v = 'unknown'; if ($doctor.verdict) { $v = $doctor.verdict }
    W ('- **doctor:** {0} ({1}, {2} fails / {3} warns), snapshot {4}' -f $v, (Age $doctor.at), $doctor.fails, $doctor.warns, $doctor.at)
    if ($doctor.dsp -eq $false) { W '- **DSP: not connected.** Everything that needs the amplifier is unverified until it is.' }
} else { W '- **doctor:** no `doctor.json` - run `doctor.ps1`' }
W ''

# ---------------------------------------------------------------- gates
W '## Gates'
$verify = Read-Json (Join-Path $root '.opencode\verify-state.json')
if ($verify) {
    $st = 'unknown'; if ($verify.status) { $st = $verify.status }
    W ('- **verify:** {0} at {1} (recipe `{2}`)' -f $st, $verify.at, $verify.recipe_source)
} else { W '- **verify:** no `.opencode\verify-state.json` - run `verify-all.ps1`' }
W '- **lint-refs:** run it - `powershell -File lint-refs.ps1` (exit 0 = every reference resolves)'
W '- **verify-all:** run it - `powershell -File verify-all.ps1` (one verdict across every gate)'
W ''

# ---------------------------------------------------------------- knowledge base
W '## Knowledge base'
$refsReadme = Join-Path $dsb 'refs\README.md'
if (Test-Path -LiteralPath $refsReadme) {
    $txt = Get-Content -LiteralPath $refsReadme -Raw
    $sources = (Get-ChildItem -Path (Join-Path $dsb 'refs') -Recurse -File -Include *.txt, *.md |
                Where-Object { $_.FullName -ne $refsReadme }).Count
    W ('- **{0} source files** under `DSB\refs\`, digested into `TUNE-PLAYBOOK.md` 9.0 onward' -f $sources)
    $trunc = ([regex]::Matches($txt, 'TRUNCATED')).Count
    $missing = ([regex]::Matches($txt, 'TRANSCRIPT NOT ON DISK')).Count
    if ($trunc -gt 0) { W ('- **{0} source(s) flagged TRUNCATED** - the digest is missing its tail' -f $trunc) }
    if ($missing -gt 0) { W ('- **{0} source(s) with no transcript on disk** - a doctrine nobody can re-read' -f $missing) }
} else { W '- **`DSB\refs\README.md` missing** - the intake protocol is gone' }
$oq = Join-Path $dsb 'OPEN-QUESTIONS.md'
if (Test-Path -LiteralPath $oq) {
    $q = (Get-Content -LiteralPath $oq) | Where-Object { $_ -match '^\|\s*(\*\*)?Q\d+' }
    W ('- **{0} open questions** - see `OPEN-QUESTIONS.md`, ranked by the decision each blocks' -f @($q).Count)
} else { W '- **`OPEN-QUESTIONS.md` missing** - the unknowns have no register' }
W ''

# ---------------------------------------------------------------- staleness
W '## Staleness'
$stale = @()
Get-ChildItem -Path $dsb -Filter *.md -File | ForEach-Object {
    $head = Get-Content -LiteralPath $_.FullName -TotalCount 40
    $stamp = $null
    foreach ($l in $head) { $m = [regex]::Match($l, 'Last verified against hardware[:\s]*\*{0,2}(\d{4}-\d{2}-\d{2})'); if ($m.Success) { $stamp = $m.Groups[1].Value; break } }
    if ($stamp) {
        try {
            $d = [datetime]::ParseExact($stamp, 'yyyy-MM-dd', [System.Globalization.CultureInfo]::InvariantCulture)
            $age = ((Get-Date) - $d).TotalDays
            if ($age -gt 7) { $stale += ('- **`{0}`** last verified against hardware {1} ({2} days ago)' -f $_.Name, $stamp, $age) }
        } catch { }
    }
}
if ($stale.Count -gt 0) { $stale | ForEach-Object { W $_ } }
else { W '- no document carries a "last verified against hardware" stamp older than 7 days' }
W ''
W '---'
W 'Regenerate with `powershell -File dsb-status.ps1`.'

$text = ($lines -join "`r`n")
if (-not $Print) {
    Set-Content -LiteralPath (Join-Path $dsb 'STATUS.md') -Value $text -Encoding UTF8
    Write-Host ("wrote DSB\STATUS.md  ({0} lines)" -f $lines.Count)
}
Write-Output $text
