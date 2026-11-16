using System;
using System.Collections.Generic;
using System.Linq;

namespace OurTaiko
{
    // The half widths of the 良, 可 and 不可 windows in real seconds, Good <= Ok <= Bad.
    public readonly struct JudgeWindows
    {
        public readonly double Good, Ok, Bad;
        public JudgeWindows(double good, double ok, double bad) { Good = good; Ok = ok; Bad = bad; }

        // The course's own windows: かんたん and ふつう are wider.
        public static JudgeWindows Standard(string course) => course == "Easy" || course == "Normal"
            ? new JudgeWindows(0.0417083358764648, 0.108441665649414, 0.125125)
            : new JudgeWindows(PlaySession.GoodWindow, PlaySession.OkWindow, PlaySession.BadWindow);

        // Whole milliseconds as the settings keep them, kept in range and in order.
        public static JudgeWindows FromMs(int good, int ok, int bad)
        {
            bad = Math.Max(PlaySettings.JudgeWindowMinMs, Math.Min(PlaySettings.JudgeWindowMaxMs, bad));
            ok = Math.Max(PlaySettings.JudgeWindowMinMs, Math.Min(bad, ok));
            good = Math.Max(PlaySettings.JudgeWindowMinMs, Math.Min(ok, good));
            return new JudgeWindows(good / 1000.0, ok / 1000.0, bad / 1000.0);
        }
    }

