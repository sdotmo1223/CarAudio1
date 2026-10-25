// SweepTool - raw sweep measurement for the DS18 DSP.
//
// Why this exists
// ---------------
// The RTA meter stores a frozen magnitude envelope: one number per band, no time. That is
// enough to see shape and to prove a ramp arrived, but it cannot produce an arrival time, so
// it cannot align drivers. This tool works on the raw recording instead.
//
// Two ways to use it:
//
//   analyse <ref.wav> <mic.wav> <out.json>
//       One burst. Correlate the mic against a known reference (the sweep file, or a
//       loopback recording) and print the delay and the transfer function.
//
//   bursts <rec.wav> <sweep.wav> <out.json>
//       A whole measurement cycle. The recording must be 2-channel:
//           channel 0 = loopback (Stereo Mix: what the PC is playing)
//           channel 1 = microphone
//       Every occurrence of the sweep is found in the loopback, then the arrival of the
//       same burst is found in the mic channel. Measuring the whole cycle in one recording
//       matters because the two capture devices do not start at the same instant: that
//       skew is a constant within a run, so it cancels when delays are compared between
//       channels - which is exactly what time alignment does.
//
// The response is mic / loopback, which cancels the source's own response and leaves what
// the DSP controls: driver, cabin and mic.
//
// Build:  build-sweep.ps1  (csc, C# 5, no SDK)

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

static class SweepTool
{
    const double TwoPi = 6.283185307179586476925286766559;

