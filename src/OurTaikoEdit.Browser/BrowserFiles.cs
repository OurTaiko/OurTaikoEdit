using Avalonia.Platform.Storage;
using OurTaikoEdit.App.Views;

namespace OurTaikoEdit.Browser;

// Files the person picks, as the app's own copies: a folder (a project) goes into the home folder,
// kept in IndexedDB; a file (a song, something to convert) into /tmp, which is not kept. Files the
// app makes are downloaded.
sealed class BrowserFiles : IPickedFiles
{
    public static string Home => Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } home ? home : "/home/web_user";
    public static string Imported => Path.Combine(Home, "Imported");
    const string Picked = "/tmp/picked";

    public async Task<string?> FolderAsync(IStorageFolder folder)
    {
        string target = Free(Path.Combine(Imported, Safe(folder.Name)));
        await CopyAsync(folder, target);
        await Interop.SaveAsync();
        return target;
    }

    public async Task<string?> FileAsync(IStorageFile file)
    {
        string dir = Path.Combine(Picked, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        string target = Path.Combine(dir, Safe(file.Name));
        await using var from = await file.OpenReadAsync();
        await using var to = File.Create(target);
        await from.CopyToAsync(to);
        return target;
    }

    public async Task OfferAsync(string file)
    {
        byte[] bytes = await File.ReadAllBytesAsync(file);
        await Interop.OfferAsync(bytes, Path.GetFileName(file), Mime(file));
    }

    static async Task CopyAsync(IStorageFolder folder, string target)
    {
        Directory.CreateDirectory(target);
        await foreach (var item in folder.GetItemsAsync())
        {
            switch (item)
            {
                case IStorageFolder sub:
                    await CopyAsync(sub, Path.Combine(target, Safe(sub.Name)));
                    break;
                case IStorageFile file:
                    await using (var from = await file.OpenReadAsync())
                    await using (var to = File.Create(Path.Combine(target, Safe(file.Name))))
                        await from.CopyToAsync(to);
                    break;
            }
        }
    }

    // A folder that is not there yet: the name, else the name with a number.
    static string Free(string path)
    {
        string candidate = path;
        for (int n = 2; Directory.Exists(candidate) || File.Exists(candidate); n++) candidate = $"{path} ({n})";
        return candidate;
    }

    static string Safe(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Length == 0 ? "file" : name;
    }

    static string Mime(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" => "video/mp4",
        ".mov" => "video/quicktime",
        ".webm" => "video/webm",
        ".mkv" => "video/x-matroska",
        ".mp3" => "audio/mpeg",
        ".m4a" => "audio/mp4",
        ".ogg" or ".oga" or ".opus" => "audio/ogg",
        ".flac" => "audio/flac",
        ".wav" => "audio/wav",
        ".zip" => "application/zip",
        ".tja" or ".txt" => "text/plain",
        _ => "application/octet-stream",
    };
}
