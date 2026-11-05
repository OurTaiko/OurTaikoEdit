using CharTaiko.Core.Languages;
using CharTaiko.Tja;
using OurTaiko;

namespace CharTaiko.Tests;

public class TjaLanguageTests
{
    static readonly TjaLanguage Tja = new();

    public static string Apply(string text, IEnumerable<TextEdit> edits) => TextEdits.Apply(text, edits);

    static string Chart(string body, string headers = "") => $"BPM:120\n{headers}COURSE:Oni\nLEVEL:5\n#START\n{body}\n#END\n";

    static string Place(string text, int measure, int step, int steps, NoteKind kind, (int Measure, int Step, int Steps)? end = null)
    {
        var a = Tja.Analyze(text, 0);
        var at = new GridPosition(a.Measures[measure], step, steps);
        GridPosition? e = end is { } x ? new GridPosition(a.Measures[x.Measure], x.Step, x.Steps) : null;
        var result = Tja.Editing.PlaceNote(a, at, kind, e);
        Assert.True(result.Ok, result.Error);
        return Apply(text, result.Edits);
    }

    [Fact]
    public void AnalysisListsBlocksAndParsesTheChosenOne()
    {
        string text = "TITLE:X\nBPM:120\nCOURSE:Oni\nLEVEL:8\n#START\n1020,\n#END\nCOURSE:Edit\nLEVEL:10\n#START\n1111,\n#END\n";
        var a = Tja.Analyze(text, 1);
        Assert.Equal(new[] { "Oni ★8", "Edit ★10" }, a.Blocks.Select(b => b.Name));
        Assert.Equal(1, a.Block);
        Assert.Equal(4, a.Chart!.Notes.Count);
        Assert.Equal(new SourceSpan(11, 1, 1), a.SourceOf(a.Chart.Notes[0]));
        Assert.Equal("X", a.Title);
        Assert.Empty(a.Diagnostics);
    }

    [Fact]
    public void ErrorsAndWarningsAreDiagnosticsWithLines()
    {
        var a = Tja.Analyze(Chart("10,\n#SCROLL x\n1H,"), 0);
        // The parser reads on past the error; the timeline refuses to edit such a chart.
        Assert.NotNull(a.Chart);
        Assert.True(a.HasErrors);
        var error = Assert.Single(a.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal((7, 2), (error.Source.Line, error.Source.Column));
        Assert.Contains(a.Diagnostics, d => d.Severity == DiagnosticSeverity.Warning && d.Source.Line == 6);
    }

    [Fact]
    public void AFileErrorIsAtItsLine()
    {
        var a = Tja.Analyze("BPM:120\nCOURSE:Tower\n#START\n1,\n#END\n", 0);
        var error = Assert.Single(a.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal((2, "TJA3001"), (error.Source.Line, error.Code));
    }

    [Fact]
    public void ANoteOnASlotReplacesIt()
        => Assert.Equal(Chart("1020,"), Place(Chart("1000,"), 0, 2, 4, NoteKind.Ka));

    [Fact]
    public void ANoteBetweenSlotsSplitsTheMeasure()
        => Assert.Equal(Chart("10 00\n#SCROLL 2\n20 01,"), Place(Chart("1 0\n#SCROLL 2\n2 0,"), 0, 7, 8, NoteKind.Don));

    [Fact]
    public void AnEmptyMeasureGetsItsNotes()
        => Assert.Equal(Chart("1111,\n0004,"), Place(Chart("1111,\n,"), 1, 6, 8, NoteKind.BigKa));

    [Fact]
    public void ARollEndsWithAnEightAndClearsWhatIsUnder()
        => Assert.Equal(Chart("1500,\n0080,"), Place(Chart("1010,\n1010,"), 0, 1, 4, NoteKind.Roll, (1, 2, 4)));

    [Fact]
    public void ANoteInsideARollIsRefused()
    {
        string text = Chart("5000,\n0080,");
        var a = Tja.Analyze(text, 0);
        var r = Tja.Editing.PlaceNote(a, new GridPosition(a.Measures[1], 0, 4), NoteKind.Don);
        Assert.False(r.Ok);
    }

    [Fact]
    public void RemovingARollClearsItsEnd()
    {
        string text = Chart("5000,\n0080,");
        var a = Tja.Analyze(text, 0);
        var r = Tja.Editing.RemoveNote(a, a.Chart!.Notes[0]);
        Assert.Equal(Chart("0000,\n0000,"), Apply(text, r.Edits));
    }

    [Fact]
    public void ReplacingARollHeadClearsItsEnd()
        => Assert.Equal(Chart("1000,\n0000,"), Place(Chart("5000,\n0080,"), 0, 0, 4, NoteKind.Don));

    [Fact]
    public void ABalloonTakesItsPlaceInTheCourseList()
    {
        string text = "BPM:120\nCOURSE:Oni\nBALLOON:10,20\n#START\n7080,\n0000,\n7080,\n#END\n";
        string placed = Place(text, 1, 0, 4, NoteKind.Balloon, (1, 2, 4));
        Assert.Equal("BPM:120\nCOURSE:Oni\nBALLOON:10,5,20\n#START\n7080,\n7080,\n7080,\n#END\n", placed);
        var a = Tja.Analyze(placed, 0);
        Assert.Equal(new[] { 10, 5, 20 }, a.Chart!.Notes.Select(n => n.BalloonHits));
    }

    [Fact]
    public void RemovingABalloonKeepsTheOthersCounts()
    {
        string text = "BPM:120\nCOURSE:Oni\nBALLOON:10,20,30\n#START\n7080,\n7080,\n7080,\n#END\n";
        var a = Tja.Analyze(text, 0);
        string removed = Apply(text, Tja.Editing.RemoveNote(a, a.Chart!.Notes[1]).Edits);
        Assert.Equal(new[] { 10, 30 }, Tja.Analyze(removed, 0).Chart!.Notes.Select(n => n.BalloonHits));
    }

    [Fact]
    public void ASharedBalloonListGetsACopyForTheCourse()
    {
        string text = "BPM:120\nBALLOON:10\nCOURSE:Oni\n#START\n7080,\n0000,\n#END\n";
        string placed = Place(text, 0, 0, 4, NoteKind.Don);
        Assert.Equal("BPM:120\nBALLOON:10\nCOURSE:Oni\nBALLOON:\n#START\n1000,\n0000,\n#END\n", placed);
    }

    [Fact]
    public void TheHashOnlyChangesWithTheNotes()
    {
        string text = Chart("1 0\n#SCROLL 2\n2 0,");
        string split = Chart("10 00\n#SCROLL 2\n20 00,");
        Assert.Equal(ChartHash.OfFile(text).Single().Hash, ChartHash.OfFile(split).Single().Hash);
    }

    [Theory]
    [InlineData("BPM:120\nCOURSE:Oni\n#START\n#BPM\n#END\n", 4, 5, "#BPMCHANGE")]
    [InlineData("BPM:120\nCOURSE:Oni\nLEV\n", 3, 4, "LEVEL:")]
    [InlineData("BPM:120\nCOURSE:O\n", 2, 9, "Oni")]
    [InlineData("BPM:120\nCOURSE:Oni\n#START\n10,\n#E", 5, 3, "#END")]
    public void CompletionOffersWhatFitsTheLine(string text, int line, int column, string expected)
        => Assert.Contains(Tja.Complete(text, line, column).Items, i => i.Text == expected);

    [Fact]
    public void ANoteLineOffersTheNotes()
        => Assert.Contains(Tja.Complete("BPM:120\nCOURSE:Oni\n#START\n10\n#END\n", 4, 3).Items, i => i.Text == "G");
}
