using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;

namespace DreTunes
{
    /// <summary>
    /// 6.2 Library and A/B swap.
    ///
    /// The list is the library - every .idsp in web\, including the six inherited files, each one
    /// labelled. Hiding them would misrepresent what is on disk, and presenting them as equals
    /// would be the app's first lie, so both are visible and only two are called ours.
    ///
    /// The swap is the part that must never cheer: snapshot, write, read back, diff, verdict. The
    /// verdict is green only when the readback matches; command 11 can only ever be amber.
    /// </summary>
    public static class SurfaceLibrary
    {
        public static void Paint(AppState s, Ui ui, Rectangle a)
        {
            int x = a.X, y = a.Y, w = a.Width;

            // ---- sub-tabs
            ui.Chip(new Rectangle(x, y, 92, 28), "SWAP", s.LibraryTab == 0,
                    delegate { s.LibraryTab = 0; s.Note("library: swap view"); });
            ui.Chip(new Rectangle(x + 98, y, 104, 28), "COMPARE", s.LibraryTab == 1,
                    delegate { s.LibraryTab = 1; s.Note("library: compare view"); });

            ToolResult gate = s.SwapGate;
            string gateText = gate.Ran
                ? "library gate: " + gate.Summary + " \u00b7 " + Chrome.Stamp(gate.At)
                : "library gate: not run this session (Rig \u203a Proof runs it)";
            ui.TextAt(gateText, Theme.F(10.5f), gate.Ran ? (gate.Pass ? Theme.Dim : Theme.Bad) : Theme.Muted,
                      new Rectangle(x + 220, y, w - 220, 28), "r");

            Rectangle body = new Rectangle(x, y + 40, w, a.Height - 40);
            if (s.LibraryTab == 0) PaintSwap(s, ui, body);
            else PaintCompare(s, ui, body);
        }

        // ------------------------------------------------------------------ swap

        private static void PaintSwap(AppState s, Ui ui, Rectangle r)
        {
            Rectangle list = new Rectangle(r.X, r.Y, 402, r.Height);
            ui.Card(list);
            Chrome.Section(ui, "TUNE LIBRARY \u00b7 .IDSP IN web\\", list.X + 14, list.Y + 11,
                           list.Width - 28, Theme.Dim);

            int rowY = list.Y + 32;
            int rowH = 58;
            for (int i = 0; i < s.Library.Count; i++)
            {
                TuneRow(s, ui, s.Library[i], i, new Rectangle(list.X + 8, rowY, list.Width - 16, rowH - 4));
                rowY += rowH;
                if (rowY > list.Bottom - 20) break;
            }
            if (s.Library.Count == 0)
                ui.Text(s.LibraryLoading ? "reading web\\*.idsp\u2026" : "No .idsp files in web\\.",
                        Theme.F(12f), Theme.Dim, list.X + 14, list.Y + 40, list.Width - 28, 18);

            int rx = list.Right + 12;
            int rw = r.Right - rx;

            TuneFile t = s.Selected;
            Rectangle detail = new Rectangle(rx, r.Y, rw, 150);
            PaintDetail(ui, detail, t);

            Rectangle target = new Rectangle(rx, r.Y + 162, rw, 118);
            PaintTarget(s, ui, target);

            Rectangle action = new Rectangle(rx, r.Y + 292, rw, 44);
            PaintAction(s, ui, action, t);

            Rectangle verdict = new Rectangle(rx, r.Y + 348, rw, r.Height - 348);
            PaintVerdict(s, ui, verdict);
        }

