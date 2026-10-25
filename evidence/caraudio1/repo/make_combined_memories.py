#!/usr/bin/env python3
"""
Build ONE .idsp whose four memory slots each hold a different version, so the
user can A/B by switching memory in the DSP display instead of reloading files.

  DSP memory 1  (xml index 0)  = ORIGINAL tune       - their starting point
  DSP memory 2  (xml index 1)  = v7  time-aligned     - alignment only
  DSP memory 3  (xml index 2)  = v8  + warmth         - midbass restored
  DSP memory 4  (xml index 3)  = v9  + separated XO   - crossovers fixed

mainMemory is pointed at the v9 slot so the file loads on the newest version.

Everything shared (routing, source matrix, version header) comes from the
original file. Each version contributed its whole memory[1] subtree, re-indexed.
"""
import copy

import idsp

BASE = '2027-base.idsp'
OUT = '2027-AB-4memories.idsp'

# (xml slot index, label, source file, source memory index)
SLOTS = [
    (0, 'ORIGINAL tune',   BASE,                       '1'),
    (1, 'v7 time-aligned', '2027-test-tune-v7.idsp',   '1'),
    (2, 'v8 + warmth',     '2027-test-tune-v8.idsp',   '1'),
    (3, 'v9 + sep. XO',    '2027-test-tune-v9.idsp',   '1'),
]
ACTIVE_SLOT = 3          # mainMemory -> the newest version


def main():
    root = idsp.load(BASE)

    for slot, label, src, src_idx in SLOTS:
        src_root = idsp.load(src)
        new_mem = copy.deepcopy(
            [m for m in idsp.memories(src_root) if m.get('index') == src_idx][0])
        new_mem.set('index', str(slot))
        # swap it into the container, replacing the existing memory element
        for i, child in enumerate(list(root)):
            if child.tag == 'memory' and child.get('index') == str(slot):
                root[i] = new_mem
                break
        print(f'slot {slot} <- {label:<18} ({src})')

    root.find('RunStatus').find('mainMemory').text = str(ACTIVE_SLOT)
    print(f'\nmainMemory -> {ACTIVE_SLOT} (loads on "{SLOTS[ACTIVE_SLOT][1]}")')

    idsp.save(root, OUT)
    assert idsp.dumps(idsp.load(OUT)) == open(OUT, 'rb').read()
    print(f'wrote {OUT} (round-trips byte-exact)')

    # ---- readable summary of what each slot actually contains -------------
    chk = idsp.load(OUT)
    role = {0: 'tw', 1: 'tw', 2: 'tw', 3: 'tw', 4: 'fr 3.5', 5: 'rr 3.5',
            6: 'fr 8"', 7: 'rr 6.5'}
    print(f"\n{'slot':>4} {'label':<17}{'delay ch1-8':<34}{'gain ch1-8'}")
    for m in idsp.memories(chk):
        idx = m.get('index')
        label = dict((s, l) for s, l, _, _ in SLOTS)[int(idx)]
        delays = '/'.join(idsp.output(m, i).findtext('delay') for i in range(8))
        gains = '/'.join(idsp.output(m, i).findtext('gain') for i in range(8))
        print(f'{int(idx)+1:>4} {label:<17}{delays:<34}{gains}')

    print(f"\n{'slot':>4} {'label':<17} HP/LP per channel (ch5,6,7,8)")
    for m in idsp.memories(chk):
        idx = int(m.get('index'))
        label = dict((s, l) for s, l, _, _ in SLOTS)[idx]
        parts = []
        for c in [4, 5, 6, 7]:
            hp = lp = '-'
            for pb in idsp.output(m, c).findall('passBand'):
                if pb.findtext('Enable') == '1':
                    if pb.findtext('hiLowType') == '0':
                        hp = pb.findtext('Fc')
                    else:
                        lp = pb.findtext('Fc')
            parts.append(f'{hp}/{lp}')
        print(f'{idx+1:>4} {label:<17} ' + '  '.join(parts))


if __name__ == '__main__':
    main()
