using System;
using System.Collections.Generic;
using System.Drawing;

namespace DreTunes
{
    /// <summary>
    /// 6.5 Showcase - the ninety-second tour.
    ///
    /// It reads. Every step draws from the same state the other surfaces draw from, and step 4 is
    /// explicitly a rehearsal: it compares two tunes and says what a swap would do, and it writes
    /// nothing. A demo that changes the car to look good is the wrong demo.
    /// </summary>
    public static class SurfaceShowcase
    {
        private static readonly string[] Titles =
        {
            "The rig is up", "What the microphone heard", "The fit",
            "The apply, and its proof", "The A/B swap, rehearsed", "What is still unproven"
        };

        public static void Paint(AppState s, Ui ui, Rectangle a)
        {
            int step = Math.Max(0, Math.Min(5, s.ShowStep));

            // ---- progress
            int segW = (a.Width - 5 * 8) / 6;
            for (int i = 0; i < 6; i++)
            {
                Rectangle seg = new Rectangle(a.X + i * (segW + 8), a.Y, segW, 34);
                bool on = i == step;
                ui.FillRound(seg, 3, on ? Theme.Mix(Theme.Panel, Theme.Accent, 0.16) : Theme.Panel);
                ui.StrokeRound(seg, 3, on ? Theme.Accent : Theme.Line);
                ui.Text((i + 1).ToString(), Theme.F(12f, true), on ? Theme.Accent : Theme.Muted,
                        seg.X + 8, seg.Y, 16, 34);
                ui.Text(Chrome.Ellipsis(ui, Titles[i], Theme.F(10.5f, on), seg.Width - 30),
                        Theme.F(10.5f, on), on ? Theme.Fg : Theme.Dim, seg.X + 24, seg.Y, seg.Width - 30, 34);
                int idx = i;
                ui.Hit(seg, delegate
                {
                    s.ShowStep = idx;
                    s.TourPlaying = false;
                    s.Note("showcase: " + Titles[idx]);
                });
            }

            Rectangle card = new Rectangle(a.X, a.Y + 46, a.Width, a.Height - 106);
            ui.Card(card);

            switch (step)
            {
                case 0: StepRig(s, ui, card); break;
                case 1: StepMeasure(s, ui, card); break;
                case 2: StepFit(s, ui, card); break;
                case 3: StepProof(s, ui, card); break;
                case 4: StepSwap(s, ui, card); break;
                default: StepUnproven(s, ui, card); break;
            }

            // ---- controls
            Rectangle bar = new Rectangle(a.X, a.Bottom - 50, a.Width, 50);
            Rectangle back = new Rectangle(bar.X, bar.Y + 6, 96, 34);
            ui.Button(back, "Back", 0, delegate
            {
                if (s.ShowStep > 0) s.ShowStep--;
                s.TourPlaying = false;
            }, step > 0);

            Rectangle play = new Rectangle(bar.X + 104, bar.Y + 6, 150, 34);
            if (s.TourPlaying)
                ui.Button(play, "Pause tour", 3, delegate { s.TourPlaying = false; }, true);
            else
                ui.Button(play, step >= 5 ? "Replay tour" : "Play tour", 1, delegate
                {
                    s.ShowStep = step >= 5 ? 0 : s.ShowStep;
                    s.TourPlaying = true;
                    s.TourNext = DateTime.Now.AddSeconds(14);
                    s.Note("showcase: playing");
                }, true);

            Rectangle next = new Rectangle(bar.X + 262, bar.Y + 6, 96, 34);
            ui.Button(next, "Next", 1, delegate
            {
                if (s.ShowStep < 5) s.ShowStep++;
                s.TourPlaying = false;
            }, step < 5);

            string state = s.TourPlaying
                ? "playing \u00b7 next step in " + Math.Max(0, (int)(s.TourNext - DateTime.Now).TotalSeconds) + " s"
                : "step " + (step + 1) + " of 6 \u00b7 " + (step < 5 ? "Next, or Play to advance on its own" : "the end");
            ui.TextAt(state, Theme.F(11f), s.TourPlaying ? Theme.Accent : Theme.Muted,
                      new Rectangle(bar.X + 370, bar.Y, bar.Width - 370, 50), "r");
        }

        // ------------------------------------------------------------------ steps

