#!/usr/bin/env python3
"""Review any .idsp tune against THIS car, and say in plain words whether it is worth loading.

The app can import a tune and show its shape. What it cannot do - and what this does - is
answer the only question that matters when someone hands you a tune: *what will it do to
this vehicle, and is it even comparable with what is playing?*

Everything here is file arithmetic. No device, no measurement, no internet.

    python tune/review-tune.py "C:\\path\\to\\someone-elses-tune.idsp"
    python tune/review-tune.py web/bor8f3.idsp --mem 1 --ref web/1-car-tune-2026-10-20.idsp
    python tune/review-tune.py some.idsp --json review.json --md review.md

The maths is the same maths as `system-predict.py` and `tune-atlas.py` - those modules are
imported, not re-implemented, so the app and this tool can never disagree.
"""
import argparse
import importlib.util
import json
import math
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)


def _load(name):
    spec = importlib.util.spec_from_file_location(name, os.path.join(HERE, name + ".py"))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


idsp = _load("idsp")
sp = _load("system-predict")
atlas = _load("tune-atlas")

ROLE_SHORT = ["tw1", "tw2", "tw3", "tw4", "3.5\" front", "3.5\" rear", "8\" front", "6.5\" rear"]
SHOW = [50, 63, 80, 100, 125, 160, 200, 250, 315, 400, 500, 630, 800, 1000, 1250, 1600,
        2000, 2500, 3150, 4000, 5000, 6300, 8000, 10000, 12500, 16000, 20000]
BAND = [f for f in range(20, 20001)]


def residual(pred, tgt, freq):
    """rms error of a predicted system curve against the house target, aligned at 300 Hz."""
    off = sp.at(freq, pred["system_energy"], 300.0)
    band = [f for f in freq if 60 <= f <= 10000]
    vals = [sp.at(freq, pred["system_energy"], f) - off - sp.at(freq, tgt, f) for f in band]
    return math.sqrt(sum(v * v for v in vals) / len(vals)), off


def flat_prediction(meas, base, freq):
    """The car as measured: EQ flat, gains as they were. The baseline every tune must beat."""
    paths = [{"path": p, "role": sp.ROLES[p], "gain": base[p], "delay": 0.0, "bands": 0,
              "curve": [meas[p][f] for f in freq]} for p in range(8)]
    energy = []
    for i in range(len(freq)):
        lin = [10.0 ** (r["curve"][i] / 20.0) for r in paths]
        energy.append(10.0 * math.log10(sum(v * v for v in lin)))
    return {"file": "car as measured", "memory": -1, "freq": freq, "paths": paths,
            "system_energy": energy, "system_coherent": energy, "crossover_mismatch": [],
            "gains": base, "delays": [0.0] * 8}


def topology(o):
    hp, lp = sp.hp_lp(o)
    hp = None if hp >= 19999 else hp
    lp = None if lp >= 19999 else lp
    return hp, lp


def census(mem):
    bands, qs, gains = [], [], []
    for o in mem["outputs"][:8]:
        for b in idsp.active_bands(o):
            bands.append(b)
            qs.append(b["Q"])
            gains.append(b["Gain"])
    notch = sum(1 for q in qs if q >= 6.0)
    return {"bands": len(bands), "q_med": idsp.median(qs), "q_max": max(qs) if qs else 0.0,
            "notch": notch, "g_min": min(gains) if gains else 0.0, "g_max": max(gains) if gains else 0.0,
            "distinct_fc": len(set(round(b["Fc"], 1) for b in bands))}


SIG_CACHE = os.path.join(HERE, "library-sigs.json")


def library_signatures(lib):
    """Payload signatures of every .idsp in web/, cached by mtime.

    Hashing eight 330 KB XML files takes ~3.4 s; the cache is keyed on mtime, so a library
    that has not changed costs a directory listing.
    """
    cache = {}
    try:
        with open(SIG_CACHE, encoding="utf-8-sig") as fh:
            cache = json.load(fh)
    except Exception:
        cache = {}
    changed = False
    sigs = {}
    for f in sorted(os.listdir(lib)):
        if not f.lower().endswith(".idsp"):
            continue
        full = os.path.join(lib, f)
        mt = os.path.getmtime(full)
        hit = cache.get(full)
        if hit and abs(hit.get("mtime", 0) - mt) < 1e-6:
            sigs[f] = hit["sigs"]
            continue
        try:
            mems = idsp.load(full)["memories"]
            sigs[f] = [atlas.signature(m) for m in mems]
        except Exception:
            continue
        cache[full] = {"mtime": mt, "sigs": sigs[f]}
        changed = True
    if changed:
        try:
            with open(SIG_CACHE, "w", encoding="utf-8") as fh:
                json.dump(cache, fh)
        except Exception:
            pass
    return sigs


