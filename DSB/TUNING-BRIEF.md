# TUNING BRIEF — the DS18 DSP8.6iA, this car, and everything learned so far

Last verified against hardware: 2026-10-21 *(inherited from `TUNE-PLAYBOOK.md`, the file that
was actually read back against the device; `STATUS.md` reports how stale that is)*.

Complete hand-over for whoever (or whichever model) does the audio tuning next.
Written 2026-10-21. Read the first three sections, then work from §8 onward.

---

## 1. What this job is

Tune a DS18 DSP8.6iA in a 2015 Dodge Charger for hip-hop/R&B, on real per-channel
measurements, with a swap-ready tune library — and hand the whole rig to a desktop app
that can show and prove everything.

**Everything measured is at the mic in the car.** There is no bench, no anechoic room, and
no internet in the car. The phone plays the stimulus over Bluetooth into the head unit; the
laptop captures through a UMIK-1, analyses, and writes to the DSP over USB. The sub is
outside the DSP entirely (fed from the head unit) and no DSP control can reach it.

**Owner rules, non-negotiable:**
- Never inject taps or swipes on the phone. Read-only phone dumps are fine. Always target
  the phone by serial.
- Every DSP write is verified by readback, and a revert path must exist for every write.
- Audio-only writes are authorised; crossovers/master are not, unless asked.
- One measurement per file. Always report which bands were used.
- Time alignment: "the best that you can" with the current kit.

---

## 2. The rig

| piece | detail |
|---|---|
| DSP | DS18 DSP8.6iA, USB, 4 memory slots, 17 outputs, 31 EQ bands per output, 2 passbands per output |
| amp | DS18 "AI DSP" (VID_1483 / PID_1095) |
| mic | UMIK-1, cal serial **7170061**, mono, 48 kHz |
| bridge | `http://127.0.0.1:8585` — DSP control, phone page, WAV stimulus service |
| RTA | `http://127.0.0.1:8586` — `/devices`, `/device?n=<i>`, `/rta.json` |
| phone | `http://LAPTOP-C7NA3SNN:8585/play.html` (stimulus, native WAV, survives screen lock) · `http://LAPTOP-C7NA3SNN:8585/` (DSP page + run cycle) |
| IP | the machine name is stable, the IP is not — `.\where.ps1` prints it |
| build | `csc.exe` (.NET Framework 4.x), **C# 5 only** — no `dotnet` install; project path has a space → `.rsp` response files; stop the running exe before rebuilding (file lock) |
| python | 3.15, **stdlib only** — no numpy, no venv, nothing installed to answer a question |
| disk | 4.9 GB free — a standing constraint |

**Stimulus** (served by the bridge, played by the phone): `web\sweep-20-20000-10s.wav` full
range · `sweep-1500-20000-10s.wav` tweeter · `sweep-200-18000-10s.wav` midrange ·
`sweep-110-12000-10s.wav` midbass · `sweep-25-200-10s.wav` sub · `pink-60s.wav` pink noise.
The page's stimulus slider (`rtaStimGain` in `web\index.html`) **defaults to −6 dB**; it
shipped at −24 dB and that was the whole "everything is quiet" mystery.

**The run cycle** (driven from the phone page): pick the 8 channels → for each: mute the
others, play that channel's sweep, capture, unmute → next. ~16 s per channel. It refuses to
start if the playing memory has any non-zero EQ gain, if the sub-amp tick is missing, or if
the mic is not provably the UMIK-1. On completion it POSTs `/api/run-save` and the laptop
writes a dated folder.

