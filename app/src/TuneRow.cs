using System;

namespace DreTunes
{
    /// <summary>
    /// One row of the Tune table: the five things a channel has, and where each number came from.
    ///
    /// The point of this class is that it is a pure projection - no painting, no requests - so the
    /// self-test can assert the strings and, more importantly, assert the *source label* that goes
    /// with each one. Level, delay and polarity are command 11 and can never be read back; the
    /// crossovers come off the device with the EQ read. A row that mixed those up without saying
    /// so would be the single most misleading thing on the screen.
    /// </summary>
    public sealed class TuneRow
    {
        /// <summary>Level, high pass, low pass, delay, polarity.</summary>
        public const int Columns = 5;

        public const string FromDevice = "DEVICE";
        public const string FromFile = "FILE";
        public const string FromMirror = "MIRROR";
        public const string Unknown = "\u2014";

        public int Path;

        /// <summary>False when neither a cmd-11 mirror nor a tune file can supply level/delay.</summary>
        public bool Have;

        public double Level;
        public string LevelText = Unknown;
        public string LevelSource = Unknown;

        public double DelayMs;
        public string DelayText = Unknown;
        public string DelaySource = Unknown;

        public bool Invert;
        public string PolarityText = Unknown;
        public string PolaritySource = Unknown;

        public bool Muted;

        public PassBand Hp, Lp;
        /// <summary>True only when a tune FILE says the filter is switched off. A device read
        /// carries no enable bit, so it can never say "open".</summary>
        public bool HpOpen, LpOpen;

        public string HpType = Unknown, HpSlope = Unknown, HpFreq = Unknown;
        public string LpType = Unknown, LpSlope = Unknown, LpFreq = Unknown;

        public string XoSource = Unknown;
        /// <summary>False for a device readback: the passband block has Fc, slope and filter
        /// type but no Enable, so "the filter is on" is not something it reported.</summary>
        public bool XoEnableKnown;

        /// <summary>
        /// How many of the five columns this app can write on this screen: zero, deliberately.
        ///
        /// A swap writes EQ from the Library surface and sends its command-11 block there. Nothing
        /// on the Tune screen writes at all - and crossovers are refused everywhere, because the
        /// brief puts them out of scope for a tune path and no readback would prove the write.
        /// The number is drawn in the footer, so a defect that changed it would change something
        /// a person can see rather than quietly widen a write path.
        /// </summary>
        public int WritableColumns
        {
            get { return Mutation.CrossoverWritable ? Columns : 0; }
        }

        public static string TypeText(PassBand pb)
        {
            if (pb == null) return Unknown;
            // Exactly Channels.CrossoverText's vocabulary: filterType 1 is Linkwitz-Riley,
            // everything else this firmware uses is a Butterworth. Never invent a third name.
            return pb.FilterType == 1 ? "LR" : "BT";
        }

        public static string SlopeText(PassBand pb)
        {
            return pb == null ? Unknown : Channels.Slope(pb.Slope);
        }

        public static string FreqText(PassBand pb)
        {
            return pb == null ? Unknown : Channels.Hz(pb.Fc) + " Hz";
        }

        public static TuneRow From(TuneFile file, Rig rig, int path)
        {
            TuneRow r = new TuneRow();
            r.Path = path;

            TuneOutput o = null;
            if (file != null)
            {
                TuneMemory m = file.Mem(0);
                if (m != null) o = m.Out(path);
            }

            bool mirror = rig != null && rig.OutputKnown
                          && path >= 0 && path < rig.Gain.Length;
            if (mirror)
            {
                r.Have = true;
                r.Level = rig.Gain[path];
                r.DelayMs = rig.Delay[path];
                r.Invert = rig.ORevert[path];
                r.Muted = rig.OMute[path];
                r.LevelSource = FromMirror;
                r.DelaySource = FromMirror;
                r.PolaritySource = FromMirror;
            }
            else if (o != null)
            {
                r.Have = true;
                r.Level = o.Gain;
                r.DelayMs = o.Delay;
                r.Invert = o.Revert;
                r.Muted = o.Mute;
                r.LevelSource = FromFile;
                r.DelaySource = FromFile;
                r.PolaritySource = FromFile;
            }

            if (r.Have)
            {
                r.LevelText = Channels.Db(r.Level, 1);
                r.DelayText = Channels.Ms(r.DelayMs);
                r.PolarityText = r.Invert ? "180\u00b0" : "0\u00b0";
            }

            bool device = rig != null && rig.EqOk && rig.Pb != null
                          && path >= 0 && path < rig.Pb.Length && rig.Pb[path] != null;
            if (device)
            {
                r.Hp = rig.Pb[path];
                r.Lp = (rig.PbLow != null && path < rig.PbLow.Length) ? rig.PbLow[path] : null;
                r.XoSource = FromDevice;
                r.XoEnableKnown = false;
            }
            else if (o != null)
            {
                r.Hp = o.Hp;
                r.Lp = o.Lp;
                r.XoSource = FromFile;
                r.XoEnableKnown = true;
            }

            if (r.XoSource != Unknown)
            {
                r.HpType = TypeText(r.Hp);
                r.HpSlope = SlopeText(r.Hp);
                r.HpFreq = FreqText(r.Hp);
                r.LpType = TypeText(r.Lp);
                r.LpSlope = SlopeText(r.Lp);
                r.LpFreq = FreqText(r.Lp);

                // A readback cannot say a filter is switched off; only a file can. Saying "open"
                // on a device read would be inventing an enable bit.
                if (r.XoEnableKnown)
                {
                    r.HpOpen = r.Hp == null || !r.Hp.Enable;
                    r.LpOpen = r.Lp == null || !r.Lp.Enable;
                }
            }

            return r;
        }
    }

    /// <summary>
    /// The Tune table's geometry, kept out of the paint code so a click can be mapped to a channel
    /// without a window on screen - and asserted.
    /// </summary>
    public static class TuneTable
    {
        public const int HeaderH = 40;
        public const int RowH = 30;

        /// <summary>Row index under a y coordinate, or -1 for the header and anything outside.</summary>
        public static int RowAt(int tableTop, int y)
        {
            int top = tableTop + HeaderH;
            if (y < top) return -1;
            int i = (y - top) / RowH;
            if (Mutation.RowOffset) i++;
            return (i >= 0 && i < Channels.Live) ? i : -1;
        }

        public static int Height { get { return HeaderH + RowH * Channels.Live; } }
    }
}
