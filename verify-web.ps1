# verify-web.ps1 - ds18-bridge's verification recipe, run by the verify gate
# (~/.config/opencode/bin/verify.cmd run) and recorded in .opencode\verify-state.json.
#
# The app is a single HTML file with inline JS, served by bin\ds18-bridge.exe: no bundler, no test
# suite, no type checker. Two classes of mistake have actually bitten this project, and both are
# caught here in under a second with no side effects:
#
#   1. The script does not parse. A stray brace in a 190 KB inline block means the whole app is
#      blank, and nothing else in the toolchain would notice.
#   2. The script reaches for an element that is not in the markup. getElementById() returns null
#      and the feature dies silently at the point of use - the shape chips, the run log, the
#      capture button have all been added this way.
#
# What this deliberately does NOT check: anything live. The DSP writes, the measurement run, the
# freeze, the microphone - those are verified on the rig against the real amplifier, and a green
# gate here says nothing about them (DS18_DSP_protocol.md sec.31). The C# half (src\, build.ps1,
# build-rta.ps1) is not built here either: both builds need the running exe stopped first, because
# the exe holds the DLL, so making it a gate step would report a false failure for a web edit.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$page = Join-Path $root 'web\index.html'
$fail = 0

function Say([string]$s) { Write-Output $s }
function Fail([string]$s) { Write-Output "FAIL $s"; $script:fail = 1 }

if (-not (Test-Path -LiteralPath $page)) {
  Fail "web\index.html is missing"
  exit 1
}
$html = [IO.File]::ReadAllText($page)
Say ("file    : web\index.html  " + [Math]::Round($html.Length / 1024, 1) + " KB")

# ---- 1. every inline script block parses -------------------------------------------------------
$blocks = [regex]::Matches($html, '(?s)<script(?![^>]*\bsrc=)[^>]*>(.*?)</script>')
if ($blocks.Count -eq 0) { Fail "no inline <script> block found" }

$node = (Get-Command node -ErrorAction SilentlyContinue)
if (-not $node) {
  # A check that cannot run must not pass: that is the overclaim this gate exists to stop.
  Fail "node is not on PATH, so the inline script cannot be parse-checked"
} else {
  $tmp = Join-Path ([IO.Path]::GetTempPath()) ('ds18-verify-' + [guid]::NewGuid().ToString('N') + '.js')
  # node writes its syntax error to stderr, and a native command's stderr under EAP Stop is a
  # terminating error: without this, a broken script dies with a PowerShell NativeCommandError
  # instead of reporting the parse failure it was written to report.
  $eap = $ErrorActionPreference
  $ErrorActionPreference = 'Continue'
  $i = 0
  foreach ($b in $blocks) {
    $i++
    [IO.File]::WriteAllText($tmp, $b.Groups[1].Value, (New-Object Text.UTF8Encoding($false)))
    $out = & node --check $tmp 2>&1
    $code = $LASTEXITCODE
    if ($code -ne 0) {
      Fail ("script block $i does not parse (" + [Math]::Round($b.Groups[1].Value.Length / 1024, 1) + " KB)")
      $out |
        ForEach-Object { if ($_ -is [System.Management.Automation.ErrorRecord]) { $_.Exception.Message } else { [string]$_ } } |
        Where-Object { $_ -and $_ -notmatch '^System\.Management\.Automation\.RemoteException$' } |
        Select-Object -First 6 | ForEach-Object { Say ("      " + [string]$_) }
    } else {
      Say ("parse   : block $i ok  " + [Math]::Round($b.Groups[1].Value.Length / 1024, 1) + " KB")
    }
  }
  $ErrorActionPreference = $eap
  Remove-Item -LiteralPath $tmp -ErrorAction SilentlyContinue
}