        private static void TuneRow(AppState s, Ui ui, TuneFile t, int index, Rectangle r)
        {
            if (t == null) return;
            bool on = s.SelectedTune == index;
            Provenance p = ProvenanceTable.Of(t.FileName);
            if (on) ui.FillRound(r, 4, Theme.Mix(Theme.Panel, Theme.Accent, 0.10));
            else if (ui.IsOver(r)) ui.FillRound(r, 4, Theme.PanelHi);
            if (on) ui.StrokeRound(r, 4, Theme.Accent);

            ui.Text(Chrome.Ellipsis(ui, t.Stem, Theme.F(13.5f, true), r.Width - 150),
                    Theme.F(13.5f, !p.Authored), p.Authored ? Theme.Fg : Theme.Dim,
                    r.X + 10, r.Y + 6, r.Width - 150, 20);

            ui.TextAt(t.LiveBands + " bands", Theme.F(10.5f), Theme.Muted,
                      new Rectangle(r.Right - 110, r.Y + 8, 100, 16), "r");

            string line = (p.Authored ? "AUTHORED \u00b7 " : "INHERITED \u00b7 ") + p.Kind.ToUpperInvariant() +
                          "  \u00b7  " + t.Modified.ToString("d MMM HH:mm") +
                          "  \u00b7  " + (t.Bytes / 1024) + " KB";
            ui.Text(Chrome.Ellipsis(ui, line, Theme.F(10f), r.Width - 20), Theme.F(10f),
                    p.Authored ? Theme.Dim : Theme.Muted, r.X + 10, r.Y + 28, r.Width - 20, 14);

            if (t.MemoriesIdentical)
                ui.Text("all 4 memory blocks identical", Theme.F(9.5f), Theme.Muted,
                        r.X + 10, r.Y + 42, r.Width - 20, 12);
            else
                ui.Text("memory blocks DIFFER - the block matters", Theme.F(9.5f), Theme.Warn,
                        r.X + 10, r.Y + 42, r.Width - 20, 12);

            ui.Hit(r, delegate
            {
                s.SelectedTune = index;
                s.Note("selected " + t.FileName + " (" + p.Kind + ")");
            });
        }

        private static void PaintDetail(Ui ui, Rectangle r, TuneFile t)
        {
            ui.Card(r);
            Chrome.Section(ui, "SELECTED TUNE", r.X + 14, r.Y + 11, 200, Theme.Dim);
            if (t == null)
            {
                ui.Text("Nothing selected.", Theme.F(12f), Theme.Dim, r.X + 14, r.Y + 36, r.Width - 28, 18);
                return;
            }
            Provenance p = ProvenanceTable.Of(t.FileName);
            ui.Text(Chrome.Ellipsis(ui, t.Stem, Theme.F(18f, true), r.Width - 28), Theme.F(18f, true), Theme.Fg,
                    r.X + 14, r.Y + 28, r.Width - 28, 24);

            int cx = r.X + 14;
            cx += Chrome.Provenance(ui, cx, r.Y + 56, p.Authored ? "AUTHORED" : "INHERITED",
                                    p.Authored ? Theme.Accent : Theme.Muted) + 6;
            Chrome.Provenance(ui, cx, r.Y + 56, p.Kind.ToUpperInvariant(), Theme.Muted);

            ui.Wrap(p.Note, Theme.F(11.5f), Theme.Dim, new Rectangle(r.X + 14, r.Y + 82, r.Width - 28, 46), 16);

            string meta = t.LiveBands + " bands on the 8 live paths  \u00b7  max Q " +
                          t.LiveMaxQ.ToString("0.0") + "  \u00b7  " + t.FileName + "  \u00b7  " +
                          t.Modified.ToString("yyyy-MM-dd HH:mm");
            ui.Text(Chrome.Ellipsis(ui, meta, Theme.F(10.5f), r.Width - 28), Theme.F(10.5f), Theme.Muted,
                    r.X + 14, r.Bottom - 20, r.Width - 28, 14);

            if (!t.MemoriesIdentical)
                ui.Text("This file's four memory blocks are not the same tune.",
                        Theme.F(10.5f), Theme.Warn, r.X + 14, r.Bottom - 36, r.Width - 28, 14);
        }

        private static void PaintTarget(AppState s, Ui ui, Rectangle r)
        {
            ui.Card(r);
            Chrome.Section(ui, "WRITE TARGET \u00b7 THE GUARD", r.X + 14, r.Y + 11, 300, Theme.Dim);

            int target = s.ResolveTargetMem();
            for (int i = 0; i < 4; i++)
            {
                Rectangle chip = new Rectangle(r.X + 14 + i * 78, r.Y + 30, 70, 30);
                bool on = target == i;
                // "playing" is a claim about the amp, so it needs a live status read. The bridge's
                // remembered memory number is shown on Now, labelled as a label.
                bool playing = s.Rig.DeviceLive && s.Rig.Mem == i;
                Color c = playing ? Theme.Ok : Theme.Accent;
                ui.Chip(chip, "M" + (i + 1), on, delegate { s.TargetMem = i; s.Note("write target: memory " + (i + 1) + " (index " + i + ")"); }, c);
                ui.TextAt(playing ? "index " + i + " \u00b7 playing" : "index " + i,
                          Theme.F(9f, playing), playing ? Theme.Ok : (on ? c : Theme.Muted),
                          new Rectangle(chip.X - 8, chip.Y + 32, chip.Width + 16, 12), "c");
            }

            // Explicitly disambiguate the trap: the phone page's M2 means device memory 3.
            ui.Text("The phone page's \u201cM2\u201d chip writes device memory 3. These are device indexes.",
                    Theme.F(10f), Theme.Muted, r.X + 14, r.Y + 80, r.Width - 28, 14);

            GuardResult g = Analysis.Guard(s.Rig.Mem, target, s.Rig.DeviceLive, s.OverrideAck);
            Color gc = g.Allowed ? (s.OverrideAck ? Theme.Warn : Theme.Dim) : Theme.Bad;
            ui.Text(Chrome.Ellipsis(ui, g.Reason, Theme.F(10.5f), r.Width - 28), Theme.F(10.5f), gc,
                    r.X + 14, r.Y + 98, r.Width - 28, 16);
        }

