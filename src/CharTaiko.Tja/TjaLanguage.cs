using CharTaiko.Core.Languages;
using OurTaiko;

namespace CharTaiko.Tja;

// TJA through TJAParser's editor API (Documentation~/Editor-API.md): a TjaDocument per version of
// the text, so blocks an edit did not touch are not read again; diagnostics of every block with
// the editor checks; the chosen block's analysis with its source map; and the library's
// completion. Measures' slots come from the document's syntax tokens.
public sealed class TjaLanguage : IChartLanguage
{
    public string Id => "tja";
    public string DisplayName => "TJA";
    public string FileExtension => ".tja";
    public IChartEditing Editing { get; } = new TjaEditing();

    // The last document, so the next text becomes its next version.
    TjaDocument? document;

    public string MessageLanguage { get; set; } = "en";

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
        => TjaLines.Read(text).Where(l => l.Kind == TjaLineKind.Header && l.Key == "WAVE").Select(l => l.Value).LastOrDefault() is { Length: > 0 } wave ? wave : null;

    public Stream? OpenHighlighting() => typeof(TjaLanguage).Assembly.GetManifestResourceStream("CharTaiko.Tja.Tja.xshd");

    TjaDocument Document(string text)
    {
        var doc = document;
        if (doc == null) doc = new TjaDocument(text, 0);
        else if (doc.Text != text) doc = doc.WithText(text, doc.Version + 1);
        return document = doc;
    }

    public CompletionResult Complete(string text, int line, int column)
    {
        var doc = Document(text);
        int offset = doc.Lines.Offset(line, column - 1);
        var items = TjaCompletion.At(doc, offset);
        if (items.Count == 0) return CompletionResult.None;
        int start = items.Min(i => i.Replace.Start);
        var (startLine, startColumn) = doc.Lines.Position(start);
        if (startLine != line) return CompletionResult.None;
        return new CompletionResult(startColumn + 1,
            items.Select(i => new CompletionItem(i.Insert, i.Detail ?? "", Label: i.Label)).ToList());
    }

    public static string BlockName(string course, string side, int level)
    {
        string name = SongInfo.DifficultyOf(course) is { } d ? SongInfo.CourseName(d) : course;
        if (side != "single") name += " " + side.ToUpperInvariant();
        return level > 0 ? $"{name} ★{level}" : name;
    }

    public ChartAnalysis Analyze(string text, int block)
    {
        if (string.IsNullOrWhiteSpace(text)) return ChartAnalysis.Empty;
        var doc = Document(text);
        var lines = doc.Lines;
        SourceSpan Span(TextSpan s)
        {
            var (l, c) = lines.Position(s.Start);
            return new SourceSpan(l, c + 1, s.Length);
        }

        var diagnostics = doc.Diagnostics()
            .Select(d => new ChartDiagnostic(Span(d.Span), $"{d.Code}: {d.MessageIn(MessageLanguage)}", d.Severity switch
            {
                TjaSeverity.Error => DiagnosticSeverity.Error,
                TjaSeverity.Warning => DiagnosticSeverity.Warning,
                _ => DiagnosticSeverity.Information,
            }, d.Code, d))
            .OrderBy(d => d.Source.Line).ThenBy(d => d.Source.Column).ToList();

        // Stars and the title from SongInfo, which refuses some files the document still reads.
        SongInfo? info = null;
        try { info = SongInfo.Read(text); } catch (FormatException) { }
        var blocks = doc.Blocks.Select((b, i) =>
        {
            var listed = info?.Blocks.FirstOrDefault(x => x.Line == b.Line);
            return new ChartBlock(i, BlockName(b.Course, b.Side, listed?.Level ?? 0), b.Course, b.Side, listed?.Level ?? 0, b.Line, listed?.IsBranching ?? false);
        }).ToList();
        if (blocks.Count == 0)
            return new ChartAnalysis
            {
                Text = text, Title = info?.Title ?? "", Audio = AudioFileOf(text),
                Diagnostics = diagnostics.Prepend(new ChartDiagnostic(new SourceSpan(1, 1, 0), "No chart yet: add COURSE: and a #START … #END block.", DiagnosticSeverity.Information)).ToList(),
            };

        int chosen = block >= 0 && block < blocks.Count ? block : 0;
        var analysis = doc.Analyze(blocks[chosen].Course, blocks[chosen].Side);
        var map = analysis.SourceMap;
        var measures = analysis.Chart == null ? new List<MeasureView>() : Measures(doc, analysis, Span);

        return new ChartAnalysis
        {
            Text = text, Blocks = blocks, Block = chosen, Chart = analysis.Chart, HasErrors = analysis.HasErrors,
            Measures = measures, Diagnostics = diagnostics, Title = info?.Title ?? "", Audio = AudioFileOf(text),
            SourceOf = n => map.TryGetSpan(n, out var s) ? Span(s) : default,
            TailOf = n => map.TryGetTailSpan(n, out var s) ? Span(s) : default,
            EventOf = e => map.TryGetSpan(e, out var s) ? Span(s) : default,
            StartLine = lines.Line(analysis.StartSpan.Start), EndLine = lines.Line(analysis.EndSpan.Start),
        };
    }