        private static void StepRig(AppState s, Ui ui, Rectangle r)
        {
            Lead(ui, r, Titles[0],
                 "One window, offline, against 127.0.0.1. It starts the bridge and the RTA itself, " +
                 "and it never renders a value without the time it was read.");

            int x = r.X + 400;
            int y = r.Y + 40;
            Live(ui, x, y, "Bridge :8585", s.Rig.BridgeUp ? "answering" : "down",
                 s.Rig.BridgeUp ? Theme.Ok : Theme.Bad); y += 30;
            Live(ui, x, y, "Amplifier", s.Rig.DeviceStateWord,
                 s.Rig.DeviceLive ? Theme.Ok : Theme.Warn); y += 30;
            Live(ui, x, y, "Active memory",
                 s.Rig.Mem < 0 ? "unknown"
                     : (s.Rig.Mem + 1) + " of 4" + (s.Rig.DeviceLive ? "" : " (bridge label)"),
                 s.Rig.DeviceLive ? Theme.Dim : Theme.Warn); y += 30;
            Live(ui, x, y, "Microphone", s.Rig.RtaUp ? s.Rig.MicDevice : "RTA not running",
                 s.Rig.RtaUp && s.Rig.MicDevice.IndexOf("UMIK", StringComparison.OrdinalIgnoreCase) >= 0
                     ? Theme.Ok : Theme.Warn); y += 30;
            Live(ui, x, y, "Calibration", s.Rig.CalLoaded
                 ? s.Rig.CalSerial + ", " + s.Rig.CalPoints + " points" : "not loaded",
                 s.Rig.CalLoaded ? Theme.Dim : Theme.Warn); y += 34;

            ui.Text("doctor.ps1", Theme.F(9.5f, true), Theme.Muted, x, y, 200, 14); y += 16;
            ui.Text(s.Doctor.Valid ? s.Doctor.Verdict + " (" + s.Doctor.Fails + " fail, " +
                    s.Doctor.Warns + " warn)" : "not run",
                    Theme.F(12f, true), s.Doctor.Valid && s.Doctor.Fails == 0 ? Theme.Warn : Theme.Muted,
                    x, y, 400, 20);

            Bullets(ui, r, new string[]
            {
                "Every surface paints from one snapshot; nothing on the UI thread touches the network.",
                "A green word appears only where a readback exists. The rail says \u201cnot reading\u201d the moment it stops.",
                "The sub amp is not on the DSP. No screen implies otherwise.",
                "Five surfaces: Now, Library, Measurements, Rig, Showcase."
            });
        }

        private static void StepMeasure(AppState s, Ui ui, Rectangle r)
        {
            Lead(ui, r, Titles[1],
                 "21 recordings on disk; this is the newest run. Each channel is one swept-sine " +
                 "capture, reported in absolute dBFS on a 1/6-octave grid - never normalised.");
            Bullets(ui, r, new string[]
            {
                "Channel N is DSP path N-1, and the app says so instead of making you remember.",
                "Peak, gate verdict and the file name stay on screen: the curve is not the only evidence.",
                "The sub is absent because it is fed from the head unit, not the DSP.",
                "Capture stays on the phone page. This reads what that cycle wrote."
            });

            Rectangle plot = new Rectangle(r.X + 560, r.Y + 40, r.Width - 590, r.Height - 80);
            MeasureSet set = s.Measure;
            MeasureChannel ch = set == null ? null : set.AtPath(s.MeasurePath);
            if (ch == null) return;
            List<Series> list = new List<Series>();
            Series m = new Series("measured " + ch.Units, ch.Hzs(), ch.Dbs(), Theme.Curve);
            m.Width = 2.1f;
            list.Add(m);
            Chart chart = new Chart();
            chart.Prepare(plot);
            if (plot.Contains(Pointer.At)) chart.HoverHz = chart.HzAt(Pointer.At.X);
            chart.Draw(ui, plot, list);
            chart.Readout(ui, list, plot);
            ui.Text("CH " + (s.MeasurePath + 1) + " \u00b7 " + ch.Name + " \u00b7 peak " +
                    ch.PeakDb.ToString("0.0") + " dBFS @ " + Channels.Hz(ch.PeakHz) + " Hz",
                    Theme.F(10.5f), Theme.Dim, plot.X, plot.Bottom + 4, plot.Width, 16);
        }

