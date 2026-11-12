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
using OurTaikoEdit.Core.Importing;
using OurTaikoEdit.Core.Localization;
using OurTaikoEdit.Core.Mods;
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
        // The mods the app ships with, from the folder the build lays them out in next to the app.
        ModRegistry.LoadFolder(ModRegistry.ShippedFolder);
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
        // A card: the difficulties as chips, and the chart's title where it says more than the name.
        var sample = projects.Projects.Single(p => p.Name == "Sample");
        Assert.Equal(new[] { "Oni ★9", "Hard ★6" }, sample.Blocks.Select(b => b.Name));
        Assert.Equal("OurTaikoEdit Sample", sample.Detail);
        Assert.False(projects.Projects.Single(p => p.Name == "Brand New Song").HasDetail);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(Texts(window), t => t == "Hard ★6");
        Shot(window, "projects");
    }

    [AvaloniaFact]
    public void TheProjectsSceneSaysWhenNothingIsTrackedAndANewProjectShowsItsFolder()
    {
        var (window, vm, _) = Open();
        Assert.True(Assert.IsType<ProjectsViewModel>(vm.Scene).IsEmpty);
        Assert.Contains(Texts(window), t => t == "No projects yet");
        Shot(window, "projects-empty");

        // The folder a new project would make is named once there is a name for it.
        var dialog = new NewProjectDialog(new DialogService(window), Path.Combine(root, "Charts"));
        dialog.Show();
        Dispatcher.UIThread.RunJobs();
        var folder = dialog.FindControl<TextBlock>("FolderText")!;
        Assert.False(folder.IsVisible);
        dialog.FindControl<TextBox>("NameBox")!.Text = "Song";
        Dispatcher.UIThread.RunJobs();
        Assert.True(folder.IsVisible);
        Assert.Contains(Path.Combine(root, "Charts", "Song"), folder.Text);
        Shot(dialog, "new-project");
        dialog.Close();

        // The question asked before unsaved changes are left behind.
        _ = new DialogService(window).AskSaveAsync("Sample");
        Dispatcher.UIThread.RunJobs();
        var ask = window.OwnedWindows.Last();
        Assert.Equal(new[] { "Save", "Don't Save", "Cancel" }, ask.GetVisualDescendants().OfType<Button>().Select(b => b.Content as string));
        Shot(ask, "dialog-save");
        ask.Close();
    }

    [AvaloniaFact]
    public void TheEditorListsProblemsAndItsBarsFitANarrowWindow()
    {
        var (window, vm, registry) = Open();
        var editor = OpenEditor(window, vm, registry);
        var code = Find<TextEditor>(window);
        var problems = window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "ProblemList");
        Assert.False(problems.IsVisible);
        // The route box is the lane's, for a chart that branches.
        var route = window.GetVisualDescendants().OfType<ComboBox>().First(c => ReferenceEquals(c.ItemsSource, editor.Routes));
        Assert.True(route.IsEffectivelyVisible);

        code.Document.Insert(code.Document.GetLineByNumber(14).Offset, "#BPMCHANGE abc\n12x4,\n#NOPE\n");
        code.TextArea.Caret.Line = 16;
        Dispatcher.UIThread.RunJobs();
        Assert.True(problems.IsVisible);
        Assert.Equal(3, editor.Diagnostics.Count);
        Shot(window, "editor-problems");

        // At the window's smallest, what the bars hold on their right stays inside it.
        window.Width = 960;
        window.Height = 600;
        Dispatcher.UIThread.RunJobs();
        var grid = window.GetVisualDescendants().OfType<ComboBox>().First(c => ReferenceEquals(c.ItemsSource, editor.Divisions));
        foreach (Control control in new Control[] { grid, route, window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "EncodingButton") })
        {
            var right = control.TranslatePoint(new Point(control.Bounds.Width, 0), window)!.Value;
            Assert.InRange(right.X, 0, 960);
        }
        Shot(window, "editor-narrow");

        // A chart with one route has no route to choose.
        var plain = new EditorViewModel(registry.CreateProject(root, "Plain", ChartLanguages.Find("tja")!, null), new DialogService(window), () => { }, vm.Settings);
        vm.Scene = plain;
        Dispatcher.UIThread.RunJobs();
        Assert.False(plain.Analysis.HasBranches);
        Assert.False(window.GetVisualDescendants().OfType<ComboBox>().First(c => ReferenceEquals(c.ItemsSource, plain.Routes)).IsEffectivelyVisible);
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

            var dialog = new SettingsDialog(vm.Settings);
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
            // (Compared as offsets: looking the top line up by the offset can land a line early.)
            double caretTop = code.TextArea.TextView.GetVisualPosition(new TextViewPosition(caret.Line, 1), AvaloniaEdit.Rendering.VisualYPosition.LineTop).Y;
            bool atBottom = code.VerticalOffset + code.ViewportHeight >= code.ExtentHeight - 1;
            Assert.True(Math.Abs(caretTop - code.VerticalOffset) < 1 || (atBottom && code.VerticalOffset < caretTop), $"scrolled to {code.VerticalOffset}, caret line {caret.Line} at {caretTop}");
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

    [AvaloniaFact]
    public void ARouteOfEmptyMeasuresIsFollowedAtItsCommas()
    {
        var (window, vm, registry) = Open();
        // The expert route has nothing written in it: two measures that are only their commas.
        string folder = Path.Combine(root, "EmptyRoute");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "song.tja"),
            "TITLE:Empty route\nBPM:120\nCOURSE:Oni\nLEVEL:5\n#START\n1000100010001000,\n#BRANCHSTART p,60,85\n#N\n1000200010002000,\n1000000010000000,\n"
            + "#E\n,\n,\n#M\n1111222211112222,\n2222111122221111,\n#BRANCHEND\n6000000000000008,\n#END\n");
        var editor = new EditorViewModel(registry.OpenFolder(folder), new DialogService(window), () => { }, vm.Settings);
        vm.Scene = editor;
        Dispatcher.UIThread.RunJobs();
        var code = Find<TextEditor>(window);
        var caret = code.TextArea.Caret;
        SourceSpan Box()
        {
            var renderer = code.TextArea.TextView.BackgroundRenderers.Single(r => r.GetType().Name == "PlayheadRenderer");
            return (SourceSpan)renderer.GetType().GetProperty("Span")!.GetValue(renderer)!;
        }
        // Lines 12 and 13 are the expert route's measures; each is two seconds long, from 2 s.
        var empty = editor.Analysis.Measures.Where(m => m.BranchId == 0 && m.Route == BranchRoute.Expert).ToList();
        Assert.Equal(new[] { (12, 2.0), (13, 4.0) }, empty.Select(m => (m.Comma.Line, m.Time)));

        // The playhead taken into the branch on the expert route: the caret and the box go to
        // the comma of the measure playing, not back to the last note before the branch.
        editor.Route = BranchRoute.Expert;
        editor.Seek(4.7);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((13, 1), (caret.Line, caret.Column));
        Assert.Equal((13, 1), (Box().Line, Box().Column));
        Assert.Equal(4.7, editor.Playhead, 3);
        editor.Seek(2.5);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((12, 1), (caret.Line, caret.Column));
        Shot(window, "editor-empty-route");

        // The caret put on an empty measure from another route: that route, at that measure.
        editor.Route = BranchRoute.Normal;
        editor.Seek(0);
        Dispatcher.UIThread.RunJobs();
        caret.Position = new TextViewPosition(13, 1);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(BranchRoute.Expert, editor.Route);
        Assert.Equal(4.0, editor.Playhead, 3);
        Assert.Equal((13, 1), (Box().Line, Box().Column));
        Assert.Equal((13, 1), (caret.Line, caret.Column));
    }

    // Answers every prompt with one line.
    sealed class Answers(string answer) : IDialogService
    {
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
        public Task<string?> PickFileAsync(string title, string filterName, params string[] patterns) => Task.FromResult<string?>(null);
        public Task<NewProjectRequest?> NewProjectAsync(string location) => Task.FromResult<NewProjectRequest?>(null);
        public Task MessageAsync(string title, string message) => Task.CompletedTask;
        public Task<bool> ConfirmAsync(string title, string message, string confirm) => Task.FromResult(true);
        public Task<string?> PromptAsync(string title, string message, string value) => Task.FromResult<string?>(answer);
        public Task<string?> BranchConditionAsync(string title, IChartLanguage language, string written) => Task.FromResult<string?>(answer);
        public Task<SaveChoice> AskSaveAsync(string name) => Task.FromResult(SaveChoice.Discard);
        public Task<bool> SettingsAsync(AppSettings settings) => Task.FromResult(false);
    }

    [AvaloniaFact]
    public void TheTimelineFlagsWhereABranchStartsAndWhereItIsJudged()
    {
        var (window, vm, registry) = Open();
        var editor = new EditorViewModel(SampleProject(registry), new Answers("p,50,80"), () => { }, vm.Settings);
        vm.Scene = editor;
        Dispatcher.UIThread.RunJobs();
        var code = Find<TextEditor>(window);
        var timeline = Find<TimelineView>(window);
        var branch = Assert.Single(editor.Analysis.Branches);
        Assert.Equal("p,60,85", branch.Start.Value);
        // Judged at the bar line before it.
        Assert.Equal(editor.Analysis.Measures.Last(m => m.BranchId < 0 && m.Time < branch.Time).Time, branch.DecisionTime, 3);
        editor.Seek(branch.DecisionTime - 0.4);
        Dispatcher.UIThread.RunJobs();
        Shot(window, "timeline-branch");

        // The flags are at the top of the rows, under the ruler: the start's, and a level lower
        // the one where the branch is judged. Either shows the #BRANCHSTART in the code.
        Point Flag(double time, int level) => timeline.TranslatePoint(new Point(timeline.XOf(time) + 8, 24 + 3 + level * 18 + 8), window)!.Value;
        void Click(Point at, int count = 1)
        {
            window.MouseMove(at);
            for (int i = 0; i < count; i++)
            {
                window.MouseDown(at, MouseButton.Left);
                window.MouseUp(at, MouseButton.Left);
            }
            Dispatcher.UIThread.RunJobs();
        }
        int line = Array.IndexOf(code.Text.Split('\n'), "#BRANCHSTART p,60,85") + 1;
        string before = code.Text;
        Click(Flag(branch.DecisionTime, 1));
        Assert.Equal(line, code.TextArea.Caret.Line);
        code.TextArea.Caret.Position = new TextViewPosition(1, 1);
        Click(Flag(branch.Time, 0));
        Assert.Equal(line, code.TextArea.Caret.Line);
        Assert.Equal(before, code.Text);

        // Twice: its condition is asked for and written.
        Click(Flag(branch.Time, 0), 2);
        Assert.Contains("\n#BRANCHSTART p,50,80\n", code.Text);
        Assert.Equal("p,50,80", Assert.Single(editor.Analysis.Branches).Start.Value);
        code.Undo();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(before, code.Text);
    }

    [AvaloniaFact]
    public void ABranchIsRemovedFromItsFlagAndUndoBringsItBack()
    {
        var (window, vm, registry) = Open();
        var editor = OpenEditor(window, vm, registry);
        var code = Find<TextEditor>(window);
        var timeline = Find<TimelineView>(window);
        var branch = Assert.Single(editor.Analysis.Branches);
        editor.Seek(branch.DecisionTime - 0.4);
        Dispatcher.UIThread.RunJobs();
        string before = code.Text;
        var normal = editor.Analysis.Chart!.Notes.Where(n => n.BranchId < 0 || n.Route == BranchRoute.Normal).Select(n => (n.Kind, Math.Round(n.Time, 3))).ToList();

        // The flag's menu, and its last entry.
        var flag = timeline.TranslatePoint(new Point(timeline.XOf(branch.Time) + 8, 24 + 3 + 8), window)!.Value;
        // (The menu opens in a popup of its own: it is caught as it opens.)
        ContextMenu? menu = null;
        using (ContextMenu.OpenedEvent.AddClassHandler<ContextMenu>((opened, _) => menu = opened))
        {
            window.MouseMove(flag);
            window.MouseDown(flag, MouseButton.Right);
            window.MouseUp(flag, MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.NotNull(menu);
        var entries = menu!.Items.OfType<MenuItem>().Select(m => m.Header as string).ToList();
        Assert.Equal(new[] { "Edit #BRANCHSTART p,60,85…", "Show in code", "Remove the branch (keep Normal)" }, entries);
        menu.Items.OfType<MenuItem>().Last().RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        menu.Close();
        Dispatcher.UIThread.RunJobs();

        // The normal route's measure is what every route plays; the other routes are gone.
        Assert.Null(editor.Message);
        Assert.Contains("1110222011102220,\n1000200010002000,\n#SCROLL 1\n", code.Text);
        Assert.DoesNotContain("#BRANCH", code.Text);
        Assert.DoesNotContain("1111222211112222", code.Text);
        Assert.Empty(editor.Analysis.Branches);
        Assert.False(editor.Analysis.HasBranches);
        Assert.Equal(normal, editor.Analysis.Chart!.Notes.Select(n => (n.Kind, Math.Round(n.Time, 3))));
        Shot(window, "timeline-branch-removed");

        // One step back, and one forward again. (The chart is read again a moment after the text
        // changes, or at once when the caret moves.)
        void Read(int line)
        {
            Dispatcher.UIThread.RunJobs();
            code.TextArea.Caret.Position = new TextViewPosition(line, 1);
            Dispatcher.UIThread.RunJobs();
        }
        code.Undo();
        Read(13);
        Assert.Equal(before, code.Text);
        Assert.Equal("p,60,85", Assert.Single(editor.Analysis.Branches).Start.Value);
        code.Redo();
        Read(14);
        Assert.Empty(editor.Analysis.Branches);
        code.Undo();
        Read(13);
        Assert.Equal(before, code.Text);
        Assert.Single(editor.Analysis.Branches);
    }

    [AvaloniaFact]
    public void TheBranchDialogChoosesAConditionAndTwoThresholds()
    {
        var (window, vm, _) = Open();
        var tja = new TjaLanguage();
        var dialog = new BranchDialog("Branch condition", tja, "R,1,2,l");
        var result = dialog.ShowDialog<string?>(window);
        Dispatcher.UIThread.RunJobs();
        var kind = dialog.FindControl<ComboBox>("KindBox")!;
        var info = dialog.FindControl<TextBlock>("KindInfo")!;
        var big = dialog.FindControl<CheckBox>("BigOnlyBox")!;
        var less = dialog.FindControl<CheckBox>("LessBox")!;
        var expert = dialog.FindControl<TextBox>("ExpertBox")!;
        var master = dialog.FindControl<TextBox>("MasterBox")!;
        var written = dialog.FindControl<TextBlock>("WrittenText")!;
        var error = dialog.FindControl<TextBlock>("ErrorText")!;
        void Ok(Window w) => w.FindControl<Button>("OkButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        // What was written, taken apart: the kind with what it counts, a box for each threshold.
        Assert.Equal(9, kind.ItemCount);
        Assert.Equal("r — Drumroll hits", kind.SelectedItem);
        Assert.Contains("drumrolls", info.Text);
        Assert.Equal(("1", "2"), (expert.Text, master.Text));
        Assert.True(big.IsVisible && big.IsChecked == true);
        Assert.Equal("Count big drumrolls only", big.Content);
        Assert.True(less.IsChecked);
        Assert.Equal("Written as: R,1,2,l", written.Text);
        Shot(dialog, "branch-dialog");

        // Another kind, and a threshold that is not a number: not accepted until it is one.
        kind.SelectedIndex = 0;
        less.IsChecked = false;
        expert.Text = "60";
        master.Text = "eighty";
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("accuracy", info.Text);
        Assert.Equal("Count big notes only", big.Content);
        Assert.Equal("Written as: P,60,eighty", written.Text);
        Ok(dialog);
        Dispatcher.UIThread.RunJobs();
        Assert.True(error.IsVisible);
        Assert.False(result.IsCompleted);
        master.Text = "85";
        big.IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        Assert.False(error.IsVisible);
        Ok(dialog);
        Dispatcher.UIThread.RunJobs();
        Assert.True(result.IsCompleted);
        Assert.Equal("p,60,85", result.Result);

        // A new branch starts with no thresholds; the score cannot be asked for big notes only.
        try
        {
            Choose(vm.Settings, s => s.Language = "zh");
            var fresh = new BranchDialog(Loc.T("NewBranchTitle"), tja, "");
            var cancelled = fresh.ShowDialog<string?>(window);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("p — 精度", fresh.FindControl<ComboBox>("KindBox")!.SelectedItem);
            Assert.Equal("", fresh.FindControl<TextBox>("ExpertBox")!.Text);
            Shot(fresh, "branch-dialog-zh");
            Ok(fresh);
            Dispatcher.UIThread.RunJobs();
            Assert.True(fresh.FindControl<TextBlock>("ErrorText")!.IsVisible);
            fresh.FindControl<ComboBox>("KindBox")!.SelectedIndex = 8;
            Dispatcher.UIThread.RunJobs();
            Assert.False(fresh.FindControl<CheckBox>("BigOnlyBox")!.IsVisible);
            fresh.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.Null(cancelled.Result);
        }
        finally
        {
            Choose(vm.Settings, s => s.Language = "en");
        }
    }

    [AvaloniaFact]
    public void ANewBranchIsMadeFromTheMenusAndUndone()
    {
        var (window, vm, registry) = Open();
        var editor = new EditorViewModel(SampleProject(registry), new Answers("p,50,80"), () => { }, vm.Settings);
        vm.Scene = editor;
        Dispatcher.UIThread.RunJobs();
        var code = Find<TextEditor>(window);
        var timeline = Find<TimelineView>(window);
        editor.Tool = editor.Tools.First(t => t.Kind == null);
        // The first two measures in view: the timeline scrolls when the playhead jumps out of it.
        var second = editor.Analysis.Measures[1];
        editor.Seek(20);
        editor.Seek(second.Time + 0.4);
        Dispatcher.UIThread.RunJobs();
        string before = code.Text;
        double rowMiddle = 24 + (timeline.Bounds.Height - 48) / 6, commands = timeline.Bounds.Height - 12;
        // The menu a right click opens at a time, on the normal row or the commands row.
        List<MenuItem> MenuAt(double time, double y)
        {
            ContextMenu? menu = null;
            var at = timeline.TranslatePoint(new Point(timeline.XOf(time), y), window)!.Value;
            using (ContextMenu.OpenedEvent.AddClassHandler<ContextMenu>((opened, _) => menu = opened))
            {
                window.MouseMove(at);
                window.MouseDown(at, MouseButton.Right);
                window.MouseUp(at, MouseButton.Right);
                Dispatcher.UIThread.RunJobs();
            }
            Assert.NotNull(menu);
            var items = menu!.Items.OfType<MenuItem>().ToList();
            menu.Close();
            Dispatcher.UIThread.RunJobs();
            return items;
        }
        MenuItem NewBranch(List<MenuItem> items) => items.Single(m => m.Header as string == "New branch at this measure…");

        // On the second measure's row, where it has no note: its condition is asked for, and the
        // measure is written for each route.
        var entry = NewBranch(MenuAt(second.TimeAt(14 / 16.0), rowMiddle));
        Assert.True(entry.IsEnabled);
        entry.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Null(editor.Message);
        Assert.Contains("1000100010001000,\n#BRANCHSTART p,50,80\n#N\n1010200010102000,\n#E\n1010200010102000,\n#M\n1010200010102000,\n#BRANCHEND\n#GOGOSTART\n", code.Text);
        Assert.Equal(2, editor.Analysis.Branches.Count);
        Assert.Equal(("p,50,80", second.Time), (editor.Analysis.Branches[0].Start.Value, editor.Analysis.Branches[0].Time));
        Shot(window, "timeline-new-branch");

        // Not inside a branch: there the entry cannot be chosen. The commands row has it too.
        Assert.False(NewBranch(MenuAt(second.TimeAt(14 / 16.0), rowMiddle)).IsEnabled);
        Assert.False(NewBranch(MenuAt(second.TimeAt(14 / 16.0), commands)).IsEnabled);
        Assert.True(NewBranch(MenuAt(editor.Analysis.Measures[0].TimeAt(14 / 16.0), commands)).IsEnabled);

        // One step back.
        code.Undo();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(before, code.Text);
    }

    [AvaloniaFact]
    public void ANewProjectIsReadFromAChartImageByTheShippedMod()
    {
        // The mod is there because the build put it in Mods/ChartImage next to the app.
        var mod = Assert.Single(ModRegistry.All, m => m.Id == "chart-image");
        Assert.True(mod.Loaded, mod.Error);
        Assert.StartsWith(ModRegistry.ShippedFolder, mod.Folder);
        Assert.NotNull(ChartImporters.Find("chart-image"));

        var (window, vm, registry) = Open();
        vm.Settings.ProjectsFolder = Path.Combine(root, "Charts");
        var projects = Assert.IsType<ProjectsViewModel>(vm.Scene);
        projects.NewProjectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var dialog = Assert.IsType<NewProjectDialog>(window.OwnedWindows.Last());
        var source = dialog.FindControl<ComboBox>("SourceBox")!;
        var filePanel = dialog.FindControl<StackPanel>("SourceFilePanel")!;
        var file = dialog.FindControl<TextBox>("SourceFileBox")!;
        var name = dialog.FindControl<TextBox>("NameBox")!;
        var type = dialog.FindControl<ComboBox>("TypeBox")!;
        var error = dialog.FindControl<TextBlock>("ErrorText")!;
        void Create() => dialog.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "Create")
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        // A chart starts empty unless something else is chosen; the image asks for its file and
        // offers the chart types it can be written as.
        Assert.True(dialog.FindControl<StackPanel>("SourcePanel")!.IsVisible);
        Assert.Equal(new[] { "An empty chart", "Chart image" }, source.Items.Cast<string>());
        Assert.Equal(0, source.SelectedIndex);
        Assert.False(filePanel.IsVisible);
        source.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        Assert.True(filePanel.IsVisible);
        Assert.Equal("Chart image (*.png)", dialog.FindControl<TextBlock>("SourceFileLabel")!.Text);
        Assert.Equal(new[] { "TJA" }, type.Items.Cast<string>());

        // No file, then a file that is no chart image: said in the dialog, which stays open.
        name.Text = "Matsuri";
        Create();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Choose the file to read.", error.Text);
        string photo = Path.Combine(root, "photo.png");
        File.WriteAllBytes(photo, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0 });
        file.Text = photo;
        Create();
        Dispatcher.UIThread.RunJobs();
        Assert.True(error.IsVisible);
        Assert.Contains("not a PNG", error.Text);
        Assert.Contains(dialog, window.OwnedWindows);
        Shot(dialog, "new-project-image-refused");

        // The image itself. Its file names a project that has no name yet.
        string image = Path.Combine(AppContext.BaseDirectory, "Fixtures", "wrapped_drumrolls.png");
        name.Text = "";
        file.Text = image;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("wrapped_drumrolls", name.Text);
        Assert.False(error.IsVisible);
        name.Text = "Matsuri";
        Dispatcher.UIThread.RunJobs();
        Shot(dialog, "new-project-image");
        Create();
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(dialog, window.OwnedWindows);

        // What was read and what an image does not say, before the editor opens on the chart.
        var report = window.OwnedWindows.Last();
        Assert.Contains(Texts(report), t => t == "Chart imported");
        Assert.Contains(Texts(report), t => t.Contains("107 measures") && t.Contains("OFFSET"));
        Shot(report, "new-project-image-report");
        report.Close();
        Dispatcher.UIThread.RunJobs();

        var editor = Assert.IsType<EditorViewModel>(vm.Scene);
        try
        {
            string text = File.ReadAllText(Path.Combine(root, "Charts", "Matsuri", "Matsuri.tja"));
            Assert.StartsWith("TITLE:Matsuri\n", text);
            Assert.Contains("\nBALLOON:6,10\n", text);
            Assert.Equal(107, editor.Analysis.Measures.Count);
            Assert.False(editor.Analysis.HasErrors);
            Assert.Single(registry.Projects);
            Shot(window, "editor-from-image");
        }
        finally { editor.Close(); }
    }

    [AvaloniaFact]
    public void TheSettingsListTheModsAndSwitchThemOff()
    {
        var (window, vm, _) = Open();
        var dialog = new SettingsDialog(vm.Settings);
        dialog.Show();
        Dispatcher.UIThread.RunJobs();
        var box = dialog.FindControl<StackPanel>("ModsPanel")!.GetVisualDescendants().OfType<CheckBox>().Single(b => (b.Content as string)!.StartsWith("Chart image import"));
        Assert.True(box.IsChecked);
        Assert.True(box.IsEnabled);
        Assert.Contains(Texts(dialog), t => t.Contains("picture of a chart") && t.Contains("comes with the app"));
        Shot(dialog, "settings-mods");

        // Unticked, the mod's id is in the settings: it is left alone at the next start.
        box.IsChecked = false;
        Assert.Equal(new[] { "chart-image" }, dialog.Edited().DisabledMods);
        box.IsChecked = true;
        Assert.Empty(dialog.Edited().DisabledMods);
        dialog.Close();
    }
}
