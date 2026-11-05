using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using CharTaiko.Core.Languages;

namespace CharTaiko.App.Editor;

sealed class CompletionData(CompletionItem item) : ICompletionData
{
    public IImage? Image => null;
    public string Text => item.Text;
    public object Content => item.Text;
    public object Description => new TextBlock
    {
        Text = item.Detail == null ? item.Description : item.Description + "\n" + item.Detail,
        TextWrapping = TextWrapping.Wrap, MaxWidth = 360,
    };
    public double Priority => 0;

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        => textArea.Document.Replace(completionSegment, item.Text);
}
