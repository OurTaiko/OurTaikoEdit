using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using OurTaikoEdit.Audio;
using OurTaikoEdit.Core.Languages;
using OurTaikoEdit.Core.Localization;
using OurTaikoEdit.Core.Projects;
using OurTaikoEdit.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OurTaiko;

namespace OurTaikoEdit.App.ViewModels;

public sealed record DiagnosticItem(ChartDiagnostic Diagnostic)
{
    public bool IsError => Diagnostic.Severity == DiagnosticSeverity.Error;
    public bool IsWarning => Diagnostic.Severity == DiagnosticSeverity.Warning;
    public bool IsInformation => Diagnostic.Severity == DiagnosticSeverity.Information;
    public string Icon => IsError ? "●" : IsWarning ? "▲" : "ℹ";
    public string Where => Diagnostic.Source.IsEmpty ? "" : Loc.F("LineN", Diagnostic.Source.Line);
    public string Message => Diagnostic.Message;
}

public sealed record ToolItem(NoteKind? Kind, string Label, string Tip, string Key)
{
    // The tool for a kind (null: Select), named in the interface language.
    public static ToolItem For(NoteKind? kind, string key)
    {
        if (kind is not { } k) return new(null, Loc.T("ToolSelect"), Loc.T("ToolSelectTip"), key);
        string name = k.ToString();
        string tip = Loc.T("Tip" + name);
        if (k is NoteKind.Roll or NoteKind.BigRoll or NoteKind.Balloon or NoteKind.Kusudama) tip = Loc.F("TipDrag", tip);
        return new(k, Loc.T("Note" + name), $"{tip} ({key})", key);
    }
}

// The editing scene. The code is the chart: every view reads the language's analysis of the
// text, and timeline edits come back as text edits the code editor applies (so undo covers them).
public sealed partial class EditorViewModel : ViewModelBase
{
    readonly IDialogService dialogs;
    readonly Action back;
    ChartText file;
    readonly DispatcherTimer analyzeTimer, playTimer;
    readonly Stopwatch playClock = new();
    double playFrom;
    string text;
    bool stale;

