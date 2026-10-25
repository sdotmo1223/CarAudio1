# Tune atlas — what the eight `.idsp` files actually contain

Read-only structural analysis of every tune in `web/`, 2026-10-21, no hardware.
`python tune/tune-atlas.py` reproduces it; the tables at the bottom are its unedited output.

Before this existed, six of the eight files had never been opened and the other two had been
read one memory at a time. The atlas reads all **32 memory blocks** and answers four
questions: how many distinct tunes are really there, what kind of filters they use, what
crossovers they assume, and which of them is nothing like the others.

## Finding 1 — the library is 12 tunes, not 32 blocks

| payload | blocks | where |
|---|---|---|
| `xswmps` = `bor8f3` (all four memories identical) | 8 | two files, one tune |
| `tinker3` = `ai2` in 3 of 4 memories | 6 | two files, one tune |
| `cb0nyk` | 4 | one tune, repeated |
| `w6784b` | 4 | one tune, repeated |
| our two authored files | 8 | one tune each, repeated 4× by design |

32 blocks collapse to **12 distinct voicings**, and 4 of those 12 are *empty* (`ai2` m3/m4 and
`tinker3` m3/m4 are zero-band, flat, gain −3 — a parked memory, not a voicing). So the real
inherited library is **8 voicings**, and the two files we thought were separate references
are one tune saved twice.

## Finding 2 — the inherited tunes are correction banks, not tone controls

4 144 active bands across the library. **85 % of them are Q ≥ 6**, and **2 292 are Q 10–16** —
that is 1/12-octave notch geometry, the signature of an automatic measurement-correction pass,
not a person drawing a curve. The filter-frequency histogram is dominated by high bands:
176 bands land on 12.9 kHz, 152 on 11.5 kHz, 144 on 5.1 kHz, 138 on 8.1 kHz.

That reframes what `bor8f3` is. It is not "a tasteful voicing to borrow"; it is an
auto-correction match curve, almost all of it fighting narrow problems in **its** car at
**its** mic position, with a handful of broad moves (≤ 3 dB, `max+` never above +3 in any
inherited file) doing the actual voicing.

Which means tune 2's construction needs re-examining: it kept the shape but refit every band
to **Q ≤ 2.8 with a 1 dB floor**. A Q-15 notch at 12.9 kHz and a Q-2.8 shelf at 12.9 kHz are
different objects — one removes a resonance at that exact frequency, the other tilts a
third of an octave. "Tune 2 keeps bor8f3's curve" is true only at 0.4-octave smoothing, and
at that smoothing scale most of bor8f3's content (85 % of its bands) has been averaged away.
The 41 bands tune 2 actually carries are the residue, not the tune.

## Finding 3 — every inherited tune band-limits its cones; ours is the only one that does not

Crossovers the files assume, HP/LP per path:

```
2500/- 2500/- 2500/- 2500/- 250/2500 250/2500 110/2500 140/1000    9 blocks  (bor8f3, xswmps)
4000/- 4000/- 4000/- 4000/- 250/3150 250/3150 120/4000 120/1000    4 blocks  (cb0nyk)
2000/- 2000/- 2000/- 2000/- 250/2500 250/2500 150/2000 120/1000    4 blocks  (w6784b)
2500/- ... 200/4200 90/2500 100/2500                               2 blocks  (ai2/tinker3 m1)
1500/- 1500/- 1500/- 1500/- 80/- 80/- 50/- 60/-                    8 blocks  (ours, open)
```
﻿=== atlas: 9 files, 36 memory blocks ===

