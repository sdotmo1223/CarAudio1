# DS18 `.idsp` ← REW workflow

Converts **REW** EQ filter exports into a loadable **DS18** `.idsp` preset, so
~30 parametric bands per channel don't have to be typed in by hand.

## Files

| file | purpose |
|---|---|
| `idsp.py` | byte-exact reader/writer for `.idsp` |
| `rew_to_idsp.py` | CLI: apply REW filters to one or more output channels |
| `2027-base.idsp` | the reference tune (kept unmodified as a template) |
| `mdat2csv.py` | extract freq/phase/IR from REW `.mdat` files |
| `test/` | throwaway test fixtures |

## Reading REW `.mdat` files

REW saves measurements as `.mdat` — **Java-serialized binary** ("REW Measurement
Data File V2"), not text. `mdat2csv.py` (vendored from `pierreaubert/autoeq`)
parses it and exports per-measurement CSV (freq / SPL / phase) plus the impulse
response, which is what time alignment needs. Standard library only.

```bash
python3 mdat2csv.py file.mdat out_dir
```

So the user does **not** have to hand-export each measurement as text — the
native `.mdat` works, provided it reaches us.

## File transport

The user's chat only sends photos. Uploads work, but not every host does:

| host | status |
|---|---|
| WeTransfer | ✅ worked — API v4 `direct_link` |
| Dropbox (`?dl=1`), 0x0.st, catbox.moe | should work |
| OneDrive (`1drv.ms`) | ❌ 403/401 — download is JS/session driven |
| LimeWire | ❌ end-to-end encrypted, key in URL fragment |


## The `.idsp` format (reverse-engineered from `2027.idsp`)

Plain XML — **not** binary. CRLF line endings, 2-space indent, no trailing
newline, no self-closing tags, one comment.

```
<DP12_Param>
  <version>1</version>
  <RunStatus> mainSource / mainMemory / mainVolume
  <UserSourceMemPriority>
  <MP6X_MainParam>
  <memory index="0..3">          ← 4 preset slots
    <output index="0..16">       ← 17 channels per slot
      <mute> <gain> <revert> <delay> <linkPair>
      <passBand index="0|1">     ← crossover (low-pass / high-pass)
        <Fc> <hiLowType> <Slope> <filterType> <butt_Type> <Enable>
      <eq index="0..30">         ← 31 peaking bands
        <Fc> <Gain> <Q>
  <source index="0..5">          ← input routing matrix
    <mix_by_out_in index="in,out"> <enable> <gain>
```

Facts worth remembering:

* EQ bands are **peaking only** — there is no filter-type field, no shelf.
* `delay` is in **ms**; `gain` in **dB**.
* The reference tune has channels 0–7 actively tuned; 8–16 look idle
  (`delay=0`, `gain=-3`, `linkPair=1`, no active EQ). Memory index 1 is the
  active slot (`RunStatus/mainMemory`).
* Round-trip is verified byte-identical (`load` → `dumps` == original sha1).

## Usage

Single channel:

```bash
python3 rew_to_idsp.py base.idsp out.idsp \
    --memory 1 --output 0 --rew left-tweeter.txt
```

Several channels in one pass (`--set OUT=FILE`, output index = channel):

```bash
python3 rew_to_idsp.py base.idsp out.idsp --memory 1 \
    --set 0=left-tweet.txt --set 1=left-mid.txt --set 6=sub.txt
```

`--memory all` writes every slot. `--dry-run` reports without writing.
`--keep-unused` preserves bands beyond the supplied filters (default is to
zero them so an old tune can't leak through).

REW side: EQ window → **Export filter settings as text** → **Generic**
(Equalizer APO) format:

```
Filter  1: ON  PK  Fc 1258.0 Hz  Gain -5.8 dB  Q 6.600
```

## Known limits

* Low/high **shelves** (REW's `LS`/`HS`, common on the low end) are skipped and
  reported — the DSP stores peaking bands only. A shelf can be approximated
  with 2–3 peaking filters if needed.
* Number formatting is trimmed to the DSP software's own resolution
  (Fc integer, Gain/Q 1 decimal) by default; adjustable via CLI flags.

## Fitting a tune from a measurement (`tune.py`)

The other direction: instead of consuming a *finished* REW filter list, consume
the **measurement** and work out the filters ourselves.

```bash
python3 tune.py measurement.txt --bands 15 --tilt -0.5 --max-boost 3 \
    --out bands.txt --idsp 2027-base.idsp --memory 1 --output 0
```

* `peq.py` — RBJ peaking-biquad magnitude response + a matching-pursuit fitter
  (`fit_peq`): each band is least-squares fitted to the remaining error, then
  all bands are refined jointly, then dead bands are dropped.
* `tune.py` — CLI: REW measurement export -> target curve -> bands -> optional
  `.idsp` (via `idsp.set_eq`).

Design choices that matter:

* **Smooth before fitting** (default 6 windows/octave) so it corrects audible
  trends rather than measurement noise.
* **Boost is capped** (`--max-boost`, default +3 dB; use `0` for cuts-only).
  Boosting narrow dips burns headroom and usually sounds worse.
* Weighting de-emphasises below 40 Hz and above 12 kHz.

**Not yet validated against the real DSP.** The fitter assumes the standard RBJ
peaking biquad; the DSP's actual coefficients aren't published. Before trusting
a generated tune, do a *calibration measurement*: set one known filter in the
DSP, measure it with REW, and compare with `peq.peq_response`. If they disagree,
the model (Q definition / gain scaling) needs correcting first.

