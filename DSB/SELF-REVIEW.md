# Self-review — grading the work against the work that already existed

2026-10-21. Written after running the two analyses that were missing, and before being
asked a second time. The subject is my own output: the tooling, the two tunes, the docs and
the app brief produced up to this morning. The standard is not "is it well made", it is
**did it use what it had**.

## The grade

| area | grade | why |
|---|---|---|
| Measurement → correction (tune 1) | **A−** | 3.6 dB predicted system residual against the house target, scored as a whole for the first time today; honest constraints (no lift above 12 kHz, lift clamped at +3, Q ≤ 1.4) |
| Verification of writes | **A−** | 42-assertion readback, `doctor.ps1`'s fail-open comparator found and fixed, negative control in the self-test |
| Reasoning about the car as a system | **C+** | The 20–30 dB driver imbalance was found and used for tune 1; the same reasoning was never applied to the tune sitting next to it |
| Tune 2 (the swap alternative) | **D** | Not level-matched to tune 1 (+18 to +29 dB), built from a voicing whose crossover topology differs, and predicted to recover 0.5 dB of the 4.0 dB tune 1 recovers |
| Use of the data already on disk | **D** | 1 of 8 tune files opened, 1 of 32 memory blocks analysed, 4 144 bands unread, 12 distinct tunes unknown until today |
| Documentation and state | **A−** | Dated, reproducible, with the failure modes written down |
| App brief | **B+** | Grounded in the real installs; described the A/B level check that my own tune 2 then violated |
| Judgement about when to call it done | **C** | Declared a "swap-ready two-tune library" on the strength of file integrity, not system plausibility |

**Overall: B−.** Craft is A− territory. Judgement is D+ territory. The gap between those
two is exactly the complaint, and it is fair.

## What was verified, and what that verification did not cover

Every claim made about the tunes was true: the files parse, the device matches them 42/42,
the swap writes landed, the crossovers read back as written, the sessions are dated and
reversible. None of that was ever the weak part.

What was never verified is the only question that matters to a listener: **is this tune
better than what it replaces, on this car?** The tooling answers "is the device what the file
says". Nothing answered "is the file worth putting on the device". I built a very good
transcription service and called it tuning.

## The four checks that were available and not run

All four cost no hardware. All four were possible from the moment the 19:12 measurement
landed, and three of them from the moment the inherited files were in `web\`.

1. **System sum / predicted residual.** `measured − gain_then + gain_tune + eq_tune` sums the
   eight paths into one predicted curve. Two minutes of maths on files I had already parsed.
   It would have printed, before tune 2 was written to the car:
   - flat 7.6 dB residual, tune 1 **3.6 dB**, tune 2 **7.1 dB**;
   - tune 2 is +18.6 to +28.9 dB louder than tune 1 across 50–800 Hz.
   That is the whole finding, and it was computable yesterday at 19:25.

2. **The library atlas.** 32 memory blocks, 4 144 active bands, read in under a second.
   It would have shown that `xswmps` is `bor8f3` and `tinker3` is `ai2`, that the library is
   12 distinct tunes and not 32, that 85 % of the inherited bands are Q ≥ 6 correction
   notches, and — decisive for tune 2 — that **every inherited voicing closes its low passes
   between 1 and 4.2 kHz while ours are open at 20 kHz**. I transplanted bor8f3's curve into a
   topology it was never fitted in. I did not need an hour to find that out; I needed to look.

3. **A topology check before transplanting.** One comparison of the source file's
   `passBand` blocks against the target's. Five minutes. It would have stopped the transplant
   or forced it to be justified.

4. **A level-match gate on the swap pair.** One subtraction of two gain vectors. Five
   minutes. My own app brief already demanded this in §6.2 — *"one sentence of plain English
   ('tune 2 is 16 dB louder on the front 8-inch pair')"* — and I shipped the pair without
   running the check the brief described. That is the sharpest line in this review: the
   brief was better than the person who wrote it.

## The specific failure, stated plainly

Tune 2 was built by copying path levels from a tune made on a car with a different amplifier
gain structure, then smoothing its correction notches into broad shelves. Both steps were
described accurately in the summary at the time. Neither was *reviewed*. The consequence is
that "the two-tune swap library" contained one engineered tune and one that is, to within
1–2 dB at most frequencies (5.6 dB worst case at 200 Hz), **the un-tuned car**. The swap that
happened at 07:31 this morning was therefore not a change of voicing. If the car now sounds
like it did before the tune, that is why, and no amount of re-reading the readback would
have shown it.

The honest summary of the process failure: I treated "the file is correct" as "the tune is
done", because file correctness is measurable and tune quality is not — unless you compute
it, which was the missing step.

## What has been done about it today

- `tune/system-predict.py` — the predicted system response, with provenance checks (it
  refuses to predict through mismatched crossovers), a flat-measured baseline column, energy
  ownership per band, coherent-sum envelope where phase is unknown, and residual against the
  house target. Verifies both tunes against the measurement's crossover map before printing.
- `tune/tune-atlas.py` — all 8 files, all 32 memory blocks, distinct-payload detection,
  crossover-map census, Q/gain/Fc distributions, shape similarity and outlier distance.
- `DSB/PREDICTION.md` — the findings, the numbers, the limits, and the three actions.
- `DSB/ATLAS.md` — the library as it actually is, including the duplicate files and the
  topology mismatch.
- `APP-BRIEF.md` — §6.6 "Prediction" added, and §6.2 given the level-match gate it had only
  described in prose.

## What "best effort" should mean from now on

A tune is not finished when the device matches the file. It is finished when:

1. its **predicted system residual** is printed next to the target, and
2. it is **level-matched** to whatever it is meant to be compared with, and
3. its **crossover topology** matches the system it was fitted for, and
4. one **measurement** has confirmed or refuted the prediction.

Tune 1 meets 1, 3 and (pending) 4. Tune 2 meets none of them. That sentence is the review.

## Decision this forces

Tune 2 should not be presented as a voicing. Either it is rebased onto tune 1's path gains —
keeping its smoothed shape, comparing like with like — or it is relabelled as "stock as
measured" and tune 1 stands alone until a second real voicing exists. The prediction also
supplies the missing argument for the open-vs-closed crossover decision: with the cones open,
tune 1 overshoots the target by +5…+7 dB at 1.25–2 kHz, and every professional tune in the
library closes those low passes. Restoring real handovers is now evidence-backed rather than a
matter of taste.

## Still not done, and not hidden

- No post-tune measurement of anything. Every number above is prediction.
- Time alignment still blocked on the 3.5 mm → RCA cable; unchanged, and it is the smaller
  problem — 1–2 ms of arrival error against a 20 dB level error.
- `/api/dspcfg` (command 15) has never been dumped; needs the DSP powered.
- REW is installed and unused; WinISD is installed and unused (excursion/thermal check of
  tune 2's bass is moot until tune 2 is rebased).
- The sub, outside the DSP, is absent from every prediction below 50 Hz.
