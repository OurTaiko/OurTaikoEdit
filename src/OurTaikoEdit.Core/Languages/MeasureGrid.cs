using OurTaiko;

namespace OurTaikoEdit.Core.Languages;

// One route's run through the measures, counted in beats (quarter notes) from the first bar
// line, for moving and pasting by grid steps: a note's place is its measure and its slot's
// fraction of it; a beat goes back to the nearest grid step of the measure it falls in.
public sealed class MeasureGrid
{
    readonly List<(MeasureView Measure, double Beat)> measures = new();
    readonly ChartAnalysis analysis;
    public BranchRoute Route { get; }
    public int Division { get; }

    public MeasureGrid(ChartAnalysis analysis, BranchRoute route, int division)
    {
        this.analysis = analysis;
        Route = route;
        Division = Math.Max(1, division);
        double beat = 0;
        foreach (var m in analysis.Measures.Where(m => m.IsOn(route)).OrderBy(m => m.Time))
        {
            measures.Add((m, beat));
            beat += m.Length * 4;
        }
    }

    // Grid steps in a measure: Division per 4/4.
    public int Steps(MeasureView m) => Math.Max(1, (int)Math.Round(Division * m.Length));

    // The beat of a measure's fraction, or null when the measure is not on this route.
    public double? Beat(MeasureView m, double fraction)
    {
        foreach (var (measure, beat) in measures)
            if (ReferenceEquals(measure, m)) return beat + fraction * m.Length * 4;
        return null;
    }

    // The grid step nearest a beat; null past the last measure or before the first.
    public GridPosition? At(double beat)
    {
        if (measures.Count == 0 || beat < -1e-9) return null;
        for (int i = 0; i < measures.Count; i++)
        {
            var (m, start) = measures[i];
            double length = m.Length * 4;
            if (beat > start + length - 1e-9 && i + 1 < measures.Count) continue;
            int steps = Steps(m);
            int step = (int)Math.Round((beat - start) / length * steps);
            if (step >= steps)
            {
                if (i + 1 < measures.Count) return new GridPosition(measures[i + 1].Measure, 0, Steps(measures[i + 1].Measure), Route);
                return null;
            }
            return new GridPosition(m, Math.Max(0, step), steps, Route);
        }
        return null;
    }

    public double? Beat(GridPosition at) => Beat(at.Measure, at.Fraction);

    // Where a note is written: its measure and slot (or, with tail, where its 8 is).
    public (MeasureView Measure, double Fraction)? PlaceOf(ChartNote note, bool tail = false)
    {
        var span = tail ? analysis.TailOf(note) : default;
        foreach (var m in analysis.Measures)
            for (int i = 0; i < m.Slots.Count; i++)
                if (tail ? !span.IsEmpty && m.Slots[i].Source == span : ReferenceEquals(m.Slots[i].Note, note))
                    return (m, (double)i / m.Slots.Count);
        return null;
    }

    public double? BeatOf(ChartNote note, bool tail = false)
        => PlaceOf(note, tail) is { } p ? Beat(p.Measure, p.Fraction) : null;

    // One grid step in beats.
    public double StepBeats => 4.0 / Division;
}
