using Avalonia.Controls;
using Avalonia.Input;
using OurTaikoEdit.App.ViewModels;

namespace OurTaikoEdit.App.Views;

public partial class ProjectsView : UserControl
{
    public ProjectsView()
    {
        InitializeComponent();
        List.DoubleTapped += (_, _) => OpenSelected();
        List.KeyDown += (_, e) => { if (e.Key == Key.Enter) OpenSelected(); };
    }

    void OpenSelected()
    {
        if (DataContext is ProjectsViewModel vm && vm.Selected != null) vm.OpenCommand.Execute(vm.Selected);
    }
}
