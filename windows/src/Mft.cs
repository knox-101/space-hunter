// Copyright (c) 2026 Blesson Reji (knox-101). Space Hunter is free software under the GNU GPL v3 or later; see LICENSE.
// SPDX-License-Identifier: GPL-3.0-or-later
// Space Hunter (Windows) - measures a whole NTFS drive by reading its master file table (the index NTFS keeps of
// every file) in one sequential pass, instead of asking Windows about each folder. About 5-10x faster on a full drive.
// Read-only: the drive is opened for reading and nothing is written. Needs administrator rights; when it can't run
// (another file system, no rights, anything unexpected) the caller falls back to the folder-by-folder scan.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace SpaceHunter
{
    static class Mft
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetFilePointerEx(SafeFileHandle h, long distance, out long newPos, uint method);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool ReadFile(SafeFileHandle h, byte[] buffer, int toRead, out int read, IntPtr overlapped);

        const uint ATTR_LIST = 0x20, ATTR_FILE_NAME = 0x30, ATTR_DATA = 0x80, ATTR_END = 0xFFFFFFFF;
        internal static Table LastTable;
        internal static string LastInfo;   // test hook: the last scan's table
        const int RootRecord = 5, FirstUserRecord = 16;   // records below 16 are NTFS's own metadata files

        // Can this path be measured from the drive index? (a whole NTFS drive, with admin rights)
        public static bool CanScan(string path)
        {
            if (!Engine.IsAdmin) return false;
            string p = path.TrimEnd('\\');
            if (p.Length != 2 || p[1] != ':') return false;
            try { return new DriveInfo(p + "\\").DriveFormat == "NTFS"; } catch { return false; }
        }

        public static FolderNode Scan(string drive)
        {
            string letter = drive.TrimEnd('\\');
            using (var h = CreateFileW(@"\\.\" + letter, 0x80000000 /* read */, 3 /* share read+write */, IntPtr.Zero, 3 /* open existing */, 0, IntPtr.Zero))
            {
                if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());

                // boot sector: where the table is and how big each record is
                var boot = Read(h, 0, 512);
                if (Encoding.ASCII.GetString(boot, 3, 4) != "NTFS") throw new InvalidDataException("not NTFS");
                int bytesPerSector = BitConverter.ToUInt16(boot, 0x0B);
                long clusterSize = (long)bytesPerSector * boot[0x0D];
                long mftStart = BitConverter.ToInt64(boot, 0x30) * clusterSize;
                sbyte c = (sbyte)boot[0x40];
                int recordSize = c > 0 ? (int)(c * clusterSize) : 1 << -c;
                if (recordSize < 512 || recordSize > 65536 || bytesPerSector < 512) throw new InvalidDataException("odd NTFS geometry");

                // record 0 describes the table itself: where its pieces lie on the disk
                var rec0 = Read(h, mftStart, Math.Max(recordSize, bytesPerSector));
                if (!Fixup(rec0, 0, recordSize)) throw new InvalidDataException("bad first record");
                long tableSize; var runs = TableRuns(h, rec0, recordSize, clusterSize, out tableSize);
                long count = tableSize / recordSize;
                LastInfo = runs.Count + " pieces, " + count.ToString("N0") + " records";
                if (count <= RootRecord || count > int.MaxValue) throw new InvalidDataException("odd table size");

                var t = new Table((int)count);
                const int chunk = 4 << 20;   // read 4 MB at a time, in disk order
                var buf = new byte[chunk];
                long record = 0;
                foreach (var run in runs)
                {
                    long offset = run.Key, left = run.Value;
                    while (left > 0 && record < count)
                    {
                        if (BigFolders.Cancel) throw new OperationCanceledException();
                        int want = (int)Math.Min(chunk, left);
                        int got = ReadInto(h, offset, buf, want);
                        if (got <= 0) break;
                        for (int at = 0; at + recordSize <= got && record < count; at += recordSize, record++)
                            t.Add(buf, at, recordSize, (int)record);
                        offset += got; left -= got;
                    }
                }
                var root = t.Build(letter + "\\");
                LastTable = t;
                return root;
            }
        }

        // The table's pieces on disk, as (byte offset, byte length). On a big or old drive the table grows in many pieces
        // and the list of pieces itself spills into other records (named in record 0's attribute list); read those too.
        static List<KeyValuePair<long, long>> TableRuns(SafeFileHandle h, byte[] rec0, int recordSize, long clusterSize, out long dataSize)
        {
            dataSize = -1;
            var runs = new List<KeyValuePair<long, long>>();
            var more = new List<KeyValuePair<long, long>>();   // (first cluster in the table, record that holds those pieces)
            int off = BitConverter.ToUInt16(rec0, 0x14);
            while (off + 16 <= recordSize)
            {
                uint type = BitConverter.ToUInt32(rec0, off); if (type == ATTR_END) break;
                int len = BitConverter.ToInt32(rec0, off + 4); if (len <= 0 || off + len > recordSize) break;
                if (type == ATTR_DATA && rec0[off + 8] != 0 && rec0[off + 9] == 0)
                {
                    if (BitConverter.ToInt64(rec0, off + 0x10) == 0) dataSize = BitConverter.ToInt64(rec0, off + 0x30);
                    runs.AddRange(Runs(rec0, off, len, clusterSize));
                }
                else if (type == ATTR_LIST)
                {
                    // the list itself is either inside record 0, or (when long) in clusters of its own
                    byte[] list; int v, vEnd;
                    if (rec0[off + 8] == 0) { list = rec0; v = off + BitConverter.ToUInt16(rec0, off + 0x14); vEnd = v + BitConverter.ToInt32(rec0, off + 0x10); }
                    else
                    {
                        long listSize = BitConverter.ToInt64(rec0, off + 0x30);
                        if (listSize <= 0 || listSize > (16 << 20)) throw new InvalidDataException("odd table piece list");
                        var ms = new MemoryStream();
                        foreach (var lr in Runs(rec0, off, len, clusterSize))
                        {
                            if (ms.Length >= listSize) break;
                            var part = Read(h, lr.Key, (int)Math.Min(lr.Value, listSize - ms.Length + clusterSize - 1) / (int)clusterSize * (int)clusterSize);
                            ms.Write(part, 0, part.Length);
                        }
                        if (ms.Length < listSize) throw new InvalidDataException("table piece list incomplete");
                        list = ms.ToArray(); v = 0; vEnd = (int)listSize;
                    }
                    while (v + 26 <= vEnd)
                    {
                        uint et = BitConverter.ToUInt32(list, v); int elen = BitConverter.ToUInt16(list, v + 4); if (elen <= 0) break;
                        long startVcn = BitConverter.ToInt64(list, v + 8), rec = BitConverter.ToInt64(list, v + 0x10) & 0xFFFFFFFFFFFFL;
                        if (et == ATTR_DATA && list[v + 6] == 0 && rec != 0) more.Add(new KeyValuePair<long, long>(startVcn, rec));
                        v += elen;
                    }
                }
                off += len;
            }
            foreach (var m in more.OrderBy(x => x.Key))
            {
                long at = OffsetOf(runs, m.Value * recordSize);   // the extra records sit in a piece we already know
                if (at < 0) throw new InvalidDataException("table piece record not found");
                var r = Read(h, at, recordSize);
                if (!Fixup(r, 0, recordSize)) throw new InvalidDataException("bad table piece record");
                int o = BitConverter.ToUInt16(r, 0x14);
                while (o + 16 <= recordSize)
                {
                    uint type = BitConverter.ToUInt32(r, o); if (type == ATTR_END) break;
                    int len = BitConverter.ToInt32(r, o + 4); if (len <= 0 || o + len > recordSize) break;
                    if (type == ATTR_DATA && r[o + 8] != 0 && r[o + 9] == 0 && BitConverter.ToInt64(r, o + 0x10) == m.Key)
                        runs.AddRange(Runs(r, o, len, clusterSize));
                    o += len;
                }
            }
            if (dataSize <= 0) throw new InvalidDataException("no table data");
            // never measure from part of the table: if the pieces don't cover all of it, the caller scans folder by folder
            if (runs.Sum(x => x.Value) < dataSize) throw new InvalidDataException("table pieces incomplete");
            return runs;
        }

        // Where byte `pos` of the table lies on the disk (-1 if outside the pieces given).
        static long OffsetOf(List<KeyValuePair<long, long>> runs, long pos)
        {
            foreach (var r in runs) { if (pos < r.Value) return r.Key + pos; pos -= r.Value; }
            return -1;
        }

        static IEnumerable<KeyValuePair<long, long>> Runs(byte[] r, int off, int len, long clusterSize)
        {
            int p = off + BitConverter.ToUInt16(r, off + 0x20);
            long lcn = 0;
            while (p < off + len && r[p] != 0)
            {
                int lenBytes = r[p] & 0xF, offBytes = r[p] >> 4; p++;
                long clusters = 0; for (int i = 0; i < lenBytes; i++) clusters |= (long)r[p + i] << (8 * i);
                p += lenBytes;
                if (offBytes == 0) throw new InvalidDataException("sparse table piece");   // would shift every record after it
                long delta = 0; for (int i = 0; i < offBytes; i++) delta |= (long)r[p + i] << (8 * i);
                if (offBytes < 8 && (r[p + offBytes - 1] & 0x80) != 0) delta |= -1L << (8 * offBytes);   // signed
                p += offBytes;
                lcn += delta;
                yield return new KeyValuePair<long, long>(lcn * clusterSize, clusters * clusterSize);
            }
        }

        // NTFS stores the last two bytes of each 512-byte stride elsewhere, to detect torn writes; put them back.
        static bool Fixup(byte[] b, int at, int size)
        {
            if (b[at] != 'F' || b[at + 1] != 'I' || b[at + 2] != 'L' || b[at + 3] != 'E') return false;
            int usa = BitConverter.ToUInt16(b, at + 4), n = BitConverter.ToUInt16(b, at + 6);
            if (n < 2 || usa + n * 2 > size) return false;
            int stride = size / (n - 1);
            for (int i = 1; i < n; i++)
            {
                int pos = at + i * stride - 2;
                if (b[pos] != b[at + usa] || b[pos + 1] != b[at + usa + 1]) return false;
                b[pos] = b[at + usa + i * 2]; b[pos + 1] = b[at + usa + i * 2 + 1];
            }
            return true;
        }

        static byte[] Read(SafeFileHandle h, long offset, int size)
        {
            var b = new byte[size];
            if (ReadInto(h, offset, b, size) != size) throw new IOException("short read");
            return b;
        }
        static int ReadInto(SafeFileHandle h, long offset, byte[] b, int size)
        {
            long pos; int got;
            if (!SetFilePointerEx(h, offset, out pos, 0) || !ReadFile(h, b, size, out got, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return got;
        }

        // Everything needed per record, in flat arrays (a full drive has millions of records).
        internal class Table
        {
            public readonly List<long> Links = new List<long>();
            readonly Dictionary<int, List<KeyValuePair<int, string>>> Alt = new Dictionary<int, List<KeyValuePair<int, string>>>();   // (file record << 32) | folder record of each extra hard link
            readonly int[] parent; readonly long[] size; readonly byte[] flags, nameKind; readonly string[] name;
            const byte InUse = 1, Dir = 2;

            public Table(int n)
            {
                parent = new int[n]; size = new long[n]; flags = new byte[n]; nameKind = new byte[n]; name = new string[n];
                for (int i = 0; i < n; i++) parent[i] = -1;
            }

            public int BadFixup, Free, Used;
            public string Describe(int rec)
            {
                if (rec < 0 || rec >= parent.Length) return "record " + rec + " outside table of " + parent.Length;
                return "record " + rec + ": flags " + flags[rec] + ", parent " + parent[rec] + ", name '" + name[rec] + "' (kind " + nameKind[rec] + "), size " + size[rec]
                     + ", node " + (Nodes != null && Nodes[rec] != null ? "yes, path " + Nodes[rec].Path : "no");
            }

            public void Add(byte[] b, int at, int recSize, int rec)
            {
                if (!Fixup(b, at, recSize)) { if (b[at] == 'F' && b[at + 1] == 'I') BadFixup++; return; }
                if ((BitConverter.ToUInt16(b, at + 0x16) & 1) == 0) Free++; else Used++;
                int recFlags = BitConverter.ToUInt16(b, at + 0x16);
                if ((recFlags & 1) == 0) return;   // a free record
                long baseRef = BitConverter.ToInt64(b, at + 0x20) & 0xFFFFFFFFFFFFL;
                int owner = baseRef == 0 ? rec : (int)baseRef;   // extension records add to their base file
                if (owner < 0 || owner >= parent.Length) return;
                if (baseRef == 0) { flags[owner] |= InUse; if ((recFlags & 2) != 0) flags[owner] |= Dir; }

                int off = at + BitConverter.ToUInt16(b, at + 0x14), end = at + recSize;
                while (off + 16 <= end)
                {
                    uint type = BitConverter.ToUInt32(b, off); if (type == ATTR_END) break;
                    int len = BitConverter.ToInt32(b, off + 4); if (len <= 0 || off + len > end) break;
                    bool nonResident = b[off + 8] != 0; int attrNameLen = b[off + 9];
                    if (type == ATTR_FILE_NAME && !nonResident)
                    {
                        int v = off + BitConverter.ToUInt16(b, off + 0x14);
                        int ns = b[v + 0x41];                       // 0 POSIX, 1 Win32, 2 DOS 8.3, 3 Win32+DOS
                        int rank = ns == 2 ? 1 : 2;                 // the short 8.3 name only if there's nothing better
                        if (rank == 2 && nameKind[owner] == 2)   // a second full name: a hard link, or a packaged app's view of a folder
                        {
                            int altParent = (int)(BitConverter.ToInt64(b, v) & 0xFFFFFFFFFFFFL);
                            Links.Add(((long)owner << 32) | (uint)altParent);
                            List<KeyValuePair<int, string>> l;
                            if (!Alt.TryGetValue(owner, out l)) Alt[owner] = l = new List<KeyValuePair<int, string>>();
                            l.Add(new KeyValuePair<int, string>(altParent, Encoding.Unicode.GetString(b, v + 0x42, b[v + 0x40] * 2)));
                        }
                        if (rank > nameKind[owner])
                        {
                            nameKind[owner] = (byte)rank;
                            parent[owner] = (int)(BitConverter.ToInt64(b, v) & 0xFFFFFFFFFFFFL);
                            name[owner] = Encoding.Unicode.GetString(b, v + 0x42, b[v + 0x40] * 2);
                        }
                    }
                    else if (type == ATTR_DATA && attrNameLen == 0)   // the file's main content (not a side stream)
                    {
                        if (!nonResident) size[owner] += BitConverter.ToUInt32(b, off + 0x10);
                        else if (BitConverter.ToInt64(b, off + 0x10) == 0) size[owner] += BitConverter.ToInt64(b, off + 0x30);   // first piece holds the size
                    }
                    off += len;
                }
                if (baseRef == 0 && (recFlags & 2) == 0)
                {
                    Interlocked.Increment(ref BigFolders.Scanned);
                    Interlocked.Add(ref BigFolders.ScannedBytes, size[owner]);
                }
            }

            public FolderNode[] Nodes;
            public IEnumerable<string> FilesIn(int folderRec)
            {
                for (int i = FirstUserRecord; i < parent.Length; i++)
                    if ((flags[i] & (InUse | Dir)) == InUse && parent[i] == folderRec) yield return name[i];
            }
            public long SizeOf(int rec) { return size[rec]; }
            public FolderNode TopOf(int folderRec)
            {
                if (Nodes == null || folderRec < 0 || folderRec >= Nodes.Length) return null;
                var f = Nodes[folderRec]; if (f == null) return null;
                while (f.Parent != null && f.Parent.Parent != null) f = f.Parent;
                return f.Parent == null ? null : f;
            }

            bool Reaches(int rec)
            {
                for (int steps = 0; steps < 4096; steps++)
                {
                    if (rec == RootRecord) return true;
                    if (rec < 0 || rec >= parent.Length) return false;
                    int p = parent[rec]; if (p == rec) return false;
                    rec = p;
                }
                return false;   // a loop
            }

            public FolderNode Build(string rootPath)
            {
                int n = parent.Length;
                // A record with several names: keep the one whose folders lead up to the drive root. (Packaged apps give
                // folders such as AppData\Local a second name inside the app's own folder, which loops back on itself.)
                for (int pass = 0; pass < 3; pass++)
                    foreach (var kv in Alt)
                    {
                        if (Reaches(kv.Key)) continue;
                        foreach (var alt in kv.Value)
                        {
                            int old = parent[kv.Key]; string oldName = name[kv.Key];
                            parent[kv.Key] = alt.Key; name[kv.Key] = alt.Value;
                            if (Reaches(kv.Key)) break;
                            parent[kv.Key] = old; name[kv.Key] = oldName;
                        }
                    }
                var nodes = new FolderNode[n];
                var root = nodes[RootRecord] = new FolderNode { Name = rootPath, Path = rootPath };
                // folders first, linked to their parent folder
                for (int i = FirstUserRecord; i < n; i++)
                    if ((flags[i] & (InUse | Dir)) == (InUse | Dir) && name[i] != null) nodes[i] = new FolderNode { Name = name[i] };
                for (int i = FirstUserRecord; i < n; i++)
                {
                    var f = nodes[i]; if (f == null) continue;
                    int p = parent[i];
                    if (p < 0 || p >= n || p == i || nodes[p] == null) { nodes[i] = null; continue; }   // orphaned or NTFS metadata
                }
                for (int i = FirstUserRecord; i < n; i++)
                {
                    var f = nodes[i]; if (f == null) continue;
                    var par = nodes[parent[i]]; if (par == null) continue;
                    f.Parent = par; par.Kids.Add(f);
                }
                // then every file adds itself to its folder
                for (int i = FirstUserRecord; i < n; i++)
                {
                    if ((flags[i] & (InUse | Dir)) != InUse) continue;
                    int p = parent[i]; if (p < 0 || p >= n) continue;
                    var folder = nodes[p]; if (folder == null) continue;
                    folder.OwnSize += size[i]; folder.OwnFiles++;
                }
                // paths, and anything not reachable from the root (a loop, or under NTFS's hidden $Extend) is dropped
                Nodes = nodes;
                var stack = new Stack<FolderNode>(); stack.Push(root);
                while (stack.Count > 0)
                {
                    var f = stack.Pop();
                    foreach (var k in f.Kids) { k.Path = f == root ? rootPath + k.Name : f.Path + "\\" + k.Name; stack.Push(k); }
                }
                return root;
            }
        }
    }
}
