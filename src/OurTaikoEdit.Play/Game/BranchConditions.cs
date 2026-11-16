using System;

namespace OurTaiko
{
    // Play events a branch condition counts, raised by PlaySession as each result is applied
    // within the game loop's update. Only #SECTION raises Reset, as in OpenTaiko.
    public sealed class BranchFeed
    {
        // A normal note's result and the points it scored.
        public event Action<ChartNote, Judgment, int> NoteJudged;
        // One drumroll or balloon hit and the points it scored.
        public event Action<ChartNote, int> LongHit;
        public event Action Reset;

        internal void Judge(ChartNote note, Judgment result, int points) => NoteJudged?.Invoke(note, result, points);
        internal void Hit(ChartNote note, int points) => LongHit?.Invoke(note, points);
        internal void Clear() => Reset?.Invoke();
    }

    // Counts play since the last reset and reports the value of a #BRANCHSTART condition,
    // compared against its expert and master thresholds when the branch is decided.
    // Big-only conditions (OpenTaiko's upper-case types) read a second tally of big notes,
    // big drumrolls and kusudama.
    public sealed class BranchCounter
    {
        sealed class Tally
        {
            public int Good, Ok, Bad, Rolls, Balloons;
            public void Clear() => Good = Ok = Bad = Rolls = Balloons = 0;
        }

        readonly Tally all = new Tally(), big = new Tally();
        int score;

        public BranchCounter(BranchFeed feed)
        {
            feed.NoteJudged += (note, result, points) =>
            {
                Judge(all, result);
                if (note.IsBigNote) Judge(big, result);
                score += points;
            };
            feed.LongHit += (note, points) =>
            {
                bool isBig = note.Kind == NoteKind.BigRoll || note.Kind == NoteKind.Kusudama;
                // r counts every roll hit, balloons and kusudama included; rb only the balloons.
                foreach (var tally in isBig ? new[] { all, big } : new[] { all })
                {
                    tally.Rolls++;
                    if (note.IsBalloon) tally.Balloons++;
                }
                score += points;
            };
            // OpenTaiko's s reads the total score, which #SECTION does not reset.
            feed.Reset += () => { all.Clear(); big.Clear(); };
        }

        static void Judge(Tally tally, Judgment result)
        {
            if (result == Judgment.Good) tally.Good++;
            else if (result == Judgment.Ok) tally.Ok++;
            else tally.Bad++;
        }

        // Truncated percentage, as the reference player compares p.
        static double Percent(double points, int notes) => notes == 0 ? 0 : Math.Max(0, Math.Min(100, (int)(points / notes * 100)));

        public double Value(ChartBranch branch)
        {
            var t = branch.BigOnly ? big : all;
            int notes = t.Good + t.Ok + t.Bad;
            switch (branch.Condition)
            {
                // p: 良 = 1, 可 = 0.5, 不可 and misses = 0.
                case BranchCondition.Accuracy: return Percent(t.Good + t.Ok * 0.5, notes);
                // pp: share of 良. OpenTaiko divides integers here (always 0 or 100); this is the intended ratio.
                case BranchCondition.PercentPerfect: return Percent(t.Good, notes);
                case BranchCondition.JudgePerfect: return t.Good;
                case BranchCondition.JudgeGood: return t.Ok;
                case BranchCondition.JudgeBad: return t.Bad;
                // r: drumroll, balloon and kusudama hits. The judgement waits for a roll ending in
                // the last measure.
                case BranchCondition.Drumroll: return t.Rolls;
                case BranchCondition.BalloonHits: return t.Balloons;
                // s: the score so far, from notes, drumrolls and balloons alike.
                case BranchCondition.Score: return score;
                default: return 0;
            }
        }
    }
}
