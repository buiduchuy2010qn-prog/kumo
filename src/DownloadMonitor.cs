using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace Kumo
{
    public struct DownloadSnapshot
    {
        public bool IsActive;
        public bool IsCompleted;
        public bool IsStoreApp;
        public bool HasExactPercent;
        public float Progress01;
        public string FileName;
        public long DownloadedBytes;
        public long TotalBytes;
        public double SpeedBytesPerSec;
        public string TargetPath;
        public DateTime SessionStartedUtc;
    }

    public sealed class DownloadMonitor : IDisposable
    {
        private readonly object _lock = new object();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();
        private readonly Dictionary<string, TrackedPart> _parts = new Dictionary<string, TrackedPart>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DoEntryState> _doStates = new Dictionary<string, DoEntryState>(StringComparer.OrdinalIgnoreCase);

        private volatile bool _running;
        private Thread _worker;
        private DateTime _monitorBootUtc;
        private bool _doBaselineReady = false;
        private DateTime _lastDoPollUtc = DateTime.MinValue;

        private DownloadSnapshot _snap;
        private DateTime _completedUntilUtc = DateTime.MinValue;
        private string _lastCompletedPath = "";
        private DateTime _lastCompletedAtUtc = DateTime.MinValue;

        private sealed class TrackedPart
        {
            public string FullPath;
            public string CleanName;
            public bool IsStoreTemp;
            public long LastBytes;
            public double SmoothedSpeed;
            public DateTime FirstSeenUtc;
            public DateTime LastSampleUtc;
            public DateTime LastGrowthUtc;
        }

        private sealed class DoEntryState
        {
            public string FileId;
            public string Title;
            public long LastBytes;
            public long FileSize;
            public double SmoothedSpeed;
            public DateTime FirstActiveUtc;
            public DateTime LastSampleUtc;
            public DateTime LastGrowthUtc;
            public bool WasActive;
        }

        public DownloadMonitor()
        {
            _monitorBootUtc = DateTime.UtcNow;
            _running = true;
            SetupWatchers();
            _worker = new Thread((ThreadStart)WorkerLoop) { IsBackground = true, Name = "KumoDownloadMon" };
            _worker.Start();
        }

        public DownloadSnapshot GetSnapshot()
        {
            lock (_lock)
            {
                if (_snap.IsCompleted && DateTime.UtcNow >= _completedUntilUtc)
                {
                    _snap = new DownloadSnapshot();
                }
                return _snap;
            }
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
            double kb = bytes / 1024.0;
            if (kb < 1024.0) return string.Format(CultureInfo.InvariantCulture, "{0:0} KB", kb);
            double mb = kb / 1024.0;
            if (mb < 1024.0) return string.Format(CultureInfo.InvariantCulture, "{0:0.0} MB", mb);
            double gb = mb / 1024.0;
            return string.Format(CultureInfo.InvariantCulture, "{0:0.00} GB", gb);
        }

        public static string FormatSpeed(double bps)
        {
            if (bps <= 512.0) return "0 KB/s";
            double kb = bps / 1024.0;
            if (kb < 1000.0) return string.Format(CultureInfo.InvariantCulture, "{0:0} KB/s", kb);
            double mb = kb / 1024.0;
            return string.Format(CultureInfo.InvariantCulture, "{0:0.0} MB/s", mb);
        }

        private static bool IsPartialDownloadExt(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext)) return false;
            return ext.Equals(".crdownload", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".part", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".opdownload", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".download", StringComparison.OrdinalIgnoreCase);
        }

        private static string CleanPartName(string fullPath)
        {
            string name = Path.GetFileName(fullPath) ?? "File";
            string lower = name.ToLowerInvariant();
            string[] suffixes = new string[] { ".crdownload", ".opdownload", ".download", ".part" };
            for (int i = 0; i < suffixes.Length; i++)
            {
                if (lower.EndsWith(suffixes[i], StringComparison.Ordinal))
                {
                    name = name.Substring(0, name.Length - suffixes[i].Length);
                    break;
                }
            }
            if (name.StartsWith("Unconfirmed ", StringComparison.OrdinalIgnoreCase))
            {
                return "T\u1EC7p tr\u00ECnh duy\u1EC7t";
            }
            return string.IsNullOrEmpty(name) ? "File" : name;
        }

        private List<string> DiscoverWatchFolders(out string winGetTemp)
        {
            List<string> dirs = new List<string>();
            winGetTemp = Path.Combine(Path.GetTempPath(), "WinGet");
            try
            {
                using (RegistryKey rk = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders", false))
                {
                    if (rk != null)
                    {
                        object v = rk.GetValue("{374DE290-123F-4565-9164-39C4925E467B}");
                        if (v != null)
                        {
                            string p = Environment.ExpandEnvironmentVariables(v.ToString());
                            if (Directory.Exists(p) && !ContainsDir(dirs, p)) dirs.Add(p);
                        }
                    }
                }
            }
            catch { }

            try
            {
                string defDl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                if (Directory.Exists(defDl) && !ContainsDir(dirs, defDl)) dirs.Add(defDl);
            }
            catch { }

            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string[] browserPrefs = new string[]
            {
                Path.Combine(localApp, @"Google\Chrome\User Data\Default\Preferences"),
                Path.Combine(localApp, @"Microsoft\Edge\User Data\Default\Preferences"),
                Path.Combine(localApp, @"BraveSoftware\Brave-Browser\User Data\Default\Preferences"),
                Path.Combine(localApp, @"CocCoc\Browser\User Data\Default\Preferences")
            };
            for (int i = 0; i < browserPrefs.Length; i++)
            {
                try
                {
                    if (!File.Exists(browserPrefs[i])) continue;
                    using (FileStream fs = new FileStream(browserPrefs[i], FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (StreamReader sr = new StreamReader(fs, Encoding.UTF8))
                    {
                        string json = sr.ReadToEnd();
                        Match m = Regex.Match(json, "\"download\"\\s*:\\s*\\{[^}]*\"default_directory\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
                        if (m.Success)
                        {
                            string customDir = Regex.Unescape(m.Groups[1].Value);
                            if (Directory.Exists(customDir) && !ContainsDir(dirs, customDir)) dirs.Add(customDir);
                        }
                    }
                }
                catch { }
            }
            return dirs;
        }

        private static bool ContainsDir(List<string> list, string dir)
        {
            string norm = dir.TrimEnd('\\', '/');
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i].TrimEnd('\\', '/'), norm, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        private List<string> _browserDirs = new List<string>();
        private string _winGetDir = "";

        private void SetupWatchers()
        {
            _browserDirs = DiscoverWatchFolders(out _winGetDir);
            for (int i = 0; i < _browserDirs.Count; i++)
            {
                AttachWatcher(_browserDirs[i], false);
            }
            if (!string.IsNullOrEmpty(_winGetDir) && Directory.Exists(_winGetDir))
            {
                AttachWatcher(_winGetDir, true);
            }
        }

        private void AttachWatcher(string dir, bool includeSubdirs)
        {
            try
            {
                FileSystemWatcher fsw = new FileSystemWatcher(dir);
                fsw.IncludeSubdirectories = includeSubdirs;
                fsw.NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite;
                fsw.Created += OnFsChanged;
                fsw.Changed += OnFsChanged;
                fsw.Renamed += OnFsRenamed;
                fsw.EnableRaisingEvents = true;
                _watchers.Add(fsw);
            }
            catch { }
        }

        private void OnFsChanged(object sender, FileSystemEventArgs e)
        {
            if (IsPartialDownloadExt(e.FullPath))
            {
                _wake.Set();
            }
        }

        private void OnFsRenamed(object sender, RenamedEventArgs e)
        {
            try
            {
                if (IsPartialDownloadExt(e.OldFullPath) && !IsPartialDownloadExt(e.FullPath))
                {
                    string fn = Path.GetFileName(e.FullPath) ?? "";
                    if (fn.StartsWith("preview_kumo_", StringComparison.OrdinalIgnoreCase)) return;
                    long finalLen = 0;
                    try { if (File.Exists(e.FullPath)) finalLen = new FileInfo(e.FullPath).Length; } catch { }
                    MarkCompleted(fn, finalLen, false, e.FullPath);
                    _wake.Set();
                }
            }
            catch { }
        }

        private void MarkCompleted(string fileName, long totalBytes, bool isStore, string targetPath)
        {
            DateTime now = DateTime.UtcNow;
            lock (_lock)
            {
                if (!string.IsNullOrEmpty(targetPath) &&
                    string.Equals(_lastCompletedPath, targetPath, StringComparison.OrdinalIgnoreCase) &&
                    (now - _lastCompletedAtUtc).TotalSeconds < 3.0)
                {
                    return;
                }
                _lastCompletedPath = targetPath ?? fileName;
                _lastCompletedAtUtc = now;
                _completedUntilUtc = now.AddSeconds(4.2);
                _snap = new DownloadSnapshot
                {
                    IsActive = true,
                    IsCompleted = true,
                    IsStoreApp = isStore,
                    HasExactPercent = true,
                    Progress01 = 1.0f,
                    FileName = string.IsNullOrEmpty(fileName) ? "File" : fileName,
                    DownloadedBytes = totalBytes,
                    TotalBytes = totalBytes,
                    SpeedBytesPerSec = 0.0,
                    TargetPath = targetPath ?? "",
                    SessionStartedUtc = now
                };
            }
        }

        private void WorkerLoop()
        {
            while (_running)
            {
                _wake.WaitOne(450);
                if (!_running) break;

                try
                {
                    DateTime now = DateTime.UtcNow;
                    DownloadSnapshot activeCandidate = new DownloadSnapshot();
                    bool foundActive = false;

                    // 1. Scan Browser partial downloads (.crdownload, .part, .opdownload, .download)
                    for (int d = 0; d < _browserDirs.Count; d++)
                    {
                        string dir = _browserDirs[d];
                        if (!Directory.Exists(dir)) continue;
                        string[] files = null;
                        try { files = Directory.GetFiles(dir); } catch { }
                        if (files == null) continue;

                        for (int i = 0; i < files.Length; i++)
                        {
                            string fp = files[i];
                            if (!IsPartialDownloadExt(fp)) continue;
                            FileInfo fi;
                            try { fi = new FileInfo(fp); if (!fi.Exists) continue; } catch { continue; }

                            // Ignore stale orphaned .crdownload older than 90s with zero recent writes
                            if ((now - fi.LastWriteTimeUtc).TotalSeconds > 25.0 && !_parts.ContainsKey(fp))
                                continue;

                            TrackedPart tp;
                            if (!_parts.TryGetValue(fp, out tp))
                            {
                                tp = new TrackedPart
                                {
                                    FullPath = fp,
                                    CleanName = CleanPartName(fp),
                                    IsStoreTemp = false,
                                    LastBytes = fi.Length,
                                    SmoothedSpeed = 0.0,
                                    FirstSeenUtc = now,
                                    LastSampleUtc = now,
                                    LastGrowthUtc = now
                                };
                                _parts[fp] = tp;
                            }
                            else
                            {
                                double dt = (now - tp.LastSampleUtc).TotalSeconds;
                                if (dt >= 0.15)
                                {
                                    long delta = fi.Length - tp.LastBytes;
                                    if (delta > 0)
                                    {
                                        double inst = delta / dt;
                                        tp.SmoothedSpeed = (tp.SmoothedSpeed <= 1.0) ? inst : (tp.SmoothedSpeed * 0.55 + inst * 0.45);
                                        tp.LastGrowthUtc = now;
                                    }
                                    else if ((now - tp.LastGrowthUtc).TotalSeconds > 2.2)
                                    {
                                        tp.SmoothedSpeed *= 0.5;
                                    }
                                    tp.LastBytes = fi.Length;
                                    tp.LastSampleUtc = now;
                                }
                                tp.CleanName = CleanPartName(fp);
                            }

                            if ((now - tp.LastGrowthUtc).TotalSeconds <= 12.0)
                            {
                                foundActive = true;
                                activeCandidate = new DownloadSnapshot
                                {
                                    IsActive = true,
                                    IsCompleted = false,
                                    IsStoreApp = false,
                                    HasExactPercent = false,
                                    Progress01 = 0.0f,
                                    FileName = tp.CleanName,
                                    DownloadedBytes = tp.LastBytes,
                                    TotalBytes = 0,
                                    SpeedBytesPerSec = tp.SmoothedSpeed,
                                    TargetPath = dir,
                                    SessionStartedUtc = tp.FirstSeenUtc
                                };
                            }
                        }
                    }

                    // Clean up vanished partial entries
                    if (_parts.Count > 0)
                    {
                        List<string> staleKeys = new List<string>();
                        foreach (KeyValuePair<string, TrackedPart> kv in _parts)
                        {
                            if (!File.Exists(kv.Key) || (now - kv.Value.LastGrowthUtc).TotalSeconds > 20.0)
                                staleKeys.Add(kv.Key);
                        }
                        for (int k = 0; k < staleKeys.Count; k++) _parts.Remove(staleKeys[k]);
                    }

                    // 2. Poll Microsoft Store / Windows Delivery Optimization every ~1.0s
                    if ((now - _lastDoPollUtc).TotalSeconds >= 1.0)
                    {
                        _lastDoPollUtc = now;
                        DownloadSnapshot storeSnap;
                        if (PollDeliveryOptimization(now, out storeSnap))
                        {
                            foundActive = true;
                            activeCandidate = storeSnap;
                        }
                    }

                    lock (_lock)
                    {
                        if (foundActive)
                        {
                            _snap = activeCandidate;
                        }
                        else if (!_snap.IsCompleted)
                        {
                            _snap = new DownloadSnapshot();
                        }
                        else if (now >= _completedUntilUtc)
                        {
                            _snap = new DownloadSnapshot();
                        }
                    }
                }
                catch { }
            }
        }

        private static string FriendlyStoreTitle(string fileId, string caller)
        {
            if (!string.IsNullOrEmpty(fileId))
            {
                // Extract clean app name if FileId contains readable publisher.app tokens (e.g. SpotifyAB.SpotifyMusic_...)
                Match m = Regex.Match(fileId, @"([A-Za-z][A-Za-z0-9]{2,})\.([A-Za-z][A-Za-z0-9]{2,})");
                if (m.Success)
                {
                    return m.Groups[2].Value;
                }
            }
            if (!string.IsNullOrEmpty(caller) && caller.IndexOf("Store", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Microsoft Store";
            return "Microsoft Store App";
        }

        private bool PollDeliveryOptimization(DateTime now, out DownloadSnapshot storeSnap)
        {
            storeSnap = new DownloadSnapshot();
            try
            {
                using (ManagementObjectSearcher mos = new ManagementObjectSearcher(
                    @"root\Microsoft\Windows\DeliveryOptimization",
                    "SELECT FileId, Status, FileSize, TotalBytesDownloaded, PredefinedCallerApplication FROM MSFT_DeliveryOptimizationFile"))
                {
                    HashSet<string> seenNow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (ManagementObject mo in mos.Get())
                    {
                        using (mo)
                        {
                            string fid = Convert.ToString(mo["FileId"], CultureInfo.InvariantCulture) ?? "";
                            if (string.IsNullOrEmpty(fid)) continue;
                            seenNow.Add(fid);

                            int status = 0;
                            try { if (mo["Status"] != null) status = Convert.ToInt32(mo["Status"], CultureInfo.InvariantCulture); } catch { }
                            long fSize = 0;
                            try { if (mo["FileSize"] != null) fSize = Convert.ToInt64(mo["FileSize"], CultureInfo.InvariantCulture); } catch { }
                            long dlBytes = 0;
                            try { if (mo["TotalBytesDownloaded"] != null) dlBytes = Convert.ToInt64(mo["TotalBytesDownloaded"], CultureInfo.InvariantCulture); } catch { }
                            string caller = Convert.ToString(mo["PredefinedCallerApplication"], CultureInfo.InvariantCulture) ?? "";

                            DoEntryState st;
                            if (!_doStates.TryGetValue(fid, out st))
                            {
                                bool activeOnFirstSight = _doBaselineReady && status == 1 && (fSize <= 0 || dlBytes < fSize);
                                st = new DoEntryState
                                {
                                    FileId = fid,
                                    Title = FriendlyStoreTitle(fid, caller),
                                    LastBytes = dlBytes,
                                    FileSize = fSize,
                                    SmoothedSpeed = 0.0,
                                    FirstActiveUtc = now,
                                    LastSampleUtc = now,
                                    LastGrowthUtc = activeOnFirstSight ? now : DateTime.MinValue,
                                    WasActive = activeOnFirstSight
                                };
                                _doStates[fid] = st;
                            }
                            else
                            {
                                double dt = Math.Max(0.2, (now - st.LastSampleUtc).TotalSeconds);
                                long delta = dlBytes - st.LastBytes;
                                if (delta > 0)
                                {
                                    double inst = delta / dt;
                                    st.SmoothedSpeed = (st.SmoothedSpeed <= 1.0) ? inst : (st.SmoothedSpeed * 0.55 + inst * 0.45);
                                    st.LastGrowthUtc = now;
                                    if (!st.WasActive) { st.WasActive = true; st.FirstActiveUtc = now; }
                                }
                                st.LastBytes = dlBytes;
                                if (fSize > 0) st.FileSize = fSize;
                                st.LastSampleUtc = now;

                                if (st.WasActive && st.FileSize > 0 && dlBytes >= st.FileSize)
                                {
                                    st.WasActive = false;
                                    MarkCompleted(st.Title, st.FileSize, true, "");
                                }
                            }

                            bool isDownloadingNow = st.WasActive &&
                                                    (st.FileSize <= 0 || dlBytes < st.FileSize) &&
                                                    (status == 1 || (now - st.LastGrowthUtc).TotalSeconds <= 6.0);
                            if (isDownloadingNow)
                            {
                                bool hasPct = (st.FileSize > 0);
                                float pct = hasPct ? (float)Math.Max(0.0, Math.Min(1.0, (double)dlBytes / st.FileSize)) : 0f;
                                storeSnap = new DownloadSnapshot
                                {
                                    IsActive = true,
                                    IsCompleted = false,
                                    IsStoreApp = true,
                                    HasExactPercent = hasPct,
                                    Progress01 = pct,
                                    FileName = st.Title,
                                    DownloadedBytes = dlBytes,
                                    TotalBytes = st.FileSize,
                                    SpeedBytesPerSec = st.SmoothedSpeed,
                                    TargetPath = "",
                                    SessionStartedUtc = st.FirstActiveUtc
                                };
                                _doBaselineReady = true;
                                return true;
                            }
                        }
                    }
                    _doBaselineReady = true;
                }
            }
            catch { }
            return false;
        }

        public void Dispose()
        {
            _running = false;
            try { _wake.Set(); } catch { }
            for (int i = 0; i < _watchers.Count; i++)
            {
                try { _watchers[i].EnableRaisingEvents = false; _watchers[i].Dispose(); } catch { }
            }
            _watchers.Clear();
        }
    }
}