using OurTaiko;
using OurTaikoEdit.App.ViewModels;
using OurTaikoEdit.Audio;
using OurTaikoEdit.Tja;

namespace OurTaikoEdit.UiTests;

// The play scene's play: the game's judge on the song's clock, the drum, auto play's sounds.
public class PlayTests
{
    sealed class Player : IAudioPlayer
    {
        public readonly List<(double? At, bool Ka)> Hits = new();
        public double Duration => 600;
        public bool IsPlaying { get; set; }
        public double Position { get; set; }
        public float Volume { get; set; }
        public float HitVolume { get; set; }
        public double OutputLatency => 0;
        public void Play() => IsPlaying = true;
        public void Pause() => IsPlaying = false;
        public void Seek(double seconds) => Position = seconds;
        public void PlayHit(bool ka, bool big) => Hits.Add((null, ka));
        public void PlayHitAt(double songTime, bool ka, bool big) => Hits.Add((songTime, ka));
        public void CancelHits() { }
        public void SetHitSounds(PcmAudio don, PcmAudio ka) { }
        public void Dispose() { }
    }

    // 120 BPM, measures of 2 s from 0: a don at 0, a ka at 0.25, a big don at 0.5, a drumroll from
    // 1 to 2, then a don at 2.5.
    static TaikoChart Chart() => new TjaLanguage().Analyze("BPM:120\nOFFSET:0\nCOURSE:Oni\nLEVEL:8\n#START\n1020300050000000,\n8000100000000000,\n#END\n", 0).Chart!;

    // A play on a clock the test moves; the song follows it once it has started.
    sealed class Rig
    {
        public double Now;
        public readonly Player Player = new();
        public readonly PlayViewModel Play;
        public bool WentBack;

        public Rig(double? from = null, TaikoChart? chart = null)
        {
            var (play, error) = PlayViewModel.Create(chart ?? Chart(), "Test", "", from, BranchRoute.Normal, Player, 0, true, () => WentBack = true, () => Now);
            Assert.Null(error);
            Play = play!;
        }

        // The clock to a time of the chart, a frame every 10 ms.
        public void RunTo(double time)
        {
            while (Play.Time < time - 1e-9 && !Play.Finished)
            {
                Now += 0.01;
                if (Player.IsPlaying) Player.Position += 0.01;
                Play.Tick();
            }
        }
    }

    [Fact]
    public void ThePlayStartsBeforeTheFirstNoteAndTheSongComesInAtItsStart()
    {
        var rig = new Rig();
        Assert.Equal(-PlayViewModel.LeadIn, rig.Play.Time, 6);
        Assert.False(rig.Player.IsPlaying);
        rig.RunTo(-0.5);
        Assert.False(rig.Player.IsPlaying);
        rig.RunTo(0.05);
        Assert.True(rig.Player.IsPlaying);
        Assert.Equal(rig.Play.Time, rig.Player.Position, 2);
    }

    [Fact]
    public void PressesAreJudgedAsTheGameJudgesThem()
    {
        var rig = new Rig();
        // On the don: 良. The wrong colour for the ka: nothing; the right one 50 ms late: 可.
        rig.RunTo(0);
        rig.Play.Hit(ka: false, right: false);
        Assert.Equal((1, 0, 0, 1), (rig.Play.Session.Good, rig.Play.Session.Ok, rig.Play.Session.Bad, rig.Play.Session.Combo));
        Assert.Equal(Judgment.Good, rig.Play.Judged);
        rig.RunTo(0.3);
        rig.Play.Hit(ka: true, right: true);
        Assert.Equal((1, 1, 0, 2), (rig.Play.Session.Good, rig.Play.Session.Ok, rig.Play.Session.Bad, rig.Play.Session.Combo));
        Assert.Equal(Judgment.Ok, rig.Play.Judged);
        // The drum sounded and flashed for both presses: don left, ka right.
        Assert.Equal(new (double?, bool)[] { (null, false), (null, true) }, rig.Player.Hits);
        Assert.True(rig.Play.FlashedAt[0] > -1 && rig.Play.FlashedAt[3] > -1);
        Assert.Equal(2, rig.Play.Flying.Count);
        // The big don nobody hits is a 不可 once its window has passed, and says nothing.
        rig.RunTo(0.7);
        Assert.Equal((1, 0), (rig.Play.Session.Bad, rig.Play.Session.Combo));
        Assert.Equal(Judgment.Ok, rig.Play.Judged);
        // Three hits on the drumroll.
        rig.RunTo(1.2);
        for (int i = 0; i < 3; i++) rig.Play.Hit(ka: i == 1, right: false);
        Assert.Equal(3, rig.Play.Session.Rolls);
        Assert.Equal(3, rig.Play.RollHits);
        Assert.True(rig.Play.Session.Score > 0);
    }

