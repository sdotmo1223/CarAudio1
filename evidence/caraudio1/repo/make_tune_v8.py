#!/usr/bin/env python3
"""
v8 - bring back the warmth, without giving up the fixes.

User feedback on v7: alignment pleased; but "the other one had more overall
warmth... the midbass giving a nice hug", "I enjoy a good mid", and they want
more output while keeping SQ.

Diagnosis - where the warmth went (v7 vs the user's original tune):

  1. ch8 (rear 6.5", Orion midbass) gain -5.1 -> -11.1 dB. A 6 dB cut on a
     midbass driver. This is almost certainly the single biggest loss - the
     user had already said they loved how "full" the 6.5" sounded.
  2. ch8 high-pass 100 -> 140 Hz removed its 100-140 Hz contribution.
  3. Midrange EQ cuts at 359/691/1198/1486 Hz on all four mid channels
     (-1.5 to -3.1 dB) trimmed the midrange the user says they enjoy.
  4. ch7 (front 8") unchanged at -11.4, so no front midbass lift to compensate.

v8 (on top of v7 - alignment, crossovers, tweeter voicing all kept):

  * ch7 front 8"  : -11.4 -> -8.5 dB   (front midbass, keeps stage forward)
  * ch8 rear  6.5": -11.1 -> -6.5 dB   (restores the "full" 6.5")
  * ch8 high-pass : 140 -> 110 Hz      (fills 110-140 Hz)
  * mid EQ cuts   : halved on out4-7   (brings the midrange back)

The 3.5" high-pass stays at 200 Hz. It is rated from 200 Hz, and it now has
clean 8"/6.5" midbass underneath it instead of doing that work itself.
"""
import idsp

SRC = '2027-test-tune-v7.idsp'
OUT = '2027-test-tune-v8.idsp'
MEMORY_INDEX = '1'

GAIN_SET = {6: -8.5,    # ch7 front 8"
            7: -6.5}    # ch8 rear 6.5"
HP_SET = {7: 110.0}     # ch8 rear 6.5"
MID_EQ_SCALE = 0.5      # halve the midrange cuts on out4-7
MID_OUTPUTS = [4, 5, 6, 7]


def main():
    root = idsp.load(SRC)
    mem = [m for m in idsp.memories(root) if m.get('index') == MEMORY_INDEX][0]

    print('output level changes (v7 -> v8):')
    for idx, new in sorted(GAIN_SET.items()):
        o = idsp.output(mem, idx)
        old = float(o.findtext('gain'))
        o.find('gain').text = idsp.fmt(new, 1)
        print(f'  out{idx} (DSP ch{idx+1}): {old:+.1f} -> {new:+.1f} dB  ({new-old:+.1f})')

    print('\nhigh-pass changes:')
    for idx, new in sorted(HP_SET.items()):
        o = idsp.output(mem, idx)
        for pb in o.findall('passBand'):
            if pb.findtext('hiLowType') == '0':
                old = pb.findtext('Fc')
                pb.find('Fc').text = idsp.fmt(new, 0)
                print(f'  out{idx} (DSP ch{idx+1}): {old} -> {idsp.fmt(new,0)} Hz')

    print(f'\nmidrange EQ cuts x{MID_EQ_SCALE}:')
    for idx in MID_OUTPUTS:
        o = idsp.output(mem, idx)
        before, after = [], []
        for band in o.findall('eq'):
            g = float(band.findtext('Gain'))
            if g != 0:
                newg = round(g * MID_EQ_SCALE, 1)
                before.append(round(g, 1))
                after.append(newg)
                band.find('Gain').text = idsp.fmt(newg, 1)
        print(f'  out{idx} (DSP ch{idx+1}): {before} -> {after}')

    idsp.save(root, OUT)
    assert idsp.dumps(idsp.load(OUT)) == open(OUT, 'rb').read()
    print(f'\nwrote {OUT} (round-trips byte-exact)')

    print('\neffective level (spec sensitivity + gain), v7 -> v8:')
    spec = {'tweeter': 93.0, '3.5"': 85.0, '8"': 95.1, '6.5"': 97.0}
    for label, chans, key in [('tweeters (1-4)', [0, 1, 2, 3], 'tweeter'),
                              ('3.5" (5-6)', [4, 5], '3.5"'),
                              ('8" (7)', [6], '8"'),
                              ('6.5" (8)', [7], '6.5"')]:
        g = sum(float(idsp.output(mem, c).findtext('gain')) for c in chans) / len(chans)
        print(f'  {label:<16} {spec[key]:5.1f} {g:+6.1f} = {spec[key]+g:5.1f} dB')


if __name__ == '__main__':
    main()
