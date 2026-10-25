"""to-single.py - collapse 4-memory .idsp library files to ONE tune per file.

Owner's rule (2026-10-22): a file carries one singular tune, not the same bank four
times over. The keepers, in order of preference:
  * if all four blocks are identical, keep block 0 (they ARE one tune already)
  * if they differ, keep the block RunStatus.mainMemory says was live when saved
Every dropped block's signature is logged to evidence\\single-memory-collapse-*.json
before it is removed - nothing disappears silently, and the original is backed up next
to the log as <name>.idsp.4mem.

Usage:  python tune\\to-single.py [--dry-run] [file.idsp ...]   (default: web\\*.idsp)
"""
import glob
import hashlib
import json
import os
import shutil
import sys
import time
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)


def sig(el):
    """Content signature of a memory block. The block's position (index attribute) is
    NOT content: banks written as one tune x4 hash as four distinct blocks without this
    normalization, and would be wrongly reported as different tunes."""
    clone = ET.fromstring(ET.tostring(el, encoding="unicode"))
    clone.set("index", "-")
    return hashlib.md5(ET.tostring(clone, encoding="utf-8")).hexdigest()[:12]


def convert(path, dry_run):
    tree = ET.parse(path)
    root = tree.getroot()
    mems = root.findall("memory")
    if len(mems) <= 1:
        return None                      # already single
    sigs = [sig(m) for m in mems]
    distinct = len(set(sigs))

    keep = 0
    if distinct > 1:
        run = root.find("RunStatus")
        mm = run.find("mainMemory") if run is not None else None
        if mm is not None and mm.text:
            try:
                keep = int(mm.text)
            except ValueError:
                keep = 0
        keep = max(0, min(keep, len(mems) - 1))

    entry = {
        "file": os.path.basename(path),
        "kept_block": keep,
        "blocks": sigs,
        "distinct_before": distinct,
        "dropped": [i for i in range(len(mems)) if i != keep],
    }
    if dry_run:
        return entry

    # backup the original beside the collapse log before touching it
    stamp = time.strftime("%Y%m%d-%H%M")
    log_dir = os.path.join(ROOT, "evidence")
    os.makedirs(log_dir, exist_ok=True)
    shutil.copy2(path, os.path.join(log_dir, os.path.basename(path) + ".4mem"))

    for i, m in enumerate(list(mems)):
        if i != keep:
            root.remove(m)
    run = root.find("RunStatus")
    if run is not None:
        mm = run.find("mainMemory")
        if mm is not None:
            mm.text = "0"
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write('<?xml version="1.0" standalone="no"?>\n')
        fh.write(ET.tostring(root, encoding="unicode"))
    entry["size_kb"] = os.path.getsize(path) // 1024
    return entry


def main(argv):
    dry = "--dry-run" in argv
    files = [a for a in argv if not a.startswith("--")]
    if not files:
        files = sorted(glob.glob(os.path.join(ROOT, "web", "*.idsp")))
    results = []
    for f in files:
        e = convert(f, dry)
        if e:
            results.append(e)
            tag = "DRY " if dry else "done"
            print("  %s %-36s kept block %d, dropped %d, %d distinct before"
                  % (tag, e["file"], e["kept_block"] + 1, len(e["dropped"]),
                     e["distinct_before"]))
        else:
            print("  skip %-36s (already one tune)" % os.path.basename(f))
    if results and not dry:
        log = os.path.join(ROOT, "evidence",
                           "single-memory-collapse-%s.json" % time.strftime("%Y%m%d-%H%M"))
        with open(log, "w", encoding="utf-8") as fh:
            json.dump(results, fh, indent=2)
        print("log: %s (+ *.idsp.4mem backups)" % os.path.relpath(log, ROOT))
    print("%d file(s) collapsed" % len(results))


if __name__ == "__main__":
    main(sys.argv[1:])
