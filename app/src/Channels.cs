using System;
using System.Collections.Generic;

namespace DreTunes
{
    /// <summary>
    /// The channel model - the single place the app decides which number means which driver.
    ///
    /// Two numbering systems are in play and confusing them makes every screen wrong:
    ///   DSP path  0..16   command 11 addresses 0..15; 16 is the master chain, outside it.
    ///   Channel   1..8    the user-facing number, always path + 1. Only these eight have drivers.
    ///
    /// The sub has no row here on purpose: it is fed from the head unit, not the DSP, so the app
    /// cannot see it and no surface may imply that it can.
    /// </summary>
    public static class Channels
    {
        public const int Live = 8;          // outputs with a driver attached
        public const int OutPaths = 17;     // outputs a .idsp carries
        public const int Cmd11Paths = 16;   // what command 11 can address

        public static readonly string[] Driver =
        {
            "front-left tweeter", "front-right tweeter", "rear-left tweeter", "rear-right tweeter",
            "front 3.5\u2033 pair", "rear 3.5\u2033 pair", "front 8\u2033 pair", "rear 6.5\u2033 pair"
        };

        public static readonly string[] Short =
        {
            "FL tweet", "FR tweet", "RL tweet", "RR tweet",
            "front 3.5", "rear 3.5", "front 8", "rear 6.5"
        };

        public static readonly string[] RosterType =
        {
            "Tweeter", "Tweeter", "Tweeter", "Tweeter",
            "Midrange", "Midrange", "Midrange", "Midbass"
        };

        /// <summary>0 = high (stereo tweeters), 1 = mid (mono pairs), 2 = bass (mono pairs).</summary>
        public static int Group(int path)
        {
            if (path < 4) return 0;
            if (path < 6) return 1;
            return 2;
        }

        public static readonly string[] GroupName = { "High", "Mid", "Bass" };
        public static readonly string[] GroupNote =
        {
            "4 tweeters, stereo",
            "2 \u00d7 3.5\u2033 pairs, mono",
            "8\u2033 and 6.5\u2033 pairs, mono"
        };

        public static string Label(int path)
        {
            return "CH " + (path + 1) + "  \u00b7  " + Driver[path];
        }

        /// <summary>DSP path for a user-facing channel number (channel 1 -> path 0).</summary>
        public static int Path(int channel) { return channel - 1; }

        public static int Channel(int path) { return path + 1; }

        public static string Hz(double hz)
        {
            if (hz >= 1000) return (hz / 1000.0).ToString(hz >= 10000 ? "0.#" : "0.##") + "k";
            return hz.ToString("0");
        }

        public static string Db(double v, int decimals)
        {
            string s = Math.Abs(v).ToString(decimals == 0 ? "0" : "0.0");
            if (v > 0.0001) return "+" + s;
            if (v < -0.0001) return "\u2212" + s;   // a real minus sign, not a hyphen
            return s;
        }

        public static string Ms(double v)
        {
            return v.ToString("0.0");
        }

        /// <summary>
        /// Filter slope: the protocol stores 1/2/3 for 12/18/24 dB/oct. An earlier formatter
        /// printed Slope*6 and called a Linkwitz-Riley 24 a "LR18"; that is the bug this helper
        /// exists to keep impossible.
        /// </summary>
        public static string Slope(int slope)
        {
            if (slope == 1) return "12";
            if (slope == 2) return "18";
            if (slope == 3) return "24";
            return slope.ToString();
        }

        public static string CrossoverText(PassBand pb)
        {
            if (pb == null || !pb.Enable) return "open";
            string kind = pb.FilterType == 1 ? "LR" : "BT";
            return kind + Slope(pb.Slope) + " \u00b7 " + Hz(pb.Fc) + " Hz";
        }
    }
}
