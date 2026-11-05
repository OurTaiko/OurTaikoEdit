using CharTaiko.Core.Play;
using OurTaiko;

namespace CharTaiko.Tests;

// The game's note placement, as ported into CharTaiko (Core/Play).
public class NoteScrollTests
{
    [Fact]
    public void AMeasureAheadIsAMeasureWidthAway()
    {
        var chart = TjaParser.Parse("BPM:120\nCOURSE:Oni\n#START\n1,\n1,\n#END\n");
        var clocks = new ScrollClockTimeline(chart);
        var (x, y) = NoteScroll.Distance(chart.Notes[1], 0, clocks, NoteScroll.MeasureWidth);
        Assert.Equal((1440.0, 0.0), (x, y));
    }

    [Fact]
    public void BmScrollPlacesByTheBeatClockNormalScrollByTheNotesOwnTempo()
    {
        // The third note is two measures on; the second measure is at 240 BPM. Seen from the start,
        // BM scrolling puts it two measure widths away; normal scrolling by its own BPM over its
        // 3 s to go: 3 × 240 / 240 widths.
        const string body = "#START\n1,\n#BPMCHANGE 240\n1,\n1,\n#END\n";
        var bm = TjaParser.Parse("BPM:120\nCOURSE:Oni\n#BMSCROLL\n" + body);
        var nm = TjaParser.Parse("BPM:120\nCOURSE:Oni\n" + body);
        Assert.Equal(3.0, bm.Notes[2].Time, 3);
        Assert.Equal(2880.0, NoteScroll.Distance(bm.Notes[2], 0, new ScrollClockTimeline(bm), NoteScroll.MeasureWidth).X, 0);
        Assert.Equal(4320.0, NoteScroll.Distance(nm.Notes[2], 0, new ScrollClockTimeline(nm), NoteScroll.MeasureWidth).X, 0);
    }

    [Fact]
    public void SuddenHidesANoteUntilItAppears()
    {
        var chart = TjaParser.Parse("BPM:120\nCOURSE:Oni\n#START\n#SUDDEN 1 0.5\n0001,\n#END\n");
        var note = chart.Notes.Single();
        Assert.False(NoteScroll.Appeared(note, note.Time - 1.5));
        Assert.True(NoteScroll.Appeared(note, note.Time - 0.9));
    }
}
