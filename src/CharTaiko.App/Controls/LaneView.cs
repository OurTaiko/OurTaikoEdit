using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using CharTaiko.App.ViewModels;
using CharTaiko.Core.Localization;
using OurTaiko;

namespace CharTaiko.App.Controls;

// The lane preview, laid out as OurTaikoPlay draws its lane (Nijiiro's 1920-wide stage scaled to the
// panel): the lane from x 498 with the judge 120 in, 200 high, the note-text strip under it, notes
// placed by the game's NoteScroll (normal, BM and HB scrolling, #SUDDEN, rolls as rigid bodies),
// #JPOSSCROLL moving the judge, bar lines 3 wide (6 and gold at a branch), the route's lane colour,
// go-go time and #LANE_ALPHA, and the game's note and note-text sprites. Notes that
// reach the judge are taken as hit; rolls stay until they end, balloons at the judge with their
// count. Clicking a note shows it in the code.
public sealed class LaneView : Control
{
    // The design units of the game's lane (NoteLaneRenderer, NoteScroll).
    const double LaneWidth = 1920 - 498, LaneHeight = 200, StripHeight = 64, JudgeX = 120, JudgeY = 100;
    const double SmallRadius = 51, BigRadius = 76, MojiDrop = 123, MeasureWidth = NoteScroll.MeasureWidth;
    // Sprite sizes: note frames 192 square, text frames 256×48, a balloon's face shifted by 12/128.
    const double NoteSize = 192, MojiWidth = 256, MojiHeight = 48, BalloonFace = 12.0 / 128;

    EditorViewModel? editor;
    ScrollClockTimeline? clocks;
    TaikoChart? clocksOf;
    readonly Typeface typeface = new(FontFamily.Default);
    readonly Typeface bold = new(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);
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
            Label(ctx, Loc.T("LaneEmpty"), new Point(16, 16), 13, ChartPalette.Muted);
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

        // Notes, later ones under earlier ones; then their text, in its own layer above the notes
        // (the game's draw_notes second pass), again earlier over later.
        var texts = new List<(ChartNote Note, Point At, double Length)>();
        for (int i = chart.Notes.Count - 1; i >= 0; i--)
        {
            var n = chart.Notes[i];
            if (!Shown(chart, n.BranchId, n.Route, route) || !NoteScroll.Appeared(n, now)) continue;
            bool rolling = n.IsLong && n.Time <= now && now <= n.EndTime;
            if (n.Time < now && !rolling) continue;
            double radius = (ChartPalette.IsBig(n.Kind) ? BigRadius : SmallRadius) * s;
            var (hx, hy) = n.IsBalloon && rolling ? (jx, jy) : Place(n);
            if (hx - NoteSize / 2 * s > b.Width) continue;
            double length = 0, rise = 0;
            if (n.IsDrumroll)
            {
                var (tx, ty) = Tail(n);
                length = tx - hx;
                rise = ty - hy;
            }
            if (hx + NoteSize * s < 0 && hx + length + NoteSize * s < 0) continue;
            var head = new Point(hx, hy);
            if (n.IsDrumroll) DrawRoll(ctx, n.Kind == NoteKind.BigRoll, head, length, rise, s);
            DrawNote(ctx, n.Kind, head, s, balloonTail: !(n.IsBalloon && rolling));
            if (n.IsBalloon && rolling)
                Label(ctx, n.BalloonHits.ToString(CultureInfo.InvariantCulture), new Point(hx + radius * 0.9, hy - radius * 1.6), 26 * s + 6, ChartPalette.Text, bold);
            texts.Add((n, new Point(hx, hy + MojiDrop * s), length));
            drawn.Add((n, head, radius));
        }
        foreach (var (n, at, length) in texts) DrawMoji(ctx, n, at, length, s);

