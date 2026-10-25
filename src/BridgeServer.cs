using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using LibHoneyWg;

namespace Ds18Bridge
{
    /// <summary>
    /// LAN HTTP bridge: serves the phone web UI and a small JSON API backed by DspClient.
    /// No third-party JSON library - JavaScriptSerializer ships with System.Web.Extensions.
    /// </summary>
    public sealed class BridgeServer : IDisposable
    {
        private readonly DspClient _client;
        private readonly string _webRoot;
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        private readonly object _apiLock = new object();

        private HttpListener _listener;
        private Thread _thread;
        private volatile bool _running;

        // Link watcher: re-opens the DSP whenever it is closed or was marked broken by a
        // failed write, so the bridge recovers from a DSP power-cycle with no human help.
        private Thread _watch;
        private readonly ManualResetEvent _watchWake = new ManualResetEvent(false);

        public string LastError = "";

        // Local mirror of the last values pushed to or read from the device, so the UI can
        // render something useful before the first successful read.
        public byte Mem;
        public byte PathIndex;
        public byte Source;
        public bool ByPass;
        public double[] GroupDb = new double[4];
        public double MainDb = -3.0;
        public bool Mute;
        public uint GroupMask;
        /// <summary>
        /// Output mask read back from the DSP (StructDspCfg.mGroupVolumePath). Group-volume
        /// commands are applied to the outputs named by this mask; writing 0 targets nothing,
        /// which is inaudible.
        /// </summary>
        public uint DeviceGroupMask;

        /// <summary>
        /// StructProductDetail.mProductOutPathCount, read from sub 4 at connect. cmd 11 masks
        /// its enable bits with `i &lt; outPathCount`, so this is the difference between a mute
        /// mask that matches the product and one that does not. 0 = not read.
        /// </summary>
        public byte ProductOutPathCount;

        /// <summary>Where ProductOutPathCount came from: "device", "requested" or "defaulted".</summary>
        public string OutPathCountSource = "unread";

        /// <summary>
        /// Fallback when sub 4 does not yield a count. 8, not 16: the connect probe showed
        /// paths 0..7 mutually distinct and 8..15 byte-identical (unpopulated), and the .idsp
        /// files carry gain and delay on outputs 0..7 only. That is evidence for this unit,
        /// not a read - OutPathCountSource says which was used.
        /// </summary>
        public const byte OutPathCountDefault = 8;
        public bool[] BandEnabled = new bool[31];
        public double[] BandGainDb = new double[31];
        public double[] BandQ = new double[31];
        public ushort[] BandHz = new ushort[31];

        // ---- Output configuration (cmd 11), indexed as mem * 16 + output ----
        //
        // The DSP cannot be asked what it holds here: no ReadParam sub-command covers output
        // mute/gain/delay/polarity, which is why the official app keeps its own copy and calls
        // CompareData against it. So this mirror is the only source of truth for what a cmd 11
        // frame will contain, and cmd 11 writes all sixteen outputs plus the group block in one
        // shot. A wrong value here is applied, not rejected.
        //
        // OutputKnown[m] is therefore the safety interlock: false means the mirror holds
        // plausible defaults that may disagree with the device, and the UI must make the user
        // choose a starting point (typically from an imported .idsp, which carries the full
        // block) rather than letting a first toggle clobber the tune's output levels.
        public bool[] OutputMute = new bool[4 * 16];
        public double[] OutputGainDb = new double[4 * 16];
        public double[] OutputDelayMs = new double[4 * 16];
        public bool[] OutputRevert = new bool[4 * 16];
        public bool[] OutputKnown = new bool[4];

        // Result of the connect-time path probe.
        //
        // Two separate questions, because they matter for different reasons:
        //   (a) Are outputs 1..15 independent of output 0?  If they are NOT, every output
        //       shares one 31-band curve and per-driver correction is impossible.
        //   (b) Is path 16 a distinct master EQ chain?  If it is, each output gets a
        //       second 31-band stage on top of its own.
        //
        // (a) is the load-bearing one. (b) is a bonus.
        public string PathProbeSummary = "";
        public bool OutputsIndependent;
        public bool MasterEqDistinct;
        public uint[] PathHashes = new uint[17];
        public int[] PathSizes = new int[17];

        /// <summary>Read attempts per path. 3: enough to ride out EP1 contention.</summary>
        private const int ProbeRetries = 3;

        /// <summary>Total USB round trips the last probe consumed (across retries).</summary>
        public int PathProbeAttempts;

        public BridgeServer(DspClient client, string webRoot)
        {
            _client = client;
            _webRoot = webRoot;
            _json.MaxJsonLength = int.MaxValue;

            for (int i = 0; i < 31; i++)
            {
                BandEnabled[i] = true;
                BandGainDb[i] = 0.0;
                BandQ[i] = 1.0;
                // Until a real read lands, assume the standard 1/3-octave ladder.
                BandHz[i] = (ushort)DspMath.DefaultBandHz[i];
            }

            // The cmd 11 mirror is the one picture the device cannot give back, so it comes off
            // disk rather than starting at "0 dB everywhere" (see SaveOutputMirror).
            LoadOutputMirror();
        }

        /// <summary>True when bound to the wildcard prefix, i.e. reachable from the phone.</summary>
        public bool PublicBinding { get; private set; }

        public bool Start(int port, out string error)
        {
            error = "";
            PublicBinding = false;

            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add("http://*:" + port + "/");
                _listener.Start();
            }
            catch (Exception ex)
            {
                string wildcardError = DescribeListenerFailure(ex, port);
                try
                {
                    _listener = new HttpListener();
                    _listener.Prefixes.Add("http://localhost:" + port + "/");
                    _listener.Start();
                    error = wildcardError + "\nFell back to http://localhost:" + port
                          + "/ - this PC only. Apply the urlacl above (or run elevated) to "
                          + "reach it from the phone.";
                }
                catch (Exception ex2)
                {
                    error = wildcardError + "\nLocalhost fallback also failed: " + ex2.Message;
                    Release();
                    return false;
                }
            }

            // Only the wildcard prefix is reachable from another machine. If we fell back to
            // localhost this stays false and the banner must say so.
            PublicBinding = !_listener.Prefixes.Contains("http://localhost:" + port + "/");

            _running = true;
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = "ds18-http";
            _thread.Start();

            _watch = new Thread(LinkWatcher);
            _watch.IsBackground = true;
            _watch.Name = "ds18-watch";
            _watch.Start();

            return true;
        }

        public void Stop()
        {
            Release();
        }

        public void Dispose()
        {
            Release();
        }

        /// <summary>
        /// HttpListener requires a URL ACL (or elevation) to bind a wildcard prefix. Turn the
        /// raw exception into something the operator can act on.
        /// </summary>
        private static string DescribeListenerFailure(Exception ex, int port)
        {
            HttpListenerException hle = ex as HttpListenerException;
            if (hle != null && (hle.NativeErrorCode == 5 || hle.ErrorCode == 5))
            {
                return "Access denied binding http://*:" + port + "/. Run this once from an "
                     + "elevated prompt to allow non-admin listening:\n"
                     + "  netsh http add urlacl url=http://*:" + port + "/ user=Everyone\n"
                     + "or start the bridge with --admin.";
            }
            return "listener start failed: " + ex.Message;
        }

        private void Release()
        {
            _running = false;
            try { if (_listener != null) _listener.Stop(); }
            catch { }
            try { if (_listener != null) _listener.Close(); }
            catch { }
            _listener = null;

            Thread t = _thread;
            _thread = null;
            if (t != null && t.IsAlive && t != Thread.CurrentThread)
            {
                try { t.Join(600); }
                catch { }
            }

            _watchWake.Set();                 // end the watcher's 5 s sleep immediately
            Thread w = _watch;
            _watch = null;
            if (w != null && w.IsAlive && w != Thread.CurrentThread)
            {
                try { w.Join(1500); }
                catch { }
            }
        }

