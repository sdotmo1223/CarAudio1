#!/usr/bin/env python3
"""
Generate a DSP tune from a measurement: measurement + target -> PEQ bands.

This is the "tuning brain". It does NOT measure anything - it takes a response
curve you already have (e.g. a REW text export), works out what correction is
needed to reach a target curve, and fits peaking filters to it within the
DSP's real limits.

    python3 tune.py measurement.txt --tilt -0.5 --bands 15 --max-boost 3

Add --idsp/--memory/--output to write the result straight into a loadable
`.idsp` preset (see rew_to_idsp.py for the REW-filter-list route).

REW side: measurement window -> File -> Export -> "Export measurement as text".
Tolerant of REW's header lines and of tab / comma / space separated columns.
"""
import argparse
import sys

import numpy as np

import idsp
import peq


def read_measurement(path):
    """Return (freqs, spl_db) from a REW-style text export."""
    freqs, db = [], []
    with open(path, encoding='utf-8', errors='replace') as fh:
        for line in fh:
            line = line.strip()
            if not line or line.startswith(('*', '#', '//')):
                continue
            parts = line.replace(',', ' ').replace('\t', ' ').split()
            try:
                f = float(parts[0])
            except (ValueError, IndexError):
                continue
            if f <= 0:
                continue
            try:
                d = float(parts[1])
            except (ValueError, IndexError):
                continue
            freqs.append(f)
            db.append(d)
    if len(freqs) < 8:
        sys.exit(f'{path}: found only {len(freqs)} usable rows - is this a '
                 'REW "export measurement as text" file?')
    order = np.argsort(freqs)
    return np.array(freqs)[order], np.array(db)[order]


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('measurement', help='REW measurement text export')
    ap.add_argument('--bands', type=int, default=15, help='max PEQ bands (default 15)')
    ap.add_argument('--tilt', type=float, default=-0.5,
                    help='house-curve slope, dB per octave (default -0.5)')
    ap.add_argument('--tilt-anchor', type=float, default=1000.0,
                    help='frequency where the target sits at 0 dB (default 1000)')
    ap.add_argument('--level', type=float, default=None,
                    help='target level in dB at the anchor (default: auto, '
                         'aligned to the measurement midband)')
    ap.add_argument('--max-boost', type=float, default=3.0,
                    help='cap on upward gain in dB (default 3; use 0 for cuts only)')
    ap.add_argument('--max-cut', type=float, default=24.0,
                    help='cap on downward gain in dB (default 24)')
    ap.add_argument('--smooth', type=float, default=6.0,
                    help='smoothing, in windows per octave (default 6; 0 = none)')
    ap.add_argument('--grid', type=int, default=250,
                    help='points on the fitting grid (default 250; 0 = keep all)')
    ap.add_argument('--out', help='write the filter list here as text')
    # optional: go straight into a preset
    ap.add_argument('--idsp', help='base .idsp to write into')
    ap.add_argument('--memory', default='all', help='memory slot or "all"')
    ap.add_argument('--output', type=int, help='output channel index for --idsp')
    args = ap.parse_args(argv)

    f, spl = read_measurement(args.measurement)
    if args.smooth > 0:
        spl_fit = peq.smooth_log(f, spl, args.smooth)
    else:
        spl_fit = spl

    # Fit on a log grid matched to the filters' own resolution. A 1/48-octave
    # measurement has far more points than 15 peaking bands can distinguish,
    # and fitting every point is both slow and pointless.
    if args.grid and len(f) > args.grid:
        g = np.logspace(np.log10(max(f[0], 20.0)),
                        np.log10(min(f[-1], 20000.0)), args.grid)
        spl = np.interp(np.log10(g), np.log10(f), spl)
        spl_fit = np.interp(np.log10(g), np.log10(f), spl_fit)
        f = g

    # Align the target to the measurement's own level. Measurements come in as
    # absolute dB SPL (~70 dB), so a 0 dB target would ask for a ~70 dB cut.
    if args.level is None:
        mid = (f >= 300) & (f <= 3000)
        level = float(np.median(spl_fit[mid])) if mid.any() \
            else float(np.median(spl_fit))
    else:
        level = args.level
    target = peq.target_tilt(f, args.tilt_anchor, args.tilt, level)
    correction = target - spl_fit

    # weight the band where it actually matters
    weights = np.ones_like(f)
    weights[f < 40] = 0.25
    weights[f > 12000] = 0.25

    peq.G_MIN = -abs(args.max_cut)
    filters, info = peq.fit_peq(f, correction, n_filters=args.bands,
                                weights=weights, max_boost_db=args.max_boost)

    band = (f >= 100) & (f <= 10000)
    before = float(np.sqrt(np.mean(correction[band] ** 2)))
    after = peq.bank_response(f, info['flat_params']) - correction
    print(f'{args.measurement}: {len(f)} points, smoothed {args.smooth}/oct')
    print(f'  target: {args.tilt:+.2f} dB/oct anchored at {args.tilt_anchor:g} Hz')
    rms_after = float(np.sqrt(np.mean(after[band] ** 2)))
    print(f'  RMS error 100 Hz-10 kHz: {before:.2f} dB -> {rms_after:.2f} dB')
    print(f'  bands: {info["used"]} used (max error after {np.max(np.abs(after[band])):.2f} dB)')
    print()
    print('  Fc (Hz)      Gain (dB)   Q')
    for f0, g, q in filters:
        print(f'  {f0:9.1f}   {g:+8.2f}   {q:5.2f}')

    if args.out:
        with open(args.out, 'w', encoding='utf-8') as fh:
            fh.write('# Fc Hz\tGain dB\tQ\n')
            for f0, g, q in filters:
                fh.write(f'{f0:.1f}\t{g:.2f}\t{q:.2f}\n')
        print(f'\nfilter list -> {args.out}')

    if args.idsp:
        if args.output is None:
            sys.exit('--idsp needs --output <channel index>')
        root = idsp.load(args.idsp)
        mems = idsp.memories(root)
        targets = mems if args.memory == 'all' else \
            [m for m in mems if m.get('index') == str(args.memory)]
        for m in targets:
            idsp.set_eq(idsp.output(m, args.output), filters)
        out = args.idsp.replace('.idsp', f'-out.idsp')
        idsp.save(root, out)
        print(f'preset -> {out} (channel {args.output}, memory {args.memory})')


if __name__ == '__main__':
    raise SystemExit(main())
