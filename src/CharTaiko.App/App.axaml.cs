using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using CharTaiko.App.ViewModels;
using CharTaiko.App.Views;
using CharTaiko.Core.Languages;
using CharTaiko.Core.Projects;
using CharTaiko.Core.Settings;
using CharTaiko.Tja;

namespace CharTaiko.App;

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
            var window = new MainWindow();
            window.DataContext = new MainWindowViewModel(registry, new DialogService(window), settings);
            window.Restore(settings.Window);
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
