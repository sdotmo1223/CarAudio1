# Tune plan - hip-hop / R&B

Source: `run-20261020-1925`  
Target: the owner curve (bass shelf, gentle mid tilt, treble rolloff), dB at
30 / 60 / 100 / 500 / 2k / 8k = +6.4 / +5.7 / +3.7 / +0.0 / -1.5 / -4.1

| path | channel | role | bands | EQ gain range | fit (before -> after) | channel gain | delay |
|---|---|---|---|---|---|---|---|
| 0 | tweeter 1 | front-left tweeter | 5 | -5.2 .. +2.3 dB | 3.0 -> 0.9 dB | -4.6 dB, -2.2 | 5.4 ms |
| 1 | tweeter 2 | front-right tweeter | 5 | -5.3 .. +2.3 dB | 2.6 -> 1.2 dB | -7.0 dB, -1.1 | 3.8 ms |
| 2 | tweeter 3 | rear-left tweeter | 4 | -5.8 .. +3.0 dB | 3.9 -> 1.6 dB | -7.0 dB, -2.0 | 6.0 ms |
| 3 | tweeter 4 | rear-right tweeter | 4 | -6.1 .. +2.3 dB | 4.0 -> 1.9 dB | -4.0 dB, -4.1 | 4.1 ms |
| 4 | midrange 1 | front 3.5" pair | 11 | -11.9 .. +2.1 dB | 8.8 -> 6.3 dB | -3.0 dB, -5.3 | 4.6 ms |
| 5 | midrange 2 | rear 3.5" pair | 7 | -12.0 .. +2.0 dB | 7.8 -> 6.2 dB | -3.0 dB, +0.0 | 5.0 ms |
| 6 | midrange 3 | front 8" pair | 11 | -9.4 .. +1.8 dB | 7.5 -> 5.3 dB | -14.0 dB, -16.0 | 3.8 ms |
| 7 | midbass 1 | rear 6.5" pair | 10 | -12.0 .. +1.2 dB | 9.2 -> 5.4 dB | -5.0 dB, -17.1 | 4.4 ms |

## Bands per channel

**path 0 - tweeter 1 (front-left tweeter)**, fitted 1500-14000 Hz

| # | Fc | gain | Q |
|---|---|---|---|
| 1 | 11494 Hz | +1.9 dB | 1.0 |
| 2 | 1709 Hz | -5.2 dB | 2.0 |
| 3 | 4833 Hz | -4.1 dB | 2.0 |
| 4 | 9665 Hz | +2.3 dB | 1.4 |
| 5 | 2874 Hz | +1.4 dB | 1.4 |

**path 1 - tweeter 2 (front-right tweeter)**, fitted 1500-14000 Hz

| # | Fc | gain | Q |
|---|---|---|---|
| 1 | 3044 Hz | +2.3 dB | 1.4 |
| 2 | 1613 Hz | -5.3 dB | 2.0 |
| 3 | 12177 Hz | -1.9 dB | 0.7 |
| 4 | 2416 Hz | +2.3 dB | 1.4 |
| 5 | 4833 Hz | -1.3 dB | 2.0 |

**path 2 - tweeter 3 (rear-left tweeter)**, fitted 1500-14000 Hz

| # | Fc | gain | Q |
|---|---|---|---|
| 1 | 1522 Hz | -5.8 dB | 2.0 |
| 2 | 9123 Hz | +3.0 dB | 0.7 |
| 3 | 3044 Hz | +2.3 dB | 1.4 |
| 4 | 1810 Hz | -4.3 dB | 2.0 |

**path 3 - tweeter 4 (rear-right tweeter)**, fitted 1500-14000 Hz

| # | Fc | gain | Q |
|---|---|---|---|
| 1 | 1613 Hz | -6.1 dB | 1.4 |
| 2 | 11494 Hz | +2.3 dB | 0.7 |
| 3 | 9665 Hz | +1.9 dB | 1.4 |
| 4 | 5424 Hz | -1.9 dB | 2.0 |

**path 4 - midrange 1 (front 3.5" pair)**, fitted 110-9000 Hz

| # | Fc | gain | Q |
|---|---|---|---|
| 1 | 538 Hz | -11.9 dB | 1.0 |
| 2 | 214 Hz | -8.8 dB | 4.0 |
| 3 | 1918 Hz | +2.1 dB | 0.7 |
| 4 | 113 Hz | +2.1 dB | 1.4 |
| 5 | 1613 Hz | -7.7 dB | 4.0 |
| 6 | 180 Hz | -5.1 dB | 4.0 |
| 7 | 135 Hz | +2.1 dB | 1.4 |
| 8 | 718 Hz | -2.5 dB | 2.8 |
| 9 | 1016 Hz | +2.1 dB | 0.7 |
| 10 | 254 Hz | -1.6 dB | 4.0 |
| 11 | 854 Hz | -4.2 dB | 2.8 |

**path 5 - midrange 2 (rear 3.5" pair)**, fitted 110-9000 Hz

| # | Fc | gain | Q |
|---|---|---|---|
| 1 | 427 Hz | -9.0 dB | 0.7 |
| 2 | 1709 Hz | -12.0 dB | 4.0 |
| 3 | 1280 Hz | +2.0 dB | 0.7 |
| 4 | 604 Hz | -8.8 dB | 4.0 |
| 5 | 214 Hz | -4.4 dB | 4.0 |
| 6 | 1076 Hz | +2.0 dB | 0.7 |
| 7 | 320 Hz | +0.5 dB | 0.7 |

**path 6 - midrange 3 (front 8" pair)**, fitted 70-5000 Hz

| # | Fc | gain | Q |
|---|---|---|---|
| 1 | 101 Hz | -9.3 dB | 0.7 |
| 2 | 1918 Hz | +1.8 dB | 0.7 |
| 3 | 403 Hz | -6.0 dB | 2.8 |
| 4 | 1613 Hz | +1.8 dB | 0.7 |
| 5 | 1280 Hz | -9.4 dB | 4.0 |
| 6 | 959 Hz | +0.8 dB | 0.7 |
| 7 | 170 Hz | +1.2 dB | 1.4 |
| 8 | 320 Hz | -5.8 dB | 4.0 |
| 9 | 254 Hz | +1.6 dB | 1.4 |
| 10 | 135 Hz | -4.5 dB | 4.0 |
| 11 | 479 Hz | -0.7 dB | 2.0 |

**path 7 - midbass 1 (rear 6.5" pair)**, fitted 80-7000 Hz

| # | Fc | gain | Q |
|---|---|---|---|
| 1 | 135 Hz | -12.0 dB | 0.7 |
| 2 | 1918 Hz | +1.2 dB | 0.7 |
| 3 | 1613 Hz | +1.2 dB | 0.7 |
| 4 | 113 Hz | -4.5 dB | 1.0 |
| 5 | 1356 Hz | +1.2 dB | 0.7 |
| 6 | 806 Hz | -9.6 dB | 4.0 |
| 7 | 240 Hz | -3.5 dB | 4.0 |
| 8 | 1076 Hz | +0.8 dB | 0.7 |
| 9 | 427 Hz | -2.4 dB | 4.0 |
| 10 | 80 Hz | +0.4 dB | 0.7 |
