using Avalonia.Media;
using OurTaiko;

namespace OurTaikoEdit.App.Controls;

// Note colours shared by the lane, the timeline and the code highlighting (Tja.xshd).
static class ChartPalette
{
    public static readonly IBrush Don = new SolidColorBrush(Color.Parse("#FF4F2E"));
    public static readonly IBrush Ka = new SolidColorBrush(Color.Parse("#43B9DF"));
    public static readonly IBrush Roll = new SolidColorBrush(Color.Parse("#F5C21B"));
    public static readonly IBrush Balloon = new SolidColorBrush(Color.Parse("#FF8A1C"));
    public static readonly IBrush Kadon = new SolidColorBrush(Color.Parse("#A05CFF"));
    public static readonly IBrush Mine = new SolidColorBrush(Color.Parse("#3C3C44"));
    public static readonly IBrush AdLib = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255));
    public static readonly IBrush Background = new SolidColorBrush(Color.Parse("#16171A"));
    public static readonly IBrush Lane = new SolidColorBrush(Color.Parse("#2A2C31"));
    public static readonly IBrush GogoLane = new SolidColorBrush(Color.Parse("#4A2B22"));
    public static readonly IBrush Text = new SolidColorBrush(Color.Parse("#E8EAED"));
    public static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#9AA0A6"));
    public static readonly IBrush Playhead = new SolidColorBrush(Color.Parse("#FF5252"));
    public static readonly IPen NoteRim = new Pen(Brushes.White, 2);
    public static readonly IPen NoteOutline = new Pen(new SolidColorBrush(Color.Parse("#111111")), 1.5);
    public static readonly IPen Bar = new Pen(new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)), 1.5);
    public static readonly IPen BranchBar = new Pen(new SolidColorBrush(Color.Parse("#F5C21B")), 2);
    public static readonly IPen MineCross = new Pen(new SolidColorBrush(Color.Parse("#FF4F2E")), 2.5);
    public static readonly IPen AdLibRim = new Pen(new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)), 1.5, new DashStyle(new double[] { 2, 2 }, 0));

    public static IBrush Fill(NoteKind kind) => kind switch
    {
        NoteKind.Don or NoteKind.BigDon => Don,
        NoteKind.Ka or NoteKind.BigKa => Ka,
        NoteKind.Roll or NoteKind.BigRoll => Roll,
        NoteKind.Balloon or NoteKind.Kusudama => Balloon,
        NoteKind.Kadon => Kadon,
        NoteKind.Mine => Mine,
        NoteKind.AdLib => AdLib,
        _ => Muted,
    };

    public static bool IsBig(NoteKind kind) => kind is NoteKind.BigDon or NoteKind.BigKa or NoteKind.BigRoll or NoteKind.Kusudama or NoteKind.Kadon;

    // A note's circle (with its markings) at a centre and radius.
    public static void DrawHead(DrawingContext ctx, NoteKind kind, Avalonia.Point c, double r, double opacity = 1)
    {
        using var _ = opacity < 1 ? ctx.PushOpacity(opacity) : default;
        if (kind == NoteKind.AdLib)
        {
            ctx.DrawEllipse(AdLib, AdLibRim, c, r, r);
            return;
        }
        ctx.DrawEllipse(null, NoteOutline, c, r + 1.5, r + 1.5);
        ctx.DrawEllipse(Fill(kind), NoteRim, c, r, r);
        if (kind == NoteKind.Mine)
        {
            double d = r * 0.45;
            ctx.DrawLine(MineCross, new(c.X - d, c.Y - d), new(c.X + d, c.Y + d));
            ctx.DrawLine(MineCross, new(c.X - d, c.Y + d), new(c.X + d, c.Y - d));
        }
        else if (kind == NoteKind.Kusudama)
            ctx.DrawEllipse(null, new Pen(Brushes.White, 1.5), c, r * 0.55, r * 0.55);
    }
}
