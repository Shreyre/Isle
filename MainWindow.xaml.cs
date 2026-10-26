using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Isle.Controls;
using Isle.Services;
using Path = System.Windows.Shapes.Path;
using IOPath = System.IO.Path;

namespace Isle;

/// The Dynamic Island itself: a transparent, top-most canvas in which a black silhouette morphs between
/// compact, expanded and popup "live activity" layouts with spring physics.
public partial class MainWindow : Window
{
    enum IslandState { Hidden, Compact, Expanded, Popup }
    enum Page { Media, Timer, Clipboard, Apps, Notes, Ocr, Drop, Tour, Home, Tasks }
    static readonly Page[] TabOrder = { Page.Media, Page.Timer, Page.Clipboard, Page.Apps, Page.Notes, Page.Home, Page.Tasks }; // new tabs go last: saved LastActiveTab indices keep their meaning
    static readonly string[] TabGlyph = { "", "", "", "", "", "", "" };
    static readonly string[] TabName = { "Media", "Timer", "Clipboard", "Apps", "Notes", "Home", "Tasks" };

    static readonly Color Orange = Color.FromRgb(0xFF, 0x9F, 0x0A), Green = Color.FromRgb(0x30, 0xD1, 0x58),
        Red = Color.FromRgb(0xFF, 0x45, 0x3A), Blue = Color.FromRgb(0x0A, 0x84, 0xFF), Secondary = Color.FromArgb(0x99, 0xEB, 0xEB, 0xF5);

    AppSettings S => AppHost.Settings;
    IntPtr _hwnd;
    double _scale = 1;   // device pixels per DIP
    string _placedFor;   // dock/monitor signature of the last placement

    // ---- state ----
    IslandState _state = IslandState.Compact;
    Page _page = Page.Media;
    bool _userHidden, _fullscreen, _fullscreenRaw, _stealthHidden, _lineMode, _dragging, _dragOut, _menuOpen, _suppressHover;
    DateTime _stateAt = DateTime.Now;
    DateTime? _overSince, _outSince = DateTime.Now;
    Rect _islandRect, _bubbleRect;
    bool _bubbleOn;

    // ---- animation ----
    sealed class Spring
    {
        public double Value, Target, Velocity;
        public bool Settled => Math.Abs(Value - Target) < 0.05 && Math.Abs(Velocity) < 0.5;
        public void Snap(double v) { Value = Target = v; Velocity = 0; }
        public void Step(double dt, double k, double c)
        {
            while (dt > 1e-6)
            {
                double h = Math.Min(1 / 240.0, dt);
                Velocity += (-k * (Value - Target) - c * Velocity) * h;
                Value += Velocity * h;
                dt -= h;
            }
        }
    }
    readonly Spring _w = new(), _h = new(), _r = new();
    double _opCompact = 1, _opExpanded, _opPopup, _opIsland = 1;
    (double val, DateTime due)? _pendingW, _pendingH;
    bool _animating;
    TimeSpan _lastRender;
    readonly ScaleTransform _islandScale = new(1, 1);
    readonly RectangleGeometry _clip = new();
    double _compactW = 120, _compactH = 34, _popupW = 300, _popupH = 34;
    bool _hoverBump; // compact island swells slightly under the pointer
    double _near;    // 0..1 how close the pointer is to the island: idle widgets react to it
    bool _peek; Rect _peekRect; // Ctrl see-through: ghosted and click-through until the pointer leaves _peekRect

    // timed micro-animations, advanced by the render loop until _animUntil
    DateTime _animUntil, _popupInAt, _popupTrailAt, _pageInAt, _trackInAt, _playPopAt;
    int _pageDir = 1, _trackDir = 1;
    Page _prevPage;
    double _ambientOp, _pbarTarget;
    bool _ambientWanted;

    // ---- services ----
    readonly MediaService _media = new();
    readonly AudioSpectrum _spectrum = new();
    VolumeService _volume;
    readonly ShakeDetector _shake = new();
    readonly float[] _bands = new float[AudioSpectrum.Bands];
    readonly DispatcherTimer _tick = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
    DateTime _lastTick = DateTime.Now;
    long _ticks;
    DateTime _lastPlayingAt = DateTime.MinValue, _spectrumIdleSince = DateTime.Now;

    // lyrics
    IReadOnlyList<LyricLine> _lyrics = Array.Empty<LyricLine>();
    string _lyricsKey = "";
    DateTime _lyricsTriedAt;

    // popups
    DateTime _popupUntil;
    Page? _popupPage;
    string _popupKind = "";
    string _lastTrackPopupApp = "";
    DateTime _lastTrackPopupAt;
    // what the island last announced, so each song pops once and each play/pause once
    string _announcedKey = "";
    bool _announcedPlaying;
    DateTime? _playFlipSince;
    DateTime _islandPlayClickAt, _prevClickAt;
    ImageSource _partFor, _martFor, _homeArtFor;
    string _homeChipsKey;
    bool _mediaWasPlaying;

    // system watchers
    BatteryState _battery;
    bool _caps, _num;
    (bool mic, bool cam) _privacy;
    SystemInfo.Weather _weather;
    DateTime _weatherAt = DateTime.MinValue;
    string _weatherCity;
    string _hotkeyRegistered;

    // timer / stopwatch / focus
    bool _timerActive, _timerRunning, _focusMode, _focusBreak;
    int _focusRound = 1;
    DateTime _timerEnd;
    TimeSpan _timerLeft, _timerTotal;
    readonly Stopwatch _sw = new();
    readonly List<TimeSpan> _laps = new();

    // clipboard page
    int _clipShown = 30;
    string _clipFilter = "All";

    // radar
    CancellationTokenSource _radarCts;
    RadarMatch _radarMatch;
    double _radarPhase;

    // tour
    int _tourStep;
    static readonly (string glyph, string title, string desc)[] Tour =
    {
        ("", "Welcome to Isle", "A new way to interact with your PC. Hover the island to open it — it comes alive with your music, timers, clipboard and alerts."),
        ("", "Media & Live Scrub", "Control Spotify, browsers and any player. Drag the seek bar to jump, click the artwork to open the player, click the source badge to switch sessions."),
        ("", "Swipe Between Tabs", "Roll the mouse wheel or swipe two fingers on the touchpad over the open island to move between Media, Timer, Clipboard, Apps, Notes and Tasks."),
        ("", "Clipboard Shelf", "Everything you copy is kept: search it, filter by type, pin favourites, drag clips out, extract text from images or look them up with Google Lens."),
        ("", "Timers, Stopwatch & Focus", "Start a countdown from a preset, run a stopwatch with laps, or stay in flow with Pomodoro focus rounds — all as live activities."),
        ("", "Apps, Folders & Drops", "Pin apps and folders for one-click launching, and drop files on the island to save them to a folder or the clipboard shelf."),
        ("", "Magic Shake & Docking", "Shake the cursor to summon the island, press Alt+H to hide it, tap Ctrl over it to see and click what's behind, and dock it to any edge or corner in Settings."),
    };

    DispatcherTimer _notesSave;
    readonly DateTime _startedAt = DateTime.Now;
    Path _playIcon, _pauseIcon, _homePlay, _homePause;

    public MainWindow()
    {
        InitializeComponent();
        _playIcon = TransportIcon("GPlay", 19, 22);
        _pauseIcon = TransportIcon("GPause", 17, 21);
        PlayBtn.Content = _playIcon;
        _homePlay = TransportIcon("GPlay", 13, 15);
        _homePause = TransportIcon("GPause", 12, 15);
        Island.RenderTransform = _islandScale;
        Island.Clip = _clip;
        _w.Snap(120); _h.Snap(34); _r.Snap(17);

        SourceInitialized += OnSourceInitialized;
        DpiChanged += (_, e) => { _scale = e.NewDpi.DpiScaleX; _placedFor = null; Dispatcher.BeginInvoke(PlaceWindow, DispatcherPriority.Background); };
        Deactivated += (_, _) => { if (_state == IslandState.Expanded && !IsCursorOver()) Collapse(); };
        AppHost.SettingsChanged += ApplySettings;

        WireIsland();
        WireMedia();
        WireTimer();
        WireClipboard();
        WireMisc();

        _media.Updated += OnMediaUpdated;
        _media.Start();
        try
        {
            _volume = new VolumeService();
            _volume.Changed += (v, m, self) => Dispatcher.BeginInvoke(() => OnVolumeChanged(v, m, self));
            _volume.DefaultDeviceChanged += name => Dispatcher.BeginInvoke(() => OnAudioDeviceChanged(name));
            MediaService.AudioProcessResolver = _volume.ActiveAudioProcess;
        }
        catch (Exception ex) { AppHost.Log("Volume service: " + ex.Message); }

        _battery = SystemInfo.Battery();
        _caps = (Native.GetKeyState(0x14) & 1) != 0;
        _num = (Native.GetKeyState(0x90) & 1) != 0;
        SystemInfo.SampleStats();

        _tick.Tick += (_, _) => OnTick();
        _tick.Start();
        ApplySettings();

        if (!S.HasCompletedOnboarding)
            Dispatcher.BeginInvoke(async () => { await Task.Delay(1200); ReplayTour(); });
    }

    // =====================================================================================
    //  Window plumbing
    // =====================================================================================

