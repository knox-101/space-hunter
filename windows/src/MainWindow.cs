// Copyright (c) 2026 Blesson Reji (knox-101). Space Hunter is free software under the GNU GPL v3 or later; see LICENSE.
// SPDX-License-Identifier: GPL-3.0-or-later
// Space Hunter (Windows) - the main window: the display, the three section tiles, the list,
// the item panel that slides in from the right, and the keys.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace SpaceHunter
{
    // The dark display: the number you get back, the disk as one bar with a clear legend, and the history.
    class DisplayPanel : Canvas
    {
        public long Selected, ReviewUnticked, Free, Total, FreedAll;
        public int Cleans;
        public bool Scanning, Compact, OnlySafe = true;
        public List<KeyValuePair<DateTime, long>> History = new List<KeyValuePair<DateTime, long>>();
        Font fLabel = Theme.CSemi(11f), fHist = Theme.CSemi(10f), fBig = Theme.CDisplay(50f), fUnit = Theme.CDisplay(20f), fDisk = Theme.CSans(11f),
             fLegend = Theme.CSemi(10.5f), fSmall = Theme.CSans(9.5f);

        public float DesignHeight { get { return Compact ? 172 : 206; } }

        // while the items are measured: one turning ring beside the number, moved by the clock (~60 fps)
        readonly System.Windows.Forms.Timer spinTick = new System.Windows.Forms.Timer { Interval = 16 };
        readonly System.Diagnostics.Stopwatch spin = System.Diagnostics.Stopwatch.StartNew();
        public DisplayPanel() { spinTick.Tick += (s, e) => { if (Scanning) Invalidate(); else spinTick.Stop(); }; }
        public void SetScanning(bool on) { if (on == Scanning) return; Scanning = on; if (on) { spin.Restart(); spinTick.Start(); } Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Begin(e); float W = LW, H = LH;
            g.Clear(Theme.Chassis);
            using (var p = Theme.Round(new RectangleF(0.5f, 0.5f, W - 1, H - 1), 7)) using (var b = new SolidBrush(Theme.Screen)) g.FillPath(b, p);
            using (var pen = new Pen(Color.Black, 2)) g.DrawLine(pen, 7, 2, W - 7, 2);   // inset lip
            float pad = 22, right = W - pad;
            // the right column is right-aligned into boxes that end on `right`, so every line ends on the same edge
            // (typographic: no GDI+ side padding, which differs by font size and made the ends ragged)
            var far = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Far }; far.FormatFlags |= StringFormatFlags.NoWrap;
            using (var b = new SolidBrush(Color.FromArgb(0xB4, 0xB1, 0xAA)))
            {
                g.DrawString("Space to free up", fLabel, b, pad, 13, StringFormat.GenericTypographic);   // starts on the bar's edge
                var dl = "Disk " + Path.GetPathRoot(Engine.W).TrimEnd('\\');
                g.DrawString(dl, fLabel, b, new RectangleF(pad, 13, right - pad, 24), far);
            }
            string num, unit; Theme.SizeParts(Selected, out num, out unit);
            using (var b = new SolidBrush(Theme.Orange))
            {
                var ns = g.MeasureString(num, fBig);
                g.DrawString(num, fBig, b, pad - 12.5f, 30);   // the digits start on the label's and the bar's edge
                g.DrawString(unit, fUnit, b, pad - 12.5f + ns.Width - 8, 30 + ns.Height - g.MeasureString(unit, fUnit).Height - 13);
                // beside the number: what kind of space it is, and what else is there if you look
                if (Scanning)
                {
                    float tx = pad - 12.5f + ns.Width - 8 + g.MeasureString(unit, fUnit).Width + 14, rr = 11, cx = tx + rr + 2, cy = 62;
                    var old = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var pen = new Pen(Theme.DisplayTrack, 3.5f)) g.DrawEllipse(pen, cx - rr, cy - rr, 2 * rr, 2 * rr);
                    using (var pen = new Pen(Theme.Orange, 3.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawArc(pen, cx - rr, cy - rr, 2 * rr, 2 * rr, (float)(spin.Elapsed.TotalSeconds * 300 % 360), 110);
                    g.SmoothingMode = old;
                    using (var ib = new SolidBrush(Theme.DisplayInk)) g.DrawString("measuring every item", fLegend, ib, cx + rr + 10, cy - g.MeasureString("Ag", fLegend).Height / 2);
                }
                if (!Scanning && Selected > 0)
                {
                    float tx = pad - 12.5f + ns.Width - 8 + g.MeasureString(unit, fUnit).Width + 14;
                    using (var ib = new SolidBrush(Theme.DisplayInk)) g.DrawString(OnlySafe ? (FreedAll == 0 ? "all safe to clean  ·  press Clean to start" : "all safe to clean") : "includes items to check first", fLegend, ib, tx, 44);
                    if (ReviewUnticked > 0)
                        using (var mb = new SolidBrush(Theme.DisplayMuted)) g.DrawString("+ " + Engine.Fmt(ReviewUnticked) + " more you can check yourself", fLegend, mb, tx, 64);
                }
            }
            string disk = Total > 0 ? Engine.Fmt(Free) + " free of " + Engine.Fmt(Total) : "";
            using (var b = new SolidBrush(Color.FromArgb(0xB4, 0xB1, 0xAA))) g.DrawString(disk, fDisk, b, new RectangleF(pad, 35, right - pad, 24), far);

            // history: free space over time + everything freed so far
            if (History.Count >= 2)
            {
                var pts = History.Skip(Math.Max(0, History.Count - 60)).ToList();
                // short windows (125% and 150% scaling) get a shorter line, with its label beside it
                float sw = Compact ? 120 : 170, sh = Compact ? 16 : 26, sx = right - 3 - sw, sy = Compact ? 80 : 66;   // the dot (radius 3) ends on `right`
                long mn = pts.Min(p => p.Value), mx = pts.Max(p => p.Value); if (mx == mn) mx = mn + 1;
                var line = new List<PointF>();
                for (int i = 0; i < pts.Count; i++)
                    line.Add(new PointF(sx + sw * i / (pts.Count - 1), sy + sh - (float)(sh * (pts[i].Value - mn) / (double)(mx - mn))));
                using (var pen = new Pen(Theme.DisplayInk, 1.5f)) g.DrawLines(pen, line.ToArray());
                using (var b = new SolidBrush(Theme.Orange)) { var lp = line[line.Count - 1]; g.FillEllipse(b, lp.X - 3, lp.Y - 3, 6, 6); }
                using (var b = new SolidBrush(Theme.DisplayMuted))
                {
                    var hl = "Free space since " + pts[0].Key.ToString("d MMM"); var hs = g.MeasureString(hl, fHist);
                    if (Compact) g.DrawString(hl, fHist, b, sx - hs.Width - 10, sy + sh / 2 - hs.Height / 2);
                    else g.DrawString(hl, fHist, b, new RectangleF(pad, sy + sh + 3, right - pad, 22), far);
                }
            }
            if (FreedAll > 0)
                using (var b = new SolidBrush(Theme.DisplayMuted))
                {
                    var fl = "Freed so far: " + Engine.Fmt(FreedAll) + " in " + Cleans + (Cleans == 1 ? " clean" : " cleans");
                    g.DrawString(fl, fSmall, b, new RectangleF(pad, Compact ? 58 : 116, right - pad, 22), far);
                }

            // the disk: used | ticked (orange) | review first, not ticked (hatched) | free
            float bx = pad, bw = W - 2 * pad, by = Compact ? 116 : 148, bh = 16;
            using (var b = new SolidBrush(Theme.DisplayTrack)) g.FillRectangle(b, bx, by, bw, bh);
            if (Total > 0)
            {
                double used = (double)(Total - Free) / Total;
                double sel = Math.Min(used, (double)Selected / Total);
                double rev = Math.Min(used - sel, (double)ReviewUnticked / Total);
                float x0 = bx, w0 = (float)(bw * (used - sel - rev)), w1 = (float)(bw * sel), w2 = (float)(bw * rev);
                if (Selected > 0 && w1 < 4) { w1 = 4; w0 = Math.Max(0, w0 - 4); }
                if (ReviewUnticked > 0 && w2 < 4) { w2 = 4; w0 = Math.Max(0, w0 - 4); }
                using (var b = new SolidBrush(Theme.DisplayUsed)) g.FillRectangle(b, x0, by, w0, bh);
                using (var b = new SolidBrush(Theme.Orange)) g.FillRectangle(b, x0 + w0, by, w1, bh);
                Theme.Hatch(g, new RectangleF(x0 + w0 + w1, by, w2, bh), Theme.Orange, Theme.DisplayTrack);
                using (var pen = new Pen(Theme.Screen, 2)) { g.DrawLine(pen, x0 + w0, by, x0 + w0, by + bh); g.DrawLine(pen, x0 + w0 + w1, by, x0 + w0 + w1, by + bh); }
                if (Selected > 0)   // a caret marks the part you get back, even when it's thin
                    using (var b = new SolidBrush(Theme.Orange))
                    {
                        float cx = x0 + w0 + w1 / 2;
                        g.FillPolygon(b, new[] { new PointF(cx - 6, by - 11), new PointF(cx + 6, by - 11), new PointF(cx, by - 3) });
                    }
            }
            float lx = bx, ly = by + bh + 12;
            lx = Legend(g, lx, ly, Theme.Orange, false, "Ticked: " + Engine.Fmt(Selected), Theme.DisplayInk);
            lx = Legend(g, lx, ly, Theme.Orange, true, "Not ticked: " + Engine.Fmt(ReviewUnticked), Theme.DisplayInk);
            lx = Legend(g, lx, ly, Theme.DisplayUsed, false, "Used", Theme.DisplayMuted);
            Legend(g, lx, ly, Theme.DisplayTrack, false, "Free", Theme.DisplayMuted);
        }

        float Legend(Graphics g, float x, float y, Color c, bool hatch, string text, Color ink)
        {
            var box = new RectangleF(x, y + 1.8f, 13, 13);   // centred on the words' x-height
            if (hatch) Theme.Hatch(g, box, c, Theme.Screen);
            else using (var b = new SolidBrush(c)) g.FillRectangle(b, box);
            // the outline sits inside the box, so the "Free" swatch is the same size as the others
            if (c == Theme.DisplayTrack) using (var p = new Pen(Theme.DisplayMuted)) g.DrawRectangle(p, box.X + 0.5f, box.Y + 0.5f, box.Width - 1, box.Height - 1);
            using (var b = new SolidBrush(ink)) g.DrawString(text, fLegend, b, x + 19, y);
            return x + 19 + g.MeasureString(text, fLegend).Width + 24;
        }
    }

    // Three tiles, one per section, so every section is visible at once. Click one to list its items.
    class TierBar : Canvas
    {
        public Level Current = Level.Safe;
        public List<Category> Cats = new List<Category>();
        public event EventHandler Changed;
        public event Action<Level, bool> TickAll;   // the small box beside "11 of 11 ticked"
        readonly RectangleF[] boxes = new RectangleF[3];
        int hover = -1, boxHover = -1;
        Font fName = Theme.CBold(11.5f), fName2 = Theme.CSans(9.75f), fDesc = Theme.CSans(9f), fSize = Theme.CSemi(13f), fSub = Theme.CSans(8.75f), fSubB = Theme.CSemi(9.75f);
        static readonly Level[] Order = { Level.Safe, Level.Review, Level.Caution };
        const float Pad = 12, Gap = 6;   // text inset inside a tab; space between tabs
        static System.Drawing.Drawing2D.GraphicsPath TopRound(RectangleF r, float rad)
        {
            var p = new System.Drawing.Drawing2D.GraphicsPath(); float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddLine(r.Right, r.Bottom, r.X, r.Bottom); p.CloseFigure(); return p;
        }

        readonly System.Windows.Forms.Timer slide = new System.Windows.Forms.Timer { Interval = 15 };
        readonly System.Diagnostics.Stopwatch slideClock = new System.Diagnostics.Stopwatch();
        float markFrom = -1, markTo;   // the marker's x, in tile units (0, 1, 2)
        Font fBig = Theme.CDisplay(24f);

        public TierBar()
        {
            Cursor = Cursors.Hand; TabStop = false;
            slide.Tick += (s, e) => { Invalidate(); if (slideClock.ElapsedMilliseconds > 320) { slide.Stop(); markFrom = markTo; } };
        }

        RectangleF Tile(int i) { float w = (LW - 2 * 18 - 2 * Gap) / 3f; return new RectangleF(18 + i * (w + Gap), 4, w, LH - 4); }
        float MarkAt()
        {
            if (markFrom < 0) markFrom = markTo = Array.IndexOf(Order, Current);
            double t = Math.Min(1, slideClock.ElapsedMilliseconds / 320.0); t = 1 - Math.Pow(1 - t, 3);
            return slide.Enabled ? (float)(markFrom + (markTo - markFrom) * t) : markTo;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var pt = new PointF(X(e), Y(e)); int h = -1;
            for (int i = 0; i < 3; i++) if (Tile(i).Contains(pt)) h = i;
            int bh = -1; for (int i = 0; i < 3; i++) if (!boxes[i].IsEmpty && RectangleF.Inflate(boxes[i], 5, 5).Contains(pt)) bh = i;
            if (h != hover || bh != boxHover) { hover = h; boxHover = bh; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; boxHover = -1; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            var pt = new PointF(X(e), Y(e));
            for (int i = 0; i < 3; i++)
                if (!boxes[i].IsEmpty && RectangleF.Inflate(boxes[i], 5, 5).Contains(pt))
                {
                    var its = Cats.Where(c => c.Level == Order[i] && c.Refused == null && (c.Blocked || c.Bytes > 0 || c.Files > 0)).ToList();
                    if (its.Count > 0 && TickAll != null) TickAll(Order[i], !its.All(c => c.Checked));
                    return;
                }
            for (int i = 0; i < 3; i++)
                if (Tile(i).Contains(pt) && Current != Order[i])
                {
                    markFrom = MarkAt(); markTo = i; slideClock.Restart(); slide.Start();   // the marker slides over
                    Current = Order[i]; Invalidate(); if (Changed != null) Changed(this, EventArgs.Empty);
                }
        }

        // "14 of 17 MB" when both share a unit, else "900 MB of 2.1 GB"
        static string Of(long a, long b)
        {
            string x = Engine.Fmt(a), y = Engine.Fmt(b);
            string ux = x.Substring(x.LastIndexOf(' ') + 1), uy = y.Substring(y.LastIndexOf(' ') + 1);
            return (ux == uy ? x.Substring(0, x.LastIndexOf(' ')) : x) + " of " + y;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Begin(e); g.Clear(Theme.Chassis);
            if (markTo != Array.IndexOf(Order, Current) && !slide.Enabled) markFrom = markTo = Array.IndexOf(Order, Current);   // set from outside
            float railY = LH - 3;
            using (var b = new SolidBrush(Theme.Line)) g.FillRectangle(b, 18, railY, LW - 36, 2);
            for (int i = 0; i < 3; i++)
            {
                var lvl = Order[i]; var r = Tile(i); bool on = lvl == Current, hot = hover == i && !on;
                var items = Cats.Where(c => c.Level == lvl).ToList();
                long total = items.Sum(c => c.Bytes), ticked = items.Where(c => c.Included).Sum(c => c.CleanBytes);
                int nTicked = items.Count(c => c.Included && (c.Blocked || c.Bytes > 0 || c.Files > 0));
                int nItems = items.Count(c => c.Blocked || c.Bytes > 0 || c.Files > 0);
                // the open section is a filled tab standing on the rail; a hovered one gets a faint fill
                if (on || hot)
                    using (var path = TopRound(new RectangleF(r.X, r.Y, r.Width, railY - r.Y), 6))
                    using (var b = new SolidBrush(on ? Theme.Selected : Theme.Hover)) g.FillPath(b, path);
                float x = r.X + Pad + 4;
                // risk in greyscale too: the same small gutter mark as the rows (none for Quick clean)
                var hf = on ? fSubB : fName2; Color hc = on || hot ? Theme.Ink : Theme.Muted;
                string nameDot = Theme.TierName(lvl);
                using (var b = new SolidBrush(hc)) g.DrawString(nameDot, hf, b, x, r.Y + 10);
                // placed by the semibold width always, so the box doesn't shift when the section opens
                float bx2 = x + g.MeasureString(nameDot, fSubB).Width + 10;
                boxes[i] = RectangleF.Empty;
                if (nItems > 0)   // the box: filled with a tick (all), a dash (some), or empty (none)
                {
                    float th2 = g.MeasureString("Ag", fSubB).Height;
                    var bxr = new RectangleF(bx2, r.Y + 10 + th2 / 2 - 7.5f, 14, 14); boxes[i] = bxr;
                    bool all = nTicked == nItems;
                    // rounded like the rows' boxes
                    if (nTicked == 0) using (var path = Theme.Round(bxr, 2)) using (var pen = new Pen(on || hot ? Theme.Ink : Theme.Soft, 1.4f)) g.DrawPath(pen, path);
                    else
                    {
                        using (var path = Theme.Round(bxr, 2)) using (var b = new SolidBrush(Theme.Orange)) g.FillPath(b, path);
                        if (all) using (var pen = new Pen(Theme.OnOrange, 1.7f)) g.DrawLines(pen, new[] { new PointF(bxr.X + 3f, bxr.Y + 7.2f), new PointF(bxr.X + 6f, bxr.Y + 10.2f), new PointF(bxr.X + 11.2f, bxr.Y + 3.8f) });
                        else using (var b = new SolidBrush(Theme.OnOrange)) g.FillRectangle(b, bxr.X + 3.5f, bxr.Y + 6.1f, 7, 2f);
                    }
                    bx2 = bxr.Right + 6;
                }
                string count = nItems == 0 ? "nothing to clean" : nTicked == 0 ? "none ticked" : nTicked == nItems && ticked >= total ? "all " + Engine.Fmt(total) + " ticked" : Of(ticked, total) + " ticked";
                using (var b = new SolidBrush(hc))
                using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                    g.DrawString(count, hf, b, new RectangleF(bx2, r.Y + 10, Math.Max(10, r.Right - bx2 - 10), 22), sf);
                if (nItems > 0)   // the box and its words are one target
                {
                    float cw = Math.Min(g.MeasureString(count, hf).Width, r.Right - bx2 - 10);
                    boxes[i] = new RectangleF(boxes[i].X, boxes[i].Y, bx2 + cw - boxes[i].X, boxes[i].Height);
                    if (boxHover == i) using (var pen = new Pen(hc)) g.DrawLine(pen, bx2 + 2, r.Y + 30, bx2 + cw - 2, r.Y + 30);
                }
                // the big size, and what's in it beside the number
                string size = Engine.Fmt(total); var ns = g.MeasureString(size, fBig);
                float ny = r.Y + 32;
                using (var b = new SolidBrush(on || hot ? Theme.Ink : Theme.Muted)) g.DrawString(size, fBig, b, r.X + Pad, ny);
                float dx = r.X + Pad + ns.Width + 6, dw = r.Right - dx - 12;
                if (dw > 60)
                    using (var b = new SolidBrush(on ? Theme.Muted : Theme.Soft))
                    using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisWord })
                    {
                        string d = Theme.TierDesc(lvl);
                        dw = Theme.Balanced(g, d, fDesc, dw);   // two even lines, never one word alone on the second
                        var dm = g.MeasureString(d, fDesc, (int)dw);
                        float dh = Math.Min(dm.Height, 36);
                        g.DrawString(d, fDesc, b, new RectangleF(dx, ny + ns.Height / 2 - dh / 2 + 1, dw, dh + 2), sf);
                    }
            }
            // the orange marker under the open section, sliding when you switch
            float tw = Tile(0).Width, mx = 18 + MarkAt() * (tw + Gap);
            using (var b = new SolidBrush(Theme.Orange)) g.FillRectangle(b, mx, railY - 2, tw, 5);
        }
    }

    // The items of one section. One strong checkbox per row, a "what happens" line, the size, and a chevron:
    // the whole row opens the item's panel; only the box ticks it.
    // Light / Dark / System: three small segments at the top right. The lit one is dark (a pressed key).
    // Top right: the guide key, then Light / Dark / System as three icon keys. Hovering one names it beside them.
    class ThemeSwitch : Canvas
    {
        static readonly string[] Names = { "Light", "Dark", "System default" };
        static readonly Theme.Mode[] Modes = { Theme.Mode.Light, Theme.Mode.Dark, Theme.Mode.System };
        public event EventHandler GuideClicked;
        readonly Font fTip = Theme.CSans(8.75f), fQ = Theme.CBold(10f);
        int hover = -1;   // 0..2 theme, 3 guide
        const float C = 26, Gap = 10, H = 26;
        public static int DesignWidth { get { return (int)(C * 4 + Gap + 220); } }
        float X0 { get { return LW - C * 3; } }               // the theme group
        RectangleF GuideR { get { return new RectangleF(X0 - Gap - C, 1, C, H - 2); } }
        RectangleF Cell(int i) { return new RectangleF(X0 + i * C, 1, C, H - 2); }
        public RectangleF Bounds_ { get { var g = GuideR; return new RectangleF(g.X, 0, LW - g.X, H); } }   // for the guide's last step
        int Hit(float x, float y) { for (int i = 0; i < 3; i++) if (Cell(i).Contains(x, y)) return i; return GuideR.Contains(x, y) ? 3 : -1; }
        public ThemeSwitch() { Cursor = Cursors.Hand; }
        protected override void OnMouseMove(MouseEventArgs e) { int h = Hit(X(e), Y(e)); Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; if (h != hover) { hover = h; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            int i = Hit(X(e), Y(e));
            if (i == 3) { if (GuideClicked != null) GuideClicked(this, EventArgs.Empty); }
            else if (i >= 0) { Theme.SetMode(Modes[i]); Invalidate(); }
        }

        static void Icon(Graphics g, int i, RectangleF r, Color ink, Color bg)
        {
            float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            using (var pen = new Pen(ink, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            using (var b = new SolidBrush(ink))
            {
                if (i == 0)   // sun
                {
                    g.FillEllipse(b, cx - 3.6f, cy - 3.6f, 7.2f, 7.2f);
                    for (int k = 0; k < 8; k++) { double a = k * Math.PI / 4; g.DrawLine(pen, cx + (float)Math.Cos(a) * 6f, cy + (float)Math.Sin(a) * 6f, cx + (float)Math.Cos(a) * 8.2f, cy + (float)Math.Sin(a) * 8.2f); }
                }
                else if (i == 1)   // moon
                {
                    using (var disc = new GraphicsPath())
                    using (var cut = new GraphicsPath())
                    {
                        disc.AddEllipse(cx - 6.5f, cy - 6.5f, 13, 13); cut.AddEllipse(cx - 2.2f, cy - 9.2f, 12, 12);
                        using (var rg = new Region(disc)) { rg.Exclude(cut); g.FillRegion(b, rg); }
                    }
                }
                else if (i == 2)   // system: half light, half dark
                {
                    g.DrawEllipse(pen, cx - 6.5f, cy - 6.5f, 13, 13);
                    g.FillPie(b, cx - 6.5f, cy - 6.5f, 13, 13, 90, 180);
                }
                else   // guide
                {
                    g.DrawEllipse(pen, cx - 7.5f, cy - 7.5f, 15, 15);
                    using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center }) g.DrawString("?", Theme.CBold(8.5f), b, new RectangleF(cx - 8, cy - 7.5f, 16, 16), sf);
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Begin(e); g.Clear(Theme.Chassis);
            // the guide key: a small key of its own
            var gr = GuideR;
            using (var p = Theme.Round(new RectangleF(gr.X, gr.Y + 2, gr.Width, gr.Height), 4)) using (var b = new SolidBrush(Theme.KeyShadow)) g.FillPath(b, p);
            using (var p = Theme.Round(gr, 4)) using (var b = new SolidBrush(hover == 3 ? ControlPaint.Light(Theme.Key, 0.3f) : Theme.Key)) g.FillPath(b, p);
            Icon(g, 3, gr, Theme.Ink, Theme.Key);
            // the theme group: one strip, the chosen one lit
            var all = new RectangleF(X0, 1, C * 3, H - 2);
            using (var p = Theme.Round(new RectangleF(all.X, all.Y + 2, all.Width, all.Height), 4)) using (var b = new SolidBrush(Theme.KeyShadow)) g.FillPath(b, p);
            using (var p = Theme.Round(all, 4)) using (var b = new SolidBrush(Theme.Key)) g.FillPath(b, p);
            for (int i = 0; i < 3; i++)
            {
                var r = Cell(i); bool on = Theme.Chosen == Modes[i];
                var inner = RectangleF.Inflate(r, -2.5f, -2.5f);
                if (on) using (var p = Theme.Round(inner, 3)) using (var b = new SolidBrush(Theme.Ink)) g.FillPath(b, p);
                else if (i == hover) using (var p = Theme.Round(inner, 3)) using (var b = new SolidBrush(Theme.Hover)) g.FillPath(b, p);
                Icon(g, i, r, on ? Theme.Pane : Theme.Muted, on ? Theme.Ink : Theme.Key);
            }
            // the name of what's under the pointer, beside the keys
            if (hover >= 0)
            {
                string t = hover == 3 ? "Guide: how Space Hunter works" : Names[hover] + (Theme.Chosen == Modes[hover] ? "  (on)" : "");
                var m = g.MeasureString(t, fTip);
                using (var b = new SolidBrush(Theme.Ink)) g.DrawString(t, fTip, b, gr.X - 10 - m.Width, (H - m.Height) / 2 + 1);
            }
        }
    }

    class CategoryList : Canvas
    {
        class Row { public float Y, H; public Category C; public string Note; public bool Tools; }
        readonly List<Row> rows = new List<Row>();
        List<Category> cats = new List<Category>();
        public Level Tier = Level.Safe;
        float totalH, scroll, dragOffset; int hover = -1; bool dragThumb;
        const float RowH = 54, SmallH = 38;
        float toolsH = 110;   // the section header row: set from the measured explain text
        public float HeaderH { get { return toolsH; } }
        public Category Selected, OpenItem;   // OpenItem: the row whose panel is showing
        public event EventHandler SelectionChanged, CheckedChanged, PanelRequested;
        Font fName = Theme.CSemi(10.5f), fNameSel = Theme.CBold(10.5f), fLine = Theme.CSans(9f), fSize = Theme.CSemi(11f),
             fFiles = Theme.CSans(8.5f), fTool = Theme.CSemi(9f), fTitle = Theme.CBold(10f), fNote = Theme.CMonoSemi(7.5f), fNoticeB = Theme.CSemi(9.75f);
        RectangleF tickAll, tickNone;

        public CategoryList() { TabStop = true; }

        public void SetItems(List<Category> c) { cats = c; Relayout(); }

        public void Relayout()
        {
            rows.Clear(); float y = 0;
            float th = toolsH;   // the section's heading, its explain line(s) and call to action (measured in OnPaint)
            rows.Add(new Row { Y = y, H = th, Tools = true }); y += th;
            var items = cats.Where(x => x.Level == Tier).ToList();
            var full = items.Where(x => !IsEmpty(x)).OrderByDescending(x => x.Bytes).ToList();
            var empty = items.Where(IsEmpty).ToList();
            foreach (var it in full) { rows.Add(new Row { Y = y, H = RowH, C = it }); y += RowH; }
            if (empty.Count > 0)
            {
                rows.Add(new Row { Y = y, H = 34, Note = "Nothing to clean right now (" + empty.Count + ")" }); y += 34;
                foreach (var it in empty) { rows.Add(new Row { Y = y, H = SmallH, C = it }); y += SmallH; }
            }
            totalH = y + 12;
            if (Selected == null || Selected.Level != Tier || !cats.Contains(Selected)) Selected = full.Concat(empty).FirstOrDefault();
            ScrollTo(scroll);
            Invalidate();
        }

        static bool IsEmpty(Category c) { return c.Refused == null && !c.Blocked && c.Scanned && c.Bytes == 0 && c.Files == 0; }

        float MaxScroll { get { return Math.Max(0, totalH - LH); } }
        void ScrollTo(float v) { scroll = Math.Max(0, Math.Min(MaxScroll, v)); Invalidate(); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); ScrollTo(scroll); }
        protected override void OnMouseWheel(MouseEventArgs e) { ScrollTo(scroll - e.Delta / 120f * RowH); }
        protected override bool IsInputKey(Keys k) { return k == Keys.Up || k == Keys.Down || k == Keys.Space || k == Keys.Enter || base.IsInputKey(k); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            var list = rows.Where(r => r.C != null).Select(r => r.C).ToList();
            int i = list.IndexOf(Selected);
            if (e.KeyCode == Keys.Down && i < list.Count - 1) Select(list[i + 1]);
            else if (e.KeyCode == Keys.Up && i > 0) Select(list[i - 1]);
            else if (e.KeyCode == Keys.Space && Selected != null) Toggle(Selected);
            else if (e.KeyCode == Keys.Enter && Selected != null && PanelRequested != null) PanelRequested(Selected, EventArgs.Empty);
        }

        void Select(Category c)
        {
            Selected = c;
            var r = rows.First(x => x.C == c);
            if (r.Y < scroll) ScrollTo(r.Y);
            else if (r.Y + r.H > scroll + LH) ScrollTo(r.Y + r.H - LH);
            Invalidate();
            if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
        }

        readonly Font fChip = Theme.CSemi(8.25f);
        // what you picked, against the whole item: files and space
        static string PickChip(Category c)
        {
            if (c.PickBytes < 0) return "measuring your picks…";
            string files = c.PickFiles <= c.Files ? c.PickFiles.ToString("N0") + " of " + c.Files.ToString("N0") + " files"
                                                  : c.PickFiles.ToString("N0") + (c.PickFiles == 1 ? " file" : " files");
            string size = c.PickBytes <= c.Bytes ? Engine.Fmt(c.PickBytes) + " of " + Engine.Fmt(c.Bytes) : Engine.Fmt(c.PickBytes);
            return files + "  ·  " + size;
        }

        void Toggle(Category c)
        {
            if (IsEmpty(c) || c.Refused != null) return;
            bool picked = c.Partial;
            c.Checked = !picked && !c.Checked; c.Picks.Clear(); c.PickBytes = -1; Invalidate();   // ticked by your picks: unticking clears them
            if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
        }

        public void SetAll(bool on)
        {
            foreach (var c in cats.Where(x => x.Level == Tier && !IsEmpty(x))) { c.Checked = on; c.Picks.Clear(); }
            Invalidate(); if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
        }

        RectangleF Thumb()
        {
            if (MaxScroll <= 0) return RectangleF.Empty;
            float h = Math.Max(40, LH * LH / totalH);
            return new RectangleF(LW - 8, (LH - h) * scroll / MaxScroll, 4, h);
        }

        Row At(float y) { y += scroll; return rows.FirstOrDefault(x => x.C != null && y >= x.Y && y < x.Y + x.H); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            float x = X(e), y = Y(e);
            var th = Thumb();
            if (!th.IsEmpty && x >= LW - 12) { dragThumb = true; dragOffset = y - th.Y; return; }
            if (tickAll.Contains(x, y)) { SetAll(true); return; }
            if (tickNone.Contains(x, y)) { SetAll(false); return; }
            var r = At(y);
            if (r == null) return;
            if (x < 56) { Toggle(r.C); return; }
            Selected = r.C; Invalidate();
            if (PanelRequested != null) PanelRequested(r.C, EventArgs.Empty);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            float x = X(e), y = Y(e);
            if (dragThumb) { var th = Thumb(); float track = LH - th.Height; if (track > 0) ScrollTo((y - dragOffset) * MaxScroll / track); return; }
            var r = At(y); int hv = r == null ? -1 : rows.IndexOf(r);
            Cursor = hv >= 0 || tickAll.Contains(x, y) || tickNone.Contains(x, y) ? Cursors.Hand : Cursors.Default;
            if (hv != hover) { hover = hv; Invalidate(); }
        }
        protected override void OnMouseUp(MouseEventArgs e) { dragThumb = false; }
        protected override void OnMouseLeave(EventArgs e) { if (hover != -1) { hover = -1; Invalidate(); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Begin(e); g.Clear(Theme.Chassis);
            // the rows span the same edges as the section rail above (18 in from each side)
            float x0 = 18, w = LW - 18;
            float wantTools = toolsH;
            var nowrap = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            for (int ri = 0; ri < rows.Count; ri++)
            {
                var r = rows[ri];
                float top = r.Y - scroll;
                if (r.Tools)   // measured even when scrolled out of view, so its height is always right
                {
                    float ew0 = Theme.Balanced(g, Theme.TierExplain(Tier), fLine, Math.Min(w - 40, 820));
                    float eh0 = Math.Min(g.MeasureString(Theme.TierExplain(Tier), fLine, (int)ew0).Height, 38);
                    wantTools = (float)Math.Round(40 + eh0 + 8 + 20 + 12);   // heading, explain, gap, call to action, gap
                }
                if (top > LH || top + r.H < 0) continue;
                if (r.Tools)
                {
                    string t = Theme.TierName(Tier).ToUpperInvariant();   // the line under it says what's in it
                    using (var b = new SolidBrush(Theme.Ink)) g.DrawString(t, fTitle, b, 22, top + 14);
                    var a = "Untick all"; var n = "Tick all";
                    var aw = g.MeasureString(a, fTool); var nw = g.MeasureString(n, fTool);
                    tickNone = new RectangleF(w - aw.Width - 2, top + 13, aw.Width + 4, 22);   // its underline ends on the rail's end
                    tickAll = new RectangleF(tickNone.X - nw.Width - 22, top + 13, nw.Width + 4, 22);
                    using (var b = new SolidBrush(Theme.Ink)) { g.DrawString(n, fTool, b, tickAll.X + 2, top + 14); g.DrawString(a, fTool, b, tickNone.X + 2, top + 14); }
                    using (var p = new Pen(Theme.Ink)) { g.DrawLine(p, tickAll.X + 2, top + 33, tickAll.Right - 2, top + 33); g.DrawLine(p, tickNone.X + 2, top + 33, tickNone.Right - 2, top + 33); }
                    // what this section is, in one plain line (two even lines when it wraps)
                    float ew = Theme.Balanced(g, Theme.TierExplain(Tier), fLine, Math.Min(w - 40, 820));
                    var em = g.MeasureString(Theme.TierExplain(Tier), fLine, (int)ew);
                    using (var b = new SolidBrush(Theme.Muted)) g.DrawString(Theme.TierExplain(Tier), fLine, b, new RectangleF(22, top + 40, ew, 38));
                    // the call to action: its own line, an orange bar and orange-deep words
                    float cy2 = top + 40 + Math.Min(em.Height, 38) + 8;
                    using (var b = new SolidBrush(Theme.Orange)) g.FillRectangle(b, 22, cy2 + 1, 3, 17);
                    using (var b = new SolidBrush(Theme.OrangeDeep))
                    using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisWord, FormatFlags = StringFormatFlags.NoWrap })
                        g.DrawString(Theme.TierCta(Tier), fNoticeB, b, new RectangleF(32, cy2, w - 50, 20), sf);
                    using (var p = new Pen(Theme.Line)) g.DrawLine(p, x0, top + r.H - 1, w, top + r.H - 1);
                    continue;
                }
                if (r.Note != null) { using (var b = new SolidBrush(Theme.Soft)) g.DrawString(r.Note, fLine, b, 22, top + 12); continue; }

                var c = r.C;
                bool empty = IsEmpty(c), small = r.H == SmallH;
                // the fill spans exactly the separator's span
                if (c == Selected || hover == ri) using (var br = new SolidBrush(c == Selected ? Theme.Selected : Theme.Hover)) g.FillRectangle(br, x0, top, w - x0, r.H - 1);
                using (var p = new Pen(Theme.Line)) g.DrawLine(p, x0, top + r.H - 1, w, top + r.H - 1);

                // checkbox: filled orange with a dark tick, an ink outline, or grey when there's nothing to clear
                var box = new RectangleF(24, top + (r.H - 17) / 2f, 17, 17);
                if (empty || c.Refused != null) using (var path = Theme.Round(box, 2)) using (var pen = new Pen(Theme.Line, 1.6f)) g.DrawPath(pen, path);
                else if (c.Checked || c.Partial)   // ticked, by the item or by the files you picked in it
                {
                    using (var path = Theme.Round(box, 2)) using (var br = new SolidBrush(Theme.Orange)) g.FillPath(br, path);
                    using (var pen = new Pen(Theme.OnOrange, 2.2f) { StartCap = LineCap.Square, EndCap = LineCap.Square })
                        g.DrawLines(pen, new[] { new PointF(box.X + 4, box.Y + 8.5f), new PointF(box.X + 7.5f, box.Y + 12), new PointF(box.X + 13, box.Y + 5) });
                }
                else using (var path = Theme.Round(box, 2)) using (var pen = new Pen(Theme.Ink, 1.6f)) g.DrawPath(pen, path);

                // chevron at the end: the whole row opens its panel
                using (var pen = new Pen(c == OpenItem ? Theme.Orange : hover == ri ? Theme.Ink : Theme.Soft, c == OpenItem ? 2.4f : 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                {
                    float cx = w - 12, cy = top + r.H / 2f;
                    g.DrawLines(pen, new[] { new PointF(cx - 4, cy - 6), new PointF(cx + 2, cy), new PointF(cx - 4, cy + 6) });
                }

                float x = 56, right = w - 34;
                string size = c.Blocked ? "size after cleaning" : !c.Scanned ? "..." : c.Kind == Kind.RunHistory ? c.Files + (c.Files == 1 ? " entry" : " entries") :
                              c.Partial ? (c.PickBytes < 0 ? "..." : Engine.Fmt(c.PickBytes)) : Engine.Fmt(c.Bytes);
                string files = !small && !c.Blocked && c.Scanned && c.Kind != Kind.RunHistory ? (c.Partial ? "picked" : c.Files.ToString("N0") + (c.Files == 1 ? " file" : " files")) : "";
                var sizeFont = c.Blocked ? fLine : fSize;
                var sz = g.MeasureString(size, sizeFont); var fz = g.MeasureString(files, fFiles);
                // the size sits on the name's baseline, the file count on the subline's
                using (var br = new SolidBrush(empty || c.Blocked ? Theme.Soft : Theme.Ink)) g.DrawString(size, sizeFont, br, right - sz.Width, top + (c.Blocked ? 18 : small ? 7.6f : 7.4f));
                using (var br = new SolidBrush(Theme.Soft)) g.DrawString(files, fFiles, br, right - fz.Width, top + 30.8f);

                float tw = right - Math.Max(sz.Width, fz.Width) - 18 - x;
                var nameFont = c == Selected ? fNameSel : fName;
                if (small)
                {
                    using (var br = new SolidBrush(Theme.Soft)) g.DrawString(c.Name, fLine, br, new RectangleF(x, top + 10, tw, 20), nowrap);
                    continue;
                }
                using (var br = new SolidBrush(Theme.Ink)) g.DrawString(c.Name, nameFont, br, new RectangleF(x, top + 8, tw, 22), nowrap);
                if (c.Partial)   // a small display chip: "1 of 10 files · 12 MB of 340 MB"
                {
                    string chip = PickChip(c);
                    var cs = g.MeasureString(chip, fChip);
                    float cw = Math.Min(cs.Width + 12, tw);
                    // the item's own description first, then the chip; the description gives way when space is short
                    float dw = Math.Min(g.MeasureString(c.Short, fLine, PointF.Empty, StringFormat.GenericTypographic).Width + 14, tw - cw - 8);
                    if (dw > 30) using (var br = new SolidBrush(Theme.Soft)) g.DrawString(c.Short, fLine, br, new RectangleF(x, top + 30, dw, 18), nowrap);
                    else dw = -8;
                    var cr = new RectangleF(x + dw + 4, top + 29, cw, 19);
                    using (var p = Theme.Round(cr, 3)) using (var br = new SolidBrush(Theme.Screen)) g.FillPath(br, p);
                    using (var br = new SolidBrush(Theme.DisplayInk)) g.DrawString(chip, fChip, br, new RectangleF(cr.X + 6, cr.Y + 2.5f, cr.Width - 8, 16), nowrap);
                    continue;
                }
                string sub = c.Blocked ? "A Windows system folder" :
                             c.Short + (c.AgeChoice ? (c.AgeMinutes > 0 ? "  ·  older than " + Engine.FmtAge(c.AgeMinutes) : "  ·  everything") : c.Kind == Kind.Contents || c.Kind == Kind.Subfolders ? "  ·  all of it" : "");
                using (var br = new SolidBrush(Theme.Soft)) g.DrawString(sub, fLine, br, new RectangleF(x, top + 30, tw, 18), nowrap);
            }
            var th = Thumb();
            if (!th.IsEmpty) using (var p = Theme.Round(th, 2)) using (var br = new SolidBrush(Color.FromArgb(120, Theme.Ink))) g.FillPath(br, p);
            if (Math.Abs(wantTools - toolsH) > 0.5f) { toolsH = wantTools; Relayout(); }   // the header's real height: lay out again
        }
    }

    // The panel for one item: what to clean (older-than or everything), the notes, and its folders with a keep switch.
    // How things are cleaned (delete or Recycle Bin) is chosen once, in the confirm window.
    class DetailPanel : Panel, IMessageFilter
    {
        public event EventHandler AgeChanged, KeepChanged, CloseRequested, RemoveFromHome, TickChanged;
        Category cat;
        readonly Panel clip = new Panel(), thumb = new Panel();
        readonly FlowLayoutPanel flow = new FlowLayoutPanel();
        Label tag, title, stats, what, deletes, safe, after, ageHint, ageValue, foldersHead, whatHead;
        Key olderKey, allKey, minus, plus, uMin, uHrs, uDays, closeKey, removeKey;
        FlowLayoutPanel modeRow, ageRow, folders, removeRow;
        Label insideHead, insideHint; InsideList inside;
        TableLayoutPanel summary; Label sumWhat, sumGoes, sumAfter; LinkLabel moreLink; bool more;
        readonly List<Control> details = new List<Control>();   // the full notes, shown by "More about this item"
        int value = 14, unit = 2;   // unit: 0 minutes, 1 hours, 2 days
        bool loading;

        public DetailPanel()
        {
            BackColor = Theme.Pane; DoubleBuffered = true;
            // a fixed band for the section name and Close; one pixel at the left keeps the Line-coloured rule visible
            Padding = new Padding(1, Theme.D(60), 0, 0);
            clip.Dock = DockStyle.Fill; clip.BackColor = Theme.Pane; Controls.Add(clip);
            flow.FlowDirection = FlowDirection.TopDown; flow.WrapContents = false;
            flow.AutoScroll = false; flow.AutoSize = true; flow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            flow.Padding = new Padding(Theme.D(28) - 1, Theme.D(2), Theme.D(24), Theme.D(30));   // less the rule's pixel flow.BackColor = Theme.Pane; flow.Location = Point.Empty;
            clip.Controls.Add(flow);
            // no stock scrollbar: the wheel moves the content, and a thin bar shows where you are
            thumb.BackColor = Theme.Disabled; thumb.Width = Theme.D(4); thumb.Visible = false;
            clip.Controls.Add(thumb); thumb.BringToFront();
            clip.Resize += (s, e) => { flow.MinimumSize = new Size(clip.ClientSize.Width, 0); flow.MaximumSize = new Size(clip.ClientSize.Width, 0); FitWidths(); ScrollBy(0); };
            flow.SizeChanged += (s, e) => ScrollBy(0);
            Application.AddMessageFilter(this);
            Disposed += (s, e) => Application.RemoveMessageFilter(this);

            closeKey = new Key("close") { Height = Theme.D(30) };
            closeKey.Click += (s, e) => { if (CloseRequested != null) CloseRequested(this, EventArgs.Empty); };
            Controls.Add(closeKey); closeKey.BringToFront();
            Resize += (s, e) => PlaceHead();

            tag = L(Theme.Semi(9.5f), Theme.Muted, 2);
            flow.Controls.Remove(tag); Controls.Add(tag); tag.Location = new Point(Theme.D(28 + 1.6f), Theme.D(26));   // letters on the title's left edge
            tag.SizeChanged += (s, e) => PlaceHead();
            // the name wraps to a second line rather than cutting off (FitWidths caps it at two lines)
            title = L(Theme.Bold(14f), Theme.Ink, 6); title.AutoEllipsis = true;
            stats = L(Theme.Sans(10.5f), Theme.Ink, 14);
            stats.Margin = new Padding(Theme.D(0.8f), 0, 0, Theme.D(14));   // smaller type sits a hair further in: line its letters up with the title's

            whatHead = Head("What to clean");
            modeRow = Row(6);
            olderKey = new Key("only older than"); allKey = new Key("everything");
            olderKey.Margin = Theme.Pad(0, 0, 4, 0); allKey.Margin = new Padding(0);
            modeRow.Controls.AddRange(new Control[] { olderKey, allKey });
            ageRow = Row(4);
            minus = new Key("−") { Width = Theme.D(34) }; plus = new Key("+") { Width = Theme.D(34) };
            ageValue = new Label { AutoSize = false, Width = Theme.D(48), Height = Theme.D(34), TextAlign = ContentAlignment.MiddleCenter, Font = Theme.Semi(13f), ForeColor = Theme.Ink, BackColor = Theme.Pane };
            uMin = new Key("min"); uHrs = new Key("hours"); uDays = new Key("days");
            minus.Margin = new Padding(0); plus.Margin = Theme.Pad(0, 0, 10, 0);
            foreach (var k in new[] { uMin, uHrs, uDays }) k.Margin = Theme.Pad(0, 0, 4, 0);
            ageRow.Controls.AddRange(new Control[] { minus, ageValue, plus, uMin, uHrs, uDays });
            ageHint = L(Theme.Sans(9.25f), Theme.Soft, 18);

            olderKey.Click += (s, e) => { if (cat != null && cat.AgeMinutes == 0) SetAge(cat.LastAge > 0 ? cat.LastAge : 14 * Engine.Day); };
            allKey.Click += (s, e) => { if (cat != null && cat.AgeMinutes > 0) { cat.LastAge = cat.AgeMinutes; SetAge(0); } };
            minus.Click += (s, e) => { if (value > 1) { value--; AgeFromKeys(); } };
            plus.Click += (s, e) => { if (value < 999) { value++; AgeFromKeys(); } };
            uMin.Click += (s, e) => { unit = 0; AgeFromKeys(); };
            uHrs.Click += (s, e) => { unit = 1; AgeFromKeys(); };
            uDays.Click += (s, e) => { unit = 2; AgeFromKeys(); };

            what = Section("What is it?");
            deletes = Section("What gets cleaned");
            safe = Section("Is it safe?");
            after = Section("What happens after");
            // three short lines always in view; the full notes above open with "More about this item"
            summary = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, RowCount = 3, BackColor = Theme.Pane, Margin = new Padding(Theme.D(1.6f), Theme.D(4), 0, Theme.D(4)), Padding = new Padding(0) };
            summary.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.D(92))); summary.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            // the key a pixel lower, so it shares its value's baseline
            Func<string, Label> sk = t => new Label { Text = t, AutoSize = true, Font = Theme.Sans(9.5f), ForeColor = Theme.Muted, BackColor = Theme.Pane, Margin = new Padding(0, Math.Max(1, Theme.D(0.8f)), 0, Theme.D(4)), UseMnemonic = false };
            Func<Label> sv = () => new Label { AutoSize = true, Font = Theme.Sans(9.75f), ForeColor = Theme.Ink, BackColor = Theme.Pane, Margin = Theme.Pad(0, 0, 0, 4), UseMnemonic = false };
            sumWhat = sv(); sumGoes = sv(); sumAfter = sv();
            summary.Controls.Add(sk("What it is"), 0, 0); summary.Controls.Add(sumWhat, 1, 0);
            summary.Controls.Add(sk("What goes"), 0, 1); summary.Controls.Add(sumGoes, 1, 1);
            summary.Controls.Add(sk("After"), 0, 2); summary.Controls.Add(sumAfter, 1, 2);
            flow.Controls.Add(summary);
            moreLink = new LinkLabel { Text = "More about this item", AutoSize = true, Font = Theme.Semi(9.5f), LinkColor = Theme.Ink, ActiveLinkColor = Theme.OrangeDeep, VisitedLinkColor = Theme.Ink, BackColor = Theme.Pane, Margin = new Padding(Theme.D(1.6f), 0, 0, Theme.D(14)), LinkBehavior = LinkBehavior.AlwaysUnderline, UseMnemonic = false };
            moreLink.LinkClicked += (s, e) => { more = !more; ShowMore(); };
            flow.Controls.Add(moreLink);
            foldersHead = Head("Folders  ·  keep one to never clean it");
            folders = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Theme.Pad(0, 4, 0, 8), BackColor = Theme.Pane };
            flow.Controls.Add(folders);
            // a folder you added: look inside it and untick what should stay
            insideHead = Head("Inside this folder  \u00b7  untick what should stay");
            insideHint = L(Theme.Sans(9.25f), Theme.Soft, 4);
            insideHint.Text = "Ticked files and folders are cleaned (older than the age above). Click a folder's name to look inside it.";
            inside = new InsideList { Margin = Theme.Pad(0, 2, 0, 14) };
            inside.Changed += (s, e) => { described.Clear(); if (cat != null) Describe(cat); if (KeepChanged != null && cat != null) KeepChanged(cat, EventArgs.Empty); };
            inside.Resized += (s, e) => ScrollBy(0);
            flow.Controls.Add(inside);
            // right under the age setting, so it's the first thing you see for your own folder
            int at = flow.Controls.GetChildIndex(stats) + 1;
            flow.Controls.SetChildIndex(insideHead, at); flow.Controls.SetChildIndex(insideHint, at + 1); flow.Controls.SetChildIndex(inside, at + 2);
            removeRow = Row(8);
            removeKey = new Key("remove from cleanup list");
            removeKey.Click += (s, e) => { if (RemoveFromHome != null && cat != null) RemoveFromHome(cat, EventArgs.Empty); };
            removeRow.Controls.Add(removeKey);
            inside.TickChanged += (s, e) => { if (cat == null) return; ShowMode(); if (TickChanged != null) TickChanged(cat, EventArgs.Empty); };
            // top to bottom: name and size, the short summary, what to clean, the files; the full notes and folders after
            var order = new List<Control> { title, stats, summary, moreLink };
            order.AddRange(details);   // the full notes open right under their link
            order.AddRange(new Control[] { whatHead, modeRow, ageRow, ageHint, insideHead, insideHint, inside }); order.Add(foldersHead); order.Add(folders); order.Add(removeRow);
            for (int i = 0; i < order.Count; i++) flow.Controls.SetChildIndex(order[i], i);
            foreach (var d in details) d.Visible = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var p = new Pen(Theme.Line)) e.Graphics.DrawLine(p, 0, 0, 0, Height);
        }

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != 0x020A || !Visible || !IsHandleCreated || Width < 50) return false;   // WM_MOUSEWHEEL over this panel
            if (!RectangleToScreen(ClientRectangle).Contains(Cursor.Position)) return false;
            int delta = (short)(((long)m.WParam >> 16) & 0xFFFF);
            ScrollBy(delta / 120 * Theme.D(64));
            return true;
        }

        void ScrollBy(int d)
        {
            int min = Math.Min(0, clip.ClientSize.Height - flow.Height);
            flow.Top = Math.Max(min, Math.Min(0, flow.Top + d));
            thumb.Visible = min < 0;
            if (min < 0)
            {
                int h = Math.Max(Theme.D(30), clip.ClientSize.Height * clip.ClientSize.Height / flow.Height);
                thumb.SetBounds(clip.ClientSize.Width - Theme.D(8), (int)((clip.ClientSize.Height - h) * (double)flow.Top / min), Theme.D(4), h);
            }
        }

        FlowLayoutPanel Row(int bottom)
        {
            var r = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Theme.Pad(0, 4, 0, bottom), BackColor = Theme.Pane };
            flow.Controls.Add(r); return r;
        }
        Label L(Font f, Color col, int bottom)
        {
            var l = new Label { AutoSize = true, Font = f, ForeColor = col, Margin = Theme.Pad(0, 0, 0, bottom), BackColor = Theme.Pane, UseMnemonic = false };
            flow.Controls.Add(l); return l;
        }
        Label Head(string t) { var h = L(Theme.Semi(10f), Theme.Muted, 2); h.Margin = new Padding(Theme.D(0.8f), 0, 0, Theme.D(2)); h.Text = t; return h; }
        Label Section(string heading) { var h = Head(heading); var l = L(Theme.Sans(10.25f), Theme.Ink, 14); details.Add(h); details.Add(l); return l; }
        void ShowMore()
        {
            foreach (var d in details) d.Visible = more;
            moreLink.Text = more ? "Less about this item" : "More about this item";
            FitWidths(); ScrollBy(0);
        }
        // the first sentence of a note, for the short summary
        static string First(string t)
        {
            if (string.IsNullOrEmpty(t)) return "";
            int i = t.IndexOf(". "); return i > 0 && i < 160 ? t.Substring(0, i + 1) : t;
        }

        // the header band: Close at the flow's right padding, the section name centred on Close
        void PlaceHead()
        {
            if (closeKey == null || tag == null) return;
            closeKey.Location = new Point(Width - closeKey.Width - Theme.D(24), Theme.D(18));
            // the label's letters sit low in its box: lift it so its capitals centre on CLOSE's
            tag.Location = new Point(tag.Left, closeKey.Top + closeKey.Height / 2 - tag.Height / 2 - Theme.D(2.8f));
        }

        void FitWidths()
        {
            int w = Math.Max(Theme.D(200), clip.ClientSize.Width - flow.Padding.Horizontal - Theme.D(4));
            foreach (Control c in flow.Controls) if (c is Label && c != title) c.MaximumSize = new Size(w, 0);
            title.MaximumSize = new Size(w, 2 * TextRenderer.MeasureText("Ag", title.Font).Height);   // two lines at most
            tag.MaximumSize = new Size(w - closeKey.Width - Theme.D(8), 0);
            inside.Width = w;
            foreach (var l in new[] { sumWhat, sumGoes, sumAfter }) l.MaximumSize = new Size(Math.Max(Theme.D(120), w - Theme.D(96)), 0);
        }

        public Category Current { get { return cat; } }

        public void ShowCategory(Category c) { ShowCategory(c, false); }
        // keepPlace: the same item refreshed (a re-measure after a tick or an age change), so stay where you were
        public void ShowCategory(Category c, bool keepPlace)
        {
            keepPlace = keepPlace && c == cat;
            loading = true; cat = c;
            tag.Text = Theme.TierName(c.Level);
            title.Text = c.Name;
            stats.Text = !c.Scanned ? "Scanning..." : c.Kind == Kind.RunHistory ? c.Files + " entries" :
                         Engine.Fmt(c.Bytes) + "   ·   " + c.Files.ToString("N0") + (c.Files == 1 ? " file" : " files");
            if (c.Refused != null) stats.Text = "Space Hunter won't clean this: " + c.Refused + ". Use “Remove from cleanup list” below.";
            what.Text = c.What; deletes.Text = c.Deletes; safe.Text = c.SafeText; after.Text = c.After;
            sumWhat.Text = First(c.What); sumAfter.Text = First(c.After);
            removeRow.Visible = c.IsCustom;
            ShowAge();

            // every item that cleans folders shows what's in them (a single file you added has nothing inside)
            bool files = c.Kind == Kind.Contents || c.Kind == Kind.Subfolders;
            hasInside = files && c.Refused == null && !(c.IsCustom && File.Exists(c.Roots[0]));
            insideHead.Visible = insideHint.Visible = inside.Visible = hasInside;
            if (c.IsCustom) Describe(c);
            if (hasInside) { inside.Open(c); insideHead.Text = c.IsCustom ? "Inside this folder" : "What's inside"; }
            ShowMode();
            ShowMore();
            folders.Controls.Clear();
            var list = hasInside || c.IsCustom ? new List<string>() : FolderList(c);   // the list above already shows the folders
            foldersHead.Visible = folders.Visible = list.Count > 0;
            foreach (var path in list.Take(14))
            {
                var p = path;
                bool kept = Keep.IsKept(p);
                var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Theme.Pad(0, 2, 0, 2), BackColor = Theme.Pane };
                var k = new Key(kept ? "kept" : "keep") { Height = Theme.D(28), Toggled = kept, Enabled = !Keep.IsBuiltIn(p) };   // Space Hunter's own folder stays kept
                k.Font = Theme.Semi(8.5f);
                k.Click += (s, e) => { if (Keep.IsKept(p)) Keep.Remove(p); else Keep.Add(p); if (KeepChanged != null) KeepChanged(cat, EventArgs.Empty); ShowCategory(cat, true); };
                var ll = new LinkLabel { Text = Theme.MidPath(p, 44), AutoSize = true, Font = Theme.Sans(9.5f), LinkColor = kept ? Theme.Soft : Theme.Ink, ActiveLinkColor = Theme.OrangeDeep, VisitedLinkColor = Theme.Ink, Margin = Theme.Pad(8, 6, 0, 0), LinkBehavior = LinkBehavior.HoverUnderline, UseMnemonic = false };
                ll.LinkClicked += (s, e) => { try { Process.Start("explorer.exe", "\"" + p + "\""); } catch { } };
                row.Controls.Add(k); row.Controls.Add(ll);
                folders.Controls.Add(row);
            }
            if (list.Count > 14) folders.Controls.Add(new Label { Text = "+ " + (list.Count - 14) + " more", AutoSize = true, ForeColor = Theme.Soft, Font = Theme.Sans(9f) });
            FitWidths();
            if (!keepPlace) flow.Top = 0;
            ScrollBy(0);
            loading = false;
        }

        bool hasInside;
        public int ScrollTop { get { return flow.Top; } }   // test hook
        public void ShowFiles() { if (hasInside) { ScrollBy(-flow.Top - insideHead.Top + Theme.D(12)); } }   // test hook: scroll to the files
        // What the item will clean right now: all of it (by its age rule), only what you picked, or nothing yet.
        void ShowMode()
        {
            var c = cat;
            bool age = c.AgeChoice && c.Refused == null && !c.Partial;
            whatHead.Visible = modeRow.Visible = ageRow.Visible = ageHint.Visible = age;
            sumGoes.Text = c.Partial ? "Only the files you tick below, whatever their age, in the next clean." :
                           !c.Checked && hasInside ? "Nothing yet: tick the item, or tick files below to clean just those." :
                           c.AgeChoice ? (c.AgeMinutes > 0 ? "Files untouched for " + Engine.FmtAge(c.AgeMinutes) + " or more." : "Everything in it, whatever its age.") :
                           First(c.Deletes);
            if (hasInside)
                insideHint.Text = c.Checked ? "Everything ticked is cleaned. Untick anything that should always stay. Click a folder to look inside it."
                                : c.Partial ? "Just the ticked files and folders are cleaned, this once. Select all cleans the whole item."
                                : "Tick files or folders to clean just those, this once, or Select all for the whole item. Click a folder to look inside it.";
            inside.Invalidate();
            FitWidths(); ScrollBy(0);
        }

        // The folders an item works on. For folder-per-session items, the sessions themselves (so one can be kept).
        static List<string> FolderList(Category c)
        {
            var roots = Engine.ExistingRoots(c).ToList();
            if (c.Kind != Kind.Subfolders) return roots;
            var subs = new List<string>();
            foreach (var r in roots)
                try { subs.AddRange(Directory.GetDirectories(r).OrderByDescending(d => { try { return new DirectoryInfo(d).LastWriteTime; } catch { return DateTime.MinValue; } })); } catch { }
            return subs;
        }

        void ShowAge()
        {
            bool older = cat.AgeMinutes > 0;
            olderKey.Toggled = older; allKey.Toggled = !older;
            int m = older ? cat.AgeMinutes : (cat.LastAge > 0 ? cat.LastAge : 14 * Engine.Day);
            if (m % Engine.Day == 0) { unit = 2; value = m / Engine.Day; }
            else if (m % Engine.Hour == 0) { unit = 1; value = m / Engine.Hour; }
            else { unit = 0; value = m; }
            ageValue.Text = value.ToString();
            ageValue.ForeColor = older ? Theme.Ink : Theme.Disabled;
            foreach (var k in new[] { minus, plus, uMin, uHrs, uDays }) k.Enabled = older;
            uMin.Toggled = older && unit == 0; uHrs.Toggled = older && unit == 1; uDays.Toggled = older && unit == 2;
            ageHint.Text = (older ? "Files untouched or added within " + Engine.FmtAge(cat.AgeMinutes) + " stay." : "Every file, whatever its age.") +
                           " Open files are always skipped.";
            if (sumGoes != null && !loading && hasInside) ShowMode();
            foreach (var k in new Key[] { olderKey, allKey, uMin, uHrs, uDays }) k.Invalidate();
        }

        void SetAge(int minutes)
        {
            cat.AgeMinutes = minutes; if (minutes > 0) cat.LastAge = minutes;
            ShowAge(); ShowMode();
            if (inside != null) inside.Invalidate();   // What's inside ticks and unticks by the new age at once
            if (!loading && AgeChanged != null) AgeChanged(cat, EventArgs.Empty);
        }

        void AgeFromKeys()
        {
            int mult = unit == 2 ? Engine.Day : unit == 1 ? Engine.Hour : 1;
            SetAge(Math.Max(1, value) * mult);
        }

        public void RefreshStats(Category c) { if (c == cat) ShowCategory(c, true); }

        // A folder you added: say what's actually in it (worked out here, nothing is sent anywhere).
        readonly Dictionary<string, string> described = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Describe(Category c)
        {
            string root = c.Roots[0], text;
            if (described.TryGetValue(root, out text)) { what.Text = text; return; }
            what.Text = "Looking inside " + Path.GetFileName(root) + "...";
            var t = new Thread(() =>
            {
                string d = FolderSummary.Describe(root);
                try { BeginInvoke((Action)(() => { described[root] = d; if (cat != null && cat.IsCustom && string.Equals(cat.Roots[0], root, StringComparison.OrdinalIgnoreCase)) { what.Text = d; FitWidths(); ScrollBy(0); } })); } catch { }
            }) { IsBackground = true };
            t.Start();
        }
    }

    // The item panel docks at the right and pushes the list over, so nothing gets covered.
    // It glides open (ease-out, 260 ms) and closed (200 ms); the form is composited, so frames don't tear.
    class Drawer
    {
        readonly Control host; readonly Panel slot; public readonly DetailPanel Panel;
        readonly System.Windows.Forms.Timer anim = new System.Windows.Forms.Timer { Interval = 10 };
        readonly Stopwatch clock = new Stopwatch();
        bool opening; public bool IsOpen;
        public static int W { get { return Theme.D(460); } }

        public Drawer(Control host)
        {
            this.host = host;
            slot = new DoubleBufferedPanel { Dock = DockStyle.Right, Width = 0, BackColor = Theme.Pane };
            Panel = new DetailPanel();
            slot.Controls.Add(Panel);
            host.Controls.Add(slot);   // added after the list, so it docks first and the list fills what's left
            slot.Resize += (s, e) => Panel.SetBounds(slot.Width - W, 0, W, slot.Height);
            anim.Tick += (s, e) =>
            {
                double t = Math.Min(1, clock.ElapsedMilliseconds / (opening ? 260.0 : 200.0));
                double k = opening ? 1 - Math.Pow(1 - t, 3) : t * t * t;   // decelerate in, accelerate out
                slot.Width = (int)Math.Round(opening ? W * k : W * (1 - k));
                host.Update();
                if (t >= 1) { anim.Stop(); if (!opening) slot.Width = 0; }
            };
        }

        public void Open(Category c, bool instant = false)
        {
            Panel.ShowCategory(c);
            if (IsOpen) return;
            IsOpen = true; opening = true;
            if (instant) { slot.Width = W; return; }
            clock.Restart(); anim.Start();
        }

        public void Close()
        {
            if (!IsOpen) return;
            IsOpen = false; opening = false;
            clock.Restart(); anim.Start();
        }
    }

    class MainForm : Form
    {
        List<Category> cats;
        CategoryList list; TierBar tiers; DisplayPanel disp; Drawer drawer; Panel body;
        Key cleanKey, finderKey, protectKey, resetKey; Label status;
        bool cleaning;   // a clean is running in its window
        ProgressForm running;
        readonly object qLock = new object();
        readonly Queue<Category> scanQueue = new Queue<Category>();
        bool workerRunning;
        readonly System.Windows.Forms.Timer ageDebounce = new System.Windows.Forms.Timer { Interval = 600 };
        Category pendingAge;
        public int GuideAt = -1;   // test hook: open the guide at this step, then snapshot
        ThemeSwitch topKeys; GuideOverlay guide; bool testRun, askShown, guideSeenAtStart = GuideOverlay.Seen;   // test runs never open the guide by themselves
        public int TestAge = -1;   // test hook: the opened item's age, in minutes (not saved)
        public Control CleanKeyForTest { get { return cleanKey; } }
        public string SnapshotPath, OpenPanelFor, FramesDir, KeepPlaceOut; public List<string> PickPaths;   // test hook: picks in the opened item

        // Composited painting: the whole window is drawn off-screen first, so the sliding panel never tears.
        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x02000000; return cp; } }

        public MainForm()
        {
            Theme.Window(this, "Space Hunter", 1200, 820);
            MinimumSize = SizeFromClientSize(Theme.Fit(940, 600));
            StartPosition = FormStartPosition.CenterScreen; KeyPreview = true;

            disp = new DisplayPanel();
            disp.Compact = ClientSize.Height / Theme.S < 760;   // short screens (e.g. 150% on a laptop): a slimmer display
            disp.Dock = DockStyle.Top; disp.Height = Theme.D(disp.DesignHeight);
            var top = new Panel { Dock = DockStyle.Top, Height = Theme.D(disp.DesignHeight + 8 + 32 + 64), Padding = Theme.Pad(18, 8, 18, 0), BackColor = Theme.Chassis };
            var strip = new Panel { Dock = DockStyle.Top, Height = Theme.D(32), BackColor = Theme.Chassis };
            topKeys = new ThemeSwitch { Dock = DockStyle.Right, Width = Theme.D(ThemeSwitch.DesignWidth) };
            strip.Controls.Add(topKeys);
            topKeys.GuideClicked += (s, e) => StartGuide(0);
            var keys = new Panel { Dock = DockStyle.Bottom, Height = Theme.D(64), BackColor = Theme.Chassis };
            finderKey = new Key("storage map") { Accent = true }; protectKey = new Key("protected folders"); resetKey = new Key("default cleanup");
            cleanKey = new Key("clean", true);
            cleanKey.Font = Theme.Semi(11.5f); cleanKey.Height = Theme.D(46); cleanKey.MinimumSize = Theme.D(240, 46); cleanKey.FitWidth();
            keys.Controls.AddRange(new Control[] { finderKey, protectKey, resetKey, cleanKey });
            keys.Resize += (s, e) => PlaceKeys(keys);
            top.Controls.Add(keys); top.Controls.Add(disp); top.Controls.Add(strip);
            // System follows Windows: when Windows switches between light and dark, so does Space Hunter
            Microsoft.Win32.UserPreferenceChangedEventHandler follow = (s, e) => { if (Theme.Chosen == Theme.Mode.System) try { BeginInvoke((Action)Theme.Apply); } catch { } };
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += follow;
            FormClosed += (s, e) => Microsoft.Win32.SystemEvents.UserPreferenceChanged -= follow;

            body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Chassis };
            tiers = new TierBar { Dock = DockStyle.Top, Height = Theme.D(disp.Compact ? 88 : 100) };
            list = new CategoryList { Dock = DockStyle.Fill };
            var listHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Chassis };
            listHost.Controls.Add(list);
            body.Controls.Add(listHost); body.Controls.Add(tiers);
            drawer = new Drawer(listHost);

            status = new Label { Dock = DockStyle.Bottom, Height = Theme.D(34), Padding = Theme.Pad(20, 10, 0, 0), ForeColor = Theme.Muted, Font = Theme.Sans(9f), BackColor = Theme.Chassis, UseMnemonic = false };
            // which version this is, always visible (bottom right)
            var ver = new Label { Text = "Space Hunter " + AppVersion.Text, Dock = DockStyle.Right, AutoSize = false, Width = Theme.D(170), Padding = new Padding(0, Theme.D(9.8f), Theme.D(12.8f), 0),   // on the status line's baseline, ending on the display's right edge
                                  TextAlign = ContentAlignment.TopRight, ForeColor = Theme.Soft, Font = Theme.Sans(8.5f), BackColor = Theme.Chassis, UseMnemonic = false, Cursor = Stats.Enabled ? Cursors.Hand : Cursors.Default };
            ver.Click += (s, e) => { if (!updateBar.Visible || askMode) ShowAsk(); };   // change your answer about the public totals
            status.Controls.Add(ver);
            divider = new Panel { Dock = DockStyle.Bottom, Height = Math.Max(1, Theme.D(1)), BackColor = Theme.Line };
            BuildUpdateBar();
            Controls.Add(body); Controls.Add(top); Controls.Add(updateBar); Controls.Add(divider); Controls.Add(status);

            Load_();
            tiers.Changed += (s, e) => { list.Tier = tiers.Current; list.Relayout(); if (drawer.IsOpen && list.Selected != null) drawer.Open(list.Selected); else drawer.Close(); list.OpenItem = drawer.IsOpen ? drawer.Panel.Current : null; };
            list.SelectionChanged += (s, e) => { if (drawer.IsOpen && list.Selected != null) { drawer.Open(list.Selected); list.OpenItem = list.Selected; } };
            list.PanelRequested += (s, e) => { var c = (Category)s; if (drawer.IsOpen && drawer.Panel.Current == c) drawer.Close(); else drawer.Open(c); list.OpenItem = drawer.IsOpen ? drawer.Panel.Current : null; list.Invalidate(); };
            list.CheckedChanged += (s, e) => { Settings.Save(cats); Refresh_(); };
            tiers.TickAll += (lvl, on) =>
            {
                foreach (var c in cats.Where(x => x.Level == lvl && x.Refused == null && (x.Blocked || x.Bytes > 0 || x.Files > 0))) { c.Checked = on; c.Picks.Clear(); }
                Settings.Save(cats); list.Invalidate(); Refresh_();
                if (drawer.IsOpen && drawer.Panel.Current != null) drawer.Panel.ShowCategory(drawer.Panel.Current, true);
            };
            drawer.Panel.CloseRequested += (s, e) => { drawer.Close(); list.OpenItem = null; list.Invalidate(); };
            drawer.Panel.AgeChanged += (s, e) => { pendingAge = (Category)s; Settings.Save(cats); list.Invalidate(); ageDebounce.Stop(); ageDebounce.Start(); };
            drawer.Panel.KeepChanged += (s, e) => Enqueue(new[] { (Category)s });
            drawer.Panel.TickChanged += (s, e) => { var c = (Category)s; Settings.Save(cats); c.PickBytes = -1; list.Invalidate(); Refresh_(); Enqueue(new[] { c }); };
            drawer.Panel.RemoveFromHome += (s, e) => { var c = (Category)s; foreach (var r in c.Roots) Custom.Remove(r); drawer.Close(); list.OpenItem = null; Load_(); Enqueue(cats); status.Text = "Removed from the cleanup list: " + c.Name; };
            ageDebounce.Tick += (s, e) => { ageDebounce.Stop(); if (pendingAge != null) Enqueue(new[] { pendingAge }); };
            cleanKey.Click += (s, e) =>
            {
                if (running != null) { if (running.WindowState == FormWindowState.Minimized) running.WindowState = FormWindowState.Normal; running.Activate(); return; }
                Clean();
            };
            finderKey.Click += (s, e) =>
            {
                using (var f = new FinderForm(cats))
                {
                    f.ShowDialog(this);
                    if (f.Added) { drawer.Close(); list.OpenItem = null; Load_(); tiers.Current = Level.Caution; list.Tier = Level.Caution; list.Relayout(); Enqueue(cats); status.Text = "Cleanup list updated. Folders you added are in the Caution section, unticked."; }
                }
            };
            protectKey.Click += (s, e) =>
            {
                using (var pf = new ProtectForm())
                {
                    pf.ShowDialog(this);
                    if (pf.Changed) { if (drawer.IsOpen && drawer.Panel.Current != null) drawer.Panel.ShowCategory(drawer.Panel.Current); Enqueue(cats); }
                }
            };
            resetKey.Click += (s, e) => ResetAll();
            FormClosing += (s, e) =>
            {
                if (!cleaning) return;
                e.Cancel = true;   // closing now would cut a clean off halfway
                status.Text = "Cleaning is still running. Press Stop in its window first, or wait for it to finish.";
                foreach (Form f in OwnedForms) if (f is ProgressForm) f.Activate();
            };
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape && guide == null && drawer.IsOpen) { drawer.Close(); list.OpenItem = null; list.Invalidate(); e.Handled = true; } };
            Shown += (s, e) =>
            {
                if (PickPaths != null && OpenPanelFor != null) { var pc = cats.FirstOrDefault(x => x.Id == OpenPanelFor); if (pc != null) { pc.Checked = false; foreach (var pp in PickPaths) pc.Picks.Add(pp); } }
                testRun = SnapshotPath != null || FramesDir != null || KeepPlaceOut != null || GuideAt >= 0;
                list.Focus(); Enqueue(cats);
                if (SnapshotPath == null && FramesDir == null)   // one quiet check for a newer version; offline = nothing happens
                    new Thread(() => { var r = Updater.Check(); if (r != null) try { BeginInvoke((Action)(() => ShowUpdate(r))); } catch { } }) { IsBackground = true }.Start();
            };
            ReadDisk();
        }

        // The first-run guide: the window dims, a lit cut-out sits on each real part in turn. "?" replays it.
        RectangleF Part(Control c, int extraH = 0)
        {
            var p = PointToClient(c.Parent.PointToScreen(c.Location));
            return new RectangleF(p.X / Theme.S, p.Y / Theme.S, c.Width / Theme.S, (c.Height + extraH) / Theme.S);
        }
        void StartGuide(int at)
        {
            if (guide != null) return;
            if (drawer.IsOpen) { drawer.Close(); list.OpenItem = null; list.Invalidate(); }
            Refresh();   // paint everything first: the dimmed backdrop is a picture of this window
            Func<RectangleF> rail = () => { var r = Part(tiers); return new RectangleF(r.X + 8, r.Y, r.Width - 16, r.Height); };
            Func<RectangleF> rows = () => { var lp = Part(list); float hh = list.HeaderH; return new RectangleF(lp.X + 8, lp.Y + hh, lp.Width - 16, Math.Min(lp.Height - hh, 3 * 54)); };
            Action openFirst = () =>
            {
                var first = cats.Where(c => c.Level == tiers.Current && (c.Bytes > 0 || c.Files > 0)).OrderByDescending(c => c.Bytes).FirstOrDefault();
                if (first != null && (!drawer.IsOpen || drawer.Panel.Current != first)) { list.Selected = first; list.OpenItem = first; drawer.Open(first, true); list.Invalidate(); Refresh(); }
            };
            Action closePanel = () => { if (drawer.IsOpen) { drawer.Close(); list.OpenItem = null; list.Invalidate(); Refresh(); } };
            var steps = new List<GuideStep>
            {
                new GuideStep { Get = () => Part(disp), Title = "This is what you can free",
                    Body = "The big number is what's ticked right now. The bar is your disk: orange is ticked, hatched is what you could still free. On the right, your free space over time." },
                new GuideStep { Get = rail, Enter = closePanel, Title = "Three sections, by how careful to be",
                    Body = "Quick clean is safe and ticked for you. Big caches come back when they're needed. Deep clean may include your work. The small box ticks or unticks a whole section." },
                new GuideStep { Get = rows, Enter = closePanel, Title = "Each row is one thing to clean",
                    Body = "Tick a row to include it; the size is what it would free. Click the row itself to see what it is." },
                new GuideStep { Get = () => Part(drawer.Panel), Enter = openFirst, Title = "What it is, what goes, what happens after",
                    Body = "Every item explains itself. Some let you choose \u201conly older than\u201d or \u201ceverything\u201d. Under What's inside, untick files to keep them, or tick just a few to clean only those." },
                new GuideStep { Get = () => Part(finderKey), Enter = closePanel, Title = "See what fills your disk",
                    Body = "Storage map shows every folder by size, on any drive. Nothing there deletes anything; you can add a folder to your cleanup list." },
                new GuideStep { Get = () => Part(protectKey), Title = "Keep what must never go",
                    Body = "Protected folders are never cleaned, whatever is ticked. Space Hunter protects your own files and itself already." },
                new GuideStep { Get = () => Part(resetKey), Title = "Default cleanup",
                    Body = "Back to the first-time setup: Quick clean ticked, the usual ages, and every size measured afresh." },
                new GuideStep { Get = () => Part(cleanKey), Title = "Clean always asks first",
                    Body = "You see the list of what goes and choose Permanently delete or Recycle Bin. When it's done, a ring shows the space you got back." },
                new GuideStep { Get = () => { var r = Part(topKeys); var b = topKeys.Bounds_; return new RectangleF(r.X + b.X, r.Y + b.Y, b.Width, b.Height); },
                    Title = "Your look, and this guide",
                    Body = "Light, dark or your computer's setting. The ? key shows this guide again whenever you want." },
            };
            guide = new GuideOverlay(this, steps, at);
            guide.Closed += () => { var g = guide; guide = null; Controls.Remove(g); g.Dispose(); list.Focus(); };
            Controls.Add(guide); guide.BringToFront(); guide.Focus();
        }

        // (Re)build the item list, e.g. after a folder is added to or removed from the cleanup list.
        void Load_()
        {
            cats = Engine.Build();
            tiers.Cats = cats;
            list.Tier = tiers.Current;
            list.SetItems(cats);
        }

        // Back to the defaults: only "Quick clean" ticked, default ages. Protected folders stay protected.
        void ResetAll()
        {
            foreach (var c in cats) c.ResetToDefault();
            Settings.Save(cats);
            if (drawer.IsOpen && drawer.Panel.Current != null) drawer.Panel.ShowCategory(drawer.Panel.Current);
            Enqueue(cats);
            status.Text = "Default cleanup: Quick clean ticked, the usual ages. Protected folders stay protected.";
        }

        void PlaceKeys(Control keys)
        {
            // The Storage map is a tool of its own, alone at the left. The cleaning keys sit together at the right:
            // protected folders and default cleanup lead into the one big Clean key.
            // the row spans the display above it exactly: Storage map on its left edge, Clean ending on its right edge,
            // and Clean centred on the other keys' centre
            int y = Theme.D(14);
            cleanKey.Location = new Point(keys.Width - cleanKey.Width, y + (resetKey.Height - cleanKey.Height) / 2);
            finderKey.Location = new Point(0, y);
            int x = cleanKey.Left - Theme.D(12);
            foreach (var k in new[] { resetKey, protectKey }) { x -= k.Width; k.Location = new Point(x, y); x -= Theme.D(8); }
        }

        void ReadDisk()
        {
            try { var d = new DriveInfo(Path.GetPathRoot(Engine.W)); disp.Free = d.AvailableFreeSpace; disp.Total = d.TotalSize; } catch { }
            int cl; disp.FreedAll = History.FreedAllTime(out cl); disp.Cleans = cl;
            disp.History = History.Read();
        }

        void Refresh_()
        {
            bool busy; lock (qLock) busy = workerRunning;
            disp.Selected = cats.Where(c => c.Included).Sum(c => c.CleanBytes);
            disp.OnlySafe = !cats.Any(c => c.Included && !c.Safe && (c.Bytes > 0 || c.Files > 0));
            disp.ReviewUnticked = cats.Where(c => !c.Included && !c.Safe && c.Kind != Kind.RecycleBin).Sum(c => c.Bytes);
            disp.SetScanning(busy);
            disp.Invalidate(); tiers.Invalidate(); list.Invalidate();
            cleanKey.Text = cleaning ? "show cleaning" : busy ? "scanning …" : "clean " + Engine.Fmt(disp.Selected);   // the key you're waiting for says why
            cleanKey.Busy = busy && !cleaning; if (cats.Count > 0) cleanKey.Progress = cats.Count(c => c.Scanned) / (float)cats.Count;
            cleanKey.Enabled = cleaning || !busy && cats.Any(c => c.Checked && (c.Blocked || c.Scanned && (c.Bytes > 0 || c.Files > 0)) || c.Partial && c.PickBytes > 0);
            resetKey.Enabled = !cleaning;   // ticks and ages stay as they were until the clean is done
            PlaceKeys(cleanKey.Parent);
        }

        // ---- updates: a card at the bottom, in place of the status line, when GitHub has a newer version ----
        Panel updateBar, divider; Label updateText, updateSub; Key updateKey, laterKey, newsKey; Release pending;
        bool askMode;   // the card is asking about the public totals, not offering an update

        // Asked once (after the first-run guide, on a later launch), and again only when you click the version number.
        public void ShowAsk()
        {
            if (!Stats.Enabled) return;
            askMode = true; bool on = Stats.On;
            updateText.Text = on ? "You're counted in Space Hunter's public totals" : "Count yourself in Space Hunter's public totals?";
            updateSub.Text = "Shares only how much you've freed and your version. Never file names or what was cleaned.";
            updateKey.Text = on ? "keep counting me" : "count me in"; laterKey.Text = on ? "stop" : "no thanks"; newsKey.Text = "what's sent";
            updateKey.Enabled = laterKey.Enabled = newsKey.Enabled = true;
            status.Visible = divider.Visible = false; updateBar.Visible = true; LayoutBar(); updateBar.Invalidate();
        }
        void EndAsk(bool on)
        {
            Stats.Set(on); if (on) Stats.MaybeSend(true);
            askMode = false; updateKey.Text = "update & relaunch"; laterKey.Text = "later"; newsKey.Text = "what's new";
            HideUpdate();
            status.Text = on ? "Thanks: you're counted in the public totals. Click the version number to change it." : "Not counted. Click the version number at the bottom right if you change your mind.";
        }

        void BuildUpdateBar()
        {
            updateBar = new Panel { Dock = DockStyle.Bottom, Height = Theme.D(84), BackColor = Theme.Chassis, Visible = false };
            updateBar.Paint += (s, e) =>
            {
                var g = e.Graphics; Theme.Hq(g); float S = Theme.S;
                var card = new RectangleF(18 * S, 8 * S, updateBar.Width - 36 * S, updateBar.Height - 18 * S);
                using (var p = Theme.Round(card, 7 * S)) using (var b = new SolidBrush(Theme.Screen)) g.FillPath(b, p);
                using (var b = new SolidBrush(Theme.Orange)) g.FillEllipse(b, card.X + 20 * S, card.Y + card.Height / 2 - 5 * S, 10 * S, 10 * S);
            };
            updateBar.Resize += (s, e) => updateBar.Invalidate();
            updateText = new Label { AutoSize = false, Font = Theme.Semi(11f), ForeColor = Theme.DisplayInk, TextAlign = ContentAlignment.BottomLeft, UseMnemonic = false, BackColor = Theme.Screen, AutoEllipsis = true };
            updateSub = new Label { AutoSize = false, Font = Theme.Sans(9f), ForeColor = Theme.DisplayMuted, TextAlign = ContentAlignment.TopLeft, UseMnemonic = false, BackColor = Theme.Screen, AutoEllipsis = true };
            updateKey = new Key("update & relaunch", true); laterKey = new Key("later"); newsKey = new Key("what's new");
            updateBar.Controls.Add(updateText); updateBar.Controls.Add(updateSub); updateBar.Controls.Add(newsKey); updateBar.Controls.Add(laterKey); updateBar.Controls.Add(updateKey);
            updateBar.Resize += (s, e) => LayoutBar();
            laterKey.Click += (s, e) => { if (askMode) EndAsk(false); else HideUpdate(); };
            newsKey.Click += (s, e) => { if (askMode) { try { Process.Start(Stats.Explained); } catch { } } else if (pending != null && pending.Page != null) try { Process.Start(pending.Page); } catch { } };
            updateKey.Click += (s, e) =>
            {
                if (askMode) { EndAsk(true); return; }
                if (pending == null) return;
                if (cleaning) { updateSub.Text = "A clean is running. Update once it's done."; return; }   // never two copies at once
                updateKey.Enabled = laterKey.Enabled = newsKey.Enabled = false;
                var r = pending;
                new Thread(() =>
                {
                    string why = Updater.Install(r, msg => BeginInvoke((Action)(() => updateSub.Text = msg)));
                    BeginInvoke((Action)(() =>
                    {
                        if (why == null) { Close(); return; }   // the new version is starting
                        updateSub.Text = why; laterKey.Enabled = newsKey.Enabled = true; updateKey.Enabled = true; updateKey.Text = "try again";
                    }));
                }) { IsBackground = true }.Start();
            };
        }

        void LayoutBar()
        {
            {
                int mid = Theme.D(8) + (updateBar.Height - Theme.D(18)) / 2, gap = Theme.D(8);
                int y = mid - updateKey.Height / 2;
                updateKey.Location = new Point(updateBar.Width - updateKey.Width - Theme.D(34), y);
                laterKey.Location = new Point(updateKey.Left - laterKey.Width - gap, y);
                newsKey.Location = new Point(laterKey.Left - newsKey.Width - gap, y);
                int tx = Theme.D(18 + 44), tw = Math.Max(10, newsKey.Left - tx - Theme.D(16));
                updateText.Bounds = new Rectangle(tx, mid - Theme.D(22), tw, Theme.D(21));
                updateSub.Bounds = new Rectangle(tx, mid + Theme.D(1), tw, Theme.D(20));
            }
        }

        public void ShowUpdate(Release r)
        {
            pending = r;
            if (askMode) { askMode = false; askShown = false; updateKey.Text = "update & relaunch"; laterKey.Text = "later"; newsKey.Text = "what's new"; updateKey.Enabled = laterKey.Enabled = newsKey.Enabled = true; }
            updateText.Text = "Space Hunter " + r.Version + " is ready";
            updateSub.Text = "You have " + AppVersion.Text + ". Updating takes a few seconds and keeps your settings.";
            // the card takes the status line's place instead of stacking on top of it
            status.Visible = divider.Visible = false;
            updateBar.Visible = true;
        }

        void HideUpdate() { updateBar.Visible = false; status.Visible = divider.Visible = true; }

        void Enqueue(IEnumerable<Category> items)
        {
            if (cleaning) return;   // measured again once the clean is done
            // Safe-to-delete items first, so the ticked total and the CLEAN key are ready first;
            // a few items are measured at once (an SSD answers several folder reads in parallel)
            int start;
            lock (qLock)
            {
                foreach (var c in items.OrderBy(x => x.Level)) { c.ScanGen++; if (!scanQueue.Contains(c)) { c.Scanned = false; scanQueue.Enqueue(c); } }
                start = Math.Min(ScanWorkers - workers, scanQueue.Count);
                if (start <= 0) { list.Invalidate(); return; }
                workers += start; workerRunning = true;
            }
            Refresh_();
            for (int w = 0; w < start; w++) StartScanWorker();
        }

        const int ScanWorkers = 4;
        int _cl;
        int workers;

        void StartScanWorker()
        {
            var t = new Thread(() =>
            {
                while (true)
                {
                    Category c;
                    int gen;
                    lock (qLock) { if (scanQueue.Count == 0) { workers--; break; } c = scanQueue.Dequeue(); gen = c.ScanGen; }
                    BeginInvoke((Action)(() => status.Text = "Scanning  " + c.Name + " ..."));
                    var cnt = new Counter();
                    try { Engine.Process(c, false, cnt); } catch { }   // a problem in one item never closes the app
                    lock (qLock) { if (c.ScanGen != gen) continue; }   // a newer measurement of this item is on its way
                    long pf = 0, pb = -1;
                    if (c.Partial) { var pc = new Counter(); try { Engine.ProcessPicks(c, false, pc); } catch { } pf = pc.Files; pb = pc.Bytes; }   // what you picked
                    c.Files = cnt.Files; c.Bytes = cnt.Bytes; c.PickFiles = pf; c.PickBytes = pb; c.Scanned = true;
                    BeginInvoke((Action)(() => { drawer.Panel.RefreshStats(c); Refresh_(); }));
                }
                lock (qLock) { if (workers > 0) return; workerRunning = false; }   // the last worker out finishes the scan
                BeginInvoke((Action)(() =>
                {
                    ReadDisk(); History.Record(disp.Free); disp.History = History.Read();
                    list.Relayout(); Refresh_();
                    status.Text = History.FreedAllTime(out _cl) == 0
                        ? "Quick clean is ticked for you: press Clean, check the list, and it's done."
                        : "Scan done.  Click a row to see what it is.  Tick the box to include it.";
                    if (OpenPanelFor != null) { var oc = cats.FirstOrDefault(x => x.Id == OpenPanelFor); if (oc != null) { if (TestAge >= 0) oc.AgeMinutes = TestAge; tiers.Current = oc.Level; list.Tier = oc.Level; list.Relayout(); list.Selected = oc; drawer.Open(oc, true); list.OpenItem = oc; if (PickPaths != null || KeepPlaceOut != null || TestAge >= 0) drawer.Panel.ShowFiles();
                        if (KeepPlaceOut != null)   // test hook: a re-measure must leave the panel where it was
                        {
                            var kc = oc; var kt = new System.Windows.Forms.Timer { Interval = 1500 };
                            kt.Tick += (s9, e9) => { kt.Stop(); int before = drawer.Panel.ScrollTop; drawer.Panel.RefreshStats(kc); int after = drawer.Panel.ScrollTop;
                                File.WriteAllText(KeepPlaceOut, before + " " + after); Environment.Exit(0); };
                            kt.Start();
                        } } OpenPanelFor = null; }
                    if (GuideAt >= 0 && SnapshotPath != null)   // test hook
                    {
                        var sp = SnapshotPath; SnapshotPath = null; StartGuide(GuideAt);
                        var gt = new System.Windows.Forms.Timer { Interval = 700 };
                        gt.Tick += (s8, e8) => { gt.Stop(); Program.Snap(this, sp); Close(); };
                        gt.Start();
                    }
                    if (SnapshotPath == null && !testRun && Stats.Asked) Stats.MaybeSend(false);   // its own check: never in the guide's way
                    if (SnapshotPath != null) { var sp = SnapshotPath; SnapshotPath = null; BeginInvoke((Action)(() => { Program.Snap(this, sp); Close(); })); }
                    else if (!testRun && !Stats.Asked && guideSeenAtStart && !askShown && guide == null && !cleaning && !updateBar.Visible) { askShown = true; ShowAsk(); }
                    else if (!GuideOverlay.Seen && !testRun && guide == null && !cleaning)
                    {
                        if (ActiveForm == this) StartGuide(0);
                        else { EventHandler once = null; once = (s7, e7) => { Activated -= once; if (guide == null && !cleaning && !GuideOverlay.Seen) StartGuide(0); }; Activated += once; }   // wait until you're looking at it
                    }
                }));
            });
            t.IsBackground = true; t.Start();
        }

        void Clean()
        {
            // the Recycle Bin item always runs first, so files moved to the bin in this clean are never emptied by it
            var originals = cats.Where(c => c.Checked && (c.Blocked || c.Bytes > 0 || c.Files > 0) || c.Partial && c.PickBytes > 0).OrderBy(c => c.Kind == Kind.RecycleBin ? 0 : 1).ToList();
            if (originals.Count == 0) return;
            // frozen copies: Reset or age changes mid-clean don't touch it; a picked item counts only what you picked
            var sel = originals.Select(c => { var s = c.Snapshot(); if (c.Partial) { s.Bytes = c.PickBytes; s.Files = c.PickFiles; s.Name = c.Name + " (your picks)"; } return s; }).ToList();
            long total = sel.Sum(c => c.Bytes);
            bool recycle;
            using (var cf = new ConfirmForm(sel))
            {
                if (cf.ShowDialog(this) != DialogResult.OK) return;
                recycle = cf.Recycle;
            }
            foreach (var c in sel) c.Recycle = recycle;   // the confirm window's choice, for this clean
            // the cleaning window doesn't block: you can keep looking around; Clean waits until this one is done
            cleaning = true; Refresh_();
            var pf = new ProgressForm(sel, total) { StartPosition = FormStartPosition.Manual };
            pf.Location = new Point(Left + (Width - pf.Width) / 2, Top + (Height - pf.Height) / 2);
            running = pf;
            var watch = new System.Windows.Forms.Timer { Interval = 400 };   // live progress here, even with the cleaning window minimised
            watch.Tick += (s, e) =>
            {
                if (running == null) { watch.Stop(); return; }
                status.Text = running.Done ? "Cleaning finished. Close its window to measure again."
                            : "Cleaning  ·  item " + running.ItemNow + " of " + running.ItemCount + "  ·  " + Engine.Fmt(running.FreedSoFar) + " freed" +
                              (running.BinnedSoFar > 0 ? " + " + Engine.Fmt(running.BinnedSoFar) + " to the Recycle Bin" : "") + " so far.  You can keep looking around.";
            };
            watch.Start();
            pf.FormClosed += (s, e) =>
            {
                foreach (var c in sel) c.Recycle = false;
                foreach (var c in originals) { c.Picks.Clear(); c.PickBytes = -1; }   // picks are for one clean
                cleaning = false; running = null; watch.Stop(); pf.Dispose();
                ReadDisk(); Enqueue(cats);
                if (!testRun) Stats.MaybeSend(true);   // the total now includes this clean (only if you said yes)
                status.Text = "Cleaning finished. The list is being measured again.";
            };
            pf.Show(this);
            status.Text = "Cleaning in its own window. You can keep looking around here; Clean is ready again when it's done.";
        }
    }
}

