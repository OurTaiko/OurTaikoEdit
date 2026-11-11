using OurTaikoEdit.Core.Languages;
using OurTaikoEdit.Tja;
using OurTaiko;

namespace OurTaikoEdit.Tests;

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
    public void ALineKnowsTheRouteItIsWrittenIn()
    {
        // The body starts on line 5.
        var a = Tja.Analyze(Chart("10,\n#BRANCHSTART p,1,2\n#N\n1,\n#E\n#SCROLL 2\n\n2,\n// a comment\n2,\n#BRANCHEND\n#SCROLL 1\n1,"), 0);
        (int, BranchRoute)? common = (-1, BranchRoute.Normal), normal = (0, BranchRoute.Normal), expert = (0, BranchRoute.Expert);
        Assert.Equal(common, a.RouteAt(5));
        // A branch's markers are between routes: no route of their own.
        Assert.Null(a.RouteAt(6));
        Assert.Null(a.RouteAt(7));
        Assert.Equal(normal, a.RouteAt(8));
        Assert.Null(a.RouteAt(9));
        // A command, an empty line and a comment inside a route are that route's.
        Assert.Equal(expert, a.RouteAt(10));
        Assert.Equal(expert, a.RouteAt(11));
        Assert.Equal(expert, a.RouteAt(12));
        Assert.Equal(expert, a.RouteAt(13));
        Assert.Equal(expert, a.RouteAt(14));
        Assert.Null(a.RouteAt(15));
        Assert.Equal(common, a.RouteAt(16));
        Assert.Equal(common, a.RouteAt(17));
        // Outside the chart.
        Assert.Null(a.RouteAt(2));
        Assert.Null(a.RouteAt(40));
    }

    [Fact]
    public void AMeasureWithNothingWrittenIsAPlaceAtItsComma()
    {
        // The body starts on line 5; a measure is two seconds long.
        var a = Tja.Analyze(Chart("10,\n#BRANCHSTART p,1,2\n#N\n1,\n#E\n,\n#BRANCHEND\n1,"), 0);
        Assert.Null(a.TimeAt(4, 1));
        Assert.Equal(1.0, a.TimeAt(5, 2));
        Assert.Equal(2.0, a.TimeAt(8, 1));
        // The expert route's measure is only its comma: its own start, not the note before it.
        Assert.Equal(2.0, a.TimeAt(10, 1));
        Assert.Equal(2.0, a.TimeAt(11, 1));
        Assert.Equal(4.0, a.TimeAt(12, 1));
        Assert.Equal((0, BranchRoute.Expert), a.RouteAt(10));
    }

    [Fact]
    public void ABranchKnowsItsStartItsConditionAndWhenItIsJudged()
    {
        // The body starts on line 5; a measure is two seconds long.
        string text = Chart("1,\n1,\n#SECTION\n1,\n#BRANCHSTART p, 60,85\n#N\n1,\n1,\n#E\n2,\n2,\n#BRANCHEND\n1,\n#BRANCHSTART r,1,2\n#N\n5008,\n#M\n,\n#BRANCHEND\n1,");
        var a = Tja.Analyze(text, 0);
        Assert.Equal(2, a.Branches.Count);
        var first = a.Branches[0];
        Assert.Equal(("#BRANCHSTART", "p, 60,85", 9, 1), (first.Start.Name, first.Start.Value, first.Start.Source.Line, first.Start.Source.Column));
        // Judged at the bar line before the branch: the start of the measure before it.
        Assert.Equal((6.0, 4.0, 10.0), (first.Time, first.DecisionTime, first.EndTime));
        var second = a.Branches[1];
        Assert.Equal(("r,1,2", 18), (second.Start.Value, second.Start.Source.Line));
        Assert.Equal((12.0, 10.0, 14.0), (second.Time, second.DecisionTime, second.EndTime));
        // Not among the commands the row moves and removes.
        Assert.DoesNotContain(a.Commands, c => c.Name == "#BRANCHSTART");

        // Its condition is changed as any command's value is.
        string changed = Apply(text, Tja.Editing.ChangeCommand(a, first.Start, "p,50,80"));
        Assert.Contains("\n#BRANCHSTART p,50,80\n#N\n", changed);
        Assert.Equal("p,50,80", Tja.Analyze(changed, 0).Branches[0].Start.Value);
    }

    [Fact]
    public void RemovingABranchKeepsOneRouteAsTheCommonPart()
    {
        string text = Chart("1,\n#BRANCHSTART p,60,85\n#N\n1000,\n2000,\n#E\n1010,\n#SCROLL 2\n2020,\n\n#M\n1111,\n2222,\n#BRANCHEND\n#SCROLL 1\n3,");
        var a = Tja.Analyze(text, 0);
        string kept = Apply(text, Tja.Editing.RemoveBranch(a, a.Branches[0], BranchRoute.Normal));
        Assert.Equal(Chart("1,\n1000,\n2000,\n#SCROLL 1\n3,"), kept);
        var b = Tja.Analyze(kept, 0);
        Assert.False(b.HasErrors);
        Assert.Empty(b.Branches);
        // What the normal route played, when it played it.
        Assert.Equal(a.Chart!.Notes.Where(n => n.BranchId < 0 || n.Route == BranchRoute.Normal).Select(n => (n.Kind, n.Time)),
            b.Chart!.Notes.Select(n => (n.Kind, n.Time)));

        // Another route kept: its own commands stay with it.
        Assert.Equal(Chart("1,\n1010,\n#SCROLL 2\n2020,\n\n#SCROLL 1\n3,"), Apply(text, Tja.Editing.RemoveBranch(a, a.Branches[0], BranchRoute.Expert)));
        Assert.Equal(Chart("1,\n1111,\n2222,\n#SCROLL 1\n3,"), Apply(text, Tja.Editing.RemoveBranch(a, a.Branches[0], BranchRoute.Master)));
    }

    [Fact]
    public void OnlyTheBranchAskedForIsRemoved()
    {
        // The first has no #BRANCHEND (the next #BRANCHSTART ends it) and no master route; the
        // second is ended by #END. Line endings stay as they are.
        string text = Chart("1,\n#BRANCHSTART p,1,2\n#E\n2,\n#N\n1,\n#BRANCHSTART r,1,2\n#N\n3,\n#M\n4,").Replace("\n", "\r\n");
        var a = Tja.Analyze(text, 0);
        Assert.Equal(2, a.Branches.Count);
        Assert.Equal(Chart("1,\n1,\n#BRANCHSTART r,1,2\n#N\n3,\n#M\n4,").Replace("\n", "\r\n"), Apply(text, Tja.Editing.RemoveBranch(a, a.Branches[0], BranchRoute.Normal)));
        Assert.Equal(Chart("1,\n#BRANCHSTART p,1,2\n#E\n2,\n#N\n1,\n3,").Replace("\n", "\r\n"), Apply(text, Tja.Editing.RemoveBranch(a, a.Branches[1], BranchRoute.Normal)));
        // A route that is not written cannot be the one kept.
        var missing = Tja.Editing.RemoveBranch(a, a.Branches[0], BranchRoute.Master);
        Assert.False(missing.Ok);
        Assert.Empty(missing.Edits);
    }

    [Fact]
    public void RemovingABranchKeepsTheBalloonCounts()
    {
        string head = "BPM:120\nCOURSE:Oni\nLEVEL:5\n";
        string body = "#START\n7008,\n#BRANCHSTART p,1,2\n#N\n7008,\n#E\n1,\n#M\n7008,\n#BRANCHEND\n1,\n#BRANCHSTART p,1,2\n#N\n7008,\n#E\n7008,\n#M\n7008,\n#BRANCHEND\n#END\n";
        string text = head + "BALLOON:10,11,12\nBALLOONEXP:20,21\nBALLOONMAS:30,31,32\n" + body;
        var a = Tja.Analyze(text, 0);
        Assert.False(a.HasErrors);
        string kept = Apply(text, Tja.Editing.RemoveBranch(a, a.Branches[0], BranchRoute.Normal));
        // The normal route's balloon is now every route's: it takes a place in each list, with
        // its own count, where the removed routes' balloons (none in expert, one in master) were.
        Assert.StartsWith(head + "BALLOON:10,11,12\nBALLOONEXP:20,11,21\nBALLOONMAS:30,11,32\n#START\n7008,\n7008,\n1,\n#BRANCHSTART", kept);
        var b = Tja.Analyze(kept, 0);
        Assert.False(b.HasErrors);
        var later = b.Chart!.Notes.Where(n => n.BranchId >= 0).ToDictionary(n => n.Route, n => n.BalloonHits);
        Assert.Equal((12, 21, 32), (later[BranchRoute.Normal], later[BranchRoute.Expert], later[BranchRoute.Master]));
        Assert.Equal(11, b.Chart.Notes.Where(n => n.BranchId < 0).ElementAt(1).BalloonHits);
    }

    [Fact]
    public void ANewBranchIsOneMeasureWrittenForEveryRoute()
    {
        string text = Chart("1000,\n#SCROLL 2\n2000,\n3000,");
        var a = Tja.Analyze(text, 0);
        string branched = Apply(text, Tja.Editing.InsertBranch(a, a.Measures[1], "p,60,85"));
        // The command before the measure stays before the branch.
        Assert.Equal(Chart("1000,\n#SCROLL 2\n#BRANCHSTART p,60,85\n#N\n2000,\n#E\n2000,\n#M\n2000,\n#BRANCHEND\n3000,"), branched);
        var b = Tja.Analyze(branched, 0);
        Assert.False(b.HasErrors);
        var branch = Assert.Single(b.Branches);
        Assert.Equal(("p,60,85", 2.0, 0.0, 4.0), (branch.Start.Value, branch.Time, branch.DecisionTime, branch.EndTime));
        foreach (var route in new[] { BranchRoute.Normal, BranchRoute.Expert, BranchRoute.Master })
            Assert.Equal(a.Chart!.Notes.Select(n => (n.Kind, n.Time, n.ScrollX)),
                b.Chart!.Notes.Where(n => n.BranchId < 0 || n.Route == route).Select(n => (n.Kind, n.Time, n.ScrollX)));
        // And taken out again it is the chart it was.
        Assert.Equal(text, Apply(branched, Tja.Editing.RemoveBranch(b, branch, BranchRoute.Normal)));

        // Measures sharing a line, one with a command inside it and a comment after it.
        string crowded = Chart("1,2,3,\n10\n#GOGOSTART\n20, // loud\n4,").Replace("\n", "\r\n");
        var c = Tja.Analyze(crowded, 0);
        Assert.Equal(Chart("1,\n#BRANCHSTART r,1,2\n#N\n2,\n#E\n2,\n#M\n2,\n#BRANCHEND\n3,\n10\n#GOGOSTART\n20, // loud\n4,").Replace("\n", "\r\n"),
            Apply(crowded, Tja.Editing.InsertBranch(c, c.Measures[1], "r,1,2")));
        Assert.Equal(Chart("1,2,3,\n#BRANCHSTART r,1,2\n#N\n10\n#GOGOSTART\n20,\n#E\n10\n#GOGOSTART\n20,\n#M\n10\n#GOGOSTART\n20,\n#BRANCHEND\n // loud\n4,").Replace("\n", "\r\n"),
            Apply(crowded, Tja.Editing.InsertBranch(c, c.Measures[3], "r,1,2")));
    }

    [Fact]
    public void ANewBranchIsRefusedWhereItWouldNotPlayTheSame()
    {
        // Inside a branch.
        var a = Tja.Analyze(Chart("1,\n#BRANCHSTART p,1,2\n#N\n1,\n#E\n2,\n#BRANCHEND\n1,"), 0);
        var inside = Tja.Editing.InsertBranch(a, a.Measures.First(m => m.BranchId >= 0), "p,1,2");
        Assert.False(inside.Ok);
        Assert.Empty(inside.Edits);
        Assert.True(Tja.Editing.InsertBranch(a, a.Measures[0], "p,1,2").Ok);
        // A measure that is not ended: nothing to write the branch's end after.
        var open = Tja.Analyze(Chart("1,\n2"), 0);
        Assert.False(Tja.Editing.InsertBranch(open, open.Measures[^1], "p,1,2").Ok);
    }

    [Fact]
    public void ANewBranchKeepsTheBalloonCounts()
    {
        string head = "BPM:120\nCOURSE:Oni\nLEVEL:5\n";
        string body = "#START\n7008,\n7008,\n7008,\n#END\n";
        // One list, which only the normal route reads: the copies get lists of their own.
        string text = head + "BALLOON:10,11,12\n" + body;
        var a = Tja.Analyze(text, 0);
        string branched = Apply(text, Tja.Editing.InsertBranch(a, a.Measures[1], "p,60,85"));
        var b = Tja.Analyze(branched, 0);
        Assert.False(b.HasErrors);
        Assert.Equal(new[] { 10, 11, 11, 11, 12 }, b.Chart!.Notes.Select(n => n.BalloonHits));
        Assert.Contains("\nBALLOON:10,11,12\n", branched);

        // Lists for every route already: each keeps its own counts.
        string three = head + "BALLOON:10,11,12\nBALLOONEXP:20,21,22\nBALLOONMAS:30,31,32\n" + body;
        var c = Tja.Analyze(three, 0);
        var before = c.Chart!.Notes.Select(n => n.BalloonHits).ToList();
        var d = Tja.Analyze(Apply(three, Tja.Editing.InsertBranch(c, c.Measures[1], "p,60,85")), 0);
        Assert.False(d.HasErrors);
        foreach (var route in new[] { BranchRoute.Normal, BranchRoute.Expert, BranchRoute.Master })
            Assert.Equal(before, d.Chart!.Notes.Where(n => n.BranchId < 0 || n.Route == route).Select(n => n.BalloonHits));
    }

    [Fact]
    public void ABranchConditionIsTakenApartAndWritten()
    {
        Assert.Equal(new[] { "p", "pp", "jp", "d", "jg", "jb", "r", "rb", "s" }, Tja.BranchConditions.Select(k => k.Key));
        Assert.All(Tja.BranchConditions, k => { Assert.NotEmpty(k.Name); Assert.NotEmpty(k.Description); });
        // Big-only cannot be asked of d (always) or s (never).
        Assert.Equal(new[] { "d", "s" }, Tja.BranchConditions.Where(k => k.BigOnly == null).Select(k => k.Key));

        Assert.Equal(new BranchConditionText("p", "60", "85"), Tja.ReadBranchCondition("p, 60,85"));
        Assert.Equal(new BranchConditionText("r", "1", "2", BigOnly: true, Less: true), Tja.ReadBranchCondition("R,1,2,l"));
        Assert.Equal(new BranchConditionText("p", "50", "80.5"), Tja.ReadBranchCondition("p50,80.5,m"));
        Assert.Equal(new BranchConditionText("s", "1000", "2000"), Tja.ReadBranchCondition("S,1000,2000"));
        foreach (string unreadable in new[] { "", "p,60", "x,1,2", "p,a,b", "p,1,2,z" }) Assert.Null(Tja.ReadBranchCondition(unreadable));

        Assert.Equal("p,60,85", Tja.WriteBranchCondition(new BranchConditionText("p", "60", " 85 ")));
        Assert.Equal("RB,3,9,l", Tja.WriteBranchCondition(new BranchConditionText("rb", "3", "9", BigOnly: true, Less: true)));
        Assert.Equal("s,1,2", Tja.WriteBranchCondition(new BranchConditionText("s", "1", "2", BigOnly: true)));
        // What is written is what the parser reads.
        var a = Tja.Analyze(Chart("1,\n#BRANCHSTART " + Tja.WriteBranchCondition(new BranchConditionText("jg", "3", "9", BigOnly: true)) + "\n#N\n1,\n#BRANCHEND"), 0);
        var read = a.Chart!.Branches[0];
        Assert.Equal((BranchCondition.JudgeGood, true, 3.0, 9.0), (read.Condition, read.BigOnly, read.ExpertThreshold(BranchRoute.Normal), read.MasterThreshold(BranchRoute.Normal)));
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
