// Reproduces the claim that the DSP's five EQ coefficients are an RBJ peaking biquad.
// POSTs band parameters to the bridge's /api/eqcoef, which calls the vendor's own
// LibHoneyWg.EqGetCoff locally (no USB, no amplifier required), then decodes the result
// and compares it against the textbook formula.
//
//   node evidence-eqcoef.js [baseUrl]
//
var BASE = process.argv[2] || "http://localhost:8585";
var FS = 48000;

var bands = [
  { fc: 1000, gain: 6,   q: 1.4 },
  { fc: 200,  gain: -6,  q: 4.0 },
  { fc: 100,  gain: 10,  q: 0.5 },
  { fc: 5000, gain: -12, q: 2.5 },
  { fc: 63,   gain: 3,   q: 7.0 }
];

function bswap(n) {
  return ((n & 0xff) << 24) | ((n & 0xff00) << 8) | ((n >>> 8) & 0xff00) | ((n >>> 24) & 0xff);
}
function decode(c) {                       // byte-swapped, unsigned Q24
  return c.map(function (n) { return bswap(n >>> 0) / 0x1000000; });
}
function rbj(fc, gainDb, Q, fs) {
  var A = Math.pow(10, gainDb / 40);
  var w0 = 2 * Math.PI * fc / fs;
  var cw = Math.cos(w0), sw = Math.sin(w0);
  var alpha = sw / (2 * Q);
  var b0 = 1 + alpha * A, b1 = -2 * cw, b2 = 1 - alpha * A;
  var a0 = 1 + alpha / A, a1 = -2 * cw, a2 = 1 - alpha / A;
  return { b0: b0 / a0, b1: b1 / a0, b2: b2 / a0, a1: a1 / a0, a2: a2 / a0 };
}

function post(band) {
  return fetch(BASE + "/api/eqcoef", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(band)
  }).then(function (r) { return r.json(); });
}

var lines = [];
function say(s) { lines.push(s); console.log(s); }

say("EQ coefficient validation - vendor EqGetCoff vs RBJ peaking cookbook");
say("bridge: " + BASE + "   assumed fs: " + FS + " Hz");
say("");

Promise.all(bands.map(post)).then(function (res) {
  var worst = 0;
  res.forEach(function (r, i) {
    var s = bands[i];
    var d = decode(r.coeffs);
    var w = rbj(s.fc, s.gain, s.q, FS);
    var pairs = [["b2", d[0], w.b2], ["b1", d[1], w.b1], ["b0", d[2], w.b0],
                 ["-a2", d[3], -w.a2], ["-a1", d[4], -w.a1]];
    say("fc=" + s.fc + " Hz  gain=" + s.gain + " dB  Q=" + s.q);
    pairs.forEach(function (p) {
      var e = Math.abs(p[1] - p[2]);
      if (e > worst) { worst = e; }
      say("    " + p[0].padEnd(4) + " decoded=" + p[1].toFixed(8) +
          "  rbj=" + p[2].toFixed(8) + "  |err|=" + e.toExponential(3));
    });
    say("");
  });
  say("WORST ABS ERROR across all coefficients: " + worst.toExponential(3));
  say("one Q24 LSB = 1/16777216 = " + (1 / 0x1000000).toExponential(3));
  say(worst < 1e-4
    ? "VERDICT: exact match - RBJ peaking @ 48 kHz, storage order [b2,b1,b0,-a2,-a1], unsigned Q24 big-endian."
    : "VERDICT: NOT an exact match under this ordering / sample rate.");

  require("fs").writeFileSync(
    require("path").join(__dirname, "eqcoef-rbj-validation.txt"),
    lines.join("\n") + "\n", "utf8");
  console.log("\nwrote evidence/eqcoef-rbj-validation.txt");
}).catch(function (e) {
  console.error("failed: " + e.message);
  process.exit(1);
});
