using System.Text;

namespace OurTaikoEdit.Core.Localization;

public readonly record struct Words(string En, string Zh, string Ja);

// Every word of the interface, read from Strings.csv (built into the assembly): one row per key,
// with the languages side by side so that a missing one shows. Columns are found by the header
// (key, en, zh, ja). A row whose key starts with "#" heads a section and blank lines are skipped.
// {0}, {1} are filled by Loc.F; a key ending in "One" is English's singular for Loc.Plural.
public static class Strings
{
    public const string ResourceName = "OurTaikoEdit.Core.Localization.Strings.csv";

    public static IReadOnlyDictionary<string, Words> Table { get; } = Load();

    static Dictionary<string, Words> Load()
    {
        using var stream = typeof(Strings).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"{ResourceName} is not in the assembly.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Read(reader);
    }

    // The table in a CSV (RFC 4180: fields with commas, quotes or line breaks are quoted, "" is a quote).
    public static Dictionary<string, Words> Read(TextReader reader)
    {
        var rows = Csv.Read(reader).ToList();
        if (rows.Count == 0) throw new FormatException("Strings.csv is empty.");
        var (headerLine, header) = rows[0];
        int Column(string name)
        {
            int i = Array.IndexOf(header, name);
            return i >= 0 ? i : throw new FormatException($"Strings.csv line {headerLine}: no “{name}” column.");
        }
        int key = Column("key"), en = Column("en"), zh = Column("zh"), ja = Column("ja");

        var table = new Dictionary<string, Words>();
        foreach (var (line, fields) in rows.Skip(1))
        {
            if (fields.All(f => f.Length == 0)) continue;
            if (fields.Length != header.Length)
                throw new FormatException($"Strings.csv line {line}: {fields.Length} fields, the header has {header.Length}.");
            string name = fields[key];
            if (name.StartsWith('#')) continue;
            if (name.Length == 0) throw new FormatException($"Strings.csv line {line}: no key.");
            if (!table.TryAdd(name, new Words(fields[en], fields[zh], fields[ja])))
                throw new FormatException($"Strings.csv line {line}: “{name}” is there twice.");
        }
        return table;
    }
}

static class Csv
{
    // Records with the line each starts on. Fields are kept as written (no trimming).
    public static IEnumerable<(int Line, string[] Fields)> Read(TextReader reader)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        int line = 1, start = 1;
        bool quoted = false, any = false;
        while (true)
        {
            int c = reader.Read();
            if (quoted)
            {
                if (c < 0) throw new FormatException($"Strings.csv line {start}: a quote is not closed.");
                if (c == '"')
                {
                    if (reader.Peek() == '"') { reader.Read(); field.Append('"'); }
                    else quoted = false;
                }
                else
                {
                    if (c == '\n') line++;
                    field.Append((char)c);
                }
                continue;
            }
            if (c == '"' && field.Length == 0) { quoted = any = true; continue; }
            if (c == ',') { fields.Add(field.ToString()); field.Clear(); any = true; continue; }
            if (c == '\r' && reader.Peek() == '\n') continue;
            if (c == '\n' || c < 0)
            {
                if (any || field.Length > 0)
                {
                    fields.Add(field.ToString());
                    yield return (start, fields.ToArray());
                }
                fields.Clear(); field.Clear(); any = false;
                if (c < 0) yield break;
                start = ++line;
                continue;
            }
            field.Append((char)c);
            any = true;
        }
    }
}
