using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using CharTaiko.App.Controls;
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

    // The timeline's x for a time, as it lays it out (header 78 px, 180 px a second from -0.5 s
    // after a seek to 0 centres nothing: the playhead is already in view).
    static double TimeX(TimelineView timeline, EditorViewModel editor, double time) => 78 + (time + 0.5) * 180;
}
