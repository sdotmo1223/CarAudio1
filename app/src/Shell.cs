using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DreTunes
{
    /// <summary>Shared chrome: the rail, the header and the few shapes every surface repeats.</summary>
    public static class Chrome
    {
        public const int Rail = 152;
        public const int Header = 86;

        public static readonly string[] Nav = { "Tune", "Library", "Measurements", "Rig", "Showcase" };

        public static readonly string[] Blurb =
        {
            "Every channel, its crossovers, and where each number came from.",
            "Every tune on disk, what it is, and a swap you can prove.",
            "What was measured, what was fitted, and what the fit predicts.",
            "Readiness, crossovers, revert points, and what is still unproven.",
            "Ninety seconds, start to finish, with no destructive write."
        };

        public static Rectangle Content(int w, int h)
        {
            return new Rectangle(Rail + 24, Header + 16, w - Rail - 48, h - Header - 32);
        }

        /// <summary>Small uppercase section label, the vendor's own 10 px QSS voice.</summary>
        public static void Section(Ui ui, string text, int x, int y, int w, Color? c)
        {
            ui.Micro(text, x, y, w, c.HasValue ? c.Value : Theme.Dim);
        }

        /// <summary>A value with its unit and a label under it. The unit is never implied.</summary>
        public static void Value(Ui ui, Rectangle r, string label, string value, string unit, Color color)
        {
            Section(ui, label, r.X, r.Y, r.Width, Theme.Muted);
            Font fv = Theme.F(26f, true);
            float wv = ui.Measure(value, fv).Width;
            ui.Text(value, fv, color, r.X, r.Y + 16, r.Width, 34);
            if (!string.IsNullOrEmpty(unit))
                ui.Text(unit, Theme.F(11f), Theme.Muted, r.X + (int)wv + 4, r.Y + 34, 60, 16);
        }

        /// <summary>Amber means "unproven", and only that. Red is a failure, green is measured.</summary>
        public static Color LevelColor(int level)
        {
            if (level > 0) return Theme.Ok;
            if (level == 0) return Theme.Warn;
            return Theme.Bad;
        }

        public static string Ago(DateTime at)
        {
            if (at == DateTime.MinValue) return "never";
            double s = (DateTime.Now - at).TotalSeconds;
            if (s < 2) return "just now";
            if (s < 60) return (int)s + " s ago";
            if (s < 3600) return (int)(s / 60) + " min ago";
            return at.ToString("HH:mm");
        }

        public static string Stamp(DateTime at)
        {
            return at == DateTime.MinValue ? "never" : at.ToString("HH:mm:ss");
        }

        public static string Ellipsis(Ui ui, string s, Font f, int width)
        {
            if (s == null) return "";
            if (ui.Measure(s, f).Width <= width) return s;
            string t = s;
            while (t.Length > 3 && ui.Measure(t + "\u2026", f).Width > width) t = t.Substring(0, t.Length - 1);
            return t + "\u2026";
        }

        /// <summary>The one-line provenance chip used above curves and tables.</summary>
        public static int Provenance(Ui ui, int x, int y, string text, Color c)
        {
            Font f = Theme.F(10.5f);
            int w = (int)Math.Ceiling(ui.Measure(text, f).Width) + 18;
            Rectangle r = new Rectangle(x, y, w, 19);
            ui.FillRound(r, 3, Theme.Alpha(c, 14));
            ui.StrokeRound(r, 3, Theme.Alpha(c, 70));
            ui.TextAt(text, f, c, new Rectangle(x + 9, y, w - 12, 19), "l");
            return w;
        }
    }

    /// <summary>
    /// The pointer in logical coordinates, published for charts to resolve a hover frequency.
    /// A chart's axis is a property of its rectangle, not of the widget toolkit, so the readout
    /// is computed during paint from this one point.
    /// </summary>
    public static class Pointer
    {
        public static Point At = new Point(-1000, -1000);
    }

    /// <summary>
    /// The window. It owns the paint loop, the click map and the keyboard, and nothing else -
    /// every fact it draws comes from AppState, which timestamps it.
    ///
    /// One deliberate oddity: the whole UI is painted into a 1366x730 logical canvas and scaled
    /// once by the DPI factor. The layout was designed for the actual laptop screen, and a
    /// layout that reflows at every size is a layout that is only right at one of them.
    /// </summary>
    public sealed class MainForm : Form
    {
        private readonly AppState _s;
        private readonly Ui _ui = new Ui();
        private readonly Timer _timer = new Timer();
        private bool _dirty = true;
        private Point _hover = new Point(-1000, -1000);
        private float _scale = 1f;

        public MainForm(AppState state)
        {
            _s = state;
            Text = "DreTunes";
            BackColor = Theme.Bg;
            ForeColor = Theme.Fg;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(1180, 700);
            KeyPreview = true;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            try
            {
                string ico = System.IO.Path.Combine(_s.Root, "DreTunes.ico");
                if (System.IO.File.Exists(ico)) Icon = new Icon(ico);
            }
            catch { }

            _s.Changed += delegate { _dirty = true; };
            _timer.Interval = 400;
            _timer.Tick += delegate
            {
                _s.TourTick();
                if (_dirty || _s.TourPlaying || _s.Swapping) Invalidate();
                _dirty = false;
            };
            _timer.Start();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int on = 1;
                // 20 is DWMWA_USE_IMMERSIVE_DARK_MODE on Win10 2004+; 19 is the earlier build.
                if (DwmSetWindowAttribute(Handle, 20, ref on, 4) != 0)
                    DwmSetWindowAttribute(Handle, 19, ref on, 4);
            }
            catch { }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            WindowState = FormWindowState.Maximized;
        }

        // ------------------------------------------------------------------ painting

        protected override void OnPaint(PaintEventArgs e)
        {
            _scale = e.Graphics.DpiX / 96f;
            if (_scale <= 0.1f) _scale = 1f;
            Size logical = new Size(
                (int)Math.Round(ClientSize.Width / _scale),
                (int)Math.Round(ClientSize.Height / _scale));
            Render(e.Graphics, logical, _scale);
        }

        /// <summary>Paint the whole app into any Graphics. Used by OnPaint and by --shot.</summary>
        public void Render(Graphics g, Size logical, float scale)
        {
            GraphicsState st = g.Save();
            g.ScaleTransform(scale, scale);
            Theme.UseSmooth(g);
            _ui.Begin(g, scale);
            _scale = scale;

            _ui.Fill(new Rectangle(0, 0, logical.Width, logical.Height), Theme.Bg);
            PaintRail(logical);
            PaintHeader(logical);
            Rectangle content = Chrome.Content(logical.Width, logical.Height);

            switch (_s.Surface)
            {
                case 0: SurfaceTune.Paint(_s, _ui, content); break;
                case 1: SurfaceLibrary.Paint(_s, _ui, content); break;
                case 2: SurfaceMeasure.Paint(_s, _ui, content); break;
                case 3: SurfaceRig.Paint(_s, _ui, content); break;
                default: SurfaceShowcase.Paint(_s, _ui, content); break;
            }

            g.Restore(st);
        }

        private void PaintRail(Size logical)
        {
            _ui.Fill(new Rectangle(0, 0, Chrome.Rail, logical.Height), Theme.Deep);
            _ui.VLine(Chrome.Rail - 1, 0, logical.Height, Theme.Line);

            _ui.Text("Dre", Theme.F(23f, true), Theme.Fg, 22, 22, 90, 30);
            _ui.Text("Tunes", Theme.F(23f, true), Theme.Accent, 22 + 40, 22, 90, 30);
            Chrome.Section(_ui, "IN-VEHICLE TUNING", 23, 54, 120, Theme.Muted);
            _ui.HLine(23, 78, Chrome.Rail - 46, Theme.Line);

            int y = 100;
            for (int i = 0; i < Chrome.Nav.Length; i++)
            {
                Rectangle row = new Rectangle(0, y, Chrome.Rail - 1, 44);
                bool on = _s.Surface == i;
                if (on) _ui.Fill(row, Theme.Mix(Theme.Deep, Theme.Accent, 0.12));
                if (on) _ui.Fill(new Rectangle(0, y, 3, 44), Theme.Accent);
                _ui.Text(Chrome.Nav[i], Theme.F(14.5f, on), on ? Theme.Fg : Theme.Dim,
                         new Rectangle(23, y, 120, 44));
                if (on) _ui.Dot(Chrome.Rail - 22, y + 22, 3, Theme.Accent);
                int idx = i;
                _ui.Hit(row, delegate { _s.Surface = idx; _s.Note("view: " + Chrome.Nav[idx]); });
                y += 44;
            }

            // ---- the honest footer: what the app is looking at, in three lines
            int fy = logical.Height - 156;
            _ui.HLine(23, fy - 14, Chrome.Rail - 46, Theme.Line);

            bool live = _s.Rig.DeviceLive;
            Color dc = live ? Theme.Ok : (_s.Rig.BridgeUp ? Theme.Warn : Theme.Bad);
            _ui.Dot(29, fy + 6, 4, dc);
            _ui.Text(live ? "DSP live" : (_s.Rig.BridgeUp ? "DSP not reading" : "bridge down"),
                     Theme.F(12f, true), dc, 40, fy - 4, 105, 20);

            // Only a live status read earns the word "playing". Otherwise the number is the
            // bridge's remembered value and is labelled as such - the rail is the one line always
            // on screen, so it is the worst place to overstate.
            string memLine;
            if (!_s.Rig.DeviceLive) memLine = _s.Rig.Mem >= 0 ? "memory " + (_s.Rig.Mem + 1) + ", bridge label" : "memory unknown";
            else memLine = _s.Rig.Mem >= 0 ? "memory " + (_s.Rig.Mem + 1) + " playing" : "memory unknown";
            _ui.Text(memLine, Theme.F(11.5f), _s.Rig.DeviceLive ? Theme.Dim : Theme.Muted,
                     24, fy + 22, 124, 18);
            _ui.Text("bridge :8585", Theme.F(11.5f),
                     _s.Rig.BridgeUp ? Theme.Dim : Theme.Bad, 24, fy + 42, 118, 18);
            _ui.Text(_s.Rig.RtaUp ? "rta :8586" : "rta down", Theme.F(11.5f),
                     _s.Rig.RtaUp ? Theme.Dim : Theme.Muted, 24, fy + 62, 118, 18);

            _ui.Text("v1.0 \u00b7 offline", Theme.F(10.5f), Theme.Muted, 24, logical.Height - 30, 110, 16);
        }

        private void PaintHeader(Size logical)
        {
            int w = logical.Width - Chrome.Rail;
            _ui.Fill(new Rectangle(Chrome.Rail, 0, w, Chrome.Header), Theme.Bg);
            _ui.HLine(Chrome.Rail, Chrome.Header - 1, w, Theme.Line);

            int x = Chrome.Rail + 24;
            string title = Chrome.Nav[Math.Max(0, Math.Min(Chrome.Nav.Length - 1, _s.Surface))];
            _ui.Text(title, Theme.F(23f, true), Theme.Fg, x, 16, 300, 30);
            _ui.Text(Chrome.Blurb[Math.Max(0, Math.Min(Chrome.Blurb.Length - 1, _s.Surface))],
                     Theme.F(12f), Theme.Dim, x, 46, 620, 20);

            // ---- status chips, right aligned
            int cx = logical.Width - 24;
            string clock = DateTime.Now.ToString("HH:mm:ss");
            Font fc = Theme.F(11.5f, true);
            int cw = (int)Math.Ceiling(_ui.Measure(clock, fc).Width) + 4;
            _ui.TextAt(clock, fc, Theme.Muted, new Rectangle(cx - cw, 14, cw, 20), "r");
            cx -= cw + 16;

            int level;
            string word;
            if (_s.Rig.DeviceLive) { level = 1; word = "DSP LIVE"; }
            else if (_s.Rig.BridgeUp && _s.Rig.DspOpen) { level = 0; word = "NO RECENT READ"; }
            else if (_s.Rig.BridgeUp) { level = -1; word = "DSP NOT CONNECTED"; }
            else { level = -1; word = "BRIDGE DOWN"; }
            int bw = BadgeWidth(_ui, word);
            _ui.Badge(cx - bw, 13, word, Chrome.LevelColor(level));
            cx -= bw + 8;

            if (_s.Rig.RtaUp)
            {
                bool umik = _s.Rig.MicDevice.IndexOf("UMIK", StringComparison.OrdinalIgnoreCase) >= 0;
                string mic = umik ? "UMIK-1" : "WRONG MIC";
                int mw = BadgeWidth(_ui, mic);
                _ui.Badge(cx - mw, 13, mic, umik ? Theme.Ok : Theme.Warn);
                cx -= mw + 8;
            }
            else
            {
                int mw = BadgeWidth(_ui, "NO RTA");
                _ui.Badge(cx - mw, 13, "NO RTA", Theme.Bad);
                cx -= mw + 8;
            }

            // second row right: the three key readings, no colour without a label
            string right = "";
            if (_s.Rig.DeviceLive)
                right = "master " + Channels.Db(_s.Rig.Master, 1) + " dB  \u00b7  " +
                        (_s.Rig.Muted ? "SELF MUTE ON" : "not muted") + "  \u00b7  " +
                        (_s.Rig.SourceName.Length > 0 ? _s.Rig.SourceName : "source unknown");
            else if (_s.Rig.BridgeUp)
                right = "last device read " + Chrome.Ago(_s.Rig.StatusAt);
            else
                right = "no bridge on 127.0.0.1:8585";
            _ui.TextAt(right, Theme.F(11.5f), _s.Rig.Muted ? Theme.Warn : Theme.Dim,
                       new Rectangle(Chrome.Rail + 600, 46, logical.Width - Chrome.Rail - 624, 20), "r");

            // last action, left of the clock row
            if (_s.Rig.LastAction.Length > 0)
                _ui.TextAt(Chrome.Ellipsis(_ui, _s.Rig.LastAction, Theme.F(10.5f), 420),
                           Theme.F(10.5f), Theme.Muted,
                           new Rectangle(x, 18, 460, 18), "l");
        }

        private static int BadgeWidth(Ui ui, string text)
        {
            return (int)Math.Ceiling(ui.Measure(text, Theme.F(11.5f, true)).Width) + 26;
        }

        // ------------------------------------------------------------------ input

        private Point Logical(Point p)
        {
            float s = _scale <= 0.1f ? 1f : _scale;
            return new Point((int)Math.Round(p.X / s), (int)Math.Round(p.Y / s));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            _hover = Logical(e.Location);
            Pointer.At = _hover;
            Cursor = _ui.IsOver(_hover) ? Cursors.Hand : Cursors.Default;
            if (_s.Surface == 0 || _s.Surface == 2 || _s.Surface == 1) Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            _ui.Click(Logical(e.Location));
            _dirty = true;
            Invalidate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode >= Keys.D1 && e.KeyCode <= Keys.D5)
            {
                _s.Surface = e.KeyCode - Keys.D1;
                _s.Note("view: " + Chrome.Nav[_s.Surface]);
            }
            else if (e.KeyCode == Keys.F5)
            {
                _s.LoadLibrary();
                _s.LoadMeasures();
                _s.RunDoctor();
            }
            else if (e.KeyCode == Keys.Left && _s.Surface == 4)
            {
                if (_s.ShowStep > 0) _s.ShowStep--;
                _s.TourPlaying = false;
            }
            else if (e.KeyCode == Keys.Right && _s.Surface == 4)
            {
                if (_s.ShowStep < 5) _s.ShowStep++;
                _s.TourPlaying = false;
            }
            else if (e.KeyCode == Keys.Escape && _s.TourPlaying)
            {
                _s.TourPlaying = false;
            }
            _dirty = true;
            Invalidate();
        }

        /// <summary>For --shot: render one image at the real client size without showing a window.</summary>
        public Bitmap Shot(Size logical, float scale)
        {
            Bitmap bmp = new Bitmap(Math.Max(1, (int)(logical.Width * scale)),
                                    Math.Max(1, (int)(logical.Height * scale)));
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Theme.Bg);
                Render(g, logical, scale);
            }
            return bmp;
        }
    }
}
