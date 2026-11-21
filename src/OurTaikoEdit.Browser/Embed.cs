using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using Avalonia.Threading;
using OurTaikoEdit.App.ViewModels;
using OurTaikoEdit.Core.Projects;

namespace OurTaikoEdit.Browser;

// The editor inside a website's page (net.ourtaiko.org/…/charts/<id>/edit, in an iframe): the page
// hands it a chart through main.js (postMessage), which becomes a project in a folder of its own
// under Online/, outside the project list; saving and going back are the page's (EmbedHost).
static partial class Embed
{
    static string Root => Path.Combine(AppPaths.Folder(Environment.SpecialFolder.UserProfile), "Online");

    // A file name from the page, made safe for a folder.
    static string Safe(string name, string fallback)
    {
        string file = Path.GetFileName(name.Replace('\\', '/'));
        foreach (char c in Path.GetInvalidFileNameChars()) file = file.Replace(c, '_');
        return string.IsNullOrWhiteSpace(file) || file is "." or ".." ? fallback : file;
    }

    // Empties the chart's folder and says where its audio goes (main.js writes it there).
    [JSExport]
    public static string Prepare(string id, string audioName)
    {
        string folder = Path.Combine(Root, Safe(id, "chart"));
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, Safe(audioName, "audio"));
    }

    // Opens the chart (its audio is in place) in the editor. "" when it is open, else why not.
    [JSExport]
    public static string Open(string id, string name, string tjaName, string tja)
    {
        try
        {
            string folder = Path.Combine(Root, Safe(id, "chart"));
            var project = ChartProject.Create(folder, string.IsNullOrWhiteSpace(name) ? id : name, "tja", Safe(tjaName, "chart.tja"));
            ChartFile.Write(project.ChartPath, new ChartText(tja));
            project.Save();
            Dispatcher.UIThread.Post(() => App.App.Main?.OpenHosted(project, new EmbedHost()));
            return "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return e.Message;
        }
    }
}

sealed partial class EmbedHost : IChartHost
{
    public async Task<HostSaveResult> SaveAsync(string text)
    {
        string answer;
        try { answer = await SaveJs(text); }
        catch (JSException e) { return new HostSaveResult(false, e.Message); }
        using var doc = JsonDocument.Parse(answer);
        var root = doc.RootElement;
        bool saved = root.TryGetProperty("saved", out var s) && s.ValueKind == JsonValueKind.True;
        string? message = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        int? line = root.TryGetProperty("line", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : null;
        return new HostSaveResult(saved, message, line);
    }

    public void Leave() => LeaveJs();

    public void DirtyChanged(bool dirty) => DirtyJs(dirty);

    [JSImport("embed.save", "main.js")]
    private static partial Task<string> SaveJs(string text);

    [JSImport("embed.leave", "main.js")]
    private static partial void LeaveJs();

    [JSImport("embed.dirty", "main.js")]
    private static partial void DirtyJs(bool dirty);
}
