using System;
using System.Runtime.InteropServices;

namespace Ds18Bridge
{
    /// <summary>
    /// CRC_Get ported verbatim from DS18 DSP_2.11.exe (decompiled AP.cs, class CRC_Get).
    /// Polynomial 0x04C11DB7 (79764919), table-driven, 4-byte groups traversed in REVERSE
    /// byte order, followed by 4 final zero-byte rounds. No final XOR.
    /// NOT interchangeable with zlib CRC32.
    /// </summary>
    public static class CrcGet
    {
        private static readonly uint[] Table = new uint[256];
        private static bool _inited;

        private static void TableInit()
        {
            const uint poly = 79764919u; // 0x04C11DB7
            for (uint i = 0; i < 256; i++)
            {
                uint msb = i << 24;
                uint acc = 0u;
                for (uint bit = 0; bit < 8; bit++)
                {
                    acc = (((msb ^ acc) & 0x80000000u) == 0) ? (acc << 1) : ((acc << 1) ^ poly);
                    msb <<= 1;
                }
                Table[i] = acc;
            }
            _inited = true;
        }

        /// <summary>Original CRC32_RevertTableWithInit. len must be a multiple of 4.</summary>
        public static uint RevertTableWithInit(int len, byte[] ptr, int startPos, uint crcInit)
        {
            uint acc = 0u;
            if (!_inited)
            {
                TableInit();
            }

            // First group: 4 bytes walked backwards, initial state injected one byte at a time.
            for (int i = startPos + 3; i >= startPos; i--)
            {
                byte b = (byte)((crcInit >> 24) & 0xFF);
                int idx = (int)((acc >> 24) & 0xFF);
                acc = ((acc << 8) | (uint)(ptr[i] ^ b)) ^ Table[idx];
                crcInit <<= 8;
            }

            int groups = len / 4;
            for (int g = 1; g < groups; g++)
            {
                for (int k = 3; k >= 0; k--)
                {
                    int idx = (int)((acc >> 24) & 0xFF);
                    acc = ((acc << 8) | ptr[startPos + 4 * g + k]) ^ Table[idx];
                }
            }

            for (int k = 0; k < 4; k++)
            {
                int idx = (int)((acc >> 24) & 0xFF);
                acc = (acc << 8) ^ Table[idx];
            }
            return acc;
        }

        /// <summary>Original CRC32_RevertTable (init = 0xFFFFFFFF). len must be a multiple of 4.</summary>
        public static uint RevertTable(int len, byte[] ptr, int startPos)
        {
            return RevertTableWithInit(len, ptr, startPos, 0xFFFFFFFFu);
        }
    }

    /// <summary>PackUsbData ported verbatim from AP.cs.</summary>
    public static class PackUsbData
    {
        /// <summary>
        /// Host -> DSP frame: 0x55 0xAA | LE16 byte-count (4-byte padded) | payload | LE32 CRC.
        /// CRC covers payload bytes only ([4 .. 4+num)), single call.
        /// </summary>
        public static byte[] PackRawInUsb(byte[] dataIn, int len, out int lenOut)
        {
            int num = ((len - 1) / 4 + 1) * 4;
            lenOut = num + 8;
            byte[] frame = new byte[num + 8];
            for (int i = 0; i < num; i++)
            {
                frame[i] = 0;
            }
            frame[0] = 85;   // 0x55
            frame[1] = 170;  // 0xAA
            frame[2] = (byte)(num & 0xFF);         // LOW byte first  (little-endian)
            frame[3] = (byte)((num >> 8) & 0xFF);  // HIGH byte second
            for (int j = 0; j < len; j++)
            {
                frame[4 + j] = dataIn[j];
            }
            uint crc = CrcGet.RevertTable(num, frame, 4);
            frame[num + 4] = (byte)(crc & 0xFF);
            frame[num + 5] = (byte)((crc >> 8) & 0xFF);
            frame[num + 6] = (byte)((crc >> 16) & 0xFF);
            frame[num + 7] = (byte)((crc >> 24) & 0xFF);
            return frame;
        }
    }

    /// <summary>StructTransHead — first 4 bytes of every payload.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct TransHead
    {
        public byte cmdIndex;
        public byte memIndex;
        public byte sourceIndex;
        public byte pathIndex;
    }

