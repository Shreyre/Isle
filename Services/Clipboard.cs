using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Path = System.IO.Path;

namespace Isle.Services;

public enum ClipKind { Text, Url, Image, Files }

public sealed class ClipItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ClipKind Kind { get; set; }
    public string Text { get; set; }          // text, url, newline-separated paths, or "Image W×H"
    public string ImagePath { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;
    public bool Pinned { get; set; }
    public string SourceApp { get; set; }
    public string Hash { get; set; }          // pixel hash, image de-duplication
}

/// Clipboard history shelf. All members are meant to be used from the UI thread.
public sealed class ClipboardStore
{
    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".tif", ".tiff" };
    const int MaxTextLength = 1_000_000;

    readonly string _folder, _dataPath;
    readonly List<ClipItem> _items = new();
    readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    readonly DispatcherTimer _captureTimer, _saveTimer, _retentionTimer;
    readonly HwndSource _listener;
    uint _selfSeq;
    int _retries;

    public IReadOnlyList<ClipItem> Items => _items;
    public event Action Changed;
    public event Action<ClipItem> Captured;

    public ClipboardStore(string folder)
    {
        _folder = folder;
        _dataPath = Path.Combine(folder, "ClipboardData.json");
        Directory.CreateDirectory(folder);
        Load();

        _captureTimer = new DispatcherTimer(DispatcherPriority.Background, _ui) { Interval = TimeSpan.FromMilliseconds(120) };
        _captureTimer.Tick += (_, _) => { _captureTimer.Stop(); Capture(); };
        _saveTimer = new DispatcherTimer(DispatcherPriority.Background, _ui) { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };
        _retentionTimer = new DispatcherTimer(DispatcherPriority.Background, _ui) { Interval = TimeSpan.FromHours(1) };
        _retentionTimer.Tick += (_, _) => ApplyRetention();
        _retentionTimer.Start();
        ApplyRetention();
        AppHost.SettingsChanged += ApplyRetention;

        _listener = new HwndSource(new HwndSourceParameters("IsleClipboard") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        _listener.AddHook(WndProc);
        Native.AddClipboardFormatListener(_listener.Handle);
        _ui.ShutdownStarted += (_, _) =>
        {
            Native.RemoveClipboardFormatListener(_listener.Handle);
            if (_saveTimer.IsEnabled) SaveNow();
        };
    }

    // ---------- public API ----------

    public void CopyToClipboard(ClipItem item)
    {
        if (item == null) return;
        try
        {
            var formats = new List<(uint, byte[])>();
            switch (item.Kind)
            {
                case ClipKind.Image:
                    if (ClipTools.LoadFull(item.ImagePath) is not BitmapSource bmp) return;
                    formats.Add((CF_DIB, ToDib(bmp)));
                    formats.Add((RegisterClipboardFormat("PNG"), File.ReadAllBytes(item.ImagePath)));
                    break;
                case ClipKind.Files:
                    formats.Add((CF_HDROP, ToHDrop(SplitLines(item.Text))));
                    formats.Add((CF_UNICODETEXT, Encoding.Unicode.GetBytes(item.Text + "\0")));
                    break;
                default:
                    formats.Add((CF_UNICODETEXT, Encoding.Unicode.GetBytes((item.Text ?? "") + "\0")));
                    break;
            }
            if (!WriteClipboard(formats)) { AppHost.Log("Clipboard copy: clipboard busy"); return; }
            _selfSeq = GetClipboardSequenceNumber();   // our own update must not be re-captured
            if (!item.Pinned) { item.Created = DateTime.Now; Commit(); }
        }
        catch (Exception ex) { AppHost.Log("Clipboard copy: " + ex.Message); }
    }

    // Win32 clipboard with immediate rendering. WPF's OLE clipboard delay-renders and blocks in a
    // non-pumping retry loop, which deadlocks against listeners asking our thread for the data.
    const uint CF_UNICODETEXT = 13, CF_DIB = 8, CF_HDROP = 15, GMEM_MOVEABLE = 2;
    [DllImport("user32.dll")] static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] static extern bool EmptyClipboard();
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern IntPtr SetClipboardData(uint format, IntPtr mem);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr h);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr h);

    bool WriteClipboard(List<(uint format, byte[] data)> formats)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            if (OpenClipboard(_listener.Handle))
            {
                try
                {
                    EmptyClipboard();
                    foreach (var (format, data) in formats)
                    {
                        IntPtr h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)data.Length);
                        Marshal.Copy(data, 0, GlobalLock(h), data.Length);
                        GlobalUnlock(h);
                        if (SetClipboardData(format, h) == IntPtr.Zero) GlobalFree(h);   // on success the system owns h
                    }
                    return true;
                }
                finally { CloseClipboard(); }
            }
            Thread.Sleep(25);   // another app is reading the previous change
        }
        return false;
    }

    /// 32-bit bottom-up DIB, transparency flattened onto white for apps that ignore alpha (PNG is offered too).
    static byte[] ToDib(BitmapSource src)
    {
        var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        bgra.CopyPixels(px, stride, 0);
        for (int i = 0; i < px.Length; i += 4)
        {
            int a = px[i + 3];
            if (a == 255) continue;
            for (int c = 0; c < 3; c++) px[i + c] = (byte)((px[i + c] * a + 255 * (255 - a)) / 255);
            px[i + 3] = 255;
        }
        var dib = new byte[40 + px.Length];
        BitConverter.GetBytes(40).CopyTo(dib, 0);
        BitConverter.GetBytes(w).CopyTo(dib, 4);
        BitConverter.GetBytes(h).CopyTo(dib, 8);
        BitConverter.GetBytes((short)1).CopyTo(dib, 12);
        BitConverter.GetBytes((short)32).CopyTo(dib, 14);
        BitConverter.GetBytes(px.Length).CopyTo(dib, 20);
        for (int y = 0; y < h; y++) Buffer.BlockCopy(px, y * stride, dib, 40 + (h - 1 - y) * stride, stride);
        return dib;
    }

    /// DROPFILES header + double-null-terminated UTF-16 path list.
    static byte[] ToHDrop(string[] paths)
    {
        var names = Encoding.Unicode.GetBytes(string.Join("\0", paths) + "\0\0");
        var buf = new byte[20 + names.Length];
        BitConverter.GetBytes(20).CopyTo(buf, 0);   // pFiles
        BitConverter.GetBytes(1).CopyTo(buf, 16);   // fWide
        names.CopyTo(buf, 20);
        return buf;
    }

    public void TogglePin(ClipItem item) { item.Pinned = !item.Pinned; Trim(); Commit(); }

    public void Remove(ClipItem item) { RemoveCore(item); Commit(); }

    public void ClearUnpinned()
    {
        foreach (var i in _items.Where(i => !i.Pinned).ToList()) RemoveCore(i);
        Commit();
    }

    public ClipItem AddText(string text) => AddTextCore(text);

    public ClipItem AddImage(BitmapSource img) => img == null ? null : AddImageCore(img);

    public void AddFiles(string[] paths)
    {
        if (paths == null) return;
        var others = new List<string>();
        foreach (var p in paths)
        {
            if (File.Exists(p) && ImageExts.Contains(Path.GetExtension(p).ToLowerInvariant()) && ClipTools.LoadFull(p) is BitmapSource img)
                AddImageCore(img);
            else
                others.Add(p);
        }
        if (others.Count > 0) AddByText(ClipKind.Files, string.Join(Environment.NewLine, others));
    }

    public string Export(string folder)
    {
        string dir = Path.Combine(folder, $"Isle_Export_{DateTime.Now:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(dir);
        var sb = new StringBuilder();
        foreach (var i in _items)
        {
            if (i.Kind == ClipKind.Image)
            {
                if (File.Exists(i.ImagePath)) File.Copy(i.ImagePath, Path.Combine(dir, Path.GetFileName(i.ImagePath)), true);
                continue;
            }
            sb.AppendLine($"[{i.Created:yyyy-MM-dd HH:mm:ss}] {i.Kind}{(i.Pinned ? " (pinned)" : "")}");
            sb.AppendLine(i.Text).AppendLine();
        }
        File.WriteAllText(Path.Combine(dir, "texts.txt"), sb.ToString());
        return dir;
    }

    public void ApplyRetention()
    {
        TimeSpan? keep = AppHost.Settings?.ClipboardRetention switch
        {
            "24Hours" => TimeSpan.FromHours(24),
            "3Days" => TimeSpan.FromDays(3),
            "7Days" => TimeSpan.FromDays(7),
            _ => null,
        };
        int before = _items.Count;
        if (keep is TimeSpan k)
            foreach (var i in _items.Where(i => !i.Pinned && DateTime.Now - i.Created > k).ToList()) RemoveCore(i);
        Trim();
        if (_items.Count != before) Commit();
    }

    // ---------- capture ----------

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == Native.WM_CLIPBOARDUPDATE) { _retries = 0; _captureTimer.Stop(); _captureTimer.Start(); }
        return IntPtr.Zero;
    }

    void Capture()
    {
        if (AppHost.Settings is { ClipboardEnabled: false }) return;
        if (GetClipboardSequenceNumber() == _selfSeq) return;
        string app = SourceApp(out bool excluded);
        if (excluded) return;
        try
        {
            var data = Clipboard.GetDataObject();
            if (data == null || IsPrivate(data)) return;
            ClipItem item = null;
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                item = AddByText(ClipKind.Files, string.Join(Environment.NewLine, files));
            else if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string text)
                item = AddTextCore(text);
            else if (ReadImage(data) is BitmapSource img)
                item = AddImageCore(img);
            if (item == null) return;
            item.SourceApp = app;
            Captured?.Invoke(item);
        }
        catch (ExternalException) when (_retries++ < 5) { _captureTimer.Start(); }   // clipboard busy, retry shortly
        catch (Exception ex) { AppHost.Log("Clipboard capture: " + ex.Message); }
    }

    static string SourceApp(out bool excluded)
    {
        string owner = Native.ProcessNameOf(Native.GetClipboardOwner());
        string fg = Native.ProcessNameOf(Native.GetForegroundWindow());
        excluded = (AppHost.Settings?.ClipboardPrivacyExclusions ?? "")
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? x[..^4] : x)
            .Any(x => x.Equals(owner, StringComparison.OrdinalIgnoreCase) || x.Equals(fg, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrEmpty(owner) ? fg : owner;
    }

    /// Password managers mark secrets with these formats (Windows clipboard-history conventions).
    static bool IsPrivate(IDataObject d)
    {
        if (d.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing") || d.GetDataPresent("Clipboard Viewer Ignore")) return true;
        return d.GetDataPresent("CanIncludeInClipboardHistory")
            && d.GetData("CanIncludeInClipboardHistory") is MemoryStream ms && ms.Length >= 4
            && BitConverter.ToInt32(ms.ToArray(), 0) == 0;
    }

    /// PNG first (keeps alpha), then CF_DIB rebuilt as a .bmp, then WPF's own reader.
    static BitmapSource ReadImage(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream png)
                return BitmapDecoder.Create(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        }
        catch { }
        try
        {
            if (data.GetDataPresent(DataFormats.Dib) && data.GetData(DataFormats.Dib) is MemoryStream dibStream)
            {
                byte[] dib = dibStream.ToArray();
                int headerSize = BitConverter.ToInt32(dib, 0), bitCount = BitConverter.ToInt16(dib, 14);
                int compression = BitConverter.ToInt32(dib, 16), clrUsed = BitConverter.ToInt32(dib, 32);
                int masks = compression == 3 && headerSize == 40 ? 12 : 0;
                int palette = (bitCount <= 8 && clrUsed == 0 ? 1 << bitCount : clrUsed) * 4;
                var bmp = new byte[14 + dib.Length];
                bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
                BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
                BitConverter.GetBytes(14 + headerSize + masks + palette).CopyTo(bmp, 10);
                dib.CopyTo(bmp, 14);
                return BitmapDecoder.Create(new MemoryStream(bmp), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            }
        }
        catch { }
        try { return Clipboard.ContainsImage() ? Clipboard.GetImage() : null; } catch { return null; }
    }

    // ---------- core ----------

    ClipItem AddTextCore(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength) return null;   // ponytail: huge copies skipped, not truncated
        string t = text.Trim();
        bool url = !t.Contains('\n') && !t.Contains(' ') && Uri.TryCreate(t, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https");
        return AddByText(url ? ClipKind.Url : ClipKind.Text, url ? t : text);
    }

    ClipItem AddByText(ClipKind kind, string text)
    {
        var dup = _items.FirstOrDefault(i => i.Kind == kind && i.Text == text);
        return dup != null ? Bump(dup) : Insert(new ClipItem { Kind = kind, Text = text });
    }

    ClipItem AddImageCore(BitmapSource img)
    {
        var (bgra, hash) = Normalize(img);
        var dup = _items.FirstOrDefault(i => i.Kind == ClipKind.Image && i.Hash == hash);
        if (dup != null) return Bump(dup);

        string path = Path.Combine(_folder, $"clip_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
        if (File.Exists(path)) path = Path.Combine(_folder, $"clip_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.png");
        using (var fs = File.Create(path))   // ponytail: sync PNG encode on UI thread; move to a worker if 4K screenshots hitch
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bgra));
            enc.Save(fs);
        }
        return Insert(new ClipItem { Kind = ClipKind.Image, ImagePath = path, Hash = hash, Text = $"Image {bgra.PixelWidth}×{bgra.PixelHeight}" });
    }

    /// Bgra32 copy + content hash. Fully transparent 32-bit DIBs (alpha unused) are made opaque.
    static (BitmapSource, string) Normalize(BitmapSource src)
    {
        BitmapSource conv = src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = conv.PixelWidth, h = conv.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        conv.CopyPixels(px, stride, 0);
        bool noAlpha = true;
        for (int i = 3; i < px.Length; i += 4) if (px[i] != 0) { noAlpha = false; break; }
        if (noAlpha) for (int i = 3; i < px.Length; i += 4) px[i] = 255;
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
        bmp.Freeze();
        var rgb = (byte[])px.Clone();   // hash ignores alpha: the clipboard DIB path drops it
        for (int i = 3; i < rgb.Length; i += 4) rgb[i] = 0;
        return (bmp, Convert.ToHexString(MD5.HashData(rgb)));
    }

    ClipItem Bump(ClipItem item)
    {
        if (!item.Pinned) item.Created = DateTime.Now;
        Commit();
        return item;
    }

    ClipItem Insert(ClipItem item)
    {
        _items.Add(item);
        Trim();
        Commit();
        return item;
    }

    void Trim()
    {
        int max = AppHost.Settings?.ClipboardMaxItems ?? 200;
        foreach (var i in _items.Where(i => !i.Pinned).OrderByDescending(i => i.Created).Skip(max).ToList()) RemoveCore(i);
    }

    void RemoveCore(ClipItem item)
    {
        if (!_items.Remove(item) || item.Kind != ClipKind.Image || string.IsNullOrEmpty(item.ImagePath)) return;
        try
        {
            if (Path.GetFullPath(item.ImagePath).StartsWith(Path.GetFullPath(_folder), StringComparison.OrdinalIgnoreCase))
                File.Delete(item.ImagePath);
        }
        catch (Exception ex) { AppHost.Log("Clipboard delete: " + ex.Message); }
    }

    void Commit()
    {
        _items.Sort((a, b) => a.Pinned != b.Pinned ? (a.Pinned ? -1 : 1) : b.Created.CompareTo(a.Created));
        _saveTimer.Stop();
        _saveTimer.Start();
        if (_ui.CheckAccess()) Changed?.Invoke();
        else _ui.BeginInvoke(() => Changed?.Invoke());
    }

    void Load()
    {
        try
        {
            if (File.Exists(_dataPath))
                _items.AddRange(JsonSerializer.Deserialize<List<ClipItem>>(File.ReadAllText(_dataPath), Json) ?? new());
        }
        catch (Exception ex)
        {
            AppHost.Log("Clipboard load: " + ex.Message);
            try { File.Copy(_dataPath, _dataPath + ".bad", true); } catch { }   // keep the corrupt file instead of overwriting it
        }
        _items.RemoveAll(i => i == null || (i.Kind == ClipKind.Image && !File.Exists(i.ImagePath)));
        _items.Sort((a, b) => a.Pinned != b.Pinned ? (a.Pinned ? -1 : 1) : b.Created.CompareTo(a.Created));
    }

    void SaveNow()
    {
        try
        {
            string tmp = _dataPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_items, Json));
            File.Move(tmp, _dataPath, true);
        }
        catch (Exception ex) { AppHost.Log("Clipboard save: " + ex.Message); }
    }

    static string[] SplitLines(string s) => (s ?? "").Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
}

