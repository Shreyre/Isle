using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Isle.Controls;

static class Ui
{
    // Apple typography: real SF Pro if the user has it installed, else the bundled Inter (OFL), which was drawn to match it.
    static readonly Uri FontBase = new("pack://application:,,,/");
    public static readonly FontFamily Font = new(FontBase, "SF Pro Text, ./Assets/Fonts/#Inter, Segoe UI");
    public static readonly FontFamily Display = new(FontBase, "SF Pro Display, ./Assets/Fonts/#Inter Display, ./Assets/Fonts/#Inter, Segoe UI");
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    public static SolidColorBrush Brush(Color c, double alpha = 1)
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)(c.A * alpha), c.R, c.G, c.B));
        b.Freeze();
        return b;
    }
    public static double Ease(double current, double target, double rate, double dt) => current + (target - current) * (1 - Math.Exp(-rate * dt));
    public static double OutCubic(double t) => 1 - Math.Pow(1 - t, 3);
    /// Overshoots ~8% then settles: the "pop" Apple uses for symbols appearing.
    public static double OutBack(double t) { const double c = 1.70158; t -= 1; return 1 + (c + 1) * t * t * t + c * t * t; }
}

/// Apple-style music waveform: rounded vertical bars mirrored around the centre line.
public sealed class Waveform : FrameworkElement
{
    public int Bars { get; set; } = 6;
    public Color Color { get; set; } = Colors.White;
    readonly double[] _v = new double[32];
    double _t;
    LinearGradientBrush _brush;
    Color _brushColor;

    public void Push(float[] bands, bool playing, bool hasSignal, double dt)
    {
        _t += dt;
        for (int i = 0; i < Bars; i++)
        {
            double target = 0;
            if (playing)
            {
                if (hasSignal && bands != null)
                {
                    int per = Math.Max(1, bands.Length / Bars);
                    double sum = 0;
                    for (int k = 0; k < per; k++) sum += bands[Math.Min(bands.Length - 1, i * per + k)];
                    target = sum / per;
                }
                else // no loopback data available: gentle synthetic motion like Apple's fallback
                    target = 0.35 + 0.3 * Math.Sin(_t * (5 + i * 0.7) + i * 1.9) * Math.Sin(_t * 2.3 + i);
            }
            double rate = target > _v[i] ? 28 : 10;
            _v[i] = Ui.Ease(_v[i], Math.Clamp(target, 0, 1), rate, dt);
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        double bw = Math.Max(2, Math.Min(4, w / (Bars * 1.9)));
        double gap = Bars > 1 ? (w - bw * Bars) / (Bars - 1) : 0;
        if (_brush == null || _brushColor != Color)
        {
            _brushColor = Color;
            _brush = new LinearGradientBrush(Color, Color.FromArgb(200, Color.R, Color.G, Color.B), 90);
            _brush.Freeze();
        }
        var brush = _brush;
        for (int i = 0; i < Bars; i++)
        {
            double bh = bw + (h - bw) * _v[i];
            dc.DrawRoundedRectangle(brush, null, new Rect(i * (bw + gap), (h - bh) / 2, bw, bh), bw / 2, bw / 2);
        }
    }
}

/// Media seekbar. Modern = Apple scrubber that thickens on hover; Thin = hairline; Soundwave = animated wavy line.
public sealed class SeekBar : FrameworkElement
{
    public double Progress { get; set; }
    public string BarStyle { get; set; } = "Modern";
    public Color Accent { get; set; } = Colors.White;
    public bool Playing { get; set; }
    public event Action<double> Seeked;
    public event Action<double> Scrubbing;

    bool _drag, _hover;
    double _dragValue, _phase, _amp, _thick = 6;

    public bool IsDragging => _drag;
    public double Shown => _drag ? _dragValue : Math.Clamp(Progress, 0, 1);

    public SeekBar() { Cursor = Cursors.Hand; Height = 18; }