        private static void PaintAction(AppState s, Ui ui, Rectangle r, TuneFile t)
        {
            int target = s.ResolveTargetMem();
            GuardResult g = Analysis.Guard(s.Rig.Mem, target, s.Rig.DeviceLive, s.OverrideAck);
            bool canSwap = t != null && !s.Swapping && g.Allowed;

            Rectangle button = new Rectangle(r.X, r.Y, 230, 40);
            ui.Button(button, s.Swapping ? "Working\u2026" : "Swap to this tune", 1,
                delegate { s.StartSwap(t); }, canSwap);

            Rectangle overrideChip = new Rectangle(r.X + 244, r.Y + 4, 210, 32);
            ui.Chip(overrideChip, s.OverrideAck ? "OVERRIDE ARMED" : "Override guard (logged)",
                    s.OverrideAck, delegate { s.ToggleOverride(); }, Theme.Warn);

            string hint = s.Rig.DeviceLive
                ? (g.Allowed ? "Snapshot \u2192 EQ \u2192 levels \u2192 readback \u2192 diff" : "Blocked: see the guard line above")
                : "Blocked: the DSP is not live, so no write can be read back";
            ui.TextAt(hint, Theme.F(10.5f), g.Allowed ? Theme.Muted : Theme.Bad,
                      new Rectangle(r.X + 466, r.Y, r.Width - 466, 40), "l");
        }

