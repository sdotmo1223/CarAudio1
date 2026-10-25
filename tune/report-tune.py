#!/usr/bin/env python3
"""Structural + tonal report on an .idsp tune: the numbers behind the prose.

For each populated output: filter count, Q, how often the gains sit on a clamp value, the
realized peak-to-peak ripple (a comb shows up here and nowhere else), and the band averages a
listener hears (low mid / presence / treble).

    python tune/report-tune.py web/bor8f3.idsp [--mem 0]
"""
import argparse
import importlib.util
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("idsp", os.path.join(HERE, "idsp.py"))
idsp = importlib.util.module_from_spec(spec)
spec.loader.exec_module(idsp)

BANDS = (("sub 30-60", 30, 60), ("bass 60-120", 60, 120), ("low mid 120-300", 120, 300),
         ("mid 300-1k", 300, 1000), ("presence 1-3k", 1000, 3000), ("treble 3-6k", 3000, 6000),
         ("top 6-12k", 6000, 12000))


def avg(f, curve, lo, hi):
    v = [c for g, c in zip(f, curve) if lo <= g <= hi]
    return sum(v) / len(v) if v else 0.0


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument("file")
    ap.add_argument("--mem", type=int)
    args = ap.parse_args(argv)

    doc = idsp.load(args.file)
    mi = args.mem if args.mem is not None else doc["run"]["memory"]
    mem = doc["memories"][mi]
    dense = [20.0 * (2.0 ** (k / 48.0)) for k in range(481)]

    print("%s  memory %d" % (os.path.basename(args.file), mi))
    print("  %d memories in file; %d outputs" % (len(doc["memories"]), len(mem["outputs"])))
    print()
    print("  out  bands  Q      on+3  on<=-6   ripple 200-8k   worst dev   300Hz  1.5kHz")
    for o in mem["outputs"]:
        bands = idsp.active_bands(o)
        if not bands:
            continue
        curve = idsp.part_curve(o["eq"], dense)
        win = [v for f, v in zip(dense, curve) if 200 <= f <= 8000]
        clamp = sum(1 for b in bands if abs(b["Gain"] - 3.0) < 0.001)
        deep = sum(1 for b in bands if b["Gain"] <= -5.999)
        print("  %-4d %5d  %-6s %4d  %6d   %6.1f dB      %5.1f dB   %+5.1f   %+5.1f" % (
            o["index"] + 1, len(bands), "%.1f" % idsp.median([b["Q"] for b in bands]),
            clamp, deep, max(win) - min(win), max(abs(v) for v in win),
            idsp.at(dense, curve, 300), idsp.at(dense, curve, 1500)))

    print("\n  band averages of the summed EQ (dB) - what is actually being shaped:")
    print("  out  " + "".join("%-15s" % b[0] for b in BANDS))
    for o in mem["outputs"]:
        if not idsp.active_bands(o):
            continue
        curve = idsp.part_curve(o["eq"], dense)
        print("  %-4d " % (o["index"] + 1) +
              "".join("%-15.1f" % avg(dense, curve, lo, hi) for _, lo, hi in BANDS))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