        /// <summary>
        /// Keeps the USB link alive without human help. The DSP drops off the bus whenever
        /// the amp loses power, and a stale handle used to leave every command dead until
        /// someone noticed and restarted the bridge. Every 5 seconds (or sooner when
        /// woken) the watcher re-opens the device when it is closed or was marked broken
        /// by a failed write - turning the key is then all it takes, and the page's
        /// auto-connect finds the link already up.
        /// </summary>
        private void LinkWatcher()
        {
            string lastError = null;
            while (_running)
            {
                _watchWake.WaitOne(5000);
                if (!_running) { return; }
                try
                {
                    lock (_apiLock)
                    {
                        if (_client.IsOpen)
                        {
                            continue;
                        }
                        if (_client.Open())
                        {
                            // Same state pull Connect does, so commands work immediately.
                            string maskError, countError;
                            RefreshDeviceGroupMask(out maskError);
                            RefreshProductDetail(out countError);
                            Console.WriteLine("link watcher: DSP connected");
                            lastError = null;
                        }
                        else if (_client.LastError != lastError)
                        {
                            // Log the state change only - the DSP being off is normal
                            // between drives and must not spam.
                            lastError = _client.LastError;
                            Console.WriteLine("link watcher: waiting - " + lastError);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("link watcher: " + ex.Message);
                }
            }
        }

        private void Loop()
        {
            while (_running)
            {
                HttpListenerContext ctx = null;
                try
                {
                    ctx = _listener.GetContext();
                }
                catch
                {
                    return; // listener closed
                }

                Thread worker = new Thread(delegate() { Handle(ctx); });
                worker.IsBackground = true;
                worker.Start();
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            try
            {
                string path = ctx.Request.Url.AbsolutePath.ToLowerInvariant();

                if (path.StartsWith("/api/"))
                {
                    HandleApi(ctx, path);
                    return;
                }
                ServeStatic(ctx, path);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                try { WriteJson(ctx.Response, 500, Err(ex.Message)); }
                catch { }
            }
        }

        // ---------------- API ----------------

        private void HandleApi(HttpListenerContext ctx, string path)
        {
            Dictionary<string, object> body = ReadBody(ctx);

            switch (path)
            {
                case "/api/info":
                    WriteJson(ctx.Response, 200, Info());
                    return;

                case "/api/tunes":
                    WriteJson(ctx.Response, 200, ListTunes());
                    return;

                case "/api/tunes/delete":
                    DoTuneDelete(ctx, body);
                    return;

                case "/api/connect":
                    DoConnect(ctx);
                    return;

                case "/api/disconnect":
                    lock (_apiLock) { _client.Close(); }
                    WriteJson(ctx.Response, 200, Ok());
                    return;

                case "/api/status":
                    DoStatus(ctx);
                    return;

                case "/api/volume":
                    DoVolume(ctx, body);
                    return;

                case "/api/master":
                    DoMaster(ctx, body);
                    return;

                case "/api/input":
                    DoInput(ctx, body);
                    return;

                case "/api/output":
                    DoOutput(ctx, body);
                    return;

                case "/api/eq":
                    DoEq(ctx, body);
                    return;

                case "/api/eqread":
                    DoEqRead(ctx, body);
                    return;

                case "/api/eqprobe":
                    DoEqProbe(ctx);
                    return;

                case "/api/eqcoef":
                    DoEqCoef(ctx, body);
                    return;

                case "/api/passband":
                    DoPassBand(ctx, body);
                    return;

                case "/api/dspcfg":
                    DoDspCfg(ctx);
                    return;

                case "/api/rta-meas":
                    DoRtaMeas(ctx, body);
                    return;

                case "/api/review":
                    DoReview(ctx, body);
                    return;

                case "/api/run-save":
                    DoRunSave(ctx, body);
                    return;

                case "/api/targets":
                    DoTargets(ctx, body);
                    return;

                case "/api/tunename":
                    DoTuneName(ctx, body);
                    return;

                default:
                    WriteJson(ctx.Response, 404, Err("unknown endpoint " + path));
                    return;
            }
        }

        /// <summary>
        /// The imported-tune library: every .idsp file sitting in the served web folder.
        /// The page builds its dropdown from this, so the list survives a reload and stays
        /// correct after a delete without hand-editing the page.
        /// </summary>
        private Dictionary<string, object> ListTunes()
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            List<object> names = new List<object>();
            try
            {
                string[] files = Directory.GetFiles(_webRoot, "*.idsp");
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < files.Length; i++)
                {
                    names.Add(Path.GetFileName(files[i]));
                }
                d["ok"] = true;
            }
            catch (Exception ex)
            {
                d["ok"] = false;
                d["error"] = ex.Message;
            }
            d["tunes"] = names;
            return d;
        }

        /// <summary>
        /// Delete ONE imported tune. The name is validated hard - no path separators, no
        /// leading dot, must end in .idsp - and re-checked with a full-path containment
        /// test, so this endpoint can never reach outside the tunes folder. The web app
        /// and every other tune are untouched.
        /// </summary>
        private void DoTuneDelete(HttpListenerContext ctx, Dictionary<string, object> body)
        {
            object nameObj = null;
            string name = (body != null && body.TryGetValue("name", out nameObj)
                           && nameObj != null) ? nameObj.ToString() : "";
            if (name.Length == 0 || name.Length > 128
                || name.IndexOfAny(new char[] { '/', '\\', ':' }) >= 0
                || name.StartsWith(".")
                || !name.EndsWith(".idsp", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(ctx.Response, 200, Err(
                    "invalid tune name - expected a plain .idsp filename from the list"));
                return;
            }

            string root;
            string full;
            try
            {
                root = Path.GetFullPath(_webRoot);
                full = Path.GetFullPath(Path.Combine(_webRoot, name));
            }
            catch (Exception ex)
            {
                WriteJson(ctx.Response, 200, Err("bad path: " + ex.Message));
                return;
            }
            if (!full.StartsWith(root + Path.DirectorySeparatorChar,
                                 StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(ctx.Response, 200,
                          Err("refusing to delete outside the tunes folder"));
                return;
            }

            try
            {
                if (!File.Exists(full))
                {
                    WriteJson(ctx.Response, 200, Err(name + " is not there anymore"));
                    return;
                }
                File.Delete(full);
                Dictionary<string, object> d = Ok();
                d["deleted"] = name;
                WriteJson(ctx.Response, 200, d);
                Console.WriteLine("tune deleted: " + name);
            }
            catch (Exception ex)
            {
                WriteJson(ctx.Response, 200, Err(ex.Message));
            }
        }

        private Dictionary<string, object> Info()
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["open"] = _client.IsOpen;
            d["autoReconnect"] = true;   // the link watcher runs whenever the bridge is up
            d["error"] = LastError.Length > 0 ? LastError : _client.LastError;
            d["vid"] = "0x" + DspClient.VendorId.ToString("X4");
            d["pid"] = "0x" + DspClient.ProductId.ToString("X4");
            d["mem"] = (int)Mem;
            d["path"] = (int)PathIndex;
            d["source"] = (int)Source;
            d["sourceName"] = SourceMap.Name((int)Source);
            d["byPass"] = ByPass;
            d["groupDb"] = GroupDb;
            d["mainDb"] = MainDb;
            d["mute"] = Mute;
            d["groupMask"] = GroupMask;
            d["deviceMask"] = DeviceGroupMask;
            d["bandGainDb"] = BandGainDb;
            d["bandEnabled"] = BandEnabled;
            d["bandQ"] = BandQ;
            d["bandHz"] = BandHz;
            d["defaultBandHz"] = DspMath.DefaultBandHz;
            d["pathProbe"] = PathProbeSummary;
            d["outputsIndependent"] = OutputsIndependent;
            d["masterEqDistinct"] = MasterEqDistinct;

            // Output block (cmd 11) for the selected memory. outputKnown is the flag that
            // matters: while false the mirror holds defaults that may disagree with the
            // device, so the UI must establish a starting point before offering toggles.
            d["outPathCount"] = (int)ProductOutPathCount;
            d["outPathCountSource"] = OutPathCountSource;
            d["outputKnown"] = OutputKnown;
            int mi = (int)Mem;
            if (mi >= 0 && mi <= 3)
            {
                bool[] oMute = new bool[16];
                double[] oGain = new double[16];
                double[] oDelay = new double[16];
                bool[] oRevert = new bool[16];
                for (int i = 0; i < 16; i++)
                {
                    oMute[i] = OutputMute[mi * 16 + i];
                    oGain[i] = OutputGainDb[mi * 16 + i];
                    oDelay[i] = OutputDelayMs[mi * 16 + i];
                    oRevert[i] = OutputRevert[mi * 16 + i];
                }
                d["outputMute"] = oMute;
                d["outputGainDb"] = oGain;
                d["outputDelayMs"] = oDelay;
                d["outputRevert"] = oRevert;
            }
            return d;
        }

        private void DoConnect(HttpListenerContext ctx)
        {
            lock (_apiLock)
            {
                // Reuse a healthy link; prove it with a probe read. A DSP that was
                // power-cycled while idle still looks open, the probe fails (and the
                // failed write marks the client broken), and the open below becomes a
                // true reopen. No needless USB churn on every Connect tap.
                bool haveMask = false;
                string maskError = "";
                if (_client.IsOpen)
                {
                    haveMask = RefreshDeviceGroupMask(out maskError);
                    if (!haveMask)
                    {
                        _client.Close();
                    }
                }

                if (!_client.Open())
                {
                    LastError = _client.LastError;
                    WriteJson(ctx.Response, 200, Err(_client.LastError));
                    return;
                }

                // The group-volume mask must come from the device, not from a default of 0 -
                // a zero mask makes every volume write inaudible.
                if (!haveMask)
                {
                    RefreshDeviceGroupMask(out maskError);
                }

                // Likewise the output count: cmd 11 turns it into a mute mask, so prefer the
                // device's own value over the inferred default.
                string countError;
                RefreshProductDetail(out countError);
            }
            // Pull real state once connected so the sliders match the hardware.
            DoStatus(ctx);
        }

        /// <summary>
        /// ReadParam sub 1 -> StructDspCfg. Only mGroupVolumePath is consumed: it names the
        /// outputs a group-volume command actually affects.
        /// </summary>
        private bool RefreshDeviceGroupMask(out string error)
        {
            error = "";
            byte[] frame = Builders.ReadParam(ReadSubCmd.BaseDsp, Mem, Source, PathIndex);
            RxFrame reply;
            if (!_client.RequestCmd(frame, ReadSubCmd.BaseDsp, 2500, out reply))
            {
                error = _client.LastError;
                return false;
            }

            uint mask;
            if (!ReplyParse.TryGetGroupVolumePath(reply.Body, out mask))
            {
                error = "sub 1 reply too short (" + reply.Body.Length
                      + " bytes) - cannot locate mGroupVolumePath";
                return false;
            }

            DeviceGroupMask = mask;
            GroupMask = mask;
            return true;
        }

        /// <summary>
        /// ReadParam sub 4 - StructProductDetail, for mProductOutPathCount only. Runs at
        /// connect beside RefreshDeviceGroupMask. Failure is recorded rather than fatal: the
        /// count is consumed only by cmd 11, which falls back to OutPathCountDefault and
        /// reports which value it used.
        /// </summary>
        private bool RefreshProductDetail(out string error)
        {
            error = "";
            byte[] frame = Builders.ReadParam(ReadSubCmd.ProductDetail, Mem, Source, PathIndex);
            RxFrame reply;
            if (!_client.RequestCmd(frame, ReadSubCmd.ProductDetail, 2500, out reply))
            {
                error = _client.LastError;
                OutPathCountSource = "defaulted";
                ProductOutPathCount = OutPathCountDefault;
                return false;
            }

            byte n;
            if (!ReplyParse.TryGetOutPathCount(reply.Body, out n))
            {
                int got = (reply.Body == null) ? 0 : reply.Body.Length;
                error = "sub 4 reply unreadable (" + got + " bytes, or the boot variant)";
                OutPathCountSource = "defaulted";
                ProductOutPathCount = OutPathCountDefault;
                return false;
            }

            ProductOutPathCount = n;
            OutPathCountSource = "device";
            return true;
        }

        /// <summary>
        /// ReadParam sub 3 across all 17 paths and fingerprint each one, so we can tell
        /// "the firmware only populates path 0" apart from "our path stride is wrong".
        ///
        /// A path that returns identical bytes to path 0 is either unpopulated (the device
        /// re-sends whatever is at the single real slot) or our offset is not advancing.
        /// Distinct bytes at 1..15 means real per-output EQ; distinct bytes at 16 means a
        /// separate master EQ chain.
        /// </summary>
        private void ProbeEqPaths()
        {
            const int paths = 17;
            PathProbeAttempts = 0;
            uint[] hashes = new uint[paths];
            int[] sizes = new int[paths];
            bool[] ok = new bool[paths];
            bool[] distinctFromZero = new bool[paths];

            // Path 0 is the reference. If it does not reply there is nothing to compare
            // against, and continuing would burn ~16 more timeouts for no information.
            if (!ReadEqPath(0, hashes, sizes, ok))
            {
                PathHashes = hashes;
                PathSizes = sizes;
                OutputsIndependent = false;
                MasterEqDistinct = false;
                PathProbeSummary = "EQ path probe: path 0 did not reply after "
                    + ProbeRetries + " attempts ("
                    + PathProbeAttempts + " round trips) - nothing to compare. If other USB "
                    + "software is using the DSP, close it and retry.";
                return;
            }

            for (int p = 1; p < paths; p++)
            {
                ReadEqPath(p, hashes, sizes, ok);
                distinctFromZero[p] = ok[p] && (hashes[p] != hashes[0]);
            }

            PathHashes = hashes;
            PathSizes = sizes;

            // Outputs 1..15 must differ from output 0 to be genuinely independent.
            int distinctOutputs = 0;
            for (int p = 1; p <= 15; p++)
            {
                if (ok[p] && distinctFromZero[p]) { distinctOutputs++; }
            }
            OutputsIndependent = (distinctOutputs > 0);

            MasterEqDistinct = ok[16] && distinctFromZero[16];

            StringBuilder sb = new StringBuilder();
            sb.Append("EQ path probe (mem ").Append(Mem).Append("): ");
            int answered = 0;
            for (int p = 0; p < paths; p++) { if (ok[p]) { answered++; } }
            sb.Append(answered).Append("/").Append(paths).Append(" replied in ")
              .Append(PathProbeAttempts).Append(" round trips. ");

            if (!ok[0])
            {
                sb.Append("Path 0 did not reply, so nothing can be concluded.");
            }
            else
            {
                sb.Append("Distinct from path 0: ");
                List<int> list = new List<int>();
                for (int p = 1; p < paths; p++)
                {
                    if (ok[p] && distinctFromZero[p]) { list.Add(p); }
                }
                if (list.Count == 0)
                {
                    sb.Append("none");
                }
                else
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (i > 0) { sb.Append(","); }
                        sb.Append(list[i]);
                    }
                }
                sb.Append(". ");
                sb.Append(OutputsIndependent
                    ? "Outputs 1-15 are INDEPENDENT (per-driver EQ is real). "
                    : "Outputs 1-15 are NOT independent - per-driver EQ is not available on this unit. ");
                sb.Append(MasterEqDistinct
                    ? "Path 16 is a distinct MASTER EQ chain."
                    : "Path 16 matches path 0 - no separate master EQ.");

                // Every sub-3 body is expected to be exactly 1044 bytes. A path returning
                // a different length is a structural difference, not a content one, and
                // would mean our parse assumptions are wrong for that path.
                List<int> oddSize = new List<int>();
                for (int p = 0; p < paths; p++)
                {
                    if (ok[p] && sizes[p] != sizes[0]) { oddSize.Add(p); }
                }
                if (oddSize.Count > 0)
                {
                    sb.Append(" WARNING: body length differs from path 0 at path");
                    if (oddSize.Count == 1) { sb.Append(" "); }
                    else { sb.Append("s "); }
                    for (int i = 0; i < oddSize.Count; i++)
                    {
                        if (i > 0) { sb.Append(","); }
                        sb.Append(oddSize[i]);
                    }
                    sb.Append(" (expected 1044 everywhere).");
                }
            }

            PathProbeSummary = sb.ToString();
        }

