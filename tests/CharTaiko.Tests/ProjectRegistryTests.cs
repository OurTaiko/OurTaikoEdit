using System.Text;
using CharTaiko.Core.Languages;
using CharTaiko.Core.Projects;
using CharTaiko.Tja;

namespace CharTaiko.Tests;

public class ProjectRegistryTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "chartaiko-" + Guid.NewGuid().ToString("N"));

    public ProjectRegistryTests()
    {
        ChartLanguages.Register(new TjaLanguage());
        Directory.CreateDirectory(root);
    }

    public void Dispose() => Directory.Delete(root, true);

    [Fact]
    public void ANewProjectIsAFolderWithItsChartAudioAndProjectFile()
    {
        string audio = Path.Combine(root, "song.ogg");
        File.WriteAllBytes(audio, new byte[] { 1, 2, 3 });
        var registry = new ProjectRegistry(Path.Combine(root, "data"));
        var project = registry.CreateProject(root, "My: Song", ChartLanguages.Find("tja")!, audio);

        Assert.Equal(Path.Combine(root, "My_ Song"), project.Folder);
        Assert.True(File.Exists(Path.Combine(project.Folder, "song.ogg")));
        Assert.True(File.Exists(Path.Combine(project.Folder, ChartProject.FileName)));
        string chart = ChartFile.Read(project.ChartPath).Text;
        Assert.Contains("WAVE:song.ogg", chart);
        Assert.NotNull(new TjaLanguage().Analyze(chart, 0).Chart);

        // Tracked across restarts.
        var again = new ProjectRegistry(Path.Combine(root, "data"));
        Assert.Equal(project.Folder, Assert.Single(again.Projects).Folder);
        Assert.Equal("My: Song", ChartProject.Load(project.Folder).Name);
    }

    [Fact]
    public void AFolderWithAChartBecomesAProject()
    {
        string folder = Path.Combine(root, "Existing");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "song.tja"), "BPM:120\nCOURSE:Oni\n#START\n1,\n#END\n");
        var registry = new ProjectRegistry(Path.Combine(root, "data"));
        var project = registry.OpenFolder(folder);
        Assert.Equal("tja", project.ChartType);
        Assert.Equal("song.tja", project.Chart);
        Assert.True(File.Exists(ChartProject.PathIn(folder)));
        registry.Untrack(folder);
        Assert.Empty(registry.Projects);
    }

    [Fact]
    public void ShiftJisChartsAreReadAndSavedBack()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string path = Path.Combine(root, "sjis.tja");
        File.WriteAllBytes(path, Encoding.GetEncoding(932).GetBytes("TITLE:太鼓\nBPM:120\n"));
        var chart = ChartFile.Read(path);
        Assert.Equal("shift_jis", chart.EncodingName);
        Assert.StartsWith("TITLE:太鼓", chart.Text);
        ChartFile.Write(path, chart with { Text = chart.Text + "// edited\n" });
        Assert.Equal("shift_jis", ChartFile.Read(path).EncodingName);
    }
}
