#!/usr/bin/env python3
"""
Test tune v2 - channels 5-8 handled as front/rear PAIRS.

Channel map (DSP numbering, as the user describes it):
  1-4  tweeters                     -> xml outputs 0..3
  5    front 3.5"   \\  pair A       -> xml output 4
  6    rear  3.5"   /               -> xml output 5
  7    front 8"     \\  pair B       -> xml output 6
  8    rear  6.5"   /               -> xml output 7

Because each pair shares a driver type, both members get the SAME correction,
fitted over the passband they have in common (the narrower of the two).

Safety / honesty unchanged from v1: cuts only, no boost, broad Q, source is one
summed ungated RTA, crossovers/delays/gains/routing and other memory slots
untouched.
"""
import glob

import numpy as np

import idsp
import peq
from tune import read_measurement

BASE = '2027-base.idsp'
OUT = '2027-test-tune-v2.idsp'
MEMORY_INDEX = '1'

MAX_CUT = 8.0          # dB - cuts are level-safe
MAX_Q = 4.0
BANDS = 7
SMOOTH = 3.0
BAND_LO, BAND_HI = 40.0, 12000.0
TILT = -0.5            # dB/octave house curve

TWEETERS = [0, 1, 2, 3]
PAIRS = [
    ('ch5 front 3.5" + ch6 rear 3.5"', [4, 5]),
    ('ch7 front 8"   + ch8 rear 6.5"', [6, 7]),
]
ROLE = {0: 'tweeter', 1: 'tweeter', 2: 'tweeter', 3: 'tweeter',
        4: 'front 3.5"', 5: 'rear 3.5"', 6: 'front 8"', 7: 'rear 6.5"'}


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

    level = float(np.median(sg[(g >= 300) & (g <= 3000)]))
    corr = peq.target_tilt(g, 1000.0, TILT, level) - sg

    peq.G_MIN = -MAX_CUT
    peq.Q_MAX = MAX_Q

    print(f'reference {level:.1f} dB, house curve {TILT:+.1f} dB/oct | '
          f'cuts only, max {MAX_CUT:g} dB, Q <= {MAX_Q:g}\n')

    root = idsp.load(BASE)
    mem = [m for m in idsp.memories(root) if m.get('index') == MEMORY_INDEX][0]

    # ---- tweeters: wide open on top, measurement shows no excess to cut -----
    for idx in TWEETERS:
        idsp.set_eq(idsp.output(mem, idx), [])
    print(f'ch1-4 tweeters : EQ cleared (measurement shows no HF excess)')

    # ---- the mid pairs -----------------------------------------------------
    for label, chans in PAIRS:
        bands = [passband(idsp.output(mem, c)) for c in chans]
        lo = max([BAND_LO] + [h for h, _ in bands if h])
        hi = min([BAND_HI] + [l for _, l in bands if l])
        keep = (g >= lo * 1.05) & (g <= hi * 0.95)

        # Only cut what is genuinely above target. Dips get a target of 0 dB,
        # so the fit is actively penalised for making them any deeper - a
        # weight of zero would let a broad band over-cut straight through them.
        cc = np.minimum(corr[keep], 0.0)
        w = np.ones_like(cc)
        sel = []
        if (cc < -0.5).sum() >= 5:
            # keep the optimiser from placing bands outside what the driver
            # can actually reproduce (it did once: 13 kHz on a 5 kHz mid)
            peq.F_MIN, peq.F_MAX = lo, hi
            filt, _ = peq.fit_peq(g[keep], cc, n_filters=BANDS,
                                  weights=w, max_boost_db=0.0)
            sel = [(a, b, q) for a, b, q in filt if abs(b) >= 0.4]

        for c in chans:
            idsp.set_eq(idsp.output(mem, c), sel)
        print(f'{label}  window {lo:.0f}-{hi:.0f} Hz')
        for a, b, q in sel:
            print(f'      {a:7.0f} Hz  {b:+6.2f} dB  Q {q:.2f}')
        if not sel:
            print('      (nothing to cut)')
        else:
            print('      achieved vs needed:')
            for probe in [119, 250, 405, 560, 700, 1200, 1530, 2000]:
                if not (lo * 0.9 <= probe <= hi * 1.1):
                    continue
                need = float(np.interp(np.log10(probe), np.log10(g), corr))
                got = float(sum(peq.peq_response(np.array([probe]), a, b, q)[0]
                                for a, b, q in sel))
                print(f'        {probe:5d} Hz   need {need:+5.1f}   got {got:+5.1f}')

    idsp.save(root, OUT)

    check = idsp.load(OUT)
    assert idsp.dumps(check) == open(OUT, 'rb').read()
    n = sum(1 for m in idsp.memories(check) for o in idsp.outputs(m)
            for _, gain, _ in idsp.read_eq(o) if gain != 0)
    print(f'\nwrote {OUT}  (active EQ bands overall: '
          f'{sum(1 for m in idsp.memories(idsp.load(BASE)) for o in idsp.outputs(m) for _,gg,_ in idsp.read_eq(o) if gg!=0)} -> {n})')
    print('verify: round-trips byte-exact')


if __name__ == '__main__':
    main()