        /// <summary>
        /// Read one path's sub-3 body and fingerprint it.
        ///
        /// Retries, because the measured reply rate with the vendor app open was 37% (3/8):
        /// two hosts sharing EP1 IN means whoever reads first takes the reply. A single
        /// miss would otherwise be reported as "path 0 did not reply" and kill the whole
        /// sweep, which is a contention artefact, not a finding about the device.
        /// </summary>
        private bool ReadEqPath(int path, uint[] hashes, int[] sizes, bool[] ok)
        {
            byte[] frame = Builders.ReadParam(ReadSubCmd.EqAndPassBand, Mem, Source, (byte)path);
            for (int attempt = 1; attempt <= ProbeRetries; attempt++)
            {
                PathProbeAttempts++;
                RxFrame reply;
                if (_client.RequestCmd(frame, ReadSubCmd.EqAndPassBand, 1500, out reply))
                {
                    hashes[path] = Fnv1a(reply.Body);
                    sizes[path] = reply.Body.Length;
                    ok[path] = true;
                    return true;
                }
                // Give the other reader a beat to finish before colliding again.
                if (attempt < ProbeRetries) { Thread.Sleep(200); }
            }
            hashes[path] = 0u;
            sizes[path] = 0;
            ok[path] = false;
            return false;
        }

        /// <summary>FNV-1a over a reply body, so two paths can be compared in one word.</summary>
        /// <remarks>internal (not private) so SelfTest can pin the vector.</remarks>
        internal static uint Fnv1a(byte[] data)
        {
            uint h = 2166136261u;
            if (data != null)
            {
                for (int i = 0; i < data.Length; i++)
                {
                    h ^= data[i];
                    h *= 16777619u;
                }
            }
            return h;
        }

        /// <summary>ReadParam sub 2 -> StructRunStatus.</summary>
        private void DoStatus(HttpListenerContext ctx)
        {
            lock (_apiLock)
            {
                if (!_client.IsOpen)
                {
                    WriteJson(ctx.Response, 200, Err("not connected"));
                    return;
                }

                byte[] frame = Builders.ReadParam(ReadSubCmd.MainRunStatus, Mem, Source, PathIndex);
                RxFrame reply;
                if (!_client.RequestCmd(frame, ReadSubCmd.MainRunStatus, 1500, out reply))
                {
                    LastError = _client.LastError;
                    WriteJson(ctx.Response, 200, Err(_client.LastError));
                    return;
                }

                bool ok;
                RunStatus st = ReplyParse.ParseRunStatus(reply.Body, out ok);
                if (!ok)
                {
                    WriteJson(ctx.Response, 200, Err("status reply too short (" + reply.Body.Length + " bytes)"));
                    return;
                }

                Mem = st.memIndex;
                Source = st.bSource;
                MainDb = GainCodec.DecodeMainDb(st.gainInt);
                Mute = st.bMute > 0;

                Dictionary<string, object> d = Ok();
                d["mute"] = st.bMute > 0;
                d["source"] = (int)st.bSource;
                d["sourceName"] = SourceMap.Name((int)st.bSource);
                d["mem"] = (int)st.memIndex;
                d["mainDb"] = GainCodec.DecodeMainDb(st.gainInt);
                d["btOrUsb"] = (st.multiFlag & 1) == 1;

                double[] groups = new double[4];
                for (int i = 0; i < 4; i++)
                {
                    groups[i] = GainCodec.DecodeGroupDb(st.groupGainSub[i]);
                    GroupDb[i] = groups[i];
                }
                d["groupDb"] = groups;
                d["deviceMask"] = DeviceGroupMask;
                // Carried here (not just in /api/info) because DoConnect replies with this
                // same payload - the UI reads them straight off the connect response.
                d["pathProbe"] = PathProbeSummary;
                d["outputsIndependent"] = OutputsIndependent;
                d["masterEqDistinct"] = MasterEqDistinct;
                d["rawHex"] = Hex(reply.Body);
                d["wireHex"] = Hex(reply.Wire);
                d["wireBytes"] = (reply.Wire != null) ? reply.Wire.Length : 0;
                WriteJson(ctx.Response, 200, d);
            }
        }

        /// <summary>
        /// cmd 12 - the master volume control the official app actually uses, carrying mute,
        /// source, memory and the four group volumes as well.
        /// </summary>
        private void DoMaster(HttpListenerContext ctx, Dictionary<string, object> body)
        {
            double mainDb = GetD(body, "mainDb", MainDb);
            bool mute = GetB(body, "mute", Mute);
            bool btOrUsb = GetB(body, "btOrUsb", false);
            double[] groups = GetArr(body, "groups", 4, 0.0);
            byte mem = (byte)GetI(body, "mem", (int)Mem);
            byte source = (byte)GetI(body, "source", (int)Source);

            if (mainDb > Builders.MainGainMaxDb) { mainDb = Builders.MainGainMaxDb; }
            if (mainDb < Builders.MainGainMinDb) { mainDb = Builders.MainGainMinDb; }

            lock (_apiLock)
            {
                if (!_client.IsOpen)
                {
                    WriteJson(ctx.Response, 200, Err("not connected"));
                    return;
                }

                byte[] frame = Builders.RunStatus(mem, source, mute, mainDb, groups, btOrUsb);
                RxFrame reply;
                bool got = _client.Request(frame, 600, out reply);

                Mem = mem;
                Source = source;
                MainDb = mainDb;
                Mute = mute;
                for (int i = 0; i < 4; i++)
                {
                    GroupDb[i] = groups[i];
                }

                Dictionary<string, object> d = Ok();
                d["acked"] = got;
                d["mainDb"] = mainDb;
                d["mute"] = mute;
                d["source"] = (int)source;
                d["sourceName"] = SourceMap.Name((int)source);
                d["txBytes"] = frame.Length;
                d["txHex"] = Hex(frame);
                d["note"] = got ? null
                              : "no reply observed (the firmware does not ACK cmd 12 writes)";
                WriteJson(ctx.Response, 200, d);
            }
        }

        private void DoVolume(HttpListenerContext ctx, Dictionary<string, object> body)
        {
            double[] db = GetArr(body, "groups", 4, 0.0);
            int requestedMask = GetI(body, "mask", unchecked((int)DeviceGroupMask));
            // A mask of 0 targets no outputs at all, which the DSP applies to nothing - that is
            // exactly what made an earlier live test inaudible. Fall back to the mask the
            // device reported, and say so in the response rather than doing it silently.
            uint mask = (requestedMask != 0)
                ? (uint)requestedMask
                : DeviceGroupMask;
            bool substituted = (requestedMask == 0 && DeviceGroupMask != 0);
            byte mem = (byte)GetI(body, "mem", (int)Mem);
            byte path = (byte)GetI(body, "path", (int)PathIndex);
            byte source = (byte)GetI(body, "source", (int)Source);

            lock (_apiLock)
            {
                if (!_client.IsOpen)
                {
                    WriteJson(ctx.Response, 200, Err("not connected"));
                    return;
                }

                byte[] frame = Builders.GroupVolume(mem, path, source, db, mask);
                RxFrame reply;
                bool got = _client.Request(frame, 600, out reply);

                for (int i = 0; i < 4; i++)
                {
                    GroupDb[i] = db[i];
                }
                GroupMask = mask;
                Mem = mem;
                PathIndex = path;
                Source = source;

                Dictionary<string, object> d = Ok();
                d["acked"] = got;
                d["txHex"] = Hex(frame);
                d["maskUsed"] = mask;
                d["deviceMask"] = DeviceGroupMask;
                d["maskSubstituted"] = substituted;
                if (substituted)
                {
                    d["note"] = "requested mask was 0 (no outputs); used the device's mask 0x"
                              + DeviceGroupMask.ToString("X") + " instead";
                }
                if (got)
                {
                    d["replyHex"] = Hex(reply.Body);
                }
                else if (!d.ContainsKey("note"))
                {
                    d["note"] = "no reply observed (device may not ACK writes)";
                }
                WriteJson(ctx.Response, 200, d);
            }
        }

        private void DoInput(HttpListenerContext ctx, Dictionary<string, object> body)
        {
            ushort enable = (ushort)GetI(body, "enable", 0);
            ushort revert = (ushort)GetI(body, "revert", 0);
            double[] gainDb = GetArr(body, "gainDb", 16, 0.0);
            double[] delayMs = GetArr(body, "delayMs", 16, 0.0);
            byte mem = (byte)GetI(body, "mem", (int)Mem);
            byte source = (byte)GetI(body, "source", (int)Source);

            ushort[] gains = new ushort[16];
            ushort[] delays = new ushort[16];
            for (int i = 0; i < 16; i++)
            {
                // DP12_InputGainTrackMin/Max is -20 .. 0 dB. Beyond 0 the 0.1 dB magnitude
                // would flip the bit that means "negative", turning attenuation into boost.
                double g = GainCodec.Clamp(gainDb[i],
                                           GainCodec.InputGainMinDb,
                                           GainCodec.InputGainMaxDb);
                double ms = GainCodec.Clamp(delayMs[i],
                                            GainCodec.InputDelayMinMs,
                                            GainCodec.InputDelayMaxMs);
                gains[i] = EncodeGainDb(g);
                delays[i] = (ushort)Math.Max(0, Math.Min(65535, Math.Round(ms)));
            }

            lock (_apiLock)
            {
                if (!_client.IsOpen)
                {
                    WriteJson(ctx.Response, 200, Err("not connected"));
                    return;
                }

                byte[] frame = Builders.Input(source, mem, enable, revert, gains, delays);
                RxFrame reply;
                bool got = _client.Request(frame, 600, out reply);

                Mem = mem;
                Source = source;

                Dictionary<string, object> d = Ok();
                d["acked"] = got;
                d["txHex"] = Hex(frame);
                WriteJson(ctx.Response, 200, d);
            }
        }

