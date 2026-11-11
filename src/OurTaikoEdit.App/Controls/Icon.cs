using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace OurTaikoEdit.App.Controls;

// A line icon drawn on a 16-unit grid (Theme/Icons.axaml) in the text colour around it, so it
// follows its button's states. Filled for the solid ones (play, pause).
public sealed class Icon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty = AvaloniaProperty.Register<Icon, Geometry?>(nameof(Data));
    public static readonly StyledProperty<bool> FilledProperty = AvaloniaProperty.Register<Icon, bool>(nameof(Filled));
    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<Icon, double>(nameof(Size), 16);

    static Icon()
    {
        AffectsRender<Icon>(DataProperty, FilledProperty, SizeProperty, TextElement.ForegroundProperty);
        AffectsMeasure<Icon>(SizeProperty);
    }

    public Geometry? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public bool Filled { get => GetValue(FilledProperty); set => SetValue(FilledProperty, value); }
    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext ctx)
    {
        if (Data is not { } data) return;
        var brush = TextElement.GetForeground(this) ?? Brushes.White;
        double scale = Size / 16;
        using var _ = ctx.PushTransform(Matrix.CreateScale(scale, scale));
        if (Filled) ctx.DrawGeometry(brush, null, data);
        else ctx.DrawGeometry(null, new Pen(brush, 1.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), data);
    }
}
