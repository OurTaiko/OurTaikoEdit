using System;

namespace OurTaiko
{
    // Nijiiro note faces: below 10 combo the notes rest (small notes closed, big notes angry and
    // open). From 10 combo all notes open and close their mouths together every eighth note,
    // from 50 combo every sixteenth, and from 100 combo the small notes' open face is the angry
    // one. A miss returns them to rest.
    public static class NoteExpression
    {
        public const int Rest = 0, Open = 1, Angry = 2, Closed = 3;
        public const int OpenCombo = 10, FastCombo = 50, AngryCombo = 100;
        const double BeatTolerance = 1e-7;

        public static double StartBeat(double noteBeat) => Math.Ceiling(noteBeat - BeatTolerance);

        // Each tier starts on its threshold note's beat, rounded up to a whole beat;
        // until then the tier before it keeps running.
        public static int Frame(double beat, int combo, double openStart, double fastStart, double angryStart)
        {
            if (double.IsNaN(beat) || double.IsInfinity(beat)) return Rest;
            bool Reached(int threshold, double start) => combo >= threshold && beat + BeatTolerance >= start;
            double start; int perBeat, face;
            if (Reached(AngryCombo, angryStart)) { start = angryStart; perBeat = 4; face = Angry; }
            else if (Reached(FastCombo, fastStart)) { start = fastStart; perBeat = 4; face = Open; }
            else if (Reached(OpenCombo, openStart)) { start = openStart; perBeat = 2; face = Open; }
            else return Rest;
            double step = Math.Max(0, Math.Floor((beat - start + BeatTolerance) * perBeat));
            return step % 2 == 0 ? face : Closed;
        }
    }
}
