using System.ComponentModel;
using System.Xml;
using Avalonia.Controls;
using Avalonia.Input;
using AvaloniaEdit;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using CharTaiko.App.Editor;
using CharTaiko.App.ViewModels;
using CharTaiko.Core.Languages;
using CharTaiko.Core.Localization;

namespace CharTaiko.App.Views;

// Binds the AvaloniaEdit code editor to the editor view model: text and caret go to the model,
// timeline edits come back as one undoable document change, diagnostics are underlined and the
// slot at the playhead is boxed.
public partial class EditorView : UserControl
{
    EditorViewModel? vm;
    readonly DiagnosticRenderer diagnostics = new();
    readonly PlayheadRenderer playhead = new();
    readonly HintRenderer hints;
    readonly FoldingManager folding;
    CompletionWindow? completion;
    InsightWindow? signature;
    bool applying;

    public EditorView()
    {
        InitializeComponent();
        var view = Code.TextArea.TextView;
        hints = new HintRenderer(Code.FontFamily);
        view.BackgroundRenderers.Add(diagnostics);
        view.BackgroundRenderers.Add(playhead);
        view.BackgroundRenderers.Add(hints);
        folding = FoldingManager.Install(Code.TextArea);
        view.PointerHover += (_, e) => ShowHover(e);
        view.PointerHoverStopped += (_, _) => ToolTip.SetIsOpen(view, false);
        Code.TextArea.ContextRequested += (_, e) =>
        {
            // The menu acts where it was asked for: the caret goes there first.
            if (e.TryGetPosition(view, out var p) && view.GetPositionFloor(p + view.ScrollOffset) is { } at)
                Code.TextArea.Caret.Position = at;
            ShowActions(atCaret: false);
            e.Handled = true;
        };
        Code.Options.ConvertTabsToSpaces = false;
        Code.Options.HighlightCurrentLine = true;
        Code.TextArea.TextView.Margin = new Avalonia.Thickness(8, 0, 0, 0);
        Code.TextChanged += (_, _) => vm?.OnTextChanged(Code.Text);
        Code.TextArea.Caret.PositionChanged += (_, _) =>
        {
            if (applying) return;
            vm?.OnCaretMoved(Code.TextArea.Caret.Line, Code.TextArea.Caret.Column);
            if (signature != null) UpdateSignature(open: false);
        };
        Code.TextArea.TextEntered += (_, e) =>
        {
            if (e.Text is { Length: 1 } t && (t[0] == '#' || t[0] == ':' || char.IsLetter(t[0]))) ShowCompletion(automatic: true);
            if (e.Text is { Length: 1 } u && (u[0] == ' ' || u[0] == ',' || signature != null)) UpdateSignature(open: true);
        };
        Code.TextArea.KeyDown += (_, e) =>
        {
            bool command = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
            if (e.Key == Key.Space && command) { ShowCompletion(automatic: false); e.Handled = true; }
            else if (e.Key == Key.OemPeriod && command) { ShowActions(atCaret: true); e.Handled = true; }
            else if (e.Key == Key.Escape && signature != null) { signature.Close(); e.Handled = true; }
        };
        UndoButton.Click += (_, _) => Code.Undo();
        RedoButton.Click += (_, _) => Code.Redo();
        ProblemList.DoubleTapped += (_, _) => GoToProblem();
        ProblemList.SelectionChanged += (_, _) => GoToProblem();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (vm != null)
        {
            vm.PropertyChanged -= Changed;
            vm.EditsRequested -= ApplyEdits;
            vm.CaretRequested -= MoveCaret;
        }
        vm = DataContext as EditorViewModel;
        if (vm == null) return;
        if (vm.Language.OpenHighlighting() is { } stream)
            using (stream)
            using (var reader = XmlReader.Create(stream))
                Code.SyntaxHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        // Loading moves the caret, which must not pick a block: the project's own block stays.
        applying = true;
        Code.Text = vm.InitialText;
        Code.Document.UndoStack.ClearAll();
        Code.Document.UndoStack.MarkAsOriginalFile();
        applying = false;
        vm.IsDirty = false;
        vm.PropertyChanged += Changed;
        vm.EditsRequested += ApplyEdits;
        vm.CaretRequested += MoveCaret;
        ApplySettings();
        if (vm.Analysis.StartLine > 0) MoveCaret(Math.Min(vm.Analysis.StartLine + 1, Code.Document.LineCount), 1);
        Refresh();
    }

