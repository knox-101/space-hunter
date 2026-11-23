// Copyright (c) 2026 Blesson Reji (knox-101). Space Hunter is free software under the GNU GPL v3 or later; see LICENSE.
// SPDX-License-Identifier: GPL-3.0-or-later
// Space Hunter (Windows) - scanning and cleaning engine.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SpaceHunter
{
    enum Kind { Contents, Subfolders, RunHistory, RecycleBin, WindowsOld }
    enum Level { Safe, Review, Caution }

    class Category
    {
        // a frozen copy for a running clean: changing ticks, ages or Reset in the window can't alter it half-way
        public int ScanGen;   // bumped whenever a new measurement starts; an older one finishing late is ignored
        public Category Snapshot() { var c = (Category)MemberwiseClone(); c.Roots = new List<string>(Roots); c.Picks = new HashSet<string>(Picks, StringComparer.OrdinalIgnoreCase); return c; }
        // Files and folders picked by hand inside an unticked item: cleaned exactly as picked (whatever their age), in the
        // next clean only. Never saved, so nothing new is ever cleaned by surprise later.
        public HashSet<string> Picks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public long PickFiles, PickBytes = -1;   // -1: not measured yet
        public bool Partial { get { return !Checked && Picks.Count > 0; } }
        public bool Included { get { return Checked || Picks.Count > 0; } }
        public long CleanBytes { get { return Checked ? Bytes : Partial ? Math.Max(0, PickBytes) : 0; } }
        // Only items that fill up over time offer "older than"; caches and the like are cleaned all at once.
        public bool AgeChoice { get { return (Kind == Kind.Contents || Kind == Kind.Subfolders) && (DefaultAgeMinutes > 0 || IsCustom); } }
        public string Id, Name, Note;
        public Level Level;
        public Kind Kind;
        public List<string> Roots = new List<string>();
        public string[] ExcludeDirs = new string[0];   // folder names never entered (e.g. an AI agent's "memory")
        public HashSet<string> Extensions;              // only files with these extensions (e.g. installers in Downloads)
        public bool TopLevelOnly;                       // only files directly in the folder; sub-folders are never touched
        public int DefaultAgeMinutes, AgeMinutes;       // only files untouched for this long are touched (0 = everything)
        public int LastAge;                             // the age to restore when switching back from "Everything"
        public bool Checked;
        public bool Recycle;                            // send to the Recycle Bin instead of deleting
        public bool AdminOnly;                          // a system folder: needs admin rights (the app starts with them)
        public bool IsCustom;                           // a folder the user added from the Storage map
        public string Refused;                          // something you added that the safety rules now refuse: never cleaned
        public string What, Deletes, SafeText, After;   // expanded notes
        public string Short;                            // one line: what happens if cleared
        public long Files, Bytes;
        public bool Scanned;
        public bool Safe { get { return Level == Level.Safe; } }
        public bool Blocked { get { return AdminOnly && !Engine.IsAdmin; } }   // can't be measured without permission
        public void ResetToDefault()
        {
            Checked = Level == Level.Safe; AgeMinutes = DefaultAgeMinutes; Picks.Clear(); PickBytes = -1;
            LastAge = DefaultAgeMinutes > 0 ? DefaultAgeMinutes : 14 * Engine.Day; Recycle = false;
        }
    }

    // One file as Windows' folder listing gives it: its name, size, dates and flags in one read.
    class Item
    {
        public string Path, Name;   // the plain form (no "\\?\")
        public long Length;
        public DateTime Write, Created;
        public FileAttributes Attr;
        // the newer of "last changed" and "arrived here" (copying or unzipping sets a new creation time)
        public DateTime Age { get { return Created > Write ? Created : Write; } }
        public string Extension { get { string n = Name ?? System.IO.Path.GetFileName(Path); int i = n.LastIndexOf('.'); return i < 0 || i == n.Length - 1 ? "" : n.Substring(i); } }
        public string DirectoryName { get { int i = Path.LastIndexOf('\\'); return i < 0 ? "" : Path.Substring(0, i); } }
        public static Item Of(FileInfo f)
        {
            try { return new Item { Path = Engine.Plain(f.FullName), Name = f.Name, Length = f.Length, Write = f.LastWriteTime, Created = f.CreationTime, Attr = f.Attributes }; }
            catch { return null; }   // gone or unreadable: left alone
        }
    }

    class Counter
    {
        public long Files, Bytes, Skipped, Recycled, RecycledBytes;
        public volatile bool Cancel;
        public string Current;
        public Action<string, long, bool> OnItem;            // path, size, deleted?
        public Action<string> OnNote;                         // messages for the progress log
    }

    static class Engine
    {
        public static readonly string L  = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        public static readonly string R  = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        public static readonly string H  = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        public static readonly string PD = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        public static readonly string W  = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        public static readonly string AppDir = Path.Combine(L, "SpaceHunter");
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\RunMRU";
        public const int Hour = 60, Day = 1440;

        public static readonly bool IsAdmin = CheckAdmin();

        // If the app was elevated with a different account's password, every user folder would be THAT account's.
        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode)]
        static extern bool WTSQuerySessionInformation(IntPtr server, int session, int infoClass, out IntPtr buffer, out int bytes);
        [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(IntPtr p);
        public static string SessionUser()
        {
            try
            {
                IntPtr buf; int n;
                if (!WTSQuerySessionInformation(IntPtr.Zero, -1 /* this session */, 5 /* WTSUserName */, out buf, out n)) return null;
                string s = Marshal.PtrToStringUni(buf); WTSFreeMemory(buf); return s;
            }
            catch { return null; }
        }
        public static bool RunningAsOtherUser(out string sessionUser, out string me)
        {
            sessionUser = SessionUser(); me = Environment.UserName;
            return !string.IsNullOrEmpty(sessionUser) && !string.Equals(sessionUser, me, StringComparison.OrdinalIgnoreCase);
        }
        static bool CheckAdmin()
        {
            try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }

        public static List<Category> Build()
        {
            // the items live in cleaners.json; only apps that are on this PC get a row
            var list = Cleaners.Load().Where(Cleaners.Installed).ToList();

            // folders the user sent here from the Storage map
            Custom.Load();
            foreach (var p in Custom.All)
            {
                bool isFile = File.Exists(p);   // a single file you picked: it goes whatever its age
                var c = new Category { Id = "custom:" + p.ToLowerInvariant(), Name = Path.GetFileName(p.TrimEnd('\\')) + (isFile ? "  (your file)" : "  (your folder)"),
                                       Level = Level.Caution, Kind = Kind.Contents, DefaultAgeMinutes = isFile ? 0 : 30 * Day, AgeMinutes = isFile ? 0 : 30 * Day, LastAge = 30 * Day };
                c.Roots.Add(p);
                c.IsCustom = true; c.Checked = false;   // your folders start unticked, older than 30 days
                c.Note = "A folder you added";
                c.Short = "Ticked contents go; the folder itself stays";
                c.What = "A folder you added from the Storage map: " + p;
                c.Deletes = "Every file inside it (older than the age you set), and the sub-folders that end up empty, except what you untick under \u201cInside this folder\u201d below. The folder itself stays.";
                c.SafeText = "Only you know. Open it and check before ticking. Protected folders inside it are skipped.";
                c.After = "The files are gone, unless you choose Recycle Bin in the confirm window.";
                if (isFile)
                {
                    c.Note = "A file you added";
                    c.Short = "This one file goes";
                    c.What = "A file you added from the Storage map: " + p;
                    c.Deletes = "Just this file, whatever its age.";
                    c.SafeText = "Only you know. Open it and check before ticking.";
                    c.After = "The file is gone, unless you choose Recycle Bin in the confirm window.";
                }
                // added before the safety rules caught it (e.g. a folder made right on a drive): listed so it can be removed, never cleaned
                string why;
                if (!CanAdd(p, out why, true)) { c.Refused = why; c.Short = "Not cleaned: " + why + ". Remove it from the list"; }
                list.Add(c);
            }
            Settings.Load(list);
            foreach (var c in list) if (c.Refused != null) c.Checked = false;
            Keep.Load();
            return list;
        }

        public static IEnumerable<string> ExistingRoots(Category c)
        {
            return c.Roots.Where(r => { try { return Directory.Exists(r); } catch { return false; } });
        }

        static bool IsSafeRoot(string root)
        {
            string full;
            try { full = Path.GetFullPath(root).TrimEnd('\\'); } catch { return false; }
            if (full.Length <= 3) return false;
            string[] banned = { H, L, R, PD, W, W + @"\System32" };
            foreach (var b in banned)
                if (string.Equals(full, b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return false;
            return PersonalFolderOf(full) == null;   // never inside Documents, Pictures, Desktop, OneDrive, .ssh...
        }

        // Your personal folders, wherever Windows keeps them (OneDrive or another drive included).
        static List<string> personal;
        public static List<string> PersonalFolders()
        {
            if (personal != null) return personal;
            var list = new List<string>();
            foreach (var sf in new[] { Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyPictures,
                                       Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.Favorites })
                try { var p = Environment.GetFolderPath(sf); if (!string.IsNullOrEmpty(p)) list.Add(p); } catch { }
            foreach (var n in new[] { "Documents", "Desktop", "Pictures", "Videos", "Music", "Favorites", "Downloads", "Contacts", "Saved Games",
                                      "source", "Projects", ".ssh", ".gnupg", ".aws", ".azure", ".kube", ".docker" })
                list.Add(Path.Combine(H, n));
            try { foreach (var d in Directory.GetDirectories(H, "OneDrive*")) list.Add(d); } catch { }
            personal = list.Select(Norm).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return personal;
        }
        public static string PersonalFolderOf(string path)
        {
            string p = Norm(path);
            foreach (var k in PersonalFolders()) if (Under(p, k)) return k;
            return null;
        }

        public static void Process(Category c, bool delete, Counter cnt)
        {
            if (c.Blocked) return;   // system folder: measured and cleaned by the permission helper
            DateTime cutoff = DateTime.Now.AddMinutes(-c.AgeMinutes);
            var exclude = new HashSet<string>(c.ExcludeDirs, StringComparer.OrdinalIgnoreCase);

            if (c.Kind == Kind.RunHistory)
            {
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(RunKey, delete))
                    {
                        if (k == null) return;
                        foreach (var n in k.GetValueNames())
                        {
                            bool ok = true;
                            if (delete) { try { k.DeleteValue(n); } catch { ok = false; } }
                            if (ok) cnt.Files++; else cnt.Skipped++;
                            if (delete && cnt.OnItem != null) cnt.OnItem("Run history entry " + n, 0, ok);
                        }
                    }
                }
                catch { }
                return;
            }

            if (c.Kind == Kind.RecycleBin)
            {
                var info = new SHQUERYRBINFO();
                info.cbSize = Marshal.SizeOf(typeof(SHQUERYRBINFO));
                if (SHQueryRecycleBin(null, ref info) != 0) return;
                if (delete && info.i64NumItems > 0) { EmptyBin(cnt); return; }
                if (!delete) { cnt.Files += info.i64NumItems; cnt.Bytes += info.i64Size; }
                return;
            }

            if (c.Kind == Kind.WindowsOld && delete)
            {
                // Windows protects these folders, so Windows' own Disk Cleanup removes them; we measure before and after
                var before = Measure(c);
                cnt.Current = "Windows Disk Cleanup is removing the previous Windows installation";
                if (cnt.OnNote != null) cnt.OnNote("Asked Windows Disk Cleanup to remove the previous Windows installation. This can take several minutes.");
                RunDiskCleanup(cnt);
                var after = Measure(c);
                long freed = Math.Max(0, before.Bytes - after.Bytes), files = Math.Max(0, before.Files - after.Files);
                cnt.Files += files; cnt.Bytes += freed; cnt.Skipped += after.Files;
                if (cnt.OnItem != null) cnt.OnItem("Previous Windows installation (" + files.ToString("N0") + " files)", freed, after.Files == 0);
                return;
            }
            if (c.Kind == Kind.WindowsOld) { var m = Measure(c); cnt.Files += m.Files; cnt.Bytes += m.Bytes; return; }

            if (c.Refused != null) return;   // the safety rules refuse it now
            if (delete && c.Partial) { ProcessPicks(c, true, cnt); return; }   // only what you picked
            var bin = delete && c.Recycle ? new RecycleBatch(cnt) : null;
            skipNow = Custom.Skip;   // what you unticked under "What's inside" (or inside a folder you added)
            touchedNow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // folders this clean takes files out of
            foreach (var root in c.Roots)
            {
                if (cnt.Cancel) break;
                if (c.IsCustom && File.Exists(root))   // a single file you added
                {
                    var fi = new FileInfo(root);
                    if ((fi.Attributes & FileAttributes.ReparsePoint) == 0) Handle(Item.Of(fi), cutoff, delete, cnt, bin);
                    if (bin != null) bin.Flush();
                    continue;
                }
                // personal folders (e.g. Downloads) are only ever touched through an extension filter, top level only
                if (!Directory.Exists(root) || (!IsSafeRoot(root) && !(c.Extensions != null && c.TopLevelOnly))) continue;
                var rd = new DirectoryInfo(root);
                if ((rd.Attributes & FileAttributes.ReparsePoint) != 0) continue;   // never follow a root that is a link
                if (Keep.IsKept(rd.FullName)) continue;
                var ext = c.Extensions;
                Action<Item> each = f => { if (ext == null || ext.Contains(f.Extension)) Handle(f, cutoff, delete, cnt, bin); };
                if (c.Kind == Kind.Contents && c.TopLevelOnly)
                {
                    FileInfo[] files; try { files = rd.GetFiles(); } catch { files = new FileInfo[0]; }
                    foreach (var f in files) { if (cnt.Cancel) break; if ((f.Attributes & FileAttributes.ReparsePoint) == 0) { var it = Item.Of(f); if (it != null) each(it); } }
                    if (bin != null) bin.Flush();
                }
                else if (c.Kind == Kind.Contents && !delete)
                    WalkWide(rd.FullName, f => ext == null || ext.Contains(f.Extension), cutoff, cnt, exclude);
                else if (c.Kind == Kind.Contents)
                {
                    Walk(rd.FullName, each, cnt, exclude);
                    if (bin != null) bin.Flush();
                    if (delete) RemoveEmptyDirs(rd, true, exclude, cutoff);
                }
                else
                {
                    DirectoryInfo[] subs;
                    try { subs = rd.GetDirectories(); } catch { continue; }
                    foreach (var s in subs)
                    {
                        if (cnt.Cancel) break;
                        if ((s.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if (Keep.IsKept(s.FullName)) continue;
                        if (!delete)   // measuring: its newest file and its size in one read
                        {
                            DateTime n = AgeTime(s); var part = new Counter();
                            Walk(s.FullName, f => { if (f.Age > n) n = f.Age; Handle(f, DateTime.MaxValue, false, part, null); }, part, exclude);
                            if (n < cutoff) { cnt.Files += part.Files; cnt.Bytes += part.Bytes; }
                            continue;
                        }
                        if (Newest(s, cnt) >= cutoff) continue;
                        Walk(s.FullName, f => Handle(f, DateTime.MaxValue, delete, cnt, bin), cnt, exclude);
                        if (bin != null) bin.Flush();
                        if (delete) RemoveEmptyDirs(s, false, exclude, DateTime.MaxValue);   // the whole old session folder goes
                    }
                }
            }
            if (bin != null) bin.Flush();
        }

        // The files and folders picked by hand in an unticked item: each one is cleaned whole, whatever its age. Only picks
        // inside the item's own folders count, and protected, kept and "always keep" files still stay.
        public static void ProcessPicks(Category c, bool delete, Counter cnt)
        {
            if (c.Refused != null || c.Blocked) return;
            var roots = ExistingRoots(c).Select(r => r.TrimEnd('\\')).ToList();
            List<string> picks; try { picks = c.Picks.ToList(); } catch { return; }
            picks = picks.Where(p => roots.Any(r => p.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase)))
                         .Where(p => !picks.Any(q => q != p && p.StartsWith(q + "\\", StringComparison.OrdinalIgnoreCase))).ToList();   // a pick inside a picked folder is covered
            var exclude = new HashSet<string>(c.ExcludeDirs, StringComparer.OrdinalIgnoreCase);
            var bin = delete && c.Recycle ? new RecycleBatch(cnt) : null;
            skipNow = Custom.Skip;
            touchedNow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ext = c.Extensions;
            foreach (var p in picks)
            {
                if (cnt.Cancel) break;
                try
                {
                    if (Keep.IsKept(p) || Custom.IsSkipped(p, skipNow)) continue;
                    var fi = new FileInfo(p);
                    if (fi.Exists)
                    {
                        if ((fi.Attributes & FileAttributes.ReparsePoint) == 0 && (ext == null || ext.Contains(fi.Extension))) Handle(Item.Of(fi), DateTime.MaxValue, delete, cnt, bin);
                        continue;
                    }
                    var di = new DirectoryInfo(p);
                    if (!di.Exists || (di.Attributes & FileAttributes.ReparsePoint) != 0 || c.TopLevelOnly || exclude.Contains(di.Name)) continue;
                    Walk(di.FullName, f => { if (ext == null || ext.Contains(f.Extension)) Handle(f, DateTime.MaxValue, delete, cnt, bin); }, cnt, exclude);
                    if (bin != null) bin.Flush();
                    if (delete) RemoveEmptyDirs(di, false, exclude, DateTime.MaxValue);   // the picked folder goes too, once empty
                }
                catch { cnt.Skipped++; }
            }
            if (bin != null) bin.Flush();
        }

        static Counter Measure(Category c)
        {
            var m = new Category { Id = c.Id, Kind = Kind.Contents, Level = c.Level };
            m.Roots.AddRange(c.Roots);
            var cnt = new Counter(); Process(m, false, cnt); return cnt;
        }

        // Runs Windows Disk Cleanup for the "previous installation" handlers only, then removes the preset again.
        static void RunDiskCleanup(Counter cnt)
        {
            const string vc = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches\";
            const string flag = "StateFlags0777";
            string[] handlers = { "Previous Installations", "Temporary Setup Files", "Windows Upgrade Log Files" };
            var set = new List<string>();
            try
            {
                foreach (var h in handlers)
                    using (var k = Registry.LocalMachine.OpenSubKey(vc + h, true))
                        if (k != null) { k.SetValue(flag, 2, RegistryValueKind.DWord); set.Add(h); }
                if (set.Count == 0) return;
                using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    Path.Combine(W, @"System32\cleanmgr.exe"), "/sagerun:777") { UseShellExecute = false }))
                    while (!p.WaitForExit(500)) if (cnt.Cancel) break;   // Stop leaves Disk Cleanup to finish on its own
            }
            catch (Exception e) { if (cnt.OnNote != null) cnt.OnNote("Windows Disk Cleanup could not run: " + e.Message); }
            finally
            {
                foreach (var h in set)
                    try { using (var k = Registry.LocalMachine.OpenSubKey(vc + h, true)) if (k != null) k.DeleteValue(flag, false); } catch { }
            }
        }

        [ThreadStatic] static HashSet<string> skipNow, touchedNow;

        static void Handle(Item f, DateTime cutoff, bool delete, Counter cnt, RecycleBatch bin)
        {
            if (f == null || Custom.IsSkipped(f.Path, skipNow)) return;
            // the newer of "last changed" and "arrived here": photos copied in today are new, whatever their date
            DateTime t = f.Age; long len = f.Length;
            if (Keep.IsKept(f.Path)) return;   // on the keep list: never touched, never counted
            if (t >= cutoff) { if (delete) cnt.Skipped++; return; }
            if (!delete) { cnt.Files++; cnt.Bytes += len; return; }
            cnt.Current = f.Path;
            if (touchedNow != null) touchedNow.Add(f.DirectoryName);
            if (bin != null) { bin.Add(f.Path, len); return; }
            bool ok = TryDelete(f);
            if (ok) { cnt.Files++; cnt.Bytes += len; } else cnt.Skipped++;
            if (cnt.OnItem != null) cnt.OnItem(f.Path, len, ok);
        }

        static bool TryDelete(Item f)
        {
            string lp = Long(f.Path);
            if ((f.Attr & FileAttributes.ReadOnly) != 0) SetFileAttributesW(lp, (uint)FileAttributes.Normal);
            return DeleteFileW(lp);
        }

        // The "\\?\" form Windows' own calls take, so no path is too long
        static string Long(string p)
        {
            if (p.StartsWith(@"\\?\")) return p;
            return p.StartsWith(@"\\") ? @"\\?\UNC\" + p.Substring(2) : @"\\?\" + p;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WIN32_FIND_DATAW
        {
            public uint dwFileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime, ftLastAccessTime, ftLastWriteTime;
            public uint nFileSizeHigh, nFileSizeLow, dwReserved0, dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr FindFirstFileExW(string name, int infoLevel, out WIN32_FIND_DATAW data, int searchOp, IntPtr filter, int flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool FindNextFileW(IntPtr h, out WIN32_FIND_DATAW data);
        [DllImport("kernel32.dll")] static extern bool FindClose(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool DeleteFileW(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetFileAttributesW(string path, uint attrs);
        static DateTime Time(System.Runtime.InteropServices.ComTypes.FILETIME t)
        {
            long v = ((long)(uint)t.dwHighDateTime << 32) | (uint)t.dwLowDateTime;
            try { return DateTime.FromFileTime(v); } catch { return DateTime.MinValue; }
        }

        // Past Windows' old 260-character limit a plain path fails (unless long paths are switched on in the registry, which
        // they aren't by default), so a folder near the limit is opened by its "\\?\" form, which has no such limit.
        // Every check on a path (protected, skipped, built in) compares the plain form (Plain), so nothing protected
        // slips through because it's deep.
        public static DirectoryInfo Deep(DirectoryInfo d)
        {
            string f = d.FullName;
            if (f.Length < 240 || f.StartsWith(@"\\?\")) return d;
            return new DirectoryInfo(f.StartsWith(@"\\") ? @"\\?\UNC\" + f.Substring(2) : @"\\?\" + f);
        }
        static FileSystemInfo Deep(FileSystemInfo i)
        {
            var d = i as DirectoryInfo; if (d != null) return Deep(d);
            string f = i.FullName;
            if (f.Length < 240 || f.StartsWith(@"\\?\")) return i;
            return new FileInfo(f.StartsWith(@"\\") ? @"\\?\UNC\" + f.Substring(2) : @"\\?\" + f);
        }
        public static string Plain(string p)
        {
            if (p == null) return "";
            if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + p.Substring(8);
            if (p.StartsWith(@"\\?\")) return p.Substring(4);
            return p;
        }

        // Every file under a folder, through Windows' own folder listing: each file's size, dates and flags come with the
        // listing, so nothing asks the disk about a file twice (a cache of 80,000 small files is measured in a second or
        // two, not ten). Always in the "\\?\" form, so no path is too long. Links are never followed.
        static void Walk(string dir, Action<Item> onFile, Counter cnt, HashSet<string> exclude)
        {
            var files = new List<Item>(); var subs = new List<string>();
            ListDir(dir, files, subs, exclude);
            foreach (var f in files)
            {
                if (cnt.Cancel) return;
                try { onFile(f); } catch { cnt.Skipped++; }   // one odd file is skipped, never the end of the scan
            }
            foreach (var sub in subs)
            {
                if (cnt.Cancel) return;
                if (!Keep.IsKept(sub) && !Custom.IsSkipped(sub, skipNow)) Walk(sub, onFile, cnt, exclude);
            }
        }

        // Measuring only (nothing is deleted): the folders under a big folder are read side by side, since a drive
        // answers several at once. The top is opened up a level or two until there are enough folders to share out.
        static void WalkWide(string root, Func<Item, bool> wanted, DateTime cutoff, Counter cnt, HashSet<string> exclude)
        {
            var skip = skipNow;
            Action<Item, Counter> take = (f, into) => { if (wanted(f)) Handle(f, cutoff, false, into, null); };
            var work = new List<string> { root };
            var parts = new List<string>();
            for (int level = 0; level < 3 && work.Count > 0 && work.Count + parts.Count < 16; level++)
            {
                var next = new List<string>();
                foreach (var dir in work)
                {
                    var files = new List<Item>(); var subs = new List<string>();
                    ListDir(dir, files, subs, exclude);
                    foreach (var f in files) take(f, cnt);
                    next.AddRange(subs.Where(x => !Keep.IsKept(x) && !Custom.IsSkipped(x, skip)));
                }
                work = next;
            }
            parts.AddRange(work);
            Parallel.ForEach(parts, new ParallelOptions { MaxDegreeOfParallelism = 4 }, dir =>
            {
                skipNow = skip;   // each thread keeps its own copy of what you unticked
                var part = new Counter();
                Walk(dir, f => take(f, part), part, exclude);
                lock (cnt) { cnt.Files += part.Files; cnt.Bytes += part.Bytes; cnt.Skipped += part.Skipped; }
            });
        }

        // One folder's files and sub-folders, from Windows' folder listing (links left out)
        static void ListDir(string dir, List<Item> files, List<string> subs, HashSet<string> exclude)
        {
            string p = Plain(dir).TrimEnd('\\') + "\\";
            WIN32_FIND_DATAW d;
            IntPtr h = FindFirstFileExW(Long(p) + "*", 1 /* basic */, out d, 0, IntPtr.Zero, 2 /* large fetch */);
            if (h == new IntPtr(-1)) return;
            try
            {
                do
                {
                    string name = d.cFileName;
                    if (name == "." || name == "..") continue;
                    var attr = (FileAttributes)d.dwFileAttributes;
                    if ((attr & FileAttributes.ReparsePoint) != 0) continue;
                    if ((attr & FileAttributes.Directory) != 0) { if (!exclude.Contains(name)) subs.Add(p + name); continue; }
                    files.Add(new Item { Path = p + name, Name = name, Attr = attr, Length = ((long)d.nFileSizeHigh << 32) | d.nFileSizeLow,
                                         Write = Time(d.ftLastWriteTime), Created = Time(d.ftCreationTime) });
                } while (FindNextFileW(h, out d));
            }
            finally { FindClose(h); }
        }

        // A file's age: the newer of "last changed" and "arrived here" (copying or unzipping sets a new creation time), so
        // photos copied in today count as new, whatever their own date.
        public static DateTime AgeTime(FileSystemInfo f)
        {
            DateTime t = f.LastWriteTime, made = f.CreationTime;
            return made > t ? made : t;
        }

        static DateTime Newest(DirectoryInfo d, Counter cnt)
        {
            DateTime n = AgeTime(d);
            Walk(d.FullName, f => { if (f.Age > n) n = f.Age; }, cnt, new HashSet<string>());
            return n;
        }

        // Removes the empty folders this clean emptied, and empty folders older than the age rule. A folder a program made a
        // moment ago (say, an installer's working folder in Temp) stays.
        static void RemoveEmptyDirs(DirectoryInfo d, bool isRoot, HashSet<string> exclude, DateTime cutoff)
        {
            d = Deep(d);
            DirectoryInfo[] subs;
            try { subs = d.GetDirectories(); } catch { return; }
            foreach (var s0 in subs)
                try   // one folder that can't be read never stops the rest
                {
                    var s = Deep(s0);
                    if ((s.Attributes & FileAttributes.ReparsePoint) == 0 && !exclude.Contains(s.Name) && !Keep.IsKept(s.FullName) && !Custom.IsSkipped(s.FullName, skipNow)) RemoveEmptyDirs(s, false, exclude, cutoff);
                }
                catch { }
            if (isRoot) return;
            try
            {
                if (d.GetFileSystemInfos().Length != 0) return;
                bool emptiedNow = touchedNow != null && touchedNow.Contains(Plain(d.FullName));
                bool old = d.CreationTime < cutoff && d.LastWriteTime < cutoff;
                if (!emptiedNow && !old) return;
                if ((d.Attributes & FileAttributes.ReadOnly) != 0) d.Attributes &= ~FileAttributes.ReadOnly;
                d.Delete();
                if (touchedNow != null && d.Parent != null) touchedNow.Add(Plain(d.Parent.FullName));   // its parent may be empty now too
            }
            catch { }
        }

        public static string Fmt(long b)
        {
            var inv = CultureInfo.InvariantCulture;   // "1.5 GB" everywhere, so the log reads back the same in every country
            // the unit switches where the rounded number would reach 1000, so "1000 MB" never shows (it reads "1.0 GB")
            if (b >= 1073688136909L) return (b / 1099511627776.0).ToString("0.00", inv) + " TB";
            if (b >= 1048051712L) return (b / 1073741824.0).ToString("0.0", inv) + " GB";
            if (b >= 1L << 20) return (b / 1048576.0).ToString("0", inv) + " MB";
            if (b > 0) return (b / 1024.0).ToString("0", inv) + " KB";
            return "0 MB";
        }

        public static string FmtAge(int minutes)
        {
            if (minutes <= 0) return "any age";
            if (minutes % Day == 0) return (minutes / Day) + (minutes == Day ? " day" : " days");
            if (minutes % Hour == 0) return (minutes / Hour) + (minutes == Hour ? " hour" : " hours");
            return minutes + " min";
        }

        public static void Log(string line)
        {
            try
            {
                Directory.CreateDirectory(AppDir);
                File.AppendAllText(Path.Combine(AppDir, "log.txt"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "  " + line + Environment.NewLine);
            }
            catch { }
        }

        public static IEnumerable<DriveInfo> FixedDrives()
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                bool ok;
                try { ok = d.DriveType == DriveType.Fixed && d.IsReady; } catch { ok = false; }
                if (ok) yield return d;
            }
        }

        // How big a single file the Recycle Bin on that drive can take (0 if the bin is turned off there).
        static readonly Dictionary<string, long> binLimits = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern bool GetVolumeNameForVolumeMountPoint(string mount, System.Text.StringBuilder name, int len);
        public static long BinLimit(string path)
        {
            string root = Path.GetPathRoot(path) ?? "C:\\";
            lock (binLimits)
            {
                long v;
                if (binLimits.TryGetValue(root, out v)) return v;
                long total = 0; try { total = new DriveInfo(root).TotalSize; } catch { }
                v = total / 20;   // Windows' usual default is about 5% of the drive
                try
                {
                    var sb = new System.Text.StringBuilder(64);
                    if (GetVolumeNameForVolumeMountPoint(root, sb, sb.Capacity))
                    {
                        var guid = sb.ToString(); int a = guid.IndexOf('{'), b = guid.IndexOf('}');
                        if (a >= 0 && b > a)
                            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\" + guid.Substring(a, b - a + 1)))
                                if (k != null)
                                {
                                    if (Convert.ToInt32(k.GetValue("NukeOnDelete", 0)) == 1) v = 0;
                                    else { var mb = k.GetValue("MaxCapacity"); if (mb != null) v = Convert.ToInt64(mb) * 1048576L; }
                                }
                    }
                }
                catch { }
                binLimits[root] = v; return v;
            }
        }

        static string Norm(string p) { try { return Path.GetFullPath(p).TrimEnd('\\'); } catch { return (p ?? "").TrimEnd('\\'); } }
        static bool Under(string child, string parent) { return child.Equals(parent, StringComparison.OrdinalIgnoreCase) || child.StartsWith(parent + "\\", StringComparison.OrdinalIgnoreCase); }

        // Can a folder from the Storage map be added to the cleanup list? Never a drive, Windows, Program Files,
        // or anything that holds the profile, AppData, Documents, Desktop, Downloads or OneDrive.
        public static bool CanAdd(string path, out string why) { return CanAdd(path, out why, false); }
        // existing: checking something already on the list (so "already on the cleanup list" doesn't count)
        public static bool CanAdd(string path, out string why, bool existing)
        {
            why = null;
            if (string.IsNullOrEmpty(path) || path.TrimEnd('\\').Length <= 2) { why = "a whole drive"; return false; }   // "C:" alone means the current folder
            string p = Norm(path);
            if (p.Length <= 3) { why = "a whole drive"; return false; }
            string top = Path.GetFileName(p);
            if (top.StartsWith("$") || top.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)) { why = "kept by Windows"; return false; }
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            foreach (var sys in new[] { W, pf, pf86, PD })
                if (!string.IsNullOrEmpty(sys) && Under(p, Norm(sys))) { why = "part of Windows or installed programs"; return false; }
            // AI tools' and editors' own folders hold settings, sign-in tokens and memory; their caches and old chats are items
            foreach (var t in new[] { ".claude", ".codex", ".gemini", ".cursor", ".vscode", ".config", ".copilot", ".continue", ".aider" })
                if (Under(p, Norm(Path.Combine(H, t)))) { why = "it holds an app's settings and sign-ins"; return false; }
            // other people's profiles (C:\Users\Alice, Public, Default) are never offered
            string users = Norm(Path.GetDirectoryName(H));
            if (Under(p, users) && !Under(p, Norm(H))) { why = "it's in another account's folders"; return false; }
            // Windows' own app data (keys, credentials, profiles) and the top of AppData
            foreach (var a in new[] { Path.Combine(R, "Microsoft"), Path.Combine(L, "Microsoft"), Path.Combine(H, "AppData", "LocalLow", "Microsoft") })
                if (Under(p, Norm(a))) { why = "it holds Windows' own settings and keys"; return false; }
            foreach (var a in new[] { Path.Combine(H, "AppData"), R, L, Path.Combine(H, "AppData", "LocalLow") })
                if (string.Equals(p, Norm(a), StringComparison.OrdinalIgnoreCase)) { why = "it holds settings for all your apps"; return false; }
            if (Path.GetFileName(p).Equals("User Data", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(p).Equals("Profiles", StringComparison.OrdinalIgnoreCase))
            { why = "it holds a browser's profiles (logins, history)"; return false; }
            foreach (var keep in new[] { H, L, R, Path.GetDirectoryName(H) }.Concat(PersonalFolders()))
                if (!string.IsNullOrEmpty(keep) && Under(Norm(keep), p)) { why = "it holds your profile or personal folders"; return false; }
            if (PersonalFolderOf(p) != null) { why = "it's inside your personal files (" + Path.GetFileName(PersonalFolderOf(p)) + ")"; return false; }
            // a folder made directly on a drive (like C:\Personal) usually holds your own files: never offer it
            var parent = Path.GetDirectoryName(p);
            if (parent == null || parent.TrimEnd('\\').Length <= 2) { why = "likely your own files (made right on the drive)"; return false; }
            if (Keep.IsBuiltIn(p)) { why = "it's Space Hunter's own folder"; return false; }
            if (Keep.IsKept(p)) { why = "it's a protected folder"; return false; }
            if (!existing && Custom.All.Any(c => Under(p, Norm(c)))) { why = "it's already on the cleanup list"; return false; }
            return true;
        }

        // Which cleanup-list items already cover a folder (it's inside them) or sit inside it.
        public static string Coverage(string path, List<Category> cats, out bool inside)
        {
            inside = false; string p = Norm(path);
            if (Path.GetFileName(p).Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase))   // the Recycle Bin item empties it
            {
                var bin = cats.FirstOrDefault(c => c.Kind == Kind.RecycleBin);
                if (bin != null) { inside = true; return bin.Name; }
            }
            foreach (var c in cats)
                foreach (var r in c.Roots)
                    if (Under(p, Norm(r))) { inside = true; return c.Name; }
            var holds = cats.Where(c => c.Roots.Any(r => Under(Norm(r), p))).Select(c => c.Name).Distinct().ToList();
            return holds.Count == 0 ? null : string.Join(", ", holds.Take(2)) + (holds.Count > 2 ? " +" + (holds.Count - 2) : "");
        }

        // Empties your Recycle Bin one deleted item at a time, so the count moves from the first file: Windows' own "empty"
        // call says nothing until it has finished, which for a few thousand items left the count at 0 for a long while.
        // Each item is two files in the drive's $Recycle.Bin folder: $R... (the file or folder) and $I... (its original
        // name). Whatever can't go this way (a file in use, a drive this can't open) is left to Windows' own call.
        // onlyUnder (test hook): only items deleted from under that folder, and never Windows' own call
        public static void EmptyBin(Counter cnt, string onlyUnder = null)
        {
            string sid = null;
            try { sid = WindowsIdentity.GetCurrent().User.Value; } catch { }
            var none = new HashSet<string>();
            if (sid != null)
                foreach (var drive in FixedDrives())
                {
                    string dir = drive.RootDirectory.FullName.TrimEnd('\\') + @"\$Recycle.Bin\" + sid;
                    string[] infos; try { infos = Directory.GetFiles(dir, "$I*"); } catch { continue; }
                    foreach (var info in infos)
                    {
                        if (cnt.Cancel) return;
                        try
                        {
                            string data = Path.Combine(dir, "$R" + Path.GetFileName(info).Substring(2));
                            string was = BinName(info) ?? "a deleted item";
                            if (onlyUnder != null && !was.StartsWith(onlyUnder, StringComparison.OrdinalIgnoreCase)) continue;
                            cnt.Current = "Emptying the Recycle Bin: " + was;
                            long b0 = cnt.Bytes; bool gone;
                            var attr = File.GetAttributes(data);   // throws if it's already gone: then only its name card goes
                            if ((attr & FileAttributes.Directory) != 0 && (attr & FileAttributes.ReparsePoint) == 0)
                            {
                                Walk(data, f => { if (TryDelete(f)) { cnt.Files++; cnt.Bytes += f.Length; } }, cnt, none);
                                try { Directory.Delete(Long(data), true); } catch { }   // the emptied folders (links are removed, never followed)
                                gone = !Directory.Exists(data);
                            }
                            else if ((attr & FileAttributes.Directory) != 0) { try { Directory.Delete(Long(data)); } catch { } gone = !Directory.Exists(data); }
                            else
                            {
                                var it = new Item { Path = data, Attr = attr, Length = new FileInfo(data).Length };
                                gone = TryDelete(it);
                                if (gone) { cnt.Files++; cnt.Bytes += it.Length; }
                            }
                            if (gone) { try { File.Delete(info); } catch { } }
                            if (cnt.OnItem != null) cnt.OnItem("Recycle Bin: " + was, cnt.Bytes - b0, gone);
                        }
                        catch (FileNotFoundException) { if (onlyUnder == null) try { File.Delete(info); } catch { } }   // a name card with nothing behind it
                        catch { }
                    }
                }
            // anything left: Windows' own call, counted from before and after
            var left = new SHQUERYRBINFO(); left.cbSize = Marshal.SizeOf(typeof(SHQUERYRBINFO));
            if (onlyUnder == null && !cnt.Cancel && SHQueryRecycleBin(null, ref left) == 0 && left.i64NumItems > 0)
            {
                cnt.Current = "Emptying the Recycle Bin";
                if (SHEmptyRecycleBin(IntPtr.Zero, null, 7) == 0) { cnt.Files += left.i64NumItems; cnt.Bytes += left.i64Size; }
            }
            try { SHUpdateRecycleBinIcon(); } catch { }
        }

        // The original path of a deleted item, from its $I file (version 1: fixed 520 bytes; version 2: length first)
        static string BinName(string info)
        {
            try
            {
                var b = File.ReadAllBytes(info);
                if (b.Length < 28) return null;
                long ver = BitConverter.ToInt64(b, 0);
                string s = ver >= 2 ? System.Text.Encoding.Unicode.GetString(b, 28, Math.Min(b.Length - 28, Math.Max(0, BitConverter.ToInt32(b, 24) * 2)))
                                    : System.Text.Encoding.Unicode.GetString(b, 24, Math.Min(b.Length - 24, 520));
                s = s.TrimEnd('\0'); int z = s.IndexOf('\0'); if (z >= 0) s = s.Substring(0, z);
                return s.Length > 0 ? s : null;
            }
            catch { return null; }
        }
        [DllImport("shell32.dll")] static extern int SHUpdateRecycleBinIcon();

        [StructLayout(LayoutKind.Sequential)]        struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHQueryRecycleBin(string pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHEmptyRecycleBin(IntPtr hwnd, string pszRootPath, uint dwFlags);
    }

    // Sends files to the Recycle Bin in batches (one shell call per 200 files), silently, with no dialogs.
    class RecycleBatch
    {
        readonly Counter cnt;
        readonly List<KeyValuePair<string, long>> pending = new List<KeyValuePair<string, long>>();
        public RecycleBatch(Counter c) { cnt = c; }

        // removable and network drives have no Recycle Bin: Windows would delete for good without asking, so keep them
        static readonly Dictionary<string, bool> hasBin = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        static bool HasBin(string path)
        {
            string root; try { root = Path.GetPathRoot(path); } catch { return false; }
            lock (hasBin)
            {
                bool ok;
                if (!hasBin.TryGetValue(root, out ok)) { try { ok = new DriveInfo(root).DriveType == DriveType.Fixed; } catch { ok = false; } hasBin[root] = ok; }
                return ok;
            }
        }

        public void Add(string path, long size)
        {
            if (path.Length >= 260)
            {
                cnt.Skipped++;
                if (cnt.OnNote != null) cnt.OnNote("kept, its path is too long for the Recycle Bin (choose Permanently delete to clean it): " + path);
                return;
            }
            if (!HasBin(path))
            {
                cnt.Skipped++;
                if (cnt.OnNote != null) cnt.OnNote("kept, this drive has no Recycle Bin (choose Permanently delete to clean it): " + path);
                return;
            }
            // a file bigger than the Recycle Bin can hold would be deleted for good by Windows: keep it instead
            long limit = Engine.BinLimit(path);
            if (size > limit)
            {
                cnt.Skipped++;
                if (cnt.OnNote != null) cnt.OnNote("kept, too big for the Recycle Bin (" + Engine.Fmt(size) + "): " + path);
                return;
            }
            pending.Add(new KeyValuePair<string, long>(path, size));
            if (pending.Count >= batch) Flush();
        }

        // the first batch is small, so the count moves at once; each one after is twice as big, up to 200 (one shell call
        // per batch: big batches are faster in all, small ones show progress sooner)
        int batch = 16;

        public void Flush()
        {
            if (pending.Count == 0) return;
            batch = Math.Min(200, batch * 2);
            var now = pending.ToList(); pending.Clear();
            if (Shell(now.Select(p => p.Key)))
            {
                foreach (var p in now) Done(p, !File.Exists(p.Key));
                return;
            }
            foreach (var p in now) Done(p, Shell(new[] { p.Key }) && !File.Exists(p.Key));   // one by one, so one locked file doesn't stop the rest
        }

        void Done(KeyValuePair<string, long> p, bool ok)
        {
            if (ok) { cnt.Files++; cnt.Recycled++; cnt.RecycledBytes += p.Value; } else cnt.Skipped++;
            if (cnt.OnItem != null) cnt.OnItem(p.Key, ok ? -p.Value - 1 : p.Value, ok);   // negative size = went to the bin
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd; public uint wFunc;
            [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
            [MarshalAs(UnmanagedType.LPWStr)] public string pTo;
            public ushort fFlags; public int fAnyOperationsAborted; public IntPtr hNameMappings;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszProgressTitle;
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

        static bool Shell(IEnumerable<string> paths)
        {
            const uint FO_DELETE = 3;
            const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = string.Join("\0", paths) + "\0\0",
                fFlags = (ushort)(FOF_SILENT | FOF_NOCONFIRMATION | FOF_ALLOWUNDO | FOF_NOERRORUI)
            };
            try { return SHFileOperation(ref op) == 0 && op.fAnyOperationsAborted == 0; } catch { return false; }
        }
    }

    // Files and folders the user chose to keep: never shown, counted or cleaned.
    static class Keep
    {
        static readonly object gate = new object();
        static List<string> paths = new List<string>();
        static string FilePath { get { return Path.Combine(Engine.AppDir, "keep.txt"); } }

        public static List<string> All { get { lock (gate) return paths.ToList(); } }

        public static void Load()
        {
            try { lock (gate) paths = File.Exists(FilePath) ? File.ReadAllLines(FilePath).Where(l => l.Trim().Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList() : new List<string>(); }
            catch { }
        }
        static void Save()
        {
            try { Directory.CreateDirectory(Engine.AppDir); lock (gate) File.WriteAllLines(FilePath, paths); } catch { }
        }
        public static void Add(string p)
        {
            p = p.TrimEnd('\\');
            lock (gate) { if (!paths.Any(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase))) paths.Add(p); }
            Save();
        }
        public static void Remove(string p)
        {
            lock (gate) paths.RemoveAll(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase));
            Save();
        }
        // Space Hunter itself: its settings folder, and the app (its own folder, or just the exe when it sits in a shared
        // folder such as Downloads). Always protected; this can't be removed from the protected list.
        static List<string> builtIn;
        public static List<string> BuiltIn
        {
            get
            {
                if (builtIn != null) return builtIn;
                var list = new List<string> { Engine.AppDir.TrimEnd('\\') };
                try
                {
                    string exe = Path.GetFullPath(System.Reflection.Assembly.GetEntryAssembly().Location), dir = Path.GetDirectoryName(exe).TrimEnd('\\');
                    list.Add(exe); list.Add(exe + ".old");
                    bool shared = dir.Length <= 3 || Engine.PersonalFolders().Any(k => string.Equals(k, dir, StringComparison.OrdinalIgnoreCase))
                                  || string.Equals(Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\'), dir, StringComparison.OrdinalIgnoreCase)
                                  || string.Equals(Engine.H.TrimEnd('\\'), dir, StringComparison.OrdinalIgnoreCase);
                    if (!shared) list.Add(dir);
                }
                catch { }
                return builtIn = list;
            }
        }
        public static bool IsBuiltIn(string path)
        {
            path = Engine.Plain(path);
            foreach (var k in BuiltIn)
                if (path.Equals(k, StringComparison.OrdinalIgnoreCase) || path.StartsWith(k + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        public static bool IsKept(string path)
        {
            path = Engine.Plain(path);
            if (IsBuiltIn(path)) return true;
            lock (gate)
            {
                if (paths.Count == 0) return false;
                foreach (var k in paths)
                    if (path.Equals(k, StringComparison.OrdinalIgnoreCase) || path.StartsWith(k + "\\", StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }
        }
        public static bool HasKeptInside(string folder)
        {
            lock (gate) return paths.Any(k => k.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
        }
    }

// Folders added to the cleanup list from the Storage map.
    static class Custom
    {
        static readonly object gate = new object();
        static List<string> paths = new List<string>();
        static string FilePath { get { return Path.Combine(Engine.AppDir, "your-folders.txt"); } }
        public static List<string> All { get { lock (gate) return paths.ToList(); } }
        public static void Load()
        {
            try { lock (gate) paths = File.Exists(FilePath) ? File.ReadAllLines(FilePath).Where(l => l.Trim().Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList() : new List<string>(); }
            catch { }
        }
        static void Save() { try { Directory.CreateDirectory(Engine.AppDir); lock (gate) File.WriteAllLines(FilePath, paths); } catch { } }
        public static void Add(string p) { p = p.TrimEnd('\\'); lock (gate) { if (!paths.Any(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase))) paths.Add(p); } Save(); }
        public static void Remove(string p) { lock (gate) paths.RemoveAll(x => string.Equals(x, p.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)); Save(); }

        // Inside a folder you added: the files and sub-folders you unticked, which that item never cleans.
        static HashSet<string> skip;
        static string SkipPath { get { return Path.Combine(Engine.AppDir, "your-folders-skip.txt"); } }
        public static HashSet<string> Skip
        {
            get
            {
                lock (gate)
                {
                    if (skip == null)
                        try { skip = new HashSet<string>(File.Exists(SkipPath) ? File.ReadAllLines(SkipPath).Where(l => l.Trim().Length > 0) : new string[0], StringComparer.OrdinalIgnoreCase); }
                        catch { skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
                    return new HashSet<string>(skip, StringComparer.OrdinalIgnoreCase);
                }
            }
        }
        public static bool IsSkipped(string path, HashSet<string> set)
        {
            if (set == null || set.Count == 0) return false;
            // walk up by plain string cuts: no path parsing, so an odd or very long path can't throw
            var p = Engine.Plain(path).TrimEnd('\\');
            while (p.Length > 0)
            {
                if (set.Contains(p)) return true;
                int cut = p.LastIndexOf('\\');
                if (cut <= 2) break;   // stop at the drive ("C:\")
                p = p.Substring(0, cut);
            }
            return false;
        }
        public static void SetSkipped(string path, bool skipped)
        {
            path = path.TrimEnd('\\');
            var s = Skip;
            if (skipped) s.Add(path); else s.Remove(path);
            lock (gate) skip = s;
            try { Directory.CreateDirectory(Engine.AppDir); File.WriteAllLines(SkipPath, s); } catch { }
        }
    }
    // Free-space history (one point per hour at most) and the total freed, read back from the log.
    static class History
    {
        static string FilePath { get { return Path.Combine(Engine.AppDir, "history.csv"); } }

        public static void Record(long free)
        {
            try
            {
                var pts = Read();
                if (pts.Count > 0 && (DateTime.Now - pts[pts.Count - 1].Key).TotalMinutes < 60) return;
                Directory.CreateDirectory(Engine.AppDir);
                File.AppendAllText(FilePath, DateTime.Now.ToString("s", CultureInfo.InvariantCulture) + "," + free + Environment.NewLine);
            }
            catch { }
        }

        public static List<KeyValuePair<DateTime, long>> Read()
        {
            var list = new List<KeyValuePair<DateTime, long>>();
            try
            {
                if (!File.Exists(FilePath)) return list;
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    var p = line.Split(','); DateTime t; long v;
                    if (p.Length == 2 && DateTime.TryParse(p[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out t) && long.TryParse(p[1], out v))
                        list.Add(new KeyValuePair<DateTime, long>(t, v));
                }
            }
            catch { }
            return list;
        }

        // Sum of "CLEANED 12.3 GB" / "AUTO-CLEANED ..." / "STOPPED ..." lines in the log, and how many cleans.
        public static long FreedAllTime(out int cleans)
        {
            cleans = 0; long total = 0;
            try
            {
                var log = Path.Combine(Engine.AppDir, "log.txt");
                if (!File.Exists(log)) return 0;
                var rx = new Regex(@"(?:CLEANED|STOPPED)\s+([\d.,]+)\s*(TB|GB|MB|KB)", RegexOptions.IgnoreCase);
                foreach (var line in File.ReadAllLines(log))
                {
                    if (line.Contains("Test folder")) continue;
                    var m = rx.Match(line); if (!m.Success) continue;
                    double v; if (!double.TryParse(m.Groups[1].Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) continue;
                    string u = m.Groups[2].Value.ToUpperInvariant();
                    double mult = u == "TB" ? 1099511627776.0 : u == "GB" ? 1073741824.0 : u == "MB" ? 1048576.0 : 1024.0;
                    total += (long)(v * mult); cleans++;
                }
            }
            catch { }
            return total;
        }
    }

    // Remembers each item's tick, age and delete/recycle choice between runs.
    static class Settings
    {
        static string FilePath { get { return Path.Combine(Engine.AppDir, "settings.txt"); } }

        public static void Load(List<Category> cats)
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var lines = File.ReadAllLines(FilePath);
                if (lines.Length == 0 || lines[0] != "v4") return;   // older settings are ignored (new defaults: only Safe ticked)
                foreach (var line in lines.Skip(1))
                {
                    var p = line.Split('|');
                    if (p.Length < 3) continue;
                    var c = cats.FirstOrDefault(x => x.Id == p[0]);
                    if (c == null) continue;
                    c.Checked = p[1] == "1";
                    int m; if (int.TryParse(p[2], out m) && m >= 0) c.AgeMinutes = c.AgeChoice ? m : 0;   // no age on caches
                    // field 4 (an old per-item Recycle Bin choice) is ignored: the confirm window decides now
                    int la; if (p.Length >= 5 && int.TryParse(p[4], out la) && la > 0) c.LastAge = la;
                }
            }
            catch { }
        }

        public static void Save(List<Category> cats)
        {
            try
            {
                Directory.CreateDirectory(Engine.AppDir);
                File.WriteAllLines(FilePath, new[] { "v4" }.Concat(cats.Select(c => c.Id + "|" + (c.Checked ? "1" : "0") + "|" + c.AgeMinutes + "|" + (c.Recycle ? "1" : "0") + "|" + c.LastAge)));
            }
            catch { }
        }
    }
}
