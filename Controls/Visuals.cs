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
    /// Sport glyphs (🏆 ⚽ 🏏 …) live outside the BMP, and WPF won't fall back to them from a font list: name the font.
    public static readonly FontFamily Symbols = new("Segoe UI Symbol");
    public static FontFamily GlyphFont(string glyph, FontFamily icons) => glyph.Length > 1 && char.IsSurrogate(glyph, 0) ? Symbols : icons;
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
    /// Cursor in local coordinates while it is close (null = far): unlit LEDs around it glow.
    Point? _pointer;
    public Point? Pointer { get => _pointer; set { if (_pointer == value) return; _pointer = value; InvalidateVisual(); } }
    Brush[] _glow;
    Color _glowFor;

    double Pitch => Dot * 1.45;
    int Columns => _text.Sum(c => (Glyphs.TryGetValue(c, out var g) ? g[0].Length : 2) + 1) - 1;

    protected override Size MeasureOverride(Size available) => new(Math.Max(0, Columns) * Pitch, 7 * Pitch);

    protected override void OnRender(DrawingContext dc)
    {
        var on = Ui.Brush(Color);
        if (_glow == null || _glowFor != Color) // unlit LED brightness steps, 10% .. 55%
        {
            _glowFor = Color;
            _glow = Enumerable.Range(0, 8).Select(i => (Brush)Ui.Brush(Color, 0.10 + 0.45 * i / 7)).ToArray();
        }
        double x = 0, r = Dot / 2;
        foreach (char ch in _text)
        {
            var g = Glyphs.TryGetValue(ch, out var gg) ? gg : Glyphs[' '];
            int cols = g[0].Length;
            for (int row = 0; row < 7; row++)
                for (int col = 0; col < cols; col++)
                {
                    var c = new Point(x + col * Pitch + r, row * Pitch + r);
                    double lit = _pointer is { } p ? Math.Clamp(1 - (c - p).Length / 26, 0, 1) : 0;
                    dc.DrawEllipse(g[row][col] == '1' ? on : _glow[(int)Math.Round(lit * 7)], null, c, r, r);
                }
            x += (cols + 1) * Pitch;
        }
    }
}

/// Apple Clock-icon face: white dial, black hands and a sweeping orange second hand.
public sealed class AnalogClock : FrameworkElement
{
    static readonly Pen Hour = Hand(Colors.Black, 2), Minute = Hand(Colors.Black, 1.4),
        Second = Hand(Color.FromRgb(0xFF, 0x9F, 0x0A), 0.8), Tick = Hand(Color.FromRgb(0x8E, 0x8E, 0x93), 0.7);
    static Pen Hand(Color c, double w) { var p = new Pen(Ui.Brush(c), w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }; p.Freeze(); return p; }

    DateTime _time;
    public DateTime Time { get => _time; set { _time = value; InvalidateVisual(); } }

    protected override void OnRender(DrawingContext dc)
    {
        double r = Math.Min(ActualWidth, ActualHeight) / 2;
        if (r <= 0) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        Point At(double turns, double len) => new(c.X + len * r * Math.Sin(turns * 2 * Math.PI), c.Y - len * r * Math.Cos(turns * 2 * Math.PI));
        dc.DrawEllipse(Brushes.White, null, c, r, r);
        for (int i = 0; i < 12; i++) dc.DrawLine(Tick, At(i / 12.0, 0.8), At(i / 12.0, 0.92));
        var t = _time.TimeOfDay;
        dc.DrawLine(Hour, c, At(t.TotalHours % 12 / 12, 0.5));
        dc.DrawLine(Minute, c, At(t.TotalMinutes % 60 / 60, 0.78));
        dc.DrawLine(Second, c, At(t.TotalSeconds % 60 / 60, 0.84));
        dc.DrawEllipse(Second.Brush, null, c, 1.4, 1.4);
    }
}

/// Siri-style glow that drifts along the capsule, chases the cursor as it comes near and stretches as it moves.
public sealed class Orb : FrameworkElement
{
    public Color Accent { get; set; } = Color.FromRgb(0x70, 0xE0, 0xC0);
    double _x = 0.5, _v, _clock, _near;
    Brush _halo, _core;
    Color _brushFor;

    /// target: cursor position across the orb's track (0..1); near: 0..1 how close the cursor is to the island.
    public void Tick(double target, double near, double dt)
    {
        _clock += dt;
        _near = Ui.Ease(_near, near, 8, dt);
        double wander = 0.5 + 0.35 * Math.Sin(_clock * 0.6) * Math.Sin(_clock * 0.23 + 1);
        double goal = wander + (Math.Clamp(target, 0, 1) - wander) * _near;
        double prev = _x;
        _x = Ui.Ease(_x, goal, 7, dt);
        _v = Ui.Ease(_v, (_x - prev) / dt, 12, dt);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        if (_halo == null || _brushFor != Accent)
        {
            _brushFor = Accent;
            var a = Accent;
            _halo = new RadialGradientBrush(Color.FromArgb(110, a.R, a.G, a.B), Color.FromArgb(0, a.R, a.G, a.B));
            _core = new RadialGradientBrush(new GradientStopCollection
            {
                new(Colors.White, 0), new(a, 0.55), new(Color.FromRgb((byte)(a.R * 0.6), (byte)(a.G * 0.6), (byte)(a.B * 0.6)), 1),
            }) { GradientOrigin = new Point(0.35, 0.3) };
            _halo.Freeze(); _core.Freeze();
        }
        double r = h / 2 * (0.6 + 0.08 * Math.Sin(_clock * 2.4) + 0.25 * _near); // breathes, swells as the cursor nears
        var c = new Point(h / 2 + (w - h) * _x, h / 2);
        double s = Math.Min(0.7, Math.Abs(_v) * 0.6); // squash & stretch with speed
        dc.PushTransform(new ScaleTransform(1 + s, 1 - s * 0.35, c.X, c.Y));
        dc.DrawEllipse(_halo, null, c, r * 1.9, r * 1.9);
        dc.DrawEllipse(_core, null, c, r, r);
        dc.Pop();
    }
}

