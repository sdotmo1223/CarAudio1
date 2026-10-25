# OPEN QUESTIONS — the single register of what this project does not know

**This replaces every other "still unknown" list.** `TUNE-PLAYBOOK.md` §15,
`EQUIPMENT.md` "Still unknown", `refs\README.md` §4, `TUNING-BRIEF.md` §6 and
`APP-NOTES.md` §18 each kept their own copy, written at different times, and by 2026-10-25
they disagreed about what ranked first. They now link here instead. If you add an unknown,
add it **here** and leave a pointer in the document that cares — do not start a sixth list.

**How to read it:** ranked by *how much a real answer would change the tune*. A question that
blocks nothing is not worth asking yet.

**Columns:** *blocks* = the decision or claim that cannot be made without it · *answer* =
the cheapest thing that would settle it · *needs* = car / mic / cable / owner / none.

---

## Tier A — these would change the tune

| ID | question | blocks | answer | needs | status |
|---|---|---|---|---|---|
| **Q1** | **Has the tune ever been measured after it was written?** Every residual in this repo (tune 1 = 3.6 dB, tune 2 = 7.1 dB, the headline 2.1 dB) is *predicted* from a pre-tune capture. | the headline accuracy claim itself | apply the tune, capture again, compare predicted to measured | car, mic | **OPEN — never attempted** |
| **Q2** | **The sub has never been measured.** No capture exists; `run-20261020-1925` is eight files with the sub amp disconnected. Its level is head-unit trim plus the Kicker knob, invisible to every tool here. | every target below 80 Hz, sub integration | one capture with the sub amp up | car, mic | **OPEN — deferred by the owner 2026-10-23** |
| **Q3** | **The summed response has never been measured.** §9.0.2. No summing run exists, so polarity and the real combined curve are unverifiable. | polarity, crossover type, the actual curve | all outputs up, one capture, nothing muted | car, mic | **OPEN — tooling exists** |
| **Q4** | **Do the low-passes stay open?** He asked for it for the measurement campaign, and with the cones open tune 1 lands **+5 to +7 dB above target at 1.25–2 kHz**. | every re-fit — the EQ fitted to the open map does not transfer to a closed one | owner's decision, then re-measure | **owner** | **DECISION REQUIRED** |
| **Q5** | **Crossover points and types are both unvalidated.** All low-passes open, LR24 hard-coded on every path, never compared to anything. §9.0.3 and §6.1 both converge on closing the cone low-passes. | where every handover sits | measure raw, place points with a full octave of overlap, then the per-pair sum at Fc | car, mic | **OPEN — three lines of evidence already converge** |

## Tier B — blocks a feature or a safety claim

| ID | question | blocks | answer | needs | status |
|---|---|---|---|---|---|
| **Q6** | ~~**Does cmd 11 have a readback anywhere in the vendor protocol?**~~ **ANSWERED 2026-10-25 — YES.** See the box below. Remaining work: use it for polarity, which has never actually been decoded from a live read. | — | `GET /api/dspcfg`, decode `memOut[mem]` | **DSP connected** | **ANSWERED — readback exists** |
| **Q7** | **True arrival times.** The delays (5.4/3.8/6/4.1/4.6/5/3.8/4.4 ms) are inherited and internally consistent with the repo's speaker distances, but their absolute offset was never measured. | time alignment, phase alignment, §9.0.1 | a 3.5 mm → RCA loopback cable, then `measure-sweep.ps1` | **cable** | **BLOCKED on hardware** |
| **Q8** | **Sub version — T2D212 or T2D412?** Decides whether the Kicker sees 1 Ω/4 Ω or 2 Ω/8 Ω, i.e. 1800 W or 900 W. | power budget, impedance safety | read the driver label | **owner** | OPEN |
| **Q9** | **Where does the Kicker get its signal?** Head-unit sub output, factory sub output, or a spliced DSP pre-out. Decides whether the DSP can mute it at all — today it cannot. | whether the sub can ever be muted in software | trace the wiring | car | OPEN |
| **Q10** | **Where do the Kicker's LP and KickEQ actually sit?** The only two sub settings that can be wrong with no readback showing it. | sub integration correctness | set them by hand and record the positions | car | OPEN — no readback exists |
| **Q11** | **Is anything measured from a second seat?** Everything fits from the driver's ear. A tune perfect there can be worse a shoulder-width away. | whether the tune is a car tune or a driver tune | one capture from the passenger seat | car, mic | OPEN |
| **Q12** | **One target at one volume.** The DSP has **four memories**, used as tune slots rather than level-dependent targets. Equal-loudness says bass and treble want lift at low volume. | loud vs quiet listening | ISO 226-based targets; two captures at two volumes | car, mic | OPEN |

## Tier C — documentation and verification debt

