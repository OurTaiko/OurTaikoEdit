using Avalonia.Controls;
using Avalonia.Interactivity;
using OurTaikoEdit.App.ViewModels;
using OurTaikoEdit.Core.Importing;
using OurTaikoEdit.Core.Languages;
using OurTaikoEdit.Core.Localization;

namespace OurTaikoEdit.App.Views;

// A new project: its name and place, what its chart starts from (the chart type's template, or
// a file one of the mods' importers reads), the chart type and the audio. A chart that is
// imported is read here, when Create is pressed, so that a file that cannot be read is said in
// the dialog while it can still be changed.
public partial class NewProjectDialog : Window
{
    readonly IDialogService? dialogs;
    readonly List<IChartLanguage> languages = ChartLanguages.All.ToList();
    readonly List<IChartImporter> importers = ChartImporters.All.ToList();
    // The chart types in the list: those the chosen importer can write.
    List<IChartLanguage> offered = new();
    // The file Create last tried to read.
    string? triedSource;

    public NewProjectDialog() => InitializeComponent();

    public NewProjectDialog(IDialogService dialogs, string location) : this()
    {
        this.dialogs = dialogs;
        LocationBox.Text = location;
        // Only when a mod has something to start from is there a choice to show.
        SourcePanel.IsVisible = importers.Count > 0;
        SourceBox.ItemsSource = importers.Select(i => i.DisplayName).Prepend(Loc.T("EmptyChart")).ToList();
        SourceBox.SelectedIndex = 0;
        SourceBox.SelectionChanged += (_, _) => ShowSource();
        ShowSource();
        NameBox.TextChanged += (_, _) => ShowFolder();
        LocationBox.TextChanged += (_, _) => ShowFolder();
        Opened += (_, _) => NameBox.Focus();
    }

    IChartImporter? Importer => SourceBox.SelectedIndex > 0 && SourceBox.SelectedIndex <= importers.Count ? importers[SourceBox.SelectedIndex - 1] : null;

    void ShowSource()
    {
        var importer = Importer;
        SourceFilePanel.IsVisible = importer != null;
        if (importer != null) SourceFileLabel.Text = $"{importer.DisplayName} ({string.Join(", ", importer.Patterns)})";
        var chosen = TypeBox.SelectedIndex >= 0 && TypeBox.SelectedIndex < offered.Count ? offered[TypeBox.SelectedIndex] : null;
        offered = importer == null ? languages : languages.Where(importer.CanWrite).ToList();
        TypeBox.ItemsSource = offered.Select(l => l.DisplayName).ToList();
        TypeBox.SelectedIndex = Math.Max(0, chosen == null ? 0 : offered.IndexOf(chosen));
        ShowError(null);
    }

    void ShowFolder()
    {
        FolderText.Text = string.IsNullOrWhiteSpace(NameBox.Text) ? "" : Loc.F("Creates", Path.Combine(LocationBox.Text ?? "", NameBox.Text.Trim()));
        FolderText.IsVisible = FolderText.Text.Length > 0;
    }

    void ShowError(string? error)
    {
        ErrorText.Text = error;
        ErrorText.IsVisible = error != null;
    }

    async void BrowseLocation(object? sender, RoutedEventArgs e)
    {
        if (dialogs != null && await dialogs.PickFolderAsync(Loc.T("WhereToCreate")) is { } folder) LocationBox.Text = folder;
    }

    async void BrowseAudio(object? sender, RoutedEventArgs e)
    {
        if (dialogs != null && await dialogs.PickFileAsync(Loc.T("ChooseAudio"), Loc.T("AudioFilter"), "*.ogg", "*.mp3", "*.wav", "*.flac", "*.opus") is { } audio)
            AudioBox.Text = audio;
    }

    async void BrowseSource(object? sender, RoutedEventArgs e)
    {
        if (dialogs == null || Importer is not { } importer) return;
        if (await dialogs.PickFileAsync(Loc.T("ChooseSource"), importer.DisplayName, importer.Patterns.ToArray()) is { } file) SourceFileBox.Text = file;
    }

    // The file's name is the song's as often as not: it names a project that has no name yet.
    void SourceFileChanged(object? sender, TextChangedEventArgs e)
    {
        string file = SourceFileBox.Text?.Trim() ?? "";
        // What was said about the file last tried is not about another one. (The event comes after
        // the text has changed, possibly after Create has already tried this very text.)
        if (file != triedSource) ShowError(null);
        if (string.IsNullOrWhiteSpace(NameBox.Text) && file.Length > 0 && File.Exists(file)) NameBox.Text = Path.GetFileNameWithoutExtension(file);
    }

    void Create(object? sender, RoutedEventArgs e)
    {
        string name = NameBox.Text?.Trim() ?? "", location = LocationBox.Text?.Trim() ?? "", audio = AudioBox.Text?.Trim() ?? "";
        string source = SourceFileBox.Text?.Trim() ?? "";
        var importer = Importer;
        triedSource = source;
        var language = offered.Count > 0 ? offered[Math.Clamp(TypeBox.SelectedIndex, 0, offered.Count - 1)] : null;
        string? error = name.Length == 0 ? Loc.T("NeedName")
            : location.Length == 0 ? Loc.T("NeedLocation")
            : importer != null && source.Length == 0 ? Loc.T("NeedSource")
            : importer != null && !File.Exists(source) ? Loc.T("SourceMissing")
            : language == null ? Loc.F("UnknownChartType", "")
            : audio.Length > 0 && !File.Exists(audio) ? Loc.T("AudioMissing")
            : null;
        ImportedChart? imported = null;
        if (error == null && importer != null)
        {
            try { imported = importer.Import(new ChartImportRequest(source, language!, name, audio.Length > 0 ? Path.GetFileName(audio) : null)); }
            catch (ChartImportException failed) { error = failed.Message; }
        }
        ShowError(error);
        if (error != null) return;
        Close(new NewProjectRequest(name, location, language!.Id, audio.Length > 0 ? audio : null, imported?.Text, imported?.Report));
    }

    void Cancel(object? sender, RoutedEventArgs e) => Close(null);
}
