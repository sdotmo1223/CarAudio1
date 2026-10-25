import json, os, math

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RUN = os.path.join(ROOT, 'ds18-bridge', 'measurements', 'run-20261020-1925')
if not os.path.isdir(RUN):
    RUN = os.path.join(ROOT, 'measurements', 'run-20261020-1925')

CH = [
    ('tweeter-1.json',  'out 1  tweeter FL', '#ff5555'),
    ('tweeter-2.json',  'out 2  tweeter FR', '#ff9944'),
    ('tweeter-3.json',  'out 3  tweeter RL', '#ffcc44'),
    ('tweeter-4.json',  'out 4  tweeter RR', '#ffee66'),
    ('midrange-1.json', 'out 5  mid 3.5" front', '#66dd66'),
    ('midrange-2.json', 'out 6  mid 3.5" rear', '#33aa55'),
    ('midrange-3.json', 'out 7  woofer 8" front', '#4488ff'),
    ('midbass-1.json',  'out 8  woofer 6.5" rear', '#aa55ff'),
]

W, H, L, R, T, B = 1240, 640, 66, 24, 44, 46
F0, F1 = 20.0, 20000.0
DB0, DB1 = -125.0, -10.0

def x(f): return L + (math.log10(f / F0) / math.log10(F1 / F0)) * (W - L - R)
def y(d): return T + (DB1 - d) / (DB1 - DB0) * (H - T - B)

def esc(s): return s.replace('&', '&amp;').replace('<', '&lt;').replace('"', '&quot;')

parts = []
parts.append('<svg xmlns="http://www.w3.org/2000/svg" width="%d" height="%d" font-family="Segoe UI,Arial" style="background:#14161a">' % (W, H))
# grid: frequency decades + notes
for f in [20, 30, 50, 70, 100, 200, 300, 500, 700, 1000, 2000, 3000, 5000, 7000, 10000, 20000]:
    xx = x(f)
    lab = '%g' % f if f < 1000 else ('%gk' % (f / 1000))
    parts.append('<line x1="%.1f" y1="%d" x2="%.1f" y2="%d" stroke="#2a2e35" stroke-width="1"/>' % (xx, T, xx, H - B))
    parts.append('<text x="%.1f" y="%d" fill="#8a919c" font-size="11" text-anchor="middle">%s</text>' % (xx, H - B + 16, lab))
for d in range(-120, -10, 10):
    yy = y(d)
    parts.append('<line x1="%d" y1="%.1f" x2="%d" y2="%.1f" stroke="#2a2e35" stroke-width="1"/>' % (L, yy, W - R, yy))
    parts.append('<text x="%d" y="%.1f" fill="#8a919c" font-size="11" text-anchor="end">%d</text>' % (L - 6, yy + 4, d))
# crossover seams
for f, lab in [(60, 'HP 60'), (80, 'HP 80'), (100, 'HP 100 (new)'), (1500, 'HP/LP 1.5k')]:
    xx = x(f)
    parts.append('<line x1="%.1f" y1="%d" x2="%.1f" y2="%d" stroke="#e8b33a" stroke-width="1.2" stroke-dasharray="6 5" opacity="0.85"/>' % (xx, T, xx, H - B))
    parts.append('<text x="%.1f" y="%d" fill="#e8b33a" font-size="11" text-anchor="middle">%s</text>' % (xx, T - 6, lab))
# curves
for fn, label, color in CH:
    p = os.path.join(RUN, fn)
    rec = json.load(open(p, encoding='utf-8-sig'))
    pts = [(float(a), float(b)) for a, b in rec['points'] if F0 * 0.9 <= a <= F1 * 1.1]
    pts.sort()
    d = 'M ' + ' L '.join('%.1f %.1f' % (x(f), y(v)) for f, v in pts)
    parts.append('<path d="%s" fill="none" stroke="%s" stroke-width="1.8" stroke-linejoin="round"/>' % (d, color))
# legend
ly = T + 6
for i, (fn, label, color) in enumerate(CH):
    col = i // 4
    lx = L + 12 + col * 300
    yy = ly + (i % 4) * 17
    parts.append('<line x1="%d" y1="%d" x2="%d" y2="%d" stroke="%s" stroke-width="3"/>' % (lx, yy - 4, lx + 22, yy - 4, color))
    parts.append('<text x="%d" y="%d" fill="#d7dbe0" font-size="12">%s</text>' % (lx + 28, yy, esc(label)))
parts.append('<text x="%d" y="%d" fill="#e8eef5" font-size="15" font-weight="600">Your sweep - 8 channels aligned, absolute dBFS at the mic (UMIK-1, 2026-10-20 19:12, EQ flat)</text>' % (L, 24))
parts.append('<text x="%d" y="%d" fill="#8a919c" font-size="11">crossover seams dashed - 60/80/100 Hz high-passes and the 1.5 kHz tweeter/mid split</text>' % (W - R - 430, H - 10))
parts.append('</svg>')

out = os.path.join(ROOT, 'ds18-bridge', 'var', 'sweep-overlay.html')
if not os.path.isdir(os.path.dirname(out)):
    out = os.path.join(ROOT, 'var', 'sweep-overlay.html')
html = '<!doctype html><meta charset="utf-8"><body style="margin:0;background:#14161a">' + ''.join(parts) + '</body>'
open(out, 'w', encoding='utf-8').write(html)
print('wrote', out)
