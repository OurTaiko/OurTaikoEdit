using System.Text;
using OurTaikoEdit.Core.Importing;
using OurTaikoEdit.Core.Languages;
using OurTaikoEdit.Core.Localization;

namespace OurTaikoEdit.Mods.ChartImage;

// A new project's chart from a picture of it. The picture is decoded into measures
// (ChartImageDecoder), which a writer for the target language turns into text; TJA is the one
// written so far, so TJA is the chart type offered. What is written is read back by the target
// language and compared with what was decoded before it is handed over.
public sealed class ChartImageImporter : IChartBlockImporter
{
    public string Id => "chart-image";
    public string DisplayName => Loc.T("ChartImageSource");
    public IReadOnlyList<string> Patterns { get; } = new[] { "*.png" };

    public bool CanWrite(IChartLanguage target) => string.Equals(target.Id, "tja", StringComparison.OrdinalIgnoreCase);

    public ImportedChart Import(ChartImportRequest request)
    {
        try
        {
            if (!CanWrite(request.Target)) throw new DecodeException("ChartImageTarget", request.Target.DisplayName);
            var chart = ChartImageDecoder.Decode(request.Source);
            string text = WriteTja(chart, request.Title, request.AudioFile);
            VerifyRoundTrip(chart, request.Target, text);
            return new ImportedChart(text, Report(chart));
        }
        catch (DecodeException e)
        {
            throw new ChartImportException(Loc.F("ChartImageFailed", e.Message) + "\n" + Loc.T("ChartImageHint"));
        }
    }

    public ImportedBlock ImportBlock(string source, IChartLanguage target)
    {
        try
        {
            if (!CanWrite(target)) throw new DecodeException("ChartImageTarget", target.DisplayName);
            var chart = ChartImageDecoder.Decode(source);
            var body = WriteBody(chart);
            VerifyRoundTrip(chart, target, WriteTja(chart, "", null, body));
            return new ImportedBlock(chart.Bpm, chart.BalloonHits, body);
        }
        catch (DecodeException e)
        {
            throw new ChartImportException(Loc.F("ChartImageFailed", e.Message) + "\n" + Loc.T("ChartImageHint"));
        }
    }

    // The decoded chart as TJA. The image has no title a program can read, no course or level
    // and no audio timing: the title and audio are the project's, the rest the usual defaults.
    public static string WriteTja(DecodedChart chart, string title, string? audioFile)
        => WriteTja(chart, title, audioFile, WriteBody(chart));

    static string WriteTja(DecodedChart chart, string title, string? audioFile, IReadOnlyList<string> body)
    {
        var lines = new List<string>
        {
            $"TITLE:{title}",
            "SUBTITLE:--",
            $"BPM:{ChartImageDecoder.FormatNumber(chart.Bpm)}",
            $"WAVE:{audioFile ?? ""}",
            "OFFSET:0",
            "DEMOSTART:0",
            "",
            "COURSE:Oni",
            "LEVEL:0",
            $"BALLOON:{string.Join(",", chart.BalloonHits)}",
            "",
            "#START",
        };
        lines.AddRange(body);
        lines.Add("#END");
        lines.Add("");
        return string.Join("\n", lines);
    }

    static List<string> WriteBody(DecodedChart chart)
    {
        var lines = new List<string>();
        string signature = "4/4";
        foreach (var measure in chart.Measures)
        {
            string next = ChartImageDecoder.TimeSignature(measure.Beats);
            if (next != signature) lines.Add($"#MEASURE {signature = next}");
            // A command inside the measure splits its notes over lines, the command between them.
            int written = 0;
            foreach (var command in measure.Commands.OrderBy(c => c.NoteIndex))
            {
                if (command.NoteIndex > written)
                {
                    lines.Add(new string(measure.Notes.Skip(written).Take(command.NoteIndex - written).ToArray()));
                    written = command.NoteIndex;
                }
                lines.Add(command.Text);
            }
            lines.Add(new string(measure.Notes.Skip(written).ToArray()) + ",");
        }
        return lines;
    }

    // The target language reads the text back: no errors, and every measure the characters that
    // were decoded. This catches a writer's mistake, such as a command splitting a measure in
    // the wrong place.
    static void VerifyRoundTrip(DecodedChart chart, IChartLanguage target, string text)
    {
        var analysis = target.Analyze(text, 0);
        var errors = analysis.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (analysis.Chart == null || analysis.HasErrors || errors.Count > 0)
            throw new DecodeException("ChartImageRoundTripErrors", errors.Count > 0 ? errors[0].Message : "—");
        if (analysis.Measures.Count != chart.Measures.Count)
            throw new DecodeException("ChartImageRoundTrip", Math.Min(analysis.Measures.Count, chart.Measures.Count) + 1);
        for (int i = 0; i < chart.Measures.Count; i++)
        {
            string written = new(chart.Measures[i].Notes.ToArray()), read = string.Concat(analysis.Measures[i].Slots.Select(s => s.Token));
            if (written != read) throw new DecodeException("ChartImageRoundTrip", chart.Measures[i].Number);
        }
    }

    // What was read, the measures drawn too small to be sure of, and what an image does not say.
    static string Report(DecodedChart chart)
    {
        int notes = chart.Measures.Sum(m => m.Notes.Count(n => n is >= '1' and <= '4'));
        int longs = chart.Measures.Sum(m => m.Notes.Count(n => n is >= '5' and <= '7'));
        var report = new StringBuilder(Loc.F("ChartImageReport", chart.Measures.Count, notes, longs));
        var tight = chart.Measures.Where(m => m.Beats * chart.PixelsPerBeat / m.Subdivisions < 4).Select(m => m.Number).ToList();
        if (tight.Count > 0) report.Append("\n\n").Append(Loc.F("ChartImageTight", string.Join(", ", tight)));
        report.Append("\n\n").Append(Loc.T("ChartImageMissing"));
        return report.ToString();
    }
}
