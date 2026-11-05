using OurTaiko;

namespace CharTaiko.Core.Languages;

// A chart type CharTaiko can edit. Each project names one by Id; the editor asks it to analyse
// the text (blocks, the parsed chart, where every note was written, diagnostics), to complete,
// to highlight and to turn timeline edits into text edits. TJA is the first; another format
// registers its own implementation in ChartLanguages.
public interface IChartLanguage
{
    string Id { get; }
    string DisplayName { get; }
    // With the dot, e.g. ".tja".
    string FileExtension { get; }

    // A new chart's text.
    string CreateTemplate(string title, string? audioFile);

    // The whole text, with the block to parse in full (an index into the result's Blocks; out of
    // range picks the first). Never throws: problems are diagnostics.
    ChartAnalysis Analyze(string text, int block);

    // Candidates at a 1-based line and column of the text.
    CompletionResult Complete(string text, int line, int column);

    // The text for the given chart file's audio, as written in the chart (e.g. TJA WAVE), if any.
    string? AudioFileOf(string text);

    // AvaloniaEdit .xshd highlighting, or null.
    Stream? OpenHighlighting();

    IChartEditing Editing { get; }

    // The editor's language features (LanguageFeatures.cs); a language offers what it has.
    HoverInfo? Hover(string text, int line, int column) => null;
    SignatureInfo? Signature(string text, int line, int column) => null;
    IReadOnlyList<FoldRange> Folds(string text) => Array.Empty<FoldRange>();
    IReadOnlyList<InlayHint> Hints(string text, int fromLine, int toLine) => Array.Empty<InlayHint>();
    // Quick fixes for the diagnostics at a place and refactorings there, in the given block.
    IReadOnlyList<CodeAction> Actions(ChartAnalysis analysis, int line, int column) => Array.Empty<CodeAction>();
    // The whole text formatted, or null when the language cannot.
    CodeAction? Format(string text) => null;
    IReadOnlyList<BlockSummary> Summaries(string text) => Array.Empty<BlockSummary>();
    // Diagnostics' messages in a language ("en", "zh", "ja") where the language has them.
    string MessageLanguage { get => "en"; set { } }
}

// Text is what is inserted; Label what the list shows (Text when null).
public sealed record CompletionItem(string Text, string Description, string? Detail = null, string? Label = null);

// Items replace the text from ReplaceColumn (1-based) up to the caret.
public sealed record CompletionResult(int ReplaceColumn, IReadOnlyList<CompletionItem> Items)
{
    public static readonly CompletionResult None = new(1, Array.Empty<CompletionItem>());
}

// A change to the text, 1-based like the analysis: Length characters at Line:Column become NewText.
public sealed record TextEdit(int Line, int Column, int Length, string NewText);

public sealed record EditResult(IReadOnlyList<TextEdit> Edits, string? Error = null)
{
    public static EditResult Fail(string error) => new(Array.Empty<TextEdit>(), error);
    public bool Ok => Error == null;
}

// A place on a measure's grid: step Step of Steps across the whole measure, seen from a route's
// row of the timeline (a common measure is on every route).
public readonly record struct GridPosition(MeasureView Measure, int Step, int Steps, BranchRoute Route = BranchRoute.Normal)
{
    public double Fraction => Steps > 0 ? (double)Step / Steps : 0;
    public double Time => Measure.TimeAt(Fraction);
}

// Timeline edits as text edits on the analysed text.
public interface IChartEditing
{
    // Puts a note at a grid position, replacing what is there. A roll, balloon or kusudama also
    // needs where it ends.
    EditResult PlaceNote(ChartAnalysis analysis, GridPosition at, NoteKind kind, GridPosition? end = null);

    // Removes a note (and a long note's end).
    EditResult RemoveNote(ChartAnalysis analysis, ChartNote note);
}
