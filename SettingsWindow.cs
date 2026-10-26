using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Isle.Services;
using Ellipse = System.Windows.Shapes.Ellipse;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace Isle;

/// macOS System Settings–style window. Every setting is a row descriptor, so the sidebar search
/// can filter rows across all sections and pages are rebuilt from the same list.
public sealed class SettingsWindow : Window
{
    sealed record Row(string Section, string Group, string Title, string Sub, Func<FrameworkElement> Make, bool Wide = false);

    static readonly (string Id, string Title, string Glyph, string Tile, string Sub)[] Sections =
    {
        ("Position", "Position & Docking", "", "#0A84FF", "Control screen edge location, idle appearance style, and position offsets."),
        ("Monitors", "Monitors & Displays", "", "#5E5CE6", "Configure multi-monitor routing and cursor following behavior."),
        ("Physics", "Interaction & Physics", "", "#FF9F0A", "Fine-tune hover sensitivity, auto-collapse timing, and activation gestures."),
        ("Appearance", "Appearance & Themes", "", "#BF5AF2", "Personalize colors, contrast, themes, and audio visualizers."),
        ("Tabs", "Island Tabs", "", "#30D158", "Toggle and organize active modules inside the expanded island card."),
        ("Widgets", "Widgets & Apps", "", "#FF375F", "Configure idle capsule mini-widgets and quick launch shortcuts."),
        ("Clipboard", "Clipboard Manager", "", "#32ADE6", "Manage clipboard history retention, capacity limits, and sensitive data filters."),
        ("TextSize", "Text & Font Size", "", "#AC8E68", "Scale typography and text labels across island views for ideal accessibility."),
        ("Extras", "Extras", "", "#00C7BE", "Bonus live activities and widgets that go beyond the original."),
        ("About", "About & Shortcuts", "", "#8E8E93", "Product details, version, gesture shortcuts, and credits."),
    };

    static readonly FontFamily UiFont = Isle.Controls.Ui.Font;
    static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    static AppSettings S => AppHost.Settings;

    readonly List<Row> _rows = new();
    readonly List<Action> _refreshers = new();
    readonly Dictionary<string, (Border Item, TextBlock Label)> _nav = new();
    string _current = "Position", _query = "";
    bool _dark;
    IntPtr _hwnd;
    ScrollViewer _scroll;
    TextBox _searchBox;
    TextBlock _toolbarTitle;
    readonly List<(Ellipse Dot, Color On)> _lights = new();
    readonly List<string> _history = new() { "Position" };
    int _historyPos;

    // palette
    Brush _win, _side, _card, _cardBorder, _sep, _label, _sec, _accent, _btn, _btnBorder, _segTrack, _segSel, _inputBg, _inputBorder, _menuBg, _hover, _red;
    Color _green, _toggleOff;

    public SettingsWindow()
    {
        Title = "Isle Settings";
        FontFamily = UiFont;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        MinWidth = 780;
        MinHeight = 520;
        Width = Math.Max(MinWidth, double.IsNaN(S.SettingsWindowWidth) ? 900 : S.SettingsWindowWidth);
        Height = Math.Max(MinHeight, double.IsNaN(S.SettingsWindowHeight) ? 660 : S.SettingsWindowHeight);
        if (!double.IsNaN(S.SettingsWindowX) && !double.IsNaN(S.SettingsWindowY) &&
            new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight)
                .Contains(new Point(S.SettingsWindowX + 80, S.SettingsWindowY + 20)))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = S.SettingsWindowX;
            Top = S.SettingsWindowY;
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;

        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/app.ico")); } catch { }

