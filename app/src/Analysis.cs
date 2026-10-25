using System;
using System.Collections.Generic;

namespace DreTunes
{
    /// <summary>The result of comparing one device EQ readback against one tune file.</summary>
    public sealed class TuneDiff
    {
        public int Paths;
        public int PathsMatched;
        public int BandDiffs;
        public double WorstGainError;
        public List<string> Detail = new List<string>();
        public DateTime At = DateTime.MinValue;

        public bool Clean
        {
            get { return Paths > 0 && PathsMatched == Paths && BandDiffs == 0; }
        }

        public string Word
        {
            get
            {
                if (At == DateTime.MinValue) return "not compared";
                if (Clean) return "device matches file on all " + Paths + " paths";
                if (PathsMatched > 0) return PathsMatched + " of " + Paths + " paths match";
                return "device does not match the file";
            }
        }
    }

    public sealed class MirrorDiff
    {
        public int Paths;
        public int Diffs;
        public List<string> Detail = new List<string>();
        public bool Read;

        public bool Clean { get { return Read && Diffs == 0; } }
    }

    public sealed class GuardResult
    {
        public bool Allowed;
        public string Reason = "";
    }

    public sealed class NarrowBand
    {
        public int Path;
        public Band Band;
        public double Octaves;
    }

    /// <summary>
    /// The pure comparisons the app makes, kept out of the surfaces so the self-test can hammer
    /// them without a window, a device or a screenshot.
    ///
    /// Two deliberate design choices:
    ///   * Tolerances are the ones tune\verify-device.py uses (Fc 1 Hz, gain 0.15 dB, Q 0.06),
    ///     not new ones. A second set of tolerances would be a second truth.
    ///   * Every comparator has a stated meaning for "could not compare": it is unproven, never
    ///     clean. The brief's own doctor bug is the reason.
    /// </summary>
    public static class Analysis
    {
        public const double FcTol = 1.0;
        public const double GainTol = 0.15;
        public const double QTol = 0.06;

        /// <summary>
        /// Device readback versus a tune file's memory block. Active bands only: the file carries
        /// 31 slots and the device echoes 31 slots, and a band at 0.0 dB is not a band.
        /// </summary>
        public static TuneDiff Diff(Band[][] device, TuneFile file, int mem)
        {
            if (device == null || file == null)
            {
                TuneDiff empty = new TuneDiff();
                empty.At = DateTime.Now;
                empty.Paths = Channels.Live;
                return empty;
            }
            TuneMemory m = file.Mem(mem);
            if (m == null)
            {
                TuneDiff empty = new TuneDiff();
                empty.At = DateTime.Now;
                empty.Paths = Channels.Live;
                return empty;
            }
            List<Band>[] want = new List<Band>[Channels.Live];
            for (int p = 0; p < Channels.Live; p++)
            {
                TuneOutput o = m.Out(p);
                want[p] = o == null ? new List<Band>() : Active(o.Bands);
            }
            return DiffRaw(device, want);
        }

        /// <summary>The same comparator against an arbitrary expectation (a snapshot's bands).</summary>
        public static TuneDiff DiffRaw(Band[][] device, List<Band>[] expected)
        {
            TuneDiff d = new TuneDiff();
            d.At = DateTime.Now;
            d.Paths = Channels.Live;
            if (Mutation.CleanAlways)
            {
                d.PathsMatched = d.Paths;
                return d;
            }
            if (device == null || expected == null) return d;

            for (int p = 0; p < Channels.Live; p++)
            {
                if (device[p] == null || expected[p] == null) continue;
                List<Band> want = new List<Band>(expected[p]);
                List<Band> have = new List<Band>();
                for (int i = 0; i < device[p].Length; i++)
                    if (device[p][i].Enable && Math.Abs(device[p][i].Gain) > 0.001) have.Add(device[p][i]);

                for (int i = want.Count - 1; i >= 0; i--)
                {
                    int hit = -1;
                    for (int k = 0; k < have.Count; k++)
                    {
                        if (Math.Abs(have[k].Fc - want[i].Fc) <= FcTol &&
                            Math.Abs(have[k].Gain - want[i].Gain) <= GainTol &&
                            Math.Abs(have[k].Q - want[i].Q) <= QTol)
                        {
                            hit = k;
                            double e = Math.Abs(have[k].Gain - want[i].Gain);
                            if (e > d.WorstGainError) d.WorstGainError = e;
                            break;
                        }
                    }
                    if (hit >= 0) { have.RemoveAt(hit); want.RemoveAt(i); }
                }
                int bad = want.Count + have.Count;
                d.BandDiffs += bad;
                if (bad == 0) d.PathsMatched++;
                else if (d.Detail.Count < 8)
                {
                    string why = "";
                    if (want.Count > 0) why += want.Count + " missing";
                    if (have.Count > 0) why += (why.Length > 0 ? ", " : "") + have.Count + " unexpected";
                    d.Detail.Add("CH " + (p + 1) + " (" + Channels.Driver[p] + "): " + bad +
                                 " band difference(s) - " + why);
                }
            }
            return d;
        }

