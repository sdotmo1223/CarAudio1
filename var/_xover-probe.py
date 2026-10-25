#!/usr/bin/env python3
"""
_xover-probe.py - apply RAWCAT's crossover-selection doctrine to the CANONICAL run.

Doctrine (DSB/refs/rawcat/crossovers-and-targets.txt, digested in TUNE-PLAYBOOK 9.0.3):
  - choose the crossover where BOTH adjacent drivers have clean, wide response,
  - give a FULL OCTAVE of overlap so the slopes have room,
  - never put a crossover in a nasty area (peak, dip, breakup).

V2, and v1 was WRONG. v1 tested FLATNESS - deviation from the driver's own home-band
median. Inside a car cabin that measures the ROOM, not the driver: modes below 300 Hz
and cabin absorption above ~4 kHz throw every driver out of a +/-6 dB window no matter
how usable it is. It reported "no clean overlap" for every adjacent pair, which is an
artefact, not a fact.

Clean here means SMOOTH, which is what Rawcat was actually reading off the graph: no
narrow peaks, no deep nulls, no breakup raggedness. A response may be tilted and still
be a perfectly good crossover region - the tilt is what the EQ is for.

Roughness metric: peak-to-peak of the 1/6-oct smoothed curve inside a 1/3-oct window.
Breakup onset = the first frequency above the home band where roughness stays high.

Pure file arithmetic. No device, nothing written.
"""
import glob
import importlib.util
import json
import math
import os

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)

spec = importlib.util.spec_from_file_location("ft", os.path.join(ROOT, "tune", "fit-tune.py"))
ft = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ft)

SMOOTH = 3.0      # dB of 1/3-oct peak-to-peak that still counts as "smooth"
MEAS = os.path.join(ROOT, "measurements", "run-20261020-1925")

CH = {
    "tweeter 1":  (0, (3000, 6000), "FL tweeter"),
    "tweeter 2":  (1, (3000, 6000), "FR tweeter"),
    "tweeter 3":  (2, (3000, 6000), "RL tweeter"),
    "tweeter 4":  (3, (3000, 6000), "RR tweeter"),
    "midrange 1": (4, (300, 800),  "3.5\" front pair"),
    "midrange 2": (5, (300, 800),  "3.5\" rear pair"),
    "midrange 3": (6, (80, 160),   "8\" front pair"),
    "midbass 1":  (7, (100, 200),  "6.5\" rear pair"),
}


def roughness(grid, sm):
    """peak-to-peak of sm inside a 1/3-oct window centred on each point."""
    out = []
    for i, f in enumerate(grid):
        lo, hi = f / 2 ** (1 / 6.0), f * 2 ** (1 / 6.0)
        w = [sm[j] for j, g in enumerate(grid) if lo <= g <= hi]
        out.append((max(w) - min(w)) if w else 0.0)
    return out


def smooth_region(grid, rough, home):
    """Contiguous run around the home band where roughness stays <= SMOOTH."""
    hlo, hhi = home
    mid = next(i for i, f in enumerate(grid) if hlo <= f <= hhi)

    def ok(i):
        return rough[i] <= SMOOTH

    lo = mid
    while lo > 0 and ok(lo - 1):
        lo -= 1
    hi = mid
    while hi < len(grid) - 1 and ok(hi + 1):
        hi += 1
    return grid[lo], grid[hi]


def main():
    recs = {}
    for fp in sorted(glob.glob(os.path.join(MEAS, "*.json"))):
        r = json.load(open(fp, encoding="utf-8-sig"))
        recs[r["channel"].strip().lower()] = r

    print("SMOOTH band per driver (1/3-oct peak-to-peak <= %.1f dB), not flatness." % SMOOTH)
    print("A tilted but smooth response is a GOOD crossover region; the tilt is EQ's job.\n")
    print("%-18s %-4s %9s %9s   %s" % ("driver", "path", "smooth lo", "smooth hi", "breakup onset"))

    out = {}
    for name, (path, home, label) in CH.items():
        r = recs[name]
        pts = [(float(a), float(b)) for a, b in r["points"]]
        sm = ft.smooth(pts, 1 / 6.0)
        grid = [f for f, _ in pts]
        rough = roughness(grid, sm)
        lo, hi = smooth_region(grid, rough, home)
        # breakup onset: first f above home where roughness exceeds SMOOTH for 2+ points
        hhi = home[1]
        onset = None
        for i, f in enumerate(grid):
            if f > hhi and rough[i] > SMOOTH and rough[min(i + 1, len(grid) - 1)] > SMOOTH:
                onset = f
                break
        out[path] = (lo, hi, onset, grid, sm)
        print("%-18s %-4d %9.0f %9.0f   %s" % (
            label, path, lo, hi, ("%.0f Hz" % onset) if onset else "none to 20k"))

    print("\nshape check - smoothed level at ISO points, dB at the mic (raw, no EQ):")
    iso = [50, 80, 125, 200, 315, 500, 800, 1250, 2000, 3150, 5000, 8000, 12500, 20000]
    print("%-18s %s" % ("driver", "".join("%7d" % f for f in iso)))
    for name, (path, home, label) in CH.items():
        lo_, hi_, _, grid, sm = out[path]
        row = []
        for f in iso:
            v = min(grid, key=lambda g: abs(g - f))
            row.append(sm[grid.index(v)])
        print("%-18s %s" % (label, "".join("%7.1f" % v for v in row)))

    print("\nadjacent pairs - is there a smooth OCTAVE of overlap?")
    pairs = [(6, 4, "8\" front  -> 3.5\" front"),
             (7, 5, "6.5\" rear -> 3.5\" rear"),
             (4, 0, "3.5\" front -> FL tweeter"),
             (5, 2, "3.5\" rear  -> RL tweeter")]
    for a, b, nm in pairs:
        la, ha, ona, _, _ = out[a]
        lb, hb, onb, _, _ = out[b]
        olo, ohi = max(la, lb), min(ha, hb)
        if olo <= 0 or ohi <= olo:
            print("  %-26s NO smooth overlap" % nm)
            continue
        ratio = ohi / olo
        # the crossover must sit below the LOWER driver's breakup onset
        lim = min([x for x in (ona, onb) if x], default=20000.0)
        cand = [f for f in iso if olo * 1.15 <= f <= ohi * 0.87 and f <= lim]
        print("  %-26s overlap %6.0f-%-6.0f Hz  ratio %5.2f  %s" % (
            nm, olo, ohi, ratio, "FULL OCTAVE" if ratio >= 2 else "less than an octave"))
        print("      breakup onset: p%d %.0f Hz, p%d %.0f Hz  -> crossover must stay below %.0f Hz" % (
            a + 1, ona if ona else 99999, b + 1, onb if onb else 99999, lim))
        print("      candidate points inside the overlap and below breakup: %s" % (
            ", ".join("%d" % c for c in cand) if cand else "NONE"))


if __name__ == "__main__":
    main()
