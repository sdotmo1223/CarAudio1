using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;

namespace DreTunes
{
    /// <summary>One crossover filter slot: high-pass (HiLow 0) or low-pass (HiLow 1).</summary>
    public sealed class PassBand
    {
        public double Fc;
        public int HiLow;          // 0 = high-pass, 1 = low-pass
        public int Slope;          // 1/2/3 = 12/18/24 dB/oct
        public int FilterType;     // 1 = Linkwitz-Riley
        public int ButtType;
        public bool Enable;
    }

    public sealed class TuneOutput
    {
        public int Index;
        public double Gain;
        public double Delay;
        public bool Mute;
        public bool Revert;
        public int LinkPair;
        public PassBand Hp;
        public PassBand Lp;
        public List<Band> Bands = new List<Band>();

        public int ActiveBands
        {
            get
            {
                int n = 0;
                for (int i = 0; i < Bands.Count; i++) if (Bands[i].IsActive) n++;
                return n;
            }
        }

        public double MaxQ
        {
            get
            {
                double m = 0;
                for (int i = 0; i < Bands.Count; i++) if (Bands[i].IsActive) m = Math.Max(m, Bands[i].Q);
                return m;
            }
        }
    }

    public sealed class TuneMemory
    {
        public int Index;
        public List<TuneOutput> Outputs = new List<TuneOutput>();

        public TuneOutput Out(int path)
        {
            for (int i = 0; i < Outputs.Count; i++) if (Outputs[i].Index == path) return Outputs[i];
            return null;
        }
    }

    /// <summary>
    /// A parsed .idsp: the vendor's own preset file, four memory blocks, 17 outputs each.
    ///
    /// Read with a direct-child walk, not a descendant search: an <output> carries
    /// passBand/Fc before eq/Fc, so a descendant lookup returns the crossover frequency every
    /// time an EQ band's centre is asked for. That is the same trap web\index.html documents in
    /// kidsOf(), and the reason this parser is written against the same rule.
    ///
    /// Nothing here writes to the DSP.
    /// </summary>
    public sealed class TuneFile
    {
        public string FullPath;
        public string FileName;
        public int Version;
        public int RunMemory;
        public int RunSource;
        public double RunVolume;
        public List<TuneMemory> Memories = new List<TuneMemory>();
        public DateTime Modified;
        public long Bytes;

        public string Stem
        {
            get
            {
                string n = FileName;
                if (n != null && n.EndsWith(".idsp", StringComparison.OrdinalIgnoreCase))
                    n = n.Substring(0, n.Length - 5);
                return n;
            }
        }

        public TuneMemory Mem(int index)
        {
            if (Memories == null || Memories.Count == 0) return null;
            for (int i = 0; i < Memories.Count; i++) if (Memories[i].Index == index) return Memories[i];
            return Memories[index >= 0 && index < Memories.Count ? index : 0];
        }

        /// <summary>Active bands across the eight live paths - the number a person quotes.</summary>
        public int LiveBands
        {
            get
            {
                TuneMemory m = Mem(0);
                if (m == null) return 0;
                int n = 0;
                for (int p = 0; p < Channels.Live; p++)
                {
                    TuneOutput o = m.Out(p);
                    if (o != null) n += o.ActiveBands;
                }
                return n;
            }
        }

        public double LiveMaxQ
        {
            get
            {
                TuneMemory m = Mem(0);
                double q = 0;
                if (m == null) return 0;
                for (int p = 0; p < Channels.Live; p++)
                {
                    TuneOutput o = m.Out(p);
                    if (o != null) q = Math.Max(q, o.MaxQ);
                }
                return q;
            }
        }

        /// <summary>True when every memory block carries the same payload (both our files do).</summary>
        public bool MemoriesIdentical
        {
            get
            {
                if (Memories.Count < 2) return true;
                string first = Signature(Memories[0]);
                for (int i = 1; i < Memories.Count; i++)
                    if (Signature(Memories[i]) != first) return false;
                return true;
            }
        }

