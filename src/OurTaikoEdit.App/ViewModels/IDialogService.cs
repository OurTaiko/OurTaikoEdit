namespace OurTaikoEdit.App.ViewModels;

public enum SaveChoice { Save, Discard, Cancel }

public sealed record NewProjectRequest(string Name, string Location, string ChartType, string? Audio);

public interface IDialogService
{
    Task<string?> PickFolderAsync(string title);
    Task<string?> PickFileAsync(string title, string filterName, params string[] patterns);
    Task<NewProjectRequest?> NewProjectAsync(string location);
    Task MessageAsync(string title, string message);
    Task<bool> ConfirmAsync(string title, string message, string confirm);
    // A line of text, or null when cancelled.
    Task<string?> PromptAsync(string title, string message, string value);
    Task<SaveChoice> AskSaveAsync(string name);
    // Shows the settings; true when they were changed and saved.
    Task<bool> SettingsAsync(OurTaikoEdit.Core.Settings.AppSettings settings);
}
