# lint-refs.ps1 - prove that every reference in the knowledge base actually resolves.
#
# Why this exists (2026-10-25): broken references were being found only by accident, during
# unrelated tasks. Three examples, all real:
#   * DS18_DSP_protocol.md - reported as "does not exist in this project". It exists, in the
#     PARENT folder (Default Project\), and nothing in ds18-bridge pointed at it.
#   * iya1m4.idsp - cited as the source of a measurement, not present in this library.
#   * DSB\refs\ level-matching transcript - digested into the playbook, never saved to disk.
# A reference nobody can follow is the failure mode TUNE-PLAYBOOK.md section 15 exists to
# prevent, and it was happening anyway. Discipline did not hold; a check will.
#
# Design note - precision over recall. The first version of this reported 339 problems of
# which about 330 were noise: API routes, URLs, UI labels, the bare token ".idsp". A gate
# that cries wolf is ignored, which is exactly as useful as no gate. So the token filter is
# deliberately strict: it only scores things that are unambiguously meant to be a file.
# What it costs in recall, the R2/R3 checks buy back on the paths that matter most.
#
# Checks
#   R1  a path written in backticks or as a markdown link resolves to a real file
#   R2  every file under DSB\refs\ is named in the refs index, and every indexed source
#       file exists
#   R3  every playbook section the refs index claims (e.g. "9.0.3") has a heading
#   R4  every dated "Last verified against hardware" stamp parses as a date
#   R5  files cited by bare name (report-tune.py) exist somewhere in the repo
#
# Read-only: opens nothing on a device, writes nothing, talks to nothing.
#
#   powershell -File lint-refs.ps1            # human report, exit 0 = clean
#   powershell -File lint-refs.ps1 -Json      # machine report
#
# Exit 0 = every reference resolves. Exit 1 = something does not.

param([switch]$Json)

$ErrorActionPreference = 'Stop'
$root   = Split-Path -Parent $MyInvocation.MyCommand.Path
$parent = Split-Path -Parent $root
$fail   = New-Object System.Collections.ArrayList
$warn   = New-Object System.Collections.ArrayList

function Add-Problem([string]$kind, [string]$where, [string]$what, [string]$detail) {
    [void]$script:fail.Add([pscustomobject]@{ kind = $kind; where = $where; ref = $what; detail = $detail })
}

$extRe = '\.(md|ps1|py|js|ts|cs|json|idsp|txt|log|cmd|vbs|html|csv|wav|png|ico)$'

# Every file name in the WORKSPACE, by leaf, for the bare-name check (R5).
# The workspace is the project folder PLUS its parent: two documents that were reported as
# "does not exist in this project" - DS18_DSP_protocol.md and decompiled\AP.cs - both exist
# one directory up. Indexing only $root is what made that wrong verdict possible, so the
# index deliberately spans the parent too.
$leafIndex = @{}
Get-ChildItem -Path $root -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '\\(vendor|bin|__pycache__|node_modules)\\' } |
    ForEach-Object { $leafIndex[$_.Name] = $_.FullName }
Get-ChildItem -Path $parent -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notlike "$root\\*" -and $_.FullName -notmatch '\\(node_modules|__pycache__|\.git)\\' } |
    ForEach-Object { if (-not $leafIndex.ContainsKey($_.Name)) { $leafIndex[$_.Name] = $_.FullName } }

# Directories outside the workspace that documents legitimately point at. The tune library
# in this repo is twelve files, but README.md verifies the browser parser against
# C:\Users\sdotm\Downloads\*.idsp - a different, larger set. A reference that resolves here
# is not broken; it is cross-boundary, and R5 reports it as a warning instead of a failure.
$externalDirs = @('C:\Users\sdotm\Downloads')
foreach ($d in $externalDirs) {
    if (Test-Path -LiteralPath $d) {
        Get-ChildItem -Path $d -File -ErrorAction SilentlyContinue |
            ForEach-Object { if (-not $leafIndex.ContainsKey($_.Name)) { $leafIndex[$_.Name] = $_.FullName } }
    }
}

# Deliberate negatives: the document says the file does not exist, so scoring it would be
# scoring the point of the sentence as an error.
$allow = @{
    'rta.json' = 'README: the capture thread deliberately never writes it - that is the section'
}

