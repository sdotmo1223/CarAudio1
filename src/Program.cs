using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using LibHoneyWg;

namespace Ds18Bridge
{
    public static class Program
    {
        private static int _failures;

        private static void Check(bool cond, string what)
        {
            Console.WriteLine((cond ? "  PASS  " : "  FAIL  ") + what);
            if (!cond)
            {
                _failures++;
            }
        }

        public static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--serve")
            {
                return Serve(args);
            }

            if (args.Length == 0 || args[0] != "--selftest")
            {
                Console.WriteLine("ds18-bridge  (MVP)");
                Console.WriteLine("  --selftest       run protocol self-tests (no hardware needed)");
                Console.WriteLine("  --serve          start the web bridge  [--port N]");
                Console.WriteLine();
                Console.WriteLine("No arguments prints this help.");
                return 0;
            }

            Console.WriteLine("== struct sizes (marshalled, Pack=1) ==");
            Check(Marshal.SizeOf(typeof(TransHead)) == 4, "TransHead = 4");
            Check(Marshal.SizeOf(typeof(GroupData)) == 8, "GroupData = 8");
            Check(Marshal.SizeOf(typeof(ReadCmdData)) == 4, "ReadCmdData = 4");
            Check(Marshal.SizeOf(typeof(EqCoff)) == 28, "EqCoff = 28");
            Check(Marshal.SizeOf(typeof(EqGroup)) == 872, "EqGroup = 872");
            Check(Marshal.SizeOf(typeof(DspInput)) == 68, "DspInput = 68");

            Console.WriteLine("== TX framing ==");
            byte[] readCmd = Builders.ReadParam(ReadSubCmd.MainRunStatus, 1, 2, 3);
            Check(readCmd.Length == 16, "ReadParam frame = 16 bytes (4 hdr + 8 payload + 4 crc)");
            Check(readCmd[0] == 0x55 && readCmd[1] == 0xAA, "preamble 0x55 0xAA");
            Check(readCmd[2] == 8 && readCmd[3] == 0, "LE16 payload byte-count = 8");
            Check(readCmd[4] == Cmd.ReadParam, "cmdIndex at payload[0] = 15");
            Check(readCmd[5] == 1 && readCmd[6] == 2 && readCmd[7] == 3, "mem/source/path forwarded");
            Check(readCmd[8] == ReadSubCmd.MainRunStatus, "readCmdIndex at payload[4] = 2");

            // CRC must be reproducible over the payload region only.
            uint manual = CrcGet.RevertTable(8, readCmd, 4);
            uint frameCrc = BitConverter.ToUInt32(readCmd, 12);
            Check(manual == frameCrc, "trailing CRC matches CrcGet.RevertTable(payload)");

            byte[] vol = Builders.GroupVolume(0, 5, 1, new double[] { -6.0, 0.0, 3.5, -12.7 }, 0x0001u);
            Check(vol.Length == 20, "GroupVolume frame = 20 bytes (4 + 12 + 4)");
            Check(vol[4] == Cmd.GroupVolume, "cmdIndex = 18");
            Check(vol[8] == (byte)(60 | 128), "gain[0] = 60 | 0x80 for -6.0 dB");
            Check(vol[9] == (byte)(0 | 128), "gain[1] = 0 | 0x80 for 0.0 dB (sign rule from source)");
            Check(vol[10] == 35, "gain[2] = 35 for +3.5 dB");
            // payload = transHead(4) + groupGain(4) + mGroupVolume(4) => mask sits at frame offset 12
            Check(vol[12] == 0x01 && vol[13] == 0x00 && vol[14] == 0x00 && vol[15] == 0x00,
                  "mGroupVolume mask little-endian at offset 12");

            byte[] input = Builders.Input(1, 0, 0xFFFF, 0, new ushort[16], new ushort[16]);
            Check(input.Length == 80, "InputCfg frame = 80 bytes (4 + 72 + 4)");

            // ---- cmd 11: output config — the only channel mute / level / delay / polarity ----
            Check(Marshal.SizeOf(typeof(DspOutMem)) == 68, "DspOutMem = 68 bytes");
            Check(Marshal.OffsetOf(typeof(DspOutMem), "enableOutput").ToInt32() == 0,
                  "DspOutMem.enableOutput at 0");
            Check(Marshal.OffsetOf(typeof(DspOutMem), "revertOutput").ToInt32() == 2,
                  "DspOutMem.revertOutput at 2");
            Check(Marshal.OffsetOf(typeof(DspOutMem), "gainIntValue").ToInt32() == 4,
                  "DspOutMem.gainIntValue at 4");
            Check(Marshal.OffsetOf(typeof(DspOutMem), "delayIntValue").ToInt32() == 36,
                  "DspOutMem.delayIntValue at 36");

