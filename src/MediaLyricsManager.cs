using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Kumo
{
    public struct LyricWordSpan
    {
        public int CharStart;
        public int CharLength;
        public double StartSec;
        public double EndSec;
    }

    public class LyricLine
    {
        public double TimeSec;
        public double EndSec;
        public string Text;
        public LyricWordSpan[] Words;
    }

    public class MediaLyricsManager : IDisposable
    {
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MediaMMDeviceEnumeratorCom { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMediaMMDeviceEnumerator
        {
            int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IntPtr ppDevices);
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMediaMMDevice ppEndpoint);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMediaMMDevice
        {
            int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        }

        [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMediaAudioMeterInformation
        {
            int GetPeakValue(out float pfPeak);
        }

        public string CurrentTitle { get; private set; }
        public string CurrentArtist { get; private set; }
        public string SourceAppId { get; private set; }
        public bool HasMedia { get; private set; }
        public bool IsPlaying { get; private set; }

        public string CurrentLyricLine { get; private set; }
        public string NextLyricLine { get; private set; }
        public string LyricsStatus { get; private set; }

        private double _basePositionSec = 0.0;
        private double _durationSec = 0.0;
        private DateTime _positionSampledUtc = DateTime.UtcNow;

        private Bitmap _albumArt = null;
        private Color _dominantColor = Color.FromArgb(44, 110, 215);

        private GlobalSystemMediaTransportControlsSessionManager _smtcManager = null;
        private GlobalSystemMediaTransportControlsSession _currentSession = null;

        private List<LyricLine> _syncedLyrics = new List<LyricLine>();
        private string _lastTrackKey = "";
        private string _lastThumbTrackKey = "";
        private string _lastLyricsQuery = "";

        // MV vs Audio-Single auto offset + Live WASAPI peak gate + User per-song fine offset
        private IMediaAudioMeterInformation _audioMeter = null;
        private DateTime _meterRetryAfterUtc = DateTime.MinValue;
        private double _lrcAudioMasterDurSec = 0.0;
        private double _lrcFirstTagSec = 0.0;
        private double _lrcLastTagSec = 0.0;
        private bool _lrcAlreadyMvShifted = false;
        private bool _lrcIsStreamingMaster = false;
        private bool _isLikelyMvTrack = false;
        private double _autoMvOffsetSec = 0.0;
        private bool _sawNearZeroStart = false;
        private int _sustainedMusicTicks = 0;
        private volatile bool _silentIntroHold = false;

        private volatile bool _running;
        private Thread _worker;
        private readonly object _lock = new object();

        public MediaLyricsManager()
        {
            CurrentTitle = "";
            CurrentArtist = "";
            SourceAppId = "";
            HasMedia = false;
            IsPlaying = false;
            LyricsStatus = "Waiting for media";
            CurrentLyricLine = "";
            NextLyricLine = "";

            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } catch { }

            _running = true;
            _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "KumoSMTC" };
            _worker.Start();
        }

        public double DurationSec
        {
            get { return _durationSec; }
        }

        public double GetLivePositionSec()
        {
            double pos = _basePositionSec;
            if (IsPlaying)
            {
                double elapsed = (DateTime.UtcNow - _positionSampledUtc).TotalSeconds;
                if (elapsed > 0 && elapsed < 30.0)
                {
                    pos += elapsed;
                }
            }
            if (_durationSec > 0.5 && pos > _durationSec) pos = _durationSec;
            if (pos < 0) pos = 0;
            return pos;
        }

        public Color DominantColor
        {
            get { return _dominantColor; }
        }

        // Thread-safe clone or direct locked draw helper so GDI+ never hits "Object is currently in use elsewhere"
        public bool DrawAlbumArt(Graphics g, RectangleF destRect, GraphicsPath clipPath)
        {
            lock (_lock)
            {
                if (_albumArt == null) return false;
                GraphicsState st = g.Save();
                if (clipPath != null) g.SetClip(clipPath, CombineMode.Intersect);
                g.DrawImage(_albumArt, destRect);
                g.Restore(st);
                return true;
            }
        }

        public double TotalLyricsOffsetSec
        {
            get { return _autoMvOffsetSec; }
        }

        public double GetEffectiveLyricsPosSec()
        {
            double raw = GetLivePositionSec();
            if (_silentIntroHold && raw < 30.0)
            {
                return -1.0;
            }
            return raw - _autoMvOffsetSec;
        }

        public LyricLine[] GetDisplayLyricsWindow(out int activeSlot)
        {
            int clampedScroll;
            return GetDisplayLyricsWindow(0, out activeSlot, out clampedScroll);
        }

        public LyricLine[] GetDisplayLyricsWindow(int scrollDeltaLines, out int activeSlot, out int clampedScroll)
        {
            activeSlot = -1;
            clampedScroll = 0;
            double effPos = GetEffectiveLyricsPosSec();
            lock (_lock)
            {
                if (_syncedLyrics.Count == 0)
                {
                    return new LyricLine[0];
                }

                bool preVocal = (effPos < _syncedLyrics[0].TimeSec);
                int curIdx = -1;
                if (!preVocal)
                {
                    curIdx = 0;
                    for (int i = 0; i < _syncedLyrics.Count; i++)
                    {
                        if (_syncedLyrics[i].TimeSec <= effPos)
                            curIdx = i;
                        else
                            break;
                    }
                }

                int baseStart = preVocal ? 0 : Math.Max(0, curIdx - 1);
                int maxStart = Math.Max(0, _syncedLyrics.Count - 4);
                if (baseStart > maxStart) baseStart = maxStart;

                int start = Math.Max(0, Math.Min(maxStart, baseStart + scrollDeltaLines));
                clampedScroll = start - baseStart;

                int count = Math.Min(4, _syncedLyrics.Count - start);
                LyricLine[] res = new LyricLine[count];
                for (int i = 0; i < count; i++)
                {
                    res[i] = _syncedLyrics[start + i];
                    if (!preVocal && (start + i == curIdx))
                    {
                        activeSlot = i;
                    }
                }
                return res;
            }
        }

        // ==================== REAL WINRT SMTC PLAYBACK CONTROLS ====================
        public void TogglePlayPause()
        {
            GlobalSystemMediaTransportControlsSession s = _currentSession;
            if (s != null)
            {
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        WaitWinRT(s.TryTogglePlayPauseAsync(), 1200);
                        PollSmtcOnce();
                    }
                    catch { NativeMethods.SendMediaKey(NativeMethods.VK_MEDIA_PLAY_PAUSE); }
                });
            }
            else
            {
                NativeMethods.SendMediaKey(NativeMethods.VK_MEDIA_PLAY_PAUSE);
            }
        }

        public void SkipNext()
        {
            GlobalSystemMediaTransportControlsSession s = _currentSession;
            if (s != null)
            {
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        WaitWinRT(s.TrySkipNextAsync(), 1200);
                        Thread.Sleep(180);
                        PollSmtcOnce();
                    }
                    catch { NativeMethods.SendMediaKey(NativeMethods.VK_MEDIA_NEXT_TRACK); }
                });
            }
            else
            {
                NativeMethods.SendMediaKey(NativeMethods.VK_MEDIA_NEXT_TRACK);
            }
        }

        public void SkipPrevious()
        {
            GlobalSystemMediaTransportControlsSession s = _currentSession;
            if (s != null)
            {
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        WaitWinRT(s.TrySkipPreviousAsync(), 1200);
                        Thread.Sleep(180);
                        PollSmtcOnce();
                    }
                    catch { NativeMethods.SendMediaKey(NativeMethods.VK_MEDIA_PREV_TRACK); }
                });
            }
            else
            {
                NativeMethods.SendMediaKey(NativeMethods.VK_MEDIA_PREV_TRACK);
            }
        }

        public void SeekToSeconds(double targetSec)
        {
            GlobalSystemMediaTransportControlsSession s = _currentSession;
            if (s == null || _durationSec <= 0.5) return;

            double clamped = Math.Max(0.0, Math.Min(_durationSec, targetSec));
            _basePositionSec = clamped;
            _positionSampledUtc = DateTime.UtcNow;

            long ticks = (long)(clamped * 10000000.0);
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    WaitWinRT(s.TryChangePlaybackPositionAsync(ticks), 1200);
                }
                catch { }
            });
        }

        public void SeekToFraction(double pct01)
        {
            if (_durationSec <= 0.5) return;
            double clampedPct = Math.Max(0.0, Math.Min(1.0, pct01));
            SeekToSeconds(clampedPct * _durationSec);
        }

        public void ActivateSourceApp()
        {
            string appId = SourceAppId ?? "";
            string[] candidates = new string[] { "Spotify", "chrome", "msedge", "firefox", "coccoc", "Music.UI", "vlc" };
            string preferred = null;
            if (appId.IndexOf("Spotify", StringComparison.OrdinalIgnoreCase) >= 0) preferred = "Spotify";
            else if (appId.IndexOf("chrome", StringComparison.OrdinalIgnoreCase) >= 0) preferred = "chrome";
            else if (appId.IndexOf("msedge", StringComparison.OrdinalIgnoreCase) >= 0) preferred = "msedge";
            else if (appId.IndexOf("firefox", StringComparison.OrdinalIgnoreCase) >= 0) preferred = "firefox";

            ThreadPool.QueueUserWorkItem(delegate
            {
                if (preferred != null && TryFocusProcess(preferred)) return;
                for (int i = 0; i < candidates.Length; i++)
                {
                    if (TryFocusProcess(candidates[i])) return;
                }
            });
        }

        private static bool TryFocusProcess(string procName)
        {
            try
            {
                Process[] procs = Process.GetProcessesByName(procName);
                for (int i = 0; i < procs.Length; i++)
                {
                    IntPtr h = procs[i].MainWindowHandle;
                    if (h != IntPtr.Zero)
                    {
                        ShowWindow(h, 9); // SW_RESTORE if minimized
                        SetForegroundWindow(h);
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static T WaitWinRT<T>(IAsyncOperation<T> op, int timeoutMs)
        {
            if (op == null) return default(T);
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (op.Status == AsyncStatus.Started && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(12);
            }
            if (op.Status == AsyncStatus.Completed)
            {
                return op.GetResults();
            }
            return default(T);
        }

        // ==================== BACKGROUND SMTC + THUMBNAIL + LRCLIB WORKER ====================
        private float ReadAudioPeak()
        {
            try
            {
                if (_audioMeter == null)
                {
                    if (DateTime.UtcNow < _meterRetryAfterUtc) return -1f;
                    _meterRetryAfterUtc = DateTime.UtcNow.AddSeconds(5);
                    IMediaMMDeviceEnumerator en = (IMediaMMDeviceEnumerator)(new MediaMMDeviceEnumeratorCom());
                    IMediaMMDevice dev;
                    if (en.GetDefaultAudioEndpoint(0, 0, out dev) == 0 && dev != null)
                    {
                        Guid iid = new Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064");
                        object obj;
                        if (dev.Activate(ref iid, 23, IntPtr.Zero, out obj) == 0 && obj != null)
                        {
                            _audioMeter = (IMediaAudioMeterInformation)obj;
                        }
                    }
                }
                if (_audioMeter != null)
                {
                    float pk;
                    if (_audioMeter.GetPeakValue(out pk) == 0) return pk;
                    _audioMeter = null;
                }
            }
            catch
            {
                _audioMeter = null;
            }
            return -1f;
        }

        private void SampleLiveAudioGate()
        {
            if (!HasMedia || !IsPlaying)
            {
                _silentIntroHold = false;
                return;
            }
            double raw = GetLivePositionSec();
            if (raw <= 2.8 && !_sawNearZeroStart)
            {
                _sawNearZeroStart = true;
                _sustainedMusicTicks = 0;
            }

            float pk = ReadAudioPeak();
            if (pk < 0f)
            {
                _silentIntroHold = false;
                return;
            }

            if (pk >= 0.045f)
            {
                _sustainedMusicTicks = Math.Min(30, _sustainedMusicTicks + 1);
            }
            else if (pk < 0.018f)
            {
                _sustainedMusicTicks = Math.Max(0, _sustainedMusicTicks - 1);
            }

            if (_sawNearZeroStart && raw < 30.0 && _sustainedMusicTicks < 3 && pk < 0.015f)
            {
                _silentIntroHold = true;
            }
            else if (_sustainedMusicTicks >= 4 || raw >= 30.0)
            {
                _silentIntroHold = false;
            }
        }

        private void RecalculateAutoMvOffsetLocked()
        {
            if (_syncedLyrics.Count == 0 ||
                !string.Equals(LyricsStatus, "Synced", StringComparison.Ordinal) ||
                _lrcAlreadyMvShifted ||
                _lrcIsStreamingMaster)
            {
                _autoMvOffsetSec = 0.0;
                return;
            }
            if (_durationSec > 45.0 && _lrcAudioMasterDurSec > 35.0)
            {
                double excess = _durationSec - _lrcAudioMasterDurSec;
                bool allowMvShift = _isLikelyMvTrack ||
                    (_lrcFirstTagSec < 18.0 && (_durationSec - _lrcLastTagSec) > Math.Max(18.0, excess * 0.65));
                if (allowMvShift && excess >= 5.0 && excess <= 95.0 && _lrcLastTagSec <= _lrcAudioMasterDurSec + 3.0)
                {
                    _autoMvOffsetSec = (excess <= 14.5)
                        ? Math.Round(excess * 0.88, 1)
                        : Math.Round(excess * 0.76, 1);
                    return;
                }
            }
            _autoMvOffsetSec = 0.0;
        }

        private void WorkerLoop()
        {
            int sub = 0;
            while (_running)
            {
                try
                {
                    if (sub == 0)
                    {
                        PollSmtcOnce();
                        UpdateActiveLyricLine();
                    }
                    SampleLiveAudioGate();
                }
                catch { }
                sub = (sub + 1) % 9;
                Thread.Sleep(100);
            }
        }

        private void PollSmtcOnce()
        {
            try
            {
                if (_smtcManager == null)
                {
                    _smtcManager = WaitWinRT(GlobalSystemMediaTransportControlsSessionManager.RequestAsync(), 2000);
                }
                if (_smtcManager == null)
                {
                    ClearMediaState();
                    return;
                }

                // Prefer a session that is actively Playing; otherwise fall back to GetCurrentSession()
                GlobalSystemMediaTransportControlsSession chosen = null;
                var sessions = _smtcManager.GetSessions();
                if (sessions != null)
                {
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        var s = sessions[i];
                        if (s == null) continue;
                        var pb = s.GetPlaybackInfo();
                        if (pb != null && pb.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                        {
                            chosen = s;
                            break;
                        }
                    }
                }
                if (chosen == null)
                {
                    chosen = _smtcManager.GetCurrentSession();
                }

                if (chosen == null)
                {
                    _currentSession = null;
                    ClearMediaState();
                    return;
                }

                _currentSession = chosen;
                var pbInfo = chosen.GetPlaybackInfo();
                var status = (pbInfo != null) ? pbInfo.PlaybackStatus : GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;

                if (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed ||
                    status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped)
                {
                    ClearMediaState();
                    return;
                }

                var props = WaitWinRT(chosen.TryGetMediaPropertiesAsync(), 1500);
                if (props == null || (string.IsNullOrEmpty(props.Title) && string.IsNullOrEmpty(props.Artist)))
                {
                    ClearMediaState();
                    return;
                }

                string title = (props.Title ?? "").Trim();
                string artist = (props.Artist ?? "").Trim();
                if (string.IsNullOrEmpty(artist) && !string.IsNullOrEmpty(props.AlbumArtist))
                {
                    artist = props.AlbumArtist.Trim();
                }

                // Clean up YouTube "- YouTube" or "(123)" prefixes if present
                title = Regex.Replace(title, @"^\(\d+\)\s*", "").Trim();
                if (string.IsNullOrEmpty(artist))
                {
                    int dash = title.IndexOf(" - ");
                    if (dash > 0 && dash < title.Length - 3)
                    {
                        artist = title.Substring(0, dash).Trim();
                        title = title.Substring(dash + 3).Trim();
                    }
                }

                bool playingNow = (status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);

                // Read Timeline Position & Duration
                try
                {
                    var tl = chosen.GetTimelineProperties();
                    if (tl != null)
                    {
                        double endSec = tl.EndTime.TotalSeconds - tl.StartTime.TotalSeconds;
                        double posSec = tl.Position.TotalSeconds - tl.StartTime.TotalSeconds;
                        if (endSec > 0.5 && endSec < 36000.0)
                        {
                            _durationSec = endSec;
                        }
                        else if (_lastTrackKey != (artist + "|" + title))
                        {
                            _durationSec = 0.0;
                        }

                        if (posSec >= 0.0 && posSec < 36000.0)
                        {
                            if (playingNow && tl.LastUpdatedTime.Year > 2000)
                            {
                                double age = (DateTimeOffset.UtcNow - tl.LastUpdatedTime).TotalSeconds;
                                if (age > 0.0 && age < 600.0) posSec += age;
                            }
                            if (_durationSec > 0.5 && posSec > _durationSec) posSec = _durationSec;
                            bool samePlayingTrack = (playingNow && IsPlaying && string.Equals(artist + "|" + title, _lastTrackKey, StringComparison.Ordinal));
                            double curExtrap = GetLivePositionSec();
                            if (!samePlayingTrack || Math.Abs(posSec - curExtrap) >= 0.36)
                            {
                                _basePositionSec = posSec;
                                _positionSampledUtc = DateTime.UtcNow;
                            }
                        }
                    }
                }
                catch { }

                SourceAppId = chosen.SourceAppUserModelId ?? "";
                CurrentTitle = title;
                CurrentArtist = artist;
                IsPlaying = playingNow;
                HasMedia = true;

                string trackKey = artist + "|" + title;
                bool trackChanged = !string.Equals(trackKey, _lastTrackKey, StringComparison.Ordinal);
                _lastTrackKey = trackKey;
                if (trackChanged)
                {
                    lock (_lock)
                    {
                        _lrcAudioMasterDurSec = 0.0;
                        _lrcFirstTagSec = 0.0;
                        _lrcLastTagSec = 0.0;
                        _lrcAlreadyMvShifted = false;
                        _lrcIsStreamingMaster = false;
                        _isLikelyMvTrack = false;
                        _autoMvOffsetSec = 0.0;
                        _sawNearZeroStart = (GetLivePositionSec() <= 3.0);
                        _sustainedMusicTicks = 0;
                        _silentIntroHold = false;
                    }
                }
                else
                {
                    lock (_lock)
                    {
                        RecalculateAutoMvOffsetLocked();
                    }
                }

                // Load Album Art Thumbnail (when track changes OR if we haven't decoded art for this track yet — YouTube sometimes attaches thumbnail 0.5s after title!)
                bool needThumb = trackChanged || !string.Equals(trackKey, _lastThumbTrackKey, StringComparison.Ordinal);
                if (needThumb && props.Thumbnail != null)
                {
                    Bitmap decoded = ReadSquareThumbnail(props.Thumbnail);
                    if (decoded != null)
                    {
                        Color dom = ExtractVibrantColor(decoded);
                        lock (_lock)
                        {
                            if (_albumArt != null) { try { _albumArt.Dispose(); } catch { } }
                            _albumArt = decoded;
                            _dominantColor = dom;
                        }
                        _lastThumbTrackKey = trackKey;
                    }
                }
                else if (trackChanged && props.Thumbnail == null)
                {
                    lock (_lock)
                    {
                        if (_albumArt != null) { try { _albumArt.Dispose(); } catch { } _albumArt = null; }
                        _dominantColor = Color.FromArgb(44, 110, 215);
                    }
                }

                // Fetch Synced Lyrics from LRCLIB when song changes
                string queryKey = (artist + " " + title).Trim();
                if (!string.IsNullOrEmpty(queryKey) && !string.Equals(queryKey, _lastLyricsQuery, StringComparison.OrdinalIgnoreCase))
                {
                    _lastLyricsQuery = queryKey;
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        FetchLyricsFromLrclib(title, artist);
                    });
                }
            }
            catch
            {
                // Reset manager handle if RPC disconnected
                _smtcManager = null;
            }
        }

        private void ClearMediaState()
        {
            HasMedia = false;
            IsPlaying = false;
            CurrentTitle = "";
            CurrentArtist = "";
            SourceAppId = "";
            _basePositionSec = 0.0;
            _durationSec = 0.0;
            _lastTrackKey = "";
            _lastThumbTrackKey = "";
            lock (_lock)
            {
                if (_albumArt != null)
                {
                    try { _albumArt.Dispose(); } catch { }
                    _albumArt = null;
                }
            }
        }

        private static Bitmap ReadSquareThumbnail(IRandomAccessStreamReference thumbRef)
        {
            IRandomAccessStreamWithContentType ras = null;
            try
            {
                ras = WaitWinRT(thumbRef.OpenReadAsync(), 2000);
                if (ras == null || ras.Size == 0) return null;

                uint targetSz = (uint)Math.Min(ras.Size, 6 * 1024 * 1024);
                using (MemoryStream ms = new MemoryStream((int)targetSz))
                {
                    using (IInputStream input = ras.GetInputStreamAt(0))
                    using (DataReader dr = new DataReader(input))
                    {
                        uint remaining = targetSz;
                        while (remaining > 0)
                        {
                            uint chunkReq = Math.Min(remaining, 65536u);
                            uint loaded = WaitWinRT(dr.LoadAsync(chunkReq), 1500);
                            uint avail = dr.UnconsumedBufferLength;
                            if (loaded == 0 && avail == 0) break;
                            uint toRead = Math.Min(avail, remaining);
                            if (toRead == 0) break;
                            byte[] chunk = new byte[toRead];
                            dr.ReadBytes(chunk);
                            ms.Write(chunk, 0, chunk.Length);
                            remaining -= toRead;
                        }
                    }

                    if (ms.Length < 32) return null;
                    ms.Position = 0;

                    using (Bitmap raw = new Bitmap(ms))
                    {
                        int w = raw.Width;
                        int h = raw.Height;
                        if (w <= 0 || h <= 0) return null;

                        // Center-crop 16:9 or letterboxed 4:3 YouTube thumbnails cleanly into a crisp 160x160 square cover!
                        int side = Math.Min(w, h);
                        if (w > h && h >= 180)
                        {
                            Color topPx = raw.GetPixel(w / 2, Math.Max(1, h / 14));
                            Color botPx = raw.GetPixel(w / 2, Math.Min(h - 2, h - h / 14));
                            if (topPx.R < 16 && topPx.G < 16 && topPx.B < 16 && botPx.R < 16 && botPx.G < 16 && botPx.B < 16)
                            {
                                side = (int)(h * 0.74f);
                            }
                        }

                        int sx = (w - side) / 2;
                        int sy = (h - side) / 2;

                        Bitmap sq = new Bitmap(160, 160, PixelFormat.Format32bppArgb);
                        using (Graphics g = Graphics.FromImage(sq))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.SmoothingMode = SmoothingMode.AntiAlias;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            g.DrawImage(raw, new Rectangle(0, 0, 160, 160), new Rectangle(sx, sy, side, side), GraphicsUnit.Pixel);
                        }
                        return sq;
                    }
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                if (ras != null)
                {
                    try { ras.Dispose(); } catch { }
                }
            }
        }

        // Used strictly by '--preview' CLI screenshot mode to verify Album Art & Ambient Glow rendering
        public void SetMockPreviewTrack(bool enable)
        {
            if (!enable)
            {
                ClearMediaState();
                return;
            }
            Bitmap art = new Bitmap(160, 160, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(art))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (LinearGradientBrush bg = new LinearGradientBrush(
                    new Rectangle(0, 0, 160, 160),
                    Color.FromArgb(244, 63, 94),
                    Color.FromArgb(59, 130, 246),
                    LinearGradientMode.ForwardDiagonal))
                {
                    g.FillRectangle(bg, 0, 0, 160, 160);
                }
                using (SolidBrush sun = new SolidBrush(Color.FromArgb(215, 253, 224, 71)))
                {
                    g.FillEllipse(sun, 42, 34, 76, 76);
                }
                using (SolidBrush wave = new SolidBrush(Color.FromArgb(210, 17, 24, 39)))
                {
                    g.FillEllipse(wave, -20, 96, 200, 90);
                }
            }
            lock (_lock)
            {
                if (_albumArt != null) _albumArt.Dispose();
                _albumArt = art;
                _dominantColor = ExtractVibrantColor(art);
                _syncedLyrics.Clear();
                _syncedLyrics.Add(new LyricLine { TimeSec = 40, Text = "Ánh chiều buông nhẹ qua khung cửa" });
                _syncedLyrics.Add(new LyricLine { TimeSec = 45, Text = "Giai điệu ngân vang trên tai thỏ" });
                _syncedLyrics.Add(new LyricLine { TimeSec = 52, Text = "Chạm nhẹ để tua đến từng câu hát" });
                _syncedLyrics.Add(new LyricLine { TimeSec = 60, Text = "Màu nền hòa quyện theo ảnh bìa" });
                FinalizeLyricTimings(_syncedLyrics, 180.0);
            }
            CurrentTitle = "Chiều Hoàng Hôn";
            CurrentArtist = "Indie Chill · YouTube Music";
            HasMedia = true;
            IsPlaying = true;
            _basePositionSec = 47.0;
            _durationSec = 214.0;
            _positionSampledUtc = DateTime.UtcNow;
        }

        private static Color ExtractVibrantColor(Bitmap bmp)
        {
            try
            {
                long sumR = 0, sumG = 0, sumB = 0, weightTotal = 0;
                int step = 8;
                for (int y = 16; y < bmp.Height - 16; y += step)
                {
                    for (int x = 16; x < bmp.Width - 16; x += step)
                    {
                        Color c = bmp.GetPixel(x, y);
                        int max = Math.Max(c.R, Math.Max(c.G, c.B));
                        int min = Math.Min(c.R, Math.Min(c.G, c.B));
                        int sat = max - min;
                        if (max < 35) continue; // skip near-black
                        long w = 1 + (sat / 8); // prefer saturated vibrant colors!
                        sumR += c.R * w;
                        sumG += c.G * w;
                        sumB += c.B * w;
                        weightTotal += w;
                    }
                }
                if (weightTotal > 0)
                {
                    int r = (int)Math.Min(245, Math.Max(35, sumR / weightTotal));
                    int g = (int)Math.Min(245, Math.Max(35, sumG / weightTotal));
                    int b = (int)Math.Min(245, Math.Max(45, sumB / weightTotal));
                    return Color.FromArgb(r, g, b);
                }
            }
            catch { }
            return Color.FromArgb(44, 110, 215);
        }

        public bool HasCollapsedLyricNow()
        {
            if (!HasMedia || !IsPlaying) return false;
            double effPos = GetEffectiveLyricsPosSec();
            lock (_lock)
            {
                if (_syncedLyrics.Count == 0) return false;
                if (effPos < _syncedLyrics[0].TimeSec) return false;
                return true;
            }
        }

        public LyricLine GetActiveCollapsedLyricLine(out double livePosSec, out bool isSyncedLine, out string fallbackText)
        {
            livePosSec = GetEffectiveLyricsPosSec();
            lock (_lock)
            {
                if (_syncedLyrics.Count > 0 && livePosSec >= _syncedLyrics[0].TimeSec)
                {
                    int idx = 0;
                    for (int i = 0; i < _syncedLyrics.Count; i++)
                    {
                        if (_syncedLyrics[i].TimeSec <= livePosSec)
                            idx = i;
                        else
                            break;
                    }
                    LyricLine ln = _syncedLyrics[idx];
                    CurrentLyricLine = ln.Text;
                    NextLyricLine = (idx + 1 < _syncedLyrics.Count) ? _syncedLyrics[idx + 1].Text : "";
                    isSyncedLine = true;
                    fallbackText = ln.Text;
                    return ln;
                }
                isSyncedLine = false;
                fallbackText = "\u266A " + (!string.IsNullOrEmpty(CurrentTitle) ? CurrentTitle : "Music");
                return null;
            }
        }

        public string GetCollapsedLyricDisplay(out double lineProg01, out bool isSyncedLine)
        {
            double pos;
            string fb;
            LyricLine ln = GetActiveCollapsedLyricLine(out pos, out isSyncedLine, out fb);
            if (isSyncedLine && ln != null)
            {
                double span = Math.Max(1.0, ln.EndSec - ln.TimeSec);
                lineProg01 = Math.Max(0.0, Math.Min(1.0, (pos - ln.TimeSec) / span));
                return ln.Text;
            }
            lineProg01 = (pos % 10.0) / 10.0;
            return fb;
        }

        private static string SanitizeSpeakerTags(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string r = Regex.Replace(s, @"\{\s*[a-zA-Z0-9_.-]+\s*:[^}]*\}", "");
            r = Regex.Replace(r, @"\[\s*(?:agent|bg|v\d+|duet|male|female|all|chorus|verse|intro|outro)\b[^\]]*\]", "", RegexOptions.IgnoreCase);
            r = Regex.Replace(r, @"^\s*(?:v\d+|male|female|duet)\s*:\s*", "", RegexOptions.IgnoreCase);
            return r;
        }

        private static string SanitizeLyricText(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string r = SanitizeSpeakerTags(s);
            r = Regex.Replace(r, @"<\d+:\d+(?:\.\d+)?>", "");
            r = Regex.Replace(r, @"<[^>\r\n]+:\d+(?:\.\d+)?:\d+(?:\.\d+)?(?:\|[^>\r\n]+)?>", "");
            return Regex.Replace(r, @"\s{2,}", " ").Trim();
        }

        private static string NormalizeFold(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string formD = s.ToLowerInvariant().Replace('\u0111', 'd').Replace('\u0110', 'd').Normalize(NormalizationForm.FormD);
            StringBuilder sb = new StringBuilder(formD.Length);
            for (int i = 0; i < formD.Length; i++)
            {
                UnicodeCategory uc = CharUnicodeInfo.GetUnicodeCategory(formD[i]);
                if (uc == UnicodeCategory.NonSpacingMark) continue;
                char ch = formD[i];
                if (char.IsLetterOrDigit(ch)) sb.Append(ch);
                else sb.Append(' ');
            }
            return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        }

        private static bool HasNonAscii(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] > 127) return true;
            }
            return false;
        }

        private static void ExtractSongAndArtist(string rawTitle, string rawArtist, out string songCore, out string artistCore, out bool isLikelyMv)
        {
            string t = rawTitle ?? "";
            string a = rawArtist ?? "";
            isLikelyMv = Regex.IsMatch(t, @"(\bofficial\s*music\s*video\b|\bofficial\s*video\b|\bmusic\s*video\b|\bofficial\s*mv\b|\bmv\s*official\b|\bm/v\b|\bmv\b)", RegexOptions.IgnoreCase) &&
                         !Regex.IsMatch(t, @"(\blyric\s*video\b|\bmv\s*lyric\b|\bofficial\s*audio\b|\baudio\b|\bremix\b)", RegexOptions.IgnoreCase);

            string cleanA = Regex.Replace(a, @"(\s*-\s*Topic$|\bOfficial\b|\bChannel\b|\bMusic\b|\bVEVO\b)", "", RegexOptions.IgnoreCase).Trim();
            int slash = cleanA.IndexOf('/');
            if (slash > 0) cleanA = cleanA.Substring(0, slash).Trim();

            string cleanT = Regex.Replace(t, @"(\(.*?\)|\[.*?\]|\|+.*$|#.*$|official\s*music\s*video|official\s*lyric\s*video|official\s*video|music\s*video|lyric\s*video|visualizer|\bmv\b|\bm/v\b|\b4k\b|\blyrics?\b|\baudio\b)", "", RegexOptions.IgnoreCase).Trim();

            string[] parts = Regex.Split(cleanT, @"\s+[-\u2013\u2014]\s+");
            if (parts.Length >= 2)
            {
                string p0 = parts[0].Trim();
                string p1 = parts[1].Trim();
                string foldA = NormalizeFold(cleanA).Replace(" ", "");
                string fold0 = NormalizeFold(p0).Replace(" ", "");
                string fold1 = NormalizeFold(p1).Replace(" ", "");
                if (!string.IsNullOrEmpty(foldA) && !string.IsNullOrEmpty(fold0) && (foldA.Contains(fold0) || fold0.Contains(foldA)))
                {
                    cleanT = p1;
                    if (string.IsNullOrEmpty(cleanA) || (HasNonAscii(p0) && !HasNonAscii(cleanA))) cleanA = p0;
                }
                else if (!string.IsNullOrEmpty(foldA) && !string.IsNullOrEmpty(fold1) && (foldA.Contains(fold1) || fold1.Contains(foldA)))
                {
                    cleanT = p0;
                    if (string.IsNullOrEmpty(cleanA) || (HasNonAscii(p1) && !HasNonAscii(cleanA))) cleanA = p1;
                }
            }

            cleanT = Regex.Replace(cleanT, @"(\bft\.?\b.*$|\bfeat\.?\b.*$|\bprod\.?\b.*$)", "", RegexOptions.IgnoreCase).Trim();
            cleanT = Regex.Replace(cleanT, @"\s{2,}", " ").Trim(' ', '-', '\u2013', '\u2014');
            songCore = cleanT;
            artistCore = cleanA;
        }

        private static double ScoreCandidateTitle(string targetSongFold, string candTrackFold, out bool isSameSong)
        {
            if (string.IsNullOrEmpty(targetSongFold))
            {
                isSameSong = true;
                return 0.0;
            }
            if (string.IsNullOrEmpty(candTrackFold))
            {
                isSameSong = false;
                return -400.0;
            }
            if (string.Equals(candTrackFold, targetSongFold, StringComparison.Ordinal))
            {
                isSameSong = true;
                return 1200.0;
            }

            string[] tWords = targetSongFold.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string[] cWords = candTrackFold.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (tWords.Length == 0)
            {
                isSameSong = true;
                return 0.0;
            }

            int matched = 0;
            for (int i = 0; i < tWords.Length; i++)
            {
                for (int j = 0; j < cWords.Length; j++)
                {
                    if (string.Equals(tWords[i], cWords[j], StringComparison.Ordinal))
                    {
                        matched++;
                        break;
                    }
                }
            }

            if (matched == tWords.Length)
            {
                isSameSong = true;
                int extra = Math.Max(0, cWords.Length - tWords.Length);
                return 920.0 - Math.Min(220.0, extra * 14.0);
            }
            if (tWords.Length >= 3 && matched >= tWords.Length - 1 && ((double)matched / tWords.Length) >= 0.75)
            {
                isSameSong = true;
                return 540.0;
            }

            isSameSong = false;
            return -950.0 + matched * 75.0;
        }

        private static void MeasureLrcTagBounds(string rawLrc, out double firstNonEmptySec, out double lastTagSec, out bool isStreamingMaster)
        {
            firstNonEmptySec = -1.0;
            lastTagSec = 0.0;
            isStreamingMaster = (rawLrc.IndexOf("{agent:", StringComparison.OrdinalIgnoreCase) >= 0) ||
                                Regex.IsMatch(rawLrc, @"<[^>\r\n]+:\d+\.\d+:\d+\.\d+\|");
            Regex rx = new Regex(@"\[(\d+):(\d+(?:\.\d+)?)\](.*)");
            string[] lines = rawLrc.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                Match m = rx.Match(lines[i]);
                if (!m.Success) continue;
                int mins;
                double secs;
                if (int.TryParse(m.Groups[1].Value, out mins) &&
                    double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out secs))
                {
                    double t = mins * 60.0 + secs;
                    if (t > lastTagSec) lastTagSec = t;
                    string txt = SanitizeLyricText(m.Groups[3].Value);
                    if (firstNonEmptySec < 0.0 && !string.IsNullOrEmpty(txt) && txt != "\u266A" && txt != "...")
                    {
                        firstNonEmptySec = t;
                    }
                }
            }
            if (firstNonEmptySec < 0.0) firstNonEmptySec = 0.0;
        }

        private sealed class LrcCandidate
        {
            public int Index;
            public double Dur;
            public double FirstSec;
            public double LastSec;
            public bool IsSameSong;
            public bool IsStreamingMaster;
            public double TitleScore;
            public double ArtistBonus;
            public string RawSynced;
        }

        private static string FetchUrlStringWithRetry(string url)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                    req.UserAgent = "Kumo/1.1 (+https://github.com/buiduchuy2010qn-prog/kumo)";
                    req.Timeout = 5500;
                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    {
                        return sr.ReadToEnd();
                    }
                }
                catch (WebException wex)
                {
                    HttpWebResponse hr = wex.Response as HttpWebResponse;
                    if (hr != null && hr.StatusCode == HttpStatusCode.NotFound)
                    {
                        return null;
                    }
                    if (attempt < 2) Thread.Sleep(550 * (attempt + 1));
                }
                catch
                {
                    if (attempt < 2) Thread.Sleep(550 * (attempt + 1));
                }
            }
            return null;
        }

        private bool TryProcessLrclibJson(string json, string targetSongFold, string targetArtistFold)
        {
            if (string.IsNullOrEmpty(json)) return false;

            MatchCollection objs = Regex.Matches(json, @"\{\s*""id""\s*:[\s\S]*?(?=\},\s*\{\s*""id""|\}\s*\]\s*$|\}\s*$)");
            if (objs.Count == 0) return false;

            List<LrcCandidate> cands = new List<LrcCandidate>();
            string bestPlain = null;
            double bestPlainScore = -1e9;
            string artistCompact = (targetArtistFold ?? "").Replace(" ", "");

            double minSameSongAudioDur = 1e9;
            double minSameSongFirstSec = 0.0;
            bool hasStreamingAtCurrentDur = false;

            for (int i = 0; i < objs.Count; i++)
            {
                string chunk = objs[i].Value;
                double cDur = 0.0;
                Match mDur = Regex.Match(chunk, @"""duration""\s*:\s*([0-9]+(?:\.[0-9]+)?)");
                if (mDur.Success)
                {
                    double.TryParse(mDur.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out cDur);
                }

                string cTrack = "";
                Match mTr = Regex.Match(chunk, "\"trackName\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
                if (mTr.Success) cTrack = Regex.Unescape(mTr.Groups[1].Value);

                string cArtist = "";
                Match mAr = Regex.Match(chunk, "\"artistName\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
                if (mAr.Success) cArtist = Regex.Unescape(mAr.Groups[1].Value);

                bool isSame;
                double tScore = ScoreCandidateTitle(targetSongFold, NormalizeFold(cTrack), out isSame);
                double aBonus = 0.0;
                string cArtCompact = NormalizeFold(cArtist).Replace(" ", "");
                if (!string.IsNullOrEmpty(artistCompact) && !string.IsNullOrEmpty(cArtCompact))
                {
                    if (cArtCompact.Contains(artistCompact) || artistCompact.Contains(cArtCompact))
                    {
                        aBonus = 220.0;
                    }
                }

                Match mSyn = Regex.Match(chunk, "\"syncedLyrics\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
                if (mSyn.Success && !string.IsNullOrEmpty(mSyn.Groups[1].Value))
                {
                    string unesc = Regex.Unescape(mSyn.Groups[1].Value);
                    double fSec, lSec;
                    bool streamMaster;
                    MeasureLrcTagBounds(unesc, out fSec, out lSec, out streamMaster);
                    if (lSec > 8.0)
                    {
                        if (isSame && streamMaster && _durationSec > 35.0 && Math.Abs(cDur - _durationSec) <= 3.5)
                        {
                            hasStreamingAtCurrentDur = true;
                        }
                        if (isSame && cDur > 35.0 && cDur < minSameSongAudioDur)
                        {
                            minSameSongAudioDur = cDur;
                            minSameSongFirstSec = fSec;
                        }
                        cands.Add(new LrcCandidate
                        {
                            Index = i,
                            Dur = cDur,
                            FirstSec = fSec,
                            LastSec = lSec,
                            IsSameSong = isSame,
                            IsStreamingMaster = streamMaster,
                            TitleScore = tScore,
                            ArtistBonus = aBonus,
                            RawSynced = unesc
                        });
                    }
                }

                Match mPl = Regex.Match(chunk, "\"plainLyrics\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
                if (mPl.Success && !string.IsNullOrEmpty(mPl.Groups[1].Value))
                {
                    double pSc = tScore + aBonus - i * 1.5;
                    if (isSame && pSc > bestPlainScore)
                    {
                        bestPlainScore = pSc;
                        bestPlain = Regex.Unescape(mPl.Groups[1].Value);
                    }
                }
            }

            LrcCandidate best = null;
            double bestScore = -1e9;
            bool bestAlreadyShifted = false;

            for (int i = 0; i < cands.Count; i++)
            {
                LrcCandidate c = cands[i];
                if (!c.IsSameSong) continue;

                double sc = c.TitleScore + c.ArtistBonus + (100.0 - c.Index * 1.5);
                bool candShifted = false;

                if (_durationSec > 35.0 && c.Dur > 35.0)
                {
                    double diff = Math.Abs(c.Dur - _durationSec);
                    double excessOverMin = (minSameSongAudioDur < 1e8) ? (_durationSec - minSameSongAudioDur) : 0.0;

                    if (diff <= 4.5)
                    {
                        if (c.IsStreamingMaster || excessOverMin < 5.0)
                        {
                            sc += 340.0 - diff * 15.0;
                        }
                        else if ((c.FirstSec - minSameSongFirstSec) >= Math.Max(4.0, excessOverMin * 0.42))
                        {
                            candShifted = true;
                            sc += 480.0 - diff * 18.0;
                        }
                        else
                        {
                            sc += 25.0 - diff * 3.0;
                        }
                    }
                    else if (minSameSongAudioDur < 1e8 && Math.Abs(c.Dur - minSameSongAudioDur) <= 2.5)
                    {
                        sc += 95.0;
                    }
                }

                if (c.RawSynced.IndexOf("<", StringComparison.Ordinal) >= 0)
                {
                    sc += 35.0;
                }

                if (sc > bestScore)
                {
                    bestScore = sc;
                    best = c;
                    bestAlreadyShifted = candShifted;
                }
            }

            if (best != null)
            {
                ParseLrc(best.RawSynced);
                if (_syncedLyrics.Count > 0)
                {
                    lock (_lock)
                    {
                        LyricsStatus = "Synced";
                        _lrcFirstTagSec = best.FirstSec;
                        _lrcLastTagSec = best.LastSec;
                        _lrcAlreadyMvShifted = bestAlreadyShifted;
                        _lrcIsStreamingMaster = best.IsStreamingMaster || hasStreamingAtCurrentDur;
                        _lrcAudioMasterDurSec = (minSameSongAudioDur < 1e8) ? minSameSongAudioDur : best.Dur;
                        RecalculateAutoMvOffsetLocked();
                    }
                    UpdateActiveLyricLine();
                    return true;
                }
            }

            if (!string.IsNullOrEmpty(bestPlain))
            {
                string[] rawLines = bestPlain.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                List<string> cleanLines = new List<string>();
                for (int i = 0; i < rawLines.Length; i++)
                {
                    string cl = SanitizeLyricText(rawLines[i]);
                    if (!string.IsNullOrEmpty(cl)) cleanLines.Add(cl);
                }
                if (cleanLines.Count > 0)
                {
                    double dur = _durationSec > 25.0 ? _durationSec : (cleanLines.Count * 4.0 + 14.0);
                    double introPad = Math.Min(16.0, dur * 0.10);
                    double stepSec = Math.Max(2.5, (dur - introPad - 6.0) / Math.Max(1, cleanLines.Count));
                    lock (_lock)
                    {
                        _syncedLyrics.Clear();
                        double t = introPad;
                        for (int i = 0; i < cleanLines.Count; i++)
                        {
                            _syncedLyrics.Add(new LyricLine { TimeSec = t, Text = cleanLines[i] });
                            t += stepSec;
                        }
                        FinalizeLyricTimings(_syncedLyrics, dur);
                        LyricsStatus = "Plain";
                        _autoMvOffsetSec = 0.0;
                    }
                    UpdateActiveLyricLine();
                    return true;
                }
            }

            return false;
        }

        private bool TryQueryLrclibSingle(string rawQuery, string targetSongFold, string targetArtistFold)
        {
            if (string.IsNullOrEmpty(rawQuery)) return false;
            string url = "https://lrclib.net/api/search?q=" + Uri.EscapeDataString(rawQuery.Trim());
            string json = FetchUrlStringWithRetry(url);
            return TryProcessLrclibJson(json, targetSongFold, targetArtistFold);
        }

        private void FetchLyricsFromLrclib(string track, string artist)
        {
            lock (_lock)
            {
                _syncedLyrics.Clear();
                CurrentLyricLine = "Loading lyrics...";
                NextLyricLine = "";
                LyricsStatus = "Loading lyrics...";
            }

            try
            {
                string songCore;
                string artistCore;
                bool isMv;
                ExtractSongAndArtist(track, artist, out songCore, out artistCore, out isMv);
                lock (_lock)
                {
                    _isLikelyMvTrack = isMv;
                }

                string songFold = NormalizeFold(songCore);
                string artistFold = NormalizeFold(artistCore);

                string q1 = (songCore + " " + artistCore).Trim();
                if (!string.IsNullOrEmpty(q1) && TryQueryLrclibSingle(q1, songFold, artistFold)) return;

                if (!string.IsNullOrEmpty(songCore) && !string.Equals(q1, songCore, StringComparison.OrdinalIgnoreCase))
                {
                    if (TryQueryLrclibSingle(songCore, songFold, artistFold)) return;
                }

                if (!string.IsNullOrEmpty(songCore) && !string.IsNullOrEmpty(artistCore))
                {
                    string getUrl = "https://lrclib.net/api/get?track_name=" + Uri.EscapeDataString(songCore) +
                                    "&artist_name=" + Uri.EscapeDataString(artistCore);
                    string getJson = FetchUrlStringWithRetry(getUrl);
                    if (!string.IsNullOrEmpty(getJson) && TryProcessLrclibJson("[" + getJson + "]", songFold, artistFold))
                    {
                        return;
                    }
                }

                lock (_lock)
                {
                    CurrentLyricLine = "Lyrics not available";
                    NextLyricLine = "";
                    LyricsStatus = "None";
                }
            }
            catch
            {
                lock (_lock)
                {
                    CurrentLyricLine = "Lyrics not available";
                    NextLyricLine = "";
                    LyricsStatus = "Offline";
                }
            }
        }

        private static void BuildProsodyWords(LyricLine ln)
        {
            string txt = ln.Text ?? "";
            MatchCollection mc = Regex.Matches(txt, @"\S+");
            if (mc.Count == 0)
            {
                ln.Words = new LyricWordSpan[0];
                return;
            }

            double rawSpan = Math.Max(0.65, ln.EndSec - ln.TimeSec);
            double punctExtra = 0.0;
            double[] weights = new double[mc.Count];
            double totalWeight = 0.0;

            for (int i = 0; i < mc.Count; i++)
            {
                string tok = mc[i].Value;
                int coreLen = 0;
                for (int c = 0; c < tok.Length; c++)
                {
                    if (char.IsLetterOrDigit(tok[c])) coreLen++;
                }
                double w = 0.68 + 0.10 * Math.Min(7, Math.Max(1, coreLen));
                char lastCh = tok[tok.Length - 1];
                if (lastCh == ',' || lastCh == ';' || lastCh == ':' || lastCh == '-' || lastCh == '\u2013' || lastCh == '\u2014')
                {
                    w += 0.45;
                    punctExtra += 0.30;
                }
                else if (lastCh == '.' || lastCh == '!' || lastCh == '?' || lastCh == '\u2026')
                {
                    w += 0.60;
                    punctExtra += 0.42;
                }
                if (i == mc.Count - 1) w *= 1.26;
                weights[i] = w;
                totalWeight += w;
            }

            double maxNatural = mc.Count * 0.72 + punctExtra + 0.85;
            double vocalSpan = (rawSpan <= maxNatural)
                ? (rawSpan * 0.92)
                : Math.Min(rawSpan * 0.92, mc.Count * 0.54 + punctExtra + 0.55);
            vocalSpan = Math.Max(0.55, vocalSpan);

            LyricWordSpan[] spans = new LyricWordSpan[mc.Count];
            double curT = ln.TimeSec;
            for (int i = 0; i < mc.Count; i++)
            {
                double durW = vocalSpan * (weights[i] / Math.Max(0.01, totalWeight));
                spans[i] = new LyricWordSpan
                {
                    CharStart = mc[i].Index,
                    CharLength = mc[i].Length,
                    StartSec = curT,
                    EndSec = curT + durW
                };
                curT += durW;
            }
            ln.Words = spans;
        }

        private static void FinalizeLyricTimings(List<LyricLine> list, double songDurSec)
        {
            if (list == null || list.Count == 0) return;
            list.Sort(delegate(LyricLine a, LyricLine b) { return a.TimeSec.CompareTo(b.TimeSec); });

            for (int i = 0; i < list.Count; i++)
            {
                double nextT = (i + 1 < list.Count)
                    ? list[i + 1].TimeSec
                    : Math.Max(list[i].TimeSec + 4.2, songDurSec > list[i].TimeSec ? songDurSec : list[i].TimeSec + 4.2);

                if (list[i].EndSec <= list[i].TimeSec + 0.15 || list[i].EndSec > nextT)
                {
                    list[i].EndSec = nextT;
                }

                if (list[i].Words != null && list[i].Words.Length > 0)
                {
                    int last = list[i].Words.Length - 1;
                    if (list[i].Words[last].EndSec <= list[i].Words[last].StartSec)
                    {
                        list[i].Words[last].EndSec = Math.Min(list[i].EndSec, list[i].Words[last].StartSec + 0.45);
                    }
                }
                else
                {
                    BuildProsodyWords(list[i]);
                }
            }
        }

        private static void TryAttachPipeWordSpans(string pipeLine, LyricLine targetLine)
        {
            if (targetLine == null || string.IsNullOrEmpty(pipeLine) || string.IsNullOrEmpty(targetLine.Text)) return;
            string inner = pipeLine.Trim();
            if (!inner.StartsWith("<", StringComparison.Ordinal) || !inner.EndsWith(">", StringComparison.Ordinal)) return;
            inner = inner.Substring(1, inner.Length - 2);
            string[] parts = inner.Split(new char[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return;

            List<LyricWordSpan> spans = new List<LyricWordSpan>();
            string lineTxt = targetLine.Text;
            int searchPos = 0;

            for (int i = 0; i < parts.Length; i++)
            {
                Match pm = Regex.Match(parts[i], @"^(.*):(\d+(?:\.\d+)?):(\d+(?:\.\d+)?)$");
                if (!pm.Success) return;
                string word = SanitizeLyricText(pm.Groups[1].Value);
                if (string.IsNullOrEmpty(word)) continue;
                double sSec, eSec;
                if (!double.TryParse(pm.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out sSec) ||
                    !double.TryParse(pm.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out eSec))
                {
                    return;
                }
                int idx = lineTxt.IndexOf(word, searchPos, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return;
                spans.Add(new LyricWordSpan
                {
                    CharStart = idx,
                    CharLength = word.Length,
                    StartSec = sSec,
                    EndSec = Math.Max(sSec + 0.04, eSec)
                });
                searchPos = idx + word.Length;
            }

            if (spans.Count >= 2)
            {
                targetLine.Words = spans.ToArray();
                targetLine.EndSec = Math.Max(targetLine.EndSec, spans[spans.Count - 1].EndSec);
            }
        }

        private void ParseLrc(string lrcText)
        {
            List<LyricLine> list = new List<LyricLine>();
            string[] lines = lrcText.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            Regex rxLine = new Regex(@"^\[(\d+):(\d+(?:\.\d+)?)\](.*)$");
            Regex rxWordTag = new Regex(@"<(\d+):(\d+(?:\.\d+)?)>([^<]*)");

            foreach (string rawLine in lines)
            {
                string trimmed = rawLine.Trim();
                if (trimmed.StartsWith("<", StringComparison.Ordinal) && trimmed.EndsWith(">", StringComparison.Ordinal) && trimmed.IndexOf('|') > 0)
                {
                    if (list.Count > 0)
                    {
                        TryAttachPipeWordSpans(trimmed, list[list.Count - 1]);
                    }
                    continue;
                }

                Match m = rxLine.Match(trimmed);
                if (!m.Success) continue;

                int mins = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                double secs = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                double tSec = mins * 60.0 + secs;
                string body = SanitizeSpeakerTags(m.Groups[3].Value).Trim();

                if (string.IsNullOrEmpty(body) || body == "\u266A" || body == "...")
                {
                    // Blank timestamp line marks exact vocal phrase end of previous line!
                    if (list.Count > 0 && list[list.Count - 1].EndSec <= 0.0 && tSec > list[list.Count - 1].TimeSec + 0.2)
                    {
                        list[list.Count - 1].EndSec = tSec;
                    }
                    continue;
                }

                MatchCollection wm = rxWordTag.Matches(body);
                if (wm.Count >= 2)
                {
                    StringBuilder sbClean = new StringBuilder();
                    List<LyricWordSpan> wList = new List<LyricWordSpan>();
                    for (int w = 0; w < wm.Count; w++)
                    {
                        int wMin = int.Parse(wm[w].Groups[1].Value, CultureInfo.InvariantCulture);
                        double wSec = double.Parse(wm[w].Groups[2].Value, CultureInfo.InvariantCulture);
                        double wt = wMin * 60.0 + wSec;
                        string seg = SanitizeSpeakerTags(wm[w].Groups[3].Value);
                        if (string.IsNullOrEmpty(seg)) continue;

                        int startIdx = sbClean.Length;
                        sbClean.Append(seg);
                        string trimmedSeg = seg.TrimEnd();
                        int coreChars = Math.Max(1, trimmedSeg.Length);
                        wList.Add(new LyricWordSpan
                        {
                            CharStart = startIdx,
                            CharLength = coreChars,
                            StartSec = wt,
                            EndSec = wt + 0.35
                        });
                    }
                    for (int w = 0; w + 1 < wList.Count; w++)
                    {
                        LyricWordSpan cur = wList[w];
                        cur.EndSec = Math.Max(cur.StartSec + 0.04, wList[w + 1].StartSec);
                        wList[w] = cur;
                    }
                    string cleanTxt = SanitizeLyricText(sbClean.ToString());
                    if (!string.IsNullOrEmpty(cleanTxt))
                    {
                        list.Add(new LyricLine
                        {
                            TimeSec = tSec,
                            Text = cleanTxt,
                            Words = wList.ToArray()
                        });
                    }
                }
                else
                {
                    string cleanBody = SanitizeLyricText(body);
                    if (!string.IsNullOrEmpty(cleanBody) && cleanBody != "\u266A" && cleanBody != "...")
                    {
                        list.Add(new LyricLine { TimeSec = tSec, Text = cleanBody });
                    }
                }
            }

            FinalizeLyricTimings(list, _durationSec);
            lock (_lock)
            {
                _syncedLyrics = list;
            }
        }

        private void UpdateActiveLyricLine()
        {
            double effPos = GetEffectiveLyricsPosSec();
            lock (_lock)
            {
                if (_syncedLyrics.Count == 0) return;
                if (effPos < _syncedLyrics[0].TimeSec)
                {
                    CurrentLyricLine = _syncedLyrics[0].Text;
                    NextLyricLine = (_syncedLyrics.Count > 1) ? _syncedLyrics[1].Text : "";
                    return;
                }
                int idx = 0;
                for (int i = 0; i < _syncedLyrics.Count; i++)
                {
                    if (_syncedLyrics[i].TimeSec <= effPos)
                        idx = i;
                    else
                        break;
                }
                CurrentLyricLine = _syncedLyrics[idx].Text;
                NextLyricLine = (idx + 1 < _syncedLyrics.Count) ? _syncedLyrics[idx + 1].Text : "";
            }
        }

        public void Dispose()
        {
            _running = false;
            lock (_lock)
            {
                if (_albumArt != null)
                {
                    try { _albumArt.Dispose(); } catch { }
                    _albumArt = null;
                }
            }
        }
    }
}
