using System.Globalization;
using System.Text.RegularExpressions;
using OurTaikoEdit.Core.Localization;
using OurTaikoEdit.Core.Settings;

namespace OurTaikoEdit.Tests;

// The interface's words in three languages, and the settings file.
public class SettingsTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "ourtaikoedit-settings-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (IOException) { }
    }

    static IEnumerable<int> Placeholders(string s) => Regex.Matches(s, @"\{(\d+)\}").Select(m => int.Parse(m.Groups[1].Value)).Distinct().Order();

    [Fact]
    public void EveryWordIsInEveryLanguageWithTheSamePlaceholders()
    {
        foreach (var (key, w) in Strings.Table)
        {
            Assert.False(string.IsNullOrWhiteSpace(w.En), key);
            // English singulars ("…One") have no Chinese or Japanese: those languages have no plural.
            if (key.EndsWith("One") && Strings.Table.ContainsKey(key[..^3])) continue;
            Assert.False(string.IsNullOrWhiteSpace(w.Zh), key + " zh");
            Assert.False(string.IsNullOrWhiteSpace(w.Ja), key + " ja");
            Assert.Equal(Placeholders(w.En), Placeholders(w.Zh));
            Assert.Equal(Placeholders(w.En), Placeholders(w.Ja));
        }
    }

    // The keys the views ({l:T Key}) and the code (Loc.T / F / Plural with a literal key) ask for.
    [Fact]
    public void EveryKeyAskedForIsInTheTable()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "OurTaikoEdit.slnx"))) root = Path.GetDirectoryName(root) ?? throw new DirectoryNotFoundException("OurTaikoEdit.slnx");
        var asked = new List<(string Key, string File)>();
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            string pattern = Path.GetExtension(file) switch
            {
                ".axaml" => @"\{l:T\s+(\w+)\s*\}",
                // The literal alone, not a prefix such as "Tip" + name.
                ".cs" => @"Loc\.(?:T|F|Plural)\(\s*""(\w+)""\s*[,)]",
                _ => "",
            };
            if (pattern.Length == 0) continue;
            asked.AddRange(Regex.Matches(File.ReadAllText(file), pattern).Select(m => (m.Groups[1].Value, Path.GetFileName(file))));
        }
        Assert.True(asked.Count > 100, $"only {asked.Count} keys found");
        Assert.Empty(asked.Where(a => !Strings.Table.ContainsKey(a.Key)).Select(a => $"{a.Key} ({a.File})"));
    }

    [Fact]
    public void TheCsvIsReadAsWritten()
    {
        string csv = "﻿key,ja,en,zh\r\n"
            + "# Section,,,\r\n"
            + "\r\n"
            + "A,\"あ, い\",\"say \"\"hi\"\"\",\" 前后 \"\r\n"
            + "B,\"一\n二\",two lines,\n"
            + "C,c,c,c";
        var table = Strings.Read(new StringReader(csv.TrimStart('﻿')));
        Assert.Equal(3, table.Count);
        Assert.Equal(new Words("say \"hi\"", " 前后 ", "あ, い"), table["A"]);
        Assert.Equal(new Words("two lines", "", "一\n二"), table["B"]);
        Assert.Equal(new Words("c", "c", "c"), table["C"]);

        var twice = Assert.Throws<FormatException>(() => Strings.Read(new StringReader("key,en,zh,ja\nA,a,a,a\n\"B\",b,\"b\nb\",b\nA,a,a,a\n")));
        Assert.Contains("line 5", twice.Message);
        var short_ = Assert.Throws<FormatException>(() => Strings.Read(new StringReader("key,en,zh,ja\nA,a,a\n")));
        Assert.Contains("line 2", short_.Message);
        Assert.Throws<FormatException>(() => Strings.Read(new StringReader("key,en,zh\nA,a,a\n")));
        Assert.Throws<FormatException>(() => Strings.Read(new StringReader("key,en,zh,ja\nA,\"a,a,a\n")));
    }

    [Fact]
    public void WordsFollowTheLanguage()
    {
        var loc = Loc.Instance;
        string before = loc.Language;
        try
        {
            loc.Language = "en";
            Assert.Equal("1 error", Loc.Plural("Errors", 1));
            Assert.Equal("2 errors", Loc.Plural("Errors", 2));
            loc.Language = "zh";
            Assert.Equal("1 个错误", Loc.Plural("Errors", 1));
            Assert.Equal("项目", loc["Projects"]);
            loc.Language = "ja";
            Assert.Equal("エラー 1 件", Loc.Plural("Errors", 1));
            // Unknown keys show themselves; unknown languages are English.
            Assert.Equal("NoSuchKey", Loc.T("NoSuchKey"));
            loc.Language = "fr";
            Assert.Equal("en", loc.Language);
        }
        finally { loc.Language = before; }
    }

    [Fact]
    public void TheSystemLanguagePicksOneWeHave()
    {
        Assert.Equal("zh", Loc.Resolve("system", new CultureInfo("zh-CN")));
        Assert.Equal("zh", Loc.Resolve("system", new CultureInfo("zh-TW")));
        Assert.Equal("ja", Loc.Resolve("system", new CultureInfo("ja-JP")));
        Assert.Equal("en", Loc.Resolve("system", new CultureInfo("de-DE")));
        Assert.Equal("ja", Loc.Resolve("ja", new CultureInfo("zh-CN")));
    }

    [Fact]
    public void SettingsAreSavedAndReadBack()
    {
        var settings = AppSettings.Load(root);
        Assert.Equal("system", settings.Language);
        int changed = 0;
        settings.Changed += () => changed++;
        var edited = settings.Clone();
        edited.Language = "zh";
        edited.MessageLanguage = "ja";
        edited.FontSize = 18;
        edited.Division = 48;
        edited.LatencyMs = 40;
        edited.WordWrap = true;
        settings.Apply(edited);
        Assert.Equal(1, changed);

        var again = AppSettings.Load(root);
        Assert.Equal(("zh", "ja", 18.0, 48, 40.0, true), (again.Language, again.MessageLanguage, again.FontSize, again.Division, again.LatencyMs, again.WordWrap));
        Assert.Equal("zh", again.UiLanguage);
        Assert.Equal("ja", again.ChartMessageLanguage);
    }

    [Fact]
    public void MessagesFollowTheInterfaceUnlessChosen()
    {
        var s = new AppSettings { Language = "ja", MessageLanguage = "ui" };
        Assert.Equal("ja", s.ChartMessageLanguage);
        s.MessageLanguage = "en";
        Assert.Equal("en", s.ChartMessageLanguage);
    }

    [Fact]
    public void ABadFileGivesTheDefaults()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "settings.json"), "{ not json");
        var s = AppSettings.Load(root);
        Assert.Equal((14.0, 16, "system"), (s.FontSize, s.Division, s.Language));

        File.WriteAllText(Path.Combine(root, "settings.json"), """{ "language": "xx", "fontSize": 400, "division": 7, "volume": 3, "latencyMs": -5, "window": { "width": 10, "height": 10 } }""");
        s = AppSettings.Load(root);
        Assert.Equal(("system", 40.0, 16, 1.0, 0.0), (s.Language, s.FontSize, s.Division, s.Volume, s.LatencyMs));
        Assert.Null(s.Window);
    }
}