    // Pure C#: independent of frame rate, rendering and audio, and testable without a scene.
    public sealed class PlaySession
    {
        public const double GoodWindow = 0.0250250015258789, OkWindow = 0.0750750045776367, BadWindow = 0.108441665649414;
        // OpenTaiko's BigNotesWaitTime (default 50 ms): a Kadon's second colour must follow the first within it.
        public const double KadonWait = 0.05;
        public readonly TaikoChart Chart;
        public int KiwamiThreshold => scoring.CeilingScore;
        // Playback rate (practice speed). Chart time runs this much faster than real time, so every
        // window and the judge offset are scaled by it to stay the same number of real milliseconds.
        public double Rate { get; }
        // In chart seconds; the configured offset is real seconds.
        public double JudgeOffset => realJudgeOffset * Rate;
        readonly double realJudgeOffset;
        // The player's own windows for every course (PlaySettings.customJudgeWindow), or null for the
        // course's standard ones. A play with them is not a real result: it is never saved or uploaded.
        public JudgeWindows? CustomWindows { get; }
        public bool CustomJudge => CustomWindows.HasValue;
        public BranchRoute? ForcedBranch { get; }
        // Results are written only here, so every change also moves Version.
        public IReadOnlyList<bool> Resolved => resolved;
        // Normal notes judged 不可 because the window passed without a hit.
        public IReadOnlyList<bool> Missed => missed;
        // Notes that ended before the practice cursor: never judged, and hidden once the attempt starts.
        public IReadOnlyList<bool> Skipped => skipped;
        // Mines that were hit. A mine left alone is resolved once its window passes and scrolls on.
        public IReadOnlyList<bool> Exploded => exploded;
        public IReadOnlyList<int> LongHits => longHits;
        readonly bool[] resolved, missed, skipped, exploded;
        readonly int[] longHits;
        // Each kusudama note's ball: its own, or shared with the other seat (Kusudama.Share).
        readonly Kusudama[] kusudamas;
        public Kusudama KusudamaOf(int index) => kusudamas[index];
        readonly double?[] timingOffsets;
        readonly List<HitTiming> hitTimings = new List<HitTiming>();
        public IReadOnlyList<HitTiming> HitTimings => hitTimings;
        public double? TimingOffsetMs(int index) => timingOffsets[index];
        // Chart milliseconds, like TimingOffsetMs, so the histogram bands match the offsets at any rate.
        public double GoodWindowMs => goodWindow * 1000;
        public double OkWindowMs => okWindow * 1000;
        public double BadWindowMs => badWindow * 1000;
        public int Score => scoring.Total;
        public int BaseScore => scoring.BaseScore;
        public int Combo { get; private set; }
        public int MaxCombo { get; private set; }
        double expressionOpenStart = double.PositiveInfinity, expressionFastStart = double.PositiveInfinity, expressionAngryStart = double.PositiveInfinity;
        public int Good { get; private set; }
        public int Ok { get; private set; }
        public int Bad { get; private set; }
        public int Rolls { get; private set; }
        // Ad-Libs hit 良 and 可: they score, combo and fill the gauge as small notes but are counted
        // apart from Good and Ok (OpenTaiko nADLIB), so the judge counts are the chart's notes only.
        public int AdLibGood { get; private set; }
        public int AdLibOk { get; private set; }
        // Mines hit (OpenTaiko nMine): each breaks the combo, costs gauge and loses a full combo,
        // but is not a 不可.
        public int MinesHit { get; private set; }
        public double Gauge => gauge.Value;
        public double GaugePoints => gauge.Points;
        public int GaugePercent => gauge.Percent;
        public double ClearThreshold => gauge.ClearThreshold;
        public bool IsClear => gauge.IsClear;
        public bool IsGaugeFull => gauge.IsFull;
        public BranchRoute CurrentBranch { get; private set; } = BranchRoute.Normal;
        public IReadOnlyList<BranchRoute> BranchHistory => branchHistory;
        public double LastBranchValue { get; private set; }
        // A #LEVELHOLD took effect: every later branch keeps the current route.
        public bool IsLevelHeld { get; private set; }
        // Changes whenever a note's result or a branch choice changes, so a frame can skip an unchanged redraw.
        public int Version { get; private set; }
        public event Action<int, Judgment> Judged;
        public event Action<ChartBranch, BranchRoute> BranchSelected;
        readonly ShinuchiScore scoring;
        readonly SoulGauge gauge;
        readonly double goodWindow, okWindow, badWindow, kadonWait;
        readonly int[] selectedRoutes;
        readonly List<BranchRoute> branchHistory = new List<BranchRoute>();
        readonly List<TimelineEvent> timeline = new List<TimelineEvent>();
        int nextEvent;
        // Notes are sorted by time (TjaParser), so only two groups can still owe a result: the
        // unresolved notes already reached (pending, ascending index) and the notes from head on.
        // Every note before head and not pending is resolved or on a decided unselected route.
        readonly bool timeSorted;
        int head;
        List<int> pending = new List<int>(), revisit = new List<int>();
        readonly BranchFeed branchFeed = new BranchFeed();
        readonly BranchCounter branchCounter;
        double practiceStart = double.NegativeInfinity;
        // The Kadon waiting for its second colour (-1 for none), and its first hit.
        int kadonWaiting = -1;
        double kadonFirstHit;
        bool kadonFirstKa;

        sealed class TimelineEvent
        {
            public double Time;
            public ChartBranch Branch;
            public ChartSection Section;
            public bool LevelHold;
            // OpenTaiko's chip order at one time: notes, #LEVELHOLD, the branch judgement, #SECTION.
            public int Priority => LevelHold ? 0 : Branch != null ? 1 : 2;
        }

