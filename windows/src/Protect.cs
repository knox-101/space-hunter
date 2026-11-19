// Copyright (c) 2026 Blesson Reji (knox-101). Space Hunter is free software under the GNU GPL v3 or later; see LICENSE.
// SPDX-License-Identifier: GPL-3.0-or-later
// Space Hunter (Windows) - protected folders: folders that are never cleaned, whatever is ticked.
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SpaceHunter
{
    class ProtectForm : Form
    {
        readonly CanvasPanel head = new CanvasPanel();
        readonly FlowLayoutPanel rows = new FlowLayoutPanel();
        readonly Key addKey = new Key("add folder", true);
        public bool Changed;
        Font fLabel = Theme.CSemi(10f), fBig = Theme.CDisplay(40f), fSub = Theme.CSans(10f);

        public ProtectForm()
        {
            Theme.Window(this, "Space Hunter  ·  protected folders", 760, 640);
            MinimumSize = SizeFromClientSize(Theme.Fit(560, 420));
            StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;

            head.Dock = DockStyle.Top; head.Height = Theme.D(140); head.Draw += PaintHead;
            var headWrap = new Panel { Dock = DockStyle.Top, Height = Theme.D(140 + 18), Padding = Theme.Pad(18, 18, 18, 0), BackColor = Theme.Chassis };
            headWrap.Controls.Add(head);
            var keys = new Panel { Dock = DockStyle.Top, Height = Theme.D(64), BackColor = Theme.Chassis };
            addKey.Location = Theme.P(18, 12); keys.Controls.Add(addKey);
            var note = new Label { Text = "Anything inside a protected folder is skipped by every item, every time.", AutoSize = true, Font = Theme.Sans(9.5f), ForeColor = Theme.Muted, UseMnemonic = false };
            keys.Controls.Add(note); keys.Resize += (s, e) => note.Location = new Point(addKey.Right + Theme.D(16), Theme.D(22));

            rows.Dock = DockStyle.Fill; rows.FlowDirection = FlowDirection.TopDown; rows.WrapContents = false; rows.AutoScroll = true;
            rows.Padding = Theme.Pad(18, 6, 18, 18); rows.BackColor = Theme.Chassis;
            rows.HandleCreated += (s, e) => Theme.SetWindowTheme(rows.Handle, "Explorer", null);
            Controls.Add(rows); Controls.Add(keys); Controls.Add(headWrap);

            addKey.Click += (s, e) =>
            {
                var p = FolderPicker.Pick(Handle, "Choose a folder to protect");
                if (p == null) return;
                Keep.Add(p); Changed = true; Fill();
            };
            Resize += (s, e) => Fill();
            Shown += (s, e) => Fill();
        }

        void PaintHead(object s, PaintEventArgs e)
        {
            var g = e.Graphics; float W = head.LW, H = head.LH, pad = 20;
            g.Clear(Theme.Chassis);
            using (var p = Theme.Round(new RectangleF(0.5f, 0.5f, W - 1, H - 1), 7)) using (var b = new SolidBrush(Theme.Screen)) g.FillPath(b, p);
            using (var b = new SolidBrush(Color.FromArgb(0xB4, 0xB1, 0xAA))) g.DrawString("Protected folders  ·  never cleaned, whatever is ticked", fLabel, b, pad, 12);
            // never "0 folders": the built-in protection is always there, even before you add your own
            int n = Keep.All.Count;
            using (var b = new SolidBrush(Theme.Orange)) g.DrawString(BuiltIn.Length + " BUILT IN + " + n + " YOURS", fBig, b, pad - 5, 28);
            using (var b = new SolidBrush(Theme.DisplayMuted)) g.DrawString("The places Space Hunter never touches, plus any folder you add.", fSub, b, pad, 104);
        }

        void Fill()
        {
            rows.SuspendLayout();
            rows.Controls.Clear();
            int w = Math.Max(Theme.D(300), rows.ClientSize.Width - rows.Padding.Horizontal - Theme.D(4));
            rows.Controls.Add(Heading("Your protected folders"));
            var mine = Keep.All;
            if (mine.Count == 0) rows.Controls.Add(Text_("None yet. Add a folder above, or press Keep on a folder in an item's panel.", Theme.Soft, w));
            foreach (var path in mine)
            {
                var p = path;
                var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Theme.Pad(0, 2, 0, 2), BackColor = Theme.Chassis };
                var rm = new Key("remove") { Height = Theme.D(30) }; rm.Font = Theme.Semi(8.5f);
                rm.Click += (s, e) => { Keep.Remove(p); Changed = true; Fill(); head.Invalidate(); };
                var link = new LinkLabel { Text = Theme.MidPath(p, 70), AutoSize = true, Font = Theme.Sans(10f), LinkColor = Theme.Ink, ActiveLinkColor = Theme.OrangeDeep, VisitedLinkColor = Theme.Ink,
                                           Margin = Theme.Pad(10, 7, 0, 0), LinkBehavior = LinkBehavior.HoverUnderline, UseMnemonic = false };
                link.LinkClicked += (s, e) => { try { System.Diagnostics.Process.Start("explorer.exe", "\"" + p + "\""); } catch { } };
                if (!Directory.Exists(p)) link.Text += "   (not found)";
                row.Controls.Add(rm); row.Controls.Add(link);
                rows.Controls.Add(row);
            }
            rows.Controls.Add(Heading("Built in"));
            foreach (var line in BuiltIn)
                rows.Controls.Add(Text_(line, Theme.Muted, w));
            rows.ResumeLayout();
            head.Invalidate();
        }

        static readonly string[] BuiltIn = {
            "Documents, Desktop, Pictures, Videos, Music, Favorites and OneDrive",
            "Downloads, except old installers and archives, and those only when you tick that item",
            "Your code and project folders (source, Projects)",
            "Keys and sign-in settings (.ssh, .gnupg, .aws, .azure, .kube, .docker)",
            "AI agents' memory folders (for example Claude Code's memory)",
            "Windows itself (only its temp, cache and log folders are cleaned)",
            "Anything reached through a shortcut-style link (links are never followed)",
            "Space Hunter's own folder: the app, its settings and its log" };

        Label Heading(string t)
        {
            return new Label { Text = t, AutoSize = true, Font = Theme.Semi(10.5f), ForeColor = Theme.Ink, Margin = Theme.Pad(0, 14, 0, 6), UseMnemonic = false };
        }
        Label Text_(string t, Color c, int w)
        {
            return new Label { Text = t, AutoSize = true, MaximumSize = new Size(w, 0), Font = Theme.Sans(10f), ForeColor = c, Margin = Theme.Pad(0, 3, 0, 3), UseMnemonic = false };
        }
    }

    // The modern Windows folder picker (IFileOpenDialog in folder mode), not the old tree dialog.
    static class FolderPicker
    {
        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")] class FileOpenDialogCom { }

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint c, IntPtr f);
            void SetFileTypeIndex(uint i);
            void GetFileTypeIndex(out uint i);
            void Advise(IntPtr p, out uint c);
            void Unadvise(uint c);
            void SetOptions(uint fos);
            void GetOptions(out uint fos);
            void SetDefaultFolder(IShellItem si);
            void SetFolder(IShellItem si);
            void GetFolder(out IShellItem si);
            void GetCurrentSelection(out IShellItem si);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string n);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string n);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string t);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string t);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string t);
            void GetResult(out IShellItem si);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem si);
            void GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
        }

        public static string Pick(IntPtr owner, string title)
        {
            try
            {
                var dlg = (IFileDialog)new FileOpenDialogCom();
                uint opts; dlg.GetOptions(out opts);
                dlg.SetOptions(opts | 0x20 /* pick folders */ | 0x40 /* file system only */);
                dlg.SetTitle(title); dlg.SetOkButtonLabel("Protect");
                if (dlg.Show(owner) != 0) return null;   // cancelled
                IShellItem item; dlg.GetResult(out item);
                string path; item.GetDisplayName(0x80058000 /* SIGDN_FILESYSPATH */, out path);
                return path;
            }
            catch { return null; }
        }
    }
}
