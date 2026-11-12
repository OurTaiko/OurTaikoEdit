using System.Globalization;
using OurTaikoEdit.Core.Localization;

namespace OurTaikoEdit.Mods.ChartImage;

// Why an image could not be read as a chart: a key of the mod's Strings.csv and what fills it.
public sealed class DecodeException(string key, params object?[] args) : Exception(Loc.F(key, args))
{
    public string Key { get; } = key;
}

// A command written before Notes[NoteIndex] of its measure (rests count).
public sealed record DecodedCommand(int NoteIndex, string Text);

// One measure of the image as the characters of a chart's measure: Number is its label in the
// image (from 1), Beats its length in quarter notes, Notes its characters in time order with
// rests and the ends of long notes.
public sealed record DecodedMeasure(int Number, double Beats, IReadOnlyList<char> Notes, IReadOnlyList<DecodedCommand> Commands)
{
    public int Subdivisions => Notes.Count;
}

public sealed record DecodedChart(double Bpm, IReadOnlyList<DecodedMeasure> Measures, IReadOnlyList<int> BalloonHits, int PixelsPerBeat);

// Reads a rendered chart image back into measures of note characters and commands.
//
// The image is exact: every colour and every pixel position is the renderer's, so nothing here
// is a threshold on how alike two colours are. What is read:
//
// - The layout: lanes (their black borders), measure lines, the beat lines that give the scale,
//   measure numbers and the BPM and HS labels in a 5-pixel bitmap font, go-go time as a pink band.
// - What is on a lane, from its shapes. A note is a disc of its colour, small or big, inside a
//   ring; a drumroll is a small or a big disc at its start, another at its end, and the band
//   between them; a balloon is its numbered head and a band. They are drawn from the last to
//   the first, so that what is played earlier lies on top: the right side of everything shows,
//   and the left side may be under the ring of what comes before. So everything is found by its
//   right end on the lane's centre line, and a note is the size whose whole shape, less what the
//   things before it hide, is what the pixels show. A drumroll starts where its rows that
//   nothing hides say it does.
// - Each measure then gets the coarsest equally spaced positions whose rounded pixel offsets hold
//   everything drawn in it.
//
// Whatever cannot be told is refused with a reason rather than put in the nearest place.
//
// The layout, the labels and the grid follow TJAAnalyzer's offline-analysis/chart-image-decoder.ts
// (MIT, © 2026 jackq), which reads the same images; how notes and drumrolls are recognised is
// written anew here. See PROVENANCE.md.
public static class ChartImageDecoder
{
    static readonly Rgb Border = new(0, 0, 0);
    static readonly Rgb White = new(255, 255, 255);
    static readonly Rgb Gogo = new(255, 192, 192);
    static readonly Rgb[] DrumrollFill = { new(255, 239, 67) };
    static readonly Rgb[] BalloonFill = { new(255, 191, 67) };
    static readonly Rgb[] DonFill = { new(255, 66, 66), new(255, 67, 67) };
    static readonly Rgb[] KaFill = { new(67, 200, 255) };

    // Where the bands of labels are, from the lane's top.
    const int BandScroll = -18, BandBpm = -12, BandMeasureNumber = -6;
    // A label starts this many pixels right of the position it is for.
    const int LabelInset = 2;
    // The widest gap between two glyphs of one label.
    const int LabelGlyphGap = 6;
    // How far right of a measure line its number is looked for.
    const int MeasureNumberScanWidth = 30;
    // The band of a drumroll or balloon is at least this long on the centre line.
    const int MinBodyRun = 9;
    // Positions closer than this many pixels are not told apart.
    const int MinSubdivisionPixels = 2;

    enum NoteKind { Don, Ka, DonBig, KaBig }
    enum LongKind { Drumroll, DrumrollBig, Balloon }

    sealed record Lane(int Index, int YMin, int XMin, int XMax);

    // A disc of flat colour as the renderer draws it: Top is its first row from the lane's top,
    // Rows the columns of each row that have the colour itself (the edge is smoothed and so is
    // left out), counted from the disc's position: the left of its two middle columns. Ring is how
    // far from the centre the ring around it hides what lies under it.
    sealed record Disc((int Left, int Right)[] Rows, int Top, double Ring)
    {
        public int Reach => Rows.Max(r => r.Right);
    }

    static readonly Disc Small = new(new[]
    {
        (0, 2), (-2, 3), (-3, 4), (-4, 5), (-4, 6), (-5, 6), (-5, 6), (-5, 6), (-4, 5), (-3, 4), (-2, 3), (-1, 2),
    }, 10, 9);

    static readonly Disc Big = new(new[]
    {
        (0, 2), (-3, 4), (-4, 5), (-5, 6), (-6, 7), (-6, 7), (-7, 8), (-7, 8), (-7, 8), (-7, 8), (-6, 7), (-6, 7), (-5, 6), (-4, 5), (-3, 4), (-1, 2),
    }, 8, 12);