**Bridge API** (all in `src\BridgeServer.cs`): `/api/info` (the only endpoint that carries
`outputGainDb` / `outputDelayMs` / `outputMute` / `outputRevert`), `/api/status`,
`/api/output` (cmd 11), `/api/eq` + `/api/eqread` + `/api/eqcoef` + `/api/eqprobe` (cmd 14),
`/api/passband` (cmd 13), `/api/dspcfg` (cmd 15, raw StructDspCfg), `/api/master`,
`/api/input`, `/api/volume`, `/api/connect` · `/api/disconnect`, `/api/targets`,
`/api/tunes`, `/api/tunename`, `/api/rta-meas`, `/api/run-save`, **`/api/review`** (POST
`{name?, xml?, mem?, ref?}` → the car-context review of an `.idsp`; the bridge runs
`tune\review-tune.py` and returns its JSON unchanged, so the app and the reports can never
disagree).

---## 3. The path map (this is where mistakes start)

> **Canonical copy: `TUNE-PLAYBOOK.md` §2.** This table is kept because the brief should stand
> alone, but if the two ever disagree, the playbook wins and this one is wrong. Three copies of
> this map existed before 2026-10-25; there are now two, and one of them is named authoritative.

DSP path N is **roster channel N+1**. The owner's labels are the roster's labels, not

descriptions — he calls path 6 "midrange 3" and path 7 "midbass 1".

| path | channel | driver | amp | position | rated band | breakup |
|---|---|---|---|---|---|---|
| 0 | tweeter 1 | BZRK RS-T150 1" dome | DSP ch1 | front **L** | 1.5 k–25 k | ~13 kHz |
| 1 | tweeter 2 | same | DSP ch2 | front **R** | 1.5 k–25 k | ~13 kHz |
| 2 | tweeter 3 | same | DSP ch3 | rear **L** | 1.5 k–25 k | ~13 kHz |
| 3 | tweeter 4 | same | DSP ch4 | rear **R** | 1.5 k–25 k | ~13 kHz |
| 4 | midrange 1 | NVX XQS3v2 3.5" (2×) | DSP ch5 | front | 200–18 k | ~3.9 kHz |
| 5 | midrange 2 | same (rear pair) | DSP ch6 | rear | 200–18 k | ~3.9 kHz |
| 6 | midrange 3 | Deaf Bonce AP-M81SE 8" (2×) | ATOM pre-out 1 | front | 110–10 k | ~1.7 kHz |
| 7 | midbass 1 | Orion XTX654 6.5" (2×) | ATOM pre-out 2 | rear | 140–12 k | ~2.1 kHz |
| — | sub | Kicker (outside the DSP) | head unit out | — | — | — |

Paths 0–3 are stereo (one driver each). Paths 4–7 are **mono pairs** (two drivers in
parallel per path). Sensitivities: 3.5" 85 dB, tweeter 93, 8" 95.1, 6.5" 97, sub 83.5.
Max SPL: 3.5" 102 dB, tweeter 112, 8" 118, 6.5" 120, sub 114 — **the bass must sit 12–20 dB
below maximum to level with the midrange.**

**Live settings on the device (tune 2 loaded, 2026-10-21 07:31, 42/42 readback):**

| | paths 0–7 |
|---|---|
| output gains | −4.6 / −7.0 / −7.0 / −4.0 / −3.0 / −3.0 / −14.0 / −5.0 dB |
| delays | 5.4 / 3.8 / 6.0 / 4.1 / 4.6 / 5.0 / 3.8 / 4.4 ms |
| high-pass | 1500 / 1500 / 1500 / 1500 / 80 / 80 / 50 / 60 Hz, LR24 (Slope 3, filterType 1) |
| low-pass | **open at 20 kHz on every path** |
| master | −4.8 dB |
| memory | device memory 2 (index 1) = the `M1` chip on the page |

The delays are inherited from the previous tune; they are internally consistent with the
repo's speaker distances (ch3 ≈ 1.9 ms behind ch4, distances give 1.7 ms) but their
absolute offset is unverified.

---

## 4. Every file you need, and where it lives

