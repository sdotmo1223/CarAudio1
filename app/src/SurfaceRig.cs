using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;

namespace DreTunes
{
    /// <summary>
    /// 6.4 Rig - the verification layer, which is the reason the rest of this is defensible.
    ///
    /// Two sub-views rather than one long scroll: Readiness is "can this rig measure right now",
    /// Evidence is "what is on the amplifier and what can I go back to". Both are built from
    /// tools that already existed; the app runs them, parses their own counts, and reports the
    /// tool's verdict next to its own.
    /// </summary>
    public static class SurfaceRig
    {
        public static void Paint(AppState s, Ui ui, Rectangle a)
        {
            ui.Chip(new Rectangle(a.X, a.Y, 112, 28), "READINESS", s.RigTab == 0,
                    delegate { s.RigTab = 0; });
            ui.Chip(new Rectangle(a.X + 118, a.Y, 104, 28), "EVIDENCE", s.RigTab == 1,
                    delegate { s.RigTab = 1; });

            ToolResult d = s.DoctorRun;
            string line = d.Ran
                ? "doctor.ps1 \u00b7 " + Chrome.Stamp(d.At) + " \u00b7 exit " + d.ExitCode +
                  " \u00b7 " + d.Seconds.ToString("0.0") + " s"
                : "doctor.ps1 has not run in this session";
            ui.TextAt(line, Theme.F(10.5f), d.Ran ? Theme.Muted : Theme.Warn,
                      new Rectangle(a.X + 240, a.Y, a.Width - 240, 28), "r");

            Rectangle body = new Rectangle(a.X, a.Y + 40, a.Width, a.Height - 40);
            if (s.RigTab == 0) PaintReadiness(s, ui, body);
            else PaintEvidence(s, ui, body);
        }

        // ------------------------------------------------------------------ readiness

