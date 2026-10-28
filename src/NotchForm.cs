using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Kumo
{
    public class NotchForm : Form
    {
        private readonly AppState _state;
        private readonly HardwareMonitor _hw;
        private readonly MediaLyricsManager _media;
        private readonly SystemOsdMonitor _osd;
        private readonly DownloadMonitor _dl;

        // 3-Stage Smart Auto-Notch states (decompiled 1:1 from nusa_v1.1.0.exe):
        // - mode = 6 (Expanded):   420 x 140 dp (or 520 x 140 dp in Split Lyrics)
        // - mode = 1 (Glance):     150 x 30 dp  (Clock 17:34 / Live Mini Album Art when active; Mascot when idle/returning)
        // - mode = 2 (Minimal):    44 x 4.5 dp  (Auto-shrinks when Chrome tabs reach the Notch, or when Minimal mode is ON)
        private bool _isExpanded = false;
        private bool _isMicroPill = false;
        private bool _autoMinimalObstruction = false;
        private bool _alwaysMinimal = false;
        private float _autoMinTimerSec = 0f;
        private bool _manualWaveDemo = false;

        private bool IsLight { get { return _state.IsNotchLight; } }
        private Color InkPri { get { return IsLight ? Color.FromArgb(255, 15, 23, 42) : Color.FromArgb(255, 255, 255); } }
        private Color Nw(int a)
        {
            a = Math.Max(0, Math.Min(255, a));
            if (!IsLight)
            {
                if (a >= 85) return Color.FromArgb(a, 255, 255, 255);
                int boostA = (a <= 4) ? a : Math.Min(116, (int)(a * 1.46f) + 12);
                return Color.FromArgb(boostA, 255, 255, 255);
            }
            if (a >= 88) return Color.FromArgb(Math.Min(255, a + 28), 15, 23, 42);
            return Color.FromArgb(Math.Max(10, (int)(a * 0.48f)), 15, 23, 42);
        }
        private Color ToCrispInk(Color col)
        {
            if (IsLight || col.A < 80) return col;
            if (col.R == col.G && col.G == col.B)
            {
                int v = (col.A >= 250) ? 255 : Math.Min(255, 174 + ((col.A - 80) * 81) / 175);
                return Color.FromArgb(255, v, v, Math.Min(255, v + 4));
            }
            if (col.A >= 200) return Color.FromArgb(255, col.R, col.G, col.B);
            return col;
        }
        private bool IsCollapsedLyricsActive
        {
            get { return _isLyricsSplit && _state.ShowLyrics && _media != null && _media.HasCollapsedLyricNow(); }
        }
        private string _lastCollapsedLyricKey = "";
        private Color VeilCol(int a)
        {
            a = Math.Max(0, Math.Min(255, a));
            return IsLight ? Color.FromArgb(a, 248, 250, 254) : Color.FromArgb(a, 0, 0, 0);
        }
        private bool _isLyricsSplit = false;
        private bool _showRestingMascot = true;

        // Interactive sub-states inside the Notch
        private bool _waterEditMode = false;       // Inline Goal & Bottle selector inside Water page
        private bool _showHeaderSeconds = false;   // Toggle HH:mm <-> HH:mm:ss when clicking header clock
        private int _todoScroll = 0;               // Scroll offset for TodoList inside Notch
        private int _sysSelectedMetric = 0;        // 0: CPU/RAM, 1: SSD, 2: Wi-Fi, 3: GPU 0
        private bool _isModalDialogOpen = false;   // Keep Notch open while OpenFileDialog / QuickTodoPopup is active

        // Mouse drag-vs-click & scrubbing state
        private bool _isScrubbingSeek = false;
        private RectangleF _lastSeekBarRect = RectangleF.Empty;
        private LyricLine[] _lastDisplayedLyrics = new LyricLine[0];
        private int _lyricScrollOffset = 0;
        private DateTime _lyricScrollUntilUtc = DateTime.MinValue;
        private string _lyricScrollTrackKey = "";
        private Point _lastHoverPt = Point.Empty;
        private int _shelfDragCandidate = -1;
        private Point _mouseDownPt = Point.Empty;

        // Exact 4-state Mascot state machine from nusa_v1.1.0.exe (RVA 0x2010..0x2260 & 0x43D20..0x43FFF)
        private int _petState = 0;
        private uint _savedIdleInputTick = 0;
        private bool _previewForceWave = false;
        private DateTime _waveUntilUtc = DateTime.MinValue;
        private volatile bool _bgScanRunning = true;
        private volatile bool _bgFullscreen = false;
        private volatile bool _bgTabObstructed = false;
        private IntPtr _cachedSelfHwnd = IntPtr.Zero;
        private IntPtr _cachedSettingsHwnd = IntPtr.Zero;
        private Rectangle _cachedScreenBounds = new Rectangle(0, 0, 1920, 1080);
        private Thread _winScanThread = null;
        private int _obstructionStreak = 0;
        private bool _suppressHoverUntilLeave = false;
        private bool _isFullscreenHidden = false;
        private SettingsForm _settingsForm = null;

        private float _batteryFraction = 0.88f;
        private int _batteryPercent = 100;
        private bool _batteryCharging = false;
        private int _batteryStateKind = 0;
        private bool _batteryBaselineReady = false;
        private OsdKind _osdKind = OsdKind.None;
        private int _osdPercent = 50;
        private bool _osdMuted = false;
        private int _osdDelta = 0;
        private DateTime _osdUntilUtc = DateTime.MinValue;
        private float _osdFade = 0.0f;
        private float _osdAnimBar = 0.5f;
        private bool _wasOsdActive = false;
        private bool IsOsdActive { get { return DateTime.UtcNow < _osdUntilUtc; } }
        private DownloadSnapshot _dlSnap;
        private DateTime _lastDlPollUtc = DateTime.MinValue;
        private float _dlFade = 0.0f;
        private float _dlExpBannerFade = 0.0f;
        private float _dlAnimBar = 0.28f;
        private bool IsDownloadActive { get { return _state.DownloadHudEnabled && _dl != null && _dlSnap.IsActive; } }
        private bool IsPrivacyActive { get { return _state.PrivacyDotsEnabled && _osd != null && (_osd.IsCameraActive || _osd.IsMicActive); } }
        private float _privacyFade = 0.0f;
        private int _lastPrivMask = 0;
        private readonly DateTime _startupUtc = DateTime.UtcNow;
        private ulong _lastPinnedClipHash = 0;
        private DateTime _lastClipCaptureUtc = DateTime.MinValue;
        private readonly Dictionary<string, Bitmap> _shelfThumbCache = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);

        // Smooth continuous blend factors ("nổi nhẹ từ từ" like Nusa!)
        private readonly Stopwatch _frameClock = Stopwatch.StartNew();
        private double _lastFrameSec = 0.0;
        private float _glanceFade = 1.0f;
        private int _activeColMode = 0; // 0=Glance, 1=Mascot, 2=Download, 3=OSD
        private float _colContentFade = 1.0f;
        private float _mascotFade = 0.0f;
        private float _sleepFade = 0.0f;
        private float _waveFade = 0.0f;
        private float _expandFade = 0.0f;
        private float _hoverDwellSec = 0.0f;
        private float _leaveDwellSec = 0.0f;
        private DateTime _gestureHoldCollapsedUntilUtc = DateTime.MinValue;
        private Point _prevScreenCursor = Point.Empty;
        private Point _stationarySuppressOrigin = Point.Empty;
        private bool _suppressStationaryExpand = false;
        private bool _wasInsidePill = false;

        // v1.1.23: real equalizer, desktop CPU ring, lock keys, mic hotkey
        private bool _hasBattery = true;
        private readonly float[] _eqLevels = new float[5];
        private float _eqGain = 0.12f;
        private bool _lockBaselineReady = false;
        private bool _capsOn, _numOn, _capsRawLast, _numRawLast, _capsWasDown, _numWasDown;
        private const int HOTKEY_MIC_ID = 0x4B4D;
        private bool _micHotkeyRegistered = false;
        private bool _forceAudioDevOsd = false;

        // Pages: 0=Music, 1=Pomodoro, 2=Water, 3=Shelf, 4=TodoList, 5=SystemMonitor
        private int _pageIndex = 5;
        private const int TotalPages = 6;
        private static readonly string[] PageNames = new string[]
        {
            "Music", "Pomodoro", "Water", "Shelf", "TodoList", "System"
        };

        private float _scale = 1.25f;

        // Spring physics state
        private float _curW, _curH, _curR, _curE;
        private float _velW, _velH, _velR, _velE;
        private float _targetW, _targetH, _targetR, _targetE;

        // Fixed-size reusable backbuffer
        private Bitmap _canvasBmp;
        private int _canvasW;
        private int _canvasH;
        private int _canvasWinX;
        private int _canvasWinY;

        private bool _needsRedraw = true;
        private int _lastSecond = -1;
        private bool _isDragHover = false;

        private System.Windows.Forms.Timer _animTimer;
        private System.Windows.Forms.Timer _secondTimer;

        private struct HitZone
        {
            public string Id;
            public RectangleF Rect;
        }
        private readonly List<HitZone> _hitZones = new List<HitZone>();
        private string _hoveredId = null;

        // Cached Fonts
        private Font _fIdleTime;
        private Font _fCollapsedLyric;
        private Font _fHdrDate;
        private Font _fHdrTime;
        private Font _fTitle;
        private Font _fMusicTitle;
        private Font _fMusicSub;
        private Font _fTimeSmall;
        private Font _fLyricOn;
        private Font _fLyricOff;
        private Font _fLyricOnFit;
        private Font _fLyricOffFit;
        private Font _fHeroBig;
        private Font _fHeroUnit;
        private Font _fPill;
        private Font _fAxis;
        private Font _fShelfMain;
        private Font _fShelfSub;

        public NotchForm(AppState state, HardwareMonitor hw, MediaLyricsManager media, SystemOsdMonitor osd = null, DownloadMonitor dl = null)
        {
            _state = state;
            _hw = hw;
            _media = media;
            _osd = osd;
            _dl = dl;
            _pageIndex = 5;
            _showRestingMascot = _state.MascotEnabled;
            _alwaysMinimal = _state.AlwaysMinimal;
            _isLyricsSplit = _state.DefaultLyricsSplit && _state.ShowLyrics;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            ShowInTaskbar = false;
            AllowDrop = true;
            DoubleBuffered = true;

            RefreshBatteryLevel();
            DetectScaleAndInitFonts();
            InitReusableCanvas();

            UpdateTargetDimensions();
            _curW = U(110f);
            _curH = U(4f);
            _curR = U(3f);
            _curE = U(3f);

            DragEnter += OnNotchDragEnter;
            DragLeave += OnNotchDragLeave;
            DragDrop += OnNotchDragDrop;
            MouseMove += OnNotchMouseMove;
            MouseDown += OnNotchMouseDown;
            MouseUp += OnNotchMouseUp;
            MouseWheel += OnNotchMouseWheel;
            MouseLeave += (s, e) =>
            {
                _isScrubbingSeek = false;
                _shelfDragCandidate = -1;
                if (_hoveredId != null)
                {
                    _hoveredId = null;
                    Cursor = Cursors.Default;
                    _needsRedraw = true;
                }
            };

            _animTimer = new System.Windows.Forms.Timer();
            _animTimer.Interval = 15;
            _animTimer.Tick += OnAnimTick;
            _animTimer.Start();

            if (_osd != null) _osd.OsdChanged += OnSystemOsdChanged;
            _cachedScreenBounds = GetTargetScreen().Bounds;
            HandleCreated += (s, e) =>
            {
                _cachedSelfHwnd = Handle;
                try { NativeMethods.AddClipboardFormatListener(Handle); } catch { }
                UpdateMicHotkey();
            };
            FormClosed += (s, e) =>
            {
                _bgScanRunning = false;
                if (_osd != null) _osd.OsdChanged -= OnSystemOsdChanged;
                try { if (_cachedSelfHwnd != IntPtr.Zero) NativeMethods.RemoveClipboardFormatListener(_cachedSelfHwnd); } catch { }
                try { if (_micHotkeyRegistered && _cachedSelfHwnd != IntPtr.Zero) NativeMethods.UnregisterHotKey(_cachedSelfHwnd, HOTKEY_MIC_ID); } catch { }
                _micHotkeyRegistered = false;
                ClearShelfThumbCache();
            };
            _winScanThread = new Thread(BackgroundWindowScanLoop)
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = "KumoWindowScan"
            };
            _winScanThread.Start();
            _secondTimer = new System.Windows.Forms.Timer();
            _secondTimer.Interval = 1000;
            _secondTimer.Tick += OnSecondTick;
            _secondTimer.Start();
        }

        private void OnSystemOsdChanged(OsdKind kind, int percent, bool isMuted, int delta)
        {
            if (IsDisposed) return;
            if (kind == OsdKind.AudioDevice && !_state.AudioDeviceOsdEnabled && !_forceAudioDevOsd) return;
            if (kind == OsdKind.AudioDevice) _forceAudioDevOsd = false;
            if (kind == OsdKind.PrivacyAlert && !_state.PrivacyDotsEnabled) return;
            if (kind == OsdKind.MicMute && !_state.MicHotkeyEnabled) return;
            double dur = 2.05;
            if (kind == OsdKind.AudioDevice) dur = 2.85;
            else if (kind == OsdKind.PrivacyAlert) dur = 2.55;
            else if (kind == OsdKind.MicMute) dur = 1.9;
            try
            {
                if (IsHandleCreated)
                    BeginInvoke(new MethodInvoker(delegate { TriggerOsdHud(kind, percent, isMuted, delta, dur); }));
            }
            catch { }
        }

        private void UpdateMicHotkey()
        {
            if (!IsHandleCreated) return;
            bool want = _state.MicHotkeyEnabled && _osd != null;
            if (_osd != null) _osd.MicWatchEnabled = _state.MicHotkeyEnabled;
            try
            {
                if (want && !_micHotkeyRegistered)
                {
                    _micHotkeyRegistered = NativeMethods.RegisterHotKey(Handle, HOTKEY_MIC_ID,
                        NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, (uint)Keys.M);
                }
                else if (!want && _micHotkeyRegistered)
                {
                    NativeMethods.UnregisterHotKey(Handle, HOTKEY_MIC_ID);
                    _micHotkeyRegistered = false;
                }
            }
            catch { }
        }

        // Caps Lock / Num Lock: GetKeyState toggle bit + global key-down edge (works even while Kumo has no focus)
        private void PollLockKeys()
        {
            bool capsRaw = (NativeMethods.GetKeyState(0x14) & 1) != 0;
            bool numRaw = (NativeMethods.GetKeyState(0x90) & 1) != 0;
            bool capsDown = (NativeMethods.GetAsyncKeyState(0x14) & 0x8000) != 0;
            bool numDown = (NativeMethods.GetAsyncKeyState(0x90) & 0x8000) != 0;

            if (!_lockBaselineReady)
            {
                _lockBaselineReady = true;
                _capsOn = capsRaw; _capsRawLast = capsRaw; _capsWasDown = capsDown;
                _numOn = numRaw; _numRawLast = numRaw; _numWasDown = numDown;
                return;
            }

            bool showCaps = false, showNum = false;
            if (capsDown && !_capsWasDown)
            {
                _capsOn = !_capsOn;
                showCaps = true;
            }
            _capsWasDown = capsDown;
            if (capsRaw != _capsRawLast)
            {
                _capsRawLast = capsRaw;
                if (capsRaw != _capsOn)
                {
                    _capsOn = capsRaw;
                    showCaps = true;
                }
            }

            if (numDown && !_numWasDown)
            {
                _numOn = !_numOn;
                showNum = true;
            }
            _numWasDown = numDown;
            if (numRaw != _numRawLast)
            {
                _numRawLast = numRaw;
                if (numRaw != _numOn)
                {
                    _numOn = numRaw;
                    showNum = true;
                }
            }

            if (!_state.LockKeysOsdEnabled) return;
            if (showCaps) TriggerOsdHud(OsdKind.CapsLock, 100, false, _capsOn ? 1 : 0, 1.45);
            else if (showNum) TriggerOsdHud(OsdKind.NumLock, 100, false, _numOn ? 1 : 0, 1.45);
        }

        public void TriggerOsdHud(OsdKind kind, int percent, bool isMuted, int delta, double durationSec)
        {
            _osdKind = kind;
            _osdPercent = Math.Max(0, Math.Min(100, percent));
            _osdMuted = isMuted;
            _osdDelta = delta;
            float targetBar = (kind == OsdKind.Volume && isMuted) ? 0f : (_osdPercent / 100.0f);
            if (_osdFade < 0.08f) _osdAnimBar = targetBar;
            _osdUntilUtc = DateTime.UtcNow.AddSeconds(durationSec);
            _wasOsdActive = true;
            if (!_isExpanded && _isMicroPill)
            {
                _isMicroPill = false;
            }
            UpdateTargetDimensions();
            _needsRedraw = true;
        }

        public int CurrentPage
        {
            get { return _pageIndex; }
            set
            {
                _pageIndex = ((value % TotalPages) + TotalPages) % TotalPages;
                _isExpanded = true;
                _isMicroPill = false;
                _hw.IsNotchExpanded = true;
                UpdateTargetDimensions();
                _needsRedraw = true;
            }
        }

        public bool ShowRestingMascot
        {
            get { return _showRestingMascot; }
            set
            {
                _showRestingMascot = value;
                _state.MascotEnabled = value;
                _state.SaveAll();
                if (value)
                {
                    TriggerMascotWave(3.2);
                }
                else
                {
                    _petState = 0;
                    _waveUntilUtc = DateTime.MinValue;
                    UpdateAutoMinimalState();
                    UpdateTargetDimensions();
                    _needsRedraw = true;
                }
            }
        }

        public void TriggerMascotWave(double durationSec)
        {
            _showRestingMascot = true;
            _state.MascotEnabled = true;
            _manualWaveDemo = true;
            _petState = 3;
            _isMicroPill = false;
            _waveUntilUtc = DateTime.UtcNow.AddSeconds(durationSec);
            UpdateTargetDimensions();
            _needsRedraw = true;
        }

        public bool MinimalMode
        {
            get { return _alwaysMinimal; }
            set
            {
                _alwaysMinimal = value;
                _state.AlwaysMinimal = value;
                _state.SaveAll();
                UpdateAutoMinimalState();
                UpdateTargetDimensions();
                _needsRedraw = true;
            }
        }

        public void OpenSettings(int tabIndex)
        {
            if (_settingsForm == null || _settingsForm.IsDisposed)
            {
                _settingsForm = new SettingsForm(_state, this);
            }
            _settingsForm.OpenAtTab(tabIndex);
            if (_settingsForm.IsHandleCreated)
            {
                _cachedSettingsHwnd = _settingsForm.Handle;
                NativeMethods.SetForegroundWindow(_settingsForm.Handle);
            }
        }

        public void RevealAndOpenSettings()
        {
            _isFullscreenHidden = false;
            _autoMinimalObstruction = false;
            if (!_alwaysMinimal) _isMicroPill = false;
            UpdateTargetDimensions();
            _needsRedraw = true;
            RenderLayeredNotch();
            OpenSettings(-1);
        }

        public void ApplySettingsLive()
        {
            _showRestingMascot = _state.MascotEnabled;
            _alwaysMinimal = _state.AlwaysMinimal;
            if (!_state.ShowLyrics) _isLyricsSplit = false;

            if (!_showRestingMascot)
            {
                _petState = 0;
                _waveUntilUtc = DateTime.MinValue;
            }

            DetectScaleAndInitFonts();
            InitReusableCanvas();

            if (IsHandleCreated)
            {
                Bounds = new Rectangle(_canvasWinX, _canvasWinY, _canvasW, _canvasH);
            }

            _cachedScreenBounds = GetTargetScreen().Bounds;
            _isFullscreenHidden = _state.HideInFullscreen && _bgFullscreen;
            UpdateMicHotkey();
            UpdateAutoMinimalState();
            UpdateTargetDimensions();
            _curW = Math.Min(_canvasW - U(50f), Math.Max(U(24f), _curW));
            _needsRedraw = true;
            RenderLayeredNotch();
        }

        private Screen GetTargetScreen()
        {
            Screen[] screens = Screen.AllScreens;
            if (screens != null && screens.Length > 0)
            {
                int idx = _state.MonitorIndex;
                if (idx >= 0 && idx < screens.Length) return screens[idx];
            }
            return Screen.PrimaryScreen;
        }

        // ==================== BULLETPROOF WIN32 OVERLAY HIT-TESTING ====================
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                // WS_EX_NOACTIVATE prevents stealing focus from YouTube/Chrome/games while delivering 100% of clicks!
                // No WS_EX_TRANSPARENT here — WM_NCHITTEST handles click-through cleanly with zero SetWindowLong bugs!
                cp.ExStyle |= NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (NativeMethods.WM_OPEN_SETTINGS != 0 && m.Msg == (int)NativeMethods.WM_OPEN_SETTINGS)
            {
                RevealAndOpenSettings();
                return;
            }
            if (m.Msg == NativeMethods.WM_DPICHANGED)
            {
                ApplySettingsLive();
                m.Result = IntPtr.Zero;
                return;
            }
            if (m.Msg == NativeMethods.WM_CLIPBOARDUPDATE)
            {
                TryCaptureClipboardScreenshotToShelf();
                m.Result = IntPtr.Zero;
                return;
            }
            if (m.Msg == NativeMethods.WM_HOTKEY && m.WParam.ToInt64() == HOTKEY_MIC_ID)
            {
                if (_osd != null && _state.MicHotkeyEnabled) _osd.ToggleMicMute();
                m.Result = IntPtr.Zero;
                return;
            }
            if (m.Msg == 0x0021) // WM_MOUSEACTIVATE
            {
                m.Result = new IntPtr(3); // MA_NOACTIVATE: process click immediately without stealing window focus!
                return;
            }
            if (m.Msg == 0x0084) // WM_NCHITTEST
            {
                if (_isFullscreenHidden)
                {
                    m.Result = new IntPtr(-1);
                    return;
                }
                if (!_isExpanded)
                {
                    int lp = unchecked((int)m.LParam.ToInt64());
                    int sx = (short)(lp & 0xFFFF);
                    int sy = (short)((lp >> 16) & 0xFFFF);
                    Rectangle scr = GetTargetScreen().Bounds;
                    int cx = scr.Left + scr.Width / 2;
                    int maxHalfW = _isMicroPill ? (int)U(26f) : (int)(_curW * 0.5f + U(4f));
                    int maxH = _isMicroPill ? (int)U(6f) : (int)Math.Max(U(30f), _curH + U(2f));
                    if (Math.Abs(sx - cx) > maxHalfW || (sy - scr.Top) > maxH)
                    {
                        m.Result = new IntPtr(-1);
                        return;
                    }
                    // Pass clicks straight through to browser tabs underneath when auto-shrunk/minimal in hover mode!
                    if (!_state.OpenByClick && (_isMicroPill || _bgTabObstructed))
                    {
                        m.Result = new IntPtr(-1);
                        return;
                    }
                }
            }
            base.WndProc(ref m);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Bounds = new Rectangle(_canvasWinX, _canvasWinY, _canvasW, _canvasH);
            RenderLayeredNotch();
        }

        private void RefreshBatteryLevel()
        {
            try
            {
                NativeMethods.SYSTEM_POWER_STATUS sps;
                if (NativeMethods.GetSystemPowerStatus(out sps))
                {
                    bool hasPack = ((sps.BatteryFlag & 128) == 0);
                    // Desktop PCs report "no system battery" (128) or "unknown" (255): show a live CPU ring instead of a fake 100% battery
                    bool noBattery = !hasPack || sps.BatteryFlag == 255;
                    if (noBattery != !_hasBattery)
                    {
                        _hasBattery = !noBattery;
                        _needsRedraw = true;
                    }
                    int pct = (hasPack && sps.BatteryLifePercent <= 100) ? (int)sps.BatteryLifePercent : 100;
                    bool charging = !noBattery && ((sps.ACLineStatus == 1) || ((sps.BatteryFlag & 8) != 0));
                    int kind = charging ? 1 : (pct <= 20 ? 2 : 0);
                    if (!_batteryBaselineReady)
                    {
                        _batteryBaselineReady = true;
                        _batteryPercent = pct; _batteryCharging = charging; _batteryStateKind = kind;
                        _batteryFraction = Math.Max(0.06f, pct / 100.0f);
                    }
                    else
                    {
                        bool chgChanged = (charging != _batteryCharging);
                        bool enteredLow = (!charging && kind == 2 && _batteryStateKind != 2);
                        bool enteredCrit = (!charging && pct <= 10 && _batteryPercent > 10);
                        if (pct != _batteryPercent || chgChanged || kind != _batteryStateKind)
                        {
                            _batteryPercent = pct; _batteryCharging = charging; _batteryStateKind = kind;
                            _batteryFraction = Math.Max(0.06f, pct / 100.0f);
                            _needsRedraw = true;
                        }
                        if (_hasBattery && (chgChanged || enteredLow || enteredCrit))
                            TriggerOsdHud(OsdKind.Battery, pct, false, charging ? 1 : -1, 2.75);
                    }
                }
            }
            catch { }
        }

        private void DisposeCachedFonts()
        {
            Font[] fonts = new Font[]
            {
                _fIdleTime, _fCollapsedLyric, _fHdrDate, _fHdrTime, _fTitle, _fMusicTitle, _fMusicSub,
                _fTimeSmall, _fLyricOn, _fLyricOff, _fLyricOnFit, _fLyricOffFit, _fHeroBig, _fHeroUnit, _fPill,
                _fAxis, _fShelfMain, _fShelfSub
            };
            for (int i = 0; i < fonts.Length; i++)
            {
                if (fonts[i] != null)
                {
                    try { fonts[i].Dispose(); } catch { }
                }
            }
        }

        private void DetectScaleAndInitFonts()
        {
            float dpi = NativeMethods.GetMonitorDpi(IsHandleCreated ? Handle : IntPtr.Zero, GetTargetScreen().Bounds);

            DisposeCachedFonts();
            _scale = (dpi / 96.0f) * _state.GetSizeMultiplier();

            _fIdleTime       = MakePxFont("Segoe UI", 15.0f, FontStyle.Bold);
            _fCollapsedLyric = MakePxFont("Segoe UI", 12.6f, FontStyle.Bold);
            _fHdrDate        = MakeSemiboldFont(12.2f);
            _fHdrTime        = MakePxFont("Segoe UI", 12.4f, FontStyle.Bold);
            _fTitle          = MakePxFont("Segoe UI", 14.6f, FontStyle.Bold);
            _fMusicTitle     = MakePxFont("Segoe UI", 14.5f, FontStyle.Bold);
            _fMusicSub       = MakeSemiboldFont(11.6f);
            _fTimeSmall      = MakeSemiboldFont(11.6f);
            _fLyricOn        = MakePxFont("Segoe UI", 14.2f, FontStyle.Bold);
            _fLyricOff       = MakeSemiboldFont(12.8f);
            _fLyricOnFit     = MakePxFont("Segoe UI", 12.6f, FontStyle.Bold);
            _fLyricOffFit    = MakeSemiboldFont(11.4f);
            _fHeroBig        = MakePxFont("Segoe UI", 28.5f, FontStyle.Bold);
            _fHeroUnit       = MakePxFont("Segoe UI", 12.6f, FontStyle.Bold);
            _fPill           = MakePxFont("Segoe UI", 11.4f, FontStyle.Bold);
            _fAxis           = MakeSemiboldFont(10.5f);
            _fShelfMain      = MakePxFont("Segoe UI", 12.5f, FontStyle.Bold);
            _fShelfSub       = MakeSemiboldFont(10.2f);
        }

        private Font MakeSemiboldFont(float dpSize)
        {
            float px = Math.Max(9.0f, (float)Math.Round(dpSize * _scale));
            try
            {
                Font f = new Font("Segoe UI Semibold", px, FontStyle.Regular, GraphicsUnit.Pixel);
                if (f != null && f.Name.IndexOf("Segoe UI", StringComparison.OrdinalIgnoreCase) >= 0) return f;
                if (f != null) f.Dispose();
            }
            catch { }
            return MakePxFont("Segoe UI", dpSize, FontStyle.Bold);
        }

        private void InitReusableCanvas()
        {
            _canvasW = Math.Max(320, (int)Math.Ceiling(U(696f)));
            _canvasH = Math.Max(120, (int)Math.Ceiling(U(182f)));
            Rectangle screen = GetTargetScreen().Bounds;
            _canvasWinX = screen.Left + (screen.Width - _canvasW) / 2;
            _canvasWinY = screen.Top;
            if (_canvasBmp != null)
            {
                try { _canvasBmp.Dispose(); } catch { }
            }
            _canvasBmp = new Bitmap(_canvasW, _canvasH, PixelFormat.Format32bppArgb);
        }

        private Font MakePxFont(string family, float dpSize, FontStyle style)
        {
            float px = Math.Max(9.0f, (float)Math.Round(dpSize * _scale));
            try { return new Font(family, px, style, GraphicsUnit.Pixel); }
            catch { return new Font(FontFamily.GenericSansSerif, px, style, GraphicsUnit.Pixel); }
        }

        private float U(float dp) { return dp * _scale; }

        private void UpdateTargetDimensions()
        {
            if (_isFullscreenHidden && !_isExpanded && !IsOsdActive)
            {
                _targetW = U(36f); _targetH = 0f; _targetR = U(2f); _targetE = U(1f);
                return;
            }
            if (_isExpanded)
            {
                bool wide = (_pageIndex == 0 && _isLyricsSplit && _state.ShowLyrics);
                _targetW = U(wide ? 598f : 416f); _targetH = U(wide ? 152f : 139f); _targetR = U(30f); _targetE = U(20f);
            }
            else if (IsOsdActive)
            {
                bool compactBar = ((_osdKind == OsdKind.Volume && !_osdMuted) || _osdKind == OsdKind.Brightness);
                bool tinyHud = (_osdKind == OsdKind.CapsLock || _osdKind == OsdKind.NumLock || _osdKind == OsdKind.MicMute);
                _targetW = U(tinyHud ? 196f : (compactBar ? 228f : 292f)); _targetH = U(32f); _targetR = U(15.5f); _targetE = U(10.5f);
            }
            else if (IsDownloadActive && !_alwaysMinimal)
            {
                _targetW = U(302f); _targetH = U(32f); _targetR = U(15.5f); _targetE = U(10.5f);
            }
            else if (_isMicroPill || _alwaysMinimal)
            {
                _targetW = U(IsPrivacyActive ? 54f : 44f); _targetH = U(IsPrivacyActive ? 5.5f : 4.5f); _targetR = U(3.0f); _targetE = U(2.5f);
            }
            else if (IsCollapsedLyricsActive)
            {
                double lp; bool syn;
                string lt = _media.GetCollapsedLyricDisplay(out lp, out syn);
                int len = string.IsNullOrEmpty(lt) ? 16 : lt.Length;
                float eqAdd = _state.EqualizerEnabled ? 22f : 0f;
                float privAdd = IsPrivacyActive ? ((_osd != null && _osd.IsCameraActive && _osd.IsMicActive) ? 24f : 14f) : 0f;
                float lyrDp = Math.Max(284f, Math.Min(504f, 126f + eqAdd + privAdd + len * 7.4f));
                _targetW = U(lyrDp); _targetH = U(29f); _targetR = U(14f); _targetE = U(10f);
            }
            else
            {
                float privAdd = IsPrivacyActive ? ((_osd != null && _osd.IsCameraActive && _osd.IsMicActive) ? 34f : 20f) : 0f;
                _targetW = U(150f + privAdd); _targetH = U(29f); _targetR = U(14f); _targetE = U(10f);
            }
        }

        private void OnSecondTick(object sender, EventArgs e)
        {
RefreshBatteryLevel();

            if (_state.PomodoroRunning)
            {
                if (_state.PomodoroSecondsLeft > 0)
                {
                    _state.PomodoroSecondsLeft--;
                    if (_isExpanded && _pageIndex == 1) _needsRedraw = true;
                    else if (!_isExpanded && !_isMicroPill) _needsRedraw = true; // live countdown on the collapsed pill
                }
                else
                {
                    _state.PomodoroRunning = false;
                    _state.PomodoroMode = (_state.PomodoroMode == 0) ? 1 : 0;
                    _state.PomodoroSecondsLeft = (_state.PomodoroBreakMode ? _state.PomodoroBreakMinutes : _state.PomodoroWorkMinutes) * 60;
                    if (_state.ReminderSounds)
                    {
                        try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
                    }
                    _isExpanded = true;
                    _pageIndex = 1;
                    UpdateTargetDimensions();
                    _needsRedraw = true;
                }
            }

            _cachedScreenBounds = GetTargetScreen().Bounds;
            bool fsNow = _state.HideInFullscreen && _bgFullscreen;
            if (fsNow != _isFullscreenHidden)
            {
                _isFullscreenHidden = fsNow;
                if (fsNow && _isExpanded)
                {
                    _isExpanded = false;
                    _hw.IsNotchExpanded = false;
                }
                UpdateTargetDimensions();
                _needsRedraw = true;
            }

            if (!_isExpanded && !_isFullscreenHidden)
            {
                UpdateAutoMinimalState();
            }

            int sec = DateTime.Now.Second;
            if (sec != _lastSecond)
            {
                _lastSecond = sec;
                if (_isExpanded)
                {
                    _needsRedraw = true;
                }
                else if (!_isMicroPill && (sec == 0 || _media.HasMedia || (!_hasBattery && sec % 2 == 0)))
                {
                    _needsRedraw = true;
                }
            }
        }

        private void UpdateAutoMinimalState()
        {
            if (_isExpanded || IsOsdActive || (IsDownloadActive && !_alwaysMinimal))
            {
                _isMicroPill = false;
                _obstructionStreak = 0;
                return;
            }

            if (_manualWaveDemo && _petState == 3 && DateTime.UtcNow < _waveUntilUtc)
            {
                if (_isMicroPill)
                {
                    _isMicroPill = false;
                    UpdateTargetDimensions();
                    _needsRedraw = true;
                }
                return;
            }

            bool rawObstructed = _alwaysMinimal || (_state.AutoMinimalEnabled && _bgTabObstructed);
            if (rawObstructed != _autoMinimalObstruction)
            {
                _obstructionStreak++;
                int neededStreak = (_alwaysMinimal || rawObstructed) ? 1 : 2;
                if (_obstructionStreak >= neededStreak)
                {
                    _obstructionStreak = 0;
                    _autoMinimalObstruction = rawObstructed;
                    _isMicroPill = rawObstructed;
                    if (_isMicroPill && !_manualWaveDemo) _petState = 0;
                    UpdateTargetDimensions();
                    _needsRedraw = true;
                }
            }
            else
            {
                _obstructionStreak = 0;
                if (_isMicroPill != _autoMinimalObstruction)
                {
                    _isMicroPill = _autoMinimalObstruction;
                    UpdateTargetDimensions();
                    _needsRedraw = true;
                }
            }
        }

        private static bool IsIgnoredShellClass(string cls)
        {
            if (string.IsNullOrEmpty(cls)) return true;
            return cls == "Progman" ||
                   cls == "WorkerW" ||
                   cls == "Shell_TrayWnd" ||
                   cls == "Shell_SecondaryTrayWnd" ||
                   cls == "NotifyIconOverflowWindow" ||
                   cls == "TopLevelWindowForOverflowXamlIsland" ||
                   cls == "XamlExplorerHostIslandWindow" ||
                   cls == "Windows.UI.Core.CoreWindow" ||
                   cls == "Windows.UI.Input.InputSite.WindowClass" ||
                   cls == "MultitaskingViewFrame" ||
                   cls == "TaskListThumbnailWnd" ||
                   cls == "ForegroundStaging";
        }

        private void BackgroundWindowScanLoop()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder(96);
            float[] xOffsetsDp = new float[] { -90f, -60f, -30f, 0f, 30f, 60f, 90f };

            while (_bgScanRunning)
            {
                try
                {
                    bool fs = false;
                    bool tabObs = false;
                    IntPtr fg = NativeMethods.GetForegroundWindow();

                    if (fg != IntPtr.Zero && fg != _cachedSelfHwnd && fg != _cachedSettingsHwnd &&
                        NativeMethods.IsWindowVisible(fg) && !NativeMethods.IsIconic(fg) && !NativeMethods.IsWindowCloaked(fg))
                    {
                        sb.Length = 0;
                        NativeMethods.GetClassName(fg, sb, 96);
                        string cls = sb.ToString();

                        if (!IsIgnoredShellClass(cls) && !cls.Equals("ApplicationFrameWindow", StringComparison.OrdinalIgnoreCase))
                        {
                            NativeMethods.RECT wr;
                            if (NativeMethods.GetWindowRect(fg, out wr))
                            {
                                Rectangle scr = _cachedScreenBounds;
                                float sc = _scale;
                                int style = NativeMethods.GetWindowLong(fg, NativeMethods.GWL_STYLE);

                                // 1. True Fullscreen game / borderless video check
                                if ((style & NativeMethods.WS_CAPTION) == 0 &&
                                    wr.Left <= scr.Left && wr.Top <= scr.Top &&
                                    wr.Right >= scr.Right && wr.Bottom >= scr.Bottom)
                                {
                                    fs = true;
                                }

                                // 2. Smart Browser/Explorer Tab collision check (zero CopyFromScreen, zero UI lag!)
                                if (!fs && _state.AutoMinimalEnabled)
                                {
                                    bool isTabbedApp =
                                        cls.IndexOf("Chrome_WidgetWin", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                        cls.IndexOf("MozillaWindowClass", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                        cls.Equals("CabinetWClass", StringComparison.OrdinalIgnoreCase) ||
                                        cls.Equals("WinUIDesktopWin32WindowClass", StringComparison.OrdinalIgnoreCase);

                                    int centerX = scr.Left + scr.Width / 2;
                                    if (isTabbedApp &&
                                        wr.Top <= scr.Top + (int)(12f * sc) &&
                                        wr.Bottom > scr.Top + (int)(60f * sc) &&
                                        wr.Left <= centerX - (int)(110f * sc) &&
                                        wr.Right >= centerX + (int)(110f * sc))
                                    {
                                        int topEdge = Math.Max(scr.Top, wr.Top);
                                        int yProbe = topEdge + Math.Max(12, (int)Math.Round(16f * sc));
                                        for (int i = 0; i < xOffsetsDp.Length; i++)
                                        {
                                            int px = centerX + (int)Math.Round(xOffsetsDp[i] * sc);
                                            IntPtr res;
                                            IntPtr ret = NativeMethods.SendMessageTimeout(
                                                fg,
                                                (uint)NativeMethods.WM_NCHITTEST,
                                                IntPtr.Zero,
                                                NativeMethods.MakePointLParam(px, yProbe),
                                                NativeMethods.SMTO_ABORTIFHUNG,
                                                12,
                                                out res);
                                            if (ret == IntPtr.Zero)
                                            {
                                                // Window busy or elevated: stop probing immediately so we never stall!
                                                break;
                                            }
                                            int hit = unchecked((int)res.ToInt64());
                                            if (hit != NativeMethods.HTCAPTION && hit != 0)
                                            {
                                                tabObs = true;
                                                break;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }

                    _bgFullscreen = fs;
                    _bgTabObstructed = tabObs;
                }
                catch { }

                Thread.Sleep(280);
            }
        }

        private static float SmoothStep01(float x)
        {
            if (x <= 0f) return 0f;
            if (x >= 1f) return 1f;
            return x * x * (3f - 2f * x);
        }

        private static bool MoveTowards(ref float cur, float target, float maxDelta)
        {
            if (Math.Abs(target - cur) <= 0.002f)
            {
                if (cur != target) { cur = target; return true; }
                return false;
            }
            if (cur < target)
                cur = Math.Min(target, cur + maxDelta);
            else
                cur = Math.Max(target, cur - maxDelta);
            return true;
        }

        private static bool StepSpringDt(ref float cur, ref float vel, float target, float dt, float omega, float zeta)
        {
            float diff = cur - target;
            float accel = -omega * omega * diff - 2f * zeta * omega * vel;
            vel += accel * dt;
            cur += vel * dt;
            if (Math.Abs(target - cur) < 0.12f && Math.Abs(vel) < 0.8f)
            {
                cur = target;
                vel = 0f;
                return false;
            }
            return true;
        }

        private void OnAnimTick(object sender, EventArgs e)
        {
            double nowSec = _frameClock.Elapsed.TotalSeconds;
            float dt = (float)(nowSec - _lastFrameSec);
            _lastFrameSec = nowSec;
            if (dt <= 0.001f || dt > 0.08f) dt = 0.016f;

            Point cursor = Cursor.Position;
            Rectangle screenBounds = GetTargetScreen().Bounds;
            int centerX = screenBounds.Left + screenBounds.Width / 2;
            int relY = cursor.Y - screenBounds.Top;
            int absDx = Math.Abs(cursor.X - centerX);

            DateTime nowUtc = DateTime.UtcNow;

            if ((nowUtc - _lastDlPollUtc).TotalMilliseconds >= 120.0)
            {
                _lastDlPollUtc = nowUtc;
                bool prevDlActive = IsDownloadActive;
                bool prevDlDone = _dlSnap.IsCompleted;
                _dlSnap = (_dl != null && _state.DownloadHudEnabled) ? _dl.GetSnapshot() : new DownloadSnapshot();
                bool curDlActive = IsDownloadActive;
                if (curDlActive != prevDlActive || _dlSnap.IsCompleted != prevDlDone)
                {
                    if (curDlActive && !_alwaysMinimal) _isMicroPill = false;
                    UpdateAutoMinimalState();
                    UpdateTargetDimensions();
                    _needsRedraw = true;
                }
            }

            PollLockKeys();
            UpdateLiveEqualizer(dt);

            if (!_isExpanded && !_isFullscreenHidden)
            {
                _autoMinTimerSec += dt;
                if (_autoMinTimerSec >= 0.18f)
                {
                    _autoMinTimerSec = 0f;
                    UpdateAutoMinimalState();
                }
            }

            // ==================== 1. EXACT NUSA MASCOT LIFECYCLE ====================
            uint lastInputTick = NativeMethods.GetLastInputTick();
            uint idleMs = unchecked((uint)Environment.TickCount - lastInputTick);

            if (_isExpanded || !_showRestingMascot || _isFullscreenHidden ||
                IsOsdActive || (IsDownloadActive && !_alwaysMinimal) ||
                ((_media.HasMedia && _media.IsPlaying) || IsCollapsedLyricsActive) ||
                ((_alwaysMinimal || _autoMinimalObstruction) && !_manualWaveDemo))
            {
                if (_petState != 0)
                {
                    _petState = 0;
                    _manualWaveDemo = false;
                    _waveUntilUtc = DateTime.MinValue;
                }
            }
            else
            {
                if (_petState == 0)
                {
                    if (idleMs >= 15000)
                    {
                        _petState = 1;
                        _savedIdleInputTick = lastInputTick;
                        if (_isMicroPill)
                        {
                            _isMicroPill = false;
                            UpdateTargetDimensions();
                        }
                        _needsRedraw = true;
                    }
                }
                else if (_petState == 1 || _petState == 2)
                {
                    if (lastInputTick != _savedIdleInputTick)
                    {
                        _petState = 3;
                        _waveUntilUtc = nowUtc.AddSeconds(3.2);
                        if (_isMicroPill)
                        {
                            _isMicroPill = false;
                            UpdateTargetDimensions();
                        }
                        _needsRedraw = true;
                    }
                    else if (_petState == 1 && idleMs >= 20500)
                    {
                        _petState = 2;
                        _needsRedraw = true;
                    }
                }
                else if (_petState == 3)
                {
                    if (nowUtc >= _waveUntilUtc)
                    {
                        _petState = 0;
                        _manualWaveDemo = false;
                        UpdateAutoMinimalState();
                        _needsRedraw = true;
                    }
                }
            }

            // ==================== 2. HOVER TO OPEN / MOVE AWAY TO CLOSE ====================
            bool wantExpand = _isExpanded;
            bool zeroMove = (_prevScreenCursor != Point.Empty && cursor.X == _prevScreenCursor.X && cursor.Y == _prevScreenCursor.Y);
            _prevScreenCursor = cursor;

            if (_isDragHover || _isModalDialogOpen || _isScrubbingSeek)
            {
                wantExpand = true;
                _hoverDwellSec = 0f;
                _leaveDwellSec = 0f;
            }
            else if (_isExpanded)
            {
                int activeHalfW = (int)(_curW * 0.5f + _curE + U(10f));
                int activeH = (int)(_curH + U(12f));
                bool insideExpanded = (absDx <= activeHalfW && relY >= -4 && relY <= activeH);
                if (insideExpanded)
                {
                    _leaveDwellSec = 0f;
                }
                else
                {
                    _leaveDwellSec += dt;
                    if (_leaveDwellSec >= 0.14f)
                    {
                        wantExpand = false;
                        _leaveDwellSec = 0f;
                    }
                }
            }
            else
            {
                bool insidePill = !_isFullscreenHidden && (_isMicroPill
                    ? (absDx <= (int)U(50f) && relY >= 0 && relY <= (int)U(9.5f))
                    : (absDx <= (int)(_curW * 0.5f + U(4f)) && relY >= 0 && relY <= (int)Math.Max(U(31f), _curH + U(2f))));

                // Only suppress if the pill widened itself over a literally untouched cursor (0 px movement),
                // and immediately release the moment the user nudges the mouse >= 4 px!
                if (insidePill && !_wasInsidePill && zeroMove)
                {
                    _suppressStationaryExpand = true;
                    _stationarySuppressOrigin = cursor;
                }
                if (_suppressStationaryExpand && (Math.Abs(cursor.X - _stationarySuppressOrigin.X) + Math.Abs(cursor.Y - _stationarySuppressOrigin.Y) >= 4))
                {
                    _suppressStationaryExpand = false;
                }
                if (nowUtc < _gestureHoldCollapsedUntilUtc && (Math.Abs(cursor.X - _stationarySuppressOrigin.X) + Math.Abs(cursor.Y - _stationarySuppressOrigin.Y) >= 8))
                {
                    _gestureHoldCollapsedUntilUtc = DateTime.MinValue;
                }
                _wasInsidePill = insidePill;

                if (!insidePill)
                {
                    _suppressHoverUntilLeave = false;
                    _suppressStationaryExpand = false;
                    _hoverDwellSec = 0f;
                }
                else if (nowUtc < _gestureHoldCollapsedUntilUtc)
                {
                    _hoverDwellSec = 0f;
                }
                else if (!_state.OpenByClick && !_suppressHoverUntilLeave && !_suppressStationaryExpand)
                {
                    _hoverDwellSec += dt;
                    if (_hoverDwellSec >= 0.08f)
                    {
                        wantExpand = true;
                        _hoverDwellSec = 0f;
                    }
                }
            }

            if (wantExpand != _isExpanded)
            {
                _isExpanded = wantExpand;
                _hw.IsNotchExpanded = _isExpanded;

                if (_isExpanded)
                {
                    _isMicroPill = false;
                }
                else
                {
                    _hoveredId = null;
                    _isScrubbingSeek = false;
                    _shelfDragCandidate = -1;
                    _waterEditMode = false;
                    _isMicroPill = _alwaysMinimal || _autoMinimalObstruction;
                }

                UpdateTargetDimensions();
                _needsRedraw = true;
            }

            // ==================== 3. SMOOTH SPRING & STRICT SINGLE-WINNER FADES ====================
            float sprOmega = _state.FastAnimation ? 23.5f : 15.5f;
            float fadeSpd  = _state.FastAnimation ? 1.65f : 1.0f;

            bool mW = StepSpringDt(ref _curW, ref _velW, _targetW, dt, sprOmega, 0.84f);
            bool mH = StepSpringDt(ref _curH, ref _velH, _targetH, dt, sprOmega, 0.84f);
            bool mR = StepSpringDt(ref _curR, ref _velR, _targetR, dt, sprOmega + 1.0f, 0.86f);
            bool mE = StepSpringDt(ref _curE, ref _velE, _targetE, dt, sprOmega + 1.0f, 0.86f);

            float targetSleep  = (_petState == 2) ? 1.0f : 0.0f;
            float targetWave   = (_petState == 3) ? 1.0f : 0.0f;
            float targetExpand = _isExpanded ? 1.0f : 0.0f;

            bool osdNow = IsOsdActive;
            if (_wasOsdActive && !osdNow) { _wasOsdActive = false; UpdateAutoMinimalState(); UpdateTargetDimensions(); _needsRedraw = true; }
            float targetBar = (_osdKind == OsdKind.Volume && _osdMuted) ? 0f : (_osdPercent / 100.0f);
            bool dlNow = IsDownloadActive && !osdNow;
            float targetDlBar = _dlSnap.IsCompleted ? 1.0f : (_dlSnap.HasExactPercent ? Math.Max(0.03f, Math.Min(1.0f, _dlSnap.Progress01)) : 0.35f);

            int curPrivMask = 0;
            if (_state.PrivacyDotsEnabled && _osd != null)
            {
                if (_osd.IsCameraActive) curPrivMask |= 2;
                if (_osd.IsMicActive) curPrivMask |= 1;
            }
            if (curPrivMask != _lastPrivMask)
            {
                _lastPrivMask = curPrivMask;
                UpdateTargetDimensions();
                _needsRedraw = true;
            }
            float targetPriv = (curPrivMask != 0) ? 1.0f : 0.0f;

            bool fO = MoveTowards(ref _osdFade, osdNow ? 1.0f : 0.0f, dt * 8.5f * fadeSpd);
            bool fD = MoveTowards(ref _dlFade, dlNow ? 1.0f : 0.0f, dt * 8.0f * fadeSpd);
            bool fM = false;
            bool fG = false;
            int desiredColMode = osdNow ? 3 : ((dlNow && !_alwaysMinimal) ? 2 : ((_petState != 0) ? 1 : 0));
            if (_isExpanded)
            {
                if (_activeColMode != 0 || _colContentFade < 1.0f)
                {
                    _activeColMode = 0;
                    _colContentFade = 1.0f;
                }
            }
            else
            {
                if (_activeColMode != desiredColMode)
                {
                    fG = MoveTowards(ref _colContentFade, 0.0f, dt * 15.0f * fadeSpd);
                    if (_colContentFade <= 0.02f)
                    {
                        _activeColMode = desiredColMode;
                        _colContentFade = 0.02f;
                        fG = true;
                    }
                }
                else
                {
                    float inSpd = (_activeColMode == 1) ? 2.6f : 12.0f;
                    fG = MoveTowards(ref _colContentFade, 1.0f, dt * inSpd * fadeSpd);
                }
            }

            bool fS  = MoveTowards(ref _sleepFade,       targetSleep,  dt * 1.0f * fadeSpd);
            bool fW  = MoveTowards(ref _waveFade,        targetWave,   dt * 2.6f * fadeSpd);
            bool fE  = MoveTowards(ref _expandFade,      targetExpand, dt * 4.2f * fadeSpd);
            bool fB  = (osdNow || _osdFade > 0.01f) && MoveTowards(ref _osdAnimBar, targetBar, dt * 7.5f);
            bool fDE = MoveTowards(ref _dlExpBannerFade, 0.0f, dt * 8.0f * fadeSpd);
            bool fDB = (dlNow || _dlFade > 0.01f) && MoveTowards(ref _dlAnimBar, targetDlBar, dt * 5.5f);
            bool fP  = MoveTowards(ref _privacyFade,     targetPriv,   dt * 4.5f * fadeSpd);

            if (mW || mH || mR || mE || fG || fM || fS || fW || fE || fO || fB || fD || fDE || fDB || fP || _osdFade > 0.01f || _dlFade > 0.01f || _dlExpBannerFade > 0.01f)
            {
                _needsRedraw = true;
            }

            if (!_isExpanded && !_isMicroPill && !IsOsdActive && IsCollapsedLyricsActive)
            {
                double lpChk; bool synChk;
                string curLyr = _media.GetCollapsedLyricDisplay(out lpChk, out synChk) ?? "";
                if (!string.Equals(curLyr, _lastCollapsedLyricKey, StringComparison.Ordinal))
                {
                    _lastCollapsedLyricKey = curLyr;
                    UpdateTargetDimensions();
                    _needsRedraw = true;
                }
            }

            // Animate smoothly when Mascot is awake/waving OR when Music/Lyrics is active
            if (!_isFullscreenHidden && (
                (!_isExpanded && !_isMicroPill && (_petState == 1 || _waveFade > 0.01f || _media.IsPlaying || IsCollapsedLyricsActive || IsDownloadActive)) ||
                (_isExpanded && (IsDownloadActive || (_media.IsPlaying && (_pageIndex == 0 || _state.EqualizerEnabled))))))
            {
                _needsRedraw = true;
            }

            if (_needsRedraw)
            {
                _needsRedraw = false;
                RenderLayeredNotch();
            }
        }

        private void RenderLayeredNotch()
        {
            if (IsDisposed || !IsHandleCreated || _canvasBmp == null) return;

            float drawW = Math.Max(U(24f), Math.Min(_canvasW - U(50f), _curW));
            float drawH = (_curH < U(1.2f)) ? 0f : Math.Max(U(2.0f), Math.Min(_canvasH - U(18f), _curH));
            float drawR = Math.Max(U(2.0f), _curR);
            float drawE = Math.Max(U(1.5f), _curE);

            DrawNotchOnCanvas(_canvasBmp, _canvasW, drawW, drawH, drawR, drawE);

            IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
            IntPtr memDc = NativeMethods.CreateCompatibleDC(screenDc);
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr oldBitmap = IntPtr.Zero;

            try
            {
                hBitmap = _canvasBmp.GetHbitmap(Color.FromArgb(0));
                oldBitmap = NativeMethods.SelectObject(memDc, hBitmap);

                NativeMethods.SIZE size = new NativeMethods.SIZE { cx = _canvasW, cy = _canvasH };
                NativeMethods.POINT ptSrc = new NativeMethods.POINT { x = 0, y = 0 };
                NativeMethods.POINT ptDst = new NativeMethods.POINT { x = _canvasWinX, y = _canvasWinY };
                NativeMethods.BLENDFUNCTION blend = new NativeMethods.BLENDFUNCTION
                {
                    BlendOp = NativeMethods.AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = 255,
                    AlphaFormat = NativeMethods.AC_SRC_ALPHA
                };

                NativeMethods.UpdateLayeredWindow(Handle, screenDc, ref ptDst, ref size, memDc, ref ptSrc, 0, ref blend, NativeMethods.ULW_ALPHA);
            }
            finally
            {
                if (oldBitmap != IntPtr.Zero) NativeMethods.SelectObject(memDc, oldBitmap);
                if (hBitmap != IntPtr.Zero) NativeMethods.DeleteObject(hBitmap);
                NativeMethods.DeleteDC(memDc);
                NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        public void SavePreviewBitmap(string filePath, bool expanded, int pageIndex)
        {
            bool oldExp = _isExpanded;
            bool oldMic = _isMicroPill;
            bool oldMascot = _showRestingMascot;
            bool oldWave = _previewForceWave;
            bool oldSplit = _isLyricsSplit;
            int oldPage = _pageIndex;
            float oldExpFade = _expandFade;
            float oldGlaFade = _glanceFade;
            float oldMasFade = _mascotFade;
            float oldWavFade = _waveFade;
            float oldSlpFade = _sleepFade;
            float oldOsdFade = _osdFade;
            float oldDlFade = _dlFade;
            float oldDlExp = _dlExpBannerFade;
            int oldColMode = _activeColMode;
            float oldColFade = _colContentFade;
            float oldPrivFade = _privacyFade;

            _isExpanded = expanded;
            _isMicroPill = !expanded && (pageIndex == -2);
            _previewForceWave = !expanded && (pageIndex == -3);
            if (!expanded && pageIndex == -4) _isLyricsSplit = true;
            if (expanded && pageIndex == 0 && _state.DefaultLyricsSplit) _isLyricsSplit = true;

            if (_previewForceWave)
            {
                _showRestingMascot = true;
                _activeColMode = 1;
                _colContentFade = 1.0f;
                _waveFade = 1.0f;
                _sleepFade = 0.0f;
                _osdFade = 0.0f;
            }
            else if (!expanded && IsOsdActive)
            {
                _activeColMode = 3;
                _colContentFade = 1.0f;
                _osdFade = 1.0f;
            }
            else if (!expanded && IsDownloadActive && !_alwaysMinimal)
            {
                _activeColMode = 2;
                _colContentFade = 1.0f;
                _dlFade = 1.0f;
            }
            else
            {
                _activeColMode = 0;
                _colContentFade = 1.0f;
                _waveFade = 0.0f;
                _sleepFade = 0.0f;
                _osdFade = (expanded && IsOsdActive) ? 1.0f : 0.0f;
            }
            _privacyFade = IsPrivacyActive ? 1.0f : 0.0f;
            _expandFade = expanded ? 1.0f : 0.0f;
            if (pageIndex >= 0) _pageIndex = pageIndex;
            UpdateTargetDimensions();

            int bmpW = (int)Math.Ceiling(_targetW + _targetE * 2f + U(30f));
            int bmpH = (int)Math.Ceiling(Math.Max(U(36f), _targetH + U(30f)));

            using (Bitmap preview = new Bitmap(bmpW, bmpH, PixelFormat.Format32bppArgb))
            using (Graphics gp = Graphics.FromImage(preview))
            {
                using (LinearGradientBrush sky = new LinearGradientBrush(
                    new Rectangle(0, 0, bmpW, bmpH),
                    Color.FromArgb(245, 245, 245),
                    Color.FromArgb(230, 234, 242),
                    LinearGradientMode.Vertical))
                {
                    gp.FillRectangle(sky, 0, 0, bmpW, bmpH);
                }

                DrawNotchOnCanvas(preview, bmpW, _targetW, _targetH, _targetR, _targetE);
                preview.Save(filePath, ImageFormat.Png);
            }

            _isExpanded = oldExp;
            _isMicroPill = oldMic;
            _showRestingMascot = oldMascot;
            _previewForceWave = oldWave;
            _isLyricsSplit = oldSplit;
            _pageIndex = oldPage;
            _expandFade = oldExpFade;
            _glanceFade = oldGlaFade;
            _mascotFade = oldMasFade;
            _waveFade = oldWavFade;
            _sleepFade = oldSlpFade;
            _osdFade = oldOsdFade;
            _dlFade = oldDlFade;
            _dlExpBannerFade = oldDlExp;
            _activeColMode = oldColMode;
            _colContentFade = oldColFade;
            _privacyFade = oldPrivFade;
            UpdateTargetDimensions();
        }

        private void DrawNotchOnCanvas(Bitmap bmp, int canvasWidth, float bodyW, float bodyH, float r, float ear)
        {
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);

                _hitZones.Clear();
                if (bodyH < U(1.2f)) return;

                float x0 = (canvasWidth - bodyW) * 0.5f;

                if (bodyH > U(10f))
                {
                    float shScale = Math.Min(1.0f, (bodyH - U(10f)) / U(20f));
                    for (int i = 4; i >= 1; i--)
                    {
                        float sp = i * U(1.8f);
                        int shA = (int)((IsLight ? 9f : 18f) * (5 - i) * shScale);
                        if (shA <= 0) continue;
                        using (GraphicsPath spath = CreateNusaNotchPath(x0 - sp * 0.2f, bodyW + sp * 0.4f, bodyH + sp * 0.85f, r + sp * 0.3f, ear))
                        using (SolidBrush sb = new SolidBrush(Color.FromArgb(shA, 15, 23, 42)))
                        {
                            g.FillPath(sb, spath);
                        }
                    }
                }

                using (GraphicsPath path = CreateNusaNotchPath(x0, bodyW, bodyH, r, ear))
                {
                    Color shellFill = IsLight
                        ? ((bodyH <= U(9f)) ? Color.FromArgb(205, 100, 116, 145) : Color.FromArgb(255, 248, 250, 254))
                        : Color.FromArgb(255, 0, 0, 0);
                    using (SolidBrush shellBrush = new SolidBrush(shellFill))
                    {
                        g.FillPath(shellBrush, path);
                    }

                    GraphicsState clipState = g.Save();
                    g.SetClip(path);
                    g.PixelOffsetMode = PixelOffsetMode.Default;
                    g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

                    if (bodyH > U(12f))
                    {
                        float splitH = U(48f);
                        if (bodyH < splitH)
                        {
                            float colProg = (1.0f - SmoothStep01((bodyH - U(33f)) / U(15f))) * SmoothStep01((bodyH - U(11f)) / U(14f));
                            if (colProg > 0.01f)
                            {
                                GraphicsState stCol = g.Save();
                                g.TranslateTransform(0f, -(1.0f - colProg) * U(4f));
                                DrawCollapsedNusa(g, x0, bodyW, Math.Min(U(36f), bodyH));
                                g.Restore(stCol);

                                int veil = (int)((1.0f - colProg) * 255f);
                                if (veil > 2)
                                {
                                    using (SolidBrush vb = new SolidBrush(VeilCol(veil)))
                                    {
                                        g.FillRectangle(vb, x0 - ear, 0f, bodyW + ear * 2f, bodyH + 2f);
                                    }
                                }
                            }
                        }
                        else
                        {
                            float expProg = SmoothStep01((bodyH - splitH) / U(60f));
                            if (expProg > 0.01f)
                            {
                                GraphicsState stExp = g.Save();
                                float yOff = -(1.0f - expProg) * U(6f);
                                g.TranslateTransform(0f, yOff);
                                float expFullH = (_pageIndex == 0 && _isLyricsSplit && _state.ShowLyrics) ? U(152f) : U(140f);
                                DrawExpandedNusa(g, x0, bodyW, expFullH);
                                g.Restore(stExp);

                                int veil = (int)((1.0f - expProg) * 255f);
                                if (veil > 2)
                                {
                                    using (SolidBrush vb = new SolidBrush(VeilCol(veil)))
                                    {
                                        g.FillRectangle(vb, x0 - ear, 0f, bodyW + ear * 2f, bodyH + 2f);
                                    }
                                }
                            }
                            else
                            {
                                using (SolidBrush vb = new SolidBrush(VeilCol(255)))
                                {
                                    g.FillRectangle(vb, x0 - ear, 0f, bodyW + ear * 2f, bodyH + 2f);
                                }
                            }
                        }
                    }
                    else if (_privacyFade > 0.04f && bodyH >= U(3.5f))
                    {
                        DrawPrivacyDotsCluster(g, x0 + bodyW - U(5f), bodyH * 0.5f, _privacyFade, true);
                    }

                    g.Restore(clipState);

                    if (IsLight && bodyH > U(9f))
                    {
                        using (Pen borderPen = new Pen(Color.FromArgb(220, 198, 210, 228), U(1.1f)))
                        {
                            g.DrawPath(borderPen, path);
                        }
                    }
                }
            }
        }

        private GraphicsPath CreateNusaNotchPath(float x0, float w, float h, float r, float e)
        {
            GraphicsPath p = new GraphicsPath();
            e = Math.Min(e, h * 0.45f);
            r = Math.Min(r, Math.Min(w * 0.45f, h - e));

            p.StartFigure();
            p.AddLine(x0 - e, 0, x0 + w + e, 0);
            if (e > 0.8f) p.AddArc(x0 + w, 0, e * 2f, e * 2f, 270f, -90f);
            if (h - r > e) p.AddLine(x0 + w, e, x0 + w, h - r);
            if (r > 0.8f) p.AddArc(x0 + w - r * 2f, h - r * 2f, r * 2f, r * 2f, 0f, 90f);
            p.AddLine(x0 + w - r, h, x0 + r, h);
            if (r > 0.8f) p.AddArc(x0, h - r * 2f, r * 2f, r * 2f, 90f, 90f);
            if (h - r > e) p.AddLine(x0, h - r, x0, e);
            if (e > 0.8f) p.AddArc(x0 - e * 2f, 0, e * 2f, e * 2f, 0f, -90f);
            p.CloseFigure();
            return p;
        }

        // ==================== STAGE 2: COLLAPSED GLANCE PILL (STRICT SINGLE-ACTIVITY) ====================
        private void DrawCollapsedNusa(Graphics g, float x0, float w, float h)
        {
            float midY = h * 0.5f;
            int mode = _previewForceWave ? 1 : _activeColMode;
            float cAlpha = _previewForceWave ? 1.0f : SmoothStep01(_colContentFade);
            if (cAlpha <= 0.01f) return;

            if (mode == 3)
            {
                GraphicsState stOsd = g.Save();
                g.TranslateTransform(0f, -(1.0f - cAlpha) * U(2.0f));
                DrawOsdHudRow(g, x0 + U(14f), midY, Math.Max(U(80f), w - U(28f)), cAlpha);
                g.Restore(stOsd);
                return;
            }

            if (mode == 2 && !_alwaysMinimal)
            {
                GraphicsState stDl = g.Save();
                g.TranslateTransform(0f, -(1.0f - cAlpha) * U(2.0f));
                DrawDownloadHudRow(g, x0 + U(14f), midY, Math.Max(U(80f), w - U(28f)), cAlpha);
                g.Restore(stDl);
                return;
            }

            float mEase = (mode == 1) ? cAlpha : 0f;
            float wEase = SmoothStep01(_previewForceWave ? 1.0f : _waveFade);
            float sEase = SmoothStep01(_previewForceWave ? 0.0f : _sleepFade);
            if (mEase > 0.01f)
            {
                float glowIntensity = mEase * (1.0f - 0.35f * sEase);
                float riseFrac = 0.20f + (1.0f - mEase) * 0.55f;
                int yStart = Math.Max(1, (int)(h * riseFrac));
                int yEnd = Math.Max(yStart + 2, (int)Math.Ceiling(h));
                for (int py = yStart; py <= yEnd; py++)
                {
                    float t = (float)(py - yStart) / Math.Max(1f, (float)(yEnd - yStart));
                    float mask = (float)Math.Pow(t, 1.75);
                    int alpha = Math.Min(235, Math.Max(0, (int)(228f * mask * glowIntensity)));
                    if (alpha <= 1) continue;

                    using (LinearGradientBrush strip = new LinearGradientBrush(
                        new PointF(x0 - 2f, py),
                        new PointF(x0 + w + 2f, py),
                        Color.FromArgb(alpha, 12, 180, 252),
                        Color.FromArgb(alpha, 96, 68, 224)))
                    {
                        ColorBlend cb = new ColorBlend(3);
                        cb.Positions = new float[] { 0.0f, 0.50f, 1.0f };
                        cb.Colors = new Color[]
                        {
                            Color.FromArgb(alpha, 12, 180, 252),
                            Color.FromArgb(alpha, 42, 96, 240),
                            Color.FromArgb(alpha, 98, 68, 226)
                        };
                        strip.InterpolationColors = cb;
                        g.FillRectangle(strip, x0, py, w, 1.6f);
                    }
                }

                double sec = _frameClock.Elapsed.TotalSeconds;
                float riseOffsetY = (1.0f - mEase) * U(5.5f);

                float wanderFactor = (1.0f - wEase) * (1.0f - sEase);
                float lookX = (float)Math.Sin(sec * 1.1) * U(2.6f) * wanderFactor;
                float lookY = (float)Math.Cos(sec * 0.8) * U(0.8f) * wanderFactor;

                float baseLx = x0 + w * 0.5f - U(11.7f) - wEase * U(3.3f) + lookX;
                float baseRx = x0 + w * 0.5f + U(11.7f) - wEase * U(4.2f) + lookX;
                float ey = h * 0.51f + lookY + riseOffsetY;

                DrawMascotEyeHalo(g, baseLx, h * 0.49f + riseOffsetY, mEase);
                DrawMascotEyeHalo(g, baseRx, h * 0.49f + riseOffsetY, mEase);

                int eyeAlpha = Math.Min(255, Math.Max(0, (int)(255f * mEase)));

                if (wEase > 0.35f)
                {
                    DrawHappyArcEye(g, baseLx, ey, eyeAlpha);
                    DrawHappyArcEye(g, baseRx, ey, eyeAlpha);
                }
                else if (sEase > 0.55f)
                {
                    DrawSleepingArcEye(g, baseLx, ey + U(1.2f), (int)(eyeAlpha * 0.88f));
                    DrawSleepingArcEye(g, baseRx, ey + U(1.2f), (int)(eyeAlpha * 0.88f));
                }
                else
                {
                    double ms = (sec * 1000.0) % 4200.0;
                    float blink = 1.0f;
                    if (ms >= 3800.0 && ms <= 3970.0)
                    {
                        float phase = (float)((ms - 3800.0) / 170.0);
                        blink = Math.Max(0.12f, 1.0f - (float)Math.Sin(phase * Math.PI) * 0.90f);
                    }
                    float droop = 1.0f - sEase * 0.82f;
                    float eyeW = U(8.4f);
                    float eyeH = Math.Max(U(1.8f), U(12.4f) * blink * droop * mEase);

                    using (SolidBrush white = new SolidBrush((IsLight ? Color.FromArgb(eyeAlpha, 30, 41, 59) : Nw(eyeAlpha))))
                    {
                        g.FillEllipse(white, baseLx - eyeW * 0.5f, ey - eyeH * 0.5f, eyeW, eyeH);
                        g.FillEllipse(white, baseRx - eyeW * 0.5f, ey - eyeH * 0.5f, eyeW, eyeH);
                    }
                }

                if (wEase > 0.02f)
                {
                    float handY = h * 0.67f + (1.0f - wEase) * U(8.5f);
                    int handAlpha = Math.Min(255, Math.Max(0, (int)(255f * wEase * mEase)));
                    float waveAngle = _previewForceWave ? 12.0f : (float)(Math.Sin(sec * 7.8) * 15.0);
                    DrawWavingMascotHand(g, x0 + w * 0.5f + U(25.0f), handY, waveAngle, handAlpha);
                }
                if (_privacyFade > 0.02f && IsPrivacyActive)
                {
                    DrawPrivacyDotsCluster(g, x0 + w - U(9.5f), midY, SmoothStep01(_privacyFade) * mEase, false);
                }
                return;
            }

            float cEase = cAlpha;
            if (cEase <= 0.01f) return;

            GraphicsState stClock = g.Save();
            g.TranslateTransform(0f, -(1.0f - cEase) * U(2.5f));

            bool pomoLive = _state.PomodoroRunning;
            if (pomoLive)
            {
                DrawPomodoroMiniRing(g, x0 + U(19.0f), midY);
            }
            else if ((_media.HasMedia && _media.IsPlaying) && _batteryStateKind != 2)
            {
                RectangleF miniArt = new RectangleF(x0 + U(10.5f), midY - U(8.5f), U(17f), U(17f));
                DrawAlbumArtThumb(g, miniArt, U(4.8f));
            }
            else
            {
                DrawBatteryRing(g, x0 + U(19.0f), midY);
            }
            float leftContentX = x0 + U(33f);

            // Right-to-left non-overlapping indicator layout: Equalizer -> PrivacyDots -> Clock -> KaraokeLyrics
            float rightCursor = x0 + w - U(10.0f);
            if (_state.EqualizerEnabled)
            {
                DrawEqualizerBars(g, rightCursor - U(9.5f), midY);
                rightCursor -= U(24.5f);
            }
            if (_privacyFade > 0.02f && IsPrivacyActive)
            {
                float pAlpha = SmoothStep01(_privacyFade);
                DrawPrivacyDotsCluster(g, rightCursor, midY, pAlpha, false);
                float pSpan = (_osd != null && _osd.IsCameraActive && _osd.IsMicActive) ? U(19.5f) : U(11.0f);
                rightCursor -= pSpan * pAlpha;
            }

            string nowClock = pomoLive ? FormatMMSS(_state.PomodoroSecondsLeft) : DateTime.Now.ToString("HH:mm");
            Color clkCol = pomoLive ? PomoAccent : InkPri;
            SizeF clkSz = g.MeasureString(nowClock, _fIdleTime);
            bool showColLyrics = IsCollapsedLyricsActive && ((rightCursor - leftContentX) >= (clkSz.Width + U(68f)));

            if (showColLyrics)
            {
                double colPosSec;
                bool isSyn;
                string colFallback;
                LyricLine colLine = _media.GetActiveCollapsedLyricLine(out colPosSec, out isSyn, out colFallback);

                float clkCx = rightCursor - clkSz.Width * 0.5f;
                float divX = (float)Math.Round(rightCursor - clkSz.Width - U(5.5f));
                using (SolidBrush divB = new SolidBrush(IsLight ? Color.FromArgb(70, 30, 41, 59) : Color.FromArgb(68, 255, 255, 255)))
                {
                    g.FillRectangle(divB, divX, (float)Math.Round(midY - U(5.5f)), Math.Max(1f, U(1f)), (float)Math.Round(U(11f)));
                }
                DrawCenteredText(g, nowClock, _fIdleTime, clkCol, clkCx, midY);

                float availW = Math.Max(U(42f), (divX - U(6.5f)) - leftContentX);
                RectangleF lyrViewport = new RectangleF(leftContentX, 0f, availW, h);
                DrawKaraokeLyricLine(g, colLine, colFallback, colPosSec, isSyn, _fCollapsedLyric, lyrViewport, true);
            }
            else
            {
                float minCx = leftContentX + clkSz.Width * 0.5f + U(1.5f);
                float maxCx = rightCursor - clkSz.Width * 0.5f - U(1.5f);
                float clkCx = (maxCx > minCx)
                    ? Math.Max(minCx, Math.Min(maxCx, x0 + w * 0.5f))
                    : (leftContentX + rightCursor) * 0.5f;
                DrawCenteredText(g, nowClock, _fIdleTime, clkCol, clkCx, midY);
            }

            g.Restore(stClock);

            // Thin 2 dp song progress line along the bottom edge while music plays
            if (_media.HasMedia && _media.IsPlaying)
            {
                double pDur = _media.DurationSec;
                if (pDur > 0.5)
                {
                    float pFrac = (float)Math.Max(0.0, Math.Min(1.0, _media.GetLivePositionSec() / pDur));
                    float lx0 = x0 + U(17f);
                    float lw = Math.Max(U(20f), w - U(34f));
                    RectangleF pTrack = new RectangleF(lx0, h - U(3.0f), lw, U(2.0f));
                    int pa = (int)(255f * cEase);
                    FillPill(g, pTrack, Color.FromArgb((int)(46 * cEase), 255, 255, 255));
                    if (pFrac > 0.004f)
                    {
                        Color dom = _media.DominantColor;
                        Color pc = Color.FromArgb(Math.Max(0, Math.Min(255, pa)),
                            Math.Min(255, dom.R + 70), Math.Min(255, dom.G + 70), Math.Min(255, dom.B + 70));
                        FillPill(g, new RectangleF(pTrack.X, pTrack.Y, Math.Max(pTrack.Height, pTrack.Width * pFrac), pTrack.Height), pc);
                    }
                }
            }

            if (cEase < 0.99f)
            {
                int clockVeil = (int)((1.0f - cEase) * 255f);
                using (SolidBrush vb = new SolidBrush(VeilCol(clockVeil)))
                {
                    g.FillRectangle(vb, x0 - U(12f), 0f, w + U(24f), h + 2f);
                }
            }
        }

        private void DrawMascotEyeHalo(Graphics g, float cx, float cy, float alphaScale)
        {
            int centerA = Math.Min(255, Math.Max(0, (int)(145f * alphaScale)));
            if (centerA <= 1) return;
            float gw = U(18.5f), gh = U(25.5f);
            using (GraphicsPath gp = new GraphicsPath())
            {
                gp.AddEllipse(cx - gw * 0.5f, cy - gh * 0.5f, gw, gh);
                using (PathGradientBrush pgb = new PathGradientBrush(gp))
                {
                    pgb.CenterPoint = new PointF(cx, cy);
                    pgb.CenterColor = Color.FromArgb(centerA, 92, 166, 255);
                    pgb.SurroundColors = new Color[] { Color.FromArgb(0, 30, 88, 225) };
                    g.FillPath(pgb, gp);
                }
            }
        }

        private void DrawHappyArcEye(Graphics g, float ex, float ey, int alpha)
        {
            if (alpha <= 1) return;
            float hw = U(4.5f);
            using (Pen p = new Pen((IsLight ? Color.FromArgb(alpha, 30, 41, 59) : Nw(alpha)), U(2.45f)))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
                g.DrawBezier(p, new PointF(ex - hw, ey + U(1.8f)), new PointF(ex - U(1.7f), ey - U(4.2f)), new PointF(ex + U(1.7f), ey - U(4.2f)), new PointF(ex + hw, ey + U(1.8f)));
            }
        }

        private void DrawSleepingArcEye(Graphics g, float ex, float ey, int alpha)
        {
            if (alpha <= 1) return;
            float hw = U(4.2f);
            using (Pen p = new Pen((IsLight ? Color.FromArgb(alpha, 30, 41, 59) : Nw(alpha)), U(2.1f)))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                g.DrawBezier(p, new PointF(ex - hw, ey - U(1.2f)), new PointF(ex - U(1.6f), ey + U(2.6f)), new PointF(ex + U(1.6f), ey + U(2.6f)), new PointF(ex + hw, ey - U(1.2f)));
            }
        }

        private void DrawWavingMascotHand(Graphics g, float hx, float hy, float angleDeg, int alpha)
        {
            if (alpha <= 1) return;
            GraphicsState st = g.Save();
            g.TranslateTransform(hx, hy + U(2.0f));
            g.RotateTransform(angleDeg);
            g.TranslateTransform(0f, -U(2.0f));

            using (SolidBrush white = new SolidBrush((IsLight ? Color.FromArgb(alpha, 30, 41, 59) : Nw(alpha))))
            using (Pen fingerPen = new Pen((IsLight ? Color.FromArgb(alpha, 30, 41, 59) : Nw(alpha)), U(1.85f)))
            {
                fingerPen.StartCap = LineCap.Round;
                fingerPen.EndCap = LineCap.Round;
                g.FillEllipse(white, -U(3.5f), -U(2.3f), U(7.0f), U(5.3f));
                g.DrawLine(fingerPen, -U(2.0f), U(0.4f), -U(4.6f), -U(0.4f));
                g.DrawLine(fingerPen, -U(1.5f), -U(1.3f), -U(2.2f), -U(4.7f));
                g.DrawLine(fingerPen,  U(0.3f), -U(1.6f),  U(0.7f), -U(5.0f));
                g.DrawLine(fingerPen,  U(2.0f), -U(1.0f),  U(3.5f), -U(3.8f));
            }
            g.Restore(st);
        }

        private float MeasurePageHeaderLeftLimit(Graphics g, float x0, bool isSplitLyrics)
        {
            if (_pageIndex == 0)
            {
                return isSplitLyrics ? (x0 + U(30f)) : (x0 + U(105f));
            }
            if (_pageIndex == 1)
            {
                return x0 + U(15f) + g.MeasureString("Pomodoro", _fTitle).Width + U(8f);
            }
            string p1 = "System ", p2 = "Monitor";
            if (_pageIndex == 2) { p1 = "Water "; p2 = "Reminder"; }
            else if (_pageIndex == 3) { p1 = "File "; p2 = "Shelf"; }
            else if (_pageIndex == 4) { p1 = "Todo"; p2 = "List"; }
            float wTitle = g.MeasureString(p1, _fTitle).Width + g.MeasureString(p2, _fTitle).Width - U(3.5f);
            return x0 + U(15f) + wTitle + U(8f);
        }

        // ==================== STAGE 1: EXPANDED NUSA (FLAT INLINE HEADER — NO FLOATING POPUPS!) ====================
        private void DrawExpandedNusa(Graphics g, float x0, float w, float h)
        {
            bool isSplitLyrics = (_pageIndex == 0 && _isLyricsSplit && _state.ShowLyrics);
            float hdrY = isSplitLyrics ? U(15.5f) : U(17.8f);

            float gearX = _state.EqualizerEnabled ? (x0 + w - U(43f)) : (x0 + w - U(23f));
            float slotRight = gearX - U(13f);
            float slotLeft = MeasurePageHeaderLeftLimit(g, x0, isSplitLyrics);

            float badgeRightX = slotRight;
            float osdInline = SmoothStep01(_osdFade);
            // At most ONE flat inline status badge on the right — Battery & Clock ALWAYS stay visible & centered!
            if (osdInline > 0.03f)
            {
                badgeRightX = DrawOsdHeaderBadge(g, "osd_banner", badgeRightX, hdrY, osdInline, !isSplitLyrics) - U(6f);
            }
            else if (IsPrivacyActive)
            {
                badgeRightX = DrawPrivacyHeaderBadge(g, "hdr_privacy", badgeRightX, hdrY, !isSplitLyrics) - U(6f);
            }
            else if (IsDownloadActive)
            {
                badgeRightX = DrawDownloadHeaderBadge(g, "hdr_dl", badgeRightX, hdrY, !isSplitLyrics) - U(6f);
            }
            else if (UpdaterService.HasNewerRelease && !string.IsNullOrEmpty(UpdaterService.LatestTag))
            {
                string upTxt = "\u2B06 " + UpdaterService.LatestTag;
                SizeF upSz = g.MeasureString(upTxt, _fShelfSub);
                float upW = upSz.Width + U(11f);
                RectangleF upRect = new RectangleF(badgeRightX - upW, hdrY - U(8.5f), upW, U(17f));
                _hitZones.Add(new HitZone { Id = "hdr_update", Rect = upRect });
                bool upHov = (_hoveredId == "hdr_update");
                FillPill(g, upRect, upHov ? Color.FromArgb(230, 16, 185, 129) : Color.FromArgb(145, 5, 150, 105));
                DrawCenteredText(g, upTxt, _fShelfSub, Color.White, upRect.X + upRect.Width * 0.5f, hdrY);
                badgeRightX = upRect.X - U(6f);
            }

            string shortBat = string.Format(CultureInfo.InvariantCulture, "{0}%", _batteryPercent);
            string fullBat;
            Color batCol;
            if (_batteryStateKind == 1)
            {
                fullBat = string.Format(CultureInfo.InvariantCulture, "{0}% \u00B7 {1}", _batteryPercent, _state.T("\u0110ang s\u1EA1c", "Charging"));
                batCol = IsLight ? Color.FromArgb(5, 150, 105) : Color.FromArgb(74, 222, 128);
            }
            else if (_batteryStateKind == 2)
            {
                fullBat = string.Format(CultureInfo.InvariantCulture, "{0}% \u00B7 {1}", _batteryPercent, _state.T("Pin y\u1EBFu", "Low"));
                batCol = IsLight ? Color.FromArgb(220, 38, 38) : Color.FromArgb(255, 98, 98);
            }
            else
            {
                fullBat = string.Format(CultureInfo.InvariantCulture, "{0}% \u00B7 {1}", _batteryPercent, _state.T("Pin bth", "Normal"));
                batCol = Nw(215);
            }
            if (!_hasBattery)
            {
                int cpuNow = (int)Math.Round(Math.Max(0f, Math.Min(100f, _hw.CpuUsagePercent)));
                shortBat = string.Format(CultureInfo.InvariantCulture, "CPU {0}%", cpuNow);
                fullBat = string.Format(CultureInfo.InvariantCulture, "CPU {0}% \u00B7 {1:0.0} GHz", cpuNow, _hw.CpuSpeedGhz);
                batCol = Nw(215);
            }

            string dateStr = DateTime.Now.ToString("dd MMM ", CultureInfo.InvariantCulture);
            string timeStr = DateTime.Now.ToString(_showHeaderSeconds ? "HH:mm:ss" : "HH:mm");
            SizeF dSz = g.MeasureString(dateStr, _fHdrDate);
            SizeF tSz = g.MeasureString(timeStr, _fHdrTime);

            float availSpan = Math.Max(U(52f), badgeRightX - slotLeft);
            string batTxt = isSplitLyrics ? shortBat : fullBat;
            SizeF bSz = g.MeasureString(batTxt, _fAxis);
            if (!isSplitLyrics && (U(24f) + bSz.Width + dSz.Width + tSz.Width + U(8f) > availSpan))
            {
                batTxt = shortBat;
                bSz = g.MeasureString(batTxt, _fAxis);
            }
            bool drawDate = true;
            if (U(24f) + bSz.Width + dSz.Width + tSz.Width + U(6f) > availSpan)
            {
                drawDate = false;
                dSz = SizeF.Empty;
            }

            // Center Battery + Date + Clock symmetrically between left title and right status area!
            float visualClusterW = U(24f) + bSz.Width + (drawDate ? dSz.Width : 0f) + tSz.Width;
            float clusterStartX = isSplitLyrics
                ? Math.Max(slotLeft + U(4f), Math.Min(badgeRightX - visualClusterW - U(4f), x0 + (w - visualClusterW) * 0.5f))
                : (slotLeft + Math.Max(U(3f), (availSpan - visualClusterW) * 0.5f));
            float ringX = clusterStartX + U(7.5f);

            DrawBatteryRing(g, ringX, hdrY);
            float batX = ringX + U(10.5f);
            _hitZones.Add(new HitZone { Id = "hdr_bat", Rect = new RectangleF(ringX - U(8f), hdrY - U(11f), bSz.Width + U(20f), U(22f)) });
            DrawLeftVCenteredText(g, batTxt, _fAxis, batCol, batX, hdrY);

            float dateX = batX + bSz.Width + U(6f);
            float dateY = hdrY - _fHdrTime.Height * 0.5f;
            float dtW = (drawDate ? dSz.Width : 0f) + tSz.Width;
            _hitZones.Add(new HitZone { Id = "hdr_clock", Rect = new RectangleF(dateX - U(2f), hdrY - U(11f), dtW + U(6f), U(22f)) });
            using (SolidBrush bDate = new SolidBrush(ToCrispInk(Nw(_hoveredId == "hdr_clock" ? 255 : 210))))
            using (SolidBrush bTime = new SolidBrush(InkPri))
            {
                if (drawDate)
                {
                    g.DrawString(dateStr, _fHdrDate, bDate, (float)Math.Round(dateX), (float)Math.Round(hdrY - dSz.Height * 0.5f));
                    g.DrawString(timeStr, _fHdrTime, bTime, (float)Math.Round(dateX + dSz.Width), (float)Math.Round(dateY));
                }
                else
                {
                    g.DrawString(timeStr, _fHdrTime, bTime, (float)Math.Round(dateX), (float)Math.Round(dateY));
                }
            }

            DrawSettingsGearButton(g, "open_settings", gearX, hdrY);
            if (_state.EqualizerEnabled)
            {
                _hitZones.Add(new HitZone { Id = "hdr_eq", Rect = new RectangleF(x0 + w - U(32f), hdrY - U(11f), U(26f), U(22f)) });
                DrawEqualizerBars(g, x0 + w - U(20f), hdrY);
            }

            switch (_pageIndex)
            {
                case 0:
                    if (_isLyricsSplit && _state.ShowLyrics)
                        DrawLyricsSplitPage(g, x0, w, h);
                    else
                        DrawMusicPage(g, x0, w, h);
                    break;
                case 1:
                    DrawPomodoroPage(g, x0, w, h);
                    break;
                case 2:
                    DrawWaterPage(g, x0, w, h);
                    break;
                case 3:
                    DrawShelfPage(g, x0, w, h);
                    break;
                case 4:
                    DrawTodoPage(g, x0, w, h);
                    break;
                case 5:
                    DrawSystemMonitorPage(g, x0, w, h);
                    break;
            }

            float navCy = h * 0.5f;
            DrawNavArrow(g, "nav_prev", x0 + U(20f), navCy, true);
            DrawNavArrow(g, "nav_next", x0 + w - U(20f), navCy, false);
            DrawPageDots(g, x0 + w * 0.5f, h - U(7.5f));
        }

        // ==================== PAGE 0: REAL WINRT SMTC MUSIC & ALBUM ART ====================
        private void DrawMusicPage(Graphics g, float x0, float w, float h)
        {
            bool hasMedia = _media.HasMedia;

            // Ambient color glow from the real Album Art when music is open (1:1 Nusa!)
            if (hasMedia)
            {
                Color dom = _media.DominantColor;
                RectangleF ambRect = new RectangleF(x0, h * 0.18f, w, h * 0.82f);
                using (LinearGradientBrush amb = new LinearGradientBrush(
                    ambRect,
                    Color.FromArgb(0, dom.R, dom.G, dom.B),
                    Color.FromArgb(_media.IsPlaying ? 105 : 55, dom.R, dom.G, dom.B),
                    LinearGradientMode.Vertical))
                {
                    g.FillRectangle(amb, ambRect);
                }
            }

            RectangleF artRect = new RectangleF(x0 + U(40f), U(14f), U(58f), U(58f));
            DrawAlbumArtThumb(g, artRect, U(11f));

            // Click Album Art or Title/Artist to focus Spotify / YouTube / Browser!
            if (hasMedia)
            {
                _hitZones.Add(new HitZone { Id = "media_focus", Rect = new RectangleF(x0 + U(40f), U(14f), U(310f), U(58f)) });
            }

            string title = hasMedia && !string.IsNullOrEmpty(_media.CurrentTitle)
                ? _media.CurrentTitle
                : _state.T("Chưa phát nhạc", "Nothing playing");
            string artist = hasMedia
                ? (!string.IsNullOrEmpty(_media.CurrentArtist) ? _media.CurrentArtist : _state.T("Đang mở trình phát", "Media Player"))
                : _state.T("Mở nhạc trên YouTube, Spotify hoặc trình duyệt", "Play music on Spotify, YouTube or browser");

            Color titleCol = hasMedia ? InkPri : Nw(205);
            Color subCol = hasMedia ? Nw(155) : Nw(110);

            DrawClippedMarqueeText(g, title, _fMusicTitle, titleCol, new RectangleF(x0 + U(110f), U(33f), U(272f), U(22f)), hasMedia && _media.IsPlaying);
            DrawClippedMarqueeText(g, artist, _fMusicSub, subCol, new RectangleF(x0 + U(110f), U(54f), U(272f), U(18f)), false);

            double pos = hasMedia ? _media.GetLivePositionSec() : 0.0;
            double dur = hasMedia ? _media.DurationSec : 0.0;
            double rem = Math.Max(0.0, dur - pos);
            float pct = (hasMedia && dur > 0.5) ? (float)Math.Max(0.0, Math.Min(1.0, pos / dur)) : 0.0f;

            Color timeCol = hasMedia ? InkPri : Nw(95);
            DrawLeftVCenteredText(g, FormatMMSS((int)pos), _fTimeSmall, timeCol, x0 + U(34f), U(91f));
            string rightTime = (!hasMedia) ? "00:00" : (dur > 0.5 ? ("-" + FormatMMSS((int)rem)) : "LIVE");
            DrawRightVCenteredText(g, rightTime, _fTimeSmall, timeCol, x0 + U(386f), U(91f));

            bool seekHov = (_hoveredId == "media_seek") || _isScrubbingSeek;
            float barH = seekHov ? U(6.0f) : U(4.2f);
            RectangleF progTrack = new RectangleF(x0 + U(78f), U(91f) - barH * 0.5f, U(260f), barH);
            _lastSeekBarRect = progTrack;

            if (hasMedia && dur > 0.5)
            {
                _hitZones.Add(new HitZone { Id = "media_seek", Rect = new RectangleF(progTrack.X - U(4f), U(81f), progTrack.Width + U(8f), U(20f)) });
            }

            FillPill(g, progTrack, Nw(52));
            if (pct > 0.005f)
            {
                RectangleF progFill = new RectangleF(progTrack.X, progTrack.Y, Math.Max(barH, progTrack.Width * pct), progTrack.Height);
                FillPill(g, progFill, InkPri);
                if (seekHov)
                {
                    float kx = progTrack.X + progTrack.Width * pct;
                    using (SolidBrush kb = new SolidBrush(InkPri))
                    {
                        g.FillEllipse(kb, kx - U(5.5f), U(91f) - U(5.5f), U(11f), U(11f));
                    }
                }
            }
            if (seekHov && hasMedia && dur > 0.5)
            {
                DrawSeekHoverTooltip(g, progTrack, dur);
            }

            DrawLyricsBubbleIcon(g, "media_lyrics", x0 + U(56f), U(116f), _isLyricsSplit);
            DrawMediaPrevIcon(g, "media_prev", x0 + U(158f), U(115f));
            DrawMediaPlayPauseIcon(g, "media_play", x0 + U(208f), U(115f), _media.IsPlaying);
            DrawMediaNextIcon(g, "media_next", x0 + U(258f), U(115f));
            DrawMusicSpeakerButton(g, "media_vol", x0 + w - U(56f), U(115f));
        }

        // ==================== PAGE 0 SPLIT: REAL SYNCED LYRICS (598 x 152 dp) ====================
        private void DrawLyricsSplitPage(Graphics g, float x0, float w, float h)
        {
            bool hasMedia = _media.HasMedia;
            Color dom = hasMedia ? _media.DominantColor : Color.FromArgb(44, 100, 184);

            RectangleF ambRect = new RectangleF(x0, h * 0.22f, w, h * 0.78f);
            using (LinearGradientBrush amb = new LinearGradientBrush(
                ambRect,
                Color.FromArgb(0, dom.R, dom.G, dom.B),
                Color.FromArgb(hasMedia ? 138 : 60, dom.R, dom.G, dom.B),
                LinearGradientMode.Vertical))
            {
                g.FillRectangle(amb, ambRect);
            }

            RectangleF artRect = new RectangleF(x0 + U(36f), U(28f), U(52f), U(52f));
            DrawAlbumArtThumb(g, artRect, U(10f));
            if (hasMedia)
            {
                _hitZones.Add(new HitZone { Id = "media_focus", Rect = new RectangleF(x0 + U(36f), U(28f), U(192f), U(52f)) });
            }

            string title = hasMedia && !string.IsNullOrEmpty(_media.CurrentTitle)
                ? _media.CurrentTitle
                : _state.T("Chưa phát nhạc", "Nothing playing");
            string artist = hasMedia && !string.IsNullOrEmpty(_media.CurrentArtist)
                ? _media.CurrentArtist
                : _state.T("YouTube · Spotify", "YouTube · Spotify");

            // Clipped smooth marquee inside Left Song Column (x0+96 .. x0+226, 12dp clear of vertical divider at x0+238!)
            DrawClippedMarqueeText(g, title, _fMusicTitle, InkPri, new RectangleF(x0 + U(96f), U(33f), U(130f), U(22f)), hasMedia && _media.IsPlaying);
            DrawClippedMarqueeText(g, artist, _fMusicSub, Nw(155), new RectangleF(x0 + U(96f), U(55f), U(130f), U(18f)), false);

            double pos = hasMedia ? _media.GetLivePositionSec() : 0.0;
            double dur = hasMedia ? _media.DurationSec : 0.0;
            double rem = Math.Max(0.0, dur - pos);
            float pct = (hasMedia && dur > 0.5) ? (float)Math.Max(0.0, Math.Min(1.0, pos / dur)) : 0.0f;

            float seekCy = U(98f);
            DrawLeftVCenteredText(g, FormatMMSS((int)pos), _fTimeSmall, InkPri, x0 + U(36f), seekCy);
            bool seekHov = (_hoveredId == "media_seek") || _isScrubbingSeek;
            float barH = seekHov ? U(5.6f) : U(4.0f);
            RectangleF progTrack = new RectangleF(x0 + U(76f), seekCy - barH * 0.5f, U(106f), barH);
            _lastSeekBarRect = progTrack;
            if (hasMedia && dur > 0.5)
            {
                _hitZones.Add(new HitZone { Id = "media_seek", Rect = new RectangleF(progTrack.X - U(3f), seekCy - U(10f), progTrack.Width + U(6f), U(20f)) });
            }
            FillPill(g, progTrack, Nw(60));
            if (pct > 0.005f)
            {
                FillPill(g, new RectangleF(progTrack.X, progTrack.Y, Math.Max(barH, progTrack.Width * pct), progTrack.Height), InkPri);
                if (seekHov)
                {
                    float kx = progTrack.X + progTrack.Width * pct;
                    using (SolidBrush kb = new SolidBrush(InkPri))
                    {
                        g.FillEllipse(kb, kx - U(4.8f), seekCy - U(4.8f), U(9.6f), U(9.6f));
                    }
                }
            }
            DrawRightVCenteredText(g, (!hasMedia) ? "00:00" : (dur > 0.5 ? ("-" + FormatMMSS((int)rem)) : "LIVE"), _fTimeSmall, InkPri, x0 + U(226f), seekCy);
            if (seekHov && hasMedia && dur > 0.5)
            {
                DrawSeekHoverTooltip(g, progTrack, dur);
            }

            float btnCy = U(124f);
            DrawLyricsBubbleIcon(g, "media_lyrics", x0 + U(50f), btnCy, true);
            DrawMediaPrevIcon(g, "media_prev", x0 + U(88f), btnCy);
            DrawMediaPlayPauseIcon(g, "media_play", x0 + U(126f), btnCy, _media.IsPlaying);
            DrawMediaNextIcon(g, "media_next", x0 + U(164f), btnCy);
            DrawMusicSpeakerButton(g, "media_vol", x0 + U(204f), btnCy);

            using (SolidBrush div = new SolidBrush(Nw(42)))
            {
                g.FillRectangle(div, x0 + U(238f), U(30f), Math.Max(1f, U(1f)), U(102f));
            }

            float paneX = x0 + U(250f);
            float paneW = Math.Max(U(140f), w - U(250f) - U(34f));

            if (!hasMedia)
            {
                _lastDisplayedLyrics = new LyricLine[0];
                DrawLeftVCenteredText(g, _state.T("♪ Phát một bài hát để xem", "♪ Play a song to see"), _fLyricOff, Nw(140), paneX, U(66f));
                DrawLeftVCenteredText(g, _state.T("lời bài hát Karaoke đồng bộ LRCLIB.", "word-by-word LRCLIB synced lyrics."), _fLyricOff, Nw(105), paneX, U(89f));
                return;
            }

            string curTrackKey = (_media.CurrentArtist ?? "") + "|" + (_media.CurrentTitle ?? "");
            if (!string.Equals(curTrackKey, _lyricScrollTrackKey, StringComparison.Ordinal))
            {
                _lyricScrollTrackKey = curTrackKey;
                _lyricScrollOffset = 0;
                _lyricScrollUntilUtc = DateTime.MinValue;
            }

            bool manualScrolling = (DateTime.UtcNow < _lyricScrollUntilUtc) && (_lyricScrollOffset != 0);
            if (!manualScrolling) _lyricScrollOffset = 0;

            int activeSlot;
            int clampedScroll;
            LyricLine[] win = _media.GetDisplayLyricsWindow(manualScrolling ? _lyricScrollOffset : 0, out activeSlot, out clampedScroll);
            if (manualScrolling) _lyricScrollOffset = clampedScroll;
            _lastDisplayedLyrics = win;

            if (win.Length == 0)
            {
                string stMsg = !string.IsNullOrEmpty(_media.CurrentLyricLine) ? _media.CurrentLyricLine : "Loading lyrics...";
                GraphicsState stEmpty = g.Save();
                g.SetClip(new RectangleF(paneX, U(56f), paneW, U(38f)), CombineMode.Intersect);
                DrawLeftVCenteredText(g, stMsg, _fLyricOff, Nw(165), paneX, U(75f));
                g.Restore(stEmpty);
            }
            else
            {
                double effPos = _media.GetEffectiveLyricsPosSec();
                float lyY = U(41.5f);
                for (int i = 0; i < win.Length; i++)
                {
                    bool active = (i == activeSlot);
                    string lid = "lyric_seek_" + i;
                    RectangleF lRect = new RectangleF(paneX - U(4f), lyY - U(11f), paneW + U(6f), U(22f));
                    _hitZones.Add(new HitZone { Id = lid, Rect = lRect });
                    bool lHov = (_hoveredId == lid);

                    if (lHov) FillRoundRect(g, lRect, U(6f), Nw(30));

                    float availLineW = paneW;
                    if (lHov && dur > 0.5)
                    {
                        double seekTarget = Math.Max(0.0, win[i].TimeSec + _media.TotalLyricsOffsetSec);
                        string tsBadge = FormatMMSS((int)seekTarget);
                        SizeF tsSz = g.MeasureString(tsBadge, _fShelfSub);
                        availLineW = Math.Max(U(80f), paneW - tsSz.Width - U(10f));
                        DrawRightVCenteredText(g, tsBadge, _fShelfSub, IsLight ? Color.FromArgb(2, 132, 199) : Color.FromArgb(125, 211, 252), paneX + paneW, lyY);
                    }

                    string fullTxt = win[i].Text ?? "";
                    Font fPrimary = active ? _fLyricOn : _fLyricOff;
                    Font fCompact = active ? _fLyricOnFit : _fLyricOffFit;
                    Font useFont = fPrimary;
                    SizeF lSz = g.MeasureString(fullTxt, useFont);
                    if (lSz.Width > availLineW + U(2f))
                    {
                        useFont = fCompact;
                        lSz = g.MeasureString(fullTxt, useFont);
                    }

                    RectangleF lineViewport = new RectangleF(paneX, lyY - U(11f), availLineW, U(22f));
                    if (active)
                    {
                        DrawKaraokeLyricLine(g, win[i], fullTxt, effPos, true, useFont, lineViewport, false);
                    }
                    else
                    {
                        Color lineCol = lHov
                            ? ToCrispInk(Nw(232))
                            : ((activeSlot >= 0 && i < activeSlot) ? ToCrispInk(Nw(142)) : ToCrispInk(Nw(100)));
                        DrawClippedMarqueeText(g, fullTxt, useFont, lineCol, lineViewport, lHov);
                    }
                    lyY += U(24.5f);
                }
            }
        }

        private void DrawClippedMarqueeText(Graphics g, string text, Font font, Color col, RectangleF viewport, bool animateWhenLong)
        {
            if (string.IsNullOrEmpty(text) || viewport.Width <= 2f) return;
            using (StringFormat sf = (StringFormat)StringFormat.GenericTypographic.Clone())
            using (SolidBrush br = new SolidBrush(ToCrispInk(col)))
            {
                sf.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
                SizeF sz = g.MeasureString(text, font, 10000, sf);
                float drawY = (float)Math.Round(viewport.Y + (viewport.Height - sz.Height) * 0.5f);
                float drawX = (float)Math.Round(viewport.X);

                if (sz.Width > viewport.Width + U(1.5f) && animateWhenLong)
                {
                    float maxShift = sz.Width - viewport.Width + U(10f);
                    double rawWave = 0.5 - 0.5 * Math.Cos(_frameClock.Elapsed.TotalSeconds * 0.72);
                    float plateau = (float)Math.Max(0.0, Math.Min(1.0, rawWave * 1.36 - 0.18));
                    drawX = (float)Math.Round(viewport.X - maxShift * plateau);
                }

                GraphicsState st = g.Save();
                g.SetClip(viewport, CombineMode.Intersect);
                g.DrawString(text, font, br, new PointF(drawX, drawY), sf);
                g.Restore(st);
            }
        }

        private void DrawSeekHoverTooltip(Graphics g, RectangleF progTrack, double durSec)
        {
            if (progTrack.Width <= 6f || durSec <= 0.5) return;
            float clampedX = Math.Max(progTrack.X, Math.Min(progTrack.Right, (float)_lastHoverPt.X));
            double frac = (clampedX - progTrack.X) / progTrack.Width;
            string tip = FormatMMSS((int)Math.Round(frac * durSec));
            SizeF tSz = g.MeasureString(tip, _fShelfSub);
            float bw = tSz.Width + U(10f);
            float bh = U(15.5f);
            float bx = Math.Max(progTrack.X - U(4f), Math.Min(progTrack.Right - bw + U(4f), clampedX - bw * 0.5f));
            RectangleF tipRect = new RectangleF(bx, progTrack.Y - bh - U(5.5f), bw, bh);
            FillPill(g, tipRect, IsLight ? Color.FromArgb(238, 15, 23, 42) : Color.FromArgb(236, 30, 41, 59));
            DrawCenteredText(g, tip, _fShelfSub, Color.White, tipRect.X + tipRect.Width * 0.5f, tipRect.Y + bh * 0.5f);
        }

        private void DrawKaraokeLyricLine(Graphics g, LyricLine line, string fallbackTxt, double posSec, bool isSynced, Font font, RectangleF viewport, bool centerWhenFits)
        {
            string txt = (line != null && !string.IsNullOrEmpty(line.Text)) ? line.Text : (fallbackTxt ?? "");
            if (string.IsNullOrEmpty(txt)) return;

            using (StringFormat sfDraw = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                sfDraw.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
                SizeF fullSz = g.MeasureString(txt, font, 10000, sfDraw);
                float totalW = fullSz.Width;
                float sweepPx = totalW + U(4f);

                if (isSynced && line != null && line.Words != null && line.Words.Length > 0)
                {
                    LyricWordSpan[] words = line.Words;
                    if (posSec <= words[0].StartSec)
                    {
                        sweepPx = 0f;
                    }
                    else if (posSec >= words[words.Length - 1].EndSec)
                    {
                        sweepPx = totalW + U(4f);
                    }
                    else
                    {
                        int wIdx = 0;
                        for (int k = 0; k < words.Length; k++)
                        {
                            wIdx = k;
                            if (posSec < words[k].EndSec) break;
                        }
                        LyricWordSpan ws = words[wIdx];
                        float u = (posSec <= ws.StartSec)
                            ? 0f
                            : (float)Math.Max(0.0, Math.Min(1.0, (posSec - ws.StartSec) / Math.Max(0.03, ws.EndSec - ws.StartSec)));

                        float wLeft = 0f;
                        float wRight = totalW;
                        int cStart = Math.Max(0, Math.Min(txt.Length - 1, ws.CharStart));
                        int cLen = Math.Max(1, Math.Min(txt.Length - cStart, ws.CharLength));
                        try
                        {
                            using (StringFormat sfMeasure = (StringFormat)StringFormat.GenericTypographic.Clone())
                            {
                                sfMeasure.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
                                sfMeasure.SetMeasurableCharacterRanges(new CharacterRange[] { new CharacterRange(cStart, cLen) });
                                Region[] regs = g.MeasureCharacterRanges(txt, font, new RectangleF(0f, 0f, 10000f, fullSz.Height + 24f), sfMeasure);
                                if (regs != null && regs.Length > 0 && regs[0] != null)
                                {
                                    RectangleF wb = regs[0].GetBounds(g);
                                    wLeft = wb.X;
                                    wRight = wb.Right;
                                    regs[0].Dispose();
                                }
                            }
                        }
                        catch { }

                        sweepPx = wLeft + (wRight - wLeft) * u;
                    }
                }

                float midY = viewport.Y + viewport.Height * 0.5f;
                float drawY = (float)Math.Round(midY - fullSz.Height * 0.5f);
                float drawX;
                if (totalW <= viewport.Width + U(1.5f))
                {
                    drawX = centerWhenFits
                        ? (float)Math.Round(viewport.X + (viewport.Width - totalW) * 0.5f)
                        : (float)Math.Round(viewport.X);
                }
                else
                {
                    float maxShift = totalW - viewport.Width + U(8f);
                    float shift;
                    if (isSynced)
                    {
                        shift = Math.Max(0f, Math.Min(maxShift, sweepPx - viewport.Width * 0.44f));
                    }
                    else
                    {
                        float pan = (float)(0.5 - 0.5 * Math.Cos(_frameClock.Elapsed.TotalSeconds * 0.85));
                        shift = maxShift * pan;
                    }
                    drawX = (float)Math.Round(viewport.X - shift);
                }

                Color unsungCol = IsLight ? Color.FromArgb(255, 138, 150, 170) : Color.FromArgb(255, 122, 134, 154);
                Color sungCol   = InkPri;

                // Strictly disjoint Left (Sung) and Right (Unsung) clips — every glyph pixel is rendered ONCE with zero ghosting!
                float splitX = Math.Max(viewport.X, Math.Min(viewport.Right, drawX + sweepPx));

                if (splitX > viewport.X + 0.3f)
                {
                    GraphicsState stSung = g.Save();
                    g.SetClip(viewport, CombineMode.Intersect);
                    g.SetClip(new RectangleF(viewport.X - 2f, viewport.Y - 4f, (splitX - viewport.X) + 2f, viewport.Height + 8f), CombineMode.Intersect);
                    using (SolidBrush bSung = new SolidBrush(sungCol))
                    {
                        g.DrawString(txt, font, bSung, new PointF(drawX, drawY), sfDraw);
                    }
                    g.Restore(stSung);
                }

                if (splitX < viewport.Right - 0.3f)
                {
                    GraphicsState stUnsung = g.Save();
                    g.SetClip(viewport, CombineMode.Intersect);
                    g.SetClip(new RectangleF(splitX, viewport.Y - 4f, (viewport.Right - splitX) + 2f, viewport.Height + 8f), CombineMode.Intersect);
                    using (SolidBrush bUnsung = new SolidBrush(unsungCol))
                    {
                        g.DrawString(txt, font, bUnsung, new PointF(drawX, drawY), sfDraw);
                    }
                    g.Restore(stUnsung);
                }
            }
        }

        // ==================== PAGE 1: INTERACTIVE POMODORO ====================
        private void DrawPomodoroPage(Graphics g, float x0, float w, float h)
        {
            DrawLeftVCenteredText(g, "Pomodoro", _fTitle, InkPri, x0 + U(15f), U(19f));

            float cx = x0 + U(80f);
            float cy = U(74f);
            float ringR = U(32f);

            // Clicking the timer ring or big digits toggles Start/Pause!
            _hitZones.Add(new HitZone { Id = "pomo_toggle", Rect = new RectangleF(cx - U(36f), cy - U(36f), U(72f), U(72f)) });

            using (GraphicsPath gp = new GraphicsPath())
            {
                gp.AddEllipse(cx - U(44f), cy - U(44f), U(88f), U(88f));
                using (PathGradientBrush pgb = new PathGradientBrush(gp))
                {
                    pgb.CenterColor = Color.FromArgb(80, 255, 60, 60);
                    pgb.SurroundColors = new Color[] { Color.FromArgb(0, 255, 60, 60) };
                    g.FillPath(pgb, gp);
                }
            }

            int modeTotalMin = (_state.PomodoroMode == 0) ? _state.FocusMinutes : (_state.PomodoroMode == 1 ? _state.ShortBreakMinutes : _state.LongBreakMinutes);
            float totalSec = Math.Max(1, modeTotalMin * 60);
            float pct = Math.Max(0.04f, Math.Min(1f, _state.PomodoroSecondsLeft / totalSec));

            using (Pen bgPen = new Pen(Color.FromArgb(60, 255, 90, 110), U(6.2f)))
            using (Pen fgPen = new Pen(Color.FromArgb(255, 255, 75, 85), U(6.2f)))
            {
                fgPen.StartCap = LineCap.Round;
                fgPen.EndCap = LineCap.Round;
                RectangleF arcRect = new RectangleF(cx - ringR + U(3f), cy - ringR + U(3f), (ringR - U(3f)) * 2f, (ringR - U(3f)) * 2f);
                g.DrawEllipse(bgPen, arcRect);
                g.DrawArc(fgPen, arcRect, -90f, 360f * pct);
            }

            using (Pen wp = new Pen(InkPri, U(1.8f)))
            {
                wp.StartCap = LineCap.Round; wp.EndCap = LineCap.Round;
                float sr = U(9.5f);
                g.DrawEllipse(wp, cx - sr, cy - sr + U(2f), sr * 2f, sr * 2f);
                g.DrawLine(wp, cx, cy + U(2f), cx, cy - U(3.5f));
                g.DrawLine(wp, cx - U(3f), cy - sr - U(1.5f), cx + U(3f), cy - sr - U(1.5f));
                g.DrawLine(wp, cx, cy - sr - U(1.5f), cx, cy - sr + U(1.5f));
            }

            // '-' and '+' duration buttons right beside big countdown + 'Reset' button!
            DrawMiniCircleBtn(g, "pomo_minus", x0 + U(192f), U(50f), "−");

            int mins = _state.PomodoroSecondsLeft / 60;
            int secs = _state.PomodoroSecondsLeft % 60;
            string timeText = string.Format("{0:00}:{1:00}", mins, secs);
            _hitZones.Add(new HitZone { Id = "pomo_toggle", Rect = new RectangleF(x0 + U(210f), U(33f), U(92f), U(34f)) });
            DrawCenteredText(g, timeText, _fHeroBig, InkPri, x0 + U(256f), U(50f));

            DrawMiniCircleBtn(g, "pomo_plus", x0 + U(320f), U(50f), "+");
            DrawMiniCircleBtn(g, "pomo_reset", x0 + U(350f), U(50f), "↺");

            string modeSub = (_state.PomodoroMode == 0)
                ? _state.T("Phiên tập trung (Focus)", "Focus time")
                : (_state.PomodoroMode == 1 ? _state.T("Nghỉ ngắn (Short break)", "Short break") : _state.T("Nghỉ dài (Long break)", "Long break"));
            DrawCenteredText(g, modeSub, _fMusicSub, Nw(145), x0 + U(256f), U(76f));

            DrawNusaPillButton(g, "pomo_focus", new RectangleF(x0 + U(138f), U(92f), U(64f), U(28f)), "Focus",
                _state.PomodoroMode == 0 ? Color.FromArgb(110, 255, 75, 85) : Nw(32));
            DrawPomoPlayButton(g, "pomo_toggle", new RectangleF(x0 + U(208f), U(92f), U(52f), U(28f)), _state.PomodoroRunning);
            DrawNusaPillButton(g, "pomo_short", new RectangleF(x0 + U(266f), U(92f), U(60f), U(28f)), "Short",
                _state.PomodoroMode == 1 ? Color.FromArgb(110, 255, 75, 85) : Nw(32));
            DrawNusaPillButton(g, "pomo_long", new RectangleF(x0 + U(332f), U(92f), U(56f), U(28f)), "Long",
                _state.PomodoroMode == 2 ? Color.FromArgb(110, 255, 75, 85) : Nw(32));
        }

        private void DrawMiniCircleBtn(Graphics g, string id, float cx, float cy, string sym)
        {
            RectangleF r = new RectangleF(cx - U(11.5f), cy - U(11.5f), U(23f), U(23f));
            _hitZones.Add(new HitZone { Id = id, Rect = r });
            bool hov = (_hoveredId == id);
            using (SolidBrush sb = new SolidBrush(Nw(hov ? 82 : 34)))
            {
                g.FillEllipse(sb, r);
            }
            DrawCenteredText(g, sym, _fPill, InkPri, cx, cy);
        }

        // ==================== PAGE 2: FULLY INTERACTIVE WATER REMINDER ====================
        private void DrawWaterPage(Graphics g, float x0, float w, float h)
        {
            DrawTwoToneTitle(g, "Water ", "Reminder", Color.FromArgb(86, 200, 255), x0 + U(15f), U(19f));

            float liters = _state.WaterMlToday / 1000.0f;
            float goalL = _state.WaterGoalMl / 1000.0f;
            int pct = Math.Min(100, (int)Math.Round((_state.WaterMlToday * 100.0) / Math.Max(1, _state.WaterGoalMl)));

            string bigL = liters.ToString("0.0", CultureInfo.InvariantCulture);
            string goalStr = string.Format(CultureInfo.InvariantCulture, " / {0:0.0} L", goalL);
            SizeF bigSz = g.MeasureString(bigL, _fHeroBig);
            float baseY = U(47f) - bigSz.Height * 0.5f;

            // Clicking the goal readout also toggles inline Plan editor!
            _hitZones.Add(new HitZone { Id = "water_plan_toggle", Rect = new RectangleF(x0 + U(38f), U(31f), U(118f), U(32f)) });

            using (SolidBrush wBr = new SolidBrush(InkPri))
            using (SolidBrush dBr = new SolidBrush(ToCrispInk(Nw(180))))
            {
                g.DrawString(bigL, _fHeroBig, wBr, (float)Math.Round(x0 + U(40f)), (float)Math.Round(baseY));
                g.DrawString(goalStr, _fHeroUnit, dBr, (float)Math.Round(x0 + U(40f) + bigSz.Width - U(4f)), (float)Math.Round(U(49f) - _fHeroUnit.Height * 0.5f));
            }

            // Interactive Reminder Toggle Switch!
            RectangleF swRect = new RectangleF(x0 + U(162f), U(37f), U(36f), U(20f));
            _hitZones.Add(new HitZone { Id = "water_toggle", Rect = new RectangleF(swRect.X - U(4f), swRect.Y - U(4f), swRect.Width + U(8f), swRect.Height + U(8f)) });
            bool remOn = _state.WaterRemindersEnabled;
            FillPill(g, swRect, remOn ? Color.FromArgb(38, 153, 255) : (IsLight ? Color.FromArgb(203, 213, 225) : Color.FromArgb(62, 68, 86)));
            float kx = remOn ? (swRect.Right - U(18f)) : (swRect.X + U(2f));
            using (SolidBrush wb = new SolidBrush(Color.FromArgb(255, 255, 255)))
            {
                g.FillEllipse(wb, kx, swRect.Y + U(2f), U(16f), U(16f));
            }

            string statusLine = !remOn
                ? _state.T("Đã tắt nhắc · Vẫn ghi nhật ký", "Reminders paused")
                : (pct >= 100
                    ? string.Format("Today · {0}% · Goal reached!", pct)
                    : string.Format("Today · {0}% · +{1}ml/drink", pct, _state.WaterCupSizeMl));
            GraphicsState stWCol = g.Save();
            g.SetClip(new RectangleF(x0 + U(40f), U(60f), U(162f), U(20f)), CombineMode.Intersect);
            DrawLeftVCenteredText(g, statusLine, _fMusicSub, Nw(155), x0 + U(44f), U(71f));
            g.Restore(stWCol);

            RectangleF wbarTrack = new RectangleF(x0 + U(44f), U(82.5f), U(152f), U(5f));
            FillPill(g, wbarTrack, Nw(38));
            if (pct > 0)
            {
                float fillW = Math.Max(U(6f), wbarTrack.Width * (pct / 100.0f));
                RectangleF wbarFill = new RectangleF(wbarTrack.X, wbarTrack.Y, fillW, wbarTrack.Height);
                using (LinearGradientBrush lgb = new LinearGradientBrush(wbarTrack, Color.FromArgb(102, 219, 255), Color.FromArgb(45, 107, 255), LinearGradientMode.Horizontal))
                {
                    FillPillBrush(g, wbarFill, lgb);
                }
            }

            // Bottom buttons: [+ Drink], [-], [⚙ Plan]
            DrawNusaPillButton(g, "water_drink", new RectangleF(x0 + U(42f), U(98f), U(64f), U(26f)), "+ Drink", Color.FromArgb(255, 22, 102, 192));
            DrawNusaPillButton(g, "water_undo", new RectangleF(x0 + U(110f), U(98f), U(28f), U(26f)), "−", Nw(38));
            DrawNusaPillButton(g, "water_plan_toggle", new RectangleF(x0 + U(142f), U(98f), U(56f), U(26f)),
                _waterEditMode ? "✓ Done" : "Plan", _waterEditMode ? Color.FromArgb(180, 38, 153, 255) : Nw(38));

            using (SolidBrush div = new SolidBrush(Nw(32)))
            {
                g.FillRectangle(div, x0 + U(208f), U(34f), Math.Max(1f, U(1f)), U(90f));
            }

            if (_waterEditMode)
            {
                // Inline Nusa Water Plan Editor right inside the Notch!
                DrawLeftVCenteredText(g, "Daily Goal:", _fAxis, Nw(165), x0 + U(216f), U(37.5f));
                int[] goals = new int[] { 1500, 2000, 2500, 3000 };
                string[] gLabels = new string[] { "1.5L", "2.0L", "2.5L", "3.0L" };
                for (int i = 0; i < 4; i++)
                {
                    bool sel = (_state.WaterGoalMl == goals[i]);
                    DrawNusaPillButton(g, "wgoal_set_" + i, new RectangleF(x0 + U(216f + i * 42f), U(46.5f), U(39f), U(21.5f)),
                        gLabels[i], sel ? Color.FromArgb(220, 37, 99, 235) : Nw(34));
                }

                DrawLeftVCenteredText(g, "Cup Size:", _fAxis, Nw(165), x0 + U(216f), U(77f));
                int[] cups = new int[] { 200, 250, 350, 500 };
                for (int i = 0; i < 4; i++)
                {
                    bool sel = (_state.WaterCupSizeMl == cups[i]);
                    DrawNusaPillButton(g, "wcup_set_" + i, new RectangleF(x0 + U(216f + i * 42f), U(86f), U(39f), U(21.5f)),
                        cups[i].ToString(), sel ? Color.FromArgb(220, 14, 165, 233) : Nw(34));
                }

                DrawNusaPillButton(g, "water_reset_zero", new RectangleF(x0 + U(216f), U(111.5f), U(165f), U(17.5f)),
                    _state.T("Đặt lại hôm nay về 0 ml", "Reset today to 0 ml"), Color.FromArgb(45, 239, 68, 68));
            }
            else
            {
                // Real Hourly Hydration Chart (08h..20h)
                using (Pen dotPen = new Pen(Nw(50), 1f))
                {
                    dotPen.DashStyle = DashStyle.Dot;
                    g.DrawLine(dotPen, x0 + U(220f), U(56f), x0 + U(336f), U(56f));
                    g.DrawLine(dotPen, x0 + U(220f), U(81f), x0 + U(336f), U(81f));
                }

                int[] sampleHours = new int[] { 8, 10, 12, 14, 16, 18, 20 };
                for (int i = 0; i < sampleHours.Length; i++)
                {
                    int h0 = sampleHours[i];
                    int mlBucket = _state.WaterHourlyMl[h0] + (h0 + 1 < 24 ? _state.WaterHourlyMl[h0 + 1] : 0);
                    float frac = Math.Min(1.0f, mlBucket / 500.0f);
                    float bx = x0 + U(224f + i * 16f);
                    float bh = Math.Max(U(3.5f), U(48f) * frac);
                    RectangleF bRect = new RectangleF(bx, U(106f) - bh, U(7f), bh);

                    if (mlBucket > 0)
                    {
                        using (LinearGradientBrush bb = new LinearGradientBrush(new RectangleF(bx, U(54f), U(7f), U(54f)), Color.FromArgb(102, 219, 255), Color.FromArgb(45, 107, 255), LinearGradientMode.Vertical))
                        {
                            FillPillBrush(g, bRect, bb);
                        }
                    }
                    else
                    {
                        FillPill(g, bRect, Nw(35));
                    }
                }

                using (SolidBrush axBlue = new SolidBrush(Color.FromArgb(115, 80, 180, 255)))
                using (SolidBrush axVert = new SolidBrush(Nw(46)))
                {
                    g.FillRectangle(axBlue, x0 + U(216f), U(106f), U(122f), Math.Max(1f, U(1f)));
                    g.FillRectangle(axVert, x0 + U(338f), U(50f), Math.Max(1f, U(1f)), U(56f));
                }

                Color dimAx = Nw(130);
                DrawLeftVCenteredText(g, "500", _fAxis, dimAx, x0 + U(344f), U(56f));
                DrawLeftVCenteredText(g, "250", _fAxis, dimAx, x0 + U(344f), U(81f));
                DrawLeftVCenteredText(g, "0 ml", _fAxis, dimAx, x0 + U(344f), U(106f));
                DrawCenteredText(g, "08h", _fAxis, dimAx, x0 + U(227f), U(117f));
                DrawCenteredText(g, "14h", _fAxis, dimAx, x0 + U(275f), U(117f));
                DrawCenteredText(g, "20h", _fAxis, dimAx, x0 + U(323f), U(117f));
            }
        }

        // ==================== PAGE 3: INTERACTIVE FILE SHELF (OPEN / DRAG-OUT / BROWSE / REMOVE) ====================
        private void DrawShelfPage(Graphics g, float x0, float w, float h)
        {
            DrawTwoToneTitle(g, "File ", "Shelf", Color.FromArgb(167, 139, 250), x0 + U(15f), U(18f));

            RectangleF zone = new RectangleF(x0 + U(40f), U(33f), U(336f), U(64f));
            bool zoneHov = (_hoveredId == "shelf_browse_zone");
            using (GraphicsPath zp = CreateRoundRect(zone, U(13f)))
            using (SolidBrush zb = new SolidBrush((_isDragHover || zoneHov) ? Nw(24) : Nw(8)))
            using (Pen zPen = new Pen(Nw(_isDragHover ? 210 : (zoneHov ? 130 : (_state.ShelfFiles.Count == 0 ? 76 : 44))), U(1.3f)))
            {
                if (_state.ShelfFiles.Count == 0) zPen.DashStyle = DashStyle.Dash;
                g.FillPath(zb, zp);
                g.DrawPath(zPen, zp);
            }

            if (_state.ShelfFiles.Count == 0)
            {
                _hitZones.Add(new HitZone { Id = "shelf_browse_zone", Rect = zone });

                float icx = zone.X + zone.Width * 0.5f;
                float icy = zone.Y + U(21f);
                using (Pen tp = new Pen(Nw(190), U(1.75f)))
                {
                    tp.StartCap = LineCap.Round; tp.EndCap = LineCap.Round;
                    g.DrawRectangle(tp, icx - U(11f), icy - U(1f), U(22f), U(8.5f));
                    g.DrawLine(tp, icx, icy - U(11.5f), icx, icy - U(3f));
                    g.DrawLine(tp, icx - U(3.8f), icy - U(6.8f), icx, icy - U(3f));
                    g.DrawLine(tp, icx + U(3.8f), icy - U(6.8f), icx, icy - U(3f));
                }

                DrawCenteredText(g, _state.T("Kéo thả tệp vào đây hoặc nhấp để chọn", "Drop files here or click to browse"), _fShelfMain, InkPri, icx, zone.Y + U(45f));
            }
            else
            {
                int count = Math.Min(5, _state.ShelfFiles.Count);
                for (int i = 0; i < count; i++)
                {
                    string fp = _state.ShelfFiles[i];
                    string name = Path.GetFileName(fp);
                    float cx = x0 + U(43f + i * 66f);
                    RectangleF cardRect = new RectangleF(cx, U(35f), U(62f), U(60f));
                    string itemId = "shelf_item_" + i;
                    string delId  = "shelf_del_" + i;

                    bool cardHov = (_hoveredId == itemId || _hoveredId == delId);
                    if (cardHov)
                    {
                        FillRoundRect(g, cardRect, U(9f), Nw(30));
                    }

                    _hitZones.Add(new HitZone { Id = itemId, Rect = cardRect });

                    RectangleF thumb = new RectangleF(cx + U(13f), U(38f), U(36f), U(36f));
                    FillRoundRect(g, thumb, U(8.5f), IsLight ? Color.FromArgb(228, 235, 247) : Color.FromArgb(38, 44, 62));

                    bool drewSysIcon = false;
                    Bitmap imgPreview = GetCachedShelfImageThumb(fp);
                    if (imgPreview != null)
                    {
                        using (GraphicsPath tpClip = CreateRoundRect(thumb, U(8.5f)))
                        {
                            GraphicsState stImg = g.Save();
                            g.SetClip(tpClip, CombineMode.Intersect);
                            g.DrawImage(imgPreview, thumb);
                            g.Restore(stImg);
                            using (Pen bPen = new Pen(Nw(58), 1f))
                            {
                                g.DrawPath(bPen, tpClip);
                            }
                        }
                        drewSysIcon = true;
                    }
                    if (!drewSysIcon)
                    {
                        try
                        {
                            if (File.Exists(fp))
                            {
                                using (Icon ico = Icon.ExtractAssociatedIcon(fp))
                                {
                                    if (ico != null)
                                    {
                                        g.DrawIcon(ico, new Rectangle((int)(thumb.X + U(5f)), (int)(thumb.Y + U(5f)), (int)U(26f), (int)U(26f)));
                                        drewSysIcon = true;
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                    if (!drewSysIcon)
                    {
                        DrawCenteredText(g, Path.GetExtension(fp).ToUpperInvariant(), _fAxis, InkPri, thumb.X + thumb.Width * 0.5f, thumb.Y + thumb.Height * 0.5f);
                    }

                    DrawCenteredText(g, Truncate(name, 9), _fShelfSub, InkPri, cx + U(31f), U(84.5f));

                    // Mini 'X' remove button on top-right of each file card
                    RectangleF delRect = new RectangleF(cardRect.Right - U(15f), cardRect.Y + U(1.5f), U(13.5f), U(13.5f));
                    _hitZones.Add(new HitZone { Id = delId, Rect = delRect });
                    if (cardHov)
                    {
                        using (SolidBrush rb = new SolidBrush(_hoveredId == delId ? Color.FromArgb(235, 239, 68, 68) : Color.FromArgb(165, 10, 14, 24)))
                        {
                            g.FillEllipse(rb, delRect);
                        }
                        using (Pen xp = new Pen(Color.FromArgb(255, 255, 255), U(1.25f)))
                        {
                            float xcx = delRect.X + delRect.Width * 0.5f, xcy = delRect.Y + delRect.Height * 0.5f;
                            g.DrawLine(xp, xcx - U(2.3f), xcy - U(2.3f), xcx + U(2.3f), xcy + U(2.3f));
                            g.DrawLine(xp, xcx + U(2.3f), xcy - U(2.3f), xcx - U(2.3f), xcy + U(2.3f));
                        }
                    }
                }
            }

            // Dedicated Shelf Action Toolbar below the Drop/Card Zone (zero header collision!)
            float barY = U(101f), barH = U(21f);
            DrawNusaPillButton(g, "shelf_browse", new RectangleF(x0 + U(40f), barY, U(60f), barH), _state.T("+ Thêm", "+ Add"), Color.FromArgb(190, 14, 116, 204));
            DrawNusaPillButton(g, "shelf_snip", new RectangleF(x0 + U(105f), barY, U(66f), barH), _state.T("✂ Chụp", "✂ Snip"), Color.FromArgb(185, 124, 58, 237));
            if (_state.ShelfFiles.Count > 0)
            {
                DrawNusaPillButton(g, "shelf_clear", new RectangleF(x0 + U(176f), barY, U(58f), barH), _state.T("Xóa kệ", "Clear"), Nw(38));
                string hint = string.Format(CultureInfo.InvariantCulture, _state.T("{0} tệp · Kéo ra ngoài", "{0} file(s) · Drag out"), _state.ShelfFiles.Count);
                DrawRightVCenteredText(g, hint, _fShelfSub, Nw(135), x0 + U(374f), barY + barH * 0.5f);
            }
            else
            {
                DrawRightVCenteredText(g, _state.T("Win+Shift+S tự ghim ảnh", "Win+Shift+S auto-pins"), _fShelfSub, Nw(130), x0 + U(374f), barY + barH * 0.5f);
            }
        }

        // ==================== PAGE 4: SCROLLABLE & INLINE-EDITABLE TODOLIST ====================
        private void DrawTodoPage(Graphics g, float x0, float w, float h)
        {
            DrawTwoToneTitle(g, "Todo", "List", Color.FromArgb(56, 211, 111), x0 + U(15f), U(19f));

            int doneCount = 0;
            for (int i = 0; i < _state.Todos.Count; i++)
            {
                if (_state.Todos[i].Done) doneCount++;
            }

            string ratio = string.Format("{0}/{1}", doneCount, _state.Todos.Count);
            DrawLeftVCenteredText(g, ratio, _fHeroBig, InkPri, x0 + U(44f), U(48f));
            DrawLeftVCenteredText(g, _state.T("Hoàn thành hôm nay", "Tasks Done Today"), _fMusicSub, Nw(145), x0 + U(44f), U(76f));

            DrawNusaPillButton(g, "todo_add", new RectangleF(x0 + U(44f), U(96f), U(64f), U(26f)), "+ Add", Color.FromArgb(255, 22, 102, 54));
            DrawNusaPillButton(g, "todo_clear", new RectangleF(x0 + U(114f), U(96f), U(64f), U(26f)), "Clean", Nw(36));

            using (SolidBrush div = new SolidBrush(Nw(32)))
            {
                g.FillRectangle(div, x0 + U(192f), U(34f), Math.Max(1f, U(1f)), U(90f));
            }

            int total = _state.Todos.Count;
            if (total == 0)
            {
                _hitZones.Add(new HitZone { Id = "todo_add", Rect = new RectangleF(x0 + U(200f), U(34f), U(182f), U(86f)) });
                DrawCenteredText(g, _state.T("Chưa có công việc (+ Add)", "No tasks yet (+ Add)"), _fPill, Nw(145), x0 + U(290f), U(74f));
                return;
            }

            int maxScroll = Math.Max(0, total - 3);
            if (_todoScroll > maxScroll) _todoScroll = maxScroll;
            if (_todoScroll < 0) _todoScroll = 0;

            int visCount = Math.Min(3, total - _todoScroll);
            for (int slot = 0; slot < visCount; slot++)
            {
                int i = _todoScroll + slot;
                TodoItem item = _state.Todos[i];
                float ry = U(34f + slot * 31f);
                RectangleF rowRect = new RectangleF(x0 + U(200f), ry, U(180f), U(28f));

                string togId = "todo_toggle_" + i;
                string delId = "todo_del_" + i;
                bool hov = (_hoveredId == togId || _hoveredId == delId);

                FillRoundRect(g, rowRect, U(9f), item.Done ? Color.FromArgb(52, 56, 211, 111) : (hov ? Nw(44) : Nw(24)));

                _hitZones.Add(new HitZone { Id = togId, Rect = new RectangleF(rowRect.X, rowRect.Y, rowRect.Width - U(24f), rowRect.Height) });

                RectangleF chk = new RectangleF(rowRect.X + U(8f), rowRect.Y + U(6f), U(16f), U(16f));
                if (item.Done)
                {
                    using (SolidBrush gb = new SolidBrush(Color.FromArgb(16, 185, 129))) g.FillEllipse(gb, chk);
                    using (Pen cp = new Pen(Color.FromArgb(255, 255, 255), U(1.8f)))
                    {
                        g.DrawLine(cp, chk.X + U(4f), chk.Y + U(8f), chk.X + U(7f), chk.Y + U(11f));
                        g.DrawLine(cp, chk.X + U(7f), chk.Y + U(11f), chk.X + U(12f), chk.Y + U(5f));
                    }
                }
                else
                {
                    using (Pen cp = new Pen(Nw(135), U(1.5f))) g.DrawEllipse(cp, chk);
                }

                Font tf = item.Done ? new Font(_fPill, FontStyle.Strikeout) : _fPill;
                DrawLeftVCenteredText(g, Truncate(item.Text, 19), tf, item.Done ? Nw(185) : InkPri, rowRect.X + U(29f), rowRect.Y + U(14f));
                if (item.Done) tf.Dispose();

                // Per-row 'X' delete button
                RectangleF delRect = new RectangleF(rowRect.Right - U(22f), rowRect.Y + U(4f), U(18f), U(20f));
                _hitZones.Add(new HitZone { Id = delId, Rect = delRect });
                if (hov)
                {
                    using (Pen xp = new Pen(_hoveredId == delId ? Color.FromArgb(255, 110, 110) : Nw(165), U(1.4f)))
                    {
                        float dcx = delRect.X + delRect.Width * 0.5f, dcy = delRect.Y + delRect.Height * 0.5f;
                        g.DrawLine(xp, dcx - U(3f), dcy - U(3f), dcx + U(3f), dcy + U(3f));
                        g.DrawLine(xp, dcx + U(3f), dcy - U(3f), dcx - U(3f), dcy + U(3f));
                    }
                }
            }

            if (total > 3)
            {
                float trackH = U(86f);
                float thumbH = Math.Max(U(20f), trackH * (3.0f / total));
                float thumbY = U(35f) + (_todoScroll / (float)maxScroll) * (trackH - thumbH);
                FillPill(g, new RectangleF(x0 + U(384f), thumbY, U(3f), thumbH), Nw(125));
            }
        }

        // ==================== PAGE 5: INTERACTIVE SYSTEM MONITOR ====================
        private void DrawSystemMonitorPage(Graphics g, float x0, float w, float h)
        {
            DrawTwoToneTitle(g, "System ", "Monitor", Color.FromArgb(25, 184, 255), x0 + U(15f), U(19f));

            double rMb = _hw.DiskReadBytesPerSec / (1024.0 * 1024.0);
            double wMb = _hw.DiskWriteBytesPerSec / (1024.0 * 1024.0);
            double rwKb = (_hw.DiskReadBytesPerSec + _hw.DiskWriteBytesPerSec) / 1024.0;
            bool netIsMbps = Math.Max(_hw.NetRecvMbps, _hw.NetSendMbps) >= 1.0;

            string heroBig, heroSub, detailLine;
            float barPct;

            if (_sysSelectedMetric == 1)
            {
                heroBig = string.Format(CultureInfo.InvariantCulture, "{0:0}%", _hw.DiskActivePercent);
                heroSub = " SSD \u00B7 Active";
                detailLine = string.Format(CultureInfo.InvariantCulture, "R: {0} \u00B7 W: {1}",
                    HardwareMonitor.FormatRate(_hw.DiskReadBytesPerSec),
                    HardwareMonitor.FormatRate(_hw.DiskWriteBytesPerSec));
                barPct = _hw.DiskActivePercent;
            }
            else if (_sysSelectedMetric == 2)
            {
                if (netIsMbps)
                {
                    heroBig = string.Format(CultureInfo.InvariantCulture, "{0:0.0}", _hw.NetRecvMbps);
                    heroSub = " Mbps \u00B7 " + _hw.NetAdapterLabel;
                    detailLine = string.Format(CultureInfo.InvariantCulture, "\u2193 {0:0.0} Mbps \u00B7 \u2191 {1:0.0} Mbps", _hw.NetRecvMbps, _hw.NetSendMbps);
                }
                else
                {
                    heroBig = string.Format(CultureInfo.InvariantCulture, "{0:0}", _hw.NetRecvKbps);
                    heroSub = " Kbps \u00B7 " + _hw.NetAdapterLabel;
                    detailLine = string.Format(CultureInfo.InvariantCulture, "\u2193 {0:0} Kbps \u00B7 \u2191 {1:0} Kbps", _hw.NetRecvKbps, _hw.NetSendKbps);
                }
                barPct = (float)Math.Min(100.0, _hw.NetRecvMbps * 2.0 + (_hw.NetRecvKbps > 1.0 ? 4.0 : 0.0));
            }
            else if (_sysSelectedMetric == 3)
            {
                heroBig = string.Format(CultureInfo.InvariantCulture, "{0:0}%", _hw.GpuUsagePercent);
                heroSub = " GPU 0 \u00B7 " + _hw.GpuEngineLabel;
                detailLine = Truncate(_hw.GpuName, 26);
                barPct = _hw.GpuUsagePercent;
            }
            else
            {
                heroBig = string.Format(CultureInfo.InvariantCulture, "{0:0}%", _hw.CpuUsagePercent);
                heroSub = string.Format(CultureInfo.InvariantCulture, " CPU \u00B7 {0:0.00} GHz", _hw.CpuSpeedGhz);
                detailLine = string.Format(CultureInfo.InvariantCulture, "RAM {0:0}% \u00B7 {1:0.0}/{2:0.0} GB", _hw.RamUsagePercent, _hw.RamUsedGb, _hw.RamTotalGb);
                barPct = _hw.CpuUsagePercent;
            }

            _hitZones.Add(new HitZone { Id = "sys_sel_0", Rect = new RectangleF(x0 + U(38f), U(28f), U(164f), U(64f)) });

            SizeF bigSz = g.MeasureString(heroBig, _fHeroBig);
            float baseY = U(47f) - bigSz.Height * 0.5f;
            GraphicsState stSysLeft = g.Save();
            g.SetClip(new RectangleF(x0 + U(36f), U(28f), U(165f), U(56f)), CombineMode.Intersect);
            using (SolidBrush wBr = new SolidBrush(InkPri))
            using (SolidBrush dBr = new SolidBrush(ToCrispInk(Nw(195))))
            {
                g.DrawString(heroBig, _fHeroBig, wBr, (float)Math.Round(x0 + U(38f)), (float)Math.Round(baseY));
                g.DrawString(heroSub, _fHeroUnit, dBr, (float)Math.Round(x0 + U(38f) + bigSz.Width - U(4f)), (float)Math.Round(U(49f) - _fHeroUnit.Height * 0.5f));
            }
            DrawLeftVCenteredText(g, detailLine, _fMusicSub, Nw(215), x0 + U(42f), U(71f));
            g.Restore(stSysLeft);

            RectangleF barTrack = new RectangleF(x0 + U(42f), U(82.5f), U(152f), U(5f));
            FillPill(g, barTrack, Nw(38));
            float fillW = Math.Max(U(5f), barTrack.Width * Math.Max(0.02f, Math.Min(1.0f, barPct / 100.0f)));
            RectangleF barFill = new RectangleF(barTrack.X, barTrack.Y, fillW, barTrack.Height);
            using (LinearGradientBrush lgb = new LinearGradientBrush(barTrack, Color.FromArgb(25, 184, 255), Color.FromArgb(123, 75, 255), LinearGradientMode.Horizontal))
            {
                FillPillBrush(g, barFill, lgb);
            }

            DrawNusaPillButton(g, "sys_taskmgr", new RectangleF(x0 + U(42f), U(98f), U(76f), U(25f)), "\u25EB Task Mgr", Color.FromArgb(255, 22, 102, 192));
            DrawNusaPillButton(g, "sys_pet", new RectangleF(x0 + U(123f), U(98f), U(72f), U(25f)), _showRestingMascot ? "^ ^ Mascot" : "\u25CB Clock", Nw(36));

            using (SolidBrush div = new SolidBrush(Nw(32)))
            {
                g.FillRectangle(div, x0 + U(205f), U(34f), Math.Max(1f, U(1f)), U(89f));
            }

            string ssdVal = ((rMb + wMb) >= 0.1)
                ? string.Format(CultureInfo.InvariantCulture, "{0:0}% \u00B7 {1:0.0} MB/s", _hw.DiskActivePercent, rMb + wMb)
                : string.Format(CultureInfo.InvariantCulture, "{0:0}% \u00B7 {1:0} KB/s", _hw.DiskActivePercent, rwKb);

            string netVal = netIsMbps
                ? string.Format(CultureInfo.InvariantCulture, "\u2193{0:0.0} \u2191{1:0.0} Mbps", _hw.NetRecvMbps, _hw.NetSendMbps)
                : string.Format(CultureInfo.InvariantCulture, "\u2193{0:0} \u2191{1:0} Kbps", _hw.NetRecvKbps, _hw.NetSendKbps);

            string gpuVal = string.Format(CultureInfo.InvariantCulture, "{0:0}% \u00B7 {1}", _hw.GpuUsagePercent, _hw.GpuEngineLabel);

            DrawMetricRow(g, "sys_sel_1", _sysSelectedMetric == 1, x0 + U(213f), U(34f), U(166f), Color.FromArgb(74, 25, 184, 255), Color.FromArgb(42, 198, 255), "SSD", ssdVal);
            DrawMetricRow(g, "sys_sel_2", _sysSelectedMetric == 2, x0 + U(213f), U(64.5f), U(166f), Color.FromArgb(74, 52, 108, 255), Color.FromArgb(98, 152, 255), _hw.NetAdapterLabel, netVal);
            DrawMetricRow(g, "sys_sel_3", _sysSelectedMetric == 3, x0 + U(213f), U(95f), U(166f), Color.FromArgb(74, 132, 82, 255), Color.FromArgb(172, 130, 255), "GPU 0", gpuVal);
        }

        private void DrawMetricRow(Graphics g, string id, bool selected, float x, float y, float w, Color rowBg, Color dotCol, string label, string val)
        {
            RectangleF rect = new RectangleF(x, y, w, U(28f));
            _hitZones.Add(new HitZone { Id = id, Rect = rect });
            bool hov = (_hoveredId == id);

            Color fill = hov ? Color.FromArgb(Math.Min(255, rowBg.A + 36), rowBg.R, rowBg.G, rowBg.B) : rowBg;
            FillRoundRect(g, rect, U(9f), fill);

            using (GraphicsPath gp = CreateRoundRect(rect, U(9f)))
            using (Pen sp = new Pen(selected ? dotCol : Color.FromArgb(hov ? 145 : 88, dotCol.R, dotCol.G, dotCol.B), selected ? U(1.6f) : 1.0f))
            {
                g.DrawPath(sp, gp);
            }

            RectangleF dot = new RectangleF(x + U(7f), y + U(7f), U(14f), U(14f));
            using (SolidBrush db = new SolidBrush(dotCol)) g.FillEllipse(db, dot);

            SizeF vSz = g.MeasureString(val, _fAxis);
            GraphicsState stRowLbl = g.Save();
            g.SetClip(new RectangleF(x + U(24f), y, Math.Max(U(28f), w - U(34f) - vSz.Width), U(28f)), CombineMode.Intersect);
            DrawLeftVCenteredText(g, label, _fPill, InkPri, x + U(26f), y + U(14f));
            g.Restore(stRowLbl);
            DrawRightVCenteredText(g, val, _fAxis, Nw(245), x + w - U(7f), y + U(14f));
        }

        // ==================== SHARED NUSA VECTOR WIDGETS ====================
        private Color PomoAccent
        {
            get { return (_state.PomodoroMode == 0) ? Color.FromArgb(255, 255, 99, 88) : Color.FromArgb(255, 52, 211, 153); }
        }

        private void DrawPomodoroMiniRing(Graphics g, float cx, float cy)
        {
            int modeTotalMin = (_state.PomodoroMode == 0) ? _state.FocusMinutes : (_state.PomodoroMode == 1 ? _state.ShortBreakMinutes : _state.LongBreakMinutes);
            float totalSec = Math.Max(1, modeTotalMin * 60);
            float frac = Math.Max(0.03f, Math.Min(1f, _state.PomodoroSecondsLeft / totalSec));
            Color c = PomoAccent;
            float r = U(7.8f);
            RectangleF arcRect = new RectangleF(cx - r + U(1.4f), cy - r + U(1.4f), (r - U(1.4f)) * 2f, (r - U(1.4f)) * 2f);
            using (Pen bg = new Pen(Color.FromArgb(72, c.R, c.G, c.B), U(2.25f)))
            using (Pen fg = new Pen(c, U(2.25f)))
            {
                fg.StartCap = LineCap.Round; fg.EndCap = LineCap.Round;
                g.DrawEllipse(bg, arcRect);
                g.DrawArc(fg, arcRect, -90f, 360f * frac);
            }
            float pulse = 0.65f + 0.35f * (float)Math.Abs(Math.Sin(_frameClock.Elapsed.TotalSeconds * 2.2));
            float dr = U(2.1f);
            using (SolidBrush db = new SolidBrush(Color.FromArgb((int)(255 * pulse), c.R, c.G, c.B)))
            {
                g.FillEllipse(db, cx - dr, cy - dr, dr * 2f, dr * 2f);
            }
        }

        private void DrawCpuRing(Graphics g, float cx, float cy)
        {
            float cpu = (_hw != null) ? Math.Max(0f, Math.Min(100f, _hw.CpuUsagePercent)) : 0f;
            Color c = (cpu >= 85f) ? Color.FromArgb(248, 113, 113) : ((cpu >= 60f) ? Color.FromArgb(251, 191, 36) : Color.FromArgb(56, 189, 248));
            float r = U(7.8f);
            RectangleF arcRect = new RectangleF(cx - r + U(1.4f), cy - r + U(1.4f), (r - U(1.4f)) * 2f, (r - U(1.4f)) * 2f);
            using (Pen bg = new Pen(Color.FromArgb(72, c.R, c.G, c.B), U(2.25f)))
            using (Pen fg = new Pen(Color.FromArgb(255, c.R, c.G, c.B), U(2.25f)))
            {
                fg.StartCap = LineCap.Round; fg.EndCap = LineCap.Round;
                g.DrawEllipse(bg, arcRect);
                g.DrawArc(fg, arcRect, -90f, Math.Max(14f, 360f * cpu / 100f));
            }
            // Tiny chip glyph in the middle
            RectangleF chip = new RectangleF(cx - U(2.6f), cy - U(2.6f), U(5.2f), U(5.2f));
            using (SolidBrush cb = new SolidBrush(Color.FromArgb(230, c.R, c.G, c.B)))
            {
                g.FillRectangle(cb, chip);
            }
        }

        private void DrawKeyCapVector(Graphics g, float cx, float cy, string glyph, bool on, int alpha)
        {
            Color ink = on
                ? (IsLight ? Color.FromArgb(alpha, 5, 150, 105) : Color.FromArgb(alpha, 74, 222, 128))
                : Color.FromArgb(Math.Max(0, (int)(alpha * 0.75f)), InkPri.R, InkPri.G, InkPri.B);
            RectangleF cap = new RectangleF(cx - U(7f), cy - U(7f), U(14f), U(14f));
            using (GraphicsPath p = CreateRoundRect(cap, U(3.4f)))
            using (Pen pen = new Pen(ink, U(1.4f)))
            {
                if (on)
                {
                    using (SolidBrush fb = new SolidBrush(Color.FromArgb(Math.Max(0, (int)(alpha * 0.22f)), ink.R, ink.G, ink.B))) g.FillPath(fb, p);
                }
                g.DrawPath(pen, p);
            }
            DrawCenteredText(g, glyph, _fShelfSub, ink, cx, cy);
        }

        private void DrawMicVector(Graphics g, float cx, float cy, bool muted, int alpha)
        {
            Color c = muted
                ? Color.FromArgb(alpha, 248, 113, 113)
                : (IsLight ? Color.FromArgb(alpha, 5, 150, 105) : Color.FromArgb(alpha, 74, 222, 128));
            RectangleF capsule = new RectangleF(cx - U(2.8f), cy - U(6.6f), U(5.6f), U(8.6f));
            using (SolidBrush sb = new SolidBrush(c)) FillPillBrush(g, capsule, sb);
            using (Pen p = new Pen(c, U(1.4f)))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                g.DrawArc(p, cx - U(5f), cy - U(4.6f), U(10f), U(8.4f), 20f, 140f);
                g.DrawLine(p, cx, cy + U(3.9f), cx, cy + U(6.4f));
                g.DrawLine(p, cx - U(2.6f), cy + U(6.6f), cx + U(2.6f), cy + U(6.6f));
                if (muted)
                {
                    using (Pen sp = new Pen(Color.FromArgb(alpha, 255, 255, 255), U(1.6f)))
                    {
                        sp.StartCap = LineCap.Round; sp.EndCap = LineCap.Round;
                        g.DrawLine(sp, cx - U(6f), cy - U(6.5f), cx + U(6f), cy + U(6.5f));
                    }
                }
            }
        }

        private void DrawBatteryRing(Graphics g, float cx, float cy)
        {
            if (!_hasBattery)
            {
                DrawCpuRing(g, cx, cy);
                return;
            }
            float r = U(7.8f);
            RectangleF arcRect = new RectangleF(cx - r + U(1.4f), cy - r + U(1.4f), (r - U(1.4f)) * 2f, (r - U(1.4f)) * 2f);
            Color c = (_batteryStateKind == 1)
                ? Color.FromArgb(46, 229, 118)
                : ((_batteryStateKind == 2)
                    ? ((_batteryPercent <= 10) ? Color.FromArgb(255, 58, 58) : Color.FromArgb(255, 102, 64))
                    : Color.FromArgb(52, 211, 153));

            using (Pen bg = new Pen(Color.FromArgb(72, c.R, c.G, c.B), U(2.25f)))
            using (Pen fg = new Pen(Color.FromArgb(255, c.R, c.G, c.B), U(2.25f)))
            {
                fg.StartCap = LineCap.Round; fg.EndCap = LineCap.Round;
                g.DrawEllipse(bg, arcRect);
                g.DrawArc(fg, arcRect, -90f, Math.Max(22f, 360f * _batteryFraction));
            }

            if (_batteryStateKind == 1)
            {
                PointF[] bolt = new PointF[] {
                    new PointF(cx + U(0.9f), cy - U(4.2f)), new PointF(cx - U(2.6f), cy + U(0.3f)),
                    new PointF(cx - U(0.2f), cy + U(0.3f)), new PointF(cx - U(1.0f), cy + U(4.2f)),
                    new PointF(cx + U(2.6f), cy - U(0.4f)), new PointF(cx + U(0.2f), cy - U(0.4f))
                };
                using (SolidBrush bb = new SolidBrush(InkPri)) g.FillPolygon(bb, bolt);
            }
            else if (_batteryStateKind == 2)
            {
                using (SolidBrush lb = new SolidBrush(c))
                {
                    g.FillRectangle(lb, cx - U(0.9f), cy - U(3.6f), U(1.8f), U(4.4f));
                    g.FillEllipse(lb, cx - U(1.0f), cy + U(1.8f), U(2.0f), U(2.0f));
                }
            }
        }

        private void DrawMusicSpeakerButton(Graphics g, string id, float cx, float cy)
        {
            RectangleF hit = new RectangleF(cx - U(16f), cy - U(12f), U(32f), U(24f));
            _hitZones.Add(new HitZone { Id = id, Rect = hit });
            if (_hoveredId == id) FillRoundRect(g, hit, U(8f), Nw(45));
            bool muted = (_osd != null) && _osd.IsMuted;
            int vol = (_osd != null) ? _osd.VolumePercent : 50;
            DrawSpeakerVector(g, cx, cy, muted, vol, muted ? Color.FromArgb(248, 113, 113) : Nw(215), 255);
        }

        private void DrawSpeakerVector(Graphics g, float cx, float cy, bool muted, int vol, Color col, int alpha)
        {
            Color c = Color.FromArgb(Math.Max(0, Math.Min(255, alpha)), col.R, col.G, col.B);
            PointF[] cone = new PointF[] {
                new PointF(cx - U(6.2f), cy - U(2.2f)), new PointF(cx - U(3.3f), cy - U(2.2f)),
                new PointF(cx + U(0.2f), cy - U(5.2f)), new PointF(cx + U(0.2f), cy + U(5.2f)),
                new PointF(cx - U(3.3f), cy + U(2.2f)), new PointF(cx - U(6.2f), cy + U(2.2f))
            };
            using (SolidBrush sb = new SolidBrush(c)) g.FillPolygon(sb, cone);
            using (Pen wp = new Pen(c, U(1.45f)))
            {
                wp.StartCap = LineCap.Round; wp.EndCap = LineCap.Round;
                if (muted || vol <= 0)
                {
                    g.DrawLine(wp, cx + U(2.4f), cy - U(2.6f), cx + U(6.6f), cy + U(2.6f));
                    g.DrawLine(wp, cx + U(6.6f), cy - U(2.6f), cx + U(2.4f), cy + U(2.6f));
                }
                else
                {
                    if (vol >= 1) g.DrawArc(wp, cx - U(1.2f), cy - U(3.2f), U(5.2f), U(6.4f), -48f, 96f);
                    if (vol >= 45) g.DrawArc(wp, cx - U(2.2f), cy - U(5.4f), U(9.4f), U(10.8f), -48f, 96f);
                }
            }
        }

        private void DrawSunVector(Graphics g, float cx, float cy, int pct, int alpha)
        {
            Color gold = IsLight ? Color.FromArgb(alpha, 217, 119, 6) : Color.FromArgb(alpha, 251, 191, 36);
            float coreR = U(3.3f);
            using (SolidBrush sb = new SolidBrush(gold)) g.FillEllipse(sb, cx - coreR, cy - coreR, coreR * 2f, coreR * 2f);
            float rayIn = U(4.8f), rayOut = U(6.4f + 1.2f * (pct / 100.0f));
            using (Pen rp = new Pen(gold, U(1.4f)))
            {
                rp.StartCap = LineCap.Round; rp.EndCap = LineCap.Round;
                for (int i = 0; i < 8; i++)
                {
                    double ang = i * (Math.PI / 4.0);
                    float cA = (float)Math.Cos(ang), sA = (float)Math.Sin(ang);
                    g.DrawLine(rp, cx + cA * rayIn, cy + sA * rayIn, cx + cA * rayOut, cy + sA * rayOut);
                }
            }
        }

        private void DrawPrivacyDotsCluster(Graphics g, float rightX, float cy, float alpha01, bool micro)
        {
            if (_osd == null || alpha01 <= 0.02f) return;
            bool cam = _osd.IsCameraActive;
            bool mic = _osd.IsMicActive;
            if (!cam && !mic) return;

            int a = Math.Max(0, Math.Min(255, (int)(255f * alpha01)));
            float dotR = micro ? U(1.65f) : U(3.15f);
            float haloR = micro ? U(2.9f) : U(6.2f);
            float curX = rightX - dotR;

            if (mic)
            {
                using (SolidBrush hb = new SolidBrush(Color.FromArgb(Math.Min(110, a / 2), 249, 115, 22)))
                using (SolidBrush db = new SolidBrush(Color.FromArgb(a, 255, 149, 0)))
                {
                    g.FillEllipse(hb, curX - haloR, cy - haloR, haloR * 2f, haloR * 2f);
                    g.FillEllipse(db, curX - dotR, cy - dotR, dotR * 2f, dotR * 2f);
                }
                curX -= (micro ? U(4.8f) : U(8.6f));
            }
            if (cam)
            {
                using (SolidBrush hb = new SolidBrush(Color.FromArgb(Math.Min(110, a / 2), 34, 197, 94)))
                using (SolidBrush db = new SolidBrush(Color.FromArgb(a, 48, 209, 88)))
                {
                    g.FillEllipse(hb, curX - haloR, cy - haloR, haloR * 2f, haloR * 2f);
                    g.FillEllipse(db, curX - dotR, cy - dotR, dotR * 2f, dotR * 2f);
                }
            }
        }

        private float DrawPrivacyHeaderBadge(Graphics g, string id, float rightEdgeX, float cy, bool compact)
        {
            if (_osd == null) return rightEdgeX;
            bool cam = _osd.IsCameraActive;
            bool mic = _osd.IsMicActive;
            if (!cam && !mic) return rightEdgeX;

            string app = cam ? _osd.CameraAppName : _osd.MicAppName;
            string prefix = (cam && mic) ? "Cam+Mic" : (cam ? "Cam" : "Mic");
            string text = (!compact && !string.IsNullOrEmpty(app)) ? (prefix + " \u00B7 " + Truncate(app, 9)) : prefix;

            SizeF tSz = g.MeasureString(text, _fShelfSub);
            float dotSpan = (cam && mic) ? U(18f) : U(11f);
            float pillW = dotSpan + tSz.Width + U(10f);
            RectangleF rect = new RectangleF(rightEdgeX - pillW, cy - U(8.5f), pillW, U(17f));
            if (!string.IsNullOrEmpty(id))
            {
                _hitZones.Add(new HitZone { Id = id, Rect = rect });
            }
            bool hov = (!string.IsNullOrEmpty(id) && _hoveredId == id);

            Color bg = cam
                ? Color.FromArgb(hov ? 105 : 64, 34, 197, 94)
                : Color.FromArgb(hov ? 105 : 64, 249, 115, 22);
            FillPill(g, rect, bg);

            float dx = rect.X + U(7f);
            float r = U(2.8f);
            if (cam)
            {
                using (SolidBrush cb = new SolidBrush(Color.FromArgb(255, 48, 209, 88)))
                {
                    g.FillEllipse(cb, dx - r, cy - r, r * 2f, r * 2f);
                }
                dx += U(6.8f);
            }
            if (mic)
            {
                using (SolidBrush mb = new SolidBrush(Color.FromArgb(255, 255, 149, 0)))
                {
                    g.FillEllipse(mb, dx - r, cy - r, r * 2f, r * 2f);
                }
                dx += U(6.8f);
            }

            DrawLeftVCenteredText(g, text, _fShelfSub, InkPri, dx - U(1.5f), cy);
            return rect.X;
        }

        private void DrawHeadphonesVector(Graphics g, float cx, float cy, int alpha)
        {
            Color c = IsLight ? Color.FromArgb(alpha, 2, 132, 199) : Color.FromArgb(alpha, 56, 189, 248);
            using (Pen bp = new Pen(c, U(1.65f)))
            using (SolidBrush sb = new SolidBrush(c))
            {
                bp.StartCap = LineCap.Round; bp.EndCap = LineCap.Round;
                g.DrawArc(bp, cx - U(5.6f), cy - U(6.0f), U(11.2f), U(10.5f), 180f, 180f);
                FillRoundRect(g, new RectangleF(cx - U(6.6f), cy - U(1.2f), U(2.9f), U(6.0f)), U(1.3f), c);
                FillRoundRect(g, new RectangleF(cx + U(3.7f), cy - U(1.2f), U(2.9f), U(6.0f)), U(1.3f), c);
            }
        }

        private void DrawPrivacySensorVector(Graphics g, float cx, float cy, bool isCam, int alpha)
        {
            Color c = isCam
                ? (IsLight ? Color.FromArgb(alpha, 22, 163, 74) : Color.FromArgb(alpha, 48, 209, 88))
                : (IsLight ? Color.FromArgb(alpha, 234, 88, 12) : Color.FromArgb(alpha, 255, 149, 0));
            using (Pen p = new Pen(c, U(1.55f)))
            using (SolidBrush sb = new SolidBrush(c))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
                if (isCam)
                {
                    FillRoundRect(g, new RectangleF(cx - U(6.2f), cy - U(3.8f), U(8.4f), U(7.6f)), U(1.8f), c);
                    PointF[] lens = new PointF[]
                    {
                        new PointF(cx + U(2.4f), cy - U(1.6f)),
                        new PointF(cx + U(6.2f), cy - U(3.5f)),
                        new PointF(cx + U(6.2f), cy + U(3.5f)),
                        new PointF(cx + U(2.4f), cy + U(1.6f))
                    };
                    g.FillPolygon(sb, lens);
                }
                else
                {
                    FillRoundRect(g, new RectangleF(cx - U(2.4f), cy - U(6.0f), U(4.8f), U(7.6f)), U(2.4f), c);
                    g.DrawArc(p, cx - U(4.6f), cy - U(3.6f), U(9.2f), U(7.4f), 0f, 180f);
                    g.DrawLine(p, cx, cy + U(3.8f), cx, cy + U(6.0f));
                }
            }
        }

        private void DrawScreenshotVector(Graphics g, float cx, float cy, int alpha)
        {
            Color c = IsLight ? Color.FromArgb(alpha, 124, 58, 237) : Color.FromArgb(alpha, 167, 139, 250);
            using (Pen p = new Pen(c, U(1.55f)))
            using (SolidBrush sb = new SolidBrush(c))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                float r = U(5.6f), s = U(2.6f);
                g.DrawLines(p, new PointF[] { new PointF(cx - r, cy - r + s), new PointF(cx - r, cy - r), new PointF(cx - r + s, cy - r) });
                g.DrawLines(p, new PointF[] { new PointF(cx + r - s, cy - r), new PointF(cx + r, cy - r), new PointF(cx + r, cy - r + s) });
                g.DrawLines(p, new PointF[] { new PointF(cx - r, cy + r - s), new PointF(cx - r, cy + r), new PointF(cx - r + s, cy + r) });
                g.DrawLines(p, new PointF[] { new PointF(cx + r - s, cy + r), new PointF(cx + r, cy + r), new PointF(cx + r, cy + r - s) });
                g.FillEllipse(sb, cx - U(2.1f), cy - U(2.1f), U(4.2f), U(4.2f));
            }
        }

        private void DrawMediaToggleVector(Graphics g, float cx, float cy, bool playing, int alpha)
        {
            Color c = IsLight ? Color.FromArgb(alpha, 14, 165, 233) : Color.FromArgb(alpha, 56, 189, 248);
            using (SolidBrush sb = new SolidBrush(c))
            {
                if (playing)
                {
                    PointF[] tri = new PointF[]
                    {
                        new PointF(cx - U(3.6f), cy - U(4.8f)),
                        new PointF(cx + U(4.8f), cy),
                        new PointF(cx - U(3.6f), cy + U(4.8f))
                    };
                    g.FillPolygon(sb, tri);
                }
                else
                {
                    FillRoundRect(g, new RectangleF(cx - U(4.4f), cy - U(4.6f), U(3.1f), U(9.2f)), U(1.1f), c);
                    FillRoundRect(g, new RectangleF(cx + U(1.3f), cy - U(4.6f), U(3.1f), U(9.2f)), U(1.1f), c);
                }
            }
        }

        private float DrawOsdHeaderBadge(Graphics g, string id, float rightEdgeX, float cy, float alpha01, bool compact)
        {
            int a = Math.Max(0, Math.Min(255, (int)(255f * alpha01)));
            if (a <= 6) return rightEdgeX;

            string text;
            Color bgTint;
            if (_osdKind == OsdKind.AudioDevice)
            {
                bool hp = (_osdDelta > 0);
                string dev = (_osd != null && !string.IsNullOrEmpty(_osd.AudioDeviceName))
                    ? _osd.AudioDeviceName
                    : (hp ? _state.T("Tai nghe", "Audio") : _state.T("Loa", "Speaker"));
                text = compact ? string.Format(CultureInfo.InvariantCulture, "{0}%", _osdPercent) : Truncate(dev, 11);
                bgTint = Color.FromArgb((int)(72 * alpha01), 14, 165, 233);
            }
            else if (_osdKind == OsdKind.Screenshot)
            {
                text = compact ? _state.T("K\u1EC7 t\u1EC7p", "Shelf") : _state.T("\u0110\u00E3 ghim", "Pinned");
                bgTint = Color.FromArgb((int)(76 * alpha01), 124, 58, 237);
            }
            else if (_osdKind == OsdKind.PrivacyAlert)
            {
                bool cam = (_osdDelta == 2 || _osdDelta == 3);
                text = (_osdDelta == 3) ? "Cam+Mic" : (cam ? "Cam" : "Mic");
                bgTint = cam ? Color.FromArgb((int)(74 * alpha01), 34, 197, 94) : Color.FromArgb((int)(74 * alpha01), 249, 115, 22);
            }
            else if (_osdKind == OsdKind.Brightness)
            {
                text = string.Format(CultureInfo.InvariantCulture, "{0}%", _osdPercent);
                bgTint = Color.FromArgb((int)(68 * alpha01), 245, 158, 11);
            }
            else if (_osdKind == OsdKind.CapsLock || _osdKind == OsdKind.NumLock)
            {
                bool on = (_osdDelta > 0);
                string nm = (_osdKind == OsdKind.CapsLock) ? "Caps" : "Num";
                text = nm + " " + (on ? _state.T("B\u1EADt", "On") : _state.T("T\u1EAFt", "Off"));
                bgTint = on ? Color.FromArgb((int)(70 * alpha01), 16, 185, 129) : (IsLight ? Color.FromArgb((int)(42 * alpha01), 15, 23, 42) : Color.FromArgb((int)(46 * alpha01), 255, 255, 255));
            }
            else if (_osdKind == OsdKind.MicMute)
            {
                bool hasMic = (_osdDelta != 0);
                text = !hasMic ? "Mic \u2014" : (_osdMuted ? _state.T("Mic t\u1EAFt", "Mic off") : _state.T("Mic b\u1EADt", "Mic on"));
                bgTint = (!hasMic || _osdMuted) ? Color.FromArgb((int)(78 * alpha01), 239, 68, 68) : Color.FromArgb((int)(70 * alpha01), 16, 185, 129);
            }
            else if (_osdKind == OsdKind.MediaToggle)
            {
                text = (_osdDelta > 0) ? _state.T("Ph\u00E1t", "Play") : _state.T("D\u1EEBng", "Pause");
                bgTint = Color.FromArgb((int)(68 * alpha01), 14, 165, 233);
            }
            else if (_osdKind == OsdKind.Battery)
            {
                text = (_batteryStateKind == 1) ? _state.T("S\u1EA1c", "Chg") : string.Format(CultureInfo.InvariantCulture, "{0}%", _osdPercent);
                bgTint = Color.FromArgb((int)(68 * alpha01), 16, 185, 129);
            }
            else
            {
                text = _osdMuted ? _state.T("T\u1EAFt", "Mute") : string.Format(CultureInfo.InvariantCulture, "{0}%", _osdPercent);
                bgTint = _osdMuted
                    ? Color.FromArgb((int)(78 * alpha01), 239, 68, 68)
                    : (IsLight ? Color.FromArgb((int)(42 * alpha01), 15, 23, 42) : Color.FromArgb((int)(46 * alpha01), 255, 255, 255));
            }

            SizeF tSz = g.MeasureString(text, _fShelfSub);
            float iconSpan = U(17f);
            float pillW = iconSpan + tSz.Width + U(9f);
            RectangleF rect = new RectangleF(rightEdgeX - pillW, cy - U(8.5f), pillW, U(17f));
            if (!string.IsNullOrEmpty(id) && (_osdKind == OsdKind.Screenshot || _osdKind == OsdKind.PrivacyAlert))
            {
                _hitZones.Add(new HitZone { Id = id, Rect = rect });
            }
            FillPill(g, rect, bgTint);

            float icx = rect.X + U(9.5f);
            GraphicsState stIc = g.Save();
            g.TranslateTransform(icx, cy);
            g.ScaleTransform(0.76f, 0.76f);
            g.TranslateTransform(-icx, -cy);
            if (_osdKind == OsdKind.AudioDevice && _osdDelta > 0) DrawHeadphonesVector(g, icx, cy, a);
            else if (_osdKind == OsdKind.Brightness) DrawSunVector(g, icx, cy, _osdPercent, a);
            else if (_osdKind == OsdKind.Screenshot) DrawScreenshotVector(g, icx, cy, a);
            else if (_osdKind == OsdKind.PrivacyAlert) DrawPrivacySensorVector(g, icx, cy, (_osdDelta == 2 || _osdDelta == 3), a);
            else if (_osdKind == OsdKind.MediaToggle) DrawMediaToggleVector(g, icx, cy, _osdDelta > 0, a);
            else if (_osdKind == OsdKind.CapsLock || _osdKind == OsdKind.NumLock) DrawKeyCapVector(g, icx, cy, (_osdKind == OsdKind.CapsLock) ? "A" : "1", _osdDelta > 0, a);
            else if (_osdKind == OsdKind.MicMute) DrawMicVector(g, icx, cy, _osdDelta == 0 || _osdMuted, a);
            else
            {
                Color spkCol = _osdMuted ? Color.FromArgb(248, 113, 113) : (IsLight ? Color.FromArgb(15, 23, 42) : Color.FromArgb(255, 255, 255));
                DrawSpeakerVector(g, icx, cy, _osdMuted, _osdPercent, spkCol, a);
            }
            g.Restore(stIc);

            Color txtCol = Color.FromArgb(a, InkPri.R, InkPri.G, InkPri.B);
            DrawLeftVCenteredText(g, text, _fShelfSub, txtCol, rect.X + iconSpan, cy);
            return rect.X;
        }

        // Single-row horizontal Dynamic Island HUD — 100% centered on cy, zero 2-deck text stacking!
        private void DrawOsdHudRow(Graphics g, float x, float cy, float w, float alpha01)
        {
            int a = Math.Max(0, Math.Min(255, (int)(255f * alpha01)));
            if (a <= 4) return;
            float iconCx = x + U(8.5f);
            string label = "";
            string rightTxt;
            bool showBar = true;
            Color barStart, barEnd, labelCol;

            if (_osdKind == OsdKind.AudioDevice)
            {
                bool hp = (_osdDelta > 0);
                if (hp)
                {
                    DrawHeadphonesVector(g, iconCx, cy, a);
                }
                else
                {
                    Color spCol = IsLight ? Color.FromArgb(2, 132, 199) : Color.FromArgb(56, 189, 248);
                    DrawSpeakerVector(g, iconCx, cy, _osdMuted, _osdPercent, spCol, a);
                }
                string dev = (_osd != null && !string.IsNullOrEmpty(_osd.AudioDeviceName))
                    ? _osd.AudioDeviceName
                    : (hp ? _state.T("Tai nghe", "Headphones") : _state.T("Loa ngo\u00E0i", "Speakers"));
                label = Truncate(dev, 18);
                rightTxt = _osdMuted ? _state.T("T\u1EAFt", "Mute") : string.Format(CultureInfo.InvariantCulture, "{0}%", _osdPercent);
                barStart = Color.FromArgb(a, 14, 165, 233); barEnd = Color.FromArgb(a, 56, 189, 248);
                labelCol = IsLight ? Color.FromArgb(a, 2, 132, 199) : Color.FromArgb(a, 125, 211, 252);
            }
            else if (_osdKind == OsdKind.PrivacyAlert)
            {
                bool cam = (_osdDelta == 2 || _osdDelta == 3);
                DrawPrivacySensorVector(g, iconCx, cy, cam, a);
                string app = (_osd != null) ? (cam ? _osd.CameraAppName : _osd.MicAppName) : "";
                string kindName = (_osdDelta == 3) ? "Camera & Mic" : (cam ? "Camera" : "Microphone");
                label = !string.IsNullOrEmpty(app) ? Truncate(kindName + " \u00B7 " + app, 21) : kindName;
                rightTxt = _state.T("\u0110ang b\u1EADt", "Active");
                showBar = false;
                barStart = Color.Empty; barEnd = Color.Empty;
                labelCol = cam
                    ? (IsLight ? Color.FromArgb(a, 21, 128, 61) : Color.FromArgb(a, 74, 222, 128))
                    : (IsLight ? Color.FromArgb(a, 194, 65, 12) : Color.FromArgb(a, 253, 186, 116));
            }
            else if (_osdKind == OsdKind.Screenshot)
            {
                DrawScreenshotVector(g, iconCx, cy, a);
                label = _state.T("\u0110\u00E3 ghim \u1EA3nh ch\u1EE5p v\u00E0o K\u1EC7", "Pinned screenshot to Shelf");
                rightTxt = _state.T("K\u1EC7 t\u1EC7p", "Shelf");
                showBar = false;
                barStart = Color.Empty; barEnd = Color.Empty;
                labelCol = IsLight ? Color.FromArgb(a, 109, 40, 217) : Color.FromArgb(a, 196, 181, 253);
            }
            else if (_osdKind == OsdKind.MediaToggle)
            {
                bool pl = (_osdDelta > 0);
                DrawMediaToggleVector(g, iconCx, cy, pl, a);
                string tr = !string.IsNullOrEmpty(_media.CurrentTitle) ? _media.CurrentTitle : _state.T("Tr\u00ECnh ph\u00E1t nh\u1EA1c", "Media Player");
                label = Truncate(tr, 20);
                rightTxt = pl ? _state.T("Ph\u00E1t", "Play") : _state.T("D\u1EEBng", "Pause");
                showBar = false;
                barStart = Color.Empty; barEnd = Color.Empty;
                labelCol = Color.FromArgb(a, InkPri.R, InkPri.G, InkPri.B);
            }
            else if (_osdKind == OsdKind.CapsLock || _osdKind == OsdKind.NumLock)
            {
                bool on = (_osdDelta > 0);
                bool caps = (_osdKind == OsdKind.CapsLock);
                DrawKeyCapVector(g, iconCx, cy, caps ? "A" : "1", on, a);
                label = caps ? "Caps Lock" : "Num Lock";
                rightTxt = on ? _state.T("B\u1EADt", "On") : _state.T("T\u1EAFt", "Off");
                showBar = false;
                barStart = Color.Empty; barEnd = Color.Empty;
                labelCol = Color.FromArgb(a, InkPri.R, InkPri.G, InkPri.B);
            }
            else if (_osdKind == OsdKind.MicMute)
            {
                bool hasMic = (_osdDelta != 0);
                DrawMicVector(g, iconCx, cy, !hasMic || _osdMuted, a);
                if (!hasMic)
                {
                    label = _state.T("Kh\u00F4ng th\u1EA5y micro", "No microphone");
                    rightTxt = "\u2014";
                    labelCol = Color.FromArgb(a, InkPri.R, InkPri.G, InkPri.B);
                }
                else if (_osdMuted)
                {
                    label = _state.T("\u0110\u00E3 t\u1EAFt micro", "Mic muted");
                    rightTxt = _state.T("T\u1EAFt", "Off");
                    labelCol = IsLight ? Color.FromArgb(a, 220, 38, 38) : Color.FromArgb(a, 248, 113, 113);
                }
                else
                {
                    label = _state.T("\u0110\u00E3 b\u1EADt micro", "Mic on");
                    rightTxt = _state.T("B\u1EADt", "On");
                    labelCol = IsLight ? Color.FromArgb(a, 5, 150, 105) : Color.FromArgb(a, 74, 222, 128);
                }
                showBar = false;
                barStart = Color.Empty; barEnd = Color.Empty;
            }
            else if (_osdKind == OsdKind.Brightness)
            {
                DrawSunVector(g, iconCx, cy, _osdPercent, a);
                rightTxt = string.Format(CultureInfo.InvariantCulture, "{0}%", _osdPercent);
                barStart = Color.FromArgb(a, 245, 158, 11); barEnd = Color.FromArgb(a, 253, 224, 71);
                labelCol = Color.Empty;
            }
            else if (_osdKind == OsdKind.Battery)
            {
                DrawBatteryRing(g, iconCx, cy);
                if (_batteryStateKind == 1)
                {
                    label = _state.T("\u0110ang s\u1EA1c pin \u26A1", "Charging \u26A1");
                    labelCol = IsLight ? Color.FromArgb(a, 5, 150, 105) : Color.FromArgb(a, 74, 222, 128);
                }
                else if (_batteryStateKind == 2)
                {
                    label = _state.T("Pin y\u1EBFu \u00B7 C\u1EA7n s\u1EA1c", "Low Battery");
                    labelCol = IsLight ? Color.FromArgb(a, 220, 38, 38) : Color.FromArgb(a, 252, 129, 129);
                }
                else
                {
                    label = _state.T("Pin b\u00ECnh th\u01B0\u1EDDng", "Battery Normal");
                    labelCol = Color.FromArgb(a, InkPri.R, InkPri.G, InkPri.B);
                }
                rightTxt = string.Format(CultureInfo.InvariantCulture, "{0}%", _osdPercent);
                showBar = false;
                barStart = Color.Empty; barEnd = Color.Empty;
            }
            else
            {
                Color spkCol = _osdMuted ? Color.FromArgb(248, 113, 113) : (IsLight ? Color.FromArgb(15, 23, 42) : Color.FromArgb(255, 255, 255));
                DrawSpeakerVector(g, iconCx, cy, _osdMuted, _osdPercent, spkCol, a);
                if (_osdMuted)
                {
                    label = _state.T("\u0110\u00E3 t\u1EAFt ti\u1EBFng", "Muted");
                    rightTxt = _state.T("T\u1EAFt", "Mute");
                    showBar = false;
                    barStart = Color.Empty; barEnd = Color.Empty;
                    labelCol = IsLight ? Color.FromArgb(a, 220, 38, 38) : Color.FromArgb(a, 248, 113, 113);
                }
                else
                {
                    rightTxt = string.Format(CultureInfo.InvariantCulture, "{0}%", _osdPercent);
                    Color volInk = IsLight ? Color.FromArgb(a, 15, 23, 42) : Color.FromArgb(a, 255, 255, 255);
                    barStart = volInk; barEnd = volInk;
                    labelCol = Color.Empty;
                }
            }

            SizeF rSz = g.MeasureString(rightTxt, _fPill);
            float contentLeft = x + U(23f);
            float contentRight = x + w - rSz.Width - U(9f);
            DrawRightVCenteredText(g, rightTxt, _fPill, Color.FromArgb(a, InkPri.R, InkPri.G, InkPri.B), x + w, cy);

            if (!string.IsNullOrEmpty(label) && showBar)
            {
                float barW = U(56f);
                float maxLblW = Math.Max(U(40f), (contentRight - barW - U(8f)) - contentLeft);
                GraphicsState stOsdLbl = g.Save();
                g.SetClip(new RectangleF(contentLeft, cy - U(11f), maxLblW, U(22f)), CombineMode.Intersect);
                DrawLeftVCenteredText(g, label, _fAxis, labelCol, contentLeft, cy);
                g.Restore(stOsdLbl);

                RectangleF track = new RectangleF(contentRight - barW, cy - U(2.5f), barW, U(5.0f));
                Color trackBg = IsLight
                    ? Color.FromArgb(Math.Max(0, Math.Min(255, (int)(55 * alpha01))), 15, 23, 42)
                    : Color.FromArgb(Math.Max(0, Math.Min(255, (int)(74 * alpha01))), 255, 255, 255);
                FillPill(g, track, trackBg);
                float frac = Math.Max(0f, Math.Min(1f, _osdAnimBar));
                if (frac > 0.008f)
                {
                    RectangleF fillRect = new RectangleF(track.X, track.Y, Math.Max(track.Height, track.Width * frac), track.Height);
                    using (LinearGradientBrush lgb = new LinearGradientBrush(track, barStart, barEnd, LinearGradientMode.Horizontal))
                    {
                        FillPillBrush(g, fillRect, lgb);
                    }
                }
            }
            else if (!string.IsNullOrEmpty(label))
            {
                float maxLblW = Math.Max(U(40f), contentRight - contentLeft);
                GraphicsState stOsdLbl = g.Save();
                g.SetClip(new RectangleF(contentLeft, cy - U(11f), maxLblW, U(22f)), CombineMode.Intersect);
                DrawLeftVCenteredText(g, label, _fAxis, labelCol, contentLeft, cy);
                g.Restore(stOsdLbl);
            }
            else if (showBar)
            {
                float barW = Math.Max(U(44f), contentRight - contentLeft);
                RectangleF track = new RectangleF(contentLeft, cy - U(2.6f), barW, U(5.2f));
                Color trackBg = IsLight
                    ? Color.FromArgb(Math.Max(0, Math.Min(255, (int)(55 * alpha01))), 15, 23, 42)
                    : Color.FromArgb(Math.Max(0, Math.Min(255, (int)(74 * alpha01))), 255, 255, 255);
                FillPill(g, track, trackBg);
                float frac = Math.Max(0f, Math.Min(1f, _osdAnimBar));
                if (frac > 0.008f)
                {
                    RectangleF fillRect = new RectangleF(track.X, track.Y, Math.Max(track.Height, track.Width * frac), track.Height);
                    using (LinearGradientBrush lgb = new LinearGradientBrush(track, barStart, barEnd, LinearGradientMode.Horizontal))
                    {
                        FillPillBrush(g, fillRect, lgb);
                    }
                }
            }
        }

        private static string ShortFileName(string name, int maxChars)
        {
            if (string.IsNullOrEmpty(name)) return "File";
            if (name.Length <= maxChars) return name;
            string ext = "";
            try { ext = Path.GetExtension(name) ?? ""; } catch { ext = ""; }
            if (ext.Length >= 2 && ext.Length <= 7 && maxChars > ext.Length + 6)
            {
                int headLen = maxChars - ext.Length - 1;
                return name.Substring(0, headLen).TrimEnd(' ', '.', '-', '_') + "\u2026" + ext;
            }
            return name.Substring(0, Math.Max(4, maxChars - 1)).TrimEnd(' ', '.', '-', '_') + "\u2026";
        }

        private float DrawDownloadHeaderBadge(Graphics g, string id, float rightEdgeX, float cy, bool compact)
        {
            string badgeTxt = "";
            if (_dlSnap.IsCompleted)
            {
                badgeTxt = _state.T("Xong", "Done");
            }
            else if (_dlSnap.HasExactPercent)
            {
                badgeTxt = string.Format(CultureInfo.InvariantCulture, "{0}%", Math.Max(1, Math.Min(99, (int)Math.Round(_dlSnap.Progress01 * 100f))));
            }
            else if (!compact && _dlSnap.SpeedBytesPerSec > 1024.0)
            {
                badgeTxt = DownloadMonitor.FormatSpeed(_dlSnap.SpeedBytesPerSec);
            }

            bool hov = (!string.IsNullOrEmpty(id) && _hoveredId == id);
            Color pillBg = _dlSnap.IsCompleted
                ? Color.FromArgb(hov ? 105 : 68, 16, 185, 129)
                : Color.FromArgb(hov ? 105 : 64, 14, 165, 233);

            if (string.IsNullOrEmpty(badgeTxt))
            {
                RectangleF rectOnly = new RectangleF(rightEdgeX - U(20f), cy - U(8.5f), U(20f), U(17f));
                if (!string.IsNullOrEmpty(id)) _hitZones.Add(new HitZone { Id = id, Rect = rectOnly });
                FillPill(g, rectOnly, pillBg);
                DrawDownloadVectorIcon(g, rectOnly.X + rectOnly.Width * 0.5f, cy, _dlSnap.IsCompleted, _dlSnap.IsStoreApp, 255, 0.76f);
                return rectOnly.X;
            }

            SizeF tSz = g.MeasureString(badgeTxt, _fShelfSub);
            float pillW = U(18f) + tSz.Width + U(8f);
            RectangleF rect = new RectangleF(rightEdgeX - pillW, cy - U(8.5f), pillW, U(17f));
            if (!string.IsNullOrEmpty(id)) _hitZones.Add(new HitZone { Id = id, Rect = rect });
            FillPill(g, rect, pillBg);
            DrawDownloadVectorIcon(g, rect.X + U(9.5f), cy, _dlSnap.IsCompleted, _dlSnap.IsStoreApp, 255, 0.72f);
            DrawLeftVCenteredText(g, badgeTxt, _fShelfSub, InkPri, rect.X + U(17.5f), cy);
            return rect.X;
        }

        private void DrawDownloadVectorIcon(Graphics g, float cx, float cy, bool completed, bool isStore, int alpha, float scaleMul)
        {
            if (alpha <= 4) return;
            float s = scaleMul;
            if (completed)
            {
                float r = U(7.2f * s);
                using (SolidBrush sb = new SolidBrush(Color.FromArgb(alpha, 16, 185, 129)))
                {
                    g.FillEllipse(sb, cx - r, cy - r, r * 2f, r * 2f);
                }
                using (Pen p = new Pen(Color.FromArgb(alpha, 255, 255, 255), Math.Max(1.4f, U(1.8f * s))))
                {
                    p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
                    g.DrawLines(p, new PointF[]
                    {
                        new PointF(cx - U(3.3f * s), cy + U(0.2f * s)),
                        new PointF(cx - U(0.8f * s), cy + U(2.7f * s)),
                        new PointF(cx + U(3.6f * s), cy - U(2.4f * s))
                    });
                }
                return;
            }

            double sec = _frameClock.Elapsed.TotalSeconds;
            float bob = (float)Math.Sin(sec * 5.4) * U(1.35f * s);
            Color accent = isStore
                ? (IsLight ? Color.FromArgb(alpha, 2, 132, 199) : Color.FromArgb(alpha, 56, 189, 248))
                : (IsLight ? Color.FromArgb(alpha, 14, 116, 144) : Color.FromArgb(alpha, 34, 211, 238));

            using (Pen trayPen = new Pen(Color.FromArgb(Math.Max(0, (int)(alpha * 0.85f)), accent.R, accent.G, accent.B), Math.Max(1.3f, U(1.65f * s))))
            using (Pen arrowPen = new Pen(IsLight ? Color.FromArgb(alpha, 15, 23, 42) : Color.FromArgb(alpha, 255, 255, 255), Math.Max(1.4f, U(1.8f * s))))
            {
                trayPen.StartCap = LineCap.Round; trayPen.EndCap = LineCap.Round; trayPen.LineJoin = LineJoin.Round;
                arrowPen.StartCap = LineCap.Round; arrowPen.EndCap = LineCap.Round; arrowPen.LineJoin = LineJoin.Round;

                if (isStore)
                {
                    RectangleF bag = new RectangleF(cx - U(5.6f * s), cy - U(3.6f * s), U(11.2f * s), U(9.4f * s));
                    using (GraphicsPath bp = CreateRoundRect(bag, U(2.2f * s)))
                    {
                        g.DrawPath(trayPen, bp);
                    }
                    g.DrawArc(trayPen, cx - U(2.8f * s), cy - U(6.4f * s), U(5.6f * s), U(5.2f * s), 180f, 180f);
                }
                else
                {
                    float tw = U(6.0f * s), by = cy + U(5.4f * s);
                    g.DrawLines(trayPen, new PointF[]
                    {
                        new PointF(cx - tw, by - U(2.8f * s)),
                        new PointF(cx - tw, by),
                        new PointF(cx + tw, by),
                        new PointF(cx + tw, by - U(2.8f * s))
                    });
                }

                float ay0 = cy - U(5.4f * s) + bob;
                float ay1 = cy + U(2.0f * s) + bob;
                g.DrawLine(arrowPen, cx, ay0, cx, ay1);
                g.DrawLines(arrowPen, new PointF[]
                {
                    new PointF(cx - U(3.1f * s), ay1 - U(2.8f * s)),
                    new PointF(cx, ay1),
                    new PointF(cx + U(3.1f * s), ay1 - U(2.8f * s))
                });
            }
        }

        // Single-row horizontal Download HUD — 100% centered on cy, zero 2-deck text stacking!
        private void DrawDownloadHudRow(Graphics g, float x, float cy, float w, float alpha01)
        {
            int a = Math.Max(0, Math.Min(255, (int)(255f * alpha01)));
            if (a <= 4) return;

            DrawDownloadVectorIcon(g, x + U(8.5f), cy, _dlSnap.IsCompleted, _dlSnap.IsStoreApp, a, 0.96f);

            string label;
            string rightTxt;
            Color labelCol;

            if (_dlSnap.IsCompleted)
            {
                label = _state.T("T\u1EA3i xong \u00B7 ", "Saved \u00B7 ") + ShortFileName(_dlSnap.FileName, 18);
                long doneBytes = _dlSnap.TotalBytes > 0 ? _dlSnap.TotalBytes : _dlSnap.DownloadedBytes;
                rightTxt = doneBytes > 0 ? DownloadMonitor.FormatBytes(doneBytes) : "100%";
                labelCol = IsLight ? Color.FromArgb(a, 5, 150, 105) : Color.FromArgb(a, 74, 222, 128);
            }
            else if (_dlSnap.IsStoreApp)
            {
                label = ShortFileName(_dlSnap.FileName, 18);
                rightTxt = _dlSnap.HasExactPercent
                    ? string.Format(CultureInfo.InvariantCulture, "{0}%", (int)Math.Round(_dlSnap.Progress01 * 100f))
                    : (_dlSnap.SpeedBytesPerSec > 1024.0 ? DownloadMonitor.FormatSpeed(_dlSnap.SpeedBytesPerSec) : _state.T("\u0110ang t\u1EA3i", "Active"));
                labelCol = Color.FromArgb(a, InkPri.R, InkPri.G, InkPri.B);
            }
            else
            {
                label = ShortFileName(_dlSnap.FileName, 18);
                if (_dlSnap.HasExactPercent)
                {
                    int pctInt = Math.Max(1, Math.Min(99, (int)Math.Round(_dlSnap.Progress01 * 100f)));
                    rightTxt = string.Format(CultureInfo.InvariantCulture, "{0}%", pctInt);
                }
                else if (_dlSnap.SpeedBytesPerSec > 1024.0)
                {
                    rightTxt = DownloadMonitor.FormatSpeed(_dlSnap.SpeedBytesPerSec);
                }
                else
                {
                    long sizeShow = _dlSnap.DownloadedBytes > 0 ? _dlSnap.DownloadedBytes : _dlSnap.TotalBytes;
                    rightTxt = sizeShow > 0 ? DownloadMonitor.FormatBytes(sizeShow) : _state.T("\u0110ang t\u1EA3i", "Active");
                }
                labelCol = Color.FromArgb(a, InkPri.R, InkPri.G, InkPri.B);
            }

            SizeF rSz = g.MeasureString(rightTxt, _fPill);
            float contentLeft = x + U(23f);
            float contentRight = x + w - rSz.Width - U(9f);
            DrawRightVCenteredText(g, rightTxt, _fPill, Color.FromArgb(a, InkPri.R, InkPri.G, InkPri.B), x + w, cy);

            if (_dlSnap.IsCompleted)
            {
                float maxLblW = Math.Max(U(40f), contentRight - contentLeft);
                GraphicsState stLbl = g.Save();
                g.SetClip(new RectangleF(contentLeft, cy - U(11f), maxLblW, U(22f)), CombineMode.Intersect);
                DrawLeftVCenteredText(g, label, _fAxis, labelCol, contentLeft, cy);
                g.Restore(stLbl);
                return;
            }

            float barW = U(62f);
            float lblAvailW = Math.Max(U(40f), (contentRight - barW - U(8f)) - contentLeft);
            GraphicsState stDlLbl = g.Save();
            g.SetClip(new RectangleF(contentLeft, cy - U(11f), lblAvailW, U(22f)), CombineMode.Intersect);
            DrawLeftVCenteredText(g, label, _fAxis, labelCol, contentLeft, cy);
            g.Restore(stDlLbl);

            RectangleF track = new RectangleF(contentRight - barW, cy - U(2.5f), barW, U(5.0f));
            Color trackCol = IsLight
                ? Color.FromArgb(Math.Max(0, Math.Min(255, (int)(55 * alpha01))), 15, 23, 42)
                : Color.FromArgb(Math.Max(0, Math.Min(255, (int)(68 * alpha01))), 255, 255, 255);
            FillPill(g, track, trackCol);

            if (_dlSnap.HasExactPercent)
            {
                float frac = Math.Max(0.04f, Math.Min(1.0f, _dlAnimBar));
                RectangleF fillRect = new RectangleF(track.X, track.Y, Math.Max(track.Height, track.Width * frac), track.Height);
                using (LinearGradientBrush lgb = new LinearGradientBrush(
                    track, Color.FromArgb(a, 14, 165, 233), Color.FromArgb(a, 103, 232, 249), LinearGradientMode.Horizontal))
                {
                    FillPillBrush(g, fillRect, lgb);
                }
            }
            else
            {
                double cyc = (_frameClock.Elapsed.TotalSeconds * 0.72) % 1.0;
                float segW = Math.Max(track.Height * 2.2f, track.Width * 0.42f);
                float segX = track.X - segW + (float)cyc * (track.Width + segW);

                GraphicsState stBar = g.Save();
                using (GraphicsPath clipTrack = CreateRoundRect(track, track.Height * 0.5f))
                {
                    g.SetClip(clipTrack, CombineMode.Intersect);
                    using (SolidBrush sbGlow = new SolidBrush(Color.FromArgb(Math.Max(0, (int)(48 * alpha01)), 56, 189, 248)))
                    {
                        g.FillRectangle(sbGlow, track.X, track.Y, track.Width, track.Height);
                    }
                    RectangleF comet = new RectangleF(segX, track.Y, segW, track.Height);
                    using (LinearGradientBrush lgb = new LinearGradientBrush(
                        comet, Color.FromArgb(a, 14, 165, 233), Color.FromArgb(a, 186, 230, 253), LinearGradientMode.Horizontal))
                    {
                        FillPillBrush(g, comet, lgb);
                    }
                }
                g.Restore(stBar);
            }
        }

        private void RevealDownloadTarget()
        {
            try
            {
                string p = _dlSnap.TargetPath;
                if (!string.IsNullOrEmpty(p))
                {
                    if (File.Exists(p)) { Process.Start("explorer.exe", "/select,\"" + p + "\""); return; }
                    string dir = Path.GetDirectoryName(p);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) { Process.Start("explorer.exe", "\"" + dir + "\""); return; }
                }
                string dlFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                if (Directory.Exists(dlFolder)) Process.Start("explorer.exe", "\"" + dlFolder + "\"");
            }
            catch { }
        }

        // Real equalizer: reads the live WASAPI output peak (L/R) only while music plays & bars are visible
        private void UpdateLiveEqualizer(float dt)
        {
            if (_osd == null) return;
            bool playing = (_media.HasMedia && _media.IsPlaying) || IsCollapsedLyricsActive;
            bool eqVisible = _state.EqualizerEnabled && !_isFullscreenHidden && (_isExpanded || !_isMicroPill);
            bool want = playing && eqVisible;
            if (_osd.MeterWanted != want) _osd.MeterWanted = want;
            if (!want || !_osd.MeterAvailable)
            {
                for (int i = 0; i < 5; i++) _eqLevels[i] = Math.Max(0f, _eqLevels[i] - dt * 3f);
                return;
            }

            float mono, l, r;
            _osd.GetPeaks(out mono, out l, out r);
            // Auto-gain so the bars still dance nicely at low system volume
            _eqGain = Math.Max(mono, Math.Max(0.06f, _eqGain * (1f - dt * 0.35f)));
            float nm = Math.Min(1f, mono / _eqGain);
            float nl = Math.Min(1f, l / _eqGain);
            float nr = Math.Min(1f, r / _eqGain);
            double sec = _frameClock.Elapsed.TotalSeconds;
            for (int i = 0; i < 5; i++)
            {
                float src = (i == 0) ? nl : (i == 1) ? (nl * 0.6f + nm * 0.4f) : (i == 2) ? nm : (i == 3) ? (nr * 0.6f + nm * 0.4f) : nr;
                float wobble = 0.72f + 0.28f * (float)Math.Sin(sec * (7.1 + i * 2.3) + i * 1.7);
                float target = (mono < 0.003f) ? 0f : (float)Math.Pow(src, 0.75) * wobble;
                float spd = (target > _eqLevels[i]) ? 30f : 6.5f;
                _eqLevels[i] += (target - _eqLevels[i]) * Math.Min(1f, dt * spd);
            }
        }

        private void DrawEqualizerBars(Graphics g, float cx, float cy)
        {
            bool active = ((_media.HasMedia && _media.IsPlaying) || IsCollapsedLyricsActive);
            bool live = active && _osd != null && _osd.MeterAvailable && _osd.MeterWanted;
            double sec = _frameClock.Elapsed.TotalSeconds;

            float barW = U(2.2f);
            float gap = U(2.0f);
            float totalW = 5 * barW + 4 * gap;
            float startX = cx - totalW * 0.5f;
            float maxH = U(13f);

            Color eqCol = active ? _media.DominantColor : Nw(145);
            if (active)
            {
                // Boost brightness slightly so bars pop crisply against #000000
                eqCol = Color.FromArgb(255,
                    Math.Min(255, eqCol.R + 55),
                    Math.Min(255, eqCol.G + 55),
                    Math.Min(255, eqCol.B + 55));
            }

            using (SolidBrush wb = new SolidBrush(eqCol))
            {
                for (int i = 0; i < 5; i++)
                {
                    float frac;
                    if (live)
                    {
                        // Real WASAPI output level: bars jump with the actual beat and go calm when the song is quiet
                        frac = 0.16f + 0.84f * Math.Max(0f, Math.Min(1f, _eqLevels[i]));
                    }
                    else if (active)
                    {
                        frac = 0.28f + 0.68f * (0.5f + 0.5f * (float)Math.Sin(sec * (5.2 + i * 1.35) + i * 1.1));
                    }
                    else
                    {
                        // Calm minimal dots when nothing is playing!
                        frac = 0.22f;
                    }
                    float bh = Math.Max(barW, maxH * frac);
                    RectangleF r = new RectangleF(startX + i * (barW + gap), cy - bh * 0.5f, barW, bh);
                    FillPillBrush(g, r, wb);
                }
            }
        }

        private void DrawSettingsGearButton(Graphics g, string id, float cx, float cy)
        {
            float sz = U(22f);
            RectangleF hit = new RectangleF(cx - sz * 0.5f, cy - sz * 0.5f, sz, sz);
            _hitZones.Add(new HitZone { Id = id, Rect = hit });

            bool hov = (_hoveredId == id);
            if (hov)
            {
                using (SolidBrush hb = new SolidBrush(Nw(72)))
                {
                    g.FillEllipse(hb, hit);
                }
            }

            Color col = hov ? InkPri : Nw(190);
            using (Pen p = new Pen(col, U(1.45f)))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                g.DrawEllipse(p, cx - U(3.2f), cy - U(3.2f), U(6.4f), U(6.4f));
                for (int a = 0; a < 360; a += 60)
                {
                    double rad = a * Math.PI / 180.0;
                    g.DrawLine(p,
                        cx + (float)Math.Cos(rad) * U(3.8f), cy + (float)Math.Sin(rad) * U(3.8f),
                        cx + (float)Math.Cos(rad) * U(5.5f), cy + (float)Math.Sin(rad) * U(5.5f));
                }
            }
        }

        private void DrawNavArrow(Graphics g, string id, float cx, float cy, bool isLeft)
        {
            float sz = U(24f);
            RectangleF rect = new RectangleF(cx - sz * 0.5f, cy - sz * 0.5f, sz, sz);
            _hitZones.Add(new HitZone { Id = id, Rect = rect });

            bool hov = (_hoveredId == id);
            using (SolidBrush bg = new SolidBrush(Nw(hov ? 135 : 72)))
            {
                g.FillEllipse(bg, rect);
            }

            using (Pen p = new Pen(InkPri, U(2.0f)))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
                float dx = U(2.2f), dy = U(3.5f);
                if (isLeft)
                    g.DrawLines(p, new PointF[] { new PointF(cx + dx * 0.6f, cy - dy), new PointF(cx - dx, cy), new PointF(cx + dx * 0.6f, cy + dy) });
                else
                    g.DrawLines(p, new PointF[] { new PointF(cx - dx * 0.6f, cy - dy), new PointF(cx + dx, cy), new PointF(cx - dx * 0.6f, cy + dy) });
            }
        }

        private void DrawPageDots(Graphics g, float cx, float cy)
        {
            float dotD = U(3.8f);
            float actW = U(15f);
            float gap = U(5.0f);
            float totalW = (TotalPages - 1) * dotD + actW + (TotalPages - 1) * gap;
            float curX = cx - totalW * 0.5f;
            int hoveredDotIdx = -1;
            float hoveredDotCx = cx;

            for (int i = 0; i < TotalPages; i++)
            {
                string dotId = "dot_" + i;
                bool hov = (_hoveredId == dotId);
                float w = (i == _pageIndex) ? actW : (hov ? U(8f) : dotD);
                RectangleF r = new RectangleF(curX, cy - dotD * 0.5f, w, dotD);
                FillPill(g, r, (i == _pageIndex || hov) ? InkPri : Nw(115));
                _hitZones.Add(new HitZone { Id = dotId, Rect = new RectangleF(curX - U(3f), cy - U(9f), w + U(6f), U(16f)) });

                if (hov && _state.ShowPageLabels)
                {
                    hoveredDotIdx = i;
                    hoveredDotCx = curX + w * 0.5f;
                }
                curX += w + gap;
            }

            if (hoveredDotIdx >= 0)
            {
                string tip = PageNames[hoveredDotIdx];
                SizeF tSz = g.MeasureString(tip, _fShelfSub);
                float tw = tSz.Width + U(14f);
                float th = U(16.5f);
                RectangleF tipRect = new RectangleF(hoveredDotCx - tw * 0.5f, cy - U(21.5f), tw, th);
                FillPill(g, tipRect, IsLight ? Color.FromArgb(248, 255, 255, 255) : Color.FromArgb(246, 18, 23, 34));
                using (GraphicsPath tp = CreateRoundRect(tipRect, th * 0.5f))
                using (Pen tPen = new Pen(IsLight ? Color.FromArgb(195, 186, 202, 224) : Color.FromArgb(155, 75, 95, 130), 1.0f))
                {
                    g.DrawPath(tPen, tp);
                }
                DrawCenteredText(g, tip, _fShelfSub, InkPri, tipRect.X + tipRect.Width * 0.5f, tipRect.Y + tipRect.Height * 0.5f);
            }
        }

        private void DrawAlbumArtThumb(Graphics g, RectangleF rect, float radius)
        {
            using (GraphicsPath path = CreateRoundRect(rect, radius))
            {
                if (!_media.DrawAlbumArt(g, rect, path))
                {
                    // Sleek dark glass empty-media placeholder with subtle vector music note (NOT fake blue circle!)
                    FillRoundRect(g, rect, radius, IsLight ? Color.FromArgb(228, 235, 247) : Color.FromArgb(26, 30, 42));
                    float cx = rect.X + rect.Width * 0.5f;
                    float cy = rect.Y + rect.Height * 0.5f;
                    float s = rect.Width / 58.0f;
                    using (Pen np = new Pen(Nw(135), Math.Max(1.2f, 2.0f * s)))
                    using (SolidBrush nb = new SolidBrush(Nw(135)))
                    {
                        np.StartCap = LineCap.Round; np.EndCap = LineCap.Round;
                        g.DrawLine(np, cx - 3f * s, cy + 6f * s, cx - 3f * s, cy - 8f * s);
                        g.DrawLine(np, cx + 7f * s, cy + 4f * s, cx + 7f * s, cy - 10f * s);
                        g.DrawLine(np, cx - 3f * s, cy - 8f * s, cx + 7f * s, cy - 10f * s);
                        g.FillEllipse(nb, cx - 9f * s, cy + 3f * s, 7f * s, 5.5f * s);
                        g.FillEllipse(nb, cx + 1f * s, cy + 1f * s, 7f * s, 5.5f * s);
                    }
                }
                using (Pen rim = new Pen(Nw(42), 1f))
                {
                    g.DrawPath(rim, path);
                }
            }
        }

        private void DrawLyricsBubbleIcon(Graphics g, string id, float cx, float cy, bool filled)
        {
            RectangleF hit = new RectangleF(cx - U(15f), cy - U(12f), U(30f), U(24f));
            _hitZones.Add(new HitZone { Id = id, Rect = hit });
            if (_hoveredId == id) FillRoundRect(g, hit, U(8f), Nw(40));

            RectangleF b = new RectangleF(cx - U(8f), cy - U(6f), U(16f), U(12f));
            if (filled)
            {
                FillRoundRect(g, b, U(3.2f), IsLight ? Color.FromArgb(37, 99, 235) : Color.FromArgb(255, 255, 255));
                using (SolidBrush blk = new SolidBrush(IsLight ? Color.FromArgb(255, 255, 255) : Color.Black))
                {
                    g.FillEllipse(blk, cx - U(3.5f), cy - U(1.2f), U(2.4f), U(2.4f));
                    g.FillEllipse(blk, cx + U(1.2f), cy - U(1.2f), U(2.4f), U(2.4f));
                }
            }
            else
            {
                using (GraphicsPath p = CreateRoundRect(b, U(3.2f)))
                using (Pen pen = new Pen(Nw(215), U(1.35f)))
                {
                    g.DrawPath(pen, p);
                }
                using (SolidBrush wb = new SolidBrush(Nw(215)))
                {
                    g.FillEllipse(wb, cx - U(3.5f), cy - U(1.2f), U(2.4f), U(2.4f));
                    g.FillEllipse(wb, cx + U(1.2f), cy - U(1.2f), U(2.4f), U(2.4f));
                }
            }
        }

        private void DrawMediaPrevIcon(Graphics g, string id, float cx, float cy)
        {
            RectangleF hit = new RectangleF(cx - U(16f), cy - U(12f), U(32f), U(24f));
            _hitZones.Add(new HitZone { Id = id, Rect = hit });
            if (_hoveredId == id) FillRoundRect(g, hit, U(8f), Nw(45));

            using (SolidBrush wb = new SolidBrush(InkPri))
            {
                float h = U(4.8f), w = U(5.5f);
                g.FillPolygon(wb, new PointF[] { new PointF(cx, cy - h), new PointF(cx - w, cy), new PointF(cx, cy + h) });
                g.FillPolygon(wb, new PointF[] { new PointF(cx + w, cy - h), new PointF(cx, cy), new PointF(cx + w, cy + h) });
            }
        }

        private void DrawMediaNextIcon(Graphics g, string id, float cx, float cy)
        {
            RectangleF hit = new RectangleF(cx - U(16f), cy - U(12f), U(32f), U(24f));
            _hitZones.Add(new HitZone { Id = id, Rect = hit });
            if (_hoveredId == id) FillRoundRect(g, hit, U(8f), Nw(45));

            using (SolidBrush wb = new SolidBrush(InkPri))
            {
                float h = U(4.8f), w = U(5.5f);
                g.FillPolygon(wb, new PointF[] { new PointF(cx - w, cy - h), new PointF(cx, cy), new PointF(cx - w, cy + h) });
                g.FillPolygon(wb, new PointF[] { new PointF(cx, cy - h), new PointF(cx + w, cy), new PointF(cx, cy + h) });
            }
        }

        private void DrawMediaPlayPauseIcon(Graphics g, string id, float cx, float cy, bool isPlaying)
        {
            RectangleF hit = new RectangleF(cx - U(18f), cy - U(12f), U(36f), U(24f));
            _hitZones.Add(new HitZone { Id = id, Rect = hit });
            if (_hoveredId == id) FillRoundRect(g, hit, U(8f), Nw(50));

            using (SolidBrush wb = new SolidBrush(InkPri))
            {
                if (isPlaying)
                {
                    g.FillRectangle(wb, cx - U(4.5f), cy - U(5.5f), U(3.2f), U(11f));
                    g.FillRectangle(wb, cx + U(1.3f), cy - U(5.5f), U(3.2f), U(11f));
                }
                else
                {
                    g.FillPolygon(wb, new PointF[] { new PointF(cx - U(4f), cy - U(6f)), new PointF(cx + U(6f), cy), new PointF(cx - U(4f), cy + U(6f)) });
                }
            }
        }

        private void DrawNusaPillButton(Graphics g, string id, RectangleF rect, string text, Color bg)
        {
            _hitZones.Add(new HitZone { Id = id, Rect = rect });
            bool hov = (_hoveredId == id);
            bool isNeutral = (bg.R == bg.G && bg.G == bg.B) || (IsLight && bg.R == 15 && bg.G == 23 && bg.B == 42);
            Color fill;
            Color txtCol;
            if (isNeutral)
            {
                fill = hov ? Nw(Math.Min(85, bg.A + 32)) : bg;
                txtCol = InkPri;
            }
            else if (bg.A >= 170)
            {
                fill = hov ? Color.FromArgb(255, Math.Min(255, bg.R + 18), Math.Min(255, bg.G + 18), Math.Min(255, bg.B + 18)) : bg;
                txtCol = Color.FromArgb(255, 255, 255);
            }
            else
            {
                fill = hov ? Color.FromArgb(Math.Min(220, bg.A + 36), bg.R, bg.G, bg.B) : bg;
                txtCol = InkPri;
            }
            FillPill(g, rect, fill);
            DrawCenteredText(g, text, _fPill, txtCol, rect.X + rect.Width * 0.5f, rect.Y + rect.Height * 0.5f);
        }

        private void DrawPomoPlayButton(Graphics g, string id, RectangleF rect, bool running)
        {
            Color keepWhite = Color.FromArgb(255, 255, 255);
            _hitZones.Add(new HitZone { Id = id, Rect = rect });
            bool hov = (_hoveredId == id);
            FillPill(g, rect, hov ? Color.FromArgb(255, 255, 102, 110) : Color.FromArgb(255, 255, 75, 85));
            float cx = rect.X + rect.Width * 0.5f;
            float cy = rect.Y + rect.Height * 0.5f;
            using (SolidBrush wb = new SolidBrush(keepWhite))
            {
                if (running)
                {
                    g.FillRectangle(wb, cx - U(3.8f), cy - U(4.8f), U(2.6f), U(9.6f));
                    g.FillRectangle(wb, cx + U(1.2f), cy - U(4.8f), U(2.6f), U(9.6f));
                }
                else
                {
                    g.FillPolygon(wb, new PointF[] { new PointF(cx - U(3.2f), cy - U(5.2f)), new PointF(cx + U(5.2f), cy), new PointF(cx - U(3.2f), cy + U(5.2f)) });
                }
            }
        }

        private void DrawTwoToneTitle(Graphics g, string part1, string part2, Color col2, float x, float cy)
        {
            SizeF s1 = g.MeasureString(part1, _fTitle);
            float y = (float)Math.Round(cy - s1.Height * 0.5f);
            using (SolidBrush b1 = new SolidBrush(InkPri))
            using (SolidBrush b2 = new SolidBrush(ToCrispInk(col2)))
            {
                g.DrawString(part1, _fTitle, b1, (float)Math.Round(x), y);
                g.DrawString(part2, _fTitle, b2, (float)Math.Round(x + s1.Width - U(3.5f)), y);
            }
        }

        private void DrawCenteredText(Graphics g, string text, Font font, Color col, float cx, float cy)
        {
            SizeF sz = g.MeasureString(text, font);
            using (SolidBrush b = new SolidBrush(ToCrispInk(col)))
            {
                g.DrawString(text, font, b, (float)Math.Round(cx - sz.Width * 0.5f), (float)Math.Round(cy - sz.Height * 0.5f));
            }
        }

        private void DrawLeftVCenteredText(Graphics g, string text, Font font, Color col, float x, float cy)
        {
            SizeF sz = g.MeasureString(text, font);
            using (SolidBrush b = new SolidBrush(ToCrispInk(col)))
            {
                g.DrawString(text, font, b, (float)Math.Round(x), (float)Math.Round(cy - sz.Height * 0.5f));
            }
        }

        private void DrawRightVCenteredText(Graphics g, string text, Font font, Color col, float rightX, float cy)
        {
            SizeF sz = g.MeasureString(text, font);
            using (SolidBrush b = new SolidBrush(ToCrispInk(col)))
            {
                g.DrawString(text, font, b, (float)Math.Round(rightX - sz.Width), (float)Math.Round(cy - sz.Height * 0.5f));
            }
        }

        private void FillPill(Graphics g, RectangleF rect, Color col)
        {
            using (SolidBrush b = new SolidBrush(col)) FillPillBrush(g, rect, b);
        }

        private void FillPillBrush(Graphics g, RectangleF rect, Brush b)
        {
            if (rect.Width <= 0f || rect.Height <= 0f) return;
            using (GraphicsPath p = CreateRoundRect(rect, rect.Height * 0.5f)) g.FillPath(b, p);
        }

        private void FillRoundRect(Graphics g, RectangleF rect, float radius, Color col)
        {
            using (GraphicsPath p = CreateRoundRect(rect, radius))
            using (SolidBrush b = new SolidBrush(col)) g.FillPath(b, p);
        }

        private GraphicsPath CreateRoundRect(RectangleF r, float rad)
        {
            GraphicsPath p = new GraphicsPath();
            float d = Math.Min(rad * 2f, Math.Min(r.Width, r.Height));
            if (d <= 1f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180f, 90f);
            p.AddArc(r.Right - d, r.Y, d, d, 270f, 90f);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0f, 90f);
            p.AddArc(r.X, r.Bottom - d, d, d, 90f, 90f);
            p.CloseFigure();
            return p;
        }

        private string FormatMMSS(int totalSeconds)
        {
            int m = Math.Max(0, totalSeconds) / 60;
            int s = Math.Max(0, totalSeconds) % 60;
            return string.Format("{0:00}:{1:00}", m, s);
        }

        private string Truncate(string s, int maxChars)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= maxChars ? s : s.Substring(0, maxChars - 1) + "…";
        }

        // ==================== FULL INTERACTION: CLICK, SCRUB, SCROLL & DRAG ====================
        private void OnNotchMouseWheel(object sender, MouseEventArgs e)
        {
            bool shiftHeld = (Control.ModifierKeys & Keys.Shift) != 0;
            bool ctrlHeld = (Control.ModifierKeys & Keys.Control) != 0;

            if (shiftHeld && _osd != null)
            {
                if (!_isExpanded)
                {
                    _hoverDwellSec = 0f;
                    _stationarySuppressOrigin = Cursor.Position;
                    _gestureHoldCollapsedUntilUtc = DateTime.UtcNow.AddSeconds(0.55);
                }
                if (_osd.SupportsBrightness)
                    _osd.AdjustBrightness(e.Delta > 0 ? 5 : -5);
                else
                    _osd.AdjustVolume(e.Delta > 0 ? 2 : -2);
                return;
            }

            if (!_isExpanded)
            {
                _hoverDwellSec = 0f;
                _stationarySuppressOrigin = Cursor.Position;
                _gestureHoldCollapsedUntilUtc = DateTime.UtcNow.AddSeconds(0.55);
                if (_osd != null)
                {
                    _osd.AdjustVolume(e.Delta > 0 ? 4 : -4);
                }
                return;
            }
            if ((ctrlHeld || _hoveredId == "media_vol") && _osd != null)
            {
                _osd.AdjustVolume(e.Delta > 0 ? 4 : -4);
                return;
            }

            float x0 = (_canvasW - _curW) * 0.5f;
            if (_pageIndex == 0 && _isLyricsSplit && _state.ShowLyrics && _media.HasMedia && e.X >= x0 + U(238f))
            {
                _lyricScrollOffset += (e.Delta < 0) ? 1 : -1;
                _lyricScrollUntilUtc = DateTime.UtcNow.AddSeconds(4.2);
                _needsRedraw = true;
                return;
            }

            // On TodoList page, scrolling over the right task list scrolls tasks smoothly!
            if (_pageIndex == 4 && _state.Todos.Count > 3 && e.X >= x0 + U(190f))
            {
                _todoScroll += (e.Delta < 0) ? 1 : -1;
                _needsRedraw = true;
                return;
            }

            if (e.Delta < 0)
                _pageIndex = (_pageIndex + 1) % TotalPages;
            else if (e.Delta > 0)
                _pageIndex = (_pageIndex + TotalPages - 1) % TotalPages;
            UpdateTargetDimensions();
            _needsRedraw = true;
        }

        private void OnNotchMouseMove(object sender, MouseEventArgs e)
        {
            _lastHoverPt = e.Location;
            if (_hoveredId == "media_seek") _needsRedraw = true;

            if (_isScrubbingSeek && e.Button == MouseButtons.Left && _lastSeekBarRect.Width > 10f)
            {
                double pct = (e.X - _lastSeekBarRect.X) / _lastSeekBarRect.Width;
                _media.SeekToFraction(pct);
                _needsRedraw = true;
                return;
            }

            if (_shelfDragCandidate >= 0 && e.Button == MouseButtons.Left)
            {
                int dist = Math.Abs(e.X - _mouseDownPt.X) + Math.Abs(e.Y - _mouseDownPt.Y);
                if (dist >= 6)
                {
                    int idx = _shelfDragCandidate;
                    _shelfDragCandidate = -1;
                    if (idx >= 0 && idx < _state.ShelfFiles.Count)
                    {
                        string path = _state.ShelfFiles[idx];
                        if (File.Exists(path) || Directory.Exists(path))
                        {
                            try
                            {
                                DataObject dobj = new DataObject(DataFormats.FileDrop, new string[] { path });
                                DoDragDrop(dobj, DragDropEffects.Copy);
                            }
                            catch { }
                        }
                    }
                    return;
                }
            }

            string hit = null;
            for (int i = _hitZones.Count - 1; i >= 0; i--)
            {
                if (_hitZones[i].Rect.Contains(e.Location))
                {
                    hit = _hitZones[i].Id;
                    break;
                }
            }
            if (hit != _hoveredId)
            {
                _hoveredId = hit;
                Cursor = (hit != null) ? Cursors.Hand : Cursors.Default;
                _needsRedraw = true;
            }
        }

        private void OnNotchMouseDown(object sender, MouseEventArgs e)
        {
            _mouseDownPt = e.Location;
            _shelfDragCandidate = -1;

            if (e.Button == MouseButtons.Middle)
            {
                if (!_isExpanded)
                {
                    _hoverDwellSec = 0f;
                    _stationarySuppressOrigin = Cursor.Position;
                    _gestureHoldCollapsedUntilUtc = DateTime.UtcNow.AddSeconds(0.55);
                }
                bool willPlay = !_media.IsPlaying;
                if (_media.HasMedia)
                    _media.TogglePlayPause();
                else
                    NativeMethods.SendMediaKey(NativeMethods.VK_MEDIA_PLAY_PAUSE);
                TriggerOsdHud(OsdKind.MediaToggle, willPlay ? 100 : 35, false, willPlay ? 1 : -1, 1.55);
                _needsRedraw = true;
                return;
            }

            if (!_isExpanded)
            {
                if (e.Button == MouseButtons.Left)
                {
                    _isExpanded = true;
                    _isMicroPill = false;
                    _hw.IsNotchExpanded = true;
                    UpdateTargetDimensions();
                    _needsRedraw = true;
                }
                else if (e.Button == MouseButtons.Right)
                {
                    OpenSettings(-1);
                }
                return;
            }

            if (e.Button == MouseButtons.Right)
            {
                if (_hoveredId == "media_vol" && _osd != null)
                {
                    _osd.ToggleMute();
                    _needsRedraw = true;
                    return;
                }
                _isExpanded = false;
                _suppressHoverUntilLeave = true;
                _isMicroPill = _alwaysMinimal || _autoMinimalObstruction;
                _hw.IsNotchExpanded = false;
                UpdateTargetDimensions();
                _needsRedraw = true;
                return;
            }

            if (e.Button != MouseButtons.Left) return;

            for (int i = _hitZones.Count - 1; i >= 0; i--)
            {
                if (_hitZones[i].Rect.Contains(e.Location))
                {
                    string id = _hitZones[i].Id;
                    if (id == "media_seek" && _lastSeekBarRect.Width > 10f)
                    {
                        _isScrubbingSeek = true;
                        double pct = (e.X - _lastSeekBarRect.X) / _lastSeekBarRect.Width;
                        _media.SeekToFraction(pct);
                        _needsRedraw = true;
                        return;
                    }
                    if (id.StartsWith("shelf_item_"))
                    {
                        int sIdx;
                        if (int.TryParse(id.Substring("shelf_item_".Length), out sIdx))
                        {
                            _shelfDragCandidate = sIdx;
                        }
                        return;
                    }

                    HandleClick(id);
                    _needsRedraw = true;
                    return;
                }
            }
        }

        private void OnNotchMouseUp(object sender, MouseEventArgs e)
        {
            if (_isScrubbingSeek)
            {
                _isScrubbingSeek = false;
                _needsRedraw = true;
            }

            if (_shelfDragCandidate >= 0 && e.Button == MouseButtons.Left)
            {
                int idx = _shelfDragCandidate;
                _shelfDragCandidate = -1;
                if (idx >= 0 && idx < _state.ShelfFiles.Count)
                {
                    string path = _state.ShelfFiles[idx];
                    if (File.Exists(path) || Directory.Exists(path))
                    {
                        try
                        {
                            ProcessStartInfo psi = new ProcessStartInfo();
                            psi.FileName = path;
                            psi.UseShellExecute = true;
                            Process.Start(psi);
                        }
                        catch { }
                    }
                }
            }
        }

        private void HandleClick(string id)
        {
            if (id == "nav_prev")
            {
                _pageIndex = (_pageIndex + TotalPages - 1) % TotalPages;
                UpdateTargetDimensions();
            }
            else if (id == "nav_next")
            {
                _pageIndex = (_pageIndex + 1) % TotalPages;
                UpdateTargetDimensions();
            }
            else if (id.StartsWith("dot_"))
            {
                int idx;
                if (int.TryParse(id.Substring(4), out idx))
                {
                    _pageIndex = idx;
                    UpdateTargetDimensions();
                }
            }
            else if (id == "hdr_clock")
            {
                _showHeaderSeconds = !_showHeaderSeconds;
            }
            else if (id == "hdr_bat")
            {
                if (!_hasBattery)
                {
                    _pageIndex = 5;
                    UpdateTargetDimensions();
                }
                else
                {
                    TriggerOsdHud(OsdKind.Battery, _batteryPercent, false, 0, 2.5);
                }
            }
            else if (id == "hdr_privacy")
            {
                bool cam = (_osd != null && _osd.IsCameraActive);
                try { Process.Start(cam ? "ms-settings:privacy-webcam" : "ms-settings:privacy-microphone"); } catch { }
            }
            else if (id == "osd_banner")
            {
                if (_osdKind == OsdKind.Screenshot)
                {
                    _osdUntilUtc = DateTime.MinValue;
                    _pageIndex = 3;
                    UpdateTargetDimensions();
                }
                else if (_osdKind == OsdKind.PrivacyAlert)
                {
                    bool cam = (_osd != null && _osd.IsCameraActive);
                    try { Process.Start(cam ? "ms-settings:privacy-webcam" : "ms-settings:privacy-microphone"); } catch { }
                }
            }
            else if (id == "dl_open" || id == "hdr_dl")
            {
                RevealDownloadTarget();
            }
            else if (id == "media_vol")
            {
                // Click = switch output (Speakers <-> Headphones) when 2+ devices exist; otherwise mute. Right-click = mute.
                if (_osd != null)
                {
                    if (_osd.ActiveOutputCount >= 2)
                    {
                        _forceAudioDevOsd = true;
                        _osd.CycleAudioOutput();
                    }
                    else
                    {
                        _osd.ToggleMute();
                    }
                }
            }
            else if (id == "hdr_eq")
            {
                if (_pageIndex == 0)
                    _media.TogglePlayPause();
                else
                {
                    _pageIndex = 0;
                    UpdateTargetDimensions();
                }
            }
            else if (id == "media_focus")
            {
                _media.ActivateSourceApp();
            }
            else if (id == "media_lyrics")
            {
                _isLyricsSplit = !_isLyricsSplit;
                _state.DefaultLyricsSplit = _isLyricsSplit;
                if (_isLyricsSplit && !_state.ShowLyrics) _state.ShowLyrics = true;
                _state.SaveAll();
                UpdateTargetDimensions();
            }
            else if (id == "media_prev")
            {
                _media.SkipPrevious();
            }
            else if (id == "media_play")
            {
                _media.TogglePlayPause();
            }
            else if (id == "media_next")
            {
                _media.SkipNext();
            }
            else if (id == "hdr_update")
            {
                _isExpanded = false;
                _suppressHoverUntilLeave = true;
                _hw.IsNotchExpanded = false;
                UpdateTargetDimensions();
                OpenSettings(6);
            }
            else if (id.StartsWith("lyric_seek_"))
            {
                int lIdx;
                if (int.TryParse(id.Substring("lyric_seek_".Length), out lIdx) && lIdx >= 0 && lIdx < _lastDisplayedLyrics.Length)
                {
                    _lyricScrollOffset = 0;
                    _lyricScrollUntilUtc = DateTime.MinValue;
                    _media.SeekToSeconds(Math.Max(0.0, _lastDisplayedLyrics[lIdx].TimeSec + _media.TotalLyricsOffsetSec));
                }
            }
            else if (id == "pomo_toggle")
            {
                _state.PomodoroRunning = !_state.PomodoroRunning;
            }
            else if (id == "pomo_minus")
            {
                _state.PomodoroSecondsLeft = Math.Max(60, _state.PomodoroSecondsLeft - 300);
            }
            else if (id == "pomo_plus")
            {
                _state.PomodoroSecondsLeft = Math.Min(180 * 60, _state.PomodoroSecondsLeft + 300);
            }
            else if (id == "pomo_reset")
            {
                _state.PomodoroRunning = false;
                int mins = (_state.PomodoroMode == 0) ? _state.FocusMinutes : (_state.PomodoroMode == 1 ? _state.ShortBreakMinutes : _state.LongBreakMinutes);
                _state.PomodoroSecondsLeft = mins * 60;
            }
            else if (id == "pomo_focus")
            {
                _state.PomodoroRunning = false;
                _state.PomodoroMode = 0;
                _state.PomodoroSecondsLeft = _state.FocusMinutes * 60;
            }
            else if (id == "pomo_short")
            {
                _state.PomodoroRunning = false;
                _state.PomodoroMode = 1;
                _state.PomodoroSecondsLeft = _state.ShortBreakMinutes * 60;
            }
            else if (id == "pomo_long")
            {
                _state.PomodoroRunning = false;
                _state.PomodoroMode = 2;
                _state.PomodoroSecondsLeft = _state.LongBreakMinutes * 60;
            }
            else if (id == "water_toggle")
            {
                _state.WaterRemindersEnabled = !_state.WaterRemindersEnabled;
                _state.SaveAll();
            }
            else if (id == "water_drink")
            {
                _state.LogWaterDrink(_state.WaterCupSizeMl);
            }
            else if (id == "water_undo")
            {
                _state.LogWaterDrink(-_state.WaterCupSizeMl);
            }
            else if (id == "water_plan_toggle")
            {
                _waterEditMode = !_waterEditMode;
            }
            else if (id.StartsWith("wgoal_set_"))
            {
                int[] goals = new int[] { 1500, 2000, 2500, 3000 };
                int idx;
                if (int.TryParse(id.Substring("wgoal_set_".Length), out idx) && idx >= 0 && idx < goals.Length)
                {
                    _state.WaterGoalMl = goals[idx];
                    _state.SaveAll();
                }
            }
            else if (id.StartsWith("wcup_set_"))
            {
                int[] cups = new int[] { 200, 250, 350, 500 };
                int idx;
                if (int.TryParse(id.Substring("wcup_set_".Length), out idx) && idx >= 0 && idx < cups.Length)
                {
                    _state.WaterCupSizeMl = cups[idx];
                    _state.SaveAll();
                }
            }
            else if (id == "water_reset_zero")
            {
                _state.ResetWaterToday();
                _waterEditMode = false;
            }
            else if (id == "shelf_browse" || id == "shelf_browse_zone")
            {
                BrowseFilesForShelf();
            }
            else if (id == "shelf_snip")
            {
                _isExpanded = false;
                _suppressHoverUntilLeave = true;
                _hw.IsNotchExpanded = false;
                UpdateTargetDimensions();
                try { Process.Start("ms-screenclip:"); } catch { }
            }
            else if (id == "shelf_clear")
            {
                _state.ShelfFiles.Clear();
                _state.Shelf.Clear();
                ClearShelfThumbCache();
                _state.SaveAll();
            }
            else if (id.StartsWith("shelf_del_"))
            {
                int idx;
                if (int.TryParse(id.Substring("shelf_del_".Length), out idx) && idx >= 0 && idx < _state.ShelfFiles.Count)
                {
                    string removed = _state.ShelfFiles[idx];
                    _state.ShelfFiles.RemoveAt(idx);
                    RemoveShelfThumbCacheItem(removed);
                    _state.SaveAll();
                }
            }
            else if (id == "todo_add")
            {
                ShowQuickTodoInput();
            }
            else if (id == "todo_clear")
            {
                bool anyRemoved = false;
                for (int i = _state.Todos.Count - 1; i >= 0; i--)
                {
                    if (_state.Todos[i].Done)
                    {
                        _state.Todos.RemoveAt(i);
                        anyRemoved = true;
                    }
                }
                if (!anyRemoved)
                {
                    for (int i = 0; i < _state.Todos.Count; i++) _state.Todos[i].Done = false;
                }
                _state.SaveAll();
            }
            else if (id.StartsWith("todo_toggle_"))
            {
                int idx;
                if (int.TryParse(id.Substring("todo_toggle_".Length), out idx) && idx >= 0 && idx < _state.Todos.Count)
                {
                    _state.Todos[idx].Done = !_state.Todos[idx].Done;
                    _state.SaveAll();
                }
            }
            else if (id.StartsWith("todo_del_"))
            {
                int idx;
                if (int.TryParse(id.Substring("todo_del_".Length), out idx) && idx >= 0 && idx < _state.Todos.Count)
                {
                    _state.Todos.RemoveAt(idx);
                    _state.SaveAll();
                }
            }
            else if (id.StartsWith("sys_sel_"))
            {
                int m;
                if (int.TryParse(id.Substring("sys_sel_".Length), out m))
                {
                    _sysSelectedMetric = m;
                }
            }
            else if (id == "sys_taskmgr")
            {
                try { Process.Start("taskmgr.exe"); } catch { }
            }
            else if (id == "sys_pet")
            {
                _showRestingMascot = !_showRestingMascot;
                _state.MascotEnabled = _showRestingMascot;
                _state.SaveAll();
                if (_showRestingMascot)
                {
                    _isExpanded = false;
                    _suppressHoverUntilLeave = true;
                    _hw.IsNotchExpanded = false;
                    TriggerMascotWave(3.5);
                }
                else
                {
                    _petState = 0;
                    _waveUntilUtc = DateTime.MinValue;
                }
            }
            else if (id == "open_settings")
            {
                // Open Settings directly on the tab matching the active Notch page!
                int targetTab = 0;
                if (_pageIndex == 0) targetTab = 2;      // Music & Lyrics
                else if (_pageIndex == 1) targetTab = 3; // Pomodoro
                else if (_pageIndex == 2) targetTab = 4; // Water
                else if (_pageIndex == 3 || _pageIndex == 4) targetTab = 5; // Todo & Shelf

                _isExpanded = false;
                _suppressHoverUntilLeave = true;
                _hw.IsNotchExpanded = false;
                UpdateTargetDimensions();
                OpenSettings(targetTab);
            }
        }

        private void BrowseFilesForShelf()
        {
            _isModalDialogOpen = true;
            try
            {
                using (OpenFileDialog ofd = new OpenFileDialog())
                {
                    ofd.Title = _state.T("Chọn tệp để ghim vào Kệ lưu trữ (Shelf)", "Select files to add to Shelf");
                    ofd.Multiselect = true;
                    if (ofd.ShowDialog() == DialogResult.OK && ofd.FileNames != null)
                    {
                        foreach (string f in ofd.FileNames)
                        {
                            if (!_state.ShelfFiles.Contains(f))
                                _state.ShelfFiles.Add(f);
                        }
                        _state.SaveAll();
                    }
                }
            }
            catch { }
            finally
            {
                _isModalDialogOpen = false;
                _needsRedraw = true;
            }
        }

        private void ShowQuickTodoInput()
        {
            _isModalDialogOpen = true;
            try
            {
                Rectangle scr = GetTargetScreen().Bounds;
                int pw = (int)U(310f);
                int ph = (int)U(42f);
                int px = scr.Left + (scr.Width - pw) / 2;
                int py = scr.Top + (int)U(146f);

                using (QuickTodoPopup pop = new QuickTodoPopup(
                    px, py, pw, ph, _fPill,
                    _state.T("Nhập việc cần làm rồi nhấn Enter...", "Type a new task and press Enter..."), IsLight))
                {
                    if (pop.ShowDialog() == DialogResult.OK && !string.IsNullOrEmpty(pop.EnteredText))
                    {
                        _state.Todos.Insert(0, new TodoItem { Text = pop.EnteredText, Done = false });
                        _todoScroll = 0;
                        _state.SaveAll();
                    }
                }
            }
            catch { }
            finally
            {
                _isModalDialogOpen = false;
                _needsRedraw = true;
            }
        }

        private void OnNotchDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop) || HasBrowserDropPayload(e.Data))
            {
                e.Effect = DragDropEffects.Copy;
                _isDragHover = true;
                _isExpanded = true;
                _isMicroPill = false;
                _pageIndex = 3;
                UpdateTargetDimensions();
                _needsRedraw = true;
            }
        }

        private void OnNotchDragLeave(object sender, EventArgs e)
        {
            _isDragHover = false;
            _needsRedraw = true;
        }

        private void OnNotchDragDrop(object sender, DragEventArgs e)
        {
            _isDragHover = false;
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files != null)
                {
                    foreach (string f in files)
                    {
                        if (!_state.ShelfFiles.Contains(f))
                            _state.ShelfFiles.Add(f);
                    }
                    _pageIndex = 3;
                    _state.SaveAll();
                    _needsRedraw = true;
                }
            }
            else
            {
                // Images / links dragged straight out of Chrome, Edge, Firefox...
                List<string> saved = ImportBrowserDrop(e.Data);
                if (saved.Count > 0)
                {
                    foreach (string f in saved)
                    {
                        _state.ShelfFiles.Remove(f);
                        _state.ShelfFiles.Insert(0, f);
                    }
                    _pageIndex = 3;
                    _state.SaveAll();
                    _needsRedraw = true;
                }
            }
        }

        private static bool HasBrowserDropPayload(IDataObject d)
        {
            try
            {
                if (d.GetDataPresent("FileGroupDescriptorW") || d.GetDataPresent(DataFormats.Bitmap) || d.GetDataPresent(DataFormats.Dib) ||
                    d.GetDataPresent("UniformResourceLocatorW") || d.GetDataPresent("UniformResourceLocator"))
                    return true;
                if (d.GetDataPresent(DataFormats.UnicodeText))
                {
                    string t = d.GetData(DataFormats.UnicodeText) as string;
                    return LooksLikeUrl(t);
                }
            }
            catch { }
            return false;
        }

        private static bool LooksLikeUrl(string t)
        {
            if (string.IsNullOrEmpty(t)) return false;
            t = t.Trim();
            return (t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || t.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                && t.IndexOf(' ') < 0 && t.IndexOf('\n') < 0 && t.Length < 4096;
        }

        private static string SanitizeDropName(string name, string fallback)
        {
            string n = (name ?? "").Trim();
            foreach (char c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
            n = n.Trim(' ', '.');
            if (n.Length > 90)
            {
                string ext = "";
                try { ext = Path.GetExtension(n) ?? ""; } catch { ext = ""; }
                if (ext.Length > 8) ext = "";
                n = n.Substring(0, 90 - ext.Length).TrimEnd(' ', '.') + ext;
            }
            return string.IsNullOrEmpty(n) ? fallback : n;
        }

        private static string UniqueDropPath(string dir, string fileName)
        {
            string p = Path.Combine(dir, fileName);
            if (!File.Exists(p)) return p;
            string stem = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);
            for (int i = 2; i < 500; i++)
            {
                p = Path.Combine(dir, stem + " (" + i + ")" + ext);
                if (!File.Exists(p)) return p;
            }
            return Path.Combine(dir, stem + "_" + DateTime.Now.Ticks + ext);
        }

        private static string[] ReadFileGroupDescriptorNames(IDataObject data)
        {
            try
            {
                MemoryStream ms = data.GetData("FileGroupDescriptorW") as MemoryStream;
                if (ms == null) return new string[0];
                byte[] buf = ms.ToArray();
                if (buf.Length < 4) return new string[0];
                int count = BitConverter.ToInt32(buf, 0);
                const int descSize = 592, nameOffset = 72;
                List<string> names = new List<string>();
                for (int i = 0; i < count && i < 16; i++)
                {
                    int start = 4 + i * descSize + nameOffset;
                    if (start + 520 > buf.Length) break;
                    string nm = System.Text.Encoding.Unicode.GetString(buf, start, 520);
                    int z = nm.IndexOf('\0');
                    if (z >= 0) nm = nm.Substring(0, z);
                    names.Add(nm);
                }
                return names.ToArray();
            }
            catch { return new string[0]; }
        }

        private static string GetDroppedUrl(IDataObject data)
        {
            try
            {
                MemoryStream w = data.GetData("UniformResourceLocatorW") as MemoryStream;
                if (w != null)
                {
                    string u = System.Text.Encoding.Unicode.GetString(w.ToArray()).TrimEnd('\0').Trim();
                    if (LooksLikeUrl(u)) return u;
                }
                MemoryStream a = data.GetData("UniformResourceLocator") as MemoryStream;
                if (a != null)
                {
                    string u = System.Text.Encoding.Default.GetString(a.ToArray()).TrimEnd('\0').Trim();
                    if (LooksLikeUrl(u)) return u;
                }
                string t = data.GetData(DataFormats.UnicodeText) as string;
                if (LooksLikeUrl(t)) return t.Trim();
            }
            catch { }
            return null;
        }

        private List<string> ImportBrowserDrop(IDataObject data)
        {
            List<string> saved = new List<string>();
            string dir = Path.Combine(_state.DataDir, "Dropped");
            try { Directory.CreateDirectory(dir); } catch { return saved; }
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);

            // 1. Virtual files (Chrome/Edge expose dragged images & links as FileGroupDescriptorW + FileContents)
            string[] names = ReadFileGroupDescriptorNames(data);
            for (int i = 0; i < names.Length && i < 8; i++)
            {
                try
                {
                    byte[] bytes = ShelfDropHelper.GetFileContents(data, i);
                    if ((bytes == null || bytes.Length == 0) && i == 0)
                    {
                        MemoryStream fc = data.GetData("FileContents") as MemoryStream;
                        if (fc != null) bytes = fc.ToArray();
                    }
                    if (bytes == null || bytes.Length == 0 || bytes.Length > 25 * 1024 * 1024) continue;
                    string fname = SanitizeDropName(names[i], "Drop_" + stamp + ".bin");
                    string path = UniqueDropPath(dir, fname);
                    File.WriteAllBytes(path, bytes);
                    saved.Add(path);
                }
                catch { }
            }
            if (saved.Count > 0) return saved;

            // 2. Raw bitmap (e.g. image copied out of some apps)
            try
            {
                if (data.GetDataPresent(DataFormats.Bitmap))
                {
                    Image img = data.GetData(DataFormats.Bitmap, true) as Image;
                    if (img != null)
                    {
                        using (img)
                        {
                            string path = UniqueDropPath(dir, "Image_" + stamp + ".png");
                            img.Save(path, ImageFormat.Png);
                            saved.Add(path);
                        }
                    }
                }
            }
            catch { }
            if (saved.Count > 0) return saved;

            // 3. Plain link -> Internet shortcut (.url) that opens in the browser on double-click
            string url = GetDroppedUrl(data);
            if (!string.IsNullOrEmpty(url))
            {
                try
                {
                    string host = "Link";
                    try { host = new Uri(url).Host; } catch { }
                    if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) host = host.Substring(4);
                    string path = UniqueDropPath(dir, SanitizeDropName(host, "Link") + ".url");
                    File.WriteAllText(path, "[InternetShortcut]\r\nURL=" + url + "\r\n", System.Text.Encoding.ASCII);
                    saved.Add(path);
                }
                catch { }
            }
            return saved;
        }

        private void ClearShelfThumbCache()
        {
            foreach (KeyValuePair<string, Bitmap> kv in _shelfThumbCache)
            {
                if (kv.Value != null)
                {
                    try { kv.Value.Dispose(); } catch { }
                }
            }
            _shelfThumbCache.Clear();
        }

        private void RemoveShelfThumbCacheItem(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            Bitmap bmp;
            if (_shelfThumbCache.TryGetValue(path, out bmp))
            {
                _shelfThumbCache.Remove(path);
                if (bmp != null)
                {
                    try { bmp.Dispose(); } catch { }
                }
            }
        }

        private Bitmap GetCachedShelfImageThumb(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string ext;
            try { ext = (Path.GetExtension(path) ?? "").ToLowerInvariant(); } catch { return null; }
            if (ext != ".png" && ext != ".jpg" && ext != ".jpeg" && ext != ".bmp" && ext != ".gif")
            {
                return null;
            }

            Bitmap cached;
            if (_shelfThumbCache.TryGetValue(path, out cached))
            {
                return cached;
            }

            try
            {
                if (!File.Exists(path)) return null;
                FileInfo fi = new FileInfo(path);
                if (fi.Length < 64 || fi.Length > 25 * 1024 * 1024) return null;

                byte[] bytes = File.ReadAllBytes(path);
                using (MemoryStream ms = new MemoryStream(bytes))
                using (Bitmap src = new Bitmap(ms))
                {
                    int w = src.Width, h = src.Height;
                    if (w < 4 || h < 4) return null;
                    int side = Math.Min(w, h);
                    int sx = (w - side) / 2;
                    int sy = (h - side) / 2;

                    Bitmap sq = new Bitmap(96, 96, PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(sq))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(src, new Rectangle(0, 0, 96, 96), new Rectangle(sx, sy, side, side), GraphicsUnit.Pixel);
                    }
                    if (_shelfThumbCache.Count >= 16) ClearShelfThumbCache();
                    _shelfThumbCache[path] = sq;
                    return sq;
                }
            }
            catch
            {
                _shelfThumbCache[path] = null;
                return null;
            }
        }

        private void TryCaptureClipboardScreenshotToShelf()
        {
            if (!_state.AutoPinScreenshots) return;
            DateTime now = DateTime.UtcNow;
            if ((now - _startupUtc).TotalSeconds < 1.5) return;
            if ((now - _lastClipCaptureUtc).TotalMilliseconds < 320.0) return;

            try
            {
                if (Clipboard.ContainsFileDropList() || !Clipboard.ContainsImage())
                {
                    return;
                }

                using (Image rawImg = Clipboard.GetImage())
                {
                    Bitmap bmp = rawImg as Bitmap;
                    bool ownBmp = false;
                    if (bmp == null && rawImg != null)
                    {
                        bmp = new Bitmap(rawImg);
                        ownBmp = true;
                    }
                    try
                    {
                        if (bmp == null || bmp.Width < 24 || bmp.Height < 24) return;

                        ulong hash = 14695981039346656037UL;
                        hash = (hash ^ (ulong)bmp.Width) * 1099511628211UL;
                        hash = (hash ^ (ulong)bmp.Height) * 1099511628211UL;
                        for (int gy = 1; gy <= 4; gy++)
                        {
                            int py = (bmp.Height * gy) / 5;
                            for (int gx = 1; gx <= 4; gx++)
                            {
                                int px = (bmp.Width * gx) / 5;
                                int argb = bmp.GetPixel(px, py).ToArgb();
                                hash = (hash ^ unchecked((ulong)(uint)argb)) * 1099511628211UL;
                            }
                        }

                        if (hash == _lastPinnedClipHash) return;
                        _lastPinnedClipHash = hash;
                        _lastClipCaptureUtc = now;

                        string snipDir = Path.Combine(_state.DataDir, "Snips");
                        if (!Directory.Exists(snipDir)) Directory.CreateDirectory(snipDir);

                        string fileName = "Snip_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".png";
                        string fullPath = Path.Combine(snipDir, fileName);
                        if (File.Exists(fullPath))
                        {
                            fileName = "Snip_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture) + ".png";
                            fullPath = Path.Combine(snipDir, fileName);
                        }

                        bmp.Save(fullPath, ImageFormat.Png);

                        // Prune old snips beyond newest 15 files so disk stays clean
                        try
                        {
                            FileInfo[] existing = new DirectoryInfo(snipDir).GetFiles("Snip_*.png");
                            if (existing != null && existing.Length > 15)
                            {
                                Array.Sort(existing, delegate(FileInfo a, FileInfo b) { return b.CreationTimeUtc.CompareTo(a.CreationTimeUtc); });
                                for (int k = 15; k < existing.Length; k++)
                                {
                                    string oldP = existing[k].FullName;
                                    if (!_state.ShelfFiles.Contains(oldP))
                                    {
                                        try { existing[k].Delete(); } catch { }
                                    }
                                }
                            }
                        }
                        catch { }

                        _state.ShelfFiles.Remove(fullPath);
                        _state.ShelfFiles.Insert(0, fullPath);
                        while (_state.ShelfFiles.Count > 12)
                        {
                            string tail = _state.ShelfFiles[_state.ShelfFiles.Count - 1];
                            _state.ShelfFiles.RemoveAt(_state.ShelfFiles.Count - 1);
                            RemoveShelfThumbCacheItem(tail);
                        }
                        _state.SaveAll();

                        TriggerOsdHud(OsdKind.Screenshot, 100, false, 1, 2.35);
                    }
                    finally
                    {
                        if (ownBmp && bmp != null)
                        {
                            try { bmp.Dispose(); } catch { }
                        }
                    }
                }
            }
            catch { }
        }
    }

    // Reads virtual files (CFSTR_FILECONTENTS) by index straight from the OLE data object — needed for Chrome/Edge image & link drags
    internal static class ShelfDropHelper
    {
        public static byte[] GetFileContents(System.Windows.Forms.IDataObject data, int index)
        {
            byte[] r = GetFileContentsAt(data, index);
            if ((r == null || r.Length == 0) && index == 0) r = GetFileContentsAt(data, -1);
            return r;
        }

        private static byte[] GetFileContentsAt(System.Windows.Forms.IDataObject data, int index)
        {
            System.Runtime.InteropServices.ComTypes.IDataObject com = data as System.Runtime.InteropServices.ComTypes.IDataObject;
            if (com == null) return null;
            System.Runtime.InteropServices.ComTypes.FORMATETC fmt = new System.Runtime.InteropServices.ComTypes.FORMATETC();
            fmt.cfFormat = unchecked((short)DataFormats.GetFormat("FileContents").Id);
            fmt.dwAspect = System.Runtime.InteropServices.ComTypes.DVASPECT.DVASPECT_CONTENT;
            fmt.lindex = index;
            fmt.ptd = IntPtr.Zero;
            fmt.tymed = System.Runtime.InteropServices.ComTypes.TYMED.TYMED_ISTREAM | System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL;

            System.Runtime.InteropServices.ComTypes.STGMEDIUM med;
            try { com.GetData(ref fmt, out med); }
            catch { return null; }

            const int maxBytes = 25 * 1024 * 1024;
            try
            {
                if (med.tymed == System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL && med.unionmember != IntPtr.Zero)
                {
                    long size = (long)NativeMethods.GlobalSize(med.unionmember).ToUInt64();
                    if (size <= 0 || size > maxBytes) return null;
                    IntPtr p = NativeMethods.GlobalLock(med.unionmember);
                    if (p == IntPtr.Zero) return null;
                    try
                    {
                        byte[] buf = new byte[size];
                        System.Runtime.InteropServices.Marshal.Copy(p, buf, 0, (int)size);
                        return buf;
                    }
                    finally { NativeMethods.GlobalUnlock(med.unionmember); }
                }
                if (med.tymed == System.Runtime.InteropServices.ComTypes.TYMED.TYMED_ISTREAM && med.unionmember != IntPtr.Zero)
                {
                    System.Runtime.InteropServices.ComTypes.IStream stm =
                        (System.Runtime.InteropServices.ComTypes.IStream)System.Runtime.InteropServices.Marshal.GetObjectForIUnknown(med.unionmember);
                    IntPtr pcb = System.Runtime.InteropServices.Marshal.AllocCoTaskMem(4);
                    try
                    {
                        using (MemoryStream ms = new MemoryStream())
                        {
                            byte[] chunk = new byte[65536];
                            while (true)
                            {
                                System.Runtime.InteropServices.Marshal.WriteInt32(pcb, 0);
                                stm.Read(chunk, chunk.Length, pcb);
                                int n = System.Runtime.InteropServices.Marshal.ReadInt32(pcb);
                                if (n <= 0) break;
                                ms.Write(chunk, 0, n);
                                if (ms.Length > maxBytes) return null;
                            }
                            return ms.ToArray();
                        }
                    }
                    finally
                    {
                        System.Runtime.InteropServices.Marshal.FreeCoTaskMem(pcb);
                        try { System.Runtime.InteropServices.Marshal.ReleaseComObject(stm); } catch { }
                    }
                }
            }
            catch { }
            finally
            {
                try { NativeMethods.ReleaseStgMedium(ref med); } catch { }
            }
            return null;
        }
    }

    // Sleek floating capsule directly below the Notch for 1-second inline task creation!
    internal class QuickTodoPopup : Form
    {
        public string EnteredText { get; private set; }
        private readonly TextBox _tb;

        public QuickTodoPopup(int x, int y, int w, int h, Font font, string placeholder, bool isLight)
        {
            EnteredText = "";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(x, y, w, h);
            TopMost = true;
            ShowInTaskbar = false;
            Color bg = isLight ? Color.FromArgb(255, 255, 255) : Color.FromArgb(18, 21, 30);
            Color fg = isLight ? Color.FromArgb(15, 23, 42) : Color.FromArgb(255, 255, 255);
            BackColor = bg;

            _tb = new TextBox();
            _tb.BorderStyle = BorderStyle.None;
            _tb.BackColor = bg;
            _tb.ForeColor = fg;
            _tb.Font = font;
            _tb.Bounds = new Rectangle(14, (h - font.Height) / 2, w - 78, font.Height + 4);
            _tb.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    Submit();
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    e.SuppressKeyPress = true;
                    DialogResult = DialogResult.Cancel;
                    Close();
                }
            };
            Controls.Add(_tb);

            Label addBtn = new Label();
            addBtn.Text = "Enter ↵";
            addBtn.Font = font;
            addBtn.ForeColor = Color.FromArgb(56, 211, 111);
            addBtn.TextAlign = ContentAlignment.MiddleCenter;
            addBtn.Cursor = Cursors.Hand;
            addBtn.Bounds = new Rectangle(w - 62, 4, 56, h - 8);
            addBtn.Click += (s, e) => Submit();
            Controls.Add(addBtn);

            Deactivate += (s, e) =>
            {
                try { if (!IsDisposed) { DialogResult = DialogResult.Cancel; Close(); } } catch { }
            };
            Shown += (s, e) => { NativeMethods.ApplyModernWindowTheme(Handle, !isLight); _tb.Focus(); };
            Paint += (s, e) =>
            {
                using (Pen p = new Pen(Color.FromArgb(56, 211, 111), 1.5f))
                {
                    e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
                }
            };
        }

        private void Submit()
        {
            string t = (_tb.Text ?? "").Trim();
            if (!string.IsNullOrEmpty(t))
            {
                EnteredText = t;
                DialogResult = DialogResult.OK;
            }
            else
            {
                DialogResult = DialogResult.Cancel;
            }
            Close();
        }
    }
}
