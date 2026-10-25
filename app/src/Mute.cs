using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace DreTunes
{
    /// <summary>
    /// One planned channel-mute change, complete with the block it would send.
    ///
    /// This is a class rather than a method because the block itself is the evidence. Command 11
    /// is all-or-nothing: POSTing a partial block does not mute one channel, it rewrites all
    /// sixteen. So the plan carries every entry, and it also carries the count of fields that
    /// actually differ from the mirror - which must be one. A plan that changes more than one
    /// field is a bug caught before anything is transmitted.
    /// </summary>
    public sealed class MutePlan
    {
        public bool Ok;
        public string Why = "";

        public int Path = -1;
        public int Mem = -1;
        public bool Mute;

        public double[] Gain = new double[Channels.Cmd11Paths];
        public double[] Delay = new double[Channels.Cmd11Paths];
        public bool[] Muted = new bool[Channels.Cmd11Paths];
        public bool[] Revert = new bool[Channels.Cmd11Paths];
        public int OutPathCount = 8;

        /// <summary>Fields that differ from the mirror. A mute toggle must be exactly 1.</summary>
        public int Changed;

        /// <summary>The sentence the app may say afterwards. Never mentions a readback, because
        /// this amplifier cannot echo command 11.</summary>
        public string Note = "";

        public string RevertPath = "";
        public bool MirrorRead;
        public bool MirrorClean;
    }

    /// <summary>
    /// The one control on the Tune surface that writes.
    ///
    /// The brief forbids writing crossovers from a tune path, and command 11 has no readback, so
    /// the only thing this can honestly do is send a complete block and then say plainly that the
    /// result is a mirror, not a reading. Everything that makes that safe lives in `Plan`, which is
    /// pure: it refuses without a bridge, without a live memory reading, without a block for that
    /// memory, and when the amplifier has moved to a different memory since the block was read.
    /// </summary>
    public static class MuteBlock
    {
        private static int _busy;

        /// <summary>
        /// Decide whether a mute change may be sent, and build the whole block. No I/O, so the
        /// self-test can assert every refusal and every field.
        /// </summary>
        public static MutePlan Plan(Rig rig, int path, bool mute, int deviceMem, bool live)
        {
            MutePlan p = new MutePlan();
            p.Path = path;
            p.Mute = mute;

            if (path < 0 || path >= Channels.Cmd11Paths)
            {
                p.Why = "path " + path + " is not addressable by command 11";
                return p;
            }
            if (rig == null) { p.Why = "there is no rig state to build a block from"; return p; }
            if (!rig.BridgeUp)
            {
                p.Why = "the bridge is not answering, so there is nothing to send a block through";
                return p;
            }
            if (!live || deviceMem < 0)
            {
                p.Why = "the amplifier is not reporting which memory it is playing, so a block " +
                        "would be addressed to a guess. Nothing was sent.";
                return p;
            }
            if (rig.Mem < 0)
            {
                p.Why = "this session has no device memory reading, so the mirror cannot be matched " +
                        "to a memory. Nothing was sent.";
                return p;
            }
            if (deviceMem != rig.Mem)
            {
                p.Why = "the amplifier is now on memory " + (deviceMem + 1) + ", but the block in " +
                        "hand was read for memory " + (rig.Mem + 1) + ". Nothing was sent.";
                return p;
            }
            if (!rig.OutputKnown)
            {
                p.Why = "no command-11 block is known for memory " + (deviceMem + 1) + " this " +
                        "session. Sending one channel's mute would have to invent the other " +
                        "fifteen entries. Nothing was sent.";
                return p;
            }

            p.Mem = deviceMem;
            p.OutPathCount = rig.OutPathCount;

            for (int i = 0; i < Channels.Cmd11Paths; i++)
            {
                p.Gain[i] = rig.Gain[i];
                p.Delay[i] = rig.Delay[i];
                p.Muted[i] = rig.OMute[i];
                p.Revert[i] = rig.ORevert[i];
            }

            // A defect that sent only the touched channel would silence the other fifteen. This
            // flag replaces the copy above with zeros and must be caught by the self-test.
            if (Mutation.MuteClobbersOthers)
            {
                for (int i = 0; i < Channels.Cmd11Paths; i++)
                {
                    p.Gain[i] = 0;
                    p.Delay[i] = 0;
                    p.Muted[i] = false;
                    p.Revert[i] = false;
                }
            }

            p.Muted[path] = mute;

            for (int i = 0; i < Channels.Cmd11Paths; i++)
            {
                if (Math.Abs(p.Gain[i] - rig.Gain[i]) > 1e-9) p.Changed++;
                else if (Math.Abs(p.Delay[i] - rig.Delay[i]) > 1e-9) p.Changed++;
                else if (p.Revert[i] != rig.ORevert[i]) p.Changed++;
                else if (p.Muted[i] != rig.OMute[i]) p.Changed++;
            }

            p.Ok = true;
            p.Note = (mute ? "CH " + (path + 1) + " muted" : "CH " + (path + 1) + " unmuted") +
                     " \u2014 sent as a complete 16-entry command-11 block. The bridge's mirror " +
                     "holds it; command 11 has no device readback on this amplifier, so this is " +
                     "mirror-only and cannot be proven.";

            if (Mutation.MuteClaimsVerified)
                p.Note = (mute ? "CH " + (path + 1) + " muted" : "CH " + (path + 1) + " unmuted") +
                         " \u2014 verified by the amplifier's readback.";

            return p;
        }

        /// <summary>
        /// Re-read the playing memory, plan, write a revert point, send, then check the bridge's
        /// echo. Runs on a background thread from the button, so a slow bridge cannot freeze the
        /// window; a second tap while the first is in flight is refused rather than queued.
        /// </summary>
        public static void Send(AppState s, int path)
        {
            if (s == null || s.Rig == null) return;
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                s.Note("the previous mute command is still being sent; nothing was queued");
                return;
            }
            try
            {
                Rig rig = s.Rig;
                bool want = !(path >= 0 && path < rig.OMute.Length && rig.OMute[path]);

                // The playing memory is re-read at the moment of the click, not taken from the
                // poll that may be up to 1.2 s old: the whole point of the guard is that the
                // device cannot move between the block being built and the block being sent.
                Dictionary<string, object> status = null;
                try { status = rig.Api.Get("/api/status", 4000); }
                catch (ApiException ex)
                {
                    s.Note("mute refused: the DSP link is not answering (" + ex.Message + ")");
                    return;
                }
                bool statusOk = Json.B(status, "ok", false);
                int deviceMem = statusOk ? Json.I(status, "mem", -1) : -1;
                MutePlan p = Plan(rig, path, want, deviceMem, statusOk && deviceMem >= 0);
                if (!p.Ok)
                {
                    s.Note("mute refused: " + p.Why);
                    App.Log("mute refused: " + p.Why);
                    return;
                }

                // No revert point, no write - the same rule the swap follows, in a smaller file.
                try
                {
                    p.RevertPath = Cmd11File.Write(s.Root, p.Mem, p.OutPathCount,
                                                   rig.Gain, rig.Delay, rig.OMute, rig.ORevert);
                }
                catch (Exception ex)
                {
                    s.Note("mute refused: the revert point could not be written (" + ex.Message +
                           "). Nothing was sent.");
                    App.Log("mute refused: revert point: " + ex.Message);
                    return;
                }

                Dictionary<string, object> body = Json.Body(
                    "mem", p.Mem, "outPathCount", p.OutPathCount,
                    "gainDb", p.Gain, "delayMs", p.Delay, "mute", p.Muted, "revert", p.Revert);

                Dictionary<string, object> res = null;
                try { res = rig.Api.Post("/api/output", body, 9000); }
                catch (ApiException ex)
                {
                    s.Note("CH " + (path + 1) + " " + (want ? "mute" : "unmute") +
                           " was not accepted: " + ex.Message + ". The row still shows the old state.");
                    App.Log("mute send failed: " + ex.Message);
                    return;
                }
                if (!Json.B(res, "ok", false))
                {
                    string why = Json.S(res, "error", "no reason given");
                    s.Note("CH " + (path + 1) + " " + (want ? "mute" : "unmute") +
                           " was refused: " + why + ". The row still shows the old state.");
                    App.Log("mute refused by bridge: " + why);
                    return;
                }

                // Accepted. Update the local mirror so the row changes at once; the poll would
                // otherwise leave it looking unmuted for up to 1.2 s after a successful send.
                rig.OMute[path] = want;
                rig.SetMirrorTouch();

                Dictionary<string, object> info = null;
                try { info = rig.Api.Get("/api/info", 4000); }
                catch (ApiException) { }
                MirrorDiff md = info == null ? null : Analysis.Mirror(
                    p.Gain, p.Delay, p.Muted, p.Revert,
                    Json.DArr(info, "outputGainDb"), Json.DArr(info, "outputDelayMs"),
                    Json.BArr(info, "outputMute"), Json.BArr(info, "outputRevert"),
                    Math.Max(1, p.OutPathCount));
                p.MirrorRead = md != null && md.Read;
                p.MirrorClean = md != null && md.Clean;

                s.Note(p.Note);
                App.Log("cmd11: " + (want ? "mute " : "unmute ") + "CH " + (path + 1) +
                        " mem " + (p.Mem + 1) + " changed=" + p.Changed +
                        " mirror=" + (p.MirrorClean ? "clean" : (p.MirrorRead ? "DIFFERS" : "not read")) +
                        " revert=" + System.IO.Path.GetFileName(p.RevertPath));
                s.LogLine(p.Note + (p.MirrorClean
                    ? " The bridge's mirror agrees with the block that was sent."
                    : " The bridge's mirror does NOT agree with what was sent \u2014 treat this row as unknown."));
                s.Raise();
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        }
    }

    /// <summary>
    /// The revert point for a command-11 change.
    ///
    /// It is deliberately NOT a `tune-before-*.json`: that shape is what `apply-tune.ps1
    /// -Restore` consumes, and a file with no bands in it would be a dangerous thing to hand it.
    /// This holds only the sixteen-entry block, says so in its own `restore` string, and is small
    /// enough that the cost of a revert point never argues against taking one.
    /// </summary>
    public static class Cmd11File
    {
        public static string Write(string root, int mem, int outPathCount,
                                   double[] gain, double[] delay, bool[] mute, bool[] revert)
        {
            if (root == null) throw new IOException("no project root");
            return WriteTo(System.IO.Path.Combine(root, "evidence"), mem, outPathCount,
                           gain, delay, mute, revert);
        }

        public static string WriteTo(string dir, int mem, int outPathCount,
                                     double[] gain, double[] delay, bool[] mute, bool[] revert)
        {
            Directory.CreateDirectory(dir);

            Dictionary<string, object> d = new Dictionary<string, object>();
            d["at"] = DateTime.Now.ToString("s", CultureInfo.InvariantCulture);
            d["tag"] = "before-mute";
            d["mem"] = mem;
            d["outPathCount"] = outPathCount;
            d["gainDb"] = gain;
            d["delayMs"] = delay;
            d["mute"] = mute;
            d["revert"] = revert;
            d["restore"] = "POST gainDb/delayMs/mute/revert with mem and outPathCount to " +
                           "http://127.0.0.1:8585/api/output. This is a command-11 block only: " +
                           "it carries no EQ bands, so do not hand it to apply-tune.ps1 -Restore.";

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string path = System.IO.Path.Combine(dir, "cmd11-before-mute-" + stamp + ".json");
            int n = 0;
            while (File.Exists(path))
            {
                n++;
                path = System.IO.Path.Combine(dir, "cmd11-before-mute-" + stamp + "-" + n + ".json");
            }
            File.WriteAllText(path, Json.Write(d), new UTF8Encoding(false));
            return path;
        }
    }
}
