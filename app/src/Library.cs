using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace DreTunes
{
    /// <summary>What the device's EQ readback looks like next to a tune file.</summary>
    public sealed class TuneMatch
    {
        public TuneFile File;
        public int PathsMatched;
        public int PathsCompared;
        public int BandDiffs;
        public List<string> Detail = new List<string>();
        public DateTime At = DateTime.MinValue;

        public bool IsClean
        {
            get { return File != null && PathsCompared > 0 && PathsMatched == PathsCompared && BandDiffs == 0; }
        }

        public string Word
        {
            get
            {
                if (File == null) return "unmatched";
                if (IsClean) return "matches";
                if (PathsMatched > 0) return "part matches";
                return "does not match";
            }
        }
    }

    public sealed class PathDelta
    {
        public int Path;
        public double GainA, GainB;
        public double DelayA, DelayB;
        public int BandsA, BandsB;
    }

    public sealed class TuneCompare
    {
        public TuneFile A, B;
        public List<PathDelta> Rows = new List<PathDelta>();
        public string Sentence = "";
        public double MaxGainDelta;
        public int MaxDeltaPath = -1;
        public bool DelaysIdentical = true;
    }

    /// <summary>
    /// The tune library: every .idsp in web\, read-only.
    ///
    /// The listing deliberately keeps inherited files - they are part of the rig's history and
    /// hiding them would misrepresent what is on disk - but every one of them is labelled. Both
    /// matching and comparing work from the parsed file, never from its name.
    /// </summary>
    public static class Library
    {
        public static List<TuneFile> Scan(string root)
        {
            List<TuneFile> list = new List<TuneFile>();
            string web = Path.Combine(root, "web");
            if (!Directory.Exists(web)) return list;
            string[] files = Directory.GetFiles(web, "*.idsp");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < files.Length; i++)
            {
                try { list.Add(TuneFile.Load(files[i])); }
                catch { /* an unreadable file must not hide the rest */ }
            }
            // Ours first, then alphabetical: the two files this project authored are the two a
            // person is actually choosing between.
            list.Sort(delegate(TuneFile a, TuneFile b)
            {
                bool aa = ProvenanceTable.Of(a.FileName).Authored;
                bool ba = ProvenanceTable.Of(b.FileName).Authored;
                if (aa != ba) return aa ? -1 : 1;
                return string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }

        /// <summary>
        /// Which file does the device's EQ look like?
        ///
        /// This is the app's answer to "what am I hearing", and it is a comparison, not a lookup
        /// of the name the bridge remembers. Bands are paired on centre frequency within the same
        /// tolerances tune\verify-device.py uses (Fc ±1 Hz, gain ±0.15 dB, Q ±0.06); a band the
        /// device holds that the file does not is a difference, and vice versa.
        /// </summary>
        public static TuneMatch Match(Band[][] device, List<TuneFile> files)
        {
            TuneMatch best = new TuneMatch();
            best.PathsCompared = Channels.Live;
            best.At = DateTime.Now;
            if (device == null || files == null || files.Count == 0) return best;

            int bestScore = int.MaxValue;
            for (int f = 0; f < files.Count; f++)
            {
                TuneFile t = files[f];
                TuneMemory mem = t.Mem(0);
                if (mem == null) continue;
                int matched = 0, diffs = 0;
                List<string> detail = new List<string>();
                for (int p = 0; p < Channels.Live; p++)
                {
                    TuneOutput o = mem.Out(p);
                    if (o == null || device[p] == null) continue;
                    List<Band> want = new List<Band>();
                    for (int i = 0; i < o.Bands.Count; i++) if (o.Bands[i].IsActive) want.Add(o.Bands[i]);
                    List<Band> have = new List<Band>();
                    for (int i = 0; i < device[p].Length; i++)
                    {
                        Band b = device[p][i];
                        if (b.Enable && Math.Abs(b.Gain) > 0.001) have.Add(b);
                    }
                    int before = want.Count;
                    int bad = 0;
                    for (int i = want.Count - 1; i >= 0; i--)
                    {
                        int hit = -1;
                        for (int k = 0; k < have.Count; k++)
                        {
                            if (Math.Abs(have[k].Fc - want[i].Fc) <= 1.0 &&
                                Math.Abs(have[k].Gain - want[i].Gain) <= 0.15 &&
                                Math.Abs(have[k].Q - want[i].Q) <= 0.06) { hit = k; break; }
                        }
                        if (hit >= 0) { have.RemoveAt(hit); want.RemoveAt(i); }
                    }
                    bad = want.Count + have.Count;
                    diffs += bad;
                    if (bad == 0 && before >= 0) matched++;
                    else if (bad > 0 && detail.Count < 6)
                        detail.Add("path " + p + ": " + bad + " band difference(s)");
                }
                // Fewest differences wins; ties go to the file that covers more paths with bands.
                int score = diffs * 100 - matched;
                if (score < bestScore)
                {
                    bestScore = score;
                    best.File = t;
                    best.PathsMatched = matched;
                    best.BandDiffs = diffs;
                    best.Detail = detail;
                }
            }
            return best;
        }

        /// <summary>Per-path gain / delay / band-count deltas plus one sentence of plain English.</summary>
        public static TuneCompare Compare(TuneFile a, TuneFile b)
        {
            TuneCompare c = new TuneCompare();
            c.A = a;
            c.B = b;
            if (a == null || b == null) return c;
            TuneMemory ma = a.Mem(0), mb = b.Mem(0);
            if (ma == null || mb == null) return c;

            for (int p = 0; p < Channels.Live; p++)
            {
                TuneOutput oa = ma.Out(p), ob = mb.Out(p);
                if (oa == null || ob == null) continue;
                PathDelta d = new PathDelta();
                d.Path = p;
                d.GainA = oa.Gain;
                d.GainB = ob.Gain;
                d.DelayA = oa.Delay;
                d.DelayB = ob.Delay;
                d.BandsA = oa.ActiveBands;
                d.BandsB = ob.ActiveBands;
                c.Rows.Add(d);
                if (Math.Abs(d.DelayA - d.DelayB) > 0.05) c.DelaysIdentical = false;
            }
            c.Sentence = Sentence(c);
            return c;
        }

        /// <summary>
        /// The comparison in words. Generated from the numbers, never typed by hand, so it cannot
        /// survive a change to the files and still say what it said before.
        /// </summary>
        private static string Sentence(TuneCompare c)
        {
            if (c.Rows.Count == 0) return "No comparable paths.";
            PathDelta biggest = null;
            double biggestAbs = -1;
            for (int i = 0; i < c.Rows.Count; i++)
            {
                double d = Math.Abs(c.Rows[i].GainB - c.Rows[i].GainA);
                if (d > biggestAbs) { biggestAbs = d; biggest = c.Rows[i]; }
            }
            c.MaxGainDelta = (biggest == null) ? 0 : biggest.GainB - biggest.GainA;
            c.MaxDeltaPath = biggest == null ? -1 : biggest.Path;

            if (biggest == null || biggestAbs < 0.05)
                return "These two tunes set the same levels on all eight live paths; only the EQ shape differs.";

            string dir = c.MaxGainDelta > 0 ? "louder" : "quieter";
            string s = c.B.Stem + " sits " + Math.Abs(c.MaxGainDelta).ToString("0.0") +
                       " dB " + dir + " than " + c.A.Stem + " on the " +
                       Channels.Driver[biggest.Path] + " (path " + biggest.Path + ")";

            // A second path only earns a clause when it is a real change someone would hear.
            PathDelta second = null;
            double secondAbs = 4.9;
            for (int i = 0; i < c.Rows.Count; i++)
            {
                if (c.Rows[i].Path == biggest.Path) continue;
                double d = Math.Abs(c.Rows[i].GainB - c.Rows[i].GainA);
                if (d > secondAbs) { secondAbs = d; second = c.Rows[i]; }
            }
            if (second != null)
            {
                double delta = second.GainB - second.GainA;
                s += (delta > 0 ? ", and " : ", but ") + Math.Abs(delta).ToString("0.0") +
                     " dB " + (delta > 0 ? "louder" : "quieter") + " on the " +
                     Channels.Driver[second.Path] + " (path " + second.Path + ")";
            }

            s += c.DelaysIdentical
                ? "; every delay is identical."
                : "; delays differ on at least one path.";

            int ba = 0, bb = 0;
            for (int i = 0; i < c.Rows.Count; i++) { ba += c.Rows[i].BandsA; bb += c.Rows[i].BandsB; }
            s += "  " + ba + " bands versus " + bb + " bands across the eight paths.";
            return s;
        }

        /// <summary>A file's crossover map, and whether it is the map the car is wearing.</summary>
        public static bool CrossoverMatches(TuneFile t, PassBand[] car)
        {
            if (t == null || car == null) return false;
            TuneMemory m = t.Mem(0);
            if (m == null) return false;
            for (int p = 0; p < Channels.Live; p++)
            {
                TuneOutput o = m.Out(p);
                if (o == null || car[p] == null) return false;
                if (o.Hp == null || Math.Abs(o.Hp.Fc - car[p].Fc) > 1.0) return false;
                if (o.Hp.Enable != true) return false;
            }
            return true;
        }

        public static string CrossoverMapText(TuneFile t)
        {
            TuneMemory m = t.Mem(0);
            if (m == null) return "-";
            List<string> parts = new List<string>();
            parts.Add("tweeters " + Channels.Hz(FirstHp(m, 0)) +
                      " \u00b7 3.5\u2033 " + Channels.Hz(FirstHp(m, 4)) +
                      " \u00b7 8\u2033 " + Channels.Hz(FirstHp(m, 6)) +
                      " \u00b7 6.5\u2033 " + Channels.Hz(FirstHp(m, 7)));
            return string.Join("  ", parts.ToArray());
        }

        private static double FirstHp(TuneMemory m, int path)
        {
            TuneOutput o = m.Out(path);
            return (o == null || o.Hp == null) ? 0 : o.Hp.Fc;
        }
    }
}
