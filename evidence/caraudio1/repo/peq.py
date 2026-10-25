"""
PEQ fitting engine: work out which parametric filters best correct a measured
response toward a target, within the limits the DSP actually allows.

The DSP's bands are peaking-only (Fc / Gain / Q), so the biquad is the standard
RBJ "peaking EQ" from the Audio EQ Cookbook. Filter shapes are assumed to be
RBJ; the DSP's exact coefficients are not published.

Nothing here measures anything - it fits filters to a response curve you already
have (e.g. a REW measurement export).
"""
import numpy as np
from scipy.optimize import least_squares

FS = 48000.0
F_MIN, F_MAX = 20.0, 20000.0
Q_MIN, Q_MAX = 0.30, 20.0
G_MIN, G_MAX = -24.0, 24.0


# --------------------------------------------------------------- filter maths
def peq_response(f, f0, gain_db, q, fs=FS):
    """Magnitude response (dB) of one RBJ peaking filter at frequencies f."""
    f = np.asarray(f, dtype=float)
    A = 10.0 ** (gain_db / 40.0)
    w0 = 2.0 * np.pi * f0 / fs
    alpha = np.sin(w0) / (2.0 * q)
    b0 = 1.0 + alpha * A
    b1 = -2.0 * np.cos(w0)
    b2 = 1.0 - alpha * A
    a0 = 1.0 + alpha / A
    a1 = -2.0 * np.cos(w0)
    a2 = 1.0 - alpha / A
    w = 2.0 * np.pi * f / fs
    z1, z2 = np.exp(-1j * w), np.exp(-2j * w)
    H = (b0 + b1 * z1 + b2 * z2) / (a0 + a1 * z1 + a2 * z2)
    return 20.0 * np.log10(np.abs(H) + 1e-30)


def bank_response(f, params, fs=FS):
    """Combined response (dB) of a bank of peaking filters.

    params: flat array, groups of 3 -> [log10(f0), gain_db, log10(q)]
    """
    f = np.asarray(f, dtype=float)
    total = np.zeros_like(f)
    for i in range(0, len(params), 3):
        total += peq_response(f, 10.0 ** params[i], params[i + 1],
                              10.0 ** params[i + 2], fs)
    return total


def unpack(params):
    return [(10.0 ** params[i], params[i + 1], 10.0 ** params[i + 2])
            for i in range(0, len(params), 3)]


def smooth_log(freqs, db, octave_fraction=6.0):
    """Smooth a response over roughly 1/octave_fraction-octave windows.

    Measurements are noisy; fitting the raw wiggle produces filters that chase
    measurement artefacts instead of audible problems.
    """
    freqs = np.asarray(freqs, float)
    db = np.asarray(db, float)
    out = np.empty_like(db)
    half = 2.0 ** (1.0 / (2.0 * octave_fraction))   # window half-width factor
    for i, f in enumerate(freqs):
        m = (freqs >= f / half) & (freqs <= f * half)
        out[i] = db[m].mean() if m.any() else db[i]
    return out


# --------------------------------------------------------------------- fitting
def _band(freqs, fs):
    return (freqs >= F_MIN) & (freqs <= min(F_MAX, fs / 2 * 0.95))


def _fit_one(freqs, residual, x0, band, weights, fs, max_boost_db):
    lo = np.array([np.log10(F_MIN), G_MIN, np.log10(Q_MIN)])
    hi = np.array([np.log10(F_MAX), min(G_MAX, max_boost_db), np.log10(Q_MAX)])

    def r(p):
        return (peq_response(freqs, 10 ** p[0], p[1], 10 ** p[2], fs)
                - residual)[band] * weights[band]

    x0 = np.clip(x0, lo, hi)
    sol = least_squares(r, x0, bounds=(lo, hi), max_nfev=200)
    return sol.x


def _merge_close(filters, tol_oct=0.15, max_boost_db=G_MAX):
    """Collapse bands the optimiser drove onto (nearly) the same frequency.

    When the correction exceeds the gain cap the optimiser will pile several
    bands onto one spot; in dB their responses add, so merging is the honest
    representation (and it stops one band's worth of work being wasted).
    """
    out = []
    for f0, g, q in sorted(filters, key=lambda t: t[0]):
        if out and abs(np.log2(f0 / out[-1][0])) < tol_oct:
            pf, pg, pq = out[-1]
            out[-1] = (pf, float(np.clip(pg + g, G_MIN, max_boost_db)),
                       float(min(pq, q)))
        else:
            out.append((f0, g, q))
    return out


