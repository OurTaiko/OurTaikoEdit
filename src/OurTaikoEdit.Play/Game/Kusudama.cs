using System;
using System.Collections.Generic;
using System.Linq;

namespace OurTaiko
{
    // One kusudama ball (OpenTaiko's KusudamaCount / KusudamaRollCount on the first linked chip). Alone it
    // is one 9 note; in two-player play each seat's kusudama at the same time (heads and tails within
    // 100 ms, ReduceMultiplayerNotes) is the same ball. Every seat's don counts toward the sum of the
    // members' BALLOON counts, and the hit that reaches it breaks the ball for every seat at once. Each
    // seat keeps its own roll count and score for its own hits. Unbroken, every member just ends at its tail.
    public sealed class Kusudama
    {
        // OpenTaiko's msMatchErrorLimit.
        public const double MatchWindow = 0.1;

        readonly List<(PlaySession Session, int Index)> members = new List<(PlaySession, int)>();
        public IReadOnlyList<(PlaySession Session, int Index)> Members => members;
        // Shared don hits so far, from every seat.
        public int Hits { get; private set; }
        public bool IsBroken { get; private set; }
        // Chart times (before the judge offset) of the first and the latest hit and of the breaking one,
        // NaN until then, and the seat's session that broke it.
        public double FirstHitAt { get; private set; } = double.NaN;
        public double LastHitAt { get; private set; } = double.NaN;
        public double BrokenAt { get; private set; } = double.NaN;
        public PlaySession BrokenBy { get; private set; }

        internal Kusudama(PlaySession session, int index) => members.Add((session, index));

        // The members on the routes being played; a route not chosen yet counts nothing.
        public int Required
        {
            get
            {
                int sum = 0;
                foreach (var (session, index) in members)
                    if (session.IsActive(session.Chart.Notes[index])) sum += session.Chart.Notes[index].BalloonHits;
                return sum;
            }
        }
        public int Remaining => Math.Max(0, Required - Hits);

        // A member still taking hits at its seat's chart time: active, reached and neither broken nor past its tail.
        public bool IsRolling(PlaySession session, double time)
        {
            foreach (var (member, index) in members)
            {
                if (member != session) continue;
                var note = member.Chart.Notes[index];
                if (member.IsActive(note) && time >= note.Time && !member.Resolved[index]) return true;
            }
            return false;
        }

        // A don from `session` at chart time `time`; a ball with nothing to count (BALLOON 0) never breaks.
        internal void Hit(PlaySession session, double time)
        {
            Hits++;
            if (double.IsNaN(FirstHitAt)) FirstHitAt = time;
            LastHitAt = time;
            int required = Required;
            if (required > 0 && Hits >= required && !IsBroken)
            {
                IsBroken = true; BrokenAt = time; BrokenBy = session;
                foreach (var (member, index) in members) member.Break(index);
            }
            // The other seats redraw too: their ball's count changed.
            foreach (var (member, _) in members) if (member != session) member.Touch();
        }

        // Two-player play: links each kusudama of one seat with the other seat's kusudama at the same time
        // (any route). A kusudama with no partner plays as an ordinary balloon, as OpenTaiko downgrades it.
        public static void Share(PlaySession first, PlaySession second)
        {
            if (first == null || second == null) throw new ArgumentNullException(first == null ? nameof(first) : nameof(second));
            var balls = new List<Kusudama>();
            foreach (var (session, index) in Candidates(first, second))
            {
                var note = session.Chart.Notes[index];
                Kusudama ball = null;
                foreach (var candidate in balls)
                {
                    var (s, i) = candidate.members[0];
                    var lead = s.Chart.Notes[i];
                    if (Math.Abs(lead.Time - note.Time) < MatchWindow && Math.Abs(lead.EndTime - note.EndTime) < MatchWindow) { ball = candidate; break; }
                }
                if (ball == null) balls.Add(ball = new Kusudama(session, index));
                else ball.members.Add((session, index));
                session.SetKusudama(index, ball);
            }
            foreach (var ball in balls)
            {
                bool shared = false;
                foreach (var (session, _) in ball.members) shared |= session != ball.members[0].Session;
                if (shared) continue;
                foreach (var (session, index) in ball.members)
                {
                    var note = session.Chart.Notes[index];
                    note.Kind = NoteKind.Balloon; note.Moji = NoteMoji.Balloon;
                    session.SetKusudama(index, null);
                }
            }
        }

        static IEnumerable<(PlaySession, int)> Candidates(PlaySession first, PlaySession second)
        {
            var all = new List<(PlaySession Session, int Index)>();
            foreach (var session in new[] { first, second })
                for (int i = 0; i < session.Chart.Notes.Count; i++)
                    if (session.Chart.Notes[i].Kind == NoteKind.Kusudama) all.Add((session, i));
            // Stable: 1P's notes come first at a time, then 2P's.
            return all.OrderBy(m => m.Session.Chart.Notes[m.Index].Time);
        }
    }
}
