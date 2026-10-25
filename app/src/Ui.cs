using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace DreTunes
{
    /// <summary>
    /// A very small immediate-mode drawing layer.
    ///
    /// The app paints itself instead of arranging controls, for three reasons that matter in a
    /// car: (1) the numbers can be large and exactly placed, (2) nothing inherits a light theme
    /// or a system animation, (3) there is no control tree to go stale when the rig state
    /// changes. Interaction is the one thing this buys less easily: OnPaint records the
    /// rectangles it drew as clickable, and OnMouseClick hit-tests that list. A rectangle only
    /// becomes clickable by having been painted, which is fine because every surface repaints
    /// when its data changes.
    /// </summary>
    public sealed class Ui
    {
        public Graphics G;
        public float S = 1f;                    // dpi scale; the Graphics transform is already set

        // Named Hot, not Hit: a nested type cannot share a name with the Hit() method below.
        private sealed class Hot
        {
            public Rectangle R;
            public Action A;
            public Cursor Cursor;
        }

        private readonly List<Hot> _hits = new List<Hot>();
        private static readonly StringFormat SfLeft = MakeSf(StringAlignment.Near);
        private static readonly StringFormat SfCenter = MakeSf(StringAlignment.Center);
        private static readonly StringFormat SfRight = MakeSf(StringAlignment.Far);

        private static StringFormat MakeSf(StringAlignment a)
        {
            StringFormat sf = (StringFormat)StringFormat.GenericTypographic.Clone();
            sf.Alignment = a;
            sf.LineAlignment = StringAlignment.Center;
            sf.FormatFlags |= StringFormatFlags.NoWrap;
            sf.Trimming = StringTrimming.None;
            return sf;
        }

        public void Begin(Graphics g, float scale)
        {
            G = g;
            S = scale;
            _hits.Clear();
            Theme.UseSmooth(g);
        }

        public IList<Rectangle> Hits
        {
            get
            {
                List<Rectangle> r = new List<Rectangle>();
                foreach (Hot h in _hits) r.Add(h.R);
                return r;
            }
        }

        public void Hit(Rectangle r, Action a)
        {
            Hot h = new Hot();
            h.R = r;
            h.A = a;
            h.Cursor = Cursors.Hand;
            _hits.Add(h);
        }

        public bool Click(Point logical)
        {
            // Reverse order: the last thing painted is on top.
            for (int i = _hits.Count - 1; i >= 0; i--)
            {
                if (_hits[i].R.Contains(logical))
                {
                    if (_hits[i].A != null) _hits[i].A();
                    return true;
                }
            }
            return false;
        }

        public bool IsOver(Point logical)
        {
            for (int i = 0; i < _hits.Count; i++) if (_hits[i].R.Contains(logical)) return true;
            return false;
        }

        /// <summary>Hover test for a region about to be painted (not yet in the hit list).</summary>
        public bool IsOver(Rectangle logical)
        {
            return logical.Contains(Pointer.At);
        }

        // ------------------------------------------------------------------ primitives

        public void Fill(Rectangle r, Color c)
        {
            using (SolidBrush b = new SolidBrush(c)) G.FillRectangle(b, r);
        }

        public void FillRound(Rectangle r, int radius, Color c)
        {
            using (GraphicsPath p = Theme.Round(r, radius))
            using (SolidBrush b = new SolidBrush(c)) G.FillPath(b, p);
        }

        public void StrokeRound(Rectangle r, int radius, Color c)
        {
            using (GraphicsPath p = Theme.Round(r, radius))
            using (Pen pen = new Pen(c, 1f)) G.DrawPath(pen, p);
        }

        /// <summary>A one-pixel hairline that lands on the pixel grid instead of straddling it.</summary>
        public void HLine(int x, int y, int w, Color c)
        {
            Fill(new Rectangle(x, y, w, 1), c);
        }

        public void VLine(int x, int y, int h, Color c)
        {
            Fill(new Rectangle(x, y, 1, h), c);
        }

        public void Dot(int cx, int cy, int r, Color c)
        {
            using (SolidBrush b = new SolidBrush(c)) G.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
        }

        // ------------------------------------------------------------------ text

        public SizeF Measure(string s, Font f)
        {
            return G.MeasureString(s, f, PointF.Empty, SfLeft);
        }

        public void Text(string s, Font f, Color c, int x, int y, int w, int h)
        {
            TextAt(s, f, c, new Rectangle(x, y, w, h), "l");
        }

        public void Text(string s, Font f, Color c, Rectangle r)
        {
            TextAt(s, f, c, r, "l");
        }

        public void TextAt(string s, Font f, Color c, Rectangle r, string align)
        {
            if (string.IsNullOrEmpty(s)) return;
            StringFormat sf = align == "c" ? SfCenter : (align == "r" ? SfRight : SfLeft);
            using (SolidBrush b = new SolidBrush(c))
            {
                float y = r.Y + (r.Height - Measure(s, f).Height) / 2f;
                G.DrawString(s, f, b, new RectangleF(r.X, y, r.Width, r.Height), sf);
            }
        }

        /// <summary>
        /// Small uppercase label with letter-spacing - the vendor's own look (its QSS uses
        /// 10-11 px labels) and the one bit of typography GDI+ will not do for us.
        /// </summary>
        public void Micro(string s, int x, int y, int w, Color c)
        {
            Micro(s, x, y, w, c, 0.9f);
        }

        /// <summary>
        /// Advance width of one glyph. A space has to be measured inside a run, because GDI+
        /// MeasureString trims trailing spaces - measuring " " returns ~0 and every space in a
        /// tracked label then collapses ("LIVEvs SNAPSHOT").
        /// </summary>
        private float Advance(string ch, Font f)
        {
            if (ch == " ")
            {
                float bar = Measure("|", f).Width;
                return Math.Max(0f, Measure("| |", f).Width - 2f * bar);
            }
            return Measure(ch, f).Width;
        }

        public void Micro(string s, int x, int y, int w, Color c, float track)
        {
            if (string.IsNullOrEmpty(s)) return;
            Font f = Theme.F(10.5f, true);
            using (SolidBrush b = new SolidBrush(c))
            {
                float cx = x;
                float limit = x + w;
                for (int i = 0; i < s.Length; i++)
                {
                    string ch = s.Substring(i, 1);
                    float adv = Advance(ch, f);
                    // A tracked label is drawn glyph by glyph, so it has to stop itself. Truncating
                    // here (instead of letting the loop run on) is what keeps one section label
                    // from bleeding into the next column.
                    if (cx + adv + track > limit)
                    {
                        if (w > 14) G.DrawString("\u2026", f, b, cx - track, y);
                        return;
                    }
                    if (ch != " ") G.DrawString(ch, f, b, cx, y);
                    cx += adv + track;
                }
            }
        }

        public int MicroWidth(string s, float track)
        {
            Font f = Theme.F(10.5f, true);
            float cx = 0;
            for (int i = 0; i < s.Length; i++) cx += Advance(s.Substring(i, 1), f) + track;
            return (int)Math.Ceiling(cx);
        }

        /// <summary>
        /// Word-wrapped prose. GDI+ can wrap on its own, but it cannot tell us how tall the block
        /// became, and these surfaces place text in fixed rectangles - so the wrap is explicit.
        /// Returns the number of lines drawn.
        /// </summary>
        public int Wrap(string s, Font f, Color c, Rectangle r, int lineH)
        {
            if (string.IsNullOrEmpty(s) || r.Width <= 4) return 0;
            string[] words = s.Split(' ');
            List<string> lines = new List<string>();
            string line = "";
            for (int i = 0; i < words.Length; i++)
            {
                string trial = line.Length == 0 ? words[i] : line + " " + words[i];
                if (Measure(trial, f).Width > r.Width && line.Length > 0)
                {
                    lines.Add(line);
                    line = words[i];
                }
                else line = trial;
            }
            if (line.Length > 0) lines.Add(line);

            int y = r.Y;
            using (SolidBrush b = new SolidBrush(c))
                for (int i = 0; i < lines.Count; i++)
                {
                    if (y + lineH > r.Bottom + lineH / 2) return i;
                    G.DrawString(lines[i], f, b, new PointF(r.X, y));
                    y += lineH;
                }
            return lines.Count;
        }

        // ------------------------------------------------------------------ components

        public void Card(Rectangle r)
        {
            Card(r, null);
        }

        public void Card(Rectangle r, string title)
        {
            FillRound(r, 4, Theme.Panel);
            StrokeRound(r, 4, Theme.Line);
            if (!string.IsNullOrEmpty(title))
                Micro(title, r.X + 14, r.Y + 11, r.Width - 28, Theme.Dim);
        }

        public void Well(Rectangle r)
        {
            FillRound(r, 4, Theme.Well);
            StrokeRound(r, 4, Theme.Line);
        }

        /// <summary>Text button. `kind`: 0 quiet (outline), 1 primary (accent), 2 danger, 3 amber.</summary>
        public void Button(Rectangle r, string label, int kind, Action onClick, bool enabled)
        {
            Color fg, bg, border;
            if (kind == 1) { bg = Theme.Accent; fg = Color.White; border = Theme.Accent; }
            else if (kind == 2) { bg = Color.Transparent; fg = Theme.Bad; border = Theme.Alpha(Theme.Bad, 110); }
            else if (kind == 3) { bg = Color.Transparent; fg = Theme.Warn; border = Theme.Alpha(Theme.Warn, 120); }
            else { bg = Color.Transparent; fg = Theme.Fg; border = Theme.Line; }

            if (!enabled)
            {
                fg = Theme.Muted;
                bg = Color.Transparent;
                border = Theme.Line;
            }
            if (bg != Color.Transparent) FillRound(r, 3, bg);
            StrokeRound(r, 3, border);
            TextAt(label, Theme.F(12.5f, true), fg, r, "c");
            if (enabled && onClick != null) Hit(r, onClick);
        }

        /// <summary>
        /// A selectable chip - the vendor's own control shape, and what the existing page uses
        /// for memory/path/scope selection. On = accent border and accent text, never a fill,
        /// so a screen full of chips stays quiet.
        /// </summary>
        public void Chip(Rectangle r, string label, bool on, Action onClick)
        {
            Chip(r, label, on, onClick, on ? Theme.Accent : Theme.Dim);
        }

        public void Chip(Rectangle r, string label, bool on, Action onClick, Color accent)
        {
            FillRound(r, 3, on ? Theme.Mix(Theme.Panel, accent, 0.10) : Theme.Panel);
            StrokeRound(r, 3, on ? accent : Theme.Line);
            TextAt(label, Theme.F(12f, on), on ? accent : Theme.Dim, r, "c");
            if (onClick != null) Hit(r, onClick);
        }

        /// <summary>Status badge: a filled dot plus a word. Never a colour without a word.</summary>
        public int Badge(int x, int y, string text, Color c)
        {
            Font f = Theme.F(11.5f, true);
            int w = (int)Math.Ceiling(Measure(text, f).Width) + 26;
            Rectangle r = new Rectangle(x, y, w, 22);
            FillRound(r, 11, Theme.Alpha(c, 26));
            StrokeRound(r, 11, Theme.Alpha(c, 90));
            Dot(x + 11, y + 11, 3, c);
            TextAt(text, f, c, new Rectangle(x + 18, y, w - 22, 22), "l");
            return w;
        }

        public void Bar(Rectangle r, double frac, Color c, Color track)
        {
            FillRound(r, r.Height / 2, track);
            int w = (int)Math.Round(r.Width * Math.Max(0, Math.Min(1, frac)));
            if (w > 0) FillRound(new Rectangle(r.X, r.Y, w, r.Height), r.Height / 2, c);
        }

        /// <summary>Clipping scope. Always dispose.</summary>
        public IDisposable Clip(Rectangle r)
        {
            GraphicsState st = G.Save();
            G.SetClip(r);
            return new Unclip(G, st);
        }

        private sealed class Unclip : IDisposable
        {
            private readonly Graphics _g;
            private readonly GraphicsState _st;
            public Unclip(Graphics g, GraphicsState st) { _g = g; _st = st; }
            public void Dispose() { _g.Restore(_st); }
        }
    }
}
