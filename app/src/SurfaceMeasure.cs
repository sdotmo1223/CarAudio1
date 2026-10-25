using System;
using System.Collections.Generic;
using System.Drawing;

namespace DreTunes
{
    /// <summary>
    /// 6.3 Measurements - the evidence, read from the capture folders.
    ///
    /// Nothing here captures. The run cycle belongs to the phone page and stays there; this
    /// surface reads what that cycle already wrote and puts three things on one axis: what the
    /// microphone heard (absolute dBFS, never normalised), what the tune predicts after the
    /// correction, and the correction itself - on its own axis, in dB, because it is a different
    /// unit and drawing them together would be the lie this product is against.
    /// </summary>
    public static class SurfaceMeasure
    {
        public static void Paint(AppState s, Ui ui, Rectangle a)
        {
            Rectangle left = new Rectangle(a.X, a.Y, 290, a.Height);
            PaintRuns(s, ui, new Rectangle(left.X, left.Y, left.Width, 150));
            PaintChannels(s, ui, new Rectangle(left.X, left.Y + 160, left.Width, 264));
            PaintArtefacts(s, ui, new Rectangle(left.X, left.Y + 434, left.Width, a.Height - 434));

            int rx = left.Right + 12;
            int rw = a.Right - rx;
            MeasureSet set = s.Measure;
            MeasureChannel ch = set == null ? null : set.AtPath(s.MeasurePath);
            if (ch == null && set != null && set.Channels.Count > 0) ch = set.Channels[0];

            PaintIdentity(s, ui, new Rectangle(rx, a.Y, rw, 112), set, ch);

            TuneFile tune = CorrectionTune(s, set);
            double[] corr = null;
            if (tune != null)
            {
                TuneMemory m = tune.Mem(0);
                TuneOutput o = m == null ? null : m.Out(s.MeasurePath);
                if (o != null) corr = Curve.PartCurve(o.Bands, Curve.Grid);
            }

            PaintMeasured(s, ui, new Rectangle(rx, a.Y + 120, rw, 232), ch, tune, corr);
            PaintCorrection(s, ui, new Rectangle(rx, a.Y + 360, rw, 118), corr, tune, s.MeasurePath);
            PaintBands(s, ui, new Rectangle(rx, a.Y + 486, rw, a.Height - 486), tune, s.MeasurePath);
        }

        private static TuneFile CorrectionTune(AppState s, MeasureSet set)
        {
            // The measured tune is the one built from the 19:12 run. Every other folder is
            // earlier evidence, and pointing it at tune 1 would attribute a correction to it
            // that was never applied.
            if (s.Library.Count == 0) return null;
            for (int i = 0; i < s.Library.Count; i++)
                if (s.Library[i].FileName.StartsWith("1-car-tune", StringComparison.OrdinalIgnoreCase))
                    return s.Library[i];
            return s.Library[0];
        }

        // ------------------------------------------------------------------ left

        private static void PaintRuns(AppState s, Ui ui, Rectangle r)
        {
            ui.Card(r);
            Chrome.Section(ui, "CAPTURE RUNS \u00b7 READ-ONLY", r.X + 14, r.Y + 11, r.Width - 28, Theme.Dim);
            int y = r.Y + 30;
            for (int i = 0; i < s.Measures.Count; i++)
            {
                MeasureSet set = s.Measures[i];
                Rectangle row = new Rectangle(r.X + 8, y, r.Width - 16, 50);
                bool on = s.MeasureSet == i;
                if (on) { ui.FillRound(row, 3, Theme.Mix(Theme.Panel, Theme.Accent, 0.10)); ui.StrokeRound(row, 3, Theme.Accent); }
                ui.Text(Chrome.Ellipsis(ui, set.Name, Theme.F(12f, on), r.Width - 30),
                        Theme.F(12f, on), on ? Theme.Fg : Theme.Dim, row.X + 8, row.Y + 4, row.Width - 16, 18);
                ui.Text(set.Channels.Count + " channel(s)  \u00b7  " +
                        (set.Captured == DateTime.MinValue ? "undated" : set.Captured.ToString("d MMM HH:mm")) +
                        (set.GateFails > 0 ? "  \u00b7  " + set.GateFails + " gate fail(s)" : ""),
                        Theme.F(9.5f), set.GateFails > 0 ? Theme.Warn : Theme.Muted,
                        row.X + 8, row.Y + 24, row.Width - 16, 14);
                int idx = i;
                ui.Hit(row, delegate { s.MeasureSet = idx; s.MeasurePath = 0; s.Note("measurement run: " + set.Name); });
                y += 52;
            }
            if (s.Measures.Count == 0)
                ui.Text("No runs in measurements\\.", Theme.F(11.5f), Theme.Muted, r.X + 14, r.Y + 36, r.Width - 28, 16);
        }

