using Avalonia;
using Avalonia.Headless;
using OurTaikoEdit.App.Export;
using OurTaikoEdit.App.Localization;
using OurTaikoEdit.Core.Projects;
using OurTaikoEdit.Core.Settings;

namespace OurTaikoEdit.App;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // A copy of the app drawing a video's frames for the app (ProcessFrames): no window.
        if (args is [ProcessFrames.Argument])
        {
            BuildHeadlessApp().SetupWithoutStarting();
            ProcessFrames.Child();
            return;
        }
        VideoExporter.DrawElsewhere = ProcessFrames.RunAsync;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .With(Fonts.Options(AppSettings.Load(ProjectRegistry.DefaultDataFolder).UiLanguage))
            .LogToTrace();

    // Avalonia without a window, drawing with Skia into memory, with the app's fonts.
    public static AppBuilder BuildHeadlessApp()
        => AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
            .With(Fonts.Options(AppSettings.Load(ProjectRegistry.DefaultDataFolder).UiLanguage));
}
