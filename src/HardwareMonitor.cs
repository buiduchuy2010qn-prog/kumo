using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Threading;
using Microsoft.Win32;

namespace Kumo
{
    public class HardwareMonitor : IDisposable
    {
        public string CpuName { get; private set; }
        public float CpuBaseGhz { get; private set; }
        public float CpuUsagePercent { get; private set; }
        public float CpuSpeedGhz { get; private set; }

        public float RamUsedGb { get; private set; }
        public float RamTotalGb { get; private set; }
        public float RamUsagePercent { get; private set; }

        public string DiskTitle { get; private set; }
        public float DiskActivePercent { get; private set; }
        public double DiskReadBytesPerSec { get; private set; }
        public double DiskWriteBytesPerSec { get; private set; }

        public string NetAdapterLabel { get; private set; }
        public string NetAdapterName { get; private set; }
        public double NetSendKbps { get; private set; }
        public double NetRecvKbps { get; private set; }
        public double NetSendMbps { get; private set; }
        public double NetRecvMbps { get; private set; }

        public string GpuName { get; private set; }
        public string GpuEngineLabel { get; private set; }
        public float GpuUsagePercent { get; private set; }

        public bool HasBattery { get; private set; }
        public int BatteryPercent { get; private set; }
        public bool IsCharging { get; private set; }
        public int BatteryStateKind { get; private set; }

        public volatile bool IsNotchExpanded = false;

        private volatile bool _running;
        private Thread _worker;
        private PerformanceCounter _pcCpuUtil;
        private PerformanceCounter _pcCpuPerf;
        private PerformanceCounter _pcCpuNomFreq;
        private PerformanceCounter _pcDiskIdle;
        private PerformanceCounter _pcDiskRead;
        private PerformanceCounter _pcDiskWrite;
        private PerformanceCounterCategory _gpuCat;
        private readonly Dictionary<string, CounterSample> _prevGpuSamples = new Dictionary<string, CounterSample>(StringComparer.OrdinalIgnoreCase);

        private string _activeNicId;
        private long _lastBytesSent = -1;
        private long _lastBytesRecv = -1;
        private readonly Stopwatch _netClock = Stopwatch.StartNew();
        private double _lastNetSec = 0.0;
        private bool _cpuPrimed = false;

        public HardwareMonitor()
        {
            CpuName = "Processor"; CpuBaseGhz = 2.40f; CpuSpeedGhz = 2.40f;
            DiskTitle = "SSD"; NetAdapterLabel = "Wi-Fi"; NetAdapterName = "Wi-Fi";
            GpuName = "GPU 0"; GpuEngineLabel = "3D";
            HasBattery = true; BatteryPercent = 100; IsCharging = false; BatteryStateKind = 0;

            UpdateMemoryAndBattery();
            _running = true;
            _worker = new Thread(WorkerLoop);
            _worker.IsBackground = true;
            _worker.Priority = ThreadPriority.BelowNormal;
            _worker.Name = "KumoTelemetry";
            _worker.Start();
        }

