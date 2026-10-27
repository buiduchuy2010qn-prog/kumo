using System;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Threading;
using Microsoft.Win32;

namespace Kumo
{
    public class HardwareMonitor : IDisposable
    {
        // Fast, zero-lag telemetry values
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

        public string NetAdapterName { get; private set; }
        public double NetSendMbps { get; private set; }
        public double NetRecvMbps { get; private set; }

        public float GpuUsagePercent { get; private set; }

        public int BatteryPercent { get; private set; }
        public bool IsCharging { get; private set; }

        // Only poll frequently when the user actually has the Notch expanded!
        public volatile bool IsNotchExpanded = false;

        private volatile bool _running;
        private Thread _worker;

        private PerformanceCounter _pcCpuUtil;
        private PerformanceCounter _pcCpuFreq;
        private PerformanceCounter _pcDiskIdle;
        private PerformanceCounter _pcDiskRead;
        private PerformanceCounter _pcDiskWrite;
        private PerformanceCounter _pcGpu;

        private NetworkInterface _activeNic;
        private long _lastBytesSent = -1;
        private long _lastBytesRecv = -1;
        private DateTime _lastNetTime = DateTime.UtcNow;

        public HardwareMonitor()
        {
            CpuName = "Intel Core Processor";
            CpuBaseGhz = 2.20f;
            CpuSpeedGhz = 2.20f;
            DiskTitle = "SSD (C: D:)";
            NetAdapterName = "Wi-Fi";
            BatteryPercent = 100;

            UpdateMemoryAndBattery();
            _running = true;
            _worker = new Thread(WorkerLoop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "KumoTelemetry" };
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
                        object mhzObj = rk.GetValue("~MHz");
                        if (mhzObj != null) CpuBaseGhz = Convert.ToSingle(mhzObj) / 1000f;
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
                try
                {
                    _pcCpuUtil = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
                    _pcCpuUtil.NextValue();
                }
                catch { }
            }

            try
            {
                _pcCpuFreq = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total", true);
                _pcCpuFreq.NextValue();
            }
            catch { }

            try
            {
                _pcDiskIdle = new PerformanceCounter("PhysicalDisk", "% Idle Time", "_Total", true);
                _pcDiskRead = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total", true);
                _pcDiskWrite = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total", true);
                _pcDiskIdle.NextValue();
                _pcDiskRead.NextValue();
                _pcDiskWrite.NextValue();
            }
            catch { }
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
                    BatteryPercent = sps.BatteryLifePercent <= 100 ? sps.BatteryLifePercent : 100;
                    IsCharging = (sps.ACLineStatus == 1);
                }
            }
            catch { }
        }

        private void UpdateNetwork()
        {
            try
            {
                if (_activeNic == null || _activeNic.OperationalStatus != OperationalStatus.Up)
                {
                    foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (nic.OperationalStatus == OperationalStatus.Up &&
                            nic.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                            nic.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                        {
                            _activeNic = nic;
                            if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) break;
                        }
                    }
                    _lastBytesSent = -1;
                }

                if (_activeNic != null)
                {
                    IPv4InterfaceStatistics stats = _activeNic.GetIPv4Statistics();
                    DateTime now = DateTime.UtcNow;
                    double dt = (now - _lastNetTime).TotalSeconds;
                    if (_lastBytesSent >= 0 && dt > 0.2)
                    {
                        double sentBps = Math.Max(0, (stats.BytesSent - _lastBytesSent) / dt);
                        double recvBps = Math.Max(0, (stats.BytesReceived - _lastBytesRecv) / dt);
                        NetAdapterName = _activeNic.Name;
                        NetSendMbps = (sentBps * 8.0) / 1000000.0;
                        NetRecvMbps = (recvBps * 8.0) / 1000000.0;
                    }
                    _lastBytesSent = stats.BytesSent;
                    _lastBytesRecv = stats.BytesReceived;
                    _lastNetTime = now;
                }
            }
            catch { }
        }

        private void WorkerLoop()
        {
            InitCounters();

            while (_running)
            {
                try
                {
                    UpdateMemoryAndBattery();

                    // Only poll detailed counters when expanded (saves 100% CPU when collapsed!)
                    if (IsNotchExpanded)
                    {
                        UpdateNetwork();

                        if (_pcCpuUtil != null)
                            CpuUsagePercent = Clamp(_pcCpuUtil.NextValue(), 0f, 100f);
                        if (_pcCpuFreq != null)
                        {
                            float freqPct = Clamp(_pcCpuFreq.NextValue(), 30f, 180f);
                            CpuSpeedGhz = Clamp(CpuBaseGhz * (freqPct / 100f), 0.8f, 5.5f);
                        }
                        if (_pcDiskIdle != null)
                            DiskActivePercent = Clamp(100f - _pcDiskIdle.NextValue(), 0f, 100f);
                        if (_pcDiskRead != null)
                            DiskReadBytesPerSec = Math.Max(0, _pcDiskRead.NextValue());
                        if (_pcDiskWrite != null)
                            DiskWriteBytesPerSec = Math.Max(0, _pcDiskWrite.NextValue());
                    }
                }
                catch { }

                // Sleep 1200ms when expanded, 3000ms when collapsed -> 0.0% CPU usage!
                Thread.Sleep(IsNotchExpanded ? 1200 : 3000);
            }
        }

        private static float Clamp(float v, float min, float max)
        {
            return v < min ? min : (v > max ? max : v);
        }

        public static string FormatRate(double bytesPerSec)
        {
            if (bytesPerSec >= 1024.0 * 1024.0)
                return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.1} MB/s", bytesPerSec / (1024.0 * 1024.0));
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0} KB/s", bytesPerSec / 1024.0);
        }

        public void Dispose()
        {
            _running = false;
        }
    }
}
