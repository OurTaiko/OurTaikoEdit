using CharTaiko.Core.Languages;
using OurTaiko;

namespace CharTaiko.Tja;

// Completion from what TJAParser reads (Documentation~/TJA-Specification.md): header keys outside
// a block, commands inside one, and the values of COURSE and STYLE.
static class TjaCompletion
{
    static readonly CompletionItem[] Headers =
    {
        new("TITLE:", "Song title", "Not in the content hash."),
        new("SUBTITLE:", "Subtitle; a leading -- hides it on song select, ++ shows it"),
        new("TITLEJA:", "Japanese title"), new("TITLEEN:", "English title"), new("TITLEZH:", "Chinese title"),
        new("TITLEZH_TW:", "Traditional Chinese title"), new("TITLEKO:", "Korean title"),
        new("SUBTITLEJA:", "Japanese subtitle"), new("SUBTITLEEN:", "English subtitle"), new("SUBTITLEZH:", "Chinese subtitle"),
        new("BPM:", "Starting BPM; positive and finite"),
        new("WAVE:", "Audio file, relative to the chart"),
        new("OFFSET:", "Seconds; positive starts the chart earlier than the audio"),
        new("DEMOSTART:", "Song-select preview start, in seconds"),
        new("GENRE:", "Genre"),
        new("MAKER:", "Chart author, for blocks without NOTESDESIGNERn"),
        new("NOTESDESIGNER0:", "Easy author"), new("NOTESDESIGNER1:", "Normal author"), new("NOTESDESIGNER2:", "Hard author"),
        new("NOTESDESIGNER3:", "Oni author"), new("NOTESDESIGNER4:", "Edit (Ura) author"),
        new("BGMOVIE:", "Background video"), new("MOVIEOFFSET:", "Background video offset, in seconds"),
        new("COURSE:", "Starts a difficulty: Easy, Normal, Hard, Oni, Edit (0–4, Ura)"),
        new("LEVEL:", "Stars, 1–10"),
        new("STYLE:", "Single or Double (with #START P1 / P2)"),
        new("BALLOON:", "Balloon hits in order, comma-separated (normal route)"),
        new("BALLOONNOR:", "Balloon hits on the normal route"),
        new("BALLOONEXP:", "Balloon hits on the expert route"),
        new("BALLOONMAS:", "Balloon hits on the master route"),
        new("#START", "Starts the chart block"),
        new("#START P1", "Starts the 1P block of a Double course"),
        new("#START P2", "Starts the 2P block of a Double course"),
        new("#NMSCROLL", "Before #START: the block starts in normal scrolling"),
        new("#BMSCROLL", "Before #START: the block starts in BM scrolling"),
        new("#HBSCROLL", "Before #START: the block starts in HB scrolling"),
    };

    static readonly CompletionItem[] Commands =
    {
        new("#BPMCHANGE ", "#BPMCHANGE <bpm>: the tempo from here"),
        new("#MEASURE ", "#MEASURE <a>/<b>: the time signature from the next measure"),
        new("#SCROLL ", "#SCROLL <speed>: scroll speed; complex values (1+2i) move vertically"),
        new("#DELAY ", "#DELAY <seconds>: shifts everything after it"),
        new("#GOGOSTART", "Starts go-go time"),
        new("#GOGOEND", "Ends go-go time"),
        new("#BARLINEOFF", "Hides bar lines from here"),
        new("#BARLINEON", "Shows bar lines from here"),
        new("#SECTION", "Resets the branch counters"),
        new("#BRANCHSTART ", "#BRANCHSTART <p|pp|r|rb|s|jp|jg|jb|d>, <expert>, <master>[, l|m]"),
        new("#N", "Normal route"),
        new("#E", "Expert route"),
        new("#M", "Master route"),
        new("#BRANCHEND", "Ends the branch"),
        new("#LEVELHOLD", "Keeps the current route from here"),
        new("#NMSCROLL", "Normal scrolling"),
        new("#BMSCROLL", "BM scrolling: by the beat clock, ignoring #SCROLL"),
        new("#HBSCROLL", "HB scrolling: by the beat clock, with #SCROLL"),
        new("#JPOSSCROLL ", "#JPOSSCROLL <seconds> <move> [direction]: moves the judge"),
        new("#SUDDEN ", "#SUDDEN <show> <move>: notes appear and start to move late"),
        new("#LANE_ALPHA ", "#LANE_ALPHA <0–100>[%] [ms]: lane background opacity (OurTaiko)"),
        new("#END", "Ends the chart block"),
    };

    static readonly Dictionary<string, CompletionItem[]> Values = new()
    {
        ["COURSE"] = new CompletionItem[] { new("Easy", "かんたん"), new("Normal", "ふつう"), new("Hard", "むずかしい"), new("Oni", "おに"), new("Edit", "裏 (Ura)") },
        ["STYLE"] = new CompletionItem[] { new("Single", "One player"), new("Double", "1P and 2P blocks") },
    };

    public static CompletionResult Complete(string text, int line, int column)
    {
        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (line < 1 || line > lines.Length) return CompletionResult.None;
        string current = lines[line - 1];
        string before = current.Substring(0, Math.Min(Math.Max(column - 1, 0), current.Length));
        int indent = before.Length - before.TrimStart().Length;
        string word = before.TrimStart();
        if (word.Contains("//")) return CompletionResult.None;

        int colon = word.IndexOf(':');
        if (colon >= 0 && !word.StartsWith('#'))
        {
            string key = word.Substring(0, colon).Trim().ToUpperInvariant();
            if (!Values.TryGetValue(key, out var values)) return CompletionResult.None;
            string typed = word.Substring(colon + 1).TrimStart();
            return new CompletionResult(column - typed.Length, Filter(values, typed));
        }
        if (word.Contains(' ')) return CompletionResult.None;
        var items = InBlock(text, line) ? Commands : Headers;
        // Inside a block only commands complete; a note line does not.
        if (InBlock(text, line) && word.Length > 0 && word[0] != '#') return CompletionResult.None;
        return new CompletionResult(indent + 1, Filter(items, word));
    }

    static IReadOnlyList<CompletionItem> Filter(IEnumerable<CompletionItem> items, string typed)
        => items.Where(i => i.Text.StartsWith(typed, StringComparison.OrdinalIgnoreCase)).ToList();

    // Whether a line is inside a block: after a #START, up to and including its #END. As in
    // TjaLines, COURSE: or another #START also ends a block; a block left open runs to the end.
    static bool InBlock(string text, int line)
    {
        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        bool inBlock = false;
        for (int i = 1; i <= line && i <= lines.Length; i++)
        {
            string t = lines[i - 1];
            int comment = t.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0) t = t.Substring(0, comment);
            t = t.Trim();
            if (t.StartsWith("#START", StringComparison.OrdinalIgnoreCase)) { if (i == line) return false; inBlock = true; continue; }
            if (inBlock && t.StartsWith("COURSE", StringComparison.OrdinalIgnoreCase) && t.TrimStart().Substring(6).TrimStart().StartsWith(':')) inBlock = false;
            if (t.Equals("#END", StringComparison.OrdinalIgnoreCase)) { if (i == line) return inBlock; inBlock = false; continue; }
            if (i == line) return inBlock;
        }
        return inBlock;
    }
}
