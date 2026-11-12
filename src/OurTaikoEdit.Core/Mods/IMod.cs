using OurTaikoEdit.Core.Importing;
using OurTaikoEdit.Core.Languages;

namespace OurTaikoEdit.Core.Mods;

// An addition to the app that lives in its own assembly and knows the app only through this
// project (OurTaikoEdit.Core): it is handed a host and registers what it adds. The assembly has
// one public class with this interface and a parameterless constructor.
public interface IMod
{
    // What the mod goes by when the app is handed it as an object; one loaded from a folder goes
    // by the "id" of its mod.json, which should be the same.
    string Id { get; }
    // In the interface's language; read again whenever they are shown.
    string Name { get; }
    string Description { get; }

    void Load(IModHost host);
}

// What a mod can add.
public interface IModHost
{
    // A chart type projects can be of.
    void AddLanguage(IChartLanguage language);
    // Something a new project's chart can be made from.
    void AddImporter(IChartImporter importer);
    // The mod's words for Loc, as a CSV like the app's Strings.csv (key, en, zh, ja). Keys the
    // table already has are kept as they are, so a mod names its keys after itself.
    void AddStrings(TextReader csv);
}
