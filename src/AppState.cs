using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;

namespace Kumo
{
    public class TodoItem
    {
        public string Text;
        public bool Done;
    }

    public class ShelfItem
    {
        public string FilePath;
        public string DisplayName;
    }

    public class AppState
    {
        public string DataDir { get; private set; }

        // ==================== GENERAL & NOTCH SETTINGS ====================
        public bool IsVietnamese = true;
        public int UiThemeMode = 0; // 0: White/Light Control Panel (Default), 1: Dark Control Panel (Notch is ALWAYS Classic Black)
        public int ThemeIndex = 0;

        public bool IsSettingsLight { get { return UiThemeMode == 0; } }
        public bool IsNotchLight { get { return false; } }
        public int MonitorIndex = 0;
        public int CompactMode = 0;

        // Nusa v1.1.0 Exact Settings:
        public int NotchSizePreset = 0;        // 0: Auto, 1: 100%, 2: 115%, 3: 130%, 4: 150%
        public bool AlwaysMinimal = false;     // "Minimal notch" — keep tiny 44x4.5 dp pill when closed
        public bool AutoMinimalEnabled = true; // Smart titlebar/tab scan auto-shrink
        public bool MascotEnabled = true;      // "Nusa mascot" — idle eyes & wave hello
        public bool ReminderSounds = true;     // "Reminder sounds" — chimes for Pomodoro & Water
        public bool HideInFullscreen = true;   // "Hide in full screen" — games, videos & slides
        public bool StartWithWindows = false;

        // ==================== APPEARANCE & MOTION ====================
        public bool OpenByClick = false;       // false = Hover, true = Click
        public bool ShowPageLabels = true;     // Show page names when hovering bottom dots
        public bool FastAnimation = false;     // false = Normal (Nusa smooth spring), true = Fast

        // ==================== MUSIC & LYRICS ====================
        public bool ShowLyrics = true;         // Show synced lyrics from LRCLIB
        public bool DefaultLyricsSplit = false;
        public bool EqualizerEnabled = true;
        public bool DownloadHudEnabled = true;   // Animated equalizer bars on Notch

        // ==================== ACTIVE UI NAVIGATION ====================
        public int ActiveTab = 0;
        public int SelectedPerfResource = 2;

        // ==================== POMODORO STATE ====================
        public int FocusMinutes = 25;
        public int ShortBreakMinutes = 5;
        public int LongBreakMinutes = 15;
        public int PomodoroMode = 0; // 0: Focus, 1: Short Break, 2: Long Break
        public int PomodoroRemainingSec = 25 * 60;
        public bool PomodoroRunning = false;
        public int PomodoroCompletedSessions = 0;

        public int PomodoroSecondsLeft
        {
            get { return PomodoroRemainingSec; }
            set { PomodoroRemainingSec = value; }
        }
        public bool PomodoroBreakMode
        {
            get { return PomodoroMode != 0; }
            set { PomodoroMode = value ? 1 : 0; }
        }
        public int PomodoroWorkMinutes { get { return FocusMinutes; } }
        public int PomodoroBreakMinutes { get { return ShortBreakMinutes; } }

        // ==================== WATER REMINDER STATE ====================
        public bool WaterRemindersEnabled = true;
        public int WaterGoalMl = 2000;
        public int WaterDrankMl = 0;
        public int WaterCupSizeMl = 250;
        public int[] WaterHourlyMl = new int[24];
        public string WaterLastDate = "";

        public int WaterMlToday
        {
            get { return WaterDrankMl; }
            set { WaterDrankMl = Math.Max(0, value); }
        }

        public void LogWaterDrink(int deltaMl)
        {
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            if (WaterLastDate != today)
            {
                WaterLastDate = today;
                WaterDrankMl = 0;
                Array.Clear(WaterHourlyMl, 0, 24);
            }
            int hr = DateTime.Now.Hour;
            WaterDrankMl = Math.Max(0, WaterDrankMl + deltaMl);
            WaterHourlyMl[hr] = Math.Max(0, WaterHourlyMl[hr] + deltaMl);
            SaveAll();
        }

