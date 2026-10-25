# Decisions

Plain-language record of the calls that shape the app, so they survive a session.
One entry per decision, newest at the top.

## 2026-10-20 — The phone plays the audio; the laptop measures it

**Context.** The original goal was to tune the DSP from the phone with no laptop. That is
not possible: the DSP is on USB and the capture device is the UMIK-1 on the laptop. The
owner is fine with the laptop being required.

**But the phone is not a viewer.** The laptop cannot play into the car. The phone is the
**stimulus source**: it is what streams the sweep/pink noise over Bluetooth into the head
unit and into the DSP, and it is where the owner starts a measurement cycle. The laptop is
the **analysis engine**: it records the UMIK-1, runs `sweep-tool`, and writes the numbers.

**Decision.**
- Phone = stimulus + cycle trigger. Laptop = capture + analysis + DSP writes.
  This is already modelled in the page (`STIM_SRC = "phone"` is the default; the engine
  only plays a tone itself when set to `thispc`).
- Phone layout is not load-bearing, but the phone path must stay correct: it is the only
  thing that makes sound in the car.
- Anything the two views must agree on belongs on the **bridge**, not in `localStorage`.
  `localhost`, `127.0.0.1` and the laptop's LAN IP are different origins with separate
  `localStorage`, which is why roster names typed in one do not appear in the other.

**Consequences of the phone being the source.**
- The laptop's Stereo Mix **cannot** be the reference while the phone plays — it only hears
  laptop audio. Capture is mic-only (`-NoLoopback`) and timing comes from correlating the
  recording against the known generated sweep.
- Bluetooth latency is common to every channel, so it cancels in relative delay. Absolute
  delay is not needed; only alignment between drivers.
- A lossy codec (SBC/AAC) colours the magnitude response. That colour is real but it is not
  the DSP's, so it must not be EQ'd out — it bounds how much to trust the curve up top.
- A backgrounded tab throttles timers, so the phone must keep the page in front with the
  screen awake for the duration of a run.

**Follow-on work (needs a bridge rebuild + restart, so it waits until the DSP is unplugged):**
persist the roster (channel → type, name, rated band) server-side next to `targets.json`,
served through `/api/roster`, with a one-time migration from the existing
`ds18.roster` localStorage value so names already typed are not lost.

## 2026-10-20 — Tweeter protection is 1.5 kHz, and it is enforced

The BZRK RS-T150 is rated from 1.5 kHz. 1 kHz was a guess that bought nothing and put
energy below the driver's rating. Consequences:
- `crossover-range.ps1 -TweeterFc` defaults to **1500**.
- The app refuses to start a real run when a *Tweeter* row's read-back high-pass is open
  or below 1.5 kHz (`sweepBandProblem`, data from `/api/eqread`'s `passband`).
- Cones stay full range: that is the whole point of the measurement.

## 2026-10-20 — Never trust a successful exit code over a readback

`crossover-range.ps1 open` used to `exit 0` without writing whenever a snapshot already
existed on disk — and taking the snapshot first is the documented safe order. Found by
timing the real run (0.6 s for work that takes 7 s) and then reading the device back.
An adopted snapshot must now prove it still matches the device on every path before any
write happens.

## 2026-10-20 — The sub is outside the DSP, permanently

Signal comes from the head unit into the Kicker CXA1800.1. No DSP delay, no DSP level, no
DSP crossover, and no way for the app to mute it. Every sweep therefore requires the sub
amp physically off; the "sub disconnected" tick box is the only guard. Details in
`EQUIPMENT.md`.
