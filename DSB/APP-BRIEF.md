# DreTunes Desktop — engineering brief and build directive

You are the lead engineer on an in-vehicle audio tuning rig. This document is your complete
context: the product, the existing system, the data formats, the traps, and the definition of done.
Everything you need is on this machine. Nothing here is aspirational — every capability described
below already runs today. Your job is to make it visible, operable, and presentable as a desktop
application.

Read the whole document before writing code. Then read the files listed in §11, in that order.

---

## 1. The directive

We built something unusual and then hid it inside a browser tab.

In two days a laptop, a measurement microphone and an amplifier in a car produced a tune that is
**verified, not guessed**: 57 measured bands across 8 channel paths, a fitted correction, a hardware
readback that proved every band landed, a two-tune swap library, and a preflight that refuses to
start a measurement when the microphone is wrong, the crossovers are open, or a tune is standing in
front of the mic.

All of that exists and runs. None of it is legible. Today the owner opens an IP address on a phone,
scrolls a page designed for a touchscreen, and cannot answer the three questions that matter in a
car: **what am I hearing right now, what was measured, and what is still unproven?**

Build the observatory. One Windows desktop application that makes the rig visible at a glance, that
a stranger understands in ninety seconds, and that the owner can operate at a red light without a
manual. It is also the showcase — when we demonstrate this rig to anyone, this app is what they see.

---

## 2. What the rig is

| Piece | Detail |
|---|---|
| Amplifier | DS18 DSP8.6iA, USB. Protocol is LibHoney/Wg, vendor code reused — not reverse-engineered from scratch. |
| Laptop | Windows, `LAPTOP-C7NA3SNN`. Runs the bridge and the RTA. This is the machine you build for. |
| Mic | miniDSP UMIK-1 over USB, 48 kHz. Calibration file loaded, serial 7170061, 615 points, 10.1–20016.8 Hz. |
| Stimulus | A phone, over Bluetooth, into the head unit. Pink noise or a sweep, played by a page served from the laptop. |
| Sub | Fed directly from the head unit. **Not on the DSP at all.** The app cannot see it and must never imply it can. |

**No internet in the car.** The laptop may have no network at all. Everything must work offline
against `127.0.0.1`.

### The channel model — get this right or every screen is wrong

> **This is not a copy of the path map.** `TUNE-PLAYBOOK.md` §2 is canonical for *which
> speaker is on which path*; what follows is the app's own model — 17 outputs, output 16 as
> the master chain, and the off-by-one between DSP path and user-facing channel number.
> Read §2 for the speakers and this for the wiring.

The DSP has **17 outputs**. Command 11 addresses outputs 0–15; output 16 is the master chain and is
outside that command. Only the first **8 outputs have drivers attached**, and the user-facing
channel number is one more than the DSP path:

| Channel | DSP path | Driver | Wiring |
|---|---|---|---|
| 1 | 0 | tweeter 1, front-left | stereo |
| 2 | 1 | tweeter 2, front-right | stereo |
| 3 | 2 | tweeter 3, rear-left | stereo |
| 4 | 3 | tweeter 4, rear-right | stereo |
| 5 | 4 | front 3.5" pair | mono pair |
| 6 | 5 | rear 3.5" pair | mono pair |
| 7 | 6 | front 8" pair | mono pair |
| 8 | 7 | rear 6.5" pair | mono pair |

Crossovers on the car right now: **Linkwitz-Riley 24 dB/oct**, high-pass at 1500 Hz (four tweeters),
80 Hz (both 3.5" pairs), 50 Hz (front 8"), 60 Hz (rear 6.5"). All low-passes open at 20 kHz. In the
protocol, `filterType 1` = LR and `Slope 1/2/3` = 12/18/24 dB/oct — an earlier formatter printed
`Slope*6` and mislabelled LR24 as "LR18". Do not repeat that.

### Glossary

- **Tune** — one EQ curve per output plus that output's level, delay, polarity, mute, and crossovers.
- **Memory** — the DSP holds four tune slots. One is active. The car is currently on **memory 2**
  (zero-based index 1). The device cannot be switched reliably from software; the tune on the
  active memory is what is playing.
- **`.idsp`** — the vendor tune file. It carries **four memory blocks**, 17 outputs each, 31 EQ bands
  per output. The Tune tab writes to the memory index *inside the file*, not to the device's active
  memory.
- **Roster / channel plan** — the app's mapping of channel → speaker type (Tweeter, Midrange,
  Midbass, Sub). Stored in `localStorage` under `ds18.roster`, per browser.