Root: `C:\Users\sdotm\OneDrive\Documents\Default Project\ds18-bridge\`

### 4.1 Measurements — the only real dataset

| location | what it is |
|---|---|
| `measurements\run-20261020-1925\` | **canonical.** 8 files `tweeter-1..4.json`, `midrange-1..3.json`, `midbass-1.json` + `.csv` + `README.md`. Captured 2026-10-20 19:12–19:14. |
| `measurements\Car tune (2026-10-20)\` | the same captures written by `/api/run-save` at 20:01: `channel 1..8.json` + `.csv` + `README.md`. Same data, different names. |
| `rta-measurements.json` (root) | the live capture store — 21 recordings, newest 19:12. Includes the four quarantined 20:39 takes (wrong mic). |
| `evidence\run-20261020-1912-first-real-run.json` | the eight captures as the page saw them. |
| `evidence\rta-measurements-before-quarantine-20261020-204723.json` | pre-quarantine backup of the store. |

**Format** (per file): `channel`, `capturedAt`, `units: dBFS`, `grid: 1/24 octave, 241 points,
20 Hz–20 kHz`, `peak{dbfs,hz}`, `sig{ok,burstDb,from,to,reason}`, `points[[hz,dbfs] × 241]`
(1/24 since 2026-10-22 by the owner's standing directive; captures taken before that date
are 1/6 = 61 points and stay as captured - the exporters label each take by its own length).
Absolute dBFS at the mic, **not normalised** — comparable only with the same crossovers,
level and mic position.

**Conditions of that run (this is what makes prediction legal):** crossovers = the map in §3,
EQ **flat** on all 17 cells, DSP master −4.8, head unit 20/30, stim −6 dB, engine off, sub
amp disconnected, UMIK-1 with cal file.

Peaks: −46.0 / −49.4 / −44.4 / −43.2 (tweeters) · −36.2 / −42.8 / −25.5 (3.5" front, 3.5"
rear, 8") · −15.9 (6.5"). The 8" and 6.5" are 20–30 dB hotter than everything else — that
imbalance is the single biggest fact about this car.

**Two folder shapes, two different jobs.** The `/api/run-save` folder (`channel N.json`, field
`sig{ok,burstDb,…}`) is the human-readable record of a cycle. The `export-meas.ps1` folder
(`tweeter-1.json` … `midbass-1.json`, field `gate{ok,burstDb,fromHz,toHz}`) is the one
**`tune\fit-tune.py --meas` consumes**. `run-20261020-1925` is the export shape; `Car tune
(2026-10-20)` is the run-save shape; they are the same eight captures.

### 4.2 Tune files (`.idsp`)

`web\` — 8 files, 316–328 KB each, XML, 4 memory blocks × 17 outputs × 31 bands + 2 passbands.

| file | what it is |
|---|---|
| `1-car-tune-2026-10-20.idsp` | **ours, tune 1.** 57 bands, gains −6.8/−8.1/−9.0/−8.1/−8.3/−3.0/−30.0/−22.1. Predicted residual **3.6 dB** rms vs the house target. |
| `2-bor8f3-improved.idsp` | **ours, tune 2.** 41 bands, bor8f3's gains (above) + its curve smoothed to Q ≤ 2.8. Predicted residual **7.1 dB** — it is within 0–2 dB of the *un-tuned car* and 15–29 dB louder than tune 1 below 800 Hz. Treat as **stock + 1 dB**, not a voicing. |
| `3-rnb-dense-2026-10-21.idsp` | **made elsewhere, 2026-10-22 04:15.** 185 bands at Q 4.0, gains −7.3/−7.0/−7.2/−8.4/−8.8/−3.9/−31.2/−23.5, the same crossover map and delays. Predicted residual **1.5 dB** — the best in the library — and within **3.5 dB** of tune 1 below 800 Hz, so the two are comparable by ear. The only tune here with no Q ≥ 6 notches. |
| `bor8f3.idsp`, `xswmps.idsp`, `ai2.idsp`, `tinker3.idsp`, `cb0nyk.idsp`, `w6784b.idsp` | inherited. 32 blocks = **12 distinct** voicings (xswmps ≡ bor8f3; tinker3 ≡ ai2 in 3 of 4; 4 blocks empty). 85 % of their bands are Q ≥ 6 correction notches. Every one of them **closes the low passes between 1 000 and 4 200 Hz** — none was fitted for our open map. |

Tune names must contain **no spaces** (`ServeStatic` uses `AbsolutePath` verbatim). Each of our
files holds **exactly one `<memory>`** (index 0) — the owner's rule of 2026-10-22 retired the
older behaviour of repeating the same tune across all four blocks. The bridge's
`build_single()` strips three of them; consumers read `memories[0]`. *(This line said "all four"
until 2026-10-25, when `verify-all -All` caught the self-test still asserting the old rule.)*

### 4.2b Reviewing a tune somebody else made — the app can do this now

Two ways, both read-only:

**In the app (phone or desktop browser).** Tunes tab → *Choose .idsp file* (or pick a preset
from the dropdown) → **Review this tune**. The page sends the file to `POST /api/review`,
the bridge runs `tune\review-tune.py` on it, and the panel answers four questions in plain
words:

| question | the line you get |
|---|---|
| does it beat the car as measured? | *residual 1.5 dB vs 7.6 dB for the un-tuned car (6.1 dB better)* |
| can it be compared by ear with what is playing? | *within 3.5 dB across 50–800 Hz, so an A/B is fair* |
| was it fitted for this topology? | *this file closes its low passes; the car runs them open* |
| is it a voicing or a correction bank? | *246 of 246 bands are Q ≥ 6 — a measurement correction bank* |

plus the predicted system curve (this tune vs the car as measured) and the level delta.
A file the browser read is written to `var\review\` on the way in — never outside the
project tree — and reviewed there. Cost: ~4 s (first run builds the signature cache).

The comparison reference defaults to **the tune the car is playing** (the app's own label
in `tunename.json`, which is the loaded `.idsp` file name), so the level line always means
"against what you are hearing now". Pass `ref` to compare against something else.

**From a terminal**, anywhere on disk:
```powershell
python tune\review-tune.py "C:\path\to\someones-tune.idsp" --mem 1
python tune\review-tune.py "C:\path\to\someones-tune.idsp" --ref web\1-car-tune-2026-10-20.idsp --md review.md
```

### 4.3 Evidence and revert points

| file | use |
|---|---|
| `evidence\tune-before-20261021-073139.json` | exact state before tune 2 was written — `apply-tune.ps1 -Restore` this file |
| `evidence\tune-before-20261020-202655.json` | state before tune 1 — **also the base-gain reference for prediction** |
| `evidence\crossover-snapshot-mem2.json` | crossover baseline, 2026-10-21 07:25:53 (current map) |
| `evidence\crossover-snapshot-mem2-20261020-191029.json` | the *old* map (2500/250/110/140) — only if you want it back |
| `evidence\eq-snapshot-mem2-20261020-075420.json` | the tune removed from memory 2 that morning |
| `evidence\dspcfg-20261020-191911-after-target-crossovers.txt` | cmd 15 raw dump, 8 KB — the only full hardware config capture on disk |
| `evidence\backups\ds18-bridge-20261019-210348.zip` | whole-project backup |
| `doctor.json`, `verify.json`, `output-mirror.json` (root) | last readiness report, last web-gate report, the cmd 11 mirror |

### 4.4 Tools (all read-only unless stated; all work with no hardware except the marked ones)

| script | what it does |
|---|---|
| `tune\idsp.py` | `.idsp` parse/analyse/write. `dump` · `curve` · `bands` · `diff` · `json`. RBJ peaking response at 48 kHz. |
| `tune\fit-tune.py` | builds the tune plan from a measurement folder + the house target. Writes `tune\tune-plan.json`. |
| `tune\report-tune.py` | per-output structure: bands, Q, clamp counts, realised ripple, band averages. |
| `tune\build-swap-tunes.py` | builds `web\1-car-tune-2026-10-20.idsp` and `web\2-bor8f3-improved.idsp`. `--dry-run`, `--keep-notches`. |
| `tune\verify-swap-tunes.py` | 46 assertions that the swap files still match their sources. Exit 1 on drift. |
| `tune\verify-device.py` | **reads the live DSP** and asserts a file matches it band for band (42 assertions) + gains, delays, crossovers. Read-only. |
| `tune\system-predict.py` | **new.** Predicts the *system* response of a tune from the 19:12 measurement: per-path, energy sum, coherent envelope, band ownership, residual vs the house target. `--selftest` = 4 known-answer/negative controls. `--json` for machine use. |
| `tune\tune-atlas.py` | **new.** All 8 files, 32 blocks: distinct payloads, crossover maps assumed, Q/gain/Fc census, shape similarity, outliers. |
| `tune\review-tune.py` | **new.** Reviews **any** `.idsp` against this car: predicted system response, residual vs the house target, level-match vs a reference tune, topology check, correction-bank census, lineage. `--json` / `--md`. Backs the app's review panel. |
| `apply-tune.ps1` | **writes.** `-Plan tune\tune-plan.json` (default), `-Restore <snapshot>`, `-WhatIf`, `-Tolerance 0.11`. Snapshots the whole live state, writes EQ per path, writes the complete output block, reads everything back, refuses to report success unless every path verified. |
| `flatten-tune.ps1` | **writes.** Snapshots, zeroes every EQ gain, proves it by reading all 17 cells back. `-Restore`. |
| `set-crossovers.ps1` | **writes.** `plan` / `show` / `apply -Hp "0=1500,4=80"` / `restore`. |
| `doctor.ps1` | readiness model. `-SelfTest` = 4 cases. |
| `preflight.ps1` | after a replug: wait for the DSP, reopen, re-point the RTA at the UMIK-1, run doctor. |
| `after-run.ps1` | grades the newest captures and plots them. |
| `verify-web.ps1` | the web gate: parse + 141 ids + 14 baseline + negative control. |
| `verify-sweep-gate.ps1` | 11 checks on the analyser gate. |
| `where.ps1` | prints the laptop's current IP. |
| `measure-sweep.ps1`, `measure.ps1`, `export-meas.ps1` | single-shot measurement paths outside the page cycle. |
| `load-tune.ps1`, `load-tune-bank.ps1`, `load-tune-jump.ps1`, `make-flat-memory.ps1`, `propose-eq.ps1`, `rehearse.ps1`, `crossover-range.ps1`, `survey.ps1`, `fingerprint.ps1`, `compare-fingerprints.ps1`, `build.ps1`, `build-rta.ps1`, `build-sweep.ps1` | supporting tooling. |

### 4.5 Documents — read in this order

> **Superseded 2026-10-25.** This was one of four competing "read this first" lists in the
> project, and none of them agreed. **`START-HERE.md` is now the single orientation** — it
> carries the read order, the three rules that override everything, and a table saying where
> every file and tool lives. Read that instead; this list is kept only so an old link lands
> somewhere sensible.

For this brief specifically: `START-HERE.md` → `STATUS.md` → `TUNE-PLAYBOOK.md` → this file.

---

## 5. The house target (what "correct" means here)

From `tune\tune-plan.json` → `target` (the owner's R&B/hip-hop house curve, bass shelf,
gentle mid tilt, treble rolloff). Anchors, dB relative to 300 Hz:

```
20 Hz +6.0 | 35 +6.5 | 55 +6.0 | 80 +4.5 | 120 +3.0 | 180 +1.5 | 300 0 | 500 0
800 −0.3 | 1k −0.5 | 2k −1.5 | 3.5k −2.5 | 6k −3.5 | 10k −4.5 | 20k −5.5
```

The **sub** is not in the DSP, so the curve below ~50 Hz is not the DSP's job. `targets.json`
("Custom") is a *different* thing — a per-channel 31-band target template with its own
crossover map (TW 2000, 3.5" 250–2500, 8" 150–2000, 6.5" 120–1000) that does **not** match
the map in use.

---

## 6. What is proven, what is predicted, what is unknown

**Proven by measurement (the only real dataset):**
- The 8 per-channel responses, 1/6-octave, absolute dBFS, EQ flat, 19:12.
- The 8"/6.5" pairs are 20–30 dB hotter than the rest.
- The 3.5" pairs have a **1.5 kHz notch** that is identical on both pairs — a copied
  measurement artefact, deliberately **not** reproduced in either tune (it became a −5/−6 dB
  scoop instead).
- The presence region sits 9–15 dB below the house target.

**Proven by device readback (42/42):** tune 2 is on the device exactly as the file says —
EQ and crossovers are true device reads; the output block (cmd 11) is mirror-verified only
because that command has no readback on this hardware.

**Predicted only (no measurement exists):** everything in `DSB\PREDICTION.md`. Tune 1's
3.6 dB residual, tune 2's 7.1 dB, the 15–29 dB level gap, the +5…+7 dB excess at
1.25–2 kHz.

**Superseded 2026-10-25.** This list was one of five copies that had drifted apart.
Every unknown now lives in **`OPEN-QUESTIONS.md`** — one register, ranked by which
decision each question blocks. Do not add an unknown here; add it there and leave a
pointer in the document that cares.
---

## 7. The rules that make a tune legal

1. **Predict before you write.** `python tune\system-predict.py <a.idsp> [<b.idsp>]` — print
   the predicted residual next to the target. A tune that does not beat the flat baseline
   (7.6 dB on the current data) is not a tune.
2. **Level-match before comparing.** Two files more than ~6 dB apart in overall level cannot
   be A/B'd by ear. Say so in words before anyone swaps tunes at a traffic light.
3. **Match the topology.** Never transplant a voicing fitted with closed low passes into an
   open-map system, and never transplant a correction-notch bank (Q ≥ 6) into a musical role
   without saying so.
4. **Fit shape per path, set level by gain.** Fitting a 16 dB error with EQ eats every band.
5. **Anchor level on the MEDIAN of the home band**, not the mean.
6. **Cut freely (−12 dB), boost rarely.** A boost needs Q ≤ 1.4 and broad shape.
7. **No lift above 12 kHz on tweeters; none above 2 kHz on any cone.** The measurement up
   there is near the mic's own floor.
8. **Cap the total lift at +4 dB** per path.
9. **One common gain shift** — shift all channels together so the one that needs it least is
   unchanged; never boost a channel above where it already sits.
10. **Smooth to 1/3 octave before fitting.** 6–14 bands per path; 31 are available.
11. **Report the residual, do not hide it.** A cone keeps ~5 dB rms of error at the bottom of
    the bass shelf because it physically cannot produce it. That stays a residual, not a fake
    boost.
12. **Snapshot → write → read back → diff → verdict.** Never claim a write that was not
    read back. Every new comparator must have been **seen to fail**.

---

## 8. The work plan, in order

### Phase 0 — bring the rig back (needs the car powered)
```powershell
.\preflight.ps1                       # wait for DSP, reopen, re-point RTA at the UMIK-1, run doctor
Invoke-RestMethod http://127.0.0.1:8585/api/dspcfg | ConvertTo-Json -Depth 6 |
  Out-File evidence\dspcfg-<stamp>.txt      # the cmd 15 dump, with tune 2 on the device