            ushort[] og = new ushort[16];
            ushort[] od = new ushort[16];
            og[0] = GainCodec.EncodeOutputGainDb(-4.6);
            og[1] = GainCodec.EncodeOutputGainDb(-14.0);
            od[0] = GainCodec.EncodeOutputDelayMs(5.4);
            byte[] outp = Builders.Output(2, 0x00FF, 0x0002, og, od,
                                          new byte[] { 128, 128, 128, 128 }, 0x00FFu);
            Check(outp.Length == 88, "OutputCfg frame = 88 bytes (4 + 80 + 4)");
            Check(outp[4] == Cmd.OutputCfg, "cmdIndex = 11");
            Check(outp[5] == 2, "memIndex = 2");
            Check(outp[7] == 0, "pathIndex = 0 (cmd 11 addresses the whole memory)");
            Check(BitConverter.ToUInt16(outp, 8) == 0x00FF, "enableOutput LE at frame offset 8");
            Check(BitConverter.ToUInt16(outp, 10) == 0x0002, "revertOutput LE at frame offset 10");
            Check(BitConverter.ToUInt16(outp, 12) == 46, "gain[0] = 46 for -4.6 dB");
            Check(BitConverter.ToUInt16(outp, 14) == 140, "gain[1] = 140 for -14 dB");
            Check(BitConverter.ToUInt16(outp, 44) == 540, "delay[0] = 540 centi-ms for 5.4 ms");
            Check(outp[76] == 128, "groupGain[0] = 0x80 — 0 dB is never 0x00");
            Check(BitConverter.ToUInt32(outp, 80) == 0x00FFu, "mGroupVolume mask LE at offset 80");

            // Output gain is an unsigned attenuation: low values become FULL LEVEL, not silence.
            Check(GainCodec.EncodeOutputGainDb(0.0) == 0, "output gain 0 dB -> 0");
            Check(GainCodec.EncodeOutputGainDb(-4.6) == 46, "output gain -4.6 dB -> 46");
            Check(GainCodec.EncodeOutputGainDb(-40.0) == 400, "output gain -40 dB -> 400 (floor)");
            Check(GainCodec.EncodeOutputGainDb(-50.0) == 0,
                  "out-of-range -50 dB resets to 0 dB full level, NOT 500");
            Check(GainCodec.EncodeOutputGainDb(5.0) == 0, "out-of-range +5 dB resets to 0 dB");
            Check(GainCodec.EncodeOutputDelayMs(5.4) == 540, "delay 5.4 ms -> 540 centi-ms");
            Check(GainCodec.EncodeOutputDelayMs(1.239) == 123,
                  "delay floors: 1.239 ms -> 123, not 124");
            Check(GainCodec.EncodeOutputDelayMs(25.0) == 2500, "delay 25 ms -> 2500 (ceiling)");
            Check(GainCodec.EncodeOutputDelayMs(30.0) == 0, "out-of-range 30 ms resets to 0");

            // The enable mask is a mute mask: bit set = live, and absent outputs stay clear.
            Check(GainCodec.BuildEnableMask(new bool[16], 8) == 0x00FF,
                  "no mutes, 8 outputs -> 0x00FF");
            Check(GainCodec.BuildEnableMask(new bool[16], 16) == 0xFFFF,
                  "no mutes, 16 outputs -> 0xFFFF");
            bool[] mA = new bool[16]; mA[0] = true;
            Check(GainCodec.BuildEnableMask(mA, 8) == 0x00FE, "output 0 muted -> 0x00FE");
            bool[] mB = new bool[16]; mB[3] = true; mB[5] = true;
            Check(GainCodec.BuildEnableMask(mB, 8) == 0x00D7, "outputs 3 and 5 muted -> 0x00D7");
            Check(GainCodec.BuildEnableMask(new bool[16], 0) == 0,
                  "count 0 -> no bits (an untrusted count fails safe to silence)");
            Check(GainCodec.BuildEnableMask(null, 8) == 0x00FF,
                  "null mute array reads as unmuted");
            bool[] rB = new bool[16]; rB[1] = true;
            Check(GainCodec.BuildRevertMask(rB, 8) == 0x0002, "output 1 inverted -> 0x0002");
            Check(GainCodec.BuildRevertMask(rB, 1) == 0,
                  "output 1 outside a 1-output product -> no bit set");

