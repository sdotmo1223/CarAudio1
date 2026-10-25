// verify-baseline.js - the measurement-baseline classifier, checked on its own.
//
// WHY THIS EXISTS. The classifier decides whether a measurement tone is allowed to play: a
// response measured through a tuned EQ is a measurement of the tune, so the page refuses to play
// (or capture) a tone unless the memory reads as NEUTRAL - every enabled band at 0 dB, or the
// block bypassed. If this classifier is wrong in the permissive direction it silently produces
// curves that describe the tune and nothing stops them being believed.
//
// WHY IT IS NOT TESTED IN THE BROWSER. With the car off the DSP reads as disconnected, so the
// "tuned" branch never runs; the one state that matters is the state a parked car cannot produce.
// The classification is therefore lifted verbatim out of the page and driven directly.
//
// Usage (from ds18-bridge, the project root):
//   node verify-baseline.js web\index.html              expect all pass -> exit 0
//   node verify-baseline.js web\index.html --mutate     break the classifier -> expect exit 1
//
// The --mutate run is the negative control: it proves these assertions can fail. verify-web.ps1
// runs both, so a green gate means the classifier is right AND that this check can still detect it
// being wrong.
"use strict";
const fs = require("fs");

const page = process.argv[2];
if (!page) { console.error("usage: node verify-baseline.js <path to index.html> [--mutate]"); process.exit(3); }

const html = fs.readFileSync(page, "utf8");

// Lift the constants + baseCell + baseVerdict out of the inline script, verbatim.
const start = html.indexOf("var BAND_TOLERANCE_DB");
const end = html.indexOf("function baseRender()");
if (start < 0 || end < 0 || end <= start) {
  // Not a pass: the safety code moved or was deleted, and this check no longer covers anything.
  console.error("could not locate the baseline classifier in " + page);
  process.exit(3);
}
let src = html.slice(start, end);

const mutate = process.argv.indexOf("--mutate") >= 0;
if (mutate) {
  const before = src;
  src = src.replace("c.flat = (c.off === 0);", "c.flat = true;");
  if (src === before) { console.error("mutation did not apply - the classifier changed shape"); process.exit(3); }
  console.log("[mutant] every cell now reports flat=true");
}

// Stubs for the page state the lifted code closes over.
const S = { connected: true, mem: 1 };
const BASE = { cells: null, mem: -1 };
const fn = new Function("S", "BASE", src + "\nreturn { baseCell: baseCell, baseVerdict: baseVerdict };");
const api = fn(S, BASE);

let pass = 0, fail = 0;
function ok(name, cond, extra) {
  if (cond) { pass++; console.log("  ok   " + name); }
  else { fail++; console.log("  FAIL " + name + (extra ? "  -> " + extra : "")); }
}

// ---- baseCell: what counts as flat on ONE path ----
let c = api.baseCell(0, { ok: true, byPass: false, bands: [
  { fc: 20, gain: 0, enable: true },
  { fc: 50, gain: 0.02, enable: true },            // inside tolerance
  { fc: 100, gain: 0, enable: true }
] });
ok("all-zero bands are flat", c.flat === true, JSON.stringify(c));

c = api.baseCell(0, { ok: true, byPass: false, bands: [
  { fc: 20, gain: 0, enable: true },
  { fc: 50, gain: 5, enable: false }               // a disabled band cannot shape anything
] });
ok("a disabled non-zero band is still flat", c.flat === true, JSON.stringify(c));

c = api.baseCell(0, { ok: true, byPass: false, bands: [
  { fc: 20, gain: 0, enable: true },
  { fc: 50, gain: 3.5, enable: true }
] });
ok("an enabled +3.5 dB band is NOT flat", c.flat === false, JSON.stringify(c));
ok("  and the worst band is reported", c.worst === 3.5, String(c.worst));

c = api.baseCell(3, { ok: true, byPass: false, bands: [{ fc: 20, gain: -4.2, enable: true }] });
ok("a cut is not flat either", c.flat === false && c.worst === -4.2, JSON.stringify(c));

c = api.baseCell(2, { ok: false, bands: [] });
ok("an unread path is not flat", c.flat === false && c.ok === false, JSON.stringify(c));

c = api.baseCell(2, { ok: true, byPass: true, bands: [{ fc: 20, gain: 6, enable: true }] });
ok("bypassed is reported as bypassed", c.byPass === true, JSON.stringify(c));

// ---- baseVerdict: the whole memory ----
function verdict(cells, mem) {
  BASE.cells = cells;
  BASE.mem = mem;
  S.mem = 1;
  return api.baseVerdict();
}

let v = verdict([{ path: 0, ok: true, byPass: false, flat: true },
                 { path: 1, ok: true, byPass: false, flat: true }], 1);
ok("all-flat memory is neutral", v.state === "neutral" && v.flat === 2, JSON.stringify(v));

v = verdict([{ path: 0, ok: true, byPass: true, flat: false },
             { path: 1, ok: true, byPass: false, flat: true }], 1);
ok("a bypassed path counts as neutral", v.state === "neutral" && v.bypassed === 1, JSON.stringify(v));

v = verdict([{ path: 0, ok: true, byPass: false, flat: true },
             { path: 4, ok: true, byPass: false, flat: false, worst: 3 }], 1);
ok("one tuned path makes the memory TUNED", v.state === "tuned", JSON.stringify(v));
ok("  and it names the path + amount", v.tuned[0] === "4 (+3 dB)", JSON.stringify(v.tuned));

v = verdict([{ path: 0, ok: true, byPass: false, flat: true }], 0);   // read for mem 0, we are on 1
ok("a read from another memory is unknown, not neutral", v.state === "unknown", JSON.stringify(v));

v = verdict([{ path: 0, ok: false }], 1);
ok("a failed read is unknown, not neutral", v.state === "unknown" && v.failed[0] === 0, JSON.stringify(v));

S.connected = false;
v = api.baseVerdict();
ok("disconnected is its own state", v.state === "offline", JSON.stringify(v));

console.log((mutate ? "[mutant] " : "") + pass + " passed, " + fail + " failed");
process.exit(fail ? 1 : 0);
