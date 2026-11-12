using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using OurTaikoEdit.App.ViewModels;
using OurTaikoEdit.App.Views;
using OurTaikoEdit.Core.Languages;
using OurTaikoEdit.Core.Mods;
using OurTaikoEdit.Core.Projects;
using OurTaikoEdit.Core.Settings;
using OurTaikoEdit.Tja;

namespace OurTaikoEdit.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // The chart languages: TJA in the first version.
        ChartLanguages.Register(new TjaLanguage());
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var registry = new ProjectRegistry(ProjectRegistry.DefaultDataFolder);
            var settings = AppSettings.Load(ProjectRegistry.DefaultDataFolder);
            // Mods: the ones the app ships with, then the user's.
            ModRegistry.LoadFolder(ModRegistry.ShippedFolder, settings.DisabledMods);
            ModRegistry.LoadFolder(ModRegistry.UserFolder(ProjectRegistry.DefaultDataFolder), settings.DisabledMods);
            var window = new MainWindow();
            window.DataContext = new MainWindowViewModel(registry, new DialogService(window), settings);
            window.Restore(settings.Window);
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