    public void Tick(double dt)
    {
        _phase += dt * 7;
        _amp = Ui.Ease(_amp, Playing ? 1 : 0, 6, dt);
        double t = BarStyle == "Thin" ? 3 : (_hover || _drag) ? 10 : 6;
        _thick = Ui.Ease(_thick, t, 18, dt);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight, mid = h / 2, p = Shown;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var track = Ui.Brush(Colors.White, 0.22);
        if (BarStyle == "Soundwave")
        {
            double x1 = w * p;
            var pen = new Pen(Ui.Brush(Accent), 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            var rest = new Pen(track, 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            if (x1 < w - 2) dc.DrawLine(rest, new Point(Math.Max(x1 + 4, 1.5), mid), new Point(w - 1.5, mid));
            if (x1 > 2)
            {
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(new Point(1.5, mid), false, false);
                    for (double x = 1.5; x <= x1; x += 1.5)
                        c.LineTo(new Point(x, mid + Math.Sin(x / 14.0 * 2 * Math.PI - _phase) * 3.2 * _amp), true, true);
                }
                g.Freeze();
                dc.DrawGeometry(null, pen, g);
            }
            dc.DrawRoundedRectangle(Ui.Brush(Colors.White), null, new Rect(x1 - 2, mid - 7, 4, 14), 2, 2);
            return;
        }
        double t = _thick, r = t / 2;
        var fill = BarStyle == "Thin" ? Ui.Brush(Accent) : Ui.Brush(Colors.White, _hover || _drag ? 1 : 0.92);
        dc.DrawRoundedRectangle(track, null, new Rect(0, mid - r, w, t), r, r);
        if (p > 0) dc.DrawRoundedRectangle(fill, null, new Rect(0, mid - r, Math.Max(t, w * p), t), r, r);
    }

    protected override void OnMouseEnter(MouseEventArgs e) { _hover = true; }
    protected override void OnMouseLeave(MouseEventArgs e) { _hover = false; }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _drag = true;
        CaptureMouse();
        Update(e);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e) { if (_drag) Update(e); }

    protected override void OnLostMouseCapture(MouseEventArgs e) { _drag = false; InvalidateVisual(); }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_drag) return;
        _drag = false;
        ReleaseMouseCapture();
        Progress = _dragValue;
        Seeked?.Invoke(_dragValue);
        e.Handled = true;
    }

    void Update(MouseEventArgs e)
    {
        _dragValue = Math.Clamp(e.GetPosition(this).X / Math.Max(1, ActualWidth), 0, 1);
        Scrubbing?.Invoke(_dragValue);
        InvalidateVisual();
    }
}

/// Nothing-OS style LED dot-matrix text (digits, colon, A/P/M, space).
public sealed class DotMatrixText : FrameworkElement
{
    static readonly Dictionary<char, string[]> Glyphs = new()
    {
        ['0'] = new[] { "01110", "10001", "10011", "10101", "11001", "10001", "01110" },
        ['1'] = new[] { "00100", "01100", "00100", "00100", "00100", "00100", "01110" },
        ['2'] = new[] { "01110", "10001", "00001", "00010", "00100", "01000", "11111" },
        ['3'] = new[] { "11111", "00010", "00100", "00010", "00001", "10001", "01110" },
        ['4'] = new[] { "00010", "00110", "01010", "10010", "11111", "00010", "00010" },
        ['5'] = new[] { "11111", "10000", "11110", "00001", "00001", "10001", "01110" },
        ['6'] = new[] { "00110", "01000", "10000", "11110", "10001", "10001", "01110" },
        ['7'] = new[] { "11111", "00001", "00010", "00100", "01000", "01000", "01000" },
        ['8'] = new[] { "01110", "10001", "10001", "01110", "10001", "10001", "01110" },
        ['9'] = new[] { "01110", "10001", "10001", "01111", "00001", "00010", "01100" },
        [':'] = new[] { "0", "0", "1", "0", "1", "0", "0" },
        [' '] = new[] { "00", "00", "00", "00", "00", "00", "00" },
        ['A'] = new[] { "01110", "10001", "10001", "11111", "10001", "10001", "10001" },
        ['P'] = new[] { "11110", "10001", "10001", "11110", "10000", "10000", "10000" },
        ['M'] = new[] { "10001", "11011", "10101", "10101", "10001", "10001", "10001" },
    };

