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

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App.App>();
}