    // ---- Editor features, from the library (TjaHover, TjaSignatureHelp, TjaFolding, TjaSummary,
    // TjaQuickFix, TjaRefactor, TjaFormat).

    static TextEdit Edit(TjaDocument doc, TjaTextEdit e)
    {
        var (line, column) = doc.Lines.Position(e.Span.Start);
        return new TextEdit(line, column + 1, e.Span.Length, e.NewText);
    }

    static CodeAction? Action(TjaDocument doc, TjaCodeAction? a, bool quickFix = false)
        => a == null || a.Edits.Count == 0 ? null : new CodeAction(a.Title, a.Edits.Select(e => Edit(doc, e)).ToList(), quickFix);

    int OffsetOf(TjaDocument doc, int line, int column) => doc.Lines.Offset(line, column - 1);

    public HoverInfo? Hover(string text, int line, int column)
    {
        var doc = Document(text);
        if (TjaHover.At(doc, OffsetOf(doc, line, column)) is not { } h || string.IsNullOrWhiteSpace(h.Markdown)) return null;
        var (l, c) = doc.Lines.Position(h.Span.Start);
        return new HoverInfo(new SourceSpan(l, c + 1, h.Span.Length), h.Markdown);
    }

    public SignatureInfo? Signature(string text, int line, int column)
    {
        var doc = Document(text);
        if (TjaSignatureHelp.At(doc, OffsetOf(doc, line, column)) is not { } s) return null;
        return new SignatureInfo(s.Label, s.Parameters, s.Active, s.Keyword?.Summary);
    }

    public IReadOnlyList<FoldRange> Folds(string text)
        => TjaFolding.Ranges(Document(text)).Select(r => new FoldRange(r.StartLine, r.EndLine, r.Kind.ToString())).ToList();

    public IReadOnlyList<InlayHint> Hints(string text, int fromLine, int toLine)
    {
        var doc = Document(text);
        int start = doc.Lines.LineStart(fromLine), end = toLine >= doc.Lines.LineCount ? doc.Text.Length : doc.Lines.LineStart(toLine + 1);
        return TjaSummary.Hints(doc, TextSpan.FromBounds(start, end)).Select(h =>
        {
            var (l, c) = doc.Lines.Position(h.Offset);
            return new InlayHint(l, c + 1, h.Label);
        }).ToList();
    }

    public IReadOnlyList<CodeAction> Actions(ChartAnalysis analysis, int line, int column)
    {
        var doc = Document(analysis.Text);
        int offset = OffsetOf(doc, line, column);
        var actions = new List<CodeAction>();
        foreach (var d in analysis.Diagnostics)
            if (d.Tag is TjaDiagnostic diagnostic && d.Source.Line == line)
                foreach (var fix in TjaQuickFix.For(doc, diagnostic))
                    if (Action(doc, fix, quickFix: true) is { } a) actions.Add(a);
        if (analysis.Block >= 0 && analysis.Block < analysis.Blocks.Count)
        {
            var b = analysis.Blocks[analysis.Block];
            if (Action(doc, TjaRefactor.Split(doc, b.Course, b.Side, offset)) is { } split) actions.Add(split);
            if (Action(doc, TjaRefactor.Join(doc, b.Course, b.Side, offset)) is { } join) actions.Add(join);
            if (Action(doc, TjaQuickFix.InlineBalloons(doc, b.Course, b.Side)) is { } inline) actions.Add(inline);
        }
        return actions;
    }