        /// <summary>
        /// POST /api/output â€” cmd 11, the only command that can mute, level, delay or invert
        /// individual outputs.
        ///
        /// The body must carry the COMPLETE block for the memory being written (mute, gainDb,
        /// delayMs, revert â€” sixteen entries each). A missing vector is rejected rather than
        /// padded, because cmd 11 writes all sixteen outputs in one frame: an omitted entry
        /// becomes a zero in a field the DSP then applies, and zero is not "leave alone" but
        /// 0 dB full level. That is exactly how a channel toggle could otherwise wipe a tune's
        /// output levels in one press.
        /// </summary>
        private void DoOutput(HttpListenerContext ctx, Dictionary<string, object> body)
        {
            object[] rawMute = GetObjArr(body, "mute");
            object[] rawRevert = GetObjArr(body, "revert");
            object[] rawGain = GetObjArr(body, "gainDb");
            object[] rawDelay = GetObjArr(body, "delayMs");
            if (rawMute == null || rawRevert == null || rawGain == null || rawDelay == null)
            {
                WriteJson(ctx.Response, 200, Err(
                    "cmd 11 writes all sixteen outputs at once â€” send the complete block: "
                  + "mute[], gainDb[], delayMs[] and revert[], 16 entries each. Partial blocks "
                  + "are rejected because omitted entries become zeroes, and a zero gain is "
                  + "0 dB full level rather than 'unchanged'."));
                return;
            }
            if (rawMute.Length < 16 || rawRevert.Length < 16
                || rawGain.Length < 16 || rawDelay.Length < 16)
            {
                WriteJson(ctx.Response, 200, Err("each output vector needs exactly 16 entries"));
                return;
            }

            byte mem = (byte)GetI(body, "mem", (int)Mem);
            if (mem > 3)
            {
                WriteJson(ctx.Response, 200, Err("mem must be 0..3"));
                return;
            }

            // The count masks the enable bits. Prefer an explicit request, then the value the
            // device reported at connect, then the probe-derived default â€” and report which.
            int requested = GetI(body, "outPathCount", 0);
            byte outPathCount;
            string countSource;
            if (requested >= 1 && requested <= 16)
            {
                outPathCount = (byte)requested;
                countSource = "requested";
            }
            else if (ProductOutPathCount >= 1 && ProductOutPathCount <= 16)
            {
                outPathCount = ProductOutPathCount;
                countSource = OutPathCountSource;
            }
            else
            {
                outPathCount = OutPathCountDefault;
                countSource = "defaulted";
            }

            double[] gainDb = GetArr(body, "gainDb", 16, 0.0);
            double[] delayMs = GetArr(body, "delayMs", 16, 0.0);

            // Normalise the JSON vectors once, so the masks, the encoding and the stored
            // mirror all read the same interpretation of "true".
            bool[] mute = new bool[16];
            bool[] revert = new bool[16];
            for (int i = 0; i < 16; i++)
            {
                mute[i] = AsBool(rawMute[i]);
                revert[i] = AsBool(rawRevert[i]);
            }

            ushort enable = GainCodec.BuildEnableMask(mute, outPathCount);
            ushort revertMask = GainCodec.BuildRevertMask(revert, outPathCount);

            ushort[] gains = new ushort[16];
            ushort[] delays = new ushort[16];
            for (int i = 0; i < 16 && i < outPathCount; i++)
            {
                // Vendor order: only outputs the product actually has get a real gain and
                // delay; the rest are left at zero rather than passed through.
                gains[i] = GainCodec.EncodeOutputGainDb(gainDb[i]);
                delays[i] = GainCodec.EncodeOutputDelayMs(delayMs[i]);
            }

            // Group block travels with cmd 11, so send the tracked levels and the device's own
            // membership mask â€” not a default of 0, which would retarget group volume.
            uint mask = (DeviceGroupMask != 0) ? DeviceGroupMask : GroupMask;
            byte[] groupGain = new byte[4];
            for (int g = 0; g < 4; g++)
            {
                groupGain[g] = GainCodec.EncodeGroupDb(GroupDb[g]);
            }

            lock (_apiLock)
            {
                // Build the frame unconditionally. This is the one command whose result the
                // device cannot be asked to confirm, so returning the exact bytes lets the
                // caller inspect what WOULD go out before trusting it â€” but only an open link
                // transmits, and only a transmission updates the local mirror: marking a block
                // "known" when nothing was sent would be a lie about what the device holds.
                byte[] frame = Builders.Output(mem, enable, revertMask, gains, delays,
                                               groupGain, mask);
                // dryRun short-circuits before the transmit even when the amp IS attached:
                // the measurement run's rehearsal needs the real frame and the real masks to
                // be checkable, and it must never be able to move a speaker by accident. The
                // failure direction is the safe one - a stray dryRun writes nothing.
                bool dry = GetB(body, "dryRun", false);
                bool open = _client.IsOpen && !dry;
                bool got = false;

                Dictionary<string, object> d;
                if (dry)
                {
                    d = Ok();
                    d["dryRun"] = true;
                }
                else
                {
                    d = open ? Ok() : Err(
                        "not connected â€” the frame below was built for inspection but NOT "
                      + "transmitted, and the local mirror was not updated");
                }

                if (open)
                {
                    RxFrame reply;
                    got = _client.Request(frame, 600, out reply);

                    // Store the whole block: this mirror is what the NEXT toggle will
                    // resend, so it must never hold a partial picture of the memory.
                    for (int i = 0; i < 16; i++)
                    {
                        OutputMute[mem * 16 + i] = mute[i];
                        OutputRevert[mem * 16 + i] = revert[i];
                        OutputGainDb[mem * 16 + i] = GainCodec.Clamp(
                            gainDb[i], GainCodec.OutputGainMinDb, GainCodec.OutputGainMaxDb);
                        OutputDelayMs[mem * 16 + i] = GainCodec.Clamp(
                            delayMs[i], GainCodec.InputDelayMinMs, GainCodec.InputDelayMaxMs);
                    }
                    OutputKnown[mem] = true;
                    Mem = mem;
                    SaveOutputMirror();
                }

                d["transmitted"] = open;
                d["acked"] = got;
                d["enableMask"] = enable;
                d["revertMask"] = revertMask;
                d["outPathCount"] = outPathCount;
                d["outPathCountSource"] = countSource;
                d["groupMask"] = mask;
                d["groupDb"] = GroupDb;
                d["frameBytes"] = frame.Length;
                d["frameHex"] = Hex(frame);
                if (dry)
                {
                    d["note"] = "dry run â€” the frame was built and the masks were computed, "
                              + "nothing was transmitted and the local mirror was not updated";
                }
                else if (open && !got)
                {
                    d["note"] = "no reply observed (the firmware does not ACK writes; state is "
                              + "mirrored locally but not confirmed by the device)";
                }
                WriteJson(ctx.Response, 200, d);
            }
        }

