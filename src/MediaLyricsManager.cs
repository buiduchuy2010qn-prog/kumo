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
        private double _autoMvOffsetSec = 0.0;
        private double _userOffsetSec = 0.0;
        private readonly Dictionary<string, double> _perSongUserOffsets = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private bool _sawNearZeroStart = false;
        private bool _onsetCalibratedOffset = false;
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
            get { return _autoMvOffsetSec + _userOffsetSec; }
        }

        public double GetEffectiveLyricsPosSec()
        {
            double raw = GetLivePositionSec();
            if (_silentIntroHold && raw < 35.0)
            {
                return -1.0;
            }
            return raw - (_autoMvOffsetSec + _userOffsetSec);
        }

        public void AdjustUserLyricsOffset(double deltaSec)
        {
            lock (_lock)
            {
                _userOffsetSec = Math.Max(-90.0, Math.Min(120.0, Math.Round(_userOffsetSec + deltaSec, 1)));
                if (!string.IsNullOrEmpty(_lastTrackKey))
                {
                    _perSongUserOffsets[_lastTrackKey] = _userOffsetSec;
                }
            }
        }

        public void ResetUserLyricsOffset()
        {
            lock (_lock)
            {
                _userOffsetSec = 0.0;
                _silentIntroHold = false;
                if (!string.IsNullOrEmpty(_lastTrackKey))
                {
                    _perSongUserOffsets.Remove(_lastTrackKey);
                }
            }
        }

        public void SyncFirstLineToNow()
        {
            double raw = GetLivePositionSec();
            lock (_lock)
            {
                if (_syncedLyrics.Count == 0) return;
                _silentIntroHold = false;
                double firstT = _syncedLyrics[0].TimeSec;
                if (raw <= firstT + 0.4)
                {
                    _userOffsetSec = 0.0;
                    if (!string.IsNullOrEmpty(_lastTrackKey))
                    {
                        _perSongUserOffsets.Remove(_lastTrackKey);
                    }
                    return;
                }
                _userOffsetSec = Math.Max(0.0, Math.Min(150.0, Math.Round((raw - firstT) - _autoMvOffsetSec, 2)));
                if (!string.IsNullOrEmpty(_lastTrackKey))
                {
                    _perSongUserOffsets[_lastTrackKey] = _userOffsetSec;
                }
            }
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
                _onsetCalibratedOffset = false;
            }

            float pk = ReadAudioPeak();
            if (pk < 0f)
            {
                _silentIntroHold = false;
                return;
            }

            if (pk >= 0.058f)
            {
                _sustainedMusicTicks = Math.Min(30, _sustainedMusicTicks + 1);
            }
            else if (pk < 0.024f)
            {
                _sustainedMusicTicks = Math.Max(0, _sustainedMusicTicks - 1);
            }

            if (raw < 35.0 && _sustainedMusicTicks < 3 && pk < 0.022f)
            {
                _silentIntroHold = true;
            }
            else if (_sustainedMusicTicks >= 5)
            {
                _silentIntroHold = false;
                lock (_lock)
                {
                    if (_sawNearZeroStart && !_onsetCalibratedOffset && _autoMvOffsetSec > 0.5 && _durationSec > 45.0 && _lrcAudioMasterDurSec > 30.0)
                    {
                        double excess = _durationSec - _lrcAudioMasterDurSec;
                        if (raw >= 4.0 && raw <= excess + 2.5)
                        {
                            _autoMvOffsetSec = Math.Round(Math.Max(0.0, raw - 0.5), 1);
                            _onsetCalibratedOffset = true;
                        }
                    }
                }
            }
            else if (raw >= 35.0)
            {
                _silentIntroHold = false;
            }
        }

        private void RecalculateAutoMvOffsetLocked()
        {
            if (_syncedLyrics.Count == 0 || !string.Equals(LyricsStatus, "Synced", StringComparison.Ordinal))
            {
                _autoMvOffsetSec = 0.0;
                return;
            }
            if (_durationSec > 45.0 && _lrcAudioMasterDurSec > 30.0)
            {
                double excess = _durationSec - _lrcAudioMasterDurSec;
                bool startsEarly = (_lrcFirstTagSec < 10.0);
                bool endsEarly = ((_durationSec - _lrcLastTagSec) > Math.Max(20.0, excess * 0.68));
                if (excess >= 6.0 && excess <= 95.0 && startsEarly && endsEarly)
                {
                    if (!_onsetCalibratedOffset)
                    {
                        _autoMvOffsetSec = Math.Round(excess * 0.72, 1);
                    }
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
                        _autoMvOffsetSec = 0.0;
                        double savedOff;
                        _userOffsetSec = _perSongUserOffsets.TryGetValue(trackKey, out savedOff) ? savedOff : 0.0;
                        _sawNearZeroStart = (GetLivePositionSec() <= 3.0);
                        _onsetCalibratedOffset = false;
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

        private static void MeasureLrcTagBounds(string rawLrc, out double firstNonEmptySec, out double lastTagSec)
        {
            firstNonEmptySec = -1.0;
            lastTagSec = 0.0;
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
                    string txt = m.Groups[3].Value.Trim();
                    if (firstNonEmptySec < 0.0 && !string.IsNullOrEmpty(txt))
                    {
                        firstNonEmptySec = t;
                    }
                }
            }
            if (firstNonEmptySec < 0.0) firstNonEmptySec = 0.0;
        }

        private bool TryQueryLrclibSingle(string rawQuery)
        {
            if (string.IsNullOrEmpty(rawQuery)) return false;
            string url = "https://lrclib.net/api/search?q=" + Uri.EscapeDataString(rawQuery.Trim());
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "Kumo/1.1 (+https://github.com/buiduchuy2010qn-prog/kumo)";
            req.Timeout = 5000;

            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                string json = sr.ReadToEnd();
                MatchCollection objs = Regex.Matches(json, @"\{\s*""id""\s*:[\s\S]*?(?=\},\s*\{\s*""id""|\}\s*\]\s*$)");

                string bestSynced = null;
                double bestScore = -1e9;
                double bestFirstSec = 0.0;
                double bestLastSec = 0.0;
                double bestCandDur = 0.0;
                double minCanonicalAudioDur = 1e9;
                string firstPlain = null;

                for (int i = 0; i < objs.Count; i++)
                {
                    string chunk = objs[i].Value;
                    double cDur = 0.0;
                    Match mDur = Regex.Match(chunk, @"""duration""\s*:\s*([0-9]+(?:\.[0-9]+)?)");
                    if (mDur.Success)
                    {
                        double.TryParse(mDur.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out cDur);
                    }

                    Match mSyn = Regex.Match(chunk, "\"syncedLyrics\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
                    if (mSyn.Success && !string.IsNullOrEmpty(mSyn.Groups[1].Value))
                    {
                        string unesc = Regex.Unescape(mSyn.Groups[1].Value);
                        double fSec, lSec;
                        MeasureLrcTagBounds(unesc, out fSec, out lSec);
                        if (lSec > 5.0)
                        {
                            if (cDur > 30.0 && cDur < minCanonicalAudioDur)
                            {
                                minCanonicalAudioDur = cDur;
                            }
                            double score = 100.0 - i * 1.5;
                            if (_durationSec > 30.0 && cDur > 30.0)
                            {
                                double diff = Math.Abs(cDur - _durationSec);
                                bool genuinelyAlignedToVideo = (fSec >= 10.0) || ((_durationSec - lSec) <= 22.0);
                                if (diff <= 4.5 && genuinelyAlignedToVideo)
                                {
                                    score += 500.0 - diff * 20.0;
                                }
                                else if (diff <= 4.5)
                                {
                                    score += 40.0 - diff * 4.0;
                                }
                            }
                            if (score > bestScore)
                            {
                                bestScore = score;
                                bestSynced = unesc;
                                bestFirstSec = fSec;
                                bestLastSec = lSec;
                                bestCandDur = cDur;
                            }
                        }
                    }

                    if (firstPlain == null)
                    {
                        Match mPl = Regex.Match(chunk, "\"plainLyrics\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
                        if (mPl.Success && !string.IsNullOrEmpty(mPl.Groups[1].Value))
                        {
                            firstPlain = Regex.Unescape(mPl.Groups[1].Value);
                        }
                    }
                }

                if (!string.IsNullOrEmpty(bestSynced))
                {
                    ParseLrc(bestSynced);
                    if (_syncedLyrics.Count > 0)
                    {
                        lock (_lock)
                        {
                            LyricsStatus = "Synced";
                            _lrcFirstTagSec = bestFirstSec;
                            _lrcLastTagSec = bestLastSec;
                            _lrcAudioMasterDurSec = (minCanonicalAudioDur < 1e8) ? minCanonicalAudioDur : bestCandDur;
                            RecalculateAutoMvOffsetLocked();
                        }
                        UpdateActiveLyricLine();
                        return true;
                    }
                }

                if (!string.IsNullOrEmpty(firstPlain))
                {
                    string[] lines = firstPlain.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    double dur = _durationSec > 25.0 ? _durationSec : (lines.Length * 4.0 + 14.0);
                    double introPad = Math.Min(16.0, dur * 0.10);
                    double stepSec = Math.Max(2.5, (dur - introPad - 6.0) / Math.Max(1, lines.Length));
                    lock (_lock)
                    {
                        _syncedLyrics.Clear();
                        double t = introPad;
                        foreach (string ln in lines)
                        {
                            _syncedLyrics.Add(new LyricLine { TimeSec = t, Text = ln.Trim() });
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
                string cleanTrack = Regex.Replace(track ?? "", @"(\(.*?\)|\[.*?\]|\|.*$|#.*$|official\s*music\s*video|official\s*lyric\s*video|official\s*video|lyric\s*video|visualizer|4k|\bmv\b|\blyrics\b|\baudio\b|ft\..*$)", "", RegexOptions.IgnoreCase).Trim();
                cleanTrack = Regex.Replace(cleanTrack, @"\s{2,}", " ").Trim(' ', '-', '\u2013', '\u2014');
                string cleanArtist = Regex.Replace(artist ?? "", @"(\s*-\s*Topic$|\bOfficial\b|\bChannel\b|\bMusic\b|\bVEVO\b)", "", RegexOptions.IgnoreCase).Trim();

                string q1 = (cleanArtist + " " + cleanTrack).Trim();
                if (TryQueryLrclibSingle(q1)) return;
                if (!string.IsNullOrEmpty(cleanTrack) && !string.Equals(q1, cleanTrack, StringComparison.OrdinalIgnoreCase))
                {
                    if (TryQueryLrclibSingle(cleanTrack)) return;
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

        private void ParseLrc(string lrcText)
        {
            List<LyricLine> list = new List<LyricLine>();
            string[] lines = lrcText.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            Regex rxLine = new Regex(@"^\[(\d+):(\d+(?:\.\d+)?)\](.*)$");
            Regex rxWordTag = new Regex(@"<(\d+):(\d+(?:\.\d+)?)>([^<]*)");

            foreach (string rawLine in lines)
            {
                Match m = rxLine.Match(rawLine.Trim());
                if (!m.Success) continue;

                int mins = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                double secs = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                double tSec = mins * 60.0 + secs;
                string body = m.Groups[3].Value.Trim();

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
                        string seg = wm[w].Groups[3].Value;
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
                    string cleanTxt = sbClean.ToString().Trim();
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
                    string cleanBody = Regex.Replace(body, @"<\d+:\d+(?:\.\d+)?>", "").Trim();
                    if (!string.IsNullOrEmpty(cleanBody))
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