            // sub 4 parsing: variant 2 has a different layout, so it must not be decoded as if
            // byte 2 were the output count — that would feed a wrong value into the mute mask.
            byte opc;
            Check(ReplyParse.TryGetOutPathCount(new byte[] { 1, 10, 8, 1 }, out opc) && opc == 8,
                  "sub 4 variant 1 -> output count 8 at payload offset 2");
            Check(!ReplyParse.TryGetOutPathCount(new byte[] { 2, 10, 8, 1 }, out opc),
                  "sub 4 variant 2 (boot) refused rather than read at the wrong offset");
            Check(!ReplyParse.TryGetOutPathCount(new byte[] { 1, 10, 0, 1 }, out opc),
                  "output count 0 rejected");
            Check(!ReplyParse.TryGetOutPathCount(new byte[] { 1, 10, 99, 1 }, out opc),
                  "output count 99 rejected");
            Check(!ReplyParse.TryGetOutPathCount(new byte[2], out opc), "short sub 4 body rejected");

            EqCoff[] bands = new EqCoff[31];
            for (int i = 0; i < 31; i++)
            {
                bands[i].freq = (ushort)(100 * (i + 1));
                bands[i].gain = 0x8000 | 25;      // -2.5 dB -> bit15 set
                bands[i].Q = 10;
                bands[i].CoffData = new uint[] { 1u, 2u, 3u, 4u, 5u };
            }
            byte[] eq = Builders.Eq(0, 2, bands, false);
            Check(eq.Length == 884, "Eq frame = 884 bytes (4 + 876 + 4)");
            Check(eq[4] == Cmd.EqOfOnePath, "cmdIndex = 14");
            // payload = transHead(4) + eq[0](freq2,gain2,Q1,res3,coff20) => band0 starts at frame offset 8
            Check(BitConverter.ToUInt16(eq, 8) == 100, "band0 freq at offset 8");
            Check(BitConverter.ToUInt16(eq, 10) == (0x8000 | 25), "band0 gain sign-encoding preserved");
            Check(eq[12] == 10, "band0 Q at offset 12");
            Check(eq[4 + 4 + 868] == 0, "byPass byte after 31 bands");

            Console.WriteLine("== RX parser round-trip (self-consistent, not firmware-proven) ==");
            byte[] body = new byte[16];
            for (int i = 0; i < body.Length; i++)
            {
                body[i] = (byte)(i * 7 + 1);
            }
            byte[] reply = RxParser.BuildSyntheticReply(Cmd.MainStatus, 0, 0, 0, body);
            RxFrame frame;
            int consumed;
            bool ok = RxParser.TryParse(reply, reply.Length, out frame, out consumed);
            Check(ok, "synthetic reply parsed");
            if (ok)
            {
                Check(frame.CmdIndex == Cmd.MainStatus, "cmdIndex recovered (12)");
                Check(consumed == reply.Length, "consumed whole frame");
                Check(frame.Body.Length == body.Length, "body length preserved");
                bool same = true;
                for (int i = 0; i < body.Length; i++)
                {
                    if (frame.Body[i] != body[i]) { same = false; break; }
                }
                Check(same, "body bytes intact");
            }

            // A corrupted CRC must be rejected.
            byte[] bad = (byte[])reply.Clone();
            bad[bad.Length - 1] ^= 0xFF;
            RxFrame f2;
            int c2;
            Check(!RxParser.TryParse(bad, bad.Length, out f2, out c2) || f2 == null,
                  "corrupted CRC rejected");

            // Trailing noise before a frame must be skipped.
            byte[] noisy = new byte[5 + reply.Length];
            noisy[0] = 0x11; noisy[1] = 0x22; noisy[2] = 0x55; noisy[3] = 0x00; noisy[4] = 0x33;
            Array.Copy(reply, 0, noisy, 5, reply.Length);
            RxFrame f3;
            int c3;
            bool ok3 = RxParser.TryParse(noisy, noisy.Length, out f3, out c3);
            Check(ok3 && f3 != null && f3.CmdIndex == Cmd.MainStatus, "sync recovered after noise");
            Check(ok3 && f3 != null && f3.Wire != null && f3.Wire.Length == c3 - 5,
                  "raw wire frame captured (length excludes the skipped noise)");
            if (ok3 && f3 != null && f3.Wire != null)
            {
                bool wireSame = f3.Wire.Length == reply.Length;
                for (int i = 0; wireSame && i < reply.Length; i++)
                {
                    if (f3.Wire[i] != reply[i]) { wireSame = false; }
                }
                Check(wireSame, "wire frame bytes identical to the original frame");
            }

