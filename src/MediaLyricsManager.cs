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
    public class LyricLine
    {
        public double TimeSec;
        public string Text;
    }

    public class MediaLyricsManager : IDisposable
    {
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

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

        public LyricLine[] GetDisplayLyricsWindow(out int activeSlot)
        {
            activeSlot = -1;
            double pos = GetLivePositionSec();
            lock (_lock)
            {
                if (_syncedLyrics.Count == 0)
                {
                    return new LyricLine[0];
                }

                int curIdx = 0;
                for (int i = 0; i < _syncedLyrics.Count; i++)
                {
                    if (_syncedLyrics[i].TimeSec <= pos + 0.35)
                        curIdx = i;
                    else
                        break;
                }

                int start = Math.Max(0, curIdx - 1);
                if (start + 4 > _syncedLyrics.Count)
                {
                    start = Math.Max(0, _syncedLyrics.Count - 4);
                }
                int count = Math.Min(4, _syncedLyrics.Count - start);
                LyricLine[] res = new LyricLine[count];
                for (int i = 0; i < count; i++)
                {
                    res[i] = _syncedLyrics[start + i];
                    if (start + i == curIdx) activeSlot = i;
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
        private void WorkerLoop()
        {
            while (_running)
            {
                try
                {
                    PollSmtcOnce();
                    UpdateActiveLyricLine();
                }
                catch { }
                Thread.Sleep(900);
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
                            _basePositionSec = posSec;
                            _positionSampledUtc = DateTime.UtcNow;
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
                string cleanTrack = Regex.Replace(track, @"(\(.*?\)|\[.*?\]|\|.*$|official\s*music\s*video|official\s*video|mv|lyrics|audio|ft\..*$)", "", RegexOptions.IgnoreCase).Trim();
                string cleanArtist = Regex.Replace(artist, @"\s*-\s*Topic$", "", RegexOptions.IgnoreCase).Trim();
                string q = Uri.EscapeDataString((cleanArtist + " " + cleanTrack).Trim());
                string url = "https://lrclib.net/api/search?q=" + q;

                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = "Kumo/1.0 (+https://github.com)";
                req.Timeout = 5000;

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string json = sr.ReadToEnd();
                    Match mSynced = Regex.Match(json, "\"syncedLyrics\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
                    if (mSynced.Success && !string.IsNullOrEmpty(mSynced.Groups[1].Value))
                    {
                        string rawLrc = Regex.Unescape(mSynced.Groups[1].Value);
                        ParseLrc(rawLrc);
                        if (_syncedLyrics.Count > 0)
                        {
                            LyricsStatus = "Synced";
                            UpdateActiveLyricLine();
                            return;
                        }
                    }

                    Match mPlain = Regex.Match(json, "\"plainLyrics\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
                    if (mPlain.Success && !string.IsNullOrEmpty(mPlain.Groups[1].Value))
                    {
                        string rawPlain = Regex.Unescape(mPlain.Groups[1].Value);
                        string[] lines = rawPlain.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        double dur = _durationSec > 20.0 ? _durationSec : (lines.Length * 4.0);
                        double stepSec = Math.Max(2.5, dur / Math.Max(1, lines.Length));
                        lock (_lock)
                        {
                            _syncedLyrics.Clear();
                            double t = 0;
                            foreach (string ln in lines)
                            {
                                _syncedLyrics.Add(new LyricLine { TimeSec = t, Text = ln.Trim() });
                                t += stepSec;
                            }
                            LyricsStatus = "Plain";
                        }
                        UpdateActiveLyricLine();
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

        private void ParseLrc(string lrcText)
        {
            List<LyricLine> list = new List<LyricLine>();
            string[] lines = lrcText.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            Regex rx = new Regex(@"\[(\d+):(\d+(?:\.\d+)?)\](.*)");
            foreach (string line in lines)
            {
                Match m = rx.Match(line);
                if (m.Success)
                {
                    int mins = int.Parse(m.Groups[1].Value);
                    double secs = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                    string txt = m.Groups[3].Value.Trim();
                    if (!string.IsNullOrEmpty(txt))
                    {
                        list.Add(new LyricLine { TimeSec = mins * 60.0 + secs, Text = txt });
                    }
                }
            }
            lock (_lock)
            {
                _syncedLyrics = list;
            }
        }

        private void UpdateActiveLyricLine()
        {
            double pos = GetLivePositionSec();
            lock (_lock)
            {
                if (_syncedLyrics.Count == 0) return;
                int idx = 0;
                for (int i = 0; i < _syncedLyrics.Count; i++)
                {
                    if (_syncedLyrics[i].TimeSec <= pos + 0.35)
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
