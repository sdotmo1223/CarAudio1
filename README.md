# ds18-bridge

LAN bridge that lets you drive a **DS18 DP12 DSP** (`VID_1483&PID_1095`) from a phone web
browser, instead of the discontinued DS18 desktop app over USB.

The laptop keeps the USB connection; the phone talks to a small HTTP server on the laptop.

## Build

No .NET SDK is required — this machine only has .NET Framework 4.8, so the sources are
compiled with the stock `csc.exe` (C# 5).

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

Vendor DLLs live in `vendor\` and are copied to `bin\` automatically. `LibHoneyWg.dll`
computes the EQ biquad coefficients — it is **reused**, not re-implemented, so the filter
math is identical to the official app by construction.

## Run

```powershell
cd bin
.\ds18-bridge.exe --selftest   # 126 protocol checks, no hardware needed
.\ds18-bridge.exe --serve      # start the web bridge (default port 8585)
```

Then open `http://localhost:8585/` on this PC, or `http://<laptop-ip>:8585/` on the phone.

### Phone access needs a URL ACL

Windows only lets elevated processes bind `http://*:8585/`. Without a rule the server falls
back to `http://localhost:8585/` (this PC only) and prints the fix. Run once from an
elevated prompt:

```text
netsh http add urlacl url=http://*:8585/ user=Everyone
```

Find the laptop's IP with `ipconfig` (look for the IPv4 address on the same network as the
phone).

## API

| endpoint | purpose |
|---|---|
| `GET /api/info` | connection state, cached values, band frequencies |
| `POST /api/connect` | open the USB device, read the group mask and status |
| `POST /api/disconnect` | release the device |
| `POST /api/status` | read master/mute/source/memory/groups (cmd 12 read) |
| **`POST /api/master`** | **master volume, mute, source — cmd 12. Use this for volume.** |
| `POST /api/volume` | per-output group gain — cmd 18 |
| `POST /api/input` | input enable mask, per-input gain and delay — cmd 9 |
| **`POST /api/output`** | **per-output mute / level / delay / polarity — cmd 11. Requires the complete block.** |
| `POST /api/eq` | write 31 EQ bands — cmd 14 |
| `POST /api/eqread` | read EQ bands back — cmd 15 sub 3 |
| `POST /api/eqprobe` | **sweep all 17 EQ paths and compare them** — see below |
| `POST /api/eqcoef` | compute one band's coefficients locally, no USB |

> **`/api/eqprobe` answers the open capacity question.** It reads the EQ block at paths
> 0–15 and 16 for the current memory and FNV-1a fingerprints each reply, then reports two
> independent findings:
>
> - **outputs distinct from output 0** → per-output EQ is genuinely independent, so each
>   driver gets its own 31 bands. If they are *not* distinct, every output shares one curve
>   and per-driver correction is unavailable on that unit.
> - **path 16 distinct from path 0** → a separate 31-band master stage exists. Whether it
>   sits *in series* is inferred from the vendor's model, not proven by listening.
>
> Each path is read up to **3 times** before being recorded as silent, because two hosts
> sharing EP1 IN will otherwise drop replies — measured at 37% and 50% success while
> `DS18 DSP_2.11` was running. A single miss would otherwise be reported as "path 0 did not
> reply" and kill the sweep, which is a contention artefact rather than a finding. The
> sweep still aborts immediately if path 0 fails all 3 attempts, and it is deliberately
> **not** run on connect: worst case is 17 × 3 round trips at a 1.5 s timeout, so connect
> must return immediately. Results surface in the Status tab and as a capacity line in the
> EQ tab, with per-path hashes and body lengths for independent checking.
>
> **Result on this unit (mem 1, run twice, identical):** paths 0–7 all mutually distinct →
> **8 independent outputs**; paths 8–15 byte-identical → unpopulated; path 16 distinct →
> separate master chain. See `evidence\eqprobe-mem1.json`.

> **Volume is `/api/master`, not `/api/volume`.** Command 12 is what the official app's
> master slider sends, and it is the only command that audibly changes loudness. Command 18
> is per-output group gain: the DSP stores it and echoes it back correctly, but it does
> nothing unless the output mask — which is read from the device, not invented by the host —
> includes an output you are actually listening to.

### Input source indices

These are `bSource` / `sourceIndex`, taken from the vendor's own `DP12_Param` constants:

| index | input |
|---|---|
| 0 | High-level (speaker-level) |
| 1 | Low-level (line-level) |
| 2 | Aux |
| 3 | Optical |
| 4 | Bluetooth + USB |
| 5 | USB |