            Console.WriteLine("== status / EQ decode ==");
            Check(Marshal.SizeOf(typeof(RunStatus)) == 16, "RunStatus = 16");
            Check(GainCodec.EncodeGroupDb(-6.0) == (byte)(60 | 128), "EncodeGroupDb(-6.0) = 0xBC");
            Check(GainCodec.DecodeGroupDb((byte)(35 | 128)) == -3.5, "DecodeGroupDb(0xA3) = -3.5");
            Check(GainCodec.DecodeGroupDb((byte)35) == 3.5, "DecodeGroupDb(0x23) = +3.5");
            Check(GainCodec.DecodeMainDb(123) == -12.3, "DecodeMainDb(123) = -12.3 dB");

            // Round-trip a synthetic RunStatus body through the reply parser.
            RunStatus st = new RunStatus();
            st.flag = 1;
            st.bMute = 0;
            st.bSource = 3;
            st.memIndex = 2;
            st.gainInt = 45;                     // -> -4.5 dB
            st.groupGainSub = new byte[] { 0x0C, (byte)(60 | 128), 0x00, (byte)(25 | 128) };
            st.multiFlag = 1;
            st.reserve2 = 0;
            st.mCrc = 0u;
            int stLen;
            byte[] stBytes = Marshalling.StructToBytes(st, out stLen);
            bool stOk;
            RunStatus parsed = ReplyParse.ParseRunStatus(stBytes, out stOk);
            Check(stOk, "RunStatus parsed from body");
            if (stOk)
            {
                Check(parsed.bSource == 3 && parsed.memIndex == 2, "source/mem recovered");
                Check(GainCodec.DecodeMainDb(parsed.gainInt) == -4.5, "main volume -4.5 dB");
                Check(GainCodec.DecodeGroupDb(parsed.groupGainSub[1]) == -6.0, "group1 -6.0 dB");
                Check((parsed.multiFlag & 1) == 1, "btOrUsb flag");
            }

            Console.WriteLine("== Q24 coefficient conversion ==");
            Check(DspMath.CalcQ24HexOfDecimal(0.0) == 0u, "Q24(0.0) = 0");
            Check(DspMath.CalcQ24HexOfDecimal(1.0) == 0x01000000u, "Q24(1.0) = 0x01000000");
            Check(DspMath.CalcQ24HexOfDecimal(0.5) == 0x00800000u, "Q24(0.5) = 0x00800000");
            Check(DspMath.CalcQ24HexOfDecimal(1.5) == 0x01800000u, "Q24(1.5) = 0x01800000");
            // -1.0 is two's complement in the low 28 bits: ~0x01000000 + 1 = 0xFF000000.
            Check(DspMath.CalcQ24HexOfDecimal(-1.0) == 0xFF000000u, "Q24(-1.0) = 0xFF000000 (two's complement)");

            // Round-trip through the original's inverse: proves the encoder agrees with the
            // vendor's own decoder rather than with hand-computed constants.
            double[] samples = new double[] { 0.0, 0.25, 0.5, 0.75, 1.0, 1.5, 2.0, 3.25, -0.5, -1.0, -1.5, -2.75, -7.125 };
            double worst = 0.0;
            for (int i = 0; i < samples.Length; i++)
            {
                uint enc = DspMath.CalcQ24HexOfDecimal(samples[i]);
                double back = DspMath.CalcQ24DecimalOfHex(enc);
                double err = Math.Abs(back - samples[i]);
                if (err > worst) { worst = err; }
            }
            Check(worst < 0.000001, "Q24 encode/decode round-trip within 1e-6 (worst=" + worst.ToString("E2") + ")");

