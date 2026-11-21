// Public totals, only if you say yes (asked once). Sent: a random ID made on this PC, the version, "windows", and the
// total space freed and number of cleans so far. Never file names, paths, or anything about what was cleaned.
// At most once a day, and after a clean. The choice and the ID live in share.txt next to the settings.
using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace SpaceHunter
{
    static class Stats
    {
        public const string Endpoint = "https://space-hunter-stats.knox-101.workers.dev";   // the stats/ worker
        public const string Explained = "https://github.com/knox-101/space-hunter#public-totals";
        static string FilePath { get { return Path.Combine(Engine.AppDir, "share.txt"); } }

        static string choice, id; static DateTime last = DateTime.MinValue; static bool loaded;
        static void Load()
        {
            if (loaded) return; loaded = true;
            try
            {
                if (!File.Exists(FilePath)) return;
                var l = File.ReadAllLines(FilePath);
                if (l.Length > 0) choice = l[0].Trim();
                if (l.Length > 1) id = l[1].Trim();
                long t; if (l.Length > 2 && long.TryParse(l[2].Trim(), out t)) last = new DateTime(t, DateTimeKind.Utc);
            }
            catch { }
        }
        static void Save()
        {
            try { Directory.CreateDirectory(Engine.AppDir); File.WriteAllLines(FilePath, new[] { choice ?? "", id ?? "", last.Ticks.ToString() }); } catch { }
        }

        public static bool Asked { get { Load(); return choice == "yes" || choice == "no"; } }
        public static bool On { get { Load(); return choice == "yes"; } }

        public static void Set(bool on)
        {
            Load(); choice = on ? "yes" : "no";
            if (on && string.IsNullOrEmpty(id)) id = Guid.NewGuid().ToString();
            if (!on) last = DateTime.MinValue;
            Save();
        }

        // force: after a clean, so the total includes it
        public static void MaybeSend(bool force)
        {
            Load();
            if (!On || string.IsNullOrEmpty(id)) return;
            if (!force && (DateTime.UtcNow - last).TotalHours < 20) return;
            int cleans; long freed = History.FreedAllTime(out cleans);
            string body = "{\"id\":\"" + id + "\",\"v\":\"" + AppVersion.Text + "\",\"os\":\"windows\",\"freed\":" + Math.Max(0, freed) + ",\"cleans\":" + Math.Max(0, cleans) + "}";
            new Thread(() =>
            {
                try
                {
                    ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;   // TLS 1.2
                    var req = (HttpWebRequest)WebRequest.Create(Endpoint + "/ping");
                    req.Method = "POST"; req.ContentType = "application/json"; req.UserAgent = "SpaceHunter/" + AppVersion.Text; req.Timeout = 8000;
                    var bytes = Encoding.UTF8.GetBytes(body); req.ContentLength = bytes.Length;
                    using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                    using (var r = (HttpWebResponse)req.GetResponse())
                        if ((int)r.StatusCode < 300) { last = DateTime.UtcNow; Save(); }
                }
                catch { }   // offline or blocked: try again another day
            }) { IsBackground = true }.Start();
        }
    }
}