```
Gate: `doctor.ps1` reports 0 failures and the mic row names **UMIK-1**. If the RTA ever falls
back to the laptop's Realtek array (−92 dBFS), that is the wrong-mic trap — stop.

### Phase 1 — settle the two open decisions (owner's call, your evidence)
- **Tune 2's status.** Rebase its gains onto tune 1's (keeping the smoothed shape) so it is a
  real alternative, or retire it and label it "stock as measured". `build-swap-tunes.py` can
  emit a third file; `system-predict.py` will score it.
- **The low passes.** Restoring real handovers (tweeters ≥ 1.5 k, 3.5" ~250–2 500, 8"
  ~110–2 500, 6.5" ~140–1 000 — the maps every inherited tune assumes) should remove most of
  tune 1's +5…+7 dB excess at 1.25–2 kHz. Then **re-fit**; the EQ fitted against the open
  map does not transfer.

### Phase 2 — the measurement that closes the loop
Parked, quiet, engine off, sub amp disconnected, head unit 20/30, master −4.8, stim −6 dB.
Phone → `play.html` for the stimulus, phone → `/` to run the cycle. The page enforces the
gates. Output lands in `measurements\Car tune (YYYY-MM-DD)\`.
Then: `.\after-run.ps1` and
`python tune\system-predict.py` — **predicted vs measured**. That comparison is the first
empirical test of every number in `PREDICTION.md`. Where one path owns a band, expect
agreement within ±3 dB; where the coherent/energy spread is 6–9 dB, expect disagreement and
do not treat it as a failed prediction.

### Phase 3 — re-fit against the new topology
```powershell
# 1. export the new captures into the folder shape the fitter consumes
.\export-meas.ps1 -Label run-<date> -Crossovers "<the map in force>" -Conditions "<what was true>"

