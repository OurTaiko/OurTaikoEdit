using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using CharTaiko.App.ViewModels;

namespace CharTaiko.App.Views;

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

    public Task<NewProjectRequest?> NewProjectAsync() => new NewProjectDialog(this).ShowDialog<NewProjectRequest?>(owner);

    public Task MessageAsync(string title, string message) => Ask(title, message, ("OK", true));

    public async Task<bool> ConfirmAsync(string title, string message, string confirm)
        => await Ask(title, message, (confirm, true), ("Cancel", false));

    public Task<SaveChoice> AskSaveAsync(string name)
        => Ask($"Save {name}?", "Your changes are lost if you don't save them.",
            ("Save", SaveChoice.Save), ("Don't Save", SaveChoice.Discard), ("Cancel", SaveChoice.Cancel));

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
