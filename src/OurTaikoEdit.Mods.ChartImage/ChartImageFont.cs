namespace OurTaikoEdit.Mods.ChartImage;

// The bitmap font the chart renderer draws measure numbers, balloon counts and the BPM and HS
// labels with. Every glyph is 5 rows tall, of its own width, and drawn without smoothing, so the
// pixels of a label match these rows exactly. Each string is one row.
static class ChartImageFont
{
    public const int GlyphHeight = 5;

    static readonly (char Char, string[] Rows)[] Glyphs =
    {
        ('0', new[] { "####", "#..#", "#..#", "#..#", "####" }),
        ('1', new[] { ".#.", "##.", ".#.", ".#.", "###" }),
        ('2', new[] { "####", "...#", "####", "#...", "####" }),
        ('3', new[] { "####", "...#", ".###", "...#", "####" }),
        ('4', new[] { "..#.", "#.#.", "#.#.", "####", "..#." }),
        ('5', new[] { "####", "#...", "####", "...#", "####" }),
        ('6', new[] { "###.", "#...", "####", "#..#", "####" }),
        ('7', new[] { "####", "#..#", "...#", "...#", "...#" }),
        ('8', new[] { "####", "#..#", "####", "#..#", "####" }),
        ('9', new[] { "####", "#..#", "####", "...#", ".###" }),
        ('.', new[] { ".", ".", ".", ".", "#" }),
        ('B', new[] { "###.", "#..#", "###.", "#..#", "###." }),
        ('P', new[] { "###.", "#..#", "###.", "#...", "#..." }),
        ('M', new[] { "#...#", "##.##", "#.#.#", "#...#", "#...#" }),
        ('H', new[] { "#..#", "#..#", "####", "#..#", "#..#" }),
        ('S', new[] { ".###", "#...", ".##.", "...#", "###." }),
    };

    static readonly Dictionary<string, char> ByPattern = Glyphs.ToDictionary(g => string.Join("/", g.Rows), g => g.Char);

    public static char? Lookup(IReadOnlyList<string> rows) => ByPattern.TryGetValue(string.Join("/", rows), out char c) ? c : null;
}
