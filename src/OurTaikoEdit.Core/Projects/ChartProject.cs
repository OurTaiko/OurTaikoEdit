using System.Text.Json;
using System.Text.Json.Serialization;

namespace OurTaikoEdit.Core.Projects;

// A project is a folder with an ourtaikoedit.json naming its chart type and chart file; the chart's
// audio and anything else the chart uses sit next to it.
public sealed class ChartProject
{
    public const string FileName = "ourtaikoedit.json";
    // The file's name before the app was renamed from CharTaiko (2026-11-11); renamed on first open.
    public const string LegacyFileName = "chartaiko.json";

    [JsonPropertyName("name")] public string Name { get; set; } = "";
    // A chart language Id (ChartLanguages), "tja" in the first version.
    [JsonPropertyName("chartType")] public string ChartType { get; set; } = "tja";
    // Relative to the folder.
    [JsonPropertyName("chart")] public string Chart { get; set; } = "";
    [JsonPropertyName("created")] public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;
    // The editor's last block, so the project reopens where it was left.
    [JsonPropertyName("lastBlock")] public int LastBlock { get; set; }

    [JsonIgnore] public string Folder { get; private set; } = "";
    [JsonIgnore] public string ChartPath => Path.Combine(Folder, Chart);

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string PathIn(string folder) => Path.Combine(folder, FileName);

    // Whether the folder is a project, under the current or the legacy file name.
    public static bool IsProject(string folder)
        => File.Exists(PathIn(folder)) || File.Exists(Path.Combine(folder, LegacyFileName));

    public static ChartProject Load(string folder)
    {
        string legacy = Path.Combine(folder, LegacyFileName);
        if (!File.Exists(PathIn(folder)) && File.Exists(legacy)) File.Move(legacy, PathIn(folder));
        var project = JsonSerializer.Deserialize<ChartProject>(File.ReadAllText(PathIn(folder)), Json)
            ?? throw new InvalidDataException($"{FileName} is empty.");
        project.Folder = folder;
        return project;
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(PathIn(Folder), JsonSerializer.Serialize(this, Json));
    }

    public static ChartProject Create(string folder, string name, string chartType, string chart)
        => new() { Name = name, ChartType = chartType, Chart = chart, Folder = folder };
}
