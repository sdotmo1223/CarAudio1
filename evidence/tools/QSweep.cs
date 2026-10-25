using System;
using System.Globalization;
using System.IO;
using LibHoneyWg;
using Ds18Bridge;

namespace Qsweep
{
    // Offline reconstruction of the ORIGINAL memory-2/output-0 EQ cell.
    // No device writes: validates the encode pipeline against the current live body,
    // identifies the trailing mCrc algorithm, then sweeps uniform Q bytes against
    // the pre-wipe FNV-1a hash 3345808037.
    static class Harness
    {
        const uint OrigTarget = 3345808037u;
        const uint CurrTarget = 4244035889u;

        static uint Fnv1a(byte[] data)
        {
            uint h = 2166136261u;
            for (int i = 0; i < data.Length; i++)
            {
                h ^= data[i];
                h *= 16777619u;
            }
            return h;
        }

        static uint[] ComputeCoeffs(int fc, double gain, double q)
        {
            EQS s = new EQS();
            s.Fc = (ushort)fc;
            s.Gain = (float)gain;
            s.Q = (float)q;
            s.Type = 0;
            s.Enable = true;
            double[] raw = new double[5];
            for (int j = 0; j < 5; j++) raw[j] = 0.0;
            StaticFunc.EqGetCoff(s, raw);
            uint[] out5 = new uint[5];
            for (int j = 0; j < 5; j++)
                out5[j] = Marshalling.UInt32Small2Big(DspMath.CalcQ24HexOfDecimal(raw[j]));
            return out5;
        }

        static byte[] BuildEqGroup(int[] fc, double[] gain, byte q, bool[] zeroCoeffs)
        {
            byte[] eq = new byte[872];
            for (int i = 0; i < 31; i++)
            {
                int o = i * 28;
                ushort f = (ushort)fc[i];
                eq[o] = (byte)(f & 0xFF);
                eq[o + 1] = (byte)(f >> 8);

                int tenths = (int)Math.Round(gain[i] * 10.0, 0);
                ushort gEnc;
                if (tenths >= 0) gEnc = (ushort)tenths;
                else { gEnc = (ushort)(-1 * tenths); gEnc |= 0x8000; }
                eq[o + 2] = (byte)(gEnc & 0xFF);
                eq[o + 3] = (byte)(gEnc >> 8);

                eq[o + 4] = q;
                eq[o + 5] = 0;
                eq[o + 6] = 0;
                eq[o + 7] = 0;

                uint[] c;
                if (zeroCoeffs != null && zeroCoeffs[i])
                {
                    c = new uint[5]; // all zero
                }
                else
                {
                    c = ComputeCoeffs(fc[i], gain[i], q / 10.0);
                }
                for (int j = 0; j < 5; j++)
                {
                    uint v = c[j];
                    int p = o + 8 + j * 4;
                    eq[p] = (byte)(v & 0xFF);
                    eq[p + 1] = (byte)((v >> 8) & 0xFF);
                    eq[p + 2] = (byte)((v >> 16) & 0xFF);
                    eq[p + 3] = (byte)(v >> 24);
                }
            }
            eq[868] = 0; // byPass
            eq[869] = 0;
            eq[870] = 0;
            eq[871] = 0;
            return eq;
        }

        static uint Swap32(uint v)
        {
            return ((v & 0x000000FFu) << 24) | ((v & 0x0000FF00u) << 8)
                 | ((v & 0x00FF0000u) >> 8) | ((v & 0xFF000000u) >> 24);
        }

        static uint Sum32(byte[] d, int start, int len)
        {
            uint s = 0;
            for (int i = 0; i < len; i += 4)
            {
                uint w = (uint)(d[start + i] | (d[start + i + 1] << 8)
                              | (d[start + i + 2] << 16) | (d[start + i + 3] << 24));
                s += w;
            }
            return s;
        }

