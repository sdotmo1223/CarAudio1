#!/usr/bin/env python3
"""
Predicted per-driver response with tune C applied.

Method, per channel:

  predicted(f) = measured_raw(f)
                 - measurement_highpass(f)      (undo the filter used when measuring)
                 + tune_highpass(f)             (LR4)
                 + tune_lowpass(f)              (LR4)
                 + (tune_gain - measurement_gain)
                 + sum(eq_band_response(f))     (RBJ peaking, Q as stored)

Filter maths:
  LR4 (24 dB/oct Linkwitz-Riley) high-pass : -20*log10(1 + (fc/f)^4)
  LR4 low-pass                             : -20*log10(1 + (f/fc)^4)
  BW2 (12 dB/oct Butterworth) high-pass    : -10*log10(1 + (fc/f)^4)

The measurement high-passes come from the user's own measurement preset
(ai tune raw.idsp, memory 1): ch1-4 = 1000 Hz, ch5-6 = 100 Hz, ch7-8 = 60 Hz,
all slope code 1 (=12 dB/oct) Butterworth (type 0).

These overlay the REAL measurement in REW - align levels in REW first, the
absolute SPL is not meaningful, only the shape.
"""
import glob
import csv
import numpy as np
import idsp
import peq

TUNE = '2027-C-competition.idsp'
MEAS_GAIN = -3.0          # every channel sat at -3 during the measurements
MEAS_HP = {0: 1000.0, 1: 1000.0, 2: 1000.0, 3: 1000.0,
           4: 100.0, 5: 100.0, 6: 60.0, 7: 60.0}
MEAS_DIR = {
    0: 'csv_pass front tweet ch2',   # ch1 mirrors ch2 (ch1 driver is damaged)
    1: 'csv_pass front tweet ch2',
    2: 'csv_rear dr tweet ch3',
    3: 'csv_rear pass tweet ch4',
    4: 'csv_front 3.5 ch5',
    5: 'csv_rear 3.5  ch 6'.replace('  ', ' '),
    6: 'csv_front 8 ch 7',
    7: 'csv_rear 6.5 ch 8',
}
BASE = '/agent/car-audio/measurements/7pack/'
OUTDIR = '/agent/car-audio/predicted'
NAMES = {0: 'ch1-driver-front-tweeter', 1: 'ch2-pass-front-tweeter',
         2: 'ch3-rear-driver-tweeter', 3: 'ch4-rear-pass-tweeter',
         4: 'ch5-front-3.5', 5: 'ch6-rear-3.5',
         6: 'ch7-front-8', 7: 'ch8-rear-6.5'}


def lr4_hp(f, fc):
    return -20.0 * np.log10(1.0 + (fc / f) ** 4)


def lr4_lp(f, fc):
    return -20.0 * np.log10(1.0 + (f / fc) ** 4)


def bw2_hp(f, fc):
    return -10.0 * np.log10(1.0 + (fc / f) ** 4)


def load_csv(d):
    hits = glob.glob(BASE + d + '/measurement_1_*.csv')
    f = []; s = []
    for line in open(hits[0]):
        line = line.strip()
        if not line or line.startswith('#') or line.startswith('freq'):
            continue
        a, b = line.split(',')[:2]; f.append(float(a)); s.append(float(b))
    f = np.array(f); s = np.array(s); o = np.argsort(f)
    return f[o], s[o]


def main():
    import os
    os.makedirs(OUTDIR, exist_ok=True)
    root = idsp.load(TUNE)
    mem = [m for m in idsp.memories(root) if m.get('index') == '1'][0]

    print(f'tune: {TUNE}\n')
    print(f"{'file':<34}{'HP':>7}{'LP':>7}{'gain':>7}{'EQ':>4}  predicted range vs measured")
    for idx in [7]:                       # ch8 only, as requested
        o = idsp.output(mem, idx)
        f, s = load_csv(MEAS_DIR[idx])

        hp = lp = None
        for pb in o.findall('passBand'):
            if pb.findtext('Enable') != '1':
                continue
            if pb.findtext('hiLowType') == '0':
                hp = float(pb.findtext('Fc'))
            else:
                lp = float(pb.findtext('Fc'))
        gain = float(o.findtext('gain'))
        bands = [(fc, g, q) for fc, g, q in idsp.read_eq(o) if g != 0]

        pred = s.copy()
        pred -= bw2_hp(f, MEAS_HP[idx])           # undo measurement high-pass
        if hp:
            pred += lr4_hp(f, hp)
        if lp:
            pred += lr4_lp(f, lp)
        pred += (gain - MEAS_GAIN)
        for fc, g, q in bands:
            pred += peq.peq_response(f, fc, g, q)

        path = f'{OUTDIR}/pred-{NAMES[idx]}.txt'
        keep = (f >= 20.0) & (f <= 20000.0)          # useful band only
        with open(path, 'w') as fh:
            fh.write('* Predicted response with tune C applied\n')
            fh.write(f'* channel: {NAMES[idx]}\n')
            fh.write('* filters: HP %s Hz, LP %s Hz, gain %s dB, %d EQ bands\n'
                     % (f'{hp:.0f}' if hp else 'none',
                        f'{lp:.0f}' if lp else 'none', gain, len(bands)))
            fh.write('* freq(Hz)\tSPL(dB)\n')
            for a, b in zip(f[keep], pred[keep]):
                fh.write(f'{a:.3f}\t{b:.3f}\n')

        delta = pred - s
        hp_s = f'{hp:.0f}' if hp else '-'
        lp_s = f'{lp:.0f}' if lp else '-'
        print(f'{os.path.basename(path):<34}{hp_s:>7}{lp_s:>7}'
              f'{gain:>7.1f}{len(bands):>4}  {delta.max():+.1f} to {delta.min():+.1f} dB')


if __name__ == '__main__':
    main()
