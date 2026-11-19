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

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Begin(e); float W = LW, H = LH;
            g.Clear(Theme.Chassis);
            using (var p = Theme.Round(new RectangleF(0.5f, 0.5f, W - 1, H - 1), 7)) using (var b = new SolidBrush(Theme.Screen)) g.FillPath(b, p);
            using (var pen = new Pen(Color.Black, 2)) g.DrawLine(pen, 7, 2, W - 7, 2);   // inset lip
            float pad = 22, right = W - pad;
            using (var b = new SolidBrush(Color.FromArgb(0xB4, 0xB1, 0xAA)))
            {
                g.DrawString(Scanning ? "Scanning ..." : "Space to free up", fLabel, b, pad, 13);
                var dl = "Disk " + Path.GetPathRoot(Engine.W).TrimEnd('\\'); var sz = g.MeasureString(dl, fLabel);
                g.DrawString(dl, fLabel, b, right - sz.Width, 13);
            }
            string num, unit; Theme.SizeParts(Selected, out num, out unit);
            using (var b = new SolidBrush(Theme.Orange))
            {
                var ns = g.MeasureString(num, fBig);
                g.DrawString(num, fBig, b, pad - 6, 30);
                g.DrawString(unit, fUnit, b, pad - 6 + ns.Width - 8, 30 + ns.Height - g.MeasureString(unit, fUnit).Height - 13);
                // beside the number: what kind of space it is, and what else is there if you look
                if (!Scanning && Selected > 0)
                {
                    float tx = pad - 6 + ns.Width - 8 + g.MeasureString(unit, fUnit).Width + 14;
                    using (var ib = new SolidBrush(Theme.DisplayInk)) g.DrawString(OnlySafe ? (FreedAll == 0 ? "all safe to clean  ·  one click on Clean" : "all safe to clean") : "includes items to check first", fLegend, ib, tx, 44);
                    if (ReviewUnticked > 0)
                        using (var mb = new SolidBrush(Theme.DisplayMuted)) g.DrawString("+ " + Engine.Fmt(ReviewUnticked) + " more you can check yourself", fLegend, mb, tx, 64);
                }
            }
            string disk = Total > 0 ? Engine.Fmt(Free) + " free of " + Engine.Fmt(Total) : "";
            using (var b = new SolidBrush(Color.FromArgb(0xB4, 0xB1, 0xAA))) { var ds = g.MeasureString(disk, fDisk); g.DrawString(disk, fDisk, b, right - ds.Width, 35); }

            // history: free space over time + everything freed so far
            if (History.Count >= 2)
            {
                var pts = History.Skip(Math.Max(0, History.Count - 60)).ToList();
                // short windows (125% and 150% scaling) get a shorter line, with its label beside it
                float sw = Compact ? 120 : 170, sh = Compact ? 16 : 26, sx = right - sw, sy = Compact ? 80 : 66;
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
                    else g.DrawString(hl, fHist, b, right - hs.Width, sy + sh + 3);
                }
            }
            if (FreedAll > 0)
                using (var b = new SolidBrush(Theme.DisplayMuted))
                {
                    var fl = "Freed so far: " + Engine.Fmt(FreedAll) + " in " + Cleans + (Cleans == 1 ? " clean" : " cleans");
                    var fs = g.MeasureString(fl, fSmall); g.DrawString(fl, fSmall, b, right - fs.Width, Compact ? 58 : 116);
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
            var box = new RectangleF(x, y + 3, 13, 13);
            if (hatch) Theme.Hatch(g, box, c, Theme.Screen);
            else using (var b = new SolidBrush(c)) g.FillRectangle(b, box);
            if (c == Theme.DisplayTrack) using (var p = new Pen(Theme.DisplayMuted)) g.DrawRectangle(p, box.X, box.Y, box.Width, box.Height);
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
        int hover = -1;
        Font fName = Theme.CBold(11.5f), fDesc = Theme.CSans(9f), fSize = Theme.CSemi(13f), fSub = Theme.CSans(8.75f), fSubB = Theme.CSemi(8.75f);
        static readonly Level[] Order = { Level.Safe, Level.Review, Level.Caution };

        public TierBar() { Cursor = Cursors.Hand; TabStop = false; }

        RectangleF Tile(int i) { float w = (LW - 2 * 18 - 2 * 10) / 3f; return new RectangleF(18 + i * (w + 10), 6, w, LH - 14); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var pt = new PointF(X(e), Y(e)); int h = -1;
            for (int i = 0; i < 3; i++) if (Tile(i).Contains(pt)) h = i;
            if (h != hover) { hover = h; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            var pt = new PointF(X(e), Y(e));
            for (int i = 0; i < 3; i++)
                if (Tile(i).Contains(pt) && Current != Order[i]) { Current = Order[i]; Invalidate(); if (Changed != null) Changed(this, EventArgs.Empty); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Begin(e); g.Clear(Theme.Chassis);
            for (int i = 0; i < 3; i++)
            {
                var lvl = Order[i]; var r = Tile(i); bool on = lvl == Current;
                var items = Cats.Where(c => c.Level == lvl).ToList();
                long total = items.Sum(c => c.Bytes), ticked = items.Where(c => c.Checked).Sum(c => c.Bytes);
                int nTicked = items.Count(c => c.Checked && (c.Blocked || c.Bytes > 0 || c.Files > 0));
                Color fill = on ? Theme.Ink : (hover == i ? ControlPaint.Light(Theme.Key, 0.25f) : Theme.Key);
                Color ink = on ? Theme.DisplayInk : Theme.Ink, soft = on ? Theme.DisplayMuted : Theme.Soft;
                using (var p = Theme.Round(new RectangleF(r.X, r.Y + 3, r.Width, r.Height - 3), 6)) using (var b = new SolidBrush(on ? Color.Black : Theme.KeyShadow)) g.FillPath(b, p);
                using (var p = Theme.Round(new RectangleF(r.X, r.Y, r.Width, r.Height - 3), 6)) using (var b = new SolidBrush(fill)) g.FillPath(b, p);
                float x = r.X + 16;
                if (lvl != Level.Safe) { Theme.LevelMark(g, new RectangleF(r.X + 10, r.Y + 12, 5, r.Height - 27), lvl, ink, fill); x = r.X + 24; }
                string name = Theme.TierName(lvl);
                using (var b = new SolidBrush(ink)) g.DrawString(name, fName, b, x, r.Y + 10);
                bool roomy = r.Height >= 80;
                using (var b = new SolidBrush(soft)) g.DrawString(Theme.TierDesc(lvl), fDesc, b, x, r.Y + (roomy ? 33 : 30));
                string size = Engine.Fmt(total); var ss = g.MeasureString(size, fSize);
                using (var b = new SolidBrush(ink)) g.DrawString(size, fSize, b, r.Right - 14 - ss.Width, r.Y + 9);
                // how much of it is ticked, at a glance: a small box (filled when anything is ticked) and "3 of 10 ticked"
                int nItems = items.Count(c => c.Blocked || c.Bytes > 0 || c.Files > 0);
                float cy = roomy ? r.Bottom - 31 : r.Bottom - 24;
                var box = new RectangleF(x, cy + 3, 11, 11);
                if (nTicked > 0)
                {
                    using (var b = new SolidBrush(Theme.Orange)) g.FillRectangle(b, box);
                    using (var pen = new Pen(on ? Theme.Ink : Theme.Ink, 1.6f)) g.DrawLines(pen, new[] { new PointF(box.X + 2.5f, box.Y + 5.5f), new PointF(box.X + 4.8f, box.Y + 8), new PointF(box.X + 8.8f, box.Y + 3) });
                }
                else using (var pen = new Pen(soft, 1.3f)) g.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height);
                string sub = nItems == 0 ? "Nothing to clean here right now"
                           : nTicked == 0 ? "None of " + nItems + " ticked"
                           : nTicked + " of " + nItems + " ticked  ·  " + Engine.Fmt(ticked);
                using (var b = new SolidBrush(nTicked > 0 ? ink : soft)) g.DrawString(sub, nTicked > 0 ? fSubB : fSub, b, x + 17, cy);
            }
        }
    }

    // The items of one section. One strong checkbox per row, a "what happens" line, the size, and a chevron:
    // the whole row opens the item's panel; only the box ticks it.
    class CategoryList : Canvas
    {
        class Row { public float Y, H; public Category C; public string Note; public bool Tools; }
        readonly List<Row> rows = new List<Row>();
        List<Category> cats = new List<Category>();
        public Level Tier = Level.Safe;
        float totalH, scroll, dragOffset; int hover = -1; bool dragThumb;
        const float RowH = 54, SmallH = 38, ToolsH = 80;
        public Category Selected, OpenItem;   // OpenItem: the row whose panel is showing
        public event EventHandler SelectionChanged, CheckedChanged, PanelRequested;
        Font fName = Theme.CSemi(10.5f), fNameSel = Theme.CBold(10.5f), fLine = Theme.CSans(9f), fSize = Theme.CSemi(11f),
             fFiles = Theme.CSans(8.5f), fTool = Theme.CSemi(9f), fTitle = Theme.CBold(10f), fNote = Theme.CMonoSemi(7.5f);
        RectangleF tickAll, tickNone;

        public CategoryList() { TabStop = true; }

        public void SetItems(List<Category> c) { cats = c; Relayout(); }

        public void Relayout()
        {
            rows.Clear(); float y = 0;
            rows.Add(new Row { Y = y, H = ToolsH, Tools = true }); y += ToolsH;
            var items = cats.Where(x => x.Level == Tier).ToList();
            var full = items.Where(x => !IsEmpty(x)).OrderByDescending(x => x.Bytes).ToList();
            var empty = items.Where(IsEmpty).ToList();
            foreach (var it in full) { rows.Add(new Row { Y = y, H = RowH, C = it }); y += RowH; }
            if (empty.Count > 0)
            {
                rows.Add(new Row { Y = y, H = 34, Note = "NOTHING TO CLEAR RIGHT NOW  ·  " + empty.Count }); y += 34;
                foreach (var it in empty) { rows.Add(new Row { Y = y, H = SmallH, C = it }); y += SmallH; }
            }
            totalH = y + 12;
            if (Selected == null || Selected.Level != Tier || !cats.Contains(Selected)) Selected = full.Concat(empty).FirstOrDefault();
            ScrollTo(scroll);
            Invalidate();
        }

        static bool IsEmpty(Category c) { return !c.Blocked && c.Scanned && c.Bytes == 0 && c.Files == 0; }

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

        void Toggle(Category c)
        {
            if (IsEmpty(c)) return;
            c.Checked = !c.Checked; Invalidate();
            if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
        }

        public void SetAll(bool on)
        {
            foreach (var c in cats.Where(x => x.Level == Tier && !IsEmpty(x))) c.Checked = on;
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
            float w = LW - 14;
            var nowrap = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            for (int ri = 0; ri < rows.Count; ri++)
            {
                var r = rows[ri];
                float top = r.Y - scroll;
                if (top > LH || top + r.H < 0) continue;
                if (r.Tools)
                {
                    string t = Theme.TierName(Tier).ToUpperInvariant() + "  ·  " + Theme.TierDesc(Tier);
                    using (var b = new SolidBrush(Theme.Ink)) g.DrawString(t, fTitle, b, 22, top + 14);
                    var a = "Untick all"; var n = "Tick all";
                    var aw = g.MeasureString(a, fTool); var nw = g.MeasureString(n, fTool);
                    tickNone = new RectangleF(w - aw.Width - 4, top + 13, aw.Width + 4, 22);
                    tickAll = new RectangleF(tickNone.X - nw.Width - 22, top + 13, nw.Width + 4, 22);
                    using (var b = new SolidBrush(Theme.Ink)) { g.DrawString(n, fTool, b, tickAll.X + 2, top + 14); g.DrawString(a, fTool, b, tickNone.X + 2, top + 14); }
                    using (var p = new Pen(Theme.Ink)) { g.DrawLine(p, tickAll.X + 2, top + 33, tickAll.Right - 2, top + 33); g.DrawLine(p, tickNone.X + 2, top + 33, tickNone.Right - 2, top + 33); }
                    // what this section is, in one plain line
                    using (var b = new SolidBrush(Theme.Muted)) g.DrawString(Theme.TierExplain(Tier), fLine, b, new RectangleF(22, top + 40, Math.Min(w - 40, 760), 36));
                    using (var p = new Pen(Theme.Line)) g.DrawLine(p, 22, top + r.H - 1, w, top + r.H - 1);
                    continue;
                }
                if (r.Note != null) { using (var b = new SolidBrush(Theme.Soft)) g.DrawString(r.Note, fNote, b, 22, top + 14); continue; }

                var c = r.C;
                bool empty = IsEmpty(c), small = r.H == SmallH;
                if (c == Selected || hover == ri) using (var br = new SolidBrush(c == Selected ? Theme.Selected : Theme.Hover)) g.FillRectangle(br, 10, top, w - 6, r.H - 1);
                using (var p = new Pen(Theme.Line)) g.DrawLine(p, 22, top + r.H - 1, w, top + r.H - 1);

                // checkbox: filled orange with a dark tick, an ink outline, or grey when there's nothing to clear
                var box = new RectangleF(24, top + (r.H - 17) / 2f, 17, 17);
                if (empty) using (var path = Theme.Round(box, 2)) using (var pen = new Pen(Theme.Line, 1.6f)) g.DrawPath(pen, path);
                else if (c.Checked)
                {
                    using (var path = Theme.Round(box, 2)) using (var br = new SolidBrush(Theme.Orange)) g.FillPath(br, path);
                    using (var pen = new Pen(Theme.Ink, 2.2f) { StartCap = LineCap.Square, EndCap = LineCap.Square })
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
                string size = c.Blocked ? "size after cleaning" : !c.Scanned ? "..." : c.Kind == Kind.RunHistory ? c.Files + (c.Files == 1 ? " entry" : " entries") : Engine.Fmt(c.Bytes);
                string files = !small && !c.Blocked && c.Scanned && c.Kind != Kind.RunHistory ? c.Files.ToString("N0") + (c.Files == 1 ? " file" : " files") : "";
                var sizeFont = c.Blocked ? fLine : fSize;
                var sz = g.MeasureString(size, sizeFont); var fz = g.MeasureString(files, fFiles);
                using (var br = new SolidBrush(empty || c.Blocked ? Theme.Soft : Theme.Ink)) g.DrawString(size, sizeFont, br, right - sz.Width, top + (c.Blocked ? 18 : small ? 9 : 9));
                using (var br = new SolidBrush(Theme.Soft)) g.DrawString(files, fFiles, br, right - fz.Width, top + 30);

                float tw = right - Math.Max(sz.Width, fz.Width) - 18 - x;
                var nameFont = c == Selected ? fNameSel : fName;
                if (small)
                {
                    using (var br = new SolidBrush(Theme.Soft)) g.DrawString(c.Name, fLine, br, new RectangleF(x, top + 10, tw, 20), nowrap);
                    continue;
                }
                using (var br = new SolidBrush(Theme.Ink)) g.DrawString(c.Name, nameFont, br, new RectangleF(x, top + 8, tw, 22), nowrap);
                string sub = c.Blocked ? "A Windows system folder" :
                             c.Short + (c.Kind == Kind.Contents || c.Kind == Kind.Subfolders ? (c.AgeMinutes > 0 ? "  ·  older than " + Engine.FmtAge(c.AgeMinutes) : "  ·  everything") : "");
                using (var br = new SolidBrush(Theme.Soft)) g.DrawString(sub, fLine, br, new RectangleF(x, top + 30, tw, 18), nowrap);
            }
            var th = Thumb();
            if (!th.IsEmpty) using (var p = Theme.Round(th, 2)) using (var br = new SolidBrush(Color.FromArgb(120, Theme.Ink))) g.FillPath(br, p);
        }
    }

    // The panel for one item: what to clean (older-than or everything), the notes, and its folders with a keep switch.
    // How things are cleaned (delete or Recycle Bin) is chosen once, in the confirm window.
    class DetailPanel : Panel, IMessageFilter
    {
        public event EventHandler AgeChanged, KeepChanged, CloseRequested, RemoveFromHome;
        Category cat;
        readonly Panel clip = new Panel(), thumb = new Panel();
        readonly FlowLayoutPanel flow = new FlowLayoutPanel();
        Label tag, title, stats, what, deletes, safe, after, ageHint, ageValue, foldersHead, whatHead;
        Key olderKey, allKey, minus, plus, uMin, uHrs, uDays, closeKey, removeKey;
        FlowLayoutPanel modeRow, ageRow, folders, removeRow;
        Label insideHead, insideHint; InsideList inside;
        int value = 14, unit = 2;   // unit: 0 minutes, 1 hours, 2 days
        bool loading;

        public DetailPanel()
        {
            BackColor = Theme.Pane; DoubleBuffered = true;
            clip.Dock = DockStyle.Fill; clip.BackColor = Theme.Pane; Controls.Add(clip);
            flow.FlowDirection = FlowDirection.TopDown; flow.WrapContents = false;
            flow.AutoScroll = false; flow.AutoSize = true; flow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            flow.Padding = Theme.Pad(28, 22, 24, 30); flow.BackColor = Theme.Pane; flow.Location = Point.Empty;
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
            Resize += (s, e) => closeKey.Location = new Point(Width - closeKey.Width - Theme.D(22), Theme.D(18));

            tag = L(Theme.Semi(9.5f), Theme.Muted, 2);
            title = L(Theme.Bold(14f), Theme.Ink, 2); title.AutoSize = false; title.AutoEllipsis = true; title.Height = Theme.D(30);
            stats = L(Theme.Sans(10.5f), Theme.Ink, 14);

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
            int at = flow.Controls.GetChildIndex(ageHint) + 1;
            flow.Controls.SetChildIndex(insideHead, at); flow.Controls.SetChildIndex(insideHint, at + 1); flow.Controls.SetChildIndex(inside, at + 2);
            removeRow = Row(8);
            removeKey = new Key("remove from cleanup list");
            removeKey.Click += (s, e) => { if (RemoveFromHome != null && cat != null) RemoveFromHome(cat, EventArgs.Empty); };
            removeRow.Controls.Add(removeKey);
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
        Label Head(string t) { var h = L(Theme.Semi(10f), Theme.Muted, 2); h.Text = t; return h; }
        Label Section(string heading) { Head(heading); return L(Theme.Sans(10.25f), Theme.Ink, 14); }

        void FitWidths()
        {
            int w = Math.Max(Theme.D(200), clip.ClientSize.Width - flow.Padding.Horizontal - Theme.D(4));
            foreach (Control c in flow.Controls) if (c is Label && c != title) c.MaximumSize = new Size(w, 0);
            title.Width = w; tag.MaximumSize = new Size(w - closeKey.Width - Theme.D(8), 0);
            inside.Width = w;
        }

        public Category Current { get { return cat; } }

        public void ShowCategory(Category c)
        {
            loading = true; cat = c;
            tag.Text = Theme.TierName(c.Level);
            title.Text = c.Name;
            stats.Text = !c.Scanned ? "Scanning..." : c.Kind == Kind.RunHistory ? c.Files + " entries" :
                         Engine.Fmt(c.Bytes) + "   ·   " + c.Files.ToString("N0") + " files";
            what.Text = c.What; deletes.Text = c.Deletes; safe.Text = c.SafeText; after.Text = c.After;

            bool files = c.Kind == Kind.Contents || c.Kind == Kind.Subfolders;
            whatHead.Visible = modeRow.Visible = ageRow.Visible = ageHint.Visible = files;
            removeRow.Visible = c.IsCustom;
            ShowAge();

            insideHead.Visible = insideHint.Visible = inside.Visible = c.IsCustom;
            if (c.IsCustom) Describe(c);
            if (c.IsCustom && (inside.Root == null || !string.Equals(inside.Root, c.Roots[0], StringComparison.OrdinalIgnoreCase))) inside.Open(c.Roots[0]);
            folders.Controls.Clear();
            var list = c.IsCustom ? new List<string>() : FolderList(c);
            foldersHead.Visible = folders.Visible = list.Count > 0;
            foreach (var path in list.Take(14))
            {
                var p = path;
                bool kept = Keep.IsKept(p);
                var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Theme.Pad(0, 2, 0, 2), BackColor = Theme.Pane };
                var k = new Key(kept ? "kept" : "keep") { Height = Theme.D(28), Toggled = kept, Enabled = !Keep.IsBuiltIn(p) };   // Space Hunter's own folder stays kept
                k.Font = Theme.Semi(8.5f);
                k.Click += (s, e) => { if (Keep.IsKept(p)) Keep.Remove(p); else Keep.Add(p); if (KeepChanged != null) KeepChanged(cat, EventArgs.Empty); ShowCategory(cat); };
                var ll = new LinkLabel { Text = Theme.MidPath(p, 44), AutoSize = true, Font = Theme.Sans(9.5f), LinkColor = kept ? Theme.Soft : Theme.Ink, ActiveLinkColor = Theme.OrangeDeep, VisitedLinkColor = Theme.Ink, Margin = Theme.Pad(8, 6, 0, 0), LinkBehavior = LinkBehavior.HoverUnderline, UseMnemonic = false };
                ll.LinkClicked += (s, e) => { try { Process.Start("explorer.exe", "\"" + p + "\""); } catch { } };
                row.Controls.Add(k); row.Controls.Add(ll);
                folders.Controls.Add(row);
            }
            if (list.Count > 14) folders.Controls.Add(new Label { Text = "+ " + (list.Count - 14) + " more", AutoSize = true, ForeColor = Theme.Soft, Font = Theme.Sans(9f) });
            FitWidths();
            flow.Top = 0; ScrollBy(0);
            loading = false;
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
            ageHint.Text = (older ? "Files untouched for " + Engine.FmtAge(cat.AgeMinutes) + " or more." : "Every file, whatever its age.") +
                           " Open files are always skipped. Delete or Recycle Bin is chosen when you press Clean.";
            foreach (var k in new Key[] { olderKey, allKey, uMin, uHrs, uDays }) k.Invalidate();
        }

        void SetAge(int minutes)
        {
            cat.AgeMinutes = minutes; if (minutes > 0) cat.LastAge = minutes;
            ShowAge();
            if (!loading && AgeChanged != null) AgeChanged(cat, EventArgs.Empty);
        }

        void AgeFromKeys()
        {
            int mult = unit == 2 ? Engine.Day : unit == 1 ? Engine.Hour : 1;
            SetAge(Math.Max(1, value) * mult);
        }

        public void RefreshStats(Category c) { if (c == cat) ShowCategory(c); }

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
        Key scanKey, cleanKey, finderKey, protectKey, resetKey; Label status;
        bool cleaning;   // a clean is running in its window
        readonly object qLock = new object();
        readonly Queue<Category> scanQueue = new Queue<Category>();
        bool workerRunning;
        readonly System.Windows.Forms.Timer ageDebounce = new System.Windows.Forms.Timer { Interval = 600 };
        Category pendingAge;
        public string SnapshotPath, OpenPanelFor, FramesDir;

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
            var top = new Panel { Dock = DockStyle.Top, Height = Theme.D(disp.DesignHeight + 18 + 64), Padding = Theme.Pad(18, 18, 18, 0), BackColor = Theme.Chassis };
            var keys = new Panel { Dock = DockStyle.Bottom, Height = Theme.D(64), BackColor = Theme.Chassis };
            scanKey = new Key("scan again"); finderKey = new Key("space map") { Accent = true }; protectKey = new Key("protected folders"); resetKey = new Key("reset to default");
            cleanKey = new Key("clean", true);
            cleanKey.Font = Theme.Semi(11.5f); cleanKey.Height = Theme.D(46); cleanKey.MinimumSize = Theme.D(240, 46); cleanKey.FitWidth();
            keys.Controls.AddRange(new Control[] { scanKey, finderKey, protectKey, resetKey, cleanKey });
            keys.Resize += (s, e) => PlaceKeys(keys);
            top.Controls.Add(keys); top.Controls.Add(disp);

            body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Chassis };
            tiers = new TierBar { Dock = DockStyle.Top, Height = Theme.D(disp.Compact ? 88 : 100) };
            list = new CategoryList { Dock = DockStyle.Fill };
            var listHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Chassis };
            listHost.Controls.Add(list);
            body.Controls.Add(listHost); body.Controls.Add(tiers);
            drawer = new Drawer(listHost);

            status = new Label { Dock = DockStyle.Bottom, Height = Theme.D(34), Padding = Theme.Pad(22, 10, 0, 0), ForeColor = Theme.Muted, Font = Theme.Sans(9f), BackColor = Theme.Chassis, UseMnemonic = false };
            divider = new Panel { Dock = DockStyle.Bottom, Height = Math.Max(1, Theme.D(1)), BackColor = Theme.Line };
            BuildUpdateBar();
            Controls.Add(body); Controls.Add(top); Controls.Add(updateBar); Controls.Add(divider); Controls.Add(status);

            Load_();
            tiers.Changed += (s, e) => { list.Tier = tiers.Current; list.Relayout(); if (drawer.IsOpen && list.Selected != null) drawer.Open(list.Selected); else drawer.Close(); list.OpenItem = drawer.IsOpen ? drawer.Panel.Current : null; };
            list.SelectionChanged += (s, e) => { if (drawer.IsOpen && list.Selected != null) { drawer.Open(list.Selected); list.OpenItem = list.Selected; } };
            list.PanelRequested += (s, e) => { var c = (Category)s; if (drawer.IsOpen && drawer.Panel.Current == c) drawer.Close(); else drawer.Open(c); list.OpenItem = drawer.IsOpen ? drawer.Panel.Current : null; list.Invalidate(); };
            list.CheckedChanged += (s, e) => { Settings.Save(cats); Refresh_(); };
            drawer.Panel.CloseRequested += (s, e) => { drawer.Close(); list.OpenItem = null; list.Invalidate(); };
            drawer.Panel.AgeChanged += (s, e) => { pendingAge = (Category)s; Settings.Save(cats); list.Invalidate(); ageDebounce.Stop(); ageDebounce.Start(); };
            drawer.Panel.KeepChanged += (s, e) => Enqueue(new[] { (Category)s });
            drawer.Panel.RemoveFromHome += (s, e) => { var c = (Category)s; foreach (var r in c.Roots) Custom.Remove(r); drawer.Close(); list.OpenItem = null; Load_(); Enqueue(cats); status.Text = "Removed from the cleanup list: " + c.Name; };
            ageDebounce.Tick += (s, e) => { ageDebounce.Stop(); if (pendingAge != null) Enqueue(new[] { pendingAge }); };
            scanKey.Click += (s, e) => Enqueue(cats);
            cleanKey.Click += (s, e) => Clean();
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
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape && drawer.IsOpen) { drawer.Close(); list.OpenItem = null; list.Invalidate(); e.Handled = true; } };
            Shown += (s, e) =>
            {
                list.Focus(); Enqueue(cats);
                if (SnapshotPath == null && FramesDir == null)   // one quiet check for a newer version; offline = nothing happens
                    new Thread(() => { var r = Updater.Check(); if (r != null) try { BeginInvoke((Action)(() => ShowUpdate(r))); } catch { } }) { IsBackground = true }.Start();
            };
            ReadDisk();
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
            status.Text = "Back to default: Quick clean items ticked, default ages. Protected folders stay protected.";
        }

        void PlaceKeys(Control keys)
        {
            // The Space map is a tool of its own, alone at the left. The cleaning keys sit together at the right:
            // scan again, protected folders and reset to default lead into the one big Clean key.
            int y = Theme.D(14);
            cleanKey.Location = new Point(keys.Width - cleanKey.Width - Theme.D(18), Theme.D(10));
            finderKey.Location = new Point(Theme.D(18), y);
            int x = cleanKey.Left - Theme.D(12);
            foreach (var k in new[] { resetKey, protectKey, scanKey }) { x -= k.Width; k.Location = new Point(x, y); x -= Theme.D(8); }
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
            disp.Selected = cats.Where(c => c.Checked).Sum(c => c.Bytes);
            disp.OnlySafe = !cats.Any(c => c.Checked && !c.Safe && (c.Bytes > 0 || c.Files > 0));
            disp.ReviewUnticked = cats.Where(c => !c.Checked && !c.Safe && c.Kind != Kind.RecycleBin).Sum(c => c.Bytes);
            disp.Scanning = busy;
            disp.Invalidate(); tiers.Invalidate(); list.Invalidate();
            cleanKey.Text = "clean " + Engine.Fmt(disp.Selected);
            cleanKey.Enabled = !busy && !cleaning && cats.Any(c => c.Checked && (c.Blocked || c.Scanned && (c.Bytes > 0 || c.Files > 0)));
            scanKey.Enabled = !busy && !cleaning;
            PlaceKeys(cleanKey.Parent);
        }

        // ---- updates: a card at the bottom, in place of the status line, when GitHub has a newer version ----
        Panel updateBar, divider; Label updateText, updateSub; Key updateKey, laterKey, newsKey; Release pending;

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
            updateBar.Resize += (s, e) =>
            {
                int mid = Theme.D(8) + (updateBar.Height - Theme.D(18)) / 2, gap = Theme.D(8);
                int y = mid - updateKey.Height / 2;
                updateKey.Location = new Point(updateBar.Width - updateKey.Width - Theme.D(34), y);
                laterKey.Location = new Point(updateKey.Left - laterKey.Width - gap, y);
                newsKey.Location = new Point(laterKey.Left - newsKey.Width - gap, y);
                int tx = Theme.D(18 + 44), tw = Math.Max(10, newsKey.Left - tx - Theme.D(16));
                updateText.Bounds = new Rectangle(tx, mid - Theme.D(22), tw, Theme.D(21));
                updateSub.Bounds = new Rectangle(tx, mid + Theme.D(1), tw, Theme.D(20));
            };
            laterKey.Click += (s, e) => HideUpdate();
            newsKey.Click += (s, e) => { if (pending != null && pending.Page != null) try { Process.Start(pending.Page); } catch { } };
            updateKey.Click += (s, e) =>
            {
                if (pending == null) return;
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

        public void ShowUpdate(Release r)
        {
            pending = r;
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
                foreach (var c in items.OrderBy(x => x.Level)) if (!scanQueue.Contains(c)) { c.Scanned = false; scanQueue.Enqueue(c); }
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
                    lock (qLock) { if (scanQueue.Count == 0) { workers--; break; } c = scanQueue.Dequeue(); }
                    BeginInvoke((Action)(() => status.Text = "Scanning  " + c.Name + " ..."));
                    var cnt = new Counter();
                    Engine.Process(c, false, cnt);
                    c.Files = cnt.Files; c.Bytes = cnt.Bytes; c.Scanned = true;
                    BeginInvoke((Action)(() => { drawer.Panel.RefreshStats(c); Refresh_(); }));
                }
                lock (qLock) { if (workers > 0) return; workerRunning = false; }   // the last worker out finishes the scan
                BeginInvoke((Action)(() =>
                {
                    ReadDisk(); History.Record(disp.Free); disp.History = History.Read();
                    list.Relayout(); Refresh_();
                    status.Text = History.FreedAllTime(out _cl) == 0
                        ? "Quick clean is ticked for you, so one click on Clean is enough. You see the list before anything goes."
                        : "Scan done.  Click a row to see what it is.  Tick the box to include it.";
                    if (OpenPanelFor != null) { var oc = cats.FirstOrDefault(x => x.Id == OpenPanelFor); if (oc != null) { tiers.Current = oc.Level; list.Tier = oc.Level; list.Relayout(); list.Selected = oc; drawer.Open(oc, true); list.OpenItem = oc; } OpenPanelFor = null; }
                    if (SnapshotPath != null) { var sp = SnapshotPath; SnapshotPath = null; BeginInvoke((Action)(() => { Program.Snap(this, sp); Close(); })); }
                }));
            });
            t.IsBackground = true; t.Start();
        }

        void Clean()
        {
            // the Recycle Bin item always runs first, so files moved to the bin in this clean are never emptied by it
            var sel = cats.Where(c => c.Checked && (c.Blocked || c.Bytes > 0 || c.Files > 0)).OrderBy(c => c.Kind == Kind.RecycleBin ? 0 : 1).ToList();
            if (sel.Count == 0) return;
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
            pf.FormClosed += (s, e) =>
            {
                foreach (var c in sel) c.Recycle = false;
                cleaning = false; pf.Dispose();
                ReadDisk(); Enqueue(cats);
                status.Text = "Cleaning finished. The list is being measured again.";
            };
            pf.Show(this);
            status.Text = "Cleaning in its own window. You can keep looking around here; Clean is ready again when it's done.";
        }
    }
}

