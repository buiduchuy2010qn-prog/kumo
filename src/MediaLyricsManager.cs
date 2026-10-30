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
        public bool IsNonMusicMedia { get; private set; }

        public string CurrentLyricLine { get; private set; }
        public string NextLyricLine { get; private set; }
        public string LyricsStatus { get; private set; }
        public double UserLyricsOffsetSec { get; set; }
        public string CurrentAudioDeviceName { get; set; }

        private double _basePositionSec = 0.0;
        private double _durationSec = 0.0;
        private DateTime _positionSampledUtc = DateTime.UtcNow;

        private Bitmap _albumArt = null;
        private Color _dominantColor = Color.FromArgb(44, 110, 215);

        private GlobalSystemMediaTransportControlsSessionManager _smtcManager = null;
        private GlobalSystemMediaTransportControlsSession _currentSession = null;

        private List<LyricLine> _syncedLyrics = new List<LyricLine>();
        private string _lastTrackKey = "";
        private string _lastLyricsQuery = "";
        private DateTime _trackChangedUtc = DateTime.MinValue;
        private ulong _lastThumbStreamSize = 0;
        private volatile bool _hasRealSmtcThumb = false;

        // MV vs Audio-Single auto offset + SponsorBlock exact intro + Live WASAPI peak gate
        private IMediaAudioMeterInformation _audioMeter = null;
        private DateTime _meterRetryAfterUtc = DateTime.MinValue;
        private double _lrcAudioMasterDurSec = 0.0;
        private double _lrcFirstTagSec = 0.0;
        private double _lrcLastTagSec = 0.0;
        private bool _lrcAlreadyMvShifted = false;
        private bool _lrcIsStreamingMaster = false;
        private bool _isLikelyMvTrack = false;
        private double _sbIntroOffsetSec = -1.0;
        private bool _sbZeroIntroConfirmed = false;
        private struct YtWordToken
        {
            public string Word;
            public double TimeSec;
        }

        private struct RawLrcSeed
        {
            public double TimeSec;
            public double EndSec;
            public string Text;
            public LyricWordSpan[] Words;
        }

        private struct LineAnchor
        {
            public int LineIdx;
            public double LocalOffset;
            public double VocalEndSec;
        }

        private List<RawLrcSeed> _baseLrcSeeds = new List<RawLrcSeed>();
        private List<YtWordToken> _ytAsrWords = null;
        private bool _ytAsrAligned = false;
        private string _lastMatchedJson = null;
        private string _lastMatchedSongFold = "";
        private string _lastMatchedArtistFold = "";
        private string _lastRawVersionHint = "";
        private double _lastEvaluatedDurSec = 0.0;
        private List<LyricLine> _ytCaptionCues = null;
        private bool _ytCaptionIsManual = false;
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

            try
            {
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
                ServicePointManager.Expect100Continue = false;
                if (ServicePointManager.DefaultConnectionLimit < 16)
                {
                    ServicePointManager.DefaultConnectionLimit = 16;
                }
            }
            catch { }

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
            return raw - _autoMvOffsetSec - GetAudioPipelineLatencySec() - UserLyricsOffsetSec;
        }

        public double GetLineSeekTargetSec(LyricLine line)
        {
            if (line == null) return 0.0;
            return Math.Max(0.0, line.TimeSec + _autoMvOffsetSec);
        }

        public double GetAudioPipelineLatencySec()
        {
            bool browser = IsLikelyBrowserApp(SourceAppId);
            double lat = browser ? 0.16 : 0.11;

            string dev = CurrentAudioDeviceName ?? "";
            if (!string.IsNullOrEmpty(dev))
            {
                if (dev.IndexOf("FxSound", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    dev.IndexOf("Voicemeeter", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    dev.IndexOf("Sonar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    dev.IndexOf("Nahimic", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    dev.IndexOf("Dolby", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    dev.IndexOf("Boom", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    dev.IndexOf("Equalizer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    dev.IndexOf("Virtual", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    lat += 0.22;
                }
                else if (dev.IndexOf("Bluetooth", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         dev.IndexOf("AirPods", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         dev.IndexOf("Buds", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         dev.IndexOf("FreeBuds", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         dev.IndexOf("Hands-Free", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         dev.IndexOf("WH-", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         dev.IndexOf("WF-", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         dev.IndexOf("BT-", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         dev.IndexOf("Soundcore", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         dev.IndexOf("Marshall", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         dev.IndexOf("JBL", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         dev.IndexOf("Bose", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         dev.IndexOf("Beats", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    lat += 0.24;
                }
            }

            if (!_ytAsrAligned && !_lrcIsStreamingMaster)
            {
                lat += 0.16;
            }
            return lat;
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

        private static string SplitVnOnset(string w, out int homophoneGroup)
        {
            homophoneGroup = 0;
            if (string.IsNullOrEmpty(w)) return "";
            if (w.StartsWith("ngh", StringComparison.Ordinal)) { homophoneGroup = 4; return w.Substring(3); }
            if (w.StartsWith("ng", StringComparison.Ordinal)) { homophoneGroup = 4; return w.Substring(2); }
            if (w.StartsWith("ch", StringComparison.Ordinal) || w.StartsWith("tr", StringComparison.Ordinal)) { homophoneGroup = 5; return w.Substring(2); }
            if (w.StartsWith("gi", StringComparison.Ordinal)) { homophoneGroup = 2; return w.Substring(2); }
            char c0 = w[0];
            if (c0 == 's' || c0 == 'x') { homophoneGroup = 1; return w.Substring(1); }
            if (c0 == 'd' || c0 == 'r' || c0 == 'v') { homophoneGroup = 2; return w.Substring(1); }
            if (c0 == 'c' || c0 == 'k' || c0 == 'q') { homophoneGroup = 3; return w.Substring(1); }
            return "";
        }

        private static bool EditDistanceAtMostOne(string a, string b)
        {
            int la = a.Length;
            int lb = b.Length;
            if (Math.Abs(la - lb) > 1) return false;
            if (la == lb)
            {
                int diff = 0;
                for (int i = 0; i < la; i++)
                {
                    if (a[i] != b[i] && ++diff > 1) return false;
                }
                return true;
            }
            string s = (la < lb) ? a : b;
            string l = (la < lb) ? b : a;
            int si = 0, li = 0, edits = 0;
            while (si < s.Length && li < l.Length)
            {
                if (s[si] == l[li]) { si++; li++; }
                else { if (++edits > 1) return false; li++; }
            }
            return true;
        }

        private static bool WordsRoughlyMatch(string w1, string w2)
        {
            if (string.IsNullOrEmpty(w1) || string.IsNullOrEmpty(w2)) return false;
            if (string.Equals(w1, w2, StringComparison.Ordinal)) return true;
            if (w2.Length == w1.Length + 1 && w2.StartsWith(w1, StringComparison.Ordinal) && w2[w2.Length - 1] == w2[w2.Length - 2]) return true;
            if (w1.Length == w2.Length + 1 && w1.StartsWith(w2, StringComparison.Ordinal) && w1[w1.Length - 1] == w1[w1.Length - 2]) return true;
            if (w1.Length >= 3 && w2.Length >= 3)
            {
                int g1, g2;
                string r1 = SplitVnOnset(w1, out g1);
                string r2 = SplitVnOnset(w2, out g2);
                if (g1 > 0 && g1 == g2 && r1.Length >= 2 && string.Equals(r1, r2, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            if (w1.Length >= 4 && w2.Length >= 4 && EditDistanceAtMostOne(w1, w2))
            {
                return true;
            }
            return false;
        }

        private void RebuildSyncedFromSeedsLocked(double[] warpedTimes, double[] vocalEndsAbs)
        {
            if (_baseLrcSeeds == null || _baseLrcSeeds.Count == 0) return;
            List<LyricLine> rebuilt = new List<LyricLine>(_baseLrcSeeds.Count);
            for (int i = 0; i < _baseLrcSeeds.Count; i++)
            {
                RawLrcSeed seed = _baseLrcSeeds[i];
                double newT = (warpedTimes != null && i < warpedTimes.Length) ? warpedTimes[i] : seed.TimeSec;
                if (i > 0 && newT < rebuilt[i - 1].TimeSec + 0.20)
                {
                    newT = rebuilt[i - 1].TimeSec + 0.20;
                }
                double dtShift = newT - seed.TimeSec;

                LyricWordSpan[] clonedWords = null;
                double newEnd = 0.0;
                if (seed.Words != null && seed.Words.Length > 0)
                {
                    clonedWords = new LyricWordSpan[seed.Words.Length];
                    for (int w = 0; w < seed.Words.Length; w++)
                    {
                        clonedWords[w] = new LyricWordSpan
                        {
                            CharStart = seed.Words[w].CharStart,
                            CharLength = seed.Words[w].CharLength,
                            StartSec = seed.Words[w].StartSec + dtShift,
                            EndSec = seed.Words[w].EndSec + dtShift
                        };
                    }
                    if (seed.EndSec > seed.TimeSec + 0.15)
                    {
                        newEnd = seed.EndSec + dtShift;
                    }
                }
                else
                {
                    if (vocalEndsAbs != null && i < vocalEndsAbs.Length && vocalEndsAbs[i] > 0.0)
                    {
                        double endRel = vocalEndsAbs[i] - _autoMvOffsetSec;
                        if (endRel > newT + 0.45) newEnd = endRel;
                    }
                    else if (seed.EndSec > seed.TimeSec + 0.15)
                    {
                        newEnd = seed.EndSec + dtShift;
                    }
                }

                rebuilt.Add(new LyricLine
                {
                    TimeSec = newT,
                    EndSec = newEnd,
                    Text = seed.Text,
                    Words = clonedWords
                });
            }

            double effSongDur = (_durationSec > 5.0) ? Math.Max(10.0, _durationSec - _autoMvOffsetSec) : _durationSec;
            FinalizeLyricTimings(rebuilt, effSongDur);
            _syncedLyrics = rebuilt;
        }

        private bool TryMatchLineInAsrWindow(string[] wL, int startAsr, int endAsr, double minTime, double maxTime, int minHits, double minRatio, int maxFirstSkip,
            out double onsetSec, out double vocalEndSec, out int firstMatchedAsr, out double matchScore)
        {
            onsetSec = 0.0;
            vocalEndSec = 0.0;
            firstMatchedAsr = -1;
            matchScore = 0.0;

            int mCheck = Math.Min(8, wL.Length);
            if (mCheck < 2 || _ytAsrWords == null || _ytAsrWords.Count < 4) return false;

            int sIdx = Math.Max(0, startAsr);
            int eIdx = Math.Min(_ytAsrWords.Count - 1, endAsr);
            int bestHits = 0;
            double bestSc = 0.0;
            double bestOnset = 0.0;
            double bestVEnd = 0.0;
            int bestFirstAsr = -1;
            int distinctTies = 0;

            for (int k = sIdx; k <= eIdx; k++)
            {
                double kt = _ytAsrWords[k].TimeSec;
                if (kt < minTime) continue;
                if (kt > maxTime) break;

                // Require _ytAsrWords[k] itself to match one of the leading words [0 .. maxFirstSkip] so windows don't duplicate
                int startW = -1;
                for (int sw = 0; sw <= Math.Min(maxFirstSkip, mCheck - 1); sw++)
                {
                    if (WordsRoughlyMatch(wL[sw], _ytAsrWords[k].Word))
                    {
                        startW = sw;
                        break;
                    }
                }
                if (startW < 0) continue;

                int hits = 1;
                int lastAsr = k;
                int curAsr = k + 1;
                int maxWinAsr = Math.Min(_ytAsrWords.Count - 1, k + mCheck + 2);

                for (int w = startW + 1; w < mCheck && curAsr <= maxWinAsr; w++)
                {
                    int lookLim = Math.Min(maxWinAsr, curAsr + 2);
                    for (int a = curAsr; a <= lookLim; a++)
                    {
                        if (_ytAsrWords[a].TimeSec - _ytAsrWords[lastAsr].TimeSec > 2.8) break;
                        if (WordsRoughlyMatch(wL[w], _ytAsrWords[a].Word))
                        {
                            hits++;
                            lastAsr = a;
                            curAsr = a + 1;
                            break;
                        }
                    }
                }

                double ratio = (double)hits / (double)mCheck;
                if (hits < minHits || ratio < minRatio) continue;

                double candOnset = kt;
                if (startW == 1)
                {
                    if (k >= 1)
                    {
                        double prevGap = kt - _ytAsrWords[k - 1].TimeSec;
                        candOnset = (prevGap >= 0.12 && prevGap <= 0.85) ? _ytAsrWords[k - 1].TimeSec : (kt - 0.36);
                    }
                    else
                    {
                        candOnset = Math.Max(0.0, kt - 0.36);
                    }
                }
                else if (startW >= 2)
                {
                    candOnset = Math.Max(0.0, kt - 0.35 * startW);
                }

                double sc = ratio + (startW == 0 ? 0.08 : 0.0) + hits * 0.015;
                if (sc > bestSc + 0.02)
                {
                    bestSc = sc;
                    bestHits = hits;
                    bestOnset = candOnset;
                    bestVEnd = _ytAsrWords[lastAsr].TimeSec + 0.38;
                    bestFirstAsr = k;
                    distinctTies = 1;
                }
                else if (Math.Abs(sc - bestSc) <= 0.02 && Math.Abs(candOnset - bestOnset) > 3.5)
                {
                    distinctTies++;
                }
            }

            if (bestHits >= minHits && distinctTies == 1)
            {
                onsetSec = bestOnset;
                vocalEndSec = bestVEnd;
                firstMatchedAsr = bestFirstAsr;
                matchScore = bestSc;
                return true;
            }
            return false;
        }

        private bool TryAlignToYtAsrWordsLocked()
        {
            if (_baseLrcSeeds == null || _baseLrcSeeds.Count < 4 || _ytAsrWords == null || _ytAsrWords.Count < 10)
            {
                return false;
            }

            int lineCount = _baseLrcSeeds.Count;
            string[][] lineWords = new string[lineCount][];
            Dictionary<string, int> freq = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < lineCount; i++)
            {
                string f = NormalizeFold(_baseLrcSeeds[i].Text);
                lineWords[i] = string.IsNullOrEmpty(f)
                    ? new string[0]
                    : f.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (!string.IsNullOrEmpty(f))
                {
                    int c;
                    freq[f] = freq.TryGetValue(f, out c) ? (c + 1) : 1;
                }
            }

            // Pass 1: Collect high-confidence unique line matches across the song, then find the Longest Physically-Valid Monotonic Chain (DP)!
            // Handles constant MV intro shifts, Remix speed-ups, AND Piano/Live/Acoustic covers with slower tempo + mid-song solo interludes!
            List<LineAnchor> rawSeeds = new List<LineAnchor>();
            List<int> rawSeedAsr = new List<int>();
            for (int i = 0; i < lineCount; i++)
            {
                string[] wL = lineWords[i];
                if (wL.Length < 3) continue;
                string fKey = string.Join(" ", wL);
                int fCnt;
                if (freq.TryGetValue(fKey, out fCnt) && fCnt > 2) continue;

                double baseT = _baseLrcSeeds[i].TimeSec;
                double onset, vEnd, sc;
                int fAsr;
                int reqHits = (wL.Length >= 4) ? 3 : 2;
                double reqRatio = (wL.Length >= 4) ? 0.60 : 0.66;
                if (TryMatchLineInAsrWindow(wL, 0, _ytAsrWords.Count - 1, Math.Max(0.0, baseT - 20.0), baseT + 180.0, reqHits, reqRatio, 1,
                    out onset, out vEnd, out fAsr, out sc))
                {
                    rawSeeds.Add(new LineAnchor
                    {
                        LineIdx = i,
                        LocalOffset = onset - baseT,
                        VocalEndSec = vEnd
                    });
                    rawSeedAsr.Add(fAsr);
                }
            }

            if (rawSeeds.Count < 3) return false;

            int mCount = rawSeeds.Count;
            int[] dpLen = new int[mCount];
            double[] dpCost = new double[mCount];
            int[] dpPrev = new int[mCount];
            int bestEnd = -1;
            int bestChainLen = 0;
            double bestChainCost = 1e9;

            for (int b = 0; b < mCount; b++)
            {
                dpLen[b] = 1;
                dpCost[b] = 0.0;
                dpPrev[b] = -1;
                double onsetB = _baseLrcSeeds[rawSeeds[b].LineIdx].TimeSec + rawSeeds[b].LocalOffset;
                double baseB = _baseLrcSeeds[rawSeeds[b].LineIdx].TimeSec;

                for (int a = 0; a < b; a++)
                {
                    if (rawSeeds[b].LineIdx <= rawSeeds[a].LineIdx) continue;
                    if (rawSeedAsr[b] <= rawSeedAsr[a]) continue;
                    double onsetA = _baseLrcSeeds[rawSeeds[a].LineIdx].TimeSec + rawSeeds[a].LocalOffset;
                    double baseA = _baseLrcSeeds[rawSeeds[a].LineIdx].TimeSec;
                    double dBase = baseB - baseA;
                    double dActual = onsetB - onsetA;
                    if (dActual < 0.35) continue;

                    // Allow 0.70x..1.45x tempo stretch plus up to +26s instrumental solo interlude between verses
                    if (dActual < dBase * 0.68 - 1.6 || dActual > dBase * 1.45 + 26.0) continue;

                    double drift = Math.Abs(dActual - dBase);
                    int candLen = dpLen[a] + 1;
                    double candCost = dpCost[a] + drift;
                    if (candLen > dpLen[b] || (candLen == dpLen[b] && candCost < dpCost[b]))
                    {
                        dpLen[b] = candLen;
                        dpCost[b] = candCost;
                        dpPrev[b] = a;
                    }
                }

                if (dpLen[b] > bestChainLen || (dpLen[b] == bestChainLen && dpCost[b] < bestChainCost))
                {
                    bestChainLen = dpLen[b];
                    bestChainCost = dpCost[b];
                    bestEnd = b;
                }
            }

            if (bestChainLen < 3 || bestEnd < 0) return false;

            List<LineAnchor> chain = new List<LineAnchor>(bestChainLen);
            for (int cur = bestEnd; cur >= 0; cur = dpPrev[cur])
            {
                chain.Add(rawSeeds[cur]);
            }
            chain.Reverse();

            List<double> chainOffs = new List<double>(chain.Count);
            for (int c = 0; c < chain.Count; c++) chainOffs.Add(chain[c].LocalOffset);
            chainOffs.Sort();
            double medOffset = chainOffs[chainOffs.Count / 2];

            // Pass 2: Guided monotonic local line-anchoring using piecewise expected time from the verified chain!
            List<LineAnchor> anchors = new List<LineAnchor>();
            int minAsrIdx = 0;
            double lastAnchorAbsOnset = -999.0;
            int chPtr = 0;

            for (int i = 0; i < lineCount; i++)
            {
                string[] wL = lineWords[i];
                if (wL.Length < 2) continue;

                double baseT = _baseLrcSeeds[i].TimeSec;
                double expectedOff;
                if (i <= chain[0].LineIdx)
                {
                    expectedOff = chain[0].LocalOffset;
                }
                else if (i >= chain[chain.Count - 1].LineIdx)
                {
                    expectedOff = chain[chain.Count - 1].LocalOffset;
                }
                else
                {
                    while (chPtr + 1 < chain.Count && chain[chPtr + 1].LineIdx < i) chPtr++;
                    LineAnchor c0 = chain[chPtr];
                    LineAnchor c1 = chain[Math.Min(chain.Count - 1, chPtr + 1)];
                    double bt0 = _baseLrcSeeds[c0.LineIdx].TimeSec;
                    double bt1 = _baseLrcSeeds[c1.LineIdx].TimeSec;
                    double u = (bt1 > bt0 + 0.05) ? ((baseT - bt0) / (bt1 - bt0)) : 0.5;
                    u = Math.Max(0.0, Math.Min(1.0, u));
                    expectedOff = c0.LocalOffset + (c1.LocalOffset - c0.LocalOffset) * u;
                }

                double expectedAbs = baseT + expectedOff;
                int reqHits = (wL.Length == 2) ? 2 : ((wL.Length == 3) ? 2 : 3);
                double reqRatio = (wL.Length == 2) ? 1.0 : ((wL.Length == 3) ? 0.66 : 0.58);
                int maxSkip = (wL.Length == 2) ? 0 : 1;

                double onset, vEnd, sc;
                int fAsr;
                if (TryMatchLineInAsrWindow(wL, minAsrIdx, _ytAsrWords.Count - 1, Math.Max(0.0, expectedAbs - 4.5), expectedAbs + 4.8,
                    reqHits, reqRatio, maxSkip, out onset, out vEnd, out fAsr, out sc))
                {
                    if (onset > lastAnchorAbsOnset + 0.30)
                    {
                        anchors.Add(new LineAnchor
                        {
                            LineIdx = i,
                            LocalOffset = onset - baseT,
                            VocalEndSec = vEnd
                        });
                        lastAnchorAbsOnset = onset;
                        minAsrIdx = fAsr + 1;
                    }
                }
            }

            if (anchors.Count < chain.Count)
            {
                anchors = chain;
            }

            // Store exact video timestamps directly in warpedTimes with _autoMvOffsetSec = 0.0
            // so earlier verses in multi-tempo covers never go negative relative to a later verse's median!
            _autoMvOffsetSec = 0.0;
            double[] warpedTimes = new double[lineCount];
            double[] vocalEnds = new double[lineCount];

            if (anchors.Count >= 2)
            {
                for (int a = 0; a < anchors.Count; a++)
                {
                    vocalEnds[anchors[a].LineIdx] = anchors[a].VocalEndSec;
                }

                int curAnc = 0;
                for (int i = 0; i < lineCount; i++)
                {
                    double interpOff;
                    if (i <= anchors[0].LineIdx)
                    {
                        interpOff = anchors[0].LocalOffset;
                    }
                    else if (i >= anchors[anchors.Count - 1].LineIdx)
                    {
                        interpOff = anchors[anchors.Count - 1].LocalOffset;
                    }
                    else
                    {
                        while (curAnc + 1 < anchors.Count && anchors[curAnc + 1].LineIdx < i)
                        {
                            curAnc++;
                        }
                        LineAnchor a0 = anchors[curAnc];
                        LineAnchor a1 = anchors[Math.Min(anchors.Count - 1, curAnc + 1)];
                        double t0 = _baseLrcSeeds[a0.LineIdx].TimeSec;
                        double t1 = _baseLrcSeeds[a1.LineIdx].TimeSec;
                        double u = (t1 > t0 + 0.05) ? ((_baseLrcSeeds[i].TimeSec - t0) / (t1 - t0)) : 0.5;
                        u = Math.Max(0.0, Math.Min(1.0, u));
                        interpOff = a0.LocalOffset + (a1.LocalOffset - a0.LocalOffset) * u;
                    }
                    warpedTimes[i] = Math.Max(0.0, _baseLrcSeeds[i].TimeSec + interpOff);
                }
            }
            else
            {
                for (int i = 0; i < lineCount; i++)
                {
                    warpedTimes[i] = Math.Max(0.0, _baseLrcSeeds[i].TimeSec + medOffset);
                }
            }

            RebuildSyncedFromSeedsLocked(warpedTimes, vocalEnds);
            _ytAsrAligned = true;
            return true;
        }

        private bool TryComputeYtCaptionOffsetLocked(out double alignedOffset)
        {
            alignedOffset = 0.0;
            if (_baseLrcSeeds == null || _baseLrcSeeds.Count < 4 || _ytCaptionCues == null || _ytCaptionCues.Count < 4)
            {
                return false;
            }

            Dictionary<string, int> freq = new Dictionary<string, int>(StringComparer.Ordinal);
            string[] lrcFolds = new string[_baseLrcSeeds.Count];
            for (int i = 0; i < _baseLrcSeeds.Count; i++)
            {
                string f = NormalizeFold(_baseLrcSeeds[i].Text);
                lrcFolds[i] = f;
                if (!string.IsNullOrEmpty(f))
                {
                    int cur;
                    freq[f] = freq.TryGetValue(f, out cur) ? (cur + 1) : 1;
                }
            }

            string[] cueFolds = new string[_ytCaptionCues.Count];
            for (int j = 0; j < _ytCaptionCues.Count; j++)
            {
                cueFolds[j] = NormalizeFold(_ytCaptionCues[j].Text);
            }

            List<double> deltas = new List<double>();
            for (int i = 0; i < _baseLrcSeeds.Count; i++)
            {
                string fL = lrcFolds[i];
                if (string.IsNullOrEmpty(fL) || fL.Length < 10) continue;
                int countL;
                if (freq.TryGetValue(fL, out countL) && countL > 2) continue;

                string[] wL = fL.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (wL.Length < 3) continue;

                double bestRatio = 0.0;
                double bestDelta = 0.0;
                int tieCount = 0;

                for (int j = 0; j < _ytCaptionCues.Count; j++)
                {
                    double dt = _ytCaptionCues[j].TimeSec - _baseLrcSeeds[i].TimeSec;
                    if (dt < -12.0 || dt > 180.0) continue;

                    string fC = cueFolds[j];
                    if (string.IsNullOrEmpty(fC) || fC.Length < 6) continue;

                    int hit = 0;
                    for (int w = 0; w < wL.Length; w++)
                    {
                        if (wL[w].Length >= 2 && (" " + fC + " ").IndexOf(" " + wL[w] + " ", StringComparison.Ordinal) >= 0)
                        {
                            hit++;
                        }
                    }

                    double ratio = (double)hit / (double)wL.Length;
                    if (hit >= 3 && ratio >= 0.65)
                    {
                        if (ratio > bestRatio + 0.01)
                        {
                            bestRatio = ratio;
                            bestDelta = dt;
                            tieCount = 1;
                        }
                        else if (Math.Abs(ratio - bestRatio) <= 0.01 && Math.Abs(dt - bestDelta) > 4.0)
                        {
                            tieCount++;
                        }
                    }
                }

                if (bestRatio >= 0.65 && tieCount == 1)
                {
                    deltas.Add(bestDelta);
                }
            }

            if (deltas.Count < 3) return false;
            deltas.Sort();

            int bestClusterStart = -1;
            int bestClusterLen = 0;
            for (int a = 0; a < deltas.Count; a++)
            {
                int b = a;
                while (b + 1 < deltas.Count && (deltas[b + 1] - deltas[a]) <= 3.2)
                {
                    b++;
                }
                int len = b - a + 1;
                if (len > bestClusterLen)
                {
                    bestClusterLen = len;
                    bestClusterStart = a;
                }
            }

            if (bestClusterLen < 3 || bestClusterStart < 0) return false;
            double med = deltas[bestClusterStart + (bestClusterLen / 2)];
            // Manual MV subtitle cards often fade in ~0.4-1.3s BEFORE singing: never pull same-duration LRCLIB earlier!
            bool sameMasterDur = (_durationSec > 35.0 && _lrcAudioMasterDurSec > 35.0 && Math.Abs(_durationSec - _lrcAudioMasterDurSec) <= 3.8);
            if (sameMasterDur && med < 0.0 && med >= -1.8)
            {
                alignedOffset = 0.0;
                return true;
            }
            alignedOffset = (Math.Abs(med) <= 0.45) ? 0.0 : Math.Round(med, 2);
            return true;
        }

        private bool TryApplyManualYtFallbackLyricsLocked()
        {
            if (_syncedLyrics.Count > 0 || !_ytCaptionIsManual || _ytCaptionCues == null || _ytCaptionCues.Count < 6)
            {
                return false;
            }
            _baseLrcSeeds.Clear();
            for (int i = 0; i < _ytCaptionCues.Count; i++)
            {
                string txt = SanitizeLyricText(_ytCaptionCues[i].Text);
                if (!string.IsNullOrEmpty(txt))
                {
                    _baseLrcSeeds.Add(new RawLrcSeed
                    {
                        TimeSec = _ytCaptionCues[i].TimeSec,
                        EndSec = 0.0,
                        Text = txt,
                        Words = null
                    });
                }
            }
            if (_baseLrcSeeds.Count < 4)
            {
                _baseLrcSeeds.Clear();
                return false;
            }
            _autoMvOffsetSec = 0.0;
            _ytAsrAligned = false;
            if (!TryAlignToYtAsrWordsLocked())
            {
                RebuildSyncedFromSeedsLocked(null, null);
            }
            LyricsStatus = "Synced";
            CurrentLyricLine = _syncedLyrics[0].Text;
            NextLyricLine = (_syncedLyrics.Count > 1) ? _syncedLyrics[1].Text : "";
            return true;
        }

        private void RecalculateAutoMvOffsetLocked()
        {
            if (_baseLrcSeeds == null || _baseLrcSeeds.Count == 0 ||
                !string.Equals(LyricsStatus, "Synced", StringComparison.Ordinal))
            {
                _autoMvOffsetSec = 0.0;
                _ytAsrAligned = false;
                return;
            }

            // 1. Highest precision: Acoustic ASR word-stream forced alignment (millisecond <s t="..."> tags + per-line cover/rubato tracking!)
            if (TryAlignToYtAsrWordsLocked())
            {
                return;
            }

            _ytAsrAligned = false;

            // 2. Manual YouTube caption line alignment (for MVs that disable ASR, e.g. official lyric subtitles)
            double ytAligned;
            if (TryComputeYtCaptionOffsetLocked(out ytAligned))
            {
                _autoMvOffsetSec = ytAligned;
                RebuildSyncedFromSeedsLocked(null, null);
                return;
            }
            if (_lrcAlreadyMvShifted || _lrcIsStreamingMaster)
            {
                _autoMvOffsetSec = 0.0;
                RebuildSyncedFromSeedsLocked(null, null);
                return;
            }
            if (_sbIntroOffsetSec > 2.5)
            {
                _autoMvOffsetSec = _sbIntroOffsetSec;
                RebuildSyncedFromSeedsLocked(null, null);
                return;
            }
            if (_sbZeroIntroConfirmed)
            {
                _autoMvOffsetSec = 0.0;
                RebuildSyncedFromSeedsLocked(null, null);
                return;
            }
            if (_durationSec > 45.0 && _lrcAudioMasterDurSec > 35.0)
            {
                double excess = _durationSec - _lrcAudioMasterDurSec;
                bool allowMvShift = _isLikelyMvTrack ||
                    (_lrcFirstTagSec < 18.0 && (_durationSec - _lrcLastTagSec) > Math.Max(18.0, excess * 0.65));
                if (allowMvShift && excess >= 5.0 && excess <= 120.0 && _lrcLastTagSec <= _lrcAudioMasterDurSec + 3.0)
                {
                    _autoMvOffsetSec = (excess <= 14.5)
                        ? Math.Round(excess * 0.85, 1)
                        : Math.Round(excess * 0.75, 1);
                    RebuildSyncedFromSeedsLocked(null, null);
                    return;
                }
            }
            _autoMvOffsetSec = 0.0;
            RebuildSyncedFromSeedsLocked(null, null);
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

                string appIdNow = chosen.SourceAppUserModelId ?? "";
                string rawSmtcArtist = artist;

                // Clean up "(123)" notification prefixes and trailing "- YouTube" tab suffix if present
                title = Regex.Replace(title, @"^\(\d+\)\s*", "").Trim();
                if (title.Length > 10 && Regex.IsMatch(title, @"\s+-\s+YouTube(\s+Music)?$", RegexOptions.IgnoreCase))
                {
                    title = Regex.Replace(title, @"\s+-\s+YouTube(\s+Music)?$", "", RegexOptions.IgnoreCase).Trim();
                }

                string rawTitleBeforeSplit = title;
                if (string.IsNullOrEmpty(artist) && !IsSocialOrGenericTitle(rawTitleBeforeSplit, rawSmtcArtist, appIdNow))
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
                                if (age > 0.0 && age < 36000.0) posSec += age;
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

                SourceAppId = appIdNow;
                CurrentTitle = title;
                CurrentArtist = artist;
                IsPlaying = playingNow;
                HasMedia = true;

                bool nowNonMusic = IsNonMusicOrSocialClip(rawTitleBeforeSplit, rawSmtcArtist, title, artist, SourceAppId, _durationSec);
                bool wasNonMusic = IsNonMusicMedia;
                IsNonMusicMedia = nowNonMusic;

                string trackKey = artist + "|" + title;
                bool trackChanged = !string.Equals(trackKey, _lastTrackKey, StringComparison.Ordinal);
                _lastTrackKey = trackKey;
                if (trackChanged)
                {
                    _trackChangedUtc = DateTime.UtcNow;
                    _lastThumbStreamSize = 0;
                    _hasRealSmtcThumb = false;
                    lock (_lock)
                    {
                        if (_albumArt != null) { try { _albumArt.Dispose(); } catch { } _albumArt = null; }
                        _dominantColor = Color.FromArgb(44, 110, 215);
                        _syncedLyrics.Clear();
                        _baseLrcSeeds.Clear();
                        _lrcAudioMasterDurSec = 0.0;
                        _lrcFirstTagSec = 0.0;
                        _lrcLastTagSec = 0.0;
                        _lrcAlreadyMvShifted = false;
                        _lrcIsStreamingMaster = false;
                        _isLikelyMvTrack = false;
                        _sbIntroOffsetSec = -1.0;
                        _sbZeroIntroConfirmed = false;
                        _ytAsrWords = null;
                        _ytAsrAligned = false;
                        _ytCaptionCues = null;
                        _ytCaptionIsManual = false;
                        _autoMvOffsetSec = 0.0;
                        _lastMatchedJson = null;
                        _lastMatchedSongFold = "";
                        _lastMatchedArtistFold = "";
                        _lastRawVersionHint = "";
                        _lastEvaluatedDurSec = 0.0;
                        _sawNearZeroStart = (GetLivePositionSec() <= 3.0);
                        _sustainedMusicTicks = 0;
                        _silentIntroHold = false;
                        if (nowNonMusic)
                        {
                            CurrentLyricLine = "";
                            NextLyricLine = "";
                            LyricsStatus = "Skipped";
                        }
                    }

                    if (!nowNonMusic)
                    {
                        string capKey = trackKey;
                        string capTitle = title;
                        string capArtist = artist;
                        ThreadPool.QueueUserWorkItem(delegate
                        {
                            ResolveYoutubeEnrichment(capKey, capTitle, capArtist);
                        });
                    }
                }
                else if (nowNonMusic && !wasNonMusic)
                {
                    lock (_lock)
                    {
                        _syncedLyrics.Clear();
                        _baseLrcSeeds.Clear();
                        _ytAsrWords = null;
                        _ytCaptionCues = null;
                        _lastMatchedJson = null;
                        CurrentLyricLine = "";
                        NextLyricLine = "";
                        LyricsStatus = "Skipped";
                        if (!_hasRealSmtcThumb && _albumArt != null)
                        {
                            try { _albumArt.Dispose(); } catch { }
                            _albumArt = null;
                            _dominantColor = Color.FromArgb(44, 110, 215);
                        }
                    }
                }
                else if (!nowNonMusic && wasNonMusic)
                {
                    _lastLyricsQuery = "";
                    lock (_lock)
                    {
                        _sawNearZeroStart = (GetLivePositionSec() <= 3.0);
                        CurrentLyricLine = "Loading lyrics...";
                        LyricsStatus = "Loading lyrics...";
                    }
                    string capKey = trackKey;
                    string capTitle = title;
                    string capArtist = artist;
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        ResolveYoutubeEnrichment(capKey, capTitle, capArtist);
                    });
                }
                else if (!nowNonMusic)
                {
                    string reEvalJson = null;
                    string reEvalSong = null;
                    string reEvalArtist = null;
                    lock (_lock)
                    {
                        if (!string.IsNullOrEmpty(_lastMatchedJson) && _durationSec > 35.0 && Math.Abs(_durationSec - _lastEvaluatedDurSec) > 2.5)
                        {
                            reEvalJson = _lastMatchedJson;
                            reEvalSong = _lastMatchedSongFold;
                            reEvalArtist = _lastMatchedArtistFold;
                        }
                    }
                    if (reEvalJson != null)
                    {
                        if (!TryProcessLrclibJson(reEvalJson, reEvalSong, reEvalArtist))
                        {
                            lock (_lock)
                            {
                                _syncedLyrics.Clear();
                                _baseLrcSeeds.Clear();
                                _lastMatchedJson = null;
                                CurrentLyricLine = "Lyrics not available";
                                NextLyricLine = "";
                                LyricsStatus = "None";
                            }
                        }
                    }
                    else
                    {
                        lock (_lock)
                        {
                            RecalculateAutoMvOffsetLocked();
                        }
                    }
                }

                // Load / Refresh Album Art Thumbnail:
                // - Rejects transparent-corner browser fallback icons (Chrome/Edge/Brave wheel!)
                // - Re-polls ras.Size during the first 18s after track change so delayed YouTube artwork always replaces early placeholders!
                bool shouldCheckThumb = (props.Thumbnail != null) &&
                                        (!_hasRealSmtcThumb || (DateTime.UtcNow - _trackChangedUtc).TotalSeconds <= 18.0);
                if (shouldCheckThumb)
                {
                    ulong seenSize;
                    Bitmap decoded = ReadSquareThumbnail(props.Thumbnail, SourceAppId, _lastThumbStreamSize, _hasRealSmtcThumb, out seenSize);
                    if (decoded != null)
                    {
                        Color dom = ExtractVibrantColor(decoded);
                        lock (_lock)
                        {
                            if (_albumArt != null) { try { _albumArt.Dispose(); } catch { } }
                            _albumArt = decoded;
                            _dominantColor = dom;
                        }
                        _lastThumbStreamSize = seenSize;
                        _hasRealSmtcThumb = true;
                    }
                }

                // Fetch Synced Lyrics from LRCLIB when song changes (strictly skipped for voice notes / social clips)
                if (!nowNonMusic)
                {
                    string queryKey = (artist + " " + title).Trim();
                    if (!string.IsNullOrEmpty(queryKey) && !string.Equals(queryKey, _lastLyricsQuery, StringComparison.OrdinalIgnoreCase))
                    {
                        _lastLyricsQuery = queryKey;
                        string capTk = trackKey;
                        string capTi = title;
                        string capAr = artist;
                        ThreadPool.QueueUserWorkItem(delegate
                        {
                            FetchLyricsFromLrclib(capTk, capTi, capAr);
                        });
                    }
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
            IsNonMusicMedia = false;
            CurrentTitle = "";
            CurrentArtist = "";
            SourceAppId = "";
            _basePositionSec = 0.0;
            _durationSec = 0.0;
            _lastTrackKey = "";
            _lastLyricsQuery = "";
            _lastThumbStreamSize = 0;
            _hasRealSmtcThumb = false;
            lock (_lock)
            {
                if (_albumArt != null)
                {
                    try { _albumArt.Dispose(); } catch { }
                    _albumArt = null;
                }
            }
        }

        public string GetFallbackArtistLabel(bool vi)
        {
            if (IsNonMusicMedia)
            {
                if (_durationSec > 900.0)
                    return vi ? "Video d\u00E0i \u00B7 Tuy\u1EC3n t\u1EADp" : "Long video \u00B7 Mix";
                if (_durationSec > 0.5 && _durationSec < 45.0)
                    return vi ? "Tin nh\u1EAFn tho\u1EA1i" : "Voice message";
                string appLbl = GetCleanSourceAppLabel(SourceAppId);
                if (!string.IsNullOrEmpty(appLbl))
                    return appLbl;
                return vi ? "\u00C2m thanh trang web" : "Web / Chat audio";
            }
            string cleanApp = GetCleanSourceAppLabel(SourceAppId);
            if (!string.IsNullOrEmpty(cleanApp)) return cleanApp;
            return vi ? "Tr\u00ECnh ph\u00E1t nh\u1EA1c" : "Media Player";
        }

        private static string GetCleanSourceAppLabel(string appId)
        {
            if (string.IsNullOrEmpty(appId)) return "";
            if (appId.IndexOf("chrome", StringComparison.OrdinalIgnoreCase) >= 0) return "Google Chrome";
            if (appId.IndexOf("msedge", StringComparison.OrdinalIgnoreCase) >= 0) return "Microsoft Edge";
            if (appId.IndexOf("coccoc", StringComparison.OrdinalIgnoreCase) >= 0) return "C\u1ED1c C\u1ED1c";
            if (appId.IndexOf("brave", StringComparison.OrdinalIgnoreCase) >= 0) return "Brave";
            if (appId.IndexOf("firefox", StringComparison.OrdinalIgnoreCase) >= 0) return "Firefox";
            if (appId.IndexOf("opera", StringComparison.OrdinalIgnoreCase) >= 0) return "Opera";
            if (appId.IndexOf("vivaldi", StringComparison.OrdinalIgnoreCase) >= 0) return "Vivaldi";
            if (appId.IndexOf("spotify", StringComparison.OrdinalIgnoreCase) >= 0) return "Spotify";
            if (appId.IndexOf("zalo", StringComparison.OrdinalIgnoreCase) >= 0) return "Zalo";
            if (appId.IndexOf("telegram", StringComparison.OrdinalIgnoreCase) >= 0) return "Telegram";
            if (appId.IndexOf("discord", StringComparison.OrdinalIgnoreCase) >= 0) return "Discord";
            if (appId.IndexOf("messenger", StringComparison.OrdinalIgnoreCase) >= 0) return "Messenger";
            if (appId.IndexOf("whatsapp", StringComparison.OrdinalIgnoreCase) >= 0) return "WhatsApp";
            if (appId.IndexOf("vlc", StringComparison.OrdinalIgnoreCase) >= 0) return "VLC";
            return "";
        }

        private static readonly HashSet<string> _genericNonSongTitles = new HashSet<string>(StringComparer.Ordinal)
        {
            "facebook", "fb", "messenger", "meta", "meta ai", "instagram", "threads",
            "zalo", "zalo web", "zalo pc", "telegram", "telegram web", "discord",
            "whatsapp", "viber", "wechat", "line", "signal", "locket", "snapchat",
            "tiktok", "douyin", "twitter", "x", "reddit", "pinterest", "linkedin", "twitch",
            "youtube", "watch", "facebook watch", "reels", "reel", "thuoc phim",
            "stories", "story", "shorts", "trang chu", "home", "new tab", "the moi",
            "chatgpt", "openai", "gemini", "google gemini", "hoi gemini", "claude", "grok", "copilot", "perplexity",
            "google", "google dich", "google translate", "google meet", "zoom", "microsoft teams", "teams",
            "canva", "capcut", "shopee", "lazada", "tiki", "aternos", "roblox", "garena",
            "tin nhan", "am thanh", "audio", "video", "media", "untitled", "dang tai", "loading"
        };

        private static bool IsChatOrCallApp(string appId)
        {
            if (string.IsNullOrEmpty(appId) || IsLikelyBrowserApp(appId)) return false;
            return appId.IndexOf("zalo", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   appId.IndexOf("telegram", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   appId.IndexOf("discord", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   appId.IndexOf("whatsapp", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   appId.IndexOf("messenger", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   appId.IndexOf("viber", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   appId.IndexOf("skype", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   appId.IndexOf("teams", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   appId.IndexOf("zoom", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   appId.IndexOf("slack", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   appId.IndexOf("signal", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   appId.IndexOf("wechat", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   appId.IndexOf("line", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsSocialOrGenericTitle(string rawTitle, string rawSmtcArtist, string appId)
        {
            string t = (rawTitle ?? "").Trim();
            if (string.IsNullOrEmpty(t)) return true;
            if (t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("blob:", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string tFold = NormalizeFold(t);
            if (string.IsNullOrEmpty(tFold)) return true;

            if (tFold == "voice" || tFold == "voice message" || tFold == "audio message" ||
                tFold == "tin nhan thoai" || tFold == "ghi am" || tFold == "ban ghi am" ||
                tFold == "nhac nen" || tFold == "am thanh goc" || tFold == "original sound" || tFold == "original audio" ||
                tFold.StartsWith("voice message ", StringComparison.Ordinal) ||
                tFold.StartsWith("audio message ", StringComparison.Ordinal) ||
                tFold.StartsWith("tin nhan thoai ", StringComparison.Ordinal) ||
                tFold.StartsWith("nhac nen ", StringComparison.Ordinal) ||
                tFold.StartsWith("am thanh goc ", StringComparison.Ordinal) ||
                tFold.StartsWith("original sound ", StringComparison.Ordinal) ||
                tFold.StartsWith("original audio ", StringComparison.Ordinal))
            {
                return true;
            }

            if (Regex.IsMatch(t, @"^(audioclip|voice[_ -]?msg|voice[_ -]?message|recording[_ -]?\d|rec[_ -]?\d|vid[_ -]?\d{4}|img[_ -]?\d{4}|pxl[_ -]?\d{4}|dji[_ -]?\d{4})", RegexOptions.IgnoreCase))
            {
                return true;
            }

            string aFold = NormalizeFold(rawSmtcArtist);
            if (string.IsNullOrEmpty(aFold) || string.Equals(aFold, tFold, StringComparison.Ordinal))
            {
                if (_genericNonSongTitles.Contains(tFold)) return true;

                if (Regex.IsMatch(t, @"\.(ogg|opus|amr|3gp|webm)$", RegexOptions.IgnoreCase)) return true;
                if (IsLikelyBrowserApp(appId) && t.IndexOf(" - ", StringComparison.Ordinal) < 0 &&
                    Regex.IsMatch(t, @"\.(mp4|m4a|wav|mp3|mov)$", RegexOptions.IgnoreCase))
                {
                    return true;
                }

                if (Regex.IsMatch(t, @"(\|\s*(Facebook|Messenger|Instagram|Threads|TikTok|Zalo|Discord|Telegram|WhatsApp|Reddit|X|Twitter|LinkedIn|Twitch)\b|[-\u2013\u2014\u2022]\s*(Facebook|Messenger|Instagram|Threads|TikTok|Zalo|Discord|Telegram|WhatsApp)\s*$|^(Facebook|Messenger|Zalo|Telegram|Discord|Instagram|TikTok|WhatsApp)\s*[-\u2013\u2014|:]|\bon Instagram:)", RegexOptions.IgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool IsNonMusicOrSocialClip(string rawTitleBeforeSplit, string rawSmtcArtist, string resolvedTitle, string resolvedArtist, string appId, double durSec)
        {
            if (IsChatOrCallApp(appId)) return true;
            if (durSec > 0.5 && durSec < 25.0) return true;
            if (durSec > 0.5 && durSec < 45.0 && string.IsNullOrEmpty(resolvedArtist)) return true;
            if (durSec > 900.0) return true;

            if (IsSocialOrGenericTitle(rawTitleBeforeSplit, rawSmtcArtist, appId)) return true;
            if (string.IsNullOrEmpty(resolvedArtist) && IsSocialOrGenericTitle(resolvedTitle, "", appId)) return true;

            if (IsLikelyBrowserApp(appId) && string.IsNullOrEmpty(resolvedArtist))
            {
                string tFold = NormalizeFold(resolvedTitle);
                if (string.IsNullOrEmpty(tFold) || !tFold.Contains(" "))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsLikelyBrowserApp(string appId)
        {
            if (string.IsNullOrEmpty(appId)) return true;
            return appId.IndexOf("chrome", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   appId.IndexOf("msedge", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   appId.IndexOf("brave", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   appId.IndexOf("coccoc", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   appId.IndexOf("firefox", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   appId.IndexOf("opera", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   appId.IndexOf("vivaldi", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsTransparentCornerAppIcon(Bitmap raw, string sourceAppId)
        {
            int w = raw.Width;
            int h = raw.Height;
            if (w < 16 || h < 16) return true;
            if (!IsLikelyBrowserApp(sourceAppId) && w > 256) return false;

            int transparentCorners = 0;
            int m = Math.Max(1, Math.Min(4, w / 32));
            if (raw.GetPixel(m, m).A < 215) transparentCorners++;
            if (raw.GetPixel(w - 1 - m, m).A < 215) transparentCorners++;
            if (raw.GetPixel(m, h - 1 - m).A < 215) transparentCorners++;
            if (raw.GetPixel(w - 1 - m, h - 1 - m).A < 215) transparentCorners++;
            return transparentCorners >= 3;
        }

        private static Bitmap CropBitmapToSquareCover(Bitmap raw)
        {
            int w = raw.Width;
            int h = raw.Height;
            if (w <= 0 || h <= 0) return null;

            int side = Math.Min(w, h);
            if (w > h && h >= 160)
            {
                Color topPx = raw.GetPixel(w / 2, Math.Max(1, h / 14));
                Color botPx = raw.GetPixel(w / 2, Math.Min(h - 2, h - h / 14));
                if (topPx.R < 20 && topPx.G < 20 && topPx.B < 20 && botPx.R < 20 && botPx.G < 20 && botPx.B < 20)
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

        private static Bitmap ReadSquareThumbnail(
            IRandomAccessStreamReference thumbRef,
            string sourceAppId,
            ulong prevSize,
            bool alreadyHasRealThumb,
            out ulong streamSize)
        {
            streamSize = 0;
            IRandomAccessStreamWithContentType ras = null;
            try
            {
                ras = WaitWinRT(thumbRef.OpenReadAsync(), 2000);
                if (ras == null || ras.Size == 0) return null;
                streamSize = ras.Size;
                if (alreadyHasRealThumb && streamSize == prevSize) return null;

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

                    if (ms.Length < 64) return null;
                    ms.Position = 0;

                    using (Bitmap raw = new Bitmap(ms))
                    {
                        if (IsTransparentCornerAppIcon(raw, sourceAppId))
                        {
                            return null;
                        }
                        return CropBitmapToSquareCover(raw);
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

        private void ResolveYoutubeEnrichment(string trackKey, string rawTitle, string rawArtist)
        {
            try
            {
                // Brief pause so _durationSec from SMTC timeline is populated if it arrived slightly after title
                Thread.Sleep(250);
                if (!string.Equals(_lastTrackKey, trackKey, StringComparison.Ordinal)) return;
                if (IsNonMusicMedia) return;
                if (_durationSec > 0.5 && (_durationSec < 28.0 || _durationSec > 900.0)) return;

                bool browserPlay = IsLikelyBrowserApp(SourceAppId);
                if (browserPlay && string.IsNullOrEmpty(rawArtist)) return;
                if (!browserPlay && _hasRealSmtcThumb) return;

                string searchQ = ((rawTitle ?? "") + " " + (rawArtist ?? "")).Trim();
                if (string.IsNullOrEmpty(searchQ)) return;

                string ytUrl = "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(searchQ) + "&hl=en";
                string html = null;
                for (int att = 0; att < 2; att++)
                {
                    try
                    {
                        HttpWebRequest req = (HttpWebRequest)WebRequest.Create(ytUrl);
                        req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36";
                        req.Timeout = 6000;
                        using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                        using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                        {
                            html = sr.ReadToEnd();
                        }
                        if (!string.IsNullOrEmpty(html)) break;
                    }
                    catch
                    {
                        if (att == 0) Thread.Sleep(250);
                    }
                }

                if (string.IsNullOrEmpty(html) || !string.Equals(_lastTrackKey, trackKey, StringComparison.Ordinal) || IsNonMusicMedia) return;

                MatchCollection vrBlocks = Regex.Matches(html, "\"videoRenderer\":\\{\"videoId\":\"([A-Za-z0-9_-]{11})\"(?s:.*?)(?=\"videoRenderer\":\\{\"videoId\":|$)");
                string chosenId = null;
                double curDur = _durationSec;
                double bestDurDiff = 1e9;

                for (int i = 0; i < Math.Min(8, vrBlocks.Count); i++)
                {
                    string vid = vrBlocks[i].Groups[1].Value;
                    if (chosenId == null && curDur <= 1.0 && !browserPlay) chosenId = vid;

                    Match lenM = Regex.Match(vrBlocks[i].Value, "\"lengthText\":\\{.*?\"simpleText\":\"([0-9:]+)\"");
                    if (!lenM.Success) continue;

                    string[] tParts = lenM.Groups[1].Value.Split(':');
                    int sec = 0;
                    for (int p = 0; p < tParts.Length; p++)
                    {
                        int v;
                        if (int.TryParse(tParts[p], out v)) sec = sec * 60 + v;
                    }
                    if (curDur > 20.0)
                    {
                        double dDiff = Math.Abs(sec - curDur);
                        if (dDiff <= 6.5 && dDiff < bestDurDiff)
                        {
                            bestDurDiff = dDiff;
                            chosenId = vid;
                            if (dDiff <= 1.5) break;
                        }
                    }
                }

                if (chosenId == null && (!browserPlay || curDur <= 1.0))
                {
                    if (vrBlocks.Count > 0)
                    {
                        chosenId = vrBlocks[0].Groups[1].Value;
                    }
                    else
                    {
                        Match mFallback = Regex.Match(html, "\"videoRenderer\":\\{\"videoId\":\"([A-Za-z0-9_-]{11})\"");
                        if (mFallback.Success) chosenId = mFallback.Groups[1].Value;
                    }
                }
                if (string.IsNullOrEmpty(chosenId)) return;

                // 0. Query YouTube Android Player TimedText for BOTH acoustic ASR word-stream (<s t="...">) AND manual lyrics!
                try
                {
                    List<YtWordToken> asrWords;
                    List<LyricLine> ytManualCues;
                    double ytDurSec;
                    if (FetchYoutubePlayerTimingData(chosenId, rawTitle, out asrWords, out ytManualCues, out ytDurSec) &&
                        string.Equals(_lastTrackKey, trackKey, StringComparison.Ordinal))
                    {
                        lock (_lock)
                        {
                            if (_durationSec <= 1.0 && ytDurSec > 20.0)
                            {
                                _durationSec = ytDurSec;
                            }
                            if (asrWords != null && asrWords.Count >= 8)
                            {
                                _ytAsrWords = asrWords;
                            }
                            if (ytManualCues != null && ytManualCues.Count >= 4)
                            {
                                _ytCaptionCues = ytManualCues;
                                _ytCaptionIsManual = true;
                            }
                            if (_syncedLyrics.Count == 0 && (LyricsStatus == "None" || LyricsStatus == "Offline"))
                            {
                                TryApplyManualYtFallbackLyricsLocked();
                            }
                            else
                            {
                                RecalculateAutoMvOffsetLocked();
                            }
                        }
                        UpdateActiveLyricLine();
                    }
                }
                catch { }

                // 1. Query SponsorBlock exact crowd-verified non-music MV skit segments!
                try
                {
                    string sbUrl = "https://sponsor.ajay.app/api/skipSegments?videoID=" + chosenId + "&categories=[%22music_offtopic%22,%22intro%22,%22outro%22]";
                    HttpWebRequest sbReq = (HttpWebRequest)WebRequest.Create(sbUrl);
                    sbReq.UserAgent = "Kumo/1.1 (+https://github.com/buiduchuy2010qn-prog/kumo)";
                    sbReq.Timeout = 3500;
                    using (HttpWebResponse sbResp = (HttpWebResponse)sbReq.GetResponse())
                    using (StreamReader sbSr = new StreamReader(sbResp.GetResponseStream(), Encoding.UTF8))
                    {
                        string sbJson = sbSr.ReadToEnd();
                        MatchCollection segs = Regex.Matches(sbJson, "\"category\"\\s*:\\s*\"([^\"]+)\"[\\s\\S]*?\"segment\"\\s*:\\s*\\[\\s*([0-9.]+)\\s*,\\s*([0-9.]+)\\s*\\]");
                        double introEnd = 0.0;
                        double tailTrim = 0.0;
                        for (int s = 0; s < segs.Count; s++)
                        {
                            string cat = segs[s].Groups[1].Value;
                            double s0, s1;
                            if (double.TryParse(segs[s].Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out s0) &&
                                double.TryParse(segs[s].Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out s1))
                            {
                                if ((cat == "music_offtopic" || cat == "intro") && s0 <= 5.0 && s1 > introEnd && s1 < 240.0)
                                {
                                    introEnd = s1;
                                }
                                else if (s0 > 45.0 && s1 > s0)
                                {
                                    tailTrim += (s1 - s0);
                                }
                            }
                        }

                        if (string.Equals(_lastTrackKey, trackKey, StringComparison.Ordinal))
                        {
                            lock (_lock)
                            {
                                if (introEnd > 2.5)
                                {
                                    _sbIntroOffsetSec = Math.Round(introEnd, 2);
                                }
                                else if (tailTrim >= 4.0)
                                {
                                    _sbZeroIntroConfirmed = true;
                                }
                                RecalculateAutoMvOffsetLocked();
                            }
                            UpdateActiveLyricLine();
                        }
                    }
                }
                catch { }

                // 2. Fallback Artwork from i.ytimg.com if Chrome/Browser only sent a generic browser icon or null!
                if (!_hasRealSmtcThumb && string.Equals(_lastTrackKey, trackKey, StringComparison.Ordinal))
                {
                    try
                    {
                        string imgUrl = "https://i.ytimg.com/vi/" + chosenId + "/hqdefault.jpg";
                        HttpWebRequest iReq = (HttpWebRequest)WebRequest.Create(imgUrl);
                        iReq.UserAgent = "Mozilla/5.0";
                        iReq.Timeout = 4000;
                        using (HttpWebResponse iResp = (HttpWebResponse)iReq.GetResponse())
                        using (Stream rStream = iResp.GetResponseStream())
                        using (MemoryStream ms = new MemoryStream())
                        {
                            byte[] buf = new byte[16384];
                            int rd;
                            while ((rd = rStream.Read(buf, 0, buf.Length)) > 0)
                            {
                                ms.Write(buf, 0, rd);
                            }
                            if (ms.Length > 256)
                            {
                                ms.Position = 0;
                                using (Bitmap rawYt = new Bitmap(ms))
                                {
                                    Bitmap sqYt = CropBitmapToSquareCover(rawYt);
                                    if (sqYt != null)
                                    {
                                        Color domYt = ExtractVibrantColor(sqYt);
                                        lock (_lock)
                                        {
                                            if (!_hasRealSmtcThumb && string.Equals(_lastTrackKey, trackKey, StringComparison.Ordinal))
                                            {
                                                if (_albumArt != null) { try { _albumArt.Dispose(); } catch { } }
                                                _albumArt = sqYt;
                                                _dominantColor = domYt;
                                                sqYt = null;
                                            }
                                        }
                                        if (sqYt != null) sqYt.Dispose();
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static string DecodeXmlText(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string s = Regex.Replace(raw, @"<[^>]+>", "");
            s = s.Replace("&amp;", "&")
                 .Replace("&#39;", "'")
                 .Replace("&apos;", "'")
                 .Replace("&quot;", "\"")
                 .Replace("&lt;", "<")
                 .Replace("&gt;", ">");
            s = Regex.Replace(s, @"&#(\d+);", delegate(Match m)
            {
                int code;
                if (int.TryParse(m.Groups[1].Value, out code) && code >= 32 && code <= 65535)
                {
                    return ((char)code).ToString();
                }
                return "";
            });
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s;
        }

        private static string FetchTimedTextXml(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            for (int att = 0; att < 2; att++)
            {
                try
                {
                    HttpWebRequest cReq = (HttpWebRequest)WebRequest.Create(url);
                    cReq.UserAgent = "com.google.android.youtube/20.10.38 (Linux; U; Android 14)";
                    cReq.Timeout = 6000;
                    using (HttpWebResponse cResp = (HttpWebResponse)cReq.GetResponse())
                    using (StreamReader cSr = new StreamReader(cResp.GetResponseStream(), Encoding.UTF8))
                    {
                        string xml = cSr.ReadToEnd();
                        if (!string.IsNullOrEmpty(xml)) return xml;
                    }
                }
                catch
                {
                    if (att == 0) Thread.Sleep(250);
                }
            }
            return null;
        }

        private static List<YtWordToken> ParseYtAsrWordTokens(string xml)
        {
            if (string.IsNullOrEmpty(xml)) return null;
            MatchCollection pMatches = Regex.Matches(xml, @"<p\s+[^>]*?\bt=""(\d+)""[^>]*>(.*?)</p>", RegexOptions.Singleline);
            if (pMatches.Count == 0) return null;

            Regex rxSpan = new Regex(@"<s(?:\s+[^>]*?\bt=""(\d+)"")?[^>]*>(.*?)</s>", RegexOptions.Singleline);
            List<YtWordToken> tokens = new List<YtWordToken>();

            for (int i = 0; i < pMatches.Count; i++)
            {
                long pMs;
                if (!long.TryParse(pMatches[i].Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out pMs)) continue;
                string inner = pMatches[i].Groups[2].Value;
                string plainCheck = DecodeXmlText(inner);
                if (string.IsNullOrEmpty(plainCheck)) continue;
                if (plainCheck.StartsWith("[", StringComparison.Ordinal) && plainCheck.EndsWith("]", StringComparison.Ordinal)) continue;
                if (plainCheck == "\u266A" || plainCheck == "\u266B") continue;

                MatchCollection sMatches = rxSpan.Matches(inner);
                if (sMatches.Count > 0)
                {
                    for (int s = 0; s < sMatches.Count; s++)
                    {
                        long sMs = 0L;
                        if (sMatches[s].Groups[1].Success)
                        {
                            long.TryParse(sMatches[s].Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out sMs);
                        }
                        string wFold = NormalizeFold(DecodeXmlText(sMatches[s].Groups[2].Value));
                        if (string.IsNullOrEmpty(wFold)) continue;
                        string[] parts = wFold.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        double baseSec = (pMs + sMs) / 1000.0;
                        for (int p = 0; p < parts.Length; p++)
                        {
                            tokens.Add(new YtWordToken
                            {
                                Word = parts[p],
                                TimeSec = baseSec + p * 0.18
                            });
                        }
                    }
                }
                else
                {
                    string wFold = NormalizeFold(plainCheck);
                    if (string.IsNullOrEmpty(wFold)) continue;
                    string[] parts = wFold.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    double baseSec = pMs / 1000.0;
                    for (int p = 0; p < parts.Length; p++)
                    {
                        tokens.Add(new YtWordToken
                        {
                            Word = parts[p],
                            TimeSec = baseSec + p * 0.34
                        });
                    }
                }
            }
            return (tokens.Count >= 8) ? tokens : null;
        }

        private static List<LyricLine> ParseYtManualCueLines(string xml)
        {
            if (string.IsNullOrEmpty(xml)) return null;
            MatchCollection pMatches = Regex.Matches(xml, @"<p\s+[^>]*?\bt=""(\d+)""[^>]*>(.*?)</p>", RegexOptions.Singleline);
            if (pMatches.Count < 4) return null;

            List<LyricLine> list = new List<LyricLine>();
            for (int i = 0; i < pMatches.Count; i++)
            {
                long ms;
                if (!long.TryParse(pMatches[i].Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out ms)) continue;
                string txt = DecodeXmlText(pMatches[i].Groups[2].Value);
                if (string.IsNullOrEmpty(txt) || txt.Length < 2) continue;
                if (txt.StartsWith("[", StringComparison.Ordinal) && txt.EndsWith("]", StringComparison.Ordinal)) continue;
                if (txt == "\u266A" || txt == "\u266B") continue;

                double sec = ms / 1000.0;
                if (list.Count > 0 && Math.Abs(list[list.Count - 1].TimeSec - sec) < 0.08)
                {
                    list[list.Count - 1].Text = (list[list.Count - 1].Text + " " + txt).Trim();
                }
                else
                {
                    list.Add(new LyricLine { TimeSec = sec, Text = txt });
                }
            }
            return (list.Count >= 4) ? list : null;
        }

        private static bool FetchYoutubePlayerTimingData(string videoId, string rawTitle, out List<YtWordToken> asrWords, out List<LyricLine> manualCues, out double playerDurSec)
        {
            asrWords = null;
            manualCues = null;
            playerDurSec = 0.0;
            try
            {
                string bodyJson = "{\"context\":{\"client\":{\"clientName\":\"ANDROID\",\"clientVersion\":\"20.10.38\",\"hl\":\"vi\"}},\"videoId\":\"" + videoId + "\"}";
                byte[] postBytes = Encoding.UTF8.GetBytes(bodyJson);

                string playerJson = null;
                for (int att = 0; att < 2; att++)
                {
                    try
                    {
                        HttpWebRequest pReq = (HttpWebRequest)WebRequest.Create("https://www.youtube.com/youtubei/v1/player?prettyPrint=false");
                        pReq.Method = "POST";
                        pReq.ContentType = "application/json";
                        pReq.UserAgent = "com.google.android.youtube/20.10.38 (Linux; U; Android 14)";
                        pReq.Timeout = 6000;
                        pReq.KeepAlive = false;
                        pReq.ServicePoint.Expect100Continue = false;
                        pReq.ContentLength = postBytes.Length;
                        using (Stream reqStream = pReq.GetRequestStream())
                        {
                            reqStream.Write(postBytes, 0, postBytes.Length);
                        }
                        using (HttpWebResponse pResp = (HttpWebResponse)pReq.GetResponse())
                        using (StreamReader pSr = new StreamReader(pResp.GetResponseStream(), Encoding.UTF8))
                        {
                            playerJson = pSr.ReadToEnd();
                        }
                        if (!string.IsNullOrEmpty(playerJson)) break;
                    }
                    catch
                    {
                        if (att == 0) Thread.Sleep(250);
                    }
                }
                if (string.IsNullOrEmpty(playerJson)) return false;

                Match durM = Regex.Match(playerJson, "\"lengthSeconds\"\\s*:\\s*\"(\\d+)\"");
                if (durM.Success)
                {
                    double.TryParse(durM.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out playerDurSec);
                }

                int capIdx = playerJson.IndexOf("\"captionTracks\":[", StringComparison.Ordinal);
                if (capIdx < 0) return false;
                int arrStart = playerJson.IndexOf('[', capIdx);
                if (arrStart < 0) return false;

                List<string> trackObjs = new List<string>();
                int bracketDepth = 0;
                int braceDepth = 0;
                int objStart = -1;
                bool inStr = false;
                bool esc = false;
                for (int p = arrStart; p < playerJson.Length; p++)
                {
                    char ch = playerJson[p];
                    if (inStr)
                    {
                        if (esc) esc = false;
                        else if (ch == '\\') esc = true;
                        else if (ch == '"') inStr = false;
                        continue;
                    }
                    if (ch == '"') { inStr = true; continue; }
                    if (ch == '[') bracketDepth++;
                    else if (ch == ']')
                    {
                        bracketDepth--;
                        if (bracketDepth <= 0) break;
                    }
                    else if (ch == '{')
                    {
                        if (bracketDepth == 1 && braceDepth == 0) objStart = p;
                        braceDepth++;
                    }
                    else if (ch == '}')
                    {
                        braceDepth--;
                        if (bracketDepth == 1 && braceDepth == 0 && objStart >= 0)
                        {
                            trackObjs.Add(playerJson.Substring(objStart, p - objStart + 1));
                            objStart = -1;
                        }
                    }
                }
                if (trackObjs.Count == 0) return false;

                bool preferVi = HasNonAscii(rawTitle ?? "");
                string bestAsrUrl = null;
                int bestAsrScore = -9999;
                string bestManUrl = null;
                int bestManScore = -9999;

                for (int i = 0; i < trackObjs.Count; i++)
                {
                    string obj = trackObjs[i];
                    Match mUrl = Regex.Match(obj, "\"baseUrl\"\\s*:\\s*\"([^\"]+)\"");
                    if (!mUrl.Success) continue;
                    string u = Regex.Unescape(mUrl.Groups[1].Value);
                    if (u.IndexOf("fmt=", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        u += "&fmt=srv3";
                    }

                    Match mVss = Regex.Match(obj, "\"vssId\"\\s*:\\s*\"([^\"]+)\"");
                    Match mKind = Regex.Match(obj, "\"kind\"\\s*:\\s*\"([^\"]+)\"");
                    Match mLang = Regex.Match(obj, "\"languageCode\"\\s*:\\s*\"([^\"]+)\"");
                    string vss = mVss.Success ? mVss.Groups[1].Value : "";
                    string kind = mKind.Success ? mKind.Groups[1].Value : "";
                    string lang = mLang.Success ? mLang.Groups[1].Value : "";
                    bool isAsr = vss.StartsWith("a.", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(kind, "asr", StringComparison.OrdinalIgnoreCase);

                    int langBonus = 0;
                    if (preferVi && string.Equals(lang, "vi", StringComparison.OrdinalIgnoreCase)) langBonus = 80;
                    else if (!preferVi && string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase)) langBonus = 40;
                    else if (string.Equals(lang, "vi", StringComparison.OrdinalIgnoreCase)) langBonus = 25;

                    int sc = langBonus - i * 2;
                    if (isAsr)
                    {
                        if (sc > bestAsrScore) { bestAsrScore = sc; bestAsrUrl = u; }
                    }
                    else
                    {
                        if (sc > bestManScore) { bestManScore = sc; bestManUrl = u; }
                    }
                }

                if (!string.IsNullOrEmpty(bestAsrUrl))
                {
                    try { asrWords = ParseYtAsrWordTokens(FetchTimedTextXml(bestAsrUrl)); } catch { }
                }
                if (!string.IsNullOrEmpty(bestManUrl))
                {
                    try { manualCues = ParseYtManualCueLines(FetchTimedTextXml(bestManUrl)); } catch { }
                }

                return (asrWords != null && asrWords.Count >= 8) || (manualCues != null && manualCues.Count >= 4);
            }
            catch
            {
                return false;
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
            SourceAppId = "chrome.exe";
            HasMedia = true;
            IsPlaying = true;
            IsNonMusicMedia = false;
            _basePositionSec = 47.0;
            _durationSec = 214.0;
            _positionSampledUtc = DateTime.UtcNow;
        }

        public void SetMockVoiceNotePreview(bool enable)
        {
            if (!enable)
            {
                ClearMediaState();
                return;
            }
            lock (_lock)
            {
                if (_albumArt != null) { try { _albumArt.Dispose(); } catch { } _albumArt = null; }
                _dominantColor = Color.FromArgb(44, 110, 215);
                _syncedLyrics.Clear();
                CurrentLyricLine = "";
                NextLyricLine = "";
                LyricsStatus = "Skipped";
            }
            CurrentTitle = "Facebook";
            CurrentArtist = "";
            SourceAppId = "chrome.exe";
            HasMedia = true;
            IsPlaying = true;
            IsNonMusicMedia = true;
            _basePositionSec = 3.0;
            _durationSec = 10.0;
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
            if (!HasMedia || !IsPlaying || IsNonMusicMedia) return false;
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
                else if ((p1.IndexOf(',') >= 0 || p1.IndexOf('@') >= 0 || Regex.IsMatch(p1, @"\b(x|ft\.?|feat\.?)\b", RegexOptions.IgnoreCase)) && p0.Length >= 4)
                {
                    // Strip trailing multi-singer / @handle credits like "- Nguyenn x @Dangtuanvu.Original" or "- JENA, ANH RỒNG..."
                    cleanT = p0;
                }
            }

            cleanT = Regex.Replace(cleanT, @"(\bft\.?\b.*$|\bfeat\.?\b.*$|\bprod\.?\b.*$)", "", RegexOptions.IgnoreCase).Trim();
            cleanT = Regex.Replace(cleanT, @"\s{2,}", " ").Trim(' ', '-', '\u2013', '\u2014');
            songCore = cleanT;
            artistCore = cleanA;
        }

        private static string FirstNWords(string s, int maxWords)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string[] w = s.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (w.Length <= maxWords) return s.Trim();
            return string.Join(" ", w, 0, maxWords);
        }

        private static string GetLyricsCacheFilePath(string songFold, string artistFold)
        {
            try
            {
                string key = (songFold ?? "") + "|" + (artistFold ?? "");
                ulong h = 14695981039346656037UL;
                for (int i = 0; i < key.Length; i++)
                {
                    h ^= (uint)key[i];
                    h *= 1099511628211UL;
                }
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Kumo\LyricsCache");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return Path.Combine(dir, h.ToString("x16", CultureInfo.InvariantCulture) + ".json");
            }
            catch
            {
                return null;
            }
        }

        private static double ScoreCandidateTitle(string targetSongFold, string candTrackFold, string candArtistFold, out bool isSameSong, out int matchedArtistWords)
        {
            matchedArtistWords = 0;
            if (string.IsNullOrEmpty(targetSongFold))
            {
                isSameSong = false;
                return -400.0;
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
            string[] aWords = string.IsNullOrEmpty(candArtistFold)
                ? new string[0]
                : candArtistFold.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (tWords.Length == 0)
            {
                isSameSong = false;
                return -400.0;
            }

            int matchedTrack = 0;
            int matchedArtist = 0;
            for (int i = 0; i < tWords.Length; i++)
            {
                bool inTrack = false;
                for (int j = 0; j < cWords.Length; j++)
                {
                    if (string.Equals(tWords[i], cWords[j], StringComparison.Ordinal))
                    {
                        inTrack = true;
                        break;
                    }
                }
                if (inTrack)
                {
                    matchedTrack++;
                    continue;
                }
                for (int j = 0; j < aWords.Length; j++)
                {
                    if (tWords[i].Length >= 2 && string.Equals(tWords[i], aWords[j], StringComparison.Ordinal))
                    {
                        matchedArtist++;
                        break;
                    }
                }
            }

            matchedArtistWords = matchedArtist;
            int effectiveTargetLen = Math.Max(1, tWords.Length - matchedArtist);
            if (matchedTrack == tWords.Length || (matchedTrack >= 2 && matchedTrack == effectiveTargetLen && cWords.Length <= matchedTrack + 3))
            {
                isSameSong = true;
                int extra = Math.Max(0, cWords.Length - matchedTrack);
                return 920.0 + matchedArtist * 35.0 - Math.Min(220.0, extra * 14.0);
            }
            if (effectiveTargetLen >= 3 && matchedTrack >= effectiveTargetLen - 1 && ((double)matchedTrack / effectiveTargetLen) >= 0.75)
            {
                isSameSong = true;
                return 540.0 + matchedArtist * 25.0;
            }

            isSameSong = false;
            return -950.0 + matchedTrack * 75.0;
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

        private static string FetchUrlStringWithRetry(string url, out bool wasServerOverloaded)
        {
            wasServerOverloaded = false;
            int[] delays = new int[] { 340, 560, 820, 1150 };
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                    req.UserAgent = "Kumo/1.1 (+https://github.com/buiduchuy2010qn-prog/kumo)";
                    req.Timeout = 5200;
                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    {
                        wasServerOverloaded = false;
                        return sr.ReadToEnd();
                    }
                }
                catch (WebException wex)
                {
                    HttpWebResponse hr = wex.Response as HttpWebResponse;
                    if (hr != null && hr.StatusCode == HttpStatusCode.NotFound)
                    {
                        wasServerOverloaded = false;
                        return null;
                    }
                    wasServerOverloaded = true;
                    if (attempt < 4) Thread.Sleep(delays[attempt]);
                }
                catch
                {
                    wasServerOverloaded = true;
                    if (attempt < 4) Thread.Sleep(delays[attempt]);
                }
            }
            return null;
        }

        private bool TryProcessLrclibJson(string json, string targetSongFold, string targetArtistFold)
        {
            if (string.IsNullOrEmpty(json)) return false;
            if (IsNonMusicMedia) return false;
            if (_durationSec > 0.5 && (_durationSec < 28.0 || _durationSec > 900.0)) return false;

            string[] targetWords = string.IsNullOrEmpty(targetSongFold)
                ? new string[0]
                : targetSongFold.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            int targetWordCount = targetWords.Length;
            if (targetWordCount == 0) return false;

            string artistCompact = (targetArtistFold ?? "").Replace(" ", "");
            if (string.IsNullOrEmpty(artistCompact) && targetWordCount < 2) return false;

            MatchCollection objs = Regex.Matches(json, @"\{\s*""id""\s*:[\s\S]*?(?=\},\s*\{\s*""id""|\}\s*\]\s*$|\}\s*$)");
            if (objs.Count == 0) return false;

            List<LrcCandidate> cands = new List<LrcCandidate>();
            string bestPlain = null;
            double bestPlainScore = -1e9;

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
                int matchedArtistWords;
                string cTrackFold = NormalizeFold(cTrack);
                string cArtistFold = NormalizeFold(cArtist);
                double tScore = ScoreCandidateTitle(targetSongFold, cTrackFold, cArtistFold, out isSame, out matchedArtistWords);
                if (isSame && !string.IsNullOrEmpty(_lastRawVersionHint) && (" " + cTrackFold + " ").IndexOf(" " + _lastRawVersionHint + " ", StringComparison.Ordinal) >= 0)
                {
                    tScore += 145.0;
                }
                double aBonus = 0.0;
                string cArtCompact = cArtistFold.Replace(" ", "");
                if (!string.IsNullOrEmpty(artistCompact) && !string.IsNullOrEmpty(cArtCompact))
                {
                    if (cArtCompact.Contains(artistCompact) || artistCompact.Contains(cArtCompact))
                    {
                        aBonus = 220.0;
                    }
                }

                bool hasArtistEvidence = (aBonus > 0.0 || matchedArtistWords > 0);
                if (isSame)
                {
                    if (_durationSec >= 28.0 && cDur > 15.0)
                    {
                        if (string.IsNullOrEmpty(artistCompact) && matchedArtistWords == 0)
                        {
                            if (Math.Abs(cDur - _durationSec) > 8.0) isSame = false;
                        }
                        else if (!hasArtistEvidence && targetWordCount == 1)
                        {
                            if (Math.Abs(cDur - _durationSec) > 10.0) isSame = false;
                        }
                        else if (cDur > _durationSec + 85.0 || _durationSec > cDur + 165.0)
                        {
                            isSame = false;
                        }
                    }
                    else if (string.IsNullOrEmpty(artistCompact) && matchedArtistWords == 0 && _durationSec <= 0.5)
                    {
                        isSame = false;
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
                    bool plainDurOk = (_durationSec <= 0.5 && cDur > 25.0) ||
                                      (_durationSec >= 35.0 && cDur > 25.0 && Math.Abs(cDur - _durationSec) <= 45.0);
                    double pSc = tScore + aBonus - i * 1.5;
                    if (isSame && hasArtistEvidence && plainDurOk && pSc > bestPlainScore)
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
                        _lastMatchedJson = json;
                        _lastMatchedSongFold = targetSongFold ?? "";
                        _lastMatchedArtistFold = targetArtistFold ?? "";
                        _lastEvaluatedDurSec = _durationSec;
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

        private bool TryQueryLrclibSingle(string rawQuery, string targetSongFold, string targetArtistFold, string cachePath, ref bool anyServerBusy)
        {
            if (string.IsNullOrEmpty(rawQuery)) return false;
            string url = "https://lrclib.net/api/search?q=" + Uri.EscapeDataString(rawQuery.Trim());
            bool busy;
            string json = FetchUrlStringWithRetry(url, out busy);
            if (busy) anyServerBusy = true;
            if (!string.IsNullOrEmpty(json) && TryProcessLrclibJson(json, targetSongFold, targetArtistFold))
            {
                if (!string.IsNullOrEmpty(cachePath))
                {
                    try { File.WriteAllText(cachePath, json, Encoding.UTF8); } catch { }
                }
                return true;
            }
            return false;
        }

        private void FetchLyricsFromLrclib(string expectedTrackKey, string track, string artist)
        {
            if (IsNonMusicMedia) return;
            lock (_lock)
            {
                _syncedLyrics.Clear();
                CurrentLyricLine = "Loading lyrics...";
                NextLyricLine = "";
                LyricsStatus = "Loading lyrics...";
            }

            try
            {
                if (_durationSec <= 0.5 && string.IsNullOrEmpty(artist))
                {
                    Thread.Sleep(250);
                    if (!string.Equals(_lastTrackKey, expectedTrackKey, StringComparison.Ordinal) || IsNonMusicMedia) return;
                }

                string songCore;
                string artistCore;
                bool isMv;
                ExtractSongAndArtist(track, artist, out songCore, out artistCore, out isMv);
                string verHint = "";
                Match verM = Regex.Match(track ?? "", @"\b(piano|acoustic|lofi|remix|live|cover|slowed|speed\s*up)\b", RegexOptions.IgnoreCase);
                if (verM.Success) verHint = NormalizeFold(verM.Groups[1].Value);
                lock (_lock)
                {
                    _isLikelyMvTrack = isMv;
                    _lastRawVersionHint = verHint;
                }

                string songFold = NormalizeFold(songCore);
                string artistFold = NormalizeFold(artistCore);
                if (string.IsNullOrEmpty(songFold) || (string.IsNullOrEmpty(artistFold) && !songFold.Contains(" ")))
                {
                    lock (_lock)
                    {
                        if (!IsNonMusicMedia && string.Equals(_lastTrackKey, expectedTrackKey, StringComparison.Ordinal))
                        {
                            CurrentLyricLine = "Lyrics not available";
                            NextLyricLine = "";
                            LyricsStatus = "None";
                        }
                    }
                    return;
                }
                string cachePath = GetLyricsCacheFilePath(songFold, artistFold);

                // 0. Instant local disk cache hit (0.3ms, works even if LRCLIB is 503 overloaded!)
                if (!string.IsNullOrEmpty(cachePath) && File.Exists(cachePath))
                {
                    try
                    {
                        string cachedJson = File.ReadAllText(cachePath, Encoding.UTF8);
                        if (TryProcessLrclibJson(cachedJson, songFold, artistFold))
                        {
                            return;
                        }
                    }
                    catch { }
                }

                string shortSong = FirstNWords(songCore, 6);
                string shortArtist = FirstNWords(artistCore, 3);

                for (int round = 0; round < 3; round++)
                {
                    if (!string.Equals(_lastTrackKey, expectedTrackKey, StringComparison.Ordinal) || IsNonMusicMedia) return;

                    bool anyBusy = false;

                    // 1. Short query if title is a long mashup (>6 words) — avoids LRCLIB FTS5 503 timeout!
                    if (!string.Equals(shortSong, songCore, StringComparison.Ordinal))
                    {
                        if (TryQueryLrclibSingle(shortSong, songFold, artistFold, cachePath, ref anyBusy)) return;
                    }

                    // 2. Song + Artist
                    string q1 = (FirstNWords(songCore, 8) + " " + shortArtist).Trim();
                    if (!string.IsNullOrEmpty(q1) && TryQueryLrclibSingle(q1, songFold, artistFold, cachePath, ref anyBusy)) return;

                    // 3. Pure Song Core
                    if (!string.IsNullOrEmpty(songCore) && !string.Equals(q1, songCore, StringComparison.OrdinalIgnoreCase))
                    {
                        if (TryQueryLrclibSingle(songCore, songFold, artistFold, cachePath, ref anyBusy)) return;
                    }

                    // 4. Exact /api/get fallback
                    if (!string.IsNullOrEmpty(songCore) && !string.IsNullOrEmpty(artistCore))
                    {
                        string getUrl = "https://lrclib.net/api/get?track_name=" + Uri.EscapeDataString(songCore) +
                                        "&artist_name=" + Uri.EscapeDataString(artistCore);
                        bool gBusy;
                        string getJson = FetchUrlStringWithRetry(getUrl, out gBusy);
                        if (gBusy) anyBusy = true;
                        if (!string.IsNullOrEmpty(getJson))
                        {
                            string wrapped = "[" + getJson + "]";
                            if (TryProcessLrclibJson(wrapped, songFold, artistFold))
                            {
                                if (!string.IsNullOrEmpty(cachePath))
                                {
                                    try { File.WriteAllText(cachePath, wrapped, Encoding.UTF8); } catch { }
                                }
                                return;
                            }
                        }
                    }

                    if (!anyBusy) break;

                    // LRCLIB was 503 ServerOverloaded: wait 3.5s and auto-retry while song is still playing!
                    lock (_lock)
                    {
                        if (!IsNonMusicMedia && string.Equals(_lastTrackKey, expectedTrackKey, StringComparison.Ordinal))
                        {
                            CurrentLyricLine = "Loading lyrics...";
                        }
                    }
                    Thread.Sleep(3500);
                }

                if (!string.Equals(_lastTrackKey, expectedTrackKey, StringComparison.Ordinal) || IsNonMusicMedia) return;
                bool usedYtFallback = false;
                lock (_lock)
                {
                    if (TryApplyManualYtFallbackLyricsLocked())
                    {
                        usedYtFallback = true;
                    }
                    else
                    {
                        CurrentLyricLine = "Lyrics not available";
                        NextLyricLine = "";
                        LyricsStatus = "None";
                    }
                }
                if (usedYtFallback) UpdateActiveLyricLine();
            }
            catch
            {
                if (!string.Equals(_lastTrackKey, expectedTrackKey, StringComparison.Ordinal) || IsNonMusicMedia) return;
                bool usedYtFallback = false;
                lock (_lock)
                {
                    if (TryApplyManualYtFallbackLyricsLocked())
                    {
                        usedYtFallback = true;
                    }
                    else
                    {
                        CurrentLyricLine = "Lyrics not available";
                        NextLyricLine = "";
                        LyricsStatus = "Offline";
                    }
                }
                if (usedYtFallback) UpdateActiveLyricLine();
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

            double onsetGrace = Math.Min(0.05, rawSpan * 0.04);
            double availSpan = Math.Max(0.50, rawSpan - onsetGrace);
            double maxNatural = mc.Count * 0.68 + punctExtra + 0.78;
            double vocalSpan = (availSpan <= maxNatural)
                ? (availSpan * 0.95)
                : Math.Min(availSpan * 0.95, mc.Count * 0.56 + punctExtra + 0.62);
            vocalSpan = Math.Max(0.52, vocalSpan);

            LyricWordSpan[] spans = new LyricWordSpan[mc.Count];
            double curT = ln.TimeSec + onsetGrace;
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

            list.Sort(delegate(LyricLine a, LyricLine b) { return a.TimeSec.CompareTo(b.TimeSec); });
            List<RawLrcSeed> seeds = new List<RawLrcSeed>(list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                LyricWordSpan[] seedWords = null;
                if (list[i].Words != null && list[i].Words.Length > 0)
                {
                    seedWords = new LyricWordSpan[list[i].Words.Length];
                    Array.Copy(list[i].Words, seedWords, list[i].Words.Length);
                }
                seeds.Add(new RawLrcSeed
                {
                    TimeSec = list[i].TimeSec,
                    EndSec = list[i].EndSec,
                    Text = list[i].Text,
                    Words = seedWords
                });
            }

            FinalizeLyricTimings(list, _durationSec);
            lock (_lock)
            {
                _baseLrcSeeds = seeds;
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
