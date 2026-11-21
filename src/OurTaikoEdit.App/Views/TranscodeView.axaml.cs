using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using OurTaikoEdit.App.ViewModels;

namespace OurTaikoEdit.App.Views;

// The transcoder: files are dropped on it from anywhere, and a click on the waveform moves where
// hearing it starts.
public partial class TranscodeView : UserControl
{
    public TranscodeView()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = e.DataTransfer.Contains(DataFormat.File) && Vm is { IsRunning: false } ? DragDropEffects.Copy : DragDropEffects.None);
        AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (Vm is not { IsRunning: false } vm || e.DataTransfer.TryGetFiles() is not { } items) return;
            var files = new List<string>();
            foreach (var item in items)
            {
                if (item is IStorageFolder folder && folder.TryGetLocalPath() is { } dir && Directory.Exists(dir))
                    files.AddRange(Directory.EnumerateFiles(dir).Order(StringComparer.OrdinalIgnoreCase));
                else if (item.TryGetLocalPath() is { } path) files.Add(path);
            }
            vm.Add(files);
        });
        Wave.Seek += t => Vm?.SeekPreview(t);
    }

    TranscodeViewModel? Vm => DataContext as TranscodeViewModel;
}
