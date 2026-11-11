using Avalonia.Controls;
using Avalonia.Controls.Templates;
using OurTaikoEdit.App.ViewModels;
using OurTaikoEdit.App.Views;

namespace OurTaikoEdit.App;

// The view for each scene's view model; explicit, so trimming keeps working.
public sealed class ViewLocator : IDataTemplate
{
    public Control? Build(object? param) => param switch
    {
        ProjectsViewModel => new ProjectsView(),
        EditorViewModel => new EditorView(),
        _ => new TextBlock { Text = "No view for " + param?.GetType().Name },
    };

    public bool Match(object? data) => data is ViewModelBase;
}
