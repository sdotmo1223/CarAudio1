#!/usr/bin/env python3
"""Build the two swap tunes for the Tune tab, from evidence rather than by hand.

    tune 1   last night's measured tune (2026-10-20), transcribed from tune/tune-plan.json
    tune 2   bor8f3's voicing, structurally rebuilt: same target curve, sane filters

Both land in web\\ as .idsp files so the bridge serves them to the Tune tab. Each holds ONE
<memory> (index 0) under the owner's rule of 2026-10-22 - see build_single(); the older
"same tune in all four memory blocks" behaviour below was retired by that rule. bor8f3.idsp
is not modified.

What "structurally rebuilt" means here, i.e. why tune 2 is not just bor8f3 copied:

bor8f3's bands are all Q 10-16 on a ~1/10-octave ladder. A Q-15 band's -3 dB width is narrower
than the space between bands, so the bands do not overlap: the file *displays* a smooth target
but the hardware produces a sawtooth (measured 11-17 dB peak-to-peak inside 200-8000 Hz, and
adjacent slots alternating +3 / -6 that cancel each other). Its gains also saturate: +3.0 appears
6-10 times per output, and three identical -6.0 slots in a row stand in for one shelf.

So tune 2 fits the SAME response with broad filters (Q 0.7-2.8), keeps every isolated deep notch
(a resonance is real, a comb is not), and costs 5-12 bands instead of 27-31.

Usage
    python tune/build-swap-tunes.py
    python tune/build-swap-tunes.py --dry-run      # report only, write nothing
"""
import argparse
import copy
import importlib.util
import io
import math
import os
import sys
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)

spec = importlib.util.spec_from_file_location("idsp", os.path.join(HERE, "idsp.py"))
idsp = importlib.util.module_from_spec(spec)
spec.loader.exec_module(idsp)

# Dense grid for fitting: finer than 1/6 octave so a narrow notch cannot hide between points.
GRID = [20.0 * (2.0 ** (k / 48.0)) for k in range(481)]

# The car's crossover map as the owner asked for it (STATE.md, 2026-10-20): tweeters 1500,
# 3.5" 80, 8" 50, 6.5" 60, every low-pass open, all LR24 (slope 3, filterType 1).
CUR_HP = {0: 1500, 1: 1500, 2: 1500, 3: 1500, 4: 80, 5: 80, 6: 100, 7: 60}   # owner, 2026-10-22: 8" HP 50 -> 100
LR24 = 3
LR_TYPE = 1

# Gains and delays the 2026-10-20 measurement tune was verified at (readback, not the plan's
# deltas). Delays are the ones bor8f3 carries too - the same physical distances.
TUNE1_GAIN = {0: -6.8, 1: -8.1, 2: -9.0, 3: -8.1, 4: -8.3, 5: -3.0, 6: -30.0, 7: -22.1}
TUNE1_DELAY = {0: 5.4, 1: 3.8, 2: 6.0, 3: 4.1, 4: 4.6, 5: 5.0, 6: 3.8, 7: 4.4}
TUNE1_MASTER = -4.8

# Unpopulated outputs (the amp has 6 amplified + 2 pre-out paths; 9-17 drive nothing).
SPARE_GAIN = -3.0


# ---------------------------------------------------------------- curve maths

def smooth(curve, freqs, octaves=0.333):
    """Moving average over a log-frequency window - the designer's intent, not the teeth."""
    out = []
    half = octaves / 2.0
    for f in freqs:
        lo, hi = f * 2.0 ** (-half), f * 2.0 ** (half)
        vals = [v for g, v in zip(freqs, curve) if lo <= g <= hi]
        out.append(sum(vals) / len(vals) if vals else 0.0)
    return out


def rms(xs):
    return math.sqrt(sum(x * x for x in xs) / len(xs)) if xs else 0.0