/// Idle-widget eyes: "Animated" glowing robot eyes in the accent colour, or "White" cartoon eyeballs. Both follow the cursor and blink.
public sealed class EyesControl : FrameworkElement
{
    public bool WhiteStyle { get; set; }
    public Color Accent { get; set; } = Color.FromRgb(0x70, 0xE0, 0xC0);
    Vector _look, _lookTarget;
    double _blink = 1, _nextBlink = 2, _clock, _happy, _near, _nearTarget;
    static readonly Random Rnd = new();

    /// dir components in -1..1; near 0..1 = how close the cursor is (the eyes widen and the pupils dilate)
    public void Look(Vector dir, double near = 0)
    {
        _lookTarget = new Vector(Math.Clamp(dir.X, -1, 1), Math.Clamp(dir.Y, -1, 1));
        _nearTarget = Math.Clamp(near, 0, 1);
    }
    public void Happy() => _happy = 1.6;

    public void Tick(double dt)
    {
        _clock += dt;
        _look = new Vector(Ui.Ease(_look.X, _lookTarget.X, 16, dt), Ui.Ease(_look.Y, _lookTarget.Y, 16, dt));
        _near = Ui.Ease(_near, _nearTarget, 8, dt);
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
        double eyeW = WhiteStyle ? h * 0.78 : h * 0.42, eyeH = h * (0.78 + 0.16 * _near);
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
                    double pr = eyeW * (0.24 + 0.07 * _near);
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
/// Every pose blends (speed, facing, sit, lie all ease) so nothing snaps. Shapes, brushes and pens are frozen and the
/// per-part transforms are reused slots, so the 30 fps redraw allocates next to nothing.
public sealed class PetControl : FrameworkElement
{
    enum Mode { Walk, Sit, Sleep, Flip, Love }
    static readonly Random Rnd = new();

    static Pen P(Brush b, double t) { var p = new Pen(b, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }; p.Freeze(); return p; }
    static Geometry G(string data) { var g = Geometry.Parse(data); g.Freeze(); return g; }
    static double Lerp(double a, double b, double t) => a + (b - a) * t;
    static Point Rot(Point c, double dx, double dy, double deg)
    {
        double a = deg * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a);
        return new Point(c.X + dx * cos - dy * sin, c.Y + dx * sin + dy * cos);
    }

    static readonly SolidColorBrush Ink = Ui.Brush(Color.FromRgb(30, 30, 30)), White = Ui.Brush(Colors.White),
        Pink = Ui.Brush(Color.FromRgb(0xFF, 0x9E, 0xB1)), Heart = Ui.Brush(Color.FromRgb(0xFF, 0x37, 0x5F)), Hoof = Ui.Brush(Color.FromRgb(0x3A, 0x2A, 0x22));
    static readonly Pen Thin = P(Ink, 1.0), BandEdge = P(Ui.Brush(Color.FromRgb(30, 22, 18), 0.85), 0.7),
        Whisker = P(Ui.Brush(Colors.White, 0.7), 0.5), BubblePen = P(Ui.Brush(Colors.White, 0.85), 0.9);
    // Soft radial fills: glossy eyes, cheeks that glow pink when petted, the ghost's halo.
    static readonly Brush EyeFill = Radial(0.5, 0.75, 0.6, (0x4A3426, 1, 0), (0x140E0B, 1, 0.75)),
        CheekSoft = Radial(0.5, 0.5, 0.5, (0xFF6F91, 0.35, 0), (0xFF6F91, 0, 1)),
        CheekLove = Radial(0.5, 0.5, 0.5, (0xFF5C84, 0.75, 0), (0xFF5C84, 0, 1)),
        GhostGlow = Radial(0.5, 0.5, 0.5, (0xE6ECFF, 0.4, 0.4), (0xE6ECFF, 0, 1));
    static Color C(int rgb, double a = 1) => Color.FromArgb((byte)(255 * a), (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    static Brush Radial(double ox, double oy, double r, params (int rgb, double a, double at)[] stops)
    {
        var b = new RadialGradientBrush { GradientOrigin = new Point(ox, oy), Center = new Point(0.5, 0.5), RadiusX = r, RadiusY = r };
        foreach (var s in stops) b.GradientStops.Add(new GradientStop(C(s.rgb, s.a), s.at));
        b.Freeze();
        return b;
    }
    static readonly FormattedText HeartText = Txt("♥", 9, Heart), ZText = Txt("z", 8, White);
    static FormattedText Txt(string s, double size, Brush b) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(Ui.Font, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), size, b, 1.25);

    // Part shapes in their own local space (head centre, tail root, fin root…), all drawn facing right.
    static readonly Geometry CatEars = G("M-6.2,-2 L-5.2,-10.5 L-0.8,-5.2 Z M0.8,-5.2 L5.2,-10.5 L6.2,-2 Z"),
        CatEarsIn = G("M-4.9,-4 L-4.5,-8.2 L-2.3,-5.4 Z M2.3,-5.4 L4.5,-8.2 L4.9,-4 Z"),
        CatTail = G("M0,0 Q-7,-1 -5.5,-11"),
        DogTail = G("M0,0 Q-4,-2 -4.5,-7.5"),
        DogEar = G("M-3.5,-4.5 Q-8.5,-3 -7,3.5 Q-4,4.5 -2.5,-2 Z"),
        BunnyEars = G("M-3.6,-4.5 Q-7.5,-12 -4.6,-17.5 Q-1.6,-12 -1.2,-5 Z M0.6,-5.2 Q0.6,-13 3.6,-17.6 Q5.6,-11 2.8,-4.6 Z"),
        BunnyEarsIn = G("M-3.2,-6 Q-5.6,-12 -4.4,-15.6 Q-2.6,-11.5 -2.2,-6.2 Z M1.6,-6.2 Q1.8,-12.4 3.5,-15.6 Q4.4,-11 2.8,-6.4 Z"),
        // horse: barrel with a raised rump, dip at the withers and a deep chest; neck/mane run along +x from the withers
        HorseBody = G("M-12.4,-0.6 C-12.6,-5.6 -8.4,-7.2 -4,-6.2 C0,-5.4 5,-6.4 9,-5.6 C12.6,-4.8 13.6,0 11.6,3.4 C9.4,6.6 -4,7.2 -8.6,5.4 C-12,4 -12.3,2 -12.4,-0.6 Z"),
        HorseNeck = G("M-1,-3.8 C4,-3.6 8,-2.8 11.6,-2.3 L11.6,2.3 C8,2.6 4,3.4 -1,4 Z"),
        HorseMane = G("M-0.5,-3.6 Q1.6,-6.4 3,-3.5 Q4.8,-6.6 6.2,-3.1 Q8,-6.2 9.2,-2.8 Q11,-5.6 12,-2.4 L11.6,-1.6 C8,-2.4 4,-3 -0.5,-2.8 Z"),
        HorseHead = G("M-4,-3.4 C-1,-4.2 3,-3 5.6,-1.6 C7.4,-0.6 7.6,2 5.8,2.8 C3.6,3.6 1,3 -1.5,3.6 C-4,4 -5.6,1.6 -5.2,-0.6 C-5,-2 -4.8,-3 -4,-3.4 Z"),
        HorseBlaze = G("M-3.4,-3.5 C0,-3.9 3,-3 5.4,-1.5 L5.2,-0.9 C3,-2.2 0,-2.9 -3.4,-2.9 Z"),
        HorseForelock = G("M-4.6,-3.2 Q-2.8,-5.4 -1,-3.6 Q-2.2,-2.2 -3.8,-1.6 Z"),
        HorseEar = G("M-3.8,-2.6 L-3.4,-7.6 L-1.4,-3.4 Z"),
        HorseEarFar = G("M-2.6,-3 L-1.6,-7.4 L-0.4,-3.4 Z"),
        HorseTail = G("M0,0 C-3.2,0.6 -6.4,4 -6,9 C-5.8,12 -3.8,13.6 -2.2,13.2 C-3.4,10 -2.8,6 0.8,1.6 Z"),
        // clownfish: rounded head (+x) tapering to the tail root, black-trimmed fins
        FishBody = G("M9.6,0.4 C9.6,-4.4 5.4,-7 0,-7 C-5,-7 -8.6,-3.6 -10,0 C-8.6,3.6 -5,6.6 0,6.6 C5.4,6.6 9.6,4.6 9.6,0.4 Z"),
        FishTail = G("M0,0 C-2,-3 -5,-6.5 -7.5,-6 C-8.6,-3 -8.6,3 -7.5,6 C-5,6.5 -2,3 0,0 Z"),
        FishDorsal = G("M-6,-5.6 C-5,-9 -2,-9.6 -0.6,-7 C0.4,-9.6 4,-9.4 5.6,-5.8 Z"),
        FishAnal = G("M-5.4,5.2 C-4.4,8.8 -1.2,8.8 0,6.2 Z"),
        FishFin = G("M0,0 Q-1.5,3.5 -4.5,3.2 Q-3,1 0,0 Z");

    string _kind = "";
    Brush _fill = Ink, _shade = Ink, _accent = Ink, _cream = Ink;
    Pen _limb = Thin, _farLimb = Thin, _limbThick = Thin, _farThick = Thin, _thickEdge = Thin, _tailPen = Thin, _neck = Thin, _accentPen = Thin, _edge = Thin, _limbEdge = Thin, _tailEdge = Thin, _neckEdge = Thin;
    public string Kind
    {
        get => _kind;
        set
        {
            if (value == _kind) return;
            _kind = value;
            switch (value)
            {
                //                    light     body      dark      edge      accent    cream
                case "Dog": Palette(34, 30, 0xF8D6A4, 0xDDA25F, 0xB0763A, 0x7E5228, 0x9C6430, 0xFFF4E4, 2.3); break;
                case "Bunny": Palette(34, 30, 0xFFFFFF, 0xF3F3F7, 0xC9C9D6, 0x9898A8, 0xFFB3C1, 0xFFFFFF, 2.4); break;
                case "Ghost": Palette(34, 30, 0xFFFFFF, 0xEEF1FF, 0xBFC8F0, 0x97A2D8, 0xB7B7E0, 0xFFFFFF, 2.2, 0.9); break;
                case "Horse": Palette(42, 36, 0xCB8C57, 0xA0602F, 0x6B3B1A, 0x47260F, 0x2E1B10, 0xFFF8F0, 2.2); _accent = Radial(0.32, 0.25, 0.62, (0x6A452C, 1, 0), (0x2E1B10, 1, 1)); break;
                case "Fish": Palette(32, 26, 0xFFC27E, 0xFF8A2A, 0xDE5E08, 0xA8460A, 0xFFFFFF, 0xFFFFFF, 2.2); break;
                default: Palette(34, 30, 0xFFD6AC, 0xF4A261, 0xD47A3A, 0x9A5527, 0xC9682A, 0xFFF1E0, 2.3); break;
            }
        }
    }

    /// Every body part is filled with one top-left-lit radial gradient (relative to the part's own bounds, so each
    /// ear, head and leg gets its own highlight) and edged with a thin darker tint of the fur instead of black ink.
    void Palette(double w, double h, int light, int body, int dark, int edge, int accent, int cream, double limb, double alpha = 1)
    {
        Width = w; Height = h;
        _fill = Radial(0.32, 0.25, 0.62, (light, alpha, 0), (body, alpha, 0.5), (dark, alpha, 1));
        _shade = Radial(0.32, 0.25, 0.62, (body, alpha, 0), (dark, alpha, 1));
        _accent = Ui.Brush(C(accent));
        _cream = Ui.Brush(C(cream), alpha);
        var bodyB = Ui.Brush(C(body), alpha); var darkB = Ui.Brush(C(dark), alpha); var edgeB = Ui.Brush(C(edge), 0.8 * alpha);
        _edge = P(edgeB, 0.9);
        _limb = P(bodyB, limb); _farLimb = P(darkB, limb); _limbEdge = P(edgeB, limb + 1.4);
        _limbThick = P(bodyB, limb + 1.4); _farThick = P(darkB, limb + 1.4); _thickEdge = P(edgeB, limb + 2.8);
        _tailPen = P(bodyB, 2.8); _tailEdge = P(edgeB, 4.2);
        _neck = P(bodyB, 5.8); _neckEdge = P(edgeB, 7.2);
        _accentPen = P(Ui.Brush(C(accent), 0.75), 1.2);
    }

    /// Soft cheek glow, brighter while being petted.
    void Cheeks(DrawingContext dc, double x1, double x2, double y, double r)
    {
        var b = _love > 0 ? CheekLove : CheekSoft;
        dc.DrawEllipse(b, null, new Point(x1, y), r, r * 0.7);
        if (!double.IsNaN(x2)) dc.DrawEllipse(b, null, new Point(x2, y), r, r * 0.7);
    }

    public bool UpsideDown { get; set; }
    Mode _mode = Mode.Walk;
    double _modeLeft = 4, _x, _vx, _dir = 1, _face = 1, _faceTarget = 1, _gait, _swim, _flip, _fade = 1, _clock;
    double _sit, _graze, _lie, _squash, _love, _blink, _blinkIn = 3, _twitch, _twitchIn = 5, _lookX, _lookY;
    bool _flipped;
    readonly List<(double x, double y, double life)> _hearts = new(), _zs = new();
    readonly MatrixTransform[] _tf = { new(), new(), new(), new(), new(), new() };
    static readonly ScaleTransform FlipY = Freeze(new ScaleTransform(1, -1));
    static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    double Speed => _kind switch { "Horse" => 36, "Bunny" => 28, "Fish" => 22, "Ghost" => 18, _ => 24 };
    double GaitRate => _kind switch { "Horse" => 12, "Bunny" => 7.5, "Dog" => 11, _ => 9.5 };
    double Stride => Math.Min(1, Math.Abs(_vx) / Speed);

    public PetControl() { Kind = "Cat"; Cursor = Cursors.Hand; ToolTip = "Pet me!"; RenderTransformOrigin = new Point(0.5, 0.5); }

    /// Called every frame with the island rect (in parent canvas coordinates).
    public void Tick(double dt, Rect island, bool islandBusy, Point cursorInCanvas)
    {
        dt = Math.Min(dt, 0.1);
        _clock += dt;
        _modeLeft -= dt;
        double left = island.Left - 6, right = island.Right - Width + 6;
        if (_x == 0) _x = (left + right) / 2;

        // Momentum: ease into and out of a walk instead of starting and stopping dead.
        _vx = Ui.Ease(_vx, _mode == Mode.Walk ? _dir * Speed : 0, 5, dt);
        _x += _vx * dt;
        _gait += dt * GaitRate * Stride;
        _swim += dt * Lerp(7 + Stride * 9, 2.5, _lie);
        // Magic edge wrap: fade out at one end, reappear at the other.
        if (_x > right + 4) { _fade -= dt * 6; if (_fade <= 0) { _x = left; _fade = 0.01; } }
        else if (_x < left - 4) { _fade -= dt * 6; if (_fade <= 0) { _x = right; _fade = 0.01; } }
        else _fade = Math.Min(1, _fade + dt * 4);
        _x = Math.Clamp(_x, left - 10, right + 10);

        if (_mode == Mode.Flip)
        {
            _flip += dt * 1.5;
            if (_flip >= 1) { _flip = 0; _squash = 1; Set(Mode.Sit, 1.5); }
        }
        else if (_modeLeft <= 0) Next(islandBusy);

        double top = UpsideDown ? island.Bottom - 2 : island.Top - Height + 3;
        double cx = _x + Width / 2, cy = top + Height / 2;
        // Walking faces the way it goes; otherwise it turns to watch a nearby cursor (not while asleep).
        if (_mode == Mode.Walk) _faceTarget = _dir;
        else if (_lie < 0.3 && Math.Abs(cursorInCanvas.X - cx) is > 8 and < 220 && Math.Abs(cursorInCanvas.Y - cy) < 140)
            _faceTarget = cursorInCanvas.X > cx ? 1 : -1;
        _face = Ui.Ease(_face, _faceTarget, 10, dt);
        double sgn = _face < 0 ? -1 : 1;
        _lookX = Ui.Ease(_lookX, Math.Clamp((cursorInCanvas.X - cx) / 60, -1, 1) * sgn, 8, dt);
        _lookY = Ui.Ease(_lookY, Math.Clamp((cursorInCanvas.Y - cy) / 60, -1, 1) * (UpsideDown ? -1 : 1), 8, dt);

        _sit = Ui.Ease(_sit, _mode == Mode.Sit || (_mode == Mode.Love && _kind != "Horse") ? 1 : 0, 6, dt);
        _graze = Ui.Ease(_graze, _mode == Mode.Sit ? 1 : 0, 3, dt);
        _lie = Ui.Ease(_lie, _mode == Mode.Sleep ? 1 : 0, 3, dt);
        _squash = Math.Max(0, _squash - dt * 3.5);
        _love = Math.Max(0, _love - dt);
        _blinkIn -= dt; if (_blinkIn <= 0) { _blink = 0.14; _blinkIn = 2 + Rnd.NextDouble() * 4; }
        _blink = Math.Max(0, _blink - dt);
        _twitchIn -= dt; if (_twitchIn <= 0) { _twitch = 0.25; _twitchIn = 3 + Rnd.NextDouble() * 6; }
        _twitch = Math.Max(0, _twitch - dt);

        for (int i = _hearts.Count - 1; i >= 0; i--) { var h = _hearts[i]; h.y -= dt * 22; h.x += Math.Sin(h.life * 7) * dt * 10; h.life -= dt; if (h.life <= 0) _hearts.RemoveAt(i); else _hearts[i] = h; }
        bool fish = _kind == "Fish";
        if (fish) { if (Rnd.NextDouble() < dt * (_lie > 0.5 ? 1.2 : 0.45)) _zs.Add((Width / 2 + 9.5 * sgn, Height * 0.42, 2)); }
        else if (_mode == Mode.Sleep && _lie > 0.6 && Rnd.NextDouble() < dt * 0.8) _zs.Add((sgn > 0 ? Width * 0.72 : Width * 0.12, 2, 1.6));
        for (int i = _zs.Count - 1; i >= 0; i--)
        {
            var z = _zs[i];
            z.y -= dt * (fish ? 10 : 8);
            z.x += fish ? Math.Sin(z.life * 9) * dt * 6 : dt * 6 * sgn;
            z.life -= dt;
            if (z.life <= 0) _zs.RemoveAt(i); else _zs[i] = z;
        }

        System.Windows.Controls.Canvas.SetLeft(this, _x);
        System.Windows.Controls.Canvas.SetTop(this, top);
        Opacity = Math.Clamp(_fade, 0, 1);
        if (_flipped != UpsideDown) { _flipped = UpsideDown; RenderTransform = UpsideDown ? FlipY : Transform.Identity; }
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
        _love = 1.8;
        Set(Mode.Love, 1.8);
        for (int i = 0; i < 3; i++) _hearts.Add((6 + Rnd.NextDouble() * (Width - 14), 4, 1.2 + i * 0.2));
    }

    /// Pushes a reused transform: scale, then rotate (degrees), then move to (x, y). Each slot is used once per frame.
    void Push(DrawingContext dc, int slot, double x, double y, double deg = 0, double sx = 1, double sy = 1)
    {
        var m = Matrix.Identity;
        if (sx != 1 || sy != 1) m.Scale(sx, sy);
        if (deg != 0) m.Rotate(deg);
        m.Translate(x, y);
        _tf[slot].Matrix = m;
        dc.PushTransform(_tf[slot]);
    }

    void PushRotate(DrawingContext dc, int slot, double deg, double px, double py)
    {
        var m = Matrix.Identity;
        m.RotateAt(deg, px, py);
        _tf[slot].Matrix = m;
        dc.PushTransform(_tf[slot]);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = Width, h = Height, ground = h - 1;
        // Flip = crouch, spin with a little stretch, then a springy landing squash.
        double lift = 0, spin = 0, sx = 1, sy = 1;
        if (_mode == Mode.Flip)
        {
            if (_flip < 0.2) { double c = Math.Sin(_flip / 0.2 * Math.PI / 2); sy = 1 - 0.22 * c; sx = 1 + 0.12 * c; }
            else
            {
                double u = Math.Min(1, (_flip - 0.2) / 0.7), arc = Math.Sin(u * Math.PI);
                lift = arc * 13; spin = -360 * u * u * (3 - 2 * u); sy = 1 + 0.08 * arc; sx = 1 - 0.05 * arc;
            }
        }
        if (_squash > 0) { double k = _squash * _squash * Math.Cos((1 - _squash) * 9); sy *= 1 - 0.2 * k; sx *= 1 + 0.14 * k; }
        if (_love > 0) lift += Math.Abs(Math.Sin(_love * 7)) * 3 * Math.Min(1, _love);
        // Facing mirrors about the centre and never quite reaches zero, so turning round reads as a quick squish.
        double f = Math.Abs(_face) < 0.2 ? (_face < 0 ? -0.2 : 0.2) : _face;
        var m = Matrix.Identity;
        m.ScaleAt(sx, sy, w / 2, ground);
        if (spin != 0) m.RotateAt(spin, w / 2, h * 0.55);
        m.Translate(0, -lift);
        m.ScaleAt(f, 1, w / 2, 0);
        _tf[0].Matrix = m;
        dc.PushTransform(_tf[0]);
        switch (_kind)
        {
            case "Ghost": DrawGhost(dc, w, h); break;
            case "Fish": DrawFish(dc, w, h); break;
            case "Horse": DrawHorse(dc, w, ground); break;
            default: DrawQuad(dc, w, ground); break;
        }
        dc.Pop();

        foreach (var hrt in _hearts) { dc.PushOpacity(Math.Min(1, hrt.life)); dc.DrawText(HeartText, new Point(hrt.x, hrt.y)); dc.Pop(); }
        bool fish = _kind == "Fish";
        foreach (var z in _zs)
        {
            dc.PushOpacity(Math.Min(1, z.life));
            if (fish) { double r = 1.1 + (2 - z.life) * 0.5; dc.DrawEllipse(null, BubblePen, new Point(z.x, z.y), r, r); }
            else dc.DrawText(ZText, new Point(z.x, z.y));
            dc.Pop();
        }
    }

    /// Cat, dog and bunny: trotting diagonal leg pairs (the bunny hops), sit tilts the body back, sleep curls up.
    void DrawQuad(DrawingContext dc, double w, double ground)
    {
        bool dog = _kind == "Dog", bunny = _kind == "Bunny";
        double rx = dog ? 10.5 : bunny ? 8.6 : 10, ry = dog ? 6.6 : bunny ? 7 : 6.2, leg = bunny ? 3.6 : 6;
        double stride = Stride, breathe = 1 + Math.Sin(_clock * (_lie > 0.5 ? 1.7 : 2.6)) * (0.02 + 0.04 * _lie);
        double hop = bunny ? Math.Max(0, Math.Sin(_gait)) * 5 * stride : Math.Abs(Math.Sin(_gait)) * 0.9 * stride;
        double tilt = -_sit * (bunny ? 10 : 16);
        var c = new Point(w * 0.45, Lerp(ground - leg - ry * 0.55, ground - ry * 0.85, _lie) + _sit * 1.2 - hop);

        // legs: far pair shaded, near pair in front; diagonal pairs share a phase
        for (int i = 0; i < 4; i++)
        {
            bool front = (i & 1) == 1, near = i >= 2;
            double ph = front ^ near ? 0 : Math.PI;
            var hip = Rot(c, (front ? rx * 0.55 : -rx * 0.55) + (near ? 1.2 : -1.2), ry * 0.45, tilt);
            double s = Math.Sin(_gait + ph), up = Math.Max(0, Math.Cos(_gait + ph)), fx, fy;
            if (bunny) { fx = hip.X + (front ? 1.5 : -1.5) * hop / 5; fy = Math.Min(ground, hip.Y + leg + 1.5); }
            else { fx = hip.X + s * 2.6 * stride; fy = ground - up * 2 * stride; }
            fx = Lerp(fx, hip.X + (front ? 0.5 : 3), _sit); fy = Lerp(fy, ground, _sit);
            fx = Lerp(fx, hip.X + (front ? 7 : 2), _lie); fy = Lerp(fy, front ? ground - 1 : hip.Y + 0.5, _lie);
            var foot = new Point(fx, fy);
            dc.DrawLine(_limbEdge, hip, foot);
            dc.DrawLine(near ? _limb : _farLimb, hip, foot);
        }

        if (!bunny)
        {
            var tb = Rot(c, -rx * 0.88, -ry * 0.15, tilt);
            double wag = dog ? Math.Sin(_clock * (_love > 0 ? 24 : 13)) * (_lie > 0.5 ? 4 : 16)
                             : Math.Sin(_clock * 2.2) * 10 + Math.Sin(_gait) * 6 * stride;
            Push(dc, 1, tb.X, tb.Y, tilt + wag - _lie * (dog ? 40 : 75));
            var g = dog ? DogTail : CatTail;
            dc.DrawGeometry(null, _tailEdge, g);
            dc.DrawGeometry(null, _tailPen, g);
            dc.Pop();
        }

        Push(dc, 2, c.X, c.Y, tilt);
        double bry = ry * breathe;
        dc.DrawEllipse(_fill, _edge, new Point(0, 0), rx, bry);
        if (dog) dc.DrawEllipse(_accent, null, new Point(-3, -2.6), 3.2, 2.4);
        else if (!bunny)
        {
            dc.DrawLine(_accentPen, new Point(-4, -bry * 0.92 + 0.4), new Point(-3.4, -bry * 0.92 + 3));
            dc.DrawLine(_accentPen, new Point(0, -bry + 0.4), new Point(0.5, -bry + 3));
            dc.DrawLine(_accentPen, new Point(3.6, -bry * 0.93 + 0.4), new Point(4, -bry * 0.93 + 2.6));
        }
        dc.Pop();
        if (bunny) dc.DrawEllipse(_fill, _edge, Rot(c, -rx * 0.95, -ry * 0.1, tilt), 3.4, 3.4);

        var hc = Rot(c, dog ? 10 : bunny ? 8 : 9.5, dog ? -5 : bunny ? -6 : -5.5, tilt);
        // lying down rests the chin on the ground just ahead of the body
        hc = new Point(hc.X + _lie * 1.5, Lerp(hc.Y, ground - 5.6, _lie) + Math.Sin(_gait * 2) * 0.5 * stride);
        double hAng = _sit * 9 * Math.Sin(_clock * 0.9) + (_love > 0 ? Math.Sin(_love * 12) * 9 : 0) + _lie * 8;
        Push(dc, 3, hc.X, hc.Y, hAng);
        if (!dog)
        {
            double earAng = (bunny ? -_lie * 55 + Math.Sin(_gait) * 6 * stride : 0) + (_twitch > 0 ? Math.Sin(_twitch * 50) * 7 : 0);
            PushRotate(dc, 4, earAng, 0, -4);
            dc.DrawGeometry(_fill, _edge, bunny ? BunnyEars : CatEars);
            dc.DrawGeometry(bunny ? _accent : Pink, null, bunny ? BunnyEarsIn : CatEarsIn);
            dc.Pop();
        }
        dc.DrawEllipse(_fill, _edge, new Point(0, 0), dog ? 7 : bunny ? 6.4 : 6.8, dog ? 6.4 : 6);
        if (dog)
        {
            dc.DrawEllipse(_cream, _edge, new Point(5, 2), 3.4, 2.6);
            dc.DrawEllipse(Ink, null, new Point(7.8, 1.2), 1.3, 1);
            dc.DrawEllipse(White, null, new Point(7.4, 0.8), 0.45, 0.3);
            PushRotate(dc, 4, Math.Sin(_gait * 2) * 12 * stride + (_love > 0 ? Math.Sin(_love * 12) * 10 : 0) + _lie * 10, -3, -4);
            dc.DrawGeometry(_accent, _edge, DogEar);
            dc.Pop();
        }
        else
        {
            dc.DrawEllipse(_cream, null, new Point(2, 2.6), 3.4, 2.4);
            dc.DrawEllipse(Pink, null, new Point(1.8, 1.6), 1.1, 0.75);
            if (!bunny)
            {
                dc.DrawLine(Whisker, new Point(4, 1.6), new Point(9, 0.8));
                dc.DrawLine(Whisker, new Point(4, 2.4), new Point(9, 3));
            }
        }
        double ey = dog ? -1.6 : -1;
        Cheeks(dc, -3.2, 5.8, 2.2, 1.9);
        Eye(dc, -0.9, ey, 1.5);
        Eye(dc, 3.8, ey, 1.5);
        dc.Pop();
    }

    /// Horse: jointed legs that trot, a flowing tail, a maned neck that arches up or drops to graze ("sitting").
    void DrawHorse(DrawingContext dc, double w, double ground)
    {
        const double rx = 12.5, ry = 6.4, leg = 12;
        double stride = Stride, breathe = 1 + Math.Sin(_clock * (_lie > 0.5 ? 1.6 : 2.2)) * (0.02 + 0.03 * _lie);
        var c = new Point(w * 0.42, Lerp(ground - leg - ry * 0.5, ground - ry * 0.9, _lie) - Math.Abs(Math.Sin(_gait)) * 1.4 * stride);

        // far legs, then the tail, then near legs, so the near hind leg sits in front of the tail
        for (int i = 0; i < 4; i++)
        {
            if (i == 2)
            {
                Push(dc, 1, c.X - rx * 0.9, c.Y - ry * 0.55, Math.Sin(_clock * 1.7) * 9 + stride * 30 + Math.Sin(_gait) * 6 * stride + _lie * 45);
                dc.DrawGeometry(_accent, _edge, HorseTail);
                dc.Pop();
            }
            bool front = (i & 1) == 1, near = i >= 2;
            double ph = front ^ near ? 0 : Math.PI;
            var hip = new Point(c.X + (front ? rx * 0.62 : -rx * 0.6) + (near ? 1.4 : -1.4), c.Y + ry * 0.35);
            double s = Math.Sin(_gait + ph), up = Math.Max(0, Math.Cos(_gait + ph));
            double fx = Lerp(hip.X + s * 4.2 * stride, hip.X + (front ? 6 : 4), _lie);
            double fy = Lerp(ground - up * 3.4 * stride, ground - 1, _lie);
            // knees bend forward on the front legs, hocks backward on the hind ones
            double bend = Lerp((front ? 1 : -1) * (1.2 + up * 2.4 * stride), front ? -2.5 : 2.5, _lie);
            var foot = new Point(fx, fy - 1);
            var knee = new Point((hip.X + fx) / 2 + bend, (hip.Y + fy) / 2);
            dc.DrawLine(_thickEdge, hip, knee); dc.DrawLine(_limbEdge, knee, foot);
            dc.DrawLine(near ? _limbThick : _farThick, hip, knee); dc.DrawLine(near ? _limb : _farLimb, knee, foot);
            dc.DrawEllipse(Hoof, null, new Point(fx, fy - 0.8), 1.7, 1.2);
        }

        // neck swings from raised (standing) to down in the grass (grazing); the head munches while it grazes
        var nb = new Point(c.X + rx * 0.6, c.Y - ry * 0.45);
        double munch = Math.Sin(_clock * 7) * 3 * _graze;
        double na = Lerp(Lerp(-58, 50, _graze), -32, _lie) + Math.Sin(_gait * 2) * 4 * stride + (_love > 0 ? Math.Sin(_love * 10) * 6 : 0);
        double nr = na * Math.PI / 180;
        var nt = new Point(nb.X + Math.Cos(nr) * 11, nb.Y + Math.Sin(nr) * 11);
        Push(dc, 5, nb.X, nb.Y, na);
        dc.DrawGeometry(_fill, _edge, HorseNeck);
        dc.DrawGeometry(_accent, null, HorseMane);
        dc.Pop();

        Push(dc, 2, c.X, c.Y, 0, 1, breathe);
        dc.DrawGeometry(_fill, _edge, HorseBody);
        dc.Pop();

        double ha = 22 + _graze * 48 + _lie * 10 + munch, hr = ha * Math.PI / 180;
        Push(dc, 3, nt.X + Math.Cos(hr) * 3, nt.Y + Math.Sin(hr) * 3, ha);
        PushRotate(dc, 4, _twitch > 0 ? Math.Sin(_twitch * 50) * 10 : 0, -2.6, -3);
        dc.DrawGeometry(_shade, _edge, HorseEarFar);
        dc.DrawGeometry(_fill, _edge, HorseEar);
        dc.Pop();
        dc.DrawGeometry(_fill, _edge, HorseHead);
        dc.PushClip(HorseHead);
        dc.DrawEllipse(_shade, null, new Point(6.2, 1), 2.9, 3.4);   // darker muzzle
        dc.Pop();
        dc.DrawGeometry(_cream, null, HorseBlaze);
        dc.DrawEllipse(Hoof, null, new Point(6.3, 0.5), 0.7, 0.55);   // nostril
        dc.DrawGeometry(_accent, null, HorseForelock);
        Cheeks(dc, 1, double.NaN, 1.8, 1.6);
        Eye(dc, -1.5, -0.8, 1.4);
        dc.Pop();
    }

    /// Clownfish swimming in the air above the island: three white bands, black-trimmed fins, a fanning tail, bubbles.
    void DrawFish(DrawingContext dc, double w, double h)
    {
        var c = new Point(w * 0.52, h * 0.5 + Math.Sin(_clock * 2.1) * 1.6 + _lie * 3);
        Push(dc, 2, c.X, c.Y, Math.Sin(_clock * 1.3) * 4 + Math.Sin(_swim) * 2 + (_love > 0 ? Math.Sin(_love * 14) * 8 : 0));
        // the tail swings and foreshortens as it beats
        Push(dc, 4, -9.2, 0, Math.Sin(_swim) * 20, 1 - Math.Abs(Math.Sin(_swim)) * 0.2);
        dc.DrawGeometry(_fill, BandEdge, FishTail);
        dc.Pop();
        dc.DrawGeometry(_fill, BandEdge, FishDorsal);
        dc.DrawGeometry(_fill, BandEdge, FishAnal);
        dc.DrawGeometry(_fill, null, FishBody);
        dc.PushClip(FishBody);
        dc.DrawEllipse(White, BandEdge, new Point(3.4, 0), 1.9, 9);
        dc.DrawEllipse(White, BandEdge, new Point(-3, 0), 2.2, 9);
        dc.DrawEllipse(White, BandEdge, new Point(-9.8, 0), 1.4, 6);
        dc.Pop();
        dc.DrawGeometry(null, _edge, FishBody);
        Push(dc, 5, 1.2, 2.4, Math.Sin(_swim * 1.3) * 25);
        dc.DrawGeometry(_fill, BandEdge, FishFin);
        dc.Pop();
        dc.DrawLine(Thin, new Point(8.4, 2.2), new Point(9.4, 1.6));
        Cheeks(dc, 7.2, double.NaN, 1.8, 1.4);
        if (_lie <= 0.4 && _love <= 0 && _blink <= 0) dc.DrawEllipse(White, BandEdge, new Point(6.3, -1.8), 2.1, 2.1);
        Eye(dc, 6.3, -1.8, 1.5);
        dc.Pop();
    }

    /// Ghost: floats and leans into its drift, rippling hem, waving nubs.
    void DrawGhost(DrawingContext dc, double w, double h)
    {
        double breathe = 1 + Math.Sin(_clock * 2) * 0.03;
        var c = new Point(w / 2, h * 0.5 + Math.Sin(_clock * 2.4) * 2 + _lie * 3);
        Push(dc, 2, c.X, c.Y, Stride * 9 + (_love > 0 ? Math.Sin(_love * 10) * 8 : 0), 2 - breathe, breathe);
        double wave = Math.Sin(_clock * 4) * 1.2;
        dc.DrawEllipse(GhostGlow, null, new Point(0, 0), 17, 16);
        dc.DrawEllipse(_fill, _edge, new Point(-9.4, 2 + wave), 2.2, 3);
        dc.DrawEllipse(_fill, _edge, new Point(9.4, 2 - wave), 2.2, 3);
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            double y0 = 9 + Math.Sin(_clock * 5 + 7.8) * 1.1;
            ctx.BeginFigure(new Point(-9, y0), true, true);
            ctx.LineTo(new Point(-9, -2), true, true);
            ctx.ArcTo(new Point(9, -2), new Size(9, 10), 0, false, SweepDirection.Clockwise, true, true);
            ctx.LineTo(new Point(9, 9), true, true);
            for (int i = 1; i <= 6; i++)
            {
                double x = 9 - i * 3, y = 9 + Math.Sin(_clock * 5 + i * 1.3) * 1.1;
                ctx.QuadraticBezierTo(new Point(x + 1.5, y + 3.2), new Point(x, y), true, true);
            }
        }
        dc.DrawGeometry(_fill, _edge, g);
        Cheeks(dc, -5, 6.2, 0.4, 2);
        Eye(dc, -2.4, -3, 1.8);
        Eye(dc, 3.6, -3, 1.8);
        if (_love > 0) dc.DrawEllipse(Ink, null, new Point(0.6, 1.6), 1.1, 1.4);
        dc.Pop();
    }

    /// Open eyes follow the cursor and blink; asleep = closed arcs, petted = happy ^ ^.
    void Eye(DrawingContext dc, double x, double y, double r)
    {
        if (_lie > 0.4)
        {
            dc.DrawLine(Thin, new Point(x - r, y - 0.2), new Point(x, y + 0.6));
            dc.DrawLine(Thin, new Point(x, y + 0.6), new Point(x + r, y - 0.2));
        }
        else if (_love > 0)
        {
            dc.DrawLine(Thin, new Point(x - r, y + 0.5), new Point(x, y - 0.8));
            dc.DrawLine(Thin, new Point(x, y - 0.8), new Point(x + r, y + 0.5));
        }
        else if (_blink > 0) dc.DrawLine(Thin, new Point(x - r, y), new Point(x + r, y));
        else
        {
            var p = new Point(x + _lookX * 0.5, y + _lookY * 0.4);
            // glossy: warm dark iris, a big catch-light and a small bounce light
            dc.DrawEllipse(EyeFill, null, p, r * 0.85, r);
            dc.DrawEllipse(White, null, new Point(p.X - r * 0.28, p.Y - r * 0.38), r * 0.36, r * 0.36);
            dc.DrawEllipse(White, null, new Point(p.X + r * 0.32, p.Y + r * 0.42), r * 0.15, r * 0.15);
        }
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
            // Parsed geometries are frozen; clone before applying the size-dependent transform.
            var g = Geometry.Parse("M 14,1 L 8,7 L 12,7 L 10,12 L 17,5.5 L 13,5.5 Z").Clone();
            g.Transform = new ScaleTransform(w / 26, h / 13);
            dc.DrawGeometry(Brushes.White, new Pen(Brushes.Black, 0.8), g);
        }
    }
}
