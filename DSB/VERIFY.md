# VERIFY - one verdict across every gate

Run 2026-10-25 11:38. Every gate below is read-only and needs no hardware.

| gate | result | time | what it proves |
|---|---|---|---|
| `lint-refs` | PASS | 5921 ms | every reference in DSB\ resolves to a real file |
| `verify-web` | PASS | 3219 ms | the inline script parses, every id the script reaches for exists, the measurement-baseline classifier still classifies |
| `verify-sweep` | PASS | 22416 ms | the analyser rejects a partial or missing sweep and accepts a real one |
| `verify-swap` | PASS | 901 ms | the A/B swap tunes are byte-consistent with what they claim to be |

**All gates green.**

Not covered here, and never can be from a desk: the DSP writes, the flatten and its
restore, the measurement run, the freeze and the microphone. Those are proved on the
rig with `doctor.ps1` and `rehearse.ps1`. A green page here says nothing about them.

Not run by default: `app\self-test.ps1` (`-All`, builds the app and needs it closed),
`doctor.ps1` (needs the car), `compare-fingerprints.ps1` and `rehearse.ps1` (need args
or a run). See `START-HERE.md` for the session-start sequence.