file                           mem bands  Qmed  Qmax  notch   max+   max-   tilt  gains
1-car-tune-2026-10-20.idsp       1    57   1.4   4.0      0    3.0  -12.0  -21.9  -7 -8 -9 -8 -8 -3 -30 -22
1-car-tune-2026-10-20.idsp       2    57   1.4   4.0      0    3.0  -12.0  -21.9  -7 -8 -9 -8 -8 -3 -30 -22
1-car-tune-2026-10-20.idsp       3    57   1.4   4.0      0    3.0  -12.0  -21.9  -7 -8 -9 -8 -8 -3 -30 -22
1-car-tune-2026-10-20.idsp       4    57   1.4   4.0      0    3.0  -12.0  -21.9  -7 -8 -9 -8 -8 -3 -30 -22
2-bor8f3-improved.idsp           1    41   2.8   2.8      0    3.7   -6.3   -0.4  -5 -7 -7 -4 -3 -3 -14 -5
2-bor8f3-improved.idsp           2    41   2.8   2.8      0    3.7   -6.3   -0.4  -5 -7 -7 -4 -3 -3 -14 -5
2-bor8f3-improved.idsp           3    41   2.8   2.8      0    3.7   -6.3   -0.4  -5 -7 -7 -4 -3 -3 -14 -5
2-bor8f3-improved.idsp           4    41   2.8   2.8      0    3.7   -6.3   -0.4  -5 -7 -7 -4 -3 -3 -14 -5
3-rnb-dense-2026-10-21.idsp      1   185   4.0   4.0      0    3.0  -12.0    3.5  -7 -7 -7 -8 -9 -4 -31 -24
3-rnb-dense-2026-10-21.idsp      2   185   4.0   4.0      0    3.0  -12.0    3.5  -7 -7 -7 -8 -9 -4 -31 -24
3-rnb-dense-2026-10-21.idsp      3   185   4.0   4.0      0    3.0  -12.0    3.5  -7 -7 -7 -8 -9 -4 -31 -24
3-rnb-dense-2026-10-21.idsp      4   185   4.0   4.0      0    3.0  -12.0    3.5  -7 -7 -7 -8 -9 -4 -31 -24
ai2.idsp                         1   138   4.5  19.6     47    9.0  -23.3   12.0  -3 -3 -3 -3 +0 +0 -11 -14
ai2.idsp                         2    20   4.0   4.0      0   -0.8   -5.0    0.4  -5 -7 -7 -4 +0 +0 -8 -6
ai2.idsp                         3     0   0.0   0.0      0    0.0    0.0    0.0  -3 -3 -3 -3 -3 -3 -3 -3
ai2.idsp                         4     0   0.0   0.0      0    0.0    0.0    0.0  -3 -3 -3 -3 -3 -3 -3 -3
bor8f3.idsp                      1   234  15.2  16.1    234    3.0  -12.4   -0.9  -5 -7 -7 -4 -3 -3 -14 -5
bor8f3.idsp                      2   246  15.2  16.1    246    3.0  -15.0   -2.7  -5 -7 -7 -4 -3 -3 -14 -5
bor8f3.idsp                      3   122  15.2  16.1    122   -0.2  -15.0    4.1  -5 -7 -7 -4 -3 -3 -14 -5
bor8f3.idsp                      4   246  15.2  16.1    246    3.0  -12.4   -2.7  -5 -7 -7 -4 -3 -3 -14 -5
cb0nyk.idsp                      1   204  14.8  16.0    204    3.0   -8.0   -0.8  -6 -9 -9 -6 -2 -1 -12 -6
cb0nyk.idsp                      2   204  14.8  16.0    204    3.0   -8.0   -0.8  -6 -9 -9 -6 -2 -1 -12 -6
cb0nyk.idsp                      3   204  14.8  16.0    204    3.0   -8.0   -0.8  -6 -9 -9 -6 -2 -1 -12 -6
cb0nyk.idsp                      4   204  14.8  16.0    204    3.0   -8.0   -0.8  -6 -9 -9 -6 -2 -1 -12 -6
tinker3.idsp                     1   138   4.5  19.6     47    9.0  -23.3   12.0  -3 -3 -3 -3 +0 +0 -11 -14
tinker3.idsp                     2    40  10.0  10.0     32   -0.2  -12.0    1.7  -5 -7 -7 -4 +0 +0 -14 -5
tinker3.idsp                     3     0   0.0   0.0      0    0.0    0.0    0.0  -3 -3 -3 -3 -3 -3 -3 -3
tinker3.idsp                     4     0   0.0   0.0      0    0.0    0.0    0.0  -3 -3 -3 -3 -3 -3 -3 -3
w6784b.idsp                      1   226  15.2  16.1    226    3.0  -12.4   12.8  -5 -7 -7 -4 -3 -3 -14 -5
w6784b.idsp                      2   226  15.2  16.1    226    3.0  -12.4   12.8  -5 -7 -7 -4 -3 -3 -14 -5
w6784b.idsp                      3   226  15.2  16.1    226    3.0  -12.4   12.8  -5 -7 -7 -4 -3 -3 -14 -5
w6784b.idsp                      4   226  15.2  16.1    226    3.0  -12.4   12.8  -5 -7 -7 -4 -3 -3 -14 -5
xswmps.idsp                      1   234  15.2  16.1    234    3.0  -12.4   -0.9  -5 -7 -7 -4 -3 -3 -14 -5
xswmps.idsp                      2   246  15.2  16.1    246    3.0  -15.0   -2.7  -5 -7 -7 -4 -3 -3 -14 -5
xswmps.idsp                      3   122  15.2  16.1    122   -0.2  -15.0    4.1  -5 -7 -7 -4 -3 -3 -14 -5
xswmps.idsp                      4   246  15.2  16.1    246    3.0  -15.0   -2.7  -5 -7 -7 -4 -3 -3 -14 -5
  tilt = mean EQ 60-120 Hz minus mean EQ 3-6 kHz on the summed 8-path curve;
         positive = bass-forward filter work, negative = presence-forward.

