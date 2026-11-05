using System.Text.Json;
using System.Text.Json.Serialization;

namespace CharTaiko.Core.Projects;

// A project is a folder with a chartaiko.json naming its chart type and chart file; the chart's
// audio and anything else the chart uses sit next to it.
public sealed class ChartProject
{
    public const string FileName = "chartaiko.json";

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

    public static ChartProject Load(string folder)
    {
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