    // ---- MVP command bodies (layouts taken from AP.cs, Pack = 1) ----

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct GroupData
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public byte[] groupGain;   // 0.1 dB units, bit7 = negative
        public uint mGroupVolume;  // bitmask of the 16 outputs belonging to this group
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ReadCmdData
    {
        public byte readCmdIndex;
        public byte reserve1;
        public byte reserve2;
        public byte reserve3;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct EqCoff
    {
        public ushort freq;
        public ushort gain;
        public byte Q;
        public byte reserve1;
        public byte reserve2;
        public byte reserve3;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)]
        public uint[] CoffData;    // biquad coefficients, big-endian (byte-swapped)
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct EqGroup
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 31)]
        public EqCoff[] eq;
        public byte byPass;
        public byte reserve1;
        public byte reserve2;
        public byte reserve3;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct DspInput
    {
        public ushort enableInput;
        public ushort revertInput;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public ushort[] gainIntValue;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public ushort[] delayIntValue;
    }

    /// <summary>
    /// StructRunStatus — body of a ReadParam reply for sub-command 2 (16 bytes, Pack = 1).
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct RunStatus
    {
        public byte flag;
        public byte bMute;
        public byte bSource;
        public byte memIndex;
        public ushort gainInt;              // main volume magnitude, 0.1 dB
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public byte[] groupGainSub;         // per-group volume, 0.1 dB, bit7 = negative
        public byte multiFlag;              // bit0 = bluetooth-or-USB active
        public byte reserve2;
        public uint mCrc;
    }

    /// <summary>Gain encodings shared by status decode and volume encode.</summary>
    public static class GainCodec
    {
        // DP12_Param ranges. Out-of-range values do not fail loudly: the 0.1 dB magnitude
        // is cast straight to a byte, so anything past 6.5535 dB silently wraps around.
        public const double GroupGainMaxDb = 6.0;     // DP12_GroupGainMax
        public const double GroupGainMinDb = -6.0;    // DP12_GroupGainMin
        public const double MainGainMaxDb = 0.0;      // DP12_MainGainTrackMax
        public const double MainGainMinDb = -60.0;    // DP12_MainGainTrackMin
        public const double InputGainMaxDb = 0.0;     // DP12_InputGainTrackMax
        public const double InputGainMinDb = -20.0;   // DP12_InputGainTrackMin
        public const double OutputGainMaxDb = 0.0;    // DP12_OutputGainTrackMax
        public const double OutputGainMinDb = -40.0;  // DP12_OutputGainTrackMin
        public const double OutputGainDefaultDb = -20.0;
        public const double EqGainMaxDb = 10.0;       // DP12_EqGainMax
        public const double EqGainMinDb = -25.0;      // DP12_EqGainMin
        public const double EqQMax = 20.0;            // DP12_EqQMax
        public const double EqQMin = 0.1;             // DP12_EqQMin
        public const double InputDelayMaxMs = 25.0;   // DP12_InputDelayTrackMax
        public const double InputDelayMinMs = 0.0;    // DP12_InputDelayTrackMin

        public static double Clamp(double v, double lo, double hi)
        {
            return (v < lo) ? lo : ((v > hi) ? hi : v);
        }

        public static double ClampGroupDb(double db)
        {
            return Clamp(db, GroupGainMinDb, GroupGainMaxDb);
        }

        /// <summary>Group volume: 0.1 dB units, bit7 set when negative (UseMainStatusDspData).</summary>
        public static double DecodeGroupDb(byte enc)
        {
            double v = (enc & 0x7F) / 10.0;
            return ((enc & 0x80) != 0) ? -v : v;
        }

        /// <summary>Main volume: unsigned magnitude that is always applied as negative dB.</summary>
        public static double DecodeMainDb(ushort gainInt)
        {
            return (-1.0 * gainInt) / 10.0;
        }

        /// <summary>
        /// Group volume encode — matches DspSendGroupVolumeCmd: Abs(v*10) | (v>0 ? 0 : 0x80).
        /// Clamped to ±6 dB, because (byte)Math.Abs(db*10) would otherwise wrap.
        /// </summary>
        public static byte EncodeGroupDb(double db)
        {
            db = ClampGroupDb(db);
            return (byte)(((byte)Math.Abs(db * 10.0)) | ((db > 0.0) ? 0 : 128));
        }

        /// <summary>
        /// Output gain for cmd 11 — floor(-dB * 10): an unsigned attenuation like the master
        /// volume, NOT the bit7 sign scheme group gains use. Range is -40.0 .. 0.0 dB so the
        /// result stays within 0..400.
        ///
        /// Out-of-range follows the vendor, which assigns the MAXIMUM before converting, so
        /// both +5 and -50 come back as 0 dB. A value past the bottom of the range therefore
        /// becomes FULL LEVEL rather than silence - worth knowing before feeding this a
        /// "quiet" number by accident.
        /// </summary>
        public static ushort EncodeOutputGainDb(double db)
        {
            if (db < OutputGainMinDb || db > OutputGainMaxDb) { db = OutputGainMaxDb; }
            return (ushort)Math.Max(0.0, Math.Floor(-db * 10.0));
        }

        /// <summary>
        /// Output delay for cmd 11 — floor(ms * 100.0), hundredths of a millisecond. The
        /// scale differs from cmd 9 even though the vendor reads the same
        /// DP12_InputDelayTrack* limits for both: the output path multiplies by 100.
        /// Out-of-range resets to the MINIMUM (0 ms), matching the vendor's assignment order.
        /// </summary>
        public static ushort EncodeOutputDelayMs(double ms)
        {
            if (ms < InputDelayMinMs || ms > InputDelayMaxMs) { ms = InputDelayMinMs; }
            return (ushort)Math.Max(0.0, Math.Floor(ms * 100.0));
        }

        /// <summary>
        /// enableOutput for cmd 11 — bit i SET means output i is live. The vendor rule is
        /// `i &lt; outPathCount && !mute`, so an output the product does not have keeps its bit
        /// CLEAR: absent, rather than muted or live. Setting a bit for a nonexistent channel
        /// would invite the DSP to drive something nothing is connected to.
        /// </summary>
        public static ushort BuildEnableMask(bool[] mute, byte outPathCount)
        {
            ushort mask = 0;
            for (int i = 0; i < 16 && i < outPathCount; i++)
            {
                bool chMute = (mute != null && i < mute.Length && mute[i]);
                if (!chMute) { mask |= (ushort)(1 << i); }
            }
            return mask;
        }

        /// <summary>revertOutput for cmd 11 — bit i SET means output i is polarity-inverted.</summary>
        public static ushort BuildRevertMask(bool[] revert, byte outPathCount)
        {
            ushort mask = 0;
            for (int i = 0; i < 16 && i < outPathCount; i++)
            {
                bool inv = (revert != null && i < revert.Length && revert[i]);
                if (inv) { mask |= (ushort)(1 << i); }
            }
            return mask;
        }
    }

    /// <summary>
    /// Input source indices, from DP12_Param.DP12_High .. DP12_Usb. These were the only
    /// unlabelled values in the protocol; the names come straight from the vendor's own
    /// constants rather than from any guesswork.
    /// </summary>
    public static class SourceMap
    {
        public const int High = 0;            // DP12_High   - high-level (speaker-level) input
        public const int Low = 1;             // DP12_Low    - low-level (line-level) input
        public const int Aux = 2;             // DP12_Aux
        public const int Optical = 3;         // DP12_Optical
        public const int BlueToothAndUsb = 4; // DP12_BlueToothAndUsb
        public const int Usb = 5;             // DP12_Usb
        public const int Count = 6;           // DP12_SourceMax

        public static string Name(int idx)
        {
            switch (idx)
            {
                case High: return "High-level";
                case Low: return "Low-level";
                case Aux: return "Aux";
                case Optical: return "Optical";
                case BlueToothAndUsb: return "Bluetooth+USB";
                case Usb: return "USB";
                default: return "source " + idx;
            }
        }
    }

    /// <summary>StructCoff — a bare uint[5] wrapper, 20 bytes.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Coff
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)]
        public uint[] CoffData;
    }

    /// <summary>
    /// StructPassBand — the block that follows StructEqGroup in a sub-3 read reply.
    /// 168 bytes: 8 bytes of filter settings + 4 + 4 StructCoff (20 bytes each).
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct PassBand
    {
        public ushort lowFreq;
        public ushort highFreq;
        public byte lowSlope;
        public byte highSlope;
        public byte lowFilterType;
        public byte highFilterType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public Coff[] lowFreqCoff;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public Coff[] highFreqCoff;
    }

    // ---- StructDspCfg (ReadParam sub 1) and its nested mix tables ----

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct MixHiLowInput
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public ushort[] mixHiLowInput;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct MixAuxOptBtInput
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
        public ushort[] mixAuxOptBtInput;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct MixData
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public MixHiLowInput[] mixForHigh;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public MixHiLowInput[] mixForLow;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public MixAuxOptBtInput[] mixForAux;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public MixAuxOptBtInput[] mixForOpt;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public MixAuxOptBtInput[] mixForBtAndUsb;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public MixAuxOptBtInput[] mixForUsb;
    }

    /// <summary>
    /// The body of cmd 11. Byte-for-byte the output twin of DspInput (same 68 bytes), with
    /// these mask semantics: enableOutput bit i SET means output i is NOT muted, and outputs
    /// the product does not have leave their bit CLEAR - which is why the ProductDetail count
    /// matters when building the mask.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct DspOutMem
    {
        public ushort enableOutput;
        public ushort revertOutput;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public ushort[] gainIntValue;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public ushort[] delayIntValue;
    }

    /// <summary>
    /// The leading portion of StructDspCfg, up to and including mGroupVolumePath.
    /// Trailing fields (StructLinkGroupAndDelayGroup) are omitted deliberately: they sit
    /// AFTER mGroupVolumePath and cannot affect its offset, and the live reply body is
    /// shorter than a fully marshalled StructDspCfg would be.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct DspCfgHead
    {
        public uint hasInitFlag;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
        public DspInput[] inputCfg;
        public MixData mixData;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public DspOutMem[] memOut;
        public uint mGroupVolumePath;
    }

    /// <summary>Command IDs from Enum_DspSendCmd as observed assigned in AP.cs.</summary>
    public static class Cmd
    {
        public const byte FluxPassBand = 6;
        public const byte UserSourceMemPriority = 7;
        public const byte MixAuxOptBtCfg = 8;
        public const byte InputCfg = 9;
        public const byte MixHiLowCfg = 10;
        public const byte OutputCfg = 11;
        public const byte MainStatus = 12;
        public const byte PassBandOfOnePath = 13;
        public const byte EqOfOnePath = 14;
        public const byte ReadParam = 15;
        public const byte CfgDsp = 16;
        public const byte Upgrade = 17;
        public const byte GroupVolume = 18;
        public const byte VolumeTelemetry = 19; // observed only in device replies
    }

    public static class ReadSubCmd
    {
        public const byte BaseDsp = 1;
        public const byte MainRunStatus = 2;
        public const byte EqAndPassBand = 3;
        public const byte ProductDetail = 4;
        public const byte McuRun = 5;
        public const byte FluxEqAndPassBand = 6;
    }

    public static class Marshalling
    {
        public static byte[] StructToBytes(object structObj, out int len)
        {
            int size = Marshal.SizeOf(structObj);
            len = size;
            byte[] bytes = new byte[size];
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(structObj, ptr, false);
                Marshal.Copy(ptr, bytes, 0, size);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
            return bytes;
        }

        public static object BytesToStruct(byte[] bytes, Type type)
        {
            int size = Marshal.SizeOf(type);
            if (size > bytes.Length)
            {
                return null;
            }
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.Copy(bytes, 0, ptr, size);
                return Marshal.PtrToStructure(ptr, type);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        /// <summary>UInt32Small2Big — byte-swap (big-endian) as used for coefficients.</summary>
        public static uint UInt32Small2Big(uint small)
        {
            if (!BitConverter.IsLittleEndian)
            {
                return small;
            }
            byte[] bytes = BitConverter.GetBytes(small);
            Array.Reverse(bytes);
            return BitConverter.ToUInt32(bytes, 0);
        }
    }

    /// <summary>Builders for the four MVP commands. Payloads are byte-exact ports.</summary>
    public static class Builders
    {
        // DP12_Param.DP12_MainGainTrackMax / Min - the master volume range the app maps its
        // slider onto. gainInt is unsigned, so a positive value would wrap.
        public const double MainGainMaxDb = GainCodec.MainGainMaxDb;
        public const double MainGainMinDb = GainCodec.MainGainMinDb;

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct GroupVolumeTrans
        {
            public TransHead transHead;
            public GroupData groupData;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct ReadCmdTrans
        {
            public TransHead transHead;
            public ReadCmdData readCmdData;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct EqTrans
        {
            public TransHead transHead;
            public EqGroup eqGroup;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct InputTrans
        {
            public TransHead transHead;
            public DspInput dspInputData;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct OutputTrans
        {
            public TransHead transHead;
            public DspOutMem dspOutData;
            public GroupData groupData;
        }

        /// <summary>
        /// DspSendGroupVolumeCmd. groupGain = 0.1 dB with bit7 set when the value is negative.
        /// </summary>
        public static byte[] GroupVolume(byte mem, byte path, byte source,
                                         double[] groupVolumeDb, uint groupVolumeMask)
        {
            GroupVolumeTrans t = new GroupVolumeTrans();
            t.transHead.cmdIndex = Cmd.GroupVolume;
            t.transHead.memIndex = mem;
            t.transHead.pathIndex = path;
            t.transHead.sourceIndex = source;
            t.groupData.groupGain = new byte[4];
            for (int i = 0; i < 4; i++)
            {
                double v = (groupVolumeDb != null && i < groupVolumeDb.Length) ? groupVolumeDb[i] : 0.0;
                t.groupData.groupGain[i] = (byte)(((byte)Math.Abs(v * 10.0)) | ((v > 0.0) ? 0 : 128));
            }
            t.groupData.mGroupVolume = groupVolumeMask;

            int len;
            byte[] payload = Marshalling.StructToBytes(t, out len);
            int lenOut;
            return PackUsbData.PackRawInUsb(payload, len, out lenOut);
        }

        /// <summary>ReadDspDataSendCmd — cmd 15 with a ReadSubCmd index.</summary>
        public static byte[] ReadParam(byte subCmd, byte mem, byte source, byte path)
        {
            ReadCmdTrans t = new ReadCmdTrans();
            t.transHead.cmdIndex = Cmd.ReadParam;
            t.transHead.memIndex = mem;
            t.transHead.sourceIndex = source;
            t.transHead.pathIndex = path;
            t.readCmdData.readCmdIndex = subCmd;

            int len;
            byte[] payload = Marshalling.StructToBytes(t, out len);
            int lenOut;
            return PackUsbData.PackRawInUsb(payload, len, out lenOut);
        }

        /// <summary>
        /// DspSendEqOfOnePathConfigData — cmd 14, all 31 bands at once.
        /// gainDb uses the original encoding: 0.1 dB units with bit15 = negative.
        /// Coefficients must already be big-endian (UInt32Small2Big applied).
        /// </summary>
        public static byte[] Eq(byte mem, byte path, EqCoff[] bands, bool byPass)
        {
            EqTrans t = new EqTrans();
            t.transHead.cmdIndex = Cmd.EqOfOnePath;
            t.transHead.memIndex = mem;
            t.transHead.pathIndex = path;
            t.transHead.sourceIndex = 0;
            t.eqGroup.eq = new EqCoff[31];
            for (int i = 0; i < 31; i++)
            {
                EqCoff src = (bands != null && i < bands.Length) ? bands[i] : new EqCoff();
                if (src.CoffData == null || src.CoffData.Length != 5)
                {
                    src.CoffData = new uint[5];
                }
                t.eqGroup.eq[i] = src;
            }
            t.eqGroup.byPass = byPass ? (byte)1 : (byte)0;

            int len;
            byte[] payload = Marshalling.StructToBytes(t, out len);
            int lenOut;
            return PackUsbData.PackRawInUsb(payload, len, out lenOut);
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct PassBandTrans
        {
            public TransHead transHead;
            public PassBand passBandParam;
        }

        /// <summary>
        /// DspSendPassBandOfOnePathConfigData — cmd 13, one path's crossover pair.
        /// Scalars ride plain; coefficients must already be big-endian — DspMath
        /// BuildPassBand does the Q24 + byte-swap with the vendor's quirks baked in.
        /// No ACK from the firmware: verify with a sub-3 read, whose passband block
        /// (body + 872, 168 bytes) carries the scalars and coefficients back.
        /// </summary>
        public static byte[] PassBand(byte mem, byte path, PassBand pb)
        {
            PassBandTrans t = new PassBandTrans();
            t.transHead.cmdIndex = Cmd.PassBandOfOnePath;
            t.transHead.memIndex = mem;
            t.transHead.pathIndex = path;
            t.transHead.sourceIndex = 0;

            if (pb.lowFreqCoff == null || pb.lowFreqCoff.Length != 4)
            {
                pb.lowFreqCoff = new Coff[4];
            }
            if (pb.highFreqCoff == null || pb.highFreqCoff.Length != 4)
            {
                pb.highFreqCoff = new Coff[4];
            }
            for (int i = 0; i < 4; i++)
            {
                if (pb.lowFreqCoff[i].CoffData == null || pb.lowFreqCoff[i].CoffData.Length != 5)
                {
                    pb.lowFreqCoff[i].CoffData = new uint[5];
                }
                if (pb.highFreqCoff[i].CoffData == null || pb.highFreqCoff[i].CoffData.Length != 5)
                {
                    pb.highFreqCoff[i].CoffData = new uint[5];
                }
            }
            t.passBandParam = pb;

            int len;
            byte[] payload = Marshalling.StructToBytes(t, out len);
            int lenOut;
            return PackUsbData.PackRawInUsb(payload, len, out lenOut);
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct RunStatusTrans
        {
            public TransHead transHead;
            public RunStatus runStatus;
        }

        /// <summary>
        /// DspSendRunStatusConfigData — cmd 12. This is the control the official app uses for
        /// the MASTER volume slider (TrackBarMp6xAllVolume_ChangedEvent), and it carries the
        /// four group volumes and the mute flag along with it.
        ///
        /// gainInt is encoded as (-1.0 * mainDb * 10.0), i.e. an unsigned attenuation, because
        /// the master range is -60.0 .. 0.0 dB. flag is the fixed 0xA5 the device echoes back.
        /// </summary>
        public static byte[] RunStatus(byte mem, byte source, bool mute, double mainDb,
                                       double[] groupDb, bool btOrUsb)
        {
            if (mainDb > MainGainMaxDb) { mainDb = MainGainMaxDb; }
            if (mainDb < MainGainMinDb) { mainDb = MainGainMinDb; }

            RunStatusTrans t = new RunStatusTrans();
            t.transHead.cmdIndex = Cmd.MainStatus;
            t.transHead.memIndex = mem;
            t.transHead.sourceIndex = source;
            t.transHead.pathIndex = 0;

            t.runStatus.flag = 165;                     // 0xA5
            t.runStatus.bMute = mute ? (byte)1 : (byte)0;
            t.runStatus.bSource = source;
            t.runStatus.memIndex = mem;
            t.runStatus.gainInt = (ushort)(-1.0 * mainDb * 10.0);
            t.runStatus.groupGainSub = new byte[4];
            for (int i = 0; i < 4; i++)
            {
                double v = (groupDb != null && i < groupDb.Length) ? groupDb[i] : 0.0;
                t.runStatus.groupGainSub[i] = GainCodec.EncodeGroupDb(v);
            }
            t.runStatus.multiFlag = btOrUsb ? (byte)1 : (byte)0;
            t.runStatus.reserve2 = 0;
            t.runStatus.mCrc = 0u;

            int len;
            byte[] payload = Marshalling.StructToBytes(t, out len);
            int lenOut;
            return PackUsbData.PackRawInUsb(payload, len, out lenOut);
        }

        /// <summary>DspSendInputConfigData — cmd 9.</summary>
        public static byte[] Input(byte source, byte mem, ushort enable, ushort revert,
                                   ushort[] gains, ushort[] delays)
        {
            InputTrans t = new InputTrans();
            t.transHead.cmdIndex = Cmd.InputCfg;
            t.transHead.sourceIndex = source;
            t.transHead.memIndex = mem;
            t.transHead.pathIndex = 0;
            t.dspInputData.enableInput = enable;
            t.dspInputData.revertInput = revert;
            t.dspInputData.gainIntValue = new ushort[16];
            t.dspInputData.delayIntValue = new ushort[16];
            for (int i = 0; i < 16; i++)
            {
                if (gains != null && i < gains.Length) t.dspInputData.gainIntValue[i] = gains[i];
                if (delays != null && i < delays.Length) t.dspInputData.delayIntValue[i] = delays[i];
            }

            int len;
            byte[] payload = Marshalling.StructToBytes(t, out len);
            int lenOut;
            return PackUsbData.PackRawInUsb(payload, len, out lenOut);
        }

        /// <summary>
        /// DspSendOutputConfigData — cmd 11. Payload is 80 bytes: head(4) + DspOutMem(68)
        /// + GroupData(8), so the frame is 88 bytes on the wire.
        ///
        /// THIS COMMAND IS ALL-OR-NOTHING. It carries mute, polarity, gain and delay for all
        /// sixteen outputs plus the four group levels and the membership mask, and the DSP
        /// applies every one of them. Passing a short array here does not "leave the rest
        /// alone" — it zeroes them. The device also cannot be asked what it currently holds:
        /// no ReadParam sub-command covers output config, which is why the official app keeps
        /// its own copy and compares against it. Callers must therefore supply the complete
        /// picture for the memory they are writing, group block included.
        /// </summary>
        public static byte[] Output(byte mem, ushort enable, ushort revert,
                                    ushort[] gains, ushort[] delays,
                                    byte[] groupGain, uint groupMask)
        {
            OutputTrans t = new OutputTrans();
            t.transHead.cmdIndex = Cmd.OutputCfg;
            t.transHead.memIndex = mem;
            t.transHead.sourceIndex = 0;
            t.transHead.pathIndex = 0;
            t.dspOutData.enableOutput = enable;
            t.dspOutData.revertOutput = revert;
            t.dspOutData.gainIntValue = new ushort[16];
            t.dspOutData.delayIntValue = new ushort[16];
            for (int i = 0; i < 16; i++)
            {
                if (gains != null && i < gains.Length) t.dspOutData.gainIntValue[i] = gains[i];
                if (delays != null && i < delays.Length) t.dspOutData.delayIntValue[i] = delays[i];
            }

            t.groupData.groupGain = new byte[4];
            for (int i = 0; i < 4; i++)
            {
                t.groupData.groupGain[i] = (groupGain != null && i < groupGain.Length)
                    ? groupGain[i]
                    : GainCodec.EncodeGroupDb(0.0);   // 0 dB encodes as 0x80, never 0x00
            }
            t.groupData.mGroupVolume = groupMask;

            int len;
            byte[] payload = Marshalling.StructToBytes(t, out len);
            int lenOut;
            return PackUsbData.PackRawInUsb(payload, len, out lenOut);
        }
    }

    /// <summary>
    /// Decoders for read-reply bodies. Bodies are the marshalled vendor structs laid out
    /// after the 4-byte head; sizes were verified against the source struct definitions.
    /// </summary>
    public static class ReplyParse
    {
        public const int EqGroupSize = 872; // 31 * EqCoff(28) + byPass + 3 reserve
        public const int PassBandSize = 168;
        // StructEqAndPassBand = EqGroup + PassBand + trailing mCrc. A live sub-3 reply
        // returned exactly 1044 body bytes, confirming this layout end to end.
        public const int EqAndPassBandSize = EqGroupSize + PassBandSize + 4;

        /// <summary>
        /// Byte offset of mGroupVolumePath inside a ReadParam sub 1 (StructDspCfg) body.
        /// Computed from the struct chain and asserted by Marshal.OffsetOf in the self-test.
        ///
        /// This field is the mask of which of the 16 outputs a group-volume command applies
        /// to. It is READ FROM THE DEVICE (AP.cs UseBaseDspData), not invented by the host,
        /// so a bridge that defaults it to 0 writes volume to zero outputs and is inaudible.
        /// </summary>
        public static readonly int GroupVolumePathOffset =
            Marshal.OffsetOf(typeof(DspCfgHead), "mGroupVolumePath").ToInt32();

        /// <summary>Extracts the output mask from a sub 1 reply body.</summary>
        public static bool TryGetGroupVolumePath(byte[] body, out uint mask)
        {
            mask = 0u;
            if (body == null || body.Length < GroupVolumePathOffset + 4)
            {
                return false;
            }
            mask = BitConverter.ToUInt32(body, GroupVolumePathOffset);
            return true;
        }

        /// <summary>
        /// ReadParam sub 4 - StructProductDetail.mProductOutPathCount, the count cmd 11 masks
        /// its enable bits against (AP.cs: `i &lt; mProductDetailGot.mProductOutPathCount`).
        ///
        /// inByte[0] selects the variant exactly as UseProductDeteilData does: 1 lays
        /// StructProductDetail out from offset 0, so the count is at byte 2. Variant 2 is
        /// StructProductDetailBoot, whose layout differs - byte 2 there is NOT the output
        /// count, so that variant is reported as unread rather than decoded at the wrong
        /// offset. An unreadable count must never be dressed up as a real one, because cmd 11
        /// turns it straight into a mute mask.
        /// </summary>
        public static bool TryGetOutPathCount(byte[] body, out byte outPathCount)
        {
            outPathCount = 0;
            if (body == null || body.Length < 3) { return false; }
            if (body[0] != 1) { return false; }          // boot variant: different layout
            byte n = body[2];
            if (n == 0 || n > 16) { return false; }      // implausible for a DP12-class unit
            outPathCount = n;
            return true;
        }

        /// <summary>ReadParam sub 2: body is a StructRunStatus (16 bytes).</summary>
        public static RunStatus ParseRunStatus(byte[] body, out bool ok)
        {
            ok = false;
            RunStatus st = new RunStatus();
            if (body == null || body.Length < Marshal.SizeOf(typeof(RunStatus)))
            {
                return st;
            }
            object obj = Marshalling.BytesToStruct(body, typeof(RunStatus));
            if (obj == null)
            {
                return st;
            }
            st = (RunStatus)obj;
            ok = true;
            return st;
        }

        /// <summary>
        /// ReadParam sub 3: body begins with a StructEqGroup (872 bytes) followed by the
        /// pass-band block and a trailing CRC. Only the EQ group is decoded here.
        /// </summary>
        public static bool TryParseEqGroup(byte[] body, out EqCoff[] bands, out byte byPass)
        {
            bands = null;
            byPass = 0;
            if (body == null || body.Length < EqGroupSize)
            {
                return false;
            }

            byte[] group = new byte[EqGroupSize];
            Array.Copy(body, 0, group, 0, EqGroupSize);

            EqGroup parsed = (EqGroup)Marshalling.BytesToStruct(group, typeof(EqGroup));
            if (parsed.eq == null || parsed.eq.Length != 31)
            {
                return false;
            }

            bands = new EqCoff[31];
            for (int i = 0; i < 31; i++)
            {
                EqCoff b = parsed.eq[i];
                if (b.CoffData == null || b.CoffData.Length != 5)
                {
                    b.CoffData = new uint[5];
                }
                bands[i] = b;
            }
            byPass = parsed.byPass;
            return true;
        }

        /// <summary>Decodes an EQ band back to user-facing units.</summary>
        public static double DecodeEqGainDb(ushort enc)
        {
            double v = (enc & 0x7FFF) / 10.0;
            return ((enc & 0x8000) != 0) ? -v : v;
        }

        public static double DecodeEqQ(byte enc)
        {
            return enc / 10.0;
        }
    }

    /// <summary>A parsed DSP -> host reply.</summary>
    public sealed class RxFrame
    {
        public byte CmdIndex;
        public byte MemIndex;
        public byte SourceIndex;
        public byte PathIndex;
        public byte[] Body;  // payload without the trailing 4-byte CRC
        public byte[] Wire;  // complete frame as it appeared on EP1 IN, header + CRC included
    }

    /// <summary>
    /// Parser mirroring FuncGetNewHead + the RX CRC check in AP.cs.
    ///
    /// NOTE: the RX length field is parsed as BIG-endian word count * 4, which differs from the
    /// TX length field (little-endian byte count). This asymmetry is read directly from the
    /// decompiled source and still needs confirmation against a live capture.
    /// </summary>
    public static class RxParser
    {
        public static bool TryParse(byte[] buf, int available, out RxFrame frame, out int consumed)
        {
            frame = null;
            consumed = 0;

            for (int i = 0; i + 8 <= available; i++)
            {
                if (buf[i] != 85) // 0x55
                {
                    continue;
                }
                if (buf[i + 1] != 170) // 0xAA
                {
                    continue;
                }

                int needLen = ((buf[i + 2] << 8) + buf[i + 3]) * 4;
                if (needLen <= 4 || needLen >= 4000)
                {
                    continue;
                }
                byte cmd = buf[i + 4];
                if (cmd >= 20)
                {
                    continue;
                }
                if (i + 8 + needLen > available)
                {
                    return false; // incomplete frame, wait for more data
                }

                byte[] head = new byte[4];
                Array.Copy(buf, i + 4, head, 0, 4);

                byte[] data = new byte[needLen];
                Array.Copy(buf, i + 8, data, 0, needLen);

                // RX verification chains two calls: 4-byte head, then the body.
                uint init = CrcGet.RevertTableWithInit(4, head, 0, 0xFFFFFFFFu);
                uint computed = CrcGet.RevertTableWithInit(needLen - 4, data, 0, init);
                uint expected = BitConverter.ToUInt32(data, needLen - 4);
                if (computed != expected)
                {
                    continue; // CRC mismatch: skip this sync candidate
                }

                frame = new RxFrame();
                frame.CmdIndex = head[0];
                frame.MemIndex = head[1];
                frame.SourceIndex = head[2];
                frame.PathIndex = head[3];
                frame.Body = new byte[needLen - 4];
                Array.Copy(data, 0, frame.Body, 0, needLen - 4);
                consumed = i + 8 + needLen;
                // consumed already spans the header, body and CRC and includes the leading
                // offset i, so the frame itself is (consumed - i) bytes.
                frame.Wire = new byte[consumed - i];
                Array.Copy(buf, i, frame.Wire, 0, consumed - i);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Build a synthetic reply in the RX on-wire shape, used only for self-testing the
        /// parser (this mirrors app conventions; it does NOT prove firmware conformance).
        /// </summary>
        public static byte[] BuildSyntheticReply(byte cmdIndex, byte memIndex, byte sourceIndex,
                                                 byte pathIndex, byte[] body)
        {
            if (body == null)
            {
                body = new byte[0];
            }
            int bodyPadded = ((body.Length + 3) / 4) * 4;
            int needLen = bodyPadded + 4; // body + trailing CRC
            int words = needLen / 4;

            byte[] outBuf = new byte[8 + needLen];
            outBuf[0] = 85;
            outBuf[1] = 170;
            outBuf[2] = (byte)((words >> 8) & 0xFF); // BIG-endian word count
            outBuf[3] = (byte)(words & 0xFF);
            outBuf[4] = cmdIndex;
            outBuf[5] = memIndex;
            outBuf[6] = sourceIndex;
            outBuf[7] = pathIndex;

            for (int i = 0; i < bodyPadded; i++)
            {
                outBuf[8 + i] = (i < body.Length) ? body[i] : (byte)0;
            }

            byte[] head = new byte[] { cmdIndex, memIndex, sourceIndex, pathIndex };
            uint init = CrcGet.RevertTableWithInit(4, head, 0, 0xFFFFFFFFu);
            uint crc = CrcGet.RevertTableWithInit(bodyPadded, outBuf, 8, init);
            int crcPos = 8 + bodyPadded;
            outBuf[crcPos + 0] = (byte)(crc & 0xFF);
            outBuf[crcPos + 1] = (byte)((crc >> 8) & 0xFF);
            outBuf[crcPos + 2] = (byte)((crc >> 16) & 0xFF);
            outBuf[crcPos + 3] = (byte)((crc >> 24) & 0xFF);
            return outBuf;
        }
    }
}