        // A fixed route (practice) plays every branch on ResolveRoute(route) and evaluates no
        // condition. Without one, branches are judged by their #BRANCHSTART condition; the reference
        // player does not define omitted routes, so such charts are refused, not guessed.
        public PlaySession(TaikoChart chart, double judgeOffset = 0, BranchRoute? forcedBranch = null, double rate = 1,
            JudgeWindows? judgeWindows = null)
        {
            if (double.IsNaN(rate) || double.IsInfinity(rate) || rate <= 0) throw new ArgumentOutOfRangeException(nameof(rate));
            if (!forcedBranch.HasValue)
                foreach (var branch in chart.Branches)
                    if (!branch.HasAllRoutes)
                        throw new NotSupportedException("A branch without #E or #M can only be played on a fixed route, as in practice.");
            ForcedBranch = forcedBranch;
            realJudgeOffset = judgeOffset;
            Rate = rate;
            CustomWindows = judgeWindows;
            Chart = chart; resolved = new bool[chart.Notes.Count]; missed = new bool[chart.Notes.Count]; skipped = new bool[chart.Notes.Count]; exploded = new bool[chart.Notes.Count]; longHits = new int[chart.Notes.Count];
            timingOffsets = new double?[chart.Notes.Count];
            kusudamas = new Kusudama[chart.Notes.Count];
            for (int i = 0; i < chart.Notes.Count; i++)
                if (chart.Notes[i].Kind == NoteKind.Kusudama) kusudamas[i] = new Kusudama(this, i);
            var statistics = new ChartStatistics(chart);
            scoring = new ShinuchiScore(statistics);
            gauge = new SoulGauge(statistics.RouteNotes, chart.Course, chart.Level);
            branchCounter = new BranchCounter(branchFeed);
            var windows = judgeWindows ?? JudgeWindows.Standard(chart.Course);
            goodWindow = windows.Good * rate;
            okWindow = windows.Ok * rate;
            badWindow = windows.Bad * rate;
            kadonWait = KadonWait * rate;
            selectedRoutes = Enumerable.Repeat(-1, chart.Branches.Count).ToArray();
            timeSorted = true;
            for (int i = 0; i < chart.Notes.Count; i++)
                if (double.IsNaN(chart.Notes[i].Time) || i > 0 && chart.Notes[i].Time < chart.Notes[i - 1].Time) timeSorted = false;
            foreach (var branch in chart.Branches) timeline.Add(new TimelineEvent { Time = branch.DecisionTime, Branch = branch });
            foreach (var section in chart.Sections) timeline.Add(new TimelineEvent { Time = section.Time, Section = section });
            foreach (double hold in chart.LevelHolds) timeline.Add(new TimelineEvent { Time = hold, LevelHold = true });
            // Stable order preserves authored checkpoint order when several become ready together.
            timeline = timeline.OrderBy(e => e.Time).ThenBy(e => e.Priority).ToList();
        }

        public bool IsActive(ChartNote note) => IsActive(note.BranchId, note.Route);

        // Drawn in normal play: the active route, and before a branch is judged the route played
        // so far (Normal at first), which OpenTaiko shows until the judgement swaps it.
        public bool IsShown(ChartNote note) => note.BranchId < 0 || note.Route == (SelectedRoute(note.BranchId)
            ?? Chart.Branches[note.BranchId].ResolveRoute(ForcedBranch ?? CurrentBranch));

        // Decisions can happen before the authored branch starts so its notes can load.
        // The lane label follows the actual section: common measures always show Normal.
        public BranchRoute DisplayBranchAt(double time)
        {
            for (int i = Chart.Branches.Count - 1; i >= 0; i--)
            {
                var branch = Chart.Branches[i];
                if (time < branch.Time || time >= branch.EndTime) continue;
                return SelectedRoute(branch.Id) ?? branch.ResolveRoute(ForcedBranch ?? BranchRoute.Normal);
            }
            return BranchRoute.Normal;
        }

