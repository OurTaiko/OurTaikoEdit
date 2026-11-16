using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using OurTaikoEdit.App.ViewModels;

namespace OurTaikoEdit.App.Views;

// The play scene's window side: every frame the play is brought up to the song (the stage draws
// itself again when the play says so), and the keys are the drum: D and K the rim, F and J the
// face, as the game has them; Space pauses, R starts again, Tab is auto play, Esc goes back.
public partial class PlayView : UserControl
{
    // The drum keys being held: a key held down repeats, and a repeat is not a press.
    readonly HashSet<PhysicalKey> held = new();
    bool running;

    public PlayView()
    {
        InitializeComponent();
        LostFocus += (_, _) => held.Clear();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        running = true;
        // Once it is shown (its place in the window is made visible as it is put there).
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Focus());
        Next();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        running = false;
    }

    void Next() => TopLevel.GetTopLevel(this)?.RequestAnimationFrame(_ =>
    {
        if (!running) return;
        (DataContext as PlayViewModel)?.Tick();
        Next();
    });

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || DataContext is not PlayViewModel play) return;
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta | KeyModifiers.Alt)) != 0) return;
        switch (e.PhysicalKey)
        {
            case PhysicalKey.D: Drum(play, e.PhysicalKey, ka: true, right: false); break;
            case PhysicalKey.F: Drum(play, e.PhysicalKey, ka: false, right: false); break;
            case PhysicalKey.J: Drum(play, e.PhysicalKey, ka: false, right: true); break;
            case PhysicalKey.K: Drum(play, e.PhysicalKey, ka: true, right: true); break;
            case PhysicalKey.Space: play.TogglePause(); break;
            case PhysicalKey.R: play.Restart(); break;
            case PhysicalKey.Tab: play.ToggleAuto(); break;
            case PhysicalKey.Escape: play.Back(); break;
            default: return;
        }
        e.Handled = true;
    }

    void Drum(PlayViewModel play, PhysicalKey key, bool ka, bool right)
    {
        if (held.Add(key)) play.Hit(ka, right);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        held.Remove(e.PhysicalKey);
    }
}
