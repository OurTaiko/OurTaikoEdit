using System.Collections.ObjectModel;
using CharTaiko.Core.Languages;
using CharTaiko.Core.Projects;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CharTaiko.App.ViewModels;

// The Projects scene: every project the app tracks, newest first.
public sealed partial class ProjectsViewModel : ViewModelBase
{
    readonly ProjectRegistry registry;
    readonly IDialogService dialogs;
    readonly Action<ChartProject> open;

    public ObservableCollection<ProjectItemViewModel> Projects { get; } = new();
    [ObservableProperty] ProjectItemViewModel? selected;
    [ObservableProperty] bool isEmpty;

    public ProjectsViewModel(ProjectRegistry registry, IDialogService dialogs, Action<ChartProject> open)
    {
        this.registry = registry;
        this.dialogs = dialogs;
        this.open = open;
        Refresh();
    }

    void Refresh()
    {
        Projects.Clear();
        foreach (var tracked in registry.Projects) Projects.Add(ProjectItemViewModel.Load(tracked));
        IsEmpty = Projects.Count == 0;
    }

    [RelayCommand]
    async Task NewProject()
    {
        var request = await dialogs.NewProjectAsync();
        if (request == null) return;
        try
        {
            var language = ChartLanguages.Find(request.ChartType) ?? throw new InvalidOperationException("Unknown chart type " + request.ChartType);
            open(registry.CreateProject(request.Location, request.Name, language, request.Audio));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await dialogs.MessageAsync("Could not create the project", e.Message);
        }
    }

    [RelayCommand]
    async Task OpenFolder()
    {
        string? folder = await dialogs.PickFolderAsync("Open a project folder");
        if (folder == null) return;
        try { open(registry.OpenFolder(folder)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            await dialogs.MessageAsync("Could not open the folder", e.Message);
        }
    }

    [RelayCommand]
    async Task Open(ProjectItemViewModel? item)
    {
        item ??= Selected;
        if (item == null) return;
        if (item.Project == null)
        {
            await dialogs.MessageAsync("Could not open the project", item.Problem ?? "The project could not be read.");
            return;
        }
        open(item.Project);
    }

    [RelayCommand]
    async Task Remove(ProjectItemViewModel? item)
    {
        item ??= Selected;
        if (item == null) return;
        if (!await dialogs.ConfirmAsync("Remove from the list", $"Stop tracking “{item.Name}”? The folder and its files stay on disk.", "Remove")) return;
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

    public static ProjectItemViewModel Load(TrackedProject tracked)
    {
        string opened = tracked.LastOpened is { } t ? "Opened " + t.LocalDateTime.ToString("yyyy-MM-dd HH:mm") : "Never opened";
        string fallbackName = Path.GetFileName(Path.TrimEndingDirectorySeparator(tracked.Folder));
        try
        {
            if (!Directory.Exists(tracked.Folder))
                return new() { Folder = tracked.Folder, Name = fallbackName, LastOpened = opened, Problem = "The folder is missing." };
            var project = ChartProject.Load(tracked.Folder);
            var language = ChartLanguages.Find(project.ChartType);
            string summary;
            if (language == null) summary = $"Unknown chart type “{project.ChartType}”";
            else if (!File.Exists(project.ChartPath)) summary = $"{project.Chart} is missing";
            else
            {
                var analysis = language.Analyze(ChartFile.Read(project.ChartPath).Text, -1);
                string blocks = analysis.Blocks.Count == 0 ? "no blocks yet" : string.Join(" · ", analysis.Blocks.Select(b => b.Name));
                summary = $"{analysis.Title} — {blocks}";
            }
            return new()
            {
                Folder = tracked.Folder, Name = project.Name, Project = project, LastOpened = opened, Summary = summary,
                ChartType = language?.DisplayName ?? project.ChartType.ToUpperInvariant(),
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            return new() { Folder = tracked.Folder, Name = fallbackName, LastOpened = opened, Problem = e.Message };
        }
    }
}
