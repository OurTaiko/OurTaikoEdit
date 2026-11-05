using System.Globalization;
using CharTaiko.Core.Languages;
using OurTaiko;

namespace CharTaiko.Tja;

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
    sealed record Placement(ChartMeasure Measure, long Num, long Den, char Char, bool Weak = false);

    public EditResult PlaceNote(ChartAnalysis analysis, GridPosition at, NoteKind kind, GridPosition? end = null)
    {
        if (analysis.Chart == null) return EditResult.Fail("Fix the chart's errors first: the timeline edits what the parser reads.");
        if (at.Steps <= 0 || at.Step < 0 || at.Step >= at.Steps) return EditResult.Fail("That is outside the measure.");
        var route = RouteOf(at);
        var placements = new List<Placement> { Place(at, CharOf(kind)) };
        double start = at.Time;

        // A note inside a roll would end it early; the roll has to go first.
        if (RollAround(analysis, start, route, at.Measure) is { } roll)
            return EditResult.Fail($"That is inside a {Describe(roll)}; remove it first.");

        if (IsLong(kind))
        {
            if (end is not { } e || e.Steps <= 0) return EditResult.Fail("A roll needs an end.");
            if (e.Step >= e.Steps) e = e with { Step = e.Steps - 1 };
            if (!(e.Time > start)) return EditResult.Fail("A roll must end after it starts.");
            if (Crosses(analysis, at.Measure, e.Measure)) return EditResult.Fail("Rolls across a branch boundary are written in the code.");
            if (RollAround(analysis, e.Time, route, e.Measure) is { } other && other.Time != start)
                return EditResult.Fail($"The end is inside a {Describe(other)}; remove it first.");
            placements.Add(Place(e, '8'));
            // What lies under the new roll would end it: clear it, with the ends of rolls cleared.
            foreach (var (measure, i, slot) in SlotsOn(analysis, route))
            {
                double t = slot.Time;
                if (t <= start || t >= e.Time || slot.Token == "0") continue;
                placements.Add(new Placement(measure, i, measure.Slots.Count, '0'));
                if (slot.Note is { IsLong: true } cleared) ClearTail(analysis, cleared, placements);
            }
        }

        // A roll replaced at its head loses its end.
        foreach (var p in placements.ToList())
            if (SlotOf(p) is { Note: { IsLong: true } replaced } && !(p.Char == '8')) ClearTail(analysis, replaced, placements);

        return Apply(analysis, placements);
    }

    public EditResult RemoveNote(ChartAnalysis analysis, ChartNote note)
    {
        if (analysis.Chart == null) return EditResult.Fail("Fix the chart's errors first.");
        var found = analysis.Measures.SelectMany(m => m.Slots.Select((s, i) => (m, i, s))).FirstOrDefault(x => ReferenceEquals(x.s.Note, note));
        if (found.m == null) return EditResult.Fail("That note is not in the chart's text.");
        var placements = new List<Placement> { new(found.m, found.i, found.m.Slots.Count, '0') };
        if (note.IsLong) ClearTail(analysis, note, placements);
        return Apply(analysis, placements);
    }

    static string Describe(ChartNote n) => n.IsBalloon ? "balloon" : "drumroll";

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
    static IEnumerable<(ChartMeasure Measure, int Index, ChartSlot Slot)> SlotsOn(ChartAnalysis analysis, BranchRoute route)
        => analysis.Measures.Where(m => m.IsOn(route)).SelectMany(m => m.Slots.Select((s, i) => (m, i, s)));

    // A roll on the route whose body (after its head, up to its end) contains the time.
    static ChartNote? RollAround(ChartAnalysis analysis, double time, BranchRoute route, ChartMeasure measure)
        => SlotsOn(analysis, route).Select(x => x.Slot.Note).FirstOrDefault(n => n is { IsLong: true } && n.Time < time && time <= n.EndTime);

    // Whether the stretch between two measures leaves or enters a branch.
    static bool Crosses(ChartAnalysis analysis, ChartMeasure a, ChartMeasure b)
    {
        if (a.BranchId != b.BranchId || a.Route != b.Route && a.BranchId >= 0) return true;
        int from = Order(analysis, a), to = Order(analysis, b);
        return a.BranchId < 0 && analysis.Measures.Where(m => Order(analysis, m) > from && Order(analysis, m) < to).Any(m => m.BranchId >= 0);
    }

    static int Order(ChartAnalysis analysis, ChartMeasure m) => IndexOf(analysis.Measures, m);

    static int IndexOf(IReadOnlyList<ChartMeasure> list, ChartMeasure m)
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

    static EditResult EditMeasure(ChartMeasure measure, List<Placement> placements)
    {
        int n = measure.Slots.Count;
        if (n == 0)
        {
            if (measure.Comma.IsEmpty) return EditResult.Fail("That measure has no comma to write before.");
            long size = placements.Aggregate(1L, (l, p) => Lcm(l, p.Den));
            if (size > MaxSlots) return EditResult.Fail($"That needs {size} slots in one measure; use a coarser grid.");
            var chars = Enumerable.Repeat('0', (int)size).ToArray();
            foreach (var p in placements) chars[p.Num * size / p.Den] = p.Char;
            if (chars.All(c => c == '0')) return new EditResult(Array.Empty<TextEdit>());
            return new EditResult(new[] { new TextEdit(measure.Comma.Line, measure.Comma.Column, measure.Comma.Length, new string(chars) + ",") });
        }

        long total = placements.Aggregate((long)n, (l, p) => Lcm(l, p.Den));
        if (total > MaxSlots) return EditResult.Fail($"That needs {total} slots in one measure; use a coarser grid.");
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
                if (values.SequenceEqual(old)) continue;
                string list = string.Join(",", values.Select(v => v.ToString(CultureInfo.InvariantCulture)));
                if (header.InCourse) edits.Add(ReplaceValue(text, header.Line, list));
                else if (courseLine > 0)
                {
                    // The shared list stays for the other courses; this course gets its own.
                    string key = r == 0 ? "BALLOON" : r == 1 ? "BALLOONEXP" : "BALLOONMAS";
                    string line = Lines(text)[courseLine - 1];
                    edits.Add(new TextEdit(courseLine, line.Length + 1, 0, Newline(text) + key + ":" + list));
                }
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