public static class ClipTools
{
    static readonly Dictionary<string, ImageSource> Thumbs = new();

    public static async Task<string> OcrAsync(string imagePath)
    {
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(Path.GetFullPath(imagePath));
            using var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.Read);
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
            var engine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages()
                         ?? Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
            if (engine == null) return "";
            uint w = decoder.OrientedPixelWidth, h = decoder.OrientedPixelHeight, longest = Math.Max(w, h);
            uint max = Windows.Media.Ocr.OcrEngine.MaxImageDimension;
            double scale = longest > max ? (double)max / longest : longest < 800 ? 2 : 1;   // small text reads better upscaled
            var transform = new Windows.Graphics.Imaging.BitmapTransform
            {
                ScaledWidth = (uint)(decoder.PixelWidth * scale),
                ScaledHeight = (uint)(decoder.PixelHeight * scale),
                InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Fant,
            };
            using var bmp = await decoder.GetSoftwareBitmapAsync(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied, transform,
                Windows.Graphics.Imaging.ExifOrientationMode.RespectExifOrientation, Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
            var result = await engine.RecognizeAsync(bmp);
            return string.Join(Environment.NewLine, result.Lines.Select(l => l.Text));
        }
        catch (Exception ex) { AppHost.Log("OCR: " + ex.Message); return ""; }
    }

    /// Opens Google Lens with the image via a self-submitting local form (Lens has no public upload API).
    public static void SearchWithLens(string imagePath)
    {
        try
        {
            string ext = Path.GetExtension(imagePath).ToLowerInvariant();
            string mime = ext switch { ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", ".bmp" => "image/bmp", _ => "image/png" };
            string b64 = Convert.ToBase64String(File.ReadAllBytes(imagePath));
            string html = $$"""
                <!doctype html>
                <html><head><meta charset="utf-8"><title>Google Lens</title></head>
                <body style="background:#000;color:#fff;font-family:-apple-system,'Segoe UI',sans-serif;display:grid;place-items:center;height:100vh;margin:0">
                <p>Opening Google Lens…</p>
                <form id="f" action="https://lens.google.com/v3/upload?hl=en" method="POST" enctype="multipart/form-data">
                <input id="i" name="encoded_image" type="file" accept="image/*" hidden></form>
                <script>
                const raw = atob("{{b64}}");
                const bytes = new Uint8Array(raw.length);
                for (let i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i);
                const dt = new DataTransfer();
                dt.items.add(new File([bytes], "image{{ext}}", { type: "{{mime}}" }));
                document.getElementById("i").files = dt.files;
                document.getElementById("f").submit();
                </script></body></html>
                """;
            string temp = Path.GetTempPath();
            foreach (var old in Directory.GetFiles(temp, "isle_lens_*.html"))
                try { if (File.GetCreationTime(old) < DateTime.Now.AddHours(-1)) File.Delete(old); } catch { }
            string path = Path.Combine(temp, $"isle_lens_{DateTime.Now.Ticks}.html");
            File.WriteAllText(path, html);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { AppHost.Log("Lens: " + ex.Message); }
    }

    public static ImageSource LoadThumb(string path, int decodePixelWidth)
    {
        if (string.IsNullOrEmpty(path)) return null;
        string key = path + "|" + decodePixelWidth;
        lock (Thumbs)
        {
            if (Thumbs.TryGetValue(key, out var cached)) return cached;
            var img = LoadFull(path, decodePixelWidth);
            if (img == null) return null;
            if (Thumbs.Count > 300) Thumbs.Clear();   // ponytail: crude bound, LRU if thumbnail churn ever matters
            return Thumbs[key] = img;
        }
    }

    /// Frozen bitmap read fully into memory so the file stays deletable.
    public static BitmapSource LoadFull(string path, int decodePixelWidth = 0)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            if (decodePixelWidth > 0) bi.DecodePixelWidth = decodePixelWidth;
            bi.StreamSource = fs;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    public static string TimeAgo(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalSeconds < 60) return "Just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}h ago";
        if (d.TotalDays < 7) return $"{(int)d.TotalDays}d ago";
        return t.ToString("MMM d", CultureInfo.InvariantCulture);
    }
}