            Console.WriteLine("== path probe fingerprint (FNV-1a) ==");
            // Standard 32-bit FNV-1a vectors, so the probe's comparison rests on a pinned
            // algorithm rather than an unverified implementation.
            Check(BridgeServer.Fnv1a(new byte[0]) == 0x811C9DC5u, "FNV-1a(\"\") = 0x811C9DC5");
            Check(BridgeServer.Fnv1a(new byte[] { 0x61 }) == 0xE40C292Cu, "FNV-1a(\"a\") = 0xE40C292C");
            Check(BridgeServer.Fnv1a(new byte[] { 0x66, 0x6F, 0x6F, 0x62, 0x61, 0x72 }) == 0xBF9CF968u,
                  "FNV-1a(\"foobar\") = 0xBF9CF968");
            Check(BridgeServer.Fnv1a(null) == BridgeServer.Fnv1a(new byte[0]), "FNV-1a(null) == FNV-1a(empty)");
            // The probe only ever asks "are these two bodies the same?", so the two
            // properties that must hold are: equal bodies -> equal hash, and any single
            // byte change -> different hash (otherwise a real difference could hide).
            byte[] a = new byte[1044];
            byte[] b = new byte[1044];
            for (int i = 0; i < a.Length; i++) { a[i] = (byte)(i * 7); b[i] = (byte)(i * 7); }
            Check(BridgeServer.Fnv1a(a) == BridgeServer.Fnv1a(b), "identical bodies hash equal");
            int differing = 0;
            for (int bit = 0; bit < 8; bit++)
            {
                b[500] = (byte)(a[500] ^ (1 << bit));
                if (BridgeServer.Fnv1a(a) != BridgeServer.Fnv1a(b)) { differing++; }
            }
            Check(differing == 8, "every single-bit change in a body changes the hash (8/8)");

            // LibHoneyWg must actually produce coefficients for a real band.
            LibHoneyWg.EQS[] flat = DspMath.FlatEq();
            EqCoff[] packed = DspMath.BuildEqBands(flat);
            Check(packed.Length == 31, "BuildEqBands returns 31 bands");
            Check(packed[0].freq == 20 && packed[20].freq == 2000, "band centre frequencies preserved");
            Check(packed[0].gain == 0 && packed[0].Q == 10, "flat band: gain 0, Q encodes 10 (1.0)");
            bool anyNonZero = false;
            for (int j = 0; j < 5; j++)
            {
                if (packed[20].CoffData[j] != 0u) { anyNonZero = true; }
            }
            Check(anyNonZero, "LibHoneyWg produced non-zero coefficients for 1 kHz");

            // Regression: EQS is a reference type, so a partially-populated array must be
            // tolerated (EqGetCoff dereferences it). This crashed the /api/eqcoef path.
            LibHoneyWg.EQS[] partial = new LibHoneyWg.EQS[31];
            partial[0] = new LibHoneyWg.EQS();
            partial[0].Fc = 1000;
            partial[0].Gain = 3f;
            partial[0].Q = 1.4f;
            partial[0].Enable = true;
            EqCoff[] partialPacked = null;
            bool threw = false;
            try { partialPacked = DspMath.BuildEqBands(partial); }
            catch (Exception) { threw = true; }
            Check(!threw, "BuildEqBands tolerates null bands");
            if (!threw)
            {
                Check(partialPacked.Length == 31, "null bands filled to 31");
                Check(partialPacked[0].freq == 1000 && partialPacked[1].freq == 25,
                      "populated band kept, null band defaulted to 25 Hz");
            }

            // EQ group read-back parse: marshal a known group and decode it.
            EqCoff[] srcBands = new EqCoff[31];
            for (int i = 0; i < 31; i++)
            {
                srcBands[i].freq = (ushort)(100 * (i + 1));
                srcBands[i].gain = (ushort)(0x8000 | 25);
                srcBands[i].Q = 12;
                srcBands[i].CoffData = new uint[] { 1u, 2u, 3u, 4u, 5u };
            }
            EqGroup grp = new EqGroup();
            grp.eq = srcBands;
            grp.byPass = 1;
            grp.reserve1 = 0; grp.reserve2 = 0; grp.reserve3 = 0;
            int grpLen;
            byte[] grpBytes = Marshalling.StructToBytes(grp, out grpLen);
            Check(grpLen == ReplyParse.EqGroupSize, "marshalled EqGroup matches EqGroupSize");
            EqCoff[] outBands;
            byte outPass;
            bool grpOk = ReplyParse.TryParseEqGroup(grpBytes, out outBands, out outPass);
            Check(grpOk, "EqGroup parsed from reply body");
            if (grpOk)
            {
                Check(outBands[20].freq == 2100, "band 20 freq recovered");
                Check(ReplyParse.DecodeEqGainDb(outBands[20].gain) == -2.5, "band 20 gain decodes to -2.5 dB");
                Check(ReplyParse.DecodeEqQ(outBands[20].Q) == 1.2, "band 20 Q decodes to 1.2");
                Check(outPass == 1, "byPass flag recovered");
            }