def fit(target, freqs, max_bands=12, qs=(0.7, 1.0, 1.4, 2.0, 2.8), floor=0.15):
    """Greedy broad-band fit, then coordinate descent. Only accepts improvements.

    The candidate is not simply the largest residual: that point is usually the edge of the
    audio band (a filter centred there can only move the edge, so the fit stalls at zero bands
    - watched it happen). Instead the strongest residual peaks are tried in turn and the one
    whose filter actually removes the most error wins.
    """
    bands, resid = [], list(target)
    for _ in range(max_bands):
        cur = rms(resid)
        cands = []
        order = sorted(range(len(resid)), key=lambda k: -abs(resid[k]))
        for k in [k for k in order if 25.0 <= freqs[k] <= 18000.0][:16]:
            f0 = freqs[k]
            for q in qs:
                g = max(-15.0, min(4.0, resid[k]))
                r = idsp.peaking_db(f0, q, g, freqs)
                cands.append((rms([a - b for a, b in zip(resid, r)]), q, f0, g))
        if not cands:
            break
        best_err = min(c[0] for c in cands)
        if best_err > cur - 0.02:
            break
        # Among candidates that are effectively as good, take the BROADEST filter. Without this
        # every band lands on the narrowest Q allowed (watched it pin at 15.2 in the source and
        # then at 2.8 in my own fit) because narrow always wins on point-by-point error, which
        # is exactly how the comb being fixed got built.
        _, q, f0, g = min([c for c in cands if c[0] <= best_err + 0.03],
                          key=lambda c: (c[1], abs(c[3])))
        bands.append({"Fc": f0, "Gain": g, "Q": q})
        resid = [a - b for a, b in zip(target, sum_bands(bands, freqs))]
    return refine(bands, target, freqs, qs)


def sum_bands(bands, freqs):
    total = [0.0] * len(freqs)
    for b in bands:
        for i, v in enumerate(idsp.peaking_db(b["Fc"], b["Q"], b["Gain"], freqs)):
            total[i] += v
    return total


def refine(bands, target, freqs, qs):
    """Coordinate descent on gain and Q. A peaking filter's response is not linear in gain, so
    this searches; it only ever accepts a lower error."""
    best_err = rms([a - b for a, b in zip(target, sum_bands(bands, freqs))])
    for _ in range(2):
        for b in bands:
            for q in qs:
                g0 = b["Gain"]
                for step in (q != b["Q"]) and [0.0] or [d / 10.0 for d in range(-15, 16)]:
                    b["Q"], b["Gain"] = q, round(g0 + step, 4)
                    err = rms([a - c for a, c in zip(target, sum_bands(bands, freqs))])
                    if err < best_err - 1e-6:
                        best_err = err
                        g0 = b["Gain"]
                    else:
                        b["Q"], b["Gain"] = q, g0
    return bands


def notches(raw, fit_curve, freqs, limit=2, depth=4.0):
    """Keep isolated deep dips. A 1/10-octave notch can be a real resonance (cone breakup,
    cabinet mode); a repeating sawtooth cannot. Only minima that stand depth dB below the fitted
    trend, with neighbours coming back up, qualify."""
    out = []
    for _ in range(limit):
        resid = [a - b for a, b in zip(raw, fit_curve)]
        if not resid:
            return out
        i = min(range(len(resid)), key=lambda k: resid[k])
        if resid[i] > -depth:
            break
        f0 = freqs[i]
        # width at -3 dB of the dip decides the Q
        j = i
        while j > 0 and resid[j] < resid[i] / 2.0:
            j -= 1
        k = i
        while k < len(resid) - 1 and resid[k] < resid[i] / 2.0:
            k += 1
        width_oct = math.log(freqs[k] / freqs[j], 2) if freqs[j] > 0 else 0.2
        q = max(2.0, min(8.0, 1.0 / max(width_oct, 0.05)))
        out.append({"Fc": f0, "Gain": max(-15.0, resid[i]), "Q": q})
        fit_curve = [a + b for a, b in zip(fit_curve, idsp.peaking_db(f0, q, resid[i], freqs))]
    return out


# ---------------------------------------------------------------- xml writing

def set_num(el, tag, value, fmt="%g"):
    k = el.find(tag)
    if k is None:
        k = ET.SubElement(el, tag)
    k.text = fmt % value


