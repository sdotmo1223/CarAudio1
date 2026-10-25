using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DreTunes
{
    /// <summary>
    /// The result of running one of the project's own tools.
    ///
    /// The app never reports a check it did not run. A ToolResult distinguishes "never ran"
    /// (At == MinValue) from "ran and failed", carries the tool's own output, and parses the
    /// tool's own counts rather than trusting a number typed into the source. The stale numbers
    /// in the brief (42 and 46 assertions) are exactly why: the library gate emits 52 today, and
    /// a hardcoded caption would have been a lie on the first screen.
    /// </summary>
    public sealed class ToolResult
    {
        public string Name = "";
        public string CommandLine = "";
        public bool Ran;
        public bool TimedOut;
        public int ExitCode = -1;
        public string Out = "";
        public string Err = "";
        public double Seconds;
        public DateTime At = DateTime.MinValue;
        public string Summary = "";
        public int OkLines;
        public int FailLines;

        public bool Pass
        {
            get { return Ran && !TimedOut && ExitCode == 0 && FailLines == 0; }
        }

        public bool Fresh(int seconds)
        {
            return Ran && (DateTime.Now - At).TotalSeconds <= seconds;
        }

        public string Word
        {
            get
            {
                if (!Ran) return "not run";
                if (TimedOut) return "timed out";
                if (FailLines > 0) return FailLines + " failed";
                if (ExitCode == 0) return "pass";
                return "exit " + ExitCode;
            }
        }
    }

    /// <summary>
    /// Child-process plumbing for the PowerShell and Python tooling that stays the source of
    /// truth. Runs with no shell, no window and a hard timeout; a hung doctor must not freeze a
    /// car app, so a timeout is reported as a timeout and never as a pass.
    /// </summary>
    public static class Tools
    {
        public const string Python = @"C:\Python315\python.exe";

        public static ToolResult Run(string name, string exe, string args, string cwd, int timeoutMs)
        {
            ToolResult r = new ToolResult();
            r.Name = name;
            r.CommandLine = exe + " " + args;
            r.At = DateTime.Now;
            r.Ran = true;
            Stopwatch sw = Stopwatch.StartNew();
            Process p = new Process();
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.WorkingDirectory = cwd;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
                p.StartInfo = psi;
                p.Start();

                Task<string> so = p.StandardOutput.ReadToEndAsync();
                Task<string> se = p.StandardError.ReadToEndAsync();

                if (!p.WaitForExit(timeoutMs))
                {
                    r.TimedOut = true;
                    try { p.Kill(); } catch { }
                    try { p.WaitForExit(2500); } catch { }
                }
                try { r.Out = so.Wait(2500) ? so.Result : ""; } catch { r.Out = ""; }
                try { r.Err = se.Wait(1500) ? se.Result : ""; } catch { r.Err = ""; }
                try { r.ExitCode = p.HasExited ? p.ExitCode : -1; } catch { r.ExitCode = -1; }
            }
            catch (Exception ex)
            {
                r.ExitCode = -1;
                r.Err = ex.Message;
                r.Summary = "could not start " + Path.GetFileName(exe) + ": " + ex.Message;
            }
            finally
            {
                try { p.Dispose(); } catch { }
            }
            r.Seconds = sw.Elapsed.TotalSeconds;
            if (r.Summary.Length == 0) r.Summary = Summarise(r);
            return r;
        }

        public static ToolResult PowerShell(string name, string root, string script, string args, int timeoutMs)
        {
            string path = Path.Combine(root, script);
            if (!File.Exists(path))
            {
                ToolResult missing = new ToolResult();
                missing.Name = name;
                missing.Ran = false;
                missing.Summary = script + " is not on disk";
                return missing;
            }
            string all = "-NoProfile -ExecutionPolicy Bypass -File \"" + path + "\"" +
                         (args.Length > 0 ? " " + args : "");
            return Run(name, "powershell.exe", all, root, timeoutMs);
        }

        public static ToolResult PythonScript(string name, string root, string script, string args, int timeoutMs)
        {
            string path = Path.Combine(root, script);
            if (!File.Exists(path))
            {
                ToolResult missing = new ToolResult();
                missing.Name = name;
                missing.Ran = false;
                missing.Summary = script + " is not on disk";
                return missing;
            }
            if (!File.Exists(Python))
            {
                ToolResult missing = new ToolResult();
                missing.Name = name;
                missing.Ran = false;
                missing.Summary = Python + " is not on disk";
                return missing;
            }
            return Run(name, Python, "\"" + path + "\"" + (args.Length > 0 ? " " + args : ""),
                       root, timeoutMs);
        }

        /// <summary>
        /// Counts the tool's own verdict lines. Case matters: PowerShell's -match is
        /// case-insensitive and once counted a final "0 failure(s)" as a failure. In C# the
        /// upper-case token only appears on a failing line, so it is used verbatim.
        /// </summary>
        /// <summary>
        /// Counts the verdict lines in a tool's own output. Public so the self-test can feed it
        /// known text: this parser is what turns "the tool printed something" into "the gate
        /// passed", so it is exactly the kind of code that must be seen to fail.
        /// </summary>
        public static string Summarise(ToolResult r)
        {
            string[] lines = (r.Out ?? "").Replace("\r", "").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string s = lines[i].TrimStart();
                if (s.StartsWith("ok ") || s.StartsWith("ok\t") || s.StartsWith("PASS ") ||
                    s.StartsWith("ok   ") || s.StartsWith("  ok   "))
                    r.OkLines++;
                if (s.StartsWith("FAIL ") || s.StartsWith("FAIL:") || s.StartsWith("FAILED"))
                    r.FailLines++;
            }
            if (r.TimedOut)
                return "timed out after " + r.Seconds.ToString("0.0") + " s - no verdict";
            if (r.FailLines > 0)
                return r.FailLines + " assertion(s) failed";
            if (r.OkLines > 0)
                return r.OkLines + " assertion(s) pass";
            if (r.ExitCode == 0) return "pass (" + r.Seconds.ToString("0.0") + " s)";
            return "exit " + r.ExitCode;
        }

        public static void Background(string name, Action work, Action<ToolResult> done)
        {
            Thread t = new Thread(delegate()
            {
                try { work(); }
                catch (Exception ex) { App.Log(name + ": " + ex.Message); }
            });
            t.IsBackground = true;
            t.Name = name;
            t.Start();
        }
    }

    // ---------------------------------------------------------------------- doctor

    public sealed class DoctorRow
    {
        public string Level = "info";
        public string Area = "";
        public string Msg = "";
    }

    /// <summary>
    /// doctor.ps1's own verdict, parsed rather than paraphrased. The app shows the tool's word
    /// ("READY WITH WARNINGS") next to its own live link state; when those disagree the
    /// disagreement is the most interesting thing on the screen, so it is shown, not smoothed.
    /// </summary>
    public sealed class DoctorReport
    {
        public string Verdict = "";
        public int Fails;
        public int Warns;
        public bool Dsp;
        public int Mem = -1;
        public DateTime At = DateTime.MinValue;
        public List<DoctorRow> Rows = new List<DoctorRow>();
        public string Raw = "";

        public static DoctorReport Parse(string json)
        {
            DoctorReport d = new DoctorReport();
            d.Raw = json ?? "";
            try
            {
                Dictionary<string, object> o = Json.Obj(Json.Parse(json));
                if (o == null) return d;
                d.Verdict = Json.S(o, "verdict", "");
                d.Fails = Json.I(o, "fails", 0);
                d.Warns = Json.I(o, "warns", 0);
                d.Dsp = Json.B(o, "dsp", false);
                object m = Json.Get(o, "mem");
                d.Mem = (m is double) ? (int)(double)m : -1;
                string at = Json.S(o, "at", "");
                DateTime when;
                if (DateTime.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.None, out when))
                    d.At = when;
                List<object> rows = Json.Arr(Json.Get(o, "rows"));
                if (rows != null)
                    for (int i = 0; i < rows.Count; i++)
                    {
                        Dictionary<string, object> ro = Json.Obj(rows[i]);
                        if (ro == null) continue;
                        DoctorRow row = new DoctorRow();
                        row.Level = Json.S(ro, "level", "info");
                        row.Area = Json.S(ro, "area", "");
                        row.Msg = Json.S(ro, "msg", "");
                        d.Rows.Add(row);
                    }
            }
            catch (Exception)
            {
                d.Verdict = "";
            }
            return d;
        }

        public bool Valid { get { return Verdict.Length > 0; } }
    }

    // ---------------------------------------------------------------------- evidence on disk

    public sealed class SnapshotInfo
    {
        public string Path;
        public string FileName;
        public DateTime At;
        public int Mem = -1;
        public string Tag = "";
        public long Bytes;
    }

    /// <summary>
    /// The revert points already on disk. Read-only and deliberately dumb: a snapshot is listed
    /// because a file exists, never because the app remembers taking one. Every destructive step
    /// in this project leaves one, and restoring one is itself verified by readback.
    /// </summary>
    public static class Evidence
    {
        public static List<SnapshotInfo> Snapshots(string root)
        {
            List<SnapshotInfo> list = new List<SnapshotInfo>();
            string dir = Path.Combine(root, "evidence");
            if (!Directory.Exists(dir)) return list;
            string[] files = Directory.GetFiles(dir, "tune-before-*.json");
            for (int i = 0; i < files.Length; i++)
            {
                SnapshotInfo s = new SnapshotInfo();
                s.Path = files[i];
                s.FileName = Path.GetFileName(files[i]);
                s.Bytes = new FileInfo(files[i]).Length;
                try
                {
                    Dictionary<string, object> d = Json.Obj(Json.Parse(File.ReadAllText(files[i])));
                    s.At = Measurements.ParseDate(Json.S(d, "at", ""));
                    s.Mem = Json.I(d, "mem", -1);
                    s.Tag = Json.S(d, "tag", "");
                }
                catch { }
                if (s.At == DateTime.MinValue)
                {
                    // Fall back to the name, which is stamped by the writer in local time.
                    string n = s.FileName.Replace("tune-before-", "").Replace(".json", "");
                    DateTime t;
                    if (DateTime.TryParseExact(n, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
                                               DateTimeStyles.None, out t))
                        s.At = t;
                }
                list.Add(s);
            }
            list.Sort(delegate(SnapshotInfo a, SnapshotInfo b) { return b.At.CompareTo(a.At); });
            return list;
        }

        /// <summary>The newest crossover snapshot and its per-path passband edges.</summary>
        public static string CrossoverBaseline(string root, out DateTime at, out int mem,
                                               out double[] lowFc, out double[] highFc,
                                               out int[] lowSlope, out int[] highSlope)
        {
            at = DateTime.MinValue; mem = -1;
            lowFc = new double[Channels.Live];
            highFc = new double[Channels.Live];
            lowSlope = new int[Channels.Live];
            highSlope = new int[Channels.Live];
            string dir = Path.Combine(root, "evidence");
            if (!Directory.Exists(dir)) return "";
            string[] files = Directory.GetFiles(dir, "crossover-snapshot*.json");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            if (files.Length == 0) return "";
            // Prefer the newest by content timestamp, not by name: the snapshot names are not
            // consistent (one has no stamp) and a name sort would pick the wrong baseline.
            string best = "";
            DateTime bestAt = DateTime.MinValue;
            for (int i = 0; i < files.Length; i++)
            {
                try
                {
                    Dictionary<string, object> d = Json.Obj(Json.Parse(File.ReadAllText(files[i])));
                    DateTime t = Measurements.ParseDate(Json.S(d, "at", ""));
                    if (best.Length == 0 || t > bestAt) { best = files[i]; bestAt = t; }
                }
                catch { }
            }
            if (best.Length == 0) return "";
            try
            {
                Dictionary<string, object> d = Json.Obj(Json.Parse(File.ReadAllText(best)));
                at = bestAt;
                mem = Json.I(d, "mem", -1);
                List<object> ps = Json.Arr(Json.Get(d, "paths"));
                if (ps != null)
                {
                    for (int i = 0; i < ps.Count; i++)
                    {
                        Dictionary<string, object> p = Json.Obj(ps[i]);
                        if (p == null) continue;
                        int path = Json.I(p, "path", i);
                        if (path < 0 || path >= Channels.Live) continue;
                        lowFc[path] = Json.D(p, "lowFc", 0);
                        highFc[path] = Json.D(p, "highFc", 0);
                        lowSlope[path] = Json.I(p, "lowSlope", 0);
                        highSlope[path] = Json.I(p, "highSlope", 0);
                    }
                }
            }
            catch { return ""; }
            return best;
        }
    }

    /// <summary>
    /// The crossover comparison, isolated so the self-test can prove it catches a change.
    ///
    /// Path identity is explicit and required. The doctor bug this replaces filtered reads on a
    /// `path` field that /api/eqread does not send, so seven of eight paths compared as
    /// "unchanged" - a comparator that failed open. Here a read without a path cannot be paired
    /// and therefore reports that path as unproven.
    /// </summary>
    public static class CrossoverCheck
    {
        public sealed class Read
        {
            public int Path = -1;         // -1 means the read did not say which path it is
            public bool HasPassband;
            public double LowFc, HighFc;
            public int LowSlope, HighSlope;
        }

        public sealed class Result
        {
            /// <summary>Paths whose crossover differs from the baseline. Path indexes.</summary>
            public List<int> Open = new List<int>();

            /// <summary>Paths the read did not prove either way. Path indexes, each once.</summary>
            public List<int> Unproven = new List<int>();

            /// <summary>
            /// Reads that did not say which path they belong to. They cannot prove anything, and
            /// they are kept apart from Unproven so a count of paths never double-counts a read.
            /// </summary>
            public int UnnamedReads;

            public List<string> Detail = new List<string>();

            public bool Clean { get { return Open.Count == 0 && Unproven.Count == 0 && UnnamedReads == 0; } }
        }

        public static Result Compare(IList<Read> reads, double[] baseLow, double[] baseHigh,
                                     int[] baseLowSlope, int[] baseHighSlope, int paths)
        {
            Result r = new Result();
            if (reads == null) return r;
            bool[] seen = new bool[Math.Max(1, paths)];
            for (int i = 0; i < reads.Count; i++)
            {
                Read read = reads[i];
                int p = read.Path;
                // A read that does not say which path it is cannot be paired with a baseline.
                // It is unproven - never "unchanged". This is the exact doctor bug (gotcha 5):
                // /api/eqread does not echo the path, so filtering on it matched nothing and the
                // check failed open for seven paths. Paths it might have covered fall out in the
                // "never seen" loop below, so nothing is silently skipped.
                if (p < 0)
                {
                    r.UnnamedReads++;
                    r.Detail.Add("read " + i + ": no path");
                    continue;
                }
                if (p >= paths) { r.Detail.Add("read " + i + ": path " + p + " out of range"); continue; }
                if (seen[p]) { r.Detail.Add("read " + i + ": path " + p + " read twice"); continue; }
                seen[p] = true;
                if (!read.HasPassband)
                {
                    r.Unproven.Add(p);
                    r.Detail.Add("path " + (p + 1) + ": no passband in the read");
                    continue;
                }
                bool changed = Math.Abs(read.LowFc - baseLow[p]) > 1.0 ||
                               Math.Abs(read.HighFc - baseHigh[p]) > 1.0 ||
                               read.LowSlope != baseLowSlope[p] ||
                               read.HighSlope != baseHighSlope[p];
                if (changed)
                {
                    r.Open.Add(p);
                    r.Detail.Add("path " + (p + 1) + ": HP " + Channels.Hz(baseLow[p]) + " -> " +
                                 Channels.Hz(read.LowFc) + ", LP " + Channels.Hz(baseHigh[p]) +
                                 " -> " + Channels.Hz(read.HighFc));
                }
            }
            for (int p = 0; p < paths; p++) if (!seen[p] && !r.Unproven.Contains(p)) r.Unproven.Add(p);
            return r;
        }
    }
}
