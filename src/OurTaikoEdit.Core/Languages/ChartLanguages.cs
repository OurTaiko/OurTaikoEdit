namespace OurTaikoEdit.Core.Languages;

// The chart languages the app knows, by Id. The app registers them at startup.
public static class ChartLanguages
{
    static readonly Dictionary<string, IChartLanguage> languages = new(StringComparer.OrdinalIgnoreCase);

    public static IEnumerable<IChartLanguage> All => languages.Values;

    public static void Register(IChartLanguage language) => languages[language.Id] = language;

    public static IChartLanguage? Find(string? id) => id != null && languages.TryGetValue(id, out var l) ? l : null;

    public static IChartLanguage? ForFile(string path)
        => languages.Values.FirstOrDefault(l => string.Equals(Path.GetExtension(path), l.FileExtension, StringComparison.OrdinalIgnoreCase));
}
