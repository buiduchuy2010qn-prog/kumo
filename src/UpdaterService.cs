using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Kumo
{
    internal static class UpdaterService
    {
        public static void CheckLatestReleaseAsync(Action<bool, string, string, bool, string> callback)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
                    string url = "https://api.github.com/repos/" + KumoBuildInfo.RepoSlug + "/releases/latest";
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                    req.UserAgent = "Kumo-Updater/" + KumoBuildInfo.Version;
                    req.Accept = "application/vnd.github+json";
                    req.Timeout = 8000;
                    string json;
                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    using (Stream s = resp.GetResponseStream())
                    using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                    {
                        json = sr.ReadToEnd();
                    }
                    Match mTag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
                    if (!mTag.Success) { callback(false, "", "", false, "No tag"); return; }
                    string latestTag = mTag.Groups[1].Value.Trim();
                    string cleanLatest = latestTag.TrimStart('v', 'V');
                    string cleanCur = (KumoBuildInfo.Version ?? "").TrimStart('v', 'V');
                    string dlUrl = "https://github.com/" + KumoBuildInfo.RepoSlug + "/releases/download/" + latestTag + "/KumoSetup-" + latestTag + ".exe";
                    Match mUrl = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]*KumoSetup[^\"]*\\.exe)\"", RegexOptions.IgnoreCase);
                    if (mUrl.Success) dlUrl = mUrl.Groups[1].Value;
                    callback(true, latestTag, dlUrl, IsNewerOrDiff(cleanCur, cleanLatest), "");
                }
                catch (Exception ex) { callback(false, "", "", false, ex.Message); }
            });
        }

        private static bool IsNewerOrDiff(string cur, string remote)
        {
            if (string.IsNullOrEmpty(remote) || string.Equals(cur, remote, StringComparison.OrdinalIgnoreCase)) return false;
            int[] a = ParseNums(cur);
            int[] b = ParseNums(remote);
            int len = Math.Max(a.Length, b.Length);
            for (int i = 0; i < len; i++)
            {
                int va = (i < a.Length) ? a[i] : 0;
                int vb = (i < b.Length) ? b[i] : 0;
                if (vb > va) return true;
                if (vb < va) return false;
            }
            return true;
        }

        private static int[] ParseNums(string s)
        {
            MatchCollection mc = Regex.Matches(s ?? "", "\\d+");
            int[] arr = new int[mc.Count];
            for (int i = 0; i < mc.Count; i++) { int v; int.TryParse(mc[i].Value, out v); arr[i] = v; }
            return arr;
        }

        public static void DownloadAndOverwriteAsync(string setupUrl, Action<bool, string> callback)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
                    string tempDir = Path.Combine(Path.GetTempPath(), "KumoAutoUpdate");
                    Directory.CreateDirectory(tempDir);
                    string tempExe = Path.Combine(tempDir, "KumoSetup-v" + KumoBuildInfo.Version + "_" + Environment.TickCount + ".exe");
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(setupUrl);
                    req.UserAgent = "Kumo-Updater/" + KumoBuildInfo.Version;
                    req.Timeout = 20000;
                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    using (Stream s = resp.GetResponseStream())
                    using (FileStream fs = new FileStream(tempExe, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        s.CopyTo(fs);
                    }
                    ProcessStartInfo psi = new ProcessStartInfo();
                    psi.FileName = tempExe;
                    psi.Arguments = "--silent --relaunch";
                    psi.UseShellExecute = true;
                    Process.Start(psi);
                    callback(true, "");
                }
                catch (Exception ex) { callback(false, ex.Message); }
            });
        }
    }
}