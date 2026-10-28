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

        // Smooth continuous blend factors ("nổi nhẹ từ từ" like Nusa!)
        private readonly Stopwatch _frameClock = Stopwatch.StartNew();
        private double _lastFrameSec = 0.0;
        private float _mascotFade = 0.0f;
        private float _sleepFade = 0.0f;
        private float _waveFade = 0.0f;
        private float _expandFade = 0.0f;
        private float _hoverDwellSec = 0.0f;
        private float _leaveDwellSec = 0.0f;

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
            HandleCreated += (s, e) => { _cachedSelfHwnd = Handle; };
            FormClosed += (s, e) =>
            {
                _bgScanRunning = false;
                if (_osd != null) _osd.OsdChanged -= OnSystemOsdChanged;
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
            try
            {
                if (IsHandleCreated)
                    BeginInvoke(new MethodInvoker(delegate { TriggerOsdHud(kind, percent, isMuted, delta, 2.05); }));
            }
            catch { }
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
            if (!_isExpanded && _isMicroPill) _isMicroPill = false;
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
            if (m.Msg == 0x0021) // WM_MOUSEACTIVATE
            {
                m.Result = new IntPtr(3); // MA_NOACTIVATE: process click immediately without stealing window focus!
                return;
            }
            if (m.Msg == 0x0084) // WM_NCHITTEST
            {
                if (_isFullscreenHidden || (!_isExpanded && !_state.OpenByClick))
                {
                    m.Result = new IntPtr(-1); // HTTRANSPARENT: pass clicks straight through when collapsed!
                    return;
                }
                if (!_isExpanded && _state.OpenByClick)
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
                    int pct = (hasPack && sps.BatteryLifePercent <= 100) ? (int)sps.BatteryLifePercent : 100;
                    bool charging = (sps.ACLineStatus == 1) || ((sps.BatteryFlag & 8) != 0);
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
                        if (chgChanged || enteredLow || enteredCrit)
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
            _canvasW = Math.Max(320, (int)Math.Ceiling(U(680f)));
            _canvasH = Math.Max(120, (int)Math.Ceiling(U(162f)));
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
                _targetW = U(wide ? 588f : 416f); _targetH = U(139f); _targetR = U(30f); _targetE = U(20f);
            }
            else if (IsOsdActive)
            {
                _targetW = U(240f); _targetH = U(36f); _targetR = U(17f); _targetE = U(11f);
            }
            else if (IsDownloadActive && !_alwaysMinimal)
            {
                _targetW = U(296f); _targetH = U(36f); _targetR = U(17f); _targetE = U(11f);
            }
            else if (_isMicroPill || _alwaysMinimal)
            {
                _targetW = U(44f); _targetH = U(4.5f); _targetR = U(3.0f); _targetE = U(2.5f);
            }
            else if (IsCollapsedLyricsActive)
            {
                double lp; bool syn;
                string lt = _media.GetCollapsedLyricDisplay(out lp, out syn);
                int len = string.IsNullOrEmpty(lt) ? 16 : lt.Length;
                float lyrDp = Math.Max(276f, Math.Min(476f, 136f + len * 7.5f));
                _targetW = U(lyrDp); _targetH = U(29f); _targetR = U(14f); _targetE = U(10f);
            }
            else
            {
                _targetW = U(148f); _targetH = U(29f); _targetR = U(14f); _targetE = U(10f);
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
                else if (!_isMicroPill && (sec == 0 || _media.HasMedia))
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

            if (_isExpanded || !_showRestingMascot || _isFullscreenHidden || ((_media.HasMedia && _media.IsPlaying) || IsCollapsedLyricsActive) || ((_alwaysMinimal || _autoMinimalObstruction) && !_manualWaveDemo))
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
                    if (_leaveDwellSec >= 0.12f)
                    {
                        wantExpand = false;
                        _leaveDwellSec = 0f;
                    }
                }
            }
            else
            {
                bool insidePill = !_isFullscreenHidden && (_isMicroPill
                    ? (absDx <= (int)U(50f) && relY >= 0 && relY <= (int)U(9.0f))
                    : (absDx <= (int)(_curW * 0.5f + U(4f)) && relY >= 0 && relY <= (int)Math.Max(U(31f), _curH + U(2f))));

                if (!insidePill)
                {
                    _suppressHoverUntilLeave = false;
                    _hoverDwellSec = 0f;
                }
                else if (!_state.OpenByClick && !_suppressHoverUntilLeave)
                {
                    _hoverDwellSec += dt;
                    if (_hoverDwellSec >= 0.06f)
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

            // ==================== 3. SMOOTH SPRING & GENTLE CROSS-FADES ====================
            float sprOmega = _state.FastAnimation ? 23.5f : 15.5f;
            float fadeSpd  = _state.FastAnimation ? 1.65f : 1.0f;

            bool mW = StepSpringDt(ref _curW, ref _velW, _targetW, dt, sprOmega, 0.84f);
            bool mH = StepSpringDt(ref _curH, ref _velH, _targetH, dt, sprOmega, 0.84f);
            bool mR = StepSpringDt(ref _curR, ref _velR, _targetR, dt, sprOmega + 1.0f, 0.86f);
            bool mE = StepSpringDt(ref _curE, ref _velE, _targetE, dt, sprOmega + 1.0f, 0.86f);

            float targetMascot = (_petState != 0) ? 1.0f : 0.0f;
            float targetSleep  = (_petState == 2) ? 1.0f : 0.0f;
            float targetWave   = (_petState == 3) ? 1.0f : 0.0f;
            float targetExpand = _isExpanded ? 1.0f : 0.0f;

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

            bool osdNow = IsOsdActive;
            if (_wasOsdActive && !osdNow) { _wasOsdActive = false; UpdateAutoMinimalState(); UpdateTargetDimensions(); _needsRedraw = true; }
            float targetOsd = osdNow ? 1.0f : 0.0f;
            float targetBar = (_osdKind == OsdKind.Volume && _osdMuted) ? 0f : (_osdPercent / 100.0f);
            bool dlNow = IsDownloadActive && !osdNow;
            bool wantExpDl = _isExpanded && dlNow && (_dlSnap.IsCompleted || (nowUtc - _dlSnap.SessionStartedUtc).TotalSeconds < 4.2 || _hoveredId == "hdr_dl" || _hoveredId == "dl_open");
            float targetDl = dlNow ? 1.0f : 0.0f;
            float targetDlExp = wantExpDl ? 1.0f : 0.0f;
            float targetDlBar = _dlSnap.IsCompleted ? 1.0f : (_dlSnap.HasExactPercent ? Math.Max(0.03f, Math.Min(1.0f, _dlSnap.Progress01)) : 0.35f);

            bool fM  = MoveTowards(ref _mascotFade,      targetMascot, dt * 1.9f * fadeSpd);
            bool fS  = MoveTowards(ref _sleepFade,       targetSleep,  dt * 1.0f * fadeSpd);
            bool fW  = MoveTowards(ref _waveFade,        targetWave,   dt * 2.6f * fadeSpd);
            bool fE  = MoveTowards(ref _expandFade,      targetExpand, dt * 4.2f * fadeSpd);
            bool fO  = MoveTowards(ref _osdFade,         targetOsd,    dt * 5.8f * fadeSpd);
            bool fB  = (_osdFade > 0.01f) && MoveTowards(ref _osdAnimBar, targetBar, dt * 6.5f);
            bool fD  = MoveTowards(ref _dlFade,          targetDl,     dt * 5.2f * fadeSpd);
            bool fDE = MoveTowards(ref _dlExpBannerFade, targetDlExp,  dt * 5.2f * fadeSpd);
            bool fDB = (_dlFade > 0.01f || _dlExpBannerFade > 0.01f) && MoveTowards(ref _dlAnimBar, targetDlBar, dt * 5.2f);

            if (mW || mH || mR || mE || fM || fS || fW || fE || fO || fB || fD || fDE || fDB || _osdFade > 0.01f || _dlFade > 0.01f || _dlExpBannerFade > 0.01f)
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
            int oldPage = _pageIndex;
            float oldExpFade = _expandFade;
            float oldMasFade = _mascotFade;
            float oldWavFade = _waveFade;
            float oldSlpFade = _sleepFade;

            _isExpanded = expanded;
            _isMicroPill = !expanded && (pageIndex == -2);
            _previewForceWave = !expanded && (pageIndex == -3);
            if (_previewForceWave)
            {
                _showRestingMascot = true;
                _mascotFade = 1.0f;
                _waveFade = 1.0f;
                _sleepFade = 0.0f;
            }
            else
            {
                _mascotFade = 0.0f;
                _waveFade = 0.0f;
                _sleepFade = 0.0f;
            }
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
            _pageIndex = oldPage;
            _expandFade = oldExpFade;
            _mascotFade = oldMasFade;
            _waveFade = oldWavFade;
            _sleepFade = oldSlpFade;
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
                        float expProg = SmoothStep01((bodyH - U(56f)) / U(74f));
                        float colProg = 1.0f - SmoothStep01((bodyH - U(40f)) / U(36f));
                        float minFade = SmoothStep01((bodyH - U(11f)) / U(14f));
                        colProg *= minFade;

                        if (colProg > 0.01f && expProg < 0.99f)
                        {
                            GraphicsState stCol = g.Save();
                            g.TranslateTransform(0f, -(1.0f - colProg) * U(4f));
                            DrawCollapsedNusa(g, x0, bodyW, Math.Min(U(38f), bodyH));
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

                        if (expProg > 0.01f)
                        {
                            GraphicsState stExp = g.Save();
                            float yOff = -(1.0f - expProg) * U(6f);
                            g.TranslateTransform(0f, yOff);
                            DrawExpandedNusa(g, x0, bodyW, U(140f));
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

        // ==================== STAGE 2: COLLAPSED GLANCE PILL (150 x 30 dp) ====================
        private void DrawCollapsedNusa(Graphics g, float x0, float w, float h)
        {
            float oEase = SmoothStep01(_osdFade);
            if (oEase > 0.98f)
            {
                DrawOsdHudRow(g, x0 + U(14f), h * 0.5f, w - U(28f), 1.0f);
                return;
            }
            float dEase = SmoothStep01(_dlFade) * (1.0f - oEase);
            if (dEase > 0.98f && !_alwaysMinimal)
            {
                DrawDownloadHudRow(g, x0 + U(14f), h * 0.5f, w - U(28f), 1.0f);
                return;
            }
            float mEase = SmoothStep01(_previewForceWave ? 1.0f : _mascotFade);
            float wEase = SmoothStep01(_previewForceWave ? 1.0f : _waveFade);
            float sEase = SmoothStep01(_previewForceWave ? 0.0f : _sleepFade);
            float cEase = (1.0f - mEase) * (1.0f - oEase) * (1.0f - (_alwaysMinimal ? 0f : dEase));
            float midY = h * 0.5f;

            if (cEase > 0.01f)
            {
                GraphicsState stClock = g.Save();
                g.TranslateTransform(0f, -(1.0f - cEase) * U(3.5f));
                bool showColLyrics = IsCollapsedLyricsActive;
                if ((_media.HasMedia && _media.IsPlaying) && _batteryStateKind != 2)
                {
                    RectangleF miniArt = new RectangleF(x0 + U(10.5f), midY - U(8.5f), U(17f), U(17f));
                    DrawAlbumArtThumb(g, miniArt, U(4.8f));
                }
                else
                {
                    DrawBatteryRing(g, x0 + U(19.5f), midY);
                }

                string nowClock = DateTime.Now.ToString("HH:mm");
                if (showColLyrics)
                {
                    double colPosSec;
                    bool isSyn;
                    string colFallback;
                    LyricLine colLine = _media.GetActiveCollapsedLyricLine(out colPosSec, out isSyn, out colFallback);

                    SizeF clkSz = g.MeasureString(nowClock, _fIdleTime);
                    float clkRight = _state.EqualizerEnabled ? (x0 + w - U(30f)) : (x0 + w - U(11f));
                    float clkCx = clkRight - clkSz.Width * 0.5f;
                    float divX = (float)Math.Round(clkRight - clkSz.Width - U(5.5f));
                    using (SolidBrush divB = new SolidBrush(IsLight ? Color.FromArgb(70, 30, 41, 59) : Color.FromArgb(68, 255, 255, 255)))
                    {
                        g.FillRectangle(divB, divX, (float)Math.Round(midY - U(5.5f)), Math.Max(1f, U(1f)), (float)Math.Round(U(11f)));
                    }
                    DrawCenteredText(g, nowClock, _fIdleTime, InkPri, clkCx, midY);

                    float leftPad = U(33f);
                    float rightPad = (x0 + w) - (divX - U(6.5f));
                    float availW = Math.Max(U(40f), w - leftPad - rightPad);
                    RectangleF lyrViewport = new RectangleF(x0 + leftPad, 0f, availW, h);
                    DrawKaraokeLyricLine(g, colLine, colFallback, colPosSec, isSyn, _fCollapsedLyric, lyrViewport, true);
                }
                else
                {
                    DrawCenteredText(g, nowClock, _fIdleTime, InkPri, x0 + w * 0.5f, midY);
                }

                if (_state.EqualizerEnabled)
                {
                    DrawEqualizerBars(g, x0 + w - U(18.5f), midY);
                }
                g.Restore(stClock);

                if (cEase < 0.99f)
                {
                    int clockVeil = (int)((1.0f - cEase) * 255f);
                    using (SolidBrush vb = new SolidBrush(VeilCol(clockVeil)))
                    {
                        g.FillRectangle(vb, x0 - U(12f), 0f, w + U(24f), h + 2f);
                    }
                }
            }

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
            }
            if (dEase > 0.01f && !_alwaysMinimal)
            {
                int dVeil = (int)(dEase * 245f);
                using (SolidBrush vb = new SolidBrush(VeilCol(dVeil)))
                {
                    g.FillRectangle(vb, x0 - U(14f), 0f, w + U(28f), h + 2f);
                }
                DrawDownloadHudRow(g, x0 + U(14f), h * 0.5f, w - U(28f), dEase);
            }
            if (oEase > 0.01f)
            {
                int veil = (int)(oEase * 245f);
                using (SolidBrush vb = new SolidBrush(VeilCol(veil)))
                {
                    g.FillRectangle(vb, x0 - U(14f), 0f, w + U(28f), h + 2f);
                }
                DrawOsdHudRow(g, x0 + U(14f), h * 0.5f, w - U(28f), oEase);
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

        // ==================== STAGE 1: EXPANDED NUSA (420 x 140 dp) ====================
        private void DrawExpandedNusa(Graphics g, float x0, float w, float h)
        {
            if (!(_pageIndex == 0 && _isLyricsSplit && _state.ShowLyrics))
            {
                DrawBatteryRing(g, x0 + U(143f), U(18f));
                string batTxt; Color batCol;
                if (_batteryStateKind == 1)
                {
                    batTxt = string.Format(CultureInfo.InvariantCulture, "{0}% \u00B7 {1}", _batteryPercent, _state.T("\u0110ang s\u1EA1c", "Charging"));
                    batCol = IsLight ? Color.FromArgb(5, 150, 105) : Color.FromArgb(74, 222, 128);
                }
                else if (_batteryStateKind == 2)
                {
                    batTxt = string.Format(CultureInfo.InvariantCulture, "{0}% \u00B7 {1}", _batteryPercent, _state.T("Pin y\u1EBFu", "Low"));
                    batCol = IsLight ? Color.FromArgb(220, 38, 38) : Color.FromArgb(255, 98, 98);
                }
                else
                {
                    batTxt = string.Format(CultureInfo.InvariantCulture, "{0}% \u00B7 {1}", _batteryPercent, _state.T("Pin bth", "Normal"));
                    batCol = Nw(215);
                }
                SizeF bSz = g.MeasureString(batTxt, _fAxis);
                float batX = x0 + U(154f);
                _hitZones.Add(new HitZone { Id = "hdr_bat", Rect = new RectangleF(x0 + U(134f), U(6f), bSz.Width + U(24f), U(24f)) });
                DrawLeftVCenteredText(g, batTxt, _fAxis, batCol, batX, U(18f));

                string dateStr = DateTime.Now.ToString("dd MMM ", CultureInfo.InvariantCulture);
                string timeStr = DateTime.Now.ToString(_showHeaderSeconds ? "HH:mm:ss" : "HH:mm");
                SizeF dSz = g.MeasureString(dateStr, _fHdrDate);
                float dateX = batX + bSz.Width + U(6f);
                float dateY = U(18f) - dSz.Height * 0.5f;
                _hitZones.Add(new HitZone { Id = "hdr_clock", Rect = new RectangleF(dateX - U(2f), U(6f), U(108f), U(24f)) });
                using (SolidBrush bDate = new SolidBrush(ToCrispInk(Nw(_hoveredId == "hdr_clock" ? 255 : 215))))
                using (SolidBrush bTime = new SolidBrush(InkPri))
                {
                    g.DrawString(dateStr, _fHdrDate, bDate, (float)Math.Round(dateX), (float)Math.Round(dateY));
                    g.DrawString(timeStr, _fHdrTime, bTime, (float)Math.Round(dateX + dSz.Width), (float)Math.Round(dateY));
                }
                if (IsDownloadActive)
                {
                    DrawDownloadHeaderBadge(g, "hdr_dl", x0 + w - U(68f), U(18f));
                }
                DrawSettingsGearButton(g, "open_settings", x0 + w - U(45f), U(18f));
                if (_state.EqualizerEnabled)
                {
                    _hitZones.Add(new HitZone { Id = "hdr_eq", Rect = new RectangleF(x0 + w - U(32f), U(6f), U(26f), U(24f)) });
                    DrawEqualizerBars(g, x0 + w - U(20f), U(18f));
                }
            }
            else
            {
                DrawBatteryRing(g, x0 + U(104f), U(21f));
                string splitClock = string.Format(CultureInfo.InvariantCulture, "{0}% \u00B7 {1}",
                    _batteryPercent, DateTime.Now.ToString(_showHeaderSeconds ? "HH:mm:ss" : "HH:mm"));
                _hitZones.Add(new HitZone { Id = "hdr_clock", Rect = new RectangleF(x0 + U(96f), U(11f), U(128f), U(19f)) });
                DrawLeftVCenteredText(g, splitClock, _fHdrTime, InkPri, x0 + U(115f), U(21f));

                if (IsDownloadActive)
                {
                    DrawDownloadHeaderBadge(g, "hdr_dl", x0 + w - U(48f), U(18f));
                }
                DrawSettingsGearButton(g, "open_settings", x0 + w - U(24f), U(18f));
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

            DrawNavArrow(g, "nav_prev", x0 + U(20f), U(70f), true);
            DrawNavArrow(g, "nav_next", x0 + w - U(20f), U(70f), false);
            DrawPageDots(g, x0 + w * 0.5f, U(132f));
            if (_osdFade > 0.02f)
            {
                float oEase = SmoothStep01(_osdFade);
                float bw = U(236f), bh = U(31f);
                RectangleF banner = new RectangleF(x0 + (w - bw) * 0.5f, U(4f) - (1.0f - oEase) * U(6f), bw, bh);
                Color capBg = IsLight ? Color.FromArgb((int)(246f * oEase), 255, 255, 255) : Color.FromArgb((int)(242f * oEase), 16, 20, 30);
                FillPill(g, banner, capBg);
                using (GraphicsPath bp = CreateRoundRect(banner, bh * 0.5f))
                using (Pen bPen = new Pen(IsLight ? Color.FromArgb((int)(190f * oEase), 186, 202, 224) : Color.FromArgb((int)(140f * oEase), 75, 95, 130), 1.0f))
                {
                    g.DrawPath(bPen, bp);
                }
                DrawOsdHudRow(g, banner.X + U(12f), banner.Y + bh * 0.5f, banner.Width - U(24f), oEase);
            }
            else if (_dlExpBannerFade > 0.02f && IsDownloadActive)
            {
                float dbEase = SmoothStep01(_dlExpBannerFade);
                float bw = U(286f), bh = U(31f);
                RectangleF banner = new RectangleF(x0 + (w - bw) * 0.5f, U(4f) - (1.0f - dbEase) * U(6f), bw, bh);
                _hitZones.Add(new HitZone { Id = "dl_open", Rect = banner });
                bool hovDl = (_hoveredId == "dl_open");
                Color capBg = IsLight
                    ? Color.FromArgb((int)(248f * dbEase), hovDl ? 241 : 255, hovDl ? 248 : 255, 255)
                    : Color.FromArgb((int)(244f * dbEase), hovDl ? 22 : 14, hovDl ? 30 : 19, hovDl ? 44 : 30);
                FillPill(g, banner, capBg);
                using (GraphicsPath bp = CreateRoundRect(banner, bh * 0.5f))
                using (Pen bPen = new Pen(IsLight ? Color.FromArgb((int)(195f * dbEase), 148, 196, 238) : Color.FromArgb((int)(155f * dbEase), 56, 189, 248), 1.0f))
                {
                    g.DrawPath(bPen, bp);
                }
                DrawDownloadHudRow(g, banner.X + U(12f), banner.Y + bh * 0.5f, banner.Width - U(24f), dbEase);
            }
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

            DrawLeftVCenteredText(g, Truncate(title, 30), _fMusicTitle, titleCol, x0 + U(110f), U(44f));
            DrawLeftVCenteredText(g, Truncate(artist, 48), _fMusicSub, subCol, x0 + U(110f), U(62f));

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

            DrawLyricsBubbleIcon(g, "media_lyrics", x0 + U(56f), U(116f), _isLyricsSplit);
            DrawMediaPrevIcon(g, "media_prev", x0 + U(158f), U(115f));
            DrawMediaPlayPauseIcon(g, "media_play", x0 + U(208f), U(115f), _media.IsPlaying);
            DrawMediaNextIcon(g, "media_next", x0 + U(258f), U(115f));
            DrawMusicSpeakerButton(g, "media_vol", x0 + w - U(56f), U(115f));
        }

        // ==================== PAGE 0 SPLIT: REAL SYNCED LYRICS (520 x 140 dp) ====================
        private void DrawLyricsSplitPage(Graphics g, float x0, float w, float h)
        {
            bool hasMedia = _media.HasMedia;
            Color dom = hasMedia ? _media.DominantColor : Color.FromArgb(44, 100, 184);

            RectangleF ambRect = new RectangleF(x0, h * 0.20f, w, h * 0.80f);
            using (LinearGradientBrush amb = new LinearGradientBrush(
                ambRect,
                Color.FromArgb(0, dom.R, dom.G, dom.B),
                Color.FromArgb(hasMedia ? 145 : 65, dom.R, dom.G, dom.B),
                LinearGradientMode.Vertical))
            {
                g.FillRectangle(amb, ambRect);
            }

            RectangleF artRect = new RectangleF(x0 + U(36f), U(14f), U(54f), U(54f));
            DrawAlbumArtThumb(g, artRect, U(10f));
            if (hasMedia)
            {
                _hitZones.Add(new HitZone { Id = "media_focus", Rect = new RectangleF(x0 + U(36f), U(31f), U(192f), U(38f)) });
            }

            string title = hasMedia && !string.IsNullOrEmpty(_media.CurrentTitle)
                ? _media.CurrentTitle
                : _state.T("Chưa phát nhạc", "Nothing playing");
            string artist = hasMedia && !string.IsNullOrEmpty(_media.CurrentArtist)
                ? _media.CurrentArtist
                : _state.T("YouTube · Spotify", "YouTube · Spotify");

            DrawLeftVCenteredText(g, Truncate(title, 17), _fMusicTitle, InkPri, x0 + U(98f), U(41f));
            DrawLeftVCenteredText(g, Truncate(artist, 21), _fMusicSub, Nw(150), x0 + U(98f), U(59f));

            double pos = hasMedia ? _media.GetLivePositionSec() : 0.0;
            double dur = hasMedia ? _media.DurationSec : 0.0;
            double rem = Math.Max(0.0, dur - pos);
            float pct = (hasMedia && dur > 0.5) ? (float)Math.Max(0.0, Math.Min(1.0, pos / dur)) : 0.0f;

            DrawLeftVCenteredText(g, FormatMMSS((int)pos), _fTimeSmall, InkPri, x0 + U(36f), U(90f));
            bool seekHov = (_hoveredId == "media_seek") || _isScrubbingSeek;
            float barH = seekHov ? U(5.6f) : U(4.0f);
            RectangleF progTrack = new RectangleF(x0 + U(76f), U(90f) - barH * 0.5f, U(104f), barH);
            _lastSeekBarRect = progTrack;
            if (hasMedia && dur > 0.5)
            {
                _hitZones.Add(new HitZone { Id = "media_seek", Rect = new RectangleF(progTrack.X - U(3f), U(80f), progTrack.Width + U(6f), U(20f)) });
            }
            FillPill(g, progTrack, Nw(60));
            if (pct > 0.005f)
            {
                FillPill(g, new RectangleF(progTrack.X, progTrack.Y, Math.Max(barH, progTrack.Width * pct), progTrack.Height), InkPri);
            }
            DrawRightVCenteredText(g, (!hasMedia) ? "00:00" : (dur > 0.5 ? ("-" + FormatMMSS((int)rem)) : "LIVE"), _fTimeSmall, InkPri, x0 + U(224f), U(90f));

            DrawLyricsBubbleIcon(g, "media_lyrics", x0 + U(52f), U(115f), true);
            DrawMediaPrevIcon(g, "media_prev", x0 + U(94f), U(115f));
            DrawMediaPlayPauseIcon(g, "media_play", x0 + U(136f), U(115f), _media.IsPlaying);
            DrawMediaNextIcon(g, "media_next", x0 + U(178f), U(115f));

            using (SolidBrush div = new SolidBrush(Nw(38)))
            {
                g.FillRectangle(div, x0 + U(234f), U(20f), Math.Max(1f, U(1f)), U(98f));
            }

            float paneX = x0 + U(244f);
            float paneW = Math.Max(U(120f), w - U(244f) - U(32f));

            if (!hasMedia)
            {
                _lastDisplayedLyrics = new LyricLine[0];
                DrawLeftVCenteredText(g, _state.T("♪ Phát một bài hát để xem", "♪ Play a song to see"), _fLyricOff, Nw(135), paneX, U(60f));
                DrawLeftVCenteredText(g, _state.T("lời bài hát đồng bộ LRCLIB.", "synced lyrics from LRCLIB."), _fLyricOff, Nw(100), paneX, U(83f));
                return;
            }

            int activeSlot;
            LyricLine[] win = _media.GetDisplayLyricsWindow(out activeSlot);
            _lastDisplayedLyrics = win;

            if (win.Length == 0)
            {
                string stMsg = !string.IsNullOrEmpty(_media.CurrentLyricLine) ? _media.CurrentLyricLine : "Loading lyrics...";
                GraphicsState stEmpty = g.Save();
                g.SetClip(new RectangleF(paneX, U(50f), paneW, U(36f)), CombineMode.Intersect);
                DrawLeftVCenteredText(g, stMsg, _fLyricOff, Nw(165), paneX, U(68f));
                g.Restore(stEmpty);
            }
            else
            {
                double effPos = _media.GetEffectiveLyricsPosSec();
                float lyY = U(27.5f);
                for (int i = 0; i < win.Length; i++)
                {
                    bool active = (i == activeSlot);
                    string lid = "lyric_seek_" + i;
                    RectangleF lRect = new RectangleF(paneX - U(4f), lyY - U(10f), paneW + U(6f), U(20f));
                    _hitZones.Add(new HitZone { Id = lid, Rect = lRect });
                    bool lHov = (_hoveredId == lid);

                    if (lHov) FillRoundRect(g, lRect, U(6f), Nw(28));

                    string fullTxt = win[i].Text ?? "";
                    Font fPrimary = active ? _fLyricOn : _fLyricOff;
                    Font fCompact = active ? _fLyricOnFit : _fLyricOffFit;
                    Font useFont = fPrimary;
                    SizeF lSz = g.MeasureString(fullTxt, useFont);
                    if (lSz.Width > paneW + U(2f))
                    {
                        useFont = fCompact;
                        lSz = g.MeasureString(fullTxt, useFont);
                    }

                    RectangleF lineViewport = new RectangleF(paneX, lyY - U(10.5f), paneW, U(21f));
                    if (active)
                    {
                        DrawKaraokeLyricLine(g, win[i], fullTxt, effPos, true, useFont, lineViewport, false);
                    }
                    else
                    {
                        Color lineCol = lHov
                            ? ToCrispInk(Nw(220))
                            : ((activeSlot >= 0 && i < activeSlot) ? ToCrispInk(Nw(142)) : ToCrispInk(Nw(96)));
                        GraphicsState stLine = g.Save();
                        g.SetClip(lineViewport, CombineMode.Intersect);
                        if (lSz.Width <= paneW + U(2f))
                        {
                            DrawLeftVCenteredText(g, fullTxt, useFont, lineCol, paneX, lyY);
                        }
                        else
                        {
                            float maxShift = lSz.Width - paneW + U(8f);
                            float panT = lHov ? (float)(0.5 - 0.5 * Math.Cos(_frameClock.Elapsed.TotalSeconds * 1.35)) : 0f;
                            float drawX = (float)Math.Round(paneX - maxShift * panT);
                            float drawY = (float)Math.Round(lyY - lSz.Height * 0.5f);
                            using (SolidBrush lb = new SolidBrush(lineCol))
                            {
                                g.DrawString(fullTxt, useFont, lb, drawX, drawY);
                            }
                        }
                        g.Restore(stLine);
                    }
                    lyY += U(21.5f);
                }

                // Bottom Sync Status & 1-Click Fine-Tuning Bar (aligned with media buttons at U(115f))
                float barCy = U(115.5f);
                double totOff = _media.TotalLyricsOffsetSec;
                string syncInfo;
                Color syncCol;
                if (activeSlot < 0)
                {
                    syncInfo = (Math.Abs(totOff) >= 0.2)
                        ? string.Format(CultureInfo.InvariantCulture, "\u266A {0} ({1:+0.0;-0.0}s)", _state.T("\u0110ang d\u1EA1o \u0111\u1EA7u", "Intro"), totOff)
                        : ("\u266A " + _state.T("\u0110ang d\u1EA1o \u0111\u1EA7u...", "Waiting for vocals..."));
                    syncCol = IsLight ? Color.FromArgb(255, 2, 132, 199) : Color.FromArgb(255, 125, 211, 252);
                }
                else if (Math.Abs(totOff) >= 0.1)
                {
                    syncInfo = string.Format(CultureInfo.InvariantCulture, "{0}: {1:+0.0;-0.0}s", _state.T("Nh\u1ECBp MV", "Offset"), totOff);
                    syncCol = ToCrispInk(Nw(155));
                }
                else
                {
                    syncInfo = "\u266A " + _state.T("\u0110\u00E3 kh\u1EDBp l\u1EDDi", "Synced");
                    syncCol = ToCrispInk(Nw(130));
                }
                DrawLeftVCenteredText(g, syncInfo, _fTimeSmall, syncCol, paneX, barCy);

                float pH = U(16.5f);
                float rx = paneX + paneW;
                RectangleF rAdd  = new RectangleF(rx - U(34f), barCy - pH * 0.5f, U(34f), pH);
                RectangleF rNow  = new RectangleF(rAdd.X - U(4f) - U(68f), barCy - pH * 0.5f, U(68f), pH);
                RectangleF rSub  = new RectangleF(rNow.X - U(4f) - U(34f), barCy - pH * 0.5f, U(34f), pH);

                _hitZones.Add(new HitZone { Id = "lyr_off_sub", Rect = rSub });
                _hitZones.Add(new HitZone { Id = "lyr_sync_now", Rect = rNow });
                _hitZones.Add(new HitZone { Id = "lyr_off_add", Rect = rAdd });

                FillPill(g, rSub, _hoveredId == "lyr_off_sub" ? Nw(68) : Nw(34));
                FillPill(g, rNow, _hoveredId == "lyr_sync_now" ? Color.FromArgb(210, 14, 165, 233) : Color.FromArgb(145, 2, 132, 199));
                FillPill(g, rAdd, _hoveredId == "lyr_off_add" ? Nw(68) : Nw(34));

                DrawCenteredText(g, "-0.5s", _fTimeSmall, InkPri, rSub.X + rSub.Width * 0.5f, barCy);
                DrawCenteredText(g, _state.T("H\u00E1t c\u00E2u \u0111\u1EA7u", "Sync start"), _fTimeSmall, Color.White, rNow.X + rNow.Width * 0.5f, barCy);
                DrawCenteredText(g, "+0.5s", _fTimeSmall, InkPri, rAdd.X + rAdd.Width * 0.5f, barCy);
            }
        }

        private void DrawKaraokeLyricLine(Graphics g, LyricLine line, string fallbackTxt, double posSec, bool isSynced, Font font, RectangleF viewport, bool centerWhenFits)
        {
            string txt = (line != null && !string.IsNullOrEmpty(line.Text)) ? line.Text : (fallbackTxt ?? "");
            if (string.IsNullOrEmpty(txt)) return;

            using (StringFormat sf = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                sf.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
                SizeF fullSz = g.MeasureString(txt, font, 10000, sf);
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
                            sf.SetMeasurableCharacterRanges(new CharacterRange[] { new CharacterRange(cStart, cLen) });
                            Region[] regs = g.MeasureCharacterRanges(txt, font, new RectangleF(0f, 0f, 10000f, fullSz.Height + 24f), sf);
                            if (regs != null && regs.Length > 0 && regs[0] != null)
                            {
                                RectangleF wb = regs[0].GetBounds(g);
                                wLeft = wb.X;
                                wRight = wb.Right;
                                regs[0].Dispose();
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

                Color unsungCol = IsLight ? Color.FromArgb(255, 138, 150, 170) : Color.FromArgb(255, 118, 128, 146);
                Color edgeCol   = IsLight ? Color.FromArgb(255, 62, 78, 104)   : Color.FromArgb(255, 194, 205, 224);
                Color sungCol   = InkPri;

                GraphicsState stVp = g.Save();
                g.SetClip(viewport, CombineMode.Intersect);

                if (sweepPx < totalW + U(1f))
                {
                    using (SolidBrush bBase = new SolidBrush(unsungCol))
                    {
                        g.DrawString(txt, font, bBase, new PointF(drawX, drawY), sf);
                    }
                }

                float splitX = drawX + sweepPx;
                float featherW = U(4.5f);

                if (sweepPx > 0.5f && sweepPx < totalW)
                {
                    GraphicsState stEdge = g.Save();
                    g.SetClip(new RectangleF(splitX - U(1f), viewport.Y - 4f, featherW, viewport.Height + 8f), CombineMode.Intersect);
                    using (SolidBrush bEdge = new SolidBrush(edgeCol))
                    {
                        g.DrawString(txt, font, bEdge, new PointF(drawX, drawY), sf);
                    }
                    g.Restore(stEdge);
                }

                if (sweepPx > 0.4f)
                {
                    GraphicsState stSung = g.Save();
                    float sungW = Math.Max(0f, splitX - (viewport.X - 4f));
                    if (sungW > 0.4f)
                    {
                        g.SetClip(new RectangleF(viewport.X - 4f, viewport.Y - 4f, sungW, viewport.Height + 8f), CombineMode.Intersect);
                        using (SolidBrush bSung = new SolidBrush(sungCol))
                        {
                            g.DrawString(txt, font, bSung, new PointF(drawX, drawY), sf);
                        }
                    }
                    g.Restore(stSung);
                }

                g.Restore(stVp);
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
            DrawLeftVCenteredText(g, statusLine, _fMusicSub, Nw(155), x0 + U(44f), U(71f));

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
                DrawLeftVCenteredText(g, "Daily Goal:", _fAxis, Nw(165), x0 + U(216f), U(36f));
                int[] goals = new int[] { 1500, 2000, 2500, 3000 };
                string[] gLabels = new string[] { "1.5L", "2.0L", "2.5L", "3.0L" };
                for (int i = 0; i < 4; i++)
                {
                    bool sel = (_state.WaterGoalMl == goals[i]);
                    DrawNusaPillButton(g, "wgoal_set_" + i, new RectangleF(x0 + U(216f + i * 42f), U(45f), U(39f), U(22f)),
                        gLabels[i], sel ? Color.FromArgb(220, 37, 99, 235) : Nw(34));
                }

                DrawLeftVCenteredText(g, "Cup Size:", _fAxis, Nw(165), x0 + U(216f), U(76f));
                int[] cups = new int[] { 200, 250, 350, 500 };
                for (int i = 0; i < 4; i++)
                {
                    bool sel = (_state.WaterCupSizeMl == cups[i]);
                    DrawNusaPillButton(g, "wcup_set_" + i, new RectangleF(x0 + U(216f + i * 42f), U(85f), U(39f), U(22f)),
                        cups[i].ToString(), sel ? Color.FromArgb(220, 14, 165, 233) : Nw(34));
                }

                DrawNusaPillButton(g, "water_reset_zero", new RectangleF(x0 + U(216f), U(111f), U(165f), U(18f)),
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
            DrawLeftVCenteredText(g, "Shelf", _fTitle, InkPri, x0 + U(15f), U(19f));

            DrawNusaPillButton(g, "shelf_browse", new RectangleF(x0 + U(58f), U(10f), U(46f), U(18f)), "+ Add", Color.FromArgb(165, 14, 116, 204));
            if (_state.ShelfFiles.Count > 0)
            {
                DrawNusaPillButton(g, "shelf_clear", new RectangleF(x0 + U(108f), U(10f), U(46f), U(18f)), "Clear", Nw(38));
            }

            RectangleF zone = new RectangleF(x0 + U(42f), U(33f), U(338f), U(90f));
            bool zoneHov = (_hoveredId == "shelf_browse_zone");
            using (GraphicsPath zp = CreateRoundRect(zone, U(16f)))
            using (SolidBrush zb = new SolidBrush((_isDragHover || zoneHov) ? Nw(24) : Nw(8)))
            using (Pen zPen = new Pen(Nw(_isDragHover ? 210 : (zoneHov ? 130 : 76)), U(1.4f)))
            {
                zPen.DashStyle = DashStyle.Dash;
                g.FillPath(zb, zp);
                g.DrawPath(zPen, zp);
            }

            if (_state.ShelfFiles.Count == 0)
            {
                _hitZones.Add(new HitZone { Id = "shelf_browse_zone", Rect = zone });

                float icx = zone.X + U(169f);
                float icy = zone.Y + U(28f);
                using (Pen tp = new Pen(Nw(190), U(1.8f)))
                {
                    g.DrawRectangle(tp, icx - U(12f), icy - U(2f), U(24f), U(10f));
                    g.DrawLine(tp, icx, icy - U(14f), icx, icy - U(4f));
                    g.DrawLine(tp, icx - U(4f), icy - U(8f), icx, icy - U(4f));
                    g.DrawLine(tp, icx + U(4f), icy - U(8f), icx, icy - U(4f));
                }

                DrawCenteredText(g, _state.T("Kéo thả tệp vào đây hoặc nhấp để chọn", "Drop files here or click to browse"), _fShelfMain, InkPri, zone.X + U(169f), zone.Y + U(56f));
                DrawCenteredText(g, _state.T("Nhấp để mở · Giữ kéo để lôi ra ngoài", "Click to open · Drag out anytime"), _fShelfSub, Nw(135), zone.X + U(169f), zone.Y + U(73f));
            }
            else
            {
                int count = Math.Min(5, _state.ShelfFiles.Count);
                for (int i = 0; i < count; i++)
                {
                    string fp = _state.ShelfFiles[i];
                    string name = Path.GetFileName(fp);
                    float cx = x0 + U(46f + i * 67f);
                    RectangleF cardRect = new RectangleF(cx, U(35f), U(63f), U(85f));
                    string itemId = "shelf_item_" + i;
                    string delId  = "shelf_del_" + i;

                    bool cardHov = (_hoveredId == itemId || _hoveredId == delId);
                    if (cardHov)
                    {
                        FillRoundRect(g, cardRect, U(10f), Nw(30));
                    }

                    _hitZones.Add(new HitZone { Id = itemId, Rect = cardRect });

                    RectangleF thumb = new RectangleF(cx + U(11.5f), U(40f), U(40f), U(40f));
                    FillRoundRect(g, thumb, U(9f), IsLight ? Color.FromArgb(228, 235, 247) : Color.FromArgb(38, 44, 62));

                    bool drewSysIcon = false;
                    try
                    {
                        if (File.Exists(fp))
                        {
                            using (Icon ico = Icon.ExtractAssociatedIcon(fp))
                            {
                                if (ico != null)
                                {
                                    g.DrawIcon(ico, new Rectangle((int)(thumb.X + U(6f)), (int)(thumb.Y + U(6f)), (int)U(28f), (int)U(28f)));
                                    drewSysIcon = true;
                                }
                            }
                        }
                    }
                    catch { }
                    if (!drewSysIcon)
                    {
                        DrawCenteredText(g, Path.GetExtension(fp).ToUpperInvariant(), _fAxis, InkPri, thumb.X + thumb.Width * 0.5f, thumb.Y + thumb.Height * 0.5f);
                    }

                    DrawCenteredText(g, Truncate(name, 9), _fShelfSub, InkPri, cx + U(31.5f), U(93f));
                    DrawCenteredText(g, _state.T("Mở / Kéo", "Open/Drag"), _fAxis, Nw(130), cx + U(31.5f), U(108f));

                    // Mini 'X' remove button on top-right of each file card
                    RectangleF delRect = new RectangleF(cardRect.Right - U(16f), cardRect.Y + U(2f), U(14f), U(14f));
                    _hitZones.Add(new HitZone { Id = delId, Rect = delRect });
                    if (cardHov)
                    {
                        using (SolidBrush rb = new SolidBrush(_hoveredId == delId ? Color.FromArgb(235, 239, 68, 68) : Color.FromArgb(140, 0, 0, 0)))
                        {
                            g.FillEllipse(rb, delRect);
                        }
                        using (Pen xp = new Pen(Color.FromArgb(255, 255, 255), U(1.3f)))
                        {
                            float xcx = delRect.X + delRect.Width * 0.5f, xcy = delRect.Y + delRect.Height * 0.5f;
                            g.DrawLine(xp, xcx - U(2.5f), xcy - U(2.5f), xcx + U(2.5f), xcy + U(2.5f));
                            g.DrawLine(xp, xcx + U(2.5f), xcy - U(2.5f), xcx - U(2.5f), xcy + U(2.5f));
                        }
                    }
                }
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
            using (SolidBrush wBr = new SolidBrush(InkPri))
            using (SolidBrush dBr = new SolidBrush(ToCrispInk(Nw(195))))
            {
                g.DrawString(heroBig, _fHeroBig, wBr, (float)Math.Round(x0 + U(38f)), (float)Math.Round(baseY));
                g.DrawString(heroSub, _fHeroUnit, dBr, (float)Math.Round(x0 + U(38f) + bigSz.Width - U(4f)), (float)Math.Round(U(49f) - _fHeroUnit.Height * 0.5f));
            }

            DrawLeftVCenteredText(g, detailLine, _fMusicSub, Nw(215), x0 + U(42f), U(71f));

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

            DrawLeftVCenteredText(g, label, _fPill, InkPri, x + U(26f), y + U(14f));
            DrawRightVCenteredText(g, val, _fAxis, Nw(245), x + w - U(7f), y + U(14f));
        }

        // ==================== SHARED NUSA VECTOR WIDGETS ====================
        private void DrawBatteryRing(Graphics g, float cx, float cy)
        {
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

        private void DrawOsdHudRow(Graphics g, float x, float cy, float w, float alpha01)
        {
            int a = Math.Max(0, Math.Min(255, (int)(255f * alpha01)));
            if (a <= 4) return;
            float iconCx = x + U(9f);
            string label, rightTxt;
            Color barStart, barEnd, labelCol;

            if (_osdKind == OsdKind.Brightness)
            {
                DrawSunVector(g, iconCx, cy, _osdPercent, a);
                string dir = (_osdDelta > 0) ? " \u25B2" : ((_osdDelta < 0) ? " \u25BC" : "");
                label = _state.T("\u0110\u1ED9 s\u00E1ng", "Brightness") + dir;
                rightTxt = string.Format(CultureInfo.InvariantCulture, "{0}%", _osdPercent);
                barStart = Color.FromArgb(a, 245, 158, 11); barEnd = Color.FromArgb(a, 253, 224, 71);
                labelCol = Nw((int)(225 * alpha01));
            }
            else if (_osdKind == OsdKind.Battery)
            {
                DrawBatteryRing(g, iconCx, cy);
                if (_batteryStateKind == 1)
                {
                    label = _state.T("\u0110ang s\u1EA1c pin \u26A1", "Charging \u26A1");
                    barStart = Color.FromArgb(a, 22, 163, 74); barEnd = Color.FromArgb(a, 74, 222, 128);
                    labelCol = IsLight ? Color.FromArgb(a, 5, 150, 105) : Color.FromArgb(a, 74, 222, 128);
                }
                else if (_batteryStateKind == 2)
                {
                    label = _state.T("Pin y\u1EBFu \u00B7 C\u1EA7n s\u1EA1c", "Low Battery");
                    barStart = Color.FromArgb(a, 220, 38, 38); barEnd = Color.FromArgb(a, 251, 146, 60);
                    labelCol = IsLight ? Color.FromArgb(a, 220, 38, 38) : Color.FromArgb(a, 252, 129, 129);
                }
                else
                {
                    label = _state.T("Pin b\u00ECnh th\u01B0\u1EDDng", "Battery Normal");
                    barStart = Color.FromArgb(a, 16, 185, 129); barEnd = Color.FromArgb(a, 52, 211, 153);
                    labelCol = Nw((int)(225 * alpha01));
                }
                rightTxt = string.Format(CultureInfo.InvariantCulture, "{0}%", _osdPercent);
            }
            else
            {
                Color spkCol = _osdMuted ? Color.FromArgb(248, 113, 113) : (IsLight ? Color.FromArgb(15, 23, 42) : Color.FromArgb(255, 255, 255));
                DrawSpeakerVector(g, iconCx, cy, _osdMuted, _osdPercent, spkCol, a);
                if (_osdMuted)
                {
                    label = _state.T("\u0110\u00E3 t\u1EAFt ti\u1EBFng", "Muted");
                    rightTxt = _state.T("T\u1EAFt", "Mute");
                    barStart = Color.FromArgb(a, 239, 68, 68); barEnd = Color.FromArgb(a, 248, 113, 113);
                    labelCol = IsLight ? Color.FromArgb(a, 220, 38, 38) : Color.FromArgb(a, 248, 113, 113);
                }
                else
                {
                    string dir = (_osdDelta > 0) ? " \u25B2" : ((_osdDelta < 0) ? " \u25BC" : "");
                    label = _state.T("\u00C2m l\u01B0\u1EE3ng", "Volume") + dir;
                    rightTxt = string.Format(CultureInfo.InvariantCulture, "{0}%", _osdPercent);
                    barStart = Color.FromArgb(a, 255, 255, 255); barEnd = Color.FromArgb(a, 255, 255, 255);
                    labelCol = Color.FromArgb(a, 255, 255, 255);
                }
            }

            float barX = x + U(24f), barW = Math.Max(U(60f), w - U(62f));
            DrawLeftVCenteredText(g, label, _fAxis, labelCol, barX, cy - U(5.8f));
            DrawRightVCenteredText(g, rightTxt, _fPill, Color.FromArgb(a, InkPri.R, InkPri.G, InkPri.B), x + w, cy);

            RectangleF track = new RectangleF(barX, cy + U(3.2f), barW, U(4.6f));
            FillPill(g, track, Color.FromArgb(Math.Max(0, Math.Min(255, (int)(74 * alpha01))), 255, 255, 255));
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

        private static string ShortFileName(string name, int maxChars)
        {
            if (string.IsNullOrEmpty(name)) return "File";
            if (name.Length <= maxChars) return name;
            string ext = "";
            try { ext = Path.GetExtension(name) ?? ""; } catch { ext = ""; }
            if (ext.Length >= 2 && ext.Length <= 7 && maxChars > ext.Length + 6)
            {
                int headLen = maxChars - ext.Length - 2;
                return name.Substring(0, headLen).TrimEnd() + ".." + ext;
            }
            return name.Substring(0, Math.Max(4, maxChars - 2)).TrimEnd() + "..";
        }

        private void DrawDownloadHeaderBadge(Graphics g, string id, float cx, float cy)
        {
            RectangleF hit = new RectangleF(cx - U(11f), cy - U(10f), U(22f), U(20f));
            _hitZones.Add(new HitZone { Id = id, Rect = hit });
            bool hov = (_hoveredId == id);
            Color pillBg = _dlSnap.IsCompleted
                ? Color.FromArgb(hov ? 95 : 65, 16, 185, 129)
                : Color.FromArgb(hov ? 95 : 62, 14, 165, 233);
            FillRoundRect(g, new RectangleF(cx - U(9.5f), cy - U(8.5f), U(19f), U(17f)), U(6f), pillBg);
            DrawDownloadVectorIcon(g, cx, cy, _dlSnap.IsCompleted, _dlSnap.IsStoreApp, 255, 0.78f);
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

        private void DrawDownloadHudRow(Graphics g, float x, float cy, float w, float alpha01)
        {
            int a = Math.Max(0, Math.Min(255, (int)(255f * alpha01)));
            if (a <= 4) return;

            DrawDownloadVectorIcon(g, x + U(9f), cy, _dlSnap.IsCompleted, _dlSnap.IsStoreApp, a, 1.0f);

            string shortName = ShortFileName(_dlSnap.FileName, _dlSnap.IsStoreApp ? 18 : 21);
            string label;
            string rightTxt;
            Color labelCol;

            if (_dlSnap.IsCompleted)
            {
                label = _state.T("T\u1EA3i xong \u00B7 ", "Saved \u00B7 ") + shortName;
                long doneBytes = _dlSnap.TotalBytes > 0 ? _dlSnap.TotalBytes : _dlSnap.DownloadedBytes;
                rightTxt = doneBytes > 0 ? DownloadMonitor.FormatBytes(doneBytes) : "100%";
                labelCol = IsLight ? Color.FromArgb(a, 5, 150, 105) : Color.FromArgb(a, 74, 222, 128);
            }
            else if (_dlSnap.IsStoreApp)
            {
                label = "Store \u00B7 " + shortName;
                rightTxt = _dlSnap.HasExactPercent
                    ? string.Format(CultureInfo.InvariantCulture, "{0}%", (int)Math.Round(_dlSnap.Progress01 * 100f))
                    : (_dlSnap.SpeedBytesPerSec > 1024.0 ? DownloadMonitor.FormatSpeed(_dlSnap.SpeedBytesPerSec) : _state.T("\u0110ang t\u1EA3i", "Active"));
                labelCol = Color.FromArgb(a, InkPri.R, InkPri.G, InkPri.B);
            }
            else
            {
                if (_dlSnap.DownloadedBytes > 0 && _dlSnap.SpeedBytesPerSec > 1024.0)
                {
                    label = ShortFileName(_dlSnap.FileName, 15) + " \u00B7 " + DownloadMonitor.FormatBytes(_dlSnap.DownloadedBytes);
                    rightTxt = DownloadMonitor.FormatSpeed(_dlSnap.SpeedBytesPerSec);
                }
                else
                {
                    label = _state.T("\u0110ang t\u1EA3i \u00B7 ", "DL \u00B7 ") + shortName;
                    rightTxt = _dlSnap.DownloadedBytes > 0
                        ? DownloadMonitor.FormatBytes(_dlSnap.DownloadedBytes)
                        : _state.T("M\u1EDBi b\u1EAFt \u0111\u1EA7u", "Starting");
                }
                labelCol = Color.FromArgb(a, InkPri.R, InkPri.G, InkPri.B);
            }

            float barX = x + U(24f);
            SizeF rSz = g.MeasureString(rightTxt, _fPill);
            float rightReserve = Math.Max(U(54f), rSz.Width + U(10f));
            float barW = Math.Max(U(64f), w - U(24f) - rightReserve);

            GraphicsState stLbl = g.Save();
            g.SetClip(new RectangleF(barX, cy - U(15f), barW + U(4f), U(16f)), CombineMode.Intersect);
            DrawLeftVCenteredText(g, label, _fAxis, labelCol, barX, cy - U(5.8f));
            g.Restore(stLbl);

            DrawRightVCenteredText(g, rightTxt, _fPill, Color.FromArgb(a, InkPri.R, InkPri.G, InkPri.B), x + w, cy);

            RectangleF track = new RectangleF(barX, cy + U(3.2f), barW, U(4.6f));
            Color trackCol = IsLight
                ? Color.FromArgb(Math.Max(0, Math.Min(255, (int)(55 * alpha01))), 15, 23, 42)
                : Color.FromArgb(Math.Max(0, Math.Min(255, (int)(68 * alpha01))), 255, 255, 255);
            FillPill(g, track, trackCol);

            if (_dlSnap.IsCompleted)
            {
                using (LinearGradientBrush lgb = new LinearGradientBrush(
                    track, Color.FromArgb(a, 16, 185, 129), Color.FromArgb(a, 74, 222, 128), LinearGradientMode.Horizontal))
                {
                    FillPillBrush(g, track, lgb);
                }
            }
            else if (_dlSnap.HasExactPercent)
            {
                float frac = Math.Max(0.03f, Math.Min(1.0f, _dlAnimBar));
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
                float segW = Math.Max(track.Height * 2.2f, track.Width * 0.40f);
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

        private void DrawEqualizerBars(Graphics g, float cx, float cy)
        {
            bool active = ((_media.HasMedia && _media.IsPlaying) || IsCollapsedLyricsActive);
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
                    if (active)
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
                    DrawCenteredText(g, PageNames[i], _fAxis, Nw(225), cx, cy - U(11f));
                }
                curX += w + gap;
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
            if (!_isExpanded) return;
            if (_hoveredId == "media_vol" && _osd != null) { _osd.AdjustVolume(e.Delta > 0 ? 4 : -4); return; }

            // On TodoList page, scrolling over the right task list scrolls tasks smoothly!
            float x0 = (_canvasW - _curW) * 0.5f;
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

            if (!_isExpanded)
            {
                if (e.Button == MouseButtons.Left && _state.OpenByClick)
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
                TriggerOsdHud(OsdKind.Battery, _batteryPercent, false, 0, 2.5);
            }
            else if (id == "dl_open" || id == "hdr_dl")
            {
                RevealDownloadTarget();
            }
            else if (id == "media_vol")
            {
                if (_osd != null) _osd.ToggleMute();
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
            else if (id.StartsWith("lyric_seek_"))
            {
                int lIdx;
                if (int.TryParse(id.Substring("lyric_seek_".Length), out lIdx) && lIdx >= 0 && lIdx < _lastDisplayedLyrics.Length)
                {
                    _media.SeekToSeconds(Math.Max(0.0, _lastDisplayedLyrics[lIdx].TimeSec + _media.TotalLyricsOffsetSec));
                }
            }
            else if (id == "lyr_off_sub")
            {
                _media.AdjustUserLyricsOffset(-0.5);
            }
            else if (id == "lyr_off_add")
            {
                _media.AdjustUserLyricsOffset(0.5);
            }
            else if (id == "lyr_sync_now")
            {
                _media.SyncFirstLineToNow();
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
            else if (id == "shelf_clear")
            {
                _state.ShelfFiles.Clear();
                _state.Shelf.Clear();
                _state.SaveAll();
            }
            else if (id.StartsWith("shelf_del_"))
            {
                int idx;
                if (int.TryParse(id.Substring("shelf_del_".Length), out idx) && idx >= 0 && idx < _state.ShelfFiles.Count)
                {
                    _state.ShelfFiles.RemoveAt(idx);
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
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
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
