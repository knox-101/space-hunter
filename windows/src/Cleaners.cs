// Copyright (c) 2026 Blesson Reji (knox-101). Space Hunter is free software under the GNU GPL v3 or later; see LICENSE.
// SPDX-License-Identifier: GPL-3.0-or-later
// Reads the cleanup items from cleaners.json (embedded in the exe). Adding an app means adding an entry there;
// see CONTRIBUTING.md for the fields.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;

namespace SpaceHunter
{
    static class Cleaners
    {
        public static List<Category> Load()
        {
            string json;
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("cleaners.json"))
            using (var r = new StreamReader(s)) json = r.ReadToEnd();
            return Parse(json);
        }

        public static List<Category> Parse(string json)
        {
            var root = (Dictionary<string, object>)new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(json);
            var list = new List<Category>();
            foreach (Dictionary<string, object> d in (IEnumerable)root["items"])
            {
                var c = new Category();
                c.Id = Str(d, "id"); c.Name = Str(d, "name");
                c.Level = ParseLevel(Str(d, "level"));
                c.Kind = ParseKind(Str(d, "kind") ?? "contents");
                c.DefaultAgeMinutes = c.AgeMinutes = ParseAge(Str(d, "age") ?? "0");
                c.LastAge = c.AgeMinutes > 0 ? c.AgeMinutes : 14 * Engine.Day;
                c.Checked = c.Level == Level.Safe;   // only Safe items start ticked
                c.AdminOnly = Bool(d, "admin");
                c.TopLevelOnly = Bool(d, "topLevelOnly");
                c.ExcludeDirs = Strs(d, "exclude").ToArray();
                var ext = Strs(d, "extensions").ToList();
                if (ext.Count > 0) c.Extensions = new HashSet<string>(ext, StringComparer.OrdinalIgnoreCase);
                foreach (var p in Strs(d, "paths"))
                    foreach (var e in Expand(p))
                        if (!c.Roots.Contains(e, StringComparer.OrdinalIgnoreCase)) c.Roots.Add(e);
                c.Note = Str(d, "note") ?? ""; c.Short = Str(d, "short") ?? "";
                c.What = Str(d, "what") ?? ""; c.Deletes = Str(d, "deletes") ?? "";
                c.SafeText = Str(d, "safe") ?? ""; c.After = Str(d, "after") ?? "";
                list.Add(c);
            }
            return list;
        }

        // Only items for apps that are on this PC: a folder item with none of its folders is left out.
        public static bool Installed(Category c)
        {
            if (c.Kind == Kind.RunHistory || c.Kind == Kind.RecycleBin) return true;
            if (c.Roots.Any(r => { try { return Directory.Exists(r); } catch { return false; } })) return true;
            // without admin rights some Windows folders can't even be seen; keep those rows (they clean after one prompt)
            return c.AdminOnly && !Engine.IsAdmin && c.Roots.Any(r => r.StartsWith(Engine.W + "\\", StringComparison.OrdinalIgnoreCase));
        }

        // %LOCALAPPDATA%\Google\Chrome\User Data\*\Cache  ->  every matching folder that exists.
        // A path without wildcards is returned as it is, whether or not it exists.
        public static IEnumerable<string> Expand(string pattern)
        {
            string p = pattern
                .Replace("%TEMP%", Path.GetTempPath().TrimEnd('\\'))
                .Replace("%LOCALAPPDATA%", Engine.L).Replace("%APPDATA%", Engine.R).Replace("%USERPROFILE%", Engine.H)
                .Replace("%PROGRAMDATA%", Engine.PD).Replace("%WINDIR%", Engine.W)
                .Replace("%PROGRAMFILES(X86)%", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86))
                .Replace("%PROGRAMFILES%", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))
                .Replace("%SYSTEMDRIVE%", Path.GetPathRoot(Engine.W).TrimEnd('\\'));
            p = Environment.ExpandEnvironmentVariables(p);
            if (p.Contains("%")) return new string[0];   // an unknown variable: never guess
            if (p.IndexOfAny(new[] { '*', '?' }) < 0) return new[] { p };

            var parts = p.Split('\\');
            var current = new List<string> { parts[0] + "\\" };
            for (int i = 1; i < parts.Length; i++)
            {
                var next = new List<string>();
                foreach (var dir in current)
                {
                    if (parts[i].IndexOfAny(new[] { '*', '?' }) < 0) next.Add(Path.Combine(dir, parts[i]));
                    else
                        try
                        {
                            if (Directory.Exists(dir))
                                foreach (var sub in new DirectoryInfo(dir).GetDirectories(parts[i]))
                                    if ((sub.Attributes & FileAttributes.ReparsePoint) == 0) next.Add(sub.FullName);
                        }
                        catch { }
                }
                current = next;
                if (current.Count == 0) break;
            }
            return current;
        }

        static string Str(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v != null ? v.ToString() : null; }
        static bool Bool(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v is bool && (bool)v; }
        static IEnumerable<string> Strs(Dictionary<string, object> d, string k)
        {
            object v; if (!d.TryGetValue(k, out v) || v == null) return new string[0];
            return ((IEnumerable)v).Cast<object>().Select(o => o.ToString());
        }

        static Level ParseLevel(string s)
        {
            switch (s) { case "safe": return Level.Safe; case "review": return Level.Review; case "caution": return Level.Caution; }
            throw new FormatException("cleaners.json: unknown level '" + s + "'");
        }
        static Kind ParseKind(string s)
        {
            switch (s)
            {
                case "contents": return Kind.Contents; case "subfolders": return Kind.Subfolders;
                case "run-history": return Kind.RunHistory; case "recycle-bin": return Kind.RecycleBin;
                case "windows-old": return Kind.WindowsOld;
            }
            throw new FormatException("cleaners.json: unknown kind '" + s + "'");
        }
        // "0" = everything; "30m", "1h", "14d"
        public static int ParseAge(string s)
        {
            s = s.Trim(); if (s == "0") return 0;
            int n = int.Parse(s.Substring(0, s.Length - 1));
            switch (s[s.Length - 1]) { case 'm': return n; case 'h': return n * Engine.Hour; case 'd': return n * Engine.Day; }
            throw new FormatException("cleaners.json: bad age '" + s + "'");
        }
    }
}