        static void Main()
        {
            string temp = Path.Combine(Environment.GetEnvironmentVariable("TEMP"), "opencode");
            byte[] body = File.ReadAllBytes(Path.Combine(temp, "path0-body.bin"));
            if (body.Length != 1044) { Console.WriteLine("BAD body length " + body.Length); return; }

            Console.WriteLine("body FNV (expect " + CurrTarget + ") = " + Fnv1a(body));

            byte[] pb = new byte[168];
            Array.Copy(body, 872, pb, 0, 168);
            uint stored = BitConverter.ToUInt32(body, 1040);

            // ---------- STEP 1: rebuild current eq from parsed values ----------
            int[] curFc = new int[31];
            double[] curGain = new double[31];
            byte[] curQ = new byte[31];
            int diffs = 0, firstDiff = -1;
            for (int i = 0; i < 31; i++)
            {
                int o = i * 28;
                curFc[i] = body[o] | (body[o + 1] << 8);
                ushort gEnc = (ushort)(body[o + 2] | (body[o + 3] << 8));
                int tenths = (gEnc & 0x8000) != 0 ? -(gEnc & 0x7FFF) : gEnc;
                curGain[i] = tenths / 10.0;
                curQ[i] = body[o + 4];
            }
            byte[] rebuilt = new byte[872];
            for (int i = 0; i < 31; i++)
            {
                int o = i * 28;
                byte[] band = new byte[28];
                band[0] = body[o]; band[1] = body[o + 1];
                band[2] = body[o + 2]; band[3] = body[o + 3];
                band[4] = body[o + 4]; band[5] = body[o + 5];
                band[6] = body[o + 6]; band[7] = body[o + 7];
                uint[] c = ComputeCoeffs(curFc[i], curGain[i], curQ[i] / 10.0);
                for (int j = 0; j < 5; j++)
                {
                    uint v = c[j];
                    int p = 8 + j * 4;
                    band[p] = (byte)(v & 0xFF);
                    band[p + 1] = (byte)((v >> 8) & 0xFF);
                    band[p + 2] = (byte)((v >> 16) & 0xFF);
                    band[p + 3] = (byte)(v >> 24);
                }
                for (int k = 0; k < 28; k++)
                {
                    if (rebuilt[i * 28 + k] == 0 && band[k] != 0) { } // placeholder
                }
                Array.Copy(band, 0, rebuilt, i * 28, 28);
                // compare directly against body
                for (int k = 0; k < 28; k++)
                {
                    if (body[i * 28 + k] != band[k])
                    {
                        diffs++;
                        if (firstDiff < 0) firstDiff = i * 28 + k;
                    }
                }
            }
            Console.WriteLine("STEP1 pipeline: " + (diffs == 0
                ? "EXACT match (fresh LibHoneyWg == device-stored bytes)"
                : diffs + " byte diffs, first at " + firstDiff));
            Console.WriteLine("  Q bytes seen: " + string.Join(",", Array.ConvertAll(curQ, delegate(byte b) { return b.ToString(); })));

            // ---------- STEP 2: mCrc algorithm identification ----------
            Console.WriteLine("STEP2 mCrc stored: LE=0x" + stored.ToString("x8")
                + " BE=0x" + Swap32(stored).ToString("x8"));
            string hit = null;
            Try("RevertTable(872 eq)", CrcGet.RevertTable(872, body, 0), stored, ref hit);
            Try("RevertTable(1040 eq+pb)", CrcGet.RevertTable(1040, body, 0), stored, ref hit);
            Try("RevertTable(168 pb)", CrcGet.RevertTable(168, body, 872), stored, ref hit);
            Try("RevertTable(868)", CrcGet.RevertTable(868, body, 0), stored, ref hit);
            Try("WithInit(1040,0)", CrcGet.RevertTableWithInit(1040, body, 0, 0u), stored, ref hit);
            Try("WithInit(872,0)", CrcGet.RevertTableWithInit(872, body, 0, 0u), stored, ref hit);
            Try("WithInit(1040,FFFFFFFF)", CrcGet.RevertTableWithInit(1040, body, 0, 0xFFFFFFFFu), stored, ref hit);
            Try("Sum32(1040)", Sum32(body, 0, 1040), stored, ref hit);
            Try("Swap32(RevertTable1040)", Swap32(CrcGet.RevertTable(1040, body, 0)), stored, ref hit);
            Try("Swap32(RevertTable872)", Swap32(CrcGet.RevertTable(872, body, 0)), stored, ref hit);

            if (hit == null)
            {
                Console.WriteLine("STEP2: NO CRC match found -- sweep aborted (hash would be unconstrained).");
                return;
            }
            Console.WriteLine("STEP2 CRC algorithm = " + hit);

            // ---------- STEP 3: original bands + uniform Q sweep ----------
            string[] rows = File.ReadAllLines(Path.Combine(temp, "orig-bands.csv"));
            int[] fc = new int[31];
            double[] gain = new double[31];
            bool[] zeroGain = new bool[31];
            for (int i = 0; i < 31; i++)
            {
                string[] parts = rows[i].Split(',');
                fc[i] = int.Parse(parts[0 + 1], CultureInfo.InvariantCulture);
                gain[i] = double.Parse(parts[2 - 1 + 1 - 1 + 1 + 1 - 1 + 1 - 1], CultureInfo.InvariantCulture);
                zeroGain[i] = gain[i] == 0.0;
            }
            // NOTE: rows are "idx,fc,gain"
            for (int i = 0; i < 31; i++)
            {
                string[] parts = rows[i].Split(',');
                fc[i] = int.Parse(parts[1], CultureInfo.InvariantCulture);
                gain[i] = double.Parse(parts[2], CultureInfo.InvariantCulture);
                zeroGain[i] = gain[i] == 0.0;
            }

            Console.WriteLine("STEP3 sweep over q=0..255 (uniform), target " + OrigTarget);
            int found = -1;
            for (int q = 0; q <= 255; q++)
            {
                byte[] eq = BuildEqGroup(fc, gain, (byte)q, null);
                byte[] full = new byte[1044];
                Array.Copy(eq, 0, full, 0, 872);
                Array.Copy(pb, 0, full, 872, 168);
                uint crc = CrcGet.RevertTable(1040, full, 0);
                BitConverter.GetBytes(crc).CopyTo(full, 1040);
                if (Fnv1a(full) == OrigTarget) { found = q; break; }
            }
            if (found >= 0) Console.WriteLine("STEP3 MATCH qByte=" + found + " (Q=" + (found / 10.0) + ")");
            else Console.WriteLine("STEP3 no uniform match");

            // variant: zero coeffs on zero-gain bands (app Enable=false write semantics)
            int found2 = -1;
            for (int q = 0; q <= 255; q++)
            {
                byte[] eq = BuildEqGroup(fc, gain, (byte)q, zeroGain);
                byte[] full = new byte[1044];
                Array.Copy(eq, 0, full, 0, 872);
                Array.Copy(pb, 0, full, 872, 168);
                uint crc = CrcGet.RevertTable(1040, full, 0);
                BitConverter.GetBytes(crc).CopyTo(full, 1040);
                if (Fnv1a(full) == OrigTarget) { found2 = q; break; }
            }
            if (found2 >= 0) Console.WriteLine("STEP4 MATCH (zero-gain bands zeroed) qByte=" + found2);
            else Console.WriteLine("STEP4 no match in zeroed-coeff variant");

            // leading hypothesis report
            foreach (int q in new int[] { 43, 10, 7 })
            {
                byte[] eq = BuildEqGroup(fc, gain, (byte)q, null);
                byte[] full = new byte[1044];
                Array.Copy(eq, 0, full, 0, 872);
                Array.Copy(pb, 0, full, 872, 168);
                uint crc = CrcGet.RevertTable(1040, full, 0);
                BitConverter.GetBytes(crc).CopyTo(full, 1040);
                Console.WriteLine("  hash(q=" + q + ") = " + Fnv1a(full));
            }
        }

        static void Try(string name, uint value, uint stored, ref string hit)
        {
            bool m = value == stored;
            Console.WriteLine("  " + name + " = 0x" + value.ToString("x8") + (m ? "  << MATCH" : ""));
            if (m && hit == null) hit = name;
        }
    }
}
