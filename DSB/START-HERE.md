# START HERE — everything DSP- and tuning-related in one place

**This folder (`DSB\`) is the single entry point.** If somebody says "look in the DSB folder",
this file is what they should read first, in this order, and it will have them current in one
sitting.

Everything written down about tuning this car lives here: the procedure, the state, the
decisions, the doctrine learned from outside sources, and what is still unknown. The code and
the raw data stay where they are, in the project tree — the table at the bottom says where.

---

## 1. Read this, in this order

| # | file | what it is | when to stop |
|---|---|---|---|
| 1 | **`STATUS.md`** | *generated* — the state of the world right now: which tune is playing, which gates are green, what is stale | after one minute |
| 2 | **`OPEN-QUESTIONS.md`** | the things this project does not know, each ranked by which decision it blocks | after two minutes |
| 3 | **`TUNE-PLAYBOOK.md`** | the procedure, end to end: path map, hard limits, measurement validity, how to fit and write and verify a tune | before touching any tune |
| 4 | **`TUNING-BRIEF.md`** | the same job written as a brief for somebody taking it on cold, with the work plan in phases | before proposing a plan |
| 5 | **`EQUIPMENT.md`** | the *why* — what is actually in the car and why the limits are what they are | when a number needs a reason |

Do not read everything else up front. `STATE.md` is the dated log (consult, don't read),
`DECISIONS.md` is short and worth a skim, and the rest are reports and references.

**If you are about to change gains, crossovers or the target curve:** read
`TUNE-PLAYBOOK.md` **§9.0 through §9.0.3** first, and `refs\README.md`. Three of those
sections currently converge on the same recommendation.

---

## 1b. Session start — two commands, about 35 seconds

Run these **before reading anything**. They answer in one page what used to take a large
slice of a 5,330-line knowledge base, and they are the reason the read order above can start
at `STATUS.md`.

```powershell
powershell -File dsb-status.ps1     # what is true right now -> DSB\STATUS.md
powershell -File verify-all.ps1     # is the repo green      -> DSB\VERIFY.md
```

If `verify-all` is red, fix it before changing anything else — a red gate means the next
edit lands on top of a known break. Add `-All` to include the desktop app's self-test (it
builds, so close `DreTunes.exe` first).

---

## 2. The three rules that override everything

1. **A write is not a write until it has been read back.** `apply-tune.ps1` snapshots before
   it sends anything, and a verifier that checks nothing must never report success.
2. **A prediction is not a measurement.** Every residual in this repo so far is predicted
   from a pre-tune capture. No post-tune capture has ever been taken. See
   `OPEN-QUESTIONS.md` #7.
3. **Evidence beats doctrine.** The material in `refs\` is doctrine from other people's
   cars; this repo's captures are evidence. When they conflict, evidence wins. Every
   `§9.0.x` section says so at its head.

---

## 3. The knowledge base: `refs\`

Outside material the owner feeds in — video transcripts, forum posts, filter theory — is
stored **verbatim** in `refs\<source>\<slug>.txt` with a provenance header, then digested
into numbered `§9.0.x` sections of `TUNE-PLAYBOOK.md`. `refs\README.md` carries the intake
protocol, the evidence-versus-doctrine hierarchy and the standing rules learned the hard way.

**Never tune from a file in `refs\` directly.** Read the playbook section it produced.

---

## 4. Where everything else lives

| path | what is there | read/written by |
|---|---|---|
| `..\` (project root) | `README.md`, and every PowerShell tool | `verify-web.ps1`, `doctor.ps1`, `apply-tune.ps1`, `measure*.ps1`, `crossover-range.ps1` |
| `..\tune\` | the fitting toolchain (`fit-tune.py`, `fit-dense.py`, `system-predict.py`) and the plan JSONs | Python; plans are inputs to `apply-tune.ps1` |
| `..\evidence\` | raw evidence: run captures, passband baselines, and `caraudio1\` — including `upstream\`, a clone of `github.com/sdotmo1223/CarAudio1` | read-only |
| `..\measurements\` | named capture runs, one folder each, with its own README | `measure.ps1`, `measure-sweep.ps1` |
| `..\banks\`, `..\web\` | the `.idsp` tune library (12 files) and the bridge UI | `load-tune*.ps1`, the web app |
| `..\app\` | **DreTunes**, the desktop companion app | `app\build.ps1` |
| `..\src\`, `..\bin\` | the bridge server, the RTA daemon, the sweep tool | `build.ps1`, `build-rta.ps1`, `build-sweep.ps1` |
| `..\var\` | scratch: probes and page-check copies | throwaway |
| `..\..\DS18_DSP_protocol.md` | **the vendor protocol log, §1–§37** — cmd 11, cmd 13, every measured protocol fact | *one directory up, outside this project* |

The last row matters: `DS18_DSP_protocol.md` and `decompiled\AP.cs` are **not** in
`ds18-bridge`. They sit in the parent folder. Two earlier sessions concluded they "do not
exist"; they exist, and `lint-refs.ps1` now reports them as cross-boundary warnings rather
than silently missing.

---

## 5. The tools, in one line each

| command | what it does |
|---|---|
| `powershell -File lint-refs.ps1` | every reference in these documents resolves? (exit 0 = clean) |
| `powershell -File verify-all.ps1` | one verdict across every gate in the project (`-All` adds the app self-test) |
| `powershell -File dsb-status.ps1` | regenerate `STATUS.md` |
| `python tune\dsb-catalog.py` | regenerate `CATALOG.md` — one card per tune |
| `powershell -File doctor.ps1` | is the rig ready? (needs the car) |
| `powershell -File apply-tune.ps1 -Plan tune\tune-plan.json -WhatIf` | snapshot and report, change nothing |

**Nothing here writes to the DSP except `apply-tune.ps1`, `set-crossovers.ps1`,
`flatten-tune.ps1`, `load-tune*.ps1` and `crossover-range.ps1` — and each of those snapshots
to `evidence\` first.**

---

*Created 2026-10-25. Every other document's "read this first" list was retired in favour of
this one; if you find an old ordered list pointing somewhere else, it is stale.*
