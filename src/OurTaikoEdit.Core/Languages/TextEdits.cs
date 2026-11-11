namespace OurTaikoEdit.Core.Languages;

// Line and column (1-based) to offsets and back, with CRLF, a lone CR and LF all ending a line,
// as the parsers and AvaloniaEdit count them.
public sealed class LineIndex
{
    readonly int[] starts;
    readonly int length;

    public LineIndex(string text)
    {
        length = text.Length;
        var list = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            if (text[i] == '\n' || text[i] == '\r') list.Add(i + 1);
        }
        starts = list.ToArray();
    }

    public int LineCount => starts.Length;

    public int Offset(int line, int column)
        => Math.Min(starts[Math.Clamp(line, 1, starts.Length) - 1] + Math.Max(0, column - 1), length);

    public (int Line, int Column) Position(int offset)
    {
        offset = Math.Clamp(offset, 0, length);
        int i = Array.BinarySearch(starts, offset);
        int line = i >= 0 ? i : ~i - 1;
        return (line + 1, offset - starts[line] + 1);
    }
}

public static class TextEdits
{
    // Applies edits that do not overlap, each Length characters from its place (across lines).
    public static string Apply(string text, IEnumerable<TextEdit> edits)
    {
        var index = new LineIndex(text);
        var sb = new System.Text.StringBuilder(text);
        foreach (var (start, edit) in edits.Select(e => (index.Offset(e.Line, e.Column), e)).OrderByDescending(x => x.Item1))
        {
            int length = Math.Min(edit.Length, sb.Length - start);
            sb.Remove(start, length).Insert(start, edit.NewText);
        }
        return sb.ToString();
    }
}