    // Discs are centred on this row boundary of the lane, and half a column right of their position.
    const int CentreRow = 16;
    // The smoothed edge of a ring: a pixel this far outside it is certainly not under it.
    const double RingEdge = 0.75;

    // What hides the things after it: the ring of a note, or of a long note's end.
    readonly record struct Cover(double X, double Radius);

    static Cover CoverOf(Disc disc, int position) => new(position + 1, disc.Ring);

    // Whether none of the covers reaches the pixel.
    static bool Clear(Lane lane, int x, int y, List<Cover> covers)
    {
        double dy = y + 0.5 - (lane.YMin + CentreRow);
        foreach (var cover in covers)
        {
            double dx = x + 0.5 - cover.X, limit = cover.Radius + RingEdge;
            if (dx * dx + dy * dy < limit * limit) return false;
        }
        return true;
    }

    sealed record ShortNote(NoteKind Kind, int X);

    // One drawn band of a drumroll or balloon. A band that reaches a lane's edge is only part of
    // a long note: the rest is on the next lane, and the pieces are put together again. The start
    // is read from the piece that has it and the end from the piece that finishes the note.
    sealed class LongNote
    {
        public LongKind Kind;
        // The band on the lane's centre line.
        public int XMin, XMax;
        // The band reaches the left edge: it continues a note from the lane above.
        public bool ContinuesLeft;
        // The band reaches the right edge: it goes on into the lane below.
        public bool ContinuesRight;
        // Where the note starts, when the pixels say so.
        public int? StartX;
        // If the pixels leave more than one possible start, the grid places it within this range.
        public int StartMin, StartMax;
        public int? HitCount;

        public Disc Disc => Kind == LongKind.DrumrollBig ? Big : Small;
        public int HeadX => StartX ?? StartMax;
        // The band ends in a disc whose right end is the band's.
        public Cover EndCover => CoverOf(Disc, XMax - Disc.Reach);
    }

    sealed record Label(bool IsBpm, double Value, int X);
    readonly record struct Span(int XMin, int XMax);

    sealed class RawMeasure
    {
        public int Number;
        public required Lane Lane;
        public int XMin, XMax;
        public List<ShortNote> ShortNotes = new();
        public List<LongNote> LongNotes = new();
        public List<Label> Labels = new();
        public List<Span> GogoSpans = new();
        // Where drumrolls and balloons end, in pixels from XMin.
        public List<int> LongNoteEndOffsets = new();
    }

    sealed class RawLane
    {
        public required Lane Lane;
        public required List<int> MeasureLineXs;
        public required List<int?> MeasureNumbers;
        public int FirstMeasureNumber, TrailingPadding;
        public required List<ShortNote> ShortNotes;
        public required List<LongNote> LongNotes;
        public required List<Label> Labels;
        public required List<Span> GogoSpans;
    }

    // Sorted values in runs: a new run where the step is more than maxGap.
    static List<List<int>> Cluster(IEnumerable<int> values, int maxGap)
    {
        var groups = new List<List<int>>();
        foreach (int value in values)
        {
            if (groups.Count > 0 && value - groups[^1][^1] <= maxGap) groups[^1].Add(value);
            else groups.Add(new List<int> { value });
        }
        return groups;
    }

    static List<int> RunStarts(IEnumerable<int> values) => Cluster(values, 1).Select(run => run[0]).ToList();

    // JavaScript's Math.round, which the renderer places things with: halves go up.
    static int Round(double value) => (int)Math.Floor(value + 0.5);

    static List<Lane> FindLanes(Pixels pixels)
    {
        var borderYs = new List<int>();
        for (int y = 0; y < pixels.Height; y++)
        {
            int black = 0;
            for (int x = 0; x < pixels.Width; x++) if (pixels.Is(x, y, Border)) black++;
            if (black > pixels.Width / 4.0) borderYs.Add(y);
        }
        var laneBorderYs = RunStarts(borderYs);
        var lanes = new List<Lane>();
        for (int i = 0; i + 1 < laneBorderYs.Count; i += 2)
        {
            int yMin = laneBorderYs[i], xMin = -1, xMax = -1;
            for (int x = 0; x < pixels.Width; x++)
            {
                if (!pixels.Is(x, yMin, Border)) continue;
                if (xMin < 0) xMin = x;
                xMax = x;
            }
            if (xMin < 0) throw new DecodeException("ChartImageNoLanes");
            lanes.Add(new Lane(lanes.Count, yMin, xMin, xMax));
        }
        if (lanes.Count == 0) throw new DecodeException("ChartImageNoLanes");
        return lanes;
    }

