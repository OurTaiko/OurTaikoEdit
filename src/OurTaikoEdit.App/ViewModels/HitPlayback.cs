using OurTaiko;
using OurTaikoEdit.Audio;

namespace OurTaikoEdit.App.ViewModels;

// The don and ka sounds of playback: each note's sound is given to the player once, as the song
// gets to the note.
//
// A sound started when the playhead reaches its note is late: the timer that notices comes up to
// a tick after, the device then starts the sound with the next block it mixes, and — where the
// playhead follows what is heard — the sound is itself heard one output latency later. So, as
// OpenSekai and OurTaikoPlay start auto play's note sounds one output latency early, here the
// sounds go by the song's own time: `now` is the place of the song the device is being handed
// (the playhead plus the latency), and while the song plays each hit is asked for Lookahead
// before that place reaches its note, for the player to start on the note's very sample. What is
// heard is then the song with its hits on it, when the playhead crosses the note.
public sealed class HitPlayback
{
    // More than a timer tick and a block of the device together, so the player always has the
    // request before the device gets there.
    public const double Lookahead = 0.06;

    // The time of the song up to which the hits have been given out.
    double done = double.NegativeInfinity;

    // Playback starts or jumps: nothing at or before this time of the song is played.
    public void StartAt(double time) => done = time;

    // Gives out the hits of the notes up to `now` on a route, or up to Lookahead past it while
    // the song is playing. With `on` false they are passed over.
    public void Advance(TaikoChart chart, BranchRoute route, IAudioPlayer player, double now, bool on)
    {
        bool ahead = player.IsPlaying;
        double from = done, to = ahead ? now + Lookahead : now;
        if (to <= from) return;
        done = to;
        // After a hold-up, what was passed over is not all played at once.
        if (!on || to - from > 0.25) return;
        foreach (var n in chart.Notes)
        {
            if (n.Time <= from) continue;
            if (n.Time > to) break;
            if (n.BranchId >= 0 && !(n.BranchId < chart.Branches.Count && chart.Branches[n.BranchId].ResolveRoute(route) == n.Route)) continue;
            if (n.IsLong || n.IsAdLib || n.IsMine) continue;
            if (n.IsKadon) { Play(player, ahead, n.Time, false, true); Play(player, ahead, n.Time, true, true); }
            else Play(player, ahead, n.Time, n.IsKa, n.IsBigNote);
        }
    }

    static void Play(IAudioPlayer player, bool ahead, double time, bool ka, bool big)
    {
        if (ahead) player.PlayHitAt(time, ka, big);
        else player.PlayHit(ka, big);
    }
}
