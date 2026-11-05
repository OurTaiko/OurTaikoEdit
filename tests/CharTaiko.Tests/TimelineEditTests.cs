using CharTaiko.Core.Languages;
using CharTaiko.Tja;
using OurTaiko;

namespace CharTaiko.Tests;

// Batch note edits, timing commands and the measure grid the timeline moves notes on.
public class TimelineEditTests
{
    static readonly TjaLanguage Tja = new();

    static string Chart(string body, string headers = "") => $"BPM:120\n{headers}COURSE:Oni\nLEVEL:5\n#START\n{body}\n#END\n";

    static string Apply(string text, EditResult r)
    {
        Assert.True(r.Ok, r.Error);
        return TextEdits.Apply(text, r.Edits);
    }

    [Fact]
    public void MovingANoteRemovesItAndPlacesItAgain()
    {
        string text = Chart("1020,\n0000,");
        var a = Tja.Analyze(text, 0);
        var grid = new MeasureGrid(a, BranchRoute.Normal, 4);
        var don = a.Chart!.Notes[0];
        var to = grid.At(grid.BeatOf(don)!.Value + 5)!.Value; // five quarters on: 2nd of the next measure
        string moved = Apply(text, Tja.Editing.Edit(a, new[] { don }, new[] { new NotePlacement(to, NoteKind.Don) }));
        Assert.Equal(Chart("0020,\n0100,"), moved);
    }

    [Fact]
    public void ANoteMovedOntoItsNeighboursPlaceWins()
    {
        string text = Chart("1200,");
        var a = Tja.Analyze(text, 0);
        var at = new GridPosition(a.Measures[0], 1, 4);
        // The don takes the ka's place; the ka is removed and placed one step on.
        var notes = a.Chart!.Notes;
        string moved = Apply(text, Tja.Editing.Edit(a, notes, new[] { new NotePlacement(at, NoteKind.Don), new NotePlacement(at with { Step = 2 }, NoteKind.Ka) }));
        Assert.Equal(Chart("0120,"), moved);
    }

    [Fact]
    public void MovingARollMovesItsEnd()
    {
        string text = Chart("5008,\n0000,");
        var a = Tja.Analyze(text, 0);
        var roll = a.Chart!.Notes[0];
        var grid = new MeasureGrid(a, BranchRoute.Normal, 4);
        double head = grid.BeatOf(roll)!.Value, tail = grid.BeatOf(roll, tail: true)!.Value;
        Assert.Equal((0, 3), (head, tail));
        var place = new NotePlacement(grid.At(head + 2)!.Value, NoteKind.Roll, grid.At(tail + 2)!.Value);
        Assert.Equal(Chart("0050,\n0800,"), Apply(text, Tja.Editing.Edit(a, new[] { roll }, new[] { place })));
    }

    [Fact]
    public void ANoteCannotLandInsideARollPlacedWithIt()
    {
        string text = Chart("0000,");
        var a = Tja.Analyze(text, 0);
        var m = a.Measures[0];
        var r = Tja.Editing.Edit(a, Array.Empty<ChartNote>(), new[]
        {
            new NotePlacement(new GridPosition(m, 0, 4), NoteKind.Roll, new GridPosition(m, 3, 4)),
            new NotePlacement(new GridPosition(m, 1, 4), NoteKind.Don),
        });
        Assert.False(r.Ok);
    }

    [Fact]
    public void ATempoChangeGoesOnItsOwnLineBeforeASlotThatStartsALine()
        => Assert.Equal(Chart("1000,\n#BPMCHANGE 180\n2000,"),
            Apply(Chart("1000,\n2000,"), Tja.Editing.InsertEvent(Tja.Analyze(Chart("1000,\n2000,"), 0), new GridPosition(Tja.Analyze(Chart("1000,\n2000,"), 0).Measures[1], 0, 4), ChartEventKind.Tempo, "180")));

    [Fact]
    public void ACommandBetweenSlotsSplitsTheMeasureAndTheLine()
    {
        string text = Chart("1020,");
        var a = Tja.Analyze(text, 0);
        string done = Apply(text, Tja.Editing.InsertEvent(a, new GridPosition(a.Measures[0], 1, 8), ChartEventKind.Scroll, "2"));
        Assert.Equal(Chart("1\n#SCROLL 2\n0002000,"), done);
        // The notes stay where they were.
        Assert.Equal(TjaParser.Parse(text).Notes.Select(n => n.Time), TjaParser.Parse(done).Notes.Select(n => n.Time));
    }

    [Fact]
    public void ACommandInsideALineBreaksIt()
    {
        string text = Chart("10 20,");
        var a = Tja.Analyze(text, 0);
        Assert.Equal(Chart("10 \n#GOGOSTART\n20,"), Apply(text, Tja.Editing.InsertEvent(a, new GridPosition(a.Measures[0], 2, 4), ChartEventKind.GogoStart, null)));
    }

    [Fact]
    public void MeasureOnlyAtAMeasuresStart()
    {
        var a = Tja.Analyze(Chart("1000,"), 0);
        Assert.False(Tja.Editing.InsertEvent(a, new GridPosition(a.Measures[0], 1, 4), ChartEventKind.Measure, "3/4").Ok);
        Assert.Equal(Chart("#MEASURE 3/4\n1000,"), Apply(Chart("1000,"), Tja.Editing.InsertEvent(a, new GridPosition(a.Measures[0], 0, 4), ChartEventKind.Measure, "3/4")));
    }

    [Fact]
    public void ATempoChangeIsChangedAndRemoved()
    {
        string text = Chart("1000,\n#BPMCHANGE 180\n2000,");
        var a = Tja.Analyze(text, 0);
        var tempo = a.Chart!.Tempos.Single();
        Assert.Equal(Chart("1000,\n#BPMCHANGE 200\n2000,"), Apply(text, Tja.Editing.ChangeEvent(a, tempo, "200")));
        Assert.Equal(Chart("1000,\n2000,"), Apply(text, Tja.Editing.RemoveEvent(a, tempo)));
    }

    [Fact]
    public void TheGridCountsBeatsAcrossMeasuresOfAnyLength()
    {
        var a = Tja.Analyze(Chart("1000,\n#MEASURE 3/4\n100,\n1,"), 0);
        var grid = new MeasureGrid(a, BranchRoute.Normal, 16);
        Assert.Equal(new double?[] { 0, 4, 7 }, a.Chart!.Notes.Select(n => grid.BeatOf(n)));
        var at = grid.At(5.5)!.Value;
        Assert.Equal((1, 6, 12), (a.Measures.ToList().IndexOf(at.Measure), at.Step, at.Steps));
        Assert.Null(grid.At(-1));
    }
}
