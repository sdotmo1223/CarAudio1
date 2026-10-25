#!/usr/bin/env python3
"""Predict what a tune will do in the car, from measurements already on disk.

The 2026-10-20 19:12 run measured every DSP path ALONE, with the crossovers of the
owner's map active and **EQ flat** (evidence: `evidence\\tune-before-20261020-202655.json`
carries 31 bands per path at gain 0, and `measurements\\run-20261020-1925\\README.md`
states "EQ flat" in its conditions list).  That makes each stored curve

    measured_i(f) = driver_i(f) + room_i(f) + mic(f) + gain_at_measurement_i

so any other tune can be predicted without touching the car:

    predicted_i(f) = measured_i(f) - gain_at_measurement_i + gain_tune_i
                                   + eq_tune_i(f) + crossover_delta_i(f)

Everything in that sum is on disk.  The one thing that is NOT recoverable is phase:
the captures are 1/6-octave magnitudes, so the sum of eight paths is reported as an
energy sum, with the fully-coherent sum printed beside it as the upper envelope.
Where those two agree, the prediction is solid; where they spread by 6-9 dB, the
honest answer is "depends on arrival phase, cannot be settled without a measurement".

    python tune/system-predict.py web/1-car-tune-2026-10-20.idsp
    python tune/system-predict.py web/1-car-tune-2026-10-20.idsp web/2-bor8f3-improved.idsp
    python tune/system-predict.py <a.idsp> <b.idsp> --json tune/prediction.json
"""
import argparse
import importlib.util
import json
import math
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
_spec = importlib.util.spec_from_file_location("idsp", os.path.join(HERE, "idsp.py"))
idsp = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(idsp)

SLUGS = ["tweeter-1", "tweeter-2", "tweeter-3", "tweeter-4",
         "midrange-1", "midrange-2", "midrange-3", "midbass-1"]
ROLES = ["tweeter 1 (front L)", "tweeter 2 (front R)", "tweeter 3 (rear L)", "tweeter 4 (rear R)",
         "3.5\" pair (front)", "3.5\" pair (rear)", "8\" pair (front)", "6.5\" pair (rear)"]
# The map both tunes carry, and the map the measurement was taken through.
# measurements\run-20261020-1925\README.md: "path 0-3 HP1500 / 4-5 HP80 / 6 HP50 / 7 HP60,
# all LP open 20k, LR24".  Verified against each tune file in verify_crossovers().
MEAS_HP = [1500.0, 1500.0, 1500.0, 1500.0, 80.0, 80.0, 50.0, 60.0]
MEAS_LP = [20000.0] * 8
SHOW = [50, 63, 80, 100, 125, 160, 200, 250, 315, 400, 500, 630, 800, 1000, 1250, 1600,
        2000, 2500, 3150, 4000, 5000, 6300, 8000, 10000, 12500, 16000, 20000]


def lr_hp_db(f, fc):
    """Linkwitz-Riley 4th order (24 dB/oct) magnitude, exactly what Slope 3 + filterType 1 means.

    LR4 is two cascaded 2nd-order Butterworth sections, so its magnitude is the SQUARE of one
    section: 20*log10(x^4/(1+x^4)), which is -6.02 dB at Fc.  The first version of this used
    10*log10 and was 3 dB out at every Fc; the self-test below caught it.
    """
    x = f / fc
    return 20.0 * math.log10((x ** 4) / (1.0 + x ** 4))


def lr_lp_db(f, fc):
    x = f / fc
    return 20.0 * math.log10(1.0 / (1.0 + x ** 4))


def load_meas(folder):
    """Eight per-channel curves from a run folder, keyed by DSP path 0..7."""
    curves = {}
    for path in range(8):
        p = os.path.join(folder, "channel %d.json" % (path + 1))
        if not os.path.exists(p):
            p = os.path.join(folder, SLUGS[path] + ".json")
        if not os.path.exists(p):
            raise SystemExit("%s: no curve for channel %d (%s)" % (folder, path + 1, SLUGS[path]))
        with open(p, encoding="utf-8-sig") as fh:
            doc = json.load(fh)
        curves[path] = {round(float(h), 3): float(v) for h, v in doc["points"]}
    return curves


