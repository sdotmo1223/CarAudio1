r"""_apply-fast.py - load an .idsp into ONE device memory over the bridge API, the same
endpoints the app's Write button uses (cmd 14 EQ per path + cmd 11 output block).
Crossovers (cmd 13) are NOT touched: every library tune shares the same map, and the
app path never writes them either. Readback verification is a separate step
(verify-device.py). Usage:  python tune\_apply-fast.py <file.idsp> [--mem 1]
"""
import json, sys, time, urllib.request

BASE = 'http://127.0.0.1:8585'

def post(path, obj, timeout=90):
    data = json.dumps(obj).encode()
    req = urllib.request.Request(BASE + path, data=data,
                                 headers={'Content-Type': 'application/json'})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.loads(r.read().decode())

def main():
    if len(sys.argv) < 2:
        sys.exit('usage: _apply-fast.py <file.idsp> [--mem N]')
    file = sys.argv[1]
    mem_idx = 1
    if '--mem' in sys.argv:
        mem_idx = int(sys.argv[sys.argv.index('--mem') + 1])

    sys.path.insert(0, 'tune')
    import idsp
    doc = idsp.load(file)
    fi = mem_idx if mem_idx < len(doc['memories']) else 0   # one tune per file
    mem = doc['memories'][fi]
    outs = {o['index']: o for o in mem['outputs']}

    ok = bad = 0
    for p in range(8):
        o = outs.get(p)
        if o is None:
            continue
        bands = [{'fc': int(round(b['Fc'])), 'gain': round(b['Gain'], 2),
                  'q': round(b['Q'], 2)} for b in idsp.active_bands(o)]
        try:
            r = post('/api/eq', {'mem': mem_idx, 'path': p, 'byPass': False, 'bands': bands})
            if r.get('ok'):
                ok += 1
                print('  eq mem%d path %d: %d bands sent' % (mem_idx, p, len(bands)))
            else:
                bad += 1
                print('  eq mem%d path %d: REJECTED %s' % (mem_idx, p, r))
        except Exception as e:
            bad += 1
            print('  eq mem%d path %d: ERR %s' % (mem_idx, p, e))
        time.sleep(0.12)

    mute, gain, delay, revert = [], [], [], []
    for i in range(16):
        o = outs.get(i)
        mute.append(bool(o.get('mute', False)) if o else False)
        gain.append(round(float(o.get('gain', 0.0)), 2) if o else 0.0)
        delay.append(round(float(o.get('delay', 0.0)), 2) if o else 0.0)
        revert.append(bool(o.get('revert', False)) if o else False)
    try:
        r = post('/api/output', {'mem': mem_idx, 'mute': mute, 'gainDb': gain,
                                 'delayMs': delay, 'revert': revert})
        print('  output block: ok=%s enable=0x%02X' % (r.get('ok'), int(r.get('enableMask', 0))))
    except Exception as e:
        bad += 1
        print('  output block: ERR %s' % e)

    print('RESULT: eq %d ok / %d bad' % (ok, bad))
    sys.exit(1 if bad else 0)

main()
