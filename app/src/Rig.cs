using System;
using System.Collections.Generic;
using System.Threading;

namespace DreTunes
{
    /// <summary>
    /// Everything the app knows about the rig right now, and when it learned it.
    ///
    /// One rule shapes this class: a value is never stored without the time it was read and the
    /// way it was obtained. The DSP cannot echo command 11, so the levels and delays in here come
    /// from the bridge's mirror of what it last sent - they are labelled "mirror" everywhere they
    /// are shown. EQ and crossovers are genuine device reads and are labelled with their time.
    ///
    /// A background thread polls; every surface paints from this snapshot. Nothing on the UI
    /// thread ever touches the network.
    /// </summary>
    public sealed class Rig
    {
        public readonly Api Api = new Api();
        public string Root;                       // ds18-bridge folder (tunes, evidence, scripts)

        private Thread _worker;
        private volatile bool _stop;
        private volatile bool _eqWanted;
        private readonly object _lock = new object();

        public event Action Changed;

        // ---- bridge + device
        public bool BridgeUp;
        public string BridgeError = "";
        public DateTime InfoAt = DateTime.MinValue;
        public DateTime StatusAt = DateTime.MinValue;
        /// <summary>
        /// True once a /api/status read has supplied Mem. /api/info carries a memory number too,
        /// but that one is the bridge's remembered value, not a read this app made - and a label
        /// that cannot tell the two apart is how "2 of 4 playing" ends up on screen with the DSP
        /// unplugged.
        /// </summary>
        public bool MemFromStatus;
        public bool DspOpen;
        public string DspError = "";
        public int Mem = -1;
        public double Master;
        public bool Muted;
        public int Source;
        public string SourceName = "";
        public bool BtOrUsb;
        public double[] GroupDb = new double[4];
        public int OutPathCount = 8;

        // ---- command 11 mirror (never a device echo)
        public bool OutputKnown;
        public double[] Gain = new double[16];
        public double[] Delay = new double[16];
        public bool[] OMute = new bool[16];
        public bool[] ORevert = new bool[16];
        public DateTime MirrorAt = DateTime.MinValue;

        // ---- tune name the bridge remembers (a label, not evidence)
        public string BridgeTuneName = "";
        public DateTime NameAt = DateTime.MinValue;

        // ---- EQ + crossover readback (real device reads)
        public Band[][] Bands;                    // [8][31]
        public PassBand[] Pb;                     // [8] high-pass edge (the low crossover)
        /// <summary>
        /// [8] low-pass edge. The readback carries both edges; the app used to parse the
        /// low-pass and throw it away, which left every surface guessing that it was open at
        /// 20 kHz. It is kept now, and it is what the Tune tab's LOW PASS column shows.
        /// </summary>
        public PassBand[] PbLow;
        public bool[] Bypass;                     // [8]
        public bool EqOk;
        public string EqError = "";
        public DateTime EqAt = DateTime.MinValue;
        public bool EqReading;

        // ---- RTA / microphone
        public bool RtaUp;
        public string MicDevice = "";
        public int MicCh;
        public string MicStatus = "";
        public double MicLevel;
        public string MicDrift = "";
        public int MicIdx = -1;
        public bool CalLoaded;
        public string CalSerial = "";
        public int CalPoints;
        public double CalFrom, CalTo;
        public DateTime RtaAt = DateTime.MinValue;

        public string LastAction = "";
        public DateTime LastActionAt = DateTime.MinValue;

        public void Note(string what)
        {
            LastAction = what;
            LastActionAt = DateTime.Now;
            Raise();
        }

        private void Raise()
        {
            Action h = Changed;
            if (h != null)
            {
                try { h(); }
                catch { }
            }
        }

        // ------------------------------------------------------------------ lifecycle

        public void Start()
        {
            if (_worker != null) return;
            _stop = false;
            _worker = new Thread(Loop);
            _worker.IsBackground = true;
            _worker.Name = "rig-poll";
            _worker.Start();
        }

        public void Stop()
        {
            _stop = true;
            Thread t = _worker;
            _worker = null;
            if (t != null) t.Join(1500);
        }

        /// <summary>Ask the worker to read the device's EQ on its next tick (8 USB round trips).</summary>
        public void RequestEq()
        {
            _eqWanted = true;
        }

