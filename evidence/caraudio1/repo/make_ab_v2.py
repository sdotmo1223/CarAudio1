#!/usr/bin/env python3
"""
Rebuild the 4-memory A/B file with the current state of things.

The previous A/B file held original / v7 / v8 / v9 - all made before we
discovered the slope mismatches. This one puts the versions that actually
matter next to each other, with each step isolating one change:

  memory 1  ORIGINAL          - baseline, mismatched slopes (12/18/24 at 2500)
  memory 2  v8                - the "well rounded" favourite: aligned + warmth
  memory 3  v10               - v8 + 8"/6.5" low-passed at 2500 (slopes untouched)
  memory 4  v11               - v10 + EVERY crossover matched at LR24

So 2 -> 3 isolates the low-pass move, and 3 -> 4 isolates the slope matching.
mainMemory points at slot 4 (v11).
"""
import copy

import idsp

BASE = '2027-base.idsp'
OUT = '2027-AB-4memories-v2.idsp'

SLOTS = [
    (0, 'ORIGINAL',  BASE,                       '1'),
    (1, 'v8  warmth', '2027-test-tune-v8.idsp',  '1'),
    (2, 'v10 LP 2500', '2027-test-tune-v10.idsp', '1'),
    (3, 'v11 all LR24', '2027-test-tune-v11.idsp', '1'),
]
ACTIVE_SLOT = 3

CODE_TO_DB = {0: 6, 1: 12, 2: 18, 3: 24, 4: 30, 5: 36, 6: 42, 7: 48}


def main():
    root = idsp.load(BASE)

    for slot, label, src, src_idx in SLOTS:
        src_root = idsp.load(src)
        new_mem = copy.deepcopy(
            [m for m in idsp.memories(src_root) if m.get('index') == src_idx][0])
        new_mem.set('index', str(slot))
        for i, child in enumerate(list(root)):
            if child.tag == 'memory' and child.get('index') == str(slot):
                root[i] = new_mem
                break
        print(f'slot {slot} <- {label:<12} ({src})')

    root.find('RunStatus').find('mainMemory').text = str(ACTIVE_SLOT)
    idsp.save(root, OUT)
    assert idsp.dumps(idsp.load(OUT)) == open(OUT, 'rb').read()
    print(f'\nwrote {OUT} (round-trips byte-exact), mainMemory -> slot {ACTIVE_SLOT+1}')

    chk = idsp.load(OUT)
    print(f"\n{'mem':>4} {'label':<13} {'2500 Hz crossovers (slope -> dB/oct, type)'}")
    for m in idsp.memories(chk):
        i = int(m.get('index'))
        label = dict((s, l) for s, l, _, _ in SLOTS)[i]
        bits = []
        for c in [0, 4, 6]:
            for pb in idsp.output(m, c).findall('passBand'):
                if pb.findtext('Enable') != '1':
                    continue
                fc = pb.findtext('Fc')
                if fc == '2500':
                    code = int(pb.findtext('Slope'))
                    band = 'HP' if pb.findtext('hiLowType') == '0' else 'LP'
                    bits.append(f"ch{c+1}{band}:{CODE_TO_DB.get(code,'?')}dB/t{pb.findtext('filterType')}")
        print(f'{"mem "+str(i+1):>4} {label:<13} ' + '  '.join(bits))


if __name__ == '__main__':
    main()
