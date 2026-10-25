# The "B" tune - structural review and rebuild

Source: `web/bor8f3.idsp`, memory 0 (its `RunStatus` says memory 1 on the device).
Built: `web/2-bor8f3-improved.idsp` by `tune/build-swap-tunes.py`.
Checked: `tune/verify-swap-tunes.py` (46 assertions, 0 failures).
Numbers: `tune/report-tune.py web/bor8f3.idsp`.

Everything below is arithmetic on the file itself. The file is plain XML and the bands are
redbook peaking filters at 48 kHz, so the response can be computed without the car - see
`tune/idsp.py`. Nothing here is a listening impression dressed up as fact.

## 1. What is in the file

| output | driver | bands | median Q | bands pinned at +3.0 | bands at or below -6 | realized ripple 200-8000 Hz |
|---|---|---|---|---|---|---|
| 1-2 | tweeters (front) | 27 | 15.2 | 3 | 4 | 6.6 dB |
| 3-4 | tweeters (rear) | 29 | 15.2 | 6 | 3-5 | 7.8-9.1 dB |
| 5-6 | 3.5" pairs | 30 | 13.6 | 8-10 | 4-5 | 15.2-17.5 dB |
| 7 | 8" pair | 31 | 9.9 | 10 | 4 | 14.0 dB |
| 8 | 6.5" pair | 31 | 16.1 | 10 | 0 | 11.5 dB |

Four memories in the file, all four different - it is a bank of four tunes, not one tune
repeated. Outputs 9-17 are flat at -3.0 dB (unpopulated). Crossovers in the file are
HP 2500 LR24 (tweeters), 250-2500 (3.5"), 110-2500 (8"), 140-1000 (6.5") - these are the map
the car had **before** the owner's 1500/80/50/60 change, and they match
`evidence/crossover-snapshot-mem2-20261020-191029.json` exactly. So this file was what was
playing in the car before the measurement work started.

## 2. What is structurally wrong with it

**a. The Q is narrower than the band spacing.** Median Q 9.9-16.1 means each filter is
1/10 to 1/16 of an octave wide, while the bands themselves sit ~1/10 octave apart. Filters
that narrow do not overlap, so they cannot sum into a curve: each band produces its own
spike and the spaces between them keep the original response. The file *displays* a smooth
target; the hardware renders a sawtooth. Measured on the file's own filters: 15.2-17.5 dB
peak-to-peak inside 200-8000 Hz on the 3.5" outputs.

**b. The gains are saturated on a clamp.** +3.0 dB appears 3-10 times per output, and runs of
three identical -6.0 dB slots stand in for one shelf (e.g. 11972/12789/13661/14593 Hz on the
tweeters, all -6.0/-7.3/-6.0). This is the signature of an automatic fitter that hit its own
range limit and then added more bands at that limit rather than accepting the error.

**c. Adjacent bands contradict each other.** Output 5 around 1 kHz: 790:+3.0, 850:+3.0,
914:+3.0, 984:-6.1, 1059:-1.1, 1140:+3.0. Four bands within a fifth of an octave fighting
opposite ways. The listener hears the residue, not the intent.

**d. Consumption.** 27-31 of 31 slots used per output. There is no room left to fix anything
later, and no way to tell which band was meant to do what.

None of this is a criticism of whoever produced it - the fitter was told to hit a measured
curve at 31 fixed frequencies and it did. The result is a graphic EQ in a parametric's
clothing.

## 3. How the mids actually sit

Band averages of the summed EQ, in dB (from `tune/report-tune.py`):

| output | low mid 120-300 | mid 300-1k | presence 1-3k | treble 3-6k |
|---|---|---|---|---|
| 5 (3.5" front) | +0.8 | -0.5 | -1.7 | 0.0 |
| 6 (3.5" rear) | +0.6 | -1.5 | -2.1 | 0.0 |
| 7 (8") | +0.2 | -1.4 | -0.9 | 0.0 |
| 8 (6.5") | -3.5 | +1.3 | +0.1 | 0.0 |

So the voicing is: **a small lift at 250-400 Hz, then a scoop through the presence range,
deepest on the 3.5" pair that carries most of the midrange.** That is a warm, thick,
back-of-the-room mid - the classic "smooth" car-audio presentation, and it is deliberate
enough to be recognised as a style.

The extreme point of it: both 3.5" outputs carry **-11.0 and -9.6 dB at ~1.5 kHz**. Both
pairs, identical filters, on drivers that are physically different. That is one measurement
copied to both outputs, not two drivers agreeing - and at 1.5 kHz, a 1/13-octave, -11 dB
notch is far more likely to be a null in the measurement position (door panel, floor,
seat bolster) than a resonance. It is the single most audible defect in the file.

## 4. What tune 2 changes, and what it deliberately does not

Rebuilt from the same curve: smooth the file's own response over 0.4 octave (that is the
intent behind 1/15-octave filters), then fit it with filters no narrower than Q 2 (= one
octave wide), minimum 1 dB of correction.

| output | bands before -> after | worst 1/1-octave change vs bor8f3 | negative bands |
|---|---|---|---|
| 1-2 | 27 -> 6 | 0.8 dB | -4.1 @ 14.1 kHz |
| 3 | 29 -> 4 | 1.2 dB | -4.7 @ 13.7 kHz |
| 4 | 29 -> 4 | 0.4 dB | -3.9 @ 14.3 kHz |
| 5 | 30 -> 4 | 0.8 dB | -5.5 @ 1.57 kHz |
| 6 | 30 -> 2 | 0.9 dB | -6.3 @ 1.52 kHz |
| 7 | 31 -> 8 | 1.1 dB | -3.8 @ 688, -3.4 @ 133 Hz |
| 8 | 31 -> 7 | 0.4 dB | -6.3 @ 196 Hz |

**Kept:** the tonal balance (every 1/1-octave band within 1.2 dB of the original), the
levels, the delays, and the 250-400 Hz warmth.

**Changed on purpose:** the -11 dB razor notch at 1.5 kHz becomes a -5 to -6 dB scoop. This
is the one deliberate tonal difference - it is the difference between a null being corrected
and a null being carved into the tune. To get the notch back:
`python tune/build-swap-tunes.py --keep-notches`.

**Not changed:** levels. bor8f3 asks for the 8" at -14.0 and the 6.5" at -5.0; last night's
measurement asked for -30.0 and -22.1. Tune 2 keeps bor8f3's numbers because they are part of
its presentation - so on the ride it will be **16-17 dB bassier than tune 1**. That is the
tune's balance, not a build error. If it is too much, change the two gain lines and nothing
else; see §6 of `TUNE-PLAYBOOK.md` for `apply-tune.ps1`, or edit path 6/7 gain in the file.

**Crossovers in the file** are the car's current map (1500/80/50/60, low-passes open, LR24),
not bor8f3's. The Tune tab never writes crossovers - it only writes EQ and, if you tick the
output box, levels and delays - so the file must agree with the car or it would be lying.