    static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0) { return Usage(); }
            string mode = args[0].ToLowerInvariant();
            if (mode == "make") { return Make(args); }
            if (mode == "analyse" || mode == "analyze") { return Analyse(args); }
            if (mode == "bursts") { return Bursts(args); }
            if (mode == "synth") { return Synth(args); }
            return Usage();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("error: " + e.Message);
            return 1;
        }
    }

    static int Usage()
    {
        Console.Error.WriteLine("sweep-tool make   <out.wav> [seconds] [f1] [f2] [rate]");
        Console.Error.WriteLine("sweep-tool analyse <ref.wav> <mic.wav> [out.json]");
        Console.Error.WriteLine("sweep-tool analyse <rec.wav> [out.json] [refCh] [micCh]");
        Console.Error.WriteLine("sweep-tool bursts  <rec.wav> <sweep.wav> [out.json] [refCh] [micCh]");
        Console.Error.WriteLine("sweep-tool synth   <sweep.wav> <out.wav> <plan.txt>   (rehearsal fixture)");
        Console.Error.WriteLine("");
        Console.Error.WriteLine("A 2-channel recording is read as:  L = reference (loopback), R = microphone.");
        return 2;
    }

    // ---------------------------------------------------------------- make

    // Exponential (logarithmic) sweep. A log sweep spends equal time per octave, so every
    // band gets the same energy, and integrating its phase gives the closed form below.
    static int Make(string[] args)
    {
        if (args.Length < 2) { return Usage(); }
        string outPath = args[1];
        double seconds = (args.Length > 2) ? Num(args[2]) : 10.0;
        double f1 = (args.Length > 3) ? Num(args[3]) : 20.0;
        double f2 = (args.Length > 4) ? Num(args[4]) : 20000.0;
        int rate = (args.Length > 5) ? (int)Num(args[5]) : 48000;
        if (seconds <= 0 || f1 <= 0 || f2 <= f1 || rate < 8000) { throw new Exception("bad sweep parameters"); }

        int n = (int)Math.Round(seconds * rate);
        int pre = (int)(0.20 * rate);          // a little silence either side: the correlation
        int post = (int)(0.50 * rate);         // window then has room on both ends
        double[] buf = new double[pre + n + post];
        double amp = 0.30;
        double k = Math.Log(f2 / f1);
        int fade = Math.Max(1, (int)(0.020 * rate));

        for (int i = 0; i < n; i++)
        {
            double t = (double)i / rate;
            // phase(t) = 2*pi*f1*T/k * (exp(t/T*k) - 1)
            double ph = TwoPi * f1 * seconds / k * (Math.Exp(t / seconds * k) - 1.0);
            double env = 1.0;
            if (i < fade) { env = (double)i / fade; }
            else if (i > n - fade) { env = (double)(n - i) / fade; }
            buf[pre + i] = amp * env * Math.Sin(ph);
        }

        WriteWav(outPath, buf, rate);
        Console.WriteLine("wrote " + outPath + "  " + seconds.ToString("0.##") + " s  "
            + f1.ToString("0.#") + "-" + f2.ToString("0.#") + " Hz  " + rate + " Hz  "
            + buf.Length + " samples");
        return 0;
    }

    // ---------------------------------------------------------------- analyse (one burst)

    static int Analyse(string[] args)
    {
        if (args.Length < 2) { return Usage(); }

        // Two forms:  <ref.wav> <mic.wav> [json]   or   <rec.wav> [json] [refCh] [micCh]
        bool twoFile = args.Length >= 3 && LooksLikeWav(args[1]) && LooksLikeWav(args[2]);

        string recPath, outPath = null, refPath = null;
        int refCh = 0, micCh = 1;

        if (twoFile)
        {
            refPath = args[1];
            recPath = args[2];
            if (args.Length >= 4) { outPath = args[3]; }
        }
        else
        {
            recPath = args[1];
            if (args.Length >= 3) { outPath = args[2]; }
            if (args.Length >= 5) { refCh = (int)Num(args[3]); micCh = (int)Num(args[4]); }
        }

        double[] refSig, micSig;
        int rate;

        if (refPath != null)
        {
            Wav a = ReadWav(refPath);
            Wav b = ReadWav(recPath);
            rate = a.Rate;
            if (b.Rate != rate) { throw new Exception("sample rates differ (" + a.Rate + " vs " + b.Rate + ")"); }
            refSig = a.Channel(0);
            micSig = b.Channel(Math.Min(1, b.Channels - 1));
        }
        else
        {
            Wav w = ReadWav(recPath);
            rate = w.Rate;
            if (w.Channels < 2) { throw new Exception("need a 2-channel recording (L=loopback, R=mic)"); }
            refSig = w.Channel(refCh);
            micSig = w.Channel(micCh);
        }

        int n;
        double[] cr, ci;
        Corr(refSig, micSig, out cr, out ci, out n);
        double snr;
        int peak = FindPeak(cr, ci, n, out snr);
        int lag = peak;
        if (lag > n / 2) { lag -= n; }
        double sub = parabolic(cr, ci, peak, n);
        double delaySamples = lag + sub;
        double delayMs = delaySamples * 1000.0 / rate;

        // How much of the reference the recording can actually hold at this lag. The burst mode
        // allows a burst to come up 5% short before refusing; the same slack applies here, because
        // a window that stops a few samples early is a rounding artefact, not a lost measurement.
        int segLen = Math.Min(refSig.Length, micSig.Length);
        int avail = (lag >= 0) ? Math.Min(segLen, micSig.Length - lag) : 0;
        bool windowFits = (lag >= 0) && avail >= (int)(segLen * 0.95);

        double[] refWin = new double[Math.Max(0, avail)];
        double[] micWin = new double[Math.Max(0, avail)];
        for (int i = 0; i < micWin.Length; i++)
        {
            refWin[i] = refSig[i];
            micWin[i] = micSig[lag + i];
        }

        // Normalised correlation - the same test the burst mode applies, and for the same reason.
        // Peak-to-rest SNR alone is not a verdict: the maximum over many noise lags sits several
        // sigma up, so a mic hearing only road noise still reports 8-16 dB. Measured on the rig
        // 2026-10-20: a take with no sweep in it came back as 8.7 dB peak-to-rest and a 27.8 s
        // "delay". Noise lands near 5/sqrt(N); a real arrival lands near 0.1-1.0.
        // A window with no energy in it must not be divided into: when the arrival lag leaves no
        // usable window (or the mic is a digital silence) the denominator collapses to the guard and
        // the coefficient explodes - measured 2026-10-20 as "coef 1.7e12" against the 200-18k
        // reference, which reads as an overwhelming match when it means the opposite.
        double eRefWin = Energy(refWin), eMicWin = Energy(micWin);
        double coef = 0.0;
        if (eRefWin > 1e-9 && eMicWin > 1e-9)
        {
            coef = Math.Sqrt(cr[peak] * cr[peak] + ci[peak] * ci[peak]) / Math.Sqrt(eRefWin * eMicWin);
            if (coef > 1.0) { coef = 1.0; }   // normalised: a true match cannot exceed 1
        }

        // Completeness: did all of the sweep arrive, or did playback stop part way through.
        //
        // This cannot be done by looking for a quiet stretch: a driver only reproduces part of a
        // log sweep, so a tweeter is genuinely silent over the first two thirds of the window
        // (all of it below 1.5 kHz) and a low-passed stimulus is silent over the last. Measured
        // 2026-10-20: the phone played 2.5 s of the sweep and stopped, which still scored coef
        // 0.228 - a presence test alone would have passed it.
        //
        // So judge the window against the reference's own band: the reference already carries the
        // band this driver is supposed to cover (the app generates a band-limited sweep per speaker
        // type - sweeps\sweep-1500-20k-10s.wav for a tweeter). Taking the band from the reference
        // instead of from the measurement is what stops the test from agreeing with itself: a take
        // where only the first 2.5 s played produces a "passband" of 20-113 Hz, which the truncated
        // part itself satisfies, so a measured-band version of this test passes it. Measured
        // 2026-10-20, and it did pass until the band came from the reference.
        double[] bandDb = new double[0], refDb = new double[0], micDb = new double[0];
        if (micWin.Length >= 16)
        {
            Bands(refWin, micWin, micWin.Length, rate, out bandDb, out refDb, out micDb);
        }
        double peakRef = -1e9;
        for (int b = 0; b < refDb.Length; b++) { if (refDb[b] > peakRef) { peakRef = refDb[b]; } }
        bool[] inBand = new bool[refDb.Length];
        for (int b = 0; b < refDb.Length; b++) { inBand[b] = refDb[b] >= peakRef - 20.0; }

        double[] refBand = BandLimited(refWin, rate, inBand);

        const int chunks = 8;
        int cLen = micWin.Length / chunks;
        double[] expected = new double[chunks];
        double[] ratio = new double[chunks];
        double maxExpected = 0.0, bestRatio = 0.0, worstRatio = double.MaxValue;
        int judged = 0;
        if (cLen > 0)
        {
            for (int c = 0; c < chunks; c++)
            {
                double eMic = 0.0, eRef = 0.0;
                for (int i = 0; i < cLen; i++)
                {
                    double v = micWin[c * cLen + i];
                    double u = refBand[c * cLen + i];
                    eMic += v * v;
                    eRef += u * u;
                }
                expected[c] = Math.Sqrt(eRef / cLen);
                double micRms = Math.Sqrt(eMic / cLen);
                ratio[c] = micRms / Math.Max(1e-12, expected[c]);
                if (expected[c] > maxExpected) { maxExpected = expected[c]; }
            }
            for (int c = 0; c < chunks; c++)
            {
                // Only chunks where this driver should be producing something can be judged.
                if (maxExpected <= 0 || expected[c] < 0.25 * maxExpected) { continue; }
                judged++;
                if (ratio[c] > bestRatio) { bestRatio = ratio[c]; }
                if (ratio[c] < worstRatio) { worstRatio = ratio[c]; }
            }
        }
        double dropDb = (judged > 0 && bestRatio > 1e-12)
            ? 20.0 * Math.Log10(bestRatio / Math.Max(worstRatio, 1e-12)) : 0.0;
        // 20 dB down on the driver's own strongest chunk is the line: a real driver rolls off inside
        // its declared band by a few dB, but a chunk with no sweep in it at all sits at the noise
        // floor, 30 dB or more down.
        bool hole = (judged > 0) && (worstRatio < 0.10 * bestRatio);
        double coveredPct = (chunks > 0) ? (100.0 * judged / chunks) : 0.0;

        bool ok = windowFits && !hole && snr >= 6.0 && coef >= 0.08;
        string why = !windowFits ? "the arrival is not fully inside the recording"
                   : (hole ? "only part of the sweep arrived - " + dropDb.ToString("0", CultureInfo.InvariantCulture)
                            + " dB down over part of the window"
                   : (coef < 0.08 ? "the mic did not correlate with the sweep"
                   : "the peak is too close to the rest of the correlation"));

        if (ok)
        {
            // bandDb/refDb/micDb were computed above: the passband test needs them before the gate.
            // The two windows are already aligned sample for sample, so this is just the division.
        }

        StringBuilder sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append("  \"tool\": \"sweep-tool\",\n");
        sb.Append("  \"mode\": \"analyse\",\n");
        sb.Append("  \"at\": \"" + Stamp() + "\",\n");
        sb.Append("  \"input\": \"" + JsonEsc(recPath) + "\",\n");
        sb.Append("  \"rate\": " + rate + ",\n");
        sb.Append("  \"delaySamples\": " + delaySamples.ToString("0.###", CultureInfo.InvariantCulture) + ",\n");
        sb.Append("  \"delayMs\": " + delayMs.ToString("0.####", CultureInfo.InvariantCulture) + ",\n");
        sb.Append("  \"snrDb\": " + snr.ToString("0.#", CultureInfo.InvariantCulture) + ",\n");
        sb.Append("  \"coef\": " + coef.ToString("0.####", CultureInfo.InvariantCulture) + ",\n");
        sb.Append("  \"windowDropDb\": " + dropDb.ToString("0.#", CultureInfo.InvariantCulture) + ",\n");
        sb.Append("  \"windowCoveragePct\": " + coveredPct.ToString("0", CultureInfo.InvariantCulture) + ",\n");
        sb.Append("  \"ok\": " + (ok ? "true" : "false") + ",\n");
        sb.Append("  \"truncated\": " + (!windowFits ? "true" : "false") + ",\n");
        sb.Append("  \"verdict\": \"" + JsonEsc(ok ? "measured" : why) + "\",\n");
        sb.Append("  \"bands\": ");
        sb.Append(ok ? BandsJson(bandDb, refDb, micDb) : "[]");
        sb.Append("\n}\n");
        string json = sb.ToString();
        if (outPath != null) { File.WriteAllText(outPath, json, new UTF8Encoding(false)); }

        if (ok)
        {
            Console.WriteLine("delay      : " + delayMs.ToString("0.000", CultureInfo.InvariantCulture) + " ms  ("
                + delaySamples.ToString("0.#", CultureInfo.InvariantCulture) + " samples at " + rate + " Hz)");
            Console.WriteLine("confidence : " + snr.ToString("0.0", CultureInfo.InvariantCulture) + " dB peak-to-rest"
                + ", coef " + coef.ToString("0.000", CultureInfo.InvariantCulture));
            PrintBands(bandDb);
        }
        else
        {
            // No delay and no curve. Reporting either would be reporting the shape of the noise;
            // the peak is still printed so the reason is on the record.
            Console.WriteLine("delay      : NOT MEASURED - " + why);
            Console.WriteLine("peak       : " + delayMs.ToString("0.000", CultureInfo.InvariantCulture) + " ms at "
                + snr.ToString("0.0", CultureInfo.InvariantCulture) + " dB peak-to-rest, coef "
                + coef.ToString("0.000", CultureInfo.InvariantCulture) + " (a real arrival is >= 0.080, "
                + "and a whole sweep is within 15 dB across the window; this one was "
                + dropDb.ToString("0", CultureInfo.InvariantCulture) + " dB)");
            Console.WriteLine("response   : not computed - there is no sweep in this recording to show");
        }
        if (outPath != null) { Console.WriteLine("wrote " + outPath); }
        return ok ? 0 : 3;
    }

    // ---------------------------------------------------------------- bursts (a whole cycle)

    static int Bursts(string[] args)
    {
        if (args.Length < 3) { return Usage(); }
        string recPath = args[1];
        string sweepPath = args[2];
        string outPath = (args.Length > 3) ? args[3] : null;
        int refCh = (args.Length > 4) ? (int)Num(args[4]) : 0;
        int micCh = (args.Length > 5) ? (int)Num(args[5]) : 1;

        Wav rec = ReadWav(recPath);
        Wav sw = ReadWav(sweepPath);
        if (rec.Rate != sw.Rate) { throw new Exception("sample rates differ (" + rec.Rate + " vs " + sw.Rate + ")"); }
        if (rec.Channels < 2) { throw new Exception("bursts needs a 2-channel recording (L=loopback, R=mic)"); }

        int rate = rec.Rate;
        double[] lb = rec.Channel(refCh);
        double[] mic = rec.Channel(micCh);
        double[] sweep = sw.Channel(0);
        int sweepLen = sweep.Length;

        // 1. every place the loopback contains the sweep
        int n;
        double[] cr, ci;
        Corr(sweep, lb, out cr, out ci, out n);
        double[] mag = new double[n];
        double maxMag = 0;
        for (int i = 0; i < n; i++)
        {
            mag[i] = Math.Sqrt(cr[i] * cr[i] + ci[i] * ci[i]);
            if (mag[i] > maxMag) { maxMag = mag[i]; }
        }

        // A burst is a local maximum well above the floor and far enough from the last one.
        // minSep is half a sweep: two bursts cannot be closer than that in a real cycle.
        int minSep = Math.Max(1, sweepLen / 2);
        double thresh = maxMag * 0.30;
        List<int> found = new List<int>();
        for (int i = 0; i < n; i++)
        {
            if (mag[i] < thresh) { continue; }
            if (i > 0 && mag[i] < mag[i - 1]) { continue; }
            if (i < n - 1 && mag[i] < mag[i + 1]) { continue; }
            if (found.Count > 0 && i - found[found.Count - 1] < minSep)
            {
                if (mag[i] > mag[found[found.Count - 1]]) { found[found.Count - 1] = i; }
                continue;
            }
            found.Add(i);
        }

        if (found.Count == 0) { throw new Exception("no sweep found in the loopback channel (is Stereo Mix capturing the output?)"); }

        // 2. for each burst, find its arrival in the mic and measure the path
        int maxDelay = (int)(0.250 * rate);          // a car path is nowhere near 250 ms
        StringBuilder sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append("  \"tool\": \"sweep-tool\",\n");
        sb.Append("  \"mode\": \"bursts\",\n");
        sb.Append("  \"at\": \"" + Stamp() + "\",\n");
        sb.Append("  \"input\": \"" + JsonEsc(recPath) + "\",\n");
        sb.Append("  \"rate\": " + rate + ",\n");
        sb.Append("  \"sweepSamples\": " + sweepLen + ",\n");
        sb.Append("  \"bursts\": [");

        int okCount = 0;
        List<string> rows = new List<string>();
        for (int k = 0; k < found.Count; k++)
        {
            int lbStart = found[k];

            // The mic window starts at the loopback burst and is long enough to hold the
            // sweep plus the largest plausible travel time.
            int winLen = Math.Min(sweepLen + maxDelay, mic.Length - lbStart);
            if (winLen < sweepLen / 2) { continue; }
            double[] win = new double[winLen];
            Array.Copy(mic, lbStart, win, 0, winLen);

            int n2;
            double[] wr, wi;
            Corr(sweep, win, out wr, out wi, out n2);
            double snr2;
            int p2 = FindPeak(wr, wi, n2, out snr2);
            // Normalised correlation: the peak divided by what a perfect copy of the sweep
            // would produce. The SNR above is peak-to-rest, and the maximum of many noise
            // lags is several sigma, so a mic hearing only noise can still show 12-14 dB.
            // The coefficient does not have that problem: noise lands near 5/sqrt(N), a real
            // arrival near 0.1-1.0. A dead channel must not be given a fabricated delay.
            double coef = Math.Sqrt(wr[p2] * wr[p2] + wi[p2] * wi[p2])
                / Math.Max(1e-12, Math.Sqrt(Energy(sweep) * Energy(win)));
            double sub2 = parabolic(wr, wi, p2, n2);
            double micOffset = p2 + sub2;              // samples after lbStart
            if (micOffset < 0) { micOffset = 0; }
            if (micOffset > winLen - 1) { micOffset = winLen - 1; }

            // If the recording ends before the burst does, say so. Clamping the offset to
            // fit would report a delay that was never measured - worse than no answer.
            bool truncated = (micOffset + sweepLen > winLen);

            int micStart = lbStart + (int)Math.Round(micOffset);
            int segLen = Math.Min(sweepLen, mic.Length - micStart);
            // A missing few milliseconds at the end of the last burst only costs a sliver of
            // the top octave; a bigger shortfall means there is no complete burst to measure.
            bool haveTf = segLen >= (int)(sweepLen * 0.95);
            double[] bandDb = new double[0], refDb = new double[0], micDb = new double[0];
            if (haveTf)
            {
                // Transfer function for this burst: mic window at the arrival vs the loopback
                // window at the burst. Equal lengths, Hann, so the division is well behaved.
                double[] ls = new double[segLen];
                double[] ms = new double[segLen];
                Array.Copy(lb, lbStart, ls, 0, segLen);
                Array.Copy(mic, micStart, ms, 0, segLen);
                Bands(ls, ms, segLen, rate, out bandDb, out refDb, out micDb);
            }

            bool ok = haveTf && snr2 >= 6.0 && coef >= 0.02;
            if (ok) { okCount++; }

            sb.Append((k == 0) ? "\n" : ",\n");
            sb.Append("    {\"i\": " + k
                + ", \"loopbackSample\": " + lbStart
                + ", \"micSample\": " + micStart
                + ", \"offsetSamples\": " + micOffset.ToString("0.###", CultureInfo.InvariantCulture)
                + ", \"delayMs\": " + (micOffset * 1000.0 / rate).ToString("0.####", CultureInfo.InvariantCulture)
                + ", \"snrDb\": " + snr2.ToString("0.#", CultureInfo.InvariantCulture)
                + ", \"coef\": " + coef.ToString("0.####", CultureInfo.InvariantCulture)
                + ", \"ok\": " + (ok ? "true" : "false")
                + ", \"truncated\": " + (truncated ? "true" : "false")
                + ", \"bands\": " + (haveTf ? BandsJson(bandDb, refDb, micDb) : "[]") + "}");
            rows.Add("  " + k.ToString().PadLeft(2)
                + "   " + ((double)lbStart / rate).ToString("0.000", CultureInfo.InvariantCulture).PadLeft(7)
                + "   " + (ok ? (micOffset * 1000.0 / rate).ToString("0.000", CultureInfo.InvariantCulture).PadLeft(9) : "        -")
                + "   " + snr2.ToString("0.0", CultureInfo.InvariantCulture).PadLeft(5)
                + "   " + coef.ToString("0.000", CultureInfo.InvariantCulture).PadLeft(6)
                + "   " + (ok ? "ok" : (truncated ? "truncated" : "low confidence")));
        }
        sb.Append("\n  ]\n}\n");
        string json = sb.ToString();
        if (outPath != null) { File.WriteAllText(outPath, json, new UTF8Encoding(false)); }

        Console.WriteLine("bursts     : " + found.Count + "   mic ok: " + okCount);
        Console.WriteLine("  #   at (s)   delay (ms)    conf   corr");
        foreach (string row in rows) { Console.WriteLine(row); }
        if (okCount == 0) { Console.WriteLine("warning: the mic channel did not correlate with any burst - check the mic level."); }
        if (outPath != null) { Console.WriteLine("wrote " + outPath); }
        return okCount > 0 ? 0 : 3;
    }

    // ---------------------------------------------------------------- synth
    // Builds the rehearsal fixture: a cycle in which the loopback (left) carries the sweep at
    // known start times and the mic (right) carries the same sweep delayed by a known number of
    // milliseconds and scaled by a known gain. Run it back through `bursts` and every recovered
    // delay can be compared with the number that went in - the same way a real cycle is checked,
    // except the answer is known before the measurement starts.
    //
    // Plan file:
    //   seconds=130
    //   noiseDb=-70
    //   <atSeconds>,<delayMs>,<gainDb>      one line per burst; gainDb <= -100 = silent channel
    static int Synth(string[] args)
    {
        if (args.Length < 4) { return Usage(); }
        string sweepPath = args[1];
        string outPath = args[2];
        string planPath = args[3];

        Wav sw = ReadWav(sweepPath);
        double[] sweep = sw.Channel(0);
        int rate = sw.Rate;

        double seconds = 0, noiseDb = -200;
        List<double[]> plan = new List<double[]>();
        foreach (string raw in File.ReadAllLines(planPath))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) { continue; }
            if (line.StartsWith("seconds=")) { seconds = Num(line.Substring(8)); continue; }
            if (line.StartsWith("noiseDb=")) { noiseDb = Num(line.Substring(8)); continue; }
            string[] f = line.Split(',');
            if (f.Length < 3) { throw new Exception("bad plan line: " + line); }
            plan.Add(new double[] { Num(f[0].Trim()), Num(f[1].Trim()), Num(f[2].Trim()) });
        }
        if (seconds <= 0) { throw new Exception("plan needs a seconds= line"); }
        if (plan.Count == 0) { throw new Exception("plan has no bursts"); }

        int n = (int)Math.Round(seconds * rate);
        double[] left = new double[n];
        double[] right = new double[n];

        // One noise generator for both channels, seeded: the fixture has to be reproducible.
        Random rnd = new Random(12345);
        double noise = (noiseDb <= -200) ? 0 : Math.Pow(10.0, noiseDb / 20.0);
        for (int i = 0; i < n; i++)
        {
            left[i] = noise * (rnd.NextDouble() * 2 - 1);
            right[i] = noise * (rnd.NextDouble() * 2 - 1);
        }

        int placed = 0;
        foreach (double[] b in plan)
        {
            int start = (int)Math.Round(b[0] * rate);
            int delay = (int)Math.Round(b[1] * rate / 1000.0);
            double gain = (b[2] <= -100) ? 0 : Math.Pow(10.0, b[2] / 20.0);
            for (int i = 0; i < sweep.Length; i++)
            {
                int j = start + i;
                if (j >= 0 && j < n) { left[j] += sweep[i]; }
                int k = start + delay + i;
                if (gain > 0 && k >= 0 && k < n) { right[k] += sweep[i] * gain; }
            }
            placed++;
        }

        WriteWavStereo(outPath, left, right, rate);
        Console.WriteLine("wrote " + outPath + "  " + seconds.ToString("0.#") + " s  " + rate + " Hz  "
            + placed + " burst(s)  noise " + (noiseDb <= -200 ? "off" : noiseDb.ToString("0") + " dB"));
        return 0;
    }

    // ---------------------------------------------------------------- dsp bits

    static double Energy(double[] a)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++) { s += a[i] * a[i]; }
        return s;
    }

    // corr[n] = sum mic[i] * ref[i-n]   (i.e. how much the mic lags the ref)
    static void Corr(double[] refSig, double[] micSig, out double[] re, out double[] im, out int n)
    {
        n = NextPow2(refSig.Length + micSig.Length);
        double[] ar = new double[n], ai = new double[n];
        double[] br = new double[n], bi = new double[n];
        Array.Copy(refSig, ar, refSig.Length);
        Array.Copy(micSig, br, micSig.Length);
        Fft(ar, ai, false);
        Fft(br, bi, false);
        re = new double[n];
        im = new double[n];
        for (int i = 0; i < n; i++)
        {
            re[i] = br[i] * ar[i] + bi[i] * ai[i];
            im[i] = bi[i] * ar[i] - br[i] * ai[i];
        }
        Fft(re, im, true);
    }

    static int FindPeak(double[] re, double[] im, int n, out double snrDb)
    {
        int peak = 0;
        double best = -1;
        for (int i = 0; i < n; i++)
        {
            double m = re[i] * re[i] + im[i] * im[i];
            if (m > best) { best = m; peak = i; }
        }
        double restSum = 0; long restN = 0;
        for (int i = 0; i < n; i++)
        {
            if (Math.Abs(i - peak) <= 2) { continue; }
            restSum += re[i] * re[i] + im[i] * im[i];
            restN++;
        }
        double restRms = (restN > 0) ? Math.Sqrt(restSum / restN) : 1e-12;
        snrDb = 10.0 * Math.Log10(Math.Sqrt(best) / Math.Max(restRms, 1e-12));
        return peak;
    }

    // Parabolic interpolation around the peak: one sample at 48 kHz is 0.02 ms, and a
    // three-point fit gets well under that without any extra data.
    static double parabolic(double[] re, double[] im, int peak, int n)
    {
        if (peak <= 0 || peak >= n - 1) { return 0.0; }
        double a = Math.Sqrt(re[peak - 1] * re[peak - 1] + im[peak - 1] * im[peak - 1]);
        double b = Math.Sqrt(re[peak] * re[peak] + im[peak] * im[peak]);
        double c = Math.Sqrt(re[peak + 1] * re[peak + 1] + im[peak + 1] * im[peak + 1]);
        double denom = (a - 2 * b + c);
        if (Math.Abs(denom) < 1e-12) { return 0.0; }
        return 0.5 * (a - c) / denom;
    }

    static void Bands(double[] refSeg, double[] micSeg, int segLen, int rate,
        out double[] bandDb, out double[] refDb, out double[] micDb)
    {
        if (segLen < 16) { throw new Exception("segment too short"); }
        double[] rs = new double[segLen], ms = new double[segLen];
        for (int i = 0; i < segLen; i++)
        {
            double w = 0.5 - 0.5 * Math.Cos(TwoPi * i / (segLen - 1));
            rs[i] = refSeg[i] * w;
            ms[i] = micSeg[i] * w;
        }
        int nTf = NextPow2(segLen);
        double[] rr = new double[nTf], ri = new double[nTf];
        double[] mr = new double[nTf], mi = new double[nTf];
        Array.Copy(rs, rr, segLen);
        Array.Copy(ms, mr, segLen);
        Fft(rr, ri, false);
        Fft(mr, mi, false);

        int bands = 61;
        bandDb = new double[bands];
        refDb = new double[bands];
        micDb = new double[bands];
        for (int b = 0; b < bands; b++)
        {
            double fc = 20.0 * Math.Pow(2.0, b / 6.0);
            double lo = fc * Math.Pow(2.0, -1.0 / 12.0);
            double hi = fc * Math.Pow(2.0, 1.0 / 12.0);
            double refP = BandPower(rr, ri, lo, hi, nTf, rate);
            double micP = BandPower(mr, mi, lo, hi, nTf, rate);
            refDb[b] = 10.0 * Math.Log10(Math.Max(refP, 1e-30));
            micDb[b] = 10.0 * Math.Log10(Math.Max(micP, 1e-30));
            bandDb[b] = 10.0 * Math.Log10(Math.Max(micP, 1e-30) / Math.Max(refP, 1e-30));
        }
    }

    /// <summary>
    /// The reference, filtered to the bands this driver actually reproduces. Done in the frequency
    /// domain with a hard mask over the same band edges Bands() reports, which is accurate enough
    /// for an energy comparison - the passband edges are already 20 dB down, so the mask does not
    /// decide anything marginal.
    /// </summary>
    static double[] BandLimited(double[] sig, int rate, bool[] inBand)
    {
        double[] outSig = new double[sig.Length];
        if (sig.Length < 16 || inBand.Length == 0) { return outSig; }
        int n = NextPow2(sig.Length);
        double[] re = new double[n], im = new double[n];
        Array.Copy(sig, re, sig.Length);
        Fft(re, im, false);
        for (int i = 1; i < n / 2; i++)
        {
            double f = (double)i * rate / n;
            if (InPassband(f, inBand)) { continue; }
            re[i] = 0.0; im[i] = 0.0;
            re[n - i] = 0.0; im[n - i] = 0.0;
        }
        Fft(re, im, true);
        // The inverse divides by n, so the result comes back at the same scale as the input.
        Array.Copy(re, outSig, sig.Length);
        return outSig;
    }

    /// <summary>Band b is centred at 20 * 2^(b/6) Hz, six bands per octave - the edges Bands() uses.</summary>
    static bool InPassband(double f, bool[] inBand)
    {
        if (f < 20.0) { return false; }
        int b = (int)Math.Round(6.0 * Math.Log(f / 20.0, 2.0));
        if (b < 0 || b >= inBand.Length) { return false; }
        return inBand[b];
    }

    static double BandPower(double[] re, double[] im, double lo, double hi, int n, int rate)
    {
        double sum = 0; int count = 0;
        for (int i = 1; i < n / 2; i++)
        {
            double f = (double)i * rate / n;
            if (f < lo) { continue; }
            if (f > hi) { break; }
            sum += re[i] * re[i] + im[i] * im[i];
            count++;
        }
        if (count == 0) { return 0; }
        return sum / count;
    }

    static string BandsJson(double[] bandDb, double[] refDb, double[] micDb)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("[");
        for (int b = 0; b < bandDb.Length; b++)
        {
            double fc = 20.0 * Math.Pow(2.0, b / 6.0);
            if (b > 0) { sb.Append(","); }
            sb.Append("\n      {\"f\": " + fc.ToString("0.#", CultureInfo.InvariantCulture)
                + ", \"db\": " + bandDb[b].ToString("0.##", CultureInfo.InvariantCulture)
                + ", \"refDb\": " + refDb[b].ToString("0.##", CultureInfo.InvariantCulture)
                + ", \"micDb\": " + micDb[b].ToString("0.##", CultureInfo.InvariantCulture) + "}");
        }
        sb.Append("\n    ]");
        return sb.ToString();
    }

    static void PrintBands(double[] bandDb)
    {
        Console.WriteLine("response   :");
        for (int b = 0; b < bandDb.Length; b += 3)
        {
            double fc = 20.0 * Math.Pow(2.0, b / 6.0);
            Console.WriteLine("   " + fc.ToString("0").PadLeft(6) + " Hz  "
                + bandDb[b].ToString("0.0", CultureInfo.InvariantCulture).PadLeft(7) + " dB");
        }
    }

    static string Stamp()
    {
        return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    static int NextPow2(int n)
    {
        int p = 1;
        while (p < n) { p <<= 1; }
        return p;
    }

    // Iterative radix-2 Cooley-Tukey. n must be a power of two.
    static void Fft(double[] re, double[] im, bool inverse)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) { j ^= bit; }
            j ^= bit;
            if (i < j)
            {
                double t = re[i]; re[i] = re[j]; re[j] = t;
                t = im[i]; im[i] = im[j]; im[j] = t;
            }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = TwoPi / len * (inverse ? 1 : -1);
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double curR = 1, curI = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = i + k + len / 2;
                    double ur = re[a], ui = im[a];
                    double vr = re[b] * curR - im[b] * curI;
                    double vi = re[b] * curI + im[b] * curR;
                    re[a] = ur + vr; im[a] = ui + vi;
                    re[b] = ur - vr; im[b] = ui - vi;
                    double nr = curR * wr - curI * wi;
                    curI = curR * wi + curI * wr;
                    curR = nr;
                }
            }
        }
        if (inverse)
        {
            for (int i = 0; i < n; i++) { re[i] /= n; im[i] /= n; }
        }
    }

    // ---------------------------------------------------------------- wav

    class Wav
    {
        public int Rate;
        public int Channels;
        public double[][] Data;      // [channel][sample], normalised to +-1

        public double[] Channel(int i)
        {
            if (i < 0 || i >= Channels) { throw new Exception("channel " + i + " not present"); }
            return Data[i];
        }
    }

    static bool LooksLikeWav(string path)
    {
        return path.Length > 4 && path.ToLowerInvariant().EndsWith(".wav");
    }

    static double Num(string s)
    {
        return double.Parse(s, CultureInfo.InvariantCulture);
    }

    static string JsonEsc(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    static Wav ReadWav(string path)
    {
        byte[] all = File.ReadAllBytes(path);
        if (all.Length < 12 || Encoding.ASCII.GetString(all, 0, 4) != "RIFF"
            || Encoding.ASCII.GetString(all, 8, 4) != "WAVE")
        {
            throw new Exception("not a RIFF/WAVE file: " + path);
        }

        int pos = 12;
        int fmt = 0, channels = 0, bits = 0, rate = 0;
        int dataOff = -1, dataLen = 0;

        while (pos + 8 <= all.Length)
        {
            string id = Encoding.ASCII.GetString(all, pos, 4);
            int size = BitConverter.ToInt32(all, pos + 4);
            int body = pos + 8;
            if (id == "fmt ")
            {
                fmt = BitConverter.ToInt16(all, body);
                channels = BitConverter.ToInt16(all, body + 2);
                rate = BitConverter.ToInt32(all, body + 4);
                bits = BitConverter.ToInt16(all, body + 14);
            }
            else if (id == "data")
            {
                dataOff = body;
                dataLen = size;
                if (dataLen <= 0 || dataOff + dataLen > all.Length) { dataLen = all.Length - dataOff; }
            }
            pos = body + size + (size & 1);
        }

        if (dataOff < 0 || channels < 1 || rate < 8000) { throw new Exception("bad WAV header: " + path); }

        int bytesPer = bits / 8;
        int frames = dataLen / (bytesPer * channels);
        double[][] data = new double[channels][];
        for (int c = 0; c < channels; c++) { data[c] = new double[frames]; }

        for (int i = 0; i < frames; i++)
        {
            for (int c = 0; c < channels; c++)
            {
                int o = dataOff + (i * channels + c) * bytesPer;
                double v;
                if (fmt == 3 && bits == 32)
                {
                    v = BitConverter.ToSingle(all, o);
                }
                else if (bits == 16)
                {
                    v = BitConverter.ToInt16(all, o) / 32768.0;
                }
                else if (bits == 24)
                {
                    int b0 = all[o], b1 = all[o + 1], b2 = all[o + 2];
                    int s = (b2 << 16) | (b1 << 8) | b0;
                    if ((s & 0x800000) != 0) { s |= unchecked((int)0xFF000000); }
                    v = s / 8388608.0;
                }
                else if (bits == 32)
                {
                    v = BitConverter.ToInt32(all, o) / 2147483648.0;
                }
                else
                {
                    throw new Exception("unsupported bit depth: " + bits);
                }
                data[c][i] = v;
            }
        }

        Wav w = new Wav();
        w.Rate = rate;
        w.Channels = channels;
        w.Data = data;
        return w;
    }

    static void WriteWav(string path, double[] samples, int rate)
    {
        int n = samples.Length;
        int dataLen = n * 2;
        using (BinaryWriter bw = new BinaryWriter(File.Create(path)))
        {
            bw.Write(Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(36 + dataLen);
            bw.Write(Encoding.ASCII.GetBytes("WAVE"));
            bw.Write(Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16);
            bw.Write((short)1);          // PCM
            bw.Write((short)1);          // mono
            bw.Write(rate);
            bw.Write(rate * 2);          // byte rate
            bw.Write((short)2);          // block align
            bw.Write((short)16);         // bits
            bw.Write(Encoding.ASCII.GetBytes("data"));
            bw.Write(dataLen);
            for (int i = 0; i < n; i++)
            {
                double v = samples[i];
                if (v > 0.9999) { v = 0.9999; }
                if (v < -0.9999) { v = -0.9999; }
                bw.Write((short)Math.Round(v * 32767.0));
            }
        }
    }

    static void WriteWavStereo(string path, double[] left, double[] right, int rate)
    {
        int n = Math.Min(left.Length, right.Length);
        int dataLen = n * 4;
        using (BinaryWriter bw = new BinaryWriter(File.Create(path)))
        {
            bw.Write(Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(36 + dataLen);
            bw.Write(Encoding.ASCII.GetBytes("WAVE"));
            bw.Write(Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16);
            bw.Write((short)1);          // PCM
            bw.Write((short)2);          // stereo
            bw.Write(rate);
            bw.Write(rate * 4);          // byte rate
            bw.Write((short)4);          // block align
            bw.Write((short)16);         // bits
            bw.Write(Encoding.ASCII.GetBytes("data"));
            bw.Write(dataLen);
            for (int i = 0; i < n; i++)
            {
                double l = left[i], r = right[i];
                if (l > 0.9999) { l = 0.9999; }
                if (l < -0.9999) { l = -0.9999; }
                if (r > 0.9999) { r = 0.9999; }
                if (r < -0.9999) { r = -0.9999; }
                bw.Write((short)Math.Round(l * 32767.0));
                bw.Write((short)Math.Round(r * 32767.0));
            }
        }
    }
}
