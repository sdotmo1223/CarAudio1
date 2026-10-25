#!/usr/bin/env python3
"""Atlas of every .idsp in web/ - what the eight tunes actually contain, not what they claim.

Six of the eight files in `web/` were never opened: `ai2`, `bor8f3` (only memory 0 was
analysed), `cb0nyk`, `tinker3`, `w6784b`, `xswmps`.  Each carries four memory blocks, so
the library is 32 candidate voicings of 8 paths x 31 bands, and the question this answers
is structural: which frequencies the voicings agree on, which filter shapes they use,
which crossovers they assume, and which of them are the same tune renamed.

Everything here is read-only and needs no hardware.

    python tune/tune-atlas.py                    # summary to stdout
    python tune/tune-atlas.py --md DSB/ATLAS.md # + the report
"""
import argparse
import hashlib
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
ROLES = ["tw1", "tw2", "tw3", "tw4", "3.5f", "3.5r", "8f", "6.5r"]
REGIONS = (("sub 30-60", 30, 60), ("bass 60-120", 60, 120), ("lowmid 120-300", 120, 300),
           ("mid 300-1k", 300, 1000), ("pres 1-3k", 1000, 3000),
           ("treb 3-6k", 3000, 6000), ("top 6-12k", 6000, 12000))
Q_EDGES = [0, 0.5, 0.7, 1.0, 1.4, 2.0, 2.8, 4.0, 6.0, 10.0, 16.0, 99]
G_EDGES = [-99, -12, -9, -6, -3, -1, 0, 1, 3, 6, 9, 12, 99]


def region_avgs(freq, curve):
    out = {}
    for name, lo, hi in REGIONS:
        v = [c for f, c in zip(freq, curve) if lo <= f <= hi]
        out[name] = sum(v) / len(v) if v else 0.0
    return out


def xo_string(mem):
    bits = []
    for o in mem["outputs"][:8]:
        hp = next((p for p in o["passBand"] if p["hiLowType"] == 0), None)
        lp = next((p for p in o["passBand"] if p["hiLowType"] == 1), None)
        h = "%g" % hp["Fc"] if hp and hp["Enable"] and hp["Fc"] < 19999 else "-"
        l = "%g" % lp["Fc"] if lp and lp["Enable"] and lp["Fc"] < 19999 else "-"
        bits.append("%s/%s" % (h, l))
    return " ".join(bits)


def signature(mem):
    blob = json.dumps([[o["gain"], o["delay"], o["mute"], o["eq"],
                        [[p["Fc"], p["Enable"], p["Slope"], p["filterType"]] for p in o["passBand"]]]
                       for o in mem["outputs"][:8]], sort_keys=True)
    return hashlib.sha1(blob.encode()).hexdigest()[:10]


def analyse(path):
    doc = idsp.load(path)
    dense = [20.0 * (2.0 ** (k / 48.0)) for k in range(481)]
    out = []
    for mi, mem in enumerate(doc["memories"]):
        bands, qs, gains, fcs = [], [], [], []
        for o in mem["outputs"][:8]:
            for b in idsp.active_bands(o):
                bands.append((o["index"], b))
                qs.append(b["Q"])
                gains.append(b["Gain"])
                fcs.append(b["Fc"])
        curves = [idsp.part_curve(o["eq"], dense) for o in mem["outputs"][:8]]
        summed = [sum(c[i] for c in curves) for i in range(len(dense))]
        reg = region_avgs(dense, summed)
        # level-aligned 8-path sum, used for similarity (shape only)
        mean = sum(summed) / len(summed)
        shape = [round(v - mean, 1) for v in summed]
        out.append({
            "file": os.path.basename(path), "memory": mi, "sig": signature(mem),
            "bands": len(bands), "per_path": [len(idsp.active_bands(o)) for o in mem["outputs"][:8]],
            "q_median": idsp.median(qs), "q_max": max(qs) if qs else 0.0,
            "q_notch": sum(1 for q in qs if q >= 6.0),
            "q_high": sum(1 for q in qs if q >= 10.0),
            "g_max": max(gains) if gains else 0.0, "g_min": min(gains) if gains else 0.0,
            "g_deep": sum(1 for g in gains if g <= -6.0),
            "g_clamp3": sum(1 for g in gains if abs(g - 3.0) < 0.01),
            "fcs": fcs, "gains": gains, "qs": qs,
            "gains_out": [o["gain"] for o in mem["outputs"][:8]],
            "delays_out": [o["delay"] for o in mem["outputs"][:8]],
            "xo": xo_string(mem),
            "mutes": sum(1 for o in mem["outputs"] if o["mute"]),
            "regions": reg,
            "tilt": reg["bass 60-120"] - reg["treb 3-6k"],
            "shape": shape, "levels": [o["gain"] for o in mem["outputs"][:8]],
        })
    return out


def bin_label(edges, v):
    for i in range(len(edges) - 1):
        if edges[i] <= v < edges[i + 1]:
            return "[%g,%g)" % (edges[i], edges[i + 1])
    return ">=%g" % edges[-2]


