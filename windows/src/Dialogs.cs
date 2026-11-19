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
        public bool Recycle;   // false = delete permanently
        readonly Key delKey = new Key("permanently delete"), binKey = new Key("recycle bin");
        readonly Label hint;
        Key ok, cancel; long total, safeBytes, otherBytes; bool onlySafe; Label how;
        const int RowH = 32, HeadH = 26, MaxPerGroup = 4;

        // One line of the list: a section heading, or an item (or a "+ N more" line) with its size.
        class Line { public string Text, Size; public Level Level; public bool Head; }

        public ConfirmForm(List<Category> sel)
        {
            total = sel.Sum(c => c.Bytes);
            safeBytes = sel.Where(c => c.Safe).Sum(c => c.Bytes); otherBytes = total - safeBytes;
            onlySafe = sel.All(c => c.Safe);
            var lines = new List<Line>();
            // grouped by risk, so it's plain which ticked items need care
            foreach (var lvl in new[] { Level.Safe, Level.Review, Level.Caution })
            {
                var items = sel.Where(c => c.Level == lvl).OrderByDescending(c => c.Bytes).ToList();
                if (items.Count == 0) continue;
                lines.Add(new Line { Head = true, Level = lvl, Text = Theme.TierName(lvl) });
                // every section names its items, so you see the real list before anything goes
                var shown = items.Where(c => !c.Blocked).Take(MaxPerGroup).ToList();
                foreach (var c in shown) lines.Add(new Line { Level = lvl, Text = c.Name, Size = Engine.Fmt(c.Bytes) });
                var rest = items.Where(c => !c.Blocked && !shown.Contains(c)).ToList();
                if (rest.Count > 0) lines.Add(new Line { Level = lvl, Text = "+ " + rest.Count + " more", Size = Engine.Fmt(rest.Sum(c => c.Bytes)) });
                var blocked = items.Where(c => c.Blocked).ToList();   // only when the app runs without admin rights
                if (blocked.Count > 0) lines.Add(new Line { Level = lvl, Text = "+ " + blocked.Count + " Windows folder" + (blocked.Count == 1 ? "" : "s") + " (" + string.Join(", ", blocked.Select(c => c.Name)) + ")", Size = "size after cleaning" });
            }
            int listH = lines.Sum(l => l.Head ? HeadH : RowH);
            Theme.Window(this, "Space Hunter", 560, 282 + listH);
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            // permanently delete is chosen to start with; the Recycle Bin is one click away, for any clean
            Recycle = false;

            var h = new Label { Text = "Clean " + Engine.Fmt(total) + "?", Font = Theme.Bold(17f), ForeColor = Theme.Ink, AutoSize = true, Location = Theme.P(26, 22), UseMnemonic = false };
            var lst = new CanvasPanel { Location = Theme.P(26, 70), Size = Theme.D(508, listH) };
            Font fn = Theme.CSemi(10.5f), fh = Theme.CBold(9.5f);
            lst.Draw += (s, e) =>
            {
                var g = e.Graphics; float W = lst.LW, y = 0;
                g.Clear(Theme.Chassis);
                foreach (var l in lines)
                {
                    if (l.Head)
                    {
                        using (var br = new SolidBrush(Theme.Muted)) g.DrawString(l.Text.ToUpperInvariant(), fh, br, 0, y + 7);
                        y += HeadH; continue;
                    }
                    Row(g, W, y, l.Text, l.Size, l.Level, fn);
                    y += RowH;
                }
            };
            how = new Label { Text = "How to clean", Font = Theme.Semi(10f), ForeColor = Theme.Muted, AutoSize = true, Location = Theme.P(28, 82 + listH), UseMnemonic = false };
            delKey.Location = Theme.P(26, 106 + listH); binKey.Location = new Point(delKey.Right + Theme.D(4), delKey.Top);
            hint = new Label { Font = Theme.Sans(9.5f), ForeColor = Theme.Muted, Location = Theme.P(28, 150 + listH), AutoSize = true, MaximumSize = Theme.D(508, 0), UseMnemonic = false };
            delKey.Click += (s, e) => { Recycle = false; ShowHow(); };
            binKey.Click += (s, e) => { Recycle = true; ShowHow(); };
            cancel = new Key("cancel"); ok = new Key("clean", true);
            ShowHow();
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            ok.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
            Controls.AddRange(new Control[] { h, lst, how, delKey, binKey, hint, cancel, ok });
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } };
        }

        static void Row(Graphics g, float W, float y, string name, string size, Level level, Font f)
        {
            Theme.LevelMark(g, new RectangleF(0, y + 7, 5, 18), level, Theme.Ink, Theme.Chassis);
            var m = g.MeasureString(size, f);
            using (var br = new SolidBrush(Theme.Ink))
            using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(name, f, br, new RectangleF(14, y + 5, W - m.Width - 26, 22), sf);
            using (var br = new SolidBrush(Theme.Ink)) g.DrawString(size, f, br, W - m.Width, y + 5);
            using (var p = new Pen(Theme.Line)) g.DrawLine(p, 14, y + 31, W, y + 31);
        }

        void ShowHow()
        {
            delKey.Toggled = !Recycle; binKey.Toggled = Recycle;
            delKey.Invalidate(); binKey.Invalidate();
            // the key says what will actually happen
            ok.Text = !Recycle ? "permanently delete " + Engine.Fmt(total) : "move " + Engine.Fmt(total) + " to recycle bin";
            ok.FitWidth();
            ok.Location = new Point(ClientSize.Width - ok.Width - Theme.D(26), ClientSize.Height - Theme.D(58));
            cancel.Location = new Point(ok.Left - cancel.Width - Theme.D(8), ok.Top);
            if (!Recycle)
                hint.Text = (onlySafe ? "These are caches and leftovers that rebuild. " : "") +
                            "Everything is deleted permanently and can't be restored. The space comes back straight away.";
            else
                hint.Text = "Everything goes to the Recycle Bin, so you can restore it. That space comes back only when you empty the bin. A file too big for the bin is kept, not deleted.";
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
        readonly List<string> results = new List<string>();   // one plain line per item, shown when the clean is done
        Key hideKey;
        // the finish: for about a second the bar clears segment by segment and the number counts up to the total
        const double FinishMs = 1200;
        readonly Stopwatch finishClock = new Stopwatch();
        System.Windows.Forms.Timer finishTick;
        readonly System.Windows.Forms.Timer frame = new System.Windows.Forms.Timer { Interval = 15 };
        readonly System.Diagnostics.Stopwatch frameClock = new System.Diagnostics.Stopwatch();
        double barShown = -1; long lastFrame;
        double FinishT { get { return !finishClock.IsRunning && finishClock.ElapsedMilliseconds == 0 ? 1 : Math.Min(1, finishClock.ElapsedMilliseconds / FinishMs); } }
        bool Finishing { get { return done && !cnt.Cancel && finishClock.IsRunning; } }
        public bool Done { get { return done; } }
        public long FreedSoFar { get { return cnt.Bytes; } }
        public long BinnedSoFar { get { return cnt.RecycledBytes; } }
        public int ItemNow { get { return Math.Min(index, sel.Count - 1) + 1; } }
        public int ItemCount { get { return sel.Count; } }
        public string SnapshotPath;
        Font fLabel = Theme.CSemi(10f), fBig = Theme.CDisplay(40f), fUnit = Theme.CDisplay(17f), fStat = Theme.CSemi(10.5f), fStat2 = Theme.CSans(10f);

        public ProgressForm(List<Category> selection, long expectedBytes)
        {
            sel = selection; expected = Math.Max(1, expectedBytes);
            Theme.Window(this, "Space Hunter  ·  cleaning", 780, 560);
            // it can be minimised to its own taskbar button while it works; the main window shows how far it's got
            FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false; MinimizeBox = true; StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = true;

            display = new CanvasPanel { Location = Theme.P(18, 18), Size = Theme.D(744, 150) };
            display.Draw += PaintDisplay;
            current = new Label { Font = Theme.Sans(9f), ForeColor = Theme.Muted, AutoEllipsis = true, Location = Theme.P(20, 180), Size = Theme.D(740, 20), UseMnemonic = false };
            var logHolder = new Panel { Location = Theme.P(18, 206), Size = Theme.D(744, 272), BackColor = Theme.Pane, Padding = Theme.Pad(12, 10, 4, 10) };
            log = new ListBox { Dock = DockStyle.Fill, Font = Theme.Mono(8.5f), BorderStyle = BorderStyle.None, IntegralHeight = false, BackColor = Theme.Pane, ForeColor = Theme.Ink, SelectionMode = SelectionMode.None };
            log.HandleCreated += (s, e) => Theme.SetWindowTheme(log.Handle, "Explorer", null);
            logHolder.Controls.Add(log);
            stopKey = new Key("stop"); logKey = new Key("open log file") { Visible = false }; hideKey = new Key("minimise");
            stopKey.Location = new Point(ClientSize.Width - stopKey.Width - Theme.D(18), Theme.D(498));
            hideKey.Location = new Point(stopKey.Left - hideKey.Width - Theme.D(8), stopKey.Top);
            hideKey.Click += (s, e) => WindowState = FormWindowState.Minimized;
            logKey.Location = Theme.P(18, 498);
            stopKey.Click += (s, e) => { if (done) Close(); else { cnt.Cancel = true; stopKey.Enabled = false; stopKey.Text = "stopping"; } };
            logKey.Click += (s, e) => { try { Process.Start("notepad.exe", Path.Combine(Engine.AppDir, "log.txt")); } catch { } };
            Controls.AddRange(new Control[] { display, current, logHolder, stopKey, hideKey, logKey });
            FormClosing += (s, e) => { if (!done) { e.Cancel = true; cnt.Cancel = true; } };

            cnt.OnItem = (path, len, ok) =>
            {
                string head = !ok ? "skip in use" : len < 0 ? "bin " + Engine.Fmt(-len - 1).PadLeft(7) : "del " + Engine.Fmt(len).PadLeft(7);
                feed.Enqueue(head.PadRight(11) + "  " + Theme.MidPath(path, 70));
            };
            cnt.OnNote = msg => feed.Enqueue("--  " + msg);
            tick = new System.Windows.Forms.Timer { Interval = 120 };
            tick.Tick += (s, e) => Pump();
            frame.Tick += (s, e) => { if (!done) display.Invalidate(); else frame.Stop(); };
            Shown += (s, e) => { frameClock.Start(); frame.Start(); Start(); };
            FormClosed += (s, e) => frame.Stop();
        }

        void PaintDisplay(object s, PaintEventArgs e)
        {
            var g = e.Graphics; float W = display.LW, H = display.LH, pad = 20;
            g.Clear(Theme.Chassis);
            using (var p = Theme.Round(new RectangleF(0.5f, 0.5f, W - 1, H - 1), 7)) using (var b = new SolidBrush(Theme.Screen)) g.FillPath(b, p);
            int i = Math.Min(index, sel.Count - 1);
            string label = done ? (cnt.Cancel ? "Stopped  ·  nothing else will be cleaned" : Finishing ? "Clearing up\u2026" : "Done  ·  all ticked items cleaned")
                                : "Cleaning  ·  item " + (i + 1) + " of " + sel.Count + "  ·  " + sel[i].Name;
            // ease-out: quick at first, settling on the real total
            double t = done && !cnt.Cancel ? FinishT : 1, ease = 1 - Math.Pow(1 - t, 3);
            using (var b = new SolidBrush(Color.FromArgb(0xB4, 0xB1, 0xAA))) g.DrawString(label, fLabel, b, pad, 12);
            bool binOnly = cnt.Bytes == 0 && cnt.RecycledBytes > 0;
            long shown = (long)((binOnly ? cnt.RecycledBytes : cnt.Bytes) * (done && !cnt.Cancel ? ease : 1));
            string num, unit; Theme.SizeParts(shown, out num, out unit);
            using (var b = new SolidBrush(Theme.Orange))
            {
                var ns = g.MeasureString(num, fBig);
                g.DrawString(num, fBig, b, pad - 5, 28);
                g.DrawString(unit + (binOnly ? " TO RECYCLE BIN" : " FREED"), fUnit, b, pad - 5 + ns.Width - 6, 28 + ns.Height - g.MeasureString(unit, fUnit).Height - 11);
            }
            string stat = ((long)(cnt.Files * (done && !cnt.Cancel ? ease : 1))).ToString("N0") + " files  ·  " + cnt.Skipped.ToString("N0") + " skipped  ·  " + clock.Elapsed.ToString(@"m\:ss");
            using (var b = new SolidBrush(Theme.DisplayInk)) { var ss = g.MeasureString(stat, fStat); g.DrawString(stat, fStat, b, W - pad - ss.Width, 44); }
            if (binOnly && done)   // honest about where the space is: it comes back when the bin is emptied
                using (var b = new SolidBrush(Theme.DisplayMuted)) { var rs = "Empty the Recycle Bin to get this space back"; var m = g.MeasureString(rs, fStat2); g.DrawString(rs, fStat2, b, W - pad - m.Width, 68); }
            if (cnt.RecycledBytes > 0 && !binOnly)
                using (var b = new SolidBrush(Theme.DisplayMuted)) { var rs = "+ " + Engine.Fmt(cnt.RecycledBytes) + " moved to the Recycle Bin"; var m = g.MeasureString(rs, fStat2); g.DrawString(rs, fStat2, b, W - pad - m.Width, 68); }
            double f = Math.Min(1.0, (double)(cnt.Bytes + cnt.RecycledBytes) / expected);
            float bx = pad, bw = W - 2 * pad, by = 118, bh = 12;
            using (var b = new SolidBrush(Theme.DisplayTrack)) g.FillRectangle(b, bx, by, bw, bh);
            if (done && !cnt.Cancel)
            {
                // the full bar breaks into segments that drop away left to right: the space is cleared
                const int N = 28; float sw = bw / N;
                for (int k = 0; k < N; k++)
                {
                    double start = 0.55 * k / N, local = Math.Max(0, Math.Min(1, (t - start) / 0.4));
                    if (local >= 1) continue;
                    double fall = local * local;   // ease-in: each piece lets go, then falls
                    int a = (int)(255 * (1 - fall));
                    using (var b = new SolidBrush(Color.FromArgb(a, Theme.Orange)))
                        g.FillRectangle(b, bx + k * sw + 1, by + (float)(fall * 16), sw - 2, bh * (float)(1 - fall * 0.6));
                }
            }
            else
            {
                // glide toward the real figure instead of stepping each time it changes
                long now = frameClock.ElapsedMilliseconds; double dt = Math.Max(0, now - lastFrame) / 1000.0; lastFrame = now;
                barShown = barShown < 0 || f < barShown ? f : barShown + (f - barShown) * (1 - Math.Exp(-dt / 0.12));
                using (var b = new SolidBrush(Theme.Orange)) g.FillRectangle(b, bx, by, (float)(bw * barShown), bh);
            }
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
                    try { Engine.Process(sel[i], true, cnt); }
                    catch (Exception ex) { if (cnt.OnNote != null) cnt.OnNote(sel[i].Name + ": stopped early (" + ex.Message + ")"); }   // the rest still runs
                    summary.Add(sel[i].Name + ": freed " + Engine.Fmt(cnt.Bytes - b0) + (cnt.RecycledBytes > r0 ? " + " + Engine.Fmt(cnt.RecycledBytes - r0) + " to Recycle Bin" : "") +
                                ", " + (cnt.Files - f0).ToString("N0") + " files, skipped " + (cnt.Skipped - s0).ToString("N0"));
                    long fb = cnt.Bytes - b0, rb = cnt.RecycledBytes - r0, sk = cnt.Skipped - s0;
                    lock (results) results.Add(sel[i].Name.PadRight(44).Substring(0, 44) + "  " +
                        (fb > 0 || rb == 0 ? Engine.Fmt(fb) + " freed" : "") + (rb > 0 ? (fb > 0 ? " + " : "") + Engine.Fmt(rb) + " to the Recycle Bin" : "") +
                        (sk > 0 ? "   (" + sk.ToString("N0") + " kept: in use or too new)" : ""));
                }
                var sys = sel.Where(c => c.Blocked).ToList();
                if (sys.Count > 0 && !cnt.Cancel)
                {
                    index = sel.IndexOf(sys[0]);
                    cnt.Current = "Waiting for Windows to allow cleaning its system folders...";
                    string sr = RunSystemHelper(sys);
                    summary.Add(sr);
                    lock (results) results.Add(sr);
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
                // a plain summary on top; every file and every note stays below it
                lock (results)
                    if (results.Count > 0)
                    {
                        var top = new List<string> { "What was cleaned", "" };
                        top.AddRange(results); top.Add(""); top.Add("Every file:");
                        log.BeginUpdate();
                        for (int k = top.Count - 1; k >= 0; k--) log.Items.Insert(0, top[k]);
                        log.TopIndex = 0; log.EndUpdate();
                    }
                tick.Stop(); clock.Stop();
                // Done closes the window; it's the one orange key now
                stopKey.Text = cnt.Cancel ? "close" : "done"; stopKey.Primary = !cnt.Cancel; stopKey.Enabled = true; stopKey.Invalidate();
                hideKey.Visible = false; Text = "Space Hunter  ·  " + (cnt.Cancel ? "stopped" : "done");
                stopKey.Left = ClientSize.Width - stopKey.Width - Theme.D(18); logKey.Visible = true;
                if (!cnt.Cancel && SystemInformation.UIEffectsEnabled)   // a moment of "clearing up"; skipped when Windows is set to reduce effects
                {
                    finishClock.Restart();
                    finishTick = new System.Windows.Forms.Timer { Interval = 16 };
                    finishTick.Tick += (s2, e2) => { display.Invalidate(); if (finishClock.ElapsedMilliseconds >= FinishMs) { finishTick.Stop(); finishClock.Stop(); display.Invalidate(); } };
                    finishTick.Start();
                }
                display.Invalidate();
                if (SnapshotPath != null)   // test hook: one frame mid-way, then the end
                {
                    var snap = new System.Windows.Forms.Timer { Interval = 450 };
                    int step = 0;
                    snap.Tick += (s3, e3) =>
                    {
                        step++;
                        if (step == 1) { using (var bmp = new Bitmap(display.Width, display.Height)) { display.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height)); bmp.Save(SnapshotPath.Replace(".png", "-mid.png")); } }
                        if (step >= 4) { snap.Stop(); Program.Snap(this, SnapshotPath); Close(); }
                    };
                    snap.Start();
                }
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