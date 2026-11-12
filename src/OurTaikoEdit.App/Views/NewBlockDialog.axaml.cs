using Avalonia.Controls;
using Avalonia.Interactivity;
using OurTaikoEdit.Core.Languages;
using OurTaikoEdit.Core.Localization;

namespace OurTaikoEdit.App.Views;

// A new difficulty: which of those the chart has not, its level, and the difficulty whose shape
// it starts with (none: four empty measures) with what of that is carried over: tempo and
// measures, branches, go-go time. Closes with the NewBlock, or null.
public partial class NewBlockDialog : Window
{
    readonly IReadOnlyList<BlockCourse> courses = Array.Empty<BlockCourse>();
    readonly IReadOnlyList<ChartBlock> blocks = Array.Empty<ChartBlock>();
    // The level is the difficulty's own until it is changed by hand.
    bool levelTouched, settingLevel;

    public NewBlockDialog() => InitializeComponent();

    // `current` is the block being edited: the one offered to start from.
    public NewBlockDialog(IReadOnlyList<BlockCourse> courses, IReadOnlyList<ChartBlock> blocks, int current) : this()
    {
        this.courses = courses;
        this.blocks = blocks;
        CourseBox.ItemsSource = courses.Select(c => c.Name).ToList();
        CourseBox.SelectedIndex = 0;
        FromBox.ItemsSource = blocks.Select(b => b.Name).Prepend(Loc.T("InheritNothing")).ToList();
        FromBox.SelectedIndex = current >= 0 && current < blocks.Count ? current + 1 : 0;
        TempoBox.IsChecked = GogoBox.IsChecked = BranchBox.IsChecked = true;
        CourseBox.SelectionChanged += (_, _) => ShowLevel();
        LevelBox.ValueChanged += (_, _) => { if (!settingLevel) levelTouched = true; };
        FromBox.SelectionChanged += (_, _) => ShowFrom();
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

    void ShowFrom()
    {
        var from = From;
        InheritPanel.IsEnabled = from != null;
        // Branches can only come from a difficulty that has them.
        BranchBox.IsEnabled = from?.IsBranching == true;
        InheritHint.Text = from == null ? Loc.T("InheritNothingHint") : Loc.F("InheritHint", from.Name);
    }

    void Accept(object? sender, RoutedEventArgs e)
    {
        if (CourseBox.SelectedIndex < 0 || CourseBox.SelectedIndex >= courses.Count) { Close(null); return; }
        var from = From;
        Close(new NewBlock(courses[CourseBox.SelectedIndex].Id, (int)(LevelBox.Value ?? 1), from?.Index ?? -1,
            Tempo: from != null && TempoBox.IsChecked == true,
            Branches: from?.IsBranching == true && BranchBox.IsChecked == true,
            Gogo: from != null && GogoBox.IsChecked == true));
    }

    void Cancel(object? sender, RoutedEventArgs e) => Close(null);
}