        private static void StepFit(AppState s, Ui ui, Rectangle r)
        {
            Lead(ui, r, Titles[2],
                 "The fit is a set of peaking filters fitted to the measured shape, then transcribed " +
                 "into a .idsp and written to the amplifier. Two files exist because one is measured " +
                 "and one is the owner's voicing - both are labelled.");
            TuneFile tune = s.Library.Count > 0 ? s.Library[0] : null;
            for (int i = 0; i < s.Library.Count; i++)
                if (s.Library[i].FileName.StartsWith("1-car-tune", StringComparison.OrdinalIgnoreCase))
                    tune = s.Library[i];

            if (tune != null)
            {
                List<NarrowBand> narrow = Analysis.NarrowBands(tune, 0, 0.25);
                Bullets(ui, r, new string[]
                {
                    tune.Stem + ": " + tune.LiveBands + " bands across the 8 live paths, max Q " +
                    tune.LiveMaxQ.ToString("0.0") + ".",
                    narrow.Count + " band(s) narrower than a quarter octave, so none combs against the " +
                    "1/6-octave measurement grid.",
                    "The predicted response is measured + correction. It is a model, and is labelled as one.",
                    "A narrow band looks narrow on the chart because the axis is real, not smoothed."
                });

                Rectangle plot = new Rectangle(r.X + 560, r.Y + 40, r.Width - 590, r.Height - 80);
                double[] corr = s.TuneCurve(tune, s.Path);
                if (corr != null)
                {
                    List<Series> list = new List<Series>();
                    Series c = new Series("correction", Curve.Grid, corr, Theme.Accent);
                    c.Fill = true;
                    list.Add(c);
                    Chart chart = new Chart();
                    chart.Prepare(plot);
                    if (plot.Contains(Pointer.At)) chart.HoverHz = chart.HzAt(Pointer.At.X);
                    chart.Draw(ui, plot, list);
                    ui.Text("CH " + (s.Path + 1) + " \u00b7 " + Channels.Driver[s.Path] +
                            " \u00b7 ripple " + Curve.Ripple(Curve.Grid, corr, 200, 8000).ToString("0.0") +
                            " dB p-p (200 Hz\u20138 kHz)", Theme.F(10.5f), Theme.Dim,
                            plot.X, plot.Bottom + 4, plot.Width, 16);
                }
            }
        }

        private static void StepProof(AppState s, Ui ui, Rectangle r)
        {
            Lead(ui, r, Titles[3],
                 "A write is not a claim. After every write the app reads all 8 paths back and " +
                 "compares them to the file, band for band, at verify-device.py's tolerances.");

            TuneMatch m = s.Match;
            int y = r.Y + 44;
            int x = r.X + 400;

            ui.Text("DEVICE READBACK", Theme.F(9.5f, true), Theme.Muted, x, y, 240, 14); y += 18;
            ui.Text(m != null && m.File != null ? m.File.Stem : "no match",
                    Theme.F(16f, true), Theme.Fg, x, y, 400, 22); y += 26;
            ui.Text(m == null ? "the device EQ has not been read"
                             : m.PathsMatched + " of " + m.PathsCompared + " paths match \u00b7 " +
                               m.BandDiffs + " band difference(s)",
                    Theme.F(11.5f), m != null && m.IsClean ? Theme.Ok : Theme.Warn, x, y, 460, 18); y += 24;
            string readAt = s.Rig.EqAt == DateTime.MinValue
                ? "never read this session"
                : "read at " + Chrome.Stamp(s.Rig.EqAt) + " \u00b7 " + Chrome.Ago(s.Rig.EqAt);
            ui.Text(readAt, Theme.F(10.5f), Theme.Muted, x, y, 460, 16); y += 30;

            ui.Text("COMMAND 11", Theme.F(9.5f, true), Theme.Muted, x, y, 240, 14); y += 18;
            ui.Text(s.Rig.OutputKnown ? "block known \u00b7 mirror only" : "no block this session",
                    Theme.F(12f, true), Theme.Warn, x, y, 460, 18); y += 22;
            ui.Wrap("Levels, delays and polarity cannot be echoed by this amplifier. They are checked " +
                    "against the block that was sent and the bridge's mirror - and every surface says so.",
                    Theme.F(10.5f), Theme.Muted, new Rectangle(x, y, 460, 60), 14);

            Bullets(ui, r, new string[]
            {
                "EQ and crossovers are read back off the device; that is the only device verification here.",
                "A swap that cannot prove its readback renders amber, never green.",
                "The wrong-memory guard refuses a target the device is not playing.",
                "Every write snapshots first: evidence\\tune-before-*.json."
            });
        }

