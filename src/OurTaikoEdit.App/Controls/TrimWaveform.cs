using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using OurTaikoEdit.App.Theme;
using OurTaikoEdit.Audio;

namespace OurTaikoEdit.App.Controls;

// A file's whole waveform with the part to convert marked: its two ends are dragged by their
// handles, a click elsewhere moves the position (where hearing it starts), and a time ruler runs
// along the top.
public sealed class TrimWaveform : Control
{
    public static readonly StyledProperty<Waveform?> WaveformProperty = AvaloniaProperty.Register<TrimWaveform, Waveform?>(nameof(Waveform));
    public static readonly StyledProperty<double> DurationProperty = AvaloniaProperty.Register<TrimWaveform, double>(nameof(Duration));
    public static readonly StyledProperty<double> StartProperty = AvaloniaProperty.Register<TrimWaveform, double>(nameof(Start), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<double?> EndProperty = AvaloniaProperty.Register<TrimWaveform, double?>(nameof(End), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<double> PositionProperty = AvaloniaProperty.Register<TrimWaveform, double>(nameof(Position));

    public Waveform? Waveform { get => GetValue(WaveformProperty); set => SetValue(WaveformProperty, value); }
    public double Duration { get => GetValue(DurationProperty); set => SetValue(DurationProperty, value); }
    public double Start { get => GetValue(StartProperty); set => SetValue(StartProperty, value); }
    public double? End { get => GetValue(EndProperty); set => SetValue(EndProperty, value); }
    public double Position { get => GetValue(PositionProperty); set => SetValue(PositionProperty, value); }

    // A click away from the handles: the time clicked.
    public event Action<double>? Seek;

    const double Ruler = 16, Tab = 12, Grab = 8, MinLength = 0.1;
    static readonly IPen Edge = new Pen(Ui.Accent, 2), CursorPen = new Pen(Ui.Text, 1.5), Tick = new Pen(Ui.LineStrong, 1);
    static readonly IBrush Outside = Ui.Solid(Ui.SunkenColor, 0.62), Wave = Ui.Muted, WaveIn = Ui.Ka;
    Waveform? scaledFor;
    float loudest = 1;
    enum Drag { None, Start, End, Seek }
    Drag drag;

    static TrimWaveform()
    {
        AffectsRender<TrimWaveform>(WaveformProperty, DurationProperty, StartProperty, EndProperty, PositionProperty);
        FocusableProperty.OverrideDefaultValue<TrimWaveform>(true);
        ClipToBoundsProperty.OverrideDefaultValue<TrimWaveform>(true);
    }

    double EndOrDuration => End ?? Duration;
    double X(double t) => Duration > 0 ? t / Duration * Bounds.Width : 0;
    double T(double x) => Duration > 0 ? Math.Clamp(x / Math.Max(1, Bounds.Width) * Duration, 0, Duration) : 0;

    public override void Render(DrawingContext ctx)
    {
        var size = Bounds.Size;
        ctx.FillRectangle(Ui.Sunken, new Rect(size));
        if (Duration <= 0) return;
        double top = Ruler, height = size.Height - Ruler, mid = top + height / 2;
        double a = X(Start), b = X(EndOrDuration);

        // The ruler: a tick every so many seconds, about every 70 pixels.
        double step = Nice(Duration * 70 / Math.Max(1, size.Width));
        var label = new Typeface(Ui.Font);
        for (double t = 0; t <= Duration; t += step)
        {
            double x = Math.Round(X(t)) + 0.5;
            ctx.DrawLine(Tick, new Point(x, Ruler - 5), new Point(x, Ruler));
            var text = new FormattedText(Clock(t, step), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, label, 10, Ui.Faint);
            ctx.DrawText(text, new Point(x + 3, 1));
        }
        ctx.DrawLine(Tick, new Point(0, Ruler - 0.5), new Point(size.Width, Ruler - 0.5));

        if (Waveform is { } wave)
        {
            double perPixel = Duration / Math.Max(1, size.Width);
            // Drawn against the file's loudest peak, so a quiet file still shows its shape.
            if (!ReferenceEquals(wave, scaledFor)) { scaledFor = wave; loudest = Math.Max(0.01f, wave.Peaks.DefaultIfEmpty(0).Max()); }
            for (int px = 0; px < (int)size.Width; px++)
            {
                float peak = wave.Peak(px * perPixel, (px + 1) * perPixel) / loudest;
                double h = Math.Max(1, peak * (height / 2 - 4));
                ctx.FillRectangle(px >= a && px <= b ? WaveIn : Wave, new Rect(px, mid - h, 1, h * 2));
            }
        }
        else ctx.DrawLine(Tick, new Point(0, mid), new Point(size.Width, mid));

        // Outside the part: dimmed; its ends: lines with tabs to drag.
        ctx.FillRectangle(Outside, new Rect(0, top, Math.Max(0, a), height));
        ctx.FillRectangle(Outside, new Rect(b, top, Math.Max(0, size.Width - b), height));
        foreach (double x in new[] { a, b })
        {
            ctx.DrawLine(Edge, new Point(x, top), new Point(x, size.Height));
            ctx.FillRectangle(Ui.Accent, new Rect(x - Tab / 2, top, Tab, Tab));
        }
        double p = X(Position);
        ctx.DrawLine(CursorPen, new Point(p, top), new Point(p, size.Height));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Duration <= 0 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        double x = e.GetPosition(this).X;
        double da = Math.Abs(x - X(Start)), db = Math.Abs(x - X(EndOrDuration));
        drag = Math.Min(da, db) <= Grab ? (da <= db ? Drag.Start : Drag.End) : Drag.Seek;
        Move(x);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        double x = e.GetPosition(this).X;
        if (drag != Drag.None) { Move(x); return; }
        bool near = Duration > 0 && (Math.Abs(x - X(Start)) <= Grab || Math.Abs(x - X(EndOrDuration)) <= Grab);
        base.Cursor = near ? new Cursor(StandardCursorType.SizeWestEast) : Avalonia.Input.Cursor.Default;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        drag = Drag.None;
        e.Pointer.Capture(null);
    }

    void Move(double x)
    {
        double t = T(x);
        switch (drag)
        {
            case Drag.Start: Start = Math.Round(Math.Min(t, EndOrDuration - MinLength), 3); break;
            // Dragged to the end: no end, the file's own.
            case Drag.End:
                double end = Math.Round(Math.Max(t, Start + MinLength), 3);
                End = end >= Duration - 0.0005 ? null : end;
                break;
            case Drag.Seek: Seek?.Invoke(t); break;
        }
    }

    // 1, 2 or 5 times a power of ten, at least as long as `raw`.
    static double Nice(double raw)
    {
        double[] steps = { 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800, 3600 };
        return steps.FirstOrDefault(s => s >= raw, 3600);
    }

    static string Clock(double t, double step)
    {
        int m = (int)(t / 60);
        double s = t - m * 60;
        return step < 1 ? $"{m}:{s:00.0}" : $"{m}:{(int)Math.Round(s):00}";
    }
}