namespace SpaceHunter
{
    // What an item cleans: the files and folders in its folders, each with a tick box and, at the top, a small line
    // saying which folder it comes from. Unticked ones stay; clicking a folder opens it, and Back goes up (never above
    // the item's own folders). It follows the item's rules: only installer files in Downloads, no "memory" folders.
    class InsideList : Canvas
    {
        class Entry { public string Path, Name, Sub; public bool Dir, Timed; public long Size = -1; public DateTime Modified, Own, Newest = DateTime.MinValue, Oldest = DateTime.MaxValue; }   // a folder's size is -1 until measured
        int token;
        readonly List<Entry> rows = new List<Entry>();
        List<string> roots = new List<string>();
        HashSet<string> exts, exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool topOnly;
        public string Key, Here;     // Here: null = the top, all of the item's folders at once
        Category cat;                // ticked: everything here is cleaned unless unticked; unticked: only what you tick here
        public event EventHandler TickChanged;   // picks, Select all or Clear
        int hover = -1, more;
        const float Max = 60;
        public event EventHandler Changed, Resized;
        Font fName = Theme.CSans(9.75f), fSize = Theme.CSans(8.75f), fSub = Theme.CSans(8.25f), fUp = Theme.CSemi(9.5f);

        public InsideList() { BackColor = Theme.Pane; }