- **Measurement** — a per-channel swept-sine capture, reported as dBFS on a 1/6-octave grid,
  61 points, 20 Hz to 20 kHz.

---

## 3. What already exists — do not rebuild

| Component | Where | What it is |
|---|---|---|
| Bridge | `ds18-bridge\src\*.cs` → `bin\ds18-bridge.exe` | Local server on `http://127.0.0.1:8585`. Owns the USB link to the DSP, the JSON API, the control page, the phone stimulus page, and the WAV stimuli. |
| RTA engine | `src\Rta\RTAMeter.cs` → `bin\rta.exe` | Capture + FFT daemon on `:8586`. Endpoints `/rta.json`, `/devices`, `/device?n=<i>\|default`. 48 kHz, 8192-point FFT, calibration applied. |
| Control page | `web\index.html` (292 KB, inline JS) | The working UI: tabs Status, Volume, EQ, Input, Tunes, Tune, Targets. **Also the phone surface — it must keep working.** |
| Stimulus page | `web\play.html` | Phone plays stimulus over Bluetooth. |
| Tune library | `web\*.idsp` | Every `.idsp` in that folder **is** the library. Currently eight files: the two we authored (`1-car-tune-2026-10-20`, `2-bor8f3-improved`) and six inherited source tunes (`ai2`, `bor8f3`, `cb0nyk`, `tinker3`, `w6784b`, `xswmps`). Do not present the inherited ones as ours. |
| Tune tooling | `tune\idsp.py`, `report-tune.py`, `fit-tune.py`, `build-swap-tunes.py`, `verify-swap-tunes.py`, `verify-device.py` | Parse, analyse, fit, build and **verify** tunes with no hardware attached. Python 3.15, standard library only — numpy is deliberately not installed. |
| Rig ops | `DreTunes.ps1`, `preflight.ps1`, `doctor.ps1`, `apply-tune.ps1`, `flatten-tune.ps1`, `set-crossovers.ps1`, `crossover-range.ps1` | Start the stack; reconnect; readiness; snapshot-and-write; flatten and restore; crossover planning. `doctor.ps1` supports `-SelfTest`, `-Json`, `-Restore`. |
| Verification | `verify-web.ps1`, `verify.json`, `.opencode\verify-state.json` | The project's verification recipe: inline-script parse check, 141 `getElementById` wiring checks, 14 baseline assertions, a deliberate negative control, sweep-mode checks. |
| Evidence | `evidence\*.json`, `evidence\backups\*.zip` | Every destructive step leaves a named, dated revert point. |

---

## 3.1 Reference applications — match the vocabulary, beat the interaction

Treat every vendor tool here as **reference material only**: take its information model and its
words, ship none of its problems.

### Installed

**DS18 DSP 2.11** — `C:\Program Files\DS18\DS18 DSP\DS18 DSP_2.11.exe` (registry: "DS18 DSP" 1.1.8).
This is the official config editor for the amplifier you are talking to. Its decompiled source is
already in this repo: `decompiled\AP.cs` (556 KB, ILSpy output, namespaces `DP12_PC` + `LibHoneyWg`)
— read `decompiled\README.md` first. It is also the source-level answer to "what does the vendor app
do when it sends command N", and the reason the bridge reuses `LibHoneyWg.dll` rather than
re-implementing the filter maths.

Its shape, from that source:
- A tabbed WinForms config editor (`Form1`, a custom `MyTabcontrol` with its own key handling, plus a
  `SplashScreen`).
