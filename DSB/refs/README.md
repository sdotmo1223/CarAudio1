# tune\refs — the tuning knowledge base

**What this is.** The owner's standing plan is to feed every piece of tuning material he finds
into this project, so that the knowledge is *accounted for whenever he asks for a tune from
anyone* — a person, or whichever agent happens to be running that session. Source material lives
here; the rules derived from it live in `TUNE-PLAYBOOK.md` §9.0 onward.

**This folder is the evidence, not the doctrine.** Never tune from a file in here directly. Read
the playbook section it produced.

Created 2026-10-23.

---

## 1. How to hand over material

Video transcripts are the common case and the owner cannot always get them. Whatever arrives:

1. **Save it here verbatim**, one file per source, in `<source>\<slug>.txt`.
2. **Prefix it with a provenance header**: who it is, what it is, the date it was taken down, and
   a pointer to the playbook section that was derived from it. Mark it **SOURCE MATERIAL, not
   doctrine**.
3. **Flag truncation explicitly.** `crossovers-and-targets.txt` stops mid-sentence around 19:22;
   it says so in its own header. A transcript that silently ends is worse than one that admits it.
4. **Then digest it into the playbook** and note the section number back in the header.

Getting a transcript: YouTube → expand the description → **Show transcript** → copy → paste here.
A raw transcript is more useful than a summary, because the digest can quote the actual numbers.

**Written sources are better than video.** A forum post, a vendor guide or a manual page can be
read directly, cited precisely, and searched later. Video costs the owner four clicks per
transcript and loses everything that was demonstrated rather than said.

---

## 2. The doctrine hierarchy — read this before digesting anything

External sources are **doctrine**. This project's measurements are **evidence**. When they
conflict, **the evidence wins**, and the playbook says so at the head of every §9.0.x section.

That is not politeness. Three concrete cases already:

- §7 is explicit that delays cannot be derived from magnitude-only captures. Rawcat's phase-
  alignment method is sound doctrine and is **blocked here** until a loopback cable exists
  (§7). Recording it as a rule without recording the block would waste the next session's time.
- He tunes a car where **every driver has its own DSP channel**. This one has mono pairs on
  paths 4–7. His left/right matching step (§9.0 rule 3) applies to the tweeters only.
- His sub is inside his DSP. Ours is outside it (§1), so §9.0's sub rules have no DSP lever at
  all and become a manual measurement step on the Kicker knob.

### Standing rules, learned the hard way

- **Map every rule onto this rig before writing it down.** A transcribed rule that cannot be
  executed here is noise, and worse, it looks like noise nobody is allowed to question.
- **"Clean" means SMOOTH, not FLAT.** §9.0.3. An analysis that tested flatness against each
  driver's own median reported "no clean overlap" for every adjacent pair, because inside a car
  cabin that metric measures the room — modes below 300 Hz, absorption above ~4 kHz. A tilted
  but smooth response is a fine crossover region. Judge roughness (local peak-to-peak in a 1/3-oct
  window), never level.
- **Distinguish what is measured from what is inferred.** Every §9.0.x section separates a
  measured fact from a physics inference. Keep that separation; a confident inference that reads
  like a measurement is how a project ends up tuning against something that was never observed.
- **Do not let doctrine override a safety rule.** §3's 1500 Hz tweeter floor, the −12/+3/+4 dB
  EQ caps and the "sub amp off for every measurement" rule are not negotiable because a video
  suggested a different number. Rawcat's own protection-first instinct agrees with all three.
- **One or two sources at a time, each followed by "does this change a build?"** §9.0 worked
  because it revealed a stage that was switched off and produced a real file. Six digests at once
  would bury the load-bearing rules under commentary.

---

## 3. Index

### RAW-CAt — "Back to basics" + 5-way series

Practitioner material, car audio, competition-grade builds. Strong on *method*, weaker on
*transferability*: his rig has a channel per driver and analyzers this project does not have.

| file | playbook section | status |
|---|---|---|
| `rawcat\fine-tuning-5way.txt` | §9.0.1 — phase, summation ladder, crossover **type** | complete |
| `rawcat\summing.txt` | §9.0.2 — cumulative summing, polarity, the missing measurement | complete |
| `rawcat\crossovers-and-targets.txt` | §9.0.3 — measure raw, place points, octave of overlap | **TRUNCATED at ~19:22** (sub section missing) |
| *(level matching — youtube `Ye0pQHxE49Y`)* | §9.0 — shape vs level, weakest link, pull everything down | **TRANSCRIPT NOT ON DISK** |