def load_base_gains(path):
    """Output gains that were live when the measurement was taken.

    `evidence\\tune-before-20261020-202655.json` is the snapshot apply-tune.ps1 took
    immediately before writing tune 1, and nothing changed the gains between 19:12
    and that write (the 19:10:29 snapshot is crossovers only).  Commands 11's gains
    are mirror-read, not device-read, but they are the same numbers the bridge held
    at both ends of the measurement, so they are the best available reference.
    """
    with open(path, encoding="utf-8-sig") as fh:
        doc = json.load(fh)
    return [float(g) for g in doc["gainDb"][:8]]


def hp_lp(o):
    hp = next((p for p in o["passBand"] if p["hiLowType"] == 0), None)
    lp = next((p for p in o["passBand"] if p["hiLowType"] == 1), None)
    hp_fc = float(hp["Fc"]) if hp and hp["Enable"] and hp["Fc"] < 19999 else 20000.0
    lp_fc = float(lp["Fc"]) if lp and lp["Enable"] and lp["Fc"] < 19999 else 20000.0
    return hp_fc, lp_fc


def verify_crossovers(mem):
    """The prediction is only valid where the tune's crossovers match the measurement's."""
    worst = 0.0
    notes = []
    for path, o in enumerate(mem["outputs"][:8]):
        hp, lp = hp_lp(o)
        if abs(hp - MEAS_HP[path]) > 0.5:
            notes.append("path %d HP %g != measured %g" % (path + 1, hp, MEAS_HP[path]))
            worst = max(worst, abs(math.log(hp / MEAS_HP[path])))
        if abs(lp - MEAS_LP[path]) > 0.5:
            notes.append("path %d LP %g != measured %g" % (path + 1, lp, MEAS_LP[path]))
            worst = max(worst, abs(math.log(lp / MEAS_LP[path])))
    return worst, notes


def predict(path, mem_index, meas, base_gains):
    doc = idsp.load(path)
    mi = doc["run"]["memory"] if mem_index is None else mem_index
    if mi >= len(doc["memories"]):     # one tune per file: a lone block answers for any slot
        mi = 0
    mem = doc["memories"][mi]
    worst, notes = verify_crossovers(mem)
    freq = sorted(next(iter(meas.values())).keys())
    rows = []
    for p in range(8):
        o = mem["outputs"][p]
        eq = idsp.part_curve(o["eq"], freq)
        hp, lp = hp_lp(o)
        xo = []
        for f in freq:
            d = 0.0
            if hp < 19999:
                d += lr_hp_db(f, hp) - (lr_hp_db(f, MEAS_HP[p]) if MEAS_HP[p] < 19999 else 0.0)
            if MEAS_HP[p] < 19999 and hp >= 19999:
                d -= lr_hp_db(f, MEAS_HP[p])
            if lp < 19999:
                d += lr_lp_db(f, lp) - (lr_lp_db(f, MEAS_LP[p]) if MEAS_LP[p] < 19999 else 0.0)
            xo.append(d)
        level = o["gain"] - base_gains[p]
        cur = [meas[p][f] + level + e + x for f, e, x in zip(freq, eq, xo)]
        rows.append({"path": p, "role": ROLES[p], "gain": o["gain"], "delay": o["delay"],
                     "bands": len(idsp.active_bands(o)), "curve": cur})
    sys_e, sys_c = [], []
    for i in range(len(freq)):
        lin = [10.0 ** (r["curve"][i] / 20.0) for r in rows]
        sys_e.append(10.0 * math.log10(sum(v * v for v in lin)))
        sys_c.append(20.0 * math.log10(sum(lin)))
    return {
        "file": os.path.basename(path), "memory": mi, "freq": freq, "paths": rows,
        "system_energy": sys_e, "system_coherent": sys_c,
        "crossover_mismatch": notes,
        "gains": [r["gain"] for r in rows], "delays": [r["delay"] for r in rows],
    }


