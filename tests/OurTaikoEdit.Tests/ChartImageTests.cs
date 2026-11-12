using System.Buffers.Binary;
using System.IO.Compression;
using OurTaikoEdit.Core.Importing;
using OurTaikoEdit.Core.Languages;
using OurTaikoEdit.Core.Localization;
using OurTaikoEdit.Core.Mods;
using OurTaikoEdit.Core.Projects;
using OurTaikoEdit.Mods.ChartImage;
using OurTaikoEdit.Tja;

namespace OurTaikoEdit.Tests;

// The chart image mod: its decoder against TJAAnalyzer's own test image and output, its PNG
// reader, and the mod loaded the way the app loads it.
public class ChartImageTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "ourtaikoedit-image-" + Guid.NewGuid().ToString("N"));
    static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
    static readonly TjaLanguage Tja = new();

    public ChartImageTests()
    {
        Directory.CreateDirectory(root);
        ModRegistry.Load(new ChartImageMod());
    }

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (IOException) { }
    }

    static DecodedMeasure Bar(int number, string notes) => new(number, 4, notes.ToCharArray(), Array.Empty<DecodedCommand>());

    [Fact]
    public void AMeasuresGridIsTheCoarsestThatHoldsWhatIsDrawn()
    {
        // The renderer rounds each position to a whole pixel, so an uneven division has uneven steps.
        Assert.Equal(new[] { 0, 48, 96, 144 }, ChartImageDecoder.SubdivisionOffsets(192, 4));
        Assert.Equal(new[] { 0, 14, 27, 41, 55, 69, 82 }, ChartImageDecoder.SubdivisionOffsets(96, 7));

        Assert.Equal(4, ChartImageDecoder.FitSubdivisions(new[] { 0, 48, 96, 144 }, 192)!.Subdivisions);
        Assert.Equal(16, ChartImageDecoder.FitSubdivisions(new[] { 0, 12, 24, 84 }, 192)!.Subdivisions);
        // Triplets and 32nds in one measure need the grid that has both.
        Assert.Equal(30, ChartImageDecoder.FitSubdivisions(new[] { 0, 16, 24, 32 }, 240)!.Subdivisions);
        Assert.Equal(205, ChartImageDecoder.FitSubdivisions(new[] { 2 }, 512)!.Subdivisions);
        // What no grid holds is refused, not moved to the nearest place.
        Assert.Null(ChartImageDecoder.FitSubdivisions(new[] { 0, 1 }, 192));

        Assert.Equal("4/4", ChartImageDecoder.TimeSignature(4));
        Assert.Equal("9/8", ChartImageDecoder.TimeSignature(4.5));
        Assert.Equal("3/4", ChartImageDecoder.TimeSignature(3));
        Assert.Equal("2/4", ChartImageDecoder.TimeSignature(2));
        Assert.Equal("6/4", ChartImageDecoder.TimeSignature(6));
    }

    [Fact]
    public void EveryLongNoteOpensAndClosesOnce()
    {
        // A tail is often past its own measure.
        ChartImageDecoder.VerifyLongNotes(new[] { Bar(1, "7000"), Bar(2, "0080") }, new[] { 12 });
        Assert.Throws<DecodeException>(() => ChartImageDecoder.VerifyLongNotes(new[] { Bar(1, "7000"), Bar(2, "0000") }, new[] { 12 }));
        Assert.Throws<DecodeException>(() => ChartImageDecoder.VerifyLongNotes(new[] { Bar(1, "7050"), Bar(2, "0080") }, new[] { 12 }));
        Assert.Throws<DecodeException>(() => ChartImageDecoder.VerifyLongNotes(new[] { Bar(1, "0080") }, Array.Empty<int>()));
        Assert.Throws<DecodeException>(() => ChartImageDecoder.VerifyLongNotes(new[] { Bar(1, "7008") }, Array.Empty<int>()));
    }

    [Fact]
    public void TheSampleImageDecodesWithItsWrappedDrumrolls()
    {
        var chart = ChartImageDecoder.Decode(Fixture("wrapped_drumrolls.png"));
        Assert.Equal(107, chart.Measures.Count);
        Assert.Equal(160, chart.Bpm);
        Assert.Equal(new[] { 6, 10 }, chart.BalloonHits);
        string NotesAt(int number) => new(chart.Measures[number - 1].Notes.ToArray());
        foreach (int offset in new[] { 0, 51 })
        {
            Assert.Equal("1102201110222050", NotesAt(42 + offset));
            Assert.Equal("00000825", NotesAt(43 + offset));
            Assert.Equal("00000846", NotesAt(46 + offset));
            Assert.Equal("00000846", NotesAt(47 + offset));
            Assert.Equal("00000008", NotesAt(48 + offset));
        }
        Assert.Equal(28, chart.Measures.Sum(m => m.Notes.Count(n => n is '5' or '6' or '7')));
        Assert.Equal(28, chart.Measures.Sum(m => m.Notes.Count(n => n == '8')));
    }

    // An image the original tool refuses (the user's, 2026-11-11): drumrolls whose starts are under
    // the note before them, in measures too finely divided for the grid alone to place them.
    [Fact]
    public void ADrumrollStartsWhereItsUncoveredRowsSay()
    {
        var chart = ChartImageDecoder.Decode(Fixture("covered_drumroll_starts.png"));
        Assert.Equal(77, chart.Measures.Count);
        Assert.Equal(128, chart.Bpm);
        Assert.Equal(new[] { 6, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2 }, chart.BalloonHits);
        string NotesAt(int number) => new(chart.Measures[number - 1].Notes.ToArray());
        // 48 positions of 4 pixels; the ka at 21 hides the start of the drumroll at 24, and the
        // grid would allow 22 and 23 as well.
        Assert.Equal("100200200000200200100200500000008000200000000000", NotesAt(49));
        // The same, where the grid has only the one place.
        Assert.Equal("1122121250802010", NotesAt(47));
        // 24ths and 32nds in one measure, the last notes 6 pixels apart, then a drumroll under the last.
        Assert.Equal(96, chart.Measures[6].Subdivisions);
        Assert.EndsWith("200200200200", NotesAt(7));
        Assert.Equal("500000000800102202102100", NotesAt(8));
        // Six short drumrolls end to end, balloons, big notes among small ones.
        Assert.Equal("0000585858585858", NotesAt(32));
        Assert.Equal("70080040", NotesAt(33));
        Assert.Equal("30787878", NotesAt(34));
        Assert.Equal("100000300300301202102200", NotesAt(58));
        Assert.Equal(711, chart.Measures.Sum(m => m.Notes.Count(n => n is >= '1' and <= '4')));
        Assert.Equal(21, chart.Measures.Sum(m => m.Notes.Count(n => n is '5' or '6' or '7')));

        var imported = ChartImporters.Find("chart-image")!.Import(new ChartImportRequest(Fixture("covered_drumroll_starts.png"), Tja, "Bassline", null));
        // The one measure under 4 pixels a position is pointed out.
        Assert.Contains("check them: 7\n", imported.Report);
        Assert.False(Tja.Analyze(imported.Text, 0).HasErrors);
    }

    static string Body(string tja)
    {
        var lines = tja.Replace("\r\n", "\n").Split('\n');
        int start = Array.IndexOf(lines, "#START"), end = Array.IndexOf(lines, "#END");
        Assert.True(start >= 0 && end > start);
        return string.Join("\n", lines[start..(end + 1)]);
    }

    [Fact]
    public void TheChartWrittenIsTheOneTheOriginalToolWrites()
    {
        var importer = ChartImporters.Find("chart-image")!;
        Assert.True(importer.CanWrite(Tja));
        var imported = importer.Import(new ChartImportRequest(Fixture("wrapped_drumrolls.png"), Tja, "おまつりタイム", "song.ogg"));
        string expected = File.ReadAllText(Fixture("wrapped_drumrolls.tja"));
        Assert.Equal(Body(expected), Body(imported.Text));
        Assert.Contains("\nBPM:160\n", imported.Text);
        Assert.Contains("\nBALLOON:6,10\n", imported.Text);
        Assert.StartsWith("TITLE:おまつりタイム\n", imported.Text);
        Assert.Contains("\nWAVE:song.ogg\n", imported.Text);
        Assert.Contains("107 measures", imported.Report);

        // The app reads it without problems that stop the timeline, as one Oni chart.
        var analysis = Tja.Analyze(imported.Text, 0);
        Assert.False(analysis.HasErrors);
        Assert.DoesNotContain(analysis.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Single(analysis.Blocks);
        Assert.Equal(107, analysis.Measures.Count);
        Assert.Equal("おまつりタイム", analysis.Title);
    }

    [Fact]
    public void ANewProjectTakesTheImportedChart()
    {
        var importer = ChartImporters.Find("chart-image")!;
        var imported = importer.Import(new ChartImportRequest(Fixture("wrapped_drumrolls.png"), Tja, "Matsuri", null));
        var registry = new ProjectRegistry(Path.Combine(root, "data"));
        var project = registry.CreateProject(root, "Matsuri", Tja, null, imported.Text);
        Assert.Equal(imported.Text, ChartFile.Read(project.ChartPath).Text);
        Assert.Equal("tja", project.ChartType);
    }

    [Fact]
    public void WhatIsNotAChartImageIsRefusedWithAReason()
    {
        var importer = ChartImporters.Find("chart-image")!;
        string notPng = Path.Combine(root, "photo.png");
        File.WriteAllBytes(notPng, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3 });
        var e = Assert.Throws<ChartImportException>(() => importer.Import(new ChartImportRequest(notPng, Tja, "x", null)));
        Assert.Contains("not a PNG", e.Message);

        // A PNG with nothing of a chart in it.
        string blank = Path.Combine(root, "blank.png");
        File.WriteAllBytes(blank, MakePng(8, 8, 2, 8, (x, y) => new byte[] { 204, 204, 204 }));
        e = Assert.Throws<ChartImportException>(() => importer.Import(new ChartImportRequest(blank, Tja, "x", null)));
        Assert.Contains("No chart lanes", e.Message);

        e = Assert.Throws<ChartImportException>(() => importer.Import(new ChartImportRequest(Path.Combine(root, "gone.png"), Tja, "x", null)));
        Assert.Contains("could not be opened", e.Message);

        // The reasons are in the interface's languages.
        Assert.Contains("不是 PNG", Strings.Table["ChartImageNotPng"].Zh);
        Assert.Contains("PNG ファイルではありません", Strings.Table["ChartImageNotPng"].Ja);
    }

    // A PNG of one filter type per row in turn, so that every filter is read. pixel gives the
    // samples of a pixel: one byte each for depth 8, or one sample for the packed depths.
    static byte[] MakePng(int width, int height, int colorType, int depth, Func<int, int, byte[]> pixel, byte[]? palette = null)
    {
        int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
        int bits = channels * depth, stride = (width * bits + 7) / 8, bpp = Math.Max(1, bits / 8);
        var rows = new byte[height][];
        for (int y = 0; y < height; y++)
        {
            rows[y] = new byte[stride];
            for (int x = 0; x < width; x++)
            {
                byte[] samples = pixel(x, y);
                if (depth == 8) samples.CopyTo(rows[y], x * channels);
                else rows[y][x * depth / 8] |= (byte)(samples[0] << (8 - depth - x * depth % 8));
            }
        }
        using var raw = new MemoryStream();
        for (int y = 0; y < height; y++)
        {
            int filter = y % 5;
            raw.WriteByte((byte)filter);
            for (int i = 0; i < stride; i++)
            {
                int a = i >= bpp ? rows[y][i - bpp] : 0, b = y > 0 ? rows[y - 1][i] : 0, c = y > 0 && i >= bpp ? rows[y - 1][i - bpp] : 0;
                int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                int predicted = filter switch { 0 => 0, 1 => a, 2 => b, 3 => (a + b) / 2, _ => pa <= pb && pa <= pc ? a : pb <= pc ? b : c };
                raw.WriteByte((byte)(rows[y][i] - predicted));
            }
        }
        using var packed = new MemoryStream();
        using (var zlib = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(raw.ToArray());

        using var file = new MemoryStream();
        file.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        void Chunk(string type, byte[] body)
        {
            var length = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)body.Length);
            file.Write(length);
            file.Write(System.Text.Encoding.ASCII.GetBytes(type));
            file.Write(body);
            file.Write(new byte[4]);   // the checksum, which the reader does not look at
        }
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        header[8] = (byte)depth;
        header[9] = (byte)colorType;
        Chunk("IHDR", header);
        if (palette != null) Chunk("PLTE", palette);
        // In two pieces, as large images are.
        byte[] data = packed.ToArray();
        Chunk("IDAT", data[..(data.Length / 2)]);
        Chunk("IDAT", data[(data.Length / 2)..]);
        Chunk("IEND", Array.Empty<byte>());
        return file.ToArray();
    }

    [Fact]
    public void PngsOfEveryKindAreReadPixelForPixel()
    {
        // Truecolour with and without alpha: each row filtered another way.
        (int, int, int) Colour(int x, int y) => ((x * 37 + y * 11) % 256, (x * 5 + y * 91) % 256, (x * y * 7 + 3) % 256);
        foreach (int colorType in new[] { 2, 6 })
        {
            var pixels = Png.Read(MakePng(13, 11, colorType, 8, (x, y) =>
            {
                var (r, g, b) = Colour(x, y);
                return colorType == 6 ? new[] { (byte)r, (byte)g, (byte)b, (byte)(x * 20) } : new[] { (byte)r, (byte)g, (byte)b };
            }));
            Assert.Equal((13, 11), (pixels.Width, pixels.Height));
            for (int y = 0; y < 11; y++)
                for (int x = 0; x < 13; x++) Assert.Equal(Colour(x, y), pixels.At(x, y));
        }

        // A palette at every depth it can have.
        byte[] palette = Enumerable.Range(0, 256).SelectMany(i => new[] { (byte)i, (byte)(255 - i), (byte)(i * 3) }).ToArray();
        foreach (int depth in new[] { 1, 2, 4, 8 })
        {
            int colours = 1 << depth;
            var pixels = Png.Read(MakePng(13, 7, 3, depth, (x, y) => new[] { (byte)((x + y * 3) % colours) }, palette[..(colours * 3)]));
            for (int y = 0; y < 7; y++)
                for (int x = 0; x < 13; x++)
                {
                    int i = (x + y * 3) % colours;
                    Assert.Equal((i, 255 - i, i * 3 % 256), pixels.At(x, y));
                }
        }

        // Grey, with alpha and packed.
        var grey = Png.Read(MakePng(5, 6, 4, 8, (x, y) => new[] { (byte)(x * 50 + y), (byte)255 }));
        Assert.Equal((150 + 5, 150 + 5, 150 + 5), grey.At(3, 5));
        var bits = Png.Read(MakePng(9, 2, 0, 1, (x, y) => new[] { (byte)((x + y) % 2) }));
        Assert.Equal((0, 0, 0), bits.At(0, 0));
        Assert.Equal((255, 255, 255), bits.At(1, 0));
        Assert.Equal((255, 255, 255), bits.At(8, 1));

        Assert.Throws<DecodeException>(() => Png.Read(new byte[] { 1, 2, 3 }));
        // Cut short.
        byte[] whole = MakePng(4, 4, 2, 8, (x, y) => new byte[] { 1, 2, 3 });
        Assert.Throws<DecodeException>(() => Png.Read(whole[..40]));
        // Asking outside the image matches nothing.
        Assert.False(bits.IsDark(-1, 0));
        Assert.False(bits.Is(9, 0, new Rgb(0, 0, 0)));
    }

    [Fact]
    public void AModIsLoadedFromItsFolderUnlessSwitchedOff()
    {
        // The mod as shipped: its assembly and mod.json in a folder of a Mods folder.
        string mods = Path.Combine(root, "Mods");
        void Ship(string name, string id)
        {
            string dir = Path.Combine(mods, name);
            Directory.CreateDirectory(dir);
            File.Copy(typeof(ChartImageMod).Assembly.Location, Path.Combine(dir, "OurTaikoEdit.Mods.ChartImage.dll"));
            File.WriteAllText(Path.Combine(dir, "mod.json"), File.ReadAllText(Fixture("mod.json")).Replace("\"chart-image\"", $"\"{id}\""));
        }
        Ship("A", "image-a");
        Ship("B", "image-b");
        Directory.CreateDirectory(Path.Combine(mods, "Broken"));
        File.WriteAllText(Path.Combine(mods, "Broken", "mod.json"), """{ "id": "broken", "name": "Broken", "assembly": "Nowhere.dll" }""");
        Directory.CreateDirectory(Path.Combine(mods, "NotAMod"));

        ModRegistry.LoadFolder(mods, new[] { "image-b" });
        ModRegistry.LoadFolder(Path.Combine(root, "no such folder"));

        // The manifest names it; what the mod calls itself is what is loaded and shown.
        var a = Assert.Single(ModRegistry.All, m => m.Folder == Path.Combine(mods, "A"));
        Assert.True(a.Loaded, a.Error);
        Assert.Equal("0.1.0", a.Version);
        Assert.Equal("Chart image import", a.Name);
        // Its code is the mod's own copy, speaking the app's interfaces.
        Assert.NotSame(typeof(ChartImageMod).Assembly, a.Mod!.GetType().Assembly);
        Assert.NotNull(ChartImporters.Find("chart-image"));

        var b = Assert.Single(ModRegistry.All, m => m.Id == "image-b");
        Assert.False(b.Enabled);
        Assert.False(b.Loaded);
        Assert.Equal("Chart image import", b.Name);

        var broken = Assert.Single(ModRegistry.All, m => m.Id == "broken");
        Assert.False(broken.Loaded);
        Assert.Contains("Nowhere.dll", broken.Error);
        Assert.DoesNotContain(ModRegistry.All, m => m.Id == "NotAMod");
    }
}
