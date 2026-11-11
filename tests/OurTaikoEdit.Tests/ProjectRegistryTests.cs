using System.Text;
using OurTaikoEdit.Core.Languages;
using OurTaikoEdit.Core.Projects;
using OurTaikoEdit.Tja;

namespace OurTaikoEdit.Tests;

public class ProjectRegistryTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "ourtaikoedit-" + Guid.NewGuid().ToString("N"));

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
    public void AProjectFromBeforeTheRenameOpensAndIsRenamed()
    {
        string folder = Path.Combine(root, "Old");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "song.tja"), "BPM:120\nCOURSE:Oni\n#START\n1,\n#END\n");
        File.WriteAllText(Path.Combine(folder, ChartProject.LegacyFileName),
            "{\"name\":\"Old Song\",\"chartType\":\"tja\",\"chart\":\"song.tja\",\"lastBlock\":2}");
        var project = new ProjectRegistry(Path.Combine(root, "data")).OpenFolder(folder);
        Assert.Equal("Old Song", project.Name);
        Assert.Equal(2, project.LastBlock);
        Assert.True(File.Exists(ChartProject.PathIn(folder)));
        Assert.False(File.Exists(Path.Combine(folder, ChartProject.LegacyFileName)));
    }

    [Fact]
    public void TheDataFolderFromBeforeTheRenameIsMovedOver()
    {
        string legacy = Path.Combine(root, "CharTaiko");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "projects.json"), "[]");
        string folder = ProjectRegistry.DataFolderIn(root);
        Assert.Equal(Path.Combine(root, "OurTaikoEdit"), folder);
        Assert.True(File.Exists(Path.Combine(folder, "projects.json")));
        Assert.False(Directory.Exists(legacy));
        // Once moved, an old folder that shows up again is left alone.
        Directory.CreateDirectory(legacy);
        Assert.Equal(folder, ProjectRegistry.DataFolderIn(root));
        Assert.True(Directory.Exists(legacy));
    }

    [Fact]
    public void ShiftJisChartsAreReadAndSavedAsUtf8()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string path = Path.Combine(root, "sjis.tja");
        File.WriteAllBytes(path, Encoding.GetEncoding(932).GetBytes("TITLE:太鼓\nBPM:120\n"));
        var chart = ChartFile.Read(path);
        Assert.Equal("shift_jis", chart.EncodingName);
        Assert.StartsWith("TITLE:太鼓", chart.Text);
        ChartFile.Write(path, chart with { Text = chart.Text + "// edited\n" });
        var saved = ChartFile.Read(path);
        Assert.Equal(("utf-8", false), (saved.EncodingName, saved.Bom));
        Assert.Equal(chart.Text + "// edited\n", saved.Text);
        Assert.False(File.ReadAllBytes(path) is [0xEF, 0xBB, 0xBF, ..]);
    }

    [Fact]
    public void ChartsAreReadInTheEncodingTheyWereWrittenIn()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string path = Path.Combine(root, "any.tja");
        // UTF-16 is known by its BOM; a UTF-8 BOM is noted and dropped on save.
        File.WriteAllBytes(path, new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("TITLE:太鼓")).ToArray());
        Assert.Equal(("utf-16", "TITLE:太鼓"), (ChartFile.Read(path).EncodingName, ChartFile.Read(path).Text));
        File.WriteAllBytes(path, new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("TITLE:x")).ToArray());
        var bom = ChartFile.Read(path);
        Assert.Equal(("utf-8", true, false, "TITLE:x"), (bom.EncodingName, bom.Bom, bom.IsConverted, bom.Text));
        // GB18030 is not told from Shift-JIS by itself: it is read as Shift-JIS until chosen.
        File.WriteAllBytes(path, Encoding.GetEncoding(54936).GetBytes("TITLE:千本樱"));
        Assert.Equal("shift_jis", ChartFile.Read(path).EncodingName);
        Assert.Equal("TITLE:千本樱", ChartFile.Read(path, "gb18030").Text);
    }
}
