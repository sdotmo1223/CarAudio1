# APP-NOTES — where DreTunes could lie, and what stops it

This is the audit. Every place the app could present something as fact when it is not, and
what the code does about it. Where the answer is "nothing can stop it", that is written down
too.

Read it before trusting a screen.

---

## 0. The three states, and why the words matter

Every value on screen is in one of three states, and the app uses a different word and a
different colour for each. There is no fourth state and no colour without a word.

| State | What it means | Colour |
|---|---|---|
| **measured** | read back off the amplifier, or read off a file on disk, with a time | green |
| **unproven** | a real reading exists but it cannot settle the claim (no path, no baseline, no readback) | amber |
| **mirrored / predicted** | the app knows what it *sent* or what a file *says*, and says so | grey / muted |

Green is only ever produced by a readback. If the readback stops, the rail says
"not reading" within one poll — `Rig.DeviceLive` requires a `/api/status` answer less than
30 s old, and every surface re-paints from that flag.

---

## 1. The memory number: a device read, or the bridge's label?

**The trap.** `/api/info` carries a `mem` field and `/api/status` carries a `mem` field. They
are not the same claim: one is the bridge's remembered value, the other is a read this app
made. Mixing them is how "2 of 4 playing" appears on screen with the DSP unplugged.

**What the code does.** `Rig.MemFromStatus` is set only by `PollStatus`. Every place that
prints the memory number distinguishes the two:

- Tune → the memory badge: `MEMORY 2 OF 4` vs `MEMORY 2 · BRIDGE LABEL` (amber).
- The header's second line carries the master volume, mute state and source, and only when
  `DeviceLive`; otherwise it says when the last device read was.
- Library → the green `playing` marker next to a memory chip requires `DeviceLive`.
- The rail → `memory 2 playing` vs `memory 2, bridge label`.
- Showcase → `2 of 4` vs `2 of 4 (bridge label)`.

**What is still possible.** A status read that succeeds and then the DSP is unplugged keeps
the number for up to 30 s, coloured as live. That window is the cost of not opening a USB
handle on every repaint; the timestamp next to it is the mitigation.

---

## 2. The curve: whose response is it?

**The trap.** A beautiful curve is the most convincing thing on the screen and the easiest
thing to fake. Drawing a tune file's own bands and calling it "the car" would be a lie.

**What the code does.**
- The Tune surface's chart title says `EQ RESPONSE · TUNE FILE (NO DEVICE READ)` when the
  series come from a `.idsp`; when they come from a readback it carries the read's timestamp
  instead.
- When a readback exists, the live series is labelled as readback and the file series stays
  separate — they are never merged into one "response".
- The prompt "verified by readback" is only produced by `Analysis.Diff` over a *fresh*
  `/api/eqread` of all eight paths.
- Measurements labels the fit `predicted` and says, in words, that the measured curve plus
  the correction is a model, not a re-measurement.

