using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Kumo Official Setup")]
[assembly: AssemblyDescription("Kumo v1.0.0 — Official Installer & Uninstaller for Windows")]
[assembly: AssemblyCompany("Kumo Desktop")]
[assembly: AssemblyProduct("Kumo")]
[assembly: AssemblyCopyright("Copyright © 2026 Kumo Desktop")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace KumoSetup
{
    // ==================== WIN32 & SHELL LINK COM INTEROP ====================
    internal static class SetupNative
    {
        public const int WM_NCLBUTTONDOWN = 0x00A1;
        public const int HTCAPTION = 0x0002;

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("shcore.dll")]
        public static extern int SetProcessDpiAwareness(int value);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        public static void EnableHighDpi()
        {
            try { SetProcessDpiAwareness(2); }
            catch { try { SetProcessDPIAware(); } catch { } }
        }

        public static void ApplyModernWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            try { int dark = 1; DwmSetWindowAttribute(hwnd, 20, ref dark, 4); } catch { }
            try { int round = 2; DwmSetWindowAttribute(hwnd, 33, ref round, 4); } catch { }
        }

        public static void NotifyShellShortcutsUpdated(string folderPath)
        {
            try
            {
                SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED
                if (!string.IsNullOrEmpty(folderPath))
                {
                    IntPtr ptr = Marshal.StringToHGlobalUni(folderPath);
                    try
                    {
                        SHChangeNotify(0x00001000, 0x0005, ptr, IntPtr.Zero); // SHCNE_UPDATEDIR | SHCNF_PATHW
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(ptr);
                    }
                }
            }
            catch { }
        }

        public static void CreateShortcut(string lnkPath, string targetExe, string arguments, string workingDir, string description, string iconPath)
        {
            string parent = Path.GetDirectoryName(lnkPath);
            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
            {
                Directory.CreateDirectory(parent);
            }

            IShellLinkW link = (IShellLinkW)new ShellLink();
            link.SetPath(targetExe);
            link.SetArguments(arguments ?? "");
            link.SetWorkingDirectory(workingDir ?? Path.GetDirectoryName(targetExe));
            link.SetDescription(description ?? "Kumo");
            if (!string.IsNullOrEmpty(iconPath))
            {
                link.SetIconLocation(iconPath, 0);
            }

            IPersistFile file = (IPersistFile)link;
            file.Save(lnkPath, false);
        }

        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLink { }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, int fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
            void Resolve(IntPtr hwnd, int fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("0000010b-0000-0000-C000-000000000046")]
        private interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            [PreserveSig] int IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
        }
    }

    // ==================== OFFICIAL INSTALLER / UNINSTALLER ENGINE ====================
    internal static class InstallerCore
    {
        public const string UninstallRegKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Kumo";
        public const string LegacyUninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\PulseNotch";
        public const string RunRegKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static string GetDefaultInstallDir()
        {
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(Path.Combine(localApp, "Programs"), "Kumo");
        }

        public static string GetExistingInstallDir()
        {
            try
            {
                using (RegistryKey rk = Registry.CurrentUser.OpenSubKey(UninstallRegKey, false))
                {
                    if (rk != null)
                    {
                        object loc = rk.GetValue("InstallLocation");
                        if (loc != null)
                        {
                            string s = loc.ToString();
                            if (!string.IsNullOrEmpty(s) && Directory.Exists(s)) return s;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        public static void StopRunningProcesses()
        {
            string[] names = new string[] { "Kumo", "PulseNotch" };
            for (int n = 0; n < names.Length; n++)
            {
                try
                {
                    Process[] procs = Process.GetProcessesByName(names[n]);
                    foreach (Process p in procs)
                    {
                        try
                        {
                            p.Kill();
                            p.WaitForExit(1500);
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }

        private static void CleanupLegacyBetaIfAny()
        {
            try
            {
                using (RegistryKey runKey = Registry.CurrentUser.OpenSubKey(RunRegKey, true))
                {
                    if (runKey != null) runKey.DeleteValue("PulseNotch", false);
                }
                Registry.CurrentUser.DeleteSubKeyTree(LegacyUninstallKey, false);

                string startPrograms = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                string oldTopLnk = Path.Combine(startPrograms, "PulseNotch.lnk");
                string oldFolder = Path.Combine(startPrograms, "PulseNotch");
                if (File.Exists(oldTopLnk)) File.Delete(oldTopLnk);
                if (Directory.Exists(oldFolder)) Directory.Delete(oldFolder, true);

                string oldDesktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "PulseNotch.lnk");
                if (File.Exists(oldDesktop)) File.Delete(oldDesktop);

                string oldInstall = Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"), "PulseNotch");
                if (Directory.Exists(oldInstall)) Directory.Delete(oldInstall, true);
            }
            catch { }
        }

        public static void PerformInstall(
            string installDir,
            bool createStartMenu,
            bool createDesktop,
            bool startWithWindows,
            Action<float, int, string, string> onProgress)
        {
            if (onProgress != null) onProgress(0.16f, 0, "Đang kiểm tra & đóng tiến trình cũ...", "Checking & closing previous processes...");
            StopRunningProcesses();
            CleanupLegacyBetaIfAny();
            Thread.Sleep(160);

            if (onProgress != null) onProgress(0.45f, 1, "Đang giải nén tệp ứng dụng Kumo.exe (x64)...", "Extracting official Kumo.exe (x64) binary...");
            Directory.CreateDirectory(installDir);

            string targetExe = Path.Combine(installDir, "Kumo.exe");
            string uninstExe = Path.Combine(installDir, "Uninstall.exe");

            using (Stream res = Assembly.GetExecutingAssembly().GetManifestResourceStream("Kumo.Payload.gz"))
            {
                if (res == null) throw new InvalidOperationException("Missing embedded Kumo.Payload.gz resource.");
                using (GZipStream gz = new GZipStream(res, CompressionMode.Decompress))
                using (FileStream fs = new FileStream(targetExe, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    gz.CopyTo(fs);
                }
            }

            string selfExe = Application.ExecutablePath;
            if (!string.Equals(Path.GetFullPath(selfExe), Path.GetFullPath(uninstExe), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(selfExe, uninstExe, true);
            }

            Thread.Sleep(160);
            if (onProgress != null) onProgress(0.74f, 2, "Đang tạo lối tắt Start Menu, Search & Desktop...", "Creating Start Menu, Search & Desktop shortcuts...");

            string startPrograms = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            string startFolder = Path.Combine(startPrograms, "Kumo");

            if (createStartMenu)
            {
                Directory.CreateDirectory(startFolder);
                SetupNative.CreateShortcut(
                    Path.Combine(startPrograms, "Kumo.lnk"),
                    targetExe, "", installDir,
                    "Kumo — Official Living Notch for Windows", targetExe);
                SetupNative.CreateShortcut(
                    Path.Combine(startFolder, "Kumo.lnk"),
                    targetExe, "", installDir,
                    "Kumo — Official Living Notch for Windows", targetExe);
                SetupNative.CreateShortcut(
                    Path.Combine(startFolder, "Kumo Settings.lnk"),
                    targetExe, "--settings", installDir,
                    "Kumo — Bảng Quản Lý Cài Đặt (Settings)", targetExe);
                SetupNative.CreateShortcut(
                    Path.Combine(startFolder, "Uninstall Kumo.lnk"),
                    uninstExe, "--uninstall", installDir,
                    "Gỡ cài đặt Kumo (Uninstall)", uninstExe);

                SetupNative.NotifyShellShortcutsUpdated(startPrograms);
                SetupNative.NotifyShellShortcutsUpdated(startFolder);
            }

            if (createDesktop)
            {
                string desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                SetupNative.CreateShortcut(
                    Path.Combine(desktopDir, "Kumo.lnk"),
                    targetExe, "", installDir,
                    "Kumo — Official Living Notch for Windows", targetExe);
                SetupNative.NotifyShellShortcutsUpdated(desktopDir);
            }

            Thread.Sleep(150);
            if (onProgress != null) onProgress(0.92f, 3, "Đang đăng ký Kumo vào Windows Apps & Features...", "Registering Kumo in Windows Apps & Features...");

            using (RegistryKey rk = Registry.CurrentUser.CreateSubKey(UninstallRegKey))
            {
                if (rk != null)
                {
                    rk.SetValue("DisplayName", "Kumo");
                    rk.SetValue("DisplayVersion", "1.0.0");
                    rk.SetValue("Publisher", "Kumo Desktop");
                    rk.SetValue("DisplayIcon", targetExe);
                    rk.SetValue("InstallLocation", installDir);
                    rk.SetValue("UninstallString", "\"" + uninstExe + "\" --uninstall");
                    rk.SetValue("QuietUninstallString", "\"" + uninstExe + "\" --uninstall --silent");
                    rk.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                    rk.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    rk.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    rk.SetValue("EstimatedSize", 340, RegistryValueKind.DWord);
                }
            }

            using (RegistryKey runKey = Registry.CurrentUser.OpenSubKey(RunRegKey, true))
            {
                if (runKey != null)
                {
                    if (startWithWindows)
                        runKey.SetValue("Kumo", "\"" + targetExe + "\"");
                    else
                        runKey.DeleteValue("Kumo", false);
                }
            }

            Thread.Sleep(140);
            if (onProgress != null) onProgress(1.0f, 4, "Cài đặt chính thức Kumo v1.0.0 hoàn tất!", "Official Kumo v1.0.0 installation complete!");
        }

        public static void PerformUninstall(string installDir, bool removeUserData)
        {
            StopRunningProcesses();
            Thread.Sleep(150);

            try
            {
                using (RegistryKey runKey = Registry.CurrentUser.OpenSubKey(RunRegKey, true))
                {
                    if (runKey != null) runKey.DeleteValue("Kumo", false);
                }
            }
            catch { }

            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(UninstallRegKey, false);
            }
            catch { }

            try
            {
                string startPrograms = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                string topLnk = Path.Combine(startPrograms, "Kumo.lnk");
                string startFolder = Path.Combine(startPrograms, "Kumo");
                if (File.Exists(topLnk)) File.Delete(topLnk);
                if (Directory.Exists(startFolder)) Directory.Delete(startFolder, true);
                SetupNative.NotifyShellShortcutsUpdated(startPrograms);
            }
            catch { }

            try
            {
                string desktopLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Kumo.lnk");
                if (File.Exists(desktopLnk)) File.Delete(desktopLnk);
            }
            catch { }

            if (removeUserData)
            {
                try
                {
                    string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kumo");
                    if (Directory.Exists(dataDir)) Directory.Delete(dataDir, true);
                }
                catch { }
            }

            if (!string.IsNullOrEmpty(installDir) && Directory.Exists(installDir))
            {
                string folderName = Path.GetFileName(installDir.TrimEnd('\\', '/'));
                if (string.Equals(folderName, "Kumo", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        string targetExe = Path.Combine(installDir, "Kumo.exe");
                        if (File.Exists(targetExe)) File.Delete(targetExe);
                    }
                    catch { }

                    try
                    {
                        ProcessStartInfo psi = new ProcessStartInfo();
                        psi.FileName = "cmd.exe";
                        psi.Arguments = "/C ping 127.0.0.1 -n 2 > nul & rmdir /s /q \"" + installDir + "\"";
                        psi.WindowStyle = ProcessWindowStyle.Hidden;
                        psi.CreateNoWindow = true;
                        psi.UseShellExecute = false;
                        Process.Start(psi);
                    }
                    catch { }
                }
            }
        }
    }

    // ==================== KUMO OFFICIAL 4-STEP SETUP WIZARD FORM ====================
    internal class SetupWizardForm : Form
    {
        private bool _isVi = true;
        private float _s = 1.0f;

        // Wizard Step:
        // 0 = Official Welcome & Highlights (Express 1-Click Install or Customize)
        // 1 = Customize Folder & Windows Integration
        // 2 = Installing Progress + Live 4-Row Checklist
        // 3 = Setup Complete
        // 10 = Uninstall Confirmation
        // 11 = Uninstalled Complete
        private int _step = 0;

        private string _installDir;
        private bool _optStartMenu = true;
        private bool _optStartup = true;
        private bool _optDesktop = true;
        private bool _optLaunchNow = true;
        private bool _optOpenSettings = true;
        private bool _optRemoveData = false;

        private float _progress = 0f;
        private int _installStageIdx = 0;
        private string _statusVi = "";
        private string _statusEn = "";

        private struct HitRect
        {
            public string Id;
            public RectangleF Rect;
        }
        private readonly List<HitRect> _hits = new List<HitRect>();
        private string _hoverId = null;

        private Font _fBrand;
        private Font _fBadge;
        private Font _fSub;
        private Font _fStep;
        private Font _fSec;
        private Font _fBold;
        private Font _fDesc;
        private Font _fSmall;
        private Font _fBtn;

        public SetupWizardForm(bool uninstallMode)
        {
            string existing = InstallerCore.GetExistingInstallDir();
            _installDir = !string.IsNullOrEmpty(existing) ? existing : InstallerCore.GetDefaultInstallDir();
            _step = uninstallMode ? 10 : 0;

            Text = uninstallMode ? "Uninstall Kumo" : "Kumo Setup — Official v1.0.0";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(16, 19, 28);
            DoubleBuffered = true;

            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            InitScaleAndFonts();
            ClientSize = new Size(S(600), S(500));

            MouseMove += OnWizMouseMove;
            MouseDown += OnWizMouseDown;
            MouseLeave += (s, e) =>
            {
                if (_hoverId != null) { _hoverId = null; Cursor = Cursors.Default; Invalidate(); }
            };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SetupNative.ApplyModernWindow(Handle);
        }

        private void InitScaleAndFonts()
        {
            float dpi = 96f;
            try
            {
                using (Graphics g = CreateGraphics()) { if (g.DpiX > 0) dpi = g.DpiX; }
            }
            catch { }
            _s = dpi / 96.0f;

            _fBrand = MakeFont(23.5f, FontStyle.Bold);
            _fBadge = MakeFont(9.8f, FontStyle.Bold);
            _fSub   = MakeFont(11.6f, FontStyle.Regular);
            _fStep  = MakeFont(10.8f, FontStyle.Bold);
            _fSec   = MakeFont(10.5f, FontStyle.Bold);
            _fBold  = MakeFont(12.6f, FontStyle.Bold);
            _fDesc  = MakeFont(11.0f, FontStyle.Regular);
            _fSmall = MakeFont(10.2f, FontStyle.Regular);
            _fBtn   = MakeFont(12.6f, FontStyle.Bold);
        }

        private Font MakeFont(float dp, FontStyle st)
        {
            float px = Math.Max(9f, dp * _s);
            try { return new Font("Segoe UI", px, st, GraphicsUnit.Pixel); }
            catch { return new Font(FontFamily.GenericSansSerif, px, st, GraphicsUnit.Pixel); }
        }

        private float U(float dp) { return dp * _s; }
        private int S(float dp) { return (int)Math.Round(dp * _s); }
        private string T(string vi, string en) { return _isVi ? vi : en; }

        public void SavePreview(string path, int previewStep)
        {
            int old = _step;
            _step = previewStep;
            int w = S(600), h = S(500);
            using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                RenderWizard(g, w, h);
                bmp.Save(path, ImageFormat.Png);
            }
            _step = old;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            RenderWizard(e.Graphics, ClientSize.Width, ClientSize.Height);
        }

        private void RenderWizard(Graphics g, int winW, int winH)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            _hits.Clear();

            g.Clear(Color.FromArgb(16, 19, 28));

            // ==================== 1. OFFICIAL HERO BANNER (116 dp) ====================
            RectangleF hero = new RectangleF(0, 0, winW, U(116f));
            using (LinearGradientBrush bg = new LinearGradientBrush(
                hero,
                Color.FromArgb(12, 16, 26),
                Color.FromArgb(24, 32, 52),
                LinearGradientMode.Vertical))
            {
                g.FillRectangle(bg, hero);
            }

            // Soft radial sky glow behind Kumo Notch
            using (GraphicsPath gpGlow = new GraphicsPath())
            {
                gpGlow.AddEllipse(winW * 0.5f - U(180f), -U(45f), U(360f), U(135f));
                using (PathGradientBrush pgb = new PathGradientBrush(gpGlow))
                {
                    pgb.CenterColor = Color.FromArgb(48, 56, 189, 248);
                    pgb.SurroundColors = new Color[] { Color.FromArgb(0, 56, 189, 248) };
                    g.FillPath(pgb, gpGlow);
                }
            }

            // Kumo Living Notch Silhouette dropping from top-center!
            float nw = U(168f), nh = U(33f);
            float nx = (winW - nw) * 0.5f;
            RectangleF notchRect = new RectangleF(nx, -U(6f), nw, nh + U(6f));
            FillRoundRect(g, notchRect, U(14f), Color.Black);
            using (GraphicsPath np = CreateRoundRect(notchRect, U(14f)))
            {
                GraphicsState st = g.Save();
                g.SetClip(np);
                using (LinearGradientBrush glow = new LinearGradientBrush(
                    new RectangleF(nx, U(11f), nw, U(23f)),
                    Color.FromArgb(230, 14, 185, 255),
                    Color.FromArgb(230, 108, 76, 238),
                    LinearGradientMode.Horizontal))
                {
                    g.FillRectangle(glow, nx, U(13f), nw, U(21f));
                }

                float mcx = winW * 0.5f;
                float mcy = U(15.5f);
                using (Pen ep = new Pen(Color.White, U(2.4f)))
                {
                    ep.StartCap = LineCap.Round; ep.EndCap = LineCap.Round;
                    g.DrawBezier(ep, new PointF(mcx - U(20f), mcy + U(2f)), new PointF(mcx - U(16.5f), mcy - U(4f)), new PointF(mcx - U(13f), mcy - U(4f)), new PointF(mcx - U(9.5f), mcy + U(2f)));
                    g.DrawBezier(ep, new PointF(mcx + U(1.5f), mcy + U(2f)), new PointF(mcx + U(5f), mcy - U(4f)), new PointF(mcx + U(8.5f), mcy - U(4f)), new PointF(mcx + U(12f), mcy + U(2f)));
                }
                float hx = mcx + U(26f), hy = mcy + U(3.5f);
                using (SolidBrush wb = new SolidBrush(Color.White))
                using (Pen fp = new Pen(Color.White, U(1.65f)))
                {
                    fp.StartCap = LineCap.Round; fp.EndCap = LineCap.Round;
                    g.FillEllipse(wb, hx - U(3.2f), hy - U(2.1f), U(6.4f), U(4.8f));
                    g.DrawLine(fp, hx - U(1.8f), hy + U(0.3f), hx - U(4.2f), hy - U(0.4f));
                    g.DrawLine(fp, hx - U(1.3f), hy - U(1.1f), hx - U(2.0f), hy - U(4.3f));
                    g.DrawLine(fp, hx + U(0.3f), hy - U(1.3f), hx + U(0.7f), hy - U(4.6f));
                    g.DrawLine(fp, hx + U(1.8f), hy - U(0.9f), hx + U(3.2f), hy - U(3.5f));
                }
                g.Restore(st);
            }

            // Top-right window controls: Language, Minimize, Close
            RectangleF langBtn = new RectangleF(winW - U(126f), U(10f), U(50f), U(24f));
            DrawSecondaryBtn(g, "lang", langBtn, _isVi ? "VI" : "EN");

            if (_step != 2)
            {
                RectangleF minBtn = new RectangleF(winW - U(68f), U(10f), U(28f), U(24f));
                _hits.Add(new HitRect { Id = "minimize", Rect = minBtn });
                if (_hoverId == "minimize") FillRoundRect(g, minBtn, U(6f), Color.FromArgb(35, 255, 255, 255));
                using (Pen mp = new Pen(Color.FromArgb(215, 255, 255, 255), U(1.6f)))
                {
                    float mcx = minBtn.X + minBtn.Width * 0.5f, mcy = minBtn.Y + minBtn.Height * 0.5f;
                    g.DrawLine(mp, mcx - U(4.5f), mcy, mcx + U(4.5f), mcy);
                }

                RectangleF clsBtn = new RectangleF(winW - U(36f), U(10f), U(28f), U(24f));
                _hits.Add(new HitRect { Id = "close", Rect = clsBtn });
                if (_hoverId == "close") FillRoundRect(g, clsBtn, U(6f), Color.FromArgb(220, 225, 48, 62));
                using (Pen cp = new Pen(Color.White, U(1.6f)))
                {
                    float ccx = clsBtn.X + clsBtn.Width * 0.5f, ccy = clsBtn.Y + clsBtn.Height * 0.5f;
                    g.DrawLine(cp, ccx - U(4f), ccy - U(4f), ccx + U(4f), ccy + U(4f));
                    g.DrawLine(cp, ccx + U(4f), ccy - U(4f), ccx - U(4f), ccy + U(4f));
                }
            }

            // Center Brand Title + "OFFICIAL v1.0.0" Pill Badge
            if (_step >= 10)
            {
                DrawCenterText(g, T("Gỡ cài đặt Kumo", "Uninstall Kumo"), _fBrand, Color.White, new RectangleF(U(20f), U(44f), winW - U(40f), U(34f)));
                DrawCenterText(g, T("Gỡ ứng dụng và lối tắt Kumo khỏi hệ thống Windows của bạn.", "Remove Kumo application files and shortcuts from Windows."), _fSub, Color.FromArgb(156, 168, 194), new RectangleF(U(20f), U(80f), winW - U(40f), U(24f)));
            }
            else
            {
                string brandTxt = "Kumo";
                SizeF bSz = g.MeasureString(brandTxt, _fBrand);
                float badgeW = U(118f);
                float totalHdrW = bSz.Width + U(8f) + badgeW;
                float startHdrX = (winW - totalHdrW) * 0.5f;

                DrawLeftText(g, brandTxt, _fBrand, Color.White, startHdrX, U(60f));

                RectangleF offBadge = new RectangleF(startHdrX + bSz.Width + U(6f), U(50f), badgeW, U(21f));
                using (LinearGradientBrush bb = new LinearGradientBrush(offBadge, Color.FromArgb(48, 14, 165, 233), Color.FromArgb(48, 99, 102, 241), LinearGradientMode.Horizontal))
                using (GraphicsPath bp = CreateRoundRect(offBadge, U(10.5f)))
                using (Pen bPen = new Pen(Color.FromArgb(115, 56, 189, 248), 1f))
                {
                    g.FillPath(bb, bp);
                    g.DrawPath(bPen, bp);
                }
                DrawCenterText(g, "OFFICIAL v1.0.0", _fBadge, Color.FromArgb(125, 211, 252), offBadge);

                DrawCenterText(g,
                    T("Trình cài đặt chính thức Tai thỏ sống động Kumo cho Windows 10 & 11", "Official Living Notch Installer for Windows 10 & 11 (64-bit)"),
                    _fSub, Color.FromArgb(158, 170, 196), new RectangleF(U(20f), U(82f), winW - U(40f), U(24f)));
            }

            float padX = U(28f);
            float cw = winW - padX * 2f;

            // ==================== 2. 4-STEP OFFICIAL STEPPER BAR (Steps 0..3) ====================
            float y = U(128f);
            if (_step < 10)
            {
                DrawStepperStrip(g, padX, y, cw);
                y += U(44f);
            }
            else
            {
                y += U(14f);
            }

            // ==================== 3. STEP CONTENT ====================
            if (_step == 0)
            {
                // STEP 0: OFFICIAL WELCOME & 2x2 FEATURE HIGHLIGHTS
                float cardW = (cw - U(12f)) * 0.5f;
                float cardH = U(82f);

                DrawFeatureCard(g, new RectangleF(padX, y, cardW, cardH), 0,
                    Color.FromArgb(56, 189, 248),
                    T("Nhạc & Ảnh bìa thật", "Real Media & Album Art"),
                    T("Đồng bộ trực tiếp YouTube, Spotify kèm lời bài hát LRCLIB & màu nền.", "Live YouTube/Spotify cover art, ambient glow & synced LRCLIB lyrics."));

                DrawFeatureCard(g, new RectangleF(padX + cardW + U(12f), y, cardW, cardH), 1,
                    Color.FromArgb(167, 139, 250),
                    T("Linh vật Kumo dễ thương", "Cute Kumo Mascot"),
                    T("Tự nổi nhẹ khi rảnh, ngủ gật và vẫy tay chào mừng khi bạn quay lại.", "Gently peeks out when idle, naps peacefully & waves hello on return."));

                y += cardH + U(11f);

                DrawFeatureCard(g, new RectangleF(padX, y, cardW, cardH), 2,
                    Color.FromArgb(52, 211, 153),
                    T("Đa nhiệm 6 trong 1", "6-in-1 Daily Workspace"),
                    T("Pomodoro, Nhắc uống nước, Kệ kéo-thả tệp, Việc cần làm & CPU/GPU.", "Pomodoro, Water log, File Shelf, To-do list & live Hardware Monitor."));

                DrawFeatureCard(g, new RectangleF(padX + cardW + U(12f), y, cardW, cardH), 3,
                    Color.FromArgb(251, 191, 36),
                    T("Siêu nhẹ & Tự thu mảnh", "Featherlight & Auto-Minimal"),
                    T("Tự thu nhỏ 44×4 dp né nút đóng tab trình duyệt · ~0% CPU khi nghỉ.", "Auto-shrinks to avoid browser tab close buttons · ~0% idle CPU."));

                y += cardH + U(14f);

                // Official Spec Info Bar
                RectangleF specBar = new RectangleF(padX, y, cw, U(38f));
                FillRoundRect(g, specBar, U(9f), Color.FromArgb(22, 26, 38));
                using (Pen sp = new Pen(Color.FromArgb(38, 45, 64), 1f))
                using (GraphicsPath gp = CreateRoundRect(specBar, U(9f)))
                {
                    g.DrawPath(sp, gp);
                }
                DrawLeftText(g,
                    T("Đích cài đặt: ", "Destination: ") + TruncateMiddle(_installDir, 44),
                    _fSmall, Color.FromArgb(168, 178, 202), specBar.X + U(14f), specBar.Y + specBar.Height * 0.5f);
                DrawSecondaryBtn(g, "go_custom", new RectangleF(specBar.Right - U(104f), specBar.Y + U(6f), U(94f), U(26f)),
                    T("Tùy chỉnh...", "Customize..."));

                bool hasExisting = (InstallerCore.GetExistingInstallDir() != null);
                if (hasExisting)
                {
                    DrawSecondaryBtn(g, "switch_uninst", new RectangleF(padX, winH - U(54f), U(138f), U(38f)),
                        T("Gỡ bản hiện tại", "Uninstall Existing"));
                }
                else
                {
                    DrawLeftText(g, T("Bản phát hành chính thức · Không cần quyền Admin", "Official Release · No Admin rights required"),
                        _fSmall, Color.FromArgb(118, 130, 155), padX, winH - U(35f));
                }

                RectangleF instBtn = new RectangleF(winW - padX - U(224f), winH - U(54f), U(224f), U(38f));
                DrawPrimaryBtn(g, "start_install", instBtn,
                    hasExisting ? T("Cập nhật Kumo v1.0", "Update Kumo v1.0") : T("Cài đặt chính thức (Install)", "Install Kumo Now"));
            }
            else if (_step == 1)
            {
                // STEP 1: CUSTOMIZE LOCATION & WINDOWS SHORTCUTS
                DrawLeftText(g, T("THƯ MỤC CÀI ĐẶT (INSTALL LOCATION)", "INSTALL LOCATION"), _fSec, Color.FromArgb(96, 165, 250), padX, y + U(6f));
                y += U(20f);

                RectangleF dirCard = new RectangleF(padX, y, cw, U(44f));
                DrawCard(g, dirCard);
                DrawLeftText(g, TruncateMiddle(_installDir, 52), _fDesc, Color.FromArgb(225, 232, 245), padX + U(14f), y + U(22f));
                DrawSecondaryBtn(g, "browse", new RectangleF(padX + cw - U(96f), y + U(8f), U(86f), U(28f)), T("Đổi thư mục...", "Browse..."));
                y += U(56f);

                DrawLeftText(g, T("TÙY CHỌN TÍCH HỢP WINDOWS", "WINDOWS INTEGRATION"), _fSec, Color.FromArgb(96, 165, 250), padX, y + U(6f));
                y += U(20f);

                y = DrawCheckRow(g, padX, y, cw, "chk_startmenu",
                    T("Thêm Kumo vào Start Menu & Windows Search (Khuyên dùng)", "Add Kumo to Windows Start Menu & Search (Recommended)"),
                    _optStartMenu);
                y = DrawCheckRow(g, padX, y, cw, "chk_startup",
                    T("Tự động khởi chạy Kumo khi bật máy tính", "Launch Kumo automatically when Windows starts"),
                    _optStartup);
                y = DrawCheckRow(g, padX, y, cw, "chk_desktop",
                    T("Tạo biểu tượng Kumo ngoài màn hình nền (Desktop)", "Create a Kumo shortcut on Desktop"),
                    _optDesktop);

                DrawSecondaryBtn(g, "go_welcome", new RectangleF(padX, winH - U(54f), U(118f), U(38f)),
                    T("← Quay lại", "← Back"));
                DrawPrimaryBtn(g, "start_install", new RectangleF(winW - padX - U(216f), winH - U(54f), U(216f), U(38f)),
                    T("Bắt đầu cài đặt →", "Start Installation →"));
            }
            else if (_step == 2)
            {
                // STEP 2: INSTALLING PROGRESS + LIVE 4-ITEM CHECKLIST
                RectangleF pCard = new RectangleF(padX, y + U(4f), cw, U(238f));
                DrawCard(g, pCard);

                string pctStr = string.Format("{0}%", (int)Math.Round(_progress * 100f));
                DrawLeftText(g, T(_statusVi, _statusEn), _fBold, Color.White, pCard.X + U(20f), pCard.Y + U(26f));
                DrawRightText(g, pctStr, _fBold, Color.FromArgb(56, 189, 248), pCard.Right - U(20f), pCard.Y + U(26f));

                RectangleF barBg = new RectangleF(pCard.X + U(20f), pCard.Y + U(48f), pCard.Width - U(40f), U(11f));
                FillRoundRect(g, barBg, U(5.5f), Color.FromArgb(35, 255, 255, 255));
                if (_progress > 0.01f)
                {
                    RectangleF barFg = new RectangleF(barBg.X, barBg.Y, Math.Max(U(11f), barBg.Width * _progress), barBg.Height);
                    using (LinearGradientBrush lgb = new LinearGradientBrush(barBg, Color.FromArgb(14, 165, 233), Color.FromArgb(99, 102, 241), LinearGradientMode.Horizontal))
                    using (GraphicsPath gp = CreateRoundRect(barFg, U(5.5f)))
                    {
                        g.FillPath(lgb, gp);
                    }
                }

                string[] stVi = new string[]
                {
                    "Kiểm tra môi trường & đóng tiến trình cũ",
                    "Giải nén tệp ứng dụng Kumo.exe (64-bit Official)",
                    "Tạo lối tắt Start Menu, Windows Search & Desktop",
                    "Đăng ký Kumo vào Windows Apps & Features"
                };
                string[] stEn = new string[]
                {
                    "Prepare environment & close previous processes",
                    "Extract official Kumo.exe (64-bit) binary",
                    "Create Start Menu, Windows Search & Desktop shortcuts",
                    "Register Kumo in Windows Apps & Features"
                };

                float cy = pCard.Y + U(84f);
                for (int i = 0; i < 4; i++)
                {
                    bool done = (_installStageIdx > i);
                    bool active = (_installStageIdx == i);
                    float icx = pCard.X + U(30f);
                    if (done)
                    {
                        using (SolidBrush gb = new SolidBrush(Color.FromArgb(16, 185, 129)))
                        {
                            g.FillEllipse(gb, icx - U(8f), cy - U(8f), U(16f), U(16f));
                        }
                        using (Pen cp = new Pen(Color.White, U(1.7f)))
                        {
                            cp.StartCap = LineCap.Round; cp.EndCap = LineCap.Round;
                            g.DrawLines(cp, new PointF[] { new PointF(icx - U(3.5f), cy), new PointF(icx - U(1f), cy + U(2.8f)), new PointF(icx + U(4f), cy - U(2.8f)) });
                        }
                    }
                    else if (active)
                    {
                        using (SolidBrush ab = new SolidBrush(Color.FromArgb(56, 189, 248)))
                        {
                            g.FillEllipse(ab, icx - U(6f), cy - U(6f), U(12f), U(12f));
                        }
                    }
                    else
                    {
                        using (Pen dp = new Pen(Color.FromArgb(75, 86, 112), 1.4f))
                        {
                            g.DrawEllipse(dp, icx - U(6f), cy - U(6f), U(12f), U(12f));
                        }
                    }

                    Color tc = done ? Color.FromArgb(225, 235, 248) : (active ? Color.White : Color.FromArgb(115, 126, 150));
                    DrawLeftText(g, _isVi ? stVi[i] : stEn[i], active ? _fBold : _fDesc, tc, pCard.X + U(50f), cy);
                    cy += U(35f);
                }
            }
            else if (_step == 3)
            {
                // STEP 3: OFFICIAL INSTALL COMPLETE
                RectangleF okCard = new RectangleF(padX, y + U(4f), cw, U(88f));
                FillRoundRect(g, okCard, U(11f), Color.FromArgb(20, 38, 36));
                using (Pen okPen = new Pen(Color.FromArgb(95, 16, 185, 129), 1f))
                using (GraphicsPath gp = CreateRoundRect(okCard, U(11f)))
                {
                    g.DrawPath(okPen, gp);
                }

                DrawCenterText(g,
                    T("✓ Đã cài đặt Kumo v1.0.0 Chính Thức thành công!", "✓ Kumo v1.0.0 Official has been installed!"),
                    _fBold, Color.FromArgb(52, 211, 153), new RectangleF(okCard.X + U(14f), okCard.Y + U(16f), okCard.Width - U(28f), U(26f)));
                DrawCenterText(g,
                    T("Bạn có thể tìm và mở 'Kumo' bất kỳ lúc nào trong Start Menu hoặc thanh tìm kiếm Windows.", "You can launch 'Kumo' anytime from the Windows Start Menu or Search."),
                    _fDesc, Color.FromArgb(182, 196, 216), new RectangleF(okCard.X + U(14f), okCard.Y + U(46f), okCard.Width - U(28f), U(26f)));

                y += U(108f);
                y = DrawCheckRow(g, padX, y, cw, "chk_launch",
                    T("Khởi chạy tai thỏ Kumo ngay bây giờ", "Launch Kumo Living Notch now"),
                    _optLaunchNow);
                y = DrawCheckRow(g, padX, y, cw, "chk_openset",
                    T("Mở kèm Bảng Quản Lý Cài Đặt Kumo", "Also open Kumo Settings Manager"),
                    _optOpenSettings);

                DrawPrimaryBtn(g, "finish", new RectangleF(winW - padX - U(186f), winH - U(54f), U(186f), U(38f)),
                    T("Hoàn tất (Finish)", "Finish"));
            }
            else if (_step == 10)
            {
                // STEP 10: UNINSTALL CONFIRMATION
                RectangleF uCard = new RectangleF(padX, y + U(10f), cw, U(84f));
                DrawCard(g, uCard);
                DrawCenterText(g,
                    T("Bạn có chắc muốn gỡ cài đặt Kumo khỏi Windows?", "Are you sure you want to uninstall Kumo from Windows?"),
                    _fBold, Color.White, new RectangleF(uCard.X + U(14f), uCard.Y + U(16f), uCard.Width - U(28f), U(26f)));
                DrawCenterText(g, _installDir, _fDesc, Color.FromArgb(145, 155, 178),
                    new RectangleF(uCard.X + U(14f), uCard.Y + U(46f), uCard.Width - U(28f), U(24f)));

                y += U(112f);
                y = DrawCheckRow(g, padX, y, cw, "chk_rmdata",
                    T("Xóa luôn dữ liệu cá nhân (việc cần làm, nhật ký nước, kệ tệp, cài đặt)", "Also remove personal data (to-do list, water log, shelf, settings)"),
                    _optRemoveData);

                DrawSecondaryBtn(g, "close", new RectangleF(padX, winH - U(54f), U(120f), U(38f)), T("Hủy bỏ", "Cancel"));
                DrawPrimaryBtn(g, "do_uninstall", new RectangleF(winW - padX - U(195f), winH - U(54f), U(195f), U(38f)),
                    T("Gỡ cài đặt Kumo", "Uninstall Kumo"));
            }
            else if (_step == 11)
            {
                // STEP 11: UNINSTALL COMPLETE
                RectangleF doneCard = new RectangleF(padX, y + U(42f), cw, U(88f));
                DrawCard(g, doneCard);
                DrawCenterText(g,
                    T("Đã gỡ cài đặt Kumo hoàn tất.", "Kumo has been completely uninstalled."),
                    _fBold, Color.White, new RectangleF(doneCard.X, doneCard.Y + U(30f), doneCard.Width, U(28f)));

                DrawPrimaryBtn(g, "close", new RectangleF(winW - padX - U(150f), winH - U(54f), U(150f), U(38f)),
                    T("Đóng (Close)", "Close"));
            }

            using (Pen borderPen = new Pen(Color.FromArgb(38, 45, 64), 1f))
            {
                g.DrawLine(borderPen, 0, U(116f), winW, U(116f));
                g.DrawRectangle(borderPen, 0, 0, winW - 1, winH - 1);
            }
        }

        private void DrawStepperStrip(Graphics g, float x, float y, float w)
        {
            string[] namesVi = new string[] { "Giới thiệu", "Tùy chỉnh", "Cài đặt", "Hoàn tất" };
            string[] namesEn = new string[] { "Overview", "Customize", "Install", "Finish" };

            float slotW = w / 4f;
            for (int i = 0; i < 4; i++)
            {
                float cx = x + slotW * i + slotW * 0.5f;
                float cy = y + U(14f);

                if (i < 3)
                {
                    float nx = x + slotW * (i + 1) + slotW * 0.5f;
                    bool lineDone = (_step > i);
                    using (Pen lp = new Pen(lineDone ? Color.FromArgb(56, 189, 248) : Color.FromArgb(42, 49, 68), U(1.8f)))
                    {
                        g.DrawLine(lp, cx + U(56f), cy, nx - U(56f), cy);
                    }
                }

                bool isDone = (_step > i) || (_step == 3 && i == 3);
                bool isCur = (_step == i);

                RectangleF pill = new RectangleF(cx - U(52f), y + U(1f), U(104f), U(26f));
                if (_step <= 1 && i <= 1)
                {
                    _hits.Add(new HitRect { Id = i == 0 ? "go_welcome" : "go_custom", Rect = pill });
                }

                if (isCur)
                {
                    FillRoundRect(g, pill, U(13f), Color.FromArgb(38, 56, 189, 248));
                    using (Pen bp = new Pen(Color.FromArgb(120, 56, 189, 248), 1f))
                    using (GraphicsPath gp = CreateRoundRect(pill, U(13f)))
                    {
                        g.DrawPath(bp, gp);
                    }
                }

                string label = (isDone ? "✓ " : ((i + 1) + ". ")) + (_isVi ? namesVi[i] : namesEn[i]);
                Color col = isCur ? Color.White : (isDone ? Color.FromArgb(52, 211, 153) : Color.FromArgb(115, 126, 150));
                DrawCenterText(g, label, _fStep, col, pill);
            }
        }

        private void DrawFeatureCard(Graphics g, RectangleF r, int iconKind, Color accent, string title, string desc)
        {
            FillRoundRect(g, r, U(11f), Color.FromArgb(23, 27, 40));
            using (Pen p = new Pen(Color.FromArgb(40, 47, 66), 1f))
            using (GraphicsPath gp = CreateRoundRect(r, U(11f)))
            {
                g.DrawPath(p, gp);
            }

            RectangleF icBg = new RectangleF(r.X + U(12f), r.Y + U(13f), U(32f), U(32f));
            FillRoundRect(g, icBg, U(9f), Color.FromArgb(34, accent.R, accent.G, accent.B));

            float icx = icBg.X + icBg.Width * 0.5f;
            float icy = icBg.Y + icBg.Height * 0.5f;
            using (Pen ip = new Pen(accent, U(1.8f)))
            using (SolidBrush ib = new SolidBrush(accent))
            {
                ip.StartCap = LineCap.Round; ip.EndCap = LineCap.Round; ip.LineJoin = LineJoin.Round;
                if (iconKind == 0)
                {
                    // Music note
                    g.DrawLine(ip, icx - U(2f), icy + U(4f), icx - U(2f), icy - U(5.5f));
                    g.DrawLine(ip, icx + U(5f), icy + U(2.5f), icx + U(5f), icy - U(6.8f));
                    g.DrawLine(ip, icx - U(2f), icy - U(5.5f), icx + U(5f), icy - U(6.8f));
                    g.FillEllipse(ib, icx - U(6.2f), icy + U(2f), U(5f), U(4f));
                    g.FillEllipse(ib, icx + U(0.8f), icy + U(0.5f), U(5f), U(4f));
                }
                else if (iconKind == 1)
                {
                    // Cute Kumo "^ ^" Mascot eyes
                    g.DrawBezier(ip, new PointF(icx - U(7.5f), icy + U(2f)), new PointF(icx - U(5.2f), icy - U(3.2f)), new PointF(icx - U(3.0f), icy - U(3.2f)), new PointF(icx - U(1.0f), icy + U(2f)));
                    g.DrawBezier(ip, new PointF(icx + U(1.0f), icy + U(2f)), new PointF(icx + U(3.0f), icy - U(3.2f)), new PointF(icx + U(5.2f), icy - U(3.2f)), new PointF(icx + U(7.5f), icy + U(2f)));
                }
                else if (iconKind == 2)
                {
                    // 4-tile bento grid
                    g.DrawRectangle(ip, icx - U(6f), icy - U(6f), U(5f), U(5f));
                    g.DrawRectangle(ip, icx + U(1f), icy - U(6f), U(5f), U(5f));
                    g.DrawRectangle(ip, icx - U(6f), icy + U(1f), U(5f), U(5f));
                    g.DrawRectangle(ip, icx + U(1f), icy + U(1f), U(5f), U(5f));
                }
                else
                {
                    // Lightning bolt
                    g.DrawLines(ip, new PointF[] { new PointF(icx + U(1f), icy - U(7f)), new PointF(icx - U(4.5f), icy + U(0.5f)), new PointF(icx + U(0.5f), icy + U(0.5f)), new PointF(icx - U(1f), icy + U(7f)), new PointF(icx + U(4.5f), icy - U(0.5f)), new PointF(icx - U(0.5f), icy - U(0.5f)), new PointF(icx + U(1f), icy - U(7f)) });
                }
            }

            DrawLeftText(g, title, _fBold, Color.White, r.X + U(52f), r.Y + U(21f));
            RectangleF descRect = new RectangleF(r.X + U(52f), r.Y + U(34f), r.Width - U(62f), r.Height - U(38f));
            using (SolidBrush sb = new SolidBrush(Color.FromArgb(156, 168, 194)))
            {
                g.DrawString(desc, _fSmall, sb, descRect);
            }
        }

        private float DrawCheckRow(Graphics g, float x, float y, float w, string id, string label, bool isChecked)
        {
            RectangleF r = new RectangleF(x, y, w, U(42f));
            _hits.Add(new HitRect { Id = id, Rect = r });
            bool hov = (_hoverId == id);

            FillRoundRect(g, r, U(9f), hov ? Color.FromArgb(30, 36, 52) : Color.FromArgb(23, 27, 40));
            using (Pen p = new Pen(hov ? Color.FromArgb(62, 74, 102) : Color.FromArgb(40, 46, 64), 1f))
            using (GraphicsPath gp = CreateRoundRect(r, U(9f)))
            {
                g.DrawPath(p, gp);
            }

            RectangleF box = new RectangleF(x + U(14f), y + U(11f), U(20f), U(20f));
            if (isChecked)
            {
                FillRoundRect(g, box, U(6f), Color.FromArgb(37, 99, 235));
                using (Pen cp = new Pen(Color.White, U(2.0f)))
                {
                    cp.StartCap = LineCap.Round; cp.EndCap = LineCap.Round;
                    g.DrawLines(cp, new PointF[] { new PointF(box.X + U(4.5f), box.Y + U(10f)), new PointF(box.X + U(8.5f), box.Y + U(14f)), new PointF(box.X + U(15.5f), box.Y + U(6f)) });
                }
            }
            else
            {
                FillRoundRect(g, box, U(6f), Color.FromArgb(16, 19, 28));
                using (Pen bp = new Pen(Color.FromArgb(85, 96, 122), 1.4f))
                using (GraphicsPath gp = CreateRoundRect(box, U(6f)))
                {
                    g.DrawPath(bp, gp);
                }
            }

            DrawLeftText(g, label, _fBold, Color.White, x + U(46f), y + U(21f));
            return y + U(48f);
        }

        private void DrawCard(Graphics g, RectangleF r)
        {
            FillRoundRect(g, r, U(11f), Color.FromArgb(23, 27, 40));
            using (Pen p = new Pen(Color.FromArgb(42, 48, 66), 1f))
            using (GraphicsPath gp = CreateRoundRect(r, U(11f)))
            {
                g.DrawPath(p, gp);
            }
        }

        private void DrawPrimaryBtn(Graphics g, string id, RectangleF r, string text)
        {
            _hits.Add(new HitRect { Id = id, Rect = r });
            bool hov = (_hoverId == id);
            bool danger = (id == "do_uninstall");

            Color c1 = danger
                ? (hov ? Color.FromArgb(248, 113, 113) : Color.FromArgb(220, 38, 38))
                : (hov ? Color.FromArgb(56, 189, 248) : Color.FromArgb(14, 165, 233));
            Color c2 = danger
                ? (hov ? Color.FromArgb(220, 38, 38) : Color.FromArgb(185, 28, 28))
                : (hov ? Color.FromArgb(99, 102, 241) : Color.FromArgb(37, 99, 235));

            using (LinearGradientBrush lgb = new LinearGradientBrush(r, c1, c2, LinearGradientMode.Horizontal))
            using (GraphicsPath gp = CreateRoundRect(r, r.Height * 0.5f))
            {
                g.FillPath(lgb, gp);
            }
            DrawCenterText(g, text, _fBtn, Color.White, r);
        }

        private void DrawSecondaryBtn(Graphics g, string id, RectangleF r, string text)
        {
            _hits.Add(new HitRect { Id = id, Rect = r });
            bool hov = (_hoverId == id);
            FillRoundRect(g, r, r.Height * 0.5f, hov ? Color.FromArgb(48, 55, 76) : Color.FromArgb(32, 38, 54));
            using (Pen p = new Pen(Color.FromArgb(62, 72, 96), 1f))
            using (GraphicsPath gp = CreateRoundRect(r, r.Height * 0.5f))
            {
                g.DrawPath(p, gp);
            }
            DrawCenterText(g, text, _fDesc, Color.White, r);
        }

        private void OnWizMouseMove(object sender, MouseEventArgs e)
        {
            string hit = null;
            for (int i = _hits.Count - 1; i >= 0; i--)
            {
                if (_hits[i].Rect.Contains(e.Location)) { hit = _hits[i].Id; break; }
            }
            if (hit != _hoverId)
            {
                _hoverId = hit;
                Cursor = (hit != null) ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        private void OnWizMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;

            string hit = null;
            for (int i = _hits.Count - 1; i >= 0; i--)
            {
                if (_hits[i].Rect.Contains(e.Location)) { hit = _hits[i].Id; break; }
            }

            if (hit == null)
            {
                if (e.Y <= U(116f))
                {
                    SetupNative.ReleaseCapture();
                    SetupNative.SendMessage(Handle, SetupNative.WM_NCLBUTTONDOWN, new IntPtr(SetupNative.HTCAPTION), IntPtr.Zero);
                }
                return;
            }

            if (hit == "lang") { _isVi = !_isVi; Invalidate(); return; }
            if (hit == "minimize") { WindowState = FormWindowState.Minimized; return; }
            if (hit == "close") { Close(); return; }
            if (hit == "go_custom") { _step = 1; Invalidate(); return; }
            if (hit == "go_welcome") { _step = 0; Invalidate(); return; }
            if (hit == "chk_startmenu") { _optStartMenu = !_optStartMenu; Invalidate(); return; }
            if (hit == "chk_startup") { _optStartup = !_optStartup; Invalidate(); return; }
            if (hit == "chk_desktop") { _optDesktop = !_optDesktop; Invalidate(); return; }
            if (hit == "chk_launch") { _optLaunchNow = !_optLaunchNow; Invalidate(); return; }
            if (hit == "chk_openset") { _optOpenSettings = !_optOpenSettings; Invalidate(); return; }
            if (hit == "chk_rmdata") { _optRemoveData = !_optRemoveData; Invalidate(); return; }
            if (hit == "switch_uninst") { _step = 10; Invalidate(); return; }

            if (hit == "browse")
            {
                using (FolderBrowserDialog fbd = new FolderBrowserDialog())
                {
                    fbd.Description = T("Chọn thư mục cài đặt cho Kumo:", "Choose installation directory for Kumo:");
                    fbd.SelectedPath = _installDir;
                    if (fbd.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(fbd.SelectedPath))
                    {
                        string chosen = fbd.SelectedPath;
                        if (!chosen.EndsWith("Kumo", StringComparison.OrdinalIgnoreCase))
                        {
                            chosen = Path.Combine(chosen, "Kumo");
                        }
                        _installDir = chosen;
                        Invalidate();
                    }
                }
                return;
            }

            if (hit == "start_install")
            {
                _step = 2;
                _progress = 0.05f;
                _installStageIdx = 0;
                _statusVi = "Đang khởi tạo trình cài đặt chính thức Kumo...";
                _statusEn = "Initializing official Kumo installer...";
                Invalidate();

                string dir = _installDir;
                bool sm = _optStartMenu;
                bool su = _optStartup;
                bool dk = _optDesktop;

                Thread worker = new Thread(() =>
                {
                    try
                    {
                        InstallerCore.PerformInstall(dir, sm, dk, su, (prog, stIdx, vi, en) =>
                        {
                            try
                            {
                                BeginInvoke((MethodInvoker)delegate
                                {
                                    _progress = prog;
                                    _installStageIdx = stIdx;
                                    _statusVi = vi;
                                    _statusEn = en;
                                    Invalidate();
                                });
                            }
                            catch { }
                        });

                        Thread.Sleep(240);
                        BeginInvoke((MethodInvoker)delegate
                        {
                            _step = 3;
                            Invalidate();
                        });
                    }
                    catch (Exception ex)
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            MessageBox.Show(this, "Installation error:\n" + ex.Message, "Kumo Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            _step = 0;
                            Invalidate();
                        });
                    }
                });
                worker.IsBackground = true;
                worker.Start();
                return;
            }

            if (hit == "do_uninstall")
            {
                InstallerCore.PerformUninstall(_installDir, _optRemoveData);
                _step = 11;
                Invalidate();
                return;
            }

            if (hit == "finish")
            {
                if (_optLaunchNow)
                {
                    try
                    {
                        string targetExe = Path.Combine(_installDir, "Kumo.exe");
                        if (File.Exists(targetExe))
                        {
                            ProcessStartInfo psi = new ProcessStartInfo();
                            psi.FileName = targetExe;
                            psi.WorkingDirectory = _installDir;
                            if (_optOpenSettings) psi.Arguments = "--settings";
                            psi.UseShellExecute = true;
                            Process.Start(psi);
                        }
                    }
                    catch { }
                }
                Close();
                return;
            }
        }

        private static string TruncateMiddle(string s, int maxChars)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= maxChars) return s;
            int half = (maxChars - 3) / 2;
            return s.Substring(0, half) + "..." + s.Substring(s.Length - half);
        }

        private void DrawLeftText(Graphics g, string txt, Font f, Color c, float x, float cy)
        {
            SizeF sz = g.MeasureString(txt, f);
            using (SolidBrush b = new SolidBrush(c)) g.DrawString(txt, f, b, x, cy - sz.Height * 0.5f);
        }

        private void DrawRightText(Graphics g, string txt, Font f, Color c, float rx, float cy)
        {
            SizeF sz = g.MeasureString(txt, f);
            using (SolidBrush b = new SolidBrush(c)) g.DrawString(txt, f, b, rx - sz.Width, cy - sz.Height * 0.5f);
        }

        private void DrawCenterText(Graphics g, string txt, Font f, Color c, RectangleF r)
        {
            using (SolidBrush b = new SolidBrush(c))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                sf.Trimming = StringTrimming.EllipsisCharacter;
                g.DrawString(txt, f, b, r, sf);
            }
        }

        private static GraphicsPath CreateRoundRect(RectangleF r, float rad)
        {
            GraphicsPath gp = new GraphicsPath();
            float d = Math.Min(rad * 2f, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { gp.AddRectangle(r); return gp; }
            gp.AddArc(r.X, r.Y, d, d, 180, 90);
            gp.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            gp.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            gp.CloseFigure();
            return gp;
        }

        private static void FillRoundRect(Graphics g, RectangleF r, float rad, Color c)
        {
            using (GraphicsPath gp = CreateRoundRect(r, rad))
            using (SolidBrush b = new SolidBrush(c))
            {
                g.FillPath(b, gp);
            }
        }
    }

    internal static class SetupProgram
    {
        [STAThread]
        static void Main(string[] args)
        {
            SetupNative.EnableHighDpi();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool isUninstall = false;
            bool isSilent = false;
            string exeName = Path.GetFileNameWithoutExtension(Application.ExecutablePath);
            if (!string.IsNullOrEmpty(exeName) && exeName.IndexOf("uninst", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                isUninstall = true;
            }

            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i] ?? "";
                    if (a == "--preview-setup" && i + 1 < args.Length)
                    {
                        string outDir = args[i + 1];
                        using (SetupWizardForm f = new SetupWizardForm(false))
                        {
                            f.SavePreview(Path.Combine(outDir, "preview_kumo_setup_welcome.png"), 0);
                            f.SavePreview(Path.Combine(outDir, "preview_kumo_setup_custom.png"), 1);
                            f.SavePreview(Path.Combine(outDir, "preview_kumo_setup_complete.png"), 3);
                        }
                        return;
                    }
                    if (a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase) || a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase))
                        isUninstall = true;
                    if (a.Equals("--silent", StringComparison.OrdinalIgnoreCase) || a.Equals("/SILENT", StringComparison.OrdinalIgnoreCase) || a.Equals("/VERYSILENT", StringComparison.OrdinalIgnoreCase))
                        isSilent = true;
                }
            }

            if (isSilent)
            {
                if (isUninstall)
                {
                    string dir = InstallerCore.GetExistingInstallDir() ?? InstallerCore.GetDefaultInstallDir();
                    InstallerCore.PerformUninstall(dir, false);
                }
                else
                {
                    InstallerCore.PerformInstall(InstallerCore.GetDefaultInstallDir(), true, true, true, null);
                }
                return;
            }

            Application.Run(new SetupWizardForm(isUninstall));
        }
    }
}