# 2. fit -> plan (the plan is a DELTA; it works with the DSP unplugged)
python tune\fit-tune.py --meas "measurements\run-<date>" --out tune\tune-plan.json

# 3. structure of a candidate file, then its predicted system response
python tune\report-tune.py web\<candidate>.idsp
python tune\system-predict.py web\<candidate>.idsp
```
Then write and prove it:
```powershell
.\apply-tune.ps1 -Plan tune\tune-plan.json -WhatIf     # snapshot + report, change nothing
.\apply-tune.ps1 -Plan tune\tune-plan.json             # write, then read every path back
python tune\verify-device.py web\<candidate>.idsp      # 42 assertions against the live device
```
`apply-tune.ps1` snapshots the entire live state before the first byte and refuses to report
success unless every path verified. Keep the snapshot as the revert point.
`fit-tune.py` reads the live gains from `/api/info` when the device is up and falls back to
**deltas** when it is not — so a plan can be built at the desk, but it must be applied to the
device that will play it.

### Phase 4 — time alignment (needs the RCA cable)
Measure arrival per path, then set delays to the nearest 0.1 ms of the *measured* arrival
difference, not to a distance guess. Until then the existing delays stay — they are
internally consistent and changing them blind makes it worse.

### Phase 5 — the sub
Outside the DSP. Measure the sub chain separately (sub amp reconnected, DSP muted), then
work the head unit's own sub level/LP/KeyEQ. Do not pretend the DSP can fix it.

### Phase 6 — the desktop app
`APP-BRIEF.md` is the complete brief (472 lines). It now includes §6.6 "Prediction" and the
§6.2 level-match gate, both of which exist because of the findings in this document.

---

## 9. Definition of done for a tune

A tune is finished when **all** of these hold:

1. Its **predicted system residual** beats the flat baseline by ≥ 3 dB, and the number is
   printed next to the target.
2. It is **level-matched** (within ~6 dB overall) to whatever it will be compared with.
3. Its **crossover topology** matches the system it was fitted for.
4. Every write was **read back** and a **revert snapshot** exists.
5. **One measurement** confirms or refutes the prediction, in the bands where the energy
   ownership shows a single dominant path.
6. The **honest residual** is stated in the notes: what the drivers could not give, and why.

---

## 10. Gotchas (each of these has already cost real time)

1. **Memory chips are 1-based.** `M2` targets device memory 3. The car plays device memory 2
   = the `M1` chip. Read `#tuneTarget` before pressing apply.
