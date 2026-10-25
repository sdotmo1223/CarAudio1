# TUNE PLAYBOOK — how to build and write a tune for this car

The one file to read before touching a tune. It is written for an agent (or a person) who has
never seen this project: everything you need to go from "the owner wants a hip-hop tune" to a
verified tune on the DSP, plus every trap this rig has already cost somebody.

**Start at `START-HERE.md`.** It is the single orientation for this folder: the read order,
the three rules that override everything, where the code and data live, and one line per tool.
Older "read this first" lists in `TUNING-BRIEF.md` §4.5 and `APP-BRIEF.md` §11 have been
retired in its favour.

Read order if you have time: `START-HERE.md` → this file → `EQUIPMENT.md` (driver physics, why
the system is what it is) → `STATE.md` (what the rig looked like last session, consult rather
than read) → `..\README.md` (the app's own detail).

**Standing directive (2026-10-23): the owner feeds tuning material from outside into this
project so it is accounted for whoever tunes next.** The source material lives in `DSB\refs\`
(read its `README.md` first — it carries the intake rules and the evidence-versus-doctrine
hierarchy). The rules derived from it are **§9.0 onward**: §9.0 level matching, §9.0.1 phase /
summation / crossover type, §9.0.2 the summing measurement this rig has never taken, §9.0.3
crossover selection. Read those before proposing a change to gains, crossovers or the target
curve — three of them currently converge on the same recommendation.

Last verified against hardware: **2026-10-21**, after the first real tune was written and read back.

---

## 1. The system

A 2017 Lincoln Continental. Eight DSP output paths, twelve drivers, three amplifiers.

| piece | what it is |
|---|---|
| DSP | **DS18 DSP8.6iA** — a 6-channel Class D amp with an 8-channel DSP in one box. 8 DSP outputs, of which **6 are amplified** and **2 are line-level pre-outs** (2 V RMS). Preset format `.idsp`, internal model `DP12_Param`, 4 memories. |
| control | `ds18-bridge` — our own C# bridge, HTTP on `:8585`, talks to the DSP over USB HID. The phone page and the scripts both drive it. It is the only sane way to program this DSP from an agent. |
| measurement | `rta.exe` on `:8586`, captures from a **UMIK-1**. The phone plays the stimulus; the laptop records it. |
| sub | Rockford Fosgate T2 12" on a **Kicker CXA1800.1**, fed **directly from the head unit**. |

### Three structural facts that decide everything

1. **Only four channels are stereo.** Paths 0–3 are the four tweeters, one each. Paths 4–7 each
   drive a **left/right pair through one channel**: front 3.5" pair, rear 3.5" pair, front 8" pair,
   rear 6.5" pair. Everything below ~2.5 kHz is **mono**, and each pair shares one gain, one delay,
   one EQ curve. No single delay can time-align a pair at two different distances.
2. **The sub is outside the DSP.** No DSP level, delay, crossover or mute can reach it. It is fed
   from the head unit into its own amp. Every other channel is delayed *to* a driver the DSP
   cannot see or correct. Its level is the head unit's sub trim and the Kicker's gain knob.
3. **The midrange is the bottleneck, and the bass hides it.** Max SPL: 3.5" **102 dB** (50 W RMS
   limit), tweeter 112 dB, 8" 118 dB, 6.5" 120 dB, sub 114 dB (+10–15 dB cabin gain below 100 Hz).
   The band the ear cares most about (1–4 kHz) is carried by the least sensitive driver in the car.
   The bass has to sit 12–20 dB below its maximum to be level with the midrange.

---

## 2. Path map — know which number is which speaker

*This table is the **canonical** path map. `TUNING-BRIEF.md` §3 and `APP-BRIEF.md` §2 used to
keep their own copies; they now link here, because a path map that exists in three places is
three chances to be wrong.*

This mapping is **channel order**, and it is what the page's roster uses. Do not guess it.

| path | app / roster label | driver | amp | power | stereo? | rated band | beams above |
|---|---|---|---|---|---|---|---|
| 0 | tweeter 1 | BZRK RS-T150 1" dome | DSP amp ch1 | 80 W @ 4Ω | **L** | 1.5 k–25 k | ~13 kHz |
| 1 | tweeter 2 | same | DSP amp ch2 | 80 W @ 4Ω | **R** | 1.5 k–25 k | ~13 kHz |
| 2 | tweeter 3 | same | DSP amp ch3 | 80 W @ 4Ω | **L rear** | 1.5 k–25 k | ~13 kHz |
| 3 | tweeter 4 | same | DSP amp ch4 | 80 W @ 4Ω | **R rear** | 1.5 k–25 k | ~13 kHz |
| 4 | midrange 1 | NVX XQS3v2 3.5" (2×) | DSP amp ch5 | 65 W each @ 2Ω | mono pair | 200–18 k | ~3.9 kHz |
| 5 | midrange 2 | same (rear pair) | DSP amp ch6 | 65 W each @ 2Ω | mono pair | 200–18 k | ~3.9 kHz |
| 6 | midrange 3 | Deaf Bonce AP-M81SE 8" (2×) | ATOM pre-out 1 | 200 W each | mono pair | 110–10 k | ~1.7 kHz |
| 7 | midbass 1 | Orion XTX654 6.5" (2×) | ATOM pre-out 2 | 200 W each | mono pair | 140–12 k | ~2.1 kHz |
| — | sub | Rockford T2 12" | Kicker CXA1800.1 | 900–1800 W | mono | 20–250 | — |

Sensitivities: 3.5" **85 dB**, tweeter 93, 8" 95.1, 6.5" **97**, sub 83.5.

**The owner's labels are not the DSP's labels.** He calls path 6 "midrange 3" and path 7
"midbass 1" because the roster is one row per channel in order. If he says "midbass", confirm
whether he means path 7 (the 6.5") or the 8" — he sometimes means the 8".

### Amplifier facts that bite

- DSP amp **channels 1–4 are 4 Ω only**. ch5–6 are rated to 2 Ω. A parallel L/R pair on ch1–4 is
  outside spec.
- **ATOM-400.4**: 4×200 W @ 4 Ω, RCA input **0.2–6 V**, SNR 80 dB, its own 12 dB/oct HP/LP and a
  bass boost — all invisible to the DSP. **Bass boost must be 0.** Its gain must be matched to the
  DSP's 2 V pre-out; at the 0.2 V end the pre-out overdrives it 10× and it clips before it sounds
  loud (harsh, not loud). This is a real, recurring cause of "bright/harsh" that no EQ fixes.
- **Kicker CXA1800.1**: LP 50–200 Hz **12 dB/oct** (this is the sub's only crossover), fixed 25 Hz
  subsonic, KickEQ 0–6 dB @ 40 Hz (**leave at 0**), and it can hand the T2 **1.8 dB more than the
  sub is rated for**. The sub's LP knob position decides whether there is a clean handoff to the
  8" or a broad overlap.

---

## 3. Hard limits — never violate these

*Every number below carries its source inline so it can be challenged without reading three
files: `§` = this playbook, `EQ` = `EQUIPMENT.md`, `PROTO` = `..\DS18_DSP_protocol.md`,
`REF` = `refs\`. A rule with no tag is an observation from a session in `STATE.md`.*

| parameter | range | notes |
|---|---|---|
| master volume | −60 … 0 dB | `POST /api/master` (cmd 12, `PROTO` §15). The only command that audibly changes loudness. |
| group gain | −6 … +6 dB | cmd 18 (`PROTO`). Stored and echoed, but does nothing useful unless the device's output mask includes it. |
| input gain | −20 … 0 dB | `PROTO` §15 |
| **output gain** | **−40 … 0 dB** (default −20) | cmd 11 (`PROTO` §15). The per-path level. **No readback on this hardware** — see §9.0.2. |
| **EQ gain** | **−25 … +10 dB** | per band. The application clamps to **−12 / +3 / +4 dB** — §9, a stricter rule than the hardware's. |
| EQ Q | **0.1 … 20.0** | `PROTO` §15 |
| EQ Fc | **20 … 20000 Hz** | `PROTO` §15 |
| input delay | 0 … 25 ms | `PROTO` §15. Never binds in a cabin: 25 ms = 8.58 m of path. |

Driver safety:

- **Tweeter high-pass never below 1500 Hz.** Rated 1.5 k–25 k (`EQ`, BZRK RS-T150);
  `crossover-range.ps1 -TweeterFc` defaults to 1500 and the app refuses to start a run when a
  tweeter's HP reads back open. Also `REF`: Rawcat protects unknown tweeters before measuring.
- **The 8" is rated from 110 Hz and the 6.5" from 140 Hz** (`EQ`, Orion XTX654 / Deaf Bonce
  AP-M81SE). Neither publishes Xmax; both are pro midranges with short excursion. A full-scale
  sweep from 20 Hz is heat and mechanical stress with no acoustic output. Do not run them below
  ~100 Hz except deliberately, at low level.
- **The sub amp must be physically off (or its RCA pulled) for every measurement.** The sub has no
  DSP path (`EQ`), so the app cannot mute it. A 114 dB sub against a 6 W midrange contaminates
  everything from 20 Hz to well past 100 Hz. The page's "sub disconnected" tick box is a human
  promise, not a check — it defaults un-ticked and the run will not start without it.

---

## 4. The rig and the run cycle

```
bridge    http://127.0.0.1:8585        (DSP control + phone page + WAV stimulus)
rta       http://127.0.0.1:8586        (/devices, /device?n=<i>, /rta.json)
phone     http://LAPTOP-C7NA3SNN:8585/play.html      stimulus (native WAV, survives screen lock)
phone     http://LAPTOP-C7NA3SNN:8585/               the DSP page, run cycle, per-channel ticks
```

The machine name is stable; the IP is not (`where.ps1` prints the current one).

**Roles.** The phone plays the audio (it is the Bluetooth source into the head unit) and triggers
the cycle. The laptop never plays into the car — proven, it cannot. The laptop captures, analyses
and writes to the DSP.

**A cycle**, as the page runs it: pick the 8 channels → for each one, mute the others, play that
channel's sweep, capture, unmute → next. About **16 s per channel**. The page refuses to start if
the playing memory has **any non-zero EQ gain** (a response measured through the tune is a
measurement of the tune), if the sub tick is absent, or if the measurement mic is not identifiable
as the UMIK-1 (section 5.1). When the cycle finishes it POSTs
`/api/run-save` and the laptop writes a folder (section 8).

**Stimulus level.** The page's slider `rtaStimGain` in `web/index.html` shipped at **−24 dB** and
that was the whole "everything is quiet" mystery. It defaults to **−6 dB** now. If peaks come back
near −90 dBFS, check this slider *and* the RTA's device before blaming the DSP.

---

## 5. Measurements — what a valid one is, and how it lies

### A capture is

241 points, **1/24-octave grid** (captures before 2026-10-22 are 1/6 = 61 points), 20 Hz → 20 kHz, **absolute dBFS** (not normalised), plus:

```json
{ "channel": "tweeter 1", "run": "Car tune (2026-10-21)", "capturedAt": "...",
  "units": "dBFS", "grid": "1/24 octave, 241 points, 20 Hz to 20 kHz",
  "gate": { "ok": true, "burstDb": 71.6, "fromHz": 453, "toHz": 20480 },
  "peak": { "dbfs": -46.0, "hz": 4561.4 },
  "sig":  { "ok": true, "reason": "a sweep arrived, low to high, ..." },
  "points": [[20.0, -110.2], [22.4, -108.9], ...] }
```

Both exporters (`export-meas.ps1` and the bridge's `/api/run-save`) write this shape, including
`gate`. The fitter reads `gate`, falling back to `sig`, and treats a missing verdict as pass.

**Absolute dBFS is the point.** Two captures are comparable only at the same stimulus level with the
same crossovers. Never normalise one before comparing it to another.

### The three ways a measurement lies

1. **Wrong microphone.** `rta.exe` addresses capture devices by *index*, and a USB replug renumbers
   them; a run then records `Microphone Array (Realtek(R) Audio)` at a **−93 dBFS** floor while
   reporting `capturing`. That happened on 2026-10-20 and wasted a whole eight-channel cycle.
   **Since 2026-10-21 the run box refuses to start on it** — see 5.1. The laptop array is digitally
   deaf (hardware mute/shutter, −100 dBFS above 300 Hz) and must never be the measuring mic.
2. **Wrong reference band.** Whatever band was played is the band to analyse against. A full-range
   take correlated against a 110 Hz–12 kHz reference scores ~0.05 and is refused — correctly. When
   a take is refused, try all five references; the highest coefficient names the band that played.
3. **A truncated take.** If the phone dimmed, playback stops and the capture is a fragment. The
   analyser checks chunk-by-chunk completeness and refuses. Use the served WAV (`/play.html`), not
   Web Audio — Web Audio dies when the tab loses focus.

**The capture gate cannot tell you the microphone was wrong.** It grades a burst, not a device, so
identity is enforced *before* the run — in the run box, where it can stop you.

### 5.1 The microphone gate — why a run refuses to start

A failing row in the run box disables **Start run** (`RUN.ready` counts `fail` rows; a `warn` row
does not block). The mic row is the one that has to be a hard stop, because every other check can
pass while the wrong device records perfect silence.

| fact | where it comes from | what it proves |
|---|---|---|
| `device` | winmm, asked about the **opened handle** (`waveInGetID`) — not about what was requested | which endpoint the stream is really on |
| `ch` | the format that actually opened | the UMIK-1 is **mono**; the laptop array is stereo |
| `devDrift` | the device list re-read now, compared with open time | whether a replug renumbered the list under the daemon |

It **fails** on: no RTA data · `status` not `capturing` · a laptop-ish name (`Microphone Array`,
`Realtek`, `Stereo Mix`, `internal`, `hdmi`, `webcam`) · a `Line In` · the anonymous Windows
default · a device whose name changed index since it was opened. It only **warns** on a mono
non-UMIK mic, and on a UMIK that is too hot.

Two changes on 2026-10-21 made that possible:

- **`rta.exe` no longer opens the WAVE_MAPPER by default.** Opening `0xFFFFFFFF` lets winmm pick an
  endpoint and then refuse to name it: `waveInGetID` answers `0xFFFFFFFF`, which winmm calls
  "Microsoft Sound Mapper" — measured with `var\probe-waveinid.cs`, because the obvious guess was
  wrong. It now resolves `UMIK`/`miniDSP`/`measurement` **by name** at startup and opens that index
  explicitly, so the name, the index and the channel count all describe one device.
- **`/rta.json` gained `ch`, `devOpenIdx`, `devDrift`.**

If the row is red the fix is hardware, not software: plug the UMIK-1 into **its own** USB port (not
the hub the DSP uses) and press **Verify setup** again. `.\preflight.ps1` runs the same check from
the terminal.

The checks only poll while the **Tune** tab is open (deliberately — no background chatter), so an
untouched page can honestly report "no reading yet". Open the Tune tab before concluding the mic is
missing.

### Grading a run

`after-run.ps1` grades the newest captures using only fields the capture carries: `peak`,
`sig.burstDb`, `sig.ok`, `sig.from/to`. Earlier versions inferred an in-band/out-of-band SNR and
were wrong twice — once taking the floor from the 12 quietest bins (which for a midrange is the
mic's digital silence near 20 kHz), once taking it from outside the driver's rated band (which,
with the low-passes open, is the driver's own output). **Do not reintroduce a passband assumption
when low-passes are open.** Plot with `var\plot-rta-meas.ps1` (`-MaxDb` is −10; at −30 it clipped
the two loud drivers flat).

---

## 6. Crossovers

Per path, two filters: HP and LP, each with Fc / slope / filter type / enable. The DSP writes them
as scalars **plus 40 coefficients**, so verify both.

```
.\set-crossovers.ps1 show                                  # read back every path + coefficients
.\set-crossovers.ps1 plan  -Hp "0=1500,4=80,6=50,7=60"     # show what would change
.\set-crossovers.ps1 apply -Hp "0=1500,4=80,6=50,7=60"     # snapshot, write, verify by readback
.\set-crossovers.ps1 restore -Snapshot evidence\crossover-snapshot-mem2-<stamp>.json
```

It snapshots to `evidence\crossover-snapshot-*` before writing, and `apply` verifies every path by
reading the scalars and the coefficient block back.

**On the device now:** paths 0–3 HP **1500**, 4–5 HP **80**, 6 HP **50**, 7 HP **60**, all LP
**open (20 kHz)**, **LR24**. That is the owner's explicit map ("that's how I want it to be set").

Two consequences you must state out loud before recommending a tune:

- With the LPs open, **every cone plays to 20 kHz**, so 2 k–20 k is a sum of eight drivers and
  nothing measured there belongs to one channel alone. EQ fitted in that region fits a fiction —
  hence the fitter's rule: **no lift above 12 kHz on tweeters, no lift above 2 kHz on any cone.**
- `doctor.ps1` reports **`FAIL crossover OPEN on path 0`**. That is this map, **not drift**. Do not
  run `doctor -Restore` or `crossover-range.ps1 restore` unless the previous tune is actually wanted
  (`evidence\crossover-snapshot-mem2-20261020-191029.json`).

Sane alternates if the owner asks for a real handover: 3.5" HP 200 / LP 2.5 k; 8" HP 110 / LP
1.2–1.5 k; 6.5" HP 140 / LP 1.2–2 k; tweeters HP 2.5 k or 1.5 k, LP open. Beaming numbers in
section 2 are the real ceilings — the rated tops are marketing for an on-axis mic.

### 6.1 Slope, filter type and polarity — the verified rules

Sources read and quoted at `DSB\refs\filter-theory\sources.md` (Rane Note 160; Linkwitz Lab;
ESP Project 253; miniDSP Flex Eight manual). Do not state a polarity rule from memory — the
specific error below has already been made once.

**Linkwitz-Riley is an even-order family only.** LR-2 = 12 dB/oct, LR-4 = 24, LR-6 = 36,
LR-8 = 48. **There is no 18 dB/oct Linkwitz-Riley.** 18 dB/oct is a **3rd-order Butterworth /
all-pass** design, which is why the miniDSP menu lists Butterworth at 18 and LR at 12/24/48 and
never puts them together.

**Polarity follows the order, per Rane:**

| slope | order | invert one output? |
|---|---|---|
| 12 dB/oct | LR-2 | **yes** |
| 24 dB/oct | LR-4 | no |
| 36 dB/oct | LR-6 | **yes** |
| 48 dB/oct | LR-8 | no |

**This car's map is LR24 = LR-4 = the no-inversion case**, so `revert 0` on all eight outputs is
correct and consistent — confirmed by the 2026-10-23 library scan. Nothing to change here.

**The trap to guard against:** setting slope byte **2 (18 dB/oct) while leaving filterType at
`1` (Linkwitz-Riley)** is a contradiction in terms. The hardware will accept it; the LR polarity
table then does not describe it, and the 40 coefficients cmd 13 writes are whatever the firmware
decides. Treat 18 dB/oct as its own case requiring a measurement, and if anyone asks for it,
ask why — 24 dB/oct is already on every path and is what both Rane's family logic and Audiofrog's
published recommendation favour.

**Acoustic-centre offset can override the order rule.** Elliott's own bench test found his
18 dB/oct network needed *no* inversion while his 24 dB/oct one *did* — but his drivers had
offset acoustic centres, and he says so. The order table above is a filter property; what you
hear at the mic is filter **plus** driver spacing **plus** delay. That is the whole reason §9.0.2's
summing run matters: it is the only thing that settles this for the actual car.

**A correction worth remembering.** A claim circulated on 2026-10-23 that Linkwitz and Elliott
contradict each other on 18 dB/oct polarity. They do not — the Linkwitz quote is from his
**LR-2 / 12 dB/oct** section and was misattributed. Check which *section* a quote comes from
before building a conclusion on it; two authorities appearing to disagree is often one of them
being quoted about a different thing.

**Delay sanity check:** `ms × 0.343 = metres`. This car's 3.8–6.0 ms is 1.3–2.1 m of path, and
the 25 ms cap is 8.6 m, so **the cap can never bind in a cabin** — a delay implying several
metres is a bug, not a tuning choice. These are *relative* delays against the sub's 250 cm
reference (§7), so subtract that before comparing to a tape measure.

**Library sweep, 2026-10-23 — three things the files carry that you must know before loading
one.** Full census and method in `DSB\refs\filter-theory\sources.md` §7. Crossovers live inside
`<passBand index="N">` as `Fc` / `hiLowType` (0 = HP, 1 = LP) / `Slope` / `filterType` /
`butt_Type` / `Enable`; **"open" low-pass means `Fc 20000` with `Enable 0`.**

- **`ai2.idsp` must never be loaded as-is.** Paths 6 and 7 carry an **active LR-2 low-pass**
  (2500 Hz, slope 1, filterType 1, Enable 1) with `revert 0` on both. Rane's rule says LR-2 is
  the case that *requires* one output inverted, so this sums to a **−6 dB null at 2.5 kHz**. It
  is inherited, it is not the playing tune, and the car is unaffected — but it is a defect, not a
  preference.
- **The firmware does not gate filterType 1 to even slopes.** Seven inherited files carry
  **slope 2 (18 dB/oct) with filterType 1 (LR)** together. All seven are the LP slot at 20 kHz
  with `Enable 0`, so none is audible — but the combination is writable, and if one is ever
  enabled the table above does not describe it.
- **The original voicing author low-passed the 6.5" at 1000 Hz — in six of seven independent
  inherited tunes.** §9.0.3's blind roughness analysis put that driver's breakup onset at
  **905 Hz**. An independent measurement and a hand-tuned voicing agreeing to within a sixth of
  an octave is the strongest evidence yet for closing the cone low-passes, and it makes **our own
  map the anomaly**: every path LP-open to 20 kHz is what almost none of the inherited voicings
  did.
- **No output is inverted in any file** — all 12 files, all 8 outputs `revert 0`. The "delay plus
  a polarity flip" trap is **checked and clear**, not merely assumed.

---

## 7. Time alignment — what is possible and what is not

**Delays are not derivable from these measurements.** The captures are magnitude only: 1/6-octave
levels, no phase, no impulse response. Arrival time is not in that data at any amount of cleverness.
Anyone claiming to derive delays from an RTA magnitude curve is guessing.

**On the device now** (carried through the 2026-10-20 tune untouched, verified unchanged):

```
path 0..7 delay ms:  5.4   3.8   6.0   4.1   4.6   5.0   3.8   4.4
```

These came from the owner's own measured distances in the repo (`make_tune_v7.py`) with the sub as
reference (estimated 250 cm). Checked: the *differences* between channels match those distances
(ch3 ≈ 1.9 ms behind ch4; the distances imply 1.7 ms). Only the absolute offset differs, equivalent
to a sub reference nearer 215 cm. **Leave them alone** unless you can measure better — a blind
change moves the stage in a random direction.

The real path to alignment: a **3.5 mm → RCA cable into the DSP's stereo aux**, one machine plays a
sweep through the aux and records it back, and `SweepTool` (`src\SweepTool\SweepTool.cs`) correlates
each channel against the loopback. Outputs `delayMs`, `delaySamples`, `coef`, and refuses a take
without a real arrival (presence `coef ≥ 0.080`, plus chunk-by-chunk completeness, with the band
taken from the *reference*). `verify-sweep-gate.ps1` (11 checks) proves the gate after any change to
the analyse path.

**The structural limit remains:** paths 4–7 are mono pairs, so one delay must serve two drivers at
different distances. That is a hardware limit; more DSP channels fix it, EQ cannot.

Writing delays is `POST /api/output` with `delayMs[16]` — the **complete block**, see section 8.

---

## 8. Writing a tune to the DSP

### The API contract

```
POST /api/eq      { mem, path, byPass, bands: [ {fc, gain, q, type, enable}, ... ] }
                  Fewer than 31 bands is fine - the rest are written as gain 0.
                  Fc is an integer Hz; type 0 = peaking; enable true.
POST /api/eqread  { mem, path } -> { ok, byPass, bands[31]{ fc, gain, q, coeffs, enable } }
POST /api/output  { mem, mute[16], gainDb[16], delayMs[16], revert[16] }   <-- COMPLETE BLOCK
                  A missing vector is rejected. An omitted entry becomes a zero, and zero is
                  not "leave alone": it is 0 dB full level. Partial blocks are refused.
GET  /api/info    connection, and the ONLY place the output block is served:
                  outputGainDb[16], outputDelayMs[16], outputMute[16], outputRevert[16], outputKnown
POST /api/status  master/mute/source/memory/groups - does NOT carry the output block
```

`/api/info` vs `/api/status` is a real trap: the writer read `/api/status` and threw on its first
real write. Use **`/api/info`** for anything touching cmd 11.

### The writer

```
.\apply-tune.ps1 -Plan .\tune\tune-plan.json              # snapshot, write EQ, write output block, verify
.\apply-tune.ps1 -Plan .\tune\tune-plan.json -WhatIf      # snapshot + print the gain table, write nothing
.\apply-tune.ps1 -Restore '.\evidence\tune-before-<stamp>.json'
```

What it does, in order — and why each step exists:

1. **Snapshot the entire live state** to `evidence\tune-before-<stamp>.json` (gains, delays, mutes,
   polarity, and all 31 EQ bands for all 8 paths). This is the only way back.
2. **EQ per path**, then read every band back and compare Fc (±1 Hz), gain (±0.11 dB), Q (±0.15).
3. **The output block, once, complete**, with delays/mutes/polarity preserved from the snapshot.
4. **Read the gains back**, and assert the delays did not move.
5. **Refuse to report success** unless every path verified. A verifier that checks nothing must
   never print "applied".

Gains in a plan are **deltas**, resolved against live values at apply time, so a plan stays valid
after the owner changes something by ear.

### Rate limits and reality

- Output gain floor is −40 dB. The 2026-10-20 tune put path 6 at **−30.0** and path 7 at **−22.1**
  because they measured 16–18 dB hot. That is legal, but it is also 16 dB of digital cut papering
  over a cranked amp gain. If a channel needs more than ~12 dB of cut, say so and suggest the amp
  gain knob — the DSP can do it, but the amp may already be clipping on transients.
- C# changes need `build.ps1` (or `build-sweep.ps1`), and the **bridge must be stopped first** — it
  locks `bin\LibHoneyWg.dll` and the copy step fails otherwise. HTML/JS changes need only a reload.

---

## 9. Building the tune itself

`tune\fit-tune.py` is the reference implementation. It is **pure stdlib** (numpy is not installed on
this machine's Python 3.15 and a stack was not installed for one job). Read it before rewriting it.

```
python .\tune\fit-tune.py --meas ".\measurements\Car tune (2026-10-21)" --out .\tune\tune-plan.json
```

It writes `tune-plan.json` (the machine input for `apply-tune.ps1`) and `tune-plan-report.md`
(the human table: bands per path, fit quality, gain deltas).

### The target

The owner's own R&B / hip-hop house curve — from the repo's `make_tune_rnb.py`, which exists because
a neutral tune sounded *"vocals good, bright"* to him. dB relative to 1 kHz:

```
 20  +6.0    35  +6.5    55  +6.0    80  +4.5   120  +3.0
180  +1.5   300   0.0   500   0.0   800  -0.3  1000  -0.5
 2k  -1.5   3.5k -2.5    6k  -3.5    10k  -4.5   20k  -5.5
```

Bass shelf, gentle mid tilt, treble rolloff. The three hip-hop moves added on top of the repo curve:
+0.5 dB at 35–55, +0.5 at 80 (kick), −1 at 300 (mud), a touch more rolloff at 20 k.

**A flat tune is not what this system wants** and is not what the owner asks for. If he asks for
"flat", confirm — he means the curve above, which his ears already chose once.

### The method, and every rule in it

1. **Fit shape per channel, set level by gain.** Fitting a 16 dB error with EQ eats every band and
   still misses; level errors are gain errors. EQ corrects shape, gain corrects level.
2. **Fit only where the driver is trusted.** Per-channel windows (lo, hi) and a "home" band where
   that driver is the dominant source:

   | channel | fit window | home band | Q max |
   |---|---|---|---|
   | tweeter 1–4 | 1500 – 14000 | 3000 – 6000 | 2.0 |
   | midrange 1–2 (3.5") | 110 – 9000 | 300 – 800 | 4.0 |
   | midrange 3 (8") | 70 – 5000 | 80 – 160 | 4.0 |
   | midbass 1 (6.5") | 80 – 7000 | 100 – 200 | 4.0 |

   The low edge sits above the high-pass corner on purpose: boosting at the corner fights the
   crossover and wastes headroom.
3. **Anchor level on the MEDIAN of the home band**, not the mean. The 6.5" has a +10 dB resonance at
   100–125 Hz inside its home band; a mean drags the whole channel down ~3 dB to fix one mode the EQ
   bands already cut.
4. **Cut freely (−12 dB), boost rarely.** A boost needs `Q ≤ 1.4` (broad). Narrow dips are usually
   cancellations or reflections; boosting one burns headroom and moves the problem.
5. **No lift above 12 kHz** on tweeters (the measurement there is near the mic's own floor) and **no
   lift above 2 kHz on any cone** (a cone 12 dB down pushed back up is a second source fighting the
   tweeters, not more treble).
6. **Cap the total lift at +4 dB.** Two broad +3 dB filters a third of an octave apart are a 6 dB
   shelf nobody asked for. Stacked lifts are how a correction becomes brightness.
7. **Never spend two bands in the same place** — skip any candidate within 1/6 octave of one already
   chosen.
8. **Smooth to 1/3 octave before fitting**, so it chases trends, not room ripple. 6–14 bands per
   channel; 31 are available per path.
9. **One common gain shift.** Compute each channel's level delta, then shift all of them so the
   channel that needs it least is unchanged. Never boost a channel above where it already sits.
10. **Report the residual, do not hide it.** A cone will keep ~5 dB rms of error at the bottom of the
    bass shelf because it physically cannot produce it. That stays a residual rather than a fake
    boost.

### 9.0 Level matching — the stage shape-fitting cannot do

Source: Rawcat, *Back to basics - level matching* (https://www.youtube.com/watch?v=Ye0pQHxE49Y),
transcript taken down by the owner 2026-10-23. This is **doctrine**, not measurement: where it
conflicts with §5's measurement rules, those still win.

Rule 1 above ("EQ sets shape, gain sets level") is only half the job. Fitting every driver's
*shape* to the target and leaving the drivers at whatever *level* they happened to sit at will
**not** sum to the house curve — a tweeter 2–3 dB hot after a perfect shape-fit gives the summed
system a rising top end the house curve never asked for. So level matching is a separate stage,
run after the shapes are in:

1. **EQ sets shape, channel gain sets level.** Each driver must sit at its target *level*, not
   merely follow its target *shape*.
2. **Read level at the band pass, under heavy smoothing.** Apply 1/1 smoothing (1/3 for tweeters)
   *before* comparing a driver to its target, over the passband only, then re-check without
   smoothing. Do not judge level at the crossover corner — the electrical crossover owns that region.
3. **Match left to right first, then both to the target.**
4. **Find the weakest link and pull everything down to it.** Rawcat's stated preference, because
   there is normally plenty of level left. Only if the car is too quiet afterwards do you raise the
   weakest link — and then **amplifier gain first** (it buys headroom), DSP channel gain for the fine
   trim.
5. **Coarse on the amp, fine in the DSP.** If an amp's gain knob is up, turn *it* down rather than
   burning attenuation in the DSP.
6. **The sub's target level is set against the SUMMED mains**, not a single driver's response: two
   correlated mains sum ~+6 dB over one, so a sub matched to a lone driver's target lands ~6 dB low
   against the real system. Rawcat raises the sub target by 6 dB before matching.
7. **If the sub clips, set it to max clean and pull every other driver down by that same amount.**
   The sub becomes the weakest link and the whole target set follows it down.
8. **Too much midbass reads as "dirty", not "punchy."** It is usually the biggest pull-down there is.

**How this maps to *this* car — three differences from Rawcat's rig, all non-negotiable:**

- **Paths 4–7 are mono pairs on one channel.** There is no left and right to match on the 3.5"
  pairs, the 8" pair or the 6.5" pair: one gain, one EQ curve, two drivers. Rule 3 therefore applies
  **only to the tweeters** (paths 0–3), which are one driver each.
- **The pair summation is already inside the measurement.** Both drivers of a pair play during a
  sweep, so the +6 dB of rule 6 is baked into paths 4–7 already. What rule 6 still applies to is the
  **sub** — and see the next point.
- **The sub has no DSP level, and no sub measurement exists.** `run-20261020-1925` holds eight files
  and the run's own conditions say *sub amp disconnected*. So rule 7's only levers are the head
  unit's sub trim and the Kicker's gain knob — invisible to the DSP and to `system-predict.py`.
  **No amount of 31-band fitting can level-match the sub.** It is a manual measurement step that has
  never been done, and it is the reason a tune can look perfect on paper and still be thin or boomy
  at 40 Hz (§1, "the sub is outside the DSP").

**The conflict with the A/B rule — read this before touching `--pin-gains`.** Two different things
are both called "level match":

| meaning | what it equalises | who needs it |
|---|---|---|
| **A/B level match** | overall loudness *between two tune files* | §9.1 rule 2 — an A/B at different volumes is a loudness test, not a voicing test |
| **driver level match** | *relative driver levels inside one tune*, so they sum to the house curve | rules 1–8 above |

`--pin-gains` serves the first and **silently disables the second**: it sets every `gainDeltaDb` to
zero, so the stage-2 level stage never runs. Tune 3 and `4-maxfit-31b` are both pinned. The machinery
for driver level matching exists (rule 9's common shift is Rawcat's rule 4, nearly verbatim) — it is
switched off to keep the loudness platform comparable to tune 2.

**Consequence: a good residual is not evidence of level matching.** `review-tune.py` grades the
*system sum* against the house target and the *between-tune* level; it does not report per-driver
band-pass level against that driver's target level. Nothing in the toolchain currently measures the
thing rules 1–8 describe, so a build can pass every gate and still have an unmatched driver.

### 9.0.1 Fine tuning — phase, summation and crossover type (Rawcat, 5-way series)

Source: RAW-CAt, *fine tuning* (the 5-way series), transcript saved verbatim at
`DSB\refs\rawcat\fine-tuning-5way.txt`, taken down by the owner 2026-10-23. Same standing as
§9.0: **doctrine, not measurement.** Where it conflicts with what this car can actually be
measured with, the measurement rules win — and on phase that is exactly what happens (see
"What is blocked" below).

His rig differs from ours in one way that shapes everything here: **every driver has its own
DSP channel**, so he can match left to right and time-align each one. Ours cannot (§1, §7).

**1. EQ is a loop, not a pass.** He EQ'd, then fixed timing and phase, then **EQ'd again with
the leftover bands** — "i have extra bands left" — because fixing alignment changed the
magnitude enough to need a second fit. Our pipeline does one fit against a fixed topology.
Every crossover or delay change invalidates the fit that preceded it; re-run the fitter after
any of them, do not hand-patch bands on top.

**2. Align on PHASE in the crossover region, not on the impulse peak.** He states it outright:
"i looked more at the phase graphs than the actual impulse response." He set timing to zero,
then nudged **+0.2 / +0.4 ms** purely to make the phase lines track, and accepted that the
impulse response still "looks like it's off". Judge alignment by whether the phase curves
overlay **across the band both drivers share** — 40–150 Hz sub-to-midbass, 100–300 Hz
midbass-to-mid — not by peak alignment.

**3. A dip that sits on a phase wrap is a PHASE problem — do not EQ it.** He identified a dip,
saw an extra phase wrap at the same frequency, and proved it: it was a reflection off the
driver's-side window. Chasing it with magnitude EQ burns bands on something EQ cannot move.

**4. All-pass filters can fix the phase plot and make the amplitude worse.** He added an APF at
500 Hz Q4: the phase traces matched, and the RTA measurement showed a **bigger dip**. He
removed it. The rule is not "APFs are bad" — it is **never trust the graph without a
re-measurement**, in either direction.

**5. The summation ladder — the most consequential item here.** "At the lower frequency you
have more summation... the mid bass they're gonna sum plus 6 dB and tweeter is going to sum
plus 3 dB." Two sources at the same frequency sum **+6 dB when coherent** (wavelength much
longer than their spacing) and **+3 dB when fully uncorrelated** (power sum). The transition
is gradual and frequency-dependent, not a step.

*What this already means in our toolchain:* `system-predict.py` prints **both** envelopes —
`system_energy` (10·log10 Σx², fully uncorrelated) and `system_coherent` (20·log10 Σx, fully
coherent) — and its own header says the honest answer between them "cannot be settled without
a measurement". The `spread` column in every review is exactly that gap. Rawcat supplies the
missing interpolation rule: **the bass end of the spread is nearer the coherent envelope and
the treble end nearer the energy envelope.** The tool brackets the truth; he tells us which
side of the bracket each frequency sits on. Nothing needs rewriting — but read the spread
column as "this is where phase decides it", not as noise.

**6. Crossover TYPE is a variable, not a constant.** He swept every filter type at the same
crossover **point** and Bessel summated best between tweeter and mid — "not crossover points
but crossover types". We hard-code **LR24 on every path** and have never tested the choice.
This is the most directly actionable item in this transcript.

**7. EQ the pair after it sums, not only the individual drivers.** He fitted L and R
separately, then overlaid the pair's summed response against the target, found it did not
follow, and **EQ'd the pair again**. Ours is one step further along already: paths 4–7 are
measured *as pairs*, so the summation is inside the capture and the fit sees it. The tweeters
(paths 0–3) are the exception — each is measured alone but all four play at once, so their
combined level is the one part of our system response that is **predicted, not measured**.

**8. Pull the midbass back after summing.** "Plus 6 dB... is a bit too much midbass" — he
reduced it twice. This is §9.0 rule 8 arriving from a second, independent direction.

**9. The 1–2 kHz scoop is a preference knob, and he left it open on purpose.** "People do like
to scoop out one or two dB between one or two k... if i'm not gonna like it i'm just gonna
bring it back up." He shipped an unresolved flatness question deliberately and A/B'd it by
ear. Note the band: the same 1–2 kHz region where our own STATE records an overshoot.

**What is blocked here, and it is a hard block.** Rules 2, 3 and 4 all need **phase**. §7 is
explicit that our captures are magnitude-only — 1/6-octave levels, no phase, no impulse — so
"arrival time is not in that data at any amount of cleverness". No amount of doctrine changes
that. What it buys us is the *interpretation*: when a dip appears and we cannot see phase, we
should now say "this may be phase or reflection, and magnitude EQ may not be the answer"
rather than fitting it. The 3.5 mm → RCA loopback in §7 is what unblocks rules 2–4 for real.

**The finding that ties this to the open crossover decision.** Because every low-pass on this
car is **open at 20 kHz** (§6), more paths overlap at more frequencies than a properly crossed
system would allow — which is why the review's `spread` column stays **5–9 dB across the whole
range** instead of collapsing toward 0 where a single path owns the band. The spread peaks at
**8.7 dB at 1.6 kHz and 8.0 at 2 kHz** — the handover band, and exactly where STATE records
tune 1 overshooting the target by +5 to +7 dB. So the largest *prediction* uncertainty and the
largest *fit* error are in the same place, and rule 6 says the fix may be a different crossover
type or a real low-pass rather than more magnitude EQ. This is evidence for closing the
crossovers, from a direction STATE had not considered: **it would shrink the uncertainty, not
just the error.**

### 9.0.2 Summing — the measurement this rig has never taken (Rawcat, 5-way series)

Source: RAW-CAt, *summing*, transcript verbatim at `DSB\refs\rawcat\summing.txt`, taken down
2026-10-23. Same standing as §9.0/§9.0.1: doctrine, and on this subject it runs straight into
the hardest limit in this file.

**The method — build the system up cumulatively and measure the SUM after each addition.**
He measured subs alone, saved it; added midbass, measured; added midrange, measured; added
tweeters, measured. Each step answers one question: *does this group still sum with what is
already playing?* Never one-driver-at-a-time with everything else muted — which is the **only
kind of capture this rig has ever taken.**

**The diagnostic rule, and it is the sharpest thing in this transcript:** *a defect present in
the SUM but absent from every individual measurement belongs to the relationship between those
drivers, not to any of them.* His mids measured fine alone and did not sum together. You
cannot EQ a relationship. The two candidates are, in the order he tried them:

1. **Polarity.** He found his midrange had inverted polarity outright. Fixing it did **not**
   fully fix the sum — so he then found the residual was a separate **timing** error. Two
   independent faults, same symptom. Check polarity *before* spending time on delay.
2. **Timing / delay.** The remaining ~5 dB dip between midrange and front sub: "this is
   basically a timing issue so it's going to be something to do with the delay".

He also tested tweeter polarity **empirically** — "let's change the polarity of the tweeters
because why not" — and it helped. Test it by measurement, never by reasoning about it.

**Verify summing with NO smoothing.** "Even if i will do no smoothing... all these sum
perfectly." Smoothing hides exactly the narrow nulls a polarity or timing error produces.

**What this costs us, stated plainly.**

> ### CORRECTION 2026-10-25 — polarity *can* be read back
>
> The first bullet below says polarity "can never be read back". **That is wrong**, and it was
> wrong because two earlier sessions reported that `DS18_DSP_protocol.md` and `decompiled\AP.cs`
> did not exist — so nobody ever read them. They do exist, one directory above this project.
>
> **ReadParam sub 1 carries it.** `StructDspCfg.memOut[4]` holds `enable`, `revert`, `gain`
> and `delay` for all sixteen outputs across all four memories, at byte offsets **1692 / 1760 /
> 1828 / 1896**. Fetch with `GET /api/dspcfg`; decode `revert & (1<<path)` for polarity,
> `-u16/10` for gain, `u16/100` for delay. The vendor app does precisely this — `AP.cs:10426`
> decodes it into `mDP12ReadFromDsp` and `AP.cs:10187` compares device-read against its own
> mirror — and `load-tune.ps1` step 4c already decodes byte 1760 to verify the output block.
> `src\BridgeServer.cs:1639` says the same thing the comments elsewhere in that file deny.
>
> What *is* true: **cmd 11 itself has no ACK and no response payload**, and the bridge's
> `/api/info` mirror is bridge-local. The app deliberately does not implement the sub-1 decode
> for output state, so `MIRROR ONLY` is the honest label for it — and its `MuteClaimsVerified`
> guard must stay.
>
> **What this changes:** the "summing is permanently unverifiable" framing below is too strong.
> Polarity and level are verifiable with a code change and a car session. The summing *capture*
> is still needed for the combined acoustic response — that part stands. Full write-up:
> `OPEN-QUESTIONS.md` **Q6**.

- **Polarity is writable on this DSP but can never be read back.** ~~CORRECTED 2026-10-25 —
  see the box above: it can, via ReadParam sub 1.~~ It rides in command 11 with
  level, delay and mute; `src\BridgeServer.cs` records that *no ReadParam sub-command covers
  output mute/gain/delay/polarity*, so the value is mirror-only — the app labels it `MIRROR
  ONLY` and a swap can only ever reach `EQ verified, levels unproven` (APP-NOTES §3).
  `web\index.html` states the consequence outright: *"per-output delay/polarity are phase,
  invisible to a magnitude sum."*
- **`apply-tune.ps1` preserves polarity from the live snapshot rather than setting it.** It
  reads `outputRevert` and writes it back unchanged. So no loader in this repo has ever
  *forced* a known-good polarity, and none has ever *verified* one.
- **Checked 2026-10-23, all twelve library `.idsp` files: every `revert` is 0 on all eight
  outputs.** No inverted path in any file, including the inherited bor8f3/xswmps/ai2/tinker3
  lineage. That is a **file-level** fact. ~~A device-level check is now possible (Q6) — nobody
  has run it, and nothing here decodes `revert` from a live sub-1 read yet.~~
- **Within-pair polarity is invisible even to the file.** One `revert` bit covers one *output*,
  but paths 4–7 each drive **two drivers**. A pair wired half backwards reads `revert 0` and
  would measure ~6 dB down with comb filtering across the whole shared band. The measured
  levels of paths 4–7 are all *hot*, not 6 dB down, which argues against it — but that is an
  inference from level, not a measurement of polarity.

**The gap, named.** Everything above resolves with one thing this rig does not have: a
**summing run** — all outputs up, one capture, nothing muted. Without it, polarity and the real
combined response are permanently unverifiable, and every statement about how the system sums
is a prediction (`system-predict.py`'s energy envelope), not an observation. Two blockers to
note before anyone builds it: the run box **requires** the sub-disconnected tick before it will
start (§3), which is exactly backwards for a system-sum measurement; and paths 0–3 are four
separate outputs that all play at once, so their combined level has never been measured either.

### 9.0.3 Crossover selection — measure raw, then place the points (Rawcat, 5-way series)

Source: RAW-CAt, *crossovers and targets*, transcript at
`DSB\refs\rawcat\crossovers-and-targets.txt`, taken down 2026-10-23. **That transcript is
TRUNCATED** — it stops mid-sentence at ~19:22 while he works on a 26 Hz sub peak, so the sub
half of this topic is missing. The crossover half is complete.

**1. Measure every driver RAW — crossovers bypassed — before choosing a single point.** His
opening move, and he forgot and re-bypassed three times mid-session, which tells you how easy
it is to measure a driver through a filter by accident. We already have the right tool:
`crossover-range.ps1` "measures every driver full range, with the tweeters still protected",
opening the passband and restoring it after. The protection rule survives it — its `-TweeterFc`
defaults to **1500**, the rated floor (§3).

**2. Protect the tweeter BEFORE measuring, not after.** He set a 1 kHz 12 dB/oct high-pass on
unknown new tweeters (Fs 740) purely for protection, then measured. Same instinct as §3's
1500 Hz floor; the reason is the same, only his floor is lower because his Fs is.

**3. Verify gain staging before trusting any measurement.** His sub came back "very very little
level"; the cause was a **bass knob on the controller sitting half up**, not a fault. Fixed it
and re-measured. This is the ATOM/Kicker trap in §2 — a mis-set gain knob reads exactly like a
broken driver. Also: identify and **ignore** known environmental artefacts (he discarded a 170 Hz
buzz from the car's ECU) rather than letting the fitter chase them.

**4. Choose the crossover where BOTH drivers are clean, with a FULL OCTAVE of overlap.** His
own reasoning, verbatim: *"if i'm gonna have 2k down to 1 is going to be a full octave which is
going to give me a very nice slope here."* The octave is what gives the filter slopes room to
sum properly inside two well-behaved regions. And **never place a point in a nasty area** —
"this is a very nasty area to have a crossover" — meaning a peak, a deep null, or breakup.

**5. "Clean" means SMOOTH, not FLAT.** This distinction cost an hour here and must not be lost.
An analysis written against flatness (deviation from the driver's own median, ±6 dB) reported
"no clean overlap" for every adjacent pair. Inside a car cabin that metric measures **the room**
— modes below 300 Hz and cabin absorption above ~4 kHz — not the driver; a tweeter looked usable
only from 1.3–3.2 kHz. A tilted but *smooth* response is a perfectly good crossover region: the
tilt is what §9's EQ is for. Judge roughness (local peak-to-peak in a 1/3-oct window), not level.

**6. Where he landed, and it is a useful prior for this car too:** midbass→mid at **200 Hz**,
mid→tweeter at **2 kHz**. His house curve: **flat to 200 Hz, then a gentle 2–3 dB/octave rise
reaching about +12 dB at 20 Hz** — "an SQ daily" slope. Note that against our own target (§9's
curve, +6 dB at 20 tapering to 0 by 300): his bass shelf is roughly twice as deep. Different car,
different sub, different genre — a data point, not a prescription.

**What this run's data already says (var\_xover-probe.py, no hardware needed).** The canonical
run has **every low-pass open**, so each driver's full *upper* range is captured and the breakup
limits below are trustworthy. The *lower* roll-offs in the same data are the current map's high-
pass skirts, not the drivers — do not read them as driver limits.

| path | driver | smooth band | breakup onset |
|---|---|---|---|
| 6 | 8" front pair | 20 – 3225 Hz | **3620 Hz** |
| 7 | 6.5" rear pair | 20 – 806 Hz | **905 Hz** |
| 4 | 3.5" front pair | 20 – 905 Hz | 10240 Hz |
| 5 | 3.5" rear pair | 20 – 906 Hz | 10240 Hz |
| 0–3 | tweeters | **1280 Hz** upward | none to 20 kHz |

- **The 6.5" breaks up at 905 Hz — and is currently open to 20 kHz.** It is also the **loudest
  driver in the car** (home-band median −18.4 dB against −27 for the 8", −39/−46 for the 3.5",
  −53/−60 for the tweeters). The most sensitive, most breakup-prone driver is playing full-range
  over everything above 1 kHz. That is the standout finding of this analysis.
- **There is a hole between the 3.5" and the tweeters.** The 3.5"'s smooth band ends at ~905 Hz;
  the tweeters only become smooth at ~1280 Hz (they are still on their 1500 Hz LR24 skirt below
  that). Nothing is smooth across roughly **900–1300 Hz**.

**Three independent lines of evidence now land on the same band**, and that convergence is the
result worth trusting:
1. STATE: with the cones open, tune 1 overshoots the house target by **+5 to +7 dB at 1.25–2 kHz**.
2. §9.0.1: the prediction's coherent-vs-energy spread peaks at **8.7 dB @ 1.6 kHz**.
3. This: **no smooth overlap** between the 3.5" and the tweeter; the gap is ~900–1300 Hz.

**The action this implies is subtractive, not additive: low-pass the cones.** Roughly 200 Hz on
paths 6 and 7 (below the 6.5"'s 905 Hz breakup, inside both smooth overlaps, and matching
Rawcat's own choice), and a low-pass on paths 4 and 5 somewhere in 1.3–2 kHz so the 3.5"s stop
fighting the tweeters up to 20 kHz. §9.0.1 already argued closing the crossovers would *shrink
the prediction uncertainty*; §9.0.2 argued the summed response is unverifiable without a summing
run. All three now point at the same edit.

**Honest limits on the numbers above.** Breakup here is roughness (local raggedness); §2's
"beams above" ratings are directivity — related, not the same criterion, and the two disagree
(6.5" rough at 905 vs beaming at 2.1 k). These are also *predictions from a single seat at a
single moment*, and per §9.0.2 the summed response has never been measured. Treat the ~200 Hz
points as well-evidenced and the 1.3–2 kHz point as provisional until a summing run or a
`crossover-range.ps1` capture settles it.

### Worked example — the 2026-10-20 tune (the current starting point)

From the 19:12 run; bands per path 5/5/4/4/11/7/11/10; fit 0.9–1.9 dB rms on tweeters, ~5–6 dB on
cones; gain deltas 0 to −17.9 dB.

| path | channel | gain before | gain after | why |
|---|---|---|---|---|
| 0 | tweeter 1 | −4.6 | −6.8 | |
| 1 | tweeter 2 | −7.0 | −8.1 | |
| 2 | tweeter 3 | −7.0 | −9.0 | |
| 3 | tweeter 4 | −4.0 | −8.1 | rear tweeters pulled further back — stage |
| 4 | 3.5" front | −3.0 | −8.3 | |
| 5 | 3.5" rear | −3.0 | −3.0 | the reference channel; everything else shifted to it |
| 6 | 8" front | −14.0 | **−30.0** | measured 25–30 dB hotter than the tweeters |
| 7 | 6.5" rear | −5.0 | **−22.1** | +10 dB resonance at 100–125 Hz, and hot overall |

Way back: `evidence\tune-before-20261020-202655.json`.

### After applying — set expectations honestly

Paths 6 and 7 lose 16–18 dB. The low end **will** feel lighter; that is the measurement, not a
fault (tweeter peak −49 dBFS vs 8" peak −18 dBFS at identical stimulus). **The sub is not in this
tune at all** — if it is thin, the answer is head-unit sub level or the ATOM gain, not more EQ. Say
that before he drives, or the tune gets blamed for a sub he never re-levelled.

---

### 9.1 Predict it before you write it — the step that was missing until 2026-10-21

A tune is not finished when the device matches the file. That only proves the transcription. Run:

```
python tune/system-predict.py <a.idsp> [<b.idsp>]
python tune/system-predict.py --selftest
```

It rebuilds the system response from the stored measurement, because the 19:12 run was taken with
the EQ flat and its path gains are known (`evidence\tune-before-20261020-202655.json`):

    predicted_i(f) = measured_i(f) - gain_at_measurement_i + gain_tune_i + eq_tune_i(f)

Three rules follow from it, and they are now part of building a tune:

1. **State the residual.** rms error against the house target, 60 Hz–10 kHz, printed next to the
   tune. The car as measured is the baseline (7.6 dB on the 2026-10-20 data); a tune that does not
   beat it is not a tune.
2. **Level-match before comparing.** Two files whose predicted levels differ by more than ~6 dB
   cannot be A/B'd by ear — the louder one wins regardless of voicing. On 2026-10-21 the pair
   differed by 15–29 dB below 800 Hz, which invalidated the comparison the library existed for.
3. **Match the topology.** A voicing fitted with closed low passes (all six inherited files:
   1 000–4 200 Hz) is not transferable to a system whose low passes are open at 20 kHz. The atlas
   prints the map each file assumes; compare it before borrowing a curve.

Reading the numbers honestly: the sum of eight paths from magnitude-only captures cannot know
arrival phase, so the tool prints the energy sum and the coherent sum. A narrow gap means one path
owns the band and the value is good; a 6–9 dB gap means the band is a phase lottery and only a
measurement settles it.

---

## 10. Auto-save after every completed cycle

The page calls `runSaveToDisk()` from `runFinish()`, which POSTs `{ count, label }` to
`/api/run-save`. The bridge takes the last `count` captures from `rta-measurements.json` and writes:

```
measurements\Car tune (2026-10-21)\
    channel 1.json   channel 1.csv
    ...              ...
    channel 8.json   channel 8.csv
    README.md            (peak, gate, burst, and the grid/units stated per file)
```

- `channel N` = DSP **path N−1** (channel 1 = tweeter 1 … channel 8 = midbass 1).
- A same-day re-run becomes `Car tune (2026-10-21) (2)` — **never an overwrite**. A measurement set
  that was silently replaced is worse than no set.
- A **partial** cycle writes nothing (the save fires at `runFinish`, not per channel), so you will
  not get a half-labelled folder. If a cycle dies partway, the captures stay in
  `rta-measurements.json` and can be quarantined (section 12).
- To re-export an older run on demand: `.\export-meas.ps1` (same schema, different filenames).

---

## 11. Gotchas that have already cost time

1. **`/api/info`, not `/api/status`.** Only info serves `outputGainDb/DelayMs/Mute/Revert`. Status
   is the light endpoint. This crashed the first real tune write.
2. **PowerShell is case-insensitive.** `param([string]$Plan ...)` plus `$plan = ... | ConvertFrom-Json`
   assigns to the *parameter*, and the `[string]` constraint coerces the parsed object into a string.
   `$plan.paths` became `$null`, both loops ran zero times, and the script printed **"TUNE APPLIED"**
   having written nothing. Use a different variable name, and make the verifier assert a nonzero
   count.
3. **`rta.exe`'s device index goes stale on every replug** — section 5.1. The run box now fails on
   it (`devDrift`), and on the laptop array generally. Do not "fix" a red mic row by weakening it:
   that row is the only thing between you and eight channels of room tone.
4. **A warning does not block a run; only a `fail` does.** `RUN.ready` counts failures. The original
   mic row warned — and an entire eight-channel cycle went through recording the laptop's own
   array. If a check matters, render it `fail`.
5. **The stimulus slider shipped at −24 dB.** `rtaStimGain` in `web/index.html`; default is −6 now.
6. **The browser BOM.** PowerShell 5.1's `Set-Content -Encoding UTF8` writes a BOM; Python chokes on
   script files that start with one, and `json.load` needs `utf-8-sig` for data files. Write with
   `[System.IO.File]::WriteAllText(path, text, New-Object System.Text.UTF8Encoding($false))`.
7. **numpy is not installed** and must not be installed casually (disk is tight). The fitter is
   stdlib on purpose.
8. **A peaking filter's shape is not linear in gain.** Solving a band's gain against its 1 dB
   response optimises a bell you are not applying — that pushed a fit from 3.5 to 6.5 dB rms. Use a
   guarded coordinate search that can only accept an improvement.
9. **Index order when summing per-filter responses.** A residual check that sums with the wrong
   order reports the filter bank's own error as the leftover error — it made a good fit look like it
   was making things worse.
10. **Fields of the same name in nested scopes.** Adding `gate` to a C# method that already had a
    local `gate` is CS0136 (compile error), not a silent bug — but the build output is easy to miss
    in a long log; grep for `error CS`.
11. **The bridge locks `bin\LibHoneyWg.dll`.** Stop it before `build.ps1`.
12. **`Start-Process -ArgumentList` with an array breaks dshow names containing spaces** — use
    `Start-Job`.
13. **`[ordered]@{}` rejects integer index assignment** — use `@{}` for `$arr[$i] = ...`.
14. **Never inject taps or swipes on the phone.** Read-only `dumpsys`/`screencap`/`pull`/`logcat`
    are fine; always target by serial.
15. **Firmware upgrade (cmd 17) is out of scope.** Brick risk. Do not.
16. **A tune filename with a space will not load.** The page fetches the file by the exact name
    `/api/tunes` returned, and `ServeStatic` uses `ctx.Request.Url.AbsolutePath` verbatim - still
    URL-escaped, and lowercased - then hands it to `File.Exists`. `my tune.idsp` arrives as
    `my%20tune.idsp` and 404s; the Tune tab just says "Could not load". Every file in the library is
    space-free for that reason. The real fix is one line in `ServeStatic`
    (`Uri.UnescapeDataString(name)`) but it needs a bridge rebuild, so name files without spaces.
17. **A `.idsp` carries four memories and the Tune tab writes to the *file's* memory index**, not the
    device's. A file that only fills memory 0 writes flat bands if you pick M2 and press apply. Files
    built by `tune/build-swap-tunes.py` repeat the tune in all four blocks so any chip is the tune.
18. **`/api/eqread` does not echo the path it read.** Anything that compares reads against a snapshot
    must pair them by loop index, not by a `path` field on the response. This cost real coverage:
    `doctor.ps1`'s crossover check filtered `$reads` on `$_.path`, every read carried `$null`,
    `[int]$null` is `0`, so every snapshot row except path 0 found no match and was **skipped** -
    seven open crossovers would have printed "closed". Fixed 2026-10-21 by stamping `path` onto each
    read, and an unread path now counts as open instead of passing.

---

## 12. Quarantining a bad run

Captures accumulate in `ds18-bridge\rta-measurements.json` and the auto-save takes the **last N**,
so bad takes must be removed or they can be swept into a future folder.

```powershell
Copy-Item .\rta-measurements.json .\evidence\rta-measurements-before-quarantine-<stamp>.json
# then delete only the entries whose local timestamp is the bad cycle
```

Match on timestamp, never on position, and **refuse if the count is not what you expected** — that
guard is what stops a mistaken filter from deleting the good run. Verify afterwards that the keeper
run is intact (all eight entries, peaks unchanged) and that `GET /api/rta-meas` reports the new
count. The 2026-10-20 quarantine is the worked example in `STATE.md`.

---

## 13. End-to-end checklist

```powershell
cd 'C:\Users\sdotm\OneDrive\Documents\Default Project\ds18-bridge'

.\where.ps1          # phone URL for this network
.\preflight.ps1      # DSP opens, RTA re-pointed at the UMIK-1, doctor green (except the known
                     # "crossover OPEN" line, which is the owner's map)
```

Then, in the car:

1. Sub amp **off**, head unit at a normal level, master not muted.
2. Phone → `/play.html`, pick the band for the driver under test, leave it there (screen may lock).
3. Phone → **Tune** tab (the checks only poll there), set speakers, make the **sub disconnected**
   tick, **Verify setup**. Every row must be ✓ or ! — a red **measurement mic** row means the wrong
   input and the run will not start (section 5.1).
4. **Start run**. Watch for 8 green ticks. If a row goes red, read the reason, not the number.
5. The folder `measurements\Car tune (<date>)` appears by itself when it finishes.

Back at the laptop:

```powershell
.\after-run.ps1                                          # grade + plot the newest captures
python .\tune\fit-tune.py --meas ".\measurements\Car tune (<date>)" --out .\tune\tune-plan.json
.\apply-tune.ps1 -Plan .\tune\tune-plan.json -WhatIf     # read the gain table before writing
.\apply-tune.ps1 -Plan .\tune\tune-plan.json             # write + verify every band
```

Then have him listen and tell you which way it leans. **The drive is the measurement that matters
most**; the RTA cannot hear a stage, a mono midbass, or a sub that was never re-levelled.

---

## 14. File map

| path | what it is |
|---|---|
| `tune\fit-tune.py` | measurement folder → `tune-plan.json` + report. The tuning brain. |
| `tune\tune-plan.json` | the plan `apply-tune.ps1` consumes (bands, gain deltas, per path). |
| `DSB\tune-plan-report.md` | the same plan as tables a person can read. |
| `apply-tune.ps1` | snapshot → EQ → output block → verify. `-WhatIf`, `-Restore`. |
| `set-crossovers.ps1` | `plan` / `show` / `apply -Hp "0=1500"` / `restore -Snapshot`. |
| `crossover-range.ps1` | per-driver full-range measurement; tweeter floor 1.5 k; `restore`. |
| `preflight.ps1` | after a replug: DSP reopen + RTA re-point + doctor. Exit 0 = ready. |
| `after-run.ps1` | grade and plot the newest captures. No arguments. |
| `export-meas.ps1` | one JSON + one CSV per channel for a folder; writes the run README. |
| `doctor.ps1` | readiness. Its crossover row compares every read path against `evidence\crossover-snapshot-mem2.json`; if it says `OPEN`, the device really differs from that baseline. Refresh the baseline (`.\crossover-range.ps1 snapshot`) only when the crossover map is changed on purpose. |
| `flatten-tune.ps1` | take EQ out of circuit / put it back, with a snapshot every time. |
| `measure-sweep.ps1` | raw sweep capture + correlation (`delay`, `coef`) via `sweep-tool`. |
| `verify-sweep-gate.ps1` | 11 checks on the analyse gate. Run after any analyse change. |
| `var\probe-waveinid.cs` | proves what `waveInGetID` really reports for a WAVE_MAPPER handle — the evidence behind the mic gate. |
| `web\index.html` | the whole UI, the run cycle, `rtaStimGain`, the roster. |
| `src\BridgeServer.cs` | the API. `src\SweepTool\` is the correlator. |
| `evidence\` | snapshots: crossovers, EQ, tune-before, run captures, quarantines. |
| `measurements\` | per-channel files, one folder per run/cycle. |
| `EQUIPMENT.md` | driver and amplifier physics; the reasoning behind the limits. |
| `TUNING-BRIEF.md` | the complete hand-over brief: rig, path map, every file and where it lives, the rules, the phased work plan, the definition of done, 22 gotchas. Start here when taking the job over. |
| `tune\idsp.py` | read/analyse/write `.idsp` tunes with no hardware: `dump`, `curve`, `bands`, `diff`, `json`. Computes the response the bands actually produce. |
| `tune\build-swap-tunes.py` | builds `web\1-car-tune-2026-10-20.idsp` (measured tune) and `web\2-bor8f3-improved.idsp` (the B tune refitted). `--dry-run`, `--keep-notches`. |
| `tune\verify-swap-tunes.py` | 46 assertions that the two swap tunes still match their sources. Exit 1 on any drift. |
| `tune\report-tune.py` | structural + tonal numbers for any tune file: bands, Q, clamp counts, ripple, band averages. |
| `DSB\B-TUNE-REVIEW.md` | what is wrong with `bor8f3.idsp` and exactly what tune 2 changed, with the numbers. |
| `tune\verify-device.py` | reads the live DSP and asserts a tune file matches it band for band, plus gains, delays and crossovers. Read-only. |
| `tune\system-predict.py` | predicts the **system** response of a tune from the stored measurement (energy sum + coherent envelope + residual vs the house target). `--selftest` = 4 known-answer/negative controls. No hardware. |
| `tune\tune-atlas.py` | reads all 8 `.idsp` files, all 32 memory blocks: distinct payloads, crossover maps assumed, Q/gain/Fc census, shape similarity, outliers. No hardware. |
| `tune\review-tune.py` | reviews **any** `.idsp` against this car: predicted system response, residual vs the house target, level-match against a reference tune, topology check, correction-bank census, lineage. `--json` / `--md`. Powers the app's review panel. No hardware. |
| `DSB\PREDICTION.md` | the predicted system response of both swap tunes, with the level-match finding. |
| `DSB\ATLAS.md` | what the tune library actually contains, including the duplicate files and the topology mismatch. |
| `DSB\SELF-REVIEW.md` | graded review of this work against what it already had: what was verified, and what that verification did not cover. |
| `STATE.md` | what the rig looks like right now, and every lesson dated. |

---

## 15. Still unknown — do not invent answers

> **Superseded 2026-10-25.** This list was one of five copies that had drifted apart.
> Every unknown now lives in **`OPEN-QUESTIONS.md`** — one register, ranked by which
> decision each question blocks. Do not add an unknown here; add it there and leave a
> pointer in the document that cares.

