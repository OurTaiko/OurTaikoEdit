using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using OurTaikoEdit.App.ViewModels;
using OurTaikoEdit.Core.Localization;
using OurTaikoEdit.Core.Languages;
using OurTaiko;

namespace OurTaikoEdit.App.Controls;

// The timing timeline: time runs left to right; one row of notes, or one per route when the
// chart branches; a ruler (click or drag to seek) and a row of tempo and go-go marks, with where
// each branch starts and where its condition is judged. The grid
// splits each measure by the chosen division. With a note tool, a click places a note on the
// grid (rolls and balloons: drag to the end); a right click removes the note under the pointer.
// Wheel scrolls, Ctrl/⌘ + wheel zooms.
public sealed class TimelineView : Control
{
    const double HeaderWidth = 78, RulerHeight = 24, EventsHeight = 24;

    EditorViewModel? editor;
    double viewStart = -0.5, pixelsPerSecond = 180;
    Point? hover;
    // What a press started: seeking on the ruler, drawing a roll, a selection box, or moving the
    // selection (by whole grid steps, from where the press snapped).
    enum Drag { None, Seek, Roll, Box, Move, Resize, Command }
    Drag drag;
    // The roll whose end is being dragged, the command being moved.
    ChartNote? resizing;
    ChartCommand? movingCommand;
    GridPosition? dragStart;
    Point pressPoint;
    BranchRoute dragRow;
    double moveBeats;
    bool seeking => drag == Drag.Seek;
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
    static readonly IBrush BranchBrush = new SolidColorBrush(Color.Parse("#C58AF9"));
    static readonly IPen BranchPen = new Pen(new SolidColorBrush(Color.FromArgb(210, 197, 138, 249)), 1.5);
    static readonly IPen JudgePen = new Pen(new SolidColorBrush(Color.FromArgb(210, 197, 138, 249)), 1.5) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) };

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
            or nameof(EditorViewModel.Division) or nameof(EditorViewModel.Tool) or nameof(EditorViewModel.Waveform)
            or nameof(EditorViewModel.SelectedNotes))
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
    public double XOf(double time) => HeaderWidth + (time - viewStart) * pixelsPerSecond;
    double TimeOf(double x) => viewStart + (x - HeaderWidth) / pixelsPerSecond;

    int RowAt(double y)
    {
        if (y < RulerHeight) return -1;
        int row = (int)((y - RulerHeight) / RowHeight);
        return row < Rows.Count ? row : -2;
    }

    int Steps(MeasureView m) => Math.Max(1, (int)Math.Round((editor?.Division ?? 16) * m.Length));

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
            ctx.FillRectangle(RowBrushes[row % 2], new Rect(HeaderWidth, RowTop(row), b.Width - HeaderWidth, RowHeight));
        DrawWaveform(ctx);

        for (int row = 0; row < rows.Count; row++)
        {
            var route = rows[row];
            double top = RowTop(row), h = RowHeight;
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
        }
        DrawBranchLines(ctx, analysis, t0, t1);
        for (int row = 0; row < rows.Count && chart != null; row++)
        {
            var route = rows[row];
            double h = RowHeight, cy = RowTop(row) + h / 2;
            double r = Math.Min(h * 0.28, 11);
            for (int i = chart.Notes.Count - 1; i >= 0; i--)
            {
                var n = chart.Notes[i];
                if (n.BranchId >= 0 && n.Route != route) continue;
                if (n.EndTime < t0 || n.Time > t1) continue;
                bool picked = editor.SelectedNotes.Contains(n);
                DrawNote(ctx, n.Kind, XOf(n.Time), XOf(n.EndTime), cy, r, n.IsLong, drag == Drag.Move && picked && moveBeats != 0 ? 0.35 : 1, picked);
            }
        }

        DrawBranchFlags(ctx, analysis, t0, t1);
        DrawGhost(ctx);
        DrawRuler(ctx, analysis, t0, t1);
        DrawEvents(ctx, analysis, t0, t1);

        // Row headers over everything that scrolled under them.
        ctx.FillRectangle(HeaderBrush, new Rect(0, 0, HeaderWidth, b.Height));
        for (int row = 0; row < rows.Count; row++)
            Label(ctx, rows.Count == 1 ? Loc.T("Notes") : Localization.Names.Route(rows[row]), new Point(10, RowTop(row) + RowHeight / 2 - 8), 12, ChartPalette.Text);
        Label(ctx, Loc.T("Commands"), new Point(10, b.Height - EventsHeight + 4), 12, ChartPalette.Muted);
        Label(ctx, $"1/{editor.Division}", new Point(10, 5), 11, ChartPalette.Muted);

        double px = XOf(editor.Playhead);
        if (px >= HeaderWidth) ctx.DrawLine(PlayheadPen, new Point(px, 0), new Point(px, b.Height));
    }

    // Where each branch starts, a line down the rows, and where its condition is judged, a dashed
    // one (when that is earlier). Under the notes.
    void DrawBranchLines(DrawingContext ctx, ChartAnalysis analysis, double t0, double t1)
    {
        double top = RulerHeight, bottom = RowTop(Rows.Count);
        foreach (var branch in analysis.Branches)
        {
            if (branch.DecisionTime < branch.Time - 1e-6) Line(branch.DecisionTime, JudgePen);
            Line(branch.Time, BranchPen);
        }

        void Line(double time, IPen pen)
        {
            double x = XOf(time);
            if (time < t0 || time > t1 || x < HeaderWidth) return;
            ctx.DrawLine(pen, new Point(x, top), new Point(x, bottom));
        }
    }

    static readonly IBrush FlagText = new SolidColorBrush(Color.Parse("#1A1B1E"));
    static readonly IBrush JudgeFlagFill = new SolidColorBrush(Color.FromArgb(220, 26, 27, 30));
    const double FlagHeight = 16;
    // The flags as drawn, for the pointer: a branch's start, or where it is judged.
    readonly List<(Rect Rect, ChartBranchMark Branch, bool Judge)> flags = new();

    // A flag at the top of each of those lines: the branch's condition as written at its start,
    // and "judged" a level lower, so the two never cover each other. A flag that would cover the
    // one before it on its level is left out; its line stays.
    void DrawBranchFlags(DrawingContext ctx, ChartAnalysis analysis, double t0, double t1)
    {
        flags.Clear();
        double top = RulerHeight + 3;
        double startRight = double.NegativeInfinity, judgeRight = double.NegativeInfinity;
        foreach (var branch in analysis.Branches)
        {
            Flag(branch, judge: false, Loc.F("BranchMark", branch.Start.Value).Trim(), top, ref startRight);
            Flag(branch, judge: true, Loc.T("JudgeMark"), top + FlagHeight + 2, ref judgeRight);
        }

        void Flag(ChartBranchMark branch, bool judge, string label, double y, ref double right)
        {
            double time = judge ? branch.DecisionTime : branch.Time, x = XOf(time);
            if (time < t0 || time > t1 || x < HeaderWidth || x < right + 4) return;
            var text = Text(label, 11, judge ? BranchBrush : FlagText);
            var rect = new Rect(x, y, text.Width + 10, FlagHeight);
            if (judge) ctx.DrawRectangle(JudgeFlagFill, new Pen(BranchBrush, 1), rect, 3, 3);
            else ctx.DrawRectangle(BranchBrush, null, rect, 3, 3);
            ctx.DrawText(text, new Point(x + 5, y + (FlagHeight - text.Height) / 2));
            flags.Add((rect, branch, judge));
            right = rect.Right;
        }
    }

    ChartBranchMark? FlagAt(Point p)
    {
        foreach (var flag in flags)
            if (flag.Rect.Contains(p)) return flag.Branch;
        return null;
    }

    static readonly IPen WavePen = new Pen(new SolidColorBrush(Color.FromArgb(70, 92, 200, 232)), 2);

    // The song's loudness behind the rows, mirrored about their middle, two pixels a line.
    void DrawWaveform(DrawingContext ctx)
    {
        if (editor?.Waveform is not { } wave) return;
        double top = RulerHeight, height = RowHeight * Rows.Count, mid = top + height / 2;
        double step = 2 / pixelsPerSecond;
        for (double x = HeaderWidth; x < Bounds.Width; x += 2)
        {
            double t = TimeOf(x);
            if (t < 0) continue;
            double peak = wave.Peak(t, t + step) * height * 0.45;
            if (peak < 0.5) continue;
            ctx.DrawLine(WavePen, new Point(x, mid - peak), new Point(x, mid + peak));
        }
    }

    static readonly IPen SelectedPen = new Pen(new SolidColorBrush(Color.Parse("#FFE066")), 2.5);
    static readonly IBrush BoxFill = new SolidColorBrush(Color.FromArgb(40, 255, 224, 102));
    static readonly IPen BoxPen = new Pen(new SolidColorBrush(Color.FromArgb(180, 255, 224, 102)), 1);

    static void DrawNote(DrawingContext ctx, NoteKind kind, double x, double tailX, double cy, double r, bool isLong, double opacity, bool selected = false)
    {
        double radius = ChartPalette.IsBig(kind) ? r * 1.3 : r;
        if (isLong && tailX > x)
        {
            using var _ = ctx.PushOpacity(opacity);
            var fill = ChartPalette.Fill(kind);
            var body = new Rect(x, cy - radius * 0.7, tailX - x, radius * 1.4);
            ctx.DrawRectangle(fill, selected ? SelectedPen : ChartPalette.NoteOutline, body, radius * 0.7, radius * 0.7);
        }
        ChartPalette.DrawHead(ctx, kind, new Point(x, cy), radius, opacity);
        if (selected) ctx.DrawEllipse(null, SelectedPen, new Point(x, cy), radius + 3, radius + 3);
    }

    double RowCenter(BranchRoute route)
    {
        int row = Rows.ToList().IndexOf(route);
        return RowTop(Math.Max(0, row)) + RowHeight / 2;
    }

    // What the pointer would do: the note to place, the roll being drawn, the selection being
    // moved, the box being drawn.
    void DrawGhost(DrawingContext ctx)
    {
        if (editor == null || hover is not { } p) return;
        double r = Math.Min(RowHeight * 0.28, 11);
        switch (drag)
        {
            case Drag.Box:
                ctx.DrawRectangle(BoxFill, BoxPen, new Rect(pressPoint, p));
                return;
            case Drag.Move when moveBeats != 0:
                foreach (var n in editor.SelectedNotes)
                {
                    var route = n.BranchId >= 0 ? n.Route : dragRow;
                    var grid = new MeasureGrid(editor.Analysis, route, editor.Division);
                    if (grid.BeatOf(n) is not { } beat || grid.At(beat + moveBeats) is not { } at) continue;
                    double tail = n.IsLong && grid.BeatOf(n, tail: true) is { } tb && grid.At(tb + moveBeats) is { } te ? XOf(te.Time) : XOf(at.Time);
                    foreach (var row in Rows.Where(row => n.BranchId < 0 || row == n.Route))
                        DrawNote(ctx, n.Kind, XOf(at.Time), tail, RowCenter(row), r, n.IsLong, 0.6, true);
                }
                return;
            case Drag.Resize when resizing is { } roll:
                if (Snap(p.X, dragRow) is { } newEnd && newEnd.Time > roll.Time)
                    foreach (var row in Rows.Where(row => roll.BranchId < 0 || row == roll.Route))
                        DrawNote(ctx, roll.Kind, XOf(roll.Time), XOf(newEnd.Time), RowCenter(row), r, true, 0.6, true);
                return;
            case Drag.Command when movingCommand is { } moving:
                if (CommandTarget(moving, p.X) is { } target)
                {
                    double x = XOf(target.Time), top = Bounds.Height - EventsHeight;
                    ctx.DrawLine(new Pen(CommandBrush(moving.Name), 2), new Point(x, RulerHeight), new Point(x, Bounds.Height));
                    Label(ctx, CommandLabel(moving), new Point(x + 3, top + 5), 11, CommandBrush(moving.Name));
                }
                return;
            case Drag.Roll when dragStart is { } start && editor.Tool.Kind is { } rollKind:
                var end = Snap(p.X, start.Route);
                double endX = end is { } e ? XOf(e.Time) : XOf(start.Time);
                DrawNote(ctx, rollKind, XOf(start.Time), Math.Max(endX, XOf(start.Time)), RowCenter(start.Route), r, true, 0.55);
                return;
            case Drag.None when editor.Tool.Kind is { } kind:
                int rowAt = RowAt(p.Y);
                if (rowAt < 0 || FlagAt(p) != null) return;
                if (Snap(p.X, Rows[rowAt]) is { } place) DrawNote(ctx, kind, XOf(place.Time), XOf(place.Time), RowTop(rowAt) + RowHeight / 2, r, false, 0.45);
                return;
        }
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

    static readonly IBrush GogoText = new SolidColorBrush(Color.Parse("#FF8A3D"));

    static IBrush CommandBrush(string name) => name switch
    {
        "#BPMCHANGE" => ChartPalette.Roll,
        "#SCROLL" => ChartPalette.Ka,
        "#DELAY" => ChartPalette.Kadon,
        "#GOGOSTART" or "#GOGOEND" => GogoText,
        _ => ChartPalette.Muted,
    };

    // A command as its row shows it: values for tempo, speed, signature and delay; short marks
    // for the rest.
    static string CommandLabel(ChartCommand c) => c.Name switch
    {
        "#BPMCHANGE" => c.Value,
        "#SCROLL" => "×" + c.Value,
        "#MEASURE" => c.Value,
        "#DELAY" => "+" + c.Value + "s",
        "#GOGOSTART" => "GO▸",
        "#GOGOEND" => "◂GO",
        "#BARLINEOFF" => "|off",
        "#BARLINEON" => "|on",
        _ => c.Value.Length > 0 ? c.Name.ToLowerInvariant() + " " + c.Value : c.Name.ToLowerInvariant(),
    };

    // The commands row: the go-go time as a band, the chart's starting BPM, and every command as a
    // tick with its label (labels that would overlap are left out; the tick stays).
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

        double right = double.NegativeInfinity;
        if (analysis.Measures.FirstOrDefault() is { } first && first.Time >= t0 && first.Time <= t1)
        {
            var text = Text(chart.Bpm.ToString("0.###", CultureInfo.InvariantCulture), 11, ChartPalette.Muted);
            ctx.DrawText(text, new Point(XOf(first.Time) + 3, top + 5));
            right = XOf(first.Time) + 3 + text.Width;
        }
        foreach (var c in analysis.Commands)
        {
            if (!c.IsOn(route) || c.Time < t0 || c.Time > t1) continue;
            double x = XOf(c.Time);
            if (x < HeaderWidth) continue;
            var brush = CommandBrush(c.Name);
            ctx.DrawLine(new Pen(brush, 1.5), new Point(x, top + 1), new Point(x, top + 7));
            var text = Text(CommandLabel(c), 11, brush);
            if (x + 3 < right + 4) continue;
            ctx.DrawText(text, new Point(x + 3, top + 5));
            right = x + 3 + text.Width;
        }

        void Gogo(double from, double to)
        {
            if (to < t0 || from > t1) return;
            ctx.FillRectangle(GogoBrush, new Rect(XOf(from), top + EventsHeight - 6, Math.Max(1, XOf(to) - XOf(from)), 6));
        }
    }

    FormattedText Text(string text, double size, IBrush brush)
        => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush);

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

    bool InEvents(double y) => y >= Bounds.Height - EventsHeight;

    // The grid beat a point snaps to on a route.
    double? BeatAt(double x, BranchRoute route)
        => editor == null || Snap(x, route) is not { } at ? null : new MeasureGrid(editor.Analysis, route, editor.Division).Beat(at);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (editor == null) return;
        var p = e.GetPosition(this);
        var point = e.GetCurrentPoint(this).Properties;
        bool additive = (e.KeyModifiers & (KeyModifiers.Shift | KeyModifiers.Meta | KeyModifiers.Control)) != 0;
        if (p.X < HeaderWidth) return;
        pressPoint = p;
        if (RowAt(p.Y) == -1)
        {
            drag = Drag.Seek;
            editor.Seek(TimeOf(p.X));
            e.Pointer.Capture(this);
            return;
        }
        if (InEvents(p.Y))
        {
            var near = CommandNear(p.X);
            if (point.IsRightButtonPressed) EventMenu(p);
            else if (e.ClickCount == 2 && near is { Value.Length: > 0 }) _ = editor.EditCommand(near);
            else if (point.IsLeftButtonPressed && near != null)
            {
                drag = Drag.Command;
                movingCommand = near;
                e.Pointer.Capture(this);
            }
            return;
        }
        if (FlagAt(p) is { } branch)
        {
            // A branch's flag: its start is shown in the code; twice, its condition is changed.
            if (point.IsRightButtonPressed) BranchMenu(branch);
            else if (e.ClickCount == 2) _ = editor.EditCommand(branch.Start);
            else if (point.IsLeftButtonPressed) editor.RevealCommand(branch.Start);
            return;
        }
        int row = RowAt(p.Y);
        if (row < 0) return;
        var route = Rows[row];
        dragRow = route;
        var note = NoteAt(p, route);
        if (point.IsRightButtonPressed)
        {
            if (editor.Tool.Kind != null) { if (note != null) editor.RemoveNote(note); }
            else NoteMenu(p, route, note);
            return;
        }
        if (!point.IsLeftButtonPressed) return;

        if (editor.Tool.Kind == null && TailAt(p, route) is { } roll)
        {
            // Select, on a roll's end: drag it to make the roll longer or shorter.
            if (!editor.SelectedNotes.Contains(roll)) editor.Select(new[] { roll });
            drag = Drag.Resize;
            resizing = roll;
            e.Pointer.Capture(this);
            return;
        }
        if (editor.Tool.Kind is not { } kind)
        {
            // Select: a note is picked (with Shift or ⌘/Ctrl added or taken away) and dragged to
            // move; elsewhere a box is drawn.
            if (note != null)
            {
                if (additive) { editor.ToggleSelected(note); return; }
                if (!editor.SelectedNotes.Contains(note)) editor.Select(new[] { note });
                editor.Reveal(note);
                drag = Drag.Move;
                moveBeats = 0;
            }
            else
            {
                if (!additive) editor.ClearSelection();
                drag = Drag.Box;
            }
            e.Pointer.Capture(this);
            return;
        }
        if (Snap(p.X, route) is not { } at) return;
        if (kind is NoteKind.Roll or NoteKind.BigRoll or NoteKind.Balloon or NoteKind.Kusudama)
        {
            drag = Drag.Roll;
            dragStart = at;
            e.Pointer.Capture(this);
        }
        else editor.PlaceNote(at, null);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        hover = e.GetPosition(this);
        if (editor == null) return;
        var p = hover.Value;
        switch (drag)
        {
            case Drag.None:
                // The end of a roll can be dragged with Select.
                int row = RowAt(p.Y);
                bool onTail = editor.Tool.Kind == null && row >= 0 && TailAt(p, Rows[row]) != null;
                bool onMark = InEvents(p.Y) ? CommandNear(p.X) != null : FlagAt(p) != null;
                Cursor = new Cursor(onTail ? StandardCursorType.SizeWestEast : onMark ? StandardCursorType.Hand : StandardCursorType.Cross);
                break;
            case Drag.Seek:
                editor.Seek(TimeOf(Math.Max(HeaderWidth, p.X)));
                break;
            case Drag.Move:
                // Whole grid steps from where the press snapped.
                if (BeatAt(pressPoint.X, dragRow) is { } from && BeatAt(p.X, dragRow) is { } to)
                {
                    double step = 4.0 / editor.Division;
                    moveBeats = Math.Round((to - from) / step) * step;
                }
                break;
        }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var p = e.GetPosition(this);
        if (editor != null)
            switch (drag)
            {
                case Drag.Roll when dragStart is { } start:
                    var end = Snap(p.X, start.Route);
                    // A click without a drag gives the shortest roll: one grid step.
                    if (end is not { } stop || stop.Time <= start.Time)
                        end = start.Step + 1 < start.Steps ? start with { Step = start.Step + 1 } : Snap(XOf(start.Measure.EndTime) + 0.5, start.Route);
                    editor.PlaceNote(start, end);
                    break;
                case Drag.Move when moveBeats != 0:
                    editor.MoveSelection((int)Math.Round(moveBeats * editor.Division / 4), dragRow);
                    break;
                case Drag.Resize when resizing is { } roll:
                    if (Snap(p.X, dragRow) is { } newEnd && newEnd.Time > roll.Time && Math.Abs(newEnd.Time - roll.EndTime) > 1e-6)
                        editor.ResizeRoll(roll, newEnd, dragRow);
                    break;
                case Drag.Command when movingCommand is { } moving:
                    if (CommandTarget(moving, p.X) is { } target) editor.MoveCommand(moving, target);
                    break;
                case Drag.Box:
                    BoxSelect(new Rect(pressPoint, p), (e.KeyModifiers & (KeyModifiers.Shift | KeyModifiers.Meta | KeyModifiers.Control)) != 0);
                    break;
            }
        drag = Drag.None;
        dragStart = null;
        resizing = null;
        movingCommand = null;
        moveBeats = 0;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    // The notes whose heads are in a box, row by row.
    void BoxSelect(Rect box, bool add)
    {
        if (editor?.Analysis.Chart is not { } chart) return;
        var picked = new List<ChartNote>();
        for (int row = 0; row < Rows.Count; row++)
        {
            double cy = RowTop(row) + RowHeight / 2;
            if (cy < box.Top || cy > box.Bottom) continue;
            var route = Rows[row];
            foreach (var n in chart.Notes)
            {
                if (n.BranchId >= 0 && n.Route != route) continue;
                double x = XOf(n.Time);
                if (x >= box.Left && x <= box.Right) picked.Add(n);
            }
        }
        editor.Select(picked, add);
    }

    static MenuItem Item(string header, Action run, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => run();
        return item;
    }

    // With Select: what to do with the notes (the one under the pointer joins the selection).
    void NoteMenu(Point p, BranchRoute route, ChartNote? note)
    {
        if (editor == null) return;
        if (note != null && !editor.SelectedNotes.Contains(note)) editor.Select(new[] { note });
        bool any = editor.SelectedNotes.Count > 0;
        var kinds = new MenuItem { Header = Loc.T("ChangeTo"), IsEnabled = any };
        foreach (var tool in editor.Tools.Where(t => t.Kind != null))
        {
            var kind = tool.Kind!.Value;
            kinds.Items.Add(Item(tool.Label, () => editor.ChangeSelectionKind(kind)));
        }
        var items = new List<object>
        {
            Item(Loc.T("Delete"), editor.DeleteSelection, any),
            kinds,
            Item(Loc.T("SwapDonKa"), editor.SwapSelectionColour, any),
            new Separator(),
            Item(Loc.T("Cut"), () => editor.CutSelection(route), any),
            Item(Loc.T("Copy"), () => editor.CopySelection(route), any),
        };
        if (Snap(p.X, route) is { } at) items.Add(Item(Loc.T("PasteHere"), () => editor.Paste(at)));
        items.Add(Item(Loc.T("SelectAll"), editor.SelectAll));
        new ContextMenu { ItemsSource = items }.Open(this);
    }

    // A branch's flag: its condition changed, its start shown in the code, or the branch taken
    // out with its normal route kept (undo brings it back).
    void BranchMenu(ChartBranchMark branch)
    {
        if (editor == null) return;
        var start = branch.Start;
        new ContextMenu
        {
            ItemsSource = new List<object>
            {
                Item(Loc.F("EditCommand", (start.Name + " " + start.Value).Trim()), () => _ = editor.EditCommand(start)),
                Item(Loc.T("ShowInCode"), () => editor.RevealCommand(start)),
                new Separator(),
                Item(Loc.F("RemoveBranch", Localization.Names.Route(BranchRoute.Normal)), () => editor.RemoveBranch(branch)),
            },
        }.Open(this);
    }

    // The command whose tick is nearest an x, within a few pixels.
    ChartCommand? CommandNear(double x)
    {
        if (editor == null) return null;
        return editor.Analysis.Commands.Where(c => c.IsOn(editor.Route))
            .Select(c => (c, d: Math.Abs(XOf(c.Time) - x))).Where(c => c.d <= 8).OrderBy(c => c.d).Select(c => c.c).FirstOrDefault();
    }

    // Where a dragged command would go: the grid position under x on its route (a measure's start
    // for #MEASURE); null where nothing would change.
    GridPosition? CommandTarget(ChartCommand command, double x)
    {
        if (editor == null) return null;
        var route = command.BranchId >= 0 ? command.Route : editor.Route;
        if (Snap(x, route) is not { } at) return null;
        if (command.Name == "#MEASURE") at = at with { Step = 0 };
        return Math.Abs(at.Time - command.Time) < 1e-6 ? null : at;
    }

    // A roll's end (on the row's route) within reach of the pointer.
    ChartNote? TailAt(Point p, BranchRoute route)
    {
        if (editor?.Analysis.Chart is not { } chart) return null;
        return chart.Notes.Where(n => n.IsLong && (n.BranchId < 0 || n.Route == route) && Math.Abs(XOf(n.EndTime) - p.X) <= 7 && XOf(n.EndTime) - XOf(n.Time) > 10)
            .OrderBy(n => Math.Abs(XOf(n.EndTime) - p.X)).FirstOrDefault();
    }

    // The commands row's menu: commands to write at the place, and the one near it to change or remove.
    void EventMenu(Point p)
    {
        if (editor == null) return;
        var items = new List<object>();
        if (Snap(p.X, editor.Route) is { } at)
        {
            foreach (var (label, kind) in new[]
            {
                (Loc.F("InsertHereValue", "#BPMCHANGE"), ChartEventKind.Tempo), (Loc.F("InsertHereValue", "#SCROLL"), ChartEventKind.Scroll),
                (Loc.F("InsertAtMeasure", "#MEASURE"), ChartEventKind.Measure), (Loc.F("InsertHereValue", "#DELAY"), ChartEventKind.Delay),
                (Loc.F("InsertHere", "#GOGOSTART"), ChartEventKind.GogoStart), (Loc.F("InsertHere", "#GOGOEND"), ChartEventKind.GogoEnd),
                (Loc.F("InsertHere", "#BARLINEOFF"), ChartEventKind.BarlineOff), (Loc.F("InsertHere", "#BARLINEON"), ChartEventKind.BarlineOn),
            })
            {
                var position = kind == ChartEventKind.Measure ? at with { Step = 0 } : at;
                items.Add(Item(label, () => _ = editor.InsertEvent(position, kind)));
            }
        }
        if (CommandNear(p.X) is { } command)
        {
            items.Add(new Separator());
            string written = (command.Name + " " + command.Value).Trim();
            if (command.Value.Length > 0) items.Add(Item(Loc.F("EditCommand", written), () => _ = editor.EditCommand(command)));
            items.Add(Item(Loc.F("RemoveCommand", written), () => editor.RemoveCommand(command)));
            items.Add(Item(Loc.T("ShowInCode"), () => editor.RevealCommand(command)));
        }
        if (items.Count > 0) new ContextMenu { ItemsSource = items }.Open(this);
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
        bool command = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        var route = editor.Route;
        bool selection = editor.SelectedNotes.Count > 0;
        e.Handled = true;
        switch (e.Key)
        {
            case Key.Space: editor.TogglePlayCommand.Execute(null); return;
            case Key.Escape: editor.ClearSelection(); return;
            case Key.Delete or Key.Back: editor.DeleteSelection(); return;
            case Key.A when command: editor.SelectAll(); return;
            case Key.C when command: editor.CopySelection(route); return;
            case Key.X when command: editor.CutSelection(route); return;
            case Key.V when command:
                // At the pointer when it is over a row, else at the playhead.
                int row = hover is { } h ? RowAt(h.Y) : -1;
                var at = row >= 0 ? Snap(hover!.Value.X, Rows[row]) : Snap(XOf(editor.Playhead), route);
                if (at is { } place) editor.Paste(place);
                return;
            case Key.Left or Key.Right:
                int steps = e.Key == Key.Left ? -1 : 1;
                if (selection) editor.MoveSelection(steps, route);
                else
                {
                    var bpm = editor.Analysis.Measures.LastOrDefault(m => m.IsOn(route) && m.Time <= editor.Playhead)?.Bpm ?? 120;
                    editor.Seek(editor.Playhead + steps * 60 / bpm * 4 / editor.Division);
                }
                return;
        }
        if (command) { e.Handled = false; return; }
        string key = e.Key switch
        {
            >= Key.D0 and <= Key.D9 => ((int)(e.Key - Key.D0)).ToString(CultureInfo.InvariantCulture),
            >= Key.NumPad0 and <= Key.NumPad9 => ((int)(e.Key - Key.NumPad0)).ToString(CultureInfo.InvariantCulture),
            _ => e.Key.ToString().ToUpperInvariant(),
        };
        if (editor.Tools.FirstOrDefault(t => t.Key == key) is not { } tool) { e.Handled = false; return; }
        // With notes selected under Select, a note key changes their kind.
        if (selection && editor.Tool.Kind == null && tool.Kind is { } kind) editor.ChangeSelectionKind(kind);
        else editor.Tool = tool;
    }
}
