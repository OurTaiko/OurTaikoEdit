using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OurTaiko;
using OurTaikoEdit.Audio;
using OurTaikoEdit.Core.Localization;

namespace OurTaikoEdit.App.ViewModels;

// A note on its way from the judge to the soul gauge.
public readonly record struct FlyingNote(NoteKind Kind, bool Big, double At, double ShiftX, double ShiftY);

// The play scene: the chart being edited, played as the game plays it. The judge, the score and
// the soul gauge are the game's own (OurTaikoEdit.Play); this is its PlayScene and PlayerLane: the
// song's clock, the drum, and what the stage needs to draw the play (when each thing last
// happened, on the chart's clock, so a paused play stands still).
//
// The song is the editor's player, lent for the play: one device, opened once.
public sealed partial class PlayViewModel : ViewModelBase
{
    // The play starts this long before the place it was asked to start at.
    public const double LeadIn = 2;
    // Auto play's drumroll and balloon hits a second (PlaySession.Advance).
    const double AutoRollRate = 15;

    readonly IAudioPlayer? player;
    readonly Action back;
    readonly Func<double> seconds;
    readonly double latency;
    // Where the play starts: a place of the chart (the notes before it are passed over), or null
    // for the whole chart.
    readonly double? from;
    readonly BranchRoute route;
    double clockFrom, clockStart, frozen;
    bool started;