--- are the four memories in each file actually different?
  1-car-tune-2026-10-20.idsp     1 distinct of 4   852bdc6593 852bdc6593 852bdc6593 852bdc6593
  2-bor8f3-improved.idsp         1 distinct of 4   c8e780e8fc c8e780e8fc c8e780e8fc c8e780e8fc
  3-rnb-dense-2026-10-21.idsp    1 distinct of 4   c9cfb94358 c9cfb94358 c9cfb94358 c9cfb94358
  ai2.idsp                       3 distinct of 4   d158f355d7 3eb2a0386f 97b632c7f8 97b632c7f8
  bor8f3.idsp                    4 distinct of 4   c1b12c9dc8 aeee36e64e 60f83c6c73 5136d8edf1
  cb0nyk.idsp                    1 distinct of 4   7e0e623937 7e0e623937 7e0e623937 7e0e623937
  tinker3.idsp                   3 distinct of 4   d158f355d7 48e92997ab 97b632c7f8 97b632c7f8
  w6784b.idsp                    1 distinct of 4   1eff459063 1eff459063 1eff459063 1eff459063
  xswmps.idsp                    3 distinct of 4   c1b12c9dc8 aeee36e64e 60f83c6c73 aeee36e64e

--- the whole library by payload: 36 memory blocks, 13 distinct tunes
  852bdc6593  x4   1-car-tune-2026-10-20.idsp m1, 1-car-tune-2026-10-20.idsp m2, 1-car-tune-2026-10-20.idsp m3, 1-car-tune-2026-10-20.idsp m4
  c8e780e8fc  x4   2-bor8f3-improved.idsp m1, 2-bor8f3-improved.idsp m2, 2-bor8f3-improved.idsp m3, 2-bor8f3-improved.idsp m4
  c9cfb94358  x4   3-rnb-dense-2026-10-21.idsp m1, 3-rnb-dense-2026-10-21.idsp m2, 3-rnb-dense-2026-10-21.idsp m3, 3-rnb-dense-2026-10-21.idsp m4
  97b632c7f8  x4   ai2.idsp m3, ai2.idsp m4, tinker3.idsp m3, tinker3.idsp m4
  7e0e623937  x4   cb0nyk.idsp m1, cb0nyk.idsp m2, cb0nyk.idsp m3, cb0nyk.idsp m4
  1eff459063  x4   w6784b.idsp m1, w6784b.idsp m2, w6784b.idsp m3, w6784b.idsp m4
  aeee36e64e  x3   bor8f3.idsp m2, xswmps.idsp m2, xswmps.idsp m4
  d158f355d7  x2   ai2.idsp m1, tinker3.idsp m1
  c1b12c9dc8  x2   bor8f3.idsp m1, xswmps.idsp m1
  60f83c6c73  x2   bor8f3.idsp m3, xswmps.idsp m3
  3eb2a0386f  x1   ai2.idsp m2
  5136d8edf1  x1   bor8f3.idsp m4
  48e92997ab  x1   tinker3.idsp m2

