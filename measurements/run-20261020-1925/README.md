# Run first-real-run-20261020-1925

Captured from the DSP/UMIK-1 rig on 2026-10-20 19:25. One file per channel: `<channel>.json` (full record)
and `<channel>.csv` (two columns: hz, dbfs) for anything that prefers a table.

## Conditions these numbers were taken under

- crossovers: path 0-3 HP1500 / 4-5 HP80 / 6 HP50 / 7 HP60, all LP open 20k, LR24
- rig: engine off, sub amp disconnected, head unit 20/30, DSP master -4.8 dB, EQ flat, stim -6 dB
- microphone: UMIK-1 (cal file applied), RTA at 48 kHz
- grid: 1/6 octave, 61 points, 20 Hz - 20 kHz, values in dBFS (absolute, not normalised)
- notes: first run with the stim level fixed (-6 dB instead of -24 dB); crossovers set to the owner target map immediately before the run

## What is in each file

| field | meaning |
|---|---|
| `gate.ok` | the analyser accepted the take (presence, completeness, correlation) |
| `gate.burstDb` | dynamic range of that take: peak above the take's own floor |
| `gate.fromHz` / `toHz` | where the analyser saw the signal rise and fall |
| `peak` | loudest point of the curve and its frequency |
| `points` | 61 `[hz, dbfs]` pairs |

A curve is only comparable to another curve taken with the same crossovers, level and mic
position. `gate.burstDb` under ~40 means the take is thin; `gate.ok` false means it is not a
measurement at all.

## This run

| file | channel | peak dBFS | at | burst dB | gate | band seen |
|---|---|---|---|---|---|---|
| `tweeter-1.json` | tweeter 1 | -46.0 | 4561.4 Hz | 71.6 | ok | 453-20480 Hz |
| `tweeter-2.json` | tweeter 2 | -49.4 | 5120 Hz | 70.4 | ok | 508-18246 Hz |
| `tweeter-3.json` | tweeter 3 | -44.4 | 1810.19 Hz | 73.8 | ok | 570-20480 Hz |
| `tweeter-4.json` | tweeter 4 | -43.2 | 1436.75 Hz | 75.4 | ok | 640-18246 Hz |
| `midrange-1.json` | midrange 1 | -36.2 | 226.27 Hz | 77.9 | ok | 63-20480 Hz |
| `midrange-2.json` | midrange 2 | -42.8 | 403.17 Hz | 73.4 | ok | 50-20480 Hz |
| `midrange-3.json` | midrange 3 | -25.5 | 113.14 Hz | 80.0 | ok | 22-20480 Hz |
| `midbass-1.json` | midbass 1 | -15.9 | 113.14 Hz | 78.7 | ok | 28-806 Hz |
