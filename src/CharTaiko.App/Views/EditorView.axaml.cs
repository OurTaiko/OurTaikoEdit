using System.ComponentModel;
using System.Xml;
using Avalonia.Controls;
using Avalonia.Input;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using CharTaiko.App.Editor;
using CharTaiko.App.ViewModels;
using CharTaiko.Core.Languages;

namespace CharTaiko.App.Views;

// Binds the AvaloniaEdit code editor to the editor view model: text and caret go to the model,
// timeline edits come back as one undoable document change, diagnostics are underlined and the
// slot at the playhead is boxed.
public partial class EditorView : UserControl
{
    EditorViewModel? vm;
    readonly DiagnosticRenderer diagnostics = new();
    readonly PlayheadRenderer playhead = new();
    CompletionWindow? completion;
    bool applying;

    public EditorView()
    {
        InitializeComponent();
        Code.TextArea.TextView.BackgroundRenderers.Add(diagnostics);
        Code.TextArea.TextView.BackgroundRenderers.Add(playhead);
        Code.Options.ConvertTabsToSpaces = false;
        Code.Options.HighlightCurrentLine = true;
        Code.TextArea.TextView.Margin = new Avalonia.Thickness(8, 0, 0, 0);
        Code.TextChanged += (_, _) => vm?.OnTextChanged(Code.Text);
        Code.TextArea.Caret.PositionChanged += (_, _) =>
        {
            if (!applying) vm?.OnCaretMoved(Code.TextArea.Caret.Line, Code.TextArea.Caret.Column);
        };
        Code.TextArea.TextEntered += (_, e) =>
        {
            if (e.Text is { Length: 1 } t && (t[0] == '#' || t[0] == ':' || char.IsLetter(t[0]))) ShowCompletion(automatic: true);
        };
        Code.TextArea.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Space && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0) { ShowCompletion(automatic: false); e.Handled = true; }
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
        if (vm.Analysis.StartLine > 0) MoveCaret(Math.Min(vm.Analysis.StartLine + 1, Code.Document.LineCount), 1);
        Refresh();
    }

    void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EditorViewModel.Analysis) or nameof(EditorViewModel.Playhead) or nameof(EditorViewModel.Route)) Refresh();
    }

    void Refresh()
    {
        if (vm == null) return;
        diagnostics.Diagnostics = vm.Analysis.Diagnostics;
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
                var line = doc.GetLineByNumber(edit.Line);
                int start = line.Offset + Math.Min(edit.Column - 1, line.Length);
                int length = Math.Min(edit.Length, line.EndOffset - start);
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
