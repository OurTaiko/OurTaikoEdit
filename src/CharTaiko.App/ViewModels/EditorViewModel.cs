using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CharTaiko.Core.Languages;
using CharTaiko.Core.Projects;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OurTaiko;

namespace CharTaiko.App.ViewModels;

public sealed record DiagnosticItem(ChartDiagnostic Diagnostic)
{
    public bool IsError => Diagnostic.Severity == DiagnosticSeverity.Error;
    public bool IsWarning => Diagnostic.Severity == DiagnosticSeverity.Warning;
    public bool IsInformation => Diagnostic.Severity == DiagnosticSeverity.Information;
    public string Icon => IsError ? "●" : IsWarning ? "▲" : "ℹ";
    public string Where => Diagnostic.Source.IsEmpty ? "" : $"Ln {Diagnostic.Source.Line}";
    public string Message => Diagnostic.Message;
}

public sealed record ToolItem(NoteKind? Kind, string Label, string Tip, string Key);

// The editing scene. The code is the chart: every view reads the language's analysis of the
// text, and timeline edits come back as text edits the code editor applies (so undo covers them).
public sealed partial class EditorViewModel : ViewModelBase
{
    readonly IDialogService dialogs;
    readonly Action back;
    readonly ChartText file;
    readonly DispatcherTimer analyzeTimer, playTimer;
    readonly Stopwatch playClock = new();
    double playFrom;
    string text;
    bool stale;

    public ChartProject Project { get; }
    public IChartLanguage Language { get; }
    public string InitialText { get; }
    public string FilePath => Project.ChartPath;
    public string Encoding => file.EncodingName.ToUpperInvariant() + (file.Bom ? " BOM" : "");