def lineage(path, mem_index):
    """Is this file a copy of something already in the library?"""
    try:
        mems = idsp.load(path)["memories"]
        mine = atlas.signature(mems[mem_index if 0 <= mem_index < len(mems) else 0])
    except Exception:
        return None
    lib = os.path.join(ROOT, "web")
    for f, sigs in library_signatures(lib).items():
        full = os.path.join(lib, f)
        if os.path.abspath(full) == os.path.abspath(path):
            continue
        for mi, s in enumerate(sigs):
            if s == mine:
                return {"file": f, "memory": mi + 1}
    return None


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument("file")
    ap.add_argument("--mem", type=int, default=None)
    ap.add_argument("--ref", default=os.path.join(ROOT, "web", "2-bor8f3-improved.idsp"),
                    help="the tune to compare levels against (default: what is loaded now)")
    ap.add_argument("--meas", default=os.path.join(ROOT, "measurements", "run-20261020-1925"))
    ap.add_argument("--base", default=os.path.join(ROOT, "evidence", "tune-before-20261020-202655.json"))
    ap.add_argument("--target", default=os.path.join(HERE, "tune-plan.json"))
    ap.add_argument("--json", default=None)
    ap.add_argument("--md", default=None)
    args = ap.parse_args(argv)

    if not os.path.exists(args.file):
        raise SystemExit("no such file: " + args.file)

    meas = sp.load_meas(args.meas)
    base = sp.load_base_gains(args.base)
    doc = idsp.load(args.file)
    mi = doc["run"]["memory"] if args.mem is None else args.mem
    mem = doc["memories"][mi]
    freq = sorted(next(iter(meas.values())).keys())

    pred = sp.predict(args.file, mi, meas, base)
    flat = flat_prediction(meas, base, freq)
    with open(args.target, encoding="utf-8-sig") as fh:
        tgt = sp.target_curve(json.load(fh)["target"], freq)

    r_flat, _ = residual(flat, tgt, freq)
    r_pred, _ = residual(pred, tgt, freq)

    # level match against the reference tune
    ref_name = os.path.basename(args.ref)
    ref_pred = None
    if args.ref:
        ref_path = args.ref
        if not os.path.exists(ref_path):
            # relative paths are resolved against the project root, not the tool's folder
            for cand in (os.path.join(ROOT, ref_path), os.path.join(ROOT, "web", ref_path),
                         os.path.join(ROOT, "web", os.path.basename(ref_path))):
                if os.path.exists(cand):
                    ref_path = cand
                    break
        if os.path.exists(ref_path):
            ref_doc = idsp.load(ref_path)
            ref_mem = ref_doc["run"]["memory"]
            ref_pred = sp.predict(ref_path, ref_mem, meas, base)
            ref_name = os.path.basename(ref_path)
    if ref_pred is None:
        raise SystemExit("reference tune not found: " + args.ref)
    delta_low = []
    for f in freq:
        if f <= 800:
            delta_low.append(sp.at(freq, pred["system_energy"], f) -
                             sp.at(freq, ref_pred["system_energy"], f))
    d_lo = max(delta_low, key=abs) if delta_low else 0.0
    d_all = [sp.at(freq, pred["system_energy"], f) - sp.at(freq, ref_pred["system_energy"], f)
             for f in freq]

    # topology: what the file assumes vs what the car runs
    topo = []
    for p in range(8):
        hp, lp = topology(mem["outputs"][p])
        ehp, elp = sp.MEAS_HP[p], sp.MEAS_LP[p]
        rows = []
        if (hp is None) != (ehp >= 19999) or (hp is not None and abs(hp - ehp) > 0.5):
            rows.append("HP %s" % ("open" if hp is None else "%g Hz" % hp))
        if (lp is None) != (elp >= 19999) or (lp is not None and abs(lp - elp) > 0.5):
            rows.append("LP %s" % ("open" if lp is None else "%g Hz" % lp))
        if rows:
            topo.append((p, rows))
    file_closed_lp = any(lp is not None for lp in [topology(mem["outputs"][p])[1] for p in range(8)])

    cen = census(mem)
    lin = lineage(args.file, mi)

    # ---- verdict lines, in the words a person uses
    verdict = []
    gain = r_flat - r_pred
    if gain >= 1.0:
        verdict.append(("good", "Beats the car as measured: residual %.1f dB vs %.1f dB for the "
                               "un-tuned car (%.1f dB better)." % (r_pred, r_flat, gain)))
    elif gain >= -1.0:
        verdict.append(("warn", "A null move: within %.1f dB of the un-tuned car (%.1f dB vs "
                               "%.1f dB). This is the stock car, not a voicing."
                        % (abs(gain), r_pred, r_flat)))
    else:
        verdict.append(("bad", "Worse than the un-tuned car: residual %.1f dB vs %.1f dB."
                        % (r_pred, r_flat)))
    if abs(d_lo) > 6.0:
        verdict.append(("bad", "Level: %+.1f dB against %s below 800 Hz. Not comparable by ear - "
                               "loudness decides the preference before the voicing does."
                        % (d_lo, ref_name)))
    else:
        verdict.append(("good", "Level: within %.1f dB of %s across 50-800 Hz, so an A/B is fair."
                        % (abs(d_lo), ref_name)))
    if file_closed_lp:
        verdict.append(("warn", "Topology: this file closes its low passes (%s). The car runs them "
                               "open at 20 kHz - the fit does not transfer."
                        % ", ".join("%s LP %g" % (ROLE_SHORT[p], topology(mem["outputs"][p])[1])
                                    for p in range(8) if topology(mem["outputs"][p])[1] is not None)))
    if cen["bands"] and cen["notch"] / float(cen["bands"]) > 0.5:
        verdict.append(("warn", "Census: %d of %d bands are Q>=6 (%.0f%%). This is a measurement "
                               "correction bank, not a voicing - narrow notches corrected one "
                               "car's response."
                        % (cen["notch"], cen["bands"], 100.0 * cen["notch"] / cen["bands"])))
    if cen["q_max"] >= 8:
        verdict.append(("warn", "Census: Q reaches %.1f - narrower than a tone control."
                        % cen["q_max"]))
    if lin:
        same_name = os.path.basename(lin["file"]).lower() == os.path.basename(args.file).lower()
        verdict.append(("info", "Lineage: identical payload to %s memory %d.%s"
                        % (lin["file"], lin["memory"],
                           "" if same_name else " It is a copy of that one.")))
    else:
        verdict.append(("info", "Lineage: not a byte-copy of anything in the library."))
    if pred["crossover_mismatch"]:
        verdict.append(("info", "Crossovers in this file: " + "; ".join(pred["crossover_mismatch"])
                        + " (the prediction above corrects for this)"))

    L = []
    def out(s=""):
        print(s)
        L.append(s)

    out("=" * 78)
    out("TUNE REVIEW  -  %s  (memory %d)" % (os.path.basename(args.file), mi + 1))
    out("=" * 78)
    out("")
    out("-- what it is " + "-" * 60)
    out("  memories in file: %d   outputs: %d   active EQ bands: %d (over paths 0-7)"
        % (len(doc["memories"]), len(mem["outputs"]), cen["bands"]))
    out("  %-14s %8s %8s %6s  %s" % ("path", "gain", "delay", "bands", "crossover (file)"))
    for p in range(8):
        o = mem["outputs"][p]
        hp, lp = topology(o)
        out("  %-14s %+7.1f %7.1f %6d  HP %s / LP %s" % (
            ROLE_SHORT[p], o["gain"], o["delay"], len(idsp.active_bands(o)),
            "open" if hp is None else "%g" % hp, "open" if lp is None else "%g" % lp))
    out("  EQ: %d bands, Q %.1f median / %.1f max, gains %+.1f to %+.1f dB, %d distinct centres"
        % (cen["bands"], cen["q_med"], cen["q_max"], cen["g_min"], cen["g_max"], cen["distinct_fc"]))
    out("")

    out("-- what it would do to this car " + "-" * 44)
    out("  predicted residual vs the house target: %.1f dB rms (60 Hz-10 kHz)" % r_pred)
    out("  the car as measured, EQ flat:        %.1f dB rms" % r_flat)
    out("  level vs %s: %+.1f dB at worst below 800 Hz" % (ref_name, d_lo))
    out("")
    out("  %-8s %9s %9s %9s %8s" % ("Hz", "this tune", "as meas.", "vs ref", "spread"))
    for f in SHOW:
        out("  %-8d %9.1f %9.1f %9.1f %8.1f" % (
            f, sp.at(freq, pred["system_energy"], f), sp.at(freq, flat["system_energy"], f),
            sp.at(freq, pred["system_energy"], f) - sp.at(freq, ref_pred["system_energy"], f),
            sp.at(freq, pred["system_coherent"], f) - sp.at(freq, pred["system_energy"], f)))
    out("  (spread = coherent sum minus energy sum: 0 dB = one path owns the band and the value")
    out("   is exact; 6-9 dB = several paths at equal level, where only a measurement settles it)")
    out("")

    out("-- verdict " + "-" * 67)
    for kind, text in verdict:
        mark = {"good": "OK  ", "warn": "WARN", "bad": "NO  ", "info": "    "}[kind]
        out("  [%s] %s" % (mark, text))
    out("")

    if args.json:
        payload = {
            "file": os.path.abspath(args.file), "memory": mi,
            "gains": pred["gains"], "delays": pred["delays"],
            "residual_db": r_pred, "flat_residual_db": r_flat, "reference": ref_name,
            "level_delta_low_db": d_lo,
            "topology_mismatch": [list(x) for x in topo],
            "census": cen, "lineage": lin,
            "verdict": [{"kind": k, "text": t} for k, t in verdict],
            "system": [[round(f, 2), round(sp.at(freq, pred["system_energy"], f), 2),
                        round(sp.at(freq, flat["system_energy"], f), 2),
                        round(sp.at(freq, pred["system_coherent"], f), 2)] for f in freq],
            "source_measurement": os.path.relpath(args.meas, ROOT),
        }
        with open(args.json, "w", encoding="utf-8") as fh:
            json.dump(payload, fh, indent=1)
        print("wrote %s" % args.json)

    if args.md:
        with open(args.md, "w", encoding="utf-8", newline="\n") as fh:
            fh.write("# Review - %s (memory %d)\n\n" % (os.path.basename(args.file), mi + 1))
            fh.write("Generated by `tune/review-tune.py`. Predicted from the %s measurement; no "
                     "device was touched.\n\n" % os.path.basename(os.path.normpath(args.meas)))
            fh.write("## Verdict\n\n")
            for kind, text in verdict:
                fh.write("- **%s** - %s\n" % (kind.upper(), text))
            fh.write("\n## Numbers\n\n")
            fh.write("| | |\n|---|---|\n")
            fh.write("| predicted residual vs house target | %.1f dB rms |\n" % r_pred)
            fh.write("| the car as measured, EQ flat | %.1f dB rms |\n" % r_flat)
            fh.write("| level vs %s below 800 Hz | %+.1f dB |\n" % (ref_name, d_lo))
            fh.write("| active bands | %d |\n" % cen["bands"])
            fh.write("| Q median / max | %.1f / %.1f |\n" % (cen["q_med"], cen["q_max"]))
            fh.write("| bands with Q >= 6 | %d |\n" % cen["notch"])
            fh.write("\n## Predicted system response (dBFS at the mic)\n\n")
            fh.write("| Hz | this tune | car as measured | vs %s |\n|---|---|---|---|\n" % ref_name)
            for f in SHOW:
                if f not in freq:
                    continue
                fh.write("| %d | %.1f | %.1f | %+.1f |\n" % (
                    f, sp.at(freq, pred["system_energy"], f),
                    sp.at(freq, flat["system_energy"], f),
                    sp.at(freq, pred["system_energy"], f) - sp.at(freq, ref_pred["system_energy"], f)))
        print("wrote %s" % args.md)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