/// macOS Quick Look-style preview for image and text clips.
public sealed class ClipPreviewWindow : Window
{

    const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";
    const double Pad = 24, TitleHeight = 40, Radius = 14;
    static readonly Brush PanelBrush = Frozen(Color.FromArgb(0xF2, 0x1E, 0x1E, 0x1E));
    static ClipPreviewWindow _open;

    static readonly ControlTemplate ButtonTemplate = (ControlTemplate)XamlReader.Parse(
        "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>" +
        "<Border x:Name='b' CornerRadius='6' Background='Transparent' Padding='{TemplateBinding Padding}'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border>" +
        "<ControlTemplate.Triggers>" +
        "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Background' Value='#26FFFFFF'/></Trigger>" +
        "<Trigger Property='IsPressed' Value='True'><Setter TargetName='b' Property='Background' Value='#40FFFFFF'/></Trigger>" +
        "<Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.5'/></Trigger>" +
        "</ControlTemplate.Triggers></ControlTemplate>");

    readonly string _imagePath, _text;
    readonly ScaleTransform _zoom = new();
    readonly TranslateTransform _pan = new();
    readonly Grid _content = new() { ClipToBounds = true, Background = Brushes.Transparent };
    Point? _dragFrom;
    Border _ocrPanel;
    TextBox _ocrText;

