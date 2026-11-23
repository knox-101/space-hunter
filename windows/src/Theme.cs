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
        // Colour tokens. Light is the original warm-grey chassis; Dark keeps the same idea (one display, darker than
        // everything around it, orange as the only accent). Every token differs from every other in both palettes, so a
        // window that is already open can be switched by swapping colours one to one (Theme.Apply).
        public static Color Chassis, Pane, Selected, Hover, Screen, DisplayInk, DisplayMuted, DisplayTrack, DisplayUsed,
                            Ink, Muted, Soft, Line, Orange, OrangeDeep, Key, KeyShadow, Disabled, KeyOff;
        public static readonly Color OnOrange = Color.FromArgb(0x1B, 0x1B, 0x1A);   // text and ticks on orange, in both
        public static bool IsDark;

        static Color[] Light()
        {
            return new[] {
                C(0xD8D5CE), C(0xE4E1DA), C(0xE9E6E0), C(0xE0DDD6), C(0x121212), C(0xE9E6DF), C(0x8A8A86), C(0x3A3A38), C(0x6F6C66),
                C(0x1B1B1A), C(0x55524C), C(0x57534D), C(0xC3BFB6), C(0xFF5B1F), C(0x9A3409), C(0xECEAE5), C(0xA9A59C), C(0x9A968F), C(0xD0CDC6) };
        }
        static Color[] DarkSet()
        {
            return new[] {
                C(0x1E1E1C), C(0x262624), C(0x30302D), C(0x2A2A27), C(0x0B0B0A), C(0xE9E6E0), C(0x8A8A87), C(0x3A3A39), C(0x6F6C67),
                C(0xECE9E3), C(0xABA79F), C(0xA6A29A), C(0x3B3A37), C(0xFF5B1E), C(0xFF7A45), C(0x34332F), C(0x0E0E0D), C(0x6E6B65), C(0x2C2B28) };
        }
        static Color C(int rgb) { return Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF); }
        static Color[] Current()
        {
            return new[] { Chassis, Pane, Selected, Hover, Screen, DisplayInk, DisplayMuted, DisplayTrack, DisplayUsed,
                           Ink, Muted, Soft, Line, Orange, OrangeDeep, Key, KeyShadow, Disabled, KeyOff };
        }
        static void Set(Color[] p)
        {
            Chassis = p[0]; Pane = p[1]; Selected = p[2]; Hover = p[3]; Screen = p[4]; DisplayInk = p[5]; DisplayMuted = p[6];
            DisplayTrack = p[7]; DisplayUsed = p[8]; Ink = p[9]; Muted = p[10]; Soft = p[11]; Line = p[12]; Orange = p[13];
            OrangeDeep = p[14]; Key = p[15]; KeyShadow = p[16]; Disabled = p[17]; KeyOff = p[18];
        }

        // Light, Dark or System (follows Windows' "app mode"). Saved next to the settings.
        public enum Mode { System, Light, Dark }
        public static Mode Chosen = Mode.System;
        static string ModeFile { get { return Path.Combine(Engine.AppDir, "theme.txt"); } }
        public static bool SystemDark()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return k != null && k.GetValue("AppsUseLightTheme") is int && (int)k.GetValue("AppsUseLightTheme") == 0;
            }
            catch { return false; }
        }
        static bool WantDark { get { return Chosen == Mode.Dark || Chosen == Mode.System && SystemDark(); } }
        public static void LoadMode()
        {
            try { var t = File.Exists(ModeFile) ? File.ReadAllText(ModeFile).Trim() : ""; Chosen = t == "light" ? Mode.Light : t == "dark" ? Mode.Dark : Mode.System; } catch { }
            IsDark = WantDark; Set(IsDark ? DarkSet() : Light());
        }
        public static void Force(bool dark) { Chosen = dark ? Mode.Dark : Mode.Light; IsDark = dark; Set(dark ? DarkSet() : Light()); }   // test hook, not saved
        public static void SetMode(Mode m)
        {
            Chosen = m;
            try { Directory.CreateDirectory(Engine.AppDir); File.WriteAllText(ModeFile, m == Mode.Light ? "light" : m == Mode.Dark ? "dark" : "system"); } catch { }
            Apply();
        }
        // Switches every open window to the theme wanted now (after a choice, or when Windows itself changes).
        public static void Apply()
        {
            bool dark = WantDark;
            if (dark == IsDark) { foreach (Form f in Application.OpenForms) f.Invalidate(true); return; }
            var from = Current(); var to = dark ? DarkSet() : Light();
            var map = new System.Collections.Generic.Dictionary<int, Color>();
            for (int i = 0; i < from.Length; i++) map[from[i].ToArgb()] = to[i];
            IsDark = dark; Set(to);
            foreach (Form f in Application.OpenForms) { Swap(f, map); TitleBar(f); f.Invalidate(true); }
        }
        static void Swap(Control c, System.Collections.Generic.Dictionary<int, Color> map)
        {
            Color n;
            if (c.BackColor.A == 255 && map.TryGetValue(c.BackColor.ToArgb(), out n)) c.BackColor = n;
            if (map.TryGetValue(c.ForeColor.ToArgb(), out n)) c.ForeColor = n;
            var ll = c as LinkLabel;
            if (ll != null)
            {
                if (map.TryGetValue(ll.LinkColor.ToArgb(), out n)) ll.LinkColor = n;
                if (map.TryGetValue(ll.ActiveLinkColor.ToArgb(), out n)) ll.ActiveLinkColor = n;
                if (map.TryGetValue(ll.VisitedLinkColor.ToArgb(), out n)) ll.VisitedLinkColor = n;
            }
            foreach (Control k in c.Controls) Swap(k, map);
        }

        public static float S = 1f;   // UI scale: the screen's DPI / 96 (or a test override)
        public static float A = 1f;   // the screen's actual DPI / 96 (fonts in points already grow by this)

        static FontFamily display, sans, sansSemi, mono, monoSemi;
        public static void Init(float overrideScale)
        {
            LoadMode();
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

        // Names a newcomer can act on without opening anything: the first is the no-worry clean.
        public static string TierName(Level l) { return l == Level.Safe ? "Quick clean" : l == Level.Review ? "Big caches & history" : "Deep clean"; }
        public static string TierDesc(Level l) { return l == Level.Safe ? "Caches and leftovers that rebuild" : l == Level.Review ? "Big caches that download again, or old history" : "Old AI chats, old downloads, folders you added"; }
        // one plain line under each section's heading, so nobody has to guess what's in it
        public static string TierExplain(Level l)
        {
            return l == Level.Safe ? "Caches, temp files and leftovers that apps and Windows rebuild or no longer need. Your files, logins and passwords aren't touched."
                 : l == Level.Review ? "Things that download again when they're needed, or history you might still want."
                 : "Old AI agent chats and workspaces, old installers in Downloads, and folders you added. Nothing here is ticked for you.";
        }

        // the one thing to do in each section, on its own highlighted line
        public static string TierCta(Level l)
        {
            return l == Level.Safe ? "Clean this every time you open Space Hunter: it's always safe."
                 : l == Level.Review ? "Tick what you won't need soon. Each item says what comes back, and how."
                 : "This may include your work. Open each item and check before cleaning.";
        }

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            if (rad <= 0) { p.AddRectangle(r); return p; }
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }

        // The narrowest width (up to maxW) that keeps text on as few lines as at maxW: wrapped lines come out even,
        // so a second line is never one word on its own.
        public static float Balanced(Graphics g, string t, Font f, float maxW)
        {
            if (string.IsNullOrEmpty(t) || maxW <= 20) return maxW;
            float one = g.MeasureString(t, f).Width;
            if (one <= maxW) return maxW;
            float h = g.MeasureString(t, f, (int)maxW).Height;
            int lines = Math.Max(2, (int)Math.Round(h / g.MeasureString("Ag", f).Height));
            for (float w = Math.Max(20, one / lines); w < maxW; w += 2)
                if (g.MeasureString(t, f, (int)w).Height <= h + 0.5f) return Math.Min(maxW, w + 4);
            return maxW;
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
            // the same switch points as Engine.Fmt: a unit changes where the rounded number would reach 1000
            if (b >= 1073688136909L) { num = (b / 1099511627776.0).ToString("0.00"); unit = "TB"; }
            else if (b >= 1048051712L) { num = (b / 1073741824.0).ToString("0.0"); unit = "GB"; }
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
                int dark = IsDark ? 1 : 0;
                DwmSetWindowAttribute(f.Handle, 20, ref dark, 4);   // dark title-bar buttons (and Windows 10's bar)
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
        bool accent;   // a tool of its own (the Storage map): an orange outline and a small block icon
        public bool Accent { get { return accent; } set { accent = value; FitWidth(); Invalidate(); } }
        bool hover, down;

        // Busy: the key waits on a scan. It fills faintly from the left as items are measured, and a soft sheen passes
        // over it every 1.6 s, so it reads as working, not broken. Moved by the clock (~60 fps), only while busy.
        bool busy; float progress, shown;
        readonly System.Windows.Forms.Timer busyTick = new System.Windows.Forms.Timer { Interval = 16 };
        readonly System.Diagnostics.Stopwatch busyClock = new System.Diagnostics.Stopwatch();
        public bool Busy
        {
            get { return busy; }
            set { if (value == busy) return; busy = value; if (busy) { shown = 0; busyClock.Restart(); busyTick.Start(); } else busyTick.Stop(); Invalidate(); }
        }
        public float Progress { set { progress = Math.Max(0, Math.Min(1, value)); } }

        public Key(string text, bool primary = false)
        {
            busyTick.Tick += (s, e) => { shown += (progress - shown) * 0.12f; Invalidate(); };
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
            using (var g = CreateGraphics()) { Theme.Hq(g); Width = (int)Math.Ceiling(g.MeasureString(Text.ToUpperInvariant(), Font).Width) + Theme.D(accent ? 54 : 30); }
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
            Color shadow = Toggled ? Color.Black : Primary ? Color.FromArgb(0xB5, 0x40, 0x0F) : Theme.KeyShadow;
            Color ink = Toggled ? Theme.Pane : Primary ? Theme.OnOrange : Theme.Ink;
            if (!Enabled) { fill = Theme.KeyOff; shadow = Theme.Line; ink = Theme.Disabled; }
            else if (hover && !Toggled) fill = ControlPaint.Light(fill, 0.25f);
            if (press == 0)
                using (var p = Theme.Round(new RectangleF(0.5f, 0.5f + lift, Width - 1, Height - 1 - lift - 1), rad)) using (var b = new SolidBrush(shadow)) g.FillPath(b, p);
            using (var p = Theme.Round(face, rad)) using (var b = new SolidBrush(fill)) g.FillPath(b, p);
            if (busy && !Enabled)
                using (var p = Theme.Round(face, rad))
                {
                    var clip = g.Clip; g.SetClip(p, CombineMode.Intersect);
                    using (var b = new SolidBrush(Color.FromArgb(Theme.IsDark ? 46 : 38, Theme.Orange))) g.FillRectangle(b, face.X, face.Y, face.Width * shown, face.Height);
                    float band = face.Width * 0.35f, t = (float)(busyClock.Elapsed.TotalSeconds % 1.6 / 1.6);
                    float x = face.X - band + (face.Width + band) * (1 - (1 - t) * (1 - t));   // ease-out across, then again
                    var r = new RectangleF(x, face.Y, band, face.Height);
                    using (var lg = new LinearGradientBrush(new RectangleF(r.X - 1, r.Y, r.Width + 2, r.Height), Color.Transparent, Color.Transparent, LinearGradientMode.Horizontal))
                    {
                        var light = Color.FromArgb(Theme.IsDark ? 22 : 70, Color.White);
                        lg.InterpolationColors = new ColorBlend { Colors = new[] { Color.FromArgb(0, light), light, Color.FromArgb(0, light) }, Positions = new[] { 0f, 0.5f, 1f } };
                        g.FillRectangle(lg, r);
                    }
                    g.Clip = clip;
                }
            var tr = new RectangleF(face.X, face.Y + s, face.Width, face.Height);
            if (accent && Enabled)
            {
                using (var p = Theme.Round(RectangleF.Inflate(face, -1 * s, -1 * s), rad)) using (var pen = new Pen(Theme.Orange, 2 * s)) g.DrawPath(pen, p);
                // four blocks of different sizes: a tiny picture of the map
                float ix = face.X + 14 * s, iy = face.Y + face.Height / 2 - 6 * s + s, u = s;
                using (var b = new SolidBrush(ink))
                {
                    g.FillRectangle(b, ix, iy, 7 * u, 12 * u);
                    g.FillRectangle(b, ix + 8 * u, iy, 6 * u, 7 * u);
                    g.FillRectangle(b, ix + 8 * u, iy + 8 * u, 3 * u, 4 * u);
                }
                using (var b = new SolidBrush(Theme.Orange)) g.FillRectangle(b, ix + 12 * u, iy + 8 * u, 2 * u, 4 * u);
                tr = new RectangleF(face.X + 24 * s, face.Y + s, face.Width - 24 * s, face.Height);
            }
            using (var b = new SolidBrush(ink))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(Text.ToUpperInvariant(), Font, b, tr, sf);
        }
    }

    class DoubleBufferedPanel : Panel
    {
        public DoubleBufferedPanel() { DoubleBuffered = true; ResizeRedraw = true; }
    }
}
