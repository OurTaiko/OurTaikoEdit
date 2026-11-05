using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using CharTaiko.App.Controls;
using CharTaiko.Audio;
using CharTaiko.App.ViewModels;
using CharTaiko.App.Views;
using CharTaiko.Core.Languages;
using CharTaiko.Core.Projects;
using CharTaiko.Tja;
using OurTaiko;

namespace CharTaiko.UiTests;

// The scenes rendered headless with Skia. Screenshots go to $CHARTAIKO_SCREENSHOTS, or to
// screenshots/ next to the test assembly, for a person to look at.
public class SceneTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "chartaiko-ui-" + Guid.NewGuid().ToString("N"));
    static string Shots => Environment.GetEnvironmentVariable("CHARTAIKO_SCREENSHOTS") is { Length: > 0 } dir ? dir : Path.Combine(AppContext.BaseDirectory, "screenshots");

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
        var vm = new MainWindowViewModel(registry, new DialogService(window));
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
        var editor = new EditorViewModel(project, new DialogService(window), () => { });
        vm.Scene = editor;
        Dispatcher.UIThread.RunJobs();
        return editor;
    }

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
}
