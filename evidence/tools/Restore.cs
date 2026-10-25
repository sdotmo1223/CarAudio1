using System;
using System.Globalization;
using System.IO;
using System.Threading;
using LibHoneyWg;
using Ds18Bridge;

namespace Restore
{
    // Standalone one-shot restore of the original memory-1/path-0 EQ cell
    // (device memory 2, output 0): the bridge's API clamps EQ gain to +/-10 dB, but the
    // original cell genuinely stores +12.0 and +10.1 dB, so the write must bypass
    // DspMath.BuildEqBands. Everything else is identical to the bridge: same vendor
    // DLL for coefficients, same Builders.Eq framing, same DspClient USB layer.
    //
    // Run while the bridge is DISCONNECTED (POST /api/disconnect first), then
    // POST /api/connect afterwards. Prints the FNV-1a of the read-back body; the
    // pre-wipe original is 3345808037.
    static class RestoreTool
    {
        const uint OrigTarget = 3345808037u;
        const byte Mem = 1;
        const byte PathIdx = 0;
        const byte Source = 0;   // bridge's current Source (0 = High-level input)
        const byte QByte = 10;   // Q = 1.0 uniform, proven by offline hash sweep

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

        static void Main(string[] args)
        {
            string temp = Path.Combine(Environment.GetEnvironmentVariable("TEMP"), "opencode");
            string csvPath = Path.Combine(temp, "orig-bands.csv");
            if (!File.Exists(csvPath))
            {
                Console.WriteLine("FAIL: missing " + csvPath);
                return;
            }

            string[] rows = File.ReadAllLines(csvPath);
            if (rows.Length != 31) { Console.WriteLine("FAIL: csv has " + rows.Length + " rows"); return; }

            EqCoff[] bands = new EqCoff[31];
            for (int i = 0; i < 31; i++)
            {
                string[] parts = rows[i].Split(',');
                int fc = int.Parse(parts[1], CultureInfo.InvariantCulture);
                double gain = double.Parse(parts[2], CultureInfo.InvariantCulture);

                EqCoff b = new EqCoff();
                b.freq = (ushort)fc;
                int tenths = (int)Math.Round(gain * 10.0, 0);
                ushort gEnc;
                if (tenths >= 0) gEnc = (ushort)tenths;
                else { gEnc = (ushort)(-1 * tenths); gEnc |= 0x8000; }
                b.gain = gEnc;
                b.Q = QByte;
                b.reserve1 = 0;
                b.reserve2 = 0;
                b.reserve3 = 0;
                b.CoffData = ComputeCoeffs(fc, gain, QByte / 10.0);
                bands[i] = b;
            }

            byte[] frame = Builders.Eq(Mem, PathIdx, bands, false);
            Console.WriteLine("cmd14 frame: " + frame.Length + " bytes (unclamped gains included)");

            DspClient client = new DspClient();
            if (!client.Open())
            {
                Console.WriteLine("FAIL open: " + client.LastError);
                return;
            }
            Console.WriteLine("USB open OK");

            if (!client.Send(frame))
            {
                Console.WriteLine("FAIL send: " + client.LastError);
                client.Close();
                return;
            }
            Console.WriteLine("cmd14 sent (writes are never acked by firmware)");
            Thread.Sleep(600);

            byte[] readFrame = Builders.ReadParam(ReadSubCmd.EqAndPassBand, Mem, Source, PathIdx);
            RxFrame reply;
            if (!client.RequestCmd(readFrame, ReadSubCmd.EqAndPassBand, 2500, out reply))
            {
                Console.WriteLine("FAIL verify read: " + client.LastError);
                client.Close();
                return;
            }

            uint h = Fnv1a(reply.Body);
            Console.WriteLine("read-back body: " + reply.Body.Length + " bytes, FNV-1a = " + h);
            Console.WriteLine(h == OrigTarget
                ? "PASS: cell is byte-identical to the pre-wipe original (3345808037)"
                : "MISMATCH: expected " + OrigTarget);

            // Show the two out-of-spec gains decoded from the device's own reply.
            EqCoff[] back;
            byte byPass;
            if (ReplyParse.TryParseEqGroup(reply.Body, out back, out byPass))
            {
                Console.WriteLine("band0 fc=" + back[0].freq + " q=" + back[0].Q
                    + "  band4 gain=" + ReplyParse.DecodeEqGainDb(back[4].gain)
                    + "  band19 gain=" + ReplyParse.DecodeEqGainDb(back[19].gain)
                    + "  byPass=" + byPass);
            }

            client.Close();
            Console.WriteLine("closed");
        }
    }
}