# ---- 2. every id the script reaches for exists in the markup -----------------------------------
$scriptText = ($blocks | ForEach-Object { $_.Groups[1].Value }) -join "`n"
$have = @([regex]::Matches($html, 'id="([^"]+)"') | ForEach-Object { $_.Groups[1].Value })
$want = @([regex]::Matches($scriptText, 'getElementById\("([^"]+)"\)') |
          ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
$missing = @($want | Where-Object { $have -notcontains $_ })
if ($missing.Count) {
  Fail ("the script reaches for " + $missing.Count + " id(s) that are not in the markup:")
  $missing | ForEach-Object { Say ("      #" + $_) }
} else {
  Say ("wiring  : $($want.Count) getElementById id(s) all present in the markup")
}

# ---- 3. the measurement-baseline classifier still classifies (with its own negative control) ---
# This is the check that decides whether a measurement tone may play at all: a response measured
# through a tuned EQ is a measurement of the tune. It is run twice - once as it is, and once
# mutated - because a classifier that has never been seen to fail has verified nothing. With the
# car off the page itself can only ever report "DSP not connected", so this is the only place the
# "tuned" verdict is exercised at all.
$bl = Join-Path $root 'verify-baseline.js'
if (-not (Test-Path -LiteralPath $bl)) {
  Fail "verify-baseline.js is missing, so the tune check is unverified"
} elseif (-not $node) {
  Fail "node is not on PATH, so the baseline classifier cannot be checked"
} else {
  $eap3 = $ErrorActionPreference
  $ErrorActionPreference = 'Continue'
  $out = & node $bl $page 2>&1
  $code = $LASTEXITCODE
  if ($code -eq 0) {
    Say ("baseline: " + (($out | Where-Object { $_ -match '^\d+ passed' }) -join ''))
  } else {
    Fail "the baseline classifier failed its own checks (exit $code)"
    $out | Select-Object -Last 8 | ForEach-Object { Say ("      " + [string]$_) }
  }
  $mout = & node $bl $page --mutate 2>&1
  $mcode = $LASTEXITCODE
  if ($mcode -eq 1) {
    $mfail = @($mout | Where-Object { $_ -match '^\s*FAIL ' }).Count
    Say ("baseline: negative control ok (" + $mfail + " assertion(s) failed on the mutant, as they must)")
  } else {
    Fail ("the baseline negative control did not fail (exit " + $mcode + ") - these assertions " +
          "cannot detect a broken classifier, so a pass means nothing")
  }
  $ErrorActionPreference = $eap3
}

# ---- 4. the parked flat reference memory is still reproducible --------------------------------
# PARKED (2026-10-19): sweep mode used to measure on memory 4, a copy of the tune with the EQ
# zeroed. The rig proved the device ignores memory selection over USB - cmd 12 carries the memory
# byte, the level in the same frame lands, and the playing memory does not move - so nothing can
# be measured through that memory and sweep mode now flattens the playing memory instead (see
# DS18_DSP_protocol.md section 37). The generator is kept because it is the only coded statement
# of what the reference is worth, and because it becomes useful again the moment a selector works.
$mk = Join-Path $root 'make-flat-memory.ps1'
$tune = Join-Path $root 'web\bor8f3.idsp'
if (-not (Test-Path -LiteralPath $mk)) {
  Fail "make-flat-memory.ps1 is missing, so the parked flat memory is unreproducible"
} elseif (-not (Test-Path -LiteralPath $tune)) {
  Fail "web\bor8f3.idsp is missing, so the flat memory cannot be checked"
} else {
  $tmp = Join-Path $env:TEMP ("flat4-verify-" + $PID + ".idsp")
  $eap4 = $ErrorActionPreference
  $ErrorActionPreference = 'Continue'
  & powershell -NoProfile -ExecutionPolicy Bypass -File $mk -File $tune -From 0 -To 0 -Out $tmp -Force | Out-Null
  $code = $LASTEXITCODE
  $ErrorActionPreference = $eap4
  if ($code -ne 0) {
    Fail "the flat memory could not be rebuilt from web\bor8f3.idsp (exit $code)"
  } elseif (-not (Test-Path -LiteralPath $tmp)) {
    Fail "make-flat-memory.ps1 reported success but wrote no file"
  } else {
    [xml]$fx = Get-Content $tmp -Raw
    $fm = @($fx.DP12_Param.SelectNodes('memory'))
    $nz = 0
    foreach ($o in @($fm[0].output)) {
      foreach ($e in @($o.eq)) { if ([math]::Abs([double]$e.Gain) -gt 0.05) { $nz++ } }
    }
    if ($fm.Count -ne 1) { Fail "the rebuilt flat bank has $($fm.Count) memories, not 1 (one tune per file)" }
    elseif ($nz -ne 0) { Fail "the flat sweep reference still carries $nz non-zero band(s)" }
    else { Say "sweep   : flat sweep reference rebuilds from the stock bank (0 non-zero bands)" }
    Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
  }
}

# ---- 5. sweep mode flattens the playing memory and can put it back ---------------------------
# The design that FAILED is cheap to re-introduce by accident (a memory byte in a request, a
# sweepAdopt on the status poll, a check that reads a second memory). These are text assertions,
# so they can only catch the shape of the design, not its behaviour - the behaviour is proved on
# the rig. What they guarantee is that the file still snapshots before it writes, writes back what
# it read, and no longer addresses a memory it cannot reach.
$src = [IO.File]::ReadAllText($page)
function HtmlHas([string]$needle, [string]$why) {
  if ($src.IndexOf($needle) -lt 0) { Fail ("web\index.html no longer " + $why) }
}
function HtmlLacks([string]$needle, [string]$why) {
  if ($src.IndexOf($needle) -ge 0) { Fail ("web\index.html still " + $why) }
}
HtmlHas 'function sweepModeEnter()'            'has a sweep entry point'
HtmlHas 'saving the tune '                     'snapshots the tune before flattening it'
HtmlHas 'sweepSnapSave(snap)'                  'persists the snapshot it took'
HtmlHas 'function sweepModeExit()'             'has a sweep exit point'
HtmlHas 'function sweepStoredSnap('              'refuses to replace an unfinished snapshot'
HtmlHas 'sweepEqWrite(mem, c.path, c.byPass, c.bands)' 'writes the snapshot back, byPass and all'
HtmlHas 'function sweepFlatness('              'checks the flatten actually landed'
HtmlHas 'function sweepEqSame('                'checks the restore actually landed'
HtmlHas 'function sweepBlocksTuneWrite('       'refuses a tune write while the copy is live'
HtmlHas 'var SWEEP = { on: false, busy: false, snap: null' 'keeps the snapshot in SWEEP.snap'
HtmlHas 'return api("/api/status", {}).then(function (d) {' 'reads the device before writing the master level'
HtmlLacks 'mute: !!S.mute'                     'carries a cached mute flag into a master write'
HtmlLacks 'sweepAdopt'                         'adopts a memory it cannot select'
HtmlLacks 'SWEEP.mem'                          'addresses a memory the device ignores'
HtmlLacks 'SWEEP.home'                         'believes a memory switch happened'
# ---- 6. channel names and the memory strip ----------------------------------------------------
# Both are about the same rule: the app must show what is real. A name is the label that ends up
# in the log and in every saved measurement, and the memory strip must never paint a memory the
# device did not actually switch to. Text/shape assertions only - the readback itself is proved on
# the rig.
function FunctionBody([string]$name) {
  $m = [regex]::Match($src, 'function\s+' + [regex]::Escape($name) + '\s*\([^)]*\)\s*\{')
  if (-not $m.Success) { return $null }
  $start = $m.Index + $m.Length
  $depth = 1; $i = $start
  while ($i -lt $src.Length -and $depth -gt 0) {
    $ch = $src[$i]
    if ($ch -eq '{') { $depth++ } elseif ($ch -eq '}') { $depth-- }
    $i++
  }
  return $src.Substring($start, $i - $start)
}

HtmlHas 'class="rname"'                        'has an editable name field on each channel row'
HtmlHas 'return r.ch <= maxCh'                  'lists only the outputs the device actually has'
HtmlLacks 'class="rout"'                        'prints a 0-based output number next to the channel'
HtmlLacks '"CH " + idx'                         'labels a channel from zero'
HtmlLacks '"output " + idx'                     'labels an output from zero'
HtmlHas 'function rosterSetName('              'can store a channel name'
HtmlHas 'name: (typeof r.name === "string")'   'loads saved channel names back'
HtmlHas 'ROSTER[i].type || (ROSTER[i].name'    'measures a channel that has a name but no type'
HtmlLacks 'class="rlab'                        'still paints the old fixed type label'

$rsn = FunctionBody 'rosterSetName'
if (-not $rsn) { Fail "rosterSetName() is missing, so the name field has no handler" }
elseif ($rsn -match 'rosterRender\(\)') {
  Fail "rosterSetName() rebuilds the rows, which would drop the caret while typing a name"
}

HtmlLacks 'id="memSwap"'                         'no memory strip - one tune per file, one playing slot'
HtmlHas 'id="tuneBarSelect"'                     'tune dropdown in the header (select a tune)'
HtmlHas 'id="btnTuneLoad"'                       'Load button in the header'
HtmlHas 'function tuneBarLoad('                'header Load fetches, parses and writes a tune'
HtmlLacks 'getElementById("memSwap")'          'no code reaches for the removed memory strip'
HtmlLacks 'getElementById("tuneScopeChips")'   'no code reaches for the removed scope chips'

$ab = FunctionBody 'applyTune'
if (-not $ab) { Fail "applyTune() is missing" }
else {
  if ($ab -notmatch 'var mi = 1') { Fail "applyTune() is not pinned to the playing memory" }
  if ($ab -notmatch 'doOut = true') { Fail "applyTune() no longer carries the level/delay block with the tune" }
  if ($ab -notmatch 'applyMemory\(') { Fail "applyTune() does not put the device on the playing slot first" }
  if ($ab -notmatch 'tune\.data\.memories\[0\]') { Fail "applyTune() does not read the file's single tune block" }
  if ($ab -notmatch 'tuneBands\(0') { Fail "applyTune() reads bands from the device slot instead of file block 0 (the blank-EQ bug)" }
  if ($ab -notmatch 'blank EQ never goes to the device') { Fail "applyTune() lost its empty-band-list refusal" }
}

if ($fail) { Say "verify-web: FAILED"; exit 1 }
$sw = ([regex]::Matches($src, 'function sweepModeEnt')).Count + ([regex]::Matches($src, 'function sweepModeExit')).Count
Say ("sweep   : flatten-in-place module present (" + $sw + "/2 entry+exit), selector design absent")
Say "names   : channel name field present; label falls back to the type + counter"
Say "memory  : a Load lands on the playing slot (applyMemory, device readback gated)"

Say "verify-web: ok"
exit 0