        // Start a fresh practice attempt without replaying judgments, sounds or missed-note penalties.
        // Preserve already chosen branches before the cursor, and recalculate future checkpoints.
        // The rate and the judge windows default to the previous attempt's.
        public static PlaySession PracticeAt(TaikoChart chart, double time, PlaySession previous = null, BranchRoute? forcedBranch = null,
            double? rate = null, JudgeWindows? judgeWindows = null)
        {
            var session = new PlaySession(chart, previous?.realJudgeOffset ?? 0, forcedBranch, rate ?? previous?.Rate ?? 1,
                judgeWindows ?? previous?.CustomWindows) { practiceStart = time };
            while (session.nextEvent < session.timeline.Count && session.timeline[session.nextEvent].Time <= time)
            {
                var item = session.timeline[session.nextEvent++];
                if (item.Branch == null) continue;
                var route = forcedBranch.HasValue ? item.Branch.ResolveRoute(forcedBranch.Value)
                    : previous?.SelectedRoute(item.Branch.Id) ?? BranchRoute.Normal;
                session.selectedRoutes[item.Branch.Id] = (int)route;
                session.CurrentBranch = route;
                session.branchHistory.Add(route);
            }
            for (int i = 0; i < chart.Notes.Count; i++)
            {
                var note = chart.Notes[i];
                bool past = (note.IsLong ? note.EndTime : note.Time) < time - session.JudgeOffset - 1e-7;
                session.resolved[i] = session.skipped[i] = past;
                // Past notes still scroll out naturally when browsing backwards/forwards.
                session.missed[i] = past && !note.IsLong;
            }
            return session;
        }

        public bool IsPracticePreviewActive(ChartNote note) => IsPracticePreviewActive(note.BranchId, note.Route);
        bool IsPracticePreviewActive(int branchId, BranchRoute route) => branchId < 0
            || route == (ForcedBranch.HasValue ? Chart.Branches[branchId].ResolveRoute(ForcedBranch.Value)
                : SelectedRoute(branchId) ?? BranchRoute.Normal);

        // Like handle_gogotime: the latest #GOGOSTART / #GOGOEND reached on the played route,
        // independent of notes, so GOGO starts and ends exactly at its commands.
        public bool IsGogo(double time, bool preview = false)
            => GogoStateAt(time, preview).Active;

        // Retain the latest start even after END: its half-second fireworks finish independently.
        // Deriving age from the timeline also makes seeking and paused practice deterministic.
        public (bool Active, double StartedAt) GogoStateAt(double time, bool preview = false)
        {
            bool on = false;
            double startedAt = double.NegativeInfinity;
            foreach (var gogo in Chart.Gogos)
            {
                if (gogo.Time > time) break;
                if (!(preview ? IsPracticePreviewActive(gogo.BranchId, gogo.Route) : IsActive(gogo.BranchId, gogo.Route))) continue;
                on = gogo.On;
                if (on) startedAt = gogo.Time;
            }
            return (on, startedAt);
        }
        // #LANE_ALPHA: the lane background's opacity (0–1) on the played route, as GOGO is read:
        // a route's commands count once its branch is decided, the common ones always.
        public double LaneAlphaAt(double time, bool preview = false)
            => Chart.LaneAlphas.Count == 0 ? LaneAlpha.Initial : LaneAlpha.At(Chart.LaneAlphas, time,
                a => preview ? IsPracticePreviewActive(a.BranchId, a.Route) : IsActive(a.BranchId, a.Route));

        // Latest reached tempo on this route. Querying by chart time also supports practice
        // seeking backwards; neither future notes' BPM nor their SCROLL affects expressions.
        public double BpmAt(double time, bool preview = false) => TempoAt(time, preview)?.Bpm ?? Chart.Bpm;

        ChartTempo TempoAt(double time, bool preview)
        {
            int low = 0, high = Chart.Tempos.Count;
            while (low < high)
            {
                int mid = low + (high - low) / 2;
                if (Chart.Tempos[mid].Time <= time) low = mid + 1;
                else high = mid;
            }
            for (int i = low - 1; i >= 0; i--)
            {
                var tempo = Chart.Tempos[i];
                if (preview ? IsPracticePreviewActive(tempo.BranchId, tempo.Route) : IsActive(tempo.BranchId, tempo.Route))
                    return tempo;
            }
            return null;
        }

        // Authored beat anchors preserve phase through OFFSET, tempo changes and delays.
        public double BeatAt(double time, bool preview = false)
        {
            var tempo = TempoAt(time, preview);
            if (tempo != null) return tempo.Beat + Math.Max(0, time - tempo.ResumeTime) * tempo.Bpm / 60;
            return (time + Chart.Offset) * Chart.Bpm / 60;
        }