# ---------------------------------------------------------------- token filter
# Returns $null when the token cannot be a repo file reference, otherwise the token.
function Select-Token([string]$tok) {
    $t = $tok.Trim()
    if ($t -eq '') { return $null }
    if ($t -match '[*<>$\\{\\}|]')  { return $null }        # glob, template, variable
    if ($t -like '/*')              { return $null }        # a server route, not a file
    if ($t -match '%')              { return $null }        # URL-encoded
    if ($t -match '\.\.')           { return $null }        # elision or a range: 1..4, ...
    if ($t -match 'YYYY|channel N') { return $null }        # placeholder
    if ($t -match '^\d')             { return $null }        # a section number or a date
    if ($t -match '^\s*(GET|POST|PUT|DELETE)\s') { return $null }
    if ($t -match '^(https?:|mailto:|ftp:|git:)') { return $null }
    if ($t -match '^github\.com')    { return $null }        # external by definition
    if ($t -like '/api/*' -or $t -like 'api/*') { return $null }
    if ($t -like '*/api/*')          { return $null }
    if ($t -like ':/*')              { return $null }        # Qt resource
    if ($t -like '*rta.json' -and $t -like '/*') { return $null }
    if ($t -like '/rta*' -or $t -like '/devices*' -or $t -like '/status*') { return $null }
    if ($t -match '^[A-Za-z]:\\')    { return $null }        # absolute path outside the repo
    if ($t -match '\s')              { return $null }        # a label, not a path
    if ($t -match '^\.[a-z0-9]+$')   { return $null }        # the bare token ".idsp"
# A repo file reference always names a file. "audio/wav", "fc/20", "sig.from/to",
# "pierreaubert/autoeq" and "RunStatus/mainMemory" are MIME types, field paths and a GitHub
# slug - none of them end in a known extension, so requiring one removes the whole class.
    $leaf = ($t -split '[\\/]')[-1]
    if ($t -notmatch '[\\/]') { $leaf = $t }
    if ($leaf -notmatch $extRe) { return $null }
    if ($t -match '[\\/]') { return $t }                     # a path - resolve it
    return ('BARE:' + $t)                                    # a bare name - look it up
}