        private static void PaintChannels(AppState s, Ui ui, Rectangle r)
        {
            ui.Card(r);
            Chrome.Section(ui, "CHANNELS \u00b7 CH 1 = PATH 0", r.X + 14, r.Y + 11,
                           r.Width - 28, Theme.Dim);
            MeasureSet set = s.Measure;
            int y = r.Y + 28;
            if (set == null)
            {
                ui.Text("No run selected.", Theme.F(11.5f), Theme.Muted, r.X + 14, y, r.Width - 28, 16);
                return;
            }
            for (int i = 0; i < set.Channels.Count && y + 25 < r.Bottom - 22; i++)
            {
                MeasureChannel c = set.Channels[i];
                int path = c.Path >= 0 ? c.Path : i;
                Rectangle row = new Rectangle(r.X + 8, y, r.Width - 16, 25);
                bool on = s.MeasurePath == path;
                if (on) ui.FillRound(row, 3, Theme.Mix(Theme.Panel, Theme.Accent, 0.10));
                ui.Text("CH " + (path + 1), Theme.F(10.5f, true), on ? Theme.Accent : Theme.Dim,
                        row.X + 6, row.Y, 44, 25);
                ui.Text(Chrome.Ellipsis(ui, c.Name, Theme.F(10.5f), 96), Theme.F(10.5f),
                        on ? Theme.Fg : Theme.Dim, row.X + 48, row.Y, 100, 25);
                ui.TextAt(c.PeakDb.ToString("0.0") + " dBFS", Theme.F(10f),
                          c.HasGate && !c.GateOk ? Theme.Warn : Theme.Muted,
                          new Rectangle(row.Right - 86, row.Y, 80, 25), "r");
                int p = path;
                ui.Hit(row, delegate
                {
                    s.MeasurePath = p;
                    s.Note("measurement channel " + (p + 1) + " (" + c.Name + ")");
                });
                y += 26;
            }
            ui.Text("One file per channel. The sub is not here: it is fed from the head unit.",
                    Theme.F(9.5f), Theme.Muted, r.X + 14, r.Bottom - 20, r.Width - 28, 14);
        }

        private static void PaintArtefacts(AppState s, Ui ui, Rectangle r)
        {
            ui.Card(r);
            Chrome.Section(ui, "WHAT WOULD MAKE THIS A POSITION", r.X + 14, r.Y + 11,
                           r.Width - 28, Theme.Warn);

            TuneFile tune = s.Library.Count > 0 ? s.Library[0] : null;
            for (int i = 0; i < s.Library.Count; i++)
                if (s.Library[i].FileName.StartsWith("1-car-tune", StringComparison.OrdinalIgnoreCase))
                    tune = s.Library[i];

            int narrow = 0;
            List<string> shared = new List<string>();
            if (tune != null)
            {
                narrow = Analysis.NarrowBands(tune, 0, 0.25).Count;
                shared = Analysis.SharedNarrow(tune, 0);
            }

            int y = r.Y + 30;
            string line1 = narrow + " band(s) in " + (tune == null ? "the tune" : tune.Stem) +
                           " are narrower than \u00bc octave.";
            ui.Wrap(line1, Theme.F(10.5f), narrow > 0 ? Theme.Warn : Theme.Dim,
                    new Rectangle(r.X + 14, y, r.Width - 28, 32), 14);
            y += 32;

            string line2 = shared.Count == 0
                ? "No identical narrow filter appears on two different drivers."
                : shared.Count + " shared narrow filter(s) - one measurement may have been copied.";
            ui.Wrap(line2, Theme.F(10.5f), shared.Count > 0 ? Theme.Warn : Theme.Dim,
                    new Rectangle(r.X + 14, y, r.Width - 28, 46), 14);
            y += 48;

            ui.Wrap("Documented case: the source voicing carried a deep ~1.5 kHz notch on both 3.5\u2033 pairs. " +
                    "Tune 2 replaced it with a broad \u22125/\u22126 dB scoop, treating it as a microphone-position " +
                    "null rather than a property of the car (DSB\\B-TUNE-REVIEW.md).",
                    Theme.F(9.5f), Theme.Muted, new Rectangle(r.X + 14, y, r.Width - 28, r.Bottom - y - 8), 13);
        }