    string _text = "";
    public string Text { get => _text; set { if (_text == value) return; _text = value ?? ""; InvalidateMeasure(); InvalidateVisual(); } }
    public double Dot { get; set; } = 2.4;
    public Color Color { get; set; } = Colors.White;

    double Pitch => Dot * 1.45;
    int Columns => _text.Sum(c => (Glyphs.TryGetValue(c, out var g) ? g[0].Length : 2) + 1) - 1;

    protected override Size MeasureOverride(Size available) => new(Math.Max(0, Columns) * Pitch, 7 * Pitch);

    protected override void OnRender(DrawingContext dc)
    {
        var on = Ui.Brush(Color);
        var off = Ui.Brush(Color, 0.10);
        double x = 0, r = Dot / 2;
        foreach (char ch in _text)
        {
            var g = Glyphs.TryGetValue(ch, out var gg) ? gg : Glyphs[' '];
            int cols = g[0].Length;
            for (int row = 0; row < 7; row++)
                for (int col = 0; col < cols; col++)
                    dc.DrawEllipse(g[row][col] == '1' ? on : off, null, new Point(x + col * Pitch + r, row * Pitch + r), r, r);
            x += (cols + 1) * Pitch;
        }
    }
}

/// Idle-widget eyes: "Animated" glowing robot eyes in the accent colour, or "White" cartoon eyeballs. Both follow the cursor and blink.
public sealed class EyesControl : FrameworkElement
{
    public bool WhiteStyle { get; set; }
    public Color Accent { get; set; } = Color.FromRgb(0x70, 0xE0, 0xC0);
    Vector _look, _lookTarget;
    double _blink = 1, _nextBlink = 2, _clock, _happy;
    static readonly Random Rnd = new();

    /// dir components in -1..1
    public void Look(Vector dir) => _lookTarget = new Vector(Math.Clamp(dir.X, -1, 1), Math.Clamp(dir.Y, -1, 1));
    public void Happy() => _happy = 1.6;