- Per-output editing arrives as **modal dialogs**, never inline: `Form_FluxEqSet` (one output's EQ
  set), `Form_Flux_AmpFreq` (amplitude/frequency — the response curve), plus `Form_AutoMute`,
  `Form_SourcePriority`, `Form_InputPassWord`, `Form_RestoreFatory`, `Form_UpgrateMcu`,
  `Form_Progressing`.
- Menu model: `Open Config File`, `Save Config File`, `Save as ...`, `Read Config From DSP`,
  `Open/Close Self Mute`, `Upgrade DSP`, `Language`, `Factory Reset`, `Encrypt DSP`,
  `Source Priority`, `Auto Mute`.
- **Vocabulary to speak**: Output Channel, Input Channel, Master Volume, **High-Level** / **RCA**
  source, **Read Config From DSP**, **Self Mute**, **Source Priority**, **Auto Mute**, Ch, dB, on/off.

Take: the vocabulary, the per-output channel model, the idea that a tune is a file you read from and
write to the device, and the response-curve view.
Reject: modal-per-function editing (four dialogs deep before you see a curve), config-file-centric
thinking, and a UI with no notion of whether a write landed. Note the evidence: their source is
dominated by Input / Output / Delay / Memory plumbing (554 / 654 / 441 / 392 hits) while EQ and
crossover appear 7 and 11 times. The curve is an afterthought in their design. In ours it is the
product.

**REW — Room EQ Wizard** — `C:\Program Files\REW\roomeqwizard.exe`. The reference for graph
literacy: axis conventions, measured-versus-target overlay, legible cursors. Match the standard;
do not clone the application.

### The Down4Sound / Kenwood reference — this is the one to match

**EZY DSP** — `C:\Users\sdotm\OneDrive\Documents\EZY_DSP_APP\EZY-DSP68.exe`.
18.8 MB, **native Qt5**, `ProductName: EZY-DSP68`, `CompanyName: Down4Sound Shop.`, v1.0.0.1, © 2024.

Naming will confuse you, so read this twice: the product ships as **two processes** — a device
launcher (`EZY-DSP`, V1.0.9) which starts the editor (`EZY-DSP68`, the DS18-branded one). The
launcher's VERSIONINFO reads `OriginalFilename: Kenwood.exe`, `ProductName: Kenwood`, v1.0.4.0,
© 2023 Kenwood. It is the **same OEM codebase sold under both brands** — Down4Sound and Kenwood.
That is the whole explanation for "it says Kenwood after decompiling". Do not treat them as two
different references, and do not be surprised by either name.

**It is the structural reference for this UI.** Its EQ page is a **31-column matrix** — band number,
Q, Gain, a vertical slider, Frequency, Bypass, per column — with the channel checkboxes in a single
row beneath it. That matrix is the thing to inherit the idea of and then beat: one screen showing the
whole curve's editable structure, rather than a dialog per band.

**The palette is already lifted from it.** `evidence\ezydsp-qss.txt` is the vendor's own Qt
stylesheet, recovered as plain text (16 KB). Our surfaces, hairlines, accent blue, curve green and
danger red all come from it. Use that file as the source of truth for the look — do not re-invent
colours, and do not approximate them from a screenshot.

Take from it: the matrix ↔ channel-strip relationship, the density, the vocabulary, the exact look.
Beat it on: it is a **config editor**, with no concept of what is currently playing, no readback
verification, no provenance for where a tune came from, tiny type, and a two-process launch you have
to run twice. Our product's entire argument is the opposite of each of those.

Assets are compiled into the binary (`:/Images/...`), so the stylesheet is the extractable design
source. Rendered screenshots were captured once at `%TEMP%\opencode\ezy-dsp68*.png` and have since
been cleaned up — if you want visual captures, take them deliberately (see the safety rule) rather
than relying on those.

### Method for any new reference app — and the one safety rule

- **Never run a vendor DSP app while the bridge holds the USB device.** Two programs writing to one
  amplifier is how a tune gets corrupted. If a vendor app must be launched, disconnect the bridge
  first (`/api/disconnect`) and confirm nothing else is attached to the DSP.
