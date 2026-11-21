using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using OurTaikoEdit.App.Views;

namespace OurTaikoEdit.UiTests;

// Dialogs are controls (shown in a window on the desktop, over the page in a browser): the tests
// show them in a window of their own, as the desktop does.
static class DialogTesting
{
    static readonly ConditionalWeakTable<Dialog, Window> Windows = new();

    public static Window Show(this Dialog dialog)
    {
        var window = WindowDialogHost.Wrap(dialog);
        dialog.CloseRequested += _ => window.Close();
        Windows.AddOrUpdate(dialog, window);
        window.Show();
        return window;
    }

    public static Task<T?> ShowDialog<T>(this Dialog dialog, Window owner)
    {
        var shown = new WindowDialogHost(owner).ShowAsync<T>(dialog);
        if (dialog.Parent is Window window) Windows.AddOrUpdate(dialog, window);
        return shown;
    }

    // The dialogs shown over a window (each in a window of its own).
    public static List<Dialog> OwnedDialogs(this Window owner) => owner.OwnedWindows.Select(w => w.Content).OfType<Dialog>().ToList();

    public static Window Window(this Dialog dialog)
        => Windows.TryGetValue(dialog, out var w) ? w : TopLevel.GetTopLevel(dialog) as Window ?? throw new InvalidOperationException("Not shown.");

    public static WriteableBitmap? CaptureRenderedFrame(this Dialog dialog) => dialog.Window().CaptureRenderedFrame();
}