        private void Loop()
        {
            int tick = 0;
            while (!_stop)
            {
                PollInfo();
                PollRta();
                if (tick % 3 == 0) PollStatus();
                if (tick % 12 == 0) PollName();
                if (_eqWanted)
                {
                    _eqWanted = false;
                    string err;
                    ReadEq(out err);
                }
                Raise();
                tick++;
                for (int i = 0; i < 8 && !_stop; i++) Thread.Sleep(150);
            }
        }

        // ------------------------------------------------------------------ polls

        private void PollInfo()
        {
            try
            {
                Dictionary<string, object> d = Api.Get("/api/info", 2500);
                lock (_lock)
                {
                    BridgeUp = true;
                    BridgeError = "";
                    InfoAt = DateTime.Now;
                    DspOpen = Json.B(d, "open", false);
                    DspError = Json.S(d, "error", "");
                    if (Json.Get(d, "mem") != null) Mem = Json.I(d, "mem", Mem);
                    Master = Json.D(d, "mainDb", Master);
                    Muted = Json.B(d, "mute", Muted);
                    SourceName = Json.S(d, "sourceName", SourceName);
                    Source = Json.I(d, "source", Source);
                    OutPathCount = Json.I(d, "outPathCount", OutPathCount);

                    // outputKnown is per memory. Index by the memory we are on.
                    bool[] known = Json.BArr(d, "outputKnown");
                    OutputKnown = false;
                    if (known != null && Mem >= 0 && Mem < known.Length) OutputKnown = known[Mem];

                    double[] g = Json.DArr(d, "outputGainDb");
                    double[] dl = Json.DArr(d, "outputDelayMs");
                    bool[] mu = Json.BArr(d, "outputMute");
                    bool[] rv = Json.BArr(d, "outputRevert");
                    if (g != null) for (int i = 0; i < 16 && i < g.Length; i++) Gain[i] = g[i];
                    if (dl != null) for (int i = 0; i < 16 && i < dl.Length; i++) Delay[i] = dl[i];
                    if (mu != null) for (int i = 0; i < 16 && i < mu.Length; i++) OMute[i] = mu[i];
                    if (rv != null) for (int i = 0; i < 16 && i < rv.Length; i++) ORevert[i] = rv[i];
                    if (OutputKnown && MirrorAt == DateTime.MinValue) MirrorAt = DateTime.Now;
                }
            }
            catch (ApiException ex)
            {
                lock (_lock)
                {
                    BridgeUp = false;
                    BridgeError = ex.Message;
                    DspOpen = false;
                }
            }
        }

        private void PollStatus()
        {
            if (!BridgeUp) return;
            try
            {
                Dictionary<string, object> d = Api.Get("/api/status", 3000);
                if (!Json.B(d, "ok", false))
                {
                    lock (_lock)
                    {
                        DspOpen = false;
                        DspError = Json.S(d, "error", "status read failed");
                    }
                    return;
                }
                lock (_lock)
                {
                    DspOpen = true;
                    DspError = "";
                    StatusAt = DateTime.Now;
                    Mem = Json.I(d, "mem", Mem);
                    MemFromStatus = true;
                    Master = Json.D(d, "mainDb", Master);
                    Muted = Json.B(d, "mute", Muted);
                    Source = Json.I(d, "source", Source);
                    SourceName = Json.S(d, "sourceName", SourceName);
                    BtOrUsb = Json.B(d, "btOrUsb", BtOrUsb);
                    double[] g = Json.DArr(d, "groupDb");
                    if (g != null) for (int i = 0; i < 4 && i < g.Length; i++) GroupDb[i] = g[i];
                }
            }
            catch (ApiException ex)
            {
                lock (_lock)
                {
                    if (ex.Down) { BridgeUp = false; DspOpen = false; BridgeError = ex.Message; }
                    else { DspError = ex.Message; }
                }
            }
        }

        private void PollRta()
        {
            try
            {
                Dictionary<string, object> d = Api.GetRta("/rta.json", 2000);
                lock (_lock)
                {
                    RtaUp = Json.B(d, "ok", true);
                    MicDevice = Json.S(d, "device", "");
                    MicCh = Json.I(d, "ch", 0);
                    MicStatus = Json.S(d, "status", "");
                    MicLevel = Json.D(d, "levelDb", -120);
                    MicDrift = Json.S(d, "devDrift", "");
                    MicIdx = Json.I(d, "devOpenIdx", -1);
                    Dictionary<string, object> cal = Json.Obj(Json.Get(d, "cal"));
                    CalLoaded = Json.B(cal, "loaded", false);
                    CalSerial = Json.S(cal, "serial", "");
                    CalPoints = Json.I(cal, "points", 0);
                    CalFrom = Json.D(cal, "fmin", 0);
                    CalTo = Json.D(cal, "fmax", 0);
                    RtaAt = DateTime.Now;
                }
            }
            catch (ApiException)
            {
                lock (_lock) { RtaUp = false; }
            }
        }

