using System.Text;

namespace OurTaikoEdit.Core.Projects;

// A chart's text and the encoding it was read in. Charts are read in whatever they were written
// in, but always saved as UTF-8 without a BOM: the game and both sites read UTF-8 only.
public sealed record ChartText(string Text, string EncodingName = "utf-8", bool Bom = false)
{
    // Whether saving changes the file's encoding (not counting a UTF-8 BOM being dropped).
    public bool IsConverted => EncodingName != "utf-8";
}

public static class ChartFile
{
    static ChartFile() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    static readonly UTF8Encoding StrictUtf8 = new(false, true);
    static readonly UTF8Encoding Utf8NoBom = new(false);

    // The encodings a chart can be read in, by name: TJA files come from Japanese (Shift-JIS),
    // Chinese (GB18030, Big5) and Korean (EUC-KR) players as well as in UTF-8 and UTF-16.
    public static IReadOnlyList<string> Encodings { get; } = new[] { "utf-8", "shift_jis", "gb18030", "big5", "euc-kr", "utf-16", "utf-16BE" };

    static Encoding EncodingOf(string name) => name switch
    {
        "utf-8" => StrictUtf8,
        "shift_jis" => Encoding.GetEncoding(932),
        "utf-16" => new UnicodeEncoding(false, false),
        "utf-16BE" => new UnicodeEncoding(true, false),
        _ => Encoding.GetEncoding(name),
    };

    // Reads a chart: with an encoding, in that one; otherwise a BOM decides, then strict UTF-8,
    // then Shift-JIS (the TJA tradition).
    public static ChartText Read(string path, string? encoding = null)
    {
        byte[] bytes = File.ReadAllBytes(path);
        int bomLength = bytes switch
        {
            [0xEF, 0xBB, 0xBF, ..] => 3,
            [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..] => 2,
            _ => 0,
        };
        if (encoding == null)
        {
            if (bomLength == 2) encoding = bytes[0] == 0xFF ? "utf-16" : "utf-16BE";
            else
            {
                try { return new ChartText(StrictUtf8.GetString(bytes, bomLength, bytes.Length - bomLength), "utf-8", bomLength == 3); }
                catch (DecoderFallbackException) { encoding = "shift_jis"; }
            }
        }
        // A BOM is skipped only when it belongs to the chosen encoding.
        int skip = encoding switch
        {
            "utf-8" when bomLength == 3 => 3,
            "utf-16" when bytes is [0xFF, 0xFE, ..] => 2,
            "utf-16BE" when bytes is [0xFE, 0xFF, ..] => 2,
            _ => 0,
        };
        var decoder = encoding == "utf-8" ? Utf8NoBom : EncodingOf(encoding);
        return new ChartText(decoder.GetString(bytes, skip, bytes.Length - skip), encoding, skip > 0);
    }

    // Always UTF-8 without a BOM.
    public static void Write(string path, ChartText chart)
    {
        // Write to a temporary file first so a failed save never leaves half a chart.
        string temp = path + ".saving";
        File.WriteAllText(temp, chart.Text, Utf8NoBom);
        File.Move(temp, path, overwrite: true);
    }
}
