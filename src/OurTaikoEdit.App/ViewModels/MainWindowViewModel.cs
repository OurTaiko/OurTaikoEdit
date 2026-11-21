using OurTaikoEdit.Core.Ffmpeg;
using OurTaikoEdit.Core.Localization;
using OurTaikoEdit.Core.Projects;
using OurTaikoEdit.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace OurTaikoEdit.App.ViewModels;

// Switches between the two scenes: the project list and a project's editor. Holds the settings
// and passes their changes on: the interface language here, the rest to the scene.
public sealed partial class MainWindowViewModel : ViewModelBase
{
    readonly ProjectRegistry registry;
    readonly IDialogService dialogs;

    public AppSettings Settings { get; }

    [ObservableProperty] ViewModelBase scene;
    [ObservableProperty] string title = "OurTaikoEdit";
    // The play scene, shown over the editor while a chart is being played (the editor stays
    // underneath as it is: its text, its undo history, its song).
    [ObservableProperty] PlayViewModel? play;
    // The transcoder, shown over the project list or the editor (which stay as they are).
    [ObservableProperty] TranscodeViewModel? transcode;

    // The ffmpeg the app downloads into its data folder (and finds).
    public FfmpegTools Tools { get; }

    public MainWindowViewModel(ProjectRegistry registry, IDialogService dialogs, AppSettings? settings = null)
    {
        this.registry = registry;
        this.dialogs = dialogs;
        Settings = settings ?? new AppSettings();
        Loc.Instance.Language = Settings.UiLanguage;
        Settings.Changed += SettingsChanged;
        Tools = new FfmpegTools(Settings.DataFolder ?? Path.Combine(Path.GetTempPath(), "OurTaikoEdit"));
        scene = NewProjects();
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
        var editor = new EditorViewModel(project, dialogs, ShowProjects, Settings, scene => Play = scene, Tools, OpenTranscode);
        Scene = editor;
        Title = $"{project.Name} — OurTaikoEdit";
    }

    // A chart a website opened in the editor (IChartHost): not one of the person's projects, so not
    // in the list; saving and going back are the host's.
    public void OpenHosted(ChartProject project, IChartHost host)
    {
        if (Scene is EditorViewModel open) open.Close();
        Play = null;
        Scene = new EditorViewModel(project, dialogs, ShowProjects, Settings, scene => Play = scene, Tools, OpenTranscode, host);
        Title = $"{project.Name} — OurTaikoEdit";
    }

    void ShowProjects()
    {
        Scene = NewProjects();
        Title = "OurTaikoEdit";
    }

    ProjectsViewModel NewProjects() => new(registry, dialogs, Open, Settings, () => OpenTranscode());

    // The transcoder over the scene: with files to start from, a format, and who is told of each
    // file converted.
    public void OpenTranscode(IEnumerable<string>? files = null, string? format = null, Action<string>? converted = null)
        => Transcode = new TranscodeViewModel(Tools, dialogs, Settings, () => Transcode = null, files, format, converted);

    // Whether the window may close: the editor asks about unsaved changes.
    public async Task<bool> CanCloseAsync()
    {
        Play?.BackCommand.Execute(null);
        if (Transcode is { IsRunning: true }) return false;
        Transcode?.Close();
        if (Scene is not EditorViewModel editor) return true;
        if (!await editor.ConfirmLeaveAsync()) return false;
        editor.Close();
        return true;
    }
}