        // ------------------------------------------------------------------ right

        private static void PaintIdentity(AppState s, Ui ui, Rectangle r, MeasureSet set, MeasureChannel ch)
        {
            ui.Card(r);
            if (ch == null)
            {
                Chrome.Section(ui, "NO CAPTURE SELECTED", r.X + 14, r.Y + 11, 300, Theme.Dim);
                ui.Text("Pick a run and a channel on the left.", Theme.F(12f), Theme.Dim,
                        r.X + 14, r.Y + 36, r.Width - 28, 18);
                return;
            }

            int path = ch.Path >= 0 ? ch.Path : s.MeasurePath;
            ui.Text("CHANNEL " + (path + 1) + "  \u00b7  " + Channels.Driver[Math.Max(0, Math.Min(7, path))],
                    Theme.F(20f, true), Theme.Fg, r.X + 14, r.Y + 10, r.Width - 28, 26);
            Chrome.Section(ui, (set == null ? "" : set.Name.ToUpperInvariant()) +
                           "  \u00b7  CAPTURED " + (ch.Captured == DateTime.MinValue ? "UNDATED" :
                           ch.Captured.ToString("yyyy-MM-dd HH:mm").ToUpperInvariant()) +
                           "  \u00b7  " + ch.Units + " (ABSOLUTE, NOT SPL)",
                           r.X + 14, r.Y + 38, r.Width - 28, Theme.Muted);

            int cx = r.X + 14;
            int cy = r.Y + 58;
            cx += ui.Badge(cx, cy, "PEAK " + ch.PeakDb.ToString("0.0") + " dBFS @ " +
                                   Channels.Hz(ch.PeakHz) + " Hz", Theme.Dim) + 6;
            if (ch.HasGate)
            {
                string word = ch.GateOk ? "GATE OK" : "GATE FAIL";
                cx += ui.Badge(cx, cy, word, ch.GateOk ? Theme.Ok : Theme.Bad) + 6;
                if (!ch.GateOk && ch.GateReason.Length > 0)
                    ui.Text(Chrome.Ellipsis(ui, ch.GateReason, Theme.F(10f), r.Width - cx + r.X - 20),
                            Theme.F(10f), Theme.Warn, cx + 2, cy + 2, r.Right - cx - 16, 20);
            }
            else
            {
                ui.Badge(cx, cy, "NO VERDICT IN FILE", Theme.Muted);
            }

            ui.Text(ch.Points.Count + " points  \u00b7  " + ch.Grid + "  \u00b7  " + ch.File,
                    Theme.F(10.5f), Theme.Muted, r.X + 14, r.Y + 88, r.Width - 28, 16);
        }

