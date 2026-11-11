using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using OurTaiko;

namespace OurTaikoEdit.App.Controls;

// A note tool as the timeline draws its notes: the kind's circle (bigger for the big ones), a
// short body behind a roll or a balloon. No kind is the Select tool, a pointer.
public sealed class NoteGlyph : Control
{
    public static readonly StyledProperty<NoteKind?> KindProperty = AvaloniaProperty.Register<NoteGlyph, NoteKind?>(nameof(Kind));
    static readonly Geometry Pointer = Geometry.Parse("M4 2 L4 13 L6.9 10.3 L8.7 14.2 L10.4 13.4 L8.6 9.6 L12.3 9.3 Z");

    static NoteGlyph() => AffectsRender<NoteGlyph>(KindProperty, TextElement.ForegroundProperty);

    public NoteKind? Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(26, 20);

    public override void Render(DrawingContext ctx)
    {
        if (Kind is not { } kind)
        {
            var brush = TextElement.GetForeground(this) ?? Brushes.White;
            using var _ = ctx.PushTransform(Matrix.CreateTranslation(5, 2));
            ctx.DrawGeometry(null, new Pen(brush, 1.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), Pointer);
            return;
        }
        double r = ChartPalette.IsBig(kind) ? 7.5 : 5.5, cy = 10;
        bool isLong = kind is NoteKind.Roll or NoteKind.BigRoll or NoteKind.Balloon or NoteKind.Kusudama;
        double cx = isLong ? r + 2 : 13;
        if (kind is NoteKind.Roll or NoteKind.BigRoll)
            ctx.DrawRectangle(ChartPalette.Fill(kind), ChartPalette.GlyphOutline, new Rect(cx, cy - r * 0.7, 24 - cx, r * 1.4), r * 0.7, r * 0.7);
        else if (isLong)
            ctx.DrawLine(new Pen(ChartPalette.Fill(kind), 2, lineCap: PenLineCap.Round), new Point(cx + r, cy), new Point(23, cy));
        ChartPalette.DrawHead(ctx, kind, new Point(cx, cy), r, rim: 1.5);
    }
}