        public void ResetWaterToday()
        {
            WaterLastDate = DateTime.Now.ToString("yyyy-MM-dd");
            WaterDrankMl = 0;
            Array.Clear(WaterHourlyMl, 0, 24);
            SaveAll();
        }

        // ==================== TO-DO & FILE SHELF ====================
        public bool ClearShelfOnStart = false;
        public List<TodoItem> Todos = new List<TodoItem>();
        public List<ShelfItem> Shelf = new List<ShelfItem>();
        public List<string> ShelfFiles = new List<string>();

        private bool _isFirstLaunchLoad = true;

        public bool RunAtStartup
        {
            get { return StartWithWindows; }
            set { StartWithWindows = value; }
        }

        public void SetStartupRegistry(bool enable)
        {
            StartWithWindows = enable;
            NativeMethods.SetStartupEnabled(enable, System.Windows.Forms.Application.ExecutablePath);
        }

        public float GetSizeMultiplier()
        {
            switch (NotchSizePreset)
            {
                case 1: return 0.90f; // 90%
                case 2: return 1.00f; // 100%
                case 3: return 1.10f; // 110%
                case 4: return 1.25f; // 125%
                default: return 1.00f; // Auto
            }
        }

        public AppState()
        {
            DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kumo");
            try { Directory.CreateDirectory(DataDir); } catch { }
            StartWithWindows = NativeMethods.IsStartupEnabled();
            LoadAll();
        }

        public void ResetDefaults()
        {
            UiThemeMode = 0;
            NotchSizePreset = 0;
            AlwaysMinimal = false;
            AutoMinimalEnabled = true;
            MascotEnabled = true;
            ReminderSounds = true;
            HideInFullscreen = true;
            MonitorIndex = 0;
            OpenByClick = false;
            ShowPageLabels = true;
            FastAnimation = false;
            ShowLyrics = true;
            DefaultLyricsSplit = false;
            EqualizerEnabled = true;
            FocusMinutes = 25;
            ShortBreakMinutes = 5;
            LongBreakMinutes = 15;
            if (!PomodoroRunning)
            {
                PomodoroMode = 0;
                PomodoroRemainingSec = FocusMinutes * 60;
            }
            WaterRemindersEnabled = true;
            WaterGoalMl = 2000;
            WaterCupSizeMl = 250;
            ClearShelfOnStart = false;
            SaveAll();
        }

        public Color GetAccentColor()
        {
            switch (ThemeIndex % 4)
            {
                case 0: return Color.FromArgb(0, 229, 255);
                case 1: return Color.FromArgb(168, 85, 247);
                case 2: return Color.FromArgb(16, 185, 129);
                default: return Color.FromArgb(244, 63, 94);
            }
        }

        public Color GetSecondaryAccentColor()
        {
            switch (ThemeIndex % 4)
            {
                case 0: return Color.FromArgb(168, 85, 247);
                case 1: return Color.FromArgb(236, 72, 153);
                case 2: return Color.FromArgb(6, 182, 212);
                default: return Color.FromArgb(251, 146, 60);
            }
        }

        public string T(string vi, string en)
        {
            return IsVietnamese ? vi : en;
        }