            Console.WriteLine("== StructEqAndPassBand layout (sub 3) ==");
            Check(Marshal.SizeOf(typeof(Coff)) == 20, "Coff = 20");
            Check(Marshal.SizeOf(typeof(PassBand)) == 168, "PassBand = 168");
            Check(ReplyParse.EqGroupSize == 872, "EqGroup = 872");
            // Cross-checked against the live capture: the device returned exactly this many
            // body bytes for a sub-3 read, so EqGroup really does sit at offset 0.
            Check(ReplyParse.EqAndPassBandSize == 1044,
                  "EqAndPassBand = 1044 (matches the live sub-3 reply body size)");

            Console.WriteLine("== StructDspCfg (sub 1) : group volume mask offset ==");
            Check(Marshal.SizeOf(typeof(MixHiLowInput)) == 32, "MixHiLowInput = 32");
            Check(Marshal.SizeOf(typeof(MixAuxOptBtInput)) == 4, "MixAuxOptBtInput = 4");
            Check(Marshal.SizeOf(typeof(MixData)) == 1280, "MixData = 1280");
            Check(Marshal.SizeOf(typeof(DspOutMem)) == 68, "DspOutMem = 68");
            Check(ReplyParse.GroupVolumePathOffset == 1964,
                  "mGroupVolumePath offset = 1964 (4 + 408 + 1280 + 272)");

            // Round-trip the mask through a synthetic sub 1 body.
            byte[] cfgBody = new byte[2100];
            BitConverter.GetBytes(0x0000003Fu).CopyTo(cfgBody, ReplyParse.GroupVolumePathOffset);
            uint maskOut;
            bool maskOk = ReplyParse.TryGetGroupVolumePath(cfgBody, out maskOut);
            Check(maskOk && maskOut == 0x3Fu, "group volume mask read back as 0x3F");
            Check(!ReplyParse.TryGetGroupVolumePath(new byte[10], out maskOut),
                  "short body rejected instead of reading past the end");

            Console.WriteLine();
            Console.WriteLine("== gain range clamping (from DP12_Param) ==");
            Check(GainCodec.GroupGainMaxDb == 6.0 && GainCodec.GroupGainMinDb == -6.0,
                  "group gain range is +/-6 dB");
            Check(GainCodec.EncodeGroupDb(6.0) == 60, "+6.0 dB -> 60 (0x3C), the maximum");
            Check(GainCodec.EncodeGroupDb(-6.0) == (byte)(60 | 128), "-6.0 dB -> 0xBC");
            Check(GainCodec.EncodeGroupDb(10.0) == 60,
                  "+10 dB clamps to 60 instead of silently becoming 100");
            Check(GainCodec.EncodeGroupDb(-99.0) == (byte)(60 | 128),
                  "-99 dB clamps to -6 instead of wrapping");
            Check(GainCodec.EncodeGroupDb(2.5) == 25, "in-range +2.5 dB is unaffected");
            Check(GainCodec.EncodeGroupDb(0.0) == 128, "0 dB still encodes as 0x80");
            Check(GainCodec.DecodeGroupDb(GainCodec.EncodeGroupDb(4.5)) == 4.5,
                  "group +4.5 round-trips");
            Check(GainCodec.DecodeGroupDb(GainCodec.EncodeGroupDb(-4.5)) == -4.5,
                  "group -4.5 round-trips");

            Console.WriteLine();
            Console.WriteLine("== input source map (DP12_Param) ==");
            Check(SourceMap.Count == 6, "DP12_SourceMax = 6");
            Check(SourceMap.Name(SourceMap.High) == "High-level", "source 0 = High-level");
            Check(SourceMap.Name(SourceMap.Low) == "Low-level", "source 1 = Low-level");
            Check(SourceMap.Name(SourceMap.Aux) == "Aux", "source 2 = Aux");
            Check(SourceMap.Name(SourceMap.Optical) == "Optical", "source 3 = Optical");
            Check(SourceMap.Name(SourceMap.BlueToothAndUsb) == "Bluetooth+USB",
                  "source 4 = Bluetooth+USB");
            Check(SourceMap.Name(SourceMap.Usb) == "USB", "source 5 = USB");
            Check(SourceMap.Name(99).StartsWith("source"), "unknown index falls back");

