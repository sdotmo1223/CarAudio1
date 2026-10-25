#!/usr/bin/env python3
"""Read the live DSP and compare it to a tune file - the transcription check, end to end.

tune 1 was built from tune/tune-plan.json because the DSP was unplugged that morning. This is
the other half of that claim: read memory N back off the amplifier and assert the file matches
the hardware, band for band, plus gains, delays and crossovers.

Read-only. It never writes to the DSP.

    python tune/verify-device.py "web/1-car-tune-2026-10-20.idsp" --mem 1
"""
import argparse
import importlib.util
import json
import os
import sys
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
spec = importlib.util.spec_from_file_location("idsp", os.path.join(HERE, "idsp.py"))
idsp = importlib.util.module_from_spec(spec)
spec.loader.exec_module(idsp)

BRIDGE = "http://127.0.0.1:8585"


def api(path, body=None, timeout=25):
    data = None if body is None else json.dumps(body).encode("utf-8")
    req = urllib.request.Request(BRIDGE + path, data=data,
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.loads(r.read().decode("utf-8"))


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument("file")
    ap.add_argument("--mem", type=int, required=True, help="device memory index (0-based)")
    ap.add_argument("--tol-gain", type=float, default=0.15)
    args = ap.parse_args(argv)

    doc = idsp.load(args.file)
    file_mem = doc["memories"][args.mem if args.mem < len(doc["memories"]) else 0]
    if len(doc["memories"]) == 1:
        print("note: single-tune file - its one block is checked against device memory %d"
              % args.mem)
    fails = []

    def check(cond, msg):
        print(("  ok   " if cond else "  FAIL ") + msg)
        if not cond:
            fails.append(msg)

    info = api("/api/info")
    check(bool(info.get("open")), "DSP open (%s)" % info.get("open"))
    check(info.get("mem") == args.mem, "device is on memory %d (device shows %d)" %
          (args.mem, args.mem + 1))
    gains = info.get("outputGainDb") or []
    delays = info.get("outputDelayMs") or []

    for o in file_mem["outputs"]:
        p = o["index"]
        if p > 7:
            continue
        d = api("/api/eqread", {"mem": args.mem, "path": p})
        if not d.get("ok"):
            check(False, "path %d: read failed (%s)" % (p, d.get("error")))
            continue
        dev = [b for b in d.get("bands", []) if b.get("enable") is not False
               and abs(float(b.get("gain") or 0)) > 0.001]
        want = [b for b in o["eq"] if b["Gain"] != 0.0]
        # Match on centre frequency (the DSP keeps the slot, not our order).
        unmatched = []
        for b in want:
            hit = None
            for c in dev:
                if abs(float(c.get("fc") or 0) - b["Fc"]) <= 1.0 and \
                   abs(float(c.get("gain") or 0) - b["Gain"]) <= args.tol_gain and \
                   abs(float(c.get("q") or 0) - b["Q"]) <= 0.06:
                    hit = c
                    break
            if hit is None:
                unmatched.append(b)
            else:
                dev.remove(hit)
        extra = [c for c in dev]
        check(not unmatched and not extra,
              "path %d: %d bands match on the device%s" %
              (p, len(want) - len(unmatched),
               "" if not (unmatched or extra) else
               "  missing=%s extra=%s" % (
                   ["%.0f/%+.1f/Q%.2f" % (b["Fc"], b["Gain"], b["Q"]) for b in unmatched],
                   ["%.0f/%+.1f/Q%.2f" % (float(c.get("fc") or 0), float(c.get("gain") or 0),
                                          float(c.get("q") or 0)) for c in extra])))

        if p < len(gains):
            check(abs(float(gains[p]) - o["gain"]) <= args.tol_gain,
                  "path %d: gain %.1f dB on device, %.1f in file" %
                  (p, float(gains[p]), o["gain"]))
        if p < len(delays):
            check(abs(float(delays[p]) - o["delay"]) <= 0.05,
                  "path %d: delay %.1f ms on device, %.1f in file" %
                  (p, float(delays[p]), o["delay"]))

        pb = d.get("passband") or {}
        hp_el = next(x for x in o["passBand"] if x["hiLowType"] == 0)
        lp_el = next(x for x in o["passBand"] if x["hiLowType"] == 1)
        if pb:
            hw = float(pb.get("lowFc") or 0)
            lw = float(pb.get("highFc") or 0)
            check(abs(hw - hp_el["Fc"]) <= 2, "path %d: HP %.0f Hz on device, %.0f in file" % (p, hw, hp_el["Fc"]))
            check(abs(lw - lp_el["Fc"]) <= 2, "path %d: LP %.0f Hz on device, %.0f in file" % (p, lw, lp_el["Fc"]))

    print("\n%d failure(s)" % len(fails))
    return 1 if fails else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
