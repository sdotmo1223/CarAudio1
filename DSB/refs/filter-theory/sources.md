# Filter theory — verified sources on slope, filter type and polarity

Digested 2026-10-23. Every quote below was read from the primary page. Status per claim is
marked **VERIFIED** or **UNVERIFIED** — an unverified item is recorded because it is believed
relevant, not because it is established.

Why this exists: this project hard-codes **LR24 on every path** (`TUNE-PLAYBOOK.md` §6) and the
hardware also offers 12 and 18 dB/oct. Nobody had written down what the slope byte and the filter
type byte are actually *supposed* to do acoustically, or what polarity each one implies.

Derived rule: **§6.1 of TUNE-PLAYBOOK.md.**

---

## 1. Linkwitz-Riley is an even-order family, and the polarity rule depends on the order

**VERIFIED.** Rane Note 160, *"Linkwitz-Riley Crossovers: A Primer"*, Dennis Bohn, October 2005.
<https://www.ranecommercial.com/legacy/note160.html>

> "cascading two 1st-order circuits produces a 2nd-order Linkwitz-Riley (LR-2); cascading two
> 2nd-order Butterworth filters creates a LR-4 design; cascading two 3rd-order Butterworth
> filters gives a LR-6, and so on. **(Starting with LR-2, every other solution requires inverting
> one output. That is, LR-2 and LR-6 need inverting, while LR-4 and LR-8 do not.)**"

| slope | LR order | one output must be inverted? |
|---|---|---|
| 12 dB/oct | LR-2 | **yes** |
| 24 dB/oct | LR-4 | **no** |
| 36 dB/oct | LR-6 | **yes** |
| 48 dB/oct | LR-8 | **no** |

**Applied to this car:** the map is LR24 (§6) = LR-4, which is the no-inversion case. The current
configuration is internally coherent and needs no polarity flip. That is a relief, not a finding —
see §3 below for the case that is *not* coherent.

---

## 2. 18 dB/octave is not a Linkwitz-Riley alignment at all

**VERIFIED.** Same Rane note, on the phase diagram:

> "The pattern shown is for the popular **18 dB/octave Butterworth all-pass design**…"

