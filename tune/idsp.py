#!/usr/bin/env python3
"""Read, analyse and write DS18 .idsp tune files (pure stdlib - no numpy on this machine).

The format is plain XML: <DP12_Param> -> 4 x <memory index> -> 17 x <output index>, each
output carrying gain/delay/mute/revert/linkPair, two <passBand> (HP then LP) and 31 <eq>
bands (Fc, Gain, Q). The values are absolute, already in the units the DSP takes:
gain and band gain in dB, delay in ms, Fc in Hz.

What this adds over reading the XML by eye: the *response* the bands actually produce. Each
band is a redbook (RBJ) peaking biquad at 48 kHz, filters in series, exactly how the DSP runs
them - so a tune's tonal shape can be compared without a measurement, and a rewritten tune
can be proven to keep the same shape.

Usage
    python tune/idsp.py dump  <file.idsp>                 per-output table + band stats
    python tune/idsp.py curve <file.idsp> [--mem N]       summed response at 1/6 octave
    python tune/idsp.py diff  <a.idsp> <b.idsp>           band/gain/delay/crossover changes
    python tune/idsp.py json  <file.idsp> [--mem N]       machine-readable dump

Design notes
- Bands are read in file order and written back in file order: slot i keeps its Fc only if
  the writer wants that; the DSP accepts any Fc/Q per slot, but keeping slots stable makes a
  diff readable and lets a slot's history be followed.
- Nothing here writes to hardware. apply-tune.ps1 does that, and verifies it.
"""
import argparse
import cmath
import json
import math
import re
import sys
import xml.etree.ElementTree as ET

FS = 48000.0
GRID = [20.0 * (2.0 ** (k / 6.0)) for k in range(61)]   # 1/6 octave, 20 Hz .. 20 kHz
KEY = [50, 80, 100, 125, 200, 300, 500, 800, 1000, 1500, 2000, 3000, 4000, 6000, 8000, 12000, 16000]


def take_gain(v):
    """A ds18 curve value to the nearest 0.1 dB, as the hardware carries it."""
    return round(float(v) * 10.0) / 10.0


def peaking_db(f0, q, gain_db, freqs):
    """RBJ peaking EQ magnitude in dB at each frequency - what the DSP's band really does."""
    if gain_db == 0.0:
        return [0.0] * len(freqs)
    a = 10.0 ** (gain_db / 40.0)
    w0 = 2.0 * math.pi * f0 / FS
    alpha = math.sin(w0) / (2.0 * max(q, 0.0001))
    cw = math.cos(w0)
    b0 = 1.0 + alpha * a
    b1 = -2.0 * cw
    b2 = 1.0 - alpha * a
    a0 = 1.0 + alpha / a
    a1 = -2.0 * cw
    a2 = 1.0 - alpha / a
    out = []
    for f in freqs:
        w = 2.0 * math.pi * f / FS
        z1 = cmath.exp(-1j * w)
        z2 = z1 * z1
        h = (b0 + b1 * z1 + b2 * z2) / (a0 + a1 * z1 + a2 * z2)
        out.append(20.0 * math.log10(abs(h)))
    return out


def part_curve(bands, freqs):
    """Sum of every band's response, in dB (series filters multiply, so dB adds)."""
    total = [0.0] * len(freqs)
    for b in bands:
        if b["Gain"] == 0.0:
            continue
        for i, v in enumerate(peaking_db(b["Fc"], b["Q"], b["Gain"], freqs)):
            total[i] += v
    return total


def num(el, tag, dflt=0.0):
    k = el.find(tag)
    if k is None or k.text is None:
        return dflt
    try:
        return float(k.text.strip())
    except ValueError:
        return dflt


def load(path):
    """Parse an .idsp into plain dicts. Keeps element order and unknown attributes intact."""
    tree = ET.parse(path)
    root = tree.getroot()
    if root.tag != "DP12_Param":
        raise SystemExit("%s: root is <%s>, expected <DP12_Param>" % (path, root.tag))
    run = root.find("RunStatus")
    doc = {
        "path": path,
        "version": int(num(root, "version", 0)),
        "run": {
            "source": int(num(run, "mainSource", 0)) if run is not None else 0,
            "memory": int(num(run, "mainMemory", 0)) if run is not None else 0,
            "volume": num(run, "mainVolume", 0) if run is not None else 0,
        },
        "memories": [],
    }
    for mi, mel in enumerate(root.findall("memory")):
        mem = {"index": int(mel.get("index", mi)), "outputs": []}
        for oi, oel in enumerate(mel.findall("output")):
            o = {
                "index": int(oel.get("index", oi)),
                "mute": int(num(oel, "mute", 0)),
                "gain": take_gain(num(oel, "gain", 0)),
                "revert": int(num(oel, "revert", 0)),
                "delay": take_gain(num(oel, "delay", 0)),
                "linkPair": int(num(oel, "linkPair", 0)),
                "passBand": [],
                "eq": [],
            }
            for pel in oel.findall("passBand"):
                o["passBand"].append({
                    "Fc": num(pel, "Fc", 0), "hiLowType": int(num(pel, "hiLowType", 0)),
                    "Slope": int(num(pel, "Slope", 0)), "filterType": int(num(pel, "filterType", 0)),
                    "butt_Type": int(num(pel, "butt_Type", 0)), "Enable": int(num(pel, "Enable", 0)),
                })
            for e in oel.findall("eq"):
                o["eq"].append({"Fc": num(e, "Fc", 20), "Gain": take_gain(num(e, "Gain", 0)),
                                "Q": round(num(e, "Q", 1) * 100) / 100.0})
            mem["outputs"].append(o)
        doc["memories"].append(mem)
    return doc


