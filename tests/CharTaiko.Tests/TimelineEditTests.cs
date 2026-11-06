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
        var tempo = Assert.Single(a.Commands);
        Assert.Equal(("#BPMCHANGE", "180", 2.0), (tempo.Name, tempo.Value, tempo.Time));
        Assert.Equal(Chart("1000,\n#BPMCHANGE 200\n2000,"), Apply(text, Tja.Editing.ChangeCommand(a, tempo, "200")));
        Assert.Equal(Chart("1000,\n2000,"), Apply(text, Tja.Editing.RemoveCommand(a, tempo)));
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

    [Fact]
    public void CommandsKnowWhenAndWhereTheyTakeEffect()
    {
        var a = Tja.Analyze(Chart("10,\n#SCROLL 1.5\n#GOGOSTART\n1\n#BPMCHANGE 240\n0,\n#BRANCHSTART p,1,2\n#N\n#SCROLL 2\n1,\n#BRANCHEND\n#GOGOEND\n1,"), 0);
        var c = a.Commands.Select(x => (x.Name, x.Value, Math.Round(x.Time, 3), x.BranchId)).ToList();
        Assert.Equal(new[]
        {
            ("#SCROLL", "1.5", 2.0, -1), ("#GOGOSTART", "", 2.0, -1), ("#BPMCHANGE", "240", 3.0, -1),
            ("#SCROLL", "2", 3.5, 0), ("#GOGOEND", "", 4.5, -1),
        }, c);
    }

    [Fact]
    public void ACommandMovesInOneChange()
    {
        string text = Chart("1000,\n#SCROLL 2\n2000,\n3000,");
        var a = Tja.Analyze(text, 0);
        var scroll = Assert.Single(a.Commands);
        string moved = Apply(text, Tja.Editing.MoveCommand(a, scroll, new GridPosition(a.Measures[2], 2, 4)));
        Assert.Equal(Chart("1000,\n2000,\n30\n#SCROLL 2\n00,"), moved);
        // To where it already is: nothing.
        Assert.Empty(Tja.Editing.MoveCommand(a, scroll, new GridPosition(a.Measures[1], 0, 4)).Edits);
    }

    [Fact]
    public void ARollAcrossMeasuresMovesWhole()
    {
        string text = Chart("0050,\n0800,\n0000,");
        var a = Tja.Analyze(text, 0);
        var roll = a.Chart!.Notes.Single();
        var grid = new MeasureGrid(a, BranchRoute.Normal, 4);
        double head = grid.BeatOf(roll)!.Value, tail = grid.BeatOf(roll, tail: true)!.Value;
        var place = new NotePlacement(grid.At(head + 4)!.Value, NoteKind.Roll, grid.At(tail + 4)!.Value);
        Assert.Equal(Chart("0000,\n0050,\n0800,"), Apply(text, Tja.Editing.Edit(a, new[] { roll }, new[] { place })));
    }

    [Fact]
    public void BlankSlotsAreTimedAtTheTempoInForce()
    {
        // A blank after a mid-measure tempo change and after a delay plays where the parser says.
        string text = Chart("1\n#BPMCHANGE 240\n00\n#DELAY 0.5\n1,");
        var a = Tja.Analyze(text, 0);
        var times = a.Measures[0].Slots.Select(s => Math.Round(s.Time, 3)).ToArray();
        Assert.Equal(new[] { 0.0, 0.5, 0.75, 1.5 }, times);
        Assert.Equal(1.5, TjaParser.Parse(text).Notes[1].Time, 3);
    }

    [Fact]
    public void ABlankSlotIsTimedWhereANoteWrittenThereWouldBe()
    {
        // TJAParser v0.13.0 keeps times to a thousandth of a millisecond: 170 BPM sixteenths are
        // 88.2352941… ms apart, 204.5 BPM ones 73.3496332… ms.
        string first = "1" + new string('0', 7), second = new string('0', 8);
        string Text(string a, string b) => "BPM:170\nOFFSET:-1.2345\nCOURSE:Oni\nLEVEL:5\n#START\n" + a + "\n#BPMCHANGE 204.5\n" + b + ",\n#END\n";
        var slots = Tja.Analyze(Text(first, second), 0).Measures[0].Slots;
        Assert.Equal(16, slots.Count);
        Assert.Equal(1.2345 + 0.088235, slots[1].Time, 9);
        for (int i = 1; i < 16; i++)
        {
            char[] a = first.ToCharArray(), b = second.ToCharArray();
            if (i < 8) a[i] = '2'; else b[i - 8] = '2';
            var ka = TjaParser.Parse(Text(new string(a), new string(b))).Notes[1];
            Assert.Equal(ka.Time, slots[i].Time);
        }
    }
}
