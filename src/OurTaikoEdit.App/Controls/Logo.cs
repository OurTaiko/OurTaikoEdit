using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using OurTaiko;

namespace OurTaikoEdit.App.Controls;

// The app's mark: a don with a ka behind it, as the timeline draws its notes.
public sealed class Logo : Control
{
    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<Logo, double>(nameof(Size), 22);

    static Logo()
    {
        AffectsRender<Logo>(SizeProperty);
        AffectsMeasure<Logo>(SizeProperty);
    }

    // The height; the mark is half again as wide.
    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(Size * 1.5, Size);

    public override void Render(DrawingContext ctx)
    {
        double r = Size / 2 - 2, rim = Math.Max(1.5, Size / 14);
        ChartPalette.DrawHead(ctx, NoteKind.Ka, new Point(Size * 1.5 - r - 2, Size / 2), r, rim: rim);
        ChartPalette.DrawHead(ctx, NoteKind.Don, new Point(r + 2, Size / 2), r, rim: rim);
    }
}