        private void InitCounters()
        {
            try
            {
                using (RegistryKey rk = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                {
                    if (rk != null)
                    {
                        object nameObj = rk.GetValue("ProcessorNameString");
                        if (nameObj != null) CpuName = nameObj.ToString().Trim();
                        object mhzObj = rk.GetValue("~MHz");
                        if (mhzObj != null) CpuBaseGhz = Math.Max(0.8f, Convert.ToSingle(mhzObj) / 1000f);
                    }
                }
            }
            catch { }

            try
            {
                using (RegistryKey gk = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000"))
                {
                    if (gk != null)
                    {
                        object desc = gk.GetValue("DriverDesc");
                        if (desc != null && !string.IsNullOrEmpty(desc.ToString()))
                            GpuName = desc.ToString().Trim();
                    }
                }
            }
            catch { }

            try
            {
                _pcCpuUtil = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total", true);
                _pcCpuUtil.NextValue();
            }
            catch
            {
                try { _pcCpuUtil = new PerformanceCounter("Processor", "% Processor Time", "_Total", true); _pcCpuUtil.NextValue(); } catch { }
            }

            try { _pcCpuPerf = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total", true); _pcCpuPerf.NextValue(); } catch { }

            try
            {
                _pcCpuNomFreq = new PerformanceCounter("Processor Information", "Processor Frequency", "_Total", true);
                float nomMhz = _pcCpuNomFreq.NextValue();
                if (nomMhz > 200f) CpuBaseGhz = nomMhz / 1000f;
            }
            catch { }

            try
            {
                _pcDiskIdle = new PerformanceCounter("PhysicalDisk", "% Idle Time", "_Total", true);
                _pcDiskRead = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total", true);
                _pcDiskWrite = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total", true);
                _pcDiskIdle.NextValue(); _pcDiskRead.NextValue(); _pcDiskWrite.NextValue();
            }
            catch { }

            try
            {
                if (PerformanceCounterCategory.Exists("GPU Engine"))
                {
                    _gpuCat = new PerformanceCounterCategory("GPU Engine");
                    UpdateGpuUsage();
                }
            }
            catch { _gpuCat = null; }
        }

        private void UpdateMemoryAndBattery()
        {
            try
            {
                NativeMethods.MEMORYSTATUSEX mem = new NativeMethods.MEMORYSTATUSEX();
                if (NativeMethods.GlobalMemoryStatusEx(mem))
                {
                    float total = (float)(mem.ullTotalPhys / (1024.0 * 1024.0 * 1024.0));
                    float avail = (float)(mem.ullAvailPhys / (1024.0 * 1024.0 * 1024.0));
                    RamTotalGb = total;
                    RamUsedGb = Math.Max(0f, total - avail);
                    RamUsagePercent = mem.dwMemoryLoad;
                }
            }
            catch { }

            try
            {
                NativeMethods.SYSTEM_POWER_STATUS sps;
                if (NativeMethods.GetSystemPowerStatus(out sps))
                {
                    bool hasPack = ((sps.BatteryFlag & 128) == 0);
                    HasBattery = hasPack;
                    BatteryPercent = (hasPack && sps.BatteryLifePercent <= 100) ? (int)sps.BatteryLifePercent : 100;
                    IsCharging = (sps.ACLineStatus == 1) || ((sps.BatteryFlag & 8) != 0);
                    BatteryStateKind = IsCharging ? 1 : (BatteryPercent <= 20 ? 2 : 0);
                }
            }
            catch { }
        }

        private static bool IsVirtualOrFilteredNic(NetworkInterface nic)
        {
            if (nic.OperationalStatus != OperationalStatus.Up) return true;
            NetworkInterfaceType t = nic.NetworkInterfaceType;
            if (t == NetworkInterfaceType.Loopback || t == NetworkInterfaceType.Tunnel) return true;
            string s = ((nic.Name ?? "") + " " + (nic.Description ?? "")).ToLowerInvariant();
            string[] skip = new string[] {
                "vethernet", "virtual", "vmware", "virtualbox", "vbox", "hyper-v",
                "wsl", "loopback", "teredo", "isatap", "npcap", "bluetooth",
                "tailscale", "zerotier", "radmin", "hamachi", "wireguard", "wan miniport"
            };
            for (int i = 0; i < skip.Length; i++)
                if (s.IndexOf(skip[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        private void UpdateNetwork()
        {
            try
            {
                NetworkInterface[] all = NetworkInterface.GetAllNetworkInterfaces();
                NetworkInterface chosen = null;
                bool chosenGw = false;
                for (int i = 0; i < all.Length; i++)
                {
                    NetworkInterface nic = all[i];
                    if (IsVirtualOrFilteredNic(nic)) continue;
                    bool hasGw = false;
                    try
                    {
                        IPInterfaceProperties ip = nic.GetIPProperties();
                        if (ip != null && ip.GatewayAddresses != null && ip.GatewayAddresses.Count > 0) hasGw = true;
                    }
                    catch { }
                    if (chosen == null || (hasGw && (!chosenGw || nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)))
                    {
                        chosen = nic; chosenGw = hasGw;
                    }
                }

                if (chosen == null)
                {
                    NetSendKbps = 0; NetRecvKbps = 0; NetSendMbps = 0; NetRecvMbps = 0;
                    _activeNicId = null; _lastBytesSent = -1; return;
                }

                NetAdapterName = chosen.Name;
                NetAdapterLabel = (chosen.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) ? "Wi-Fi" : "Ethernet";
                IPInterfaceStatistics stats = chosen.GetIPStatistics();
                double nowSec = _netClock.Elapsed.TotalSeconds;
                double dt = nowSec - _lastNetSec;

                if (_activeNicId == chosen.Id && _lastBytesSent >= 0 && dt >= 0.25)
                {
                    long dSent = Math.Max(0L, stats.BytesSent - _lastBytesSent);
                    long dRecv = Math.Max(0L, stats.BytesReceived - _lastBytesRecv);
                    double sentBits = (dSent * 8.0) / dt;
                    double recvBits = (dRecv * 8.0) / dt;
                    NetSendKbps = sentBits / 1000.0; NetRecvKbps = recvBits / 1000.0;
                    NetSendMbps = sentBits / 1000000.0; NetRecvMbps = recvBits / 1000000.0;
                }
                else if (_activeNicId != chosen.Id)
                {
                    NetSendKbps = 0; NetRecvKbps = 0; NetSendMbps = 0; NetRecvMbps = 0;
                }

                _activeNicId = chosen.Id;
                _lastBytesSent = stats.BytesSent;
                _lastBytesRecv = stats.BytesReceived;
                _lastNetSec = nowSec;
            }
            catch { }
        }

        private void UpdateGpuUsage()
        {
            if (_gpuCat == null) return;
            try
            {
                InstanceDataCollectionCollection data = _gpuCat.ReadCategory();
                if (!data.Contains("Utilization Percentage")) return;
                InstanceDataCollection col = data["Utilization Percentage"];
                Dictionary<string, float> engineSums = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, CounterSample> nextSamples = new Dictionary<string, CounterSample>(col.Count, StringComparer.OrdinalIgnoreCase);

                foreach (DictionaryEntry de in col)
                {
                    InstanceData id = (InstanceData)de.Value;
                    string instName = id.InstanceName;
                    CounterSample cur = id.Sample;
                    nextSamples[instName] = cur;
                    if (instName.IndexOf("_phys_0_", StringComparison.OrdinalIgnoreCase) < 0 &&
                        instName.IndexOf("_phys_", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;

                    CounterSample prev;
                    if (_prevGpuSamples.TryGetValue(instName, out prev))
                    {
                        float val = CounterSample.Calculate(prev, cur);
                        if (val > 0.01f && val <= 100.0f)
                        {
                            int luidIdx = instName.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
                            string engKey = (luidIdx >= 0) ? instName.Substring(luidIdx) : instName;
                            float curSum;
                            if (engineSums.TryGetValue(engKey, out curSum)) engineSums[engKey] = curSum + val;
                            else engineSums[engKey] = val;
                        }
                    }
                }

                _prevGpuSamples.Clear();
                foreach (KeyValuePair<string, CounterSample> kv in nextSamples) _prevGpuSamples[kv.Key] = kv.Value;

                float maxUtil = 0f; string bestEng = "3D";
                foreach (KeyValuePair<string, float> kv in engineSums)
                {
                    float v = Clamp(kv.Value, 0f, 100f);
                    if (v > maxUtil) { maxUtil = v; bestEng = MapEngineLabel(kv.Key); }
                }
                GpuUsagePercent = maxUtil;
                GpuEngineLabel = (maxUtil >= 0.5f) ? bestEng : "3D";
            }
            catch { }
        }

        private static string MapEngineLabel(string engKey)
        {
            if (string.IsNullOrEmpty(engKey)) return "3D";
            string s = engKey.ToLowerInvariant();
            if (s.IndexOf("videodecode", StringComparison.Ordinal) >= 0 || s.IndexOf("videoprocessing", StringComparison.Ordinal) >= 0 || s.IndexOf("videoencode", StringComparison.Ordinal) >= 0)
                return "Video";
            if (s.IndexOf("copy", StringComparison.Ordinal) >= 0) return "Copy";
            if (s.IndexOf("compute", StringComparison.Ordinal) >= 0 || s.IndexOf("cuda", StringComparison.Ordinal) >= 0) return "Comp";
            return "3D";
        }

        private void WorkerLoop()
        {
            InitCounters();
            Thread.Sleep(350);
            int tick = 0;
            while (_running)
            {
                tick++;
                try
                {
                    UpdateMemoryAndBattery();
                    UpdateNetwork();
                    if (_pcCpuNomFreq != null)
                    {
                        float nomMhz = _pcCpuNomFreq.NextValue();
                        if (nomMhz > 200f) CpuBaseGhz = nomMhz / 1000f;
                    }
                    if (_pcCpuUtil != null)
                    {
                        float rawCpu = Clamp(_pcCpuUtil.NextValue(), 0f, 100f);
                        CpuUsagePercent = _cpuPrimed ? (0.72f * rawCpu + 0.28f * CpuUsagePercent) : rawCpu;
                    }
                    if (_pcCpuPerf != null)
                    {
                        float perfPct = _pcCpuPerf.NextValue();
                        if (perfPct > 5f)
                        {
                            float ghz = Clamp(CpuBaseGhz * (perfPct / 100f), 0.40f, 6.20f);
                            CpuSpeedGhz = _cpuPrimed ? (0.70f * ghz + 0.30f * CpuSpeedGhz) : ghz;
                        }
                    }
                    _cpuPrimed = true;
                    if (_pcDiskIdle != null) DiskActivePercent = Clamp(100f - _pcDiskIdle.NextValue(), 0f, 100f);
                    if (_pcDiskRead != null) DiskReadBytesPerSec = Math.Max(0, _pcDiskRead.NextValue());
                    if (_pcDiskWrite != null) DiskWriteBytesPerSec = Math.Max(0, _pcDiskWrite.NextValue());
                    if (IsNotchExpanded || (tick % 2 == 0)) UpdateGpuUsage();
                }
                catch { }
                Thread.Sleep(1000);
            }
        }

        private static float Clamp(float v, float min, float max) { return v < min ? min : (v > max ? max : v); }

        public static string FormatRate(double bytesPerSec)
        {
            if (bytesPerSec >= 1024.0 * 1024.0)
                return string.Format(CultureInfo.InvariantCulture, "{0:0.0} MB/s", bytesPerSec / (1024.0 * 1024.0));
            return string.Format(CultureInfo.InvariantCulture, "{0:0} KB/s", bytesPerSec / 1024.0);
        }

        public void Dispose() { _running = false; }
    }
}