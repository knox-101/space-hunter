// Copyright (c) 2026 Blesson Reji (knox-101). Space Hunter is free software under the GNU GPL v3 or later; see LICENSE.
// SPDX-License-Identifier: GPL-3.0-or-later
// Space Hunter (Windows) - Space map: drill down from a drive to whatever is big, see whether the cleanup
// list already covers it, and add a folder to the cleanup list. Nothing is deleted here.
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
    class FinderForm : Form
    {
        readonly CanvasPanel head = new CanvasPanel();
        readonly FolderListView list;
        readonly DriveKey driveKey = new DriveKey();
        DrivePicker picker;
        public bool OpenDrivesOnStart;   // test hook
        readonly Key upKey = new Key("up one level"), openKey = new Key("open in explorer");
        readonly Label hint = new Label();
        FolderNode cur; string drive; bool scanning; long target; int sweep;   // target: the drive's used bytes (0 = unknown)
        readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer { Interval = 60 };
        public string SnapshotPath; readonly string startPath;
        public bool Added;
        Font fLabel = Theme.CSemi(10f), fBig = Theme.CDisplay(40f), fUnit = Theme.CDisplay(17f), fPath = Theme.CSans(11f), fSub = Theme.CSans(9.5f);

        public FinderForm(List<Category> cats, string start = null)
        {
            startPath = start;
            Theme.Window(this, "Space Hunter  ·  Space map", 940, 760);
            MinimumSize = SizeFromClientSize(Theme.Fit(660, 480));
            StartPosition = FormStartPosition.CenterParent;
            list = new FolderListView(cats);

            head.Dock = DockStyle.Top; head.Height = Theme.D(158); head.Draw += PaintHead;
            var headWrap = new Panel { Dock = DockStyle.Top, Height = Theme.D(158 + 18), Padding = Theme.Pad(18, 18, 18, 0), BackColor = Theme.Chassis };
            headWrap.Controls.Add(head);
            var keys = new Panel { Dock = DockStyle.Top, Height = Theme.D(60), BackColor = Theme.Chassis };
            driveKey.Location = Theme.P(18, 12);
            keys.Controls.Add(driveKey); keys.Controls.Add(upKey); keys.Controls.Add(openKey);
            keys.Resize += (s, e) => { openKey.Location = new Point(keys.Width - openKey.Width - Theme.D(18), Theme.D(12)); upKey.Location = new Point(openKey.Left - upKey.Width - Theme.D(8), Theme.D(12)); };
            hint.Dock = DockStyle.Bottom; hint.Height = Theme.D(30); hint.Padding = Theme.Pad(20, 7, 0, 0); hint.Font = Theme.Sans(9f); hint.ForeColor = Theme.Muted; hint.UseMnemonic = false;
            hint.Text = "Click a folder to look inside. “Add to cleanup list” puts it in the Caution section, unticked. Nothing here deletes anything.";
            list.Dock = DockStyle.Fill;
            Controls.Add(list); Controls.Add(keys); Controls.Add(headWrap); Controls.Add(hint);

            driveKey.Click += (s, e) => OpenDrives();
            upKey.Click += (s, e) => { if (cur != null && cur.Parent != null) Go(cur.Parent); };
            openKey.Click += (s, e) => { if (cur != null) try { Process.Start("explorer.exe", "\"" + cur.Path + "\""); } catch { } };
            list.Open += (s, e) => Go((FolderNode)s);
            list.Reveal += (s, e) => { try { Process.Start("explorer.exe", "\"" + ((FolderNode)s).Path + "\""); } catch { } };
            list.AddedFolder += (s, e) => { Added = true; hint.Text = "Added: " + Theme.MidPath(((FolderNode)s).Path, 70) + ". It's on the cleanup list, in Caution, unticked. Click “Added” to undo."; };
            list.RemovedFolder += (s, e) => { Added = true; hint.Text = "Taken off the cleanup list: " + Theme.MidPath(((FolderNode)s).Path, 70) + "."; };
            tick.Tick += (s, e) => { sweep++; head.Invalidate(); };
            Shown += (s, e) => StartScan(startPath ?? Path.GetPathRoot(Engine.W));
        }

        // test hook: draw the window off-screen (no focus needed) and save it
        public void SaveImage(string path)
        {
            using (var bmp = new Bitmap(ClientSize.Width, ClientSize.Height)) { DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height)); bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png); }
        }

        void StartScan(string path)
        {
            if (scanning) return;
            scanning = true; drive = path; cur = null; list.SetNode(null);
            target = 0;
            try { if (path.TrimEnd('\\').Length <= 2) { var di = new DriveInfo(path); target = di.TotalSize - di.TotalFreeSpace; } } catch { }
            driveKey.Drive = DriveChoice.All().FirstOrDefault(d => path.StartsWith(d.Root, StringComparison.OrdinalIgnoreCase));
            driveKey.Enabled = false;
            upKey.Enabled = openKey.Enabled = false;
            tick.Start(); head.Invalidate();
            var t = new Thread(() =>
            {
                var n = BigFolders.Scan(path);
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        scanning = false; tick.Stop(); Go(n); driveKey.Enabled = true;
                        if (OpenDrivesOnStart) { OpenDrivesOnStart = false; OpenDrives(); return; }
                        if (SnapshotPath != null) { Program.Snap(this, SnapshotPath); Close(); }
                    }));
                }
                catch { }
            });
            t.IsBackground = true; t.Start();
        }

        // The drive list: the window dims behind it until a drive is picked (or the list is dismissed).
        void OpenDrives()
        {
            if (picker != null || scanning) return;
            var drives = DriveChoice.All();
            if (drives.Count == 0) return;
            driveKey.Open = true; Refresh();   // paint everything first: the dimmed backdrop is a picture of this window
            picker = new DrivePicker(this, driveKey, drives, driveKey.Drive != null ? driveKey.Drive.Root : drive);
            picker.Picked += d => { if (!string.Equals(d.Root, drive, StringComparison.OrdinalIgnoreCase)) StartScan(d.Root); };
            picker.Closed += () => { var p = picker; picker = null; Controls.Remove(p); p.Dispose(); driveKey.Open = false; driveKey.Invalidate(); };
            Controls.Add(picker); picker.BringToFront(); picker.Focus();
            if (SnapshotPath != null)   // test hook: capture the open list once it has faded in
            {
                var tm = new System.Windows.Forms.Timer { Interval = 400 };
                tm.Tick += (s, e) => { tm.Stop(); Program.Snap(this, SnapshotPath); Close(); };
                tm.Start();
            }
        }

        void Go(FolderNode n)
        {
            cur = n; list.SetNode(n);
            upKey.Enabled = n.Parent != null; openKey.Enabled = true; head.Invalidate();
        }

        void PaintHead(object s, PaintEventArgs e)
        {
            var g = e.Graphics; float W = head.LW, H = head.LH, pad = 20;
            g.Clear(Theme.Chassis);
            using (var p = Theme.Round(new RectangleF(0.5f, 0.5f, W - 1, H - 1), 7)) using (var b = new SolidBrush(Theme.Screen)) g.FillPath(b, p);
            string phase = BigFolders.Phase;
            bool waiting = scanning && (phase == "starting" || phase == "sorting" || BigFolders.Scanned == 0);
            string label = !scanning ? "Space map  ·  looking only, nothing is deleted here"
                         : "Space map  ·  " + (phase == "sorting" ? "sorting folders by size" : waiting ? "getting ready to measure " + (drive ?? "").TrimEnd('\\')
                                                                     : "measuring " + (drive ?? "").TrimEnd('\\') + "  ·  counting files");
            using (var b = new SolidBrush(Color.FromArgb(0xB4, 0xB1, 0xAA))) g.DrawString(label, fLabel, b, pad, 12);
            if (waiting)   // a small turning arc at the top right: something is happening before the numbers start
            {
                float cx = W - pad - 9, cy = 23, rr = 8;
                using (var pen = new Pen(Theme.DisplayTrack, 2.4f)) g.DrawEllipse(pen, cx - rr, cy - rr, 2 * rr, 2 * rr);
                using (var pen = new Pen(Theme.Orange, 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(pen, cx - rr, cy - rr, 2 * rr, 2 * rr, (sweep * 24) % 360, 100);
            }
            if (cur != null)
            {
                string num, unit; Theme.SizeParts(cur.Size, out num, out unit);
                using (var b = new SolidBrush(Theme.Orange))
                {
                    var ns = g.MeasureString(num, fBig);
                    g.DrawString(num, fBig, b, pad - 5, 28);
                    g.DrawString(unit, fUnit, b, pad - 5 + ns.Width - 6, 28 + ns.Height - g.MeasureString(unit, fUnit).Height - 11);
                }
                using (var b = new SolidBrush(Theme.DisplayInk)) g.DrawString(Theme.MidPath(cur.Path, 90), fPath, b, pad, 104);
                using (var b = new SolidBrush(Theme.DisplayMuted))
                    g.DrawString(cur.Files.ToString("N0") + " files" + (cur.Denied ? "  ·  some folders couldn't be read" : ""), fSub, b, pad, 128);
            }
            else if (scanning)
            {
                // a big running count and a bar, so the scan is impossible to miss
                using (var b = new SolidBrush(Theme.Orange)) g.DrawString(BigFolders.Scanned.ToString("N0") + " FILES", fBig, b, pad - 5, 28);
                float bx = pad, by = 98, bw = W - 2 * pad, bh = 14;
                using (var b = new SolidBrush(Theme.DisplayTrack)) g.FillRectangle(b, bx, by, bw, bh);
                if (target > 0)
                {
                    float f = (float)Math.Min(1.0, (double)BigFolders.ScannedBytes / target);
                    using (var b = new SolidBrush(Theme.Orange)) g.FillRectangle(b, bx, by, bw * f, bh);
                    using (var b = new SolidBrush(Theme.DisplayInk))
                        g.DrawString("Measured " + Engine.Fmt(BigFolders.ScannedBytes) + " of " + Engine.Fmt(target) + " used  ·  " + (int)(f * 100) + "%", fSub, b, pad, 122);
                }
                else
                {
                    float seg = bw * 0.22f, x = bx + ((sweep * 9) % (int)(bw + seg)) - seg;   // unknown total: a moving block
                    var st = g.Save(); g.SetClip(new RectangleF(bx, by, bw, bh));
                    using (var b = new SolidBrush(Theme.Orange)) g.FillRectangle(b, x, by, seg, bh);
                    g.Restore(st);
                    using (var b = new SolidBrush(Theme.DisplayInk)) g.DrawString("Measured " + Engine.Fmt(BigFolders.ScannedBytes) + " so far", fSub, b, pad, 122);
                }
            }
        }
    }

    // Folder rows: name, an "on the list" tag or an "add to cleanup list" key, a share bar, the size.
    class FolderListView : Canvas
    {
        readonly List<Category> cats;
        FolderNode node; List<FolderNode> rows = new List<FolderNode>();
        readonly Dictionary<FolderNode, string> cover = new Dictionary<FolderNode, string>();
        readonly Dictionary<FolderNode, bool> inside = new Dictionary<FolderNode, bool>();
        readonly Dictionary<FolderNode, string> refusal = new Dictionary<FolderNode, string>();
        readonly HashSet<FolderNode> added = new HashSet<FolderNode>();
        float scroll, dragOffset; int hover = -1, hoverChip = -1; bool dragThumb;
        const float RowH = 50;
        public event EventHandler Open, Reveal, AddedFolder, RemovedFolder;
        Font fName = Theme.CSemi(10.5f), fSize = Theme.CSemi(10.5f), fSub = Theme.CSans(8.75f), fChip = Theme.CSemi(8.25f);

        public FolderListView(List<Category> cats) { this.cats = cats ?? new List<Category>(); }

        public void SetNode(FolderNode n)
        {
            node = n; scroll = 0; rows = new List<FolderNode>(); cover.Clear(); inside.Clear(); refusal.Clear(); added.Clear();
            if (n != null)
            {
                rows.AddRange(n.Kids.Where(k => k.Size > 0));
                if (n.OwnSize > 0) { rows.Add(new FolderNode { Name = "Files directly in this folder", Path = n.Path, Size = n.OwnSize, Files = n.OwnFiles }); rows.Sort((a, b) => b.Size.CompareTo(a.Size)); }
                // folders you added earlier show as "added", so they can be taken off again here
                var mine = new HashSet<string>(Custom.All.Select(p => p.TrimEnd(Path.DirectorySeparatorChar)), StringComparer.OrdinalIgnoreCase);
                foreach (var r in rows.Where(r => r.Parent != null))
                {
                    if (mine.Contains(r.Path.TrimEnd(Path.DirectorySeparatorChar))) { added.Add(r); continue; }
                    bool ins; var c = Engine.Coverage(r.Path, cats, out ins);
                    if (c != null) { cover[r] = c; inside[r] = ins; }
                    string why; if (!Engine.CanAdd(r.Path, out why)) refusal[r] = why;
                }
            }
            Invalidate();
        }

        float MaxScroll { get { return Math.Max(0, rows.Count * RowH - LH); } }
        void ScrollTo(float v) { scroll = Math.Max(0, Math.Min(MaxScroll, v)); Invalidate(); }
        protected override void OnMouseWheel(MouseEventArgs e) { ScrollTo(scroll - e.Delta / 120f * RowH * 2); }
        RectangleF Thumb()
        {
            if (MaxScroll <= 0) return RectangleF.Empty;
            float h = Math.Max(40, LH * LH / Math.Max(1, rows.Count * RowH));
            return new RectangleF(LW - 8, (LH - h) * scroll / MaxScroll, 4, h);
        }
        int At(float y) { int i = (int)((y + scroll) / RowH); return i >= 0 && i < rows.Count ? i : -1; }

        // the "add to cleanup list" key sits between the name and the bar
        RectangleF Chip(int i) { float w = LW - 14; return new RectangleF(w * 0.38f, i * RowH - scroll + 12, 162, 26); }
        bool CanAddRow(FolderNode r) { return r.Parent != null && !added.Contains(r) && !refusal.ContainsKey(r) && !(inside.ContainsKey(r) && inside[r]); }
        bool HasChip(FolderNode r) { return CanAddRow(r) || added.Contains(r); }   // "added" chips can be clicked to undo

        protected override void OnMouseMove(MouseEventArgs e)
        {
            float x = X(e), y = Y(e);
            if (dragThumb) { var th = Thumb(); float track = LH - th.Height; if (track > 0) ScrollTo((y - dragOffset) * MaxScroll / track); return; }
            int h = At(y), hc = h >= 0 && HasChip(rows[h]) && Chip(h).Contains(x, y) ? h : -1;
            if (h != hover || hc != hoverChip) { hover = h; hoverChip = hc; Cursor = h >= 0 && (hc >= 0 || rows[h].Kids.Count > 0) ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { hover = hoverChip = -1; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { dragThumb = false; }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            var th = Thumb();
            if (!th.IsEmpty && X(e) >= LW - 12) { dragThumb = true; dragOffset = Y(e) - th.Y; }
        }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            float x = X(e), y = Y(e); int i = At(y); if (i < 0 || dragThumb) return;
            var r = rows[i];
            if (added.Contains(r) && Chip(i).Contains(x, y))   // undo: take it off the cleanup list again
            {
                Custom.Remove(r.Path); added.Remove(r); Invalidate();
                if (RemovedFolder != null) RemovedFolder(r, EventArgs.Empty);
                return;
            }
            if (CanAddRow(r) && Chip(i).Contains(x, y))
            {
                Custom.Add(r.Path); added.Add(r); Invalidate();
                if (AddedFolder != null) AddedFolder(r, EventArgs.Empty);
                return;
            }
            if (r.Kids.Count > 0 && r.Parent != null && Open != null) Open(r, EventArgs.Empty);
        }
        protected override void OnMouseDoubleClick(MouseEventArgs e) { int i = At(Y(e)); if (i >= 0 && Reveal != null) Reveal(rows[i], EventArgs.Empty); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Begin(e); g.Clear(Theme.Chassis);
            if (node == null) return;
            float w = LW - 14;
            int first = (int)(scroll / RowH), last = Math.Min(rows.Count - 1, (int)((scroll + LH) / RowH));
            long max = Math.Max(1, node.Size);
            var nowrap = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            for (int i = first; i <= last; i++)
            {
                var r = rows[i]; float top = i * RowH - scroll;
                if (i == hover) using (var b = new SolidBrush(Theme.Hover)) g.FillRectangle(b, 10, top, w - 6, RowH - 1);
                bool own = r.Parent == null;
                float nameW = w * 0.38f - 30;
                using (var b = new SolidBrush(own ? Theme.Soft : Theme.Ink)) g.DrawString(r.Name, fName, b, new RectangleF(22, top + 7, nameW, 20), nowrap);
                string sub = r.Files.ToString("N0") + (r.Files == 1 ? " file" : " files") + (r.Denied ? "  ·  partly unreadable" : "");
                if (!own && cover.ContainsKey(r)) sub = (inside[r] ? "Covered by: " : "Holds: ") + cover[r];
                else if (!own && refusal.ContainsKey(r) && !added.Contains(r)) sub = "Can't add: " + refusal[r];
                using (var b = new SolidBrush(Theme.Soft)) g.DrawString(sub, fSub, b, new RectangleF(22, top + 27, nameW, 18), nowrap);

                // tag or key
                var chip = Chip(i);
                if (!own && (added.Contains(r) || (inside.ContainsKey(r) && inside[r])))
                {
                    bool undo = added.Contains(r) && hoverChip == i;   // pointing at "added" offers the way back
                    using (var p = Theme.Round(chip, 4)) using (var b = new SolidBrush(undo ? Theme.Muted : Theme.Ink)) g.FillPath(b, p);
                    using (var b = new SolidBrush(Theme.DisplayInk)) using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        g.DrawString(undo ? "REMOVE FROM LIST" : added.Contains(r) ? "ADDED" : "ON THE LIST", fChip, b, chip, sf);
                }
                else if (CanAddRow(r))
                {
                    bool hot = hoverChip == i;
                    using (var p = Theme.Round(new RectangleF(chip.X, chip.Y + 2, chip.Width, chip.Height), 4)) using (var b = new SolidBrush(Theme.KeyShadow)) g.FillPath(b, p);
                    using (var p = Theme.Round(chip, 4)) using (var b = new SolidBrush(hot ? ControlPaint.Light(Theme.Key, 0.25f) : Theme.Key)) g.FillPath(b, p);
                    using (var b = new SolidBrush(Theme.Ink)) using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        g.DrawString("+ ADD TO CLEANUP LIST", fChip, b, chip, sf);
                }

                // bar: share of the current folder
                float bx = w * 0.38f + 178, bw = w * 0.82f - bx, frac = (float)((double)r.Size / max);
                if (bw > 20)
                {
                    using (var b = new SolidBrush(Theme.Line)) g.FillRectangle(b, bx, top + 21, bw, 8);
                    using (var b = new SolidBrush(Theme.Ink)) g.FillRectangle(b, bx, top + 21, Math.Max(frac > 0 ? 2 : 0, bw * frac), 8);
                }
                string size = Engine.Fmt(r.Size); var m = g.MeasureString(size, fSize);
                using (var b = new SolidBrush(Theme.Ink)) g.DrawString(size, fSize, b, w - m.Width - 14, top + 7);
                string pct = (100.0 * r.Size / max).ToString(r.Size * 1000 < max ? "0.0" : "0") + "%"; var pm = g.MeasureString(pct, fSub);
                using (var b = new SolidBrush(Theme.Soft)) g.DrawString(pct, fSub, b, w - pm.Width - 14, top + 27);
                if (!own && r.Kids.Count > 0)   // drill-in chevron
                    using (var pen = new Pen(i == hover ? Theme.Ink : Theme.Soft, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    { float cx = w - 2, cy = top + RowH / 2; g.DrawLines(pen, new[] { new PointF(cx - 4, cy - 6), new PointF(cx + 2, cy), new PointF(cx - 4, cy + 6) }); }
                using (var pen = new Pen(Theme.Line)) g.DrawLine(pen, 22, top + RowH - 1, w, top + RowH - 1);
            }
            var th = Thumb();
            if (!th.IsEmpty) using (var p = Theme.Round(th, 2)) using (var b = new SolidBrush(Color.FromArgb(120, Theme.Ink))) g.FillPath(b, p);
        }
    }
}