Index 4 being a combined Bluetooth/USB entry mirrors `multiFlag`, which the firmware sets
whenever the source is BT or USB. `/api/status` returns `sourceName` so callers never have
to hard-code this table.

### Parameter limits

| parameter | range |
|---|---|
| master volume | -60 … 0 dB |
| group gain | **-6 … +6 dB** |
| input gain | -20 … 0 dB |
| output gain | -40 … 0 dB (default -20) |
| EQ gain | -25 … +10 dB |
| EQ Q | 0.1 … 20.0 |
| input delay | 0 … 25 ms |

## Safety

- **The firmware/MCU upgrade command (17) is deliberately not implemented.** Upgrading a
  discontinued unit risks bricking it with no recovery path.
- **Command 11 (output config) is all-or-nothing.** It carries mute, level, delay and
  polarity for all sixteen outputs plus the four group levels and the membership mask in a
  single frame, and the DSP applies every one of them. An omitted entry is written as zero —
  and a zero gain is 0 dB *full level*, not "leave it alone" — so `/api/output` refuses any
  block that is not complete rather than padding it. Because no read sub-command covers
  output config, a memory is marked "known" only after a frame is actually transmitted; a
  dry run builds and returns the bytes but never updates the mirror.
- Every write command is built byte-for-byte from the decompiled official app, so it sends
  what the vendor software would have sent.
- Volume and EQ changes take effect on the DSP immediately. Start with small changes.

## What is proven vs. assumed

`--selftest` (162 checks) proves the port is *faithful to the decompiled source*: struct
sizes, frame layout, CRC, gain encodings, status decode, EQ pack/unpack, the sub-3 body
layout, the sub-1 group-mask offset, the cmd 12 builder, the DP12_Param range clamps, the
input source map, the sign-magnitude EQ gain decode, a Q24 encode/decode round-trip
through the vendor's own inverse function, and the FNV-1a fingerprint used by
`/api/eqprobe` (pinned against the standard `""` / `"a"` / `"foobar"` vectors, plus a
check that every single-bit change to a body changes the hash).

The cmd 11 builder is covered too: the 88-byte frame and its `DspOutMem` field offsets,
the `floor(-dB*10)` / `floor(ms*100)` encodings (including that a delay of 1.239 ms lands
on 123, not 124), the enable mask's "bit set = live" rule, and a refusal to decode the sub
4 *boot* variant at the offset that belongs to the app variant — a wrong offset there would
feed a bogus channel count straight into a mute mask.

Verified **against the real device** (`USB\VID_1483&PID_1095`, service `libusbDspAi`):

- device enumerates and binds to the expected driver;
- USB open, interface claim and EP1 IN/OUT all succeed;
- TX frames are accepted by the firmware;
- the RX length-field asymmetry is real — confirmed on two frames of different sizes;
- the two-call RX CRC chain is correct;
- `StructRunStatus` decodes field-for-field (master `-3.0 dB`, mem 1, source 0, groups 0 dB);
- the sub-3 body is exactly 1044 bytes, matching `872 + 168 + 4`;
- **cmd 12 master volume is audible** — writing -12.0 dB produced a clear level drop and
  read back as -12 dB;
- **cmd 14 EQ write persists** — `+6.0 dB` on one band read back exactly, other 30 unchanged;
- **EQ reads are correct at path 0** — mem 1 returns the exact ISO 1/3-octave ladder
  (20 Hz … 20 kHz) and mem 2 reads flat at 0 dB.

Confirmed parameters: the firmware **never ACKs writes** (cmds 12, 14, 18 all land silently),
so the bridge treats a missing reply as normal.

### Ranges are enforced because they must be

`DP12_Param` limits are now clamped in the codec and mirrored in the phone UI. This is not
politeness — the encoders cast straight to a byte with no range check, so out-of-range values
wrap silently instead of failing. Group gain is only **±6 dB**, and input gain is the sharp
one: because bit 7 means "negative", a value just past 0 dB does not wrap, it converts an
attenuation into a boost.

Still unconfirmed:

2. **Whether cmd 18 is ever sufficient on its own.** It writes and reads back fine, but
   produced no audible change in the live test; cmd 12 did.
3. **Out-of-spec EQ values.** mem 1 / path 0 stores 12.0 dB and 10.1 dB, both above the
   10.0 maximum. The official app resets out-of-range reads to 0; the bridge reports them
   raw. Settling this needs the official app open on the same preset.

Writes are confirmed working end to end: a master write of -12.0 dB was clearly audible and
read back as -12 dB. The firmware simply never ACKs writes, so the bridge correctly treats a
missing reply as normal.

### Resolved: output independence and the master EQ

