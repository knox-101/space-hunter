// Copyright (c) 2026 Blesson Reji (knox-101). Space Hunter is free software under the GNU GPL v3 or later; see LICENSE.
// SPDX-License-Identifier: GPL-3.0-or-later
// Space Hunter (Windows) - the first-run guide: the window dims, a lit cut-out sits on the real part, and a small
// display-style card explains it. The cut-out glides from one part to the next. Skip on every step; "?" replays it.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace SpaceHunter
{
    // Get: where the part is (design units of the window's client area), asked when the step starts.
    // Enter: what to do first (e.g. open an item's panel), so the step can point at it.
    class GuideStep { public Func<RectangleF> Get; public Action Enter; public string Title, Body; }

    class GuideOverlay : Canvas
    {
        Bitmap behind; readonly Form owner;
        readonly List<GuideStep> steps;
        readonly Timer anim = new Timer { Interval = 15 };
        DateTime animStart; int step; bool closing;
        float veil;                     // 0..1, fades in and out
        RectangleF from, to, spot;      // the cut-out glides from one target to the next
        RectangleF nextBox, skipBox, backBox;
        const float FadeMs = 180, GlideMs = 300, MaxVeil = 0.58f, CardW = 320;
        public event Action Closed;
        Font fTitle = Theme.CBold(12.5f), fBody = Theme.CSans(9.75f), fFoot = Theme.CMono(8.5f), fKey = Theme.CSemi(9f);

        public static string SeenFile { get { return Path.Combine(Engine.AppDir, "guide-seen.txt"); } }
        public static bool Seen { get { try { return File.Exists(SeenFile); } catch { return true; } } }

        public GuideOverlay(Form owner, List<GuideStep> steps, int start = 0)
        {
            this.steps = steps; this.owner = owner; step = Math.Max(0, Math.Min(start, steps.Count - 1));
            if (steps[step].Enter != null) steps[step].Enter();
            TakePicture();
            Bounds = new Rectangle(Point.Empty, owner.ClientSize); Anchor = AnchorStyles.None;
            from = to = spot = Pad(steps[step].Get());
            anim.Tick += (s, e) => Step();
            animStart = DateTime.Now; anim.Start();
            owner.Resize += (s, e) => Finish();   // the picture behind would be out of date
        }

        // the window as it looks now, drawn from itself (never a screen capture: another window on top must not end up in it)
        void TakePicture()
        {
            var cs = owner.ClientSize;
            var nb = new Bitmap(Math.Max(1, cs.Width), Math.Max(1, cs.Height));
            bool was = Visible && Parent != null; if (was) Visible = false;
            owner.Update();
            using (var whole = new Bitmap(Math.Max(1, owner.Width), Math.Max(1, owner.Height)))
            using (var g = Graphics.FromImage(nb))
            {
                owner.DrawToBitmap(whole, new Rectangle(0, 0, whole.Width, whole.Height));
                var o = owner.PointToScreen(Point.Empty);
                g.DrawImage(whole, new Rectangle(0, 0, cs.Width, cs.Height), new Rectangle(o.X - owner.Left, o.Y - owner.Top, cs.Width, cs.Height), GraphicsUnit.Pixel);
            }
            if (was) { Visible = true; Focus(); }
            var old = behind; behind = nb; if (old != null) old.Dispose();
        }

        static RectangleF Pad(RectangleF r) { r.Inflate(6, 6); return r; }
        static float Ease(float t) { t = Math.Max(0, Math.Min(1, t)); return 1 - (1 - t) * (1 - t) * (1 - t); }

        void Step()
        {
            float ms = (float)(DateTime.Now - animStart).TotalMilliseconds;
            veil = closing ? 1 - Ease(ms / FadeMs) : Math.Max(veil, Ease(ms / FadeMs));
            float k = Ease(ms / GlideMs);
            spot = new RectangleF(from.X + (to.X - from.X) * k, from.Y + (to.Y - from.Y) * k, from.Width + (to.Width - from.Width) * k, from.Height + (to.Height - from.Height) * k);
            Invalidate();
            if (closing && ms >= FadeMs) { anim.Stop(); if (Closed != null) Closed(); }
            else if (!closing && ms >= Math.Max(FadeMs, GlideMs)) anim.Stop();
        }

        void Go(int n)
        {
            if (n < 0 || closing) return;
            if (n >= steps.Count) { Finish(); return; }
            bool changes = steps[n].Enter != null || steps[step].Enter != null;
            if (steps[n].Enter != null) steps[n].Enter();
            if (changes) TakePicture();   // the window changed under the guide (a panel opened or closed)
            from = spot; to = Pad(steps[n].Get()); step = n;
            animStart = DateTime.Now; anim.Start();
        }

        public void Finish()
        {
            if (closing) return;
            try { File.WriteAllText(SeenFile, DateTime.Now.ToString("s")); } catch { }
            closing = true; animStart = DateTime.Now; anim.Start();
        }

        public void Next() { Go(step + 1); }
        public void Back() { Go(step - 1); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var raw = e.Graphics;
            raw.DrawImageUnscaled(behind, 0, 0);
            var g = Begin(e);
            int a = (int)(255 * veil);
            // the veil everywhere but the lit part
            using (var region = new Region(new RectangleF(0, 0, LW, LH)))
            using (var hole = Theme.Round(spot, 6))
            {
                region.Exclude(hole);
                using (var b = new SolidBrush(Color.FromArgb((int)(255 * MaxVeil * veil), 0x12, 0x12, 0x12))) g.FillRegion(b, region);
            }
            using (var hole = Theme.Round(spot, 6)) using (var pen = new Pen(Color.FromArgb(a, Theme.Orange), 2f)) g.DrawPath(pen, hole);

            // the card: under the lit part if there's room, else above it; kept inside the window
            var st = steps[step];
            float bodyH = g.MeasureString(st.Body, fBody, (int)(CardW - 28)).Height;
            float h = 16 + 22 + bodyH + 14 + 30 + 12;
            float x = Math.Max(16, Math.Min(LW - CardW - 16, spot.X + spot.Width / 2 - CardW / 2));
            float y = spot.Bottom + 14 + h < LH - 12 ? spot.Bottom + 14 : spot.Y - 14 - h;
            if (y < 12) y = Math.Min(LH - h - 12, spot.Y + 24);
            var card = new RectangleF(x, y, CardW, h);
            for (int i = 6; i >= 1; i--)
                using (var b = new SolidBrush(Color.FromArgb((int)(a * 0.06f), 0, 0, 0))) g.FillRectangle(b, card.X - i + 3, card.Y - i + 6, card.Width + 2 * i, card.Height + 2 * i);
            using (var p = Theme.Round(card, 5)) using (var b = new SolidBrush(Color.FromArgb(a, Theme.Screen))) g.FillPath(b, p);
            using (var p = Theme.Round(card, 5)) using (var pen = new Pen(Color.FromArgb(a, 0x5A, 0x56, 0x4F), 1f)) g.DrawPath(pen, p);
            using (var b = new SolidBrush(Color.FromArgb(a, Theme.DisplayInk)))
            {
                g.DrawString(st.Title, fTitle, b, card.X + 14, card.Y + 14);
                using (var bb = new SolidBrush(Color.FromArgb((int)(a * 0.82f), Theme.DisplayInk))) g.DrawString(st.Body, fBody, bb, new RectangleF(card.X + 14, card.Y + 40, CardW - 28, bodyH + 4));
            }
            float fy = card.Bottom - 12 - 28;
            using (var b = new SolidBrush(Color.FromArgb(a, Theme.DisplayMuted))) g.DrawString((step + 1) + " / " + steps.Count, fFoot, b, card.X + 14, fy + 7);
            bool last = step == steps.Count - 1;
            string nk = last ? "GOT IT" : "NEXT";
            var nm = g.MeasureString(nk, fKey);
            nextBox = new RectangleF(card.Right - 14 - nm.Width - 22, fy, nm.Width + 22, 28);
            using (var p = Theme.Round(new RectangleF(nextBox.X, nextBox.Y + 2, nextBox.Width, nextBox.Height), 3)) using (var b = new SolidBrush(Color.FromArgb(a, 0xB5, 0x40, 0x0F))) g.FillPath(b, p);
            using (var p = Theme.Round(nextBox, 3)) using (var b = new SolidBrush(Color.FromArgb(a, Theme.Orange))) g.FillPath(b, p);
            using (var b = new SolidBrush(Color.FromArgb(a, Theme.OnOrange))) using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center }) g.DrawString(nk, fKey, b, nextBox, sf);
            skipBox = backBox = RectangleF.Empty;
            float lx = nextBox.X - 16;
            using (var b = new SolidBrush(Color.FromArgb(a, Theme.DisplayInk))) using (var pen = new Pen(Color.FromArgb(a, Theme.DisplayInk)))
            {
                if (!last)
                {
                    var sm = g.MeasureString("Skip", fKey); skipBox = new RectangleF(lx - sm.Width, fy + 5, sm.Width, 20);
                    g.DrawString("Skip", fKey, b, skipBox.X, skipBox.Y); g.DrawLine(pen, skipBox.X + 2, skipBox.Y + 17, skipBox.Right - 2, skipBox.Y + 17);
                    lx = skipBox.X - 14;
                }
                if (step > 0)
                {
                    var bm = g.MeasureString("Back", fKey); backBox = new RectangleF(lx - bm.Width, fy + 5, bm.Width, 20);
                    g.DrawString("Back", fKey, b, backBox.X, backBox.Y); g.DrawLine(pen, backBox.X + 2, backBox.Y + 17, backBox.Right - 2, backBox.Y + 17);
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            float x = X(e), y = Y(e);
            Cursor = nextBox.Contains(x, y) || skipBox.Contains(x, y) || backBox.Contains(x, y) ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            float x = X(e), y = Y(e);
            if (nextBox.Contains(x, y)) Next();
            else if (skipBox.Contains(x, y)) Finish();
            else if (backBox.Contains(x, y)) Back();
        }

        protected override bool IsInputKey(Keys k) { return k == Keys.Left || k == Keys.Right || base.IsInputKey(k); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) Finish();
            else if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Right || e.KeyCode == Keys.Space) Next();
            else if (e.KeyCode == Keys.Left) Back();
            e.Handled = true;
        }

        protected override void Dispose(bool disposing) { if (disposing) { anim.Dispose(); behind.Dispose(); } base.Dispose(disposing); }
    }
}