    public ChartProject Project { get; }
    public IChartLanguage Language { get; }
    public string InitialText { get; }
    public string FilePath => Project.ChartPath;
    // How the file was read; saving always writes UTF-8, so a conversion is shown as one.
    public string Encoding => file.IsConverted ? file.EncodingName.ToUpperInvariant() + " → UTF-8" : "UTF-8" + (file.Bom ? " BOM" : "");
    // A file read in another encoding is changed by saving, so it starts unsaved.
    public bool StartsDirty => file.IsConverted;
    public IReadOnlyList<string> Encodings => ChartFile.Encodings;

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
    [ObservableProperty] string caret = Loc.F("Caret", 1, 1);
    [ObservableProperty] string? message;
    [ObservableProperty] string problems = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ProblemListVisible))] bool showProblems = true;
    public bool ProblemListVisible => ShowProblems && Diagnostics.Count > 0;

    // The chosen block at a glance (notes, combo, length, hash), when the language gives one.
    [ObservableProperty] string summary = "";
    // Diagnostics' language: "ui" (the interface's), or English, Chinese or Japanese.
    [ObservableProperty] string messageLanguage;
    public IReadOnlyList<string> MessageLanguages { get; } = new[] { "ui", "en", "zh", "ja" };

    public AppSettings Settings { get; }

    // Per analysis: foldable ranges, and the hints drawn at the end of lines (line → label).
    public IReadOnlyList<FoldRange> Folds { get; private set; } = Array.Empty<FoldRange>();
    public IReadOnlyDictionary<int, string> Hints { get; private set; } = new Dictionary<int, string>();

    // The song: its waveform for the timeline, the player (null without an audio device: the
    // playhead then runs on a silent clock), and what the toolbar says about it.
    public static Func<PcmAudio, IAudioPlayer?> OpenPlayer { get; set; } = OpenAlPlayer.Open;
    IAudioPlayer? player;
    string? audioPath;
    int audioLoad;
    [ObservableProperty] Waveform? waveform;
    [ObservableProperty] string audioStatus = Loc.T("NoAudio");
    // What makes the status, so that it is made again in another language.
    Func<string> audioStatusText = () => Loc.T("NoAudio");

    void SetAudioStatus(Func<string> text)
    {
        audioStatusText = text;
        AudioStatus = text();
    }
    [ObservableProperty] bool hitSounds = true;
    [ObservableProperty] double volume = 0.8;
    double lastTick;

    public ObservableCollection<DiagnosticItem> Diagnostics { get; } = new();
    public ObservableCollection<ChartBlock> Blocks { get; } = new();
    public IReadOnlyList<int> Divisions => AppSettings.Divisions;
    public IReadOnlyList<BranchRoute> Routes { get; } = new[] { BranchRoute.Normal, BranchRoute.Expert, BranchRoute.Master };

    [ObservableProperty] IReadOnlyList<ToolItem> tools = MakeTools();

    static IReadOnlyList<ToolItem> MakeTools() => new[]
    {
        ToolItem.For(null, "Q"), ToolItem.For(NoteKind.Don, "1"), ToolItem.For(NoteKind.Ka, "2"), ToolItem.For(NoteKind.BigDon, "3"),
        ToolItem.For(NoteKind.BigKa, "4"), ToolItem.For(NoteKind.Roll, "5"), ToolItem.For(NoteKind.BigRoll, "6"), ToolItem.For(NoteKind.Balloon, "7"),
        ToolItem.For(NoteKind.Kusudama, "9"), ToolItem.For(NoteKind.Kadon, "G"), ToolItem.For(NoteKind.Mine, "C"), ToolItem.For(NoteKind.AdLib, "F"),
    };

    // The code editor applies these as one undoable change.
    public event Action<IReadOnlyList<TextEdit>>? EditsRequested;
    // The code editor moves its caret there (1-based line and column) and shows it.
    public event Action<int, int>? CaretRequested;

    // Another block was chosen in the picker (not by the caret walking into it): the code editor
    // takes its caret into the new block's chart, at the playhead.
    public event Action? BlockChosen;

    public EditorViewModel(ChartProject project, IDialogService dialogs, Action back, AppSettings? settings = null)
    {
        Settings = settings ?? new AppSettings();
        division = Settings.Division;
        volume = Settings.Volume;
        hitSounds = Settings.HitSounds;
        messageLanguage = Settings.MessageLanguage;
        Project = project;
        this.dialogs = dialogs;
        this.back = back;
        Language = ChartLanguages.Find(project.ChartType) ?? throw new InvalidOperationException(Loc.F("UnknownChartType", project.ChartType));
        Language.MessageLanguage = Settings.ChartMessageLanguage;
        file = File.Exists(project.ChartPath) ? ChartFile.Read(project.ChartPath) : new ChartText(Language.CreateTemplate(project.Name, null));
        text = InitialText = file.Text;
        if (file.IsConverted) message = Loc.F("ConvertedOnSave", file.EncodingName.ToUpperInvariant());
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
        LoadAudio(next.Audio);
        Folds = Language.Folds(text);
        Hints = Language.Hints(text, 1, int.MaxValue).GroupBy(h => h.Line).ToDictionary(g => g.Key, g => string.Join("  ", g.Select(h => h.Label)));
        Summary = Language.Summaries(text).FirstOrDefault(b => b.Block == next.Block)?.Text ?? "";
        Analysis = next;
        ResolveSelection();
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
        Problems = errors + warnings == 0 ? Loc.T("NoProblems") : Loc.F("ProblemsCount", Loc.Plural("Errors", errors), Loc.Plural("Warnings", warnings));
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
    // Set while the block follows the caret, which is already where it should be.
    bool caretChoosingBlock;

    partial void OnBlockIndexChanged(int value)
    {
        if (value < 0 || syncingBlock) return;
        Reanalyze();
        if (!caretChoosingBlock) BlockChosen?.Invoke();
    }

    // The code editor's caret: the playhead follows it while stopped, and so do the block and, in
    // a branch, the route. Not when the caret was itself moved to follow the playhead
    // (movePlayhead false).
    public void OnCaretMoved(int line, int column, bool movePlayhead = true)
    {
        caretLine = line; caretColumn = column;
        Caret = Loc.F("Caret", line, column);
        if (IsPlaying || !movePlayhead) return;
        EnsureFresh();
        var blocks = Analysis.Blocks;
        for (int i = blocks.Count - 1; i >= 0; i--)
        {
            if (blocks[i].StartLine > line) continue;
            if (i != BlockIndex)
            {
                caretChoosingBlock = true;
                try { BlockIndex = i; }
                finally { caretChoosingBlock = false; }
            }
            break;
        }
        if (line < Analysis.StartLine || line > Analysis.EndLine) return;
        // In another route of a branch than the one shown: that route is shown, so that the slot
        // at the playhead is the one the caret is on. (Not when the route shown already plays
        // this one, for a branch that does not have it.)
        if (Analysis.RouteAt(line) is { BranchId: >= 0 } at && Analysis.Chart is { } chart && at.BranchId < chart.Branches.Count
            && chart.Branches[at.BranchId].ResolveRoute(Route) != at.Route)
            Route = at.Route;
        if (Analysis.TimeAt(line, column) is { } time) Playhead = time;
    }

    // ---- Selection on the timeline. Kept as places (measure number and fraction), since every
    // edit reads the chart again and makes new notes; resolved to notes after each analysis.

    readonly List<(int Measure, double Fraction)> selectionKeys = new();
    HashSet<ChartNote> selected = new();
    public IReadOnlyCollection<ChartNote> SelectedNotes => selected;

    // Copied notes: beats from the first, kind, and a long note's length in beats.
    static List<(double Beat, NoteKind Kind, double? Length)> clipboard = new();

    (int, double)? KeyOf(ChartNote note)
    {
        foreach (var m in Analysis.Measures)
            for (int i = 0; i < m.Slots.Count; i++)
                if (ReferenceEquals(m.Slots[i].Note, note)) return (m.Index, (double)i / m.Slots.Count);
        return null;
    }

    void ResolveSelection()
    {
        var notes = new HashSet<ChartNote>();
        foreach (var (index, fraction) in selectionKeys)
            foreach (var m in Analysis.Measures)
            {
                if (m.Index != index) continue;
                for (int i = 0; i < m.Slots.Count; i++)
                    if (Math.Abs((double)i / m.Slots.Count - fraction) < 1e-9 && m.Slots[i].Note is { } n) notes.Add(n);
            }
        selected = notes;
        OnPropertyChanged(nameof(SelectedNotes));
    }

    public void Select(IEnumerable<ChartNote> notes, bool add = false)
    {
        if (!add) selectionKeys.Clear();
        foreach (var n in notes)
            if (KeyOf(n) is { } key && !selectionKeys.Contains(key)) selectionKeys.Add(key);
        ResolveSelection();
    }

    public void ToggleSelected(ChartNote note)
    {
        if (KeyOf(note) is not { } key) return;
        if (!selectionKeys.Remove(key)) selectionKeys.Add(key);
        ResolveSelection();
    }

    public void ClearSelection()
    {
        selectionKeys.Clear();
        ResolveSelection();
    }

    public void SelectAll() => Select(Analysis.Chart?.Notes ?? new List<ChartNote>());

    MeasureGrid Grid(BranchRoute route) => new(Analysis, route, Division);

    static BranchRoute RouteOf(ChartNote note, BranchRoute row) => note.BranchId >= 0 ? note.Route : row;

    static bool IsLongKind(NoteKind k) => k is NoteKind.Roll or NoteKind.BigRoll or NoteKind.Balloon or NoteKind.Kusudama;

    // Each note again, moved by some beats and snapped to the grid, kind changed if asked; the
    // selection follows.
    bool Replace(IReadOnlyList<ChartNote> notes, double beats, BranchRoute row, Func<ChartNote, NoteKind>? kind = null)
    {
        EnsureFresh();
        if (!ChartIsCurrent) { Message = Loc.T("FixErrorsFirst"); return false; }
        var places = new List<NotePlacement>();
        foreach (var n in notes)
        {
            var grid = Grid(RouteOf(n, row));
            if (grid.BeatOf(n) is not { } beat || grid.At(beat + beats) is not { } at) { Message = Loc.T("MoveOut"); return false; }
            var newKind = kind?.Invoke(n) ?? n.Kind;
            GridPosition? end = null;
            if (IsLongKind(newKind))
            {
                if (grid.BeatOf(n, tail: true) is not { } tail || grid.At(tail + beats) is not { } e) { Message = Loc.T("RollEndOffGrid"); return false; }
                end = e;
            }
            places.Add(new NotePlacement(at, newKind, end));
        }
        var result = Language.Editing.Edit(Analysis, notes, places);
        if (!result.Ok) { Message = result.Error; return false; }
        selectionKeys.Clear();
        selectionKeys.AddRange(places.Select(p => (p.At.Measure.Index, p.At.Fraction)));
        Apply(result);
        ResolveSelection();
        return true;
    }

    // Moves the selection by grid steps of the current division.
    public void MoveSelection(int steps, BranchRoute row)
    {
        if (selected.Count == 0 || steps == 0) return;
        Replace(selected.ToList(), steps * 4.0 / Division, row);
    }

    public void DeleteSelection()
    {
        if (selected.Count == 0) return;
        EnsureFresh();
        var result = Language.Editing.Edit(Analysis, selected.ToList(), Array.Empty<NotePlacement>());
        if (!result.Ok) { Message = result.Error; return; }
        selectionKeys.Clear();
        Apply(result);
        ResolveSelection();
    }

    // Changes the kind of the selected notes: short notes to a short kind, long ones to a long one.
    public void ChangeSelectionKind(NoteKind kind)
    {
        var notes = selected.Where(n => n.IsLong == IsLongKind(kind) && n.Kind != kind).ToList();
        if (notes.Count == 0) { Message = Loc.T(IsLongKind(kind) ? "OnlyLongToLong" : "OnlyShortToShort"); return; }
        Replace(notes, 0, Route, _ => kind);
    }

    // Don ↔ ka, big stays big.
    public void SwapSelectionColour()
    {
        var notes = selected.Where(n => n.Kind is NoteKind.Don or NoteKind.Ka or NoteKind.BigDon or NoteKind.BigKa).ToList();
        if (notes.Count == 0) return;
        Replace(notes, 0, Route, n => n.Kind switch
        {
            NoteKind.Don => NoteKind.Ka, NoteKind.Ka => NoteKind.Don, NoteKind.BigDon => NoteKind.BigKa, _ => NoteKind.BigDon,
        });
    }

    public void CopySelection(BranchRoute row)
    {
        if (selected.Count == 0) return;
        var items = new List<(double, NoteKind, double?)>();
        foreach (var n in selected)
        {
            var grid = Grid(RouteOf(n, row));
            if (grid.BeatOf(n) is not { } beat) continue;
            double? length = n.IsLong && grid.BeatOf(n, tail: true) is { } tail ? tail - beat : null;
            items.Add((beat, n.Kind, length));
        }
        if (items.Count == 0) return;
        double first = items.Min(i => i.Item1);
        clipboard = items.Select(i => (i.Item1 - first, i.Item2, i.Item3)).OrderBy(i => i.Item1).ToList();
        Message = Loc.Plural("CopiedNotes", clipboard.Count);
    }

    public void CutSelection(BranchRoute row)
    {
        CopySelection(row);
        DeleteSelection();
    }

    // Pastes the copied notes with the first at a grid position; they become the selection.
    public void Paste(GridPosition at)
    {
        if (clipboard.Count == 0) { Message = Loc.T("NothingCopied"); return; }
        EnsureFresh();
        if (!ChartIsCurrent) { Message = Loc.T("FixErrorsFirst"); return; }
        var grid = Grid(at.Route);
        if (grid.Beat(at) is not { } start) return;
        var places = new List<NotePlacement>();
        foreach (var (beat, kind, length) in clipboard)
        {
            if (grid.At(start + beat) is not { } p) { Message = Loc.T("PastePastEnd"); return; }
            GridPosition? end = length is { } l ? grid.At(start + beat + l) : null;
            if (length != null && end == null) { Message = Loc.T("PastePastEnd"); return; }
            places.Add(new NotePlacement(p, kind, end));
        }
        var result = Language.Editing.Edit(Analysis, Array.Empty<ChartNote>(), places);
        if (!result.Ok) { Message = result.Error; return; }
        selectionKeys.Clear();
        selectionKeys.AddRange(places.Select(p => (p.At.Measure.Index, p.At.Fraction)));
        Apply(result);
        ResolveSelection();
    }

    // ---- Timing commands from the timeline.

    public async Task InsertEvent(GridPosition at, ChartEventKind kind)
    {
        EnsureFresh();
        string? value = null;
        if (kind is ChartEventKind.Tempo or ChartEventKind.Scroll or ChartEventKind.Measure or ChartEventKind.Delay)
        {
            var (title, prompt, initial) = kind switch
            {
                ChartEventKind.Tempo => ("#BPMCHANGE", Loc.T("PromptTempo"), at.Measure.Bpm.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)),
                ChartEventKind.Scroll => ("#SCROLL", Loc.T("PromptScroll"), "1"),
                ChartEventKind.Measure => ("#MEASURE", Loc.T("PromptMeasure"), "4/4"),
                _ => ("#DELAY", Loc.T("PromptDelay"), "0"),
            };
            value = await dialogs.PromptAsync(title, prompt, initial);
            if (string.IsNullOrWhiteSpace(value)) return;
            EnsureFresh();
        }
        Apply(Language.Editing.InsertEvent(Analysis, at, kind, value));
    }

    public async Task EditCommand(ChartCommand command)
    {
        var value = await dialogs.PromptAsync(command.Name, Loc.T("NewValue"), command.Value);
        if (string.IsNullOrWhiteSpace(value)) return;
        EnsureFresh();
        if (FindCommand(command) is { } current) Apply(Language.Editing.ChangeCommand(Analysis, current, value));
    }

    public void RevealCommand(ChartCommand command)
    {
        if (!command.Source.IsEmpty) CaretRequested?.Invoke(command.Source.Line, command.Source.Column);
    }

    public void RemoveCommand(ChartCommand command)
    {
        EnsureFresh();
        if (FindCommand(command) is { } current) Apply(Language.Editing.RemoveCommand(Analysis, current));
    }

    public void MoveCommand(ChartCommand command, GridPosition to)
    {
        EnsureFresh();
        if (FindCommand(command) is { } current) Apply(Language.Editing.MoveCommand(Analysis, current, to));
    }

    // The same command in the current analysis (a dialog may have let the text change).
    // The command as the text has it now (a branch's start is one too, for its condition).
    ChartCommand? FindCommand(ChartCommand command)
    {
        var all = Analysis.Commands.Concat(Analysis.Branches.Select(b => b.Start)).ToList();
        return all.FirstOrDefault(c => c == command) ?? all.FirstOrDefault(c => c.Source == command.Source && c.Name == command.Name);
    }

    // A roll, balloon or kusudama given a new end; it stays selected.
    public void ResizeRoll(ChartNote roll, GridPosition end, BranchRoute row)
    {
        EnsureFresh();
        if (!ChartIsCurrent || !roll.IsLong) return;
        var grid = Grid(RouteOf(roll, row));
        if (grid.BeatOf(roll) is not { } beat || grid.At(beat) is not { } head) return;
        var result = Language.Editing.Edit(Analysis, new[] { roll }, new[] { new NotePlacement(head, roll.Kind, end) });
        if (!result.Ok) { Message = result.Error; return; }
        selectionKeys.Clear();
        selectionKeys.Add((head.Measure.Index, head.Fraction));
        Apply(result);
        ResolveSelection();
    }

    // Timeline: a note at a grid position (a long note with its end).
    public void PlaceNote(GridPosition at, GridPosition? end)
    {
        if (Tool.Kind is not { } kind) return;
        EnsureFresh();
        if (!ChartIsCurrent) { Message = Loc.T("FixErrorsTimeline"); return; }
        var result = Language.Editing.PlaceNote(Analysis, at, kind, end);
        Apply(result);
    }

    public void RemoveNote(ChartNote note)
    {
        EnsureFresh();
        if (!ChartIsCurrent) { Message = Loc.T("FixErrorsFirst"); return; }
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

    partial void OnMessageLanguageChanged(string value)
    {
        Settings.MessageLanguage = value;
        Language.MessageLanguage = Settings.ChartMessageLanguage;
        Reanalyze();
    }

    int caretLine = 1, caretColumn = 1;
    // The hit sound set the player has.
    int loadedHitSet;

    // The settings changed (the settings dialog): the words of the interface are made again and
    // the rest is taken over; the grid stays as it is, the setting is for opening a project.
    public void ApplySettings()
    {
        int tool = Tools.ToList().IndexOf(Tool);
        Tools = MakeTools();
        Tool = Tools[Math.Max(0, tool)];
        Volume = Settings.Volume;
        HitSounds = Settings.HitSounds;
        if (player != null) player.HitVolume = (float)Settings.HitVolume;
        if (player != null && loadedHitSet != Settings.HitSoundSet)
        {
            loadedHitSet = Settings.HitSoundSet;
            var (don, ka) = HitSoundSets.Load(loadedHitSet);
            player.SetHitSounds(don, ka);
        }
        Caret = Loc.F("Caret", caretLine, caretColumn);
        AudioStatus = audioStatusText();
        if (MessageLanguage != Settings.MessageLanguage) MessageLanguage = Settings.MessageLanguage;
        else
        {
            Language.MessageLanguage = Settings.ChartMessageLanguage;
            Reanalyze();
        }
        OnPropertyChanged(nameof(Settings));
    }

    [RelayCommand]
    async Task OpenSettings() => await dialogs.SettingsAsync(Settings);

    // Quick fixes and refactorings at a place of the code.
    public IReadOnlyList<CodeAction> ActionsAt(int line, int column)
    {
        EnsureFresh();
        return Language.Actions(Analysis, line, column);
    }

    public void Run(CodeAction action)
    {
        Message = null;
        if (action.Edits.Count == 0) return;
        EditsRequested?.Invoke(action.Edits);
        EnsureFresh();
        Message = action.Title;
    }

    [RelayCommand]
    void FormatDocument()
    {
        EnsureFresh();
        if (Language.Format(text) is { } format) Run(format);
        else Message = Loc.T("AlreadyFormatted");
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
        lastTick = time;
        if (!IsPlaying) return;
        playFrom = time;
        playClock.Restart();
        if (player == null) return;
        double sound = time + Latency;
        if (sound >= 0 && sound < player.Duration) { player.Seek(sound); if (!player.IsPlaying) player.Play(); }
        else if (player.IsPlaying) player.Pause();
    }

    // Loads the song named by the chart (WAVE) when it changes; decoding runs in the background.
    void LoadAudio(string? audio)
    {
        string? path = audio == null ? null : Path.GetFullPath(Path.Combine(Project.Folder, audio));
        if (path == audioPath) return;
        audioPath = path;
        int load = ++audioLoad;
        StopAudio();
        Waveform = null;
        if (path == null) { SetAudioStatus(() => Loc.T("NoAudioWave")); return; }
        if (!File.Exists(path)) { SetAudioStatus(() => Loc.F("FileMissing", audio)); return; }
        SetAudioStatus(() => Loc.F("AudioLoading", Path.GetFileName(path)));
        int hitSet = loadedHitSet = Settings.HitSoundSet;
        Task.Run(() =>
        {
            var pcm = PcmAudio.Decode(path);
            return (pcm, new Waveform(pcm), HitSoundSets.Load(hitSet));
        }).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            if (load != audioLoad) return;
            if (t.IsFaulted) { string error = t.Exception!.InnerException?.Message ?? ""; SetAudioStatus(() => $"{Path.GetFileName(path)}: {error}"); return; }
            var (pcm, wave, hits) = t.Result;
            Waveform = wave;
            player = OpenPlayer(pcm);
            if (player != null) { player.Volume = (float)Volume; player.HitVolume = (float)Settings.HitVolume; player.SetHitSounds(hits.Don, hits.Ka); }
            var length = TimeSpan.FromSeconds(pcm.Duration);
            bool silent = player == null;
            SetAudioStatus(() => $"♪ {Path.GetFileName(path)} {(int)length.TotalMinutes}:{length.Seconds:00}" + (silent ? Loc.T("NoAudioDevice") : ""));
        }));
    }

    void StopAudio()
    {
        player?.Dispose();
        player = null;
    }

    partial void OnHitSoundsChanged(bool value) => Settings.HitSounds = value;

    partial void OnVolumeChanged(double value)
    {
        Settings.Volume = value;
        if (player != null) player.Volume = (float)value;
    }

    // Leaving the editor: the device is let go.
    public void Close()
    {
        if (IsPlaying) TogglePlay();
        audioLoad++;
        StopAudio();
        Settings.Save();
    }

    [RelayCommand]
    void TogglePlay()
    {
        if (IsPlaying)
        {
            IsPlaying = false;
            playTimer.Stop();
            playClock.Stop();
            if (player?.IsPlaying == true) player.Pause();
            return;
        }
        EnsureFresh();
        if (Analysis.Chart is { } c && Playhead >= c.Duration) Playhead = StartTime;
        playFrom = lastTick = Playhead;
        playClock.Restart();
        IsPlaying = true;
        if (player != null && Playhead + Latency >= 0 && Playhead + Latency < player.Duration) { player.Seek(Playhead + Latency); player.Play(); }
        playTimer.Start();
    }

    [RelayCommand]
    void Rewind()
    {
        Seek(StartTime);
    }

    // Seconds from the device taking a sample to it being heard: the song is played that much
    // ahead of the playhead, and the hit sounds are started that much early, so that what is
    // seen and what is heard meet.
    double Latency => Settings.LatencyMs / 1000.0;

    void Tick()
    {
        double clock = playFrom + playClock.Elapsed.TotalSeconds;
        if (player != null && player.IsPlaying) Playhead = player.Position - Latency;
        else
        {
            Playhead = clock;
            // From before the song (a negative OFFSET start), the song comes in at 0.
            if (player != null && Playhead + Latency >= 0 && Playhead + Latency < player.Duration - 0.05) { player.Seek(Playhead + Latency); player.Play(); }
        }
        if (HitSounds && player != null && Analysis.Chart is { } chart) Hits(chart, lastTick + Latency, Playhead + Latency);
        lastTick = Playhead;
        if (Analysis.Chart is not { } c || Playhead > c.Duration + 2) TogglePlay();
    }

    // The don and ka sounds of the notes the playhead passed, on the route being shown.
    void Hits(TaikoChart chart, double from, double to)
    {
        if (to <= from || to - from > 0.25) return;
        foreach (var n in chart.Notes)
        {
            if (n.Time <= from) continue;
            if (n.Time > to) break;
            if (n.BranchId >= 0 && !(n.BranchId < chart.Branches.Count && chart.Branches[n.BranchId].ResolveRoute(Route) == n.Route)) continue;
            if (n.IsLong || n.IsAdLib || n.IsMine) continue;
            if (n.IsKadon) { player!.PlayHit(false, true); player.PlayHit(true, true); }
            else player!.PlayHit(n.IsKa, n.IsBigNote);
        }
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
            if (file.IsConverted) { file = new ChartText(text); OnPropertyChanged(nameof(Encoding)); }
            Message = Loc.F("Saved", Path.GetFileName(Project.ChartPath));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.EncoderFallbackException)
        {
            await dialogs.MessageAsync(Loc.T("CouldNotSave"), e.Message);
            return false;
        }
    }

    // Reads the file again in another encoding (one undoable change of the whole text).
    public async Task ReopenWith(string encoding)
    {
        if (!File.Exists(Project.ChartPath)) return;
        string name = Path.GetFileName(Project.ChartPath);
        if (IsDirty && !file.IsConverted && !await dialogs.ConfirmAsync(Loc.T("ReopenTitle"), Loc.F("ReopenDiscards", name, encoding.ToUpperInvariant()), Loc.F("ReopenWith", encoding.ToUpperInvariant()))) return;
        ChartText read;
        try { read = ChartFile.Read(Project.ChartPath, encoding); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { Message = e.Message; return; }
        file = read;
        OnPropertyChanged(nameof(Encoding));
        if (read.Text != text) EditsRequested?.Invoke(new[] { new TextEdit(1, 1, text.Length, read.Text) });
        EnsureFresh();
        IsDirty = read.IsConverted;
        Message = read.IsConverted ? Loc.F("ConvertedOnSave", read.EncodingName.ToUpperInvariant()) : null;
    }

    [RelayCommand]
    async Task Back()
    {
        if (!await ConfirmLeaveAsync()) return;
        Close();
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