        public int NoteExpressionFrame(double time, bool preview = false) => NoteExpression.Frame(
            BeatAt(time, preview), Combo, expressionOpenStart, expressionFastStart, expressionAngryStart);

        bool IsActive(int branchId, BranchRoute route) => branchId < 0 || selectedRoutes[branchId] == (int)route;
        // The route whose soul gauge increments a note uses (OpenTaiko UpdateGauge): its own inside
        // a branch section, the route being played elsewhere.
        public BranchRoute GaugeRoute(int note)
        {
            var n = Chart.Notes[note];
            return n.BranchId >= 0 ? n.Route : CurrentBranch;
        }
        public BranchRoute? SelectedRoute(int branchId) => selectedRoutes[branchId] < 0 ? (BranchRoute?)null : (BranchRoute)selectedRoutes[branchId];

        public void Advance(double time, bool auto)
        {
            // Split a long frame at each reset/decision. Future hits must not affect an
            // earlier checkpoint, and inactive routes must never enter its statistics.
            // Notes due at the same time come first, as in OpenTaiko.
            while (nextEvent < timeline.Count && timeline[nextEvent].Time <= time)
            {
                var item = timeline[nextEvent++];
                AdvanceNotes(item.Time, auto);
                if (item.LevelHold) IsLevelHeld = true;
                else if (item.Branch != null) SelectBranch(item.Branch);
                // Like OpenTaiko, a #SECTION on any route of a branch resets the counts.
                else branchFeed.Clear();
            }
            AdvanceNotes(time, auto);
        }

        void SelectBranch(ChartBranch branch)
        {
            double value = ForcedBranch.HasValue ? 0 : branchCounter.Value(branch);
            BranchRoute chosen;
            // A #LEVELHOLD reached, or an empty or unreadable condition, keeps the route played so
            // far (Normal at first).
            if (IsLevelHeld || branch.Condition == BranchCondition.None) chosen = CurrentBranch;
            // The thresholds of the route being played (fumen keeps a pair per route; TJA gives
            // every route the same). 達人 first, then 玄人, or below them with `l`.
            else chosen = branch.Choose(value, CurrentBranch);
            chosen = branch.ResolveRoute(ForcedBranch ?? chosen);
            if (branch.HoldRoutes[(int)chosen]) IsLevelHeld = true;
            selectedRoutes[branch.Id] = (int)chosen;
            // Counts carry on into the next branch; only #SECTION resets them (OpenTaiko).
            CurrentBranch = chosen; LastBranchValue = value; branchHistory.Add(chosen); Version++;
            BranchSelected?.Invoke(branch, chosen);
        }

        void AdvanceNotes(double time, bool auto)
        {
            // B changes manual judgment and timeout together; chart events and autoplay stay on A.
            if (!auto) time -= JudgeOffset;
            if (!timeSorted)
            {
                for (int i = 0; i < Chart.Notes.Count; i++) AdvanceNote(i, time, auto);
                return;
            }
            // Same index order as a full scan: every pending note is before head.
            revisit.Clear();
            foreach (int i in pending) if (AdvanceNote(i, time, auto)) revisit.Add(i);
            for (; head < Chart.Notes.Count && Chart.Notes[head].Time <= time; head++)
                if (AdvanceNote(head, time, auto)) revisit.Add(head);
            (pending, revisit) = (revisit, pending);
        }