namespace SpaceHunter
{
    // Inside a folder you added: its files and sub-folders, each with a tick box. Unticked ones stay; clicking a
    // folder's name opens it, and the top line goes back up (never above the folder you added).
    class InsideList : Canvas
    {
        class Entry { public string Path, Name; public bool Dir; public long Size; }
        readonly List<Entry> rows = new List<Entry>();
        public string Root, Here;
        int hover = -1, hoverBox = -1, more;
        const float RowH = 30, Max = 60;
        public event EventHandler Changed, Resized;
        Font fName = Theme.CSans(9.75f), fSize = Theme.CSans(8.75f), fUp = Theme.CSemi(9.5f);

        public InsideList() { BackColor = Theme.Pane; }

        public void Open(string root) { Root = root.TrimEnd('\\'); Go(Root); }

        void Go(string dir)
        {
            Here = dir; rows.Clear(); hover = hoverBox = -1; more = 0;
            try
            {
                var d = new DirectoryInfo(dir);
                var items = d.GetFileSystemInfos().Where(i => (i.Attributes & FileAttributes.ReparsePoint) == 0).ToList();
                foreach (var i in items.OfType<DirectoryInfo>().OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
                    rows.Add(new Entry { Path = i.FullName, Name = i.Name, Dir = true });
                foreach (var f in items.OfType<FileInfo>().OrderByDescending(x => { try { return x.Length; } catch { return 0L; } }))
                    rows.Add(new Entry { Path = f.FullName, Name = f.Name, Size = f.Length });
            }
            catch { }
            if (rows.Count > Max) { more = rows.Count - (int)Max; rows.RemoveRange((int)Max, more); }
            Height = Theme.D((int)(RowH * (rows.Count + (Deeper ? 1 : 0) + (more > 0 ? 1 : 0) + (rows.Count == 0 ? 1 : 0)) + 4));
            Invalidate();
            if (Resized != null) Resized(this, EventArgs.Empty);
        }

        bool Deeper { get { return Here != null && Root != null && !string.Equals(Here, Root, StringComparison.OrdinalIgnoreCase); } }
        float Top0 { get { return Deeper ? RowH : 0; } }
        int At(float y) { int i = (int)((y - Top0) / RowH); return y >= Top0 && i >= 0 && i < rows.Count ? i : -1; }
        bool InheritedSkip(string p) { var s = Custom.Skip; return Custom.IsSkipped(System.IO.Path.GetDirectoryName(p), s) && !string.Equals(p, Root, StringComparison.OrdinalIgnoreCase); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            float x = X(e), y = Y(e); int h = Deeper && y < RowH ? -2 : At(y), hb = h >= 0 && x < 30 ? h : -1;
            if (h != hover || hb != hoverBox) { hover = h; hoverBox = hb; Cursor = h == -2 || hb >= 0 || (h >= 0 && rows[h].Dir) ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { hover = hoverBox = -1; Invalidate(); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            float x = X(e), y = Y(e);
            if (Deeper && y < RowH) { Go(System.IO.Path.GetDirectoryName(Here)); return; }
            int i = At(y); if (i < 0) return;
            var r = rows[i];
            if (x < 30)
            {
                if (InheritedSkip(r.Path)) return;   // the folder around it is unticked
                Custom.SetSkipped(r.Path, !Custom.IsSkipped(r.Path, Custom.Skip));
                Invalidate(); if (Changed != null) Changed(this, EventArgs.Empty);
                return;
            }
            if (r.Dir) Go(r.Path);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Begin(e); g.Clear(Theme.Pane);
            float w = LW, y = 0;
            var skip = Custom.Skip;
            var nowrap = new StringFormat { Trimming = StringTrimming.EllipsisPath, FormatFlags = StringFormatFlags.NoWrap };
            if (Deeper)
            {
                if (hover == -2) using (var b = new SolidBrush(Theme.Hover)) g.FillRectangle(b, 0, 0, w, RowH - 1);
                using (var pen = new Pen(Theme.Ink, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLines(pen, new[] { new PointF(14, 9), new PointF(9, 14.5f), new PointF(14, 20) });
                string rel = Here.Substring(Root.Length).TrimStart('\\');
                using (var b = new SolidBrush(Theme.Ink)) g.DrawString("Back  \u00b7  " + System.IO.Path.GetFileName(Root) + "\\" + rel, fUp, b, new RectangleF(24, 6, w - 30, 20), nowrap);
                using (var pen = new Pen(Theme.Line)) g.DrawLine(pen, 0, RowH - 1, w, RowH - 1);
                y = RowH;
            }
            if (rows.Count == 0) using (var b = new SolidBrush(Theme.Soft)) g.DrawString("Empty", fName, b, 30, y + 6);
            for (int i = 0; i < rows.Count; i++, y += RowH)
            {
                var r = rows[i];
                bool inherited = InheritedSkip(r.Path), off = inherited || Custom.IsSkipped(r.Path, skip);
                if (i == hover) using (var b = new SolidBrush(Theme.Hover)) g.FillRectangle(b, 0, y, w, RowH - 1);
                var box = new RectangleF(6, y + 7, 15, 15);
                if (!off)
                {
                    using (var p = Theme.Round(box, 2)) using (var b = new SolidBrush(Theme.Orange)) g.FillPath(b, p);
                    using (var pen = new Pen(Theme.Ink, 2f)) g.DrawLines(pen, new[] { new PointF(box.X + 3.5f, box.Y + 7.5f), new PointF(box.X + 6.5f, box.Y + 10.5f), new PointF(box.X + 11.5f, box.Y + 4.5f) });
                }
                else using (var p = Theme.Round(box, 2)) using (var pen = new Pen(inherited ? Theme.Line : Theme.Ink, 1.5f)) g.DrawPath(pen, p);
                string size = r.Dir ? "folder" : Engine.Fmt(r.Size); var sm = g.MeasureString(size, fSize);
                using (var b = new SolidBrush(off ? Theme.Soft : Theme.Ink))
                    g.DrawString(r.Name, fName, b, new RectangleF(30, y + 6, w - 30 - sm.Width - (r.Dir ? 26 : 10), 20), new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });
                using (var b = new SolidBrush(Theme.Soft)) g.DrawString(size, fSize, b, w - sm.Width - (r.Dir ? 20 : 4), y + 8);
                if (r.Dir)
                    using (var pen = new Pen(i == hover ? Theme.Ink : Theme.Soft, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    { float cx = w - 8, cy = y + RowH / 2; g.DrawLines(pen, new[] { new PointF(cx - 3, cy - 5), new PointF(cx + 2, cy), new PointF(cx - 3, cy + 5) }); }
                using (var pen = new Pen(Theme.Line)) g.DrawLine(pen, 30, y + RowH - 1, w, y + RowH - 1);
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
                    try { len = f.Length; t = f.LastWriteTime; } catch { continue; }
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
