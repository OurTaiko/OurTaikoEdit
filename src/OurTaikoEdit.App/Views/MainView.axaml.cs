using Avalonia.Controls;

namespace OurTaikoEdit.App.Views;

public partial class MainView : UserControl
{
    public MainView() => InitializeComponent();

    // Where dialogs shown in the page go (OverlayDialogHost).
    public Panel Dialogs => DialogLayer;
}