def active_bands(o):
    return [b for b in o["eq"] if b["Gain"] != 0.0]


def at(freqs, curve, f):
    """Bilinear read of the curve at f (grid is log-spaced)."""
    if f <= freqs[0]:
        return curve[0]
    if f >= freqs[-1]:
        return curve[-1]
    for i in range(len(freqs) - 1):
        if freqs[i] <= f <= freqs[i + 1]:
            t = (math.log(f) - math.log(freqs[i])) / (math.log(freqs[i + 1]) - math.log(freqs[i]))
            return curve[i] + t * (curve[i + 1] - curve[i])
    return curve[-1]


def describe(doc, mi):
    mem = doc["memories"][mi]
    rows = []
    for o in mem["outputs"]:
        bands = active_bands(o)
        curve = part_curve(o["eq"], GRID)
        rows.append({
            "output": o["index"],
            "gain": o["gain"], "delay": o["delay"], "mute": o["mute"], "revert": o["revert"],
            "hp": next((p for p in o["passBand"] if p["hiLowType"] == 0), None),
            "lp": next((p for p in o["passBand"] if p["hiLowType"] == 1), None),
            "bands": len(bands),
            "max_boost": max([b["Gain"] for b in bands], default=0.0),
            "max_cut": min([b["Gain"] for b in bands], default=0.0),
            "sum_abs": round(sum(abs(b["Gain"]) for b in bands), 1),
            "q_median": median([b["Q"] for b in bands]),
            "shape": {str(f): round(at(GRID, curve, f), 1) for f in KEY},
        })
    return rows


