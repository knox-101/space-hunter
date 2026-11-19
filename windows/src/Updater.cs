// Copyright (c) 2026 Blesson Reji (knox-101). Space Hunter is free software under the GNU GPL v3 or later; see LICENSE.
// SPDX-License-Identifier: GPL-3.0-or-later
// Space Hunter (Windows) - updates. Once per launch the app asks GitHub for the latest release of this repository
// (one small request; nothing about you is sent). If a newer version exists, a bar offers it. Installing downloads
// the new exe and its signature, refuses it unless the signature matches the project's public key (built into this
// exe), swaps the files and relaunches. Without a public key built in, the app never installs anything.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace SpaceHunter
{
    class Release
    {
        public Version Version; public string Tag, Page, ExeUrl, SigUrl;
    }

    static class Updater
    {
        public const string Repo = "knox-101/space-hunter";
        const string ExeAsset = "SpaceHunter.exe", SigAsset = "SpaceHunter.exe.sig";
        static string Dir { get { return Path.Combine(Engine.AppDir, "update"); } }

        public static Version Current { get { return Version.Parse(AppVersion.Text); } }

        // The public half of the release signing key, embedded at build time (null if this build has none).
        static string PublicKey()
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("update-key.xml"))
                if (s != null) using (var r = new StreamReader(s)) return r.ReadToEnd();
            return null;
        }
        public static bool CanInstall { get { return PublicKey() != null; } }

        // After an update, the previous exe is left as SpaceHunter.exe.old; remove it on the next start.
        public static void CleanUpOld()
        {
            try { var old = Application.ExecutablePath + ".old"; if (File.Exists(old)) File.Delete(old); } catch { }
        }

        static WebClient Client()
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;   // TLS 1.2
            var w = new WebClient();
            w.Headers[HttpRequestHeader.UserAgent] = "SpaceHunter-updater";
            w.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
            return w;
        }

        // A newer release, or null (offline, no release yet, or already up to date). Never throws.
        public static Release Check()
        {
            try
            {
                string json;
                using (var w = Client()) json = w.DownloadString("https://api.github.com/repos/" + Repo + "/releases/latest");
                var d = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);
                string tag = (d["tag_name"] ?? "").ToString();
                Version v; if (!Version.TryParse(tag.TrimStart('v', 'V'), out v) || v <= Current) return null;
                var r = new Release { Version = v, Tag = tag, Page = d.ContainsKey("html_url") ? (d["html_url"] ?? "").ToString() : null };
                foreach (Dictionary<string, object> a in (IEnumerable)d["assets"])
                {
                    string name = (a["name"] ?? "").ToString(), url = (a["browser_download_url"] ?? "").ToString();
                    if (name == ExeAsset) r.ExeUrl = url; else if (name == SigAsset) r.SigUrl = url;
                }
                return r.ExeUrl != null && r.SigUrl != null ? r : null;
            }
            catch { return null; }
        }

        // Downloads, checks the signature, swaps the exe and starts the new one. Returns null when the caller should
        // quit (the new version is starting), or a sentence saying why it didn't update.
        public static string Install(Release r, Action<string> say)
        {
            string key = PublicKey();
            if (key == null) return "This copy wasn't built with the release key, so it can't check updates. Download the new version from GitHub instead.";
            string exe = Application.ExecutablePath, fresh = Path.Combine(Dir, ExeAsset);
            try
            {
                Directory.CreateDirectory(Dir);
                say("Downloading Space Hunter " + r.Version + " ...");
                byte[] data, sig;
                using (var w = Client()) { data = w.DownloadData(r.ExeUrl); sig = w.DownloadData(r.SigUrl); }
                say("Checking its signature ...");
                if (!Verify(data, sig, key)) return "The download's signature didn't match, so it wasn't installed. Nothing was changed.";
                File.WriteAllBytes(fresh, data);
                say("Installing ...");
                string old = exe + ".old";
                if (File.Exists(old)) File.Delete(old);
                File.Move(exe, old);   // a running exe can't be overwritten, but it can be renamed
                try { File.Copy(fresh, exe); }
                catch { File.Move(old, exe); throw; }   // put the old one back if the copy fails
                File.Delete(fresh);
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                return null;
            }
            catch (Exception e) { return "The update didn't install (" + e.Message + "). Nothing was changed."; }
        }

        public static bool Verify(byte[] data, byte[] sig, string publicKeyXml)
        {
            try
            {
                using (var rsa = new RSACryptoServiceProvider(new CspParameters(24)))   // provider 24 knows SHA-256
                {
                    rsa.PersistKeyInCsp = false;
                    rsa.FromXmlString(publicKeyXml);
                    return rsa.VerifyData(data, "SHA256", sig);
                }
            }
            catch { return false; }
        }
    }
}
