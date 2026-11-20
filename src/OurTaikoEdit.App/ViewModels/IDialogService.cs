namespace OurTaikoEdit.App.ViewModels;

public enum SaveChoice { Save, Discard, Cancel }

// ChartText is the chart when it was imported from another file rather than started from the
// chart type's template, and ImportReport what the importer has to say about it.
public sealed record NewProjectRequest(string Name, string Location, string ChartType, string? Audio, string? ChartText = null, string? ImportReport = null);

// What the editor knows when a video is asked for: where it may go, the playhead, where a play
// from the beginning starts, and whether hit sounds are on.
public sealed record VideoExportRequest(string SuggestedOutput, double Playhead, double Beginning, bool HitSounds);

public interface IDialogService
{
    Task<string?> PickFolderAsync(string title);
    Task<string?> PickFileAsync(string title, string filterName, params string[] patterns);
    Task<NewProjectRequest?> NewProjectAsync(string location);
    Task MessageAsync(string title, string message);
    Task<bool> ConfirmAsync(string title, string message, string confirm);
    // A line of text, or null when cancelled.
    Task<string?> PromptAsync(string title, string message, string value);
    // A branch's condition as the language writes it, starting from one as written (empty for a
    // new branch); null when cancelled.
    Task<string?> BranchConditionAsync(string title, OurTaikoEdit.Core.Languages.IChartLanguage language, string written);
    // A new difficulty of those the language offers, with the block it starts with the shape of
    // chosen among the chart's (current is the one being edited); null when cancelled.
    Task<OurTaikoEdit.Core.Languages.NewBlock?> NewBlockAsync(OurTaikoEdit.Core.Languages.IChartLanguage language, IReadOnlyList<OurTaikoEdit.Core.Languages.BlockCourse> courses, IReadOnlyList<OurTaikoEdit.Core.Languages.ChartBlock> blocks, int current);
    Task<SaveChoice> AskSaveAsync(string name);
    // The preview video's settings (starting from the last ones), or null when cancelled.
    Task<OurTaikoEdit.App.Export.VideoExportOptions?> ExportVideoAsync(OurTaikoEdit.Core.Settings.AppSettings settings, VideoExportRequest request);
    // Long work with its progress shown and a way to stop it: true when it finished, false when it
    // was stopped (its other exceptions are passed on).
    // `waiting` is said until the first progress, `describe` says each one.
    Task<bool> ProgressAsync(string title, string waiting, Func<int, int, string> describe, Func<IProgress<(int Done, int Total)>, CancellationToken, Task> work);
    // Shows the settings; true when they were changed and saved.
    Task<bool> SettingsAsync(OurTaikoEdit.Core.Settings.AppSettings settings);
}
