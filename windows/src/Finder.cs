// Copyright (c) 2026 Blesson Reji (knox-101). Space Hunter is free software under the GNU GPL v3 or later; see LICENSE.
// SPDX-License-Identifier: GPL-3.0-or-later
// Space Hunter (Windows) - Storage map: drill down from a drive to whatever is big, see whether the cleanup
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
    class FinderForm : Form, IMessageFilter
    {
        readonly CanvasPanel head = new CanvasPanel();
        readonly FolderListView list;
        readonly TreeMapView map;
        readonly PathBar pathBar = new PathBar { Dock = DockStyle.Top };
        readonly DriveKey driveKey = new DriveKey();
        DrivePicker picker;
        public bool OpenDrivesOnStart;   // test hook
        public int DrillDepth;           // test hook: open the biggest folder this many levels down
        readonly Key upKey = new Key("up one level"), openKey = new Key("open in explorer");
        readonly Label hint = new Label();
        FolderNode cur; string drive; bool scanning; long target; int sweep;   // target: the drive's used bytes (0 = unknown)
        readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer { Interval = 15 };
        readonly System.Diagnostics.Stopwatch spin = System.Diagnostics.Stopwatch.StartNew();   // motion follows time, so a late frame never jumps
        public string SnapshotPath; readonly string startPath;
        public bool Added;
        Font fLabel = Theme.CSemi(10f), fBig = Theme.CDisplay(40f), fUnit = Theme.CDisplay(17f), fPath = Theme.CSans(11f), fSub = Theme.CSans(9.5f);

        public FinderForm(List<Category> cats, string start = null)
        {
            startPath = start;
            Theme.Window(this, "Space Hunter  ·  Storage map", 940, 760);
            MinimumSize = SizeFromClientSize(Theme.Fit(660, 480));
            StartPosition = FormStartPosition.CenterParent;
            list = new FolderListView(cats);
            // the wheel goes to the list wherever the pointer is on it (bar and percentage included), not to whatever has focus
            Application.AddMessageFilter(this);
            FormClosed += (s, e) => Application.RemoveMessageFilter(this);
            map = new TreeMapView(cats) { Dock = DockStyle.Top, Height = Theme.D(220) };
            Resize += (s, e) => map.Height = Math.Max(Theme.D(110), Math.Min(Theme.D(240), (ClientSize.Height - Theme.D(176 + 60 + 46 + 30)) / 2));

            head.Dock = DockStyle.Top; head.Height = Theme.D(158); head.Draw += PaintHead;
            var headWrap = new Panel { Dock = DockStyle.Top, Height = Theme.D(158 + 18), Padding = Theme.Pad(18, 18, 18, 0), BackColor = Theme.Chassis };
            headWrap.Controls.Add(head);
            var keys = new Panel { Dock = DockStyle.Top, Height = Theme.D(60), BackColor = Theme.Chassis };
            driveKey.Location = Theme.P(18, 12);
            keys.Controls.Add(driveKey);   // going up and "open in Explorer" live in the path bar, beside the folder they act on
            pathBar.Height = Theme.D(46);
            hint.Dock = DockStyle.Bottom; hint.Height = Theme.D(30); hint.Padding = Theme.Pad(20, 7, 0, 0); hint.Font = Theme.Sans(9f); hint.ForeColor = Theme.Muted; hint.UseMnemonic = false;
            hint.Text = "“Add to list” puts a folder or file in Personal & work items, unticked. Nothing here deletes anything.";
            list.Dock = DockStyle.Fill;
            Controls.Add(list); Controls.Add(map); Controls.Add(pathBar); Controls.Add(keys); Controls.Add(headWrap); Controls.Add(hint);

            driveKey.Click += (s, e) => OpenDrives();
            upKey.Click += (s, e) => { if (cur != null && cur.Parent != null) Go(cur.Parent); };
            pathBar.OpenHere += (s, e) => { if (cur != null) try { Process.Start("explorer.exe", "\"" + cur.Path + "\""); } catch { } };
            list.Open += (s, e) => Go((FolderNode)s);
            map.Open += (s, e) => Go((FolderNode)s);
            pathBar.Navigate += (s, e) => Go((FolderNode)s);
            pathBar.AddHere += (s, e) =>
            {
                if (cur == null) return;
                if (pathBar.IsAdded) { Custom.Remove(cur.Path); hint.Text = "Taken off the cleanup list: " + Theme.MidPath(cur.Path, 70) + "."; }
                else { Custom.Add(cur.Path); hint.Text = "Added: " + Theme.MidPath(cur.Path, 70) + ". It's on the cleanup list, in Personal & work items, unticked."; }
                Added = true; pathBar.SetNode(cur);
            };
            KeyPreview = true;   // Backspace or Alt+Left: up one level, as in Explorer
            KeyDown += (s, e) => { if ((e.KeyCode == Keys.Back || (e.Alt && e.KeyCode == Keys.Left)) && cur != null && cur.Parent != null && picker == null) { Go(cur.Parent); e.Handled = true; } };
            list.Reveal += (s, e) => { var r = (FolderNode)s; try { Process.Start("explorer.exe", (r.IsFile ? "/select,\"" : "\"") + r.Path + "\""); } catch { } };
            list.Placeholder = "Measuring\u2026 the map and the list appear here.";
            list.AddedFolder += (s, e) => { Added = true; map.SetNode(cur, FreeHere()); hint.Text = "Added: " + Theme.MidPath(((FolderNode)s).Path, 70) + ". It's on the cleanup list, in Personal & work items, unticked. Click “Added” to undo."; };
            list.RemovedFolder += (s, e) => { Added = true; map.SetNode(cur, FreeHere()); hint.Text = "Taken off the cleanup list: " + Theme.MidPath(((FolderNode)s).Path, 70) + "."; };
            tick.Tick += (s, e) => { sweep++; head.Invalidate(); };
            Shown += (s, e) => StartScan(startPath ?? Path.GetPathRoot(Engine.W));
            FormClosed += (s, e) => BigFolders.Cancel = true;   // closing the map stops its scan
        }

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != 0x020A || picker != null || !Visible || !IsHandleCreated || Form.ActiveForm != this) return false;   // WM_MOUSEWHEEL
            if (!list.RectangleToScreen(list.ClientRectangle).Contains(Cursor.Position)) return false;
            list.Wheel((short)(((long)m.WParam >> 16) & 0xFFFF));
            return true;
        }

        // test hook: draw the window off-screen (no focus needed) and save it
        public void SaveImage(string path)
        {
            using (var bmp = new Bitmap(ClientSize.Width, ClientSize.Height)) { DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height)); bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png); }
        }

        void StartScan(string path)
        {
            if (scanning) return;
            scanning = true; drive = path; cur = null; list.SetNode(null); map.SetNode(null, 0); pathBar.SetNode(null);
            target = 0;
            try { if (path.TrimEnd('\\').Length <= 2) { var di = new DriveInfo(path); target = di.TotalSize - di.TotalFreeSpace; } } catch { }
            driveKey.Drive = DriveChoice.All().FirstOrDefault(d => (path.TrimEnd('\\') + "\\").StartsWith(d.Root, StringComparison.OrdinalIgnoreCase));
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
                        for (int d = 0; d < DrillDepth && cur.Kids.Count > 0; d++) Go(cur.Kids[0]);
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
            cur = n; list.SetNode(n); map.SetNode(n, FreeHere()); pathBar.SetNode(n);
            map.Visible = n.Kids.Count(k => k.Size > 0) + (n.OwnSize > 0 ? 1 : 0) + (FreeHere() > 0 ? 1 : 0) > 1;   // a single block says nothing
            upKey.Enabled = n.Parent != null; openKey.Enabled = true; head.Invalidate();
        }

        // free space shows as a block only at the top of a drive
        long FreeHere()
        {
            if (cur == null || cur.Parent != null || cur.Path.TrimEnd('\\').Length > 2) return 0;
            try { return new DriveInfo(cur.Path).TotalFreeSpace; } catch { return 0; }
        }

        void PaintHead(object s, PaintEventArgs e)
        {
            var g = e.Graphics; float W = head.LW, H = head.LH, pad = 20;
            g.Clear(Theme.Chassis);
            using (var p = Theme.Round(new RectangleF(0.5f, 0.5f, W - 1, H - 1), 7)) using (var b = new SolidBrush(Theme.Screen)) g.FillPath(b, p);
            string phase = BigFolders.Phase;
            bool waiting = scanning && (phase == "starting" || phase == "sorting" || BigFolders.Scanned == 0);
            string label = !scanning ? "Storage map  ·  just looking, nothing is deleted here"
                         : "Storage map  ·  " + (phase == "sorting" ? "sorting folders by size" : waiting ? "getting ready to measure " + (drive ?? "").TrimEnd('\\')
                                                                     : "measuring " + (drive ?? "").TrimEnd('\\') + "  ·  counting files");
            using (var b = new SolidBrush(Color.FromArgb(0xB4, 0xB1, 0xAA))) g.DrawString(label, fLabel, b, pad, 12);
            if (scanning)   // a large turning ring at the right for the whole scan: impossible to miss
            {
                float rr = 26, cx = W - pad - rr - 6, cy = H / 2 - 4;
                using (var pen = new Pen(Theme.DisplayTrack, 5f)) g.DrawEllipse(pen, cx - rr, cy - rr, 2 * rr, 2 * rr);
                using (var pen = new Pen(Theme.Orange, 5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(pen, cx - rr, cy - rr, 2 * rr, 2 * rr, (float)(spin.Elapsed.TotalSeconds * 300 % 360), 110);
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
                float bx = pad, by = 98, bw = W - 2 * pad - 80, bh = 14;   // leaves room for the ring
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
                    float seg = bw * 0.22f, x = bx + (float)(spin.Elapsed.TotalSeconds * 150 % (bw + seg)) - seg;   // unknown total: a moving block
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
        public string Placeholder;   // shown while there's nothing to list yet

        // why a folder can't be added, as a short calm label (the rules themselves are in Engine.CanAdd)
        static string Calm(string why)
        {
            string w = why.ToLowerInvariant();
            if (w.Contains("space hunter")) return "Space Hunter's own folder";
            if (w.Contains("another account")) return "Another account's files";
            if (w.Contains("browser")) return "Browser profile";
            if (w.Contains("windows or installed")) return "Windows or an installed app";
            if (w.Contains("windows' own")) return "Windows settings";
            if (w.Contains("settings") || w.Contains("keys")) return "App settings";
            if (w.Contains("own files") || w.Contains("personal") || w.Contains("profile")) return "Your own files";
            if (w.Contains("protected")) return "Protected";
            if (w.Contains("already")) return "Already on your list";
            if (w.Contains("kept by windows")) return "Kept by Windows";
            if (w.Contains("drive")) return "A whole drive";
            return "Can't be added";
        }

        static string FileKind(string name)
        {
            string e = Path.GetExtension(name).TrimStart('.').ToUpperInvariant();
            return e.Length == 0 ? "file" : e + " file";
        }

        public void SetNode(FolderNode n)
        {
            node = n; scroll = 0; rows = new List<FolderNode>(); cover.Clear(); inside.Clear(); refusal.Clear(); added.Clear();
            if (n != null)
            {
                rows.AddRange(n.Kids.Where(k => k.Size > 0));
                // the files directly in this folder, one row each, under the folders (read now, only for this folder)
                if (n.OwnFiles > 0)
                {
                    var fl = new List<FolderNode>();
                    try
                    {
                        foreach (var f in new DirectoryInfo(n.Path).GetFiles())
                            if ((f.Attributes & FileAttributes.ReparsePoint) == 0) fl.Add(new FolderNode { Name = f.Name, Path = f.FullName, Size = f.Length, Files = 1, IsFile = true });
                    }
                    catch { }
                    fl.Sort((a, b) => b.Size.CompareTo(a.Size));
                    rows.AddRange(fl.Take(400));
                    if (fl.Count > 400) rows.Add(new FolderNode { Name = "+ " + (fl.Count - 400).ToString("N0") + " smaller files", Path = n.Path, Size = fl.Skip(400).Sum(f => f.Size), Files = fl.Count - 400 });
                }
                // folders you added earlier show as "added", so they can be taken off again here
                var mine = new HashSet<string>(Custom.All.Select(p => p.TrimEnd(Path.DirectorySeparatorChar)), StringComparer.OrdinalIgnoreCase);
                foreach (var r in rows.Where(r => r.Parent != null || r.IsFile))
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
        protected override void OnMouseWheel(MouseEventArgs e) { Wheel(e.Delta); }
        public void Wheel(int delta) { ScrollTo(scroll - delta / 120f * RowH * 2); }
        public float ScrollPos { get { return scroll; } }   // test hook
        RectangleF Thumb()
        {
            if (MaxScroll <= 0) return RectangleF.Empty;
            float h = Math.Max(40, LH * LH / Math.Max(1, rows.Count * RowH));
            return new RectangleF(LW - 8, (LH - h) * scroll / MaxScroll, 4, h);
        }
        int At(float y) { int i = (int)((y + scroll) / RowH); return i >= 0 && i < rows.Count ? i : -1; }

        // the "add to cleanup list" key sits between the name and the bar
        RectangleF Chip(int i) { float w = LW - 14; return new RectangleF(w * 0.38f, i * RowH - scroll + 12, 162, 26); }
        bool CanAddRow(FolderNode r) { return (r.Parent != null || r.IsFile) && !added.Contains(r) && !refusal.ContainsKey(r) && !(inside.ContainsKey(r) && inside[r]); }
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
            if (node == null)
            {
                if (Placeholder != null) using (var b = new SolidBrush(Theme.Muted)) using (var sf = new StringFormat { Alignment = StringAlignment.Center })
                    g.DrawString(Placeholder, Theme.CSemi(11f), b, new RectangleF(0, LH / 2 - 40, LW, 30), sf);
                return;
            }
            float w = LW - 14;
            int first = (int)(scroll / RowH), last = Math.Min(rows.Count - 1, (int)((scroll + LH) / RowH));
            long max = Math.Max(1, node.Size);
            var nowrap = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            for (int i = first; i <= last; i++)
            {
                var r = rows[i]; float top = i * RowH - scroll;
                if (i == hover) using (var b = new SolidBrush(Theme.Hover)) g.FillRectangle(b, 10, top, w - 6, RowH - 1);
                bool own = r.Parent == null && !r.IsFile;   // the folder's own summary rows
                float nameW = w * 0.38f - 30;
                using (var b = new SolidBrush(own ? Theme.Soft : Theme.Ink)) g.DrawString(r.Name, fName, b, new RectangleF(22, top + 7, nameW, 20), nowrap);
                string sub = r.IsFile ? FileKind(r.Name) : r.Files.ToString("N0") + (r.Files == 1 ? " file" : " files") + (r.Denied ? "  ·  partly unreadable" : "");
                if (!own && cover.ContainsKey(r)) sub = (inside[r] ? "Cleaned by: " : "Has cleanup items: ") + cover[r];
                else if (!own && refusal.ContainsKey(r) && !added.Contains(r)) sub = Calm(refusal[r]) + (r.IsFile ? "" : "  \u00b7  " + r.Files.ToString("N0") + (r.Files == 1 ? " file" : " files"));
                using (var b = new SolidBrush(Theme.Soft)) g.DrawString(sub, fSub, b, new RectangleF(22, top + 27, nameW, 18), nowrap);

                // tag or key
                var chip = Chip(i);
                if (!own && (added.Contains(r) || (inside.ContainsKey(r) && inside[r])))
                {
                    bool undo = added.Contains(r) && hoverChip == i;   // pointing at "added" offers the way back
                    using (var p = Theme.Round(chip, 4)) using (var b = new SolidBrush(undo ? Theme.DisplayTrack : Theme.Screen)) g.FillPath(b, p);
                    using (var b = new SolidBrush(Theme.DisplayInk)) using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        g.DrawString(undo ? "REMOVE FROM LIST" : added.Contains(r) ? "ADDED" : "ALREADY INCLUDED", fChip, b, chip, sf);
                }
                else if (CanAddRow(r))
                {
                    bool hot = hoverChip == i;
                    using (var p = Theme.Round(new RectangleF(chip.X, chip.Y + 2, chip.Width, chip.Height), 4)) using (var b = new SolidBrush(Theme.KeyShadow)) g.FillPath(b, p);
                    using (var p = Theme.Round(chip, 4)) using (var b = new SolidBrush(hot ? ControlPaint.Light(Theme.Key, 0.25f) : Theme.Key)) g.FillPath(b, p);
                    using (var b = new SolidBrush(Theme.Ink)) using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        g.DrawString("+ ADD TO LIST", fChip, b, chip, sf);
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
                double pv = 100.0 * r.Size / max; string pct = pv < 1 ? "<1%" : pv.ToString("0") + "%"; var pm = g.MeasureString(pct, fSub);
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

namespace SpaceHunter
{
    // The block chart: each folder is a block sized by its share of the folder you're in; at a drive's top level,
    // free space is a hatched block. An orange corner: part of it is already a cleanup item. Click a block to go in.
    class TreeMapView : Canvas
    {
        class Block { public FolderNode Node; public string Name; public long Size; public bool Free, Marked, Rest; public RectangleF R; }
        readonly List<Category> cats;
        readonly List<Block> blocks = new List<Block>();
        FolderNode node; long free; int hover = -1;
        public event EventHandler Open;
        const float Caption = 22;
        Font fName = Theme.CSemi(9.5f), fSize = Theme.CSans(8.75f), fCap = Theme.CSans(8.75f);

        public TreeMapView(List<Category> cats) { this.cats = cats ?? new List<Category>(); }

        public void SetNode(FolderNode n, long freeBytes)
        {
            node = n; free = freeBytes; hover = -1; Relayout(); Invalidate();
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Relayout(); }

        void Relayout()
        {
            blocks.Clear();
            if (node == null || LW < 40 || LH < Caption + 30) return;
            var all = new List<Block>();
            var mine = new HashSet<string>(Custom.All.Select(p => p.TrimEnd('\\')), StringComparer.OrdinalIgnoreCase);
            foreach (var k in node.Kids.Where(k => k.Size > 0))
            {
                bool ins; bool marked = mine.Contains(k.Path.TrimEnd('\\')) || Engine.Coverage(k.Path, cats, out ins) != null;
                all.Add(new Block { Node = k, Name = k.Name, Size = k.Size, Marked = marked });
            }
            if (node.OwnSize > 0) all.Add(new Block { Name = "Files here", Size = node.OwnSize });
            all.Sort((a, b) => b.Size.CompareTo(a.Size));
            long total = all.Sum(b => b.Size); if (total <= 0) return;
            // tiny folders become one "smaller folders" block, so every block stays big enough to point at
            var keep = all.Where(b => b.Size >= (total + free) * 0.012).Take(24).ToList();
            var rest = all.Except(keep).ToList();
            if (rest.Count > 0) keep.Add(new Block { Name = rest.Count + " smaller", Size = rest.Sum(b => b.Size), Rest = true, Marked = rest.Any(b => b.Marked) });
            keep.Sort((a, b) => b.Size.CompareTo(a.Size));
            if (free > 0) keep.Add(new Block { Name = "Free space", Size = free, Free = true });   // always last: the empty end of the disk
            total = keep.Sum(b => b.Size);
            Squarify(keep, new RectangleF(18, 0, LW - 36, LH - Caption));
            blocks.AddRange(keep);
        }

        static double Worst(List<double> row, double side)
        {
            double s = row.Sum(), mx = row.Max(), mn = row.Min();
            return Math.Max(side * side * mx / (s * s), s * s / (side * side * mn));
        }

        static void Squarify(List<Block> items, RectangleF r)
        {
            double total = items.Sum(b => (double)b.Size); if (total <= 0) return;
            double scale = r.Width * r.Height / total;
            int i = 0; var rect = r;
            while (i < items.Count)
            {
                double side = Math.Min(rect.Width, rect.Height); if (side <= 0) break;
                var row = new List<double>(); int start = i; double prev = double.MaxValue;
                while (i < items.Count)
                {
                    row.Add(items[i].Size * scale);
                    double w = Worst(row, side);
                    if (row.Count > 1 && w > prev) { row.RemoveAt(row.Count - 1); break; }
                    prev = w; i++;
                }
                double sum = row.Sum();
                if (rect.Width >= rect.Height)
                {
                    float cw = (float)(sum / rect.Height), y = rect.Y;
                    for (int k = 0; k < row.Count; k++) { float h = (float)(row[k] / cw); items[start + k].R = new RectangleF(rect.X, y, cw, h); y += h; }
                    rect = new RectangleF(rect.X + cw, rect.Y, rect.Width - cw, rect.Height);
                }
                else
                {
                    float rh = (float)(sum / rect.Width), x = rect.X;
                    for (int k = 0; k < row.Count; k++) { float w = (float)(row[k] / rh); items[start + k].R = new RectangleF(x, rect.Y, w, rh); x += w; }
                    rect = new RectangleF(rect.X, rect.Y + rh, rect.Width, rect.Height - rh);
                }
            }
        }

        bool CanOpen(Block b) { return b.Node != null && b.Node.Kids.Count > 0; }
        int At(float x, float y) { for (int i = 0; i < blocks.Count; i++) if (blocks[i].R.Contains(x, y)) return i; return -1; }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = At(X(e), Y(e));
            if (h != hover) { hover = h; Cursor = h >= 0 && CanOpen(blocks[h]) ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { if (hover != -1) { hover = -1; Invalidate(); } }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            int i = At(X(e), Y(e));
            if (i >= 0 && CanOpen(blocks[i]) && Open != null) Open(blocks[i].Node, EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Begin(e); g.Clear(Theme.Chassis);
            var nowrap = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            for (int i = 0; i < blocks.Count; i++)
            {
                var b = blocks[i]; var r = RectangleF.Inflate(b.R, -1.5f, -1.5f);
                if (r.Width < 2 || r.Height < 2) continue;
                bool hot = i == hover && CanOpen(b);
                if (b.Free) Theme.Hatch(g, r, Theme.Line, Theme.Hover);
                else using (var br = new SolidBrush(hot ? Theme.Screen : b.Rest ? Theme.Hover : Theme.Key)) g.FillRectangle(br, r);
                if (b.Marked)
                {
                    float c = Math.Min(14, Math.Min(r.Width, r.Height) / 2);
                    using (var br = new SolidBrush(Theme.Orange)) g.FillPolygon(br, new[] { new PointF(r.Right - c, r.Y), new PointF(r.Right, r.Y), new PointF(r.Right, r.Y + c) });
                }
                if (r.Width >= 46 && r.Height >= 34)
                {
                    var tr = new RectangleF(r.X + 8, r.Y + 6, r.Width - 16 - (b.Marked ? 8 : 0), 18);
                    Color ink = hot ? Theme.DisplayInk : Theme.Ink, soft = hot ? Theme.DisplayMuted : Theme.Soft;
                    using (var br = new SolidBrush(b.Free ? Theme.Muted : ink)) g.DrawString(b.Name, fName, br, tr, nowrap);
                    using (var br = new SolidBrush(soft)) g.DrawString(Engine.Fmt(b.Size), fSize, br, new RectangleF(r.X + 8, r.Y + 23, r.Width - 16, 16), nowrap);
                }
            }
            if (blocks.Count == 0) return;   // nothing to explain until the chart is there
            string cap = "Click a block to go inside it  ·  orange corner: part of it is already a cleanup item" + (free > 0 ? "  ·  hatched: free space" : "");
            if (hover >= 0) { var hb = blocks[hover]; cap = hb.Name + "  ·  " + Engine.Fmt(hb.Size) + (hb.Marked ? "  ·  part of it is already a cleanup item" : "") + (CanOpen(hb) ? "  ·  click to go inside" : ""); }
            using (var br = new SolidBrush(Theme.Muted))
                g.DrawString(cap, fCap, br, new RectangleF(20, LH - Caption + 4, LW - 40, 16), nowrap);
        }
    }
}

namespace SpaceHunter
{
    // Where you are in the Storage map: a Back key, then every folder from the drive down, each one clickable.
    class PathBar : Canvas
    {
        FolderNode node; int hover = -1;
        readonly List<KeyValuePair<RectangleF, FolderNode>> hits = new List<KeyValuePair<RectangleF, FolderNode>>();
        public event EventHandler Navigate;
        Font fKey = Theme.CSemi(9.25f), fCrumb = Theme.CSans(10f), fNow = Theme.CBold(10f);

        public void SetNode(FolderNode n)
        {
            node = n; hover = -1;
            string why; canAdd = n != null && Engine.CanAdd(n.Path, out why);
            IsAdded = n != null && Custom.All.Any(p => string.Equals(p.TrimEnd('\\'), n.Path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            Invalidate();
        }
        bool canAdd; public bool IsAdded;
        public event EventHandler AddHere, OpenHere;
        // "Open in Explorer" at the right of the trail, for the folder you're looking at
        RectangleF OpenKey() { return new RectangleF(LW - 18 - 158, 7, 158, 30); }
        // "+ Add this folder" at the right, for a folder you've opened (so you don't have to go back to add it)
        RectangleF AddKey() { return new RectangleF(OpenKey().X - 8 - 160, 7, 160, 30); }
        bool ShowAdd { get { return node != null && (canAdd || IsAdded); } }

        RectangleF Back() { return new RectangleF(18, 7, 92, 30); }
        bool CanGoBack { get { return node != null && node.Parent != null; } }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            float x = X(e), y = Y(e); int h = -1;
            if (CanGoBack && Back().Contains(x, y)) h = 0;
            else if (ShowAdd && AddKey().Contains(x, y)) h = 1000;
            else if (node != null && OpenKey().Contains(x, y)) h = 1001;
            else for (int i = 0; i < hits.Count; i++) if (hits[i].Key.Contains(x, y)) h = i + 1;
            if (h != hover) { hover = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { if (hover != -1) { hover = -1; Invalidate(); } }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (Navigate == null || node == null) return;
            float x = X(e), y = Y(e);
            if (CanGoBack && Back().Contains(x, y)) { Navigate(node.Parent, EventArgs.Empty); return; }
            if (ShowAdd && AddKey().Contains(x, y)) { if (AddHere != null) AddHere(this, EventArgs.Empty); return; }
            if (OpenKey().Contains(x, y)) { if (OpenHere != null) OpenHere(this, EventArgs.Empty); return; }
            foreach (var h in hits) if (h.Key.Contains(x, y)) { Navigate(h.Value, EventArgs.Empty); return; }
        }

        string NameOf(FolderNode n) { return n.Parent == null ? (n.Path.TrimEnd('\\').Length <= 2 ? n.Path.TrimEnd('\\') : Theme.MidPath(n.Path, 24)) : n.Name; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Begin(e); g.Clear(Theme.Chassis); hits.Clear();
            if (node == null) return;
            // the Back key, drawn like the other keys (flat and grey at the top of the drive)
            var b = Back(); bool on = CanGoBack;
            if (on) using (var p = Theme.Round(new RectangleF(b.X, b.Y + 2, b.Width, b.Height), 4)) using (var br = new SolidBrush(Theme.KeyShadow)) g.FillPath(br, p);
            using (var p = Theme.Round(b, 4)) using (var br = new SolidBrush(!on ? Theme.KeyOff : hover == 0 ? ControlPaint.Light(Theme.Key, 0.25f) : Theme.Key)) g.FillPath(br, p);
            Color ink = on ? Theme.Ink : Theme.Disabled;
            using (var pen = new Pen(ink, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                float ax = b.X + 14, ay = b.Y + b.Height / 2;
                g.DrawLines(pen, new[] { new PointF(ax + 5, ay - 5), new PointF(ax, ay), new PointF(ax + 5, ay + 5) });
                g.DrawLine(pen, ax, ay, ax + 12, ay);
            }
            using (var br = new SolidBrush(ink)) g.DrawString("BACK", fKey, br, b.X + 36, b.Y + 6);

            // the trail: drive > folder > folder; a long trail keeps the drive and the last folders
            var chain = new List<FolderNode>(); for (var n = node; n != null; n = n.Parent) chain.Insert(0, n);
            float x = b.Right + 16, maxX = OpenKey().X - 16 - (ShowAdd ? 168 : 0), y = 13;
            {
                var o = OpenKey();
                using (var p = Theme.Round(new RectangleF(o.X, o.Y + 2, o.Width, o.Height), 4)) using (var br = new SolidBrush(Theme.KeyShadow)) g.FillPath(br, p);
                using (var p = Theme.Round(o, 4)) using (var br = new SolidBrush(hover == 1001 ? ControlPaint.Light(Theme.Key, 0.25f) : Theme.Key)) g.FillPath(br, p);
                using (var br = new SolidBrush(Theme.Ink)) using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("OPEN IN EXPLORER", fKey, br, o, sf);
            }
            if (ShowAdd)
            {
                var a = AddKey();
                using (var p = Theme.Round(new RectangleF(a.X, a.Y + 2, a.Width, a.Height), 4)) using (var br = new SolidBrush(IsAdded ? Color.Black : Theme.KeyShadow)) g.FillPath(br, p);
                using (var p = Theme.Round(a, 4)) using (var br = new SolidBrush(IsAdded ? (hover == 1000 ? Theme.DisplayTrack : Theme.Screen) : hover == 1000 ? ControlPaint.Light(Theme.Key, 0.25f) : Theme.Key)) g.FillPath(br, p);
                using (var br = new SolidBrush(IsAdded ? Theme.DisplayInk : Theme.Ink)) using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(IsAdded ? (hover == 1000 ? "REMOVE FROM LIST" : "ADDED") : "+ ADD THIS FOLDER", fKey, br, a, sf);
            }
            var w = chain.Select((n, i) => g.MeasureString(NameOf(n), i == chain.Count - 1 ? fNow : fCrumb).Width + 26).ToList();
            int skipTo = 1;
            while (skipTo < chain.Count - 1 && x + w[0] + 30 + w.Skip(skipTo).Sum() > maxX) skipTo++;
            for (int i = 0; i < chain.Count; i++)
            {
                if (i > 0 && i < skipTo)
                {
                    if (i == 1) { using (var br = new SolidBrush(Theme.Soft)) g.DrawString("\u2026", fCrumb, br, x, y); x += 16; Chevron(g, ref x, y); }
                    continue;
                }
                bool last = i == chain.Count - 1;
                var f = last ? fNow : fCrumb; string name = NameOf(chain[i]); var sz = g.MeasureString(name, f);
                var r = new RectangleF(x - 4, y - 4, sz.Width + 8, sz.Height + 8);
                bool hot = false;
                if (!last) { hits.Add(new KeyValuePair<RectangleF, FolderNode>(r, chain[i])); hot = hover == hits.Count; }
                if (hot) using (var p = Theme.Round(r, 3)) using (var br = new SolidBrush(Theme.Key)) g.FillPath(br, p);
                using (var br = new SolidBrush(last ? Theme.Ink : hot ? Theme.Ink : Theme.Muted)) g.DrawString(name, f, br, x, y);
                x += sz.Width + 2;
                if (!last) Chevron(g, ref x, y);
            }
        }

        void Chevron(Graphics g, ref float x, float y)
        {
            using (var pen = new Pen(Theme.Soft, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLines(pen, new[] { new PointF(x + 4, y + 5), new PointF(x + 8, y + 9.5f), new PointF(x + 4, y + 14) });
            x += 20;
        }
    }
}