    public TaikoChart Chart { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public PlaySession Session { get; private set; }
    // Whether the notes passed over at the start are hidden (a play from a place of the chart).
    public bool HidesSkipped => from != null;

    // The chart's time now, as the song is heard.
    public double Time { get; private set; }

    [ObservableProperty] bool auto;
    [ObservableProperty] bool paused;
    [ObservableProperty] bool finished;
    [ObservableProperty] bool hitSounds;
    // Counted up for every new frame of the play, for the stage to draw again.
    [ObservableProperty] int frame;

    // ---- What the stage draws, by the chart time it happened at.

    // The drum's four flashes: don left, don right, ka left, ka right.
    public double[] FlashedAt { get; } = { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
    // The last 良／可／不可 of a press (a note nobody hit says nothing).
    public Judgment Judged { get; private set; }
    public double JudgedAt { get; private set; } = double.NegativeInfinity;
    // The last hit's effect at the judge: its result (a roll hit too), whether the note was big.
    public Judgment Struck { get; private set; }
    public bool StruckBig { get; private set; }
    public double StruckAt { get; private set; } = double.NegativeInfinity;
    public List<FlyingNote> Flying { get; } = new();
    // The soul gauge as shown, and when it last changed.
    public double GaugeChangedAt { get; private set; } = double.NegativeInfinity;
    public double GaugeBefore { get; private set; }
    // The balloon being popped (-1 for none): its note, the hits left, when it was last hit and
    // when it popped.
    public int Balloon { get; private set; } = -1;
    public int BalloonLeft { get; private set; }
    public double BalloonHitAt { get; private set; } = double.NegativeInfinity;
    public double BalloonPoppedAt { get; private set; } = double.NegativeInfinity;
    // The drumroll being hit (-1 for none): its hits so far and when the last one was.
    public int Roll { get; private set; } = -1;
    public int RollHits { get; private set; }
    public double RollHitAt { get; private set; } = double.NegativeInfinity;
    // When the combo last grew, and when it was last announced (50, then every 100).
    public double ComboAt { get; private set; } = double.NegativeInfinity;
    public int Announced { get; private set; }
    public double AnnouncedAt { get; private set; } = double.NegativeInfinity;
    // The route the lane shows, the one before it and when it changed.
    public BranchRoute LaneRoute { get; private set; }
    public BranchRoute LaneRouteBefore { get; private set; }
    public double LaneRouteAt { get; private set; } = double.NegativeInfinity;

    // The result, once the play is finished.
    public string ResultTitle => Finished && Session.IsClear ? Loc.T("PlayCleared") : Loc.T("PlayFailed");
    public int Score => Session.Score;
    public int Good => Session.Good;
    public int Ok => Session.Ok;
    public int Bad => Session.Bad;
    public int Rolls => Session.Rolls;
    public int MaxCombo => Session.MaxCombo;
    public int GaugePercent => Session.GaugePercent;

    // Auto play: the strikes whose sounds were given to the player ahead of their judgment.
    int[] aheadStrikes = Array.Empty<int>(), aheadOwed = Array.Empty<int>();
    readonly List<int> aheadOpen = new();
    int aheadHead;
    bool nextAutoRight = true, lastKa;

    PlayViewModel(TaikoChart chart, string title, string subtitle, double? from, BranchRoute route, IAudioPlayer? player, double latency,
        bool hitSounds, Action back, Func<double>? seconds)
    {
        Chart = chart; Title = title; Subtitle = subtitle;
        this.from = from; this.route = route; this.player = player; this.latency = latency; this.back = back;
        this.hitSounds = hitSounds;
        if (seconds == null)
        {
            var watch = Stopwatch.StartNew();
            seconds = () => watch.Elapsed.TotalSeconds;
        }
        this.seconds = seconds;
        Session = NewSession();
        Restart();
    }

    // The play of a chart from a place of it (null: from its beginning), or why it cannot be
    // played. `route`: the route a branch without all three routes is played on, and the one the
    // branches before the starting place are taken to have chosen.
    public static (PlayViewModel? Play, string? Error) Create(TaikoChart chart, string title, string subtitle, double? from, BranchRoute route,
        IAudioPlayer? player, double latency, bool hitSounds, Action back, Func<double>? seconds = null)
    {
        try { return (new PlayViewModel(chart, title, subtitle, from, route, player, latency, hitSounds, back, seconds), null); }
        // The game's soul gauge knows the five courses only (not Tower or Dan).
        catch (NotSupportedException e) { return (null, e.Message); }
    }

    PlaySession NewSession()
    {
        // A branch written without #E or #M is not judged: the play keeps to the editor's route.
        bool judged = Chart.Branches.TrueForAll(b => b.HasAllRoutes);
        BranchRoute? forced = judged ? null : route;
        if (from is not { } at) return new PlaySession(Chart, 0, forced);
        // The branches already passed are on the editor's route; those to come are judged.
        PlaySession? before = null;
        if (judged && Chart.Branches.Count > 0)
        {
            before = new PlaySession(Chart, 0, route);
            before.Advance(at, true);
        }
        return PlaySession.PracticeAt(Chart, at, before, forced);
    }

    double Begin
    {
        get
        {
            if (from is { } at) return at - LeadIn;
            double first = Chart.Notes.Count > 0 ? Chart.Notes[0].Time : 0;
            return Math.Min(0, first - LeadIn);
        }
    }

    // The play from its start again: a new session, nothing on the stage.
    [RelayCommand]
    public void Restart()
    {
        if (player?.IsPlaying == true) player.Pause();
        Session.Judged -= OnJudged;
        Session.BranchSelected -= OnBranch;
        Session = NewSession();
        Session.Judged += OnJudged;
        Session.BranchSelected += OnBranch;
        int count = Chart.Notes.Count;
        aheadStrikes = new int[count]; aheadOwed = new int[count];
        aheadOpen.Clear(); aheadHead = 0; nextAutoRight = true;
        Array.Fill(FlashedAt, double.NegativeInfinity);
        Judged = Struck = Judgment.None;
        JudgedAt = StruckAt = GaugeChangedAt = BalloonHitAt = BalloonPoppedAt = RollHitAt = ComboAt = AnnouncedAt = LaneRouteAt = double.NegativeInfinity;
        Flying.Clear();
        GaugeBefore = 0; Balloon = Roll = -1; RollHits = BalloonLeft = Announced = 0;
        Time = frozen = clockFrom = Begin;
        LaneRoute = LaneRouteBefore = Session.DisplayBranchAt(Time);
        clockStart = seconds();
        started = true;
        Paused = Finished = false;
        // The sounds of what the play passes over are not played.
        while (aheadHead < count && Chart.Notes[aheadHead].Time < Time) aheadHead++;
        StartSong();
        Frame++;
    }

    // The song from where the clock is, if it has started and not ended there.
    void StartSong()
    {
        if (player == null) return;
        double song = Time + latency;
        if (song >= 0 && song < player.Duration - 0.05) { player.Seek(song); player.Play(); }
    }

    double Now()
    {
        if (!started || Paused || Finished) return frozen;
        if (player is { IsPlaying: true }) return player.Position - latency;
        return clockFrom + (seconds() - clockStart);
    }

    // One frame: the clock read, the session brought up to it, the play ended after its last note.
    public void Tick()
    {
        if (!started || Paused || Finished) return;
        double before = Time;
        Time = Now();
        // The clock does not run back when the song takes over from the stopwatch.
        if (Time < before) Time = before;
        if (player != null && !player.IsPlaying)
        {
            double song = Time + latency;
            // From before the song (a negative OFFSET start), the song comes in at 0.
            if (song >= 0 && song < player.Duration - 0.05) { player.Seek(song); player.Play(); }
        }
        if (Auto) SoundAhead(Time);
        Session.Advance(Time, Auto);
        var shown = Session.DisplayBranchAt(Time);
        if (shown != LaneRoute) { LaneRouteBefore = LaneRoute; LaneRoute = shown; LaneRouteAt = Time; }
        Flying.RemoveAll(f => Time - f.At > 1);
        if (Time > Chart.Duration + 2) Finish();
        Frame++;
    }

    void Finish()
    {
        frozen = Time;
        if (player?.IsPlaying == true) player.Pause();
        Finished = true;
        foreach (string name in new[] { nameof(ResultTitle), nameof(Score), nameof(Good), nameof(Ok), nameof(Bad), nameof(Rolls), nameof(MaxCombo), nameof(GaugePercent) })
            OnPropertyChanged(name);
    }

    [RelayCommand]
    public void TogglePause()
    {
        if (!started || Finished) return;
        if (!Paused)
        {
            frozen = Time = Now();
            if (player?.IsPlaying == true) player.Pause();
            Paused = true;
            return;
        }
        clockFrom = frozen;
        clockStart = seconds();
        Paused = false;
        StartSong();
    }

    [RelayCommand]
    public void ToggleAuto() => Auto = !Auto;

    // Back to the editor: the song is stopped and handed back.
    [RelayCommand]
    public void Back()
    {
        started = false;
        Session.Judged -= OnJudged;
        Session.BranchSelected -= OnBranch;
        if (player?.IsPlaying == true) player.Pause();
        back();
    }

    // A press of the drum: its sound and flash, and the judge's answer.
    public void Hit(bool ka, bool right)
    {
        if (!started || Paused || Finished || Auto) return;
        double time = Time = Math.Max(Time, Now());
        player?.PlayHit(ka, false);
        Flash(ka, right, time);
        lastKa = ka;
        Session.Hit(ka, time);
        Frame++;
    }

    void Flash(bool ka, bool right, double time) => FlashedAt[(ka ? 2 : 0) + (right ? 1 : 0)] = time;

    // Auto play's sounds are given to the player ahead, each for the very sample of the song its
    // strike falls on (as the editor's playback does, HitPlayback); a strike this did not see
    // (the song not playing, a branch decided within the look-ahead) sounds at its judgment.
    void SoundAhead(double time)
    {
        if (player is not { IsPlaying: true } || !HitSounds) return;
        double to = time + latency + HitPlayback.Lookahead;
        var notes = Chart.Notes;
        for (int k = aheadOpen.Count - 1; k >= 0; k--)
            if (!SoundAhead(aheadOpen[k], to)) aheadOpen.RemoveAt(k);
        for (; aheadHead < notes.Count && notes[aheadHead].Time <= to; aheadHead++)
            if (SoundAhead(aheadHead, to)) aheadOpen.Add(aheadHead);
    }

    // Whether the note may have more to sound later: a long note still running, or a note of a
    // branch not decided yet.
    bool SoundAhead(int i, double to)
    {
        var note = Chart.Notes[i];
        if (Session.Resolved[i] || note.IsMine) return false;
        if (!Session.IsActive(note)) return Session.SelectedRoute(note.BranchId) == null;
        if (!note.IsLong)
        {
            Strike(i, note, note.Time);
            return false;
        }
        int hits = Session.AutoLongHitsBy(i, to);
        while (aheadStrikes[i] < hits) Strike(i, note, note.Time + aheadStrikes[i] / AutoRollRate);
        return to < note.EndTime && !(note.Kind == NoteKind.Balloon && aheadStrikes[i] >= note.BalloonHits);
    }

    void Strike(int i, ChartNote note, double at)
    {
        aheadStrikes[i]++; aheadOwed[i]++;
        player!.PlayHitAt(at, note.IsKa, ChartBig(note));
        if (note.IsKadon) player.PlayHitAt(at, true, true);
    }

    static bool ChartBig(ChartNote note) => note.IsBigNote || note.IsKadon || note.Kind is NoteKind.BigRoll or NoteKind.Kusudama;

    void OnBranch(ChartBranch branch, BranchRoute chosen) { }

    // PlayerLane.OnJudged: what a result shows and sounds.
    void OnJudged(int index, Judgment result)
    {
        var note = Chart.Notes[index];
        double time = Time;
        if (result != Judgment.Roll)
        {
            GaugeBefore = shownGauge;
            GaugeChangedAt = time;
            shownGauge = Session.GaugePoints;
        }
        if (Auto)
        {
            bool sounded = aheadOwed[index] > 0;
            if (sounded) aheadOwed[index]--;
            else if (HitSounds && player != null)
            {
                player.PlayHit(note.IsKa, ChartBig(note));
                if (note.IsKadon) player.PlayHit(true, true);
            }
            // The hands take turns, within a long note too; a Kadon is both at once.
            Flash(note.IsKa, nextAutoRight, time);
            if (note.IsKadon) Flash(true, !nextAutoRight, time);
            nextAutoRight = !nextAutoRight;
        }
        Fly(index, note, result, time);
        Struck = result; StruckBig = note.IsBigNote || note.IsKadon; StruckAt = time;
        if (result != Judgment.Roll)
        {
            // A note nobody hit says nothing: 不可 is drawn for a press only.
            if (!Session.Missed[index]) { Judged = result; JudgedAt = time; }
        }
        else if (note.Kind == NoteKind.Balloon)
        {
            Balloon = index; BalloonLeft = Math.Max(0, note.BalloonHits - Session.LongHits[index]); BalloonHitAt = time;
            if (BalloonLeft == 0) BalloonPoppedAt = time;
        }
        else if (note.Kind == NoteKind.Kusudama && Session.KusudamaOf(index) is { } ball)
        {
            Balloon = index; BalloonLeft = ball.Remaining; BalloonHitAt = time;
            if (ball.IsBroken) BalloonPoppedAt = time;
        }
        else if (note.IsDrumroll)
        {
            Roll = index; RollHits = Session.LongHits[index]; RollHitAt = time;
        }
        int combo = Session.Combo;
        if (combo > lastCombo) ComboAt = time;
        if (combo != lastCombo && (combo == 50 || combo > 0 && combo % 100 == 0)) { Announced = combo; AnnouncedAt = time; }
        lastCombo = combo;
    }

    double shownGauge;
    int lastCombo;

    // PlayerLane.SpawnArc: a 良 or 可 note flies to the gauge as itself, an Ad-Lib as the small note
    // of the struck drum, a popped balloon as a big don, a roll hit as the struck drum's colour.
    void Fly(int index, ChartNote note, Judgment result, double time)
    {
        NoteKind kind;
        bool ka = lastKa && !Auto;
        if (result is Judgment.Good or Judgment.Ok) kind = note.IsAdLib ? (ka ? NoteKind.Ka : NoteKind.Don) : note.Kind;
        else if (result != Judgment.Roll || note.Kind == NoteKind.Kusudama) return;
        else if (note.Kind == NoteKind.Balloon)
        {
            if (Session.LongHits[index] != note.BalloonHits) return;
            kind = NoteKind.BigDon;
        }
        else if (note.Kind == NoteKind.BigRoll) kind = ka ? NoteKind.BigKa : NoteKind.BigDon;
        else kind = ka ? NoteKind.Ka : NoteKind.Don;
        bool big = kind is NoteKind.BigDon or NoteKind.BigKa or NoteKind.Kadon;
        var shift = JudgeShift.At(Chart.JudgeMoves, time);
        Flying.Add(new FlyingNote(kind, big, time, shift.X, shift.Y));
    }

    partial void OnAutoChanged(bool value)
    {
        // What auto play had sounded ahead is not owed to a player's own presses, nor the reverse.
        Array.Clear(aheadOwed);
        aheadOpen.Clear();
        while (aheadHead < Chart.Notes.Count && Chart.Notes[aheadHead].Time < Time) aheadHead++;
        if (!value) player?.CancelHits();
    }
}
