# STATE â€” where the rig is, and what to do next session

**Building or changing a tune? Read `TUNE-PLAYBOOK.md` first** â€” it is the complete procedure
(path map, limits, measurement validity, the fitting rules, how to write and verify a tune).
This file is only the rig's current condition.

Last updated 2026-10-21, after the prediction and atlas pass.

## The device right now

| | |
|---|---|
| memory | 2 (device index 1), the `M1` chip |
| EQ | **tune 2 loaded** â€” `web\2-bor8f3-improved.idsp`, 41 bands over 8 paths, verified 42/42 at 07:33 |
| crossovers | the owner's map: ch1â€“4 HP1500 Â· ch5â€“6 HP80 Â· ch7 HP50 Â· ch8 HP60, all LP open 20 kHz, LR24 |
| master | âˆ’4.8 dB |
| link | **off USB as of ~07:55** (`device not enumerated`, VID_1483&PID_1095); tune 2 persists in flash |
| the tune that was replaced | `evidence\tune-before-20261021-073139.json` |

Swap tunes with:

```
Tune tab -> M1 chip (= device memory 2) -> "All 17 outputs" -> load web\1-car-tune-2026-10-20.idsp
```

Take the EQ out of circuit (for a measurement run) with `flatten-tune.ps1` (it snapshots to
`evidence\` first, every time, then writes zero gain and proves the result by reading all 17 cells
back).

**Predict before swapping:** `python tune\system-predict.py <a.idsp> <b.idsp>` â€” on 2026-10-21 it
showed the two tunes differ by 15â€“29 dB below 800 Hz and that tune 2 is within ~1 dB of the un-tuned
car. Read `DSB\PREDICTION.md` before treating them as an A/B.

## Why the run button was greyed out

The page refuses a run while any path in the playing memory still has a non-zero EQ gain â€” a
response measured through the tune is a measurement of the tune. Memory 2 had up to **+15.0 dB**
on paths 1â€“3, so the check was correct, and the only way forward was to flatten it. It now reads
back **0 tuned, 17 flat**. Start run un-greys on the phone after **Re-read**, plus the tick in the
*sub amp disconnected* box (the app cannot see that amplifier â€” the subs are fed from the head
unit, not the DSP).

## The two failures from the car session, and what changed

**1. The analyser invented measurements.** `sweep-tool analyse` reported a 27.8 s delay from a take
with no sweep in it, and a 40.7 s delay from a take where the phone played 2.5 s and stopped. It
now applies two tests, the same ones the burst mode already used:

- *presence* â€” normalised correlation `coef â‰¥ 0.080` (noise lands near 5/âˆšN; measured noise takes
  scored 0.036 and 0.038)
- *completeness* â€” the reference is band-limited to its own band, and the mic must hold up its end
  chunk by chunk: a chunk 20 dB below the driver's strongest is a hole, meaning playback stopped

The band comes from **the reference**, never from the measurement. Taking it from the measurement
made the test agree with itself: a 2.5 s take produces a "passband" of 20â€“113 Hz that the truncated
part satisfies on its own. That version passed the bad take; this one refuses it.

`verify-sweep-gate.ps1` proves all of it â€” 11 checks, real arrivals at 500.000 ms accepted, noise,
truncation and three field takes refused. Run it after any change to the analyse path.

**2. The phone's sweep stopped after ~2.5 s.** The page generated it with Web Audio, which dies
when the phone dims or the tab loses focus. The stimulus is now a plain WAV served by the bridge
and played natively:

**http://LAPTOP-C7NA3SNN:8585/play.html**

Full range / tweeter / midrange / midbass / sub sweeps, plus 60 s pink noise, and it keeps playing
with the screen locked. The bridge now serves `.wav` as `audio/wav`; it was `application/octet-stream`,
which a phone will not reliably play.

## Next session, in order

1. `powershell -File where.ps1` â€” the address changes with the network (it was 10.66.161.189 in the
   car, 192.168.7.253 an hour later). The machine name does not: use `http://LAPTOP-C7NA3SNN:8585`.
2. Phone â†’ `/play.html`, pick the band for the driver under test, leave it on that page.
3. Phone â†’ `/` (the DSP page): speakers set, stimulus chosen, then **Verify setup**. Every row must
   be âœ“ except the sub-amp tick, which is yours to make.
4. On the laptop, record while the phone plays:
   `.\measure-sweep.ps1 -Name <channel> -Sweep .\sweeps\sweep-<band>-10s.wav -NoPlay -NoLoopback -Seconds 40`
   The reference **must** be the band-matched sweep, or the take is refused as a hole â€” that is the
   test working, not a bug.
5. Read the verdict line, not the number: `delay : NOT MEASURED` means the take is unusable, and
   the reason is printed.

## Still open

- **The run already starts from the phone** â€” `runCycle` lives in the page and works from any
  browser that can reach 8585 and 8586. Nothing needs rebuilding for that, and the bridge has never
  had a `/api/run`.
- **What is fragile:** that run plays its stimulus with Web Audio (`stimStart`), so it dies when the
  phone dims â€” the same failure that killed the 2026-10-20 takes. `play.html` is a native-file
  workaround, but it is a *separate* source, so it does not feed the page's run cycle. Pointing the
  run at the served WAV instead of the oscillator is a page edit, **no rebuild**, and it is the next
  real piece of work â€” do it with the rig attached so the run's "did the sweep arrive" check can be
  watched as it changes.
- The bridge-side coordinator (`/api/stim`, `/api/run`, `/api/run/status`) in yesterday's notes is a
  *resilience* item, not a missing feature: it would stop a hidden tab's throttled timers from
  stalling a cycle. Only worth building if the phone-first version proves unreliable when tested.
- Disk: 1.25 GB free. A car session writes ~1 MB per 10 s take plus snapshots; clear space before a
  long run.
- The sub is fed from the head unit and has no DSP path at all. Nothing in the app can mute, delay
  or level it â€” hence the manual tick box.

## 2026-10-20 evening â€” the reference must match what was actually played

Six takes were refused in a row and the failure looked like a dead microphone. It was not. The
phone played the **full-range** sweep every time while the recordings were analysed against the
**110 Hz â€“ 12 kHz** reference, and two sweeps of different bands cannot correlate: the same
frequency sits at a different time in each. Coefficients came back 0.03â€“0.05 and the gate refused
correctly.

Proof, one recording (`measurements/sweep-20261020-180254-midbass_take1.wav`) analysed against all
five references:

    full range  20-20k   coef 0.296   coverage 88%   measured
    tweeter   1.5k-20k   coef 0.009   refused
    midbass    110-12k   coef 0.050   refused
    midrange   200-18k   coef 0.061   refused
    sub         25-200   coef 0.049   refused

Lessons, in order of how much time they cost:

- **Whatever band is played is the band to analyse against.** When a take is refused, try all five
  references before touching hardware; the highest coefficient names the band that played.
- The mic was cleared in three steps, each needing a known source: hand taps (structure-borne, so
  they prove nothing about air), a 1 kHz tone from the laptop speakers (-57 dBFS), white noise
  measured band by band (flat to 12 kHz, -56 dBFS at 12 k), and finally the owner's voice at 20 cm
  (-29 dBFS mean, -8.9 peak). Only the voice proves the capsule works in the room being measured.
- The laptop's own Realtek array reads -100 dBFS above 300 Hz â€” it is digitally deaf (hardware mute
  or shutter) and must never be used as a reference microphone.
- `var\probe-umik.ps1` (endpoint volume/mute by WASAPI), `var\probe-two-mics.ps1`,
  `var\probe-laptop-tone.ps1` and `var\probe-mic-response.ps1` are the medical kit for a mic
  dispute; they each answer one question with a number.
- `sweep-tool analyse` used to report `coef 1.7e12` when a lag left no usable window (energy
  collapse divided by the 1e-12 guard). Now guarded: zero energy reports `coef 0`, and a true
  match is clamped at 1.0.

## 2026-10-20 late â€” the RTA device index goes stale on every replug

