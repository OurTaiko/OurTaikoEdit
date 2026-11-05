using System.Text.RegularExpressions;
using CharTaiko.Core.Languages;
using OurTaiko;

namespace CharTaiko.Tja;

// TJA through TJAParser: the same parser the game and the servers use, with its source map for
// positions. Every block is parsed for diagnostics; the chosen one is also the chart to show.
public sealed class TjaLanguage : IChartLanguage
{
    public string Id => "tja";
    public string DisplayName => "TJA";
    public string FileExtension => ".tja";
    public IChartEditing Editing { get; } = new TjaEditing();

    public string CreateTemplate(string title, string? audioFile) => string.Join("\n",
        $"TITLE:{title}",
        "SUBTITLE:--",
        "BPM:120",
        $"WAVE:{audioFile ?? ""}",
        "OFFSET:0",
        "DEMOSTART:0",
        "",
        "COURSE:Oni",
        "LEVEL:8",
        "BALLOON:",
        "",
        "#START",
        "1000100010001000,",
        "1000100010001000,",
        ",",
        ",",
        "#END",
        "");

    public string? AudioFileOf(string text)
    {
        try { var wave = SongInfo.Read(text).Wave; return string.IsNullOrWhiteSpace(wave) ? null : wave; }
        catch (FormatException) { return HeaderValue(text, "WAVE"); }
    }

    static string? HeaderValue(string text, string key)
        => TjaLines.Read(text).Where(l => l.Kind == TjaLineKind.Header && l.Key == key).Select(l => l.Value).LastOrDefault();

    public Stream? OpenHighlighting() => typeof(TjaLanguage).Assembly.GetManifestResourceStream("CharTaiko.Tja.Tja.xshd");

    public CompletionResult Complete(string text, int line, int column) => TjaCompletion.Complete(text, line, column);

    static readonly Regex LineNumber = new(@"\(lines? (\d+)(?: and (\d+))?\)");

    public static string BlockName(TjaBlock b)
    {
        string name = SongInfo.CourseName(b.Difficulty);
        if (b.Side != "single") name += " " + b.Side.ToUpperInvariant();
        return b.Level > 0 ? $"{name} ★{b.Level}" : name;
    }

    public ChartAnalysis Analyze(string text, int block)
    {
        var diagnostics = new List<ChartDiagnostic>();
        SongInfo info;
        try
        {
            if (string.IsNullOrWhiteSpace(text)) return ChartAnalysis.Empty;
            info = SongInfo.Read(text);
        }
        catch (FormatException e)
        {
            // SongInfo names the line in its message: "(line 12)" or "(lines 3 and 12)".
            var m = LineNumber.Match(e.Message);
            int at = m.Success ? int.Parse(m.Groups[m.Groups[2].Success ? 2 : 1].Value) : 1;
            diagnostics.Add(new ChartDiagnostic(new SourceSpan(at, 1, 0), e.Message, DiagnosticSeverity.Error));
            return new ChartAnalysis { Text = text, Diagnostics = diagnostics, Audio = HeaderValue(text, "WAVE") };
        }

        var blocks = info.Blocks.Select((b, i) => new ChartBlock(i, BlockName(b), b.Course, b.Side, b.Level, b.Line, b.IsBranching)).ToList();
        if (blocks.Count == 0)
            return new ChartAnalysis { Text = text, Title = info.Title, Audio = Blank(info.Wave),
                Diagnostics = new[] { new ChartDiagnostic(new SourceSpan(1, 1, 0), "No chart yet: add COURSE: and a #START … #END block.", DiagnosticSeverity.Warning) } };
        int chosen = block >= 0 && block < blocks.Count ? block : 0;

        TaikoChart? chart = null;
        TjaSourceMap? chosenMap = null;
        for (int i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            var map = new TjaSourceMap();
            TaikoChart? parsed = null;
            try { parsed = TjaParser.Parse(text, b.Course, b.Side, map); }
            catch (Exception e) when (e is FormatException or NotSupportedException or IndexOutOfRangeException or ArgumentException)
            {
                var at = map.Error?.Source ?? default;
                diagnostics.Add(new ChartDiagnostic(Span(at, b.StartLine), $"{b.Name}: {e.Message}", DiagnosticSeverity.Error));
            }
            foreach (var w in map.Warnings)
                diagnostics.Add(new ChartDiagnostic(Span(w.Source, b.StartLine), $"{b.Name}: {w.Message}", DiagnosticSeverity.Warning));
            if (i == chosen) { chart = parsed; chosenMap = map; }
        }

        var measures = new List<ChartMeasure>();
        if (chart != null && chosenMap != null)
            foreach (var m in chosenMap.Measures)
                measures.Add(new ChartMeasure
                {
                    Index = m.Index, BranchId = m.BranchId, Route = m.Route, Time = m.Time, EndTime = m.EndTime, Bpm = m.Bpm, Length = m.Length,
                    Comma = Span(m.Comma, 0), Tag = m,
                    Slots = m.Slots.Select(s => new ChartSlot(Span(s.Source, 0), s.Token, s.Time, s.Note)).ToList(),
                });

        return new ChartAnalysis
        {
            Text = text, Blocks = blocks, Block = chosen, Chart = chart, Measures = measures,
            Diagnostics = diagnostics.OrderBy(d => d.Source.Line).ThenBy(d => d.Source.Column).ToList(),
            Title = info.Title, Audio = Blank(info.Wave),
            SourceOf = n => n is TjaSourceNote s ? Span(s.Source, 0) : default,
            TailOf = n => n is TjaSourceNote s ? Span(s.TailSource, 0) : default,
            StartLine = chosenMap?.StartLine ?? blocks[chosen].StartLine, EndLine = chosenMap?.EndLine ?? blocks[chosen].StartLine,
        };
    }

    static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    // A parser span, or the fallback line (whole line) when it has none.
    static SourceSpan Span(TjaSpan s, int fallbackLine)
        => s.IsEmpty ? (fallbackLine > 0 ? new SourceSpan(fallbackLine, 1, 0) : default) : new SourceSpan(s.Line, s.Column, s.Length);
}