        private static void PaintReadiness(AppState s, Ui ui, Rectangle r)
        {
            Rectangle verdict = new Rectangle(r.X, r.Y, 620, 140);
            ui.Card(verdict);

            DoctorReport doc = s.Doctor;
            Color vc = !doc.Valid ? Theme.Muted
                : (doc.Fails > 0 ? Theme.Bad : (doc.Warns > 0 ? Theme.Warn : Theme.Ok));
            string word = doc.Valid ? doc.Verdict : "NOT RUN";
            ui.Text(word, Theme.F(26f, true), vc, verdict.X + 16, verdict.Y + 18, verdict.Width - 32, 34);
            ui.TextAt(doc.Valid ? "as at " + doc.At.ToString("HH:mm:ss") : "",
                      Theme.F(11f), Theme.Muted,
                      new Rectangle(verdict.Right - 150, verdict.Y + 18, 134, 34), "r");

            ui.Text(doc.Valid
                    ? doc.Fails + " blocking \u00b7 " + doc.Warns + " warning(s) \u00b7 dsp " +
                      (doc.Dsp ? "answering" : "NOT connected")
                    : "Run doctor.ps1 to get the project's own readiness verdict.",
                    Theme.F(11.5f), Theme.Dim, verdict.X + 16, verdict.Y + 56, verdict.Width - 32, 18);

            // the app's own live line, which can disagree with doctor.ps1 - show both
            string mine = s.Rig.DeviceLive
                ? "DreTunes: the DSP is live, status read " + Chrome.Ago(s.Rig.StatusAt)
                : "DreTunes: " + s.Rig.DeviceStateWord;
            ui.Text(mine, Theme.F(11f), s.Rig.DeviceLive ? Theme.Ok : Theme.Warn,
                    verdict.X + 16, verdict.Y + 78, verdict.Width - 32, 16);

            Rectangle b1 = new Rectangle(verdict.X + 16, verdict.Y + 100, 132, 30);
            ui.Button(b1, s.DoctorRunning ? "Running\u2026" : "Run doctor", 1,
                      delegate { s.RunDoctor(); }, !s.DoctorRunning);

            Rectangle b2 = new Rectangle(verdict.Right - 174, verdict.Y + 100, 158, 30);
            ui.Button(b2, s.GatesRunning ? "Running\u2026" : "Self-test comparators", 0,
                      delegate { s.RunDoctorSelfTest(); }, !s.GatesRunning);

            Rectangle rowsCard = new Rectangle(r.X, r.Y + 152, 620, r.Height - 152);
            ui.Card(rowsCard);
            Chrome.Section(ui, "READINESS ROWS \u00b7 doctor.ps1 -Json", rowsCard.X + 14, rowsCard.Y + 11,
                           400, Theme.Dim);
            if (!doc.Valid)
            {
                ui.Text("No rows: doctor.ps1 has not produced a report yet.", Theme.F(11.5f), Theme.Muted,
                        rowsCard.X + 14, rowsCard.Y + 36, rowsCard.Width - 28, 18);
            }
            else
            {
                int y = rowsCard.Y + 30;
                for (int i = 0; i < doc.Rows.Count && y < rowsCard.Bottom - 22; i++)
                {
                    DoctorRow row = doc.Rows[i];
                    Color c = row.Level == "ok" ? Theme.Ok : (row.Level == "warn" ? Theme.Warn :
                              (row.Level == "fail" ? Theme.Bad : Theme.Muted));
                    ui.Dot(rowsCard.X + 20, y + 8, 3, c);
                    ui.Text(row.Area.ToUpperInvariant(), Theme.F(9f, true), Theme.Muted,
                            rowsCard.X + 30, y, 78, 16);
                    ui.Text(Chrome.Ellipsis(ui, row.Msg, Theme.F(10.5f), rowsCard.Width - 130),
                            Theme.F(10.5f), c, rowsCard.X + 104, y, rowsCard.Width - 118, 16);
                    y += 22;
                }
            }

            // ---- right: gates
            Rectangle gates = new Rectangle(r.X + 632, r.Y, r.Width - 632, 256);
            ui.Card(gates);
            Chrome.Section(ui, "THE FOUR GATES \u00b7 RUN IN A TERMINAL, PARSED HERE", gates.X + 14,
                           gates.Y + 11, gates.Width - 28, Theme.Dim);
            int gy = gates.Y + 30;
            Gate(ui, gates, gy, "Crossover comparator", "doctor.ps1 -SelfTest \u00b7 4 cases",
                 s.DoctorSelfTest, delegate { s.RunDoctorSelfTest(); }, !s.GatesRunning);
            gy += 54;
            Gate(ui, gates, gy, "Swap library", "tune\\verify-swap-tunes.py",
                 s.SwapGate, delegate { s.RunSwapGate(); }, !s.GatesRunning);
            gy += 54;
            Gate(ui, gates, gy, "Phone page", "verify-web.ps1 \u00b7 baseline + negative control",
                 s.WebGate, delegate { s.RunWebGate(); }, !s.GatesRunning);
            gy += 54;
            Gate(ui, gates, gy, "Device readback", "tune\\verify-device.py \u00b7 read-only",
                 s.DeviceCheck, delegate { s.RunDeviceCheck(); }, !s.GatesRunning && s.Rig.DeviceLive);

            Rectangle unproven = new Rectangle(r.X + 632, r.Y + 268, r.Width - 632, r.Height - 268);
            PaintUnproven(s, ui, unproven);
        }

        private static void Gate(Ui ui, Rectangle card, int y, string title, string tool,
                                 ToolResult tr, Action run, bool enabled)
        {
            Color c = !tr.Ran ? Theme.Muted : (tr.Pass ? Theme.Ok : Theme.Bad);
            ui.Dot(card.X + 20, y + 9, 3, c);
            ui.Text(title, Theme.F(11.5f, true), Theme.Fg, card.X + 30, y - 2, 150, 18);
            string status = tr.Ran ? tr.Word + " \u00b7 " + Chrome.Stamp(tr.At) : "not run";
            // Stops short of the Run button (which starts at card.Right - 92) instead of running
            // underneath it, which is what "not run" was doing.
            ui.TextAt(Chrome.Ellipsis(ui, status, Theme.F(10f), card.Width - 286), Theme.F(10f), c,
                      new Rectangle(card.X + 176, y - 2, card.Width - 286, 18), "r");
            ui.Text(Chrome.Ellipsis(ui, tool, Theme.F(9.5f), 210), Theme.F(9.5f), Theme.Muted,
                    card.X + 30, y + 14, 210, 14);
            string detail = tr.Ran ? Chrome.Ellipsis(ui, tr.Summary, Theme.F(9.5f), card.Width - 340) : "";
            if (detail.Length > 0)
                ui.Text(detail, Theme.F(9.5f), c, card.X + 30, y + 28, card.Width - 44, 14);

            Rectangle b = new Rectangle(card.Right - 92, y, 78, 26);
            ui.Button(b, tr.Ran ? "Re-run" : "Run", 0, run, enabled);
            ui.HLine(card.X + 14, y + 46, card.Width - 28, Theme.Alpha(Theme.Line, 140));
        }

