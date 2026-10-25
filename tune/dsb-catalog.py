#!/usr/bin/env python3
"""
dsb-catalog.py - one card per tune, generated from artefacts that already exist.

Why this exists (2026-10-25): a tune's identity used to be smeared across tune-plan*.json,
_build-*.log, prediction.json, ATLAS.md, PREDICTION.md, SELF-REVIEW.md, B-TUNE-REVIEW.md and
prose in STATE.md. Nobody could answer "what tunes exist and what is true of each" without
reading all of them, and the answer went stale in each copy separately.

This reads the artefacts and writes DSB\\CATALOG.md + DSB\\catalog.json. It never invents a
number: every field is either read from a file or reported as NOT MEASURED.

    python tune\\dsb-catalog.py

Output is generated - do not edit CATALOG.md by hand.
"""

import json
import os
import re
import xml.etree.ElementTree as ET
from datetime import datetime, timezone

ROOT = r"C:\Users\sdotm\OneDrive\Documents\Default Project\ds18-bridge"
DSB = os.path.join(ROOT, "DSB")

NOT_MEASURED = "**NOT MEASURED**"


def load_json(path):
    try:
        with open(path, "r", encoding="utf-8-sig") as fh:
            return json.load(fh)
    except Exception:
        return None


def mtime(path):
    try:
        return datetime.fromtimestamp(os.path.getmtime(path)).strftime("%Y-%m-%d %H:%M")
    except OSError:
        return "unknown"


def parse_idsp(path):
    """The .idsp is XML. Read the fields a tuner cares about; never guess the rest."""
    info = {"ok": False, "memories": 0, "paths": 0, "error": None}
    try:
        tree = ET.parse(path)
        root = tree.getroot()
        mems = root.findall(".//memory") or root.findall(".//Memory")
        info["memories"] = len(mems)
        # count output paths across the first memory that has any
        paths = root.findall(".//passBand")
        info["paths"] = len(paths)
        info["ok"] = True
    except Exception as exc:
        info["error"] = str(exc)
    return info


def find_idsp():
    r"""Only banks\ and web\ are THIS project's library.

    evidence\caraudio1\upstream\ holds twenty .idsp files cloned from the owner's GitHub
    repo - a different project's tunes. Counting them here made the library look like 32
    files when ATLAS.md says 12, and a catalog that inflates the library is worse than no
    catalog. They are reported separately, clearly labelled.
    """
    out = []
    for sub in ("banks", "web"):
        d = os.path.join(ROOT, sub)
        if not os.path.isdir(d):
            continue
        for fn in sorted(os.listdir(d)):
            if fn.lower().endswith(".idsp"):
                out.append(os.path.join(d, fn))
    return out


def find_reference_idsp():
    """Tunes from other projects: context, never part of the library count."""
    out = []
    for sub in (os.path.join("evidence", "caraudio1", "upstream"),
                os.path.join("evidence", "caraudio1", "repo")):
        d = os.path.join(ROOT, sub)
        if not os.path.isdir(d):
            continue
        for fn in sorted(os.listdir(d)):
            if fn.lower().endswith(".idsp"):
                out.append(os.path.join(d, fn))
    return out


def find_plans():
    d = os.path.join(ROOT, "tune")
    return sorted(os.path.join(d, f) for f in os.listdir(d)
                  if f.startswith("tune-plan") and f.endswith(".json")) if os.path.isdir(d) else []


def residuals():
    """Predicted residuals from prediction.json, if present. Never a measurement."""
    p = load_json(os.path.join(ROOT, "tune", "prediction.json"))
    rows = []
    if isinstance(p, dict) and isinstance(p.get("predictions"), dict):
        for name, val in p["predictions"].items():
            if isinstance(val, dict):
                for k, v in val.items():
                    if isinstance(v, (int, float)):
                        rows.append((name, k, v))
            elif isinstance(val, (int, float)):
                rows.append((name, "residual", val))
    return rows


