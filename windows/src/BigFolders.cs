// Copyright (c) 2026 Blesson Reji (knox-101). Space Hunter is free software under the GNU GPL v3 or later; see LICENSE.
// SPDX-License-Identifier: GPL-3.0-or-later
// Space Hunter (Windows) - measures every folder on a drive, so the biggest ones can be found wherever they are.
// Read-only: nothing here deletes anything.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SpaceHunter
{
    class FolderNode
    {
        public string Name, Path;
        public long Size, Files;
        public bool Denied;                     // some of it couldn't be read (needs admin mode)
        public FolderNode Parent;
        public List<FolderNode> Kids = new List<FolderNode>();
        public long OwnFiles, OwnSize;          // files directly in this folder
        public bool IsFile;                     // a row for one file in the Space map's list (read when you open its folder)
    }

    static class BigFolders
    {
        public static long Scanned;             // files seen so far (for the progress bar)
        public static long ScannedBytes;        // bytes seen so far
        public static volatile bool Cancel;
        public static bool UsedIndex;           // the last scan read the drive's file table
        public static volatile string Phase = "";   // "starting", "measuring" or "sorting" (for the display)

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
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern bool FindNextFileW(IntPtr h, out WIN32_FIND_DATAW data);
        [DllImport("kernel32.dll")] static extern bool FindClose(IntPtr h);
        static readonly IntPtr Invalid = new IntPtr(-1);
        const uint DIR = 0x10, REPARSE = 0x400;

        public static FolderNode Scan(string root)
        {
            Scanned = 0; ScannedBytes = 0; Cancel = false; Phase = "starting";
            if (Mft.CanScan(root))   // a whole NTFS drive: read its file table in one pass (much faster)
            {
                try { var r = Mft.Scan(root); Phase = "sorting"; Total(r); UsedIndex = true; return r; }
                catch (OperationCanceledException) { return new FolderNode { Name = root, Path = root }; }
                catch (Exception e) { IndexError = e.Message; }   // anything unexpected: measure folder by folder instead
            }
            return ScanFolders(root);
        }
        public static string IndexError;

        // Folder by folder, through Windows (works on any drive or folder, with or without admin rights).
        public static FolderNode ScanFolders(string root)
        {
            Scanned = 0; ScannedBytes = 0; UsedIndex = false; Phase = "measuring";
            var node = new FolderNode { Name = root, Path = root.TrimEnd('\\') + "\\" };
            // list the top level, then measure each top-level folder in parallel
            var kids = List(node);
            Parallel.ForEach(kids, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount) }, k => Fill(k));
            Phase = "sorting";
            Total(node);
            return node;
        }

        // Reads one folder's entries; returns its sub-folders (already attached to it).
        static List<FolderNode> List(FolderNode n)
        {
            var subs = new List<FolderNode>();
            WIN32_FIND_DATAW d;
            string p = n.Path.EndsWith("\\") ? n.Path : n.Path + "\\";
            IntPtr h = FindFirstFileExW(@"\\?\" + p + "*", 1 /* basic */, out d, 0, IntPtr.Zero, 2 /* large fetch */);
            if (h == Invalid) { n.Denied = true; return subs; }
            try
            {
                do
                {
                    if (Cancel) break;
                    string name = d.cFileName;
                    if (name == "." || name == "..") continue;
                    if ((d.dwFileAttributes & DIR) != 0)
                    {
                        if ((d.dwFileAttributes & REPARSE) != 0) continue;   // never follow links
                        var k = new FolderNode { Name = name, Path = p + name, Parent = n };
                        subs.Add(k);
                    }
                    else
                    {
                        long size = ((long)d.nFileSizeHigh << 32) | d.nFileSizeLow;
                        n.OwnSize += size; n.OwnFiles++;
                        Interlocked.Increment(ref Scanned);
                        Interlocked.Add(ref ScannedBytes, size);
                    }
                } while (FindNextFileW(h, out d));
            }
            finally { FindClose(h); }
            n.Kids = subs;
            return subs;
        }

        static void Fill(FolderNode n)
        {
            if (Cancel) return;
            foreach (var k in List(n)) Fill(k);
        }

        static void Total(FolderNode n)
        {
            n.Size = n.OwnSize; n.Files = n.OwnFiles;
            foreach (var k in n.Kids)
            {
                Total(k);
                n.Size += k.Size; n.Files += k.Files;
                if (k.Denied) n.Denied = true;
            }
            n.Kids.Sort((a, b) => b.Size.CompareTo(a.Size));
        }
    }
}
