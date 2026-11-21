namespace OurTaikoEdit.Core.Projects;

// The platform's folders. In a browser .NET has none of its own (GetFolderPath is empty): there
// they are under HOME, the folder the page keeps (in IndexedDB).
public static class AppPaths
{
    public static string Folder(Environment.SpecialFolder folder)
    {
        string path = Environment.GetFolderPath(folder);
        if (path.Length > 0) return path;
        string home = Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } h ? h : "/home/web_user";
        return folder switch
        {
            Environment.SpecialFolder.ApplicationData => Path.Combine(home, ".config"),
            Environment.SpecialFolder.LocalApplicationData => Path.Combine(home, ".local", "share"),
            _ => home,
        };
    }
}
