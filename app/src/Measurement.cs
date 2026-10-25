using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace DreTunes
{
    /// <summary>One 1/6-octave point: absolute dBFS, never normalised.</summary>
    public sealed class MeasurePoint
    {
        public double Hz;
        public double Db;
    }

    /// <summary>
    /// One per-channel capture. The schema is the one /api/run-save writes (channel N.json):
    /// absolute dBFS on a 1/6-octave grid plus the analyser's own verdict, so a curve can never
    /// be read as a normalised shape. Files exported by export-meas.ps1 carry "gate" instead of
    /// "sig" and are accepted identically - both are the same verdict from the same analyser.
    /// </summary>
    public sealed class MeasureChannel
    {
        public string File;
        public string Name;
        public int Number;              // 1-based channel, 0 when the file does not say
        public int Path = -1;           // DSP path (channel - 1)
        public string Type;
        public string Run;
        public DateTime Captured;
        public string Units = "dBFS";
        public string Grid;
        public double PeakDb;
        public double PeakHz;
        public bool GateOk;
        public string GateReason;
        public double BurstDb;
        public double FromHz;
        public double ToHz;
        public List<MeasurePoint> Points = new List<MeasurePoint>();

        public bool HasGate { get { return GateReason != null; } }

        public double DbAt(double hz)
        {
            if (Points.Count == 0) return 0;
            for (int i = 0; i < Points.Count - 1; i++)
            {
                if (hz >= Points[i].Hz && hz <= Points[i + 1].Hz)
                {
                    double t = (hz - Points[i].Hz) / (Points[i + 1].Hz - Points[i].Hz);
                    return Points[i].Db + t * (Points[i + 1].Db - Points[i].Db);
                }
            }
            return Points[Points.Count - 1].Db;
        }

        public double[] Hzs()
        {
            double[] r = new double[Points.Count];
            for (int i = 0; i < Points.Count; i++) r[i] = Points[i].Hz;
            return r;
        }

        public double[] Dbs()
        {
            double[] r = new double[Points.Count];
            for (int i = 0; i < Points.Count; i++) r[i] = Points[i].Db;
            return r;
        }
    }

    public sealed class MeasureSet
    {
        public string Folder;
        public string Name;
        public DateTime Captured;
        public List<MeasureChannel> Channels = new List<MeasureChannel>();
        public string Readme;
        public bool IsPerChannel;       // one file per channel with a points array

        public int GateFails
        {
            get
            {
                int n = 0;
                for (int i = 0; i < Channels.Count; i++) if (Channels[i].HasGate && !Channels[i].GateOk) n++;
                return n;
            }
        }

        public double PeakMin
        {
            get
            {
                double m = 0;
                for (int i = 0; i < Channels.Count; i++) if (i == 0 || Channels[i].PeakDb < m) m = Channels[i].PeakDb;
                return m;
            }
        }

        public double PeakMax
        {
            get
            {
                double m = 0;
                for (int i = 0; i < Channels.Count; i++) if (i == 0 || Channels[i].PeakDb > m) m = Channels[i].PeakDb;
                return m;
            }
        }

        public MeasureChannel AtPath(int path)
        {
            for (int i = 0; i < Channels.Count; i++) if (Channels[i].Path == path) return Channels[i];
            return null;
        }
    }

    /// <summary>Reads the measurement folders. Read-only: capture stays on the phone page.</summary>
    public static class Measurements
    {
        public static List<MeasureSet> Scan(string root)
        {
            List<MeasureSet> sets = new List<MeasureSet>();
            string dir = Path.Combine(root, "measurements");
            if (!Directory.Exists(dir)) return sets;

            foreach (string folder in Directory.GetDirectories(dir))
            {
                MeasureSet set = new MeasureSet();
                set.Folder = folder;
                set.Name = Path.GetFileName(folder);

                string[] jsons = Directory.GetFiles(folder, "*.json");
                List<MeasureChannel> list = new List<MeasureChannel>();
                for (int i = 0; i < jsons.Length; i++)
                {
                    MeasureChannel c = TryLoad(jsons[i]);
                    if (c != null) list.Add(c);
                }
                if (list.Count == 0) continue;

                // "channel N.json" first (the auto-save shape), then anything else with points.
                list.Sort(delegate(MeasureChannel a, MeasureChannel b)
                {
                    int an = a.Number > 0 ? a.Number : 99;
                    int bn = b.Number > 0 ? b.Number : 99;
                    if (an != bn) return an.CompareTo(bn);
                    return string.Compare(a.File, b.File, StringComparison.OrdinalIgnoreCase);
                });

                // A channel number is authoritative; without one, derive the path from the
                // channel's own name (tweeter 1-4, midrange 1-3, midbass 1 in roster order).
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].Number > 0) list[i].Path = Channels.Path(list[i].Number);
                    else if (list[i].Path < 0) list[i].Path = DerivePath(list[i].Name, i);
                }

                // Re-sort by the path that was just resolved, so the list reads CH 1..CH 8 instead
                // of alphabetically by file name ("midbass 1" before "tweeter 1"). The captured
                // files carry no channel number - this folder format predates it - so without this
                // the roster looked shuffled even though nothing was wrong with the data.
                list.Sort(delegate(MeasureChannel a, MeasureChannel b)
                {
                    int ap = a.Path >= 0 ? a.Path : 99;
                    int bp = b.Path >= 0 ? b.Path : 99;
                    if (ap != bp) return ap.CompareTo(bp);
                    return string.Compare(a.File, b.File, StringComparison.OrdinalIgnoreCase);
                });

                set.Channels = list;
                set.IsPerChannel = true;
                DateTime newest = DateTime.MinValue;
                for (int i = 0; i < list.Count; i++) if (list[i].Captured > newest) newest = list[i].Captured;
                set.Captured = newest;

                string readme = Path.Combine(folder, "README.md");
                if (File.Exists(readme)) set.Readme = readme;
                sets.Add(set);
            }

            sets.Sort(delegate(MeasureSet a, MeasureSet b) { return b.Captured.CompareTo(a.Captured); });
            return sets;
        }

        private static int DerivePath(string name, int fallback)
        {
            if (string.IsNullOrEmpty(name)) return fallback < Channels.Live ? fallback : -1;
            string n = name.ToLowerInvariant();
            int num = TrailingNumber(n);
            if (n.Contains("tweeter")) return Math.Max(0, num - 1);
            if (n.Contains("midrange")) return 3 + Math.Max(1, num);
            if (n.Contains("midbass")) return 6 + Math.Max(1, num);
            return fallback < Channels.Live ? fallback : -1;
        }

        private static int TrailingNumber(string s)
        {
            int i = s.Length - 1;
            while (i >= 0 && !char.IsDigit(s[i])) i--;
            int end = i;
            while (i >= 0 && char.IsDigit(s[i])) i--;
            if (end < 0 || end == i) return 1;
            int v;
            return int.TryParse(s.Substring(i + 1, end - i), out v) ? v : 1;
        }

        public static MeasureChannel TryLoad(string file)
        {
            try
            {
                string text = File.ReadAllText(file, System.Text.Encoding.UTF8);
                Dictionary<string, object> d = Json.Obj(Json.Parse(text));
                if (d == null) return null;
                List<object> pts = Json.Arr(Json.Get(d, "points"));
                if (pts == null || pts.Count == 0) return null;

                MeasureChannel c = new MeasureChannel();
                c.File = Path.GetFileName(file);
                c.Name = Json.S(d, "channel", Path.GetFileNameWithoutExtension(file));
                c.Number = Json.I(d, "channelNumber", 0);
                c.Type = Json.S(d, "type", "");
                c.Run = Json.S(d, "run", "");
                c.Units = Json.S(d, "units", "dBFS");
                c.Grid = Json.S(d, "grid", "");
                c.Captured = ParseDate(Json.S(d, "capturedAt", ""));

                Dictionary<string, object> peak = Json.Obj(Json.Get(d, "peak"));
                c.PeakDb = Json.D(peak, "dbfs", 0);
                c.PeakHz = Json.D(peak, "hz", 0);

                Dictionary<string, object> gate = Json.Obj(Json.Get(d, "gate"));
                if (gate == null) gate = Json.Obj(Json.Get(d, "sig"));
                if (gate != null)
                {
                    c.GateOk = Json.B(gate, "ok", false);
                    c.GateReason = Json.S(gate, "reason", null);
                    c.BurstDb = Json.D(gate, "burstDb", 0);
                    c.FromHz = gate.ContainsKey("fromHz") ? Json.D(gate, "fromHz", 0) : Json.D(gate, "from", 0);
                    c.ToHz = gate.ContainsKey("toHz") ? Json.D(gate, "toHz", 0) : Json.D(gate, "to", 0);
                }

                for (int i = 0; i < pts.Count; i++)
                {
                    List<object> pair = Json.Arr(pts[i]);
                    if (pair == null || pair.Count < 2) continue;
                    MeasurePoint p = new MeasurePoint();
                    p.Hz = (pair[0] is double) ? (double)pair[0] : 0;
                    p.Db = (pair[1] is double) ? (double)pair[1] : 0;
                    if (p.Hz > 0) c.Points.Add(p);
                }
                return c.Points.Count > 0 ? c : null;
            }
            catch
            {
                return null;                 // a malformed file is skipped, not fatal
            }
        }

        public static DateTime ParseDate(string s)
        {
            DateTime d;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
                                  DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeLocal, out d))
                return d;
            return DateTime.MinValue;
        }
    }
}
