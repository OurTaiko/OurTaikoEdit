using System.Text.Json;
using System.Text.Json.Serialization;
using OurTaikoEdit.Core.Localization;

namespace OurTaikoEdit.Core.Settings;

// The app's settings, kept as settings.json in the app's data folder next to projects.json.
// Unknown or out-of-range values fall back to the defaults; a file that cannot be read is
// replaced at the next save.
public sealed class AppSettings
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static readonly IReadOnlyList<int> Divisions = new[] { 4, 8, 12, 16, 24, 32, 48, 64, 96, 192 };

    // "system", "en", "zh" or "ja".
    [JsonPropertyName("language")] public string Language { get; set; } = "system";
    // "ui" (the interface's), "en", "zh" or "ja".
    [JsonPropertyName("messageLanguage")] public string MessageLanguage { get; set; } = "ui";
    [JsonPropertyName("projectsFolder")] public string? ProjectsFolder { get; set; }

    [JsonPropertyName("fontSize")] public double FontSize { get; set; } = 14;
    [JsonPropertyName("showHints")] public bool ShowHints { get; set; } = true;
    [JsonPropertyName("wordWrap")] public bool WordWrap { get; set; }

    [JsonPropertyName("division")] public int Division { get; set; } = 16;

    [JsonPropertyName("volume")] public double Volume { get; set; } = 0.8;
    [JsonPropertyName("hitSounds")] public bool HitSounds { get; set; } = true;
    [JsonPropertyName("hitVolume")] public double HitVolume { get; set; } = 0.8;
    // The game's hit sound set (0 is its default, 太鼓).
    [JsonPropertyName("hitSoundSet")] public int HitSoundSet { get; set; }
    // Milliseconds between the device reporting a position and the sound being heard.
    [JsonPropertyName("latencyMs")] public double LatencyMs { get; set; }

    // The ids of mods that are found but not loaded (ModRegistry).
    [JsonPropertyName("disabledMods")] public List<string> DisabledMods { get; set; } = new();

    [JsonPropertyName("window")] public WindowPlacement? Window { get; set; }

    // Where the settings are saved; null keeps them in memory (tests).
    [JsonIgnore] public string? File { get; private set; }
    // The app's data folder, which the file is in.
    [JsonIgnore] public string? DataFolder => File == null ? null : Path.GetDirectoryName(File);

    // After Save: for views that read the settings.
    public event Action? Changed;

    public static string DefaultProjectsFolder
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OurTaikoEdit");

    public string ProjectsFolderOrDefault => string.IsNullOrWhiteSpace(ProjectsFolder) ? DefaultProjectsFolder : ProjectsFolder;

    // The interface language in effect ("en", "zh" or "ja").
    public string UiLanguage => Loc.Resolve(Language);

    // The language of the chart's messages in effect.
    public string ChartMessageLanguage => Loc.Languages.Contains(MessageLanguage) ? MessageLanguage : UiLanguage;

    public static AppSettings Load(string dataFolder)
    {
        string file = Path.Combine(dataFolder, "settings.json");
        AppSettings settings;
        try { settings = System.IO.File.Exists(file) ? JsonSerializer.Deserialize<AppSettings>(System.IO.File.ReadAllText(file), Json) ?? new() : new(); }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { settings = new(); }
        settings.File = file;
        settings.Normalize();
        return settings;
    }

    // Values out of range back to something usable.
    public void Normalize()
    {
        if (Language != "system" && !Loc.Languages.Contains(Language)) Language = "system";
        if (MessageLanguage != "ui" && !Loc.Languages.Contains(MessageLanguage)) MessageLanguage = "ui";
        FontSize = double.IsFinite(FontSize) ? Math.Clamp(Math.Round(FontSize), 8, 40) : 14;
        if (!Divisions.Contains(Division)) Division = 16;
        Volume = Unit(Volume, 0.8);
        HitVolume = Unit(HitVolume, 0.8);
        if (HitSoundSet < 0) HitSoundSet = 0;
        LatencyMs = double.IsFinite(LatencyMs) ? Math.Clamp(Math.Round(LatencyMs), 0, 500) : 0;
        DisabledMods = (DisabledMods ?? new()).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (Window is { } w && !(w.Width >= 400 && w.Height >= 300 && double.IsFinite(w.Width) && double.IsFinite(w.Height))) Window = null;

        static double Unit(double v, double fallback) => double.IsFinite(v) ? Math.Clamp(v, 0, 1) : fallback;
    }

    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.Changed = null;
        copy.Window = Window == null ? null : Window with { };
        copy.DisabledMods = DisabledMods.ToList();
        return copy;
    }

    // Takes another's values (a settings dialog's edited copy), saves, and tells the views.
    public void Apply(AppSettings edited)
    {
        Language = edited.Language;
        MessageLanguage = edited.MessageLanguage;
        ProjectsFolder = string.IsNullOrWhiteSpace(edited.ProjectsFolder) ? null : edited.ProjectsFolder.Trim();
        FontSize = edited.FontSize;
        ShowHints = edited.ShowHints;
        WordWrap = edited.WordWrap;
        Division = edited.Division;
        Volume = edited.Volume;
        HitSounds = edited.HitSounds;
        HitVolume = edited.HitVolume;
        HitSoundSet = edited.HitSoundSet;
        LatencyMs = edited.LatencyMs;
        DisabledMods = edited.DisabledMods.ToList();
        Save();
    }

    // Writes the file (when there is one) and tells the views.
    public void Save()
    {
        Normalize();
        if (File != null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(File)!);
                string temp = File + ".tmp";
                System.IO.File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
                System.IO.File.Move(temp, File, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        Changed?.Invoke();
    }
}

public sealed record WindowPlacement
{
    [JsonPropertyName("width")] public double Width { get; init; }
    [JsonPropertyName("height")] public double Height { get; init; }
    [JsonPropertyName("maximized")] public bool Maximized { get; init; }
}