        private static void PaintMeasured(AppState s, Ui ui, Rectangle r, MeasureChannel ch,
                                          TuneFile tune, double[] corr)
        {
            ui.Card(r);
            Chrome.Section(ui, "MEASURED, AND WHAT THE CORRECTION PREDICTS", r.X + 14, r.Y + 11,
                           r.Width - 28, Theme.Dim);

            if (ch == null)
            {
                ui.Text("No capture to draw.", Theme.F(12f), Theme.Dim, r.X + 14, r.Y + 40, r.Width - 28, 18);
                return;
            }

            double[] f = ch.Hzs();
            double[] db = ch.Dbs();
            List<Series> list = new List<Series>();
            Series meas = new Series("measured", f, db, Theme.Curve);
            meas.Width = 2.1f;
            list.Add(meas);

            if (corr != null)
            {
                double[] predicted = new double[db.Length];
                for (int i = 0; i < db.Length; i++) predicted[i] = db[i] + Curve.At(Curve.Grid, corr, f[i]);
                Series pred = new Series("predicted", f, predicted, Theme.Accent);
                pred.Width = 1.4f;
                pred.Dash = true;
                list.Add(pred);
            }

            Chart chart = new Chart();
            chart.DbMin = -90;
            chart.DbMax = 0;
            chart.Auto = true;
            chart.Unit = "dBFS";
            Rectangle plot = new Rectangle(r.X + 10, r.Y + 30, r.Width - 250, r.Height - 40);
            chart.Prepare(plot);
            if (plot.Contains(Pointer.At)) chart.HoverHz = chart.HzAt(Pointer.At.X);
            chart.Draw(ui, plot, list);
            chart.Readout(ui, list, plot);

            int lx = r.Right - 232;
            int ly = r.Y + 34;
            Dot(ui, lx, ly, Theme.Curve, "measured " + ch.Units);
            ui.Text("the microphone, absolute", Theme.F(10f), Theme.Muted, lx + 16, ly + 12, 200, 14);
            ly += 32;
            if (corr != null)
            {
                Dot(ui, lx, ly, Theme.Accent, "predicted");
                ui.Wrap("measured + the correction this path carries. A model, not a re-measurement.",
                        Theme.F(10f), Theme.Muted, new Rectangle(lx + 16, ly + 12, 204, 32), 13);
                ly += 40;
            }
            ly += 6;
            ui.Text("predicted-versus-measured", Theme.F(10f, true), Theme.Muted, lx, ly, 210, 14);
            ly += 16;
            ui.Text("is the only claim here; neither", Theme.F(10f), Theme.Muted, lx, ly, 210, 14);
            ly += 14;
            ui.Text("curve is normalised or smoothed.", Theme.F(10f), Theme.Muted, lx, ly, 210, 14);
            ly += 22;
            if (tune != null)
            {
                double ripple = corr == null ? 0 : Curve.Ripple(Curve.Grid, corr, 200, 8000);
                ui.Text("correction: " + tune.Stem, Theme.F(10f), Theme.Muted, lx, ly, 210, 14);
                ly += 14;
                ui.Text(tune.FileName.StartsWith("1-car-tune", StringComparison.OrdinalIgnoreCase)
                            ? "built from this run" : "not the tune built from this run",
                        Theme.F(10f), Theme.Muted, lx, ly, 210, 14);
                ly += 14;
                ui.Text("ripple 200\u20138k: " + ripple.ToString("0.0") + " dB p-p",
                        Theme.F(10f), Theme.Muted, lx, ly, 210, 14);
            }
        }

        private static void Dot(Ui ui, int x, int y, Color c, string label)
        {
            ui.Dot(x + 5, y + 7, 3, c);
            ui.Text(label, Theme.F(10.5f, true), Theme.Fg, x + 16, y, 200, 14);
        }

