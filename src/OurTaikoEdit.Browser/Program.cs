using System.Globalization;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Browser;
using Avalonia.Media;
using OurTaikoEdit.App;
using OurTaikoEdit.Core.Mods;

[assembly: SupportedOSPlatform("browser")]

namespace OurTaikoEdit.Browser;

static class Program
{
    static async Task Main(string[] args)
    {
        UseThePagesLanguage();
        Sounds.Unpack();
        ModRegistry.Load(new OurTaikoEdit.Mods.ChartImage.ChartImageMod());
        App.App.PickedFiles = new BrowserFiles();
        OurTaikoEdit.App.ViewModels.EditorViewModel.OpenPlayer = WebAudioPlayer.Open;
        OurTaikoEdit.Core.Transcoding.TranscodeFormat.Wasm = true;
        OurTaikoEdit.App.ViewModels.TranscodeViewModel.BuiltIn = new WasmFfmpeg();
        OurTaikoEdit.App.Export.VideoExporter.CreateSink = () => new WebCodecsSink();
        await CjkFont.LoadAsync();
        App.App.Starting = CjkFont.Register;
        var builder = BuildAvaloniaApp().WithInterFont();
        if (CjkFont.HasFont)
            builder = builder.With(new FontManagerOptions { FontFallbacks = new[] { new FontFallback { FontFamily = new FontFamily(CjkFont.Family) } } });
        await builder.StartBrowserAppAsync("out");
        Interop.Loaded();
        _ = Interop.PersistAsync();
    }

    // The interface language "system" is the page's: ?lang= (the website gives its own) or else the
    // browser's (.NET in a browser starts with no culture of its own, which shows English).
    static void UseThePagesLanguage()
    {
        var global = JSHost.GlobalThis;
        string search = global.GetPropertyAsJSObject("location")?.GetPropertyAsString("search") ?? "";
        string? lang = search.TrimStart('?').Split('&')
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2 && p[0] == "lang")
            .Select(p => Uri.UnescapeDataString(p[1]))
            .FirstOrDefault();
        lang ??= global.GetPropertyAsJSObject("navigator")?.GetPropertyAsString("language");
        if (string.IsNullOrWhiteSpace(lang)) return;
        try
        {
            var culture = CultureInfo.GetCultureInfo(lang);
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }
        catch (CultureNotFoundException) { }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App.App>();
}
