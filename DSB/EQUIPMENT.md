# Equipment — what is actually in the car

Last verified against hardware: 2026-10-21 *(inherited: this document's rows are
manufacturer specifications cross-checked against product pages on 2026-10-20, not readings
taken in the car — the rig-level stamp lives on `TUNE-PLAYBOOK.md`)*.

> This file is the **why**. For the **how** — programming a tune end to end — read
> `TUNE-PLAYBOOK.md`.

Source: `github.com/sdotmo1223/CarAudio1`, files `make_tune_v4.py` / `make_tune_v5.py`
(the docstrings list the drivers against the DSP outputs), cross-checked against each
manufacturer's own product page on 2026-10-20.

Car: 2017 Lincoln Continental. DSP: DS18 `DP12` (12-channel, `.idsp` preset format).

## The drivers

| DSP out | App label | Driver | Impedance | Rated band | Sensitivity | Power | Nominal size |
|---|---|---|---|---|---|---|---|
| 0 | ch 1 | BZRK Audio **RS-T150** 1" aluminium-dome neodymium tweeter | 4 Ω | 1.5 k – 25 k Hz | 93 dB | 150 W **max** (no RMS published) | 1" (42 mm cutout, 11 mm deep) |
| 1 | ch 2 | same | 4 Ω | same | 93 dB | same | same |
| 2 | ch 3 | same | 4 Ω | same | 93 dB | same | same |
| 3 | ch 4 | same | 4 Ω | same | 93 dB | same | same |
| 4 | ch 5 | NVX **XQS3v2** 3.5" carbon-fibre neo midrange | 4 Ω | 200 – 18 k Hz | **85 dB** | **50 W RMS** / 100 W peak | 3.5" (Fs 150 Hz, Qts 0.80) |
| 5 | ch 6 | same | 4 Ω | same | 85 dB | 50 W RMS | same |
| 6 | ch 7 | Deaf Bonce **Apocalypse AP-M81SE** 8" midrange | 4 Ω | 110 – 10 k Hz | 95.1 dB | 170 W RMS / 340 W max | 8" (1.5" CCAW coil) |
| 7 | ch 8 | Orion **XTR XTX654** 6.5" midrange, bullet phase plug | 4 Ω | 140 – 12 k Hz | 97 dB | 350 W RMS / 1400 W peak (pair) | 6.5" (1.5" coil, cast basket, paper cone) |

## The amplifier — DS18 DSP8.6iA

It is not a separate amp: it is **6-channel Class D amplifier with an 8-channel DSP** in
one box (17 × 8 × 4 in). Confirmed by the owner and by DS18's own spec sheet.

| | |
|---|---|
| amplified outputs | 6 |
| DSP channels | 8 |
| pre-out (line level) | **2** (2 V RMS) |
| ch 1–4 | 6 × 80 W RMS @ **4 Ω only** (70 W in DS18's marketing copy — their page contradicts itself) |
| ch 5–6 | 80 W @ 4 Ω, **130 W @ 2 Ω**, 260 W bridged @ 4 Ω |
| bandwidth | 10 Hz – 21 kHz |
| inputs | 6-ch high level, stereo aux, optical 96/24, Bluetooth |
| SNR | > 95 dB A-weighted |

**Only channels 5 and 6 are rated for 2 Ω.** Channels 1–4 are 4 Ω only, so any L/R pair
wired in parallel (2 Ω) on channels 1–4 is outside the amp's spec.

## Second amp — Deaf Bonce Apocalypse ATOM-400.4 PRO

Runs the two 8" and the two 6.5" drivers, fed from the DSP's two pre-outs (owner-confirmed).

| | |
|---|---|
| channels | 4, **1 Ω stable** |
| 4 Ω | 4 × **200 W RMS** |
| 2 Ω | 4 × 310 W |
| bridged | 2 × 620 W @ 4 Ω, 2 × 800 W @ 2 Ω |
| response | 20 Hz – 20 kHz |
| crossovers | HP 20–12 k, LP 45–12 k, **12 dB/oct**, with bass boost |
| input | RCA 0.2–6 V, high level 1.7–11 V |
| SNR | **80 dB** |
| damping | 102 |

## The complete chain

| DSP out | DSP gain | path | amp channel | power | driver |
|---|---|---|---|---|---|
| 0 | −4.6 | DSP amp ch 1 | 80 W @ 4 Ω | 80 W | front-left tweeter |
| 1 | −7.0 | DSP amp ch 2 | 80 W @ 4 Ω | 80 W | front-right tweeter |
| 2 | −7.0 | DSP amp ch 3 | 80 W @ 4 Ω | 80 W | rear-left tweeter |
| 3 | −4.0 | DSP amp ch 4 | 80 W @ 4 Ω | 80 W | rear-right tweeter |
| 4 | −3.0 | DSP amp ch 5 | 130 W @ 2 Ω | 65 W each | front 3.5" **pair, mono** |
| 5 | −3.0 | DSP amp ch 6 | 130 W @ 2 Ω | 65 W each | rear 3.5" **pair, mono** |
| 6 | −14.0 | pre-out 1 → ATOM 1+2 | 200 W @ 4 Ω | 200 W each | two 8" **mono** |
| 7 | −5.0 | pre-out 2 → ATOM 3+4 | 200 W @ 4 Ω | 200 W each | two 6.5" **mono** |
| — | — | head unit → sub amp | unknown | unknown | Rockford T2 12" |

**Everything below 2.5 kHz is mono.** The DSP has 8 channels for 12 drivers, so each mid
pair and each bass pair shares one channel — one signal, two speakers. Only the four
tweeters are stereo. This is the single biggest structural limit in the system:

- The stage can only be stereo above the tweeter crossover.
- **No single delay can align a left/right pair at different distances.** The tune used
  the *average* distance per pair, so left and right midbass are permanently offset from
  each other — a comb filter at the listening position, worst around the 1–3 kHz region.
- Measuring one of those channels measures **both** drivers at once, so the impulse
  response contains two arrivals and the reported delay is a blend, not a truth.

Fixing it needs more DSP channels (a DP12-class unit), not more EQ.

## Power and signal topology (owner-confirmed)

**Power:** all three amps share one battery and one power splitter.

**Signal:** the sub takes its input **directly from the head unit**. Everything else goes
through the DSP.

Consequences that actually matter for a tune:

- The DSP master fader is a **mains-only** control. It does not move the sub. The sub's
  level is the head unit's sub trim × the Kicker's gain knob, and both are invisible to
  the DSP.
- The sub therefore has no DSP delay, no DSP level and no DSP crossover. Every other
  channel is delayed *to* it, so the DSP delay values are anchored to a driver the DSP
  cannot see or correct.
- Shared supply: the sub's transients sag the rail that the DSP amp and the ATOM run on.
  At sweep levels (≤ 10 W per channel) that is irrelevant and does not compromise a
  measurement. It only shows up at full volume, where the midrange loses headroom exactly
  when the bass hits.

## The sub — Rockford Fosgate Power T2, 12"

| | |
|---|---|
| power | 1200 W RMS / 2400 W max (CEA-2031) |
| impedance | dual voice coil — T2D212 = 2 Ω + 2 Ω, T2D412 = 4 Ω + 4 Ω |
| sensitivity | 83.5 dB @ 1 W/1 m |
| Fs | 38.7 Hz |
| Xmax | 22.3 mm |
| response | 20 – 250 Hz |
| voice coil | 4.0" (101 mm), 292 oz magnet, 36.5 kg |

It has its own amp and sits **outside the DSP**: no DSP delay, no DSP level, and no DSP
crossover. Its level lives on the amp's gain, and the DSP channels are delayed to it
(v7 used an estimated 250 cm reference).

## Sub amp — Kicker CXA1800.1

Owner-confirmed. Kicker's own spec sheet:

| | |
|---|---|
| 4 Ω | 900 W × 1 |
| 2 Ω | **1800 W × 1** |
| 1 Ω | 1800 W × 1 ±10 % |
| response | 25 – 200 Hz |
| low-pass | variable **50 – 200 Hz, 12 dB/oct** |
| subsonic | **25 Hz, 24 dB/oct — fixed, not defeatable** |
| bass boost | KickEQ **0 – 6 dB @ 40 Hz** |
| input | 125 mV – 5 V low level, 1 – 40 V high level |
| SNR | 95 dB |

Two things follow.

**The amp can overdrive the sub by 1.8 dB.** 1800 W into a 1200 W driver, with a fixed
25 Hz subsonic and no thermal limiter. The gain knob is the only thing between the T2 and
its voice coil — and the T2's Fs is 38.7 Hz, so 25 Hz subsonic is not much protection in
a box tuned low.

**The sub's low-pass is 12 dB/oct.** The 8" high-passes at 110 Hz (24 dB/oct). A 12 dB
low-pass over a 24 dB high-pass leaves a 12 dB/oct overlap: at 200 Hz the sub is only
−12 dB. Where the LP knob actually sits decides whether there is a clean handoff or a
broad overlap with the doors. Target ≈ 80–100 Hz, and KickEQ at 0.

## Rated band is not usable band

Beaming starts roughly where the diameter equals a wavelength (c ≈ 343 m/s):

| Driver | Beams above | Consequence |
|---|---|---|
| 8" | ~1.7 k Hz | rated to 10 k, but only on-axis |
| 6.5" | ~2.1 k Hz | rated to 12 k, but only on-axis |
| 3.5" | ~3.9 k Hz | rated to 18 k, but only on-axis |
| 1" dome | ~13 k Hz | fine to the top |

In a car every driver is heard off-axis, so the rated top of each cone is a number to
ignore when choosing crossovers. The useful ceilings are the beaming numbers.

## The efficiency problem (the one that shapes the whole tune)

Max SPL with the power each driver can actually get (1 W/1 m sensitivity + 10·log10(W)):

| Driver | Power available | Math | Ceiling |
|---|---|---|---|
| NVX 3.5" | **50 W — its own RMS limit** | 85.0 + 17.0 | **102.0 dB** |
| BZRK 1" | 80 W (DSP amp) | 93.0 + 19.0 | 112.0 dB |
| Deaf Bonce 8" | **200 W** (ATOM) | 95.1 + 23.0 | 118.1 dB |
| Orion 6.5" | **200 W** (ATOM) | 97.0 + 23.0 | 120.0 dB |
| Rockford T2 12" | **1200 W (its own RMS limit)** — Kicker can give 1800 | 83.5 + 30.8 | **114.3 dB** |

18 dB of spread across the mains, and the 3.5" still owns the top of it. Driven to the
same level, at the point where the 3.5" is at its 50 W limit (102 dB):

- the 8" needs **4.9 W of its 200 W** (2.5 %)
- the 6.5" needs **3.2 W of its 200 W** (1.6 %)
- the tweeter needs **7.9 W of its 80 W**

So the system ceiling is set by the 3.5", and the two pro drivers sit at a few percent
of what the amp can give them. That is not a defect to EQ around — it is a hardware
reality that levels, not filters, have to absorb.

It also means the band the ear is most sensitive to (roughly 1–4 kHz) is carried by the
least sensitive driver in the car, while the 12 dB-hotter drivers do 110–1200 Hz where
they are barely working.

The sub is the opposite case: 83.5 dB is the *lowest* sensitivity in the system, but it
has 1200 W of clean headroom, so it reaches 114 dB — **12 dB above the mains' ceiling**.
Inside a car the cabin adds roughly another 10–15 dB below 100 Hz, so the real low-end
advantage is closer to 20 dB. Nothing about this system is bass-shy: the bass has to be
pulled down to 12–20 dB below its maximum to sit level with the midrange, and every dB it
sits hot above that masks the exact band the 3.5" is struggling to produce.

That is the whole complaint in one line: **the midrange is the limit, and the bass is
hiding it.**

## Sweep safety at 80 % master

80 % on the master slider = −8 dB = 0.158 of the power, and the sweep is a full-scale
sine (the worst case a signal can be). With the current tune's channel gains:

| ch | driver | channel gain | amp | W at −8 dB master |
|---|---|---|---|---|
| 1–4 | tweeter | −4.6 / −7.0 | 80 W | 4.4 / 2.5 W |
| 5–6 | 3.5" pair (2 Ω) | −3.0 | 130 W | 10.3 W per channel = **5.1 W per driver** |
| 7 | two 8" | −14.0 | 200 W | **1.3 W per driver** |
| 8 | two 6.5" | −5.0 | 200 W | **10.0 W per driver** |

Everything stays around 10 W or less. Electrically the sweep is gentle; the only remaining
risk is **mechanical excursion at low frequency**, which is why the sweep floor matters,
not the master setting.

**The sub is not in this table and cannot be.** It hangs off the head unit into its own
amp, so the DSP's mute, master and sweep mode do not touch it. Any per-channel sweep run
with the sub live is contaminated from 20 Hz up to well past 100 Hz — a 114 dB sub against
a 6 W midrange is not a small error. The sub amp has to be switched off (or its RCA pulled)
for every sweep, not just turned down.

## What this implies for measurement and for a new tune

1. **Tweeter floor: 1.5 kHz, not 1 kHz.** The tweeter is rated from 1.5 kHz. A 1 kHz
   high-pass puts energy below its rating; 1.5 kHz is strictly safer and still shows the
   whole rated band. 24 dB/oct LR at 2.5 kHz (the current tune) is the right permanent
   value.
2. **20 Hz is not safe for ch 7 or ch 8.** The 8" is rated from 110 Hz and the 6.5" from
   140 Hz; neither publishes Xmax, and both are pro midranges with short excursion. A
   full-scale sweep from 20 Hz is heat and mechanical stress with no acoustic output.
   Use `-LowFc 100` for ch 7 and ch 8 too, or 110 / 140 Hz to sit exactly on rating.
3. **100 Hz is a good floor for the 3.5"s** — the owner's own figure, and their Fs is
   150 Hz, so nothing below 100 Hz is usable anyway.
4. **Crossover architecture already matches the hardware:** 3.5" 200 Hz–2.5 kHz, tweeters
   2.5 kHz up, 8"/6.5" below ~1.2 kHz. The only arguable change is pulling the 8" low-pass
   down from 2.5 kHz to ~1.2–1.5 kHz so it stops beaming into the handoff.
5. **Four tweeters + rear fill** are the stage problem, not the EQ: rear tweeters at
   2.5 kHz pull the image back. They need a bigger level cut than the front pair, or a
   higher high-pass.
6. **Level, not EQ, fixes the "bright" and "rear-biased" complaints** — this is exactly
   what `make_tune_v5.py` concluded (tweeters and rear 6.5" running 6–7 dB hot).
7. **The ATOM's gain must be matched to a 2 V pre-out, not cranked.** Its RCA sensitivity
   range is 0.2–6 V. At the 0.2 V end, the DSP's 2 V pre-out overdrives the input 10× and
   the amp clips long before it sounds loud — harsh, not loud. That is a plausible source
   of "bright/harsh" that no EQ can fix.
8. **Turn the ATOM's bass boost off and park its crossovers out of the way.** It has its
   own 12 dB/oct HP (20–12 k) and LP (45–12 k) plus a bass boost, all invisible to the
   DSP. Bass boost must be at 0. If the HP is used at all, put it well below the DSP's
   crossover (~80 Hz) so it acts as a subsonic guard instead of a second filter in the
   tuning band.
9. **80 dB SNR on the ATOM, driving 95–97 dB drivers, means audible hiss at idle.** The
   DSP amp is > 95 dB. That is a hardware limit, not a tuning fault.
10. **The mono midbass is the one thing EQ cannot touch.** If the stage never locks, no
    amount of per-channel EQ will fix a 110 Hz–2.5 kHz band that has no left/right
    information, and no single delay can align a left/right pair at different distances.
11. **The Kicker's low-pass is 12 dB/oct and it is the sub's only crossover.** The 8"
    high-passes at 110 Hz at 24 dB/oct. A 12 dB low-pass against a 24 dB high-pass only
    clears the band if the LP knob sits near 80–100 Hz. Parked at 200 Hz, the sub is still
    −12 dB at 200 Hz and running straight through the midbass. Set it deliberately.
12. **KickEQ at 0, and the sub gain low.** The Kicker can hand the T2 1.8 dB more than it
    is rated for, and the sub already has 12 dB (plus cabin gain) on the mains. A bass
    knob at 3–6 dB @ 40 Hz does nothing useful and masks the midrange by the same amount.
13. **The sub cannot be muted from the app.** It is outside the DSP, so sweep mode's mute,
    master and channel selection do not reach it. Every measurement run needs the Kicker
    physically off, and the app's "sub disconnected" tick box is the only thing preventing
    a contaminated take.

## Still unknown

> **Superseded 2026-10-25.** This list was one of five copies that had drifted apart.
> Every unknown now lives in **`OPEN-QUESTIONS.md`** — one register, ranked by which
> decision each question blocks. Do not add an unknown here; add it there and leave a
> pointer in the document that cares.

## Sources

- DS18 DSP8.6iA product page + spec sheet — 6 × 80 W @ 4 Ω, 130 W @ 2 Ω on ch 5/6 only,
  8-ch DSP, 2 pre-outs, 10 Hz–21 kHz
- NVX XQS3v2 product page — 50 W RMS each, 85 dB, 200–18 k, 4 Ω, Fs 150 Hz, Qts 0.803
- Deaf Bonce / Alphard Apocalypse AP-M81SE — 170 W RMS, 340 W max, 110–10 k, 95.10 dB, 4 Ω
- Orion XTX654 product page — 350 W RMS, 1400 W peak pair, 97 dB, 140–12 k, 4 Ω
- BZRK RS-T150 retail listings — 93 dB, 4 Ω, 1.5 k–25 k, 150 W each, 42 mm cutout
- Rockford Fosgate Power T2D212 page — 1200 W RMS, 83.5 dB, Fs 38.7, Xmax 22.3 mm, 20–250 Hz
- Deaf Bonce ATOM-400.4 PRO spec sheet — 4 × 200 W @ 4 Ω, 4 × 310 W @ 2 Ω, 1 Ω stable,
  HP 20–12 k / LP 45–12 k @ 12 dB/oct, RCA 0.2–6 V, SNR 80 dB, damping 102
- Kicker CXA1800.1 spec sheet — 900 W @ 4 Ω, 1800 W @ 2 Ω and 1 Ω, LP 50–200 Hz 12 dB/oct,
  25 Hz subsonic @ 24 dB/oct (fixed), KickEQ 0–6 dB @ 40 Hz, 125 mV–5 V input, SNR 95 dB