        // Returns whether the note may still need a later visit: unresolved and active, or on
        // a branch not decided yet (it may become active once the branch is chosen).
        bool AdvanceNote(int i, double time, bool auto)
        {
            var note = Chart.Notes[i];
            if (resolved[i]) return false;
            if (!IsActive(note)) return selectedRoutes[note.BranchId] < 0;
            if (note.Time > time) return true;
            if (note.IsLong)
            {
                if (auto && time >= practiceStart)
                {
                    int expected = (int)(Math.Max(0, Math.Min(time, note.EndTime) - Math.Max(note.Time, practiceStart)) * 15) + 1;
                    while (!resolved[i] && longHits[i] < expected) HitLong(i, time);
                }
                if (time > note.EndTime && !resolved[i]) { resolved[i] = true; Version++; }
            }
            // Autoplay leaves a mine alone (OpenTaiko CanAutoplayHitMine is off by default).
            else if (auto && !note.IsMine) Resolve(i, Judgment.Good);
            // An Ad-Lib or mine nobody hit past its 可 window just goes: never 不可, never a miss.
            else if (note.IsAdLib || note.IsMine) { if (time - note.Time > okWindow) { resolved[i] = true; Version++; } }
            else if (time - note.Time > badWindow) { missed[i] = true; Resolve(i, Judgment.Bad); }
            return !resolved[i];
        }

        // How many hits Advance(time, true) will have made on long note `i` by `time`, counted from
        // its start (the same 15 a second; a balloon stops at its count).
        public int AutoLongHitsBy(int i, double time)
        {
            var note = Chart.Notes[i];
            if (!note.IsLong || note.Time > time || time < practiceStart) return 0;
            int hits = (int)(Math.Max(0, Math.Min(time, note.EndTime) - Math.Max(note.Time, practiceStart)) * 15) + 1;
            return note.IsBalloon && kusudamas[i] == null ? Math.Min(hits, note.BalloonHits) : hits;
        }

        public Judgment Hit(bool ka, double time)
        {
            Advance(time, false);
            time -= JudgeOffset;
            // check_note: don and ka are separate lanes, so a press judges the front of
            // its own colour's lane regardless of a pending note of the other colour.
            // A Kadon (G) is in both lanes.
            int target = NextInLane(ka, 0);
            if (target >= 0 && time > Chart.Notes[target].Time + okWindow)
            {
                // A front note already past 可 yields to the next note of its lane once that
                // one is within 可, unless any other pending note lies between them.
                int next = NextInLane(ka, target + 1);
                if (next >= 0 && time > Chart.Notes[next].Time - okWindow && !PendingBetween(target, next)) target = next;
            }
            bool kadonCompletes = target >= 0 && KadonWaits(target, time) && kadonFirstKa != ka;
            int nearest = kadonCompletes ? -1 : NearestTarget(target, time);
            // A mine hit is shown as 不可 (OpenTaiko sets the bomb's result to Bad).
            if (nearest >= 0 && Chart.Notes[nearest].IsMine) { Explode(nearest); return Judgment.Bad; }
            if (nearest >= 0)
            {
                double nearestDelta = Math.Abs(Chart.Notes[nearest].Time - time);
                Judgment nearestResult = nearestDelta <= goodWindow ? Judgment.Good : Judgment.Ok;
                double nearestOffsetMs = (time - Chart.Notes[nearest].Time) * 1000;
                timingOffsets[nearest] = nearestOffsetMs;
                hitTimings.Add(new HitTiming(nearest, nearestOffsetMs, nearestResult));
                Resolve(nearest, nearestResult); return nearestResult;
            }
            if (target >= 0)
            {
                // OpenTaiko's swap note: the other colour within KadonWait completes it, judged at the first hit.
                bool completes = KadonWaits(target, time) && kadonFirstKa != ka;
                double at = completes ? kadonFirstHit : time;
                double delta = Math.Abs(Chart.Notes[target].Time - at);
                if (delta <= badWindow)
                {
                    Judgment result = delta <= goodWindow ? Judgment.Good : delta <= okWindow ? Judgment.Ok : Judgment.Bad;
                    if (Chart.Notes[target].IsKadon && !completes && (result != Judgment.Bad || KadonWaits(target, time)))
                    {
                        // A good or ok first hit waits; the same colour again is an empty hit. A wait that
                        // ran out starts over, and the note is missed once its window passes (OpenTaiko).
                        if (!KadonWaits(target, time)) { kadonWaiting = target; kadonFirstHit = time; kadonFirstKa = ka; }
                        return Judgment.None;
                    }
                    if (kadonWaiting == target) kadonWaiting = -1;
                    double offsetMs = (at - Chart.Notes[target].Time) * 1000;
                    timingOffsets[target] = offsetMs;
                    hitTimings.Add(new HitTiming(target, offsetMs, result));
                    Resolve(target, result); return result;
                }
            }
            int roll = HittableLong(ka, time);
            if (roll >= 0) { HitLong(roll, time + JudgeOffset); return Judgment.Roll; }
            return Judgment.None;
        }

