using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using CharTaiko.App.ViewModels;
using CharTaiko.Core.Languages;
using OurTaiko;

namespace CharTaiko.App.Controls;

// The timing timeline: time runs left to right; one row of notes, or one per route when the
// chart branches; a ruler (click or drag to seek) and a row of tempo and go-go marks. The grid
// splits each measure by the chosen division. With a note tool, a click places a note on the
// grid (rolls and balloons: drag to the end); a right click removes the note under the pointer.
// Wheel scrolls, Ctrl/⌘ + wheel zooms.
public sealed class TimelineView : Control
{
    const double HeaderWidth = 78, RulerHeight = 24, EventsHeight = 24;

    EditorViewModel? editor;
    double viewStart = -0.5, pixelsPerSecond = 180;
    Point? hover;
    GridPosition? dragStart;
    bool seeking;
    readonly Typeface typeface = new(FontFamily.Default);
    static readonly IBrush HeaderBrush = new SolidColorBrush(Color.Parse("#1D1F23"));
    static readonly IBrush[] RowBrushes = { new SolidColorBrush(Color.Parse("#202227")), new SolidColorBrush(Color.Parse("#23252B")) };
    static readonly IBrush[] RouteTints =
    {
        new SolidColorBrush(Color.FromArgb(26, 180, 180, 180)),
        new SolidColorBrush(Color.FromArgb(30, 67, 185, 223)),
        new SolidColorBrush(Color.FromArgb(30, 255, 79, 154)),
    };
    static readonly IPen MeasurePen = new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), 1.2);
    static readonly IPen BeatPen = new Pen(new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)), 1);
    static readonly IPen StepPen = new Pen(new SolidColorBrush(Color.FromArgb(32, 255, 255, 255)), 1);
    static readonly IPen RowLine = new Pen(new SolidColorBrush(Color.Parse("#2E3036")), 1);
    static readonly IBrush GogoBrush = new SolidColorBrush(Color.FromArgb(110, 255, 120, 40));
    static readonly IPen PlayheadPen = new Pen(ChartPalette.Playhead, 1.5);

    public TimelineView()
    {
        ClipToBounds = true;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (editor != null) editor.PropertyChanged -= Changed;
        editor = DataContext as EditorViewModel;
        if (editor != null) editor.PropertyChanged += Changed;
        InvalidateVisual();
    }

    void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorViewModel.Playhead) && editor != null) Follow(editor.Playhead, editor.IsPlaying);
        if (e.PropertyName is nameof(EditorViewModel.Playhead) or nameof(EditorViewModel.Analysis) or nameof(EditorViewModel.Route)
            or nameof(EditorViewModel.Division) or nameof(EditorViewModel.Tool))
            InvalidateVisual();
    }

    // Keeps the playhead in view: paging while playing, centring when it jumps away.
    void Follow(double time, bool playing)
    {
        double width = Math.Max(1, Bounds.Width - HeaderWidth), seconds = width / pixelsPerSecond;
        double x = (time - viewStart) / seconds;
        if (playing && x > 0.85) viewStart = time - seconds * 0.1;
        else if (x < 0 || x > 1) viewStart = time - seconds * 0.3;
    }

    // The rows: one per route when the chart branches.
    IReadOnlyList<BranchRoute> Rows => editor?.Analysis.HasBranches == true
        ? new[] { BranchRoute.Normal, BranchRoute.Expert, BranchRoute.Master }
        : new[] { BranchRoute.Normal };

    double RowHeight => Math.Max(28, (Bounds.Height - RulerHeight - EventsHeight) / Rows.Count);
    double RowTop(int row) => RulerHeight + row * RowHeight;
    double XOf(double time) => HeaderWidth + (time - viewStart) * pixelsPerSecond;
    double TimeOf(double x) => viewStart + (x - HeaderWidth) / pixelsPerSecond;

    int RowAt(double y)
    {
        if (y < RulerHeight) return -1;
        int row = (int)((y - RulerHeight) / RowHeight);
        return row < Rows.Count ? row : -2;
    }

    int Steps(ChartMeasure m) => Math.Max(1, (int)Math.Round((editor?.Division ?? 16) * m.Length));

    // The grid position nearest an x on a row's route.
    GridPosition? Snap(double x, BranchRoute route)
    {
        if (editor == null) return null;
        var measures = editor.Analysis.Measures.Where(m => m.IsOn(route)).ToList();
        if (measures.Count == 0) return null;
        double t = TimeOf(x);
        int i = measures.FindLastIndex(m => m.Time <= t);
        if (i < 0) i = 0;
        var measure = measures[i];
        int steps = Steps(measure);
        int step = (int)Math.Round(measure.FractionAt(t) * steps);
        if (step >= steps)
        {
            if (i + 1 < measures.Count && measures[i + 1].Time <= measure.EndTime + 1e-6) return new GridPosition(measures[i + 1], 0, Steps(measures[i + 1]), route);
            step = steps - 1;
        }
        return new GridPosition(measure, step, steps, route);
    }

    public override void Render(DrawingContext ctx)
    {
        var b = Bounds;
        ctx.FillRectangle(ChartPalette.Background, new Rect(b.Size));
        if (editor == null) return;
        var analysis = editor.Analysis;
        var chart = analysis.Chart;
        double t0 = TimeOf(HeaderWidth), t1 = TimeOf(b.Width);
        var rows = Rows;

        for (int row = 0; row < rows.Count; row++)
        {
            var route = rows[row];
            double top = RowTop(row), h = RowHeight, cy = top + h / 2;
            ctx.FillRectangle(RowBrushes[row % 2], new Rect(HeaderWidth, top, b.Width - HeaderWidth, h));
            foreach (var m in analysis.Measures)
            {
                if (!m.IsOn(route) || m.EndTime < t0 || m.Time > t1) continue;
                if (m.BranchId >= 0)
                    ctx.FillRectangle(RouteTints[(int)m.Route], new Rect(XOf(m.Time), top, Math.Max(0, XOf(m.EndTime) - XOf(m.Time)), h));
                int steps = Steps(m);
                double spacing = (XOf(m.EndTime) - XOf(m.Time)) / steps;
                int perBeat = Math.Max(1, editor.Division / 4);
                for (int k = 0; k < steps; k++)
                {
                    bool isBeat = editor.Division % 4 == 0 && k % perBeat == 0;
                    if (k > 0 && !isBeat && spacing < 5) continue;
                    double x = XOf(m.TimeAt((double)k / steps));
                    if (x < HeaderWidth) continue;
                    ctx.DrawLine(k == 0 ? MeasurePen : isBeat ? BeatPen : StepPen, new Point(x, top), new Point(x, top + h));
                }
            }
            ctx.DrawLine(RowLine, new Point(0, top + h), new Point(b.Width, top + h));
            if (chart == null) continue;
            double r = Math.Min(h * 0.28, 11);
            for (int i = chart.Notes.Count - 1; i >= 0; i--)
            {
                var n = chart.Notes[i];
                if (n.BranchId >= 0 && n.Route != route) continue;
                if (n.EndTime < t0 || n.Time > t1) continue;
                DrawNote(ctx, n.Kind, XOf(n.Time), XOf(n.EndTime), cy, r, n.IsLong, 1);
            }
        }

        DrawGhost(ctx);
        DrawRuler(ctx, analysis, t0, t1);
        DrawEvents(ctx, analysis, t0, t1);

        // Row headers over everything that scrolled under them.
        ctx.FillRectangle(HeaderBrush, new Rect(0, 0, HeaderWidth, b.Height));
        for (int row = 0; row < rows.Count; row++)
            Label(ctx, rows.Count == 1 ? "Notes" : rows[row].ToString(), new Point(10, RowTop(row) + RowHeight / 2 - 8), 12, ChartPalette.Text);
        Label(ctx, "Tempo", new Point(10, b.Height - EventsHeight + 4), 12, ChartPalette.Muted);
        Label(ctx, $"1/{editor.Division}", new Point(10, 5), 11, ChartPalette.Muted);

        double px = XOf(editor.Playhead);
        if (px >= HeaderWidth) ctx.DrawLine(PlayheadPen, new Point(px, 0), new Point(px, b.Height));
    }

    static void DrawNote(DrawingContext ctx, NoteKind kind, double x, double tailX, double cy, double r, bool isLong, double opacity)
    {
        double radius = ChartPalette.IsBig(kind) ? r * 1.3 : r;
        if (isLong && tailX > x)
        {
            using var _ = ctx.PushOpacity(opacity);
            var fill = ChartPalette.Fill(kind);
            ctx.DrawRectangle(fill, ChartPalette.NoteOutline, new Rect(x, cy - radius * 0.7, tailX - x, radius * 1.4), radius * 0.7, radius * 0.7);
        }
        ChartPalette.DrawHead(ctx, kind, new Point(x, cy), radius, opacity);
    }

    void DrawGhost(DrawingContext ctx)
    {
        if (editor?.Tool.Kind is not { } kind || hover is not { } p || seeking) return;
        int row = RowAt(p.Y);
        if (row < 0) return;
        var route = Rows[row];
        double cy = RowTop(row) + RowHeight / 2, r = Math.Min(RowHeight * 0.28, 11);
        if (dragStart is { } start)
        {
            var end = Snap(p.X, start.Route);
            double endX = end is { } e ? XOf(e.Time) : XOf(start.Time);
            cy = RowTop(Rows.ToList().IndexOf(start.Route)) + RowHeight / 2;
            DrawNote(ctx, kind, XOf(start.Time), Math.Max(endX, XOf(start.Time)), cy, r, true, 0.55);
            return;
        }
        if (Snap(p.X, route) is { } at) DrawNote(ctx, kind, XOf(at.Time), XOf(at.Time), cy, r, false, 0.45);
    }

    void DrawRuler(DrawingContext ctx, ChartAnalysis analysis, double t0, double t1)
    {
        var b = Bounds;
        ctx.FillRectangle(HeaderBrush, new Rect(0, 0, b.Width, RulerHeight));
        // Seconds, thinned out when zoomed out.
        double every = pixelsPerSecond >= 60 ? 1 : pixelsPerSecond >= 15 ? 5 : 15;
        for (double s = Math.Ceiling(t0 / every) * every; s <= t1; s += every)
        {
            double x = XOf(s);
            ctx.DrawLine(StepPen, new Point(x, RulerHeight - 6), new Point(x, RulerHeight));
            Label(ctx, $"{(int)Math.Floor(s / 60)}:{Math.Abs(s % 60):00}", new Point(x + 2, 12), 9, ChartPalette.Muted);
        }
        double lastX = double.NegativeInfinity;
        foreach (var m in analysis.Measures)
        {
            if (m.BranchId >= 0 && m.Route != (editor?.Route ?? BranchRoute.Normal)) continue;
            if (m.Time < t0 || m.Time > t1) continue;
            double x = XOf(m.Time);
            if (x - lastX < 22) continue;
            lastX = x;
            Label(ctx, (m.Index + 1).ToString(CultureInfo.InvariantCulture), new Point(x + 3, 0), 11, ChartPalette.Text);
        }
        ctx.DrawLine(RowLine, new Point(0, RulerHeight), new Point(b.Width, RulerHeight));
    }

    void DrawEvents(DrawingContext ctx, ChartAnalysis analysis, double t0, double t1)
    {
        var b = Bounds;
        double top = b.Height - EventsHeight;
        ctx.FillRectangle(HeaderBrush, new Rect(0, top, b.Width, EventsHeight));
        if (analysis.Chart is not { } chart || editor == null) return;
        var route = editor.Route;
        double? gogoFrom = null;
        foreach (var g in chart.Gogos.Where(g => LaneView.Shown(chart, g.BranchId, g.Route, route)))
        {
            if (g.On) gogoFrom ??= g.Time;
            else if (gogoFrom is { } from) { Gogo(from, g.Time); gogoFrom = null; }
        }
        if (gogoFrom is { } open) Gogo(open, chart.Duration);

        double? last = null;
        double lastX = double.NegativeInfinity;
        foreach (var tempo in chart.Tempos.Where(t => LaneView.Shown(chart, t.BranchId, t.Route, route)).Prepend(new ChartTempo { Time = 0, Bpm = chart.Bpm }))
        {
            if (last == tempo.Bpm) continue;
            last = tempo.Bpm;
            double x = XOf(tempo.Time);
            if (tempo.Time > t1 || x - lastX < 40) continue;
            lastX = x;
            if (x >= HeaderWidth) Label(ctx, tempo.Bpm.ToString("0.###", CultureInfo.InvariantCulture), new Point(x + 2, top + 5), 11, ChartPalette.Roll);
        }

        void Gogo(double from, double to)
        {
            if (to < t0 || from > t1) return;
            ctx.FillRectangle(GogoBrush, new Rect(XOf(from), top + EventsHeight - 6, Math.Max(1, XOf(to) - XOf(from)), 6));
        }
    }

    void Label(DrawingContext ctx, string text, Point at, double size, IBrush brush)
        => ctx.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush), at);

    // The note drawn under a point of a row (a roll anywhere along its body).
    ChartNote? NoteAt(Point p, BranchRoute route)
    {
        var chart = editor?.Analysis.Chart;
        if (chart == null) return null;
        double r = Math.Min(RowHeight * 0.28, 11) * 1.3;
        ChartNote? best = null;
        double bestDistance = double.MaxValue;
        foreach (var n in chart.Notes)
        {
            if (n.BranchId >= 0 && n.Route != route) continue;
            double x = XOf(n.Time), tail = n.IsLong ? XOf(n.EndTime) : x;
            double d = p.X < x ? x - p.X : p.X > tail ? p.X - tail : 0;
            if (d <= r && d < bestDistance) { best = n; bestDistance = d; }
        }
        return best;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (editor == null) return;
        var p = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;
        int row = RowAt(p.Y);
        if (p.X < HeaderWidth) return;
        if (row == -1)
        {
            seeking = true;
            editor.Seek(TimeOf(p.X));
            e.Pointer.Capture(this);
            return;
        }
        if (row < 0) return;
        var route = Rows[row];
        if (props.IsRightButtonPressed)
        {
            if (NoteAt(p, route) is { } note) editor.RemoveNote(note);
            return;
        }
        if (!props.IsLeftButtonPressed) return;
        if (editor.Tool.Kind is not { } kind)
        {
            if (NoteAt(p, route) is { } note) { editor.Reveal(note); editor.Seek(note.Time); }
            else editor.Seek(TimeOf(p.X));
            return;
        }
        if (Snap(p.X, route) is not { } at) return;
        if (kind is NoteKind.Roll or NoteKind.BigRoll or NoteKind.Balloon or NoteKind.Kusudama)
        {
            dragStart = at;
            e.Pointer.Capture(this);
        }
        else editor.PlaceNote(at, null);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        hover = e.GetPosition(this);
        if (seeking && editor != null) editor.Seek(TimeOf(Math.Max(HeaderWidth, hover.Value.X)));
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        seeking = false;
        if (dragStart is { } start && editor != null)
        {
            dragStart = null;
            var p = e.GetPosition(this);
            var end = Snap(p.X, start.Route);
            // A click without a drag gives the shortest roll: one grid step.
            if (end is not { } stop || stop.Time <= start.Time)
                end = start.Step + 1 < start.Steps ? start with { Step = start.Step + 1 } : Snap(XOf(start.Measure.EndTime) + 0.5, start.Route);
            editor.PlaceNote(start, end);
        }
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        hover = null;
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var p = e.GetPosition(this);
        bool zoom = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        if (zoom)
        {
            double anchor = TimeOf(p.X);
            pixelsPerSecond = Math.Clamp(pixelsPerSecond * Math.Pow(1.15, e.Delta.Y), 8, 2400);
            viewStart = anchor - (p.X - HeaderWidth) / pixelsPerSecond;
        }
        else viewStart -= (e.Delta.Y + e.Delta.X) * 60 / pixelsPerSecond;
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (editor == null) return;
        if (e.Key == Key.Space) { editor.TogglePlayCommand.Execute(null); e.Handled = true; return; }
        string key = e.Key switch
        {
            >= Key.D0 and <= Key.D9 => ((int)(e.Key - Key.D0)).ToString(CultureInfo.InvariantCulture),
            >= Key.NumPad0 and <= Key.NumPad9 => ((int)(e.Key - Key.NumPad0)).ToString(CultureInfo.InvariantCulture),
            _ => e.Key.ToString().ToUpperInvariant(),
        };
        if (editor.Tools.FirstOrDefault(t => t.Key == key) is { } tool) { editor.Tool = tool; e.Handled = true; }
    }
}
