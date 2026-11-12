namespace OurTaikoEdit.App.ViewModels;

public enum SaveChoice { Save, Discard, Cancel }

// ChartText is the chart when it was imported from another file rather than started from the
// chart type's template, and ImportReport what the importer has to say about it.
public sealed record NewProjectRequest(string Name, string Location, string ChartType, string? Audio, string? ChartText = null, string? ImportReport = null);

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
    Task<SaveChoice> AskSaveAsync(string name);
    // Shows the settings; true when they were changed and saved.
    Task<bool> SettingsAsync(OurTaikoEdit.Core.Settings.AppSettings settings);
}
