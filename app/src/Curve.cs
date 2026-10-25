using System;
using System.Collections.Generic;

namespace DreTunes
{
    /// <summary>One parametric EQ band, in the units the DSP actually stores.</summary>
    public sealed class Band
    {
        public double Fc;
        public double Gain;
        public double Q;
        public bool Enable = true;

        public Band() { }

        public Band(double fc, double gain, double q)
        {
            Fc = fc; Gain = gain; Q = q;
        }

        public bool IsActive { get { return Enable && Math.Abs(Gain) > 0.0001; } }

        public override string ToString()
        {
            return Fc.ToString("0") + " Hz " + (Gain > 0 ? "+" : "") + Gain.ToString("0.0") + " dB Q" +
                   Q.ToString("0.00");
        }
    }

    /// <summary>
    /// The response a set of peaking bands actually produces.
    ///
    /// This is a MIRROR of tune\idsp.py - peaking_db() and part_curve() there - not a second
    /// implementation with its own opinion. Same RBJ cookbook peaking biquad, same 48 kHz
    /// sample rate, same series-of-filters (dB adds) model. The self-test (--selftest) runs
    /// tune\idsp.py curve on a real tune file and compares this code's output against it at
    /// every key frequency to 0.15 dB; if the two ever drift apart the app says so instead of
    /// quietly drawing a different curve from the tool that builds the tunes.
    /// </summary>
    public static class Curve
    {
        public const double Fs = 48000.0;

        /// <summary>1/6-octave grid, 20 Hz .. 20 kHz - the measurement grid, so overlays line up.</summary>
        public static readonly double[] Grid = MakeLogGrid(20.0, 61, 6.0);

        /// <summary>Frequencies every report in this project tabulates (idsp.py's KEY list).</summary>
        public static readonly double[] Key = new double[]
        {
            50, 80, 100, 125, 200, 300, 500, 800, 1000, 1500, 2000, 3000, 4000, 6000, 8000,
            12000, 16000
        };

        public static double[] MakeLogGrid(double f0, int n, double perOctave)
        {
            double[] g = new double[n];
            for (int k = 0; k < n; k++) g[k] = f0 * Math.Pow(2.0, k / perOctave);
            return g;
        }

        /// <summary>Magnitude of one peaking band, in dB, at f. Exactly idsp.peaking_db().</summary>
        public static double PeakingDb(double f0, double q, double gainDb, double f)
        {
            if (Mutation.FlatCurve) return 0.0;
            if (gainDb == 0.0) return 0.0;
            double a = Math.Pow(10.0, gainDb / 40.0);
            double w0 = 2.0 * Math.PI * f0 / Fs;
            double alpha = Math.Sin(w0) / (2.0 * Math.Max(q, 0.0001));
            double cw = Math.Cos(w0);
            double b0 = 1.0 + alpha * a;
            double b1 = -2.0 * cw;
            double b2 = 1.0 - alpha * a;
            double a0 = 1.0 + alpha / a;
            double a1 = -2.0 * cw;
            double a2 = 1.0 - alpha / a;

            double w = 2.0 * Math.PI * f / Fs;
            // z1 = exp(-jw)
            double zr = Math.Cos(-w), zi = Math.Sin(-w);
            // numerator b0 + b1 z1 + b2 z2 ; z2 = z1^2
            double z2r = zr * zr - zi * zi, z2i = 2 * zr * zi;
            double nr = b0 + b1 * zr + b2 * z2r;
            double ni = b1 * zi + b2 * z2i;
            double dr = a0 + a1 * zr + a2 * z2r;
            double di = a1 * zi + a2 * z2i;
            double den = dr * dr + di * di;
            if (den < 1e-30) return 0.0;
            double mag = Math.Sqrt((nr * nr + ni * ni) / den);
            return 20.0 * Math.Log10(Math.Max(mag, 1e-12));
        }

        /// <summary>Sum of every band's response. Series filters multiply, so their dB adds.</summary>
        public static double[] PartCurve(IEnumerable<Band> bands, double[] freqs)
        {
            double[] total = new double[freqs.Length];
            foreach (Band b in bands)
            {
                if (b == null || !b.IsActive) continue;
                for (int i = 0; i < freqs.Length; i++)
                    total[i] += PeakingDb(b.Fc, b.Q, b.Gain, freqs[i]);
            }
            return total;
        }

        /// <summary>Bilinear read of a log-spaced curve at an arbitrary frequency.</summary>
        public static double At(double[] freqs, double[] curve, double f)
        {
            if (curve == null || curve.Length == 0) return 0.0;
            if (f <= freqs[0]) return curve[0];
            if (f >= freqs[freqs.Length - 1]) return curve[curve.Length - 1];
            for (int i = 0; i < freqs.Length - 1; i++)
            {
                if (freqs[i] <= f && f <= freqs[i + 1])
                {
                    double t = (Math.Log(f) - Math.Log(freqs[i])) /
                               (Math.Log(freqs[i + 1]) - Math.Log(freqs[i]));
                    return curve[i] + t * (curve[i + 1] - curve[i]);
                }
            }
            return curve[curve.Length - 1];
        }

        /// <summary>Peak-to-peak of a curve between lo and hi Hz - the "realized ripple" figure.</summary>
        public static double Ripple(double[] freqs, double[] curve, double lo, double hi)
        {
            double mn = double.MaxValue, mx = double.MinValue;
            for (int i = 0; i < freqs.Length; i++)
            {
                if (freqs[i] < lo || freqs[i] > hi) continue;
                if (curve[i] < mn) mn = curve[i];
                if (curve[i] > mx) mx = curve[i];
            }
            return (mn == double.MaxValue) ? 0.0 : mx - mn;
        }

        public static double Median(IList<double> xs)
        {
            if (xs == null || xs.Count == 0) return 0.0;
            List<double> s = new List<double>(xs);
            s.Sort();
            int n = s.Count;
            return (n % 2 == 1) ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2.0;
        }
    }
}
