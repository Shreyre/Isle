using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using WinRT;

namespace Isle.Services;

public sealed class MediaSnapshot
{
    public string Title = "", Artist = "", Album = "", AppId = "";
    public bool IsPlaying;
    public TimeSpan Position, Duration;
    public DateTimeOffset PositionUpdated = DateTimeOffset.Now;
    public ImageSource Art;
    public ulong ArtHash;   // hash of the thumbnail bytes, to tell a fresh cover from a re-sent stale one
    public Color ArtColor = Colors.White;
    public bool CanSeek;

    public bool HasMedia => !string.IsNullOrWhiteSpace(Title);
    public string TrackKey => $"{AppId}|{Title}|{Artist}";
    public string AppName => MediaService.FriendlyAppName(AppId);

    public TimeSpan LivePosition
    {
        get
        {
            if (!IsPlaying) return Position;
            var p = Position + (DateTimeOffset.Now - PositionUpdated);
            if (Duration > TimeSpan.Zero && p > Duration) p = Duration;
            return p < TimeSpan.Zero ? TimeSpan.Zero : p;
        }
    }
}

/// Polls the Windows media session manager (Spotify, browsers, Apple Music, Media Player, VLC…).
public sealed class MediaService
{
    GlobalSystemMediaTransportControlsSessionManager _mgr;
    string _pinnedAppId;   // set when the user cycles sessions with the source badge
    bool _busy;
    DateTime _trackChangedAt;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(700) };

    public MediaSnapshot Current { get; private set; } = new();
    public int SessionCount { get; private set; }

    /// (snapshot, trackChanged)
    public event Action<MediaSnapshot, bool> Updated;

    public async void Start()
    {
        try { _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(); }
        catch (Exception ex) { AppHost.Log("SMTC unavailable: " + ex.Message); return; }
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    GlobalSystemMediaTransportControlsSession PickSession()
    {
        var sessions = _mgr.GetSessions();
        SessionCount = sessions.Count;
        if (sessions.Count == 0) return null;
        if (_pinnedAppId != null)
        {
            var pinned = sessions.FirstOrDefault(s => s.SourceAppUserModelId == _pinnedAppId);
            if (pinned != null && (IsPlaying(pinned) || !sessions.Any(IsPlaying))) return pinned;
            _pinnedAppId = null;
        }
        // Apple behaviour: the island follows whatever is actually playing.
        var current = _mgr.GetCurrentSession();
        if (current != null && IsPlaying(current)) return current;
        return sessions.FirstOrDefault(IsPlaying) ?? current ?? sessions[0];
    }

    static bool IsPlaying(GlobalSystemMediaTransportControlsSession s)
    {
        try { return s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
        catch { return false; }
    }

    GlobalSystemMediaTransportControlsSession Session()
    {
        try { return _mgr == null ? null : PickSession(); } catch { return null; }
    }

    public async void Refresh()
    {
        if (_busy || _mgr == null) return;
        _busy = true;
        try
        {
            var s = Session();
            var snap = new MediaSnapshot();
            if (s != null)
            {
                var props = await s.TryGetMediaPropertiesAsync().AsTask();
                var pb = s.GetPlaybackInfo();
                var tl = s.GetTimelineProperties();
                snap.AppId = s.SourceAppUserModelId ?? "";
                snap.Title = props?.Title ?? "";
                snap.Artist = props?.Artist ?? "";
                if (string.IsNullOrWhiteSpace(snap.Artist)) snap.Artist = props?.AlbumArtist ?? "";
                snap.Album = props?.AlbumTitle ?? "";
                snap.IsPlaying = pb.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                snap.CanSeek = pb.Controls.IsPlaybackPositionEnabled;
                snap.Duration = tl.EndTime - tl.StartTime;
                snap.Position = tl.Position - tl.StartTime;
                snap.PositionUpdated = tl.LastUpdatedTime;
                // Some players never update LastUpdatedTime; avoid runaway extrapolation.
                if (snap.PositionUpdated.Year < 2000 || DateTimeOffset.Now - snap.PositionUpdated > TimeSpan.FromHours(6))
                    snap.PositionUpdated = DateTimeOffset.Now;

                bool changed = snap.TrackKey != Current.TrackKey;
                if (changed) _trackChangedAt = DateTime.UtcNow;
                // Players often publish the new title before its cover, or re-send the previous song's cover first:
                // keep re-reading the thumbnail for a few seconds after a change and swap when the bytes differ.
                bool settling = DateTime.UtcNow - _trackChangedAt < TimeSpan.FromSeconds(4);
                snap.Art = Current.Art;
                snap.ArtColor = Current.ArtColor;
                snap.ArtHash = Current.ArtHash;
                if (props?.Thumbnail != null && (settling || Current.Art == null))
                {
                    var (art, color, hash) = await LoadArt(props.Thumbnail, Current.ArtHash);
                    if (art != null) { snap.Art = art; snap.ArtColor = color; snap.ArtHash = hash; }
                }
                else if (changed && props?.Thumbnail == null) { snap.Art = null; snap.ArtHash = 0; }
                Current = snap;
                Updated?.Invoke(snap, changed);
            }
            else if (Current.HasMedia || Current.AppId != "")
            {
                Current = snap;
                Updated?.Invoke(snap, true);
            }
        }
        catch (Exception ex) { AppHost.Log("Media refresh: " + ex); }
        finally { _busy = false; }
    }

    /// Decodes the cover, unless its bytes hash to `known` (then Art is null: keep the image already decoded).
    static async Task<(ImageSource, Color, ulong)> LoadArt(Windows.Storage.Streams.IRandomAccessStreamReference thumb, ulong known)
    {
        try
        {
            using var stream = await thumb.OpenReadAsync().AsTask();
            using var net = stream.AsStream();
            var ms = new MemoryStream();
            await net.CopyToAsync(ms);
            ulong hash = 14695981039346656037; // FNV-1a
            var buf = ms.GetBuffer();
            for (int i = 0; i < ms.Length; i++) hash = (hash ^ buf[i]) * 1099511628211;
            if (hash == known) return (null, Colors.White, hash);
            ms.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 300;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return (bmp, VibrantColor(bmp), hash);
        }
        catch { return (null, Colors.White, 0); }
    }

    /// Picks the most vivid reasonably-bright colour in the artwork (what Apple uses for the waveform tint).
    public static Color VibrantColor(BitmapSource src)
    {
        try
        {
            var small = new TransformedBitmap(new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0),
                new ScaleTransform(24.0 / src.PixelWidth, 24.0 / src.PixelHeight));
            int w = small.PixelWidth, h = small.PixelHeight;
            var px = new byte[w * h * 4];
            small.CopyPixels(px, w * 4, 0);
            double bestScore = -1; Color best = Colors.White;
            double ar = 0, ag = 0, ab = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                byte b = px[i], g = px[i + 1], r = px[i + 2];
                ar += r; ag += g; ab += b;
                double max = Math.Max(r, Math.Max(g, b)) / 255.0, min = Math.Min(r, Math.Min(g, b)) / 255.0;
                double sat = max == 0 ? 0 : (max - min) / max;
                double score = sat * 0.7 + max * 0.3 - (max < 0.25 ? 1 : 0);
                if (score > bestScore) { bestScore = score; best = Color.FromRgb(r, g, b); }
            }
            int n = px.Length / 4;
            if (bestScore < 0.35) best = Color.FromRgb((byte)(ar / n), (byte)(ag / n), (byte)(ab / n));
            // Lift dark colours so bars stay visible on black.
            double lum = (0.299 * best.R + 0.587 * best.G + 0.114 * best.B) / 255.0;
            if (lum < 0.45)
            {
                double k = 0.45 / Math.Max(lum, 0.05);
                best = Color.FromRgb((byte)Math.Min(255, best.R * k + 40), (byte)Math.Min(255, best.G * k + 40), (byte)Math.Min(255, best.B * k + 40));
            }
            return best;
        }
        catch { return Colors.White; }
    }

    async void Run(Func<GlobalSystemMediaTransportControlsSession, Windows.Foundation.IAsyncOperation<bool>> op)
    {
        var s = Session();
        if (s == null) return;
        try { await op(s).AsTask(); } catch { }
        await Task.Delay(150);
        Refresh();
    }

    public void PlayPause()
    {
        var c = Current;
        if (c.HasMedia) { c.Position = c.LivePosition; c.PositionUpdated = DateTimeOffset.Now; c.IsPlaying = !c.IsPlaying; }
        Run(s => s.TryTogglePlayPauseAsync());
    }
    public void Next() => Run(s => s.TrySkipNextAsync());
    public void Previous() => Run(s => s.TrySkipPreviousAsync());

    public void Seek(TimeSpan pos)
    {
        Current.Position = pos;
        Current.PositionUpdated = DateTimeOffset.Now;
        Run(s => s.TryChangePlaybackPositionAsync((s.GetTimelineProperties().StartTime + pos).Ticks));
    }

    /// Source badge click: rotate through every active media session.
    public void CycleSession()
    {
        try
        {
            var list = _mgr?.GetSessions();
            if (list == null || list.Count < 2) return;
            int idx = list.ToList().FindIndex(s => s.SourceAppUserModelId == Current.AppId);
            _pinnedAppId = list[(idx + 1) % list.Count].SourceAppUserModelId;
            Refresh();
        }
        catch { }
    }

    /// Album-art click: bring the playing app's window to the front.
    public void BringPlayerToFront()
    {
        string id = Current.AppId;
        if (string.IsNullOrEmpty(id)) return;
        using var p = FindPlayer(id);
        if (p != null && p.MainWindowHandle != IntPtr.Zero) { Native.ForceForeground(p.MainWindowHandle); return; }
        // Packaged apps (Apple Music, Media Player): activate through their AUMID.
        if (id.Contains('!'))
            try { Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + id) { UseShellExecute = true }); } catch { }
    }

    /// The process (preferably the one owning a window) behind a media session id.
    static Process FindPlayer(string id)
    {
        string exe = id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? id[..^4] : id;
        if (exe.Equals("microsoftedge", StringComparison.OrdinalIgnoreCase)) exe = "msedge";
        foreach (var name in new[] { exe, exe.Split('.', '_', '!').FirstOrDefault() ?? exe }.Distinct())
        {
            Process hit = null;
            foreach (var p in SafeProcesses(name))
            {
                if (hit == null && p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrEmpty(p.MainWindowTitle)) hit = p;
                else p.Dispose();
            }
            if (hit != null) return hit;
        }
        // "Chromium.<hash>" (Spotify/CEF, PWAs) or a bare hash: ask Core Audio who is actually playing
        if (!IsOpaque(id.Split('!')[0].Split('.').Last())) return null;
        if (AudioProcessResolver?.Invoke() is { } playing) return playing;
        // nothing audible (paused): Spotify desktop is by far the most common owner of such an id
        return id.StartsWith("Chromium.", StringComparison.OrdinalIgnoreCase)
            ? SafeProcesses("Spotify").FirstOrDefault(x => x.MainWindowHandle != IntPtr.Zero)
            : null;
    }

    static readonly Dictionary<string, ImageSource> IconCache = new();
    static readonly Dictionary<string, DateTime> IconMiss = new();

    /// Icon of the app behind a media session (shown as the badge on the album art).
    public static ImageSource AppIcon(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        if (IconCache.TryGetValue(id, out var cached)) return cached;
        // a miss walks the process list; the media page asks on every poll, so retry a miss only every few seconds
        if (IconMiss.TryGetValue(id, out var missAt) && DateTime.UtcNow - missAt < TimeSpan.FromSeconds(8)) return null;
        ImageSource img = null;
        try
        {
            using var p = FindPlayer(id);
            string path = p?.MainModule?.FileName;
            if (path != null) img = IconHelper.Get(path);
        }
        catch { }
        if (img != null) IconCache[id] = img; // hits are cached for good; the app may simply not be found yet
        else IconMiss[id] = DateTime.UtcNow;
        return img;
    }

    public static bool IsBrowser(string id) => FriendlyAppName(id) is "Chrome" or "Edge" or "Firefox" or "Brave" or "Opera" or "Vivaldi";

    static Process[] SafeProcesses(string name)
    {
        try { return Process.GetProcessesByName(name); } catch { return Array.Empty<Process>(); }
    }

    public static string FriendlyAppName(string id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        string l = id.ToLowerInvariant();
        if (l.Contains("spotify")) return "Spotify";
        if (l.Contains("chrome")) return "Chrome";
        if (l.Contains("msedge") || l.Contains("microsoftedge")) return "Edge";
        if (l.Contains("firefox")) return "Firefox";
        if (l.Contains("brave")) return "Brave";
        if (l.Contains("opera")) return "Opera";
        if (l.Contains("vivaldi")) return "Vivaldi";
        if (l.Contains("applemusic") || l.Contains("appleinc")) return "Apple Music";
        if (l.Contains("zunemusic") || l.Contains("zunevideo") || l.Contains("mediaplayer")) return "Media Player";
        if (l.Contains("vlc")) return "VLC";
        if (l.Contains("tidal")) return "TIDAL";
        if (l.Contains("deezer")) return "Deezer";
        if (l.Contains("youtube")) return "YouTube Music";
        string shell = ShellDisplayName(id);
        if (!string.IsNullOrWhiteSpace(shell)) return shell;
        string s = id.Split('!')[0].Split('_')[0];
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        s = s.Split('.').Last();
        if (IsOpaque(s))
        {
            // e.g. Spotify registers a random id: name it after whoever is actually making sound.
            using var p = FindPlayer(id);
            if (p == null) return "Media";
            string n = "Media";
            try { n = p.MainModule?.FileVersionInfo.FileDescription; } catch { }
            if (string.IsNullOrWhiteSpace(n) || n.Length > 24) n = char.ToUpper(p.ProcessName[0]) + p.ProcessName[1..];
            NameCache[id] = n;
            return n;
        }
        return s.Length > 0 ? char.ToUpper(s[0]) + s[1..] : s;
    }

    static bool IsOpaque(string s) => s.Length > 16 && s.All(char.IsLetterOrDigit) && s.Any(char.IsDigit);

    /// Supplied by the island: the process currently playing audio (Core Audio sessions).
    public static Func<Process> AudioProcessResolver;

    // ---- AUMID → display name via the shell's Apps folder (covers PWAs, Store apps, registered Win32 apps) ----
    static readonly Dictionary<string, string> NameCache = new();

    [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"),
     System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint sigdn, out IntPtr name);
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPStruct)] Guid riid, out IShellItem item);

    static string ShellDisplayName(string aumid)
    {
        if (NameCache.TryGetValue(aumid, out var cached)) return cached;
        string name = null;
        try
        {
            SHCreateItemFromParsingName("shell:AppsFolder\\" + aumid, IntPtr.Zero, typeof(IShellItem).GUID, out var item);
            item.GetDisplayName(0, out IntPtr p); // SIGDN_NORMALDISPLAY
            name = System.Runtime.InteropServices.Marshal.PtrToStringUni(p);
            System.Runtime.InteropServices.Marshal.FreeCoTaskMem(p);
            if (name != null && (name.Equals(aumid, StringComparison.OrdinalIgnoreCase) || name.Length > 40)) name = null;
        }
        catch { }
        NameCache[aumid] = name;
        return name;
    }
}
