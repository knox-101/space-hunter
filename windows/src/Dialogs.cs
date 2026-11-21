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
        // The finish (about 2 s): the display grows over the log, the same orange bar turns into the disk (used space
        // slides in, the freed slice drains into free space), a held "Done" beat, then the list of what went.
        const double GrowMs = 420, RingAt = 250, RingMs = 520, LiftAt = 820, LiftMs = 300, CrumbleAt = 1120, CrumbleMs = 520, CloseAt = 1250, CloseMs = 520,
                     DoneAt = 1820, PulseMs = 650, ListAt = 2150, ListMs = 320;
        struct Speck { public float A, R, Vx, Vy, S, Delay; }
        Speck[] specks;   // the freed wedge's dust, made once
        const double FinishMs = ListAt + ListMs;
        class Freed { public string Name; public long Bytes, Binned, Kept; }
        readonly List<Freed> freed = new List<Freed>();   // one per item, for the result list
        readonly string diskRoot = Path.GetPathRoot(Engine.W);
        long diskTotal, freeBefore, freeAfter = -1;
        bool details;   // "What was cleaned": the log back under a small display
        LinkLabel whatLink; Panel logHolder; int baseH, fullH;
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
        Font fRing = Theme.CDisplay(22f), fGain = Theme.CMonoSemi(10f), fList = Theme.CMono(9.5f), fListB = Theme.CMonoSemi(9.5f), fLabel = Theme.CSemi(10f), fBig = Theme.CDisplay(40f), fUnit = Theme.CDisplay(17f), fStat = Theme.CSemi(10.5f), fStat2 = Theme.CSans(10f);

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
            logHolder = new Panel { Location = Theme.P(18, 206), Size = Theme.D(744, 272), BackColor = Theme.Pane, Padding = Theme.Pad(12, 10, 4, 10) };
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
            whatLink = new LinkLabel { Text = "What was cleaned", AutoSize = true, Font = Theme.Semi(10f), LinkColor = Theme.Ink, ActiveLinkColor = Theme.OrangeDeep, VisitedLinkColor = Theme.Ink,
                                       LinkBehavior = LinkBehavior.AlwaysUnderline, BackColor = Theme.Chassis, Location = Theme.P(20, 507), Visible = false, UseMnemonic = false };
            whatLink.LinkClicked += (s, e) => ShowDetails(!details);
            baseH = display.Height; fullH = Theme.D(462);
            try { var di = new DriveInfo(diskRoot); diskTotal = di.TotalSize; freeBefore = di.AvailableFreeSpace; } catch { }
            KeyPreview = true;
            KeyDown += (s, e) => { if (done && (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Escape)) Close(); };
            Controls.AddRange(new Control[] { display, current, logHolder, stopKey, hideKey, logKey, whatLink });
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

        static double Ease(double t) { t = Math.Max(0, Math.Min(1, t)); return 1 - Math.Pow(1 - t, 3); }
        double Ms { get { return !done || cnt.Cancel ? -1 : finishClock.IsRunning || finishClock.ElapsedMilliseconds > 0 ? finishClock.ElapsedMilliseconds : FinishMs; } }

        void PaintDisplay(object s, PaintEventArgs e)
        {
            var g = e.Graphics; float W = display.LW, H = display.LH, pad = 20;
            g.Clear(Theme.Chassis);
            using (var p = Theme.Round(new RectangleF(0.5f, 0.5f, W - 1, H - 1), 7)) using (var b = new SolidBrush(Theme.Screen)) g.FillPath(b, p);
            int i = Math.Min(index, sel.Count - 1);
            double ms = Ms;                       // -1 while cleaning (or when stopped)
            bool fin = ms >= 0, beat = fin && ms >= DoneAt;
            bool binOnly = cnt.Bytes == 0 && cnt.RecycledBytes > 0;
            long total = binOnly ? cnt.RecycledBytes : cnt.Bytes;

            // the label: what's happening, then the plain result
            string label = !done ? "Cleaning  ·  item " + (i + 1) + " of " + sel.Count + "  ·  " + sel[i].Name
                         : cnt.Cancel ? "Stopped  ·  nothing else will be cleaned"
                         : !beat ? "Finishing"
                         : binOnly ? "Done  ·  moved to the Recycle Bin"
                         : "Done  ·  everything you ticked is cleaned";
            if (beat && !cnt.Cancel)
            {
                using (var b = new SolidBrush(Theme.DisplayInk)) g.DrawString("DONE", fUnit, b, pad - 3, 4);
                float dw = g.MeasureString("DONE", fUnit).Width;
                using (var b = new SolidBrush(Theme.DisplayMuted)) g.DrawString(label.Replace("Done  ·  ", "·  "), fLabel, b, pad - 3 + dw - 2, 12);
            }
            else using (var b = new SolidBrush(Color.FromArgb(0xB4, 0xB1, 0xAA))) g.DrawString(label, fLabel, b, pad, 12);
            // the big number counts to the real total as the last files go
            double count = fin ? Ease(ms / 300.0) : 1;
            long shown = (long)(total * (fin ? 0.97 + 0.03 * count : 1));
            string num, unit; Theme.SizeParts(shown, out num, out unit);
            using (var b = new SolidBrush(Theme.Orange))
            {
                var ns = g.MeasureString(num, fBig);
                g.DrawString(num, fBig, b, pad - 5, 28);
                g.DrawString(unit + (binOnly ? " TO RECYCLE BIN" : " FREED"), fUnit, b, pad - 5 + ns.Width - 6, 28 + ns.Height - g.MeasureString(unit, fUnit).Height - 11);
            }
            if (!fin)   // while cleaning: files, skipped, time
            {
                string stat = cnt.Files.ToString("N0") + " files  ·  " + cnt.Skipped.ToString("N0") + " skipped  ·  " + clock.Elapsed.ToString(@"m\:ss");
                using (var b = new SolidBrush(Theme.DisplayInk)) { var ss = g.MeasureString(stat, fStat); g.DrawString(stat, fStat, b, W - pad - ss.Width, 44); }
                if (cnt.RecycledBytes > 0 && !binOnly)
                    using (var b = new SolidBrush(Theme.DisplayMuted)) { var rs = "+ " + Engine.Fmt(cnt.RecycledBytes) + " moved to the Recycle Bin"; var m = g.MeasureString(rs, fStat2); g.DrawString(rs, fStat2, b, W - pad - m.Width, 68); }
            }

            // the bar while cleaning; at the end it gives way to your drive, drawn as a ring
            float bx = pad, bw = W - 2 * pad, by = 118, bh = 12;
            if (!fin || ms < RingAt + 200)
            {
                double f = Math.Min(1.0, (double)(cnt.Bytes + cnt.RecycledBytes) / expected);
                if (done) f = 1;
                long now = frameClock.ElapsedMilliseconds; double dt = Math.Max(0, now - lastFrame) / 1000.0; lastFrame = now;
                barShown = barShown < 0 || f < barShown ? f : barShown + (f - barShown) * (1 - Math.Exp(-dt / 0.12));
                int ba = fin ? (int)(255 * (1 - Ease(ms / (RingAt + 200)))) : 255;
                using (var b = new SolidBrush(Color.FromArgb(ba, Theme.DisplayTrack))) g.FillRectangle(b, bx, by, bw, bh);
                using (var b = new SolidBrush(Color.FromArgb(ba, Theme.Orange))) g.FillRectangle(b, bx, by, (float)(bw * (fin ? 1 : barShown)), bh);
                if (!fin) return;
            }
            // the drive, measured before and after (never a made-up number)
            double usedAfter = diskTotal > 0 && freeAfter >= 0 ? 1 - (double)freeAfter / diskTotal : 0;
            double slice = diskTotal > 0 && freeAfter >= 0 && !binOnly ? Math.Max(0, (double)(freeAfter - freeBefore) / diskTotal) : 0;
            if (slice > 0 && slice * 360 < 5) slice = freeAfter - freeBefore >= (1L << 20) ? 5 / 360.0 : 0;   // a small clean still shows a wedge; the numbers stay exact
            if (usedAfter + slice > 1) usedAfter = 1 - slice;
            // centred while it plays; it moves right when the list arrives
            float R = 104, sw = 26, cxEnd = W - pad - R - sw / 2 - 40, cxMid = W / 2;
            float cx = cxMid + (cxEnd - cxMid) * (float)Ease((ms - ListAt) / 380), cy = 214;
            var box = new RectangleF(cx - R, cy - R, 2 * R, 2 * R);
            double sweep = Ease((ms - RingAt) / RingMs);                       // the ring draws itself round
            double lift = slice > 0 ? Ease((ms - LiftAt) / LiftMs) : 0;         // the freed wedge lifts out...
            double crumble = slice > 0 ? Math.Max(0, Math.Min(1, (ms - CrumbleAt) / CrumbleMs)) : 1;   // ...turns to dust...
            double close = slice > 0 ? Ease((ms - CloseAt) / CloseMs) : 1;     // ...and free space grows into the gap it left
            double gap = slice * (1 - close);
            Func<double, float> deg = f => (float)(-90 + 360 * f);
            int ta = (int)(255 * Math.Min(1, sweep * 2));
            // free space: from the end of the gap round to the top
            float fStart = deg(usedAfter + gap), fExt = (float)(360 * (1 - usedAfter - gap));
            if (fExt > 0.3f) using (var pen = new Pen(Color.FromArgb(ta, Theme.DisplayTrack), sw)) g.DrawArc(pen, box, fStart, fExt);
            // used space, sweeping round; the freed wedge sits at its end in orange until it lifts out
            double drawn = (usedAfter + slice) * sweep;
            float used = (float)(360 * Math.Min(drawn, usedAfter));
            if (used > 0.3f) using (var pen = new Pen(Theme.DisplayUsed, sw)) g.DrawArc(pen, box, -90, used);
            if (slice > 0 && crumble < 1 && drawn > usedAfter)
            {
                float start = deg(usedAfter), ext = (float)(360 * Math.Min(slice, drawn - usedAfter));
                double mid = (start + ext / 2) * Math.PI / 180, off = 16 * lift;
                var wb = box; wb.Offset((float)(Math.Cos(mid) * off), (float)(Math.Sin(mid) * off));
                int wa = (int)(255 * (1 - Ease(crumble * 1.4)));
                if (ext > 0.3f && wa > 0) using (var pen = new Pen(Color.FromArgb(wa, Theme.Orange), sw)) g.DrawArc(pen, wb, start, ext);
            }
            if (slice > 0 && ms >= CrumbleAt)   // the wedge turns to dust and falls away
            {
                if (specks == null)
                {
                    var rnd = new Random(7); specks = new Speck[90];
                    for (int k = 0; k < specks.Length; k++)
                        specks[k] = new Speck { A = (float)rnd.NextDouble(), R = (float)(rnd.NextDouble() - 0.5), Vx = (float)(rnd.NextDouble() * 1.6 + 0.4), Vy = (float)(-rnd.NextDouble() * 1.4 - 0.2), S = (float)(rnd.NextDouble() * 3 + 2), Delay = (float)(rnd.NextDouble() * 180) };
                }
                float start = deg(usedAfter);
                foreach (var sp in specks)
                {
                    double tt = (ms - CrumbleAt - sp.Delay) / 1000.0; if (tt <= 0) continue;
                    double an = (start + 360 * slice * sp.A) * Math.PI / 180, rr = R + sp.R * sw;
                    double mx = Math.Cos(an), my = Math.Sin(an);
                    float px = (float)(cx + mx * (rr + 16) + mx * sp.Vx * 90 * tt), py = (float)(cy + my * (rr + 16) + sp.Vy * 90 * tt + 260 * tt * tt);
                    int pa = (int)(255 * Math.Max(0, 1 - tt / 0.75)); if (pa <= 0) continue;
                    using (var b = new SolidBrush(Color.FromArgb(pa, Theme.Orange))) g.FillRectangle(b, px, py, sp.S, sp.S);
                }
            }
            if (slice > 0 && close > 0)   // what you gained stays marked: an orange edge on the newly free span
            {
                var eb = new RectangleF(cx - R - sw / 2 + 2, cy - R - sw / 2 + 2, 2 * R + sw - 4, 2 * R + sw - 4);
                float e0 = deg(usedAfter + gap), ee = (float)(360 * (slice - gap));
                if (ee > 0.3f) using (var pen = new Pen(Theme.Orange, 4f)) g.DrawArc(pen, eb, e0, ee);
            }
            if (beat)   // done: the whole ring flashes orange once and settles
            {
                double pt = (ms - DoneAt) / PulseMs;
                if (pt < 1) using (var pen = new Pen(Color.FromArgb((int)(150 * (1 - pt) * (1 - pt)), Theme.Orange), sw + 2)) g.DrawEllipse(pen, box);
            }
            // in the middle: free space, counting up as the ring closes, and what you gained
            if (freeAfter >= 0 && sweep > 0)
            {
                int ca = (int)(255 * Math.Min(1, sweep * 1.5));
                long fShown = freeBefore + (long)((freeAfter - freeBefore) * close);
                string fn, fu; Theme.SizeParts(fShown, out fn, out fu);
                using (var sf = new StringFormat { Alignment = StringAlignment.Center })
                {
                    using (var b = new SolidBrush(Color.FromArgb(ca, Theme.DisplayInk))) g.DrawString(fn + " " + fu, fRing, b, cx, cy - 34, sf);
                    using (var m = new SolidBrush(Color.FromArgb(ca, Theme.DisplayMuted))) g.DrawString("free of " + Engine.Fmt(diskTotal) + " on " + diskRoot.TrimEnd('\\'), fStat2, m, cx, cy + 2, sf);
                    long gain = freeAfter - freeBefore;
                    if (gain > 0 && close > 0)
                        using (var o = new SolidBrush(Color.FromArgb((int)(255 * close), Theme.Orange))) g.DrawString("+ " + Engine.Fmt(gain), fGain, o, cx, cy + 22, sf);
                }
            }
            if (ms < ListAt || details) return;
            // the list of what went: the biggest items, the rest, the total
            int a = (int)(255 * Ease((ms - ListAt) / ListMs));
            List<Freed> items; lock (freed) items = freed.Where(x => x.Bytes + x.Binned > 0).OrderByDescending(x => x.Bytes + x.Binned).ToList();
            float y = by + 6, lh = 22, rx = W - pad - 2 * 104 - 26 - 80;
            using (var ink = new SolidBrush(Color.FromArgb(a, Theme.DisplayInk))) using (var mut = new SolidBrush(Color.FromArgb(a, Theme.DisplayMuted)))
            using (var org = new SolidBrush(Color.FromArgb(a, Theme.Orange)))
            {
                Action<string, string, Brush, Font> line = (l, r, br, f) =>
                {
                    var rm = g.MeasureString(r, f);
                    using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                        g.DrawString(l, f, br, new RectangleF(pad, y, rx - pad - rm.Width - 12, lh), sf);
                    g.DrawString(r, f, br, rx - rm.Width, y); y += lh;
                };
                int show = items.Count > 5 ? 4 : items.Count;
                foreach (var it in items.Take(show)) line(it.Name, Engine.Fmt(it.Bytes + it.Binned) + (it.Binned > 0 && it.Bytes == 0 ? "  bin" : ""), ink, fList);
                if (items.Count > show) line("+ " + (items.Count - show) + " more", Engine.Fmt(items.Skip(show).Sum(x => x.Bytes + x.Binned)), mut, fList);
                if (items.Count == 0) line("Nothing needed cleaning", "", mut, fList);
                // the total sits at the bottom of the display, like the total on a slip
                int after = 2 + ((cnt.RecycledBytes > 0 && !binOnly) || binOnly ? 1 : 0);
                y = Math.Max(y + 6, H - 18 - after * lh - 10);
                using (var pen = new Pen(Color.FromArgb(a, Theme.DisplayTrack)) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot }) g.DrawLine(pen, pad, y - 3, rx, y - 3);
                y += 4;
                line(binOnly ? "MOVED TO THE RECYCLE BIN" : "FREED", Engine.Fmt(total), org, fListB);
                if (cnt.RecycledBytes > 0 && !binOnly) line("+ moved to the Recycle Bin (empty it to get this back)", Engine.Fmt(cnt.RecycledBytes), mut, fList);
                if (binOnly) line("Empty the Recycle Bin to get this space back", "", mut, fList);
                long kept = cnt.Skipped;
                line(cnt.Files.ToString("N0") + (cnt.Files == 1 ? " file" : " files") + (kept > 0 ? "  ·  " + kept.ToString("N0") + " kept (in use or too new)" : ""), TimeSpan.FromSeconds(Math.Max(1, Math.Ceiling(clock.Elapsed.TotalSeconds))).ToString(@"m\:ss"), mut, fList);
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
                    lock (freed) freed.Add(new Freed { Name = sel[i].Name, Bytes = fb, Binned = rb, Kept = sk });
                    lock (results) results.Add(sel[i].Name.PadRight(44).Substring(0, 44) + "  " +
                        (fb > 0 || rb == 0 ? Engine.Fmt(fb) + " freed" : "") + (rb > 0 ? (fb > 0 ? " + " : "") + Engine.Fmt(rb) + " to the Recycle Bin" : "") +
                        (sk > 0 ? "   (" + sk.ToString("N0") + " kept: in use or too new)" : ""));
                }
                var sys = sel.Where(c => c.Blocked).ToList();
                if (sys.Count > 0 && !cnt.Cancel)
                {
                    index = sel.IndexOf(sys[0]);
                    cnt.Current = "Waiting for Windows to allow cleaning its system folders...";
                    long sb0 = cnt.Bytes;
                    string sr = RunSystemHelper(sys);
                    lock (freed) freed.Add(new Freed { Name = "Windows system folders", Bytes = cnt.Bytes - sb0 });
                    summary.Add(sr);
                    lock (results) results.Add(sr);
                }
                Engine.Log((cnt.Cancel ? "STOPPED " : "CLEANED ") + Engine.Fmt(cnt.Bytes) + ", " + cnt.Files.ToString("N0") + " files; " +
                           (cnt.RecycledBytes > 0 ? Engine.Fmt(cnt.RecycledBytes) + " to Recycle Bin; " : "") + "skipped " +
                           cnt.Skipped.ToString("N0") + " (in use, recent or too big for the bin). " + string.Join(" | ", summary));
                foreach (var line in summary) feed.Enqueue("— " + line);
                try { freeAfter = new DriveInfo(diskRoot).AvailableFreeSpace; } catch { }
                done = true;
            });
            t.IsBackground = true; t.Start();
        }

        void SetStop(string text, bool on)
        {
            stopKey.Text = text; stopKey.Primary = on; stopKey.Enabled = on; stopKey.FitWidth();
            stopKey.Left = ClientSize.Width - stopKey.Width - Theme.D(18); stopKey.Invalidate();
        }

        // 0 = cleaning layout, 1 = the display fills the window and the log has sunk away
        void Grow(double t)
        {
            display.Height = baseH + (int)((fullH - baseH) * t);
            logHolder.Top = Theme.D(206) + (int)(Theme.D(330) * t); logHolder.Visible = t < 1;
            current.Visible = t <= 0;
        }

        void ShowDetails(bool on)
        {
            details = on;
            if (on) { display.Height = baseH; logHolder.Top = Theme.D(206); logHolder.Visible = true; current.Visible = false; logKey.Visible = true; }
            else { Grow(1); logKey.Visible = false; }
            whatLink.Text = on ? (cnt.Cancel ? "What was cleaned" : "Back to the summary") : "What was cleaned";
            if (cnt.Cancel) whatLink.Visible = false;
            logKey.Left = whatLink.Right + Theme.D(16);
            display.Invalidate();
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
                hideKey.Visible = false; logKey.Visible = false;
                whatLink.Visible = !SystemInformation.UIEffectsEnabled; logKey.Left = whatLink.Right + Theme.D(16); logKey.Top = stopKey.Top;
                if (cnt.Cancel) { whatLink.Visible = false; ShowDetails(true); }   // stopped: the log stays, so you can see where it stopped
                else if (!SystemInformation.UIEffectsEnabled) { finishClock.Reset(); Grow(1); }   // reduce effects: straight to the result
                else
                {
                    SetStop("finishing", false); Text = "Space Hunter  ·  cleaning";
                    finishClock.Restart();
                    finishTick = new System.Windows.Forms.Timer { Interval = 15 };
                    finishTick.Tick += (s2, e2) =>
                    {
                        double ms = finishClock.ElapsedMilliseconds;
                        if (!details) Grow(Ease(ms / GrowMs));
                        if (ms >= ListAt && !whatLink.Visible) whatLink.Visible = true;
                        if (ms >= DoneAt && !stopKey.Enabled) { SetStop("done", true); Text = "Space Hunter  ·  done"; }   // the way to the full log, once the summary is up
                        display.Invalidate();
                        if (ms >= FinishMs) { finishTick.Stop(); finishClock.Stop(); display.Invalidate(); }
                    };
                    finishTick.Start();
                }
                display.Invalidate();
                if (SnapshotPath != null)   // test hook: the display at set moments of the finish, then the whole window at the end
                {
                    var snap = new System.Windows.Forms.Timer { Interval = 15 };
                    var at = new[] { 500, 900, 1250, 1500, 1950 }; int next = 0;
                    snap.Tick += (s3, e3) =>
                    {
                        long ms = finishClock.ElapsedMilliseconds;
                        if (next < at.Length && ms >= at[next])
                        {
                            display.Refresh();
                            using (var bmp = new Bitmap(display.Width, display.Height)) { display.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height)); bmp.Save(SnapshotPath.Replace(".png", "-t" + at[next] + ".png")); }
                            next++;
                        }
                        if (ms >= FinishMs + 300 || (!finishClock.IsRunning && next >= at.Length)) { snap.Stop(); Program.Snap(this, SnapshotPath); Close(); }
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