The full eight-channel run passed after this was fixed. Before it, a run produced all red crosses
while the UMIK was demonstrably healthy. Cause: **`rta.exe` keeps a device *index*, and a USB
replug reorders the list.** `/rta.json` still reported the UMIK name while the capture stream had
moved to `Microphone Array (Realtek(R) Audio)`, which reads **-93 dBFS forever** and still reports
`status: capturing`. The page reads its levels from that RTA, so every channel measured silence.

    GET  http://127.0.0.1:8586/devices        -> names in current index order
    GET  http://127.0.0.1:8586/device?n=<i>   -> re-point (then re-read /rta.json)

Rules that follow:

- **After any USB replug, re-point the RTA before trusting a single measurement.** `doctor.ps1`
  checks this (`mic rta is on the UMIK-1`) and is the cheapest guard; run it after every replug.
- Trust `/rta.json` `device` **and** `levelDb`. A plausible-looking device name with a -93 dBFS
  floor is the Realtek array on stale index, not a quiet room.
- The laptop array must never be used as the measurement mic. It is digitally deaf (hardware
  mute/shutter): -100 dBFS above 300 Hz in a room with audible sound.
- The 60 s no-loopback capture (`measure-sweep.ps1 -NoPlay -NoLoopback -Seconds 60`) with the
  full-range reference is the fastest end-to-end proof that the whole chain works: it returned
  `coef 0.189` plus a 21-point curve at 18:14 when everything was healthy.

Result: full per-channel run, **all eight channels green**, DSP verified unchanged afterwards
(crossovers match snapshot, EQ flat, nothing left muted). Per-channel results live only in the
phone page's browser storage - they are not written to disk, so a cache clear loses them.

## 2026-10-20 19:12 - the first trustworthy run, and where the level had gone

Level was never the DSP. Master sat at -4.8 dB (nearly wide open) and the head unit was at
20 of 30, but the page played its stimulus through a slider that **shipped at -24 dB**
(`rtaStimGain` in `web/index.html`). 24 dB thrown away before the head unit, which is almost
exactly the deficit in the earlier captures. Default is now **-6 dB**. Peaks went from a
-45 dBFS ceiling to -16..-46 dBFS, burst 70-80 dB, and 8/8 captures accepted by the gate.

The grading tool was wrong twice before it was right, and both mistakes are the same shape:

- floor taken from the 12 quietest bins -> for a midrange that is the mic digital silence near
  20 kHz, so a tweeter reported "50 dB headroom, usable 20-160 Hz". That 20-160 Hz hump is
  shared by every channel, including tweeters crossed at 2.5 k: it is the room, not the driver.
- floor taken from the median OUTSIDE the rated band -> fine only while the crossovers
  band-limit the driver. With the low-pass filters opened to 20 kHz the 6.5" plays 60-400 Hz,
  so "outside its band" is its own output, and it was scored "buried in the room" at -31 dBFS
  with a 4 dB margin. It was not buried; the yardstick was.

`after-run.ps1` now reports peak + `sig.burstDb` + `sig.ok` + `sig.from/to`, all of which the
capture carries itself and none of which assume a passband.

Run evidence: `evidence\run-20261020-1912-first-real-run.json` (the eight captures),
`var\plots\run-curves.png` (plot; its ceiling was clipping the two loud drivers flat at -30,
now -10).

### Crossovers are open on purpose now

The owner asked for a per-driver map, written with the new `set-crossovers.ps1` (per-path HP,
LP open, snapshot first, every path verified by readback of scalars **and** 40 coefficients):

    path 0-3  tweeters         HP 1500  LP open   LR24
    path 4-5  3.5" pairs       HP   80  LP open   LR24
    path 6    8" pair          HP   50  LP open   LR24
    path 7    6.5" pair        HP   60  LP open   LR24

Way back (the previous tune): `evidence\crossover-snapshot-mem2-20261020-191029.json`,
restore with `.\set-crossovers.ps1 restore -Snapshot <that file>`.

**`doctor.ps1` now reports `FAIL crossover OPEN on path 0`. That is this state, not drift.**
Do not run `doctor -Restore` or `crossover-range.ps1 restore` unless the old tune is wanted.

Consequence measured, not assumed: with the low-pass open on paths 4-7, every cone plays to
20 kHz, so 2 k-20 k is a sum of eight drivers and nothing measured there belongs to one
channel. EQ fitted against those bands would fit something that does not exist on its own.
Open decision: (A) keep the open map and level-match the eight, or (B) restore real handovers
and tune from there.

### Tools added this evening

- `preflight.ps1` - after a replug: waits for the DSP, reopens it, re-points the RTA at the
  UMIK-1, then runs doctor. Exit 0 means ready. (Two things break on every replug and neither
  announces itself.)
- `after-run.ps1` - grades the newest captures and plots them. No arguments.
- `set-crossovers.ps1` - `plan` / `show` / `apply -Hp "0=1500,4=80"` / `restore`.

Both the bridge and rta.exe were left running. DSP state at unplug: memory 2, master -4.8 dB,
unmuted, High-level, EQ flat on all 17 cells.

---

## 2026-10-20 late - first tune from the real run, and per-cycle saving

### The tune (tune\fit-tune.py -> tune\tune-plan.json)

Built from `measurements\run-20261020-1925` (the eight per-channel files), against the owner's
own R&B/hip-hop house curve (the one in the repo's `make_tune_rnb.py` - bass shelf, gentle mid
tilt, treble rolloff; that curve exists because a neutral tune sounded "vocals good, bright").

What it does, in order of audible size:

* **Level.** The 8" (path 6) and 6.5" (path 7) are 20-30 dB hotter than everything else - the
  ATOM-400.4 gain is cranked, exactly the failure EQUIPMENT.md warns about. The plan cuts path 6
  by -15.8 dB and path 7 by -17.9 dB at the output stage. That is the single biggest change.
* **Shape.** 4-11 peaking filters per channel, cuts to -12 dB, lifts capped at +3 dB and only
  broad (Q <= 1.4), total cumulative lift capped at +4 dB.
* **No lift above 12 kHz** on tweeters (the measurement there is near the mic floor) and **no lift
  above 2 kHz on the cones** (a cone 12 dB down pushed back up is a second source fighting the
  tweeters, not more treble).
* Fits land at 0.9-1.9 dB rms on the tweeters, 5-6 dB on the cones - the cone residual is the low
  end the target's shelf asks for and the drivers cannot give, so it stays a residual rather than
  a 6 dB boost.

**Time alignment is deliberately NOT touched.** These captures are magnitude only, 1/6-oct, no
phase and no impulse - arrival time is not recoverable from them. The delays on the device
(5.4/3.8/6.0/4.1/4.6/5.0/3.8/4.4 ms) were checked against the distances in the repo: the
*differences* between channels match those measurements (ch3 is ~1.9 ms behind ch4; the repo's
distances give 1.7 ms), so the set is internally consistent and only its absolute offset differs
(equivalent to a sub reference nearer 215 cm than 250 cm). Changing it blind would make it worse.

### Writing it

`apply-tune.ps1` - snapshots the whole live state to `evidence\tune-before-<stamp>.json` first,
writes EQ per path then the complete output block, reads every band and every gain back, and
fails loudly listing anything that did not land. `-WhatIf` reports without writing, `-Restore
<snapshot>` puts it back. Tested against an unplugged DSP: refuses before touching anything.

### Per-cycle saving

New bridge route `POST /api/run-save {count,label}` writes
`measurements\Car tune (YYYY-MM-DD)\channel N.json` + `.csv` + `README.md` (peak, gate, burst, and
the grid/units stated in the file so it cannot be misread later as a normalised curve). The page
calls it from `runFinish()` via `runSaveToDisk()`. A same-day re-run gets " (2)", never an
overwrite. Verified on the existing run: all eight channels, names and peaks correct.

`fit-tune.py` note: it is pure stdlib. numpy is NOT installed on this machine's Python 3.15 and a
stack was not installed for this. Two self-inflicted bugs worth remembering: the residual check
summed per-filter responses with the wrong index order and reported the fit as making things
worse, and an early "refinement" solved each gain against the filter's 1 dB response - a peaking
filter's shape is not linear in gain, so it optimised a bell it was not applying (3.5 -> 6.5 dB
rms). Both are commented in the file.

