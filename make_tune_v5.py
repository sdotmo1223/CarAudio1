#!/usr/bin/env python3
"""
Test tune v5 - fix the LEVEL balance.

Full sensitivity picture, from manufacturer specs:

  BZRK RS-T150 1" tweeter  : 1.5k-25k Hz, 93.0 dB  -> ch1-4
  NVX XQS3v2 3.5"          : 200-18k Hz,  85.0 dB  -> ch5, ch6
  Deaf Bonce AP-M81SE 8"   : 110-10k Hz,  95.1 dB  -> ch7
  Orion XTR XTX654 6.5"    : 140-12k Hz,  97.0 dB  -> ch8

That is a **12 dB sensitivity spread**. Effective levels with the tune's
existing gains:

  tweeters   93.0 - 1.65 = 91.4   -> ~6 dB HOT
  3.5"       85.0 - 0    = 85.0   <- the reference
  8"         95.1 - 11.4 = 83.7   (already matched)
  6.5"       97.0 - 5.1  = 91.9   -> ~7 dB HOT

So the tweeters and the rear 6.5" are both running ~6-7 dB hot against the
3.5"s. That is the single cause of the user's two complaints - "bright" and the
rear-biased stage. Fix it with LEVEL, not EQ.

v5 = v4 (corrected crossovers + R&B house curve) with those two groups pulled
down to match the 3.5"s. Nothing else touched.
"""
import idsp

SRC = '2027-test-tune-v4.idsp'
OUT = '2027-test-tune-v5.idsp'
MEMORY_INDEX = '1'

# output index (DSP channel - 1) -> dB to remove
GAIN_DELTA = {0: -4.0,    # ch1 tweeter
              1: -4.0,    # ch2 tweeter
              2: -4.0,    # ch3 tweeter
              3: -4.0,    # ch4 tweeter
              7: -6.0}    # ch8 rear 6.5"

ROLE = {0: 'ch1 tweeter', 1: 'ch2 tweeter', 2: 'ch3 tweeter', 3: 'ch4 tweeter',
        7: 'ch8 rear 6.5"'}


def main():
    root = idsp.load(SRC)
    mem = [m for m in idsp.memories(root) if m.get('index') == MEMORY_INDEX][0]

    print('level changes (v4 -> v5):')
    for idx, delta in sorted(GAIN_DELTA.items()):
        o = idsp.output(mem, idx)
        old = float(o.findtext('gain'))
        new = old + delta
        o.find('gain').text = idsp.fmt(new, 1)
        print(f'  {ROLE[idx]:<16} gain {old:+.1f} -> {new:+.1f} dB')

    idsp.save(root, OUT)
    assert idsp.dumps(idsp.load(OUT)) == open(OUT, 'rb').read()
    print(f'\nwrote {OUT} (round-trips byte-exact)')

    # effective levels after the change
    spec = {'tweeter': 93.0, '3.5"': 85.0, '8"': 95.1, '6.5"': 97.0}
    print('\neffective level after v5 (spec + gain):')
    for label, chans, key in [('tweeters (1-4)', [0, 1, 2, 3], 'tweeter'),
                              ('3.5" (5-6)', [4, 5], '3.5"'),
                              ('8" (7)', [6], '8"'),
                              ('6.5" (8)', [7], '6.5"')]:
        g = sum(float(idsp.output(mem, c).findtext('gain')) for c in chans) / len(chans)
        print(f'  {label:<16} {spec[key]:5.1f} {g:+6.1f}  =  {spec[key]+g:5.1f} dB')


if __name__ == '__main__':
    main()
