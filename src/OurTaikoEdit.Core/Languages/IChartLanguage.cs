using OurTaiko;

namespace OurTaikoEdit.Core.Languages;

// A chart type OurTaikoEdit can edit. Each project names one by Id; the editor asks it to analyse
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

// A note to put on the grid; a roll, balloon or kusudama with where it ends.
public sealed record NotePlacement(GridPosition At, NoteKind Kind, GridPosition? End = null);

// Timing commands the timeline can write.
public enum ChartEventKind { Tempo, Scroll, Measure, GogoStart, GogoEnd, BarlineOff, BarlineOn, Delay }

// Timeline edits as text edits on the analysed text.
public interface IChartEditing
{
    // Removes notes (a long note with its end) and places others, as one change: a placement
    // wins over a removal at the same place, so moving notes is removing them and placing them
    // again. Placing replaces what is there.
    EditResult Edit(ChartAnalysis analysis, IReadOnlyList<ChartNote> remove, IReadOnlyList<NotePlacement> place);

    EditResult PlaceNote(ChartAnalysis analysis, GridPosition at, NoteKind kind, GridPosition? end = null)
        => Edit(analysis, Array.Empty<ChartNote>(), new[] { new NotePlacement(at, kind, end) });

    EditResult RemoveNote(ChartAnalysis analysis, ChartNote note)
        => Edit(analysis, new[] { note }, Array.Empty<NotePlacement>());

    // Writes a timing command so that it takes effect at a grid position (value: the BPM, the
    // speed, a/b, seconds).
    EditResult InsertEvent(ChartAnalysis analysis, GridPosition at, ChartEventKind kind, string? value);

    // A command of ChartAnalysis.Commands: removed, given a new value, or moved to take effect
    // at another grid position (one change).
    EditResult RemoveCommand(ChartAnalysis analysis, ChartCommand command);
    EditResult ChangeCommand(ChartAnalysis analysis, ChartCommand command, string value);
    EditResult MoveCommand(ChartAnalysis analysis, ChartCommand command, GridPosition to);
}
