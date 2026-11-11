using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using OurTaikoEdit.App.ViewModels;
using OurTaikoEdit.Core.Localization;
using OurTaikoEdit.Core.Settings;

namespace OurTaikoEdit.App.Views;

public sealed class DialogService(Window owner) : IDialogService
{
    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickFileAsync(string title, string filterName, params string[] patterns)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title, AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType(filterName) { Patterns = patterns }, FilePickerFileTypes.All },
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public Task<NewProjectRequest?> NewProjectAsync(string location) => new NewProjectDialog(this, location).ShowDialog<NewProjectRequest?>(owner);

    public Task MessageAsync(string title, string message) => Ask(title, message, (Loc.T("Ok"), true));

    public async Task<bool> ConfirmAsync(string title, string message, string confirm)
        => await Ask(title, message, (confirm, true), (Loc.T("Cancel"), false));

    public async Task<string?> PromptAsync(string title, string message, string value)
    {
        var box = new TextBox { Text = value, MinWidth = 260 };
        var window = new Window
        {
            Title = title, Width = 380, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        string? result = null;
        var ok = new Button { Content = Loc.T("Ok"), IsDefault = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Classes.Add("accent");
        ok.Click += (_, _) => { result = box.Text; window.Close(); };
        var cancel = new Button { Content = Loc.T("Cancel"), IsCancel = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        cancel.Click += (_, _) => window.Close();
        window.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20), Spacing = 12,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                box,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } },
            },
        };
        window.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        await window.ShowDialog(owner);
        return result;
    }

    public Task<SaveChoice> AskSaveAsync(string name)
        => Ask(Loc.F("SaveQuestion", name), Loc.T("SaveLost"),
            (Loc.T("Save"), SaveChoice.Save), (Loc.T("DontSave"), SaveChoice.Discard), (Loc.T("Cancel"), SaveChoice.Cancel));

    public async Task<bool> SettingsAsync(AppSettings settings)
        => await new SettingsDialog(settings).ShowDialog<bool>(owner);

    // A small modal window with a message and a row of buttons; closing it picks the last one.
    async Task<T> Ask<T>(string title, string message, params (string Label, T Value)[] buttons)
    {
        var window = new Window
        {
            Title = title, Width = 420, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        T result = buttons[^1].Value;
        for (int i = 0; i < buttons.Length; i++)
        {
            var (label, value) = buttons[i];
            var button = new Button { Content = label, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = i == 0, IsCancel = i == buttons.Length - 1 };
            if (i == 0) button.Classes.Add("accent");
            button.Click += (_, _) => { result = value; window.Close(); };
            row.Children.Add(button);
        }
        window.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20), Spacing = 16,
            Children =
            {
                new TextBlock { Text = title, FontSize = 16, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                row,
            },
        };
        await window.ShowDialog(owner);
        return result;
    }
}