### Applied - 2026-10-20 20:26 (memory 2)

`apply-tune.ps1` wrote the plan and read every band back. Verified independently of the script's
own report: **57 bands across 8 paths, all present**, gains
`-6.8 / -8.1 / -9.0 / -8.1 / -8.3 / -3.0 / -30.0 / -22.1`, delays untouched
`5.4/3.8/6.0/4.1/4.6/5.0/3.8/4.4`, master -4.8 dB, unmuted, memory 2.
Way back: `evidence\tune-before-20261020-202655.json` -> `.\apply-tune.ps1 -Restore <file>`.

Two bugs found by the first real write, both worth remembering:

1. **`/api/status` does not carry the output block.** `outputGainDb`, `outputDelayMs`,
   `outputMute`, `outputRevert` live on **`/api/info`** only. Status is the light endpoint and
   returns none of them. Anything that needs to read or rewrite cmd 11 must use `/api/info`.

2. **PowerShell case-insensitivity produced a false success.** `param([string]$Plan ...)` followed
   by `$plan = ... | ConvertFrom-Json` assigns to the *parameter* and the [string] constraint
   coerces the parsed object back into a string. `$plan.paths` was `$null`, both write and verify
   loops ran zero times, and the script printed "TUNE APPLIED" having written nothing. The plan
   object is now `$tune`, and the script refuses to report success unless
   `$verifiedPaths -eq $pathProps.Count`.

Also fixed in the fitter: the level anchor used the **mean** of the home band, which let the 6.5"'s
+10 dB 100-125 Hz resonance drag the whole channel down ~3 dB. It uses the **median** now.