        /// <summary>
        /// The cmd 11 mirror, persisted next to the other json files (..\output-mirror.json).
        ///
        /// The DSP cannot be asked what it holds here, so this mirror is the ONLY picture of the
        /// output block that exists anywhere. Keeping it in RAM alone meant every bridge restart
        /// silently replaced a loaded tune's real levels (-4.6 / -7 / ... dB) with plausible
        /// defaults (0 dB = full level), and the next mute toggle, or a measurement run's solo,
        /// would then have written those defaults to the device. Saved on every successful
        /// transmission; reloaded in the constructor. Nothing here is inferred - it is a copy of
        /// what was last actually sent.
        /// </summary>
        private string OutputMirrorFile()
        {
            return Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "output-mirror.json"));
        }

        private void SaveOutputMirror()
        {
            try
            {
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["mem"] = (int)Mem;
                d["outPathCount"] = (int)ProductOutPathCount;
                d["known"] = OutputKnown;
                d["mute"] = OutputMute;
                d["gainDb"] = OutputGainDb;
                d["delayMs"] = OutputDelayMs;
                d["revert"] = OutputRevert;
                File.WriteAllText(OutputMirrorFile(), _json.Serialize(d),
                                  new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Console.WriteLine("output-mirror save failed: " + ex.Message);
            }
        }

        public void LoadOutputMirror()
        {
            string file = OutputMirrorFile();
            if (!File.Exists(file)) { return; }
            try
            {
                Dictionary<string, object> obj =
                    _json.DeserializeObject(File.ReadAllText(file)) as Dictionary<string, object>;
                if (obj == null) { return; }

                // Shape-check before trusting any of it: a short or mismatched file must leave
                // OutputKnown false rather than half-fill the mirror.
                object[] rawMute = GetObjArr(obj, "mute");
                object[] rawRevert = GetObjArr(obj, "revert");
                object[] rawKnown = GetObjArr(obj, "known");
                object[] rawGain = GetObjArr(obj, "gainDb");
                object[] rawDelay = GetObjArr(obj, "delayMs");
                if (rawMute == null || rawRevert == null || rawKnown == null
                    || rawGain == null || rawDelay == null
                    || rawMute.Length != 64 || rawRevert.Length != 64
                    || rawGain.Length != 64 || rawDelay.Length != 64
                    || rawKnown.Length != 4)
                {
                    Console.WriteLine("output-mirror ignored: wrong shape in " + file);
                    return;
                }

                double[] g = GetArr(obj, "gainDb", 64, 0.0);
                double[] dl = GetArr(obj, "delayMs", 64, 0.0);
                for (int i = 0; i < 64; i++)
                {
                    OutputMute[i] = AsBool(rawMute[i]);
                    OutputRevert[i] = AsBool(rawRevert[i]);
                    OutputGainDb[i] = g[i];
                    OutputDelayMs[i] = dl[i];
                }
                for (int i = 0; i < 4; i++) { OutputKnown[i] = AsBool(rawKnown[i]); }

                // Mem travels with the mirror: OutputKnown is per-memory, so the page's
                // "which memory am I looking at" must point at the block that is known.
                int mem = GetI(obj, "mem", 0);
                if (mem >= 0 && mem <= 3) { Mem = (byte)mem; }

                // The device's output count is learned at connect and was also lost on every
                // restart, which flipped the shown channel count to the sixteen fallback. The
                // file records what the device actually reported last time; only use it until
                // a connect answers for itself, and label it as the cached value it is.
                int cnt = GetI(obj, "outPathCount", 0);
                if (cnt >= 1 && cnt <= 16 && OutPathCountSource == "unread")
                {
                    ProductOutPathCount = (byte)cnt;
                    OutPathCountSource = "device (cached)";
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("output-mirror load failed: " + ex.Message);
            }
        }

        /// <summary>JSON booleans arrive as true/false, but accept 1/0 and "true" as well.</summary>
        private static bool AsBool(object v)        {
            if (v is bool) { return (bool)v; }
            if (v == null) { return false; }
            string s = v.ToString().Trim().ToLowerInvariant();
            return s == "true" || s == "1" || s == "yes";
        }

        private void DoEq(HttpListenerContext ctx, Dictionary<string, object> body)
        {
            byte mem = (byte)GetI(body, "mem", (int)Mem);
            byte path = (byte)GetI(body, "path", (int)PathIndex);
            bool byPass = GetB(body, "byPass", ByPass);

            EQS[] eq = new EQS[31];
            object[] bands = GetObjArr(body, "bands");

            for (int i = 0; i < 31; i++)
            {
                EQS s = new EQS();
                s.Fc = (ushort)DspMath.DefaultBandHz[i];
                s.Gain = 0f;
                s.Q = 1.0f;
                s.Type = 0;
                s.Enable = true;

                if (bands != null && i < bands.Length && bands[i] is Dictionary<string, object>)
                {
                    Dictionary<string, object> b = (Dictionary<string, object>)bands[i];
                    s.Fc = (ushort)GetI(b, "fc", (int)s.Fc);
                    s.Gain = (float)GetD(b, "gain", 0.0);
                    s.Q = (float)GetD(b, "q", 1.0);
                    s.Type = (byte)GetI(b, "type", 0);
                    s.Enable = GetB(b, "enable", true);
                }
                eq[i] = s;
            }

            EqCoff[] packed = DspMath.BuildEqBands(eq);

            lock (_apiLock)
            {
                if (!_client.IsOpen)
                {
                    WriteJson(ctx.Response, 200, Err("not connected"));
                    return;
                }

                byte[] frame = Builders.Eq(mem, path, packed, byPass);
                RxFrame reply;
                bool got = _client.Request(frame, 800, out reply);

                Mem = mem;
                PathIndex = path;
                ByPass = byPass;
                for (int i = 0; i < 31; i++)
                {
                    BandGainDb[i] = eq[i].Gain;
                    BandEnabled[i] = eq[i].Enable;
                    BandQ[i] = eq[i].Q;
                    BandHz[i] = eq[i].Fc;
                }

                Dictionary<string, object> d = Ok();
                d["acked"] = got;
                d["txBytes"] = frame.Length;
                d["txHex"] = Hex(frame);
                WriteJson(ctx.Response, 200, d);
            }
        }

        /// <summary>
        /// POST /api/passband â€” cmd 13, one path's crossover pair. Scalars (edge
        /// frequencies, slopes, filter types) pass through as given; coefficients are
        /// computed by the vendor's own HlpGetCoff inside DspMath.BuildPassBand, so the
        /// DSP receives byte-identical math to what the official app sends.
        ///
        /// Body: { "mem":1, "path":0,
        ///         "low":  {"fc":2000,"slope":3,"filterType":1,"hiLow":1,"butt":1,"enable":true},
        ///         "high": {"fc":20000,...} }
        /// slope 1/2/3 = 12/18/24 dB/oct; filterType 1 = Linkwitz-Riley (AP.cs menus).
        /// hiLow/butt/enable ride into HlpGetCoff only â€” the wire struct carries Fc,
        /// slope and filterType per slot, exactly as DspSendPassBandOfOnePathConfigData.
        ///
        /// The firmware does not ACK cmd 13: the readback through /api/eqread's passband
        /// block (scalars + all 40 coefficients) is the verification path.
        /// </summary>
        private void DoPassBand(HttpListenerContext ctx, Dictionary<string, object> body)
        {
            byte mem = (byte)GetI(body, "mem", (int)Mem);
            byte path = (byte)GetI(body, "path", (int)PathIndex);

            Dictionary<string, object> low = (body.ContainsKey("low") ? body["low"] : null)
                as Dictionary<string, object>;
            Dictionary<string, object> high = (body.ContainsKey("high") ? body["high"] : null)
                as Dictionary<string, object>;
            if (low == null || high == null)
            {
                WriteJson(ctx.Response, 200, Err(
                    "send both filters: {\"low\":{\"fc\":..,\"slope\":..,\"filterType\":..,"
                  + "\"hiLow\":..,\"butt\":..,\"enable\":true},\"high\":{...}}"));
                return;
            }

            HLPS l = new HLPS();
            l.Fc = GetI(low, "fc", 20);
            l.Slope = GetI(low, "slope", 1);
            l.filterType = GetI(low, "filterType", 1);
            l.hiLowType = GetI(low, "hiLow", 0);
            l.butt_Type = GetI(low, "butt", 0);
            l.Enable = GetB(low, "enable", true);

            HLPS h = new HLPS();
            h.Fc = GetI(high, "fc", 20000);
            h.Slope = GetI(high, "slope", 1);
            h.filterType = GetI(high, "filterType", 1);
            h.hiLowType = GetI(high, "hiLow", 1);
            h.butt_Type = GetI(high, "butt", 0);
            h.Enable = GetB(high, "enable", true);

            PassBand pb = DspMath.BuildPassBand(l, h);

            // dryRun: build the exact 168-byte block and return it without transmitting.
            // This is the pre-flight path â€” the bytes can be diffed against a saved
            // device readback (scalars as ints, coefficients as Q24 hex) before a real
            // cmd 13 is ever put on the wire.
            if (GetB(body, "dryRun", false))
            {
                int pbLen;
                byte[] pbBytes = Marshalling.StructToBytes(pb, out pbLen);
                Dictionary<string, object> dr = Ok();
                dr["dryRun"] = true;
                dr["lowFc"] = (int)pb.lowFreq;
                dr["highFc"] = (int)pb.highFreq;
                dr["lowSlope"] = (int)pb.lowSlope;
                dr["highSlope"] = (int)pb.highSlope;
                dr["lowFilterType"] = (int)pb.lowFilterType;
                dr["highFilterType"] = (int)pb.highFilterType;
                dr["pbBytes"] = pbLen;
                dr["pbHex"] = Hex(pbBytes);
                WriteJson(ctx.Response, 200, dr);
                return;
            }

            lock (_apiLock)
            {
                if (!_client.IsOpen)
                {
                    WriteJson(ctx.Response, 200, Err("not connected"));
                    return;
                }

                byte[] frame = Builders.PassBand(mem, path, pb);
                RxFrame reply;
                bool got = _client.Request(frame, 800, out reply);

                Mem = mem;
                PathIndex = path;

                Dictionary<string, object> d = Ok();
                d["acked"] = got;
                d["lowFc"] = (int)pb.lowFreq;
                d["highFc"] = (int)pb.highFreq;
                d["lowSlope"] = (int)pb.lowSlope;
                d["highSlope"] = (int)pb.highSlope;
                d["lowFilterType"] = (int)pb.lowFilterType;
                d["highFilterType"] = (int)pb.highFilterType;
                d["txBytes"] = frame.Length;
                d["txHex"] = Hex(frame);
                if (!got)
                {
                    d["note"] = "no reply observed (the firmware does not ACK writes â€” "
                              + "verify with /api/eqread passband readback)";
                }
                WriteJson(ctx.Response, 200, d);
            }
        }

        /// <summary>
        /// On-demand path sweep. Deliberately NOT run on connect: 17 reads at a 2.5 s
        /// timeout is up to ~42 s when paths stay silent, and connect should be instant.
        /// ProbeEqPaths is called here so the answer is one tap away.
        /// </summary>
        private void DoEqProbe(HttpListenerContext ctx)
        {
            lock (_apiLock)
            {
                if (!_client.IsOpen)
                {
                    WriteJson(ctx.Response, 200, Err("not connected"));
                    return;
                }

                ProbeEqPaths();

                Dictionary<string, object> d = Ok();
                d["summary"] = PathProbeSummary;
                d["outputsIndependent"] = OutputsIndependent;
                d["masterEqDistinct"] = MasterEqDistinct;
                d["mem"] = (int)Mem;
                d["hashes"] = PathHashes;
                d["sizes"] = PathSizes;
                d["attempts"] = PathProbeAttempts;
                d["retriesPerPath"] = ProbeRetries;
                d["note"] = "hashes are FNV-1a of each path's raw sub-3 body; equal hashes " +
                            "mean byte-identical replies, and every body should be 1044 bytes";
                WriteJson(ctx.Response, 200, d);
            }
        }

        private void DoEqRead(HttpListenerContext ctx, Dictionary<string, object> body)
        {
            byte mem = (byte)GetI(body, "mem", (int)Mem);
            byte path = (byte)GetI(body, "path", (int)PathIndex);

            lock (_apiLock)
            {
                if (!_client.IsOpen)
                {
                    WriteJson(ctx.Response, 200, Err("not connected"));
                    return;
                }

                byte[] frame = Builders.ReadParam(ReadSubCmd.EqAndPassBand, mem, Source, path);
                RxFrame reply;
                if (!_client.RequestCmd(frame, ReadSubCmd.EqAndPassBand, 2500, out reply))
                {
                    LastError = _client.LastError;
                    WriteJson(ctx.Response, 200, Err(_client.LastError));
                    return;
                }

                EqCoff[] bands;
                byte byPass;
                if (!ReplyParse.TryParseEqGroup(reply.Body, out bands, out byPass))
                {
                    WriteJson(ctx.Response, 200, Err("EQ reply too short (" + reply.Body.Length + " bytes)"));
                    return;
                }

                object[] list = new object[31];
                for (int i = 0; i < 31; i++)
                {
                    Dictionary<string, object> b = new Dictionary<string, object>();
                    b["fc"] = (int)bands[i].freq;
                    b["gain"] = ReplyParse.DecodeEqGainDb(bands[i].gain);
                    b["q"] = ReplyParse.DecodeEqQ(bands[i].Q);
                    b["coeffs"] = bands[i].CoffData;
                    // StructEqCoff carries no per-band enable bit -- Enable is a host-side
                    // flag in the official app that decides whether real coefficients are
                    // computed or zeroed on write. Nothing comes back from the device.
                    b["enable"] = true;
                    list[i] = b;

                    BandGainDb[i] = (double)b["gain"];
                    BandQ[i] = (double)b["q"];
                    BandHz[i] = (ushort)(int)b["fc"];
                }
                ByPass = byPass != 0;

                // Sub-3 body = EqGroup (872) + PassBand (168) + mCrc (4). Pull the
                // crossover block back too: cmd 13 writes are verified against these
                // bytes, scalars and all forty coefficients.
                Dictionary<string, object> passband = null;
                if (reply.Body != null
                    && reply.Body.Length >= ReplyParse.EqGroupSize + ReplyParse.PassBandSize)
                {
                    byte[] pbBytes = new byte[ReplyParse.PassBandSize];
                    Array.Copy(reply.Body, ReplyParse.EqGroupSize, pbBytes, 0, pbBytes.Length);
                    PassBand pb = (PassBand)Marshalling.BytesToStruct(pbBytes, typeof(PassBand));
                    passband = new Dictionary<string, object>();
                    passband["lowFc"] = (int)pb.lowFreq;
                    passband["highFc"] = (int)pb.highFreq;
                    passband["lowSlope"] = (int)pb.lowSlope;
                    passband["highSlope"] = (int)pb.highSlope;
                    passband["lowFilterType"] = (int)pb.lowFilterType;
                    passband["highFilterType"] = (int)pb.highFilterType;
                    // Coefficients ride the wire big-endian: normalize back to Q24 form
                    // (reverse of the write-side UInt32Small2Big) before reading the value.
                    string[] lowHex = new string[20];
                    string[] highHex = new string[20];
                    double[] lowDec = new double[20];
                    double[] highDec = new double[20];
                    for (int j = 0; j < 4; j++)
                    {
                        for (int k = 0; k < 5; k++)
                        {
                            uint lowNorm = Marshalling.UInt32Small2Big(
                                pb.lowFreqCoff[j].CoffData[k]);
                            uint highNorm = Marshalling.UInt32Small2Big(
                                pb.highFreqCoff[j].CoffData[k]);
                            lowHex[j * 5 + k] = lowNorm.ToString("X8");
                            highHex[j * 5 + k] = highNorm.ToString("X8");
                            lowDec[j * 5 + k] = DspMath.CalcQ24DecimalOfHex(lowNorm);
                            highDec[j * 5 + k] = DspMath.CalcQ24DecimalOfHex(highNorm);
                        }
                    }
                    passband["lowCoffHex"] = lowHex;
                    passband["highCoffHex"] = highHex;
                    passband["lowCoff"] = lowDec;
                    passband["highCoff"] = highDec;
                }

                Dictionary<string, object> d = Ok();
                d["byPass"] = ByPass;
                d["bands"] = list;
                if (passband != null) { d["passband"] = passband; }
                d["bodyBytes"] = reply.Body.Length;
                d["wireHex"] = Hex(reply.Wire);
                d["wireBytes"] = (reply.Wire != null) ? reply.Wire.Length : 0;
                WriteJson(ctx.Response, 200, d);
            }
        }

        /// <summary>
        /// cmd 15 sub 1 (StructDspCfg) returned raw. Read-only: the input config, the mix
        /// routing tables and the per-memory output block (enable/revert/gain/delay for all
        /// sixteen outputs) all live in this body - cmd 11/18/12 write them but no write
        /// ever needs to be guessed at when the device can just be asked.
        /// </summary>
        private void DoDspCfg(HttpListenerContext ctx)
        {
            lock (_apiLock)
            {
                if (!_client.IsOpen)
                {
                    WriteJson(ctx.Response, 200, Err("not connected"));
                    return;
                }

                byte[] frame = Builders.ReadParam(ReadSubCmd.BaseDsp, Mem, Source, PathIndex);
                RxFrame reply;
                if (!_client.RequestCmd(frame, ReadSubCmd.BaseDsp, 2500, out reply))
                {
                    LastError = _client.LastError;
                    WriteJson(ctx.Response, 200, Err(_client.LastError));
                    return;
                }

                Dictionary<string, object> d = Ok();
                d["bodyBytes"] = reply.Body.Length;
                d["bodyHex"] = Hex(reply.Body);
                d["wireHex"] = Hex(reply.Wire);
                WriteJson(ctx.Response, 200, d);
            }
        }

        /// <summary>
        /// Computes one band's coefficients locally (no USB) so the UI can show what will be
        /// sent and the maths can be validated against the official app side by side.
        /// </summary>
        private void DoEqCoef(HttpListenerContext ctx, Dictionary<string, object> body)
        {
            EQS s = new EQS();
            s.Fc = (ushort)GetI(body, "fc", 1000);
            s.Gain = (float)GetD(body, "gain", 0.0);
            s.Q = (float)GetD(body, "q", 1.0);
            s.Type = (byte)GetI(body, "type", 0);
            s.Enable = GetB(body, "enable", true);

            EQS[] one = new EQS[31];
            one[0] = s;   // remaining bands stay null -> BuildEqBands fills them flat

            EqCoff[] packed = DspMath.BuildEqBands(one);

            Dictionary<string, object> d = Ok();
            d["freq"] = (int)packed[0].freq;
            d["gainEnc"] = (int)packed[0].gain;
            d["qEnc"] = (int)packed[0].Q;
            d["coeffs"] = packed[0].CoffData;
            WriteJson(ctx.Response, 200, d);
        }

        // ---------------- static files ----------------

        private void ServeStatic(HttpListenerContext ctx, string path)
        {
            string name = (path == "/" || path.Length == 1) ? "index.html" : path.TrimStart('/');
            if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0 || name.IndexOf("..") >= 0)
            {
                ctx.Response.StatusCode = 400;
                ctx.Response.Close();
                return;
            }

            string full = Path.Combine(_webRoot, name);
            if (!File.Exists(full))
            {
                ctx.Response.StatusCode = 404;
                byte[] nf = Encoding.UTF8.GetBytes("not found");
                ctx.Response.ContentType = "text/plain";
                ctx.Response.OutputStream.Write(nf, 0, nf.Length);
                ctx.Response.Close();
                return;
            }

            byte[] data = File.ReadAllBytes(full);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = ContentTypeFor(name);
            ctx.Response.ContentLength64 = data.Length;
            ctx.Response.OutputStream.Write(data, 0, data.Length);
            ctx.Response.Close();
        }

        private static string ContentTypeFor(string name)
        {
            string ext = Path.GetExtension(name).ToLowerInvariant();
            switch (ext)
            {
                case ".html": return "text/html; charset=utf-8";
                case ".js": return "application/javascript; charset=utf-8";
                case ".css": return "text/css; charset=utf-8";
                case ".json": return "application/json; charset=utf-8";
                // A sweep is played by the phone as a native <audio> file, not through Web Audio:
                // on 2026-10-20 the page's OscillatorNode sweep played 2.5 s and stopped when the
                // phone dimmed, twice, which cost a whole measurement session. Native playback
                // survives the screen locking and does not depend on an AudioContext staying
                // resumed. It needs a real audio type, not octet-stream.
                case ".wav": return "audio/wav";
                case ".mp3": return "audio/mpeg";
                case ".png": return "image/png";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".ico": return "image/x-icon";
                default: return "application/octet-stream";
            }
        }

        // ---------------- helpers ----------------

        // POST /api/rta-meas {meas:[{t,db,name}]} â€” phone â†’ computer hand-off of the
        // RTA measurement list. Merges by capture timestamp t (resends idempotent),
        // keeps newest 200, one write per user action (never a timer â€” see the
        // capture-thread OneDrive note). GET returns everything stored so far.
        private void DoRtaMeas(HttpListenerContext ctx, Dictionary<string, object> body)
        {
            string file = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "rta-measurements.json"));

            if (ctx.Request.HttpMethod != "POST")
            {
                List<object> all = MeasLoad(file);
                Dictionary<string, object> resp = Ok();
                resp["count"] = all.Count;
                resp["file"] = file;
                resp["meas"] = all;
                WriteJson(ctx.Response, 200, resp);
                return;
            }

            object raw;
            if (body == null || !body.TryGetValue("meas", out raw) || raw == null ||
                raw is string || !(raw is System.Collections.IEnumerable))
            {
                WriteJson(ctx.Response, 400,
                    Err("need {\"meas\":[{t,db,name},...]}"));
                return;
            }

            List<object> all2 = MeasLoad(file);
            HashSet<long> have = new HashSet<long>();
            foreach (object e in all2)
            {
                long t;
                if (MeasTime(e, out t)) { have.Add(t); }
            }

            int added = 0, dupes = 0, bad = 0;
            foreach (object o in (System.Collections.IEnumerable)raw)
            {
                long t;
                if (!MeasTime(o, out t) || !MeasHasDb(o)) { bad++; continue; }
                if (have.Contains(t)) { dupes++; continue; }
                have.Add(t);
                all2.Add(o);
                added++;
            }

            all2.Sort(delegate (object a, object b)
            {
                long ta = 0, tb = 0;
                MeasTime(a, out ta);
                MeasTime(b, out tb);
                return ta.CompareTo(tb);
            });
            if (all2.Count > 200)
            {
                all2.RemoveRange(0, all2.Count - 200);
            }

            try
            {
                File.WriteAllText(file, _json.Serialize(all2), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                WriteJson(ctx.Response, 500, Err("write failed: " + ex.Message));
                return;
            }

            Dictionary<string, object> r = Ok();
            r["added"] = added;
            r["dupes"] = dupes;
            r["bad"] = bad;
            r["total"] = all2.Count;
            r["file"] = file;
            WriteJson(ctx.Response, 200, r);
        }

        /// <summary>
        /// POST /api/run-save {count, label} -> one folder per completed cycle, one file per
        /// channel, on the laptop. The page calls this when a run finishes.
        ///
        /// The captures already live in rta-measurements.json; that file is a single growing
        /// array, which is fine for the app and useless for handing one channel to a person or
        /// a model. This writes the last `count` captures out as channel 1..N, with an index
        /// that states the grid, the units and the gate verdict - so a file read months later
        /// cannot be misread as a normalised curve.
        /// </summary>
        private void DoRunSave(HttpListenerContext ctx, Dictionary<string, object> body)
        {
            if (ctx.Request.HttpMethod != "POST")
            {
                WriteJson(ctx.Response, 405, Err("POST {count,label}"));
                return;
            }

            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, ".."));
            string measFile = Path.Combine(root, "rta-measurements.json");

            int count = GetI(body, "count", 0);
            string label = null;
            object lraw;
            if (body != null && body.TryGetValue("label", out lraw) && lraw is string)
            {
                label = ((string)lraw).Trim();
            }

            List<object> all = MeasLoad(measFile);
            if (count <= 0 || count > all.Count) { count = Math.Min(8, all.Count); }
            if (count <= 0)
            {
                WriteJson(ctx.Response, 200, Err("no measurements stored yet"));
                return;
            }
            List<object> take = all.GetRange(all.Count - count, count);

            if (string.IsNullOrEmpty(label))
            {
                label = "Car tune (" + DateTime.Now.ToString("yyyy-MM-dd") + ")";
            }
            foreach (char c in Path.GetInvalidFileNameChars()) { label = label.Replace(c, '-'); }
            label = label.Trim();
            if (label.Length == 0) { label = "Car tune"; }

            // Same-day re-runs must not overwrite the previous folder: a measurement set that
            // was silently replaced is worse than no set at all.
            string dir = Path.Combine(root, "measurements", label);
            int n = 1;
            while (Directory.Exists(dir))
            {
                n++;
                dir = Path.Combine(root, "measurements", label + " (" + n + ")");
            }
            Directory.CreateDirectory(dir);

            // The grid follows the take: current takes are 1/24-octave (241 points), legacy
            // takes 1/6 (61). Re-exporting an old set must never relabel its frequencies.
            int gridLen = 61;
            for (int k = 0; k < take.Count && gridLen == 61; k++)
            {
                Dictionary<string, object> td = take[k] as Dictionary<string, object>;
                object traw;
                if (td == null || !td.TryGetValue("db", out traw) || traw == null) { continue; }
                object[] tdb = traw as object[];
                if (tdb == null && traw is System.Collections.ArrayList)
                { tdb = ((System.Collections.ArrayList)traw).ToArray(); }
                if (tdb != null && tdb.Length > 100) { gridLen = 241; }
            }
            double gridStep = (gridLen > 100) ? 24.0 : 6.0;
            double[] grid = new double[gridLen];
            for (int i = 0; i < gridLen; i++) { grid[i] = Math.Round(20.0 * Math.Pow(2.0, i / gridStep), 2); }
            string gridDesc = (gridLen > 100) ? "1/24-octave" : "1/6-octave";
            string gridLabel = (gridLen > 100) ? "1/24 octave, 241 points, 20 Hz to 20 kHz"
                                               : "1/6 octave, 61 points, 20 Hz to 20 kHz";

            List<object> files = new List<object>();
            StringBuilder readme = new StringBuilder();
            readme.AppendLine("# " + Path.GetFileName(dir));
            readme.AppendLine();
            readme.AppendLine("Written by the bridge when a run cycle finished, " +
                              DateTime.Now.ToString("yyyy-MM-dd HH:mm") + ".");
            readme.AppendLine("One file per channel. Values are absolute dBFS on a " + gridDesc + " grid");
            readme.AppendLine("(" + gridLen + " points, 20 Hz - 20 kHz), not normalised - a curve is only");
            readme.AppendLine("comparable to another taken at the same level with the same crossovers.");
            readme.AppendLine();
            readme.AppendLine("| file | channel | peak dBFS | at | gate | sig burst |");
            readme.AppendLine("|---|---|---|---|---|---|");

            for (int k = 0; k < take.Count; k++)
            {
                object e = take[k];
                Dictionary<string, object> d = e as Dictionary<string, object>;
                if (d == null) { continue; }

                object raw;
                object[] dbArr = null;
                if (d.TryGetValue("db", out raw) && raw != null)
                {
                    dbArr = raw as object[];
                    if (dbArr == null && raw is System.Collections.ArrayList)
                    {
                        dbArr = ((System.Collections.ArrayList)raw).ToArray();
                    }
                }
                if (dbArr == null || dbArr.Length < 10) { continue; }

                string name = "";
                if (d.TryGetValue("name", out raw) && raw is string) { name = (string)raw; }
                long t = 0;
                MeasTime(e, out t);
                double peak = double.NegativeInfinity;
                int peakIdx = 0;
                List<object> points = new List<object>();
                for (int i = 0; i < dbArr.Length && i < gridLen; i++)
                {
                    double v = Convert.ToDouble(dbArr[i], System.Globalization.CultureInfo.InvariantCulture);
                    if (v > peak) { peak = v; peakIdx = i; }
                    points.Add(new object[] { grid[i], Math.Round(v, 2) });
                }

                StringBuilder csv = new StringBuilder("hz,dbfs\n");
                for (int i = 0; i < points.Count; i++)
                {
                    object[] pt = (object[])points[i];
                    csv.Append(pt[0]).Append(',').Append(pt[1]).Append('\n');
                }

                Dictionary<string, object> sig = null;
                if (d.TryGetValue("sig", out raw)) { sig = raw as Dictionary<string, object>; }

                Dictionary<string, object> rec = new Dictionary<string, object>();
                rec["channel"] = name;
                rec["channelNumber"] = k + 1;
                rec["run"] = Path.GetFileName(dir);
                if (t > 0)
                {
                    rec["capturedAt"] = DateTimeOffset.FromUnixTimeMilliseconds(t)
                        .ToLocalTime().ToString("yyyy-MM-ddTHH:mm:sszzz");
                }
                rec["units"] = "dBFS";
                rec["grid"] = gridLabel;
                rec["peak"] = new Dictionary<string, object>
                {
                    { "dbfs", Math.Round(peak, 2) },
                    { "hz", grid[Math.Min(peakIdx, gridLen - 1)] }
                };
                // Same shape export-meas.ps1 writes, so tune/fit-tune.py reads either folder
                // without a special case. "sig" is kept as well: it carries the gate's reason
                // in words, which is what a person needs when a channel is refused.
                rec["sig"] = sig;
                if (sig != null)
                {
                    Dictionary<string, object> gateBlock = new Dictionary<string, object>();
                    object gv;
                    gateBlock["ok"] = sig.TryGetValue("ok", out gv) && gv is bool && (bool)gv;
                    gateBlock["burstDb"] = sig.TryGetValue("burstDb", out gv) ? gv : null;
                    gateBlock["fromHz"] = sig.TryGetValue("from", out gv) ? gv : null;
                    gateBlock["toHz"] = sig.TryGetValue("to", out gv) ? gv : null;
                    rec["gate"] = gateBlock;
                }
                rec["points"] = points;

                string stem = "channel " + (k + 1);
                File.WriteAllText(Path.Combine(dir, stem + ".json"), _json.Serialize(rec),
                                  new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(dir, stem + ".csv"), csv.ToString(),
                                  new UTF8Encoding(false));

                // REW copy (File > Import frequency response, Ctrl+I): only lines that
                // begin with a number are imported, so the '*' header is provenance and
                // costs nothing on import. Trace name = the channel name, slugged.
                string rwSrc = (name != null && name.Length > 0) ? name : stem;
                System.Text.StringBuilder sbN = new System.Text.StringBuilder();
                for (int ci = 0; ci < rwSrc.Length; ci++)
                {
                    char c = char.ToLowerInvariant(rwSrc[ci]);
                    bool keep = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
                    sbN.Append(keep ? c : '-');
                }
                string rwSlug = sbN.ToString().Trim('-');
                if (rwSlug.Length == 0) { rwSlug = stem.Replace(' ', '-'); }
                StringBuilder rew = new StringBuilder();
                rew.Append("* ").Append(rwSrc)
                   .Append("  -  ds18-bridge measurement ").Append(Path.GetFileName(dir)).Append("\n");
                if (t > 0)
                {
                    rew.Append("* captured ").Append(DateTimeOffset.FromUnixTimeMilliseconds(t)
                        .ToLocalTime().ToString("yyyy-MM-dd HH:mm"))
                       .Append(", absolute dBFS at the mic, EQ flat, not normalised\n");
                }
                rew.Append("* import in REW: File > Import frequency response (Ctrl+I)\n");
                rew.Append("* grid: ").Append(gridLabel).Append("\n");
                for (int i = 0; i < points.Count; i++)
                {
                    object[] pt = (object[])points[i];
                    rew.Append(Convert.ToString(pt[0], System.Globalization.CultureInfo.InvariantCulture))
                       .Append(' ')
                       .Append(Convert.ToString(pt[1], System.Globalization.CultureInfo.InvariantCulture))
                       .Append('\n');
                }
                string rewDir = Path.Combine(dir, "rew");
                if (!Directory.Exists(rewDir)) { Directory.CreateDirectory(rewDir); }
                File.WriteAllText(Path.Combine(rewDir, rwSlug + ".txt"), rew.ToString(),
                                  new UTF8Encoding(false));

                files.Add(stem + ".json");

                string gate = "?";
                object burstObj = null;
                bool ok = false;
                if (sig != null)
                {
                    object o;
                    if (sig.TryGetValue("ok", out o)) { ok = (o is bool) && (bool)o; }
                    gate = ok ? "ok" : "REJECT";
                    if (sig.TryGetValue("burstDb", out burstObj)) { }
                }
                readme.AppendLine("| `" + stem + ".json` | " + (name.Length > 0 ? name : "(unnamed)") +
                                  " | " + Math.Round(peak, 1) + " | " + grid[Math.Min(peakIdx, gridLen - 1)] +
                                  " Hz | " + gate + " | " + (burstObj == null ? "-" : burstObj.ToString()) + " |");
            }

            File.WriteAllText(Path.Combine(dir, "README.md"), readme.ToString(), new UTF8Encoding(false));

            Dictionary<string, object> resp = Ok();
            resp["folder"] = dir;
            resp["files"] = files;
            resp["count"] = files.Count;
            WriteJson(ctx.Response, 200, resp);
        }

        // GET /api/targets -> the stored target-curve set (house curve + per-channel
        // crossover windows); POST {targets:{name,db[31],offsetDb,chans[8]}} saves it.
        // Same folder as rta-measurements.json, one write per user action (debounced
        // page-side), never a timer.
        private void DoTargets(HttpListenerContext ctx, Dictionary<string, object> body)
        {
            string file = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "targets.json"));

            if (ctx.Request.HttpMethod != "POST")
            {
                Dictionary<string, object> resp = Ok();
                resp["file"] = file;
                if (File.Exists(file))
                {
                    try { resp["targets"] = _json.DeserializeObject(File.ReadAllText(file)); }
                    catch (Exception ex) { resp["error"] = ex.Message; }
                }
                WriteJson(ctx.Response, 200, resp);
                return;
            }

            object raw;
            if (body == null || !body.TryGetValue("targets", out raw) || raw == null ||
                raw is string || !(raw is Dictionary<string, object>))
            {
                WriteJson(ctx.Response, 400, Err("need {\"targets\":{...}}"));
                return;
            }

            string text = _json.Serialize(raw);
            if (text.Length > 65536)
            {
                WriteJson(ctx.Response, 400, Err("targets too large"));
                return;
            }

            try
            {
                File.WriteAllText(file, text, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                WriteJson(ctx.Response, 500, Err("write failed: " + ex.Message));
                return;
            }

            Dictionary<string, object> r = Ok();
            r["file"] = file;
            r["bytes"] = text.Length;
            WriteJson(ctx.Response, 200, r);
        }

        // GET /api/tunename -> {"name":"..."}; POST {"name":"night cruise"} saves the
        // single tune name that replaces the old Memory 1-4 tabs in the header. The name
        // is the app's own label (the DSP protocol has no name field), defaults to the
        // loaded .idsp file name, and is written once per user edit - never a timer.
        private void DoTuneName(HttpListenerContext ctx, Dictionary<string, object> body)
        {
            string file = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "tunename.json"));

            if (ctx.Request.HttpMethod != "POST")
            {
                Dictionary<string, object> resp = Ok();
                resp["file"] = file;
                string cur = "";
                if (File.Exists(file))
                {
                    try
                    {
                        object parsed = _json.DeserializeObject(File.ReadAllText(file));
                        Dictionary<string, object> obj = parsed as Dictionary<string, object>;
                        object v;
                        if (obj != null && obj.TryGetValue("name", out v) && v is string)
                        {
                            cur = (string)v;
                        }
                    }
                    catch (Exception ex) { resp["error"] = ex.Message; }
                }
                resp["name"] = cur;
                WriteJson(ctx.Response, 200, resp);
                return;
            }

            object raw;
            if (body == null || !body.TryGetValue("name", out raw) || !(raw is string))
            {
                WriteJson(ctx.Response, 400, Err("need {\"name\":\"...\"}"));
                return;
            }

            string name = ((string)raw).Trim();
            if (name.Length > 64)
            {
                WriteJson(ctx.Response, 400, Err("name too long (max 64)"));
                return;
            }
            for (int i = 0; i < name.Length; i++)
            {
                if (name[i] < 0x20)
                {
                    WriteJson(ctx.Response, 400, Err("name has control characters"));
                    return;
                }
            }

            Dictionary<string, object> store = new Dictionary<string, object>();
            store["name"] = name;
            string text = _json.Serialize(store);
            if (text.Length > 65536)
            {
                WriteJson(ctx.Response, 400, Err("name too large"));
                return;
            }

            try
            {
                File.WriteAllText(file, text, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                WriteJson(ctx.Response, 500, Err("write failed: " + ex.Message));
                return;
            }

            Dictionary<string, object> r2 = Ok();
            r2["file"] = file;
            r2["bytes"] = text.Length;
            r2["name"] = name;
            WriteJson(ctx.Response, 200, r2);
        }

        private List<object> MeasLoad(string file)
        {
            List<object> list = new List<object>();
            try
            {
                if (File.Exists(file))
                {
                    object parsed = _json.DeserializeObject(File.ReadAllText(file));
                    System.Collections.IEnumerable e = parsed as System.Collections.IEnumerable;
                    if (e != null && !(parsed is string))
                    {
                        foreach (object o in e) { list.Add(o); }
                    }
                }
            }
            catch { /* unreadable file = start fresh rather than lose the send */ }
            return list;
        }

        private static bool MeasTime(object entry, out long t)
        {
            t = 0;
            Dictionary<string, object> d = entry as Dictionary<string, object>;
            object v;
            if (d == null || !d.TryGetValue("t", out v) || v == null) { return false; }
            try { t = Convert.ToInt64(v); return true; } catch { return false; }
        }

        private static bool MeasHasDb(object entry)
        {
            Dictionary<string, object> d = entry as Dictionary<string, object>;
            object v;
            if (d == null || !d.TryGetValue("db", out v) || v == null || v is string)
            { return false; }
            System.Collections.IEnumerable e = v as System.Collections.IEnumerable;
            if (e == null) { return false; }
            int n = 0;
            foreach (object x in e) { n++; if (n >= 8) { return true; } }
            return false;
        }

        private Dictionary<string, object> ReadBody(HttpListenerContext ctx)
        {
            if (ctx.Request.HttpMethod != "POST")
            {
                return new Dictionary<string, object>();
            }
            try
            {
                using (StreamReader r = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
                {
                    string text = r.ReadToEnd();
                    if (string.IsNullOrEmpty(text))
                    {
                        return new Dictionary<string, object>();
                    }
                    object parsed = _json.DeserializeObject(text);
                    Dictionary<string, object> d = parsed as Dictionary<string, object>;
                    return d ?? new Dictionary<string, object>();
                }
            }
            catch
            {
                return new Dictionary<string, object>();
            }
        }

        /// <summary>
        /// POST /api/review {name?, xml?, mem?, ref?} -> an .idsp reviewed against THIS car.
        ///
        /// The analysis lives in tune\review-tune.py, which imports the same modules the
        /// prediction and the atlas use, so the app can never disagree with the reports.
        /// If "xml" is supplied - a file the user picked in the browser, which the server has
        /// never seen - it is written into var\review\ first. Never outside this project tree.
        ///
        /// The tool is a separate process on purpose: the maths is Python's, it is tested
        /// there, and re-implementing it in C# would create a second truth.
        /// </summary>
        private void DoReview(HttpListenerContext ctx, Dictionary<string, object> body)
        {
            string name = GetS(body, "name", "");
            string xml = GetS(body, "xml", "");
            int mem = GetI(body, "mem", -1);
            string reference = GetS(body, "ref", "");

            string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
            string reviewDir = Path.Combine(root, "var", "review");
            string file = "";

            if (reference.Length == 0)
            {
                // No reference given: compare against the tune the car is playing, which the
                // app's own label (tunename.json, defaulting to the loaded .idsp file name)
                // names. Falls back to the tool's own default if that file is not there.
                reference = PlayingTuneFile(root);
            }

            try
            {
                Directory.CreateDirectory(reviewDir);
                if (xml.Length > 0)
                {
                    // A file the browser read: keep it in the project's own tree, under a
                    // sanitised name, and say so in the response.
                    string safe = SanitizeTuneName(name);
                    if (safe.Length == 0)
                    {
                        WriteJson(ctx.Response, 200, Err(
                            "name must be a plain .idsp filename (letters, digits, dash, underscore)"));
                        return;
                    }
                    Directory.CreateDirectory(reviewDir);
                    file = Path.Combine(reviewDir, safe);
                    File.WriteAllText(file, xml, new UTF8Encoding(false));
                }
                else
                {
                    string plain = SanitizeTuneName(name);
                    if (plain.Length == 0)
                    {
                        WriteJson(ctx.Response, 200, Err("name must be a plain .idsp filename"));
                        return;
                    }
                    string inWeb = Path.Combine(Path.GetFullPath(_webRoot), plain);
                    string inReview = Path.Combine(reviewDir, plain);
                    if (File.Exists(inWeb)) { file = inWeb; }
                    else if (File.Exists(inReview)) { file = inReview; }
                    else
                    {
                        WriteJson(ctx.Response, 200, Err(plain + " is not in the tunes folder"));
                        return;
                    }
                }

                string tool = Path.Combine(root, "tune", "review-tune.py");
                if (!File.Exists(tool))
                {
                    WriteJson(ctx.Response, 200, Err("review-tune.py is missing from tune\\"));
                    return;
                }

                string outJson = Path.Combine(reviewDir, Path.GetFileNameWithoutExtension(file) + ".review.json");
                string stdout, stderr;
                int code = RunTool(tool, file, mem, reference, outJson, out stdout, out stderr);
                if (code != 0)
                {
                    WriteJson(ctx.Response, 200, Err("review failed (exit " + code + "): " +
                        (stderr.Length > 400 ? stderr.Substring(stderr.Length - 400) : stderr)));
                    return;
                }
                if (!File.Exists(outJson))
                {
                    WriteJson(ctx.Response, 200, Err("review produced no output"));
                    return;
                }
                string payload = File.ReadAllText(outJson, Encoding.UTF8);
                WriteRawJson(ctx.Response, 200, payload);
            }
            catch (Exception ex)
            {
                WriteJson(ctx.Response, 200, Err("review failed: " + ex.Message));
            }
        }

        /// <summary>The .idsp file the car is playing, from the app's own label, if it is there.</summary>
        private static string PlayingTuneFile(string root)
        {
            try
            {
                string labelFile = Path.Combine(root, "tunename.json");
                if (!File.Exists(labelFile)) { return ""; }
                string label = "";
                using (StreamReader r = new StreamReader(labelFile, Encoding.UTF8))
                {
                    Dictionary<string, object> d =
                        new JavaScriptSerializer().DeserializeObject(r.ReadToEnd()) as Dictionary<string, object>;
                    if (d != null)
                    {
                        object v;
                        if (d.TryGetValue("name", out v) && v != null) { label = v.ToString(); }
                    }
                }
                label = label.Trim();
                if (label.Length == 0) { return ""; }
                string candidate = Path.Combine(Path.Combine(root, "web"), label + ".idsp");
                return File.Exists(candidate) ? candidate : "";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>Plain .idsp filename only: no path, no traversal, no spaces.</summary>
        private static string SanitizeTuneName(string raw)        {
            if (string.IsNullOrEmpty(raw)) { return ""; }
            string n = raw.Trim();
            int slash = n.LastIndexOfAny(new char[] { '/', '\\' });
            if (slash >= 0) { n = n.Substring(slash + 1); }
            if (!n.ToLowerInvariant().EndsWith(".idsp")) { return ""; }
            foreach (char c in n)
            {
                if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.')) { return ""; }
            }
            return n;
        }

        /// <summary>Runs the review tool and captures its output. Returns the exit code.</summary>
        private static int RunTool(string tool, string file, int mem, string reference,
                                   string outJson, out string stdout, out string stderr)
        {
            stdout = "";
            stderr = "";
            string py = PythonExe();
            if (py.Length == 0)
            {
                stderr = "no python found (set DS18_PYTHON to the full path of python.exe)";
                return -1;
            }
            string args = "\"" + tool + "\" \"" + file + "\"";
            if (mem >= 0) { args += " --mem " + mem.ToString(); }
            if (reference.Length > 0) { args += " --ref \"" + reference + "\""; }
            args += " --json \"" + outJson + "\"";

            ProcessStartInfo psi = new ProcessStartInfo(py, args);
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            psi.WorkingDirectory = Path.GetDirectoryName(tool);
            using (Process p = Process.Start(psi))
            {
                stdout = p.StandardOutput.ReadToEnd();
                stderr = p.StandardError.ReadToEnd();
                if (!p.WaitForExit(120000))
                {
                    try { p.Kill(); } catch { }
                    stderr = "review timed out after 120 s";
                    return -2;
                }
                return p.ExitCode;
            }
        }

        /// <summary>Python for the tune tools: env override, then PATH, then the usual install.</summary>
        private static string PythonExe()
        {
            string env = Environment.GetEnvironmentVariable("DS18_PYTHON");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) { return env; }
            string[] candidates = {
                "python.exe",
                @"C:\Python315\python.exe",
                @"C:\Python313\python.exe",
                @"C:\Python312\python.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                             "Programs", "Python", "Python315", "python.exe")
            };
            foreach (string c in candidates)
            {
                if (c.IndexOf(Path.DirectorySeparatorChar) >= 0)
                {
                    if (File.Exists(c)) { return c; }
                }
                else
                {
                    return c;   // resolved from PATH by CreateProcess
                }
            }
            return "";
        }

        private static void WriteRawJson(HttpListenerResponse resp, int status, string payload)
        {
            byte[] data = Encoding.UTF8.GetBytes(payload);
            resp.StatusCode = status;
            resp.ContentType = "application/json; charset=utf-8";
            resp.ContentLength64 = data.Length;
            resp.OutputStream.Write(data, 0, data.Length);
            resp.Close();
        }

        private static string GetS(Dictionary<string, object> d, string key, string fallback)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) { return fallback; }
            string s = v as string;
            if (s == null) { s = v.ToString(); }
            return s ?? fallback;
        }

        private void WriteJson(HttpListenerResponse resp, int status, object payload)
        {
            byte[] data = Encoding.UTF8.GetBytes(_json.Serialize(payload));
            resp.StatusCode = status;
            resp.ContentType = "application/json; charset=utf-8";
            resp.ContentLength64 = data.Length;
            resp.OutputStream.Write(data, 0, data.Length);
            resp.Close();
        }

        private static Dictionary<string, object> Ok()
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["ok"] = true;
            return d;
        }

        private static Dictionary<string, object> Err(string message)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["ok"] = false;
            d["error"] = message;
            return d;
        }

        private static double GetD(Dictionary<string, object> d, string key, double fallback)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null)
            {
                return fallback;
            }
            try { return Convert.ToDouble(v); }
            catch { return fallback; }
        }

        private static int GetI(Dictionary<string, object> d, string key, int fallback)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null)
            {
                return fallback;
            }
            try { return Convert.ToInt32(Math.Round(Convert.ToDouble(v))); }
            catch { return fallback; }
        }

        private static bool GetB(Dictionary<string, object> d, string key, bool fallback)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null)
            {
                return fallback;
            }
            if (v is bool)
            {
                return (bool)v;
            }
            try { return Convert.ToDouble(v) != 0.0; }
            catch { return fallback; }
        }

        private static double[] GetArr(Dictionary<string, object> d, string key, int count, double fallback)
        {
            double[] result = new double[count];
            object[] raw = GetObjArr(d, key);
            if (raw == null)
            {
                return result;
            }
            for (int i = 0; i < count && i < raw.Length; i++)
            {
                if (raw[i] == null)
                {
                    result[i] = fallback;
                    continue;
                }
                try { result[i] = Convert.ToDouble(raw[i]); }
                catch { result[i] = fallback; }
            }
            return result;
        }

        private static object[] GetObjArr(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null)
            {
                return null;
            }
            return v as object[];
        }

        /// <summary>Input gain encoding matches the source: 0.1 dB magnitude, bit7 = negative.</summary>
        private static ushort EncodeGainDb(double db)
        {
            int tenths = (int)Math.Round(Math.Abs(db) * 10.0);
            if (tenths > 127)
            {
                tenths = 127;
            }
            return (ushort)(tenths | ((db < 0.0) ? 0x80 : 0));
        }

        private static string Hex(byte[] data)
        {
            if (data == null)
            {
                return "";
            }
            StringBuilder sb = new StringBuilder(data.Length * 2);
            for (int i = 0; i < data.Length; i++)
            {
                sb.Append(data[i].ToString("X2"));
            }
            return sb.ToString();
        }
    }
}
