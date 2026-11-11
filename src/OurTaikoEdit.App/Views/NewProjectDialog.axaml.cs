using Avalonia.Controls;
using Avalonia.Interactivity;
using OurTaikoEdit.App.ViewModels;
using OurTaikoEdit.Core.Languages;
using OurTaikoEdit.Core.Localization;

namespace OurTaikoEdit.App.Views;

public partial class NewProjectDialog : Window
{
    readonly IDialogService? dialogs;
    readonly List<IChartLanguage> languages = ChartLanguages.All.ToList();

    public NewProjectDialog() => InitializeComponent();

    public NewProjectDialog(IDialogService dialogs, string location) : this()
    {
        this.dialogs = dialogs;
        LocationBox.Text = location;
        TypeBox.ItemsSource = languages.Select(l => l.DisplayName).ToList();
        TypeBox.SelectedIndex = 0;
        NameBox.TextChanged += (_, _) => ShowFolder();
        LocationBox.TextChanged += (_, _) => ShowFolder();
        Opened += (_, _) => NameBox.Focus();
    }

    void ShowFolder()
        => FolderText.Text = string.IsNullOrWhiteSpace(NameBox.Text) ? "" : Loc.F("Creates", Path.Combine(LocationBox.Text ?? "", NameBox.Text.Trim()));

    async void BrowseLocation(object? sender, RoutedEventArgs e)
    {
        if (dialogs != null && await dialogs.PickFolderAsync(Loc.T("WhereToCreate")) is { } folder) LocationBox.Text = folder;
    }

    async void BrowseAudio(object? sender, RoutedEventArgs e)
    {
        if (dialogs != null && await dialogs.PickFileAsync(Loc.T("ChooseAudio"), Loc.T("AudioFilter"), "*.ogg", "*.mp3", "*.wav", "*.flac", "*.opus") is { } audio)
            AudioBox.Text = audio;
    }

    void Create(object? sender, RoutedEventArgs e)
    {
        string name = NameBox.Text?.Trim() ?? "", location = LocationBox.Text?.Trim() ?? "", audio = AudioBox.Text?.Trim() ?? "";
        string? error = name.Length == 0 ? Loc.T("NeedName")
            : location.Length == 0 ? Loc.T("NeedLocation")
            : audio.Length > 0 && !File.Exists(audio) ? Loc.T("AudioMissing")
            : null;
        ErrorText.Text = error;
        ErrorText.IsVisible = error != null;
        if (error != null) return;
        Close(new NewProjectRequest(name, location, languages[Math.Max(0, TypeBox.SelectedIndex)].Id, audio.Length > 0 ? audio : null));
    }

    void Cancel(object? sender, RoutedEventArgs e) => Close(null);
}