        float RowH { get { return Here == null ? 42 : 30; } }   // at the top, each row also names the folder it's in
        const float BarH = 30;   // Select all / Clear
        RectangleF allBox, clearBox, openBox;

        public void Open(Category c)
        {
            bool same = Key == c.Id;
            Key = c.Id; cat = c;
            roots = Engine.ExistingRoots(c).Select(r => r.TrimEnd('\\')).ToList();
            exts = c.Extensions; topOnly = c.TopLevelOnly;
            exclude = new HashSet<string>(c.ExcludeDirs ?? new string[0], StringComparer.OrdinalIgnoreCase);
            Go(same && Here != null && roots.Any(r => Under(Here, r)) ? Here : null);
        }

        static bool Under(string p, string root) { return p.Equals(root, StringComparison.OrdinalIgnoreCase) || p.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase); }
        string RootOf(string p) { return roots.FirstOrDefault(r => Under(p, r)); }

        void List(string dir, string sub)
        {
            try
            {
                var items = new DirectoryInfo(dir).GetFileSystemInfos().Where(i => (i.Attributes & FileAttributes.ReparsePoint) == 0).ToList();
                if (!topOnly)
                    foreach (var i in items.OfType<DirectoryInfo>().Where(d => !exclude.Contains(d.Name)).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
                        rows.Add(new Entry { Path = i.FullName, Name = i.Name, Dir = true, Size = -1, Sub = sub });
                foreach (var f in items.OfType<FileInfo>().Where(f => exts == null || exts.Contains(f.Extension)).OrderByDescending(x => { try { return x.Length; } catch { return 0L; } }))
                    { var t = Engine.AgeTime(f); rows.Add(new Entry { Path = f.FullName, Name = f.Name, Size = f.Length, Modified = t, Newest = t, Oldest = t, Timed = true, Sub = sub }); }
            }
            catch { }
        }

        void Go(string dir)
        {
            Here = dir; rows.Clear(); hover = -1; more = 0;
            if (dir == null) foreach (var r in roots) List(r, "in " + Theme.MidPath(r, 52));
            else List(dir, null);
            if (rows.Count > Max) { more = rows.Count - (int)Max; rows.RemoveRange((int)Max, more); }
            Height = Theme.D((int)(BarH + RowH * rows.Count + 30 * ((Here != null ? 1 : 0) + (more > 0 ? 1 : 0) + (rows.Count == 0 ? 1 : 0)) + 4));
            Invalidate();
            // each folder's size, measured in the background and filled in as it's known
            int my = ++token; var dirs = rows.Where(r => r.Dir).ToList();
            new System.Threading.Thread(() =>
            {
                foreach (var r in dirs)
                {
                    if (my != token) return;
                    // its size, and its newest and oldest file, so the age choice can tick it (all old), dash it (some) or leave it
                    long sum = 0; var st = new Stack<DirectoryInfo>(); st.Push(new DirectoryInfo(r.Path));
                    DateTime newest = DateTime.MinValue, oldest = DateTime.MaxValue;
                    DateTime own = DateTime.MinValue; try { own = Engine.AgeTime(new DirectoryInfo(r.Path)); } catch { }   // its own date: counts for a whole subfolder, or when it has no files
                    while (st.Count > 0 && my == token)
                    {
                        FileSystemInfo[] items; try { items = st.Pop().GetFileSystemInfos(); } catch { continue; }
                        foreach (var i in items)
                        {
                            if ((i.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                            var d = i as DirectoryInfo; if (d != null) { st.Push(d); continue; }
                            try { sum += ((FileInfo)i).Length; var t = Engine.AgeTime(i); if (t > newest) newest = t; if (t < oldest) oldest = t; } catch { }
                        }
                    }
                    r.Size = sum; r.Newest = newest; r.Oldest = oldest; r.Own = own; r.Timed = my == token;
                    if (r.Timed) lock (tops) tops[r.Path] = newest > own ? newest : own;
                    try { BeginInvoke((Action)Invalidate); } catch { return; }
                }
            }) { IsBackground = true }.Start();
            if (Resized != null) Resized(this, EventArgs.Empty);
        }

        float Top0 { get { return BarH + (Here != null ? 30 : 0); } }
        readonly Dictionary<string, DateTime> tops = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);   // a folder's newest file, once measured

        // What the age choice does to a row in a ticked item: 2 it goes, 1 some of it goes (newer files stay), 0 it stays.
        // Follows the age as you change it. Picks in an unticked item go whatever their age.
        int AgeState(Entry r)
        {
            int age = cat != null && cat.AgeChoice ? cat.AgeMinutes : 0;
            if (!Whole || age <= 0) return 2;
            var cut = DateTime.Now.AddMinutes(-age);
            if (cat.Kind == Kind.Subfolders)   // a whole subfolder goes only when nothing in it is newer
            {
                string root = RootOf(r.Path); if (root == null) return 2;
                string rel = r.Path.Substring(root.Length).TrimStart('\\'); int cut1 = rel.IndexOf('\\');
                string top = cut1 < 0 ? r.Path : root + "\\" + rel.Substring(0, cut1);
                if (top.Equals(r.Path, StringComparison.OrdinalIgnoreCase))
                    return !r.Dir ? (r.Modified < cut ? 2 : 0) : !r.Timed ? 2 : (r.Newest > r.Own ? r.Newest : r.Own) < cut ? 2 : 0;
                DateTime t; lock (tops) if (!tops.TryGetValue(top, out t)) return 2;
                return t < cut ? 2 : 0;
            }
            if (!r.Dir) return r.Modified < cut ? 2 : 0;
            if (!r.Timed) return 2;   // not measured yet
            if (r.Oldest == DateTime.MaxValue) return r.Own < cut ? 2 : 0;   // no files in it: its own date decides
            return r.Newest < cut ? 2 : r.Oldest >= cut ? 0 : 1;
        }
        bool Whole { get { return cat == null || cat.Checked; } }
        // a folder around it is picked (so it's picked too, and can't be unpicked on its own)
        bool InheritedPick(string p) { return cat != null && cat.Picks.Any(q => p.StartsWith(q + "\\", StringComparison.OrdinalIgnoreCase)); }
        bool Locked(string p) { return InheritedSkip(p) || !Whole && (InheritedPick(p) || Custom.IsSkipped(p, Custom.Skip)); }
        bool On(string p) { return Whole ? !(InheritedSkip(p) || Custom.IsSkipped(p, Custom.Skip)) : cat.Picks.Contains(p) || InheritedPick(p); }
        int At(float y) { int i = (int)((y - Top0) / RowH); return y >= Top0 && i >= 0 && i < rows.Count ? i : -1; }
        bool InheritedSkip(string p)
        {
            string parent = System.IO.Path.GetDirectoryName(p);
            return !roots.Any(r => string.Equals(r, parent, StringComparison.OrdinalIgnoreCase)) && Custom.IsSkipped(parent, Custom.Skip);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            float x = X(e), y = Y(e); int h = Here != null && y >= BarH && y < BarH + 30 && !openBox.Contains(x, y) ? -2 : At(y);
            bool link = allBox.Contains(x, y) || clearBox.Contains(x, y) || openBox.Contains(x, y);
            if (h != hover || link) { hover = h; Cursor = link || h == -2 || (h >= 0 && (x < 30 || rows[h].Dir)) ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            float x = X(e), y = Y(e);
            if (cat != null && (allBox.Contains(x, y) || clearBox.Contains(x, y)))
            {
                bool all = allBox.Contains(x, y);
                if (all)   // the whole item, with nothing held back here
                    foreach (var sp in Custom.Skip.Where(p => roots.Any(r => Under(p, r))).ToList()) Custom.SetSkipped(sp, false);
                cat.Checked = all; cat.Picks.Clear();
                Invalidate(); if (TickChanged != null) TickChanged(this, EventArgs.Empty);
                return;
            }
            if (Here != null && openBox.Contains(x, y)) { try { System.Diagnostics.Process.Start("explorer.exe", "\"" + Here + "\""); } catch { } return; }
            if (Here != null && y >= BarH && y < BarH + 30)   // back: up a folder, or to the top from one of the item's own folders
            {
                Go(roots.Any(r => string.Equals(r, Here, StringComparison.OrdinalIgnoreCase)) ? null : System.IO.Path.GetDirectoryName(Here));
                return;
            }
            int i = At(y); if (i < 0) return;
            var row = rows[i];
            if (x < 30)
            {
                if (Locked(row.Path)) return;   // the folder around it decides
                if (AgeState(row) == 0 && On(row.Path)) return;   // the age decides: change the age above to include it
                if (Whole)   // unticking in a ticked item: always keep it
                {
                    Custom.SetSkipped(row.Path, !Custom.IsSkipped(row.Path, Custom.Skip));
                    Invalidate(); if (Changed != null) Changed(this, EventArgs.Empty);
                    return;
                }
                // ticking in an unticked item: clean just this, in the next clean
                if (!cat.Picks.Remove(row.Path))
                {
                    cat.Picks.RemoveWhere(q => q.StartsWith(row.Path + "\\", StringComparison.OrdinalIgnoreCase));   // now covered by the folder
                    cat.Picks.Add(row.Path);
                }
                Invalidate(); if (TickChanged != null) TickChanged(this, EventArgs.Empty);
                return;
            }
            if (row.Dir) Go(row.Path);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Begin(e); g.Clear(Theme.Pane);
            float w = LW, y = 0, rh = RowH;
            var nowrap = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            // the bar: what is picked, and Select all / Clear at the right
            {
                int ageNow = cat != null && cat.AgeChoice ? cat.AgeMinutes : 0;
                string state = cat == null ? "" : Whole ? (ageNow > 0 ? "Only files older than " + Engine.FmtAge(ageNow) : "All of it, except anything unticked") : cat.Partial ? cat.Picks.Count + " picked" + (cat.PickBytes >= 0 ? "  \u00b7  " + cat.PickFiles.ToString("N0") + (cat.PickFiles == 1 ? " file" : " files") + "  \u00b7  " + Engine.Fmt(cat.PickBytes) : "") : "Nothing picked yet";
                using (var b = new SolidBrush(cat != null && cat.Partial ? Theme.OrangeDeep : Theme.Muted)) g.DrawString(state, fUp, b, new RectangleF(0, 6, w - 150, 20), nowrap);
                var cs = g.MeasureString("Clear", fUp); var al = g.MeasureString("Select all", fUp);
                clearBox = new RectangleF(w - cs.Width - 2, 4, cs.Width + 2, 22); allBox = new RectangleF(clearBox.X - al.Width - 16, 4, al.Width + 2, 22);
                using (var b = new SolidBrush(Theme.Ink)) { g.DrawString("Select all", fUp, b, allBox.X + 1, 6); g.DrawString("Clear", fUp, b, clearBox.X + 1, 6); }
                using (var pen = new Pen(Theme.Ink)) { g.DrawLine(pen, allBox.X + 2, 25, allBox.Right - 2, 25); g.DrawLine(pen, clearBox.X + 2, 25, clearBox.Right - 2, 25); }
                using (var pen = new Pen(Theme.Line)) g.DrawLine(pen, 0, BarH - 1, w, BarH - 1);
                y = BarH;
            }
            openBox = RectangleF.Empty;
            if (Here != null)
            {
                var os = g.MeasureString("Open in Explorer", fSize);
                openBox = new RectangleF(w - os.Width - 4, y + 4, os.Width + 4, 22);
                if (hover == -2) using (var b = new SolidBrush(Theme.Hover)) g.FillRectangle(b, 0, y, openBox.X - 6, 29);
                using (var pen = new Pen(Theme.Ink, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLines(pen, new[] { new PointF(14, y + 9), new PointF(9, y + 14.5f), new PointF(14, y + 20) });
                string root = RootOf(Here) ?? Here;
                string rel = Here.Substring(root.Length).TrimStart('\\').Replace("\\", "  \u203a  ");
                string trail = System.IO.Path.GetFileName(root) + (rel.Length > 0 ? "  \u203a  " + rel : "");
                using (var b = new SolidBrush(Theme.Ink)) g.DrawString("Back  \u00b7  " + trail, fUp, b, new RectangleF(24, y + 6, openBox.X - 34, 20), nowrap);
                using (var b = new SolidBrush(Theme.Ink)) g.DrawString("Open in Explorer", fSize, b, openBox.X + 2, y + 7);
                using (var pen = new Pen(Theme.Soft)) g.DrawLine(pen, openBox.X + 3, y + 24, openBox.Right - 2, y + 24);
                using (var pen = new Pen(Theme.Line)) g.DrawLine(pen, 0, y + 29, w, y + 29);
                y += 30;
            }
            if (rows.Count == 0) using (var b = new SolidBrush(Theme.Soft)) g.DrawString(roots.Count == 0 ? "Nothing to show: its folders aren't on this PC, or Windows only lets an administrator look inside." : "Empty", fName, b, new RectangleF(30, y + 6, w - 30, 20), nowrap);
            for (int i = 0; i < rows.Count; i++, y += rh)
            {
                var r = rows[i];
                int ast = AgeState(r);
                bool on = On(r.Path), locked = Locked(r.Path) || on && ast == 0;
                if (on && ast == 0) on = false;   // too new for the age: stays, shown unticked
                if (i == hover) using (var b = new SolidBrush(Theme.Hover)) g.FillRectangle(b, 0, y, w, rh - 1);
                var box = new RectangleF(6, y + 7, 15, 15);
                if (on)
                {
                    using (var p = Theme.Round(box, 2)) using (var b = new SolidBrush(locked ? Theme.Line : Theme.Orange)) g.FillPath(b, p);
                    if (ast == 1 && !locked) using (var b = new SolidBrush(Theme.OnOrange)) g.FillRectangle(b, box.X + 3.5f, box.Y + 6.5f, 8, 2f);   // some of it
                    else using (var pen = new Pen(Theme.OnOrange, 2f)) g.DrawLines(pen, new[] { new PointF(box.X + 3.5f, box.Y + 7.5f), new PointF(box.X + 6.5f, box.Y + 10.5f), new PointF(box.X + 11.5f, box.Y + 4.5f) });
                }
                else using (var p = Theme.Round(box, 2)) using (var pen = new Pen(locked ? Theme.Line : Theme.Ink, 1.5f)) g.DrawPath(pen, p);
                bool kept = Custom.IsSkipped(r.Path, Custom.Skip) || InheritedSkip(r.Path);   // unticked by you: the age doesn't matter
                if (kept) ast = 2;
                bool tooNew = ast == 0;
                string size = r.Dir ? (tooNew ? "too new, stays" : (r.Size >= 0 ? Engine.Fmt(r.Size) + "  \u00b7  " : "") + (ast == 1 ? "newer files stay" : "folder"))
                                    : tooNew ? "too new, stays" : r.Size < 1024 ? "under 1 KB" : Engine.Fmt(r.Size); var sm = g.MeasureString(size, fSize);
                using (var b = new SolidBrush(Theme.Ink))
                    g.DrawString(r.Name, fName, b, new RectangleF(30, y + 6, w - 30 - sm.Width - (r.Dir ? 26 : 10), 20), nowrap);
                if (r.Sub != null)   // which folder it comes from
                    using (var b = new SolidBrush(Theme.Soft)) g.DrawString(r.Sub, fSub, b, new RectangleF(30, y + 23, w - 40, 16), nowrap);
                using (var b = new SolidBrush(Theme.Soft)) g.DrawString(size, fSize, b, w - sm.Width - (r.Dir ? 20 : 4), y + 8);
                if (r.Dir)
                    using (var pen = new Pen(i == hover ? Theme.Ink : Theme.Soft, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    { float cx = w - 8, cy = y + 15; g.DrawLines(pen, new[] { new PointF(cx - 3, cy - 5), new PointF(cx + 2, cy), new PointF(cx - 3, cy + 5) }); }
                using (var pen = new Pen(Theme.Line)) g.DrawLine(pen, 30, y + rh - 1, w, y + rh - 1);
            }
            if (more > 0) using (var b = new SolidBrush(Theme.Soft)) g.DrawString("+ " + more + " more (open the folder in Explorer to see them all)", fSize, b, 30, y + 7);
        }
    }
}

namespace SpaceHunter
{
    // What's in a folder, in plain words: the kinds of files, how old they are, and what the folder looks like.
    static class FolderSummary
    {
        static readonly KeyValuePair<string, string[]>[] Kinds = {
            new KeyValuePair<string, string[]>("videos", new[] { ".mp4", ".mov", ".mkv", ".avi", ".wmv", ".webm", ".m4v", ".mts" }),
            new KeyValuePair<string, string[]>("photos and images", new[] { ".jpg", ".jpeg", ".png", ".gif", ".heic", ".webp", ".bmp", ".tif", ".tiff", ".raw", ".cr2", ".nef", ".arw", ".dng", ".psd", ".svg" }),
            new KeyValuePair<string, string[]>("music and audio", new[] { ".mp3", ".wav", ".flac", ".aac", ".m4a", ".ogg", ".wma" }),
            new KeyValuePair<string, string[]>("documents", new[] { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".md", ".odt", ".csv", ".rtf", ".pages", ".key", ".numbers" }),
            new KeyValuePair<string, string[]>("3D and CAD files", new[] { ".stl", ".3mf", ".obj", ".fbx", ".blend", ".step", ".stp", ".iges", ".igs", ".sldprt", ".sldasm", ".slddrw", ".dwg", ".dxf", ".f3d", ".ipt", ".iam", ".gcode" }),
            new KeyValuePair<string, string[]>("archives and installers", new[] { ".zip", ".rar", ".7z", ".tar", ".gz", ".iso", ".exe", ".msi", ".dmg", ".pkg", ".appimage", ".deb" }),
            new KeyValuePair<string, string[]>("code", new[] { ".cs", ".js", ".ts", ".tsx", ".jsx", ".py", ".java", ".c", ".cpp", ".h", ".go", ".rs", ".swift", ".kt", ".html", ".css", ".json", ".ipynb", ".m" }),
        };

        public static string Describe(string root)
        {
            if (File.Exists(root))
            {
                var fi = new FileInfo(root); string e = fi.Extension.TrimStart('.').ToUpperInvariant();
                return "A single file: " + (e.Length > 0 ? "a " + e + " file of " : "") + Engine.Fmt(fi.Length) + ", last changed " + Ago(fi.LastWriteTime) + ".";
            }
            var kindOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in Kinds) foreach (var e in k.Value) kindOf[e] = k.Key;
            var bytes = new Dictionary<string, long>(); var files = new Dictionary<string, long>();
            long total = 0, count = 0, old30 = 0, old365 = 0; DateTime newest = DateTime.MinValue;
            var signs = new List<string>(); bool capped = false;
            var skip = Custom.Skip;
            var stack = new Stack<DirectoryInfo>(); stack.Push(new DirectoryInfo(root));
            while (stack.Count > 0)
            {
                var d = stack.Pop();
                FileSystemInfo[] items; try { items = d.GetFileSystemInfos(); } catch { continue; }
                foreach (var i in items)
                {
                    if ((i.Attributes & FileAttributes.ReparsePoint) != 0 || Keep.IsKept(i.FullName) || Custom.IsSkipped(i.FullName, skip)) continue;
                    var sub = i as DirectoryInfo;
                    if (sub != null)
                    {
                        string n = sub.Name.ToLowerInvariant();
                        if (n == ".git" && !signs.Contains("a code project (its history is in a .git folder)")) signs.Add("a code project (its history is in a .git folder)");
                        if (n == "node_modules" && !signs.Contains("JavaScript packages (node_modules) that reinstall with npm install")) signs.Add("JavaScript packages (node_modules) that reinstall with npm install");
                        if ((n == "cache" || n == "caches" || n == ".cache") && !signs.Contains("cache folders that apps rebuild")) signs.Add("cache folders that apps rebuild");
                        if ((n == "venv" || n == ".venv") && !signs.Contains("a Python environment (venv) that can be recreated")) signs.Add("a Python environment (venv) that can be recreated");
                        if ((n == "bin" || n == "obj" || n == "build" || n == "dist" || n == "target") && !signs.Contains("build output that is made again on the next build")) signs.Add("build output that is made again on the next build");
                        stack.Push(sub); continue;
                    }
                    var f = (FileInfo)i; long len; DateTime t;
                    try { len = f.Length; t = Engine.AgeTime(f); } catch { continue; }
                    string kind; if (!kindOf.TryGetValue(f.Extension, out kind)) kind = "other files";
                    long v; bytes.TryGetValue(kind, out v); bytes[kind] = v + len; files.TryGetValue(kind, out v); files[kind] = v + 1;
                    total += len; count++;
                    if (t > newest) newest = t;
                    var age = DateTime.Now - t; if (age.TotalDays > 30) old30 += len; if (age.TotalDays > 365) old365 += len;
                    if (count >= 400000) { capped = true; break; }
                }
                if (capped) break;
            }
            if (count == 0) return "It's empty right now" + (skip.Count > 0 ? ", apart from what you unticked." : ".");
            var sb = new System.Text.StringBuilder();
            sb.Append(Engine.Fmt(total) + " in " + count.ToString("N0") + " files" + (capped ? " (counted the first 400,000)" : "") + ". ");
            var top = bytes.OrderByDescending(x => x.Value).Take(3).ToList();
            sb.Append(top[0].Value * 100 / Math.Max(1, total) >= 60 ? "Mostly " : "Mainly ");
            sb.Append(string.Join(", ", top.Select(x => x.Key + " (" + Engine.Fmt(x.Value) + ", " + files[x.Key].ToString("N0") + ")")) + ". ");
            if (newest > DateTime.MinValue) sb.Append("Last changed " + Ago(newest) + ". ");
            if (old30 > 0) sb.Append(Engine.Fmt(old30) + " hasn't been touched for over a month" + (old365 > 0 ? ", " + Engine.Fmt(old365) + " for over a year" : "") + ". ");
            if (signs.Count > 0) sb.Append("It has " + string.Join("; ", signs) + ".");
            return sb.ToString().Trim();
        }

        static string Ago(DateTime t)
        {
            var d = DateTime.Now - t;
            if (d.TotalHours < 1) return "within the last hour";
            if (d.TotalDays < 1) return (int)d.TotalHours + " hours ago";
            if (d.TotalDays < 60) return (int)d.TotalDays + " days ago";
            if (d.TotalDays < 730) return (int)(d.TotalDays / 30) + " months ago";
            return (int)(d.TotalDays / 365) + " years ago";
        }
    }
}
