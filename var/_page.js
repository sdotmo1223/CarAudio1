
(function () {
  "use strict";

  // pathRead: TRUE only once the CURRENT (mem, path) cell has been read back from the
  // device in this session. Until then the grid holds the bridge's boot defaults (the
  // 1/3-octave ladder at 0 dB), and writing those would overwrite whatever the cell really
  // holds - which is exactly how output 0 of memory 2 lost its EQ on 2026-10-15.
  var S = { connected: false, mem: 0, path: 0, source: 0, mainDb: -3, mute: false,
            bandHz: [], bandGainDb: [], bandQ: [], bandEnabled: [], byPass: false,
            pathRead: false };
  var logEl = document.getElementById("log");

  function log(msg) {
    logEl.textContent += (logEl.textContent ? "\n" : "") + msg;
    logEl.scrollTop = logEl.scrollHeight;
  }

  var toastTimer = null;
  function toast(msg) {
    var t = document.getElementById("toast");
    t.textContent = msg;
    t.classList.add("show");
    if (toastTimer) clearTimeout(toastTimer);
    toastTimer = setTimeout(function () { t.classList.remove("show"); }, 1800);
  }

  function api(path, body) {
    var opts = body === undefined
      ? { method: "GET" }
      : { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) };
    return fetch(path, opts).then(function (r) { return r.json(); });
  }

  function setConnected(on, err) {
    var was = S.connected;
    S.connected = on;
    document.getElementById("dot").classList.toggle("on", on);
    document.getElementById("btnConnect").textContent = on ? "Disconnect" : "Connect";
    document.getElementById("stUsb").textContent = on ? "connected" : (err || "not connected");
    document.getElementById("stUsb").style.color = on ? "var(--ok)" : "var(--bad)";
    // the header strip's copy of the link state
    var tu = document.getElementById("tsUsb");
    if (tu) {
      tu.textContent = on ? "USB connected" : "not connected";
      tu.title = on ? "" : (err || "");      // the full reason stays on the Status tab
      tu.style.color = on ? "var(--ok)" : "var(--bad)";
    }
    if (typeof hdrVol === "function") { hdrVol(); }   // "-" when the link is down
    // A run that lost the link mid-way could not put the channel block back (the write it tried
    // went nowhere). The moment the link is back and no run is in flight, that block goes out -
    // otherwise the car sits with all but one output muted until someone notices.
    if (on && !was && RUN && !RUN.busy && RUN.dirty && RUN.snap) {
      runRestoreAfter("the DSP link came back before the block was put back");
    }
  }

  // ---------- status ----------
  function renderStatus(d) {
    // Source names come from the vendor's own DP12_Param constants via /api/status,
    // so the UI no longer guesses at what each index means.
    var srcName = d.sourceName || ("source " + d.source);
    S.sourceName = srcName;                 // homeSubText() reuses it outside this call
    document.getElementById("stSource").textContent = d.source + " - " + srcName;
    document.getElementById("stTune").textContent = TUNE.name || "-";
    document.getElementById("stMaster").textContent = d.mainDb.toFixed(1) + " dB";

    // Front-page hero. The source label reuses the vendor's own DP12_Param names pulled
    // through /api/status, so the biggest text on the screen is also the least guessy.
    var hs = document.getElementById("homeSub");
    if (hs) { hs.textContent = homeSubText(); }
    var hm = document.getElementById("homeMaster");
    if (hm && typeof d.mainDb === "number") {
      hm.innerHTML = (d.mainDb > 0 ? "+" : "") + d.mainDb.toFixed(1) +
                     '<span class="heroUnit">dB</span>';
    }
    document.getElementById("stGroups").textContent = d.groupDb.map(function (v) { return v.toFixed(1); }).join(" / ");
    document.getElementById("stBt").textContent = d.btOrUsb ? "active" : "-";
    if (typeof d.mainDb === "number") { S.mainDb = d.mainDb; setMasterDisplay(d.mainDb); }
    if (typeof d.mute === "boolean") {
      S.mute = d.mute;
      document.getElementById("btnMute").classList.toggle("on", d.mute);
    }
    if (typeof d.source === "number") { S.source = d.source; renderSourceChecks(); }

    if (typeof d.mem === "number") {
      // The preset can also change WITHOUT us (preset buttons on the head unit): the
      // displayed cell then belongs to another memory - invalidate and re-read.
      var memChanged = (d.mem !== S.mem);
      S.mem = d.mem;
      if (memChanged && S.connected) {
        S.pathRead = false;
        readEq(true);
      }
      // Sweep mode flattens the memory that is playing. /api/status is the device itself, so if
      // the preset buttons on the head unit move the device off that memory, the sweeps would be
      // measuring the tune again - say so rather than report a curve that is not the reference.
      if (SWEEP.on && SWEEP.snap && typeof SWEEP.snap.mem === "number"
          && d.mem !== SWEEP.snap.mem && !SWEEP.warnedMem) {
        SWEEP.warnedMem = true;
        sweepStatus("memory moved to " + (d.mem + 1) + " — the flat copy is on "
                    + (SWEEP.snap.mem + 1), true);
        toast("sweep mode: the device moved to memory " + (d.mem + 1)
              + " — the flat copy is on memory " + (SWEEP.snap.mem + 1));
      }
    }

    // Mirror the same state onto the header strip, which is on screen on every tab.
    tuneNowFill(d);
  }

  // The header's status strip. Fed by /api/status once the device answers, and by the boot
  // /api/info call (the bridge's last-known state) so it is never blank - the "usb" item is
  // what says whether those numbers came off the DSP just now. The tune name is not repeated
  // here; it is the field on the tune bar directly underneath.
  function setTxt(id, v) {
    var e = document.getElementById(id);
    if (e) { e.textContent = v; }
  }
  function tuneNowFill(d) {
    if (!document.getElementById("hstats")) { return; }
    setTxt("tsSource", (typeof d.source === "number")
      ? (d.source + (d.sourceName ? " - " + d.sourceName : ""))
      : "-");
    setTxt("tsMaster", (typeof d.mainDb === "number")
      ? (((d.mainDb > 0 ? "+" : "") + d.mainDb.toFixed(1) + " dB") + (d.mute ? " · muted" : ""))
      : "-");
    setTxt("tsGroups", d.groupDb && d.groupDb.length
      ? d.groupDb.map(function (v) { return v.toFixed(1); }).join(" / ")
      : "-");
    setTxt("tsMem", (typeof d.mem === "number") ? ((d.mem + 1) + " of 4") : "-");
    memPaint();
    hdrEq();
  }

  // The one piece of the header that is app state rather than a device read: whether the EQ is
  // in circuit. Kept apart from tuneNowFill so the toggle and the boot both land in one place.
  function hdrEq() {
    setTxt("tsEq", S.byPass ? "bypassed" : "in circuit");
  }

  // The per-output levels on the banner. Same source as the Volume tab's sliders (CH.gain) and
  // painted from seedVolume(), so the bar and the sliders can never disagree: output 0 first,
  // one decimal, "dB" left off because it is one long number being read at a glance. Trailing
  // ".0" is dropped to keep the strip short. Until the tune's levels have been read the numbers
  // are boot defaults, so they are shown dimmed rather than pretending to be the tune.
  function hdrVol() {
    var el = document.getElementById("tsVol");
    if (!el) { return; }
    // No output count yet (before the first /api/info) means no honest numbers to show.
    var n = (CH.count >= 1 && CH.count <= 16) ? CH.count : 0;
    var unread = S.connected && !CH.known[S.mem];
    var parts = [], i;
    for (i = 0; i < n; i++) {
      var g = Number(CH.gain[i]);
      if (isNaN(g)) { g = 0; }
      parts.push(g.toFixed(1).replace(/\.0$/, ""));
    }
    el.textContent = (!S.connected || !parts.length) ? "-" : parts.join(" · ");
    el.title = !S.connected
      ? "connect to read the per-output levels"
      : (unread
        ? "this tune's levels have not been read yet — these are boot defaults"
        : "per-output levels in dB, output 1 first — the same numbers as the Volume tab's sliders");
    el.className = "hv" + ((!S.connected || unread) ? " dim" : "");
  }

  function refreshStatus() {
    api("/api/status").then(function (d) {
      if (!d.ok) { toast(d.error); log("status: " + d.error); return; }
      renderStatus(d);
      log("status ok, raw=" + d.rawHex);
    }).catch(function (e) { toast(String(e)); });
  }

  // ---------- channel volume (per-output level, cmd 11) ----------
  // One slider per output, seeded from the active memory's stored levels - i.e. what the
  // current tune set. The DSP's own output-gain limits are -40.0 ... 0.0 dB (protocol
  // section 15), where 0 dB is FULL LEVEL, so the slider runs left = silent, right =
  // full. Releasing a slider rewrites the complete 16-entry block through chSend, which
  // keeps every other channel exactly as it is.
  var volSliders = [];

  function buildVolume() {
    var host = document.getElementById("volSliders");
    host.innerHTML = "";
    volSliders = [];
    var n = (typeof chVisible === "function") ? chVisible() : 8;
    for (var i = 0; i < n; i++) {
      var row = document.createElement("div");
      row.className = "slider";
      row.innerHTML = '<label>CH ' + (i + 1) + '</label>' +
                      '<input type="range" min="-400" max="0" value="0" step="1">' +
                      '<span class="val">0.0 dB</span>';
      host.appendChild(row);
      (function (idx, input, out) {
        input.addEventListener("input", function () {
          out.textContent = (input.value / 10).toFixed(1) + " dB";
        });
        // "change" fires on release: one write per thumb-lift, not a stream of frames.
        input.addEventListener("change", function () {
          if (!S.connected) { toast("connect first"); return; }
          CH.gain[idx] = parseFloat(input.value) / 10;
          hdrVol();                    // the banner follows the slider immediately
          chSend("volume CH " + (idx + 1) + " -> " + CH.gain[idx].toFixed(1) + " dB");
        });
      })(i, row.querySelector("input"), row.querySelector(".val"));
      volSliders.push({ input: row.querySelector("input"), out: row.querySelector(".val") });
    }
    seedVolume();
  }

  // Push the device's stored levels into the sliders. Called after every channel-data
  // load (connect, memory switch, refresh), so the sliders show the tune's own numbers
  // rather than the last thing dragged.
  function seedVolume() {
    for (var i = 0; i < volSliders.length; i++) {
      var g = Number(CH.gain[i]);
      if (isNaN(g)) { g = 0; }
      volSliders[i].input.value = Math.round(-g * 10);   // wire encoding: u16 = -dB * 10
      volSliders[i].out.textContent = g.toFixed(1) + " dB";
    }
    var note = document.getElementById("volNote");
    if (note) {
      if (!S.connected) {
        note.textContent = "Connect to read the live levels.";
      } else if (!CH.known[S.mem]) {
        note.textContent = "This tune's levels not read yet - sliders " +
          "show full level until the first read.";
      } else {
        note.textContent = "Levels from this tune, as the device reports them.";
      }
    }
    hdrVol();                          // the banner's copy of the same numbers
  }

  // ---------- master volume (cmd 12) ----------
  function setMasterDisplay(db) {
    document.getElementById("masterVol").value = Math.round(db * 10);
    document.getElementById("masterVal").textContent = db.toFixed(1) + " dB";
  }

  function applyMaster(mute) {
    var db = parseInt(document.getElementById("masterVol").value, 10) / 10;
    // The sliders now carry PER-CHANNEL levels, so group levels travel from the device
    // mirror instead. They must be sent: DoMaster zeroes all four groups when the array
    // is omitted (protocol section 12).
    var groups = S.groupDb || [0, 0, 0, 0];
    var body = { mainDb: db, mute: mute, groups: groups, mem: S.mem, source: S.source };
    api("/api/master", body).then(function (d) {
      if (!d.ok) { toast(d.error); return; }
      S.mainDb = d.mainDb; S.mute = d.mute;
      document.getElementById("btnMute").classList.toggle("on", d.mute);
      toast(d.acked ? "master sent" : "sent (no reply)");
      log("master " + d.mainDb + " dB mute=" + d.mute + " tx=" + d.txHex);
    }).catch(function (e) { toast(String(e)); });
  }

  // ---------- input source (carried by cmd 12) ----------
  var SOURCES = [
    { id: 0, name: "High-level" },
    { id: 1, name: "Low-level" },
    { id: 2, name: "Aux" },
    { id: 3, name: "Optical" },
    { id: 4, name: "Bluetooth+USB" },
    { id: 5, name: "USB" }
  ];

  function renderSourceChecks() {
    var host = document.getElementById("srcChecks");
    host.innerHTML = "";
    SOURCES.forEach(function (s) {
      var b = document.createElement("button");
      b.className = "src" + (s.id === S.source ? " on" : " ghost");
      b.textContent = s.name;
      b.addEventListener("click", function () { applySource(s.id); });
      host.appendChild(b);
    });
  }

  function applySource(src) {
    var db = parseInt(document.getElementById("masterVol").value, 10) / 10;
    var groups = S.groupDb || [0, 0, 0, 0];   // device mirror; see applyMaster note
    var body = { mainDb: db, mute: S.mute, groups: groups, mem: S.mem, source: src };
    api("/api/master", body).then(function (d) {
      if (!d.ok) { toast(d.error); return; }
      S.source = src;
      S.mainDb = d.mainDb;
      renderSourceChecks();
      toast("source -> " + (d.sourceName || SOURCES[src].name));
      log("source " + src + " (" + (d.sourceName || SOURCES[src].name) + ") tx=" + d.txHex);
    }).catch(function (e) { toast(String(e)); });
  }

  // ---------- tune name (replaces the old Memory 1-4 tabs) ----------
  // The DSP still addresses its cells by memIndex underneath (cmd 12, cmd 9 EQ writes),
  // but the UI deals in ONE tune: the name of what is running. It defaults to the loaded
  // .idsp file name and can be retyped to anything ("heavy bass", "night cruise").
  // Stored bridge-side in tunename.json - one write per finished edit, never a timer.
  var TUNE = { name: "", edited: false };

  function homeSubText() {
    var src = S.sourceName || ("source " + S.source);
    return (TUNE.name || ("mem " + S.mem)) + " / " + src;
  }

  function renderTuneName() {
    var input = document.getElementById("tuneBarInput");
    // Never clobber the field mid-edit: focus means the user is still typing.
    if (input && document.activeElement !== input) { input.value = TUNE.name; }
    var row = document.getElementById("stTune");
    if (row) { row.textContent = TUNE.name || "-"; }
    var hs = document.getElementById("homeSub");
    if (hs) { hs.textContent = homeSubText(); }
  }

  function tuneNameLoad() {
    api("/api/tunename").then(function (d) {
      // edited = the user typed before this boot GET landed; their save wins over it.
      if (!d.ok || TUNE.edited) { return; }
      TUNE.name = (typeof d.name === "string") ? d.name : "";
      renderTuneName();
      log("tune name: \"" + TUNE.name + "\"");
    }).catch(function (e) { log("tune name load failed: " + e); });
  }

  // quiet = caller has its own progress line (tune apply), so no toast.
  function tuneNameSave(quiet) {
    api("/api/tunename", { name: TUNE.name }).then(function (d) {
      if (!d.ok) { toast(d.error); log("tune name save: " + d.error); return; }
      if (!quiet) { toast("tune name saved"); }
      log("tune name -> \"" + TUNE.name + "\" " + d.bytes + "B");
    }).catch(function (e) { toast(String(e)); });
  }

  document.getElementById("tuneBarInput").addEventListener("change", function () {
    var v = this.value.replace(/\s+/g, " ").trim();
    if (v === TUNE.name) { return; }        // unchanged = no write
    this.value = v;
    TUNE.name = v;
    TUNE.edited = true;
    tuneNameSave();
    renderTuneName();
  });

  document.getElementById("memSwap").addEventListener("click", function (ev) {
    var b = (ev.target && ev.target.closest) ? ev.target.closest(".membtn") : null;
    if (!b) { return; }
    memSwitch(parseInt(b.getAttribute("data-mem"), 10));
  });

  // Switching the DSP's active cell: cmd 12 with a new memIndex - exactly what
  // Flux_Select_Memory_Tsmi_Click does in the official app (sets mMemory, then
  // DspSendRunStatusConfigData()). The header's 1-4 strip and "Write EQ to device" (when the
  // file's memory differs from the running one) both come through here. The result is only
  // believed after /api/status says so.
  // Resolves true when the write was accepted, false if it failed.
  function applyMemory(mem, quiet) {
    if (sweepBlocksTuneWrite("switching memory")) { return Promise.resolve(false); }
    if (mem === S.mem) { return Promise.resolve(true); }
    var db = parseInt(document.getElementById("masterVol").value, 10) / 10;
    var groups = S.groupDb || [0, 0, 0, 0];   // device mirror; see applyMaster note
    var body = { mainDb: db, mute: S.mute, groups: groups, mem: mem, source: S.source };
    return api("/api/master", body).then(function (d) {
      if (!d.ok) { toast(d.error); return false; }
      S.mem = mem;
      S.pathRead = false;            // the new preset's cell is still unread
      if (!quiet) { toast("memory " + (mem + 1)); }
      log("memory -> " + (mem + 1) + " (memIndex " + mem + ") tx=" + d.txHex);
      // The DSP reloads that preset's stored values, so read the real state back.
      refreshStatus();
      chRefresh();                    // and the new memory's own output block
      readEq(true);                   // plus its EQ - a preset switch changes the cell
      return true;
    }).catch(function (e) { toast(String(e)); return false; });
  }

  // ---------- the memory strip on the header ----------
  // cmd 12 is a write: it is a request, not a guarantee. /api/status is the device talking,
  // so the highlight follows S.mem and the button that was pressed means nothing until the
  // readback agrees. The old Memory tabs were removed for this exact reason (the bridge's
  // `mem` is a mirror, not a readback).
  function memPaint() {
    var host = document.getElementById("memSwap");
    if (!host) { return; }
    var btns = host.querySelectorAll(".membtn"), i;
    for (i = 0; i < btns.length; i++) {
      btns[i].classList.toggle("on", parseInt(btns[i].getAttribute("data-mem"), 10) === S.mem);
      btns[i].disabled = !S.connected;
    }
  }
  function memSwitch(mem) {
    if (!S.connected) { toast("not connected"); return; }
    if (mem === S.mem) { return; }
    // A cycle is walking the outputs and rewriting the tune's master each step; moving the
    // memory underneath it would change what is being measured halfway through.
    if (typeof RUN !== "undefined" && RUN && RUN.busy) {
      toast("a measurement is running - the memory stays put until it ends");
      return;
    }
    applyMemory(mem).then(function (ok) {
      if (!ok) { memPaint(); return; }
      return api("/api/status").then(function (d) {
        if (d && d.ok && typeof d.mem === "number" && d.mem !== mem) {
          toast("device stayed on memory " + (d.mem + 1) + " - the switch did not take");
          log("memory switch to " + (mem + 1) + " did not take (device reports " + (d.mem + 1) + ")");
        }
        memPaint();
      });
    }).catch(function (e) { toast(String(e)); memPaint(); });
  }

  // ---------- EQ ----------
  // The DSP stores 31 bands, each with its OWN centre frequency, gain and Q. The official
  // app presents this as a fixed 1/3-octave graphic EQ, but nothing forces the frequencies
  // to be the standard ladder -- they are writable, which is why this is numeric entry
  // rather than sliders.
  var EQ_DEFAULT_HZ = [20, 25, 31, 40, 50, 63, 80, 100, 125, 160, 200, 250, 315,
                       400, 500, 630, 800, 1000, 1250, 1600, 2000, 2500, 3150, 4000,
                       5000, 6300, 8000, 10000, 12500, 16000, 20000];
  var EQ_FC_MIN = 20, EQ_FC_MAX = 20000;
  var EQ_GAIN_MIN = -25, EQ_GAIN_MAX = 10;   // DP12_EqGainMin / Max
  var EQ_Q_MIN = 0.1, EQ_Q_MAX = 20;         // DP12_EqQMin / Max

  function clampNum(v, lo, hi) {
    if (isNaN(v)) { return lo; }
    return (v < lo) ? lo : ((v > hi) ? hi : v);
  }

  // ---------- response curve ----------
  // VALIDATED, not assumed. The DSP's own EqGetCoff output was decoded (byte-swapped
  // unsigned Q24) and compared with the RBJ peaking-EQ cookbook: worst error 5.689e-8,
  // which is exactly one Q24 LSB (1/16777216). Storage order is [b2,b1,b0,-a2,-a1] and
  // the sample rate is 48 kHz. So evaluating RBJ here reproduces the DSP's transfer
  // function rather than approximating it.
  var EQ_FS = 48000;

  // Magnitude of one peaking band in dB at linear frequency f.
  function bandDb(fc, gainDb, Q, f) {
    if (!(fc > 0) || !(Q > 0) || !gainDb) { return 0; }
    var A = Math.pow(10, gainDb / 40);
    var w0 = 2 * Math.PI * fc / EQ_FS;
    var cw = Math.cos(w0), sw = Math.sin(w0);
    var alpha = sw / (2 * Q);
    var b0 = 1 + alpha * A, b1 = -2 * cw, b2 = 1 - alpha * A;
    var a0 = 1 + alpha / A, a1 = -2 * cw, a2 = 1 - alpha / A;
    var w = 2 * Math.PI * f / EQ_FS;
    var c1 = Math.cos(w), s1 = Math.sin(w), c2 = Math.cos(2 * w), s2 = Math.sin(2 * w);
    var nr = b0 + b1 * c1 + b2 * c2, ni = -(b1 * s1 + b2 * s2);
    var dr = a0 + a1 * c1 + a2 * c2, di = -(a1 * s1 + a2 * s2);
    var den = dr * dr + di * di;
    if (den <= 0) { return 0; }
    return 10 * Math.log10((nr * nr + ni * ni) / den);
  }

  // Cascaded biquads multiply in magnitude, so dB simply adds.
  function totalDb(bands, f) {
    var s = 0;
    for (var i = 0; i < bands.length; i++) {
      if (bands[i].enable === false) { continue; }
      s += bandDb(bands[i].fc, bands[i].gain, bands[i].q, f);
    }
    return s;
  }

  // Read whatever is in the boxes right now, tolerating half-typed rows (the curve is a
  // live view, so it must not refuse to draw the way collectEq() does before sending).
  function curveBands() {
    var out = [];
    for (var i = 0; i < eqRows.length; i++) {
      var r = eqRows[i];
      var fc = parseFloat(r.hz.value);
      var g = parseFloat(r.db.value);
      var q = parseFloat(r.qq.value);
      if (isNaN(fc) || isNaN(g)) { continue; }
      if (isNaN(q)) { q = 1.0; }
      out.push({
        fc: clampNum(fc, EQ_FC_MIN, EQ_FC_MAX),
        gain: clampNum(g, EQ_GAIN_MIN, EQ_GAIN_MAX),
        q: clampNum(q, EQ_Q_MIN, EQ_Q_MAX),
        enable: r.en.checked
      });
    }
    return out;
  }

  // Each channel owns one colour end to end: the selected curve, its band markers, the
  // legend swatch, the rail button and the grid's accents all wear the same hue, and the
  // other channels' cached curves ghost underneath in their own hues - so one glance shows
  // every driver's area of focus without muddying the channel being edited.
  var EQ_OTHERS = true;
  function chColor(p) { return CH_COLORS[((p % CH_COLORS.length) + CH_COLORS.length) % CH_COLORS.length]; }

  function drawEqCurve() {
    var color = chColor(S.path);
    var ghosts = [];
    if (EQ_OTHERS && SYS.cells && SYS.mem === S.mem) {
      for (var i = 0; i < SYS.cells.length; i++) {
        var c = SYS.cells[i];
        if (!c || !c.ok || c.path === S.path || c.path >= CH_COLORS.length) { continue; }
        ghosts.push({ bands: c.bands, byPass: c.byPass,
                      color: hexA(CH_COLORS[c.path], 0.5) });
      }
    }
    var map = renderCurve("eqCurve", curveBands(), S.byPass, "curveRange",
                { ghosts: ghosts, color: color, marker: color });
    // The grid inherits --accent from its container: checkboxes and slider thumbs take the
    // channel's colour too, so "selected" is legible without reading a label.
    var gr = document.getElementById("eqGrid");
    if (gr) { gr.style.setProperty("--accent", color); }
    var sw = document.getElementById("eqSwatch");
    if (sw) { sw.style.background = color; }
    var note = "CH " + (S.path + 1) + " · 20 Hz - 20 kHz · composite";
    if (EQ_OV.on) {
      var meas = eqOvMeas();
      drawEqOverlay(map, meas);
      note += meas ? " · meas " + meas.label : " · no measurement";
      note += " · target " + TGT.name;
    }
    var nt = document.getElementById("eqLegendNote");
    if (nt) { nt.textContent = note; }
  }

  // ---- the one tuning view: measurement + target on the composite canvas ----
  // The composite says what the DSP is DOING, the measurement says what the cabin IS
  // DOING, the target says what it SHOULD do - three lines on one grid, so tuning stops
  // being eye-alignment between two screens. Both extra layers are level-aligned (shape
  // only: there is no absolute SPL reference yet) and neither feeds the y-span, so the
  // EQ curve keeps its own readable +/- scale.
  var EQ_OV = { on: true };
  try { EQ_OV.on = localStorage.getItem("ds18.eqOv") !== "0"; } catch (e) {}

  // Alignment window: this channel's own crossover passband, falling back to the RTA
  // tab's 200 Hz - 5 kHz when no crossover info exists. Centering a tweeter's slice on
  // 200-5k would center its high-pass DIVE and shove the passband off the top of the
  // plot; the passband answers the question that matters - shape agreement where this
  // driver actually plays.
  function eqOvWin() {
    var ch = (TGT.chans && TGT.chans[S.path]) || null;
    return ch ? [Math.max(ch.hp || 20, 20), Math.min(ch.lp || 20000, 20000)] : [200, 5000];
  }

  // Mean dB over the alignment window.
  function eqOvMean(pts, win) {
    var s = 0, n = 0;
    for (var i = 0; i < pts.length; i++) {
      if (pts[i][0] >= win[0] && pts[i][0] <= win[1]) { s += pts[i][1]; n++; }
    }
    return n ? s / n : 0;
  }

  // Which measurement to draw: the live mic when its data is fresh (the EQ tab keeps the
  // poll running while the overlay is on), otherwise the tapped-or-latest saved capture.
  // Frequencies follow the daemon's 1/6-oct ladder - the same formula the RTA tab's
  // overlay fallback uses, so both tabs place a capture identically.
  function eqOvMeas() {
    var fresh = RTA.lastOk && (Date.now() - RTA.lastOk < 2000) &&
                RTA.data && RTA.data.bands && RTA.data.bands.length;
    if (fresh) {
      var b = RTA.data.bands, pts = [], i, j;
      for (i = 0; i < b.length; i++) {
        // average the <=10-frame poll history: one raw frame is speckle, not a curve
        var s = 0, n = 0;
        for (j = 0; j < RTA.hist.length; j++) {
          var row = RTA.hist[j];
          if (row && isFinite(row[i])) { s += row[i]; n++; }
        }
        var f = Number(b[i].f);
        if (!isFinite(f) || f <= 0) { f = 20 * Math.pow(2, i / 6); }
        pts.push([f, n ? s / n : Number(b[i].db)]);
      }
      return { pts: pts, label: "live" };
    }
    var idx = (RTA.overlay !== null && RTA.meas && RTA.meas[RTA.overlay]) ?
              RTA.overlay :
              (RTA.meas && RTA.meas.length ? RTA.meas.length - 1 : -1);
    if (idx >= 0 && RTA.meas[idx] && RTA.meas[idx].db && RTA.meas[idx].db.length) {
      var m = RTA.meas[idx], sp = [];
      for (var k = 0; k < m.db.length; k++) {
        sp.push([20 * Math.pow(2, k / 6), Number(m.db[k])]);
      }
      return { pts: sp, label: (m.name || rtaMeasDate(m.t)) };
    }
    return null;
  }

  // This channel's target: the house curve through its own crossover window - the same
  // slice the auto-EQ loop will aim at, drawn here so slider edits are aimed by it.
  function eqOvTgt() {
    var ch = (TGT.chans && TGT.chans[S.path]) || null;
    var pts = [];
    for (var i = 0; i <= 120; i++) {
      var f = 20 * Math.pow(1000, i / 120);   // log sweep, 20 Hz - 20 kHz
      var t = tgtAt(f);
      if (t === null) { continue; }
      if (ch) { t += tgtHpDb(f, ch.hp) + tgtLpDb(f, ch.lp); }
      pts.push([f, t]);
    }
    return pts;
  }

  function drawEqOverlay(map, meas) {
    if (!map) { return; }
    var cv = document.getElementById("eqCurve");
    var g = cv && cv.getContext ? cv.getContext("2d") : null;
    if (!g) { return; }
    var span = map.span;
    function X(f) {
      return map.padL + (Math.log(f) / Math.LN10 - map.lMin) / (map.lMax - map.lMin) * map.pw;
    }
    var win = eqOvWin();
    var mMean = meas ? eqOvMean(meas.pts, win) : 0;

    // measurement (near-white): its own passband mean rides the 0 dB line, so
    // deviations above / below it are what the EQ sliders should counteract.
    if (meas && meas.pts.length) {
      g.strokeStyle = "#e6ecf5";
      g.lineWidth = 2;
      g.lineJoin = "round";
      g.beginPath();
      for (var i = 0; i < meas.pts.length; i++) {
        var v = Math.max(-span, Math.min(span, meas.pts[i][1] - mMean));
        var x = X(meas.pts[i][0]), y = map.Y(v);
        if (i === 0) { g.moveTo(x, y); } else { g.lineTo(x, y); }
      }
      g.stroke();
    }

    // target (dashed violet, the app's target colour everywhere): centred on the same
    // 0 dB footing the measurement already sits on - its own passband mean - so the two
    // curves compare shape directly. Points outside the channel's crossover window break
    // the line instead of drawing the LR24 cliff: "no target here" must not read as
    // "target = -40".
    var tp = eqOvTgt();
    if (tp.length) {
      var shift = -eqOvMean(tp, win);
      g.strokeStyle = "#b38cff";
      g.lineWidth = 2;
      g.lineJoin = "round";
      g.setLineDash([6, 4]);
      g.beginPath();
      var run = false;
      for (var j = 0; j < tp.length; j++) {
        var tv = tp[j][1] + shift;
        if (tv < -span || tv > span) { run = false; continue; }
        var tx = X(tp[j][0]), ty = map.Y(tv);
        if (!run) { g.moveTo(tx, ty); run = true; } else { g.lineTo(tx, ty); }
      }
      g.stroke();
      g.setLineDash([]);
    }
  }

  // Live refresh: while the overlay is on and the EQ tab is in front, every poll lands on
  // the composite canvas too - move a slider, watch the measurement answer.
  function eqOvRefresh() {
    if (EQ_OV.on && document.getElementById("tab-eq").classList.contains("active")) {
      drawEqCurve();
    }
  }

  // Front-page curve mirrors whatever the bridge has loaded for the selected cell. It is
  // built from the reported band state, not the editable rows, so it stays truthful while
  // the user is on another tab or has half-typed a value in a box.
  function drawHomeCurve() {
    var bands = [];
    for (var i = 0; i < S.bandHz.length; i++) {
      bands.push({
        fc: S.bandHz[i],
        gain: S.bandGainDb[i] || 0,
        q: S.bandQ[i] || 4.3,
        enable: S.bandEnabled.length ? !!S.bandEnabled[i] : true
      });
    }
    var color = chColor(S.path);
    renderCurve("homeCurve", bands, !!S.byPass, "homeRange",
                { color: color, marker: color });
    var sw = document.getElementById("homeSwatch");
    if (sw) { sw.style.background = color; }
    var nt = document.getElementById("homeNote");
    if (nt) { nt.textContent = "CH " + (S.path + 1) + " · current EQ shape"; }
  }

  // Shared by the live EQ tab and the Tunes preview: only the target canvas, the band
  // set and the readout element differ between them.
  // Optional 5th arg opts = { ghosts:[{bands,byPass,weight,color}], custom:function(f),
  // color:"#", marker:"#" } lets the system view reuse this exact grid: ghosts draw one
  // dim curve per active channel, custom() replaces the composite (a power sum is not one
  // band list), and color/marker repaint the composite line and the per-band centre ticks
  // in the calling channel's hue. Four-argument callers behave exactly as before.
  function renderCurve(cvId, bands, byPass, rangeId, opts) {
    var cv = document.getElementById(cvId);
    if (!cv || !cv.getContext) { return; }
    var dpr = window.devicePixelRatio || 1;
    // Read the element's real height: the front-page curve is 150px and the EQ tab's is
    // 190px, and a hardcoded value squashes whichever one differs.
    var w = cv.clientWidth || 320, h = cv.clientHeight || 190;
    cv.width = Math.round(w * dpr);
    cv.height = Math.round(h * dpr);
    var g = cv.getContext("2d");
    g.setTransform(dpr, 0, 0, dpr, 0, 0);
    g.clearRect(0, 0, w, h);

    var padL = 34, padR = 8, padT = 10, padB = 18;
    var pw = w - padL - padR, ph = h - padT - padB;
    var fMin = EQ_FC_MIN, fMax = EQ_FC_MAX;
    var lMin = Math.log(fMin) / Math.LN10, lMax = Math.log(fMax) / Math.LN10;

    // 600 points, not one-per-pixel: with N=260 the readout reported +9.9 dB for a band
    // set to exactly +10, because no sample landed on the peak. Narrow AI bands (Q up to
    // 20 -> bandwidth fc/20) need the density too, or their peaks get sliced off.
    var N = 600, vals = [], lo = 0, hi = 0;
    var ghosts = (opts && opts.ghosts) ? opts.ghosts : [];
    var ghostVals = [];                       // [point][ghost] parallel to vals
    for (var i = 0; i <= N; i++) {
      var f = Math.pow(10, lMin + (lMax - lMin) * i / N);
      var v = opts && opts.custom ? opts.custom(f)
            : (byPass ? 0 : totalDb(bands, f));
      vals.push(v);
      if (v < lo) { lo = v; }
      if (v > hi) { hi = v; }
      var row = [];
      for (var gv = 0; gv < ghosts.length; gv++) {
        var gh = ghosts[gv];
        var gvv = gh.byPass ? 0 : (totalDb(gh.bands, f) + (gh.weight || 0));
        row.push(gvv);
        if (gvv < lo) { lo = gvv; }
        if (gvv > hi) { hi = gvv; }
      }
      ghostVals.push(row);
    }

    // Range: never tighter than +/-10 dB, otherwise a flat tune has nothing to see.
    var span = Math.max(Math.max(Math.abs(lo), Math.abs(hi)) * 1.15, 10);
    span = Math.ceil(span / 5) * 5;

    function X(f) { return padL + (Math.log(f) / Math.LN10 - lMin) / (lMax - lMin) * pw; }
    function Y(v) { return padT + (span - v) / (2 * span) * ph; }

    // horizontal dB grid
    g.font = "10px -apple-system, 'Segoe UI', Roboto, sans-serif";
    g.textAlign = "right";
    g.textBaseline = "middle";
    var step = span <= 15 ? 5 : (span <= 40 ? 10 : 20);
    for (var d = -span; d <= span; d += step) {
      var y = Y(d);
      g.strokeStyle = (d === 0) ? "#3a3f49" : "#1c2027";
      g.lineWidth = (d === 0) ? 1.4 : 1;
      g.beginPath(); g.moveTo(padL, y); g.lineTo(padL + pw, y); g.stroke();
      g.fillStyle = (d === 0) ? "#9098a5" : "#7c828b";
      g.fillText((d > 0 ? "+" : "") + d, padL - 5, y);
    }

    // vertical log-frequency grid
    var ticks = [20, 30, 50, 100, 200, 300, 500, 1000, 2000, 3000, 5000, 10000, 20000];
    var majors = { 20: 1, 50: 1, 100: 1, 500: 1, 1000: 1, 5000: 1, 10000: 1, 20000: 1 };
    g.textAlign = "center";
    g.textBaseline = "top";
    ticks.forEach(function (t) {
      var x = X(t);
      g.strokeStyle = majors[t] ? "#23272f" : "#171a20";
      g.lineWidth = 1;
      g.beginPath(); g.moveTo(x, padT); g.lineTo(x, padT + ph); g.stroke();
      if (majors[t]) {
        g.fillStyle = "#7c828b";
        g.fillText(t >= 1000 ? (t / 1000) + "k" : String(t), x, padT + ph + 4);
      }
    });

    // per-band centre markers: where the tune put its filters and how deep each one cuts.
    // They wear the channel's colour and ride live on the grid's values, so editing a
    // band's frequency slides its marker across the plot.
    if (!byPass) {
      var mcLine = opts.marker ? hexA(opts.marker, 0.35) : "rgba(132,191,93,.35)";
      var mcDot = opts.marker ? hexA(opts.marker, 0.85) : "rgba(132,191,93,.85)";
      for (var b = 0; b < bands.length; b++) {
        var bd = bands[b];
        if (bd.enable === false || !bd.gain) { continue; }
        var bx = X(bd.fc), by = Y(bd.gain);
        g.strokeStyle = mcLine;
        g.lineWidth = 1;
        g.beginPath(); g.moveTo(bx, Y(0)); g.lineTo(bx, by); g.stroke();
        g.fillStyle = mcDot;
        g.beginPath(); g.arc(bx, by, 2.4, 0, 6.2832); g.fill();
      }
    }

    // per-channel ghosts (system view only): dim, drawn under the composite so the
    // summed system response stays the loudest line on the plot.
    for (var gi = 0; gi < ghosts.length; gi++) {
      g.strokeStyle = ghosts[gi].color || "rgba(124,130,139,.5)";
      g.lineWidth = 1;
      g.lineJoin = "round";
      g.beginPath();
      for (var gk = 0; gk <= N; gk++) {
        var gx = padL + (gk / N) * pw, gy = Y(ghostVals[gk][gi]);
        if (gk === 0) { g.moveTo(gx, gy); } else { g.lineTo(gx, gy); }
      }
      g.stroke();
    }

    // composite response - the channel's own colour when the caller gave one
    g.strokeStyle = byPass ? "#7c828b" : ((opts && opts.color) ? opts.color : "#0babf2");
    g.lineWidth = 2;
    g.lineJoin = "round";
    g.beginPath();
    for (var k = 0; k <= N; k++) {
      var xk = padL + (k / N) * pw, yk = Y(vals[k]);
      if (k === 0) { g.moveTo(xk, yk); } else { g.lineTo(xk, yk); }
    }
    g.stroke();

    var rng = document.getElementById(rangeId);
    if (rng) {
      rng.textContent = byPass
        ? "bypassed — 0.0 dB"
        : (lo >= 0 ? "+" : "") + lo.toFixed(1) + " … +" + hi.toFixed(1) + " dB";
    }

    // The EQ tab composites two more layers onto this exact grid (measurement + target);
    // hand back the mapping this draw used so the overlays land pixel-true.
    return { X: X, Y: Y, padL: padL, padT: padT, pw: pw, ph: ph,
             lMin: lMin, lMax: lMax, span: span };
  }

  var eqRows = [];

  // The grid is built row-major so the CSS grid lines up the bands: numbers, Q, Gain,
  // vertical sliders, Freq, then the bypass boxes - the same order the vendor page uses.
  // eqRows keeps one object per band (Q / Gain / Freq / bypass / slider) so collectEq(),
  // renderEq() and flatEq() can stay band-indexed.
  function buildEq() {
    var grid = document.getElementById("eqGrid");
    if (!grid) { return; }
    grid.innerHTML = "";
    eqRows = [];
    var i;

    function label(text) {
      var l = document.createElement("div");
      l.className = "lbl";
      l.textContent = text;
      grid.appendChild(l);
    }
    function numCell(cls, min, max, step, title) {
      var inp = document.createElement("input");
      inp.type = "number";
      inp.className = cls;
      inp.min = min; inp.max = max; inp.step = step;
      inp.inputMode = "numeric";
      inp.title = title;
      grid.appendChild(inp);
      return inp;
    }

    label("");                                        // band numbers
    for (i = 0; i < 31; i++) {
      var hd = document.createElement("div");
      hd.className = "hd";
      hd.textContent = i + 1;
      grid.appendChild(hd);
      eqRows.push({});
    }

    label("Q Value");
    for (i = 0; i < 31; i++) {
      eqRows[i].qq = numCell("qq", EQ_Q_MIN, EQ_Q_MAX, 0.1, "Q");
    }

    label("Gain");
    for (i = 0; i < 31; i++) {
      eqRows[i].db = numCell("db", EQ_GAIN_MIN, EQ_GAIN_MAX, 0.1, "Gain, dB");
    }

    label("");                                        // vertical gain sliders
    for (i = 0; i < 31; i++) {
      var wrap = document.createElement("div");
      wrap.className = "vslider";
      var sl = document.createElement("input");
      sl.type = "range";
      sl.min = EQ_GAIN_MIN; sl.max = EQ_GAIN_MAX; sl.step = 0.1;
      sl.title = "Gain, dB (drag)";
      wrap.appendChild(sl);
      grid.appendChild(wrap);
      eqRows[i].sl = sl;
      eqRows[i].slw = wrap;
    }

    label("Freq.");
    for (i = 0; i < 31; i++) {
      eqRows[i].hz = numCell("hz", EQ_FC_MIN, EQ_FC_MAX, 1, "Centre frequency, Hz");
    }

    label("Bypass");
    for (i = 0; i < 31; i++) {
      var cb = document.createElement("input");
      cb.type = "checkbox";
      cb.checked = true;
      cb.title = "Bypass this band";
      grid.appendChild(cb);
      eqRows[i].en = cb;
    }

    for (i = 0; i < 31; i++) {
      (function (idx, r) {
        function setOff(off) {
          r.qq.classList.toggle("off", off);
          r.db.classList.toggle("off", off);
          r.hz.classList.toggle("off", off);
          r.slw.classList.toggle("off", off);
        }
        setOff(!r.en.checked);
        r.en.addEventListener("change", function () {
          setOff(!r.en.checked);
          drawEqCurve();
        });
        // Live redraw: the curve is the feedback for typing a band, so it must follow
        // each keystroke rather than waiting for Apply.
        ["hz", "db", "qq"].forEach(function (k) {
          r[k].addEventListener("input", drawEqCurve);
        });
        // The slider mirrors the Gain box: type for precision, drag for feel. Setting
        // .value programmatically fires no input event, so the two cannot loop.
        r.sl.addEventListener("input", function () {
          r.db.value = (Math.round(parseFloat(r.sl.value) * 10) / 10).toFixed(1);
          drawEqCurve();
        });
        r.db.addEventListener("input", function () {
          var v = parseFloat(r.db.value);
          if (!isNaN(v)) { r.sl.value = clampNum(v, EQ_GAIN_MIN, EQ_GAIN_MAX); }
        });
      })(i, eqRows[i]);
    }

    renderEq();
  }

  function renderEq() {
    for (var i = 0; i < 31; i++) {
      var r = eqRows[i];
      if (!r) { continue; }
      var fc = (S.bandHz.length > i && S.bandHz[i]) ? S.bandHz[i] : EQ_DEFAULT_HZ[i];
      var g = S.bandGainDb.length > i ? S.bandGainDb[i] : 0;
      var q = S.bandQ.length > i ? S.bandQ[i] : 1.0;
      var on = S.bandEnabled.length > i ? S.bandEnabled[i] : true;
      r.hz.value = fc;
      r.db.value = (Math.round(g * 10) / 10).toFixed(1);
      r.qq.value = (Math.round(q * 10) / 10).toFixed(1);
      r.sl.value = clampNum(g, EQ_GAIN_MIN, EQ_GAIN_MAX);
      r.en.checked = !!on;
      r.qq.classList.toggle("off", !on);
      r.db.classList.toggle("off", !on);
      r.hz.classList.toggle("off", !on);
      r.slw.classList.toggle("off", !on);
    }
    document.getElementById("eqBypass").classList.toggle("on", S.byPass);
    drawEqCurve();
    drawHomeCurve();
  }

  // Read the fields back out, clamping to the DP12_Param limits. Returns null if any
  // band is incomplete so a half-typed row cannot silently send garbage.
  function collectEq() {
    var bands = [];
    for (var i = 0; i < 31; i++) {
      var r = eqRows[i];
      var fcRaw = parseFloat(r.hz.value);
      var dbRaw = parseFloat(r.db.value);
      var qRaw = parseFloat(r.qq.value);
      if (isNaN(fcRaw) || isNaN(dbRaw) || isNaN(qRaw)) {
        toast("Band " + (i + 1) + " needs a number in every box");
        return null;
      }
      bands.push({
        fc: Math.round(clampNum(fcRaw, EQ_FC_MIN, EQ_FC_MAX)),
        gain: clampNum(dbRaw, EQ_GAIN_MIN, EQ_GAIN_MAX),
        q: clampNum(qRaw, EQ_Q_MIN, EQ_Q_MAX),
        type: 0,
        enable: r.en.checked
      });
    }
    return bands;
  }

  // While sweep mode is on the playing memory is the flat copy: a tune write here would land on
  // top of it (and be undone by the restore). Refuse rather than guess.
  function sweepBlocksTuneWrite(what) {
    if (!SWEEP.on) { return false; }
    var mem = (SWEEP.snap && typeof SWEEP.snap.mem === "number") ? SWEEP.snap.mem : S.mem;
    toast("sweep mode is on — " + what + " would land on the flattened memory (memory "
          + (mem + 1) + "). Switch sweep mode off first.");
    log("blocked: " + what + " while sweep mode is on (memory " + (mem + 1) + ")");
    return true;
  }

  function applyEq() {
    if (sweepBlocksTuneWrite("writing an EQ cell")) { return; }
    // Never transmit a cell that has not been read this session: the grid would be
    // shipping the boot defaults over the cell's real contents (see S.pathRead).
    if (!S.pathRead) {
      toast("reading memory " + (S.mem + 1) + " / output " + S.path +
            " from the device first — check it, then apply again");
      log("apply blocked: cell not read yet -> reading");
      readEq(false);
      return;
    }
    var bands = collectEq();
    if (!bands) { return; }
    api("/api/eq", { mem: S.mem, path: S.path, byPass: S.byPass, bands: bands })
      .then(function (d) {
        if (!d.ok) { toast(d.error); return; }
        S.bandHz = bands.map(function (b) { return b.fc; });
        S.bandGainDb = bands.map(function (b) { return b.gain; });
        S.bandQ = bands.map(function (b) { return b.q; });
        S.bandEnabled = bands.map(function (b) { return b.enable; });
        toast(d.acked ? "EQ sent" : "sent (no reply)");
        log("eq tx " + d.txBytes + " bytes, mem " + (S.mem + 1) + " path " + S.path);
        // System view: patch the written cell in the cache instead of re-reading all
        // seventeen paths over USB - only a memory switch or auto mode re-reads.
        if (SYS.cells && SYS.mem === S.mem) {
          for (var ci = 0; ci < SYS.cells.length; ci++) {
            if (SYS.cells[ci].path === S.path) {
              SYS.cells[ci] = { path: S.path, ok: true, byPass: !!S.byPass, bands: bands };
            }
          }
          sysDraw();
        } else if (SYS.auto) { sysRefresh(); }
        baseInvalidate();          // the last baseline read is now a statement about the past
      }).catch(function (e) { toast(String(e)); });
  }

  // "Flat all" means the WHOLE memory, not the one channel on screen: the measurement loop
  // steps channel to channel, so a curve left untouched on the next one would show up as
  // that driver's response when it is really the tune's.
  //
  // A 0 dB peaking band is an exact identity (b == a), so zeroing all 31 gives a genuinely
  // flat response at every frequency regardless of the centre and Q left in the cell.
  //
  // Writes go out one at a time: the firmware ACKs nothing, and overlapping requests
  // interleave telemetry. 17 paths x ~800 ms is therefore a ~14 s operation - the counter
  // in the channel panel says which path it is on rather than leaving a dead button.
  var flatBusy = false;
  function flatEq() {
    if (sweepBlocksTuneWrite("flattening the EQ")) { return; }
    if (!S.connected) { toast("connect first"); return; }
    if (flatBusy) { toast("a flatten is already running"); return; }

    for (var i = 0; i < 31; i++) {
      if (eqRows[i]) {
        eqRows[i].db.value = "0.0";
        eqRows[i].sl.value = 0;
      }
    }
    drawEqCurve();

    flatBusy = true;
    var btn = document.getElementById("eqFlat");
    var last = document.getElementById("chLast");
    if (btn) { btn.classList.add("on"); }

    var paths = [], p, b;
    for (p = 0; p <= 16; p++) { paths.push(p); }        // 0..15 outputs + 16 master chain
    var bands = [];
    for (b = 0; b < 31; b++) {
      bands.push({ fc: EQ_DEFAULT_HZ[b], gain: 0, q: 4.3, enable: true });
    }
    var ok = 0, bad = 0;

    function step(k) {
      if (k >= paths.length) {
        flatBusy = false;
        if (btn) { btn.classList.remove("on"); }
        var msg = "flattened " + ok + "/" + paths.length + (bad ? ", " + bad + " failed" : "");
        if (last) { last.textContent = msg; }
        toast(bad ? "flatten incomplete" : "memory " + (S.mem + 1) + " flattened");
        log("flatten mem " + (S.mem + 1) + ": " + msg);
        readEq(true);
        baseInvalidate();
        return;
      }
      if (last) { last.textContent = "flattening path " + paths[k] + " (" + k + "/" + paths.length + ")"; }
      api("/api/eq", { mem: S.mem, path: paths[k], byPass: false, bands: bands })
        .then(function (r) { if (r && r.ok) { ok++; } else { bad++; } })
        .catch(function () { bad++; })
        .then(function () { step(k + 1); });
    }
    step(0);
  }

  // Headline band capacity, decided by the probe rather than by assumption.
  //   independent + master distinct -> 31 output + 31 master = 62 in series
  //   independent, no master        -> 31 per output, nothing stacked on top
  //   outputs not independent       -> one shared curve, per-driver EQ unavailable
  function setCapacity(p) {
    var el = document.getElementById("eqCapacity");
    if (!el || !p) { return; }
    var txt, cls;
    if (p.outputsIndependent && p.masterEqDistinct) {
      txt = "Capacity: 62 bands per output - 31 output EQ (verified, per channel) plus a " +
            "distinct 31-band master chain at path 16 (per the vendor's data model it " +
            "sits in series; not yet proven by listening).";
      cls = "capacity good";
    } else if (p.outputsIndependent) {
      txt = "Capacity: 31 bands per output. No separate master EQ (the master chain matches output 1).";
      cls = "capacity good";
    } else {
      txt = "Capacity: the outputs are not independent of each other - one shared curve, " +
            "so per-driver EQ is not available on this unit.";
      cls = "capacity bad";
    }
    el.textContent = txt;
    el.className = cls;
  }

  // Per-path evidence, so a result can be re-checked by eye rather than trusted blindly.
  // "*" marks the reference (path 0), "=" marks a reply byte-identical to it, "!" a
  // body whose length is not 1044.
  function probeTable(d) {
    var h = d.hashes || [], s = d.sizes || [];
    var lines = ["path  bytes  hash        flag"];
    for (var p = 0; p < h.length; p++) {
      if (!s[p]) { lines.push(pad(p, 4) + "  (no reply)"); continue; }
      var flag = p === 0 ? "*" : (h[p] === h[0] ? "=" : "!");
      if (s[p] !== s[0]) { flag += "!"; }
      lines.push(pad(p, 4) + pad(s[p], 6) + "  " +
                 ("00000000" + h[p].toString(16)).slice(-8) + "  " + flag);
    }
    return lines.join("\n");
  }
  function pad(v, n) { v = String(v); while (v.length < n) { v = " " + v; } return v; }

  function probePaths() {
    var out = document.getElementById("probeOut");
    var btn = document.getElementById("btnProbe");
    out.className = "probeout";
    out.textContent = "probing 17 paths... this is 17 USB round trips, please wait";
    btn.disabled = true;
    api("/api/eqprobe", {}).then(function (d) {
      btn.disabled = false;
      if (!d.ok) { out.className = "probeout bad"; out.textContent = d.error; return; }
      out.textContent = d.summary + "\n\n" + probeTable(d);
      // Colour by the answer that actually matters: are outputs independent?
      out.className = "probeout " + (d.outputsIndependent ? "good" : "bad");
      setCapacity(d);
      log("probe: " + d.summary);
    }).catch(function (e) {
      btn.disabled = false;
      out.className = "probeout bad";
      out.textContent = "probe failed: " + e;
    });
  }

  function readEq(quiet) {
    var m = S.mem, p = S.path;                 // discard responses that land after a switch
    api("/api/eqread", { mem: m, path: p }).then(function (d) {
      if (!d.ok) { toast(d.error); log("eqread: " + d.error); return; }
      if (S.mem !== m || S.path !== p) { return; }
      S.byPass = d.byPass;
      S.bandHz = d.bands.map(function (b) { return b.fc; });
      S.bandGainDb = d.bands.map(function (b) { return b.gain; });
      S.bandQ = d.bands.map(function (b) { return (typeof b.q === "number") ? b.q : 1.0; });
      S.bandEnabled = d.bands.map(function (b) { return (typeof b.enable === "boolean") ? b.enable : true; });
      S.pathRead = true;                       // grid now shows THIS cell's real contents
      renderEq();
      if (!quiet) { toast("EQ read ok"); }   // channel taps re-read too often to announce
    }).catch(function (e) { toast(String(e)); });
  }

  // ---------- input ----------
  var inChecks = [];
  function buildInput() {
    var host = document.getElementById("inChecks");
    host.innerHTML = "";
    inChecks = [];
    for (var i = 0; i < 16; i++) {
      var l = document.createElement("label");
      l.innerHTML = '<input type="checkbox" checked><span>' + i + '</span>';
      host.appendChild(l);
      inChecks.push(l.querySelector("input"));
    }
  }

  function applyInput() {
    var enable = 0;
    for (var i = 0; i < 16; i++) { if (inChecks[i].checked) enable |= (1 << i); }
    var revert = parseInt(document.getElementById("inRevert").value, 10) >>> 0;
    api("/api/input", { enable: enable & 0xFFFF, revert: revert & 0xFFFF, mem: S.mem, source: S.source })
      .then(function (d) {
        if (!d.ok) { toast(d.error); return; }
        toast(d.acked ? "input sent" : "sent (no reply)");
        log("input tx=" + d.txHex);
      }).catch(function (e) { toast(String(e)); });
  }

  // ---------- system view (all active channels, summed) ----------
  // The EQ tab shows ONE output's curve; this shows what the whole system is asking the
  // room for: every un-muted output's EQ chain in the memory, each weighted by its output
  // level where the bridge has one (cmd 11 mirror — the DSP cannot be asked), power-summed
  // (incoherent: independent drivers add in power, not dB), with the master chain (output
  // 16) cascaded on top. Group gains are broadband and don't change shape, so they are
  // not applied; per-output delay/polarity are phase, invisible to a magnitude sum.
  var SYS = { cells: null, n: 0, auto: false, last: 0, mem: -1, busy: false };
  // "#rrggbb" + alpha -> "rgba(r,g,b,a)" - canvas wants the latter for dimmed layers.
  function hexA(hex, a) {
    var n = parseInt(hex.slice(1), 16);
    return "rgba(" + ((n >> 16) & 255) + "," + ((n >> 8) & 255) + "," +
           (n & 255) + "," + a + ")";
  }
  // THE channel palette: output N wears this hue everywhere it appears - rail dot,
  // selected rail button, EQ curve + band markers, grid accents, tune preview, ghosts.
  var CH_COLORS = ["#845fbf", "#c98a3b", "#3aa0a0", "#c1577c",
                    "#7a9e3f", "#5f8fd6", "#c96a3b", "#9c7bb8"];

  function sysRefresh() {
    if (SYS.busy) { return; }               // one read chain at a time (auto + EQ tab)
    if (!S.connected) { toast("connect first"); return; }
    SYS.busy = true;
    var n = chVisible();
    var paths = [];
    for (var i = 0; i < n; i++) { paths.push(i); }
    paths.push(16);                                   // master chain rides on everything
    var cells = [], idx = 0;
    var info = document.getElementById("sysInfo");
    info.textContent = "reading " + paths.length + " paths...";
    (function step() {
      if (idx >= paths.length) {
        SYS.cells = cells; SYS.n = n; SYS.mem = S.mem; SYS.last = Date.now();
        SYS.busy = false; sysDraw(); drawEqCurve(); return;
      }
      var p = paths[idx];
      api("/api/eqread", { mem: S.mem, path: p }).then(function (d) {
        cells.push({ path: p, ok: !!(d && d.ok), byPass: !!(d && d.byPass),
                     bands: (d && d.bands) || [] });
        idx++;
        info.textContent = "reading... " + idx + "/" + paths.length;
        step();
      }).catch(function (e) {
        cells.push({ path: p, ok: false, byPass: false, bands: [] });
        log("sysview path " + p + ": " + e);
        idx++;
        step();
      });
    })();
  }

  function sysCell(p) {
    if (!SYS.cells) { return null; }
    for (var i = 0; i < SYS.cells.length; i++) {
      if (SYS.cells[i].path === p) { return SYS.cells[i]; }
    }
    return null;
  }

  // ---------- measurement baseline: is a tune standing in front of the mic? ----------
  // Every tone this app plays for a measurement has to arrive at a DSP whose EQ is out of circuit
  // for THIS memory, otherwise the curve is a measurement of the tune. That has to be established
  // by ASKING THE DEVICE - not by remembering which chip was pressed, and not by trusting the
  // grid, which holds boot defaults until a cell has been read.
  //
  // /api/eqread is a plain cmd 14 read, so looking costs nothing and writes nothing. Neutral means
  // either of two honest states, and the difference is only whether the curve is still on the chip:
  //     flat      every enabled band at 0.0 dB   (what "Flatten memory" produces)
  //     bypassed  the block's byPass bit is set  (the curve is intact, just out of circuit)
  var BASE = { mem: -1, cells: null, at: 0, busy: false, tried: 0 };
  var BASELINE_RETRY_MS = 10000;   // a dead DSP must not be re-read once a second
  var BAND_TOLERANCE_DB = 0.05;    // a band that reads 0.0 but not exactly 0 is still flat

  function baseRead(force) {
    if (BASE.busy) { return; }
    if (!S.connected) { BASE.cells = null; BASE.mem = -1; baseRender(); return; }
    if (!force && BASE.mem === S.mem && BASE.cells) { return; }
    if (!force && (Date.now() - BASE.tried) < BASELINE_RETRY_MS) { return; }
    BASE.busy = true;
    BASE.tried = Date.now();
    var n = chVisible(), paths = [], i;
    for (i = 0; i < n; i++) { paths.push(i); }
    paths.push(16);                       // the master chain rides on every output
    var mem = S.mem, cells = [], idx = 0;
    (function step() {
      if (idx >= paths.length) {
        BASE.cells = cells; BASE.mem = mem; BASE.at = Date.now(); BASE.busy = false;
        baseRender();
        rosterPaintBands();
        return;
      }
      var p = paths[idx];
      api("/api/eqread", { mem: mem, path: p }).then(function (d) {
        cells.push(baseCell(p, d));
        idx++; step();
      }).catch(function () {
        cells.push({ path: p, ok: false, byPass: false, flat: false, worst: null, off: 0 });
        idx++; step();
      });
    })();
  }

  // One path's verdict, straight from a cmd 14 read.
  function baseCell(path, d) {
    var c = { path: path, ok: !!(d && d.ok), byPass: !!(d && d.byPass),
              flat: false, worst: null, off: 0, lo: 0, hi: 0 };
    // The same read carries the path's crossover pair. Keep it: it costs nothing here and it
    // is the only live evidence of whether a tweeter still has a high-pass in front of it.
    if (d && d.passband) {
      c.lo = Number(d.passband.lowFc);
      c.hi = Number(d.passband.highFc);
      if (!isFinite(c.lo) || c.lo < 0) { c.lo = 0; }
      if (!isFinite(c.hi) || c.hi < 0) { c.hi = 0; }
    }
    if (!c.ok || !d.bands || !d.bands.length) { return c; }
    var worst = 0, k;
    for (k = 0; k < d.bands.length; k++) {
      var b = d.bands[k];
      if (!b || b.enable === false) { continue; }   // a disabled band cannot shape anything
      var g = Number(b.gain);
      if (!isFinite(g)) { g = 0; }
      if (Math.abs(g) > Math.abs(worst)) { worst = g; }
      if (Math.abs(g) > BAND_TOLERANCE_DB) { c.off++; }
    }
    c.worst = Math.round(worst * 10) / 10;
    c.flat = (c.off === 0);
    return c;
  }

  // The crossover the device is actually running on one output, from the cmd 14 read the
  // baseline check already does. null = not read yet (or the path did not answer).
  function basePass(path) {
    if (!BASE.cells || BASE.mem !== S.mem) { return null; }
    for (var i = 0; i < BASE.cells.length; i++) {
      var c = BASE.cells[i];
      if (c.path === path && c.ok) { return { lo: c.lo || 0, hi: c.hi || 0 }; }
    }
    return null;
  }

  // Full range is a real measurement for a cone and a lie for a dome: below its rated floor a
  // 1" tweeter makes no sound, only heat, and a 20 Hz - 20 kHz sweep hands it the whole band.
  // So a tweeter run needs its own high-pass read back from the chip first. This is the one
  // check the app refuses to take on trust, because the failure is a burnt voice coil rather
  // than a bad number in a table.
  function sweepBandProblem(job) {
    var b = rosterBand(job.type);
    if (!b || job.type !== "Tweeter") { return null; }
    var pb = basePass(job.ch - 1);
    if (!pb) { return "its crossover has not been read back yet"; }
    if (!(pb.lo > 0)) { return "its high-pass is OPEN — the sweep would start at 20 Hz"; }
    if (pb.lo + 1 < b[0]) {
      return "its high-pass is " + fmtHz(pb.lo) + " Hz, under its " + fmtHz(b[0]) + " Hz rating";
    }
    return null;
  }

  // Paint the band chip red where the readback proves the run would be unsafe. Unknown is not
  // red: at boot and with the device unplugged there is no readback yet, and eight alarms on a
  // page that has not talked to the DSP would teach the owner to ignore the one that matters.
  function rosterPaintBands() {
    var rows = document.querySelectorAll("#rosterRows .rrow");
    if (!rows || !rows.length) { return; }
    for (var i = 0; i < rows.length; i++) {
      var ch = parseInt(rows[i].getAttribute("data-ch"), 10) || 0;
      var chip = rows[i].querySelector(".rband");
      if (!chip || !ch) { continue; }
      var job = rosterJobFor(ch), bad = false;
      if (job && job.type === "Tweeter") {
        var b = rosterBand(job.type), pb = basePass(job.ch - 1);
        bad = !!(b && pb && (!(pb.lo > 0) || (pb.lo + 1) < b[0]));
      }
      chip.className = "rband" + (bad ? " open" : "");
    }
  }

  function baseVerdict() {
    if (!S.connected) { return { state: "offline" }; }
    if (!BASE.cells || BASE.mem !== S.mem) { return { state: "unknown" }; }
    var tuned = [], failed = [], bypassed = 0, flat = 0, i, c;
    for (i = 0; i < BASE.cells.length; i++) {
      c = BASE.cells[i];
      if (!c.ok) { failed.push(c.path); continue; }
      if (c.byPass) { bypassed++; continue; }
      if (c.flat) { flat++; continue; }
      tuned.push(c.path + (c.worst === null ? "" : (" (" + (c.worst > 0 ? "+" : "") + c.worst + " dB)")));
    }
    if (failed.length) { return { state: "unknown", failed: failed }; }
    if (tuned.length) { return { state: "tuned", tuned: tuned }; }
    return { state: "neutral", flat: flat, bypassed: bypassed };
  }

  function baseRender() {
    var el = document.getElementById("baseState");
    if (!el) { return; }
    var b = baseVerdict(), txt, cls = "";
    if (b.state === "neutral") {
      txt = "flat — " + b.flat + " path" + (b.flat === 1 ? "" : "s") + " at 0 dB"
            + (b.bypassed ? ", " + b.bypassed + " bypassed" : "");
      cls = "ok";
    } else if (b.state === "tuned") {
      txt = "tuned — EQ in circuit on path " + b.tuned.join(", ");
      cls = "bad";
    } else if (b.state === "offline") {
      txt = "DSP not connected";
    } else {
      txt = "not read yet";
    }
    el.textContent = txt;
    el.className = "v " + cls;
  }

  // Any EQ write, tune load or memory switch makes the last read a statement about the past.
  function baseInvalidate() { BASE.cells = null; BASE.mem = -1; BASE.tried = 0; baseRender(); }

  // A hand Capture plays a tone exactly like a run does, so it obeys the same rule. The one
  // exception is honest: with the DSP disconnected there is no tune to colour anything, so a
  // microphone-only capture is still allowed to happen.
  function baseAllowTone() {
    if (!S.connected) { return true; }
    var b = baseVerdict();
    if (b.state === "neutral") { return true; }
    if (b.state === "tuned") {
      toast("memory " + (S.mem + 1) + " has EQ in circuit on path " + b.tuned.join(", ")
        + " — a measurement through the tune measures the tune. Flatten or bypass it on the EQ tab, "
        + "then Re-read.");
    } else {
      toast("the tune has not been read for memory " + (S.mem + 1) + " yet — reading it now, "
        + "press Capture again when it says flat");
      baseRead(true);
    }
    return false;
  }

  // The check row itself. "unknown" fails on purpose: not having looked is not the same as being
  // neutral, and a run that plays a tone without having looked is the exact mistake this is for.
  function baseCheckRow() {
    var b = baseVerdict();
    if (b.state === "neutral") {
      return { k: "no tune in the way", s: "ok",
               v: "memory " + (S.mem + 1) + ": " + (b.flat + b.bypassed) + " paths neutral ("
                  + b.flat + " flat" + (b.bypassed ? ", " + b.bypassed + " bypassed" : "") + ")" };
    }
    if (b.state === "tuned") {
      return { k: "no tune in the way", s: "fail",
               v: "memory " + (S.mem + 1) + " still has EQ in circuit on path " + b.tuned.join(", ")
                  + " — a response measured through the tune is a measurement of the tune. Flatten "
                  + "or bypass it on the EQ tab, then press Re-read." };
    }
    if (b.state === "offline") {
      return { k: "no tune in the way", s: "fail",
               v: "the DSP is not connected, so the tune cannot be read" };
    }
    return { k: "no tune in the way", s: "fail",
             v: "not read yet for memory " + (S.mem + 1) + " — press Re-read in the Measure card" };
  }

  // ---------- sweep mode: the live memory, flat, at a level that cannot hurt a tweeter ----------
  // The owner's rule: a sweep never measures the tune, and the master drops to 80% of its travel
  // (-8.0 dB) so a full-scale ramp cannot arrive the way the tune's own balance never would.
  //
  // The first design selected a second memory that is the tune with zeroed EQ. That does not
  // work: measured on the rig (2026-10-19, amp attached), cmd 12 carries the memory byte, the
  // device applies the level from the same frame, and the active memory does NOT change - a
  // byte-identical cmd 14 write and a cmd 11 write addressed to the other memory changed nothing
  // either. Every earlier "device is on memory N" came from /api/info, which is the bridge's own
  // mirror (DoMaster stores mem before replying, so it can only echo what was sent). /api/status
  // is the only readback that asks the device, and it says the memory never moved. See
  // DS18_DSP_protocol.md section 37.
  //
  // So sweep mode flattens the memory that is playing, and puts it back afterwards from a
  // snapshot taken first. Nothing is written until all seventeen cells are in hand, so a read
  // that fails leaves the tune untouched.
  //
  // Crossovers are NOT touched. /api/eqread returns the passband's edges and slopes but not its
  // enable flag, and /api/passband cannot restore a flag it was never told - so a crossover
  // change could not be undone exactly. Zero-gain EQ bands are an exact identity at every
  // frequency, which is what makes an EQ-only flatten safe.
  //
  // The hard revert, if anything ever goes wrong, is the bank on disk:
  //   load-tune-bank.ps1 -File web\bor8f3.idsp -Play 1
  var SWEEP = { on: false, busy: false, snap: null, err: null, pct: 80 };
  var SWEEP_KEY = "ds18.sweepSnap";
  var SWEEP_PCT_KEY = "ds18.sweepPct";

  function sweepLevelDb(pct) {          // 0 % = -40 dB (silent), 100 % = 0 dB (full level)
    var p = Math.max(0, Math.min(100, Number(pct) || 0));
    return Math.round((-40 + 40 * p / 100) * 10) / 10;
  }

  // One cmd 12 that changes the LEVEL and nothing else. Everything else in the frame comes from the
  // device itself (`/api/status`), never from the page's cached S: a stale mute flag in the page
  // unmutes the car - which is exactly what a stale tab did on 2026-10-19, twice over - and a stale
  // group or source would move those too. The bridge fills any missing field from its own mirror,
  // and that mirror is only as fresh as the last write, so the device has to be asked.
  function sweepSwitch(mainDb) {
    return api("/api/status", {}).then(function (d) {
      if (!d || !d.ok) { throw new Error("the device did not answer a status read"); }
      return api("/api/master", {
        mainDb: mainDb,
        mute: !!d.mute,
        groups: (d.groupDb && d.groupDb.length === 4) ? d.groupDb : [0, 0, 0, 0],
        mem: (typeof d.mem === "number") ? d.mem : S.mem,
        source: (typeof d.source === "number") ? d.source : S.source
      });
    });
  }

  // 0..15 outputs plus 16, the master chain - the same set "Flatten memory" writes.
  function sweepPaths() {
    var out = [], i;
    for (i = 0; i <= 16; i++) { out.push(i); }
    return out;
  }

  function sweepBands(bands, gain) {
    var out = [];
    for (var i = 0; i < bands.length; i++) {
      out.push({ fc: bands[i].fc, gain: gain, q: bands[i].q, enable: true });
    }
    return out;
  }

  function sweepEqWrite(mem, path, byPass, bands) {
    return api("/api/eq", { mem: mem, path: path, byPass: !!byPass, bands: bands });
  }

  // Both the byPass bit and every band, within the tolerances the loader itself verifies with
  // (gain 0.061 dB, Q 0.051 - the encode/decode step of the wire format, not slop).
  function sweepEqSame(a, b) {
    if (!a || !b || a.length !== b.length) { return false; }
    for (var i = 0; i < a.length; i++) {
      if (Math.abs(Number(a[i].fc) - Number(b[i].fc)) > 0.5) { return false; }
      if (Math.abs(Number(a[i].gain) - Number(b[i].gain)) > 0.061) { return false; }
      if (Math.abs(Number(a[i].q) - Number(b[i].q)) > 0.051) { return false; }
      if ((a[i].enable !== false) !== (b[i].enable !== false)) { return false; }
    }
    return true;
  }

  // Read-only: how far this path still is from flat. Used to prove the flatten landed.
  function sweepFlatness(cell) {
    if (!cell || !cell.ok) { return "path " + (cell ? cell.path : "?") + " unreadable"; }
    if (cell.byPass) { return null; }
    var worst = 0;
    for (var i = 0; i < cell.bands.length; i++) {
      if (cell.bands[i].enable === false) { continue; }
      worst = Math.max(worst, Math.abs(Number(cell.bands[i].gain) || 0));
    }
    return (worst > 0.061) ? ("path " + cell.path + " carries " + worst.toFixed(1) + " dB") : null;
  }

  function sweepReadCell(mem, path) {
    return api("/api/eqread", { mem: mem, path: path }).then(function (d) {
      if (!d || !d.ok || !d.bands || !d.bands.length) {
        throw new Error("path " + path + " could not be read back");
      }
      return { path: path, ok: true, byPass: !!d.byPass,
               bands: d.bands.map(function (b) {
                 return { fc: b.fc, gain: b.gain, q: b.q, enable: b.enable !== false };
               }) };
    });
  }

  // One after another, never in parallel: the firmware ACKs nothing and overlapping requests
  // corrupt the stream. Same reason flatEq walks its seventeen paths one at a time.
  function sweepSeq(items, fn) {
    var i = 0;
    function next() {
      if (i >= items.length) { return Promise.resolve(); }
      return Promise.resolve(fn(items[i++])).then(next);
    }
    return next();
  }

  function sweepStatus(txt, bad) {
    var el = document.getElementById("sweepState");
    if (el) { el.textContent = txt; el.className = "v" + (bad ? " bad" : (SWEEP.on ? " ok" : "")); }
  }

  function sweepSnapSave(snap) {
    try { localStorage.setItem(SWEEP_KEY, JSON.stringify(snap)); } catch (e) { /* memory only */ }
  }
  function sweepSnapClear() {
    try { localStorage.removeItem(SWEEP_KEY); } catch (e) { /* ignore */ }
  }

  // The snapshot on disk is the only copy of the tune while a flatten is live, and it is shared by
  // every window on this profile. A second page entering sweep mode would snapshot the memory that
  // is ALREADY flat and overwrite that copy - the tune would be gone from both. So an existing
  // snapshot is never replaced; it is adopted and the switch becomes "put it back".
  function sweepStoredSnap() {
    try {
      var raw = localStorage.getItem(SWEEP_KEY);
      if (!raw) { return null; }
      var s = JSON.parse(raw);
      return (s && s.cells && s.cells.length) ? s : null;
    } catch (e) { return null; }
  }

  function sweepModeEnter() {
    if (SWEEP.busy) { return Promise.resolve(false); }
    if (SWEEP.on) { return Promise.resolve(true); }
    // Another window (or an earlier session) is holding the tune. Taking a snapshot now would
    // snapshot the flat memory and destroy the way back, so adopt their snapshot instead.
    var held = sweepStoredSnap();
    if (held) {
      SWEEP.snap = held;
      SWEEP.on = true;
      sweepStatus("left on — press Sweep mode to put the tune back", true);
      toast("sweep mode is already on (another window) — press Sweep mode to put the tune back");
      log("sweep mode: an unfinished snapshot was adopted instead of taking a new one");
      return Promise.resolve(false);
    }
    if (!S.connected) { toast("sweep mode needs the DSP connected"); return Promise.resolve(false); }
    if (flatBusy) { toast("a flatten is already running"); return Promise.resolve(false); }
    SWEEP.busy = true;
    SWEEP.err = null;
    var mem = S.mem;
    var paths = sweepPaths();
    // Only what the enter actually changes: the bands and byPass bit of every path, plus the master
    // level. Groups, mute and source are left live on purpose - nothing in sweep mode changes them,
    // and restoring a stale copy would undo a group move the owner made while the copy was on.
    var snap = { mem: mem, at: Date.now(),
                 mainDb: (typeof S.mainDb === "number") ? S.mainDb : null, cells: [] };
    var k = 0;
    sweepStatus("saving the tune 0/" + paths.length);
    return sweepSeq(paths, function (p) {
      k++;
      sweepStatus("saving the tune " + k + "/" + paths.length);
      return sweepReadCell(mem, p).then(function (c) { snap.cells.push(c); });
    }).then(function () {
      // Every cell is in hand, so from here the tune is recoverable and the flatten may start.
      // "On" means exactly that: what is playing is no longer the tune, whatever happens next.
      SWEEP.snap = snap;
      SWEEP.on = true;
      sweepSnapSave(snap);
      var j = 0;
      return sweepSeq(snap.cells, function (c) {
        j++;
        sweepStatus("flattening " + j + "/" + snap.cells.length);
        return sweepEqWrite(mem, c.path, false, sweepBands(c.bands, 0)).then(function (r) {
          if (!r || !r.ok) { throw new Error("path " + c.path + " would not take the flat bands"); }
        });
      });
    }).then(function () {
      // Read it back: a flatten that did not land must never be measured through.
      sweepStatus("checking the flatten");
      var bad = [];
      return sweepSeq(snap.cells, function (c) {
        return sweepReadCell(mem, c.path).then(function (back) {
          var w = sweepFlatness(back);
          if (w) { bad.push(w); }
        });
      }).then(function () {
        if (bad.length) {
          throw new Error("the flatten did not land (" + bad.slice(0, 3).join(", ")
            + (bad.length > 3 ? ", +" + (bad.length - 3) + " more" : "")
            + "). Press Sweep mode again to put the tune back, or reload the bank.");
        }
      });
    }).then(function () {
      var db = sweepLevelDb(SWEEP.pct);
      sweepStatus("setting level " + SWEEP.pct + "% (" + db.toFixed(1) + " dB)");
      return sweepSwitch(db);
    }).then(function (m) {
      if (!m || !m.ok) { throw new Error("level write failed"); }
      S.mainDb = (typeof m.mainDb === "number") ? m.mainDb : sweepLevelDb(SWEEP.pct);
      SWEEP.busy = false;
      readEq(true);
      baseInvalidate(); baseRead(true);
      sweepStatus("on — " + snap.cells.length + " paths flat, " + SWEEP.pct + "% ("
                  + sweepLevelDb(SWEEP.pct).toFixed(1) + " dB)");
      toast("sweep mode on — memory " + (mem + 1) + " flattened, master " + SWEEP.pct + "% ("
            + sweepLevelDb(SWEEP.pct).toFixed(1) + " dB)");
      log("sweep mode on: memory " + (mem + 1) + " flattened, master "
          + sweepLevelDb(SWEEP.pct).toFixed(1) + " dB");
      return true;
    }).catch(function (e) {
      SWEEP.busy = false;
      SWEEP.err = String((e && e.message) || e);
      // "On" stays set on purpose: the snapshot is in hand, so pressing the switch again is the
      // way back. Clearing it here would strand a half-flattened memory.
      sweepStatus("failed — " + SWEEP.err, true);
      toast("sweep mode: " + SWEEP.err);
      log("sweep mode failed: " + SWEEP.err);
      return false;
    });
  }

  function sweepModeExit() {
    if (SWEEP.busy) { return Promise.resolve(false); }
    if (!SWEEP.on || !SWEEP.snap) { return Promise.resolve(false); }
    SWEEP.busy = true;
    var snap = SWEEP.snap;
    var mem = snap.mem;
    var j = 0;
    sweepStatus("putting the tune back 0/" + snap.cells.length);
    return sweepSeq(snap.cells, function (c) {
      j++;
      sweepStatus("putting the tune back " + j + "/" + snap.cells.length);
      return sweepEqWrite(mem, c.path, c.byPass, c.bands).then(function (r) {
        if (!r || !r.ok) { throw new Error("path " + c.path + " would not take the tune back"); }
      });
    }).then(function () {
      sweepStatus("checking the tune");
      var bad = [];
      return sweepSeq(snap.cells, function (c) {
        return sweepReadCell(mem, c.path).then(function (back) {
          if (back.byPass !== c.byPass) { bad.push("path " + c.path + " byPass"); }
          else if (!sweepEqSame(c.bands, back.bands)) { bad.push("path " + c.path + " bands"); }
        });
      }).then(function () {
        if (bad.length) {
          throw new Error("the tune did not read back (" + bad.slice(0, 3).join(", ")
            + (bad.length > 3 ? ", +" + (bad.length - 3) + " more" : ")"));
        }
      });
    }).then(function () {
      // The level the enter changed, and nothing else. A snapshot with no level (the page never saw
      // one) leaves the master alone rather than writing a guess into it.
      if (typeof snap.mainDb !== "number") { return null; }
      return sweepSwitch(snap.mainDb);
    }).then(function (m) {
      if (m && !m.ok) { throw new Error("level restore failed"); }
      if (m && typeof m.mainDb === "number") { S.mainDb = m.mainDb; }
      SWEEP.on = false; SWEEP.snap = null; SWEEP.busy = false; SWEEP.err = null;
      sweepSnapClear();
      readEq(true);
      baseInvalidate(); baseRead(true);
      sweepStatus("off");
      toast("sweep mode off — the tune is back"
            + (typeof snap.mainDb === "number"
               ? ", master " + Number(snap.mainDb).toFixed(1) + " dB" : ""));
      log("sweep mode off: memory " + (mem + 1) + " restored from the snapshot");
      return true;
    }).catch(function (e) {
      SWEEP.busy = false;
      SWEEP.err = String((e && e.message) || e);
      // The snapshot stays, and "on" stays set, so the switch is still the way back.
      sweepStatus("put it back failed — " + SWEEP.err, true);
      toast("SWEEP MODE could not put the tune back: " + SWEEP.err
            + " — press Sweep mode to try again");
      log("sweep mode exit failed: " + SWEEP.err);
      return false;
    });
  }

  // A snapshot left on disk means a session ended with the memory flattened. Never write on
  // boot (that is a decision, not a load) - say so and leave the switch in reach.
  function sweepModeLoad() {
    try {
      var raw = localStorage.getItem(SWEEP_KEY);
      if (raw) {
        var snap = JSON.parse(raw);
        if (snap && snap.cells && snap.cells.length) {
          SWEEP.snap = snap;
          SWEEP.on = true;
          sweepStatus("left on — press Sweep mode to put the tune back", true);
          toast("sweep mode was left on — memory " + (Number(snap.mem) + 1)
                + " is flattened. Press Sweep mode to put the tune back.");
        }
      }
      var p = localStorage.getItem(SWEEP_PCT_KEY);
      if (p !== null && isFinite(Number(p))) { SWEEP.pct = Math.max(0, Math.min(100, Number(p))); }
    } catch (e) { /* ignore */ }
    var el = document.getElementById("sweepLevel");
    if (el) { el.value = String(SWEEP.pct); }
    var lab = document.getElementById("sweepLevelVal");
    if (lab) { lab.textContent = SWEEP.pct + "% · " + sweepLevelDb(SWEEP.pct).toFixed(1) + " dB"; }
  }

  // Redraw from cached cells - cheap, so muting a channel from the strip updates the
  // summed curve instantly without re-reading seventeen paths over USB.
  function sysDraw() {
    if (!SYS.cells) { return; }
    var ghosts = [], used = 0, muted = 0, failed = [];
    for (var i = 0; i < SYS.n; i++) {
      var c = sysCell(i);
      if (!c || !c.ok) { failed.push(i); continue; }
      if (CH.mute[i]) { muted++; continue; }
      var w = CH.known[i] ? (Number(CH.gain[i]) || 0) : 0;
      // colour by CHANNEL index, not draw order: muting must not hand the survivors
      // new hues - the colour is the channel's identity on every screen it appears.
      ghosts.push({ bands: c.bands, byPass: c.byPass, weight: w,
                    color: CH_COLORS[i % CH_COLORS.length] });
      used++;
    }
    var master = sysCell(16);
    // The summed curve: power-add live channels, cascade the master chain on top.
    var masterOk = master && master.ok;
    function sumDb(f) {
      var p = 0;
      for (var g = 0; g < ghosts.length; g++) {
        var v = ghosts[g].byPass ? 0 : totalDb(ghosts[g].bands, f);
        p += Math.pow(10, (v + ghosts[g].weight) / 10);
      }
      if (p <= 0) { return 0; }
      var s = 10 * Math.log(p) / Math.LN10;
      if (masterOk && !master.byPass) { s += totalDb(master.bands, f); }
      return s;
    }
    renderCurve("sysCurve", [], false, "sysRange", { ghosts: ghosts, custom: sumDb });
    document.getElementById("sysCount").textContent =
      used + " live" + (muted ? " · " + muted + " muted" : "") +
      (failed.length ? " · unread: " + failed.join(",") : "");
    document.getElementById("sysMem").textContent = String(S.mem + 1);
    document.getElementById("sysInfo").textContent = failed.length
      ? "could not read output" + (failed.length > 1 ? "s " : " ") + failed.join(", ") +
        " — sum shown without them"
      : (masterOk ? "incl. master chain (16)" : "master chain unreadable — excluded") +
        (muted ? " · " + muted + " muted ch excluded" : "") +
        (SYS.auto ? " · auto-refresh on" : "");
  }

  // ---------- RTA tab ----------
  // Served by rta.exe (own process, own port) - the bridge never touches this data, so
  // the daemon can die or be absent without affecting anything else on this page.
  var RTA = { timer: null, data: null, lastOk: 0, peak: true, actx: null, src: null,
              gainNode: null, hist: [], meas: [], overlay: null, measBusy: false,
              win: null,                  // frames of an open capture window (sweep signature)
              hold: false, decay: 3,      // daemon freeze (/hold) + the rate it reports
              sendWarned: false };        // auto-upload already complained once, don't nag
  var RTA_URL = (location.protocol === "file:" ? "http://localhost:"
                 : location.protocol + "//" + location.hostname + ":") + "8586/rta.json";

  function rtaStart() {
    if (!RTA.timer) { RTA.timer = setInterval(rtaPoll, 300); }
    rtaPoll();
  }
  function rtaStop() { if (RTA.timer) { clearInterval(RTA.timer); RTA.timer = null; } }

  function rtaPoll() {
    fetch(RTA_URL, { cache: "no-store" }).then(function (r) {
      if (!r.ok) { throw new Error("HTTP " + r.status); }
      return r.json();
    }).then(function (d) {
      RTA.data = d;
      RTA.lastOk = Date.now();
      // ring of the last <=10 frames (~3 s) so Capture stores a smooth average
      var snap = [];
      for (var si = 0; si < (d.bands || []).length; si++) {
        snap.push(Number(d.bands[si].db));
      }
      if (snap.length) {
        RTA.hist.push(snap);
        if (RTA.hist.length > 10) { RTA.hist.shift(); }
        // While a capture window is open, keep every frame: the sweep signature is the only
        // record of what the window actually contained, and it is what decides a sweep run's
        // verdict - see rtaSweepSignature.
        if (RTA.win) { RTA.win.push({ t: Date.now(), db: snap }); }
      }
      document.getElementById("rtaDev").textContent =
        (d.device || "-") + ((typeof d.devIdx === "number") ? " [" + d.devIdx + "]" : "");
      document.getElementById("rtaState").textContent =
        (d.status || "-") + " · " + (d.fs || "?") + " Hz";
      micSummary();
      document.getElementById("rtaLevelText").textContent =
        (typeof d.levelDb === "number" ? d.levelDb.toFixed(1) : "-120.0") + " dBFS";
      rtaCalShow(d.cal);
      // the daemon owns the freeze state (the run switches it too), so follow what it reports
      if (d.hold !== undefined) {
        RTA.decay = Number(d.peakDecayDbPerSec) || 3;
        rtaHoldShow(d.hold);
      }
      rtaDraw();
      eqOvRefresh();
    }).catch(function () {
      document.getElementById("rtaState").textContent =
        "rta.exe not reachable on " + RTA_URL.replace("/rta.json", "") +
        " — start it on the laptop";
      micSummary();
    });
  }

  // Mic calibration state, straight from the daemon (survives restarts - the file
  // lives next to rta.exe and is re-applied on boot).
  function rtaCalShow(cal) {
    var st = document.getElementById("rtaCalState");
    var dt = document.getElementById("rtaCalDetail");
    if (!st || !dt) { return; }
    if (!cal || !cal.loaded) {
      st.textContent = "none";
      st.style.color = "#888";
      dt.textContent = "no calibration applied — raw mic response";
      micSummary();
      return;
    }
    st.textContent = "loaded";
    st.style.color = "#6d6";
    dt.textContent = "SERNO " + cal.serial + " · " + cal.points + " pts · " +
      cal.fmin + "–" + cal.fmax + " Hz · " + cal.file;
    micSummary();
  }

  // The folded mic setup's one visible line. Assembled from the same elements the open
  // panel shows, so the two can never disagree.
  function micSummary() {
    var s = document.getElementById("micSetupSum");
    if (!s) { return; }
    var dev = document.getElementById("rtaDev").textContent || "-";
    var st = document.getElementById("rtaState").textContent || "-";
    var cal = document.getElementById("rtaCalState").textContent || "none";
    s.textContent = dev + " · " + st + " · cal " + cal;
  }

  // ---- mic device selector ----
  var RTA_BASE = RTA_URL.replace("/rta.json", "");

  function rtaLoadDevices() {
    fetch(RTA_BASE + "/devices", { cache: "no-store" }).then(function (r) {
      if (!r.ok) { throw new Error("HTTP " + r.status); }
      return r.json();
    }).then(function (d) {
      var sel = document.getElementById("rtaDevSel");
      var cur = sel.value;
      sel.innerHTML = "";
      if (!d.devices || !d.devices.length) {
        sel.innerHTML = '<option value="">— no devices —</option>';
        return;
      }
      d.devices.forEach(function (dev) {
        var o = document.createElement("option");
        o.value = String(dev.index);
        o.textContent = "[" + dev.index + "] " + dev.name + " (" + dev.ch + "ch)";
        if (dev.index === d.active || String(dev.index) === String(d.active)) {
          o.selected = true;
        }
        sel.appendChild(o);
      });
      if (d.active === "default") {
        var o = document.createElement("option");
        o.value = "default";
        o.textContent = "system default";
        o.selected = true;
        sel.appendChild(o);
      }
      if (cur) { sel.value = cur; }   // keep user's view across refreshes
    }).catch(function () {
      var sel = document.getElementById("rtaDevSel");
      if (sel) { sel.innerHTML = '<option value="">— rta.exe offline —</option>'; }
    });
  }

  function rtaSwitchDevice() {
    var sel = document.getElementById("rtaDevSel");
    var v = sel.value;
    if (!v) { return; }
    fetch(RTA_BASE + "/device?n=" + encodeURIComponent(v), { cache: "no-store" })
      .then(function (r) { return r.json(); })
      .then(function (d) {
        if (d.ok) { toast("switching mic → " + v); setTimeout(rtaLoadDevices, 800); }
        else { toast("switch failed: " + (d.error || "?")); }
      })
      .catch(function (e) { toast("switch failed: " + e); });
  }

  // ---- freeze / hold-peak (rta.exe /hold) ----
  // The daemon's held trace decays 3 dB/s by default, which is right for a live room but useless
  // for a 10 s 20 Hz->20 kHz sweep: the low end would be ~30 dB down by the time the sweep
  // reaches 20 kHz. Freeze sets the decay to 0, so whatever the mic hears is left behind on the
  // curve. Clear wipes the held trace (and its broadband peak), so an earlier tone cannot
  // flatter the next measurement. Both are one call to the daemon; the page never fakes it.
  function rtaHoldShow(on) {
    RTA.hold = !!on;
    var chip = document.getElementById("rtaHold");
    if (chip) {
      chip.classList.toggle("on", RTA.hold);
      chip.textContent = "Freeze: " + (RTA.hold ? "on" : "off");
    }
    var lg = document.getElementById("rtaPeakLegend");
    if (lg) {
      lg.textContent = RTA.peak
        ? "peak hold ▲ " + (RTA.hold ? "frozen" : RTA.decay + " dB/s") +
          " · 1/6-oct · 20 Hz–20 kHz"
        : "live trace · 1/6-oct · 20 Hz–20 kHz";
    }
  }

  function rtaHoldSet(on, quiet) {
    return fetch(RTA_BASE + "/hold?on=" + (on ? "1" : "0"), { cache: "no-store" })
      .then(function (r) { return r.json(); })
      .then(function (d) {
        // Freezing while the live trace is drawn would look like nothing happened: show the
        // frozen curve, since that is the one the freeze is about.
        if (d.hold && !RTA.peak) {
          RTA.peak = true;
          var pc = document.getElementById("rtaPeak");
          if (pc) { pc.classList.add("on"); pc.textContent = "Peak hold: on"; }
        }
        rtaHoldShow(d.hold);
        if (RTA.data) { rtaDraw(); }
        // A run freezes and unfreezes once per channel; a toast each time would bury the log the
        // run is writing to, so the run asks for quiet and reads the daemon's reply instead.
        if (!quiet) {
          toast("peak freeze " + (d.hold ? "on — holds until cleared"
                                         : "off — peaks decay " + d.decayDbPerSec + " dB/s"));
        }
        return d;
      })
      .catch(function (e) {
        if (!quiet) { toast("freeze failed: " + e); }
        return null;                  // the caller has to know, not assume
      });
  }

  function rtaClearPeaks(quiet) {
    return fetch(RTA_BASE + "/hold?reset=1", { cache: "no-store" })
      .then(function (r) { return r.json(); })
      .then(function (d) {
        rtaHoldShow(d.hold);
        if (RTA.data) { rtaDraw(); }
        if (!quiet) { toast("held peaks cleared" + (d.hold ? " (still frozen)" : "")); }
        return d;
      })
      .catch(function (e) {
        if (!quiet) { toast("clear failed: " + e); }
        return null;
      });
  }

  // ---- calibration upload ----
  function rtaUploadCal(file) {
    if (!file) { return; }
    if (!/\.txt$/i.test(file.name)) { toast("cal files are .txt (miniDSP format)"); return; }
    file.text().then(function (txt) {
      return fetch(RTA_BASE + "/cal?name=" + encodeURIComponent(file.name), {
        method: "POST",
        headers: { "Content-Type": "text/plain" },
        body: txt
      });
    }).then(function (r) { return r.json(); })
      .then(function (d) {
        if (d.ok) {
          toast("cal verified: SERNO " + d.serial + " · " + d.points + " pts" +
                (d.persisted ? " · saved" : ""));
          rtaCalShow({ loaded: true, serial: d.serial, points: d.points,
                       fmin: d.fmin, fmax: d.fmax, file: d.name });
        } else {
          toast("cal rejected: " + (d.error || "unknown error"));
        }
      })
      .catch(function (e) { toast("cal upload failed: " + e); });
  }

  // ---- saved measurements (REW-style table) ----
  // Each capture = 61 floats (1/6-oct, mic-calibrated) + timestamp, kept in
  // localStorage on this phone (persist across reloads; no daemon involvement).
  // Capture averages the last <=10 polls (~3 s) so the thumb isn't frame noise.
  var MEAS_KEY = "ds18.rtaMeas";
  var MEAS_COLORS = ["#e05b4b", "#4bd07a", "#4aa3ff", "#e8a33d", "#c9d1d9",
                     "#3ec1a9", "#b07ade", "#5ec8e8"];
  var MEAS_MON = ["Jan", "Feb", "Mar", "Apr", "May", "Jun",
                  "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

  function rtaMeasLoad() {
    try {
      var raw = localStorage.getItem(MEAS_KEY);
      RTA.meas = raw ? JSON.parse(raw) : [];
      if (!Array.isArray(RTA.meas)) { RTA.meas = []; }
    } catch (e) { RTA.meas = []; }
  }
  function rtaMeasPersist() {
    try { localStorage.setItem(MEAS_KEY, JSON.stringify(RTA.meas)); }
    catch (e) { toast("save failed: " + e); }
  }
  function rtaMeasDate(t) {
    var dd = new Date(t);
    return MEAS_MON[dd.getMonth()] + " " + dd.getDate() + "  " +
      ("0" + dd.getHours()).slice(-2) + ":" + ("0" + dd.getMinutes()).slice(-2);
  }

  // ---------- speaker setup (Tune tab) ----------
  // The measurement plan: which speakers are being measured and what each one is. It is the
  // one place that answers "how many measurements does a run need" and "what is this capture
  // called", so a run ends up with tweeter 1 / tweeter 2 / midrange 1 / sub 1 instead of a
  // row of timestamps. It lives in this browser (localStorage) - the DSP protocol has no
  // field for it, and it is a plan, not device state.
  //
  // Channels are numbered the way you count speakers in the car, 1..10. The app's own output
  // list is 0-based ("CH 0".."CH 7"), so plan channel 1 is DSP output 0; rows show that
  // mapping. Ten rather than eight keeps the plan ahead of the hardware.
  var ROSTER_KEY = "ds18.roster";
  var ROSTER_MAX_CH = 10;
  var ROSTER_TYPES = ["Tweeter", "Midrange", "Midbass", "Sub"];

  // What each speaker type is physically rated to reproduce, in Hz - the makers' numbers
  // (ds18-bridge\DSB\EQUIPMENT.md), not a guess:
  //     Tweeter   BZRK RS-T150 1" alu dome   1.5 k - 25 k  (1.5 k is its rated FLOOR)
  //     Midrange  NVX XQS3v2 3.5" (Fs 150)   200 - 18 k
  //     Midbass   Deaf Bonce 8" 110 / Orion 6.5" 140 - 12 k
  //     Sub       Rockford T2 12" (not on a DSP path - informational)
  // The top is clamped to 20 k because that is the last band the device's own ladder reaches.
  // The tweeter floor is the one number that is enforced: see sweepBandProblem().
  var ROSTER_BAND = {
    "Tweeter":  [1500, 20000],
    "Midrange": [200, 18000],
    "Midbass":  [110, 12000],
    "Sub":      [25, 200]
  };
  function rosterBand(type) { return ROSTER_BAND[type] || null; }
  // 1500 -> "1.5k", 20000 -> "20k", 110 -> "110"
  function fmtHz(f) {
    f = Number(f);
    if (!isFinite(f) || f <= 0) { return "?"; }
    if (f >= 1000) {
      var k = f / 1000;
      return (k >= 10 || k === Math.floor(k) ? Math.round(k) : k.toFixed(1)) + "k";
    }
    return String(Math.round(f));
  }
  function bandLabel(type) {
    var b = rosterBand(type);
    return b ? (fmtHz(b[0]) + "–" + fmtHz(b[1])) : "";
  }
  var ROSTER = [];              // [{ ch: 1, type: "" | "Tweeter" | "Midrange" | "Midbass" | "Sub" }]
  var ROSTER_NOW = 0;           // the channel being measured right now (0 = none)

  function rosterLoad() {
    try {
      var raw = localStorage.getItem(ROSTER_KEY);
      var a = raw ? JSON.parse(raw) : null;
      if (Array.isArray(a) && a.length) {
        ROSTER = [];
        for (var i = 0; i < a.length; i++) {
          var r = a[i];
          if (!r || !(r.ch >= 1) || r.ch > ROSTER_MAX_CH) { continue; }
          ROSTER.push({ ch: r.ch | 0,
                        type: (ROSTER_TYPES.indexOf(r.type) >= 0) ? r.type : "",
                        name: (typeof r.name === "string") ? r.name.slice(0, 28) : "" });
        }
        if (ROSTER.length) { return; }
      }
    } catch (e) { /* fall through to the default list */ }
    rosterDefault();
  }
  function rosterDefault() {
    ROSTER = [];
    var n = Math.min(ROSTER_MAX_CH, rosterOutCount());
    for (var i = 1; i <= n; i++) { ROSTER.push({ ch: i, type: "", name: "" }); }
    rosterPersist();
  }
  function rosterPersist() {
    try { localStorage.setItem(ROSTER_KEY, JSON.stringify(ROSTER)); }
    catch (e) { /* quota - the plan just will not survive a reload */ }
  }

  // One job per configured channel, in channel order. A channel is part of the run when it has
  // a type or a name. The label is the name when one is set, otherwise the old type + counter -
  // naming is how two rows that both say "tweeter 2" become "driver tweeter" and
  // "passenger tweeter", which is what ends up in the log and in the saved measurements.
  function rosterJobs() {
    var rows = [], i, maxCh = rosterOutCount();
    for (i = 0; i < ROSTER.length; i++) {
      if (ROSTER[i].ch > maxCh) { continue; }
      if (ROSTER[i].type || (ROSTER[i].name && ROSTER[i].name.trim())) { rows.push(ROSTER[i]); }
    }
    rows.sort(function (a, b) { return a.ch - b.ch; });
    var seen = {}, out = [];
    for (i = 0; i < rows.length; i++) {
      var t = rows[i].type, name = (rows[i].name || "").trim();
      if (t) { seen[t] = (seen[t] || 0) + 1; }
      out.push({ ch: rows[i].ch, type: t, name: name,
                 label: name || ((t ? t.toLowerCase() : "channel") + " " + (t ? seen[t] : rows[i].ch)) });
    }
    return out;
  }
  function rosterJobFor(ch) {
    var jobs = rosterJobs();
    for (var i = 0; i < jobs.length; i++) {
      if (jobs[i].ch === ch) { return jobs[i]; }
    }
    return null;
  }
  function rosterNextFree() {
    var used = {}, i, n = Math.min(ROSTER_MAX_CH, rosterOutCount());
    for (i = 0; i < ROSTER.length; i++) { used[ROSTER[i].ch] = true; }
    for (i = 1; i <= n; i++) { if (!used[i]) { return i; } }
    return 0;
  }

  // How many DSP outputs this device actually has. The channel state (CH) is filled in later
  // in the boot sequence than the first roster paint, so fall back to the product's eight
  // instead of reading an object that does not exist yet - renderChannels() repaints once the
  // device has answered.
  function rosterOutCount() {
    try {
      if (typeof CH !== "undefined" && CH && CH.count >= 1) { return chVisible(); }
    } catch (e) { /* pre-boot */ }
    return 8;
  }

  function escHtml(s) {
    return String(s == null ? "" : s).replace(/&/g, "&amp;").replace(/</g, "&lt;")
      .replace(/>/g, "&gt;").replace(/"/g, "&quot;");
  }

  function rosterRender() {
    var host = document.getElementById("rosterRows");
    if (!host || !ROSTER || !ROSTER.length) { return; }
    // Never rebuild the rows while a name is being typed - the caret and the half-typed
    // value would go with them. The next render catches up.
    if (document.activeElement && document.activeElement.className === "rname") { return; }

    // Only the channels this device actually has. The owner's unit has 8 outputs, so the
    // speaker setup shows 8 rows - not a fixed list of 10 with two dead ones at the bottom.
    var jobs = rosterJobs(), i, t, maxCh = rosterOutCount();
    var rows = ROSTER.slice().sort(function (a, b) { return a.ch - b.ch; })
      .filter(function (r) { return r.ch <= maxCh; });

    // The placeholder is the label the run would use if the field were empty, so the two
    // never disagree about what a row is called.
    var auto = {}, cnt = {};
    for (i = 0; i < rows.length; i++) {
      if (!rows[i].type) { auto[rows[i].ch] = ""; continue; }
      cnt[rows[i].type] = (cnt[rows[i].type] || 0) + 1;
      auto[rows[i].ch] = rows[i].type.toLowerCase() + " " + cnt[rows[i].type];
    }

    var html = "";
    for (i = 0; i < rows.length; i++) {
      var r = rows[i];
      html += '<div class="rrow" data-ch="' + r.ch + '">' +
        '<span class="rch">ch ' + r.ch + '</span>' +
        '<select class="rtype">' +
          '<option value="">— none —</option>';
      for (t = 0; t < ROSTER_TYPES.length; t++) {
        html += '<option value="' + ROSTER_TYPES[t] + '"' +
          ((r.type === ROSTER_TYPES[t]) ? " selected" : "") + '>' + ROSTER_TYPES[t] + '</option>';
      }
      html += '</select>' +
        '<input class="rname" type="text" maxlength="28" spellcheck="false" autocomplete="off"' +
          ' placeholder="' + escHtml(auto[r.ch] || "name it") + '"' +
          ' value="' + escHtml(r.name || "") + '"' +
          ' title="name this channel (e.g. driver tweeter)">' +
        '<span class="rband">' + bandLabel(r.type) + '</span>' +
        '<span class="rstat"></span>' +
        '<button class="rdel" title="remove this channel">×</button></div>';
    }
    host.innerHTML = html;
    runRowsPaint();

    // Just the count now. Each row already carries its own label (tweeter 1, midrange 1), so
    // the spelled-out plan line was a paragraph saying the same thing a second time.
    setTxt("rosterCount", String(jobs.length));

    rosterRenderNow();
    rosterPaintBands();
  }

  // The "measuring" picker: which of the planned channels the next capture belongs to.
  function rosterRenderNow() {
    var sel = document.getElementById("rosterNow");
    if (!sel) { return; }
    var jobs = rosterJobs(), html = '<option value="">— pick a channel —</option>', i;
    for (i = 0; i < jobs.length; i++) {
      html += '<option value="' + jobs[i].ch + '">' + jobs[i].label +
              ' · ch ' + jobs[i].ch + '</option>';
    }
    sel.innerHTML = html;
    sel.value = ROSTER_NOW ? String(ROSTER_NOW) : "";
    var btn = document.getElementById("rtaMeasSave");
    if (btn) {
      var job = ROSTER_NOW ? rosterJobFor(ROSTER_NOW) : null;
      btn.textContent = job ? ("Capture " + job.label) : "Capture";
    }
  }

  function rosterAdd() {
    // The visible list is the device's outputs, so "full" means every output has a row -
    // not every slot of the old fixed ten.
    var c = rosterNextFree();
    if (!c) {
      toast("all " + Math.min(ROSTER_MAX_CH, rosterOutCount()) + " outputs are already listed");
      return;
    }
    ROSTER.push({ ch: c, type: "", name: "" });
    runRowsClear(); rosterPersist(); rosterRender();
  }

  function rosterRemove(ch) {
    var out = [], i;
    for (i = 0; i < ROSTER.length; i++) {
      if (ROSTER[i].ch !== ch) { out.push(ROSTER[i]); }
    }
    ROSTER = out;
    if (ROSTER_NOW === ch) { ROSTER_NOW = 0; }
    runRowsClear(); rosterPersist(); rosterRender();
  }

  function rosterSetType(ch, type) {
    for (var i = 0; i < ROSTER.length; i++) {
      if (ROSTER[i].ch === ch) { ROSTER[i].type = type; }
    }
    runRowsClear(); rosterPersist(); rosterRender();
  }

  // Typing a name updates the model and the picker but never rebuilds the rows: a render
  // here would drop the caret mid-word.
  function rosterSetName(ch, name) {
    for (var i = 0; i < ROSTER.length; i++) {
      if (ROSTER[i].ch === ch) { ROSTER[i].name = String(name || "").slice(0, 28); }
    }
    rosterPersist();
    runRowsClear();
    setTxt("rosterCount", String(rosterJobs().length));
    rosterRenderNow();
  }

  function rosterReset() {
    var any = rosterJobs().length;
    if (any && !window.confirm("Clear the " + any + " configured channel(s)?")) { return; }
    ROSTER_NOW = 0;
    runRowsClear();
    rosterDefault();
    rosterRender();
  }

  // ---------- stimulus source (Tune tab) ----------
  // Two ways the tone gets in: the phone plays it into the vehicle and this box only listens
  // (the usual case in the car), or this computer's own output is wired into the DSP. The
  // chips below the selector always play from this computer - this only says which path is
  // in use, so the hints and the run steps tell the truth about where to start the tone.
  var STIMSRC_KEY = "ds18.stimSrc";
  var STIM_SRC = "phone";

  function stimSrcLoad() {
    try {
      var v = localStorage.getItem(STIMSRC_KEY);
      if (v === "phone" || v === "thispc") { STIM_SRC = v; }
    } catch (e) { /* keep the default */ }
  }
  function stimSrcRender() {
    var ph = document.getElementById("stimSrcPhone");
    var pc = document.getElementById("stimSrcPc");
    var hint = document.getElementById("stimSrcHint");
    if (ph) { ph.classList.toggle("on", STIM_SRC === "phone"); }
    if (pc) { pc.classList.toggle("on", STIM_SRC === "thispc"); }
    if (hint) {
      hint.textContent = (STIM_SRC === "phone")
        ? "Something else plays the tone into the car — a phone over Bluetooth, a tone app, the "
          + "head unit — while this page listens with the mic and talks to the DSP over USB. Start "
          + "the tone there; the play chips stay idle."
        : "The play chips below make the sound on the device running this page: on the phone that "
          + "is the phone → Bluetooth → head unit, on the computer it is the computer's own output "
          + "(route it into the car). Pressing Capture plays a sweep this way by itself. Start at "
          + "low volume.";
    }
  }
  function stimSrcSet(src) {
    if (src !== "phone" && src !== "thispc") { return; }
    STIM_SRC = src;
    try { localStorage.setItem(STIMSRC_KEY, src); } catch (e) { /* ignore */ }
    stimSrcRender();
  }

  // ---------- what shape is playing (Tune tab) ----------
  // The capture rule depends on the stimulus, not only on how long the mic listens. Pink noise is
  // stationary, so the average of the live trace over a few seconds is the measurement. A sweep is
  // not: it is in each 1/6-octave band for a fraction of a second, and by the time it reaches
  // 20 kHz the 3 dB/s hold decay has dragged the bottom of the curve down ~30 dB, so averaging
  // bakes that decay into the result. A sweep has to be frozen and read from the held envelope
  // (bands[].peak), and it has to be given a whole ramp's worth of time to pass.
  //
  // The app cannot hear which shape is playing, so this is declared once and remembered. The Live
  // RTA card's own Pink noise / Sweep chips set it as well, because those chips *are* the shape
  // being played whenever the stimulus comes from this computer.
  var STIMSHAPE_KEY = "ds18.stimShape";
  var SWEEP_WINDOW_MS = 12000;   // one full 10 s 20 Hz -> 20 kHz ramp, with margin
  var RING_SETTLE_MS = 3200;     // the ~10-frame (~3 s) ring has to fill with the tone alone
  var STIM_SHAPE = "pink";       // "pink" | "sweep"

  // What "a sweep arrived" means, as three numbers rather than a level. A band the ramp crosses
  // jumps far above its OWN level for a moment and then falls back, and the moment gets later as
  // the frequency rises. Neither is true of a room, of music, or of rumble - which is what makes
  // this usable where a threshold is not: in a car, the bass floor sits around -40 dBFS and no
  // fixed number can separate "this driver played" from "the car was loud".
  var SWEEP_BURST_DB = 10;       // a band has to leave its own median by this much at least once
  var SWEEP_DWELL_PCT = 30;      // ...and only briefly: a ramp is through a band in ~60 ms
  var SWEEP_MIN_BANDS = 5;       // and this many bands in a row have to do it
  var SWEEP_ASCEND_PCT = 60;     // and the jumps have to happen in ascending frequency order
  var SWEEP_GAP_BANDS = 1;       // one unlucky band is allowed inside a run, no more

  function stimShapeLoad() {
    try {
      var v = localStorage.getItem(STIMSHAPE_KEY);
      if (v === "pink" || v === "sweep") { STIM_SHAPE = v; }
    } catch (e) { /* keep the default */ }
  }
  function stimShapeRender() {
    var pk = document.getElementById("stimShapePink");
    var sw = document.getElementById("stimShapeSweep");
    if (pk) { pk.classList.toggle("on", STIM_SHAPE === "pink"); }
    if (sw) { sw.classList.toggle("on", STIM_SHAPE === "sweep"); }
  }
  function stimShapeSet(v) {
    if (v !== "pink" && v !== "sweep") { return; }
    STIM_SHAPE = v;
    try { localStorage.setItem(STIMSHAPE_KEY, v); } catch (e) { /* ignore */ }
    stimShapeRender();
  }


  // ---------- measurement run box (Tune tab) ----------
  // The run cycle: play the stimulus, mute the others, settle, capture, next channel, then put
  // the channel block back. It writes exactly one thing to the DSP - cmd 11 with a mute vector -
  // and it writes it as a COMPLETE block for the memory (see runWrite). Preview is not a second
  // implementation of the loop: it is this same cycle with every frame built by the bridge and
  // not transmitted, so the box cannot drift from what Start really does.
  var RUN = { busy: false, timer: null, wait: null, steps: [], at: 0, pace: 240, mode: "",
              dry: true, jobs: [], snap: null, dirty: false, stim: false, ok: 0, bad: 0,
              ready: false, subOff: false, sig: "", holdUntil: 0, locked: false, rows: {},
              holdStart: false };   // the daemon's freeze when the run began (sweep captures)
  // RUN.rows: ch -> { s: "queued" | "run" | "done" | "fail", pct: 0..100 }. That is what the
  // dash at the end of a speaker-setup row turns into while the run walks the channels.

  // Paint the channel rows from RUN.rows. Called instead of a roster rebuild while a run is in
  // flight: the plan is locked, and rebuilding would throw the animation away.
  function runStatText(st) {
    if (!st) { return "—"; }                                  // the idle dash
    if (st.s === "run") { return '<b><i style="width:' + (st.pct || 0) + '%"></i></b>'; }
    if (st.s === "done") { return "✓"; }
    if (st.s === "fail") { return "✕"; }
    return "—";
  }

  function runRowsPaint() {
    if (!RUN) { return; }
    var all = document.querySelectorAll("#rosterRows .rrow");
    if (!all.length) { return; }
    var jobs = rosterJobs(), planned = {}, i;
    for (i = 0; i < jobs.length; i++) { planned[jobs[i].ch] = true; }
    for (i = 0; i < all.length; i++) {
      var ch = parseInt(all[i].getAttribute("data-ch"), 10) || 0;
      var st = RUN.rows[ch] || null;
      var c = "rrow";
      if (RUN.locked) {
        c += " locked";
        if (!planned[ch]) { c += " dim"; }
        else if (st && st.s === "run") { c += " active"; }
      }
      all[i].className = c;
      var slot = all[i].querySelector(".rstat");
      if (slot) {
        slot.className = "rstat" + (st ? " " + st.s : "");
        slot.innerHTML = runStatText(st);
      }
    }
  }

  // One channel's row. state "" clears it back to the idle dash.
  function runRow(ch, state, pct) {
    if (!RUN) { return; }
    if (state) { RUN.rows[ch] = { s: state, pct: (pct == null ? 0 : pct) }; }
    else { delete RUN.rows[ch]; }
    runRowsPaint();
  }

  // The plan changed: every bar and tick on the rows is now void.
  function runRowsClear() {
    if (!RUN) { return; }
    RUN.rows = {};
    runRowsPaint();
  }

  // Lock the plan while a run is in flight. Gray the speaker setup and the measuring picker
  // out, put every planned channel in the queue, and let the rows report the run from then on.
  function runLock(on, jobs) {
    RUN.locked = !!on;
    var rc = document.getElementById("rosterCard"), mc = document.getElementById("measCard");
    if (rc) { rc.className = on ? "card locked" : "card"; }
    if (mc) { mc.className = on ? "card locked" : "card"; }
    var rn = document.getElementById("rosterNow");
    if (rn) { rn.disabled = !!on; }
    if (on) {
      RUN.rows = {};
      jobs = jobs || rosterJobs();
      for (var i = 0; i < jobs.length; i++) { RUN.rows[jobs[i].ch] = { s: "queued", pct: 0 }; }
    }
    // Unlocking deliberately keeps RUN.rows, so the ticks and crosses stay on the rows after a
    // run until the plan itself changes.
    runRowsPaint();
  }

  // Log reads top-down like a chat. Every message goes in as TEXT: device names, file names and
  // error strings are device-controlled, so they must never be parsed as markup.
  function runLog(kind, msg) {
    var box = document.getElementById("runLog");
    if (!box) { return; }
    var t = new Date();
    var p = function (n) { return (n < 10 ? "0" : "") + n; };
    var line = document.createElement("div");
    line.className = "runline " + (kind || "info");
    var ts = document.createElement("span");
    ts.className = "runt";
    ts.textContent = p(t.getHours()) + ":" + p(t.getMinutes()) + ":" + p(t.getSeconds());
    var m = document.createElement("span");
    m.className = "runm";
    m.textContent = String(msg);
    line.appendChild(ts);
    line.appendChild(m);
    box.appendChild(line);
    box.scrollTop = box.scrollHeight;
    while (box.childNodes.length > 240) { box.removeChild(box.firstChild); }
  }

  function runSet(state, label) {
    var el = document.getElementById("runState");
    if (!el) { return; }
    el.className = "runstate " + state;
    el.textContent = label || state;
  }
  function runHold(ms) { RUN.holdUntil = Date.now() + (ms || 0); }

  // "(default)" and the like are noise on a 380px row.
  function runShortDev(name) {
    var s = String(name || "mic").replace(/\s*\((?:default|loopback)\)\s*$/i, "");
    return (s.length > 26) ? (s.slice(0, 25) + "…") : s;
  }

  // The measurement mic is the one check that must be a HARD STOP. On 2026-10-20 a whole
  // eight-channel run recorded the laptop's own array: rta.exe was genuinely "capturing" (that
  // status is true of ANY device), the array idles at -93 dBFS, and every channel "measured"
  // silence. It was only a warning, and RUN.ready counts failures - so the run started anyway.
  //
  // Three independent facts, because a device name can lie:
  //   device    what winmm says the OPENED handle is (rta.exe now asks the handle, not the request)
  //   devDrift  the capture list renumbered under us since open - the stale-index trap
  //   ch        the UMIK-1 is MONO; the laptop array is stereo
  function runMicVerdict(d) {
    if (!d) {
      return { s: "fail", v: "no reading yet — rta.exe must be running on this machine (port 8586); "
        + "without it there is nothing to measure with" };
    }
    var name = String(d.device || "");
    var lvl = (typeof d.levelDb === "number") ? d.levelDb : null;
    var ch = (typeof d.ch === "number") ? d.ch : null;
    var tail = (lvl !== null ? " · " + lvl.toFixed(1) + " dBFS" : "");
    var drift = String(d.devDrift || "");
    var laptopish = /microphone array|realtek|stereo mix|internal|hdmi|webcam/i.test(name);
    var lineIn = /line in/i.test(name);
    if (drift) {
      return { s: "fail", v: "the input changed under rta.exe: it opened \"" + name + "\" but that "
        + "slot now names \"" + drift + "\" — Windows renumbered the capture devices (a replug does "
        + "this). Re-point the mic and Verify again." };
    }
    if (/system default/i.test(name)) {
      return { s: "fail", v: "rta.exe is on the Windows default input, which winmm will not "
        + "identify, so it cannot prove a UMIK is connected — the UMIK-1 is not on USB. Plug it in "
        + "and select it; a run now would record whatever Windows picked." };
    }
    if (laptopish) {
      return { s: "fail", v: runShortDev(name) + " is the laptop's own input, not a measurement mic"
        + tail + ". A run would record this room, not the car. Plug in the UMIK-1 and select it "
        + "(mic row below, or .\\preflight.ps1), then Verify again." };
    }
    if (lineIn) {
      return { s: "fail", v: runShortDev(name) + " is a line input, not a microphone" + tail
        + ". The UMIK-1 is a USB mic and appears as \"Microphone (UMIK-1)\"; a line input cannot "
        + "carry it. Plug the UMIK-1 in and select it, then Verify again." };
    }
    if (d.status !== "capturing") {
      return { s: "fail", v: name + " — " + (d.status || "not capturing") };
    }
    if (!/umik|minidsp/i.test(name)) {
      return { s: (ch === 1 ? "warn" : "fail"),
        v: runShortDev(name) + " · " + (d.fs || "?") + " Hz" + tail
          + (ch !== null ? " · " + ch + " ch" : "")
          + (ch === 1 ? " — mono, so probably the mic, but not a UMIK"
                      : " — not a UMIK and not mono: this is not the measurement mic") };
    }
    var hot = (lvl !== null && lvl > -6);
    return { s: hot ? "warn" : "ok",
      v: runShortDev(name) + " · " + (d.fs || "?") + " Hz" + tail
        + (ch !== null ? " · " + ch + " ch" : "")
        + (hot ? " — very hot, turn the stimulus down" : "") };
  }

  // The checks. Everything except the sub amp is read from live state, so "verify" cannot pass
  // on a guess. The subs sit on their own amplifier - the app cannot see that one at all, so it
  // is a person's word, kept as a tick box rather than quietly assumed.
  function runCheckData() {
    var d = RTA.data, jobs = rosterJobs();
    var calOk = !!(d && d.cal && d.cal.loaded);
    var blockOk = !!(CH && CH.known && CH.known[S.mem]);
    var mic = runMicVerdict(d);
    var plan = "none — give each channel a speaker type above";
    if (jobs.length) {
      plan = jobs.length + " to measure: " + jobs.map(function (j) { return j.label; }).join(", ");
    }
    return [
      { k: "DSP link",
        s: S.connected ? "ok" : "fail",
        v: S.connected ? ("USB · now on memory " + (S.mem + 1) + " of 4")
                       : "not connected — amp powered on, cable must carry data" },
      { k: "measurement mic", s: mic.s, v: mic.v },
      { k: "cal file", s: calOk ? "ok" : "warn",
        v: calOk ? (d.cal.file + " · " + d.cal.points + " pts · " + d.cal.fmin + "–" + d.cal.fmax + " Hz")
                 : "none — curves would be shape only" },
      { k: "stimulus", s: "ok",
        v: ((STIM_SRC === "phone") ? "phone → vehicle (Bluetooth)" : "this device plays it")
           + " · " + (STIM_SHAPE === "sweep"
             ? "sweep 20–20k: frozen held trace, and each channel judged by the sweep signature"
             : "pink noise, averaged from the live trace") },
      { k: "channel plan", s: jobs.length ? "ok" : "fail", v: plan },
      // Before the channel block and before any tone: is a tune standing in front of the mic?
      // Read from the device, never assumed - see baseRead.
      baseCheckRow(),
      // The one check that exists purely to stop the run damaging a tune. cmd 11 always carries
      // all sixteen outputs, and a zero gain is 0 dB full level - so if the block for this memory
      // has never been written, a solo would not "leave the levels alone", it would set every one
      // of them to 0 dB. The bridge keeps that flag (outputKnown) and refuses nothing; the run
      // refuses instead.
      { k: "channel block", s: blockOk ? "ok" : "fail",
        v: blockOk
          ? ("memory " + (S.mem + 1) + " established — a solo can be put back exactly")
          : ("memory " + (S.mem + 1) + " output block has not been established. A cmd 11 write "
             + "now would set every output to what is shown (0 dB = full level) instead of leaving "
             + "the tune alone. Load or apply a tune once with the amp attached.") },
      { k: "sub amp", s: RUN.subOff ? "ok" : "warn", manual: true,
        v: "disconnected for the run" }
    ];
  }

  function runRenderChecks(list) {
    var host = document.getElementById("runChecks");
    if (!host) { return; }
    host.textContent = "";
    var mark = { ok: "✓", warn: "!", fail: "✕" };
    for (var i = 0; i < list.length; i++) {
      var c = list[i];
      var row = document.createElement("div");
      row.className = "runck " + c.s + (c.manual ? " manual" : "");
      var ic = document.createElement("span");
      ic.className = "runci";
      ic.textContent = mark[c.s] || "·";
      var kk = document.createElement("span");
      kk.className = "runk";
      kk.textContent = c.k;
      var vv = document.createElement("span");
      vv.className = "runv";
      if (c.manual) {
        var cb = document.createElement("input");
        cb.type = "checkbox";
        cb.id = "runSubOff";
        cb.checked = RUN.subOff;
        cb.title = "the subs are on their own amplifier — muting a DSP channel does not silence them";
        cb.addEventListener("change", function () {
          RUN.subOff = this.checked;
          runLog(RUN.subOff ? "ok" : "warn", RUN.subOff
            ? "sub amp confirmed off for the run — the app cannot see this one"
            : "sub-amp confirmation cleared");
          runEvalState();
        });
        vv.appendChild(cb);
      }
      var txt = document.createElement("span");
      txt.textContent = c.v;
      vv.appendChild(txt);
      row.appendChild(ic);
      row.appendChild(kk);
      row.appendChild(vv);
      host.appendChild(row);
    }
  }

  function runEvalState(list) {
    list = list || runCheckData();
    var fails = 0, w = 0, i;
    for (i = 0; i < list.length; i++) {
      if (list[i].s === "fail") { fails++; }
      if (list[i].s === "warn") { w++; }
    }
    RUN.ready = (fails === 0) && RUN.subOff;
    if (!RUN.busy && Date.now() > RUN.holdUntil) {
      runSet(fails ? "blocked" : (RUN.subOff ? "ready" : "confirm"),
             fails ? "blocked" : (RUN.subOff ? "ready" : "confirm sub"));
    }
    var b = document.getElementById("runStart");
    if (b) {
      b.disabled = !RUN.ready || RUN.busy;
      b.title = RUN.ready ? "" : (fails ? "fix the failing check(s) above"
        : "tick the sub-amp box — the app cannot see the sub amp");
    }
    var st = document.getElementById("runStop");
    if (st) { st.disabled = !RUN.busy; }
    return list;
  }

  // Repaint quietly: only when something actually changed, and never while the pointer is on a
  // control inside the card (a rebuild would take the tick box out from under the click).
  function runRefresh(force) {
    var card = document.getElementById("runCard");
    if (!card) { return null; }
    // A forced refresh is a user action and always rebuilds. A quiet one never does while the
    // pointer or the keyboard is inside the card - that would pull a control out from under a
    // click or a tick.
    if (!force && (card.contains(document.activeElement) || card.matches(":hover"))) { return null; }
    baseRead(false);   // keep the tune check fed; self-throttled, read-only, one chain at a time
    var list = runCheckData();
    var sig = JSON.stringify(list);
    if (force || sig !== RUN.sig) {
      RUN.sig = sig;
      runRenderChecks(list);
    }
    return runEvalState(list);
  }

  // Real checks, real log lines. This is the part that is genuinely wired today.
  function runVerify() {
    var list = runRefresh(true);
    if (!list) { return null; }
    var fails = 0, i;
    for (i = 0; i < list.length; i++) { if (list[i].s === "fail") { fails++; } }
    runLog("step", "verifying the setup");
    for (i = 0; i < list.length; i++) {
      if (list[i].manual) { continue; }
      runLog(list[i].s === "ok" ? "ok" : (list[i].s === "warn" ? "warn" : "fail"),
             list[i].k + " — " + list[i].v);
    }
    var jobs = rosterJobs();
    if (fails) {
      runHold(12000);
      runLog("fail", fails + " check" + (fails === 1 ? "" : "s") + " failing — a run would not start");
    } else if (!RUN.subOff) {
      runLog("warn", "everything the app can see is ready — tick the sub-amp box to arm the run");
    } else {
      runLog("ok", "ready — " + jobs.length + " measurement" + (jobs.length === 1 ? "" : "s")
        + " over " + jobs.length + " channel" + (jobs.length === 1 ? "" : "s"));
    }
    return list;
  }

  function runProgress(done, total, label) {
    var fill = document.getElementById("runBar");
    var st = document.getElementById("runStep");
    if (fill) { fill.style.width = (total ? Math.round(done / total * 100) : 0) + "%"; }
    if (st) {
      st.textContent = total
        ? (done + " of " + total + (label ? " · " + label : " done"))
        : "nothing planned yet";
    }
  }

  // ---- the cycle itself --------------------------------------------------------------
  // A step returns nothing to carry on, { abort: "why" } to stop the run, { skip: n } to jump
  // over the next n steps, or a promise of any of those. Nothing in a step touches the DSP
  // except runWrite(), and every exit path that has already written a solo comes back through
  // runRestoreAfter() - a run must never leave the car with one channel playing.
  var RUN_TONE_FLOOR_DB = -80;   // under this the mic is hearing itself, not a driver
  var RUN_TONE_WAIT_MS = 45000;  // how long to wait for a tone that has not started
  var RUN_SETTLE_MS = 2000;      // steady state before the average is worth trusting

  // The signal, read from the daemon's own number rather than from a level the page tracks:
  // one source for the check, the log and the capture. A frame that stopped arriving is not a
  // reading - a daemon that died mid-run leaves its last number behind, and that number reads
  // exactly like a channel nobody can hear.
  function rtaFresh() { return (Date.now() - RTA.lastOk) < 2000; }

  function rtaLevel() {
    var d = RTA.data;
    if (!rtaFresh()) { return null; }
    return (d && typeof d.levelDb === "number") ? d.levelDb : null;
  }

  // Sleep that ticks a readout and gives up the moment the run is stopped.
  function runWait(ms, tick) {
    return new Promise(function (resolve) {
      var t0 = Date.now();
      (function loop() {
        if (!RUN.busy) { resolve({ abort: "stopped" }); return; }
        var el = Date.now() - t0;
        if (tick) { tick(Math.min(99, Math.round(el / ms * 100))); }
        if (el >= ms) { resolve(null); return; }
        RUN.wait = setTimeout(loop, 120);
      })();
    });
  }

  function runEngine(steps) {
    RUN.steps = steps;
    RUN.at = 0;
    runNext();
  }

  function runNext() {
    if (!RUN.busy) { return; }
    if (RUN.at >= RUN.steps.length) { runFinish(); return; }
    var fn = RUN.steps[RUN.at++], out;
    try { out = fn(); }
    catch (e) { runAbort("step threw: " + e); return; }
    if (out && typeof out.then === "function") {
      out.then(function (r) {
        if (!RUN.busy) { return; }
        if (r && r.abort) { runAbort(r.abort); return; }
        if (r && r.skip) { RUN.at += r.skip; }
        RUN.timer = setTimeout(runNext, RUN.pace);
      }, function (e) {
        if (!RUN.busy) { return; }
        runAbort("step failed: " + (e && e.message ? e.message : e));
      });
    } else {
      if (out && out.abort) { runAbort(out.abort); return; }
      if (out && out.skip) { RUN.at += out.skip; }
      RUN.timer = setTimeout(runNext, RUN.pace);
    }
  }

  // The only DSP write in the module: one cmd 11 frame carrying the COMPLETE block for the
  // running memory, with this step's mute vector. Levels, delays and polarity ride along as they
  // are - a zero in that vector is 0 dB FULL LEVEL, not "leave it alone" - so the block is never
  // assembled from a partial picture, and it is refused outright when this memory's block has
  // never been established, because the write would then overwrite a tune with defaults.
  //
  // dryRun is the rehearsal: the bridge builds the same frame and reports the same masks, and
  // nothing leaves the machine. That is the whole difference between Preview and Start.
  function runWrite(what, mute) {
    if (!CH.known[S.mem]) {
      return { abort: "the output block for memory " + (S.mem + 1) + " has not been "
        + "established — writing cmd 11 now would set every output to what is shown (0 dB = "
        + "full level) instead of leaving the tune alone. Load or apply a tune once first." };
    }
    var body = chBlock(), i;
    body.mute = mute;
    if (RUN.dry) { body.dryRun = true; }
    chBusy = true;
    return api("/api/output", body).then(function (res) {
      chBusy = false;
      if (!res || !res.frameHex) {
        return { abort: what + " — the bridge refused it: " + ((res && res.error) || "no reply") };
      }
      var bits = Number(res.enableMask) || 0;
      var frame = res.frameBytes + " B · enable 0x" + bits.toString(16) + " · "
        + res.outPathCount + " ch (" + res.outPathCountSource + ")";
      if (res.dryRun) {
        runLog("ok", what + " — frame built, NOT transmitted: " + frame + " (rehearsal)");
        return null;
      }
      if (!res.transmitted) {
        runLog("fail", what + " — the frame was built but nothing left the machine: "
          + (res.note || "the DSP link is down"));
        return { abort: "no DSP link — nothing was written" };
      }
      RUN.dirty = true;
      log("cmd 11 run " + what + ": enable=0x" + bits.toString(16)
        + " revert=0x" + (Number(res.revertMask) || 0).toString(16) + " " + frame
        + " " + res.frameHex);
      // The app's own copy of the block follows the write, so the channel strip keeps telling
      // the truth while the run holds the car soloed.
      for (i = 0; i < 16; i++) { CH.mute[i] = !!mute[i]; }
      renderChannels();
      runLog("ok", what + " — cmd 11 written: " + frame + (res.acked ? ""
        : " (the firmware does not answer a write, so this is what was sent, not what the "
          + "device now holds)"));
      return null;
    }).catch(function (e) {
      chBusy = false;
      return { abort: what + " — " + e };
    });
  }

  // Everything down but this one. Only the outputs the product actually has are touched; the
  // rest keep whatever the mirror holds, since cmd 11 always carries all sixteen.
  function runSoloMutes(ch) {
    var v = [], i, n = chVisible(), want = ch - 1;
    for (i = 0; i < 16; i++) { v.push(i < n ? (i !== want) : !!CH.mute[i]); }
    return v;
  }

  // 1. Arm: keep the block that has to come back, and say what the run does and does not touch.
  function runStepArm() {
    RUN.snap = { mute: CH.mute.slice(0, 16), gain: CH.gain.slice(0, 16),
                 delay: CH.delay.slice(0, 16), revert: CH.revert.slice(0, 16), mem: S.mem };
    runLog("step", "arm — memory " + (S.mem + 1) + " of 4, " + RUN.jobs.length
      + " channel" + (RUN.jobs.length === 1 ? "" : "s") + " to measure");
    if (RUN.dry) {
      runLog("info", "every cmd 11 frame below is built by the bridge with dryRun: the real "
        + "bytes and enable mask, nothing transmitted, no tone, nothing captured");
      return null;
    }
    runLog("info", "the only writes in a run are cmd 11 mutes — one output up, the rest down — "
      + "and the block as it is now is written back at the end. Levels, delays and polarity are "
      + "carried through every frame, so nothing else in the tune moves.");
    return null;
  }

  // 2. The tone, before anything is soloed. If the rig is not playing at all, every channel
  //    would come back "no output" and the log would be a wall of crosses saying nothing.
  function runStepTone() {
    var sweep = (STIM_SHAPE === "sweep");
    var what = sweep ? "the sweep (20 Hz → 20 kHz)" : "the pink noise";
    if (RUN.dry) {
      runLog("info", "would wait here for the mic to hear " + what);
      return null;
    }
    if (STIM_SRC === "thispc") {
      stimStart(sweep ? "sweep" : "pink");
      RUN.stim = true;
      runLog("step", "playing " + what + " from this computer — waiting for the mic to hear it");
    } else {
      runLog("step", "start " + what + " on the phone now — the laptop is listening for it");
    }
    var before = rtaLevel();
    return runWaitTone(RUN_TONE_WAIT_MS).then(function (r) {
      if (r && r.abort) { return r; }
      if (r && r.timeout) {
        runLog("fail", "the mic never heard the stimulus in "
          + Math.round(RUN_TONE_WAIT_MS / 1000) + " s — last reading "
          + (r.level === null ? "none" : r.level.toFixed(1) + " dBFS")
          + ". Check that the tone is really playing, that the DSP input is the one the car is "
          + "using, that no output is muted, and that the mic is not on the wrong device.");
        return { abort: "no stimulus reached the mic" };
      }
      runLog("ok", "tone is here — " + r.level.toFixed(1) + " dBFS at the mic (was "
        + (before === null ? "no reading" : before.toFixed(1)) + ")");
      // Start the ring from here, so the per-channel average below is the tone alone. The ring
      // holds ~3 s and the settle is shorter than that, so without this the first channel's
      // average carries whatever the cabin was doing before the tone started.
      if (STIM_SRC === "thispc") { RTA.hist = []; }
      return null;
    });
  }

  // Wait for the mic to hear something, reporting the level while it waits so a tone that is
  // playing too quietly is visible rather than mysterious.
  function runWaitTone(ms) {
    return new Promise(function (resolve) {
      var t0 = Date.now();
      (function loop() {
        if (!RUN.busy) { resolve({ abort: "stopped" }); return; }
        var lvl = rtaLevel();
        var el = Math.round((Date.now() - t0) / 1000);
        setTxt("runStep", "waiting for the tone · " + el + " s of " + Math.round(ms / 1000) + " s"
          + (lvl === null ? " · no reading" : " · " + lvl.toFixed(1) + " dBFS"));
        if (lvl !== null && lvl > RUN_TONE_FLOOR_DB) { resolve({ level: lvl }); return; }
        if (Date.now() - t0 >= ms) { resolve({ timeout: true, level: lvl }); return; }
        RUN.wait = setTimeout(loop, 250);
      })();
    });
  }

  // 3. Solo this channel.
  function runStepSolo(job, idx) {
    runRow(job.ch, "run", 12);
    runProgress(idx, RUN.jobs.length, job.label + " · ch " + job.ch);
    var n = rosterOutCount();
    if (job.ch > n) {
      runRow(job.ch, "fail", 100);
      RUN.bad++;
      runLog("fail", job.label + " — ch " + job.ch + " has no DSP output on this device ("
        + n + " outputs, 1.." + n + "), so there is nothing to solo and nothing to hear. "
        + "Not measured.");
      runProgress(idx + 1, RUN.jobs.length, "");
      return { skip: 2 };              // no settle, no capture: there is nothing to wait for
    }
    runLog("step", "channel " + (idx + 1) + " of " + RUN.jobs.length + " — " + job.label
      + " (ch " + job.ch + ")");
    return runWrite("solo " + job.label + " (output " + job.ch + " up, the rest muted)",
                    runSoloMutes(job.ch));
  }

  // 4. Settle. The average the capture stores has to be steady state, not the tail of the mute
  //    change, so this wait is part of the measurement rather than politeness.
  function runStepSettle(job) {
    runLog("info", (RUN.dry ? "would settle " : "settling ") + (RUN_SETTLE_MS / 1000).toFixed(1)
      + " s - the driver has to reach steady state after the mute change before "
      + (STIM_SHAPE === "sweep" ? "the window opens" : "the average means anything"));
    var bar = function (p) { runRow(job.ch, "run", 12 + Math.round(p * 0.6)); };
    return runWait(RUN.dry ? 600 : RUN_SETTLE_MS, bar);
  }

  // 5a. The sweep flavour of the capture, wired to the daemon's freeze. A sweep is in each
  //     1/6-octave band for a fraction of a second, so the average is not a measurement of the
  //     sweep - it is a measurement of the decay the sweep walked past. The envelope the frozen
  //     peaks leave behind is. Same sequence as the Capture button (rtaSweepWindow owns it), so
  //     the two cannot drift apart.
  function runSweepCapture(job, idx, total) {
    var bar = function (p) { runRow(job.ch, "run", 12 + Math.round(p * 0.8)); };
    runLog("step", job.label + " — freeze the peaks, clear them, then read the envelope after one "
      + "whole " + Math.round(SWEEP_WINDOW_MS / 1000) + " s ramp"
      + (STIM_SRC === "thispc" ? " played from this device" : " played from the phone")
      + ". The verdict is the sweep itself: which bands it lifts, in ascending order.");
    return rtaSweepWindow(function () { return runWait(SWEEP_WINDOW_MS, bar); })
      .then(function (r) {
        var fail = function (msg) {
          runRow(job.ch, "fail", 100);
          RUN.bad++;
          runLog("fail", job.label + " — " + msg);
          runProgress(idx + 1, total, "");
          return null;
        };
        if (r && r.abort) { return r; }          // Stop was pressed; the run is already over
        if (r && r.error) {
          return fail(r.error + ". The held trace is what a sweep measurement is read from, so "
            + "nothing was stored.");
        }
        if (!r || !r.db) {
          return fail("no fresh frame from rta.exe: it stopped answering mid-run, so the envelope "
            + "would have been a curve that no longer exists. Nothing was stored; the rest of the "
            + "run continues.");
        }
        var sig = r.sig || { ok: false, reason: "no signature was taken" };
        if (!sig.ok) {
          return fail("the window did not contain a sweep — " + sig.reason + ". Either nothing is "
            + "wired to output " + job.ch + ", or it is muted, or the stimulus is not "
            + "the ramp. A level check could not tell those apart in a car, so this channel is not "
            + "called measured. The other channels still get measured.");
        }
        var mx = rtaMeasMax(r.db);
        if (mx === null || mx <= RUN_TONE_FLOOR_DB) {
          return fail("a sweep arrived but the frozen envelope peaked at "
            + (mx === null ? "nothing" : mx.toFixed(1) + " dBFS") + " — the ramp is moving through "
            + "the cabin without reaching this output.");
        }
        rtaMeasPush(r.db, job, "sweep", sig);
        runRow(job.ch, "done", 100);
        RUN.ok++;
        runLog("ok", job.label + " — sweep arrived " + sig.from + "–" + sig.to + " Hz across "
          + sig.bands + " bands (rising " + sig.rising + "%), envelope peak " + mx.toFixed(1)
          + " dBFS; stored as " + job.label + " under Live RTA");
        runProgress(idx + 1, total, "");
        return null;
      });
  }

  // 5. Capture: the verdict for this channel. A channel the mic cannot hear is a red cross and
  //    the run carries on - one dead driver must not cost the other five their measurements.
  function runStepCapture(job, idx) {
    var total = RUN.jobs.length;
    if (RUN.dry) {
      runRow(job.ch, "done", 100);
      RUN.ok++;
      runLog("ok", STIM_SHAPE === "sweep"
        ? ("would freeze the peaks, clear them, wait a whole "
           + Math.round(SWEEP_WINDOW_MS / 1000) + " s ramp, check that the sweep actually arrived, "
           + "and store the held envelope as " + job.label)
        : ("would average ~3 s of the live trace and store it as " + job.label));
      runProgress(idx + 1, total, "");
      return null;
    }
    if (STIM_SHAPE === "sweep") { return runSweepCapture(job, idx, total); }
    var lvl = rtaLevel();
    if (lvl === null) {
      runRow(job.ch, "fail", 100);
      RUN.bad++;
      runLog("fail", job.label + " — no reading from the RTA at all: rta.exe stopped answering "
        + "mid-run. The rest of the run continues.");
      runProgress(idx + 1, total, "");
      return null;
    }
    if (lvl <= RUN_TONE_FLOOR_DB) {
      runRow(job.ch, "fail", 100);
      RUN.bad++;
      runLog("fail", job.label + " — the mic is hearing nothing from this channel ("
        + lvl.toFixed(1) + " dBFS). Either nothing is wired to output " + job.ch
        + ", or it is muted, or that driver is not playing. The other channels still get "
        + "measured.");
      runProgress(idx + 1, total, "");
      return null;
    }
    var saved = rtaMeasStore(job);
    if (!saved) {
      runRow(job.ch, "fail", 100);
      RUN.bad++;
      runLog("fail", job.label + " — the capture came back empty and nothing was stored");
      runProgress(idx + 1, total, "");
      return null;
    }
    runRow(job.ch, "done", 100);
    RUN.ok++;
    runLog("ok", job.label + " — " + lvl.toFixed(1) + " dBFS at the mic, averaged and stored in "
      + "the measurements list under Live RTA");
    runProgress(idx + 1, total, "");
    return null;
  }

  // 6. Put the block back. This step never aborts: by now the verdict is what matters, and a
  //    failed restore is shouted about rather than swallowed.
  function runStepRestore() {
    if (RUN.dry) {
      runLog("info", "would write the block back exactly as it was before the run");
      return null;
    }
    if (!RUN.dirty) {
      runLog("info", "no channel write went out — the DSP was never touched");
      return null;
    }
    runLog("step", "putting the channel block back — levels, delays, polarity and mutes as they "
      + "were before the run");
    return runWrite("restore the channel block", RUN.snap.mute).then(function (r) {
      if (!r || !r.abort) {
        RUN.dirty = false;
        runLog("ok", "channels restored — the tune is back as it was");
      } else {
        runLog("fail", "THE CHANNEL BLOCK WAS NOT RESTORED — the DSP may still have all but one "
          + "output muted. Press \"All live\" on the Status tab's channel strip, or re-apply the "
          + "tune.");
      }
      return null;
    });
  }

  // The rule that cannot bend. Stop, an aborted step and a dropped link all come through here,
  // and it is a no-op when nothing was written.
  function runRestoreAfter(why) {
    if (!RUN.dirty || !RUN.snap) { runLock(false); runRowsPaint(); return; }
    runLog("step", "putting the channel block back (" + why + ")");
    runWrite("restore the channel block", RUN.snap.mute).then(function (r) {
      runLock(false);
      if (!r || !r.abort) {
        RUN.dirty = false;
        runLog("ok", "channels restored — the tune is back as it was");
      } else {
        runLog("fail", "THE CHANNEL BLOCK WAS NOT RESTORED — the DSP may still have all but one "
          + "output muted. Press \"All live\" on the Status tab's channel strip, or re-apply the "
          + "tune.");
      }
    });
  }

  // The cycle. mode "preview" comes from Preview, mode "run" from Start, and the only difference
  // is RUN.dry - which turns each write into its dryRun frame and each capture into its log line.
  function runCycle(mode) {
    if (RUN.busy) { return; }
    var jobs = rosterJobs();
    if (!jobs.length) { return; }
    var i;
    // Pre-flight, real runs only: a tweeter whose own high-pass is missing (or under its rated
    // floor) would be handed the full 20 Hz - 20 kHz sweep, and no amount of "\"full range\" for
    // everything" makes that safe for a 1" dome. Rehearsal writes nothing and plays nothing, so
    // it is allowed to run and show the plan.
    if (mode === "run") {
      var unsafe = [], j, why;
      for (j = 0; j < jobs.length; j++) {
        why = sweepBandProblem(jobs[j]);
        if (why) { unsafe.push(jobs[j].label + " (ch " + jobs[j].ch + ") — " + why); }
      }
      if (unsafe.length) {
        runLog("fail", "NOT STARTED — " + unsafe.join("; ") + ".");
        runLog("info", "Put the tweeter protection in place first: .\\crossover-range.ps1 open "
          + "-TweeterFc 1500, then wait for the readback. Cones stay open at full range.");
        toast("tweeter protection is not in place");
        return;
      }
    }
    RUN.mode = mode;
    RUN.jobs = jobs;
    RUN.dry = (mode !== "run");
    RUN.pace = RUN.dry ? 620 : 240;
    RUN.busy = true;
    RUN.at = 0;
    RUN.wait = null;
    RUN.holdUntil = 0;
    RUN.dirty = false;
    RUN.stim = false;
    RUN.snap = null;
    RUN.holdStart = RTA.hold;   // whether the freeze was already on before this run touched it
    RUN.ok = 0;
    RUN.bad = 0;
    runSet("running", RUN.dry ? "rehearsal" : "running");
    runLock(true, jobs);               // gray the plan out and queue every channel
    runProgress(0, jobs.length, "");   // the last run's "4 of 4 done" must not be on screen
    runEvalState();                    // arm Stop / disarm Start for the duration of the run
    // Keep the box on screen while it works - the log is the thing to watch, so line its bottom
    // up with the bottom of the window rather than jumping to the top of the card.
    var card = document.getElementById("runCard");
    if (card && card.scrollIntoView) { card.scrollIntoView({ block: "nearest" }); }
    runLog("info", RUN.dry
      ? "REHEARSAL — the same steps as a real run, with every cmd 11 frame built by the bridge "
        + "and NOT transmitted. No tone is played, no capture is stored."
      : "RUN — " + jobs.length + " channel" + (jobs.length === 1 ? "" : "s") + " on memory "
        + (S.mem + 1) + " of 4. The writes are cmd 11 mutes"
        + (SWEEP.on ? ", with the tune's EQ already flattened by sweep mode" : "")
        + "; everything changed is put back at the end.");
    var steps = [runStepArm, runStepTone];
    for (i = 0; i < jobs.length; i++) {
      (function (job, idx) {
        steps.push(function () { return runStepSolo(job, idx); });
        steps.push(function () { return runStepSettle(job); });
        steps.push(function () { return runStepCapture(job, idx); });
      })(jobs[i], i);
    }
    steps.push(runStepRestore);
    runEngine(steps);
  }

  // Every completed cycle lands on the laptop as one folder, one file per channel. The page
  // cannot write files; the bridge can, and the captures are already in rta-measurements.json,
  // so this just asks for the last N of them to be split out. A run that failed part-way still
  // saves what it captured - a partial set with a stated count beats a silent loss.
  function runSaveToDisk() {
    var n = (RUN.jobs && RUN.jobs.length) ? RUN.jobs.length : 0;
    if (!n) { return; }
    var d = new Date();
    function two(v) { return (v < 10 ? "0" : "") + v; }
    var stamp = d.getFullYear() + "-" + two(d.getMonth() + 1) + "-" + two(d.getDate());
    api("/api/run-save", { count: n, label: "Car tune (" + stamp + ")" }).then(function (r) {
      if (r && r.ok) {
        runLog("ok", "saved on the laptop: " + r.folder + " — " + r.count + " channel file(s)");
      } else {
        runLog("warn", "the run folder was NOT saved: " + ((r && r.error) || "the bridge did not answer"));
      }
    }).catch(function () {
      runLog("warn", "the run folder was NOT saved: the bridge did not answer");
    });
  }

  function runFinish() {
    RUN.busy = false;
    if (RUN.timer) { clearTimeout(RUN.timer); RUN.timer = null; }
    if (RUN.stim) { stimStop(); RUN.stim = false; runLog("info", "stimulus stopped"); }
    runLock(false);                    // editable again; the ticks stay on the rows
    runHold(12000);
    runProgress(RUN.ok + RUN.bad, RUN.jobs.length, "");
    if (RUN.dry) {
      runSet(RUN.bad ? "issues" : "done", RUN.bad ? "rehearsal · issues" : "rehearsal done");
      runLog(RUN.bad ? "warn" : "done", "REHEARSAL complete — " + RUN.ok + " of "
        + RUN.jobs.length + " measurement(s) would have been stored; nothing was written and "
        + "nothing was captured.");
    } else {
      runSet(RUN.bad ? "issues" : "done", RUN.bad ? "done · issues" : "run done");
      runLog(RUN.bad ? "warn" : "done", "RUN complete — " + RUN.ok + " captured, " + RUN.bad
        + " failed. The captures are in the measurements list under Live RTA; Send to PC keeps "
        + "them on this computer.");
      runSaveToDisk();
      if (RUN.sweepOwned) {
        RUN.sweepOwned = false;
        runLog("step", "putting the tune back — sweep mode was on for this run");
        sweepModeExit().then(function (ok) {
          if (ok) {
            runLog("ok", "sweep mode off — the tune is back and the level is back where it was. "
              + "The cable can be pulled whenever you like.");
          } else {
            runLog("fail", "SWEEP MODE DID NOT RESTORE — the tune may still be flattened. Press "
              + "Sweep mode in the Measure card to try again.");
          }
        });
      } else if (!RUN.dirty) {
        runLog("ok", "nothing is pending on the DSP — the cable can be pulled whenever you like.");
      }
    }
    runEvalState();
  }

  function runAbort(reason) {
    if (!RUN.busy) { return; }
    RUN.busy = false;
    if (RUN.timer) { clearTimeout(RUN.timer); RUN.timer = null; }
    if (RUN.wait) { clearTimeout(RUN.wait); RUN.wait = null; }
    if (RUN.stim) { stimStop(); RUN.stim = false; }
    runLog("fail", "run aborted — " + reason);
    runHold(12000);
    runSet("stopped", "aborted");
    runEvalState();
    runRestoreAfter("the run stopped early");
    if (RUN.sweepOwned) {
      RUN.sweepOwned = false;
      sweepModeExit().then(function (ok) {
        runLog(ok ? "ok" : "fail", ok
          ? "sweep mode off — the tune is back as it was"
          : "SWEEP MODE DID NOT RESTORE — press Sweep mode in the Measure card");
      });
    }
  }

  function runPreview() {
    runVerify();                       // the preview shows what a run would have to pass
    runCycle("preview");
  }

  function runStop() {
    RUN.gen = (RUN.gen || 0) + 1;      // cancels a sweep-mode prep that has not started the run
    if (RUN.timer) { clearTimeout(RUN.timer); RUN.timer = null; }
    if (RUN.wait) { clearTimeout(RUN.wait); RUN.wait = null; }
    if (!RUN.busy) { return; }
    RUN.busy = false;
    if (RUN.stim) { stimStop(); RUN.stim = false; runLog("info", "stimulus stopped"); }
    runLog("warn", RUN.dry ? "rehearsal stopped by hand" : "stopped by hand");
    // A half-filled bar would read like a measurement that exists, so whatever was mid-channel
    // goes back to the dash. Channels that already came back keep their tick.
    for (var ch in RUN.rows) {
      if (RUN.rows[ch] && RUN.rows[ch].s === "run") { delete RUN.rows[ch]; }
    }
    runRowsPaint();
    runHold(12000);
    runEvalState();
    runSet("stopped", "stopped");
    runProgress(RUN.ok, RUN.jobs.length, "");
    // If a solo is on the car right now it comes off before anything else - see runRestoreAfter.
    runRestoreAfter("stopped by hand");
    if (RUN.sweepOwned) {
      RUN.sweepOwned = false;
      sweepModeExit().then(function (ok) {
        runLog(ok ? "ok" : "fail", ok
          ? "sweep mode off — the tune memory is back"
          : "SWEEP MODE DID NOT SWITCH BACK — press Sweep mode in the Measure card");
      });
    }
  }

  // Start is where the orchestrator lands. The checks behind it are real, and so is everything
  // the cycle does after it: solo, tone, settle, capture, restore.
  function runStart() {
    if (RUN.busy) { return; }
    var list = runRefresh(true);
    if (!list) { return; }
    if (!RUN.ready) {
      runVerify();                     // spell out what is failing before refusing
      toast("run blocked — see the checks in the run box");
      return;
    }
    if (!rosterJobs().length) { return; }
    RUN.sweepOwned = false;
    if (STIM_SHAPE === "sweep" && !SWEEP.on) {
      RUN.busy = true;                 // hold the button while the memory is being switched
      RUN.gen = (RUN.gen || 0) + 1;
      var prepGen = RUN.gen;
      runSet("running", "sweep mode");
      runLog("step", "sweep mode first — the playing memory is flattened and the level drops, then "
        + "the cycle starts");
      sweepModeEnter().then(function (ok) {
        RUN.busy = false;
        if (prepGen !== RUN.gen) {     // Stop was pressed while the memory moved
          if (SWEEP.on) {
            sweepModeExit().then(function (back) {
              runLog(back ? "ok" : "fail", back
                ? "run cancelled — sweep mode came back off again, the tune is where it was"
                : "run cancelled — SWEEP MODE DID NOT PUT THE TUNE BACK; press Sweep mode to restore");
            });
          }
          return;
        }
        if (!ok) {
          runLog("fail", "run did not start — sweep mode could not be applied");
          runSet("stopped", "sweep mode failed");
          runEvalState();
          return;
        }
        RUN.sweepOwned = true;
        runCycle("run");
      });
      return;
    }
    // Already on: the person turned it on, so the run is not the thing that turns it off.
    runCycle("run");
  }

  (function runWire() {
    var rv = document.getElementById("runVerify");
    var rp = document.getElementById("runPreview");
    var rs = document.getElementById("runStart");
    var rx = document.getElementById("runStop");
    if (rv) { rv.addEventListener("click", function () { runVerify(); }); }
    if (rp) { rp.addEventListener("click", runPreview); }
    if (rs) { rs.addEventListener("click", runStart); }
    if (rx) { rx.addEventListener("click", runStop); }
    runLog("info", "box ready — Verify setup checks the rig; Preview runs the whole cycle with "
      + "every cmd 11 frame built but not transmitted.");
    runRefresh(true);
    setInterval(function () { runRefresh(false); }, 1000);
  })();

  // Closing the page mid-run would leave the DSP soloed with nothing left to put it back, so
  // the browser is asked to confirm first. (A run in flight is a handful of seconds.)
  // Sweep mode is the same kind of promise: the tune's EQ is flattened in the memory that is
  // playing, so leaving without putting it back is a thing the person has to mean.
  window.addEventListener("beforeunload", function (e) {
    // A run that vanishes mid-sweep leaves the daemon frozen: the held peaks would stop decaying
    // and the next session would read a curve left over from this one. keepalive, because the
    // page is going away and a normal fetch would be cancelled with it.
    if (RUN.busy && !RUN.holdStart) {
      try { fetch(RTA_BASE + "/hold?on=0", { keepalive: true }); } catch (x) { /* gone */ }
    }
    if (RUN.busy || RUN.dirty) { e.preventDefault(); e.returnValue = ""; }
    if (SWEEP.on && SWEEP.snap) { e.preventDefault(); e.returnValue = ""; }
  });

  // The single place a capture becomes a stored entry, and the single naming rule: the speaker
  // setup names it, so a finished session reads tweeter 1 / midrange 1 / ... instead of "#1 #2".
  // Shared by the Capture button and the run. `kind` records which curve was stored - a pink-noise
  // average and a frozen sweep envelope are not the same measurement and must not look alike later.
  function rtaMeasPush(db, job, kind, sig) {
    var e = { t: Date.now(), db: db, name: job ? job.label : "", kind: kind || "avg" };
    if (sig) { e.sig = { ok: !!sig.ok, bands: sig.bands, rising: sig.rising, from: sig.from,
                         to: sig.to, burstDb: sig.burstDb, reason: sig.reason }; }
    RTA.meas.push(e);
    if (RTA.meas.length > 24) { RTA.meas.shift(); }
    rtaMeasPersist();
    rtaMeasRender();
    rtaMeasSend(true);        // hand it to the PC unprompted; see rtaMeasSend
    return RTA.meas[RTA.meas.length - 1];
  }

  // Average the ring (~3 s of settled spectrum) into one capture - the pink-noise rule. Stationary
  // signal, so the mean is the measurement and a longer look is a steadier one.
  function rtaMeasStore(job) {
    var d = RTA.data;
    if (!rtaFresh() || !d || !d.bands || !d.bands.length) { return null; }
    var len = d.bands.length, sums = [], cnt = 0, i, k;
    for (i = 0; i < RTA.hist.length; i++) {
      var fr = RTA.hist[i];
      if (fr.length !== len) { continue; }
      for (k = 0; k < len; k++) {
        var v = Number(fr[k]);
        if (!isFinite(v)) { v = -120; }
        sums[k] = (cnt === 0) ? v : sums[k] + v;
      }
      cnt++;
    }
    if (cnt === 0) {                       // no ring yet (first second): single frame
      for (k = 0; k < len; k++) { sums.push(Number(d.bands[k].db) || -120); }
      cnt = 1;
    }
    var db = [];
    for (k = 0; k < len; k++) { db.push(Math.round(sums[k] / cnt * 10) / 10); }
    return rtaMeasPush(db, job, "avg");
  }

  // The held envelope, one point per band, straight from the daemon's own peak trace: what a
  // frozen 20 Hz -> 20 kHz sweep leaves behind. Refuses a stale frame, because a held curve that
  // stopped updating looks exactly like a silent channel and would be stored as a measurement.
  function rtaMeasPeakDb() {
    var d = RTA.data;
    if (!rtaFresh() || !d || !d.bands || !d.bands.length) { return null; }
    var db = [], k;
    for (k = 0; k < d.bands.length; k++) {
      var v = Number(d.bands[k].peak);
      if (!isFinite(v)) { v = -120; }
      db.push(Math.round(v * 10) / 10);
    }
    return db;
  }

  // The loudest point of a stored curve. It is no longer the verdict for a sweep - the signature
  // is (§31.11) - but it still catches a window that was simply dead.
  function rtaMeasMax(db) {
    if (!db || !db.length) { return null; }
    var mx = -120;
    for (var i = 0; i < db.length; i++) { if (db[i] > mx) { mx = db[i]; } }
    return mx;
  }

  // The sweep signature: a sweep is not "loud", it is "moving". A band the ramp crosses jumps far
  // above that band's OWN level for a moment and falls back, and the moment gets later as the
  // frequency rises. A silent room, music and road rumble have neither property - so this is what
  // decides the run's verdict, and the bands it lights up are the driver's real bandwidth, which
  // is worth recording on its own (a midbass that only reaches 120 Hz has told you something).
  //
  // Returns { ok, reason, frames, bands, rising, from, to, burstDb } - `from`/`to` in Hz.
  function rtaSweepSignature(frames) {
    var n = (frames || []).length;
    var out = { ok: false, frames: n, bands: 0, rising: 0, from: null, to: null, burstDb: 0,
                reason: "" };
    if (!n || !frames[0].db || !frames[0].db.length) {
      out.reason = "the instrument was not polled during the window";
      return out;
    }
    if (n < 8) {
      out.reason = "only " + n + " frames arrived during the window (the Tune tab has to stay "
        + "open for the instrument to be read)";
      return out;
    }
    var bands = frames[0].db.length, hot = [], k, i;
    var dwellMax = Math.max(2, Math.round(n * SWEEP_DWELL_PCT / 100));
    for (k = 0; k < bands; k++) {
      var vals = [], mx = -999, tMax = 0, dwell = 0;
      for (i = 0; i < n; i++) {
        var v = Number(frames[i].db[k]);
        if (!isFinite(v)) { v = -120; }
        vals.push(v);
        if (v > mx) { mx = v; tMax = frames[i].t; }
      }
      var s = vals.slice().sort(function (a, b) { return a - b; });
      var med = s[Math.floor(s.length / 2)];
      var rise = mx - med;
      for (i = 0; i < n; i++) { if (vals[i] >= med + SWEEP_BURST_DB) { dwell++; } }
      // Both halves matter. A ramp lifts a band out of its own level AND is gone again quickly:
      // music lifts bands too, but it holds them up, and that is the difference this needs to
      // make. The reference is the band itself, so a car whose bass sits at -40 dBFS all window
      // cannot masquerade as a signal either.
      if (rise >= SWEEP_BURST_DB && dwell <= dwellMax) {
        hot.push({ k: k, rise: rise, dwell: dwell, t: tMax });
      }
    }
    if (!hot.length) {
      out.reason = "no band rose " + SWEEP_BURST_DB + " dB above its own level and fell back "
        + "again inside the window";
      return out;
    }
    // The bands a ramp lights form a run: it walks the whole range it is allowed to reach. Scattered
    // hot bands are a room with transients in it, so the verdict is about the longest run, and that
    // run is also the honest bandwidth statement.
    var best = [], cur = [];
    for (i = 0; i < hot.length; i++) {
      if (cur.length && hot[i].k - cur[cur.length - 1].k > 1 + SWEEP_GAP_BANDS) {
        if (cur.length > best.length) { best = cur; }
        cur = [];
      }
      cur.push(hot[i]);
    }
    if (cur.length > best.length) { best = cur; }
    out.bands = best.length;
    if (best.length < SWEEP_MIN_BANDS) {
      out.reason = "the " + hot.length + " bands that rose out of their own level do not form a "
        + "run (longest is " + best.length + " band" + (best.length === 1 ? "" : "s") + ", need "
        + SWEEP_MIN_BANDS + ") — that is a room with transients in it, not a ramp";
      return out;
    }
    var asc = 0, big = best[0];
    for (i = 0; i < best.length; i++) { if (best[i].rise > big.rise) { big = best[i]; } }
    for (i = 1; i < best.length; i++) { if (best[i].t >= best[i - 1].t) { asc++; } }
    out.rising = Math.round(asc / (best.length - 1) * 100);
    out.from = Math.round(20 * Math.pow(2, best[0].k / 6));
    out.to = Math.round(20 * Math.pow(2, best[best.length - 1].k / 6));
    out.burstDb = Math.round(big.rise * 10) / 10;
    out.ok = (out.rising >= SWEEP_ASCEND_PCT);
    out.reason = out.ok
      ? ("a sweep arrived, low to high, across " + best.length + " bands (" + out.from + "–"
         + out.to + " Hz); the biggest single band rose " + out.burstDb + " dB")
      : ("bands rose briefly, but not in frequency order (rising " + out.rising + "% of the steps, "
         + "need " + SWEEP_ASCEND_PCT + "%) - that is a room or music, not a ramp");
    return out;
  }

  // One frozen sweep window, start to finish: freeze the peaks, clear them (an earlier tone must
  // not flatter this measurement), optionally play a fresh ramp from this computer, wait a whole
  // ramp, then hand back both the held envelope and the signature of what crossed the mic.
  // The caller supplies the wait, so the run can hold its progress bar and its Stop button while
  // the Capture button just sleeps.
  //
  // The freeze and the frame window close on EVERY exit path - failure included - because the
  // daemon is shared with whatever else is drawing the instrument, and a freeze nobody remembers
  // leaving is a curve that stops meaning anything.
  function rtaSweepWindow(waitFn) {
    var prevHold = RTA.hold;
    var back = function (r) {
      RTA.win = null;
      if (!prevHold) { rtaHoldSet(false, true); }
      return r;
    };
    RTA.win = [];                 // the poll starts keeping frames for the signature
    return rtaHoldSet(true, true).then(function (d) {
      if (!d || !d.hold) { throw new Error("rta.exe did not freeze the peaks"); }
      return rtaClearPeaks(true);
    }).then(function (d) {
      if (!d || !d.reset) { throw new Error("rta.exe did not clear the held peaks"); }
      if (STIM_SRC === "thispc") { stimStart("sweep"); RUN.stim = true; }
      return waitFn();
    }).then(function (r) {
      if (r && r.abort) { return back({ abort: r.abort }); }   // stopped by hand
      var frames = RTA.win || [];
      RTA.win = null;
      return back({ db: rtaMeasPeakDb(), sig: rtaSweepSignature(frames) });
    }, function (e) {
      return back({ error: String((e && e.message) || e) });
    });
  }

  // Capture by hand. Both shapes use the same sequence a run uses, so a stored curve means the
  // same thing however it was made.
  function rtaMeasCapture() {
    if (RTA.measBusy) { return; }
    var jobNow = ROSTER_NOW ? rosterJobFor(ROSTER_NOW) : null;
    // A sweep carries its own guard: sweep mode bypasses the tune and drops the level first.
    if (STIM_SHAPE === "sweep") { rtaMeasCaptureSweep(jobNow); return; }
    if (!baseAllowTone()) { return; }
    rtaMeasCaptureAvg(jobNow);
  }

  // Pink noise by hand. Nothing about a stationary tone needs freezing, so the measurement is just
  // the mean of the live ring — but the tone does have to be the ONLY thing in that ring. When this
  // device is the source it is started here, the ring is emptied, and a whole ring's worth of tone
  // is allowed to arrive before the average is taken. Otherwise the average is part pink and part
  // whatever the cabin was doing before, and half a window of silence reads as a driver that rolls
  // off at 500 Hz.
  function rtaMeasCaptureAvg(jobNow) {
    if (RTA.measBusy) { return; }
    RTA.measBusy = true;
    var btn = document.getElementById("rtaMeasSave");
    var mine = (STIM_SRC === "thispc");
    if (btn) { btn.disabled = true; btn.textContent = "listening…"; }
    var finish = function (msg) {
      RTA.measBusy = false;
      if (btn) { btn.disabled = false; btn.textContent = "Capture"; }
      if (mine) { stimStop(); }
      if (msg) { toast(msg); }
    };
    if (mine) { stimStart("pink"); }
    RTA.hist = [];                 // the average must not include what came before the tone
    toast(mine ? "pink noise from this device — letting the trace settle"
               : "listening for pink noise from the phone");
    setTimeout(function () {
      var saved = rtaMeasStore(jobNow);
      if (!saved) { finish("no RTA data yet — nothing stored"); return; }
      finish(saved.name ? ("saved " + saved.name) : ("saved #" + RTA.meas.length));
    }, mine ? RING_SETTLE_MS : 0);
  }

  function rtaMeasCaptureSweep(jobNow) {
    if (RTA.measBusy) { return; }
    // Every sweep runs through this door, so sweep mode is applied here once and then reused:
    // the tune's EQ is flattened and the master drops before the ramp can reach a tweeter.
    if (!SWEEP.on) {
      RTA.measBusy = true;
      sweepModeEnter().then(function (ok) {
        RTA.measBusy = false;
        if (ok) { rtaMeasCaptureSweep(jobNow); }
      });
      return;
    }
    RTA.measBusy = true;
    var btn = document.getElementById("rtaMeasSave");
    if (btn) { btn.disabled = true; btn.textContent = "listening…"; }
    toast("freezing the peaks — one whole " + Math.round(SWEEP_WINDOW_MS / 1000) + " s ramp");
    rtaSweepWindow(function () {
      return new Promise(function (res) { setTimeout(res, SWEEP_WINDOW_MS); });
    }).then(function (r) {
      RTA.measBusy = false;
      if (btn) { btn.disabled = false; btn.textContent = "Capture"; }
      if (r && r.error) { toast("sweep capture failed: " + r.error); return; }
      if (!r || !r.db) { toast("no fresh frame from rta.exe — nothing stored"); return; }
      var d = r.db, sig = r.sig || { ok: false, reason: "no signature was taken" };
      var mx = rtaMeasMax(d);
      if (mx !== null && mx <= RUN_TONE_FLOOR_DB) {
        toast("heard nothing to store (envelope peaked at " + mx.toFixed(1) + " dBFS)");
        return;
      }
      // A tap on Capture is a person saying "store this", so it is stored either way — but the
      // signature is recorded with it, and said out loud, so a curve that is not a sweep cannot
      // pass itself off as one later.
      rtaMeasPush(d, jobNow, "sweep", sig);
      toast("saved " + (jobNow ? jobNow.label : ("#" + RTA.meas.length))
        + (sig.ok ? (" · sweep " + sig.from + "–" + sig.to + " Hz, peak " + mx.toFixed(1) + " dBFS")
                  : (" — but " + sig.reason)));
    });
  }

  // ---- phone → computer hand-off (bridge API, same origin, LAN only) ----
  // The whole list goes in one POST; the bridge merges by capture timestamp, so
  // sending twice never duplicates — safe to hammer "Send to PC" all session.
  //
  // `quiet` is the automatic path taken by every stored capture (rtaMeasPush): a curve sitting in
  // a phone's browser is worth nothing, and the owner should not have to remember a button. It is
  // idempotent, so repeats cost nothing; it stays silent on success and complains exactly once if
  // the capture could not leave the device, because a lost measurement is worth knowing about and
  // a toast per capture is not.
  function rtaMeasSend(quiet) {
    if (!RTA.meas.length) { if (!quiet) { toast("no captures to send"); } return; }
    if (quiet && RTA.sendWarned) { return; }
    return api("/api/rta-meas", { meas: RTA.meas }).then(function (d) {
      if (d.ok) {
        RTA.sendWarned = false;
        if (quiet) { return; }
        toast("sent " + d.added + " to the PC" +
              (d.dupes ? " (" + d.dupes + " already there)" : "") +
              " · PC keeps " + d.total);
        return;
      }
      if (quiet) {
        if (!RTA.sendWarned) { RTA.sendWarned = true;
          toast("capture kept on this device — the PC did not take it (" + (d.error || "?") + ")"); }
        return;
      }
      toast("send failed: " + (d.error || "?"));
    }).catch(function (e) {
      if (quiet) {
        if (!RTA.sendWarned) { RTA.sendWarned = true;
          toast("capture kept on this device — bridge unreachable"); }
        return;
      }
      toast("send failed — bridge offline? " + e);
    });
  }

  function rtaMeasFromPc() {
    api("/api/rta-meas").then(function (d) {
      if (!d.ok) { toast("read failed: " + (d.error || "?")); return; }
      if (!d.meas || !d.meas.length) { toast("nothing saved on the PC yet"); return; }
      RTA.overlay = null;                       // list is about to be re-ordered
      var have = {}, i;
      for (i = 0; i < RTA.meas.length; i++) { have[RTA.meas[i].t] = true; }
      var pulled = 0;
      for (i = 0; i < d.meas.length; i++) {
        var m = d.meas[i];
        if (!m || !m.db || have[m.t]) { continue; }
        RTA.meas.push({ t: m.t, db: m.db, name: m.name || "" });
        have[m.t] = true;
        pulled++;
      }
      RTA.meas.sort(function (a, b) { return a.t - b.t; });
      while (RTA.meas.length > 24) { RTA.meas.shift(); }
      rtaMeasPersist();
      rtaMeasRender();
      rtaDraw();
      toast(pulled ? ("loaded " + pulled + " from PC · list has " + RTA.meas.length)
                   : "everything already on the phone");
    }).catch(function (e) { toast("load failed — bridge offline? " + e); });
  }

  // Mini REW-style thumb: auto-scaled per capture, faint decade grid, 20/20k labels.
  function rtaMeasThumb(cv, db, color) {
    var dpr = window.devicePixelRatio || 1;
    var w = 118, h = 44;
    cv.width = Math.round(w * dpr); cv.height = Math.round(h * dpr);
    var g = cv.getContext("2d");
    g.setTransform(dpr, 0, 0, dpr, 0, 0);
    g.clearRect(0, 0, w, h);
    var lo = 999, hi = -999;
    for (var i = 0; i < db.length; i++) {
      var v = Number(db[i]);
      if (!isFinite(v)) { continue; }
      if (v < lo) { lo = v; }
      if (v > hi) { hi = v; }
    }
    if (lo > hi) { return; }
    if (hi - lo < 10) { var mid = (hi + lo) / 2; lo = mid - 5; hi = mid + 5; }
    lo -= 2; hi += 2;
    var b = 2, pb = 10;                    // pad, bottom room for the axis labels
    var lMin = Math.log(20) / Math.LN10, lMax = Math.log(20000) / Math.LN10;
    function X(f) { return b + (Math.log(f) / Math.LN10 - lMin) / (lMax - lMin) * (w - 2 * b); }
    function Y(v) { return b + (hi - v) / (hi - lo) * (h - b - pb); }
    g.strokeStyle = "#1e232b"; g.lineWidth = 1;
    [100, 1000, 10000].forEach(function (t) {
      var x = X(t);
      g.beginPath(); g.moveTo(x, b); g.lineTo(x, h - pb); g.stroke();
    });
    g.strokeStyle = color; g.lineWidth = 1.4; g.lineJoin = "round";
    g.beginPath();
    for (var k = 0; k < db.length; k++) {
      var f = 20 * Math.pow(2, k / 6);     // same centers the daemon reports
      var vv = Math.max(Number(db[k]), lo);
      if (k === 0) { g.moveTo(X(f), Y(vv)); } else { g.lineTo(X(f), Y(vv)); }
    }
    g.stroke();
    g.font = "8px -apple-system, 'Segoe UI', Roboto, sans-serif";
    g.fillStyle = "#5a6069";
    g.textAlign = "left"; g.textBaseline = "bottom";
    g.fillText("20", 1, h);
    g.textAlign = "right";
    g.fillText("20k", w - 1, h);
  }

  function rtaMeasRender() {
    var list = document.getElementById("rtaMeasList");
    if (!list) { return; }
    list.innerHTML = "";
    if (!RTA.meas.length) {
      var em = document.createElement("div");
      em.className = "hint";
      em.style.padding = "10px";
      em.textContent = "No captures yet — hit Capture while the curve is live.";
      list.appendChild(em);
      return;
    }
    for (var i = 0; i < RTA.meas.length; i++) {
      var m = RTA.meas[i];
      var color = MEAS_COLORS[i % MEAS_COLORS.length];
      var row = document.createElement("div");
      row.className = "measrow" + (RTA.overlay === i ? " on" : "");
      row.setAttribute("data-i", String(i));

      var num = document.createElement("span");
      num.className = "mnum";
      num.textContent = String(i + 1);

      var cv = document.createElement("canvas");
      rtaMeasThumb(cv, m.db, color);

      var meta = document.createElement("div");
      meta.className = "mmeta";

      var nm = document.createElement("input");
      nm.className = "mname";
      nm.type = "text";
      nm.value = m.name || "";
      nm.placeholder = "name\u2026";
      nm.maxLength = 40;
      nm.title = "label this measurement";

      var dt = document.createElement("span");
      dt.className = "mdate";
      // A frozen sweep envelope and a pink-noise average are different measurements, and a sweep
      // whose signature did not check out is a third thing: the list says which is which, and the
      // row's tooltip carries the bandwidth the ramp actually lit up.
      var isSw = (m.kind === "sweep");
      dt.textContent = rtaMeasDate(m.t)
        + (isSw ? (m.sig && m.sig.ok === false ? " · sweep?" : " · sweep") : "");
      if (isSw && m.sig) {
        row.title = "sweep: " + m.sig.reason
          + (m.sig.ok ? (" · covered " + m.sig.from + "–" + m.sig.to + " Hz") : "");
      }

      meta.appendChild(nm);
      meta.appendChild(dt);

      var del = document.createElement("button");
      del.className = "mdel";
      del.textContent = "\u00d7";          // ×
      del.title = "delete";

      row.appendChild(num); row.appendChild(cv); row.appendChild(meta); row.appendChild(del);
      list.appendChild(row);
    }
  }

  // One delegated handler: row/canvas/date tap = toggle overlay, × = delete.
  document.getElementById("rtaMeasList").addEventListener("click", function (ev) {
    var t = ev.target;
    var row = t.closest ? t.closest(".measrow") : null;
    if (!row) { return; }
    if (t.classList && t.classList.contains("mname")) { return; }  // editing name, not overlay
    var i = Number(row.getAttribute("data-i"));
    if (t.classList.contains("mdel")) {
      RTA.meas.splice(i, 1);
      if (RTA.overlay === i) { RTA.overlay = null; }
      else if (RTA.overlay !== null && RTA.overlay > i) { RTA.overlay--; }
      rtaMeasPersist(); rtaMeasRender(); rtaDraw();
      toast("deleted #" + (i + 1));
      return;
    }
    RTA.overlay = (RTA.overlay === i) ? null : i;
    rtaMeasRender();
    rtaDraw();
    toast(RTA.overlay === null ? "overlay off" : "overlay #" + (i + 1) + " on big plot");
  });

  // Rename: live-save on every keystroke (no re-render — that would eat the focus).
  document.getElementById("rtaMeasList").addEventListener("input", function (ev) {
    var t = ev.target;
    if (!t.classList || !t.classList.contains("mname")) { return; }
    var row = t.closest ? t.closest(".measrow") : null;
    if (!row) { return; }
    var i = Number(row.getAttribute("data-i"));
    if (!RTA.meas[i]) { return; }
    RTA.meas[i].name = t.value;
    rtaMeasPersist();
  });

  function rtaDraw() {
    var cv = document.getElementById("rtaCurve");
    var d = RTA.data;
    if (!cv || !cv.getContext || !d || !d.bands || !d.bands.length) { return; }
    var dpr = window.devicePixelRatio || 1;
    var w = cv.clientWidth || 320, h = cv.clientHeight || 190;
    cv.width = Math.round(w * dpr); cv.height = Math.round(h * dpr);
    var g = cv.getContext("2d");
    g.setTransform(dpr, 0, 0, dpr, 0, 0);
    g.clearRect(0, 0, w, h);
    var padL = 40, padR = 8, padT = 10, padB = 18;
    var pw = w - padL - padR, ph = h - padT - padB;
    var lMin = Math.log(20) / Math.LN10, lMax = Math.log(20000) / Math.LN10;

    // Floor follows the data (a quiet cabin sits near -100; don't waste half the plot).
    var lo = -100, hi = 0;
    for (var i = 0; i < d.bands.length; i++) {
      var v = Number(d.bands[i].db);
      if (isFinite(v) && v < lo) { lo = v; }
    }
    lo = Math.floor(lo / 10) * 10;
    // overlaid capture must fit the y-range too, or it lands off-plot
    var ov = (RTA.overlay !== null && RTA.meas[RTA.overlay]) ? RTA.meas[RTA.overlay] : null;
    if (ov) {
      for (var oi = 0; oi < ov.db.length; oi++) {
        var ovv = Number(ov.db[oi]);
        if (isFinite(ovv) && ovv < lo) { lo = ovv; }
      }
      lo = Math.floor(lo / 10) * 10;
    }
    function X(f) { return padL + (Math.log(f) / Math.LN10 - lMin) / (lMax - lMin) * pw; }
    function Y(v) { return padT + (hi - v) / (hi - lo) * ph; }

    g.font = "10px -apple-system, 'Segoe UI', Roboto, sans-serif";
    g.textAlign = "right"; g.textBaseline = "middle";
    for (var db = lo; db <= hi; db += 20) {
      var y = Y(db);
      g.strokeStyle = "#1c2027"; g.lineWidth = 1;
      g.beginPath(); g.moveTo(padL, y); g.lineTo(padL + pw, y); g.stroke();
      g.fillStyle = "#7c828b"; g.fillText(db, padL - 5, y);
    }
    var ticks = [20, 50, 100, 500, 1000, 5000, 20000];
    g.textAlign = "center"; g.textBaseline = "top";
    ticks.forEach(function (t) {
      var x = X(t);
      g.strokeStyle = "#171a20";
      g.beginPath(); g.moveTo(x, padT); g.lineTo(x, padT + ph); g.stroke();
      g.fillStyle = "#7c828b";
      g.fillText(t >= 1000 ? (t / 1000) + "k" : String(t), x, padT + ph + 4);
    });

    // peak hold first (under), live curve over
    if (RTA.peak) {
      g.strokeStyle = "rgba(132,191,93,.55)"; g.lineWidth = 1;
      g.beginPath();
      for (var k = 0; k < d.bands.length; k++) {
        var pk = Number(d.bands[k].peak);
        if (!isFinite(pk)) { continue; }
        var px = X(Number(d.bands[k].f)), py = Y(Math.max(pk, lo));
        if (k === 0) { g.moveTo(px, py); } else { g.lineTo(px, py); }
      }
      g.stroke();
    }
    g.strokeStyle = "#0babf2"; g.lineWidth = 2; g.lineJoin = "round";
    g.beginPath();
    for (var b = 0; b < d.bands.length; b++) {
      var bv = Math.max(Number(d.bands[b].db), lo);
      var bx = X(Number(d.bands[b].f)), byy = Y(bv);
      if (b === 0) { g.moveTo(bx, byy); } else { g.lineTo(bx, byy); }
    }
    g.stroke();

    // saved measurement overlaid: dashed amber, aligned to the live band centers
    if (ov) {
      g.strokeStyle = "#e8a33d"; g.lineWidth = 2;
      g.setLineDash([6, 4]);
      g.beginPath();
      for (var m2 = 0; m2 < ov.db.length; m2++) {
        var mv = Math.max(Number(ov.db[m2]), lo);
        var mf = (d.bands[m2] ? Number(d.bands[m2].f) : 20 * Math.pow(2, m2 / 6));
        var mx = X(mf), my = Y(mv);
        if (m2 === 0) { g.moveTo(mx, my); } else { g.lineTo(mx, my); }
      }
      g.stroke();
      g.setLineDash([]);
    }

    // target curve: violet dashed, level-aligned to the live curve (the mic chain has
    // no SPL cal, so only the SHAPE is comparable - shift the target to the measured
    // curve's mean level over 200 Hz - 5 kHz, the band the ear judges tilt by).
    if (TGT.db && d.bands.length) {
      var sumM = 0, sumT = 0, nMT = 0;
      for (var ai = 0; ai < d.bands.length; ai++) {
        var af = Number(d.bands[ai].f), av2 = Number(d.bands[ai].db);
        if (af < 200 || af > 5000 || !isFinite(av2)) { continue; }
        var tv2 = tgtAt(af);
        if (tv2 === null) { continue; }
        sumM += av2; sumT += tv2; nMT++;
      }
      if (nMT > 0) {
        var tShift = (sumM - sumT) / nMT;
        g.strokeStyle = "#b38cff"; g.lineWidth = 2;
        g.setLineDash([3, 3]);
        g.beginPath();
        for (var ti = 0; ti < d.bands.length; ti++) {
          var tf = Number(d.bands[ti].f);
          var td = tgtAt(tf);
          if (td === null) { continue; }
          var tyv = Math.max(td + tShift, lo);
          var tx = X(tf), ty2 = Y(tyv);
          if (ti === 0) { g.moveTo(tx, ty2); } else { g.lineTo(tx, ty2); }
        }
        g.stroke();
        g.setLineDash([]);
      }
    }
  }

  // Stimulus: Web Audio on THIS device. Pink = Paul Kellet generator; sweep = one
  // exponential ramp 20 -> 20k. Both run through a gain node so the slider is live.
  function stimGainDb() {
    return Number(document.getElementById("rtaStimGain").value);
  }
  function stimApplyGain() {
    if (RTA.gainNode) {
      RTA.gainNode.gain.value = Math.pow(10, stimGainDb() / 20);
    }
    document.getElementById("rtaStimGainVal").textContent = stimGainDb() + " dB";
  }
  function stimStop() {
    if (RTA.src) {
      try { RTA.src.stop(); } catch (e) { /* already stopped */ }
      try { RTA.src.disconnect(); } catch (e) { /* gone */ }
      RTA.src = null;
    }
  }
  function stimStart(kind) {
    try {
      if (!RTA.actx) {
        var AC = window.AudioContext || window.webkitAudioContext;
        RTA.actx = new AC();
        RTA.gainNode = RTA.actx.createGain();
        RTA.gainNode.connect(RTA.actx.destination);
      }
      if (RTA.actx.state === "suspended") { RTA.actx.resume(); }
      stimStop();
      stimApplyGain();
      // Playing a shape from here is also declaring it: the capture rule has to match what is
      // actually in the air, and this is the app's own signal.
      stimShapeSet(kind);
      if (kind === "pink") {
        var len = RTA.actx.sampleRate * 2;
        var buf = RTA.actx.createBuffer(1, len, RTA.actx.sampleRate);
        var ch = buf.getChannelData(0);
        var b0 = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0, b5 = 0, b6 = 0;
        for (var i = 0; i < len; i++) {
          var wn = Math.random() * 2 - 1;
          b0 = 0.99886 * b0 + wn * 0.0555179;
          b1 = 0.99332 * b1 + wn * 0.0750759;
          b2 = 0.96900 * b2 + wn * 0.1538520;
          b3 = 0.86650 * b3 + wn * 0.3104856;
          b4 = 0.55000 * b4 + wn * 0.5329522;
          b5 = -0.7616 * b5 - wn * 0.0168980;
          ch[i] = (b0 + b1 + b2 + b3 + b4 + b5 + b6 + wn * 0.5362) * 0.11;
          b6 = wn * 0.115926;
        }
        var src = RTA.actx.createBufferSource();
        src.buffer = buf; src.loop = true;
        src.connect(RTA.gainNode); src.start(0);
        RTA.src = src;
      } else if (kind === "sweep") {
        var o = RTA.actx.createOscillator();
        o.type = "sine";
        var t0 = RTA.actx.currentTime, T = 10;
        o.frequency.setValueAtTime(20, t0);
        o.frequency.exponentialRampToValueAtTime(20000, t0 + T);
        o.connect(RTA.gainNode); o.start(t0); o.stop(t0 + T);
        RTA.src = o;
      }
      toast("stimulus: " + kind + " @ " + stimGainDb() + " dB");
    } catch (e) { toast("stimulus failed: " + e); }
  }

  // ---------- targets (house curve + per-channel aim) ----------
  // The target set: a 31-point house curve + each channel's crossover window - the
  // same model the Jazzi spreadsheet held, now in the app and saved via the bridge
  // (targets.json), so the phone can shape curves without the laptop or Excel.
  var TGT_FREQS = [20, 25, 31, 40, 50, 63, 80, 100, 125, 160, 200, 250, 315, 400,
                   500, 630, 800, 1000, 1200, 1600, 2000, 2500, 3100, 4000, 5000,
                   6300, 8000, 10000, 12000, 16000, 20000];
  var TGT_PRESETS = {
    "Whitledge": [24,22,20,16,11,8,6,4,3,1.5,0.5,0,-1,-2,-2.25,-2.5,-2.5,-3,-3.3,-3.6,-4,-4.5,-5,-5.5,-6,-6.75,-7.5,-8,-8.75,-9.5,-10],
    "Flat": [0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0],
    "ATF": [10,10,10,10,9.5,9,8.5,8,7,6.5,6,5,4,3,2.5,2,1.5,1,1,0,0,0,0,0,0,0,-1,-2,-3,-4,-5],
    "Audiofrog": [10,9.8,9.5,8.8,7.8,6.4,4.9,3.3,1.9,0.9,0.4,0.1,0,0,0,0,0,0,0,-0.1,-0.2,-0.5,-0.9,-1.4,-2,-2.5,-3.1,-3.9,-4.5,-5.5,-6.2],
    "EPY": [10,10,10,10,10,9,8,7,6,5,4,3,2,1,0,0,0,0,0,0,0,0,0,0,0,0,-1,-2,-2.5,-3,-3.5],
    "Harman": [5.4,5.4,5.4,5.4,5.2,4.9,4,2.8,1.6,0.5,-0.1,-0.3,-0.5,-0.6,-0.7,-0.7,-0.9,-1,-1.1,-1.3,-1.4,-1.5,-1.7,-1.9,-2,-2.2,-2.4,-2.6,-2.9,-3.2,-3.5],
    "Jazzi": [10.5,9,7.25,5.75,4.5,3.25,3,2.25,1.5,0.75,0,0,0,0,0,0,0,0,0,0,-1.5,-3,-3,-1.5,0,0,0,0,0,0,0],
    "Jazzi v2": [17,17,17,17,16.5,16,13,11,8.5,7,6,5,4,3,2.5,2,1,0.5,0,-2,-3,-4,-3.5,-3,-1,0,0,0,0,0,0]
  };
  // Channels seeded with w6784b's real crossovers (read from the file) + the walk-map
  // speaker names (protocol doc 18). CH 1-8 here = outputs 0-7 everywhere else.
  var TGT = {
    name: "Whitledge",
    db: TGT_PRESETS["Whitledge"].slice(),
    offsetDb: 0,
    dirty: false,
    chans: [
      { name: "Driver TW",      hp: 2000, lp: 0 },
      { name: "Pass TW",        hp: 2000, lp: 0 },
      { name: "Rear driver TW", hp: 2000, lp: 0 },
      { name: "Rear pass TW",   hp: 2000, lp: 0 },
      { name: "Front 3.5",      hp: 250,  lp: 2500 },
      { name: "Rear 3.5",       hp: 250,  lp: 2500 },
      { name: "Front 8",        hp: 150,  lp: 2000 },
      { name: "Rear 6.5",       hp: 120,  lp: 1000 }
    ]
  };

  // House curve at any frequency (log interpolation) + the global offset.
  function tgtAt(f) {
    if (!TGT.db || !TGT.db.length) { return null; }
    var v = null;
    if (f <= TGT_FREQS[0]) { v = TGT.db[0]; }
    else if (f >= TGT_FREQS[30]) { v = TGT.db[30]; }
    else {
      for (var i = 1; i < 31; i++) {
        if (f <= TGT_FREQS[i]) {
          var t = (Math.log(f) - Math.log(TGT_FREQS[i - 1])) /
                  (Math.log(TGT_FREQS[i]) - Math.log(TGT_FREQS[i - 1]));
          v = TGT.db[i - 1] + (TGT.db[i] - TGT.db[i - 1]) * t;
          break;
        }
      }
    }
    return v === null ? null : v + TGT.offsetDb;
  }

  // LR24 high/low-pass magnitude in dB (two cascaded BW2 sections: -6 dB at Fc,
  // 24 dB/oct away) - models each channel's crossover window the way the Jazzi
  // sheet's per-driver targets did.
  function tgtHpDb(f, fc) { return fc > 0 ? -20 * Math.log(1 + Math.pow(fc / f, 4)) / Math.LN10 : 0; }
  function tgtLpDb(f, fc) { return fc > 0 ? -20 * Math.log(1 + Math.pow(f / fc, 4)) / Math.LN10 : 0; }

  function tgtNameShow() {
    document.getElementById("tgtName").textContent = TGT.name + (TGT.dirty ? " (edited)" : "");
  }

  function tgtHouseDraw() {
    var cv = document.getElementById("tgtHouse");
    if (!cv || !cv.getContext || !cv.clientWidth) { return; }
    var dpr = window.devicePixelRatio || 1;
    var w = cv.clientWidth, h = cv.clientHeight || 190;
    cv.width = Math.round(w * dpr); cv.height = Math.round(h * dpr);
    var g = cv.getContext("2d");
    g.setTransform(dpr, 0, 0, dpr, 0, 0);
    g.clearRect(0, 0, w, h);
    var padL = 36, padR = 10, padT = 12, padB = 20;
    var pw = w - padL - padR, ph = h - padT - padB;
    var lMin = Math.log(20) / Math.LN10, lMax = Math.log(20000) / Math.LN10;
    var lo = -20, hi = 30;
    function X(f) { return padL + (Math.log(f) / Math.LN10 - lMin) / (lMax - lMin) * pw; }
    function Y(v) { return padT + (hi - v) / (hi - lo) * ph; }
    g.font = "10px -apple-system, 'Segoe UI', Roboto, sans-serif";
    g.textAlign = "right"; g.textBaseline = "middle";
    for (var db = lo; db <= hi; db += 10) {
      var y = Y(db);
      g.strokeStyle = "#1c2027"; g.lineWidth = 1;
      g.beginPath(); g.moveTo(padL, y); g.lineTo(padL + pw, y); g.stroke();
      g.fillStyle = "#7c828b"; g.fillText(db, padL - 5, y);
    }
    var ticks = [20, 50, 100, 500, 1000, 5000, 20000];
    g.textAlign = "center"; g.textBaseline = "top";
    ticks.forEach(function (t) {
      var x = X(t);
      g.strokeStyle = "#171a20";
      g.beginPath(); g.moveTo(x, padT); g.lineTo(x, padT + ph); g.stroke();
      g.fillStyle = "#7c828b";
      g.fillText(t >= 1000 ? (t / 1000) + "k" : String(t), x, padT + ph + 4);
    });
    g.strokeStyle = "#b38cff"; g.lineWidth = 2; g.lineJoin = "round";
    g.beginPath();
    for (var i = 0; i < 31; i++) {
      var px = X(TGT_FREQS[i]), py = Y(Math.max(lo, Math.min(hi, TGT.db[i] + TGT.offsetDb)));
      if (i === 0) { g.moveTo(px, py); } else { g.lineTo(px, py); }
    }
    g.stroke();
    g.fillStyle = "#b38cff";
    for (var k = 0; k < 31; k++) {
      g.beginPath();
      g.arc(X(TGT_FREQS[k]), Y(Math.max(lo, Math.min(hi, TGT.db[k] + TGT.offsetDb))), 2.5, 0, 6.29);
      g.fill();
    }
  }

  function tgtSlicesDraw() {
    var cv = document.getElementById("tgtSlices");
    if (!cv || !cv.getContext || !cv.clientWidth) { return; }
    var dpr = window.devicePixelRatio || 1;
    var w = cv.clientWidth, h = cv.clientHeight || 190;
    cv.width = Math.round(w * dpr); cv.height = Math.round(h * dpr);
    var g = cv.getContext("2d");
    g.setTransform(dpr, 0, 0, dpr, 0, 0);
    g.clearRect(0, 0, w, h);
    var padL = 36, padR = 10, padT = 12, padB = 20;
    var pw = w - padL - padR, ph = h - padT - padB;
    var lMin = Math.log(20) / Math.LN10, lMax = Math.log(20000) / Math.LN10;
    var lo = -45, hi = 30;
    function X(f) { return padL + (Math.log(f) / Math.LN10 - lMin) / (lMax - lMin) * pw; }
    function Y(v) { return padT + (hi - v) / (hi - lo) * ph; }
    g.font = "10px -apple-system, 'Segoe UI', Roboto, sans-serif";
    g.textAlign = "right"; g.textBaseline = "middle";
    for (var db = lo; db <= hi; db += 15) {
      var y = Y(db);
      g.strokeStyle = "#1c2027"; g.lineWidth = 1;
      g.beginPath(); g.moveTo(padL, y); g.lineTo(padL + pw, y); g.stroke();
      g.fillStyle = "#7c828b"; g.fillText(db, padL - 5, y);
    }
    var ticks = [20, 50, 100, 500, 1000, 5000, 20000];
    g.textAlign = "center"; g.textBaseline = "top";
    ticks.forEach(function (t) {
      var x = X(t);
      g.strokeStyle = "#171a20";
      g.beginPath(); g.moveTo(x, padT); g.lineTo(x, padT + ph); g.stroke();
      g.fillStyle = "#7c828b";
      g.fillText(t >= 1000 ? (t / 1000) + "k" : String(t), x, padT + ph + 4);
    });
    // the raw house curve first, dim, as reference
    g.strokeStyle = "rgba(179,140,255,.35)"; g.lineWidth = 1.5;
    g.beginPath();
    for (var i2 = 0; i2 < 31; i2++) {
      var hv = tgtAt(TGT_FREQS[i2]);
      var hx = X(TGT_FREQS[i2]), hy = Y(Math.max(lo, Math.min(hi, hv)));
      if (i2 === 0) { g.moveTo(hx, hy); } else { g.lineTo(hx, hy); }
    }
    g.stroke();
    // each channel's slice: house curve through its own crossover window
    TGT.chans.forEach(function (c, ci) {
      g.strokeStyle = CH_COLORS[ci]; g.lineWidth = 2; g.lineJoin = "round";
      g.beginPath();
      for (var j = 0; j < 31; j++) {
        var f = TGT_FREQS[j];
        var v = tgtAt(f) + tgtHpDb(f, c.hp) + tgtLpDb(f, c.lp);
        var sx = X(f), sy = Y(Math.max(lo, Math.min(hi, v)));
        if (j === 0) { g.moveTo(sx, sy); } else { g.lineTo(sx, sy); }
      }
      g.stroke();
    });
    var lg = document.getElementById("tgtSliceLegend");
    lg.innerHTML = "";
    TGT.chans.forEach(function (c, i3) {
      var s = document.createElement("span");
      s.className = "curswatch";
      s.style.background = CH_COLORS[i3];
      var t = document.createElement("span");
      t.textContent = c.name + " " + (c.hp || "20") + "-" + (c.lp ? (c.lp >= 1000 ? (c.lp / 1000) + "k" : c.lp) : "20k");
      lg.appendChild(s); lg.appendChild(t);
    });
  }

  function tgtPresetsRender() {
    var host = document.getElementById("tgtPresets");
    host.innerHTML = "";
    var names = Object.keys(TGT_PRESETS);
    if (TGT.name === "Custom" && names.indexOf("Custom") < 0) { names.push("Custom"); }
    names.forEach(function (n) {
      var chip = document.createElement("div");
      chip.className = "chip" + (n === TGT.name ? " on" : "");
      chip.textContent = n;
      chip.addEventListener("click", function () {
        if (n !== "Custom") {
          TGT.name = n;
          TGT.db = TGT_PRESETS[n].slice();
        }
        TGT.dirty = true;
        tgtPresetsRender(); tgtHouseDraw(); tgtSlicesDraw(); tgtNameShow();
        tgtQueueSave();
      });
      host.appendChild(chip);
    });
  }

  function tgtChansRender() {
    var host = document.getElementById("tgtChans");
    host.innerHTML = "";
    TGT.chans.forEach(function (c, i) {
      var row = document.createElement("div");
      row.className = "row";
      row.innerHTML =
        '<span class="k" style="color:' + CH_COLORS[i] + ';flex:0 0 auto;">CH ' + (i + 1) + '</span>' +
        '<input type="text" class="tgn" style="flex:1;min-width:0;background:#111;color:#ddd;border:1px solid #333;padding:5px;border-radius:6px;font-size:12px;">' +
        '<input type="number" class="tghp" min="0" max="20000" step="10" title="HP (Hz), 0 = off" style="width:62px;background:#111;color:#ddd;border:1px solid #333;padding:5px;border-radius:6px;font-size:12px;">' +
        '<input type="number" class="tglp" min="0" max="20000" step="10" title="LP (Hz), 0 = off" style="width:62px;background:#111;color:#ddd;border:1px solid #333;padding:5px;border-radius:6px;font-size:12px;">';
      var nameIn = row.querySelector(".tgn"), hpIn = row.querySelector(".tghp"),
          lpIn = row.querySelector(".tglp");
      nameIn.value = c.name;
      hpIn.value = c.hp;
      lpIn.value = c.lp;
      nameIn.addEventListener("change", function () {
        c.name = nameIn.value; TGT.dirty = true; tgtSlicesDraw(); tgtQueueSave();
      });
      hpIn.addEventListener("change", function () {
        c.hp = Math.max(0, Math.min(20000, Number(hpIn.value) || 0));
        hpIn.value = c.hp; TGT.dirty = true; tgtSlicesDraw(); tgtQueueSave();
      });
      lpIn.addEventListener("change", function () {
        c.lp = Math.max(0, Math.min(20000, Number(lpIn.value) || 0));
        lpIn.value = c.lp; TGT.dirty = true; tgtSlicesDraw(); tgtQueueSave();
      });
      host.appendChild(row);
    });
  }

  var tgtSaveTimer = null;
  function tgtQueueSave() {
    document.getElementById("tgtSaveState").textContent = "unsaved changes...";
    if (tgtSaveTimer) { clearTimeout(tgtSaveTimer); }
    tgtSaveTimer = setTimeout(tgtSave, 1200);
  }
  function tgtSave() {
    fetch("/api/targets", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ targets: { name: TGT.name, db: TGT.db,
                                        offsetDb: TGT.offsetDb, chans: TGT.chans } })
    }).then(function (r) { return r.json(); }).then(function (d) {
      TGT.dirty = !d.ok;
      document.getElementById("tgtSaveState").textContent = d.ok
        ? "saved to bridge (" + d.bytes + " bytes)"
        : "save failed: " + (d.error || "?");
      tgtNameShow();
    }).catch(function (e) {
      document.getElementById("tgtSaveState").textContent = "save failed: " + e;
    });
  }
  function tgtLoad() {
    fetch("/api/targets", { cache: "no-store" }).then(function (r) { return r.json(); }).then(function (d) {
      var t = d && d.targets;
      if (t && t.db && t.db.length === 31) {
        TGT.name = (typeof t.name === "string" && t.name) ? t.name : "Custom";
        TGT.db = t.db.map(Number);
        TGT.offsetDb = Number(t.offsetDb) || 0;
        if (t.chans && t.chans.length === 8) {
          for (var i = 0; i < 8; i++) {
            if (typeof t.chans[i].name === "string") { TGT.chans[i].name = t.chans[i].name; }
            TGT.chans[i].hp = Number(t.chans[i].hp) || 0;
            TGT.chans[i].lp = Number(t.chans[i].lp) || 0;
          }
        }
        document.getElementById("tgtSaveState").textContent = "loaded saved targets";
      } else {
        document.getElementById("tgtSaveState").textContent = "no saved targets yet - using defaults";
      }
      document.getElementById("tgtOffset").value = String(TGT.offsetDb);
      document.getElementById("tgtOffsetVal").textContent = TGT.offsetDb + " dB";
      tgtPresetsRender(); tgtChansRender(); tgtNameShow();
    }).catch(function () {
      document.getElementById("tgtSaveState").textContent = "bridge unreachable - using defaults";
      tgtPresetsRender(); tgtChansRender(); tgtNameShow();
    });
  }

  // drag anywhere on the house chart: snaps to the nearest of the 31 points
  (function () {
    var cv = document.getElementById("tgtHouse");
    if (!cv) { return; }
    var dragging = false;
    function applyDrag(ev) {
      var rect = cv.getBoundingClientRect();
      var x = ev.clientX - rect.left, y = ev.clientY - rect.top;
      var padL = 36, padR = 10, padT = 12, padB = 20;
      var pw = cv.clientWidth - padL - padR, ph = (cv.clientHeight || 190) - padT - padB;
      var lMin = Math.log(20) / Math.LN10, lMax = Math.log(20000) / Math.LN10;
      var lo = -20, hi = 30;
      var f = Math.pow(10, lMin + (x - padL) / pw * (lMax - lMin));
      var val = hi - (y - padT) / ph * (hi - lo);
      val = Math.max(-20, Math.min(30, Math.round(val * 2) / 2));
      var best = 0, bd = 1e9;
      for (var i = 0; i < 31; i++) {
        var d2 = Math.abs(Math.log(TGT_FREQS[i]) - Math.log(f));
        if (d2 < bd) { bd = d2; best = i; }
      }
      TGT.db[best] = val - TGT.offsetDb;
      var pre = TGT_PRESETS[TGT.name];
      if (pre && pre[best] !== TGT.db[best]) { TGT.name = "Custom"; }
      TGT.dirty = true;
      tgtPresetsRender(); tgtHouseDraw(); tgtSlicesDraw(); tgtNameShow();
      document.getElementById("tgtReadout").textContent =
        TGT_FREQS[best] + " Hz: " + (val >= 0 ? "+" : "") + val + " dB";
      tgtQueueSave();
    }
    cv.addEventListener("pointerdown", function (ev) {
      dragging = true;
      try { cv.setPointerCapture(ev.pointerId); } catch (e) { /* no capture */ }
      applyDrag(ev); ev.preventDefault();
    });
    cv.addEventListener("pointermove", function (ev) { if (dragging) { applyDrag(ev); } });
    cv.addEventListener("pointerup", function () { dragging = false; });
    cv.addEventListener("pointercancel", function () { dragging = false; });
  })();

  document.getElementById("tgtOffset").addEventListener("input", function () {
    TGT.offsetDb = Number(this.value);
    document.getElementById("tgtOffsetVal").textContent = TGT.offsetDb + " dB";
    tgtHouseDraw(); tgtSlicesDraw();
  });
  document.getElementById("tgtOffset").addEventListener("change", function () {
    TGT.dirty = true; tgtQueueSave();
  });
  document.getElementById("tgtSaveBtn").addEventListener("click", function () {
    if (tgtSaveTimer) { clearTimeout(tgtSaveTimer); tgtSaveTimer = null; }
    tgtSave();
  });

  // ---------- wiring ----------
  function showTab(name) {
    var secs = document.querySelectorAll("section");
    for (var i = 0; i < secs.length; i++) secs[i].classList.remove("active");
    document.getElementById("tab-" + name).classList.add("active");
    var btns = document.querySelectorAll("nav button");
    for (var j = 0; j < btns.length; j++) btns[j].classList.toggle("on", btns[j].dataset.tab === name);
    // A hidden canvas reports clientWidth 0, so a curve has to be re-laid out the moment
    // its section becomes visible.
    if (name === "eq") {
      drawEqCurve();
      if (SYS.cells && SYS.mem === S.mem) { sysDraw(); }
      else if (S.connected) { sysRefresh(); }   // ghosts need every channel's bands
    }
    if (name === "status") { drawHomeCurve(); }
    if (name === "tunes" && tune.data) { drawTuneCurve(); }
    // RTA polls while its own tab is open, and while the EQ tab is open with the overlay
    // on (live measurement under the sliders) - no background chatter otherwise.
    if (name === "rta") { rtaStart(); rtaLoadDevices(); }
    else if (name === "eq" && EQ_OV.on) { rtaStart(); }
    else { rtaStop(); }
    if (name === "targets") { tgtHouseDraw(); tgtSlicesDraw(); }
  }

  document.querySelectorAll("nav button").forEach(function (b) {
    b.addEventListener("click", function () { showTab(b.dataset.tab); });
  });

  // Shared by the Connect button, the auto-connect poll and page init: everything the
  // page must reflect after /api/connect succeeds. Running it on EVERY connect (even
  // when the link was already up) is what keeps the UI honest after a bridge restart -
  // the tracked defaults in /api/info (memory 0, -3.0 dB) are not the device's real state.
  function connectDone(d) {
    setConnected(true);
    if (d.groupDb) {
      renderStatus(d);
      // The sliders no longer carry group levels; keep the device's own values here so
      // cmd 12 writes (master, source, memory) preserve them.
      S.groupDb = d.groupDb;
    }
    // Seed the master control from what the DSP actually reports.
    if (typeof d.mainDb === "number") {
      S.mainDb = d.mainDb;
      setMasterDisplay(d.mainDb);
    }
    if (typeof d.mute === "boolean") {
      S.mute = d.mute;
      document.getElementById("btnMute").classList.toggle("on", d.mute);
    }
    // The probe is on-demand (17 USB round trips), so the connect response only
    // carries a result if a probe already ran. Show it when there is one.
    if (d.pathProbe) {
      var po = document.getElementById("probeOut");
      po.textContent = d.pathProbe;
      po.className = "probeout " + (d.outputsIndependent ? "good" : "bad");
      setCapacity(d);
    }
    log("connected");
    // Channel data - and with it the channel volume sliders - comes from a fresh info
    // read, so an auto-join seeds them exactly like a manual connect does.
    chRefresh();
  }

  document.getElementById("btnConnect").addEventListener("click", function () {
    if (S.connected) {
      api("/api/disconnect", {}).then(function () { setConnected(false); });
      return;
    }
    toast("connecting...");
    api("/api/connect", {}).then(function (d) {
      if (!d.ok) { setConnected(false, d.error); toast(d.error); return; }
      connectDone(d);
    }).catch(function (e) { toast(String(e)); });
  });

  // The bridge re-links the DSP by itself (5 s link watcher); follow along so an already
  // open page joins in on its own once the amp is powered - no Connect tap needed.
  // The full connect routine runs even when the link was already up: it is what pulls
  // the device's real memory/master/groups into a page that may hold restart defaults.
  var autoConnectBusy = false;
  function bridgeAutoConnect() {
    if (S.connected || autoConnectBusy) { return; }
    autoConnectBusy = true;
    api("/api/info", {}).then(function (d) {
      if (!(d && d.open)) { autoConnectBusy = false; return; }
      log("bridge link found - connecting");
      return api("/api/connect", {}).then(function (r) {
        autoConnectBusy = false;
        if (r && r.ok) { connectDone(r); }
      });
    }).catch(function () { autoConnectBusy = false; });
  }

  document.getElementById("btnRefresh").addEventListener("click", refreshStatus);
  document.getElementById("masterVol").addEventListener("input", function () {
    document.getElementById("masterVal").textContent = (this.value / 10).toFixed(1) + " dB";
  });
  document.getElementById("btnMasterApply").addEventListener("click", function () { applyMaster(S.mute); });
  document.getElementById("btnMute").addEventListener("click", function () { applyMaster(!S.mute); });
  document.getElementById("btnReadEq").addEventListener("click", readEq);
  document.getElementById("btnProbe").addEventListener("click", probePaths);

  document.getElementById("btnInApply").addEventListener("click", applyInput);
  document.getElementById("eqApply").addEventListener("click", applyEq);
  document.getElementById("eqBypass").addEventListener("click", function () {
    S.byPass = !S.byPass;
    this.classList.toggle("on", S.byPass);
    hdrEq();
    drawEqCurve();
  });
  document.getElementById("eqFlat").addEventListener("click", flatEq);
  document.getElementById("eqOthers").addEventListener("click", function () {
    EQ_OTHERS = !EQ_OTHERS;
    this.classList.toggle("on", EQ_OTHERS);
    drawEqCurve();
  });
  document.getElementById("eqOv").addEventListener("click", function () {
    EQ_OV.on = !EQ_OV.on;
    try { localStorage.setItem("ds18.eqOv", EQ_OV.on ? "1" : "0"); } catch (e) {}
    this.classList.toggle("on", EQ_OV.on);
    drawEqCurve();
    // the poll follows the overlay: live data only while someone is looking at it
    var sec = document.getElementById("tab-eq");
    if (sec && sec.classList.contains("active")) {
      if (EQ_OV.on) { rtaStart(); } else { rtaStop(); }
    }
  });

  // ---------- system view wiring ----------
  document.getElementById("sysRefresh").addEventListener("click", sysRefresh);
  document.getElementById("sysAuto").addEventListener("click", function () {
    SYS.auto = !SYS.auto;
    this.classList.toggle("on", SYS.auto);
    this.textContent = "Auto: " + (SYS.auto ? "on" : "off");
    if (SYS.auto && !SYS.cells && S.connected) { sysRefresh(); }
    if (SYS.cells) { sysDraw(); }
  });

  // ---------- RTA wiring ----------
  document.getElementById("rtaPeak").addEventListener("click", function () {
    RTA.peak = !RTA.peak;
    this.classList.toggle("on", RTA.peak);
    this.textContent = "Peak hold: " + (RTA.peak ? "on" : "off");
    rtaHoldShow(RTA.hold);   // the legend names the curve being drawn
    if (RTA.data) { rtaDraw(); }
  });
  document.getElementById("rtaHold").addEventListener("click", function () { rtaHoldSet(!RTA.hold); });
  document.getElementById("rtaClear").addEventListener("click", rtaClearPeaks);
  document.getElementById("rtaStimPink").addEventListener("click", function () { stimStart("pink"); });
  document.getElementById("rtaStimSweep").addEventListener("click", function () { stimStart("sweep"); });
  document.getElementById("rtaStimStop").addEventListener("click", function () {
    stimStop();
    if (RTA.actx && RTA.actx.state === "running") { RTA.actx.suspend(); }
  });
  document.getElementById("rtaStimGain").addEventListener("input", function () {
    stimApplyGain();
  });
  stimApplyGain();   // initialise the label from the default slider value
  document.getElementById("stimShapeChips").addEventListener("click", function (ev) {
    var c = (ev.target && ev.target.closest) ? ev.target.closest(".chip") : null;
    if (c && c.getAttribute("data-shape")) { stimShapeSet(c.getAttribute("data-shape")); }
  });
  document.getElementById("rtaDevSel").addEventListener("change", rtaSwitchDevice);
  document.getElementById("rtaDevReload").addEventListener("click", rtaLoadDevices);
  document.getElementById("rtaCalFile").addEventListener("change", function () {
    if (this.files && this.files[0]) { rtaUploadCal(this.files[0]); }
    this.value = "";   // allow re-selecting the same file after a reject
  });
  document.getElementById("rtaMeasSave").addEventListener("click", rtaMeasCapture);
  // Measure cycle is the run box's own Start: same checks, same steps, same log. It is offered
  // here so the whole measurement job can be done from one card instead of two.
  document.getElementById("rtaMeasCycle").addEventListener("click", function () { runStart(); });
  // Re-read the tune state on demand: the run check reads the device, so a deliberate re-read is
  // how a fresh flatten or bypass turns the check green without waiting for anything.
  document.getElementById("baseRead").addEventListener("click", function () { baseRead(true); });
  document.getElementById("rtaMeasSend").addEventListener("click", function () { rtaMeasSend(false); });
  document.getElementById("rtaMeasFrom").addEventListener("click", rtaMeasFromPc);
  document.getElementById("rtaMeasClr").addEventListener("click", function () {
    if (!RTA.meas.length) { return; }
    if (!window.confirm("Delete all " + RTA.meas.length + " captures?")) { return; }
    RTA.meas = []; RTA.overlay = null;
    rtaMeasPersist(); rtaMeasRender(); rtaDraw();
    toast("all captures cleared");
  });
  // speaker setup + stimulus source (Tune tab). Rows are rebuilt on every change, so the
  // listeners sit on the containers rather than on the rows themselves.
  document.getElementById("rosterRows").addEventListener("change", function (ev) {
    var sel = ev.target;
    if (!sel || !sel.classList.contains("rtype")) { return; }
    rosterSetType(parseInt(sel.parentNode.getAttribute("data-ch"), 10), sel.value);
  });
  document.getElementById("rosterRows").addEventListener("input", function (ev) {
    var f = ev.target;
    if (!f || !f.classList.contains("rname")) { return; }
    rosterSetName(parseInt(f.parentNode.getAttribute("data-ch"), 10), f.value);
  });
  document.getElementById("rosterRows").addEventListener("click", function (ev) {
    if (!ev.target || !ev.target.classList.contains("rdel")) { return; }
    rosterRemove(parseInt(ev.target.parentNode.getAttribute("data-ch"), 10));
  });
  document.getElementById("rosterAdd").addEventListener("click", rosterAdd);
  document.getElementById("rosterReset").addEventListener("click", rosterReset);
  document.getElementById("rosterNow").addEventListener("change", function () {
    ROSTER_NOW = parseInt(this.value, 10) || 0;
    // Point the rest of the app at the same speaker, so the EQ tab is already on the channel
    // you are about to measure. chSelect() re-reads that cell from the DSP - a read, and it
    // is skipped unless that output actually exists on this device.
    if (ROSTER_NOW && S.connected && (ROSTER_NOW - 1) < chVisible()) {
      chSelect(ROSTER_NOW - 1);
    }
    rosterRenderNow();
  });
  document.getElementById("stimSrcChips").addEventListener("click", function (ev) {
    var c = (ev.target && ev.target.closest) ? ev.target.closest(".chip") : null;
    if (c && c.getAttribute("data-src")) { stimSrcSet(c.getAttribute("data-src")); }
  });
  rosterLoad();
  rosterRender();
  memPaint();
  stimSrcLoad();
  stimSrcRender();
  stimShapeLoad();
  stimShapeRender();

  // sweep mode: the switch and the level. The switch is the only thing that touches the tune, and
  // it is deliberately one press, not a side effect of opening a tab. It flattens the playing
  // memory and puts it back from a snapshot taken first (see the sweep module above).
  sweepModeLoad();
  document.getElementById("sweepToggle").addEventListener("click", function () {
    if (SWEEP.on) { sweepModeExit(); } else { sweepModeEnter(); }
  });
  document.getElementById("sweepLevel").addEventListener("input", function () {
    SWEEP.pct = Math.max(0, Math.min(100, parseInt(this.value, 10) || 0));
    try { localStorage.setItem(SWEEP_PCT_KEY, String(SWEEP.pct)); } catch (e) { /* ignore */ }
    document.getElementById("sweepLevelVal").textContent =
      SWEEP.pct + "% · " + sweepLevelDb(SWEEP.pct).toFixed(1) + " dB";
    if (SWEEP.on) {                    // a level that is only a label protects nothing
      sweepSwitch(sweepLevelDb(SWEEP.pct))
        .then(function (m) { if (m && m.ok && typeof m.mainDb === "number") { S.mainDb = m.mainDb; } })
        .catch(function (e) { toast("sweep level: " + ((e && e.message) || e)); });
    }
  });

  rtaMeasLoad();
  rtaMeasRender();
  document.getElementById("eqOv").classList.toggle("on", EQ_OV.on);

  // channel strip
  document.getElementById("chSolo").addEventListener("click", chSolo);
  document.getElementById("chAllLive").addEventListener("click", function () { chSetAll(false); });
  document.getElementById("chAllMute").addEventListener("click", function () { chSetAll(true); });
  window.addEventListener("resize", function () {
    drawEqCurve();
    drawHomeCurve();
    if (tune.data) { drawTuneCurve(); }
  });

  // The banner (title + state + tune name + tabs) is now the only sticky element on the page,
  // so there is nothing left to line up underneath it.

  document.getElementById("inRevert").addEventListener("input", function () {
    document.getElementById("inRevertLbl").textContent = "0x" + (parseInt(this.value, 10) >>> 0).toString(16).toUpperCase();
  });

  // ---------- tunes (.idsp import) ----------
  // The official DS18 tune format. Parsing lives entirely in the browser, so a file can be
  // inspected with no amplifier attached; only the final write needs the bridge.
  var tune = { data: null, mem: 0, path: 0, scope: "one" };

  // Direct-child lookup is mandatory here, not a style choice: an <output> carries
  // passBand/Fc before eq/Fc, so a descendant search would return the crossover
  // frequency every time an EQ band's centre frequency was asked for.
  function kidsOf(node, tag) {
    var out = [], cs = node ? node.childNodes : [];
    for (var i = 0; i < cs.length; i++) {
      var c = cs[i];
      if (c.nodeType === 1 && (!tag || c.nodeName === tag)) { out.push(c); }
    }
    return out;
  }
  function kidOf(node, tag) { var k = kidsOf(node, tag); return k.length ? k[0] : null; }
  function numOf(node, tag, dflt) {
    var k = kidOf(node, tag);
    if (!k) { return dflt; }
    var n = parseFloat(k.textContent);
    return isNaN(n) ? dflt : n;
  }
  function idxOf(node, fallback) {
    var n = parseInt(node.getAttribute("index"), 10);
    return isNaN(n) ? fallback : n;
  }

  function parseIdsp(text, name) {
    var doc = new DOMParser().parseFromString(text, "application/xml");
    if (doc.getElementsByTagName("parsererror").length) { throw new Error("not valid XML"); }
    var root = doc.documentElement;
    if (!root || root.nodeName !== "DP12_Param") {
      throw new Error("root is <" + (root ? root.nodeName : "?") + ">, expected <DP12_Param>");
    }
    var runEl = kidOf(root, "RunStatus");
    var d = {
      name: name,
      version: numOf(root, "version", 0),
      run: { source: numOf(runEl, "mainSource", 0),
             memory: numOf(runEl, "mainMemory", 0),
             volume: numOf(runEl, "mainVolume", 0) },
      memories: []
    };
    var memEls = kidsOf(root, "memory");
    for (var mi = 0; mi < memEls.length; mi++) {
      var mEl = memEls[mi];
      var mem = { index: idxOf(mEl, mi), outputs: [] };
      var outEls = kidsOf(mEl, "output");
      for (var oi = 0; oi < outEls.length; oi++) {
        var oEl = outEls[oi];
        var o = { index: idxOf(oEl, oi),
                  mute: numOf(oEl, "mute", 0),
                  gain: numOf(oEl, "gain", 0),
                  revert: numOf(oEl, "revert", 0),
                  delay: numOf(oEl, "delay", 0),
                  linkPair: numOf(oEl, "linkPair", 0),
                  passBand: [], eq: [] };
        var pbEls = kidsOf(oEl, "passBand");
        for (var pi = 0; pi < pbEls.length; pi++) {
          o.passBand.push({
            Fc: numOf(pbEls[pi], "Fc", 0),
            hiLowType: numOf(pbEls[pi], "hiLowType", 0),
            Slope: numOf(pbEls[pi], "Slope", 0),
            filterType: numOf(pbEls[pi], "filterType", 0),
            butt_Type: numOf(pbEls[pi], "butt_Type", 0),
            Enable: numOf(pbEls[pi], "Enable", 0)
          });
        }
        var eqEls = kidsOf(oEl, "eq");
        for (var ei = 0; ei < eqEls.length; ei++) {
          o.eq.push({ Fc: numOf(eqEls[ei], "Fc", 20),
                      Gain: numOf(eqEls[ei], "Gain", 0),
                      Q: numOf(eqEls[ei], "Q", 1) });
        }
        mem.outputs.push(o);
      }
      d.memories.push(mem);
    }
    if (!d.memories.length) { throw new Error("no <memory> blocks in this file"); }
    return d;
  }

  // Bands shaped for /api/eq, clamped to the same limits the DSP codec enforces. The
  // number clamped is returned rather than hidden: several AI tunes in the wild carry
  // +23 dB, which the hardware cannot represent.
  function tuneBands(mi, pi) {
    var mem = tune.data && tune.data.memories[mi];
    var o = mem && mem.outputs[pi];
    var bands = [], clamped = 0;
    if (!o) { return { bands: [], clamped: 0 }; }
    for (var i = 0; i < o.eq.length && i < 31; i++) {
      var e = o.eq[i];
      var fc = clampNum(e.Fc, EQ_FC_MIN, EQ_FC_MAX);
      var g = clampNum(e.Gain, EQ_GAIN_MIN, EQ_GAIN_MAX);
      var q = clampNum(e.Q, EQ_Q_MIN, EQ_Q_MAX);
      // Any of the three silently losing precision counts as data loss, frequency
      // included - a band parked at 99999 Hz is just as gone as one at +23 dB.
      if (fc !== e.Fc || g !== e.Gain || q !== e.Q) { clamped++; }
      bands.push({ fc: fc, gain: g, q: q, enable: true });
    }
    return { bands: bands, clamped: clamped };
  }

  function fmtDb(v) { return (v > 0 ? "+" : "") + v.toFixed(1) + " dB"; }

  function chipRow(hostId, labels, selIdx, cb, colorFn) {
    var host = document.getElementById(hostId);
    host.innerHTML = "";
    labels.forEach(function (lab, i) {
      var el = document.createElement("div");
      el.className = "chip" + (i === selIdx ? " on" : "");
      el.textContent = lab;
      if (colorFn) {                      // channel rows wear the channel's colour
        var col = colorFn(i);
        el.style.borderColor = col;
        if (i === selIdx) { el.style.color = col; }
      }
      el.addEventListener("click", function () { cb(i); });
      host.appendChild(el);
    });
  }

  function tuneRow(k, v) {
    return '<div class="row"><span class="k">' + k + '</span><span class="v">' + v + "</span></div>";
  }

  function tuneSummary(mi) {
    var m = tune.data.memories[mi], s = { delayOn: 0, inv: 0, muted: 0, pbOn: 0 };
    for (var k = 0; k < m.outputs.length; k++) {
      var oo = m.outputs[k];
      if (oo.delay) { s.delayOn++; }
      if (oo.revert) { s.inv++; }
      if (oo.mute) { s.muted++; }
      for (var p = 0; p < oo.passBand.length; p++) { if (oo.passBand[p].Enable) { s.pbOn++; } }
    }
    return s;
  }

  function drawTuneCurve() {
    var tb = tuneBands(tune.mem, tune.path);
    var color = CH_COLORS[tune.path % CH_COLORS.length];
    renderCurve("tuneCurve", tb.bands, false, "tuneRange",
                { color: color, marker: color });
    var sw = document.getElementById("tuneSwatch");
    if (sw) { sw.style.background = color; }
  }

  function renderTune() {
    var d = tune.data;
    document.getElementById("tunePreview").style.display = d ? "" : "none";
    document.getElementById("tuneApplyCard").style.display = d ? "" : "none";
    var rc = document.getElementById("tuneReviewCard");
   if (rc) { rc.style.display = d ? "" : "none"; }
   if (!d) { var rv = document.getElementById("tuneReview"); if (rv) { rv.style.display = "none"; } document.getElementById("tuneInfo").textContent = "No tune loaded."; return; }
    if (tune.mem >= d.memories.length) { tune.mem = 0; }
    var mem = d.memories[tune.mem];
    if (tune.path >= mem.outputs.length) { tune.path = 0; }
    var o = mem.outputs[tune.path];

    document.getElementById("tuneName").textContent = d.name;
    document.getElementById("tuneRun").textContent =
      "memory " + d.run.memory + " / source " + d.run.source + " / volume " + d.run.volume;
    document.getElementById("tuneMemCount").textContent =
      d.memories.length + " (index 0-" + (d.memories.length - 1) + ")";

    chipRow("tuneMemChips", d.memories.map(function (m) { return "M" + m.index; }), tune.mem,
      function (i) { tune.mem = i; renderTune(); });
    chipRow("tunePathChips", mem.outputs.map(function (oo) { return String(oo.index); }), tune.path,
      function (i) { tune.path = i; renderTune(); },
      function (i) { return CH_COLORS[i % CH_COLORS.length]; });

    document.getElementById("tuneWhere").textContent = "memory " + (mem.index + 1) + " / output " + (o.index + 1);
    drawTuneCurve();

    var used = 0, maxG = 0, minG = 0, qMin = 999, qMax = 0, offLadder = 0;
    for (var i = 0; i < o.eq.length; i++) {
      var e = o.eq[i];
      if (e.Gain) { used++; }
      if (e.Gain > maxG) { maxG = e.Gain; }
      if (e.Gain < minG) { minG = e.Gain; }
      if (e.Q < qMin) { qMin = e.Q; }
      if (e.Q > qMax) { qMax = e.Q; }
      if (EQ_DEFAULT_HZ.indexOf(Math.round(e.Fc)) < 0) { offLadder++; }
    }
    var pbOn = [], s = tuneSummary(tune.mem), tb = tuneBands(tune.mem, tune.path);
    for (var p = 0; p < o.passBand.length; p++) {
      if (o.passBand[p].Enable) { pbOn.push(o.passBand[p].Fc + " Hz"); }
    }

    document.getElementById("tuneStats").innerHTML =
      tuneRow("EQ bands in use", used + " / " + o.eq.length) +
      tuneRow("Gain range", fmtDb(minG) + " ... " + fmtDb(maxG)) +
      tuneRow("Q range", qMin + " ... " + qMax) +
      tuneRow("Centres off the ISO ladder", String(offLadder)) +
      tuneRow("Output level", fmtDb(o.gain)) +
      tuneRow("Time alignment", o.delay + " ms") +
      tuneRow("Polarity", o.revert ? "inverted (180 deg)" : "normal") +
      tuneRow("Mute", o.mute ? "muted" : "live") +
      tuneRow("Crossover", pbOn.length ? pbOn.length + " on (" + pbOn.join(", ") + ")" : "off") +
      tuneRow("Memory-wide: delay / inverted / muted", s.delayOn + " / " + s.inv + " / " + s.muted) +
      tuneRow("Memory-wide: crossover filters on", String(s.pbOn));

    var warns = [];
    if (tb.clamped) {
      warns.push(tb.clamped + " band" + (tb.clamped === 1 ? "" : "s") +
                 " here sit outside the DSP range and will be clamped.");
    }
    var carry = 0;
    for (var q2 = 0; q2 < mem.outputs.length; q2++) {
      if (mem.outputs[q2].delay || mem.outputs[q2].revert) { carry++; }
    }
    if (carry) {
      warns.push(carry + " output" + (carry === 1 ? "" : "s") +
                 " in this memory carry delay or polarity - written by cmd 11 when the " +
                 "output box below is ticked.");
    }
    var warn = document.getElementById("tuneWarn");
    warn.style.display = warns.length ? "" : "none";
    warn.textContent = warns.join(" ");
    document.getElementById("tuneTarget").textContent =
      "File memory " + (mem.index + 1) + " -> device memory " + (mem.index + 1) + ", " +
      (tune.scope === "all" ? "all " + mem.outputs.length + " outputs" : "output " + (o.index + 1)) + ".";
    var tom = document.getElementById("tuneOutMem");
    if (tom) { tom.textContent = String(tune.mem); }   // the memory actually sent
  }

  function refreshEqFromDevice() {
    api("/api/info").then(function (d) {
      S.mem = d.mem; S.path = d.path;
      S.pathRead = false;
      buildEq();
      renderEq();
      chLoad(d);                     // a tune apply also carries output levels - show them
      // /api/info's band arrays are the bridge's SINGLE-slot mirror (last write or read,
      // to any path) - not this cell. Only a device read proves what the cell holds.
      readEq(true);
    }).catch(function (e) { log("refresh failed: " + e); });
  }

  function applyTune() {
    if (sweepBlocksTuneWrite("writing a tune")) { return; }
    if (!tune.data) { toast("import a tune first"); return; }
    if (!S.connected) { toast("connect first"); return; }
    var mi = tune.mem, mem = tune.data.memories[mi], paths = [];
    if (tune.scope === "all") {
      for (var i = 0; i < mem.outputs.length; i++) { paths.push(i); }
    } else {
      paths.push(tune.path);
    }
    var btn = document.getElementById("btnTuneApply");
    var prog = document.getElementById("tuneProg");
    var doOut = !!(document.getElementById("tuneOut") &&
                   document.getElementById("tuneOut").checked);
    btn.disabled = true;
    var ok = 0, bad = 0, clamped = 0;

    // The tune's own output block, shaped for cmd 11. Every output in the memory is
    // present: cmd 11 writes all sixteen in one frame, and anything omitted would be
    // written as 0 dB FULL LEVEL rather than left alone. Outputs the file does not
    // mention stay at 0 dB / 0 ms / live, which is the file's own default state.
    function tuneOutputBlock() {
      var m = [], g = [], d = [], r = [], i, k;
      for (i = 0; i < 16; i++) {
        var o = null;
        for (k = 0; k < mem.outputs.length; k++) {
          if (mem.outputs[k].index === i) { o = mem.outputs[k]; break; }
        }
        m.push(o ? !!o.mute : false);
        g.push(o ? (Number(o.gain) || 0) : 0);
        d.push(o ? (Number(o.delay) || 0) : 0);
        r.push(o ? !!o.revert : false);
      }
      return { mem: mi, mute: m, gainDb: g, delayMs: d, revert: r };
    }

    function finish(msg) {
      btn.disabled = false;
      // Whatever file was written becomes the running tune's name (until retyped) - the
      // header then really does say "whatever the current tune file name is".
      if (ok > 0) { tuneNameFromFile(); }
      prog.textContent = msg;
      refreshEqFromDevice();
    }

    function tuneNameFromFile() {
      var base = String((tune.data && tune.data.name) || "")
        .replace(/^.*[\\\/]/, "").replace(/\.idsp$/i, "");
      if (!base || base === TUNE.name) { return; }
      TUNE.name = base;
      tuneNameSave(true);
      renderTuneName();
    }

    // Serial on purpose. The firmware never acknowledges a write, so parallel requests
    // would interleave with the unsolicited telemetry frames and make any failure
    // undiagnosable - one cell at a time, the way the vendor's own bulk routine runs.
    function step(i) {
      if (i >= paths.length) { writeOutput(); return; }
      var p = paths[i];
      var tb = tuneBands(mi, p);
      clamped += tb.clamped;
      prog.textContent = "writing output " + p + " (" + (i + 1) + "/" + paths.length + ") ...";
      api("/api/eq", { mem: mi, path: p, byPass: false, bands: tb.bands })
        .then(function (r) {
          if (r && r.ok) { ok++; } else { bad++; log("tune write mem" + mi + "/out" + p + ": " + (r && r.error)); }
        })
        .catch(function (e) { bad++; log("tune write mem" + mi + "/out" + p + ": " + e); })
        .then(function () { step(i + 1); });
    }

    // Levels come last because they are memory-wide (one cmd 11, not one per output), so
    // they land after the curves are in and are reported as their own line.
    function writeOutput() {
      var base = "wrote " + ok + " of " + paths.length +
                 (bad ? ", " + bad + " failed" : "") +
                 (clamped ? ", " + clamped + " bands clamped" : "");
      if (!doOut) {
        finish(base + "; output levels/delays left untouched");
        return;
      }
      prog.textContent = "writing output levels, delays and polarity ...";
      api("/api/output", tuneOutputBlock()).then(function (r) {
        if (r && r.ok) {
          log("tune output block mem" + mi + ": enable=0x" +
              (Number(r.enableMask) || 0).toString(16) +
              " revert=0x" + (Number(r.revertMask) || 0).toString(16) +
              " count=" + r.outPathCount +
              " (" + r.outPathCountSource + ") " + r.frameBytes + "B " + r.frameHex);
          finish(base + "; output block written - enable 0x" +
                 Number(r.enableMask || 0).toString(16) + ", " + r.outPathCount + " ch" +
                 (r.acked ? "" : ", not acked"));
          toast("tune written");
        } else {
          log("tune output block not written: " + (r && r.error));
          finish(base + "; output block NOT written: " + (r && r.error));
          toast("tune EQ written, levels skipped");
        }
      }).catch(function (e) {
        log("tune output block error: " + e);
        finish(base + "; output block error: " + e);
      });
    }

    // The device must be on this tune's cell before the writes land anywhere audible.
    // A file whose memory differs from the running one jumps there first (cmd 12) - the
    // job the old Memory tabs used to do by hand.
    function startWrites() {
      if (mi === S.mem) { step(0); return; }
      prog.textContent = "switching to memory " + (mi + 1) + " ...";
      applyMemory(mi, true).then(function (sw) {
        if (!sw) {
          btn.disabled = false;
          prog.textContent = "device would not switch to memory " + (mi + 1) +
                             " - tune not written";
          return;
        }
        step(0);
      });
    }
    startWrites();
  }

  document.getElementById("tunePick").addEventListener("click", function () {
    document.getElementById("tuneFile").click();
  });
  document.getElementById("tuneClear").addEventListener("click", function () {
    tune.data = null;
    document.getElementById("tuneFile").value = "";
    renderTune();
  });
  // One loader for both entry points: the file picker and the preloaded dropdown.
  function loadTuneText(text, name) {
    try {
      tune.data = parseIdsp(text, name);
      tune.raw = text;                    // kept so the review can send the exact bytes
      // Start on the memory the vendor had live when they saved, since that is almost
      // always the one worth auditioning first.
      tune.mem = Math.min(Math.max(tune.data.run.memory || 0, 0), tune.data.memories.length - 1);
      tune.path = 0;
      document.getElementById("tuneInfo").textContent =
        name + " - parsed, " + tune.data.memories.length + " memories";
      renderTune();
      toast("tune parsed");
    } catch (err) {
      tune.data = null;
      renderTune();
      document.getElementById("tuneInfo").textContent =
        "Could not read " + name + ": " + err.message;
      toast("parse failed");
      log("idsp parse failed: " + err.message);
    }
  }

  document.getElementById("tuneFile").addEventListener("change", function () {
    var f = this.files && this.files[0];
    if (!f) { return; }
    var rd = new FileReader();
    rd.onload = function () { loadTuneText(String(rd.result), f.name); };
    rd.readAsText(f);
  });
  document.getElementById("tuneReviewBtn").addEventListener("click", function () { reviewTune(); });
  document.getElementById("tuneReviewClear").addEventListener("click", function () {
    var rv = document.getElementById("tuneReview");
    if (rv) { rv.style.display = "none"; }
  });
  document.getElementById("tunePreset").addEventListener("change", function () {
    var url = this.value;
    if (!url) { return; }
    document.getElementById("tuneInfo").textContent = "loading " + url + "...";
    fetch(url, { cache: "no-store" }).then(function (r) {
      if (!r.ok) { throw new Error("HTTP " + r.status); }
      return r.text();
    }).then(function (t) { loadTuneText(t, url); })
      .catch(function (e) {
        document.getElementById("tuneInfo").textContent =
          "Could not load " + url + ": " + e;
        toast("load failed");
      });
  });
  // ---------- review a tune against this car ----------
  // The maths is server-side (tune\review-tune.py, the same modules the reports use), so the
  // app can never disagree with DSB\PREDICTION.md. This only draws what comes back.
  function esc(s) {
   return String(s).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
   }

  function reviewTune() {
   if (!tune.data) { toast("load a tune first"); return; }
   var out = document.getElementById("tuneReview");
   out.style.display = "";
   document.getElementById("reviewVerdict").textContent = "reviewing...";
   var name = String(tune.data.name || "tune.idsp").split(/[\\\/]/).pop();
   var body = { name: name, mem: tune.mem };
   if (tune.raw) { body.xml = tune.raw; }
   api("/api/review", body).then(function (d) {
    if (!d || d.ok === false) {
     document.getElementById("reviewVerdict").innerHTML =
      '<span style="color:var(--bad)">' + esc((d && d.error) ? d.error : "review failed") + "</span>";
     return;
    }
    renderReview(d);
   }).catch(function (e) {
    document.getElementById("reviewVerdict").innerHTML =
     '<span style="color:var(--bad)">review failed: ' + esc(e) + "</span>";
   });
  }

  function renderReview(d) {
   var colour = { good: "var(--ok)", warn: "#f0b429", bad: "var(--bad)", info: "var(--dim)" };
   var html = "";
   (d.verdict || []).forEach(function (r) {
    var c = colour[r.kind] || "var(--fg)";
    var mark = r.kind === "good" ? "OK" : (r.kind === "warn" ? "WARN" : (r.kind === "bad" ? "NO" : ""));
    html += '<div style="margin:4px 0;"><b style="color:' + c + '">' + (mark ? "[" + mark + "] " : "") +
     "</b>" + esc(r.text) + "</div>";
   });
   document.getElementById("reviewVerdict").innerHTML = html;
   document.getElementById("reviewNumbers").innerHTML =
    tuneRow("Predicted residual vs the house target", Number(d.residual_db).toFixed(1) + " dB rms") +
    tuneRow("The car as measured, EQ flat", Number(d.flat_residual_db).toFixed(1) + " dB rms") +
    tuneRow("Level vs " + esc(d.reference) + " below 800 Hz",
     (d.level_delta_low_db >= 0 ? "+" : "") + Number(d.level_delta_low_db).toFixed(1) + " dB") +
    tuneRow("Active EQ bands", String(d.census.bands)) +
    tuneRow("Q median / max", Number(d.census.q_med).toFixed(1) + " / " + Number(d.census.q_max).toFixed(1)) +
    tuneRow("Bands with Q >= 6", String(d.census.notch)) +
    tuneRow("Lineage", d.lineage ? (esc(d.lineage.file) + " memory " + d.lineage.memory) : "not in the library");
   document.getElementById("reviewProvenance").textContent =
    "Predicted from " + d.source_measurement + " - 1/6-octave dBFS at the mic, EQ flat, UMIK-1. " +
    "Where several drivers share a band the sum depends on arrival phase, so this is an energy " +
    "sum; a measurement settles it.";
   drawReviewCurve(d.system || []);
  }

  function drawReviewCurve(arr) {
   var cv = document.getElementById("reviewCurve");
   if (!cv || !arr.length) { return; }
   var dpr = window.devicePixelRatio || 1;
   var w = cv.clientWidth || 320, h = cv.clientHeight || 190;
   cv.width = Math.round(w * dpr); cv.height = Math.round(h * dpr);
   var g = cv.getContext("2d");
   g.setTransform(dpr, 0, 0, dpr, 0, 0);
   g.clearRect(0, 0, w, h);
   var padL = 34, padR = 8, padT = 10, padB = 18;
   var pw = w - padL - padR, ph = h - padT - padB;
   var fMin = 20, fMax = 20000;
   var lo = 1e9, hi = -1e9;
   arr.forEach(function (p) {
    if (p[1] < lo) { lo = p[1]; } if (p[1] > hi) { hi = p[1]; }
    if (p[2] < lo) { lo = p[2]; } if (p[2] > hi) { hi = p[2]; }
   });
   if (hi - lo < 6) { var mid = (hi + lo) / 2; lo = mid - 3; hi = mid + 3; }
   function X(f) { return padL + (Math.log(f / fMin) / Math.log(fMax / fMin)) * pw; }
   function Y(v) { return padT + (1 - (v - lo) / (hi - lo)) * ph; }
   g.strokeStyle = "#3a3f45"; g.lineWidth = 1;
   [100, 1000, 10000].forEach(function (f) {
    g.beginPath(); g.moveTo(X(f), padT); g.lineTo(X(f), padT + ph); g.stroke();
    g.fillStyle = "#8b949e"; g.font = "10px sans-serif";
    g.fillText(f >= 1000 ? (f / 1000) + "k" : String(f), X(f) - 8, padT + ph + 12);
   });
   g.strokeStyle = "#4a5058";
   g.beginPath(); g.moveTo(padL, Y(0)); g.lineTo(padL + pw, Y(0)); g.stroke();
   g.fillStyle = "#8b949e"; g.fillText("0", 10, Y(0) + 3);
   function line(idx, colour) {
    g.strokeStyle = colour; g.lineWidth = 1.7; g.beginPath();
    for (var i = 0; i < arr.length; i++) {
     var x = X(arr[i][0]), y = Y(arr[i][idx]);
     if (i === 0) { g.moveTo(x, y); } else { g.lineTo(x, y); }
    }
    g.stroke();
   }
   line(2, "#999999");    // the car as measured
   line(1, "#0babf2");    // this tune
   var el = document.getElementById("reviewRange");
   if (el) { el.textContent = lo.toFixed(1) + " ... " + hi.toFixed(1) + " dBFS"; }
  }

  // The library lives on the bridge (the .idsp files in its web folder), so the
  // dropdown survives reloads and stays correct after a delete.
  function refreshTuneList(keep) {
    api("/api/tunes", {}).then(function (d) {
      if (!d.ok) { log("tune list: " + d.error); return; }
      var sel = document.getElementById("tunePreset");
      while (sel.options.length > 1) { sel.remove(1); }
      for (var i = 0; i < d.tunes.length; i++) {
        var o = document.createElement("option");
        o.value = d.tunes[i];
        o.textContent = d.tunes[i].replace(/\.idsp$/i, "");
        sel.appendChild(o);
      }
      if (keep) { sel.value = keep; }
    }).catch(function (e) { log("tune list fetch failed: " + e); });
  }

  // Delete ONE tune from the bridge's folder: the selected one, after confirmation.
  document.getElementById("tuneDelete").addEventListener("click", function () {
    var sel = document.getElementById("tunePreset");
    var name = sel.value;
    if (!name) { toast("select a tune first"); return; }
    if (!window.confirm("Delete " + name + " from the bridge? This cannot be undone.")) {
      return;
    }
    api("/api/tunes/delete", { name: name }).then(function (d) {
      if (!d.ok) { toast(d.error); return; }
      for (var i = sel.options.length - 1; i >= 0; i--) {
        if (sel.options[i].value === name) { sel.remove(i); }
      }
      sel.value = "";
      // If the deleted tune was on screen, clear the preview too.
      if (tune.data && tune.data.name === name) {
        tune.data = null;
        renderTune();
        document.getElementById("tuneInfo").textContent = "No tune loaded.";
      }
      toast("deleted " + name);
      log("tune deleted: " + name);
    }).catch(function (e) { toast(String(e)); });
  });

  document.getElementById("tuneScopeChips").addEventListener("click", function (e) {
    var el = e.target;
    if (!el.classList || !el.classList.contains("chip")) { return; }
    tune.scope = el.getAttribute("data-scope");
    var cs = this.querySelectorAll(".chip");
    for (var i = 0; i < cs.length; i++) { cs[i].classList.toggle("on", cs[i] === el); }
    renderTune();
  });
  document.getElementById("btnTuneApply").addEventListener("click", applyTune);

  // ---------- channels (cmd 11) ----------
  // Channel state lives on the BRIDGE, not in this page. The DSP cannot be asked what it
  // is doing here at all - no ReadParam sub-command covers output mute/level/delay/polarity,
  // which is why the official app keeps its own copy and compares against it - so
  // /api/info's mirror is the only picture of what the next cmd 11 frame will contain.
  //
  // cmd 11 writes ALL sixteen outputs plus the four group levels in one shot, so every edit
  // here resends the COMPLETE block rather than a delta. Nothing in this file invents a
  // value: /api/output refuses a partial block, and a write reports what was transmitted,
  // never what the device is now doing.
  var CH = { mute: [], gain: [], delay: [], revert: [], known: [],
             count: 0, src: "unread" };
  var chBusy = false;

  function chLoad(d) {
    CH.mute = d.outputMute || [];
    CH.gain = d.outputGainDb || [];
    CH.delay = d.outputDelayMs || [];
    CH.revert = d.outputRevert || [];
    CH.known = d.outputKnown || [];
    CH.count = d.outPathCount || 0;
    CH.src = d.outPathCountSource || "unread";
    renderChannels();
    // The channel volume sliders mirror this data; rebuild when the output count
    // arrives or changes so the card shows exactly the outputs the product has.
    var vs = document.getElementById("volSliders");
    if (vs && vs.childElementCount !== chVisible()) { buildVolume(); }
    else { seedVolume(); }
  }

  function chRefresh() {
    return api("/api/info").then(chLoad).catch(function (e) {
      log("channel refresh: " + e);
    });
  }

  // How many rows to show: the product's own count when sub 4 answered, otherwise the full
  // sixteen the struct carries - labelled as inferred rather than quietly trimmed to a guess.
  function chVisible() {
    return (CH.count >= 1 && CH.count <= 16) ? CH.count : 16;
  }

  function renderChannels() {
    var host = document.getElementById("chanRail");
    if (!host) { return; }
    var cm = document.getElementById("chMem");
    if (cm) { cm.textContent = "memory " + (S.mem + 1); }

    var n = chVisible();
    host.innerHTML = "";
    for (var i = 0; i < n; i++) {
      (function (idx) {
        var live = !CH.mute[idx];
        var btn = document.createElement("div");
        btn.className = "chbtn" + (idx === S.path ? " sel" : "") + (live ? " live" : " off");
        btn.style.setProperty("--ch", CH_COLORS[idx % CH_COLORS.length]);
        btn.title = "output " + (idx + 1) + " — " +
          (Number(CH.gain[idx]) || 0).toFixed(1) + " dB, " +
          (Number(CH.delay[idx]) || 0).toFixed(2) + " ms" +
          (CH.revert[idx] ? ", inverted" : "");

        var box = document.createElement("span");
        box.className = "chbox";
        box.title = live ? "Mute output " + (idx + 1) : "Un-mute output " + (idx + 1);

        var lab = document.createElement("span");
        lab.textContent = "CH " + (idx + 1);   // 1-based, the only numbering the owner sees

        var dot = document.createElement("span");
        dot.className = "chdot";         // this output's colour, lit when selected

        btn.appendChild(dot);
        btn.appendChild(box);
        btn.appendChild(lab);
        btn.addEventListener("click", function (ev) {
          if (ev.target === box) { chToggle(idx); return; }   // box = mute on/off
          chSelect(idx);                                       // label = this is my channel
        });
        host.appendChild(btn);
      })(i);
    }

    var note = document.getElementById("chNote");
    if (note) {
      if (!CH.known[S.mem]) {
        note.style.display = "";
        note.textContent = "Output block for memory " + (S.mem + 1) +
          " has not been established. The first write will set every channel to what is " +
          "shown (0 dB, 0 ms, live) - import an .idsp first to load a tune's real levels, " +
          "delays and polarity instead.";
      } else if (CH.src === "defaulted" || CH.src === "unread") {
        note.style.display = "";
        note.textContent = "Channel count " + CH.count + " is not read from the device " +
          "(sub 4 did not answer); it is inferred from the path probe. Pass outPathCount " +
          "per write to override.";
      } else {
        note.style.display = "none";
      }
    }

    // Mute/selection changes re-weight the system view; cheap redraw from cache, and a
    // no-op (SYS.cells null) before the first read.
    if (SYS.cells) { sysDraw(); }

    // The speaker setup shows which channel maps to which DSP output, and the output count
    // only lands here - repaint it once the device has answered (no-op until the plan loads).
    if (ROSTER && ROSTER.length) { rosterRender(); }
  }

  // The complete block cmd 11 demands: sixteen entries of every field, no gaps. An omitted
  // entry becomes a zero, and a zero gain means 0 dB FULL LEVEL, not "leave it alone".
  function chBlock() {
    var m = [], g = [], d = [], r = [], i;
    for (i = 0; i < 16; i++) {
      m.push(!!CH.mute[i]);
      g.push(Number(CH.gain[i]) || 0);
      d.push(Number(CH.delay[i]) || 0);
      r.push(!!CH.revert[i]);
    }
    return { mem: S.mem, mute: m, gainDb: g, delayMs: d, revert: r };
  }

  function chSend(what, extra) {
    if (chBusy) { toast("a channel write is already running"); return Promise.resolve(null); }
    if (!S.connected) { toast("connect first"); return Promise.resolve(null); }
    chBusy = true;
    var last = document.getElementById("chLast");
    if (last) { last.textContent = what + " ..."; }
    var body = chBlock(), k;
    if (extra) { for (k in extra) { if (extra.hasOwnProperty(k)) { body[k] = extra[k]; } } }

    return api("/api/output", body).then(function (res) {
      chBusy = false;
      if (res && res.ok) {
        CH.known[S.mem] = true;
        var cnt = res.outPathCount + " ch (" + res.outPathCountSource + ")";
        if (last) {
          last.textContent = what + " — enable 0x" +
            Number(res.enableMask || 0).toString(16) + ", " + cnt +
            (res.acked ? "" : ", not acked");
        }
        // enableMask/revertMask arrive as JSON numbers: print them as HEX, or "0x" + 255
        // reads as 0x255 while the wire mask is 0xff.
        log("cmd 11 " + what + ": enable=0x" + (Number(res.enableMask) || 0).toString(16) +
            " revert=0x" + (Number(res.revertMask) || 0).toString(16) + " count=" + cnt +
            " " + res.frameBytes + "B " + res.frameHex);
        toast(what);
        renderChannels();
        if (SYS.cells) { sysDraw(); }     // level/mute weights changed -> re-sum
        return res;
      }
      var msg = (res && res.error) ? res.error : "write failed";
      var sent = !!(res && res.transmitted);
      if (last) {
        last.textContent = what + " — " + (sent ? "refused" : "not sent");
      }
      // An unconnected call still returns the encoded frame, so the bytes can be read back
      // here rather than guessed at — it just says plainly that nothing left the machine.
      if (res && res.frameHex) {
        log("cmd 11 preview (NOT transmitted): enable=0x" +
            (Number(res.enableMask) || 0).toString(16) +
            " revert=0x" + (Number(res.revertMask) || 0).toString(16) +
            " count=" + res.outPathCount +
            " (" + res.outPathCountSource + ") " + res.frameBytes + "B " + res.frameHex);
      }
      log("cmd 11: " + msg);
      toast(msg);
      return null;
    }).catch(function (e) {
      chBusy = false;
      if (last) { last.textContent = what + " — error"; }
      log("cmd 11 error: " + e);
      toast(String(e));
      return null;
    });
  }

  // Tap the row to say "this is the channel I'm working on" - it becomes S.path, so Apply
  // EQ, Flat and the curve all follow it, and the device's own EQ is read back for it.
  function chSelect(idx) {
    if (idx === S.path) { return; }
    S.path = idx;
    S.pathRead = false;              // the grid still shows the OLD cell until the read lands
    renderChannels();
    readEq(true);
  }

  function chToggle(idx) {
    if (!S.connected) { toast("connect first"); return; }
    CH.mute[idx] = !CH.mute[idx];
    renderChannels();
    chSend("output " + (idx + 1) + (CH.mute[idx] ? " muted" : " live"));
  }

  function chSetAll(muted) {
    if (!S.connected) { toast("connect first"); return; }
    var n = chVisible(), i;
    for (i = 0; i < n; i++) { CH.mute[i] = muted; }
    renderChannels();
    chSend(muted ? "all channels muted" : "all channels live");
  }

  // Measurement step: one channel up, everything else down, in a single cmd 11.
  function chSolo() {
    if (!S.connected) { toast("connect first"); return; }
    var n = chVisible(), i;
    for (i = 0; i < n; i++) { CH.mute[i] = (i !== S.path); }
    renderChannels();
    chSend("solo output " + S.path);
  }

  // boot
  buildVolume();
  buildInput();
  renderSourceChecks();
  tuneNameLoad();                      // the running tune's name, before first status paint
  api("/api/info").then(function (d) {
    S.mem = d.mem; S.path = d.path; S.source = d.source;
    S.sourceName = d.sourceName || S.sourceName;   // hero text before any /api/status
    renderTuneName();                              // re-paint with the real source name
    S.mainDb = d.mainDb; S.mute = !!d.mute;
    setMasterDisplay(S.mainDb);
    document.getElementById("btnMute").classList.toggle("on", S.mute);
    tuneNowFill(d);                    // Tune tab: tune name, source, level, memory
    renderSourceChecks();
    S.bandHz = d.bandHz || [];
    S.bandGainDb = d.bandGainDb || [];
    S.bandQ = d.bandQ || [];
    S.bandEnabled = d.bandEnabled || [];
    S.byPass = !!d.byPass;
    hdrEq();
    buildEq();
    renderEq();
    setConnected(!!d.open, d.error);
    chLoad(d);                       // channel strip mirrors the same info payload
    // Read the open cell's EQ immediately: without this the grid shows boot defaults
    // until someone taps a channel, and a write would erase the cell's real EQ.
    if (d.open) { readEq(true); }
    // A linked bridge carries restart defaults in /api/info (memory 0, -3.0 dB) until
    // something reads the device - so pull the REAL state now, exactly like a Connect
    // tap would. This is also what brings the status rows to the DSP's real preset.
    if (d.open) {
      api("/api/connect", {}).then(function (r) { if (r && r.ok) { connectDone(r); } })
        .catch(function () { });
    }
  }).catch(function (e) {
    buildEq();
    renderChannels();                // still render, so the strip shows its unknown-state note
    toast("bridge unreachable");
    log("info failed: " + e);
  });

  // Poll so an already-open page notices the bridge's link watcher taking the DSP in.
  setInterval(bridgeAutoConnect, 10000);

  // Tune library comes from the bridge's folder, not this file, so deletes stick.
  refreshTuneList();

  // Saved target curve (house curve + per-channel windows) from the bridge.
  tgtLoad();
})();
