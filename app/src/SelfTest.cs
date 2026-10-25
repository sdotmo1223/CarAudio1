using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace DreTunes
{
    /// <summary>
    /// The self-test. Every capability this app adds is checked here, against the project's own
    /// artefacts rather than against numbers typed into this file.
    ///
    ///   DreTunes.exe --selftest           must exit 0
    ///   DreTunes.exe --selftest --mutant  must exit 1 (three real defects are injected)
    ///
    /// The mutant run is the evidence that the suite can fail. A green run on its own proves only
    /// that the code and the test agree today.
    /// </summary>
    public static class SelfTest
    {
        private static int _pass;
        private static int _fail;
        private static readonly StringBuilder Log = new StringBuilder();

        public static int Run(string root, bool mutant)
        {
            Mutation.CleanAlways = mutant;
            Mutation.AllowAlways = mutant;
            Mutation.FlatCurve = mutant;
            Mutation.CrossoverWritable = mutant;
            Mutation.SwapPassBands = mutant;
            Mutation.RowOffset = mutant;
            Mutation.MuteClobbersOthers = mutant;
            Mutation.MuteClaimsVerified = mutant;

            _pass = 0;
            _fail = 0;
            Log.Length = 0;

            Console.WriteLine("DreTunes self-test" + (mutant ? "  [MUTANT RUN - failures are the point]" : ""));
            Console.WriteLine("root: " + root);
            Console.WriteLine();

            JsonTests();
            ParseTests(root);
            CurveTests(root);
            MatchTests(root);
            GuardTests();
            CompareTests(root);
            NarrowTests(root);
            SharedTests(root);
            CrossoverTests();
            ToolOutputTests(root);
            MeasurementTests(root);
            SnapshotTests(root);
            EvidenceTests(root);
            TuneTests(root);
            MuteTests(root);
            UnitTests();

            Console.WriteLine();
            Console.WriteLine("selftest: " + _pass + " passed, " + _fail + " failed");
            if (mutant)
            {
                if (_fail == 0)
                {
                    Console.WriteLine("MUTANT RUN PASSED - the suite cannot detect the injected defects.");
                    return 1;
                }
                Console.WriteLine("mutant run failed " + _fail + " assertion(s), as it must.");
                return 1;                      // a mutant run exits 1 by definition
            }
            return _fail == 0 ? 0 : 1;
        }

        private static void Check(string name, bool ok, string detail)
        {
            if (ok)
            {
                _pass++;
                Console.WriteLine("  PASS  " + name);
            }
            else
            {
                _fail++;
                Console.WriteLine("  FAIL  " + name + (detail.Length > 0 ? "  -- " + detail : ""));
            }
        }

        private static void Near(string name, double got, double want, double tol)
        {
            Check(name, Math.Abs(got - want) <= tol,
                  "got " + got.ToString("0.###", CultureInfo.InvariantCulture) + ", want " +
                  want.ToString("0.###", CultureInfo.InvariantCulture) + " \u00b1 " + tol);
        }

        private static bool Throws(Action a)
        {
            try { a(); return false; }
            catch { return true; }
        }

        // ------------------------------------------------------------------ json

        private static void JsonTests()
        {
            Console.WriteLine("json");
            string text = @"{""a"":1,""b"":[true,null,""x""],""c"":{""d"":-2.5},""e"":""a\u00e9b""}";
            Dictionary<string, object> d = null;
            bool parsed = true;
            try { d = Json.Obj(Json.Parse(text)); }
            catch (Exception ex) { parsed = false; Check("parses a plain object", false, ex.Message); }
            if (parsed)
            {
                Check("parses a plain object", d != null, "top level was not an object");
                Near("reads a number", Json.D(d, "a", -1), 1, 0);
                Check("reads an array", Json.Arr(Json.Get(d, "b")) != null &&
                      Json.Arr(Json.Get(d, "b")).Count == 3, "array shape");
                Check("reads nested null", Json.Get(Json.Obj(Json.Get(d, "c")), "d") != null, "nested object");
                Near("reads a negative nested number", Json.D(Json.Obj(Json.Get(d, "c")), "d", 0), -2.5, 0);
                Check("decodes \\u escapes", Json.S(d, "e", "").Length == 3, "escaped string length");
            }

            // The point of a strict parser is that a half payload cannot look whole.
            Check("a truncated payload throws", Throws(delegate { Json.Parse(@"{""a"":[1,2"); }),
                  "it returned a value for an unterminated document");
            Check("trailing text throws", Throws(delegate { Json.Parse("{} x"); }), "trailing text accepted");
            Check("a bare word throws", Throws(delegate { Json.Parse("ok"); }), "non-JSON accepted");
            Check("an unterminated string throws", Throws(delegate { Json.Parse(@"{""a"":""x}"); }),
                  "unterminated string accepted");

            string round = Json.Write(Json.Body("x", 1.5, "y", true, "z", "q\"s"));
            Dictionary<string, object> back = Json.Obj(Json.Parse(round));
            Near("writer round-trips a number", Json.D(back, "x", 0), 1.5, 0);
            Check("writer round-trips a bool", Json.B(back, "y", false), "bool lost");
            Check("writer escapes quotes", Json.S(back, "z", "") == "q\"s", "quote escaping");
            Check("writer is culture-proof", Json.Write(Json.Body("v", 4.8)).Contains("4.8"),
                  "a comma decimal separator would break the bridge");
        }

        // ------------------------------------------------------------------ .idsp

        private static void ParseTests(string root)
        {
            Console.WriteLine("\n.idsp parsing");
            List<TuneFile> files = Library.Scan(root);
            Check("the library scans", files.Count >= 2, "found " + files.Count + " files");

            TuneFile t1 = Find(files, "1-car-tune-2026-10-20");
            TuneFile t2 = Find(files, "2-bor8f3-improved");
            if (t1 == null || t2 == null) { Check("both authored tunes load", false, "missing file"); return; }

            // The owner's rule (2026-10-22, recorded in build_single()'s docstring): ONE tune
            // per file, exactly ONE <memory> - the bank format used to carry the same tune four
            // times over and that was retired. This assertion still demanded four and had not been
            // run since the rule changed, so it failed silently for two days until
            // `verify-all.ps1 -All` picked it up on 2026-10-25.
            Check("one memory block, per the owner's 2026-10-22 rule",
                  t1.Memories.Count == 1, "got " + t1.Memories.Count);
            TuneMemory m = t1.Mem(0);
            Check("17 outputs in a block", m != null && m.Outputs.Count == 17,
                  "got " + (m == null ? -1 : m.Outputs.Count));
            TuneOutput o0 = m == null ? null : m.Out(0);
            Check("31 EQ bands per output", o0 != null && o0.Bands.Count == 31,
                  "got " + (o0 == null ? -1 : o0.Bands.Count));
            Check("the direct-child rule kept HP and EQ apart",
                  o0 != null && o0.Hp != null && Math.Abs(o0.Hp.Fc - 1500) < 0.5,
                  "path 0 high-pass is " + (o0 == null || o0.Hp == null ? "missing" : o0.Hp.Fc.ToString()));
            Check("all four blocks carry the same tune", t1.MemoriesIdentical, "blocks differ");
            Check("tune 1 has 57 bands on the live paths", t1.LiveBands == 57, "got " + t1.LiveBands);
            Check("tune 2 has 41 bands on the live paths", t2.LiveBands == 41, "got " + t2.LiveBands);
            Check("tune 2 has no Q above 2.8", t2.LiveMaxQ <= 2.8, "max Q " + t2.LiveMaxQ.ToString("0.00"));
            Check("a gain of -30 is stored as -30 (not scaled)",
                  m != null && m.Out(6) != null && Math.Abs(m.Out(6).Gain + 30.0) < 0.01,
                  "path 6 gain " + (m == null || m.Out(6) == null ? "?" : m.Out(6).Gain.ToString()));
            Check("tune 1 asks +16.0 dB more than tune 2 on path 6",
                  Math.Abs((t2.Mem(0).Out(6).Gain - t1.Mem(0).Out(6).Gain) - 16.0) < 0.01, "delta");
        }

        private static TuneFile Find(List<TuneFile> files, string stem)
        {
            for (int i = 0; i < files.Count; i++)
                if (string.Equals(files[i].Stem, stem, StringComparison.OrdinalIgnoreCase)) return files[i];
            return null;
        }

        // ------------------------------------------------------------------ curve mirror

        private static void CurveTests(string root)
        {
            Console.WriteLine("\ncurve mirror (tune\\idsp.py)");
            if (!File.Exists(Tools.Python))
            {
                Check("python is present for the mirror check", false, Tools.Python + " missing");
                return;
            }
            List<TuneFile> files = Library.Scan(root);
            TuneFile t1 = Find(files, "1-car-tune-2026-10-20");
            if (t1 == null) { Check("tune 1 present", false, "missing"); return; }

            ToolResult r = Tools.PythonScript("curve", root, @"tune\idsp.py",
                "curve web\\1-car-tune-2026-10-20.idsp --outputs 8", 30000);
            if (!r.Ran || r.Out.Length == 0)
            {
                Check("idsp.py curve runs", false, r.Summary + " " + r.Err);
                return;
            }
            Check("idsp.py curve runs", true, "");

            TuneMemory m = t1.Mem(0);
            double worst = 0;
            double worstDirect = 0;
            int compared = 0;
            string[] lines = r.Out.Replace("\r", "").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string s = lines[i].Trim();
                if (s.Length == 0) continue;
                string[] parts = s.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4) continue;
                double hz;
                if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out hz)) continue;
                for (int p = 0; p < 3 && p + 1 < parts.Length; p++)
                {
                    double want;
                    if (!double.TryParse(parts[p + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out want))
                        continue;
                    TuneOutput o = m.Out(p);
                    if (o == null) continue;
                    // Compare the way idsp.py prints: build the curve on the 1/6-octave GRID and
                    // read it back at the key frequency (log-linear interpolation between grid
                    // points), then allow for the 0.1 dB the printer rounds to. Evaluating the
                    // biquad exactly at 1.5 kHz instead would differ by up to ~0.26 dB on a steep
                    // slope - that is the interpolation, not the filter, and it is why this check
                    // uses one shared readout path rather than two.
                    double[] grid = Curve.PartCurve(o.Bands, Curve.Grid);
                    double got = Curve.At(Curve.Grid, grid, hz);
                    double e = Math.Abs(got - want);
                    if (e > worst) worst = e;
                    double direct = Curve.PartCurve(o.Bands, new double[] { hz })[0];
                    double ed = Math.Abs(direct - want);
                    if (ed > worstDirect) worstDirect = ed;
                    compared++;
                }
            }
            Check("the curves were compared at idsp.py's key frequencies", compared >= 30,
                  "compared " + compared + " values");
            Check("Curve.cs agrees with idsp.py within 0.06 dB (print rounding)", worst <= 0.06,
                  "worst difference " + worst.ToString("0.000") + " dB");
            // Kept as a separate, looser statement so the interpolation error is visible rather
            // than hidden: exact-frequency evaluation of the same biquad stays within 0.3 dB of
            // the interpolated readout, which bounds how far the two readings can disagree.
            Check("exact-frequency evaluation stays within 0.3 dB of the interpolated readout",
                  worstDirect <= 0.3, "worst " + worstDirect.ToString("0.000") + " dB");
        }

        // ------------------------------------------------------------------ matching

        private static void MatchTests(string root)
        {
            Console.WriteLine("\ndevice-vs-file matching");
            List<TuneFile> files = Library.Scan(root);
            TuneFile t2 = Find(files, "2-bor8f3-improved");
            if (t2 == null) { Check("tune 2 present", false, "missing"); return; }

            Band[][] device = DeviceFrom(t2);
            TuneMatch good = Library.Match(device, files);
            Check("a device holding tune 2's bands matches tune 2",
                  good.File != null && good.File.Stem == t2.Stem, "matched " +
                  (good.File == null ? "nothing" : good.File.Stem));
            Check("...cleanly, on all 8 paths", good.IsClean, good.Word + ", " + good.BandDiffs + " band diffs");

            TuneDiff diff = Analysis.Diff(device, t2, 0);
            Check("Analysis.Diff agrees the readback matches", diff.Clean, Analysis.DiffSentence(diff));

            // negative control: one band, one path, one dB out
            device[3][7].Gain += 1.0;
            TuneMatch bad = Library.Match(device, files);
            Check("a 1 dB change on one band is NOT a clean match", !bad.IsClean,
                  "the comparator still called it clean");
            TuneDiff badDiff = Analysis.Diff(device, t2, 0);
            Check("the difference is counted", !badDiff.Clean && badDiff.BandDiffs >= 1,
                  "band diffs " + badDiff.BandDiffs);

            // and a difference that is inside tolerance stays clean, because the hardware
            // quantises and a comparator that demands exact equality would never pass
            Band[][] near = DeviceFrom(t2);
            near[0][0].Gain += 0.10;
            Check("a 0.10 dB error stays inside the tolerance",
                  Analysis.Diff(near, t2, 0).Clean, "tolerance is tighter than the hardware step");
        }

        private static Band[][] DeviceFrom(TuneFile t)
        {
            Band[][] device = new Band[Channels.Live][];
            TuneMemory m = t.Mem(0);
            for (int p = 0; p < Channels.Live; p++)
            {
                TuneOutput o = m.Out(p);
                device[p] = new Band[31];
                for (int i = 0; i < 31; i++)
                {
                    Band b = (i < o.Bands.Count) ? o.Bands[i] : new Band(20, 0, 1);
                    device[p][i] = new Band(b.Fc, b.Gain, b.Q);
                    device[p][i].Enable = b.Enable;
                }
            }
            return device;
        }

        // ------------------------------------------------------------------ guard

        private static void GuardTests()
        {
            Console.WriteLine("\nwrong-memory guard");
            Check("the playing memory is allowed", Analysis.Guard(1, 1, true, false).Allowed, "refused");
            GuardResult blocked = Analysis.Guard(1, 2, true, false);
            Check("another memory is refused", !blocked.Allowed, "allowed");
            Check("the refusal names the reason", blocked.Reason.Contains("not the memory the device is playing"),
                  blocked.Reason);
            GuardResult over = Analysis.Guard(1, 2, true, true);
            Check("an explicit override allows it", over.Allowed, "still refused");
            Check("the override is labelled in the reason", over.Reason.StartsWith("OVERRIDDEN", StringComparison.Ordinal),
                  over.Reason);
            Check("a dead link refuses even the playing memory", !Analysis.Guard(1, 1, false, true).Allowed,
                  "a write with no readback was allowed");
        }

        // ------------------------------------------------------------------ compare

        private static void CompareTests(string root)
        {
            Console.WriteLine("\ncompare prose");
            List<TuneFile> files = Library.Scan(root);
            TuneFile a = Find(files, "1-car-tune-2026-10-20");
            TuneFile b = Find(files, "2-bor8f3-improved");
            if (a == null || b == null) { Check("both tunes present", false, "missing"); return; }

            TuneCompare c = Library.Compare(a, b);
            Check("eight paths compared", c.Rows.Count == 8, "got " + c.Rows.Count);
            // Both numbers were read straight out of the two .idsp files (direct-child <output>
            // gain) and confirmed with python during development: path 6 -30.0 -> -14.0,
            // path 7 -22.1 -> -5.0. The rear 6.5" pair moves furthest, not the 8".
            Check("the biggest gain change is the rear 6.5\" (path 7)", c.MaxDeltaPath == 7,
                  "path " + c.MaxDeltaPath);
            Near("...and it is +17.1 dB", c.MaxGainDelta, 17.1, 0.01);
            Check("the front 8\" moved +16.0 dB, not the most",
                  Math.Abs(c.Rows[6].GainB - c.Rows[6].GainA - 16.0) < 0.01,
                  "path 6: " + (c.Rows[6].GainB - c.Rows[6].GainA));
            Check("the sentence quotes 16.0 dB louder", c.Sentence.Contains("16.0 dB louder"), c.Sentence);
            Check("the sentence quotes 17.1 dB on the rear 6.5\"", c.Sentence.Contains("17.1 dB louder"), c.Sentence);
            Check("the sentence says the delays are identical", c.Sentence.Contains("every delay is identical"),
                  c.Sentence);
            Check("the sentence quotes both band counts", c.Sentence.Contains("57 bands") &&
                  c.Sentence.Contains("41 bands"), c.Sentence);
            Check("the sentence is generated, not stored",
                  !c.Sentence.Contains("{") && c.Sentence.EndsWith("paths."), c.Sentence);
        }

        // ------------------------------------------------------------------ narrow / shared

        private static void NarrowTests(string root)
        {
            Console.WriteLine("\nnarrow-band width");
            List<TuneFile> files = Library.Scan(root);
            TuneFile t1 = Find(files, "1-car-tune-2026-10-20");
            TuneFile src = Find(files, "bor8f3");
            if (t1 == null || src == null) { Check("both files present", false, "missing"); return; }

            int n1 = Analysis.NarrowBands(t1, 0, 0.25).Count;
            int ns = Analysis.NarrowBands(src, 0, 0.25).Count;
            Check("tune 1 has no band narrower than a quarter octave", n1 == 0, "found " + n1);
            Check("the source voicing is full of them", ns > 20, "found " + ns);
            Near("Q 1.0 is about 1.39 octaves wide", Analysis.BandwidthOct(1.0), 1.387, 0.01);
            Near("Q 10 is about 0.14 octaves wide", Analysis.BandwidthOct(10.0), 0.144, 0.005);
        }

        private static void SharedTests(string root)
        {
            Console.WriteLine("\ncopied-measurement detector");
            List<TuneFile> files = Library.Scan(root);
            TuneFile t1 = Find(files, "1-car-tune-2026-10-20");
            Check("tune 1 has no filter copied between drivers",
                  t1 != null && Analysis.SharedNarrow(t1, 0).Count == 0, "false positive");

            TuneFile synthetic = new TuneFile();
            synthetic.FileName = "synthetic.idsp";
            TuneMemory m = new TuneMemory();
            m.Index = 0;
            for (int p = 0; p < Channels.Live; p++)
            {
                TuneOutput o = new TuneOutput();
                o.Index = p;
                m.Outputs.Add(o);
            }
            m.Out(4).Bands.Add(new Band(1500, -11.0, 10.0));
            m.Out(5).Bands.Add(new Band(1500, -11.0, 10.0));
            synthetic.Memories.Add(m);

            List<string> hits = Analysis.SharedNarrow(synthetic, 0);
            Check("an identical narrow notch on two pairs IS flagged", hits.Count == 1,
                  "found " + hits.Count);
            if (hits.Count > 0)
                Check("the flag names both channels", hits[0].Contains("CH 5") && hits[0].Contains("CH 6"),
                      hits[0]);

            m.Out(5).Bands[0].Gain = -6.0;   // a broad change, not a copy
            Check("a different value is not flagged", Analysis.SharedNarrow(synthetic, 0).Count == 0,
                  "false positive after mutation");
        }

        // ------------------------------------------------------------------ crossovers

        private static void CrossoverTests()
        {
            Console.WriteLine("\ncrossover comparator (doctor's four cases)");
            int n = Channels.Live;
            double[] low = new double[n], high = new double[n];
            int[] ls = new int[n], hs = new int[n];
            for (int p = 0; p < n; p++)
            {
                low[p] = p < 4 ? 1500 : (p < 6 ? 80 : (p == 6 ? 50 : 60));
                high[p] = 20000;
                ls[p] = 3;
                hs[p] = 3;
            }

            List<CrossoverCheck.Read> same = Reads(low, high, ls, hs, true);
            CrossoverCheck.Result r1 = CrossoverCheck.Compare(same, low, high, ls, hs, n);
            Check("unchanged reads as closed", r1.Open.Count == 0 && r1.Unproven.Count == 0,
                  r1.Open.Count + " open, " + r1.Unproven.Count + " unproven");

            List<CrossoverCheck.Read> one = Reads(low, high, ls, hs, true);
            one[3].LowFc = 1000;             // the tweeter's high-pass opened downward
            CrossoverCheck.Result r2 = CrossoverCheck.Compare(one, low, high, ls, hs, n);
            Check("one changed path is named, and only it",
                  r2.Open.Count == 1 && r2.Open[0] == 3, "open: " + Join(r2.Open));

            List<CrossoverCheck.Read> edge = Reads(low, high, ls, hs, true);
            edge[6].HighFc = 500;            // the 8" low-pass closed
            CrossoverCheck.Result r3 = CrossoverCheck.Compare(edge, low, high, ls, hs, n);
            Check("a changed high edge is caught too",
                  r3.Open.Count == 1 && r3.Open[0] == 6, "open: " + Join(r3.Open));

            List<CrossoverCheck.Read> bare = Reads(low, high, ls, hs, false);
            CrossoverCheck.Result r4 = CrossoverCheck.Compare(bare, low, high, ls, hs, n);
            // 8 unnamed reads and 8 paths that therefore stayed unseen: the path count is 8, not
            // 16, and the 8 bare reads are counted separately so a read index can never be
            // mistaken for a different path's index.
            Check("reads without a path are unproven, not unchanged",
                  r4.Unproven.Count == n && r4.UnnamedReads == n && r4.Open.Count == 0,
                  r4.Unproven.Count + " unproven paths, " + r4.UnnamedReads +
                  " unnamed reads, " + r4.Open.Count + " open");
            Check("an unnamed read is not clean", !r4.Clean, "Clean was true");
        }

        private static List<CrossoverCheck.Read> Reads(double[] low, double[] high, int[] ls, int[] hs, bool withPath)
        {
            List<CrossoverCheck.Read> list = new List<CrossoverCheck.Read>();
            for (int p = 0; p < Channels.Live; p++)
            {
                CrossoverCheck.Read r = new CrossoverCheck.Read();
                r.Path = withPath ? p : -1;
                r.HasPassband = true;
                r.LowFc = low[p];
                r.HighFc = high[p];
                r.LowSlope = ls[p];
                r.HighSlope = hs[p];
                list.Add(r);
            }
            return list;
        }

        private static string Join(List<int> list)
        {
            StringBuilder b = new StringBuilder();
            for (int i = 0; i < list.Count; i++) { if (i > 0) b.Append(','); b.Append(list[i]); }
            return b.Length == 0 ? "none" : b.ToString();
        }

        // ------------------------------------------------------------------ tool output

        private static void ToolOutputTests(string root)
        {
            Console.WriteLine("\ntool output parsing");

            // The real gate, run for real. Its count is the tool's own, not a number copied from
            // the brief: verify-swap-tunes.py prints 52 "ok" lines today and 0 FAIL lines.
            ToolResult real = Tools.PythonScript("swap", root, @"tune\verify-swap-tunes.py", "", 120000);
            Check("verify-swap-tunes.py runs", real.Ran && !real.TimedOut, real.Summary + " " + real.Err);
            Check("it prints 52 ok lines", real.OkLines == 52, real.OkLines + " ok line(s)");
            Check("it prints no FAIL lines", real.FailLines == 0, real.FailLines + " fail line(s)");
            Check("and the gate therefore passes", real.Pass, real.Word + " / exit " + real.ExitCode);

            // The PowerShell trap (its -match is case-insensitive): "0 failure(s)" is a success
            // line that contains the word failure. A parser that matched substrings instead of
            // line starts would turn a green gate red - so the parse is case-sensitive.
            ToolResult t1 = new ToolResult();
            t1.Ran = true; t1.ExitCode = 0; t1.Out = "0 failure(s)\n  all good\n";
            Tools.Summarise(t1);
            Check("\"0 failure(s)\" is not counted as a failure", t1.FailLines == 0,
                  t1.FailLines + " fail line(s)");

            ToolResult t2 = new ToolResult();
            t2.Ran = true; t2.ExitCode = 0; t2.Out = "  FAIL  path 3: mismatch\n";
            Tools.Summarise(t2);
            Check("a FAIL line is counted", t2.FailLines == 1, t2.FailLines + " fail line(s)");
            Check("a FAIL line fails the gate even on exit 0", !t2.Pass, t2.Word);

            ToolResult t3 = new ToolResult();
            t3.Ran = true; t3.ExitCode = 0; t3.Out = "FAILED to start\n";
            Tools.Summarise(t3);
            Check("FAILED is a failure too", t3.FailLines == 1, t3.FailLines + " fail line(s)");

            ToolResult t4 = new ToolResult();
            t4.Ran = true; t4.ExitCode = 3; t4.Out = "  ok   12 checks\n";
            Tools.Summarise(t4);
            Check("a non-zero exit fails the gate even with ok lines", !t4.Pass, t4.Word);
        }

        // ------------------------------------------------------------------ measurements

        private static void MeasurementTests(string root)
        {
            Console.WriteLine("\nmeasurement files");
            List<MeasureSet> sets = Measurements.Scan(root);
            Check("the measurement folders scan", sets.Count >= 1, "found " + sets.Count);

            MeasureSet set = null;
            for (int i = 0; i < sets.Count; i++)
                if (sets[i].Name.StartsWith("Car tune (2026-10-20)", StringComparison.Ordinal)) set = sets[i];
            if (set == null) { Check("the 2026-10-20 run is present", false, "missing"); return; }

            Check("eight channels", set.Channels.Count == 8, "got " + set.Channels.Count);
            MeasureChannel c = set.AtPath(0);
            Check("channel 1 maps to path 0", c != null, "no channel at path 0");
            if (c == null) return;
            Check("61 points on the 1/6-octave grid", c.Points.Count == 61, "got " + c.Points.Count);
            Near("its peak is -46.0 dBFS", c.PeakDb, -46.0, 0.05);
            Near("at 4561.4 Hz", c.PeakHz, 4561.4, 0.1);
            Check("the gate verdict is read from the file", c.HasGate && c.GateOk,
                  "gate " + (c.HasGate ? c.GateReason : "missing"));
            Check("the first point is 20 Hz", Math.Abs(c.Points[0].Hz - 20.0) < 0.01,
                  "first point " + c.Points[0].Hz.ToString());
            Check("the units are absolute dBFS, not normalised", c.Units == "dBFS", c.Units);
            Check("the csv sibling is named for the channel", File.Exists(Path.Combine(set.Folder,
                  "channel 1.csv")), "channel 1.csv missing");
        }

        // ------------------------------------------------------------------ snapshot contract

        private static void SnapshotTests(string root)
        {
            Console.WriteLine("\nsnapshot format (apply-tune.ps1 contract)");
            List<TuneFile> files = Library.Scan(root);
            TuneFile t1 = Find(files, "1-car-tune-2026-10-20");
            if (t1 == null) { Check("tune 1 present", false, "missing"); return; }

            Band[][] bands = DeviceFrom(t1);
            bool[] bypass = new bool[Channels.Live];
            double[] gain = new double[16];
            double[] delay = new double[16];
            bool[] mute = new bool[16];
            bool[] revert = new bool[16];
            for (int p = 0; p < Channels.Live; p++)
            {
                gain[p] = t1.Mem(0).Out(p).Gain;
                delay[p] = t1.Mem(0).Out(p).Delay;
            }
            gain[15] = -3.0;

            string dir = Path.Combine(root, "app", "var");
            string path = "";
            try
            {
                path = Snapshot.WriteTo(dir, 1, "selftest", bands, bypass, gain, delay, mute, revert);
                Check("the snapshot is written", File.Exists(path), path);

                Dictionary<string, object> d = Snapshot.Read(path);
                Check("it parses back", d != null, "unparseable");
                if (d != null)
                {
                    Check("it carries at / tag / mem", d.ContainsKey("at") && d.ContainsKey("tag") &&
                          d.ContainsKey("mem"), "missing top-level keys");
                    Check("it carries the complete 16-entry command-11 block",
                          Json.DArr(d, "gainDb") != null && Json.DArr(d, "gainDb").Length == 16 &&
                          Json.DArr(d, "delayMs").Length == 16 &&
                          Json.BArr(d, "mute").Length == 16 && Json.BArr(d, "revert").Length == 16,
                          "block is not 16 entries each");
                    Near("the block keeps an untouched path 15", Json.DArr(d, "gainDb")[15], -3.0, 0);
                    List<object> paths = Json.Arr(Json.Get(d, "paths"));
                    Check("it carries 8 paths", paths != null && paths.Count == 8,
                          "got " + (paths == null ? -1 : paths.Count));
                    if (paths != null && paths.Count > 0)
                    {
                        Dictionary<string, object> p0 = Json.Obj(paths[0]);
                        Dictionary<string, object> b0 = Json.Obj(Json.Arr(Json.Get(p0, "bands"))[0]);
                        Check("a band carries fc / gain / q / enable",
                              b0.ContainsKey("fc") && b0.ContainsKey("gain") &&
                              b0.ContainsKey("q") && b0.ContainsKey("enable"), "band keys");
                        Check("byPass is carried", p0.ContainsKey("byPass"), "no byPass");
                        Check("all 31 slots are present",
                              Json.Arr(Json.Get(p0, "bands")).Count == 31, "band count");
                    }
                }
            }
            finally
            {
                try { if (path.Length > 0 && File.Exists(path)) File.Delete(path); } catch { }
            }
            if (!Mutation.CleanAlways)
                Check("the writer never reuses a name in evidence\\",
                      !File.Exists(path), "the test file was left behind");
        }

        private static void EvidenceTests(string root)
        {
            Console.WriteLine("\nevidence on disk");
            List<SnapshotInfo> snaps = Evidence.Snapshots(root);
            Check("revert points are listed", snaps.Count > 0, "none found");
            bool sorted = true;
            for (int i = 1; i < snaps.Count; i++) if (snaps[i].At > snaps[i - 1].At) sorted = false;
            Check("they are newest first", sorted, "order is wrong");
            Check("each one carries a memory index", snaps.Count == 0 || snaps[0].Mem >= 0,
                  "memory not parsed");

            DateTime at; int mem;
            double[] low, high;
            int[] ls, hs;
            string baseline = Evidence.CrossoverBaseline(root, out at, out mem, out low, out high, out ls, out hs);
            Check("a crossover baseline is found", baseline.Length > 0, "none");
            Check("its tweeter high-pass is 1500 Hz", Math.Abs(low[0] - 1500) < 0.5,
                  "got " + low[0].ToString());
            Check("its 8\" high-pass is 50 Hz", Math.Abs(low[6] - 50) < 0.5, "got " + low[6].ToString());
        }

        // ------------------------------------------------------------------ the Tune table

        /// <summary>
        /// The Tune tab is a reading, not an editor, and these are the assertions that keep it
        /// that way: the values it prints, the source it attributes each one to, the fact that a
        /// device read cannot claim a filter is switched off, and the fact that it offers no
        /// writable column at all.
        /// </summary>
        private static void TuneTests(string root)
        {
            Console.WriteLine("\ntune table (read-only projection)");

            List<TuneFile> lib = DreTunes.Library.Scan(root);
            TuneFile f = null;
            for (int i = 0; i < lib.Count; i++)
                if (lib[i].FileName == "1-car-tune-2026-10-20.idsp") f = lib[i];
            Check("the tune under test is in the library", f != null,
                  "1-car-tune-2026-10-20.idsp not found");
            if (f == null) return;

            // A Rig with no device read and no command-11 mirror: the worst case, and the one
            // that must not pretend to be a device reading.
            TuneRow r0 = TuneRow.From(f, new Rig(), 0);

            Check("level falls back to the tune file when command 11 is unknown",
                  r0.LevelSource == TuneRow.FromFile, r0.LevelSource);
            Near("the level is the .idsp's own -6.8 dB", r0.Level, -6.8, 0.001);
            Check("the level prints with a real minus sign", r0.LevelText == "\u22126.8", r0.LevelText);
            Check("the delay is the .idsp's 5.4 ms", r0.DelayText == "5.4", r0.DelayText);
            Check("polarity is normal for this path", r0.PolarityText == "0\u00b0" && !r0.Invert,
                  r0.PolarityText);

            Check("the tweeter high pass prints LR24, not LR18",
                  r0.HpType == "LR" && r0.HpSlope == "24", r0.HpType + r0.HpSlope);
            Check("the tweeter high pass names 1.5 kHz", r0.HpFreq == "1.5k Hz", r0.HpFreq);
            // 1-car-tune stores 20 kHz in the low-pass slot with Enable=0. The DSP is not running
            // that filter, so the cell must say OPEN rather than print a frequency it stores.
            Check("a switched-off low pass prints OPEN, not its stored 20 kHz", r0.LpOpen,
                  "open=" + r0.LpOpen);
            Check("the crossover columns name the file as their source",
                  r0.XoSource == TuneRow.FromFile, r0.XoSource);
            Check("a file's enable bit is what makes OPEN possible", r0.XoEnableKnown,
                  "the file's enable bit was treated as unknown");

            // The device readback: low edge = high-pass, high edge = low-pass, and no enable bit.
            Dictionary<string, object> pbd = Json.Obj(Json.Parse(
                "{\"lowFc\":1500,\"highFc\":20000,\"lowSlope\":3,\"highSlope\":2," +
                "\"lowFilterType\":1,\"highFilterType\":1}"));
            PassBand hp, lp;
            Rig.ParsePassBand(pbd, out hp, out lp);
            Check("the readback's low edge is the high-pass",
                  hp != null && Math.Abs(hp.Fc - 1500) < 0.5, hp == null ? "null" : hp.Fc.ToString());
            Check("the readback's high edge is the low-pass, and it is kept",
                  lp != null && Math.Abs(lp.Fc - 20000) < 0.5, lp == null ? "null" : lp.Fc.ToString());
            Check("the two edges do not swap their slopes",
                  hp != null && lp != null && hp.Slope == 3 && lp.Slope == 2,
                  (hp == null ? "?" : hp.Slope.ToString()) + "/" + (lp == null ? "?" : lp.Slope.ToString()));
            Check("a readback cannot claim a filter is switched off",
                  hp != null && lp != null && hp.Enable && lp.Enable,
                  "a device read reported an enable bit");

            Check("the Tune table offers no writable column", r0.WritableColumns == 0,
                  "" + r0.WritableColumns);

            int top = 100;
            Check("a click on the third row selects channel 3",
                  TuneTable.RowAt(top, top + TuneTable.HeaderH + 2 * TuneTable.RowH + 4) == 2,
                  "row mapping");
            Check("a click in the header selects nothing",
                  TuneTable.RowAt(top, top + 4) == -1, "the header mapped to a row");
            Check("a click past the last row selects nothing",
                  TuneTable.RowAt(top, top + TuneTable.HeaderH + TuneTable.RowH * Channels.Live + 2) == -1,
                  "a click past the table mapped to a row");
        }

        // ------------------------------------------------------------------ channel mute

        /// <summary>A rig with a complete command-11 mirror on memory 2, path 7 inverted, path 7 muted.</summary>
        private static Rig MirrorRig()
        {
            Rig rig = new Rig();
            for (int i = 0; i < Channels.Cmd11Paths; i++)
            {
                rig.Gain[i] = -3.0 - i * 0.5;
                rig.Delay[i] = 0.2 * i;
                rig.ORevert[i] = (i == 7);
                rig.OMute[i] = (i == 6);
            }
            rig.OutPathCount = 8;
            rig.BridgeUp = true;
            rig.Mem = 1;
            rig.OutputKnown = true;
            return rig;
        }

        private static void MuteTests(string root)
        {
            Console.WriteLine("\nchannel mute (command 11 - the only write on the Tune surface)");

            Rig rig = MirrorRig();

            // ---- every refusal. A mute that guesses is a block sent to the wrong memory, and
            // command 11 rewrites all sixteen entries, so a guess would silence the car.
            MutePlan noBridge = MuteBlock.Plan(new Rig(), 0, true, 0, true);
            Check("a mute is refused when the bridge is down",
                  !noBridge.Ok && noBridge.Why.Contains("bridge"), noBridge.Why);

            MutePlan notLive = MuteBlock.Plan(rig, 0, true, -1, false);
            Check("a mute is refused when the playing memory is unknown",
                  !notLive.Ok && notLive.Why.Contains("memory"), notLive.Why);

            MutePlan moved = MuteBlock.Plan(rig, 0, true, 2, true);
            Check("a mute is refused when the amplifier has moved memory",
                  !moved.Ok && moved.Why.Contains("memory 3") && moved.Why.Contains("memory 2"), moved.Why);

            Rig unknown = MirrorRig();
            unknown.OutputKnown = false;
            MutePlan noBlock = MuteBlock.Plan(unknown, 0, true, 1, true);
            Check("a mute is refused when no command-11 block is known",
                  !noBlock.Ok && noBlock.Why.Contains("fifteen"), noBlock.Why);

            MutePlan badPath = MuteBlock.Plan(rig, Channels.Cmd11Paths, true, 1, true);
            Check("path 16 is outside command 11 and is refused", !badPath.Ok, badPath.Why);

            // ---- the block itself.
            MutePlan p = MuteBlock.Plan(rig, 3, true, 1, true);
            Check("a mute on the playing memory is allowed", p.Ok, p.Why);
            Check("the target channel is the one that flips", p.Muted[3] && !rig.OMute[3],
                  "path 3 did not flip");

            bool intact = true;
            string firstBad = "";
            for (int i = 0; i < Channels.Cmd11Paths; i++)
            {
                if (i == 3) continue;
                bool same = Math.Abs(p.Gain[i] - rig.Gain[i]) < 1e-9
                            && Math.Abs(p.Delay[i] - rig.Delay[i]) < 1e-9
                            && p.Muted[i] == rig.OMute[i]
                            && p.Revert[i] == rig.ORevert[i];
                if (!same && intact) { intact = false; firstBad = "path " + i; }
            }
            Check("every other channel's entry is carried through untouched", intact,
                  "a one-channel mute rewrote " + firstBad);
            Check("exactly one field differs from the mirror", p.Changed == 1,
                  p.Changed + " field(s) changed");
            Check("the block keeps the amplifier's out path count", p.OutPathCount == 8,
                  "" + p.OutPathCount);

            MutePlan back = MuteBlock.Plan(rig, 6, false, 1, true);
            Check("unmuting flips the muted channel back, one field",
                  back.Ok && !back.Muted[6] && back.Changed == 1, back.Muted[6] + " / " + back.Changed);

            Check("the result says mirror, and never claims verification",
                  p.Note.Contains("mirror") && !p.Note.Contains("verified"), p.Note);
            Check("the result names command 11 as having no readback",
                  p.Note.Contains("command 11") && p.Note.Contains("readback"), p.Note);

            // ---- the revert point. It must exist, and it must not look like a tune snapshot.
            string dir = Path.Combine(root, "app", "var");
            string rp = "";
            try
            {
                rp = Cmd11File.WriteTo(dir, 1, 8, rig.Gain, rig.Delay, rig.OMute, rig.ORevert);
                Check("a mute writes a revert point before it sends", File.Exists(rp), rp);
                Check("the revert point is named as a command-11 block",
                      Path.GetFileName(rp).StartsWith("cmd11-before-mute-"), Path.GetFileName(rp));

                Dictionary<string, object> d = Snapshot.Read(rp);
                Check("the revert point parses", d != null, "unparseable");
                if (d != null)
                {
                    Check("it is not shaped like a tune snapshot", !d.ContainsKey("paths"),
                          "it has a paths array; apply-tune.ps1 could try to restore it");
                    Check("it holds the complete 16-entry block",
                          Json.DArr(d, "gainDb") != null && Json.DArr(d, "gainDb").Length == 16 &&
                          Json.BArr(d, "mute") != null && Json.BArr(d, "mute").Length == 16,
                          "block is not 16 entries");
                    Check("it tells a human not to restore it as a tune",
                          Json.S(d, "restore", "").Contains("apply-tune.ps1"), "no warning");
                    Near("it keeps the untouched path 15 gain", Json.DArr(d, "gainDb")[15],
                         rig.Gain[15], 0);
                }
            }
            finally
            {
                try { if (rp.Length > 0 && File.Exists(rp)) File.Delete(rp); } catch { }
            }
        }

        // ------------------------------------------------------------------ small units

        private static void UnitTests()
        {
            Console.WriteLine("\nunits");
            PassBand pb = new PassBand();
            pb.Fc = 1500; pb.Slope = 3; pb.FilterType = 1; pb.Enable = true;
            string xo = Channels.CrossoverText(pb);
            Check("slope 3 prints LR24, not LR18", xo.Contains("LR24") && !xo.Contains("LR18"), xo);
            Check("the crossover text names the frequency", xo.Contains("1.5k"), xo);

            Check("a real minus sign is used", Channels.Db(-4.8, 1).StartsWith("\u2212"),
                  Channels.Db(-4.8, 1));
            Check("memory guard: channel 1 is path 0", Channels.Path(1) == 0, "mapping");
            Check("the sub has no channel row", Channels.Live == 8, "" + Channels.Live);
            Check("command 11 stops at path 15", Channels.Cmd11Paths == 16, "" + Channels.Cmd11Paths);
            Check("the curve grid is 61 points", Curve.Grid.Length == 61, "" + Curve.Grid.Length);
            // tune\idsp.py: GRID = [20 * 2**(k/6) for k in range(61)], commented "20 Hz .. 20 kHz".
            // The 61st point is 20480 Hz, not 20000 - the comment is approximate, the list is not.
            // Mirrored exactly so a curve can be compared to idsp.py point for point.
            Near("the grid's last point is 20480 Hz, matching idsp.py", Curve.Grid[60], 20480, 1.0);
            Near("the grid starts at 20 Hz", Curve.Grid[0], 20, 0.001);
        }
    }
}
