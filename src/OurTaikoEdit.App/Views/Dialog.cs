using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace OurTaikoEdit.App.Views;

// A dialog's content, shown by an IDialogHost: in a window of its own on the desktop, or over the
// page in a browser (which has no windows). It closes itself with a result.
public class Dialog : UserControl
{
    public static readonly StyledProperty<string> TitleProperty = AvaloniaProperty.Register<Dialog, string>(nameof(Title), "");

    public string Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    // Asked to close with a result (the host takes it away).
    public event Action<object?>? CloseRequested;
    // Shown, and taken away.
    public event EventHandler? Opened;
    public event EventHandler? Closed;
    // A close the dialog did not ask for (a window's close button): false keeps it open.
    public Func<bool>? AllowClose { get; set; }

    public void Close(object? result = null) => CloseRequested?.Invoke(result);

    // The service that showed it, for its own pickers.
    public OurTaikoEdit.App.ViewModels.IDialogService? Dialogs { get; set; }

    // The pickers of what it is shown in.
    public IStorageProvider StorageProvider => TopLevel.GetTopLevel(this)?.StorageProvider
        ?? throw new InvalidOperationException("The dialog is not shown.");

    internal void RaiseOpened() => OnOpened(EventArgs.Empty);
    internal void RaiseClosed() => OnClosed(EventArgs.Empty);

    protected virtual void OnOpened(EventArgs e) => Opened?.Invoke(this, e);
    protected virtual void OnClosed(EventArgs e) => Closed?.Invoke(this, e);
}

public interface IDialogHost
{
    // Shows the dialog until it closes; its result, or null when it was closed without one.
    Task<object?> ShowAsync(Dialog dialog);
}

public static class DialogHostExtensions
{
    public static async Task<T?> ShowAsync<T>(this IDialogHost host, Dialog dialog)
        => await host.ShowAsync(dialog) is T t ? t : default;
}

// The desktop: a fixed-size modal window over the owner, titled with the dialog's title.
public sealed class WindowDialogHost(Window owner) : IDialogHost
{
    public async Task<object?> ShowAsync(Dialog dialog)
    {
        var window = Wrap(dialog);
        object? result = null;
        bool requested = false;
        dialog.CloseRequested += r => { result = r; requested = true; window.Close(); };
        window.Closing += (_, e) => { if (!requested && dialog.AllowClose is { } allow && !allow()) e.Cancel = true; };
        await window.ShowDialog(owner);
        return result;
    }

    // The window a dialog is shown in (also for tests that show one on its own).
    public static Window Wrap(Dialog dialog)
    {
        var window = new Window
        {
            Title = dialog.Title, SizeToContent = SizeToContent.WidthAndHeight, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = dialog,
        };
        window.Opened += (_, _) => dialog.RaiseOpened();
        window.Closed += (_, _) => dialog.RaiseClosed();
        return window;
    }
}

// A browser: over the page, on a dimmed layer, one on top of another. Escape is the dialog's own
// Cancel button (IsCancel), as in a window.
public sealed class OverlayDialogHost(Panel layer) : IDialogHost
{
    public Task<object?> ShowAsync(Dialog dialog)
    {
        var done = new TaskCompletionSource<object?>();
        var scroll = new ScrollViewer { Content = dialog, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var title = new TextBlock { Text = dialog.Title, FontWeight = FontWeight.SemiBold, Margin = new Thickness(20, 14, 20, 0) };
        title.Classes.Add("muted");
        var card = new Border
        {
            Background = Brush("PanelBrush"), BorderBrush = Brush("LineStrongBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            BoxShadow = new BoxShadows(new BoxShadow { OffsetY = 12, Blur = 40, Color = Color.FromArgb(140, 0, 0, 0) }),
            Child = new DockPanel
            {
                Children = { Dock(title), scroll },
            },
        };
        var shade = new Panel { Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), Children = { card } };
        // Never taller than the page.
        layer.GetObservable(Visual.BoundsProperty).Subscribe(new Watcher(b => card.MaxHeight = Math.Max(200, b.Height - 48)));
        dialog.CloseRequested += result =>
        {
            if (done.Task.IsCompleted) return;
            layer.Children.Remove(shade);
            dialog.RaiseClosed();
            done.SetResult(result);
        };
        layer.Children.Add(shade);
        Dispatcher.UIThread.Post(() =>
        {
            dialog.RaiseOpened();
            // Focus inside, so keys go to it (and not to the editor underneath).
            if (!dialog.IsKeyboardFocusWithin
                && dialog.GetVisualDescendants().OfType<InputElement>().FirstOrDefault(c => c.Focusable && c.IsEffectivelyVisible && c.IsEffectivelyEnabled && c is not ScrollViewer) is { } first)
                first.Focus();
            // It opens at its top, whatever took the focus.
            Dispatcher.UIThread.Post(() => scroll.Offset = default, DispatcherPriority.Background);
        });
        return done.Task;

        static Control Dock(Control c) { DockPanel.SetDock(c, Avalonia.Controls.Dock.Top); return c; }
    }

    static IBrush Brush(string key)
        => Application.Current?.TryFindResource(key, out var value) == true && value is IBrush b ? b : Brushes.Gray;

    sealed class Watcher(Action<Rect> next) : IObserver<Rect>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(Rect value) => next(value);
    }
}
