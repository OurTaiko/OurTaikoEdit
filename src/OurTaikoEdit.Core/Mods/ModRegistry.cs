using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;
using OurTaikoEdit.Core.Importing;
using OurTaikoEdit.Core.Languages;
using OurTaikoEdit.Core.Localization;

namespace OurTaikoEdit.Core.Mods;

// A mod's mod.json, next to its assembly in the mod's own folder: enough to list the mod, and to
// leave it alone when it is switched off, without loading its code.
public sealed class ModManifest
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    // The assembly's file name, in the same folder.
    [JsonPropertyName("assembly")] public string Assembly { get; set; } = "";
}

// A mod the app found: loaded, switched off, or failed (Error says why).
public sealed class ModInfo
{
    public required string Id { get; init; }
    public string Version { get; init; } = "";
    // Null for a mod built into the app.
    public string? Folder { get; init; }
    public IMod? Mod { get; init; }
    public bool Enabled { get; init; } = true;
    public string? Error { get; init; }
    // The manifest's name, for a mod that is not loaded.
    public string ManifestName { get; init; } = "";

    public bool Loaded => Mod != null && Error == null;
    public string Name => Mod?.Name ?? (ManifestName.Length > 0 ? ManifestName : Id);
    public string Description => Mod?.Description ?? "";
}

// The app's mods. At startup the app loads the Mods folder next to itself (the mods it ships
// with) and the one in its data folder (the user's): every folder in them with a mod.json is a
// mod. A mod named in the settings' disabled list is listed but not loaded; one that fails to
// load is listed with the reason and the app goes on without it.
public static class ModRegistry
{
    public const string FolderName = "Mods";
    public const string ManifestFile = "mod.json";

    static readonly List<ModInfo> all = new();
    static readonly Host host = new();

    // As it is now: a copy, since mods may still be loading.
    public static IReadOnlyList<ModInfo> All
    {
        get { lock (all) return all.ToList(); }
    }

    // The mods the app ships with, and the user's.
    public static string ShippedFolder => Path.Combine(AppContext.BaseDirectory, FolderName);
    public static string UserFolder(string dataFolder) => Path.Combine(dataFolder, FolderName);

    static ModInfo? Find(string id) => all.Find(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    // A mod the caller already has as an object (built into the app, or a test's).
    public static ModInfo Load(IMod mod) => Load(mod, new ModManifest { Id = mod.Id }, null);

    // A mod from a folder goes by its manifest's id: that is what the settings switch off.
    static ModInfo Load(IMod mod, ModManifest manifest, string? folder)
    {
        lock (all) return LoadLocked(mod, manifest, folder);
    }

    static ModInfo LoadLocked(IMod mod, ModManifest manifest, string? folder)
    {
        if (Find(manifest.Id) is { Loaded: true } loaded) return loaded;
        string? error = null;
        try { mod.Load(host); }
        catch (Exception e) { error = e.Message; }
        var info = new ModInfo
        {
            Id = manifest.Id, Version = manifest.Version, Folder = folder, ManifestName = manifest.Name,
            Mod = error == null ? mod : null, Error = error,
        };
        Replace(info);
        return info;
    }

    // Every mod in the folders of a mods folder, by folder name. A folder that is not there holds
    // no mods.
    public static void LoadFolder(string folder, IReadOnlyCollection<string>? disabled = null)
    {
        if (!Directory.Exists(folder)) return;
        foreach (string dir in Directory.EnumerateDirectories(folder).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            if (File.Exists(Path.Combine(dir, ManifestFile))) LoadMod(dir, disabled);
    }

    // The mod in one folder. One whose id is already loaded is left out: the mods the app ships
    // with are loaded first.
    public static ModInfo? LoadMod(string dir, IReadOnlyCollection<string>? disabled = null)
    {
        lock (all) return LoadModLocked(dir, disabled);
    }

    static ModInfo? LoadModLocked(string dir, IReadOnlyCollection<string>? disabled)
    {
        string fallbackId = Path.GetFileName(Path.TrimEndingDirectorySeparator(dir));
        ModManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ModManifest>(File.ReadAllText(Path.Combine(dir, ManifestFile))) ?? new();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return Failed(new ModManifest { Id = fallbackId }, dir, e.Message);
        }
        if (manifest.Id.Length == 0) manifest.Id = fallbackId;
        if (Find(manifest.Id) != null) return null;
        if (disabled != null && disabled.Contains(manifest.Id, StringComparer.OrdinalIgnoreCase))
        {
            var off = new ModInfo { Id = manifest.Id, Version = manifest.Version, Folder = dir, Enabled = false, ManifestName = manifest.Name };
            all.Add(off);
            return off;
        }

        string file = Path.Combine(dir, manifest.Assembly);
        // The assembly is a file of the mod's own folder, not a path out of it.
        if (manifest.Assembly.Length == 0 || Path.GetFileName(manifest.Assembly) != manifest.Assembly || !File.Exists(file))
            return Failed(manifest, dir, Loc.F("ModNoAssembly", manifest.Assembly));
        try
        {
            var assembly = new ModContext(dir).LoadFromAssemblyPath(Path.GetFullPath(file));
            var type = assembly.GetExportedTypes().FirstOrDefault(t => typeof(IMod).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false });
            if (type == null) return Failed(manifest, dir, Loc.T("ModNoClass"));
            var mod = (IMod)Activator.CreateInstance(type)!;
            return LoadLocked(mod, manifest, dir);
        }
        catch (Exception e) when (e is BadImageFormatException or IOException or TypeLoadException or ReflectionTypeLoadException
            or MissingMethodException or TargetInvocationException or MemberAccessException or InvalidCastException)
        {
            return Failed(manifest, dir, (e as TargetInvocationException)?.InnerException?.Message ?? e.Message);
        }
    }

    static ModInfo Failed(ModManifest manifest, string dir, string error)
    {
        var info = new ModInfo { Id = manifest.Id, Version = manifest.Version, Folder = dir, Error = error, ManifestName = manifest.Name };
        Replace(info);
        return info;
    }

    static void Replace(ModInfo info)
    {
        int at = all.FindIndex(m => string.Equals(m.Id, info.Id, StringComparison.OrdinalIgnoreCase));
        if (at >= 0) all[at] = info;
        else all.Add(info);
    }

    sealed class Host : IModHost
    {
        public void AddLanguage(IChartLanguage language) => ChartLanguages.Register(language);
        public void AddImporter(IChartImporter importer) => ChartImporters.Register(importer);
        public void AddStrings(TextReader csv) => Strings.Add(csv);
    }

    // A mod's assemblies: whatever the app itself has (Core, the parser, the framework) is the
    // app's own copy, so the mod and the app mean the same types; the rest comes from the mod's
    // folder.
    sealed class ModContext(string folder) : AssemblyLoadContext(Path.GetFileName(folder))
    {
        protected override Assembly? Load(AssemblyName name)
        {
            try
            {
                Default.LoadFromAssemblyName(name);
                return null;
            }
            catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException) { }
            string file = Path.Combine(folder, name.Name + ".dll");
            return File.Exists(file) ? LoadFromAssemblyPath(Path.GetFullPath(file)) : null;
        }
    }
}