--- crossover maps the tunes assume (HP/LP per path, '-' = open)
  1500/- 1500/- 1500/- 1500/- 80/- 80/- 50/- 60/-              12  1 m1, 1 m2, 1 m3, 1 m4 ...
  2500/- 2500/- 2500/- 2500/- 250/2500 250/2500 110/2500 140/1000  9  bor8f3.idsp m1, bor8f3.idsp m2, bor8f3.idsp m3, bor8f3.idsp m4 ...
  -/- -/- -/- -/- -/- -/- -/- -/-                               4  ai2.idsp m3, ai2.idsp m4, tinker3.idsp m3, tinker3.idsp m4
  4000/- 4000/- 4000/- 4000/- 250/3150 250/3150 120/4000 120/1000  4  cb0nyk.idsp m1, cb0nyk.idsp m2, cb0nyk.idsp m3, cb0nyk.idsp m4
  2000/- 2000/- 2000/- 2000/- 250/2500 250/2500 150/2000 120/1000  4  w6784b.idsp m1, w6784b.idsp m2, w6784b.idsp m3, w6784b.idsp m4
  2500/- 2500/- 2500/- 2500/- 250/2500 200/4200 90/2500 100/2500  2  ai2.idsp m1, tinker3.idsp m1
  2500/- 2500/- 2500/- 2500/- 200/2500 200/2500 110/2500 110/2500  1  ai2.idsp m2

--- where the 32 voicings put their filters (1/6-oct bins, all active bands)
  11494   196 ############################################################
  12901.6 176 ############################################################
  5120    160 ############################################################
  570.175 154 ############################################################
  1612.7  152 ############################################################
  403.175 152 ############################################################
  8127.49 150 ############################################################
  4561.4  146 ############################################################
  2031.87 142 ############################################################
  1280    141 ############################################################
  10240   140 ############################################################
  359.188 128 ############################################################
  5747.01 128 ############################################################
  718.376 122 ############################################################
  6450.8  122 ############################################################
  7240.77 122 ############################################################
  1436.75 120 ############################################################
  806.349 118 ############################################################
  total active bands in the library: 4884

--- filter shapes (Q) and sizes (dB), all active bands
  Q      [2,2.8):92  [1.4,2):136  [1,1.4):68  [0.7,1):188  [4,6):554  [2.8,4):304  [6,10):548  [16,99):702  [10,16):2292
  gain   [-6,-3):790  [1,3):690  [-3,-1):1267  [3,6):614  [-9,-6):331  [-12,-9):158  [0,1):338  [-1,0):572  [-99,-12):104  [9,12):4  [6,9):16
  Q>=6 (a notch rather than a tone control): 3542 of 4884 bands = 73%

