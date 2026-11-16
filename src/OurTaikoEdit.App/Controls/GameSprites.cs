using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using OurTaiko;

namespace OurTaikoEdit.App.Controls;

// The game's note sprites (OurTaikoPlay Assets/OurTaiko/Art/game/notes, see Assets/Game/PROVENANCE.md),
// cut as its sprite metadata cuts them. Unity measures a sprite's rect from the sheet's bottom.
public static class GameSprites
{
    public static Bitmap Notes { get; } = Load("notes_atlas.png");
    public static Bitmap Moji { get; } = Load("moji.png");
    public static Bitmap MojiRoll { get; } = Load("moji_drumroll_mid.png");

    static Bitmap Load(string name) => new(AssetLoader.Open(new Uri("avares://OurTaikoEdit/Assets/Game/" + name)));

    const double NotesHeight = 2504, MojiHeight = 624;

    static Rect Cut(double sheetHeight, double x, double y, double w, double h) => new(x, sheetHeight - y - h, w, h);

    // NoteN frames are 192 square, N the note's TJA digit (NoteKind's value).
    static readonly Dictionary<int, (double X, double Y)> NoteFrames = new()
    {
        [1] = (0, 2112), [2] = (0, 1920), [3] = (0, 1728), [4] = (0, 1536), [5] = (0, 1344), [6] = (0, 1152),
        [7] = (0, 960), [9] = (0, 576), [12] = (192, 768), [16] = (384, 1728),
    };

    // The head of a note (expression frame 0), or null for one the game does not draw (ad-lib).
    public static Rect? Note(NoteKind kind)
        => NoteFrames.TryGetValue((int)kind, out var at) ? Cut(NotesHeight, at.X, at.Y, 192, 192) : null;

    // NoteNAlternate (the other mouth) and NoteNAngry frames, for the notes that have them.
    static readonly Dictionary<int, (double X, double Y)> AlternateFrames = new()
    {
        [1] = (192, 2112), [2] = (192, 1920), [3] = (192, 1728), [4] = (192, 1536), [5] = (192, 1344), [6] = (192, 1152),
        [7] = (192, 960), [12] = (384, 768), [16] = (384, 1536),
    };
    static readonly Dictionary<int, (double X, double Y)> AngryFrames = new()
    {
        [1] = (384, 2112), [2] = (384, 1920), [5] = (384, 1344), [7] = (384, 960),
    };

    // The head of a note with the face of a NoteExpression frame (NoteLaneRenderer.HeadSprite):
    // every note opens its mouth on the same steps; the big notes rest with theirs open, and a
    // note without an angry face opens its mouth instead.
    public static Rect? Note(NoteKind kind, int expression)
    {
        int k = (int)kind;
        if (!NoteFrames.TryGetValue(k, out var rest)) return null;
        var at = rest;
        if (expression != OurTaiko.NoteExpression.Rest)
        {
            bool open = expression != OurTaiko.NoteExpression.Closed;
            bool restsOpen = kind is NoteKind.BigDon or NoteKind.BigKa or NoteKind.BigRoll or NoteKind.Kadon;
            if (restsOpen) { if (!open && AlternateFrames.TryGetValue(k, out var shut)) at = shut; }
            else if (open)
            {
                if (expression == OurTaiko.NoteExpression.Angry && AngryFrames.TryGetValue(k, out var angry)) at = angry;
                else if (AlternateFrames.TryGetValue(k, out var other)) at = other;
            }
        }
        return Cut(NotesHeight, at.X, at.Y, 192, 192);
    }

    public static Rect BalloonTail { get; } = Cut(NotesHeight, 0, 384, 192, 192);
    public static Rect RollBody(bool big) => big ? Cut(NotesHeight, 72, 768, 72, 192) : Cut(NotesHeight, 0, 768, 72, 192);
    public static Rect RollTail(bool big) => big ? Cut(NotesHeight, 0, 0, 120, 192) : Cut(NotesHeight, 0, 192, 80, 192);

    // MojiN frames are 256×48, N the note's NoteMoji (12 is Kadon at the sheet's top).
    public static Rect? MojiFrame(int moji)
    {
        if (moji < 0 || moji > 12) return null;
        double y = moji == 12 ? 576 : 528 - 48 * moji;
        return Cut(MojiHeight, 0, y, 256, 48);
    }
}