    void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorViewModel.Settings)) ApplySettings();
        if (e.PropertyName is nameof(EditorViewModel.Analysis) or nameof(EditorViewModel.Playhead) or nameof(EditorViewModel.Route)) Refresh();
    }

    // Font size, line wrapping and the hints at line ends, from the settings.
    void ApplySettings()
    {
        if (vm == null) return;
        Code.FontSize = vm.Settings.FontSize;
        Code.WordWrap = vm.Settings.WordWrap;
        hints.Visible = vm.Settings.ShowHints;
        Code.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Background);
    }

    void Refresh()
    {
        if (vm == null) return;
        diagnostics.Diagnostics = vm.Analysis.Diagnostics;
        if (hints.Hints != vm.Hints)
        {
            hints.Hints = vm.Hints;
            UpdateFolds();
        }
        var slot = SlotAtPlayhead(vm);
        var span = slot?.Source ?? default;
        bool moved = span != playhead.Span;
        playhead.Span = span;
        Code.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Selection);
        Code.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Background);
        if (moved && vm.IsPlaying && !span.IsEmpty) Code.ScrollToLine(span.Line);
    }

    // The last slot at or before the playhead on the route being shown.
    static ChartSlot? SlotAtPlayhead(EditorViewModel vm)
    {
        if (vm.Analysis.Chart is not { } chart) return null;
        ChartSlot? best = null;
        foreach (var m in vm.Analysis.Measures)
        {
            if (m.Time > vm.Playhead) break;
            if (m.BranchId >= 0 && !Controls.LaneView.Shown(chart, m.BranchId, m.Route, vm.Route)) continue;
            foreach (var s in m.Slots)
                if (s.Time <= vm.Playhead + 1e-6 && (best == null || s.Time >= best.Time)) best = s;
        }
        return best;
    }

    // Timeline edits: one document update, so one undo step, with the caret kept in place.
    void ApplyEdits(IReadOnlyList<TextEdit> edits)
    {
        var doc = Code.Document;
        applying = true;
        doc.BeginUpdate();
        try
        {
            foreach (var edit in edits.OrderByDescending(e => e.Line).ThenByDescending(e => e.Column))
            {
                if (edit.Line < 1 || edit.Line > doc.LineCount) continue;
                // An edit may run across lines (quick fixes, formatting).
                var line = doc.GetLineByNumber(edit.Line);
                int start = line.Offset + Math.Min(edit.Column - 1, line.Length);
                int length = Math.Min(edit.Length, doc.TextLength - start);
                doc.Replace(start, length, edit.NewText);
            }
        }
        finally
        {
            doc.EndUpdate();
            applying = false;
        }
        vm?.OnTextChanged(Code.Text);
    }

    void MoveCaret(int line, int column)
    {
        if (line < 1 || line > Code.Document.LineCount) return;
        Code.TextArea.Caret.Position = new TextViewPosition(line, column);
        Code.ScrollTo(line, column);
        Code.TextArea.Caret.BringCaretToView();
    }

    void GoToProblem()
    {
        if (vm == null || ProblemList.SelectedItem is not DiagnosticItem item) return;
        vm.GoTo(item);
        Code.TextArea.Focus();
    }

    void UpdateFolds()
    {
        if (vm == null) return;
        var doc = Code.Document;
        var folds = vm.Folds
            .Where(f => f.StartLine >= 1 && f.EndLine > f.StartLine && f.EndLine <= doc.LineCount)
            .Select(f => new NewFolding(doc.GetLineByNumber(f.StartLine).EndOffset, doc.GetLineByNumber(f.EndLine).EndOffset) { Name = " … " })
            .OrderBy(f => f.StartOffset)
            .ToList();
        folding.UpdateFoldings(folds, -1);
    }

    // The diagnostics at the pointer and the language's hover there, in a tooltip.
    void ShowHover(PointerEventArgs e)
    {
        var view = Code.TextArea.TextView;
        if (vm == null || view.GetPositionFloor(e.GetPosition(view) + view.ScrollOffset) is not { } at) return;
        var parts = new List<string>();
        foreach (var d in vm.Analysis.Diagnostics)
        {
            var s = d.Source;
            if (s.Line == at.Line && (s.Length == 0 || at.Column >= s.Column && at.Column <= s.Column + Math.Max(1, s.Length)))
                parts.Add((d.Severity == DiagnosticSeverity.Error ? "● " : d.Severity == DiagnosticSeverity.Warning ? "▲ " : "ℹ ") + d.Message);
        }
        if (vm.Language.Hover(Code.Text, at.Line, at.Column) is { } hover) parts.Add(Plain(hover.Markdown));
        if (parts.Count == 0) return;
        ToolTip.SetTip(view, new TextBlock { Text = string.Join("\n\n", parts), TextWrapping = TextWrapping.Wrap, MaxWidth = 520 });
        ToolTip.SetIsOpen(view, true);
        e.Handled = true;
    }

    // Markdown as plain text: no emphasis marks, code ticks or heading marks.
    static string Plain(string markdown)
        => string.Join("\n", markdown.Replace("**", "").Replace("`", "").Split('\n').Select(l => l.TrimStart('#', ' ').TrimEnd()));

    // A command's parameters while its arguments are written, the one being written in bold.
    void UpdateSignature(bool open)
    {
        if (vm == null) return;
        var caret = Code.TextArea.Caret;
        var sig = vm.Language.Signature(Code.Text, caret.Line, caret.Column);
        if (sig == null) { signature?.Close(); return; }
        if (signature == null)
        {
            if (!open) return;
            signature = new InsightWindow(Code.TextArea) { CloseAutomatically = false };
            signature.Closed += (_, _) => signature = null;
            signature.Show();
        }
        var text = new TextBlock { FontFamily = Code.FontFamily, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 };
        int at = 0;
        for (int i = 0; i < sig.Parameters.Count; i++)
        {
            var (start, length) = sig.Parameters[i];
            if (start < at || start + length > sig.Label.Length) continue;
            text.Inlines!.Add(new Run(sig.Label.Substring(at, start - at)));
            var run = new Run(sig.Label.Substring(start, length));
            if (i == sig.Active) { run.FontWeight = FontWeight.Bold; run.Foreground = new SolidColorBrush(Color.Parse("#FF8A65")); }
            text.Inlines.Add(run);
            at = start + length;
        }
        text.Inlines!.Add(new Run(sig.Label.Substring(at)));
        if (!string.IsNullOrEmpty(sig.Summary)) text.Inlines.Add(new Run("\n" + sig.Summary) { Foreground = Brushes.Gray });
        // (InsightWindow is a Popup here: its content is its Child.)
        signature.Child = new Border
        {
            Child = text, Padding = new Avalonia.Thickness(8, 5), CornerRadius = new Avalonia.CornerRadius(4),
            Background = new SolidColorBrush(Color.Parse("#2A2C31")), BorderBrush = new SolidColorBrush(Color.Parse("#44474E")), BorderThickness = new Avalonia.Thickness(1),
        };
    }

    // Quick fixes and refactorings where the caret is, then formatting; from the context menu
    // (with the clipboard) or ⌘/Ctrl+. at the caret.
    void ShowActions(bool atCaret)
    {
        if (vm == null) return;
        var caret = Code.TextArea.Caret;
        var actions = vm.ActionsAt(caret.Line, caret.Column);
        var items = new List<object>();
        if (!atCaret)
        {
            items.Add(Item(Loc.T("Cut"), () => Code.Cut()));
            items.Add(Item(Loc.T("Copy"), () => Code.Copy()));
            items.Add(Item(Loc.T("Paste"), () => Code.Paste()));
            items.Add(new Separator());
        }
        foreach (var action in actions)
        {
            var a = action;
            items.Add(Item(a.IsQuickFix ? Loc.F("FixPrefix", a.Title) : a.Title, () => vm.Run(a)));
        }
        if (actions.Count == 0) items.Add(new MenuItem { Header = Loc.T("NoActions"), IsEnabled = false });
        items.Add(new Separator());
        items.Add(Item(Loc.T("FormatDocument"), () => vm.FormatDocumentCommand.Execute(null)));
        var menu = new ContextMenu { ItemsSource = items };
        if (atCaret)
        {
            var view = Code.TextArea.TextView;
            var rect = caret.CalculateCaretRectangle();
            menu.Placement = PlacementMode.BottomEdgeAlignedLeft;
            menu.PlacementRect = new Avalonia.Rect(rect.Position - view.ScrollOffset, rect.Size);
            menu.Open(view);
        }
        else menu.Open(Code.TextArea);

        static MenuItem Item(string header, Action run)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => run();
            return item;
        }
    }

    void ShowCompletion(bool automatic)
    {
        if (vm == null || completion != null) return;
        var caret = Code.TextArea.Caret;
        var result = vm.Language.Complete(Code.Text, caret.Line, caret.Column);
        if (result.Items.Count == 0) return;
        // Typing a letter in a note line or a header value should not pop anything up.
        if (automatic && result.Items.Count > 40) return;
        var line = Code.Document.GetLineByNumber(caret.Line);
        completion = new CompletionWindow(Code.TextArea)
        {
            StartOffset = line.Offset + Math.Clamp(result.ReplaceColumn - 1, 0, line.Length),
            EndOffset = Code.TextArea.Caret.Offset,
        };
        foreach (var item in result.Items) completion.CompletionList.CompletionData.Add(new CompletionData(item));
        completion.Closed += (_, _) => completion = null;
        completion.Show();
    }
}
