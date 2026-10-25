#!/usr/bin/env python3
"""
v7 - the complete tune: crossovers + levels + R&B EQ + REAL time alignment.

All eight speaker distances now come from the user's own measurements:

  ch1 driver front tweet   2'5"  =  73.7 cm
  ch2 pass   front tweet   3'10" = 116.8 cm
  ch3 pass   rear  tweet   3'1"  =  94.0 cm
  ch4 driver rear  tweet   1'2"  =  35.6 cm
  ch5 front 3.5"  L/R      95.2 cm average (sits beside the front tweeters)
  ch6 rear  3.5"  L/R      64.8 cm average
  ch7 front 8"    L/R     116.8 cm average
  ch8 rear  6.5"  L/R     106.7 cm average

Time alignment = delay each driver so all arrivals coincide. The reference is
the SUBWOOFER, because it runs off its own amp and cannot be delayed.

The sub distance has NOT been supplied. ESTIMATED at 250 cm (a 12" T2 in a
custom trunk enclosure in a 5.1 m sedan). Every delay shifts by 1 ms per
34.3 cm of sub distance error, so this is the one number to confirm.
"""
import idsp

SRC = '2027-test-tune-v6.idsp'
OUT = '2027-test-tune-v7.idsp'
MEMORY_INDEX = '1'

C_CM_PER_MS = 34.3          # speed of sound, 20 C
SUB_CM = 250.0              # ESTIMATE - confirm

# output index (DSP ch - 1) -> measured distance in cm
DIST = {
    0: 73.66,      # ch1 driver front tweet
    1: 116.84,     # ch2 passenger front tweet
    2: 93.98,      # ch3 passenger rear tweet
    3: 35.56,      # ch4 driver rear tweet
    4: (73.66 + 116.84) / 2,    # ch5 front 3.5" L/R
    5: (93.98 + 35.56) / 2,     # ch6 rear 3.5" L/R
    6: (106.68 + 127.00) / 2,   # ch7 front 8" L/R
    7: (86.36 + 127.00) / 2,    # ch8 rear 6.5" L/R
}
ROLE = {0: 'ch1 driver front tweet', 1: 'ch2 pass front tweet',
        2: 'ch3 pass rear tweet', 3: 'ch4 driver rear tweet',
        4: 'ch5 front 3.5" L/R', 5: 'ch6 rear 3.5" L/R',
        6: 'ch7 front 8" L/R', 7: 'ch8 rear 6.5" L/R'}


def main():
    root = idsp.load(SRC)
    mem = [m for m in idsp.memories(root) if m.get('index') == MEMORY_INDEX][0]

    print(f'reference = subwoofer at an ESTIMATED {SUB_CM:.0f} cm\n')
    print(f'{"ch":>3} {"role":<26}{"dist":>9}{"old":>8}{"new":>8}{"delta":>8}')
    for idx in sorted(DIST):
        d = DIST[idx]
        new = round((SUB_CM - d) / C_CM_PER_MS, 1)
        o = idsp.output(mem, idx)
        old = float(o.findtext('delay'))
        o.find('delay').text = idsp.fmt(new, 1)
        print(f'{idx+1:>3} {ROLE[idx]:<26}{d:>8.1f}cm{old:>7.1f}ms{new:>7.1f}ms{new-old:>+7.1f}')

    idsp.save(root, OUT)
    assert idsp.dumps(idsp.load(OUT)) == open(OUT, 'rb').read()
    print(f'\nwrote {OUT} (round-trips byte-exact)')


if __name__ == '__main__':
    main()
