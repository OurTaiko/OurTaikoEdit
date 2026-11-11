using Avalonia.Controls;
using Avalonia.Media;
using OurTaiko;

namespace OurTaikoEdit.App.Theme;

// The interface's colours, in one place for the XAML (App.axaml names them as resources) and for
// the controls that draw themselves. The OurTaiko sites' ink-and-paper palette with the lights
// off: warm near-black surfaces, cream text, don red for what acts, ka cyan for focus and
// selection.
public static class Ui
{
    // Surfaces, from the deepest (the canvases) to what lies on top (controls, hovered controls).
    public static readonly Color SunkenColor = Color.Parse("#151110");
    public static readonly Color BaseColor = Color.Parse("#1B1615");
    public static readonly Color PanelColor = Color.Parse("#231D1B");
    public static readonly Color RaisedColor = Color.Parse("#2E2623");
    public static readonly Color HoverColor = Color.Parse("#3A302C");
    public static readonly Color ActiveColor = Color.Parse("#463A34");
    public static readonly Color LineColor = Color.Parse("#352C28");
    public static readonly Color LineStrongColor = Color.Parse("#4B3F39");

    public static readonly Color TextColor = Color.Parse("#F3E9DC");
    public static readonly Color MutedColor = Color.Parse("#B7A89D");
    public static readonly Color FaintColor = Color.Parse("#84766C");

    // Don red: the mark itself, and the darker fill that carries white text.
    public static readonly Color AccentColor = Color.Parse("#F65040");
    public static readonly Color AccentFillColor = Color.Parse("#D83A2C");
    public static readonly Color KaColor = Color.Parse("#68D0D7");
    public static readonly Color WarningColor = Color.Parse("#F2B63C");
    public static readonly Color ErrorColor = Color.Parse("#FF7A6B");
    public static readonly Color InfoColor = Color.Parse("#7DB8E8");
    public static readonly Color BranchColor = Color.Parse("#B99AE8");

    public static readonly IBrush Sunken = Solid(SunkenColor), Base = Solid(BaseColor), Panel = Solid(PanelColor), Raised = Solid(RaisedColor);
    public static readonly IBrush Hover = Solid(HoverColor), Active = Solid(ActiveColor), Line = Solid(LineColor), LineStrong = Solid(LineStrongColor);
    public static readonly IBrush Text = Solid(TextColor), Muted = Solid(MutedColor), Faint = Solid(FaintColor);
    public static readonly IBrush Accent = Solid(AccentColor), AccentFill = Solid(AccentFillColor), Ka = Solid(KaColor);
    public static readonly IBrush Warning = Solid(WarningColor), Error = Solid(ErrorColor), Info = Solid(InfoColor), Branch = Solid(BranchColor);
    public static readonly IBrush AccentSoft = Solid(AccentColor, 0.16), KaSoft = Solid(KaColor, 0.16), Selection = Solid(KaColor, 0.28);

    // The sites' difficulty colours (dark text goes on them).
    public static readonly IBrush Easy = Solid(Color.Parse("#F9A15A")), Normal = Solid(Color.Parse("#8FD16A")), Hard = Solid(Color.Parse("#6CC7D8"));
    public static readonly IBrush Oni = Solid(Color.Parse("#F77FAB")), Ura = Solid(Color.Parse("#B99AE8"));
    public static readonly IBrush OnDifficulty = Solid(Color.Parse("#382D2A"));

    // The routes of a branch, as the lane tints them.
    public static readonly IBrush[] Routes = { Solid(Color.Parse("#9C8F86")), Solid(Color.Parse("#4FB4E6")), Solid(Color.Parse("#F06CC0")) };

    // The interface's font (Inter, then the system's) and the code's, for the controls that draw
    // their own text.
    public static FontFamily Font => Resource("ContentControlThemeFontFamily");
    public static FontFamily CodeFont => Resource("CodeFont");

    static FontFamily Resource(string key)
        => Avalonia.Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var found) && found is FontFamily family ? family : FontFamily.Default;

    public static IBrush Solid(Color color, double opacity = 1) => new SolidColorBrush(color, opacity).ToImmutable();

    // A difficulty's colour from its course as written ("Oni", "3", "Edit"); a course that is not
    // one of the five is plain.
    public static IBrush Difficulty(string? course)
    {
        return SongInfo.DifficultyOf(course ?? "") switch
        {
            OurTaiko.Difficulty.Easy => Easy,
            OurTaiko.Difficulty.Normal => Normal,
            OurTaiko.Difficulty.Hard => Hard,
            OurTaiko.Difficulty.Oni => Oni,
            OurTaiko.Difficulty.Ura => Ura,
            _ => Muted,
        };
    }
}
