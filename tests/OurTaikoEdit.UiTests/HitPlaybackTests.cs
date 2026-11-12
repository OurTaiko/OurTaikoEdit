using OurTaiko;
using OurTaikoEdit.App.ViewModels;
using OurTaikoEdit.Audio;
using OurTaikoEdit.Tja;

namespace OurTaikoEdit.UiTests;

// The hit sounds of playback go by the song's own time, asked for ahead of it.
public class HitPlaybackTests
{
    sealed class Player : IAudioPlayer
    {
        // What was asked for: the song's time for a hit asked for ahead, null for one started at once.
        public readonly List<(double? At, bool Ka, bool Big)> Hits = new();
        public int Cancelled;
        public double Duration => 600;
        public bool IsPlaying { get; set; }
        public double Position { get; set; }
        public float Volume { get; set; }
        public float HitVolume { get; set; }
        public double OutputLatency => 0.027;
        public void Play() => IsPlaying = true;
        public void Pause() => IsPlaying = false;
        public void Seek(double seconds) => Position = seconds;
        public void PlayHit(bool ka, bool big) => Hits.Add((null, ka, big));
        public void PlayHitAt(double songTime, bool ka, bool big) => Hits.Add((songTime, ka, big));
        public void CancelHits() => Cancelled++;
        public void SetHitSounds(PcmAudio don, PcmAudio ka) { }
        public void Dispose() { }
    }

    // 120 BPM, a measure of 2 s from 0: a don at 0, a ka at 0.25, a big don at 0.5, a drumroll from
    // 1 to the next measure (no hit sound), then a don at 2.5.
    static TaikoChart Chart() => new TjaLanguage().Analyze("BPM:120\nOFFSET:0\nCOURSE:Oni\n#START\n1020300050000000,\n8000100000000000,\n#END\n", 0).Chart!;

    [Fact]
    public void WhileTheSongPlaysEachHitIsAskedForAheadForItsNotesOwnTime()
    {
        var chart = Chart();
        var player = new Player { IsPlaying = true };
        var hits = new HitPlayback();
        hits.StartAt(-0.2);
        // The timer every 15 ms, over the first measure and a little more.
        for (double now = -0.2; now < 2.2; now += 0.015) hits.Advance(chart, BranchRoute.Normal, player, now, true);
        // Each note once, for the time it is written at, whatever the ticks fell on.
        Assert.Equal(new (double?, bool, bool)[] { (0, false, false), (0.25, true, false), (0.5, false, true) }, player.Hits);

        // Each was asked for before the song got there, with time to spare for the device.
        player.Hits.Clear();
        hits.StartAt(0.1);
        var asked = new List<(double Now, double At)>();
        for (double now = 0.1; now < 0.8; now += 0.015)
        {
            int before = player.Hits.Count;
            hits.Advance(chart, BranchRoute.Normal, player, now, true);
            asked.AddRange(player.Hits.Skip(before).Select(h => (now, h.At!.Value)));
        }
        Assert.Equal(new[] { 0.25, 0.5 }, asked.Select(a => a.At));
        Assert.All(asked, a => Assert.InRange(a.At - a.Now, HitPlayback.Lookahead - 0.016, HitPlayback.Lookahead));
    }

    [Fact]
    public void BeforeTheSongComesInHitsAreStartedAsTheirTimeComes()
    {
        var chart = Chart();
        var player = new Player { IsPlaying = false };
        var hits = new HitPlayback();
        hits.StartAt(0.15);
        var at = new List<double>();
        for (double now = 0.15; now < 0.35; now += 0.015)
        {
            int before = player.Hits.Count;
            hits.Advance(chart, BranchRoute.Normal, player, now, true);
            if (player.Hits.Count > before) at.Add(now);
        }
        Assert.Equal(new (double?, bool, bool)[] { (null, true, false) }, player.Hits);
        Assert.InRange(Assert.Single(at), 0.25, 0.265);

        // The song comes in: from here hits are asked for ahead, and none is played twice.
        player.IsPlaying = true;
        for (double now = 0.35; now < 0.9; now += 0.015) hits.Advance(chart, BranchRoute.Normal, player, now, true);
        Assert.Equal(new (double?, bool, bool)[] { (null, true, false), (0.5, false, true) }, player.Hits);
        // And when it has run out while the chart goes on, what was asked for ahead is not started again.
        player.IsPlaying = false;
        for (double now = 0.9; now < 1.1; now += 0.015) hits.Advance(chart, BranchRoute.Normal, player, now, true);
        Assert.Equal(2, player.Hits.Count);
    }

    [Fact]
    public void WhatIsPassedOverWithTheSoundsOffOrAfterAJumpIsNotPlayedLate()
    {
        var chart = Chart();
        var player = new Player { IsPlaying = true };
        var hits = new HitPlayback();
        hits.StartAt(-0.2);
        for (double now = -0.2; now < 0.35; now += 0.015) hits.Advance(chart, BranchRoute.Normal, player, now, false);
        Assert.Empty(player.Hits);
        // Switched on again: only what is still to come.
        for (double now = 0.35; now < 0.9; now += 0.015) hits.Advance(chart, BranchRoute.Normal, player, now, true);
        Assert.Equal(new (double?, bool, bool)[] { (0.5, false, true) }, player.Hits);

        // The timer held up for a second and a half: the notes in between are let go.
        player.Hits.Clear();
        hits.StartAt(0.9);
        hits.Advance(chart, BranchRoute.Normal, player, 2.48, true);
        Assert.Empty(player.Hits);
        hits.Advance(chart, BranchRoute.Normal, player, 2.495, true);
        Assert.Empty(player.Hits);
        // A jump back plays from there again.
        hits.StartAt(2.4);
        hits.Advance(chart, BranchRoute.Normal, player, 2.45, true);
        Assert.Equal(new (double?, bool, bool)[] { (2.5, false, false) }, player.Hits);
    }
}
