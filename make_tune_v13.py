#!/usr/bin/env python3
"""
v13 - first tune built from the REAL per-driver measurements.

Measured levels, all at the same playback level (1/3-oct smoothed, dB):

   Hz      ch2     ch4     ch3     ch5     ch6     ch7     ch8
         pf tw   rp tw   rd tw   fr3.5   rr3.5    fr8    rr6.5
  100     19.4    20.5    23.0    25.6    25.1    61.6    60.1
  200     12.8    13.6    14.4    34.7    31.4    54.5    57.5
  500     12.8    10.9    13.5    36.8    30.1    52.6    45.7
 1000     26.3    27.4    24.0    36.4    32.2    54.7    41.0
 2000     36.0    42.7    41.5    36.0    35.2    48.8    44.0
 5000     42.0    38.5    38.5    23.9    22.7    53.2    43.1

READING OF THAT:
  * ch7 (front 8") is a broadband LOUDSPEAKER - 50 Hz to 15 kHz, and the
    loudest thing in the car from 100 Hz to 5 kHz. +18 dB over the 3.5"s at
    1 kHz, and still +11 dB over the tweeters at 5 kHz. This is the user's
    "clear bright loud speaker" - it is doing the midrange AND the treble.
  * ch8 (rear 6.5") is a pure MIDBASS - 81 to ~1500 Hz, peak 115 Hz. This is
    the user's "midbass warmth".
  * 3.5"s cover ~200-2300 Hz but sit 11-18 dB BELOW the 8".
  * tweeters cover ~1200-19000 Hz but sit ~11 dB below the 8" at 5 kHz.

v13 therefore:
  * pulls ch7's gain to -14.0 dB (the lowest value observed in the user's own
    files, so a known-valid value). At -14 the 8" lands within ~2 dB of the
    tweeters at 2500 Hz - i.e. the 8"-to-tweeter crossover finally matches.
  * keeps ch8 LOW-PASSED at 1000 Hz so it stays a warm midbass instead of
    fighting for the midrange, and lifts it 1.5 dB because the user loves it.
  * narrows the 3.5"s to 250-2500 Hz, where they actually work.
  * leaves the tweeters, alignment and voicing exactly as v11.
"""
import glob
import numpy as np
import idsp

SRC = '2027-test-tune-v11.idsp'
OUT = '2027-test-tune-v13.idsp'
MEMORY_INDEX = '1'

# output index -> (high-pass Hz or None, low-pass Hz or None)
XB = {
    4: (250.0, 2500.0),   # ch5 front 3.5"
    5: (250.0, 2500.0),   # ch6 rear 3.5"
    6: (60.0, 2500.0),    # ch7 front 8"  - broadband, capped at the tweeter handoff
    7: (80.0, 1000.0),    # ch8 rear 6.5" - pure midbass
}
GAIN = {6: -14.0, 7: -5.0}   # ch7 8", ch8 6.5"

ROLE = {0:'ch1 tw',1:'ch2 tw',2:'ch3 tw',3:'ch4 tw',4:'ch5 fr3.5',5:'ch6 rr3.5',
        6:'ch7 fr8',7:'ch8 rr6.5'}


def main():
    root = idsp.load(SRC)
    mem = [m for m in idsp.memories(root) if m.get('index') == MEMORY_INDEX][0]

    print('crossover changes (v11 -> v13):')
    for idx, (hp, lp) in sorted(XB.items()):
        for pb in idsp.output(mem, idx).findall('passBand'):
            if pb.findtext('Enable') != '1':
                continue
            hl = pb.findtext('hiLowType')
            new = hp if hl == '0' else lp
            if new is None:
                continue
            old = pb.findtext('Fc')
            pb.find('Fc').text = idsp.fmt(new, 0)
            print(f'  {ROLE[idx]:<12} {"HP" if hl=="0" else "LP"} {old:>5} -> {idsp.fmt(new,0):>5} Hz')

    print('\ngain changes:')
    for idx, g in sorted(GAIN.items()):
        o = idsp.output(mem, idx)
        old = float(o.findtext('gain'))
        o.find('gain').text = idsp.fmt(g, 1)
        print(f'  {ROLE[idx]:<12} {old:+.1f} -> {g:+.1f} dB')

    idsp.save(root, OUT)
    assert idsp.dumps(idsp.load(OUT)) == open(OUT, 'rb').read()
    print(f'\nwrote {OUT} (round-trips byte-exact)')

    # ---------- predicted balance from the real measurements ----------------
    curves = {
        'ch2 tw': 'out_pass front tweet ch2',
        'ch4 tw': 'out_rear pass tweet ch4',
        'ch3 tw': 'out_rear dr tweet ch3',
        'ch5 3.5': 'out_front 3.5 ch5',
        'ch6 3.5': 'out_rear 3.5 ch 6',
        'ch7 8"': 'out_front 8 ch 7',
        'ch8 6.5': 'out_rear 6.5 ch 8',
    }
    base = '/tmp/incoming3/m/'
    data = {}
    for label, d in curves.items():
        hits = glob.glob(base + d + '/measurement_1_*.csv')
        if not hits:
            continue
        f = []; s = []
        for line in open(hits[0]):
            line = line.strip()
            if not line or line.startswith('#') or line.startswith('freq'):
                continue
            a, b = line.split(',')[:2]; f.append(float(a)); s.append(float(b))
        f = np.array(f); s = np.array(s); o = np.argsort(f)
        data[label] = (f[o], s[o])

    def lvl(label, hz):
        f, s = data[label]
        m = (f >= hz * 0.9) & (f <= hz * 1.1)
        return float(s[m].mean()) if m.any() else float('nan')

    gains = {'ch2 tw': -4.6, 'ch4 tw': -4.0, 'ch3 tw': -7.0,
             'ch5 3.5': 0.0, 'ch6 3.5': 0.0, 'ch7 8"': -14.0, 'ch8 6.5': -5.0}
    print('\npredicted in-band levels after v13 (measured + new gain):')
    print(f"  {'driver':<9}{'200Hz':>8}{'500Hz':>8}{'1kHz':>8}{'2.5kHz':>9}{'5kHz':>8}")
    for label in ['ch7 8"', 'ch8 6.5', 'ch5 3.5', 'ch6 3.5', 'ch2 tw', 'ch4 tw', 'ch3 tw']:
        if label not in data:
            continue
        g = gains[label]
        vals = [lvl(label, hz) + g for hz in (200, 500, 1000, 2500, 5000)]
        print(f"  {label:<9}" + ''.join(f'{v:>8.1f}' for v in vals))


if __name__ == '__main__':
    main()
