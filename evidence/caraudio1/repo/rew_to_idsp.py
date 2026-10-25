#!/usr/bin/env python3
"""
Turn REW (Room EQ Wizard) EQ filter exports into a loadable DS18 .idsp file.

Instead of typing 30 parametric bands into the DSP software by hand, export the
filter list from REW and let this write them straight into the preset.

REW side: in the EQ window, "Export filter settings as text" using the
"Generic" (a.k.a. Equalizer APO) format:

    Filter  1: ON PK Fc 1258.0 Hz Gain -5.8 dB Q 6.600
    Filter  2: ON PK Fc 1295.0 Hz Gain -4.0 dB Q 6.600

Usage
-----
Single channel:

    python3 rew_to_idsp.py base.idsp out.idsp \
        --memory 1 --output 0 --rew left-tweeter.txt

Several channels at once (repeat --set; output index = REW file):

    python3 rew_to_idsp.py base.idsp out.idsp --memory 1 \
        --set 0=left-tweet.txt --set 1=left-mid.txt --set 6=sub.txt

Options:
    --memory N|all     which preset slot(s) to write (default: all)
    --zero-unused      clear unused bands (default: on)
    --keep-unused      leave unused bands as they were
    --dry-run          show what would change, write nothing
"""
import argparse
import re
import sys

import idsp

# Generic / Equalizer APO line, e.g.
#   Filter  1: ON  PK       Fc   1258.0 Hz  Gain  -5.8 dB  Q  6.600
#   Filter  2: ON  LS       Fc    100.0 Hz  Gain   3.0 dB  Q  0.710
FILTER_RE = re.compile(
    r'^\s*Filter\s+(\d+)\s*:\s*(ON|OFF)\s+([A-Za-z]+)\s+'
    r'Fc\s+([0-9.]+)\s*Hz'
    r'(?:\s+Gain\s+(-?[0-9.]+)\s*dB)?'
    r'(?:\s+Q\s+([0-9.]+))?',
    re.IGNORECASE)

SHELF_TYPES = {'LS', 'HS', 'LSC', 'HSC', 'LSQ', 'HSQ', 'LP', 'HP'}


def parse_rew(text):
    """Return (peaking_filters, skipped) where filters are (Fc, Gain, Q)."""
    filters, skipped = [], []
    for line in text.splitlines():
        line = line.strip()
        if not line or line.startswith('#'):
            continue
        m = FILTER_RE.match(line)
        if not m:
            continue
        _, onoff, ftype, fc, gain, q = m.groups()
        if onoff.upper() != 'ON':
            continue
        ftype = ftype.upper()
        if ftype in SHELF_TYPES:
            skipped.append((ftype, float(fc), float(gain or 0), q))
            continue
        if q is None:
            skipped.append((ftype, float(fc), float(gain or 0), None))
            continue
        filters.append((float(fc), float(gain or 0.0), float(q)))
    return filters, skipped


def parse_sets(args):
    sets = {}
    if args.output is not None and args.rew:
        sets[args.output] = args.rew
    for item in (args.set or []):
        if '=' not in item:
            sys.exit(f'--set needs OUT=FILE, got {item!r}')
        k, v = item.split('=', 1)
        sets[int(k)] = v
    if not sets:
        sys.exit('nothing to do: pass --output/--rew or at least one --set OUT=FILE')
    return sets


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('base', help='existing .idsp to use as the template')
    ap.add_argument('out', nargs='?', help='.idsp to write')
    ap.add_argument('--memory', default='all',
                    help='memory slot index, or "all" (default: all)')
    ap.add_argument('--output', type=int, help='output channel index')
    ap.add_argument('--rew', help='REW filter export for --output')
    ap.add_argument('--set', action='append', metavar='OUT=FILE',
                    help='channel index = REW export (repeatable)')
    ap.add_argument('--zero-unused', dest='zero', action='store_true', default=True)
    ap.add_argument('--keep-unused', dest='zero', action='store_false')
    ap.add_argument('--gain-decimals', type=int, default=1)
    ap.add_argument('--q-decimals', type=int, default=1)
    ap.add_argument('--fc-decimals', type=int, default=0)
    ap.add_argument('--dry-run', action='store_true')
    args = ap.parse_args(argv)

    sets = parse_sets(args)
    root = idsp.load(args.base)

    mems = idsp.memories(root)
    if args.memory == 'all':
        targets = mems
    else:
        want = int(args.memory)
        targets = [m for m in mems if int(m.get('index', -1)) == want]
        if not targets:
            sys.exit(f'no memory index {want} in {args.base}')

    if not args.dry_run and not args.out:
        sys.exit('output path is required unless --dry-run')

    for fpath, out_idx in [(v, k) for k, v in sorted(sets.items())]:
        filters, skipped = parse_rew(open(fpath, encoding='utf-8', errors='replace').read())
        print(f'  channel {out_idx}: {len(filters)} peaking filters from {fpath}')
        for ftype, fc, gain, q in skipped:
            print(f'    ! skipped {ftype} @ {fc:g} Hz {gain:+g} dB '
                  f'(this DSP stores peaking bands only)')
        for m in targets:
            o = idsp.output(m, out_idx)
            before = idsp.read_eq(o)
            if args.dry_run:
                active = sum(1 for _, g, _ in before if g != 0)
                print(f'    memory {m.get("index")} out {out_idx}: '
                      f'{active} active bands -> {len(filters)}')
            else:
                idsp.set_eq(o, filters, gain_decimals=args.gain_decimals,
                            q_decimals=args.q_decimals,
                            fc_decimals=args.fc_decimals, zero_unused=args.zero)

    if args.dry_run:
        print('(dry run - nothing written)')
        return 0

    idsp.save(root, args.out)
    print(f'wrote {args.out}')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
