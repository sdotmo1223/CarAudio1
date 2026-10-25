r"""to-rew.py - make the raw measurements openable in REW (Room EQ Wizard).

One .txt per channel, written next to the run in <run>\rew\:
    * comment header (channel, run, capture time, provenance)
    <freq> <dbfs>        <- space-delimited, ascending, number-led

REW's rules (roomeqwizard.com/help - Importing Measurement Data):
  * File -> Import frequency response (Ctrl+I), extension .txt/.frd/.dat
  * freq + magnitude per line (phase optional), arbitrary spacing, >= 5 points
  * ONLY lines beginning with a number are imported; everything else is ignored
    - so the '*' header is free text and lands in the trace as... it doesn't:
      REW names the trace after the file name, and the header survives as provenance
      in the file itself.

Reads the run's channel JSONs (works for the legacy 1/6 = 61-point runs and the
1/24 = 241-point runs from 2026-10-22 on, and for both folder layouts:
"tweeter-1.json" and "channel 1.json"). Idempotent - rerun any time.

Usage:
    python tune\to-rew.py                      # every folder under measurements\
    python tune\to-rew.py "measurements\run-20261020-1925"
"""
import glob
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)


def slug(name):
    s = re.sub(r"[^a-z0-9]+", "-", name.lower()).strip("-")
    return s or "channel"


def convert(folder):
    files = sorted(glob.glob(os.path.join(folder, "*.json")))
    runs = [f for f in files if not os.path.basename(f).lower().startswith("readme")]
    if not runs:
        return 0, "no channel jsons"
    outdir = os.path.join(folder, "rew")
    os.makedirs(outdir, exist_ok=True)
    n = 0
    for fp in runs:
        try:
            rec = json.load(open(fp, encoding="utf-8-sig"))
        except Exception:
            continue
        ch = rec.get("channel")
        pts = rec.get("points")
        if not ch or not pts:
            continue
        run = rec.get("run", os.path.basename(folder))
        when = rec.get("capturedAt", "")
        lines = [
            "* %s  -  ds18-bridge measurement %s" % (ch, run),
            "* captured %s, absolute dBFS at the mic, EQ flat, not normalised" % when,
            "* import in REW: File > Import frequency response (Ctrl+I)",
            "* grid: %s" % rec.get("grid", "unknown"),
        ]
        for f, d in pts:
            lines.append("%.2f %.2f" % (float(f), float(d)))
        out = os.path.join(outdir, slug(ch) + ".txt")
        with open(out, "w", encoding="ascii", newline="\n") as fh:
            fh.write("\n".join(lines) + "\n")
        n += 1
    return n, "rew\\"


def main(argv):
    folders = [a for a in argv if not a.startswith("--")]
    if not folders:
        folders = sorted(d for d in glob.glob(os.path.join(ROOT, "measurements", "*"))
                         if os.path.isdir(d))
    total = 0
    for folder in folders:
        n, where = convert(folder)
        print("  %-42s -> %d REW file(s) in %s"
              % (os.path.basename(folder), n, where))
        total += n
    print("%d REW file(s) total" % total)


if __name__ == "__main__":
    main(sys.argv[1:])
