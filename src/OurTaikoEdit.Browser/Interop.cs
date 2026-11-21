using System.Runtime.InteropServices.JavaScript;

namespace OurTaikoEdit.Browser;

// What main.js offers the app (setModuleImports).
static partial class Interop
{
    [JSImport("storage.save", "main.js")]
    public static partial Task SaveAsync();

    [JSImport("storage.persist", "main.js")]
    public static partial Task<bool> PersistAsync();

    [JSImport("files.offer", "main.js")]
    public static partial Task OfferAsync(byte[] bytes, string name, string mime);

    [JSImport("page.loaded", "main.js")]
    public static partial void Loaded();
}
