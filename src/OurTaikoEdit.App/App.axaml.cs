using Avalonia;
using Avalonia.Controls;
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
    // How picked files become paths where the app runs without a file system of its own (a browser).
    public static IPickedFiles? PickedFiles { get; set; }

    // The host's last setup once the platform is up and before the first view (a browser's fonts).
    public static Action? Starting { get; set; }

    // The app's one view model where there is one view (a browser): what a host page talks to.
    public static MainWindowViewModel? Main { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    static (ProjectRegistry, AppSettings) Start()
        => (new ProjectRegistry(ProjectRegistry.DefaultDataFolder), AppSettings.Load(ProjectRegistry.DefaultDataFolder));

    public override void OnFrameworkInitializationCompleted()
    {
        Starting?.Invoke();
        // The chart languages: TJA in the first version.
        ChartLanguages.Register(new TjaLanguage());
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var (registry, settings) = Start();
            // Mods: the ones the app ships with, then the user's.
            ModRegistry.LoadFolder(ModRegistry.ShippedFolder, settings.DisabledMods);
            ModRegistry.LoadFolder(ModRegistry.UserFolder(ProjectRegistry.DefaultDataFolder), settings.DisabledMods);
            var window = new MainWindow();
            window.DataContext = new MainWindowViewModel(registry, new DialogService(window), settings);
            window.Restore(settings.Window);
            desktop.MainWindow = window;
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime single)
        {
            // A browser: one view, dialogs over the page, picked files copied into the app's
            // storage (the host's PickedFiles); the host has loaded the mods it ships with.
            var (registry, settings) = Start();
            var view = new MainView();
            var dialogs = new DialogService(() => TopLevel.GetTopLevel(view), new OverlayDialogHost(view.Dialogs), PickedFiles ?? new LocalFiles());
            view.DataContext = Main = new MainWindowViewModel(registry, dialogs, settings);
            single.MainView = view;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
