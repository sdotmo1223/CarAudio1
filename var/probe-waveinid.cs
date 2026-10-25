// Why does waveInGetID fail after a successful waveInOpen(WAVE_MAPPER)?
//
// The page gates a run on the capture device's identity, so "which device did that handle
// actually open" has to be answerable. RTAMeter.cs asks waveInGetID for it; on 2026-10-21 that
// returned non-zero and the name fell back to "system default". This probe isolates the question:
// it opens the mapper exactly as rta.exe does, then asks the handle what it is, and prints the
// MMRESULT either way. Nothing here is used at runtime - it is a measuring instrument.
//
// build + run:  powershell -File .\var\probe-waveinid.ps1
using System;
using System.Runtime.InteropServices;
using System.Threading;

static class ProbeWaveInId
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    struct WaveInCaps
    {
        public short wMid, wPid;
        public int vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPName;
        public int dwFormats;
        public short wChannels, wReserved1;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WaveFormatEx
    {
        public short wFormatTag, nChannels;
        public int nSamplesPerSec, nAvgBytesPerSec;
        public short nBlockAlign, wBitsPerSample, cbSize;
    }

    [DllImport("winmm.dll")] static extern uint waveInGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Ansi)]
    static extern int waveInGetDevCaps(uint id, ref WaveInCaps caps, int cb);
    [DllImport("winmm.dll")]
    static extern int waveInOpen(out IntPtr hwi, uint id, ref WaveFormatEx fmt,
                                 IntPtr cb, IntPtr inst, uint flags);
    [DllImport("winmm.dll")] static extern int waveInGetID(IntPtr hwi, out uint id);
    [DllImport("winmm.dll")] static extern int waveInClose(IntPtr hwi);
    [DllImport("kernel32.dll")] static extern IntPtr CreateEvent(IntPtr a, bool b, bool c, IntPtr d);

    const uint CALLBACK_EVENT = 0x00050000;

    static string Name(uint id)
    {
        WaveInCaps c = new WaveInCaps();
        return waveInGetDevCaps(id, ref c, Marshal.SizeOf(c)) == 0 ? c.szPName : "<caps failed>";
    }

    static void Try(string label, uint id)
    {
        WaveFormatEx fmt = new WaveFormatEx();
        fmt.wFormatTag = 1; fmt.nChannels = 2; fmt.nSamplesPerSec = 48000;
        fmt.nBlockAlign = 4; fmt.nAvgBytesPerSec = 192000; fmt.wBitsPerSample = 16;
        IntPtr ev = CreateEvent(IntPtr.Zero, true, false, IntPtr.Zero);
        IntPtr hwi;
        int open = waveInOpen(out hwi, id, ref fmt, ev, IntPtr.Zero, CALLBACK_EVENT);
        Console.WriteLine(label + ": waveInOpen(" + (id == 0xFFFFFFFF ? "MAPPER" : id.ToString()) + ") -> " + open);
        if (open != 0) { return; }
        uint real;
        int gid = waveInGetID(hwi, out real);
        Console.WriteLine("   waveInGetID -> " + gid + (gid == 0 ? ("   real id = " + real + "  \"" + Name(real) + "\"") : "   (non-zero: see mmsystem.h)"));
        waveInClose(hwi);
    }

    static void Main()
    {
        uint n = waveInGetNumDevs();
        Console.WriteLine("capture devices: " + n);
        for (uint d = 0; d < n; d++) { Console.WriteLine("  [" + d + "] " + Name(d)); }
        Console.WriteLine();
        Try("mapper", 0xFFFFFFFF);
        for (uint d = 0; d < n; d++) { Try("fixed", d); }
    }
}
