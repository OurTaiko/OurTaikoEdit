using OurTaikoEdit.Core.Languages;
using OurTaikoEdit.Tja;
using OurTaiko;

namespace OurTaikoEdit.Tests;

// A new difficulty, empty or with the shape of one the chart has.
public class NewBlockTests
{
    static readonly TjaLanguage Tja = new();

    // An Oni chart: a tempo change half-way through the second measure, go-go time over the
    // third and fourth, a 3/4 measure, a delay, a scroll change, and a branch whose routes differ.
    const string Oni = """
        TITLE:Song
        BPM:120
        OFFSET:-1
        BALLOON:5

        COURSE:Oni
        LEVEL:8

        #START
        1010201010102010, // intro
        10102010
        #BPMCHANGE 180
        #SCROLL 2
        10102010,
        #GOGOSTART
        1111,
        #MEASURE 3/4
        700008,
        #GOGOEND
        #MEASURE 4/4
        #DELAY 0.5
        #BRANCHSTART p,60,85
        #N
        1000,
        2000,
        #E
        1010,
        #GOGOSTART
        2020,
        #M
        1111,
        2222,
        #BRANCHEND
        #GOGOEND
        3,
        #END

        """;

    static string Text => Oni.Replace("\r\n", "\n");

    static string Added(string text, NewBlock block)
    {
        var a = Tja.Analyze(text, 0);
        var r = Tja.Editing.InsertBlock(a, block);
        Assert.True(r.Ok, r.Error);
        string after = TextEdits.Apply(text, r.Edits);
        Assert.StartsWith(text, after);
        return after[text.Length..];
    }

    static ChartAnalysis Block(string text, int block) => Tja.Analyze(text, block);

    [Fact]
    public void TheDifficultiesOfferedAreThoseTheChartHasNot()
    {
        var a = Tja.Analyze(Text, 0);
        Assert.Equal(new[] { "Easy", "Normal", "Hard", "Edit" }, Tja.NewBlockCourses(a).Select(c => c.Id));
        Assert.All(Tja.NewBlockCourses(a), c => Assert.InRange(c.Level, 1, 10));
        // One it has is refused.
        var twice = Tja.Editing.InsertBlock(a, new NewBlock("Oni", 8));
        Assert.False(twice.Ok);
        Assert.Contains("Oni", twice.Error);
        // A chart with no block yet can have any.
        Assert.Equal(5, Tja.NewBlockCourses(Tja.Analyze("TITLE:x\nBPM:120\n", 0)).Count);
    }

    [Fact]
    public void ANewDifficultyIsWrittenAfterTheLastWithFourEmptyMeasures()
    {
        Assert.Equal("\nCOURSE:Hard\nLEVEL:6\nBALLOON:\n\n#START\n,\n,\n,\n,\n#END\n", Added(Text, new NewBlock("Hard", 6)));
        // A file that does not end its last line, with the other line ending.
        string bare = "BPM:120\r\nCOURSE:Oni\r\n#START\r\n1,\r\n#END";
        Assert.Equal("\r\n\r\nCOURSE:Easy\r\nLEVEL:3\r\nBALLOON:\r\n\r\n#START\r\n,\r\n,\r\n,\r\n,\r\n#END\r\n", Added(bare, new NewBlock("Easy", 3)));
        // No block yet.
        Assert.Equal("\nCOURSE:Oni\nLEVEL:10\nBALLOON:\n\n#START\n,\n,\n,\n,\n#END\n", Added("TITLE:x\nBPM:120\n", new NewBlock("Oni", 99)));

        // It is the chart's second block, read without problems, and the first is as it was.
        string both = Text + Added(Text, new NewBlock("Hard", 6));
        var hard = Block(both, 1);
        Assert.Equal(new[] { "Oni ★8", "Hard ★6" }, hard.Blocks.Select(b => b.Name));
        Assert.False(hard.HasErrors);
        Assert.Equal(4, hard.Measures.Count);
        Assert.Empty(hard.Chart!.Notes);
        Assert.Equal(Block(Text, 0).Chart!.Notes.Select(n => n.Time), Block(both, 0).Chart!.Notes.Select(n => n.Time));
    }

