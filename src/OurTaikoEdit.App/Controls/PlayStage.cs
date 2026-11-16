using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using OurTaiko;
using OurTaikoEdit.App.Theme;
using OurTaikoEdit.App.ViewModels;

namespace OurTaikoEdit.App.Controls;

// The play scene's art (Assets/Game/play, see Assets/Game/PROVENANCE.md), each sheet read when it
// is first drawn.
static class PlaySprites
{
    static readonly Dictionary<string, Bitmap> Loaded = new();

    public static Bitmap Get(string name)
    {
        if (!Loaded.TryGetValue(name, out var bitmap))
            Loaded[name] = bitmap = new Bitmap(AssetLoader.Open(new Uri("avares://OurTaikoEdit/Assets/Game/play/" + name + ".png")));
        return bitmap;
    }

    // A sheet at a steady opacity, made once: drawing it costs what drawing a sheet costs, where
    // an opacity pushed on the drawing context is an off-screen layer every frame.
    public static IImage Faded(string name, double opacity)
    {
        string key = name + "@" + opacity.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!Made.TryGetValue(key, out var made))
        {
            var source = Get(name);
            var target = new RenderTargetBitmap(source.PixelSize);
            using (var ctx = target.CreateDrawingContext())
            using (ctx.PushOpacity(opacity))
                ctx.DrawImage(source, new Rect(source.Size));
            Made[key] = made = Freeze(target);
        }
        return made;
    }

    // A frame of a white-shaped sheet filled with a colour, made once.
    public static IImage Tinted(string name, Rect frame, Color tint)
    {
        string key = name + "#" + tint + frame;
        if (!Made.TryGetValue(key, out var made))
        {
            var target = new RenderTargetBitmap(new PixelSize((int)frame.Width, (int)frame.Height));
            var at = new Rect(0, 0, frame.Width, frame.Height);
            var mask = new ImageBrush(Get(name)) { SourceRect = new RelativeRect(frame, RelativeUnit.Absolute), Stretch = Stretch.Fill };
            using (var ctx = target.CreateDrawingContext())
            using (ctx.PushOpacityMask(mask, at))
                ctx.FillRectangle(new SolidColorBrush(tint), at);
            Made[key] = made = Freeze(target);
        }
        return made;
    }

    static readonly Dictionary<string, Bitmap> Made = new();

    // What was drawn, as a plain picture: one the renderer keeps on the graphics card like any
    // sheet, where a render target may be sent to it again every frame it is drawn.
    public static Bitmap Freeze(RenderTargetBitmap drawn)
    {
        using (drawn)
        {
            var size = drawn.PixelSize;
            int stride = size.Width * 4, bytes = stride * size.Height;
            IntPtr pixels = System.Runtime.InteropServices.Marshal.AllocHGlobal(bytes);
            try
            {
                drawn.CopyPixels(new PixelRect(size), pixels, bytes, stride);
                return new Bitmap(drawn.Format ?? PixelFormats.Bgra8888, drawn.AlphaFormat ?? AlphaFormat.Premul, pixels, size, new Vector(96, 96), stride);
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(pixels); }
        }
    }
}

// OurTaikoPlay's single-player scene (Play.Single), drawn by hand on its 1920×1080 canvas and fitted
// to the control: every rectangle, sprite frame and timing below is the scene's own (its
// RectTransforms resolved to canvas pixels, its view scripts and animation clips). In the game's
// draw order: the stage art, the lane, the soul gauge, bar lines, notes and their text, what lies
// over the lane (the player's panel with the drum, combo, score and judgment), then the HUD (judge
// counter, balloon and drumroll counters, the notes flying to the gauge).
//
// Left out: the dancers, the nameplate, the combo announcement, go-go time's fireworks, the
// kusudama's own ball (it is counted as a balloon is) and the ending animation.
public sealed class PlayStage : Control
{
    const double StageWidth = 1920, StageHeight = 1080;
    // The judge's centre, and the lane it is on.
    const double JudgeX = 618, JudgeY = 386, LaneLeft = 498, LaneTop = 276;
    // The soul badge, where the notes fly to.
    const double SoulX = 1834, SoulY = 246;

    PlayViewModel? play;
    readonly List<(ChartNote Note, Point At, double Length)> texts = new();
    ScrollClockTimeline? clocks;
    TaikoChart? clocksOf;
    readonly Typeface heavy = new(Ui.Font, FontStyle.Normal, FontWeight.Black);

