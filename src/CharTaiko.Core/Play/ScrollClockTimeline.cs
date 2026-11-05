// Ported from OurTaikoPlay Assets/OurTaiko/Runtime/Core/ScrollClockTimeline.cs as of main 39ca3cf (GPL-3.0, same as CharTaiko)
// so the lane preview places notes exactly as the game does. Keep in step with the game; these
// belong in TJAParser once the game and View_Web take them from there.
using System;
using System.Linq;

using OurTaiko;

namespace CharTaiko.Core.Play
{
    // The beat clock BM/HB-scroll notes are placed by (OpenTaiko GetNowPBPMPoint / GetNowPBMTime),
    // one per route: from the latest BPM point at or before a time (both in whole milliseconds) it
    // runs at that point's BPM from the point's unrounded time; before the first point it runs back
    // from it. A branch's end resets every route to the clock of the route written last, so a
    // route's clock may jump there.
    public sealed class ScrollClockTimeline
    {
        readonly ChartClockPoint[][] routes = new ChartClockPoint[3][];
        readonly long offsetMs;

        public ScrollClockTimeline(TaikoChart chart)
        {
            offsetMs = (long)Math.Round(chart.Offset * 1000);
            // A chart built without TjaParser has no points: its clock starts at 0 s.
            var points = chart.ScrollClocks.Count > 0 ? chart.ScrollClocks.ToArray()
                : new[] { new ChartClockPoint { RawMs = offsetMs, Bpm = chart.Bpm } };
            for (int r = 0; r < 3; r++)
                routes[r] = points.Where(p => p.BranchId < 0 || (int)p.Route == r).ToArray();
        }

        // OpenTaiko plays only the Normal copy of a common note, so it reads the Normal clock.
        public static BranchRoute RouteOf(ChartNote note) => note.BranchId < 0 ? BranchRoute.Normal : note.Route;

        // In measures of four beats.
        public double At(BranchRoute route, double time) => At16(route, time) / 16;

        // In sixteenth notes, as OpenTaiko computes it, at the time's whole millisecond.
        public double At16(BranchRoute route, double time)
        {
            var points = routes[(int)route];
            long now = TjaTime.Ms(time);
            int low = 0, high = points.Length;
            while (low < high)
            {
                int mid = low + (high - low) / 2;
                if (TjaTime.Ms(points[mid].Time) <= now) low = mid + 1;
                else high = mid;
            }
            var point = points[Math.Max(0, low - 1)];
            return point.Clock * 16 + ((double)(now + offsetMs) - point.RawMs) * point.Bpm / 15000.0;
        }

        // The span of times during which the route's clock is within [from, to]. Where the clock
        // jumps back the true set has gaps; the span covers them, which is enough for culling.
        public void Span(BranchRoute route, double from, double to, out double start, out double end)
        {
            var points = routes[(int)route];
            start = double.PositiveInfinity; end = double.NegativeInfinity;
            for (int i = 0; i < points.Length; i++)
            {
                var point = points[i];
                double segmentStart = i == 0 ? double.NegativeInfinity : point.Time;
                double segmentEnd = i + 1 < points.Length ? points[i + 1].Time : double.PositiveInfinity;
                // Whole milliseconds move each end by under 1 ms; widen by that much.
                double enter = point.Time + (from - point.Clock) * 240 / point.Bpm - 0.002;
                double leave = point.Time + (to - point.Clock) * 240 / point.Bpm + 0.002;
                enter = Math.Max(enter, segmentStart - 0.002); leave = Math.Min(leave, segmentEnd + 0.002);
                if (!(enter <= leave)) continue;
                start = Math.Min(start, enter); end = Math.Max(end, leave);
            }
        }
    }
}
