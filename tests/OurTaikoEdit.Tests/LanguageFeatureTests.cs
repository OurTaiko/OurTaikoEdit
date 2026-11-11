using OurTaikoEdit.Core.Languages;
using OurTaikoEdit.Tja;
using OurTaiko;

namespace OurTaikoEdit.Tests;

// The editor features OurTaikoEdit takes from TJAParser, through IChartLanguage.
public class LanguageFeatureTests
{
    [Fact]
    public void TheLibrarysHelpFollowsTheMessageLanguage()
    {
        var tja = new TjaLanguage { MessageLanguage = "zh" };
        string text = "BPM:120\nCOURSE:Oni\nLEVEL:5\n#START\n1020,\n#END\n";
        Assert.StartsWith("**咚**", tja.Hover(text, 5, 1)!.Markdown);
        Assert.Equal("咚", tja.Complete(text, 5, 1).Items.Single(i => i.Text == "1").Description);
        Assert.StartsWith("2 个音符", tja.Summaries(text).Single().Text);
        tja.MessageLanguage = "ja";
        Assert.Equal("ここで小節を分ける", tja.Actions(tja.Analyze(text, 0), 5, 3).Single(a => !a.IsQuickFix && a.Title.Contains("分ける")).Title);
    }

    const string Text = "TITLE:X\nBPM:120\nCOURSE:Oni\nLEVEL:5\n#START\n1000,\n#BPMCHANGE 180\n10201020,\n#END\n";

    static IChartLanguage Tja() => new TjaLanguage();

    [Fact]
    public void HoverExplainsACommand()
    {
        var hover = Tja().Hover(Text, 7, 3);
        Assert.NotNull(hover);
        Assert.Contains("BPMCHANGE", hover!.Markdown);
        Assert.Equal(7, hover.Span.Line);
    }

    [Fact]
    public void SignatureHelpWhileWritingArguments()
    {
        var sig = Tja().Signature(Text, 7, 13);
        Assert.NotNull(sig);
        Assert.Contains("#BPMCHANGE", sig!.Label);
        Assert.NotEmpty(sig.Parameters);
    }

    [Fact]
    public void TheBlockFolds()
        => Assert.Contains(Tja().Folds(Text), f => f.StartLine == 5 && f.EndLine >= 8);

    [Fact]
    public void MeasuresGetHints()
    {
        var hints = Tja().Hints(Text, 1, 100);
        Assert.Contains(hints, h => h.Line == 6 && h.Label.StartsWith("#1"));
        Assert.Contains(hints, h => h.Line == 8 && h.Label.Contains("180"));
    }

    [Fact]
    public void AMeasureSplitsAtTheCaret()
    {
        var tja = Tja();
        var analysis = tja.Analyze(Text, 0);
        var split = Assert.Single(tja.Actions(analysis, 8, 5), a => a.Title.Contains("Split", StringComparison.OrdinalIgnoreCase));
        string after = TextEdits.Apply(Text, split.Edits);
        Assert.Contains("1020,", after);
        // Splitting adds a bar line (so the hash changes) but moves no note.
        Assert.Equal(TjaParser.Parse(Text).Notes.Select(n => (n.Kind, n.Time)), TjaParser.Parse(after).Notes.Select(n => (n.Kind, n.Time)));
        Assert.Equal(TjaParser.Parse(Text).Bars.Count + 1, TjaParser.Parse(after).Bars.Count);
    }

    [Fact]
    public void AQuickFixMendsAMissingEnd()
    {
        var tja = Tja();
        string text = "BPM:120\nCOURSE:Oni\nLEVEL:5\n#START\n1000,\n";
        var analysis = tja.Analyze(text, 0);
        var line = analysis.Diagnostics.First(d => d.Tag != null).Source.Line;
        var fixes = tja.Actions(analysis, line, 1).Where(a => a.IsQuickFix).ToList();
        Assert.NotEmpty(fixes);
        Assert.Contains("#END", TextEdits.Apply(text, fixes[0].Edits));
    }

    [Fact]
    public void FormattingKeepsTheHash()
    {
        string messy = "bpm:120\ncourse:Oni\nlevel:5\n#start\n1000,\n#end\n";
        var format = Tja().Format(messy);
        Assert.NotNull(format);
        string formatted = TextEdits.Apply(messy, format!.Edits);
        Assert.Equal(ChartHash.OfFile(messy).Single().Hash, ChartHash.OfFile(formatted).Single().Hash);
    }

    [Fact]
    public void EachBlockIsSummed()
        => Assert.Contains("notes", Assert.Single(Tja().Summaries(Text)).Text);

    [Fact]
    public void MessagesComeInChinese()
    {
        var tja = Tja();
        tja.MessageLanguage = "zh";
        var d = tja.Analyze("BPM:120\nCOURSE:Oni\nLEVEL:5\n#START\n1000,\n#SCROLL x\n#END\n", 0).Diagnostics;
        Assert.Contains(d, x => x.Message.Any(c => c > 0x4E00));
    }
}