            Console.WriteLine();
            Console.WriteLine("== EQ gain decode (sign-magnitude, bit15) ==");
            Check(ReplyParse.DecodeEqGainDb(0x0078) == 12.0, "0x0078 -> +12.0 dB");
            Check(ReplyParse.DecodeEqGainDb(0x8032) == -5.0, "0x8032 -> -5.0 dB (bit15 = sign)");
            Check(ReplyParse.DecodeEqGainDb(0x0000) == 0.0, "0x0000 -> 0.0 dB");
            Check(ReplyParse.DecodeEqGainDb(0x8000) == 0.0, "0x8000 -> 0.0 dB (sign only)");
            Check(ReplyParse.DecodeEqQ(10) == 1.0, "Q byte 10 -> 1.0");
            // The vendor additionally range-checks (-25..10) and resets out-of-spec values
            // to 0.0 rather than clamping. We deliberately report the raw stored value, so
            // 12.0 dB above stays 12.0 here where the official app would show 0.0.
            Check(GainCodec.EqGainMaxDb == 10.0 && GainCodec.EqGainMinDb == -25.0,
                  "EQ gain range is -25 .. +10 dB");

            Console.WriteLine();
            Console.WriteLine("== EQ packing clamps to DP12_Param limits ==");
            // Nothing downstream range-checks: freq widens to a ushort, gain to a ushort with
            // bit15 as sign, Q to a byte at 0.1 units. Out-of-range input would wrap and
            // silently become a completely different filter.
            EQS[] wild = DspMath.FlatEq();
            wild[0].Fc = 65535;              // ushort max, far above 20000
            wild[0].Gain = 50f;              // > +10
            wild[0].Q = 99f;                 // > 20
            wild[1].Gain = -99f;             // < -25
            wild[1].Q = 0.01f;               // < 0.1
            EqCoff[] eqWild = DspMath.BuildEqBands(wild);
            Check(eqWild[0].freq == 20000, "fc 65535 clamps to 20000");
            Check(eqWild[0].gain == 100, "gain +50 dB clamps to +100 tenths");
            Check(eqWild[0].Q == 200, "Q 99 clamps to 200 (20.0)");
            Check(eqWild[1].gain == (ushort)(250 | 0x8000), "gain -99 dB clamps to -250 tenths");
            Check(eqWild[1].Q == 1, "Q 0.01 clamps to 0.1");
            // In-range values must pass through untouched.
            EQS[] good = DspMath.FlatEq();
            good[5].Fc = 440;
            good[5].Gain = -3.5f;
            good[5].Q = 2.4f;
            EqCoff[] eqGood = DspMath.BuildEqBands(good);
            Check(eqGood[5].freq == 440, "in-range fc passes through");
            Check(eqGood[5].gain == (ushort)(35 | 0x8000), "in-range negative gain passes through");
            Check(eqGood[5].Q == 24, "in-range Q passes through");
            // Clamping must apply to the EQS handed to EqGetCoff too, so coefficients and
            // packed fields stay derived from the same numbers.
            Check(wild[0].Fc == 20000 && wild[0].Gain == 10f && wild[0].Q == 20f,
                  "clamp mutates the EQS before EqGetCoff runs");

