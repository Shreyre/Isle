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
    enum Page { Media, Timer, Clipboard, Apps, Notes, Ocr, Drop, Tour }
    static readonly Page[] TabOrder = { Page.Media, Page.Timer, Page.Clipboard, Page.Apps, Page.Notes };
    static readonly string[] TabGlyph = { "", "", "", "", "" };
    static readonly string[] TabName = { "Media", "Timer", "Clipboard", "Apps", "Notes" };

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
        ("", "Swipe Between Tabs", "Roll the mouse wheel or swipe two fingers on the touchpad over the open island to move between Media, Timer, Clipboard, Apps and Notes."),
        ("", "Clipboard Shelf", "Everything you copy is kept: search it, filter by type, pin favourites, drag clips out, extract text from images or look them up with Google Lens."),
        ("", "Timers, Stopwatch & Focus", "Start a countdown from a preset, run a stopwatch with laps, or stay in flow with Pomodoro focus rounds — all as live activities."),
        ("", "Apps, Folders & Drops", "Pin apps and folders for one-click launching, and drop files on the island to save them to a folder or the clipboard shelf."),
        ("", "Magic Shake & Docking", "Shake the cursor to summon the island, press Alt+H to hide it, and dock it to any edge or corner in Settings."),
    };

    DispatcherTimer _notesSave;
    readonly DateTime _startedAt = DateTime.Now;
    Path _playIcon, _pauseIcon;

    public MainWindow()
    {
        InitializeComponent();
        _playIcon = TransportIcon("GPlay", 19, 22);
        _pauseIcon = TransportIcon("GPause", 17, 21);
        PlayBtn.Content = _playIcon;
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

    bool IsCursorOver() => Inflate(_islandRect, 10).Contains(CursorDip());

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
        PinMark.Visibility = S.IsPinned ? Visibility.Visible : Visibility.Collapsed;
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
            if (S.TabMediaEnabled) l.Add(Page.Media);
            if (S.TabTimerEnabled) l.Add(Page.Timer);
            if (S.TabClipboardEnabled) l.Add(Page.Clipboard);
            if (S.TabQuickLaunchEnabled) l.Add(Page.Apps);
            if (S.TabNotesEnabled) l.Add(Page.Notes);
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
                if (AsLine) return IsSide ? (t, Math.Max(100, _compactH * 0.9), t / 2) : (Math.Max(120, _compactW * 0.85), t, t / 2);
                double rc = S.DockMode == "Notch" ? Math.Min(_compactH / 2, 12) : Math.Min(_compactW, _compactH) / 2;
                return (_compactW, _compactH, rc);
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
        bool dots = S.TabNavigationStyle == "Dots" && IsTab(p) && EnabledTabs.Count > 1;
        bool header = p is not (Page.Drop or Page.Tour);
        header = header && S.TabNavigationStyle == "Header";
        double chrome = (header ? 38 : 16) + 6 + (dots || S.IsPinned ? 15 : 10);
        return p switch
        {
            Page.Media => (420, chrome + 136 + (LyricsShownInPage ? 62 : 0)),
            Page.Timer => (440, chrome + 118),
            Page.Clipboard => (440, chrome + 290),
            Page.Apps => (420, chrome + 8 + Math.Clamp((int)Math.Ceiling((S.QuickAppPaths.Count + 1) / 5.0), 1, 3) * 82),
            Page.Notes => (420, chrome + 170),
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
        _opIsland = Ui.Ease(_opIsland, visible ? 1 : 0, visible ? 14 : 10, dt);

        ApplyFrame();

        bool settled = _w.Settled && _h.Settled && _r.Settled && _pendingW == null && _pendingH == null
                       && Done(_opCompact, showC) && Done(_opExpanded, showE) && Done(_opPopup, showP)
                       && Math.Abs(_opIsland - (visible ? 1 : 0)) < 0.01;
        if (settled)
        {
            _w.Value = _w.Target; _h.Value = _h.Target; _r.Value = _r.Target;
            _opIsland = visible ? 1 : 0;
            ApplyFrame();
            CompositionTarget.Rendering -= OnRendering;
            _animating = false;
        }

        static double Fade(double v, bool on, double dt) => on ? Math.Min(1, v + dt * 7) : Math.Max(0, v - dt * 14);
        static bool Done(double v, bool on) => on ? v >= 1 : v <= 0;
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
        Shape.Data = BuildShape(left, top, w * s, h * s, r * s);
        Shape.Opacity = Island.Opacity = _opIsland;
        Island.IsHitTestVisible = _opIsland > 0.5;

        // content layers
        Layer(CompactView, _opCompact);
        Layer(PopupView, _opPopup);
        Layer(ExpandedView, _opExpanded);
        double ease = 1 - Math.Pow(1 - _opExpanded, 3);
        ExpandedScale.ScaleX = ExpandedScale.ScaleY = 0.94 + 0.06 * ease;

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
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            if (S.EnablePrivacyIndicator) _privacy = SystemInfo.PrivacyInUse(); else _privacy = default;
        }
        if (_ticks % 45 == 0 && S.IdleWidgetMode == "SystemStats") SystemInfo.SampleStats();
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
            over = Inflate(_islandRect, 14).Contains(p) || _bubbleRect.Contains(p) || Mouse.Captured != null || _dragging || _menuOpen;
        else
            over = Inflate(_islandRect, 4).Contains(p) || _bubbleRect.Contains(p) || HotRect().Contains(p);

        if (over) { _outSince = null; _overSince ??= now; }
        else { _overSince = null; _outSince ??= now; }
        double overFor = _overSince.HasValue ? (now - _overSince.Value).TotalSeconds : 0;
        double outFor = _outSince.HasValue ? (now - _outSince.Value).TotalSeconds : 0;
        double dwell = S.DwellTimeMs / 1000.0;

        // auto-collapse to thin line when idle
        bool wantLine = S.AutoHideToEdge && !HasActivity && !over && outFor > 1.5 && _state == IslandState.Compact;
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
                else if (!over && S.AutoHideWhenIdle && !HasActivity && outFor > 1.5) { _stealthHidden = true; SetState(IslandState.Hidden); }
                break;
            case IslandState.Expanded:
                bool keep = S.IsPinned || _page == Page.Tour || (IsActive && Keyboard.FocusedElement is TextBox);
                if (!over && !keep && outFor >= S.GraceTimeMs / 1000.0) Collapse();
                break;
            case IslandState.Popup:
                if (over && !_suppressHover && !S.ExpandOnClickOnly && overFor >= dwell + 0.35 && _popupPage != null) Expand(_popupPage);
                else if (now > _popupUntil && !over) EndPopup();
                break;
        }
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
        _fullscreenRaw = SystemInfo.IsForegroundFullscreen(_hwnd);
        bool fs = S.AutoHideInFullscreen && _fullscreenRaw;
        if (fs == _fullscreen) return;
        _fullscreen = fs;
        if (fs && _state != IslandState.Compact) SetState(IslandState.Compact);
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

    string _lastClockText;
    ImageSource _cartFor;

    void UpdateCompact(DateTime now, double dt, Point cursor)
    {
        UIElement[] all = { CArt, CLeadIcon, CBatt, CText, CDots, CBold, CEyes, CDot, CGap, CWave, CTrailText, CRing };
        var on = new HashSet<UIElement>();
        double h = 34;
        bool side = IsSide;
        var m = _media.Current;
        _bubbleOn = false;

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
            _lastClockText = null;
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
        if (side && mode is "Clock" or "TrackInfo" or "SystemStats" or "Weather") mode = "SideClock";

        switch (mode)
        {
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
                on.Add(CDot);
                break;
            case "AnimatedEyes":
            case "WhiteEyeballs":
                CEyes.WhiteStyle = mode == "WhiteEyeballs";
                CEyes.Accent = Accent;
                var ctr = new Point(_islandRect.X + _islandRect.Width / 2, _islandRect.Y + _islandRect.Height / 2);
                CEyes.Look(new Vector((cursor.X - ctr.X) / 300, (cursor.Y - ctr.Y) / 200));
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
                        on.Add(CDots);
                        break;
                    default:
                        string txt = $"{now:ddd d MMM}|{(S.Use12HourClock ? now.ToString("h:mm tt") : time)}";
                        if (txt != _lastClockText)
                        {
                            _lastClockText = txt;
                            var parts = txt.Split('|');
                            CText.Inlines.Clear();
                            CText.Inlines.Add(new System.Windows.Documents.Run(parts[0]) { Foreground = Ui.Brush(Secondary) });
                            CText.Inlines.Add(new System.Windows.Documents.Run("   " + parts[1]) { Foreground = Brushes.White });
                        }
                        CText.MaxWidth = 300;
                        on.Add(CText);
                        return;
                }
                break;
        }
        _lastClockText = null;
    }

    // =====================================================================================
    //  Pages (expanded)
    // =====================================================================================

    void ShowPage(Page p)
    {
        if (IsTab(p) && !EnabledTabs.Contains(p)) p = EnabledTabs[0];
        _page = p;
        if (IsTab(p)) { int idx = Array.IndexOf(TabOrder, p); if (S.LastActiveTab != idx) { S.LastActiveTab = idx; } }
        foreach (var (g, pg) in new (Grid, Page)[] { (PageMedia, Page.Media), (PageTimer, Page.Timer), (PageClipboard, Page.Clipboard), (PageApps, Page.Apps), (PageNotes, Page.Notes), (PageOcr, Page.Ocr), (PageDrop, Page.Drop), (PageTour, Page.Tour) })
            g.Visibility = pg == p ? Visibility.Visible : Visibility.Collapsed;
        Header.Visibility = IsTab(p) && S.TabNavigationStyle == "Header" ? Visibility.Visible : Visibility.Collapsed;
        Pages.Margin = new Thickness(18, Header.Visibility == Visibility.Visible ? 2 : 16, 18, 6);
        var (w, h) = PageSize(p);
        ExpandedView.Width = w;
        ExpandedView.Height = h;
        UpdateTabChrome();
        switch (p)
        {
            case Page.Media: RefreshMediaPage(); break;
            case Page.Timer: RefreshTimerPage(); break;
            case Page.Clipboard: _clipShown = 30; RebuildClips(); break;
            case Page.Apps: BuildApps(); break;
            case Page.Notes: NotesBox.Text = S.Notes; NotesHint.Visibility = string.IsNullOrEmpty(S.Notes) ? Visibility.Visible : Visibility.Collapsed; break;
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
            var dot = new Ellipse { Width = 6, Height = 6, Margin = new Thickness(3, 0, 3, 0), Tag = tab, Cursor = Cursors.Hand };
            dot.MouseLeftButtonUp += (_, _) => ShowPage(tab);
            Dots.Children.Add(dot);
        }
        UpdateTabChrome();
    }

    void UpdateTabChrome()
    {
        bool tab = IsTab(_page);
        TabIcons.Visibility = tab && S.TabNavigationStyle == "Header" ? Visibility.Visible : Visibility.Collapsed;
        Dots.Visibility = tab && S.TabNavigationStyle == "Dots" && EnabledTabs.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        PinMark.Visibility = S.IsPinned ? Visibility.Visible : Visibility.Collapsed;
        DotsRow.Visibility = Dots.Visibility == Visibility.Visible || S.IsPinned ? Visibility.Visible : Visibility.Collapsed;
        foreach (Button b in TabIcons.Children) b.Foreground = (Page)b.Tag == _page ? Brushes.White : (Brush)FindResource("Tertiary");
        foreach (Ellipse d in Dots.Children) d.Fill = (Page)d.Tag == _page ? Brushes.White : Ui.Brush(Colors.White, 0.3);
    }

    void CycleTab(int dir)
    {
        var tabs = EnabledTabs;
        if (!IsTab(_page) || tabs.Count < 2) return;
        int i = tabs.IndexOf(_page);
        ShowPage(tabs[(i + dir + tabs.Count) % tabs.Count]);
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
            case Page.Media: UpdateMediaLive(dt); break;
            case Page.Timer: UpdateTimerLive(); break;
        }
    }

    // ----- media -----

    void WireMedia()
    {
        PlayBtn.Click += (_, _) => { _media.PlayPause(); RefreshMediaPage(); };
        PrevBtn.Click += (_, _) => _media.Previous();
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
        MControls.Visibility = show ? Visibility.Hidden : Visibility.Visible;
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
            if (m.HasMedia && m.IsPlaying && DateTime.Now - _startedAt > TimeSpan.FromSeconds(4)) TrackPopup(m);
        }
        if (_state == IslandState.Expanded && _page == Page.Media) RefreshMediaPage();
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
        if (m.Art != null) { MArt.Background = new ImageBrush(m.Art) { Stretch = Stretch.UniformToFill }; MArtPlaceholder.Visibility = Visibility.Collapsed; }
        else { MArt.Background = Ui.Brush(Color.FromRgb(0x2C, 0x2C, 0x2E)); MArtPlaceholder.Visibility = Visibility.Visible; }
        PlayBtn.Content = m.IsPlaying ? _pauseIcon : _playIcon;
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
        PlayBtn.Content = m.IsPlaying ? _pauseIcon : _playIcon;
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

    static void PulseRing(Ellipse e, double t)
    {
        e.Width = e.Height = 44 + 26 * t;
        e.Opacity = 1 - t;
    }

    ImageSource _ambientFor;
    BitmapSource _ambientSmall;

    void ShowAmbient()
    {
        var m = _media.Current;
        bool on = S.EnableMediaBackdrop && m.Art != null &&
                  ((_state == IslandState.Expanded && _page == Page.Media) || (_state == IslandState.Popup && _popupKind == "track"));
        if (on && _ambientFor != m.Art)
        {
            _ambientFor = m.Art;
            var src = (BitmapSource)m.Art;
            _ambientSmall = new TransformedBitmap(src, new ScaleTransform(16.0 / src.PixelWidth, 16.0 / src.PixelHeight));
            _ambientSmall.Freeze();
        }
        Ambient.Source = on ? _ambientSmall : null;
        Ambient.Opacity = on ? 0.75 : 0;
        AmbientShade.Opacity = on ? 1 : 0;
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
        foreach (var e in new UIElement[] { PArt, PIconWrap, PLeadText, PBattWrap, PRingWrap, PWave, PTrailText, PBarWrap })
            e.Visibility = Visibility.Collapsed;
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

    void TrackPopup(MediaSnapshot m)
    {
        if (!S.EnableTrackPopups || S.SilentDormantMode) return;
        // Quiet Feed: swallow rapid-fire changes from the same app (Shorts, Reels, autoplay)
        if (S.QuietFeedMode && m.AppId == _lastTrackPopupApp && DateTime.Now - _lastTrackPopupAt < TimeSpan.FromSeconds(45)) { _lastTrackPopupAt = DateTime.Now; return; }
        _lastTrackPopupApp = m.AppId;
        _lastTrackPopupAt = DateTime.Now;
        ResetPopup();
        PArt.Background = m.Art != null ? new ImageBrush(m.Art) { Stretch = Stretch.UniformToFill } : Ui.Brush(Color.FromRgb(0x2C, 0x2C, 0x2E));
        PArt.Visibility = Visibility.Visible;
        PTitle.Text = m.Title;
        PSub.Text = m.Artist;
        PWave.Color = Tint;
        PWave.Visibility = Visibility.Visible;
        PopupView.Margin = new Thickness(12, 0, 18, 0);
        ShowPopup("track", 68, 3.5, Page.Media);
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
        PBar.Width = 110 * (muted ? 0 : v);
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
                case IslandState.Expanded:
                    // "Click Pin": clicking empty island space locks it open until clicked again
                    if (e.OriginalSource == Island && IsTab(_page))
                    {
                        S.IsPinned = !S.IsPinned;
                        AppHost.SaveSettings();
                        e.Handled = true;
                    }
                    break;
                case IslandState.Popup:
                    if (_popupKind == "banner" && _popupPage == Page.Timer && !_timerActive) { EndPopup(); e.Handled = true; break; }
                    Expand(_popupPage);
                    e.Handled = true;
                    break;
            }
        };
        Bubble.MouseLeftButtonUp += (_, e) => { Expand(Page.Timer); e.Handled = true; };

        Island.PreviewMouseWheel += (_, e) =>
        {
            if (_state != IslandState.Expanded) return;
            // let genuinely scrollable areas scroll; everywhere else the wheel flips tabs
            if (e.OriginalSource is DependencyObject src &&
                ((IsDescendant(ClipScroll, src) && ClipScroll.ScrollableHeight > 0) ||
                 (IsDescendant(AppsScroll, src) && AppsScroll.ScrollableHeight > 0) ||
                 (IsDescendant(LapsScroll, src) && LapsScroll.ScrollableHeight > 0) ||
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

    void BuildIslandMenu() => Island.ContextMenu = Menu(
        ("Settings", () => AppHost.OpenSettings()),
        (S.IsPinned ? "Unpin Island" : "Pin Island Open", () => { S.IsPinned = !S.IsPinned; AppHost.SaveSettings(); }),
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