        private void PollName()
        {
            if (!BridgeUp) return;
            try
            {
                Dictionary<string, object> d = Api.Get("/api/tunename", 2000);
                lock (_lock)
                {
                    BridgeTuneName = Json.S(d, "name", BridgeTuneName);
                    NameAt = DateTime.Now;
                }
            }
            catch (ApiException) { }
        }

        // ------------------------------------------------------------------ device reads

        /// <summary>
        /// Read EQ + crossovers off the device for the eight live paths. Read-only, and the only
        /// way this app knows what the amplifier actually holds.
        ///
        /// /api/eqread does not echo the path it read (protocol gotcha 18), so every result is
        /// stamped with the path from this loop's index. Without that, anything comparing reads
        /// to a snapshot silently compares them all to path 0.
        /// </summary>
        public bool ReadEq(out string error)
        {
            error = "";
            int mem;
            lock (_lock) { mem = Mem; }
            if (mem < 0)
            {
                lock (_lock) { EqOk = false; EqError = "device memory is not known yet"; }
                error = "device memory is not known yet";
                return false;
            }

            Band[][] bands = new Band[Channels.Live][];
            PassBand[] pb = new PassBand[Channels.Live];
            PassBand[] pbLow = new PassBand[Channels.Live];
            bool[] bypass = new bool[Channels.Live];
            DateTime started = DateTime.Now;
            lock (_lock) { EqReading = true; }
            Raise();

            try
            {
                for (int p = 0; p < Channels.Live; p++)
                {
                    Dictionary<string, object> d = Api.Post("/api/eqread", Json.Body("mem", mem, "path", p), 6000);
                    if (!Json.B(d, "ok", false))
                        throw new ApiException("path " + p + ": " + Json.S(d, "error", "read refused"), false);

                    List<object> list = Json.Arr(Json.Get(d, "bands"));
                    Band[] b = new Band[31];
                    for (int i = 0; i < 31; i++)
                    {
                        b[i] = new Band(20, 0, 1);
                        if (list != null && i < list.Count)
                        {
                            Dictionary<string, object> bo = Json.Obj(list[i]);
                            b[i].Fc = Json.D(bo, "fc", 20);
                            b[i].Gain = Json.D(bo, "gain", 0);
                            b[i].Q = Json.D(bo, "q", 1);
                            b[i].Enable = Json.B(bo, "enable", true);
                        }
                    }
                    bands[p] = b;
                    bypass[p] = Json.B(d, "byPass", false);

                    Dictionary<string, object> pbd = Json.Obj(Json.Get(d, "passband"));
                    PassBand hp, lp;
                    ParsePassBand(pbd, out hp, out lp);
                    pb[p] = hp;
                    pbLow[p] = lp;
                }

                lock (_lock)
                {
                    Bands = bands;
                    Pb = pb;
                    PbLow = pbLow;
                    Bypass = bypass;
                    EqOk = true;
                    EqError = "";
                    EqAt = DateTime.Now;
                    EqReading = false;
                }
                Raise();
                return true;
            }
            catch (ApiException ex)
            {
                lock (_lock)
                {
                    EqOk = false;
                    EqError = ex.Message;
                    EqReading = false;
                }
                error = ex.Message;
                Raise();
                return false;
            }
        }

        // ------------------------------------------------------------------ actions