        private static void PaintUnproven(AppState s, Ui ui, Rectangle r)
        {
            ui.Card(r);
            Chrome.Section(ui, "WHAT IS STILL UNPROVEN", r.X + 14, r.Y + 11, r.Width - 28, Theme.Warn);

            int y = r.Y + 30;
            Unproven(ui, r, ref y, "Time alignment",
                     "Delays are relative, not absolute: there is no loopback cable yet, so the " +
                     "flight time from each driver cannot be measured. The numbers are the tunes', " +
                     "not a measurement of this car.");
            Unproven(ui, r, ref y, "Post-tune measurement",
                     "The newest capture (2026-10-20 19:12) is the evidence the tune was built from. " +
                     "No capture has been taken since it was written, so the result is predicted, not measured.");
            Unproven(ui, r, ref y, "Ride comparison",
                     "A and B have not been compared by ear in the car. The 16\u201317 dB bass difference " +
                     "is a fact in the files; whether it is better is not.");

            // the sub, with the only honest control: an assertion the owner makes
            ui.Dot(r.X + 20, y + 8, 3, s.SubTicked ? Theme.Ok : Theme.Warn);
            ui.Text("Sub amplifier", Theme.F(11.5f, true), Theme.Fg, r.X + 30, y, 200, 16);
            Rectangle tick = new Rectangle(r.Right - 152, y - 2, 138, 22);
            ui.Chip(tick, s.SubTicked ? "checked by owner" : "mark as checked",
                    s.SubTicked, delegate
                    {
                        s.SubTicked = !s.SubTicked;
                        s.Note(s.SubTicked
                            ? "owner asserted the sub amp is on; the app cannot see it"
                            : "sub amp tick cleared");
                    }, s.SubTicked ? Theme.Ok : Theme.Warn);
            y += 20;
            ui.Wrap("Fed from the head unit, not the DSP. No command in this app can see or change it; " +
                    "this tick is your assertion, not a reading.",
                    Theme.F(9.5f), Theme.Muted, new Rectangle(r.X + 30, y, r.Width - 44, 30), 13);
        }

        private static void Unproven(Ui ui, Rectangle card, ref int y, string title, string text)
        {
            ui.Dot(card.X + 20, y + 8, 3, Theme.Warn);
            ui.Text(title, Theme.F(11.5f, true), Theme.Fg, card.X + 30, y, 220, 16);
            y += 18;
            int lines = ui.Wrap(text, Theme.F(9.5f), Theme.Muted,
                                new Rectangle(card.X + 30, y, card.Width - 44, 40), 13);
            y += Math.Max(1, lines) * 13 + 12;
        }

        // ------------------------------------------------------------------ evidence

        private static void PaintEvidence(AppState s, Ui ui, Rectangle r)
        {
            Rectangle xo = new Rectangle(r.X, r.Y, 478, 320);
            PaintCrossovers(s, ui, xo);

            Rectangle log = new Rectangle(r.X, r.Y + 332, 478, r.Height - 332);
            PaintLog(s, ui, log);

            Rectangle snaps = new Rectangle(r.X + 490, r.Y, r.Width - 490, 340);
            PaintSnapshots(s, ui, snaps);

            Rectangle note = new Rectangle(r.X + 490, r.Y + 352, r.Width - 490, r.Height - 352);
            ui.Card(note);
            Chrome.Section(ui, "HOW A RESTORE IS PROVEN", note.X + 14, note.Y + 11, note.Width - 28, Theme.Dim);
            ui.Wrap("Restoring reads the device first and writes a fresh snapshot of what is about to be " +
                    "replaced, then writes the chosen snapshot's bands and its complete command-11 block, " +
                    "then reads all 8 paths back and compares them to the snapshot. " +
                    "apply-tune.ps1 -Restore writes and exits; this does not.",
                    Theme.F(11f), Theme.Dim, new Rectangle(note.X + 14, note.Y + 34, note.Width - 28, 90), 16);
            ui.Wrap("The file format is identical, so evidence\\tune-before-*.json is interchangeable " +
                    "between the app and the script.",
                    Theme.F(10f), Theme.Muted, new Rectangle(note.X + 14, note.Y + 120, note.Width - 28, 50), 15);
        }

