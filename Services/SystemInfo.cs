using System.Media;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Windows.Input;
using Microsoft.Win32;

namespace Isle.Services;

public readonly record struct BatteryState(bool HasBattery, int Percent, bool PluggedIn, bool Charging);

public static class SystemInfo
{
    internal static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    static SystemInfo() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("Isle/1.0");

    // ---------- battery ----------
    public static BatteryState Battery()
    {
        var ps = System.Windows.Forms.SystemInformation.PowerStatus;
        bool has = !ps.BatteryChargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.NoSystemBattery)
                   && ps.BatteryChargeStatus != System.Windows.Forms.BatteryChargeStatus.Unknown;
        int pct = (int)Math.Round(Math.Clamp(ps.BatteryLifePercent, 0f, 1f) * 100);
        bool plugged = ps.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
        bool charging = plugged && (ps.BatteryChargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.Charging) || pct < 100);
        return new BatteryState(has, has ? pct : 100, plugged || !has, charging);
    }

    // ---------- CPU / RAM / network ----------
    static ulong _idle, _total;
    static long _rx, _tx;
    static DateTime _netAt;
    public static double Cpu { get; private set; }
    public static double Ram { get; private set; }
    public static double DownBps { get; private set; }
    public static double UpBps { get; private set; }

    /// net=false skips the adapter enumeration, which allocates far more than the CPU/RAM counters
    public static void SampleStats(bool net = true)
    {
        if (Native.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            ulong total = kernel.Value + user.Value, idl = idle.Value;
            if (_total != 0 && total > _total) Cpu = 100.0 * (1 - (double)(idl - _idle) / (total - _total));
            _idle = idl; _total = total;
        }
        var m = new Native.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (Native.GlobalMemoryStatusEx(ref m)) Ram = m.dwMemoryLoad;

        if (!net) return;
        try
        {
            long rx = 0, tx = 0;
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var s = ni.GetIPStatistics();
                rx += s.BytesReceived; tx += s.BytesSent;
            }
            var now = DateTime.UtcNow;
            double dt = (now - _netAt).TotalSeconds;
            if (_netAt != default && dt > 0.2) { DownBps = Math.Max(0, (rx - _rx) / dt); UpBps = Math.Max(0, (tx - _tx) / dt); }
            _rx = rx; _tx = tx; _netAt = now;
        }
        catch { }
    }

    public static string FormatRate(double bps) =>
        bps >= 1 << 20 ? $"{bps / (1 << 20):0.0} MB/s" : bps >= 1024 ? $"{bps / 1024:0} KB/s" : $"{bps:0} B/s";

    // ---------- camera / microphone in use (privacy indicator) ----------
    static string _micKey, _micName;

    /// Mic / camera in use, plus which app holds the mic and since when (the call live activity).
    public static (bool Mic, bool Cam, string MicApp, DateTime MicSince) PrivacyInUse()
    {
        var (app, since) = InUse("microphone");
        return (app != null, InUse("webcam").App != null, app, since);
    }

    static (string App, DateTime Since) InUse(string capability)
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\" + capability);
            return root != null ? Scan(root, desktop: false) : default;
        }
        catch { return default; }

        static (string, DateTime) Scan(RegistryKey key, bool desktop)
        {
            foreach (var name in key.GetSubKeyNames())
            {
                using var sub = key.OpenSubKey(name);
                if (sub == null) continue;
                if (name == "NonPackaged") { var hit = Scan(sub, desktop: true); if (hit.Item1 != null) return hit; continue; }
                if (sub.GetValue("LastUsedTimeStop") is long stop && stop == 0 && sub.GetValue("LastUsedTimeStart") is long start && start > 0
                    && (!desktop || Running(name, DateTime.FromFileTimeUtc(start).ToLocalTime())))
                    return (AppName(name, desktop), DateTime.FromFileTimeUtc(start).ToLocalTime());
            }
            return default;
        }

        // desktop keys are exe paths ("C:#…#Discord.exe"); packaged ones are family names ("5319275A.WhatsAppDesktop_cv1g1gvanyjgm")
        static string AppName(string key, bool desktop)
        {
            if (key == _micKey) return _micName; // sampled every 2 s: read the exe's version info once per app
            _micKey = key;
            if (desktop)
            {
                string path = key.Replace('#', '\\');
                try { if (System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } d) return _micName = d; } catch { }
                return _micName = Path.GetFileNameWithoutExtension(path);
            }
            string n = key.Split('_')[0];
            n = n[(n.LastIndexOf('.') + 1)..];
            return _micName = n.EndsWith("Desktop") ? n[..^7] : n.StartsWith("MS") && n.Length > 2 ? n[2..] : n; // WhatsAppDesktop → WhatsApp, MSTeams → Teams
        }

        // a desktop app that quits or crashes mid-call never writes its stop time, so its entry reads "in use"
        // forever: trust it only while that exe is running AND was already running when the mic opened. A name
        // match alone isn't enough: Discord relaunched (or updated into a new app-1.0.x folder) after a crash
        // revived a 2-year-old entry as a 17652-hour call. Key names are exe paths with '#' for '\'.
        static bool Running(string key, DateTime since)
        {
            bool hit = false;
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(key.Replace('#', '\\'))))
            {
                try { hit |= p.StartTime <= since; } catch { hit = true; } // elevated/protected processes hide their start time
                p.Dispose();
            }
            return hit;
        }
    }

    // ---------- weather (Open-Meteo, no API key) ----------
    public sealed record Weather(double TempC, int Code, string Place);

    public static async Task<Weather> GetWeatherAsync(string city)
    {
        try
        {
            double lat, lon; string place;
            if (string.IsNullOrWhiteSpace(city))
            {
                using var ip = JsonDocument.Parse(await Http.GetStringAsync("http://ip-api.com/json/?fields=lat,lon,city"));
                lat = ip.RootElement.GetProperty("lat").GetDouble();
                lon = ip.RootElement.GetProperty("lon").GetDouble();
                place = ip.RootElement.GetProperty("city").GetString();
            }
            else
            {
                using var geo = JsonDocument.Parse(await Http.GetStringAsync(
                    "https://geocoding-api.open-meteo.com/v1/search?count=1&name=" + Uri.EscapeDataString(city.Trim())));
                var r = geo.RootElement.GetProperty("results")[0];
                lat = r.GetProperty("latitude").GetDouble();
                lon = r.GetProperty("longitude").GetDouble();
                place = r.GetProperty("name").GetString();
            }
            string url = FormattableString.Invariant($"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}&current=temperature_2m,weather_code");
            using var w = JsonDocument.Parse(await Http.GetStringAsync(url));
            var cur = w.RootElement.GetProperty("current");
            return new Weather(cur.GetProperty("temperature_2m").GetDouble(), cur.GetProperty("weather_code").GetInt32(), place);
        }
        catch (Exception ex) { AppHost.Log("Weather: " + ex.Message); return null; }
    }

    /// Monochrome weather symbol (Segoe UI Symbol) for a WMO weather code — SF Symbols style.
    public static string WeatherGlyph(int code) => code switch
    {
        0 => "☀",                          // sun
        1 or 2 => "⛅",                     // sun behind cloud
        3 or 45 or 48 => "☁",              // cloud / fog
        >= 51 and <= 67 or >= 80 and <= 82 => "☂", // rain
        >= 71 and <= 77 or 85 or 86 => "❄",        // snow
        >= 95 => "⚡",                      // thunder
        _ => "☁",
    };

    // ---------- Bluetooth battery ----------
    /// Every connected Bluetooth device that reports a battery level. Windows keeps the last level on a device after
    /// it disconnects, so only devices whose Dev_ node says connected count; the level itself often sits on a child
    /// node (Hands-Free) that carries the same address.
    public static async Task<List<(string Name, int Percent)>> BluetoothBatteriesAsync()
    {
        var list = new List<(string, int)>();
        try
        {
            const string battKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2", connKey = "{83DA6326-97A6-4088-9453-A1923F573B29} 15";
            var devs = await Windows.Devices.Enumeration.Pnp.PnpObject.FindAllAsync(
                Windows.Devices.Enumeration.Pnp.PnpObjectType.Device,
                new[] { "System.ItemNameDisplay", battKey, connKey },
                "System.Devices.DeviceInstanceId:~<\"BTH\"");
            foreach (var d in devs)
            {
                // BTHENUM\Dev_1406A7A22F66\… (classic) or BTHLE\Dev_ac8ebd3e1489\… (LE)
                int at = d.Id.IndexOf("\\Dev_", StringComparison.OrdinalIgnoreCase);
                if (at < 0 || d.Id.Length < at + 17 || !(d.Properties.TryGetValue(connKey, out var c) && c is true)) continue;
                string addr = d.Id.Substring(at + 5, 12);
                foreach (var x in devs)
                    if (x.Properties.TryGetValue(battKey, out var v) && v is byte level && x.Id.Contains(addr, StringComparison.OrdinalIgnoreCase))
                    {
                        list.Add((d.Properties.TryGetValue("System.ItemNameDisplay", out var n) && n is string dn ? dn : "Bluetooth", level));
                        break;
                    }
            }
        }
        catch (Exception ex) { AppHost.Log("BT battery: " + ex.Message); }
        return list;
    }

    /// Battery of the connected Bluetooth device behind an audio endpoint, e.g. "Headphones (AirPods Pro)".
    public static async Task<int?> BluetoothBatteryAsync(string endpointName)
    {
        string name = endpointName;
        int a = name.IndexOf('('), b = name.LastIndexOf(')');
        if (a >= 0 && b > a) name = name[(a + 1)..b];
        name = name.Replace(" Stereo", "").Replace(" Hands-Free AG Audio", "").Replace(" Hands-Free", "").Trim();
        foreach (var (dn, level) in await BluetoothBatteriesAsync())
            if (dn.Contains(name, StringComparison.OrdinalIgnoreCase) || name.Contains(dn, StringComparison.OrdinalIgnoreCase))
                return level;
        return null;
    }

    public static bool LooksLikeHeadphones(string name)
    {
        string l = name.ToLowerInvariant();
        return new[] { "airpod", "bud", "headphone", "headset", "earphone", "in-ear", "over-ear", "on-ear", "bose", "quietcomfort", "wh-", "wf-", "beats", "jabra", "galaxy" }
            .Any(l.Contains);
    }

    // ---------- hotkey ----------
    public static bool TryParseHotkey(string text, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= Native.MOD_CONTROL; break;
                case "alt": mods |= Native.MOD_ALT; break;
                case "shift": mods |= Native.MOD_SHIFT; break;
                case "win": case "windows": mods |= Native.MOD_WIN; break;
                default:
                    string k = raw.Length == 1 && char.IsDigit(raw[0]) ? "D" + raw : raw;
                    if (Enum.TryParse<Key>(k, true, out var key)) vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                    break;
            }
        }
        return vk != 0;
    }

    // ---------- fullscreen detection ----------
    public static bool IsForegroundFullscreen(IntPtr ownWindow)
    {
        IntPtr fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == ownWindow || fg == Native.GetShellWindow() || fg == Native.GetDesktopWindow()) return false;
        string cls = Native.ClassOf(fg);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "XamlExplorerHostIslandWindow"
            or "Windows.UI.Core.CoreWindow" or "TopLevelWindowForOverflowXamlIsland") return false;
        if (!Native.GetWindowRect(fg, out var r)) return false;
        var mon = Native.MonitorFromWindow(fg, 2);
        if (mon != Native.MonitorFromWindow(ownWindow, 2)) return false; // fullscreen on another display doesn't concern us
        var mi = new Native.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFOEX>() };
        if (!Native.GetMonitorInfo(mon, ref mi)) return false;
        var m = mi.rcMonitor;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    // ---------- Game Mode: an app that wants the screen to itself is in front ----------
    static HashSet<string> _games;
    static DateTime _gamesAt;
    static uint _lastPid;
    static string _lastApps;
    static bool _lastHit;

    /// A game Windows recognises (Game Bar's list, so windowed and borderless games count too), exclusive-fullscreen
    /// Direct3D, presentation mode, or an app from the user's list. Any monitor: they all want undivided attention.
    public static bool IsFocusAppInFront(string userApps)
    {
        if (Native.SHQueryUserNotificationState(out int q) == 0 && q is 3 or 4) return true; // QUNS_RUNNING_D3D_FULL_SCREEN, QUNS_PRESENTATION_MODE
        Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint pid);
        if (pid == 0 || pid == Environment.ProcessId) return false;
        var games = KnownGames();
        if (pid == _lastPid && userApps == _lastApps) return _lastHit; // checked every second: decide once per foreground process
        _lastPid = pid;
        _lastApps = userApps;
        return _lastHit = IsFocusProcess(pid, userApps, games);
    }

    static bool IsFocusProcess(uint pid, string userApps, HashSet<string> games)
    {
        string path = Native.ProcessPathOf(pid), name;
        if (path != null) name = Path.GetFileNameWithoutExtension(path);
        else try { name = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { return false; }

        foreach (var raw in (userApps ?? "").Split(',', ';'))
        {
            string app = raw.Trim();
            if (app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) app = app[..^4];
            if (app.Length > 0 && app.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        // no path (access denied): fall back to the exe name
        return path != null ? games.Contains(path) : games.Any(g => Path.GetFileNameWithoutExtension(g).Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    static HashSet<string> KnownGames()
    {
        if (_games != null && DateTime.UtcNow - _gamesAt < TimeSpan.FromMinutes(2)) return _games;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore\Children");
            foreach (var id in root?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                using var k = root.OpenSubKey(id);
                if (k?.GetValue("MatchedExeFullPath") is string p && p.Length > 0) set.Add(p);
            }
        }
        catch { }
        _gamesAt = DateTime.UtcNow;
        _lastPid = 0; // Game Bar may have just recognised the running game: decide again
        return _games = set;
    }

    // ---------- timer chime (synthesised, Apple "Radial"-like triple tone) ----------
    static byte[] _chime;
    public static void PlayChime()
    {
        try
        {
            _chime ??= BuildChime();
            using var ms = new MemoryStream(_chime);
            new SoundPlayer(ms).Play();
        }
        catch { SystemSounds.Asterisk.Play(); }
    }

    static byte[] BuildChime()
    {
        const int rate = 44100;
        double[] notes = { 1318.5, 1760.0, 2093.0, 1318.5, 1760.0, 2093.0 };
        int noteLen = (int)(rate * 0.16), gap = (int)(rate * 0.04);
        var samples = new List<short>();
        for (int n = 0; n < notes.Length; n++)
        {
            for (int i = 0; i < noteLen; i++)
            {
                double t = (double)i / rate;
                double env = Math.Min(1, i / (rate * 0.005)) * Math.Exp(-t * 14);
                double v = Math.Sin(2 * Math.PI * notes[n] * t) * 0.7 + Math.Sin(4 * Math.PI * notes[n] * t) * 0.15;
                samples.Add((short)(v * env * 12000));
            }
            samples.AddRange(Enumerable.Repeat((short)0, n == 2 ? rate / 3 : gap));
        }
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int dataLen = samples.Count * 2;
        w.Write("RIFF"u8); w.Write(36 + dataLen); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataLen);
        foreach (var s in samples) w.Write(s);
        return ms.ToArray();
    }
}

/// Detects a vigorous horizontal back-and-forth of the cursor.
public sealed class ShakeDetector
{
    readonly Queue<(DateTime t, int dir)> _reversals = new();
    int _lastX = int.MinValue, _dir, _travel;
    DateTime _cooldown;

    public bool Feed(int x, string sensitivity)
    {
        var now = DateTime.UtcNow;
        if (_lastX == int.MinValue) { _lastX = x; return false; }
        int dx = x - _lastX;
        _lastX = x;
        if (dx == 0) return false;
        int dir = Math.Sign(dx);
        (int minTravel, int needed) = sensitivity switch { "High" => (40, 4), "Low" => (120, 6), _ => (70, 5) };
        if (dir == _dir) { _travel += Math.Abs(dx); return false; }
        if (_travel >= minTravel) _reversals.Enqueue((now, dir));
        _dir = dir; _travel = Math.Abs(dx);
        while (_reversals.Count > 0 && (now - _reversals.Peek().t).TotalMilliseconds > 900) _reversals.Dequeue();
        if (_reversals.Count >= needed && now > _cooldown)
        {
            _reversals.Clear();
            _cooldown = now.AddSeconds(1.2);
            return true;
        }
        return false;
    }
}
