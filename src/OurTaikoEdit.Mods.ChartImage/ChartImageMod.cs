using System.Text;
using OurTaikoEdit.Core.Localization;
using OurTaikoEdit.Core.Mods;

namespace OurTaikoEdit.Mods.ChartImage;

// Adds "chart image" to what a new project can start from.
public sealed class ChartImageMod : IMod
{
    public string Id => "chart-image";
    public string Name => Loc.T("ChartImageModName");
    public string Description => Loc.T("ChartImageModInfo");

    public void Load(IModHost host)
    {
        using var stream = typeof(ChartImageMod).Assembly.GetManifestResourceStream("OurTaikoEdit.Mods.ChartImage.Strings.csv")
            ?? throw new InvalidOperationException("Strings.csv is not in the assembly.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        host.AddStrings(reader);
        host.AddImporter(new ChartImageImporter());
    }
}
