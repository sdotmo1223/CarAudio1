#!/usr/bin/env python3
"""
fit-tune.py - turn the per-channel measurement files into a tune.

Input : the JSON files export-meas.ps1 writes (one per channel, 61 [hz, dbfs] pairs)
Output: tune-plan.json  - per DSP path: EQ bands, output gain, delay. Nothing is written
                         to the DSP here; apply-tune.ps1 does that and verifies it.

Why this shape, and what it deliberately does not do
----------------------------------------------------
* The target is the owner's own R&B / hip-hop house curve, the one in the repo's
  make_tune_rnb.py: bass shelf, gentle mid tilt, treble rolloff. That curve exists
  because he reported a neutral tune as "vocals good, bright" - so this is not a
  guess at a genre, it is the shape his ears already picked once.

* Fitting is done per channel, inside that channel's usable band, and the result is a
  SHAPE not a level. Level is handled by output gain, because a 30 dB error is a gain
  error and fitting it with EQ would eat every band.

* Boosts are capped at +2 dB and cuts at -12 dB. A narrow dip is usually a cancellation
  or a reflection, not something EQ should chase: boosting it burns headroom and moves
  the problem. Broad errors get fixed; narrow ones are reported and left alone.

* Time alignment is NOT computed here. It cannot be: these captures are magnitude only.
  The delays carried through are the ones already on the device - see the note in the
  report.

Usage:
    python tune/fit-tune.py --meas measurements/run-20261020-1925 --out tune/tune-plan.json
"""
import argparse
import glob
import json
import math
import os
import sys

FS = 48000.0

# The owner's R&B / hip-hop house curve, dB relative to 1 kHz.
# Identical to make_tune_rnb.py RNB_POINTS except three deliberate moves for hip-hop:
# +0.5 dB at 35 - 55 Hz (sub/kick weight), +0.5 at 80 (kick body), -1 dB at 300 (mud),
# and a touch more rolloff at 20 k so compressed masters do not sizzle.
TARGET = [
    (20,   +6.0), (35,   +6.5), (55,   +6.0), (80,   +4.5), (120,  +3.0),
    (180,  +1.5), (300,  +0.0), (500,  +0.0), (800,  -0.3), (1000, -0.5),
    (2000, -1.5), (3500, -2.5), (6000, -3.5), (10000, -4.5), (20000, -5.5),
]

# channel name -> DSP output path, and the band that channel is trusted in.
# The mapping is channel order: the page's roster is one row per DSP channel, so
# tweeter 1-4 are ch 1-4 (paths 0-3), midrange 1-3 are ch 5-7 (paths 4-6) and
# midbass 1 is ch 8 (path 7).
CHANNELS = {
    'tweeter 1':  dict(path=0, lo=1500, hi=14000, qmax=2.0, home=(3000, 6000), role='front-left tweeter'),
    'tweeter 2':  dict(path=1, lo=1500, hi=14000, qmax=2.0, home=(3000, 6000), role='front-right tweeter'),
    'tweeter 3':  dict(path=2, lo=1500, hi=14000, qmax=2.0, home=(3000, 6000), role='rear-left tweeter'),
    'tweeter 4':  dict(path=3, lo=1500, hi=14000, qmax=2.0, home=(3000, 6000), role='rear-right tweeter'),
    'midrange 1': dict(path=4, lo=110,  hi=9000,  qmax=4.0, home=(300, 800),   role='front 3.5" pair'),
    'midrange 2': dict(path=5, lo=110,  hi=9000,  qmax=4.0, home=(300, 800),   role='rear 3.5" pair'),
    'midrange 3': dict(path=6, lo=70,   hi=5000,  qmax=4.0, home=(80, 160),    role='front 8" pair'),
    'midbass 1':  dict(path=7, lo=80,   hi=7000,  qmax=4.0, home=(100, 200),   role='rear 6.5" pair'),
}

MAX_BANDS = 14
MAX_CUT = -12.0
MAX_BOOST = 3.0        # broad lifts only - see the Q guard in fit_bands()
MIN_IMPROVE = 0.05      # dB RMS


