using OurTaikoEdit.Core.Languages;

namespace OurTaikoEdit.Core.Importing;

// Something a new project's chart can be made from instead of the language's template: a file of
// another kind (a picture of the chart, another program's format), read and written out as one
// of the chart languages. Mods register theirs with the mod host; the new project dialog lists
// them and offers, as chart types, the languages the chosen one can write.
public interface IChartImporter
{
    string Id { get; }
    // What it reads, as the dialog names it, in the interface's language ("Chart image").
    string DisplayName { get; }
    // The files it reads, as patterns for the file picker ("*.png").
    IReadOnlyList<string> Patterns { get; }

    // Whether it can write what it reads as this language.
    bool CanWrite(IChartLanguage target);

    // Reads the source and writes it as the target language. A source that cannot be read as a
    // chart is a ChartImportException whose message is for the user.
    ImportedChart Import(ChartImportRequest request);
}

// Source is the file to read; Title and AudioFile (the audio's name next to the chart, if any)
// are the new project's, for the chart's headers.
public sealed record ChartImportRequest(string Source, IChartLanguage Target, string Title, string? AudioFile);

// Text is the chart in the target language. Report says, for the user, what was read and what
// the source does not carry and so was left at a default.
public sealed record ImportedChart(string Text, string Report);

public sealed class ChartImportException(string message) : Exception(message);

// The importers the app knows, by Id, in the order they were registered.
public static class ChartImporters
{
    static volatile IReadOnlyList<IChartImporter> importers = Array.Empty<IChartImporter>();

    public static IReadOnlyList<IChartImporter> All => importers;

    public static void Register(IChartImporter importer)
    {
        lock (typeof(ChartImporters))
        {
            var next = importers.ToList();
            int at = next.FindIndex(i => string.Equals(i.Id, importer.Id, StringComparison.OrdinalIgnoreCase));
            if (at >= 0) next[at] = importer;
            else next.Add(importer);
            importers = next;
        }
    }

    public static IChartImporter? Find(string? id)
        => id == null ? null : importers.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));
}
