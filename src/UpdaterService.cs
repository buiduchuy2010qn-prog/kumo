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
        public static volatile bool CheckCompleted = false;
        public static volatile bool HasNewerRelease = false;
        public static string LatestTag = "";
        public static string LatestSetupUrl = "";
        public static string LastCheckedTimeText = "";
        private static int _startupCheckStarted = 0;

        public static void StartSilentStartupCheck()
        {
            if (Interlocked.CompareExchange(ref _startupCheckStarted, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    Thread.Sleep(3800);
                    CheckLatestReleaseAsync(delegate { });
                }
                catch { }
            });
        }

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
                    req.Timeout = 9000;
                    string json;
                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    using (Stream s = resp.GetResponseStream())
                    using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                    {
                        json = sr.ReadToEnd();
                    }
                    Match mTag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
                    if (!mTag.Success)
                    {
                        if (callback != null) callback(false, "", "", false, "No release tag");
                        return;
                    }
                    string latestTag = mTag.Groups[1].Value.Trim();
                    string cleanLatest = latestTag.TrimStart('v', 'V').Trim();
                    string cleanCur = (KumoBuildInfo.Version ?? "").TrimStart('v', 'V').Trim();
                    string dlUrl = "https://github.com/" + KumoBuildInfo.RepoSlug + "/releases/download/" + latestTag + "/KumoSetup-" + latestTag + ".exe";
                    Match mUrl = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]*KumoSetup[^\"]*\\.exe)\"", RegexOptions.IgnoreCase);
                    if (mUrl.Success) dlUrl = mUrl.Groups[1].Value;

                    bool strictlyNewer = IsRemoteStrictlyNewer(cleanCur, cleanLatest);
                    LatestTag = latestTag;
                    LatestSetupUrl = dlUrl;
                    HasNewerRelease = strictlyNewer;
                    LastCheckedTimeText = DateTime.Now.ToString("HH:mm");
                    CheckCompleted = true;

                    if (callback != null) callback(true, latestTag, dlUrl, strictlyNewer, "");
                }
                catch (Exception ex)
                {
                    if (callback != null) callback(false, "", "", false, ex.Message);
                }
            });
        }

        public static bool IsRemoteStrictlyNewer(string cur, string remote)
        {
            if (string.IsNullOrEmpty(remote)) return false;
            if (string.Equals(cur, remote, StringComparison.OrdinalIgnoreCase)) return false;
            int[] a = ParseNums(cur);
            int[] b = ParseNums(remote);
            if (b.Length == 0) return false;
            int len = Math.Max(a.Length, b.Length);
            for (int i = 0; i < len; i++)
            {
                int va = (i < a.Length) ? a[i] : 0;
                int vb = (i < b.Length) ? b[i] : 0;
                if (vb > va) return true;
                if (vb < va) return false;
            }
            return false;
        }

        private static int[] ParseNums(string s)
        {
            MatchCollection mc = Regex.Matches(s ?? "", "\\d+");
            int[] arr = new int[mc.Count];
            for (int i = 0; i < mc.Count; i++)
            {
                int v;
                int.TryParse(mc[i].Value, out v);
                arr[i] = v;
            }
            return arr;
        }

        public static void DownloadAndOverwriteAsync(string setupUrl, Action<int> onProgress, Action<bool, string> callback)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
                    string tempDir = Path.Combine(Path.GetTempPath(), "KumoAutoUpdate");
                    Directory.CreateDirectory(tempDir);
                    string safeTag = string.IsNullOrEmpty(LatestTag) ? ("v" + KumoBuildInfo.Version) : Regex.Replace(LatestTag, "[^0-9A-Za-z._-]", "");
                    string tempExe = Path.Combine(tempDir, "KumoSetup-" + safeTag + "_" + Environment.TickCount + ".exe");

                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(setupUrl);
                    req.UserAgent = "Kumo-Updater/" + KumoBuildInfo.Version;
                    req.Timeout = 25000;
                    req.ReadWriteTimeout = 25000;

                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    {
                        long totalBytes = resp.ContentLength;
                        long received = 0;
                        int lastPct = -1;
                        byte[] buf = new byte[16384];
                        using (Stream s = resp.GetResponseStream())
                        using (FileStream fs = new FileStream(tempExe, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            int n;
                            while ((n = s.Read(buf, 0, buf.Length)) > 0)
                            {
                                fs.Write(buf, 0, n);
                                received += n;
                                if (totalBytes > 0 && onProgress != null)
                                {
                                    int pct = (int)Math.Min(100L, (received * 100L) / totalBytes);
                                    if (pct != lastPct)
                                    {
                                        lastPct = pct;
                                        onProgress(pct);
                                    }
                                }
                            }
                        }
                    }

                    FileInfo fi = new FileInfo(tempExe);
                    if (!fi.Exists || fi.Length < 40960)
                    {
                        throw new IOException("Downloaded setup package is incomplete.");
                    }

                    if (onProgress != null) onProgress(100);

                    ProcessStartInfo psi = new ProcessStartInfo();
                    psi.FileName = tempExe;
                    psi.Arguments = "--silent --relaunch";
                    psi.UseShellExecute = true;
                    Process.Start(psi);
                    if (callback != null) callback(true, "");
                }
                catch (Exception ex)
                {
                    if (callback != null) callback(false, ex.Message);
                }
            });
        }
    }
}