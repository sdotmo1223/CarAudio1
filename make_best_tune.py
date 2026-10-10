#!/usr/bin/env python3
"""
First-try tune: rebuild the EQ as a clean, broad correction.

Review of the existing tune (memory 1) found it heavily over-fitted:
138 active bands, 96 of them cuts of 6 dB or more, 78 with Q >= 4, and single
bands cutting -21 dB @ 149 Hz, -23 dB @ 215 Hz (Q 19.6), -18.7 dB @ 19870 Hz.
Those are surgical notches chasing comb-filtering artefacts of an ungated RTA.

So instead of layering more EQ on top, this REPLACES the per-channel EQ with a
broad, cuts-only correction fitted to the 1/3-octave trend.

Kept untouched: crossovers, delays, gains, routing, other memory slots.

Still a first try: the source is one summed, ungated RTA, so it cannot tell
left from right or separate a driver from the cabin.
"""
import glob

import numpy as np

import idsp
import peq
from tune import read_measurement

BASE = '2027-base.idsp'
OUT = 'test/first-try-memory1.idsp'
MEMORY_INDEX = '1'

MAX_CUT = 6.0          # dB
MAX_Q = 3.0            # broad, musical - no surgical notches
BANDS_PER_CH = 6
SMOOTH = 3.0           # windows per octave
BAND_LO, BAND_HI = 40.0, 12000.0     # above ~12 kHz is the mic, not the car
TILT = -0.5            # dB/octave house curve

CHANNEL_MAP = {0: 'tweeter', 1: 'tweeter', 2: 'tweeter', 3: 'tweeter',
               4: 'mid 3.5"', 5: 'mid 3.5"',
               6: 'full-range', 7: 'full-range'}


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
    target = peq.target_tilt(g, 1000.0, TILT, level)
    corr = target - sg

    peq.G_MIN = -MAX_CUT
    peq.Q_MAX = MAX_Q

    print(f'reference level {level:.1f} dB, house curve {TILT:+.1f} dB/oct')
    print(f'limits: cuts only, max {MAX_CUT:g} dB, Q <= {MAX_Q:g}, '
          f'{BAND_LO:.0f}-{BAND_HI:.0f} Hz\n')

    root = idsp.load(BASE)
    mem = [m for m in idsp.memories(root) if m.get('index') == MEMORY_INDEX][0]

    print(f'{"out":>3} {"role":<11}{"old":>5}{"new":>5}  new bands')
    for idx in sorted(CHANNEL_MAP):
        o = idsp.output(mem, idx)
        old = [b for b in idsp.read_eq(o) if b[1] != 0]
        hp, lp = passband(o)
        lo = max(BAND_LO, hp * 1.05) if hp else BAND_LO
        hi = min(BAND_HI, lp * 0.95) if lp else BAND_HI
        keep = (g >= lo) & (g <= hi)

        sel = []
        if keep.sum() >= 8:
            cc = corr[keep]
            # Only chase the peaks. Everything below target is either a null
            # (impossible for EQ to fill) or a rolloff; including those in the
            # error metric drags the fit away from the cuts that do matter.
            w = np.where(cc < 0, 1.0, 0.0)
            if w.sum() >= 5:
                filt, _ = peq.fit_peq(g[keep], cc, n_filters=BANDS_PER_CH,
                                      weights=w, max_boost_db=0.0)
                sel = [(a, b, q) for a, b, q in filt if abs(b) >= 0.4]

        idsp.set_eq(o, sel)
        shown = ', '.join(f'{a:.0f}Hz {b:+.1f}dB Q{q:.1f}' for a, b, q in sel) or '-'
        print(f'{idx:>3} {CHANNEL_MAP[idx]:<11}{len(old):>5}{len(sel):>5}  {shown}')

    idsp.save(root, OUT)
    print(f'\nwrote {OUT}')

    check = idsp.load(OUT)
    assert idsp.dumps(check) == open(OUT, 'rb').read()
    print('verify: round-trips byte-exact')

    # predicted response change from the new EQ alone
    print('\npredicted correction (new EQ only):')
    for lo, hi in [(40, 80), (80, 160), (160, 315), (315, 630), (630, 1250),
                   (1250, 2500), (2500, 5000), (5000, 12000)]:
        tot = 0.0
        for idx in sorted(CHANNEL_MAP):
            o = idsp.output(mem, idx)
            for fc, gain, q in idsp.read_eq(o):
                if gain != 0:
                    tot += float(peq.peq_response(np.array([np.sqrt(lo * hi)])[0],
                                                  fc, gain, q))
        print(f'  {lo:>5}-{hi:<5} Hz  {tot/len(CHANNEL_MAP):+5.1f} dB (avg per channel)')


if __name__ == '__main__':
    main()
