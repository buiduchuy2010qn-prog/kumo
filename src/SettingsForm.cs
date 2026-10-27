using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Windows.Forms;

namespace Kumo
{
    public class SettingsForm : Form
    {
        private readonly AppState _state;
        private readonly NotchForm _notch;

        private float _s = 1.0f;
        private int _tabIndex = 0; // 0:General, 1:Display, 2:Music, 3:Pomodoro, 4:Water, 5:Todo&Shelf, 6:About
        private float _scrollY = 0f;
        private float _maxScrollY = 0f;

        private struct UiZone
        {
            public string Id;
            public RectangleF Rect;
        }
        private readonly List<UiZone> _zones = new List<UiZone>();
        private string _hoverId = null;
        private bool _resetConfirmPending = false;
        private int _updateState = 0;
        private string _latestTag = "";
        private string _latestSetupUrl = "";
        private string _updateInfoVi = "Nhấn kiểm tra để tìm bản mới trên GitHub Releases (hỗ trợ tự tải & cài đè giữ nguyên dữ liệu).";
        private string _updateInfoEn = "Check GitHub Releases for newer builds and overwrite seamlessly without losing data.";

        // Embedded dark TextBox for adding custom To-do items seamlessly
        private TextBox _todoInput;

        // Cached Fonts
        private Font _fAppTitle;
        private Font _fBadge;
        private Font _fNav;
        private Font _fPageHeader;
        private Font _fSecHeader;
        private Font _fRowTitle;
        private Font _fRowDesc;
        private Font _fSeg;
        private Font _fSmall;

        public SettingsForm(AppState state, NotchForm notch)
        {
            _state = state;
            _notch = notch;

            Text = "Kumo Settings";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = _state.IsSettingsLight ? Color.FromArgb(244, 247, 252) : Color.FromArgb(21, 24, 33);
            DoubleBuffered = true;
            KeyPreview = true;

            try
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch { }

            InitScaleAndFonts();
            ClientSize = new Size(S(840), S(580));

            _todoInput = new TextBox();
            _todoInput.BorderStyle = BorderStyle.None;
            _todoInput.BackColor = _state.IsSettingsLight ? Color.FromArgb(245, 248, 253) : Color.FromArgb(20, 23, 32);
            _todoInput.ForeColor = _state.IsSettingsLight ? Color.FromArgb(15, 23, 42) : Color.White;
            _todoInput.Font = _fRowTitle;
            _todoInput.Visible = false;
            _todoInput.KeyDown += (sender, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    AddTypedTodo();
                }
            };
            Controls.Add(_todoInput);

            MouseMove += OnFormMouseMove;
            MouseDown += OnFormMouseDown;
            MouseWheel += OnFormMouseWheel;
            MouseLeave += (sender, e) =>
            {
                if (_hoverId != null)
                {
                    _hoverId = null;
                    Cursor = Cursors.Default;
                    Invalidate();
                }
            };
            KeyDown += (sender, e) =>
            {
                if (e.KeyCode == Keys.Escape && !_todoInput.Focused)
                {
                    Hide();
                }
            };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            NativeMethods.ApplyModernWindowTheme(Handle, !_state.IsSettingsLight);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                // Hide instead of destroying so Notch keeps running smoothly in background (just like Nusa!)
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        private void InitScaleAndFonts()
        {
            float dpi = 96f;
            try
            {
                using (Graphics g = CreateGraphics())
                {
                    if (g.DpiX > 0) dpi = g.DpiX;
                }
            }
            catch { }

            _s = dpi / 96.0f;

            _fAppTitle   = MakeFont(14.5f, FontStyle.Bold);
            _fBadge      = MakeFont(10.0f, FontStyle.Bold);
            _fNav        = MakeFont(12.8f, FontStyle.Bold);
            _fPageHeader = MakeFont(19.0f, FontStyle.Bold);
            _fSecHeader  = MakeFont(11.2f, FontStyle.Bold);
            _fRowTitle   = MakeFont(13.2f, FontStyle.Bold);
            _fRowDesc    = MakeFont(11.2f, FontStyle.Regular);
            _fSeg        = MakeFont(11.5f, FontStyle.Bold);
            _fSmall      = MakeFont(10.5f, FontStyle.Regular);
        }

        private Font MakeFont(float dp, FontStyle style)
        {
            float px = Math.Max(9f, dp * _s);
            try { return new Font("Segoe UI", px, style, GraphicsUnit.Pixel); }
            catch { return new Font(FontFamily.GenericSansSerif, px, style, GraphicsUnit.Pixel); }
        }

        private float U(float dp) { return dp * _s; }
        private int S(float dp) { return (int)Math.Round(dp * _s); }

        public void OpenAtTab(int tab)
        {
            if (tab >= 0 && tab <= 6)
            {
                _tabIndex = tab;
                _scrollY = 0f;
            }
            UpdateTodoInputPlacement();
            if (!Visible)
            {
                Show();
            }
            if (WindowState == FormWindowState.Minimized)
            {
                WindowState = FormWindowState.Normal;
            }
            Activate();
            BringToFront();
            if (_tabIndex == 5 && _todoInput != null && _todoInput.Visible)
            {
                _todoInput.Focus();
            }
            Invalidate();
        }

        private void UpdateTodoInputPlacement()
        {
            if (_todoInput == null) return;
            if (_tabIndex == 5)
            {
                int leftX = S(218 + 28 + 22);
                int topY = S(171) - (int)_scrollY;
                int w = ClientSize.Width - leftX - S(150);
                int h = S(22);
                bool inView = (topY >= S(54) && topY + h <= ClientSize.Height - S(12));
                _todoInput.Bounds = new Rectangle(leftX, topY, Math.Max(80, w), h);
                _todoInput.Visible = inView;
            }
            else
            {
                _todoInput.Visible = false;
            }
        }

        private void AddTypedTodo()
        {
            if (_todoInput == null) return;
            string txt = _todoInput.Text != null ? _todoInput.Text.Trim() : "";
            if (string.IsNullOrEmpty(txt)) return;
            _state.Todos.Insert(0, new TodoItem { Text = txt, Done = false });
            _todoInput.Text = "";
            SaveAndApply();
        }

        private bool IsLight { get { return _state.IsSettingsLight; } }
        private Color ColTextPri { get { return IsLight ? Color.FromArgb(15, 23, 42) : Color.White; } }
        private Color ColTextSec { get { return IsLight ? Color.FromArgb(71, 85, 105) : Color.FromArgb(152, 162, 184); } }
        private Color ColTextMuted { get { return IsLight ? Color.FromArgb(100, 116, 139) : Color.FromArgb(135, 148, 175); } }
        private Color ColSecHdr { get { return IsLight ? Color.FromArgb(2, 132, 199) : Color.FromArgb(96, 165, 250); } }
        private Color ColCardBg { get { return IsLight ? Color.White : Color.FromArgb(29, 33, 46); } }
        private Color ColCardBgHov { get { return IsLight ? Color.FromArgb(248, 251, 255) : Color.FromArgb(34, 39, 54); } }
        private Color ColCardBorder { get { return IsLight ? Color.FromArgb(220, 228, 240) : Color.FromArgb(44, 50, 68); } }
        private Color ColCardBorderHov { get { return IsLight ? Color.FromArgb(165, 192, 230) : Color.FromArgb(62, 72, 98); } }

        private void SyncThemeColors()
        {
            BackColor = IsLight ? Color.FromArgb(244, 247, 252) : Color.FromArgb(21, 24, 33);
            if (_todoInput != null)
            {
                _todoInput.BackColor = IsLight ? Color.FromArgb(243, 247, 253) : Color.FromArgb(20, 23, 32);
                _todoInput.ForeColor = IsLight ? Color.FromArgb(15, 23, 42) : Color.White;
            }
            if (IsHandleCreated)
            {
                NativeMethods.ApplyModernWindowTheme(Handle, !IsLight);
            }
        }

        private void SaveAndApply()
        {
            SyncThemeColors();
            _state.SaveAll();
            if (_notch != null && !_notch.IsDisposed)
            {
                _notch.ApplySettingsLive();
            }
            Invalidate();
        }

        public void SaveSettingsPreview(string filePath, int tab)
        {
            int oldTab = _tabIndex;
            float oldScroll = _scrollY;
            _tabIndex = tab;
            _scrollY = 0f;

            int w = S(840);
            int h = S(580);
            using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                RenderFullSettingsUI(g, w, h, true);
                bmp.Save(filePath, ImageFormat.Png);
            }

            _tabIndex = oldTab;
            _scrollY = oldScroll;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            RenderFullSettingsUI(e.Graphics, ClientSize.Width, ClientSize.Height, false);
        }

        private void RenderFullSettingsUI(Graphics g, int winW, int winH, bool isStaticPreview)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            _zones.Clear();

            float headerH = U(48f);
            float sideW = U(218f);

            // 1. Backgrounds (Eye-Care Soft Daylight by default!)
            g.Clear(IsLight ? Color.FromArgb(244, 247, 252) : Color.FromArgb(21, 24, 33));
            using (SolidBrush sbSide = new SolidBrush(IsLight ? Color.FromArgb(235, 241, 250) : Color.FromArgb(15, 17, 24)))
            {
                g.FillRectangle(sbSide, 0f, headerH, sideW, winH - headerH);
            }
            using (SolidBrush sbTop = new SolidBrush(IsLight ? Color.White : Color.FromArgb(13, 15, 21)))
            {
                g.FillRectangle(sbTop, 0f, 0f, winW, headerH);
            }

            // 2. Top Titlebar
            DrawHeaderBar(g, winW, headerH);

            // 3. Left Sidebar Categories + Live Mascot Card
            DrawSidebar(g, sideW, headerH, winH);

