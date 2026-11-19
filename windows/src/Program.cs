// Copyright (c) 2026 Blesson Reji (knox-101). Space Hunter is free software under the GNU GPL v3 or later; see LICENSE.
// SPDX-License-Identifier: GPL-3.0-or-later
// Space Hunter (Windows) - entry point.
//   SpaceHunter.exe                     the app (asks for admin rights at launch)
//   SpaceHunter.exe --auto              clean the ticked "Quick clean" items WITHOUT asking, log, exit
//   SpaceHunter.exe --report            print a scan, delete nothing
//   SpaceHunter.exe --version           print the version
// Console output is only visible when redirected from an Administrator terminal (it's a windowed app):
//   SpaceHunter.exe --report > report.txt
// Test hooks (used to check the design): --snapshot <png>, --scale <1.25>, --open <item id>, --test-confirm,
// --test-progress <folder> [--bin], --test-finder <folder>, --test-protect, --frames <dir>.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace SpaceHunter
{
    static class Program
    {
        // real screen pixels, so overlapping panels and the tinted title bar are captured as they look
        public static void Snap(Form f, string path)
        {
            f.TopMost = true; f.Activate(); f.Refresh(); Application.DoEvents(); Thread.Sleep(250); Application.DoEvents();
            var b = f.Bounds;
            using (var bmp = new Bitmap(b.Width, b.Height)) { using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(b.Location, Point.Empty, b.Size); bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png); }
        }

        static string Arg(string[] args, string name)
        {
            int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        static bool Has(string[] args, string name) { return args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase)); }

        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string n, uint a, uint s, IntPtr sec, uint d, uint f, IntPtr t);
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        struct BY_HANDLE_FILE_INFORMATION { public uint Attr; public long C, A, W; public uint Vol, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
        [DllImport("kernel32.dll")] static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle h, out BY_HANDLE_FILE_INFORMATION i);
        // test hook: the NTFS record number of a file or folder
        static long FileRecord(string path)
        {
            using (var h = CreateFileW(path, 0x80 /* read attributes */, 7, IntPtr.Zero, 3, 0x02000000 /* folders too */, IntPtr.Zero))
            {
                BY_HANDLE_FILE_INFORMATION i;
                if (h.IsInvalid || !GetFileInformationByHandle(h, out i)) return -1;
                return (((long)i.IndexHigh << 32) | i.IndexLow) & 0xFFFFFFFFFFFFL;
            }
        }

        [STAThread]
        static int Main(string[] args)
        {
            // long paths (past the old 260-character limit) work instead of throwing; must be set before any file access
            AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
            AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
            if (Has(args, "--version")) { Console.WriteLine("Space Hunter " + AppVersion.Text); return 0; }
            string pretend = Arg(args, "--test-check");   // test hook: check for an update as if this were an older version
            if (pretend != null)
            {
                Updater.Pretend = Version.Parse(pretend);
                var r = Updater.Check();
                Console.WriteLine(r == null ? "no update found" : "update found: " + r.Version + "  " + r.ExeUrl);
                return 0;
            }
            string verifyFile = Arg(args, "--test-verify");   // test hook: --test-verify <file> <sig> <public-key.xml>
            if (verifyFile != null)
            {
                var rest = args.SkipWhile(a => a != "--test-verify").Skip(1).ToArray();
                bool ok = rest.Length >= 3 && Updater.Verify(File.ReadAllBytes(rest[0]), File.ReadAllBytes(rest[1]), File.ReadAllText(rest[2]));
                Console.WriteLine(ok ? "SIGNATURE OK" : "SIGNATURE BAD");
                return ok ? 0 : 1;
            }
            string mftDrive = Arg(args, "--test-mft");   // test hook: measure a drive both ways and compare, into a file
            if (mftDrive != null)
            {
                var o = new System.Text.StringBuilder();
                Func<FolderNode, string> top = n => string.Join(Environment.NewLine, n.Kids.Take(12).Select(k => string.Format("  {0,-40} {1,10} {2,12:N0} files", k.Name, Engine.Fmt(k.Size), k.Files)));
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var a = BigFolders.Scan(mftDrive); var ta = sw.Elapsed.TotalSeconds;
                    o.AppendLine(string.Format("drive index: {0} ({1:N2} s){2}  total {3}, {4:N0} files", BigFolders.UsedIndex ? "used, " + Mft.LastInfo : "NOT used: " + BigFolders.IndexError, ta, Environment.NewLine, Engine.Fmt(a.Size), a.Files));
                    o.AppendLine(top(a));
                    var t = Mft.LastTable;
                    if (t != null)
                    {
                        o.AppendLine(string.Format("records: {0:N0} used, {1:N0} free, {2:N0} failed the torn-write check", t.Used, t.Free, t.BadFixup));
                        // files directly in a folder that one method sees and the other doesn't
                        Func<FolderNode, string, FolderNode> find = (r, path) => {
                            var cur = r;
                            foreach (var part in path.Substring(3).Split(new[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
                            { cur = cur == null ? null : cur.Kids.FirstOrDefault(k => string.Equals(k.Name, part, StringComparison.OrdinalIgnoreCase)); }
                            return cur; };
                        foreach (var lp in args.SkipWhile(q => q != "--list").Skip(1).TakeWhile(q => !q.StartsWith("--")))
                        {
                            long fr = FileRecord(lp);
                            var idx = new HashSet<string>(fr < 0 ? new string[0] : t.FilesIn((int)fr), StringComparer.OrdinalIgnoreCase);
                            string[] disk; try { disk = Directory.GetFiles(lp).Select(Path.GetFileName).ToArray(); } catch { disk = new string[0]; }
                            var fn = find(a, lp);
                            o.AppendLine("list " + lp + " (record " + fr + "): index " + idx.Count + ", disk " + disk.Length + ", index node own files " + (fn == null ? -1 : fn.OwnFiles)
                                + ", kids index/disk " + (fn == null ? -1 : fn.Kids.Count) + "/" + (Directory.Exists(lp) ? Directory.GetDirectories(lp).Length : -1));
                            foreach (var d in disk.Where(d => !idx.Contains(d)).Take(10)) o.AppendLine("   only on disk: " + d);
                            foreach (var d in idx.Where(d => !disk.Contains(d, StringComparer.OrdinalIgnoreCase)).Take(10)) o.AppendLine("   only in index: " + d);
                            if (fn != null && Directory.Exists(lp))
                                foreach (var sd in Directory.GetDirectories(lp).Select(Path.GetFileName).Where(sd => !fn.Kids.Any(k => string.Equals(k.Name, sd, StringComparison.OrdinalIgnoreCase))).Take(10))
                                    o.AppendLine("   folder only on disk: " + sd);
                        }
                        foreach (var probe in args.SkipWhile(q => q != "--probe").Skip(1).TakeWhile(q => !q.StartsWith("--")))
                        {
                            long rec = FileRecord(probe);
                            o.AppendLine("probe " + probe + " -> " + (rec < 0 ? "could not open" : t.Describe((int)rec)));
                            if (rec >= 0) { var par = t.Describe((int)rec); }
                        }
                        o.AppendLine("extra hard links (counted once above, once per link folder by folder):");
                        foreach (var g in t.Links.Select(l => new { rec = (int)(l >> 32), top = t.TopOf((int)(l & 0xFFFFFFFF)) })
                                                 .Where(x => x.top != null).GroupBy(x => x.top.Name).OrderByDescending(g => g.Sum(x => t.SizeOf(x.rec))).Take(8))
                            o.AppendLine(string.Format("  {0,-40} {1,10} {2,12:N0} links", g.Key, Engine.Fmt(g.Sum(x => t.SizeOf(x.rec))), g.Count()));
                    }
                    sw.Restart();
                    var b = BigFolders.ScanFolders(mftDrive); var tb = sw.Elapsed.TotalSeconds;
                    o.AppendLine(string.Format("folder by folder: ({0:N2} s){1}  total {2}, {3:N0} files", tb, Environment.NewLine, Engine.Fmt(b.Size), b.Files));
                    o.AppendLine(top(b));
                    // follow the biggest difference in file count downwards, to see where the two disagree
                    o.AppendLine("where they differ most (index files / folder-by-folder files):");
                    string from = Arg(args, "--trace") ?? "";
                    FolderNode ni = a.Kids.FirstOrDefault(k => k.Name == from) ?? a, nf = b.Kids.FirstOrDefault(k => k.Name == from) ?? b;
                    for (int depth = 0; depth < 8 && ni != null && nf != null; depth++)
                    {
                        var pairs = nf.Kids.Select(k => new { k, m = ni.Kids.FirstOrDefault(j => string.Equals(j.Name, k.Name, StringComparison.OrdinalIgnoreCase)) })
                                          .OrderByDescending(p => Math.Abs(p.k.Files - (p.m == null ? 0 : p.m.Files))).Take(3).ToList();
                        foreach (var p in pairs) o.AppendLine(string.Format("  {0}{1}: {2:N0} / {3:N0} files, own files {4:N0} / {5:N0}", new string(' ', depth * 2), p.k.Path, p.m == null ? -1 : p.m.Files, p.k.Files, p.m == null ? -1 : p.m.OwnFiles, p.k.OwnFiles));
                        if (pairs.Count == 0) break;
                        nf = pairs[0].k; ni = pairs[0].m;
                    }
                }
                catch (Exception e) { o.AppendLine("ERROR " + e); }
                File.WriteAllText(Arg(args, "--out") ?? Path.Combine(Engine.AppDir, "mft-test.txt"), o.ToString());
                return 0;
            }
            if (Has(args, "--test-canadd"))   // test hook: print the add-to-cleanup-list decision for each path
            {
                foreach (var p in args.SkipWhile(a => a != "--test-canadd").Skip(1)) { string why; bool ok = Engine.CanAdd(p, out why); Console.WriteLine((ok ? "ALLOW  " : "REFUSE ") + p + (ok ? "" : "   (" + why + ")")); }
                return 0;
            }
            bool auto = Has(args, "--auto"), report = Has(args, "--report");
            if (auto || report)
            {
                var all = new Counter();
                var results = Engine.Build().Where(c => !auto || (c.Safe && c.Checked))
                    .AsParallel().AsOrdered().WithDegreeOfParallelism(4)
                    .Select(c => { var cnt = new Counter(); Engine.Process(c, auto, cnt); return new { c, cnt }; }).ToList();
                foreach (var r in results)
                {
                    all.Files += r.cnt.Files; all.Bytes += r.cnt.Bytes; all.Skipped += r.cnt.Skipped;
                    Console.WriteLine("{0,-52} {1,10:N0} files {2,10}  {3,-8} {4}", r.c.Name, r.cnt.Files, Engine.Fmt(r.cnt.Bytes), r.c.Level, Engine.FmtAge(r.c.AgeMinutes));
                }
                string line = (auto ? "AUTO-CLEANED " : "REPORT ") + Engine.Fmt(all.Bytes) + ", " + all.Files.ToString("N0") + " files; skipped " + all.Skipped.ToString("N0");
                Console.WriteLine(line);
                if (auto) Engine.Log(line);
                return 0;
            }

            string sysIds = Arg(args, "--clean-system");   // elevated helper (only used if the app wasn't started as admin)
            if (sysIds != null)
            {
                var ids = sysIds.Split(',');
                var c2 = new Counter();
                foreach (var c in Engine.Build().Where(x => ids.Contains(x.Id) && x.AdminOnly)) Engine.Process(c, true, c2);
                try { File.WriteAllText(Path.Combine(Engine.AppDir, "system-result.txt"), c2.Bytes + "|" + c2.Files + "|" + c2.Skipped); } catch { }
                return 0;
            }

            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            float scale; if (!float.TryParse(Arg(args, "--scale") ?? "", NumberStyles.Float, CultureInfo.InvariantCulture, out scale)) scale = 0;
            Theme.Init(scale);
            if (Arg(args, "--theme") != null) Theme.Force(Arg(args, "--theme") == "dark");   // test hook for screenshots
            string snap = Arg(args, "--snapshot");

            // elevated with another account's password: every user folder would be that account's, so stop
            string sessionUser, me;
            if (Engine.RunningAsOtherUser(out sessionUser, out me))
            {
                Application.Run(new NoticeForm("Space Hunter is running as " + me + ", not " + sessionUser,
                    "Windows started it with the administrator account \u201C" + me + "\u201D, so it would clean that account's files instead of yours. " +
                    "Nothing has been scanned or deleted. To clean " + sessionUser + "'s files, sign in to an administrator account of your own and open Space Hunter there."));
                return 0;
            }

            string testDir = Arg(args, "--test-progress");
            if (testDir != null)
            {
                var c = new Category { Id = "test", Name = "Test folder", Level = Level.Safe, Kind = Kind.Contents, Checked = true, Recycle = Has(args, "--bin") };
                c.Roots.Add(testDir);
                if (Arg(args, "--pick") != null) { c.Checked = false; foreach (var pp in Arg(args, "--pick").Split('|')) c.Picks.Add(pp); }   // only these
                var cnt = new Counter(); if (c.Partial) Engine.ProcessPicks(c, false, cnt); else Engine.Process(c, false, cnt); c.Bytes = cnt.Bytes; c.Files = cnt.Files;
                var pf = new ProgressForm(new List<Category> { c }, c.Bytes) { SnapshotPath = snap };
                pf.StartPosition = FormStartPosition.CenterScreen; pf.ShowInTaskbar = true;
                Application.Run(pf);
                return 0;
            }
            if (Has(args, "--test-confirm"))
            {
                var all2 = Engine.Build(); var demo = all2.Where(x => x.Safe).Take(3).Concat(all2.Where(x => x.Level == Level.Review).Take(2)).Concat(all2.Where(x => x.Level == Level.Caution).Take(1)).ToList();
                long[] sizes = { 23400000000L, 3500000000L, 1200000000L, 9500000000L, 1500000000L, 4400000000L };
                for (int k = 0; k < demo.Count; k++) demo[k].Bytes = sizes[k];
                var cf = new ConfirmForm(demo) { StartPosition = FormStartPosition.CenterScreen, ShowInTaskbar = true };
                if (snap != null) cf.Shown += (s, e) => { Application.DoEvents(); Snap(cf, snap); cf.Close(); };
                Application.Run(cf);
                return 0;
            }
            string insideDir = Arg(args, "--test-inside");   // test hook: the "Inside this folder" list on its own
            if (insideDir != null)
            {
                var fm = new Form { ClientSize = Theme.D(440, 300), BackColor = Theme.Pane, StartPosition = FormStartPosition.CenterScreen, Text = "Inside" };
                var il = new InsideList { Location = Theme.P(20, 16), Width = Theme.D(400) };
                var tc = new Category { Id = "test-inside", Name = "Test", Kind = Kind.Contents, Level = Level.Caution, IsCustom = true }; tc.Roots.Add(insideDir);
                fm.Controls.Add(il); il.Open(tc);
                if (Arg(args, "--go") != null) il.GetType().GetMethod("Go", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(il, new object[] { Arg(args, "--go") });
                if (snap != null) fm.Shown += (s, e) => { Application.DoEvents(); Snap(fm, snap); fm.Close(); };
                Application.Run(fm);
                return 0;
            }
            if (Has(args, "--test-protect"))
            {
                Keep.Load();
                var pf = new ProtectForm { StartPosition = FormStartPosition.CenterScreen, ShowInTaskbar = true };
                if (snap != null) pf.Shown += (s, e) => { Application.DoEvents(); Snap(pf, snap); pf.Close(); };
                Application.Run(pf);
                return 0;
            }
            string finderPath = Arg(args, "--test-finder");
            if (finderPath != null)
            {
                string during = Arg(args, "--snapshot-during");   // capture the scan in progress, then quit
                if (Has(args, "--demo-drives"))   // example drives, to show the drive list with more than one drive
                {
                    var real = DriveChoice.All();
                    DriveChoice.Demo = real.Concat(new[] {
                        new DriveChoice { Root = "D:\\", Letter = "D:", Name = "Data", Total = 1000204886016L, Free = 227633266688L },
                        new DriveChoice { Root = "E:\\", Letter = "E:", Name = "Games", Total = 2000398934016L, Free = 103510790144L },
                        new DriveChoice { Root = "F:\\", Letter = "F:", Name = "Projects", Total = 500107862016L, Free = 323385753600L },
                        new DriveChoice { Root = "G:\\", Letter = "G:", Name = "Media", Total = 4000787030016L, Free = 1095216660480L },
                        new DriveChoice { Root = "H:\\", Letter = "H:", Name = "Backup", Total = 2000398934016L, Free = 1505337982976L, Removable = true } }).ToList();
                }
                var ff = new FinderForm(Engine.Build(), finderPath) { SnapshotPath = during == null ? snap : null, StartPosition = FormStartPosition.CenterScreen, ShowInTaskbar = true, OpenDrivesOnStart = Has(args, "--open-drives"), DrillDepth = Has(args, "--drill") ? 4 : 0 };
                if (during != null) ff.Shown += (s, e) => { var tm = new System.Windows.Forms.Timer { Interval = 4000 }; tm.Tick += (s2, e2) => { tm.Stop(); try { ff.SaveImage(during); } catch { } Environment.Exit(0); }; tm.Start(); };
                Application.Run(ff);
                return 0;
            }

            Updater.CleanUpOld();
            var form = new MainForm { SnapshotPath = snap, OpenPanelFor = Arg(args, "--open"), FramesDir = Arg(args, "--frames") };
            if (Arg(args, "--pick") != null) form.PickPaths = Arg(args, "--pick").Split('|').ToList();
            if (Has(args, "--test-update-bar"))   // test hook: show the update bar with a made-up newer version
                form.Shown += (s, e) => form.ShowUpdate(new Release { Version = new Version(Updater.Current.Major, Updater.Current.Minor + 1, 0), Tag = "test" });
            Application.Run(form);
            return 0;
        }
    }
}