        private static void PaintCrossovers(AppState s, Ui ui, Rectangle r)
        {
            ui.Card(r);
            string snap = "";
            DateTime at; int mem;
            double[] low, high; int[] lowS, highS;
            snap = Evidence.CrossoverBaseline(s.Root, out at, out mem, out low, out high, out lowS, out highS);

            // Two short pieces instead of one long tracked label: a 10.5 px letter-spaced
            // filename ran wider than the card and collided with its own words.
            Chrome.Section(ui, "CROSSOVERS \u00b7 LIVE vs SNAPSHOT", r.X + 14, r.Y + 11, 240, Theme.Dim);
            ui.TextAt(snap.Length > 0 ? Path.GetFileName(snap) : "no snapshot on disk",
                      Theme.F(9.5f), Theme.Muted,
                      new Rectangle(r.X + 252, r.Y + 8, r.Width - 266, 16), "r");

            if (!s.Rig.EqOk || s.Rig.Pb == null)
            {
                ui.Text("Not read. The crossover comparison needs a device readback.",
                        Theme.F(11.5f), Theme.Warn, r.X + 14, r.Y + 36, r.Width - 28, 18);
                return;
            }

            List<CrossoverCheck.Read> reads = new List<CrossoverCheck.Read>();
            for (int p = 0; p < Channels.Live; p++)
            {
                CrossoverCheck.Read rd = new CrossoverCheck.Read();
                rd.Path = p;

                // Both edges come off the same /api/eqread passband block: lowFc/lowSlope is the
                // high-pass, highFc/highSlope is the low-pass. This used to compare the low-pass
                // against the snapshot it was being compared to - which cannot fail - because the
                // parser threw the high edge away. A path is only comparable when both edges were
                // actually read, so a missing one is unproven rather than assumed open.
                PassBand hp = s.Rig.Pb == null ? null : s.Rig.Pb[p];
                PassBand lp = s.Rig.PbLow == null ? null : s.Rig.PbLow[p];
                rd.HasPassband = hp != null && lp != null;
                if (rd.HasPassband)
                {
                    rd.LowFc = hp.Fc;
                    rd.HighFc = lp.Fc;
                    rd.LowSlope = hp.Slope;
                    rd.HighSlope = lp.Slope;
                }
                reads.Add(rd);
            }
            CrossoverCheck.Result res = snap.Length > 0
                ? CrossoverCheck.Compare(reads, low, high, lowS, highS, Channels.Live)
                : new CrossoverCheck.Result();

            int y = r.Y + 32;
            ui.Text("CH", Theme.F(9f, true), Theme.Muted, r.X + 16, y, 40, 12);
            ui.Text("HIGH-PASS", Theme.F(9f, true), Theme.Muted, r.X + 64, y, 110, 12);
            ui.Text("LOW-PASS", Theme.F(9f, true), Theme.Muted, r.X + 200, y, 110, 12);
            ui.Text("vs SNAPSHOT", Theme.F(9f, true), Theme.Muted, r.X + 330, y, 120, 12);
            y += 18;

            for (int p = 0; p < Channels.Live; p++)
            {
                PassBand hp = s.Rig.Pb == null ? null : s.Rig.Pb[p];
                PassBand lp = s.Rig.PbLow == null ? null : s.Rig.PbLow[p];
                ui.Text("CH " + (p + 1), Theme.F(10f, true), Theme.Dim, r.X + 16, y, 44, 18);
                ui.Text(hp == null ? "\u2014" : Channels.CrossoverText(hp), Theme.F(10f),
                        hp == null ? Theme.Muted : Theme.Fg, r.X + 64, y, 130, 18);
                ui.Text(lp == null ? "not read" : Channels.CrossoverText(lp), Theme.F(10f),
                        lp == null ? Theme.Muted : Theme.Fg, r.X + 200, y, 130, 18);

                string verdict;
                Color c;
                if (snap.Length == 0) { verdict = "no baseline"; c = Theme.Muted; }
                else if (res.Open.Contains(p)) { verdict = "CHANGED"; c = Theme.Warn; }
                else if (res.Unproven.Contains(p)) { verdict = "unproven"; c = Theme.Bad; }
                else { verdict = "matches"; c = Theme.Ok; }
                ui.Text(verdict, Theme.F(10f, true), c, r.X + 330, y, 120, 18);
                y += 24;
            }

            string summary = snap.Length == 0
                ? "No crossover snapshot on disk: there is nothing to compare against."
                : (res.Clean ? "All 8 paths match the snapshot taken " + at.ToString("d MMM HH:mm") + "."
                             : res.Open.Count + " path(s) changed, " + res.Unproven.Count + " unproven" +
                               (res.UnnamedReads > 0 ? ", " + res.UnnamedReads + " read(s) named no path." : "."));
            ui.Text(Chrome.Ellipsis(ui, summary, Theme.F(10.5f), r.Width - 28),
                    Theme.F(10.5f), res.Clean && snap.Length > 0 ? Theme.Dim : Theme.Warn,
                    r.X + 14, r.Bottom - 44, r.Width - 28, 16);
            ui.Text("Reads without a path count as unproven, never as unchanged.",
                    Theme.F(9.5f), Theme.Muted, r.X + 14, r.Bottom - 24, r.Width - 28, 14);
        }

