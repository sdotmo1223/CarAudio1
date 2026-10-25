#!/usr/bin/env python3
"""
Three tunes from the same measured data, three philosophies.

  A - BEST GUESS     : my ongoing judgement. Measured level balance, safe
                       crossovers, and a moderate per-driver EQ fit (6 bands,
                       Q<=3) so each driver is flatter inside its own band.

  B - ENSURE SUCCESS : the conservative one. Same crossovers and levels, but
                       minimal EQ - only the mild cuts, nothing aggressive.
                       Nothing here can surprise you or hurt a driver.

  C - COMPETITION    : "chasing wiggles" to the limit. Up to 20 bands per
                       channel, Q up to 10, fitted hard to each driver's own
                       measured curve. This is what a competition tuner would
                       attempt - and with ungated RTA data it WILL partly chase
                       reflections. Built because you asked for it, flagged
                       because it is the riskiest of the three.

All three fix the high-pass mistake: ch7 60->110 Hz, ch8 80->140 Hz (their
rated limits).
"""
import glob
import numpy as np
import idsp
import peq

SRC = '2027-test-tune-v13.idsp'
MEM = '1'
MEAS = '/agent/car-audio/measurements/7pack/'

# output index -> measured curve dir (ch1 is damaged, so it mirrors ch2)
CURVE = {
    0: 'csv_pass front tweet ch2',    # ch1 -> mirror of ch2
    1: 'csv_pass front tweet ch2',    # ch2
    2: 'csv_rear dr tweet ch3',       # ch3
    3: 'csv_rear pass tweet ch4',     # ch4
    4: 'csv_front 3.5 ch5',           # ch5
    5: 'csv_rear 3.5 ch 6',           # ch6
    6: 'csv_front 8 ch 7',            # ch7
    7: 'csv_rear 6.5 ch 8',           # ch8
}
# passband each channel should be equalised over, after the new crossovers
BAND = {0: (2500, 20000), 1: (2500, 20000), 2: (2500, 20000), 3: (2500, 20000),
        4: (250, 2500), 5: (250, 2500), 6: (110, 2500), 7: (140, 1000)}

# high-pass corrections (rated limits)
HP_FIX = {6: 110.0, 7: 140.0}

VARIANTS = [
    ('A', 'best guess',     6,  3.0,  8.0, 6.0,  0.5, '2027-A-best-guess.idsp'),
    ('B', 'ensure success', 0,  0.0,  0.0, 6.0,  0.5, '2027-B-safe.idsp'),
    ('C', 'competition',    20, 10.0, 12.0, 12.0, 0.15, '2027-C-competition.idsp'),
]


def load_curve(d):
    hits = glob.glob(MEAS + d + '/measurement_1_*.csv')
    f = []; s = []
    for line in open(hits[0]):
        line = line.strip()
        if not line or line.startswith('#') or line.startswith('freq'):
            continue
        a, b = line.split(',')[:2]; f.append(float(a)); s.append(float(b))
    f = np.array(f); s = np.array(s); o = np.argsort(f)
    return f[o], s[o]


def fit_driver(f, s, lo, hi, n_bands, max_q, max_cut, smooth=6.0, min_gain=0.5):
    """Cut the peaks in this driver's own measured response inside its band."""
    sm = peq.smooth_log(f, s, smooth)
    keep = (f >= lo * 1.05) & (f <= hi * 0.95)
    if keep.sum() < 12 or n_bands == 0:
        return []
    ff, ss = f[keep], sm[keep]
    trend = np.median(ss)
    cc = np.minimum(trend - ss, 0.0)          # cuts only
    peq.F_MIN, peq.F_MAX = lo, hi
    peq.Q_MAX = max_q
    peq.G_MIN = -max_cut
    filt, _ = peq.fit_peq(ff, cc, n_filters=n_bands,
                          weights=np.ones_like(cc), max_boost_db=0.0)
    return [(a, b, q) for a, b, q in filt if abs(b) >= min_gain]


def build(tag, label, n_bands, max_q, max_cut, smooth, min_gain, out):
    root = idsp.load(SRC)
    mem = [m for m in idsp.memories(root) if m.get('index') == MEM][0]

    # high-pass fix on every variant
    for idx, hp in HP_FIX.items():
        for pb in idsp.output(mem, idx).findall('passBand'):
            if pb.findtext('hiLowType') == '0':
                pb.find('Fc').text = idsp.fmt(hp, 0)

    for idx in range(8):
        if n_bands:
            f, s = load_curve(CURVE[idx])
            lo, hi = BAND[idx]
            sel = fit_driver(f, s, lo, hi, n_bands, max_q, max_cut,
                             smooth, min_gain)
            idsp.set_eq(idsp.output(mem, idx), sel)
    # B keeps whatever EQ v13 already had (do nothing)

    idsp.save(root, out)
    assert idsp.dumps(idsp.load(out)) == open(out, 'rb').read()
    tot = sum(len([1 for f_, g, q in idsp.read_eq(idsp.output(mem, i)) if g != 0])
              for i in range(8))
    print(f'{tag}  {label:<15} {out:<28} bands={tot:>3}  '
          + (f'per-driver fit (smooth 1/{smooth:g} oct)' if n_bands
             else 'minimal EQ (v13 cuts kept)'))
    return out


def main():
    print('high-pass fix on all three: ch7 60->110 Hz, ch8 80->140 Hz\n')
    outs = []
    for tag, label, n, mxq, mxc, sm, mg, out in VARIANTS:
        outs.append(build(tag, label, n, mxq, mxc, sm, mg, out))
    return outs


if __name__ == '__main__':
    main()