`POST /api/eqprobe` was run against live hardware (mem 1) **twice, with an identical
verdict** — 17/17 paths replied in 17 round trips, no retries needed, every body exactly
1044 bytes. Raw result: `evidence\eqprobe-mem1.json`.

| paths | result |
|---|---|
| **0–7** | all mutually **distinct** — `c76cfaa5, 9e986bb4, 3ce813af, 309636e8, 09c7d778, 0889971e, fb6764f6, 701f1cee` |
| **8–15** | byte-**identical** to each other — `b9b2cf4e` ×8 |
| **16** | distinct from every other path — `4b9e93c9` |

Two conclusions:

1. **Outputs 1–7 are genuinely independent of output 0**, so per-driver EQ is real: this is
   an **8-output** unit with 31 bands each. Paths 8–15 sharing one hash marks them as
   unpopulated slots, which also confirms the 8-channel form factor.
2. **Path 16 is a separate EQ chain**, not a 17th output — so a second 31-band stage exists
   alongside each output's own.

This also overturns the earlier reading that "paths 1/2/3 are byte-identical". They are not:
that sample was taken while another host was competing for EP1, and later attempts were
failing at the write layer (`write error: Win32Error`, a stale USB handle) rather than at the
read. Reconnecting cleared it. **Contention and stale handles must be excluded before
trusting a negative probe result.**

**Caveat on "62 bands":** path 16 being *distinct* is proven. That it sits *in series* with
each output is inferred from the vendor's model (`mDp12OutputSet[mem, 16]` feeding
`CalcMainEqCurve` behind its own `eq_main_eq` control) and has **not** been confirmed by
listening. Path 16 currently decodes flat — 0 dB across all 31 bands — so it is an unused
stage as it stands. The defensible claim is *31 verified bands per output, plus a distinct
31-band master stage available*.

## Tune import (`.idsp`)

The **Tunes** tab reads the official DS18 tune format. Parsing runs entirely in the browser,
so a file can be inspected with the amplifier unplugged; only the final write needs the
bridge.

Per memory and per output it reports: EQ bands in use, gain range, Q range, how many centre
frequencies sit off the ISO ladder, output level, time-alignment delay, polarity, mute, and
the crossover filters - plus memory-wide totals. Selecting an output redraws the response
curve for that cell.

**Write scope.** EQ (cmd 14) first, then the **output block (cmd 11)**: level,
time-alignment delay, polarity and mute, complete for all sixteen outputs in one frame.
A tick box defaults this on and says plainly that it is memory-wide, not scoped to the EQ
selection. The crossover still needs command 13, which the bridge does not send, so the
preview reports it rather than dropping it. Output 16 — the master chain — is outside cmd
11 entirely, since the vendor's own sender loops `i<16`; every file in the collection
leaves it at a constant −3 dB / 0 ms, so nothing tune-specific is lost there.

Writes go through the existing `/api/eq`, one cell at a time, with `mem` and `path` in the
body. Serial on purpose: the firmware never acknowledges a write, so parallel requests would
interleave with unsolicited telemetry frames and make a failure undiagnosable.

**Clamping is reported, not hidden.** The bridge clamps to `DP12_Param` ranges at the codec
level, and the importer counts every band whose gain, Q or centre frequency falls outside
them. The stats table shows the file's raw values while the curve shows what the DSP will
actually do, so a clamp is visible rather than silent.

Verified against real files in `C:\Users\sdotm\Downloads`: `iya1m4.idsp`, `ai2.idsp`,
`0r05ac.idsp`, `cb0nyk.idsp`. The browser parser's numbers agree exactly with an
independent PowerShell XML parse (e.g. output 6 of `iya1m4` memory 2: level -14.0 dB, delay
3.8 ms, 12 crossover filters memory-wide, 8 delayed outputs, no mutes or inversions).

**Correction.** `ai2.idsp` and `0r05ac.idsp` were earlier described as out of spec at
23.3 dB. That was wrong: the figure is the magnitude of a **cut** (`min = -23.3 dB`,
`max = +9.0 dB`), comfortably inside the DSP's -25...+10 range. Nothing in those files needs
clamping. A deliberately illegal fixture confirmed the warning does fire when it should.

## Response curve

The EQ and Tunes tabs, and the front page, draw the composite magnitude response of the
enabled bands.

This is the DSP's **real** transfer function, not an approximation. The vendor's own
`EqGetCoff` output was decoded (byte-swapped unsigned Q24, storage order
`[b2, b1, b0, -a2, -a1]`) and matched against the RBJ peaking-EQ cookbook at 48 kHz to a
worst error of `5.689e-8` - one Q24 LSB. See `DS18_DSP_protocol.md` section 10.1 and
`evidence/eqcoef-rbj-validation.txt`.