        private static void PaintCorrection(AppState s, Ui ui, Rectangle r, double[] corr, TuneFile tune, int path)
        {
            ui.Card(r);
            Chrome.Section(ui, "THE CORRECTION ITSELF \u00b7 dB, ZERO-CENTRED", r.X + 14, r.Y + 11,
                           r.Width - 250, Theme.Dim);
            if (corr == null)
            {
                ui.Text("No correction: this path has no tune band set to attribute.",
                        Theme.F(10.5f), Theme.Muted, r.X + 14, r.Y + 40, r.Width - 28, 16);
                return;
            }
            List<Series> list = new List<Series>();
            Series c = new Series("correction", Curve.Grid, corr, Theme.Accent);
            c.Fill = true;
            c.Width = 1.6f;
            list.Add(c);

            Chart chart = new Chart();
            Rectangle plot = new Rectangle(r.X + 10, r.Y + 28, r.Width - 250, r.Height - 36);
            chart.Prepare(plot);
            if (plot.Contains(Pointer.At)) chart.HoverHz = chart.HzAt(Pointer.At.X);
            chart.Draw(ui, plot, list);

            int lx = r.Right - 232;
            int ly = r.Y + 32;
            TuneMemory m = tune == null ? null : tune.Mem(0);
            TuneOutput o = m == null ? null : m.Out(path);
            if (o != null)
            {
                ui.Text(o.ActiveBands + " active bands", Theme.F(11f, true), Theme.Fg, lx, ly, 210, 16);
                ly += 18;
                ui.Text("max Q " + (o.MaxQ > 0 ? o.MaxQ.ToString("0.0") : "\u2014") +
                        "  \u00b7  " + (o.MaxQ > 0 ? Analysis.BandwidthOct(o.MaxQ).ToString("0.00") + " oct" : "flat"),
                        Theme.F(10f), Theme.Muted, lx, ly, 210, 14);
                ly += 16;
                double ripple = Curve.Ripple(Curve.Grid, corr, 200, 8000);
                ui.Text("realized ripple " + ripple.ToString("0.0") + " dB", Theme.F(10f), Theme.Muted,
                        lx, ly, 210, 14);
            }
        }

        private static void PaintBands(AppState s, Ui ui, Rectangle r, TuneFile tune, int path)
        {
            ui.Card(r);
            Chrome.Section(ui, "BAND TABLE \u00b7 fc / GAIN / Q / WIDTH", r.X + 14, r.Y + 11, 400, Theme.Dim);

            TuneMemory m = tune == null ? null : tune.Mem(0);
            TuneOutput o = m == null ? null : m.Out(path);
            if (o == null)
            {
                ui.Text("No bands for this path.", Theme.F(11f), Theme.Muted, r.X + 14, r.Y + 36, r.Width - 28, 16);
                return;
            }

            List<Band> bands = new List<Band>();
            for (int i = 0; i < o.Bands.Count; i++) if (o.Bands[i].IsActive) bands.Add(o.Bands[i]);
            bands.Sort(delegate(Band a, Band b) { return a.Fc.CompareTo(b.Fc); });

            int perCol = 6;
            int colW = (r.Width - 28) / 3;
            for (int i = 0; i < bands.Count && i < perCol * 3; i++)
            {
                int col = i / perCol;
                int row = i % perCol;
                int bx = r.X + 14 + col * colW;
                int by = r.Y + 32 + row * 13;
                Band b = bands[i];
                double oct = Analysis.BandwidthOct(b.Q);
                bool narrow = oct < 0.25;
                ui.Text(Channels.Hz(b.Fc) + " Hz", Theme.F(9.5f), Theme.Dim, bx, by, 54, 12);
                ui.TextAt(Channels.Db(b.Gain, 1), Theme.F(9.5f, true), narrow ? Theme.Warn : Theme.Dim,
                          new Rectangle(bx + 52, by, 40, 12), "r");
                ui.TextAt("Q" + b.Q.ToString("0.0"), Theme.F(9.5f), narrow ? Theme.Warn : Theme.Muted,
                          new Rectangle(bx + 94, by, 40, 12), "r");
                ui.TextAt(oct.ToString("0.00") + " oct", Theme.F(9f), Theme.Muted,
                          new Rectangle(bx + 132, by, 50, 12), "r");
            }

            if (bands.Count > perCol * 3)
                ui.TextAt("+" + (bands.Count - perCol * 3) + " more", Theme.F(9.5f), Theme.Muted,
                          new Rectangle(r.Right - 90, r.Y + 11, 76, 14), "r");
            ui.TextAt(bands.Count + " active band(s)  \u00b7  amber = narrower than \u00bc octave",
                      Theme.F(9.5f), Theme.Muted, new Rectangle(r.X + 300, r.Y + 11, r.Width - 314, 14), "r");
        }
    }
}