        // OpenTaiko's judged-from-nearest notes, the Ad-Lib and the mine: either colour, taken only
        // within 可 (a press in their 不可 range goes on as if they were not there). As
        // GetChipToJudgeIgnoringRollBody: the latest past one hides the older notes; a past note
        // after it within 可 comes first; past and future then go to the nearer when the past one is
        // an Ad-Lib or mine, else to the past note unless only it is 不可.
        // Returns the Ad-Lib or mine to judge, or -1 when the press goes to `target` as before.
        int NearestTarget(int target, double time)
        {
            int past = -1, future = -1;
            int from = 0;
            if (timeSorted)
            {
                foreach (int i in pending) Consider(i);
                from = head;
            }
            for (int i = from; i < Chart.Notes.Count && (!timeSorted || Chart.Notes[i].Time <= time + okWindow); i++) Consider(i);
            if (past < 0 && future < 0) return -1;

            double offset = target >= 0 ? Chart.Notes[target].Time - time : double.PositiveInfinity;
            bool note = Math.Abs(offset) <= badWindow, noteBad = Math.Abs(offset) > okWindow;
            bool notePast = note && offset <= 0 && (past < 0 || Chart.Notes[target].Time > Chart.Notes[past].Time);
            if (notePast && !noteBad) return -1;
            // The note is the first future one: future = -2.
            if (note && offset > 0 && (future < 0 || Chart.Notes[target].Time < Chart.Notes[future].Time)) future = -2;
            // Only a 不可 past note, or none: the first future one (-2, the note itself, gives -1).
            if (past < 0) return Math.Max(future, -1);
            if (future == -1) return past;
            double futureOffset = future == -2 ? offset : Chart.Notes[future].Time - time;
            return futureOffset < time - Chart.Notes[past].Time ? (future == -2 ? -1 : future) : past;

            void Consider(int i)
            {
                if (!Pending(i) || !(Chart.Notes[i].IsAdLib || Chart.Notes[i].IsMine)) return;
                double d = Chart.Notes[i].Time - time;
                if (Math.Abs(d) > okWindow) return;
                if (d <= 0) past = i;
                else if (future < 0) future = i;
            }
        }

        bool KadonWaits(int i, double time) => kadonWaiting == i && Pending(i) && time - kadonFirstHit < kadonWait;

        // A Kadon waits for this colour: a press of it in the same frame as the first is judged too.
        public bool AwaitsKadon(bool ka, double time) => kadonWaiting >= 0 && kadonFirstKa != ka && KadonWaits(kadonWaiting, time - JudgeOffset);

        bool Pending(int i) => !resolved[i] && IsActive(Chart.Notes[i]);
        bool CanHitLong(int i, bool ka, double time)
        {
            var n = Chart.Notes[i];
            return Pending(i) && n.IsLong && time >= Math.Max(n.Time, practiceStart) && time <= n.EndTime && (!n.IsBalloon || !ka);
        }

