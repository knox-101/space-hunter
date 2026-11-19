// Copyright (c) 2026 Blesson Reji (knox-101). Space Hunter is free software under the GNU GPL v3 or later; see LICENSE.
// SPDX-License-Identifier: GPL-3.0-or-later
// Space Hunter (Windows) - the drive key in the Space map and the list it opens. One key however many drives
// there are; while the list is open the rest of the window dims, until a drive is picked or the list is dismissed.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace SpaceHunter
{
    class DriveChoice
    {
        public string Root, Letter, Name;
        public long Total, Free;
        public bool Removable;

        public static List<DriveChoice> Demo;   // test hook: example drives for screenshots

        public static List<DriveChoice> All()
        {
            if (Demo != null) return Demo;
            var list = new List<DriveChoice>();
            string sys = Path.GetPathRoot(Engine.W);
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady || (d.DriveType != DriveType.Fixed && d.DriveType != DriveType.Removable)) continue;
                    string label = d.VolumeLabel;
                    if (string.IsNullOrWhiteSpace(label))
                        label = string.Equals(d.Name, sys, StringComparison.OrdinalIgnoreCase) ? "Windows" : d.DriveType == DriveType.Removable ? "USB drive" : "Local disk";
                    list.Add(new DriveChoice { Root = d.Name, Letter = d.Name.TrimEnd('\\'), Name = label, Total = d.TotalSize, Free = d.AvailableFreeSpace, Removable = d.DriveType == DriveType.Removable });
                }
                catch { }
            }
            return list;
        }
    }

    // The key: "C:  Windows · 75.5 GB free  ▾"
    class DriveKey : Control
    {
        DriveChoice drive; bool hover, down;
        readonly Font fLetter = Theme.Bold(11f), fText = Theme.Sans(9.75f), fEmpty = Theme.Semi(9.75f);
        public DriveKey()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent; Cursor = Cursors.Hand;
            Size = new Size(Theme.D(300), Theme.D(36));
        }
        public DriveChoice Drive { get { return drive; } set { drive = value; Invalidate(); } }
        public bool Open;   // drawn pressed while its list is open
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Theme.Hq(g);
            float s = Theme.S, lift = 2 * s, press = (down || Open) && Enabled ? lift : 0, rad = 4 * s;
            var face = new RectangleF(0.5f, 0.5f + press, Width - 1, Height - 1 - lift - 1);
            Color fill = !Enabled ? Color.FromArgb(0xD0, 0xCD, 0xC6) : hover || Open ? ControlPaint.Light(Theme.Key, 0.25f) : Theme.Key;
            if (press == 0)
                using (var p = Theme.Round(new RectangleF(0.5f, 0.5f + lift, Width - 1, Height - 1 - lift - 1), rad)) using (var b = new SolidBrush(Theme.KeyShadow)) g.FillPath(b, p);
            using (var p = Theme.Round(face, rad)) using (var b = new SolidBrush(fill)) g.FillPath(b, p);
            Color ink = Enabled ? Theme.Ink : Theme.Disabled;
            float cy = face.Y + face.Height / 2 + s;
            using (var b = new SolidBrush(ink))
            using (var sf = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            {
                if (drive == null) { g.DrawString("DRIVES", fEmpty, b, new RectangleF(14 * s, face.Y, Width, face.Height), sf); }
                else
                {
                    g.DrawString(drive.Letter, fLetter, b, new RectangleF(14 * s, face.Y + s, 40 * s, face.Height), sf);
                    g.DrawString(drive.Name + "  ·  " + Engine.Fmt(drive.Free) + " free", fText, b, new RectangleF(46 * s, face.Y + s, Width - 46 * s - 34 * s, face.Height), sf);
                }
            }
            // the arrow says there's a list
            float ax = Width - 22 * s;
            using (var b = new SolidBrush(ink)) g.FillPolygon(b, new[] { new PointF(ax - 6 * s, cy - 3 * s), new PointF(ax + 6 * s, cy - 3 * s), new PointF(ax, cy + 4 * s) });
        }
    }

    // The list, drawn over the whole window: the window behind it fades to a dim veil while the list is open.
    class DrivePicker : Canvas
    {
        readonly Bitmap behind;                 // the window as it looked when the list opened
        readonly List<DriveChoice> drives;
        readonly string current;
        readonly RectangleF anchor;             // the drive key, in design units of this overlay
        readonly Timer fade = new Timer { Interval = 15 };
        DateTime fadeStart; bool closing; float veil;   // 0..1
        int hover = -1; float scroll;
        const float RowH = 58, PanelW = 520, Pad = 8, FadeIn = 160, FadeOut = 120, MaxVeil = 0.42f;
        public event Action<DriveChoice> Picked;
        public event Action Closed;

        public DrivePicker(Form owner, Control key, List<DriveChoice> drives, string current)
        {
            this.drives = drives; this.current = current;
            var cs = owner.ClientSize;
            behind = new Bitmap(Math.Max(1, cs.Width), Math.Max(1, cs.Height));
            using (var g = Graphics.FromImage(behind)) g.CopyFromScreen(owner.PointToScreen(Point.Empty), Point.Empty, cs);
            var kp = owner.PointToClient(key.Parent.PointToScreen(key.Location));
            anchor = new RectangleF(kp.X / Theme.S, kp.Y / Theme.S, key.Width / Theme.S, key.Height / Theme.S);
            Bounds = new Rectangle(Point.Empty, cs); Anchor = AnchorStyles.None;
            hover = Math.Max(0, drives.FindIndex(d => string.Equals(d.Root, current, StringComparison.OrdinalIgnoreCase)));
            fade.Tick += (s, e) => Step();
            fadeStart = DateTime.Now; fade.Start();
        }

        RectangleF Panel()
        {
            float w = Math.Min(PanelW, LW - anchor.X - 18);
            float top = anchor.Bottom + 6, maxH = LH - top - 18;
            float h = Math.Min(drives.Count * RowH + 2 * Pad, Math.Max(RowH + 2 * Pad, maxH));
            return new RectangleF(anchor.X, top, w, h);
        }
        float MaxScroll { get { var p = Panel(); return Math.Max(0, drives.Count * RowH + 2 * Pad - p.Height); } }

        void Step()
        {
            float t = (float)(DateTime.Now - fadeStart).TotalMilliseconds / (closing ? FadeOut : FadeIn);
            float k = Math.Min(1, t); k = 1 - (1 - k) * (1 - k) * (1 - k);   // ease out
            veil = closing ? 1 - k : k;
            Invalidate();
            if (t >= 1) { fade.Stop(); if (closing) { if (Closed != null) Closed(); } }
        }

        public void Dismiss(DriveChoice pick)
        {
            if (closing) return;
            closing = true; fadeStart = DateTime.Now; fade.Start();
            if (pick != null && Picked != null) Picked(pick);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var raw = e.Graphics;
            raw.DrawImageUnscaled(behind, 0, 0);
            using (var b = new SolidBrush(Color.FromArgb((int)(255 * MaxVeil * veil), 0x12, 0x12, 0x12))) raw.FillRectangle(b, ClientRectangle);
            var g = Begin(e);
            int a = (int)(255 * veil);
            var p = Panel();
            // a soft shadow, then the panel
            for (int i = 6; i >= 1; i--)
                using (var b = new SolidBrush(Color.FromArgb((int)(a * 0.05f), 0, 0, 0))) g.FillRectangle(b, p.X - i + 3, p.Y - i + 6, p.Width + 2 * i, p.Height + 2 * i);
            using (var path = Theme.Round(p, 5)) using (var b = new SolidBrush(Color.FromArgb(a, Theme.Key))) g.FillPath(b, path);
            var st = g.Save(); g.SetClip(p);
            using (var fL = Theme.CBold(15f)) using (var fN = Theme.CSemi(11f)) using (var fS = Theme.CSans(9.5f))
                for (int i = 0; i < drives.Count; i++)
                {
                    var d = drives[i];
                    float y = p.Y + Pad + i * RowH - scroll;
                    if (y + RowH < p.Y || y > p.Bottom) continue;
                    bool sel = string.Equals(d.Root, current, StringComparison.OrdinalIgnoreCase);
                    if (i == hover) using (var b = new SolidBrush(Color.FromArgb(a, Theme.Hover))) g.FillRectangle(b, p.X + 1, y, p.Width - 2, RowH);
                    if (sel) using (var b = new SolidBrush(Color.FromArgb(a, Theme.Orange))) g.FillRectangle(b, p.X + 1, y + 6, 4, RowH - 12);
                    using (var b = new SolidBrush(Color.FromArgb(a, Theme.Ink)))
                    {
                        g.DrawString(d.Letter, fL, b, p.X + 18, y + RowH / 2 - fL.GetHeight(g) / 2 - 1);
                        g.DrawString(d.Name + (d.Removable ? "   USB" : ""), fN, b, p.X + 62, y + 10);
                    }
                    using (var b = new SolidBrush(Color.FromArgb(a, Theme.Muted)))
                        g.DrawString(Engine.Fmt(d.Free) + " free of " + Engine.Fmt(d.Total), fS, b, p.X + 62, y + 31);
                    float bx = p.X + Math.Max(250, p.Width - 220), bw = p.Right - 24 - bx, by = y + RowH / 2 - 4;
                    if (bw > 30)
                    {
                        float used = d.Total > 0 ? (float)(d.Total - d.Free) / d.Total : 0;
                        using (var b = new SolidBrush(Color.FromArgb(a, Theme.Line))) g.FillRectangle(b, bx, by, bw, 8);
                        using (var b = new SolidBrush(Color.FromArgb(a, sel ? Theme.Orange : Theme.DisplayUsed))) g.FillRectangle(b, bx, by, bw * used, 8);
                    }
                    if (i < drives.Count - 1) using (var pen = new Pen(Color.FromArgb(a, Theme.Line), 1f)) g.DrawLine(pen, p.X + 16, y + RowH, p.Right - 16, y + RowH);
                }
            g.Restore(st);
        }

        int RowAt(float x, float y)
        {
            var p = Panel();
            if (!p.Contains(x, y)) return -1;
            int i = (int)Math.Floor((y - p.Y - Pad + scroll) / RowH);
            return i >= 0 && i < drives.Count ? i : -1;
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = RowAt(X(e), Y(e));
            Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
            if (h >= 0 && h != hover) { hover = h; Invalidate(); }
        }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            int i = RowAt(X(e), Y(e));
            if (i >= 0) Dismiss(drives[i]);
            else if (!Panel().Contains(X(e), Y(e))) Dismiss(null);   // a click on the dimmed window closes the list
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            scroll = Math.Max(0, Math.Min(MaxScroll, scroll - e.Delta / 120f * RowH)); Invalidate();
        }
        protected override bool IsInputKey(Keys k) { return k == Keys.Up || k == Keys.Down || k == Keys.Enter || k == Keys.Escape || base.IsInputKey(k); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) Dismiss(null);
            else if (e.KeyCode == Keys.Enter && hover >= 0) Dismiss(drives[hover]);
            else if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Up)
            {
                hover = Math.Max(0, Math.Min(drives.Count - 1, hover + (e.KeyCode == Keys.Down ? 1 : -1)));
                float rowTop = hover * RowH, ph = Panel().Height - 2 * Pad;
                if (rowTop < scroll) scroll = rowTop; else if (rowTop + RowH > scroll + ph) scroll = rowTop + RowH - ph;
                Invalidate();
            }
        }
        protected override void Dispose(bool disposing) { if (disposing) { fade.Dispose(); behind.Dispose(); } base.Dispose(disposing); }
    }
}