def write_output(oel, gain, delay, mute, revert, hp, bands, spare):
    set_num(oel, "gain", gain, "%.1f")
    set_num(oel, "delay", delay, "%.1f")
    set_num(oel, "mute", mute, "%d")
    set_num(oel, "revert", revert, "%d")
    pbs = oel.findall("passBand")
    # HP then LP, in file order. hiLowType 0 = high-pass, 1 = low-pass.
    hp_el = next((p for p in pbs if int(float(p.findtext("hiLowType", "0"))) == 0), None)
    lp_el = next((p for p in pbs if int(float(p.findtext("hiLowType", "1"))) == 1), None)
    if hp_el is None:
        hp_el = ET.SubElement(oel, "passBand")
    if lp_el is None:
        lp_el = ET.SubElement(oel, "passBand")
    for el, hi_low, fc, enable in ((hp_el, 0, hp, 1 if not spare else 0), (lp_el, 1, 20000, 0)):
        set_num(el, "Fc", fc)
        set_num(el, "hiLowType", hi_low, "%d")
        set_num(el, "Slope", LR24, "%d")
        set_num(el, "filterType", LR_TYPE, "%d")
        set_num(el, "butt_Type", 0, "%d")
        set_num(el, "Enable", enable, "%d")
    eqs = oel.findall("eq")
    for i, e in enumerate(eqs):
        if i < len(bands):
            b = bands[i]
            set_num(e, "Fc", b["Fc"], "%d")
            set_num(e, "Gain", b["Gain"], "%.1f")
            set_num(e, "Q", b["Q"], "%.2f")
        else:
            set_num(e, "Gain", 0, "%.1f")       # slot unused: gain 0 is a pass-through
    return len(bands)


def clone_bank(template_path):
    tree = ET.parse(template_path)
    return tree


