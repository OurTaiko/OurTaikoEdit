using Avalonia.Controls;
using Avalonia.Interactivity;
using OurTaikoEdit.Core.Importing;
using OurTaikoEdit.Core.Languages;
using OurTaikoEdit.Core.Localization;

namespace OurTaikoEdit.App.Views;

// A new difficulty can start empty, inherit an existing block's shape, or import a source file.
public partial class NewBlockDialog : Window
{
    readonly IReadOnlyList<BlockCourse> courses = Array.Empty<BlockCourse>();
    readonly IReadOnlyList<ChartBlock> blocks = Array.Empty<ChartBlock>();
    readonly IChartLanguage? language;
    readonly IReadOnlyList<IChartBlockImporter> importers = Array.Empty<IChartBlockImporter>();
    // The level is the difficulty's own until it is changed by hand.
    bool levelTouched, settingLevel;
    string? triedSource;

    public NewBlockDialog() => InitializeComponent();

    // `current` is the block being edited: the one offered to start from.
    public NewBlockDialog(IChartLanguage language, IReadOnlyList<BlockCourse> courses, IReadOnlyList<ChartBlock> blocks, int current) : this()
    {
        this.language = language;
        importers = ChartImporters.All.OfType<IChartBlockImporter>().Where(i => i.CanWrite(language)).ToList();
        this.courses = courses;
        this.blocks = blocks;
        CourseBox.ItemsSource = courses.Select(c => c.Name).ToList();
        CourseBox.SelectedIndex = 0;
        FromBox.ItemsSource = blocks.Select(b => b.Name).Prepend(Loc.T("InheritNothing"))
            .Concat(importers.Select(i => i.DisplayName)).ToList();
        FromBox.SelectedIndex = current >= 0 && current < blocks.Count ? current + 1 : 0;
        TempoBox.IsChecked = GogoBox.IsChecked = BranchBox.IsChecked = true;
        CourseBox.SelectionChanged += (_, _) => ShowLevel();
        LevelBox.ValueChanged += (_, _) => { if (!settingLevel) levelTouched = true; };
        FromBox.SelectionChanged += (_, _) => ShowFrom();
        SourceFileBox.TextChanged += (_, _) =>
        {
            if (SourcePath != triedSource) ShowError(null);
        };
        ShowLevel();
        ShowFrom();
    }

    void ShowLevel()
    {
        if (levelTouched || CourseBox.SelectedIndex < 0 || CourseBox.SelectedIndex >= courses.Count) return;
        settingLevel = true;
        LevelBox.Value = courses[CourseBox.SelectedIndex].Level;
        settingLevel = false;
    }

    ChartBlock? From => FromBox.SelectedIndex > 0 && FromBox.SelectedIndex <= blocks.Count ? blocks[FromBox.SelectedIndex - 1] : null;

    IChartBlockImporter? Importer
    {
        get
        {
            int index = FromBox.SelectedIndex - blocks.Count - 1;
            return index >= 0 && index < importers.Count ? importers[index] : null;
        }
    }

    void ShowFrom()
    {
        var from = From;
        bool importing = Importer != null;
        SourceFilePanel.IsVisible = importing;
        InheritPanel.IsVisible = InheritHint.IsVisible = !importing;
        ShowError(null);
        InheritPanel.IsEnabled = from != null;
        // Branches can only come from a difficulty that has them.
        BranchBox.IsEnabled = from?.IsBranching == true;
        InheritHint.Text = from == null ? Loc.T("InheritNothingHint") : Loc.F("InheritHint", from.Name);
    }

    async void BrowseSource(object? sender, RoutedEventArgs e)
    {
        if (Importer is not { } importer) return;
        var dialogs = new DialogService(this);
        if (await dialogs.PickFileAsync(Loc.T("ChooseSource"), importer.DisplayName, importer.Patterns.ToArray()) is { } file)
            SourceFileBox.Text = file;
    }

    string SourcePath => SourceFileBox.Text?.Trim() ?? "";

    void ShowError(string? error)
    {
        ImportError.Text = error;
        ImportError.IsVisible = error != null;
    }

    void Accept(object? sender, RoutedEventArgs e)
    {
        if (CourseBox.SelectedIndex < 0 || CourseBox.SelectedIndex >= courses.Count) { Close(null); return; }
        ImportedBlock? imported = null;
        if (Importer is { } importer)
        {
            string source = triedSource = SourcePath;
            if (source.Length == 0) { ShowError(Loc.T("NeedSource")); return; }
            if (!File.Exists(source)) { ShowError(Loc.T("SourceMissing")); return; }
            try { imported = importer.ImportBlock(source, language!); }
            catch (ChartImportException failed) { ShowError(failed.Message); return; }
        }

        var from = From;
        Close(new NewBlock(courses[CourseBox.SelectedIndex].Id, (int)(LevelBox.Value ?? 1), from?.Index ?? -1,
            Tempo: from != null && TempoBox.IsChecked == true,
            Branches: from?.IsBranching == true && BranchBox.IsChecked == true,
            Gogo: from != null && GogoBox.IsChecked == true,
            Imported: imported));
    }

    void Cancel(object? sender, RoutedEventArgs e) => Close(null);
}