        private static void PaintVerdict(AppState s, Ui ui, Rectangle r)
        {
            ui.Card(r);
            Chrome.Section(ui, "THE SWAP, AND WHAT IT PROVED", r.X + 14, r.Y + 11, 400, Theme.Dim);

            if (s.Swapping)
            {
                List<SwapProgress> list;
                lock (s.Progress) list = new List<SwapProgress>(s.Progress);
                int py = r.Y + 32;
                for (int i = 0; i < list.Count && py < r.Bottom - 20; i++)
                {
                    SwapProgress p = list[i];
                    bool current = i == list.Count - 1;
                    Color c = current ? Theme.Accent : Theme.Dim;
                    ui.Dot(r.X + 22, py + 8, 4, c);
                    ui.Text(p.Step, Theme.F(11f, true), c, r.X + 34, py, 130, 16);
                    ui.Text(Chrome.Ellipsis(ui, p.Message, Theme.F(11f), r.Width - 190),
                            Theme.F(11f), current ? Theme.Fg : Theme.Muted, r.X + 168, py, r.Width - 182, 16);
                    py += 22;
                }
                if (list.Count == 0)
                    ui.Text("Starting\u2026", Theme.F(11.5f), Theme.Dim, r.X + 14, r.Y + 34, r.Width - 28, 16);
                return;
            }

            SwapResult v = s.LastSwap;
            if (v == null)
            {
                ui.Text("No swap this session. The engine will snapshot first, then write, then read",
                        Theme.F(12f), Theme.Dim, r.X + 14, r.Y + 34, r.Width - 28, 18);
                ui.Text("all 8 paths back and diff them against the file before it says anything.",
                        Theme.F(12f), Theme.Dim, r.X + 14, r.Y + 52, r.Width - 28, 18);
                if (s.SwapStopped.Length > 0)
                    ui.Wrap(s.SwapStopped, Theme.F(11.5f), Theme.Bad,
                            new Rectangle(r.X + 14, r.Y + 82, r.Width - 28, 80), 16);
                return;
            }

            Color lv = Chrome.LevelColor(v.Level);
            string word = v.Verdict.ToUpperInvariant();
            Font fw = Theme.F(19f, true);
            ui.Text(Chrome.Ellipsis(ui, word, fw, r.Width - 220), fw, lv, r.X + 14, r.Y + 30, r.Width - 220, 26);

            ui.TextAt(Chrome.Stamp(v.At), Theme.F(11f), Theme.Muted,
                      new Rectangle(r.Right - 120, r.Y + 30, 106, 26), "r");

            int yy = r.Y + 62;
            int lines = ui.Wrap(v.Detail, Theme.F(11.5f), Theme.Dim,
                                new Rectangle(r.X + 14, yy, r.Width - 28, 74), 16);
            yy += Math.Max(1, lines) * 16 + 4;

            if (v.SnapshotPath.Length > 0)
            {
                ui.Text("revert point: " + Path.GetFileName(v.SnapshotPath) +
                        (v.Wrote ? "  (written before the first byte)" : "  (nothing was written)"),
                        Theme.F(10.5f), Theme.Muted, r.X + 14, yy, r.Width - 28, 14);
                yy += 18;
            }

            if (v.Diff != null)
            {
                ui.Text("readback: " + v.Diff.PathsMatched + "/" + v.Diff.Paths + " paths \u00b7 " +
                        v.BandAssertions + " band assertions \u00b7 worst gain error " +
                        v.Diff.WorstGainError.ToString("0.00") + " dB",
                        Theme.F(10.5f), v.Diff.Clean ? Theme.Dim : Theme.Bad, r.X + 14, yy, r.Width - 28, 14);
                yy += 18;
            }
            if (v.Mirror != null && v.Mirror.Read)
            {
                ui.Text("command 11 mirror: " + (v.Mirror.Diffs == 0 ? "echoes the block that was sent" :
                        v.Mirror.Diffs + " path(s) differ") + " \u00b7 not a device read",
                        Theme.F(10.5f), v.Mirror.Diffs == 0 ? Theme.Muted : Theme.Warn, r.X + 14, yy, r.Width - 28, 14);
                yy += 18;
            }

            if (v.Caveat.Length > 0 && yy < r.Bottom - 28)
            {
                Rectangle bar = new Rectangle(r.X + 14, r.Bottom - 30, r.Width - 28, 22);
                ui.FillRound(bar, 3, Theme.Alpha(Theme.Warn, 12));
                ui.TextAt(Chrome.Ellipsis(ui, v.Caveat, Theme.F(10f), bar.Width - 20), Theme.F(10f), Theme.Warn,
                          new Rectangle(bar.X + 10, bar.Y, bar.Width - 20, bar.Height), "l");
            }
        }

        // ------------------------------------------------------------------ compare

        private static void PaintCompare(AppState s, Ui ui, Rectangle r)
        {
            int colW = 286;
            PaintComparePick(s, ui, new Rectangle(r.X, r.Y, colW, r.Height), 0);
            PaintComparePick(s, ui, new Rectangle(r.X + colW + 10, r.Y, colW, r.Height), 1);

            Rectangle table = new Rectangle(r.X + (colW + 10) * 2, r.Y, r.Width - (colW + 10) * 2, r.Height);
            PaintCompareTable(s, ui, table);
        }

        private static void PaintComparePick(AppState s, Ui ui, Rectangle r, int which)
        {
            ui.Card(r);
            Chrome.Section(ui, which == 0 ? "A \u00b7 BASELINE" : "B \u00b7 COMPARISON",
                           r.X + 14, r.Y + 11, 200, Theme.Dim);
            int sel = which == 0 ? s.CompareA : s.CompareB;
            int y = r.Y + 32;
            for (int i = 0; i < s.Library.Count; i++)
            {
                TuneFile t = s.Library[i];
                Rectangle row = new Rectangle(r.X + 8, y, r.Width - 16, 42);
                bool on = sel == i;
                Provenance p = ProvenanceTable.Of(t.FileName);
                if (on) { ui.FillRound(row, 3, Theme.Mix(Theme.Panel, Theme.Accent, 0.12)); ui.StrokeRound(row, 3, Theme.Accent); }
                ui.Text(Chrome.Ellipsis(ui, t.Stem, Theme.F(11.5f, on), r.Width - 30),
                        Theme.F(11.5f, on), p.Authored ? Theme.Fg : Theme.Dim,
                        row.X + 8, row.Y + 4, row.Width - 16, 16);
                ui.Text(t.LiveBands + " bands  \u00b7  " + (p.Authored ? "authored" : "inherited"),
                        Theme.F(9.5f), Theme.Muted, row.X + 8, row.Y + 22, row.Width - 16, 14);
                int idx = i;
                ui.Hit(row, delegate
                {
                    if (which == 0) s.CompareA = idx; else s.CompareB = idx;
                    s.Note("compare " + (which == 0 ? "A" : "B") + ": " + t.Stem);
                });
                y += 44;
                if (y > r.Bottom - 50) break;
            }
        }

