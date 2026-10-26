using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Isle.Services;

public sealed record LyricLine(TimeSpan Time, string Text);

/// Time-synced lyrics from LRCLIB (free, no key).
public static class LyricsService
{
    static readonly HttpClient Http = CreateClient();
    static readonly ConcurrentDictionary<string, Task<IReadOnlyList<LyricLine>>> Cache = new();
    static readonly IReadOnlyList<LyricLine> Empty = Array.Empty<LyricLine>();

    static HttpClient CreateClient()
    {
        var c = new HttpClient { BaseAddress = new Uri("https://lrclib.net/api/"), Timeout = TimeSpan.FromSeconds(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Isle/1.0");
        return c;
    }

    public static async Task<IReadOnlyList<LyricLine>> GetAsync(string title, string artist, string album, TimeSpan duration)
    {
        if (string.IsNullOrWhiteSpace(title)) return Empty;
        var (t, a) = Clean(title, artist);
        string key = $"{t}|{a}".ToLowerInvariant();
        var result = await Cache.GetOrAdd(key, _ => FetchAsync(t, a, album, duration));
        if (result != null) return result;
        Cache.TryRemove(key, out _); // network failure: allow a retry next time
        return Empty;
    }

    public static int IndexAt(IReadOnlyList<LyricLine> lines, TimeSpan position)
    {
        int lo = 0, hi = (lines?.Count ?? 0) - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (lines[mid].Time <= position) { found = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return found;
    }

    // null = request failed (not cached), empty = no synced lyrics exist
    static async Task<IReadOnlyList<LyricLine>> FetchAsync(string title, string artist, string album, TimeSpan duration)
    {
        try
        {
            string q = $"get?track_name={E(title)}&artist_name={E(artist)}";
            if (!string.IsNullOrWhiteSpace(album)) q += $"&album_name={E(album)}";
            if (duration.TotalSeconds >= 1) q += $"&duration={(int)Math.Round(duration.TotalSeconds)}";
            using (var res = await Http.GetAsync(q))
            {
                if (res.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
                    var lines = Parse(Synced(doc.RootElement));
                    if (lines.Count > 0) return lines;
                }
            }

            using var search = await Http.GetAsync($"search?q={E($"{artist} {title}".Trim())}");
            if (!search.IsSuccessStatusCode) return (int)search.StatusCode >= 500 ? null : Empty;
            using var results = JsonDocument.Parse(await search.Content.ReadAsStringAsync());
            if (results.RootElement.ValueKind != JsonValueKind.Array) return Empty;
            var best = results.RootElement.EnumerateArray()
                .Where(e => Synced(e) != null)
                .OrderBy(e => duration.TotalSeconds >= 1 && e.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                    ? Math.Abs(d.GetDouble() - duration.TotalSeconds) : 0)
                .FirstOrDefault();
            return best.ValueKind == JsonValueKind.Object ? Parse(Synced(best)) : Empty;
        }
        catch (Exception ex)
        {
            AppHost.Log("Lyrics: " + ex.Message);
            return null;
        }
    }

    static string E(string s) => Uri.EscapeDataString(s ?? "");

    static string Synced(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty("syncedLyrics", out var s) && s.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(s.GetString()) ? s.GetString() : null;

    static readonly Regex Stamp = new(@"\[(\d{1,3}):(\d{1,2}(?:[.:]\d{1,3})?)\]", RegexOptions.Compiled);
    static readonly Regex WordStamp = new(@"<\d+:\d+(?:[.:]\d+)?>", RegexOptions.Compiled);

    internal static IReadOnlyList<LyricLine> Parse(string lrc)
    {
        var list = new List<LyricLine>();
        if (string.IsNullOrEmpty(lrc)) return list;
        foreach (var raw in lrc.Split('\n'))
        {
            var stamps = Stamp.Matches(raw);
            if (stamps.Count == 0) continue;
            string text = WordStamp.Replace(Stamp.Replace(raw, ""), "").Trim();
            if (text.Length == 0) text = "♪";
            foreach (Match m in stamps)
            {
                double secs = double.Parse(m.Groups[2].Value.Replace(':', '.'), CultureInfo.InvariantCulture);
                list.Add(new LyricLine(TimeSpan.FromMinutes(int.Parse(m.Groups[1].Value)) + TimeSpan.FromSeconds(secs), text));
            }
        }
        list.Sort((x, y) => x.Time.CompareTo(y.Time));
        return list;
    }

    const RegexOptions I = RegexOptions.IgnoreCase | RegexOptions.Compiled;
    static readonly Regex Noise = new(@"\s*[\(\[\{][^\)\]\}]*\b(official|video|audio|lyrics?|visuali[sz]er|m/?v|hd|hq|4k|explicit|clean|color coded|performance)\b[^\)\]\}]*[\)\]\}]", I);
    static readonly Regex Feat = new(@"\s*[\(\[]\s*(feat\.?|ft\.?|featuring)\s[^\)\]]*[\)\]]|\s+(feat\.?|ft\.?|featuring)\s.*$", I);
    static readonly Regex VersionSuffix = new(@"\s+[-–—]\s+[^-–—]*\b(remaster(ed)?|version|edit|mix|mono|stereo|live|acoustic|demo|from|bonus)\b.*$", I);
    static readonly Regex ArtistDash = new(@"^(.+?)\s+[-–—]\s+(.+)$", RegexOptions.Compiled);
    static readonly Regex ChannelSuffix = new(@"(\s*-\s*Topic|VEVO|\s+Official)$", I);

    /// Turns noisy player titles ("Coldplay - Yellow (Official Video)", channel "ColdplayVEVO") into a clean title/artist pair.
    internal static (string title, string artist) Clean(string title, string artist)
    {
        string original = (title ?? "").Trim();
        title = Noise.Replace(original, "");
        title = Regex.Replace(title, @"\s*[|｜].*$", "");
        title = Regex.Replace(title, @"\s*#\w+", "");
        artist = ChannelSuffix.Replace((artist ?? "").Trim(), "").Trim();

        title = VersionSuffix.Replace(title, "");
        var m = ArtistDash.Match(title);
        if (m.Success)
        {
            // YouTube convention "Artist - Title"
            artist = m.Groups[1].Value.Trim();
            title = m.Groups[2].Value.Trim();
        }
        title = Feat.Replace(title, "").Trim().Trim('"', '\'', '“', '”');
        return (title.Length > 0 ? title : original, artist);
    }
}

/// Floating Apple Music–style lyrics panel.
public sealed class LyricsWindow : Window
{
    public static LyricsWindow Instance { get; private set; }
    static double? _savedLeft, _savedTop;

    static readonly FontFamily TextFont = Isle.Controls.Ui.Display;
    static readonly Brush Dim = new SolidColorBrush(Color.FromArgb(0x99, 255, 255, 255));
    const double InactiveOpacity = 0.32;

    readonly TextBlock _title, _artist, _message;
    readonly StackPanel _lines;
    readonly Grid _viewport;
    readonly TranslateTransform _scroll = new();
    IReadOnlyList<LyricLine> _current;
    int _index = -2;
    string _shownTitle, _shownArtist;

    public static void Toggle()
    {
        if (Instance != null) Instance.Close();
        else new LyricsWindow().Show();
    }

    LyricsWindow()
    {
        Instance = this;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Width = 360;
        Height = 460;
        FontFamily = TextFont;
        var work = SystemParameters.WorkArea;
        Left = _savedLeft ?? work.Right - Width - 24;
        Top = _savedTop ?? work.Top + (work.Height - Height) / 2;

        _title = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis, Text = "Lyrics" };
        _artist = new TextBlock { FontSize = 13, Foreground = Dim, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(_title);
        titles.Children.Add(_artist);

        var close = new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Cursor = Cursors.Hand, Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Top, ToolTip = "Close",
            Background = new SolidColorBrush(Color.FromArgb(0x33, 255, 255, 255)),
            Child = new TextBlock
            {
                Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        close.MouseLeftButtonDown += (_, e) => e.Handled = true;
        close.MouseLeftButtonUp += (_, e) => { e.Handled = true; Close(); };

        var header = new Grid { Margin = new Thickness(20, 18, 16, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(titles);
        Grid.SetColumn(close, 1);
        header.Children.Add(close);

        // Canvas host: a Grid would layout-clip the tall list to the viewport before the scroll transform moves it.
        _lines = new StackPanel { RenderTransform = _scroll };
        Canvas.SetLeft(_lines, 22);
        var host = new Canvas();
        host.Children.Add(_lines);
        _message = new TextBlock
        {
            FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromArgb(0x80, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24),
        };
        var fade = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        fade.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
        fade.GradientStops.Add(new GradientStop(Colors.Black, 0.12));
        fade.GradientStops.Add(new GradientStop(Colors.Black, 0.82));
        fade.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        _viewport = new Grid { ClipToBounds = true, OpacityMask = fade, Margin = new Thickness(0, 0, 0, 14) };
        _viewport.Children.Add(host);
        _viewport.Children.Add(_message);
        _viewport.SizeChanged += (_, e) => _lines.Width = Math.Max(0, e.NewSize.Width - 44);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.Children.Add(header);
        Grid.SetRow(_viewport, 1);
        root.Children.Add(_viewport);

        Content = new Border
        {
            CornerRadius = new CornerRadius(22),
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x10, 0x10, 0x12)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x1F, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Child = root,
        };

        MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { } };
        SourceInitialized += (_, _) => Native.SetToolWindow(new WindowInteropHelper(this).Handle, false);
        Closed += (_, _) => { _savedLeft = Left; _savedTop = Top; Instance = null; };
        ShowMessage("Play music to see lyrics");
    }

    public void Update(string title, string artist, IReadOnlyList<LyricLine> lines, TimeSpan position, bool playing)
    {
        if (title != _shownTitle || artist != _shownArtist)
        {
            _shownTitle = title;
            _shownArtist = artist;
            _title.Text = string.IsNullOrWhiteSpace(title) ? "Lyrics" : title;
            _artist.Text = artist ?? "";
        }
        if (string.IsNullOrWhiteSpace(title)) { SetLines(null); ShowMessage("Play music to see lyrics"); return; }
        if (!ReferenceEquals(lines, _current)) SetLines(lines);
        if (lines == null || lines.Count == 0) { ShowMessage("No synced lyrics available"); return; }

        _message.Visibility = Visibility.Collapsed;
        _lines.Visibility = Visibility.Visible;
        // Highlight slightly early, like Apple Music, so the line lights up as it is sung.
        int idx = LyricsService.IndexAt(lines, position + TimeSpan.FromMilliseconds(150));
        if (idx != _index) Highlight(idx);
    }

    void ShowMessage(string text)
    {
        _message.Text = text;
        _message.Visibility = Visibility.Visible;
        _lines.Visibility = Visibility.Collapsed;
    }

    void SetLines(IReadOnlyList<LyricLine> lines)
    {
        _current = lines;
        _index = -2;
        _lines.Children.Clear();
        _scroll.BeginAnimation(TranslateTransform.YProperty, null);
        _scroll.Y = 0;
        if (lines == null) return;
        foreach (var line in lines)
            _lines.Children.Add(new TextBlock
            {
                Text = line.Text, FontSize = 24, FontWeight = FontWeights.Bold, Foreground = Brushes.White, Opacity = InactiveOpacity,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 7),
                RenderTransformOrigin = new Point(0, 0.5), RenderTransform = new ScaleTransform(0.96, 0.96),
            });
    }

    void Highlight(int idx)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        for (int i = 0; i < _lines.Children.Count; i++)
        {
            var tb = (TextBlock)_lines.Children[i];
            bool active = i == idx;
            if (active == (i == _index)) continue; // only the old and new current lines change
            tb.BeginAnimation(OpacityProperty, new DoubleAnimation(active ? 1 : InactiveOpacity, TimeSpan.FromMilliseconds(260)));
            var scale = (ScaleTransform)tb.RenderTransform;
            var grow = new DoubleAnimation(active ? 1 : 0.96, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }
        _index = idx;
        if (_lines.Children.Count == 0) return;

        _lines.UpdateLayout();
        var anchor = (FrameworkElement)_lines.Children[Math.Max(0, idx)];
        double center = anchor.TranslatePoint(new Point(0, 0), _lines).Y + anchor.ActualHeight / 2;
        double target = _viewport.ActualHeight / 3 - center;
        _scroll.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(target, TimeSpan.FromMilliseconds(520)) { EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut } });
    }
}