    void OnSourceInitialized(object sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        Native.SetToolWindow(_hwnd, noActivate: true);
        HwndSource.FromHwnd(_hwnd).AddHook(WndProc);
        _scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        PlaceWindow();
        RegisterHotkey();
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Native.WM_HOTKEY:
                ToggleUserHidden();
                handled = true;
                break;
            case Native.WM_MOUSEHWHEEL:
                if (_state == IslandState.Expanded) OnHorizontalWheel((short)((wParam.ToInt64() >> 16) & 0xFFFF));
                break;
            case 0x001A when wParam.ToInt64() == 0x2F: // WM_SETTINGCHANGE + SPI_SETWORKAREA
            case Native.WM_DISPLAYCHANGE:
                _placedFor = null;
                Dispatcher.BeginInvoke(PlaceWindow, DispatcherPriority.Background);
                break;
        }
        return IntPtr.Zero;
    }

    bool IsSide => S.DockPosition is "Left" or "Right";
    bool Attached => S.DockMode != "Floating";

    System.Windows.Forms.Screen TargetScreen()
    {
        if (S.FollowCursorDisplay)
        {
            var c = Native.Cursor();
            return System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(c.X, c.Y));
        }
        return System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == S.TargetDisplayId)
               ?? System.Windows.Forms.Screen.PrimaryScreen;
    }

    void PlaceWindow()
    {
        if (_hwnd == IntPtr.Zero) return;
        var scr = TargetScreen();
        string sig = $"{scr.DeviceName}|{scr.Bounds}|{scr.WorkingArea}|{S.DockPosition}|{S.SideOffsetPercent}|{_scale}";
        if (sig == _placedFor) return;
        _placedFor = sig;

        var b = scr.Bounds;
        var hmon = Native.MonitorFromPoint(new Native.POINT { X = b.Left + b.Width / 2, Y = b.Top + b.Height / 2 }, 2);
        double scale = Native.GetDpiForMonitor(hmon, 0, out uint dpiX, out _) == 0 ? dpiX / 96.0 : _scale;

        Width = IsSide ? 600 : 640;
        Height = IsSide ? 640 : 560;
        int wpx = (int)Math.Ceiling(Width * scale), hpx = (int)Math.Ceiling(Height * scale);
        var wa = scr.WorkingArea;
        int midY = b.Top + (int)(b.Height * S.SideOffsetPercent / 100.0) - hpx / 2;
        midY = Math.Clamp(midY, b.Top, Math.Max(b.Top, b.Bottom - hpx));
        (int x, int y) = S.DockPosition switch
        {
            "Bottom" => (b.Left + (b.Width - wpx) / 2, wa.Bottom - hpx),
            "Left" => (b.Left, midY),
            "Right" => (b.Right - wpx, midY),
            "TopLeft" => (b.Left, b.Top),
            "TopRight" => (b.Right - wpx, b.Top),
            _ => (b.Left + (b.Width - wpx) / 2, b.Top),
        };
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, wpx, hpx, Native.SWP_NOACTIVATE);
        ArrangeCompactAxis();
        Retarget(stagger: false);
        ApplyFrame();
    }

    Point CursorDip()
    {
        var c = Native.Cursor();
        Native.GetWindowRect(_hwnd, out var r);
        return new Point((c.X - r.Left) / _scale, (c.Y - r.Top) / _scale);
    }

    bool IsCursorOver() => ExpandedHoverRect().Contains(CursorDip());

    /// While open (or opening), the pointer counts as "over" anywhere the island is or is springing towards:
    /// testing only the half-grown shape closed it again the instant it opened when entered from the hot zone's ends.
    Rect ExpandedHoverRect() => Inflate(Rect.Union(_islandRect, TargetRect()), 14);

    Rect TargetRect()
    {
        var (w, h, _) = TargetShape();
        double s = S.IslandTextScale;
        var (ax, ay, hx, hy) = Anchor();
        return new Rect(ax - hx * w * s, ay - hy * h * s, w * s, h * s);
    }

    static Rect Inflate(Rect r, double d) { if (r.IsEmpty) return r; r.Inflate(d, d); return r; }

    void RegisterHotkey()
    {
        if (_hwnd == IntPtr.Zero || _hotkeyRegistered == S.ToggleVisibilityHotkey) return;
        Native.UnregisterHotKey(_hwnd, 1);
        _hotkeyRegistered = S.ToggleVisibilityHotkey;
        if (SystemInfo.TryParseHotkey(S.ToggleVisibilityHotkey, out uint mods, out uint vk) &&
            !Native.RegisterHotKey(_hwnd, 1, mods | Native.MOD_NOREPEAT, vk))
        {
            AppHost.Log($"Hotkey {S.ToggleVisibilityHotkey} is in use by another app");
            Dispatcher.BeginInvoke(async () => { await Task.Delay(2500); Toast("", $"{S.ToggleVisibilityHotkey} is used by another app"); });
        }
    }

    /// True while the island is open or mid-animation (memory trimming waits for idle).
    public bool IsBusy => _state == IslandState.Expanded || _animating;

    public void ToggleUserHidden()
    {
        _userHidden = !_userHidden;
        if (_userHidden) { if (_state != IslandState.Compact) SetState(IslandState.Compact); }
        else Toast("", "Island shown");
        StartAnimating();
    }

    public void ReplayTour()
    {
        _tourStep = 0;
        _userHidden = false;
        Expand(Page.Tour);
    }

    void ApplySettings()
    {
        _placedFor = null;
        PlaceWindow();
        RegisterHotkey();
        Shape.Fill = Ui.Brush(AppHost.ParseColor(S.IslandColor, Colors.Black));
        BubbleBg.Fill = Shape.Fill;
        Pet.Visibility = S.IslandPetEnabled ? Visibility.Visible : Visibility.Collapsed;
        Pet.Kind = S.IslandPetType;
        RadarBtn.Visibility = S.EnableMusicRadar ? Visibility.Visible : Visibility.Collapsed;
        BuildTabChrome();
        BuildQuickApps();
        BuildIslandMenu();
        _ = EnsureLyrics();
        if (_weatherCity != S.WeatherCity) { _weatherCity = S.WeatherCity; _weatherAt = DateTime.MinValue; }
        BuildTimerPresets();
        if (!EnabledTabs.Contains(_page) && IsTab(_page)) _page = EnabledTabs.FirstOrDefault();
        if (_state == IslandState.Expanded) ShowPage(_page);
        Retarget(stagger: false);
        StartAnimating();
    }

    Color Accent => AppHost.ParseColor(S.AccentColor, Color.FromRgb(0x70, 0xE0, 0xC0));
    Color Tint => S.UseAlbumArtTint && _media.Current.Art != null ? _media.Current.ArtColor : Accent;

    // =====================================================================================
    //  State machine & geometry
    // =====================================================================================

    List<Page> EnabledTabs
    {
        get
        {
            var l = new List<Page>();
            if (S.TabHomeEnabled) l.Add(Page.Home);
            if (S.TabMediaEnabled) l.Add(Page.Media);
            if (S.TabTimerEnabled) l.Add(Page.Timer);
            if (S.TabClipboardEnabled) l.Add(Page.Clipboard);
            if (S.TabQuickLaunchEnabled) l.Add(Page.Apps);
            if (S.TabNotesEnabled) l.Add(Page.Notes);
            if (S.TabTasksEnabled) l.Add(Page.Tasks);
            if (l.Count == 0) l.Add(Page.Media);
            return l;
        }
    }

    static bool IsTab(Page p) => Array.IndexOf(TabOrder, p) >= 0;

    bool MediaLive
    {
        get
        {
            var m = _media.Current;
            return m.HasMedia && (m.IsPlaying || DateTime.Now - _lastPlayingAt < TimeSpan.FromSeconds(30));
        }
    }

    bool TimerLive => _timerActive || _sw.IsRunning;
    bool HasActivity => MediaLive || TimerLive || _radarCts != null;

    void SetState(IslandState st)
    {
        bool growing = st == IslandState.Expanded || (st == IslandState.Popup && _state != IslandState.Expanded);
        _state = st;
        _stateAt = DateTime.Now;
        if (st == IslandState.Popup) PopupView.Visibility = Visibility.Visible;
        if (st == IslandState.Expanded) ExpandedView.Visibility = Visibility.Visible;
        Retarget(stagger: S.UseStaggeredNotchMotion, growing);
        StartAnimating();
    }

    void Expand(Page? page = null)
    {
        if (_userHidden || _fullscreen) return;
        _stealthHidden = false;
        var pg = page ?? DefaultPage();
        ShowPage(pg);
        SetState(IslandState.Expanded);
    }

    Page DefaultPage()
    {
        // Apple: opening the island expands the live activity it is showing
        var tabs = EnabledTabs;
        if (MediaLive && tabs.Contains(Page.Media)) return Page.Media;
        if (TimerLive && tabs.Contains(Page.Timer)) return Page.Timer;
        if (S.IdleWidgetMode == "NextTask" && tabs.Contains(Page.Tasks)) return Page.Tasks; // the capsule was showing a task
        if (tabs.Contains(Page.Home)) return Page.Home; // nothing live: the start page, not an empty "Not Playing"
        int i = Math.Clamp(S.LastActiveTab, 0, TabOrder.Length - 1);
        return tabs.Contains(TabOrder[i]) ? TabOrder[i] : tabs[0];
    }

    void Collapse()
    {
        if (_page == Page.Tour && _state == IslandState.Expanded) return;
        ToggleVolumeRow(false);
        if (Keyboard.FocusedElement is DependencyObject d && IsDescendant(Island, d)) Keyboard.ClearFocus();
        Native.SetToolWindow(_hwnd, noActivate: true);
        _suppressHover = true; // re-arm hover only after the pointer has left once
        SetState(_stealthHidden ? IslandState.Hidden : IslandState.Compact);
    }

    /// Recomputes the spring targets for the current state.
    void Retarget(bool stagger, bool growing = true)
    {
        var (w, h, r) = TargetShape();
        _r.Target = r;
        _pendingW = _pendingH = null;
        if (stagger && Math.Abs(h - _h.Target) > 4)
        {
            var due = DateTime.Now.AddMilliseconds(55);
            if (growing) { _w.Target = w; _pendingH = (h, due); }
            else { _h.Target = h; _pendingW = (w, due); }
        }
        else { _w.Target = w; _h.Target = h; }
        StartAnimating();
    }

    bool AsLine => S.DockMode == "ThinLine" || _lineMode;

    (double w, double h, double r) TargetShape()
    {
        double t = S.ThinLineThickness;
        switch (_state)
        {
            case IslandState.Hidden:
                return IsSide ? (t, 90, t / 2) : (90, t, t / 2);
            case IslandState.Compact:
                if (_userHidden) return IsSide ? (t, 60, t / 2) : (60, t, t / 2);
                double b = _hoverBump ? 1 : 0, lt = t + 3 * b;
                if (AsLine) return IsSide ? (lt, Math.Max(100, _compactH * 0.9) + 10 * b, lt / 2) : (Math.Max(120, _compactW * 0.85) + 10 * b, lt, lt / 2);
                double cw = _compactW + (IsSide ? 3 : 8) * b, ch = _compactH + (IsSide ? 8 : 3) * b;
                double rc = S.DockMode == "Notch" ? Math.Min(ch / 2, 12) : Math.Min(cw, ch) / 2;
                return (cw, ch, rc);
            case IslandState.Expanded:
                var (pw, ph) = PageSize(_page);
                return (pw, ph, Attached ? 28 : 38);
            default:
                return (_popupW, _popupH, _popupH <= 40 ? _popupH / 2 : (Attached ? 24 : 30));
        }
    }

    bool LyricsShownInPage => S.ShowLyricsInExpanded && _lyrics.Count > 0 && _media.Current.HasMedia && _radarCts == null && _radarMatch == null;

    (double w, double h) PageSize(Page p)
    {
        bool header = p is not (Page.Drop or Page.Tour);
        header = header && S.TabNavigationStyle == "Header";
        bool row = IsTab(p) && S.TabNavigationStyle != "Header"; // bottom row: dots and/or the pin button
        double chrome = (header ? 38 : 16) + 6 + (row ? 17 : 10);
        return p switch
        {
            Page.Home => (420, chrome + 64 + (_media.Current.HasMedia ? 64 : 0)),
            Page.Media => (420, chrome + 136 + (LyricsShownInPage ? 62 : 0)),
            Page.Timer => (440, chrome + 118),
            Page.Clipboard => (440, chrome + 290),
            Page.Apps => (420, chrome + 8 + Math.Clamp((int)Math.Ceiling((S.QuickAppPaths.Count + 1) / 5.0), 1, 3) * 82),
            Page.Notes => (420, chrome + 170),
            Page.Tasks => (420, chrome + 196),
            Page.Ocr => (440, chrome + 200),
            Page.Drop => (420, chrome + 116),
            _ => (440, chrome + 160),
        };
    }

    (double ax, double ay, double hx, double hy) Anchor()
    {
        double W = Width, H = Height;
        double off = Attached ? 0 : S.TopOffset;
        return S.DockPosition switch
        {
            "Bottom" => (W / 2, H - off, 0.5, 1),
            "Left" => (off, H / 2, 0, 0.5),
            "Right" => (W - off, H / 2, 1, 0.5),
            "TopLeft" => (off, off, 0, 0),
            "TopRight" => (W - off, off, 1, 0),
            _ => (W / 2, off, 0.5, 0),
        };
    }

    void StartAnimating()
    {
        if (_animating) return;
        _animating = true;
        _lastRender = default;
        CompositionTarget.Rendering += OnRendering;
    }

    (double k, double c) SpringParams()
    {
        double response = S.AnimationSpeed switch { "Snappy" => 0.26, "Fast" => 0.34, "Gentle" => 0.62, _ => 0.45 };
        double damping = S.AnimationStyle switch { "Classic" => 1.0, "Spring" => 0.62, "Bounce" => 0.45, _ => 0.8 };
        double w0 = 2 * Math.PI / response;
        return (w0 * w0, 2 * damping * w0);
    }

    void OnRendering(object sender, EventArgs e)
    {
        var t = ((RenderingEventArgs)e).RenderingTime;
        double dt = _lastRender == default ? 1 / 60.0 : (t - _lastRender).TotalSeconds;
        _lastRender = t;
        if (dt <= 0) return;
        dt = Math.Min(dt, 0.05);
        var now = DateTime.Now;
        if (_pendingW is { } pw && now >= pw.due) { _w.Target = pw.val; _pendingW = null; }
        if (_pendingH is { } ph && now >= ph.due) { _h.Target = ph.val; _pendingH = null; }

        var (k, c) = SpringParams();
        _w.Step(dt, k, c);
        _h.Step(dt, k, c);
        _r.Step(dt, k, c * 1.3);
        if (_w.Value < 1) _w.Value = 1;
        if (_h.Value < 1) _h.Value = 1;

        // content cross-fade (staggered behind the silhouette, like iOS)
        double since = (now - _stateAt).TotalSeconds;
        double delay = S.UseStaggeredNotchMotion ? 0.12 : 0.05;
        bool showC = _state == IslandState.Compact && !AsLine && !_userHidden;
        bool showE = _state == IslandState.Expanded && since > delay;
        bool showP = _state == IslandState.Popup && since > delay * 0.7;
        _opCompact = Fade(_opCompact, showC && since > 0.04, dt);
        _opExpanded = Fade(_opExpanded, showE, dt);
        _opPopup = Fade(_opPopup, showP, dt);
        bool visible = !(_state == IslandState.Hidden || _fullscreen || (_userHidden && _state == IslandState.Compact));
        double opIsland = !visible ? 0 : _peek ? 0.15 : 1; // see-through leaves a faint ghost so you know it's there
        _opIsland = Ui.Ease(_opIsland, opIsland, visible ? 14 : 10, dt);
        _ambientWanted = AmbientWanted();
        _ambientOp = Ui.Ease(_ambientOp, _ambientWanted ? 1 : 0, _ambientWanted ? 7 : 11, dt);

        ApplyFrame();
        ApplyMotion(now, dt);

        bool settled = _w.Settled && _h.Settled && _r.Settled && _pendingW == null && _pendingH == null
                       && Done(_opCompact, showC) && Done(_opExpanded, showE) && Done(_opPopup, showP)
                       && Math.Abs(_opIsland - opIsland) < 0.01
                       && Math.Abs(_ambientOp - (_ambientWanted ? 1 : 0)) < 0.01
                       && now >= _animUntil && Math.Abs(BarWidth - _pbarTarget) < 0.5;
        if (settled)
        {
            _w.Value = _w.Target; _h.Value = _h.Target; _r.Value = _r.Target;
            _opIsland = opIsland;
            _ambientOp = _ambientWanted ? 1 : 0;
            ApplyFrame();
            // faded out for a game / fullscreen app: drop the window itself, so even a transparent topmost
            // overlay can't keep the game out of independent-flip presentation
            if (_fullscreen && _opIsland == 0 && IsVisible) Hide();
            CompositionTarget.Rendering -= OnRendering;
            _animating = false;
        }

        static double Fade(double v, bool on, double dt) => on ? Math.Min(1, v + dt * 7) : Math.Max(0, v - dt * 14);
        static bool Done(double v, bool on) => on ? v >= 1 : v <= 0;
    }

    /// Keeps the render loop running for a timed animation of `secs` and returns its start time.
    DateTime Kick(double secs)
    {
        var now = DateTime.Now;
        if (now.AddSeconds(secs) > _animUntil) _animUntil = now.AddSeconds(secs);
        StartAnimating();
        return now;
    }

    static double Prog(DateTime now, DateTime at, double delay, double dur) => Math.Clamp(((now - at).TotalSeconds - delay) / dur, 0, 1);

    double BarWidth => double.IsNaN(PBar.Width) ? 0 : PBar.Width;

    /// Content micro-animations layered on top of the silhouette morph.
    void ApplyMotion(DateTime now, double dt)
    {
        // tab switch: the new page glides in from the side it came from, the active dot stretches into a pill
        double pg = Ui.OutCubic(Prog(now, _pageInAt, 0, 0.34));
        PagesShift.X = _pageDir * 30 * (1 - pg);
        Pages.Opacity = pg;
        ApplyDots(pg);

        // new song while open: artwork pops, titles glide in from the skip direction
        double tr = Prog(now, _trackInAt, 0, 0.45);
        MHeaderShift.X = _trackDir * 20 * (1 - Ui.OutCubic(tr));
        MHeader.Opacity = Ui.OutCubic(tr);
        MArtScale.ScaleX = MArtScale.ScaleY = 0.82 + 0.18 * Ui.OutBack(tr);

        double pp = Prog(now, _playPopAt, 0, 0.36);
        PlayScale.ScaleX = PlayScale.ScaleY = 0.7 + 0.3 * Ui.OutBack(pp);

        // popup entrance, staggered behind the silhouette
        double a = Prog(now, _popupInAt, 0.04, 0.42), b = Prog(now, _popupInAt, 0.09, 0.38), c = Prog(now, _popupTrailAt, 0.14, 0.4);
        PLeadScale.ScaleX = PLeadScale.ScaleY = 0.5 + 0.5 * Ui.OutBack(a);
        PLead.Opacity = Math.Min(1, a * 2.5);
        PTextsShift.X = 14 * (1 - Ui.OutCubic(b));
        PTexts.Opacity = b;
        PTrailScale.ScaleX = PTrailScale.ScaleY = 0.4 + 0.6 * Ui.OutBack(c);
        PTrail.Opacity = Math.Min(1, c * 2.5);

        // volume HUD bar slides to its level instead of jumping
        double bw = Ui.Ease(BarWidth, _pbarTarget, 22, dt);
        PBar.Width = Math.Abs(bw - _pbarTarget) < 0.5 ? _pbarTarget : bw;
    }

    void ApplyDots(double e)
    {
        foreach (Border d in Dots.Children)
        {
            var pg = (Page)d.Tag;
            double on = pg == _page ? e : pg == _prevPage ? 1 - e : 0;
            d.Width = 6 + 10 * on;
            d.Opacity = 0.3 + 0.7 * on;
        }
    }

    void ApplyFrame()
    {
        double s = S.IslandTextScale;
        _islandScale.ScaleX = _islandScale.ScaleY = s;
        double w = _w.Value, h = _h.Value, r = Math.Max(0, Math.Min(_r.Value, Math.Min(w, h) / 2));
        var (ax, ay, hx, hy) = Anchor();
        double left = ax - hx * w * s, top = ay - hy * h * s;
        Canvas.SetLeft(Island, left);
        Canvas.SetTop(Island, top);
        Island.Width = w;
        Island.Height = h;
        _clip.Rect = new Rect(0, 0, w, h);
        _clip.RadiusX = _clip.RadiusY = r;
        _islandRect = new Rect(left, top, w * s, h * s);
        Shape.Data = AmbientShape.Data = BuildShape(left, top, w * s, h * s, r * s);
        Shape.Opacity = Island.Opacity = _opIsland;
        AmbientShape.Opacity = _ambientOp * _opIsland;
        Island.IsHitTestVisible = _opIsland > 0.5;

        // content layers: each one fades and grows into place
        Layer(CompactView, _opCompact);
        Layer(PopupView, _opPopup);
        Layer(ExpandedView, _opExpanded);
        ExpandedScale.ScaleX = ExpandedScale.ScaleY = 0.94 + 0.06 * Ui.OutCubic(_opExpanded);
        CompactScale.ScaleX = CompactScale.ScaleY = 0.9 + 0.1 * Ui.OutCubic(_opCompact);
        PopupScale.ScaleX = PopupScale.ScaleY = 0.95 + 0.05 * Ui.OutCubic(_opPopup);

        var va = S.DockPosition == "Bottom" ? VerticalAlignment.Bottom : IsSide ? VerticalAlignment.Center : VerticalAlignment.Top;
        ExpandedView.VerticalAlignment = va;
        ExpandedScale.CenterY = va == VerticalAlignment.Bottom ? ExpandedView.ActualHeight : 0;

        // Apple "minimal" bubble for a second live activity
        bool bubble = _bubbleOn && _state == IslandState.Compact && !AsLine && !_userHidden && _opIsland > 0.5;
        Bubble.Visibility = bubble ? Visibility.Visible : Visibility.Collapsed;
        if (bubble)
        {
            double bs = 34 * s, gap = 6 * s;
            double bx, by;
            if (IsSide) { bx = left + (w * s - bs) / 2; by = top + h * s + gap; }
            else if (S.DockPosition == "TopRight") { bx = left - gap - bs; by = top + (h * s - bs) / 2; }
            else { bx = left + w * s + gap; by = top + (h * s - bs) / 2; }
            Bubble.RenderTransform = new ScaleTransform(s, s);
            Canvas.SetLeft(Bubble, bx);
            Canvas.SetTop(Bubble, by);
            _bubbleRect = new Rect(bx, by, bs, bs);
        }
        else _bubbleRect = Rect.Empty;
    }

    static void Layer(UIElement e, double op)
    {
        e.Opacity = op;
        var v = op > 0.01 ? Visibility.Visible : Visibility.Collapsed;
        if (e.Visibility != v) e.Visibility = v;
        e.IsHitTestVisible = op > 0.6;
    }

    /// Island silhouette. Notch mode squares the screen-side corners and adds concave "ears" that melt into the bezel.
    Geometry BuildShape(double x, double y, double w, double h, double r)
    {
        bool notch = S.DockMode == "Notch" || (S.DockMode == "ThinLine" && !(_state == IslandState.Compact && AsLine) && _state != IslandState.Hidden);
        if (!notch || h < 6 || w < 6) return new RectangleGeometry(new Rect(x, y, w, h), r, r);

        bool corner = S.DockPosition is "TopLeft" or "TopRight";
        bool side = IsSide;
        // Build in a canonical frame (screen edge along the top, length L, depth D) then map onto the real edge.
        double L = side ? h : w, D = side ? w : h;
        // concave fillet that melts into the bezel; grows with the morph like the MacBook notch
        double e = Math.Clamp(r * 0.55, 4, 13);
        var ear = new Size(e, e);
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            if (corner)
            {
                c.BeginFigure(new Point(0, 0), true, true);
                c.LineTo(new Point(L + e, 0), true, true);
                c.ArcTo(new Point(L, e), ear, 0, false, SweepDirection.Counterclockwise, true, true);
                c.LineTo(new Point(L, D - r), true, true);
                c.ArcTo(new Point(L - r, D), new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
                c.LineTo(new Point(e, D), true, true);
                c.ArcTo(new Point(0, D + e), ear, 0, false, SweepDirection.Counterclockwise, true, true);
            }
            else
            {
                c.BeginFigure(new Point(-e, 0), true, true);
                c.ArcTo(new Point(0, e), ear, 0, false, SweepDirection.Clockwise, true, true);
                c.LineTo(new Point(0, D - r), true, true);
                c.ArcTo(new Point(r, D), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true, true);
                c.LineTo(new Point(L - r, D), true, true);
                c.ArcTo(new Point(L, D - r), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true, true);
                c.LineTo(new Point(L, e), true, true);
                c.ArcTo(new Point(L + e, 0), ear, 0, false, SweepDirection.Clockwise, true, true);
            }
        }
        g.Transform = new MatrixTransform(S.DockPosition switch
        {
            "Bottom" => new Matrix(1, 0, 0, -1, x, y + h),
            "Left" => new Matrix(0, 1, 1, 0, x, y),
            "Right" => new Matrix(0, 1, -1, 0, x + w, y),
            "TopRight" => new Matrix(-1, 0, 0, 1, x + w, y),
            _ => new Matrix(1, 0, 0, 1, x, y),
        });
        g.Freeze();
        return g;
    }

    // =====================================================================================
    //  Main tick: hover physics, live activities, system watchers
    // =====================================================================================

    void OnTick()
    {
        var now = DateTime.Now;
        double dt = Math.Clamp((now - _lastTick).TotalSeconds, 0.001, 0.2);
        _lastTick = now;
        _ticks++;
        if (_hwnd == IntPtr.Zero) return;

        var p = CursorDip();
        // belt and braces: no mouse button down means no drag in progress
        if (_dragging && !_dragOut && Native.GetAsyncKeyState(0x01) >= 0 && Native.GetAsyncKeyState(0x02) >= 0) _dragging = false;
        UpdateHover(p, now);

        if (S.MagicShakeEnabled && !(S.MagicShakeExcludeFullscreen && _fullscreenRaw) &&
            _shake.Feed(Native.Cursor().X, S.MagicShakeSensitivity))
            OnShake();

        UpdateTimers(now);
        UpdateMediaClock(now);
        AnnounceMedia(now);
        UpdateCompact(now, dt, p);
        if (_state == IslandState.Expanded) UpdateExpandedLive(dt);
        if (_state == IslandState.Popup && PWave.Visibility == Visibility.Visible) PWave.Push(_bands, _media.Current.IsPlaying, _spectrum.HasSignal, dt);
        if (S.IslandPetEnabled && _opIsland > 0.5 && !_islandRect.IsEmpty)
        {
            bool upside = S.DockPosition is not "Bottom" && _islandRect.Top < Pet.Height;
            Pet.UpsideDown = upside;
            Pet.Tick(dt, _islandRect, _state != IslandState.Compact, p);
        }
        else if (Pet.Visibility == Visibility.Visible && _opIsland <= 0.5) Pet.Opacity = 0;

        CheckLockKeys();
        if (_ticks % 3 == 0 && LyricsWindow.Instance != null)
            LyricsWindow.Instance.Update(_media.Current.Title, _media.Current.Artist, _lyrics, _media.Current.LivePosition, _media.Current.IsPlaying);
        if (_ticks % 15 == 0 && S.FollowCursorDisplay && _state != IslandState.Expanded) PlaceWindow();
        if (_ticks % 30 == 0) CheckFullscreen();
        if (_ticks % 60 == 0)
        {
            // not while a menu is open: menus are topmost popups too, and re-asserting would bury them under the island
            if (!_fullscreen && !_menuOpen) Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            if (S.EnablePrivacyIndicator) _privacy = SystemInfo.PrivacyInUse(); else _privacy = default;
        }
        if (_ticks % 45 == 0 && S.IdleWidgetMode is "SystemStats" or "ActivityRings" or "Network")
            SystemInfo.SampleStats(net: S.IdleWidgetMode == "Network" || (S.IdleWidgetMode == "SystemStats" && S.ShowNetworkSpeed));
        if (_ticks % 90 == 0) CheckBattery();
        if (S.IdleWidgetMode == "Weather" && now - _weatherAt > TimeSpan.FromMinutes(20))
        {
            _weatherAt = now;
            Dispatcher.BeginInvoke(async () =>
            {
                var w = await SystemInfo.GetWeatherAsync(S.WeatherCity);
                if (w != null) _weather = w;
                else _weatherAt = DateTime.Now - TimeSpan.FromMinutes(19); // retry in about a minute
            });
        }
    }

    void UpdateHover(Point p, DateTime now)
    {
        bool over;
        if (_state == IslandState.Expanded)
            over = ExpandedHoverRect().Contains(p) || _bubbleRect.Contains(p) || Mouse.Captured != null || _dragging || _menuOpen;
        else
            over = Inflate(_islandRect, 4).Contains(p) || _bubbleRect.Contains(p) || HotRect().Contains(p);

        // see-through: Ctrl over the island ghosts it and lets clicks reach what's behind; latched (so the click
        // itself needn't carry Ctrl) until the pointer leaves the area it covered
        if (!_peek && over && S.CtrlSeeThrough && Native.GetAsyncKeyState(0x11) < 0 && Mouse.Captured == null && !_dragging && !_menuOpen
            && !(IsActive && Keyboard.FocusedElement is TextBox)) SetPeek(true);
        else if (_peek && !_peekRect.Contains(p)) SetPeek(false);
        if (_peek) over = false;

        if (over) { _outSince = null; _overSince ??= now; }
        else { _overSince = null; _outSince ??= now; }

        bool bump = over && _state == IslandState.Compact && !_userHidden && !_fullscreen;
        if (bump != _hoverBump) { _hoverBump = bump; if (_state == IslandState.Compact) Retarget(false); } // other states ignore it; retargeting there would cancel the staggered expand
        double overFor = _overSince.HasValue ? (now - _overSince.Value).TotalSeconds : 0;
        double outFor = _outSince.HasValue ? (now - _outSince.Value).TotalSeconds : 0;
        double dwell = S.DwellTimeMs / 1000.0, idleDelay = S.IdleHideDelayMs / 1000.0;

        // auto-collapse to thin line when idle
        bool wantLine = S.AutoHideToEdge && !HasActivity && !over && outFor > idleDelay && _state == IslandState.Compact;
        if (wantLine != _lineMode && (wantLine || over || HasActivity)) { _lineMode = wantLine; Retarget(false); }

        if (_suppressHover && !over) _suppressHover = false;
        if (_userHidden || _fullscreen) return;
        switch (_state)
        {
            case IslandState.Hidden:
                if ((over && !_suppressHover) || HasActivity || !S.AutoHideWhenIdle) { _stealthHidden = false; SetState(IslandState.Compact); }
                break;
            case IslandState.Compact:
                if (over && !_suppressHover && !S.ExpandOnClickOnly && overFor >= dwell) Expand(_bubbleRect.Contains(p) ? Page.Timer : null);
                else if (!over && S.AutoHideWhenIdle && !HasActivity && outFor > idleDelay) { _stealthHidden = true; SetState(IslandState.Hidden); }
                break;
            case IslandState.Expanded:
                if (S.IsPinned && S.PinTimeoutSeconds > 0 && !over && outFor >= S.PinTimeoutSeconds)
                {
                    TogglePin(); // the pin has expired
                    Collapse();
                    break;
                }
                bool keep = S.IsPinned || _page == Page.Tour || (IsActive && Keyboard.FocusedElement is TextBox);
                // a click anywhere else closes it at once, however long "stay open" is set
                bool clickedAway = !over && (Native.GetAsyncKeyState(0x01) < 0 || Native.GetAsyncKeyState(0x02) < 0);
                if (!over && !keep && (outFor >= S.GraceTimeMs / 1000.0 || clickedAway)) Collapse();
                break;
            case IslandState.Popup:
                // page-less popups (volume, toasts) open the default page; before, hovering one just pinned it on screen
                if (over && !_suppressHover && !S.ExpandOnClickOnly && overFor >= dwell + 0.35) Expand(_popupPage);
                else if (now > _popupUntil && !over) EndPopup();
                break;
        }
    }

    void SetPeek(bool on)
    {
        _peek = on;
        _peekRect = Rect.Union(Rect.Union(ExpandedHoverRect(), HotRect()), _bubbleRect);
        Native.SetClickThrough(_hwnd, on);
        StartAnimating();
    }

    Rect HotRect()
    {
        double t = Math.Max(2, S.HotZoneThickness) / _scale;
        double s = S.IslandTextScale;
        double span = Math.Max(IsSide ? _compactH : _compactW, 140) * s + 60;
        var c = new Point(_islandRect.X + _islandRect.Width / 2, _islandRect.Y + _islandRect.Height / 2);
        return S.DockPosition switch
        {
            "Bottom" => new Rect(c.X - span / 2, Height - t, span, t),
            "Left" => new Rect(0, c.Y - span / 2, t, span),
            "Right" => new Rect(Width - t, c.Y - span / 2, t, span),
            "TopLeft" => new Rect(0, 0, span, t),
            "TopRight" => new Rect(Width - span, 0, span, t),
            _ => new Rect(c.X - span / 2, 0, span, t),
        };
    }

    void OnShake()
    {
        if (_userHidden) { _userHidden = false; StartAnimating(); }
        if (_state == IslandState.Expanded) Collapse(); else Expand();
    }

    void CheckFullscreen()
    {
        bool game = S.GameMode && SystemInfo.IsFocusAppInFront(S.FocusApps);
        _fullscreenRaw = game || SystemInfo.IsForegroundFullscreen(_hwnd);
        bool fs = game || (S.AutoHideInFullscreen && _fullscreenRaw);
        if (fs == _fullscreen) return;
        _fullscreen = fs;
        if (fs && _state != IslandState.Compact) SetState(IslandState.Compact);
        if (!fs && !IsVisible)
        {
            Show(); // ShowActivated=False: comes back without taking focus
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
        StartAnimating();
    }

    void CheckLockKeys()
    {
        bool caps = (Native.GetKeyState(0x14) & 1) != 0, num = (Native.GetKeyState(0x90) & 1) != 0;
        if (caps != _caps) { _caps = caps; if (S.EnableLockKeysPopup) LockPopup("Caps Lock", caps); }
        if (num != _num) { _num = num; if (S.EnableLockKeysPopup) LockPopup("Num Lock", num); }
    }

    void CheckBattery()
    {
        var b = SystemInfo.Battery();
        var old = _battery;
        _battery = b;
        if (!b.HasBattery || !S.EnableBatteryPopups || S.SilentDormantMode) return;
        if (b.PluggedIn != old.PluggedIn) BatteryPopup(b, b.PluggedIn ? "Charging" : "On Battery");
        else if (!b.PluggedIn && ((old.Percent > 20 && b.Percent <= 20) || (old.Percent > 10 && b.Percent <= 10))) BatteryPopup(b, "Low Battery");
        else if (b.PluggedIn && old.Percent < 100 && b.Percent >= 100) BatteryPopup(b, "Fully Charged");
    }

    // =====================================================================================
    //  Compact (resting / live-activity) content
    // =====================================================================================

    void ArrangeCompactAxis()
    {
        bool v = IsSide;
        void At(UIElement e, int i) { Grid.SetColumn(e, v ? 0 : i); Grid.SetRow(e, v ? i : 0); }
        At(CLead, 0); At(CCenter, 1); At(CTrail, 2); At(CPrivacy, 3);
        CompactGrid.Margin = v ? new Thickness(0, 10, 0, 10) : new Thickness(12, 0, 12, 0);
        CCenter.Margin = v ? new Thickness(0, 6, 0, 6) : new Thickness(8, 0, 8, 0);
        CPrivacy.Margin = v ? new Thickness(0, 6, 0, 0) : new Thickness(6, 0, 0, 0);
    }

    ImageSource _cartFor;

    void UpdateCompact(DateTime now, double dt, Point cursor)
    {
        UIElement[] all = { CArt, CLeadIcon, CBatt, CAnalog, CRings, CCal, CText, CDots, CBold, CEyes, CDot, COrb, CGap, CWave, CTrailText, CRing };
        var on = new HashSet<UIElement>();
        double h = 34;
        bool side = IsSide;
        var m = _media.Current;
        _bubbleOn = false;

        // pointer magnetism: within ~160 DIP of the island the content leans a few pixels towards the cursor
        Vector d = cursor - new Point(_islandRect.X + _islandRect.Width / 2, _islandRect.Y + _islandRect.Height / 2);
        double ex = Math.Max(0, Math.Abs(d.X) - _islandRect.Width / 2), ey = Math.Max(0, Math.Abs(d.Y) - _islandRect.Height / 2);
        _near = Ui.Ease(_near, Math.Clamp(1 - Math.Sqrt(ex * ex + ey * ey) / 160, 0, 1), 10, dt);
        double lx = side ? 1.5 : 4, ly = side ? 4 : 1.5;
        CompactShift.X = Ui.Ease(CompactShift.X, Math.Clamp(d.X / (_islandRect.Width / 2 + 30), -1, 1) * lx * _near, 14, dt);
        CompactShift.Y = Ui.Ease(CompactShift.Y, Math.Clamp(d.Y / (_islandRect.Height / 2 + 30), -1, 1) * ly * _near, 14, dt);

        void Lead(string glyph, Color c, string font = null)
        {
            CLeadIcon.Text = glyph;
            CLeadIcon.FontFamily = font != null ? new FontFamily(font) : (FontFamily)FindResource("Icons");
            CLeadIcon.Foreground = Ui.Brush(c);
            on.Add(CLeadIcon);
        }
        void Trail(string text, Color c) { CTrailText.Text = text; CTrailText.Foreground = Ui.Brush(c); on.Add(CTrailText); }

        if (TimerLive && !MediaLive)
        {
            TimerCompact(Lead, Trail);
            if (!side) on.Add(CGap);
        }
        else if (MediaLive)
        {
            if (_cartFor != m.Art || CArt.Background == null)
            {
                _cartFor = m.Art;
                CArt.Background = m.Art != null ? new ImageBrush(m.Art) { Stretch = Stretch.UniformToFill } : Ui.Brush(Color.FromRgb(0x2C, 0x2C, 0x2E));
            }
            on.Add(CArt);
            string lyric = CurrentLyric(0);
            if (!side && S.ShowLyricsInCollapsed && !string.IsNullOrEmpty(lyric) && m.IsPlaying)
            {
                CText.Text = lyric; CText.Foreground = Brushes.White; CText.FontWeight = FontWeights.SemiBold; CText.MaxWidth = 260;
                on.Add(CText);
            }
            else if (!side) on.Add(CGap);
            CWave.Color = Tint;
            CWave.Push(_bands, m.IsPlaying, _spectrum.HasSignal, dt);
            on.Add(CWave);
            _bubbleOn = TimerLive;
            if (_bubbleOn) UpdateBubble();
        }
        else IdleWidget(now, dt, cursor, on, Lead, Trail, ref h);

        foreach (var e in all)
        {
            var v = on.Contains(e) ? Visibility.Visible : Visibility.Collapsed;
            if (e.Visibility != v) e.Visibility = v;
        }

        bool priv = _privacy.mic || _privacy.cam;
        CPrivacy.Visibility = priv ? Visibility.Visible : Visibility.Collapsed;
        if (priv) CPrivacy.Fill = Ui.Brush(_privacy.cam ? Green : Orange);

        CompactGrid.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w, hh;
        if (side) { w = Math.Max(34, CompactGrid.DesiredSize.Width + 10); hh = Math.Max(64, CompactGrid.DesiredSize.Height); }
        else { w = Math.Max(S.IdleWidgetMode == "MinimalDot" && !HasActivity ? 44 : 64, CompactGrid.DesiredSize.Width); hh = h; }
        if (Math.Abs(w - _compactW) > 0.5 || Math.Abs(hh - _compactH) > 0.5)
        {
            _compactW = w; _compactH = hh;
            if (_state == IslandState.Compact || _state == IslandState.Hidden) Retarget(false);
        }
    }

    void TimerCompact(Action<string, Color, string> lead, Action<string, Color> trail)
    {
        if (_timerActive)
        {
            var c = _focusMode && _focusBreak ? Green : Orange;
            lead(_focusMode ? (_focusBreak ? "" : "") : "", c, null);
            trail(Fmt(TimerLeft), c);
        }
        else
        {
            lead("", Colors.White, null);
            trail(FmtSw(_sw.Elapsed, false), Colors.White);
        }
    }

    void UpdateBubble()
    {
        if (_timerActive)
        {
            BubbleRing.Visibility = Visibility.Visible;
            BubbleRing.Color = _focusMode && _focusBreak ? Green : Orange;
            BubbleRing.Value = _timerTotal.TotalSeconds > 0 ? TimerLeft.TotalSeconds / _timerTotal.TotalSeconds : 0;
            BubbleRing.InvalidateVisual();
            BubbleIcon.Text = "";
            BubbleIcon.Foreground = Ui.Brush(BubbleRing.Color);
        }
        else
        {
            BubbleRing.Visibility = Visibility.Collapsed;
            BubbleIcon.Text = "";
            BubbleIcon.Foreground = Brushes.White;
        }
    }

    void IdleWidget(DateTime now, double dt, Point cursor, HashSet<UIElement> on, Action<string, Color, string> lead, Action<string, Color> trail, ref double h)
    {
        bool side = IsSide;
        string time = S.Use12HourClock ? now.ToString("h:mm") : now.ToString("HH:mm");
        string mode = S.IdleWidgetMode;
        if (side && mode is "Clock" or "TrackInfo" or "SystemStats" or "Weather" or "Network" or "NextTask") mode = "SideClock";
        if (side && mode == "Orb") mode = "MinimalDot"; // the orb's track runs along a horizontal capsule

        switch (mode)
        {
            case "AnalogClock":
                CAnalog.Time = now;
                on.Add(CAnalog);
                if (!side) { CText.Text = now.ToString("ddd d"); CText.Foreground = Brushes.White; CText.MaxWidth = 200; on.Add(CText); }
                break;
            case "Calendar":
                CCalDay.Text = now.ToString("ddd").ToUpperInvariant();
                CCalNum.Text = now.Day.ToString();
                on.Add(CCal);
                if (!side) { CText.Text = S.Use12HourClock ? now.ToString("h:mm tt") : time; CText.Foreground = Brushes.White; CText.MaxWidth = 200; on.Add(CText); }
                break;
            case "ActivityRings":
                // ease towards each 1.5 s sample so the rings fill like Apple's instead of jumping; redraw only while moving
                void Fill(Ring ring, double v)
                {
                    double nv = Ui.Ease(ring.Value, v, 5, dt);
                    if (Math.Abs(nv - ring.Value) > 1e-3) { ring.Value = nv; ring.InvalidateVisual(); }
                }
                Fill(CRingCpu, SystemInfo.Cpu / 100);
                Fill(CRingRam, SystemInfo.Ram / 100);
                on.Add(CRings);
                if (side) break;
                Runs(on, ("CPU ", Secondary), ($"{SystemInfo.Cpu:0}%", CRingCpu.Color), ("   RAM ", Secondary), ($"{SystemInfo.Ram:0}%", CRingRam.Color));
                CText.MaxWidth = 300;
                break;
            case "Network":
                Runs(on, ("↓ ", Blue), (SystemInfo.FormatRate(SystemInfo.DownBps), Colors.White), ("    ↑ ", Orange), (SystemInfo.FormatRate(SystemInfo.UpBps), Colors.White));
                CText.MaxWidth = 300;
                break;
            case "NextTask":
                var next = S.Tasks.FirstOrDefault(t => !t.Done);
                int open = S.Tasks.Count(t => !t.Done);
                lead(next != null ? TabGlyph[Array.IndexOf(TabOrder, Page.Tasks)] : "", next != null ? Accent : Green, null);
                CText.Text = next?.Text ?? "All caught up";
                CText.Foreground = next != null ? Brushes.White : Ui.Brush(Secondary);
                CText.MaxWidth = 240;
                on.Add(CText);
                if (open > 1) trail($"+{open - 1}", Secondary);
                break;
            case "Orb":
                COrb.Accent = Accent;
                COrb.Tick(Root.TranslatePoint(cursor, COrb).X / Math.Max(1, COrb.ActualWidth), _near, dt);
                on.Add(COrb);
                break;
            case "Battery":
                var b = _battery;
                if (b.HasBattery)
                {
                    CBatt.Level = b.Percent / 100.0; CBatt.Charging = b.Charging; CBatt.InvalidateVisual();
                    on.Add(CBatt);
                    if (!side) trail($"{b.Percent}%", b.Charging ? Green : b.Percent <= 20 ? Red : Colors.White);
                }
                else lead("", Colors.White, null);
                break;
            case "TrackInfo":
                var m = _media.Current;
                lead("", Secondary, null);
                CText.Text = m.HasMedia ? $"{m.Title}  —  {m.Artist}" : "No media playing";
                CText.Foreground = m.HasMedia ? Brushes.White : Ui.Brush(Secondary);
                CText.MaxWidth = 260;
                on.Add(CText);
                break;
            case "SystemStats":
                string net = S.ShowNetworkSpeed ? $"   ↓ {SystemInfo.FormatRate(SystemInfo.DownBps)}" : "";
                CText.Text = $"CPU {SystemInfo.Cpu:0}%   RAM {SystemInfo.Ram:0}%{net}";
                CText.Foreground = Brushes.White; CText.MaxWidth = 340;
                on.Add(CText);
                break;
            case "MinimalDot":
                CDot.Fill = Ui.Brush(Accent);
                CDot.Opacity = 0.55 + 0.45 * Math.Sin(now.TimeOfDay.TotalSeconds * 2.2);
                CDotScale.ScaleX = CDotScale.ScaleY = 1 + 0.7 * _near; // swells as the pointer approaches
                on.Add(CDot);
                break;
            case "AnimatedEyes":
            case "WhiteEyeballs":
                CEyes.WhiteStyle = mode == "WhiteEyeballs";
                CEyes.Accent = Accent;
                // aim from the eyes themselves, saturating softly: small moves near the island still register,
                // a cursor across the screen doesn't pin them to the corner
                var e = Root.TranslatePoint(cursor, CEyes) - new Point(CEyes.ActualWidth / 2, CEyes.ActualHeight / 2);
                CEyes.Look(new Vector(e.X / (Math.Abs(e.X) + 40), e.Y / (Math.Abs(e.Y) + 24)), _near);
                CEyes.Tick(dt);
                on.Add(CEyes);
                break;
            case "Weather":
                if (_weather != null)
                {
                    lead(SystemInfo.WeatherGlyph(_weather.Code), Colors.White, "Segoe UI Symbol");
                    double temp = S.WeatherFahrenheit ? _weather.TempC * 9 / 5 + 32 : _weather.TempC;
                    CText.Text = $"{temp:0}°  {_weather.Place}";
                }
                else CText.Text = "Weather…";
                CText.Foreground = Brushes.White; CText.MaxWidth = 220;
                on.Add(CText);
                break;
            case "SideClock":
                CBoldTop.Text = S.Use12HourClock ? now.ToString("%h") : now.ToString("HH");
                CBoldBottom.Text = now.ToString("mm");
                CBoldTop.FontSize = CBoldBottom.FontSize = 14;
                CBoldTop.LineHeight = CBoldBottom.LineHeight = 15;
                CBoldBottom.Foreground = Ui.Brush(Secondary);
                on.Add(CBold);
                break;
            default: // Clock
                switch (S.IdleClockStyle)
                {
                    case "Bold":
                        CBoldTop.Text = S.Use12HourClock ? now.ToString("%h") : now.ToString("HH");
                        CBoldBottom.Text = now.ToString("mm");
                        CBoldTop.FontSize = CBoldBottom.FontSize = 24;
                        CBoldTop.LineHeight = CBoldBottom.LineHeight = 23;
                        CBoldBottom.Foreground = Ui.Brush(Accent);
                        on.Add(CBold);
                        h = 62;
                        break;
                    case "DotMatrix":
                        CDots.Text = time;
                        CDots.Color = Colors.White;
                        CDots.Pointer = _near > 0.02 ? Root.TranslatePoint(cursor, CDots) : null; // LEDs glow under the approaching pointer
                        on.Add(CDots);
                        break;
                    default:
                        Runs(on, (now.ToString("ddd d MMM"), Secondary), ("   " + (S.Use12HourClock ? now.ToString("h:mm tt") : time), Colors.White));
                        CText.MaxWidth = 300;
                        break;
                }
                break;
        }
    }

    /// Multi-colour single line in CText. The runs already there are compared in place (allocation-free, every tick)
    /// and rebuilt only when their text changed or another mode overwrote CText.Text; each mode's colours are fixed.
    void Runs(HashSet<UIElement> on, params (string text, Color color)[] runs)
    {
        var il = CText.Inlines.FirstInline;
        bool same = true;
        foreach (var (text, _) in runs)
        {
            if (il is not System.Windows.Documents.Run r || r.Text != text) { same = false; break; }
            il = il.NextInline;
        }
        if (!same || il != null)
        {
            CText.Inlines.Clear();
            foreach (var (text, color) in runs) CText.Inlines.Add(new System.Windows.Documents.Run(text) { Foreground = Ui.Brush(color) });
        }
        on.Add(CText);
    }

    // =====================================================================================
    //  Pages (expanded)
    // =====================================================================================

    void ShowPage(Page p, int dir = 0)
    {
        if (IsTab(p) && !EnabledTabs.Contains(p)) p = EnabledTabs[0];
        if (_state == IslandState.Expanded && p != _page && _opExpanded > 0.5)
        {
            var tabs = EnabledTabs;
            if (dir == 0) dir = tabs.IndexOf(p) >= tabs.IndexOf(_page) ? 1 : -1;
            _prevPage = _page;
            _pageDir = dir;
            _pageInAt = Kick(0.36);
        }
        _page = p;
        if (IsTab(p)) { int idx = Array.IndexOf(TabOrder, p); if (S.LastActiveTab != idx) { S.LastActiveTab = idx; } }
        foreach (var (g, pg) in new (Grid, Page)[] { (PageHome, Page.Home), (PageMedia, Page.Media), (PageTimer, Page.Timer), (PageClipboard, Page.Clipboard), (PageApps, Page.Apps), (PageNotes, Page.Notes), (PageTasks, Page.Tasks), (PageOcr, Page.Ocr), (PageDrop, Page.Drop), (PageTour, Page.Tour) })
            g.Visibility = pg == p ? Visibility.Visible : Visibility.Collapsed;
        Header.Visibility = IsTab(p) && S.TabNavigationStyle == "Header" ? Visibility.Visible : Visibility.Collapsed;
        Pages.Margin = new Thickness(18, Header.Visibility == Visibility.Visible ? 2 : 16, 18, 6);
        var (w, h) = PageSize(p);
        ExpandedView.Width = w;
        ExpandedView.Height = h;
        UpdateTabChrome();
        switch (p)
        {
            case Page.Home: RefreshHome(); break;
            case Page.Media: RefreshMediaPage(); break;
            case Page.Timer: RefreshTimerPage(); break;
            case Page.Clipboard: _clipShown = 30; RebuildClips(); break;
            case Page.Apps: BuildApps(); break;
            case Page.Notes: NotesBox.Text = S.Notes; NotesHint.Visibility = string.IsNullOrEmpty(S.Notes) ? Visibility.Visible : Visibility.Collapsed; break;
            case Page.Tasks: BuildTasks(); break;
            case Page.Tour: RefreshTour(); break;
            case Page.Drop: DropFolderName.Text = IOPath.GetFileName(DropFolderPath()); break;
        }
        ShowAmbient();
        if (_state == IslandState.Expanded) Retarget(false);
    }

    void BuildTabChrome()
    {
        TabIcons.Children.Clear();
        Dots.Children.Clear();
        foreach (var tab in EnabledTabs)
        {
            int i = Array.IndexOf(TabOrder, tab);
            var btn = new Button { Style = (Style)FindResource("Glyph"), Content = TabGlyph[i], Width = 26, Height = 26, FontSize = 12, ToolTip = TabName[i], Tag = tab, Margin = new Thickness(0, 0, 2, 0) };
            btn.Click += (_, _) => ShowPage(tab);
            TabIcons.Children.Add(btn);
            var dot = new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Background = Brushes.White, Margin = new Thickness(3, 0, 3, 0), Tag = tab, Cursor = Cursors.Hand };
            dot.MouseLeftButtonUp += (_, e) => { ShowPage(tab); e.Handled = true; };
            Dots.Children.Add(dot);
        }
        UpdateTabChrome();
    }

    void UpdateTabChrome()
    {
        bool tab = IsTab(_page);
        TabIcons.Visibility = tab && S.TabNavigationStyle == "Header" ? Visibility.Visible : Visibility.Collapsed;
        Dots.Visibility = tab && S.TabNavigationStyle == "Dots" && EnabledTabs.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        // one pin button: in the header for the Header style, beside the dots otherwise
        bool header = S.TabNavigationStyle == "Header";
        Panel host = header ? Header : DotsRow;
        if (PinBtn.Parent != host) { ((Panel)PinBtn.Parent).Children.Remove(PinBtn); host.Children.Add(PinBtn); }
        PinBtn.HorizontalAlignment = header ? HorizontalAlignment.Right : HorizontalAlignment.Center;
        PinBtn.Margin = header ? new Thickness(0) : new Thickness(Dots.Visibility == Visibility.Visible ? 6 : 0, 0, 0, 0);
        PinBtn.Visibility = tab ? Visibility.Visible : Visibility.Collapsed;
        PinBtn.Content = S.IsPinned ? "" : "";
        PinBtn.Foreground = S.IsPinned ? Ui.Brush(Orange) : (Brush)FindResource("Tertiary");
        PinBtn.ToolTip = !S.IsPinned ? "Pin open"
            : S.PinTimeoutSeconds > 0 ? $"Pinned — closes {(S.PinTimeoutSeconds < 60 ? $"{S.PinTimeoutSeconds} s" : $"{S.PinTimeoutSeconds / 60} min")} after you leave. Click to unpin"
            : "Pinned open — click to unpin";
        DotsRow.Visibility = tab && !header ? Visibility.Visible : Visibility.Collapsed;
        foreach (Button b in TabIcons.Children) b.Foreground = (Page)b.Tag == _page ? Brushes.White : (Brush)FindResource("Tertiary");
        ApplyDots(Ui.OutCubic(Prog(DateTime.Now, _pageInAt, 0, 0.34)));
    }

    void CycleTab(int dir)
    {
        var tabs = EnabledTabs;
        if (!IsTab(_page) || tabs.Count < 2) return;
        int i = tabs.IndexOf(_page);
        ShowPage(tabs[(i + dir + tabs.Count) % tabs.Count], dir);
    }

    DateTime _lastWheelTab;
    int _hWheelAcc;

    void OnHorizontalWheel(int delta)
    {
        _hWheelAcc += delta;
        if (Math.Abs(_hWheelAcc) >= 120 && (DateTime.Now - _lastWheelTab).TotalMilliseconds > 280)
        {
            CycleTab(_hWheelAcc > 0 ? 1 : -1);
            _hWheelAcc = 0;
            _lastWheelTab = DateTime.Now;
        }
    }

    void UpdateExpandedLive(double dt)
    {
        switch (_page)
        {
            case Page.Home: if (_ticks % 15 == 0) RefreshHome(); break; // clock and chips; twice a second is plenty
            case Page.Media: UpdateMediaLive(dt); break;
            case Page.Timer: UpdateTimerLive(); break;
        }
    }

    // ----- home (start page) -----

    void RefreshHome()
    {
        var now = DateTime.Now;
        HomeTime.Text = S.Use12HourClock ? now.ToString("h:mm") : now.ToString("HH:mm");
        HomeDate.Text = now.ToString("dddd, d MMMM");

        string chipsKey = $"{_battery}|{_weather}|{S.WeatherFahrenheit}";
        if (chipsKey != _homeChipsKey) { _homeChipsKey = chipsKey; BuildHomeChips(); }
        RefreshHomeMedia();
    }

    void BuildHomeChips()
    {
        HomeChips.Children.Clear();
        if (_battery.HasBattery)
        {
            var b = _battery;
            var glyph = new BatteryGlyph { Level = b.Percent / 100.0, Charging = b.Charging, VerticalAlignment = VerticalAlignment.Center };
            HomeChips.Children.Add(Chip(glyph, $"{b.Percent}%", b.Charging ? Green : b.Percent <= 20 ? Red : Colors.White));
        }
        if (_weather != null)
        {
            double temp = S.WeatherFahrenheit ? _weather.TempC * 9 / 5 + 32 : _weather.TempC;
            var icon = new TextBlock { Text = SystemInfo.WeatherGlyph(_weather.Code), FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
            HomeChips.Children.Add(Chip(icon, $"{temp:0}°", Colors.White));
        }
    }

    void RefreshHomeMedia()
    {
        var m = _media.Current;
        HomeResume.Visibility = m.HasMedia ? Visibility.Visible : Visibility.Collapsed;
        if (m.HasMedia)
        {
            HomeTrack.Text = m.Title;
            HomeArtist.Text = (m.IsPlaying ? "" : "Paused · ") + m.Artist;
            if (_homeArtFor != m.Art || HomeArt.Background == null)
            {
                _homeArtFor = m.Art;
                HomeArt.Background = m.Art != null ? new ImageBrush(m.Art) { Stretch = Stretch.UniformToFill } : Ui.Brush(Color.FromRgb(0x2C, 0x2C, 0x2E));
            }
            var icon = m.IsPlaying ? _homePause : _homePlay;
            if (HomePlay.Content != icon) HomePlay.Content = icon;
        }
        // the resume row comes and goes with the session: keep the island sized to it
        var (w, h) = PageSize(Page.Home);
        if (_state == IslandState.Expanded && _page == Page.Home && Math.Abs(ExpandedView.Height - h) > 0.5)
        {
            ExpandedView.Width = w; ExpandedView.Height = h;
            Retarget(false);
        }
    }

    Border Chip(UIElement icon, string text, Color color)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(icon);
        sp.Children.Add(new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush(color), Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return new Border { Child = sp, CornerRadius = new CornerRadius(11), Background = Ui.Brush(Color.FromRgb(0x1C, 0x1C, 0x1E)), Padding = new Thickness(10, 5, 11, 5), Margin = new Thickness(0, 3, 0, 3), HorizontalAlignment = HorizontalAlignment.Right };
    }

    // ----- media -----

    void WireMedia()
    {
        PlayBtn.Click += (_, _) => { _islandPlayClickAt = DateTime.Now; _media.PlayPause(); RefreshMediaPage(); };
        // Home's resume row: play turns the page over to the player (see OnMediaUpdated); the row itself opens it
        HomePlay.Click += (_, _) => { _islandPlayClickAt = DateTime.Now; _media.PlayPause(); RefreshHomeMedia(); };
        HomeResume.MouseLeftButtonUp += (_, e) => { if (EnabledTabs.Contains(Page.Media)) ShowPage(Page.Media, 1); e.Handled = true; };
        PrevBtn.Click += (_, _) => { _prevClickAt = DateTime.Now; _media.Previous(); };
        NextBtn.Click += (_, _) => _media.Next();
        MArt.MouseLeftButtonUp += (_, e) => { _media.BringPlayerToFront(); e.Handled = true; };
        MBadge.MouseLeftButtonUp += (_, e) => { _media.CycleSession(); e.Handled = true; };
        MSeek.Seeked += f => { var d = _media.Current.Duration; if (d > TimeSpan.Zero) _media.Seek(TimeSpan.FromSeconds(d.TotalSeconds * f)); };
        MSeek.Scrubbing += f => { var d = _media.Current.Duration; MElapsed.Text = Fmt(TimeSpan.FromSeconds(d.TotalSeconds * f)); MRemain.Text = "-" + Fmt(TimeSpan.FromSeconds(d.TotalSeconds * (1 - f))); };
        LyricsBtn.Click += (_, _) => { LyricsWindow.Toggle(); _ = EnsureLyrics(); };
        RadarBtn.Click += (_, _) => StartRadar();
        RadarCancel.Click += (_, _) => StopRadar();
        RadarClose.Click += (_, _) => { _radarMatch = null; RadarView.Visibility = Visibility.Collapsed; MediaMain.Visibility = Visibility.Visible; Retarget(false); };
        RadarSpotify.Click += (_, _) => { if (_radarMatch != null) IconHelper.OpenUrl(_radarMatch.SpotifyUrl); };
        RadarYouTube.Click += (_, _) => { if (_radarMatch != null) IconHelper.OpenUrl(_radarMatch.YouTubeUrl); };
        RadarCopy.Click += (_, _) => { if (_radarMatch != null) TrySetClipboard($"{_radarMatch.Title} — {_radarMatch.Artist}"); };

        VolumeBtn.Click += (_, _) => ToggleVolumeRow(MVolumeRow.Visibility != Visibility.Visible);
        VolumeBtn.MouseWheel += (_, e) => { NudgeVolume(e.Delta > 0 ? 0.02f : -0.02f); e.Handled = true; };
        MVolumeRow.MouseWheel += (_, e) => { NudgeVolume(e.Delta > 0 ? 0.02f : -0.02f); e.Handled = true; };
        MuteBtn.Click += (_, _) => { if (_volume == null) return; _volume.Muted = !_volume.Muted; SyncVolumeRow(); };
        VolSeek.BarStyle = "Modern";
        VolSeek.Scrubbing += f => { if (_volume != null) { _volume.Volume = (float)f; SyncVolumeRow(); } };
        VolSeek.Seeked += f => { if (_volume != null) _volume.Volume = (float)f; SyncVolumeRow(); };
    }

    DateTime _volumeRowAt;

    void ToggleVolumeRow(bool show)
    {
        MVolumeRow.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        MControls.Visibility = MQuickApps.Visibility = show ? Visibility.Hidden : Visibility.Visible; // the row is see-through over the album glow
        // the slider takes the whole row; only the speaker button stays, turned into a close button
        LyricsBtn.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        RadarBtn.Visibility = !show && S.EnableMusicRadar ? Visibility.Visible : Visibility.Collapsed;
        VolumeBtn.ToolTip = show ? "Close volume" : "Volume (scroll to adjust)";
        if (show) VolumeBtn.Content = "";
        else if (_volume != null) VolumeBtn.Content = VolumeGlyph(_volume.Volume, _volume.Muted);
        _volumeRowAt = DateTime.Now;
        SyncVolumeRow();
    }

    void NudgeVolume(float d)
    {
        if (_volume == null) return;
        _volume.Volume += d;
        if (_volume.Muted && d > 0) _volume.Muted = false;
        ToggleVolumeRow(true);
    }

    void SyncVolumeRow()
    {
        if (_volume == null) return;
        float v = _volume.Volume;
        bool muted = _volume.Muted;
        VolSeek.Progress = v;
        VolSeek.InvalidateVisual();
        VolText.Text = muted ? "Muted" : $"{v * 100:0}%";
        string g = VolumeGlyph(v, muted);
        MuteBtn.Content = g;
        if (MVolumeRow.Visibility != Visibility.Visible) VolumeBtn.Content = g;
        _volumeRowAt = DateTime.Now;
    }

    Path TransportIcon(string key, double w, double h) => new()
    {
        Data = (Geometry)FindResource(key), Fill = Brushes.White, Stroke = Brushes.White, StrokeThickness = 1.6,
        StrokeLineJoin = PenLineJoin.Round, Width = w, Height = h, Stretch = Stretch.Uniform,
    };

    static string VolumeGlyph(float v, bool muted) => muted || v <= 0.001 ? "" : v < 0.34 ? "" : v < 0.67 ? "" : "";

    void OnMediaUpdated(MediaSnapshot m, bool trackChanged)
    {
        if (m.IsPlaying) _lastPlayingAt = DateTime.Now;
        if (trackChanged)
        {
            _lyrics = Array.Empty<LyricLine>();
            _lyricsKey = "";
            if (m.HasMedia) _ = EnsureLyrics();
            if (m.HasMedia && _state == IslandState.Expanded && _page == Page.Media)
            {
                _trackDir = DateTime.Now - _prevClickAt < TimeSpan.FromSeconds(2) ? -1 : 1;
                _trackInAt = Kick(0.45);
            }
        }
        // the cover often lands a poll or two after the title: refresh a popup that is already showing this song
        if (_popupKind is "track" or "play" && m.TrackKey == _announcedKey && m.Art != _partFor) { PopupArt(m); PWave.Color = Tint; }
        if (_state == IslandState.Expanded && _page == Page.Home)
        {
            // music just started while the start page is open: focus the player (only on the start, so you can still browse back)
            if (m.IsPlaying && !_mediaWasPlaying && EnabledTabs.Contains(Page.Media)) ShowPage(Page.Media, 1);
            else RefreshHomeMedia();
        }
        _mediaWasPlaying = m.IsPlaying;
        if (_state == IslandState.Expanded && _page == Page.Media) RefreshMediaPage();
        else ShowAmbient();
    }

    /// Announces songs and play/pause by watching the live snapshot (polled from the 30 fps tick).
    void AnnounceMedia(DateTime now)
    {
        var m = _media.Current;
        if (!m.HasMedia) return;
        bool quiet = now - _startedAt < TimeSpan.FromSeconds(4); // don't greet whatever was already playing at launch
        if (m.TrackKey != _announcedKey)
        {
            // players often report a new track as paused for a beat (or one that's paused anyway): announce it once it plays
            if (!m.IsPlaying) return;
            _announcedKey = m.TrackKey;
            _announcedPlaying = true;
            _playFlipSince = null;
            if (!quiet) TrackPopup(m);
            return;
        }
        if (m.IsPlaying == _announcedPlaying) { _playFlipSince = null; return; }
        _playFlipSince ??= now;
        if (now - _playFlipSince.Value < TimeSpan.FromMilliseconds(450)) return; // ride out the play/pause flicker around skips
        _announcedPlaying = m.IsPlaying;
        _playFlipSince = null;
        // pressed in the island itself: the button already showed it
        if (!quiet && now - _islandPlayClickAt > TimeSpan.FromSeconds(1.5)) PlayStatePopup(m);
    }

    async Task EnsureLyrics()
    {
        var m = _media.Current;
        if (!m.HasMedia) return;
        bool wanted = S.ShowLyricsInExpanded || S.ShowLyricsInCollapsed || LyricsWindow.Instance != null;
        if (!wanted || (_lyricsKey == m.TrackKey && (_lyrics.Count > 0 || DateTime.UtcNow - _lyricsTriedAt < TimeSpan.FromMinutes(1)))) return;
        _lyricsKey = m.TrackKey;
        _lyricsTriedAt = DateTime.UtcNow;
        var lines = await LyricsService.GetAsync(m.Title, m.Artist, m.Album, m.Duration);
        if (_media.Current.TrackKey != m.TrackKey) return;
        _lyrics = lines ?? Array.Empty<LyricLine>();
        if (_state == IslandState.Expanded && _page == Page.Media) { RefreshMediaPage(); Retarget(false); }
    }

    string CurrentLyric(int offset)
    {
        if (_lyrics.Count == 0) return null;
        int i = LyricsService.IndexAt(_lyrics, _media.Current.LivePosition + TimeSpan.FromMilliseconds(250)) + offset;
        return i >= 0 && i < _lyrics.Count ? _lyrics[i].Text : (offset == 0 ? "♪" : "");
    }

    void UpdateMediaClock(DateTime now)
    {
        var m = _media.Current;
        if (m.IsPlaying) _lastPlayingAt = now;
        // run the loopback analyser only while something plays and the island is on screen
        bool want = S.ShowWaveformVisualizer && m.IsPlaying && _opIsland > 0.1;
        if (want) { _spectrumIdleSince = now; if (!_spectrum.Running) _spectrum.Start(); }
        else if (_spectrum.Running && now - _spectrumIdleSince > TimeSpan.FromSeconds(8)) _spectrum.Stop();
        if (_spectrum.Running) _spectrum.Read(_bands); else Array.Clear(_bands);
        if (!S.ShowWaveformVisualizer) Array.Clear(_bands);
        if (MVolumeRow.Visibility == Visibility.Visible && now - _volumeRowAt > TimeSpan.FromSeconds(4) && !VolSeek.IsDragging) ToggleVolumeRow(false);
    }

    void RefreshMediaPage()
    {
        var m = _media.Current;
        bool has = m.HasMedia;
        MTitle.Text = has ? m.Title : "Not Playing";
        MArtist.Text = has ? m.Artist : "Play something in Spotify, a browser or any media app";
        MBadge.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        var appIcon = has ? MediaService.AppIcon(m.AppId) : null;
        MBadge.Background = appIcon != null ? new ImageBrush(appIcon) { Stretch = Stretch.UniformToFill } : Ui.Brush(Color.FromRgb(0x3A, 0x3A, 0x3C));
        MBadgeGlyph.Visibility = appIcon == null ? Visibility.Visible : Visibility.Collapsed;
        MBadge.ToolTip = m.AppName + (_media.SessionCount > 1 ? " — click to switch source" : "");
        if (m.Art != _martFor || MArt.Background == null)
        {
            _martFor = m.Art;
            MArt.Background = m.Art != null ? new ImageBrush(m.Art) { Stretch = Stretch.UniformToFill } : Ui.Brush(Color.FromRgb(0x2C, 0x2C, 0x2E));
            MArtPlaceholder.Visibility = m.Art != null ? Visibility.Collapsed : Visibility.Visible;
        }
        SetPlayIcon(m.IsPlaying);
        PlayBtn.IsEnabled = PrevBtn.IsEnabled = NextBtn.IsEnabled = has;
        MSeek.IsEnabled = has && m.CanSeek;
        MSeek.BarStyle = S.SeekbarStyle;
        MWave.Visibility = S.ShowWaveformVisualizer ? Visibility.Visible : Visibility.Collapsed;
        MLyrics.Visibility = LyricsShownInPage ? Visibility.Visible : Visibility.Collapsed;
        bool radar = _radarCts != null || _radarMatch != null;
        RadarView.Visibility = radar ? Visibility.Visible : Visibility.Collapsed;
        MediaMain.Visibility = radar ? Visibility.Hidden : Visibility.Visible;
        if (_volume != null && MVolumeRow.Visibility != Visibility.Visible) VolumeBtn.Content = VolumeGlyph(_volume.Volume, _volume.Muted);
        ShowAmbient();
        var (w, h) = PageSize(Page.Media);
        if (_page == Page.Media) { ExpandedView.Width = w; ExpandedView.Height = h; }
    }

    void UpdateMediaLive(double dt)
    {
        var m = _media.Current;
        var tint = Tint;
        MWave.Color = tint;
        MWave.Push(_bands, m.IsPlaying, _spectrum.HasSignal, dt);
        MSeek.Accent = tint;
        MSeek.Playing = m.IsPlaying;
        if (!MSeek.IsDragging)
        {
            var pos = m.LivePosition;
            MSeek.Progress = m.Duration.TotalSeconds > 0 ? pos.TotalSeconds / m.Duration.TotalSeconds : 0;
            MElapsed.Text = Fmt(pos);
            MRemain.Text = "-" + Fmt(m.Duration > pos ? m.Duration - pos : TimeSpan.Zero);
        }
        MSeek.Tick(dt);
        SetPlayIcon(m.IsPlaying);
        if (MLyrics.Visibility == Visibility.Visible)
        {
            MLyric0.Text = CurrentLyric(-1);
            MLyric1.Text = CurrentLyric(0);
            MLyric2.Text = CurrentLyric(1);
        }
        if (MVolumeRow.Visibility == Visibility.Visible) VolSeek.Tick(dt);
        if (_radarCts != null)
        {
            _radarPhase = (_radarPhase + dt * 0.8) % 1;
            PulseRing(RadarPulse1, _radarPhase);
            PulseRing(RadarPulse2, (_radarPhase + 0.5) % 1);
        }
    }

    void SetPlayIcon(bool playing)
    {
        var icon = playing ? _pauseIcon : _playIcon;
        if (PlayBtn.Content == icon) return;
        PlayBtn.Content = icon;
        if (_state == IslandState.Expanded) _playPopAt = Kick(0.36); // the symbol pops as it swaps, like SF Symbols' replace effect
    }

    static void PulseRing(Ellipse e, double t)
    {
        e.Width = e.Height = 44 + 26 * t;
        e.Opacity = 1 - t;
    }

    ImageSource _ambientFor;
    BitmapSource _ambientSmall;

    bool AmbientWanted()
    {
        var m = _media.Current;
        return S.EnableMediaBackdrop && m.Art != null && _radarCts == null && _radarMatch == null &&
               ((_state == IslandState.Expanded && _page == Page.Media) || (_state == IslandState.Popup && _popupKind is "track" or "play"));
    }

    /// Keeps the glow brush on the current cover; the render loop fades it with the island's state.
    void ShowAmbient()
    {
        var m = _media.Current;
        if (m.Art is BitmapSource src && _ambientFor != m.Art)
        {
            _ambientFor = m.Art;
            _ambientSmall = new TransformedBitmap(src, new ScaleTransform(16.0 / src.PixelWidth, 16.0 / src.PixelHeight));
            _ambientSmall.Freeze();
            // 0.34 = the old 75% artwork under a 55% black shade, collapsed into one layer
            var brush = new ImageBrush(_ambientSmall) { Stretch = Stretch.UniformToFill, Opacity = 0.34 };
            RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality); // smooth upscale = the blur
            brush.Freeze();
            AmbientShape.Fill = brush;
        }
        if (AmbientWanted() != _ambientWanted) StartAnimating();
    }

    // ----- music radar -----

    async void StartRadar()
    {
        if (_radarCts != null) return;
        _radarMatch = null;
        _radarCts = new CancellationTokenSource();
        RadarListening.Visibility = Visibility.Visible;
        RadarResult.Visibility = Visibility.Collapsed;
        RadarStatus.Text = "Listening…";
        RefreshMediaPage();
        Retarget(false);
        var progress = new Progress<string>(s => RadarStatus.Text = s);
        RadarMatch match = null;
        try { match = await MusicRadar.IdentifyAsync(progress, _radarCts.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppHost.Log("Radar: " + ex.Message); }
        bool cancelled = _radarCts?.IsCancellationRequested ?? true;
        _radarCts = null;
        if (cancelled) { RefreshMediaPage(); Retarget(false); return; }

        if (match == null)
        {
            string why = RadarStatus.Text is { Length: > 0 } st && st != "Listening…" && st != "Identifying…" ? st : "No match found";
            _radarMatch = null;
            RefreshMediaPage();
            Retarget(false);
            Toast("", why);
            return;
        }
        _radarMatch = match;
        RadarTitle.Text = match.Title;
        RadarArtist.Text = match.Artist;
        RadarCover.Background = Ui.Brush(Color.FromRgb(0x2C, 0x2C, 0x2E));
        if (!string.IsNullOrEmpty(match.CoverUrl))
            try { RadarCover.Background = new ImageBrush(new BitmapImage(new Uri(match.CoverUrl))) { Stretch = Stretch.UniformToFill }; } catch { }
        RadarListening.Visibility = Visibility.Collapsed;
        RadarResult.Visibility = Visibility.Visible;
        RefreshMediaPage();
        Retarget(false);
        if (_state != IslandState.Expanded)
        {
            ResetPopup();
            PArt.Background = RadarCover.Background; PArt.Visibility = Visibility.Visible;
            PTitle.Text = match.Title; PSub.Text = match.Artist;
            PTrailText.Text = ""; PTrailText.FontFamily = (FontFamily)FindResource("Icons"); PTrailText.Foreground = Ui.Brush(Blue); PTrailText.Visibility = Visibility.Visible;
            ShowPopup("radar", 68, 8, Page.Media);
        }
    }

    void StopRadar()
    {
        _radarCts?.Cancel();
        _radarCts = null;
        RefreshMediaPage();
        Retarget(false);
    }

    // ----- timer / stopwatch / focus -----

    TimeSpan TimerLeft => _timerRunning ? (_timerEnd - DateTime.UtcNow) : _timerLeft;

    void WireTimer()
    {
        SegTimer.Checked += (_, _) => RefreshTimerPage();
        SegStopwatch.Checked += (_, _) => RefreshTimerPage();
        SegFocus.Checked += (_, _) => RefreshTimerPage();
        TimerStartCustom.Click += (_, _) =>
        {
            int.TryParse(TimerMin.Text, out int mm);
            int.TryParse(TimerSec.Text, out int ss);
            var d = TimeSpan.FromMinutes(Math.Max(0, mm)) + TimeSpan.FromSeconds(Math.Max(0, ss));
            if (d.TotalSeconds >= 1) StartTimer(d);
            Keyboard.ClearFocus();
        };
        TimerPause.Click += (_, _) =>
        {
            if (SegFocus.IsChecked == true && !(_timerActive && _focusMode)) { _focusRound = 1; StartTimer(TimeSpan.FromMinutes(S.PomodoroFocusMinutes), focus: true, brk: false); return; }
            if (!_timerActive) return;
            if (_timerRunning) { _timerLeft = _timerEnd - DateTime.UtcNow; _timerRunning = false; }
            else { _timerEnd = DateTime.UtcNow + _timerLeft; _timerRunning = true; }
            RefreshTimerPage();
        };
        TimerCancel.Click += (_, _) => { _timerActive = _timerRunning = false; _focusMode = false; RefreshTimerPage(); };
        SwStart.Click += (_, _) => { if (_sw.IsRunning) _sw.Stop(); else _sw.Start(); RefreshTimerPage(); };
        SwLap.Click += (_, _) =>
        {
            if (_sw.IsRunning) _laps.Insert(0, _sw.Elapsed);
            else { _sw.Reset(); _laps.Clear(); }
            RefreshTimerPage();
        };
        foreach (var tb in new[] { TimerMin, TimerSec })
            tb.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);
    }

    void BuildTimerPresets()
    {
        TimerPresets.Children.Clear();
        foreach (var (label, mins) in new[] { ("1 min", 1), ("5 min", 5), ("10 min", 10), ("15 min", 15), ("30 min", 30), ("1 hr", 60) })
        {
            var b = new Button { Style = (Style)FindResource("Pill"), Content = label, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(10, 6, 10, 6) };
            b.Click += (_, _) => StartTimer(TimeSpan.FromMinutes(mins));
            TimerPresets.Children.Add(b);
        }
    }

    void StartTimer(TimeSpan d, bool focus = false, bool brk = false)
    {
        _timerTotal = d;
        _timerEnd = DateTime.UtcNow + d;
        _timerLeft = d;
        _timerActive = _timerRunning = true;
        _focusMode = focus;
        _focusBreak = brk;
        if (focus) SegFocus.IsChecked = true; else SegTimer.IsChecked = true;
        RefreshTimerPage();
    }

    void UpdateTimers(DateTime now)
    {
        if (_timerActive && _timerRunning && TimerLeft <= TimeSpan.Zero) TimerDone();
    }

    void TimerDone()
    {
        _timerActive = _timerRunning = false;
        if (S.PlayTimerSound) SystemInfo.PlayChime();
        if (_focusMode)
        {
            bool nextBreak = !_focusBreak;
            if (!nextBreak) _focusRound++;
            StartTimer(TimeSpan.FromMinutes(nextBreak ? S.PomodoroBreakMinutes : S.PomodoroFocusMinutes), true, nextBreak);
            Banner(nextBreak ? "" : "", nextBreak ? Green : Orange, nextBreak ? "Break time" : "Focus time",
                nextBreak ? $"Nice work — relax for {S.PomodoroBreakMinutes} min" : $"Round {_focusRound} — {S.PomodoroFocusMinutes} min", 6, Page.Timer);
        }
        else
        {
            if (_state == IslandState.Expanded) { SegTimer.IsChecked = true; ShowPage(Page.Timer); }
            else Banner("", Orange, "Timer", "Time's up!", 12, Page.Timer);
        }
        RefreshTimerPage();
    }

    void RefreshTimerPage()
    {
        bool sw = SegStopwatch.IsChecked == true, focus = SegFocus.IsChecked == true;
        StopwatchView.Visibility = sw ? Visibility.Visible : Visibility.Collapsed;
        bool showRunning = !sw && ((_timerActive && _focusMode == focus) || focus);
        TimerRunning.Visibility = showRunning ? Visibility.Visible : Visibility.Collapsed;
        TimerSetup.Visibility = !sw && !showRunning ? Visibility.Visible : Visibility.Collapsed;
        var color = _focusMode && _focusBreak ? Green : Orange;
        TimerLabel.Foreground = TimerBig.Foreground = TimerBar.Fill = Ui.Brush(color);
        TimerPause.Foreground = Ui.Brush(color);
        TimerPause.Background = Ui.Brush(color, 0.25);
        bool focusIdle = focus && !(_timerActive && _focusMode);
        TimerPause.Content = focusIdle || !_timerRunning ? "" : "";
        TimerCancel.Visibility = focusIdle ? Visibility.Collapsed : Visibility.Visible;
        TimerLabel.Text = focus ? (focusIdle ? $"Focus · {S.PomodoroFocusMinutes} / {S.PomodoroBreakMinutes} min" : (_focusBreak ? "Break" : $"Focus · Round {_focusRound}")) : "Timer";
        if (focusIdle) { TimerBig.Text = Fmt(TimeSpan.FromMinutes(S.PomodoroFocusMinutes)); TimerBar.Width = 0; }

        SwStart.Content = _sw.IsRunning ? "Stop" : "Start";
        SwStart.Background = Ui.Brush(_sw.IsRunning ? Red : Green, 0.25);
        SwStart.Foreground = Ui.Brush(_sw.IsRunning ? Red : Green);
        SwLap.Content = _sw.IsRunning ? "Lap" : "Reset";
        SwLaps.Children.Clear();
        for (int i = 0; i < _laps.Count; i++)
        {
            var prev = i + 1 < _laps.Count ? _laps[i + 1] : TimeSpan.Zero;
            var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            row.Children.Add(new TextBlock { Text = $"Lap {_laps.Count - i}", Foreground = Ui.Brush(Secondary), FontSize = 12 });
            row.Children.Add(new TextBlock { Text = FmtSw(_laps[i] - prev, true), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right });
            SwLaps.Children.Add(row);
        }
        UpdateTimerLive();
    }

    void UpdateTimerLive()
    {
        if (_timerActive && TimerRunning.Visibility == Visibility.Visible && !(SegFocus.IsChecked == true && !_focusMode))
        {
            var left = TimerLeft < TimeSpan.Zero ? TimeSpan.Zero : TimerLeft;
            TimerBig.Text = Fmt(left);
            double frac = _timerTotal.TotalSeconds > 0 ? 1 - left.TotalSeconds / _timerTotal.TotalSeconds : 0;
            TimerBar.Width = Math.Max(0, (TimerRunning.ActualWidth) * frac);
        }
        if (StopwatchView.Visibility == Visibility.Visible) SwBig.Text = FmtSw(_sw.Elapsed, true);
    }

    static string Fmt(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    static string FmtSw(TimeSpan t, bool hundredths) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" :
        hundredths ? $"{t.Minutes}:{t.Seconds:00}.{t.Milliseconds / 10:00}" : $"{t.Minutes}:{t.Seconds:00}";

    // ----- clipboard shelf -----

    void WireClipboard()
    {
        var clipRebuild = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(System.Windows.Forms.SystemInformation.DoubleClickTime + 50) };
        clipRebuild.Tick += (_, _) => { clipRebuild.Stop(); if (_state == IslandState.Expanded && _page == Page.Clipboard) RebuildClips(); };
        AppHost.Clips.Changed += () => { clipRebuild.Stop(); clipRebuild.Start(); };
        ClipSearch.TextChanged += (_, _) => { ClipSearchHint.Visibility = ClipSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; _clipShown = 30; RebuildClips(); };
        foreach (var (rb, f) in new[] { (FAll, "All"), (FImages, "Images"), (FUrls, "Links"), (FText, "Text"), (FPinned, "Pinned") })
            rb.Checked += (_, _) => { _clipFilter = f; _clipShown = 30; RebuildClips(); };
        DateTime armedAt = default;
        ClipClear.Click += (_, _) =>
        {
            if (DateTime.UtcNow - armedAt > TimeSpan.FromSeconds(3))
            {
                armedAt = DateTime.UtcNow;
                ClipClear.Foreground = Ui.Brush(Red);
                ClipClear.ToolTip = "Click again to delete all unpinned clips";
                var reset = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                reset.Tick += (_, _) => { reset.Stop(); ClipClear.Foreground = (Brush)FindResource("Secondary"); ClipClear.ToolTip = "Clear unpinned clips"; };
                reset.Start();
                return;
            }
            armedAt = default;
            ClipClear.Foreground = (Brush)FindResource("Secondary");
            AppHost.Clips.ClearUnpinned();
        };
        ClipMore.Click += (_, _) => { _clipShown += 30; RebuildClips(); };
        OcrCopy.Click += (_, _) => { TrySetClipboard(OcrText.Text); Toast("", "Copied"); };
        OcrDismiss.Click += (_, _) => ShowPage(Page.Clipboard);
    }

    void RebuildClips()
    {
        string q = ClipSearch.Text.Trim();
        var items = AppHost.Clips.Items.Where(c => _clipFilter switch
        {
            "Images" => c.Kind == ClipKind.Image,
            "Links" => c.Kind == ClipKind.Url,
            "Text" => c.Kind == ClipKind.Text,
            "Pinned" => c.Pinned,
            _ => true,
        }).Where(c => q.Length == 0 || (c.Text ?? "").Contains(q, StringComparison.OrdinalIgnoreCase) || (c.SourceApp ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

        ClipList.Children.Clear();
        foreach (var c in items.Take(_clipShown)) ClipList.Children.Add(BuildClip(c));
        ClipEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClipEmpty.Text = AppHost.Clips.Items.Count == 0 ? (S.ClipboardEnabled ? "Copy text, images, or drop files here" : "Clipboard shelf is turned off in Settings") : "No clips match this filter";
        ClipCount.Text = items.Count > _clipShown ? $"Showing {_clipShown} of {items.Count}" : $"{items.Count} clips";
        ClipMore.Visibility = items.Count > _clipShown ? Visibility.Visible : Visibility.Collapsed;
    }

    FrameworkElement BuildClip(ClipItem c)
    {
        var card = new Border { CornerRadius = new CornerRadius(12), Background = Ui.Brush(Color.FromRgb(0x1C, 0x1C, 0x1E)), Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 6), Cursor = Cursors.Hand };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        FrameworkElement lead;
        if (c.Kind == ClipKind.Image && File.Exists(c.ImagePath))
            lead = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(8), Background = new ImageBrush(ClipTools.LoadThumb(c.ImagePath, 96)) { Stretch = Stretch.UniformToFill } };
        else
        {
            var (glyph, col) = c.Kind switch { ClipKind.Url => ("", Blue), ClipKind.Files => ("", Orange), _ => ("", Colors.White) };
            lead = new Border
            {
                Width = 40, Height = 40, CornerRadius = new CornerRadius(8), Background = Ui.Brush(Color.FromRgb(0x2C, 0x2C, 0x2E)),
                Child = new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 15, Foreground = Ui.Brush(col), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
        }
        grid.Children.Add(lead);

        string preview = c.Kind == ClipKind.Image ? "Image" : (c.Text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        if (preview.Length > 220) preview = preview[..220];
        var texts = new StackPanel { Margin = new Thickness(10, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = preview, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.Wrap, MaxHeight = 34, Foreground = c.Kind == ClipKind.Url ? Ui.Brush(Blue) : Brushes.White });
        texts.Children.Add(new TextBlock { Text = (string.IsNullOrEmpty(c.SourceApp) ? "" : c.SourceApp + " · ") + ClipTools.TimeAgo(c.Created), FontSize = 10.5, Foreground = Ui.Brush(Secondary), Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Button Act(string g, string tip, Action a, Brush fg = null)
        {
            var b = new Button { Style = (Style)FindResource("Glyph"), Content = g, Width = 26, Height = 26, FontSize = 11.5, ToolTip = tip, Foreground = fg ?? (Brush)FindResource("Secondary") };
            b.Click += (_, e) => { a(); e.Handled = true; };
            return b;
        }
        if (c.Kind == ClipKind.Image)
        {
            actions.Children.Add(Act("", "Extract text (OCR)", () => RunOcr(c)));
            actions.Children.Add(Act("", "Search with Google Lens", () => ClipTools.SearchWithLens(c.ImagePath)));
        }
        actions.Children.Add(Act(c.Pinned ? "" : "", c.Pinned ? "Unpin" : "Pin", () => AppHost.Clips.TogglePin(c), c.Pinned ? Ui.Brush(Orange) : null));
        actions.Children.Add(Act("", "Delete", () => AppHost.Clips.Remove(c)));
        actions.Opacity = c.Pinned ? 1 : 0.0;
        card.MouseEnter += (_, _) => { actions.Opacity = 1; card.Background = Ui.Brush(Color.FromRgb(0x26, 0x26, 0x28)); };
        card.MouseLeave += (_, _) => { actions.Opacity = c.Pinned ? 1 : 0; card.Background = Ui.Brush(Color.FromRgb(0x1C, 0x1C, 0x1E)); };
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);
        card.Child = grid;

        // click = copy, double-click image = Quick Look, drag = drag out
        Point down = default;
        bool pressed = false;
        card.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { pressed = false; if (c.Kind == ClipKind.Image) ClipPreviewWindow.ShowImage(c.ImagePath); else ClipPreviewWindow.ShowText(c.Text); return; }
            down = e.GetPosition(card); pressed = true;
        };
        card.MouseMove += (_, e) =>
        {
            if (!pressed || e.LeftButton != MouseButtonState.Pressed) return;
            var d = e.GetPosition(card) - down;
            if (Math.Abs(d.X) + Math.Abs(d.Y) < 8) return;
            pressed = false;
            var data = new DataObject();
            if (c.Kind == ClipKind.Image && File.Exists(c.ImagePath)) data.SetFileDropList(new System.Collections.Specialized.StringCollection { c.ImagePath });
            else if (c.Kind == ClipKind.Files) { var sc = new System.Collections.Specialized.StringCollection(); sc.AddRange((c.Text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); data.SetFileDropList(sc); }
            else data.SetText(c.Text ?? "");
            _dragging = _dragOut = true;
            try { DragDrop.DoDragDrop(card, data, DragDropEffects.Copy); } catch { }
            _dragging = _dragOut = false;
        };
        card.MouseLeftButtonUp += (_, _) =>
        {
            if (!pressed) return;
            pressed = false;
            AppHost.Clips.CopyToClipboard(c);
            Flash(card);
        };

        card.ContextMenu = Menu(
            ("Copy", () => AppHost.Clips.CopyToClipboard(c)),
            (c.Kind == ClipKind.Image ? "Quick Look" : "Open in Viewer", () => { if (c.Kind == ClipKind.Image) ClipPreviewWindow.ShowImage(c.ImagePath); else ClipPreviewWindow.ShowText(c.Text); }),
            (c.Kind == ClipKind.Image ? "Extract Text (OCR)" : null, () => RunOcr(c)),
            (c.Kind == ClipKind.Image ? "Search with Google Lens" : null, () => ClipTools.SearchWithLens(c.ImagePath)),
            (c.Kind == ClipKind.Url ? "Open Link" : null, () => IconHelper.OpenUrl(c.Text.Trim())),
            (c.Kind == ClipKind.Image ? "Show in Folder" : null, () => Process.Start("explorer.exe", $"/select,\"{c.ImagePath}\"")),
            (c.Pinned ? "Unpin" : "Pin", () => AppHost.Clips.TogglePin(c)),
            ("-", null),
            ("Delete", () => AppHost.Clips.Remove(c)));
        return card;
    }

    static void Flash(Border b)
    {
        b.Background = Ui.Brush(Color.FromRgb(0x0A, 0x84, 0xFF), 0.35);
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
        t.Tick += (_, _) => { t.Stop(); b.Background = Ui.Brush(Color.FromRgb(0x1C, 0x1C, 0x1E)); };
        t.Start();
    }

    async void RunOcr(ClipItem c)
    {
        OcrText.Text = "Recognising text…";
        ShowPage(Page.Ocr);
        string text = await ClipTools.OcrAsync(c.ImagePath);
        OcrText.Text = string.IsNullOrWhiteSpace(text) ? "No text found in this image." : text;
    }

    // ----- apps -----

    void BuildApps()
    {
        AppsPanel.Children.Clear();
        foreach (var path in S.QuickAppPaths.ToList())
        {
            var tile = AppTile(IconHelper.Get(path), IconHelper.DisplayName(path), () => { IconHelper.Launch(path); Collapse(); });
            tile.ToolTip = path;
            tile.ContextMenu = Menu(
                ("Open", () => IconHelper.Launch(path)),
                ("Show in Folder", () => { var f = IconHelper.Resolve(path); if (File.Exists(f) || Directory.Exists(f)) Process.Start("explorer.exe", $"/select,\"{f}\""); }),
                ("-", null),
                ("Remove", () => { S.QuickAppPaths.Remove(path); AppHost.SaveSettings(); BuildApps(); }));
            AppsPanel.Children.Add(tile);
        }
        var add = AppTile(null, "Add", () => AppHost.OpenSettings("Widgets"));
        AppsPanel.Children.Add(add);
    }

    Border AppTile(ImageSource icon, string name, Action click)
    {
        var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (icon != null) sp.Children.Add(new Image { Source = icon, Width = 34, Height = 34 });
        else sp.Children.Add(new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(10), Background = Ui.Brush(Color.FromRgb(0x2C, 0x2C, 0x2E)), Child = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
        sp.Children.Add(new TextBlock { Text = name, FontSize = 10.5, Margin = new Thickness(0, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 68, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Ui.Brush(Color.FromArgb(0xDD, 255, 255, 255)) });
        var b = new Border { Width = 74, Height = 76, CornerRadius = new CornerRadius(16), Margin = new Thickness(1, 3, 1, 3), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = sp };
        b.MouseEnter += (_, _) => b.Background = Ui.Brush(Color.FromRgb(0x1F, 0x1F, 0x21));
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, e) => { click(); e.Handled = true; };
        return b;
    }

    void BuildQuickApps()
    {
        MQuickApps.Children.Clear();
        bool[] show = { S.ShowQuickApp1InMedia, S.ShowQuickApp2InMedia, S.ShowQuickApp3InMedia };
        for (int i = 0; i < Math.Min(3, S.QuickAppPaths.Count); i++)
        {
            if (!show[i]) continue;
            string path = S.QuickAppPaths[i];
            var b = new Button { Style = (Style)FindResource("Glyph"), Width = 30, Height = 30, ToolTip = IconHelper.DisplayName(path), Content = new Image { Source = IconHelper.Get(path), Width = 18, Height = 18 } };
            b.Click += (_, _) => IconHelper.Launch(path);
            MQuickApps.Children.Add(b);
        }
    }

    // ----- tour -----

    void RefreshTour()
    {
        var (glyph, title, desc) = Tour[_tourStep];
        TourIcon.Text = glyph;
        TourTitle.Text = title;
        TourDesc.Text = desc;
        TourStep.Text = _tourStep == 0 ? "ISLE" : $"TIP {_tourStep} OF {Tour.Length - 1}";
        TourBack.Visibility = _tourStep > 0 ? Visibility.Visible : Visibility.Collapsed;
        TourNext.Content = _tourStep == 0 ? "See Hints" : _tourStep == Tour.Length - 1 ? "Finish Tour" : "Next";
        TourSkip.Content = _tourStep == 0 ? "Start Using" : "Skip";
    }

    void EndTour()
    {
        S.HasCompletedOnboarding = true;
        AppHost.SaveSettings();
        _page = DefaultPage();
        ShowPage(_page);
        Collapse();
    }

    // =====================================================================================
    //  Popups / live-activity banners
    // =====================================================================================

    void ResetPopup()
    {
        foreach (var e in new UIElement[] { PArt, PIconWrap, PLeadText, PBattWrap, PRingWrap, PWave, PTrailText, PBarWrap, PGlyph })
            e.Visibility = Visibility.Collapsed;
        PArt.Opacity = 1;
        PTexts.Visibility = Visibility.Visible;
        PTitle.Foreground = Brushes.White;
        PSub.Visibility = Visibility.Visible;
        PTrailText.FontFamily = FontFamily;
        PTrailText.FontSize = 13;
        PLeadText.FontFamily = FontFamily;
        PLeadText.Foreground = Brushes.White;
        PopupView.Margin = new Thickness(14, 0, 14, 0);
    }

    bool PopupsAllowed => !_userHidden && !_fullscreen && _state != IslandState.Expanded;

    void ShowPopup(string kind, double height, double seconds, Page? page)
    {
        if (!PopupsAllowed) return;
        // replay the staggered entrance for a new popup, not for a live one being updated (volume ticks, battery %)
        if (_state != IslandState.Popup || kind != _popupKind) _popupInAt = _popupTrailAt = Kick(0.6);
        _popupKind = kind;
        PopupView.Visibility = Visibility.Visible;
        PopupView.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _popupW = Math.Clamp(PopupView.DesiredSize.Width + 6, height <= 40 ? 200 : 300, 470);
        if (IsSide) _popupW = Math.Min(Math.Max(_popupW, 300), (Width - (Attached ? 0 : S.TopOffset)) / S.IslandTextScale - 8);
        _popupH = height;
        _popupUntil = DateTime.Now.AddSeconds(seconds);
        _popupPage = page;
        ShowAmbient();
        if (_state == IslandState.Popup) Retarget(false); else SetState(IslandState.Popup);
    }

    void EndPopup()
    {
        _popupKind = "";
        SetState(_stealthHidden ? IslandState.Hidden : IslandState.Compact);
    }

    /// Small one-line confirmation ("Copied", "Saved to …").
    void Toast(string glyph, string text)
    {
        if (_state == IslandState.Expanded) return;
        ResetPopup();
        PLeadText.Text = glyph; PLeadText.FontFamily = (FontFamily)FindResource("Icons"); PLeadText.Visibility = Visibility.Visible;
        PTitle.Text = text; PSub.Visibility = Visibility.Collapsed;
        PopupView.Margin = new Thickness(16, 0, 16, 0);
        ShowPopup("toast", 34, 2.2, null);
    }

    void Banner(string glyph, Color color, string title, string sub, double secs, Page? page)
    {
        ResetPopup();
        PIcon.Text = glyph; PIcon.Foreground = Ui.Brush(color); PIconBg.Fill = Ui.Brush(color, 0.22);
        PIconWrap.Visibility = Visibility.Visible;
        PTitle.Text = title; PTitle.Foreground = Ui.Brush(color);
        PSub.Text = sub;
        ShowPopup("banner", 68, secs, page);
    }

    void PopupArt(MediaSnapshot m)
    {
        _partFor = m.Art;
        PArt.Background = m.Art != null ? new ImageBrush(m.Art) { Stretch = Stretch.UniformToFill } : Ui.Brush(Color.FromRgb(0x2C, 0x2C, 0x2E));
        PArt.Visibility = Visibility.Visible;
    }

    void TrackPopup(MediaSnapshot m)
    {
        if (!S.EnableTrackPopups || S.SilentDormantMode) return;
        // Quiet Feed: swallow rapid-fire short clips from the same browser (Shorts, Reels, autoplay). Real songs always announce;
        // before, skipping through a playlist stayed silent because every skip landed inside the 45 s window.
        bool feedLike = MediaService.IsBrowser(m.AppId) && m.Duration < TimeSpan.FromSeconds(90);
        if (S.QuietFeedMode && feedLike && m.AppId == _lastTrackPopupApp && DateTime.Now - _lastTrackPopupAt < TimeSpan.FromSeconds(45)) { _lastTrackPopupAt = DateTime.Now; return; }
        _lastTrackPopupApp = m.AppId;
        _lastTrackPopupAt = DateTime.Now;
        ResetPopup();
        PopupArt(m);
        PTitle.Text = m.Title;
        PSub.Text = m.Artist;
        PWave.Color = Tint;
        PWave.Visibility = Visibility.Visible;
        PopupView.Margin = new Thickness(12, 0, 18, 0);
        ShowPopup("track", 68, 3.5, Page.Media);
    }

    /// Play / pause from media keys, the player or another device: the song with a popping play or pause symbol.
    void PlayStatePopup(MediaSnapshot m)
    {
        if (!S.EnableTrackPopups || S.SilentDormantMode) return;
        ResetPopup();
        PopupArt(m);
        PArt.Opacity = m.IsPlaying ? 1 : 0.55; // a paused cover dims, as in Apple's Now Playing
        PTitle.Text = m.Title;
        PSub.Text = m.IsPlaying ? m.Artist : "Paused";
        PGlyph.Data = (Geometry)FindResource(m.IsPlaying ? "GPlay" : "GPause");
        PGlyph.Fill = PGlyph.Stroke = Ui.Brush(Tint);
        PGlyph.Visibility = Visibility.Visible;
        PopupView.Margin = new Thickness(12, 0, 22, 0);
        ShowPopup("play", 68, 2.4, Page.Media);
        _popupTrailAt = Kick(0.6); // pause → play inside one popup still pops the new symbol
    }

    void BatteryPopup(BatteryState b, string label)
    {
        ResetPopup();
        Color c = label == "Low Battery" ? Red : b.Charging || label == "Fully Charged" ? Green : Colors.White;
        PLeadText.Text = label; PLeadText.Foreground = label == "Low Battery" ? Ui.Brush(Red) : Brushes.White; PLeadText.Visibility = Visibility.Visible;
        PTexts.Visibility = Visibility.Collapsed;
        PBattText.Text = $"{b.Percent}%"; PBattText.Foreground = Ui.Brush(c);
        PBatt.Level = b.Percent / 100.0; PBatt.Charging = b.Charging; PBatt.InvalidateVisual();
        PBattWrap.Visibility = Visibility.Visible;
        PopupView.Margin = new Thickness(18, 0, 16, 0);
        PopupView.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        ShowPopup("battery", 34, 3.5, null);
        // Apple spreads the two halves apart: give the pill a wide stance
        _popupW = Math.Max(_popupW, 330);
        Retarget(false);
    }

    void LockPopup(string name, bool onState)
    {
        if (S.SilentDormantMode) return;
        ResetPopup();
        PLeadText.Text = name == "Caps Lock" ? "⇪" : "①";
        PLeadText.FontFamily = new FontFamily("Segoe UI Symbol");
        PLeadText.Visibility = Visibility.Visible;
        PTitle.Text = name; PSub.Visibility = Visibility.Collapsed;
        PTrailText.Text = onState ? "On" : "Off";
        PTrailText.Foreground = onState ? Ui.Brush(Green) : Ui.Brush(Secondary);
        PTrailText.Visibility = Visibility.Visible;
        PopupView.Margin = new Thickness(18, 0, 18, 0);
        ShowPopup("lock", 34, 1.6, null);
        _popupW = Math.Max(_popupW, 260);
        Retarget(false);
    }

    void OnVolumeChanged(float v, bool muted, bool self)
    {
        if (MVolumeRow.Visibility == Visibility.Visible) SyncVolumeRow();
        if (self || !S.EnableVolumePopup || S.SilentDormantMode) return;
        if (_state == IslandState.Popup && _popupKind != "volume" && _popupKind != "") return;
        ResetPopup();
        PLeadText.Text = VolumeGlyph(v, muted); PLeadText.FontFamily = (FontFamily)FindResource("Icons"); PLeadText.FontSize = 15; PLeadText.Visibility = Visibility.Visible;
        PTexts.Visibility = Visibility.Collapsed;
        if (_popupKind != "volume") PBar.Width = 0; // a fresh HUD fills up to the level
        _pbarTarget = 110 * (muted ? 0 : v);
        PBarWrap.Visibility = Visibility.Visible;
        PopupView.Margin = new Thickness(18, 0, 18, 0);
        ShowPopup("volume", 34, 1.5, null);
        _popupW = Math.Max(_popupW, 250);
        Retarget(false);
    }

    async void OnAudioDeviceChanged(string name)
    {
        _spectrum.Restart();
        if (!S.EnableHeadsetPopup || string.IsNullOrWhiteSpace(name) || S.SilentDormantMode) return;
        bool phones = SystemInfo.LooksLikeHeadphones(name);
        string shortName = name;
        int a = name.IndexOf('('), b = name.LastIndexOf(')');
        if (a >= 0 && b > a) shortName = name[(a + 1)..b];
        ResetPopup();
        PIcon.Text = phones ? "" : "";
        PIcon.Foreground = Brushes.White; PIconBg.Fill = Ui.Brush(Color.FromRgb(0x2C, 0x2C, 0x2E));
        PIconWrap.Visibility = Visibility.Visible;
        PTitle.Text = shortName;
        PSub.Text = "Connected";
        ShowPopup("device", 68, 4, Page.Media);
        int? batt = await SystemInfo.BluetoothBatteryAsync(name);
        if (batt is int lvl && _popupKind == "device")
        {
            PRing.Value = lvl / 100.0; PRing.Color = lvl <= 20 ? Red : Green; PRing.InvalidateVisual();
            PRingText.Text = lvl.ToString();
            PRingWrap.Visibility = Visibility.Visible;
            if (lvl <= 20) PSub.Text = "Connected · Low Battery";
            ShowPopup("device", 68, 4, Page.Media);
        }
    }

    // =====================================================================================
    //  Input wiring: clicks, wheel, drag & drop, menus
    // =====================================================================================

    void WireIsland()
    {
        Island.MouseLeftButtonDown += (_, e) =>
        {
            if (e.Handled) return;
            switch (_state)
            {
                case IslandState.Compact:
                case IslandState.Hidden:
                    if (!S.ExpandOnClickOnly || S.ExpandClickMode != "Double" || e.ClickCount >= 2) Expand();
                    e.Handled = true;
                    break;
                // Expanded: no hidden gesture here. Clicking empty space used to toggle the pin, and with a short hover
                // delay the click that was meant to open the capsule landed on the already-open island and pinned it.
                case IslandState.Popup:
                    if (_popupKind == "banner" && _popupPage == Page.Timer && !_timerActive) { EndPopup(); e.Handled = true; break; }
                    Expand(_popupPage);
                    e.Handled = true;
                    break;
            }
        };
        Bubble.MouseLeftButtonUp += (_, e) => { Expand(Page.Timer); e.Handled = true; };
        PinBtn.Click += (_, _) => TogglePin();

        Island.PreviewMouseWheel += (_, e) =>
        {
            if (_state != IslandState.Expanded) return;
            // let genuinely scrollable areas scroll; everywhere else the wheel flips tabs
            if (e.OriginalSource is DependencyObject src &&
                ((IsDescendant(ClipScroll, src) && ClipScroll.ScrollableHeight > 0) ||
                 (IsDescendant(AppsScroll, src) && AppsScroll.ScrollableHeight > 0) ||
                 (IsDescendant(LapsScroll, src) && LapsScroll.ScrollableHeight > 0) ||
                 (IsDescendant(TaskScroll, src) && TaskScroll.ScrollableHeight > 0) ||
                 (IsDescendant(NotesBox, src) && NotesBox.ExtentHeight > NotesBox.ViewportHeight) ||
                 IsDescendant(OcrText, src) || IsDescendant(VolumeBtn, src) || IsDescendant(MVolumeRow, src)))
                return;
            if ((DateTime.Now - _lastWheelTab).TotalMilliseconds < 280) { e.Handled = true; return; }
            CycleTab(e.Delta < 0 ? 1 : -1);
            _lastWheelTab = DateTime.Now;
            e.Handled = true;
        };

        // keyboard input inside the island (search, notes, timer) needs real activation
        Island.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && FindParent<TextBox>(d) != null)
            {
                Native.SetToolWindow(_hwnd, noActivate: false);
                Native.ForceForeground(_hwnd);
                Activate();
            }
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Collapse(); };



        // drag & drop onto the island
        Island.DragEnter += (_, e) =>
        {
            if (_dragOut) return;
            _dragging = true;
            if (_state != IslandState.Expanded || _page != Page.Drop) Expand(Page.Drop);
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        };
        Island.DragOver += (_, e) => { e.Effects = DragDropEffects.Copy; e.Handled = true; };
        Island.DragLeave += (_, _) => _dragging = false; // the next DragEnter (child element) sets it again
        Island.Drop += (_, e) => { _dragging = false; AcceptDrop(e.Data, toFolder: false); };
        foreach (var (zone, folder) in new[] { (DropFolder, true), (DropClips, false) })
        {
            zone.DragEnter += (_, _) => zone.BorderBrush = Ui.Brush(folder ? Blue : Green);
            zone.DragLeave += (_, _) => zone.BorderBrush = Ui.Brush(Colors.White, 0.33);
            zone.Drop += (_, e) => { zone.BorderBrush = Ui.Brush(Colors.White, 0.33); _dragging = false; AcceptDrop(e.Data, folder); e.Handled = true; };
        }
    }

    void TogglePin()
    {
        S.IsPinned = !S.IsPinned;
        AppHost.SaveSettings();
    }

    void BuildIslandMenu() => Island.ContextMenu = Menu(
        ("Settings", () => AppHost.OpenSettings()),
        (S.IsPinned ? "Unpin Island" : "Pin Island Open", TogglePin),
        ("Floating Lyrics", () => { LyricsWindow.Toggle(); _ = EnsureLyrics(); }),
        (S.EnableMusicRadar ? "Music Radar" : null, () => { Expand(Page.Media); StartRadar(); }),
        ("Hide Island", ToggleUserHidden),
        ("Replay Welcome Tour", ReplayTour),
        ("-", null),
        ("Quit", AppHost.Quit));

    void WireMisc()
    {
        TourNext.Click += (_, _) => { if (_tourStep >= Tour.Length - 1) EndTour(); else { _tourStep++; RefreshTour(); } };
        TourBack.Click += (_, _) => { if (_tourStep > 0) { _tourStep--; RefreshTour(); } };
        TourSkip.Click += (_, _) => EndTour();

        _notesSave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _notesSave.Tick += (_, _) => { _notesSave.Stop(); S.Save(); };
        NotesBox.TextChanged += (_, _) =>
        {
            NotesHint.Visibility = NotesBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (S.Notes == NotesBox.Text) return;
            S.Notes = NotesBox.Text;
            _notesSave.Stop(); _notesSave.Start();
        };

        TaskBox.TextChanged += (_, _) => TaskHint.Visibility = TaskBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        TaskBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            if (string.IsNullOrWhiteSpace(TaskBox.Text)) return;
            S.Tasks.Insert(0, new TodoItem { Text = TaskBox.Text.Trim() });
            TaskBox.Clear(); // focus stays in the box, ready for the next one
            S.Save();
            BuildTasks();
        };
    }

    void BuildTasks()
    {
        TaskList.Children.Clear();
        // Reminders-style: open tasks on top, finished ones sink to the bottom
        foreach (var t in S.Tasks.Where(t => !t.Done).Concat(S.Tasks.Where(t => t.Done)))
            TaskList.Children.Add(TaskRow(t));
        TaskEmpty.Visibility = S.Tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    Border TaskRow(TodoItem t)
    {
        var tertiary = (Brush)FindResource("Tertiary");
        var check = new Border
        {
            Width = 18, Height = 18, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1.5), Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center, ToolTip = t.Done ? "Mark as not done" : "Mark as done",
            BorderBrush = t.Done ? Ui.Brush(Accent) : tertiary, Background = t.Done ? Ui.Brush(Accent) : Brushes.Transparent,
            Child = t.Done ? new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 10, Foreground = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } : null,
        };
        check.MouseLeftButtonUp += (_, e) => { t.Done = !t.Done; S.Save(); BuildTasks(); e.Handled = true; };

        var text = new TextBlock
        {
            Text = t.Text, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 8, 0), ToolTip = t.Text,
            Foreground = t.Done ? tertiary : Brushes.White, TextDecorations = t.Done ? TextDecorations.Strikethrough : null,
        };

        var del = new Button { Style = (Style)FindResource("Glyph"), Width = 22, Height = 22, FontSize = 10, Content = "", Foreground = (Brush)FindResource("Secondary"), ToolTip = "Delete", Opacity = 0 };
        del.Click += (_, _) => { S.Tasks.Remove(t); S.Save(); BuildTasks(); };

        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 1);
        Grid.SetColumn(del, 2);
        g.Children.Add(check); g.Children.Add(text); g.Children.Add(del);

        var row = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 5, 4, 5), Background = Brushes.Transparent, Child = g };
        row.MouseEnter += (_, _) => { del.Opacity = 1; row.Background = Ui.Brush(Color.FromRgb(0x1C, 0x1C, 0x1E)); };
        row.MouseLeave += (_, _) => { del.Opacity = 0; row.Background = Brushes.Transparent; };
        row.ContextMenu = Menu(
            (t.Done ? "Mark as Not Done" : "Mark as Done", () => { t.Done = !t.Done; S.Save(); BuildTasks(); }),
            ("Copy", () => System.Windows.Clipboard.SetText(t.Text)),
            ("-", null),
            ("Delete", () => { S.Tasks.Remove(t); S.Save(); BuildTasks(); }),
            (S.Tasks.Any(x => x.Done) ? "Clear Completed" : null, () => { S.Tasks.RemoveAll(x => x.Done); S.Save(); BuildTasks(); }));
        return row;
    }

    string DropFolderPath()
    {
        string f = string.IsNullOrWhiteSpace(S.DropTargetFolderPath)
            ? IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Isle Drops")
            : S.DropTargetFolderPath;
        return f;
    }

    void AcceptDrop(IDataObject data, bool toFolder)
    {
        try
        {
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                if (toFolder)
                {
                    string dest = DropFolderPath();
                    string destFull = IOPath.GetFullPath(dest).TrimEnd('\\') + '\\';
                    if (files.Any(f => Directory.Exists(f) && destFull.StartsWith(IOPath.GetFullPath(f).TrimEnd('\\') + '\\', StringComparison.OrdinalIgnoreCase)))
                    { Collapse(); Toast("\uE7BA", "Can't save a folder into itself"); return; }
                    Directory.CreateDirectory(dest);
                    Collapse();
                    Task.Run(() => { foreach (var f in files) CopyInto(f, dest); })
                        .ContinueWith(t => Dispatcher.BeginInvoke(() =>
                            Toast(t.IsFaulted ? "\uE7BA" : "", t.IsFaulted ? "Couldn't save the dropped item" : $"Saved to {IOPath.GetFileName(dest)}")));
                }
                else { AppHost.Clips.AddFiles(files); Collapse(); Toast("", files.Length == 1 ? "Added to Clipboard Shelf" : $"{files.Length} items added to shelf"); }
                return;
            }
            if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource bmp)
            {
                AppHost.Clips.AddImage(bmp);
                Collapse(); Toast("", "Image added to shelf");
                return;
            }
            if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string text && text.Length > 0)
            {
                if (toFolder)
                {
                    string dest = DropFolderPath();
                    Directory.CreateDirectory(dest);
                    File.WriteAllText(IOPath.Combine(dest, $"drop_{DateTime.Now:yyyyMMdd_HHmmss}.txt"), text);
                    Collapse(); Toast("", $"Saved to {IOPath.GetFileName(dest)}");
                }
                else { AppHost.Clips.AddText(text); Collapse(); Toast("", "Added to Clipboard Shelf"); }
            }
        }
        catch (Exception ex) { AppHost.Log("Drop: " + ex.Message); Toast("", "Couldn't save the dropped item"); }
    }

    static void CopyInto(string src, string destDir)
    {
        if (Directory.Exists(src))
        {
            string target = Unique(IOPath.Combine(destDir, IOPath.GetFileName(src.TrimEnd('\\'))));
            Directory.CreateDirectory(target);
            foreach (var f in Directory.GetFiles(src)) File.Copy(f, IOPath.Combine(target, IOPath.GetFileName(f)));
            foreach (var d in Directory.GetDirectories(src)) CopyInto(d, target);
        }
        else if (File.Exists(src)) File.Copy(src, Unique(IOPath.Combine(destDir, IOPath.GetFileName(src))));
    }

    static string Unique(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        string dir = IOPath.GetDirectoryName(path), name = IOPath.GetFileNameWithoutExtension(path), ext = IOPath.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string p = IOPath.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(p) && !Directory.Exists(p)) return p;
        }
    }

    ContextMenu Menu(params (string text, Action action)[] items)
    {
        var menu = new ContextMenu { Style = (Style)FindResource("AppleMenu") };
        foreach (var (text, action) in items)
        {
            if (text == null) continue;
            if (text == "-") { menu.Items.Add(new Separator { Style = (Style)FindResource("AppleSeparator") }); continue; }
            var mi = new MenuItem { Header = text, Style = (Style)FindResource("AppleMenuItem") };
            mi.Click += (_, _) => action?.Invoke();
            menu.Items.Add(mi);
        }
        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) => _menuOpen = false;
        return menu;
    }

    static bool IsDescendant(DependencyObject ancestor, DependencyObject node)
    {
        while (node != null)
        {
            if (node == ancestor) return true;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    static T FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        while (d != null && d is not T) d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    static void TrySetClipboard(string text)
    {
        for (int i = 0; i < 5; i++)
        {
            try { Clipboard.SetText(text ?? ""); return; } catch { Thread.Sleep(30); }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _spectrum.Stop();
        if (_hwnd != IntPtr.Zero) Native.UnregisterHotKey(_hwnd, 1);
        base.OnClosed(e);
    }
}