    public void Tick(double dt)
    {
        _clock += dt;
        _look = new Vector(Ui.Ease(_look.X, _lookTarget.X, 9, dt), Ui.Ease(_look.Y, _lookTarget.Y, 9, dt));
        _happy = Math.Max(0, _happy - dt);
        _nextBlink -= dt;
        if (_nextBlink <= 0) { _blink = 0; _nextBlink = 2.5 + Rnd.NextDouble() * 4; }
        _blink = Math.Min(1, _blink + dt * 7);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0) return;
        double open = _blink < 1 ? Math.Max(0.08, 1 - Math.Sin(_blink * Math.PI)) : 1; // dips to closed mid-blink
        double eyeW = WhiteStyle ? h * 0.78 : h * 0.42, eyeH = h * 0.78;
        double gap = WhiteStyle ? h * 0.28 : h * 0.5;
        double cx = w / 2, cy = h / 2;
        for (int s = -1; s <= 1; s += 2)
        {
            double ex = cx + s * (gap / 2 + eyeW / 2);
            if (WhiteStyle)
            {
                double rx = eyeW / 2, ry = eyeH / 2 * open;
                dc.DrawEllipse(Brushes.White, null, new Point(ex, cy), rx, Math.Max(1, ry));
                if (open > 0.3)
                {
                    double pr = eyeW * 0.24;
                    var pc = new Point(ex + _look.X * (rx - pr - 1), cy + _look.Y * (ry - pr - 1));
                    dc.DrawEllipse(Brushes.Black, null, pc, pr, Math.Min(pr, ry * 0.8));
                    dc.DrawEllipse(Brushes.White, null, new Point(pc.X + pr * 0.35, pc.Y - pr * 0.35), pr * 0.25, pr * 0.25);
                }
            }
            else
            {
                double ox = _look.X * h * 0.22, oy = _look.Y * h * 0.12;
                var brush = Ui.Brush(Accent);
                if (_happy > 0)
                {
                    // ^ ^ happy eyes
                    var pen = new Pen(brush, eyeW * 0.45) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                    var g = new StreamGeometry();
                    using (var c = g.Open())
                    {
                        c.BeginFigure(new Point(ex - eyeW * 0.7 + ox, cy + eyeH * 0.15), false, false);
                        c.LineTo(new Point(ex + ox, cy - eyeH * 0.2), true, true);
                        c.LineTo(new Point(ex + eyeW * 0.7 + ox, cy + eyeH * 0.15), true, true);
                    }
                    dc.DrawGeometry(null, pen, g);
                    continue;
                }
                double hh = Math.Max(eyeW * 0.35, eyeH * open);
                var glow = Ui.Brush(Accent, 0.22);
                dc.DrawRoundedRectangle(glow, null, new Rect(ex - eyeW / 2 - 2 + ox, cy - hh / 2 - 2 + oy, eyeW + 4, hh + 4), eyeW / 2 + 2, eyeW / 2 + 2);
                dc.DrawRoundedRectangle(brush, null, new Rect(ex - eyeW / 2 + ox, cy - hh / 2 + oy, eyeW, hh), eyeW / 2, eyeW / 2);
            }
        }
    }
}

/// Little companion that lives on the island: walks (wrapping round the ends), sits, sleeps, backflips, loves being petted.
public sealed class PetControl : FrameworkElement
{
    enum Mode { Walk, Sit, Sleep, Flip, Love }
    static readonly Random Rnd = new();
    public string Kind { get; set; } = "Cat";
    public bool UpsideDown { get; set; }
    Mode _mode = Mode.Walk;
    double _modeLeft = 4, _x, _dir = 1, _walkPhase, _flip, _fade = 1, _clock;
    Point _cursor;
    readonly List<(double x, double y, double life)> _hearts = new();
    readonly List<(double x, double y, double life)> _zs = new();

    public PetControl() { Width = 34; Height = 30; Cursor = Cursors.Hand; ToolTip = "Pet me!"; }

    /// Called every frame with the island rect (in parent canvas coordinates).
    public void Tick(double dt, Rect island, bool islandBusy, Point cursorInCanvas)
    {
        _clock += dt;
        _cursor = cursorInCanvas;
        _modeLeft -= dt;
        double left = island.Left - 6, right = island.Right - Width + 6;
        if (_x == 0) _x = (left + right) / 2;

        switch (_mode)
        {
            case Mode.Walk:
                _walkPhase += dt * 9;
                _x += _dir * dt * 26;
                // Magic edge wrap: fade out at one end, reappear at the other.
                if (_x > right + 4) { _fade -= dt * 6; if (_fade <= 0) { _x = left; _fade = 0.01; } }
                else if (_x < left - 4) { _fade -= dt * 6; if (_fade <= 0) { _x = right; _fade = 0.01; } }
                else _fade = Math.Min(1, _fade + dt * 4);
                if (_modeLeft <= 0) Next(islandBusy);
                break;
            case Mode.Flip:
                _flip += dt * 2.2;
                if (_flip >= 1) { _flip = 0; Set(Mode.Sit, 1.5); }
                break;
            default:
                if (_modeLeft <= 0) Next(islandBusy);
                break;
        }
        _x = Math.Clamp(_x, left - 10, right + 10);

        for (int i = _hearts.Count - 1; i >= 0; i--) { var h = _hearts[i]; h.y -= dt * 22; h.life -= dt; if (h.life <= 0) _hearts.RemoveAt(i); else _hearts[i] = h; }
        if (_mode == Mode.Sleep && Rnd.NextDouble() < dt * 0.8) _zs.Add((Width * 0.7, 4, 1.6));
        for (int i = _zs.Count - 1; i >= 0; i--) { var z = _zs[i]; z.y -= dt * 8; z.x += dt * 6; z.life -= dt; if (z.life <= 0) _zs.RemoveAt(i); else _zs[i] = z; }

        System.Windows.Controls.Canvas.SetLeft(this, _x);
        System.Windows.Controls.Canvas.SetTop(this, UpsideDown ? island.Bottom - 2 : island.Top - Height + 3);
        Opacity = Math.Clamp(_fade, 0, 1);
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = new ScaleTransform(1, UpsideDown ? -1 : 1);
        InvalidateVisual();
    }

