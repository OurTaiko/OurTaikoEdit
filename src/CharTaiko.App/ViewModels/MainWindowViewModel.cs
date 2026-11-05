using CharTaiko.Core.Projects;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CharTaiko.App.ViewModels;

// Switches between the two scenes: the project list and a project's editor.
public sealed partial class MainWindowViewModel : ViewModelBase
{
    readonly ProjectRegistry registry;
    readonly IDialogService dialogs;

    [ObservableProperty] ViewModelBase scene;
    [ObservableProperty] string title = "CharTaiko";

    public MainWindowViewModel(ProjectRegistry registry, IDialogService dialogs)
    {
        this.registry = registry;
        this.dialogs = dialogs;
        scene = new ProjectsViewModel(registry, dialogs, Open);
    }

    void Open(ChartProject project)
    {
        registry.MarkOpened(project.Folder);
        var editor = new EditorViewModel(project, dialogs, ShowProjects);
        Scene = editor;
        Title = $"{project.Name} — CharTaiko";
    }

    void ShowProjects()
    {
        Scene = new ProjectsViewModel(registry, dialogs, Open);
        Title = "CharTaiko";
    }

    // Whether the window may close: the editor asks about unsaved changes.
    public Task<bool> CanCloseAsync() => Scene is EditorViewModel editor ? editor.ConfirmLeaveAsync() : Task.FromResult(true);
}