**Known gap:** §9.0 was digested from a chat paste before this folder existed, so its source is
the one entry with no file. The section is complete and was verified against a full transcript at
the time; the provenance is what is missing. Re-saving it is a two-minute job and it should be
done — a doctrine section whose source nobody can re-read is exactly the thing §15 warns about.

**Unwatched, confirmed to exist** (in his "Back to basics" playlist):
`how to EQ by ear` · `tuning by ear vs RTA with a mic` · `rear fill`.
Skip the first: the DIYMobileAudio consensus advises watching the playlist *except* that one, and
this toolchain is measurement-driven by design.

### Filter theory — written sources, not video

| file | playbook section | status |
|---|---|---|
| `filter-theory\sources.md` | §6.1 — slope, filter type and polarity | 3 of 4 claims verified from primary sources; 1 UNVERIFIED |

Rane Note 160, Linkwitz Lab, ESP Project 253 and the miniDSP Flex Eight manual, read directly and
quoted. Written sources beat video here: every claim could be checked against the *section* it
came from, which is exactly what caught a misattribution (see §6.1's correction).

### CarAudio1 — the owner's own earlier project

| where | what | status |
|---|---|---|
| `..\..\evidence\caraudio1\upstream\` | live `git clone` of `github.com/sdotmo1223/CarAudio1` | cloned 2026-10-25, origin of `EQUIPMENT.md`'s driver table |
| `..\..\evidence\caraudio1\repo\` | an older flat snapshot of the same repo | kept, no longer the only copy |

The repo is the source `EQUIPMENT.md` cites for the driver table: `make_tune_v4.py` /
`make_tune_v5.py` list the drivers against the DSP outputs in their docstrings. It also holds
`rew_to_idsp.py`, `peq.py`, `mdat2csv.py`, `tune.py`, a `measurements\` tree and a
`MEASUREMENT-GUIDE.md` — **a working REW-to-DS18 path, which is the original question this
project started from.** Its twenty `.idsp` files belong to that project, not to this one's
library, and `tune\dsb-catalog.py` lists them separately so the library count stays honest.

This entry also records two project-level gaps it exposed, **both since corrected on
2026-10-25 — the original verdicts were wrong**:

- **`DS18_DSP_protocol.md` does exist.** It was reported as "not in this repo", which was
  true of `ds18-bridge\` and false of the workspace: the file is 144 KB and sits one
  directory up at `..\DS18_DSP_protocol.md`, carrying §1–§37 of measured protocol truth
  (§15 cmd 11, §16 cmd 13, §31, §37). `verify-web.ps1` cites it in a comment and was
  probably always reading it from there. This matters: an undocumented cmd-11 readback is
  the one thing that would clear §9.0.2's blocker, and the document that would contain it
  was being reported as absent.
- **`decompiled\AP.cs` does exist**, same directory level. The 2026-10-23 task said to check
  the vendor app's slope/filterType dropdowns in `AP.cs`; that check was skipped on a false
  "no such file" verdict and has still not been done.
- **`iya1m4.idsp` exists too**, at `C:\Users\sdotm\Downloads\` alongside twenty other
  `.idsp` files — outside this project, which is what "not in this library" always meant.
  Any measurement attributed to it was still made against a different project's tune and
  must be re-derived here before it is trusted; only the *file's absence* was wrong.

`lint-refs.ps1` now indexes the whole workspace rather than the project folder and reports
all three as **cross-boundary warnings** instead of silently missing, so the verdict cannot
be reproduced by accident.

---

## 4. What this project still does not know

> **Superseded 2026-10-25.** This list was one of five copies that had drifted apart.
> Every unknown now lives in **`OPEN-QUESTIONS.md`** — one register, ranked by which
> decision each question blocks. Do not add an unknown here; add it there and leave a
> pointer in the document that cares.

## 5. Adding a source

```
DSB\refs\<source>\<slug>.txt     verbatim, provenance header, truncation flagged
TUNE-PLAYBOOK.md §9.0.n          the digest, mapped to this car, evidence-vs-doctrine stated
STATE.md                          only if it changed a build or closed a decision
```

If a source produces no playbook section, say so and say why. "Read it, nothing applied to this
rig" is a legitimate and useful outcome; padding the playbook to justify the read is not.
