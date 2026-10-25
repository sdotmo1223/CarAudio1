#!/usr/bin/env python3
"""
Build a conservative TEST tune from the system measurement.

Purpose: prove the round-trip on real hardware - does the DSP actually accept
a file we generated? It is NOT a final tune. The source measurement is an
ungated RTA of the whole system, so it cannot support a real per-driver tune.

Safety choices:
  * cuts only (no boost at all)
  * no cut deeper than TEST_MAX_CUT
  * Q capped at TEST_MAX_Q (no surgical notches)
  * each channel is fitted only inside its own crossover passband
  * the region above ~12 kHz is excluded: the RTA's steep HF rise there is
    almost certainly the microphone, not the car, and "correcting" it would
    just make the system dull
  * crossovers, delays, gains, routing and other memory slots untouched
  * writes to ONE memory slot only

Channel map (DSP 1-indexed -> xml output index 0-indexed):
  1-4 tweeters      -> outputs 0..3
  5-6 mid 3.5"      -> outputs 4..5
  7-8 full-range    -> outputs 6..7
"""
import glob

import numpy as np

import idsp
import peq
from tune import read_measurement

BASE = '2027-base.idsp'
OUT = 'test/test-tune-memory1.idsp'
MEMORY_INDEX = '1'          # the slot RunStatus/mainMemory points at

TEST_MAX_CUT = 6.0          # dB
TEST_MAX_Q = 6.0
TEST_BANDS_PER_CH = 8
SMOOTH = 3.0                # windows per octave
BAND_LO, BAND_HI = 50.0, 12000.0   # only fit where we trust the measurement
MIN_KEEP = 0.3              # dB, drop bands smaller than this

CHANNEL_MAP = {0: 'tweeter', 1: 'tweeter', 2: 'tweeter', 3: 'tweeter',
               4: 'mid 3.5"', 5: 'mid 3.5"',
               6: 'full-range', 7: 'full-range'}


def passband(o):
    """Return (high_pass_fc, low_pass_fc) in Hz, or None where disabled."""
    hp = lp = None
    for pb in o.findall('passBand'):
        if pb.findtext('Enable') != '1':
            continue
        fc = float(pb.findtext('Fc'))
        if pb.findtext('hiLowType') == '0':
            hp = fc
        else:
            lp = fc
    return hp, lp


def main():
    # ---------------------------------------------------------- measurement
    path = glob.glob('measurements/csv/measurement_1_*.csv')[0]
    f, spl = read_measurement(path)
    sm = peq.smooth_log(f, spl, SMOOTH)

    g = np.logspace(np.log10(30.0), np.log10(20000.0), 250)
    sg = np.interp(np.log10(g), np.log10(f), sm)

    mid = (g >= 300) & (g <= 3000)
    level = float(np.median(sg[mid]))
    corr = peq.target_tilt(g, 1000.0, -0.5, level) - sg

    peq.G_MIN = -TEST_MAX_CUT          # limit cuts
    peq.Q_MAX = TEST_MAX_Q             # no surgical notches

    print(f'measurement : {path}')
    print(f'target level: {level:.1f} dB (midband median); fits limited to '
          f'{BAND_LO:.0f}-{BAND_HI:.0f} Hz, cuts only, max {TEST_MAX_CUT:g} dB, '
          f'Q <= {TEST_MAX_Q:g}')
    print()

    # ------------------------------------------------- per-channel fitting
    root = idsp.load(BASE)
    mem = [m for m in idsp.memories(root) if m.get('index') == MEMORY_INDEX][0]

    print(f'{"out":>3} {"role":<11} {"HP":>7} {"LP":>7}   bands applied')
    total = 0
    for idx in sorted(CHANNEL_MAP):
        o = idsp.output(mem, idx)
        hp, lp = passband(o)
        lo = max(BAND_LO, hp * 1.05) if hp else BAND_LO
        hi = min(BAND_HI, lp * 0.95) if lp else BAND_HI
        keep = (g >= lo) & (g <= hi)

        sel = []
        if keep.sum() >= 8:
            filt, _ = peq.fit_peq(g[keep], corr[keep],
                                  n_filters=TEST_BANDS_PER_CH,
                                  weights=np.ones(int(keep.sum())),
                                  max_boost_db=0.0, min_gain_db=0.0)
            sel = [(a, b, q) for a, b, q in filt if abs(b) >= MIN_KEEP]

        idsp.set_eq(o, sel)
        total += len(sel)
        shown = ', '.join(f'{a:.0f}Hz/{b:+.1f}dB/Q{q:.1f}' for a, b, q in sel) or '-'
        print(f'{idx:>3} {CHANNEL_MAP[idx]:<11} {f"{hp:g}" if hp else "-":>7} '
              f'{f"{lp:g}" if lp else "-":>7}   {shown}')

    idsp.save(root, OUT)
    print()
    print(f'{total} band writes across {len(CHANNEL_MAP)} channels')
    print(f'wrote {OUT} (memory slot {MEMORY_INDEX})')

    # ------------------------------------------------------------- verify
    check = idsp.load(OUT)
    assert idsp.dumps(check) == open(OUT, 'rb').read(), 'format round-trip broke'
    print('verify: output round-trips byte-exact, crossovers untouched')


if __name__ == '__main__':
    main()