def main():
    playing = load_json(os.path.join(ROOT, "tunename.json")) or {}
    prediction = load_json(os.path.join(ROOT, "tune", "prediction.json")) or {}
    cards = []

    for path in find_idsp():
        rel = os.path.relpath(path, ROOT)
        meta = parse_idsp(path)
        cards.append({
            "file": rel,
            "modified": mtime(path),
            "bytes": os.path.getsize(path),
            "memories": meta["memories"],
            "parse": "ok" if meta["ok"] else ("failed: " + str(meta["error"])),
            "playing": playing.get("name", "").split("-")[0] in os.path.basename(path)
                       if playing.get("name") else False,
            "source": "this project",
            "post_tune_measured_residual": NOT_MEASURED,
        })

    reference = []
    for path in find_reference_idsp():
        reference.append({
            "file": os.path.relpath(path, ROOT),
            "modified": mtime(path),
            "bytes": os.path.getsize(path),
            "source": "github.com/sdotmo1223/CarAudio1 - another project, context only",
        })

    plans = []
    for path in find_plans():
        d = load_json(path) or {}
        plans.append({
            "file": os.path.relpath(path, ROOT),
            "modified": mtime(path),
            "paths": len(d.get("paths") or []),
            "target": d.get("target"),
            "source": d.get("source"),
            "post_tune_measured_residual": NOT_MEASURED,
        })

    doc = {
        "generated": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "playing_memory": playing.get("name"),
        "tunes": cards,
        "reference_tunes": reference,
        "plans": plans,
        "predicted_residuals": [
            {"tune": a, "field": b, "value": c, "kind": "PREDICTION, not a measurement"}
            for a, b, c in residuals()
        ],
        "measured_residual_count": 0,
        "note": "Every residual in this file is predicted from a pre-tune capture. "
                "No post-tune capture has ever been taken - OPEN-QUESTIONS.md Q1.",
    }

    with open(os.path.join(DSB, "catalog.json"), "w", encoding="utf-8") as fh:
        json.dump(doc, fh, indent=2)

    L = []
    L.append("# CATALOG - one card per tune (generated)")
    L.append("")
    L.append("Generated by `python tune\\dsb-catalog.py`. Do not edit by hand.")
    L.append("")
    L.append("## Playing now")
    L.append("")
    L.append("- **DSP memory carries:** `%s`" % (playing.get("name") or "unknown"))
    L.append("")
    L.append("## Tunes (%d)" % len(cards))
    L.append("")
    L.append("| file | modified | size | memories | parse | origin | post-tune measured residual |")
    L.append("|---|---|---|---|---|---|---|")
    for c in cards:
        L.append("| `%s` | %s | %s | %s | %s | %s | %s |" % (
            os.path.basename(c["file"]), c["modified"], c["bytes"],
            c["memories"], c["parse"], c["source"], c["post_tune_measured_residual"]))
    L.append("")
    L.append("## Plans (%d)" % len(plans))
    L.append("")
    L.append("| plan | modified | paths | target | post-tune measured residual |")
    L.append("|---|---|---|---|---|")
    for p in plans:
        tgt = p["target"]
        tgt = tgt if isinstance(tgt, str) else json.dumps(tgt)[:48]
        L.append("| `%s` | %s | %d | %s | %s |" % (
            os.path.basename(p["file"]), p["modified"], p["paths"],
            tgt or "-", p["post_tune_measured_residual"]))
    L.append("")
    L.append("## Reference tunes from other projects (%d, context only)" % len(reference))
    L.append("")
    if reference:
        L.append("| file | modified | origin |")
        L.append("|---|---|---|")
        for c in reference:
            L.append("| `%s` | %s | %s |" % (
                os.path.basename(c["file"]), c["modified"], c["source"]))
    else:
        L.append("_None present._")
    L.append("")
    L.append("## Predicted residuals")
    L.append("")
    if doc["predicted_residuals"]:
        L.append("| tune | field | value | kind |")
        L.append("|---|---|---|---|")
        for r in doc["predicted_residuals"]:
            L.append("| %s | %s | %.2f | %s |" % (r["tune"], r["field"], r["value"], r["kind"]))
    else:
        L.append("_No `tune\\prediction.json` readable._")
    L.append("")
    L.append("---")
    L.append("")
    L.append("**The `post-tune measured residual` column is empty on every row, by design.**")
    L.append("It is a required field with no data, because no tune has ever been measured after")
    L.append("it was written. See `OPEN-QUESTIONS.md` **Q1** - the highest-ranked unknown here.")
    L.append("A blank required field is more annoying than an unasked question; that is the point.")

    with open(os.path.join(DSB, "CATALOG.md"), "w", encoding="utf-8", newline="") as fh:
        fh.write("\r\n".join(L) + "\r\n")

    print("wrote DSB\\CATALOG.md  (%d tunes, %d plans)" % (len(cards), len(plans)))
    print("wrote DSB\\catalog.json")


if __name__ == "__main__":
    main()
