using System.Text.Json;
using System.Text.Json.Serialization;

namespace Isle;

// Property names intentionally match the original Dynamic Edge settings.json so an existing
// configuration can be imported on first run.
public sealed class AppSettings
{
    // Position & docking
    public string DockPosition { get; set; } = "Top";          // Top, Bottom, Left, Right, TopLeft, TopRight
    public string DockMode { get; set; } = "Floating";         // ThinLine, Floating, Notch
    public int TopOffset { get; set; } = 10;
    public int SideOffsetPercent { get; set; } = 50;
    public bool AutoHideToEdge { get; set; }                   // auto-collapse to thin line strip when idle
    public bool AutoHideWhenIdle { get; set; }                 // stealth: fully hidden until edge hover
    public string TargetDisplayId { get; set; } = "Primary";   // "Primary" or a screen DeviceName
    public bool FollowCursorDisplay { get; set; }
    public int ThinLineThickness { get; set; } = 3;
    public string ToggleVisibilityHotkey { get; set; } = "Alt+H";
    public bool AutoHideInFullscreen { get; set; } = true;

    // Interaction & physics
    public bool IsPinned { get; set; }
    public bool ExpandOnClickOnly { get; set; }
    public string ExpandClickMode { get; set; } = "Single";    // Single, Double
    public int DwellTimeMs { get; set; } = 120;
    public int GraceTimeMs { get; set; } = 350;
    public int HotZoneThickness { get; set; } = 8;
    public string AnimationSpeed { get; set; } = "Balanced";   // Snappy, Fast, Balanced, Gentle
    public string AnimationStyle { get; set; } = "FluidSpring"; // Classic, FluidSpring, Spring, Bounce
    public bool UseStaggeredNotchMotion { get; set; } = true;
    public bool EnableHeadsetPopup { get; set; } = true;
    public bool LaunchOnStartup { get; set; } = true;
    public bool QuietFeedMode { get; set; } = true;
    public bool SilentDormantMode { get; set; }
    public bool PlayTimerSound { get; set; } = true;
    public bool MagicShakeEnabled { get; set; }
    public string MagicShakeSensitivity { get; set; } = "Medium"; // Low, Medium, High
    public bool MagicShakeExcludeFullscreen { get; set; } = true;

    // Appearance
    public string IslandColor { get; set; } = "#000000";
    public string AccentColor { get; set; } = "#70E0C0";
    public string AppTheme { get; set; } = "System";           // System, Dark, Light
    public bool ShowWaveformVisualizer { get; set; } = true;
    public bool EnableMediaBackdrop { get; set; }
    public string SeekbarStyle { get; set; } = "Modern";       // Modern, Thin, Soundwave
    public double IslandTextScale { get; set; } = 1.0;         // 0.85 .. 1.3

    // Tabs
    public bool TabMediaEnabled { get; set; } = true;
    public bool TabTimerEnabled { get; set; } = true;
    public bool TabClipboardEnabled { get; set; } = true;
    public bool TabQuickLaunchEnabled { get; set; } = true;
    public string TabNavigationStyle { get; set; } = "Dots";   // None, Header, Dots
    public int LastActiveTab { get; set; }
    public bool ShowLyricsInCollapsed { get; set; }
    public bool ShowLyricsInExpanded { get; set; } = true;
    public bool EnableMusicRadar { get; set; } = true;

    // Widgets
    public string IdleWidgetMode { get; set; } = "Clock";      // Clock, Battery, TrackInfo, SystemStats, MinimalDot, AnimatedEyes, WhiteEyeballs, Weather
    public bool Use12HourClock { get; set; } = true;
    public string IdleClockStyle { get; set; } = "Classic";    // Classic, Bold, DotMatrix
    public string DropTargetFolderPath { get; set; } = "";
    public List<string> QuickAppPaths { get; set; } = new() { "spotify.exe", "calc.exe", "chrome.exe" };
    public bool ShowQuickApp1InMedia { get; set; } = true;
    public bool ShowQuickApp2InMedia { get; set; } = true;
    public bool ShowQuickApp3InMedia { get; set; } = true;
    public bool IslandPetEnabled { get; set; }
    public string IslandPetType { get; set; } = "Cat";         // Cat, Dog, Bunny, Ghost

    // Clipboard
    public bool ClipboardEnabled { get; set; } = true;
    public string ClipboardRetention { get; set; } = "Permanent"; // 24Hours, 3Days, 7Days, Permanent
    public int ClipboardMaxItems { get; set; } = 200;
    public string ClipboardPrivacyExclusions { get; set; } = "";

