// pull-phone-meas.js - copy the tune page's browser storage off the phone, over USB.
//
// The page keeps everything that matters in localStorage on the phone: the eight RTA
// measurements (ds18.rtaMeas), the speaker roster (ds18.roster), the sweep snapshot and the
// playback preferences. Nothing in the page ever sends that anywhere, so a USB pull through
// Chrome's DevTools socket is the only way to get it onto the laptop without a code change.
//
// Usage (phone attached, USB debugging on):
//   adb -s <serial> forward tcp:9222 localabstract:chrome_devtools_remote
//   node var\pull-phone-meas.js
//
// Writes measurements/phone-storage-<timestamp>.json and prints what it found.

const fs = require('fs');
const path = require('path');

const OUT_DIR = path.join(__dirname, '..', 'measurements');

async function listTargets() {
  const res = await fetch('http://127.0.0.1:9222/json/list');
  return res.json();
}

function evaluate(wsUrl, expression) {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(wsUrl);
    const id = 1;
    const timer = setTimeout(() => { try { ws.close(); } catch (e) {} reject(new Error('timeout')); }, 15000);
    ws.onopen = () => {
      ws.send(JSON.stringify({ id, method: 'Runtime.evaluate', params: { expression, returnByValue: true } }));
    };
    ws.onmessage = (ev) => {
      const msg = JSON.parse(ev.data);
      if (msg.id !== id) { return; }
      clearTimeout(timer);
      try { ws.close(); } catch (e) {}
      if (msg.error) { reject(new Error(JSON.stringify(msg.error))); return; }
      const r = msg.result || {};
      if (r.exceptionDetails) { reject(new Error(r.exceptionDetails.text || 'evaluate failed')); return; }
      resolve(r.result ? r.result.value : undefined);
    };
    ws.onerror = (e) => { clearTimeout(timer); reject(new Error('websocket error')); };
  });
}

(async () => {
  let targets;
  try {
    targets = await listTargets();
  } catch (e) {
    console.error('cannot reach the DevTools socket on 127.0.0.1:9222 - is the forward set up?');
    console.error('  adb -s <serial> forward tcp:9222 localabstract:chrome_devtools_remote');
    process.exit(2);
  }

  const pages = targets.filter((t) => t.type === 'page' && t.webSocketDebuggerUrl);
  if (!pages.length) { console.error('no debuggable page found'); process.exit(3); }

  const tune = pages.find((t) => /:8585/.test(t.url || '')) ||
               pages.find((t) => /DreTunes|ds18|tune/i.test((t.title || '') + (t.url || ''))) ||
               pages[0];
  console.log('page : ' + (tune.title || '(untitled)'));
  console.log('url  : ' + tune.url);

  const all = await evaluate(tune.webSocketDebuggerUrl,
    'JSON.stringify(Object.fromEntries(Object.entries(localStorage)))');
  const store = JSON.parse(all || '{}');

  const keys = Object.keys(store).sort();
  console.log('keys : ' + keys.join(', '));

  if (!fs.existsSync(OUT_DIR)) { fs.mkdirSync(OUT_DIR, { recursive: true }); }
  const stamp = new Date().toISOString().replace(/[:.]/g, '-').slice(0, 19);
  const out = path.join(OUT_DIR, 'phone-storage-' + stamp + '.json');
  fs.writeFileSync(out, JSON.stringify({ pulledAt: new Date().toISOString(), url: tune.url, storage: store }, null, 2));
  console.log('wrote: ' + out);

  // What actually matters: the measurements table.
  try {
    const meas = JSON.parse(store['ds18.rtaMeas'] || '[]');
    console.log('measurements: ' + meas.length);
    meas.forEach((m, i) => {
      const bands = Array.isArray(m.bands) ? m.bands.length : (Array.isArray(m.dB) ? m.dB.length : 0);
      const when = m.t ? new Date(m.t).toISOString().replace('T', ' ').slice(0, 19) : '(no timestamp)';
      console.log('  [' + i + '] ' + when + '  name=' + (m.name || m.label || '?') + '  points=' + bands);
    });
  } catch (e) {
    console.log('measurements: could not parse ds18.rtaMeas - ' + e.message);
  }

  try {
    const roster = JSON.parse(store['ds18.roster'] || 'null');
    if (roster) { console.log('roster: ' + JSON.stringify(roster).slice(0, 300)); }
  } catch (e) {}
})().catch((e) => { console.error(e.message); process.exit(1); });
