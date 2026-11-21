using System.Runtime.InteropServices.JavaScript;
using Avalonia.Media;
using Avalonia.Media.Fonts;

namespace OurTaikoEdit.Browser;

// Chinese and Japanese text: a browser gives the canvas no system fonts, so Noto Sans SC (SIL Open
// Font License; it has kana too) is fetched before the app starts and is the fallback for what the
// app's own font (Inter) lacks. Without it (offline, the fetch failed) such text shows as boxes.
static class CjkFont
{
    // On ourtaiko-public at a path that never changes (build/publish-web-vendor.py): from a build's
    // own folder (edit/<id>/) it is ../../fonts/…, so it comes from wherever the page came from
    // (distrib.ourtaiko.org or the Aliyun mirror). Elsewhere (dotnet run) distrib is used.
    const string FontPath = "fonts/noto-sans-sc/a85815a/NotoSansSC-VF.ttf";
    const string Distrib = "https://distrib.ourtaiko.org/";
    public const string Family = "fonts:OurTaikoEditCjk#Noto Sans SC";

    static byte[]? font;

    public static bool HasFont => font != null;

    // The font's bytes, for a Web Worker (FrameWorker), which is given them rather than fetching anew.
    public static byte[]? Bytes => font;
    public static void Use(byte[]? bytes) => font = bytes;

    public static async Task LoadAsync()
    {
        string page = JSHost.GlobalThis.GetPropertyAsJSObject("document")!.GetPropertyAsString("baseURI")!;
        using var http = new HttpClient();
        foreach (var url in new[] { new Uri(new Uri(page), "../../" + FontPath), new Uri(Distrib + FontPath) })
        {
            try
            {
                byte[] bytes = await http.GetByteArrayAsync(url);
                // A TrueType font, not a development server's page in its place.
                if (bytes.Length > 4 && bytes[0] == 0 && bytes[1] == 1 && bytes[2] == 0 && bytes[3] == 0)
                {
                    font = bytes;
                    return;
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                Console.WriteLine($"The CJK font could not be loaded from {url}: {e.Message}");
            }
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
