#!/usr/bin/env python3
"""Verify the generated swap tunes against their sources - no eyeballing.

tune 1 must be a faithful transcription of tune/tune-plan.json (the exact bands the hardware
was written from) at the gains the readback confirmed.
tune 2 must keep bor8f3's gains and delays, sit inside the car's crossover map, and stay within
a stated octave-band distance of bor8f3's own curve.

Exit code 1 on any mismatch, so this can gate a future edit.
"""
import importlib.util
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)

spec = importlib.util.spec_from_file_location("idsp", os.path.join(ROOT, "tune", "idsp.py"))
idsp = importlib.util.module_from_spec(spec)
spec.loader.exec_module(idsp)
spec2 = importlib.util.spec_from_file_location("bs", os.path.join(ROOT, "tune", "build-swap-tunes.py"))
build = importlib.util.module_from_spec(spec2)
spec2.loader.exec_module(build)

fails = []


def check(cond, msg):
    print(("  ok   " if cond else "  FAIL ") + msg)
    if not cond:
        fails.append(msg)


t1_path = os.path.join(ROOT, "web", "1-car-tune-2026-10-20.idsp")
t2_path = os.path.join(ROOT, "web", "2-bor8f3-improved.idsp")
t1 = idsp.load(t1_path)
t2 = idsp.load(t2_path)
src = idsp.load(os.path.join(ROOT, "web", "bor8f3.idsp"))
with open(os.path.join(ROOT, "tune", "tune-plan.json"), encoding="utf-8") as fh:
    plan = json.load(fh)

print("both files")
for name, doc in (("tune 1", t1), ("tune 2", t2)):
    check(len(doc["memories"]) == 1, "%s: exactly 1 memory (one tune per file)" % name)
    sig = []
    for mem in doc["memories"]:
        sig.append(json.dumps([[o["gain"], o["delay"], [b for b in o["eq"]]] for o in mem["outputs"]], sort_keys=True))
    check(len(set(sig)) == 1, "%s: every memory carries the same tune (so any memory chip works)" % name)
    for mem in doc["memories"]:
        for o in mem["outputs"]:
            if len(o["eq"]) != 31 or len(o["passBand"]) != 2:
                check(False, "%s: output %d has %d bands / %d passbands" %
                      (name, o["index"] + 1, len(o["eq"]), len(o["passBand"])))

print("\ntune 1 vs tune-plan.json (the bands the DSP was written from)")
for key, p in plan["paths"].items():
    path = int(key)
    got = [b for b in t1["memories"][0]["outputs"][path]["eq"] if b["Gain"] != 0.0]
    want = sorted(p["bands"], key=lambda b: float(b["fc"]))
    ok = len(got) == len(want)
    if ok:
        for g, w in zip(got, want):
            if abs(g["Fc"] - float(w["fc"])) > 0.5 or abs(g["Gain"] - float(w["gain"])) > 0.05 \
               or abs(g["Q"] - float(w["q"])) > 0.01:
                ok = False
    check(ok, "path %d: %d bands match fc/gain/q" % (path, len(want)))

print("\ntune 1 levels (readback values)")
for path, gain in sorted(build.TUNE1_GAIN.items()):
    o = t1["memories"][0]["outputs"][path]
    check(abs(o["gain"] - gain) < 0.05 and abs(o["delay"] - build.TUNE1_DELAY[path]) < 0.05,
          "path %d: gain %+.1f dB, delay %.1f ms" % (path, o["gain"], o["delay"]))

print("\ntune 2 keeps bor8f3's levels and delays")
for path in sorted(build.CUR_HP):
    a = src["memories"][0]["outputs"][path]
    b = t2["memories"][0]["outputs"][path]
    check(a["gain"] == b["gain"] and a["delay"] == b["delay"],
          "path %d: gain %+.1f, delay %.1f unchanged" % (path, b["gain"], b["delay"]))

print("\ncrossover map (both files)")
for name, doc in (("tune 1", t1), ("tune 2", t2)):
    for path, hp in sorted(build.CUR_HP.items()):
        o = doc["memories"][0]["outputs"][path]
        hp_el = next(p for p in o["passBand"] if p["hiLowType"] == 0)
        lp_el = next(p for p in o["passBand"] if p["hiLowType"] == 1)
        check(hp_el["Enable"] == 1 and abs(hp_el["Fc"] - hp) < 0.5 and hp_el["Slope"] == 3
              and hp_el["filterType"] == 1 and lp_el["Enable"] == 0,
              "%s path %d: HP %g LR24, LP open" % (name, path, hp))

print("\ntune 2 distance from bor8f3's own curve (octave bands are what is heard)")
for path in sorted(build.CUR_HP):
    raw = idsp.part_curve(src["memories"][0]["outputs"][path]["eq"], build.GRID)
    got = idsp.part_curve(t2["memories"][0]["outputs"][path]["eq"], build.GRID)
    devs = [build.octave_avg(build.GRID, raw, f) - build.octave_avg(build.GRID, got, f) for f in build.OCT]
    check(max(abs(d) for d in devs) <= 2.0,
          "path %d: worst octave-band change %.1f dB" % (path, max(abs(d) for d in devs)))

print("\n%d failure(s)" % len(fails))
sys.exit(1 if fails else 0)

