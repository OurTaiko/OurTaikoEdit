using System.Collections.ObjectModel;
using OurTaikoEdit.Core.Languages;
using OurTaikoEdit.Core.Localization;
using OurTaikoEdit.Core.Projects;
using OurTaikoEdit.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OurTaikoEdit.App.ViewModels;

// The Projects scene: every project the app tracks, newest first.
public sealed partial class ProjectsViewModel : ViewModelBase
{
    readonly ProjectRegistry registry;
    readonly IDialogService dialogs;
    readonly Action<ChartProject> open;
    readonly AppSettings settings;

    public ObservableCollection<ProjectItemViewModel> Projects { get; } = new();
    [ObservableProperty] ProjectItemViewModel? selected;
    [ObservableProperty] bool isEmpty;

    public ProjectsViewModel(ProjectRegistry registry, IDialogService dialogs, Action<ChartProject> open, AppSettings? settings = null)
    {
        this.settings = settings ?? new AppSettings();
        this.registry = registry;
        this.dialogs = dialogs;
        this.open = open;
        Refresh();
    }

    public void Refresh()
    {
        Projects.Clear();
        foreach (var tracked in registry.Projects) Projects.Add(ProjectItemViewModel.Load(tracked));
        IsEmpty = Projects.Count == 0;
    }

    [RelayCommand]
    async Task NewProject()
    {
        var request = await dialogs.NewProjectAsync(settings.ProjectsFolderOrDefault);
        if (request == null) return;
        try
        {
            var language = ChartLanguages.Find(request.ChartType) ?? throw new InvalidOperationException(Loc.F("UnknownChartType", request.ChartType));
            var project = registry.CreateProject(request.Location, request.Name, language, request.Audio, request.ChartText);
            if (request.ImportReport != null) await dialogs.MessageAsync(Loc.T("ImportedTitle"), request.ImportReport);
            open(project);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await dialogs.MessageAsync(Loc.T("CouldNotCreate"), e.Message);
        }
    }

    [RelayCommand]
    async Task OpenFolder()
    {
        string? folder = await dialogs.PickFolderAsync(Loc.T("OpenProjectFolder"));
        if (folder == null) return;
        try { open(registry.OpenFolder(folder)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            await dialogs.MessageAsync(Loc.T("CouldNotOpenFolder"), e.Message);
        }
    }

    [RelayCommand]
    async Task Open(ProjectItemViewModel? item)
    {
        item ??= Selected;
        if (item == null) return;
        if (item.Project == null)
        {
            await dialogs.MessageAsync(Loc.T("CouldNotOpenProject"), item.Problem ?? Loc.T("ProjectUnreadable"));
            return;
        }
        open(item.Project);
    }

    [RelayCommand]
    async Task Settings() => await dialogs.SettingsAsync(settings);

    [RelayCommand]
    async Task Remove(ProjectItemViewModel? item)
    {
        item ??= Selected;
        if (item == null) return;
        if (!await dialogs.ConfirmAsync(Loc.T("RemoveTitle"), Loc.F("RemoveMessage", item.Name), Loc.T("Remove"))) return;
        registry.Untrack(item.Folder);
        Refresh();
    }
}

public sealed class ProjectItemViewModel
{
    public required string Folder { get; init; }
    public required string Name { get; init; }
    public ChartProject? Project { get; init; }
    public string ChartType { get; init; } = "?";
    public string Summary { get; init; } = "";
    public string LastOpened { get; init; } = "";
    public string? Problem { get; init; }
    public bool HasProblem => Problem != null;
    // What the card shows beside the name: the chart's title where it says more than the name
    // (or what is wrong with the chart file), and its difficulties.
    public string Detail { get; init; } = "";
    public bool HasDetail => Detail.Length > 0;
    public IReadOnlyList<ChartBlock> Blocks { get; init; } = Array.Empty<ChartBlock>();
    // The folder with the home folder as ~.
    public string FolderShort
    {
        get
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return home.Length > 1 && Folder.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? "~" + Folder[home.Length..] : Folder;
        }
    }

    public static ProjectItemViewModel Load(TrackedProject tracked)
    {
        string opened = tracked.LastOpened is { } t ? Loc.F("OpenedAt", t.LocalDateTime.ToString("yyyy-MM-dd HH:mm")) : Loc.T("NeverOpened");
        string fallbackName = Path.GetFileName(Path.TrimEndingDirectorySeparator(tracked.Folder));
        try
        {
            if (!Directory.Exists(tracked.Folder))
                return new() { Folder = tracked.Folder, Name = fallbackName, LastOpened = opened, Problem = Loc.T("FolderMissing") };
            var project = ChartProject.Load(tracked.Folder);
            var language = ChartLanguages.Find(project.ChartType);
            string summary, detail;
            IReadOnlyList<ChartBlock> difficulties = Array.Empty<ChartBlock>();
            if (language == null) summary = detail = Loc.F("UnknownChartType", project.ChartType);
            else if (!File.Exists(project.ChartPath)) summary = detail = Loc.F("FileMissing", project.Chart);
            else
            {
                var analysis = language.Analyze(ChartFile.Read(project.ChartPath).Text, -1);
                string blocks = analysis.Blocks.Count == 0 ? Loc.T("NoBlocksYet") : string.Join(" · ", analysis.Blocks.Select(b => b.Name));
                summary = $"{analysis.Title} — {blocks}";
                difficulties = analysis.Blocks;
                detail = analysis.Blocks.Count == 0 ? Loc.T("NoBlocksYet") : analysis.Title == project.Name ? "" : analysis.Title;
            }
            return new()
            {
                Folder = tracked.Folder, Name = project.Name, Project = project, LastOpened = opened, Summary = summary,
                Detail = detail, Blocks = difficulties,
                ChartType = language?.DisplayName ?? project.ChartType.ToUpperInvariant(),
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            return new() { Folder = tracked.Folder, Name = fallbackName, LastOpened = opened, Problem = e.Message };
        }
    }
}
