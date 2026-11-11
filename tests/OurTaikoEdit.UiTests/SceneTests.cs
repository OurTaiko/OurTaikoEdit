using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using OurTaikoEdit.App.Controls;
using OurTaikoEdit.Audio;
using OurTaikoEdit.App.ViewModels;
using OurTaikoEdit.App.Views;
using OurTaikoEdit.Core.Languages;
using OurTaikoEdit.Core.Localization;
using OurTaikoEdit.Core.Projects;
using OurTaikoEdit.Core.Settings;
using OurTaikoEdit.Tja;
using OurTaiko;

namespace OurTaikoEdit.UiTests;

// The scenes rendered headless with Skia. Screenshots go to $OURTAIKOEDIT_SCREENSHOTS, or to
// screenshots/ next to the test assembly, for a person to look at.
public class SceneTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "ourtaikoedit-ui-" + Guid.NewGuid().ToString("N"));
    static string Shots => Environment.GetEnvironmentVariable("OURTAIKOEDIT_SCREENSHOTS") is { Length: > 0 } dir ? dir : Path.Combine(AppContext.BaseDirectory, "screenshots");

    public SceneTests()
    {
        ChartLanguages.Register(new TjaLanguage());
        // No sound from the tests: the song is decoded and drawn, never played.
        EditorViewModel.OpenPlayer = _ => null;
        Directory.CreateDirectory(root);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (IOException) { }
    }

    (MainWindow Window, MainWindowViewModel Vm, ProjectRegistry Registry) Open()
    {
        var registry = new ProjectRegistry(Path.Combine(root, "data"));
        var window = new MainWindow { Width = 1440, Height = 900 };
        var vm = new MainWindowViewModel(registry, new DialogService(window), new AppSettings { Language = "en" });
        window.DataContext = vm;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, vm, registry);
    }

    ChartProject SampleProject(ProjectRegistry registry)
    {
        string folder = Path.Combine(root, "Sample");
        Directory.CreateDirectory(folder);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Sample.tja"), Path.Combine(folder, "Sample.tja"));
        return registry.OpenFolder(folder);
    }

    static void Shot(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        Directory.CreateDirectory(Shots);
        window.CaptureRenderedFrame()?.Save(Path.Combine(Shots, name + ".png"));
    }

    static T Find<T>(Window window) where T : Visual => window.GetVisualDescendants().OfType<T>().First();

    [AvaloniaFact]
    public void TheProjectsSceneListsTrackedProjects()
    {
        var (window, vm, registry) = Open();
        SampleProject(registry);
        registry.CreateProject(root, "Brand New Song", ChartLanguages.Find("tja")!, null);
        registry.Track(Path.Combine(root, "Gone"));
        vm.Scene = new ProjectsViewModel(registry, new DialogService(window), _ => { });
        var projects = (ProjectsViewModel)vm.Scene;
        Assert.Equal(3, projects.Projects.Count);
        Assert.Contains(projects.Projects, p => p.HasProblem);
        Assert.Contains(projects.Projects, p => p.Summary.Contains("Oni ★9 · Hard ★6"));
        Shot(window, "projects");
    }

    EditorViewModel OpenEditor(MainWindow window, MainWindowViewModel vm, ProjectRegistry registry)
    {
        var project = SampleProject(registry);
        var editor = new EditorViewModel(project, new DialogService(window), () => { }, vm.Settings);
        vm.Scene = editor;
        Dispatcher.UIThread.RunJobs();
        return editor;
    }

    [AvaloniaFact]
    public void TheInterfaceFollowsTheLanguageSetting()
    {
        var (window, vm, registry) = Open();
        var editor = OpenEditor(window, vm, registry);
        try
        {
            Assert.Contains(Texts(window), t => t == "Save");
            editor.Route = BranchRoute.Expert;
            editor.MessageLanguage = "ja";
            Choose(vm.Settings, s => s.Language = "zh");
            // The lists keep what is chosen in them.
            Assert.Equal((BranchRoute.Expert, "ja"), (editor.Route, editor.MessageLanguage));
            editor.MessageLanguage = "ui";
            Assert.Equal("zh", Loc.Instance.Language);
            // Words in the markup follow at once; those made in code are made again.
            Assert.Contains(Texts(window), t => t == "保存");
            Assert.DoesNotContain(Texts(window), t => t == "Save");
            Assert.Equal("咚", editor.Tools[1].Label);
            Assert.StartsWith("第 ", editor.Caret);
            Assert.True(editor.Problems == "没有问题" || editor.Problems.Contains("个"), editor.Problems);
            Shot(window, "editor-zh");

            var dialog = new SettingsDialog(vm.Settings) { Width = 560 };
            dialog.Show();
            Dispatcher.UIThread.RunJobs();
            Shot(dialog, "settings-zh");
            dialog.Close();

            vm.Scene = new ProjectsViewModel(registry, new DialogService(window), _ => { }, vm.Settings);
            Shot(window, "projects-zh");

            // The chart's messages follow the interface until a language is chosen for them.
            Assert.Equal("zh", editor.Language.MessageLanguage);
            vm.Scene = editor;
            Dispatcher.UIThread.RunJobs();
            Choose(vm.Settings, s => { s.Language = "ja"; s.MessageLanguage = "en"; });
            Assert.Equal("en", editor.Language.MessageLanguage);
            Assert.Equal("en", editor.MessageLanguage);
            Assert.Contains(Texts(window), t => t == "保存");
            Assert.Equal("ドン", editor.Tools[1].Label);
            Shot(window, "editor-ja");
        }
        finally
        {
            Choose(vm.Settings, s => { s.Language = "en"; s.MessageLanguage = "ui"; });
        }
    }

    [AvaloniaFact]
    public void AChartInAnotherEncodingIsReopenedAndSavedAsUtf8()
    {
        var (window, vm, registry) = Open();
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        string folder = Path.Combine(root, "Gb");
        Directory.CreateDirectory(folder);
        string text = "TITLE:千本樱\nBPM:120\nCOURSE:Oni\nLEVEL:5\n#START\n1,\n#END\n";
        File.WriteAllBytes(Path.Combine(folder, "song.tja"), System.Text.Encoding.GetEncoding(54936).GetBytes(text));
        var editor = new EditorViewModel(registry.OpenFolder(folder), new DialogService(window), () => { }, vm.Settings);
        vm.Scene = editor;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("SHIFT_JIS → UTF-8", editor.Encoding);
        Assert.True(editor.IsDirty);

        _ = editor.ReopenWith("gb18030");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(text, Find<TextEditor>(window).Text);
        Assert.Equal("GB18030 → UTF-8", editor.Encoding);
        Assert.Equal("千本樱", editor.Analysis.Title);
        Shot(window, "editor-gb18030");

        _ = editor.Save();
        Dispatcher.UIThread.RunJobs();
        Assert.False(editor.IsDirty);
        Assert.Equal("UTF-8", editor.Encoding);
        Assert.Equal(text, File.ReadAllText(Path.Combine(folder, "song.tja")));
    }

    static void Choose(AppSettings settings, Action<AppSettings> change)
    {
        var edited = settings.Clone();
        change(edited);
        settings.Apply(edited);
        Dispatcher.UIThread.RunJobs();
    }

    static List<string> Texts(Window window) => window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();

    [AvaloniaFact]
    public void TheEditorShowsCodeLaneAndTimeline()
    {
        var (window, vm, registry) = Open();
        var editor = OpenEditor(window, vm, registry);
        Assert.NotNull(editor.Analysis.Chart);
        Assert.Equal(2, editor.Blocks.Count);
        Assert.True(editor.Analysis.HasBranches);
        // Into the go-go measures, with the caret on the line that plays there.
        var code = Find<TextEditor>(window);
        code.TextArea.Caret.Position = new TextViewPosition(17, 5);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(editor.Analysis.Measures[3].TimeAt(4 / 16.0), editor.Playhead, 3);
        Shot(window, "editor");

        editor.Route = BranchRoute.Master;
        double t = editor.Analysis.Chart!.Branches[0].Time + 0.3;
        editor.Seek(t);
        Assert.Equal(t, editor.Playhead, 3);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(t, editor.Playhead, 3);
        Shot(window, "editor-master");
    }

    [AvaloniaFact]
    public void TheCaretFollowsThePlayhead()
    {
        var (window, vm, registry) = Open();
        var editor = OpenEditor(window, vm, registry);
        var code = Find<TextEditor>(window);
        var caret = code.TextArea.Caret;

        // A seek between two slots: the caret goes to the earlier one, the playhead stays put.
        var m = editor.Analysis.Measures[1];
        var (a, b) = (m.Slots[4], m.Slots[5]);
        double t = (a.Time + b.Time) / 2;
        editor.Seek(t);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(t, editor.Playhead, 3);
        Assert.Equal((a.Source.Line, a.Source.Column), (caret.Line, caret.Column));

        // Moved by hand, the caret still takes the playhead with it, and stays where it was put.
        var other = editor.Analysis.Measures[2].Slots[3];
        caret.Position = new TextViewPosition(other.Source.Line, other.Source.Column + 1);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(editor.Analysis.SlotAt(other.Source.Line, other.Source.Column + 1)!.Time, editor.Playhead, 3);
        Assert.Equal((other.Source.Line, other.Source.Column + 1), (caret.Line, caret.Column));

        // Another difficulty from the picker: the caret goes into its chart at the same time, and
        // its line is the editor's top line (unless the editor cannot scroll that far).
        double at = editor.Playhead;
        foreach (int block in new[] { 1, 0 })
        {
            editor.BlockIndex = block;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(block, editor.BlockIndex);
            Assert.InRange(caret.Line, editor.Analysis.StartLine, editor.Analysis.EndLine);
            Assert.Equal(at, editor.Playhead, 3);
            var view = code.TextArea.TextView;
            int top = view.GetDocumentLineByVisualTop(code.VerticalOffset).LineNumber;
            bool atBottom = code.VerticalOffset + code.ViewportHeight >= code.ExtentHeight - 1;
            Assert.True(top == caret.Line || (atBottom && top < caret.Line), $"top line {top}, caret line {caret.Line}");
        }

        // The chosen difficulty clicked again: its #START at the top, the caret at the start of the
        // first measure, and the playhead there too.
        editor.Seek(editor.Analysis.Chart!.Duration / 2);
        Dispatcher.UIThread.RunJobs();
        var list = window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "BlockList");
        var tab = list.ContainerFromIndex(editor.BlockIndex)!;
        var press = tab.TranslatePoint(new Point(tab.Bounds.Width / 2, tab.Bounds.Height / 2), window)!.Value;
        window.MouseMove(press);
        window.MouseDown(press, MouseButton.Left);
        window.MouseUp(press, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, editor.BlockIndex);
        var first = editor.Analysis.Measures[0];
        Assert.Equal((first.Slots[0].Source.Line, first.Slots[0].Source.Column), (caret.Line, caret.Column));
        Assert.Equal(first.Time, editor.Playhead, 3);
        int startTop = code.TextArea.TextView.GetDocumentLineByVisualTop(code.VerticalOffset).LineNumber;
        Assert.Equal(editor.Analysis.StartLine, startTop);
        Assert.Equal("#START", code.Document.GetText(code.Document.GetLineByNumber(startTop)).Trim());
    }

    [AvaloniaFact]
    public void ClickingTheTimelineWritesTheNoteAndUndoTakesItBack()
    {
        var (window, vm, registry) = Open();
        var editor = OpenEditor(window, vm, registry);
        var code = Find<TextEditor>(window);
        var timeline = Find<TimelineView>(window);
        string before = code.Text;
        editor.Division = 16;
        editor.Tool = editor.Tools.First(t => t.Kind == NoteKind.Ka);
        editor.Seek(0);
        Dispatcher.UIThread.RunJobs();

        // The 2nd sixteenth of the first measure: one grid step after its bar line.
        var first = editor.Analysis.Measures[0];
        double x = TimeX(timeline, editor, first.TimeAt(1 / 16.0));
        var at = timeline.TranslatePoint(new Point(x, 24 + 20), window)!.Value;
        window.MouseMove(at);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(editor.Message);
        Assert.Contains("1200100010001000,", code.Text);
        Assert.Equal(NoteKind.Ka, editor.Analysis.Chart!.Notes[1].Kind);
        Shot(window, "editor-after-click");

        code.Undo();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(before, code.Text);
    }

    [AvaloniaFact]
    public void SelectingDraggingPastingAndATempoChangeOnTheTimeline()
    {
        var (window, vm, registry) = Open();
        var editor = OpenEditor(window, vm, registry);
        var code = Find<TextEditor>(window);
        var timeline = Find<TimelineView>(window);
        string original = code.Text;
        editor.Division = 16;
        editor.Tool = editor.Tools.First(t => t.Kind == null);
        editor.Seek(0);
        Dispatcher.UIThread.RunJobs();
        Point At(double time, double y = 44) => timeline.TranslatePoint(new Point(TimeX(timeline, editor, time), y), window)!.Value;

        // Drag the first don one sixteenth later.
        var first = editor.Analysis.Measures[0];
        var from = At(first.TimeAt(0));
        var to = At(first.TimeAt(1 / 16.0));
        window.MouseMove(from);
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(new Point((from.X + to.X) / 2, from.Y));
        window.MouseMove(to);
        window.MouseUp(to, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(editor.Message);
        Assert.StartsWith("0100100010001000,", code.Text.Split('\n')[12]);
        Assert.Single(editor.SelectedNotes);
        Assert.Equal(first.TimeAt(1 / 16.0), editor.SelectedNotes.Single().Time, 3);

        // A box over the rest of the first measure picks its other three dons.
        // (Row 0 runs from the ruler, 24 px, down a third of the rows: the box spans its middle.)
        double rowMiddle = 24 + (timeline.Bounds.Height - 48) / 6;
        var a = At(first.TimeAt(3 / 16.0), rowMiddle - 15);
        var b = At(first.TimeAt(15 / 16.0), rowMiddle + 15);
        window.MouseMove(a);
        window.MouseDown(a, MouseButton.Left);
        window.MouseMove(b);
        window.MouseUp(b, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(3, editor.SelectedNotes.Count);
        Shot(window, "timeline-selection");

        // Copy them and paste one measure on, over the second measure's notes.
        editor.CopySelection(BranchRoute.Normal);
        var second = editor.Analysis.Measures[1];
        editor.Paste(new GridPosition(second, 4, 16));
        Assert.Null(editor.Message);
        Assert.StartsWith("1010100010101000,", code.Text.Split('\n')[13]);
        Assert.Equal(3, editor.SelectedNotes.Count);

        // A command at measure 3's first slot goes right before its note line (after the
        // #GOGOSTART already there); then undo takes each step back.
        var task = editor.InsertEvent(new GridPosition(editor.Analysis.Measures[2], 0, 16), ChartEventKind.GogoEnd);
        Dispatcher.UIThread.RunJobs();
        Assert.True(task.IsCompleted);
        Assert.Contains("#GOGOSTART\n#GOGOEND\n3000400030004000,", code.Text);
        for (int i = 0; i < 3; i++) code.Undo();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(original, code.Text);
    }

    [AvaloniaFact]
    public void ARollsEndAndACommandAreDragged()
    {
        var (window, vm, registry) = Open();
        var editor = OpenEditor(window, vm, registry);
        var code = Find<TextEditor>(window);
        var timeline = Find<TimelineView>(window);
        editor.Division = 16;
        editor.Tool = editor.Tools.First(t => t.Kind == null);
        editor.Seek(8.0);
        Dispatcher.UIThread.RunJobs();
        double rowMiddle = 24 + (timeline.Bounds.Height - 48) / 6, commands = timeline.Bounds.Height - 12;
        Point At(double time, double y) => timeline.TranslatePoint(new Point(timeline.XOf(time), y), window)!.Value;
        void DragOn(Point from, Point to)
        {
            window.MouseMove(from);
            window.MouseDown(from, MouseButton.Left);
            window.MouseMove(new Point((from.X + to.X) / 2, from.Y));
            window.MouseMove(to);
            window.MouseUp(to, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        }

        // The drumroll of measure 5 (sixteenths of 0.1 s at 150 BPM) made two sixteenths longer.
        var roll = editor.Analysis.Chart!.Notes.First(n => n.IsDrumroll);
        DragOn(At(roll.EndTime, rowMiddle), At(roll.EndTime + 0.2, rowMiddle));
        Assert.Null(editor.Message);
        Assert.Contains("\n5000000000000800,\n", code.Text);

        // #GOGOEND (at measure 5's start) dragged four sixteenths on, into the measure's line.
        var gogoEnd = editor.Analysis.Commands.Single(c => c.Name == "#GOGOEND");
        DragOn(At(gogoEnd.Time, commands), At(gogoEnd.Time + 0.4, commands));
        Assert.Null(editor.Message);
        Assert.Contains("#GOGOSTART\n3000400030004000,\n1122112211221120,\n5000\n#GOGOEND\n000000000800,", code.Text);
        Assert.Equal(gogoEnd.Time + 0.4, editor.Analysis.Commands.Single(c => c.Name == "#GOGOEND").Time, 3);
        Shot(window, "timeline-commands");
    }

    [AvaloniaFact]
    public void TheSongIsDrawnUnderTheTimeline()
    {
        var (window, vm, registry) = Open();
        var project = SampleProject(registry);
        // Twenty seconds of beeps on the chart's beats (BPM 150 from 1.5 s).
        const int rate = 8000;
        var samples = new float[rate * 20];
        for (double beat = 1.5; beat < 20; beat += 0.4)
            for (int i = 0; i < rate / 20; i++)
            {
                int f = (int)(beat * rate) + i;
                if (f < samples.Length) samples[f] = (float)(Math.Sin(2 * Math.PI * 660 * i / rate) * (1 - i / (rate / 20.0)));
            }
        File.WriteAllBytes(Path.Combine(project.Folder, "song.wav"), new PcmAudio { SampleRate = rate, Channels = 1, Samples = samples }.ToWav());
        File.WriteAllText(project.ChartPath, File.ReadAllText(project.ChartPath).Replace("WAVE:\n", "WAVE:song.wav\n"));

        var editor = new EditorViewModel(project, new DialogService(window), () => { });
        vm.Scene = editor;
        var until = DateTime.UtcNow.AddSeconds(10);
        while (editor.Waveform == null && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
        Assert.NotNull(editor.Waveform);
        Assert.Contains("song.wav 0:20", editor.AudioStatus);
        editor.Seek(3);
        Shot(window, "editor-waveform");
    }

    // The timeline's x for a time, as it lays it out (header 78 px, 180 px a second from -0.5 s
    // after a seek to 0 centres nothing: the playhead is already in view).
    static double TimeX(TimelineView timeline, EditorViewModel editor, double time) => 78 + (time + 0.5) * 180;

    [AvaloniaFact]
    public void FindUndoAndRedoWorkFromTheWholeScene()
    {
        var (window, vm, registry) = Open();
        var editor = OpenEditor(window, vm, registry);
        var code = Find<TextEditor>(window);
        var timeline = Find<TimelineView>(window);
        // The code editor's own keys use the platform's command key; the scene takes either.
        var command = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
        void Press(Control focus, PhysicalKey key, RawInputModifiers modifiers)
        {
            focus.Focus();
            Dispatcher.UIThread.RunJobs();
            window.KeyPressQwerty(key, modifiers);
            Dispatcher.UIThread.RunJobs();
        }

        // Find from the timeline opens the code's search panel with the search box focused.
        var panel = code.SearchPanel;
        Assert.False(panel.IsOpened);
        Press(timeline, PhysicalKey.F, command);
        Assert.True(panel.IsOpened);
        var box = panel.GetVisualDescendants().OfType<TextBox>().First();
        Assert.True(box.IsFocused);
        window.KeyTextInput("#GOGO");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("#GOGO", panel.SearchPattern);
        Shot(window, "editor-find");
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.False(panel.IsOpened);

        // From the code, and from the toolbar: the selection is what to look for.
        Press(code.TextArea, PhysicalKey.F, command);
        Assert.True(panel.IsOpened);
        panel.Close();
        code.Select(code.Text.IndexOf("#SCROLL", StringComparison.Ordinal), 7);
        var find = window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "FindButton");
        find.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.True(panel.IsOpened);
        Assert.Equal("#SCROLL", panel.SearchPattern);

        // Undo with the focus on the timeline. A key the search box leaves alone is still not
        // the document's: redo there changes nothing.
        string original = code.Text;
        code.Document.Insert(0, "// a comment\n");
        Dispatcher.UIThread.RunJobs();
        string edited = code.Text;
        Press(timeline, PhysicalKey.Z, command);
        Assert.Equal(original, code.Text);
        Press(box, PhysicalKey.Z, command | RawInputModifiers.Shift);
        Assert.True(box.IsFocused);
        Assert.Equal(original, code.Text);
        panel.Close();

        // Redo and undo from the timeline, then in the code.
        Press(timeline, PhysicalKey.Z, command | RawInputModifiers.Shift);
        Assert.Equal(edited, code.Text);
        Press(timeline, PhysicalKey.Z, command);
        Assert.Equal(original, code.Text);
        Press(timeline, PhysicalKey.Y, command);
        Assert.Equal(edited, code.Text);
        Press(code.TextArea, PhysicalKey.Z, command);
        Assert.Equal(original, code.Text);
        Press(code.TextArea, PhysicalKey.Z, command | RawInputModifiers.Shift);
        Assert.Equal(edited, code.Text);
    }

    [AvaloniaFact]
    public void TheCaretInAnotherRouteOfABranchChoosesThatRoute()
    {
        var (window, vm, registry) = Open();
        // Two measures in each route: the measures are in the order they are written, so the
        // expert and master ones come after normal ones that play later.
        string folder = Path.Combine(root, "Branches");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "song.tja"),
            "TITLE:Branches\nBPM:120\nCOURSE:Oni\nLEVEL:5\n#START\n1000100010001000,\n#BRANCHSTART p,60,85\n#N\n1000200010002000,\n1000000010000000,\n"
            + "#E\n1010201010102010,\n1010101010101010,\n#M\n1111222211112222,\n2222111122221111,\n#BRANCHEND\n6000000000000008,\n#END\n");
        var editor = new EditorViewModel(registry.OpenFolder(folder), new DialogService(window), () => { }, vm.Settings);
        vm.Scene = editor;
        Dispatcher.UIThread.RunJobs();
        var code = Find<TextEditor>(window);
        var caret = code.TextArea.Caret;
        var lines = code.Text.Split('\n');
        int LineOf(string text) => Array.IndexOf(lines, text) + 1;
        // The box drawn on the slot at the playhead.
        SourceSpan Box()
        {
            var renderer = code.TextArea.TextView.BackgroundRenderers.Single(r => r.GetType().Name == "PlayheadRenderer");
            return (SourceSpan)renderer.GetType().GetProperty("Span")!.GetValue(renderer)!;
        }
        void Put(int line, int column)
        {
            caret.Position = new TextViewPosition(line, column);
            Dispatcher.UIThread.RunJobs();
        }
        int normal = LineOf("1000200010002000,"), expert = LineOf("1010201010102010,"), master = LineOf("1111222211112222,");
        Assert.Equal(BranchRoute.Normal, editor.Route);

        // Into the expert route: the route shown follows, and the box is where the caret is.
        Put(expert, 5);
        Assert.Equal(BranchRoute.Expert, editor.Route);
        Assert.Equal((expert, 5), (Box().Line, Box().Column));
        Assert.Equal((expert, 5), (caret.Line, caret.Column));
        Shot(window, "editor-caret-expert");

        // The master route, then a line of its own in it that holds no note (its marker's line
        // changes nothing), and back to the normal route.
        Put(master, 9);
        Assert.Equal(BranchRoute.Master, editor.Route);
        Assert.Equal((master, 9), (Box().Line, Box().Column));
        Put(LineOf("#E"), 1);
        Assert.Equal(BranchRoute.Master, editor.Route);
        Put(normal, 1);
        Assert.Equal(BranchRoute.Normal, editor.Route);
        Assert.Equal((normal, 1), (Box().Line, Box().Column));

        // Further into each route than its first measure.
        foreach (var (text, route, column) in new[] { ("2222111122221111,", BranchRoute.Master, 7), ("1010101010101010,", BranchRoute.Expert, 3), ("1000000010000000,", BranchRoute.Normal, 9) })
        {
            Put(LineOf(text), column);
            Assert.Equal(route, editor.Route);
            Assert.Equal((LineOf(text), column), (Box().Line, Box().Column));
        }

        // The part every route plays keeps the route that was chosen.
        Put(expert, 1);
        Put(LineOf("1000100010001000,"), 3);
        Assert.Equal(BranchRoute.Expert, editor.Route);
        Put(LineOf("6000000000000008,"), 1);
        Assert.Equal(BranchRoute.Expert, editor.Route);
    }
}
