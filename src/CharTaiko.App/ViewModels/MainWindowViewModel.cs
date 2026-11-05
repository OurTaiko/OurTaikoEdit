using CharTaiko.Core.Localization;
using CharTaiko.Core.Projects;
using CharTaiko.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CharTaiko.App.ViewModels;

// Switches between the two scenes: the project list and a project's editor. Holds the settings
// and passes their changes on: the interface language here, the rest to the scene.
public sealed partial class MainWindowViewModel : ViewModelBase
{
    readonly ProjectRegistry registry;
    readonly IDialogService dialogs;

    public AppSettings Settings { get; }

    [ObservableProperty] ViewModelBase scene;
    [ObservableProperty] string title = "CharTaiko";

    public MainWindowViewModel(ProjectRegistry registry, IDialogService dialogs, AppSettings? settings = null)
    {
        this.registry = registry;
        this.dialogs = dialogs;
        Settings = settings ?? new AppSettings();
        Loc.Instance.Language = Settings.UiLanguage;
        Settings.Changed += SettingsChanged;
        scene = new ProjectsViewModel(registry, dialogs, Open, Settings);
    }

    void SettingsChanged()
    {
        Loc.Instance.Language = Settings.UiLanguage;
        switch (Scene)
        {
            case ProjectsViewModel projects: projects.Refresh(); break;
            case EditorViewModel editor: editor.ApplySettings(); break;
        }
    }

    void Open(ChartProject project)
    {
        registry.MarkOpened(project.Folder);
        var editor = new EditorViewModel(project, dialogs, ShowProjects, Settings);
        Scene = editor;
        Title = $"{project.Name} — CharTaiko";
    }

    void ShowProjects()
    {
        Scene = new ProjectsViewModel(registry, dialogs, Open, Settings);
        Title = "CharTaiko";
    }

    // Whether the window may close: the editor asks about unsaved changes.
    public async Task<bool> CanCloseAsync()
    {
        if (Scene is not EditorViewModel editor) return true;
        if (!await editor.ConfirmLeaveAsync()) return false;
        editor.Close();
        return true;
    }
}
