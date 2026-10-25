using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace DreTunes
{
    public sealed class SwapProgress
    {
        public string Step = "";
        public int StepNo;
        public int StepTotal = 7;
        public string Message = "";
        public bool Final;
    }

    /// <summary>One swap, with everything it proved and everything it could not.</summary>
    public sealed class SwapResult
    {
        public TuneFile Target;
        public int Mem = -1;
        public bool Wrote;
        public string Verdict = "not attempted";
        public int Level = -1;                  // 1 verified, 0 unproven, -1 failed
        public string Detail = "";
        public string Caveat = "";
        public string SnapshotPath = "";
        public TuneDiff Diff;
        public MirrorDiff Mirror;
        public int BandAssertions;
        public DateTime At = DateTime.MinValue;
        public List<string> Log = new List<string>();
    }

    /// <summary>
    /// The snapshot writer. The shape is apply-tune.ps1's, byte for byte in the fields it reads,
    /// so a file this app writes can be handed to `apply-tune.ps1 -Restore` from a terminal.
    /// That is a deliberate compatibility contract, and the self-test checks the key names.
    /// </summary>
    public static class Snapshot
    {
        public static string Write(string root, int mem, string tag, Band[][] bands, bool[] bypass,
                                   double[] gain, double[] delay, bool[] mute, bool[] revert)
        {
            return WriteTo(Path.Combine(root, "evidence"), mem, tag, bands, bypass, gain, delay, mute, revert);
        }

        public static string WriteTo(string dir, int mem, string tag, Band[][] bands, bool[] bypass,
                                     double[] gain, double[] delay, bool[] mute, bool[] revert)
        {
            Dictionary<string, object> snap = new Dictionary<string, object>();
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            snap["at"] = DateTime.Now.ToString("s", CultureInfo.InvariantCulture);
            snap["tag"] = tag;
            snap["mem"] = mem;
            snap["mute"] = mute;
            snap["gainDb"] = gain;
            snap["delayMs"] = delay;
            snap["revert"] = revert;

            List<object> paths = new List<object>();
            for (int p = 0; p < Channels.Live; p++)
            {
                Dictionary<string, object> po = new Dictionary<string, object>();
                po["path"] = p;
                po["byPass"] = bypass != null && p < bypass.Length && bypass[p];
                List<object> list = new List<object>();
                if (bands != null && bands[p] != null)
                {
                    for (int i = 0; i < bands[p].Length; i++)
                    {
                        Dictionary<string, object> bo = new Dictionary<string, object>();
                        bo["fc"] = bands[p][i].Fc;
                        bo["gain"] = bands[p][i].Gain;
                        bo["q"] = bands[p][i].Q;
                        bo["enable"] = bands[p][i].Enable;
                        list.Add(bo);
                    }
                }
                po["bands"] = list;
                paths.Add(po);
            }
            snap["paths"] = paths;

            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "tune-before-" + stamp + ".json");
            int n = 0;
            while (File.Exists(path))
            {
                n++;
                path = Path.Combine(dir, "tune-before-" + stamp + "-" + n + ".json");
            }
            File.WriteAllText(path, Json.Write(snap), new UTF8Encoding(false));
            return path;
        }

        public static int Memory(string path)
        {
            Dictionary<string, object> d = Read(path);
            return d == null ? -1 : Json.I(d, "mem", -1);
        }

        public static Dictionary<string, object> Read(string path)
        {
            try { return Json.Obj(Json.Parse(File.ReadAllText(path, Encoding.UTF8))); }
            catch { return null; }
        }
    }

    /// <summary>
    /// Snapshot, write, read back, diff, verdict - the only place this app transmits anything.
    ///
    /// Every run is ordered the same way and every step is reported to the caller as it happens,
    /// because the honesty of the progress is part of the product: a swap that has written EQ but
    /// not yet read it back must not look like a finished swap. The engine never reports green
    /// for the command-11 block - this amplifier cannot echo it - so a swap is green only when
    /// the EQ readback matches the file, and the levels line always says "mirror".
    /// </summary>
    public sealed class SwapEngine
    {
        private readonly Rig _rig;
        private readonly string _root;

        public SwapEngine(Rig rig, string root) { _rig = rig; _root = root; }

        public SwapResult Apply(TuneFile target, int mem, bool overrideAck, Action<SwapProgress> report)
        {
            SwapResult r = new SwapResult();
            r.Target = target;
            r.Mem = mem;
            r.At = DateTime.Now;

            if (target == null) { Fail(r, report, "no tune selected"); return r; }

            // ---- 1. guard -------------------------------------------------------------
            Step(report, r, 1, "Guard", "resolving the playing memory from the device");
            Dictionary<string, object> status = null;
            try { status = _rig.Api.Get("/api/status", 4000); }
            catch (ApiException ex) { Fail(r, report, "the DSP link is not answering: " + ex.Message); return r; }

            bool statusOk = Json.B(status, "ok", false);
            int deviceMem = statusOk ? Json.I(status, "mem", -1) : -1;
            bool live = statusOk && deviceMem >= 0;
            GuardResult guard = Analysis.Guard(deviceMem, mem, live, overrideAck);
            r.Log.Add(Now() + " guard: " + guard.Reason);
            if (!guard.Allowed) { Fail(r, report, guard.Reason); return r; }

            TuneMemory fileMem = target.Mem(mem);
            if (fileMem == null) { Fail(r, report, target.FileName + " has no memory block " + mem); return r; }
            if (IsFlat(fileMem))
            {
                Fail(r, report, "the file's memory " + (mem + 1) + " block is flat: applying it would write " +
                                "31 zero-gain bands per path and silence the correction already in the car");
                return r;
            }
            if (!target.MemoriesIdentical)
                r.Log.Add(Now() + " note: " + target.FileName +
                          " does not repeat the same payload in all four memory blocks; using block " + mem);

            // ---- 2. snapshot ----------------------------------------------------------
            Step(report, r, 2, "Snapshot", "reading the car's current EQ, levels and delays");
            Band[][] before = new Band[Channels.Live][];
            bool[] bypass = new bool[Channels.Live];
            if (!ReadAll(mem, before, bypass, out r.SnapshotPath, false))
            {
                Fail(r, report, "could not read the current state, so there is nothing to revert to. " +
                                "Nothing was written.");
                return r;
            }

            string snap;
            try
            {
                snap = Snapshot.Write(_root, mem, "before-swap-" + target.Stem,
                                      before, bypass, _rig.Gain, _rig.Delay, _rig.OMute, _rig.ORevert);
            }
            catch (Exception ex)
            {
                Fail(r, report, "could not write the revert snapshot: " + ex.Message + ". Nothing was written.");
                return r;
            }
            r.SnapshotPath = snap;
            r.Log.Add(Now() + " snapshot: " + Path.GetFileName(snap));
            report(Progress(2, "Snapshot", "revert point saved: " + Path.GetFileName(snap)));

            // ---- 3. EQ ---------------------------------------------------------------
            Step(report, r, 3, "Write EQ", "writing 31 bands on each of the 8 live paths");
            int bands = 0;
            for (int p = 0; p < Channels.Live; p++)
            {
                TuneOutput o = fileMem.Out(p);
                if (o == null) { Fail(r, report, "the file has no output " + p); return r; }
                List<object> payload = new List<object>();
                for (int i = 0; i < 31; i++)
                {
                    Band b = (i < o.Bands.Count) ? o.Bands[i] : new Band(20, 0, 1);
                    Dictionary<string, object> bo = new Dictionary<string, object>();
                    bo["fc"] = b.Fc;
                    bo["gain"] = b.Gain;
                    bo["q"] = b.Q;
                    bo["enable"] = true;
                    bo["type"] = 0;
                    payload.Add(bo);
                    if (Math.Abs(b.Gain) > 0.0001 && b.Fc > 20 && i < o.Bands.Count) bands++;
                }
                Dictionary<string, object> body = Json.Body("mem", mem, "path", p, "byPass", false, "bands", payload);
                Dictionary<string, object> res;
                try { res = _rig.Api.Post("/api/eq", body, 9000); }
                catch (ApiException ex) { Fail(r, report, "path " + p + " EQ write failed: " + ex.Message); return r; }
                if (!Json.B(res, "ok", false))
                {
                    Fail(r, report, "path " + p + " EQ write refused: " + Json.S(res, "error", "no reason given"));
                    return r;
                }
                r.Wrote = true;
                report(Progress(3, "Write EQ", "path " + (p + 1) + " of 8 written, " +
                                              (bands) + " bands so far"));
            }
            r.BandAssertions = bands;

            // ---- 4. command 11 -------------------------------------------------------
            Step(report, r, 4, "Write levels", "sending the complete 16-entry output block (command 11)");
            double[] g = new double[Channels.Cmd11Paths];
            double[] dl = new double[Channels.Cmd11Paths];
            bool[] mu = new bool[Channels.Cmd11Paths];
            bool[] rv = new bool[Channels.Cmd11Paths];
            Array.Copy(_rig.Gain, g, Channels.Cmd11Paths);
            Array.Copy(_rig.Delay, dl, Channels.Cmd11Paths);
            Array.Copy(_rig.OMute, mu, Channels.Cmd11Paths);
            Array.Copy(_rig.ORevert, rv, Channels.Cmd11Paths);
            for (int p = 0; p < Channels.Live; p++)
            {
                TuneOutput o = fileMem.Out(p);
                if (o == null) continue;
                g[p] = o.Gain;
                dl[p] = o.Delay;
                mu[p] = o.Mute;
                rv[p] = o.Revert;
            }
            Dictionary<string, object> outBody = Json.Body(
                "mem", mem, "outPathCount", _rig.OutPathCount,
                "gainDb", g, "delayMs", dl, "mute", mu, "revert", rv);
            Dictionary<string, object> outRes;
            try { outRes = _rig.Api.Post("/api/output", outBody, 9000); }
            catch (ApiException ex) { Fail(r, report, "the output block was not accepted: " + ex.Message); return r; }
            bool outOk = Json.B(outRes, "ok", false);
            r.Log.Add(Now() + " cmd11: " + (outOk ? "sent" : "refused: " + Json.S(outRes, "error", "?")));
            if (!outOk)
            {
                // EQ landed, levels did not: that is a real, partial, audible state. Say so.
                r.Level = 0;
                r.Verdict = "EQ written, levels not";
                r.Detail = "The EQ for " + target.Stem + " is on the amplifier. The output block was " +
                           "refused: " + Json.S(outRes, "error", "no reason given") + ". Levels, delays " +
                           "and polarity are the previous tune's.";
                r.Caveat = Caveat();
                report(Progress(4, "Write levels", r.Detail));
                r.At = DateTime.Now;
                return r;
            }
            _rig.SetMirrorTouch();

            // ---- 5. read back --------------------------------------------------------
            Step(report, r, 5, "Read back", "reading all 8 paths back off the amplifier");
            Band[][] after = new Band[Channels.Live][];
            bool[] afterByPass = new bool[Channels.Live];
            string ignored;
            if (!ReadAll(mem, after, afterByPass, out ignored, true))
            {
                r.Level = 0;
                r.Verdict = "written, not verified";
                r.Detail = "The write was accepted but the readback failed, so nothing is proven. " +
                           "The previous state is in " + Path.GetFileName(snap) + ".";
                r.Caveat = Caveat();
                report(Progress(5, "Read back", r.Detail));
                r.At = DateTime.Now;
                return r;
            }

            // ---- 6. diff -------------------------------------------------------------
            Step(report, r, 6, "Diff", "comparing the readback to the file, band by band");
            r.Diff = Analysis.Diff(after, target, mem);

            // mirror comparison: the only thing that can be said about command 11
            Dictionary<string, object> info = null;
            try { info = _rig.Api.Get("/api/info", 4000); }
            catch (ApiException) { }
            double[] echoGain = info == null ? null : Json.DArr(info, "outputGainDb");
            double[] echoDelay = info == null ? null : Json.DArr(info, "outputDelayMs");
            bool[] echoMute = info == null ? null : Json.BArr(info, "outputMute");
            bool[] echoRevert = info == null ? null : Json.BArr(info, "outputRevert");
            r.Mirror = Analysis.Mirror(g, dl, mu, rv, echoGain, echoDelay, echoMute, echoRevert,
                                       Math.Max(1, _rig.OutPathCount));

            // ---- 7. verdict ----------------------------------------------------------
            Step(report, r, 7, "Verdict", "deciding what the evidence supports");
            if (r.Diff.Clean && r.Mirror.Clean)
            {
                r.Level = 1;
                r.Verdict = "verified";
                r.Detail = "EQ verified by readback on all 8 paths (" + r.Diff.PathsMatched +
                           " of " + r.Diff.Paths + ", " + r.BandAssertions +
                           " bands compared). Levels, delays and polarity were sent and are held by the " +
                           "bridge's mirror.";
            }
            else if (r.Diff.Clean)
            {
                r.Level = 0;
                r.Verdict = "EQ verified, levels unproven";
                r.Detail = "EQ verified by readback on all 8 paths. The output block could not be " +
                           "cross-checked against the mirror" +
                           (r.Mirror.Read ? " (" + r.Mirror.Diffs + " path(s) differ)" : " (the mirror was not readable)") +
                           ". Command 11 has no device readback on this amplifier.";
            }
            else
            {
                r.Level = -1;
                r.Verdict = "readback disagrees";
                r.Detail = Analysis.DiffSentence(r.Diff) + " " +
                           (r.Diff.Detail.Count > 0 ? string.Join("; ", r.Diff.Detail.ToArray()) : "");
            }
            r.Caveat = Caveat();
            r.At = DateTime.Now;
            report(Progress(7, "Verdict", r.Verdict + " - " + r.Detail));
            return r;
        }

        /// <summary>
        /// Restore one of the snapshots on disk, then verify it by reading the device back.
        ///
        /// apply-tune.ps1 -Restore writes and exits. This does the same writes and then proves
        /// them, because "restored" without a readback is the exact claim this product exists to
        /// stop making.
        /// </summary>
        public SwapResult Restore(string snapshotPath, bool overrideAck, Action<SwapProgress> report)
        {
            SwapResult r = new SwapResult();
            r.At = DateTime.Now;
            Dictionary<string, object> snap = Snapshot.Read(snapshotPath);
            if (snap == null) { Fail(r, report, "the snapshot could not be parsed: " + Path.GetFileName(snapshotPath)); return r; }
            int mem = Json.I(snap, "mem", -1);
            r.Mem = mem;
            r.Target = null;

            Step(report, r, 1, "Guard", "checking the snapshot's memory against the playing memory");
            Dictionary<string, object> status = null;
            try { status = _rig.Api.Get("/api/status", 4000); }
            catch (ApiException ex) { Fail(r, report, "the DSP link is not answering: " + ex.Message); return r; }
            bool statusOk = Json.B(status, "ok", false);
            int deviceMem = statusOk ? Json.I(status, "mem", -1) : -1;
            GuardResult guard = Analysis.Guard(deviceMem, mem, statusOk && deviceMem >= 0, overrideAck);
            r.Log.Add(Now() + " restore guard: " + guard.Reason);
            if (!guard.Allowed) { Fail(r, report, guard.Reason); return r; }

            // Read the current state first. Restoring is destructive too.
            Step(report, r, 2, "Snapshot", "saving the state this restore is about to replace");
            Band[][] before = new Band[Channels.Live][];
            bool[] bypass = new bool[Channels.Live];
            string ignoredPath;
            if (!ReadAll(mem, before, bypass, out ignoredPath, false))
            {
                Fail(r, report, "could not read the current state. Nothing was written.");
                return r;
            }
            try
            {
                r.SnapshotPath = Snapshot.Write(_root, mem, "before-restore",
                    before, bypass, _rig.Gain, _rig.Delay, _rig.OMute, _rig.ORevert);
                r.Log.Add(Now() + " snapshot: " + Path.GetFileName(r.SnapshotPath));
            }
            catch (Exception ex)
            {
                Fail(r, report, "could not write a safety snapshot: " + ex.Message + ". Nothing was written.");
                return r;
            }

            Step(report, r, 3, "Write EQ", "writing the snapshot's bands back to all 8 live paths");
            List<object> paths = Json.Arr(Json.Get(snap, "paths"));
            List<Band>[] want = new List<Band>[Channels.Live];
            if (paths == null) { Fail(r, report, "the snapshot has no paths array"); return r; }
            for (int i = 0; i < paths.Count; i++)
            {
                Dictionary<string, object> po = Json.Obj(paths[i]);
                if (po == null) continue;
                int p = Json.I(po, "path", i);
                if (p < 0 || p >= Channels.Live) continue;
                List<object> bandsArr = Json.Arr(Json.Get(po, "bands"));
                List<object> payload = new List<object>();
                List<Band> expected = new List<Band>();
                if (bandsArr != null)
                {
                    for (int k = 0; k < bandsArr.Count; k++)
                    {
                        Dictionary<string, object> bo = Json.Obj(bandsArr[k]);
                        if (bo == null) continue;
                        Band b = new Band(Json.D(bo, "fc", 20), Json.D(bo, "gain", 0), Json.D(bo, "q", 1));
                        b.Enable = Json.B(bo, "enable", true);
                        if (b.Enable && Math.Abs(b.Gain) > 0.001) expected.Add(b);
                        payload.Add(bo);
                    }
                }
                want[p] = expected;
                Dictionary<string, object> body = Json.Body("mem", mem, "path", p,
                    "byPass", Json.B(po, "byPass", false), "bands", payload);
                Dictionary<string, object> res;
                try { res = _rig.Api.Post("/api/eq", body, 9000); }
                catch (ApiException ex) { Fail(r, report, "path " + p + " restore failed: " + ex.Message); return r; }
                if (!Json.B(res, "ok", false))
                { Fail(r, report, "path " + p + " restore refused: " + Json.S(res, "error", "?")); return r; }
                r.Wrote = true;
                report(Progress(3, "Write EQ", "path " + (p + 1) + " of 8 restored"));
            }

            Step(report, r, 4, "Write levels", "sending the snapshot's complete output block");
            Dictionary<string, object> outBody = Json.Body(
                "mem", mem, "outPathCount", _rig.OutPathCount,
                "gainDb", Json.DArr(snap, "gainDb"), "delayMs", Json.DArr(snap, "delayMs"),
                "mute", Json.BArr(snap, "mute"), "revert", Json.BArr(snap, "revert"));
            Dictionary<string, object> outRes;
            try { outRes = _rig.Api.Post("/api/output", outBody, 9000); }
            catch (ApiException ex) { Fail(r, report, "output block refused: " + ex.Message); return r; }
            if (!Json.B(outRes, "ok", false))
            { Fail(r, report, "output block refused: " + Json.S(outRes, "error", "?")); return r; }
            _rig.SetMirrorTouch();

            Step(report, r, 5, "Read back", "reading all 8 paths back off the amplifier");
            Band[][] after = new Band[Channels.Live][];
            bool[] afterByPass = new bool[Channels.Live];
            string ignoredAfter;
            if (!ReadAll(mem, after, afterByPass, out ignoredAfter, true))
            { Fail(r, report, "the restore was written but the readback failed - nothing is proven"); return r; }

            Step(report, r, 6, "Diff", "comparing the readback to the snapshot");
            r.Diff = Analysis.DiffRaw(after, want);
            r.Level = r.Diff.Clean ? 1 : -1;
            r.Verdict = r.Diff.Clean ? "restored and verified" : "restored, readback disagrees";
            r.Detail = r.Diff.Clean
                ? "All 8 paths now hold the snapshot's bands (" + r.Diff.PathsMatched + " of 8 paths). " +
                  "Levels and delays were sent from the snapshot; they are mirror-only."
                : Analysis.DiffSentence(r.Diff);
            r.Caveat = Caveat();
            r.At = DateTime.Now;
            report(Progress(7, "Verdict", r.Verdict));
            return r;
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>
        /// Read paths 0..7. The `path` is stamped from the loop index because /api/eqread does not
        /// echo it - without that, every later comparison pairs path 0's data with every path.
        /// </summary>
        private bool ReadAll(int mem, Band[][] into, bool[] bypass, out string snapshotPath, bool mark)
        {
            snapshotPath = "";
            for (int p = 0; p < Channels.Live; p++)
            {
                Dictionary<string, object> d;
                try { d = _rig.Api.Post("/api/eqread", Json.Body("mem", mem, "path", p), 8000); }
                catch (ApiException) { return false; }
                if (!Json.B(d, "ok", false)) return false;
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
                into[p] = b;
                bypass[p] = Json.B(d, "byPass", false);
            }
            if (mark)
            {
                lock (_rig)
                {
                    _rig.Bands = into;
                    _rig.Bypass = bypass;
                    _rig.EqOk = true;
                    _rig.EqError = "";
                    _rig.EqAt = DateTime.Now;
                }
            }
            return true;
        }

        private static bool IsFlat(TuneMemory m)
        {
            int active = 0;
            for (int p = 0; p < Channels.Live; p++)
            {
                TuneOutput o = m.Out(p);
                if (o == null) continue;
                active += o.ActiveBands;
            }
            return active == 0;
        }

        private static string Caveat()
        {
            return "Levels, delays and polarity travel in command 11, which this amplifier cannot echo " +
                   "back. The bridge's mirror is what was cross-checked - not the device. EQ and " +
                   "crossovers are read back off the amplifier and are the only device-verified claims here.";
        }

        private static string Now()
        {
            return DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        }

        private static SwapProgress Progress(int n, string step, string msg)
        {
            SwapProgress p = new SwapProgress();
            p.StepNo = n;
            p.Step = step;
            p.Message = msg;
            return p;
        }

        private static void Step(Action<SwapProgress> report, SwapResult r, int n, string step, string msg)
        {
            r.Log.Add(Now() + " " + step + ": " + msg);
            if (report != null) report(Progress(n, step, msg));
        }

        private static void Fail(SwapResult r, Action<SwapProgress> report, string message)
        {
            r.Level = -1;
            r.Verdict = "not applied";
            r.Detail = message;
            r.At = DateTime.Now;
            r.Log.Add(Now() + " FAILED: " + message);
            if (report != null)
            {
                SwapProgress p = Progress(0, "Stopped", message);
                p.Final = true;
                report(p);
            }
        }
    }
}