        // Earliest long note this press can count for. A hittable one has started, so it is
        // pending or among the started notes from head.
        int HittableLong(bool ka, double time)
        {
            int from = 0;
            if (timeSorted)
            {
                foreach (int i in pending) if (CanHitLong(i, ka, time)) return i;
                from = head;
            }
            for (int i = from; i < Chart.Notes.Count && (!timeSorted || Chart.Notes[i].Time <= time); i++)
                if (CanHitLong(i, ka, time)) return i;
            return -1;
        }

        // Earliest pending don (ka = false) or ka note from index start; big notes share the lane, Kadon both.
        // Ad-Libs and mines are left to NearestTarget.
        int NextInLane(bool ka, int start)
        {
            if (timeSorted)
            {
                foreach (int i in pending) if (i >= start && IsLaneNote(i, ka)) return i;
                start = Math.Max(start, head);
            }
            for (int i = start; i < Chart.Notes.Count; i++)
                if (IsLaneNote(i, ka)) return i;
            return -1;
        }
        bool IsLaneNote(int i, bool ka) => Pending(i) && !Chart.Notes[i].IsLong && !Chart.Notes[i].IsAdLib && !Chart.Notes[i].IsMine
            && (Chart.Notes[i].IsKa == ka || Chart.Notes[i].IsKadon);

        bool PendingBetween(int first, int last)
        {
            for (int i = first + 1; i < last; i++)
                if (Pending(i)) return true;
            return false;
        }

        // `time`: the chart time of the press (autoplay strikes on the unshifted clock).
        void HitLong(int i, double time)
        {
            var n = Chart.Notes[i]; longHits[i]++; Rolls++; Version++;
            branchFeed.Hit(n, scoring.AddLongHit());
            if (kusudamas[i] != null) kusudamas[i].Hit(this, time);
            else if (n.IsBalloon && longHits[i] == n.BalloonHits) resolved[i] = true;
            Judged?.Invoke(i, Judgment.Roll);
        }

        internal void SetKusudama(int index, Kusudama ball) => kusudamas[index] = ball;
        // The ball broke: the member still in play is done (a route not played stays as it is).
        internal void Break(int index)
        {
            if (!Pending(index)) return;
            resolved[index] = true; Version++;
        }
        // Another seat hit a ball shared with this session.
        internal void Touch() => Version++;

        // OpenTaiko's bomb hit: the combo breaks and the gauge takes MineDamage, but it scores
        // nothing and is not counted as 不可 (nor by branch conditions). Shown as 不可.
        void Explode(int i)
        {
            resolved[i] = exploded[i] = true; Version++;
            MinesHit++; Combo = 0;
            expressionOpenStart = expressionFastStart = expressionAngryStart = double.PositiveInfinity;
            gauge.ApplyMine();
            Judged?.Invoke(i, Judgment.Bad);
        }

        void Resolve(int i, Judgment result)
        {
            resolved[i] = true; Version++;
            if (result == Judgment.Bad)
            {
                Bad++; Combo = 0;
                expressionOpenStart = expressionFastStart = expressionAngryStart = double.PositiveInfinity;
            }
            else
            {
                if (Chart.Notes[i].IsAdLib) { if (result == Judgment.Good) AdLibGood++; else AdLibOk++; }
                else if (result == Judgment.Good) Good++; else Ok++;
                Combo++; MaxCombo = Math.Max(MaxCombo, Combo);
                if (Combo == NoteExpression.OpenCombo) expressionOpenStart = NoteExpression.StartBeat(Chart.Notes[i].Beat);
                if (Combo == NoteExpression.FastCombo) expressionFastStart = NoteExpression.StartBeat(Chart.Notes[i].Beat);
                if (Combo == NoteExpression.AngryCombo) expressionAngryStart = NoteExpression.StartBeat(Chart.Notes[i].Beat);
            }
            int points = scoring.ApplyJudgment(result);
            gauge.ApplyJudgment(result, GaugeRoute(i));
            branchFeed.Judge(Chart.Notes[i], result, points);
            Judged?.Invoke(i, result);
        }
    }
}
