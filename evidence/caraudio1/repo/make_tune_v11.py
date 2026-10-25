#!/usr/bin/env python3
"""
v11 - MATCH every crossover that meets at 2500 Hz.

The user confirmed the DSP's slope dropdown offers 6/12/18/.../48 dB/oct with
Butterworth or Linkwitz-Riley. The file stores that as Slope codes 1, 2, 3 -
i.e. the three SHALLOWEST settings available. So every crossover in the car is
shallow, which is why four drivers overlapped across three octaves.

Worse, they were also MISMATCHED at the 2500 Hz handoff:

  tweeters      HP 2500   slope 1   type 1
  front 3.5"    LP 2500   slope 2   type 0   <- different slope AND type
  rear  3.5"    LP 2500   slope 3   type 1
  front 8"      LP 2500   slope 1   type 1
  rear  6.5"    LP 2500   slope 1   type 1

Three different slopes and two different filter types meeting at one frequency
cannot sum flat. That is very likely the 2.8 kHz suck-out.

v11 makes every crossover use slope 3 + filter type 1 - a combination already
proven valid in this exact file, because the rear 3.5"'s low-pass already uses
it. Nothing else changes from v10.

Effect:
  * all 4 mid low-passes and all 4 tweeter high-passes now match exactly
  * everything got STEEPER (the tweeter high-pass especially, from 1 to 3)
  * the 1200-2500 Hz region - where the "sprinkle" lives - is untouched
"""
import idsp

SRC = '2027-test-tune-v10.idsp'
OUT = '2027-test-tune-v11.idsp'
MEMORY_INDEX = '1'

SLOPE = '3'
FILTER_TYPE = '1'


def main():
    root = idsp.load(SRC)
    mem = [m for m in idsp.memories(root) if m.get('index') == MEMORY_INDEX][0]

    print('crossover slope/type normalisation (v10 -> v11):')
    print(f"  {'ch':>3} {'band':<5}{'Fc':>7}   slope/type before -> after")
    for i in range(8):
        for pb in idsp.output(mem, i).findall('passBand'):
            if pb.findtext('Enable') != '1':
                continue
            band = 'HP' if pb.findtext('hiLowType') == '0' else 'LP'
            old = (pb.findtext('Slope'), pb.findtext('filterType'))
            pb.find('Slope').text = SLOPE
            pb.find('filterType').text = FILTER_TYPE
            flag = '' if old == (SLOPE, FILTER_TYPE) else '  <- changed'
            print(f"  {i+1:>3} {band:<5}{pb.findtext('Fc'):>7}   "
                  f"{old[0]}/{old[1]} -> {SLOPE}/{FILTER_TYPE}{flag}")

    idsp.save(root, OUT)
    assert idsp.dumps(idsp.load(OUT)) == open(OUT, 'rb').read()
    print(f'\nwrote {OUT} (round-trips byte-exact)')

    chk = idsp.load(OUT)
    m = [x for x in idsp.memories(chk) if x.get('index') == MEMORY_INDEX][0]
    combos = set()
    for i in range(8):
        for pb in idsp.output(m, i).findall('passBand'):
            if pb.findtext('Enable') == '1':
                combos.add((pb.findtext('Slope'), pb.findtext('filterType'),
                            pb.findtext('butt_Type')))
    print('distinct (slope, filterType, butt_Type) now in use:', sorted(combos))


if __name__ == '__main__':
    main()
