// Copyright (c) 2026 Blesson Reji (knox-101). Space Hunter is free software under the GNU GPL v3 or later; see LICENSE.
// SPDX-License-Identifier: GPL-3.0-or-later
// Space Hunter (Windows) - the confirm window and the live cleaning window.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace SpaceHunter
{
    // Shown every time Clean is pressed: what will go, and one toggle for how (Delete or Recycle Bin).
    class ConfirmForm : Form
    {
        public bool Recycle;   // false = delete permanently (the default)
        readonly Key delKey = new Key("delete"), binKey = new Key("recycle bin");
        readonly Label hint;

        public ConfirmForm(List<Category> sel)
        {
            long total = sel.Sum(c => c.Bytes);
            var risky = sel.Where(c => !c.Safe).OrderByDescending(c => c.Bytes).ToList();
            var safe = sel.Where(c => c.Safe).ToList();
            int n = Math.Min(risky.Count, 6), listH = n * 32 + (safe.Count > 0 ? 32 : 0);
            Theme.Window(this, "Space Hunter", 560, 268 + listH);
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;

            var h = new Label { Text = "Clean " + Engine.Fmt(total) + (sel.Any(c => c.Blocked) ? " + system folders" : "") + "?", Font = Theme.Bold(17f), ForeColor = Theme.Ink, AutoSize = true, Location = Theme.P(26, 22), UseMnemonic = false };
            var lst = new CanvasPanel { Location = Theme.P(26, 70), Size = Theme.D(508, listH) };
            Font fn = Theme.CSemi(10.5f);
            lst.Draw += (s, e) =>
            {
                var g = e.Graphics; float W = lst.LW, y = 0;
                g.Clear(Theme.Chassis);
                if (safe.Count > 0)
                {
                    Row(g, W, y, safe.Count + " safe-to-delete item" + (safe.Count == 1 ? "" : "s"), Engine.Fmt(safe.Sum(c => c.Bytes)), false, fn);
                    y += 32;
                }
                for (int i = 0; i < n; i++, y += 32)
                {
                    var c = risky[i];
                    bool more = i == 5 && risky.Count > 6;
                    Row(g, W, y, more ? "+ " + (risky.Count - 5) + " more to review" : c.Name + (c.Level == Level.Caution ? "  !" : ""),
                        Engine.Fmt(more ? risky.Skip(5).Sum(x => x.Bytes) : c.Bytes), true, fn);
                }
            };
            var how = new Label { Text = "How to clean", Font = Theme.Semi(10f), ForeColor = Theme.Muted, AutoSize = true, Location = Theme.P(28, 82 + listH), UseMnemonic = false };
            delKey.Location = Theme.P(26, 106 + listH); binKey.Location = new Point(delKey.Right + Theme.D(4), delKey.Top);
            hint = new Label { Font = Theme.Sans(9.5f), ForeColor = Theme.Soft, Location = Theme.P(28, 150 + listH), AutoSize = true, MaximumSize = Theme.D(508, 0), UseMnemonic = false };
            delKey.Click += (s, e) => { Recycle = false; ShowHow(); };
            binKey.Click += (s, e) => { Recycle = true; ShowHow(); };
            ShowHow();

            var cancel = new Key("cancel"); var ok = new Key("clean " + Engine.Fmt(total), true);
            ok.Location = new Point(ClientSize.Width - ok.Width - Theme.D(26), ClientSize.Height - Theme.D(58));
            cancel.Location = new Point(ok.Left - cancel.Width - Theme.D(8), ok.Top);
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            ok.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
            Controls.AddRange(new Control[] { h, lst, how, delKey, binKey, hint, cancel, ok });
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } };
        }

        static void Row(Graphics g, float W, float y, string name, string size, bool hatch, Font f)
        {
            if (hatch) Theme.Hatch(g, new RectangleF(0, y + 7, 5, 18), Theme.Ink, Theme.Chassis);
            using (var br = new SolidBrush(Theme.Ink)) g.DrawString(name, f, br, 14, y + 5);
            var m = g.MeasureString(size, f);
            using (var br = new SolidBrush(Theme.Ink)) g.DrawString(size, f, br, W - m.Width, y + 5);
            using (var p = new Pen(Theme.Line)) g.DrawLine(p, 14, y + 31, W, y + 31);
        }

        void ShowHow()
        {
            delKey.Toggled = !Recycle; binKey.Toggled = Recycle;
            delKey.Invalidate(); binKey.Invalidate();
            hint.Text = Recycle ? "Everything goes to the Recycle Bin, so you can restore it; the space comes back when you empty the bin. A file too big for the bin is kept, not deleted. If the Recycle Bin item is ticked, the old bin is emptied first."
                                : "Everything is deleted permanently. The space comes back straight away.";
        }
    }

    // Live window while cleaning: a display with the freed number and progress, the current file, a running log.
    class ProgressForm : Form
    {
        readonly List<Category> sel; readonly long expected;
        readonly Counter cnt = new Counter();
        readonly ConcurrentQueue<string> feed = new ConcurrentQueue<string>();
        readonly Stopwatch clock = new Stopwatch();
        CanvasPanel display; Label current; ListBox log; Key stopKey, logKey;
        System.Windows.Forms.Timer tick;
        volatile int index; volatile bool done;
        public string SnapshotPath;
        Font fLabel = Theme.CMonoSemi(7.5f), fBig = Theme.CDisplay(40f), fUnit = Theme.CDisplay(17f), fStat = Theme.CSemi(10.5f), fStat2 = Theme.CSans(10f);

        public ProgressForm(List<Category> selection, long expectedBytes)
        {
            sel = selection; expected = Math.Max(1, expectedBytes);
            Theme.Window(this, "Space Hunter  ·  cleaning", 780, 560);
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;

            display = new CanvasPanel { Location = Theme.P(18, 18), Size = Theme.D(744, 150) };
            display.Draw += PaintDisplay;
            current = new Label { Font = Theme.Sans(9f), ForeColor = Theme.Muted, AutoEllipsis = true, Location = Theme.P(20, 180), Size = Theme.D(740, 20), UseMnemonic = false };
            var logHolder = new Panel { Location = Theme.P(18, 206), Size = Theme.D(744, 272), BackColor = Theme.Pane, Padding = Theme.Pad(12, 10, 4, 10) };
            log = new ListBox { Dock = DockStyle.Fill, Font = Theme.Mono(8.5f), BorderStyle = BorderStyle.None, IntegralHeight = false, BackColor = Theme.Pane, ForeColor = Theme.Ink, SelectionMode = SelectionMode.None };
            log.HandleCreated += (s, e) => Theme.SetWindowTheme(log.Handle, "Explorer", null);
            logHolder.Controls.Add(log);
            stopKey = new Key("stop"); logKey = new Key("open log file") { Visible = false };
            stopKey.Location = new Point(ClientSize.Width - stopKey.Width - Theme.D(18), Theme.D(498));
            logKey.Location = Theme.P(18, 498);
            stopKey.Click += (s, e) => { if (done) Close(); else { cnt.Cancel = true; stopKey.Enabled = false; stopKey.Text = "stopping"; } };
            logKey.Click += (s, e) => { try { Process.Start("notepad.exe", Path.Combine(Engine.AppDir, "log.txt")); } catch { } };
            Controls.AddRange(new Control[] { display, current, logHolder, stopKey, logKey });
            FormClosing += (s, e) => { if (!done) { e.Cancel = true; cnt.Cancel = true; } };

            cnt.OnItem = (path, len, ok) =>
            {
                string head = !ok ? "skip in use" : len < 0 ? "bin " + Engine.Fmt(-len - 1).PadLeft(7) : "del " + Engine.Fmt(len).PadLeft(7);
                feed.Enqueue(head.PadRight(11) + "  " + Theme.MidPath(path, 70));
            };
            cnt.OnNote = msg => feed.Enqueue("--  " + msg);
            tick = new System.Windows.Forms.Timer { Interval = 120 };
            tick.Tick += (s, e) => Pump();
            Shown += (s, e) => Start();
        }

        void PaintDisplay(object s, PaintEventArgs e)
        {
            var g = e.Graphics; float W = display.LW, H = display.LH, pad = 20;
            g.Clear(Theme.Chassis);
            using (var p = Theme.Round(new RectangleF(0.5f, 0.5f, W - 1, H - 1), 7)) using (var b = new SolidBrush(Theme.Screen)) g.FillPath(b, p);
            int i = Math.Min(index, sel.Count - 1);
            string label = done ? (cnt.Cancel ? "STOPPED  ·  NOTHING ELSE WILL BE CLEANED" : "DONE  ·  ALL TICKED ITEMS")
                                : "CLEANING  ·  ITEM " + (i + 1) + " OF " + sel.Count + "  ·  " + sel[i].Name.ToUpperInvariant();
            using (var b = new SolidBrush(Theme.DisplayMuted)) g.DrawString(label, fLabel, b, pad, 14);
            bool binOnly = cnt.Bytes == 0 && cnt.RecycledBytes > 0;
            string num, unit; Theme.SizeParts(binOnly ? cnt.RecycledBytes : cnt.Bytes, out num, out unit);
            using (var b = new SolidBrush(Theme.Orange))
            {
                var ns = g.MeasureString(num, fBig);
                g.DrawString(num, fBig, b, pad - 5, 28);
                g.DrawString(unit + (binOnly ? " TO RECYCLE BIN" : " FREED"), fUnit, b, pad - 5 + ns.Width - 6, 28 + ns.Height - g.MeasureString(unit, fUnit).Height - 11);
            }
            string stat = cnt.Files.ToString("N0") + " files  ·  " + cnt.Skipped.ToString("N0") + " skipped  ·  " + clock.Elapsed.ToString(@"m\:ss");
            using (var b = new SolidBrush(Theme.DisplayInk)) { var ss = g.MeasureString(stat, fStat); g.DrawString(stat, fStat, b, W - pad - ss.Width, 44); }
            if (cnt.RecycledBytes > 0 && !binOnly)
                using (var b = new SolidBrush(Theme.DisplayMuted)) { var rs = "+ " + Engine.Fmt(cnt.RecycledBytes) + " moved to the Recycle Bin"; var m = g.MeasureString(rs, fStat2); g.DrawString(rs, fStat2, b, W - pad - m.Width, 68); }
            double f = Math.Min(1.0, (double)(cnt.Bytes + cnt.RecycledBytes) / expected);
            if (done && !cnt.Cancel) f = 1;
            using (var b = new SolidBrush(Theme.DisplayTrack)) g.FillRectangle(b, pad, 118, W - 2 * pad, 12);
            using (var b = new SolidBrush(Theme.Orange)) g.FillRectangle(b, pad, 118, (float)((W - 2 * pad) * f), 12);
        }

        void Start()
        {
            clock.Start(); tick.Start();
            var t = new Thread(() =>
            {
                var summary = new List<string>();
                for (int i = 0; i < sel.Count && !cnt.Cancel; i++)
                {
                    index = i;
                    if (sel[i].Blocked) continue;   // only when not elevated: these go through one permission prompt, below
                    long b0 = cnt.Bytes, r0 = cnt.RecycledBytes, f0 = cnt.Files, s0 = cnt.Skipped;
                    Engine.Process(sel[i], true, cnt);
                    summary.Add(sel[i].Name + ": freed " + Engine.Fmt(cnt.Bytes - b0) + (cnt.RecycledBytes > r0 ? " + " + Engine.Fmt(cnt.RecycledBytes - r0) + " to Recycle Bin" : "") +
                                ", " + (cnt.Files - f0).ToString("N0") + " files, skipped " + (cnt.Skipped - s0).ToString("N0"));
                }
                var sys = sel.Where(c => c.Blocked).ToList();
                if (sys.Count > 0 && !cnt.Cancel)
                {
                    index = sel.IndexOf(sys[0]);
                    cnt.Current = "Waiting for Windows to allow cleaning its system folders...";
                    summary.Add(RunSystemHelper(sys));
                }
                Engine.Log((cnt.Cancel ? "STOPPED " : "CLEANED ") + Engine.Fmt(cnt.Bytes) + ", " + cnt.Files.ToString("N0") + " files; " +
                           (cnt.RecycledBytes > 0 ? Engine.Fmt(cnt.RecycledBytes) + " to Recycle Bin; " : "") + "skipped " +
                           cnt.Skipped.ToString("N0") + " (in use, recent or too big for the bin). " + string.Join(" | ", summary));
                foreach (var line in summary) feed.Enqueue("— " + line);
                done = true;
            });
            t.IsBackground = true; t.Start();
        }

        // Fallback when the app isn't elevated: one permission prompt, an elevated copy cleans just the system items.
        string RunSystemHelper(List<Category> sys)
        {
            string res = Path.Combine(Engine.AppDir, "system-result.txt");
            try { File.Delete(res); } catch { }
            try
            {
                var psi = new ProcessStartInfo(Application.ExecutablePath, "--clean-system " + string.Join(",", sys.Select(c => c.Id)))
                          { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
                using (var p = Process.Start(psi)) p.WaitForExit();
                var parts = File.ReadAllText(res).Split('|');
                long b = long.Parse(parts[0]), f = long.Parse(parts[1]), s = long.Parse(parts[2]);
                cnt.Bytes += b; cnt.Files += f; cnt.Skipped += s;
                feed.Enqueue("del " + Engine.Fmt(b).PadLeft(7) + "  system folders: " + f.ToString("N0") + " files, " + s.ToString("N0") + " in use");
                return "System folders: freed " + Engine.Fmt(b) + ", " + f.ToString("N0") + " files, skipped " + s.ToString("N0");
            }
            catch
            {
                feed.Enqueue("--  Windows permission was declined: system folders were left as they are");
                return "System folders: skipped (permission declined)";
            }
        }

        void Pump()
        {
            current.Text = done ? "" : (cnt.Current ?? "");
            display.Invalidate();
            var batch = new List<string>(); string s;
            while (feed.TryDequeue(out s)) batch.Add(s);
            if (batch.Count > 0)
            {
                int dropped = 0;
                if (batch.Count > 400) { dropped = batch.Count - 400; batch = batch.Skip(dropped).ToList(); }
                log.BeginUpdate();
                if (dropped > 0) log.Items.Add("  ... " + dropped.ToString("N0") + " more");
                foreach (var x in batch) log.Items.Add(x);
                while (log.Items.Count > 3000) log.Items.RemoveAt(0);
                log.TopIndex = Math.Max(0, log.Items.Count - 1);
                log.EndUpdate();
            }
            if (done && tick.Enabled && feed.IsEmpty)
            {
                tick.Stop(); clock.Stop(); display.Invalidate();
                stopKey.Text = "close"; stopKey.Enabled = true; Text = "Space Hunter  ·  " + (cnt.Cancel ? "stopped" : "done");
                stopKey.Left = ClientSize.Width - stopKey.Width - Theme.D(18); logKey.Visible = true;
                if (SnapshotPath != null) { Program.Snap(this, SnapshotPath); Close(); }
            }
        }
    }
}

namespace SpaceHunter
{
    // A short styled message with one key (used when something stops the app from running safely).
    class NoticeForm : Form
    {
        public NoticeForm(string head, string body)
        {
            Theme.Window(this, "Space Hunter", 560, 260);
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; StartPosition = FormStartPosition.CenterScreen;
            var h = new Label { Text = head, Font = Theme.Bold(16f), ForeColor = Theme.Ink, AutoSize = true, MaximumSize = Theme.D(508, 0), Location = Theme.P(26, 22), UseMnemonic = false };
            var b = new Label { Text = body, Font = Theme.Sans(10.5f), ForeColor = Theme.Ink, AutoSize = true, MaximumSize = Theme.D(508, 0), Location = Theme.P(28, 70), UseMnemonic = false };
            var ok = new Key("close", true);
            ok.Location = new Point(ClientSize.Width - ok.Width - Theme.D(26), ClientSize.Height - Theme.D(58));
            ok.Click += (s, e) => Close();
            Controls.AddRange(new Control[] { h, b, ok });
        }
    }
}