    // Measure lines are drawn above the lane too, where no note covers them.
    static List<int> FindMeasureLines(Pixels pixels, Lane lane)
    {
        var columns = new List<int>();
        for (int x = lane.XMin; x <= lane.XMax; x++)
        {
            int white = 0;
            for (int y = lane.YMin - 8; y < lane.YMin; y++) if (pixels.Is(x, y, White)) white++;
            if (white >= 6) columns.Add(x);
        }
        return RunStarts(columns);
    }

    sealed record GlyphRun(int XMin, int XMax, string[] Rows);

    static List<GlyphRun> ReadGlyphRuns(int yTop, int xFrom, int xTo, Func<int, int, bool> matches)
    {
        var columns = new List<int>();
        for (int x = xFrom; x <= xTo; x++)
            for (int y = yTop; y < yTop + ChartImageFont.GlyphHeight; y++)
                if (matches(x, y)) { columns.Add(x); break; }
        return Cluster(columns, 1).Select(group =>
        {
            int xMin = group[0], xMax = group[^1];
            var rows = new string[ChartImageFont.GlyphHeight];
            for (int r = 0; r < rows.Length; r++)
            {
                var row = new char[xMax - xMin + 1];
                for (int x = xMin; x <= xMax; x++) row[x - xMin] = matches(x, yTop + r) ? '#' : '.';
                rows[r] = new string(row);
            }
            return new GlyphRun(xMin, xMax, rows);
        }).ToList();
    }

    static string ReadText(IEnumerable<GlyphRun> runs, Lane lane)
    {
        var text = new System.Text.StringBuilder();
        foreach (var run in runs)
            text.Append(ChartImageFont.Lookup(run.Rows) ?? throw new DecodeException("ChartImageGlyph", lane.Index + 1, run.XMin));
        return text.ToString();
    }

