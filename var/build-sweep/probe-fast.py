"""Probe: does the incremental fast evaluator agree with the exact sys evaluation?

Reproduces the engine's sys_curve/resid_rms/lin_state/fast_rms verbatim against
plan-a1.json and the same measurement sweep, for one sys-add candidate.
"""
import importlib.util
import json
import math
import os

ROOT = r'C:\Users\sdotm\OneDrive\Documents\Default Project\ds18-bridge'
TUNE = os.path.join(ROOT, 'tune')


def load_mod(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    m = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(m)
    return m


ft = load_mod('ft', os.path.join(TUNE, 'fit-tune.py'))
idsp = load_mod('idsp', os.path.join(TUNE, 'idsp.py'))
sp = load_mod('sp', os.path.join(TUNE, 'system-predict.py'))
fd = load_mod('fd', os.path.join(TUNE, 'fit-dense.py'))

BASE_GAIN = {0: -4.6, 1: -7.0, 2: -7.0, 3: -4.0, 4: -3.0, 5: -3.0, 6: -14.0, 7: -5.0}
meas_full = sp.load_meas(os.path.join(ROOT, 'measurements', 'run-20261020-1925'))
plan = json.load(open(os.path.join(ROOT, 'var', 'build-sweep', 'plan-a1.json'),
                      encoding='utf-8'))

freq = sorted(next(iter(meas_full.values())).keys())
tgt = [ft.interp_db(ft.TARGET, f) for f in freq]
peq_grid = fd.peq_grid


def bands_of(p):
    return [(b['fc'], b['gain'], b['q']) for b in plan['paths'][str(p)]['bands']]


def sys_curve(bands_by_path):
    lin = []
    for p in range(8):
        level = plan['paths'][str(p)]['newGainDb'] - BASE_GAIN[p]
        eq = [0.0] * len(freq)
        for fc, g, q in bands_by_path[p]:
            eq = [e + r for e, r in zip(eq, peq_grid(freq, fc, g, q))]
        cur = [meas_full[p][f] + level + e for f, e in zip(freq, eq)]
        lin.append([10.0 ** (c / 20.0) for c in cur])
    return [10.0 * math.log10(sum(x * x for x in row)) for row in zip(*lin)]


def resid_rms(sys):
    off = idsp.at(freq, sys, 300.0)
    idx = [i for i, f in enumerate(freq) if 60.0 <= f <= 10000.0]
    return math.sqrt(sum((sys[i] - off - tgt[i]) ** 2 for i in idx) / len(idx))


def lin_state():
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
    return math.sqrt(sum((s[i] - off - tgt[i]) ** 2 for i in widx) / len(widx))


cur = {p: bands_of(p) for p in range(8)}
base = resid_rms(sys_curve(cur))
print('base exact                = %.4f' % base)

# the debug run's top candidate
cand = (7, 1140.0, -1.9, 1.0)
lin, tot2 = lin_state()
fr = fast_rms(lin, tot2, *cand)
print('fast  p7 1140 -1.9 Q1     = %.4f' % fr)

trial = {p: (bands_of(p) + [cand[1:]] if p == cand[0] else bands_of(p))
         for p in range(8)}
s_trial = sys_curve(trial)
ex = resid_rms(s_trial)
print('exact p7 1140 -1.9 Q1     = %.4f' % ex)

# pointwise curve comparison fast vs exact
r = peq_grid(freq, cand[1], cand[2], cand[3])
s_fast = [math.log10(tot2[i] - lin[cand[0]][i] ** 2
                     + (lin[cand[0]][i] * 10.0 ** (r[i] / 20.0)) ** 2) * 10.0
          for i in range(len(freq))]
diffs = [(abs(a - b), i) for i, (a, b) in enumerate(zip(s_fast, s_trial))]
diffs.sort(reverse=True)
print('max |fast - exact| curve  = %.4f dB at %.1f Hz'
      % (diffs[0][0], freq[diffs[0][1]]))
i = diffs[0][1]
print('at that bin: fast=%.3f exact=%.3f meas7=%.3f eqsum7=?' %
      (s_fast[i], s_trial[i], meas_full[cand[0]][freq[i]]))

# in-window breakdown: which bins make fast's rms differ
offf = idsp.at(freq, s_fast, 300.0)
offe = idsp.at(freq, s_trial, 300.0)
contrib = sorted([(((s_fast[k] - offf - tgt[k]) ** 2
                    - (s_trial[k] - offe - tgt[k]) ** 2), k) for k in widx],
                 key=lambda t: -abs(t[0]))
print('alignment: fast@300=%.3f exact@300=%.3f' % (offf, offe))
print('top in-window rms contributors (fast2-exact2, f, s_fast, s_exact, r, lin7^2, tot2):')
for c, k in contrib[:8]:
    f = freq[k]
    l7 = lin[cand[0]][k] ** 2
    print('  %+.4f @%.0fHz  fast=%.2f exact=%.2f r=%.2f lin7sq=%.3e tot2=%.3e'
          % (c, f, s_fast[k], s_trial[k], r[k], l7, tot2[k]))

# sanity: what does the exact curve look like vs target at the top error bins
off = idsp.at(freq, s_trial, 300.0)
errs = sorted(((s_trial[k] - off - tgt[k], freq[k]) for k in widx),
              key=lambda t: -abs(t[0]))
print('top exact residuals: ' + ', '.join('%+.1f@%.0f' % (e, f) for e, f in errs[:6]))

# does the exact metric respond to p7 cuts AT ALL (bigger, at the actual peak)?
for c2 in [(7, 1010.0, -3.9, 2.0), (7, 1010.0, -6.0, 4.0), (0, 2281.0, -4.0, 2.0)]:
    t2 = {p: (bands_of(p) + [c2[1:]] if p == c2[0] else bands_of(p))
          for p in range(8)}
    print('exact p%d %.0fHz %+.1f Q%.1f -> rms %.4f'
          % (c2[0], c2[1], c2[2], c2[3], resid_rms(sys_curve(t2))))
# path7 share at the +3.9 peak bin
k1016 = min(range(len(freq)), key=lambda k: abs(math.log(freq[k] / 1016.0)))
shares = [lin[p][k1016] ** 2 / tot2[k1016] for p in range(8)]
print('p7 energy share @1016Hz = %.0f%%; per-path: %s'
      % (shares[7] * 100, ' '.join('%d:%.0f%%' % (p, shares[p] * 100) for p in range(8))))
