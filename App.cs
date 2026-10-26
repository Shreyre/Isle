using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Isle.Services;

namespace Isle;

/// Process-wide state shared by the island, settings window and services.
public static class AppHost
{
    public static AppSettings Settings { get; private set; }
    public static MainWindow Island { get; private set; }
    public static ClipboardStore Clips { get; private set; }
    public static string DataDir => AppSettings.DataDir;

    /// Raised on the UI thread after any setting changes.
    public static event Action SettingsChanged;

    static DispatcherTimer _saveTimer;
    static SettingsWindow _settingsWindow;
    static System.Windows.Forms.NotifyIcon _tray;

    [STAThread]
    public static void Main()
    {
        using var mutex = new Mutex(true, @"Local\Isle_SingleInstance", out bool first);
        if (!first)
        {
            // Second launch: hand our foreground right to the running instance and ask it to open Settings.
            Native.AllowSetForegroundWindow(-1);
            try { using var ev = EventWaitHandle.OpenExisting(@"Local\Isle_OpenSettings"); ev.Set(); } catch { }
            return;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) => { Log("UI exception: " + e.Exception); e.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("Fatal: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log("Task: " + e.Exception); e.SetObserved(); };

        Settings = AppSettings.Load();
        Settings.Save();
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Settings.Save(); };

        // Build UI inside Startup so the WPF SynchronizationContext exists and every await resumes on the UI thread.
        app.Startup += (_, _) =>
        {
            Clips = new ClipboardStore(Path.Combine(DataDir, "Clipboard"));
            Island = new MainWindow();
            Island.Show();
            CreateTray();
            ApplyStartup();
            SettingsChanged += () => { if (Settings.LaunchOnStartup != _startupApplied) ApplyStartup(); };

            // Trim shortly after start-up (start-up allocations are the bulk) and then every few minutes while idle.
            var trim = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(12) };
            trim.Tick += (_, _) =>
            {
                trim.Interval = TimeSpan.FromMinutes(3);
                if (!Island.IsBusy) TrimMemory();
            };
            trim.Start();
        };
        // Software rendering skips loading Direct3D and the GPU driver stack: committed memory roughly halves
        // (≈150 → ≈70 MB measured) and a layered window needs a CPU copy of every frame anyway.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        var openEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Isle_OpenSettings");
        new Thread(() => { while (openEvent.WaitOne()) app.Dispatcher.InvokeAsync(() => OpenSettings()); }) { IsBackground = true }.Start();

        app.Run();
        if (_tray != null) _tray.Visible = false;
        Settings.Save();
    }

    /// Persist (debounced) and notify listeners immediately.
    public static void SaveSettings()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
        SettingsChanged?.Invoke();
    }

    public static void OpenSettings(string section = null)
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow();
            _settingsWindow.Closed += (_, _) =>
            {
                _settingsWindow = null;
                Application.Current.Dispatcher.InvokeAsync(TrimMemory, DispatcherPriority.ApplicationIdle);
            };
            _settingsWindow.Show();
        }
        if (section != null) _settingsWindow.Navigate(section);
        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
        // we're usually a background process (tray / island click), so push through focus-stealing prevention
        Native.ForceForeground(new System.Windows.Interop.WindowInteropHelper(_settingsWindow).Handle);
    }

    public static void Quit() => Application.Current.Shutdown();

    /// Compact the managed heap and hand untouched pages back to Windows. The island idles most of its life,
    /// so this keeps its resident memory small; pages fault back in cheaply if they're needed again.
    public static void TrimMemory()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        Native.SetProcessWorkingSetSize(new IntPtr(-1), new IntPtr(-1), new IntPtr(-1));
    }

    static bool _startupApplied;

    public static void ApplyStartup()
    {
        _startupApplied = Settings.LaunchOnStartup;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            key?.DeleteValue("DynamicEdgeCopy", false); // entry from before the rename to Isle
            if (Settings.LaunchOnStartup) key?.SetValue("Isle", $"\"{Environment.ProcessPath}\"");
            else key?.DeleteValue("Isle", false);
        }
        catch (Exception ex) { Log("Startup registry: " + ex.Message); }
    }

    /// True when Windows apps are set to dark mode (used for AppTheme = System).
    public static bool IsDarkTheme()
    {
        if (Settings.AppTheme == "Dark") return true;
        if (Settings.AppTheme == "Light") return false;
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return true; }
    }

    public static Color ParseColor(string hex, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); } catch { return fallback; }
    }

    static readonly object LogLock = new();
    public static void Log(string msg)
    {
        try
        {
            lock (LogLock)
            {
                string path = Path.Combine(DataDir, "app.log");
                if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024) File.Delete(path);
                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}{Environment.NewLine}");
            }
        }
        catch { }
    }

    static void CreateTray()
    {
        System.Drawing.Icon icon;
        try { icon = new System.Drawing.Icon(Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico")).Stream); }
        catch { icon = System.Drawing.SystemIcons.Application; }

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Show / Hide Island", null, (_, _) => Island.ToggleUserHidden());
        menu.Items.Add("Settings", null, (_, _) => OpenSettings());
        menu.Items.Add("Replay Welcome Tour", null, (_, _) => Island.ReplayTour());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Quit Isle", null, (_, _) => Quit());

        _tray = new System.Windows.Forms.NotifyIcon { Icon = icon, Text = "Isle", ContextMenuStrip = menu, Visible = true };
        _tray.MouseClick += (_, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) OpenSettings(); };
    }
}
