namespace CharTaiko.Core.Languages;

// What the code editor asks a language for besides analysis and completion. Every member of
// IChartLanguage that returns these has a default that offers nothing, so a new language can
// start with analysis alone.

// Markdown shown when the pointer rests on Span.
public sealed record HoverInfo(SourceSpan Span, string Markdown);

// A command's parameters while its arguments are written: Parameters are (start, length) in Label.
public sealed record SignatureInfo(string Label, IReadOnlyList<(int Start, int Length)> Parameters, int Active, string? Summary = null);

// Lines StartLine to EndLine (1-based) fold to StartLine.
public sealed record FoldRange(int StartLine, int EndLine, string Kind);

// A note drawn at the end of a line (measure numbers, times).
public sealed record InlayHint(int Line, int Column, string Label);

// A change offered by name: a quick fix, a refactoring, formatting.
public sealed record CodeAction(string Title, IReadOnlyList<TextEdit> Edits, bool IsQuickFix = false);

// One block at a glance.
public sealed record BlockSummary(int Block, string Text, string Hash);
