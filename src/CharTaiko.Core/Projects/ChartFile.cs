using System.Text;

namespace CharTaiko.Core.Projects;

// A chart's text with how it was stored, so saving writes it back the same way. TJA files are
// often Shift-JIS; TJAParser reads UTF-8 text, which the editor always has in memory.
public sealed record ChartText(string Text, string EncodingName = "utf-8", bool Bom = false);

public static class ChartFile
{
    static ChartFile() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static ChartText Read(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        try
        {
            return new ChartText(StrictUtf8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)), "utf-8", bom);
        }
        catch (DecoderFallbackException)
        {
            // Not UTF-8: the TJA tradition is Shift-JIS (code page 932).
            return new ChartText(Encoding.GetEncoding(932).GetString(bytes), "shift_jis", false);
        }
    }

    public static void Write(string path, ChartText chart)
    {
        var encoding = chart.EncodingName == "utf-8" ? new UTF8Encoding(chart.Bom) : Encoding.GetEncoding(chart.EncodingName);
        // Write to a temporary file first so a failed save never leaves half a chart.
        string temp = path + ".saving";
        File.WriteAllText(temp, chart.Text, encoding);
        File.Move(temp, path, overwrite: true);
    }
}