600 sample points, because at 260 the readout reported +9.9 dB for a band set to exactly
+10; narrow AI bands (Q up to 20, bandwidth `fc/20`) need the density or their peaks get
sliced off. Verified: Q 1.0 / 4.3 / 10 all read +10.0 dB, Q 20 reads +9.9 dB.

## Front page

The Status tab leads with a hero readout - master level at 34px, source and memory beneath -
and the current EQ shape, so the state of the system is readable at a glance rather than by
hunting through rows.

## Look and feel

The palette is lifted from the Down4Sound/Kenwood tuner's own Qt stylesheet, recovered as
plain text from `EZY-DSP.exe` (`evidence/ezydsp-qss.txt`): surfaces `#26282c` / `#2e2d2d`,
hairlines `#2f3a41`, a single blue accent `#0babf2`, curve green `#84bf5d`, danger `#f92638`,
and small 2-4px radii. The crisp edge is a deliberate part of that look.

Note on that binary: despite the filename its VERSIONINFO reads `OriginalFilename: Kenwood.exe`,
`ProductName: Kenwood`, v1.0.4.0, (c) 2023 Kenwood. It is a native **Qt5** app, not Electron
and not .NET - an early string match on "Electron" was a false positive inside
"Secure Electronic **Transactions**". Since a Qt UI lives in compiled widgets rather than
markup, the stylesheet was the extractable design source.

The running app has since been rendered and screenshotted too (PowerShell `CopyFromScreen`
while it is in the foreground — it is two processes: `EZY-DSP (V1.0.9)`, a D4S-branded device
launcher, which starts `EZY-DSP68`, the DS18-branded editor). Its **EQ page is the structural
reference** for this UI: a 31-column matrix of band numbers / Q / Gain / vertical sliders /
Freq / Bypass, with the channel checkboxes in a single row beneath it. Captures live in
`%TEMP%\opencode\ezy-dsp68*.png`.

## EQ matrix and channel strip

The EQ tab is laid out like the vendor's EQ page: a **31-column matrix** with the row labels
pinned to the left edge, scrolled sideways as one block so every band's controls line up
column by column — band numbers, then `Q Value` / `Gain` / vertical gain slider / `Freq.` /
`Bypass` rows. The numbers stay typeable (an exact Hz, dB or Q is a typed value, not a
dragged one) and each slider mirrors its Gain box: type or drag, both edit the same cell.
The vendor page's Allpass row is omitted because the DSP's band type is fixed to peaking
(type 0), and the crossover row needs cmd 13, which the bridge does not send yet.

Under the matrix sits the **channel strip** — one chip per output (a compact `CH n` row
replacing the earlier vertical side rail, matching how the vendor shows `CH1..CH8`):

- **tap a CH label** to select it: it becomes the path being edited, and that channel's own EQ
  is read back from the device;
- **tap the chip's box** to take that output out (muted) or bring it back;
- **Solo selected** lifts one channel and mutes the rest in a single cmd 11;
- **All live** / **Mute all** set every visible channel at once;
- **Flatten memory** zeroes all 31 bands across paths 0–16 with a progress counter. A 0 dB
  peaking band is an exact identity, so this is genuinely flat rather than approximately so.
  It takes roughly 14 s (17 cells, writes serialised), and re-importing the tune restores it.

Mute toggles are refused while disconnected: the chip must never show a state that was not
transmitted.

**Never write an unread cell** (learned the hard way, 2026-10-15). The bridge's band mirror
boots seeded with the default 1/3-octave ladder at 0 dB, and until this session the UI only
read a cell's EQ when a channel was *tapped* — boot never read it. The first live Apply on
such a cell therefore shipped the defaults over the cell's real contents: memory 2,
output 0 lost its per-driver EQ (probe FNV hashes prove the blast radius was exactly one
path: `3345808037 → 1900251915` on path 0, while paths 1–16 stayed byte-identical to the
earlier capture in `evidence/eqprobe-mem1.json`). Recover it by re-importing the tune that
cell belonged to (`.idsp` import, scope "This output only").

The UI now guards every route to a stale cell:

- **boot** reads the selected cell from the device before the grid is editable;
- **channel select** marks the cell unread until the read lands, and discards read
  responses that arrive after another switch;