    public static void ShowImage(string path) { if (File.Exists(path)) Open(new ClipPreviewWindow(path, null)); }
    public static void ShowText(string text) => Open(new ClipPreviewWindow(null, text ?? ""));

    static void Open(ClipPreviewWindow w)
    {
        _open?.Close();
        _open = w;
        w.Closed += (_, _) => { if (_open == w) _open = null; };
        w.Show();
        w.Activate();
    }

    ClipPreviewWindow(string imagePath, string text)
    {
        _imagePath = imagePath;
        _text = text;
        Title = imagePath != null ? Path.GetFileName(imagePath) : "Text";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        UseLayoutRounding = true;
        FontFamily = Isle.Controls.Ui.Font;
        WindowStartupLocation = WindowStartupLocation.Manual;

        var (work, scale) = CursorScreen();
        double maxW = work.Width / scale * 0.8 - 2 * Pad, maxH = work.Height / scale * 0.8 - 2 * Pad - TitleHeight;
        Size size;
        if (imagePath != null)
        {
            var img = ClipTools.LoadFull(imagePath);
            double w = (img?.PixelWidth ?? 400) / scale, h = (img?.PixelHeight ?? 300) / scale;   // 1 image px = 1 screen px
            double fit = Math.Min(1, Math.Min(maxW / w, maxH / h));
            size = new Size(Math.Max(380, w * fit), Math.Max(220, h * fit));
            var image = new Image { Source = img, Stretch = Stretch.Uniform, MaxWidth = w, MaxHeight = h };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            _content.Children.Add(new Border { Child = image, RenderTransform = new TransformGroup { Children = { _zoom, _pan } } });
            WireZoom();
        }
        else
        {
            size = new Size(Math.Min(640, maxW), Math.Min(460, maxH));
            _content.Children.Add(new TextBox
            {
                Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Foreground = Brushes.White, CaretBrush = Brushes.White, SelectionBrush = Frozen(Color.FromRgb(0x0A, 0x84, 0xFF)),
                FontSize = 14, Padding = new Thickness(18, 6, 18, 18), FontFamily = FontFamily,
            });
        }
        Width = size.Width + 2 * Pad;
        Height = size.Height + TitleHeight + 2 * Pad;

        Content = BuildChrome(size);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && _imagePath != null) { CopyCurrent(null); e.Handled = true; }
        };
        SourceInitialized += (_, _) =>
        {
            // Move onto the cursor's monitor first (may trigger a DPI rescale), then center precisely.
            Native.SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero, (int)work.Left, (int)work.Top, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            Dispatcher.BeginInvoke(() => CenterOn(work), DispatcherPriority.Loaded);
        };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(() => CenterOn(work), DispatcherPriority.Loaded);
    }

    FrameworkElement BuildChrome(Size size)
    {
        // Title bar: traffic light (close), centered title, actions.
        var bar = new Grid { Height = TitleHeight, Background = Brushes.Transparent };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };

        var title = new TextBlock
        {
            Text = Title, Foreground = Frozen(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)), FontSize = 13, FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(12, 0, 12, 0), IsHitTestVisible = false,
        };
        Grid.SetColumn(title, 1);
        bar.Children.Add(title);

        var closeGlyph = new TextBlock { Text = "", FontFamily = new FontFamily(IconFont), FontSize = 7, Foreground = Frozen(Color.FromRgb(0x4D, 0x00, 0x00)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Hidden };
        var close = new Border
        {
            Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Background = Frozen(Color.FromRgb(0xFF, 0x5F, 0x57)),
            BorderBrush = Frozen(Color.FromArgb(0x33, 0, 0, 0)), BorderThickness = new Thickness(0.5),
            Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Child = closeGlyph, Cursor = Cursors.Arrow, ToolTip = "Close",
        };
        close.MouseEnter += (_, _) => closeGlyph.Visibility = Visibility.Visible;
        close.MouseLeave += (_, _) => closeGlyph.Visibility = Visibility.Hidden;
        close.MouseLeftButtonDown += (_, e) => e.Handled = true;   // don't start a window drag
        close.MouseLeftButtonUp += (_, _) => Close();
        bar.Children.Add(close);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(actions, 2);
        actions.Children.Add(MakeButton("", "Copy", CopyCurrent));
        if (_imagePath != null)
        {
            actions.Children.Add(MakeButton("", "Extract Text", ExtractText));
            actions.Children.Add(MakeButton("", "Google Lens", _ => ClipTools.SearchWithLens(_imagePath)));
        }
        bar.Children.Add(actions);

        // Body: content plus the OCR overlay as siblings (overlay clicks don't pan the image).
        var body = new Grid();
        body.Children.Add(_content);
        if (_imagePath != null) body.Children.Add(BuildOcrPanel(size));

        var layout = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        layout.Children.Add(bar);
        layout.Children.Add(body);

        // Shadow lives on its own layer so the content stays crisp and cheap to redraw while zooming.
        var shadow = new Border { CornerRadius = new CornerRadius(Radius), Background = PanelBrush, Effect = new DropShadowEffect { BlurRadius = 32, ShadowDepth = 10, Direction = 270, Opacity = 0.55 } };
        var surface = new Grid();
        surface.SizeChanged += (_, e) => surface.Clip = new RectangleGeometry(new Rect(e.NewSize), Radius, Radius);
        surface.Children.Add(new Border { Background = PanelBrush });
        surface.Children.Add(layout);
        surface.Children.Add(new Border { CornerRadius = new CornerRadius(Radius), BorderBrush = Frozen(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1), IsHitTestVisible = false });

        var root = new Grid { Margin = new Thickness(Pad), RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(0.94, 0.94), Opacity = 0 };
        root.Children.Add(shadow);
        root.Children.Add(surface);
        root.Loaded += (_, _) =>
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var dur = TimeSpan.FromMilliseconds(200);
            root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, dur) { EasingFunction = ease });
            ((ScaleTransform)root.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, dur) { EasingFunction = ease });
            ((ScaleTransform)root.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, dur) { EasingFunction = ease });
        };
        return root;
    }

    Border BuildOcrPanel(Size size)
    {
        _ocrText = new TextBox
        {
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.White, CaretBrush = Brushes.White,
            SelectionBrush = Frozen(Color.FromRgb(0x0A, 0x84, 0xFF)), FontSize = 13, FontFamily = FontFamily, Padding = new Thickness(12, 0, 12, 10),
        };
        var header = new DockPanel { Margin = new Thickness(12, 6, 6, 4) };
        var dismiss = MakeButton("", null, _ => _ocrPanel.Visibility = Visibility.Collapsed);
        DockPanel.SetDock(dismiss, Dock.Right);
        var copy = MakeButton("", "Copy Text", b => { TrySetText(_ocrText.Text); Flash(b, "Copied"); });
        DockPanel.SetDock(copy, Dock.Right);
        header.Children.Add(dismiss);
        header.Children.Add(copy);
        header.Children.Add(new TextBlock { Text = "Extracted Text", Foreground = Frozen(Color.FromArgb(0x99, 0xEB, 0xEB, 0xF5)), FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var stack = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        stack.Children.Add(header);
        stack.Children.Add(_ocrText);
        return _ocrPanel = new Border
        {
            Child = stack, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(12), MaxHeight = Math.Max(140, size.Height * 0.5),
            CornerRadius = new CornerRadius(10), Background = Frozen(Color.FromArgb(0xEB, 0x2C, 0x2C, 0x2E)),
            BorderBrush = Frozen(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1), Visibility = Visibility.Collapsed,
        };
    }

    void WireZoom()
    {
        _content.MouseWheel += (_, e) =>
        {
            var p = e.GetPosition(_content);
            double next = Math.Clamp(_zoom.ScaleX * (e.Delta > 0 ? 1.15 : 1 / 1.15), 0.2, 30), f = next / _zoom.ScaleX;
            _pan.X = p.X - (p.X - _pan.X) * f;   // keep the point under the cursor fixed
            _pan.Y = p.Y - (p.Y - _pan.Y) * f;
            _zoom.ScaleX = _zoom.ScaleY = next;
        };
        _content.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { _zoom.ScaleX = _zoom.ScaleY = 1; _pan.X = _pan.Y = 0; return; }
            _dragFrom = e.GetPosition(_content);
            _content.CaptureMouse();
            _content.Cursor = Cursors.SizeAll;
        };
        _content.MouseMove += (_, e) =>
        {
            if (_dragFrom is not Point from) return;
            var p = e.GetPosition(_content);
            _pan.X += p.X - from.X;
            _pan.Y += p.Y - from.Y;
            _dragFrom = p;
        };
        _content.MouseLeftButtonUp += (_, _) => { _dragFrom = null; _content.ReleaseMouseCapture(); _content.Cursor = null; };
        _content.ToolTip = "Scroll to zoom · drag to pan · double-click to reset";
    }

    void CopyCurrent(Button source)
    {
        var store = AppHost.Clips;
        var item = store?.Items.FirstOrDefault(i => _imagePath != null
            ? string.Equals(i.ImagePath, _imagePath, StringComparison.OrdinalIgnoreCase)
            : i.Kind != ClipKind.Image && i.Text == _text);
        if (item != null) store.CopyToClipboard(item);
        else if (_imagePath != null) { try { if (ClipTools.LoadFull(_imagePath) is BitmapSource b) Clipboard.SetImage(b); } catch { } }
        else TrySetText(_text);
        if (source != null) Flash(source, "Copied");
    }

    async void ExtractText(Button source)
    {
        source.IsEnabled = false;
        SetLabel(source, "Extracting…");
        string text = await ClipTools.OcrAsync(_imagePath);
        source.IsEnabled = true;
        SetLabel(source, "Extract Text");
        _ocrText.Text = string.IsNullOrWhiteSpace(text) ? "No text found." : text;
        _ocrPanel.Visibility = Visibility.Visible;
    }

    Button MakeButton(string glyph, string label, Action<Button> click)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily(IconFont), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        if (label != null) row.Children.Add(new TextBlock { Text = label, FontSize = 12, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Content = row, Template = ButtonTemplate, Foreground = Brushes.White, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(2, 0, 0, 0), Cursor = Cursors.Hand, Focusable = false };
        b.Click += (_, _) => click(b);
        return b;
    }

    static void SetLabel(Button b, string text)
    {
        if (b.Content is StackPanel p && p.Children.Count > 1 && p.Children[1] is TextBlock t) t.Text = text;
    }

    static void Flash(Button b, string text)
    {
        var old = (b.Content as StackPanel)?.Children.OfType<TextBlock>().Skip(1).FirstOrDefault()?.Text;
        if (old == null) return;
        SetLabel(b, text);
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        t.Tick += (_, _) => { t.Stop(); SetLabel(b, old); };
        t.Start();
    }

    static void TrySetText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); } catch (Exception ex) { AppHost.Log("Preview copy: " + ex.Message); }
    }

    /// Work area (physical px) and DPI scale of the monitor under the cursor.
    static (Rect work, double scale) CursorScreen()
    {
        var mon = Native.MonitorFromPoint(Native.Cursor(), 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
        Native.GetMonitorInfo(mon, ref info);
        Native.GetDpiForMonitor(mon, 0, out uint dpi, out _);
        var r = info.rcWork;
        return (new Rect(r.Left, r.Top, r.Width, r.Height), dpi == 0 ? 1 : dpi / 96.0);
    }

    void CenterOn(Rect work)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !Native.GetWindowRect(hwnd, out var r)) return;
        int x = (int)(work.Left + (work.Width - r.Width) / 2), y = (int)(work.Top + (work.Height - r.Height) / 2);
        Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}
