#!/usr/bin/env python3
"""
v6 - the full run.

Builds on v5 (corrected crossovers + matched output levels) and re-voices the
EQ more assertively for the user's actual listening: R&B and hip-hop.

Deeper bass shelf, smoother top end than the v3/v4 curve:

    Hz :  20   35   55   80  120  180  300  500  800  1k   2k   3.5k  6k   10k
    dB : +8   +8  +6.5 +5  +3.5 +2.2 +1  +0.3 -0.3 -0.6 -1.8 -3.0  -4.2 -5.2

Cuts only, never deepens a dip, per-group fits inside each corrected passband.

TIME ALIGNMENT IS DELIBERATELY NOT TOUCHED. See the reply: the existing delays
imply up to 2.3 m of path difference, which exceeds the cabin length of a 2017
Continental, and they are mutually inconsistent across the tweeter and mid
groups. Guessing new ones would be worse than leaving them.
"""
import glob

import numpy as np

import idsp
import peq
from tune import read_measurement

SRC = '2027-test-tune-v5.idsp'
OUT = '2027-test-tune-v6.idsp'
MEASUREMENT_GLOB = 'measurements/csv/measurement_1_*.csv'
MEMORY_INDEX = '1'

MAX_CUT = 9.0
MAX_Q = 4.0
BANDS = 7
SMOOTH = 3.0
BAND_LO, BAND_HI = 40.0, 16000.0

RNB_POINTS = [
    (20, +8.0), (35, +8.0), (55, +6.5), (80, +5.0), (120, +3.5),
    (180, +2.2), (300, +1.0), (500, +0.3), (800, -0.3), (1000, -0.6),
    (2000, -1.8), (3500, -3.0), (6000, -4.2), (10000, -5.2), (20000, -6.0),
]

GROUPS = [
    ('ch1-4 tweeters', [0, 1, 2, 3], 1.5, 5.0),
    ('ch5 front 3.5" + ch6 rear 3.5"', [4, 5], MAX_Q, MAX_CUT),
    ('ch7 front 8"   + ch8 rear 6.5"', [6, 7], MAX_Q, MAX_CUT),
]


def rnb_shape(f):
    fx = np.array([p[0] for p in RNB_POINTS], float)
    fy = np.array([p[1] for p in RNB_POINTS], float)
    return np.interp(np.log10(np.asarray(f, float)), np.log10(fx), fy)


def passband(o):
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
    f, spl = read_measurement(glob.glob(MEASUREMENT_GLOB)[0])
    sm = peq.smooth_log(f, spl, SMOOTH)
    g = np.logspace(np.log10(30.0), np.log10(20000.0), 250)
    sg = np.interp(np.log10(g), np.log10(f), sm)

    mid = (g >= 300) & (g <= 3000)
    level = float(np.median(sg[mid]) - np.median(rnb_shape(g[mid])))
    corr = (level + rnb_shape(g)) - sg

    root = idsp.load(SRC)
    mem = [m for m in idsp.memories(root) if m.get('index') == MEMORY_INDEX][0]

    for label, chans, qmax, cutmax in GROUPS:
        bands = [passband(idsp.output(mem, c)) for c in chans]
        lo = max([BAND_LO] + [h for h, _ in bands if h])
        hi = min([BAND_HI] + [l for _, l in bands if l])
        keep = (g >= lo * 1.05) & (g <= hi * 0.95)
        cc = np.minimum(corr[keep], 0.0)
        sel = []
        if (cc < -0.5).sum() >= 5:
            peq.F_MIN, peq.F_MAX = lo, hi
            peq.Q_MAX = qmax
            peq.G_MIN = -cutmax
            filt, _ = peq.fit_peq(g[keep], cc, n_filters=BANDS,
                                  weights=np.ones_like(cc), max_boost_db=0.0)
            sel = [(a, b, q) for a, b, q in filt if abs(b) >= 0.5]
        for c in chans:
            idsp.set_eq(idsp.output(mem, c), sel)
        print(f'{label:<32} {lo:>4.0f}-{hi:<5.0f} Hz  '
              + (', '.join(f'{a:.0f}Hz {b:+.1f}/Q{q:.1f}' for a, b, q in sel) or 'flat'))

    idsp.save(root, OUT)
    assert idsp.dumps(idsp.load(OUT)) == open(OUT, 'rb').read()
    print(f'\nwrote {OUT} (round-trips byte-exact)')


if __name__ == '__main__':
    main()