| ID | question | blocks | answer | needs | status |
|---|---|---|---|---|---|
| **Q13** | **Is the Audiofrog "never combine delay with a polarity reversal" rule real?** The site returns teaser text only to a fetcher, so it could not be read directly. | trust in that specific rule | a human opens the page and reads it | **owner** | **UNVERIFIED — do not rely on it** |
| **Q14** | **3.5" pair wiring — 2 Ω parallel or 8 Ω series?** Amp channel supports 2 Ω, so parallel is likely, never confirmed. | power per driver (65 W vs 40 W) | measure the resistance across the pair | car | OPEN |
| **Q15** | **Which pre-out feeds the 8" pair?** Cosmetic unless the two ever get different gains. | nothing today | trace it | car | OPEN, low priority |
| **Q16** | **A fresh cmd 15 config dump.** The 19:19 one exists but predates tune 2. | knowing what the device actually holds now | pull it with the DSP connected | car | OPEN |
| **Q17** | **The `1.5 kHz notch` claim is not reproducible from the file.** The app cannot reconstruct it; `B-TUNE-REVIEW.md` is the source, not the app. | using the app as evidence for that number | re-read `B-TUNE-REVIEW.md`; do not cite the app | none | OPEN |
| **Q18** | **Six app claims are code-complete but not hardware-proven** — a swap really writes the amp, a readback diff is clean live, restore writes back what it snapshotted, a horn click mutes that speaker, the crossover comparator against the real amp, time alignment. | trusting the desktop app's verdicts | plug the DSP in and run each one | car | OPEN — see `APP-NOTES.md` §18 |

---

## Q6 — the answer: cmd 11 *can* be read back

Found 2026-10-25 by reading `..\DS18_DSP_protocol.md` §15 and `..\decompiled\AP.cs`
first-hand, because two earlier sessions concluded those files did not exist.

**ReadParam sub 1 (`StructDspCfg`) carries the output block for all four memories.**

| what | where |
|---|---|
| the field | `StructDspCfg.memOut[4]` — `AP.cs:478`, `src\Protocol.cs:425` |
| byte offsets in the sub-1 body | mem0 @ **1692**, mem1 @ **1760**, mem2 @ **1828**, mem3 @ **1896** |
| layout at that offset | `enable` u16, `revert` u16, `gainIntValue[16]` u16, `delayIntValue[16]` u16 |
| decode | gain dB = `-u16/10` · delay ms = `u16/100` · **polarity = `revert & (1<<path)`** · mute = `!(enable & (1<<path))` |
| how to fetch | `GET /api/dspcfg` → `bodyHex` (bridge already exposes it raw) |

**The vendor app does exactly this.** `AP.cs:10426-10448` loops the four memories and decodes
`enableOutput` → `bMute`, `revertOutput` → **`b180Dge`**, `gainIntValue` → `mGain` (÷10, negated)
and `delayIntValue` → `mDelay` (÷100) into `mDP12ReadFromDsp`. Then `AP.cs:10187` runs
`mDP12ReadFromDsp.CompareData(mDP12SetData)` — device read against the app's own mirror — and
`DP12_SetData.CompareData` (`AP.cs:11955`) walks all four memories × 17 outputs to do it.

**This repo already does it too.** `load-tune.ps1` step 4c fetches `/api/dspcfg` and decodes
`enable` at byte **1760** (mem1) to compare the output block against the file. `src\BridgeServer.cs:1639`
says so in as many words: *"the per-memory output block (enable/revert/gain/delay for all sixteen
outputs) all live in this body — cmd 11/18/12 write them but no write ever needs to be guessed at
when the device can just be asked."*

**So why does everything say it cannot be read?** Because five places assert the opposite, all
traceable to one comment:

| file | the claim |
|---|---|
| `src\Protocol.cs:755` | "no ReadParam sub-command covers output config" |
| `src\BridgeServer.cs:76` | "no ReadParam sub-command covers output mute/gain/delay/polarity" |
| `web\index.html:6192` | same sentence, in the UI |
| `app\src\Mute.cs:49`, `Swap.cs:314`, `SurfaceTune.cs:17` | "command 11 has no readback" |
| `TUNE-PLAYBOOK.md` §9.0.2, `APP-NOTES.md` §3 | propagated upward |

Those statements are **true of cmd 11 itself** — the command has no ACK and no response
payload — and **false as a statement about verifiability**, because sub 1 carries the same data.
`BridgeServer.cs` contradicts itself in the same file, 1,560 lines apart.

**Honest limits.**

- Capability for `enable`, `gain` and `delay` is **proven on hardware** (2026-10-16, a botched
  −3.0 dB block caught by sub-1 readback and corrected).
- `revert` sits at the same offset and the vendor decodes it, but **this project has never
  decoded polarity from a live read** — no tool here reads offset +2. That is the remaining step.
- The app's `MIRROR ONLY` label and its `MuteClaimsVerified` mutation guard are conservative
  rather than wrong: the app genuinely does not implement this readback, so it must not claim
  it verified anything. **Do not relax that guard** — it is what stops the app lying.
- Running any of this needs the DSP connected.

**What this changes:** §9.0.2's blocker was "polarity cannot be echoed". It can. A polarity
verification is now a code change plus a car session, not an impossible thing.

---

## What is *not* on this list

Settled, and recorded in the playbook rather than here: the hard limits (§3), the polarity
table for LR orders (§6.1), what a valid capture is (§5), and the fact that `DS18_DSP_protocol.md`
and `decompiled\AP.cs` **do exist** — one directory above this project, a verdict two earlier
sessions got wrong. `lint-refs.ps1` now reports both as cross-boundary warnings so that
cannot recur silently.

---

*Created 2026-10-25, absorbing `TUNE-PLAYBOOK.md` §15, `EQUIPMENT.md` "Still unknown",
`refs\README.md` §4, `TUNING-BRIEF.md` §6 and `APP-NOTES.md` §18. Those sections are now
pointers here.*
