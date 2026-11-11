using OurTaiko;

namespace OurTaikoEdit.Core.Languages;

public enum DiagnosticSeverity { Information, Warning, Error }

// 1-based line and column; Length 0 marks the whole line.
public readonly record struct SourceSpan(int Line, int Column, int Length)
{
    public bool IsEmpty => Line <= 0;
}

// Code is the language's own (TJA1001 …), if it has codes.
// Tag is the language's own diagnostic, for its quick fixes.
// A command line of the block: its name (with #), its argument, where it is written, the time it
// takes effect (that of the next slot) and the route it is on (BranchId -1: every route).
public sealed record ChartCommand(string Name, string Value, SourceSpan Source, double Time, int BranchId, BranchRoute Route)
{
    public bool IsOn(BranchRoute route) => BranchId < 0 || Route == route;
}

public sealed record ChartDiagnostic(SourceSpan Source, string Message, DiagnosticSeverity Severity, string? Code = null, object? Tag = null);

// One playable block of the file (a TJA #START … #END).
public sealed record ChartBlock(int Index, string Name, string Course, string Side, int Level, int StartLine, bool IsBranching);

// One character position of a measure.
public sealed record ChartSlot(SourceSpan Source, string Token, double Time, ChartNote? Note);

// A measure of the parsed block, on the common part (BranchId -1) or one route of a branch.
public sealed class MeasureView
{
    public int Index { get; init; }
    public int BranchId { get; init; } = -1;
    public BranchRoute Route { get; init; }
    public double Time { get; init; }
    public double EndTime { get; init; }
    public double Bpm { get; init; }
    // As a fraction of a 4/4 measure.
    public double Length { get; init; } = 1;
    public IReadOnlyList<ChartSlot> Slots { get; init; } = Array.Empty<ChartSlot>();
    public SourceSpan Comma { get; init; }
    // The language's own measure object, for its IChartEditing.
    public object? Tag { get; init; }

    public bool IsOn(BranchRoute route) => BranchId < 0 || Route == route;

    // Where in the measure (0 to 1) a time falls: linear between the slots, so a tempo change
    // inside the measure keeps the grid on the written slots.
    public double FractionAt(double time)
    {
        int n = Slots.Count;
        if (n == 0 || EndTime <= Time) return EndTime > Time ? Math.Clamp((time - Time) / (EndTime - Time), 0, 1) : 0;
        for (int i = 0; i < n; i++)
        {
            double t0 = Slots[i].Time, t1 = i + 1 < n ? Slots[i + 1].Time : EndTime;
            if (time < t1 || i == n - 1)
                return Math.Clamp((i + (t1 > t0 ? (time - t0) / (t1 - t0) : 0)) / n, 0, 1);
        }
        return 1;
    }

    public double TimeAt(double fraction)
    {
        int n = Slots.Count;
        fraction = Math.Clamp(fraction, 0, 1);
        if (n == 0) return Time + (EndTime - Time) * fraction;
        double position = fraction * n;
        int i = Math.Min((int)Math.Floor(position), n - 1);
        double t0 = Slots[i].Time, t1 = i + 1 < n ? Slots[i + 1].Time : EndTime;
        return t0 + (t1 - t0) * (position - i);
    }
}

public sealed class ChartAnalysis
{
    public static readonly ChartAnalysis Empty = new();

    // The text this analysis is of; edits apply to it.
    public string Text { get; init; } = "";

    public IReadOnlyList<ChartBlock> Blocks { get; init; } = Array.Empty<ChartBlock>();
    // Index into Blocks of the parsed block, -1 when there is none.
    public int Block { get; init; } = -1;
    // The chosen block's chart, read on past errors where the language can; null when there is
    // none. Timeline edits need HasErrors false: they must land where the parser reads them.
    public TaikoChart? Chart { get; init; }
    public bool HasErrors { get; init; }
    public IReadOnlyList<MeasureView> Measures { get; init; } = Array.Empty<MeasureView>();
    public IReadOnlyList<ChartDiagnostic> Diagnostics { get; init; } = Array.Empty<ChartDiagnostic>();
    public string Title { get; init; } = "";
    public string? Audio { get; init; }
    // Where a note was written, and where a long note ends.
    public Func<ChartNote, SourceSpan> SourceOf { get; init; } = _ => default;
    public Func<ChartNote, SourceSpan> TailOf { get; init; } = _ => default;
    // Every command of the chosen block as written (#BPMCHANGE, #SCROLL, #GOGOSTART …), with when
    // it takes effect, for the timeline's command row.
    public IReadOnlyList<ChartCommand> Commands { get; init; } = Array.Empty<ChartCommand>();
    // The parsed block's lines (from its #START to its #END).
    public int StartLine { get; init; }
    public int EndLine { get; init; }

    public bool HasBranches => Chart?.Branches.Count > 0;

    // The slot written at or before a place in the text, within the parsed block.
    public ChartSlot? SlotAt(int line, int column)
    {
        ChartSlot? best = null;
        foreach (var measure in Measures)
            foreach (var slot in measure.Slots)
            {
                var s = slot.Source;
                if (s.Line < line || s.Line == line && s.Column <= column) { if (best == null || Before(best.Source, s)) best = slot; }
            }
        return best;

        static bool Before(SourceSpan a, SourceSpan b) => a.Line < b.Line || a.Line == b.Line && a.Column < b.Column;
    }

    // The time written at or before a place in the text, within the parsed block: that of the slot
    // there, or the start of a measure with nothing written in it, at its comma.
    public double? TimeAt(int line, int column)
    {
        SourceSpan best = default;
        double? time = null;
        foreach (var measure in Measures)
        {
            if (measure.Slots.Count == 0 && !measure.Comma.IsEmpty) See(measure.Comma, measure.Time);
            foreach (var slot in measure.Slots) See(slot.Source, slot.Time);
        }
        return time;

        void See(SourceSpan s, double at)
        {
            if (s.Line > line || s.Line == line && s.Column > column) return;
            if (time == null || best.Line < s.Line || best.Line == s.Line && best.Column < s.Column) (best, time) = (s, at);
        }
    }

    // The branch and route a line of the parsed block is written in (BranchId -1: the part every
    // route plays): that of the notes or command on the line, or for a line with neither, of what
    // is written before and after it when the two agree. Null between routes (a branch's own
    // markers) and outside the chart.
    public (int BranchId, BranchRoute Route)? RouteAt(int line)
    {
        (int Line, int BranchId, BranchRoute Route)? before = null, after = null;
        foreach (var m in Measures)
        {
            foreach (var slot in m.Slots) See(slot.Source.Line, m.BranchId, m.Route);
            if (!m.Comma.IsEmpty) See(m.Comma.Line, m.BranchId, m.Route);
        }
        foreach (var c in Commands) See(c.Source.Line, c.BranchId, c.Route);
        if (before is not { } b || after is not { } a || b.BranchId != a.BranchId || b.Route != a.Route) return null;
        return (b.BranchId, b.Route);

        void See(int at, int branchId, BranchRoute route)
        {
            if (branchId < 0) (branchId, route) = (-1, BranchRoute.Normal);
            if (at <= line && (before == null || at > before.Value.Line)) before = (at, branchId, route);
            if (at >= line && (after == null || at < after.Value.Line)) after = (at, branchId, route);
        }
    }
}
