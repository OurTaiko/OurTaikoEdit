using System.Text.Json;
using System.Text.Json.Serialization;
using CharTaiko.Core.Languages;
using CharTaiko.Core.Localization;

namespace CharTaiko.Core.Projects;

public sealed class TrackedProject
{
    [JsonPropertyName("folder")] public string Folder { get; set; } = "";
    [JsonPropertyName("lastOpened")] public DateTimeOffset? LastOpened { get; set; }
}

// The projects the app tracks: a list of folders kept in the app's data folder
// (projects.json), most recently opened first.
public sealed class ProjectRegistry
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    readonly string file;
    readonly List<TrackedProject> projects;

    public ProjectRegistry(string dataFolder)
    {
        file = Path.Combine(dataFolder, "projects.json");
        projects = File.Exists(file)
            ? JsonSerializer.Deserialize<List<TrackedProject>>(File.ReadAllText(file), Json) ?? new()
            : new();
    }

    public static string DefaultDataFolder
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CharTaiko");

    public IReadOnlyList<TrackedProject> Projects
        => projects.OrderByDescending(p => p.LastOpened ?? DateTimeOffset.MinValue).ToList();

    void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(projects, Json));
    }

    TrackedProject? Find(string folder)
        => projects.Find(p => string.Equals(Path.GetFullPath(p.Folder), Path.GetFullPath(folder), StringComparison.Ordinal));

    public void Track(string folder)
    {
        if (Find(folder) == null) projects.Add(new TrackedProject { Folder = Path.GetFullPath(folder) });
        Save();
    }

    public void Untrack(string folder)
    {
        if (Find(folder) is { } p) projects.Remove(p);
        Save();
    }

    public void MarkOpened(string folder)
    {
        var p = Find(folder);
        if (p == null) { p = new TrackedProject { Folder = Path.GetFullPath(folder) }; projects.Add(p); }
        p.LastOpened = DateTimeOffset.Now;
        Save();
    }

    // A new project folder `parent/name` with the chart from the language's template and, when
    // given, a copy of the audio file next to it.
    public ChartProject CreateProject(string parent, string name, IChartLanguage language, string? audioSource)
    {
        string folder = Path.Combine(parent, SafeName(name));
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
            throw new IOException(Loc.F("FolderNotEmpty", folder));
        Directory.CreateDirectory(folder);
        string? audio = null;
        if (!string.IsNullOrEmpty(audioSource))
        {
            audio = Path.GetFileName(audioSource);
            File.Copy(audioSource, Path.Combine(folder, audio), overwrite: false);
        }
        string chart = SafeName(name) + language.FileExtension;
        ChartFile.Write(Path.Combine(folder, chart), new ChartText(language.CreateTemplate(name, audio)));
        var project = ChartProject.Create(folder, name, language.Id, chart);
        project.Save();
        Track(folder);
        return project;
    }

    // Opens a folder as a project: its chartaiko.json, or, without one, a new one for the first
    // chart file a known language reads.
    public ChartProject OpenFolder(string folder)
    {
        ChartProject project;
        if (File.Exists(ChartProject.PathIn(folder))) project = ChartProject.Load(folder);
        else
        {
            var chart = Directory.EnumerateFiles(folder).OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Select(f => (File: f, Language: ChartLanguages.ForFile(f))).FirstOrDefault(c => c.Language != null);
            if (chart.Language == null) throw new FileNotFoundException(Loc.T("NoChartFile"));
            project = ChartProject.Create(folder, Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)), chart.Language.Id, Path.GetFileName(chart.File));
            project.Save();
        }
        Track(folder);
        return project;
    }

    static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':' }).ToHashSet();
        string safe = new string(name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return safe.Length == 0 ? "Untitled" : safe;
    }
}