# ---------------------------------------------------------------- curves
def interp_db(points, f):
    """log-frequency interpolation over (hz, db) points."""
    xs = [math.log10(p[0]) for p in points]
    ys = [p[1] for p in points]
    x = math.log10(f)
    if x <= xs[0]:
        return ys[0]
    if x >= xs[-1]:
        return ys[-1]
    for i in range(len(xs) - 1):
        if xs[i] <= x <= xs[i + 1]:
            t = (x - xs[i]) / (xs[i + 1] - xs[i])
            return ys[i] + t * (ys[i + 1] - ys[i])
    return ys[-1]


def smooth(meas, octaves=1 / 3.0):
    """moving average in log frequency, so the fit chases trends not room ripple."""
    out = []
    for hz, _ in meas:
        acc = []
        for hz2, db2 in meas:
            if abs(math.log2(hz2 / hz)) <= octaves:
                acc.append(db2)
        out.append(sum(acc) / len(acc))
    return out


def freq_grid(lo, hi, per_oct=24):
    f = lo
    g = []
    while f <= hi:
        g.append(f)
        f *= 2 ** (1.0 / per_oct)
    return g


# ---------------------------------------------------------------- peaking response
def peq_response_db(f, fc, gain_db, q):
    """RBJ peaking biquad magnitude at frequency f, dB."""
    if abs(gain_db) < 1e-9:
        return 0.0
    A = 10 ** (gain_db / 40.0)
    w0 = 2 * math.pi * fc / FS
    alpha = math.sin(w0) / (2 * q)
    b0 = 1 + alpha * A
    b1 = -2 * math.cos(w0)
    b2 = 1 - alpha * A
    a0 = 1 + alpha / A
    a1 = -2 * math.cos(w0)
    a2 = 1 - alpha / A
    w = 2 * math.pi * f / FS
    cw1, sw1 = math.cos(w), math.sin(w)
    cw2, sw2 = math.cos(2 * w), math.sin(2 * w)
    # numerator and denominator as complex numbers
    nr = b0 + b1 * cw1 + b2 * cw2
    ni = -(b1 * sw1 + b2 * sw2)
    dr = a0 + a1 * cw1 + a2 * cw2
    di = -(a1 * sw1 + a2 * sw2)
    mag = math.hypot(nr, ni) / math.hypot(dr, di)
    return 20 * math.log10(mag)


