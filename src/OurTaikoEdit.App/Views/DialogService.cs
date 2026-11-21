using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using OurTaikoEdit.App.ViewModels;
using OurTaikoEdit.Core.Localization;
using OurTaikoEdit.Core.Settings;

namespace OurTaikoEdit.App.Views;

// How a file or folder the person picked becomes a path the app reads and writes: on the desktop
// the picked one itself; in a browser a copy in the app's own storage (BrowserFiles).
public interface IPickedFiles
{
    Task<string?> FolderAsync(IStorageFolder folder);
    Task<string?> FileAsync(IStorageFile file);
    // A file the app made (a video, a converted song, a project's zip), handed to the person: nothing
    // to do on the desktop, where it is already where they chose; a browser downloads it.
    Task OfferAsync(string file);
}

public sealed class LocalFiles : IPickedFiles
{
    public Task<string?> FolderAsync(IStorageFolder folder) => Task.FromResult(folder.TryGetLocalPath());
    public Task<string?> FileAsync(IStorageFile file) => Task.FromResult(file.TryGetLocalPath());
    public Task OfferAsync(string file) => Task.CompletedTask;
}

public sealed class DialogService : IDialogService
{
    readonly Func<TopLevel?> top;
    readonly IDialogHost host;
    readonly IPickedFiles files;

    // The desktop: dialogs in windows over the owner.
    public DialogService(Window owner) : this(() => owner, new WindowDialogHost(owner), new LocalFiles()) { }

    public DialogService(Func<TopLevel?> top, IDialogHost host, IPickedFiles files)
    {
        this.top = top;
        this.host = host;
        this.files = files;
    }

    IStorageProvider Storage => top()?.StorageProvider ?? throw new InvalidOperationException("Nothing to show a picker in.");

    Task<T?> Show<T>(Dialog dialog)
    {
        dialog.Dialogs = this;
        return host.ShowAsync<T>(dialog);
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await Storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count > 0 ? await files.FolderAsync(folders[0]) : null;
    }

    public async Task<string?> PickFileAsync(string title, string filterName, params string[] patterns)
    {
        var picked = await Storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title, AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType(filterName) { Patterns = patterns }, FilePickerFileTypes.All },
        });
        return picked.Count > 0 ? await files.FileAsync(picked[0]) : null;
    }

    public async Task<IReadOnlyList<string>> PickFilesAsync(string title)
    {
        var picked = await Storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = true });
        var paths = new List<string>();
        foreach (var file in picked)
            if (await files.FileAsync(file) is { } path) paths.Add(path);
        return paths;
    }

    public Task OfferAsync(string file) => files.OfferAsync(file);

    public Task<NewProjectRequest?> NewProjectAsync(string location) => Show<NewProjectRequest>(new NewProjectDialog(this, location));

    public Task MessageAsync(string title, string message) => Ask(title, message, (Loc.T("Ok"), true));

    public async Task<bool> ConfirmAsync(string title, string message, string confirm)
        => await Ask(title, message, (confirm, true), (Loc.T("Cancel"), false));

    public async Task<string?> PromptAsync(string title, string message, string value)
    {
        var box = new TextBox { Text = value, MinWidth = 260 };
        var dialog = new Dialog { Title = title, Width = 380 };
        var ok = Button(Loc.T("Ok"), accent: true);
        ok.IsDefault = true;
        ok.Click += (_, _) => dialog.Close(box.Text);
        var cancel = Button(Loc.T("Cancel"), accent: false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => dialog.Close();
        dialog.Content = Layout(new Control[] { new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, box }, ok, cancel);
        dialog.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        return await Show<string>(dialog);
    }

    public Task<string?> BranchConditionAsync(string title, OurTaikoEdit.Core.Languages.IChartLanguage language, string written)
        => Show<string>(new BranchDialog(title, language, written));

    public Task<OurTaikoEdit.Core.Languages.NewBlock?> NewBlockAsync(OurTaikoEdit.Core.Languages.IChartLanguage language, IReadOnlyList<OurTaikoEdit.Core.Languages.BlockCourse> courses, IReadOnlyList<OurTaikoEdit.Core.Languages.ChartBlock> blocks, int current)
        => Show<OurTaikoEdit.Core.Languages.NewBlock>(new NewBlockDialog(language, courses, blocks, current));

    public Task<SaveChoice> AskSaveAsync(string name)
        => Ask(Loc.F("SaveQuestion", name), Loc.T("SaveLost"),
            (Loc.T("Save"), SaveChoice.Save), (Loc.T("DontSave"), SaveChoice.Discard), (Loc.T("Cancel"), SaveChoice.Cancel));

    public Task<OurTaikoEdit.App.Export.VideoExportOptions?> ExportVideoAsync(AppSettings settings, VideoExportRequest request)
        => Show<OurTaikoEdit.App.Export.VideoExportOptions>(new ExportVideoDialog(settings, request));

    public async Task<bool> ProgressAsync(string title, string waiting, Func<int, int, string> describe, Func<IProgress<(int Done, int Total)>, CancellationToken, Task> work)
    {
        using var stop = new CancellationTokenSource();
        var heading = new TextBlock { Text = title, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        heading.Classes.Add("h1");
        var said = new TextBlock { Text = waiting };
        said.Classes.Add("muted");
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, IsIndeterminate = true };
        var cancel = Button(Loc.T("Cancel"), accent: false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => stop.Cancel();
        var dialog = new Dialog { Title = title, Width = 420, Content = Layout(new Control[] { heading, said, bar }, cancel) };
        bool running = true;
        // Closing it stops the work; it goes once the work has.
        dialog.AllowClose = () => { if (running) stop.Cancel(); return !running; };
        var progress = new Progress<(int Done, int Total)>(p =>
        {
            bar.IsIndeterminate = false;
            bar.Value = p.Total > 0 ? (double)p.Done / p.Total : 0;
            said.Text = describe(p.Done, p.Total);
        });
        var shown = Show<object>(dialog);
        try
        {
            await work(progress, stop.Token);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        finally
        {
            running = false;
            dialog.Close();
            await shown;
        }
    }

    public async Task<bool> SettingsAsync(AppSettings settings) => await Show<bool>(new SettingsDialog(settings));

    // A small dialog with a message and a row of buttons; closing it otherwise picks the last one.
    async Task<T> Ask<T>(string title, string message, params (string Label, T Value)[] buttons)
    {
        var dialog = new Dialog { Title = title, Width = 420 };
        var row = new List<Button>();
        for (int i = 0; i < buttons.Length; i++)
        {
            var (label, value) = buttons[i];
            var button = Button(label, accent: i == 0);
            button.IsDefault = i == 0;
            button.IsCancel = i == buttons.Length - 1;
            button.Click += (_, _) => dialog.Close(value);
            row.Add(button);
        }
        var heading = new TextBlock { Text = title, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        heading.Classes.Add("h1");
        var body = new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        body.Classes.Add("muted");
        dialog.Content = Layout(new Control[] { heading, body }, row.ToArray());
        return await host.ShowAsync(dialog) is T chosen ? chosen : buttons[^1].Value;
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