        private static List<Band> Active(List<Band> bands)
        {
            List<Band> list = new List<Band>();
            for (int i = 0; i < bands.Count; i++) if (bands[i].IsActive) list.Add(bands[i]);
            return list;
        }

        /// <summary>
        /// The wrong-memory guard.
        ///
        /// The device has one playing memory. A write to any other memory reports success and
        /// changes nothing audible - that is the trap the owner already hit, twice, from the
        /// phone page. The app resolves the playing memory from the device every time and refuses
        /// any other target unless the caller has explicitly acknowledged the override.
        /// </summary>
        public static GuardResult Guard(int deviceMem, int targetMem, bool deviceLive, bool overrideAck)
        {
            GuardResult g = new GuardResult();
            if (Mutation.AllowAlways)
            {
                g.Allowed = true;
                g.Reason = "target accepted";
                return g;
            }
            if (!deviceLive)
            {
                g.Allowed = false;
                g.Reason = "the DSP link is not live, so a write cannot be read back. Reconnect first.";
                return g;
            }
            if (deviceMem < 0)
            {
                g.Allowed = false;
                g.Reason = "the device has not reported which memory is playing.";
                return g;
            }
            if (targetMem == deviceMem) { g.Allowed = true; g.Reason = "target is the playing memory"; return g; }
            if (overrideAck)
            {
                g.Allowed = true;
                g.Reason = "OVERRIDDEN: target memory " + (targetMem + 1) + " is not the playing memory (" +
                           (deviceMem + 1) + "). The write will land, and nothing audible will change.";
                return g;
            }
            g.Allowed = false;
            g.Reason = "memory " + (targetMem + 1) + " is not the memory the device is playing (" +
                       (deviceMem + 1) + "). Writing it would report success and change nothing audible.";
            return g;
        }

        /// <summary>
        /// The sent command-11 block against the bridge's mirror of it.
        ///
        /// This is NOT a device verification and every caller must label it "mirror". It answers a
        /// narrower, still useful question: did the bridge store the block this app sent, or did a
        /// silent failure/quantisation change it on the way?
        /// </summary>
        public static MirrorDiff Mirror(double[] wantGain, double[] wantDelay, bool[] wantMute,
                                        bool[] wantRevert, double[] echoGain, double[] echoDelay,
                                        bool[] echoMute, bool[] echoRevert, int paths)
        {
            MirrorDiff m = new MirrorDiff();
            m.Paths = paths;
            if (echoGain == null || echoDelay == null) return m;
            m.Read = true;
            for (int i = 0; i < paths && i < Channels.Cmd11Paths; i++)
            {
                bool bad = Math.Abs(wantGain[i] - echoGain[i]) > 0.06 ||
                           Math.Abs(wantDelay[i] - echoDelay[i]) > 0.02;
                if (wantMute != null && echoMute != null && wantMute[i] != echoMute[i]) bad = true;
                if (wantRevert != null && echoRevert != null && wantRevert[i] != echoRevert[i]) bad = true;
                if (bad)
                {
                    m.Diffs++;
                    if (m.Detail.Count < 6)
                        m.Detail.Add("CH " + (i + 1) + ": sent " + Channels.Db(wantGain[i], 1) + " dB / " +
                                     wantDelay[i].ToString("0.0") + " ms, mirror holds " +
                                     Channels.Db(echoGain[i], 1) + " dB / " +
                                     echoDelay[i].ToString("0.0") + " ms");
                }
            }
            return m;
        }

