# DreTunes

A Windows desktop window for the in-vehicle DSP rig in this folder. It shows what the car is
playing, what was measured, how the fit was proved, and what is still unproven — and it can
swap the amplifier from one tune to another **and prove afterwards that the swap happened**.

It is a *view and a gated action surface* for tools that already exist here. It is not a new
tuning implementation: `web\index.html` (the phone page) and `tune\idsp.py` remain the source
of truth, and every number on screen either comes from them or is labelled as not being
device-verified.

```
app\bin\DreTunes.exe        the built window (~490 KB)
app\build.ps1               builds it with the .NET Framework C# compiler
app\self-test.ps1           builds, then runs the self-test and its negative control
app\src\*.cs                the sources (24 files)
DSB\APP-NOTES.md            every place this app could lie, and what stops it
```

## Launch

Double-click **`DreTunes App.lnk`** (in this folder or on the Desktop), or run
`app\bin\DreTunes.exe`.

- No elevation. The manifest is `asInvoker` and every call is to `127.0.0.1`.
- The window appears first; the bridge (`:8585`) and the RTA (`:8586`) are started in the
  background only if they are down, so a cold start never shows a black screen for 25 s.
- Nothing is stopped on exit: the bridge also serves the phone page, and killing it would
  break the surface this app must not break.
- Offline. It never resolves a hostname; the only addresses are `127.0.0.1:8585` and
  `127.0.0.1:8586`.
- The window opens maximised and paints into a 1366×730 logical canvas scaled once by the
  real DPI. The layout was designed against this laptop's actual 1366×768 screen.

The pre-existing `DreTunes.ps1` / `DreTunes.vbs` launcher and the existing `DreTunes.lnk`
are untouched. The new shortcut is a separate name on purpose.

## Build

```
cd ds18-bridge\app
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\self-test.ps1
```

`build.ps1` writes `bin\build.rsp` and calls
`%windir%\Microsoft.NET\Framework64\v4.0.30319\csc.exe @build.rsp`. The response file exists
because the project path contains a space and PowerShell does not reliably re-quote
constructed arguments for a native command.

### Why this toolchain (and not something newer)

This is a deliberate choice, not a fallback that was discovered late:

- `dotnet` and MSBuild are **not installed** on this machine, and the disk has ~5 GB free with
  a standing "no internet installs" rule. A downloaded SDK is out of the question.
- The **WebView2 runtime is installed, but the managed SDK assembly is not**, and cannot be
  installed offline. A WebView2-hosted UI therefore cannot be compiled here — even though the
  runtime would happily render one.
- `csc.exe` from .NET Framework 4.8 **is** present, is the same compiler the bridge already
  builds with (`ds18-bridge\build.ps1`), and is whitelisted on this machine.

The sources are written to **C# 5** because that compiler predates string interpolation, `?.`,
`nameof` and expression-bodied members. Do not "modernise" a file without checking the build.

There are no external assemblies. JSON and `.idsp` parsing are in-repo (`src\Json.cs`,
`src\TuneFile.cs`) so there is no `JavaScriptSerializer` dependency and no schema guesswork.

## The five surfaces