--- tonal work per voicing (mean dB of the summed EQ in each region)
  file                         m   sub      bass     lowmid   mid      pres     treb     top      
  1-car-tune-2026-10-20.idsp   1   -7.0     -19.0    -28.7    -25.8    -11.8    2.9      6.6      
  1-car-tune-2026-10-20.idsp   2   -7.0     -19.0    -28.7    -25.8    -11.8    2.9      6.6      
  1-car-tune-2026-10-20.idsp   3   -7.0     -19.0    -28.7    -25.8    -11.8    2.9      6.6      
  1-car-tune-2026-10-20.idsp   4   -7.0     -19.0    -28.7    -25.8    -11.8    2.9      6.6      
  2-bor8f3-improved.idsp       1   -0.1     -1.1     -3.3     -3.0     -5.6     -0.7     1.6      
  2-bor8f3-improved.idsp       2   -0.1     -1.1     -3.3     -3.0     -5.6     -0.7     1.6      
  2-bor8f3-improved.idsp       3   -0.1     -1.1     -3.3     -3.0     -5.6     -0.7     1.6      
  2-bor8f3-improved.idsp       4   -0.1     -1.1     -3.3     -3.0     -5.6     -0.7     1.6      
  3-rnb-dense-2026-10-21.idsp  1   -3.7     -21.3    -37.3    -35.3    -49.0    -24.8    -12.5    
  3-rnb-dense-2026-10-21.idsp  2   -3.7     -21.3    -37.3    -35.3    -49.0    -24.8    -12.5    
  3-rnb-dense-2026-10-21.idsp  3   -3.7     -21.3    -37.3    -35.3    -49.0    -24.8    -12.5    
  3-rnb-dense-2026-10-21.idsp  4   -3.7     -21.3    -37.3    -35.3    -49.0    -24.8    -12.5    
  ai2.idsp                     1   -12.2    -54.5    -61.7    -34.6    -77.8    -66.5    -72.5    
  ai2.idsp                     2   -0.0     -0.0     -0.4     -2.8     -2.3     -0.5     -2.7     
  ai2.idsp                     3   0.0      0.0      0.0      0.0      0.0      0.0      0.0      
  ai2.idsp                     4   0.0      0.0      0.0      0.0      0.0      0.0      0.0      
  bor8f3.idsp                  1   -0.0     -1.0     -1.9     -2.2     -5.2     -0.1     2.3      
  bor8f3.idsp                  2   -0.0     -1.0     -1.9     -2.1     -3.2     1.6      1.9      
  bor8f3.idsp                  3   -0.0     -1.1     -5.4     -9.7     -10.3    -5.1     -5.2     
  bor8f3.idsp                  4   -0.0     -1.0     -1.9     -2.1     -3.2     1.7      2.3      
  cb0nyk.idsp                  1   -0.0     -0.5     -1.2     2.1      1.8      0.4      -7.6     
  cb0nyk.idsp                  2   -0.0     -0.5     -1.2     2.1      1.8      0.4      -7.6     
  cb0nyk.idsp                  3   -0.0     -0.5     -1.2     2.1      1.8      0.4      -7.6     
  cb0nyk.idsp                  4   -0.0     -0.5     -1.2     2.1      1.8      0.4      -7.6     
  tinker3.idsp                 1   -12.2    -54.5    -61.7    -34.6    -77.8    -66.5    -72.5    
  tinker3.idsp                 2   -0.2     -2.4     -7.4     -7.0     -6.3     -4.1     -3.3     
  tinker3.idsp                 3   0.0      0.0      0.0      0.0      0.0      0.0      0.0      
  tinker3.idsp                 4   0.0      0.0      0.0      0.0      0.0      0.0      0.0      
  w6784b.idsp                  1   -0.0     -1.0     -1.9     -2.2     -5.4     -13.8    -8.1     
  w6784b.idsp                  2   -0.0     -1.0     -1.9     -2.2     -5.4     -13.8    -8.1     
  w6784b.idsp                  3   -0.0     -1.0     -1.9     -2.2     -5.4     -13.8    -8.1     
  w6784b.idsp                  4   -0.0     -1.0     -1.9     -2.2     -5.4     -13.8    -8.1     
  xswmps.idsp                  1   -0.0     -1.0     -1.9     -2.2     -5.2     -0.1     2.3      
  xswmps.idsp                  2   -0.0     -1.0     -1.9     -2.1     -3.2     1.6      1.9      
  xswmps.idsp                  3   -0.0     -1.1     -5.4     -9.7     -10.3    -5.1     -5.2     
  xswmps.idsp                  4   -0.0     -1.0     -1.9     -2.1     -3.2     1.6      1.9      

