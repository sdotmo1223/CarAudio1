using System;
using LibHoneyWg;

namespace Ds18Bridge
{
    /// <summary>
    /// Biquad coefficient conversion, ported from AP.cs class DspDataTrans.
    ///
    /// Q-format: a 28-bit magnitude (integer part in bits 24..27, 24 fraction bits in 0..23)
    /// with the sign carried in bits 28..31 as 0xF. Zero returns 0 without a sign nibble.
    ///
    /// The biquad coefficients themselves are NOT re-implemented here: they are computed by
    /// LibHoneyWg.StaticFunc.EqGetCoff, the same vendor DLL the official app calls.
    /// </summary>
    public static class DspMath
    {
        /// <summary>Original DspDataTrans.CalcQ24HexOfDecimal.</summary>
        public static uint CalcQ24HexOfDecimal(double de)
        {
            uint acc = 0u;
            double magnitude = (de > 0.0) ? de : (-1.0 * de);
            uint intPart = (uint)Math.Floor(magnitude);
            acc |= (intPart << 24) & 0x0F000000u;

            uint frac = 0u;
            double fracPart = magnitude - (double)intPart;
            for (int i = 0; i < 24; i++)
            {
                double doubled = fracPart * 2.0;
                if (doubled >= 1.0)
                {
                    frac |= 1u;
                    fracPart = doubled - 1.0;
                }
                else
                {
                    fracPart = doubled;
                }
                frac <<= 1;
            }

            acc |= (frac >> 1) & 0x00FFFFFFu;
            if (fracPart * 2.0 > 1.0)
            {
                acc |= 1u;
            }

            if (de < 0.0)
            {
                acc = ~acc + 1u;
            }

            if (acc == 0u)
            {
                return 0u;
            }
            if (de < 0.0)
            {
                return 0xF0000000u | (acc & 0x0FFFFFFFu);
            }
            return acc & 0x0FFFFFFFu;
        }

        /// <summary>
        /// Builds the 168-byte StructPassBand for command 13 from two HLPS filters.
        /// Ports the packing block of DspSendPassBandOfOnePathConfigData (AP.cs:1412):
        /// scalars pass through untouched, coefficients come from the vendor's own
        /// LibHoneyWg.StaticFunc.HlpGetCoff with both vendor quirks preserved:
        ///   - low Fc &lt;= 20 Hz -&gt; identity coefficients {0,0,1,0,0} in all four slots
        ///   - high Fc &gt;= 20000 Hz -&gt; coefficients computed at 22000 Hz while the wire
        ///     keeps the frequency the UI showed
        /// Each coefficient is UInt32Small2Big(CalcQ24HexOfDecimal(result)), exactly like
        /// the EQ path. HLPS fields not carried in .idsp XML (Q_Value, Gain_Value) stay at
        /// zero - the official app's own load path leaves them there too.
        /// </summary>
        public static PassBand BuildPassBand(HLPS low, HLPS high)
        {
            PassBand pb = new PassBand();
            pb.lowFreq = (ushort)low.Fc;
            pb.highFreq = (ushort)high.Fc;
            pb.lowSlope = (byte)low.Slope;
            pb.highSlope = (byte)high.Slope;
            pb.lowFilterType = (byte)low.filterType;
            pb.highFilterType = (byte)high.filterType;

            double[] raw = new double[20];
            byte groupNum;

            // Low (slot 0): at or below 20 Hz the vendor sends identity passthrough.
            Array.Clear(raw, 0, raw.Length);
            StaticFunc.HlpGetCoff(low, raw, out groupNum);
            pb.lowFreqCoff = new Coff[4];
            for (int j = 0; j < 4; j++)
            {
                pb.lowFreqCoff[j].CoffData = new uint[5];
                if (low.Fc <= 20)
                {
                    pb.lowFreqCoff[j].CoffData[0] = 0u;
                    pb.lowFreqCoff[j].CoffData[1] = 0u;
                    pb.lowFreqCoff[j].CoffData[2] = 1u;
                    pb.lowFreqCoff[j].CoffData[3] = 0u;
                    pb.lowFreqCoff[j].CoffData[4] = 0u;
                }
                else
                {
                    for (int k = 0; k < 5; k++)
                    {
                        pb.lowFreqCoff[j].CoffData[k] =
                            Marshalling.UInt32Small2Big(CalcQ24HexOfDecimal(raw[j * 5 + k]));
                    }
                }
            }

            // High (slot 1): coefficients are computed at 22 kHz once the edge reaches
            // 20 kHz; the wire still carries the frequency the UI showed.
            HLPS highCalc = new HLPS();
            highCalc.Fc = (high.Fc >= 20000) ? 22000 : high.Fc;
            highCalc.hiLowType = high.hiLowType;
            highCalc.Slope = high.Slope;
            highCalc.filterType = high.filterType;
            highCalc.butt_Type = high.butt_Type;
            highCalc.Enable = high.Enable;
            highCalc.Q_Value = high.Q_Value;
            highCalc.Gain_Value = high.Gain_Value;

            Array.Clear(raw, 0, raw.Length);
            StaticFunc.HlpGetCoff(highCalc, raw, out groupNum);
            pb.highFreqCoff = new Coff[4];
            for (int j = 0; j < 4; j++)
            {
                pb.highFreqCoff[j].CoffData = new uint[5];
                for (int k = 0; k < 5; k++)
                {
                    pb.highFreqCoff[j].CoffData[k] =
                        Marshalling.UInt32Small2Big(CalcQ24HexOfDecimal(raw[j * 5 + k]));
                }
            }

            return pb;
        }