- **Apply** refuses to transmit an unread cell — it reads first and asks you to apply again;
- **preset switches** (ours or the head unit's) invalidate the cell and re-read;
- **refresh after a tune apply** re-reads the cell instead of trusting `/api/info`, whose
  band arrays are the bridge's single-slot mirror of the last write to *any* path.

Verified live: Apply in the same tick as a channel switch logs `apply blocked` and sends
nothing; once the read lands, Apply writes normally (`eq tx 884 bytes`). `Flatten memory`
and tune import are intentionally exempt — overwriting is what they are for.

Every one of those writes resends the **complete** output block, because cmd 11 has no
partial form and the DSP cannot be asked to read any of it back. The strip therefore shows
the bridge's own record of the last block it sent, and says so: while a memory is not yet
"established" a note warns that the first write would set everything to the values on
screen (0 dB, 0 ms, live) — import a tune first to load real levels instead.

Two counts are reported rather than assumed. The channel count comes from ReadParam sub 4
when it answers, and otherwise from the path probe's 8-independent-output finding, tagged
`defaulted` so an inferred value is never mistaken for a read one.

## Live RTA (`rta.exe`)

Separate process, separate port — the bridge never touches RTA data, so the daemon can
die or be absent without affecting anything else.

```powershell
cd bin
.\rta.exe                     # default capture device, serves http://localhost:8586/rta.json
.\rta.exe --list              # show capture devices
.\rta.exe --device 1          # pick by index
.\rta.exe --port 9000 --out C:\Temp\rta.json
```

The UI's RTA tab polls `http://<same-host>:8586/rta.json` every 300 ms while the tab is
open (CORS `*`, one request per connection, `Connection: close`). The stimulus buttons
play pink noise or a 20 Hz–20 kHz sweep **from the phone/laptop itself** — the DSP has no
USB audio device, so the signal path is: phone → head unit → DSP input, while the laptop
mic measures the result.

`measure.ps1` runs a scripted capture against the same endpoint (averages bands, prints
tilt in dB/oct, saves JSON evidence) — it polls HTTP, never the file system.

### Mic selection & calibration file (in-app)

The RTA tab has a **mic dropdown** and a **cal file** control — both talk straight to
`rta.exe`, the bridge stays out of it:

```powershell
GET  /devices                    # every winmm input: {index, name, ch, inUse}
GET  /device?n=2                 # switch capture device at runtime (also n=default);
                                 # capture thread breaks, reopens immediately, no backoff
GET  /cal                        # calibration status: {loaded, file, serial, points, range}
POST /cal?name=<file>.txt        # upload a miniDSP/REW cal file (raw text body)
```

**Which device is it really on?** `/rta.json` answers with three fields, added 2026-10-21 after a
whole measurement cycle was recorded through the laptop's own array:

| field | meaning |
|---|---|
| `device` | the name of the endpoint winmm **actually opened** — asked of the handle with `waveInGetID`, not taken from what was requested |
| `ch` | channels in the opened format (UMIK-1 = 1, laptop array = 2) |
| `devOpenIdx` / `devDrift` | the index opened, and the name that index means **now** (non-empty = a replug renumbered the list under the daemon) |

Startup no longer leaves the choice to the WAVE_MAPPER. Opening `0xFFFFFFFF` lets winmm pick an
endpoint and then refuse to name it — `waveInGetID` returns `0xFFFFFFFF`, "Microsoft Sound Mapper"
(verified with `var\probe-waveinid.cs`) — so `rta.exe` resolves `UMIK`/`miniDSP`/`measurement` by name
and opens that index explicitly. With no such device present it falls back to the mapper and says
`"system default"`, which the run box treats as a **failure**: the mic row is a hard stop, not a
warning. See `TUNE-PLAYBOOK.md` §5.1.

Upload **verifies** before it applies: SERNO header present, ≥50 freq/dB pairs,
frequencies strictly ascending, sane range — otherwise `422` with the reason and the
previous cal stays active. On success the file is saved as `bin/rta-cal.txt` and
**auto-reloaded on every startup**, and its correction is added to all 61 bands
(REW convention, log-frequency interpolation, edge-clamped) — so the curve you see is
the mic-corrected one. Response includes spot corrections at 20/100/1k/10 kHz as a
sanity check against the file text.

The UMIK-1 files (serial **7170061**, 0° and 90°) are kept in `cal/` in this repo —
Downloads gets cleaned, the project doesn't. A rejected upload never clobbers a loaded
cal; only a verified parse swaps it.

Verified end-to-end: `POST` of the real 0° file → `{"ok":true,"serial":"7170061",
"points":615,"fmin":10.1,"fmax":20016.8,"persisted":true}`; daemon restart re-applies
it automatically; UI shows `loaded` + serial; runtime switch 0→2→0 confirmed through
the dropdown with capture resuming each time.

### Saved measurements (REW-style table)

The RTA tab keeps a measurement list like REW's: numbered rows, each with a
mini-curve thumbnail (auto-scaled, decade grid, 20/20k axis labels, per-row color),
date/time, and a × delete.

- **Capture** stores a **3 s average** of the live mic-calibrated curve (ring of the
  last ≤10 polls) — not a single noisy frame.
- **Tap a row** → overlays it on the big plot as a dashed amber curve (y-range widens
  so it can't fall off-plot); tap again to turn the overlay off. The tapped row stays
  highlighted.
- **×** deletes (overlay index follows); **Clear** wipes all after a confirm.
- **Editable names**: every row has a name field (placeholder `name…`) — label rows
  `front left`, `sub`, etc. Saves live on each keystroke to localStorage; tapping the
  name focuses the field (does **not** trigger the overlay — tap the thumbnail or date
  for that).
- **Send to PC / From PC**: hands the whole list over the LAN through the bridge —
  `POST /api/rta-meas {meas:[{t,db,name}]}` writes `rta-measurements.json` at the
  project root (newest 200 kept, one write per click — never on a timer); `GET` reads
  it back. Merges by capture timestamp, so resending is idempotent (`sent 0 … 2
  already there`), and **From PC** merges the computer's copy into the phone's list
  (dedupe by `t`, names included) — fresh phone / cleared storage recovers everything.
  No internet needed; phone and laptop just need the same LAN (the car setup).
- Storage: `localStorage["ds18.rtaMeas"]` on the phone — 61 floats + timestamp per
  row, capped at 24 rows, survives reloads. No daemon involvement, no network.

Verified in-browser: 3 captures rendered with distinct colored thumbs, overlay row
highlighted + dashed curve visible on the main plot, delete renumbered the list,
Clear emptied it (`"[]"` in storage, empty-state hint back), rows survived a full
page reload, zero console errors throughout.

### Why the capture thread never touches the filesystem

Measured: writing `rta.json` into this OneDrive-synced tree 5×/s stalled the capture loop
for up to 2 s per sync (analysis dropped from ~11 Hz to ~3.3 Hz), which would corrupt a
real measurement. JSON is built in memory; `--out` is written by the HTTP thread at 1 Hz.

### The ucrtbase crash (fixed): winmm needs a PINNED header array

The daemon died ~20 s into browser polling with `c0000005` in `ucrtbase!memcpy`
(evidence: Windows Application event log, faulting export resolved from the PE export
table). Cause: `waveInAddBuffer(hwi, ref hdrs[i], ...)` queues `&hdrs[i]` — winmm keeps
that pointer and writes into it (WHDR_DONE, byte counts, memcpy of captured PCM) until
the buffer returns. `ref` on a blittable struct pins only for the call; the HTTP
server's ~3 allocations/s eventually forced a gen2 compaction that moved the unpinned
`WaveHdr[]`, so the driver dereferenced stale memory. Fix: `GCHandle.Alloc(hdrs,
GCHandleType.Pinned)` for the whole capture session, freed in `finally` **after**
`waveInUnprepareHeader`. Verified: 90/90 polls over 90 s at 10.4 Hz analysis, where the
unpinned build died every time at ~15–20 s.

Also fixed in the same pass: the HTTP 404 advertised `Content-Length` but omitted the
body — browsers report that as a CORS/network error rather than a clean 404.

## Raw sweep measurement (`sweep-tool.exe` + `measure-sweep.ps1`)

The RTA stores a frozen magnitude envelope: one number per band, no time. That is enough to
see shape, and it is what the capture rules in the UI use, but it cannot produce an arrival
time — so it cannot align drivers. This path records the raw audio instead.

```
build-sweep.ps1                                  # csc, no SDK, C# 5
bin\sweep-tool.exe make sweeps\sweep-20-20k-10s.wav 10 20 20000 48000
measure-sweep.ps1 -Name "driver tweeter"         # plays the sweep and records
measure-sweep.ps1 -NoPlay -Seconds 240           # page plays a cycle; script only records
measure-sweep.ps1 -From measurements\x.wav       # re-analyse an existing take
```

**Two channels, one recording.** Left is Stereo Mix (what the PC is playing), right is the
mic. `sweep-tool bursts` finds every occurrence of the sweep in the loopback, then finds the
arrival of the same burst in the mic and reports the delay and the transfer function
(mic / loopback, so the source's own response cancels).

**Why one recording for the whole cycle.** The two capture devices do not start at the same
instant — measured here as roughly a second of skew — so an absolute delay from a single
take is not trustworthy on its own. Inside one recording that skew is a constant, and a
constant cancels when delays are compared between channels. That comparison is exactly what
time alignment needs; the absolute number is reported but should not be used across runs.

**What is proven** (synthetic files with known answers, `evidence\sweeptool-test\`):

| check | expected | measured |
|---|---|---|
| single burst, 50 ms delay, −6 dB path | 50.000 ms / −6.0 dB | 50.000 ms / −6.02 dB |
| single burst, 137 ms | 137.000 ms | 137.000 ms |
| 1 kHz lowpass in the path | rolloff above ~1 kHz | −2.3 dB @ 905 Hz, −5.7 dB @ 1.28 kHz |
| four bursts, 13 / 3 / 5 / 9 ms, 0.9 / 0.5 / 0.25 / 0.125 gain | same | all four exact; levels within 0.07 dB |

**What is not proven yet.** No acoustic run with a working mic has been done: the UMIK-1 was
unplugged during this build, and the laptop's built-in array is muted at the Windows level
(−88 dB), so the only live capture was the loopback channel (healthy). The failure path is
verified — a silent mic yields `ok: false`, no delay, exit code 3, and a warning, never a
fabricated number.

**Two gates, not one.** `bursts` marks a channel measured only when the transfer function
exists, the peak-to-rest SNR is at least 6 dB, *and* the normalised correlation coefficient is
at least 0.02. The second gate was added because the first is not enough on its own: with 60
bands' worth of lags, the maximum of noise alone is several sigma, and a channel hearing only
a −70 dB noise floor reached 7.9 dB SNR with a random 40.9 ms delay. Its coefficient was 0.006
against 1.000 for a real arrival, so the coefficient separates them by two orders of magnitude.
`rehearse.ps1` keeps a silent-channel control that fails if this ever regresses.

**Settings that matter.** The page's sweep is an oscillator ramping 20 Hz → 20 kHz over 10 s,
which is the same log sweep this tool generates, so the generated file can be used as the
reference for the page's own stimulus. Stereo Mix only captures the Realtek output: with a
Bluetooth output there is no loopback, and `-NoLoopback` records the mic alone (shape only,
no comparable delay). The mic needs a real level; −88 dB RMS is silence, not a quiet room.

## Measuring a driver full range (`crossover-range.ps1`)

A sweep can only show what the crossover lets through. The tune band-limits paths 4-7 at
2.5 kHz (the 6.5" at 1 kHz) and the tweeters at 2.5 kHz, so measuring through the tune tells
you what the tune sounds like, not what the driver can do. This script opens the passbands
for the measurement, then puts them back.

```
crossover-range.ps1 plan                 # what would be written; no device needed
crossover-range.ps1 show                 # current crossovers, paths 0-7
crossover-range.ps1 open -TweeterFc 1000 # snapshot, write, verify
crossover-range.ps1 restore              # write the snapshot back, verify
crossover-range.ps1 open -LowFc 100      # floor the small drivers instead of 20 Hz
```

**What "open" means on this hardware.** cmd 13 carries two edges, their slopes, their filter
types and 40 coefficients — there is no enable bit on the wire, so the vendor expresses "off"
with an edge: low edge ≤ 20 Hz gives identity coefficients `{0,0,1,0,0}`, a true bypass, and
high edge 20000 Hz computes an LR filter at 22 kHz, which is what the official app's own
`.idsp` produces for a disabled low-pass. So full range = 20 Hz / 20 kHz, and the high edge
keeps a real filter that sits above the band. Tweeters get 1000 Hz LR24 instead, so a sweep
cannot drive them below that.

**Why a snapshot is enough to get back.** The coefficients are a pure function of the six
scalars, and the bridge ports the vendor's own `HlpGetCoff`. Checked against the device's
saved readback (`evidence\passband-baseline-2026-10-16.json`): for all eight paths, a dry run
of the recorded scalars reproduces the device's 168 bytes exactly, and the coefficient split
used for verification matches the readback arrays. A write is accepted only after the
readback matches both the scalars and all 40 coefficients; `restore` compares against the
snapshot's coefficients, so a path that does not go back byte-for-byte is reported and fails.

**The hazard this script is built around.** The change lives in the DSP memory until it is
restored, and it survives a power cycle. The snapshot is written to disk *before* the first
write and is never overwritten while it exists (a second `open` adopts it), so the way back
cannot be lost by re-running the command. `restore` refuses to write into a different memory
than the snapshot's, because a cmd 13 write is addressed to the memory it names.

**Not proven yet.** No write has been sent with the DSP unplugged — `plan` exercises the
byte-building only. The first `open` needs the device connected, and its readback is the
proof.

## Is the rig ready? (`doctor.ps1`)

A measurement depends on more than the app looking connected: the DSP must be on the memory
whose tune is intact, sweep mode must not still be flattening it, the crossovers must not
still be open from last night, the UMIK-1 must be present, rta.exe must be running, and the
cal file must be there. Each of those has cost a run at least once.

```
doctor.ps1                # one screen, green / amber / red
doctor.ps1 -Restore       # close the crossovers if they are still open
doctor.ps1 -Json          # also writes doctor.json
doctor.ps1 -SelfTest      # the snapshot comparator, with a negative control
```

It is read-only. The two checks that exist nowhere else are:

- **crossovers still open?** it reads paths 0-7 and compares the six scalars on each against
  the snapshot `crossover-range.ps1` wrote for the device's current memory. A mismatch is a
  FAIL that names the paths, because the tune is then not what the owner tuned.
- **sweep mode still on?** every path flat *and* the master at −8.0 dB is the fingerprint it
  leaves. That is a warning, not a certainty: a deliberately flat tune looks the same.

`-SelfTest` replays the device's saved readback as if it were live, then mutates one low edge
and one high edge; it passes only if the unchanged set reads closed and each mutation names
exactly the path that changed.

## Rehearsing a cycle (`rehearse.ps1`)

The first real cycle should not also be the first time the pipeline runs. This builds a
two-channel fixture with `sweep-tool synth`: left is the loopback (eight sweeps at the page's
14 s cadence: 12 s window + 2 s settle), right is the mic (the same sweeps delayed by a known
number of milliseconds and scaled by a known gain), over a −70 dB noise floor. Then it runs
the real `measure-sweep.ps1` over the file and compares.

```
rehearse.ps1              # 8 channels, delays 3.2-5.4 ms, gains 0 to -14 dB
rehearse.ps1 -NoNegative  # skip the silent-channel control
```

What it proved on the synthetic cycle: every delay within 0.008 ms, every level within
0.00 dB, and a silent channel marked not-ok instead of being given a random delay (that is
what found the second gate above). Exit 0 only if all of that still holds.

## Proposing EQ (`propose-eq.ps1`)

The DSP's EQ is 31 fixed bands at Q=1, so designing a tune is choosing a gain per band. This
reads a measured curve and works out the gains that would flatten it — and writes nothing.

```
propose-eq.ps1 -Bursts evidence\...\sweep-....json -Burst 0
propose-eq.ps1 -Name "tweeter 1"                 # newest saved RTA measurement
propose-eq.ps1 -Name "front 3.5" -TiltDbPerOct -0.5
propose-eq.ps1 -SelfTest                         # round trip with a known answer
```

It solves a weighted least-squares fit over the 31 bands using the peaking response of each
band (RBJ, Q=1, 48 kHz), so overlapping neighbours are accounted for instead of assuming one
band touches one frequency. Three guardrails keep it safe: the curve is normalised to the
median of its usable band (a proposal never tries to raise a channel by the mic's absolute
level), a frequency more than 15 dB under the curve's top is excluded, and a band whose own
centre is under that floor can be cut but never boosted. Boosts cap at +6 dB, cuts at −12 dB.

The predicted curve is a model, not a device readback, and it is printed next to the measured
curve so the residual is visible. `-SelfTest` flattens a synthetic curve built from known
gains (max residual 0.31 dB) and checks that a 125 Hz null and a sub-60 Hz rolloff are not
chased.

## Layout

```
src/Protocol.cs      CRC, framing, RX parser, command builders
src/DspMath.cs       Q24 conversion, EQ band packing
src/DspClient.cs     USB transport (LibUsbDotNet)
src/BridgeServer.cs  HTTP JSON API + static file host
src/Program.cs       --selftest / --serve
src/Rta/RTAMeter.cs  rta.exe: winmm capture, FFT, 1/6-oct bands, HTTP server
web/index.html       phone UI
measure.ps1          scripted RTA capture -> JSON evidence
measure-sweep.ps1    loopback + mic recording -> per-burst delay and transfer function
rehearse.ps1         synthetic eight-channel cycle -> checks the analysis end to end
crossover-range.ps1  open / restore the crossovers for a full-range measurement
doctor.ps1           is the rig ready: DSP, memory, crossovers, sweep mode, mic, tools
propose-eq.ps1       measured curve -> 31-band EQ proposal (no writes)
vendor/              LibHoneyWg.dll, LibUsbDotNet.dll (extracted from the official app)
```

Protocol details are in `../DS18_DSP_protocol.md`; the decompiled reference source is in
`../decompiled/AP.cs`.
