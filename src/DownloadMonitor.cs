using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
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
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32W
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessIoCounters(IntPtr hProcess, out IO_COUNTERS lpIoCounters);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetCompressedFileSizeW(string lpFileName, out uint lpFileSizeHigh);

        private static readonly string[] PartialSuffixes = new string[]
        {
            ".fdmdownload",
            ".crdownload",
            ".opdownload",
            ".download",
            ".bt.xltd",
            ".part",
            ".abdm",
            ".!qb",
            ".!ut",
            ".aria2",
            ".bc!",
            ".dlm"
        };

        private static readonly string[] DownloaderExes = new string[]
        {
            "fdm.exe",
            "idman.exe",
            "abdownloadmanager.exe",
            "aria2c.exe",
            "qbittorrent.exe",
            "utorrent.exe",
            "bittorrent.exe",
            "bitcomet.exe",
            "motrix.exe",
            "neatdm.exe",
            "eagleget.exe",
            "curl.exe",
            "wget.exe"
        };

        private readonly object _lock = new object();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();
        private readonly Dictionary<string, TrackedPart> _parts = new Dictionary<string, TrackedPart>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DoEntryState> _doStates = new Dictionary<string, DoEntryState>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<uint, ulong> _pidLastWrite = new Dictionary<uint, ulong>();
        private readonly List<CachedDmProc> _cachedDmProcs = new List<CachedDmProc>();

        private volatile bool _running;
        private Thread _worker;
        private bool _doBaselineReady = false;
        private DateTime _lastDoPollUtc = DateTime.MinValue;
        private DateTime _lastProcSnapUtc = DateTime.MinValue;
        private DateTime _lastDmSampleUtc = DateTime.MinValue;
        private DateTime _lastDmWriteUtc = DateTime.MinValue;
        private DateTime _dmSessionStartUtc = DateTime.MinValue;
        private double _dmSmoothedSpeed = 0.0;
        private long _dmSessionBytes = 0;
        private int _dmSustainedTicks = 0;
        private string _activeDmExe = "";

        private DownloadSnapshot _snap;
        private DateTime _completedUntilUtc = DateTime.MinValue;
        private string _lastCompletedPath = "";
        private DateTime _lastCompletedAtUtc = DateTime.MinValue;

        private List<string> _browserDirs = new List<string>();
        private string _desktopDir = "";
        private string _winGetDir = "";

        private struct CachedDmProc
        {
            public uint Pid;
            public string ExeName;
        }

        private sealed class TrackedPart
        {
            public string FullPath;
            public string CleanName;
            public bool StartedFromZero;
            public long LastLogicalBytes;
            public long LastPhysicalBytes;
            public long LogicalGrewBytes;
            public long AccumulatedWrites;
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
            for (int i = 0; i < PartialSuffixes.Length; i++)
            {
                if (path.EndsWith(PartialSuffixes[i], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static string CleanPartName(string fullPath)
        {
            string name = Path.GetFileName(fullPath) ?? "File";
            for (int i = 0; i < PartialSuffixes.Length; i++)
            {
                if (name.EndsWith(PartialSuffixes[i], StringComparison.OrdinalIgnoreCase))
                {
                    name = name.Substring(0, name.Length - PartialSuffixes[i].Length);
                    break;
                }
            }
            if (name.StartsWith("Unconfirmed ", StringComparison.OrdinalIgnoreCase))
            {
                return "T\u1EC7p tr\u00ECnh duy\u1EC7t";
            }
            return string.IsNullOrEmpty(name) ? "File" : name;
        }

        private static bool IsDownloaderExe(string exeName)
        {
            if (string.IsNullOrEmpty(exeName)) return false;
            for (int i = 0; i < DownloaderExes.Length; i++)
            {
                if (string.Equals(exeName, DownloaderExes[i], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static string FriendlyDmTitle(string exeName)
        {
            if (string.IsNullOrEmpty(exeName)) return "Download Manager";
            string low = exeName.ToLowerInvariant();
            if (low == "fdm.exe") return "Free Download Manager";
            if (low == "idman.exe") return "Internet Download Manager";
            if (low == "abdownloadmanager.exe") return "AB Download Manager";
            if (low == "qbittorrent.exe") return "qBittorrent";
            if (low == "utorrent.exe" || low == "bittorrent.exe") return "BitTorrent";
            if (low == "aria2c.exe" || low == "motrix.exe") return "Motrix / Aria2";
            if (low == "neatdm.exe") return "Neat Download Manager";
            if (low == "eagleget.exe") return "EagleGet";
            if (low == "bitcomet.exe") return "BitComet";
            return Path.GetFileNameWithoutExtension(exeName);
        }

        private List<string> DiscoverWatchFolders(out string winGetTemp, out string desktopDir)
        {
            List<string> dirs = new List<string>();
            winGetTemp = Path.Combine(Path.GetTempPath(), "WinGet");
            desktopDir = "";
            try
            {
                desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            }
            catch { }

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

            if (!string.IsNullOrEmpty(desktopDir) && Directory.Exists(desktopDir) && !ContainsDir(dirs, desktopDir))
            {
                dirs.Add(desktopDir);
            }

            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roamApp = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string[] browserPrefs = new string[]
            {
                Path.Combine(localApp, @"Google\Chrome\User Data\Default\Preferences"),
                Path.Combine(localApp, @"Microsoft\Edge\User Data\Default\Preferences"),
                Path.Combine(localApp, @"BraveSoftware\Brave-Browser\User Data\Default\Preferences"),
                Path.Combine(localApp, @"CocCoc\Browser\User Data\Default\Preferences"),
                Path.Combine(roamApp, @"Opera Software\Opera Stable\Preferences"),
                Path.Combine(roamApp, @"Opera Software\Opera GX Stable\Preferences")
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

            // Free Download Manager (FDM) custom download paths in settings.ini
            try
            {
                string fdmIni = Path.Combine(localApp, @"Softdeluxe\Free Download Manager\settings.ini");
                if (File.Exists(fdmIni))
                {
                    using (FileStream fs = new FileStream(fdmIni, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (StreamReader sr = new StreamReader(fs, Encoding.UTF8))
                    {
                        string line;
                        while ((line = sr.ReadLine()) != null)
                        {
                            int eq = line.IndexOf('=');
                            if (eq <= 0) continue;
                            string val = line.Substring(eq + 1).Trim().Trim('"');
                            if (val.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
                            {
                                val = Uri.UnescapeDataString(val.Substring(8));
                            }
                            if (val.Length >= 3 && val[1] == ':' && (val[2] == '/' || val[2] == '\\'))
                            {
                                string norm = val.Replace('/', '\\');
                                if (Directory.Exists(norm) && !ContainsDir(dirs, norm)) dirs.Add(norm);
                            }
                        }
                    }
                }
            }
            catch { }

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

        private void SetupWatchers()
        {
            _browserDirs = DiscoverWatchFolders(out _winGetDir, out _desktopDir);
            for (int i = 0; i < _browserDirs.Count; i++)
            {
                bool isDesktop = !string.IsNullOrEmpty(_desktopDir) &&
                                 string.Equals(_browserDirs[i].TrimEnd('\\', '/'), _desktopDir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
                AttachWatcher(_browserDirs[i], !isDesktop);
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

        private long SampleDownloaderWriteDelta(DateTime now)
        {
            if ((now - _lastProcSnapUtc).TotalSeconds >= 2.8)
            {
                _lastProcSnapUtc = now;
                _cachedDmProcs.Clear();
                HashSet<uint> alive = new HashSet<uint>();
                IntPtr snap = CreateToolhelp32Snapshot(2u, 0u);
                if (snap != IntPtr.Zero && snap != new IntPtr(-1))
                {
                    try
                    {
                        PROCESSENTRY32W pe = new PROCESSENTRY32W();
                        pe.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32W));
                        if (Process32FirstW(snap, ref pe))
                        {
                            do
                            {
                                if (IsDownloaderExe(pe.szExeFile))
                                {
                                    _cachedDmProcs.Add(new CachedDmProc { Pid = pe.th32ProcessID, ExeName = pe.szExeFile });
                                    alive.Add(pe.th32ProcessID);
                                }
                            }
                            while (Process32NextW(snap, ref pe));
                        }
                    }
                    finally
                    {
                        CloseHandle(snap);
                    }
                }

                if (_pidLastWrite.Count > 0)
                {
                    List<uint> dead = new List<uint>();
                    foreach (KeyValuePair<uint, ulong> kv in _pidLastWrite)
                    {
                        if (!alive.Contains(kv.Key)) dead.Add(kv.Key);
                    }
                    for (int i = 0; i < dead.Count; i++) _pidLastWrite.Remove(dead[i]);
                }
            }

            double dt = (_lastDmSampleUtc == DateTime.MinValue) ? 0.45 : Math.Max(0.12, (now - _lastDmSampleUtc).TotalSeconds);
            _lastDmSampleUtc = now;

            ulong sumDelta = 0;
            ulong bestProcDelta = 0;
            string bestExe = "";

            for (int i = 0; i < _cachedDmProcs.Count; i++)
            {
                CachedDmProc cp = _cachedDmProcs[i];
                IntPtr hProc = OpenProcess(0x1000u, false, cp.Pid);
                if (hProc == IntPtr.Zero) continue;
                try
                {
                    IO_COUNTERS io;
                    if (GetProcessIoCounters(hProc, out io))
                    {
                        ulong prev;
                        if (_pidLastWrite.TryGetValue(cp.Pid, out prev))
                        {
                            if (io.WriteTransferCount > prev)
                            {
                                ulong d = io.WriteTransferCount - prev;
                                sumDelta += d;
                                if (d > bestProcDelta)
                                {
                                    bestProcDelta = d;
                                    bestExe = cp.ExeName;
                                }
                            }
                        }
                        _pidLastWrite[cp.Pid] = io.WriteTransferCount;
                    }
                }
                finally
                {
                    CloseHandle(hProc);
                }
            }

            long deltaSigned = (sumDelta > (ulong)long.MaxValue) ? long.MaxValue : (long)sumDelta;
            double instSpeed = deltaSigned / dt;

            if (instSpeed > 2048.0)
            {
                if (_dmSustainedTicks == 0)
                {
                    _dmSessionStartUtc = now;
                    _dmSessionBytes = 0;
                }
                _dmSustainedTicks = Math.Min(30, _dmSustainedTicks + 1);
                _dmSessionBytes += deltaSigned;
                _dmSmoothedSpeed = (_dmSmoothedSpeed <= 1.0) ? instSpeed : (_dmSmoothedSpeed * 0.55 + instSpeed * 0.45);
                _lastDmWriteUtc = now;
                if (!string.IsNullOrEmpty(bestExe)) _activeDmExe = bestExe;
            }
            else if ((now - _lastDmWriteUtc).TotalSeconds > 2.5)
            {
                _dmSmoothedSpeed *= 0.45;
                if ((now - _lastDmWriteUtc).TotalSeconds > 6.0)
                {
                    _dmSustainedTicks = 0;
                    _dmSessionBytes = 0;
                    _dmSmoothedSpeed = 0.0;
                }
            }

            return deltaSigned;
        }

        private static void GetPayloadMetrics(string partPath, FileInfo partFi, out long logicalBytes, out long physicalBytes)
        {
            string dataPath = partPath;
            long len = partFi.Length;
            if (partPath.EndsWith(".aria2", StringComparison.OrdinalIgnoreCase) && partPath.Length > 6)
            {
                string companion = partPath.Substring(0, partPath.Length - 6);
                try
                {
                    FileInfo cfi = new FileInfo(companion);
                    if (cfi.Exists)
                    {
                        dataPath = companion;
                        len = cfi.Length;
                    }
                }
                catch { }
            }

            logicalBytes = len;
            physicalBytes = len;
            try
            {
                uint hi;
                uint lo = GetCompressedFileSizeW(dataPath, out hi);
                if (!(lo == 0xFFFFFFFFu && Marshal.GetLastWin32Error() != 0))
                {
                    long comp = ((long)hi << 32) | (long)lo;
                    if (comp >= 0) physicalBytes = comp;
                }
            }
            catch { }
        }

        private static List<string> EnumerateCandidateFiles(string rootDir, bool includeChildFolders)
        {
            List<string> result = new List<string>();
            try
            {
                string[] top = Directory.GetFiles(rootDir);
                for (int i = 0; i < top.Length; i++)
                {
                    if (IsPartialDownloadExt(top[i])) result.Add(top[i]);
                }
            }
            catch { }

            if (includeChildFolders)
            {
                try
                {
                    string[] subDirs = Directory.GetDirectories(rootDir);
                    int cap = Math.Min(24, subDirs.Length);
                    for (int s = 0; s < cap; s++)
                    {
                        try
                        {
                            string[] subFiles = Directory.GetFiles(subDirs[s]);
                            for (int f = 0; f < subFiles.Length; f++)
                            {
                                if (IsPartialDownloadExt(subFiles[f])) result.Add(subFiles[f]);
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }
            return result;
        }

        private bool TryPollIdmActiveChunk(DateTime now, out DownloadSnapshot idmSnap)
        {
            idmSnap = new DownloadSnapshot();
            try
            {
                string dwnlRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"IDM\DwnlData");
                if (!Directory.Exists(dwnlRoot)) return false;

                string[] userDirs = Directory.GetDirectories(dwnlRoot);
                string bestFolder = null;
                DateTime bestTime = DateTime.MinValue;
                long bestSum = 0;

                for (int u = 0; u < userDirs.Length; u++)
                {
                    string[] jobDirs = Directory.GetDirectories(userDirs[u]);
                    for (int j = 0; j < jobDirs.Length; j++)
                    {
                        DirectoryInfo di = new DirectoryInfo(jobDirs[j]);
                        FileInfo[] chunks = di.GetFiles();
                        if (chunks.Length == 0) continue;

                        DateTime newest = di.LastWriteTimeUtc;
                        long sum = 0;
                        for (int c = 0; c < chunks.Length; c++)
                        {
                            sum += chunks[c].Length;
                            if (chunks[c].LastWriteTimeUtc > newest) newest = chunks[c].LastWriteTimeUtc;
                        }
                        if (sum > 0 && newest > bestTime)
                        {
                            bestTime = newest;
                            bestFolder = jobDirs[j];
                            bestSum = sum;
                        }
                    }
                }

                if (bestFolder != null && ((now - bestTime).TotalSeconds <= 15.0 || string.Equals(_activeDmExe, "idman.exe", StringComparison.OrdinalIgnoreCase)))
                {
                    string folderName = Path.GetFileName(bestFolder) ?? "IDM Download";
                    int lastUnd = folderName.LastIndexOf('_');
                    if (lastUnd > 1) folderName = folderName.Substring(0, lastUnd);

                    idmSnap = new DownloadSnapshot
                    {
                        IsActive = true,
                        IsCompleted = false,
                        IsStoreApp = false,
                        HasExactPercent = false,
                        Progress01 = 0f,
                        FileName = folderName,
                        DownloadedBytes = bestSum,
                        TotalBytes = 0,
                        SpeedBytesPerSec = _dmSmoothedSpeed,
                        TargetPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                        SessionStartedUtc = (_dmSessionStartUtc == DateTime.MinValue) ? now : _dmSessionStartUtc
                    };
                    return true;
                }
            }
            catch { }
            return false;
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
                    long dmDelta = SampleDownloaderWriteDelta(now);
                    bool dmWritingNow = (dmDelta > 2048L) || ((now - _lastDmWriteUtc).TotalSeconds <= 4.5 && _dmSmoothedSpeed > 2048.0);

                    DownloadSnapshot activeCandidate = new DownloadSnapshot();
                    bool foundActive = false;

                    // 1. Scan Browser & Download Manager partial files (.fdmdownload, .crdownload, .part, .!qb, .abdm, .aria2, etc.)
                    for (int d = 0; d < _browserDirs.Count; d++)
                    {
                        string dir = _browserDirs[d];
                        if (!Directory.Exists(dir)) continue;

                        bool isDesktop = !string.IsNullOrEmpty(_desktopDir) &&
                                         string.Equals(dir.TrimEnd('\\', '/'), _desktopDir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
                        List<string> files = EnumerateCandidateFiles(dir, !isDesktop);

                        for (int i = 0; i < files.Count; i++)
                        {
                            string fp = files[i];
                            FileInfo fi;
                            try { fi = new FileInfo(fp); if (!fi.Exists) continue; } catch { continue; }

                            bool recentTouch = ((now - fi.LastWriteTimeUtc).TotalSeconds <= 22.0) ||
                                               ((now - fi.CreationTimeUtc).TotalSeconds <= 22.0);
                            if (!_parts.ContainsKey(fp) && !recentTouch && !dmWritingNow)
                            {
                                continue;
                            }

                            long logBytes, physBytes;
                            GetPayloadMetrics(fp, fi, out logBytes, out physBytes);

                            TrackedPart tp;
                            if (!_parts.TryGetValue(fp, out tp))
                            {
                                bool freshCreate = ((now - fi.CreationTimeUtc).TotalSeconds <= 8.0);
                                tp = new TrackedPart
                                {
                                    FullPath = fp,
                                    CleanName = CleanPartName(fp),
                                    StartedFromZero = freshCreate,
                                    LastLogicalBytes = logBytes,
                                    LastPhysicalBytes = physBytes,
                                    LogicalGrewBytes = 0,
                                    AccumulatedWrites = freshCreate ? Math.Max(0L, dmDelta) : 0L,
                                    SmoothedSpeed = dmWritingNow ? _dmSmoothedSpeed : 0.0,
                                    FirstSeenUtc = now,
                                    LastSampleUtc = now,
                                    LastGrowthUtc = (recentTouch || dmWritingNow) ? now : DateTime.MinValue
                                };
                                _parts[fp] = tp;
                            }
                            else
                            {
                                double dt = (now - tp.LastSampleUtc).TotalSeconds;
                                if (dt >= 0.15)
                                {
                                    long logDelta = Math.Max(0L, logBytes - tp.LastLogicalBytes);
                                    long physDelta = Math.Max(0L, physBytes - tp.LastPhysicalBytes);
                                    long fileDelta = Math.Max(logDelta, physDelta);
                                    if (logDelta > 0) tp.LogicalGrewBytes += logDelta;

                                    if (fileDelta > 0)
                                    {
                                        double inst = fileDelta / dt;
                                        tp.SmoothedSpeed = (tp.SmoothedSpeed <= 1.0) ? inst : (tp.SmoothedSpeed * 0.55 + inst * 0.45);
                                        tp.AccumulatedWrites += fileDelta;
                                        tp.LastGrowthUtc = now;
                                    }
                                    else if (dmDelta > 0)
                                    {
                                        tp.SmoothedSpeed = _dmSmoothedSpeed;
                                        tp.AccumulatedWrites += dmDelta;
                                        tp.LastGrowthUtc = now;
                                    }
                                    else if ((now - tp.LastGrowthUtc).TotalSeconds > 2.2)
                                    {
                                        tp.SmoothedSpeed *= 0.5;
                                    }

                                    tp.LastLogicalBytes = logBytes;
                                    tp.LastPhysicalBytes = physBytes;
                                    tp.LastSampleUtc = now;
                                }
                                tp.CleanName = CleanPartName(fp);
                            }

                            if ((now - tp.LastGrowthUtc).TotalSeconds <= 9.5)
                            {
                                long dlBytes = logBytes;
                                long totBytes = 0;
                                bool hasPct = false;
                                float pct = 0f;

                                if (logBytes > 65536L && physBytes > 0L && physBytes + 65536L < logBytes)
                                {
                                    // NTFS sparse pre-allocated file (exact physical disk blocks vs total length!)
                                    dlBytes = physBytes;
                                    totBytes = logBytes;
                                    hasPct = true;
                                    pct = (float)Math.Max(0.01, Math.Min(0.99, (double)physBytes / (double)logBytes));
                                }
                                else if (logBytes > 512L * 1024L && tp.LogicalGrewBytes == 0L)
                                {
                                    // Non-sparse pre-allocated file (e.g. Free Download Manager .fdmdownload)
                                    totBytes = logBytes;
                                    if (tp.StartedFromZero && tp.AccumulatedWrites > 0L && tp.AccumulatedWrites < logBytes)
                                    {
                                        dlBytes = tp.AccumulatedWrites;
                                        hasPct = true;
                                        pct = (float)Math.Max(0.01, Math.Min(0.99, (double)tp.AccumulatedWrites / (double)logBytes));
                                    }
                                    else
                                    {
                                        dlBytes = 0L;
                                        hasPct = false;
                                    }
                                }

                                string folderPath = Path.GetDirectoryName(fp) ?? dir;
                                foundActive = true;
                                activeCandidate = new DownloadSnapshot
                                {
                                    IsActive = true,
                                    IsCompleted = false,
                                    IsStoreApp = false,
                                    HasExactPercent = hasPct,
                                    Progress01 = pct,
                                    FileName = tp.CleanName,
                                    DownloadedBytes = dlBytes,
                                    TotalBytes = totBytes,
                                    SpeedBytesPerSec = tp.SmoothedSpeed,
                                    TargetPath = folderPath,
                                    SessionStartedUtc = tp.FirstSeenUtc
                                };
                            }
                        }
                    }

                    // Clean up vanished or idle partial entries
                    if (_parts.Count > 0)
                    {
                        List<string> staleKeys = new List<string>();
                        foreach (KeyValuePair<string, TrackedPart> kv in _parts)
                        {
                            bool exists = File.Exists(kv.Key);
                            if (!exists)
                            {
                                // Check if completed file (without partial suffix) now exists!
                                string cleanDest = Path.Combine(Path.GetDirectoryName(kv.Key) ?? "", kv.Value.CleanName);
                                if (!string.IsNullOrEmpty(kv.Value.CleanName) && File.Exists(cleanDest))
                                {
                                    long finalLen = 0;
                                    try { finalLen = new FileInfo(cleanDest).Length; } catch { }
                                    MarkCompleted(kv.Value.CleanName, finalLen, false, cleanDest);
                                }
                                staleKeys.Add(kv.Key);
                            }
                            else if ((now - kv.Value.LastGrowthUtc).TotalSeconds > 16.0)
                            {
                                staleKeys.Add(kv.Key);
                            }
                        }
                        for (int k = 0; k < staleKeys.Count; k++) _parts.Remove(staleKeys[k]);
                    }

                    // 2. Fallback for IDM chunk directory or Download Managers writing to custom drives
                    if (!foundActive && dmWritingNow)
                    {
                        DownloadSnapshot idmSnap;
                        if (string.Equals(_activeDmExe, "idman.exe", StringComparison.OrdinalIgnoreCase) &&
                            TryPollIdmActiveChunk(now, out idmSnap))
                        {
                            foundActive = true;
                            activeCandidate = idmSnap;
                        }
                        else if (_dmSustainedTicks >= 2 && _dmSmoothedSpeed > 24576.0)
                        {
                            string defDl = "";
                            try { defDl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"); } catch { }
                            foundActive = true;
                            activeCandidate = new DownloadSnapshot
                            {
                                IsActive = true,
                                IsCompleted = false,
                                IsStoreApp = false,
                                HasExactPercent = false,
                                Progress01 = 0f,
                                FileName = FriendlyDmTitle(_activeDmExe),
                                DownloadedBytes = _dmSessionBytes,
                                TotalBytes = 0,
                                SpeedBytesPerSec = _dmSmoothedSpeed,
                                TargetPath = defDl,
                                SessionStartedUtc = (_dmSessionStartUtc == DateTime.MinValue) ? now : _dmSessionStartUtc
                            };
                        }
                    }

                    // 3. Poll Microsoft Store / Windows Delivery Optimization every ~1.0s
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
                    foreach (ManagementObject mo in mos.Get())
                    {
                        using (mo)
                        {
                            string fid = Convert.ToString(mo["FileId"], CultureInfo.InvariantCulture) ?? "";
                            if (string.IsNullOrEmpty(fid)) continue;

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