def fit_peq(freqs, correction_db, n_filters=15, weights=None, fs=FS,
            max_boost_db=3.0, min_gain_db=0.15, seed=0):
    """Fit up to `n_filters` peaking filters so their sum matches `correction_db`.

    `correction_db` is what must be *added* to the measurement to reach the
    target (target - measured), in dB, on the `freqs` grid.

    Matching pursuit: each new band is least-squares fitted to whatever error
    remains, then subtracted, and finally all bands are refined together.

    `max_boost_db` caps upward gain (boosting narrow dips wastes headroom and
    usually sounds worse than leaving them); cuts are unrestricted to G_MIN.
    Returns (filters, info).
    """
    freqs = np.asarray(freqs, float)
    target = np.asarray(correction_db, float)
    if weights is None:
        weights = np.ones_like(freqs)
    band = _band(freqs, fs) & np.isfinite(target)
    weights = weights / weights[band].sum()

    residual = target.copy()
    params = []
    chosen = []
    for _ in range(n_filters):
        if not np.isfinite(residual[band]).any():
            break
        # Score each candidate by how much error a filter placed there could
        # actually remove. A point that needs boost scores 0 when boosting is
        # disallowed, so the search skips it instead of wasting a band.
        gains = np.clip(residual, G_MIN, max_boost_db)
        score = np.abs(gains) * np.abs(residual)
        # Never stack another band on top of one already placed: if the needed
        # correction exceeds the gain cap, the residual stays large there and
        # greedy would otherwise keep re-picking the same frequency.
        for c in chosen:
            score[np.abs(np.log2(freqs / c)) < 0.2] = -np.inf
        masked = np.where(band, score, -np.inf)
        j = int(np.argmax(masked))
        if not np.isfinite(masked[j]) or masked[j] <= 0:
            break
        f0 = float(np.clip(freqs[j], F_MIN, F_MAX))
        chosen.append(f0)
        x0 = np.array([np.log10(f0),
                       float(np.clip(residual[j], G_MIN, max_boost_db)),
                       np.log10(1.0)])
        x = _fit_one(freqs, residual, x0, band, weights, fs, max_boost_db)
        params += list(x)
        residual -= peq_response(freqs, 10 ** x[0], x[1], 10 ** x[2], fs)

    if not params:
        return [], {'rms_error': float('nan'), 'max_error': float('nan'),
                    'flat_params': np.array([]), 'predicted_db': np.zeros_like(freqs)}

    # ---- joint refinement ---------------------------------------------------
    params = np.array(params)
    lb, ub = [], []
    for _ in range(len(params) // 3):
        lb += [np.log10(F_MIN), G_MIN, np.log10(Q_MIN)]
        ub += [np.log10(F_MAX), min(G_MAX, max_boost_db), np.log10(Q_MAX)]

    def resid(p):
        return (bank_response(freqs, p, fs) - target)[band] * weights[band]

    sol = least_squares(resid, np.clip(params, lb, ub), bounds=(lb, ub),
                        max_nfev=1500, xtol=1e-9, ftol=1e-9)

    # ---- collapse stacked bands, then drop ones doing nothing ---------------
    kept = _merge_close(unpack(sol.x), max_boost_db=max_boost_db)
    kept = [x for x in kept if abs(x[1]) >= min_gain_db]
    kept.sort(key=lambda t: t[0])
    flat = []
    for f0, g, q in kept:
        flat += [np.log10(f0), g, np.log10(q)]
    flat = np.array(flat) if flat else np.array([])

    pred = bank_response(freqs, flat, fs) if len(flat) else np.zeros_like(freqs)
    err = (pred - target)[band]
    w = weights[band]
    info = {
        'rms_error': float(np.sqrt(np.sum(w * err ** 2))),
        'max_error': float(np.max(np.abs(err))) if err.size else float('nan'),
        'flat_params': flat,
        'predicted_db': pred,
        'requested': n_filters,
        'used': len(kept),
    }
    return kept, info


# ---------------------------------------------------------------- target curve
def target_tilt(freqs, anchor_hz=1000.0, db_per_octave=-0.5, level_db=0.0):
    """A simple downward house curve, the usual starting point."""
    freqs = np.asarray(freqs, float)
    return level_db + db_per_octave * np.log2(freqs / anchor_hz)