Corroborated independently by Rod Elliott (ESP Project 253, <https://sound-au.com/project253.htm>):

> "A Linkwitz-Riley filter (**always even-order**) has both outputs at -6dB at the crossover
> frequency."

And by a modern DSP manual that keeps the two families apart in its own menu — the miniDSP Flex
Eight lists **Butterworth at 18 dB/oct** and **Linkwitz-Riley at 12, 24 and 48 dB/oct**
(<https://docs.minidsp.com/product-manuals/flex-eight/dsp-reference/crossover.html>).

**Consequence:** an 18 dB/oct slope belongs to the Butterworth / all-pass family. Labelling it
"Linkwitz-Riley" is a contradiction in terms, and the LR polarity table above does not apply to it.

---

## 3. CORRECTION — two sources do NOT disagree about 18 dB/oct polarity

A claim circulated on 2026-10-23 that Linkwitz and Elliott contradict each other on whether an
18 dB/oct crossover needs a polarity inversion, and that this made the choice "unresolvable, so
measure it." **That is a misattribution, and the disagreement does not exist.**

**VERIFIED.** Linkwitz Lab, *Crossovers*, <https://www.linkwitzlab.com/crossovers.htm>. The quoted
passage —

> "HP and LP are 2 x 90 degrees out of phase at all frequencies and the polarity of one driver must
> be reversed to obtain allpass behavior."

— sits under Linkwitz's section **"2nd order Linkwitz-Riley filter (12 dB/oct, LR2)"**. It is a
statement about **LR-2 / 12 dB/oct**, not about 18 dB/oct. Linkwitz separately describes the
**LR-4 / 24 dB/oct** outputs as in phase, needing no inversion. He never says what was attributed
to him.

Elliott's side is real but is a *measurement on specific drivers*, not a filter rule:

> "Unlike even-order crossovers, odd-order versions do not require a signal inversion (aka 180°
> phase shift) for one of the outputs…"
>
> "For reasons that aren't entirely clear, the 18dB xover didn't require polarity inversion, and
> the response across the xover region was as flat as one can expect from any pair of loudspeaker
> drivers."

**Read Elliott's caveat before quoting him.** His test rig had **offset acoustic centres**, and he
says the result depends on the drivers and that offset. His 24 dB/oct test *did* need an inverting
buffer — but because of his particular geometry, not because of the filter order. **Acoustic
centre offset can override the textbook order rule.** That is the genuine lesson here, and it is
the opposite of "two authorities disagree, so it's unknowable."

---

## 4. UNVERIFIED — Audiofrog on delay plus polarity inversion

Believed relevant, **not confirmed from the primary source.** A claim circulated that Andy
Wehmeyer's *Time Alignment Part 2* states: "Never use delay and reverse the polarity of one of the
mids at the same time… reversing the polarity with delay just screws things up."

Attempts to read <https://www.audiofrog.com/time-alignment-part-2/> on 2026-10-23 returned only
teaser text — the site did not expose the article body to a text fetcher. The quote is therefore
**not verified** and must not be relied on until someone reads the article directly.

If it holds, it is directly actionable here: this car carries **3.8–6.0 ms of delay on every
channel** (§7), so a polarity flip stacked on top of that would be exactly the mistake described.

Note the article that *was* reachable: Andy Wehmeyer, *"Crossover Slopes and Protection for
Speakers: Does a Steeper Filter Provide More Protection?"* (7 October 2021), which opens *"I
usually recommend 24dB/octave Linkwitz Riley"* — consistent with this project's existing map. Body
not retrievable either; worth reading by hand.

---

## 5. Delay sanity check (arithmetic, not sourced)

Speed of sound ~0.343 m/ms at 20 °C, so `ms × 0.343 = metres of path`.

- This car: 3.8–6.0 ms → **1.3–2.1 m** of path.
- The hardware cap of 25 ms → 8.6 m, so **the cap can never bind inside a cabin.**
- A delay value implying several metres of extra path is a bug or a unit error, not a tuning choice.

**Caveat that must travel with this:** §7's delays are *relative* — they came from measured
distances with the sub as a 250 cm reference — so the metres figure is extra path relative to that
reference, not absolute distance from the driver's ear. Do not compare it against a tape measure
without subtracting the reference first.

---

## 6. Two gaps this run exposed

- **`DS18_DSP_protocol.md` does not exist in this project.** It is *referenced* by
  `verify-web.ps1` ("DS18_DSP_protocol.md section 37") but there is no such file anywhere in the
  tree — no file matching `*protocol*` or `*DS18*` except `src/Protocol.cs`, and no markdown with
  a `## 37` or `## 38` section. Either it was never committed or it lives outside the repo. The
  slope byte values (1/2/3 = 12/18/24) are documented only in `crossover-range.ps1`'s header
  comment. **Finding the real vendor protocol document is worth doing** — it is the only place an
  undocumented cmd-11 readback could be hiding (see `README.md` gap 2).
- **`iya1m4.idsp` is not in this library.** A claim on 2026-10-23 described that file's delay
  contents. This project holds twelve `.idsp` files and that is not one of them, so whatever was
  measured there was measured against a different project. Do not merge its conclusions here
  without re-deriving them.
- **`AP.cs` does not exist here either.** A task on 2026-10-23 said to check the vendor app's
  slope/filterType dropdowns in `AP.cs`. There is no such file in this tree; if it exists it is in
  the vendor's own source, not ours.

---

## 7. Repo sweep, 2026-10-23 — what the library actually carries

All 12 `.idsp` (11 in `web\`, 1 in `banks\`), outputs 0–7, both `passBand` slots each. The
crossover fields live inside `<passBand index="N">` as `Fc` / `hiLowType` (0 = HP, 1 = LP) /
`Slope` / `filterType` / `butt_Type` / `Enable`. **"Open" low-pass = `Fc 20000` with
`Enable 0`** — that is how §6's "LP open on every path" is encoded.

**Slope and filter-type census:**

| slot | slope | filterType | Enable | count | |
|---|---|---|---|---|---|
| HP | 3 | 1 | 1 | **96** | every high-pass in the library is LR24, no exceptions |
| LP | 3 | 1 | 0 | 47 | LR24, disabled (open) — our five tunes |
| LP | 3 | 1 | 1 | 26 | LR24, active — the inherited voicings |
| LP | 2 | 1 | 0 | 7 | **18 dB/oct labelled Linkwitz-Riley** — but all disabled |
| LP | 1 | 1 | 0 | 14 | 12 dB/oct, disabled |
| LP | 1 | 1 | 1 | **2** | **12 dB/oct ACTIVE — see finding A** |

**Finding A — `ai2.idsp` carries two active LR-2 low-passes with no polarity inversion.**
Paths 6 (8" front) and 7 (6.5" rear) are low-passed at **2500 Hz, slope 1 (12 dB/oct),
filterType 1 (Linkwitz-Riley), Enable 1**, and both carry `revert 0`. Per Rane, **LR-2 is the
every-other case that *requires* one output to be inverted** — and neither is. That is a real
acoustic defect in that file: an LR-2 handover with both sides in normal polarity sums to a
**−6 dB null at the crossover**. `ai2` is inherited and is **not** the playing tune, so nothing is
currently broken in the car — but it must never be loaded as-is, and it explains why the inherited
voicings may have sounded disconnected at 2.5 kHz.

**Finding B — the firmware does not gate filterType 1 to even slopes.** Seven paths across seven
inherited files carry **slope 2 (18 dB/oct) with filterType 1 (Linkwitz-Riley)** together —
output 0 of `bor8f3-flat4`, `ai2`, `bor8f3`, `cb0nyk`, `tinker3`, `w6784b`, `xswmps`. Every one
of them is the LP slot at `Fc 20000` with `Enable 0`, so **none of them is audible** and the
incoherence is harmless in practice. It is still proof that the combination can be written: an
18 dB/oct slope wearing an LR label is accepted by the hardware, and if anyone ever enables one,
the LR polarity table does not describe it (§6.1).

**Finding C — the original voicing author band-limited the 6.5" at 1000 Hz. Six of the seven
independent inherited tunes (`bor8f3-flat4`, `bor8f3`, `cb0nyk`, `tinker3`, `w6784b`, `xswmps`)
low-pass path 7 at exactly 1000 Hz** (`ai2` uses 2500). §9.0.3's roughness analysis, derived
blindly from the canonical measurement, put that driver's breakup onset at **905 Hz**. The
original author's number and an independent measurement agree to within a sixth of an octave.
That is the strongest corroboration yet that §9.0.3's "close the cone low-passes" recommendation
is right, and that **our own map — LP open to 20 kHz on every path — is the anomaly**, not the
inherited voicings.

**Finding D — no output is inverted anywhere.** All 12 files, all 8 outputs: `revert 0`. So the
"delay plus a polarity flip" trap is **not currently present** in any file in this library. Worth
recording as a checked-and-clear rather than leaving it assumed.
