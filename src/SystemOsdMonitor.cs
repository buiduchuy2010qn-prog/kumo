using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace Kumo
{
    public enum OsdKind
    {
        None = 0,
        Volume = 1,
        Brightness = 2,
        Battery = 3,
        AudioDevice = 4,
        MediaToggle = 5,
        Screenshot = 6,
        PrivacyAlert = 7
    }

    public delegate void OsdChangedHandler(OsdKind kind, int percent, bool isMuted, int delta);

    public sealed class SystemOsdMonitor : IDisposable
    {
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorComObject { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IntPtr ppDevices);
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
            int OpenPropertyStore(int stgmAccess, out IPropertyStore ppProperties);
            int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
            int GetState(out int pdwState);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROPERTYKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROPVARIANT
        {
            public ushort vt;
            public ushort wReserved1;
            public ushort wReserved2;
            public ushort wReserved3;
            public IntPtr pwszVal;
            public IntPtr pad;
        }

        [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            int GetCount(out uint cProps);
            int GetAt(uint iProp, out PROPERTYKEY pkey);
            int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        }

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PROPVARIANT pvar);

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolume
        {
            int RegisterControlChangeNotify(IntPtr pNotify);
            int UnregisterControlChangeNotify(IntPtr pNotify);
            int GetChannelCount(out uint pnChannelCount);
            int SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext);
            int SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext);
            int GetMasterVolumeLevel(out float pfLevelDB);
            int GetMasterVolumeLevelScalar(out float pfLevel);
            int SetChannelVolumeLevel(uint nChannel, float fLevelDB, ref Guid pguidEventContext);
            int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, ref Guid pguidEventContext);
            int GetChannelVolumeLevel(out float pfLevelDB);
            int GetChannelVolumeLevelScalar(out float pfLevel);
            int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid pguidEventContext);
            int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
        }

        private static Guid IID_IAudioEndpointVolume = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
        private static PROPERTYKEY PKEY_Device_FriendlyName = new PROPERTYKEY
        {
            fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
            pid = 14
        };

        public int VolumePercent { get; private set; }
        public bool IsMuted { get; private set; }
        public int BrightnessPercent { get; private set; }
        public bool SupportsBrightness { get; private set; }

        public string AudioDeviceName { get; private set; }
        public bool IsHeadphoneDevice { get; private set; }

        public volatile bool IsCameraActive;
        public volatile bool IsMicActive;
        public string CameraAppName { get; private set; }
        public string MicAppName { get; private set; }

        public event OsdChangedHandler OsdChanged;

        private volatile bool _running;
        private Thread _worker;
        private readonly object _audioLock = new object();
        private IAudioEndpointVolume _epVol;
        private bool _volReady;
        private int _lastVol;
        private bool _lastMute;
        private string _lastEndpointId = "";
        private bool _endpointBaselineReady = false;
        private bool _privacyBaselineReady = false;

        private bool _briReady;
        private int _lastBri;
        private ManagementEventWatcher _briWatcher;

        public SystemOsdMonitor()
        {
            VolumePercent = 50;
            IsMuted = false;
            BrightnessPercent = 80;
            SupportsBrightness = false;
            AudioDeviceName = "Speakers";
            IsHeadphoneDevice = false;
            CameraAppName = "";
            MicAppName = "";
            _lastVol = -1;
            _lastBri = -1;
            _running = true;

            _worker = new Thread(WorkerLoop);
            _worker.IsBackground = true;
            _worker.Priority = ThreadPriority.BelowNormal;
            _worker.Name = "KumoSystemOsd";
            _worker.SetApartmentState(ApartmentState.MTA);
            _worker.Start();
        }

        private static void ParseEndpointDisplayName(string rawName, out string cleanLabel, out bool isHeadphone)
        {
            string raw = (rawName ?? "").Trim();
            isHeadphone = Regex.IsMatch(raw, @"(headphone|headset|airpods|earbuds|earphone|buds|tai\s*nghe|bluetooth|wh-|wf-|qc\d|kraken|arctis|cloud\s*ii|g\s*pro|blackshark)", RegexOptions.IgnoreCase);

            string inner = raw;
            Match mParen = Regex.Match(raw, @"^([^(]+)\((.+)\)\s*$");
            if (mParen.Success)
            {
                string prefix = mParen.Groups[1].Value.Trim();
                string inside = mParen.Groups[2].Value.Trim();
                inside = Regex.Replace(inside, @"^\d+\s*-\s*", "").Trim();
                inside = Regex.Replace(inside, @"\s*\(R\)|\s*\(TM\)|\s*Hands-Free.*$|\s*Stereo$", "", RegexOptions.IgnoreCase).Trim();
                if (!string.IsNullOrEmpty(inside) &&
                    !Regex.IsMatch(inside, @"^(realtek|high\s*definition\s*audio|amd\s*high|nvidia\s*high|intel)", RegexOptions.IgnoreCase))
                {
                    inner = inside;
                }
                else if (!string.IsNullOrEmpty(inside))
                {
                    inner = prefix + " (" + inside.Replace("Audio", "").Trim() + ")";
                }
            }

            inner = Regex.Replace(inner, @"\s{2,}", " ").Trim();
            if (string.IsNullOrEmpty(inner))
            {
                inner = isHeadphone ? "Headphones" : "Speakers";
            }
            cleanLabel = inner;
        }

        private bool ProbeDefaultAudioEndpoint(out string endpointId, out string cleanName, out bool isHeadphone)
        {
            endpointId = "";
            cleanName = AudioDeviceName;
            isHeadphone = IsHeadphoneDevice;

            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            IPropertyStore store = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)(new MMDeviceEnumeratorComObject());
                int hr = enumerator.GetDefaultAudioEndpoint(0, 1, out device);
                if (hr != 0 || device == null) hr = enumerator.GetDefaultAudioEndpoint(0, 0, out device);
                if (hr != 0 || device == null) return false;

                string id;
                if (device.GetId(out id) == 0 && !string.IsNullOrEmpty(id))
                {
                    endpointId = id;
                }

                if (device.OpenPropertyStore(0, out store) == 0 && store != null)
                {
                    PROPVARIANT pv = new PROPVARIANT();
                    PROPERTYKEY key = PKEY_Device_FriendlyName;
                    if (store.GetValue(ref key, out pv) == 0)
                    {
                        try
                        {
                            if (pv.vt == 31 && pv.pwszVal != IntPtr.Zero)
                            {
                                string rawFriendly = Marshal.PtrToStringUni(pv.pwszVal);
                                ParseEndpointDisplayName(rawFriendly, out cleanName, out isHeadphone);
                            }
                        }
                        finally
                        {
                            try { PropVariantClear(ref pv); } catch { }
                        }
                    }
                }
                return !string.IsNullOrEmpty(endpointId);
            }
            catch
            {
                return false;
            }
            finally
            {
                if (store != null) { try { Marshal.ReleaseComObject(store); } catch { } }
                if (device != null) { try { Marshal.ReleaseComObject(device); } catch { } }
                if (enumerator != null) { try { Marshal.ReleaseComObject(enumerator); } catch { } }
            }
        }

        private IAudioEndpointVolume EnsureEndpoint()
        {
            if (_epVol != null) return _epVol;
            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)(new MMDeviceEnumeratorComObject());
                int hr = enumerator.GetDefaultAudioEndpoint(0, 1, out device);
                if (hr != 0 || device == null) hr = enumerator.GetDefaultAudioEndpoint(0, 0, out device);
                if (hr == 0 && device != null)
                {
                    object obj;
                    Guid iid = IID_IAudioEndpointVolume;
                    if (device.Activate(ref iid, 23, IntPtr.Zero, out obj) == 0 && obj != null)
                        _epVol = (IAudioEndpointVolume)obj;
                }
            }
            catch { _epVol = null; }
            finally
            {
                if (device != null) { try { Marshal.ReleaseComObject(device); } catch { } }
                if (enumerator != null) { try { Marshal.ReleaseComObject(enumerator); } catch { } }
            }
            return _epVol;
        }

        private void ReleaseEndpoint()
        {
            if (_epVol != null)
            {
                try { Marshal.ReleaseComObject(_epVol); } catch { }
                _epVol = null;
            }
        }

        public void ToggleMute()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                lock (_audioLock)
                {
                    try
                    {
                        IAudioEndpointVolume ep = EnsureEndpoint();
                        if (ep == null) return;
                        bool curMute;
                        if (ep.GetMute(out curMute) == 0)
                        {
                            Guid ctx = Guid.Empty;
                            ep.SetMute(!curMute, ref ctx);
                        }
                    }
                    catch { ReleaseEndpoint(); }
                }
            });
        }

        public void AdjustVolume(int deltaPercent)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                lock (_audioLock)
                {
                    try
                    {
                        IAudioEndpointVolume ep = EnsureEndpoint();
                        if (ep == null) return;
                        float curScalar;
                        if (ep.GetMasterVolumeLevelScalar(out curScalar) == 0)
                        {
                            float next = Math.Max(0f, Math.Min(1f, curScalar + (deltaPercent / 100.0f)));
                            Guid ctx = Guid.Empty;
                            if (deltaPercent > 0)
                            {
                                bool curMute;
                                if (ep.GetMute(out curMute) == 0 && curMute) ep.SetMute(false, ref ctx);
                            }
                            ep.SetMasterVolumeLevelScalar(next, ref ctx);
                        }
                    }
                    catch { ReleaseEndpoint(); }
                }
            });
        }

        public void AdjustBrightness(int deltaPercent)
        {
            if (!SupportsBrightness) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    int target = Math.Max(0, Math.Min(100, BrightnessPercent + deltaPercent));
                    using (ManagementObjectSearcher mos = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM WmiMonitorBrightnessMethods"))
                    {
                        foreach (ManagementObject mo in mos.Get())
                        {
                            mo.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)target });
                            break;
                        }
                    }
                }
                catch { }
            });
        }

        private static string FormatPrivacyAppName(string rawKeyName, bool isNonPackaged)
        {
            if (string.IsNullOrEmpty(rawKeyName)) return "App";
            string core = rawKeyName;
            if (isNonPackaged)
            {
                int hash = core.LastIndexOf('#');
                if (hash >= 0 && hash + 1 < core.Length) core = core.Substring(hash + 1);
                if (core.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    core = core.Substring(0, core.Length - 4);
            }
            else
            {
                int us = core.IndexOf('_');
                if (us > 0) core = core.Substring(0, us);
                int dot = core.LastIndexOf('.');
                if (dot >= 0 && dot + 1 < core.Length) core = core.Substring(dot + 1);
            }

            string low = core.ToLowerInvariant();
            if (low == "chrome") return "Chrome";
            if (low == "msedge" || low == "msedgewebview2") return "Edge";
            if (low == "firefox") return "Firefox";
            if (low == "brave") return "Brave";
            if (low == "coccoc") return "C\u1ED1c C\u1ED1c";
            if (low == "discord") return "Discord";
            if (low == "zalo") return "Zalo";
            if (low == "zoom") return "Zoom";
            if (low == "obs64" || low == "obs32" || low == "obs") return "OBS Studio";
            if (low.IndexOf("teams", StringComparison.Ordinal) >= 0) return "Teams";
            if (low.IndexOf("windowscamera", StringComparison.Ordinal) >= 0) return "Camera";
            if (low.IndexOf("screensketch", StringComparison.Ordinal) >= 0) return "Snipping Tool";
            if (low.IndexOf("soundrecorder", StringComparison.Ordinal) >= 0) return "Voice Recorder";

            if (core.Length > 1)
                return char.ToUpperInvariant(core[0]) + core.Substring(1);
            return core;
        }

        private static bool ScanConsentCapability(string capability, out string activeApp)
        {
            activeApp = "";
            long newestStart = 0;
            long minRecentFt = DateTime.UtcNow.AddHours(-18).ToFileTimeUtc();

            string regPath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\" + capability;
            try
            {
                using (RegistryKey root = Registry.CurrentUser.OpenSubKey(regPath, false))
                {
                    if (root == null) return false;
                    string[] subNames = root.GetSubKeyNames();
                    for (int i = 0; i < subNames.Length; i++)
                    {
                        string sn = subNames[i];
                        if (string.Equals(sn, "NonPackaged", StringComparison.OrdinalIgnoreCase))
                        {
                            using (RegistryKey npRoot = root.OpenSubKey(sn, false))
                            {
                                if (npRoot == null) continue;
                                string[] npSubs = npRoot.GetSubKeyNames();
                                for (int j = 0; j < npSubs.Length; j++)
                                {
                                    using (RegistryKey appKey = npRoot.OpenSubKey(npSubs[j], false))
                                    {
                                        if (appKey == null) continue;
                                        object oStart = appKey.GetValue("LastUsedTimeStart");
                                        object oStop = appKey.GetValue("LastUsedTimeStop");
                                        if (oStart == null) continue;
                                        long tStart = Convert.ToInt64(oStart);
                                        long tStop = (oStop != null) ? Convert.ToInt64(oStop) : 0L;
                                        if (tStart > minRecentFt && (tStop == 0L || tStart > tStop))
                                        {
                                            // Verify executable process is still running so crashed sessions never stick
                                            string exeName = npSubs[j];
                                            int hIdx = exeName.LastIndexOf('#');
                                            if (hIdx >= 0 && hIdx + 1 < exeName.Length) exeName = exeName.Substring(hIdx + 1);
                                            if (exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                                                exeName = exeName.Substring(0, exeName.Length - 4);

                                            bool alive = true;
                                            if (!string.IsNullOrEmpty(exeName))
                                            {
                                                Process[] procs = null;
                                                try
                                                {
                                                    procs = Process.GetProcessesByName(exeName);
                                                    alive = (procs != null && procs.Length > 0);
                                                }
                                                catch { alive = true; }
                                                finally
                                                {
                                                    if (procs != null)
                                                    {
                                                        for (int p = 0; p < procs.Length; p++)
                                                        {
                                                            try { procs[p].Dispose(); } catch { }
                                                        }
                                                    }
                                                }
                                            }
                                            if (alive && tStart >= newestStart)
                                            {
                                                newestStart = tStart;
                                                activeApp = FormatPrivacyAppName(npSubs[j], true);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        else
                        {
                            using (RegistryKey pkgKey = root.OpenSubKey(sn, false))
                            {
                                if (pkgKey == null) continue;
                                object oStart = pkgKey.GetValue("LastUsedTimeStart");
                                object oStop = pkgKey.GetValue("LastUsedTimeStop");
                                if (oStart == null) continue;
                                long tStart = Convert.ToInt64(oStart);
                                long tStop = (oStop != null) ? Convert.ToInt64(oStop) : 0L;
                                if (tStart > minRecentFt && (tStop == 0L || tStart > tStop) && tStart >= newestStart)
                                {
                                    newestStart = tStart;
                                    activeApp = FormatPrivacyAppName(sn, false);
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            return newestStart > 0;
        }

        private void PollPrivacySensors()
        {
            string camApp;
            string micApp;
            bool camOn = ScanConsentCapability("webcam", out camApp);
            bool micOn = ScanConsentCapability("microphone", out micApp);

            bool prevCam = IsCameraActive;
            bool prevMic = IsMicActive;

            CameraAppName = camOn ? camApp : "";
            MicAppName = micOn ? micApp : "";
            IsCameraActive = camOn;
            IsMicActive = micOn;

            if (!_privacyBaselineReady)
            {
                _privacyBaselineReady = true;
                return;
            }

            bool camStarted = (camOn && !prevCam);
            bool micStarted = (micOn && !prevMic);
            if (camStarted || micStarted)
            {
                int mode = (camOn && micOn) ? 3 : (camOn ? 2 : 1);
                OsdChangedHandler h = OsdChanged;
                if (h != null)
                {
                    try { h(OsdKind.PrivacyAlert, 100, false, mode); } catch { }
                }
            }
        }

        private int QueryWmiBrightness()
        {
            try
            {
                using (ManagementObjectSearcher mos = new ManagementObjectSearcher("root\\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness"))
                {
                    foreach (ManagementObject mo in mos.Get())
                    {
                        object val = mo["CurrentBrightness"];
                        if (val != null)
                        {
                            int b = Convert.ToInt32(val);
                            if (b >= 0 && b <= 100) return b;
                        }
                    }
                }
            }
            catch { }
            return -1;
        }

        private void StartBrightnessWatcher()
        {
            try
            {
                int initB = QueryWmiBrightness();
                if (initB >= 0)
                {
                    SupportsBrightness = true;
                    BrightnessPercent = initB;
                    _lastBri = initB;
                    _briReady = true;
                    ManagementScope scope = new ManagementScope("\\\\.\\root\\WMI");
                    scope.Connect();
                    _briWatcher = new ManagementEventWatcher(scope, new EventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
                    _briWatcher.EventArrived += OnBrightnessEventArrived;
                    _briWatcher.Start();
                }
            }
            catch { _briWatcher = null; }
        }

        private void OnBrightnessEventArrived(object sender, EventArrivedEventArgs e)
        {
            try
            {
                if (e == null || e.NewEvent == null) return;
                object val = e.NewEvent["Brightness"];
                if (val != null) HandleBrightnessSample(Math.Max(0, Math.Min(100, Convert.ToInt32(val))));
            }
            catch { }
        }

        private void HandleBrightnessSample(int b)
        {
            if (!_briReady) { _briReady = true; _lastBri = b; BrightnessPercent = b; return; }
            if (b != _lastBri)
            {
                int delta = b - _lastBri;
                _lastBri = b;
                BrightnessPercent = b;
                OsdChangedHandler h = OsdChanged;
                if (h != null) { try { h(OsdKind.Brightness, b, false, delta); } catch { } }
            }
        }

        private void WorkerLoop()
        {
            StartBrightnessWatcher();
            int tick = 0;
            while (_running)
            {
                tick++;
                try
                {
                    bool endpointSwitched = false;
                    if (tick == 1 || tick % 11 == 0)
                    {
                        string epId;
                        string epName;
                        bool isHp;
                        if (ProbeDefaultAudioEndpoint(out epId, out epName, out isHp))
                        {
                            AudioDeviceName = epName;
                            IsHeadphoneDevice = isHp;
                            if (!_endpointBaselineReady)
                            {
                                _endpointBaselineReady = true;
                                _lastEndpointId = epId;
                            }
                            else if (!string.Equals(epId, _lastEndpointId, StringComparison.OrdinalIgnoreCase))
                            {
                                _lastEndpointId = epId;
                                endpointSwitched = true;
                            }
                        }
                    }

                    lock (_audioLock)
                    {
                        if (endpointSwitched || tick % 45 == 0) ReleaseEndpoint();
                        IAudioEndpointVolume ep = EnsureEndpoint();
                        if (ep != null)
                        {
                            float scalar; bool muted;
                            if (ep.GetMasterVolumeLevelScalar(out scalar) == 0 && ep.GetMute(out muted) == 0)
                            {
                                int pct = Math.Max(0, Math.Min(100, (int)Math.Round(scalar * 100.0f)));
                                if (!_volReady)
                                {
                                    _volReady = true; _lastVol = pct; _lastMute = muted;
                                    VolumePercent = pct; IsMuted = muted;
                                }
                                else if (endpointSwitched)
                                {
                                    _lastVol = pct; _lastMute = muted;
                                    VolumePercent = pct; IsMuted = muted;
                                    OsdChangedHandler h = OsdChanged;
                                    if (h != null) { try { h(OsdKind.AudioDevice, pct, muted, IsHeadphoneDevice ? 1 : 0); } catch { } }
                                }
                                else if (pct != _lastVol || muted != _lastMute)
                                {
                                    int delta = (muted != _lastMute) ? (muted ? -1 : 1) : (pct - _lastVol);
                                    _lastVol = pct; _lastMute = muted;
                                    VolumePercent = pct; IsMuted = muted;
                                    OsdChangedHandler h = OsdChanged;
                                    if (h != null) { try { h(OsdKind.Volume, pct, muted, delta); } catch { } }
                                }
                            }
                            else ReleaseEndpoint();
                        }
                    }

                    if (tick == 2 || tick % 13 == 0)
                    {
                        PollPrivacySensors();
                    }

                    if (SupportsBrightness && (tick % 6 == 0))
                    {
                        int curB = QueryWmiBrightness();
                        if (curB >= 0) HandleBrightnessSample(curB);
                    }
                }
                catch { }
                Thread.Sleep(75);
            }
        }

        public void Dispose()
        {
            _running = false;
            try { if (_briWatcher != null) { _briWatcher.Stop(); _briWatcher.Dispose(); _briWatcher = null; } } catch { }
            lock (_audioLock) { ReleaseEndpoint(); }
        }
    }
}
