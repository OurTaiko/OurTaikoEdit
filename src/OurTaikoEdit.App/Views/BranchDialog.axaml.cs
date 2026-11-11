using Avalonia.Controls;
using Avalonia.Interactivity;
using OurTaikoEdit.Core.Languages;
using OurTaikoEdit.Core.Localization;

namespace OurTaikoEdit.App.Views;

// A branch's condition: what it is decided by, chosen from the language's kinds with what each
// counts, and the two thresholds each in a box of its own. Closes with the condition as the
// language writes it, or null.
public partial class BranchDialog : Window
{
    readonly IChartLanguage? language;
    readonly IReadOnlyList<BranchConditionKind> kinds = Array.Empty<BranchConditionKind>();

    public BranchDialog() => InitializeComponent();

    // `written` is the condition to start from; one that cannot be read starts from the first
    // kind with no thresholds.
    public BranchDialog(string title, IChartLanguage language, string written) : this()
    {
        this.language = language;
        kinds = language.BranchConditions;
        Title = title;
        Heading.Text = title;
        KindBox.ItemsSource = kinds.Select(k => $"{k.Key} — {k.Name}").ToList();
        var read = language.ReadBranchCondition(written);
        KindBox.SelectedIndex = Math.Max(0, kinds.ToList().FindIndex(k => k.Key == read?.Kind));
        ExpertBox.Text = read?.Expert ?? "";
        MasterBox.Text = read?.Master ?? "";
        BigOnlyBox.IsChecked = read?.BigOnly == true;
        LessBox.IsChecked = read?.Less == true;
        KindBox.SelectionChanged += (_, _) => Refresh();
        ExpertBox.TextChanged += (_, _) => Refresh();
        MasterBox.TextChanged += (_, _) => Refresh();
        BigOnlyBox.IsCheckedChanged += (_, _) => Refresh();
        LessBox.IsCheckedChanged += (_, _) => Refresh();
        Opened += (_, _) => { ExpertBox.Focus(); ExpertBox.SelectAll(); };
        Refresh();
    }

    BranchConditionKind? Kind => KindBox.SelectedIndex >= 0 && KindBox.SelectedIndex < kinds.Count ? kinds[KindBox.SelectedIndex] : null;

    // The condition as the boxes have it now.
    string? Written => language == null || Kind is not { } kind ? null : language.WriteBranchCondition(new BranchConditionText(
        kind.Key, ExpertBox.Text?.Trim() ?? "", MasterBox.Text?.Trim() ?? "", kind.BigOnly != null && BigOnlyBox.IsChecked == true, LessBox.IsChecked == true));

    void Refresh()
    {
        var kind = Kind;
        KindInfo.Text = kind?.Description ?? "";
        BigOnlyBox.IsVisible = kind?.BigOnly != null;
        BigOnlyBox.Content = kind?.BigOnly;
        WrittenText.Text = Written is { } written ? Loc.F("BranchWritten", written) : "";
        ErrorText.IsVisible = false;
    }

    void Accept(object? sender, RoutedEventArgs e)
    {
        // Readable by the language as it would be written: the thresholds are numbers.
        if (Written is not { } written || language?.ReadBranchCondition(written) == null)
        {
            ErrorText.Text = Loc.T("BranchNumberNeeded");
            ErrorText.IsVisible = true;
            return;
        }
        Close(written);
    }

    void Cancel(object? sender, RoutedEventArgs e) => Close(null);
}