    static readonly IBrush Backdrop = new SolidColorBrush(Color.Parse("#A52C24"));
    static readonly IBrush BarLine = new SolidColorBrush(Color.FromArgb(89, 255, 255, 255));
    static readonly IBrush BranchLine = new SolidColorBrush(Color.FromArgb(204, 255, 204, 51));

    public PlayStage()
    {
        ClipToBounds = true;
        // Bilinear: the sheets are drawn near their own size, and the still parts of the scene are
        // kept ready at the window's own pixels (Layers).
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.LowQuality);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (play != null) play.PropertyChanged -= Changed;
        play = DataContext as PlayViewModel;
        if (play != null) play.PropertyChanged += Changed;
        InvalidateVisual();
    }

    void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayViewModel.Frame) or nameof(PlayViewModel.Auto) or nameof(PlayViewModel.Paused)) InvalidateVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        back?.Dispose(); front?.Dispose();
        back = front = null;
    }

    public override void Render(DrawingContext ctx)
    {
        var b = Bounds;
        ctx.FillRectangle(Brushes.Black, new Rect(b.Size));
        if (play == null) return;
        double s = Math.Min(b.Width / StageWidth, b.Height / StageHeight);
        if (!(s > 0)) return;
        // The stage on whole pixels of the window, so the layers kept at that size are drawn as they are.
        double device = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var pixels = new PixelSize(Math.Max(1, (int)Math.Round(StageWidth * s * device)), Math.Max(1, (int)Math.Round(StageHeight * s * device)));
        double left = Math.Round((b.Width - pixels.Width / device) / 2 * device) / device, top = Math.Round((b.Height - pixels.Height / device) / 2 * device) / device;
        var fit = Matrix.CreateScale(pixels.Width / device / StageWidth, pixels.Height / device / StageHeight) * Matrix.CreateTranslation(left, top);
        PrepareLayers(play, pixels);
        using (ctx.PushTransform(fit))
        using (ctx.PushClip(new Rect(0, 0, StageWidth, StageHeight)))
            Draw(ctx, play);
    }

    // ---- The parts of the scene that stand still, each drawn once into a picture of the stage at
    // the window's pixels and then drawn as one image a frame: the stage art with the song's
    // title (back), and the player's panel and the judge counter's frame (front). Made again
    // when what they show changes, or when the stage has kept a new size for a few frames (while
    // the window is being dragged they are drawn piece by piece).
    Bitmap? back, front;
    string layersOf = "";
    PixelSize wanted;
    int wantedFor;

    void PrepareLayers(PlayViewModel p, PixelSize pixels)
    {
        string of = $"{p.Title}\n{p.Subtitle}\n{p.Chart.Course}\n{p.Auto}";
        if (pixels != wanted) { wanted = pixels; wantedFor = back == null ? 100 : 0; }
        else wantedFor++;
        bool current = back != null && back.PixelSize == pixels && layersOf == of;
        if (current) return;
        if (back != null && back.PixelSize != pixels && wantedFor < 8)
        {
            back.Dispose(); front?.Dispose();
            back = front = null;
            wantedFor = 0;
            return;
        }
        if (back == null && wantedFor < 8) return;
        back?.Dispose(); front?.Dispose();
        back = Layer(pixels, c => DrawBack(c, p));
        front = Layer(pixels, c => DrawFront(c, p));
        layersOf = of;
    }

    static Bitmap Layer(PixelSize pixels, Action<DrawingContext> draw)
    {
        var layer = new RenderTargetBitmap(pixels);
        using (var ctx = layer.CreateDrawingContext())
        using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
        using (ctx.PushTransform(Matrix.CreateScale(pixels.Width / StageWidth, pixels.Height / StageHeight)))
            draw(ctx);
        return PlaySprites.Freeze(layer);
    }

    void DrawBack(DrawingContext ctx, PlayViewModel p)
    {
        ctx.FillRectangle(Backdrop, new Rect(0, 0, StageWidth, StageHeight));
        for (int i = 0; i < 4; i++) Image(ctx, "background/header", 496 * i, 0, 496, 276);
        Image(ctx, "background/festival", 0, 540, 1920, 540);
        Image(ctx, "background/festival_lights", 0, 540, 1920, 540);
        Image(ctx, "background/footer", 0, 984, 1920, 96);
        Outlined(ctx, p.Title, 1878, 27, 72, 57);
        Outlined(ctx, p.Subtitle.TrimStart('-', '+'), 1878, 102, 48, 33);
    }

    // What lies over the notes and under the drum.
    void DrawFront(DrawingContext ctx, PlayViewModel p)
    {
        Image(ctx, "lane/1p_lane_cover", 0, 276, 498, 264);
        int difficulty = (int)(SongInfo.DifficultyOf(p.Chart.Course) ?? Difficulty.Oni);
        Frame(ctx, "lane/lane_difficulty", new Rect(0, 96 * Math.Min(difficulty, 6), 132, 96), new Rect(29, 346, 132, 96));
        Sliced(ctx, "judge_counter/panel", 24, new Rect(36, 64, 316.8, 190.8), 0.9);
        foreach (var (top, label, source) in JudgeRows)
        {
            Sliced(ctx, "judge_counter/bar", 17, new Rect(59.4, top, 156.6, 30.6), 0.9);
            Frame(ctx, "judge_counter/labels", source, label);
        }
    }

    static readonly (double Top, Rect Label, Rect Source)[] JudgeRows =
    {
        (80.2, new Rect(124.848, 80.506, 25.704, 29.988), new Rect(13, 300 - 250 - 49, 42, 49)),
        (120.7, new Rect(124.542, 121.618, 26.316, 28.764), new Rect(13, 300 - 187 - 47, 43, 47)),
        (161.2, new Rect(114.444, 162.118, 46.512, 28.764), new Rect(12, 300 - 124 - 47, 76, 47)),
        (201.7, new Rect(100.062, 202.618, 75.276, 28.764), new Rect(8, 300 - 63 - 47, 123, 47)),
    };

    static readonly Rect Stage = new(0, 0, StageWidth, StageHeight);

    void Draw(DrawingContext ctx, PlayViewModel p)
    {
        var session = p.Session;
        var chart = p.Chart;
        double now = p.Time;
        if (!ReferenceEquals(chart, clocksOf)) { clocks = new ScrollClockTimeline(chart); clocksOf = chart; }
        var shift = JudgeShift.At(chart.JudgeMoves, now);
        double jx = JudgeX + shift.X, jy = JudgeY + shift.Y;

        // ---- The stage art.
        if (back != null) ctx.DrawImage(back, Stage);
        else DrawBack(ctx, p);

        // ---- The lane: its background under #LANE_ALPHA with the route's and go-go time's tints.
        var gogo = session.GogoStateAt(now);
        double laneAlpha = Math.Clamp(session.LaneAlphaAt(now), 0, 1);
        using (laneAlpha < 1 ? ctx.PushClip(new Rect(496, LaneTop, 1424, 264)) : default)
        using (laneAlpha < 1 ? ctx.PushOpacity(laneAlpha) : default)
        {
            Image(ctx, "lane/lane_background", LaneLeft, LaneTop, 1422, 264);
            if (chart.Branches.Count > 0 && p.LaneRoute != BranchRoute.Normal)
            {
                string tint = p.LaneRoute == BranchRoute.Expert ? "branch/expert_bg" : "branch/master_bg";
                double shown = Ramp(now - p.LaneRouteAt, 0.1, 0.1665);
                if (shown >= 1) ctx.DrawImage(PlaySprites.Faded(tint, 0.5), new Rect(LaneLeft, 283, 1422, 200));
                else Image(ctx, tint, LaneLeft, 283, 1422, 200, 0.5 * shown);
            }
            if (gogo.Active)
            {
                double h = 195 * Math.Clamp((now - gogo.StartedAt) / 0.133, 0, 1);
                ctx.DrawImage(PlaySprites.Faded("gogo_time/lane_effect", 0.5), new Rect(496, 385.5 - h / 2, 1424, h));
            }
        }
        if (chart.Branches.Count > 0) DrawRoute(ctx, p, now);
        Image(ctx, "lane/lane_hit_circle", jx - 108, jy - 108, 216, 216);
        if (gogo.Active)
        {
            // Go-go time's fire at the judge: it bursts in large and settles.
            double age = now - gogo.StartedAt;
            double scale = age < 0.0667 ? 3.474 : 3.474 + (1.351 - 3.474) * Math.Clamp((age - 0.0667) / (0.333 - 0.0667), 0, 1);
            int frame = (int)Math.Floor(Math.Max(0, age) / 0.0333) % 8;
            double size = 440 * scale;
            ctx.DrawImage(PlaySprites.Faded("gogo_time/fire", 0.699), new Rect(440 * (frame % 4), 440 * (frame / 4), 440, 440), new Rect(jx + 8 - size / 2, jy - 1 - size / 2, size, size));
        }
        // The face lit at the judge by a 良 or 可, under the notes.
        double struck = now - p.StruckAt;
        bool lit = p.Struck is Judgment.Good or Judgment.Ok && struck >= 0;
        string effect = (p.Struck == Judgment.Ok ? "ok" : "good") + (p.StruckBig ? "_big" : "");
        if (lit && struck < 0.35)
            Image(ctx, "hit_effect/hit_effect_" + effect, jx - 108, jy - 108, 216, 216,
                struck < 0.0667 ? 0.5 + 0.5 * struck / 0.0667 : struck < 0.2833 ? 1 : 1 - 0.5 * (struck - 0.2833) / 0.0667);

        // ---- The soul gauge: over the lane, under the notes.
        DrawGauge(ctx, p, now);

        // ---- Bar lines, notes, and the notes' text over them all.
        DrawNotes(ctx, p, now, jx, jy);

        // ---- Over the lane: the player's panel.
        if (front != null) ctx.DrawImage(front, Stage);
        else DrawFront(ctx, p);
        if (lit && struck < 0.2)
        {
            int frame = struck < 0.0545 ? 0 : struck < 0.0727 ? 1 : struck < 0.0909 ? 2 : 3;
            Frame(ctx, "hit_effect/outer_" + effect, new Rect(336 * frame, 0, 336, 336), new Rect(jx - 168, jy - 168, 336, 336),
                struck < 0.1667 ? 1 : 1 - (struck - 0.1667) / 0.0333);
        }
        Image(ctx, "lane/drum", 262, 258, 276, 304);
        string[] flashes = { "lane/drum_don_l", "lane/drum_don_r", "lane/drum_kat_l", "lane/drum_kat_r" };
        for (int i = 0; i < 4; i++)
            if (now - p.FlashedAt[i] is >= 0 and < 0.12) Image(ctx, flashes[i], 262, 258, 276, 304);
        DrawCombo(ctx, session.Combo, now - p.ComboAt);
        // 良／可／不可 over the judge, for a press: it stands a quarter of a second, stretched as it comes.
        double said = now - p.JudgedAt;
        if (p.Judged is Judgment.Good or Judgment.Ok or Judgment.Bad && said is >= 0 and < 0.25)
        {
            double h = 80 + Stretch(said) * 80 / 80;
            Image(ctx, p.Judged == Judgment.Good ? "hit_effect/judge_good" : p.Judged == Judgment.Ok ? "hit_effect/judge_ok" : "hit_effect/judge_bad",
                jx - 72, jy - 90 - h, 144, h);
        }
        Image(ctx, "lane/lane_score_cover", 0, 288, 304, 52);
        // The score, right-aligned, stretched by each hit that adds to it.
        double scoreStretch = p.Struck != Judgment.Bad ? Stretch(now - p.StruckAt) : 0;
        string score = session.Score.ToString(CultureInfo.InvariantCulture);
        for (int i = 0; i < score.Length; i++)
            Frame(ctx, "lane/score_number", new Rect(56 * (score[i] - '0'), 0, 56, 64), new Rect(255 - 30 * score.Length + 30 * i, 281.5 - scoreStretch, 56, 64 + scoreStretch));
        DrawRollCounter(ctx, p, now);
        if (p.Auto) Outlined(ctx, "AUTO", 300, 352, 34, 26);

        // ---- The HUD.
        DrawJudgeCounter(ctx, session);
        DrawBalloon(ctx, p, now, shift.X, shift.Y);
        DrawFlying(ctx, p, now);
    }

    // The route's label at the lane's right end, and the level-up or level-down badge of a change.
    static void DrawRoute(DrawingContext ctx, PlayViewModel p, double now)
    {
        double age = now - p.LaneRouteAt;
        string name = p.LaneRoute switch { BranchRoute.Expert => "branch/expert", BranchRoute.Master => "branch/master", _ => "branch/normal" };
        Image(ctx, name, 1606.5, 340.5, 264, 84, double.IsInfinity(age) ? 1 : Ramp(age, 0.1, 0.233));
        if (!(age >= 0 && age < 1.392)) return;
        double scale = age < 0.116 ? 1 + 0.2 * age / 0.116 : age < 0.232 ? 1.2 - 0.2 * (age - 0.116) / 0.116 : 1;
        double alpha = age < 0.116 ? age / 0.116 : age < 1.276 ? 1 : 1 - (age - 1.276) / 0.116;
        Image(ctx, p.LaneRoute > p.LaneRouteBefore ? "branch/level_up" : "branch/level_down", 376.5 - 132 * scale, 234 - 42 * scale, 264 * scale, 84 * scale, alpha);
    }

    // SoulGaugeView: fifty cells of 21 from x 738; red up to the clear cell, gold past it, the
    // newest cell fading in, the rainbow and the soul's fire when it is full.
    static void DrawGauge(DrawingContext ctx, PlayViewModel p, double now)
    {
        var session = p.Session;
        double threshold = session.ClearThreshold;
        string style = threshold < 0.7 ? "easy" : threshold < 0.8 ? "normal" : "hard";
        int clearCell = (int)Math.Round(threshold * 50);
        int filled = Math.Clamp((int)Math.Floor(session.GaugePoints * 50 / SoulGauge.MaximumPoints + 1e-9), 0, 50);
        int before = Math.Clamp((int)Math.Floor(p.GaugeBefore * 50 / SoulGauge.MaximumPoints + 1e-9), 0, 50);
        double fade = now - p.GaugeChangedAt;
        bool fading = filled > before && fade is >= 0 and < 0.45;
        int solid = fading ? filled - 1 : filled;
        bool clear = session.IsClear, full = session.IsGaugeFull;

        Image(ctx, "gauge/border_" + style, 720, 204, 1200, 84);
        Image(ctx, "gauge/1p_unfilled_" + style, 720, 204, 1200, 84);
        int red = Math.Min(solid, clearCell - 1);
        if (red > 0) Image(ctx, "gauge/1p_bar", 738, 249, red * 21, 33);
        if (clear) Image(ctx, "gauge/bar_clear_transition", 738 + (clearCell - 1) * 21, 213, 24, 72);
        int gold = Math.Max(0, solid - clearCell);
        if (gold > 0)
        {
            Image(ctx, "gauge/bar_clear_top", 738 + clearCell * 21, 216, gold * 21, 34);
            Image(ctx, "gauge/bar_clear_bottom", 738 + clearCell * 21, 250, gold * 21, 32);
        }
        if (fading)
        {
            double x = 738 + (filled - 1) * 21, alpha = fade / 0.45;
            if (filled < clearCell) Image(ctx, "gauge/1p_bar_fade", x, 249, 21, 33, alpha);
            else Image(ctx, filled == clearCell ? "gauge/bar_clear_transition_fade" : "gauge/bar_clear_fade", x, 216, 21, 66, alpha);
        }
        if (full)
        {
            // Eight frames one under another, each fading into the next.
            double t = Math.Max(0, now) / 0.075;
            int frame = (int)Math.Floor(t) % 8;
            Frame(ctx, "gauge/rainbow_" + style, new Rect(0, 78 * frame, 1057, 78), new Rect(731, 209, 1057, 78));
            Frame(ctx, "gauge/rainbow_" + style, new Rect(0, 78 * ((frame + 1) % 8), 1057, 78), new Rect(731, 209, 1057, 78), t - Math.Floor(t));
        }
        ctx.DrawImage(PlaySprites.Faded("gauge/overlay_" + style, 0.15), new Rect(720, 204, 1200, 84));
        Image(ctx, clear ? "gauge/clear_ja" : "gauge/clear_dark_ja", style == "easy" ? 1350 : style == "normal" ? 1455 : 1560, 202, 200, 70);
        if (full) Image(ctx, "gauge/tamashii_fire/" + (int)Math.Floor(Math.Max(0, now) / 0.05) % 8, 1757, 83, 240, 240);
        Image(ctx, clear ? "gauge/tamashii" : "gauge/tamashii_dark", 1790, 202, 88, 88);
        if (full && Math.Max(0, now) % 0.2 < 0.1) ctx.DrawImage(PlaySprites.Faded("gauge/tamashii_overlay", 0.5), new Rect(1790, 202, 88, 88));
    }

    // NoteLaneRenderer: only a hit takes a note off the lane; a missed note, a drumroll and a mine
    // left alone scroll on under the player's panel. Later notes under earlier ones.
    void DrawNotes(DrawingContext ctx, PlayViewModel p, double now, double jx, double jy)
    {
        var session = p.Session;
        var chart = p.Chart;
        const double travel = NoteScroll.MeasureWidth;
        foreach (var bar in chart.Bars)
        {
            if (!bar.Display || !session.IsShown(bar)) continue;
            var d = NoteScroll.Distance(bar, now, clocks!, travel);
            double x = jx + d.X, y = jy + d.Y;
            if (x < 300 || x > StageWidth + 6) continue;
            double w = bar.IsBranchStart ? 6 : 3;
            ctx.FillRectangle(bar.IsBranchStart ? BranchLine : BarLine, new Rect(x - w / 2, y - 96, w, 200));
        }
        int expression = session.NoteExpressionFrame(now);
        int balloon = BalloonShown(p, now) ? p.Balloon : -1;
        texts.Clear();
        for (int i = chart.Notes.Count - 1; i >= 0; i--)
        {
            var n = chart.Notes[i];
            if (n.IsAdLib || !n.Display || !NoteScroll.Appeared(n, now) || p.HidesSkipped && session.Skipped[i] || !session.IsShown(n)) continue;
            bool scrollsOn = n.IsDrumroll || n.IsMine && !session.Exploded[i];
            if (!(scrollsOn || session.Missed[i] || !session.Resolved[i])) continue;
            if (session.KusudamaOf(i) is { Hits: > 0 }) continue;
            var d = NoteScroll.Distance(n, now, clocks!, travel);
            double hx = jx + d.X, hy = jy + d.Y;
            if (n.IsBalloon && now >= n.Time) (hx, hy) = (jx, jy);
            if (hx - 96 > StageWidth) continue;
            double length = 0, rise = 0;
            if (n.IsDrumroll)
            {
                var t = NoteScroll.TailDistance(n, now, clocks!, travel);
                length = jx + t.X - hx;
                rise = jy + t.Y - hy;
            }
            if (Math.Max(hx, hx + length) < 300) continue;
            var head = new Point(hx, hy);
            if (n.IsDrumroll) LaneView.DrawRoll(ctx, n.Kind == NoteKind.BigRoll, head, length, rise, 1);
            LaneView.DrawNote(ctx, n.Kind, head, 1, balloonTail: balloon != i, expression);
            if (balloon != i && n.Moji >= 0) texts.Add((n, new Point(hx, hy + 123), length));
        }
        foreach (var (n, at, length) in texts) LaneView.DrawMoji(ctx, n, at, length, 1);
    }

    // ComboView: from 10, white; from 50, silver; from 100, gold. The digits stand on their feet
    // and stretch up at each new one.
    static void DrawCombo(DrawingContext ctx, int combo, double age)
    {
        if (combo < 10) return;
        string sheet = combo >= 100 ? "combo/counter_gold" : combo >= 50 ? "combo/counter_100" : "combo/counter";
        string digits = combo.ToString(CultureInfo.InvariantCulture);
        double stretch = Stretch(age);
        for (int i = 0; i < digits.Length; i++)
            Frame(ctx, sheet, new Rect(64 * (digits[i] - '0'), 0, 64, 80), new Rect(401 - 26 * digits.Length + 52 * i - 6, 334 - stretch, 64, 80 + stretch));
        Image(ctx, combo >= 100 ? "combo/combo_100_ja" : "combo/combo_ja", 320, 412, 160, 40);
    }

    // DrumrollCounterView: the bubble over the drum with the hits so far; it stays two and a half
    // seconds after the last hit.
    static void DrawRollCounter(DrawingContext ctx, PlayViewModel p, double now)
    {
        double age = now - p.RollHitAt;
        if (p.Roll < 0 || !(age >= 0 && age < 2.698)) return;
        double alpha = age < 2.532 ? 1 : 1 - (age - 2.532) / 0.166;
        Image(ctx, "drumroll_counter/bubble", 296, 5, 464, 272, alpha);
        string digits = p.RollHits.ToString(CultureInfo.InvariantCulture);
        double stretch = Stretch(age);
        for (int i = 0; i < digits.Length; i++)
            Frame(ctx, "drumroll_counter/counter", new Rect(96 * (digits[i] - '0'), 0, 96, 112), new Rect(520 - 40 * digits.Length + 80 * i, 61 - stretch, 96, 112 + stretch), alpha);
    }

    static bool BalloonShown(PlayViewModel p, double now)
    {
        if (p.Balloon < 0) return false;
        var note = p.Chart.Notes[p.Balloon];
        return now >= note.Time && (!p.Session.Resolved[p.Balloon] ? now <= note.EndTime : p.BalloonLeft == 0 && now - p.BalloonPoppedAt < 0.166);
    }

    // BalloonCounterView: the balloon swelling beside the judge and the hits left in its bubble.
    static void DrawBalloon(DrawingContext ctx, PlayViewModel p, double now, double dx, double dy)
    {
        if (!BalloonShown(p, now)) return;
        var note = p.Chart.Notes[p.Balloon];
        bool popped = p.BalloonLeft == 0;
        double alpha = popped ? Math.Clamp(1 - (now - p.BalloonPoppedAt) / 0.166, 0, 1) : 1;
        int total = Math.Max(1, p.Session.KusudamaOf(p.Balloon)?.Required ?? note.BalloonHits);
        int[] swelling = { 0, 2, 3, 4, 5, 6 };
        int frame = popped ? 7 : swelling[Math.Min(5, (total - p.BalloonLeft) * 6 / total)];
        Frame(ctx, "balloon/pop", new Rect(384 * (frame % 3), 360 * (frame / 3), 384, 360), new Rect(645 + dx, 195 + dy, 384, 360), alpha);
        Image(ctx, "balloon/bubble", 589 + dx, 23 + dy, 280, 256, alpha);
        string digits = p.BalloonLeft.ToString(CultureInfo.InvariantCulture);
        double stretch = Stretch(now - p.BalloonHitAt) * 90 / 112;
        for (int i = 0; i < digits.Length; i++)
            Frame(ctx, "drumroll_counter/counter", new Rect(96 * (digits[i] - '0'), 0, 96, 112), new Rect(723 + dx - 32 * digits.Length + 64 * i, 96 + dy - stretch, 77, 90 + stretch), alpha);
    }

    // JudgeCounterView: the counts of the four rows (the panel itself is in the front layer).
    static void DrawJudgeCounter(DrawingContext ctx, PlaySession session)
    {
        Span<int> counts = stackalloc int[] { session.Good, session.Ok, session.Bad, session.Rolls };
        for (int row = 0; row < 4; row++)
        {
            double top = JudgeRows[row].Top;
            int count = counts[row], digits = 1;
            for (int rest = count; rest >= 10; rest /= 10) digits++;
            for (int i = digits - 1; i >= 0; i--, count /= 10)
                Frame(ctx, "lane/score_number", new Rect(56 * (count % 10), 0, 56, 64), new Rect(276.075 - 16.03 * (digits - 1 - i), top - 1.8, 29.925, 34.2));
        }
    }

    // NoteArcView: a hit note flies from the judge to the soul along a circle in half a second,
    // and bursts there (GaugeHitEffect).
    static void DrawFlying(DrawingContext ctx, PlayViewModel p, double now)
    {
        const double pivotX = 1269.22, pivotY = 691.14, radius = 719.14, from = -154.894, to = -38.244;
        foreach (var f in p.Flying)
        {
            double age = now - f.At;
            if (age < 0) continue;
            if (age < 0.5)
            {
                double t = age / 0.5, angle = (from + (to - from) * t) * Math.PI / 180;
                LaneView.DrawNote(ctx, f.Kind, new Point(pivotX + radius * Math.Cos(angle) + f.ShiftX * (1 - t), pivotY + radius * Math.Sin(angle) + f.ShiftY * (1 - t)), 1, balloonTail: false);
                continue;
            }
            double landed = age - 0.5;
            if (landed >= 0.383) continue;
            int frame = landed < 0.033 ? 0 : landed < 0.067 ? 1 : 2;
            double size = landed < 0.1167 ? 185.6 : 185.6 + (348 - 185.6) * (landed - 0.1167) / (0.3827 - 0.1167);
            double alpha = landed < 0.3 ? 1 : 1 - (landed - 0.3) / 0.083;
            var tint = landed < 0.1167 ? Color.FromRgb(253, 249, 0) : landed < 0.1547 ? Color.FromRgb(255, 161, 0) : Color.FromRgb(230, 41, 55);
            var at = new Rect(SoulX - size / 2, SoulY - size / 2, size, size);
            // The burst is a white shape, coloured as it grows.
            var burst = PlaySprites.Tinted("gauge/hit_effect", new Rect(232 * frame, 0, 232, 232), tint);
            using (alpha < 1 ? ctx.PushClip(at.Inflate(4)) : default)
            using (alpha < 1 ? ctx.PushOpacity(alpha) : default)
            {
                ctx.DrawImage(burst, at);
                LaneView.DrawNote(ctx, f.Kind, new Point(SoulX, SoulY), 1, balloonTail: false);
            }
        }
    }

    // TextStretch.anim: how many pixels taller a counter stands, by the time since it changed.
    static double Stretch(double seconds)
    {
        double ms = seconds * 1000;
        if (!(ms >= 0) || ms >= 166) return 0;
        if (ms < 50) return 2 + 0.2 * ms;
        return 10 - 2 * Math.Floor((ms - 50) / 16.6);
    }

    // 0 before `from`, 1 after `to` (seconds), linear between.
    static double Ramp(double age, double from, double to) => double.IsNaN(age) ? 1 : Math.Clamp((age - from) / (to - from), 0, 1);

    static void Image(DrawingContext ctx, string name, double x, double y, double w, double h, double opacity = 1)
    {
        if (opacity <= 0 || w <= 0 || h <= 0) return;
        var bitmap = PlaySprites.Get(name);
        // (An opacity is an off-screen layer as large as what may be drawn: the clip keeps it small.)
        using (opacity < 1 ? ctx.PushClip(new Rect(x, y, w, h)) : default)
        using (opacity < 1 ? ctx.PushOpacity(opacity) : default)
            ctx.DrawImage(bitmap, new Rect(bitmap.Size), new Rect(x, y, w, h));
    }

    // A frame of a sheet, its rectangle measured from the sheet's top left.
    static void Frame(DrawingContext ctx, string name, Rect source, Rect at, double opacity = 1)
    {
        if (opacity <= 0 || at.Width <= 0 || at.Height <= 0) return;
        using (opacity < 1 ? ctx.PushClip(at) : default)
        using (opacity < 1 ? ctx.PushOpacity(opacity) : default)
            ctx.DrawImage(PlaySprites.Get(name), source, at);
    }

    // A nine-sliced image: its corners keep their size (times `scale`), its edges and middle stretch.
    static void Sliced(DrawingContext ctx, string name, double border, Rect at, double scale)
    {
        var bitmap = PlaySprites.Get(name);
        double w = bitmap.Size.Width, h = bitmap.Size.Height, b = border * scale;
        double[] sx = { 0, border, w - border, w }, sy = { 0, border, h - border, h };
        double[] dx = { at.X, at.X + b, at.Right - b, at.Right }, dy = { at.Y, at.Y + b, at.Bottom - b, at.Bottom };
        for (int row = 0; row < 3; row++)
            for (int column = 0; column < 3; column++)
            {
                var source = new Rect(sx[column], sy[row], sx[column + 1] - sx[column], sy[row + 1] - sy[row]);
                var target = new Rect(dx[column], dy[row], dx[column + 1] - dx[column], dy[row + 1] - dy[row]);
                if (source.Width > 0 && source.Height > 0 && target.Width > 0 && target.Height > 0) ctx.DrawImage(bitmap, source, target);
            }
    }

    // White text with a black outline, right-aligned to `right` and centred in a row `height` high.
    void Outlined(DrawingContext ctx, string text, double right, double top, double height, double size)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var white = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, heavy, size, Brushes.White);
        var origin = new Point(right - white.Width, top + (height - white.Height) / 2);
        var outline = white.BuildGeometry(origin);
        if (outline != null) ctx.DrawGeometry(null, new Pen(Brushes.Black, size / 6) { LineJoin = PenLineJoin.Round }, outline);
        ctx.DrawText(white, origin);
    }
}