    void Next(bool islandBusy)
    {
        double r = Rnd.NextDouble();
        if (r < 0.45) { _dir = Rnd.NextDouble() < 0.5 ? -1 : 1; Set(Mode.Walk, 3 + Rnd.NextDouble() * 6); }
        else if (r < 0.75) Set(Mode.Sit, 2 + Rnd.NextDouble() * 4);
        else if (r < 0.92 && !islandBusy) Set(Mode.Sleep, 8 + Rnd.NextDouble() * 14);
        else { _flip = 0; Set(Mode.Flip, 1); }
    }

    void Set(Mode m, double secs) { _mode = m; _modeLeft = secs; }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (e.ClickCount >= 2) { _flip = 0; Set(Mode.Flip, 1); return; }
        Set(Mode.Love, 1.8);
        for (int i = 0; i < 3; i++) _hearts.Add((6 + Rnd.NextDouble() * 20, 4, 1.2 + i * 0.2));
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = Width, h = Height;
        bool ghost = Kind == "Ghost";
        Color body = Kind switch { "Dog" => Color.FromRgb(0xD9, 0xA0, 0x66), "Bunny" => Color.FromRgb(0xF2, 0xF2, 0xF2), "Ghost" => Color.FromArgb(235, 245, 245, 255), _ => Color.FromRgb(0xF4, 0xA2, 0x61) };
        var fill = Ui.Brush(body);
        var outline = new Pen(Ui.Brush(Color.FromRgb(30, 30, 30)), 1.2);
        double facing = _mode == Mode.Walk ? _dir : (_cursor.X > System.Windows.Controls.Canvas.GetLeft(this) + w / 2 ? 1 : -1);

        dc.PushTransform(new ScaleTransform(facing, 1, w / 2, h / 2));
        if (_mode == Mode.Flip)
        {
            double a = _flip * 360, lift = Math.Sin(_flip * Math.PI) * 12;
            dc.PushTransform(new TranslateTransform(0, -lift));
            dc.PushTransform(new RotateTransform(-a, w / 2, h * 0.6));
        }

        bool sleeping = _mode == Mode.Sleep;
        double bob = _mode == Mode.Walk ? Math.Abs(Math.Sin(_walkPhase)) * 1.2 : sleeping ? Math.Sin(_clock * 2) * 0.6 : 0;
        if (ghost) bob = Math.Sin(_clock * 3) * 2;
        double bodyY = h * 0.62 - bob;