            // 4. Right Content Area (clipped below titlebar)
            RectangleF contentViewport = new RectangleF(sideW, headerH, winW - sideW, winH - headerH);
            GraphicsState clipSt = g.Save();
            g.SetClip(contentViewport);

            float startX = sideW + U(28f);
            float contentW = winW - sideW - U(56f);
            float y = headerH + U(24f) - _scrollY;

            switch (_tabIndex)
            {
                case 0: y = DrawTabGeneral(g, startX, y, contentW); break;
                case 1: y = DrawTabDisplay(g, startX, y, contentW); break;
                case 2: y = DrawTabMusic(g, startX, y, contentW); break;
                case 3: y = DrawTabPomodoro(g, startX, y, contentW); break;
                case 4: y = DrawTabWater(g, startX, y, contentW); break;
                case 5: y = DrawTabTodoShelf(g, startX, y, contentW, isStaticPreview); break;
                case 6: y = DrawTabAbout(g, startX, y, contentW); break;
            }

            float totalUsed = (y + _scrollY) - headerH + U(28f);
            _maxScrollY = Math.Max(0f, totalUsed - contentViewport.Height);
            if (_scrollY > _maxScrollY) _scrollY = _maxScrollY;

            // Scrollbar indicator if needed
            if (_maxScrollY > 2f)
            {
                float viewH = contentViewport.Height;
                float thumbH = Math.Max(U(36f), viewH * (viewH / totalUsed));
                float thumbY = headerH + U(6f) + (_scrollY / _maxScrollY) * (viewH - thumbH - U(12f));
                FillRoundRect(g, new RectangleF(winW - U(9f), thumbY, U(4.5f), thumbH), U(2.2f), IsLight ? Color.FromArgb(95, 100, 116, 139) : Color.FromArgb(90, 255, 255, 255));
            }

            g.Restore(clipSt);

