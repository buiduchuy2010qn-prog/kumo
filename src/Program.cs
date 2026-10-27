using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("Kumo")]
[assembly: AssemblyDescription("Kumo — Official Living Notch & Settings Manager for Windows")]
[assembly: AssemblyCompany("Kumo Desktop")]
[assembly: AssemblyProduct("Kumo")]
[assembly: AssemblyCopyright("Copyright © 2026 Kumo Desktop")]
// Dynamic version via BuildInfo.g.cs

namespace Kumo
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            NativeMethods.EnableHighDpi();

            if (args != null && args.Length > 0 && args[0] == "--preview")
            {
                string outDir = args.Length > 1 ? args[1] : Application.StartupPath;
                try
                {
                    AppState st = new AppState();
                    using (HardwareMonitor hw = new HardwareMonitor())
                    using (MediaLyricsManager media = new MediaLyricsManager())
                    {
                        hw.IsNotchExpanded = true;
                        Thread.Sleep(350);
                        using (NotchForm nf = new NotchForm(st, hw, media))
                        using (SettingsForm sf = new SettingsForm(st, nf))
                        {
                            nf.SavePreviewBitmap(Path.Combine(outDir, "preview_kumo_music.png"), true, 0);
                            nf.SavePreviewBitmap(Path.Combine(outDir, "preview_kumo_pomo.png"), true, 1);
                            nf.SavePreviewBitmap(Path.Combine(outDir, "preview_kumo_water.png"), true, 2);
                            nf.SavePreviewBitmap(Path.Combine(outDir, "preview_kumo_shelf.png"), true, 3);
                            nf.SavePreviewBitmap(Path.Combine(outDir, "preview_kumo_todo.png"), true, 4);
                            nf.SavePreviewBitmap(Path.Combine(outDir, "preview_kumo_perf.png"), true, 5);

                            media.SetMockPreviewTrack(true);
                            nf.SavePreviewBitmap(Path.Combine(outDir, "preview_kumo_music_playing.png"), true, 0);
                            nf.SavePreviewBitmap(Path.Combine(outDir, "preview_kumo_pill_playing.png"), false, -1);
                            media.SetMockPreviewTrack(false);

                            sf.SaveSettingsPreview(Path.Combine(outDir, "preview_kumo_settings.png"), 0);
                        }
                    }
                }
                catch (Exception ex)
                {
                    File.WriteAllText(Path.Combine(outDir, "preview_err.txt"), ex.ToString());
                }
                return;
            }

            bool openSettingsOnLaunch = (args != null && args.Length > 0 && args[0] == "--settings");

            bool createdNew;
            using (Mutex mutex = new Mutex(true, "Local\\Kumo.SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    // Running Kumo.exe a second time pops open the Settings Manager on the running instance!
                    if (NativeMethods.WM_OPEN_SETTINGS != 0)
                    {
                        NativeMethods.PostMessage(NativeMethods.HWND_BROADCAST, NativeMethods.WM_OPEN_SETTINGS, IntPtr.Zero, IntPtr.Zero);
                    }
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                AppState state = new AppState();
                using (HardwareMonitor hw = new HardwareMonitor())
                using (MediaLyricsManager media = new MediaLyricsManager())
                using (NotifyIcon tray = new NotifyIcon())
                {
                    NotchForm form = new NotchForm(state, hw, media);

                    try
                    {
                        tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                    }
                    catch
                    {
                        tray.Icon = SystemIcons.Application;
                    }
                    tray.Text = "Kumo — Nhấp để mở Cài đặt (Settings)";
                    tray.Visible = true;

                    tray.MouseClick += (s, e) =>
                    {
                        if (e.Button == MouseButtons.Left)
                        {
                            form.OpenSettings(-1);
                        }
                    };
                    tray.DoubleClick += (s, e) => { form.OpenSettings(-1); };

                    ContextMenu menu = new ContextMenu();
                    MenuItem miSettings = new MenuItem("⚙ Kumo Cài đặt (Settings...)", (s, e) => { form.OpenSettings(-1); });
                    miSettings.DefaultItem = true;
                    menu.MenuItems.Add(miSettings);
                    menu.MenuItems.Add("-");
                    menu.MenuItems.Add("👋 Xem thử Kumo vẫy tay (Test Wave)", (s, e) => { form.TriggerMascotWave(3.5); });
                    menu.MenuItems.Add("☁️ Bật / Tắt Linh vật Kumo", (s, e) => { form.ShowRestingMascot = !form.ShowRestingMascot; });
                    menu.MenuItems.Add("➖ Bật / Tắt Luôn thu gọn (Minimal)", (s, e) => { form.MinimalMode = !form.MinimalMode; });
                    menu.MenuItems.Add("-");
                    menu.MenuItems.Add("🎵 Music & Lyrics", (s, e) => { form.CurrentPage = 0; });
                    menu.MenuItems.Add("⏱ Pomodoro Timer", (s, e) => { form.CurrentPage = 1; });
                    menu.MenuItems.Add("💧 Water Reminder", (s, e) => { form.CurrentPage = 2; });
                    menu.MenuItems.Add("📥 File Shelf", (s, e) => { form.CurrentPage = 3; });
                    menu.MenuItems.Add("✅ TodoList", (s, e) => { form.CurrentPage = 4; });
                    menu.MenuItems.Add("⚡ System Monitor", (s, e) => { form.CurrentPage = 5; });
                    menu.MenuItems.Add("-");
                    menu.MenuItems.Add("❌ Thoát Kumo (Exit)", (s, e) =>
                    {
                        tray.Visible = false;
                        Application.Exit();
                    });
                    tray.ContextMenu = menu;

                    if (openSettingsOnLaunch)
                    {
                        form.Shown += (s, e) => { form.OpenSettings(-1); };
                    }

                    Application.Run(form);
                }
            }
        }
    }
}
