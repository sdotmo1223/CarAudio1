#!/usr/bin/env python3
"""
v10 - the middle ground: keep the "sprinkle", lose the beaming.

User compared v8 and v9:
  v8 (memory 3): "well rounded, better"
  v9 (memory 4): "boom/punchier but lacks that sprinkle"

That is exactly right, and it names the mechanism. v9 low-passed the 8"/6.5"
at 1200 Hz, which removed the two most efficient drivers in the car (95.1 and
97 dB) from 1200 Hz upward. The band that carries presence and detail - roughly
1.2 to 4 kHz - was left to the 85 dB 3.5"s alone. Less energy up there makes
the low end feel louder by contrast: "boomier, no sprinkle."

v8 keeps the sprinkle because those drivers still play high. But they BEAM
above ~1.7 kHz (8") and ~2.1 kHz (6.5"), which is what the overlap costs.

v10 tries to keep both: leave everything from 110-2500 Hz exactly as v8 has it
(so the sprinkle survives untouched) and only remove the 8"/6.5" from ABOVE
2500 Hz, where the tweeters live and where the beaming is worst.

  ch6 rear  3.5": LP 4200 -> 2500
  ch7 front 8"  : LP 8000 -> 2500
  ch8 rear  6.5": LP 5000 -> 2500

Nothing else changes from v8. The 3.5" high-pass stays at 200, the alignment,
warmth, levels and EQ are all v8.
"""
import idsp

SRC = '2027-test-tune-v8.idsp'
OUT = '2027-test-tune-v10.idsp'
MEMORY_INDEX = '1'

# output index -> new low-pass Fc
LP_SET = {5: 2500.0,    # ch6 rear 3.5"
          6: 2500.0,    # ch7 front 8"
          7: 2500.0}    # ch8 rear 6.5"
ROLE = {5: 'ch6 rear 3.5"', 6: 'ch7 front 8"', 7: 'ch8 rear 6.5"'}


def main():
    root = idsp.load(SRC)
    mem = [m for m in idsp.memories(root) if m.get('index') == MEMORY_INDEX][0]

    print('low-pass changes (v8 -> v10):')
    for idx, new in sorted(LP_SET.items()):
        for pb in idsp.output(mem, idx).findall('passBand'):
            if pb.findtext('hiLowType') == '1' and pb.findtext('Enable') == '1':
                old = pb.findtext('Fc')
                pb.find('Fc').text = idsp.fmt(new, 0)
                print(f'  {ROLE[idx]:<16} LP {old:>5} -> {idsp.fmt(new,0):>5} Hz')

    idsp.save(root, OUT)
    assert idsp.dumps(idsp.load(OUT)) == open(OUT, 'rb').read()
    print(f'\nwrote {OUT} (round-trips byte-exact)')

    print('\nwhat each version asks of the 8"/6.5":')
    for label, src in [('v8  (well rounded)', '2027-test-tune-v8.idsp'),
                       ('v9  (no sprinkle)', '2027-test-tune-v9.idsp'),
                       ('v10 (this one)', OUT)]:
        r = idsp.load(src)
        m = [x for x in idsp.memories(r) if x.get('index') == '1'][0]
        out = []
        for c in [6, 7]:
            lp = hp = None
            for pb in idsp.output(m, c).findall('passBand'):
                if pb.findtext('Enable') == '1':
                    if pb.findtext('hiLowType') == '1':
                        lp = pb.findtext('Fc')
                    else:
                        hp = pb.findtext('Fc')
            out.append(f'{hp}-{lp}')
        print(f'  {label:<22} 8"/6.5" cover {out[0]} / {out[1]} Hz')


if __name__ == '__main__':
    main()