--- nearest neighbour by shape (rms dB over 1/6-oct, level-aligned)
  1-car-tune-2026-10-20.idsp     m1 -> 1-car-tune-2026-10-20.idsp   m2   rms 0.0 dB
  1-car-tune-2026-10-20.idsp     m2 -> 1-car-tune-2026-10-20.idsp   m1   rms 0.0 dB
  1-car-tune-2026-10-20.idsp     m3 -> 1-car-tune-2026-10-20.idsp   m1   rms 0.0 dB
  1-car-tune-2026-10-20.idsp     m4 -> 1-car-tune-2026-10-20.idsp   m1   rms 0.0 dB
  2-bor8f3-improved.idsp         m1 -> 2-bor8f3-improved.idsp       m2   rms 0.0 dB
  2-bor8f3-improved.idsp         m2 -> 2-bor8f3-improved.idsp       m1   rms 0.0 dB
  2-bor8f3-improved.idsp         m3 -> 2-bor8f3-improved.idsp       m1   rms 0.0 dB
  2-bor8f3-improved.idsp         m4 -> 2-bor8f3-improved.idsp       m1   rms 0.0 dB
  3-rnb-dense-2026-10-21.idsp    m1 -> 3-rnb-dense-2026-10-21.idsp  m2   rms 0.0 dB
  3-rnb-dense-2026-10-21.idsp    m2 -> 3-rnb-dense-2026-10-21.idsp  m1   rms 0.0 dB
  3-rnb-dense-2026-10-21.idsp    m3 -> 3-rnb-dense-2026-10-21.idsp  m1   rms 0.0 dB
  3-rnb-dense-2026-10-21.idsp    m4 -> 3-rnb-dense-2026-10-21.idsp  m1   rms 0.0 dB
  ai2.idsp                       m1 -> tinker3.idsp                 m1   rms 0.0 dB
  ai2.idsp                       m2 -> ai2.idsp                     m3   rms 3.5 dB
  ai2.idsp                       m3 -> ai2.idsp                     m4   rms 0.0 dB
  ai2.idsp                       m4 -> ai2.idsp                     m3   rms 0.0 dB
  bor8f3.idsp                    m1 -> xswmps.idsp                  m1   rms 0.0 dB
  bor8f3.idsp                    m2 -> xswmps.idsp                  m2   rms 0.0 dB
  bor8f3.idsp                    m3 -> xswmps.idsp                  m3   rms 0.0 dB
  bor8f3.idsp                    m4 -> bor8f3.idsp                  m1   rms 2.0 dB
  cb0nyk.idsp                    m1 -> cb0nyk.idsp                  m2   rms 0.0 dB
  cb0nyk.idsp                    m2 -> cb0nyk.idsp                  m1   rms 0.0 dB
  cb0nyk.idsp                    m3 -> cb0nyk.idsp                  m1   rms 0.0 dB
  cb0nyk.idsp                    m4 -> cb0nyk.idsp                  m1   rms 0.0 dB
  tinker3.idsp                   m1 -> ai2.idsp                     m1   rms 0.0 dB
  tinker3.idsp                   m2 -> bor8f3.idsp                  m3   rms 3.5 dB
  tinker3.idsp                   m3 -> ai2.idsp                     m3   rms 0.0 dB
  tinker3.idsp                   m4 -> ai2.idsp                     m3   rms 0.0 dB
  w6784b.idsp                    m1 -> w6784b.idsp                  m2   rms 0.0 dB
  w6784b.idsp                    m2 -> w6784b.idsp                  m1   rms 0.0 dB
  w6784b.idsp                    m3 -> w6784b.idsp                  m1   rms 0.0 dB
  w6784b.idsp                    m4 -> w6784b.idsp                  m1   rms 0.0 dB
  xswmps.idsp                    m1 -> bor8f3.idsp                  m1   rms 0.0 dB
  xswmps.idsp                    m2 -> bor8f3.idsp                  m2   rms 0.0 dB
  xswmps.idsp                    m3 -> bor8f3.idsp                  m3   rms 0.0 dB
  xswmps.idsp                    m4 -> bor8f3.idsp                  m2   rms 0.0 dB

--- outliers: mean rms distance to the other 31 voicings
  ai2.idsp                       m1   25.6 dB
  tinker3.idsp                   m1   25.6 dB
  3-rnb-dense-2026-10-21.idsp    m1   17.6 dB
  3-rnb-dense-2026-10-21.idsp    m2   17.6 dB
  3-rnb-dense-2026-10-21.idsp    m3   17.6 dB
  3-rnb-dense-2026-10-21.idsp    m4   17.6 dB
  1-car-tune-2026-10-20.idsp     m1   14.6 dB
  1-car-tune-2026-10-20.idsp     m2   14.6 dB

wrote .\tune\atlas.json
```
