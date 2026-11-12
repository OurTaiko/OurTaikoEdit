using System.Globalization;
using OurTaikoEdit.Core.Languages;
using OurTaikoEdit.Core.Localization;
using OurTaiko;

namespace OurTaikoEdit.Tja;

// Timeline edits as TJA text edits. A note goes into its measure's slot when the grid position is
// on one; otherwise every slot of the measure is split evenly (`1020,` → `10002000,`) so that it
// is, keeping commands and line breaks where they are. Removing writes `0`. Balloon hit lists
// (BALLOON, BALLOONNOR/EXP/MAS) keep each remaining balloon's count, a new one gets 5.
sealed class TjaEditing : IChartEditing
{
    // The most slots a measure may get from splitting.
    const int MaxSlots = 384;
    const int DefaultBalloon = 5;

    static char CharOf(NoteKind kind) => kind switch
    {
        NoteKind.Don => '1', NoteKind.Ka => '2', NoteKind.BigDon => '3', NoteKind.BigKa => '4',
        NoteKind.Roll => '5', NoteKind.BigRoll => '6', NoteKind.Balloon => '7', NoteKind.Kusudama => '9',
        NoteKind.Mine => 'C', NoteKind.AdLib => 'F', NoteKind.Kadon => 'G',
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    static bool IsLong(NoteKind kind) => kind is NoteKind.Roll or NoteKind.BigRoll or NoteKind.Balloon or NoteKind.Kusudama;
    static bool IsBalloonChar(char c) => c is '7' or '9';

    // A character to write at a place of a measure. Weak ones (clearing a removed roll's end) give
    // way to any other at the same place.
    sealed record Placement(MeasureView Measure, long Num, long Den, char Char, bool Weak = false);

    public EditResult Edit(ChartAnalysis analysis, IReadOnlyList<ChartNote> remove, IReadOnlyList<NotePlacement> place)
    {
        if (analysis.Chart == null || analysis.HasErrors) return EditResult.Fail(Loc.T("FixErrorsTimeline"));
        var placements = new List<Placement>();
        var removed = new HashSet<ChartNote>(remove);

        // Removals first: a placement at the same place then wins.
        foreach (var note in remove)
        {
            var found = analysis.Measures.SelectMany(m => m.Slots.Select((s, i) => (m, i, s))).FirstOrDefault(x => ReferenceEquals(x.s.Note, note));
            if (found.m == null) return EditResult.Fail(Loc.T("NoteNotInText"));
            placements.Add(new Placement(found.m, found.i, found.m.Slots.Count, '0'));
            if (note.IsLong) ClearTail(analysis, note, placements);
        }

        // The rolls being placed, by route, to keep notes out of their bodies.
        var newRolls = new List<(BranchRoute Route, double Start, double End)>();
        foreach (var p in place)
            if (IsLong(p.Kind) && p.End is { } e) newRolls.Add((RouteOf(p.At), p.At.Time, e.Time));

        foreach (var p in place)
        {
            var at = p.At;
            if (at.Steps <= 0 || at.Step < 0 || at.Step >= at.Steps) return EditResult.Fail(Loc.T("OutsideMeasure"));
            var route = RouteOf(at);
            double start = at.Time;
            // A note inside a roll would end it early; the roll has to go first.
            if (RollAround(analysis, start, route, removed) is { } roll)
                return EditResult.Fail(Loc.F("InsideRoll", Describe(roll)));
            if (newRolls.Any(r => r.Route == route && r.Start < start && start <= r.End))
                return EditResult.Fail(Loc.T("NoteInsidePlacedRoll"));
            placements.Add(Place(at, CharOf(p.Kind)));
            if (!IsLong(p.Kind)) continue;

            if (p.End is not { } end || end.Steps <= 0) return EditResult.Fail(Loc.T("RollNeedsEnd"));
            if (end.Step >= end.Steps) end = end with { Step = end.Steps - 1 };
            if (!(end.Time > start)) return EditResult.Fail(Loc.T("RollEndAfterStart"));
            if (Crosses(analysis, at.Measure, end.Measure)) return EditResult.Fail(Loc.T("RollAcrossBranch"));
            if (RollAround(analysis, end.Time, route, removed) is { } other && other.Time != start)
                return EditResult.Fail(Loc.F("EndInsideRoll", Describe(other)));
            placements.Add(Place(end, '8'));
            // What lies under the new roll would end it: it is cleared (weakly, so notes placed
            // with the roll stay), with the ends of rolls cleared.
            foreach (var (measure, i, slot) in SlotsOn(analysis, route))
            {
                double t = slot.Time;
                if (t <= start || t >= end.Time || slot.Token == "0") continue;
                placements.Add(new Placement(measure, i, measure.Slots.Count, '0', Weak: true));
                if (slot.Note is { IsLong: true } cleared) ClearTail(analysis, cleared, placements);
            }
        }

        // A roll replaced at its head loses its end.
        foreach (var p in placements.ToList())
            if (!p.Weak && p.Char != '8' && SlotOf(p) is { Note: { IsLong: true } replaced } && !removed.Contains(replaced))
                ClearTail(analysis, replaced, placements);

        return Apply(analysis, placements);
    }

    // ---- Timing commands.

    static string CommandOf(ChartEventKind kind, string? value) => kind switch
    {
        ChartEventKind.Tempo => "#BPMCHANGE " + value,
        ChartEventKind.Scroll => "#SCROLL " + value,
        ChartEventKind.Measure => "#MEASURE " + value,
        ChartEventKind.Delay => "#DELAY " + value,
        ChartEventKind.GogoStart => "#GOGOSTART",
        ChartEventKind.GogoEnd => "#GOGOEND",
        ChartEventKind.BarlineOff => "#BARLINEOFF",
        ChartEventKind.BarlineOn => "#BARLINEON",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    static string[] Lines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    static string Newline(string text) => text.Contains("\r\n") ? "\r\n" : "\n";

    public EditResult InsertEvent(ChartAnalysis analysis, GridPosition at, ChartEventKind kind, string? value)
    {
        if (analysis.Chart == null || analysis.HasErrors) return EditResult.Fail(Loc.T("FixErrorsFirst"));
        if (kind is ChartEventKind.Tempo or ChartEventKind.Scroll or ChartEventKind.Measure or ChartEventKind.Delay && string.IsNullOrWhiteSpace(value))
            return EditResult.Fail(Loc.T("CommandNeedsValue"));
        return Insert(analysis, at, CommandOf(kind, value?.Trim()));
    }

    // A command line written so that it takes effect at a grid position.
    static EditResult Insert(ChartAnalysis analysis, GridPosition at, string command)
    {
        if (command.StartsWith("#MEASURE", StringComparison.OrdinalIgnoreCase) && at.Step != 0) return EditResult.Fail(Loc.T("MeasureAtStart"));
        string nl = Newline(analysis.Text);
        var lines = Lines(analysis.Text);
        var m = at.Measure;
        int n = m.Slots.Count;
        if (n == 0)
        {
            if (m.Comma.IsEmpty) return EditResult.Fail(Loc.T("MeasureNoComma"));
            return new EditResult(new[] { BeforeOnLine(lines, m.Comma, command, nl) });
        }
        long g = Gcd(at.Step, at.Steps), num = at.Step / g, den = at.Steps / g;
        long total = Lcm(n, den);
        if (total > MaxSlots) return EditResult.Fail(Loc.F("TooManySlots", total));
        int factor = (int)(total / n);
        long target = num * total / den;
        int j = (int)(target / factor), r = (int)(target % factor);
        if (factor == 1) return new EditResult(new[] { BeforeOnLine(lines, m.Slots[j].Source, command, nl) });
        // Between slots: split the measure and break the line at the place.
        var edits = new List<TextEdit>();
        for (int i = 0; i < n; i++)
        {
            var units = new List<string> { m.Slots[i].Token };
            units.AddRange(Enumerable.Repeat("0", factor - 1));
            if (i == j) units.Insert(r, nl + command + nl);
            var slot = m.Slots[i].Source;
            edits.Add(new TextEdit(slot.Line, slot.Column, slot.Length, string.Concat(units)));
        }
        return new EditResult(edits);
    }

    // A command line before what is written at a place: on its own line above it when it starts
    // its line, else breaking the line there.
    static TextEdit BeforeOnLine(string[] lines, SourceSpan at, string command, string nl)
    {
        string line = lines[at.Line - 1];
        bool first = line.Substring(0, Math.Min(at.Column - 1, line.Length)).Trim().Length == 0;
        return first ? new TextEdit(at.Line, 1, 0, command + nl) : new TextEdit(at.Line, at.Column, 0, nl + command + nl);
    }

    public EditResult RemoveCommand(ChartAnalysis analysis, ChartCommand command)
    {
        var span = command.Source;
        if (span.IsEmpty) return EditResult.Fail(Loc.T("CommandNotInText"));
        var lines = Lines(analysis.Text);
        string line = lines[span.Line - 1];
        // A command alone on its line goes with its line.
        if (line.Trim().Length <= span.Length || span.Length == 0)
        {
            int newline = span.Line < lines.Length ? Newline(analysis.Text).Length : 0;
            return new EditResult(new[] { new TextEdit(span.Line, 1, line.Length + newline, "") });
        }
        return new EditResult(new[] { new TextEdit(span.Line, span.Column, span.Length, "") });
    }

    public EditResult ChangeCommand(ChartAnalysis analysis, ChartCommand command, string value)
    {
        var span = command.Source;
        if (span.IsEmpty) return EditResult.Fail(Loc.T("CommandNotInText"));
        string line = Lines(analysis.Text)[span.Line - 1];
        int length = span.Length > 0 ? span.Length : line.Length - (span.Column - 1);
        return new EditResult(new[] { new TextEdit(span.Line, span.Column, length, command.Name + " " + value.Trim()) });
    }

    public EditResult MoveCommand(ChartAnalysis analysis, ChartCommand command, GridPosition to)
    {
        if (analysis.Chart == null || analysis.HasErrors) return EditResult.Fail(Loc.T("FixErrorsFirst"));
        if (Math.Abs(to.Time - command.Time) < 1e-6) return new EditResult(Array.Empty<TextEdit>());
        var insert = Insert(analysis, to, (command.Name + " " + command.Value).Trim());
        if (!insert.Ok) return insert;
        var remove = RemoveCommand(analysis, command);
        if (!remove.Ok) return remove;
        // The removed line and the place written to never overlap: commands have lines of their own.
        return new EditResult(remove.Edits.Concat(insert.Edits).ToList());
    }

    // A branch taken out: the line of its #BRANCHSTART, the lines of its route markers, every
    // route but the one kept (from its marker to the next marker or the branch's end) and its
    // #BRANCHEND go; the kept route's lines stay where they are, now played by every route. The
    // balloon lists follow. (Commands have lines of their own, so whole lines are removed.)
    public EditResult RemoveBranch(ChartAnalysis analysis, ChartBranchMark branch, BranchRoute keep)
    {
        if (analysis.Chart == null || analysis.HasErrors) return EditResult.Fail(Loc.T("FixErrorsTimeline"));
        if (branch.Start.Source.IsEmpty) return EditResult.Fail(Loc.T("CommandNotInText"));
        var doc = new TjaDocument(analysis.Text);
        var index = new LineIndex(analysis.Text);
        var tokens = doc.Syntax;
        int startLine = branch.Start.Source.Line, startOffset = index.Offset(startLine, branch.Start.Source.Column);

        // The branch's markers in the order written, and the line of what ends it: its
        // #BRANCHEND (which goes too), or the next #BRANCHSTART or #END (which stay).
        var markers = new List<(BranchRoute Route, int Line)>();
        int endLine = 0;
        bool ownEnd = false;
        foreach (var token in tokens)
        {
            if (token.Kind != TjaSyntaxKind.Command || token.Span.Start <= startOffset) continue;
            string name = analysis.Text.Substring(token.Span.Start, token.Span.Length).ToUpperInvariant();
            int line = index.Position(token.Span.Start).Line;
            if (name is "#N" or "#E" or "#M") markers.Add((name == "#N" ? BranchRoute.Normal : name == "#E" ? BranchRoute.Expert : BranchRoute.Master, line));
            else if (name is "#BRANCHEND" or "#BRANCHSTART" or "#END")
            {
                endLine = line;
                ownEnd = name == "#BRANCHEND";
                break;
            }
        }
        if (endLine == 0) return EditResult.Fail(Loc.T("CommandNotInText"));
        if (!markers.Any(m => m.Route == keep)) return EditResult.Fail(Loc.F("BranchRouteNotWritten", Loc.T(keep switch
        {
            BranchRoute.Expert => "RouteExpert", BranchRoute.Master => "RouteMaster", _ => "RouteNormal",
        })));

        // Whole lines, first to last, joined where they touch.
        var ranges = new List<(int First, int Last)>();
        void Remove(int first, int last)
        {
            if (ranges.Count > 0 && ranges[^1].Last + 1 >= first) ranges[^1] = (ranges[^1].First, Math.Max(ranges[^1].Last, last));
            else ranges.Add((first, last));
        }
        Remove(startLine, startLine);
        for (int i = 0; i < markers.Count; i++)
        {
            int next = i + 1 < markers.Count ? markers[i + 1].Line : endLine;
            Remove(markers[i].Line, markers[i].Route == keep ? markers[i].Line : next - 1);
        }
        if (ownEnd) Remove(endLine, endLine);

        var edits = new List<TextEdit>();
        foreach (var (first, last) in ranges)
        {
            // Up to the start of the line after (the last of these lines is never the text's last:
            // the block's #END follows).
            int from = index.Offset(first, 1), to = last < index.LineCount ? index.Offset(last + 1, 1) : analysis.Text.Length;
            edits.Add(new TextEdit(first, 1, to - from, ""));
        }
        edits.AddRange(Balloons.EditsWithout(analysis, branch.Id, keep));
        return new EditResult(edits);
    }

    // OFFSET is how long before the song's start the chart starts, so the first measure moves
    // later by as much as OFFSET gets smaller. The OFFSET in force for the block is the last one
    // written before it; without one it is written after BPM (or first of all). The value is a
    // whole number of milliseconds, as the game reads it. The text is read again to see that the
    // measure is where it was asked to be.
    public EditResult MoveStart(ChartAnalysis analysis, double time)
    {
        if (analysis.Measures.Count == 0) return EditResult.Fail(Loc.T("NoMeasureToPlace"));
        string text = analysis.Text;
        double first = analysis.Measures.Min(m => m.Time);
        int offsetLine = 0, bpmLine = 0;
        string written = "";
        foreach (var l in TjaLines.Read(text))
        {
            if (l.Line >= analysis.StartLine) break;
            if (l.Kind != TjaLineKind.Header) continue;
            if (l.Key == "OFFSET") (offsetLine, written) = (l.Line, l.Value);
            else if (l.Key == "BPM") bpmLine = l.Line;
        }
        int comment = written.IndexOf("//", StringComparison.Ordinal);
        if (comment >= 0) written = written[..comment];
        // One that cannot be read is 0 to the parser.
        if (!TjaNumber.TryParse(written, out double offset)) offset = 0;
        double value = Math.Round((offset - (time - first)) * 1000) / 1000;
        string number = value == 0 ? "0" : value.ToString("0.###", CultureInfo.InvariantCulture);

        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        string newline = text.Contains("\r\n") ? "\r\n" : "\n";
        TextEdit edit;
        if (offsetLine > 0)
        {
            string raw = lines[offsetLine - 1];
            int colon = raw.IndexOf(':'), end = raw.IndexOf("//", colon + 1, StringComparison.Ordinal);
            if (end < 0) end = raw.Length;
            while (end > colon + 1 && char.IsWhiteSpace(raw[end - 1])) end--;
            if (raw[(colon + 1)..end].Trim() == number) return new EditResult(Array.Empty<TextEdit>());
            edit = new TextEdit(offsetLine, colon + 2, end - colon - 1, number);
        }
        else if (bpmLine > 0) edit = new TextEdit(bpmLine, lines[bpmLine - 1].Length + 1, 0, newline + "OFFSET:" + number);
        else edit = new TextEdit(1, 1, 0, "OFFSET:" + number + newline);

        var after = new TjaLanguage().Analyze(TextEdits.Apply(text, new[] { edit }), analysis.Block);
        if (after.Measures.Count == 0 || Math.Abs(after.Measures.Min(m => m.Time) - time) > 0.0015) return EditResult.Fail(Loc.T("StartNotMoved"));
        return new EditResult(new[] { edit });
    }

    // ---- A new block.

    static readonly HashSet<string> TempoCommands = new(StringComparer.OrdinalIgnoreCase) { "#BPMCHANGE", "#MEASURE", "#DELAY" };
    static readonly HashSet<string> GogoCommands = new(StringComparer.OrdinalIgnoreCase) { "#GOGOSTART", "#GOGOEND" };
    static readonly HashSet<string> RouteMarkers = new(StringComparer.OrdinalIgnoreCase) { "#N", "#E", "#M" };
    static readonly HashSet<string> BranchCommands = new(StringComparer.OrdinalIgnoreCase) { "#BRANCHSTART", "#BRANCHEND", "#N", "#E", "#M", "#SECTION", "#LEVELHOLD" };

    // Append a course with its own level and balloon counts, then check that it parses.
    // An inherited block must also preserve the requested measure timing.
    public EditResult InsertBlock(ChartAnalysis analysis, NewBlock block)
    {
        string text = analysis.Text, newline = text.Contains("\r\n") ? "\r\n" : "\n";
        if (SongInfo.DifficultyOf(block.Course) is not { } difficulty || difficulty < Difficulty.Easy) return EditResult.Fail(Loc.F("CourseExists", block.Course));
        string course = SongInfo.CourseName(difficulty);
        var doc = new TjaDocument(text);
        if (doc.Blocks.Any(b => b.Side == "single" && SongInfo.DifficultyOf(b.Course) == difficulty)) return EditResult.Fail(Loc.F("CourseExists", course));

        List<string> body;
        TjaAnalysis? source = null;
        bool everyBranchHasNormal = true;
        if (block.Imported is { } imported)
        {
            body = new List<string> { $"#BPMCHANGE {imported.Bpm.ToString(CultureInfo.InvariantCulture)}" };
            body.AddRange(imported.Body);
        }
        else if (block.From >= 0)
        {
            if (block.From >= doc.Blocks.Count) return EditResult.Fail(Loc.T("FixErrorsFirst"));
            var from = doc.Blocks[block.From];
            source = doc.Analyze(from.Course, from.Side);
            if (source.Chart == null || source.HasErrors) return EditResult.Fail(Loc.T("FixErrorsFirst"));
            body = Skeleton(doc, from, block, out everyBranchHasNormal);
        }
        else body = Enumerable.Repeat(",", 4).ToList();

        string balloonHits = block.Imported == null ? "" : string.Join(",", block.Imported.BalloonHits);
        var lines = new List<string> { "", $"COURSE:{course}", $"LEVEL:{Math.Clamp(block.Level, 1, 10)}", $"BALLOON:{balloonHits}", "", "#START" };
        lines.AddRange(body);
        lines.Add("#END");
        lines.Add("");
        string written = (text.Length == 0 || text.EndsWith('\n') || text.EndsWith('\r') ? "" : newline) + string.Join(newline, lines);
        var (line, column) = new LineIndex(text).Position(text.Length);
        var edit = new TextEdit(line, column, 0, written);

        var after = new TjaDocument(TextEdits.Apply(text, new[] { edit })).Analyze(course, "single");
        if (after.Chart == null || after.HasErrors)
            return EditResult.Fail(Loc.F(block.Imported == null ? "BlockNotCarried" : "BlockImportInvalid", course));
        if (source != null && block.Tempo)
        {
            // What the new block has of the other's: every measure, or without the branches, the
            // common ones and the normal route's.
            bool branching = source.Chart!.Branches.Count > 0;
            var expected = source.Measures.Where(m => block.Branches || m.BranchId < 0 || m.Route == BranchRoute.Normal).ToList();
            if (block.Branches || !branching || everyBranchHasNormal)
            {
                if (after.Measures.Count != expected.Count) return EditResult.Fail(Loc.F("BlockNotCarried", course));
                for (int i = 0; i < expected.Count; i++)
                    if (Math.Abs(after.Measures[i].StartTime - expected[i].StartTime) > 0.0015 || Math.Abs(after.Measures[i].EndTime - expected[i].EndTime) > 0.0015)
                        return EditResult.Fail(Loc.F("BlockNotCarried", course));
            }
        }
        return new EditResult(new[] { edit });
    }

    // A block's lines between #START and #END with nothing to play: each measure is a comma, and
    // the commands asked for stand where they stood. A command inside a measure keeps its place
    // by the measure being written as rests around it, as few as hold the place (a tempo change
    // half-way through sixteen notes becomes `0`, the command, `0,`). Other commands, comments
    // and the notes are left out. Without the branches, of each branch only the normal route is
    // written (the first route written, where there is no normal one), as what every route plays.
    static List<string> Skeleton(TjaDocument doc, TjaDocumentBlock block, NewBlock options, out bool everyBranchHasNormal)
    {
        var tokens = doc.Syntax;
        var lines = new List<string>();
        var pending = new List<(int At, string Text)>();
        int slots = 0;
        bool inBranch = false;
        string? route = null, kept = null;
        everyBranchHasNormal = true;

        string Name(int i) => doc.Text.Substring(tokens[i].Span.Start, tokens[i].Span.Length).ToUpperInvariant();

        // The route of the branch starting at a token that is written out: #N, or the first written.
        string? KeptRoute(int start)
        {
            string? first = null;
            for (int i = start + 1; i < tokens.Count && tokens[i].Span.Start < block.EndSpan.Start; i++)
            {
                if (tokens[i].Kind != TjaSyntaxKind.Command) continue;
                string name = Name(i);
                if (name is "#BRANCHSTART" or "#BRANCHEND") break;
                if (!RouteMarkers.Contains(name)) continue;
                if (name == "#N") return name;
                first ??= name;
            }
            return first;
        }

        void Flush()
        {
            int unit = slots;
            foreach (var c in pending)
                if (c.At > 0 && c.At < slots) unit = Gcd(unit, c.At);
            int written = 0;
            foreach (var c in pending)
            {
                if (c.At > written) { lines.Add(new string('0', (c.At - written) / unit)); written = c.At; }
                lines.Add(c.Text);
            }
            lines.Add((written > 0 && slots > written ? new string('0', (slots - written) / unit) : "") + ",");
            pending.Clear();
            slots = 0;
        }

        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Span.Start < block.StartSpan.End) continue;
            if (t.Span.Start >= block.EndSpan.Start) break;
            // Without the branches, the other routes are passed over whole.
            bool skipped = !options.Branches && inBranch && route != null && route != kept;
            switch (t.Kind)
            {
                case TjaSyntaxKind.Note:
                case TjaSyntaxKind.Blank:
                case TjaSyntaxKind.RollEnd:
                case TjaSyntaxKind.Unknown:
                    if (!skipped) slots++;
                    break;
                case TjaSyntaxKind.Comma:
                    if (!skipped) Flush();
                    break;
                case TjaSyntaxKind.Command:
                    string name = Name(i);
                    // The command as written, with what follows it on its line.
                    int end = t.Span.End;
                    while (i + 1 < tokens.Count && tokens[i + 1].Kind is TjaSyntaxKind.Argument or TjaSyntaxKind.Whitespace)
                    {
                        if (tokens[i + 1].Kind == TjaSyntaxKind.Argument) end = tokens[i + 1].Span.End;
                        i++;
                    }
                    if (name == "#BRANCHSTART")
                    {
                        (inBranch, route, kept) = (true, null, KeptRoute(i));
                        if (kept != "#N") everyBranchHasNormal = false;
                        skipped = false;
                    }
                    else if (name == "#BRANCHEND") (inBranch, route, skipped) = (false, null, false);
                    else if (RouteMarkers.Contains(name)) { route = name; skipped = !options.Branches && route != kept; }
                    bool wanted = TempoCommands.Contains(name) ? options.Tempo
                        : GogoCommands.Contains(name) ? options.Gogo
                        : BranchCommands.Contains(name) && options.Branches;
                    if (wanted && !skipped) pending.Add((slots, doc.Text.Substring(t.Span.Start, end - t.Span.Start).TrimEnd()));
                    break;
            }
        }
        // Commands after the last measure.
        lines.AddRange(pending.Select(c => c.Text));
        return lines;

        static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);
    }

    // A branch made of one measure: the measure as written (from its first note to its comma,
    // with whatever is written inside it) becomes the normal route and is written again as the
    // expert and the master one, between a #BRANCHSTART and a #BRANCHEND, each on a line of its
    // own. Commands written before the measure stay before the branch. The balloon lists give
    // the copies the counts their balloons had. Refused where the result would not play as the
    // chart does now on every route (a drumroll across the measure's ends, for one).
    public EditResult InsertBranch(ChartAnalysis analysis, MeasureView measure, string condition)
    {
        if (analysis.Chart == null || analysis.HasErrors) return EditResult.Fail(Loc.T("FixErrorsTimeline"));
        if (measure.BranchId >= 0) return EditResult.Fail(Loc.T("BranchInsideBranch"));
        if (measure.Comma.IsEmpty) return EditResult.Fail(Loc.T("MeasureNoCommaBefore"));
        string text = analysis.Text, nl = Newline(text);
        var index = new LineIndex(text);
        var first = measure.Slots.Count > 0 ? measure.Slots[0].Source : measure.Comma;
        int start = index.Offset(first.Line, first.Column), end = index.Offset(measure.Comma.Line, measure.Comma.Column) + measure.Comma.Length;
        if (end <= start) return EditResult.Fail(Loc.T("MeasureNoCommaBefore"));
        string written = text.Substring(start, end - start);

        // From the start of the line when only blanks come before the measure there; a line of
        // its own for what follows the comma, when anything does.
        int lineStart = index.Offset(first.Line, 1), lineEnd = end;
        while (lineEnd < text.Length && text[lineEnd] != '\r' && text[lineEnd] != '\n') lineEnd++;
        bool startsLine = string.IsNullOrWhiteSpace(text.Substring(lineStart, start - lineStart));
        bool endsLine = string.IsNullOrWhiteSpace(text.Substring(end, lineEnd - end));
        int from = startsLine ? lineStart : start;
        string branch = (startsLine ? "" : nl) + "#BRANCHSTART " + condition.Trim() + nl
            + "#N" + nl + written + nl + "#E" + nl + written + nl + "#M" + nl + written + nl + "#BRANCHEND" + (endsLine ? "" : nl);
        var (line, column) = index.Position(from);
        var edits = new List<TextEdit> { new(line, column, end - from, branch) };
        edits.AddRange(Balloons.EditsForCopies(analysis, measure));

        // Read again: every route must play what it played.
        if (analysis.Block < 0 || analysis.Block >= analysis.Blocks.Count) return new EditResult(edits);
        var block = analysis.Blocks[analysis.Block];
        var after = new TjaDocument(TextEdits.Apply(text, edits)).Analyze(block.Course, block.Side);
        if (after.Chart == null || after.HasErrors) return EditResult.Fail(Loc.T("BranchChangesChart"));
        foreach (var route in new[] { BranchRoute.Normal, BranchRoute.Expert, BranchRoute.Master })
            if (!Played(analysis.Chart, route).SequenceEqual(Played(after.Chart, route))) return EditResult.Fail(Loc.T("BranchChangesChart"));
        return new EditResult(edits);
    }

    // What a route plays: each note with when, how long, how fast and how many hits.
    static IEnumerable<(NoteKind, double, double, double, double, double, int)> Played(TaikoChart chart, BranchRoute route)
        => chart.Notes.Where(n => n.BranchId < 0 || n.BranchId < chart.Branches.Count && chart.Branches[n.BranchId].ResolveRoute(route) == n.Route)
            .Select(n => (n.Kind, Math.Round(n.Time, 4), Math.Round(n.EndTime, 4), n.Bpm, n.ScrollX, n.ScrollY, n.BalloonHits));

    static string Describe(ChartNote n) => Loc.T(n.IsBalloon ? "DescribeBalloon" : "DescribeRoll");

    static BranchRoute RouteOf(GridPosition at) => at.Measure.BranchId >= 0 ? at.Measure.Route : at.Route;

    static Placement Place(GridPosition at, char c)
    {
        long g = Gcd(at.Step, at.Steps);
        return new Placement(at.Measure, at.Step / g, at.Steps / g, c);
    }

    static ChartSlot? SlotOf(Placement p)
    {
        int n = p.Measure.Slots.Count;
        if (n == 0 || p.Num * n % p.Den != 0) return null;
        return p.Measure.Slots[(int)(p.Num * n / p.Den)];
    }

    // The measures' slots on a route, in the order written.
    static IEnumerable<(MeasureView Measure, int Index, ChartSlot Slot)> SlotsOn(ChartAnalysis analysis, BranchRoute route)
        => analysis.Measures.Where(m => m.IsOn(route)).SelectMany(m => m.Slots.Select((s, i) => (m, i, s)));

    // A roll on the route, not being removed, whose body (after its head, up to its end) contains the time.
    static ChartNote? RollAround(ChartAnalysis analysis, double time, BranchRoute route, HashSet<ChartNote> removed)
        => SlotsOn(analysis, route).Select(x => x.Slot.Note).FirstOrDefault(n => n is { IsLong: true } && !removed.Contains(n) && n.Time < time && time <= n.EndTime);

    // Whether the stretch between two measures leaves or enters a branch.
    static bool Crosses(ChartAnalysis analysis, MeasureView a, MeasureView b)
    {
        if (a.BranchId != b.BranchId || a.Route != b.Route && a.BranchId >= 0) return true;
        int from = Order(analysis, a), to = Order(analysis, b);
        return a.BranchId < 0 && analysis.Measures.Where(m => Order(analysis, m) > from && Order(analysis, m) < to).Any(m => m.BranchId >= 0);
    }

    static int Order(ChartAnalysis analysis, MeasureView m) => IndexOf(analysis.Measures, m);

    static int IndexOf(IReadOnlyList<MeasureView> list, MeasureView m)
    {
        for (int i = 0; i < list.Count; i++) if (ReferenceEquals(list[i], m)) return i;
        return -1;
    }

    // Clears a long note's 8 where it is a slot (an end by a note or #END needs nothing).
    static void ClearTail(ChartAnalysis analysis, ChartNote note, List<Placement> placements)
    {
        var tail = analysis.TailOf(note);
        if (tail.IsEmpty) return;
        foreach (var m in analysis.Measures)
            for (int i = 0; i < m.Slots.Count; i++)
                if (m.Slots[i].Source == tail && m.Slots[i].Token == "8")
                {
                    placements.Add(new Placement(m, i, m.Slots.Count, '0', Weak: true));
                    return;
                }
    }

    EditResult Apply(ChartAnalysis analysis, List<Placement> placements)
    {
        // At one place the last strong placement wins over weak ones.
        var final = new List<Placement>();
        foreach (var p in placements)
        {
            int same = final.FindIndex(q => ReferenceEquals(q.Measure, p.Measure) && q.Num * p.Den == p.Num * q.Den);
            if (same < 0) final.Add(p);
            else if (!p.Weak) final[same] = p;
        }

        var edits = new List<TextEdit>();
        foreach (var group in final.GroupBy(p => p.Measure))
        {
            var result = EditMeasure(group.Key, group.ToList());
            if (!result.Ok) return result;
            edits.AddRange(result.Edits);
        }
        if (edits.Count == 0) return new EditResult(edits);
        edits.AddRange(Balloons.Edits(analysis, final, IsBalloonChar));
        return new EditResult(edits);
    }

    static EditResult EditMeasure(MeasureView measure, List<Placement> placements)
    {
        int n = measure.Slots.Count;
        if (n == 0)
        {
            if (measure.Comma.IsEmpty) return EditResult.Fail(Loc.T("MeasureNoCommaBefore"));
            long size = placements.Aggregate(1L, (l, p) => Lcm(l, p.Den));
            if (size > MaxSlots) return EditResult.Fail(Loc.F("TooManySlots", size));
            var chars = Enumerable.Repeat('0', (int)size).ToArray();
            foreach (var p in placements) chars[p.Num * size / p.Den] = p.Char;
            if (chars.All(c => c == '0')) return new EditResult(Array.Empty<TextEdit>());
            return new EditResult(new[] { new TextEdit(measure.Comma.Line, measure.Comma.Column, measure.Comma.Length, new string(chars) + ",") });
        }

        long total = placements.Aggregate((long)n, (l, p) => Lcm(l, p.Den));
        if (total > MaxSlots) return EditResult.Fail(Loc.F("TooManySlots", total));
        int factor = (int)(total / n);
        // Each slot becomes its token followed by factor-1 blanks.
        var texts = measure.Slots.Select(s => new List<string> { s.Token }.Concat(Enumerable.Repeat("0", factor - 1)).ToList()).ToList();
        foreach (var p in placements)
        {
            long at = p.Num * total / p.Den;
            texts[(int)(at / factor)][(int)(at % factor)] = p.Char.ToString();
        }
        var edits = new List<TextEdit>();
        for (int i = 0; i < n; i++)
        {
            string text = string.Concat(texts[i]);
            if (text != measure.Slots[i].Token)
                edits.Add(new TextEdit(measure.Slots[i].Source.Line, measure.Slots[i].Source.Column, measure.Slots[i].Source.Length, text));
        }
        return new EditResult(edits);
    }

    static long Gcd(long a, long b) { while (b != 0) (a, b) = (b, a % b); return Math.Abs(a) == 0 ? 1 : Math.Abs(a); }
    static long Lcm(long a, long b) => a / Gcd(a, b) * b;

    // Keeps the balloon hit lists in step with the balloons: each list is read in the order the
    // balloons are written, a common balloon taking a place in every route's list.
    static class Balloons
    {
        // Which header sets each route's list for the block: line, key and whether it is written
        // in the block's course (not before the first COURSE:, which every course shares).
        sealed record Header(int Line, string Key, List<int>? Values, bool InCourse);

        public static IEnumerable<TextEdit> Edits(ChartAnalysis analysis, List<Placement> placements, Func<char, bool> isBalloon)
        {
            var text = Text(analysis);
            if (text == null) return Array.Empty<TextEdit>();
            var (headers, courseLine) = HeadersFor(text, analysis.StartLine);
            var edits = new List<TextEdit>();
            for (int r = 0; r < 3; r++)
            {
                var route = (BranchRoute)r;
                var header = headers[r];
                if (header == null || header.Values == null) continue; // every balloon reads 5 either way
                // The route's balloons in order, before and after: old ones by their slot.
                var oldOrder = new List<(int Measure, double Fraction, ChartSlot Slot)>();
                var newOrder = new List<(int Measure, double Fraction, ChartSlot? Old)>();
                for (int mi = 0; mi < analysis.Measures.Count; mi++)
                {
                    var m = analysis.Measures[mi];
                    if (!m.IsOn(route)) continue;
                    int n = m.Slots.Count;
                    var here = placements.Where(p => ReferenceEquals(p.Measure, m)).ToList();
                    for (int i = 0; i < n; i++)
                    {
                        var slot = m.Slots[i];
                        bool was = slot.Note?.IsBalloon == true;
                        if (was) oldOrder.Add((mi, (double)i / n, slot));
                        var replaced = here.FirstOrDefault(p => p.Num * n == (long)i * p.Den);
                        if (replaced == null ? was : isBalloon(replaced.Char))
                            newOrder.Add((mi, (double)i / n, was ? slot : null));
                    }
                    foreach (var p in here)
                        if (isBalloon(p.Char) && (n == 0 || p.Num * n % p.Den != 0))
                            newOrder.Add((mi, (double)p.Num / p.Den, null));
                }
                newOrder.Sort((a, b) => a.Measure != b.Measure ? a.Measure.CompareTo(b.Measure) : a.Fraction.CompareTo(b.Fraction));
                var old = header.Values;
                var values = new List<int>();
                int lastExplicit = -1;
                foreach (var (_, _, slot) in newOrder)
                {
                    int k = slot == null ? -1 : oldOrder.FindIndex(o => ReferenceEquals(o.Slot, slot));
                    if (k >= 0 && k < old.Count) { values.Add(old[k]); lastExplicit = values.Count - 1; }
                    else values.Add(DefaultBalloon);
                    if (slot == null) lastExplicit = values.Count - 1;
                }
                values = values.Take(Math.Max(lastExplicit + 1, 0)).ToList();
                Write(text, header, r, values, courseLine, edits);
            }
            return edits;
        }

        // A route's list written as its header, when it changed; a route without a header gets
        // one in the course when it has counts to keep.
        static void Write(string text, Header? header, int r, List<int> values, int courseLine, List<TextEdit> edits)
        {
            if (header == null ? values.Count == 0 : values.SequenceEqual(header.Values!)) return;
            string list = string.Join(",", values.Select(v => v.ToString(CultureInfo.InvariantCulture)));
            if (header is { InCourse: true }) edits.Add(ReplaceValue(text, header.Line, list));
            else if (courseLine > 0)
            {
                // The shared list stays for the other courses; this course gets its own.
                string key = r == 0 ? "BALLOON" : r == 1 ? "BALLOONEXP" : "BALLOONMAS";
                string line = Lines(text)[courseLine - 1];
                edits.Add(new TextEdit(courseLine, line.Length + 1, 0, Newline(text) + key + ":" + list));
            }
        }

        // The lists once a measure every route played is written for each route: its balloons
        // are now the expert and master routes' own, read from those routes' lists at the places
        // they had there, so those lists must hold the counts the balloons have now. Each is
        // written out as its route's balloons are counted today (trailing fives a list did not
        // have are left off: a balloon past its list's end gets five).
        public static IEnumerable<TextEdit> EditsForCopies(ChartAnalysis analysis, MeasureView measure)
        {
            var text = Text(analysis);
            if (text == null || !measure.Slots.Any(s => s.Note?.IsBalloon == true)) return Array.Empty<TextEdit>();
            var (headers, courseLine) = HeadersFor(text, analysis.StartLine);
            var edits = new List<TextEdit>();
            for (int r = 1; r < 3; r++)
            {
                var header = headers[r];
                if (header != null && header.Values == null) continue;
                var values = analysis.Measures.Where(m => m.IsOn((BranchRoute)r)).SelectMany(m => m.Slots)
                    .Where(s => s.Note?.IsBalloon == true).Select(s => s.Note!.BalloonHits).ToList();
                int had = header?.Values!.Count ?? 0;
                while (values.Count > had && values[^1] == DefaultBalloon) values.RemoveAt(values.Count - 1);
                Write(text, header, r, values, courseLine, edits);
            }
            return edits;
        }

        // The lists once a branch is gone and one of its routes is what every route plays there:
        // in each route's list the removed route's balloons give their places to the kept
        // route's, which bring the counts they had in the kept route's own list.
        public static IEnumerable<TextEdit> EditsWithout(ChartAnalysis analysis, int branchId, BranchRoute keep)
        {
            var text = Text(analysis);
            if (text == null) return Array.Empty<TextEdit>();
            var (headers, courseLine) = HeadersFor(text, analysis.StartLine);
            // Each route's balloons in the order written, as its list is read now.
            var order = new List<ChartSlot>[3];
            for (int r = 0; r < 3; r++)
                order[r] = analysis.Measures.Where(m => m.IsOn((BranchRoute)r)).SelectMany(m => m.Slots).Where(s => s.Note?.IsBalloon == true).ToList();
            var edits = new List<TextEdit>();
            for (int r = 0; r < 3; r++)
            {
                var header = headers[r];
                if (header == null || header.Values == null) continue; // every balloon reads 5 either way
                var values = new List<int>();
                int lastExplicit = -1;
                foreach (var m in analysis.Measures)
                {
                    bool inBranch = m.BranchId == branchId;
                    if (inBranch ? m.Route != keep : !m.IsOn((BranchRoute)r)) continue;
                    // A balloon of the kept route was counted by that route's list.
                    int from = inBranch ? (int)keep : r;
                    foreach (var slot in m.Slots)
                    {
                        if (slot.Note?.IsBalloon != true) continue;
                        int k = order[from].FindIndex(s => ReferenceEquals(s, slot));
                        var list = headers[from]?.Values;
                        if (k >= 0 && list != null && k < list.Count) { values.Add(list[k]); lastExplicit = values.Count - 1; }
                        else values.Add(DefaultBalloon);
                    }
                }
                Write(text, header, r, values.Take(lastExplicit + 1).ToList(), courseLine, edits);
            }
            return edits;
        }

        static string? Text(ChartAnalysis analysis) => analysis.Text.Length > 0 ? analysis.Text : null;

        static string[] Lines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        static string Newline(string text) => text.Contains("\r\n") ? "\r\n" : "\n";

        static (Header?[] Headers, int CourseLine) HeadersFor(string text, int blockStart)
        {
            var global = new Header?[3];
            var current = new Header?[3];
            int courseLine = 0;
            bool seenCourse = false;
            foreach (var l in TjaLines.Read(text))
            {
                if (l.Line >= blockStart) break;
                if (l.Kind != TjaLineKind.Header) continue;
                if (l.Key == "COURSE") { seenCourse = true; courseLine = l.Line; Array.Copy(global, current, 3); continue; }
                int r = l.Key switch { "BALLOON" or "BALLOONNOR" => 0, "BALLOONEXP" => 1, "BALLOONMAS" => 2, _ => -1 };
                if (r < 0) continue;
                var header = new Header(l.Line, l.Key, Parse(l.Value), seenCourse);
                current[r] = header;
                if (!seenCourse) global[r] = header;
            }
            return (current, courseLine);
        }

        // As TJAParser reads it: whole numbers up to the first empty item; null when unreadable.
        static List<int>? Parse(string value)
        {
            var hits = new List<int>();
            foreach (string item in value.Split(','))
            {
                if (item.Length == 0) break;
                if (!int.TryParse(item, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) return null;
                hits.Add(n);
            }
            return hits;
        }

        // Replaces a header line's value (after the colon, before a // comment).
        static TextEdit ReplaceValue(string text, int line, string value)
        {
            string raw = Lines(text)[line - 1];
            int colon = raw.IndexOf(':');
            int comment = raw.IndexOf("//", colon + 1, StringComparison.Ordinal);
            int endIndex = comment < 0 ? raw.Length : comment;
            while (endIndex > colon + 1 && char.IsWhiteSpace(raw[endIndex - 1])) endIndex--;
            return new TextEdit(line, colon + 2, endIndex - colon - 1, value);
        }
    }
}
