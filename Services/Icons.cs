using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Isle.Services;

/// Resolves, labels, draws and launches quick-app entries (exe names, full paths, folders, shell: URIs, URLs).
public static class IconHelper
{
    static readonly Dictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static readonly (string Name, string Path)[] Presets =
    {
        ("File Explorer", "explorer.exe"), ("Task Manager", "taskmgr.exe"), ("Windows Terminal", "wt.exe"),
        ("Command Prompt", "cmd.exe"), ("PowerShell", "powershell.exe"), ("Notepad", "notepad.exe"),
        ("Calculator", "calc.exe"), ("Snipping Tool", "snippingtool.exe"), ("Control Panel", "control.exe"),
        ("Google Chrome", "chrome.exe"), ("Microsoft Edge", "msedge.exe"), ("Spotify", "spotify.exe"),
        ("VS Code", "code"), ("Downloads", "shell:Downloads"), ("Documents", "shell:Personal"),
    };

    /// Turns "chrome.exe" into a full path via App Paths / PATH; returns input unchanged if not resolvable.
    public static string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || path.Contains("://") || Path.IsPathRooted(path)) return path;
        string exe = path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? path : path + ".exe";
        foreach (var hive in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
        {
            try
            {
                using var k = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe);
                if (k?.GetValue(null) is string p && File.Exists(p.Trim('"'))) return p.Trim('"');
            }
            catch { }
        }
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').Append(Environment.SystemDirectory).Append(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        foreach (var d in dirs)
        {
            try { string c = Path.Combine(d.Trim(), exe); if (File.Exists(c)) return c; } catch { }
        }
        var cmd = dirs.Select(d => { try { return Path.Combine(d.Trim(), path + ".cmd"); } catch { return null; } }).FirstOrDefault(File.Exists);
        return cmd ?? path;
    }

    public static string DisplayName(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var preset = Presets.FirstOrDefault(p => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (preset.Name != null) return preset.Name;
        string full = Resolve(path);
        try
        {
            if (File.Exists(full) && full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var desc = FileVersionInfo.GetVersionInfo(full).FileDescription;
                if (!string.IsNullOrWhiteSpace(desc) && desc.Length < 28) return desc.Trim();
            }
        }
        catch { }
        if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return path[6..];
        if (path.Contains("://"))
            return Uri.TryCreate(path, UriKind.Absolute, out var u) && u.Host.Length > 0 ? u.Host.Replace("www.", "") : path;
        string name = Path.GetFileNameWithoutExtension(full.TrimEnd('\\'));
        if (string.IsNullOrEmpty(name)) name = full;
        return name.Length > 0 ? char.ToUpper(name[0]) + name[1..] : name;
    }

    public static ImageSource Get(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (Cache.TryGetValue(path, out var img)) return img;
        img = Load(Resolve(path));
        Cache[path] = img;
        return img;
    }

    static ImageSource Load(string full)
    {
        try
        {
            var info = new Native.SHFILEINFO();
            uint flags = Native.SHGFI_ICON | Native.SHGFI_LARGEICON;
            string query = full;
            if (full.Contains("://")) { query = ".url"; flags |= Native.SHGFI_USEFILEATTRIBUTES; }
            else if (!File.Exists(full) && !Directory.Exists(full) && !full.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            { query = ".exe"; flags |= Native.SHGFI_USEFILEATTRIBUTES; }
            if (full.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            {
                string known = ResolveShellFolder(full);
                if (known != null) query = known; else { query = "folder"; flags |= Native.SHGFI_USEFILEATTRIBUTES; }
            }
            Native.SHGetFileInfo(query, query == "folder" ? 0x10u : 0x80u, ref info, (uint)Marshal.SizeOf(info), flags);
            if (info.hIcon == IntPtr.Zero) return null;
            var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            Native.DestroyIcon(info.hIcon);
            src.Freeze();
            return src;
        }
        catch { return null; }
    }

    static string ResolveShellFolder(string shell) => shell[6..].ToLowerInvariant() switch
    {
        "downloads" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        "personal" or "documents" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "my pictures" or "pictures" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        "my music" or "music" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        "desktop" => Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        _ => null,
    };

    public static void Launch(string path)
    {
        try
        {
            string full = Resolve(path);
            if (full.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                Process.Start(new ProcessStartInfo("explorer.exe", full) { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo(full) { UseShellExecute = true });
        }
        catch (Exception ex) { AppHost.Log($"Launch '{path}': {ex.Message}"); }
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}
