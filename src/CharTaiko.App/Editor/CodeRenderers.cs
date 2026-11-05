using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using CharTaiko.Core.Languages;

namespace CharTaiko.App.Editor;

static class Spans
{
    // A 1-based span as a document segment; length 0 is the line's text.
    public static TextSegment? Segment(TextDocument doc, SourceSpan span)
    {
        if (span.IsEmpty || span.Line > doc.LineCount) return null;
        var line = doc.GetLineByNumber(span.Line);
        if (span.Length <= 0) return new TextSegment { StartOffset = line.Offset, Length = Math.Max(1, line.Length) };
        int start = line.Offset + Math.Clamp(span.Column - 1, 0, line.Length);
        return new TextSegment { StartOffset = start, Length = Math.Clamp(span.Length, 1, line.EndOffset - start + 1) };
    }
}

// Wavy underlines under the analysis's diagnostics.
sealed class DiagnosticRenderer : IBackgroundRenderer
{
    static readonly IPen ErrorPen = new Pen(new SolidColorBrush(Color.Parse("#FF5A5A")), 1.2);
    static readonly IPen WarningPen = new Pen(new SolidColorBrush(Color.Parse("#E5B53A")), 1.2);
    static readonly IPen InfoPen = new Pen(new SolidColorBrush(Color.Parse("#6CA8E8")), 1);

    public IReadOnlyList<ChartDiagnostic> Diagnostics { get; set; } = Array.Empty<ChartDiagnostic>();
    public KnownLayer Layer => KnownLayer.Selection;

    public void Draw(TextView textView, DrawingContext ctx)
    {
        var doc = textView.Document;
        if (doc == null || !textView.VisualLinesValid) return;
        foreach (var d in Diagnostics)
        {
            if (Spans.Segment(doc, d.Source) is not { } segment) continue;
            var pen = d.Severity switch { DiagnosticSeverity.Error => ErrorPen, DiagnosticSeverity.Warning => WarningPen, _ => InfoPen };
            foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
            {
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    double y = rect.Bottom - 1;
                    g.BeginFigure(new Point(rect.Left, y), false);
                    for (double x = rect.Left; x < rect.Right; x += 4)
                    {
                        g.LineTo(new Point(x + 2, y - 2));
                        g.LineTo(new Point(Math.Min(x + 4, rect.Right), y));
                    }
                    g.EndFigure(false);
                }
                ctx.DrawGeometry(null, pen, geometry);
            }
        }
    }
}

// The slot at the playhead, boxed.
sealed class PlayheadRenderer : IBackgroundRenderer
{
    static readonly IBrush Fill = new SolidColorBrush(Color.FromArgb(70, 255, 82, 82));
    static readonly IPen Rim = new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 82, 82)), 1);

    public SourceSpan Span { get; set; }
    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext ctx)
    {
        if (textView.Document is not { } doc || !textView.VisualLinesValid || Spans.Segment(doc, Span) is not { } segment) return;
        foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
            ctx.DrawRectangle(Fill, Rim, rect.Inflate(new Thickness(1, 0)), 2, 2);
    }
}