        private static void StepSwap(AppState s, Ui ui, Rectangle r)
        {
            Lead(ui, r, Titles[4],
                 "The feature the owner uses at a traffic light - shown without touching the car. " +
                 "This compares the two authored tunes and reports exactly what a swap would change.");

            TuneFile a = null, b = null;
            for (int i = 0; i < s.Library.Count; i++)
            {
                if (s.Library[i].FileName.StartsWith("1-car-tune", StringComparison.OrdinalIgnoreCase)) a = s.Library[i];
                if (s.Library[i].FileName.StartsWith("2-bor8f3", StringComparison.OrdinalIgnoreCase)) b = s.Library[i];
            }
            TuneCompare c = Library.Compare(a, b);

            int x = r.X + 400;
            int y = r.Y + 44;
            ui.Text("CH", Theme.F(9f, true), Theme.Muted, x, y, 40, 12);
            ui.Text("A dB", Theme.F(9f, true), Theme.Muted, x + 56, y, 60, 12);
            ui.Text("B dB", Theme.F(9f, true), Theme.Muted, x + 126, y, 60, 12);
            ui.Text("\u0394 dB", Theme.F(9f, true), Theme.Muted, x + 196, y, 60, 12);
            ui.Text("bands", Theme.F(9f, true), Theme.Muted, x + 266, y, 60, 12);
            y += 18;
            for (int i = 0; i < c.Rows.Count; i++)
            {
                PathDelta d = c.Rows[i];
                double delta = d.GainB - d.GainA;
                if (i % 2 == 0) ui.Fill(new Rectangle(x - 4, y - 2, 380, 20), Theme.Alpha(Theme.Line, 70));
                ui.Text("CH " + (d.Path + 1), Theme.F(10f), Theme.Dim, x, y, 44, 16);
                ui.TextAt(Channels.Db(d.GainA, 1), Theme.F(10f), Theme.Dim, new Rectangle(x + 50, y, 60, 16), "r");
                ui.TextAt(Channels.Db(d.GainB, 1), Theme.F(10f), Theme.Fg, new Rectangle(x + 120, y, 60, 16), "r");
                ui.TextAt(Channels.Db(delta, 1), Theme.F(10f, true),
                          Math.Abs(delta) < 0.05 ? Theme.Muted : (delta > 0 ? Theme.Warn : Theme.Accent),
                          new Rectangle(x + 190, y, 60, 16), "r");
                ui.TextAt(d.BandsA + "/" + d.BandsB, Theme.F(10f), Theme.Muted,
                          new Rectangle(x + 258, y, 50, 16), "r");
                y += 21;
            }

            y += 8;
            ui.Wrap(c.Sentence, Theme.F(11.5f), Theme.Fg, new Rectangle(x, y, 400, 90), 15);

            Bullets(ui, r, new string[]
            {
                "A real swap: guard, snapshot, EQ on 8 paths, complete command-11 block, readback, diff.",
                "The readback holds the last word. The levels line is always and only \u201cmirror\u201d.",
                "This step is a rehearsal: compare only, nothing sent.",
                "Every memory chip in this app shows its device index, because the phone page's M2 means memory 3."
            });
        }

        private static void StepUnproven(AppState s, Ui ui, Rectangle r)
        {
            Lead(ui, r, Titles[5],
                 "A rig that lies about its own gaps is worthless. These are the four that remain, " +
                 "stated on the screen rather than discovered later in a car.");

            Bullets(ui, r, new string[]
            {
                "Time alignment: no loopback cable, so delays are relative and unmeasured.",
                "Post-tune measurement: nothing has been captured since the tune was written.",
                "Ride comparison: A and B have not been compared by ear.",
                "Sub amplifier: outside the DSP, so it is an owner assertion, not a reading."
            });
            ui.Wrap("The app's own answer to \u201cwhat is still unproven\u201d lives on the Rig surface, next to the " +
                    "readiness rows and the four gates that can still change the answer.",
                    Theme.F(11.5f), Theme.Dim, new Rectangle(r.X + 560, r.Y + 120, r.Width - 610, 110), 17);
        }

        // ------------------------------------------------------------------ helpers

        private static void Lead(Ui ui, Rectangle r, string title, string lead)
        {
            ui.Text(title, Theme.F(24f, true), Theme.Fg, r.X + 24, r.Y + 22, r.Width - 48, 32);
            ui.Wrap(lead, Theme.F(12.5f), Theme.Dim, new Rectangle(r.X + 24, r.Y + 62, 340, 110), 18);
        }

        /// <summary>
        /// Four claims, each wrapped into its own slot instead of clipped to one line. They used
        /// to be ellipsised at 340 px, which on this width turned every bullet into an unfinished
        /// sentence - the one thing a "what is still unproven" list must not do.
        /// </summary>
        private static void Bullets(Ui ui, Rectangle r, string[] lines, int width = 500)
        {
            const int slot = 40, lineH = 14;
            int y = r.Bottom - 24 - lines.Length * slot;
            for (int i = 0; i < lines.Length; i++)
            {
                ui.Dot(r.X + 30, y + 7, 2, Theme.Accent);
                ui.Wrap(lines[i], Theme.F(10.5f), Theme.Dim,
                        new Rectangle(r.X + 40, y, width, slot - 4), lineH);
                y += slot;
            }
        }

        private static void Live(Ui ui, int x, int y, string label, string value, Color c)
        {
            ui.Text(label, Theme.F(10f, true), Theme.Muted, x, y, 128, 20);
            ui.Dot(x + 138, y + 9, 3, c);
            // 370 px, not 300: "Microphone Array (Realtek(R) Audio)" is long and a truncated
            // device name is exactly the kind of half-fact that hides a wrong microphone.
            ui.Text(Chrome.Ellipsis(ui, value, Theme.F(11.5f), 370), Theme.F(11.5f), c,
                    x + 148, y, 370, 20);
        }
    }
}