        if (ghost)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(w * 0.2, h * 0.95 - bob), true, true);
                c.LineTo(new Point(w * 0.2, h * 0.45 - bob), true, true);
                c.ArcTo(new Point(w * 0.8, h * 0.45 - bob), new Size(w * 0.3, h * 0.35), 0, false, SweepDirection.Clockwise, true, true);
                c.LineTo(new Point(w * 0.8, h * 0.95 - bob), true, true);
                for (int i = 0; i < 4; i++)
                    c.LineTo(new Point(w * (0.8 - (i + 0.5) * 0.15), h * (i % 2 == 0 ? 0.85 : 0.95) - bob), true, true);
            }
            dc.DrawGeometry(fill, outline, g);
        }
        else
        {
            // legs
            var legPen = new Pen(Ui.Brush(Color.FromRgb(30, 30, 30)), 2.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            if (!sleeping)
                for (int i = 0; i < 4; i++)
                {
                    double lx = w * (0.3 + i * 0.12);
                    double swing = _mode == Mode.Walk ? Math.Sin(_walkPhase + i * Math.PI / 2) * 2.5 : 0;
                    dc.DrawLine(legPen, new Point(lx, bodyY + 3), new Point(lx + swing, h - 1));
                }
            // tail
            var tailPen = new Pen(fill, Kind == "Bunny" ? 0.1 : 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            if (Kind == "Bunny") dc.DrawEllipse(fill, outline, new Point(w * 0.18, bodyY - 1), 3.5, 3.5);
            else
            {
                double wag = Math.Sin(_clock * (Kind == "Dog" ? 14 : 3)) * 3;
                var tg = new StreamGeometry();
                using (var c = tg.Open())
                {
                    c.BeginFigure(new Point(w * 0.22, bodyY - 2), false, false);
                    c.QuadraticBezierTo(new Point(w * 0.05, bodyY - 6), new Point(w * 0.08 + wag * 0.3, bodyY - (Kind == "Dog" ? 8 : 13) + wag), true, true);
                }
                dc.DrawGeometry(null, new Pen(Ui.Brush(Color.FromRgb(30, 30, 30)), 4.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, tg);
                dc.DrawGeometry(null, tailPen, tg);
            }
            // body
            dc.DrawEllipse(fill, outline, new Point(w * 0.47, bodyY), w * 0.3, sleeping ? h * 0.2 : h * 0.22);
            // head
            double hx = w * 0.74, hy = sleeping ? bodyY + 1 : bodyY - h * 0.22;
            // ears
            var earG = new StreamGeometry();
            using (var c = earG.Open())
            {
                if (Kind == "Bunny")
                {
                    c.BeginFigure(new Point(hx - 3, hy - 5), true, true); c.LineTo(new Point(hx - 5, hy - 17), true, true); c.LineTo(new Point(hx - 0.5, hy - 6), true, true);
                    c.BeginFigure(new Point(hx + 1, hy - 5), true, true); c.LineTo(new Point(hx + 3, hy - 17), true, true); c.LineTo(new Point(hx + 4, hy - 5), true, true);
                }
                else if (Kind == "Dog")
                {
                    c.BeginFigure(new Point(hx - 5, hy - 4), true, true); c.LineTo(new Point(hx - 8, hy + 4), true, true); c.LineTo(new Point(hx - 3, hy + 2), true, true);
                }
                else
                {
                    c.BeginFigure(new Point(hx - 6, hy - 3), true, true); c.LineTo(new Point(hx - 5, hy - 10), true, true); c.LineTo(new Point(hx - 1, hy - 5), true, true);
                    c.BeginFigure(new Point(hx + 1, hy - 5), true, true); c.LineTo(new Point(hx + 5, hy - 10), true, true); c.LineTo(new Point(hx + 6, hy - 3), true, true);
                }
            }
            dc.DrawGeometry(fill, outline, earG);
            dc.DrawEllipse(fill, outline, new Point(hx, hy), 7, 6.2);
            DrawFace(dc, hx + 1.5, hy, sleeping);
        }
        if (ghost) DrawFace(dc, w / 2, h * 0.55 - bob, sleeping);

        if (_mode == Mode.Flip) { dc.Pop(); dc.Pop(); }
        dc.Pop();

        var heart = Ui.Brush(Color.FromRgb(0xFF, 0x37, 0x5F));
        foreach (var hrt in _hearts) DrawText(dc, "♥", hrt.x, hrt.y, 9, heart, Math.Min(1, hrt.life));
        foreach (var z in _zs) DrawText(dc, "z", z.x, z.y, 8, Brushes.White, Math.Min(1, z.life));
    }

    void DrawFace(DrawingContext dc, double cx, double cy, bool sleeping)
    {
        var dark = Ui.Brush(Color.FromRgb(25, 25, 25));
        if (sleeping || _mode == Mode.Love)
        {
            var p = new Pen(dark, 1.1) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawLine(p, new Point(cx - 4, cy - 1), new Point(cx - 1.5, cy - (_mode == Mode.Love ? 2 : 1)));
            dc.DrawLine(p, new Point(cx + 1.5, cy - (_mode == Mode.Love ? 2 : 1)), new Point(cx + 4, cy - 1));
        }
        else
        {
            dc.DrawEllipse(dark, null, new Point(cx - 2.6, cy - 1), 1.2, 1.5);
            dc.DrawEllipse(dark, null, new Point(cx + 2.6, cy - 1), 1.2, 1.5);
        }
        dc.DrawEllipse(Ui.Brush(Color.FromRgb(0xFF, 0x8F, 0xA3)), null, new Point(cx, cy + 1.6), 1, 0.7);
    }

    static void DrawText(DrawingContext dc, string s, double x, double y, double size, Brush b, double alpha)
    {
        dc.PushOpacity(alpha);
        var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(Ui.Font, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), size, b, 1.25);
        dc.DrawText(ft, new Point(x, y));
        dc.Pop();
    }
}