        /// <summary>
        /// Split one /api/eqread passband block into its two edges.
        ///
        /// The wire names the LOW edge "lowFc" and the HIGH edge "highFc"; the low edge is the
        /// high-pass filter and the high edge is the low-pass. Reading them the other way round
        /// would put 1.5 kHz in the low-pass column for every tweeter, which is exactly the kind
        /// of swap that looks plausible on screen - so it lives here, alone, and is asserted.
        ///
        /// Neither edge comes back with an enable bit: the firmware reports Fc, slope and filter
        /// type only, so "this filter is switched off" is not something a read can establish.
        /// </summary>
        public static void ParsePassBand(Dictionary<string, object> pbd, out PassBand hp, out PassBand lp)
        {
            hp = null;
            lp = null;
            if (pbd == null) return;

            hp = new PassBand();
            hp.HiLow = 0;
            hp.Fc = Json.D(pbd, "lowFc", 0);
            hp.Slope = Json.I(pbd, "lowSlope", 0);
            hp.FilterType = Json.I(pbd, "lowFilterType", 0);
            hp.Enable = true;

            lp = new PassBand();
            lp.HiLow = 1;
            lp.Fc = Json.D(pbd, "highFc", 0);
            lp.Slope = Json.I(pbd, "highSlope", 0);
            lp.FilterType = Json.I(pbd, "highFilterType", 0);
            lp.Enable = true;

            if (Mutation.SwapPassBands)
            {
                double f = hp.Fc; hp.Fc = lp.Fc; lp.Fc = f;
                int s = hp.Slope; hp.Slope = lp.Slope; lp.Slope = s;
                int t = hp.FilterType; hp.FilterType = lp.FilterType; lp.FilterType = t;
            }
        }

        public bool Connect(out string message)
        {
            message = "";
            try
            {
                Dictionary<string, object> d = Api.Post("/api/connect", Json.Body(), 15000);
                bool ok = Json.B(d, "ok", false);
                message = ok ? "DSP connected" : Json.S(d, "error", "connect failed");
                PollInfo();
                if (ok) { PollStatus(); RequestEq(); }
                Raise();
                return ok;
            }
            catch (ApiException ex)
            {
                message = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Point rta.exe at a capture device by index. It remembers nothing across a replug: every
        /// start comes back on the Windows default, which on this laptop is the Realtek array that
        /// reads -93 dBFS forever. This is read-mostly of the rig, and the app never claims the
        /// switch took without re-reading /rta.json.
        /// </summary>
        public bool PointMic(int index, out string message)
        {
            message = "";
            try
            {
                Dictionary<string, object> d = Api.GetRta("/device?n=" + index, 5000);
                bool ok = Json.B(d, "ok", true);
                message = ok ? "asked rta.exe for input " + index : Json.S(d, "error", "refused");
                Thread.Sleep(700);
                PollRta();
                if (MicIdx == index && MicDevice.Length > 0)
                    message += "; now on " + MicDevice;
                else
                    message += "; it did NOT report the new device";
                Raise();
                return ok;
            }
            catch (ApiException ex)
            {
                message = ex.Message;
                return false;
            }
        }

        public List<Dictionary<string, object>> MicDevices()
        {
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            try
            {
                Dictionary<string, object> d = Api.GetRta("/devices", 3000);
                List<object> arr = Json.Arr(Json.Get(d, "devices"));
                if (arr != null)
                    for (int i = 0; i < arr.Count; i++)
                    {
                        Dictionary<string, object> o = Json.Obj(arr[i]);
                        if (o != null) list.Add(o);
                    }
            }
            catch (ApiException) { }
            return list;
        }

        // ------------------------------------------------------------------ derived state

        public int TunedPaths
        {
            get
            {
                Band[][] b;
                lock (_lock) b = Bands;
                if (b == null) return -1;
                int n = 0;
                for (int p = 0; p < b.Length; p++)
                {
                    bool any = false;
                    for (int i = 0; i < b[p].Length; i++) if (Math.Abs(b[p][i].Gain) > 0.05) { any = true; break; }
                    if (any) n++;
                }
                return n;
            }
        }

        public bool Fresh(DateTime at, int seconds)
        {
            return at != DateTime.MinValue && (DateTime.Now - at).TotalSeconds <= seconds;
        }

        /// <summary>
        /// The honest one-line answer to "is the DSP really talking to us". `open` alone is not
        /// enough: the handle can be open while the last read failed, which is exactly what the
        /// arming UI must not paper over.
        /// </summary>
        public bool DeviceLive
        {
            get { lock (_lock) return BridgeUp && DspOpen && StatusAt != DateTime.MinValue && Fresh(StatusAt, 30); }
        }

        public string DeviceStateWord
        {
            get
            {
                if (!BridgeUp) return "bridge down";
                if (!DspOpen || DspError.Length > 0) return "not reading";
                if (!Fresh(StatusAt, 30)) return "link open, no recent read";
                return "live";
            }
        }

        public void SetMirrorTouch()
        {
            lock (_lock) { MirrorAt = DateTime.Now; }
        }
    }
}