    static bool WholeNumber(string text, out int value)
    {
        value = 0;
        return text.Length > 0 && text.All(char.IsAsciiDigit) && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    static int? ReadMeasureNumber(Pixels pixels, Lane lane, int tick)
    {
        var runs = ReadGlyphRuns(lane.YMin + BandMeasureNumber, tick + 1, tick + MeasureNumberScanWidth, pixels.IsDark);
        if (runs.Count == 0) return null;
        string text = ReadText(runs, lane);
        return WholeNumber(text, out int value) ? value : throw new DecodeException("ChartImageMeasureNumber", lane.Index + 1, text);
    }

    static List<Label> ReadLabels(Pixels pixels, Lane lane)
    {
        var labels = new List<Label>();
        foreach (var (isBpm, y, prefix, test) in new (bool, int, string, Func<int, int, bool>)[]
        {
            (false, lane.YMin + BandScroll, "HS", pixels.IsRedText),
            (true, lane.YMin + BandBpm, "BPM", pixels.IsBlueText),
        })
        {
            var runs = ReadGlyphRuns(y, lane.XMin, lane.XMax, test);
            int cursor = 0;
            foreach (var group in Cluster(runs.Select(run => run.XMin), LabelGlyphGap + 4))
            {
                var glyphs = runs.GetRange(cursor, group.Count);
                cursor += group.Count;
                string text = ReadText(glyphs, lane);
                // Two labels drawn over each other read as neither.
                if (!text.StartsWith(prefix, StringComparison.Ordinal)
                    || !double.TryParse(text[prefix.Length..], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double value)
                    || !double.IsFinite(value))
                    throw new DecodeException("ChartImageLabel", lane.Index + 1, glyphs[0].XMin, text);
                labels.Add(new Label(isBpm, value, glyphs[0].XMin - LabelInset));
            }
        }
        return labels;
    }

    // Go-go time is a pink band in the strip above the lane.
    static List<Span> FindGogoSpans(Pixels pixels, Lane lane)
    {
        var columns = new List<int>();
        for (int x = lane.XMin; x <= lane.XMax; x++)
            for (int y = lane.YMin + BandScroll; y < lane.YMin; y++)
                if (pixels.Is(x, y, Gogo)) { columns.Add(x); break; }
        // Measure lines are drawn over the band: their width is bridged.
        return Cluster(columns, 3).Select(group => new Span(group[0], group[^1])).ToList();
    }

    sealed record ColorRun(int XMin, int XMax, Rgb[] Colors);

    static List<ColorRun> ScanRuns(Pixels pixels, int y, int xMin, int xMax, Rgb[] colors)
    {
        var runs = new List<ColorRun>();
        int start = -1;
        for (int x = xMin; x <= xMax; x++)
        {
            if (pixels.IsAny(x, y, colors))
            {
                if (start < 0) start = x;
                continue;
            }
            if (start >= 0) runs.Add(new ColorRun(start, x - 1, colors));
            start = -1;
        }
        if (start >= 0) runs.Add(new ColorRun(start, xMax, colors));
        return runs;
    }

    static int FillHeight(Pixels pixels, Lane lane, int x, Rgb[] colors)
    {
        int count = 0;
        for (int y = lane.YMin + 2; y < lane.YMin + 30; y++) if (pixels.IsAny(x, y, colors)) count++;
        return count;
    }

    // Whether the pixels are a note of this size at this position: every pixel of its disc that
    // nothing before it hides has its colour, and no such pixel next to the disc has (there the
    // note's own ring is). At least one pixel of it has to show.
    static bool IsNote(Pixels pixels, Lane lane, Disc disc, int position, Rgb[] colors, List<Cover> covers)
    {
        int shown = 0;
        for (int i = -1; i <= disc.Rows.Length; i++)
        {
            int y = lane.YMin + disc.Top + i;
            // The rows over and under the disc are looked at across the disc's first and last row.
            bool inDisc = i >= 0 && i < disc.Rows.Length;
            var (left, right) = disc.Rows[Math.Clamp(i, 0, disc.Rows.Length - 1)];
            // The top of a big note can have one extra pixel on the left.
            int extra = position + left - 1;
            if (disc == Big && i == 0 && Clear(lane, extra, y, covers) && pixels.IsAny(extra, y, colors))
                left--;
            for (int x = position + left - (inDisc ? 1 : 0); x <= position + right + (inDisc ? 1 : 0); x++)
            {
                if (!Clear(lane, x, y, covers)) continue;
                bool inside = inDisc && x >= position + left && x <= position + right;
                if (pixels.IsAny(x, y, colors) != inside) return false;
                if (inside) shown++;
            }
        }
        return shown > 0;
    }

    // The notes of a lane, from the first to the last. Each stretch of a note colour on the
    // centre line ends at a note's right end, which nothing hides, so the note is at one of two
    // places, as a small one or a big one; it is the one whose shape the pixels show, given the
    // notes before it and the ends of the long notes (ends).
    static List<ShortNote> FindShortNotes(Pixels pixels, Lane lane, List<LongNote> longNotes)
    {
        int y = lane.YMin + CentreRow - 1;
        var runs = new[] { DonFill, KaFill }.SelectMany(colors => ScanRuns(pixels, y, lane.XMin, lane.XMax, colors)).OrderBy(run => run.XMax);
        var notes = new List<ShortNote>();
        var covers = new List<Cover>();
        var ends = new Queue<LongNote>(longNotes.Where(n => !n.ContinuesRight).OrderBy(n => n.XMax));
        foreach (var run in runs)
        {
            while (ends.Count > 0 && ends.Peek().XMax < run.XMax) covers.Add(ends.Dequeue().EndCover);
            bool small = IsNote(pixels, lane, Small, run.XMax - Small.Reach, run.Colors, covers);
            bool big = IsNote(pixels, lane, Big, run.XMax - Big.Reach, run.Colors, covers);
            // Both: so little of it shows that either would look like this. Neither: not a note.
            if (small == big) throw new DecodeException(small ? "ChartImageNoteClipped" : "ChartImageNoteShape", lane.Index + 1, run.XMax);
            var disc = small ? Small : Big;
            bool don = run.Colors == DonFill;
            notes.Add(new ShortNote(don ? big ? NoteKind.DonBig : NoteKind.Don : big ? NoteKind.KaBig : NoteKind.Ka, run.XMax - disc.Reach));
            covers.Add(CoverOf(disc, run.XMax - disc.Reach));
        }
        return notes;
    }

    // The bands of the long notes of a lane, on its centre line: where each is, whether it is
    // cut by the lane's edges, and its kind (a big drumroll's band is taller).
    static List<LongNote> FindLongNotes(Pixels pixels, Lane lane, int xContentMin, int xContentMax)
    {
        int y = lane.YMin + CentreRow - 1;
        var results = new List<LongNote>();
        foreach (var (colors, balloon) in new[] { (DrumrollFill, false), (BalloonFill, true) })
            foreach (var run in ScanRuns(pixels, y, lane.XMin, lane.XMax, colors))
            {
                if (run.XMax - run.XMin + 1 < MinBodyRun) continue;
                int bodyHeight = FillHeight(pixels, lane, Round((run.XMin + run.XMax) / 2.0), colors);
                results.Add(new LongNote
                {
                    Kind = balloon ? LongKind.Balloon : bodyHeight > Small.Rows.Length + 2 ? LongKind.DrumrollBig : LongKind.Drumroll,
                    XMin = run.XMin, XMax = run.XMax,
                    ContinuesLeft = run.XMin >= xContentMin && run.XMin <= xContentMin + 1,
                    ContinuesRight = run.XMax >= xContentMax - 1,
                });
            }
        return results.OrderBy(n => n.XMin).ToList();
    }

    // Where the long notes of a lane start. A balloon's band starts a fixed way after its
    // numbered head. A drumroll starts with a disc like a note's: in each of its rows the colour
    // begins where the disc does, unless the ring of something before it hides that. So every row
    // whose first pixel has nothing reaching the pixel before it constrains the start;
    // the other rows only give an upper bound. The grid resolves any remaining ambiguity.
    static void FindLongNoteStarts(Pixels pixels, Lane lane, List<LongNote> longNotes, List<ShortNote> notes)
    {
        foreach (var note in longNotes)
        {
            if (note.ContinuesLeft) continue;
            if (note.Kind == LongKind.Balloon)
            {
                note.StartX = note.XMin + Insets(note.Kind).Start;
                note.HitCount = ReadBalloonHitCount(pixels, lane, note.XMin);
                continue;
            }
            var before = notes.Where(n => n.X < note.XMin).ToList();
            var covers = before.Select(n => CoverOf(n.Kind is NoteKind.DonBig or NoteKind.KaBig ? Big : Small, n.X))
                .Concat(longNotes.Where(n => !n.ContinuesRight && n.XMax < note.XMin).Select(n => n.EndCover)).ToList();
            var disc = note.Disc;
            int middle = (note.XMin + note.XMax) / 2;
            int min = int.MinValue, max = int.MaxValue;
            for (int i = 0; i < disc.Rows.Length; i++)
            {
                int y = lane.YMin + disc.Top + i, x = middle;
                if (!pixels.IsAny(x, y, DrumrollFill)) continue;
                while (pixels.IsAny(x - 1, y, DrumrollFill)) x--;
                int first = x - disc.Rows[i].Left;
                // The top row of a big roll has two rasterizations, one pixel apart.
                int last = note.Kind == LongKind.DrumrollBig && i == 0 ? first + 1 : first;
                max = Math.Min(max, last);
                if (Clear(lane, x - 1, y, covers)) min = Math.Max(min, first);
            }
            if (max == int.MaxValue || min > max) throw new DecodeException("ChartImageHeadShape", lane.Index + 1, note.XMin);
            note.StartX = min == max ? min : null;
            note.StartMin = before.Count > 0 ? Math.Max(min, before.Max(n => n.X) + 1) : min;
            note.StartMax = max;
        }
    }

    static int ReadBalloonHitCount(Pixels pixels, Lane lane, int xBodyStart)
    {
        var digits = ReadGlyphRuns(lane.YMin + 13, Math.Max(lane.XMin, xBodyStart - 30), xBodyStart, pixels.IsDark)
            .Where(run => run.XMax - run.XMin >= 2).ToList();
        if (digits.Count == 0) throw new DecodeException("ChartImageBalloonCount", lane.Index + 1, xBodyStart);
        string text = ReadText(digits, lane);
        return WholeNumber(text, out int hits) && hits > 0 ? hits : throw new DecodeException("ChartImageBalloonCount", lane.Index + 1, xBodyStart);
    }

    static RawLane ReadLane(Pixels pixels, Lane lane, int previousTrailingPadding)
    {
        var lineXs = FindMeasureLines(pixels, lane);
        if (lineXs.Count == 0) throw new DecodeException("ChartImageNoMeasureLines", lane.Index + 1);
        int trailingPadding = lane.XMax - lineXs[^1];
        var numbers = lineXs.Select(x => ReadMeasureNumber(pixels, lane, x)).ToList();
        if (numbers[0] is not { } first) throw new DecodeException("ChartImageMeasureUnlabelled", lane.Index + 1);
        for (int i = 0; i < numbers.Count; i++)
            if (numbers[i] is { } value && value != first + i)
                throw new DecodeException("ChartImageMeasureLabel", lane.Index + 1, first + i, value);
        // The bands of the long notes first: their ends hide the notes after them. Then the notes,
        // and with the notes, what hides the long notes' starts.
        var longNotes = FindLongNotes(pixels, lane, lineXs[0], lane.XMax - Math.Min(previousTrailingPadding, trailingPadding));
        var shortNotes = FindShortNotes(pixels, lane, longNotes);
        FindLongNoteStarts(pixels, lane, longNotes, shortNotes);
        return new RawLane
        {
            Lane = lane, MeasureLineXs = lineXs, MeasureNumbers = numbers, FirstMeasureNumber = first, TrailingPadding = trailingPadding,
            ShortNotes = shortNotes,
            LongNotes = longNotes,
            Labels = ReadLabels(pixels, lane),
            GogoSpans = FindGogoSpans(pixels, lane),
        };
    }

    // Checks the body coming in from the lane above against this lane's, and gives the body that
    // goes on below.
    static LongNote? ValidateLongNoteContinuation(RawLane rawLane, LongNote? continued)
    {
        var incoming = rawLane.LongNotes.Where(n => n.ContinuesLeft).ToList();
        var outgoing = rawLane.LongNotes.Where(n => n.ContinuesRight).ToList();
        if (incoming.Count > 1 || outgoing.Count > 1
            || incoming.Count > 0 != (continued != null)
            || incoming.Count > 0 && continued != null && incoming[0].Kind != continued.Kind)
            throw new DecodeException("ChartImageWrap", rawLane.Lane.Index + 1);
        return outgoing.FirstOrDefault();
    }

    // Ends are filed under the measure they are drawn in, which can be on a later lane than the head.
    static void CollectLongNoteEnds(RawLane rawLane, Dictionary<int, List<int>> endOffsetsByMeasure)
    {
        foreach (var note in rawLane.LongNotes)
        {
            if (note.ContinuesRight) continue;
            int xEnd = note.XMax + Insets(note.Kind).End;
            int index = rawLane.MeasureLineXs.Count - 1;
            while (index > 0 && rawLane.MeasureLineXs[index] > xEnd) index--;
            if (xEnd < rawLane.MeasureLineXs[0]) throw new DecodeException("ChartImageWrap", rawLane.Lane.Index + 1);
            int number = rawLane.FirstMeasureNumber + index;
            if (!endOffsetsByMeasure.TryGetValue(number, out var ends)) endOffsetsByMeasure[number] = ends = new List<int>();
            ends.Add(xEnd - rawLane.MeasureLineXs[index]);
        }
    }

    static List<RawMeasure> SplitLaneMeasures(RawLane rawLane)
    {
        var measures = new List<RawMeasure>();
        for (int i = 0; i < rawLane.MeasureLineXs.Count; i++)
        {
            if (rawLane.MeasureNumbers[i] is not { } number) continue;
            int xMin = rawLane.MeasureLineXs[i];
            int? xMax = i + 1 < rawLane.MeasureLineXs.Count ? rawLane.MeasureLineXs[i + 1] : null;
            int xEnd = xMax ?? rawLane.Lane.XMax;
            measures.Add(new RawMeasure
            {
                Number = number, Lane = rawLane.Lane, XMin = xMin, XMax = xMax ?? -1,
                ShortNotes = rawLane.ShortNotes.Where(n => n.X >= xMin && n.X < xEnd).ToList(),
                // Only the piece with the head starts a note in this measure.
                LongNotes = rawLane.LongNotes.Where(n => !n.ContinuesLeft && n.HeadX >= xMin && n.HeadX < xEnd).ToList(),
                Labels = rawLane.Labels.Where(l => l.X >= xMin && l.X < xEnd).ToList(),
                GogoSpans = rawLane.GogoSpans.Select(s => new Span(Math.Max(s.XMin, xMin), Math.Min(s.XMax, xEnd - 1))).Where(s => s.XMin <= s.XMax).ToList(),
            });
        }
        return measures;
    }

    // Every lane leaves the same room after its last measure line. Only the last lane differs:
    // its last measure has no closing line to leave room after.
    static int ValidateLanePaddings(List<int> paddings)
    {
        int padding = paddings.Min();
        var different = Enumerable.Range(0, paddings.Count).Where(i => paddings[i] != padding).ToList();
        if (different.Count > 1 || different.Count == 1 && different[0] != paddings.Count - 1)
            throw new DecodeException("ChartImageLayout");
        return padding;
    }

    static List<RawMeasure> CollectMeasures(Pixels pixels, List<Lane> lanes)
    {
        var paddings = new List<int>();
        var measures = new List<RawMeasure>();
        var endOffsetsByMeasure = new Dictionary<int, List<int>>();
        LongNote? continued = null;
        foreach (var lane in lanes)
        {
            var rawLane = ReadLane(pixels, lane, paddings.Count > 0 ? paddings.Min() : int.MaxValue);
            paddings.Add(rawLane.TrailingPadding);
            continued = ValidateLongNoteContinuation(rawLane, continued);
            CollectLongNoteEnds(rawLane, endOffsetsByMeasure);
            measures.AddRange(SplitLaneMeasures(rawLane));
        }
        if (continued != null) throw new DecodeException("ChartImageWrap", lanes.Count);
        int padding = ValidateLanePaddings(paddings);

        foreach (var measure in measures)
        {
            if (measure.XMax < 0) measure.XMax = measure.Lane.XMax - padding;
            if (endOffsetsByMeasure.TryGetValue(measure.Number, out var ends)) measure.LongNoteEndOffsets = ends;
        }
        if (endOffsetsByMeasure.Keys.Any(number => !measures.Any(m => m.Number == number))) throw new DecodeException("ChartImageLayout");
        measures = measures.OrderBy(m => m.Number).ToList();
        for (int i = 0; i < measures.Count; i++)
            if (measures[i].Number != i + 1) throw new DecodeException("ChartImageMeasureOrder", i + 1, measures[i].Number);
        return measures;
    }

    // Beat lines are the brightest lines drawn inside the lane.
    static int FindPixelsPerBeat(Pixels pixels, List<Lane> lanes)
    {
        var counts = new List<(int Spacing, int Count)>();
        foreach (var lane in lanes)
        {
            int y = lane.YMin + 15;
            var columns = new List<int>();
            for (int x = lane.XMin; x <= lane.XMax; x++)
                if (pixels.Is(x, y, new Rgb(191, 191, 191))) columns.Add(x);
            var lines = RunStarts(columns);
            for (int i = 1; i < lines.Count; i++)
            {
                int spacing = lines[i] - lines[i - 1], at = counts.FindIndex(c => c.Spacing == spacing);
                if (at >= 0) counts[at] = (spacing, counts[at].Count + 1);
                else counts.Add((spacing, 1));
            }
        }
        if (counts.Count == 0) throw new DecodeException("ChartImageNoBeatLines");
        return counts.OrderByDescending(c => c.Count).First().Spacing;
    }

    public static int[] SubdivisionOffsets(int width, int subdivisions)
    {
        var offsets = new int[subdivisions];
        for (int i = 0; i < subdivisions; i++) offsets[i] = Round((double)i * width / subdivisions);
        return offsets;
    }

    public sealed record SubdivisionFit(int Subdivisions, IReadOnlyDictionary<int, int> NoteIndexByPixelOffset);

    // The coarsest grid that, rounded the way the renderer rounds, has every offset on it; null
    // when none down to MinSubdivisionPixels has.
    public static SubdivisionFit? FitSubdivisions(IReadOnlyCollection<int> offsets, int width)
    {
        for (int subdivisions = 1; (double)width / subdivisions >= MinSubdivisionPixels; subdivisions++)
        {
            var positions = SubdivisionOffsets(width, subdivisions);
            var indexByOffset = new Dictionary<int, int>();
            for (int i = 0; i < positions.Length; i++) indexByOffset.TryAdd(positions[i], i);
            if (offsets.All(indexByOffset.ContainsKey)) return new SubdivisionFit(subdivisions, indexByOffset);
        }
        return null;
    }

    // Pixels between a band's ends on the centre line and the positions a long note stands for:
    // where a balloon starts, counted from the band after its numbered head, and where each kind
    // ends. (Where a drumroll starts is read from its shape.) Each is checked against the fitted
    // grid, so a change in the renderer is a refusal, not notes quietly moved.
    static (int Start, int End) Insets(LongKind kind) => kind switch
    {
        LongKind.Drumroll => (5, -1),
        LongKind.DrumrollBig => (7, -4),
        _ => (-13, -1),
    };

    static char NoteChar(NoteKind kind) => kind switch { NoteKind.Don => '1', NoteKind.Ka => '2', NoteKind.DonBig => '3', _ => '4' };
    static char LongChar(LongKind kind) => kind switch { LongKind.Drumroll => '5', LongKind.DrumrollBig => '6', _ => '7' };

    static double BeatsOf(int width, int pixelsPerBeat, int number)
    {
        double beats = (double)width / pixelsPerBeat;
        if (Math.Abs(beats * 2 - Math.Round(beats * 2)) > 1e-9) throw new DecodeException("ChartImageMeasureWidth", number, width);
        return Math.Round(beats * 2) / 2;
    }

    // A count of quarter notes as a time signature, reduced no further than quarters.
    public static string TimeSignature(double beats)
    {
        int numerator = (int)Math.Round(beats * 2), denominator = 8;
        while (denominator > 4 && numerator % 2 == 0) { numerator /= 2; denominator /= 2; }
        return $"{numerator}/{denominator}";
    }

    // The pink band as changes from the state so far.
    static List<(int Offset, bool On)> GogoTransitions(RawMeasure measure, bool wasOn)
    {
        var transitions = new List<(int, bool)>();
        bool on = wasOn;
        int cursor = measure.XMin;
        foreach (var span in measure.GogoSpans.OrderBy(s => s.XMin))
        {
            if (on && span.XMin > cursor) { transitions.Add((cursor - measure.XMin, false)); on = false; }
            if (!on) { transitions.Add((span.XMin - measure.XMin, true)); on = true; }
            cursor = span.XMax + 1;
        }
        if (on && cursor < measure.XMax) transitions.Add((cursor - measure.XMin, false));
        return transitions;
    }

    sealed class ChartState
    {
        public double? Bpm, InitialBpm;
        public double Scroll = 1;
        public bool GogoOn;
        public List<int> BalloonHits = new();
    }

    static string Number(double value) => value.ToString("0.###############", CultureInfo.InvariantCulture);

    static DecodedMeasure AssembleMeasure(RawMeasure measure, int pixelsPerBeat, ChartState state)
    {
        int width = measure.XMax - measure.XMin;
        double beats = BeatsOf(width, pixelsPerBeat, measure.Number);
        var gogoChanges = GogoTransitions(measure, state.GogoOn);

        var offsets = new List<int>();
        offsets.AddRange(measure.ShortNotes.Select(n => n.X - measure.XMin));
        offsets.AddRange(measure.LongNotes.Where(n => n.StartX != null).Select(n => n.StartX!.Value - measure.XMin));
        offsets.AddRange(measure.LongNoteEndOffsets);
        offsets.AddRange(measure.Labels.Select(l => l.X - measure.XMin));
        offsets.AddRange(gogoChanges.Select(g => g.Offset));
        offsets = offsets.Where(o => o >= 0 && o < width).ToList();
        var fit = FitSubdivisions(offsets, width) ?? throw new DecodeException("ChartImageNoGrid", measure.Number);
        var positions = SubdivisionOffsets(width, fit.Subdivisions);

        var events = new List<(int Offset, string Text, bool IsCommand)>();
        foreach (var note in measure.ShortNotes) events.Add((note.X - measure.XMin, NoteChar(note.Kind).ToString(), false));
        foreach (var note in measure.LongNotes)
        {
            int start;
            if (note.StartX is { } x) start = x - measure.XMin;
            else
            {
                var candidates = positions.Where(o => measure.XMin + o >= note.StartMin && measure.XMin + o <= note.StartMax).ToList();
                if (candidates.Count != 1) throw new DecodeException("ChartImageHeadCovered", measure.Lane.Index + 1, note.XMin);
                start = candidates[0];
            }
            events.Add((start, LongChar(note.Kind).ToString(), false));
            if (note is { Kind: LongKind.Balloon, HitCount: { } hits }) state.BalloonHits.Add(hits);
        }
        foreach (int end in measure.LongNoteEndOffsets) events.Add((end, "8", false));
        foreach (var label in measure.Labels)
        {
            int at = label.X - measure.XMin;
            if (label.IsBpm)
            {
                if (state.Bpm == null) state.Bpm = state.InitialBpm = label.Value;
                else if (label.Value != state.Bpm)
                {
                    events.Add((at, "#BPMCHANGE " + Number(label.Value), true));
                    state.Bpm = label.Value;
                }
                continue;
            }
            if (label.Value == state.Scroll) continue;
            events.Add((at, "#SCROLL " + Number(label.Value), true));
            state.Scroll = label.Value;
        }
        foreach (var (offset, on) in gogoChanges)
        {
            events.Add((offset, on ? "#GOGOSTART" : "#GOGOEND", true));
            state.GogoOn = on;
        }

        var notes = Enumerable.Repeat('0', fit.Subdivisions).ToArray();
        var commands = new List<DecodedCommand>();
        foreach (var (offset, text, isCommand) in events)
        {
            if (offset < 0 || offset >= width) continue;
            if (!fit.NoteIndexByPixelOffset.TryGetValue(offset, out int index)) throw new DecodeException("ChartImageNoGrid", measure.Number);
            if (isCommand) { commands.Add(new DecodedCommand(index, text)); continue; }
            if (notes[index] != '0') throw new DecodeException("ChartImageTwoNotes", measure.Number);
            notes[index] = text[0];
        }
        return new DecodedMeasure(measure.Number, beats, notes, commands);
    }

    // Every drumroll and balloon opens and closes once, and every balloon has its count. A tail
    // can be past its own measure, so this also catches an end filed under the wrong one.
    public static void VerifyLongNotes(IReadOnlyList<DecodedMeasure> measures, IReadOnlyList<int> balloonHits)
    {
        bool open = false;
        int openedIn = 0, balloons = 0;
        foreach (var measure in measures)
            foreach (char note in measure.Notes)
            {
                if (note is '5' or '6' or '7')
                {
                    if (open) throw new DecodeException("ChartImageLongOpen", openedIn);
                    (open, openedIn) = (true, measure.Number);
                    if (note == '7') balloons++;
                }
                else if (note == '8')
                {
                    if (!open) throw new DecodeException("ChartImageLongEnd", measure.Number);
                    open = false;
                }
            }
        if (open) throw new DecodeException("ChartImageLongOpen", openedIn);
        if (balloons != balloonHits.Count) throw new DecodeException("ChartImageBalloons", balloons, balloonHits.Count);
    }

    public static DecodedChart Decode(Pixels pixels)
    {
        var lanes = FindLanes(pixels);
        int pixelsPerBeat = FindPixelsPerBeat(pixels, lanes);
        var state = new ChartState();
        var measures = CollectMeasures(pixels, lanes).Select(m => AssembleMeasure(m, pixelsPerBeat, state)).ToList();
        if (state.InitialBpm is not { } bpm) throw new DecodeException("ChartImageNoBpm");
        VerifyLongNotes(measures, state.BalloonHits);
        return new DecodedChart(bpm, measures, state.BalloonHits, pixelsPerBeat);
    }

    public static DecodedChart Decode(string file)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(file); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new DecodeException("ChartImageUnreadable", e.Message); }
        return Decode(Png.Read(bytes));
    }

    public static string FormatNumber(double value) => Number(value);
}
