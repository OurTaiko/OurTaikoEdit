using System;
using System.Collections.Generic;

namespace OurTaiko
{
    // Fixed chart totals used by the reference player, regardless of the route selected in play.
    public sealed class ChartStatistics
    {
        public int JudgeableNotes { get; }
        // Judgeable notes on each whole route (Normal, Expert, Master): OpenTaiko nノーツ数_Branch,
        // which sizes the soul gauge increments of that route.
        public IReadOnlyList<int> RouteNotes { get; }
        public int BalloonHitBudget { get; }
        public double DrumrollMilliseconds { get; }

        public ChartStatistics(TaikoChart chart)
        {
            if (chart == null) throw new ArgumentNullException(nameof(chart));
            int judgeableNotes = 0, balloonHitBudget = 0;
            double drumrollMilliseconds = 0;
            // Player::reset_chart aggregates the common list followed by every Master section.
            // Preserve that order for the source's double accumulation of roll durations.
            Accumulate(-1);
            for (int branch = 0; branch < chart.Branches.Count; branch++) Accumulate(branch);
            JudgeableNotes = judgeableNotes;
            var routeNotes = new int[3];
            foreach (var note in chart.Notes)
            {
                if (!IsJudgeable(note)) continue;
                for (int route = 0; route < 3; route++)
                    if (note.BranchId < 0 || note.Route == chart.Branches[note.BranchId].ResolveRoute((BranchRoute)route)) routeNotes[route]++;
            }
            RouteNotes = routeNotes;
            BalloonHitBudget = balloonHitBudget;
            DrumrollMilliseconds = drumrollMilliseconds;

            void Accumulate(int branchId)
            {
                foreach (var note in chart.Notes)
                {
                    // A branch without #M counts the route drawn in its place.
                    if (note.BranchId != branchId || (branchId >= 0 && note.Route != chart.Branches[branchId].ResolveRoute(BranchRoute.Master))) continue;
                    if (IsJudgeable(note)) judgeableNotes++;
                    else if (note.IsBalloon) balloonHitBudget += Math.Min(100, note.BalloonHits);
                    else if (note.IsDrumroll)
                        drumrollMilliseconds += (note.EndTime - note.Time) * 1000;
                }
            }
        }

        // Notes 1–4 and the Kadon (G): judged 良/可/不可 and counted for score and gauge. An Ad-Lib (F)
        // is not: hit, it scores and fills the gauge on top, as a bonus. Nor is a mine (C): hit, it
        // only costs.
        static bool IsJudgeable(ChartNote note) => note.Kind >= NoteKind.Don && note.Kind <= NoteKind.BigKa || note.IsKadon;
    }
}
