using Avalonia;
using CharTaiko.App.Localization;
using CharTaiko.Core.Projects;
using CharTaiko.Core.Settings;

namespace CharTaiko.App;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .With(Fonts.Options(AppSettings.Load(ProjectRegistry.DefaultDataFolder).UiLanguage))
            .LogToTrace();
}
