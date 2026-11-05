using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using CharTaiko.App.ViewModels;
using CharTaiko.Core.Play;
using OurTaiko;

namespace CharTaiko.App.Controls;

// The lane preview, laid out as OurTaikoPlay draws its lane (Nijiiro's 1920-wide stage scaled to the
// panel): the lane from x 498 with the judge 120 in, 200 high, the note-text strip under it, notes
// placed by the game's NoteScroll (normal, BM and HB scrolling, #SUDDEN, rolls as rigid bodies),
// #JPOSSCROLL moving the judge, bar lines 3 wide (6 and gold at a branch), the route's lane colour,
// go-go time and #LANE_ALPHA. Notes are drawn plain (the game's sprites are its art). Notes that
// reach the judge are taken as hit; rolls stay until they end, balloons at the judge with their
// count. Clicking a note shows it in the code.
public sealed class LaneView : Control
{
    // The design units of the game's lane (NoteLaneRenderer, NoteScroll).
    const double LaneWidth = 1920 - 498, LaneHeight = 200, StripHeight = 64, JudgeX = 120, JudgeY = 100;
    const double SmallRadius = 51, BigRadius = 76, MojiDrop = 123, MeasureWidth = NoteScroll.MeasureWidth;

    EditorViewModel? editor;
    ScrollClockTimeline? clocks;
    TaikoChart? clocksOf;
    readonly Typeface typeface = new(FontFamily.Default);
    readonly Typeface bold = new(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);
    readonly Typeface moji = new(new FontFamily("Hiragino Sans, Yu Gothic UI, Noto Sans CJK JP, Microsoft YaHei, sans-serif"), FontStyle.Normal, FontWeight.Black);
    // Where notes were drawn in the last frame, for clicks.
    readonly List<(ChartNote Note, Point Center, double Radius)> drawn = new();