        // macOS chrome: client area extends into the title bar; traffic lights are drawn by us
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 52, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0, 0, 0, 1),
            UseAeroCaptionButtons = false, CornerRadius = new CornerRadius(0),
        });
        Activated += (_, _) => PaintLights(true);
        Deactivated += (_, _) => PaintLights(false);

        _dark = AppHost.IsDarkTheme();
        DefineRows();
        BuildShell();

        SourceInitialized += (_, _) => { _hwnd = new WindowInteropHelper(this).Handle; StyleFrame(); };
        AppHost.SettingsChanged += OnSettingsChanged;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreference;
        Closing += (_, _) =>
        {
            var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            S.SettingsWindowX = b.X; S.SettingsWindowY = b.Y; S.SettingsWindowWidth = b.Width; S.SettingsWindowHeight = b.Height;
            S.Save();
        };
        Closed += (_, _) =>
        {
            AppHost.SettingsChanged -= OnSettingsChanged;
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreference;
        };
    }

    public void Navigate(string section) => Go(section, push: true);

    void Go(string section, bool push)
    {
        if (Sections.All(s => s.Id != section)) return;
        if (push && _history[_historyPos] != section)
        {
            _history.RemoveRange(_historyPos + 1, _history.Count - _historyPos - 1);
            _history.Add(section);
            _historyPos = _history.Count - 1;
        }
        _current = section;
        if (_query.Length > 0) _searchBox.Text = ""; // TextChanged rebuilds the page
        else ShowPage();
        _scroll.ScrollToTop();
    }

    void OnUserPreference(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e) => Dispatcher.InvokeAsync(OnSettingsChanged);

    void OnSettingsChanged()
    {
        bool dark = AppHost.IsDarkTheme();
        if (dark != _dark)
        {
            _dark = dark;
            BuildShell();
            if (_hwnd != IntPtr.Zero) StyleFrame();
            return;
        }
        foreach (var r in _refreshers.ToArray()) r();
    }

    static void Changed() => AppHost.SaveSettings();
    void Reg(Action refresh) => _refreshers.Add(refresh);

    // ───────────────────────────── rows ─────────────────────────────

    void Add(string sec, string grp, string title, string sub, Func<FrameworkElement> make, bool wide = false) =>
        _rows.Add(new Row(sec, grp, title, sub, make, wide));

    void Tog(string sec, string grp, string title, string sub, Func<bool> get, Action<bool> set) =>
        Add(sec, grp, title, sub, () => Toggle(get, set));

    void Seg(string sec, string grp, string title, string sub, string[] labels, string[] values, Func<string> get, Action<string> set) =>
        Add(sec, grp, title, sub, () => Segmented(labels, values, get, set));

    void Pick(string sec, string grp, string title, string sub, (string Label, string Value)[] items, Func<string> get, Action<string> set) =>
        Add(sec, grp, title, sub, () => Menu(() => items, get, set));

    void Sld(string sec, string grp, string title, string sub, double min, double max, double step, Func<double> get, Action<double> set, Func<double, string> fmt) =>
        Add(sec, grp, title, sub, () => Slider(min, max, step, get, set, fmt));

    void DefineRows()
    {
        // Position & Docking
        Pick("Position", "Dock", "Screen Edge Dock Location", "Choose Top Edge (Mac Dynamic Island) or Left/Right (Edge-Drop Blade).",
            new[] { ("Top Edge (Dynamic Island)", "Top"), ("Bottom Edge (Above Taskbar)", "Bottom"), ("Left Edge (Side Blade)", "Left"),
                    ("Right Edge (Side Blade)", "Right"), ("Top-Left Corner", "TopLeft"), ("Top-Right Corner", "TopRight") },
            () => S.DockPosition, v => S.DockPosition = v);
        Seg("Position", "Dock", "Idle Appearance Style", "Thin Line takes 0 space; Capsule stays a floating pill; Notch merges into the bezel.",
            new[] { "Thin Line", "Capsule", "Notch" }, new[] { "ThinLine", "Floating", "Notch" }, () => S.DockMode, v => S.DockMode = v);
        Sld("Position", "Offsets", "Top Edge Offset", "Distance below top monitor bezel. 0px creates seamless bezel notch.",
            0, 60, 1, () => S.TopOffset, v => S.TopOffset = (int)v, v => $"{v:0} px");
        Sld("Position", "Offsets", "Side Vertical Center %", "Vertical placement along screen edge. 50% centers vertically.",
            5, 95, 1, () => S.SideOffsetPercent, v => S.SideOffsetPercent = (int)v, v => $"{v:0}%");
        Sld("Position", "Offsets", "Thin Line Thickness", "Hairline thickness when collapsed to thin strip (2px = ultra discreet, 8px = touch friendly).",
            2, 8, 1, () => S.ThinLineThickness, v => S.ThinLineThickness = (int)v, v => $"{v:0} px");
        Add("Position", "Visibility", "Toggle Island Visibility Hotkey", "Global shortcut to instantly show or hide the Dynamic Island from anywhere.", HotkeyBox);
        Tog("Position", "Visibility", "Auto-Hide in Fullscreen", "Hides when a full-screen app or video covers the island's display.",
            () => S.AutoHideInFullscreen, v => S.AutoHideInFullscreen = v);
        Tog("Position", "Visibility", "Game Mode", "Stays out of the way while you play: hides, holds popups and disables hover for any game Windows recognises — windowed or full screen — and during presentations.",
            () => S.GameMode, v => S.GameMode = v);
        Add("Position", "Visibility", "Focus Apps", "Other apps that should get the same treatment while they're in front (comma-separated, e.g. obs64, zoom).",
            () => Input(() => S.FocusApps, v => S.FocusApps = v, 240, "e.g. obs64, zoom", out _));
        Tog("Position", "Companion", "Virtual Island Companion (Pet)", "Adds a lively interactive desktop pet walking and sleeping on top of your island dock.",
            () => S.IslandPetEnabled, v => S.IslandPetEnabled = v);
        Seg("Position", "Companion", "Companion Type", "Choose which creature lives on your island.",
            new[] { "Cat", "Dog", "Bunny", "Ghost" }, new[] { "Cat", "Dog", "Bunny", "Ghost" }, () => S.IslandPetType, v => S.IslandPetType = v);

        // Monitors & Displays
        Add("Monitors", "", "Target Display Monitor", "Select which monitor the Dynamic Island should dock to.",
            () => Menu(MonitorItems, () => S.TargetDisplayId, v => S.TargetDisplayId = v));
        Tog("Monitors", "", "Follow Cursor Display", "Automatically moves the island to whichever monitor your mouse cursor is currently on.",
            () => S.FollowCursorDisplay, v => S.FollowCursorDisplay = v);
        Add("Monitors", "Detected Displays", null, null, DisplayList, wide: true);

        // Interaction & Physics
        Tog("Physics", "Behavior", "Stealth Auto-Hide When Idle", "Completely hide the island when idle. Reveal it smoothly by hovering your cursor near the screen edge.",
            () => S.AutoHideWhenIdle, v => S.AutoHideWhenIdle = v);
        Tog("Physics", "Behavior", "Auto-Collapse to Thin Line Strip", "When idle, smoothly shrinks down to an ultra-thin hairline strip.",
            () => S.AutoHideToEdge, v => S.AutoHideToEdge = v);
        Sld("Physics", "Behavior", "Idle Capsule Hides After", "How long the resting capsule stays after your mouse leaves before it shrinks to a line or hides (uses the two options above).",
            500, 30000, 500, () => S.IdleHideDelayMs, v => S.IdleHideDelayMs = (int)v, v => $"{v / 1000:0.#} s");
        Tog("Physics", "Behavior", "Expand on Click Only", "Prevents the island from expanding when your mouse hovers over it. Stays collapsed until you explicitly click on it.",
            () => S.ExpandOnClickOnly, v => S.ExpandOnClickOnly = v);
        Seg("Physics", "Behavior", "Click Trigger Mode", "Choose whether a single tap or a quick double-click expands the island.",
            new[] { "Single Click", "Double Click" }, new[] { "Single", "Double" }, () => S.ExpandClickMode, v => S.ExpandClickMode = v);
        Tog("Physics", "Behavior", "Tap Ctrl to See Through", "With the pointer on the island, tap Ctrl: it fades to a ghost and clicks reach whatever is behind it, until you move the pointer away.",
            () => S.CtrlSeeThrough, v => S.CtrlSeeThrough = v);
        Tog("Physics", "Behavior", "Keep Island Pinned Open", "Locks the island open after the mouse leaves. The pin button on the open island does the same.",
            () => S.IsPinned, v => S.IsPinned = v);
        Pick("Physics", "Behavior", "Pin Auto-Release", "How long a pin lasts once your mouse moves away, before the island closes by itself.",
            new[] { ("Never (stay pinned)", "0"), ("10 seconds", "10"), ("30 seconds", "30"), ("1 minute", "60"), ("5 minutes", "300") },
            () => S.PinTimeoutSeconds.ToString(CultureInfo.InvariantCulture), v => S.PinTimeoutSeconds = int.Parse(v, CultureInfo.InvariantCulture));
        Tog("Physics", "Popups", "Quiet Feed Mode", "Suppresses repetitive popups for consecutive media from the same app (e.g. YouTube Shorts).",
            () => S.QuietFeedMode, v => S.QuietFeedMode = v);
        Tog("Physics", "Popups", "Silent Dormant Mode", "Completely suppresses auto-flash popups on track change or battery charge.",
            () => S.SilentDormantMode, v => S.SilentDormantMode = v);
        Tog("Physics", "Popups", "Bluetooth Earbuds & Headset Banner", "Show Apple-style earbuds/headset connection banner with live battery ring and low-battery alert.",
            () => S.EnableHeadsetPopup, v => S.EnableHeadsetPopup = v);
        Tog("Physics", "Popups", "Timer Sound Notification", "Play sound when timer ends.", () => S.PlayTimerSound, v => S.PlayTimerSound = v);
        Tog("Physics", "Popups", "Launch on Windows Startup", "Start automatically when you log into Windows.", () => S.LaunchOnStartup, v => S.LaunchOnStartup = v);
        Sld("Physics", "Timing", "Hover Dwell Delay", "How long the cursor must linger at screen edge before island expands.",
            0, 1000, 10, () => S.DwellTimeMs, v => S.DwellTimeMs = (int)v, v => $"{v:0} ms");
        Sld("Physics", "Timing", "Stay Open After Mouse Leaves", "How long the island waits after your mouse leaves before it closes. Clicking anywhere else closes it right away.",
            0, 10000, 100, () => S.GraceTimeMs, v => S.GraceTimeMs = (int)v, v => v < 1000 ? $"{v:0} ms" : $"{v / 1000:0.#} s");
        Sld("Physics", "Timing", "Edge Hot-Zone Reach", "Active trigger height along screen border that detects summon gestures.",
            2, 40, 1, () => S.HotZoneThickness, v => S.HotZoneThickness = (int)v, v => $"{v:0} px");
        Seg("Physics", "Motion", "Animation Dynamics & Speed", "Silky spring physics profile for dimension transforms.",
            new[] { "Snappy", "Fast", "Balanced", "Gentle" }, new[] { "Snappy", "Fast", "Balanced", "Gentle" }, () => S.AnimationSpeed, v => S.AnimationSpeed = v);
        Seg("Physics", "Motion", "Expansion Motion", "Choose a distinct response for every hover and click expansion.",
            new[] { "Classic", "Fluid Spring", "Spring", "Bounce" }, new[] { "Classic", "FluidSpring", "Spring", "Bounce" }, () => S.AnimationStyle, v => S.AnimationStyle = v);
        Tog("Physics", "Motion", "Staggered Notch Motion", "Apple-style staggered expansion with cubic-bezier easing and delayed content reveal.",
            () => S.UseStaggeredNotchMotion, v => S.UseStaggeredNotchMotion = v);
        Tog("Physics", "Magic Shake", "Magic Shake Cursor Trigger", "Vigorously shake mouse cursor horizontally anywhere on screen to summon the island.",
            () => S.MagicShakeEnabled, v => S.MagicShakeEnabled = v);
        Seg("Physics", "Magic Shake", "Shake Sensitivity", "How aggressively you need to shake the mouse.",
            new[] { "Low", "Medium", "High" }, new[] { "Low", "Medium", "High" }, () => S.MagicShakeSensitivity, v => S.MagicShakeSensitivity = v);
        Tog("Physics", "Magic Shake", "Full Screen Exclusions", "Skip Magic Shake automatically while any fullscreen game or presentation is running.",
            () => S.MagicShakeExcludeFullscreen, v => S.MagicShakeExcludeFullscreen = v);

        // Appearance & Themes
        Seg("Appearance", "Theme", "Application Theme", "Select Dark, Light, or follow your Windows System Theme.",
            new[] { "System", "Dark", "Light" }, new[] { "System", "Dark", "Light" }, () => S.AppTheme, v => S.AppTheme = v);
        Add("Appearance", "Theme", "Apple Look", "One click back to the iPhone feel: plain scrubber, page dots, fluid spring motion, album-tinted waveform, true-black island.",
            () => Btn("Apply", () =>
            {
                S.SeekbarStyle = "Modern"; S.TabNavigationStyle = "Dots"; S.AnimationStyle = "FluidSpring"; S.AnimationSpeed = "Balanced";
                S.UseStaggeredNotchMotion = true; S.UseAlbumArtTint = true; S.IslandColor = "#000000";
                Changed();
            }, "primary"));
        Add("Appearance", "Colors", "Island Base Color", "Choose the background canvas color for the island capsule.", () => Swatches(
            new[] { ("OLED True Black", "#000000"), ("Slate Graphite", "#1C1C1E"), ("Deep Midnight Blue", "#0B1220"), ("Emerald Obsidian", "#04140E"), ("Cyber Plum", "#160B1F") },
            () => S.IslandColor, v => S.IslandColor = v));
        Add("Appearance", "Colors", "Accent Glow Color", "Color used for active playback glows, slider tracks, and highlights.", () => Swatches(
            new[] { ("Mint Glow", "#70E0C0"), ("Cyber Cyan", "#38BDF8"), ("Electric Blue", "#0A84FF"), ("Rose Pink", "#EC4899"), ("Sunset Amber", "#FF9F0A"), ("Crisp White", "#F5F5F7") },
            () => S.AccentColor, v => S.AccentColor = v));
        Tog("Appearance", "Media", "Live Audio Waveform Visualizer", "Renders real-time WASAPI audio frequency bars during music and video playback.",
            () => S.ShowWaveformVisualizer, v => S.ShowWaveformVisualizer = v);
        Tog("Appearance", "Media", "Album Art Tint", "Tint the waveform and progress with the album artwork color, just like iPhone.",
            () => S.UseAlbumArtTint, v => S.UseAlbumArtTint = v);
        Tog("Appearance", "Media", "Ambient Album Art Background", "Extend album artwork colors into a soft, blurred glow across the island.",
            () => S.EnableMediaBackdrop, v => S.EnableMediaBackdrop = v);
        Seg("Appearance", "Media", "Seekbar Style", "Choose modern scrubber bar, thin line, or dynamic wave seekbar style.",
            new[] { "Modern", "Thin", "Wave" }, new[] { "Modern", "Thin", "Soundwave" }, () => S.SeekbarStyle, v => S.SeekbarStyle = v);

        // Island Tabs
        Tog("Tabs", "Modules", "Home Start Page", "Opens first when nothing is playing: time, date, battery, weather and a one-click resume. Music still opens the player.",
            () => S.TabHomeEnabled, v => S.TabHomeEnabled = v);
        Tog("Tabs", "Modules", "Universal Media Player Tab", "Controls Spotify, YouTube, Apple Music, browsers, and Windows media sessions.",
            () => S.TabMediaEnabled, v => S.TabMediaEnabled = v);
        Tog("Tabs", "Modules", "Timer & Stopwatch Tab", "Quick access count-down timers and millisecond-accurate stopwatch with lap tracking.",
            () => S.TabTimerEnabled, v => S.TabTimerEnabled = v);
        Tog("Tabs", "Modules", "Clipboard Manager Shelf Tab", "Visual shelf with live search, image thumbnails, link cards, and pin management.",
            () => S.TabClipboardEnabled, v => S.TabClipboardEnabled = v);
        Tog("Tabs", "Modules", "Quick Launch & Mini Apps Tab", "Fast access slots for your favorite applications and system utilities.",
            () => S.TabQuickLaunchEnabled, v => S.TabQuickLaunchEnabled = v);
        Tog("Tabs", "Modules", "Quick Notes Tab", "A persistent scratchpad that lives inside the island.",
            () => S.TabNotesEnabled, v => S.TabNotesEnabled = v);
        Tog("Tabs", "Modules", "Tasks Tab", "A Reminders-style to-do list: type a task and press Enter, tick the circle when it's done.",
            () => S.TabTasksEnabled, v => S.TabTasksEnabled = v);
        Seg("Tabs", "Modules", "Tab Navigation Style", "Choose between top header icon switcher, bottom indicator dots, or disable tab switchers.",
            new[] { "None", "Header", "Dots" }, new[] { "None", "Header", "Dots" }, () => S.TabNavigationStyle, v => S.TabNavigationStyle = v);
        Tog("Tabs", "Live Lyrics & Radar", "Show Lyrics in Compact Island", "Show active live lyric line in the compact idle capsule instead of track title.",
            () => S.ShowLyricsInCollapsed, v => S.ShowLyricsInCollapsed = v);
        Tog("Tabs", "Live Lyrics & Radar", "Show Lyrics in Expanded Island", "Show rolling 3-line synced lyrics alongside media controls and audio scrubber.",
            () => S.ShowLyricsInExpanded, v => S.ShowLyricsInExpanded = v);
        Tog("Tabs", "Live Lyrics & Radar", "Music Radar & Audio Finder", "Instant 1-click song identification for desktop audio, YouTube & earbuds with direct Spotify & YouTube links.",
            () => S.EnableMusicRadar, v => S.EnableMusicRadar = v);

        // Widgets & Apps
        Pick("Widgets", "Idle Capsule", "Idle Capsule Widget Display", "Choose what information is displayed inside the compact pill when idle.",
            new[] { ("Live Clock & Date", "Clock"), ("Battery & Power Status", "Battery"), ("Current Track Info", "TrackInfo"), ("System Stats (CPU / RAM)", "SystemStats"),
                    ("Minimal Activity Dot", "MinimalDot"), ("Animated Island Eyes", "AnimatedEyes"), ("Solid White Eyeballs", "WhiteEyeballs"), ("Weather", "Weather"),
                    ("Analog Clock", "AnalogClock"), ("Calendar Date & Time", "Calendar"), ("Activity Rings (CPU / RAM)", "ActivityRings"),
                    ("Network Speed", "Network"), ("Next Task", "NextTask"), ("Glow Orb (follows cursor)", "Orb") },
            () => S.IdleWidgetMode, v => S.IdleWidgetMode = v);
        Seg("Widgets", "Idle Capsule", "Clock Time Format", "Toggle between 12-Hour format (with AM/PM) and 24-Hour military time.",
            new[] { "12-Hour", "24-Hour" }, new[] { "12", "24" }, () => S.Use12HourClock ? "12" : "24", v => S.Use12HourClock = v == "12");
        Seg("Widgets", "Idle Capsule", "Idle Clock Style", "Classic single-line pill, Bold Notch stacked hero digits, or Nothing OS LED dot matrix.",
            new[] { "Classic", "Bold", "Dot Matrix" }, new[] { "Classic", "Bold", "DotMatrix" }, () => S.IdleClockStyle, v => S.IdleClockStyle = v);
        Add("Widgets", "Drop Zone", "Drop Zone Target Folder", "Folder where files dragged and dropped into the island are saved.", DropFolder);
        Add("Widgets", "Quick Launch", "Apps & Folders Quick Shelf", "Assign apps, tools, folders and links to the island. The first three can also appear in the media player.", QuickApps, wide: true);

        // Clipboard
        Tog("Clipboard", "History", "Enable Clipboard Shelf", "Automatically captures copied text, links, and pictures into your shelf.",
            () => S.ClipboardEnabled, v => S.ClipboardEnabled = v);
        Seg("Clipboard", "History", "History Retention Duration", "Automatically purges old unpinned clips after the selected time window.",
            new[] { "24 Hours", "3 Days", "7 Days", "Forever" }, new[] { "24Hours", "3Days", "7Days", "Permanent" }, () => S.ClipboardRetention, v => S.ClipboardRetention = v);
        Pick("Clipboard", "History", "Maximum Saved Clips", "Upper limit of saved history entries before oldest clips are trimmed.",
            new[] { 50, 100, 200, 500, 1000, 2000 }.Select(n => ($"{n:N0} clips", n.ToString(CultureInfo.InvariantCulture))).ToArray(),
            () => S.ClipboardMaxItems.ToString(CultureInfo.InvariantCulture), v => S.ClipboardMaxItems = int.Parse(v, CultureInfo.InvariantCulture));
        Add("Clipboard", "History", "Privacy Exclusions", "Comma-separated app names to exclude from clipboard capture (e.g., password managers).",
            () => Input(() => S.ClipboardPrivacyExclusions, v => S.ClipboardPrivacyExclusions = v, 240, "e.g. 1Password, KeePass", out _));
        Add("Clipboard", "Data", "Clear All Clipboard History", "Permanently deletes all unpinned items currently saved in your history shelf.", ClearHistory);
        Add("Clipboard", "Data", "Export Clipboard History", "Save every clip (text and images) to a folder of your choice.", ExportClips);

        // Text & Font Size
        Sld("TextSize", "Scale", "Island Text Scale Multiplier", "Adjust text size proportionally across all cards, titles, sliders, and badges.",
            0.85, 1.3, 0.05, () => S.IslandTextScale, v => S.IslandTextScale = v, v => $"{v * 100:0}%");
        Seg("TextSize", "Scale", "Scale Presets", "Compact, default, large and extra-large island sizes.",
            new[] { "85%", "100%", "115%", "130%" }, new[] { "0.85", "1", "1.15", "1.3" },
            () => new[] { "0.85", "1", "1.15", "1.3" }.FirstOrDefault(p => Math.Abs(double.Parse(p, CultureInfo.InvariantCulture) - S.IslandTextScale) < 0.001) ?? "",
            v => S.IslandTextScale = double.Parse(v, CultureInfo.InvariantCulture));
        Add("TextSize", "Preview", "Live Island Preview", "Interactive mockup displaying real-time text scaling on island components.", Preview, wide: true);
        Add("TextSize", "Preview", "Reset Scale", "Restore standard 100% factory text and UI scale.",
            () => Btn("Reset to Default (100%)", () => { S.IslandTextScale = 1; Changed(); }));

        // Extras
        Tog("Extras", "Live Activities", "Volume Change Popup", "Show an iPhone-style volume HUD inside the island when the system volume changes.",
            () => S.EnableVolumePopup, v => S.EnableVolumePopup = v);
        Tog("Extras", "Live Activities", "Caps Lock & Num Lock Popup", "Briefly show lock-key changes in the island, like the iPhone silent switch.",
            () => S.EnableLockKeysPopup, v => S.EnableLockKeysPopup = v);
        Tog("Extras", "Live Activities", "Mic & Camera Privacy Indicator", "Show an orange or green dot while an app is using your microphone or camera.",
            () => S.EnablePrivacyIndicator, v => S.EnablePrivacyIndicator = v);
        Tog("Extras", "Live Activities", "Battery & Charging Popups", "Show charging, unplugged and low-battery live activities.",
            () => S.EnableBatteryPopups, v => S.EnableBatteryPopups = v);
        Tog("Extras", "Live Activities", "Track & Play/Pause Popups", "Briefly expand the island when a new song starts, or when music pauses or resumes.",
            () => S.EnableTrackPopups, v => S.EnableTrackPopups = v);
        Tog("Extras", "Widgets", "Network Speed in System Stats", "Add live download and upload speed to the System Stats widget.",
            () => S.ShowNetworkSpeed, v => S.ShowNetworkSpeed = v);
        Add("Extras", "Widgets", "Weather City", "City for the Weather widget. Leave empty to detect your location automatically.",
            () => Input(() => S.WeatherCity, v => S.WeatherCity = v, 180, "Automatic", out _));
        Tog("Extras", "Widgets", "Use Fahrenheit", "Show temperatures in °F instead of °C.", () => S.WeatherFahrenheit, v => S.WeatherFahrenheit = v);
        Sld("Extras", "Pomodoro", "Focus Length", "Length of each Pomodoro focus session.",
            5, 90, 1, () => S.PomodoroFocusMinutes, v => S.PomodoroFocusMinutes = (int)v, v => $"{v:0} min");
        Sld("Extras", "Pomodoro", "Break Length", "Length of the break between focus sessions.",
            1, 30, 1, () => S.PomodoroBreakMinutes, v => S.PomodoroBreakMinutes = (int)v, v => $"{v:0} min");

        // About
        Add("About", "", null, null, Hero, wide: true);
        Add("About", "", "Replay Welcome Tour", "Walk through the island's gestures and features again.",
            () => Btn("Replay Tour", () => AppHost.Island?.ReplayTour(), "primary"));
        foreach (var (t, d) in new[]
        {
            ("Universal Media & Live Scrub", "Real-time seekbar progress, SMTC session cycling, album-art tinted waveform and media controls."),
            ("Master Volume Flyout", "Control Center volume slider, mute toggle, and mouse wheel scrubbing."),
            ("Dual Docking Modes", "Top notch or floating island, corner docking, or left & right screen edge blade."),
            ("Clipboard Manager Shelf", "Live search, image thumbnails, link cards, pins, OCR text extraction and Google Lens."),
            ("Timers, Stopwatch & Pomodoro", "Countdown presets with a live progress line, lap-tracking stopwatch and focus cycles."),
            ("Live Lyrics & Music Radar", "Synced lyrics in the capsule and one-click song identification."),
            ("Island Companions", "Animated eyes and a virtual pet that live on your island."),
            ("Spring Physics Engine", "Sub-pixel fluid elasticity with Apple-style staggered morphing."),
        })
            Add("About", "Core Capabilities", t, d, null);
        foreach (var (tag, d) in new[]
        {
            ("Hover Edge", "Expands the Dynamic Island smoothly with spring physics"),
            ("Click Pin", "Locks the island open permanently until clicked again"),
            ("Click Art", "Brings the active media player (Spotify, Chrome, Edge, VLC) to front"),
            ("Click Badge", "Cycles between all active media playback sessions on Windows"),
            ("Scroll Volume", "Hover over the speaker icon and roll mouse wheel to adjust master volume"),
            ("Scrub Bar", "Drag or click the seek slider to jump to any position in the song"),
            ("Wheel / Swipe", "Roll the mouse wheel or swipe two fingers on the island to cycle tabs"),
            ("Magic Shake", "Shake the cursor horizontally back and forth to summon or hide the island"),
        })
            Add("About", "Interaction & Gesture Shortcuts", tag, d, null);
        Add("About", "Interaction & Gesture Shortcuts", "Visibility Hotkey", "Instantly show or hide the island from anywhere",
            () => { var t = T("", 12, _label, FontWeights.SemiBold); void A() => t.Text = Pretty(S.ToggleVisibilityHotkey); A(); Reg(A); return Pill(t); });
        Add("About", "Data", "Data Folder", AppHost.DataDir, () => Btn("Open", () => Reveal(AppHost.DataDir)));
    }

    // ───────────────────────────── shell & pages ─────────────────────────────

    void ApplyPalette()
    {
        if (_dark)
        {
            _win = B("#1E1E1E"); _side = B("#262626"); _card = B("#2C2C2E"); _cardBorder = B("#0FFFFFFF"); _sep = B("#3A3A3C");
            _label = B("#FFFFFF"); _sec = B("#99EBEBF5"); _accent = B("#0A84FF"); _btn = B("#4A4A4E"); _btnBorder = B("#00000000");
            _segTrack = B("#1C1C1E"); _segSel = B("#5A5A5E"); _inputBg = B("#1C1C1E"); _inputBorder = B("#48484A");
            _menuBg = B("#F2323234"); _hover = B("#14FFFFFF"); _red = B("#FF453A");
            _green = Hex("#30D158"); _toggleOff = Hex("#39393D");
        }
        else
        {
            _win = B("#F5F5F7"); _side = B("#EBEBEB"); _card = B("#FFFFFF"); _cardBorder = B("#E5E5EA"); _sep = B("#E5E5EA");
            _label = B("#000000"); _sec = B("#993C3C43"); _accent = B("#007AFF"); _btn = B("#FFFFFF"); _btnBorder = B("#C8C8CC");
            _segTrack = B("#E3E3E8"); _segSel = B("#FFFFFF"); _inputBg = B("#FFFFFF"); _inputBorder = B("#D1D1D6");
            _menuBg = B("#F7F6F6F6"); _hover = B("#0F000000"); _red = B("#FF3B30");
            _green = Hex("#34C759"); _toggleOff = Hex("#E9E9EA");
        }
    }

    void BuildShell()
    {
        ApplyPalette();
        Background = _win;
        Foreground = _label;
        _nav.Clear();

        var root = new Grid { Background = _win };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        root.ColumnDefinitions.Add(new ColumnDefinition());

        var search = Input(() => _query, _ => { }, double.NaN, "Search", out _searchBox, "\uE721");
        search.Margin = new Thickness(12, 0, 12, 10);
        _searchBox.TextChanged += (_, _) => { _query = _searchBox.Text.Trim(); ShowPage(); };
        _searchBox.KeyDown += (_, e) => { if (e.Key == Key.Escape) _searchBox.Text = ""; };

        var navList = new StackPanel { Margin = new Thickness(10, 0, 10, 12) };
        foreach (var s in Sections)
        {
            var tile = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(6), Background = B(s.Tile), Child = Glyph(s.Glyph, 12, Brushes.White) };
            var label = T(s.Title, 13, _label);
            label.Margin = new Thickness(9, 0, 0, 0);
            var item = new Border
            {
                CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 5, 6, 5), Margin = new Thickness(0, 1, 0, 1),
                Background = Brushes.Transparent, Cursor = Cursors.Hand, Focusable = true, FocusVisualStyle = null,
                Child = HStack(tile, label),
            };
            AutomationProperties.SetName(item, s.Title);
            string id = s.Id;
            item.MouseEnter += (_, _) => UpdateSidebar();
            item.MouseLeave += (_, _) => UpdateSidebar();
            item.MouseLeftButtonUp += (_, e) => { Navigate(id); e.Handled = true; };
            item.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) Navigate(id); };
            _nav[id] = (item, label);
            navList.Children.Add(item);
        }

        var side = new DockPanel { Background = _side };
        var lights = TrafficLights();
        DockPanel.SetDock(lights, Dock.Top);
        side.Children.Add(lights);
        DockPanel.SetDock(search, Dock.Top);
        side.Children.Add(search);
        side.Children.Add(new ScrollViewer { Content = navList, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Focusable = false });
        var sideBorder = new Border { Child = side, BorderBrush = _sep, BorderThickness = new Thickness(0, 0, 1, 0) };
        root.Children.Add(sideBorder);

        _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        var back = IconBtn("\uE76B", "Back", () => { if (_historyPos > 0) Go(_history[--_historyPos], false); });
        var fwd = IconBtn("\uE76C", "Forward", () => { if (_historyPos < _history.Count - 1) Go(_history[++_historyPos], false); });
        WindowChrome.SetIsHitTestVisibleInChrome(back, true);
        WindowChrome.SetIsHitTestVisibleInChrome(fwd, true);
        _toolbarTitle = T("", 15, _label, FontWeights.Bold);
        _toolbarTitle.Margin = new Thickness(10, 0, 0, 1);
        var toolbar = HStack(back, fwd, _toolbarTitle);
        toolbar.Margin = new Thickness(14, 0, 0, 0);
        var toolbarHost = new Border { Height = 52, Child = toolbar, Background = _win };
        var main = new DockPanel();
        DockPanel.SetDock(toolbarHost, Dock.Top);
        main.Children.Add(toolbarHost);
        main.Children.Add(_scroll);
        Grid.SetColumn(main, 1);
        root.Children.Add(main);

        Content = root;
        if (_query.Length > 0) _searchBox.Text = _query; else ShowPage();
    }

    void ShowPage(bool keepScroll = false)
    {
        double offset = _scroll.VerticalOffset;
        _refreshers.Clear();
        var content = new StackPanel { Margin = new Thickness(28, 2, 28, 30), MaxWidth = 760 };
        if (_query.Length > 0) BuildSearch(content, _query);
        else BuildSection(content, _current);
        _scroll.Content = content;
        if (keepScroll) Dispatcher.InvokeAsync(() => _scroll.ScrollToVerticalOffset(offset), System.Windows.Threading.DispatcherPriority.Loaded);
        UpdateSidebar();
    }

    void UpdateSidebar()
    {
        foreach (var (id, (item, label)) in _nav)
        {
            bool sel = id == _current && _query.Length == 0;
            item.Background = sel ? _accent : item.IsMouseOver ? _hover : Brushes.Transparent;
            label.Foreground = sel ? Brushes.White : _label;
        }
    }

    void BuildSection(StackPanel content, string id)
    {
        var sec = Sections.First(s => s.Id == id);
        _toolbarTitle.Text = sec.Title;
        // macOS Sonoma pane header: icon, title and description in a card
        var tile = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(11), Background = B(sec.Tile), Child = Glyph(sec.Glyph, 22, Brushes.White), VerticalAlignment = VerticalAlignment.Top };
        var sub = T(sec.Sub, 12, _sec);
        sub.TextWrapping = TextWrapping.Wrap;
        sub.Margin = new Thickness(0, 3, 0, 0);
        var text = VStack(T(sec.Title, 15, _label, FontWeights.Bold), sub);
        text.Margin = new Thickness(14, 0, 0, 0);
        var hero = new Grid();
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hero.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(text, 1);
        hero.Children.Add(tile);
        hero.Children.Add(text);
        content.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = _card, BorderBrush = _cardBorder, BorderThickness = new Thickness(1), Padding = new Thickness(14, 12, 14, 12), Child = hero });
        foreach (var grp in _rows.Where(r => r.Section == id).GroupBy(r => r.Group))
            AddCard(content, grp.Key, grp);
    }

    void BuildSearch(StackPanel content, string q)
    {
        bool Hit(string s) => s != null && s.Contains(q, StringComparison.OrdinalIgnoreCase);
        var hits = _rows.Where(r => r.Title != null && (Hit(r.Title) || Hit(r.Sub))).ToList();
        _toolbarTitle.Text = "Search";
        var sub = T(hits.Count == 0 ? $"No settings found for \"{q}\". Try searching for toggle, hotkey, lyrics, theme, sound, or monitor."
                  : $"{hits.Count} setting{(hits.Count == 1 ? "" : "s")} for \"{q}\". Click a section name to navigate directly to it.", 13, _sec);
        sub.TextWrapping = TextWrapping.Wrap;
        sub.Margin = new Thickness(0, 8, 0, 6);
        content.Children.Add(sub);
        foreach (var grp in hits.GroupBy(r => r.Section))
        {
            var sec = Sections.First(s => s.Id == grp.Key);
            var link = T(sec.Title + "  ›", 13, _accent, FontWeights.SemiBold);
            link.Cursor = Cursors.Hand;
            link.Margin = new Thickness(4, 18, 0, 6);
            link.MouseLeftButtonUp += (_, _) => Navigate(sec.Id);
            content.Children.Add(link);
            AddCard(content, null, grp);
        }
    }

    void AddCard(StackPanel content, string header, IEnumerable<Row> rows)
    {
        if (!string.IsNullOrEmpty(header))
        {
            var h = T(header, 13, _label, FontWeights.SemiBold);
            h.Margin = new Thickness(4, 18, 0, 6);
            content.Children.Add(h);
        }
        else content.Children.Add(new Border { Height = 12 });
        var stack = new StackPanel();
        foreach (var r in rows)
        {
            if (stack.Children.Count > 0) stack.Children.Add(new Border { Height = 1, Background = _sep, Margin = new Thickness(14, 0, 0, 0) });
            stack.Children.Add(BuildRow(r));
        }
        content.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = _card, BorderBrush = _cardBorder, BorderThickness = new Thickness(1), Child = stack });
    }

    FrameworkElement BuildRow(Row r)
    {
        var ctl = r.Make?.Invoke();
        if (ctl != null && r.Title != null) AutomationProperties.SetName(ctl, r.Title);
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        if (r.Title != null) left.Children.Add(T(r.Title, 13, _label));
        if (!string.IsNullOrEmpty(r.Sub))
        {
            var s = T(r.Sub, 11.5, _sec);
            s.TextWrapping = TextWrapping.Wrap;
            s.Margin = new Thickness(0, 2, 0, 0);
            left.Children.Add(s);
        }
        if (r.Wide)
        {
            var v = new StackPanel { Margin = new Thickness(14, 10, 14, 12) };
            if (r.Title != null) v.Children.Add(left);
            if (ctl != null) { ctl.Margin = new Thickness(0, r.Title != null ? 10 : 0, 0, 0); v.Children.Add(ctl); }
            return v;
        }
        var g = new Grid { Margin = new Thickness(14, 9, 14, 9), MinHeight = 28 };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(left);
        if (ctl != null)
        {
            ctl.Margin = new Thickness(16, 0, 0, 0);
            ctl.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(ctl, 1);
            g.Children.Add(ctl);
        }
        return g;
    }

    // ───────────────────────────── Apple controls ─────────────────────────────

    FrameworkElement Toggle(Func<bool> get, Action<bool> set)
    {
        var tt = new TranslateTransform();
        var knob = new Ellipse
        {
            Width = 20, Height = 20, Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0), RenderTransform = tt, Effect = Shadow(3, 0.3),
        };
        var brush = new SolidColorBrush();
        var track = new Border
        {
            Width = 40, Height = 24, CornerRadius = new CornerRadius(12), Background = brush, Child = knob,
            Cursor = Cursors.Hand, Focusable = true, FocusVisualStyle = null, HorizontalAlignment = HorizontalAlignment.Right,
        };
        bool? shown = null;
        void Apply()
        {
            bool on = get();
            var c = on ? ((SolidColorBrush)_accent).Color : _toggleOff;
            double x = on ? 16 : 0;
            if (shown == null) { brush.Color = c; tt.X = x; }
            else if (shown != on)
            {
                brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(c, Dur(180)));
                tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, Dur(240)) { EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut } });
            }
            shown = on;
        }
        void Flip() { set(!get()); Changed(); Apply(); }
        track.MouseLeftButtonUp += (_, e) => { Flip(); e.Handled = true; };
        track.KeyDown += (_, e) => { if (e.Key is Key.Space or Key.Enter) { Flip(); e.Handled = true; } };
        Apply();
        Reg(Apply);
        return track;
    }

    FrameworkElement Segmented(string[] labels, string[] values, Func<string> get, Action<string> set)
    {
        int n = labels.Length;
        var grid = new Grid();
        var tt = new TranslateTransform();
        var pill = new Border { CornerRadius = new CornerRadius(5), Background = _segSel, RenderTransform = tt, HorizontalAlignment = HorizontalAlignment.Left, Effect = _dark ? null : Shadow(3, 0.18) };
        Grid.SetColumnSpan(pill, n);
        grid.Children.Add(pill);
        var texts = new TextBlock[n];
        double maxW = 0, segW = 0;
        int? shown = null;
        for (int i = 0; i < n; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var t = new TextBlock { Text = labels[i], FontFamily = UiFont, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = _label, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 0, 3) };
            t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            maxW = Math.Max(maxW, t.DesiredSize.Width);
            var cell = new Border { Background = Brushes.Transparent, Child = t, Cursor = Cursors.Hand };
            Grid.SetColumn(cell, i);
            int idx = i;
            cell.MouseLeftButtonUp += (_, e) => { Select(idx); e.Handled = true; };
            grid.Children.Add(cell);
            texts[i] = t;
        }
        segW = Math.Ceiling(maxW + 22);
        grid.Width = segW * n;
        pill.Width = segW;
        void Select(int idx) { if (values[idx] == get()) return; set(values[idx]); Changed(); Apply(); }
        void Apply()
        {
            int idx = Array.IndexOf(values, get());
            pill.Visibility = idx < 0 ? Visibility.Hidden : Visibility.Visible;
            for (int i = 0; i < n; i++) texts[i].FontWeight = i == idx ? FontWeights.SemiBold : FontWeights.Normal;
            if (idx >= 0 && shown != idx)
            {
                double x = idx * segW;
                if (shown is null or < 0) { tt.BeginAnimation(TranslateTransform.XProperty, null); tt.X = x; }
                else tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, Dur(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
            shown = idx;
        }
        var track = new Border { CornerRadius = new CornerRadius(7), Background = _segTrack, Padding = new Thickness(2), Child = grid, HorizontalAlignment = HorizontalAlignment.Right, Focusable = true, FocusVisualStyle = null };
        track.KeyDown += (_, e) =>
        {
            int idx = Array.IndexOf(values, get());
            if (e.Key == Key.Left && idx > 0) { Select(idx - 1); e.Handled = true; }
            if (e.Key == Key.Right && idx < n - 1) { Select(idx + 1); e.Handled = true; }
        };
        Apply();
        Reg(Apply);
        return track;
    }

    FrameworkElement Menu(Func<IEnumerable<(string Label, string Value)>> items, Func<string> get, Action<string> set)
    {
        var text = T("", 13, _label);
        var btn = new Border
        {
            CornerRadius = new CornerRadius(6), Background = _btn, BorderBrush = _btnBorder, BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 3, 6, 3), Cursor = Cursors.Hand, Focusable = true, FocusVisualStyle = null,
            HorizontalAlignment = HorizontalAlignment.Right, Effect = _dark ? null : Shadow(2, 0.08),
        };
        var chevron = new Border // macOS pop-up button: blue box with up/down chevrons
        {
            Width = 16, Height = 16, CornerRadius = new CornerRadius(4), Background = _accent, Margin = new Thickness(10, 0, -4, 0),
            Child = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M1,3.5 L4,0.8 L7,3.5 M1,6.5 L4,9.2 L7,6.5"), Stroke = Brushes.White, StrokeThickness = 1.4,
                StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Width = 8, Height = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        btn.Child = HStack(text, chevron);
        void Apply()
        {
            string v = get();
            text.Text = items().Where(i => i.Value == v).Select(i => i.Label).FirstOrDefault() ?? v;
        }
        void Open() => ShowMenu(btn, items().Select(i => (i.Label, i.Value == get(), (Action)(() => { set(i.Value); Changed(); Apply(); }))));
        btn.MouseLeftButtonUp += (_, e) => { Open(); e.Handled = true; };
        btn.KeyDown += (_, e) => { if (e.Key is Key.Space or Key.Enter or Key.Down) { Open(); e.Handled = true; } };
        Apply();
        Reg(Apply);
        return btn;
    }

    void ShowMenu(FrameworkElement target, IEnumerable<(string Label, bool Checked, Action Click)> items)
    {
        var popup = new Popup { PlacementTarget = target, Placement = PlacementMode.Bottom, AllowsTransparency = true, StaysOpen = false, PopupAnimation = PopupAnimation.Fade, VerticalOffset = -8, HorizontalOffset = -12 };
        var stack = new StackPanel();
        foreach (var (label, check, click) in items)
        {
            var mark = Glyph(check ? "" : "", 11, null);
            mark.Width = 18;
            var t = T(label, 13, null);
            var row = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 4, 14, 4), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = HStack(mark, t) };
            row.SetValue(TextElement.ForegroundProperty, _label);
            row.MouseEnter += (_, _) => { row.Background = _accent; row.SetValue(TextElement.ForegroundProperty, Brushes.White); };
            row.MouseLeave += (_, _) => { row.Background = Brushes.Transparent; row.SetValue(TextElement.ForegroundProperty, _label); };
            row.MouseLeftButtonUp += (_, e) => { popup.IsOpen = false; click(); e.Handled = true; };
            stack.Children.Add(row);
        }
        popup.Child = new Border
        {
            CornerRadius = new CornerRadius(8), Background = _menuBg, BorderBrush = _dark ? B("#1FFFFFFF") : B("#1F000000"), BorderThickness = new Thickness(1),
            Padding = new Thickness(5), Margin = new Thickness(12), MinWidth = target.ActualWidth, Effect = Shadow(18, 0.35, 4),
            Child = new ScrollViewer { Content = stack, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        };
        popup.IsOpen = true;
    }

    FrameworkElement Slider(double min, double max, double step, Func<double> get, Action<double> set, Func<double, string> fmt)
    {
        var label = T("", 12, _sec);
        label.Width = 58;
        label.TextAlignment = TextAlignment.Right;
        var track = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = _dark ? B("#48484A") : B("#D1D1D6"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 0, 9, 0) };
        var fill = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = _accent, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 0, 0, 0) };
        var knob = new Ellipse { Width = 18, Height = 18, Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Effect = Shadow(3, 0.35), Stroke = _dark ? null : B("#1A000000") };
        var host = new Grid { Width = 190, Height = 24, Background = Brushes.Transparent, Cursor = Cursors.Hand, Focusable = true, FocusVisualStyle = null };
        host.Children.Add(track);
        host.Children.Add(fill);
        host.Children.Add(knob);
        double v = get();
        void Layout()
        {
            double w = Math.Max(0, host.ActualWidth - 18), t = (v - min) / (max - min);
            knob.Margin = new Thickness(t * w, 0, 0, 0);
            fill.Width = t * w;
            label.Text = fmt(v);
        }
        void SetValue(double nv)
        {
            nv = Math.Round(Math.Clamp(min + Math.Round((nv - min) / step) * step, min, max), 4);
            if (Math.Abs(nv - v) < 1e-9) return;
            v = nv;
            set(nv);
            Changed();
            Layout();
        }
        void FromX(double x) => SetValue(min + Math.Clamp((x - 9) / Math.Max(1, host.ActualWidth - 18), 0, 1) * (max - min));
        host.MouseLeftButtonDown += (_, e) => { host.CaptureMouse(); host.Focus(); FromX(e.GetPosition(host).X); e.Handled = true; };
        host.MouseMove += (_, e) => { if (host.IsMouseCaptured) FromX(e.GetPosition(host).X); };
        host.MouseLeftButtonUp += (_, _) => host.ReleaseMouseCapture();
        host.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Left or Key.Down) { SetValue(v - step); e.Handled = true; }
            if (e.Key is Key.Right or Key.Up) { SetValue(v + step); e.Handled = true; }
        };
        host.SizeChanged += (_, _) => Layout();
        Reg(() => { v = get(); Layout(); });
        Layout();
        return HStack(host, label);
    }

    FrameworkElement Swatches((string Name, string Hex)[] items, Func<string> get, Action<string> set)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var checks = new List<(TextBlock Mark, string Hex)>();
        foreach (var (name, hex) in items)
        {
            var c = Hex(hex);
            var mark = Glyph("", 11, (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255 > 0.6 ? Brushes.Black : Brushes.White);
            mark.HorizontalAlignment = HorizontalAlignment.Center;
            var dot = new Grid { Width = 24, Height = 24, Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand, ToolTip = name, Background = Brushes.Transparent };
            dot.Children.Add(new Ellipse { Fill = new SolidColorBrush(c), Stroke = _dark ? B("#40FFFFFF") : B("#26000000"), StrokeThickness = 1 });
            dot.Children.Add(mark);
            AutomationProperties.SetName(dot, name);
            dot.MouseLeftButtonUp += (_, e) => { set(hex); Changed(); e.Handled = true; };
            checks.Add((mark, hex));
            panel.Children.Add(dot);
        }
        var input = Input(get, s =>
        {
            s = s.Trim();
            if (s.Length > 0 && s[0] != '#') s = "#" + s;
            try { ColorConverter.ConvertFromString(s); set(s.ToUpperInvariant()); } catch { /* invalid hex: refresh restores the old value */ }
        }, 84, "#RRGGBB", out _);
        panel.Children.Add(input);
        void Apply() { foreach (var (m, h) in checks) m.Visibility = string.Equals(h, get(), StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Hidden; }
        Apply();
        Reg(Apply);
        return panel;
    }

    Border Input(Func<string> get, Action<string> commit, double width, string placeholder, out TextBox box, string glyph = null)
    {
        var tb = new TextBox
        {
            Text = get(), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = _label, CaretBrush = _label,
            SelectionBrush = _accent, FontSize = 13, Padding = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center, FocusVisualStyle = null,
        };
        box = tb;
        var ph = T(placeholder ?? "", 13, _sec);
        ph.IsHitTestVisible = false;
        void Ph() => ph.Visibility = tb.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        tb.TextChanged += (_, _) => Ph();
        Ph();
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        if (glyph != null) { var g = Glyph(glyph, 12, _sec); g.Margin = new Thickness(0, 0, 6, 0); grid.Children.Add(g); }
        Grid.SetColumn(tb, 1);
        Grid.SetColumn(ph, 1);
        grid.Children.Add(tb);
        grid.Children.Add(ph);
        var border = new Border
        {
            CornerRadius = new CornerRadius(6), Background = _inputBg, BorderBrush = _inputBorder, BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 4, 8, 4), Child = grid, Cursor = Cursors.IBeam,
        };
        if (!double.IsNaN(width)) { border.Width = width; border.HorizontalAlignment = HorizontalAlignment.Right; }
        void Commit() { if (tb.Text != get()) { commit(tb.Text); Changed(); } }
        tb.GotKeyboardFocus += (_, _) => border.BorderBrush = _accent;
        tb.LostKeyboardFocus += (_, _) => { border.BorderBrush = _inputBorder; Commit(); };
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        border.MouseLeftButtonDown += (_, _) => tb.Focus();
        Reg(() => { if (!tb.IsKeyboardFocused && tb.Text != get()) tb.Text = get(); });
        return border;
    }

    Border Btn(string text, Action click, string kind = "normal")
    {
        bool filled = kind is "primary" or "destructive";
        var t = T(text, 13, filled ? Brushes.White : _label, FontWeights.Medium);
        var b = new Border
        {
            CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 4, 12, 4), Child = t, Cursor = Cursors.Hand, Focusable = true, FocusVisualStyle = null,
            Background = kind == "primary" ? _accent : kind == "destructive" ? _red : _btn,
            BorderBrush = filled ? Brushes.Transparent : _btnBorder, BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right, Effect = _dark ? null : Shadow(2, 0.08),
        };
        AutomationProperties.SetName(b, text);
        b.MouseLeftButtonDown += (_, e) => { b.Opacity = 0.75; e.Handled = true; };
        b.MouseLeave += (_, _) => b.Opacity = 1;
        b.MouseLeftButtonUp += (_, e) => { if (b.Opacity < 1) { b.Opacity = 1; click(); } e.Handled = true; };
        b.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { click(); e.Handled = true; } };
        return b;
    }

    Border IconBtn(string glyph, string name, Action click)
    {
        var b = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = Glyph(glyph, 12, _sec), ToolTip = name, Focusable = true, FocusVisualStyle = null };
        AutomationProperties.SetName(b, name);
        b.MouseEnter += (_, _) => b.Background = _hover;
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, e) => { click(); e.Handled = true; };
        b.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { click(); e.Handled = true; } };
        return b;
    }

    Border Pill(TextBlock t) => new() { CornerRadius = new CornerRadius(5), Background = _segTrack, Padding = new Thickness(8, 2, 8, 3), Child = t, HorizontalAlignment = HorizontalAlignment.Right };

    // ───────────────────────────── composite rows ─────────────────────────────

    FrameworkElement HotkeyBox()
    {
        var t = T("", 13, _label, FontWeights.SemiBold);
        var box = new Border
        {
            CornerRadius = new CornerRadius(6), Background = _inputBg, BorderBrush = _inputBorder, BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 4, 10, 4), MinWidth = 130, Child = t, Cursor = Cursors.Hand, Focusable = true, FocusVisualStyle = null,
            ToolTip = "Click, then press a key combination. Esc cancels.", HorizontalAlignment = HorizontalAlignment.Right,
        };
        bool rec = false;
        void Show()
        {
            t.Text = rec ? "Type shortcut…" : Pretty(S.ToggleVisibilityHotkey);
            t.Foreground = rec ? _sec : _label;
            box.BorderBrush = rec ? _accent : _inputBorder;
        }
        box.MouseLeftButtonUp += (_, e) => { rec = true; box.Focus(); Keyboard.Focus(box); Show(); e.Handled = true; };
        box.LostKeyboardFocus += (_, _) => { rec = false; Show(); };
        box.PreviewKeyDown += (_, e) =>
        {
            if (!rec) return;
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape) { rec = false; Show(); return; }
            var mods = new List<string>();
            var m = Keyboard.Modifiers;
            if (m.HasFlag(ModifierKeys.Control)) mods.Add("Ctrl");
            if (m.HasFlag(ModifierKeys.Alt)) mods.Add("Alt");
            if (m.HasFlag(ModifierKeys.Shift)) mods.Add("Shift");
            if (m.HasFlag(ModifierKeys.Windows) || Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) mods.Add("Win");
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            {
                t.Text = mods.Count > 0 ? string.Join(" + ", mods) + " + …" : "Type shortcut…";
                return;
            }
            if (!mods.Any(x => x is "Ctrl" or "Alt" or "Win")) { t.Text = "Add Ctrl, Alt or Win"; return; }
            S.ToggleVisibilityHotkey = string.Join("+", mods.Append(key.ToString()));
            rec = false;
            Changed();
            Show();
        };
        Show();
        Reg(Show);
        return box;
    }

    IEnumerable<(string, string)> MonitorItems()
    {
        yield return ("Primary Display (Default)", "Primary");
        var screens = System.Windows.Forms.Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            var s = screens[i];
            yield return ($"Display {i + 1} · {s.Bounds.Width} × {s.Bounds.Height}{(s.Primary ? " (Primary)" : "")}", s.DeviceName);
        }
    }

    FrameworkElement DisplayList()
    {
        var stack = new StackPanel();
        var screens = System.Windows.Forms.Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            var s = screens[i];
            var icon = Glyph("", 18, _sec);
            icon.Margin = new Thickness(0, 0, 12, 0);
            var name = T($"Display {i + 1}{(s.Primary ? "  ·  Primary" : "")}", 13, _label, FontWeights.Medium);
            var info = T($"{s.DeviceName.TrimStart('\\', '.')}  ·  {s.Bounds.Width} × {s.Bounds.Height}  ·  Bounds [{s.Bounds.X}, {s.Bounds.Y}]", 11.5, _sec);
            var row = HStack(icon, VStack(name, info));
            row.Margin = new Thickness(0, i == 0 ? 0 : 8, 0, 0);
            stack.Children.Add(row);
        }
        return stack;
    }

    FrameworkElement DropFolder()
    {
        var t = T("", 12, _sec);
        t.MaxWidth = 220;
        t.TextTrimming = TextTrimming.CharacterEllipsis;
        t.Margin = new Thickness(0, 0, 10, 0);
        var reset = IconBtn("", "Reset to default", () => { S.DropTargetFolderPath = ""; Changed(); });
        void Apply()
        {
            bool set = !string.IsNullOrWhiteSpace(S.DropTargetFolderPath);
            t.Text = set ? S.DropTargetFolderPath : "Documents (default)";
            t.ToolTip = t.Text;
            reset.Visibility = set ? Visibility.Visible : Visibility.Collapsed;
        }
        Apply();
        Reg(Apply);
        return HStack(t, Btn("Choose…", () => { var p = PickFolder("Choose the drop zone folder"); if (p != null) { S.DropTargetFolderPath = p; Changed(); } }), reset);
    }

    FrameworkElement QuickApps()
    {
        var panel = new StackPanel();
        var list = S.QuickAppPaths;
        var inMedia = new (Func<bool> Get, Action<bool> Set)[]
        {
            (() => S.ShowQuickApp1InMedia, v => S.ShowQuickApp1InMedia = v),
            (() => S.ShowQuickApp2InMedia, v => S.ShowQuickApp2InMedia = v),
            (() => S.ShowQuickApp3InMedia, v => S.ShowQuickApp3InMedia = v),
        };
        for (int i = 0; i < list.Count; i++)
        {
            string path = list[i];
            int idx = i;
            var g = new Grid { Margin = new Thickness(0, 6, 0, 6) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var src = IconHelper.Get(path);
            FrameworkElement icon = src != null
                ? new Image { Source = src, Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Left }
                : Glyph("", 18, _sec);
            g.Children.Add(icon);
            var full = T(IconHelper.Resolve(path), 11, _sec);
            full.TextTrimming = TextTrimming.CharacterEllipsis;
            var text = VStack(T(IconHelper.DisplayName(path), 13, _label), full);
            Grid.SetColumn(text, 1);
            g.Children.Add(text);
            if (i < 3)
            {
                var lbl = T("In Media", 11, _sec);
                lbl.Margin = new Thickness(0, 0, 8, 0);
                var media = HStack(lbl, Toggle(inMedia[i].Get, inMedia[i].Set));
                media.Margin = new Thickness(12, 0, 4, 0);
                media.ToolTip = "Show this app as a shortcut button in the media player";
                Grid.SetColumn(media, 2);
                g.Children.Add(media);
            }
            var remove = IconBtn("", "Remove shortcut slot", () => { list.Remove(path); Changed(); ShowPage(true); });
            remove.Margin = new Thickness(6, 0, 0, 0);
            Grid.SetColumn(remove, 3);
            g.Children.Add(remove);
            if (i > 0) panel.Children.Add(new Border { Height = 1, Background = _sep, Margin = new Thickness(36, 0, 0, 0) });
            panel.Children.Add(g);
        }
        if (list.Count == 0)
        {
            var empty = T("No quick apps yet. Add an app, folder or link below.", 12, _sec);
            empty.Margin = new Thickness(0, 4, 0, 8);
            panel.Children.Add(empty);
        }

        var input = Input(() => "", _ => { }, double.NaN, "Executable, shortcut, folder path or URL…", out var tb);
        void AddFromBox() { AddQuickApp(tb.Text); }
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) AddFromBox(); };
        var addRow = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        addRow.ColumnDefinitions.Add(new ColumnDefinition());
        addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        addRow.Children.Add(input);
        var addBtn = Btn("Add", AddFromBox, "primary");
        addBtn.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(addBtn, 1);
        addRow.Children.Add(addBtn);
        panel.Children.Add(addRow);

        Border presets = null;
        presets = Btn("Presets…", () => ShowMenu(presets, IconHelper.Presets.Select(p =>
            (p.Name, list.Any(x => x.Equals(p.Path, StringComparison.OrdinalIgnoreCase)), (Action)(() => AddQuickApp(p.Path))))));
        var file = Btn("Add File…", () =>
        {
            var d = new Microsoft.Win32.OpenFileDialog { Title = "Browse application executable or shortcut", Filter = "Apps & shortcuts|*.exe;*.lnk;*.bat;*.cmd;*.url;*.appref-ms|All files|*.*" };
            if (d.ShowDialog(this) == true) AddQuickApp(d.FileName);
        });
        var folder = Btn("Add Folder…", () => { var p = PickFolder("Browse folder"); if (p != null) AddQuickApp(p); });
        foreach (var b in new[] { file, folder }) b.Margin = new Thickness(0, 0, 8, 0);
        var buttons = HStack(file, folder, presets);
        buttons.Margin = new Thickness(0, 10, 0, 0);
        buttons.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(buttons);
        return panel;
    }

    void AddQuickApp(string path)
    {
        path = path?.Trim().Trim('"');
        if (string.IsNullOrEmpty(path) || S.QuickAppPaths.Any(x => x.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
        S.QuickAppPaths.Add(path);
        Changed();
        ShowPage(true);
    }

    FrameworkElement ClearHistory()
    {
        var host = new ContentControl { HorizontalAlignment = HorizontalAlignment.Right, Focusable = false };
        void Normal() => host.Content = Btn("Clear History", Confirm, "destructive");
        void Confirm()
        {
            var q = T("Remove all unpinned clips? Pinned clips are kept.", 12, _sec);
            q.Margin = new Thickness(0, 0, 10, 0);
            var cancel = Btn("Cancel", Normal);
            cancel.Margin = new Thickness(0, 0, 8, 0);
            host.Content = HStack(q, cancel, Btn("Clear All", () =>
            {
                try { AppHost.Clips.ClearUnpinned(); host.Content = T("✓ History cleared", 12, new SolidColorBrush(_green)); }
                catch (Exception ex) { host.Content = T("Couldn't clear: " + ex.Message, 12, _red); }
            }, "destructive"));
        }
        Normal();
        return host;
    }

    FrameworkElement ExportClips()
    {
        var status = T("", 12, _sec);
        status.Margin = new Thickness(0, 0, 10, 0);
        status.MaxWidth = 240;
        status.TextTrimming = TextTrimming.CharacterEllipsis;
        return HStack(status, Btn("Export…", () =>
        {
            var folder = PickFolder("Choose where to export clipboard history");
            if (folder == null) return;
            try { var p = AppHost.Clips.Export(folder); status.Text = "Exported"; status.ToolTip = p; Reveal(p); }
            catch (Exception ex) { status.Text = "Export failed: " + ex.Message; status.Foreground = _red; }
        }));
    }

    FrameworkElement Preview()
    {
        var scale = new ScaleTransform(S.IslandTextScale, S.IslandTextScale);
        var accent = new SolidColorBrush(AppHost.ParseColor(S.AccentColor, Hex("#70E0C0")));
        var gray = B("#99EBEBF5");
        var art = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(10), Background = new LinearGradientBrush(Hex("#FF2D55"), Hex("#5E5CE6"), 45) };
        var bars = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var h in new[] { 8, 16, 24, 14, 20, 10 })
            bars.Children.Add(new Rectangle { Width = 3, Height = h, RadiusX = 1.5, RadiusY = 1.5, Fill = accent, Margin = new Thickness(1.5, 0, 1.5, 0), VerticalAlignment = VerticalAlignment.Center });
        var titles = VStack(T("Now Playing", 14, Brushes.White, FontWeights.SemiBold), T("Audio Stream", 12, gray));
        titles.Margin = new Thickness(10, 0, 10, 0);
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(titles, 1);
        Grid.SetColumn(bars, 2);
        top.Children.Add(art); top.Children.Add(titles); top.Children.Add(bars);

        var bar = new Grid { Height = 4, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        var trackBg = new Border { CornerRadius = new CornerRadius(2), Background = B("#40FFFFFF") };
        Grid.SetColumnSpan(trackBg, 2);
        bar.Children.Add(trackBg);
        bar.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = Brushes.White });
        var progress = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        progress.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        progress.ColumnDefinitions.Add(new ColumnDefinition());
        progress.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var end = T("-2:18", 10, gray);
        Grid.SetColumn(bar, 1);
        Grid.SetColumn(end, 2);
        progress.Children.Add(T("0:42", 10, gray)); progress.Children.Add(bar); progress.Children.Add(end);

        var island = new Border
        {
            Background = Brushes.Black, CornerRadius = new CornerRadius(26), Padding = new Thickness(14, 12, 14, 12), Width = 300,
            LayoutTransform = scale, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Child = VStack(top, progress), Effect = Shadow(20, 0.45, 4),
        };
        Reg(() => { scale.ScaleX = scale.ScaleY = S.IslandTextScale; accent.Color = AppHost.ParseColor(S.AccentColor, Hex("#70E0C0")); });
        return new Border
        {
            CornerRadius = new CornerRadius(8), MinHeight = 170, Padding = new Thickness(20),
            Background = new LinearGradientBrush(Hex("#1E3C72"), Hex("#8E54E9"), 30), Child = island,
        };
    }

    FrameworkElement Hero()
    {
        FrameworkElement icon;
        var img = LoadAppIcon();
        if (img != null) icon = new Image { Source = img, Width = 72, Height = 72 };
        else icon = new Border
        {
            Width = 72, Height = 72, CornerRadius = new CornerRadius(16), Background = new LinearGradientBrush(Hex("#2C2C2E"), Hex("#000000"), 90),
            Child = new Border { Width = 40, Height = 13, CornerRadius = new CornerRadius(6.5), Background = Brushes.Black, BorderBrush = B("#30FFFFFF"), BorderThickness = new Thickness(1) },
        };
        icon.VerticalAlignment = VerticalAlignment.Top;
        var desc = T("The fluid, versatile desktop capsule and edge dock for Windows — an Apple-style Dynamic Island with universal media controls, " +
                     "master volume, live audio visualizers, synced lyrics, music radar, clipboard shelf, timers and every Plus feature included.", 12, _sec);
        desc.TextWrapping = TextWrapping.Wrap;
        desc.Margin = new Thickness(0, 6, 0, 0);
        var text = VStack(T("Isle", 20, _label, FontWeights.Bold), T("Version 1.0.0", 12, _sec), desc);
        text.Margin = new Thickness(16, 0, 0, 0);
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(text, 1);
        g.Children.Add(icon);
        g.Children.Add(text);
        return g;
    }

    // ───────────────────────────── macOS window chrome ─────────────────────────────

    FrameworkElement TrafficLights()
    {
        _lights.Clear();
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent };
        var marks = new List<UIElement>();
        foreach (var (hex, geo, name, act) in new (string, string, string, Action)[]
        {
            ("#FF5F57", "M2,2 L6,6 M6,2 L2,6", "Close", Close),
            ("#FEBC2E", "M1.5,4 L6.5,4", "Minimize", () => WindowState = WindowState.Minimized),
            ("#28C840", "M4,1.5 L4,6.5 M1.5,4 L6.5,4", "Zoom", () => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized),
        })
        {
            var dot = new Ellipse { Fill = B(hex), Stroke = B("#26000000"), StrokeThickness = 0.5 };
            var mark = new System.Windows.Shapes.Path { Data = Geometry.Parse(geo), Stroke = B("#A6000000"), StrokeThickness = 1.3, Width = 8, Height = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = 0 };
            var g = new Grid { Width = 12, Height = 12, Margin = new Thickness(0, 0, 8, 0), Background = Brushes.Transparent };
            g.Children.Add(dot);
            g.Children.Add(mark);
            AutomationProperties.SetName(g, name);
            g.MouseLeftButtonUp += (_, e) => { act(); e.Handled = true; };
            WindowChrome.SetIsHitTestVisibleInChrome(g, true);
            marks.Add(mark);
            _lights.Add((dot, Hex(hex)));
            panel.Children.Add(g);
        }
        WindowChrome.SetIsHitTestVisibleInChrome(panel, true);
        panel.MouseEnter += (_, _) => { PaintLights(true); foreach (var m in marks) m.Opacity = 1; };
        panel.MouseLeave += (_, _) => { PaintLights(IsActive); foreach (var m in marks) m.Opacity = 0; };
        return new Border { Height = 52, Child = panel, Background = Brushes.Transparent };
    }

    /// macOS greys the traffic lights out while the window isn't key.
    void PaintLights(bool active)
    {
        foreach (var (dot, on) in _lights)
            dot.Fill = new SolidColorBrush(active ? on : _dark ? Hex("#5A5A5C") : Hex("#D0D0D2"));
    }

    void StyleFrame()
    {
        int dark = _dark ? 1 : 0, round = 2; // DWMWA_USE_IMMERSIVE_DARK_MODE, DWMWCP_ROUND
        Native.DwmSetWindowAttribute(_hwnd, 20, ref dark, 4);
        Native.DwmSetWindowAttribute(_hwnd, 33, ref round, 4);
    }

    // ───────────────────────────── helpers ─────────────────────────────

    static ImageSource LoadAppIcon()
    {
        try
        {
            var dec = BitmapDecoder.Create(new Uri("pack://application:,,,/Assets/app.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            return dec.Frames.OrderByDescending(f => f.PixelWidth).First();
        }
        catch { return null; }
    }

    static string PickFolder(string description)
    {
        using var d = new System.Windows.Forms.FolderBrowserDialog { UseDescriptionForTitle = true, Description = description, ShowNewFolderButton = true };
        return d.ShowDialog() == System.Windows.Forms.DialogResult.OK ? d.SelectedPath : null;
    }

    static void Reveal(string path)
    {
        try
        {
            if (Directory.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else if (File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch { }
    }

    static string Pretty(string hk) => string.IsNullOrWhiteSpace(hk) ? "None" : hk.Replace("+", " + ");

    static SolidColorBrush B(string hex) { var b = new SolidColorBrush(Hex(hex)); b.Freeze(); return b; }
    static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    static Duration Dur(int ms) => new(TimeSpan.FromMilliseconds(ms));
    static DropShadowEffect Shadow(double blur, double opacity, double depth = 1) =>
        new() { BlurRadius = blur, ShadowDepth = depth, Direction = 270, Opacity = opacity, Color = Colors.Black };

    static TextBlock T(string text, double size, Brush fg, FontWeight? weight = null)
    {
        var t = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center };
        if (fg != null) t.Foreground = fg;
        return t;
    }

    TextBlock Glyph(string glyph, double size, Brush fg)
    {
        var t = new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (fg != null) t.Foreground = fg;
        return t;
    }

    static StackPanel HStack(params UIElement[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    static StackPanel VStack(params UIElement[] children)
    {
        var p = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }
}
