using Avalonia.Controls;
using Avalonia.Interactivity;
using CharTaiko.App.ViewModels;
using CharTaiko.Core.Languages;

namespace CharTaiko.App.Views;

public partial class NewProjectDialog : Window
{
    readonly IDialogService? dialogs;
    readonly List<IChartLanguage> languages = ChartLanguages.All.ToList();

    public NewProjectDialog() => InitializeComponent();

    public NewProjectDialog(IDialogService dialogs) : this()
    {
        this.dialogs = dialogs;
        LocationBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CharTaiko");
        TypeBox.ItemsSource = languages.Select(l => l.DisplayName).ToList();
        TypeBox.SelectedIndex = 0;
        NameBox.TextChanged += (_, _) => ShowFolder();
        LocationBox.TextChanged += (_, _) => ShowFolder();
        Opened += (_, _) => NameBox.Focus();
    }

    void ShowFolder()
        => FolderText.Text = string.IsNullOrWhiteSpace(NameBox.Text) ? "" : "Creates " + Path.Combine(LocationBox.Text ?? "", NameBox.Text.Trim());

    async void BrowseLocation(object? sender, RoutedEventArgs e)
    {
        if (dialogs != null && await dialogs.PickFolderAsync("Where to create the project") is { } folder) LocationBox.Text = folder;
    }

    async void BrowseAudio(object? sender, RoutedEventArgs e)
    {
        if (dialogs != null && await dialogs.PickFileAsync("Choose the song's audio", "Audio", "*.ogg", "*.mp3", "*.wav", "*.flac", "*.opus") is { } audio)
            AudioBox.Text = audio;
    }

    void Create(object? sender, RoutedEventArgs e)
    {
        string name = NameBox.Text?.Trim() ?? "", location = LocationBox.Text?.Trim() ?? "", audio = AudioBox.Text?.Trim() ?? "";
        string? error = name.Length == 0 ? "Give the project a name."
            : location.Length == 0 ? "Choose where to create it."
            : audio.Length > 0 && !File.Exists(audio) ? "The audio file does not exist."
            : null;
        ErrorText.Text = error;
        ErrorText.IsVisible = error != null;
        if (error != null) return;
        Close(new NewProjectRequest(name, location, languages[Math.Max(0, TypeBox.SelectedIndex)].Id, audio.Length > 0 ? audio : null));
    }

    void Cancel(object? sender, RoutedEventArgs e) => Close(null);
}
