#!/usr/bin/env python3
"""set-hp.py - surgical high-pass edit on .idsp files (library crossover maintenance).

The owner's standing crossover map lives in build-swap-tunes.CUR_HP; when it changes,
library files written before the change still carry the old HP and a future load would
write it back to the DSP. This tool brings chosen files' HP element in line, ONE value
at a time, leaving everything else byte-identical (ElementTree round-trip, same writer
as to-single.py).

  python tune/set-hp.py --hz 100 web/1-car-tune-2026-10-20.idsp web/2-bor8f3-improved.idsp

Every file that changes is copied to evidence/hp-<timestamp>/ FIRST (revert path) and a
log json records old/new per file. Files already at the value are left untouched.
"""
import argparse
import datetime
import json
import os
import shutil
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("files", nargs="+")
    ap.add_argument("--hz", type=float, required=True, help="new HP frequency")
    ap.add_argument("--path", type=int, default=6,
                    help="output index to edit (default 6 = out 7, the front 8in)")
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args()

    stamp = datetime.datetime.now().strftime("%Y-%m-%d-%H%M%S")
    bak_dir = os.path.join(ROOT, "evidence", "hp-" + stamp)
    log = []

    for rel in args.files:
        path = os.path.join(ROOT, rel) if not os.path.isabs(rel) else rel
        tree = ET.parse(path)
        root = tree.getroot()
        if root.tag != "DP12_Param":
            raise SystemExit("%s: root <%s> is not DP12_Param" % (rel, root.tag))
        mem = root.find("memory")
        out = None
        for oel in mem.findall("output"):
            if int(oel.get("index", -1)) == args.path:
                out = oel
                break
        if out is None:
            raise SystemExit("%s: no output index %d in first memory" % (rel, args.path))
        hp_el = None
        for pel in out.findall("passBand"):
            try:
                hl = int((pel.findtext("hiLowType") or "0"))
            except ValueError:
                hl = 0
            if hl == 0:
                hp_el = pel
                break
        if hp_el is None:
            raise SystemExit("%s: output %d has no HP passBand element" % (rel, args.path))
        old = float(hp_el.findtext("Fc") or 0)
        if abs(old - args.hz) < 0.5:
            print("  keep  %-40s path %d HP %g (already there)" % (rel, args.path, old))
            continue
        new_el = hp_el.find("Fc")
        if new_el is None:
            raise SystemExit("%s: HP element has no <Fc>" % rel)
        print("  edit  %-40s path %d HP %g -> %g" % (rel, args.path, old, args.hz))
        log.append({"file": rel, "path": args.path, "old_hz": old, "new_hz": args.hz})
        if args.dry_run:
            continue
        os.makedirs(bak_dir, exist_ok=True)
        shutil.copy2(path, os.path.join(bak_dir, os.path.basename(rel)))
        new_el.text = ("%g" % args.hz)
        with open(path, "w", encoding="utf-8", newline="\n") as fh:
            fh.write('<?xml version="1.0" standalone="no"?>\n')
            fh.write(ET.tostring(root, encoding="unicode"))

    if log:
        out = os.path.join(ROOT, "evidence",
                           ("hp-%s-log.json" % stamp) if not args.dry_run
                           else "hp-dryrun-log.json")
        with open(out, "w", encoding="utf-8") as fh:
            json.dump({"hz": args.hz, "path": args.path, "changes": log}, fh, indent=2)
        print("  %s: %d file(s) changed, backup + log under evidence\\%s"
              % ("dry run" if args.dry_run else "done", len(log),
                 os.path.basename(out)[:-5]))
    else:
        print("  nothing to do")


if __name__ == "__main__":
    main()