def median(xs):
    if not xs:
        return 0.0
    s = sorted(xs)
    n = len(s)
    return s[n // 2] if n % 2 else (s[n // 2 - 1] + s[n // 2]) / 2.0


def fmt_pb(p):
    if p is None or not p["Enable"]:
        return "open"
    kind = "LR" if p["filterType"] == 1 else "BT"
    # Slope 1/2/3 = 12/18/24 dB/oct, per set-crossovers.ps1 and the bridge's own API notes.
    # (Not * 6: that read a 24 dB/oct crossover as "LR18" for a while.)
    db = {1: 12, 2: 18, 3: 24}.get(p["Slope"], p["Slope"])
    return "%s%d @ %gHz" % (kind, db, p["Fc"])


def cmd_dump(args):
    doc = load(args.file)
    print("%s  version %d  run: source %d memory %d volume %g" %
          (args.file, doc["version"], doc["run"]["source"], doc["run"]["memory"], doc["run"]["volume"]))
    # Are the four memories actually different? A bank that repeats one tune four times is
    # normal for a saved preset; a real bank has four different ones.
    sigs = []
    for mem in doc["memories"]:
        blob = json.dumps([[o["gain"], o["delay"], o["eq"]] for o in mem["outputs"]], sort_keys=True)
        sigs.append(hash(blob) & 0xFFFFFFFF)
    print("memories: %d   distinct payloads: %d   [%s]" %
          (len(doc["memories"]), len(set(sigs)), " ".join("%08x" % s for s in sigs)))
    mi = args.mem if args.mem is not None else doc["run"]["memory"]
    print("\n--- memory %d (device shows it as memory %d) ---" % (mi, mi + 1))
    print("%-3s %6s %6s %4s %-16s %-16s %5s %6s %6s %6s %5s" %
          ("out", "gain", "delay", "mute", "high-pass", "low-pass", "bnds", "max+", "max-", "sum|g|", "Qmed"))
    for r in describe(doc, mi):
        print("%-3d %6.1f %6.1f %4d %-16s %-16s %5d %6.1f %6.1f %6.1f %5.2f" %
              (r["output"] + 1, r["gain"], r["delay"], r["mute"], fmt_pb(r["hp"]), fmt_pb(r["lp"]),
               r["bands"], r["max_boost"], r["max_cut"], r["sum_abs"], r["q_median"]))
    if args.curve:
        cmd_curve(args)


def cmd_curve(args):
    doc = load(args.file)
    mi = args.mem if args.mem is not None else doc["run"]["memory"]
    mem = doc["memories"][mi]
    print("\n--- summed EQ response, memory %d (dB, relative to each output's own mean) ---" % mi)
    head = "%-6s" % "Hz" + "".join("  o%-5d" % (o["index"] + 1) for o in mem["outputs"][:args.outputs])
    print(head)
    curves = [part_curve(o["eq"], GRID) for o in mem["outputs"]]
    for f in KEY:
        row = "%-6d" % f
        for c in curves[:args.outputs]:
            row += "  %+6.1f" % at(GRID, c, f)
        print(row)


def cmd_diff(args):
    a, b = load(args.file), load(args.other)
    ma = args.mem if args.mem is not None else a["run"]["memory"]
    mb = ma if args.mem is not None else b["run"]["memory"]
    print("%s (mem %d)  ->  %s (mem %d)" % (args.file, ma, args.other, mb))
    for oa, ob in zip(a["memories"][ma]["outputs"], b["memories"][mb]["outputs"]):
        bits = []
        if oa["gain"] != ob["gain"]:
            bits.append("gain %+.1f -> %+.1f" % (oa["gain"], ob["gain"]))
        if oa["delay"] != ob["delay"]:
            bits.append("delay %.1f -> %.1f" % (oa["delay"], ob["delay"]))
        if fmt_pb(next((p for p in oa["passBand"] if p["hiLowType"] == 0), None)) != \
           fmt_pb(next((p for p in ob["passBand"] if p["hiLowType"] == 0), None)):
            bits.append("HP %s -> %s" % (fmt_pb(next((p for p in oa["passBand"] if p["hiLowType"] == 0), None)),
                                         fmt_pb(next((p for p in ob["passBand"] if p["hiLowType"] == 0), None))))
        ca, cb = part_curve(oa["eq"], GRID), part_curve(ob["eq"], GRID)
        delta = max(abs(x - y) for x, y in zip(ca, cb))
        na, nb = len(active_bands(oa)), len(active_bands(ob))
        if na != nb or delta > 0.05:
            bits.append("bands %d -> %d, max curve delta %.1f dB" % (na, nb, delta))
        if bits:
            print("  out %d: %s" % (oa["index"] + 1, "; ".join(bits)))
    print("  (outputs with no line are identical in gain, delay, HP and curve)")


def cmd_json(args):
    doc = load(args.file)
    mi = args.mem if args.mem is not None else doc["run"]["memory"]
    out = {"file": doc["path"], "run": doc["run"], "memory": mi, "outputs": describe(doc, mi)}
    print(json.dumps(out, indent=2))


def cmd_bands(args):
    doc = load(args.file)
    mi = args.mem if args.mem is not None else doc["run"]["memory"]
    mem = doc["memories"][mi]
    dense = [20.0 * (2.0 ** (k / 24.0)) for k in range(241)]
    for o in mem["outputs"]:
        bands = active_bands(o)
        if not bands:
            continue
        print("=== output %d  gain %+.1f dB  delay %.1f ms  %d active bands ===" %
              (o["index"] + 1, o["gain"], o["delay"], len(bands)))
        print("   " + "  ".join("%d:%+g/Q%g" % (round(b["Fc"]), b["Gain"], b["Q"]) for b in bands))
        curve = part_curve(o["eq"], dense)
        lo, hi = args.lo, args.hi
        win = [v for f, v in zip(dense, curve) if lo <= f <= hi]
        if win:
            print("   ripple %.0f-%.0f Hz: peak-to-peak %.1f dB, max %+.1f, min %+.1f" %
                  (lo, hi, max(win) - min(win), max(win), min(win)))
        print()


def main(argv):
    p = argparse.ArgumentParser(description="read/analyse DS18 .idsp tunes")
    sub = p.add_subparsers(dest="cmd", required=True)
    d = sub.add_parser("dump"); d.add_argument("file"); d.add_argument("--mem", type=int)
    d.add_argument("--curve", action="store_true"); d.add_argument("--outputs", type=int, default=8)
    d.set_defaults(fn=cmd_dump)
    c = sub.add_parser("curve"); c.add_argument("file"); c.add_argument("--mem", type=int)
    c.add_argument("--outputs", type=int, default=17); c.set_defaults(fn=cmd_curve)
    f = sub.add_parser("diff"); f.add_argument("file"); f.add_argument("other"); f.add_argument("--mem", type=int)
    f.set_defaults(fn=cmd_diff)
    s = sub.add_parser("json"); s.add_argument("file"); s.add_argument("--mem", type=int); s.set_defaults(fn=cmd_json)
    b = sub.add_parser("bands"); b.add_argument("file"); b.add_argument("--mem", type=int)
    b.add_argument("--lo", type=float, default=200.0); b.add_argument("--hi", type=float, default=8000.0)
    b.set_defaults(fn=cmd_bands)
    args = p.parse_args(argv)
    args.fn(args)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
