namespace DreTunes
{
    /// <summary>
    /// Seven defects the self-test can inject into the real code paths, so `--selftest --mutant`
    /// can prove the suite is capable of failing.
    ///
    /// This exists because a comparator that has never been observed to fail has verified
    /// nothing - the same principle doctor.ps1 -SelfTest applies to the crossover check. The flags
    /// are set only by Program.cs when the command line asks for a mutant run, and no UI path
    /// reads or writes them. If a flag ever leaks into a normal run, the self-test's own
    /// expectations fail loudly, which is the safe direction.
    /// </summary>
    public static class Mutation
    {
        /// <summary>Analysis.Diff returns a clean result without comparing anything.</summary>
        public static bool CleanAlways;

        /// <summary>The wrong-memory guard allows every target.</summary>
        public static bool AllowAlways;

        /// <summary>Every peaking filter reports 0 dB, so the curve mirror cannot agree.</summary>
        public static bool FlatCurve;

        /// <summary>The Tune table claims its five columns are writable.</summary>
        public static bool CrossoverWritable;

        /// <summary>The passband readback swaps the high-pass and low-pass edges.</summary>
        public static bool SwapPassBands;

        /// <summary>Every click on the Tune table lands one channel too low.</summary>
        public static bool RowOffset;

        /// <summary>The channel-mute block carries only the touched channel, zeroing the other
        /// fifteen entries - which is what a partial command-11 write would actually do.</summary>
        public static bool MuteClobbersOthers;

        /// <summary>The mute result claims the device confirmed it, which command 11 cannot do.</summary>
        public static bool MuteClaimsVerified;
    }
}
