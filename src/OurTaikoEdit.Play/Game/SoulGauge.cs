using System;
using System.Collections.Generic;

namespace OurTaiko
{
    // Numeric gauge only; SoulGaugeView owns cells, skin art and animations.
    // OpenTaiko's normal gauge (CAct演奏ゲージ共通.Init and Damage): every route has its own
    // increments from that route's note count, worked out in float as OpenTaiko does, and the
    // gauge is a 0–100 value rounded to five decimals after each judgment.
    public sealed class SoulGauge
    {
        public const int MaximumPoints = 10000;
        public const double PointsEpsilon = 1e-6;
        // db現在のゲージ値; Points scales it to the 10000-point gauge the views and results use.
        double percent;
        // [route, 良／可／不可], in percent.
        readonly float[,] increments = new float[3, 3];
        public double Points => Math.Round(percent * 100, 3);
        public double Value => percent / 100;
        public int Percent => (int)Math.Floor(percent + PointsEpsilon);
        public int ClearPoints { get; }
        public double ClearThreshold => (double)ClearPoints / MaximumPoints;
        public bool IsClear => Points >= ClearPoints;
        public bool IsFull => Points >= MaximumPoints;

        public SoulGauge(int judgeableNotes, string course, int level)
            : this(new[] { judgeableNotes, judgeableNotes, judgeableNotes }, course, level) { }

        // routeNotes: the judgeable notes played on the Normal, Expert and Master routes.
        public SoulGauge(IReadOnlyList<int> routeNotes, string course, int level)
            : this(routeNotes, SoulGaugeRules.ForChart(course, level)) { }

        SoulGauge(IReadOnlyList<int> routeNotes, SoulGaugeRules rules)
        {
            if (routeNotes == null || routeNotes.Count != 3) throw new ArgumentException("A gauge takes the notes of three routes.", nameof(routeNotes));
            ClearPoints = rules.ClearPoints;
            float[] volume = { 1.0f, 0.5f, -Math.Abs(rules.DamageRate) };
            // A route without notes takes the last finite route's increments ×3 (0.4 before any).
            float finite = 0.4f;
            for (int route = 0; route < 3; route++)
            {
                float maxCombo = routeNotes[route] * (rules.GaugeRate / 100.0f);
                double rank = 10000.0f / maxCombo;
                bool infinite = double.IsInfinity(rank / 100.0f);
                if (!infinite) finite = (float)(rank / 100.0f);
                for (int j = 0; j < 3; j++)
                {
                    float increment = infinite ? finite * volume[j] * 3f : finite * volume[j];
                    // GAUGEINCR's default (Normal) truncates to four decimals.
                    increments[route, j] = MathF.Truncate(increment * 10000.0f) / 10000.0f;
                }
            }
        }

        // A 段位 course is one gauge over every song with no norma zone (it is full or it is not).
        // OpenTaiko reads one LEVEL for the whole course; ours has a level per song, so the rate is
        // the harmonic mean of the songs' and the miss cost their average.
        public static SoulGauge Dan(IReadOnlyList<(string Course, int Level)> songs, IReadOnlyList<int> routeNotes)
        {
            if (songs == null || songs.Count == 0) throw new ArgumentException("A course has songs.", nameof(songs));
            float inverseSum = 0, damage = 0;
            foreach (var (course, level) in songs)
            {
                var rules = SoulGaugeRules.ForChart(course, level);
                inverseSum += 1 / rules.GaugeRate;
                damage += rules.DamageRate / songs.Count;
            }
            return new SoulGauge(routeNotes, new SoulGaugeRules(songs.Count / inverseSum, damage, MaximumPoints));
        }

        public float Increment(Judgment judgment, BranchRoute route = BranchRoute.Normal)
        {
            switch (judgment)
            {
                case Judgment.Good: return increments[(int)route, 0];
                case Judgment.Ok: return increments[(int)route, 1];
                case Judgment.Bad: return increments[(int)route, 2];
                default: return 0;
            }
        }

        // A mine hit (OpenTaiko MineDamage under the default BOOMRULE, SCAL 4): four percent off,
        // whatever the course and route.
        public const float MineDamage = 4f;
        public void ApplyMine()
        {
            percent = Math.Round(percent - MineDamage, 5, MidpointRounding.ToEven);
            percent = Math.Max(0.0, Math.Min(100.0, percent));
        }

        // route: the note's own route inside a branch section, else the route being played.
        public void ApplyJudgment(Judgment judgment, BranchRoute route = BranchRoute.Normal)
        {
            if (judgment != Judgment.Good && judgment != Judgment.Ok && judgment != Judgment.Bad) return;
            percent = Math.Round(percent + Increment(judgment, route), 5, MidpointRounding.ToEven);
            percent = Math.Max(0.0, Math.Min(100.0, percent));
        }
    }
}
