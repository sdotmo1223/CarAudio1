#!/usr/bin/env python3
"""
Test tune v4 - protect the drivers, keep the R&B voicing.

Specs (manufacturer-rated):
  NVX XQS3v2  3.5"  : 200 Hz - 18 kHz, 85.0 dB   -> ch5, ch6
  Orion XTX654 6.5" : 140 Hz - 12 kHz, 97.0 dB   -> ch8
  Deaf Bonce AP-M81SE 8": 110 Hz - 10 kHz, 95.1 dB (Fs 110) -> ch7

v3 (and the user's original tune) high-passed ALL of these at 100 Hz, which is
below every one of their rated limits. v4 sets each high-pass to the driver's
rated bottom instead:

  ch5/ch6 (3.5")  : 100 -> 200 Hz   (an octave; the 8"/6.5" already cover this)
  ch7 (8")        : 100 -> 110 Hz
  ch8 (6.5")      : 100 -> 140 Hz

Low-pass points are left alone. EQ is refitted inside the new passbands using
the same R&B / hip-hop house curve as v3. Cuts only.
"""
import glob

import numpy as np

import idsp
import peq
from tune import read_measurement

BASE = '2027-base.idsp'
OUT = '2027-test-tune-v4.idsp'
MEMORY_INDEX = '1'

MAX_CUT = 9.0
MAX_Q = 4.0
BANDS = 7
SMOOTH = 3.0
BAND_LO, BAND_HI = 40.0, 16000.0
TILT_REF = 1000.0

RNB_POINTS = [
    (20, +6.0), (35, +6.0), (55, +5.0), (80, +4.0), (120, +3.0),
    (180, +2.0), (300, +1.0), (500, +0.3), (800, -0.2), (1000, -0.5),
    (2000, -1.5), (3500, -2.5), (6000, -3.5), (10000, -4.5), (20000, -5.0),
]

# output index -> new high-pass Fc (Hz). None = leave alone.
NEW_HP = {4: 200.0, 5: 200.0, 6: 110.0, 7: 140.0}

TWEETERS = [0, 1, 2, 3]
GROUPS = [
    ('ch1-4 tweeters', TWEETERS, 1.5, 5.0),
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


def set_hp(o, fc):
    for pb in o.findall('passBand'):
        if pb.findtext('hiLowType') == '0':
            pb.find('Fc').text = idsp.fmt(fc, 0)
            return True
    return False


def main():
    path = glob.glob('measurements/csv/measurement_1_*.csv')[0]
    f, spl = read_measurement(path)
    sm = peq.smooth_log(f, spl, SMOOTH)

    g = np.logspace(np.log10(30.0), np.log10(20000.0), 250)
    sg = np.interp(np.log10(g), np.log10(f), sm)

    mid = (g >= 300) & (g <= 3000)
    level = float(np.median(sg[mid]) - np.median(rnb_shape(g[mid])))
    corr = (level + rnb_shape(g)) - sg

    root = idsp.load(BASE)
    mem = [m for m in idsp.memories(root) if m.get('index') == MEMORY_INDEX][0]

    # ---- 1. correct the high-pass points -----------------------------------
    print('high-pass changes:')
    for idx, fc in sorted(NEW_HP.items()):
        o = idsp.output(mem, idx)
        old, lp = passband(o)
        set_hp(o, fc)
        print(f'  output {idx}: {old:.0f} -> {fc:.0f} Hz   (low-pass kept at '
              f'{lp if lp else "off"})')
    print()

    # ---- 2. refit EQ inside the NEW passbands ------------------------------
    for label, chans, qmax, cutmax in GROUPS:
        bands = [passband(idsp.output(mem, c)) for c in chans]
        lo = max([BAND_LO] + [h for h, _ in bands if h])
        hi = min([BAND_HI] + [l for _, l in bands if l])
        keep = (g >= lo * 1.05) & (g <= hi * 0.95)

        cc = np.minimum(corr[keep], 0.0)   # never deepen a dip
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
        print(f'{label:<32} window {lo:>4.0f}-{hi:<5.0f} Hz  '
              + (', '.join(f'{a:.0f}Hz {b:+.1f}/Q{q:.1f}' for a, b, q in sel) or 'flat'))

    idsp.save(root, OUT)
    assert idsp.dumps(idsp.load(OUT)) == open(OUT, 'rb').read()
    print(f'\nwrote {OUT}  (round-trips byte-exact)')

    # ---- predicted change vs the user's original ---------------------------
    A = idsp.load(BASE)
    ma = [m for m in idsp.memories(A) if m.get('index') == MEMORY_INDEX][0]
    print('\npredicted EQ change vs original (before crossovers):')
    for lab, ch in [('tweeters', [0, 1, 2, 3]), ('3.5in', [4, 5]), ('8in/6.5in', [6, 7])]:
        print(f'  {lab}')
        for probe in [80, 120, 200, 400, 1000, 4000, 10000, 15000]:
            tot = 0.0
            for c in ch:
                for fc, gn, q in idsp.read_eq(idsp.output(ma, c)):
                    if gn != 0:
                        tot += float(peq.peq_response(np.array([probe]), fc, gn, q)[0])
            print(f'     {probe:>6} Hz {tot/len(ch):+5.1f} dB')


if __name__ == '__main__':
    main()
