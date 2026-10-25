#!/usr/bin/env python3
"""
fit-dense.py - a MAXIMUM-DENSITY tune from the same measurement tune 1 was built from.

What this is
------------
The owner asked for a "well rounded, highly tuned" hip-hop/R&B tune that uses MOST of the
DSP's 31 bands per output. fit-tune.py (tune 1) smoothed to 1/3 octave and stopped at
<=14 bands per path, fitting only broad shape. This fitter keeps every owner rule that
protects the hardware and the data, but fits the measurement at its native 1/6-octave
resolution with up to 28 bands per path:

  KEPT (non-negotiable physics / owner rules):
    - boosts: Q <= 1.4 only, per-band cap +3.0 dB, TOTAL lift cap +4.0 dB per path
      (broad lifts only - a narrow boost is chasing a cancellation)
    - no boost above 12 kHz on tweeters / above 2 kHz on any cone (mic floor & breakup)
    - cuts free to -12.0 dB per band
    - level by output gain, anchored on the MEDIAN of the home band, one common shift
    - the 1.5 kHz notch on the 3.5" pairs is a copied measurement artefact: the region
      1000-1700 Hz is bridged before fitting so it can never be chased
  CHANGED (what the owner asked for):
    - smoothing 1/3 -> 1/6 octave (the measurement's own resolution)
    - matching-pursuit band ceiling 14 -> 28 (tweeters are capped at ~20 by bin count)
    - Q ladder for cuts extends to 4.0
    - candidate ladder 1/12 octave, min separation 1/12 octave (was 1/8)
    - --fill: after the shared stage-3 trim, ONE more legal pass per path with the bands
      already found held as seeds, chasing the ADJUSTED target up to the 31-band ceiling
      (or until nothing strictly improves). Remaining lift headroom is computed from the
      seeded bands, so fill bands obey the same caps as every other band. With --fill the
      trim and the fill ALTERNATE to a fixpoint (max 4 rounds): each round re-derives the
      ideal common shift from the CURRENT system, so a trim that ran before a fill cannot
      permanently miss what the fill revealed. Without --fill the build is the original
      single-pass behaviour, byte for byte.

Output: tune/tune-plan-dense.json (+ -report.md) and web/3-rnb-dense-2026-10-21.idsp,
built through build-swap-tunes.py's own writer (all four memory blocks carry the tune,
master -4.8 dB, delays and crossovers untouched). The file is transcription-checked by
re-reading it, and the checker is seen to fail on a deliberately perturbed copy.

NOTHING is written to the DSP here. apply-tune.ps1 -Plan tune\\tune-plan-dense.json does
that and verifies it, or the Tune tab loads the .idsp directly.
"""
import argparse
import copy
import importlib.util
import json
import math
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)