        private static void PaintCompareTable(AppState s, Ui ui, Rectangle r)
        {
            TuneFile a = s.TuneA, b = s.TuneB;
            TuneCompare c = Library.Compare(a, b);

            ui.Card(r);
            Chrome.Section(ui, "PER-PATH DIFFERENCE", r.X + 14, r.Y + 11, 300, Theme.Dim);

            if (a == null || b == null)
            {
                ui.Text("Pick two tunes to compare.", Theme.F(12f), Theme.Dim, r.X + 14, r.Y + 40, r.Width - 28, 18);
                return;
            }

            int y = r.Y + 32;
            int[] colX = { 14, 78, 158, 238, 300, 366 };
            string[] head = { "CH", "A dB", "B dB", "\u0394 dB", "delay A/B", "bands A/B" };
            for (int i = 0; i < head.Length; i++)
                ui.Text(head[i], Theme.F(9.5f, true), Theme.Muted, r.X + colX[i], y, 70, 14);
            ui.HLine(r.X + 14, y + 16, r.Width - 28, Theme.Line);
            y += 20;

            for (int i = 0; i < c.Rows.Count; i++)
            {
                PathDelta d = c.Rows[i];
                double delta = d.GainB - d.GainA;
                Color dc = Math.Abs(delta) < 0.05 ? Theme.Muted : (delta > 0 ? Theme.Warn : Theme.Accent);
                Rectangle row = new Rectangle(r.X + 8, y - 3, r.Width - 16, 25);
                if (s.Path == d.Path) ui.FillRound(row, 3, Theme.Mix(Theme.Panel, Theme.Accent, 0.08));
                ui.Text("CH " + (d.Path + 1), Theme.F(10.5f), Theme.Dim, r.X + colX[0], y, 50, 16);
                ui.TextAt(Channels.Db(d.GainA, 1), Theme.F(10.5f), Theme.Dim, new Rectangle(r.X + colX[1], y, 70, 16), "r");
                ui.TextAt(Channels.Db(d.GainB, 1), Theme.F(10.5f), Theme.Fg, new Rectangle(r.X + colX[2], y, 70, 16), "r");
                ui.TextAt(Channels.Db(delta, 1), Theme.F(10.5f, true), dc, new Rectangle(r.X + colX[3], y, 56, 16), "r");
                ui.TextAt(Channels.Ms(d.DelayA) + " / " + Channels.Ms(d.DelayB), Theme.F(10.5f),
                          c.DelaysIdentical ? Theme.Muted : Theme.Warn,
                          new Rectangle(r.X + colX[4], y, 66, 16), "r");
                ui.TextAt(d.BandsA + " / " + d.BandsB, Theme.F(10.5f), Theme.Muted,
                          new Rectangle(r.X + colX[5], y, 58, 16), "r");
                int path = d.Path;
                ui.Hit(row, delegate
                {
                    s.Path = path;
                    s.Note("compare row CH " + (path + 1));
                });
                y += 25;
                if (y > r.Bottom - 140) break;
            }

            ui.HLine(r.X + 14, y + 2, r.Width - 28, Theme.Line);
            y += 12;

            Rectangle sentence = new Rectangle(r.X + 14, y, r.Width - 28, r.Bottom - y - 46);
            ui.Wrap(c.Sentence, Theme.F(12.5f), Theme.Fg, sentence, 18);

            // crossover maps, side by side, so "which crossovers does each file ask for" is answered
            string mapA = Library.CrossoverMapText(a);
            string mapB = Library.CrossoverMapText(b);
            bool sameXo = string.Equals(mapA, mapB, StringComparison.Ordinal);
            ui.Text("crossovers A: " + mapA, Theme.F(10f), Theme.Muted, r.X + 14, r.Bottom - 40, r.Width - 28, 14);
            ui.Text("crossovers B: " + mapB, Theme.F(10f), Theme.Muted, r.X + 14, r.Bottom - 24, r.Width - 28, 14);
            if (!sameXo)
                ui.TextAt("files disagree - a swap writes EQ only, never crossovers",
                          Theme.F(9.5f), Theme.Warn, new Rectangle(r.X + 200, r.Bottom - 24, r.Width - 214, 14), "r");
        }
    }
}
