#!/usr/bin/env python3
"""
v12 - target Linkwitz-Riley 4th order (24 dB/oct) everywhere.

User confirms the FilterType selection is Linkwitz-Riley on every channel. So
file filterType=1 == Linkwitz-Riley (and the front 3.5" LP, which was type 0,
was the lone Butterworth - v11 already corrected it).

WHY THE ORDER MATTERS WITH LINKWITZ-RILEY
  LR2  (12 dB/oct)  sums flat ONLY with ONE driver polarity-inverted
  LR4  (24 dB/oct)  sums flat with both drivers in phase
  LR8  (48 dB/oct)  same as LR4, just steeper

Every output in this file has revert=0 - i.e. nothing is inverted. That means:
  * at LR4 or LR8 the crossovers will sum correctly
  * at LR2 they would NULL at the crossover frequency
So if any handoff is sitting on LR2, that alone could be the 2.8 kHz dip.

SLOPE CODE ASSUMPTION
The file holds slope as codes 1/2/3 and Linkwitz-Riley is an even-order
alignment, so the LR list is almost certainly 12/24/36(or 48). That makes
  code 1 = LR2 (12)   code 2 = LR4 (24)   code 3 = LR6/LR8 (36 or 48)
v11 used code 3 (steeper than I intended). v12 uses code 2 = LR4 = 24 dB/oct,
which is the sweet spot. VERIFY by reading the dB/oct the software displays.
"""
import idsp

SRC = '2027-test-tune-v11.idsp'
OUT = '2027-test-tune-v12.idsp'
MEMORY_INDEX = '1'

SLOPE = '2'          # assumed LR4 = 24 dB/oct
FILTER_TYPE = '1'    # Linkwitz-Riley


def main():
    root = idsp.load(SRC)
    mem = [m for m in idsp.memories(root) if m.get('index') == MEMORY_INDEX][0]

    print('slope change (v11 -> v12):  code 3 -> code 2 on every crossover')
    for i in range(8):
        for pb in idsp.output(mem, i).findall('passBand'):
            if pb.findtext('Enable') != '1':
                continue
            band = 'HP' if pb.findtext('hiLowType') == '0' else 'LP'
            old = pb.findtext('Slope')
            pb.find('Slope').text = SLOPE
            pb.find('filterType').text = FILTER_TYPE
            print(f"  ch{i+1:<3} {band} {pb.findtext('Fc'):>5} Hz   slope {old} -> {SLOPE}   type {FILTER_TYPE}")

    idsp.save(root, OUT)
    assert idsp.dumps(idsp.load(OUT)) == open(OUT, 'rb').read()
    print(f'\nwrote {OUT} (round-trips byte-exact)')

    combos = {(pb.findtext('Slope'), pb.findtext('filterType'), pb.findtext('butt_Type'))
              for i in range(8) for pb in idsp.output(mem, i).findall('passBand')
              if pb.findtext('Enable') == '1'}
    print('distinct (slope, filterType, butt_Type):', sorted(combos))


if __name__ == '__main__':
    main()