2. **The EQ scope chip defaults to "This output only."** Click "All 17 outputs" or you
   half-apply a tune.
3. **`/api/status` does not carry the output block.** `outputGainDb` / `outputDelayMs` /
   `outputMute` / `outputRevert` live on **`/api/info`** only.
4. **`/api/eqread` does not echo `path`.** Pair reads by loop index; `[int]$null` is 0, which
   once made a comparator fail open.
5. **Command 11 has no readback.** Gains and delays are mirror-verified only. Say so every
   time.
6. **The vendor `Enable` bit is cosmetic.** "Open" means Fc 20 Hz / 20 kHz.
7. **Tune filenames must have no spaces** — `ServeStatic` uses `AbsolutePath` verbatim.
8. **The stimulus slider shipped at −24 dB.** It is −6 dB now; a quiet capture is a slider,
   not a broken DSP.
9. **The phone's Web-Audio sweep dies when the screen dims.** Use the served WAV
   (`web\play.html`), not the oscillator.
10. **A USB replug renumbers the capture devices.** `rta.exe` addresses by index; the mic
    gate reads `waveInGetID` on the opened handle plus the format (UMIK-1 is mono).
11. **A passing gate does not prove which mic answered.** Four takes from the laptop's own
    array all passed the gate; they were quarantined afterwards. Check identity *before* a
    run, never infer it after.
