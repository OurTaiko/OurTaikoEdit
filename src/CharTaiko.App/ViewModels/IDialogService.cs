namespace CharTaiko.App.ViewModels;

public enum SaveChoice { Save, Discard, Cancel }

public sealed record NewProjectRequest(string Name, string Location, string ChartType, string? Audio);

public interface IDialogService
{
    Task<string?> PickFolderAsync(string title);
    Task<string?> PickFileAsync(string title, string filterName, params string[] patterns);
    Task<NewProjectRequest?> NewProjectAsync();
    Task MessageAsync(string title, string message);
    Task<bool> ConfirmAsync(string title, string message, string confirm);
    Task<SaveChoice> AskSaveAsync(string name);
}