    [Fact]
    public void EverythingCarriedOverGivesTheSameMeasuresWithNothingToPlay()
    {
        string added = Added(Text, new NewBlock("Hard", 6, From: 0, Tempo: true, Branches: true, Gogo: true));
        // The tempo change keeps its place half-way through its measure; the scroll, the comment
        // and the notes are not there.
        Assert.Equal("""

            COURSE:Hard
            LEVEL:6
            BALLOON:

            #START
            ,
            0
            #BPMCHANGE 180
            0,
            #GOGOSTART
            ,
            #MEASURE 3/4
            ,
            #GOGOEND
            #MEASURE 4/4
            #DELAY 0.5
            #BRANCHSTART p,60,85
            #N
            ,
            ,
            #E
            ,
            #GOGOSTART
            ,
            #M
            ,
            ,
            #BRANCHEND
            #GOGOEND
            ,
            #END

            """.Replace("\r\n", "\n"), added);

        var oni = Block(Text, 0);
        var hard = Block(Text + added, 1);
        Assert.False(hard.HasErrors);
        Assert.Empty(hard.Chart!.Notes);
        Assert.Equal(oni.Measures.Select(m => (m.BranchId, m.Route, Math.Round(m.Time, 4), Math.Round(m.EndTime, 4), m.Length)),
            hard.Measures.Select(m => (m.BranchId, m.Route, Math.Round(m.Time, 4), Math.Round(m.EndTime, 4), m.Length)));
        Assert.Equal(oni.Branches.Select(b => (b.Start.Value, Math.Round(b.Time, 4))), hard.Branches.Select(b => (b.Start.Value, Math.Round(b.Time, 4))));
        string[] kept = { "#BPMCHANGE", "#MEASURE", "#DELAY", "#GOGOSTART", "#GOGOEND" };
        Assert.Equal(oni.Commands.Where(c => kept.Contains(c.Name)).Select(c => (c.Name, c.Value, Math.Round(c.Time, 4), c.BranchId, c.Route)),
            hard.Commands.Select(c => (c.Name, c.Value, Math.Round(c.Time, 4), c.BranchId, c.Route)));
    }

    [Fact]
    public void WithoutTheBranchesTheNormalRouteIsWhatEveryRoutePlays()
    {
        string added = Added(Text, new NewBlock("Normal", 5, From: 0, Tempo: true, Gogo: true));
        Assert.Equal("#START\n,\n0\n#BPMCHANGE 180\n0,\n#GOGOSTART\n,\n#MEASURE 3/4\n,\n#GOGOEND\n#MEASURE 4/4\n#DELAY 0.5\n,\n,\n#GOGOEND\n,\n#END\n",
            added[added.IndexOf("#START", StringComparison.Ordinal)..]);
        var oni = Block(Text, 0);
        var normal = Block(Text + added, 1);
        Assert.False(normal.HasBranches);
        Assert.Equal(oni.Measures.Where(m => m.IsOn(BranchRoute.Normal)).Select(m => (Math.Round(m.Time, 4), Math.Round(m.EndTime, 4))),
            normal.Measures.Select(m => (Math.Round(m.Time, 4), Math.Round(m.EndTime, 4))));
    }

    [Fact]
    public void EachKindIsCarriedOverOnItsOwn()
    {
        // Go-go time alone: the same number of measures, all 4/4 at the first tempo.
        string gogo = Added(Text, new NewBlock("Easy", 3, From: 0, Gogo: true));
        Assert.Equal("#START\n,\n,\n#GOGOSTART\n,\n,\n#GOGOEND\n,\n,\n#GOGOEND\n,\n#END\n", gogo[gogo.IndexOf("#START", StringComparison.Ordinal)..]);
        // The branches alone, every route.
        string branches = Added(Text, new NewBlock("Easy", 3, From: 0, Branches: true));
        Assert.Equal("#START\n,\n,\n,\n,\n#BRANCHSTART p,60,85\n#N\n,\n,\n#E\n,\n,\n#M\n,\n,\n#BRANCHEND\n,\n#END\n", branches[branches.IndexOf("#START", StringComparison.Ordinal)..]);
        // Nothing asked for: its measures, of the normal route.
        string none = Added(Text, new NewBlock("Easy", 3, From: 0));
        Assert.Equal("#START\n" + string.Concat(Enumerable.Repeat(",\n", 7)) + "#END\n", none[none.IndexOf("#START", StringComparison.Ordinal)..]);
    }

    [Fact]
    public void CommandsInsideAMeasureKeepTheirPlacesWithAsFewRestsAsHoldThem()
    {
        // At 4 and 12 of 16, and one after the last note: quarters hold the first two.
        string text = "BPM:120\nCOURSE:Oni\nLEVEL:8\n#START\n1010\n#BPMCHANGE 150\n20102010\n#GOGOSTART\n1010\n#BPMCHANGE 90\n,\n1,\n#END\n";
        string added = Added(text, new NewBlock("Hard", 6, From: 0, Tempo: true, Gogo: true));
        Assert.Equal("#START\n0\n#BPMCHANGE 150\n00\n#GOGOSTART\n0\n#BPMCHANGE 90\n,\n,\n#END\n", added[added.IndexOf("#START", StringComparison.Ordinal)..]);
        var oni = Block(text, 0);
        var hard = Block(text + added, 1);
        Assert.Equal(oni.Measures.Select(m => (Math.Round(m.Time, 4), Math.Round(m.EndTime, 4))), hard.Measures.Select(m => (Math.Round(m.Time, 4), Math.Round(m.EndTime, 4))));
        Assert.Equal(oni.Commands.Select(c => (c.Name, Math.Round(c.Time, 4))), hard.Commands.Select(c => (c.Name, Math.Round(c.Time, 4))));

        // A block with an error in it is not started from.
        string broken = "BPM:120\nCOURSE:Oni\n#START\n#BPMCHANGE fast\n1,\n#END\n";
        var a = Tja.Analyze(broken, 0);
        if (a.HasErrors) Assert.False(Tja.Editing.InsertBlock(a, new NewBlock("Hard", 6, From: 0, Tempo: true)).Ok);
    }
}
