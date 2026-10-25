# Overnight — 2026-10-19

Three things were built and tested. Nothing touched the DSP, nothing touched the page, and no
microphone was used: everything here is offline and reversible.

**First thing in the morning:**

```
cd ds18-bridge
.\doctor.ps1
```

Then plug the DSP in and run it again — that second run is the one that checks the crossovers.

---

## 1. `doctor.ps1` — is the rig ready?

One screen, green / amber / red, exit 0 = ready. It checks the bridge, the DSP, the memory,
every path's EQ and crossover block, rta.exe, the UMIK-1, the cal file, ffmpeg/csc, the
binaries, the recordings, the backups and the disk.

Two checks did not exist before:

- **crossovers still open?** reads paths 0-7 and compares the six edge scalars against
  `evidence\crossover-snapshot-mem<N>.json`. A mismatch is a FAIL that names the paths and
  prints the restore command.
- **sweep mode still on?** all paths flat + master −8.0 dB. A warning, since a deliberately
  flat tune looks the same.

```
.\doctor.ps1            # the screen
.\doctor.ps1 -Restore   # close the crossovers if they are open
.\doctor.ps1 -Json      # doctor.json
.\doctor.ps1 -SelfTest  # 3 assertions, incl. mutating one low edge and one high edge
```

**Proven tonight:** the self-test (unchanged reads closed; a mutated low edge names only that
path; a mutated high edge is caught), the dead-bridge path (exit 1, valid JSON), and the live
run with the DSP unplugged.

**Not proven:** the connected comparison against a real snapshot — there is no snapshot on
disk yet, because `crossover-range.ps1 open` has never run.

---

## 2. `rehearse.ps1` — the analysis, with the answers known

`bin\sweep-tool.exe` grew a `synth` command. The rehearsal builds a two-channel fixture:
left = eight sweeps at the page's real cadence (12 s window + 2 s settle = 14 s), right = the
same sweeps delayed by known milliseconds and scaled by known gains, over a −70 dB noise
floor. Then it runs the real `measure-sweep.ps1` over it and compares.

```
.\rehearse.ps1
```

**Result tonight:** 8/8 delays within **0.008 ms**, 8/8 levels within **0.00 dB**, silent
channel correctly not measured. Exit 0 only if that still holds.

### It found a real bug

The silent channel came back **measured**, with a random 40.87 ms delay:

| gate | silent channel | a real channel |
|---|---|---|
| peak-to-rest SNR, old gate (≥ 6 dB) | **7.9 dB — passed** | 23.4 dB |
| normalised coefficient, new gate (≥ 0.02) | **0.006 — fails** | 1.000 |

With 60 bands' worth of lags, the maximum of noise alone is several sigma, so 6 dB does not
mean "a sweep arrived". In a car with a normal noise floor, a dead or unplugged driver could
have been handed a plausible delay and then used for alignment. `bursts` now reports `coef`
and requires both gates. Regression: 50 ms → 50.000, 137 ms → 137.000, four bursts
13/3/5/9 ms with levels unchanged.

---

## 3. `propose-eq.ps1` — a measured curve into 31 band gains

Reads a sweep take or a saved RTA measurement, solves for the 31 fixed bands (Q=1) with a
weighted least-squares fit — so overlapping neighbours are accounted for — and prints the
gain table, the predicted curve and the residual. **It never writes to the DSP.**

```
.\propose-eq.ps1 -Bursts evidence\...\sweep-....json -Burst 0
.\propose-eq.ps1 -Name "tweeter 1"
.\propose-eq.ps1 -Name "front 3.5" -TiltDbPerOct -0.5
.\propose-eq.ps1 -SelfTest
```

Guardrails: boosts ≤ +6 dB, cuts ≥ −12 dB, no boost more than 15 dB under the curve's top,
and a band whose own centre is under that floor can be cut but never boosted. The curve is
normalised to its usable median, so it never tries to raise a channel by the mic's absolute
level.

**Proven:** the self-test (synthetic curve flattened to 0.31 dB max residual; a 125 Hz null and
a sub-60 Hz rolloff are not chased) and the demo on the 1 kHz-lowpass fixture, where it stops
boosting at the knee (+3.8 dB @ 2 kHz) and leaves the rolloff alone.

**Not proven:** a proposal from a real measurement. Every saved RTA capture in
`rta-measurements.json` is from the period when the laptop array was silent (peaks around
−50 dBFS, floor −120), so there is no real curve to fit yet. The first honest proposal comes
after tomorrow's UMIK-1 run.

---

## State right now

| | |
|---|---|
| DSP | unplugged — `/api/status` says not connected, so no crossover write was made |
| UMIK-1 | in the device tree as **Unknown**; rta.exe is on the Realtek array at −96 dBFS |
| crossovers | untouched; no snapshot exists, so nothing is open |
| page | not modified tonight — `web\index.html` is 283,847 bytes and its SHA-256 is identical to the pre-night backup; `verify-web: ok` |
| protocol | `--selftest` all pass |
| revert point | `evidence\backups\ds18-bridge-20261019-210348.zip` (86 files, source/web/scripts) |

**Disk: 1.6 GB free on C:.** Each 240 s stereo take is ~46 MB, so a cycle fits, but Windows is
uncomfortable under ~5 GB and the rehearsal fixtures are 43 MB each (deleted unless `-Keep`).
Worth clearing something before a long session.

## Not done, on purpose

- no DSP write of any kind (the device is unplugged anyway)
- no microphone run (the UMIK-1 is not present)
- no change to `web/index.html`

## Still waiting on you

1. Channel names — reload the page once and type them (the roster field is there).
2. Full-range floor for the 3.5" / 8" / 6.5": 20 Hz, or `-LowFc 100` to protect the small ones
   for the first 2.3 s of each sweep?
3. Whether a full-range open/restore button belongs on the Measure card — worth deciding after
   the first real `open` **and** `restore` are both proven on hardware.
