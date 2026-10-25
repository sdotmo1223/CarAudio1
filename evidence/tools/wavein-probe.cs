using System;
using System.Runtime.InteropServices;

// Diagnostic: which waveInOpen parameter combo fails with err 11?
class WaveInProbe
{
    [StructLayout(LayoutKind.Sequential)]
    struct WFE
    {
        public ushort wFormatTag, nChannels;
        public uint nSamplesPerSec, nAvgBytesPerSec;
        public ushort nBlockAlign, wBitsPerSample, cbSize;
    }

    [DllImport("winmm.dll")]
    static extern int waveInOpen(out IntPtr h, uint id, ref WFE f, IntPtr cb, IntPtr inst, uint open);

    [DllImport("winmm.dll")]
    static extern int waveInClose(IntPtr h);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateEvent(IntPtr a, bool m, bool i, string n);

    static int Try(IntPtr cb, uint flags, int rate, int ch, string tag)
    {
        WFE f = new WFE();
        f.wFormatTag = 1;
        f.nChannels = (ushort)ch;
        f.nSamplesPerSec = (uint)rate;
        f.wBitsPerSample = 16;
        f.nBlockAlign = (ushort)(ch * 2);
        f.nAvgBytesPerSec = (uint)(rate * ch * 2);
        IntPtr h;
        int e = waveInOpen(out h, 0xFFFFFFFF, ref f, cb, IntPtr.Zero, flags);
        Console.WriteLine(tag + " ch=" + ch + " r=" + rate + " sizeof(WFE)=" + Marshal.SizeOf(typeof(WFE)) + " -> " + e);
        if (e == 0) waveInClose(h);
        return e;
    }

    static void Main()
    {
        IntPtr evt = CreateEvent(IntPtr.Zero, true, false, null);
        Console.WriteLine("event handle = 0x" + evt.ToString("X"));

        int[] rates = { 48000, 44100 };
        foreach (int r in rates)
        {
            foreach (int ch in new int[] { 2, 1 })
            {
                Try(IntPtr.Zero, 0, r, ch, "null-cb    ");      // CALLBACK_NULL / polling
                Try(evt, 0x00010000, r, ch, "event-cb   ");      // CALLBACK_EVENT
            }
        }
    }
}