        private static string Signature(TuneMemory m)
        {
            System.Text.StringBuilder b = new System.Text.StringBuilder();
            for (int i = 0; i < m.Outputs.Count; i++)
            {
                TuneOutput o = m.Outputs[i];
                b.Append(o.Index).Append('|').Append(o.Gain.ToString("0.0", CultureInfo.InvariantCulture))
                 .Append('|').Append(o.Delay.ToString("0.0", CultureInfo.InvariantCulture));
                for (int k = 0; k < o.Bands.Count; k++)
                {
                    Band x = o.Bands[k];
                    b.Append('|').Append(x.Fc.ToString("0.##", CultureInfo.InvariantCulture))
                     .Append(':').Append(x.Gain.ToString("0.0", CultureInfo.InvariantCulture))
                     .Append(':').Append(x.Q.ToString("0.##", CultureInfo.InvariantCulture));
                }
                b.Append(';');
            }
            return b.ToString();
        }

        // ------------------------------------------------------------------ load

        public static TuneFile Load(string path)
        {
            XmlDocument doc = new XmlDocument();
            doc.XmlResolver = null;
            doc.Load(path);                       // Load(path) honours the BOM and the encoding

            XmlElement root = doc.DocumentElement;
            if (root == null || root.Name != "DP12_Param")
                throw new InvalidDataException("root is <" + (root == null ? "?" : root.Name) +
                                               ">, expected <DP12_Param>");

            TuneFile t = new TuneFile();
            t.FullPath = path;
            t.FileName = Path.GetFileName(path);
            FileInfo fi = new FileInfo(path);
            t.Modified = fi.LastWriteTime;
            t.Bytes = fi.Length;

            t.Version = (int)Num(root, "version", 0);
            XmlElement run = Child(root, "RunStatus");
            if (run != null)
            {
                t.RunMemory = (int)Num(run, "mainMemory", 0);
                t.RunSource = (int)Num(run, "mainSource", 0);
                t.RunVolume = Num(run, "mainVolume", 0);
            }

            int mi = 0;
            foreach (XmlNode n in root.ChildNodes)
            {
                XmlElement mel = n as XmlElement;
                if (mel == null || mel.Name != "memory") continue;
                TuneMemory mem = new TuneMemory();
                mem.Index = IndexAttr(mel, mi);
                int oi = 0;
                foreach (XmlNode on in mel.ChildNodes)
                {
                    XmlElement oel = on as XmlElement;
                    if (oel == null || oel.Name != "output") continue;
                    TuneOutput o = new TuneOutput();
                    o.Index = IndexAttr(oel, oi);
                    o.Mute = Num(oel, "mute", 0) != 0;
                    o.Gain = Round1(Num(oel, "gain", 0));
                    o.Delay = Round1(Num(oel, "delay", 0));
                    o.Revert = Num(oel, "revert", 0) != 0;
                    o.LinkPair = (int)Num(oel, "linkPair", 0);
                    foreach (XmlNode pn in oel.ChildNodes)
                    {
                        XmlElement pel = pn as XmlElement;
                        if (pel == null || pel.Name != "passBand") continue;
                        PassBand pb = new PassBand();
                        pb.Fc = Num(pel, "Fc", 0);
                        pb.HiLow = (int)Num(pel, "hiLowType", 0);
                        pb.Slope = (int)Num(pel, "Slope", 0);
                        pb.FilterType = (int)Num(pel, "filterType", 0);
                        pb.ButtType = (int)Num(pel, "butt_Type", 0);
                        pb.Enable = Num(pel, "Enable", 0) != 0;
                        if (pb.HiLow == 0 && o.Hp == null) o.Hp = pb;
                        else if (pb.HiLow == 1 && o.Lp == null) o.Lp = pb;
                    }
                    foreach (XmlNode en in oel.ChildNodes)
                    {
                        XmlElement eel = en as XmlElement;
                        if (eel == null || eel.Name != "eq") continue;
                        Band b = new Band();
                        b.Fc = Num(eel, "Fc", 20);
                        b.Gain = Round1(Num(eel, "Gain", 0));
                        b.Q = Math.Round(Num(eel, "Q", 1) * 100.0) / 100.0;
                        b.Enable = true;
                        o.Bands.Add(b);
                    }
                    mem.Outputs.Add(o);
                    oi++;
                }
                t.Memories.Add(mem);
                mi++;
            }
            if (t.Memories.Count == 0)
                throw new InvalidDataException("no <memory> blocks in this file");
            return t;
        }