function Resolve-RepoPath([string]$token, [string]$fromDir) {
    $t = $token -replace '/', '\'
    $candidates = @()
    if ($t -match '[\\/]') { $candidates += (Join-Path $fromDir $t) }
    $candidates += (Join-Path $root $t)
    if ($t -like 'ds18-bridge\*') {
        $strip = $t.Substring('ds18-bridge\'.Length)
        $candidates += (Join-Path $root $strip)
        $candidates += (Join-Path $fromDir $strip)
        $candidates += (Join-Path $parent $strip)
    }
    $candidates += (Join-Path $parent $t)     # DS18_DSP_protocol.md genuinely lives one level up
    foreach ($c in $candidates) {
        if (Test-Path -LiteralPath $c) { return $c }
    }
    return $null
}

# ---------------------------------------------------------------- extract
function Get-CandidateTokens([string]$line) {
    $out = @()
    foreach ($m in [regex]::Matches($line, '`([^`]+)`')) { $out += $m.Groups[1].Value }
    foreach ($m in [regex]::Matches($line, '\[[^\]]*\]\(([^)]+)\)')) {
        $u = $m.Groups[1].Value
        if ($u -notmatch '^(https?:|#|mailto:)') { $out += $u }
    }
    return $out
}

# ---------------------------------------------------------------- R1 / R5: paths in markdown
$mdFiles = Get-ChildItem -Path $root -Recurse -Filter *.md -File |
           Where-Object { $_.FullName -notmatch '\\(vendor|bin|__pycache__|node_modules)\\' }

$checked = 0
foreach ($md in $mdFiles) {
    $dir = $md.DirectoryName
    $lines = Get-Content -LiteralPath $md.FullName
    for ($i = 0; $i -lt $lines.Count; $i++) {
        foreach ($raw in (Get-CandidateTokens $lines[$i])) {
            $tok = Select-Token $raw
            if ($null -eq $tok) { continue }
            $checked++
            if ($tok -like 'BARE:*') {
                $leaf = $tok.Substring(5)
                if ($allow.ContainsKey($leaf)) { continue }
                if (-not $leafIndex.ContainsKey($leaf)) {
                    Add-Problem 'R5' "$($md.Name):$($i+1)" $leaf 'cited by name but no such file anywhere in the workspace'
                } elseif ($leafIndex[$leaf] -notlike "$root\*") {
                    [void]$warn.Add("$($md.Name):$($i+1)  '$leaf' resolves outside the project -> $($leafIndex[$leaf])")
                }
            } else {
                if ($null -eq (Resolve-RepoPath $tok $dir)) {
                    Add-Problem 'R1' "$($md.Name):$($i+1)" $tok 'path does not resolve'
                }
            }
        }
    }
}

# ---------------------------------------------------------------- R2: refs index <-> refs folder
$refsDir    = Join-Path $root 'DSB\refs'
$refsReadme = Join-Path $refsDir 'README.md'
if (-not (Test-Path -LiteralPath $refsReadme)) {
    Add-Problem 'R2' 'DSB\refs\README.md' 'index' 'the knowledge-base index is missing'
}
$idxText = ''
if (Test-Path -LiteralPath $refsReadme) {
    $idxText = Get-Content -LiteralPath $refsReadme -Raw

    foreach ($m in [regex]::Matches($idxText, '`([^`]+\.txt)`')) {
        $tok = $m.Groups[1].Value
        if ($tok -notmatch '[\\/]') { continue }
        if ($tok -match '[*<>$\\{\\}|]') { continue }
        if ($null -eq (Resolve-RepoPath $tok $refsDir)) {
            Add-Problem 'R2' 'refs index' $tok 'indexed source file does not exist'
        }
    }

    $files = Get-ChildItem -Path $refsDir -Recurse -File -Include *.txt, *.md |
             Where-Object { $_.FullName -ne $refsReadme }
    foreach ($f in $files) {
        $rel = $f.FullName.Substring($refsDir.Length + 1)
        if ($idxText -notmatch [regex]::Escape($f.Name)) {
            Add-Problem 'R2' 'refs index' $rel 'file exists on disk but is not in the index'
        }
    }
}

# ---------------------------------------------------------------- R3: claimed playbook sections exist
$playbook = Join-Path $root 'DSB\TUNE-PLAYBOOK.md'
if (-not (Test-Path -LiteralPath $playbook)) {
    Add-Problem 'R3' 'DSB\TUNE-PLAYBOOK.md' 'playbook' 'the playbook is missing from DSB\'
}
if ($idxText -ne '' -and (Test-Path -LiteralPath $playbook)) {
    $pb = Get-Content -LiteralPath $playbook -Raw
    $claimed = @()
    foreach ($m in [regex]::Matches($idxText, '(?:section|§)\s*(9\.\d+(?:\.\d+)?)')) { $claimed += $m.Groups[1].Value }
    foreach ($sec in ($claimed | Select-Object -Unique)) {
        $escaped = [regex]::Escape($sec)
        if ($pb -notmatch "(?m)^#+\s*$escaped\b") {
            Add-Problem 'R3' (Split-Path -Leaf $playbook) "section $sec" 'cited by the refs index but no such heading exists'
        }
    }
}

# ---------------------------------------------------------------- R4: dated stamps parse
foreach ($md in $mdFiles) {
    $head = Get-Content -LiteralPath $md.FullName -TotalCount 40
    foreach ($line in $head) {
        foreach ($m in [regex]::Matches($line, 'Last verified against hardware[:\s]*\*{0,2}(\d{4}-\d{2}-\d{2})')) {
            # ParseExact in a try/catch: PowerShell 5.1 will not resolve the five-argument
            # TryParseExact overload with an [ref] out-parameter, and a lint must never die.
            $ok = $false
            try {
                [void][datetime]::ParseExact($m.Groups[1].Value, 'yyyy-MM-dd',
                    [System.Globalization.CultureInfo]::InvariantCulture)
                $ok = $true
            } catch { $ok = $false }
            if (-not $ok) { Add-Problem 'R4' $md.Name $m.Groups[1].Value 'stamp is not a parseable date' }
        }
    }
}

# ---------------------------------------------------------------- report
$result = [pscustomobject]@{
    checked   = $checked
    documents = $mdFiles.Count
    broken    = @($fail)
    warnings  = @($warn)
    clean     = (@($fail).Count -eq 0)
}

if ($Json) {
    $result | ConvertTo-Json -Depth 6
} else {
    "lint-refs: $checked references scored across $($mdFiles.Count) documents"
    if ($warn.Count -gt 0) { ""; "WARNINGS:"; $warn | ForEach-Object { "  $_" } }
    if ($fail.Count -gt 0) {
        ""; "BROKEN ($($fail.Count)):"
        $fail | ForEach-Object { "  [$($_.kind)] $($_.where)  ->  $($_.ref)`n        $($_.detail)" }
        ""; "lint-refs: FAIL"
        exit 1
    }
    ""; "lint-refs: PASS - every scored reference resolves"
    exit 0
}