def build(template, tune_by_mem, master, out_path, dry_run):
    """tune_by_mem: list of 4 dicts {path: {"gain","delay","hp","bands"}}."""
    tree = clone_bank(template)
    root = tree.getroot()
    set_num(root.find("RunStatus"), "mainVolume", master, "%.1f")
    mems = root.findall("memory")
    if len(mems) != 4:
        raise SystemExit("template has %d memories, expected 4" % len(mems))
    for mi, mel in enumerate(mems):
        plan = tune_by_mem[mi]
        for oel in mel.findall("output"):
            p = int(oel.get("index"))
            spec = plan.get(p)
            if spec is None:
                write_output(oel, SPARE_GAIN, 0.0, 0, 0, 20000, [], spare=True)
            else:
                write_output(oel, spec["gain"], spec["delay"], 0, 0, spec["hp"], spec["bands"],
                             spare=False)
    if dry_run:
        return
    buf = io.StringIO()
    buf.write('<?xml version="1.0" standalone="no"?>\n')
    buf.write(ET.tostring(root, encoding="unicode"))
    with open(out_path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(buf.getvalue())
    print("wrote %s  (%d KB)" % (out_path, os.path.getsize(out_path) // 1024))


def build_single(template, plan, master, out_path, dry_run):
    """ONE tune per file (owner's rule, 2026-10-22): the bank format carried the same
    tune four times over; a file now holds exactly ONE <memory> (index 0) - the whole
    tune, nothing repeated. plan: {path: {"gain","delay","hp","bands"}}, the same
    per-path spec build() takes. Consumers (page, review, verify) read memories[0]."""
    tree = clone_bank(template)
    root = tree.getroot()
    set_num(root.find("RunStatus"), "mainVolume", master, "%.1f")
    set_num(root.find("RunStatus"), "mainMemory", 0, "%d")
    mems = root.findall("memory")
    if not mems:
        raise SystemExit("template has no memories")
    for mel in mems[1:]:
        root.remove(mel)
    mel = root.findall("memory")[0]
    for oel in mel.findall("output"):
        p = int(oel.get("index"))
        spec = plan.get(p)
        if spec is None:
            write_output(oel, SPARE_GAIN, 0.0, 0, 0, 20000, [], spare=True)
        else:
            write_output(oel, spec["gain"], spec["delay"], 0, 0, spec["hp"], spec["bands"],
                         spare=False)
    if dry_run:
        return
    buf = io.StringIO()
    buf.write('<?xml version="1.0" standalone="no"?>\n')
    buf.write(ET.tostring(root, encoding="unicode"))
    with open(out_path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(buf.getvalue())
    print("wrote %s  (%d KB, one tune)" % (out_path, os.path.getsize(out_path) // 1024))


# ---------------------------------------------------------------- the two tunes

def tune1_plan():
    """Last night's bands, straight from the plan the hardware was written from."""
    import json
    with open(os.path.join(HERE, "tune-plan.json"), encoding="utf-8") as fh:
        plan = json.load(fh)
    per_path = {}
    for key, p in plan["paths"].items():
        path = int(key)
        bands = [{"Fc": float(b["fc"]), "Gain": float(b["gain"]), "Q": float(b["q"])}
                 for b in p["bands"]]
        bands.sort(key=lambda b: b["Fc"])
        per_path[path] = {
            "gain": TUNE1_GAIN[path], "delay": TUNE1_DELAY[path],
            "hp": CUR_HP[path], "bands": bands,
        }
    return per_path


def tune2_plan(source_doc, keep_notches=False):
    """bor8f3's curve, refitted with broad filters; its gains and delays kept.

    The target is the source curve smoothed over 0.4 octave: that is the *intent* behind a band
    set whose own filters are 1/15 octave wide. Fitting the raw curve point-by-point is what
    produced the comb in the first place, so it is deliberately not the target.
    """
    mem = source_doc["memories"][0]
    per_path, report = {}, []
    for o in mem["outputs"]:
        p = o["index"]
        if p not in CUR_HP:
            per_path[p] = None
            continue
        raw = idsp.part_curve(o["eq"], GRID)
        target = smooth(raw, GRID, 0.4)
        bands = fit(target, GRID, max_bands=10, qs=(0.5, 0.7, 1.0, 1.4, 2.0, 2.8), floor=1.0)
        if keep_notches:
            bands += notches(raw, sum_bands(bands, GRID), GRID, limit=2, depth=4.0)
        final = sum_bands(bands, GRID)
        devs = [octave_avg(GRID, raw, f) - octave_avg(GRID, final, f) for f in OCT]
        report.append({
            "path": p,
            "before": sum(1 for b in o["eq"] if b["Gain"] != 0),
            "after": len(bands),
            "rms": rms([a - b for a, b in zip(raw, final)]),
            "oct": max(abs(d) for d in devs) if devs else 0.0,
            "bands": bands,
        })
        per_path[p] = {"gain": o["gain"], "delay": o["delay"], "hp": CUR_HP[p], "bands": bands}
    return per_path, report


def octave_avg(freqs, curve, f):
    lo, hi = f / 2 ** 0.5, f * 2 ** 0.5
    v = [c for g, c in zip(freqs, curve) if lo <= g <= hi]
    return sum(v) / len(v) if v else 0.0


OCT = [31.5, 63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000]


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--keep-notches", action="store_true",
                    help="also reproduce bor8f3's deep narrow dips (its 1.5 kHz null etc.)")
    args = ap.parse_args(argv)

    template = os.path.join(ROOT, "web", "bor8f3.idsp")
    src = idsp.load(template)

    t1 = tune1_plan()
    t2, report = tune2_plan(src, keep_notches=args.keep_notches)

    print("tune 1 - measured, 2026-10-20")
    for p in sorted(t1):
        s = t1[p]
        print("  out %d  gain %+.1f  delay %.1f  HP %d  %2d bands" %
              (p + 1, s["gain"], s["delay"], s["hp"], len(s["bands"])))

    print("\ntune 2 - bor8f3 voicing, refitted  (bands %s)" % ("+ preserved notches" if args.keep_notches else "broad only"))
    print("  out  before  after   rms vs source   worst 1/1-oct band")
    for r in report:
        print("   %-3d %6d %6d   %8.2f dB      %5.1f dB" %
              (r["path"] + 1, r["before"], r["after"], r["rms"], r["oct"]))
        print("        " + "  ".join("%d:%+.1f/Q%g" % (round(b["Fc"]), b["Gain"], b["Q"]) for b in r["bands"]))

    if args.dry_run:
        return 0

    # Space-free on purpose. ServeStatic() takes ctx.Request.Url.AbsolutePath (still URL-escaped,
    # and lowercased) and hands it straight to File.Exists, so a filename with a space becomes
    # "2%20bor8f3..." and 404s - the Tune tab then says "Could not load". The existing library
    # (ai2, bor8f3, cb0nyk ...) is space-free for the same reason.
    out1 = os.path.join(ROOT, "web", "1-car-tune-2026-10-20.idsp")
    out2 = os.path.join(ROOT, "web", "2-bor8f3-improved.idsp")
    build_single(template, t1, TUNE1_MASTER, out1, False)
    build_single(template, t2, TUNE1_MASTER, out2, False)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
