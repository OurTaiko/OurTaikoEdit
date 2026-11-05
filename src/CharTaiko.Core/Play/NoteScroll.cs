// Ported from OurTaikoPlay Assets/OurTaiko/Runtime/Core/NoteScroll.cs as of main 39ca3cf (GPL-3.0, same as CharTaiko)
// so the lane preview places notes exactly as the game does. Keep in step with the game; these
// belong in TJAParser once the game and View_Web take them from there.
using OurTaiko;

namespace CharTaiko.Core.Play
{
    public static class NoteScroll
    {
        // Nijiiro's native 1920-wide field: judge X=618 and 192-pixel notes.
        public const double DefaultTravelDistance = 1302, NoteHalfWidth = 96;
        // How far a note moves in one measure (four beats) at SCROLL 1, in canvas units: OpenTaiko's
        // Game_Notes_Interval 960 on its 1280-wide layout, i.e. 75% of the 1920 design width. The
        // canvas scales with the window, so this is a proportion of it, not of the lane: a wider
        // window shows more lane at the same speed.
        public const double MeasureWidth = 0.75 * 1920;

        // Same entry boundary as Player::get_load_time, including the sprite radius.
        public static double LoadTime(ChartNote note)
        {
            double scroll = System.Math.Abs(note.ScrollX);
            if (scroll == 0) scroll = System.Math.Abs(note.ScrollY);
            double speed = note.Bpm / 240.0 * scroll * DefaultTravelDistance;
            return speed == 0 ? note.Time : note.Time - (DefaultTravelDistance + NoteHalfWidth) / speed;
        }

        // OurTaikoPlayer Player::get_position_x/y uses BPM / 240000 in milliseconds.
        // Our clock is seconds: at SCROLL 1, four beats span the right edge-to-judge distance.
        public static double DistanceFromJudge(double hitTime, double currentTime, double bpm,
            double scroll, double travelDistance)
        {
            return (hitTime - currentTime) * bpm / 240.0 * scroll * travelDistance;
        }

        // A note's distance from the judge on both axes, as OpenTaiko places it for its scroll mode
        // (UpdateScrolledChipPosition, NotesManager.GetNoteX/Y): in whole milliseconds and cut to
        // whole pixels. NM by its own time and BPM; BM and HB by the beat clock in sixteenths, BM
        // with SCROLL 1 and no vertical part, HB with its SCROLL.
        // #SUDDEN: until the note starts to move its horizontal place stays where it will start;
        // the vertical part moves as usual (TJAP3, kept by OpenTaiko).
        public static (double X, double Y) Distance(ChartNote note, double currentTime, ScrollClockTimeline clocks,
            double travelDistance)
            => Place(note, currentTime, clocks, travelDistance, note.Time, note.ScrollClock, Standing(note, currentTime));

        // The same without #SUDDEN's standing still.
        public static (double X, double Y) RawDistance(ChartNote note, double currentTime, ScrollClockTimeline clocks,
            double travelDistance)
            => Place(note, currentTime, clocks, travelDistance, note.Time, note.ScrollClock, false);

        // A long note's tail, placed on its own by the head's BPM, SCROLL and mode, and cut to
        // whole pixels on its own, as OpenTaiko places the roll-end chip; with a vertical SCROLL it
        // is off the head's line, and the roll is drawn turned toward it.
        public static (double X, double Y) TailDistance(ChartNote note, double currentTime, ScrollClockTimeline clocks, double travelDistance)
            => Place(note, currentTime, clocks, travelDistance, note.EndTime, note.EndScrollClock, Standing(note, currentTime));

        // A note is drawn (with its text) once its #SUDDEN appearance time has come.
        public static bool Appeared(ChartNote note, double currentTime)
            => !(note.SuddenHidesRoll && note.IsLong) && currentTime >= note.Time - note.SuddenShow;

        static bool Standing(ChartNote note, double currentTime)
            => !double.IsPositiveInfinity(note.SuddenMove) && TjaTime.Ms(currentTime) < TjaTime.Ms(note.Time) - TjaTime.Ms(note.SuddenMove);

        // `time`/`clock` are the head's or the tail's; `standing` moves it as from the head's start.
        static (double X, double Y) Place(ChartNote note, double currentTime, ScrollClockTimeline clocks, double travel,
            double time, double clock, bool standing)
        {
            long now = TjaTime.Ms(currentTime), head = TjaTime.Ms(note.Time), at = TjaTime.Ms(time);
            if (note.ScrollMode == ScrollMode.Normal)
            {
                double ms = at - now;
                double msMove = standing ? TjaTime.Ms(note.SuddenMove) + (at - head) : ms;
                return (Cut(note.ScrollX * msMove * note.Bpm / 240000.0 * travel),
                    note.ScrollY == 0 ? 0 : Cut(note.ScrollY * ms * note.Bpm / 240000.0 * travel));
            }
            var route = ScrollClockTimeline.RouteOf(note);
            double sixteenths = clock * 16 - clocks.At16(route, currentTime);
            // th16DBeatPreMove: the clock from the start of moving to the head's own time.
            double sixteenthsMove = standing
                ? clocks.At16(route, note.Time) - clocks.At16(route, note.Time - note.SuddenMove) + (clock - note.ScrollClock) * 16
                : sixteenths;
            double scrollY = ScrollY(note);
            return (Cut(ScrollX(note) * sixteenthsMove / 16 * travel), scrollY == 0 ? 0 : Cut(scrollY * sixteenths / 16 * travel));
        }

        // OpenTaiko's (int) cast of a pixel distance.
        static double Cut(double pixels) => double.IsNaN(pixels) || double.IsInfinity(pixels) ? pixels : System.Math.Truncate(pixels);

        // The SCROLL a note moves by: BM-scroll ignores the chart's (OpenTaiko forces 1 and no Y).
        public static double ScrollX(ChartNote note) => note.ScrollMode == ScrollMode.BMScroll ? 1 : note.ScrollX;
        public static double ScrollY(ChartNote note) => note.ScrollMode == ScrollMode.BMScroll ? 0 : note.ScrollY;

        // A roll is a rigid body: its tail inherits the head's BPM and SCROLL (and scroll mode),
        // even when chart commands change before the tail. Its length stays constant.
        public static double RollLength(ChartNote note, double travelDistance)
        {
            if (note.ScrollMode != ScrollMode.Normal)
                return (note.EndScrollClock - note.ScrollClock) * ScrollX(note) * travelDistance;
            return DistanceFromJudge(note.EndTime, note.Time, note.Bpm, note.ScrollX, travelDistance);
        }
    }
}
