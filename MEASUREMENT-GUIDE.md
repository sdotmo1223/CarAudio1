# REW Measurement Guide — 2017 Lincoln Continental, DS18 DSP

Purpose: get a **gated, per-driver, phase-aware** measurement so the tune can
stop being built on one summed ungated RTA. This unlocks per-channel EQ,
verifies the time alignment in the car rather than by tape measure, and settles
the sub/mid handoff.

Time needed: about 45–60 minutes, once.

---

## 0. What changes vs what we've been using

| | what we have now | what this produces |
|---|---|---|
| method | RTA (real-time analyser) | **swept measurement** |
| reflections | all baked in | **gated out** |
| phase / impulse | none | **included** |
| per driver | no — one summed curve | **yes — one curve per channel** |

An RTA cannot separate a driver from the room. A gated sweep can.

---

## 1. Setup

1. **Microphone** — calibrated USB mic (UMIK-1 or similar). If yours has a
   calibration file, load it: `Preferences → Cal Files` → point at the **0°**
   file.
2. **Placement** — mic at the **driver's head position**, at ear height,
   pointing **forward**. Headrest removed if you can, or the mic clipped to a
   stand. This position must not move for the whole session.
3. **Route REW's output into the car system** — laptop headphone/USB out into
   the DSP or head unit input. You should be able to play REW's sweep through
   all the speakers.
4. **Level** — run one sweep and check. You want roughly **75–85 dB** at the
   mic and **no clipping**. Loud enough to be above noise, not so loud it
   compresses.

---

## 2. REW measurement settings

Open the **Measure** dialog (the big Measure button on the main screen).

| setting | value |
|---|---|
| Start frequency | 20 Hz |
| End frequency | 20 000 Hz |
| Sweep length | **256k** (use 512k if you want better low-end resolution) |
| Repetitions | **4–8** (averaging kills noise) |
| Timing | **enable a timing reference** — see below |
| Level | −12 to −6 dBFS, adjusted so nothing clips |

**Timing reference — this is the important one.** Without it there is no
impulse response, no phase, and no gating. Two options:

- **Loopback** — a cable from a spare output back to a spare input. Most
  accurate.
- **Acoustic** — REW plays a short chirp from one designated speaker and times
  everything against it. Easier in a car, slightly less precise.

Either is far better than none. Menu wording varies a little between REW
versions — look for "Timing" in the Measure dialog.

---

## 3. What to measure — 10 sweeps

**Solo each channel in the DSP software** before measuring it: mute every other
output, leaving only the one under test.

| # | measurement | how |
|---|---|---|
| 1 | ch1 driver-front tweeter | solo ch1 |
| 2 | ch2 passenger-front tweeter | solo ch2 |
| 3 | ch3 passenger-rear tweeter | solo ch3 |
| 4 | ch4 driver-rear tweeter | solo ch4 |
| 5 | ch5 front 3.5″ (pair) | solo ch5 |
| 6 | ch6 rear 3.5″ (pair) | solo ch6 |
| 7 | ch7 front 8″ (pair) | solo ch7 |
| 8 | ch8 rear 6.5″ (pair) | solo ch8 |
| 9 | **subwoofer alone** | mute all DSP outputs, play sub only |
| 10 | **full system** | everything on |

**Name each one clearly in REW** — e.g. `ch5-front-3.5`. REW keeps the name in
the file, and it saves me guessing which is which.

Measurement **9 is worth a lot**: we have never known your sub's low-pass, and
that curve will show exactly where it rolls off and how it meets the mids.

---

## 4. Gate out the reflections

After each sweep, open **IR Windows** and apply a gate — start around
**5–10 ms**, wide enough to keep the driver's own response but short enough to
cut the first strong reflection. Too short and you lose bass; too long and the
room comes back in. Aim for a clean impulse that decays before the gate closes.

If you only do this loosely, that's fine — even an approximate gate is a large
step up from an ungated RTA.

---

## 5. Save and send

**Easiest:** `File → Save` creates a `.mdat`. **I can read `.mdat` directly** —
no text export needed.

Alternatively `File → Export → Export measurement as text` gives a `.txt` with
frequency / SPL / phase; that works too.

Then: **zip all ten and send with WeTransfer** (that route has worked reliably).

---

## 6. Optional but valuable — validate my filter model

One extra measurement settles whether my EQ maths matches your hardware.

1. Pick one channel (ch5, the front 3.5″).
2. Set a **single extreme filter**: **−10 dB at 1 000 Hz, Q 4**. Disable all
   other EQ on that channel.
3. Measure it, export, and tell me exactly what you set.

I compare that curve against what `peq.py` predicts a −10 dB / Q4 filter should
do. If they match, every EQ value I generate is trustworthy. If they don't, I
learn the difference and correct for it.

---

## 7. What I do with it

Once I have these ten curves I can:

- **EQ each channel individually** instead of applying one summed correction to
  all eight — this is where the other 20+ bands per channel start earning their
  place
- **Verify the time alignment** from the actual impulse responses rather than
  from a tape measure
- **Set the sub/mid handoff properly** now that I can see both sides of it
- **Check polarity** — a mis-wired driver shows up immediately in the impulse
  response, so we stop guessing about it
- **Build the R&B house curve precisely** rather than approximating it with
  four broad cuts

---

## Quick checklist

- [ ] Mic calibrated, positioned at ear height, pointing forward, not moved
- [ ] Sweep output routed through the car system
- [ ] Measure dialog: 20 Hz–20 kHz, 256k, 4–8 reps, **timing reference ON**
- [ ] Level 75–85 dB, no clipping
- [ ] 8 solo channel sweeps + sub solo + full system
- [ ] IR window applied
- [ ] Saved as `.mdat`, zipped, sent via WeTransfer
- [ ] Optional: the −10 dB @ 1 kHz Q4 model-validation sweep
