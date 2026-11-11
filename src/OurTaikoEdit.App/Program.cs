using Avalonia;
using OurTaikoEdit.App.Localization;
using OurTaikoEdit.Core.Projects;
using OurTaikoEdit.Core.Settings;

namespace OurTaikoEdit.App;

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
