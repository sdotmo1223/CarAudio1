#!/usr/bin/env python3
"""
v9 - separate the crossovers. The biggest remaining SQ problem.

Found by inspection of v8: the mid drivers were almost full-range.

  front 8"   : 110 - 8000 Hz    (an 8" beams above ~1700 Hz)
  rear  6.5" : 110 - 5000 Hz    (a 6.5" beams above ~2100 Hz)
  front 3.5" : 200 - 2500 Hz
  rear  3.5" : 200 - 4200 Hz
  tweeters   : 2500 - up

=> FOUR drivers covered 200 Hz - 5 kHz simultaneously, two of them beaming.
That overlap is the likely origin of the broad 10 dB suck-out measured at
2.1-3.4 kHz, and it wrecks imaging and resolution regardless of EQ.

v9 gives each driver its own band:

  sub        : < ~110 Hz          (T2, own amp)
  8" / 6.5"  : 110  - 1200 Hz     midbass, below beaming
  3.5"       : 1000 - 2500 Hz     midrange
  tweeters   : 2500 Hz +          top

Everything else (delays, levels, EQ, warmth) is inherited from v8, so
A/B-ing v8 against v9 isolates the crossover change alone.
"""
import idsp

SRC = '2027-test-tune-v8.idsp'
OUT = '2027-test-tune-v9.idsp'
MEMORY_INDEX = '1'

# output index -> {hiLowType: new Fc}   ('0' = high-pass, '1' = low-pass)
XB = {
    4: {'0': 1000.0},                 # ch5 front 3.5": HP 200 -> 1000
    5: {'0': 1000.0, '1': 2500.0},    # ch6 rear 3.5":  HP 200->1000, LP 4200->2500
    6: {'1': 1200.0},                 # ch7 front 8":   LP 8000 -> 1200
    7: {'1': 1200.0},                 # ch8 rear 6.5":  LP 5000 -> 1200
}
ROLE = {4: 'ch5 front 3.5"', 5: 'ch6 rear 3.5"', 6: 'ch7 front 8"', 7: 'ch8 rear 6.5"'}


def main():
    root = idsp.load(SRC)
    mem = [m for m in idsp.memories(root) if m.get('index') == MEMORY_INDEX][0]

    print('crossover changes (v8 -> v9):')
    for idx, spec in sorted(XB.items()):
        o = idsp.output(mem, idx)
        for pb in o.findall('passBand'):
            hl = pb.findtext('hiLowType')
            if hl in spec:
                old = pb.findtext('Fc')
                new = spec[hl]
                pb.find('Fc').text = idsp.fmt(new, 0)
                band = 'HP' if hl == '0' else 'LP'
                print(f'  {ROLE[idx]:<16} {band} {old:>5} -> {idsp.fmt(new,0):>5} Hz')

    idsp.save(root, OUT)
    assert idsp.dumps(idsp.load(OUT)) == open(OUT, 'rb').read()
    print(f'\nwrote {OUT} (round-trips byte-exact)')

    # ---- verify separation -------------------------------------------------
    print('\nband coverage after v9 (memory slot 1):')
    roles = {0: 'tw1', 1: 'tw2', 2: 'tw3', 3: 'tw4', 4: 'front 3.5',
             5: 'rear 3.5', 6: 'front 8', 7: 'rear 6.5'}
    for f in [80, 200, 500, 800, 1200, 1600, 2000, 2500, 4000, 8000]:
        n = []
        for i in range(8):
            ok = True
            for pb in idsp.output(mem, i).findall('passBand'):
                if pb.findtext('Enable') != '1':
                    continue
                fc = float(pb.findtext('Fc'))
                if pb.findtext('hiLowType') == '0':
                    ok = ok and f >= fc * 0.5
                else:
                    ok = ok and f <= fc * 2.0
            if ok:
                n.append(roles[i])
        print(f'  {f:>5} Hz  {len(n)} driver(s): {", ".join(n) if n else "(none)"}')


if __name__ == '__main__':
    main()
