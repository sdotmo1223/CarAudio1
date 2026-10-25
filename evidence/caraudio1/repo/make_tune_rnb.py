#!/usr/bin/env python3
"""
Test tune v3 - R&B / Hip-hop house curve.

Why v3 exists: the user reported v2 as "vocals good, bright", and listens
mostly to R&B and hip-hop. The v2 target was a neutral -0.5 dB/oct tilt with no
bass lift and no treble rolloff, so it preserved the system's top-end rise.

This target instead:
  * lifts the bass shelf (relative reference sits at the bass, everything else
    is trimmed to it - cuts only, so no headroom is burned)
  * rolls the treble off gently so cymbals/sibilance stop being the loudest
    thing in the car

Channel map / pairing identical to v2.

Still cuts only, still one summed ungated RTA, crossovers/delays/gains/routing
and other memory slots untouched.
"""
import glob

import numpy as np

import idsp
import peq
from tune import read_measurement

BASE = '2027-base.idsp'
OUT = '2027-test-tune-v3.idsp'
MEMORY_INDEX = '1'

MAX_CUT = 9.0
MAX_Q = 4.0
BANDS = 7
SMOOTH = 3.0
BAND_LO, BAND_HI = 40.0, 16000.0

# R&B / hip-hop car target, dB relative to the reference at 1 kHz.
# Bass shelf, gentle mid tilt, treble rolloff.
RNB_POINTS = [
    (20, +6.0), (35, +6.0), (55, +5.0), (80, +4.0), (120, +3.0),
    (180, +2.0), (300, +1.0), (500, +0.3), (800, -0.2), (1000, -0.5),
    (2000, -1.5), (3500, -2.5), (6000, -3.5), (10000, -4.5), (20000, -5.0),
]

TWEETERS = [0, 1, 2, 3]
PAIRS = [
    ('ch5 front 3.5" + ch6 rear 3.5"', [4, 5]),
    ('ch7 front 8"   + ch8 rear 6.5"', [6, 7]),
]
ROLE = {0: 'tweeter', 1: 'tweeter', 2: 'tweeter', 3: 'tweeter',
        4: 'front 3.5"', 5: 'rear 3.5"', 6: 'front 8"', 7: 'rear 6.5"'}


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
    path = glob.glob('measurements/csv/measurement_1_*.csv')[0]
    f, spl = read_measurement(path)
    sm = peq.smooth_log(f, spl, SMOOTH)

    g = np.logspace(np.log10(30.0), np.log10(20000.0), 250)
    sg = np.interp(np.log10(g), np.log10(f), sm)

    # Align the shaped target to the system's own midband level.
    mid = (g >= 300) & (g <= 3000)
    level = float(np.median(sg[mid]) - np.median(rnb_shape(g[mid])))
    target = level + rnb_shape(g)
    corr = target - sg

    peq.G_MIN = -MAX_CUT
    peq.Q_MAX = MAX_Q

    print('R&B / hip-hop target (dB relative to 1 kHz):')
    for hz in [30, 60, 100, 200, 500, 1000, 2000, 4000, 8000, 12000]:
        print(f'   {hz:>6} Hz  {rnb_shape(np.array([hz]))[0]:+5.1f}')
    print(f'\nmidband reference {np.median(sg[mid]):.1f} dB, '
          f'cuts only, max {MAX_CUT:g} dB, Q <= {MAX_Q:g}\n')

    root = idsp.load(BASE)
    mem = [m for m in idsp.memories(root) if m.get('index') == MEMORY_INDEX][0]

    # ---- every driver group gets its own fit inside its own passband -------
    # Tweeters get broader, shallower limits: a treble rolloff should be a
    # gentle shelf, not a pair of narrow notches at 13 kHz.
    groups = [('ch1-4 tweeters', TWEETERS, 1.5, 5.0),
              ('ch5 front 3.5" + ch6 rear 3.5"', [4, 5], MAX_Q, MAX_CUT),
              ('ch7 front 8"   + ch8 rear 6.5"', [6, 7], MAX_Q, MAX_CUT)]
    for label, chans, qmax, cutmax in groups:
        bands = [passband(idsp.output(mem, c)) for c in chans]
        lo = max([BAND_LO] + [h for h, _ in bands if h])
        hi = min([BAND_HI] + [l for _, l in bands if l])
        keep = (g >= lo * 1.05) & (g <= hi * 0.95)

        # Cuts only, and never deepen a dip: dips get a 0 dB target.
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
        print(f'{label:<32} {lo:>5.0f}-{hi:<6.0f} Hz  '
              + (', '.join(f'{a:.0f}Hz {b:+.1f}/Q{q:.1f}' for a, b, q in sel) or 'flat'))

    idsp.save(root, OUT)
    assert idsp.dumps(idsp.load(OUT)) == open(OUT, 'rb').read()
    print(f'\nwrote {OUT}  (verify: round-trips byte-exact)')


if __name__ == '__main__':
    main()