def _import(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


ft = _import("fit_tune_dense", os.path.join(HERE, "fit-tune.py"))
bs = _import("build_swap_tunes", os.path.join(HERE, "build-swap-tunes.py"))
idsp = _import("idsp", os.path.join(HERE, "idsp.py"))

TARGET = ft.TARGET
CHANNELS = ft.CHANNELS
HOME = {spec['path']: spec['home'] for spec in CHANNELS.values()}

# the region of the 3.5" curves that is a copied artefact, not the drivers (STATE.md 10-20)
ARTEFACT = (1000.0, 1700.0)
# the gains/delays live during the 19:12 measurement (evidence\\tune-before-20261020-202655.json)
BASE_GAIN = {0: -4.6, 1: -7.0, 2: -7.0, 3: -4.0, 4: -3.0, 5: -3.0, 6: -14.0, 7: -5.0}
BASE_DELAY = bs.TUNE1_DELAY
MASTER = bs.TUNE1_MASTER

MAX_BANDS = 28
MAX_TRIM_BANDS = 12       # per path, stage 3 (system trim) - must keep every path <= 31
TRIM_LO = 80.0            # below 80 Hz is the bass-shelf bottom: a physical residual,
                          # never a fake boost (playbook "Report the residual")
TRIM_HI = 12000.0         # above this the measurement nears the mic floor
MAX_CUT = -12.0
MAX_BOOST = 3.0
MAX_LIFT = 4.0          # total stacked lift per path (playbook cap)
MIN_SEP = 0.0833        # 1/12 octave between two bands on one path
MIN_IMPROVE = 0.0       # accept ANY strict improvement (owner asked for density; the
                        # legality rules live in the caps, not in a benefit threshold)
TIE_RMS = 0.03          # among candidates within 0.03 dB rms of the best, take the
                        # broadest - point-by-point error always favours narrow filters,
                        # which is how bor8f3's 1/10-octave comb got built (ATLAS.md)
QS_CUT = [0.7, 1.0, 1.4, 2.0, 2.8, 4.0]
QS_BOOST = [0.7, 1.0, 1.4]


def bridge_artefact(grid, curve):
    """Replace 1000-1700 Hz with a log-log interpolation between the shoulders. The two 3.5"
    pairs carry the same deep dip there; it was proven to be a measurement artefact, so the
    fitter is never allowed to see it."""
    lo, hi = ARTEFACT
    below = [i for i, f in enumerate(grid) if f < lo]
    above = [i for i, f in enumerate(grid) if f > hi]
    if not below or not above:
        return list(curve)
    i0, i1 = below[-1], above[0]
    f0, f1 = grid[i0], grid[i1]
    v0, v1 = curve[i0], curve[i1]
    out = list(curve)
    for i in range(i0 + 1, i1):
        t = (math.log(grid[i]) - math.log(f0)) / (math.log(f1) - math.log(f0))
        out[i] = v0 + t * (v1 - v0)
    return out


def peq_grid(grid, fc, g, q):
    """fit-tune.py's RBJ peaking response, evaluated over a whole frequency list."""
    return [ft.peq_response_db(f, fc, g, q) for f in grid]


def grid_of(pts):
    return [f for f, _ in pts]


# Dense grid for CAP enforcement: stacked-boost peaks fall BETWEEN the 1/6-oct
# measurement points (a cap checked only at measurement points read 4.00 while the true
# stack was 4.09), and plan writes round gains to 0.1 dB. Lift and budget are therefore
# evaluated here - 1/48 octave, aligned so every point of the 1/24-oct grid the
# standalone legality scan uses (20*2^(k/24), k=0..240) is included exactly.
LGRID = [20.0 * 2.0 ** (k / 48.0) for k in range(481)]


def sse(v):
    return sum(x * x for x in v)


def lift_max(bands, grid):
    """Peak stacked lift of every positive band - the quantity the +4 dB cap governs.

    Enforced INSIDE the search: a boost candidate that would push the stack past the cap
    is never accepted, so no post-hoc rescale can wreck a fit that already converged
    (the first version rescaled after convergence and turned path 6's 5.6 dB fit into
    8.8 - worse than no EQ at all).
    """
    lift = [0.0] * len(grid)
    for fc, g, q in bands:
        if g > 0:
            lift = [a + r for a, r in zip(lift, peq_grid(grid, fc, g, q))]
    return max(lift) if lift else 0.0


def fit_dense(grid, correction, nmax, boost_hi, headroom=None, seeds=None):
    """Two passes, both bounded by the same legality rules (caps inside the search, never
    a post-hoc rescale - scaling boosts after convergence can leave EQ worse than none):

    pass 1  matching pursuit at 1/12-octave spacing. A band lands only if it STRICTLY
            lowers total error, broadest-first among near-equals (the comb lesson).
    pass 2  joint bands: greedy stalls while one broad error straddles two spots - each
            band ALONE loses to the current best, but the PAIR wins once every gain and
            Q is re-solved together. Proposals enter at residual extrema (|r| >= 0.5 dB),
            the whole set is re-descended, and the set is kept only if the net error
            fell - so every band still earns its slot, none is decorative.

    headroom: optional per-frequency lift budget (list over LGRID) instead of the scalar
    +4 dB cap. The system-trim stage uses it so stacked trim + per-path boosts still
    honour +4 dB on EVERY path.
    """
    n = len(grid)
    ln = len(LGRID)
    budget = list(headroom) if headroom is not None else [MAX_LIFT] * ln
    if len(budget) != ln:
        raise SystemExit(f'headroom must be over LGRID ({ln}), got {len(budget)}')
    # seeds: bands already found (stage 1 + trim). They count toward nmax, they take part
    # in every descent and every separation check, and their stacked lift is pre-charged
    # against the budget - so a fill pass obeys every rule a first pass obeys, and can
    # never carry the total stack past the cap the seeds already used up.
    chosen = [list(s) for s in seeds] if seeds else []
    # fc is quantised to integer Hz HERE, before any response is computed, because the
    # plan writes int(round(fc)): checking the cap at a float fc the file will never carry
    # let path 6's stack land at 4.001 dB. What the cap sees is what the DSP gets.
    fcs = [float(round(f)) for f in ft.freq_grid(grid[0], grid[-1], per_oct=12)]
    lift = [0.0] * ln                 # stacked boost response over LGRID
    memo = {}                         # (fc, g, q) -> response (descent re-asks constantly)
    lmemo = {}                        # same, over LGRID (cap checks only)

    def resp_of(fc, g, q):
        key = (fc, g, q)
        if key not in memo:
            memo[key] = peq_grid(grid, fc, g, q)
        return memo[key]

    def lresp_of(fc, g, q):
        key = (fc, g, q)
        if key not in lmemo:
            lmemo[key] = peq_grid(LGRID, fc, g, q)
        return lmemo[key]

    def snap_pos(g):
        """floor a boost to the 0.1 dB slot grid: what the cap check sees is what the
        file will carry (rounding UP after the check is how 4.00 became 4.09)"""
        return math.floor(g * 10.0) / 10.0

    def boost_g(fc, g, q):
        """shrink a boost candidate (snapped down to 0.1 slots) until the per-frequency
        lift budget holds over LGRID"""
        resp = lresp_of(fc, g, q)
        new = [a + r for a, r in zip(lift, resp)]
        if max(ni - bi for ni, bi in zip(new, budget)) <= 1e-9:
            return g
        ratio = min(((budget[i] - lift[i]) / resp[i] if resp[i] > 1e-9 else 1.0)
                    for i in range(ln))
        if ratio >= 1.0:
            return g
        return max(0.0, math.floor(g * ratio * 10.0) / 10.0)

    def total_sse(bands):
        acc = [0.0] * n
        for fc, g, q in bands:
            acc = [a + r for a, r in zip(acc, resp_of(fc, g, q))]
        return sse([c - a for c, a in zip(correction, acc)])

    def lift_ok(bands):
        tot = [0.0] * ln
        for fc, g, q in bands:
            if g > 0:
                tot = [a + r for a, r in zip(tot, lresp_of(fc, g, q))]
        return all(t <= b + 1e-9 for t, b in zip(tot, budget))

    def descend(bands):
        """coordinate descent on gains then Q: only improvements, always lift-legal"""
        bands = [list(b) for b in bands]
        for _ in range(4):
            improved = False
            for idx in range(len(bands)):
                base_sse = total_sse(bands)
                cur = bands[idx][1]
                best_g, best_sse = cur, base_sse
                for step in (-2, -1, -0.5, -0.2, -0.1, 0.1, 0.2, 0.5, 1, 2):
                    cand = max(MAX_CUT, min(MAX_BOOST, cur + step))
                    if cand == cur:
                        continue
                    trial = [list(b) for b in bands]
                    trial[idx][1] = cand
                    if not lift_ok(trial):
                        continue
                    s = total_sse(trial)
                    if s < best_sse - 1e-6:
                        best_g, best_sse = cand, s
                if best_g != cur:
                    bands[idx][1] = best_g
                    improved = True
            if not improved:
                break
        for _ in range(2):
            improved = False
            for idx in range(len(bands)):
                base_sse = total_sse(bands)
                cur_q = bands[idx][2]
                ladder = QS_BOOST if bands[idx][1] > 0 else QS_CUT
                best_q, best_sse = cur_q, base_sse
                for q in ladder:
                    if q == cur_q:
                        continue
                    trial = [list(b) for b in bands]
                    trial[idx][2] = q
                    if not lift_ok(trial):
                        continue
                    s = total_sse(trial)
                    if s < best_sse - 1e-6:
                        best_q, best_sse = q, s
                if best_q != cur_q:
                    bands[idx][2] = best_q
                    improved = True
            if not improved:
                break
        return bands

    # ------------------------------------------------------------ pass 1: greedy pursuit
    if chosen:
        acc = [0.0] * n
        for fc, g, q in chosen:
            acc = [a + r for a, r in zip(acc, resp_of(fc, g, q))]
        residual = [c - a for c, a in zip(correction, acc)]
        for fc, g, q in chosen:
            if g > 0:
                lift = [a + r for a, r in zip(lift, lresp_of(fc, g, q))]
    else:
        residual = list(correction)
    best = sse(residual)
    while len(chosen) < nmax:
        cands = []
        for fc in fcs:
            if any(abs(math.log2(fc / c[0])) < MIN_SEP for c in chosen):
                continue
            i = min(range(n), key=lambda k: abs(math.log(grid[k] / fc)))
            want = residual[i]
            if abs(want) < 0.15:
                continue
            for q in QS_CUT + QS_BOOST:
                g = max(MAX_CUT, min(MAX_BOOST, want))
                if g > 0:
                    if q > 1.4 or fc > boost_hi:
                        continue
                    g = snap_pos(g)
                    g2 = boost_g(fc, g, q)
                    if g2 <= 0.05:
                        continue
                    g = g2
                    resp = resp_of(fc, g, q)
                else:
                    g = round(g, 1)    # cuts are written at 0.1 too - check the written value
                    resp = resp_of(fc, g, q)
                cands.append((sse([r - rr for r, rr in zip(residual, resp)]), fc, g, q, resp))
        if not cands:
            break
        # only candidates that STRICTLY beat the running error; among those the broadest
        # (point-by-point error favours narrow filters - how bor8f3's comb got built)
        imps = [c for c in cands if c[0] < best - 1e-9]
        if not imps:
            break
        s_min = min(c[0] for c in imps)
        pool = [c for c in imps
                if math.sqrt(c[0] / n) <= math.sqrt(s_min / n) + TIE_RMS]
        s, fc, g, q, resp = min(pool, key=lambda c: (c[3], abs(c[2])))
        residual = [r - rr for r, rr in zip(residual, resp)]
        chosen.append([fc, g, q])
        if g > 0:
            lift = [a + r for a, r in zip(lift, lresp_of(fc, g, q))]
        best = s

    chosen = descend(chosen)
    best = total_sse(chosen)

    # ------------------------------------------ pass 2: joint bands at residual extrema
    for _round in range(3):
        acc = [0.0] * n
        for fc, g, q in chosen:
            acc = [a + r for a, r in zip(acc, resp_of(fc, g, q))]
        resid = [c - a for c, a in zip(correction, acc)]
        proposals = []
        for k in range(1, n - 1):
            r = resid[k]
            if abs(r) < 0.3:
                continue
            if not ((r <= resid[k - 1] and r <= resid[k + 1]) or
                    (r >= resid[k - 1] and r >= resid[k + 1])):
                continue
            fc = float(round(grid[k]))      # integer Hz - the value the plan writes
            if any(abs(math.log2(fc / c[0])) < MIN_SEP for c in chosen):
                continue
            if any(abs(math.log2(fc / c[0])) < MIN_SEP for c in proposals):
                continue
            if r > 0:
                if fc > boost_hi:
                    continue                # no lift above the region (owner rule)
                # lifts stay broad (owner rule), snapped to 0.1 slots FLOOR-first so the
                # value lift_ok checks is byte-for-byte the value the plan writes
                g0 = math.floor(min(r, MAX_BOOST) * 10.0) / 10.0
                q0 = 1.0
                if g0 <= 0.05:
                    continue
            else:
                g0 = round(max(r, MAX_CUT), 1)
                # Q from the feature's own width at half depth: cuts follow the data
                half = abs(r) / 2
                j = k
                while j > 0 and abs(resid[j - 1]) >= half:
                    j -= 1
                m = k
                while m < n - 1 and abs(resid[m + 1]) >= half:
                    m += 1
                width = math.log(grid[m] / grid[j]) / math.log(2) if m > j else 0.0
                q0 = 4.0 if width <= 0 else max(0.7, min(4.0, 1.0 / width))
            proposals.append([fc, g0, q0])
        if not proposals:
            break
        progressed = False
        for prop in proposals:
            if len(chosen) >= nmax:
                break
            trial = [list(c) for c in chosen] + [list(prop)]
            if not lift_ok(trial):
                continue
            trial = descend(trial)
            trial = [t for t in trial if abs(t[1]) >= 0.35]
            s = total_sse(trial)
            if s < best - 1e-6:
                chosen, best = trial, s
                progressed = True
        if not progressed:
            break

    final = math.sqrt(best / n)
    initial = math.sqrt(sse(correction) / n)
    if not chosen:
        return []                    # nothing worth placing (system trim can be ~zero)
    if final >= initial:
        raise SystemExit(f'fit error: EQ would not improve this path ({initial:.2f} -> {final:.2f})')
    chosen.sort(key=lambda c: c[0])
    return chosen


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--meas', default=os.path.join(ROOT, 'measurements', 'run-20261020-1925'))
    ap.add_argument('--out', default=os.path.join(HERE, 'tune-plan-dense.json'))
    ap.add_argument('--idsp', default=os.path.join(ROOT, 'web', '3-rnb-dense-2026-10-21.idsp'))
    # --pin-gains: hold every output gain at BASE_GAIN - the gains the car had DURING the
    # sweep (verified against evidence\\tune-before-20261020-202655.json). The system level
    # then equals the level of the car as measured - tune 2's loudness platform - by
    # construction, instead of letting the stage-2 deltas re-balance the drivers.
    ap.add_argument('--pin-gains', action='store_true')
    # --trim-lo: lower edge of the stage-3 system trim in Hz (default TRIM_LO = 80).
    # Raising it to 300 keeps the stock cabin-gain hump (the voicing the owner listens
    # for) while still fixing the 300 Hz-12 kHz energy-sum overshoot.
    ap.add_argument('--trim-lo', type=float, default=TRIM_LO)
    # --bass-hz: below this frequency the car's OWN smoothed shape is the target -
    # the cabin hump is the voicing the owner listens for, not an error to remove.
    # Only fine structure (narrow ripple over the 1/3-octave trend) is corrected there;
    # from bass-hz up the house target rules as before. This is what makes
    # --pin-gains hold on the CAR's spectrum, not just in the gain table.
    ap.add_argument('--bass-hz', type=float, default=0.0)
    # --max-bands: per-path stage-1 band budget (default MAX_BANDS = 28). For a build
    # that must leave room for the 12 shared stage-3 trim bands under the hardware's
    # 31-band-per-output ceiling, pass 19 -> 19 + 12 = 31 exactly.
    ap.add_argument('--max-bands', type=int, default=MAX_BANDS)
    # --fill: post-trim fill (see module docstring). Off by default so a plain run still
    # reproduces the verified builds bit for bit.
    ap.add_argument('--fill', action='store_true',
                    help='post-trim fill: seed stage-1+trim bands, chase the adjusted '
                         'target until each path reaches 31 bands or nothing improves')
    ap.add_argument('--fill-rounds', type=int, default=4,
                    help='max alternating trim/fill rounds with --fill (default 4 = the '
                         'verified recipe; higher keeps exploring while rounds still '
                         'strictly improve)')
    ap.add_argument('--sys-prune', action='store_true',
                    help='after the trim/fill fixpoint: drop any band whose REMOVAL '
                         'strictly lowers the SYSTEM residual, then refill freed slots '
                         '(alternating, bounded). Per-path fits can keep bands that cost '
                         'the energy sum more than they buy; this pass is the subtractive '
                         'half at system level. Default off: plain runs unchanged.')
    ap.add_argument('--trim-deepen', action='store_true',
                    help='when the shared trim hits the 31-band ceiling (nmax=0), do not '
                         'stop: RE-FIT the bands already shared by all eight paths '
                         '(count cannot grow - nmax = current shared count - so the '
                         'ceiling cannot block deepening an existing shared cut). '
                         'Same strict system-improvement acceptance as a trim round. '
                         'Default off: plain runs unchanged.')
    ap.add_argument('--sys-add', action='store_true',
                    help='place CUTS on paths that still have slots, scored by the '
                         'SYSTEM residual: the operation per-path fill cannot propose '
                         '(a band that corrects the energy sum can be pointless for the '
                         "path's own fit) and the shared trim cannot reach once any path "
                         'sits at the 31 ceiling. Cuts only, over-target regions only, '
                         '60 Hz-10 kHz, stage-1 ladder and separation, strict '
                         'system-improvement acceptance. Default off.')
    args = ap.parse_args()

    import glob
    files = sorted(glob.glob(os.path.join(args.meas, '*.json')))
    if not files:
        sys.exit(f'no channel files in {args.meas}')

    plan = {'source': os.path.abspath(args.meas), 'target': TARGET, 'paths': {}, 'skipped': []}
    fit_report = []
    meas_full = {}          # path -> {hz: dbfs} over the FULL measurement grid
    per_grid, per_sm = {}, {}   # path -> trusted-band grid + its (artefact-bridged) curve
    per_wide = {}               # path -> 1/3-oct trend (only when --bass-hz is on)

    for fp in files:
        rec = json.load(open(fp, encoding='utf-8-sig'))
        name = rec['channel'].strip().lower()
        spec = CHANNELS.get(name)
        if not spec:
            plan['skipped'].append(name)
            continue
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
        meas_full[spec['path']] = {round(f, 3): d for f, d in pts}
        lo, hi = spec['lo'], spec['hi']
        pts = [(f, d) for f, d in pts if lo * 0.95 <= f <= min(hi, 20000) * 1.05]
        sm = ft.smooth(pts, 1 / 6.0)
        if spec['path'] in (4, 5):
            sm = bridge_artefact([f for f, _ in pts], sm)
        per_grid[spec['path']] = grid_of(pts)
        per_sm[spec['path']] = list(sm)

        corr = [ft.interp_db(TARGET, f) - d for (f, _), d in zip(pts, sm)]
        if args.bass_hz > 0:
            # below bass-hz: target = the measurement's own 1/3-octave trend, so corr
            # is ripple-over-trend only - the hump survives, the wiggle does not
            wide_pairs = [(f, d) for (f, _), d in zip(pts, ft.smooth(pts, 1 / 3.0))]
            per_wide[spec['path']] = wide_pairs
            for k, (f, _) in enumerate(pts):
                if f < args.bass_hz:
                    corr[k] = ft.interp_db(wide_pairs, f) - sm[k]
        mean = sum(corr) / len(corr)
        corr = [c - mean for c in corr]

        grid = [f for f, _ in pts]
        boost_hi = 12000.0 if spec['path'] < 4 else 2000.0
        nmax = min(args.max_bands, len(grid))
        bands = fit_dense(grid, corr, nmax, boost_hi)

        pred = list(sm)
        eqsum = [0.0] * len(grid)
        for fci, g2, q in bands:
            for k, f in enumerate(grid):
                r = ft.peq_response_db(f, fci, g2, q)
                pred[k] += r
                eqsum[k] += r
        hlo, hhi = spec['home']
        home_idx = [k for k, f in enumerate(grid) if hlo <= f <= hhi] or list(range(len(grid)))
        home_vals = sorted(ft.interp_db(TARGET, grid[k]) - pred[k] for k in home_idx)
        delta = home_vals[len(home_vals) // 2]

        rms_before = math.sqrt(sum(c * c for c in corr) / len(corr))
        rms_after = math.sqrt(sum((c - e) ** 2 for c, e in zip(corr, eqsum)) / len(corr))
        err = [c - e for c, e in zip(corr, eqsum)]

        path = spec['path']
        plan['paths'][str(path)] = {
            'channel': rec['channel'], 'role': spec['role'],
            'bands': [{'fc': int(round(fc)), 'gain': round(g2, 1), 'q': round(q, 1),
                       'type': 0, 'enable': True} for fc, g2, q in bands],
            'gainDeltaDb': round(delta, 1),
            'currentGainDb': None, 'delayMs': BASE_DELAY[path],
            'bandRangeHz': [lo, hi],
            'rmsBeforeDb': round(rms_before, 2), 'rmsAfterDb': round(rms_after, 2),
            'rippleAfterDb': round(max(err) - min(err), 2),
        }
        fit_report.append((path, rec['channel'], len(bands), delta,
                           rms_before, rms_after))

    if not plan['paths']:
        sys.exit('no paths fitted')

    if args.pin_gains:
        print('      gains PINNED to BASE_GAIN (the sweep-time device gains = tune 2): '
              + ', '.join(f'{BASE_GAIN[k]:.1f}' for k in sorted(BASE_GAIN)))
        for p in plan['paths'].values():
            p['gainDeltaDb'] = 0.0
    else:
        worst = max(p['gainDeltaDb'] for p in plan['paths'].values())
        if worst > 0:
            for p in plan['paths'].values():
                p['gainDeltaDb'] = round(p['gainDeltaDb'] - worst, 1)
    for key, p in plan['paths'].items():
        path = int(key)
        p['newGainDb'] = round(max(-40.0, min(0.0, BASE_GAIN[path] + p['gainDeltaDb'])), 1)

    # -------------------------------------------------- stage 3: system-level trim
    # Each path is fitted ALONE against the same target, so wherever two or three paths
    # play together the energy sum lands ~10*log10(k) dB over the target - invisible to
    # every per-path fit, and exactly where the +5..+9 dB overshoot at 1.25-8 kHz lives.
    # Shifting EVERY path by one curve c(f) scales the energy sum by exactly c(f), so fit
    # c once, legally, and append the same bands to all eight paths.
    # (Chain-order unknown below an HP corner matters only where a path's share of the
    # sum is already negligible, so the identity holds where contributors actually play.)
    trim_info = None
    sys_rms_before = sys_rms_after = None
    if len(plan['paths']) == 8 and all(p in meas_full for p in range(8)):
        freq = sorted(next(iter(meas_full.values())).keys())
        tgt = [ft.interp_db(TARGET, f) for f in freq]

        def bands_of(p):
            return [(b['fc'], b['gain'], b['q']) for b in plan['paths'][str(p)]['bands']]

        def sys_curve(bands_by_path, shift=None, skip_shift=None):
            curves = []
            for p in range(8):
                level = plan['paths'][str(p)]['newGainDb'] - BASE_GAIN[p]
                eq = [0.0] * len(freq)
                for fc, g, q in bands_by_path[p]:
                    eq = [e + r for e, r in zip(eq, peq_grid(freq, fc, g, q))]
                cur = [meas_full[p][f] + level + e for f, e in zip(freq, eq)]
                if shift is not None and p != skip_shift:
                    cur = [c + s for c, s in zip(cur, shift)]
                curves.append(cur)
            lin = [[10.0 ** (c / 20.0) for c in cur] for cur in curves]
            return [10.0 * math.log10(sum(x * x for x in row)) for row in zip(*lin)]

        def resid_rms(sys):
            off = idsp.at(freq, sys, 300.0)
            idx = [i for i, f in enumerate(freq) if 60.0 <= f <= 10000.0]
            return math.sqrt(sum((sys[i] - off - tgt[i]) ** 2 for i in idx) / len(idx))

        def corr_for(p, clip_off):
            """The per-path correction on the target it ACTUALLY chases: the house target
            (or the bass-hz local trend below that edge), mean-centred so level stays the
            output gain's job, plus the common-shift offset the shared trim carries."""
            grid = per_grid[p]
            # below bass-hz the local trend (ripple only), the house target above -
            # otherwise a ripple-only fit is graded against hump removal it was
            # deliberately told not to do (v2 refusal)
            if args.bass_hz > 0 and p in per_wide:
                cp = [ft.interp_db(per_wide[p], f) - d if f < args.bass_hz
                      else ft.interp_db(TARGET, f) - d
                      for f, d in zip(grid, per_sm[p])]
            else:
                cp = [ft.interp_db(TARGET, f) - d for f, d in zip(grid, per_sm[p])]
            m = sum(cp) / len(cp)
            return [c - m + c0 for c, c0 in zip(cp, clip_off)]

        def fill_paths(clip):
            """One legal fill pass over all eight paths (module docstring, --fill): seeds
            every path's existing bands, spends only the lift headroom the seeded stack
            has not already used, and stops at the 31-band ceiling."""
            for p in range(8):
                grid = per_grid[p]
                cc = [clip[f] for f in grid]   # per_grid keys are meas_full keys
                seeds = [[float(b['fc']), float(b['gain']), float(b['q'])]
                         for b in plan['paths'][str(p)]['bands']]
                if len(seeds) >= 31:
                    continue
                lift0 = [0.0] * len(LGRID)
                for fci, gi, qi in seeds:
                    if gi > 0:
                        lift0 = [a + r for a, r in zip(lift0, peq_grid(LGRID, fci, gi, qi))]
                budget4 = [max(0.0, MAX_LIFT - v) for v in lift0]
                bhi = 12000.0 if p < 4 else 2000.0
                try:
                    full = fit_dense(grid, corr_for(p, cc), 31, bhi,
                                     headroom=budget4, seeds=seeds)
                except SystemExit:
                    full = seeds        # would not improve: keep what stage 1+trim found
                if full != seeds:
                    plan['paths'][str(p)]['bands'] = [
                        {'fc': int(round(fc)), 'gain': round(g, 1), 'q': round(q, 1),
                         'type': 0, 'enable': True} for fc, g, q in full]
                print(f'      fill path {p}: {len(seeds)} -> {len(full)} bands '
                      f'({len(full) - len(seeds):+d})')

        cur = {p: bands_of(p) for p in range(8)}
        sys0 = sys_curve(cur)
        sys_rms_before = resid_rms(sys0)
        off0 = idsp.at(freq, sys0, 300.0)
        corr = [tgt[i] + off0 - sys0[i] for i in range(len(freq))]

        # The identity the whole stage rests on, proved numerically + its negative
        # control: shifting all eight paths must move the sum by exactly c(f); shifting
        # seven (one path omitted) must NOT - if the control ever passes, the check is
        # blind and stage 3 must not be trusted.
        full = sys_curve(cur, shift=corr)
        ident = max(abs(full[i] - (sys0[i] + corr[i])) for i in range(len(freq)))
        partial = sys_curve(cur, shift=corr, skip_shift=6)   # path 7's share unshifted
        ctrl = max(abs(partial[i] - (sys0[i] + corr[i])) for i in range(len(freq)))
        print(f'      system-shift identity: max |dev| {ident:.2e} dB  '
              f'{"PASS" if ident < 1e-9 else "FAIL"};  negative control (path 7 removed) '
              f'dev {ctrl:.2f} dB  {"PASS (detected)" if ctrl > 0.5 else "FAIL (blind)"}')
        if ident >= 1e-9:
            sys.exit('system-shift identity broken - stage 3 maths is wrong, refusing')
        if ctrl <= 0.5:
            sys.exit('negative control failed - the identity check above proves nothing')

        # ---- stage 3 rounds: shared trim, alternating with the fill when --fill is on.
        # Round 1 is the original single pass (and identical with or without --fill).
        # Each further round RE-DERIVES the ideal common shift from the CURRENT system,
        # so a trim that ran before a fill cannot permanently miss what the fill revealed
        # (probe 2026-10-22: round 2 found 7 more strictly-improving shared cuts,
        # system 2.65 -> 2.27 dB, that round 1 could not see). Every round enforces the
        # 31-band ceiling through nmax; the loop stops at the first no-improvement round.
        trim_total = 0
        max_rounds = (args.fill_rounds if args.fill else 1)
        for rnd in range(max_rounds):
            cur = {p: bands_of(p) for p in range(8)}
            sysC = sys_curve(cur)
            offC = idsp.at(freq, sysC, 300.0)
            corr = [tgt[i] + offC - sysC[i] for i in range(len(freq))]
            before_r = resid_rms(sysC)
            gidx = [i for i, f in enumerate(freq) if args.trim_lo <= f <= TRIM_HI]
            gf = [freq[i] for i in gidx]
            gc = [corr[i] for i in gidx]
            # common lift budget = tightest per-path headroom left by stage 1/2 boosts,
            # measured over LGRID (the dense cap grid), not over gf - the cap is checked
            # between the 1/6-oct points too
            budget = [MAX_LIFT] * len(LGRID)
            for p in range(8):
                lift = [0.0] * len(LGRID)
                for fc, g, q in bands_of(p):
                    if g > 0:
                        lift = [a + r for a, r in zip(lift, peq_grid(LGRID, fc, g, q))]
                budget = [min(b, MAX_LIFT - l) for b, l in zip(budget, lift)]
            maxc = max(len(bands_of(p)) for p in range(8))
            nmax = min(MAX_TRIM_BANDS, 31 - maxc)
            trim = []
            seeds = None
            shared_set = None
            if nmax == 0 and args.trim_deepen:
                # Ceiling reached: the shared trim does not stop here - it RE-FITS the
                # bands already shared by every path (identical fc/gain/q on all eight).
                # nmax is set to their count, so the result can only reshape or drop,
                # never grow past 31: deepening the cut the plateau needs costs no slot.
                shared = None
                for p in range(8):
                    ids = {tuple(b) for b in bands_of(p)}
                    shared = ids if shared is None else (shared & ids)
                shared = sorted(shared or [])
                if shared:
                    seeds = [list(t) for t in shared]
                    shared_set = set(shared)
                    nmax = len(shared)
                    print(f'      stage-3 round {rnd + 1}: ceiling reached - re-fitting '
                          f'{nmax} existing shared bands in place')
            if nmax > 0:
                try:
                    if seeds is not None:
                        trim = fit_dense(gf, gc, nmax, 2000.0, headroom=budget,
                                         seeds=seeds)
                    else:
                        trim = fit_dense(gf, gc, nmax, 2000.0, headroom=budget)
                except SystemExit:
                    trim = []        # fit refused (would not improve): no trim this round
            trim_r = [{'fc': int(round(fc)), 'gain': round(g, 1), 'q': round(q, 1),
                       'type': 0, 'enable': True} for fc, g, q in trim]
            trim_r = [b for b in trim_r if b['gain'] != 0.0]
            if not trim_r:
                break
            trial = {}
            for p in range(8):
                base = cur[p]
                if shared_set is not None:
                    base = [b for b in base if tuple(b) not in shared_set]
                trial[p] = base + [(b['fc'], b['gain'], b['q']) for b in trim_r]
            sys1 = sys_curve(trial)
            sys_rms_after = resid_rms(sys1)
            if sys_rms_after >= before_r - 1e-9:
                break
            for p in range(8):
                if shared_set is not None:
                    kept = [b for b in plan['paths'][str(p)]['bands']
                            if (b['fc'], b['gain'], b['q']) not in shared_set]
                    plan['paths'][str(p)]['bands'] = kept + copy.deepcopy(trim_r)
                else:
                    plan['paths'][str(p)]['bands'].extend(copy.deepcopy(trim_r))
            if shared_set is None:
                trim_total += len(trim_r)
            what = (f'{len(trim_r)} shared bands refitted in place'
                    if shared_set is not None else f'+{len(trim_r)} shared bands/path')
            where = ('count preserved' if shared_set is not None
                     else f'ceiling headroom {31 - maxc - len(trim_r)} bands/path')
            print(f'      stage-3 round {rnd + 1}: {what}, '
                  f'system rms {before_r:.2f} -> {sys_rms_after:.2f} dB  ({where})')
            if args.fill:
                clip_r = {f: (corr[i] if args.trim_lo <= f <= TRIM_HI else 0.0)
                          for i, f in enumerate(freq)}
                fill_paths(clip_r)

        # ---- system add (opt-in): place CUTS on ONE path at a time (the path with
        # slots that owns the energy there), scored by the SYSTEM residual - the
        # operation per-path fill cannot propose and the shared trim cannot reach once
        # any path sits at the 31 ceiling. Search uses a fast incremental evaluator
        # (rebuild only the trial path's contribution); every ACCEPTED band is verified
        # against the exact same sys_curve/resid_rms the trim rounds use, strict 0.01 dB
        # or it does not ship, with a revert guard if rounding gives any of it back.
        # Cuts only, over-target regions only, 60 Hz-10 kHz, stage-1 cut Q subset and
        # 1/12-oct lattice/separation. No boost policy invented here. Default off.
        sys_added = 0
        if args.sys_add:
            SYS_LO, SYS_HI = 60.0, 10000.0
            qs_add = [1.0, 2.0, 4.0]

            def sys_e(bands_by_path):
                return resid_rms(sys_curve(bands_by_path))

            def lin_state():
                """per-path linear arrays + squared total, exactly as sys_curve builds
                them, for incremental single-path trials."""
                lin = {}
                for p in range(8):
                    level = plan['paths'][str(p)]['newGainDb'] - BASE_GAIN[p]
                    eq = [0.0] * len(freq)
                    for fc, g, q in bands_of(p):
                        eq = [e + r for e, r in zip(eq, peq_grid(freq, fc, g, q))]
                    lin[p] = [10.0 ** ((meas_full[p][f] + level + e) / 20.0)
                              for f, e in zip(freq, eq)]
                tot2 = [sum(lin[p][i] ** 2 for p in range(8)) for i in range(len(freq))]
                return lin, tot2

            widx = [i for i, f in enumerate(freq) if 60.0 <= f <= 10000.0]

            def fast_rms(lin, tot2, p, fc, g, q):
                r = peq_grid(freq, fc, g, q)
                s = [math.log10(tot2[i] - lin[p][i] ** 2
                                + (lin[p][i] * 10.0 ** (r[i] / 20.0)) ** 2) * 10.0
                     for i in range(len(freq))]
                off = idsp.at(freq, s, 300.0)
                return math.sqrt(sum((s[i] - off - tgt[i]) ** 2 for i in widx)
                                 / len(widx))

            free = {p: 31 - len(bands_of(p)) for p in range(8)}
            for it in range(14):
                open_paths = [p for p in range(8) if free[p] > 0]
                if not open_paths:
                    break
                cur = {p: bands_of(p) for p in range(8)}
                base = sys_e(cur)
                s = sys_curve(cur)
                off = idsp.at(freq, s, 300.0)
                err = [tgt[i] + off - s[i] for i in range(len(freq))]
                peaks = []
                for i in range(1, len(freq) - 1):
                    f = freq[i]
                    if SYS_LO <= f <= SYS_HI and err[i] >= 0.6 \
                            and err[i] >= err[i - 1] and err[i] >= err[i + 1]:
                        peaks.append(i)
                peaks.sort(key=lambda i: -err[i])
                peaks = peaks[:6]
                if not peaks:
                    print(f'      sys-add it{it + 1}: no over-target peak >= 0.6 dB in '
                          f'60 Hz-10 kHz - stopping')
                    break
                lin, tot2 = lin_state()
                best = None
                seen = []
                for p in open_paths:
                    have = [b[0] for b in bands_of(p)]
                    cand_fcs = set()
                    for i in peaks:
                        k0 = int(math.floor(math.log2(freq[i] / 20.0) * 12))
                        for k in range(k0 - 2, k0 + 3):
                            cand_fcs.add(20.0 * 2.0 ** (k / 12.0))
                    for fc in sorted(cand_fcs):
                        if not (SYS_LO <= fc <= SYS_HI):
                            continue
                        if any(abs(math.log2(fc / h)) < 0.0833 for h in have):
                            continue
                        j = min(range(len(freq)),
                                key=lambda k: abs(math.log(freq[k] / fc)))
                        want = err[j]
                        if want < 0.6:
                            continue          # not over target here: cuts only
                        g0 = -min(12.0, max(0.5, want))
                        for q in qs_add:
                            for g in (g0, g0 * 0.5):
                                fr = fast_rms(lin, tot2, p, fc, g, q)
                                seen.append((fr, p, fc, g, q))
                                if fr < base - 0.01 and (best is None or fr < best[0]):
                                    best = (fr, p, fc, g, q)
                if best is None:
                    seen.sort()
                    top = ', '.join(f'p{p} {fc:.0f}Hz {g:+.1f} Q{q} -> {fr:.4f}'
                                    for fr, p, fc, g, q in seen[:5])
                    print(f'      sys-add it{it + 1}: nothing clears base {base:.4f} - '
                          f'top5 [{top}]')
                    break
                fr, p, fc, g, q = best
                # exact verification of the ONE candidate the fast search picked
                trial = {pp: (bands_of(pp) + [(fc, g, q)] if pp == p else bands_of(pp))
                         for pp in range(8)}
                r = sys_e(trial)
                if r >= base - 0.01:
                    print(f'      sys-add it{it + 1}: fast pick {fc:.0f} Hz failed exact '
                          f'check ({r:.4f} vs base {base:.4f}) - stopping')
                    break
                plan['paths'][str(p)]['bands'].append(
                    {'fc': int(round(fc)), 'gain': round(g, 1), 'q': round(q, 1),
                     'type': 0, 'enable': True})
                plan['paths'][str(p)]['bands'].sort(key=lambda b: b['fc'])
                actual = sys_e({pp: bands_of(pp) for pp in range(8)})
                if actual >= base - 0.005:      # rounding gave the gain back: revert
                    plan['paths'][str(p)]['bands'] = [
                        b for b in plan['paths'][str(p)]['bands']
                        if not (b['fc'] == int(round(fc)) and b['q'] == round(q, 1)
                                and abs(b['gain'] - round(g, 1)) < 0.051)]
                    print(f'      sys-add it{it + 1}: rounding guard reverted '
                          f'{fc:.0f} Hz (actual {actual:.4f}) - stopping')
                    break
                free[p] -= 1
                sys_added += 1
                print(f'      sys-add it{it + 1}: path {p} cut {fc:.0f} Hz {g:+.1f} dB '
                      f'Q{q} -> system {actual:.4f} dB (base was {base:.4f})')
            if sys_added:
                print(f'      sys-add total: {sys_added} cuts placed, system '
                      f'{sys_e({p: bands_of(p) for p in range(8)}):.4f} dB')

        # ---- system prune (opt-in): the SUBTRACTIVE half at system level. stage 1,
        # the shared trim and the fill all ADD bands against per-path targets; a band
        # can earn its per-path fit while costing the energy sum (two paths each
        # fitted to the target land 10*log10(k) dB over it where they overlap - the
        # overshoot stage 3 chases, and the shared trim cannot reach it once any one
        # path sits at the 31-band ceiling). Drop test: remove one band, recompute
        # the SYSTEM residual, keep the removal only if it strictly lowers it; scan
        # to fixpoint, refill freed slots against the fresh adjusted target, repeat
        # bounded. Removals are monotone (rms only falls) so the scan terminates.
        prune_removed = prune_added = 0
        if args.sys_prune:

            def sys_rms_now():
                return resid_rms(sys_curve({p: bands_of(p) for p in range(8)}))

            def do_prune(cyc):
                nonlocal prune_removed
                base = sys_rms_now()
                removed = 0
                while True:
                    hit = False
                    for p in range(8):
                        blist = plan['paths'][str(p)]['bands']
                        i = 0
                        while i < len(blist):
                            popped = blist.pop(i)
                            r = sys_rms_now()
                            if r < base - 1e-6:
                                base = r
                                removed += 1
                                prune_removed += 1
                                hit = True
                                print(f'      sys-prune {cyc}: path {p} drop '
                                      f"{popped['fc']} Hz {popped['gain']:+.1f} dB -> "
                                      f"system {r:.4f} dB")
                            else:
                                blist.insert(i, popped)
                                i += 1
                    if not hit:
                        return removed

            for cycle in range(3):
                removed = do_prune(f'c{cycle + 1}')
                if not args.fill:
                    break
                cur = {p: bands_of(p) for p in range(8)}
                sysC = sys_curve(cur)
                offC = idsp.at(freq, sysC, 300.0)
                corr = [tgt[i] + offC - sysC[i] for i in range(len(freq))]
                clip_r = {f: (corr[i] if args.trim_lo <= f <= TRIM_HI else 0.0)
                          for i, f in enumerate(freq)}
                before_fill = sum(len(plan['paths'][str(p)]['bands']) for p in range(8))
                fill_paths(clip_r)
                after_fill = sum(len(plan['paths'][str(p)]['bands']) for p in range(8))
                prune_added += after_fill - before_fill
                if removed == 0 and after_fill == before_fill:
                    break
            if args.fill:
                do_prune('final')   # never end on a refill the system would object to
            print(f'      sys-prune total: -{prune_removed} bands, '
                  f'+{prune_added} refilled, system {sys_rms_now():.4f} dB')

        # trim_info reports the ACTUAL system (every accepted round + fill included)
        trim_rms_now = resid_rms(sys_curve({p: bands_of(p) for p in range(8)}))
        trim_info = (trim_total, sys_rms_before,
                     trim_rms_now if trim_total else sys_rms_before)

        # Re-grade every path against the target it ACTUALLY chases now: all eight paths
        # carry the trim, so the effective per-path target is corr + c (clipped to where
        # trim was allowed to act). Compare EQ-on vs NO-EQ on THAT target - a path where
        # EQ no longer beats no-EQ means stage 3 turned against that driver: refuse.
        trimmed = trim_total > 0
        c_clip = {f: (corr[i] if (trimmed and args.trim_lo <= f <= TRIM_HI) else 0.0)
                  for i, f in enumerate(freq)}
        for p in range(8):
            grid = per_grid[p]
            cc = [c_clip[f] for f in grid]           # per_grid keys are meas_full keys
            corr_p = corr_for(p, cc)                 # adjusted target (see corr_for)
            eq = [0.0] * len(grid)
            for b in plan['paths'][str(p)]['bands']:
                eq = [e + r for e, r in zip(eq, peq_grid(grid, b['fc'], b['gain'], b['q']))]
            rms_a = math.sqrt(sum((c - e) ** 2 for c, e in zip(corr_p, eq)) / len(corr_p))
            rms_0 = math.sqrt(sum(c * c for c in corr_p) / len(corr_p))  # same target, no EQ
            if rms_a >= rms_0:
                sys.exit(f'path {p}: final EQ does not beat no-EQ on its adjusted target '
                         f'({rms_a:.2f} >= {rms_0:.2f}) - trim_info={trim_info}')
            # keep the table coherent: both columns on the SAME (adjusted) target
            plan['paths'][str(p)]['rmsBeforeDb'] = round(rms_0, 2)
            plan['paths'][str(p)]['rmsAfterDb'] = round(rms_a, 2)
            err = [c - e for c, e in zip(corr_p, eq)]
            plan['paths'][str(p)]['rippleAfterDb'] = round(max(err) - min(err), 2)

    os.makedirs(os.path.dirname(args.out) or '.', exist_ok=True)
    with open(args.out, 'w', encoding='utf-8') as fh:
        json.dump(plan, fh, indent=2)

    # ---------------------------------------------------------------- build the .idsp
    tune_plan = {}
    for key, p in plan['paths'].items():
        path = int(key)
        tune_plan[path] = {
            'gain': p['newGainDb'], 'delay': BASE_DELAY[path], 'hp': bs.CUR_HP[path],
            'bands': [{'Fc': float(b['fc']), 'Gain': float(b['gain']), 'Q': float(b['q'])}
                      for b in p['bands']],
        }
    template = os.path.join(bs.ROOT, 'web', 'bor8f3.idsp')
    bs.build_single(template, tune_plan, MASTER, args.idsp, False)

    # ---------------------------------------------------------------- transcription check
    # Re-read the written file and compare band-for-band against the plan. A comparator that
    # has never been seen to fail is not evidence, so the check is deliberately run against a
    # perturbed copy too.
    def compare(doc):
        bad = []
        mem = doc['memories'][0]
        for o in mem['outputs'][:8]:
            p = o['index']
            want = plan['paths'].get(str(p))
            got = [{'fc': b['Fc'], 'gain': b['Gain'], 'q': b['Q']}
                   for b in idsp.active_bands(o)]
            if want is None:
                if got:
                    bad.append(f'path {p}: spare output has {len(got)} bands')
                continue
            if abs(o['gain'] - want['newGainDb']) > 0.05:
                bad.append(f'path {p}: gain {o["gain"]} != plan {want["newGainDb"]}')
            if len(got) != len(want['bands']):
                bad.append(f'path {p}: {len(got)} bands != plan {len(want["bands"])}')
                continue
            for i, (g, w) in enumerate(zip(got, want['bands'])):
                if abs(g['fc'] - w['fc']) > 1 or abs(g['gain'] - w['gain']) > 0.05 \
                        or abs(g['q'] - w['q']) > 0.05:
                    bad.append(f'path {p} band {i}: {g} != {w}')
        return bad

    doc = idsp.load(args.idsp)
    # Single-memory files since to-single.py (STATE 2026-10-22 14:40): compare only the
    # memories that exist. The old fixed (1, 2, 3) loop crashed on a one-memory file
    # AFTER it was written, which silently skipped this check on every build since.
    for mi in range(1, len(doc['memories'])):
        if json.dumps(doc['memories'][mi]['outputs'], sort_keys=True) != \
           json.dumps(doc['memories'][0]['outputs'], sort_keys=True):
            print(f'FAIL  memory {mi + 1} differs from memory 1')
    bad = compare(doc)
    # negative control: perturb one band and demand the comparator notices
    doc2 = idsp.load(args.idsp)
    o = doc2['memories'][0]['outputs'][0]
    for b in idsp.active_bands(o):
        b['Gain'] = round(b['Gain'] + 0.5, 2)
        break
    neg = compare(doc2)
    print(f'      transcription check: {"PASS" if not bad else "FAIL"}'
          f'  ({len(bad)} mismatches)')
    print(f'      negative control:    {"PASS (perturbation detected)" if neg else "FAIL (comparator is blind)"}')
    if bad:
        for line in bad[:10]:
            print('   ', line)
        sys.exit(1)
    if not neg:
        sys.exit('negative control failed - the check above proves nothing')

    # ---------------------------------------------------------------- report
    print()
    if trim_info is not None:
        ntrim, rb, ra = trim_info
        print(f'stage 3 system trim: {ntrim} shared bands/path, system residual rms '
              f'{rb:.2f} -> {ra:.2f} dB (60 Hz-10 kHz, aligned at 300 Hz)')
        if ntrim == 0:
            print('  (no legal trim candidate improved the sum - per-path fits stand)')
    print(f'{"path":>4} {"channel":<12} {"bands":>5} {"used/31":>8} {"fit rms":>16} {"base gain":>10} {"delta":>7}')
    for path in sorted(plan['paths'], key=int):
        p = plan['paths'][path]
        n = len(p['bands'])
        d = p['gainDeltaDb']   # after the common shift
        print(f'{path:>4} {p["channel"]:<12} {n:>5} {n}/31    '
              f'{p["rmsBeforeDb"]:>6.1f} -> {p["rmsAfterDb"]:<5.1f} dB  '
              f'{BASE_GAIN[int(path)]:>10.1f} {d:>+7.1f}')
    total = sum(len(p['bands']) for p in plan['paths'].values())
    print(f'\n{total} bands across 8 paths (tune 1 used 57; ceiling here is 8x31=248)')
    print(f'wrote {os.path.relpath(args.out, ROOT)}')
    print(f'wrote {os.path.relpath(args.idsp, ROOT)}')
    print('nothing has been written to the DSP - apply-tune.ps1 or the Tune tab does that, and verifies it.')


if __name__ == '__main__':
    main()
