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
        var ok = Button(Loc.T("Ok"), accent: true);
        ok.IsDefault = true;
        ok.Click += (_, _) => { result = box.Text; window.Close(); };
        var cancel = Button(Loc.T("Cancel"), accent: false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => window.Close();
        window.Content = Layout(new Control[] { new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, box }, ok, cancel);
        window.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        await window.ShowDialog(owner);
        return result;
    }

    public Task<string?> BranchConditionAsync(string title, OurTaikoEdit.Core.Languages.IChartLanguage language, string written)
        => new BranchDialog(title, language, written).ShowDialog<string?>(owner);

    public Task<OurTaikoEdit.Core.Languages.NewBlock?> NewBlockAsync(OurTaikoEdit.Core.Languages.IChartLanguage language, IReadOnlyList<OurTaikoEdit.Core.Languages.BlockCourse> courses, IReadOnlyList<OurTaikoEdit.Core.Languages.ChartBlock> blocks, int current)
        => new NewBlockDialog(language, courses, blocks, current).ShowDialog<OurTaikoEdit.Core.Languages.NewBlock?>(owner);

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
        var row = new List<Button>();
        T result = buttons[^1].Value;
        for (int i = 0; i < buttons.Length; i++)
        {
            var (label, value) = buttons[i];
            var button = Button(label, accent: i == 0);
            button.IsDefault = i == 0;
            button.IsCancel = i == buttons.Length - 1;
            button.Click += (_, _) => { result = value; window.Close(); };
            row.Add(button);
        }
        var heading = new TextBlock { Text = title, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        heading.Classes.Add("h1");
        var body = new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        body.Classes.Add("muted");
        window.Content = Layout(new Control[] { heading, body }, row.ToArray());
        await window.ShowDialog(owner);
        return result;
    }

    static Button Button(string label, bool accent)
    {
        var button = new Button { Content = label };
        button.Classes.Add("wide");
        if (accent) button.Classes.Add("accent");
        return button;
    }

    // The dialogs' layout: what is said, over a footer with the buttons at its right.
    static Control Layout(Control[] body, params Button[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        row.Children.AddRange(buttons);
        var footer = new Border { Child = row };
        footer.Classes.Add("footer");
        DockPanel.SetDock(footer, Dock.Bottom);
        var content = new StackPanel { Margin = new Avalonia.Thickness(20, 18, 20, 20), Spacing = 10 };
        content.Children.AddRange(body);
        return new DockPanel { Children = { footer, content } };
    }
}
