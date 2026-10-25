# STATUS - generated, do not edit

Generated 2026-10-25 11:35 by dsb-status.ps1. Read START-HERE.md first, then this page.

## Playing now
- **DSP memory carries:** `4-maxfit-31b-2026-10-22`
- **Last built plan:** `tune\tune-plan.json` (8 paths, target 20 Hz @ 6.0, 35 Hz @ 6.5, 55 Hz @ 6.0, 80 Hz @ 4.5, 120 Hz @ 3.0 ... (15 anchors total))
- **Last cmd-11 mirror:** memory 1, 8 outputs, delays 5.4/3.8/6/4.1/4.6/5/3.8/4.4 ms

## Rig
- **doctor:** READY WITH WARNINGS (4 d ago, 0 fails / 4 warns), snapshot 2026-10-21T20:18:41
- **DSP: not connected.** Everything that needs the amplifier is unverified until it is.

## Gates
- **verify:** passed at 2026-10-19T22:31:36Z (recipe `verify.json`)
- **lint-refs:** run it - `powershell -File lint-refs.ps1` (exit 0 = every reference resolves)
- **verify-all:** run it - `powershell -File verify-all.ps1` (one verdict across every gate)

## Knowledge base
- **4 source files** under `DSB\refs\`, digested into `TUNE-PLAYBOOK.md` 9.0 onward
- **1 source(s) flagged TRUNCATED** - the digest is missing its tail
- **1 source(s) with no transcript on disk** - a doctrine nobody can re-read
- **18 open questions** - see `OPEN-QUESTIONS.md`, ranked by the decision each blocks

## Repository
- **HEAD:** 272194f 2026-10-25 Consolidate DSP/tuning knowledge into DSB\ and make it verifiable
- **working tree: 7 change(s) pending, not committed**
  - `M DSB/STATUS.md`
  - `M DSB/VERIFY.md`
  - `M dsb-status.ps1`
  - `M var/gate/ctrl-fullband.wav`
  - `M var/gate/ctrl-truncated.wav`
  - `M var/gate/ctrl-tweeter.wav`
  - `M var/gate/r.json`

## Staleness
- no document carries a "last verified against hardware" stamp older than 7 days

---
Regenerate with `powershell -File dsb-status.ps1`.