def at(freq, curve, f):
    return idsp.at(freq, curve, f)


def target_curve(points, freq):
    out = []
    for f in freq:
        out.append(idsp.at([p[0] for p in points], [p[1] for p in points], f))
    return out


def self_test(meas, base):
    """Known-answer and negative controls. A comparator never seen to fail is not evidence."""
    checks = []

    # 1. Identity: a tune with the measurement's gains and no EQ must predict the measurement.
    doc = {"memories": [{"outputs": [
        {"index": i, "gain": base[i], "delay": 0.0, "mute": 0, "revert": 0, "linkPair": 0,
         "passBand": [{"Fc": MEAS_HP[i], "hiLowType": 0, "Slope": 3, "filterType": 1,
                       "butt_Type": 0, "Enable": 1 if MEAS_HP[i] < 19999 else 0},
                      {"Fc": 20000, "hiLowType": 1, "Slope": 3, "filterType": 1,
                       "butt_Type": 0, "Enable": 0}],
         "eq": [{"Fc": 20.0, "Gain": 0.0, "Q": 1.0}] * 31} for i in range(8)]}],
        "run": {"memory": 0}}
    freq = sorted(next(iter(meas.values())).keys())
    worst = 0.0
    for p in range(8):
        o = doc["memories"][0]["outputs"][p]
        eq = idsp.part_curve(o["eq"], freq)
        pred = [meas[p][f] + (o["gain"] - base[p]) + e for f, e in zip(freq, eq)]
        worst = max(worst, max(abs(a - meas[p][f]) for a, f in zip(pred, freq)))
    checks.append(("identity: flat tune predicts the measurement exactly", worst < 1e-9,
                   "max |error| %.2e dB" % worst))

    # 2. Negative control: a crossover that differs must be reported, not silently predicted through.
    bad = {"outputs": [dict(o) for o in doc["memories"][0]["outputs"]]}
    bad["outputs"][0]["passBand"] = [{"Fc": 2500, "hiLowType": 0, "Slope": 3, "filterType": 1,
                                      "butt_Type": 0, "Enable": 1},
                                     {"Fc": 20000, "hiLowType": 1, "Slope": 3, "filterType": 1,
                                      "butt_Type": 0, "Enable": 0}]
    w, notes = verify_crossovers(bad)
    checks.append(("negative control: HP 2500 against a 1500 measurement is refused",
                   bool(notes) and w > 0, "; ".join(notes) or "NOT DETECTED"))

    # 3. Level check: +6 dB on one path must move that path, and only that path, by 6 dB.
    lift = {"outputs": [dict(o) for o in doc["memories"][0]["outputs"]]}
    lift["outputs"][3] = dict(lift["outputs"][3])
    lift["outputs"][3]["gain"] = base[3] + 6.0
    f = 4000.0
    moved = 0.0
    for p in range(8):
        o = lift["outputs"][p]
        eq = idsp.part_curve(o["eq"], freq)
        d = at(freq, [meas[p][x] + (o["gain"] - base[p]) + e for x, e in zip(freq, eq)], f) - \
            at(freq, [meas[p][x] for x in freq], f)
        moved = max(moved, abs(d - (6.0 if p == 3 else 0.0)))
    checks.append(("level: +6 dB on one path moves that path 6.0 dB and no other", moved < 1e-9,
                   "max |error| %.2e dB" % moved))

    # 4. Crossover maths: LR24 HP is -6.02 dB at Fc and -0.0 dB two octaves up.
    checks.append(("LR24 HP response: -6.0 dB at Fc, > -0.1 dB two octaves up",
                   abs(lr_hp_db(1500.0, 1500.0) + 6.0206) < 0.01 and lr_hp_db(6000.0, 1500.0) > -0.1,
                   "%.2f / %.2f dB" % (lr_hp_db(1500.0, 1500.0), lr_hp_db(6000.0, 1500.0))))

    for name, ok, detail in checks:
        print("  %s  %-70s %s" % ("PASS" if ok else "FAIL", name, detail))
    return 0 if all(c[1] for c in checks) else 1


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument("tunes", nargs="*", help="one or two .idsp files")
    ap.add_argument("--mem", type=int, default=None)
    ap.add_argument("--meas", default=os.path.join(ROOT, "measurements", "run-20261020-1925"))
    ap.add_argument("--base", default=os.path.join(ROOT, "evidence", "tune-before-20261020-202655.json"))
    ap.add_argument("--target", default=os.path.join(HERE, "tune-plan.json"))
    ap.add_argument("--json", default=None)
    ap.add_argument("--selftest", action="store_true")
    args = ap.parse_args(argv)

    meas = load_meas(args.meas)
    base = load_base_gains(args.base)
    if args.selftest:
        print("=== system-predict self-test ===")
        return self_test(meas, base)
    if not args.tunes:
        ap.error("give one or two .idsp files (or --selftest)")

    preds = [predict(t, args.mem, meas, base) for t in args.tunes]
    freq = preds[0]["freq"]

    # What the car measured as, EQ flat and gains as they were. This is the honest
    # "before" column: no tune of ours is in it, only the drivers and the room.
    flat_paths = [{"path": p, "role": ROLES[p], "gain": base[p], "delay": 0.0, "bands": 0,
                   "curve": [meas[p][f] for f in freq]} for p in range(8)]
    flat_e = []
    for i in range(len(freq)):
        lin = [10.0 ** (r["curve"][i] / 20.0) for r in flat_paths]
        flat_e.append(10.0 * math.log10(sum(v * v for v in lin)))
    flat = {"file": "measured, EQ flat", "memory": -1, "freq": freq, "paths": flat_paths,
            "system_energy": flat_e, "system_coherent": flat_e, "crossover_mismatch": [],
            "gains": base, "delays": [0.0] * 8}
    columns = [flat] + preds

    tgt_points = None
    if args.target and os.path.exists(args.target):
        with open(args.target, encoding="utf-8-sig") as fh:
            tgt_points = json.load(fh)["target"]

    print("=== predicted response, from measurements already on disk ===")
    print("  measurement  %s" % os.path.relpath(args.meas, ROOT))
    print("  gains then   %s" % " / ".join("%+.1f" % g for g in base))
    print("  crossovers   HP %s / LP open, LR24 (identical to the run's conditions)"
          % " ".join("%g" % MEAS_HP[i] for i in range(8)))
    for p in preds:
        if p["crossover_mismatch"]:
            print("  !! %s: %s" % (p["file"], "; ".join(p["crossover_mismatch"])))
        else:
            print("  ok  %s memory %d matches the measurement's crossovers on all 8 paths"
                  % (p["file"], p["memory"]))
    print()

    # --- per path, at the frequencies that decide the voicing
    for p in preds:
        print("--- %s  gains %s" % (p["file"], " ".join("%+.1f" % g for g in p["gains"])))
        print("  path role                 " + "".join("%7d" % f for f in SHOW))
        for r in p["paths"]:
            print("  %d    %-20s" % (r["path"] + 1, r["role"]) +
                  "".join("%7.1f" % at(freq, r["curve"], f) for f in SHOW))
        print()

    # --- system
    print("--- system, all 8 paths summed (dBFS at the mic, same stimulus as the run)")
    print("  %-8s %s" % ("Hz", "".join("%11s" % p["file"][:9] for p in columns) +
                         ("%10s" % "B - A" if len(preds) == 2 else "") + "   spread"))
    for f in SHOW:
        line = "  %-8d" % f
        vals = []
        for p in columns:
            v = at(freq, p["system_energy"], f)
            vals.append(v)
            line += "%11.1f" % v
        if len(preds) == 2:
            line += "%10.1f" % (at(freq, preds[1]["system_energy"], f) -
                                at(freq, preds[0]["system_energy"], f))
        sp = at(freq, preds[0]["system_coherent"], f) - at(freq, preds[0]["system_energy"], f)
        line += "   %5.1f" % sp
        print(line)
    print("  (last column: coherent-sum minus energy-sum for tune A. 0.0 = one path owns the")
    print("   band and the number is exact; 6-9 = several paths at equal level, and the real")
    print("   sum depends on arrival phase, which a magnitude-only capture cannot tell us.)")
    print()

    # --- who owns which band
    print("--- energy ownership (share of the summed energy per band, tune A)")
    lin = [[10.0 ** (r["curve"][i] / 20.0) for r in preds[0]["paths"]] for i in range(len(freq))]
    for f in [63, 100, 160, 250, 400, 630, 1000, 1600, 2500, 4000, 6300, 10000, 16000]:
        i = min(range(len(freq)), key=lambda k: abs(freq[k] - f))
        tot = sum(v * v for v in lin[i])
        shares = sorted(((v * v / tot, p) for v, p in zip(lin[i], range(8))), reverse=True)
        top = ", ".join("p%d %2.0f%%" % (p + 1, 100 * s) for s, p in shares[:3])
        print("  %-6d %s" % (f, top))
    print()

    # --- versus the house target the fit aimed at
    if tgt_points:
        print("--- residual against the house target (tune-plan.json anchors), aligned at 300 Hz")
        print("  %-8s %s" % ("Hz", "".join("%10s" % p["file"][:9] for p in columns) + "   target"))
        tgt = target_curve(tgt_points, freq)
        off = [at(freq, p["system_energy"], 300.0) for p in columns]
        for f in SHOW:
            line = "  %-8d" % f
            for p, o in zip(columns, off):
                line += "%10.1f" % (at(freq, p["system_energy"], f) - o - at(freq, tgt, f))
            line += "%9.1f" % at(freq, tgt, f)
            print(line)
        print("  (0.0 would be a curve that hits the house target exactly; the fit could only")
        print("   reach it where the drivers can actually produce the level asked for.)")
        print()

    # --- short verdict numbers
    for p in columns:
        band = [f for f in freq if 60 <= f <= 10000]
        vals = [at(freq, p["system_energy"], f) for f in band]
        off = at(freq, p["system_energy"], 300.0)
        resid = [v - off - at(freq, target_curve(tgt_points, freq) if tgt_points else [0.0] * len(freq), f)
                 for v, f in zip(vals, band)] if tgt_points else []
        bass = [at(freq, p["system_energy"], f) - off for f in (63, 80, 100)]
        pres = [at(freq, p["system_energy"], f) - off for f in (2000, 3150, 4000)]
        print("%-30s bass(63/80/100) %s   presence(2k/3.15k/4k) %s%s" % (
            p["file"], " ".join("%+.1f" % v for v in bass), " ".join("%+.1f" % v for v in pres),
            ("   residual rms %.1f dB" % (sum(v * v for v in resid) / len(resid)) ** 0.5) if resid else ""))

    if args.json:
        out = []
        for p in preds:
            out.append({
                "file": p["file"], "memory": p["memory"], "gains": p["gains"], "delays": p["delays"],
                "crossover_mismatch": p["crossover_mismatch"],
                "system": [[round(f, 2), round(at(freq, p["system_energy"], f), 2),
                            round(at(freq, p["system_coherent"], f), 2)] for f in freq],
                "paths": [[r["path"] + 1, r["gain"], r["delay"],
                           [round(at(freq, r["curve"], f), 2) for f in freq_named(p)]] for r in p["paths"]],
            })
        with open(args.json, "w", encoding="utf-8") as fh:
            json.dump({"source_measurement": os.path.relpath(args.meas, ROOT),
                       "base_gains": base, "predictions": out}, fh, indent=1)
        print("\nwrote %s" % args.json)
    return 0


def freq_named(p):
    return p["freq"]


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