    [Fact]
    public void AutoPlayHitsEveryNoteAndItsSoundsAreAskedForAheadOnTheNotesOwnTimes()
    {
        var rig = new Rig();
        rig.Play.Auto = true;
        rig.RunTo(0.9);
        Assert.Equal((3, 0, 0), (rig.Play.Session.Good, rig.Play.Session.Ok, rig.Play.Session.Bad));
        // Each sound was asked for ahead, for its note's own time of the song.
        Assert.Equal(new (double?, bool)[] { (0, false), (0.25, true), (0.5, false) }, rig.Player.Hits);
        // A press does nothing while the chart plays itself.
        rig.Play.Hit(ka: false, right: false);
        Assert.Equal(3, rig.Player.Hits.Count);
        // The drumroll is hit 15 times a second from its first moment, each hit sounded once.
        rig.RunTo(2.2);
        Assert.Equal(16, rig.Play.Session.Rolls);
        Assert.Equal(16, rig.Player.Hits.Count - 3);
        // The play ends two seconds after the chart does, the song stopped.
        rig.RunTo(100);
        Assert.True(rig.Play.Finished);
        Assert.False(rig.Player.IsPlaying);
        Assert.Equal(4, rig.Play.Good);
        Assert.Equal(4, rig.Play.MaxCombo);
    }

    [Fact]
    public void APlayFromAPlaceOfTheChartPassesOverWhatIsBeforeIt()
    {
        // From the drumroll's measure: the three notes before it are neither hit nor missed.
        var rig = new Rig(from: 0.9);
        Assert.Equal(0.9 - PlayViewModel.LeadIn, rig.Play.Time, 6);
        Assert.True(rig.Play.HidesSkipped);
        rig.RunTo(0.8);
        Assert.Equal((0, 0, 0), (rig.Play.Session.Good, rig.Play.Session.Ok, rig.Play.Session.Bad));
        Assert.True(rig.Play.Session.Skipped[0] && rig.Play.Session.Skipped[2] && !rig.Play.Session.Skipped[3]);
        rig.RunTo(2.5);
        rig.Play.Hit(ka: false, right: true);
        Assert.Equal((1, 0), (rig.Play.Session.Good, rig.Play.Session.Bad));
    }

    [Fact]
    public void PauseHoldsTheClockAndTheSongAndRestartBeginsAgain()
    {
        var rig = new Rig();
        rig.RunTo(0.4);
        rig.Play.TogglePause();
        Assert.True(rig.Play.Paused);
        Assert.False(rig.Player.IsPlaying);
        double held = rig.Play.Time;
        rig.Now += 5;
        rig.Play.Tick();
        rig.Play.Hit(ka: false, right: false);
        Assert.Equal(held, rig.Play.Time);
        Assert.Empty(rig.Player.Hits);
        rig.Play.TogglePause();
        Assert.True(rig.Player.IsPlaying);
        Assert.Equal(held, rig.Player.Position, 6);
        rig.RunTo(0.7);
        Assert.Equal(3, rig.Play.Session.Bad);

        rig.Play.Restart();
        Assert.Equal(-PlayViewModel.LeadIn, rig.Play.Time, 6);
        Assert.Equal(0, rig.Play.Session.Bad);
        Assert.False(rig.Player.IsPlaying);

        rig.Play.Back();
        Assert.True(rig.WentBack);
    }

    [Fact]
    public void ABranchWithoutAllItsRoutesIsPlayedOnTheEditorsRoute()
    {
        var chart = new TjaLanguage().Analyze("BPM:120\nOFFSET:0\nCOURSE:Oni\nLEVEL:8\n#START\n1000,\n#BRANCHSTART p,50,80\n#N\n1111,\n#E\n2222,\n#BRANCHEND\n1,\n#END\n", 0).Chart!;
        Assert.False(chart.Branches[0].HasAllRoutes);
        var (play, error) = PlayViewModel.Create(chart, "Test", "", null, BranchRoute.Expert, null, 0, true, () => { });
        Assert.Null(error);
        Assert.Equal(BranchRoute.Expert, play!.Session.ForcedBranch);

        // A course the game's gauge does not know cannot be played, and says why.
        var tower = new TjaLanguage().Analyze("BPM:120\nCOURSE:Tower\n#START\n1,\n#END\n", 0).Chart!;
        var (none, why) = PlayViewModel.Create(tower, "Test", "", null, BranchRoute.Normal, null, 0, true, () => { });
        Assert.Null(none);
        Assert.NotNull(why);
    }
}
