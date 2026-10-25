using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace DreTunes
{
    /// <summary>
    /// Everything one window knows, in one place.
    ///
    /// The split is deliberate: Rig owns the wire and the timestamps, AppState owns the
    /// selections, the long-running tool runs and the log, and the surfaces are pure paint. No
    /// surface starts a thread or sends a request itself - it asks AppState, which records who
    /// asked and when, and repaints when the answer arrives.
    /// </summary>
    public sealed class AppState
    {
        public readonly string Root;
        public readonly Rig Rig;
        public readonly SwapEngine Swap;

        public List<TuneFile> Library = new List<TuneFile>();
        public List<MeasureSet> Measures = new List<MeasureSet>();
        public DoctorReport Doctor = new DoctorReport();
        public List<SnapshotInfo> Snapshots = new List<SnapshotInfo>();

        public ToolResult DoctorRun = new ToolResult();
        public ToolResult DoctorSelfTest = new ToolResult();
        public ToolResult SwapGate = new ToolResult();
        public ToolResult WebGate = new ToolResult();
        public ToolResult DeviceCheck = new ToolResult();

        public bool DoctorRunning;
        public bool GatesRunning;
        public bool LibraryLoading;
        public bool Swapping;

        public SwapResult LastSwap;
        public string SwapStopped = "";

        public int Surface;                    // 0 now, 1 library, 2 measurements, 3 rig, 4 showcase
        public int LibraryTab;                 // 0 swap, 1 compare
        public int RigTab;                     // 0 readiness, 1 evidence
        public int Path = 0;
        public int TargetMem = -1;             // -1 = follow the playing memory
        public bool OverrideAck;
        public int SelectedTune = -1;
        public int CompareA = -1;
        public int CompareB = -1;
        public int MeasureSet = 0;
        public int MeasurePath = 0;
        public int ShowStep = 0;
        public bool TourPlaying;
        public DateTime TourNext = DateTime.MinValue;
        public bool RestoreArmed;
        public string RestoreTarget = "";
        public bool SubTicked;

        public readonly List<string> Log = new List<string>();
        public readonly List<SwapProgress> Progress = new List<SwapProgress>();

        public event Action Changed;

        public AppState(string root)
        {
            Root = root;
            Rig = new Rig();
            Rig.Root = root;
            Swap = new SwapEngine(Rig, root);
            Rig.Changed += Raise;
        }

        public void Raise()
        {
            Action h = Changed;
            if (h != null)
            {
                try { h(); }
                catch { }
            }
        }

        public void Note(string what)
        {
            LogLine(what);
            Raise();
        }

        public void LogLine(string what)
        {
            lock (Log)
            {
                Log.Add(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + what);
                while (Log.Count > 220) Log.RemoveAt(0);
            }
        }

        // ------------------------------------------------------------------ startup

        public void Start()
        {
            LogLine("DreTunes starting; root " + Root);
            Rig.Start();
            Background("library", delegate { LoadLibrary(); });
            Background("measurements", delegate { LoadMeasures(); });
        }

        public void LoadLibrary()
        {
            LibraryLoading = true;
            Raise();
            List<TuneFile> list = DreTunes.Library.Scan(Root);
            Library = list;
            if (SelectedTune < 0 && list.Count > 0) SelectedTune = 0;
            if (CompareA < 0 && list.Count > 0) CompareA = 0;
            if (CompareB < 0 && list.Count > 1) CompareB = 1;
            else if (CompareB < 0 && list.Count > 0) CompareB = 0;
            Snapshots = Evidence.Snapshots(Root);
            LibraryLoading = false;
            LogLine("library: " + list.Count + " tune file(s), " + Snapshots.Count + " revert point(s)");
            Raise();
        }

        public void LoadMeasures()
        {
            Measures = Measurements.Scan(Root);
            LogLine("measurements: " + Measures.Count + " run folder(s)");
            Raise();
        }

        public void Background(string name, Action work)
        {
            Thread t = new Thread(delegate()
            {
                try { work(); }
                catch (Exception ex)
                {
                    LogLine(name + " failed: " + ex.Message);
                    Raise();
                }
            });
            t.IsBackground = true;
            t.Name = name;
            t.Start();
        }

        // ------------------------------------------------------------------ tools

        public void RunDoctor()
        {
            if (DoctorRunning) return;
            DoctorRunning = true;
            LogLine("doctor.ps1 -Json: running");
            Raise();
            Background("doctor", delegate
            {
                ToolResult r = Tools.PowerShell("doctor", Root, "doctor.ps1", "-Json", 90000);
                DoctorRun = r;
                string json = r.Out;
                int brace = json.IndexOf('{');
                if (brace > 0) json = json.Substring(brace);
                DoctorReport d = DoctorReport.Parse(json);
                if (d.Valid) Doctor = d;
                LogLine("doctor: " + (d.Valid ? d.Verdict + " (" + d.Fails + " fail, " + d.Warns +
                                       " warn)" : r.Summary));
                DoctorRunning = false;
                Raise();
            });
        }

        public void RunDoctorSelfTest()
        {
            if (GatesRunning) return;
            GatesRunning = true;
            LogLine("doctor.ps1 -SelfTest: running the 4-case crossover comparator");
            Raise();
            Background("selftest", delegate
            {
                ToolResult r = Tools.PowerShell("doctor -SelfTest", Root, "doctor.ps1", "-SelfTest", 90000);
                DoctorSelfTest = r;
                LogLine("doctor selftest: " + r.Summary);
                GatesRunning = false;
                Raise();
            });
        }

        public void RunSwapGate()
        {
            if (GatesRunning) return;
            GatesRunning = true;
            LogLine("verify-swap-tunes.py: running the library integrity gate");
            Raise();
            Background("swapgate", delegate
            {
                ToolResult r = Tools.PythonScript("verify-swap-tunes", Root, @"tune\verify-swap-tunes.py",
                                                 "", 120000);
                SwapGate = r;
                LogLine("swap gate: " + r.Summary);
                GatesRunning = false;
                Raise();
            });
        }

        public void RunWebGate()
        {
            if (GatesRunning) return;
            GatesRunning = true;
            LogLine("verify-web.ps1: running the phone-page gate");
            Raise();
            Background("webgate", delegate
            {
                ToolResult r = Tools.PowerShell("verify-web", Root, "verify-web.ps1", "", 120000);
                WebGate = r;
                LogLine("web gate: " + r.Summary);
                GatesRunning = false;
                Raise();
            });
        }

        /// <summary>
        /// verify-device.py against the tune currently believed to be playing. Read-only, and the
        /// only check that talks to the amplifier itself; it is offered, never run behind the
        /// user's back, because it takes ~20 s over USB.
        /// </summary>
        public void RunDeviceCheck()
        {
            if (GatesRunning) return;
            if (!Rig.DeviceLive || Rig.Mem < 0)
            {
                Note("verify-device.py needs a live DSP and a known memory; it was not started");
                return;
            }
            int mem = Rig.Mem;
            string file = (LastSwap != null && LastSwap.Target != null)
                ? LastSwap.Target.FullPath
                : (SelectedTune >= 0 && SelectedTune < Library.Count ? Library[SelectedTune].FullPath : "");
            if (file.Length == 0) { Note("no tune file selected for the device check"); return; }
            GatesRunning = true;
            LogLine("verify-device.py " + System.IO.Path.GetFileName(file) + " --mem " + mem + ": running");
            Raise();
            Background("devicecheck", delegate
            {
                ToolResult r = Tools.PythonScript("verify-device", Root, @"tune\verify-device.py",
                                                 "\"" + file + "\" --mem " + mem, 120000);
                DeviceCheck = r;
                LogLine("device check: " + r.Summary);
                GatesRunning = false;
                Raise();
            });
        }

        // ------------------------------------------------------------------ swap

        public int ResolveTargetMem()
        {
            if (TargetMem >= 0) return TargetMem;
            return Rig.Mem;
        }

        public void ToggleOverride()
        {
            OverrideAck = !OverrideAck;
            if (OverrideAck)
            {
                LogLine("OVERRIDE ARMED: a write to a memory the device is not playing will be allowed once. " +
                        "Nothing audible changes when that write lands.");
                Note("Override armed for the next swap only.");
            }
            else
            {
                LogLine("override disarmed");
            }
        }

        public bool StartSwap(TuneFile target)
        {
            if (Swapping) return false;
            if (target == null) return false;
            int mem = ResolveTargetMem();
            if (mem < 0)
            {
                Note("The device has not reported which memory is playing; nothing was sent.");
                return false;
            }
            Swapping = true;
            Progress.Clear();
            SwapStopped = "";
            LastSwap = null;
            LogLine("swap: " + target.FileName + " -> memory " + (mem + 1) +
                    (OverrideAck ? " with the override armed" : ""));
            Raise();

            bool overrideNow = OverrideAck;
            if (overrideNow) OverrideAck = false;   // one use only, and it is already in the log

            Background("swap", delegate
            {
                SwapResult r = Swap.Apply(target, mem, overrideNow, delegate(SwapProgress p)
                {
                    lock (Progress) Progress.Add(p);
                    Raise();
                });
                LastSwap = r;
                if (!r.Wrote && r.Level < 0) SwapStopped = r.Detail;
                foreach (string line in r.Log) LogLine("  " + line);
                LogLine("swap verdict: " + r.Verdict);
                Rig.RequestEq();
                Swapping = false;
                Raise();
            });
            return true;
        }

        public void StartRestore(string snapshotPath)
        {
            if (Swapping || snapshotPath.Length == 0) return;
            Swapping = true;
            Progress.Clear();
            LastSwap = null;
            LogLine("restore: " + System.IO.Path.GetFileName(snapshotPath) +
                    (OverrideAck ? " with the override armed" : ""));
            Raise();
            bool overrideNow = OverrideAck;
            if (overrideNow) OverrideAck = false;
            Background("restore", delegate
            {
                SwapResult r = Swap.Restore(snapshotPath, overrideNow, delegate(SwapProgress p)
                {
                    lock (Progress) Progress.Add(p);
                    Raise();
                });
                LastSwap = r;
                foreach (string line in r.Log) LogLine("  " + line);
                LogLine("restore verdict: " + r.Verdict);
                Rig.RequestEq();
                Swapping = false;
                Raise();
            });
        }

        // ------------------------------------------------------------------ derived

        private TuneMatch _match;
        private DateTime _matchAt = DateTime.MinValue;

        /// <summary>
        /// Which file the device's EQ looks like, cached against the readback's timestamp. A
        /// change in that timestamp is the only thing that invalidates the answer, so a stale
        /// match cannot survive a fresh read.
        /// </summary>
        public TuneMatch Match
        {
            get
            {
                Band[][] b = Rig.Bands;
                if (b == null) return null;
                if (_match != null && _matchAt == Rig.EqAt && _match.File != null) return _match;
                _match = DreTunes.Library.Match(b, Library);
                _matchAt = Rig.EqAt;
                return _match;
            }
        }

        public TuneFile Selected
        {
            get { return (SelectedTune >= 0 && SelectedTune < Library.Count) ? Library[SelectedTune] : null; }
        }

        public TuneFile TuneA
        {
            get { return (CompareA >= 0 && CompareA < Library.Count) ? Library[CompareA] : null; }
        }

        public TuneFile TuneB
        {
            get { return (CompareB >= 0 && CompareB < Library.Count) ? Library[CompareB] : null; }
        }

        public MeasureSet Measure
        {
            get { return (MeasureSet >= 0 && MeasureSet < Measures.Count) ? Measures[MeasureSet] : null; }
        }

        public double[] DeviceCurve(int path)
        {
            Band[][] b = Rig.Bands;
            if (b == null || path < 0 || path >= b.Length || b[path] == null) return null;
            return Curve.PartCurve(b[path], Curve.Grid);
        }

        public double[] TuneCurve(TuneFile t, int path)
        {
            if (t == null) return null;
            TuneMemory m = t.Mem(0);
            TuneOutput o = m == null ? null : m.Out(path);
            if (o == null) return null;
            return Curve.PartCurve(o.Bands, Curve.Grid);
        }

        public void TourTick()
        {
            if (!TourPlaying) return;
            if (DateTime.Now < TourNext) return;
            if (ShowStep >= 5)
            {
                TourPlaying = false;
                Raise();
                return;
            }
            ShowStep++;
            TourNext = DateTime.Now.AddSeconds(14);
            Raise();
        }
    }
}
