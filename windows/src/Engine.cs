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
using Microsoft.Win32;

namespace SpaceHunter
{
    enum Kind { Contents, Subfolders, RunHistory, RecycleBin, WindowsOld }
    enum Level { Safe, Review, Caution }

    class Category
    {
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
        public bool IsCustom;                           // a folder the user added from Find big folders
        public string What, Deletes, SafeText, After;   // expanded notes
        public string Short;                            // one line: what happens if cleared
        public long Files, Bytes;
        public bool Scanned;
        public bool Safe { get { return Level == Level.Safe; } }
        public bool Blocked { get { return AdminOnly && !Engine.IsAdmin; } }   // can't be measured without permission
        public void ResetToDefault()
        {
            Checked = Level == Level.Safe; AgeMinutes = DefaultAgeMinutes;
            LastAge = DefaultAgeMinutes > 0 ? DefaultAgeMinutes : 14 * Engine.Day; Recycle = false;
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

            // folders the user sent here from "Find big folders"
            Custom.Load();
            foreach (var p in Custom.All)
            {
                var c = new Category { Id = "custom:" + p.ToLowerInvariant(), Name = Path.GetFileName(p.TrimEnd('\\')) + "  (your folder)",
                                       Level = Level.Caution, Kind = Kind.Contents, DefaultAgeMinutes = 30 * Day, AgeMinutes = 30 * Day, LastAge = 30 * Day };
                c.Roots.Add(p);
                c.IsCustom = true; c.Checked = false;   // your folders start unticked, older than 30 days
                c.Note = "A folder you added";
                c.Short = "Everything inside goes; the folder itself stays";
                c.What = "A folder you sent here from Find big folders: " + p;
                c.Deletes = "Every file inside it (older than the age you set), and the sub-folders that end up empty. The folder itself stays.";
                c.SafeText = "Only you know. Open it and check before ticking. Protected folders inside it are skipped.";
                c.After = "The files are gone, unless you choose Recycle Bin in the confirm window.";
                list.Add(c);
            }
            Settings.Load(list);
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
                if (delete && info.i64NumItems > 0)
                {
                    cnt.Current = "Emptying the Recycle Bin";
                    if (SHEmptyRecycleBin(IntPtr.Zero, null, 7) != 0) return;
                    if (cnt.OnItem != null) cnt.OnItem("Recycle Bin emptied (" + info.i64NumItems + " items)", info.i64Size, true);
                }
                cnt.Files += info.i64NumItems; cnt.Bytes += info.i64Size;
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

            var bin = delete && c.Recycle ? new RecycleBatch(cnt) : null;
            foreach (var root in c.Roots)
            {
                if (cnt.Cancel) break;
                // personal folders (e.g. Downloads) are only ever touched through an extension filter, top level only
                if (!Directory.Exists(root) || (!IsSafeRoot(root) && !(c.Extensions != null && c.TopLevelOnly))) continue;
                var rd = new DirectoryInfo(root);
                if ((rd.Attributes & FileAttributes.ReparsePoint) != 0) continue;   // never follow a root that is a link
                if (Keep.IsKept(rd.FullName)) continue;
                var ext = c.Extensions;
                Action<FileInfo> each = f => { if (ext == null || ext.Contains(f.Extension)) Handle(f, cutoff, delete, cnt, bin); };
                if (c.Kind == Kind.Contents && c.TopLevelOnly)
                {
                    FileInfo[] files; try { files = rd.GetFiles(); } catch { files = new FileInfo[0]; }
                    foreach (var f in files) { if (cnt.Cancel) break; if ((f.Attributes & FileAttributes.ReparsePoint) == 0) each(f); }
                    if (bin != null) bin.Flush();
                }
                else if (c.Kind == Kind.Contents)
                {
                    Walk(rd, each, cnt, exclude);
                    if (bin != null) bin.Flush();
                    if (delete) RemoveEmptyDirs(rd, true, exclude);
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
                        if (Newest(s, cnt) >= cutoff) continue;
                        Walk(s, f => Handle(f, DateTime.MaxValue, delete, cnt, bin), cnt, exclude);
                        if (bin != null) bin.Flush();
                        if (delete) RemoveEmptyDirs(s, false, exclude);
                    }
                }
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

        static void Handle(FileInfo f, DateTime cutoff, bool delete, Counter cnt, RecycleBatch bin)
        {
            DateTime t; long len;
            try { t = f.LastWriteTime; len = f.Length; } catch { return; }
            if (Keep.IsKept(f.FullName)) return;   // on the keep list: never touched, never counted
            if (t >= cutoff) { if (delete) cnt.Skipped++; return; }
            if (!delete) { cnt.Files++; cnt.Bytes += len; return; }
            cnt.Current = f.FullName;
            if (bin != null) { bin.Add(f.FullName, len); return; }
            bool ok = TryDelete(f);
            if (ok) { cnt.Files++; cnt.Bytes += len; } else cnt.Skipped++;
            if (cnt.OnItem != null) cnt.OnItem(f.FullName, len, ok);
        }

        static bool TryDelete(FileInfo f)
        {
            try
            {
                if ((f.Attributes & FileAttributes.ReadOnly) != 0) f.Attributes = FileAttributes.Normal;
                f.Delete();
                return true;
            }
            catch { return false; }
        }

        static void Walk(DirectoryInfo d, Action<FileInfo> onFile, Counter cnt, HashSet<string> exclude)
        {
            FileSystemInfo[] items;
            try { items = d.GetFileSystemInfos(); } catch { return; }
            foreach (var i in items)
            {
                if (cnt.Cancel) return;
                if ((i.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                var sub = i as DirectoryInfo;
                if (sub != null) { if (!exclude.Contains(sub.Name) && !Keep.IsKept(sub.FullName)) Walk(sub, onFile, cnt, exclude); }
                else onFile((FileInfo)i);
            }
        }

        static DateTime Newest(DirectoryInfo d, Counter cnt)
        {
            DateTime n = d.LastWriteTime;
            Walk(d, f => { try { if (f.LastWriteTime > n) n = f.LastWriteTime; } catch { } }, cnt, new HashSet<string>());
            return n;
        }

        static void RemoveEmptyDirs(DirectoryInfo d, bool isRoot, HashSet<string> exclude)
        {
            DirectoryInfo[] subs;
            try { subs = d.GetDirectories(); } catch { return; }
            foreach (var s in subs)
                if ((s.Attributes & FileAttributes.ReparsePoint) == 0 && !exclude.Contains(s.Name) && !Keep.IsKept(s.FullName)) RemoveEmptyDirs(s, false, exclude);
            if (!isRoot)
                try { if (d.GetFileSystemInfos().Length == 0) { if ((d.Attributes & FileAttributes.ReadOnly) != 0) d.Attributes &= ~FileAttributes.ReadOnly; d.Delete(); } } catch { }
        }

        public static string Fmt(long b)
        {
            if (b >= 1L << 40) return (b / 1099511627776.0).ToString("0.00") + " TB";
            if (b >= 1L << 30) return (b / 1073741824.0).ToString("0.0") + " GB";
            if (b >= 1L << 20) return (b / 1048576.0).ToString("0") + " MB";
            if (b > 0) return (b / 1024.0).ToString("0") + " KB";
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

        // Can a folder from Find big folders be added to the cleanup list? Never a drive, Windows, Program Files,
        // or anything that holds the profile, AppData, Documents, Desktop, Downloads or OneDrive.
        public static bool CanAdd(string path, out string why)
        {
            why = null;
            if (string.IsNullOrEmpty(path) || path.TrimEnd('\\').Length <= 2) { why = "a whole drive"; return false; }   // "C:" alone means the current folder
            string p = Norm(path);
            if (p.Length <= 3) { why = "a whole drive"; return false; }
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            foreach (var sys in new[] { W, pf, pf86, PD })
                if (!string.IsNullOrEmpty(sys) && Under(p, Norm(sys))) { why = "part of Windows or installed programs"; return false; }
            foreach (var keep in new[] { H, L, R, Path.GetDirectoryName(H) }.Concat(PersonalFolders()))
                if (!string.IsNullOrEmpty(keep) && Under(Norm(keep), p)) { why = "it holds your profile or personal folders"; return false; }
            if (PersonalFolderOf(p) != null) { why = "it's inside your personal files (" + Path.GetFileName(PersonalFolderOf(p)) + ")"; return false; }
            if (Keep.IsKept(p)) { why = "it's a protected folder"; return false; }
            if (Custom.All.Any(c => Under(p, Norm(c)))) { why = "it's already on the cleanup list"; return false; }
            return true;
        }

        // Which cleanup-list items already cover a folder (it's inside them) or sit inside it.
        public static string Coverage(string path, List<Category> cats, out bool inside)
        {
            inside = false; string p = Norm(path);
            foreach (var c in cats)
                foreach (var r in c.Roots)
                    if (Under(p, Norm(r))) { inside = true; return c.Name; }
            var holds = cats.Where(c => c.Roots.Any(r => Under(Norm(r), p))).Select(c => c.Name).Distinct().ToList();
            return holds.Count == 0 ? null : string.Join(", ", holds.Take(2)) + (holds.Count > 2 ? " +" + (holds.Count - 2) : "");
        }

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

        public void Add(string path, long size)
        {
            // a file bigger than the Recycle Bin can hold would be deleted for good by Windows: keep it instead
            long limit = Engine.BinLimit(path);
            if (size > limit)
            {
                cnt.Skipped++;
                if (cnt.OnNote != null) cnt.OnNote("kept, too big for the Recycle Bin (" + Engine.Fmt(size) + "): " + path);
                return;
            }
            pending.Add(new KeyValuePair<string, long>(path, size));
            if (pending.Count >= 200) Flush();
        }

        public void Flush()
        {
            if (pending.Count == 0) return;
            var batch = pending.ToList(); pending.Clear();
            if (Shell(batch.Select(p => p.Key)))
            {
                foreach (var p in batch) Done(p, !File.Exists(p.Key));
                return;
            }
            foreach (var p in batch) Done(p, Shell(new[] { p.Key }) && !File.Exists(p.Key));   // one by one, so one locked file doesn't stop the rest
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
        public static bool IsKept(string path)
        {
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

// Folders added to the cleanup list from Find big folders.
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
                    int m; if (int.TryParse(p[2], out m) && m >= 0) c.AgeMinutes = m;
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