        var measure = editor.Analysis.Measures.LastOrDefault(m => m.Time <= now && (m.BranchId < 0 || Shown(chart, m.BranchId, m.Route, route)));
        var tempo = chart.Tempos.LastOrDefault(t => t.Time <= now && Shown(chart, t.BranchId, t.Route, route));
        double bpm = tempo?.Bpm ?? chart.Bpm;
        string info = $"{Time(now)}   BPM {bpm.ToString("0.###", CultureInfo.InvariantCulture)}   {Loc.F("LaneMeasure", (measure?.Index ?? 0) + 1)}";
        if (chart.Branches.Count > 0) info += "   " + Loc.F("LaneRoute", Localization.Names.Route(route));
        Label(ctx, info, new Point(12, 8), 13, ChartPalette.Text);
        string summary = editor.Summary.Length > 0 ? editor.Summary
            : Loc.Plural("NoteCount", chart.Notes.Count(n => !n.IsLong && n.Kind != NoteKind.AdLib && n.Kind != NoteKind.Mine));
        Label(ctx, $"{chart.Course} ★{chart.Level} · {summary}", new Point(12, b.Height - 22), 12, ChartPalette.Muted);
    }

    // A note's head from the game's sheet, 192 units square about its centre. A balloon's face
    // is shifted left by 12/128 of its width with its tail joined on the right (draw_balloon); the
    // tail goes while it is being popped. An ad-lib, which the game does not draw, is a faint ring.
    static void DrawNote(DrawingContext ctx, NoteKind kind, Point c, double s, bool balloonTail)
    {
        if (GameSprites.Note(kind) is not { } frame)
        {
            ctx.DrawEllipse(null, ChartPalette.AdLibRim, c, SmallRadius * s * 0.8, SmallRadius * s * 0.8);
            return;
        }
        double size = NoteSize * s, left = c.X - size / 2, top = c.Y - size / 2;
        if (kind == NoteKind.Balloon)
        {
            left -= BalloonFace * size;
            if (balloonTail) ctx.DrawImage(GameSprites.Notes, GameSprites.BalloonTail, new Rect(left + size, top, size, size));
        }
        ctx.DrawImage(GameSprites.Notes, frame, new Rect(left, top, size, size));
    }

    // A drumroll's body from the head's centre and its tail at the end, turned toward a tail that a
    // vertical SCROLL put off the line, under the head (NoteLaneRenderer: the body is the length
    // plus 1/128 of the note's height, the tail as wide as its sprite's aspect).
    static void DrawRoll(DrawingContext ctx, bool big, Point head, double length, double rise, double s)
    {
        double size = NoteSize * s, direction = length < 0 ? -1 : 1;
        double span = Math.Sqrt(length * length + rise * rise);
        var body = GameSprites.RollBody(big);
        var tail = GameSprites.RollTail(big);
        double angle = Math.Atan2(direction * rise, Math.Abs(length));
        var turn = Matrix.CreateScale(direction, 1) * Matrix.CreateRotation(angle);
        using (ctx.PushTransform(turn * Matrix.CreateTranslation(head.X, head.Y)))
            ctx.DrawImage(GameSprites.Notes, body, new Rect(0, -size / 2, span + size / 128, size));
        using (ctx.PushTransform(turn * Matrix.CreateTranslation(head.X + length, head.Y + rise)))
            ctx.DrawImage(GameSprites.Notes, tail, new Rect(0, -size / 2, size * tail.Width / tail.Height, size));
    }

    // The note's text (NoteMoji) from the game's sheet, 256×48 under the note; a roll's 連打 runs
    // along moji_drumroll_mid to its ーっ!! at the tail.
    static void DrawMoji(DrawingContext ctx, ChartNote n, Point at, double length, double s)
    {
        if (GameSprites.MojiFrame(n.Moji) is not { } frame) return;
        double w = MojiWidth * s, h = MojiHeight * s;
        if (n.IsDrumroll)
        {
            // The strip is its native width plus the length, from the head's centre.
            double strip = h * GameSprites.MojiRoll.Size.Width / GameSprites.MojiRoll.Size.Height + length;
            var stripRect = strip >= 0 ? new Rect(at.X, at.Y - h / 2, strip, h) : new Rect(at.X + strip, at.Y - h / 2, -strip, h);
            ctx.DrawImage(GameSprites.MojiRoll, stripRect);
        }
        ctx.DrawImage(GameSprites.Moji, frame, new Rect(at.X - w / 2, at.Y - h / 2, w, h));
        if (n.IsDrumroll && GameSprites.MojiFrame(NoteMoji.Tail) is { } tail)
            ctx.DrawImage(GameSprites.Moji, tail, new Rect(at.X + length - w / 2, at.Y - h / 2, w, h));
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