    static readonly IBrush LaneBrush = new SolidColorBrush(Color.Parse("#313131"));
    static readonly IBrush StripBrush = new SolidColorBrush(Color.Parse("#6A6A6A"));
    static readonly IBrush StripEdge = new SolidColorBrush(Color.Parse("#4A4A4A"));
    static readonly IBrush[] RouteTint =
    {
        Brushes.Transparent,
        new SolidColorBrush(Color.FromArgb(150, 0x1E, 0x82, 0xBD)),
        new SolidColorBrush(Color.FromArgb(140, 0xE5, 0x1E, 0xBD)),
    };
    static readonly IBrush GogoWash = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.FromArgb(230, 0x98, 0x14, 0x28), 0), new GradientStop(Color.FromArgb(120, 0xC8, 0x3C, 0x1E), 1) },
    };
    static readonly IBrush Don = new SolidColorBrush(Color.Parse("#F14B2E"));
    static readonly IBrush Ka = new SolidColorBrush(Color.Parse("#5BC0CB"));
    static readonly IBrush Roll = new SolidColorBrush(Color.Parse("#FCC31C"));
    static readonly IBrush Balloon = new SolidColorBrush(Color.Parse("#FF7E1C"));
    static readonly IBrush Kadon = new SolidColorBrush(Color.Parse("#A562E8"));
    static readonly IBrush Mine = new SolidColorBrush(Color.Parse("#3B3B44"));
    static readonly IBrush Rim = new SolidColorBrush(Color.Parse("#F7F0E0"));
    static readonly IBrush Outline = new SolidColorBrush(Color.Parse("#1C1C1C"));

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
        if (e.PropertyName is nameof(EditorViewModel.Playhead) or nameof(EditorViewModel.Analysis) or nameof(EditorViewModel.Route) or nameof(EditorViewModel.Summary))
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
        if (!ReferenceEquals(chart, clocksOf)) { clocks = new ScrollClockTimeline(chart); clocksOf = chart; }
        double now = editor.Playhead;
        var route = editor.Route;

        // The game's lane, scaled to the panel's width (and kept to its height).
        double s = Math.Min(b.Width / LaneWidth, (b.Height - 70) / (LaneHeight + StripHeight));
        if (!(s > 0)) return;
        double top = Math.Max(34, (b.Height - (LaneHeight + StripHeight) * s) / 2), laneH = LaneHeight * s;
        var lane = new Rect(0, top, b.Width, laneH);

        // The lane: its colour under #LANE_ALPHA, the route's tint once a branch has been decided,
        // go-go time's wash.
        double alpha = LaneAlpha.At(chart.LaneAlphas, now, a => Shown(chart, a.BranchId, a.Route, route));
        using (ctx.PushOpacity(alpha))
        {
            ctx.FillRectangle(LaneBrush, lane);
            if (chart.Branches.Count > 0 && now >= chart.Branches[0].DecisionTime) ctx.FillRectangle(RouteTint[(int)route], lane);
            bool gogo = chart.Gogos.Where(g => g.Time <= now && Shown(chart, g.BranchId, g.Route, route)).Select(g => g.On).LastOrDefault();
            if (gogo) ctx.FillRectangle(GogoWash, lane);
        }
        ctx.FillRectangle(StripEdge, new Rect(0, top + laneH, b.Width, 3 * s));
        ctx.FillRectangle(StripBrush, new Rect(0, top + laneH + 3 * s, b.Width, StripHeight * s - 3 * s));

        // The judge, moved by #JPOSSCROLL; notes are placed from it.
        var shift = JudgeShift.At(chart.JudgeMoves, now);
        double jx = (JudgeX + shift.X) * s, jy = top + (JudgeY + shift.Y) * s;
        ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Color.Parse("#8A8A8A")), 2.5 * s), new Point(jx, jy), 78 * s, 78 * s);
        ctx.DrawEllipse(new SolidColorBrush(Color.Parse("#3C3C3C")), new Pen(new SolidColorBrush(Color.Parse("#6E6E6E")), 7 * s), new Point(jx, jy), 52 * s, 52 * s);

        (double X, double Y) Place(ChartNote n) { var d = NoteScroll.Distance(n, now, clocks!, MeasureWidth); return (jx + d.X * s, jy + d.Y * s); }
        (double X, double Y) Tail(ChartNote n) { var d = NoteScroll.TailDistance(n, now, clocks!, MeasureWidth); return (jx + d.X * s, jy + d.Y * s); }

        foreach (var bar in chart.Bars)
        {
            if (!bar.Display || !Shown(chart, bar.BranchId, bar.Route, route)) continue;
            var (x, y) = Place(bar);
            if (x < -4 || x > b.Width + 4) continue;
            double w = (bar.IsBranchStart ? 6 : 3) * s;
            var brush = bar.IsBranchStart ? new SolidColorBrush(Color.FromArgb(204, 255, 204, 51)) : new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));
            ctx.FillRectangle(brush, new Rect(x - w / 2, y - 4 * s - 100 * s, w, 200 * s));
        }

        // Notes, later ones under earlier ones; their text in the strip.
        for (int i = chart.Notes.Count - 1; i >= 0; i--)
        {
            var n = chart.Notes[i];
            if (!Shown(chart, n.BranchId, n.Route, route) || !NoteScroll.Appeared(n, now)) continue;
            bool rolling = n.IsLong && n.Time <= now && now <= n.EndTime;
            if (n.Time < now && !rolling) continue;
            double radius = (ChartPalette.IsBig(n.Kind) ? BigRadius : SmallRadius) * s;
            var (hx, hy) = n.IsBalloon && rolling ? (jx, jy) : Place(n);
            if (hx - radius > b.Width) continue;
            double tailX = hx;
            if (n.IsDrumroll)
            {
                var (tx, ty) = Tail(n);
                tailX = tx;
                if (tx > -radius) DrawRoll(ctx, n.Kind, new Point(hx, hy), new Point(tx, ty), radius, s);
            }
            if (hx + radius < 0 && tailX < 0) continue;
            DrawNote(ctx, n.Kind, new Point(hx, hy), radius, s);
            if (n.IsBalloon && rolling)
                Label(ctx, n.BalloonHits.ToString(CultureInfo.InvariantCulture), new Point(hx + radius * 0.9, hy - radius * 1.6), 26 * s + 6, ChartPalette.Text, bold);
            DrawMoji(ctx, n, new Point(hx, jy + MojiDrop * s), tailX, s);
            drawn.Add((n, new Point(hx, hy), radius));
        }

        var measure = editor.Analysis.Measures.LastOrDefault(m => m.Time <= now && (m.BranchId < 0 || Shown(chart, m.BranchId, m.Route, route)));
        var tempo = chart.Tempos.LastOrDefault(t => t.Time <= now && Shown(chart, t.BranchId, t.Route, route));
        double bpm = tempo?.Bpm ?? chart.Bpm;
        string info = $"{Time(now)}   BPM {bpm:0.###}   Measure {(measure?.Index ?? 0) + 1}";
        if (chart.Branches.Count > 0) info += "   Route " + route;
        Label(ctx, info, new Point(12, 8), 13, ChartPalette.Text);
        string summary = editor.Summary.Length > 0 ? editor.Summary
            : $"{chart.Notes.Count(n => !n.IsLong && n.Kind != NoteKind.AdLib && n.Kind != NoteKind.Mine)} notes";
        Label(ctx, $"{chart.Course} ★{chart.Level} · {summary}", new Point(12, b.Height - 22), 12, ChartPalette.Muted);
    }

    static IBrush Fill(NoteKind kind) => kind switch
    {
        NoteKind.Don or NoteKind.BigDon => Don,
        NoteKind.Ka or NoteKind.BigKa => Ka,
        NoteKind.Roll or NoteKind.BigRoll => Roll,
        NoteKind.Balloon or NoteKind.Kusudama => Balloon,
        NoteKind.Kadon => Kadon,
        NoteKind.Mine => Mine,
        _ => ChartPalette.AdLib,
    };

    // A note as the game's sprites are shaped: a dark outline, a cream rim, the colour.
    static void DrawNote(DrawingContext ctx, NoteKind kind, Point c, double r, double s)
    {
        if (kind == NoteKind.AdLib)
        {
            // Not drawn in play; a faint ring here so it can be found.
            ctx.DrawEllipse(null, ChartPalette.AdLibRim, c, r * 0.8, r * 0.8);
            return;
        }
        if (kind is NoteKind.Balloon or NoteKind.Kusudama)
        {
            // The balloon's knot trailing behind the face.
            var knot = new StreamGeometry();
            using (var g = knot.Open())
            {
                g.BeginFigure(new Point(c.X + r * 0.55, c.Y - r * 0.55), true);
                g.LineTo(new Point(c.X + r * 1.45, c.Y));
                g.LineTo(new Point(c.X + r * 0.55, c.Y + r * 0.55));
                g.EndFigure(true);
            }
            ctx.DrawGeometry(Balloon, new Pen(Outline, 3 * s), knot);
        }
        ctx.DrawEllipse(Outline, null, c, r, r);
        ctx.DrawEllipse(Rim, null, c, r - 3 * s, r - 3 * s);
        double face = r - 10 * s;
        ctx.DrawEllipse(Fill(kind), null, c, face, face);
        if (kind == NoteKind.Mine)
        {
            var cross = new Pen(Don, 6 * s);
            double d = face * 0.5;
            ctx.DrawLine(cross, new Point(c.X - d, c.Y - d), new Point(c.X + d, c.Y + d));
            ctx.DrawLine(cross, new Point(c.X - d, c.Y + d), new Point(c.X + d, c.Y - d));
        }
        else if (kind == NoteKind.Kusudama) ctx.DrawEllipse(null, new Pen(Rim, 5 * s), c, face * 0.62, face * 0.62);
        else if (kind == NoteKind.Kadon) ctx.DrawLine(new Pen(Ka, face * 0.5), new Point(c.X, c.Y - face * 0.75), new Point(c.X, c.Y + face * 0.75));
    }

    // A drumroll's body from the head to its tail (turned toward a tail that a vertical SCROLL
    // put off the line), with a rounded end, under the head.
    static void DrawRoll(DrawingContext ctx, NoteKind kind, Point head, Point tail, double r, double s)
    {
        double dx = tail.X - head.X, dy = tail.Y - head.Y, length = Math.Sqrt(dx * dx + dy * dy);
        double angle = Math.Atan2(dy, dx);
        using var _ = ctx.PushTransform(Matrix.CreateRotation(angle) * Matrix.CreateTranslation(head.X, head.Y));
        ctx.DrawRectangle(Outline, null, new Rect(0, -r, length + r, 2 * r), r, r);
        ctx.DrawRectangle(Rim, null, new Rect(0, -(r - 3 * s), length + r - 3 * s, 2 * (r - 3 * s)), r - 3 * s, r - 3 * s);
        double face = r - 10 * s;
        ctx.DrawRectangle(Fill(kind), null, new Rect(0, -face, length + face, 2 * face), face, face);
    }

    // The note's text (NoteMoji) in the strip under it; a roll's 連打 runs to its ーっ!!.
    void DrawMoji(DrawingContext ctx, ChartNote n, Point at, double tailX, double s)
    {
        string? text = n.Moji switch
        {
            NoteMoji.Don => "ドン", NoteMoji.DonShort => "ド", NoteMoji.Ko => "コ", NoteMoji.Ka => "カッ", NoteMoji.KaShort => "カ",
            NoteMoji.BigDon => "ドン(大)", NoteMoji.BigKa => "カッ(大)", NoteMoji.Roll => "連打", NoteMoji.BigRoll => "連打(大)",
            NoteMoji.Balloon => "ふうせん", NoteMoji.Kusudama => "くすだま", NoteMoji.Kadon => "カドン",
            _ => null,
        };
        if (text == null) return;
        double size = 30 * s + 2;
        var head = MojiText(text, size);
        // The outline under the letters, so they stay white.
        var outline = new Pen(Outline, 5 * s + 1.5) { LineJoin = PenLineJoin.Round };
        void Text(FormattedText t, Point origin)
        {
            var g = t.BuildGeometry(origin)!;
            ctx.DrawGeometry(null, outline, g);
            ctx.DrawGeometry(Brushes.White, null, g);
        }
        if (n.IsDrumroll && tailX > at.X + head.Width)
        {
            var end = MojiText("ーっ!!", size);
            var from = new Point(at.X + head.Width / 2, at.Y);
            var to = new Point(tailX - end.Width / 2, at.Y);
            ctx.DrawLine(new Pen(Outline, 9 * s + 2), from, to);
            ctx.DrawLine(new Pen(Brushes.White, 5 * s + 1), from, to);
            Text(end, new Point(tailX - end.Width / 2, at.Y - end.Height / 2));
        }
        Text(head, new Point(at.X - head.Width / 2, at.Y - head.Height / 2));
    }

    FormattedText MojiText(string text, double size) => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, moji, size, Brushes.White);

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