| Surface | What it is for |
|---|---|
| **Tune** | Eight channels in the amplifier's own column layout: output level, high pass, low pass, delay, polarity. Each column's heading names its source (`device`, `file`, `mirror`), and the selected channel's crossover edges are marked on the response below. The horn at the head of each row is the one control on the screen: it mutes or unmutes that output by sending the complete 16-entry command-11 block, and every result of it is labelled mirror-only. The five value columns are read-only. |
| **Library** | Every `.idsp` in `web\` with its provenance, plus **Swap** (write a tune, with a guard) and **Compare** (A/B two files as prose, not adjectives). |
| **Measurements** | The capture runs on disk, one channel at a time, absolute dBFS on the 1/6-octave grid; the correction a tune carries; and the narrow-band / copied-filter analysis. |
| **Rig** | `doctor.ps1`'s verdict and rows verbatim, the four gates, revert points, and the list of what is still unproven. |
| **Showcase** | Six steps, start to finish, with no manual step and **no destructive write** — every claim on it is drawn from the same state the other surfaces use. |

Nothing auto-plays. The Showcase tour advances only when you press *Play tour*.

## What a swap actually does

Press **Swap to this tune** on the Library surface and the engine runs seven named steps,
reporting each one:

1. **Guard** — resolve the playing memory *from the device* (`/api/status`) and refuse unless
   it is the target. The override is a chip that must be armed, and it writes an `OVERRIDE`
   line to the activity log and to `%LOCALAPPDATA%\DreTunes\desktop.log`.
2. **Snapshot** — read the current EQ, the complete 16-entry command-11 block, and write
   `evidence\tune-before-<stamp>.json` in `apply-tune.ps1`'s exact shape, so
   `apply-tune.ps1 -Restore` can consume it too. (A second swap in the same second gets
   `-2`, `-3`, ... rather than overwriting a revert point.)
3. **Write EQ** — 31 bands, paths 0–7 only.
4. **Write levels** — the *complete* 16-entry block (levels, delays, polarity, mute) in one
   command, because partial writes would leave paths 8–15 at their old values.
5. **Read back** — all eight paths, off the amplifier.
6. **Diff** — readback vs the file, band by band, with `verify-device.py`'s tolerances
   (Fc ±1 Hz, gain ±0.15 dB, Q ±0.06).
7. **Verdict** — one of *verified*, *unproven*, *failed*. It never claims more than the
   readback supports.

It writes **EQ paths 0–7 and command 11 only**. It never writes crossovers, never writes the
master volume, and never touches firmware (command 17, permanently out of scope).

The Tune tab's per-channel **horn** is the only control in the app that sends command 11 outside a
swap: it mutes or unmutes one output by sending the complete 16-entry block, never a partial one.
It refuses unless the block in hand belongs to the memory the amplifier is playing at that moment.

**Restore** in the app is deliberately stricter than `apply-tune.ps1 -Restore`: it snapshots
the state it is about to replace, writes, reads back, diffs and reports — where the script
writes and exits without verification.

Command 11 has **no readback**: the amplifier cannot echo levels, delays, polarity or mute.
The app therefore labels those numbers *mirror only* and compares them against the block it
sent, never against the device. That label is not decoration; see `APP-NOTES.md`.

## What this app does not verify

- **Levels, delays, polarity, mute** — no readback exists. Mirror-only, always labelled.
- **The enable bit of each crossover** — `/api/eqread` returns both edges (frequency, slope,
  filter type) but no "this filter is on" flag, so only a tune file can say `OPEN`.
- **Time alignment** — delays are relative, not measured: there is no loopback cable.
- **The post-tune result** — the newest capture predates the tune it justifies. The curve is
  a prediction, labelled *predicted*.
- **A/B by ear** — never done in the car. The 16–17 dB bass difference is a fact in the files;
  whether it sounds better is not.
- **The sub amplifier** — fed from the head unit, outside the DSP. The one control is an
  owner assertion, and the app says so instead of drawing a row it cannot read.
- **Anything on hardware, today** — the DSP was unplugged and the UMIK-1 was not attached
  while this was built. The write path is code-complete and unit-tested against synthetic
  reads, but it has not been run against a live amplifier. `APP-NOTES.md` says exactly which
  claims are therefore still open.

## Testing

```
app\bin\DreTunes.exe --selftest          # 146 assertions, exit 0
app\bin\DreTunes.exe --selftest --mutant # negative control, exit 1 (17 assertions must fail)
app\bin\DreTunes.exe --shot out.png --surface 0..4
```

The self-test is not a smoke test. It parses the real files in this folder, runs
`tune\idsp.py curve` and compares the app's curve mirror against it at every key frequency,
re-parses `doctor.ps1 -Json`, reads `measurements\Car tune (2026-10-20)\channel N.json`, and
writes and re-reads a real snapshot in `apply-tune.ps1`'s format.

`--mutant` replaces three implementations with deliberately wrong ones — a diff that is always
clean, a guard that always allows, a curve that is always flat — and the run **must** fail.
A test that cannot fail proves nothing; `self-test.ps1` fails if the mutant run passes.

`--shot` renders one surface to a PNG without showing a window. It is how the layout was
checked against the real screen size, and it stays in the binary because a screenshot is the
only honest way to review a custom-painted UI.

## Related files

- `..\APP-BRIEF.md` — the brief this was built to.
- `..\tune\idsp.py` — the curve model this app mirrors (and the self-test compares against).
- `..\apply-tune.ps1` — the snapshot format and `-Restore` semantics mirrored here.
- `..\doctor.ps1`, `..\verify-web.ps1`, `..\tune\verify-swap-tunes.py`,
  `..\tune\verify-device.py` — the four gates, run from the Rig surface.
- `APP-NOTES.md` — the lie audit.
