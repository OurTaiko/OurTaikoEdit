using Avalonia.Controls;
using CharTaiko.App.ViewModels;
using CharTaiko.Core.Settings;

namespace CharTaiko.App.Views;

public partial class MainWindow : Window
{
    bool closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        Closing += async (_, e) =>
        {
            if (closeConfirmed || DataContext is not MainWindowViewModel vm) return;
            e.Cancel = true;
            if (!await vm.CanCloseAsync()) return;
            closeConfirmed = true;
            vm.Settings.Window = Placement();
            vm.Settings.Save();
            Close();
        };
    }

    // The size the window had last time (not the position: the screens may have changed).
    public void Restore(WindowPlacement? placement)
    {
        if (placement == null) return;
        Width = placement.Width;
        Height = placement.Height;
        if (placement.Maximized) WindowState = WindowState.Maximized;
    }

    WindowPlacement Placement()
        => WindowState == WindowState.Maximized && ClientSize.Width > 0
            ? new WindowPlacement { Width = Width, Height = Height, Maximized = true }
            : new WindowPlacement { Width = ClientSize.Width, Height = ClientSize.Height };
}
