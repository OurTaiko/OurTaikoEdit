using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using CharTaiko.App.ViewModels;
using OurTaiko;

namespace CharTaiko.App.Controls;

// The lane preview: the parsed chart as it scrolls to the judge at the playhead, each note at
// its own BPM × SCROLL. Notes that reach the judge are taken as hit; rolls stay until they end.
// Clicking a note shows it in the code.
public sealed class LaneView : Control
{
    EditorViewModel? editor;
    readonly Typeface typeface = new(FontFamily.Default);
    readonly Typeface bold = new(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);
    // Where notes were drawn in the last frame, for clicks.
    readonly List<(ChartNote Note, Point Center, double Radius)> drawn = new();

    public LaneView()
    {
        ClipToBounds = true;
        Focusable = true;
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
        if (e.PropertyName is nameof(EditorViewModel.Playhead) or nameof(EditorViewModel.Analysis) or nameof(EditorViewModel.Route))
            InvalidateVisual();
    }

    // Whether a note (or anything with a branch and route) is on the route being shown.
    public static bool Shown(TaikoChart chart, int branchId, BranchRoute route, BranchRoute requested)
        => branchId < 0 || branchId < chart.Branches.Count && chart.Branches[branchId].ResolveRoute(requested) == route;

    public override void Render(DrawingContext ctx)
    {
        var b = Bounds;
        ctx.FillRectangle(ChartPalette.Background, new Rect(b.Size));
        drawn.Clear();
        if (editor?.Analysis.Chart is not { } chart)
        {
            Label(ctx, "The chart appears here once its block parses.", new Point(16, 16), 13, ChartPalette.Muted);
            return;
        }
        double now = editor.Playhead;
        var route = editor.Route;

        double laneH = Math.Clamp(b.Height * 0.42, 64, 150);
        double top = (b.Height - laneH) / 2 + 10, cy = top + laneH / 2;
        // As on the arcade: a small note is about two fifths of the lane high, and one 4/4 measure
        // at SCROLL 1 takes about the lane from the judge, so 16ths overlap a little.
        double r = laneH * 0.21;
        double judgeX = laneH * 0.8 + 12;
        double beat = (b.Width - judgeX) / 4.2;

        bool gogo = chart.Gogos.Where(g => g.Time <= now && Shown(chart, g.BranchId, g.Route, route)).Select(g => g.On).LastOrDefault();
        double alpha = LaneAlpha.At(chart.LaneAlphas, now, a => Shown(chart, a.BranchId, a.Route, route));
        using (ctx.PushOpacity(0.35 + 0.65 * alpha))
            ctx.FillRectangle(gogo ? ChartPalette.GogoLane : ChartPalette.Lane, new Rect(0, top, b.Width, laneH));

        double X(double time, ChartNote n) => judgeX + (time - now) * n.Bpm / 60 * n.ScrollX * beat;
        double Y(double time, ChartNote n) => cy - (time - now) * n.Bpm / 60 * n.ScrollY * beat;

        foreach (var bar in chart.Bars)
        {
            if (!bar.Display || bar.Time < now || !Shown(chart, bar.BranchId, bar.Route, route)) continue;
            double x = X(bar.Time, bar);
            if (x > b.Width + 2) continue;
            double y = Y(bar.Time, bar) - cy;
            ctx.DrawLine(bar.IsBranchStart ? ChartPalette.BranchBar : ChartPalette.Bar, new Point(x, top + y), new Point(x, top + laneH + y));
        }

        // The judge.
        ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), new Pen(new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)), 2), new Point(judgeX, cy), r * 1.08, r * 1.08);
        ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 1.5), new Point(judgeX, cy), r * 1.45, r * 1.45);

        // Later notes under earlier ones.
        for (int i = chart.Notes.Count - 1; i >= 0; i--)
        {
            var n = chart.Notes[i];
            if (!Shown(chart, n.BranchId, n.Route, route)) continue;
            bool rolling = n.IsLong && n.Time <= now && now <= n.EndTime;
            if (n.Time < now && !rolling) continue;
            double radius = ChartPalette.IsBig(n.Kind) ? r * 1.45 : r;
            double hx = rolling ? judgeX : X(n.Time, n), hy = rolling ? cy : Y(n.Time, n);
            if (hx - radius > b.Width) continue;
            if (n.IsDrumroll)
            {
                double tx = X(n.EndTime, n), ty = Y(n.EndTime, n);
                var body = new Rect(new Point(hx, hy - radius * 0.92), new Point(Math.Max(hx, tx), hy + radius * 0.92));
                ctx.DrawRectangle(ChartPalette.Roll, ChartPalette.NoteRim, body);
                ctx.DrawEllipse(ChartPalette.Roll, ChartPalette.NoteRim, new Point(Math.Max(hx, tx), ty), radius * 0.92, radius * 0.92);
                ctx.FillRectangle(ChartPalette.Roll, new Rect(new Point(Math.Max(hx, tx) - radius * 0.92, ty - radius * 0.92 + 2), new Point(Math.Max(hx, tx), ty + radius * 0.92 - 2)));
            }
            ChartPalette.DrawHead(ctx, n.Kind, new Point(hx, hy), radius);
            if (n.IsBalloon && rolling)
                Label(ctx, n.BalloonHits.ToString(CultureInfo.InvariantCulture), new Point(hx - 6, hy - radius - 22), 14, ChartPalette.Text, bold);
            drawn.Add((n, new Point(hx, hy), radius));
        }

        var measure = editor.Analysis.Measures.LastOrDefault(m => m.Time <= now && (m.BranchId < 0 || Shown(chart, m.BranchId, m.Route, route)));
        var tempo = chart.Tempos.LastOrDefault(t => t.Time <= now && Shown(chart, t.BranchId, t.Route, route));
        double bpm = tempo?.Bpm ?? chart.Bpm;
        string info = $"{Time(now)}   BPM {bpm:0.###}   Measure {(measure?.Index ?? 0) + 1}";
        if (chart.Branches.Count > 0) info += "   Route " + route;
        Label(ctx, info, new Point(12, 10), 13, ChartPalette.Text);
        Label(ctx, $"{chart.Course} ★{chart.Level} · {chart.Notes.Count(n => !n.IsLong && n.Kind != NoteKind.AdLib && n.Kind != NoteKind.Mine)} notes",
            new Point(12, b.Height - 26), 12, ChartPalette.Muted);
    }

    static string Time(double seconds)
    {
        string sign = seconds < 0 ? "-" : "";
        var t = TimeSpan.FromSeconds(Math.Abs(seconds));
        return $"{sign}{(int)t.TotalMinutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";
    }

    void Label(DrawingContext ctx, string text, Point at, double size, IBrush brush, Typeface? face = null)
        => ctx.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face ?? typeface, size, brush), at);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var p = e.GetPosition(this);
        // The front-most note under the pointer (drawn last).
        for (int i = drawn.Count - 1; i >= 0; i--)
        {
            var (note, c, radius) = drawn[i];
            if (Math.Abs(p.X - c.X) <= radius && Math.Abs(p.Y - c.Y) <= radius) { editor?.Reveal(note); return; }
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Space && editor != null) { editor.TogglePlayCommand.Execute(null); e.Handled = true; }
    }
}
