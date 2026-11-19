// Copyright (c) 2026 Blesson Reji (knox-101). Space Hunter is free software under the GNU GPL v3 or later; see LICENSE.
// SPDX-License-Identifier: GPL-3.0-or-later
// Space Hunter (Windows) - theme, fonts, display scaling and the shared controls.
// Direction "Instrument + Strata" (design/space-hunter/JOB.md): warm-grey chassis, one dark display,
// orange as the only accent, square hardware keys. Fonts are embedded (SIL OFL).
//
// Display scaling: every drawn surface (Canvas) is painted in 96-dpi "design units" and scaled as a whole
// by Theme.S, so text and geometry grow together at 125%, 150%, 200%. Ordinary controls get their sizes
// through Theme.D(). "--scale 1.5" forces a scale for testing without changing Windows settings.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SpaceHunter
{
    static class Fonts
    {
        static readonly PrivateFontCollection pfc = new PrivateFontCollection();
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        static extern int AddFontResourceEx(string name, uint fl, IntPtr res);

        // Fonts ship inside the exe; copy them to %LOCALAPPDATA%\SpaceHunter\fonts and register them privately.
        public static void Init()
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                string dir = Path.Combine(Engine.AppDir, "fonts");
                Directory.CreateDirectory(dir);
                foreach (var name in asm.GetManifestResourceNames().Where(n => n.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)))
                {
                    string path = Path.Combine(dir, name);
                    using (var s = asm.GetManifestResourceStream(name))
                    {
                        if (!File.Exists(path) || new FileInfo(path).Length != s.Length)
                            using (var f = File.Create(path)) s.CopyTo(f);
                    }
                    pfc.AddFontFile(path);
                    AddFontResourceEx(path, 0x10 /* FR_PRIVATE */, IntPtr.Zero);
                }
            }
            catch { }
        }

        public static FontFamily Family(string name, string fallback)
        {
            foreach (var f in pfc.Families) if (f.Name == name) return f;
            try { return new FontFamily(fallback); } catch { return FontFamily.GenericSansSerif; }
        }
    }

    static class Theme
    {
        public static readonly Color Chassis = Color.FromArgb(0xD8, 0xD5, 0xCE);
        public static readonly Color Pane = Color.FromArgb(0xE4, 0xE1, 0xDA);
        public static readonly Color Selected = Color.FromArgb(0xE9, 0xE6, 0xE0);
        public static readonly Color Hover = Color.FromArgb(0xE0, 0xDD, 0xD6);
        public static readonly Color Screen = Color.FromArgb(0x12, 0x12, 0x12);
        public static readonly Color DisplayInk = Color.FromArgb(0xE9, 0xE6, 0xDF);
        public static readonly Color DisplayMuted = Color.FromArgb(0x8A, 0x8A, 0x86);
        public static readonly Color DisplayTrack = Color.FromArgb(0x3A, 0x3A, 0x38);
        public static readonly Color DisplayUsed = Color.FromArgb(0x6F, 0x6C, 0x66);
        public static readonly Color Ink = Color.FromArgb(0x1B, 0x1B, 0x1A);
        public static readonly Color Muted = Color.FromArgb(0x5F, 0x5C, 0x56);
        public static readonly Color Soft = Color.FromArgb(0x7D, 0x79, 0x72);
        public static readonly Color Line = Color.FromArgb(0xC3, 0xBF, 0xB6);
        public static readonly Color Orange = Color.FromArgb(0xFF, 0x5B, 0x1F);
        public static readonly Color OrangeDeep = Color.FromArgb(0xB5, 0x40, 0x0F);
        public static readonly Color Key = Color.FromArgb(0xEC, 0xEA, 0xE5);
        public static readonly Color KeyShadow = Color.FromArgb(0xA9, 0xA5, 0x9C);
        public static readonly Color Disabled = Color.FromArgb(0x9A, 0x96, 0x8F);

        public static float S = 1f;   // UI scale: the screen's DPI / 96 (or a test override)
        public static float A = 1f;   // the screen's actual DPI / 96 (fonts in points already grow by this)

        static FontFamily display, sans, sansSemi, mono, monoSemi;
        public static void Init(float overrideScale)
        {
            Fonts.Init();
            try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) A = g.DpiX / 96f; } catch { A = 1f; }
            S = overrideScale > 0 ? overrideScale : A;
            display = Fonts.Family("Big Shoulders Display Black", "Bahnschrift");
            sans = Fonts.Family("Schibsted Grotesk", "Segoe UI");
            sansSemi = Fonts.Family("Schibsted Grotesk SemiBold", "Segoe UI Semibold");
            mono = Fonts.Family("IBM Plex Mono", "Consolas");
            monoSemi = Fonts.Family("IBM Plex Mono SemiBold", "Consolas");
        }

        public static int D(float v) { return (int)Math.Round(v * S); }
        public static Size D(int w, int h) { return new Size(D(w), D(h)); }
        public static Point P(int x, int y) { return new Point(D(x), D(y)); }
        public static Padding Pad(int l, int t, int r, int b) { return new Padding(D(l), D(t), D(r), D(b)); }

        // A window's client size: the design size scaled, but never bigger than the screen it opens on.
        public static Size Fit(int w, int h)
        {
            var wa = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
            return new Size(Math.Min(D(w), (int)(wa.Width * 0.94)), Math.Min(D(h), (int)(wa.Height * 0.9)));
        }

        static Font Make(FontFamily f, float size, FontStyle st)
        {
            if (!f.IsStyleAvailable(st)) st = FontStyle.Regular;
            return new Font(f, Math.Max(1f, size), st, GraphicsUnit.Point);
        }
        // fonts for ordinary controls (labels, keys, links): they grow with the UI scale
        public static Font Display(float s) { return Make(display, s * S / A, FontStyle.Regular); }
        public static Font Sans(float s) { return Make(sans, s * S / A, FontStyle.Regular); }
        public static Font Bold(float s) { return Make(sans, s * S / A, FontStyle.Bold); }
        public static Font Semi(float s) { return Make(sansSemi, s * S / A, FontStyle.Regular); }
        public static Font Mono(float s) { return Make(mono, s * S / A, FontStyle.Regular); }
        public static Font MonoSemi(float s) { return Make(monoSemi, s * S / A, FontStyle.Regular); }
        // fonts for drawn canvases, which are scaled as a whole
        public static Font CDisplay(float s) { return Make(display, s / A, FontStyle.Regular); }
        public static Font CSans(float s) { return Make(sans, s / A, FontStyle.Regular); }
        public static Font CBold(float s) { return Make(sans, s / A, FontStyle.Bold); }
        public static Font CSemi(float s) { return Make(sansSemi, s / A, FontStyle.Regular); }
        public static Font CMono(float s) { return Make(mono, s / A, FontStyle.Regular); }
        public static Font CMonoSemi(float s) { return Make(monoSemi, s / A, FontStyle.Regular); }

        public static string TierName(Level l) { return l == Level.Safe ? "Safe to delete" : l == Level.Review ? "Review first" : "Caution"; }
        public static string TierDesc(Level l) { return l == Level.Safe ? "Rebuilds itself" : l == Level.Review ? "Re-downloads or history" : "Can hold your history or work"; }

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            if (rad <= 0) { p.AddRectangle(r); return p; }
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }

        public static void Hq(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        }

        public static void Hatch(Graphics g, RectangleF r, Color ink, Color ground)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using (var b = new SolidBrush(ground)) g.FillRectangle(b, r);
            var st = g.Save();
            g.SetClip(r);
            using (var pen = new Pen(ink, 1.5f))
                for (float x = r.X - r.Height; x < r.Right; x += 5) g.DrawLine(pen, x, r.Bottom, x + r.Height, r.Y);
            g.Restore(st);
        }

        // The mark for each section, readable without colour: Safe none, Review a hatched bar, Caution a solid bar.
        public static void LevelMark(Graphics g, RectangleF r, Level level, Color ink, Color ground)
        {
            if (level == Level.Review) Hatch(g, r, ink, ground);
            else if (level == Level.Caution) using (var b = new SolidBrush(ink)) g.FillRectangle(b, r);
        }

        // Shorten a path in the middle, keeping the last part whole and a fixed-length start.
        public static string MidPath(string p, int max)
        {
            if (p.StartsWith(Engine.H, StringComparison.OrdinalIgnoreCase)) p = "~" + p.Substring(Engine.H.Length);
            if (p.Length <= max) return p;
            string name = Path.GetFileName(p.TrimEnd('\\'));
            if (name.Length + 4 >= max) return "...\\" + (name.Length > max - 4 ? name.Substring(0, max - 7) + "..." : name);
            int head = Math.Min(20, Math.Max(4, max - name.Length - 3));
            int tail = max - head - 3;
            return p.Substring(0, head) + "..." + p.Substring(p.Length - tail);
        }

        // "34.8" + "GB"
        public static void SizeParts(long b, out string num, out string unit)
        {
            if (b >= 1L << 40) { num = (b / 1099511627776.0).ToString("0.00"); unit = "TB"; }
            else if (b >= 1L << 30) { num = (b / 1073741824.0).ToString("0.0"); unit = "GB"; }
            else if (b >= 1L << 20) { num = (b / 1048576.0).ToString("0"); unit = "MB"; }
            else if (b > 0) { num = (b / 1024.0).ToString("0"); unit = "KB"; }
            else { num = "0"; unit = "GB"; }
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] public static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

        // Windows 11: paint the title bar in the chassis colour.
        public static void TitleBar(Form f)
        {
            try
            {
                int cap = Chassis.R | (Chassis.G << 8) | (Chassis.B << 16);
                int txt = Ink.R | (Ink.G << 8) | (Ink.B << 16);
                int border = Line.R | (Line.G << 8) | (Line.B << 16);
                DwmSetWindowAttribute(f.Handle, 35, ref cap, 4);
                DwmSetWindowAttribute(f.Handle, 36, ref txt, 4);
                DwmSetWindowAttribute(f.Handle, 34, ref border, 4);
            }
            catch { }
        }

        // Common window setup: no WinForms auto-scaling (Space Hunter scales itself), chassis colour, icon.
        public static void Window(Form f, string title, int w, int h)
        {
            f.AutoScaleMode = AutoScaleMode.None;
            f.Text = title; f.BackColor = Chassis; f.Font = Sans(10f);
            f.ClientSize = Fit(w, h);
            try { f.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            f.HandleCreated += (s, e) => TitleBar(f);
        }
    }

    // A surface painted in 96-dpi design units; the whole drawing is scaled by Theme.S.
    class Canvas : Control
    {
        public Canvas()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        }
        public float LW { get { return ClientSize.Width / Theme.S; } }
        public float LH { get { return ClientSize.Height / Theme.S; } }
        protected static float X(MouseEventArgs e) { return e.X / Theme.S; }
        protected static float Y(MouseEventArgs e) { return e.Y / Theme.S; }
        protected static Graphics Begin(PaintEventArgs e)
        {
            var g = e.Graphics; Theme.Hq(g); g.ScaleTransform(Theme.S, Theme.S); return g;
        }
    }

    // A canvas whose drawing is supplied by a handler (for the displays inside dialogs).
    class CanvasPanel : Canvas
    {
        public event PaintEventHandler Draw;
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Begin(e);
            if (Draw != null) Draw(this, new PaintEventArgs(g, e.ClipRectangle));
        }
    }

    // A square hardware key. Light by default, orange when Primary, dark when Toggled (a lit segment).
    class Key : Control
    {
        public bool Primary, Toggled;
        bool hover, down;
        public Key(string text, bool primary = false)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            Font = Theme.Semi(9.75f);
            Primary = primary; Cursor = Cursors.Hand; Height = Theme.D(36);
            Text = text;
            FitWidth();
        }
        public void FitWidth()
        {
            using (var g = CreateGraphics()) { Theme.Hq(g); Width = (int)Math.Ceiling(g.MeasureString(Text.ToUpperInvariant(), Font).Width) + Theme.D(30); }
        }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); FitWidth(); Invalidate(); }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); FitWidth(); Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Theme.Hq(g);
            float s = Theme.S, lift = 2 * s, press = down && Enabled ? lift : 0, rad = 4 * s;
            var face = new RectangleF(0.5f, 0.5f + press, Width - 1, Height - 1 - lift - 1);
            Color fill = Toggled ? Theme.Ink : Primary ? Theme.Orange : Theme.Key;
            Color shadow = Toggled ? Color.Black : Primary ? Theme.OrangeDeep : Theme.KeyShadow;
            Color ink = Toggled ? Theme.DisplayInk : Theme.Ink;
            if (!Enabled) { fill = Color.FromArgb(0xD0, 0xCD, 0xC6); shadow = Theme.Line; ink = Theme.Disabled; }
            else if (hover && !Toggled) fill = ControlPaint.Light(fill, 0.25f);
            if (press == 0)
                using (var p = Theme.Round(new RectangleF(0.5f, 0.5f + lift, Width - 1, Height - 1 - lift - 1), rad)) using (var b = new SolidBrush(shadow)) g.FillPath(b, p);
            using (var p = Theme.Round(face, rad)) using (var b = new SolidBrush(fill)) g.FillPath(b, p);
            using (var b = new SolidBrush(ink))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(Text.ToUpperInvariant(), Font, b, new RectangleF(face.X, face.Y + s, face.Width, face.Height), sf);
        }
    }

    class DoubleBufferedPanel : Panel
    {
        public DoubleBufferedPanel() { DoubleBuffered = true; ResizeRedraw = true; }
    }
}
