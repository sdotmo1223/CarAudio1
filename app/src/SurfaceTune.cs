using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace DreTunes
{
    /// <summary>
    /// 6.1 Tune - the eight output channels, their crossovers, and the response.
    ///
    /// This is the console's own layout, read back: output level, high-pass, low-pass, delay and
    /// polarity per channel, in one table, with the EQ response underneath so a crossover edge can
    /// be seen against the curve it is shaping.
    ///
    /// It is deliberately read-only as a *table*, and the reason is not modesty. A swap writes EQ
    /// from the Library surface; the brief forbids writing crossovers from any tune path; and
    /// command 11 - level, delay, polarity - has no readback at all, so an edit to any of those
    /// numbers could never be proven and "verified" would be a word about the block we sent
    /// ourselves. The footer states the count of editable columns and it is zero. Every value on
    /// this screen is a reading with its source named in the column header.
    ///
    /// There is exactly one control that transmits: the horn at the head of each row, which sends
    /// a complete sixteen-entry command-11 block to mute or unmute that channel. It is the one
    /// thing here that cannot be a reading, so it is labelled mirror-only in the footer, in the
    /// action line and in the log, and it refuses rather than guesses when the block in hand is
    /// not the block for the memory the amplifier says it is playing.
    /// </summary>
    public static class SurfaceTune
    {
        // Column starts in design units; the block is stretched to the width actually available
        // so it fills the surface at 1366 and still cannot overlap itself in a smaller window.
        // DesignWidth is the block's own width (1080), not the canvas, so the columns take the
        // slack rather than leaving an empty strip on the right. The label column is wider than
        // the channel text needs because the horn button lives at its head, as on the console.
        private static readonly int[] Design = { 0, 180, 190, 300, 380, 458, 576, 656, 734, 852, 952, 1030 };
        private const int DesignWidth = 1034;

        public static void Paint(AppState s, Ui ui, Rectangle a)
        {
            int x = a.X, y = a.Y, w = a.Width;

            PaintHead(s, ui, new Rectangle(x, y, w, 64));

            int tableTop = y + 68;
            PaintTable(s, ui, x, tableTop, w);

            int footTop = tableTop + TuneTable.Height + 6;
            PaintFooter(s, ui, new Rectangle(x, footTop, w, 18));

            int chartTop = footTop + 24;
            PaintCurves(s, ui, new Rectangle(x, chartTop, w, a.Bottom - chartTop));
        }

        private static int[] Columns(int left, int width)
        {
            double k = width / (double)DesignWidth;
            int[] c = new int[Design.Length];
            for (int i = 0; i < Design.Length; i++) c[i] = left + (int)Math.Round(Design[i] * k);
            return c;
        }

        // ------------------------------------------------------------------ head

        private static void PaintHead(AppState s, Ui ui, Rectangle r)
        {
            TuneFile f = s.Selected;
            string name = f != null ? f.Stem : "no tune selected";
            ui.Text(Chrome.Ellipsis(ui, name, Theme.F(21f, true), 320), Theme.F(21f, true),
                    f != null ? Theme.Fg : Theme.Muted, r.X, r.Y + 1, 324, 28);

            Rectangle read = new Rectangle(r.Right - 104, r.Y + 2, 96, 26);
            ui.Button(read, s.Rig.EqReading ? "Reading\u2026" : "Read EQ", 0, delegate
            {
                s.Rig.RequestEq();
                s.Note("reading the device EQ and crossovers (8 USB round trips)");
            }, s.Rig.BridgeUp && !s.Rig.EqReading);

            int cx = r.X + 332, cy = r.Y + 4, limit = read.X - 12;

            string mem;
            Color memColor;
            if (!s.Rig.DeviceLive)
            {
                mem = s.Rig.Mem >= 0 ? "MEMORY " + (s.Rig.Mem + 1) + " \u00b7 BRIDGE LABEL" : "MEMORY UNKNOWN";
                memColor = Theme.Warn;
            }
            else
            {
                mem = s.Rig.Mem >= 0 ? "MEMORY " + (s.Rig.Mem + 1) + " OF 4" : "MEMORY UNKNOWN";
                memColor = s.Rig.Mem >= 0 ? Theme.Accent : Theme.Muted;
            }
            cx += ui.Badge(cx, cy, mem, memColor) + 8;

            if (f != null && cx < limit)
            {
                Provenance p = ProvenanceTable.Of(f.FileName);
                cx += Chip(ui, cx, cy + 2, limit,
                           (p.Authored ? "AUTHORED \u00b7 " : "INHERITED \u00b7 ") + p.Kind.ToUpperInvariant(),
                           p.Authored ? Theme.Accent : Theme.Muted);
                cx += Chip(ui, cx, cy + 2, limit, f.LiveBands + " BANDS", Theme.Muted);
            }

            // A readback that identifies a different file than the one the table's numbers come
            // from is the single most confusing state this screen can be in, so it is said out
            // loud next to the name rather than left for the reader to notice.
            TuneMatch m = s.Match;
            if (f != null && cx < limit)
            {
                if (m != null && m.File != null &&
                    !string.Equals(m.File.FullPath, f.FullPath, StringComparison.OrdinalIgnoreCase))
                    cx += Chip(ui, cx, cy + 2, limit, "DEVICE MATCHES " + m.File.Stem.ToUpperInvariant(), Theme.Warn);
                else if (m == null && s.Rig.EqOk && s.Rig.Bands != null)
                    cx += Chip(ui, cx, cy + 2, limit, "DEVICE MATCHES NOTHING", Theme.Bad);
            }

            // The bridge's remembered name is a label from the last write, never evidence. It is
            // shown only when it disagrees with the file being described, because that
            // disagreement is the thing a reader would otherwise take at face value.
            if (f != null && s.Rig.BridgeTuneName.Length > 0 &&
                !string.Equals(s.Rig.BridgeTuneName, f.Stem, StringComparison.OrdinalIgnoreCase))
                cx += Chip(ui, cx, cy + 2, limit, "BRIDGE SAYS " + s.Rig.BridgeTuneName.ToUpperInvariant(),
                           Theme.Warn);

            string word;
            string detail;
            Color c;
            if (!s.Rig.BridgeUp)
            {
                word = "BRIDGE DOWN";
                detail = "Nothing in this table is live. No value here was read from the amplifier.";
                c = Theme.Bad;
            }
            else if (!s.Rig.EqOk || s.Rig.Bands == null)
            {
                word = "EQ NOT READ";
                detail = "The device has not been read back" +
                         (s.Rig.EqError.Length > 0 ? ": " + s.Rig.EqError : "") +
                         ". The crossover columns below are the tune file's, not the amplifier's.";
                c = Theme.Warn;
            }
            else if (m == null || m.File == null)
            {
                word = "EQ READ \u00b7 UNMATCHED";
                detail = "The EQ was read at " + Chrome.Stamp(s.Rig.EqAt) + " and matches no .idsp in the library.";
                c = Theme.Warn;
            }
            else if (m.IsClean)
            {
                word = "VERIFIED BY READBACK";
                detail = "All 8 paths match " + m.File.FileName + " (" + m.PathsMatched + "/" + m.PathsCompared +
                         " paths, " + m.BandDiffs + " band differences) as read at " + Chrome.Stamp(s.Rig.EqAt) + ".";
                c = Theme.Ok;
            }
            else
            {
                word = "PARTIAL MATCH";
                detail = m.PathsMatched + " of " + m.PathsCompared + " paths match " + m.File.FileName +
                         "; " + m.BandDiffs + " band difference(s). The EQ is not what the file says.";
                c = Theme.Warn;
            }

            int bw = ui.Badge(r.X, r.Y + 36, word, c);
            ui.Text(Chrome.Ellipsis(ui, detail, Theme.F(11.5f), r.Width - bw - 18), Theme.F(11.5f), c,
                    r.X + bw + 10, r.Y + 36, r.Width - bw - 14, 22);
        }

        /// <summary>A provenance chip that refuses to paint itself off the end of the row.</summary>
        private static int Chip(Ui ui, int x, int y, int limit, string text, Color c)
        {
            int w = (int)Math.Ceiling(ui.Measure(text, Theme.F(10.5f)).Width) + 18;
            if (x + w > limit) return 0;
            Chrome.Provenance(ui, x, y, text, c);
            return w + 8;
        }

        // ------------------------------------------------------------------ table

        private static void PaintTable(AppState s, Ui ui, int x, int top, int w)
        {
            int[] c = Columns(x, w);
            TuneRow probe = TuneRow.From(s.Selected, s.Rig, s.Path);

            // ---- header: what the column is, and which side of the fence it came from.
            // Units ride the source line rather than the heading: "LEVEL dB" set against
            // "HIGH PASS" read as one phrase, and the number that followed it had no gap.
            ui.HLine(x, top + TuneTable.HeaderH - 1, w, Theme.Line);
            Micro(ui, "CHANNEL", c[0] + 38, top + 6, c[2] - c[0] - 40, Theme.Muted);
            MicroRight(ui, "LEVEL", c[3] - 8, top + 6, Theme.Muted);
            Micro(ui, "HIGH PASS", c[3] + 10, top + 6, c[6] - c[3] - 12, Theme.Muted);
            Micro(ui, "LOW PASS", c[6] + 10, top + 6, c[9] - c[6] - 12, Theme.Muted);
            MicroRight(ui, "DELAY", c[10] - 8, top + 6, Theme.Muted);
            Micro(ui, "POLARITY", c[10] + 10, top + 6, c[11] - c[10] - 12, Theme.Muted);

            int ly = top + 20;
            Source(ui, c[3] - 8, ly, probe.LevelSource, true, true, 70, "dB");
            Source(ui, c[3] + 10, ly, probe.XoSource, false, probe.XoEnableKnown, c[6] - c[3] - 12, "");
            Source(ui, c[6] + 10, ly, probe.XoSource, false, probe.XoEnableKnown, c[9] - c[6] - 12, "");
            Source(ui, c[10] - 8, ly, probe.DelaySource, true, true, 70, "ms");
            Source(ui, c[10] + 10, ly, probe.PolaritySource, false, true, c[11] - c[10] - 12, "");

            // ---- rows. One hit region for the body, resolved by TuneTable.RowAt, so the mapping
            // from a y coordinate to a channel is the same function the self-test asserts.
            //
            // The body is registered BEFORE the rows paint: click hit-testing walks the list in
            // reverse, so the horn buttons painted on top of the row win over the row itself.
            Rectangle body = new Rectangle(x, top, w, TuneTable.HeaderH + TuneTable.RowH * Channels.Live);
            ui.Hit(body, delegate
            {
                int p = TuneTable.RowAt(top, Pointer.At.Y);
                if (p < 0) return;
                s.Path = p;
                s.Note("selected CH " + (p + 1) + " \u00b7 " + Channels.Driver[p]);
            });

            for (int p = 0; p < Channels.Live; p++)
            {
                int ry = top + TuneTable.HeaderH + p * TuneTable.RowH;
                PaintRow(s, ui, x, ry, w, c, TuneRow.From(s.Selected, s.Rig, p), p == s.Path);
            }
        }

        /// <summary>
        /// The provenance word under a column heading. "MIRROR" means command 11 and can never be
        /// a device reading; a crossover column that came off a readback says so, and says the
        /// enable bit did not come with it.
        /// </summary>
        private static void Source(Ui ui, int edge, int y, string source, bool right, bool enableKnown,
                                   int width, string unit)
        {
            string word;
            Color c;
            if (source == TuneRow.FromDevice)
            {
                word = enableKnown ? "device" : "device \u00b7 enable not read";
                c = Theme.Accent;
            }
            else if (source == TuneRow.FromMirror) { word = "mirror"; c = Theme.Warn; }
            else if (source == TuneRow.FromFile) { word = "file"; c = Theme.Dim; }
            else { word = "not read"; c = Theme.Muted; }

            string text = unit.Length > 0 ? unit + " \u00b7 " + word : word;
            if (right) MicroRight(ui, text, edge, y, c);
            else Micro(ui, text, edge, y, width, c);
        }

        private static void PaintRow(AppState s, Ui ui, int x, int ry, int w, int[] c, TuneRow row, bool sel)
        {
            int h = TuneTable.RowH;
            if (sel)
            {
                ui.Fill(new Rectangle(x, ry, w, h - 1), Theme.Mix(Theme.Panel, Theme.Accent, 0.10));
                ui.Fill(new Rectangle(x, ry, 3, h - 1), Theme.Accent);
            }
            else ui.HLine(x, ry + h - 1, w, Theme.Line);

            int g = Channels.Group(row.Path);
            Color labelC = sel ? Theme.Accent : (g == 0 ? Theme.Dim : Theme.Muted);

            // The horn sits at the head of the row, as on the console. It is the only control on
            // this surface that transmits; everything to its right is a reading.
            Horn(s, ui, row.Path, new Rectangle(c[0] + 12, ry + (h - 20) / 2, 22, 20), row.Muted);

            ui.Text("CH " + (row.Path + 1), Theme.F(11f, true), labelC, c[0] + 40, ry + 1, 34, h - 2);
            string sub = row.Muted ? Channels.Short[row.Path] + " \u00b7 MUTED" : Channels.Short[row.Path];
            ui.Text(Chrome.Ellipsis(ui, sub, Theme.F(10.5f), c[1] - c[0] - 86), Theme.F(10.5f),
                    row.Muted ? Theme.Bad : Theme.Muted, c[0] + 76, ry + 1, c[1] - c[0] - 84, h - 2);

            // level meter: attenuation depth, 0 dB at the top, -30 dB at the floor
            if (row.Have)
            {
                int bx = c[1] + 3, bh = 18, by = ry + (h - 18) / 2;
                ui.Fill(new Rectangle(bx, by, 4, bh), Theme.Mix(Theme.Well, Theme.Line, 0.6));
                double frac = Math.Max(0, Math.Min(1, (row.Level + 30.0) / 30.0));
                int fh = (int)Math.Round(bh * frac);
                if (fh > 0)
                    ui.Fill(new Rectangle(bx, by + bh - fh, 4, fh),
                            sel ? Theme.Accent : Theme.Alpha(Theme.Dim, 150));
            }

            ui.TextAt(row.LevelText, Theme.F(13f, row.Have), row.Have ? Theme.Fg : Theme.Muted,
                      new Rectangle(c[2], ry + 1, c[3] - c[2] - 10, h - 2), "r");

            Filter(ui, row.Hp, row.HpOpen, c[3] + 10, c[4], c[5], c[6], ry, h);
            Filter(ui, row.Lp, row.LpOpen, c[6] + 10, c[7], c[8], c[9], ry, h);

            ui.TextAt(row.DelayText, Theme.F(13f, row.Have), row.Have ? Theme.Fg : Theme.Muted,
                      new Rectangle(c[9], ry + 1, c[10] - c[9] - 10, h - 2), "r");
            ui.TextAt(row.PolarityText, Theme.F(12f, row.Invert), row.Invert ? Theme.Warn : Theme.Dim,
                      new Rectangle(c[10] + 10, ry + 1, c[11] - c[10] - 12, h - 2), "l");
        }

        /// <summary>
        /// The per-channel horn: number that mutes and unmutes that output.
        ///
        /// Three states, and the difference between them is a fact about what the app knows, not a
        /// decoration: waves = unmuted per the command-11 mirror; slash = muted per that mirror;
        /// no waves and a dim glyph = no block is known, so the app cannot say whether this channel
        /// is playing and will not pretend to. A click always explains itself - the sender refuses
        /// out loud rather than silently doing nothing.
        /// </summary>
        private static void Horn(AppState s, Ui ui, int path, Rectangle r, bool muted)
        {
            bool known = s.Rig.OutputKnown;
            bool armed = known && s.Rig.BridgeUp && s.Rig.DeviceLive;
            bool over = ui.IsOver(r);

            // Known but not armed is the common state in the car with the DSP unplugged: the app
            // can say what the mirror holds but cannot send anything, so the glyph is dimmed
            // rather than dressed up as a live control.
            Color fg;
            if (!known) fg = Theme.Muted;
            else if (muted) fg = armed ? Theme.Bad : Theme.Alpha(Theme.Bad, 120);
            else fg = armed ? Theme.Fg : Theme.Dim;
            if (over && armed) ui.FillRound(r, 3, Theme.Mix(Theme.Panel, Theme.Accent, 0.16));
            ui.StrokeRound(r, 3, over && armed ? Theme.Alpha(Theme.Accent, 170) : Theme.Line);
            Speaker(ui, r, muted && known, fg, !known);

            ui.Hit(r, delegate
            {
                s.Path = path;
                MuteBlock.Send(s, path);
            });
        }

        private static void Speaker(Ui ui, Rectangle r, bool muted, Color c, bool unknown)
        {
            int cx = r.X + r.Width / 2 - 1, cy = r.Y + r.Height / 2;
            Point[] cone =
            {
                new Point(cx - 7, cy - 3), new Point(cx - 3, cy - 3), new Point(cx + 2, cy - 7),
                new Point(cx + 2, cy + 7), new Point(cx - 3, cy + 3), new Point(cx - 7, cy + 3)
            };
            using (SolidBrush b = new SolidBrush(c)) ui.G.FillPolygon(b, cone);

            if (muted)
            {
                using (Pen p = new Pen(Theme.Bad, 1.8f))
                {
                    p.StartCap = LineCap.Round;
                    p.EndCap = LineCap.Round;
                    ui.G.DrawLine(p, cx - 6, cy + 7, cx + 9, cy - 7);
                }
            }
            else if (!unknown)
            {
                using (Pen p = new Pen(Theme.Alpha(c, 210), 1.3f))
                {
                    p.StartCap = LineCap.Round;
                    p.EndCap = LineCap.Round;
                    ui.G.DrawArc(p, cx - 1, cy - 4, 8, 8, -52, 104);
                    ui.G.DrawArc(p, cx - 1, cy - 7, 14, 14, -50, 100);
                }
            }
        }

        private static void Filter(Ui ui, PassBand pb, bool open, int typeX, int slopeX, int freqX, int endX,
                                   int ry, int h)
        {
            if (pb == null)
            {
                ui.TextAt(TuneRow.Unknown, Theme.F(11.5f), Theme.Muted,
                          new Rectangle(typeX + 2, ry + 1, endX - typeX - 4, h - 2), "l");
                return;
            }
            if (open)
            {
                // A file can say a filter is switched off, and that is not the same as "flat at
                // its stored frequency" - printing 20 kHz there would be a filter the DSP is not
                // running.
                ui.TextAt("OPEN", Theme.F(10.5f, true), Theme.Muted,
                          new Rectangle(typeX + 2, ry + 1, endX - typeX - 4, h - 2), "l");
                return;
            }
            // The slope carries its own unit. A bare "24" floating between the type and the
            // frequency is a number a reader has to decode; "24 dB/oct" is not.
            ui.TextAt(TuneRow.TypeText(pb), Theme.F(11.5f), Theme.Dim,
                      new Rectangle(typeX, ry + 1, slopeX - typeX - 4, h - 2), "c");
            ui.TextAt(TuneRow.SlopeText(pb) + " dB/oct", Theme.F(11.5f), Theme.Dim,
                      new Rectangle(slopeX, ry + 1, freqX - slopeX - 6, h - 2), "r");
            ui.TextAt(TuneRow.FreqText(pb), Theme.F(11.5f), Theme.Fg,
                      new Rectangle(freqX, ry + 1, endX - freqX - 4, h - 2), "r");
        }

        private static void Micro(Ui ui, string s, int x, int y, int w, Color c)
        {
            ui.Micro(s, x, y, w, c);
        }

        private static void MicroRight(Ui ui, string s, int right, int y, Color c)
        {
            int w = ui.MicroWidth(s, 0.9f);
            ui.Micro(s, right - w, y, w + 2, c);
        }

        // ------------------------------------------------------------------ footer

        private static void PaintFooter(AppState s, Ui ui, Rectangle r)
        {
            TuneRow row = TuneRow.From(s.Selected, s.Rig, s.Path);

            string left;
            Color c;
            if (s.Rig.OutputKnown)
            {
                left = "MIRROR ONLY \u2014 command 11 cannot echo: level, delay and polarity are the " +
                       "block this session sent, never a device read.";
                c = Theme.Warn;
            }
            else
            {
                left = "Level, delay and polarity are the tune file's values \u2014 no command-11 block " +
                       "is known for this memory this session.";
                c = Theme.Muted;
            }
            ui.Text(Chrome.Ellipsis(ui, left, Theme.F(10.5f), r.Width - 360), Theme.F(10.5f), c,
                    r.X, r.Y, r.Width - 360, r.Height);

            string right = row.WritableColumns + " of " + TuneRow.Columns +
                           " columns editable \u00b7 the horn sends cmd 11";
            ui.TextAt(right, Theme.F(10.5f, true),
                      row.WritableColumns == 0 ? Theme.Warn : Theme.Bad,
                      new Rectangle(r.Right - 340, r.Y, 340, r.Height), "r");
        }

        // ------------------------------------------------------------------ curves

        private static void PaintCurves(AppState s, Ui ui, Rectangle r)
        {
            bool device = s.Rig.EqOk && s.Rig.Bands != null;
            ui.Card(r);

            string title = device
                ? "EQ RESPONSE \u00b7 DEVICE READBACK " + Chrome.Stamp(s.Rig.EqAt).ToUpperInvariant()
                : "EQ RESPONSE \u00b7 TUNE FILE (NO DEVICE READ)";
            Chrome.Section(ui, title, r.X + 14, r.Y + 11, r.Width - 300, device ? Theme.Dim : Theme.Warn);

            List<Series> all = new List<Series>();
            TuneFile f = s.Selected;
            for (int p = 0; p < Channels.Live; p++)
            {
                double[] db = device ? s.DeviceCurve(p) : s.TuneCurve(f, p);
                if (db == null) continue;
                Series series = new Series();
                series.Name = "CH " + (p + 1) + " \u00b7 " + Channels.Short[p];
                series.F = Curve.Grid;
                series.Db = db;
                series.C = p == s.Path ? Theme.Accent : Theme.Curve;
                series.Width = p == s.Path ? 2.1f : 1.2f;
                series.Dim = p != s.Path;
                all.Add(series);
            }

            if (all.Count == 0)
            {
                ui.Text("No EQ to draw. " + (s.Rig.BridgeUp
                    ? "Read the device, or select a tune in the library."
                    : "The bridge is down, so there is no device read and no library yet."),
                    Theme.F(12f), Theme.Dim, r.X + 16, r.Y + 34, r.Width - 32, 18);
                return;
            }

            Series selected = null;
            for (int i = 0; i < all.Count; i++)
                if (all[i].Name.StartsWith("CH " + (s.Path + 1) + " ", StringComparison.Ordinal))
                    selected = all[i];

            int legendW = 250;
            Chart chart = new Chart();
            Rectangle plot = new Rectangle(r.X + 10, r.Y + 30, r.Width - legendW - 20, r.Height - 40);
            chart.Prepare(plot);
            if (plot.Contains(Pointer.At)) chart.HoverHz = chart.HzAt(Pointer.At.X);
            chart.Draw(ui, plot, all);

            PaintXoMarkers(ui, chart, s);

            if (selected != null)
            {
                List<Series> one = new List<Series>();
                one.Add(selected);
                chart.Readout(ui, one, plot);
            }

            PaintLegend(s, ui, r, legendW);
        }

        /// <summary>
        /// The crossover edges of the selected channel, drawn on the response as frequency
        /// markers. They are markers, not a computed filter: Curve mirrors the peaking EQ and
        /// nothing else, so drawing a roll-off would be inventing the shape.
        /// </summary>
        private static void PaintXoMarkers(Ui ui, Chart chart, AppState s)
        {
            TuneRow row = TuneRow.From(s.Selected, s.Rig, s.Path);
            Marker(ui, chart, row.Hp, row.HpOpen, "HP", Theme.Accent, true);
            Marker(ui, chart, row.Lp, row.LpOpen, "LP", Theme.Dim, false);
        }

        private static void Marker(Ui ui, Chart chart, PassBand pb, bool open, string label, Color c, bool top)
        {
            if (pb == null || open || pb.Fc < chart.FMin || pb.Fc > chart.FMax) return;
            int x = chart.X(pb.Fc);
            using (Pen p = new Pen(Theme.Alpha(c, 130), 1f))
            {
                p.DashStyle = DashStyle.Dash;
                ui.G.DrawLine(p, x, chart.Plot.Y, x, chart.Plot.Bottom);
            }
            int ty = top ? chart.Plot.Y + 2 : chart.Plot.Bottom - 16;
            ui.TextAt(label + " " + Channels.Hz(pb.Fc), Theme.F(9.5f, true), Theme.Alpha(c, 230),
                      new Rectangle(x - 36, ty, 72, 14), "c");
        }

        private static void PaintLegend(AppState s, Ui ui, Rectangle r, int legendW)
        {
            int lx = r.Right - legendW;
            int ly = r.Y + 32;

            for (int g = 0; g < 3; g++)
            {
                ui.Dot(lx + 6, ly + 8, 3, Theme.Curve);
                ui.Text(Channels.GroupName[g] + " \u00b7 " + Channels.GroupNote[g].Split(',')[0],
                        Theme.F(10.5f), Theme.Dim, lx + 16, ly, legendW - 20, 16);
                ly += 14;
            }
            ly += 4;
            ui.HLine(lx, ly, legendW - 30, Theme.Line);
            ly += 8;

            ui.Text(Chrome.Ellipsis(ui, "CH " + (s.Path + 1) + " \u00b7 " + Channels.Driver[s.Path],
                                    Theme.F(12f, true), legendW - 10),
                    Theme.F(12f, true), Theme.Accent, lx, ly, legendW - 10, 18);
            ly += 19;

            Series selected = null;
            {
                int p = s.Path;
                double[] db = (s.Rig.EqOk && s.Rig.Bands != null) ? s.DeviceCurve(p) : s.TuneCurve(s.Selected, p);
                if (db != null) { selected = new Series(); selected.Db = db; selected.F = Curve.Grid; }
            }

            if (selected != null)
            {
                double[] db = selected.Db;
                double ripple = Curve.Ripple(Curve.Grid, db, 200, 8000);
                double mx = -999, mn = 999;
                for (int i = 0; i < db.Length; i++)
                {
                    if (Curve.Grid[i] < 200 || Curve.Grid[i] > 8000) continue;
                    if (db[i] > mx) mx = db[i];
                    if (db[i] < mn) mn = db[i];
                }
                ui.Text("ripple 200 Hz\u20138 kHz", Theme.F(10.5f), Theme.Muted, lx, ly, legendW - 10, 15);
                ui.TextAt(ripple.ToString("0.0") + " dB p-p", Theme.F(10.5f, true), Theme.Dim,
                          new Rectangle(lx + 120, ly, legendW - 130, 15), "r");
                ly += 15;
                ui.Text("max boost / cut", Theme.F(10.5f), Theme.Muted, lx, ly, legendW - 10, 15);
                ui.TextAt(Channels.Db(mx, 1) + " / " + Channels.Db(mn, 1), Theme.F(10.5f, true), Theme.Dim,
                          new Rectangle(lx + 120, ly, legendW - 130, 15), "r");
                ly += 15;
            }

            TuneFile f = s.Selected;
            TuneMemory mem = f == null ? null : f.Mem(0);
            TuneOutput o = mem == null ? null : mem.Out(s.Path);
            if (o != null)
            {
                ui.Text(o.ActiveBands + " active bands", Theme.F(10.5f), Theme.Muted, lx, ly, 130, 15);
                ui.TextAt("max Q " + (o.MaxQ > 0 ? o.MaxQ.ToString("0.0") : "\u2014"),
                          Theme.F(10.5f, true), Theme.Dim,
                          new Rectangle(lx + 130, ly, legendW - 140, 15), "r");
                ly += 15;
                ui.Text("file level " + Channels.Db(o.Gain, 1) + " dB \u00b7 " + Channels.Ms(o.Delay) + " ms",
                        Theme.F(10.5f), Theme.Muted, lx, ly, legendW - 10, 15);
                ly += 15;
            }

            TuneRow row = TuneRow.From(f, s.Rig, s.Path);
            string hp = row.HpOpen ? "open" : Channels.CrossoverText(row.Hp);
            string lp = row.LpOpen ? "open" : Channels.CrossoverText(row.Lp);
            ui.Text(Chrome.Ellipsis(ui, "HP " + hp + " \u00b7 LP " + lp, Theme.F(10.5f), legendW - 10),
                    Theme.F(10.5f), Theme.Muted, lx, ly, legendW - 10, 15);
        }
    }
}
