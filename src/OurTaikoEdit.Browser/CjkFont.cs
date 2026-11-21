using Avalonia.Media;
using Avalonia.Media.Fonts;

namespace OurTaikoEdit.Browser;

// Chinese and Japanese text: a browser gives the canvas no system fonts, so Noto Sans SC (SIL Open
// Font License; it has kana too) is fetched before the app starts and is the fallback for what the
// app's own font (Inter) lacks. Without it (offline, the fetch failed) such text shows as boxes.
static class CjkFont
{
    // google/fonts at a fixed commit: the file cannot change under the app.
    const string Url = "https://cdn.jsdelivr.net/gh/google/fonts@a85815a42757630ce188fdad368c2dfc444d4773/ofl/notosanssc/NotoSansSC%5Bwght%5D.ttf";
    public const string Family = "fonts:OurTaikoEditCjk#Noto Sans SC";

    static byte[]? font;

    public static bool HasFont => font != null;

    public static async Task LoadAsync()
    {
        try
        {
            using var http = new HttpClient();
            font = await http.GetByteArrayAsync(Url);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Console.WriteLine($"The CJK font could not be loaded: {e.Message}");
        }
    }

    // Once the font manager is up (App.Starting): a collection made then (its base needs it).
    public static void Register()
    {
        if (font == null) return;
        var collection = new Collection();
        FontManager.Current.AddFontCollection(collection);
        collection.Add(font);
    }

    sealed class Collection : FontCollectionBase
    {
        static readonly Uri CollectionKey = new("fonts:OurTaikoEditCjk");
        public override Uri Key => CollectionKey;
        public void Add(byte[] bytes) => TryAddGlyphTypeface(new MemoryStream(bytes), out _);
    }
}