def hist(values, edges):
    h = {}
    for v in values:
        h[bin_label(edges, v)] = h.get(bin_label(edges, v), 0) + 1
    return h


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument("--md", default=None)
    ap.add_argument("--json", default=None)
    args = ap.parse_args(argv)

    files = sorted(f for f in os.listdir(os.path.join(ROOT, "web")) if f.lower().endswith(".idsp"))
    rows = []
    for f in files:
        rows.extend(analyse(os.path.join(ROOT, "web", f)))

    L = []
    def out(s=""):
        print(s)
        L.append(s)

    out("=== atlas: %d files, %d memory blocks ===" % (len(files), len(rows)))
    out("")
    out("%-30s %3s %5s %5s %5s %6s %6s %6s %6s  %s" %
        ("file", "mem", "bands", "Qmed", "Qmax", "notch", "max+", "max-", "tilt", "gains"))
    for r in rows:
        out("%-30s %3d %5d %5.1f %5.1f %6d %6.1f %6.1f %6.1f  %s" % (
            r["file"], r["memory"] + 1, r["bands"], r["q_median"], r["q_max"], r["q_notch"],
            r["g_max"], r["g_min"], r["tilt"],
            " ".join("%+.0f" % g for g in r["gains_out"])))
    out("  tilt = mean EQ 60-120 Hz minus mean EQ 3-6 kHz on the summed 8-path curve;")
    out("         positive = bass-forward filter work, negative = presence-forward.")
    out("")

    # memory distinctness per file
    out("--- are the four memories in each file actually different?")
    for f in files:
        sigs = [r["sig"] for r in rows if r["file"] == f]
        out("  %-30s %d distinct of %d   %s" % (f, len(set(sigs)), len(sigs), " ".join(sigs)))
    out("")
    seen = {}
    for r in rows:
        seen.setdefault(r["sig"], []).append("%s m%d" % (r["file"], r["memory"] + 1))
    out("--- the whole library by payload: %d memory blocks, %d distinct tunes" % (len(rows), len(seen)))
    for sig, where in sorted(seen.items(), key=lambda kv: -len(kv[1])):
        out("  %s  x%-2d  %s" % (sig, len(where), ", ".join(where[:6]) + (" ..." if len(where) > 6 else "")))
    out("")

    # crossover maps assumed
    out("--- crossover maps the tunes assume (HP/LP per path, '-' = open)")
    xo = {}
    for r in rows:
        xo.setdefault(r["xo"], []).append("%s m%d" % (r["file"].split("-")[0][:12], r["memory"] + 1))
    for k, v in sorted(xo.items(), key=lambda kv: -len(kv[1])):
        out("  %-60s %2d  %s" % (k, len(v), ", ".join(v[:4]) + (" ..." if len(v) > 4 else "")))
    out("")

    # what the filters are actually doing
    out("--- where the 32 voicings put their filters (1/6-oct bins, all active bands)")
    allf = [f for r in rows for f in r["fcs"]]
    bins = {}
    for f in allf:
        k = round(6 * math.log2(f / 20.0))
        bins[k] = bins.get(k, 0) + 1
    top = sorted(bins.items(), key=lambda kv: -kv[1])[:18]
    for k, n in top:
        out("  %-7s %-2d %s" % ("%g" % (20.0 * 2 ** (k / 6.0)), n, "#" * min(n, 60)))
    out("  total active bands in the library: %d" % len(allf))
    out("")

    out("--- filter shapes (Q) and sizes (dB), all active bands")
    qh = hist([q for r in rows for q in r["qs"]], Q_EDGES)
    gh = hist([g for r in rows for g in r["gains"]], G_EDGES)
    out("  Q      " + "  ".join("%s:%d" % (k, v) for k, v in qh.items()))
    out("  gain   " + "  ".join("%s:%d" % (k, v) for k, v in gh.items()))
    out("  Q>=6 (a notch rather than a tone control): %d of %d bands = %.0f%%"
        % (sum(r["q_notch"] for r in rows), len(allf), 100.0 * sum(r["q_notch"] for r in rows) / max(1, len(allf))))
    out("")

    # which parts of the spectrum each voicing works on
    out("--- tonal work per voicing (mean dB of the summed EQ in each region)")
    out("  %-28s %-3s %s" % ("file", "m", "".join("%-9s" % n.split()[0] for n, _, _ in REGIONS)))
    for r in rows:
        out("  %-28s %-3d %s" % (r["file"][:28], r["memory"] + 1,
                                 "".join("%-9.1f" % r["regions"][n] for n, _, _ in REGIONS)))
    out("")

    # similarity: nearest neighbour per memory (shape only, level removed)
    out("--- nearest neighbour by shape (rms dB over 1/6-oct, level-aligned)")
    for i, a in enumerate(rows):
        best = None
        for j, b in enumerate(rows):
            if i == j:
                continue
            d = math.sqrt(sum((x - y) ** 2 for x, y in zip(a["shape"], b["shape"])) / len(a["shape"]))
            if best is None or d < best[0]:
                best = (d, b)
        out("  %-30s m%d -> %-28s m%d   rms %.1f dB" % (
            a["file"], a["memory"] + 1, best[1]["file"], best[1]["memory"] + 1, best[0]))
    out("")

    # the outlier test: which voicings are far from everything else
    out("--- outliers: mean rms distance to the other 31 voicings")
    scored = []
    for i, a in enumerate(rows):
        ds = []
        for j, b in enumerate(rows):
            if i == j:
                continue
            ds.append(math.sqrt(sum((x - y) ** 2 for x, y in zip(a["shape"], b["shape"])) / len(a["shape"])))
        scored.append((sum(ds) / len(ds), a))
    for d, a in sorted(scored, key=lambda t: -t[0])[:8]:
        out("  %-30s m%d   %.1f dB" % (a["file"], a["memory"] + 1, d))

    if args.json:
        with open(args.json, "w", encoding="utf-8") as fh:
            json.dump([{k: v for k, v in r.items() if k not in ("fcs", "gains", "qs", "shape")}
                       for r in rows], fh, indent=1)
        print("\nwrote %s" % args.json)
    if args.md:
        with open(args.md, "w", encoding="utf-8") as fh:
            fh.write("```\n" + "\n".join(L) + "\n```\n")
        print("wrote %s" % args.md)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