        /// <summary>
        /// Builds the 31 EqCoff records for command 14 from user-facing EQS values.
        /// Mirrors the packing block of DspSendEqOfOnePathConfigData:
        ///   freq   = Fc (Hz, raw)
        ///   gain   = round(Gain * 10) in 0.1 dB, bit15 set when negative
        ///   Q      = round(Q * 10)
        ///   coeff  = UInt32Small2Big(CalcQ24HexOfDecimal(EqGetCoff result))
        /// </summary>
        public static EqCoff[] BuildEqBands(EQS[] eq)
        {
            EqCoff[] result = new EqCoff[31];
            double[] raw = new double[5];

            for (int i = 0; i < 31; i++)
            {
                // EQS is a reference type in LibHoneyWg; a missing band must become a flat
                // default rather than null (EqGetCoff dereferences it).
                EQS s = null;
                if (eq != null && i < eq.Length)
                {
                    s = eq[i];
                }
                if (s == null)
                {
                    s = new EQS();
                    s.Fc = (ushort)DefaultBandHz[i];
                    s.Gain = 0f;
                    s.Q = 1.0f;
                    s.Type = 0;
                    s.Enable = true;
                }

                // Clamp to the DP12_Param limits. Nothing downstream checks these: freq is
                // widened into a ushort, gain into a ushort with bit15 as the sign, and Q
                // into a byte at 0.1 units -- so an out-of-range value does not fail, it
                // wraps and produces a different filter entirely. The clamp is applied to s
                // itself, before EqGetCoff, so the packed fields and the vendor's own
                // coefficients are always derived from the same numbers.
                double fcClamped = GainCodec.Clamp((double)s.Fc, (double)EqFcMinHz, (double)EqFcMaxHz);
                double gainClamped = GainCodec.Clamp((double)s.Gain, GainCodec.EqGainMinDb, GainCodec.EqGainMaxDb);
                double qClamped = GainCodec.Clamp((double)s.Q, GainCodec.EqQMin, GainCodec.EqQMax);
                s.Fc = (ushort)Math.Round(fcClamped);
                s.Gain = (float)gainClamped;
                s.Q = (float)qClamped;

                EqCoff band = new EqCoff();
                band.freq = s.Fc;

                int gainTenths = (int)Math.Round((double)s.Gain * 10.0, 0);
                ushort gainEnc;
                if (gainTenths >= 0)
                {
                    gainEnc = (ushort)gainTenths;
                }
                else
                {
                    gainEnc = (ushort)(-1 * gainTenths);
                    gainEnc |= 0x8000;
                }
                band.gain = gainEnc;

                band.Q = (byte)(int)Math.Round((double)s.Q * 10.0, 0);
                band.reserve1 = 0;
                band.reserve2 = 0;
                band.reserve3 = 0;

                for (int j = 0; j < 5; j++)
                {
                    raw[j] = 0.0;
                }
                StaticFunc.EqGetCoff(s, raw);

                band.CoffData = new uint[5];
                for (int j = 0; j < 5; j++)
                {
                    band.CoffData[j] = Marshalling.UInt32Small2Big(CalcQ24HexOfDecimal(raw[j]));
                }

                result[i] = band;
            }
            return result;
        }

        /// <summary>
        /// Original DspDataTrans.CalcQ24DecimalOfHex - the inverse of CalcQ24HexOfDecimal.
        /// Implemented here so the encoder can be verified by round-trip rather than by
        /// hand-computed constants.
        /// </summary>
        public static double CalcQ24DecimalOfHex(uint q24)
        {
            uint mag = q24 & 0x0FFFFFFFu;
            bool negative = (q24 & 0xF0000000u) == 0xF0000000u;
            if (negative)
            {
                mag = ~mag + 1u;
            }

            int intPart = (int)((mag >> 24) & 0xF);
            uint rest = mag;
            double frac = 0.0;
            for (int i = 0; i < 24; i++)
            {
                frac /= 2.0;
                if ((rest & 1u) != 0)
                {
                    frac++;
                }
                rest >>= 1;
            }
            frac /= 2.0;
            double value = (double)intPart + frac;
            return negative ? -1.0 * value : value;
        }

        /// <summary>
        /// DP12_Param.DP12_FreqMin / Max. The app enforces these on its frequency text
        /// boxes, and the passband enable flags are derived from sitting exactly on either
        /// end (lowFreq != FreqMin means the low filter is enabled).
        /// </summary>
        public const int EqFcMinHz = 20;
        public const int EqFcMaxHz = 20000;

        /// <summary>Typical band centres for a 31-band graphic EQ (used to seed the UI).</summary>
        public static readonly double[] DefaultBandHz = new double[]
        {
            20, 25, 31, 40, 50, 63, 80, 100, 125, 160,
            200, 250, 315, 400, 500, 630, 800, 1000, 1250, 1600,
            2000, 2500, 3150, 4000, 5000, 6300, 8000, 10000, 12500, 16000, 20000
        };

        /// <summary>Builds a flat 31-band EQ at the default centre frequencies.</summary>
        public static EQS[] FlatEq()
        {
            EQS[] eq = new EQS[31];
            for (int i = 0; i < 31; i++)
            {
                EQS s = new EQS();
                s.Fc = (ushort)DefaultBandHz[i];
                s.Gain = 0f;
                s.Q = 1.0f;
                s.Type = 0;
                s.Enable = true;
                eq[i] = s;
            }
            return eq;
        }
    }
}
