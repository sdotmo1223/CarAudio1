using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Rta
{
    // RTA capture daemon for the DS18 bridge.
    //
    // Captures the laptop's default (or selected) waveform-audio input via winmm, runs an
    // 65536-point FFT (Hann, 3/4 overlap), smooths to 1/24-octave bands and writes an atomic
    // JSON snapshot the bridge serves as /rta.json. The phone UI polls it; no bridge code
    // changes and no device needed - the DSP itself has no USB audio path, so the stimulus
    // comes from the phone (Web Audio) through the head unit, and this box only listens.
    //
    //   rta.exe --list                 enumerate capture devices
    //   rta.exe --device N             capture device index (default: system default)
    //   rta.exe --out <path>           where to write rta.json
    //   rta.exe --rate 48000           try rate (falls back to 44100)
    //
    // HTTP: GET  /rta.json             snapshot (bands + cal + devIdx + hold)
    //       GET  /devices              winmm device list (index/name/ch/inUse)
    //       GET  /device?n=N|default   switch capture device at runtime
    //       GET/POST /hold?on=1|0      freeze the peak trace (no decay) - the sweep prerequisite;
    //              /hold?reset=1      clear the held peaks; /hold alone reports the state
    //       POST /cal?name=<f>.txt     upload miniDSP/REW mic cal file (raw body);
    //                                  verified (SERNO, >=50 pts, ascending) then
    //                                  applied to all 241 bands + saved next to rta.exe
    //       GET  /cal                  cal status (loaded/serial/points/range)
    //
    // Windows/Ghidra read-only boundary does not apply: this is plain winmm, no RE.
    static class RtaMeter
    {
        const int FftSize = 65536;        // 0.73 Hz bins at 48 kHz: a 1/24-oct band at 100 Hz is 2.9 Hz
        const int NumBands = 241;           // 20 Hz .. ~20 kHz in 1/24-octave steps
        const double BandF0 = 20.0;
        const double PeakDecayDbPerSec = 3.0;

        // Peak-hold decay while `hold` is OFF: the peaks fall 3 dB/s, which is what makes the
        // live trace readable (a sliding few-second maximum). With `hold` ON the decay is 0, so
        // the peaks only ever rise: a 10 s sweep would otherwise leave the low end ~30 dB down by
        // the time it reaches 20 kHz, which is why a sweep needs this before it can be measured.
        const double HoldDecayDbPerSec = 0.0;

        // ---------------- winmm ----------------
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        struct WaveInCaps
        {
            public ushort wMid;
            public ushort wPid;
            public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szPName;
            public uint dwFormats;
            public ushort wChannels;
            public ushort dwReserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WaveFormatEx
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WaveHdr
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        [DllImport("winmm.dll")]
        static extern uint waveInGetNumDevs();

        [DllImport("winmm.dll", CharSet = CharSet.Ansi)]
        static extern int waveInGetDevCaps(uint uDeviceID, ref WaveInCaps caps, int cb);

        [DllImport("winmm.dll")]
        static extern int waveInOpen(out IntPtr phwi, uint uDeviceID, ref WaveFormatEx pwfx,
                                     IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);

        // Which device did that handle ACTUALLY open? waveInOpen(WAVE_MAPPER) resolves the default
        // itself, and caps(0) is not necessarily its pick - asking the handle is the only truthful
        // source. Added 2026-10-21 after /rta.json could name the UMIK while capturing the laptop.
        [DllImport("winmm.dll")]
        static extern int waveInGetID(IntPtr hwi, out uint puDeviceID);

        [DllImport("winmm.dll")]
        static extern int waveInPrepareHeader(IntPtr hwi, ref WaveHdr phwi, int cbwh);

        [DllImport("winmm.dll")]
        static extern int waveInUnprepareHeader(IntPtr hwi, ref WaveHdr phwi, int cbwh);

        [DllImport("winmm.dll")]
        static extern int waveInAddBuffer(IntPtr hwi, ref WaveHdr phwi, int cbwh);

        [DllImport("winmm.dll")]
        static extern int waveInStart(IntPtr hwi);

        [DllImport("winmm.dll")]
        static extern int waveInReset(IntPtr hwi);

        [DllImport("winmm.dll")]
        static extern int waveInClose(IntPtr hwi);

        [DllImport("kernel32.dll")]
        static extern int CloseHandle(IntPtr hObject);

        // mmsystem.h: CALLBACK_WINDOW=0x00010000, CALLBACK_EVENT=0x00050000.
        // Using the WINDOW value here once cost waveInOpen(err=11) on every format
        // (the event handle got read as an HWND) - verified with wavein-probe.cs.
        const uint CALLBACK_EVENT = 0x00050000;
        const uint WAVE_FORMAT_PCM = 1;
        const uint WHDR_DONE = 0x00000001;

        // ---------------- state ----------------
        static volatile bool _run = true;
        static readonly object _lock = new object();
        static double[] _bandDb = new double[NumBands];
        static double[] _bandPeakDb = new double[NumBands];
        static double _levelDb = -120.0;
        static double _peakDb = -120.0;
        static long _frames;
        static string _deviceName = "";
        static int _channels;                       // channels actually captured (UMIK-1 = 1, laptop array = 2)
        static uint _openDevId = 0xFFFFFFFF;        // the device winmm really opened (waveInGetID)
        static string _status = "starting";
        static int _fs = 48000;
        static double _loopMsAvg;
        static long _loops, _loopTimeouts;

        // Freeze/hold-peak (protocol sec.31 prerequisite for sweeps). _hold stops the peak decay; it is
        // read on the capture thread and set from the HTTP thread, hence volatile + _lock for
        // the reset. The live trace (_bandDb) keeps moving either way - it is the HELD trace
        // (_bandPeakDb, reported as bands[].peak) that stops falling.
        static volatile bool _hold;
        static DateTime _heldSinceUtc = DateTime.MinValue;
        static long _holds, _resets;      // evidence: the mode and the clears actually happened

        // Runtime-selected capture device (0xFFFFFFFF = system default). /device?n=N
        // flips _restartReq; the capture loop breaks out and Main reopens on _devId.
        static volatile uint _devId = 0xFFFFFFFF;
        static volatile bool _restartReq;

        // Mic calibration (miniDSP/REW .txt). Correction is ADDED to each band - REW's
        // convention, so miniDSP UMIK files drop straight in. Arrays are swapped
        // atomically under _lock; the capture thread snapshots the references per
        // analysis, so an upload never touches the audio path.
        static double[] _calF = new double[0];
        static double[] _calDb = new double[0];
        static string _calName = "";
        static string _calSerial = "";

        static RtaMeter()
        {
            for (int i = 0; i < NumBands; i++) { _bandDb[i] = -120.0; _bandPeakDb[i] = -120.0; }
        }

        static double BandCenter(int k) { return BandF0 * Math.Pow(2.0, k / 24.0); }

        // ---------------- mic calibration (miniDSP/REW .txt) ----------------
        // Format (verified on the real UMIK-1 files): optional quoted header line
        // "Sens Factor =-0.388dB, SERNO: 7170061", optional label line, then
        // tab-separated "freqHz<TAB>dB" pairs. The dB values are what REW adds to a
        // measurement, so we add them too - same convention, files stay portable.

        static string CalFilePath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rta-cal.txt");
        }

        // Parses + verifies an uploaded cal file, applies it, optionally persists it
        // next to rta.exe. Returns the verification JSON (never throws to caller).
        static string LoadCalText(string text, string name, bool persist)
        {
            try
            {
                string serial = "";
                double sens = double.NaN;
                System.Collections.Generic.List<double> fs =
                    new System.Collections.Generic.List<double>();
                System.Collections.Generic.List<double> dbs =
                    new System.Collections.Generic.List<double>();

                string[] lines = text.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    string ln = lines[i].Trim();
                    if (ln.Length == 0) { continue; }

                    if (serial.Length == 0 && ln.IndexOf("SERNO", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        // "Sens Factor =-0.388dB, SERNO: 7170061" (quote chars tolerated)
                        int si = ln.IndexOf("SERNO", StringComparison.OrdinalIgnoreCase);
                        int colon = ln.IndexOf(':', si);
                        if (colon > 0)
                        {
                            int j = colon + 1;
                            while (j < ln.Length && !char.IsDigit(ln[j])) { j++; }
                            int k = j;
                            while (k < ln.Length && char.IsDigit(ln[k])) { k++; }
                            if (k > j) { serial = ln.Substring(j, k - j); }
                        }
                        int ei = ln.IndexOf("dB", StringComparison.OrdinalIgnoreCase);
                        int eq = ln.IndexOf('=');
                        if (ei > eq && eq >= 0)
                        {
                            double sv;
                            if (double.TryParse(ln.Substring(eq + 1, ei - eq - 1).Trim(),
                                    NumberStyles.Float, CultureInfo.InvariantCulture, out sv))
                            { sens = sv; }
                        }
                        continue;   // header, not a data line
                    }

                    // data line: "<freq>\t<dB>" (tab or whitespace separated)
                    string[] parts = ln.Split(new char[] { '\t' },
                                              StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2)
                    {
                        parts = ln.Split(new char[] { ' ' },
                                         StringSplitOptions.RemoveEmptyEntries);
                    }
                    if (parts.Length < 2) { continue; }
                    double f, d;
                    if (!double.TryParse(parts[0].Trim(), NumberStyles.Float,
                            CultureInfo.InvariantCulture, out f) ||
                        !double.TryParse(parts[1].Trim(), NumberStyles.Float,
                            CultureInfo.InvariantCulture, out d))
                    { continue; }
                    if (f <= 0 || d < -60 || d > 60) { continue; }   // sanity: not our data
                    fs.Add(f); dbs.Add(d);
                }

                // ---- verification gates ----
                if (fs.Count < 50)
                {
                    return "{\"ok\":false,\"error\":\"rejected: only " + fs.Count +
                           " usable freq/db pairs (need 50+) - not a calibration file?\"}";
                }
                for (int i = 1; i < fs.Count; i++)
                {
                    if (fs[i] <= fs[i - 1])
                    {
                        return "{\"ok\":false,\"error\":\"rejected: frequencies not " +
                               "ascending at index " + i + "\"}";
                    }
                }
                if (serial.Length == 0)
                {
                    return "{\"ok\":false,\"error\":\"rejected: no SERNO header - " +
                           "this does not look like a miniDSP calibration file\"}";
                }

                double[] cf = fs.ToArray();
                double[] cd = dbs.ToArray();
                lock (_lock) { _calF = cf; _calDb = cd; _calName = name; _calSerial = serial; }

                if (persist)
                {
                    // HTTP thread, not the capture thread - the no-IO rule holds.
                    File.WriteAllText(CalFilePath(), text, new UTF8Encoding(false));
                }

                System.Globalization.NumberFormatInfo inv =
                    CultureInfo.InvariantCulture.NumberFormat;
                StringBuilder sb = new StringBuilder(512);
                sb.Append("{\"ok\":true,\"name\":\"").Append(JsonEscape(name)).Append('"');
                sb.Append(",\"serial\":\"").Append(JsonEscape(serial)).Append('"');
                sb.Append(",\"sensDb\":");
                if (double.IsNaN(sens)) { sb.Append("null"); }
                else { sb.Append(sens.ToString("0.###", inv)); }
                sb.Append(",\"points\":").Append(cf.Length);
                sb.Append(",\"fmin\":").Append(cf[0].ToString("0.#", inv));
                sb.Append(",\"fmax\":").Append(cf[cf.Length - 1].ToString("0.#", inv));
                sb.Append(",\"persisted\":").Append(persist ? "true" : "false");
                sb.Append(",\"corr\":{\"20\":").Append(CalAt(cf, cd, 20.0).ToString("0.###", inv));
                sb.Append(",\"100\":").Append(CalAt(cf, cd, 100.0).ToString("0.###", inv));
                sb.Append(",\"1000\":").Append(CalAt(cf, cd, 1000.0).ToString("0.###", inv));
                sb.Append(",\"10000\":").Append(CalAt(cf, cd, 10000.0).ToString("0.###", inv));
                sb.Append("}}");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return "{\"ok\":false,\"error\":\"parse failed: " + JsonEscape(ex.Message) + "\"}";
            }
        }

        // Log-frequency linear interpolation, edge-clamped. Empty cal = 0.0 correction.
        static double CalAt(double[] cf, double[] cd, double f)
        {
            int n = cf.Length;
            if (n == 0) { return 0.0; }
            if (f <= cf[0]) { return cd[0]; }
            if (f >= cf[n - 1]) { return cd[n - 1]; }
            int lo = 0, hi = n - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (cf[mid] <= f) { lo = mid; } else { hi = mid; }
            }
            double t = (Math.Log(f) - Math.Log(cf[lo])) /
                       (Math.Log(cf[hi]) - Math.Log(cf[lo]));
            return cd[lo] + t * (cd[hi] - cd[lo]);
        }

        // Current cal state for /rta.json and GET /cal.
        static string CalInfoJson()
        {
            double[] cf; string name, serial;
            lock (_lock) { cf = _calF; name = _calName; serial = _calSerial; }
            System.Globalization.NumberFormatInfo inv =
                CultureInfo.InvariantCulture.NumberFormat;
            StringBuilder sb = new StringBuilder(256);
            sb.Append("\"loaded\":").Append(cf.Length > 0 ? "true" : "false");
            sb.Append(",\"file\":\"").Append(JsonEscape(name)).Append('"');
            sb.Append(",\"serial\":\"").Append(JsonEscape(serial)).Append('"');
            sb.Append(",\"points\":").Append(cf.Length);
            if (cf.Length > 0)
            {
                sb.Append(",\"fmin\":").Append(cf[0].ToString("0.#", inv));
                sb.Append(",\"fmax\":").Append(cf[cf.Length - 1].ToString("0.#", inv));
            }
            return sb.ToString();
        }

        // GET /devices - what winmm can capture from right now.
        static string DevicesJson()
        {
            uint n = waveInGetNumDevs();
            uint active = _devId;
            StringBuilder sb = new StringBuilder(1024);
            sb.Append("{\"ok\":true,\"active\":");
            if (active == 0xFFFFFFFF) { sb.Append("\"default\""); }
            else { sb.Append(active); }
            sb.Append(",\"devices\":[");
            bool first = true;
            for (uint d = 0; d < n; d++)
            {
                WaveInCaps c = new WaveInCaps();
                if (waveInGetDevCaps(d, ref c, Marshal.SizeOf(c)) != 0) { continue; }
                if (!first) { sb.Append(','); }
                first = false;
                sb.Append("{\"index\":").Append(d);
                sb.Append(",\"name\":\"").Append(JsonEscape(c.szPName)).Append('"');
                sb.Append(",\"ch\":").Append(c.wChannels);
                sb.Append(",\"inUse\":").Append(active == d ? "true" : "false");
                sb.Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        // GET /device?n=N (or "default") - flip the target; capture thread reopens.
        static string SwitchDeviceJson(string query)
        {
            string v = QueryVal(query, "n");
            uint target;
            if (v == "default") { target = 0xFFFFFFFF; }
            else if (v == null || !uint.TryParse(v, NumberStyles.Integer,
                     CultureInfo.InvariantCulture, out target))
            {
                return "{\"ok\":false,\"error\":\"need ?n=<index> or ?n=default\"}";
            }
            uint count = waveInGetNumDevs();
            if (target != 0xFFFFFFFF && target >= count)
            {
                return "{\"ok\":false,\"error\":\"no such device " + target + "\"}";
            }
            lock (_lock) { _status = "switching device..."; }
            _devId = target;
            _restartReq = true;   // capture loop breaks, Main reopens immediately
            return "{\"ok\":true,\"requested\":" +
                   (target == 0xFFFFFFFF ? "\"default\"" : target.ToString()) + "}";
        }

        static string QueryVal(string query, string key)
        {
            if (query == null) { return null; }
            string[] pairs = query.Split('&');
            for (int i = 0; i < pairs.Length; i++)
            {
                int eq = pairs[i].IndexOf('=');
                if (eq <= 0) { continue; }
                if (Uri.UnescapeDataString(pairs[i].Substring(0, eq)) != key) { continue; }
                return Uri.UnescapeDataString(pairs[i].Substring(eq + 1));
            }
            return null;
        }

        // The measurement mic, by the names these capture devices actually report through winmm
        // ("Microphone (UMIK-1)"). Returns 0xFFFFFFFF when none is present.
        static uint FindMeasurementMic()
        {
            uint n = waveInGetNumDevs();
            for (uint d = 0; d < n; d++)
            {
                WaveInCaps c = new WaveInCaps();
                if (waveInGetDevCaps(d, ref c, Marshal.SizeOf(c)) != 0) { continue; }
                string nm = c.szPName ?? "";
                if (nm.IndexOf("UMIK", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    nm.IndexOf("miniDSP", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    nm.IndexOf("measurement", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return d;
                }
            }
            return 0xFFFFFFFF;
        }

        static void Main(string[] args)
        {
            // Crash evidence first: a hidden .NET process that dies of an unhandled
            // exception leaves NOTHING behind (measured: vanished ~15 s into browser
            // polling, no console output, no event). Record the exception before dying.
            string crashLog = Path.Combine(Path.GetTempPath(), "rta-crash.log");
            AppDomain.CurrentDomain.UnhandledException += delegate (object sender, UnhandledExceptionEventArgs ee)
            {
                try
                {
                    File.AppendAllText(crashLog,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " +
                        ee.ExceptionObject + "\r\n\r\n");
                }
                catch { }
            };

            int rate = 48000;
            string outPath = null;
            int port = 8586;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--list")
                {
                    uint n = waveInGetNumDevs();
                    Console.WriteLine("capture devices: " + n);
                    for (uint d = 0; d < n; d++)
                    {
                        WaveInCaps c = new WaveInCaps();
                        if (waveInGetDevCaps(d, ref c, Marshal.SizeOf(c)) == 0)
                        {
                            Console.WriteLine("  [" + d + "] " + c.szPName
                                + "  (" + c.wChannels + " ch)");
                        }
                    }
                    return;
                }
                else if (args[i] == "--device" && i + 1 < args.Length)
                {
                    _devId = uint.Parse(args[++i], CultureInfo.InvariantCulture);
                }
                else if (args[i] == "--rate" && i + 1 < args.Length)
                {
                    rate = int.Parse(args[++i], CultureInfo.InvariantCulture);
                }
                else if (args[i] == "--port" && i + 1 < args.Length)
                {
                    port = int.Parse(args[++i], CultureInfo.InvariantCulture);
                }
                else if (args[i] == "--out" && i + 1 < args.Length)
                {
                    outPath = args[++i];
                }
            }

            // Never leave "which mic" to the WAVE_MAPPER. Opening 0xFFFFFFFF makes winmm pick an
            // endpoint and then refuse to name it - waveInGetID answers 0xFFFFFFFF, which winmm
            // calls "Microsoft Sound Mapper" (measured with var\probe-waveinid.cs, 2026-10-21). So a
            // page could not tell a UMIK from the laptop array by any means. Resolve the measurement
            // mic by NAME once, up front, and open that index explicitly: the name, the index and
            // the channel count then all describe the same device. With no mic present we fall back
            // to the mapper, which is deliberately anonymous - the page refuses to measure on it.
            if (_devId == 0xFFFFFFFF)
            {
                uint pick = FindMeasurementMic();
                if (pick != 0xFFFFFFFF)
                {
                    _devId = pick;
                    WaveInCaps pc = new WaveInCaps();
                    if (waveInGetDevCaps(pick, ref pc, Marshal.SizeOf(pc)) == 0)
                    {
                        Console.WriteLine("rta: using " + pc.szPName + " (device " + pick + ")");
                    }
                }
                else
                {
                    Console.WriteLine("rta: no measurement mic found (no UMIK/miniDSP capture device) "
                        + "- falling back to the WAVE_MAPPER, which cannot be identified");
                }
            }

            // Auto-load a previously uploaded calibration file, so the correction is
            // still applied after a restart ("the cal file is there" persists).
            try
            {
                string calPath = CalFilePath();
                if (File.Exists(calPath))
                {
                    string res = LoadCalText(File.ReadAllText(calPath), Path.GetFileName(calPath), false);
                    Console.WriteLine("rta: cal reload: " + res);
                }
            }
            catch (Exception ex) { Console.Error.WriteLine("rta: cal reload failed: " + ex.Message); }

            // HTTP server thread: serves the in-memory snapshot on GET /rta.json.
            // The capture thread NEVER touches the filesystem - measured: writing the
            // JSON into the OneDrive-synced web root 5x/s stalled the capture loop for
            // up to 2 s per sync (analysis dropped to ~3 Hz), which would corrupt a
            // real measurement. --out still exists for evidence capture, but it is
            // written by this server thread at 1 Hz, off the audio path.
            Thread server = new Thread(delegate () { Serve(port, outPath); });
            server.IsBackground = true;
            server.Start();

            // 2048 frames = 42 ms of wake cadence at 48k (the analysis gate rides on
            // buffer-done events; 4096 capped the rate near 85 ms/wake, measured).
            int bufFrames = 2048;
            int buffers = 6;

            while (_run)
            {
                try
                {
                    Capture(_devId, rate, bufFrames, buffers);
                }
                catch (Exception ex)
                {
                    lock (_lock) { _status = "error: " + ex.Message; }
                }
                if (_run)
                {
                    if (_restartReq)
                    {
                        // deliberate device switch: reopen on the new id immediately,
                        // no 2 s backoff (the old device was working fine).
                        _restartReq = false;
                        continue;
                    }
                    string cur;
                    lock (_lock) { cur = _status; }
                    if (cur == null || !cur.StartsWith("waveInOpen failed"))
                    {
                        lock (_lock) { _status = "reopening in 2s..."; }
                    }
                    Thread.Sleep(2000);
                }
            }
        }

        static void Capture(uint devId, int wantRate, int bufFrames, int numBufs)
        {
            int[] rates = (wantRate == 44100) ? new int[] { 44100, 48000 }
                                              : new int[] { 48000, 44100 };
            IntPtr hwi = IntPtr.Zero;
            int fs = 0;
            int channels = 0;
            int lastErr = 0;

            // Try 2-channel first (mics are usually stereo through the audio engine), then 1.
            for (int r = 0; r < rates.Length && hwi == IntPtr.Zero; r++)
            {
                for (int ch = 2; ch >= 1 && hwi == IntPtr.Zero; ch--)
                {
                    WaveFormatEx fmt = new WaveFormatEx();
                    fmt.wFormatTag = (ushort)WAVE_FORMAT_PCM;
                    fmt.nChannels = (ushort)ch;
                    fmt.nSamplesPerSec = (uint)rates[r];
                    fmt.wBitsPerSample = 16;
                    fmt.nBlockAlign = (ushort)(ch * 2);
                    fmt.nAvgBytesPerSec = (uint)(rates[r] * ch * 2);
                    fmt.cbSize = 0;
                    lastErr = waveInOpen(out hwi, devId, ref fmt, GetEventHandle(), IntPtr.Zero, CALLBACK_EVENT);
                    if (lastErr == 0) { fs = rates[r]; channels = ch; }
                }
            }
            if (hwi == IntPtr.Zero)
            {
                string msg = "waveInOpen failed (winmm err " + lastErr + ") - mic plugged in?";
                lock (_lock) { _status = msg; }
                Console.Error.WriteLine("rta: " + msg);
                return;
            }
            _fs = fs;
            _channels = channels;

            // Name the device from the HANDLE, not from what was asked for. waveInOpen(0xFFFFFFFF)
            // resolves the default itself and caps(0) is not necessarily its pick; a fixed index
            // can also have been renumbered by a USB replug before this reopen. Either way the old
            // code could publish a name describing a device it was not recording from - the exact
            // failure that let a whole eight-channel run record the laptop's own array.
            _openDevId = 0xFFFFFFFF;
            uint realId;
            if (waveInGetID(hwi, out realId) == 0) { _openDevId = realId; }
            if (_openDevId == 0xFFFFFFFF) { _openDevId = devId; }
            WaveInCaps caps = new WaveInCaps();
            if (_openDevId != 0xFFFFFFFF &&
                waveInGetDevCaps(_openDevId, ref caps, Marshal.SizeOf(caps)) == 0)
            {
                _deviceName = caps.szPName;
            }
            else
            {
                _deviceName = (devId == 0xFFFFFFFF) ? "system default" : ("device " + devId);
            }
            lock (_lock) { _status = "capturing"; }

            int bytesPerFrame = channels * 2;
            int bufBytes = bufFrames * bytesPerFrame;
            WaveHdr[] hdrs = new WaveHdr[numBufs];
            IntPtr[] bufs = new IntPtr[numBufs];
            // PIN the header array for the whole session. winmm keeps &hdrs[i] after
            // waveInAddBuffer returns and keeps writing into it (WHDR_DONE, byte counts,
            // and memcpy of captured PCM into the lpData it read from there) until the
            // buffer comes back - `ref` pins only for the call itself. Unpinned, a gen2
            // compaction moves the array and the driver dereferences stale memory: that
            // was the ucrtbase!memcpy AV (c0000005) that killed the daemon ~20 s into
            // HTTP polling, once the server's allocation rate made a compaction likely.
            GCHandle hdrPin = GCHandle.Alloc(hdrs, GCHandleType.Pinned);
            for (int i = 0; i < numBufs; i++)
            {
                bufs[i] = Marshal.AllocHGlobal(bufBytes);
                hdrs[i] = new WaveHdr();
                hdrs[i].lpData = bufs[i];
                hdrs[i].dwBufferLength = (uint)bufBytes;
                waveInPrepareHeader(hwi, ref hdrs[i], Marshal.SizeOf(typeof(WaveHdr)));
                waveInAddBuffer(hwi, ref hdrs[i], Marshal.SizeOf(typeof(WaveHdr)));
            }
            waveInStart(hwi);

            // FFT state: windowed overlap-add style analysis at Hop spacing.
            double[] window = new double[FftSize];
            for (int i = 0; i < FftSize; i++)
            {
                window[i] = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (FftSize - 1)));
            }
            double[] re = new double[FftSize];
            double[] im = new double[FftSize];
            double[] mag = new double[FftSize / 2];
            // Fixed ring of the NEWEST FftSize samples. A growable queue would add 4096
            // per input buffer but only retire 2048 per analysis, hit capacity and start
            // dropping fresh audio (measured: analysis stalled near 1 Hz, stale data).
            double[] ring = new double[FftSize];
            int ringLen = 0, ringPos = 0;
            double sampleAccum = 0.0;
            int sampleCount = 0;
            DateTime lastAnalysis = DateTime.UtcNow;
            DateTime lastDecay = DateTime.UtcNow;
            DateTime lastWake = DateTime.UtcNow;
            IntPtr evt = GetEventHandle();

            try
            {
                while (_run)
                {
                    if (_restartReq) { break; }   // device switch requested: exit, clean
                                                  // up (finally below), Main reopens _devId
                    if (WaitOne(evt, 300) == 258) { _loopTimeouts++; }   // WAIT_TIMEOUT
                    _loops++;
                    // ...and the wake cadence that the analysis gate rides on.
                    {
                        double dt = (DateTime.UtcNow - lastWake).TotalMilliseconds;
                        lastWake = DateTime.UtcNow;
                        _loopMsAvg = (_loops <= 1) ? dt
                            : _loopMsAvg + (dt - _loopMsAvg) / Math.Min(_loops, 100);
                    }
                for (int i = 0; i < numBufs; i++)
                {
                    if ((hdrs[i].dwFlags & WHDR_DONE) == 0) { continue; }
                    if ((int)hdrs[i].dwBytesRecorded <= 0) { continue; }

                    // mono downmix + RMS into pending ring
                    int n = (int)hdrs[i].dwBytesRecorded / bytesPerFrame;
                    unsafe
                    {
                        byte* p = (byte*)hdrs[i].lpData;
                        for (int s = 0; s < n; s++)
                        {
                            short l = (short)(p[s * bytesPerFrame] | (p[s * bytesPerFrame + 1] << 8));
                            double v = l / 32768.0;
                            if (channels == 2)
                            {
                                short rgt = (short)(p[s * bytesPerFrame + 2] | (p[s * bytesPerFrame + 3] << 8));
                                v = 0.5 * (v + rgt / 32768.0);
                            }
                            ring[ringPos] = v;
                            ringPos = (ringPos + 1) % FftSize;
                            if (ringLen < FftSize) { ringLen++; }
                            sampleAccum += v * v;
                            sampleCount++;
                        }
                    }
                    hdrs[i].dwBytesRecorded = 0;
                    // NEVER zero dwFlags here: WHDR_PREPARED lives in it. Clearing it made
                    // every re-AddBuffer return WAVERR_UNPREPARED (ignored), so capture died
                    // after the first 4 buffers - the 1 kHz tone test caught it.
                    int addErr = waveInAddBuffer(hwi, ref hdrs[i], Marshal.SizeOf(typeof(WaveHdr)));
                    if (addErr != 0)
                    {
                        throw new Exception("waveInAddBuffer failed (winmm " + addErr + ")");
                    }
                }

                // ~10 analyses per second. NOTE the real ceiling is the wake cadence
                // (buffer-done every 85 ms at 4096 frames/48k): a 100 ms gate here made
                // the effective period 170 ms (~6 Hz, measured). 80 ms = every wake.
                DateTime now = DateTime.UtcNow;
                if ((now - lastAnalysis).TotalMilliseconds >= 80 && ringLen >= FftSize)
                {
                    Analyze(ring, ringPos, re, im, mag, window, fs);
                    lastAnalysis = now;
                }

                if (sampleCount > 0 && (now - lastDecay).TotalMilliseconds >= 200)
                {
                    double rmsDb = 10.0 * Math.Log10(sampleAccum / sampleCount + 1e-12);
                    double secs = (now - lastDecay).TotalSeconds;
                    bool holding = _hold;
                    double decay = holding ? HoldDecayDbPerSec : PeakDecayDbPerSec;
                    lock (_lock)
                    {
                        _levelDb = rmsDb;
                        if (rmsDb > _peakDb) { _peakDb = rmsDb; }
                        else { _peakDb = Math.Max(rmsDb, _peakDb - decay * secs); }
                        for (int b = 0; b < NumBands; b++)
                        {
                            if (_bandDb[b] > _bandPeakDb[b]) { _bandPeakDb[b] = _bandDb[b]; }
                            else
                            {
                                _bandPeakDb[b] = Math.Max(_bandDb[b],
                                    _bandPeakDb[b] - decay * secs);
                            }
                        }
                    }
                    sampleAccum = 0.0;
                    sampleCount = 0;
                    lastDecay = now;
                }
            }   // end while (_run)
            }   // end try
            finally
            {
                // runs on both normal exit and the AddBuffer throw above - the caller
                // then reopens the device, so nothing leaks either way.
                waveInReset(hwi);
                for (int i = 0; i < numBufs; i++)
                {
                    waveInUnprepareHeader(hwi, ref hdrs[i], Marshal.SizeOf(typeof(WaveHdr)));
                    Marshal.FreeHGlobal(bufs[i]);
                }
                waveInClose(hwi);
                // release the pin LAST: unprepare above still dereferences the headers
                hdrPin.Free();
            }
        }

        // GET|POST /hold?on=1|0[&reset=1] - the freeze for sweeps.
        //
        // Peak hold decays 3 dB/s by default: right for a live room, useless for a 10 s sweep,
        // because the 20 Hz end would be ~30 dB down by the time the sweep reaches 20 kHz. With
        // hold on, the peaks only ever rise, so what is left behind IS the sweep's envelope.
        //
        // Hold never touches the live trace (bands[].db keeps moving). Only bands[].peak and the
        // broadband peakDb stop falling - and a reset clears those to -120 so a stray tone from
        // the last run cannot flatter the next one.
        static string HoldJson(string query)
        {
            bool didReset = false, changed = false;
            double pk;
            string onS = QueryVal(query, "on");
            string rS = QueryVal(query, "reset");
            lock (_lock)
            {
                if (!string.IsNullOrEmpty(onS))
                {
                    bool on = (onS == "1" || onS == "true" || onS == "on"
                               || onS == "yes" || onS == "TRUE");
                    if (on != _hold)
                    {
                        _hold = on;
                        _heldSinceUtc = on ? DateTime.UtcNow : DateTime.MinValue;
                        _holds++;
                        changed = true;
                    }
                }
                if (rS == "1" || rS == "true" || rS == "yes")
                {
                    for (int i = 0; i < NumBands; i++) { _bandPeakDb[i] = -120.0; }
                    _peakDb = -120.0;
                    // Held-since restarts with the clear: the counter below reports how long
                    // this held trace has been accumulating, which is what the reader wants.
                    _heldSinceUtc = _hold ? DateTime.UtcNow : DateTime.MinValue;
                    _resets++;
                    didReset = true;
                }
                pk = _peakDb;
            }
            return "{\"ok\":true,\"hold\":" + (_hold ? "true" : "false") +
                   ",\"changed\":" + (changed ? "true" : "false") +
                   ",\"reset\":" + (didReset ? "true" : "false") +
                   ",\"heldMs\":" + HeldMs().ToString("0", CultureInfo.InvariantCulture) +
                   ",\"decayDbPerSec\":" + DecayNowDbPerSec.ToString("0.0", CultureInfo.InvariantCulture) +
                   ",\"peakDb\":" + pk.ToString("0.0", CultureInfo.InvariantCulture) +
                   ",\"holds\":" + _holds + ",\"resets\":" + _resets + "}";
        }

        static double HeldMs()
        {
            if (!_hold || _heldSinceUtc == DateTime.MinValue) { return 0.0; }
            double ms = (DateTime.UtcNow - _heldSinceUtc).TotalMilliseconds;
            return ms < 0 ? 0.0 : ms;
        }

        // What the peaks are doing right now, for /rta.json - the UI can say "peak hold 3 dB/s"
        // or "frozen", and a sweep trace is distinguishable from a live one.
        static double DecayNowDbPerSec
        {
            get { return _hold ? HoldDecayDbPerSec : PeakDecayDbPerSec; }
        }

        static IntPtr _evt = IntPtr.Zero;
        static IntPtr GetEventHandle()
        {
            // auto-reset: one wake per SetEvent, and any completion we haven't scanned yet
            // keeps the event signaled, so no buffer-done is ever lost between scans.
            if (_evt == IntPtr.Zero) { _evt = CreateEvent(IntPtr.Zero, false, false, null); }
            return _evt;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr CreateEvent(IntPtr sec, bool manual, bool init, string name);

        [DllImport("kernel32.dll")]
        static extern int WaitForSingleObject(IntPtr h, int ms);

        static int WaitOne(IntPtr h, int ms) { return WaitForSingleObject(h, ms); }

        static void Analyze(double[] ring, int ringPos, double[] re, double[] im,
                            double[] mag, double[] window, int fs)
        {
            // ring is full: the oldest sample sits at ringPos, newest just before it.
            for (int i = 0; i < FftSize; i++)
            {
                int idx = (ringPos + i) % FftSize;
                re[i] = ring[idx] * window[i];
                im[i] = 0.0;
            }
            Fft(re, im);

            double binHz = (double)fs / FftSize;
            for (int i = 0; i < FftSize / 2; i++)
            {
                mag[i] = Math.Sqrt(re[i] * re[i] + im[i] * im[i]) / (FftSize / 4.0);
            }

            double[] outDb = new double[NumBands];
            double[] cf, cd;
            lock (_lock) { cf = _calF; cd = _calDb; }   // snapshot refs; upload swaps them
            for (int k = 0; k < NumBands; k++)
            {
                double c = BandCenter(k);
                if (c * Math.Pow(2.0, 1.0 / 24.0) > fs / 2.0) { outDb[k] = -120.0; continue; }
                double lo = c * Math.Pow(2.0, -1.0 / 48.0) / binHz;
                double hi = c * Math.Pow(2.0, 1.0 / 48.0) / binHz;
                int b0 = (int)Math.Floor(lo); if (b0 < 1) { b0 = 1; }
                int b1 = (int)Math.Ceiling(hi); if (b1 > FftSize / 2 - 1) { b1 = FftSize / 2 - 1; }
                double power = 0.0;
                int cnt = 0;
                for (int b = b0; b <= b1; b++) { power += mag[b] * mag[b]; cnt++; }
                double v = (cnt > 0) ? 10.0 * Math.Log10(power / cnt + 1e-14) : -120.0;
                v = Math.Max(-120.0, v);
                double corr = CalAt(cf, cd, c);         // 0.0 when no cal is loaded
                outDb[k] = Math.Max(-120.0, v + corr);  // REW convention: ADD the file's dB
            }

            lock (_lock)
            {
                for (int k = 0; k < NumBands; k++) { _bandDb[k] = outDb[k]; }
                _frames++;
            }
        }

        // In-place iterative radix-2 Cooley-Tukey. FftSize is a power of two.
        static void Fft(double[] re, double[] im)
        {
            int n = re.Length;
            int j = 0;
            for (int i = 1; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) { j ^= bit; }
                j |= bit;
                if (i < j)
                {
                    double t = re[i]; re[i] = re[j]; re[j] = t;
                    t = im[i]; im[i] = im[j]; im[j] = t;
                }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = -2.0 * Math.PI / len;
                double wr = Math.Cos(ang), wi = Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    double curR = 1.0, curI = 0.0;
                    for (int k = 0; k < len / 2; k++)
                    {
                        double ur = re[i + k], ui = im[i + k];
                        double vr = re[i + k + len / 2] * curR - im[i + k + len / 2] * curI;
                        double vi = re[i + k + len / 2] * curI + im[i + k + len / 2] * curR;
                        re[i + k] = ur + vr; im[i + k] = ui + vi;
                        re[i + k + len / 2] = ur - vr; im[i + k + len / 2] = ui - vi;
                        double nR = curR * wr - curI * wi;
                        curI = curR * wi + curI * wr;
                        curR = nR;
                    }
                }
            }
        }

        // The device list, checked as it stands NOW against the index winmm opened. winmm addresses
        // capture devices by INDEX and a replug renumbers them, so the handle can keep working
        // while its index starts naming somebody else. Returns "" when the index still means what
        // it meant at open, "gone" when it no longer exists, otherwise the name it means now.
        static string DriftNote(uint openIdx, string openedName)
        {
            if (openIdx == 0xFFFFFFFF) { return ""; }
            WaveInCaps c = new WaveInCaps();
            if (waveInGetDevCaps(openIdx, ref c, Marshal.SizeOf(c)) != 0) { return "gone"; }
            if (string.Equals(c.szPName, openedName, StringComparison.OrdinalIgnoreCase)) { return ""; }
            return c.szPName;
        }

        // Pure JSON snapshot of current state - no I/O here. The capture thread calls
        // this at 1 Hz for --out evidence files; the HTTP thread calls it per request.
        static string BuildJson()
        {
            double[] bands, peaks;
            double lvl, pk, frames, loopMs;
            long loops, timeouts;
            string dev, status;
            int fs, ch;
            uint openIdx;
            lock (_lock)
            {
                bands = (double[])_bandDb.Clone();
                peaks = (double[])_bandPeakDb.Clone();
                lvl = _levelDb; pk = _peakDb; frames = (double)_frames;
                dev = _deviceName; status = _status; fs = _fs;
                ch = _channels; openIdx = _openDevId;
                loopMs = _loopMsAvg; loops = _loops; timeouts = _loopTimeouts;
            }

            StringBuilder sb = new StringBuilder(4096);
            System.Globalization.NumberFormatInfo inv =
                CultureInfo.InvariantCulture.NumberFormat;
            sb.Append("{\"ok\":true,\"ts\":").Append(
                (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds);
            sb.Append(",\"device\":\"").Append(JsonEscape(dev)).Append('"');
            // The channel count is the one identity fact a device name cannot fake: the UMIK-1 is
            // mono, the laptop array is stereo. The page gates a run on it so "capturing" is never
            // mistaken for "capturing from the measurement mic".
            sb.Append(",\"ch\":").Append(ch);
            sb.Append(",\"devOpenIdx\":");
            if (openIdx == 0xFFFFFFFF) { sb.Append("\"unknown\""); } else { sb.Append(openIdx); }
            sb.Append(",\"devDrift\":\"").Append(JsonEscape(DriftNote(openIdx, dev))).Append('"');
            sb.Append(",\"devIdx\":");
            uint did = _devId;
            if (did == 0xFFFFFFFF) { sb.Append("\"default\""); } else { sb.Append(did); }
            sb.Append(",\"status\":\"").Append(JsonEscape(status)).Append('"');
            sb.Append(",\"fs\":").Append(fs);
            sb.Append(",\"fft\":").Append(FftSize);
            sb.Append(",\"frames\":").Append(frames.ToString("0", inv));
            sb.Append(",\"levelDb\":").Append(lvl.ToString("0.0", inv));
            sb.Append(",\"peakDb\":").Append(pk.ToString("0.0", inv));
            sb.Append(",\"hold\":").Append(_hold ? "true" : "false");
            sb.Append(",\"holdMs\":").Append(HeldMs().ToString("0", inv));
            sb.Append(",\"peakDecayDbPerSec\":").Append(DecayNowDbPerSec.ToString("0.0", inv));
            sb.Append(",\"loopMs\":").Append(loopMs.ToString("0", inv));
            sb.Append(",\"loops\":").Append(loops);
            sb.Append(",\"timeouts\":").Append(timeouts);
            sb.Append(",\"bands\":[");
            for (int k = 0; k < NumBands; k++)
            {
                if (k > 0) { sb.Append(','); }
                sb.Append("{\"f\":").Append(BandCenter(k).ToString("0.#", inv));
                sb.Append(",\"db\":").Append(bands[k].ToString("0.0", inv));
                sb.Append(",\"peak\":").Append(peaks[k].ToString("0.0", inv));
                sb.Append('}');
            }
            sb.Append("],\"cal\":{").Append(CalInfoJson()).Append("}}");
            return sb.ToString();
        }

        // Minimal HTTP server on a raw TcpListener. NOT HttpListener: that needs an
        // admin URL ACL (netsh http add urlacl) to bind http://+:8586/, while the
        // bridge UI on :8585 must be able to fetch cross-origin from any phone on the
        // LAN - so: CORS header, Connection: close, one request per accept. Serves
        // GET /rta.json (and "/" for a quick human check) from the in-memory string.
        static void Serve(int port, string outPath)
        {
            TcpListener listener = null;
            try
            {
                listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
            }
            catch (Exception ex)
            {
                lock (_lock) { _status = "listen failed: " + ex.Message; }
                Console.Error.WriteLine("rta: listen on " + port + " failed: " + ex.Message);
                return;
            }
            Console.WriteLine("rta: http://localhost:" + port + "/rta.json");

            DateTime lastOut = DateTime.MinValue;
            while (_run)
            {
                // --out evidence file: written HERE, never on the capture thread.
                if (outPath != null && (DateTime.UtcNow - lastOut).TotalSeconds >= 1.0)
                {
                    try { File.WriteAllText(outPath, BuildJson(), new UTF8Encoding(false)); }
                    catch { /* one missed sample is not worth a retry loop */ }
                    lastOut = DateTime.UtcNow;
                }

                while (listener.Pending())
                {
                    TcpClient client = null;
                    try
                    {
                        client = listener.AcceptTcpClient();
                        client.ReceiveTimeout = 2000;
                        client.SendTimeout = 2000;
                        HandleClient(client);
                    }
                    catch { /* client vanished mid-response - nothing to do */ }
                    finally { if (client != null) { try { client.Close(); } catch { } } }
                }
                Thread.Sleep(40);   // ~25 accept-checks/s; well under one poll interval
            }
            try { listener.Stop(); } catch { }
        }

        static void HandleClient(TcpClient client)
        {
            NetworkStream ns = client.GetStream();
            byte[] buf = new byte[4096];
            int got = ns.Read(buf, 0, buf.Length);
            if (got <= 0) { return; }
            string raw = Encoding.UTF8.GetString(buf, 0, got);
            int sep = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            string headText = (sep >= 0) ? raw.Substring(0, sep) : raw;
            int headBytes = (sep >= 0) ? sep + 4 : got;

            string[] lines = headText.Split('\r', '\n');
            string reqLine = (lines.Length > 0) ? lines[0] : "";
            string[] parts = reqLine.Split(' ');
            string method = (parts.Length > 0) ? parts[0] : "";
            string path = (parts.Length > 1) ? parts[1] : "/";

            // Full POST body (cal file is ~10-17 KB; first read only got a slice).
            int contentLen = 0;
            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(lines[i].Substring(15).Trim(), NumberStyles.Integer,
                                 CultureInfo.InvariantCulture, out contentLen);
                }
            }
            string bodyText = "";
            if (contentLen > 0)
            {
                int have = Math.Min(got - headBytes, contentLen);
                byte[] body = new byte[contentLen];
                Buffer.BlockCopy(buf, headBytes, body, 0, have < 0 ? 0 : have);
                int filled = have < 0 ? 0 : have;
                while (filled < contentLen)
                {
                    int r = ns.Read(body, filled, contentLen - filled);
                    if (r <= 0) { break; }
                    filled += r;
                }
                bodyText = Encoding.UTF8.GetString(body, 0, filled);
            }

            string q = "";
            int qi = path.IndexOf('?');
            if (qi >= 0) { q = path.Substring(qi + 1); path = path.Substring(0, qi); }

            string outJson;
            string status = "200 OK";

            if (method == "OPTIONS")          // CORS preflight, e.g. POST /cal
            {
                outJson = "";
                status = "204 No Content";
            }
            else if (path == "/rta.json") { outJson = BuildJson(); }
            else if (path == "/devices") { outJson = DevicesJson(); }
            else if (path == "/device") { outJson = SwitchDeviceJson(q); }
            else if (path == "/hold") { outJson = HoldJson(q); }
            else if (path == "/cal" && method == "POST")
            {
                string name = QueryVal(q, "name");
                if (string.IsNullOrEmpty(name)) { name = "uploaded-cal.txt"; }
                outJson = LoadCalText(bodyText, name, true);
                if (!outJson.Contains("\"ok\":true")) { status = "422 Unprocessable Entity"; }
            }
            else if (path == "/cal")
            {
                outJson = "{\"ok\":true," + CalInfoJson() + "}";
            }
            else if (path == "/")
            {
                outJson = "{\"service\":\"ds18-rta\",\"get\":[\"/rta.json\",\"/devices\",\"/cal\",\"/hold\"]," +
                          "\"post\":[\"/cal?name=<file>.txt\"]," +
                          "\"query\":{\"/device\":\"n=<index>|default\"," +
                          "\"/hold\":\"on=1|0\",\"/hold?reset=1\":\"clear the held peaks\"}}";
            }
            else
            {
                outJson = "{\"ok\":false,\"error\":\"not found\"}";
                status = "404 Not Found";
            }

            byte[] bodyOut = Encoding.UTF8.GetBytes(outJson);
            byte[] hdr = Encoding.ASCII.GetBytes(
                "HTTP/1.1 " + status + "\r\n" +
                "Content-Type: application/json\r\n" +
                "Content-Length: " + bodyOut.Length + "\r\n" +
                "Access-Control-Allow-Origin: *\r\n" +   // bridge UI on :8585 is cross-origin
                "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
                "Access-Control-Allow-Headers: Content-Type\r\n" +
                "Cache-Control: no-store\r\n" +
                "Connection: close\r\n\r\n");
            ns.Write(hdr, 0, hdr.Length);
            ns.Write(bodyOut, 0, bodyOut.Length);   // ALWAYS: Content-Length promised it, and a
                                                    // truncated response reads as a CORS/network
                                                    // error in the browser, not a clean 404
            ns.Flush();
        }

        static string JsonEscape(string s)
        {
            if (s == null) { return ""; }
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                    .Replace("\n", "\\n").Replace("\r", "\\r");
        }
    }
}