- .NET app → decompile into a room (`decompiled\`), then grep for `.Text = "`, `Menu`, `TabPage`, and
  form class names. Do not read 556 KB linearly.
- Obfuscated or non-.NET → use the REA toolkit (`rea` agent) to inspect the artifact, extract
  manifest resources, and decompile managed code. That is how `LibHoneyWg.dll` was obtained.
- Copy the information architecture and the words, not the pixels.

---

## 4. The live tune — your first real dataset

Master level −4.8 dB. Device on memory 2. Crossovers as in §2.

| Path | Driver | Tune 1 gain | Tune 2 gain | Delay (both) |
|---|---|---|---|---|
| 0 | front-left tweeter | −6.8 | −4.6 | 5.4 ms |
| 1 | front-right tweeter | −8.1 | −7.0 | 3.8 ms |
| 2 | rear-left tweeter | −9.0 | −7.0 | 6.0 ms |
| 3 | rear-right tweeter | −8.1 | −4.0 | 4.1 ms |
| 4 | front 3.5" pair | −8.3 | −3.0 | 4.6 ms |
| 5 | rear 3.5" pair | −3.0 | −3.0 | 5.0 ms |
| 6 | front 8" pair | −30.0 | −14.0 | 3.8 ms |
| 7 | rear 6.5" pair | −22.1 | −5.0 | 4.4 ms |

- **Tune 1** (`1-car-tune-2026-10-20.idsp`) — built from the measurement. 57 bands on the live paths.
- **Tune 2** (`2-bor8f3-improved.idsp`) — the owner's own B voicing, structurally repaired. 41 bands,
  every Q ≤ 2.8 (about a third of an octave) where the source had Q 10–16 (1/10–1/16 octave, narrower
  than the band spacing, which combs).
- Tune 2 is **+16 dB on the front 8" and +17 dB on the rear 6.5"** versus tune 1. That is the B tune's
  presentation, deliberate. A compare view must say so in plain English.
- One artefact to know about: the source tune had an identical −11.0 / −9.6 dB notch at 1.5 kHz on
  both 3.5" pairs. Identical values on two different pairs means one measurement was copied — almost
  certainly a microphone-position null. Tune 2 deliberately does not reproduce it; it uses a broad
  −5 / −6 dB scoop instead. Never present that notch as a property of the car.

Tune 2 is loaded and verified right now, so "what is playing" must show tune 2. Both tunes are
swap-ready in the library.

---

## 5. What you are building

**DreTunes** — a Windows desktop application. One window. No address bar, no `localhost` URL, no
browser chrome. Launched from a shortcut in a car. Offline. It starts the rig, shows the state
honestly, and drives the tune.

**Scope is three things, and only three: tuning, application, desktop.**
*Tuning* — inspect a curve, compare two tunes, see what the drivers actually do and what the
correction is doing to them. *Application* — write a tune to the amplifier, prove it landed, swap
between tunes, restore the previous state. *Desktop* — one window, process lifecycle, offline, one
action to launch in a car. Measurement **capture** stays where it is: the existing page and its run
cycle. The app reads existing captures as evidence; it does not replace the measurement flow, and a
v1 that tries to will be a worse version of something that already works.

It wraps or replaces `web\index.html` for the laptop. **`web\index.html` stays as the phone surface** —
the phone still loads `http://LAPTOP-C7NA3SNN:8585/` and emits the stimulus.

Technology is your call within §7. My bias: a WebView2 shell around the existing page is the fastest
credible path, reuses verified logic, and keeps the footprint small. `dotnet` is **not installed** on
this machine, so a full .NET desktop rewrite means new toolchain decisions — justify any such choice
in writing before you start. Whatever you choose, the shell must own process lifecycle, state, and
error reporting. A browser tab cannot.

---

## 6. The five surfaces

### Look and feel

Inherit the existing visual language; do not rebrand it. `web\index.html` defines:

```
--bg #26282c   --panel #2e2d2d   --line #2f3a41   --well #111418   --deep #0f0f0f
--fg #ededed   --dim  #999999    --accent #0babf2 --curve #84bf5d
--ok #84bf5d   --bad  #f92638     15px / 1.45 Segoe UI
```

Dark, high contrast, one accent blue, one green for measured response, red reserved for failure.
Those values are not invented — they were lifted from the vendor's own stylesheet,
`evidence\ezydsp-qss.txt` (see §3.1). Treat that file as the authority, not a screenshot.
Curves are the signature element — they are why the app exists; give them space and a real axis.

**Physical context drives the design.** The owner is in a car: daylight glare, one free hand, ten
seconds at a red light. The default screen must be readable from the driver's seat at a glance. Large
numbers, strong contrast, no 12 px diagnostic prose in the primary path. Detail lives one deliberate
action away, never on the surface you land on. Nothing auto-plays, nothing animates for decoration,
and no control that needs two hands.

### 6.1 Now — what the car is playing
The default view, readable from the driver's seat.
- Which tune is loaded, which memory it sits in, whether the write was **verified by readback**, and
  when.
- Per-path gain / delay / polarity / crossover in one strip, 8 live paths, visually weighted so bass,
  mid and high read as groups.
- Master level, mute, input source, DSP link health.
- The honest caveat wherever one exists: *"levels and delays were written; this DSP cannot echo
  command 11 back, so these are verified against the sent block, not the device."* This distinction
  is the product, not a footnote. Never render "applied" without saying how it was proven.

### 6.2 Tune library and A/B swap
The feature the owner actually uses at a traffic light.
- Every `.idsp` in `web\`, labelled with what it is: authored vs inherited, measured vs voicing,
  band count, file date.
- **Swap in one action**, with truthful progress: snapshot → write EQ → write output block → read
  back → diff → verdict. A swap that cannot be proven renders **amber, not green**.
- **Compare view**: two tunes side by side, per-path gain delta and delay delta, band counts, and one
  sentence of plain English ("tune 2 is 16 dB louder on the front 8-inch pair").
- **Wrong-memory guard.** Two traps in the current UI already produced a write that reported success
  and changed nothing audible: the memory chips are 1-based (`M2` targets device memory 3), and the
  EQ scope defaults to *"This output only."* The app must resolve the playing memory from the device,
  refuse a write whose target is not that memory, and require an explicit, logged override.
- **Level-match gate.** Two tunes are only an A/B if they are within a few dB of each other in overall
  level. Compute the predicted level difference from the two files plus the current output gains
  (`tune\system-predict.py` does exactly this, offline) and put it in the compare view as a number.
  If it exceeds ~6 dB, the app must say so in plain words — "these are not comparable by ear; tune B is
  20 dB louder in the bass" — instead of inviting a preference judgement.

### 6.3 Measurements
- Each capture run is a folder: `measurements\Car tune (YYYY-MM-DD)\`, one `channel N.json`, one
  `channel N.csv` per channel, plus a `README.md`. 21 recordings on disk, newest 2026-10-20 18:15.
- Per channel: the measured response, the fitted correction, and predicted-versus-measured on one
  axis. Channel N is DSP path N−1 — put that in the UI, do not make people remember it.
- Band table (fc, gain, Q) plus realized ripple, so a narrow band that combs *looks* narrow.
- Mark anything that is a position artefact rather than a driver property (§4).

### 6.4 Rig — the verification layer
Today this is PowerShell output. It is what makes the product defensible.
- Live readiness rows: DSP link, measurement mic (device name, sample rate, level, channel count),
  cal file, channel plan, "is a tune in the way", channel block, sub amp (manual tick only — the app
  cannot see that amplifier and must never pretend to).
- Crossover map against a named baseline snapshot, per path, with the check's provenance.
- Revert points: every snapshot on disk, newest first, each restorable with a verified write.
- **The unproven list, stated plainly**: time alignment (no loopback cable yet), post-tune measurement
  pending, ride comparison pending. A rig that lies about its own gaps is worthless.

### 6.5 Showcase
A scripted ninety-second tour for a stranger: rig state → a measurement's evidence → the fit → the
apply with its readback proof → the A/B swap. No slide deck. It must run without the car moving and
without a destructive write.

### 6.6 Prediction — what a tune will sound like before it is written
The capability the 2026-10-21 review found missing, and the one that would have caught a bad tune
before it reached the car. All inputs are local files; no device, no measurement, instant.
- **Model**: `predicted_i(f) = measured_i(f) − gain_at_measurement_i + gain_tune_i + eq_tune_i(f)`.
  Valid only while the tune's crossovers match the measurement's — verify that per path and refuse
  to draw a curve for a path that does not match. Reference implementation: `tune\system-predict.py`.
- **Show three curves on one axis**: the car as measured (EQ flat), the selected tune, and the house
  target. Under them, the residual per band and one headline number — rms residual 60 Hz–10 kHz.
- **State the uncertainty where it exists.** Summing eight paths from magnitude-only captures cannot
  know arrival phase: draw the energy sum as the line and the coherent sum as a band. Where the band
  is narrow (one path owns the band) the curve is trustworthy; where it is 6–9 dB wide, label it.
- **Gate writes on it**: a tune whose residual is no better than the flat measurement is a null move,
  and the app must say so before the user swaps to it.


---

## 7. Non-negotiables

1. **Offline.** No CDN, no web fonts, no telemetry, no cloud, no AI calls. It must work with the
   network off.
2. **Footprint.** Target under ~80 MB installed. **4.9 GB free on this disk** is a standing constraint.
   No `node_modules`, no Python venv, no SDK install to answer a question.
3. **Never claim an unverified write.** EQ and crossovers are read back off the device — demand that.
   The output block (command 11) has **no readback** on this hardware; say so every single time.
4. **Never write crossovers or master from a tune path.** Crossovers are command 13 and the tune path
   does not touch them. Firmware upgrade (command 17) is permanently out of scope — brick risk.
5. **Do not break the phone.** `web\index.html` and `web\play.html` stay served and functional.
6. **No automation on the phone.** Owner rule: never inject taps or swipes into that device.
7. **Every new capability gets a self-test that has been seen to fail.** `doctor.ps1 -SelfTest` is the
   model. A comparator never observed to fail is not evidence.
8. **Preserve the tooling.** `tune\verify-device.py`, `verify-swap-tunes.py`, `verify-web.ps1` and
   `doctor.ps1` stay runnable from a terminal and remain the source of truth the UI reports. The app
   surfaces their results; it does not reimplement their logic and create a second truth.
9. **The car is the only test bench.** A wrong write is audible immediately and may damage drivers.
   Read-only first. Every write snapshots before it writes and verifies after.

---

## 8. Known traps — these have all cost real time

| # | Trap | Consequence |
|---|---|---|
| 1 | `dotnet` is not installed; the bridge is compiled with `csc.exe` from .NET Framework 4.x at **C# 5** language level | No string interpolation, no `?.`, no `nameof`, no expression-bodied members. `build.ps1` / `build-rta.ps1` are the build. |
| 2 | The running exe holds an exclusive lock on `bin\` binaries | Stop `ds18-bridge.exe` / `rta.exe` **before** building, or the build fails with a file lock. |
| 3 | The project path contains a space | The existing builds pass a response file (`.rsp`) to `csc` because PowerShell does not requote native arguments. Do not replace them with direct argument arrays. |
| 4 | `ServeStatic` uses `ctx.Request.Url.AbsolutePath` verbatim (still escaped, lowercased) | A served filename containing a space 404s. Keep tune filenames space-free, or fix the handler with `Uri.UnescapeDataString`. |
| 5 | `/api/eqread` does **not** echo the path it read | Anything comparing reads to a snapshot must pair by loop index. Filtering on `$_.path` silently matched only path 0 and made a crossover check fail open for seven paths. |
| 6 | Command 11 has no readback | Levels, delays, polarity and mute are never device-confirmed. Report them as mirror-verified only. |
| 7 | Memory chips are 1-based; EQ scope defaults to one output | A write can "succeed" into the wrong memory and change nothing audible. Guard it. |
| 8 | An `.idsp` carries four memory blocks and the Tune tab writes the **file's** memory index | A file that fills only block 0 writes flat bands if another memory is selected. Our two files repeat the tune in all four blocks. |
| 9 | The run checks only poll while the Tune tab is open (`showTab('rta')` → `rtaStart()`) | A desktop app that hides that tab must keep the polling alive itself. |
| 10 | `szPName` truncates device names at 31 characters | Name matching must be tolerant; never match a device on a full string. |
| 11 | PowerShell: variable names are case-insensitive, `Start-Process -ArgumentList` arrays break names containing spaces, files written with a BOM break Python, and heredocs do not work | Each has produced a false success or a silent failure here. |
| 12 | Python is 3.15 and the tooling is stdlib-only | Do not add a dependency to solve a plotting or fitting problem. |

---

## 9. Data formats and interfaces

**Measurement file, `channel N.json`:**
`{"channel":"tweeter 1","channelNumber":1,"run":"Car tune (2026-10-20)","capturedAt":"2026-10-20T19:12:13-04:00","units":"dBFS","grid":"1/6 octave, 61 points, 20 Hz to 20 kHz","peak":{"dbfs":-46,"hz":4561.4},"sig":{"ok":true,"bands":33,"rising":97,"from":453,"to":20480,"burstDb":71.6,"reason":"…"},"points":[[20,-72.3],…]}`

Each channel also has a `.csv` with the same grid, and each run folder has a `README.md`.

**Bridge API on `:8585`** (authoritative route table: `src\BridgeServer.cs`, ~line 335):
`/api/info`, `/api/status`, `/api/connect`, `/api/disconnect`, `/api/volume`, `/api/master`,
`/api/input`, `/api/output`, `/api/eq`, `/api/eqread`, `/api/eqprobe`, `/api/eqcoef`, `/api/passband`,
`/api/dspcfg`, `/api/targets`, `/api/tunes`, `/api/tunename`.

Note: `/api/info` carries the command-11 block (`outputGainDb`, `outputDelayMs`, `outputMute`);
`/api/status` does not. Reading status where info is needed is a bug that has already been fixed once.

**RTA on `:8586`**: `/rta.json` (device, ch, devOpenIdx, devDrift, status, fs, fft, levelDb, cal),
`/devices`, `/device?n=<i>|default`.

**Tune files**: parse them with `tune\idsp.py` rather than writing a second parser. It has
`dump`, `curve`, `bands`, `diff` and `json` subcommands and computes RBJ peaking response at 48 kHz.

**The verification counts you will be reporting on screen** — do not invent your own metric:
a device readback against a tune file is **42 assertions** (`tune\verify-device.py`); the swap-library
integrity gate is **46 assertions** (`tune\verify-swap-tunes.py`); the UI gate is **14 baseline
assertions plus a deliberate negative control** (`verify-web.ps1`); the readiness comparator's
self-test is **4 cases** (`doctor.ps1 -SelfTest`). Those are the numbers a user should see, in those
words, with a pass/fail and a timestamp.

---

## 10. Definition of done — scenarios that must be demonstrated

1. **Cold start.** Launch from a shortcut with no browser open → window in under 3 s → rig state
   shown. If the bridge or RTA is down, the app starts them and says so. If the DSP is unplugged, it
   says exactly that.
2. **Swap with proof.** A→B: snapshot, write, read the device back, diff against the file, show the
   assertion count with any mismatch named. Unprovable swaps render amber.
3. **Wrong-memory guard.** Writing a memory the device is not playing is blocked by default with a
   named reason. Override possible and logged.
4. **DSP unplugged mid-swap.** Last known state with a timestamp, writes disabled, reconnect offered.
   No stale green.
5. **No mic.** The measurement surface explains the failure and the fix (its own USB port, not the hub
   shared with the DSP) and does not offer a Run that will waste a cycle.
6. **Compare.** The two current tunes produce a per-path gain/delay table and one plain-English
   sentence about the difference.
7. **Network off.** Everything above still works.
8. **Showcase.** The ninety-second tour runs start to finish with no manual step.
9. **The phone still works.** Phone connects to `:8585`, plays stimulus, page unchanged.
10. **The honesty layer.** With the DSP unplugged and a stale tune on screen, nothing anywhere in the
    UI claims a verified state.
11. **It speaks DSP.** A DSP-literate stranger needs no glossary. The app uses the ecosystem's own
    words — Output Channel, Master Volume, Read Config From DSP, Self Mute, Source Priority — and its
    EQ surface has the structure a Down4Sound or Kenwood user recognises on sight: the whole curve's
    editable cells in one matrix, with the channel strip directly beneath.

---

## 11. Orientation — read these, in this order

> **Superseded 2026-10-25.** This list diverged from the one in `TUNING-BRIEF.md` §4.5 and
> neither mentioned the knowledge base. **`START-HERE.md` is now the single orientation**
> for DSP and tuning context. What follows is app-specific and still applies: it is the
> order for somebody wrapping the existing UI, not for somebody tuning the car.

App-specific order:

1. `START-HERE.md` — the project orientation (read this first, always).
2. `ds18-bridge\web\index.html` — the existing UI and the logic you are wrapping.
3. `ds18-bridge\src\BridgeServer.cs` — the API and the DSP command surface.
4. `ds18-bridge\tune\idsp.py` and `tune\report-tune.py` — tune format and analysis.
5. `ds18-bridge\doctor.ps1` — the readiness model, and the tone to match: prove it or say you cannot.
6. `ds18-bridge\verify-web.ps1` — how this project verifies a UI change.

Commands you will need:

```
.\DreTunes.ps1                     # start bridge + rta
.\preflight.ps1                    # reconnect after a USB re-plug
.\doctor.ps1                       # readiness, human-readable
.\doctor.ps1 -Json                 # readiness, machine-readable
.\doctor.ps1 -SelfTest             # prove the comparators still catch mutations
powershell -File .\verify-web.ps1  # UI parse + wiring + baseline checks
python .\tune\verify-device.py .\web\2-bor8f3-improved.idsp --mem 1
python .\tune\verify-swap-tunes.py
```

---

## 12. Do not

- Do not redesign the DSP protocol layer. It is verified hardware behaviour; treat it as fixed.
- Do not introduce a second tuning implementation. If a script does it, call it or mirror it.
- Do not ship a browser shortcut and call it a desktop app.
- Do not hide unproven things to make a cleaner screenshot.
- Do not delete or overwrite evidence snapshots. `crossover-range.ps1` refuses to overwrite one —
  that instinct is correct; copy your way forward, never overwrite your way forward.
- Do not ask the owner to test anything in the car that you could have verified on disk.

---

## 13. Sequencing

1. Shell, lifecycle, state — surfaces 6.1 and 6.4, **read-only**. No writes.
2. Library and compare — 6.2, read-only.
3. Swap: snapshot, write, readback, diff, memory guard.
4. Measurements browser — 6.3, reading existing folders. No new capture path.
5. Showcase — 6.5.
6. Only then, anything that changes how a measurement is taken.

Ship 6.1 and 6.4 first. If the owner sits in the car, opens this, and it tells him the truth about
what he is hearing, we have already won. Everything after that is depth.

---

## 14. Deliverables

- Application source in `ds18-bridge\app\`, with a `README.md` stating how to build it, how to launch
  it, and what it does not verify.
- A built artifact and a shortcut that works without an elevated shell.
- No new runtime dependency that needs the internet to install.
- A short `APP-NOTES.md`: what you built, what you chose not to build, and every place where the app
  could be lying to a user — so the next engineer knows where to look.

When you are unsure about a hardware behaviour, stop and read `STATE.md` and `TUNE-PLAYBOOK.md`
before guessing. If they do not answer it, ask. A wrong guess about this amplifier is audible in a
car within seconds.