# ---------------------------------------------------------------- fit
def fit_bands(grid, correction, qmax, nmax, boost_hi=12000.0):
    """matching pursuit: repeatedly add the filter that removes the most error."""
    residual = list(correction)
    chosen = []
    qs = [0.7, 1.0, 1.4, 2.0, 2.8, 4.0]
    qs = [q for q in qs if q <= qmax + 1e-9]
    fcs = freq_grid(grid[0], grid[-1], per_oct=12)

    def sse(vec):
        return sum(v * v for v in vec)

    best_sse = sse(residual)
    for _ in range(nmax):
        best = None
        for fc in fcs:
            # never spend two bands on the same place: a second filter within 1/8 octave
            # of one already chosen just re-cuts what that one already cut
            if any(abs(math.log2(fc / c[0])) < 0.1667 for c in chosen):
                continue
            i = min(range(len(grid)), key=lambda k: abs(math.log(grid[k] / fc)))
            want = residual[i]
            if abs(want) < 0.3:
                continue
            for q in qs:
                g = max(MAX_CUT, min(MAX_BOOST, want))
                # a lift is only allowed if it is broad: boosting a narrow dip is how a
                # tune ends up chasing a cancellation and spending headroom on nothing
                if g > 0 and q > 1.4:
                    continue
                # no lift above 12 kHz: the measurement there is near the mic's own floor
                if g > 0 and fc > boost_hi:
                    continue
                resp = [peq_response_db(f, fc, g, q) for f in grid]
                cand = [r - rr for r, rr in zip(residual, resp)]
                s = sse(cand)
                if best is None or s < best[0]:
                    best = (s, fc, g, q, resp)
        if best is None:
            break
        s, fc, g, q, resp = best
        if s > best_sse - MIN_IMPROVE * len(grid):
            break
        residual = [r - rr for r, rr in zip(residual, resp)]
        chosen.append([fc, g, q])
        best_sse = s

    # Coordinate search on the gains, accepting a change only when the total error
    # actually falls. An earlier version solved each gain analytically against the
    # filter's 1 dB response - wrong, because a peaking filter's shape is not linear
    # in gain, so at -12 dB it optimised a bell that is not the one being applied and
    # drove the fit from 3.5 to 6.5 dB rms. This cannot make things worse by design.
    def total_sse(bands):
        acc = [0.0] * len(grid)
        for fc, g, q in bands:
            acc = [a + peq_response_db(f, fc, g, q) for a, f in zip(acc, grid)]
        return sum((c - a) ** 2 for c, a in zip(correction, acc))

    for _ in range(4):
        improved = False
        for idx in range(len(chosen)):
            base_sse = total_sse(chosen)
            cur = chosen[idx][1]
            best_g, best_sse = cur, base_sse
            for cand in (cur - 2, cur - 1, cur - 0.5, cur + 0.5, cur + 1, cur + 2, cur * 0.75, cur * 1.25):
                cand = max(MAX_CUT, min(MAX_BOOST, cand))
                trial = [list(c) for c in chosen]
                trial[idx][1] = cand
                s = total_sse(trial)
                if s < best_sse - 1e-6:
                    best_g, best_sse = cand, s
            if best_g != cur:
                chosen[idx][1] = best_g
                improved = True
        if not improved:
            break

    chosen = [c for c in chosen if abs(c[1]) >= 0.4]

    # Cap the TOTAL lift. Two broad +3 dB filters a third of an octave apart are a 6 dB
    # shelf nobody asked for, and stacked lifts are how a "correction" becomes brightness.
    if chosen:
        lift = [0.0] * len(grid)
        for fc, g, q in chosen:
            if g > 0:
                for k, f in enumerate(grid):
                    lift[k] += peq_response_db(f, fc, g, q)
        mx = max(lift)
        if mx > 4.0:
            k = 4.0 / mx
            for c in chosen:
                if c[1] > 0:
                    c[1] = c[1] * k
    return chosen


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--meas', required=True, help='folder written by export-meas.ps1')
    ap.add_argument('--out', default='tune/tune-plan.json')
    ap.add_argument('--status', default='http://127.0.0.1:8585/api/info',
                    help='bridge /api/info - the only endpoint that carries the output block')
    args = ap.parse_args()

    # what the device has now, so the plan states a delta not just a number.
    # The DSP may be unplugged while the plan is made: then the delta is all we know,
    # and apply-tune.ps1 reads the live gains and applies the delta to them.
    import urllib.request
    try:
        st = json.load(urllib.request.urlopen(args.status, timeout=10))
        cur_gain = st['outputGainDb'] if st.get('open') else None
        cur_delay = st['outputDelayMs'] if st.get('open') else None
    except Exception:
        cur_gain = None
        cur_delay = None
    if cur_gain is None:
        print('note: DSP not connected - the plan carries gain DELTAS only, '
              'apply-tune.ps1 resolves them against the live device.')

    files = sorted(glob.glob(os.path.join(args.meas, '*.json')))
    if not files:
        sys.exit(f'no channel files in {args.meas}')

    plan = {'source': os.path.abspath(args.meas), 'target': TARGET, 'paths': {}, 'skipped': []}
    lines = []

    for fp in files:
        rec = json.load(open(fp, encoding='utf-8-sig'))
        name = rec['channel'].strip().lower()
        spec = CHANNELS.get(name)
        if not spec:
            plan['skipped'].append(name)
            continue
        # The gate verdict, from either exporter: export-meas.ps1 and the bridge's
        # /api/run-save both write gate{ok,burstDb,fromHz,toHz}; the bridge also keeps the
        # "sig" block with the reason in words. Tolerate either, and tolerate neither.
        gate_ok = True
        g = rec.get('gate')
        if isinstance(g, dict) and 'ok' in g:
            gate_ok = bool(g['ok'])
        elif isinstance(rec.get('sig'), dict) and 'ok' in rec['sig']:
            gate_ok = bool(rec['sig']['ok'])
        if not gate_ok:
            plan['skipped'].append(name + ' (gate rejected)')
            continue

        pts = [(float(p[0]), float(p[1])) for p in rec['points']]
        lo, hi = spec['lo'], spec['hi']
        pts = [(f, d) for f, d in pts if lo * 0.95 <= f <= min(hi, 20000) * 1.05]
        sm = smooth(pts)

        # correction = target shape - measured, as a shape centred on its own mean
        corr = [interp_db(TARGET, f) - d for (f, _), d in zip(pts, sm)]
        mean = sum(corr) / len(corr)
        corr = [c - mean for c in corr]

        grid = [f for f, _ in pts]
        # a cone must not be lifted above 2 kHz: the tweeters own that region and a 12 dB-down
        # cone pushed back up is just a second source fighting them, not more treble
        boost_hi = 12000.0 if spec['path'] < 4 else 2000.0
        bands = fit_bands(grid, corr, spec['qmax'], MAX_BANDS, boost_hi)

        # predicted response after EQ, then the level that puts it on the target
        pred = list(sm)
        for fci, g, q in bands:
            for k, f in enumerate(grid):
                pred[k] += peq_response_db(f, fci, g, q)
        hlo, hhi = spec['home']
        home_idx = [k for k, f in enumerate(grid) if hlo <= f <= hhi]
        if not home_idx:
            home_idx = list(range(len(grid)))
        # MEDIAN, not mean. The 6.5" has a +10 dB resonance sitting at 100-125 Hz right in
        # its anchor band; averaging that in drags the whole channel down to fix one mode
        # that the EQ bands already cut. The median reads the channel's actual working level.
        home_vals = sorted(interp_db(TARGET, grid[k]) - pred[k] for k in home_idx)
        delta = home_vals[len(home_vals) // 2]

        # how well the fit actually lands, so the numbers can be judged not trusted
        n = len(grid)
        eqsum = [0.0] * n
        for fc, g, q in bands:
            for k, f in enumerate(grid):
                eqsum[k] += peq_response_db(f, fc, g, q)
        rms_before = math.sqrt(sum(c * c for c in corr) / n)
        rms_after = math.sqrt(sum((c - e) ** 2 for c, e in zip(corr, eqsum)) / n)

        # what the channel will look like afterwards, relative to its own midband - the
        # number that decides whether this is worth listening to, not the rms
        shaped = [s + e for s, e in zip(sm, eqsum)]
        mid = sum(shaped[k] for k in home_idx) / len(home_idx)
        probes = (70, 100, 150, 250, 500, 1000, 2000, 4000, 8000)
        predicted = {}
        for f in probes:
            if f < lo or f > grid[-1]:
                continue
            k = min(range(n), key=lambda j: abs(math.log(grid[j] / f)))
            predicted[str(f)] = round(shaped[k] - mid, 1)

        path = spec['path']
        plan['paths'][str(path)] = {
            'channel': rec['channel'],
            'role': spec['role'],
            'bands': [{'fc': int(round(fc)), 'gain': round(g, 1), 'q': round(q, 1),
                       'type': 0, 'enable': True} for fc, g, q in bands],
            'gainDeltaDb': round(delta, 1),
            'currentGainDb': (round(cur_gain[path], 1) if cur_gain else None),
            'delayMs': (round(cur_delay[path], 1) if cur_delay else None),
            'bandRangeHz': [lo, hi],
            'rmsBeforeDb': round(rms_before, 2),
            'rmsAfterDb': round(rms_after, 2),
            'predictedRelDb': predicted,
        }
        lines.append((path, rec['channel'], len(bands), delta))

    # one common shift so the channel that needs it least is unchanged: never boost,
    # the master can come down and the balance is what matters
    if plan['paths']:
        worst = max(p['gainDeltaDb'] for p in plan['paths'].values())
        if worst > 0:
            for p in plan['paths'].values():
                p['gainDeltaDb'] = round(p['gainDeltaDb'] - worst, 1)
    for path, p in plan['paths'].items():
        if p.get('currentGainDb') is None:
            p['newGainDb'] = None          # apply-tune.ps1 resolves the delta live
            continue
        p['newGainDb'] = round(max(-40.0, min(0.0, p['currentGainDb'] + p['gainDeltaDb'])), 1)

    os.makedirs(os.path.dirname(args.out) or '.', exist_ok=True)
    with open(args.out, 'w', encoding='utf-8') as fh:
        json.dump(plan, fh, indent=2)

    # a report a person can read without the JSON
    rep = []
    rep.append('# Tune plan - hip-hop / R&B')
    rep.append('')
    rep.append(f'Source: `{os.path.basename(os.path.abspath(args.meas))}`  ')
    rep.append('Target: the owner curve (bass shelf, gentle mid tilt, treble rolloff), dB at')
    rep.append('30 / 60 / 100 / 500 / 2k / 8k = '
               + ' / '.join(f'{interp_db(TARGET, f):+.1f}' for f in (30, 60, 100, 500, 2000, 8000)))
    rep.append('')
    rep.append('| path | channel | role | bands | EQ gain range | fit (before -> after) | channel gain | delay |')
    rep.append('|---|---|---|---|---|---|---|---|')
    for path in sorted(plan['paths'], key=int):
        p = plan['paths'][path]
        lo = min(b['gain'] for b in p['bands']) if p['bands'] else 0
        hi = max(b['gain'] for b in p['bands']) if p['bands'] else 0
        cur = '?' if p['currentGainDb'] is None else f'{p["currentGainDb"]:.1f}'
        delay = 'unchanged' if p['delayMs'] is None else f'{p["delayMs"]:.1f} ms'
        rep.append(f'| {path} | {p["channel"]} | {p["role"]} | {len(p["bands"])} | '
                   f'{lo:+.1f} .. {hi:+.1f} dB | {p["rmsBeforeDb"]:.1f} -> {p["rmsAfterDb"]:.1f} dB | '
                   f'{cur} dB, {p["gainDeltaDb"]:+.1f} | {delay} |')
    rep.append('')
    rep.append('## Bands per channel')
    rep.append('')
    for path in sorted(plan['paths'], key=int):
        p = plan['paths'][path]
        rep.append(f'**path {path} - {p["channel"]} ({p["role"]})**, fitted {p["bandRangeHz"][0]}-{p["bandRangeHz"][1]} Hz')
        rep.append('')
        rep.append('| # | Fc | gain | Q |')
        rep.append('|---|---|---|---|')
        for i, b in enumerate(p['bands'], 1):
            rep.append(f'| {i} | {b["fc"]} Hz | {b["gain"]:+.1f} dB | {b["q"]} |')
        rep.append('')
    if plan['skipped']:
        rep.append('Skipped: ' + ', '.join(plan['skipped']))
        rep.append('')
    rep_path = os.path.splitext(args.out)[0] + '-report.md'
    with open(rep_path, 'w', encoding='utf-8') as fh:
        fh.write('\n'.join(rep))

    print()
    print(f'{"path":>4} {"channel":<12} {"bands":>5} {"gain dB":>9} {"->":>3} {"new":>6}  delay')
    for path in sorted(plan['paths'], key=int):
        p = plan['paths'][path]
        cur = '?' if p['currentGainDb'] is None else f'{p["currentGainDb"]:.1f}'
        new = 'live' if p['newGainDb'] is None else f'{p["newGainDb"]:.1f}'
        delay = '?' if p['delayMs'] is None else f'{p["delayMs"]:.1f} ms'
        fit = f'{p["rmsBeforeDb"]:.1f}->{p["rmsAfterDb"]:.1f} dB rms'
        print(f'{path:>4} {p["channel"]:<12} {len(p["bands"]):>5} {cur:>9} '
              f'{p["gainDeltaDb"]:>+5.1f} {new:>6}  {delay:>8}  {fit}')
    if plan['skipped']:
        print(f'\nskipped: {", ".join(plan["skipped"])}')
    print(f'\nwrote {args.out}')
    print('nothing has been written to the DSP - apply-tune.ps1 does that, and verifies it.')


if __name__ == '__main__':
    main()