        /// <summary>
        /// -3 dB bandwidth of a peaking band, in octaves: 2*asinh(1/(2Q))/ln2.
        ///
        /// The measurement grid is 1/6 octave (0.167). A band narrower than roughly a quarter
        /// octave cannot be trusted to describe a driver rather than a position, and it visibly
        /// combs against neighbouring bands. This is a number, not an adjective.
        /// </summary>
        public static double BandwidthOct(double q)
        {
            if (q <= 0.01) return 99;
            return 2.0 * Asinh(1.0 / (2.0 * q)) / Math.Log(2.0);
        }

        /// <summary>
        /// asinh is not in .NET Framework 4.x (it arrived in .NET Core 3), so it is written here.
        /// ln(x + sqrt(x^2 + 1)) is accurate enough over the Q range a biquad uses.
        /// </summary>
        private static double Asinh(double x)
        {
            return Math.Log(x + Math.Sqrt(x * x + 1.0));
        }

        public static List<NarrowBand> NarrowBands(TuneFile t, int mem, double maxOctaves)
        {
            List<NarrowBand> list = new List<NarrowBand>();
            TuneMemory m = t == null ? null : t.Mem(mem);
            if (m == null) return list;
            for (int p = 0; p < Channels.Live; p++)
            {
                TuneOutput o = m.Out(p);
                if (o == null) continue;
                for (int i = 0; i < o.Bands.Count; i++)
                {
                    Band b = o.Bands[i];
                    if (!b.IsActive) continue;
                    double oct = BandwidthOct(b.Q);
                    if (oct >= maxOctaves) continue;
                    NarrowBand nb = new NarrowBand();
                    nb.Path = p; nb.Band = b; nb.Octaves = oct;
                    list.Add(nb);
                }
            }
            return list;
        }

        /// <summary>
        /// Pairs of different channels carrying the same narrow band. Identical deep filters on
        /// two physically different drivers is one measurement copied, not two drivers agreeing -
        /// the positional-null signature. Reported as a question ("copied measurement?"), never as
        /// a fact about the car.
        /// </summary>
        public static List<string> SharedNarrow(TuneFile t, int mem)
        {
            List<string> hits = new List<string>();
            TuneMemory m = t == null ? null : t.Mem(mem);
            if (m == null) return hits;
            for (int a = 0; a < Channels.Live; a++)
            {
                TuneOutput oa = m.Out(a);
                if (oa == null) continue;
                for (int b = a + 1; b < Channels.Live; b++)
                {
                    TuneOutput ob = m.Out(b);
                    if (ob == null) continue;
                    for (int i = 0; i < oa.Bands.Count; i++)
                    {
                        Band x = oa.Bands[i];
                        if (!x.IsActive || BandwidthOct(x.Q) > 0.30) continue;
                        for (int k = 0; k < ob.Bands.Count; k++)
                        {
                            Band y = ob.Bands[k];
                            if (!y.IsActive || BandwidthOct(y.Q) > 0.30) continue;
                            if (Math.Abs(x.Fc - y.Fc) <= 1.0 && Math.Abs(x.Gain - y.Gain) <= 0.05 &&
                                Math.Abs(x.Q - y.Q) <= 0.02)
                            {
                                hits.Add("CH " + (a + 1) + " and CH " + (b + 1) + " both carry " +
                                         Channels.Hz(x.Fc) + " Hz " + Channels.Db(x.Gain, 1) +
                                         " dB Q" + x.Q.ToString("0.0") + " - identical narrow filters " +
                                         "on different drivers");
                                k = ob.Bands.Count;
                                i = oa.Bands.Count;
                            }
                        }
                    }
                }
            }
            return hits;
        }

        /// <summary>One sentence for a measured-versus-file comparison, with the numbers in it.</summary>
        public static string DiffSentence(TuneDiff d)
        {
            if (d == null || d.At == DateTime.MinValue) return "No readback comparison has been made.";
            if (d.Clean)
                return "All " + d.Paths + " paths match the file: every band's centre, gain and Q are " +
                       "inside Fc \u00b11 Hz, gain \u00b10.15 dB, Q \u00b10.06 (the same tolerances " +
                       "tune\\verify-device.py uses).";
            return d.PathsMatched + " of " + d.Paths + " paths match; " + d.BandDiffs +
                   " band(s) differ, worst gain error " + d.WorstGainError.ToString("0.00") + " dB.";
        }
    }
}
