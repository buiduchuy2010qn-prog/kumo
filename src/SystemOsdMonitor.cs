using System;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;

namespace Kumo
{
    public enum OsdKind { None = 0, Volume = 1, Brightness = 2, Battery = 3 }
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
        }

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
            int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
            int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
            int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid pguidEventContext);
            int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
        }

        private static Guid IID_IAudioEndpointVolume = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");

        public int VolumePercent { get; private set; }
        public bool IsMuted { get; private set; }
        public int BrightnessPercent { get; private set; }
        public bool SupportsBrightness { get; private set; }
        public event OsdChangedHandler OsdChanged;

        private volatile bool _running;
        private Thread _worker;
        private readonly object _audioLock = new object();
        private IAudioEndpointVolume _epVol;
        private bool _volReady;
        private int _lastVol;
        private bool _lastMute;
        private bool _briReady;
        private int _lastBri;
        private ManagementEventWatcher _briWatcher;

        public SystemOsdMonitor()
        {
            VolumePercent = 50; IsMuted = false; BrightnessPercent = 80; SupportsBrightness = false;
            _lastVol = -1; _lastBri = -1; _running = true;
            _worker = new Thread(WorkerLoop);
            _worker.IsBackground = true;
            _worker.Priority = ThreadPriority.BelowNormal;
            _worker.Name = "KumoSystemOsd";
            _worker.SetApartmentState(ApartmentState.MTA);
            _worker.Start();
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
                    lock (_audioLock)
                    {
                        if (tick % 45 == 0) ReleaseEndpoint();
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