**What is still possible.** `Curve.cs` is a mirror of `tune\idsp.py`. If `idsp.py` changes and
nobody rebuilds, the drawn curve can drift from the tool that builds tunes. That is why the
self-test runs `python tune\idsp.py curve` on a real file and compares at 17 key frequencies
× 3 paths, failing above 0.06 dB (the printer's own 0.1 dB rounding). Observed today:
`Curve.cs agrees with idsp.py within 0.06 dB`.

**A number that is smaller than it looks.** The chart is drawn on the 1/6-octave grid and read
back at key frequencies by log-linear interpolation, exactly as `idsp.py` prints it.
Evaluating the biquad exactly at 1.5 kHz differs by up to ~0.26 dB on a steep slope. Both
paths are asserted separately (`within 0.06 dB` for the printed readout, `within 0.3 dB`
between the two readings) so the interpolation error is visible instead of hidden in one
loose tolerance.

---

## 3. Command 11 has no readback. Ever.

Levels, delays, polarity and mute are sent as one 16-entry block. The amplifier **cannot echo
them back**. Any app that shows them as "verified" is lying, and this one does not:

- The numbers are labelled **MIRROR ONLY** in the strip under the Tune table, with the
  reason ("this amplifier cannot echo it back").
- A swap compares the block it sent against the bridge's mirror and can reach at most
  `EQ verified, levels unproven`. It never reaches "verified".
- The table's level/delay/polarity values come from the mirror, which is why their source word
  is amber `mirror` rather than the accent `device` used by the crossover columns.

**The horn is a write, and it is the only control on that screen.** Muting a channel sends a
complete 16-entry block — a partial block would rewrite the other fifteen entries, which is how a
"mute one channel" button silences a car. `MuteBlock.Plan` builds the block, counts the fields that
differ from the mirror (exactly one), and refuses rather than guesses when:

- the bridge is down;
- the amplifier is not answering `/api/status` with a memory index;
- the memory it reports is not the memory the block in hand was read for (re-read at the moment of
  the click, not from a poll up to 1.2 s old);
- no command-11 block is known for that memory, so the other fifteen entries would have to be
  invented.

Before sending, it writes `evidence\cmd11-before-mute-<stamp>.json` — a block-shaped revert point,
deliberately **not** a `tune-before-*.json`, because that shape is what `apply-tune.ps1 -Restore`
consumes and a file with no bands in it must never be handed to it. The file says so in its own
`restore` string. After sending it re-reads `/api/info` and compares the echo, and the action line
says mirror-only either way. **A mute can therefore be refused, sent, or sent-but-unconfirmed —
never verified.**

**What is still possible.** The mirror can be wrong in the same direction as the write (a bug
in the bridge's own encode). Nothing in this app can detect that. The mitigation is that the
bridge is the same code the phone page uses, and the swap snapshot records exactly what was
sent so a later investigation has the block.

---

## 4. Crossovers: both edges are read, the enable bit is not, and nothing writes them

`/api/eqread` returns a passband block per path carrying `lowFc`/`lowSlope`/`lowFilterType` and
`highFc`/`highSlope`/`highFilterType`. The **low edge is the high-pass**; the high edge is the
low-pass.

The protocol names them backwards from the way a person says them, so the split lives in one
place (`Rig.ParsePassBand`) and is asserted in the self-test. Reading them the other way round
would put 1.5 kHz in the low-pass column of every tweeter, which looks entirely plausible on
screen and is wrong on every row.

What the block does **not** carry is an enable bit. A device read therefore cannot say "this
filter is switched off". Only a `.idsp` can, because it stores `<Enable>` per passband — so the
Tune tab prints `OPEN` for a file whose filter is off, and never for a readback. In the readback
case the column header says `device · enable not read`, rather than leaving a reader to assume
the filter is running.

The Rig surface compares both edges against `evidence\crossover-snapshot-mem*.json`. Until this
version it compared the low-pass edge against the snapshot it was comparing *to* — a comparison
that cannot fail — because the parser discarded the high edge. A path whose low-pass did not
come back is now **unproven**, not "open at 20 kHz".

**Never written.** Nothing in this app sends `/api/passband` (command 13). The Tune tab shows the
crossovers because they are part of what the amplifier is doing; it offers no control for them,
and its footer states that zero of the five columns are editable.

---

## 5. "Unproven" is not "unchanged" — the doctor bug this app refuses to repeat

The known gotcha: `/api/eqread` does not echo the path it read. A comparison that filters
reads by path matches nothing, skips seven paths, and reports success.

- `CrossoverCheck.Compare` treats a read with `Path < 0` as **unproven**, never as unchanged.
- It counts unproven **paths** once (a read index can never be mistaken for a different path's
  index), and counts unnamed reads separately in `UnnamedReads`.
- `Clean` requires no opens, no unproven paths **and** no unnamed reads — so a run where every
  read failed to name its path is not "clean".
- `Rig.ReadEq` stamps each read with the loop index rather than trusting the payload, because
  the payload has no path to trust.

**Asserted in the self-test** (`crossover comparator (doctor's four cases)`): unchanged reads
close, one changed path is named and only it, a changed high edge is caught, and eight reads
with no path produce eight unproven paths — not zero.

---

## 6. The wrong-memory guard

Memory chips are 1-based on the phone page and 0-based in the protocol; the guard is the
thing that stops a write into a memory the amp is not playing.

- `Analysis.Guard` refuses unless `target == playing` **and** the link is live, and the refusal
  text names which rule fired.
- The override is a chip that must be deliberately armed, is labelled `OVERRIDE ARMED`, writes
  an `OVERRIDE ...` line into the activity log and into `%LOCALAPPDATA%\DreTunes\desktop.log`,
  and colours the guard line amber so the screen never looks like a normal swap.
- The self-test asserts all four cases: playing memory allowed, other memory refused, refusal
  names the reason, explicit override allowed *and labelled*, and — separately — that a dead
  link refuses even the playing memory.

**What is still possible.** The override exists on purpose. A determined owner can arm it and
write to the wrong memory. The app's job is to make that a visible, logged decision rather
than an accident, and it does that.

---

## 7. "Verified" means the EQ, and only the EQ

The verdict word on a swap is one of `verified`, `unproven`, `failed`. "Verified" is produced
only by a band-by-band diff of a fresh readback against the file, using
`verify-device.py`'s tolerances (Fc ±1 Hz, gain ±0.15 dB, Q ±0.06). It never covers levels,
delays, polarity or mute.

Near-misses are printed, not rounded away: the diff's detail lines quote what was sent and
what the mirror holds, so a 0.2 dB error appears as a number rather than a colour.

**What is still possible.** A readback can be internally consistent and still not describe
what the speakers do (e.g. the amp applied a different memory than the one read). The memory
is pinned by the guard, and the readback is compared per path — but the app trusts the
amplifier's answer about itself, because there is no other witness.

---

## 8. The bridge's remembered tune name

`/api/info` carries a tune label the bridge remembers from the last write. It is a label, not
evidence. The Tune surface shows it only when it **disagrees** with the file being described, as
an amber `BRIDGE SAYS ...` chip next to the tune name — that disagreement is the thing a reader
would otherwise take at face value. The app never uses the label to decide what is playing; the
readback match does that.

---

## 9. Stale green

`Rig.DeviceLive` = bridge up **and** DSP open **and** a status read inside 30 s. The header
badge distinguishes four states (`DSP LIVE`, `NO RECENT READ`, `DSP NOT CONNECTED`,
`BRIDGE DOWN`) rather than two, because "the DSP is there but nothing recent came back" is a
different fact from "the DSP is gone". Header right says `last device read never` / `12 s ago`
in words.

---

## 10. Predicted, measured, and the documented artefact

- The Measurements surface never normalises: values are absolute dBFS, and the surface says
  so in the run line (`dBFS (ABSOLUTE, NOT SPL)`).
- The newest capture (2026-10-20 19:12) is the evidence the tune was built *from*; no capture
  has been taken since. So the fit is labelled **predicted** and the Rig surface lists
  "Post-tune measurement" as unproven.
- The ~1.5 kHz notch story is presented as what it is: a documented case from
  `DSB\B-TUNE-REVIEW.md`, not a recomputation. **This app could not reproduce that claim
  from `web\bor8f3.idsp`** — that file shows −12.4 / −8.3 dB at 1529 Hz with Q 13.6, not two
  identical narrow notches. The surface therefore says "documented case", and the
  copied-filter detector is proved on a synthetic pair *and* run as a negative control on
  tune 1 (which correctly reports none). Do not cite the app as evidence for the notch.

---

## 11. Numbers that are derived, not measured

These are computed by this app and could be wrong in its own way:

- **Bandwidth in octaves** = `2·asinh(1/(2Q))/ln2`. `Math.Asinh` does not exist in .NET
  Framework 4.x, so it is `ln(x + sqrt(x²+1))` here. Asserted against known values: Q 1.0 ≈
  1.39 octaves, Q 10 ≈ 0.14.
- **"Narrow"** means narrower than a quarter octave (0.25), stated on the surface. Tune 1 has
  0 such bands; `bor8f3` (all Q 13.6 ≈ 0.11 octave) has more than 20.
- **Ripple** is peak-to-peak of the drawn curve between 200 Hz and 8 kHz, on grid points only.
- **Grid**: `20 · 2^(k/6)`, 61 points, last point 20480 Hz. `idsp.py`'s comment calls this
  "20 Hz .. 20 kHz"; the 61st point is 20480. The app mirrors the list, not the comment, and
  the self-test asserts 20480.

---

## 12. The microphone name is truncated at the source

`rta.exe` reports `"device": "Microphone Array (Realtek(R) Au"` — the name is cut off inside
the RTA, not by this app. Consequences:

- The UMIK-1 check matches on `UMIK` anywhere in the string, so the truncation cannot hide a
  correct mic.
- A Realtek array is reported as `WRONG MIC` in amber, with the name shown as the RTA gave it.
- The app cannot show the full device name to help a human identify it. Use the Windows sound
  control panel for that; the app will keep saying "the name is what the RTA reported".

---

## 13. `doctor.ps1` can disagree with this app, and both are shown

`doctor.ps1` reports `READY WITH WARNINGS` with `dsp:false` and still exits 0. The Rig surface
shows that verdict **verbatim**, then a second line, `DreTunes: not reading`, which is the
app's own live state. When they disagree, they are both on screen and neither is edited.

The readiness rows are `doctor.ps1 -Json` parsed, not re-interpreted: a row's word, colour and
detail come from the tool. `doctor.ps1` not run in this session is `NOT RUN`, not "ready".

---

## 14. Snapshots and restore

- A snapshot is written **before** the first byte of a write, in `apply-tune.ps1`'s shape
  (`evidence\tune-before-<stamp>.json`), so `apply-tune.ps1 -Restore <file>` can consume it.
  The self-test writes one to a scratch name and re-parses it: it must carry `at`, `tag`, `mem`,
  the complete 16-entry block (including an untouched path 15), eight paths, 31 band slots per
  path, `byPass`, and `fc/gain/q/enable`.
- `apply-tune.ps1 -Restore` writes and exits without verification. **This app's restore does
  not**: it snapshots the state it is about to replace, writes, reads back, diffs, and reports.
  It is stricter than the script on purpose, and the README says so.
- A snapshot's `mem` is the memory the readback came from. Restore refuses if that memory is not
  the playing memory, unless the override is armed.

**What is still possible.** A snapshot is only as good as the readback it was taken from. If
the amplifier answered with another memory's bands, the snapshot is of that memory and its
`mem` field will say so — which is the only warning available.

---

## 15. The Showcase cannot damage anything

- It never calls `StartSwap`, `StartRestore`, `SwapEngine.Apply` or `Restore`. Checked by
  grep over `SurfaceShowcase.cs`: no such call exists.
- Nothing auto-plays. *Play tour* is a press, and *Pause tour* stops it.
- Its live column is the same `Rig` state the other surfaces read, so it cannot show a
  healthier rig than the Rig surface does.

The only writes this program performs anywhere are: the log file; a snapshot in `evidence\` when a
write is actually started by a human; and — from the Tune tab's horn only — the command-11 block
itself, after its own revert point has been written.

---

## 16. Permanently out of scope

- **Firmware (command 17)** — never sent, by design. Not a bug, not "coming later".
- **Crossovers and master volume** — never written from a tune path. The Tune tab displays the
  crossovers per channel and has no control to change them.
- **The sub amplifier** — outside the DSP; the only control is an owner tick, labelled as an
  assertion. No surface implies the app can see or change it.
- **Paths 8–15** beyond command 11 — no driver is attached; the app writes their block values
  because the command is all-or-nothing, and says so in the step text.

---

## 17. The mutation harness: proof that the tests can fail

`--selftest --mutant` sets seven static flags that replace real implementations with wrong ones:

| Flag | Wrong behaviour injected | Assertions that must fail |
|---|---|---|
| `CleanAlways` | `Analysis.DiffRaw` reports zero differences | `the difference is counted`, and the match/verdict assertions |
| `AllowAlways` | `Analysis.Guard` allows any target | `another memory is refused`, `the refusal names the reason`, `the override is labelled in the reason`, `a dead link refuses even the playing memory` |
| `FlatCurve` | `Curve.PeakingDb` returns 0 dB | `Curve.cs agrees with idsp.py within 0.06 dB`, `exact-frequency evaluation ...` |
| `SwapPassBands` | the passband readback swaps the two edges | `the readback's low edge is the high-pass`, `the readback's high edge is the low-pass, and it is kept`, `the two edges do not swap their slopes` |
| `CrossoverWritable` | the Tune table claims five writable columns | `the Tune table offers no writable column` |
| `RowOffset` | every table click lands one channel low | `a click on the third row selects channel 3` |
| `MuteClobbersOthers` | the mute block carries only the touched channel, zeroing the other fifteen | `every other channel's entry is carried through untouched`, `exactly one field differs from the mirror`, `unmuting flips the muted channel back, one field` |
| `MuteClaimsVerified` | the mute result says the amplifier confirmed it | `the result says mirror, and never claims verification`, `the result names command 11 as having no readback` |

`CrossoverWritable` guards a decision rather than a calculation: the table's five value columns stay
read-only because crossovers are out of scope and command 11 has no readback, and a future edit that
quietly widened it would have to change a number that is drawn on screen. `MuteClobbersOthers` is
the same idea for the horn — it is what a partial command-11 write would actually do to the car.

**Observed today**, verbatim from the run:

```
selftest: 128 passed, 17 failed
  FAIL  Curve.cs agrees with idsp.py within 0.06 dB (print rounding)  -- worst difference 7.700 dB
  FAIL  exact-frequency evaluation stays within 0.3 dB of the interpolated readout  -- worst 7.700 dB
  FAIL  the difference is counted  -- band diffs 0
  FAIL  another memory is refused  -- allowed
  FAIL  the refusal names the reason  -- target accepted
  FAIL  the override is labelled in the reason  -- target accepted
  FAIL  a dead link refuses even the playing memory  -- a write with no readback was allowed
  FAIL  the readback's low edge is the high-pass  -- 20000
  FAIL  the readback's high edge is the low-pass, and it is kept  -- 1500
  FAIL  the two edges do not swap their slopes  -- 2/3
  FAIL  the Tune table offers no writable column  -- 5
  FAIL  a click on the third row selects channel 3  -- row mapping
  FAIL  every other channel's entry is carried through untouched  -- a one-channel mute rewrote path 0
  FAIL  exactly one field differs from the mirror  -- 16 field(s) changed
  FAIL  unmuting flips the muted channel back, one field  -- False / 16
  FAIL  the result says mirror, and never claims verification  -- CH 4 muted - verified by the amplifier's readback.
  FAIL  the result names command 11 as having no readback  -- CH 4 muted - verified by the amplifier's readback.
```

and the normal run is `selftest: 126 passed, 0 failed` with exit 0. `self-test.ps1` fails if
either half is wrong — including if the mutant run *passes*, which is the failure mode a
self-test usually has.

---

## 18. What is NOT verified today, and how to close it

> **Superseded 2026-10-25.** This list was one of five copies that had drifted apart.
> Every unknown now lives in **`OPEN-QUESTIONS.md`** — one register, ranked by which
> decision each question blocks. Do not add an unknown here; add it there and leave a
> pointer in the document that cares.

## 19. Gates, and their real numbers today

Run from this machine during the build, not quoted from memory:

| Gate | Command | Observed |
|---|---|---|
| Self-test | `bin\DreTunes.exe --selftest` | 146 passed, 0 failed, exit 0 |
| Negative control | `--selftest --mutant` | 128 passed, 17 failed, exit 1 |
| Phone page | `verify-web.ps1` | `verify-web: ok`, 14 passed / 0 failed, negative control ok (2 failures on the mutant) |
| Crossover comparator | `doctor.ps1 -SelfTest` | 4 passed, 0 failed, exit 0 |
| Swap library | `tune\verify-swap-tunes.py` | `0 failure(s)`, 52 `ok` lines |

The swap-library count is 52, not the 46 (or 42) the brief quotes — the brief's number is
stale. The app parses the tool's own output and reports what the tool says.

**PowerShell trap that is not repeated in the app:** `-match 'FAIL'` is case-insensitive and
matches the line `0 failure(s)`. Filtering that output for `FAIL` finds a "failure" that is
not one. The C# side uses case-sensitive `StartsWith("FAIL")`.

---

## 20. Where the app writes

- `%LOCALAPPDATA%\DreTunes\desktop.log` — start-up, errors, overrides, exit. UTF-8, appended.
- `evidence\tune-before-<stamp>.json` — only when a swap or restore actually begins. A file
  name that already exists becomes `-2`, `-3`, ... so a revert point is never overwritten.
- Nothing else. No temp files, no registry, no `node_modules`, no venv, no install.

If the window never opens, the log is the first place to look; the reason will be in it.
