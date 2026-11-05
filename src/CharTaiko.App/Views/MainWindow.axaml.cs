using Avalonia.Controls;
using CharTaiko.App.ViewModels;

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
            if (await vm.CanCloseAsync()) { closeConfirmed = true; Close(); }
        };
    }
}