    // Window memory / onboarding
    public double SettingsWindowX { get; set; } = double.NaN;
    public double SettingsWindowY { get; set; } = double.NaN;
    public double SettingsWindowWidth { get; set; } = 900;
    public double SettingsWindowHeight { get; set; } = 660;
    public bool HasCompletedOnboarding { get; set; }

    // Extras (not in the original)
    public bool TabNotesEnabled { get; set; } = true;
    public string Notes { get; set; } = "";
    public bool EnableVolumePopup { get; set; } = true;
    public bool EnableLockKeysPopup { get; set; } = true;
    public bool EnablePrivacyIndicator { get; set; } = true;
    public bool EnableBatteryPopups { get; set; } = true;
    public bool EnableTrackPopups { get; set; } = true;
    public bool UseAlbumArtTint { get; set; } = true;          // Apple-style: tint waveform with album colour
    public bool ShowNetworkSpeed { get; set; } = true;
    public string WeatherCity { get; set; } = "";              // empty = locate by IP
    public bool WeatherFahrenheit { get; set; }
    public int PomodoroFocusMinutes { get; set; } = 25;
    public int PomodoroBreakMinutes { get; set; } = 5;

    // ---- persistence ----
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals | JsonNumberHandling.AllowReadingFromString,
    };

    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Isle");
    static string FilePath => Path.Combine(DataDir, "settings.json");

    /// The app used to be called "Dynamic Edge Copy": carry its data folder (settings, clipboard history) over once.
    static void MigrateOldDataDir()
    {
        string old = Path.Combine(Path.GetDirectoryName(DataDir), "DynamicEdgeCopy");
        if (Directory.Exists(DataDir) || !Directory.Exists(old)) return;
        try
        {
            Directory.Move(old, DataDir);
            string clips = Path.Combine(DataDir, "Clipboard", "ClipboardData.json");
            if (File.Exists(clips)) // image clips are stored with absolute paths (JSON-escaped backslashes)
                File.WriteAllText(clips, File.ReadAllText(clips).Replace(old.Replace("\\", "\\\\"), DataDir.Replace("\\", "\\\\")));
        }
        catch { /* a failed move just means starting fresh */ }
    }

    public static AppSettings Load()
    {
        MigrateOldDataDir();
        Directory.CreateDirectory(DataDir);
        string path = FilePath;
        if (!File.Exists(path))
        {
            // First run: import the original app's settings if it is installed.
            string original = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages", "EyuelEngida.DynamicIslandWin_z0hs4p09nm9p2", "LocalState", "settings.json");
            if (File.Exists(original)) path = original;
        }
        try
        {
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings();
                if (path != FilePath)
                {
                    // Imported: keep functional choices, but start from the Apple-style motion & look.
                    var d = new AppSettings();
                    s.AnimationStyle = d.AnimationStyle;
                    s.AnimationSpeed = d.AnimationSpeed;
                    s.UseStaggeredNotchMotion = d.UseStaggeredNotchMotion;
                    s.SeekbarStyle = d.SeekbarStyle;
                    s.TabNavigationStyle = d.TabNavigationStyle;
                    s.HasCompletedOnboarding = false;
                    s.EnableMusicRadar = true; // was a Plus lock in the original; it's just a button here
                    s.SettingsWindowX = s.SettingsWindowY = double.NaN;
                }
                s.Normalize();
                return s;
            }
        }
        catch
        {
            // unreadable: keep a copy before defaults overwrite it
            try { if (path == FilePath) File.Copy(path, path + ".bad", true); } catch { }
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, true);
        }
        catch { }
    }

    void Normalize()
    {
        QuickAppPaths ??= new();
        IslandTextScale = Math.Clamp(IslandTextScale <= 0 ? 1 : IslandTextScale, 0.85, 1.3);
        ThinLineThickness = Math.Clamp(ThinLineThickness, 2, 8);
        TopOffset = Math.Clamp(TopOffset, 0, 60);
        SideOffsetPercent = Math.Clamp(SideOffsetPercent, 5, 95);
        ClipboardMaxItems = Math.Clamp(ClipboardMaxItems, 10, 100000);
        if (SeekbarStyle is not ("Modern" or "Thin" or "Soundwave")) SeekbarStyle = "Modern";
    }
}