            // Dividers & subtle outer frame
            using (Pen divPen = new Pen(IsLight ? Color.FromArgb(214, 223, 237) : Color.FromArgb(36, 41, 56), 1f))
            {
                g.DrawLine(divPen, 0, headerH, winW, headerH);
                g.DrawLine(divPen, sideW, headerH, sideW, winH);
                g.DrawRectangle(divPen, 0, 0, winW - 1, winH - 1);
            }
        }

        private void DrawHeaderBar(Graphics g, float winW, float headerH)
        {
            // Mini Nusa Notch emblem with cyan-blue glow & "^ ^" eyes
            RectangleF pill = new RectangleF(U(16f), U(13f), U(36f), U(22f));
            FillRoundRect(g, pill, U(8f), Color.Black);
            using (LinearGradientBrush glow = new LinearGradientBrush(
                new RectangleF(pill.X, pill.Y + U(10f), pill.Width, U(12f)),
                Color.FromArgb(200, 12, 180, 252),
                Color.FromArgb(200, 98, 68, 226),
                LinearGradientMode.Horizontal))
            {
                using (GraphicsPath gp = CreateRoundRect(pill, U(8f)))
                {
                    GraphicsState st = g.Save();
                    g.SetClip(gp);
                    g.FillRectangle(glow, pill.X, pill.Y + U(11f), pill.Width, U(12f));
                    g.Restore(st);
                }
            }
            using (Pen eyePen = new Pen(Color.White, U(1.8f)))
            {
                eyePen.StartCap = LineCap.Round;
                eyePen.EndCap = LineCap.Round;
                float cy = pill.Y + U(11f);
                g.DrawLines(eyePen, new PointF[] { new PointF(pill.X + U(9f), cy + U(1.5f)), new PointF(pill.X + U(12f), cy - U(2f)), new PointF(pill.X + U(15f), cy + U(1.5f)) });
                g.DrawLines(eyePen, new PointF[] { new PointF(pill.X + U(21f), cy + U(1.5f)), new PointF(pill.X + U(24f), cy - U(2f)), new PointF(pill.X + U(27f), cy + U(1.5f)) });
            }

            string title = _state.T("Kumo — Quản Lý Cài Đặt", "Kumo Settings");
            DrawLeftText(g, title, _fAppTitle, ColTextPri, U(62f), headerH * 0.5f);

            SizeF tSz = g.MeasureString(title, _fAppTitle);
            string badgeTxt = "v" + KumoBuildInfo.Version;
            SizeF bSz = g.MeasureString(badgeTxt, _fBadge);
            RectangleF verBadge = new RectangleF(U(68f) + tSz.Width, U(14.5f), Math.Max(U(64f), bSz.Width + U(22f)), U(19f));
            FillRoundRect(g, verBadge, U(9.5f), IsLight ? Color.FromArgb(224, 242, 254) : Color.FromArgb(35, 37, 99, 235));
            using (Pen bp = new Pen(Color.FromArgb(90, 59, 130, 246), 1f))
            using (GraphicsPath vp = CreateRoundRect(verBadge, U(9.5f)))
            {
                g.DrawPath(bp, vp);
            }
            DrawCenterText(g, badgeTxt, _fBadge, IsLight ? Color.FromArgb(3, 105, 161) : Color.FromArgb(125, 195, 255), verBadge);

            // Quick Theme Mode Pill + Language Switcher Pill on Right
            string themeLabel = (_state.UiThemeMode == 0) ? (_state.IsVietnamese ? "\u2600 S\u00E1ng to\u00E0n b\u1ED9" : "\u2600 Full Light") : ((_state.UiThemeMode == 1) ? (_state.IsVietnamese ? "\u25CF Tai th\u1ECF \u0110en" : "\u25CF Black Notch") : (_state.IsVietnamese ? "\u25D0 T\u1ED1i" : "\u25D0 Dark"));
            RectangleF themeRect = new RectangleF(winW - U(304f), U(11f), U(110f), U(26f));
            DrawActionPill(g, "toggle_theme", themeRect, themeLabel, false);

            RectangleF langRect = new RectangleF(winW - U(186f), U(11f), U(92f), U(26f));
            DrawActionPill(g, "toggle_lang", langRect, _state.IsVietnamese ? "Ngôn ngữ: VI" : "Lang: EN", false);

            // Minimize & Close Buttons
            RectangleF minRect = new RectangleF(winW - U(82f), U(10f), U(32f), U(28f));
            RectangleF clsRect = new RectangleF(winW - U(44f), U(10f), U(32f), U(28f));
            AddZone("win_min", minRect, true);
            AddZone("win_close", clsRect, true);

            if (_hoverId == "win_min") FillRoundRect(g, minRect, U(6f), IsLight ? Color.FromArgb(226, 232, 240) : Color.FromArgb(35, 255, 255, 255));
            if (_hoverId == "win_close") FillRoundRect(g, clsRect, U(6f), Color.FromArgb(220, 225, 48, 62));

            Color minIconCol = IsLight ? Color.FromArgb(51, 65, 85) : Color.FromArgb(220, 255, 255, 255);
            Color clsIconCol = (_hoverId == "win_close") ? Color.White : minIconCol;
            using (Pen wpMin = new Pen(minIconCol, U(1.6f)))
            using (Pen wp = new Pen(clsIconCol, U(1.6f)))
            {
                float mcx = minRect.X + minRect.Width * 0.5f, mcy = minRect.Y + minRect.Height * 0.5f;
                g.DrawLine(wpMin, mcx - U(5f), mcy, mcx + U(5f), mcy);

                float ccx = clsRect.X + clsRect.Width * 0.5f, ccy = clsRect.Y + clsRect.Height * 0.5f;
                g.DrawLine(wp, ccx - U(4.5f), ccy - U(4.5f), ccx + U(4.5f), ccy + U(4.5f));
                g.DrawLine(wp, ccx + U(4.5f), ccy - U(4.5f), ccx - U(4.5f), ccy + U(4.5f));
            }
        }

        private void DrawSidebar(Graphics g, float sideW, float headerH, float winH)
        {
            string[] labelsVi = new string[]
            {
                "Cài đặt chung",
                "Màn hình & Giao diện",
                "Nhạc & Lời bài hát",
                "Đồng hồ Pomodoro",
                "Nhắc uống nước",
                "Công việc & Kệ tệp",
                "Cập nhật & Giới thiệu"
            };
            string[] labelsEn = new string[]
            {
                "General",
                "Display & Appearance",
                "Music & Lyrics",
                "Pomodoro Timer",
                "Water Reminder",
                "To-do & File Shelf",
                "Update & About"
            };

            float y = headerH + U(16f);
            for (int i = 0; i < 7; i++)
            {
                string id = "tab_" + i;
                RectangleF r = new RectangleF(U(12f), y, sideW - U(24f), U(38f));
                AddZone(id, r, true);

                bool sel = (_tabIndex == i);
                bool hov = (_hoverId == id);

                if (sel)
                {
                    using (LinearGradientBrush lgb = new LinearGradientBrush(
                        r,
                        IsLight ? Color.FromArgb(219, 234, 254) : Color.FromArgb(52, 37, 99, 235),
                        IsLight ? Color.FromArgb(224, 242, 254) : Color.FromArgb(28, 14, 165, 233),
                        LinearGradientMode.Horizontal))
                    using (GraphicsPath gp = CreateRoundRect(r, U(9f)))
                    {
                        g.FillPath(lgb, gp);
                        using (Pen p = new Pen(IsLight ? Color.FromArgb(147, 197, 253) : Color.FromArgb(95, 59, 130, 246), 1f))
                        {
                            g.DrawPath(p, gp);
                        }
                    }
                    FillRoundRect(g, new RectangleF(r.X + U(4f), r.Y + U(10f), U(3f), U(18f)), U(1.5f), IsLight ? Color.FromArgb(37, 99, 235) : Color.FromArgb(56, 189, 248));
                }
                else if (hov)
                {
                    FillRoundRect(g, r, U(9f), IsLight ? Color.FromArgb(224, 232, 245) : Color.FromArgb(20, 255, 255, 255));
                }

                Color fg = IsLight
                    ? (sel ? Color.FromArgb(29, 78, 216) : (hov ? Color.FromArgb(15, 23, 42) : Color.FromArgb(71, 85, 105)))
                    : (sel ? Color.White : (hov ? Color.FromArgb(235, 240, 250) : Color.FromArgb(165, 174, 192)));
                DrawSidebarVectorIcon(g, i, r.X + U(21f), r.Y + r.Height * 0.5f, sel ? (IsLight ? Color.FromArgb(37, 99, 235) : Color.FromArgb(56, 189, 248)) : fg);
                DrawLeftText(g, _state.IsVietnamese ? labelsVi[i] : labelsEn[i], _fNav, fg, r.X + U(38f), r.Y + r.Height * 0.5f);

                y += U(43f);
            }

            // Bottom Live Mascot Mini-Preview Card inside Sidebar!
            RectangleF petCard = new RectangleF(U(12f), winH - U(138f), sideW - U(24f), U(124f));
            FillRoundRect(g, petCard, U(12f), ColCardBg);
            using (Pen p = new Pen(ColCardBorder, 1f))
            using (GraphicsPath gp = CreateRoundRect(petCard, U(12f)))
            {
                g.DrawPath(p, gp);
            }

            // Draw actual miniature 136x28 Nusa Mascot Notch inside the card
            RectangleF miniNotch = new RectangleF(petCard.X + (petCard.Width - U(138f)) * 0.5f, petCard.Y + U(12f), U(138f), U(30f));
            FillRoundRect(g, miniNotch, U(12f), Color.Black);
            using (GraphicsPath np = CreateRoundRect(miniNotch, U(12f)))
            {
                GraphicsState st = g.Save();
                g.SetClip(np);
                using (LinearGradientBrush glow = new LinearGradientBrush(
                    new RectangleF(miniNotch.X, miniNotch.Y + U(12f), miniNotch.Width, U(19f)),
                    Color.FromArgb(215, 12, 180, 252),
                    Color.FromArgb(215, 98, 68, 226),
                    LinearGradientMode.Horizontal))
                {
                    g.FillRectangle(glow, miniNotch.X, miniNotch.Y + U(14f), miniNotch.Width, U(17f));
                }

                float mcx = miniNotch.X + miniNotch.Width * 0.5f;
                float mcy = miniNotch.Y + U(15.5f);
                using (Pen ep = new Pen(Color.White, U(2.2f)))
                {
                    ep.StartCap = LineCap.Round;
                    ep.EndCap = LineCap.Round;
                    g.DrawBezier(ep, new PointF(mcx - U(18f), mcy + U(2f)), new PointF(mcx - U(15f), mcy - U(3.5f)), new PointF(mcx - U(12f), mcy - U(3.5f)), new PointF(mcx - U(9f), mcy + U(2f)));
                    g.DrawBezier(ep, new PointF(mcx + U(2f), mcy + U(2f)), new PointF(mcx + U(5f), mcy - U(3.5f)), new PointF(mcx + U(8f), mcy - U(3.5f)), new PointF(mcx + U(11f), mcy + U(2f)));
                }
                // Mini white waving hand
                float hx = mcx + U(23f), hy = mcy + U(3.5f);
                using (SolidBrush wb = new SolidBrush(Color.White))
                using (Pen fp = new Pen(Color.White, U(1.5f)))
                {
                    fp.StartCap = LineCap.Round; fp.EndCap = LineCap.Round;
                    g.FillEllipse(wb, hx - U(3f), hy - U(2f), U(6f), U(4.5f));
                    g.DrawLine(fp, hx - U(1.8f), hy + U(0.3f), hx - U(4.0f), hy - U(0.4f));
                    g.DrawLine(fp, hx - U(1.2f), hy - U(1.0f), hx - U(1.8f), hy - U(4.0f));
                    g.DrawLine(fp, hx + U(0.3f), hy - U(1.2f), hx + U(0.6f), hy - U(4.3f));
                    g.DrawLine(fp, hx + U(1.7f), hy - U(0.8f), hx + U(3.0f), hy - U(3.3f));
                }
                g.Restore(st);
            }

            DrawCenterText(
                g,
                _state.MascotEnabled
                    ? _state.T("Mascot đang bật · Tự chào khi quay lại", "Mascot Active · Auto-greets on return")
                    : _state.T("Mascot đang tắt · Chỉ hiện đồng hồ", "Mascot Off · Clock only"),
                _fSmall,
                ColTextSec,
                new RectangleF(petCard.X + U(6f), petCard.Y + U(48f), petCard.Width - U(12f), U(24f)));

            RectangleF waveBtn = new RectangleF(petCard.X + U(14f), petCard.Bottom - U(38f), petCard.Width - U(28f), U(28f));
            DrawActionPill(g, "demo_wave", waveBtn, _state.T("Thử vẫy tay chào ngay", "Test Mascot Wave Now"), true);
        }

        private void DrawSidebarVectorIcon(Graphics g, int idx, float cx, float cy, Color col)
        {
            using (Pen p = new Pen(col, U(1.65f)))
            using (SolidBrush b = new SolidBrush(col))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                p.LineJoin = LineJoin.Round;

                switch (idx)
                {
                    case 0: // Gear
                        g.DrawEllipse(p, cx - U(4.5f), cy - U(4.5f), U(9f), U(9f));
                        for (int a = 0; a < 360; a += 60)
                        {
                            double rad = a * Math.PI / 180.0;
                            g.DrawLine(p, cx + (float)Math.Cos(rad) * U(5f), cy + (float)Math.Sin(rad) * U(5f),
                                          cx + (float)Math.Cos(rad) * U(7.2f), cy + (float)Math.Sin(rad) * U(7.2f));
                        }
                        break;
                    case 1: // Monitor
                        g.DrawRectangle(p, cx - U(7f), cy - U(5.5f), U(14f), U(9.5f));
                        g.DrawLine(p, cx - U(3.5f), cy + U(6.5f), cx + U(3.5f), cy + U(6.5f));
                        g.DrawLine(p, cx, cy + U(4f), cx, cy + U(6.5f));
                        break;
                    case 2: // Music note
                        g.DrawLine(p, cx - U(2f), cy + U(4f), cx - U(2f), cy - U(5.5f));
                        g.DrawLine(p, cx + U(5f), cy + U(2.5f), cx + U(5f), cy - U(6.5f));
                        g.DrawLine(p, cx - U(2f), cy - U(5.5f), cx + U(5f), cy - U(6.5f));
                        g.FillEllipse(b, cx - U(6.2f), cy + U(2f), U(5f), U(4f));
                        g.FillEllipse(b, cx + U(0.8f), cy + U(0.5f), U(5f), U(4f));
                        break;
                    case 3: // Pomodoro clock
                        g.DrawEllipse(p, cx - U(6.5f), cy - U(6f), U(13f), U(13f));
                        g.DrawLine(p, cx, cy + U(0.5f), cx, cy - U(3.2f));
                        g.DrawLine(p, cx, cy + U(0.5f), cx + U(3f), cy + U(2f));
                        break;
                    case 4: // Water drop
                        PointF[] drop = new PointF[]
                        {
                            new PointF(cx, cy - U(6.8f)),
                            new PointF(cx + U(5.5f), cy + U(1.5f)),
                            new PointF(cx, cy + U(6.8f)),
                            new PointF(cx - U(5.5f), cy + U(1.5f))
                        };
                        g.DrawClosedCurve(p, drop, 0.45f, FillMode.Alternate);
                        break;
                    case 5: // Checkmark circle
                        g.DrawEllipse(p, cx - U(6.5f), cy - U(6.5f), U(13f), U(13f));
                        g.DrawLines(p, new PointF[] { new PointF(cx - U(3f), cy), new PointF(cx - U(0.5f), cy + U(2.5f)), new PointF(cx + U(3.5f), cy - U(2.2f)) });
                        break;
                    default: // Sparkle / Info
                        g.DrawEllipse(p, cx - U(6.5f), cy - U(6.5f), U(13f), U(13f));
                        g.DrawLine(p, cx, cy - U(0.5f), cx, cy + U(3.5f));
                        g.FillEllipse(b, cx - U(1.1f), cy - U(4.2f), U(2.2f), U(2.2f));
                        break;
                }
            }
        }

        // ==================== TAB 0: GENERAL (CHUNG) ====================
        private float DrawTabGeneral(Graphics g, float x, float y, float w)
        {
            y = DrawPageTitle(g, x, y,
                _state.T("Cài đặt chung", "General Settings"),
                _state.T("Quản lý khởi động, kích thước tai thỏ, linh vật Kumo và chế độ thu nhỏ thông minh.", "Configure startup, notch size, Kumo mascot, and smart minimal behavior."));

            y = DrawSectionHeader(g, x, y, _state.T("GIAO DIỆN SÁNG / TỐI (EYE-CARE THEME)", "INTERFACE THEME"));
            y = DrawSegmentedCard(g, x, y, w, "seg_theme_",
                _state.T("Chế độ màu giao diện", "Color theme mode"),
                _state.T("Giao diện Sáng dịu mắt giúp đọc chữ rõ nét, không chói tối.", "Soft daylight theme protects your eyes from dark-screen glare."),
                new string[] {
                    _state.T("☀ Sáng toàn bộ", "☀ Full Light"),
                    _state.T("🌗 Tai thỏ Đen", "🌗 Black Notch"),
                    _state.T("🌙 Tối", "🌙 Dark")
                },
                _state.UiThemeMode);
            y += U(8f);

            y = DrawSectionHeader(g, x, y, _state.T("KHỞI ĐỘNG HỆ THỐNG", "STARTUP"));
            y = DrawToggleCard(g, x, y, w, "tog_startup",
                _state.T("Khởi động cùng Windows", "Start with Windows"),
                _state.T("Tự động bật Kumo ngay khi bạn đăng nhập vào máy tính.", "Automatically launch Kumo when you sign in to Windows."),
                _state.StartWithWindows);

            y += U(10f);
            y = DrawSectionHeader(g, x, y, _state.T("TAI THỎ & LINH VẬT KUMO", "NOTCH & KUMO MASCOT"));

            y = DrawSegmentedCard(g, x, y, w, "seg_size_",
                _state.T("Kích thước tai thỏ (Notch size)", "Notch size"),
                _state.T("Đổi tỷ lệ kích thước hiển thị ngay lập tức.", "Applies the new scale immediately."),
                new string[] { "Auto (Nusa)", "90%", "100%", "110%", "125%" },
                _state.NotchSizePreset);

            y = DrawToggleCard(g, x, y, w, "tog_auto_min",
                _state.T("Tự thu nhỏ thông minh khi vướng tab (Auto-Minimal)", "Smart Auto-Minimal on titlebar tabs"),
                _state.T("Tự thu về 44×4 dp khi mở nhiều tab Chrome chạm giữa màn hình để không che nút đóng tab.", "Auto-shrinks to 44×4 dp when Chrome tabs reach the center so close buttons stay uncovered."),
                _state.AutoMinimalEnabled);

            y = DrawToggleCard(g, x, y, w, "tog_always_min",
                _state.T("Luôn thu gọn tối thiểu (Minimal notch)", "Always Minimal notch"),
                _state.T("Luôn giữ tai thỏ ở dạng vạch mảnh 44×4 dp khi không mở rộng.", "Always keep a smaller minimal pill when closed."),
                _state.AlwaysMinimal);

            y = DrawToggleCard(g, x, y, w, "tog_mascot",
                _state.T("Linh vật Kumo (Kumo mascot)", "Kumo mascot"),
                _state.T("Hiện đôi mắt nổi nhẹ sau 15s nghỉ, ngủ gật sau 20.5s và vẫy tay chào khi bạn quay lại.", "A little friend that lives in the notch: peeks out when idle and waves hello on return."),
                _state.MascotEnabled);

            y += U(10f);
            y = DrawSectionHeader(g, x, y, _state.T("TOÀN MÀN HÌNH & ÂM BÁO", "FULL SCREEN & SOUNDS"));

            y = DrawToggleCard(g, x, y, w, "tog_fullscreen",
                _state.T("Ẩn khi xem toàn màn hình (Hide in full screen)", "Hide in full screen"),
                _state.T("Tự động ẩn tai thỏ khi bạn chơi game, xem phim hoặc trình chiếu toàn màn hình.", "Automatically hide during games, full-screen videos, and presentations."),
                _state.HideInFullscreen);

            y = DrawToggleCard(g, x, y, w, "tog_sounds",
                _state.T("Âm báo nhắc nhở (Reminder sounds)", "Reminder sounds"),
                _state.T("Phát âm báo nhẹ khi kết thúc phiên tập trung Pomodoro.", "Play subtle chimes when Pomodoro focus or break timers complete."),
                _state.ReminderSounds);

            return y;
        }

        // ==================== TAB 1: DISPLAY & APPEARANCE ====================
        private float DrawTabDisplay(Graphics g, float x, float y, float w)
        {
            y = DrawPageTitle(g, x, y,
                _state.T("Màn hình & Giao diện", "Display & Appearance"),
                _state.T("Chọn màn hình đặt tai thỏ, cách mở tai thỏ và tốc độ chuyển động lò xo.", "Choose which screen hosts the notch, how it opens, and spring motion speed."));

            y = DrawSectionHeader(g, x, y, _state.T("MÀN HÌNH HIỂN THỊ (NOTCH SCREEN)", "NOTCH SCREEN"));

            Screen[] screens = Screen.AllScreens;
            float cardH = U(108f);
            RectangleF monWrap = new RectangleF(x, y, w, cardH);
            DrawCardShell(g, monWrap);

            float cardW = Math.Min(U(185f), (w - U(36f)) / Math.Max(1, screens.Length));
            float mx = x + U(16f);
            for (int i = 0; i < screens.Length; i++)
            {
                Screen scr = screens[i];
                string id = "mon_" + i;
                RectangleF mr = new RectangleF(mx, y + U(14f), cardW - U(8f), U(80f));
                AddZone(id, mr, false);

                bool sel = (_state.MonitorIndex == i) || (i == 0 && _state.MonitorIndex >= screens.Length);
                bool hov = (_hoverId == id);
                FillRoundRect(g, mr, U(10f), sel ? (IsLight ? Color.FromArgb(219, 234, 254) : Color.FromArgb(45, 37, 99, 235)) : (hov ? (IsLight ? Color.FromArgb(238, 244, 252) : Color.FromArgb(26, 255, 255, 255)) : (IsLight ? Color.FromArgb(245, 248, 253) : Color.FromArgb(18, 20, 28))));
                using (Pen bp = new Pen(sel ? (IsLight ? Color.FromArgb(37, 99, 235) : Color.FromArgb(56, 189, 248)) : ColCardBorder, sel ? 1.8f : 1f))
                using (GraphicsPath gp = CreateRoundRect(mr, U(10f)))
                {
                    g.DrawPath(bp, gp);
                }

                // Mini notch bar at top center of selected monitor preview
                FillRoundRect(g, new RectangleF(mr.X + (mr.Width - U(34f)) * 0.5f, mr.Y + U(5f), U(34f), U(6f)), U(3f),
                    sel ? Color.FromArgb(56, 189, 248) : Color.FromArgb(80, 90, 110));

                string mTitle = string.Format("Display {0}{1}", i + 1, scr.Primary ? " (Main)" : "");
                string mRes = string.Format("{0} × {1}", scr.Bounds.Width, scr.Bounds.Height);
                DrawCenterText(g, mTitle, _fRowTitle, ColTextPri, new RectangleF(mr.X, mr.Y + U(22f), mr.Width, U(22f)));
                DrawCenterText(g, mRes, _fSmall, ColTextSec, new RectangleF(mr.X, mr.Y + U(46f), mr.Width, U(20f)));

                mx += cardW;
            }
            if (screens.Length == 1)
            {
                DrawLeftText(g,
                    _state.T("Kết nối thêm màn hình rời để chọn vị trí đặt tai thỏ.", "Connect another display to choose where the notch appears."),
                    _fRowDesc, ColTextSec, mx + U(12f), y + cardH * 0.5f);
            }
            y += cardH + U(16f);

            y = DrawSectionHeader(g, x, y, _state.T("TƯƠNG TÁC & CHUYỂN ĐỘNG", "APPEARANCE & MOTION"));

            y = DrawSegmentedCard(g, x, y, w, "seg_open_",
                _state.T("Cách mở tai thỏ (Open the notch)", "Open the notch"),
                _state.T("Mở ngay khi lướt chuột vào tai thỏ hoặc chỉ mở khi nhấp chuột trái.", "Expand smoothly on mouse hover or require a left click."),
                new string[] { _state.T("Đưa chuột (Hover)", "Hover"), _state.T("Nhấp chuột (Click)", "Click") },
                _state.OpenByClick ? 1 : 0);

            y = DrawSegmentedCard(g, x, y, w, "seg_anim_",
                _state.T("Tốc độ chuyển động (Animation speed)", "Animation speed"),
                _state.T("'Mượt tự nhiên' nổi nhẹ từ từ chuẩn Nusa; 'Nhanh' phản hồi tức thì.", "'Normal' glides gently like Nusa; 'Fast' snaps open quicker."),
                new string[] { _state.T("Mượt tự nhiên", "Normal"), _state.T("Nhanh nhạy", "Fast") },
                _state.FastAnimation ? 1 : 0);

            y = DrawToggleCard(g, x, y, w, "tog_pagelabels",
                _state.T("Hiện tên trang khi trỏ vào chấm chuyển trang", "Show page names on dot hover"),
                _state.T("Hiển thị tên trang (Music, Pomodoro, Water...) khi đưa chuột qua hàng chấm dưới đáy.", "Display page titles when hovering the bottom navigation dots."),
                _state.ShowPageLabels);

            return y;
        }

        // ==================== TAB 2: MUSIC & LYRICS ====================
        private float DrawTabMusic(Graphics g, float x, float y, float w)
        {
            y = DrawPageTitle(g, x, y,
                _state.T("Nhạc & Lời bài hát", "Music & Lyrics"),
                _state.T("Đồng bộ bài hát từ Spotify, trình duyệt YouTube/Chrome và lời bài hát LRCLIB.", "Sync media playback from Spotify/browsers and real-time lyrics from LRCLIB."));

            y = DrawSectionHeader(g, x, y, _state.T("LỜI BÀI HÁT & SÓNG NHẠC", "LYRICS & VISUALIZER"));

            y = DrawToggleCard(g, x, y, w, "tog_lyrics",
                _state.T("Hiện lời bài hát đồng bộ (Show lyrics)", "Show synced lyrics"),
                _state.T("Tự động tải lời bài hát theo thời gian thực từ LRCLIB (lrclib.net) trong tai thỏ.", "Fetch and display synced lyrics from LRCLIB inside the notch."),
                _state.ShowLyrics);

            y = DrawToggleCard(g, x, y, w, "tog_lyrics_split",
                _state.T("Mở sẵn chế độ chia đôi lời bài hát (520 dp)", "Default to wide split-lyrics view"),
                _state.T("Mở rộng trang Nhạc thành khung đôi hiển thị cả bìa đĩa lẫn 3 dòng lời bài hát.", "Open the Music page in wide split mode showing album art alongside synced lyrics."),
                _state.DefaultLyricsSplit);

            y = DrawToggleCard(g, x, y, w, "tog_eq",
                _state.T("Thanh sóng nhạc Equalizer ở góc phải", "Show audio equalizer bars"),
                _state.T("Hiển thị biểu tượng sóng âm ở góc phải của tai thỏ.", "Display the minimal audio equalizer indicator on the right side of the notch."),
                _state.EqualizerEnabled);

            RectangleF openMusicBtn = new RectangleF(x, y + U(6f), U(210f), U(34f));
            DrawActionPill(g, "jump_page_0", openMusicBtn, _state.T("Mở trang Nhạc trên tai thỏ", "Open Music Page in Notch"), true);
            return y + U(48f);
        }

        // ==================== TAB 3: POMODORO ====================
        private float DrawTabPomodoro(Graphics g, float x, float y, float w)
        {
            y = DrawPageTitle(g, x, y,
                _state.T("Đồng hồ tập trung Pomodoro", "Pomodoro Focus Timer"),
                _state.T("Tùy chỉnh thời lượng phiên làm việc tập trung và thời gian nghỉ giải lao.", "Customize your Focus, Short Break, and Long Break durations."));

            y = DrawSectionHeader(g, x, y, _state.T("THỜI LƯỢNG PHIÊN (DURATIONS)", "DURATIONS"));

            int[] focusOpts = new int[] { 15, 25, 45, 60 };
            int fIdx = Math.Max(0, Array.IndexOf(focusOpts, _state.FocusMinutes));
            y = DrawSegmentedCard(g, x, y, w, "seg_focus_",
                _state.T("Thời gian tập trung (Focus)", "Focus duration"),
                _state.T("Số phút cho mỗi phiên tập trung làm việc sâu.", "Minutes per focus session."),
                new string[] { "15 min", "25 min", "45 min", "60 min" }, fIdx);

            int[] shortOpts = new int[] { 5, 10, 15 };
            int sIdx = Math.Max(0, Array.IndexOf(shortOpts, _state.ShortBreakMinutes));
            y = DrawSegmentedCard(g, x, y, w, "seg_short_",
                _state.T("Nghỉ giải lao ngắn (Short break)", "Short break"),
                _state.T("Thời gian thư giãn mắt sau mỗi phiên tập trung.", "Quick rest after a focus session."),
                new string[] { "5 min", "10 min", "15 min" }, sIdx);

            int[] longOpts = new int[] { 15, 20, 30 };
            int lIdx = Math.Max(0, Array.IndexOf(longOpts, _state.LongBreakMinutes));
            y = DrawSegmentedCard(g, x, y, w, "seg_long_",
                _state.T("Nghỉ giải lao dài (Long break)", "Long break"),
                _state.T("Nghỉ nạp lại năng lượng sau chuỗi 4 phiên làm việc.", "Extended rest after completing a full cycle."),
                new string[] { "15 min", "20 min", "30 min" }, lIdx);

            RectangleF openPomoBtn = new RectangleF(x, y + U(6f), U(230f), U(34f));
            DrawActionPill(g, "jump_page_1", openPomoBtn, _state.T("Mở đồng hồ Pomodoro trên tai thỏ", "Open Pomodoro in Notch"), true);
            return y + U(48f);
        }

        // ==================== TAB 4: WATER REMINDER ====================
        private float DrawTabWater(Graphics g, float x, float y, float w)
        {
            y = DrawPageTitle(g, x, y,
                _state.T("Nhắc uống nước mỗi ngày", "Daily Water Reminder"),
                _state.T("Theo dõi lượng nước đã uống trong ngày và tùy chỉnh mục tiêu cá nhân.", "Track daily hydration progress and customize your target goal & cup size."));

            y = DrawSectionHeader(g, x, y, _state.T("TIẾN ĐỘ HÔM NAY (TODAY'S PROGRESS)", "TODAY'S PROGRESS"));

            RectangleF progCard = new RectangleF(x, y, w, U(92f));
            DrawCardShell(g, progCard);

            float pct = Math.Min(1.0f, (float)_state.WaterDrankMl / Math.Max(1f, (float)_state.WaterGoalMl));
            string progTxt = string.Format("{0} / {1} ml ({2:0}%)", _state.WaterDrankMl, _state.WaterGoalMl, pct * 100f);
            DrawLeftText(g, progTxt, _fPageHeader, ColTextPri, x + U(18f), y + U(28f));

            RectangleF barBg = new RectangleF(x + U(18f), y + U(56f), w - U(280f), U(12f));
            FillRoundRect(g, barBg, U(6f), IsLight ? Color.FromArgb(226, 232, 240) : Color.FromArgb(35, 255, 255, 255));
            if (pct > 0.01f)
            {
                RectangleF barFg = new RectangleF(barBg.X, barBg.Y, Math.Max(U(12f), barBg.Width * pct), barBg.Height);
                using (LinearGradientBrush wGrad = new LinearGradientBrush(barBg, Color.FromArgb(14, 165, 233), Color.FromArgb(59, 130, 246), LinearGradientMode.Horizontal))
                using (GraphicsPath gp = CreateRoundRect(barFg, U(6f)))
                {
                    g.FillPath(wGrad, gp);
                }
            }

            DrawActionPill(g, "water_add_now", new RectangleF(x + w - U(242f), y + U(28f), U(120f), U(34f)),
                string.Format("+ {0} ml", _state.WaterCupSizeMl), true);
            DrawActionPill(g, "water_reset_day", new RectangleF(x + w - U(112f), y + U(28f), U(96f), U(34f)),
                _state.T("Đặt lại 0 ml", "Reset 0 ml"), false);

            y += U(106f);
            y = DrawSectionHeader(g, x, y, _state.T("KẾ HOẠCH (PLAN)", "PLAN"));

            int[] goals = new int[] { 1500, 2000, 2500, 3000 };
            int gIdx = Math.Max(0, Array.IndexOf(goals, _state.WaterGoalMl));
            y = DrawSegmentedCard(g, x, y, w, "seg_wgoal_",
                _state.T("Mục tiêu mỗi ngày (Daily goal)", "Daily goal"),
                _state.T("Tổng lượng nước khuyến nghị cần uống trong ngày.", "Recommended daily water intake target."),
                new string[] { "1.5 L", "2.0 L", "2.5 L", "3.0 L" }, gIdx);

            int[] cups = new int[] { 200, 250, 350, 500 };
            int cIdx = Math.Max(0, Array.IndexOf(cups, _state.WaterCupSizeMl));
            y = DrawSegmentedCard(g, x, y, w, "seg_wcup_",
                _state.T("Dung tích cốc / bình (Bottle size)", "Bottle / Cup size"),
                _state.T("Lượng nước cộng thêm mỗi lần bạn bấm nút '+ Drink' trên tai thỏ.", "Amount added each time you log a drink in the notch."),
                new string[] { "200 ml", "250 ml", "350 ml", "500 ml" }, cIdx);

            return y;
        }

        // ==================== TAB 5: TO-DO & FILE SHELF ====================
        private float DrawTabTodoShelf(Graphics g, float x, float y, float w, bool isStaticPreview)
        {
            y = DrawPageTitle(g, x, y,
                _state.T("Công việc & Kệ lưu trữ tệp", "To-do List & File Shelf"),
                _state.T("Thêm/xóa việc cần làm (đồng bộ trực tiếp lên tai thỏ) và quản lý kệ kéo-thả tệp.", "Add or remove tasks synced live with the notch, and manage your drag-and-drop File Shelf."));

            y = DrawSectionHeader(g, x, y, _state.T("DANH SÁCH VIỆC CẦN LÀM (TO-DO LIST)", "TO-DO LIST"));

            // Input bar card
            RectangleF inputCard = new RectangleF(x, y, w, U(48f));
            DrawCardShell(g, inputCard);
            RectangleF inputBoxBg = new RectangleF(x + U(12f), y + U(8f), w - U(132f), U(32f));
            FillRoundRect(g, inputBoxBg, U(8f), IsLight ? Color.FromArgb(243, 247, 253) : Color.FromArgb(20, 23, 32));
            using (Pen ip = new Pen(IsLight ? Color.FromArgb(203, 213, 225) : Color.FromArgb(52, 60, 82), 1f))
            using (GraphicsPath gp = CreateRoundRect(inputBoxBg, U(8f)))
            {
                g.DrawPath(ip, gp);
            }

            if (isStaticPreview || (_todoInput != null && string.IsNullOrEmpty(_todoInput.Text) && !_todoInput.Focused))
            {
                DrawLeftText(g,
                    _state.T("Nhập công việc mới rồi nhấn Enter...", "Type a new task and press Enter..."),
                    _fRowDesc, ColTextMuted, inputBoxBg.X + U(10f), inputBoxBg.Y + inputBoxBg.Height * 0.5f);
            }

            DrawActionPill(g, "todo_add_btn", new RectangleF(x + w - U(110f), y + U(8f), U(98f), U(32f)),
                _state.T("+ Thêm việc", "+ Add Task"), true);
            y += U(56f);

            // Task list items
            int count = _state.Todos.Count;
            if (count == 0)
            {
                RectangleF emptyCard = new RectangleF(x, y, w, U(48f));
                DrawCardShell(g, emptyCard);
                DrawCenterText(g, _state.T("Chưa có công việc nào. Hãy thêm ở ô phía trên!", "No tasks yet. Add one above!"), _fRowDesc, ColTextSec, emptyCard);
                y += U(56f);
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    TodoItem item = _state.Todos[i];
                    RectangleF row = new RectangleF(x, y, w, U(42f));
                    DrawCardShell(g, row);

                    RectangleF toggleHit = new RectangleF(x, y, w - U(48f), U(42f));
                    AddZone("todo_chk_" + i, toggleHit, false);

                    RectangleF chk = new RectangleF(x + U(14f), y + U(12f), U(18f), U(18f));
                    if (item.Done)
                    {
                        FillRoundRect(g, chk, U(9f), Color.FromArgb(16, 185, 129));
                        using (Pen cp = new Pen(Color.White, U(1.8f)))
                        {
                            g.DrawLines(cp, new PointF[] { new PointF(chk.X + U(4.5f), chk.Y + U(9f)), new PointF(chk.X + U(8f), chk.Y + U(12.5f)), new PointF(chk.X + U(13.5f), chk.Y + U(5.5f)) });
                        }
                    }
                    else
                    {
                        using (Pen cp = new Pen(Color.FromArgb(120, 140, 170), U(1.6f)))
                        {
                            g.DrawEllipse(cp, chk);
                        }
                    }

                    Font tf = item.Done ? new Font(_fRowTitle, FontStyle.Strikeout) : _fRowTitle;
                    DrawLeftText(g, item.Text, tf, item.Done ? ColTextMuted : ColTextPri, x + U(42f), y + U(21f));
                    if (item.Done) tf.Dispose();

                    // Delete 'X' button
                    RectangleF delRect = new RectangleF(x + w - U(38f), y + U(8f), U(26f), U(26f));
                    string delId = "todo_del_" + i;
                    AddZone(delId, delRect, false);
                    bool delHov = (_hoverId == delId);
                    if (delHov) FillRoundRect(g, delRect, U(6f), Color.FromArgb(55, 239, 68, 68));
                    using (Pen dp = new Pen(delHov ? Color.FromArgb(248, 113, 113) : Color.FromArgb(130, 140, 165), U(1.6f)))
                    {
                        float dcx = delRect.X + delRect.Width * 0.5f, dcy = delRect.Y + delRect.Height * 0.5f;
                        g.DrawLine(dp, dcx - U(4f), dcy - U(4f), dcx + U(4f), dcy + U(4f));
                        g.DrawLine(dp, dcx + U(4f), dcy - U(4f), dcx - U(4f), dcy + U(4f));
                    }

                    y += U(47f);
                }
            }

            y += U(10f);
            y = DrawSectionHeader(g, x, y, _state.T("KỆ LƯU TRỮ TỆP KÉO-THẢ (FILE SHELF)", "FILE SHELF"));

            y = DrawToggleCard(g, x, y, w, "tog_shelf_clear",
                _state.T("Làm trống Kệ tệp khi khởi động", "Start with an empty Shelf"),
                _state.T("Tự động dọn danh sách tệp tạm thời của phiên trước khi mở lại ứng dụng.", "Files from your last session are cleared when Kumo starts."),
                _state.ClearShelfOnStart);

            RectangleF shelfInfo = new RectangleF(x, y, w, U(54f));
            DrawCardShell(g, shelfInfo);
            DrawLeftText(g,
                string.Format(_state.T("Hiện đang có {0} tệp trong Kệ lưu trữ.", "Currently holding {0} file(s) in Shelf."), _state.ShelfFiles.Count),
                _fRowTitle, ColTextPri, x + U(16f), y + U(27f));
            DrawActionPill(g, "shelf_clear_now", new RectangleF(x + w - U(142f), y + U(11f), U(126f), U(32f)),
                _state.T("Dọn sạch kệ ngay", "Clear Shelf Now"), false);

            return y + U(64f);
        }

        // ==================== TAB 6: ABOUT & RESET ====================
        private float DrawTabAbout(Graphics g, float x, float y, float w)
        {
            y = DrawPageTitle(g, x, y,
                _state.T("Cập nhật & Giới thiệu", "Update & About"),
                _state.T("Thông tin phiên bản, thư mục lưu trữ cấu hình cục bộ và khôi phục mặc định.", "Version details, local configuration directory, and factory reset."));

            RectangleF hero = new RectangleF(x, y, w, U(112f));
            DrawCardShell(g, hero);

            DrawLeftText(g, "Kumo v" + KumoBuildInfo.Version + " — Official Living Notch", _fPageHeader, Color.White, x + U(20f), y + U(27f));
            DrawLeftText(g,
                _state.T("Tai thỏ sống động siêu nhẹ cho Windows (Native Win32 · ~0% CPU khi nghỉ).", "Ultra-lightweight native living notch for Windows (Native Win32 · ~0% idle CPU)."),
                _fRowDesc, ColTextSec, x + U(20f), y + U(55f));
            DrawLeftText(g,
                _state.T("Hỗ trợ phiên bản động & cài đè trực tiếp không mất dữ liệu từ GitHub Releases.", "Supports dynamic rolling releases & seamless in-place overwrite updates via GitHub."),
                _fRowDesc, ColTextMuted, x + U(20f), y + U(79f));

            y += U(124f);
            y = DrawSectionHeader(g, x, y, _state.T("CẬP NHẬT & CÀI ĐÈ TRỰC TIẾP (OVERWRITE UPDATE)", "CLOUD UPDATE & IN-PLACE OVERWRITE"));

            RectangleF updCard = new RectangleF(x, y, w, U(72f));
            DrawCardShell(g, updCard);
            DrawLeftText(g,
                _state.T("Phiên bản đang chạy: v" + KumoBuildInfo.Version, "Installed version: v" + KumoBuildInfo.Version),
                _fRowTitle, ColTextPri, x + U(16f), y + U(23f));
            DrawLeftText(g,
                _state.T(_updateInfoVi, _updateInfoEn),
                _fSmall, (_updateState == 2) ? (IsLight ? Color.FromArgb(5, 150, 105) : Color.FromArgb(52, 211, 153)) : ColTextSec, x + U(16f), y + U(47f));

            string updBtnTxt;
            if (_updateState == 1) updBtnTxt = _state.T("Đang kiểm tra...", "Checking...");
            else if (_updateState == 2) updBtnTxt = _state.T("Cài đè " + _latestTag, "Install " + _latestTag);
            else if (_updateState == 3) updBtnTxt = _state.T("Cài đè lại " + _latestTag, "Reinstall " + _latestTag);
            else if (_updateState == 4) updBtnTxt = _state.T("Đang cài đè...", "Updating...");
            else updBtnTxt = _state.T("Kiểm tra bản mới", "Check Updates");

            DrawActionPill(g, "cloud_update_btn", new RectangleF(x + w - U(172f), y + U(19f), U(156f), U(34f)), updBtnTxt, (_updateState == 2));
            y += U(84f);
            y = DrawSectionHeader(g, x, y, _state.T("DỮ LIỆU & KHÔI PHỤC CÀI ĐẶT", "DATA & FACTORY RESET"));

            RectangleF actCard = new RectangleF(x, y, w, U(66f));
            DrawCardShell(g, actCard);
            DrawLeftText(g, _state.T("Thư mục lưu cấu hình & dữ liệu", "Configuration & Data Folder"), _fRowTitle, ColTextPri, x + U(16f), y + U(22f));
            DrawLeftText(g, _state.DataDir, _fSmall, ColTextMuted, x + U(16f), y + U(44f));
            DrawActionPill(g, "open_datadir", new RectangleF(x + w - U(156f), y + U(16f), U(140f), U(34f)),
                _state.T("Mở thư mục dữ liệu", "Open Data Folder"), false);
            y += U(76f);

            RectangleF rstCard = new RectangleF(x, y, w, U(64f));
            DrawCardShell(g, rstCard);
            DrawLeftText(g, _state.T("Khôi phục cài đặt gốc", "Reset all settings to defaults"), _fRowTitle, ColTextPri, x + U(16f), y + U(22f));
            DrawLeftText(g, _state.T("Đưa kích thước, chuyển động, Pomodoro và nhắc nước về mặc định.", "Restore size, motion, Pomodoro, and water settings back to default."), _fRowDesc, ColTextSec, x + U(16f), y + U(43f));
            DrawActionPill(g, "reset_defaults", new RectangleF(x + w - U(176f), y + U(15f), U(160f), U(34f)),
                _resetConfirmPending
                    ? _state.T("Nhấn lần nữa để đặt lại!", "Click again to reset!")
                    : _state.T("Khôi phục mặc định", "Reset Settings"),
                _resetConfirmPending);

            return y + U(74f);
        }

        // ==================== REUSABLE UI CARD BUILDERS ====================
        private float DrawPageTitle(Graphics g, float x, float y, string title, string sub)
        {
            DrawLeftText(g, title, _fPageHeader, ColTextPri, x, y + U(10f));
            DrawLeftText(g, sub, _fRowDesc, ColTextSec, x, y + U(35f));
            return y + U(60f);
        }

        private float DrawSectionHeader(Graphics g, float x, float y, string title)
        {
            DrawLeftText(g, title, _fSecHeader, ColSecHdr, x + U(2f), y + U(8f));
            return y + U(24f);
        }

        private void DrawCardShell(Graphics g, RectangleF r)
        {
            FillRoundRect(g, r, U(11f), ColCardBg);
            using (Pen p = new Pen(ColCardBorder, 1f))
            using (GraphicsPath gp = CreateRoundRect(r, U(11f)))
            {
                g.DrawPath(p, gp);
            }
        }

        private float DrawToggleCard(Graphics g, float x, float y, float w, string id, string title, string desc, bool isOn)
        {
            float h = U(64f);
            RectangleF card = new RectangleF(x, y, w, h);
            AddZone(id, card, false);

            bool hov = (_hoverId == id);
            FillRoundRect(g, card, U(11f), hov ? ColCardBgHov : ColCardBg);
            using (Pen p = new Pen(hov ? ColCardBorderHov : ColCardBorder, 1f))
            using (GraphicsPath gp = CreateRoundRect(card, U(11f)))
            {
                g.DrawPath(p, gp);
            }

            DrawLeftText(g, title, _fRowTitle, ColTextPri, x + U(16f), y + U(22f));
            DrawLeftText(g, desc, _fRowDesc, ColTextSec, x + U(16f), y + U(43f));

            // Smooth iOS/Nusa pill Toggle Switch (46 x 25 dp)
            RectangleF sw = new RectangleF(x + w - U(62f), y + (h - U(25f)) * 0.5f, U(46f), U(25f));
            if (isOn)
            {
                using (LinearGradientBrush lgb = new LinearGradientBrush(sw, Color.FromArgb(14, 165, 233), Color.FromArgb(37, 99, 235), LinearGradientMode.Horizontal))
                using (GraphicsPath gp = CreateRoundRect(sw, sw.Height * 0.5f))
                {
                    g.FillPath(lgb, gp);
                }
            }
            else
            {
                FillRoundRect(g, sw, sw.Height * 0.5f, IsLight ? Color.FromArgb(203, 213, 225) : Color.FromArgb(56, 62, 82));
            }

            float knobD = U(19f);
            float knobX = isOn ? (sw.Right - knobD - U(3f)) : (sw.X + U(3f));
            using (SolidBrush wb = new SolidBrush(Color.White))
            {
                g.FillEllipse(wb, knobX, sw.Y + U(3f), knobD, knobD);
            }

            return y + h + U(8f);
        }

        private float DrawSegmentedCard(Graphics g, float x, float y, float w, string idPrefix, string title, string desc, string[] options, int selectedIdx)
        {
            float h = U(68f);
            RectangleF card = new RectangleF(x, y, w, h);
            DrawCardShell(g, card);

            // Measure segmented pills on right first so left text never overlaps!
            float totalSegW = U(6f);
            float[] itemWidths = new float[options.Length];
            for (int i = 0; i < options.Length; i++)
            {
                SizeF sz = g.MeasureString(options[i], _fSeg);
                itemWidths[i] = Math.Max(U(42f), sz.Width + U(14f));
                totalSegW += itemWidths[i] + U(3f);
            }

            RectangleF tray = new RectangleF(x + w - totalSegW - U(14f), y + (h - U(32f)) * 0.5f, totalSegW, U(32f));

            GraphicsState txtClip = g.Save();
            g.SetClip(new RectangleF(x + U(12f), y + U(4f), Math.Max(U(80f), tray.X - x - U(22f)), h - U(8f)), CombineMode.Intersect);
            DrawLeftText(g, title, _fRowTitle, ColTextPri, x + U(16f), y + U(23f));
            DrawLeftText(g, desc, _fRowDesc, ColTextSec, x + U(16f), y + U(45f));
            g.Restore(txtClip);

            FillRoundRect(g, tray, U(8f), IsLight ? Color.FromArgb(234, 240, 248) : Color.FromArgb(18, 21, 30));

            float curX = tray.X + U(3f);
            for (int i = 0; i < options.Length; i++)
            {
                string id = idPrefix + i;
                RectangleF segRect = new RectangleF(curX, tray.Y + U(3f), itemWidths[i], U(26f));
                AddZone(id, segRect, false);

                bool sel = (i == selectedIdx);
                bool hov = (_hoverId == id);
                if (sel)
                {
                    using (LinearGradientBrush lgb = new LinearGradientBrush(segRect, Color.FromArgb(37, 99, 235), Color.FromArgb(29, 78, 216), LinearGradientMode.Vertical))
                    using (GraphicsPath gp = CreateRoundRect(segRect, U(6f)))
                    {
                        g.FillPath(lgb, gp);
                    }
                }
                else if (hov)
                {
                    FillRoundRect(g, segRect, U(6f), IsLight ? Color.FromArgb(219, 229, 243) : Color.FromArgb(30, 255, 255, 255));
                }

                DrawCenterText(g, options[i], _fSeg, sel ? Color.White : (IsLight ? Color.FromArgb(51, 65, 85) : Color.FromArgb(175, 185, 205)), segRect);
                curX += itemWidths[i] + U(3f);
            }

            return y + h + U(8f);
        }

        private void DrawActionPill(Graphics g, string id, RectangleF r, string text, bool primaryAccent)
        {
            AddZone(id, r, id == "toggle_lang" || id == "toggle_theme");
            bool hov = (_hoverId == id);
            if (primaryAccent)
            {
                using (LinearGradientBrush lgb = new LinearGradientBrush(
                    r,
                    hov ? Color.FromArgb(56, 189, 248) : Color.FromArgb(14, 165, 233),
                    hov ? Color.FromArgb(59, 130, 246) : Color.FromArgb(37, 99, 235),
                    LinearGradientMode.Horizontal))
                using (GraphicsPath gp = CreateRoundRect(r, r.Height * 0.5f))
                {
                    g.FillPath(lgb, gp);
                }
                DrawCenterText(g, text, _fSeg, Color.White, r);
            }
            else
            {
                FillRoundRect(g, r, r.Height * 0.5f, IsLight
                    ? (hov ? Color.FromArgb(226, 235, 248) : Color.FromArgb(241, 245, 251))
                    : (hov ? Color.FromArgb(52, 58, 78) : Color.FromArgb(38, 43, 58)));
                using (Pen p = new Pen(IsLight ? Color.FromArgb(203, 213, 225) : Color.FromArgb(64, 72, 96), 1f))
                using (GraphicsPath gp = CreateRoundRect(r, r.Height * 0.5f))
                {
                    g.DrawPath(p, gp);
                }
                DrawCenterText(g, text, _fSeg, IsLight ? Color.FromArgb(30, 41, 59) : Color.White, r);
            }
        }

        private void AddZone(string id, RectangleF r, bool isFixedChrome)
        {
            if (!isFixedChrome)
            {
                // Clip interactive hit-testing to the scrollable right pane
                if (r.Bottom < U(48f) || r.Top > ClientSize.Height) return;
            }
            _zones.Add(new UiZone { Id = id, Rect = r });
        }

        // ==================== INPUT & SCROLL INTERACTION ====================
        private void OnFormMouseWheel(object sender, MouseEventArgs e)
        {
            if (_maxScrollY <= 0f) return;
            float delta = (e.Delta > 0) ? -U(44f) : U(44f);
            float next = Math.Max(0f, Math.Min(_maxScrollY, _scrollY + delta));
            if (Math.Abs(next - _scrollY) > 0.5f)
            {
                _scrollY = next;
                UpdateTodoInputPlacement();
                Invalidate();
            }
        }

        private void OnFormMouseMove(object sender, MouseEventArgs e)
        {
            string hit = null;
            for (int i = _zones.Count - 1; i >= 0; i--)
            {
                if (_zones[i].Rect.Contains(e.Location))
                {
                    hit = _zones[i].Id;
                    break;
                }
            }
            if (hit != _hoverId)
            {
                _hoverId = hit;
                Cursor = (hit != null) ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        private void OnFormMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;

            for (int i = _zones.Count - 1; i >= 0; i--)
            {
                if (_zones[i].Rect.Contains(e.Location))
                {
                    HandleZoneClick(_zones[i].Id);
                    return;
                }
            }

            // Dragging anywhere on the top titlebar (or left sidebar empty area) moves the window natively!
            if (e.Y <= S(48))
            {
                NativeMethods.ReleaseCapture();
                NativeMethods.SendMessage(Handle, NativeMethods.WM_NCLBUTTONDOWN, new IntPtr(NativeMethods.HTCAPTION), IntPtr.Zero);
            }
        }

        private void HandleZoneClick(string id)
        {
            if (id != "reset_defaults") _resetConfirmPending = false;

            if (id == "win_min")
            {
                WindowState = FormWindowState.Minimized;
                return;
            }
            if (id == "win_close")
            {
                Hide();
                return;
            }
            if (id == "toggle_theme")
            {
                _state.UiThemeMode = (_state.UiThemeMode + 1) % 3;
                SaveAndApply();
                return;
            }
            if (id.StartsWith("seg_theme_"))
            {
                int tm; if (int.TryParse(id.Substring("seg_theme_".Length), out tm) && tm >= 0 && tm <= 2) { _state.UiThemeMode = tm; SaveAndApply(); }
                return;
            }
            if (id == "toggle_lang")
            {
                _state.IsVietnamese = !_state.IsVietnamese;
                SaveAndApply();
                return;
            }
            if (id.StartsWith("tab_"))
            {
                int t;
                if (int.TryParse(id.Substring(4), out t))
                {
                    _tabIndex = t;
                    _scrollY = 0f;
                    UpdateTodoInputPlacement();
                    Invalidate();
                }
                return;
            }
            if (id == "demo_wave")
            {
                _state.MascotEnabled = true;
                SaveAndApply();
                if (_notch != null && !_notch.IsDisposed) _notch.TriggerMascotWave(3.5);
                return;
            }

            // General toggles & size presets
            if (id == "tog_startup")
            {
                _state.SetStartupRegistry(!_state.StartWithWindows);
                SaveAndApply();
            }
            else if (id.StartsWith("seg_size_"))
            {
                int idx;
                if (int.TryParse(id.Substring("seg_size_".Length), out idx))
                {
                    _state.NotchSizePreset = idx;
                    SaveAndApply();
                }
            }
            else if (id == "tog_auto_min")
            {
                _state.AutoMinimalEnabled = !_state.AutoMinimalEnabled;
                SaveAndApply();
            }
            else if (id == "tog_always_min")
            {
                _state.AlwaysMinimal = !_state.AlwaysMinimal;
                SaveAndApply();
            }
            else if (id == "tog_mascot")
            {
                _state.MascotEnabled = !_state.MascotEnabled;
                SaveAndApply();
                if (_state.MascotEnabled && _notch != null) _notch.TriggerMascotWave(3.2);
            }
            else if (id == "tog_fullscreen")
            {
                _state.HideInFullscreen = !_state.HideInFullscreen;
                SaveAndApply();
            }
            else if (id == "tog_sounds")
            {
                _state.ReminderSounds = !_state.ReminderSounds;
                SaveAndApply();
            }
            else if (id.StartsWith("mon_"))
            {
                int m;
                if (int.TryParse(id.Substring(4), out m))
                {
                    _state.MonitorIndex = m;
                    SaveAndApply();
                }
            }
            else if (id.StartsWith("seg_open_"))
            {
                _state.OpenByClick = (id == "seg_open_1");
                SaveAndApply();
            }
            else if (id.StartsWith("seg_anim_"))
            {
                _state.FastAnimation = (id == "seg_anim_1");
                SaveAndApply();
            }
            else if (id == "tog_pagelabels")
            {
                _state.ShowPageLabels = !_state.ShowPageLabels;
                SaveAndApply();
            }
            else if (id == "tog_lyrics")
            {
                _state.ShowLyrics = !_state.ShowLyrics;
                SaveAndApply();
            }
            else if (id == "tog_lyrics_split")
            {
                _state.DefaultLyricsSplit = !_state.DefaultLyricsSplit;
                SaveAndApply();
            }
            else if (id == "tog_eq")
            {
                _state.EqualizerEnabled = !_state.EqualizerEnabled;
                SaveAndApply();
            }
            else if (id == "jump_page_0")
            {
                if (_notch != null) _notch.CurrentPage = 0;
            }
            else if (id == "jump_page_1")
            {
                if (_notch != null) _notch.CurrentPage = 1;
            }
            else if (id.StartsWith("seg_focus_"))
            {
                int[] opts = new int[] { 15, 25, 45, 60 };
                int idx;
                if (int.TryParse(id.Substring("seg_focus_".Length), out idx) && idx >= 0 && idx < opts.Length)
                {
                    _state.FocusMinutes = opts[idx];
                    if (!_state.PomodoroRunning && _state.PomodoroMode == 0)
                        _state.PomodoroRemainingSec = _state.FocusMinutes * 60;
                    SaveAndApply();
                }
            }
            else if (id.StartsWith("seg_short_"))
            {
                int[] opts = new int[] { 5, 10, 15 };
                int idx;
                if (int.TryParse(id.Substring("seg_short_".Length), out idx) && idx >= 0 && idx < opts.Length)
                {
                    _state.ShortBreakMinutes = opts[idx];
                    if (!_state.PomodoroRunning && _state.PomodoroMode == 1)
                        _state.PomodoroRemainingSec = _state.ShortBreakMinutes * 60;
                    SaveAndApply();
                }
            }
            else if (id.StartsWith("seg_long_"))
            {
                int[] opts = new int[] { 15, 20, 30 };
                int idx;
                if (int.TryParse(id.Substring("seg_long_".Length), out idx) && idx >= 0 && idx < opts.Length)
                {
                    _state.LongBreakMinutes = opts[idx];
                    SaveAndApply();
                }
            }
            else if (id == "water_add_now")
            {
                _state.LogWaterDrink(_state.WaterCupSizeMl);
                SaveAndApply();
            }
            else if (id == "water_reset_day")
            {
                _state.ResetWaterToday();
                SaveAndApply();
            }
            else if (id.StartsWith("seg_wgoal_"))
            {
                int[] goals = new int[] { 1500, 2000, 2500, 3000 };
                int idx;
                if (int.TryParse(id.Substring("seg_wgoal_".Length), out idx) && idx >= 0 && idx < goals.Length)
                {
                    _state.WaterGoalMl = goals[idx];
                    SaveAndApply();
                }
            }
            else if (id.StartsWith("seg_wcup_"))
            {
                int[] cups = new int[] { 200, 250, 350, 500 };
                int idx;
                if (int.TryParse(id.Substring("seg_wcup_".Length), out idx) && idx >= 0 && idx < cups.Length)
                {
                    _state.WaterCupSizeMl = cups[idx];
                    SaveAndApply();
                }
            }
            else if (id == "todo_add_btn")
            {
                AddTypedTodo();
            }
            else if (id.StartsWith("todo_chk_"))
            {
                int idx;
                if (int.TryParse(id.Substring("todo_chk_".Length), out idx) && idx >= 0 && idx < _state.Todos.Count)
                {
                    _state.Todos[idx].Done = !_state.Todos[idx].Done;
                    SaveAndApply();
                }
            }
            else if (id.StartsWith("todo_del_"))
            {
                int idx;
                if (int.TryParse(id.Substring("todo_del_".Length), out idx) && idx >= 0 && idx < _state.Todos.Count)
                {
                    _state.Todos.RemoveAt(idx);
                    SaveAndApply();
                }
            }
            else if (id == "tog_shelf_clear")
            {
                _state.ClearShelfOnStart = !_state.ClearShelfOnStart;
                SaveAndApply();
            }
            else if (id == "shelf_clear_now")
            {
                _state.ShelfFiles.Clear();
                _state.Shelf.Clear();
                SaveAndApply();
            }
            else if (id == "cloud_update_btn")
            {
                if (_updateState == 1 || _updateState == 4) return;
                if ((_updateState == 2 || _updateState == 3) && !string.IsNullOrEmpty(_latestSetupUrl))
                {
                    _updateState = 4;
                    _updateInfoVi = "Đang tải bộ cài " + _latestTag + " và tiến hành cài đè tự động...";
                    _updateInfoEn = "Downloading " + _latestTag + " and performing seamless overwrite...";
                    Invalidate();
                    UpdaterService.DownloadAndOverwriteAsync(_latestSetupUrl, (ok, err) =>
                    {
                        try
                        {
                            BeginInvoke((MethodInvoker)delegate
                            {
                                if (ok) { Application.Exit(); }
                                else { _updateState = 5; _updateInfoVi = "Lỗi tải: " + err; _updateInfoEn = "Error: " + err; Invalidate(); }
                            });
                        }
                        catch { }
                    });
                    return;
                }
                _updateState = 1;
                _updateInfoVi = "Đang kết nối GitHub Releases kiểm tra phiên bản mới nhất...";
                _updateInfoEn = "Checking GitHub Releases for the latest build...";
                Invalidate();
                UpdaterService.CheckLatestReleaseAsync((ok, tag, url, isNewer, err) =>
                {
                    try
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            if (!ok) { _updateState = 5; _updateInfoVi = "Không thể kiểm tra: " + err; _updateInfoEn = "Check failed: " + err; }
                            else if (isNewer) { _updateState = 2; _latestTag = tag; _latestSetupUrl = url; _updateInfoVi = "Phát hiện bản mới " + tag + "! Nhấn nút bên phải để tự tải & cài đè ngay."; _updateInfoEn = "New release " + tag + " available! Click to download & overwrite."; }
                            else { _updateState = 3; _latestTag = tag; _latestSetupUrl = url; _updateInfoVi = "Bạn đang ở bản mới nhất (" + tag + "). Vẫn có thể bấm để cài đè lại."; _updateInfoEn = "Already on latest (" + tag + "). Click to reinstall."; }
                            Invalidate();
                        });
                    }
                    catch { }
                });
            }
            else if (id == "open_datadir")
            {
                try { Process.Start("explorer.exe", _state.DataDir); } catch { }
            }
            else if (id == "reset_defaults")
            {
                if (!_resetConfirmPending)
                {
                    _resetConfirmPending = true;
                    Invalidate();
                }
                else
                {
                    _resetConfirmPending = false;
                    _state.ResetDefaults();
                    SaveAndApply();
                }
            }
        }

        // ==================== DRAWING HELPERS ====================
        private void DrawLeftText(Graphics g, string text, Font font, Color col, float x, float cy)
        {
            if (string.IsNullOrEmpty(text)) return;
            SizeF sz = g.MeasureString(text, font);
            using (SolidBrush b = new SolidBrush(col))
            {
                g.DrawString(text, font, b, x, cy - sz.Height * 0.5f);
            }
        }

        private void DrawCenterText(Graphics g, string text, Font font, Color col, RectangleF rect)
        {
            if (string.IsNullOrEmpty(text)) return;
            SizeF sz = g.MeasureString(text, font);
            using (SolidBrush b = new SolidBrush(col))
            {
                g.DrawString(text, font, b, rect.X + (rect.Width - sz.Width) * 0.5f, rect.Y + (rect.Height - sz.Height) * 0.5f);
            }
        }

        private void FillRoundRect(Graphics g, RectangleF r, float rad, Color col)
        {
            if (r.Width <= 0f || r.Height <= 0f) return;
            using (GraphicsPath gp = CreateRoundRect(r, rad))
            using (SolidBrush b = new SolidBrush(col))
            {
                g.FillPath(b, gp);
            }
        }

        private GraphicsPath CreateRoundRect(RectangleF r, float rad)
        {
            GraphicsPath p = new GraphicsPath();
            float d = Math.Min(rad * 2f, Math.Min(r.Width, r.Height));
            if (d <= 1f)
            {
                p.AddRectangle(r);
                return p;
            }
            p.AddArc(r.X, r.Y, d, d, 180f, 90f);
            p.AddArc(r.Right - d, r.Y, d, d, 270f, 90f);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0f, 90f);
            p.AddArc(r.X, r.Bottom - d, d, d, 90f, 90f);
            p.CloseFigure();
            return p;
        }
    }
}