        public void LoadAll()
        {
            try
            {
                string settingsFile = Path.Combine(DataDir, "settings.txt");
                if (File.Exists(settingsFile))
                {
                    foreach (string line in File.ReadAllLines(settingsFile, Encoding.UTF8))
                    {
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string k = line.Substring(0, eq).Trim();
                        string v = line.Substring(eq + 1).Trim();
                        if (k == "lang") IsVietnamese = (v == "vi");
                        else if (k == "theme") int.TryParse(v, out ThemeIndex);
                        else if (k == "ui_theme_mode") { int.TryParse(v, out UiThemeMode); if (UiThemeMode < 0 || UiThemeMode > 2) UiThemeMode = 0; }
                        else if (k == "monitor") int.TryParse(v, out MonitorIndex);
                        else if (k == "compact") int.TryParse(v, out CompactMode);
                        else if (k == "size_preset") int.TryParse(v, out NotchSizePreset);
                        else if (k == "always_minimal") AlwaysMinimal = (v == "1");
                        else if (k == "auto_minimal") AutoMinimalEnabled = (v == "1");
                        else if (k == "mascot") MascotEnabled = (v == "1");
                        else if (k == "sounds") ReminderSounds = (v == "1");
                        else if (k == "fullscreen_hide") HideInFullscreen = (v == "1");
                        else if (k == "open_click") OpenByClick = (v == "1");
                        else if (k == "page_labels") ShowPageLabels = (v == "1");
                        else if (k == "fast_anim") FastAnimation = (v == "1");
                        else if (k == "show_lyrics") ShowLyrics = (v == "1");
                        else if (k == "lyrics_split") DefaultLyricsSplit = (v == "1");
                        else if (k == "equalizer") EqualizerEnabled = (v == "1");
                        else if (k == "dl_hud") DownloadHudEnabled = (v == "1");
                        else if (k == "water_remind") WaterRemindersEnabled = (v == "1");
                        else if (k == "water_goal") int.TryParse(v, out WaterGoalMl);
                        else if (k == "water_drank") int.TryParse(v, out WaterDrankMl);
                        else if (k == "water_cup") int.TryParse(v, out WaterCupSizeMl);
                        else if (k == "water_date") WaterLastDate = v;
                        else if (k == "water_hours")
                        {
                            string[] parts = v.Split(',');
                            for (int hi = 0; hi < 24 && hi < parts.Length; hi++)
                            {
                                int hv;
                                if (int.TryParse(parts[hi], out hv)) WaterHourlyMl[hi] = Math.Max(0, hv);
                            }
                        }
                        else if (k == "clear_shelf_start") ClearShelfOnStart = (v == "1");
                        else if (k == "short_break_min") int.TryParse(v, out ShortBreakMinutes);
                        else if (k == "long_break_min") int.TryParse(v, out LongBreakMinutes);
                        else if (k == "focus_min")
                        {
                            if (int.TryParse(v, out FocusMinutes))
                                PomodoroRemainingSec = FocusMinutes * 60;
                        }
                    }
                }
                string today = DateTime.Now.ToString("yyyy-MM-dd");
                if (!string.IsNullOrEmpty(WaterLastDate) && WaterLastDate != today)
                {
                    WaterLastDate = today;
                    WaterDrankMl = 0;
                    Array.Clear(WaterHourlyMl, 0, 24);
                }
                else if (string.IsNullOrEmpty(WaterLastDate))
                {
                    WaterLastDate = today;
                }

                // Ensure hourly hydration chart matches WaterDrankMl if upgraded from an earlier settings file
                int hourlySum = 0;
                for (int hi = 0; hi < 24; hi++) hourlySum += WaterHourlyMl[hi];
                if (WaterDrankMl > 0 && hourlySum == 0)
                {
                    int rem = WaterDrankMl;
                    int cup = Math.Max(200, WaterCupSizeMl);
                    int curH = Math.Max(8, Math.Min(20, DateTime.Now.Hour));
                    for (int hSlot = 8; hSlot <= curH && rem > 0; hSlot += 2)
                    {
                        int add = Math.Min(rem, cup);
                        WaterHourlyMl[hSlot] += add;
                        rem -= add;
                    }
                    if (rem > 0) WaterHourlyMl[curH] += rem;
                }
            }
            catch { }

            try
            {
                string todoFile = Path.Combine(DataDir, "todo.txt");
                if (File.Exists(todoFile))
                {
                    Todos.Clear();
                    foreach (string line in File.ReadAllLines(todoFile, Encoding.UTF8))
                    {
                        if (string.IsNullOrEmpty(line)) continue;
                        bool done = line.StartsWith("[x] ");
                        string txt = line.Length > 4 ? line.Substring(4) : line;
                        Todos.Add(new TodoItem { Done = done, Text = txt });
                    }
                }
                else
                {
                    Todos.Add(new TodoItem { Done = false, Text = "Theo dõi hiệu năng CPU / SSD / GPU" });
                    Todos.Add(new TodoItem { Done = false, Text = "Uống đủ 2.0L nước hôm nay" });
                    Todos.Add(new TodoItem { Done = true, Text = "Cài đặt thành công Kumo v" + KumoBuildInfo.Version });
                }
            }
            catch { }

            try
            {
                string shelfFile = Path.Combine(DataDir, "shelf.txt");
                Shelf.Clear();
                ShelfFiles.Clear();
                if (_isFirstLaunchLoad && ClearShelfOnStart)
                {
                    try { if (File.Exists(shelfFile)) File.WriteAllText(shelfFile, "", Encoding.UTF8); } catch { }
                }
                else if (File.Exists(shelfFile))
                {
                    foreach (string line in File.ReadAllLines(shelfFile, Encoding.UTF8))
                    {
                        string p = line.Trim();
                        if (string.IsNullOrEmpty(p)) continue;
                        if (File.Exists(p) || Directory.Exists(p))
                        {
                            ShelfFiles.Add(p);
                            Shelf.Add(new ShelfItem { FilePath = p, DisplayName = Path.GetFileName(p) });
                        }
                    }
                }
                _isFirstLaunchLoad = false;
            }
            catch { }
        }

