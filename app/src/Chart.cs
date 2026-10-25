using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace DreTunes
{
    /// <summary>One line on a chart. Frequencies ascend; dB is what the tool prints.</summary>
    public sealed class Series
    {
        public string Name = "";
        public double[] F;
        public double[] Db;
        public Color C = Theme.Curve;
        public float Width = 1.4f;
        public bool Dash;
        public bool Fill;
        public bool Dim;

        public Series() { }

        public Series(string name, double[] f, double[] db, Color c)
        {
            Name = name; F = f; Db = db; C = c;
        }
    }

    /// <summary>
    /// A log-frequency / dB chart, drawn by hand.
    ///
    /// This is the one place the app is allowed to be decorative, and it still is not: the axis is
    /// the standard audio axis (20 Hz to 20 kHz, log), the grid is labelled in the units on the
    /// data, and every series says where it came from in the legend the caller draws. A curve is
    /// never smoothed toward a nicer shape, never normalised, and never scaled to fill the box -
    /// AutoScale rounds the dB range outward to a whole grid step so two charts side by side can
    /// be compared by eye.
    /// </summary>
    public sealed class Chart
    {
        public double FMin = 20.0;
        public double FMax = 20000.0;
        public double DbMin = -18.0;
        public double DbMax = 12.0;
        public bool Auto = true;
        public double HoverHz;
        public string Unit = "dB";

        public int PadL = 44;
        public int PadR = 10;
        public int PadT = 10;
        public int PadB = 22;

        public Rectangle Plot;

        private static readonly double[] Ticks =
            { 20, 30, 50, 100, 200, 300, 500, 1000, 2000, 3000, 5000, 10000, 20000 };
        private static readonly string[] TickText =
            { "20", "30", "50", "100", "200", "300", "500", "1k", "2k", "3k", "5k", "10k", "20k" };

        public int X(double hz)
        {
            double f = Math.Max(FMin, Math.Min(FMax, hz));
            double t = Math.Log(f / FMin) / Math.Log(FMax / FMin);
            return Plot.X + (int)Math.Round(t * Plot.Width);
        }

        public int Y(double db)
        {
            double t = (db - DbMin) / (DbMax - DbMin);
            return Plot.Bottom - (int)Math.Round(t * Plot.Height);
        }

        public double HzAt(int px)
        {
            double t = (double)(px - Plot.X) / Math.Max(1, Plot.Width);
            t = Math.Max(0, Math.Min(1, t));
            return FMin * Math.Pow(FMax / FMin, t);
        }

        /// <summary>Set Plot without drawing, so a caller can resolve a hover frequency first.</summary>
        public void Prepare(Rectangle r)
        {
            Plot = new Rectangle(r.X + PadL, r.Y + PadT,
                                 r.Width - PadL - PadR, r.Height - PadT - PadB);
        }

        public static double[] LogGrid(double f0, double f1, int n)
        {
            double[] g = new double[n];
            for (int i = 0; i < n; i++)
                g[i] = f0 * Math.Pow(f1 / f0, n == 1 ? 0 : (double)i / (n - 1));
            return g;
        }

        /// <summary>Round the dB window outward to a whole step, never in toward the data.</summary>
        public void Fit(IList<Series> list)
        {
            if (!Auto) return;
            double mn = 0, mx = 0;
            bool any = false;
            for (int i = 0; i < list.Count; i++)
            {
                Series s = list[i];
                if (s.Db == null) continue;
                for (int k = 0; k < s.Db.Length; k++)
                {
                    double v = s.Db[k];
                    if (double.IsNaN(v) || double.IsInfinity(v)) continue;
                    if (!any) { mn = v; mx = v; any = true; }
                    else { if (v < mn) mn = v; if (v > mx) mx = v; }
                }
            }
            if (!any) { mn = -12; mx = 6; }
            // Always include 0 dB: "flat" must be visible, because a curve that never touches it
            // is exactly the thing a reader needs to notice.
            mn = Math.Min(mn, 0); mx = Math.Max(mx, 0);
            double span = Math.Max(6.0, mx - mn);
            double step = span > 30 ? 12 : (span > 18 ? 6 : (span > 9 ? 3 : 2));
            DbMin = Math.Floor(mn / step) * step;
            DbMax = Math.Ceiling(mx / step) * step;
            if (DbMax - DbMin < step * 2) DbMax = DbMin + step * 2;
        }

        public void Draw(Ui ui, Rectangle r, IList<Series> list)
        {
            if (r.Width < 80 || r.Height < 50) return;
            Fit(list);
            Prepare(r);

            ui.FillRound(r, 4, Theme.Well);
            ui.StrokeRound(r, 4, Theme.Line);

            double step = (DbMax - DbMin) > 24 ? 12 : ((DbMax - DbMin) > 12 ? 6 : 3);
            // horizontal grid + labels
            for (double db = Math.Ceiling(DbMin / step) * step; db <= DbMax + 0.01; db += step)
            {
                int y = Y(db);
                ui.HLine(Plot.X, y, Plot.Width, db == 0 ? Theme.Mix(Theme.Line, Theme.Dim, 0.45) : Theme.Line);
                ui.TextAt(Channels.Db(db, 0), Theme.F(10.5f), Theme.Muted,
                          new Rectangle(r.X + 2, y - 8, PadL - 8, 16), "r");
            }

            // vertical grid
            for (int i = 0; i < Ticks.Length; i++)
            {
                double hz = Ticks[i];
                if (hz < FMin || hz > FMax) continue;
                int x = X(hz);
                ui.VLine(x, Plot.Y, Plot.Height, Theme.Line);
                ui.TextAt(TickText[i], Theme.F(10.5f), Theme.Muted,
                          new Rectangle(x - 18, Plot.Bottom + 3, 36, 16), "c");
            }

            using (ui.Clip(Plot))
            {
                for (int i = 0; i < list.Count; i++)
                {
                    Series s = list[i];
                    if (s.F == null || s.Db == null || s.F.Length < 2) continue;
                    List<PointF> pts = new List<PointF>(s.F.Length);
                    for (int k = 0; k < s.F.Length; k++)
                    {
                        double v = s.Db[k];
                        if (double.IsNaN(v) || double.IsInfinity(v)) continue;
                        pts.Add(new PointF(X(s.F[k]), Y(Math.Max(DbMin - 6, Math.Min(DbMax + 6, v)))));
                    }
                    if (pts.Count < 2) continue;

                    if (s.Fill)
                    {
                        using (GraphicsPath fill = new GraphicsPath())
                        {
                            fill.AddLines(pts.ToArray());
                            fill.AddLine(pts[pts.Count - 1].X, Y(0), pts[0].X, Y(0));
                            fill.CloseFigure();
                            using (LinearGradientBrush b = new LinearGradientBrush(
                                new Rectangle(Plot.X, Plot.Y, Math.Max(1, Plot.Width), Math.Max(1, Plot.Height)),
                                Theme.Alpha(s.C, 46), Theme.Alpha(s.C, 4), LinearGradientMode.Vertical))
                                ui.G.FillPath(b, fill);
                        }
                    }

                    using (Pen p = new Pen(s.Dim ? Theme.Alpha(s.C, 120) : s.C, s.Width))
                    {
                        p.LineJoin = LineJoin.Round;
                        if (s.Dash) p.DashStyle = DashStyle.Dash;
                        ui.G.DrawLines(p, pts.ToArray());
                    }
                }
            }

            if (HoverHz >= FMin && HoverHz <= FMax)
            {
                int x = X(HoverHz);
                using (Pen p = new Pen(Theme.Alpha(Theme.Fg, 60), 1f))
                    ui.G.DrawLine(p, x, Plot.Y, x, Plot.Bottom);
                ui.TextAt(Channels.Hz(HoverHz) + " Hz", Theme.F(10.5f, true), Theme.Dim,
                          new Rectangle(x - 34, Plot.Y + 2, 68, 15), "c");
            }

            ui.StrokeRound(Plot, 0, Theme.Alpha(Theme.Line, 200));
        }

        /// <summary>The hover readout, one line per series, drawn where the caller asks.</summary>
        public void Readout(Ui ui, IList<Series> list, Rectangle r)
        {
            if (HoverHz < FMin || HoverHz > FMax) return;
            int x = X(HoverHz) + 8;
            int y = Plot.Y + 18;
            int w = 176;
            if (x + w > Plot.Right) x = X(HoverHz) - w - 8;
            int h = 16 + list.Count * 15;
            ui.FillRound(new Rectangle(x, y, w, h), 3, Theme.Deep);
            ui.StrokeRound(new Rectangle(x, y, w, h), 3, Theme.Line);
            ui.Text(Channels.Hz(HoverHz) + " Hz", Theme.F(10.5f, true), Theme.Fg, x + 8, y + 2, w - 16, 14);
            for (int i = 0; i < list.Count; i++)
            {
                Series s = list[i];
                if (s.F == null) continue;
                double v = Curve.At(s.F, s.Db, HoverHz);
                int ty = y + 16 + i * 15;
                ui.Dot(x + 12, ty + 7, 3, s.C);
                ui.Text(s.Name, Theme.F(10.5f), Theme.Dim, x + 20, ty, w - 84, 14);
                ui.TextAt(Channels.Db(v, 1), Theme.F(10.5f, true), s.C,
                          new Rectangle(x + w - 62, ty, 54, 14), "r");
            }
        }
    }
}