            Console.WriteLine();
            Console.WriteLine("== master volume write (cmd 12) ==");
            byte[] run = Builders.RunStatus(1, 0, false, -3.0, null, false);
            Check(run.Length == 28, "RunStatus frame = 28 bytes (4 + 20 + 4)");
            Check(run[4] == Cmd.MainStatus, "cmdIndex = 12");
            Check(run[8] == 165, "flag = 0xA5 at payload[0]");
            Check(run[9] == 0, "bMute = 0");
            Check(run[10] == 0 && run[11] == 1, "bSource/memIndex forwarded");
            Check(BitConverter.ToUInt16(run, 12) == 30, "gainInt = 30 for -3.0 dB");
            Check(run[14] == (byte)(0 | 128) && run[17] == (byte)(0 | 128),
                  "groupGainSub uses the 0 dB = 0x80 encoding");
            // 0 dB must clamp to gainInt 0, and the range must be enforced.
            byte[] runMax = Builders.RunStatus(0, 0, false, 0.0, null, false);
            Check(BitConverter.ToUInt16(runMax, 12) == 0, "0 dB -> gainInt 0");
            byte[] runMin = Builders.RunStatus(0, 0, false, -60.0, null, false);
            Check(BitConverter.ToUInt16(runMin, 12) == 600, "-60 dB -> gainInt 600");
            byte[] runClamped = Builders.RunStatus(0, 0, false, 12.0, null, false);
            Check(BitConverter.ToUInt16(runClamped, 12) == 0, "positive dB clamped to 0");
            byte[] runMute = Builders.RunStatus(0, 0, true, -3.0, null, false);
            Check(runMute[9] == 1, "mute flag set");

            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "ALL TESTS PASSED" : (_failures + " TEST(S) FAILED"));
            return _failures == 0 ? 0 : 1;
        }

        /// <summary>--serve: start the HTTP bridge and block until killed.</summary>
        /// <summary>
        /// Best-effort list of this machine's IPv4 addresses, for the startup banner. The
        /// phone URL changes whenever DHCP hands out a new lease, so printing a placeholder
        /// forces the user to go hunting with ipconfig.
        /// </summary>
        private static System.Collections.Generic.List<string> LanAddresses()
        {
            System.Collections.Generic.List<string> list = new System.Collections.Generic.List<string>();
            try
            {
                System.Net.NetworkInformation.NetworkInterface[] nics =
                    System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
                foreach (System.Net.NetworkInformation.NetworkInterface ni in nics)
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                    {
                        continue;
                    }
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                    {
                        continue;
                    }
                    System.Net.NetworkInformation.UnicastIPAddressInformationCollection addrs =
                        ni.GetIPProperties().UnicastAddresses;
                    foreach (System.Net.NetworkInformation.UnicastIPAddressInformation a in addrs)
                    {
                        if (a.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                        {
                            continue;
                        }
                        string ip = a.Address.ToString();
                        if (!list.Contains(ip))
                        {
                            list.Add(ip);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Enumeration is best-effort: failing to list IPs must never stop the server.
            }
            return list;
        }

        private static int Serve(string[] args)
        {
            int port = 8585;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--port" && i + 1 < args.Length)
                {
                    int parsed;
                    if (int.TryParse(args[i + 1], out parsed) && parsed > 0 && parsed < 65536)
                    {
                        port = parsed;
                    }
                    i++;
                }
            }

            string webRoot = ResolveWebRoot();
            if (webRoot == null)
            {
                Console.Error.WriteLine("web/ folder not found next to the executable or in the working directory.");
                return 2;
            }

            using (DspClient client = new DspClient())
            using (BridgeServer server = new BridgeServer(client, webRoot))
            {
                string error;
                if (!server.Start(port, out error))
                {
                    Console.Error.WriteLine(error);
                    return 2;
                }

                Console.WriteLine("ds18-bridge listening on port " + port);
                Console.WriteLine("  web root : " + webRoot);
                Console.WriteLine("  scope    : " + (server.PublicBinding
                    ? "LAN (reachable from the phone)"
                    : "this PC only (see warning above)"));
                Console.WriteLine();
                Console.WriteLine("  This PC : http://localhost:" + port + "/");
                if (server.PublicBinding)
                {
                    System.Collections.Generic.List<string> ips = LanAddresses();
                    if (ips.Count == 0)
                    {
                        Console.WriteLine("  Phone   : http://<this-PCs-IP>:" + port + "/   (run ipconfig)");
                    }
                    else
                    {
                        foreach (string ip in ips)
                        {
                            Console.WriteLine("  Phone   : http://" + ip + ":" + port + "/");
                        }
                    }
                }
                Console.WriteLine();
                Console.WriteLine("Plug in the DSP and press Connect in the page. Ctrl+C to stop.");

                System.Threading.ManualResetEvent done = new System.Threading.ManualResetEvent(false);
                Console.CancelKeyPress += delegate(object s, ConsoleCancelEventArgs e)
                {
                    e.Cancel = true;
                    done.Set();
                };
                done.WaitOne();
                Console.WriteLine("stopping.");
            }
            return 0;
        }

        /// <summary>Locates the web assets: ./web next to the exe, then the project layout.</summary>
        private static string ResolveWebRoot()
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates = new string[]
            {
                Path.Combine(exeDir, "web"),
                Path.Combine(exeDir, @"..\web"),
                Path.Combine(exeDir, @"..\..\web"),
                Path.Combine(Environment.CurrentDirectory, "web")
            };
            for (int i = 0; i < candidates.Length; i++)
            {
                string full = Path.GetFullPath(candidates[i]);
                if (Directory.Exists(full) && File.Exists(Path.Combine(full, "index.html")))
                {
                    return full;
                }
            }
            return null;
        }
    }
}