    [ObservableProperty] ChartAnalysis analysis = ChartAnalysis.Empty;
    [ObservableProperty] int blockIndex;
    [ObservableProperty] bool isDirty;
    // Seconds on the audio's clock, as ChartNote.Time.
    [ObservableProperty] double playhead;
    [ObservableProperty] bool isPlaying;
    [ObservableProperty] BranchRoute route;
    // Grid lines per 4/4 measure.
    [ObservableProperty] int division = 16;
    [ObservableProperty] ToolItem tool;
    [ObservableProperty] string caret = "Ln 1, Col 1";
    [ObservableProperty] string? message;
    [ObservableProperty] string problems = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ProblemListVisible))] bool showProblems = true;
    public bool ProblemListVisible => ShowProblems && Diagnostics.Count > 0;

    public ObservableCollection<DiagnosticItem> Diagnostics { get; } = new();
    public ObservableCollection<ChartBlock> Blocks { get; } = new();
    public IReadOnlyList<int> Divisions { get; } = new[] { 4, 8, 12, 16, 24, 32, 48, 64, 96, 192 };
    public IReadOnlyList<BranchRoute> Routes { get; } = new[] { BranchRoute.Normal, BranchRoute.Expert, BranchRoute.Master };

    public IReadOnlyList<ToolItem> Tools { get; } = new ToolItem[]
    {
        new(null, "Select", "Select: click a note to find it in the code", "Q"),
        new(NoteKind.Don, "Don", "Don (1)", "1"),
        new(NoteKind.Ka, "Ka", "Ka (2)", "2"),
        new(NoteKind.BigDon, "DON", "Big don (3)", "3"),
        new(NoteKind.BigKa, "KA", "Big ka (4)", "4"),
        new(NoteKind.Roll, "Roll", "Drumroll (5): drag from start to end", "5"),
        new(NoteKind.BigRoll, "ROLL", "Big drumroll (6): drag from start to end", "6"),
        new(NoteKind.Balloon, "Balloon", "Balloon (7): drag from start to end", "7"),
        new(NoteKind.Kusudama, "Kusudama", "Kusudama (9): drag from start to end", "9"),
        new(NoteKind.Kadon, "Kadon", "Kadon (G): don and ka together", "G"),
        new(NoteKind.Mine, "Mine", "Mine (C): a note not to hit", "C"),
        new(NoteKind.AdLib, "Ad-lib", "Ad-lib (F): a hidden bonus note", "F"),
    };

    // The code editor applies these as one undoable change.
    public event Action<IReadOnlyList<TextEdit>>? EditsRequested;
    // The code editor moves its caret there (1-based line and column) and shows it.
    public event Action<int, int>? CaretRequested;

    public EditorViewModel(ChartProject project, IDialogService dialogs, Action back)
    {
        Project = project;
        this.dialogs = dialogs;
        this.back = back;
        Language = ChartLanguages.Find(project.ChartType) ?? throw new InvalidOperationException($"Unknown chart type {project.ChartType}");
        file = File.Exists(project.ChartPath) ? ChartFile.Read(project.ChartPath) : new ChartText(Language.CreateTemplate(project.Name, null));
        text = InitialText = file.Text;
        tool = Tools[1];
        blockIndex = Math.Max(0, project.LastBlock);
        // (The DispatcherTimer constructor that takes a callback starts the timer; these wait.)
        analyzeTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(180) };
        analyzeTimer.Tick += (_, _) => Reanalyze();
        playTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
        playTimer.Tick += (_, _) => Tick();
        Reanalyze();
        Playhead = StartTime;
    }

    // A little before the first note, or zero.
    double StartTime => Analysis.Chart is { } c ? Math.Min(0, c.Notes.Count > 0 ? c.Notes[0].Time - 1 : 0) : 0;

    // The code editor reports every change of its text.
    public void OnTextChanged(string newText)
    {
        if (newText == text) return;
        text = newText;
        IsDirty = true;
        stale = true;
        analyzeTimer.Stop();
        analyzeTimer.Start();
    }

    // Brings the analysis up to the current text (timeline edits need exact positions).
    void EnsureFresh()
    {
        if (stale) Reanalyze();
    }

    void Reanalyze()
    {
        analyzeTimer.Stop();
        stale = false;
        var previous = Analysis;
        var next = Language.Analyze(text, BlockIndex);
        // The parser reads on past errors, but edits must land where it reads cleanly.
        ChartIsCurrent = next.Chart != null && !next.HasErrors;
        // While the chosen block does not parse, keep showing the last chart that did.
        if (next.Chart == null && previous.Chart != null && previous.Block == next.Block) next = Keep(previous, next);
        Analysis = next;
        if (!Blocks.SequenceEqual(next.Blocks))
        {
            Blocks.Clear();
            foreach (var b in next.Blocks) Blocks.Add(b);
        }
        if (next.Block >= 0 && next.Block != BlockIndex)
        {
            syncingBlock = true;
            BlockIndex = next.Block;
            syncingBlock = false;
        }
        Diagnostics.Clear();
        foreach (var d in next.Diagnostics) Diagnostics.Add(new DiagnosticItem(d));
        int errors = next.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
        int warnings = next.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
        OnPropertyChanged(nameof(ProblemListVisible));
        Problems = errors + warnings == 0 ? "No problems" : $"{errors} error{(errors == 1 ? "" : "s")}, {warnings} warning{(warnings == 1 ? "" : "s")}";
    }

    // The failed analysis's diagnostics with the last good chart, which edits must not touch.
    static ChartAnalysis Keep(ChartAnalysis good, ChartAnalysis failed) => new()
    {
        Text = failed.Text, Blocks = failed.Blocks, Block = failed.Block, Diagnostics = failed.Diagnostics, Title = failed.Title, Audio = failed.Audio,
        Chart = good.Chart, Measures = good.Measures, SourceOf = good.SourceOf, TailOf = good.TailOf,
        StartLine = good.StartLine, EndLine = good.EndLine,
    };

    // Whether the shown chart is the current text's, not one kept from before an error.
    public bool ChartIsCurrent { get; private set; }

    // Set while the block follows the analysis, which must not start another one.
    bool syncingBlock;

    partial void OnBlockIndexChanged(int value)
    {
        if (value < 0 || syncingBlock) return;
        Reanalyze();
    }

    // The code editor's caret: the playhead follows it while stopped, and so does the block.
    public void OnCaretMoved(int line, int column)
    {
        Caret = $"Ln {line}, Col {column}";
        if (IsPlaying) return;
        EnsureFresh();
        var blocks = Analysis.Blocks;
        for (int i = blocks.Count - 1; i >= 0; i--)
        {
            if (blocks[i].StartLine > line) continue;
            if (i != BlockIndex) BlockIndex = i;
            break;
        }
        if (line < Analysis.StartLine || line > Analysis.EndLine) return;
        if (Analysis.SlotAt(line, column) is { } slot) Playhead = slot.Time;
    }

    // Timeline: a note at a grid position (a long note with its end).
    public void PlaceNote(GridPosition at, GridPosition? end)
    {
        if (Tool.Kind is not { } kind) return;
        EnsureFresh();
        if (!ChartIsCurrent) { Message = "Fix the errors in the code first; the timeline edits what the parser reads."; return; }
        var result = Language.Editing.PlaceNote(Analysis, at, kind, end);
        Apply(result);
    }

    public void RemoveNote(ChartNote note)
    {
        EnsureFresh();
        if (!ChartIsCurrent) { Message = "Fix the errors in the code first."; return; }
        Apply(Language.Editing.RemoveNote(Analysis, note));
    }

    void Apply(EditResult result)
    {
        if (!result.Ok) { Message = result.Error; return; }
        Message = null;
        if (result.Edits.Count == 0) return;
        EditsRequested?.Invoke(result.Edits);
        EnsureFresh();
    }

    // Shows a note in the code.
    public void Reveal(ChartNote note)
    {
        var span = Analysis.SourceOf(note);
        if (!span.IsEmpty) CaretRequested?.Invoke(span.Line, span.Column);
    }

    public void GoTo(DiagnosticItem? item)
    {
        if (item == null || item.Diagnostic.Source.IsEmpty) return;
        CaretRequested?.Invoke(item.Diagnostic.Source.Line, Math.Max(1, item.Diagnostic.Source.Column));
    }

    public void Seek(double time)
    {
        Playhead = time;
        if (IsPlaying) { playFrom = time; playClock.Restart(); }
    }

    [RelayCommand]
    void TogglePlay()
    {
        if (IsPlaying)
        {
            IsPlaying = false;
            playTimer.Stop();
            playClock.Stop();
            return;
        }
        EnsureFresh();
        if (Analysis.Chart is { } c && Playhead >= c.Duration) Playhead = StartTime;
        playFrom = Playhead;
        playClock.Restart();
        IsPlaying = true;
        playTimer.Start();
    }

    [RelayCommand]
    void Rewind()
    {
        Seek(StartTime);
    }

    void Tick()
    {
        Playhead = playFrom + playClock.Elapsed.TotalSeconds;
        if (Analysis.Chart is not { } c || Playhead > c.Duration + 2) TogglePlay();
    }

    [RelayCommand]
    void SelectTool(ToolItem item) => Tool = item;

    [RelayCommand]
    void ToggleProblems() => ShowProblems = !ShowProblems;

    [RelayCommand]
    public async Task<bool> Save()
    {
        try
        {
            ChartFile.Write(Project.ChartPath, file with { Text = text });
            Project.LastBlock = BlockIndex;
            Project.Save();
            IsDirty = false;
            Message = "Saved " + Path.GetFileName(Project.ChartPath);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.EncoderFallbackException)
        {
            await dialogs.MessageAsync("Could not save", e.Message);
            return false;
        }
    }

    [RelayCommand]
    async Task Back()
    {
        if (!await ConfirmLeaveAsync()) return;
        if (IsPlaying) TogglePlay();
        back();
    }

    public async Task<bool> ConfirmLeaveAsync()
    {
        if (!IsDirty) return true;
        return await dialogs.AskSaveAsync(Path.GetFileName(Project.ChartPath)) switch
        {
            SaveChoice.Save => await Save(),
            SaveChoice.Discard => true,
            _ => false,
        };
    }
}