12. **Grading a run by "floor from the 12 quietest bins" is wrong** (it grades the mic's own
    digital silence) and **grading by "median outside the band" is wrong when the low passes
    are open** (the band is the driver's own output).
13. **PowerShell is case-insensitive**: `param([string]$Plan)` then `$plan = ...` coerces the
    parsed object back to a string, the loops run zero times, and the script prints success
    having written nothing. The plan variable is `$tune` now.
14. **Do not flatten the tune while driving** — it removes the tune from what is being
    listened to mid-session.
15. **`doctor -Restore` / `crossover-range restore`** put the *old* tune back. Only if that
    is what you want.
16. **C# 5 only, `csc.exe`, `.rsp` response files** (the project path has a space), and stop
    the running exe before rebuilding or the write fails on a file lock.
17. **Do not install `dotnet`**, a venv, or numpy to answer a question. `uvx` it, use it,
    evict it — and label the cost as transient or resident.
18. **The DSP drops off USB when the car loses power** (`device not enumerated (VID_1483&PID_1095)`),
    and the RTA then **silently** falls back to the laptop's array at −92 dBFS. The tune
    persists in flash; the *measurement* does not survive.
19. **Verification proves transcription, not tuning.** A file can match the device perfectly
    and still be the wrong tune. This is the lesson of `DSB\SELF-REVIEW.md`.
20. **Level-match before A/B.** Loudness beats preference.
21. **Topology is part of the fit.** A curve fitted with closed low passes is not portable.
22. **The 1.5 kHz notch was a copied artefact** — identical on both 3.5" pairs, so it was the
    measurement that was wrong, not the drivers. Do not reproduce it.

---

## 11. What not to do

- Do not write crossovers, master, input or volume from a tune path. Crossovers are cmd 13
  and the tune path does not touch them. Firmware (cmd 17) is permanently out of scope.
- Do not launch the vendor DSP apps (`EZY-DSP68.exe`, `EZY-DSP.exe`) while the bridge owns
  the USB device — they will contend for it. They are reference material for the app build.
- Do not install anything to answer a question. Do not scatter generated files outside this
  project tree.
- Do not let the sub amp be connected during a DSP measurement run.
- Do not "improve" a tune by ear without a measurement behind it.
- Do not claim a write, a measurement, or a prediction is proven unless the tool output says
  so.

---

## 12. If you are taking over from a model or a session

1. Read §1–§5 of this brief, then `STATE.md` and `TUNE-PLAYBOOK.md`.
2. Run, in order:
   ```powershell
   .\doctor.ps1 -SelfTest
   python tune\system-predict.py --selftest
   python tune\system-predict.py web\1-car-tune-2026-10-20.idsp web\2-bor8f3-improved.idsp
   python tune\tune-atlas.py
   ```
   All four must be clean before you change anything.
3. Confirm the rig's actual state before planning: `Invoke-RestMethod
   http://127.0.0.1:8585/api/info` and `.\where.ps1`.
4. Work from §8. Do not start at phase 2 (measuring) before phase 1 (decisions) — a
   measurement under the wrong topology is another dataset that has to be explained later.