    public CodeAction? Format(string text)
    {
        var doc = Document(text);
        var edits = TjaFormat.Edits(doc);
        return edits.Count == 0 ? null : new CodeAction("Format document", edits.Select(e => Edit(doc, e)).ToList());
    }

    public IReadOnlyList<BlockSummary> Summaries(string text)
    {
        var doc = Document(text);
        var blocks = doc.Blocks;
        return TjaSummary.Blocks(doc).Select(s => new BlockSummary(IndexOf(blocks, s.Block), s.Title, s.Hash)).ToList();
    }

    static int IndexOf(IReadOnlyList<TjaDocumentBlock> blocks, TjaDocumentBlock block)
    {
        for (int i = 0; i < blocks.Count; i++)
            if (blocks[i].Line == block.Line) return i;
        return -1;
    }

    // Each measure with its slots: the note, blank, 8 and unknown tokens in its span (an inline
    // count belongs to the note before it). A slot's time is its note's; blanks lie evenly
    // between the times known around them.
    static List<MeasureView> Measures(TjaDocument doc, TjaAnalysis analysis, Func<TextSpan, SourceSpan> span)
    {
        var tokens = doc.Syntax;
        var map = analysis.SourceMap;
        var result = new List<MeasureView>();
        foreach (var m in analysis.Measures)
        {
            int first = FirstAt(tokens, m.Span.Start);
            var slots = new List<(TextSpan Span, string Token, ChartNote? Note, double? Time)>();
            TextSpan comma = default;
            for (int i = first; i < tokens.Count && tokens[i].Span.Start < m.Span.End; i++)
            {
                var t = tokens[i];
                switch (t.Kind)
                {
                    case TjaSyntaxKind.Note:
                    case TjaSyntaxKind.Blank:
                    case TjaSyntaxKind.RollEnd:
                    case TjaSyntaxKind.Unknown:
                        var note = map.NoteAt(t.Span.Start, out bool tail);
                        double? time = note == null ? null : tail ? note.EndTime : note.Time;
                        slots.Add((t.Span, doc.Text.Substring(t.Span.Start, t.Span.Length), tail ? null : note, time));
                        break;
                    case TjaSyntaxKind.InlineCount when slots.Count > 0:
                        var last = slots[^1];
                        var joined = TextSpan.FromBounds(last.Span.Start, t.Span.End);
                        slots[^1] = (joined, doc.Text.Substring(joined.Start, joined.Length), last.Note, last.Time);
                        break;
                    case TjaSyntaxKind.Comma:
                        comma = t.Span;
                        break;
                }
            }
            var times = SlotTimes(slots.Select(s => s.Time).ToList(), m.StartTime, m.EndTime);
            result.Add(new MeasureView
            {
                Index = m.Index, BranchId = m.BranchId, Route = m.Route, Time = m.StartTime, EndTime = m.EndTime, Bpm = m.Bpm, Length = m.Length,
                Comma = comma.Length > 0 ? span(comma) : default, Tag = m,
                Slots = slots.Select((s, i) => new ChartSlot(span(s.Span), s.Token, times[i], s.Note)).ToList(),
            });
        }
        return result;
    }

    // The first token at or after an offset.
    static int FirstAt(IReadOnlyList<TjaSyntaxToken> tokens, int offset)
    {
        int lo = 0, hi = tokens.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (tokens[mid].Span.End <= offset) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    // Known times kept; the others interpolated by slot between the known ones (the measure's
    // start before the first slot, its end after the last).
    static double[] SlotTimes(List<double?> known, double start, double end)
    {
        int n = known.Count;
        var times = new double[n];
        int prev = -1;
        double prevTime = start;
        for (int i = 0; i <= n; i++)
        {
            if (i < n && known[i] == null) continue;
            double t = i < n ? known[i]!.Value : end;
            for (int k = prev + 1; k < i; k++) times[k] = prevTime + (t - prevTime) * (k - prev) / (double)(i - prev);
            if (i < n) times[i] = t;
            prev = i;
            prevTime = t;
        }
        return times;
    }
}