        public void SaveAll()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("lang=" + (IsVietnamese ? "vi" : "en"));
                sb.AppendLine("panel_theme_mode=" + UiThemeMode);
                sb.AppendLine("theme=" + ThemeIndex);
                sb.AppendLine("monitor=" + MonitorIndex);
                sb.AppendLine("compact=" + CompactMode);
                sb.AppendLine("size_preset=" + NotchSizePreset);
                sb.AppendLine("always_minimal=" + (AlwaysMinimal ? "1" : "0"));
                sb.AppendLine("auto_minimal=" + (AutoMinimalEnabled ? "1" : "0"));
                sb.AppendLine("mascot=" + (MascotEnabled ? "1" : "0"));
                sb.AppendLine("sounds=" + (ReminderSounds ? "1" : "0"));
                sb.AppendLine("fullscreen_hide=" + (HideInFullscreen ? "1" : "0"));
                sb.AppendLine("open_click=" + (OpenByClick ? "1" : "0"));
                sb.AppendLine("page_labels=" + (ShowPageLabels ? "1" : "0"));
                sb.AppendLine("fast_anim=" + (FastAnimation ? "1" : "0"));
                sb.AppendLine("show_lyrics=" + (ShowLyrics ? "1" : "0"));
                sb.AppendLine("lyrics_split=" + (DefaultLyricsSplit ? "1" : "0"));
                sb.AppendLine("equalizer=" + (EqualizerEnabled ? "1" : "0"));
                sb.AppendLine("dl_hud=" + (DownloadHudEnabled ? "1" : "0"));
                sb.AppendLine("water_remind=" + (WaterRemindersEnabled ? "1" : "0"));
                sb.AppendLine("water_goal=" + WaterGoalMl);
                sb.AppendLine("water_drank=" + WaterDrankMl);
                sb.AppendLine("water_cup=" + WaterCupSizeMl);
                sb.AppendLine("water_date=" + (WaterLastDate ?? ""));
                string[] hrStrs = new string[24];
                for (int hi = 0; hi < 24; hi++) hrStrs[hi] = WaterHourlyMl[hi].ToString();
                sb.AppendLine("water_hours=" + string.Join(",", hrStrs));
                sb.AppendLine("clear_shelf_start=" + (ClearShelfOnStart ? "1" : "0"));
                sb.AppendLine("focus_min=" + FocusMinutes);
                sb.AppendLine("short_break_min=" + ShortBreakMinutes);
                sb.AppendLine("long_break_min=" + LongBreakMinutes);
                File.WriteAllText(Path.Combine(DataDir, "settings.txt"), sb.ToString(), Encoding.UTF8);

                List<string> todoLines = new List<string>();
                foreach (var t in Todos)
                    todoLines.Add((t.Done ? "[x] " : "[ ] ") + t.Text);
                File.WriteAllText(Path.Combine(DataDir, "todo.txt"), string.Join("\r\n", todoLines.ToArray()), Encoding.UTF8);

                File.WriteAllText(Path.Combine(DataDir, "shelf.txt"), string.Join("\r\n", ShelfFiles.ToArray()), Encoding.UTF8);
            }
            catch { }
        }
    }
}
