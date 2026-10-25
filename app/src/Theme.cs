using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace DreTunes
{
    /// <summary>
    /// The visual language, inherited rather than invented.
    ///
    /// Every colour here is already in this project: web\index.html's :root block, which was
    /// itself lifted from the vendor's own Qt stylesheet recovered from EZY-DSP68.exe
    /// (evidence\ezydsp-qss.txt: #26282c surfaces, #0f0f0f deep, #0BABF2 accent, #f92638
    /// danger, #ededed text). Two additions are stated rather than smuggled in:
    ///
    ///   Warn  #e0a53d  - required by the brief for a swap that cannot be proven (amber), and
    ///                    used nowhere else, so amber always means "unproven".
    ///   Muted #6f767e  - the hairline-level grey for values that are known to be unread.
    ///
    /// Green means measured or verified. Red means a failure. Nothing decorative is coloured.
    /// </summary>
    public static class Theme
    {
        public static readonly Color Bg = FromHex(0x26282c);
        public static readonly Color Panel = FromHex(0x2e2d2d);
        public static readonly Color PanelHi = FromHex(0x333335);
        public static readonly Color Line = FromHex(0x2f3a41);
        public static readonly Color Well = FromHex(0x111418);
        public static readonly Color Deep = FromHex(0x0f0f0f);
        public static readonly Color Fg = FromHex(0xededed);
        public static readonly Color Dim = FromHex(0x999999);
        public static readonly Color Muted = FromHex(0x6f767e);
        public static readonly Color Accent = FromHex(0x0babf2);
        public static readonly Color AccentDim = FromHex(0x0e9ee7);
        public static readonly Color Curve = FromHex(0x84bf5d);
        public static readonly Color Ok = FromHex(0x84bf5d);
        public static readonly Color Bad = FromHex(0xf92638);
        public static readonly Color Warn = FromHex(0xe0a53d);

        public const string Family = "Segoe UI";

        private static readonly Dictionary<string, Font> Fonts = new Dictionary<string, Font>();

        /// <summary>A font sized in logical pixels (the layout is designed in px, not points).</summary>
        public static Font F(float px, bool bold, bool italic)
        {
            string key = ((int)Math.Round(px * 4)).ToString() + (bold ? "b" : "r") + (italic ? "i" : "n");
            Font f;
            if (Fonts.TryGetValue(key, out f)) return f;
            FontStyle st = bold ? FontStyle.Bold : FontStyle.Regular;
            if (italic) st = st | FontStyle.Italic;
            f = new Font(Family, px * 0.75f, st, GraphicsUnit.Point);
            Fonts[key] = f;
            return f;
        }

        public static Font F(float px, bool bold) { return F(px, bold, false); }
        public static Font F(float px) { return F(px, false, false); }

        public static Color FromHex(int rgb)
        {
            return Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        }

        public static Color Alpha(Color c, int a)
        {
            return Color.FromArgb(a, c.R, c.G, c.B);
        }

        public static Color Mix(Color a, Color b, double t)
        {
            if (t < 0) t = 0; if (t > 1) t = 1;
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        /// <summary>Green / amber / red for a three-state verdict, used by every surface.</summary>
        public static Color Verdict(int level)
        {
            if (level > 0) return Ok;
            if (level == 0) return Warn;
            return Bad;
        }

        public static GraphicsPath Round(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            int d = radius * 2;
            if (d <= 0 || r.Width < d || r.Height < d)
            {
                p.AddRectangle(r);
                return p;
            }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void UseSmooth(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        }
    }
}