        private static double Round1(double v) { return Math.Round(v * 10.0) / 10.0; }

        private static XmlElement Child(XmlElement parent, string name)
        {
            if (parent == null) return null;
            foreach (XmlNode n in parent.ChildNodes)
            {
                XmlElement e = n as XmlElement;
                if (e != null && e.Name == name) return e;
            }
            return null;
        }

        private static double Num(XmlElement parent, string name, double dflt)
        {
            XmlElement e = Child(parent, name);
            if (e == null) return dflt;
            double v;
            if (!double.TryParse(e.InnerText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                return dflt;
            return v;
        }

        private static int IndexAttr(XmlElement e, int fallback)
        {
            string a = e.GetAttribute("index");
            int v;
            return int.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }
    }

    /// <summary>
    /// What a tune file is, in words the app is allowed to use.
    ///
    /// The distinction is not cosmetic: six of the eight files in web\ were inherited from the
    /// vendor's own preset banks and are not this project's work or its measurements. Presenting
    /// them as equals would be the app's first lie.
    /// </summary>
    public sealed class Provenance
    {
        public bool Authored;
        public string Kind;        // "measured tune", "voicing tune", "source tune"
        public string Note;        // one sentence, shown in the library detail

        public Provenance(bool authored, string kind, string note)
        {
            Authored = authored; Kind = kind; Note = note;
        }
    }

    public static class ProvenanceTable
    {
        private static readonly Dictionary<string, Provenance> Known =
            new Dictionary<string, Provenance>(StringComparer.OrdinalIgnoreCase)
        {
            { "1-car-tune-2026-10-20.idsp", new Provenance(true, "measured tune",
                "Built from the 2026-10-20 19:12 eight-channel run: 57 bands across the 8 live paths, " +
                "transcribed band for band from tune\\tune-plan.json and confirmed against the device.") },
            { "2-bor8f3-improved.idsp", new Provenance(true, "voicing tune",
                "The owner's B voicing, structurally repaired: 41 bands, every Q \u2264 2.8 (about a " +
                "third of an octave). Keeps bor8f3's levels, so the 8\u2033 and 6.5\u2033 pairs sit " +
                "16-17 dB above what the measurement asked for - deliberately.") },
            { "bor8f3.idsp", new Provenance(false, "source tune",
                "Inherited bank preset (the pre-change crossover map: 2500 / 250-2500 / 110-2500 / " +
                "140-1000). Not ours, not measured here.") },
            { "ai2.idsp", new Provenance(false, "source tune", "Inherited bank preset. Not ours, not measured here.") },
            { "cb0nyk.idsp", new Provenance(false, "source tune", "Inherited bank preset. Not ours, not measured here.") },
            { "tinker3.idsp", new Provenance(false, "source tune", "Inherited bank preset. Not ours, not measured here.") },
            { "w6784b.idsp", new Provenance(false, "source tune", "Inherited bank preset. Not ours, not measured here.") },
            { "xswmps.idsp", new Provenance(false, "source tune", "Inherited bank preset. Not ours, not measured here.") }
        };

        public static Provenance Of(string fileName)
        {
            Provenance p;
            if (Known.TryGetValue(fileName ?? "", out p)) return p;
            return new Provenance(false, "unlabelled", "A .idsp in the library that this app has no provenance for.");
        }
    }
}