        private static void PaintSnapshots(AppState s, Ui ui, Rectangle r)
        {
            ui.Card(r);
            Chrome.Section(ui, "REVERT POINTS \u00b7 " + s.Snapshots.Count + " ON DISK", r.X + 14, r.Y + 11,
                           r.Width - 28, Theme.Dim);

            int y = r.Y + 30;
            for (int i = 0; i < s.Snapshots.Count && i < 5; i++)
            {
                SnapshotInfo sn = s.Snapshots[i];
                Rectangle row = new Rectangle(r.X + 8, y, r.Width - 16, 44);
                bool on = s.RestoreTarget == sn.Path;
                if (on) { ui.FillRound(row, 3, Theme.Mix(Theme.Panel, Theme.Warn, 0.10)); ui.StrokeRound(row, 3, Theme.Warn); }
                ui.Text(sn.At == DateTime.MinValue ? sn.FileName : sn.At.ToString("d MMM HH:mm:ss"),
                        Theme.F(11.5f, on), on ? Theme.Warn : Theme.Fg, row.X + 8, row.Y + 4, 190, 16);
                ui.Text("memory " + (sn.Mem >= 0 ? (sn.Mem + 1).ToString() : "?") + "  \u00b7  " +
                        (sn.Tag.Length > 0 ? sn.Tag : "untagged") + "  \u00b7  " + (sn.Bytes / 1024) + " KB",
                        Theme.F(9.5f), Theme.Muted, row.X + 8, row.Y + 22, row.Width - 100, 14);
                ui.TextAt((i == 0 ? "newest" : ""), Theme.F(9.5f), Theme.Muted,
                          new Rectangle(row.Right - 70, row.Y + 4, 60, 16), "r");
                string path = sn.Path;
                ui.Hit(row, delegate
                {
                    s.RestoreTarget = path;
                    s.RestoreArmed = false;
                    s.Note("revert point selected: " + Path.GetFileName(path));
                });
                y += 48;
                if (y > r.Bottom - 90) break;
            }

            if (s.Snapshots.Count == 0)
                ui.Text("No snapshots in evidence\\.", Theme.F(11.5f), Theme.Muted, r.X + 14, r.Y + 36,
                        r.Width - 28, 18);

            Rectangle button = new Rectangle(r.X + 8, r.Bottom - 40, 250, 32);
            bool canRestore = s.RestoreTarget.Length > 0 && !s.Swapping;
            ui.Button(button,
                      s.RestoreArmed ? "Click again to restore" :
                      (s.Swapping ? "Working\u2026" : "Restore selected snapshot"),
                      3, delegate
                      {
                          if (!s.RestoreArmed) { s.RestoreArmed = true; s.Note("restore armed - click again"); return; }
                          s.RestoreArmed = false;
                          s.StartRestore(s.RestoreTarget);
                      }, canRestore);

            ui.TextAt(s.RestoreTarget.Length > 0 ? Path.GetFileName(s.RestoreTarget) : "nothing selected",
                      Theme.F(10f), Theme.Muted,
                      new Rectangle(button.Right + 10, button.Y, r.Right - button.Right - 20, 32), "l");
        }

        private static void PaintLog(AppState s, Ui ui, Rectangle r)
        {
            ui.Card(r);
            Chrome.Section(ui, "ACTIVITY \u00b7 WHO ASKED FOR WHAT", r.X + 14, r.Y + 11, r.Width - 28, Theme.Dim);
            List<string> lines;
            lock (s.Log)
            {
                int start = Math.Max(0, s.Log.Count - ((r.Height - 40) / 16));
                lines = s.Log.GetRange(start, s.Log.Count - start);
            }
            int y = r.Y + 32;
            for (int i = 0; i < lines.Count && y < r.Bottom - 16; i++)
            {
                ui.Text(Chrome.Ellipsis(ui, lines[i], Theme.F(9.5f), r.Width - 28), Theme.F(9.5f),
                        lines[i].Contains("OVERRIDE") ? Theme.Warn : Theme.Muted,
                        r.X + 14, y, r.Width - 28, 14);
                y += 16;
            }
        }
    }
}