Expected audible result: paths 6 and 7 are 16 and 17 dB quieter than before. That is the measurement
(tweeter peak -49 dBFS vs 8" peak -18 dBFS at the same stimulus), not a mistake - but the sub is not
on a DSP path, so if the low end now feels light the fix is head-unit sub level or the ATOM gain
physically, not this tune.

### Quarantined - 2026-10-20 20:47

Four captures at 20:39 were deleted from `rta-measurements.json` (62 -> 58). They were taken
while rta.exe was pointed at `Microphone Array (Realtek(R) Audio)`; the UMIK-1 was not on USB at
all, so those curves are the room through the laptop's own mic, not the drivers. Evidence: all
four read within a few dB of each other where the 19:12 set spreads 30 dB, and all four sit at
-119.6 dBFS above 9 kHz.

Untouched backup: `evidence\rta-measurements-before-quarantine-20261020-204723.json`.
The 19:12 run is verified intact - all eight entries, peaks unchanged
(-46.0 / -49.4 / -44.4 / -43.2 / -36.2 / -42.8 / -25.5 / -15.9).

**The gate cannot catch this.** `sig.ok` was true for all four, because it checks whether a burst
arrived at a sane level, not which microphone arrived. Device identity has to be checked before a
run (that is what `preflight.ps1` step 3 is for), never inferred from a passing gate afterwards.
Superseded 2026-10-21: the run box now refuses to start on the wrong input - see the entry below.

## 2026-10-21 - the wrong input can no longer start a run

The 2026-10-20 quarantine above ended with "the gate cannot catch this". It now can, before the run
instead of after it, because the run box is a gate and the mic row is a **fail** rather than a
warning. `RUN.ready` counts `fail` rows only, so the old `warn` was decorative - an eight-channel
cycle recorded the laptop's own array with every row green-ish.

Three facts now decide the row, so no single lie carries it:

| fact | source | proves |
|---|---|---|
| `device` | winmm asked about the **opened handle** (`waveInGetID`) | which endpoint the stream is on |
| `ch` | the format that opened | UMIK-1 is mono, the laptop array is stereo |
| `devDrift` | device list re-read now vs at open | a replug renumbered the list under us |

Fails on: no RTA data, not `capturing`, laptop-ish name (Microphone Array / Realtek / Stereo Mix /
internal / hdmi / webcam), a `Line In`, the anonymous Windows default, or a device whose index was
renumbered. Warns only on a mono non-UMIK mic and on a too-hot UMIK.

**The real bug was upstream, in `rta.exe`.** Opening `0xFFFFFFFF` (WAVE_MAPPER) lets winmm pick an
endpoint and then refuse to name it: `waveInGetID` returns `0xFFFFFFFF`, which winmm calls
"Microsoft Sound Mapper". Measured, not assumed - `var\probe-waveinid.cs` opens the mapper exactly as
the daemon does and prints the MMRESULT; it returns 0 (success) with id 0xFFFFFFFF. So the daemon now
resolves `UMIK`/`miniDSP`/`measurement` **by name** at startup and opens that index explicitly, and
`/rta.json` gained `ch`, `devOpenIdx`, `devDrift`.

Verified on the live page (browser, Tune tab, real daemon): with no UMIK on USB the row reads
"rta.exe is on the Windows default input, which winmm will not identify ..." and **Start run is
disabled**; pointing the daemon at device 0 makes it read "Microphone Array (Realtek(R) Au is the
laptop's own input" at -99.6 dBFS, also disabled. Eleven fixtures across the branch set pass against
the function text as served (UMIK ok / hot / drifted / open-failed, mapper, stereo mix, line in,
generic mono and stereo).

Open, because it needs hardware: the **UMIK-present** path end to end (name match -> explicit index)
has not been exercised - the mic is not on USB. When it is, `/rta.json` must show
`device: Microphone (UMIK-1)`, `ch: 1`, `devOpenIdx` a number, `devDrift: ""`.

## 2026-10-21 - the tune library now holds tune 1 and tune 2

The Tune tab lists every `.idsp` in `web\`, so two files were built rather than hand-edited:

- **`1-car-tune-2026-10-20.idsp`** - the measured tune. Transcribed band-for-band from
  `tune\tune-plan.json`, at the gains/delays the readback confirmed
  (`-6.8 -8.1 -9.0 -8.1 -8.3 -3.0 -30.0 -22.1`, delays `5.4 3.8 6.0 4.1 4.6 5.0 3.8 4.4`),
  master -4.8.
- **`2-bor8f3-improved.idsp`** - the B tune's voicing, rebuilt. Its 27-31 bands per output at
  Q 9.9-16.1 (1/10-1/16 octave, narrower than the band spacing, so they cannot sum into a
  curve) were refitted as 2-8 bands per output at Q 2.8 (about 1/3 octave). Every 1/1-octave
  band lands within 1.2 dB of the original; the one deliberate change is the -11 dB notch at
  1.5 kHz turning into a -5/-6 dB scoop. Numbers in `DSB\B-TUNE-REVIEW.md`.

Both files repeat the same tune in all four memory blocks. That is not laziness: the Tune tab
writes to the **file's** memory index, so a one-block file writes flat bands if M2 is selected.

Verified two ways, not by inspection: `tune\verify-swap-tunes.py` (46 assertions, 0 failures -
bands match the plan exactly, levels and delays match, crossovers match the car), and the app's
own parser in a real browser tab, where the dropdown reports "parsed, 4 memories" for both.

New tooling (`tune\`): `idsp.py` reads/analyses/writes `.idsp` with no hardware and computes the
response the bands actually produce; `build-swap-tunes.py` builds both files; `report-tune.py`
prints the structural numbers; `verify-swap-tunes.py` gates edits.

Lessons:

- **A tune filename with a space does not load.** `ServeStatic` takes
  `ctx.Request.Url.AbsolutePath` verbatim - still escaped, and lowercased - so `my tune.idsp`
  arrives as `my%20tune.idsp` and 404s. The whole library is space-free for this reason. Hence
  the hyphenated names; see TUNE-PLAYBOOK gotcha 16.
- **`bor8f3.idsp` is the pre-change map.** Its crossovers (2500 / 250-2500 / 110-2500 /
  140-1000, all LR24) match `evidence\crossover-snapshot-mem2-20261020-191029.json` exactly, so
  it is what the car was playing before 19:10 on 2026-10-20. Both new files carry the owner's
  current map (1500/80/50/60, low-passes open) instead, because the Tune tab never writes
  crossovers and a file that disagreed with the car would be lying about itself.
- **tune 2's bass is not a defect.** It keeps bor8f3's levels on purpose, so path 6 (8") sits
  16 dB and path 7 (6.5") 17 dB above what the measurement asked for. That is the B tune's
  balance; if it is too much on the ride, change those two gain lines and nothing else.
- **A peaking filter at Q 2 is one octave wide** (bandwidth = f0/Q). Q 2.8 is 1/2.8 octave -
  still broad. Only the source's Q 10-16 is narrow enough to comb.

## 2026-10-21 - in the car, mic connected: the mic path verified, and a doctor bug found

The laptop went into the car and the UMIK-1 was plugged in. What that finally allowed:

**The UMIK-present path is verified end to end** - the open item from this morning. `/rta.json` now
reads `device: Microphone (UMIK-1)`, `devOpenIdx: 0`, `devDrift: ""`, `status: capturing`,
`cal.loaded: true` (serial 7170061, 615 points, 10.1-20016.8 Hz). The name-match -> explicit-index
branch ran for the first time and opened index 0 by name. Note it enumerates as **2 channels**, not
mono: the gate's mono rule only applies to non-UMIK devices, so a UMIK reporting 2 ch still reads
`ok`. On the live page the row is green: "Microphone (UMIK-1) - 48000 Hz - -53.9 dBFS - 2 ch".

**The device holds tune 1 exactly.** `tune\verify-device.py` reads memory 2 back and compares it to
`web\1-car-tune-2026-10-20.idsp`: every band's fc/gain/Q matches on all 8 paths, gains and delays
match, and the crossovers read 1500/1500/1500/1500/80/80/50/60 with all low-passes at 20 kHz. So the
file built from `tune-plan.json` this morning is not just a transcription - it is the hardware.

**`doctor.ps1`'s crossover check was only ever testing path 0 - and failing open.** `/api/eqread`
does not echo the path it read, so every response carried `$null` for `path`, `[int]$null` is `0`,
and `Get-OpenPaths` matched only path 0; every other snapshot row found no read and hit `continue`.
Seven open crossovers would have reported "closed". This is the bug behind what the playbook called
"the known false alarm": the FAIL was real in the sense that path 0 genuinely differed, but the
comparison was against `evidence\crossover-snapshot-mem2.json` from **07:21**, which still held the
pre-1500 map. Fixed: each live read is stamped with its path, and an unread path now counts as open
instead of passing. `doctor.ps1 -SelfTest` still catches one changed path and a changed high edge.

The stale baseline was preserved as `evidence\crossover-snapshot-mem2-20261020-072143.json` and a
fresh one taken from the live device (`crossover-range.ps1 snapshot`, 2026-10-21T07:25:53), so the
check now compares the car to the map the owner actually asked for. doctor: **READY WITH WARNINGS**,
0 fail, 1 warn (disk).

**Two gates remain before a run, and both are deliberate:**
- `channel plan` - the roster is `localStorage` (`ds18.roster`) on whichever browser drives the cycle,
  so it has to be set on the phone: ch1-4 Tweeter, ch5-7 Midrange, ch8 Midbass.
- `no tune in the way` - memory 2 must be flat for the measurement. `flatten-tune.ps1 -WhatIf`
  confirms it would snapshot and zero exactly 8 tuned paths / 17 cells, and `-Restore` puts it back.
  Not done while the car is moving: flattening removes the tune from what is being listened to.

## 2026-10-21 07:33 - tune 2 loaded, the car is playing the B voicing now

`web\2-bor8f3-improved.idsp` was written to **device memory 2 (index 1)** through the Tune tab with
the output box ticked, so the car now carries tune 2's EQ *and* its levels. Readback against the file:
42 assertions, 0 failures - all 24 bands and both crossover edges read straight off the device.

What changed on the eight live paths (tune 1 -> tune 2): tweeters +1.1 to +4.1 dB, front 3.5" +5.3 dB,
rear 3.5" unchanged, **front 8" +16.0 dB, rear 6.5" +17.1 dB**. Delays identical (3.8-6.0 ms), so this
is the same presentation and a different balance, which is what tune 2 was built to be.

Revert points, in order of preference:
- load `web\1-car-tune-2026-10-20.idsp` in the Tune tab (M1 chip = device memory 2, "All 17 outputs")
- `.\apply-tune.ps1 -Restore evidence\tune-before-20261021-073139.json` (the exact pre-write state)

Two things that cost time and are easy to repeat:
- **The memory chips are 1-based.** `M2` targets device memory 3, not the memory being listened to.
  The car plays device memory 2, which is the `M1` chip. `#tuneTarget` names the target - read it
  before pressing apply, because writing the wrong memory looks exactly like a tune that did nothing.
- **The EQ scope chip defaults to "This output only."** Left alone, apply writes one path's EQ and
  then the complete output block - a half-applied tune. Click "All 17 outputs."

The output block is written "not acked": cmd 11 has no readback on this hardware, so gains and delays
are proven against the bridge's mirror of what it sent, not a device echo. The EQ and crossovers are
genuine device reads.

Also changed: outputs 9-17 (unused) go from 0 dB to -3 dB, which is how the bor8f3 file carries them.
Nothing is wired to those, so it is inaudible, but it is why the readback expects -3 there.

Owner has no 3.5 mm -> RCA cable yet; time alignment stays unimproved until he does.
## 2026-10-21 09:5x - the prediction that should have run before the swap

With the car powered off and the DSP off USB (see below), the two analyses that need no hardware
were finally run. Both are new tools; both are pure file arithmetic.

**`tune/system-predict.py`** rebuilds the system response from the 19:12 measurement, which was
taken EQ-flat with known path gains (`evidence\tune-before-20261020-202655.json`). Result against
the house target, rms 60 Hz-10 kHz:

| curve | residual |
|---|---|
| the car as measured, EQ flat | 7.6 dB |
| tune 1 (`1-car-tune-2026-10-20`) | **3.6 dB** |
| tune 2 (`2-bor8f3-improved`) | **7.1 dB** |

Tune 2 is within 0-2 dB of the un-tuned car at nearly every frequency (worst 5.6 dB at 200 Hz)
because it keeps bor8f3's path gains and those were the gains already live during the measurement.
So the 07:33 swap was not a change of voicing: it put the car back to stock balance with ~1 dB of
broad shaping. It is also **15-29 dB louder than tune 1 below 800 Hz**, so no by-ear A/B
between the two could have been a judgement about voicing.

**`tune/tune-atlas.py`** reads all 8 files / 32 memory blocks: 12 distinct tunes (xswmps = bor8f3,
tinker3 = ai2 in 3 of 4 memories, 4 blocks empty), 4 144 active bands of which 85 % are Q>=6 and
2 292 are Q 10-16 (correction notches, not tone controls), and - the one that matters - **every
inherited voicing closes its low passes between 1 000 and 4 200 Hz while our map has them open at
20 kHz.** Tune 2's curve was fitted in a topology this car does not have.

Consequence for the open-vs-closed crossover decision: with the cones open, tune 1 overshoots the
house target by +5 to +7 dB at 1.25-2 kHz, the handover band. Restoring real low passes is now the
evidence-backed option rather than a matter of taste; it still needs the owner's decision and a
re-measure.

Reports: `DSB/PREDICTION.md`, `DSB/ATLAS.md`, and the graded review in `DSB/SELF-REVIEW.md`.
`python tune/system-predict.py --selftest` is 4/4 (identity, negative control, level, LR24 maths);
the LR24 term was 3 dB wrong until that test caught it.

**Rig state at the time of writing:** the DSP is off USB (`/api/connect` -> "device not enumerated
(VID_1483&PID_1095)"), the UMIK-1 is gone with it, and the RTA has fallen back to the laptop's
Realtek array at -92 dBFS - the exact trap the mic gate was built for. Tune 2 persists in the
amplifier's memory across the power loss. When the car is powered: `.\preflight.ps1` first, then the
`/api/dspcfg` dump that has still never been taken.

## 2026-10-22 - the app can now review a tune against this car

The question was "somebody made me a tune - can I go over it in the app?" The answer was
"not yet": the Tunes tab could import any `.idsp` and show its shape, but every judgement
it made was about the FILE, never about the CAR. That is the gap the 2026-10-21 review
found, so it is now closed.

**New: `tune\review-tune.py`** - reviews any `.idsp` against this vehicle, no device
needed. It reuses `system-predict.py` and `tune-atlas.py` by import, so the app, the
reports and the terminal all quote the same numbers. It answers four questions in plain
words: does it beat the car as measured, is it level-matched enough to A/B by ear, was it
fitted for this crossover topology, and is it a voicing or a measurement-correction bank.
Plus a lineage check (is this a copy of something already in the library).

**New: `POST /api/review` on the bridge** - runs that tool and returns its JSON unchanged.
The bridge does no maths of its own, so there is no second truth.

**New in the page:** the Tunes tab grew a "Review it in this car" card - one tap, the
verdict lines, the numbers, and the predicted system curve against the car as measured.
A file the browser read is written to `var\review\` first; nothing leaves the project tree.
`verify-web.ps1` passes (152 ids, baseline 14/14, negative control ok).

**The library grew a ninth file.** `web\3-rnb-dense-2026-10-21.idsp`, made elsewhere and
dropped in at 04:15 today. Scored:

| tune | predicted residual vs the house target | level vs tune 1 below 800 Hz |
|---|---|---|
| the car as measured, EQ flat | 7.6 dB | - |
| `1-car-tune-2026-10-20` | 3.6 dB | - |
| `2-bor8f3-improved` | 7.1 dB | +29.1 dB (not comparable by ear) |
| **`3-rnb-dense-2026-10-21`** | **1.5 dB** | **-3.5 dB (fair A/B)** |

185 bands at Q 4.0, the same crossover map and the same delays as ours, no Q>=6 notches,
and a positive bass tilt (+3.5 dB) - it was written as a voicing, not as a correction
bank. On the numbers it is the best tune in the library and the only one that is both
level-matched to tune 1 and better than the un-tuned car. It is still prediction: it needs
the parked measurement before anyone trusts it with the car.

Rig note: the DSP is still off USB (car powered down), the RTA is not running, and the
bridge was restarted today with the new route. Tune 2 remains in the amplifier's memory.

## 2026-10-22 12:05 - in-car session, tune 3 loaded, now building the tune-2-level replacement

**What was loaded.** Tune 3 (3-rnb-dense-2026-10-21) went in via the bank loader onto all
four memories, jump to the playing slot (device memory 2), runs verified: slot 1-3 clean at
248/248 bands + 8/8 crossover paths; slot 4's bank-loader JSON never closed (loader readback
outlived the owner's USB unplug - reads only, the playing-memory proof below stands on its own).
Independent proof: 	une\verify-device.py web\3-rnb-dense-2026-10-21.idsp --mem 1 -> 0 failures.
Revert point for tune 2: evidence\bank-before-2026-10-22-1011.json.

**Outcome by ear.** The owner heard it as "entirely too low", which the review card had
computed in advance: -31.5 dB vs tune 2 below 800 Hz (tune 2's woofer gains are 17-18.5 dB
hotter, and it keeps the stock +13.7 dB hump at 100 Hz). Master is -4.8 dB in every file;
the difference is per-channel gains, so master cannot level-match anything.

**The owner's decision.** Tune 2 is the daily driver. Library policy going forward: every
designed tune must be LEVEL-MATCHED TO TUNE 2, i.e. its loudness platform - gains equal to
the sweep-time device gains (BASE_GAIN = -4.6/-7/-7/-4/-3/-3/-14/-5, =
evidence\tune-before-20261020-202655.json), never re-balanced between paths by the fitter.

**Measurement bookkeeping.** exactly ONE sweep exists on record: 8 channels captured
19:12-19:14 on 2026-10-20 (peak -46@4561 tweeter 1 through -15.9@113 midbass 1), stored twice
('measurements\run-20261020-1925' full-res, 'measurements\Car tune (2026-10-20)' identical
summary export at 20:01 - same capturedAt values, same peaks). Nothing else exists to void.

**Building now.** tune 4 'the tune-2-level dense tune': fit-dense.py --pin-gains --trim-lo 300
-> web\4-rnb-tune2-level-2026-10-22.idsp. Pinned gains = tune 2's device gains; stage-3 trim
floor raised to 300 Hz so the stock hump survives; 300 Hz-12 kHz sum overshoot still corrected.
Level-verified against tune 2 with review-tune.py before it touches the device.

## 2026-10-22 later - standing rules from the owner, and why v3 of the tune-2-level build died

**Owner's standing directive (competition standard).** Every tune is built as if against the
world: best possible result from the equipment at hand. No artificial band scarcity - if a
band reduces error AT ALL it goes in (up to the hardware's 31-per-output ceiling). The fitter's
own rule already matches this ("a band lands only if it STRICTLY lowers total error"); the cap
was the only limit, so builds now leave room for the 12 shared trim bands under 31
(--max-bands 19 + trim 12). "Do more with less" = exhaust the measurement's information, not
decorate slot counters.

**v3 of the tune-2-level build failed its own gate (kept out of the car).** The --bass-hz
exemption ("below 300 Hz chase the measurement's own 1/3-oct trend instead of the target")
systematically CUT every local peak below 300: a 1/3-oct trend is definitionally lower than
the 1/6 curve at each peak, and mean-centering stacked those into -25 dB of bass cut
(midbass: -11.2@90, -8.8@107, -8.7@127...). Review caught it: -30.8 dB worst below 800 vs
tune 2. Diagnosis: construction error, NOT a tool bug - the reviewer's per-frequency table
matched the file's band content exactly. The --bass-hz knob stays in the tool but must not
be used until its trend bias is fixed (subtract the LOCAL trend offset, not the raw curve).

**Build in flight: the real Tune 3** = web\3-house-curve-2026-10-22.idsp, --pin-gains
(tune 2's device gains, the loudness platform) --trim-lo 80 --max-bands 19 (-> 31 with trim),
house target full range (TARGET already carries the car shelf: +6 dB @35 tapering to 0 @300).
Gate: review vs tune 2 - residual must beat the car's 7.6 dB; mids/tweeters at tune-2 level by
construction; the bass hump gets pulled toward the house curve BY DESIGN (that IS the tune).

## 2026-10-22 afternoon - Tune 3 (house curve) BUILT and REVIEWED, car still off USB

**web\3-house-curve-2026-10-22.idsp** - the competition-standard build: --pin-gains
(tune 2's device gains as the loudness platform) --max-bands 19 + 9 shared stage-3 trim
bands, house target full range. 161 active bands (15/18/17/17/24/24/25/21 per output),
Q 4.0, gains -12..+3, no notches, transcription check PASS, stage-3 identity + negative
control PASS. Review vs tune 2: residual 2.7 dB rms (car as measured: 7.6), level profile
vs tune 2: 20-60 Hz +4.0 (house shelf), 60-800 mean -4.9 with worst -13.5 at 127 Hz (the
cabin hump pulled toward the house curve - BY DESIGN), 300-800 -1.2 (level-matched),
0.8-5k +5.3 and 5-20k +7.8 (house-curve correction of tune 2's recessed top). Expected
by ear: same midlevel, less boom, noticeably more presence/air. Tuned for a fair audition
against tune 2: same gains, same delays, same crossovers, same master.

**Library hygiene.** var\rejected\4-rnb-tune2-level-2026-10-22.idsp - the v3 build whose
bass-hz exemption cut peaks (see previous entry); failed its gate, kept for lineage only,
removed from web\ so the app cannot offer it.

**Load when powered:** .\load-tune-jump.ps1 -File web\3-house-curve-2026-10-22.idsp -Memory 1
(writes the playing memory, device memory 2), then
python tune\verify-device.py web\3-house-curve-2026-10-22.idsp --mem 1, then set
tunename.json to 3-house-curve. Revert to tune 2: same jump loader with
web\2-bor8f3-improved.idsp.

## 2026-10-22 13:20 - OWNER DIRECTIVE: measurements at 1/24-octave, standing ('no more 1/6')

Changed end to end and rebuilt (rta.exe + ds18-bridge.exe, 13:18):
- RTAMeter.cs: NumBands 61 -> 241 (1/24 grid, 20 Hz - 20.5 kHz), FftSize 8192 -> 65536
  (0.73 Hz bins at 48 kHz - at 8192 a 1/24 band at 100 Hz is narrower than one bin, so the
  finer grid would have been decorative), band window +/-1/12 -> +/-1/48 octave.
- web\\index.html: band-center math /6 -> /24 everywhere; sweep-gate constants scaled to hold
  the SAME octave strictness at 4x band density: SWEEP_MIN_BANDS 5 -> 20 consecutive,
  SWEEP_GAP_BANDS 1 -> 4 (percent thresholds unchanged).
- BridgeServer.cs export: grid now derives PER TAKE (241 points when the take has them,
  legacy 61 labelled 1/6) - re-exporting a pre-change set can never mislabel frequencies.
- export-meas.ps1 / propose-eq.ps1 / measure.ps1: same per-take rule, labels and docs.
- SweepTool burst analyser stays 61 @ 1/6 BY DESIGN: it measures arrival/bursts/delay, not
  response. Fitters untouched (grid-agnostic; 1/6 smoothing of 1/24 points is still a
  correct trend).

Validated: /rta.json reports 241 bands on true 1/24 centers (20, 20.6, 21.2...; five bands
95-110 Hz where 1/6 had two); doctor -SelfTest 4/4; verify-web ok (negative control ok).
Docs updated: TUNING-BRIEF.md format section, TUNE-PLAYBOOK.md capture spec.
NOTE: browsers cache the page - refresh the phone tab before the next measurement run.
Next measurement (parked, UMIK-1 on USB, DSP on) will be the first 1/24 set; the fitters
will then see the Q4-band structure that 1/6 used to smear - including the 1-1.7 kHz
3.5-inch artefact region at true resolution.

## 2026-10-22 14:05 - 3-house-curve loaded to the playing memory, verified

Owner authorised a direct load. NOTE (owner asked for this recorded): before the load the
device showed output 1 gain at -31.2 dB (was -7.3 this morning, value identical to output
7's) with all other paths and bands untouched - a stray cmd-11-level write from an unknown
source between plug-in and load (page fader, partial write, or a second app on USB). Not
reproduced, not investigated further by owner's call; the full output-block write of this
load erased it. Load path: new tune\_apply-fast.py (cmd 14 per path + cmd 11 block, the
app's own endpoints, crossovers untouched - same map across the library). 8/8 EQ writes,
output block enable 0xFF, verify-device.py --mem 1 = 0 failures (bands, gains, delays,
HP/LP all match the file). tunename.json -> 3-house-curve. Device: memory 2, master -4.8,
unmuted. Revert: tune\_apply-fast.py web\2-bor8f3-improved.idsp --mem 1 + verify.

## 2026-10-22 14:40 - THREE owner changes shipped: one tune per file, dropdown+Load, slider truth

**1. One tune per file (no more 4-slot banks).**
- tune\to-single.py: collapsed ALL 9 library files to a single <memory> block. Keepers:
  identical blocks -> block 0; genuinely distinct banks (ai2, tinker3, bor8f3, xswmps)
  -> the RunStatus.mainMemory block (live when saved). Every dropped block signed and
  logged to evidence\single-memory-collapse-20261022-1428.json; originals backed up as
  evidence\<name>.idsp.4mem. Sizes: 309-500 KB -> 123-125 KB.
  Signature gotcha fixed in the converter: the block's index attribute is position, not
  content - without normalizing it every bank reads as '4 distinct'.
- build-swap-tunes.py gained build_single() (used by fit-dense and build-swap mains);
  round-trip proven byte-equivalent (plan -> file -> reparse -> compare gains/delays/bands).
- Guards: verify-device, _apply-fast, system-predict now fall back to block 0 when a file
  carries one block but a higher index is asked for. review/atlas/idsp/page parser were
  already N-memory tolerant (verified on the converted files).

**2. Header: tune-name field -> dropdown + Load; memory buttons gone.**
- Header row: select#tuneBarSelect (filled from /api/tunes at boot via tuneBarFill) +
  btnTuneLoad. Load = fetch the file over HTTP, parseIdsp, applyTune. The 1-4 memory
  buttons, memSwap markup, memPaint/memSwitch, the scope chips and the 'also write levels'
  checkbox are REMOVED - applyTune now: file block 0 -> device memory 1 (the playing
  slot), always all outputs, EQ + levels/delays always, applyMemory first (readback-gated
  switch to the playing slot). Tunes tab card re-labelled 'Load to device'.
- Wire proof: python urllib GET of the converted file returns 127575 bytes,
  application/octet-stream, one <memory> (PS Invoke-WebRequest's decode of octet-stream
  was misleading; fetch().text() decodes UTF-8 per spec).
- verify-web.ps1 updated: HtmlLacks memSwap/tuneScopeChips reaching, HtmlHas dropdown+Load,
  applyTune asserts (var mi = 1, doOut = true, applyMemory, memories[0]). 149 ids all
  present, baseline 14/14, negative control ok, node --check clean.

**3. Volume sliders now show true position.**
- Root cause: seedVolume wrote input.value = -g*10 (the WIRE's u16 encoding) into a slider
  whose range is -400..0 -> every real level clamped to 0 -> thumb pinned at max while the
  label read e.g. -4.6 dB. Fixed to g*10; round trip via the change handler (value/10) is
  now consistent. Range verified against Protocol.cs GainCodec: DP12_OutputGainTrack
  -40.0..0.0 dB (midpoint -20 = exactly halfway); master slider already -60..0 (DP12 main).

**Pending (car off USB):** the live round-trip of the converted file on the device - next
power-up, one Load from the new dropdown IS that test (apply + verify-device). Phone page
needs a refresh to pick up the new UI. Device slots 1/3/4 still hold house-curve from
earlier; the new UX never touches them.

## 2026-10-22 15:35 - raw measurements now open in REW, and the sweep reference tool caught up

**REW export (owner: 'make the raw measurements openable and viewable in REW').**
Format verified against REW's own help (roomeqwizard.com - Importing Measurement Data):
File > Import frequency response (Ctrl+I) takes .txt/.frd/.dat, freq + magnitude per line
(space/comma/tab), >= 5 points, ascending - 'only lines which begin with a number are
imported, others ignored', so a '*' comment header carries provenance for free.
- tune\to-rew.py: converts ANY run folder (both layouts, 1/6 and 1/24 grids) ->
  <run>\rew\<channel>.txt, idempotent. Ran on both existing runs: 16 REW files total
  (8 from run-20261020-1925, 8 from 'Car tune (2026-10-20)').
- export-meas.ps1 now writes the REW copy next to its CSV, and BridgeServer.cs does the
  same in the run-cycle export (invariant-culture numbers, slug trace names) - future
  runs are REW-ready with no manual step. Bridge rebuilt (15:27) and restarted; DSP was
  closed during the swap. Also fixed a label bug from the 1/24 change: export-meas's
  grid field said '24 octave' -> now '1/24 octave'.
- Validation: doctor -SelfTest 4/4; verify-web ok (149 ids, baseline 14/14, negative
  control ok).

**Collateral fix - make-flat-memory.ps1 vs one-tune-per-file.** verify-web caught the
sweep-reference generator failing: it defaulted to -From 1 -To 3 (a 4-slot bank) and
web\bor8f3.idsp now has ONE block. Adapted honestly, not clamped:
- defaults now -From 0 -To 0 (flatten the single block in the copy; 4-block banks via
  evidence\*.idsp.4mem still take 1/3); -From == -To allowed only when the file has one
  memory; the 'tune untouched' integrity check now re-reads the SOURCE file from disk in
  in-place mode (before, it compared the output's own [From] slot - meaningless when
  From == To). The source-overwrite guard was kept through the refactor.
- verify-web's invocation and assertions follow (1 memory, flat block 0, new Say line).
  Manual run: VERDICT PASS (0 non-zero bands, gains/delays from the tune, protection
  HPs 2500/100/off/off). Note: with From == To the replaced slot is also the reference -
  documented in the script header.

## 2026-10-22 evening - max-utilisation build: web\4-maxfit-31b-2026-10-22.idsp, residual 2.1 dB

**Owner request: "turn my measurement into the target curve as best as possible - my DSP
allows 31 band adjustments each, use them."** Competition standard (earlier standing
directive): no artificial band scarcity, but no decorative bands either - only bands that
STRICTLY reduce error go in.

**fit-dense.py grew `--fill` (opt-in; default path provably unchanged).**
- `fit_dense(..., seeds=...)`: existing bands enter the search as seeds - they count
  toward the 31 ceiling, take part in every descent and every 1/12-oct separation check,
  and their stacked lift is pre-charged against the +4 dB budget, so a fill band obeys
  every rule a stage-1 band obeys.
- With `--fill`, stage-3 trim and per-path fill ALTERNATE, max 4 rounds; each round
  RE-DERIVES the ideal common shift from the CURRENT system. Probe that motivated it:
  a second trim pass on the then-current system found 7 more strictly-improving shared
  cuts (2.65 -> 2.27) that the single pass could not see. nmax = min(12, 31 - max count)
  keeps every round under the hardware ceiling; rounds stop at the first no-improvement.
- Regression proof: `--pin-gains --max-bands 19` WITHOUT `--fill` reproduces
  `tune-plan-house.json` byte-identical (A2 build, PLANS IDENTICAL), with identity,
  negative control and transcription all PASS.
- **Latent bug fixed:** the transcription check's `for mi in (1,2,3)` assumed 4-memory
  banks; since to-single.py every file carries ONE memory, so every fit-dense build since
  crashed AFTER writing the file and silently skipped the check. Now iterates only the
  memories that exist. Seen to fail? Yes - the crash was the failure, on screen.

**The build:** `web\4-maxfit-31b-2026-10-22.idsp` + `tune\tune-plan-max.json`
- `python tune/fit-dense.py --pin-gains --max-bands 19 --fill --out tune/tune-plan-max.json --idsp web/4-maxfit-31b-2026-10-22.idsp`
- 205 bands: 20/23/22/22/30/30/**31**/27 per output (path 6 at the ceiling; 43 slots
  genuinely empty - no legal band left that strictly improves).
- Gains = BASE_GAIN pinned (tune 2's loudness platform), delays/crossovers/master as the
  whole library. Rounds: trim 9+3+2 shared bands/path, fill +4 (all in round 1), converged.
- Checks: identity PASS, negative control PASS, transcription PASS (0 mismatches),
  negative control PASS, per-path EQ-beats-no-EQ re-grade PASS, verify-web ok (149 ids).
- **system-predict: residual 2.1 dB rms (60 Hz-10 kHz, aligned 300 Hz)** - the new best:
  flat 7.6 / tune 2 7.1 / tune 1 3.6 / tune 3 house 2.7 / **this 2.1**.
- review-tune: `[OK] Beats the car as measured (5.5 dB better)`. Level vs tune 2 below
  800 Hz: worst -19.0 dB (at 100-125 Hz) - the house-curve bass pull, by design (tune 3
  showed the same shape at -13.5 at 127 Hz; this fit just reaches the target harder).
  2-16 kHz sits within +/-1.5 dB of tune 2. Gains/delays/crossovers/master identical, so
  a fair audition is still "same platform, corrected spectrum" - but expect the bass and
  lower mids audibly lower than tune 2; that IS the tune.

**REW view (owner's original loop):** `tune\rew\4-maxfit-predicted-system.txt` and
`tune\rew\house-target-curve.txt` - File > Import frequency response both; their
difference is the fit residual. The eight measured curves are already exported at
`measurements\run-20261020-1925\rew\`.

**Prediction only - nothing written to the DSP** (car off USB). Load when powered:
`.\load-tune-jump.ps1 -File web\4-maxfit-31b-2026-10-22.idsp -Memory 1`, then
`python tune\verify-device.py web\4-maxfit-31b-2026-10-22.idsp --mem 1`, then set
tunename.json. Revert: same loader with `web\2-bor8f3-improved.idsp`.

**PRE-EXISTING failures found (not from this session, left untouched):**
`tune\verify-swap-tunes.py` reports 4 failures - "tune 1/2: 4 memories" (files collapsed
to one block by to-single.py at 14:28) and "tune 1/2 path 6: HP 100" (the owner's 8"
HP 50 -> 100 change in CUR_HP). The gate still describes the pre-14:40 world; the two
tune files' mtimes predate this session and were not rewritten by it.

## 2026-10-22 - phone Load button bug (mine), found by the owner's tap, fixed and guarded

The owner hit Load on the phone with 4-maxfit selected: it WIPED all 8 outputs to 31 blank
bands (flat) instead of loading. Root cause: applyTune used mi for BOTH the device slot
(1) and the file index - tuneBands(1) on a single-memory file returned an empty list (its
defensive path), and the bridge zero-filled the block on an empty band write. Every other
read (tuneOutputBlock's mem) was already block-0; this one call site was missed, and my
verify-web assertion checked the head of applyTune, not the call site.
Fixes: step() now reads tuneBands(0, p); an empty band list REFUSES the write ('blank EQ
never goes to the device'); applyTune's head guards memories[0] presence; verify-web now
asserts both the tuneBands(0 call site and the refusal text (deeper assertion).
Restored: 4-maxfit loaded via _apply-fast (8/8 + output block), his pending 8in HP 100
written via /api/passband with eqread readback (HP 100 slope 3 = LR24), full
verify-device = 0 failures. NOTE: master reads -5.8 today (design -4.8) - moved during the
session, owner's call. tunename = 4-maxfit-31b-2026-10-22.

## 2026-10-23 pre-dawn - competition-entry push: mistake ledger, three experiments, verdict

Owner: "analyze all your mistakes, capitalize on the new observations, present me a
competition-worthy entry."

**MISTAKE LEDGER (full, honest):**
1. Phone Load wipe: applyTune passed the DEVICE slot (1) into tuneBands (a FILE index);
   single-memory file -> empty list -> bridge zero-filled all 8 outputs. The one-tune
   rewrite missed one call site, and verify-web asserted applyTune's HEAD, not the call
   site. Fixed: block-0 read + blank-EQ refusal + head guard + call-site assertions.
   Found by the owner's ear, not by my gates.
2. Band scarcity: house-curve built one-shot at 19/path - 161 bands, residual 2.7 while
   the same data and the fill method reach 205/2.1. I treated 31 as politeness instead
   of a budget (the owner's active-bands directive, proven by the numbers).
3. Bass hedging: after the RnB rejection I softened the bass pull (-9.9 vs the target's
   -11.5 vs tune 2). Let one taste reaction override the standard. Rule now: the target
   owns voicing; the ear judges BETWEEN tunes, never inside the fit.
4. Shallow test assertion (root of #1): assertions must bind call sites and behavior,
   not the first lines of a function.
5. --bass-hz trend bias: the exemption redefined the target below its edge and cut the
   peaks out (-30.8 dB). Retired; any knob that reshapes the target needs its bias
   proven before it ships.
6. Stale gate left owed: verify-swap-tunes failed 4 assertions for hours after two
   legitimate world changes (one-memory collapse, owner's HP 100). Closed tonight.
7. Library HP inconsistency (found while closing #6): tune 1/2 files still carried the
   old 8" HP 50 - a future full-crossover load would have REGRESSED the owner's
   explicit 100 Hz setting. Normalized via tune/set-hp.py; backups + log in
   evidence\hp-2026-10-23-*. Revert = restore the backups.
8. Prune lesson (new): system-level subtraction is right in principle but my first
   implementation had epsilon 1e-6 (ate numerical dust), per-path refill cycles that
   re-inflated the system the prune had lowered, and a greedy walk that left the basin:
   68 bands / 2.5 dB, metric AND density lost. Rejected. If revisited: meaningful
   epsilon, NO per-path refill after a system prune, and a net-improvement requirement
   vs the control before anything ships.

**BUILD SWEEP (5 configs: --pin-gains --fill, no bass exemption, stage-1 at 15/19/23/27
bands + a 8-rounds run):**
- mb15 -> 2.3 dB (a path hit 31 during stage 1, serializing the shared trim early -
  composition matters, not just totals: 206 bands but WORSE than 205).
- mb19 / mb23 / mb27 / mb19+rounds8 -> residual 2.1 dB, all FOUR byte-identical to
  each other AND to tune-plan-max.json (the other agent's plan, independently
  re-derived). Basin-independent, round-independent fixpoint.

**OPERATION EXHAUSTION - why 2.09 dB stands on this measurement:**
- ADD: fill logs +0 on every path, every round; shared trim freezes at the 31 ceiling
  (nmax=0).
- REMOVE: --sys-prune from the fixpoint only found sub-0.0005 dB numerical dust and
  below-HP-corner inaudibles; real removals plus refill churned to 68 bands / 2.5 dB.
  Rejected, evidence kept in var\build-sweep\ (plan-p1, cand-p1, log-p1).
- MODIFY: --trim-deepen re-fits the 14 bands shared by all eight paths IN PLACE (count
  cannot grow, so the ceiling cannot block it) - the fit declines: no strictly
  improving configuration exists. Run output byte-identical to 4-maxfit, identity /
  negative control / transcription all PASS.
Three independent operation classes refuse => **web\4-maxfit-31b-2026-10-22.idsp is
the local optimum of run-20261020-1925 under the house rules.** What remains sits at
the data's edge: 63 Hz lift-cap/single-driver physics (only the rear 6.5" plays there),
the 1 kHz energy-sum plateau (two cones at equal share, deep shared cut refused), and
the 10 kHz mic-floor approach. The only thing that breaks 2.1 is NEW DATA: a 1/24-oct
measurement of the PLAYING system (tooling already upgraded), then fit measured truth
instead of a magnitude-only prediction that cannot see arrival phase.

**GATES (final battery):** verify-swap-tunes 0 failures (expectation -> one memory;
library HP normalized); default-path regression byte-identical x2 (before and after
the new args); --fill path byte-identical to the verified plan after all edits;
deepen run identity PASS / negative control PASS / transcription PASS; verify-web ok.

**THE ENTRY:** web\4-maxfit-31b-2026-10-22.idsp - 205 bands 20/23/22/22/30/30/31/27,
residual 2.1 dB (flat car 7.6, tune 2 7.1, house 2.7), gains pinned to tune 2's
platform, delays unchanged, 8" HP 100 in file AND on device. Device-verified 0/42
earlier this evening and loaded by the owner's own phone test; nothing in the library
beats it from this measurement. New tool flags tonight: --fill-rounds, --sys-prune
(rejected, kept for research), --trim-deepen (proves the ceiling's refusal) - all
default-off with byte-identical default regressions. tune/set-hp.py is the new
crossover-maintenance tool.

## 2026-10-23 - driver level matching (Rawcat method), web\5-levelmatch-31b-2026-10-23.idsp, 1.8 dB

**Doctrine source:** Rawcat, *Back to basics - level matching* (youtube, Ye0pQHxE49Y),
transcript taken down by the owner; digest written into TUNE-PLAYBOOK.md as **§9.0**. His
rule: EQ sets SHAPE, channel gain sets LEVEL, and a tune whose drivers are only shape-fitted
will not sum to the house curve. Read level at the band pass under 1/1 smoothing, find the
weakest link, pull everything down to it.

**Why this became a build and not just a lecture:** the machinery already existed and was
switched off. fit-dense.py's stage-2 `delta` is *the median over the home band of (target -
predicted after EQ)* - Rawcat's rule, median-anchored = playbook rule 3 - and the common shift
that follows is his "weakest link, pull everything down." `--pin-gains` sets every
`gainDeltaDb` to 0, so the stage never runs. Tune 3 and 4-maxfit are both pinned.

Two different things were both called "level match", and they conflict:

| | equalises | needed for |
|---|---|---|
| A/B level match | loudness BETWEEN two files | §9.1 rule 2 |
| driver level match | relative driver levels INSIDE one tune | summing to the house curve |

`--pin-gains` serves the first and silently disables the second.

**The build:** `web\5-levelmatch-31b-2026-10-23.idsp` + `tune\tune-plan-lvl.json`
- `python tune/fit-dense.py --max-bands 19 --out tune/tune-plan-lvl.json --idsp web/5-levelmatch-31b-2026-10-23.idsp`
  (identical to the 4-maxfit command minus `--pin-gains`; single pass, no `--fill`)
- 185 bands 18/21/20/20/27/27/28/24. Deltas (dB): -2.7 / 0.0 / -0.2 / -4.4 / -5.8 / -0.9 /
  -17.2 / -18.5. Path 1 (tweeter 2) is the reference = the weakest link. New gains
  -7.3 / -7.0 / -7.2 / -8.4 / -8.8 / -3.9 / **-31.2** / **-23.5**. Nothing clamps at -40.
- **Independent cross-check:** paths 6/7 land at -31.2/-23.5, within ~1 dB of tune 1's
  -30.0/-22.1, which a DIFFERENT fitter (fit-tune.py) derived from the same run. Two
  independent fits converging on the same driver balance is evidence the level match is
  real, not an artefact of this tool.
- **residual 1.8 dB rms (system-predict) vs 4-maxfit 2.1 / house 2.7 / tune 1 3.6 / flat 7.6.**
  The gain lands exactly where level matching should put it: presence 2k/3.15k/4k goes from
  -1.7/-4.9/-3.8 (4-maxfit) to **-0.2/-0.6/-2.6**.
- Gates: system-shift identity PASS, negative control PASS, transcription PASS (0 mismatches),
  per-path EQ-beats-no-EQ PASS, verify-web ok (149 ids, baseline 14/14).

**This does NOT overturn the entry above.** 4-maxfit remains the local optimum *subject to the
pin-gains constraint*; releasing that one constraint reaches 1.8. The "only new data breaks
2.1" claim was made under pinned gains and now needs that qualifier.

**The cost, stated plainly - review says [NO] on A/B:** level vs 4-maxfit is **-16.3 dB at
worst below 800 Hz**, roughly -11 to -16 dB broadband. This tune is ~13 dB quieter than what
is playing now. It CANNOT be A/B'd against 4-maxfit or tune 2 by ear - loudness would decide
it, which is exactly why pin-gains existed. Expect the bass and lower mids much quieter; that
IS correct level matching, not a fault.

**Why it cannot just be turned back up in the DSP:** restoring the platform needs a common
~+12 dB shift, but output gain tops out at 0 dB and paths 0-5 sit at -3.9..-8.8, so a uniform
shift clamps them and destroys the balance just bought. The only legal way back up is Rawcat's
own escape hatch: raise the WEAKEST LINK (amplifier gains on the tweeter and 3.5" channels)
and re-run. Hardware step, needs the car.

**--fill FAILS here, honestly:** `--max-bands 19 --fill` reaches 1.40 dB internally but exits
1 on the per-path gate: `path 0: final EQ does not beat no-EQ on its adjusted target
(4.75 >= 4.55)`. Cause: the fill iteration optimises the SYSTEM residual and does not check
the per-path gate until the end, so round 3's two extra shared trim bands pushed tweeter 1
below its no-EQ baseline. The gate is right and was NOT weakened or bypassed. The fix belongs
in the iteration (reject a round that breaks any path), not in the gate - not done today.

**Sub: untouched, by owner's decision.** Still no sub measurement anywhere (run-20261020-1925
is eight files, sub amp disconnected). Its level remains the head-unit trim and the Kicker
knob, invisible to every tool here.

**Prediction only - nothing written to the DSP.** Load when powered:
`.\load-tune-jump.ps1 -File web\5-levelmatch-31b-2026-10-23.idsp -Memory 1`, then
`python tune\verify-device.py web\5-levelmatch-31b-2026-10-23.idsp --mem 1`, then set
tunename.json. Revert: same loader with web\4-maxfit-31b-2026-10-22.idsp.

