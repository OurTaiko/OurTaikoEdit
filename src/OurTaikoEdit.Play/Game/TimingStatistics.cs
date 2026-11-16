using System;
using System.Collections.Generic;
using System.Linq;

namespace OurTaiko
{
    public readonly struct HitTiming
    {
        public readonly int NoteIndex;
        public readonly double OffsetMs;
        public readonly Judgment Judgment;
        public HitTiming(int noteIndex, double offsetMs, Judgment judgment)
        { NoteIndex = noteIndex; OffsetMs = offsetMs; Judgment = judgment; }
    }

    // A detached snapshot of actual manual note hits. Misses have no measured timing.
    public sealed class TimingStatistics
    {
        public const double BinWidthMs = 5, MinimumMs = -132.5, MaximumMs = 132.5;
        public const int BinCount = 53;
        public IReadOnlyList<HitTiming> Samples { get; }
        public IReadOnlyList<int> Bins { get; }
        public int Count => Samples.Count;
        public int Misses { get; }
        public int Early { get; }
        public int Late { get; }
        public int Exact { get; }
        public double? MeanMs { get; }
        public double? StandardDeviationMs { get; }
        public double? MeanAbsoluteMs { get; }
        public double GoodWindowMs { get; }
        public double OkWindowMs { get; }
        public double BadWindowMs { get; }

        public TimingStatistics(IEnumerable<HitTiming> samples, int misses, double goodWindowMs,
            double okWindowMs, double badWindowMs)
        {
            Samples = Array.AsReadOnly(samples.ToArray());
            Misses = misses;
            GoodWindowMs = goodWindowMs; OkWindowMs = okWindowMs; BadWindowMs = badWindowMs;
            var bins = new int[BinCount];
            double mean = 0, m2 = 0, absolute = 0;
            int count = 0;
            foreach (var sample in Samples)
            {
                double value = sample.OffsetMs;
                if (double.IsNaN(value) || double.IsInfinity(value) || value < MinimumMs || value > MaximumMs)
                    throw new ArgumentOutOfRangeException(nameof(samples), "Timing is outside the supported judgment windows.");
                if (value < 0) Early++; else if (value > 0) Late++; else Exact++;
                double delta = value - mean;
                mean += delta / ++count;
                m2 += delta * (value - mean);
                absolute += Math.Abs(value);
                int bin = Math.Min(BinCount - 1, (int)Math.Floor((value - MinimumMs) / BinWidthMs));
                bins[bin]++;
            }
            Bins = Array.AsReadOnly(bins);
            if (count == 0) return;
            MeanMs = mean;
            MeanAbsoluteMs = absolute / count;
            if (count > 1) StandardDeviationMs = Math.Sqrt(Math.Max(0, m2 / count));
        }

        public static TimingStatistics From(PlaySession session) => new TimingStatistics(session.HitTimings,
            session.Missed.Where((value, i) => value && !session.Skipped[i]).Count(),
            session.GoodWindowMs, session.OkWindowMs, session.BadWindowMs);
    }
}