/// Circular progress ring (battery level, timer minimal view).
public sealed class Ring : FrameworkElement
{
    public double Value { get; set; }
    public Color Color { get; set; } = Color.FromRgb(0x30, 0xD1, 0x58);
    public double Thickness { get; set; } = 3;

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight), r = (size - Thickness) / 2;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        dc.DrawEllipse(null, new Pen(Ui.Brush(Color, 0.25), Thickness), c, r, r);
        double v = Math.Clamp(Value, 0, 1);
        if (v <= 0) return;
        var pen = new Pen(Ui.Brush(Color), Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (v >= 0.999) { dc.DrawEllipse(null, pen, c, r, r); return; }
        double a = v * 2 * Math.PI;
        var start = new Point(c.X, c.Y - r);
        var end = new Point(c.X + r * Math.Sin(a), c.Y - r * Math.Cos(a));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(r, r), 0, a > Math.PI, SweepDirection.Clockwise, true, false);
        }
        dc.DrawGeometry(null, pen, g);
    }
}

/// iOS status-bar battery glyph.
public sealed class BatteryGlyph : FrameworkElement
{
    public double Level { get; set; } = 1;
    public bool Charging { get; set; }

    public BatteryGlyph() { Width = 27; Height = 13; }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth - 3, h = ActualHeight;
        Color fill = Charging ? Color.FromRgb(0x30, 0xD1, 0x58) : Level <= 0.2 ? Color.FromRgb(0xFF, 0x45, 0x3A) : Colors.White;
        dc.DrawRoundedRectangle(null, new Pen(Ui.Brush(Colors.White, 0.4), 1), new Rect(0.5, 0.5, w - 1, h - 1), 3.5, 3.5);
        dc.DrawRoundedRectangle(Ui.Brush(Colors.White, 0.4), null, new Rect(w + 0.5, h * 0.33, 2, h * 0.34), 1, 1);
        double fw = Math.Max(2, (w - 4) * Math.Clamp(Level, 0, 1));
        dc.DrawRoundedRectangle(Ui.Brush(fill), null, new Rect(2, 2, fw, h - 4), 2, 2);
        if (Charging)
        {
            var g = Geometry.Parse("M 14,1 L 8,7 L 12,7 L 10,12 L 17,5.5 L 13,5.5 Z");
            g.Transform = new ScaleTransform(w / 26, h / 13);
            dc.DrawGeometry(Brushes.White, new Pen(Brushes.Black, 0.8), g);
        }
    }
}
